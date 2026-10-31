// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/OcrRecWidthBench.cs
// OCR rec「固定 max side vs 批内真实最大宽」的可复跑仪表（全仓性能审查 2026-10-31 §未量项 ①）。
//
//   dotnet run --project src/TrueToneCap.Tools -- --bench-ocr-rec [--out DIR] [--rows 40] [--reps 5]
//                                                 [--step 32] [--provider dml|cpu] [--models DIR] [--keep]
//
// 约定（口径同 SavePathBench.cs）：结论走 Environment.ExitCode + 落盘 CSV；
//   0=通过 / 1=回归或仪表不自证 / 3=前置条件缺失(SKIP)。枚举到 0 个格子一律红。
//
// 两臂只差在【识别张量的宽度维】：
//   arm-fixed  = 产品现状 OcrModelConfig.Default.RecImgMaxW = 1280，一批 4 框恒按 1280 送
//   arm-narrow = 同一批的【批内真实最大 recW】向上取整到 --step，其余逐字不变
// 裁剪/缩放/归一化/CTC 解码/合批形状一律走产品自己的
//   OnnxOcrEngine.RunRecognitionBatch —— 反射只读，签名变了立刻抛 MissingMethodException。
//   本文件不复现任何一步算术；唯一自己算的是"这一批的批内最大 recW 应该是多少"，
//   那是收窄策略的输入，不是被测的推理路。
//
// ⚠ 判据的主次：文本相等 ＞ 速度。两臂识别文本只要有一批不同，这台仪表就【退 1 并大声报】，
//   因为它就不再是性能项而是正确性项。
// ⚠ 输入是本文件合成的确定性文本条（渲染真值框，不是 det 输出）⇒ 读数只对这一种宽度分布负责，
//   是指示性的，不是发货判据；换分布（--rows / 真实语料 --ocr-bench-real）比值就会动。
// ⚠ 正控两问：① 有一批框宽本就顶到 1280 ⇒ 两臂同形，此时 ms 差必须落在噪声内、文本必须相同
//   （两臂在同形处不收敛 = 仪表有别的变量在动）；② 判别批必须真的拉开 ms，否则
//   non-discriminating:timing 退 1。

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using TrueToneCap.Core.Services;

/// <summary>rec 合批张量宽度维 A/B：固定 max side vs 批内真实最大宽。</summary>
public static class OcrRecWidthBench
{
    private const int DefaultRows = 40;
    private const int DefaultReps = 5;
    private const int DefaultStep = 32;

    // 合成语料：短菜单项 + 长句混排，行宽自然跨 40…1400px（这就是"批内宽度分布"）
    private static readonly string[] Lines =
    [
        "文件 编辑 视图 帮助", "保存到文件夹", "复制到剪贴板", "OCR 识字并翻译",
        "the quick brown fox jumps over the lazy dog", "设置 与 隐私",
        "今天下午三点开会记得带上笔记本电脑和充电器", "retry", "确定",
        "capture region and translate every detected text line here",
        "好的，我马上发给你", "预览 复制 保存 取消 应用",
        "WinUI 3 screenshot tool with PP-OCRv6 on DirectML and CPU fallback",
        "网络不可达", "应用需要管理员权限才能修改系统设置吗", "hello world 测试",
    ];

