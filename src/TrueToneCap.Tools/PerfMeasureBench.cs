// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// PerfMeasureBench.cs — 管线补测（2026-10-30 审查 §6 待量清单）
//   dotnet run --project src/TrueToneCap.Tools -- --perf-ocr <modelDir> [--w 1200 --h 800 --iterations 5]
//   dotnet run --project src/TrueToneCap.Tools -- --perf-png [--w 2250 --h 1350]
//
// OCR 臂：端到端 RecognizeAsync 计时；再用反射调 private RunDetection / RunRecognition
//         把同一张图拆成 det / rec / 其余（后处理与张量拼装）。
// PNG 臂：反射调 private BuildRawScanlines16 / CompressDeflate，把一次 16-bit PNG 保存
//         拆成 转换+滤波 / deflate / 其余（chunk 组装与写盘），并对照三档 effort 的总耗时。
// 反射只读、不改产品代码；私有成员签名变了这里会立刻抛 MissingMethodException。

using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Services;

/// <summary>补测用的两个基准：OCR 分段与 PNG 保存拆分。</summary>
public static class PerfMeasureBench
{
    // ═══════════════════════════════════════
    //  OCR：端到端 + det/rec 分段
    // ═══════════════════════════════════════

    public static void RunOcr(string[] args)
    {
        string modelDir = args.Length >= 2 && !args[1].StartsWith("--")
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        int w = 1200, h = 800, iterations = 5;
        string providerFilter = "both";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--w" when i + 1 < args.Length: int.TryParse(args[i + 1], out w); break;
                case "--h" when i + 1 < args.Length: int.TryParse(args[i + 1], out h); break;
                case "--iterations" when i + 1 < args.Length: int.TryParse(args[i + 1], out iterations); break;
                case "--provider" when i + 1 < args.Length: providerFilter = args[i + 1]; break;
            }
        }

        Console.WriteLine($"═══ OCR 端到端 + det/rec 分段 ═══");
        Console.WriteLine($"modelDir={modelDir}");
        Console.WriteLine($"size={w}x{h} iterations={iterations}");
        Console.WriteLine($"models: det={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx"))} " +
                          $"rec={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx"))} " +
                          $"dict={File.Exists(Path.Combine(modelDir, "ppocrv6_dict.txt"))}");

        var bgra = RenderUiImage(w, h);
        var detMi = typeof(OnnxOcrEngine).GetMethod("RunDetection", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("OnnxOcrEngine.RunDetection");
        var recMi = typeof(OnnxOcrEngine).GetMethod("RunRecognition", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("OnnxOcrEngine.RunRecognition");

        var providers = providerFilter switch
        {
            "dml" or "gpu" => new[] { OnnxExecutionProvider.DirectML },
            "cpu" => new[] { OnnxExecutionProvider.Cpu },
            _ => new[] { OnnxExecutionProvider.DirectML, OnnxExecutionProvider.Cpu },
        };
        foreach (var provider in providers)
        {
            OnnxOcrEngine engine;
            try { engine = new OnnxOcrEngine(provider, modelDir); }
            catch (Exception ex) { Console.WriteLine($"⚠ {provider} 初始化失败: {ex.Message}"); continue; }
            if (!engine.Info.IsAvailable) { Console.WriteLine($"⚠ {provider} 不可用"); engine.Dispose(); continue; }

            Console.WriteLine($"\n── {provider} ──");
            var warm = Stopwatch.StartNew();
            engine.RecognizeAsync(bgra, w, h, "zh-en").GetAwaiter().GetResult();
            warm.Stop();
            Console.WriteLine($"warmup={warm.ElapsedMilliseconds}ms");

            var totals = new List<long>();
            var dets = new List<long>();
            var recs = new List<long>();
            var posts = new List<long>();
            int boxCount = 0;
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = engine.RecognizeAsync(bgra, w, h, "zh-en").GetAwaiter().GetResult();
                sw.Stop();
                long total = sw.ElapsedMilliseconds;

                var swDet = Stopwatch.StartNew();
                var boxesObj = detMi.Invoke(engine, new object[] { bgra, w, h })!;
                swDet.Stop();
                var boxes = ((System.Collections.IEnumerable)boxesObj).Cast<object>().ToList();
                long recMs = 0;
                foreach (var box in boxes)
                {
                    var swRec = Stopwatch.StartNew();
                    recMi.Invoke(engine, new object[] { bgra, w, h, box });
                    swRec.Stop();
                    recMs += swRec.ElapsedMilliseconds;
                }

                boxCount = boxes.Count;
                totals.Add(total);
                dets.Add(swDet.ElapsedMilliseconds);
                recs.Add(recMs);
                posts.Add(Math.Max(0, total - swDet.ElapsedMilliseconds - recMs));
                string text = result?.Text ?? "";
                if (i == 0) Console.WriteLine($"  text: {text.Replace("\n", " | ")}");
                Console.WriteLine($"  run{i + 1}: total={total}ms det={swDet.ElapsedMilliseconds}ms rec={recMs}ms " +
                                  $"rest={posts[^1]}ms boxes={boxes.Count} chars={text.Length} hash={TextHash(text)} " +
                                  $"err={result?.Error ?? "null"}");
            }

            Console.WriteLine($"  MEDIAN {provider}: total={Median(totals)}ms det={Median(dets)}ms rec={Median(recs)}ms " +
                              $"rest={Median(posts)}ms boxes={boxCount}");
            var joined = string.Join(",", totals.Select(t => $"{t}ms"));
            Console.WriteLine($"  SAMPLES {provider}: total=[{joined}] det=[{string.Join(",", dets.Select(t => $"{t}ms"))}] " +
                              $"rec=[{string.Join(",", recs.Select(t => $"{t}ms"))}]");
            engine.Dispose();
        }
    }

    // ═══════════════════════════════════════
    //  OCR 路由：MultiOcrService 的懒建与合批（不经过 UI）
    // ═══════════════════════════════════════

    public static void RunOcrRoute(string[] args)
    {
        string modelDir = args.Length >= 2 && !args[1].StartsWith("--")
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string route = "dml";
        for (int i = 1; i < args.Length; i++)
            if (args[i] == "--provider" && i + 1 < args.Length) route = args[i + 1];
        var sw = Stopwatch.StartNew();
        MultiOcrService.Initialize(modelDir);
        Console.WriteLine($"Initialize: {sw.ElapsedMilliseconds}ms 引擎={MultiOcrService.Engines.Count} " +
                          $"[{string.Join(", ", MultiOcrService.Engines.Select(e => e.Info.EngineType))}]");
        Console.WriteLine($"默认选中: {MultiOcrService.SelectedEngineType}");

        // 显式切到 CPU ⇒ 必须在这里按需构建（而不是启动期就常驻）
        var swCpu = Stopwatch.StartNew();
        if (route is "cpu")
        {
            MultiOcrService.SelectedEngineType = OcrEngineType.OnnxCpu;
            var cpu = MultiOcrService.SelectedEngine;
            swCpu.Stop();
            Console.WriteLine($"切到 CPU: {swCpu.ElapsedMilliseconds}ms built={cpu is not null} " +
                              $"engines={MultiOcrService.Engines.Count} [{string.Join(", ", MultiOcrService.Engines.Select(e => e.Info.EngineType))}]");
            if (cpu is null) return;
        }
        else
        {
            Console.WriteLine($"使用默认引擎: {MultiOcrService.SelectedEngineType}（不触发 CPU 懒建）");
        }

        var bgra = RenderUiImage(1200, 800);
        var warm = Stopwatch.StartNew();
        var warmRes = MultiOcrService.RecognizeAsync(bgra, 1200, 800, "zh-en").GetAwaiter().GetResult();
        Console.WriteLine($"CPU warmup: {warm.ElapsedMilliseconds}ms chars={warmRes.Text.Length} err={warmRes.Error ?? "null"}");

        var times = new List<long>();
        string lastText = "";
        for (int i = 0; i < 3; i++)
        {
            var t = Stopwatch.StartNew();
            var r = MultiOcrService.RecognizeAsync(bgra, 1200, 800, "zh-en").GetAwaiter().GetResult();
            t.Stop();
            times.Add(t.ElapsedMilliseconds);
            lastText = r.Text;
            Console.WriteLine($"  run{i + 1}: {t.ElapsedMilliseconds}ms chars={r.Text.Length} hash={TextHash(r.Text)} err={r.Error ?? "null"}");
        }
        Console.WriteLine($"MEDIAN route({route}): {Median(times)}ms chars={lastText.Length} hash={TextHash(lastText)}");
    }

    // ═══════════════════════════════════════
    //  AVIF 真 10-bit NVENC：压缩强度是否真的下发
    // ═══════════════════════════════════════

    public static void RunAvif10(string[] args)
    {
        int w = 1280, h = 720;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--w" when i + 1 < args.Length: int.TryParse(args[i + 1], out w); break;
                case "--h" when i + 1 < args.Length: int.TryParse(args[i + 1], out h); break;
            }
        }

        var mi = typeof(NvencAvifBackend).GetMethod("TryEncodeNvencHdrAvif",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException("NvencAvifBackend.TryEncodeNvencHdrAvif");

        // A2R10G10B10：B 低 10 位 / G 次 / R 再次 / A 最高 2 位；用确定性梯度制造真实熵
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint b = (uint)((x * 1023) / Math.Max(1, w - 1));
                uint g = (uint)((y * 1023) / Math.Max(1, h - 1));
                uint r = (uint)(((x * 7 + y * 13) & 1023));
                uint a = 3;
                uint v = (a << 30) | (r << 20) | (g << 10) | b;
                int o = (y * w + x) * 4;
                px[o] = (byte)v; px[o + 1] = (byte)(v >> 8);
                px[o + 2] = (byte)(v >> 16); px[o + 3] = (byte)(v >> 24);
            }

        Console.WriteLine($"═══ AVIF 真 10-bit NVENC 压缩强度对撞 ═══ size={w}x{h}");
        foreach (var effort in new[] { CompressionEffort.Smallest, CompressionEffort.Balanced, CompressionEffort.Fastest })
        {
            var times = new List<long>();
            long size = 0;
            for (int i = 0; i < 2; i++)
            {
                string path = Path.Combine(Path.GetTempPath(), $"ttc_avif10_{effort}_{Guid.NewGuid():N}.avif");
                try
                {
                    var sw = Stopwatch.StartNew();
                    var ok = (bool)mi.Invoke(null, new object[] { px, w, h, 30, path, CancellationToken.None, "BT2020", 100f, effort })!;
                    sw.Stop();
                    if (!ok) { Console.WriteLine($"{effort}: NVENC 拒绝（ok=false）"); break; }
                    times.Add(sw.ElapsedMilliseconds);
                    size = new FileInfo(path).Length;
                }
                finally { try { File.Delete(path); } catch { } }
            }
            if (times.Count > 0)
                Console.WriteLine($"{effort}: median={Median(times)}ms samples=[{string.Join(",", times)}] bytes={size}");
        }
    }

    // ═══════════════════════════════════════
    //  PNG：转换+滤波 / deflate / 其余
    // ═══════════════════════════════════════

    public static void RunPng(string[] args)
    {
        int w = 2250, h = 1350;
        string? dumpRawDir = null;
        string? rawFile = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--w" when i + 1 < args.Length: int.TryParse(args[i + 1], out w); break;
                case "--h" when i + 1 < args.Length: int.TryParse(args[i + 1], out h); break;
                case "--dump-raw" when i + 1 < args.Length: dumpRawDir = args[i + 1]; break;
                // 直接吃一份已滤波的 16-bit raw（例如从真实 HDR PNG 的 IDAT 解出来的扫描线）
                case "--raw" when i + 1 < args.Length: rawFile = args[i + 1]; break;
            }
        }

        var rawMi = typeof(ManagedPngEncoder).GetMethod("BuildRawScanlines16", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ManagedPngEncoder.BuildRawScanlines16");
        // ═══ 2026-10-31：ManagedPngEncoder.CompressDeflate 已被流式入口替掉（IDAT 现在直写 sink，
        // 全帧压缩数组不再存在）。这一段量的必须是【产品此刻真正走的那条腿】，所以照产品的形状
        // 分两步反射调 DeflateZlib 的内部入口：整帧判一次后端（SelectNativeBackend）→
        // 增量压给一条流（CompressTo）。反射只读、不改产品代码；签名变了这里立刻抛
        // MissingMethodException —— 与原口径同一个 fail-closed 形状，不许静默量到别的东西上。
        var dzType = typeof(DeflateZlib);
        var selectMi = dzType.GetMethod("SelectNativeBackend",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(byte[]), typeof(int) }, null)
            ?? throw new MissingMethodException("DeflateZlib.SelectNativeBackend(byte[], int)");
        var compressToMi = dzType.GetMethod("CompressTo", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("DeflateZlib.CompressTo(Stream, byte[], int, bool)");

        // deflate 段的形状 = 产品的形状：不留住一整份压缩结果，只报产出的字节数
        long DeflateSegment(byte[] data, CompressionEffort eff)
        {
            int level = CompressionEffortMap.PngTiffDeflateLevel(eff);
            bool useNative = (bool)selectMi.Invoke(null, new object[] { data, level })!;
            using var sink = new MemoryStream();
            compressToMi.Invoke(sink, new object[] { data, level, useNative });
            return sink.Length;
        }

        Console.WriteLine($"═══ PNG 16-bit 保存拆分 ═══");
        Console.WriteLine($"size={w}x{h} (10-bit 量化, 左对齐)");

        // --raw：只测 deflate 段（真实样张的 raw 已在文件里），走的是产品同一个 CompressDeflate
        if (rawFile is not null)
        {
            var raw = File.ReadAllBytes(rawFile);
            Console.WriteLine($"raw={rawFile} bytes={raw.Length}");
            Console.WriteLine("arm,effort,raw_build_ms,deflate_ms,encode_total_ms,bytes,raw_mb,deflate_share");
            foreach (var effort in new[] { CompressionEffort.Smallest, CompressionEffort.Quick, CompressionEffort.Fastest })
            {
                int reps = effort == CompressionEffort.Smallest ? 2 : 3;
                var times = new List<long>();
                long bytes = 0;
                for (int i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    bytes = DeflateSegment(raw, effort);
                    sw.Stop();
                    times.Add(sw.ElapsedMilliseconds);
                }
                long ms = Median(times);
                Console.WriteLine($"real,{effort},0,{ms},{ms},{bytes},{raw.Length / 1048576.0:F1},1.00");
            }
            return;
        }

        var arms = new (string Name, byte[] Px)[]
        {
            ("ui10", RenderUi10(w, h)),
            ("noise10", BuildNoise10(w, h)),
        };

        Console.WriteLine("arm,effort,raw_build_ms,deflate_ms,encode_total_ms,bytes,raw_mb,deflate_share");
        foreach (var (name, px) in arms)
        {
            // 预热（并行滤波与 zlib 内部都可能有一次性开销）
            EncodeToTemp(px, w, h, CompressionEffort.Fastest);

            var raw = (byte[])rawMi.Invoke(null, new object[] { px, w, h, 10 })!;
            double rawMb = raw.Length / 1048576.0;

            if (dumpRawDir is not null)
            {
                Directory.CreateDirectory(dumpRawDir);
                File.WriteAllBytes(Path.Combine(dumpRawDir, name + ".raw"), raw);
                File.WriteAllText(Path.Combine(dumpRawDir, name + ".txt"),
                    $"w={w}\nh={h}\nstride={1 + w * 8}\nrows={h}\nbytes={raw.Length}\n");
                Console.WriteLine($"raw dumped: {Path.Combine(dumpRawDir, name + ".raw")}");
            }

            // 转换+滤波：3 次取中位
            var rawTimes = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                rawMi.Invoke(null, new object[] { px, w, h, 10 });
                sw.Stop();
                rawTimes.Add(sw.ElapsedMilliseconds);
            }
            long rawMs = Median(rawTimes);

            foreach (var effort in new[] { CompressionEffort.Smallest, CompressionEffort.Quick, CompressionEffort.Fastest })
            {
                // level 9 在噪声上可到数秒 ⇒ 只跑 2 次；其它档跑 3 次
                int reps = effort == CompressionEffort.Smallest ? 2 : 3;
                var defTimes = new List<long>();
                for (int i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    DeflateSegment(raw, effort);
                    sw.Stop();
                    defTimes.Add(sw.ElapsedMilliseconds);
                }
                long defMs = Median(defTimes);

                var encTimes = new List<long>();
                long bytes = 0;
                for (int i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    long len = EncodeToTemp(px, w, h, effort);
                    sw.Stop();
                    encTimes.Add(sw.ElapsedMilliseconds);
                    bytes = len;
                }
                long encMs = Median(encTimes);

                double share = encMs > 0 ? (double)defMs / encMs : 0;
                Console.WriteLine($"{name},{effort},{rawMs},{defMs},{encMs},{bytes},{rawMb:F1},{share:P0}");
            }
        }
    }

    static long EncodeToTemp(byte[] px, int w, int h, CompressionEffort effort)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ttc_perf_{Guid.NewGuid():N}.png");
        try
        {
            ManagedPngEncoder.Encode16(px, w, h, path, cicp: null, iccProfile: null, bitDepth: 10,
                fastest: false, effort: effort);
            return new FileInfo(path).Length;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    static string TextHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..8];
    }

    static long Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    // ═══════════════════════════════════════
    //  测试图生成
    // ═══════════════════════════════════════

    static byte[] RenderUiImage(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                       Color.FromArgb(246, 247, 249), Color.FromArgb(228, 232, 238), 35f))
                g.FillRectangle(bg, 0, 0, w, h);

            using var titleFont = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
            using var bodyFont = new Font("Segoe UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
            using var dark = new SolidBrush(Color.FromArgb(32, 34, 38));
            using var accent = new SolidBrush(Color.FromArgb(30, 110, 210));

            g.DrawString("TrueToneCap 性能测量样本 / Performance sample", titleFont, dark, 60, 50);
            g.DrawString("保存到文件 复制到剪贴板 取消 应用 明天下午3点开会记得带上笔记本", bodyFont, accent, 60, 110);

            string[] lines =
            {
                "设置 → 输出格式 → PNG（16-bit 容器，10-bit 有效）",
                "压缩强度：极高（level 9）/ 均衡 / 快速",
                "预览窗口：选区 2250x1350，帧率 60 Hz，HDR 开启",
                "The quick brown fox jumps over the lazy dog 0123456789",
                "日志详细度：简 / 标准 / 诊断；日志目录在 exe 同级 log/",
                "OCR 引擎：PP-OCRv6 medium (DirectML, FP16)",
                "翻译：文本路逐行请求，并发上限 4；视觉路整图一次",
                "选区回读：只读矩形；共享槽冻结；池闲置 15s 回收",
                "内存：私有字节 / 工作集 / 托管堆 / LOH",
                "NVENC preset P1..P7；avifenc -s 0..10；cjxl -e 1..10",
                "This line exists to add horizontal entropy to the sample.",
                "最后一行：测量结束，产物用完即删。",
            };
            float y = 170;
            foreach (var line in lines)
            {
                g.DrawString(line, bodyFont, dark, 64, y);
                y += 42;
            }

            for (int i = 0; i < 6; i++)
            {
                var rect = new Rectangle(70 + i * 180, h - 140, 150, 46);
                using var b = new SolidBrush(Color.FromArgb(30 + i * 20, 110 + i * 10, 210 - i * 15));
                g.FillRectangle(b, rect);
            }
        }
        return BitmapToBgra(bmp);
    }

    static byte[] RenderUi10(int w, int h)
    {
        var bgra8 = RenderUiImage(w, h);
        return ExpandTo10(bgra8);
    }

    static byte[] BuildNoise10(int w, int h)
    {
        var rng = new Random(20261030);
        var px = new byte[w * h * 8];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 8;
            Write10(px, o, rng.Next(0, 1024));
            Write10(px, o + 2, rng.Next(0, 1024));
            Write10(px, o + 4, rng.Next(0, 1024));
            Write10(px, o + 6, 1023);
        }
        return px;
    }

    /// <summary>8-bit BGRA → 10-bit 左对齐 BGRA16（v*257 后就近取 64 的整数倍）。</summary>
    static byte[] ExpandTo10(byte[] bgra8)
    {
        var px = new byte[bgra8.Length * 2];
        for (int i = 0; i < bgra8.Length; i += 4)
        {
            int o = i * 2;
            Write10(px, o, Quant(bgra8[i]));
            Write10(px, o + 2, Quant(bgra8[i + 1]));
            Write10(px, o + 4, Quant(bgra8[i + 2]));
            Write10(px, o + 6, 1023);
        }
        return px;
    }

    static int Quant(byte v)
    {
        int v16 = v * 257;
        int q = (v16 + 32) / 64;
        return Math.Min(q, 1023);
    }

    static void Write10(byte[] px, int off, int value10)
    {
        int v16 = value10 << 6;
        px[off] = (byte)(v16 >> 8);
        px[off + 1] = (byte)v16;
    }

    static byte[] BitmapToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[rect.Height * Math.Abs(data.Stride)];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            // ARGB → BGRA（与 OcrPerfBench 同约定）
            for (int i = 0; i < bytes.Length; i += 4)
            {
                (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            }
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }
}