    public static int Run(string[] args)
    {
        string outDir = ResolveOut(args);
        int rows = Math.Max(4, GetIntOpt(args, "--rows", DefaultRows));
        int reps = Math.Max(3, GetIntOpt(args, "--reps", DefaultReps));
        int step = Math.Max(1, GetIntOpt(args, "--step", DefaultStep));
        bool wantCpu = string.Equals(GetOpt(args, "--provider"), "cpu", StringComparison.OrdinalIgnoreCase);
        bool keep = Array.IndexOf(args, "--keep") >= 0;

        Console.WriteLine($"═══ [bench-ocr-rec] rec 张量宽度维：固定 max side vs 批内真实最大宽 ═══ out={outDir}");
        Console.WriteLine($"rows={rows} reps={reps} step={step} provider={(wantCpu ? "cpu" : "dml→cpu 自动降级")}");

        // ── 反射：产品的合批识别入口 + Box + 批大小（签名变了立刻抛，不静默量旧账）──
        var engineType = typeof(OnnxOcrEngine);
        var boxType = engineType.GetNestedType("Box", BindingFlags.NonPublic)
            ?? throw new MissingMethodException("OnnxOcrEngine+Box（私有嵌套 record struct）不见了");
        // 名字唯一 ⇒ 按名取；参数形状在下面 Invoke 时由运行时校验（对不上就抛 TargetParameterCountException）
        var runBatchMi = engineType.GetMethod("RunRecognitionBatch",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(
                "OnnxOcrEngine.RunRecognitionBatch(byte[],int,int,List<Box>,int,int) 不见了（仪表按此反射）");
        var batchField = engineType.GetField("RecBatchSize", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("OnnxOcrEngine.RecBatchSize 常量不见了（合批形状改了口径）");
        int recBatch = (int)batchField.GetValue(null)!;

        var defCfg = OcrModelConfig.Default;
        int recH = defCfg.RecImgH;
        int fixedMaxW = defCfg.RecImgMaxW;
        Console.WriteLine($"  产品配置：RecImgH={recH} RecImgMaxW={fixedMaxW} RecBatchSize={recBatch}");

        string modelDir = GetOpt(args, "--models");
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            try { modelDir = OnnxOcrEngine.ResolveModelDir(); }
            catch (Exception ex)
            {
                Console.WriteLine($"⏭ SKIP —— 产品自己的 ResolveModelDir() 就抛了 {ex.GetType().Name}: "
                    + $"{ex.Message}（本机没有随包模型 ⇒ 两臂都没有前提，退出码 3）");
                return 3;
            }
        }
        string detPath = Path.Combine(modelDir, defCfg.DetFileName);
        string recPath = Path.Combine(modelDir, defCfg.RecFileName);
        string dictPath = Path.Combine(modelDir, defCfg.DictFileName);
        if (!File.Exists(recPath) || !File.Exists(detPath) || !File.Exists(dictPath))
        {
            Console.WriteLine($"⏭ SKIP —— 模型不齐（{modelDir}）：det={File.Exists(detPath)} rec={File.Exists(recPath)} "
                + $"dict={File.Exists(dictPath)}；没有模型就没有任何一臂可跑（退出码 3，不打印推算数）");
            return 3;
        }

        // ── 合成页面 + 渲染真值框（确定性：固定字号/坐标，无随机）──
        var (bgra, imgW, imgH, boxes) = BuildSyntheticPage(rows);
        var natural = new int[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
            natural[i] = NaturalRecW(boxes[i], imgW, imgH, recH, fixedMaxW);

        // ── 引擎按 (max side) 各一台：arm-fixed 一台，arm-narrow 每个量化宽度一台 ──
        var provider = wantCpu ? OnnxExecutionProvider.Cpu : OnnxExecutionProvider.DirectML;
        var engines = new Dictionary<int, OnnxOcrEngine>();
        OnnxOcrEngine GetEngine(int maxW)
        {
            if (engines.TryGetValue(maxW, out var e)) return e;
            var cfg = defCfg with { RecImgMaxW = maxW };
            var created = new OnnxOcrEngine(provider, modelDir, cfg);
            if (!created.Info.IsAvailable)
            {
                created.Dispose();
                throw new InvalidOperationException($"引擎不可用（maxW={maxW}, dir={modelDir}）");
            }
            engines[maxW] = created;
            // 臂身份必须印出来：这台引擎自降成 CPU 时，"收窄省的是 GPU 时间"这句就不成立了。
            // 2026-10-31 第一次跑这条腿时 Tools 目录缺 DirectML.dll/onnxruntime.dll ⇒ 两臂都在 CPU 上，
            // 日志里却看不出来 ⇒ 只印了请求的 provider，没印实际落点。此后每个新建引擎留一行。
            Console.WriteLine($"    [身份] maxW={maxW} 请求 provider={provider} ⇒ 实得 "
                + $"{created.Info.EngineType} / {created.Info.Name}");
            return created;
        }

        int cells = 0, discBatches = 0, convBatches = 0, textDiffBatches = 0;
        double convMaxRel = 0.0;
        double minDiscRatio = double.MaxValue;
        long fixedTotalMs = 0, narrowTotalMs = 0;
        var rowsCsv = new List<string>
        {
            "arm,batch,boxes,fixed_w,narrow_w,rec_ms_median,text_equal,detail",
        };
        var diffs = new List<string>();

        Console.WriteLine($"  批内真实最大宽（向上取整到 {step}）= "
            + string.Join(", ", Enumerable.Range(0, (boxes.Count + recBatch - 1) / recBatch)
                .Select(b => MaxNatural(natural, b, recBatch))));

        for (int start = 0; start + recBatch <= boxes.Count; start += recBatch)
        {
            int narrowW = RoundUp(Math.Max(4, MaxNatural(natural, start, recBatch)), step);
            if (narrowW > fixedMaxW) narrowW = fixedMaxW;
            bool converging = narrowW == fixedMaxW;

            var list = MakeBoxList(boxType, boxes, start, recBatch);
            var (msFixed, textFixed) = Measure(runBatchMi, GetEngine(fixedMaxW), bgra, imgW, imgH, list, recBatch, reps);
            // 收敛批（narrowW == fixedMaxW）也【真的再跑一次】同一台引擎：正控判的是
            // "同一形状两次测量必须落在噪声内"，直接沿用固定臂就成了自证，量不出仪表的重复性
            var (msNarrow, textNarrow) = Measure(runBatchMi, GetEngine(narrowW), bgra, imgW, imgH, list, recBatch, reps);

            bool equal = textFixed.Length == textNarrow.Length
                && Enumerable.Range(0, textFixed.Length).All(i => textFixed[i] == textNarrow[i]);
            cells++;
            fixedTotalMs += msFixed;
            narrowTotalMs += msNarrow;
            double ratio = msNarrow > 0 ? (double)msFixed / msNarrow : 0.0;

            if (converging)
            {
                convBatches++;
                double rel = Math.Abs(msFixed - msNarrow) / (double)Math.Max(1, Math.Max(msFixed, msNarrow));
                convMaxRel = Math.Max(convMaxRel, rel);
            }
            else
            {
                discBatches++;
                minDiscRatio = Math.Min(minDiscRatio, ratio);
            }
            if (!equal)
            {
                textDiffBatches++;
                for (int i = 0; i < Math.Min(textFixed.Length, textNarrow.Length); i++)
                    if (textFixed[i] != textNarrow[i])
                        diffs.Add($"    批{start} 行{i}: 固定={Quote(textFixed[i])} 收窄={Quote(textNarrow[i])}");
            }
            rowsCsv.Add($"fixed,{start},{recBatch},{fixedMaxW},{narrowW},{msFixed},{(equal ? "yes" : "NO")},"
                + (converging ? "正控·两臂同形" : ""));
            rowsCsv.Add($"narrow,{start},{recBatch},{fixedMaxW},{narrowW},{msNarrow},{(equal ? "yes" : "NO")},"
                + $"ratio={ratio:F2}x");
            Console.WriteLine($"  批{start,3} n={recBatch} 固定{fixedMaxW}={msFixed,7}ms | 收窄{narrowW,5}={msNarrow,7}ms "
                + $"| Δ={msFixed - msNarrow,7}ms ({ratio:F2}x) 文本相同={(equal ? "是" : "❌否")} "
                + (converging ? "[正控·两臂同形]" : ""));
        }

        foreach (var e in engines.Values) e.Dispose();

        if (keep)
        {
            var path = Path.Combine(outDir, $"keep_ocr_rec_page_{imgW}x{imgH}.bgra");
            File.WriteAllBytes(path, bgra);
            Console.WriteLine($"  --keep → {path}（喂给仪表的那一页像素，BGRA8；外部裁判可用）");
        }

        double totalRatio = narrowTotalMs > 0 ? (double)fixedTotalMs / narrowTotalMs : 0.0;
        Console.WriteLine($"\n  合计 rec 段：固定={fixedTotalMs}ms 收窄={narrowTotalMs}ms ⇒ 端到端差 "
            + $"{fixedTotalMs - narrowTotalMs}ms（{totalRatio:F3}x）");
        Console.WriteLine($"  格子={cells} 判别批={discBatches} 同形批(正控)={convBatches} 文本不一致批={textDiffBatches}");

        var csvPath = WriteCsv(outDir, "bench_ocr_rec", rowsCsv);
        Console.WriteLine($"CSV → {csvPath}");

        // ── 判据（fail-closed）──
        if (cells == 0)
        {
            Console.WriteLine("❌ 枚举到 0 个格子（--rows 小于 RecBatchSize？合批一批都没凑齐）—— 仪表什么都没跑");
            return 1;
        }
        if (textDiffBatches > 0)
        {
            Console.WriteLine($"❌❌ 收窄改变了识别文本（{textDiffBatches}/{cells} 批）—— 这一项【不是性能收益，"
                + "是正确性回归】，先看下面的逐行差，别看比值");
            foreach (var d in diffs.Take(20)) Console.WriteLine(d);
            return 1;
        }
        if (convBatches == 0)
        {
            Console.WriteLine($"❌ 正控缺失 —— 没有任何一批的批内真实最大宽 == 固定 {fixedMaxW}（两臂同形），"
                + "仪表无法证明自己在应收敛处收敛。把 --rows 调大或塞进一条超长行再来一轮");
            return 1;
        }
        if (convMaxRel > 0.10)
        {
            Console.WriteLine($"❌ non-discriminating:convergence —— 两臂同形的批却给出最大 {convMaxRel:P0} 的 ms 差，"
                + "说明仪表在比的不是宽度（有别的变量在动），本次不判");
            return 1;
        }
        if (discBatches == 0)
        {
            Console.WriteLine("❌ non-discriminating —— 没有任何一批的两臂宽度不同，宽度维没被量到");
            return 1;
        }
        if (minDiscRatio < 1.02)
        {
            Console.WriteLine($"❌ non-discriminating:timing —— 判别批里最小比值只有 {minDiscRatio:F2}x（<1.02），"
                + "宽度换了而 ms 没动 ⇒ 这台量不出宽度维的成本，本次不判");
            return 1;
        }
        Console.WriteLine($"✅ bench-ocr-rec 通过（{cells} 批两臂识别文本逐批相同；同形批收敛 ≤{convMaxRel:P1}；"
            + $"判别批比值 ≥{minDiscRatio:F2}x；合计 {totalRatio:F3}x）");
        Console.WriteLine("   ⚠ 文本相同这一条在产品里是靠 DecodeRecognitionRow 那句 "
            + ".Where(d => d-1 < _dict.Length) 兜住的（模型第 18710 类 = 尾随特殊类）；"
            + "那道界一旦动，收窄就会把尾类暴露成文本差 —— 见 2026-10-31 仪表读数");
        return 0;
    }

    private static (long ms, string[] texts) Measure(MethodInfo runBatch, OnnxOcrEngine engine,
        byte[] bgra, int imgW, int imgH, object boxList, int count, int reps)
    {
        // 预热一次（首跑有 JIT / DML 内核装载，不该进中位）
        string[] last = Invoke(runBatch, engine, bgra, imgW, imgH, boxList, count);
        var times = new List<long>();
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            last = Invoke(runBatch, engine, bgra, imgW, imgH, boxList, count);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }
        return (Median(times), last);
    }

    private static string[] Invoke(MethodInfo runBatch, OnnxOcrEngine engine,
        byte[] bgra, int imgW, int imgH, object boxList, int count)
        => (string[])runBatch.Invoke(engine,
            new object?[] { bgra, imgW, imgH, boxList, 0, count })!;

    private static object MakeBoxList(Type boxType, List<(float X1, float Y1, float X2, float Y2)> boxes,
        int start, int count)
    {
        var listType = typeof(List<>).MakeGenericType(boxType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        for (int i = 0; i < count; i++)
        {
            var b = boxes[start + i];
            // Box(float X1, float Y1, float X2, float Y2, bool IsFragment = false) —— 非子框
            list.Add(Activator.CreateInstance(boxType,
                new object[] { b.X1, b.Y1, b.X2, b.Y2, false })!);
        }
        return list;
    }

    /// <summary>批内真实最大 recW（未量化）。见 FillRecognitionInput 的同一式子。</summary>
    private static int MaxNatural(int[] natural, int start, int count)
    {
        int m = 0;
        for (int i = 0; i < count && start + i < natural.Length; i++)
            m = Math.Max(m, natural[start + i]);
        return m;
    }

    /// <summary>逐字对齐 OnnxOcrEngine.FillRecognitionInput：crop(+padX/padY) → 保比例 → recW=⌈48*AR⌉ 夹到 maxW。</summary>
    private static int NaturalRecW((float X1, float Y1, float X2, float Y2) box,
        int imgW, int imgH, int recH, int maxW)
    {
        int x = Math.Max(0, (int)box.X1);
        int y = Math.Max(0, (int)box.Y1);
        int bw = Math.Min(imgW - x, (int)(box.X2 - box.X1));
        int bh = Math.Min(imgH - y, (int)(box.Y2 - box.Y1));
        if (bw <= 0 || bh <= 0) return 4;
        int padX = Math.Max(2, bw / 10);
        int padY = Math.Max(2, bh / 5);
        int cropX = Math.Max(0, x - padX);
        int cropY = Math.Max(0, y - padY);
        int cropW = Math.Min(imgW - cropX, bw + padX * 2);
        int cropH = Math.Min(imgH - cropY, bh + padY * 2);
        if (cropW <= 0 || cropH <= 0) return 4;
        int recW = Math.Min((int)Math.Ceiling(recH * ((float)cropW / cropH)), maxW);
        return Math.Max(4, recW);
    }

    private static int RoundUp(int v, int step) => step <= 1 ? v : (v + step - 1) / step * step;

    /// <summary>确定性合成页：白底 + 固定字号文本行，逐行取字形包围盒当"det 给的框"。</summary>
    private static (byte[] Bgra, int W, int H, List<(float X1, float Y1, float X2, float Y2)> Boxes)
        BuildSyntheticPage(int rows)
    {
        const int w = 1400, rowPitch = 60, margin = 20;
        int h = margin * 2 + rowPitch * rows;
        var boxes = new List<(float X1, float Y1, float X2, float Y2)>();
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Microsoft YaHei", 26f,
                System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            for (int i = 0; i < rows; i++)
            {
                string text = Lines[i % Lines.Length];
                float y = margin + i * rowPitch;
                g.DrawString(text, font, brush, margin, y);
                // 字形包围盒（不是 MeasureString 的行盒：行盒会把 CJK 的行距也算进宽）
                System.Drawing.RectangleF rect;
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddString(text, font.FontFamily, (int)font.Style, font.Size,
                        new System.Drawing.PointF(margin, y),
                        // AddString 的第 6 参是 StringFormat（不是 StringAlignment）；GenericTypographic
                        // 只去掉行盒多余的上下留白，字形包围盒本身不受影响。
                        System.Drawing.StringFormat.GenericTypographic);
                    rect = path.GetBounds();
                }
                float x1 = Math.Max(0, rect.X - 3), y1 = Math.Max(0, rect.Y - 3);
                float x2 = Math.Min(w, rect.Right + 3), y2 = Math.Min(h, rect.Bottom + 3);
                if (x2 <= x1 || y2 <= y1) { x1 = margin; y1 = y; x2 = margin + 40; y2 = y + 26; }
                boxes.Add((x1, y1, x2, y2));
            }
        }

        var rectAll = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rectAll, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bytes = new byte[w * h * 4];
        try
        {
            // 逐行拷（stride 可能带填充；产品约定是紧凑 BGRA ⇒ 对齐后再拷）
            var src = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), src, 0, src.Length);
                Array.Copy(src, 0, bytes, y * w * 4, w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }
        return (bytes, w, h, boxes);
    }

    private static string Quote(string s) => "\"" + s + "\"";

    // ── 共用小件（口径同 SavePathBench.cs，刻意自带以免跨文件改私有件）──
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TrueToneCap.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string ResolveOut(string[] args)
    {
        string? o = GetOpt(args, "--out");
        if (string.IsNullOrWhiteSpace(o)) o = Path.Combine(FindRepoRoot(), "artifacts", "bench");
        o = Path.GetFullPath(o);
        Directory.CreateDirectory(o);
        return o;
    }

    private static string GetOpt(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null!;
    }

    private static int GetIntOpt(string[] args, string key, int def)
        => int.TryParse(GetOpt(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static long Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToArray();
        return s[s.Length / 2];
    }

    private static string WriteCsv(string outDir, string prefix, IEnumerable<string> lines)
    {
        var path = Path.Combine(outDir,
            $"{prefix}_{DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}.csv");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }
}
