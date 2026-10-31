// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/SavePathBench.cs
// 离线性能仪表（批4：把"纸面段"变成"有读数"）。七台，均不改产品代码，只走公开 API 或反射只读。
//
//   dotnet run --project src/TrueToneCap.Tools -- --bench-tiff     [--out DIR] [--w 3840 --h 2160] [--reps 3]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-color    [--out DIR] [--w 3840 --h 2160] [--reps 5]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-savepath [--out DIR] [--w 3840 --h 2160] [--formats png,tiff,...] [--timeout 60]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-pq-table [--out DIR] [--sweep 20000001] [--lut 4096] [--reps 3] [--w 3840 --h 2160]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-png-idat [--out DIR] [--w 1280 --h 720] [--reps 3] [--keep]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-hdr-routing [--out DIR] [--w 960 --h 640] [--bitdepth 10] [--reps 2] [--keep]
//   dotnet run --project src/TrueToneCap.Tools -- --bench-deflate-cap [--out DIR] [--strip-kib 1024] [--reps 3] [--seq 12]
//
// 约定：结论走 Environment.ExitCode（顶层入口是 void）+ 落盘 CSV；0=通过 / 1=回归或仪表不自证 / 3=前置条件缺失(SKIP)。
// 守卫 fail-closed：枚举到 0 个格子一律红。每台都带正控，指不出判别性就自我声明 "本次不判"。
// 反射只读、签名变了立刻抛 MissingMethodException（口径同 PerfMeasureBench.cs）。
// 输出默认落仓库内被 gitignore 的 artifacts/bench/，绝不写 %TEMP%（本仓有 %TEMP 产物被秒删的事故）。
// ⚠ 分派在 FormatBench.cs 的顶层入口（`--bench-*` 每条各一行）；新台必须在那里接上才可运行。
// ⚠ 容器（chunk 布局 / 后端选路）的**验收**裁判必须是外部解码器：本文件的自查只判
//   "两臂是否只差在搬运形状"，不判"文件能不能被解"。--keep 就是留给外部裁判的件。

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using TrueToneCap.Core;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;

/// <summary>七台离线性能仪表：TIFF 全尺寸计时 / 色彩·ICC 计时 / 一次保存分段累计 / PQ 查表正控 /
    /// PNG IDAT 分块 vs 整帧物化 / 高高光 PQ 夹具的 deflate 后端分流 / deflate 缓冲初值 A/B。</summary>
public static class SavePathBench
{
    // ═══════════════════════════════════════════════════════════════════
    //  共用：仓库根 / 输出目录 / 统计 / CSV / 合成帧
    // ═══════════════════════════════════════════════════════════════════

    // x64 LOH 门槛 = 85_000 字节；单次数组 > 该值即落 LOH。LOH 对象数按各 op 的输出数组尺寸手工估算
    // 并在 CSV 的 loh_objs_est 列标注（无 GC 剖析 API 时这是诚实口径；分配字节列是实测）。
    private const int DefaultW = 3840, DefaultH = 2160;

    /// <summary>自 bin/Debug/netX 逐级上溯找含 TrueToneCap.slnx 或 .git 的目录 = 仓库根。</summary>
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
        // 找不到根 ⇒ 退回 exe 同级 artifacts/（仍在仓库视野内，不写 %TEMP%）
        return AppContext.BaseDirectory;
    }

    /// <summary>解析 --out（默认 &lt;repo&gt;/artifacts/bench）。永远返回绝对路径并建目录。</summary>
    private static string ResolveOut(string[] args)
    {
        string? o = GetOpt(args, "--out");
        if (string.IsNullOrWhiteSpace(o))
            o = Path.Combine(FindRepoRoot(), "artifacts", "bench");
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

    private static double GetDoubleOpt(string[] args, string key, double def)
        => double.TryParse(GetOpt(args, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static long Median(List<long> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        return s[s.Length / 2];
    }

    private static double MedianDouble(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        return s[s.Length / 2];
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>把 CSV 行落盘（覆盖同名时间戳文件即可），返回实际路径。</summary>
    private static string WriteCsv(string outDir, string prefix, IEnumerable<string> lines)
    {
        var path = Path.Combine(outDir, $"{prefix}_{Stamp()}.csv");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    /// <summary>4K 确定性渐变 BGRA8（与 FormatBench 同形状：B=y梯度 G=x梯度 R=对角条纹 A=255）。
    /// 造真实熵，让 deflate / 编码耗时随内容稳定可复现。</summary>
    private static byte[] GenBgra(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = (byte)(y * 255 / h);                 // B
                bgra[i + 1] = (byte)(x * 255 / w);             // G
                bgra[i + 2] = (byte)(((x + y) % 512) * 255 / 512); // R
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    /// <summary>BGRA8 → 16-bit BGRA（每通道 v*257、大端），TIFF/PNG 16-bit 中转格式。</summary>
    private static byte[] ExpandTo16BE(byte[] bgra8)
    {
        var px = new byte[bgra8.Length * 2];
        for (int i = 0, o = 0; i < bgra8.Length; i += 4, o += 8)
        {
            WriteBE(px, o, bgra8[i] * 257);       // B
            WriteBE(px, o + 2, bgra8[i + 1] * 257); // G
            WriteBE(px, o + 4, bgra8[i + 2] * 257); // R
            WriteBE(px, o + 6, bgra8[i + 3] * 257); // A
        }
        return px;
    }

    private static void WriteBE(byte[] buf, int off, int v)
    {
        buf[off] = (byte)((v >> 8) & 0xFF);
        buf[off + 1] = (byte)(v & 0xFF);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  仪表 1：--bench-tiff  全尺寸 TIFF 计时（转换 / deflate / 组装+写盘 三段）
    // ═══════════════════════════════════════════════════════════════════
    //
    // 现状：ManagedTiffEncoder 全文 0 处 Parallel.、单 strip ⇒ 全尺寸耗时零读数。
    // 拆法（残差法，同 PerfMeasureBench）：
    //   convert_ms = 反射调 private ConvertToRgba 的中位
    //   deflate_ms = DeflateZlib.Compress(raw, level) 的中位（raw 来自同一份 ConvertToRgba）
    //   assemble+write_ms = 整趟 ManagedTiffEncoder.Encode(写盘) 的中位 − convert − deflate
    // 6 格 = {8-bit,16-bit} × {Smallest,Quick,Fastest}，打印产物字节。

    public static int RunTiff(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", DefaultW), h = GetIntOpt(args, "--h", DefaultH);
        int reps = Math.Max(2, GetIntOpt(args, "--reps", 3));
        bool keep = Array.IndexOf(args, "--keep") >= 0;
        Console.WriteLine($"═══ [bench-tiff] 全尺寸 TIFF 三段计时 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h} reps={reps} (8-bit 与 16-bit 各 × 三档 effort)");

        // 反射：private ConvertToRgba(byte[] bgra, int w, int h, int rowsPerStrip, byte[][] stripRaw,
        //   bool srcIs16, int bitDepth) -> void（2026-10-31 分条并行后由"返回整帧 byte[]"改成直写各条缓冲）
        var convMi = typeof(ManagedTiffEncoder).GetMethod("ConvertToRgba",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]), typeof(int), typeof(int), typeof(int),
                    typeof(byte[][]), typeof(bool), typeof(int) }, modifiers: null)
            ?? throw new MissingMethodException(
                "ManagedTiffEncoder.ConvertToRgba(byte[],int,int,int,byte[][],bool,int) 签名变了（仪表按此反射）");

        var src8 = GenBgra(w, h);
        var src16 = ExpandTo16BE(src8);

        var arms = new (string Name, byte[] Px, int BitDepth, bool Src16)[]
        {
            ("8bit", src8, 8, false),
            ("16bit", src16, 16, true),
        };
        var efforts = new[] { CompressionEffort.Smallest, CompressionEffort.Quick, CompressionEffort.Fastest };

        var rows = new List<string>();
        rows.Add("arm,bitdepth,effort,level,convert_ms,deflate_ms,assemble_write_ms,total_encode_ms,bytes");
        int cells = 0;
        // 正控收集：每臂 effort 两档产物字节必须不同；deflate level9 与 level1 计时必须有差
        bool anyBytesDiscriminated = false, anyDeflateDiscriminated = false;

        foreach (var (name, px, bitDepth, srcIs16) in arms)
        {
            // 转换段：与产品同形 —— 先按 ~1 MiB/条切好条缓冲，再让 ConvertToRgba 直写各条。
            // 分条参数（StripTargetBytes=1<<20、末条可短）照抄 ManagedTiffEncoder.Encode，
            // 对不上就说明产品那边改了形状而这台仪表还在量旧账。
            int outStride = bitDepth > 8 ? 8 : 4;
            long rowBytes = (long)w * outStride;
            int rowsPerStrip = (int)Math.Clamp((1L << 20) / rowBytes, 1L, (long)h);
            int stripCount = (int)(((long)h + rowsPerStrip - 1) / rowsPerStrip);

            var convTimes = new List<long>();
            byte[][] stripRaw = null!;
            for (int i = 0; i < reps; i++)
            {
                stripRaw = new byte[stripCount][];
                for (int s = 0; s < stripCount; s++)
                {
                    long rowsIn = Math.Min((long)rowsPerStrip, (long)h - (long)s * rowsPerStrip);
                    stripRaw[s] = new byte[(int)(rowsIn * rowBytes)];
                }
                var sw = Stopwatch.StartNew();
                convMi.Invoke(null, new object?[] { px, w, h, rowsPerStrip, stripRaw, srcIs16, bitDepth });
                sw.Stop();
                convTimes.Add(sw.ElapsedMilliseconds);
            }
            long convertMs = Median(convTimes);

            long bytesSmall = 0, bytesFast = 0;
            long defSmall = 0, defFast = 0;

            foreach (var effort in efforts)
            {
                int level = CompressionEffortMap.PngTiffDeflateLevel(effort);
                // deflate 段口径 ⚠ 这台仪表是【串行逐条】且【逐条各判一次后端】（走公开的
                // DeflateZlib.Compress）；产品里是【整帧判一次 + 条间并行】。所以这一列是
                // "分条带来的上限代价"，比 total_encode_ms 慢是预期的，别拿它当产品读数。
                var defTimes = new List<long>();
                int defReps = effort == CompressionEffort.Smallest ? Math.Min(reps, 2) : reps;
                long defBytes = 0;
                for (int i = 0; i < defReps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    long sum = 0;
                    for (int s = 0; s < stripCount; s++) sum += DeflateZlib.Compress(stripRaw[s], level).Length;
                    sw.Stop();
                    defTimes.Add(sw.ElapsedMilliseconds);
                    defBytes = sum;
                }
                long deflateMs = Median(defTimes);

                // 整趟（含组装 + 写盘）：写到 out 下唯一临时文件，取字节后立即删
                var totalTimes = new List<long>();
                long fileBytes = 0;
                for (int i = 0; i < defReps; i++)
                {
                    string tmp = Path.Combine(outDir, $"bench_tiff_{Guid.NewGuid():N}.tif");
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        ManagedTiffEncoder.Encode(px, w, h, tmp, bitDepth,
                            iccProfile: null, inputBitDepth: srcIs16 ? 16 : 8, effort: effort);
                        sw.Stop();
                        totalTimes.Add(sw.ElapsedMilliseconds);
                        fileBytes = new FileInfo(tmp).Length;
                        // --keep：把一份产物留在 outDir 里给【外部解码器】当裁判用
                        // （多 strip 改了容器结构，判"能不能解、像素对不对"不许用仓内第二份实现）。
                        if (keep)
                        {
                            string dst = Path.Combine(outDir, $"keep_tiff_{w}x{h}_{name}_{effort}.tif");
                            File.Copy(tmp, dst, overwrite: true);
                            // 同时留下【喂给编码器的那份输入】：外部解码器解回来的 RGBA
                            // 要与它逐通道对上，才算"多 strip 的容器改动没动像素"。
                            // 拿仓内第二个解码器自证不算证（本仓口径：裁判必须是外部实现）。
                            File.WriteAllBytes(Path.Combine(outDir, $"keep_tiff_{w}x{h}_{name}_src.bgra"), px);
                        }
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                long totalMs = Median(totalTimes);
                // ⚠ 这一列是【推算】不是测量：deflate 那列是"串行逐条"口径，比产品里的
                //    "整帧判一次 + 条间并行"慢，所以差值经常为负。为负就写 -1，
                //    不许留一个 0 让人读成"组装与写盘不要钱"。
                long assembleWrite = totalMs - convertMs - deflateMs;
                if (assembleWrite < 0) assembleWrite = -1;

                rows.Add($"{name},{bitDepth},{effort},{level},{convertMs},{deflateMs},{assembleWrite},{totalMs},{fileBytes}");
                Console.WriteLine($"  {name} {effort,-8} lvl={level} convert={convertMs}ms deflate={deflateMs}ms " +
                                  $"assemble+write={assembleWrite}ms total={totalMs}ms bytes={fileBytes}");
                cells++;

                if (effort == CompressionEffort.Smallest) { bytesSmall = fileBytes; defSmall = deflateMs; }
                if (effort == CompressionEffort.Fastest) { bytesFast = fileBytes; defFast = deflateMs; }
            }

            bool bytesDiffer = bytesSmall != bytesFast;
            // deflate 计时判别：level9 应比 level1 慢（至少不更快）
            bool defDisc = defSmall > defFast;
            Console.WriteLine($"  [正控:{name}] effort 产物字节不同={(bytesDiffer ? "是" : "否")} " +
                              $"(smallest={bytesSmall} fastest={bytesFast}) | deflate lvl9>lvl1={(defDisc ? "是" : "否")} " +
                              $"({defSmall}ms vs {defFast}ms)");
            if (bytesDiffer) anyBytesDiscriminated = true;
            if (defDisc) anyDeflateDiscriminated = true;
        }

        var csvPath = WriteCsv(outDir, "bench_tiff", rows);
        Console.WriteLine($"\nCSV → {csvPath}");

        if (cells == 0)
        {
            Console.WriteLine("❌ 枚举到 0 个格子 —— 仪表什么都没跑");
            return 1;
        }
        if (!anyBytesDiscriminated)
        {
            Console.WriteLine("❌ non-discriminating:effort —— 没有任何一档 effort 改变产物字节，仪表不能证明 effort 下发");
            return 1;
        }
        if (!anyDeflateDiscriminated)
            Console.WriteLine("⚠ non-discriminating:deflate_timing —— 同图 lvl9/lvl1 deflate 计时差在噪声内；本次不判 deflate 段耗时");
        Console.WriteLine("✅ bench-tiff 通过（effort 判别成立）");
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  仪表 2：--bench-color  色彩/ICC 与色调映射计时（4K）
    // ═══════════════════════════════════════════════════════════════════
    //
    // 现状：结构判据一堆但不计时，唯一计时是 FormatBench 开场那行色调映射（stdout、未落盘）。
    // 量：FloatToSRgbBytes / HdrToPq16 / HdrToArgb10Pq / ConvertFloat16ToSdrBgra /
    //     PreparePixelsWithIcc（Tools 内桥接，走 Core ColorDecision）/ GetDisplayIccProfile(Async)。
    // 读数：ms/次 + 分配字节（GC.GetTotalAllocatedBytes 全线程口径）+ LOH 对象数（估算并标注）。

    public static int RunColor(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", DefaultW), h = GetIntOpt(args, "--h", DefaultH);
        int reps = Math.Max(3, GetIntOpt(args, "--reps", 5));
        Console.WriteLine($"═══ [bench-color] 色彩·ICC·色调映射计时 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h} reps={reps}  分配口径=GC.GetTotalAllocatedBytes(precise) 全线程");

        var bgra = GenBgra(w, h);
        var hdrLin = PixelOps.BgraToScrgbLinearFast(bgra, w, h); // float[4/px]
        var tone = new ToneMappingParams(ToneMapMode.SegmentedReinhard, 0, 200, 1000);
        var frame = new HdrFrameData { Pixels = hdrLin, Width = w, Height = h };

        var rows = new List<string>();
        rows.Add("op,width,height,reps,ms_per_call,total_alloc_bytes,loh_objs_est,detail");

        int cells = 0;
        long maxAllocSeen = 0;

        // 每个 op：预热 1 次，随后 reps 次取中位；分配 = (末-首)/reps；LOH = 该 op 单次会分配的 >85KB 数组个数（估算）。
        void Measure(string name, Func<object?> op, int lohEst, string detail)
        {
            try
            {
                op(); op(); // 预热（并行/JIT 一次性开销）
                long base0 = GC.GetTotalAllocatedBytes(precise: true);
                var times = new List<long>();
                for (int i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var res = op();
                    sw.Stop();
                    GC.KeepAlive(res);
                    times.Add(sw.ElapsedMilliseconds);
                }
                long alloc = (GC.GetTotalAllocatedBytes(precise: true) - base0) / reps;
                long ms = Median(times);
                maxAllocSeen = Math.Max(maxAllocSeen, alloc);
                rows.Add($"{name},{w},{h},{reps},{ms},{alloc},{lohEst},{CsvSafe(detail)}");
                Console.WriteLine($"  {name,-26} {ms,6} ms/call  alloc={FormatBytes(alloc)}/call  LOH≈{lohEst}  {detail}");
                cells++;
            }
            catch (Exception ex)
            {
                rows.Add($"{name},{w},{h},0,-1,-1,-1,ERROR:{ex.GetType().Name}:{CsvSafe(ex.Message)}");
                Console.WriteLine($"  {name,-26} ⚠ 抛 {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 4K 单次调用典型分配的大对象：
        Measure("FloatToSRgbBytes", () => ToneMapper.FloatToSRgbBytes(hdrLin, w, h, tone), 1, "输出 byte[4/px]");
        Measure("HdrToArgb10Pq", () => ToneMapper.HdrToArgb10Pq(hdrLin, w, h, "BT2020"), 1, "输出 byte[4/px] + 矩阵");
        Measure("HdrToPq16@10bit", () => FormatHelper.HdrToPq16(frame, "BT2020", 10), 1, "输出 ushort[4/px]");
        Measure("HdrToPq16@16bit", () => FormatHelper.HdrToPq16(frame, null, 16), 1, "输出 ushort[4/px]");
        // ⚠ 这个核在 ColorSpaceConverter 里（1684 行住的那类型），不在 ColorProfileProvider ——
        //    按文件引用符号是本仓最容易写错的一类，编译期就撞上它。
        Measure("ConvertFloat16ToSdrBgra", () => ColorSpaceConverter.ConvertFloat16ToSdrBgra(hdrLin, w, h, "BT2020", tone), 2, "converted float[] + byte[]");
        Measure("PreparePixelsWithIcc", () => CapturePipelineServiceBridge.PreparePixelsWithIcc(bgra, w, h, bake: true, "BT2020"), 1, "烘焙 → 目标色域");

        // ICC 获取：同步只读缓存（未命中恒 null，故先 Async 预热再测同步命中成本）
        MeasureIccAcquisition(rows, reps);

        var csvPath = WriteCsv(outDir, "bench_color", rows);
        Console.WriteLine($"\nCSV → {csvPath}");

        if (cells == 0)
        {
            Console.WriteLine("❌ 枚举到 0 个格子");
            return 1;
        }
        // 正控（仪表活着）：这些 op 全都产出大数组 ⇒ 至少有一格 alloc>0；若全为 0 说明没在测真活。
        if (maxAllocSeen <= 0)
        {
            Console.WriteLine("❌ non-discriminating:alloc —— 所有色彩 op 的分配读数都是 0，仪表没在测到实际工作");
            return 1;
        }
        Console.WriteLine("✅ bench-color 通过");
        return 0;
    }

    private static void MeasureIccAcquisition(List<string> rows, int reps)
    {
        // 前置：至少一块显示器；没有 ⇒ 该格 SKIP(3)，不虚报 ms。
        List<(nint Handle, string Name)> displays;
        try
        {
            displays = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays()
                .Select(d => (d.MonitorHandle, d.Name)).ToList();
        }
        catch (Exception ex)
        {
            rows.Add($"GetDisplayIccProfile,-,-,0,-1,-1,-1,ERROR:enumerate:{ex.GetType().Name}");
            Console.WriteLine($"  {"GetDisplayIccProfile",-26} ⚠ 枚举显示器失败 {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (displays.Count == 0)
        {
            rows.Add("GetDisplayIccProfile,-,-,0,-1,-1,-1,SKIP:no_display");
            Console.WriteLine($"  {"GetDisplayIccProfile",-26} SKIP —— 无显示器（退出码 3），headless 无法量 WCS 取 ICC");
            return;
        }

        // WCS 冷取（真工作）：先清缓存，异步首取计时。
        var handle = displays[0].Handle;
        try
        {
            ColorProfileProvider.InvalidateCache();
            var sw = Stopwatch.StartNew();
            byte[]? icc = ColorProfileProvider.GetDisplayIccProfileAsync(handle).GetAwaiter().GetResult();
            sw.Stop();
            rows.Add($"GetDisplayIccProfileAsync(cold),-,-,1,{sw.ElapsedMilliseconds},{icc?.Length ?? 0},-1,{CsvSafe(displays[0].Name)}");
            Console.WriteLine($"  {"GetDisplayIccProfileAsync",-26} {sw.ElapsedMilliseconds,6} ms (cold) {(icc is null ? "无 WCS ICC" : $"{icc.Length}B")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {"GetDisplayIccProfileAsync",-26} ⚠ {ex.GetType().Name}: {ex.Message}");
        }

        // 同步命中缓存成本（编码保存时走的就是这条）
        ColorProfileProvider.GetDisplayIccProfile(handle); // 触发/等待预热
        var times = new List<long>();
        for (int i = 0; i < reps; i++)
        {
            var s = Stopwatch.StartNew();
            var c = ColorProfileProvider.GetDisplayIccProfile(handle);
            s.Stop();
            GC.KeepAlive(c);
            times.Add(s.ElapsedMilliseconds);
        }
        long ms = Median(times);
        rows.Add($"GetDisplayIccProfile(cache-hit),-,-,{reps},{ms},0,0,只读缓存");
        Console.WriteLine($"  {"GetDisplayIccProfile(sync)",-26} {ms,6} ms (cache-hit)");
    }

    private static string FormatBytes(long b) => b >= 1024 * 1024
        ? $"{b / 1048576.0:F1} MB" : (b >= 1024 ? $"{b / 1024.0:F1} KB" : $"{b} B");

    // ═══════════════════════════════════════════════════════════════════
    //  仪表 3：--bench-savepath  一次保存的分段累计（取像素→色彩/色调→编码+写盘）
    // ═══════════════════════════════════════════════════════════════════
    //
    // 现状：没有任何仪表把整条相加，只能靠散落日志拼。用 Core 公开入口 EncoderFactory.Create(...)
    //   .EncodeSdrAsync/EncodeAsync 跑一次 4K 保存（PNG/TIFF/AVIF/JXL/WebP/JPEG-LI/GainMap 各一档）。
    //   分段：取像素（合成源）/ 色彩·色调（PreparePixelsWithIcc 或 HDR 线性化）/ 编码+写盘（公开入口，
    //   产品在 EncodeAsync 内部把编码与写盘绑定；更细的 deflate/组装/写盘 拆分见 --bench-tiff）。
    //   每段 ms + 总 ms 落 CSV，并印每段分配字节（GC.GetAllocatedBytesForCurrentThread 前后差，不改产品代码）。

    private static readonly (string Key, OutputFormat Fmt, string Name, float Quality, bool Hdr)[] SaveFormats =
    {
        ("png",    OutputFormat.PNG,         "PNG",         100f, false),
        ("tiff",   OutputFormat.TIFF,        "TIFF",        100f, false),
        ("webp",   OutputFormat.WebP,        "WebP",        90f,  false),
        ("jpegli", OutputFormat.JPEG_LI,     "JPEG_LI",     2.0f, false),
        ("jxl",    OutputFormat.JPEG_XL,     "JPEG_XL",     2.0f, false),
        ("avif",   OutputFormat.AVIF,        "AVIF",        30f,  false),
        ("gainmap",OutputFormat.JPEG_GAINMAP,"JPEG_GAINMAP",1.0f, true),
    };

    public static int RunSavePath(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", DefaultW), h = GetIntOpt(args, "--h", DefaultH);
        double timeoutS = GetDoubleOpt(args, "--timeout", 60);
        string? filter = GetOpt(args, "--formats");
        var keys = (string.IsNullOrWhiteSpace(filter)
            ? SaveFormats.Select(f => f.Key)
            : filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"═══ [bench-savepath] 一次保存分段累计 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h} timeout={timeoutS}s formats=[{string.Join(",", keys)}]");

        var rows = new List<string>();
        rows.Add("fmt,pixels_ms,color_tone_ms,encode_write_ms,total_ms,bytes,pixels_alloc,color_tone_alloc,encode_alloc,status");

        int okCells = 0, skipCells = 0;
        long pngBytes = -1, tiffBytes = -1;

        foreach (var (key, fmt, name, quality, hdr) in SaveFormats)
        {
            if (!keys.Contains(key)) continue;
            var encoder = EncoderFactory.Create(fmt);
            string ext = fmt switch
            {
                OutputFormat.JPEG_GAINMAP or OutputFormat.JPEG_LI => ".jpg",
                OutputFormat.JPEG_XL => ".jxl",
                OutputFormat.TIFF => ".tif",
                OutputFormat.WebP => ".webp",
                OutputFormat.AVIF => ".avif",
                _ => ".png",
            };
            string path = Path.Combine(outDir, $"bench_savepath_{key}_{Guid.NewGuid():N}{ext}");

            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sPixels = Stopwatch.StartNew();
            byte[] src = GenBgra(w, h);          // 取像素（合成源，代替真实截图）
            sPixels.Stop();
            long a1 = GC.GetAllocatedBytesForCurrentThread();

            long colorMs = 0; long aColor0 = 0, aColor1 = 0, encBase = 0;
            object? payload = null;
            try
            {
                var sColor = Stopwatch.StartNew();
                aColor0 = GC.GetAllocatedBytesForCurrentThread();
                if (hdr && encoder.SupportsHdr)
                {
                    // HDR 路：线性化为 scRGB（产品色调映射在编码器内部再做）
                    payload = new HdrFrameData { Pixels = PixelOps.BgraToScrgbLinearFast(src, w, h), Width = w, Height = h };
                }
                else
                {
                    // SDR 路：PreparePixelsWithIcc（桥接走 Core ColorDecision；tag=System → 直通不烘焙）
                    var (px, icc, tag) = CapturePipelineServiceBridge.PreparePixelsWithIcc(src, w, h, bake: true, "System");
                    payload = (px, icc, tag);
                }
                aColor1 = GC.GetAllocatedBytesForCurrentThread();
                sColor.Stop();
                colorMs = sColor.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                rows.Add($"{name},{sPixels.ElapsedMilliseconds},-1,-1,-1,-1,{a1 - a0},0,0,ERROR:color:{ex.GetType().Name}");
                Console.WriteLine($"  {name,-12} ⚠ 色彩/色调段异常 {ex.GetType().Name}: {ex.Message}");
                skipCells++; continue;
            }

            var settings = new EncodingSettings
            {
                Format = fmt,
                Quality = quality,
                HdrOutput = hdr && encoder.SupportsHdr,
                ColorSpaceTag = hdr ? "BT2020" : "System",
            };
            if (payload is ValueTuple<byte[], byte[]?, string> tup)
            {
                if (tup.Item2 is not null) settings.IccProfile = tup.Item2;
                settings.ColorSpaceTag = tup.Item3;
            }

            long encMs = 0, allocEnc = 0, fileBytes = 0;
            string status;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutS));
                GC.Collect();
                encBase = GC.GetAllocatedBytesForCurrentThread();
                var sEnc = Stopwatch.StartNew();
                var task = payload is HdrFrameData hf
                    ? encoder.EncodeAsync(hf, settings, path, cts.Token)
                    : encoder.EncodeSdrAsync(((ValueTuple<byte[], byte[]?, string>)payload!).Item1, w, h, settings, path, cts.Token);
                task.GetAwaiter().GetResult();
                sEnc.Stop();
                encMs = sEnc.ElapsedMilliseconds;
                allocEnc = GC.GetAllocatedBytesForCurrentThread() - encBase;
                status = "ok";
                if (File.Exists(path)) fileBytes = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                // 外部编码器缺件 / 超时 ⇒ 该档 SKIP(3)，不算回归；托管档（PNG/TIFF）失败才算红。
                bool managed = fmt is OutputFormat.PNG or OutputFormat.TIFF;
                status = managed ? "FAIL:managed" : "skip:" + ex.GetType().Name;
                Console.WriteLine($"  {name,-12} {(managed ? "❌" : "⏭")} {ex.GetType().Name}: {Truncate(ex.Message, 80)}");
            }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }

            long total = sPixels.ElapsedMilliseconds + colorMs + encMs;
            rows.Add($"{name},{sPixels.ElapsedMilliseconds},{colorMs},{encMs},{total},{fileBytes},{a1 - a0},{aColor1 - aColor0},{allocEnc},{status}");
            Console.WriteLine($"  {name,-12} pixels={sPixels.ElapsedMilliseconds}ms color/tone={colorMs}ms " +
                              $"encode+write={encMs}ms total={total}ms bytes={fileBytes} [{status}]");
            if (status == "ok")
            {
                okCells++;
                if (fmt == OutputFormat.PNG) pngBytes = fileBytes;
                if (fmt == OutputFormat.TIFF) tiffBytes = fileBytes;
            }
            else if (status.StartsWith("skip")) skipCells++;
            else return 1; // 托管档失败 = 真回归
        }

        var csvPath = WriteCsv(outDir, "bench_savepath", rows);
        Console.WriteLine($"\nCSV → {csvPath}");

        if (okCells == 0)
        {
            // 全 SKIP（除托管外都缺件）且托管也没成功 ⇒ 前置条件不满足
            Console.WriteLine(skipCells > 0
                ? "⏭ SKIP —— 无任何档成功（多为外部编码器缺件/无显示器）"
                : "❌ 枚举到 0 个格子");
            return skipCells > 0 ? 3 : 1;
        }
        // 正控（格式判别）：PNG 与 TIFF 同时跑 ⇒ 产物字节必须不同（不同容器）；相等 ⇒ 仪表没在区分格式。
        if (pngBytes >= 0 && tiffBytes >= 0)
        {
            if (pngBytes == tiffBytes)
            {
                Console.WriteLine("❌ non-discriminating:format —— PNG 与 TIFF 产物字节相同，仪表没在真正区分格式");
                return 1;
            }
            Console.WriteLine($"  [正控] PNG({pngBytes}B) vs TIFF({tiffBytes}B) 产物不同 ⇒ 格式判别成立");
        }
        Console.WriteLine($"✅ bench-savepath 通过（成功 {okCells} 档" + (skipCells > 0 ? $"，跳过 {skipCells} 档" : "") + "）");
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  仪表 4：--bench-pq-table  PQ 查表候选的正控
    // ═══════════════════════════════════════════════════════════════════
    //
    // 主循环正把逐像素 MathF.Pow 换成 L^(1/4) 双 sqrt + 4096 格插值表（外科已实测 2.30×，
    // 10-bit max=1）。这台把同一件事挪进仓库可复跑，且与产品解耦：
    //   ref       = 本 bench 内独立实现的 ST2084 pow 公式（恒定语义基准，不受产品改动影响）
    //   product   = 反射 CreateDelegate 绑到产品 FormatHelper.LinearToPQ（换实现后自动跟随被测）
    //   cand      = s=L^(1/4) 均匀 4096 格 + 线性插值（提案表，表点用 ref 生成）
    //   weak(正控)= L 均匀 64 格粗表 —— 必须 max 码字误差>1，否则说明误差扫描是死的
    // 判据（口径）：10-bit max 码字误差 ≤1 才算语义不动。12-bit 一并报告。
    // 输出：(a) 两者 ms/帧，(b) 10-bit 与 12-bit 两种 max 码字误差，(c) 不同码占比。

    private const float PQ_m1 = 2610f / 16384f;
    private const float PQ_m2 = 2523f / 32f;
    private const float PQ_c1 = 3424f / 4096f;
    private const float PQ_c2 = 2413f / 128f;
    private const float PQ_c3 = 2392f / 128f;

    // 语义基准：与产品同一公式，但独立在 bench 里实现（主循环换实现也不会动摇它）。
    private static float PqRef(float scRgbLinear)
    {
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, PQ_m1);
        return MathF.Pow((PQ_c1 + PQ_c2 * Lp) / (1f + PQ_c3 * Lp), PQ_m2);
    }

    // 候选表：以 s = L^(1/4) = sqrt(sqrt(L)) 为自变量均匀建 N 格（eval 期 0 次 pow，2 次 sqrt + 1 次插值）。
    private sealed class PqLut
    {
        private readonly float[] _t; // PQ 值，索引 = s*(N-1)
        private readonly int _n;
        public PqLut(int n, Func<float, float> ground)
        {
            _n = n;
            _t = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = (float)i / (n - 1);
                float L = s * s * s * s; // s^4 = L
                _t[i] = ground(L);       // 用 ref(L) 生成表点 ⇒ 候选是 ref 的逼近
            }
        }
        // 输入 = L（已 clamp 到 [0,1]）
        public float Eval(float L)
        {
            float s = MathF.Sqrt(MathF.Sqrt(L));     // L^(1/4) 双 sqrt
            float f = s * (_n - 1);
            int i0 = (int)f;
            if (i0 >= _n - 1) return _t[_n - 1];
            float frac = f - i0;
            return _t[i0] + (_t[i0 + 1] - _t[i0]) * frac;
        }
    }

    // 粗表正控：在 L 上均匀建 64 格（近黑区 PQ≈L^0.159 曲率大 → 误差被放大，必 >1）。
    private static float PqWeak(float L)
    {
        const int n = 64;
        if (L <= 0f) return 0f;
        if (L >= 1f) return 1f;
        float f = L * (n - 1);
        int i0 = (int)f;
        float frac = f - i0;
        float a = PqRefLinearTable(i0, n), b = PqRefLinearTable(i0 + 1, n);
        return a + (b - a) * frac;
    }
    private static readonly float[] WeakTable = BuildWeakTable();
    private static float[] BuildWeakTable()
    {
        const int n = 64;
        var t = new float[n];
        for (int i = 0; i < n; i++) t[i] = PqRef_L(i / (n - 1f));
        return t;
    }
    private static float PqRef_L(float L) // 直接喂 L（不是 scRgbLinear）到 PQ 内核
    {
        L = Math.Clamp(L, 0f, 1f);
        float Lp = MathF.Pow(L, PQ_m1);
        return MathF.Pow((PQ_c1 + PQ_c2 * Lp) / (1f + PQ_c3 * Lp), PQ_m2);
    }
    private static float PqRefLinearTable(int i, int n) => WeakTable[Math.Min(i, n - 1)];

    public static int RunPqTable(string[] args)
    {
        string outDir = ResolveOut(args);
        long sweep = (long)GetDoubleOpt(args, "--sweep", 20_000_001);
        int w = GetIntOpt(args, "--w", DefaultW), h = GetIntOpt(args, "--h", DefaultH);
        int reps = Math.Max(2, GetIntOpt(args, "--reps", 3));
        Console.WriteLine($"═══ [bench-pq-table] PQ 查表候选正控 ═══ out={outDir}");
        Console.WriteLine($"全域密扫 sweep={sweep:N0} 帧={w}x{h} reps={reps}  判据=10-bit max 码字误差≤1");

        if (sweep < 2) { Console.WriteLine("❌ sweep 必须 ≥2"); return 1; }

        // 产品 LinearToPQ(float)：反射取 delegate（签名变了立刻抛），换实现后自动跟随。
        var fhType = typeof(ManagedTiffEncoder).Assembly.GetType("TrueToneCap.Core.Encoding.FormatHelper", throwOnError: true)!;
        var linToPq = fhType.GetMethod("LinearToPQ", BindingFlags.NonPublic | BindingFlags.Static, binder: null,
                new[] { typeof(float) }, modifiers: null)
            ?? throw new MissingMethodException("FormatHelper.LinearToPQ(float) 签名变了（仪表按此反射）");
        Func<float, float> product = (Func<float, float>)linToPq.CreateDelegate(typeof(Func<float, float>));

        // 候选表：s 空间 4096 格，表点用 ref 生成。
        int N = Math.Max(2, GetIntOpt(args, "--lut", 4096));
        var cand = new PqLut(N, PqRef_L);

        // ── (a) 帧计时：ref(pow) vs cand(table) vs product(实测) ──
        var bgra = GenBgra(w, h);
        var lin = PixelOps.BgraToScrgbLinearFast(bgra, w, h); // float[4/px] RGBA
        int pxCount = w * h;

        double RefFrame() => FrameMs(lin, pxCount, l => PqRef(l));
        double CandFrame() => FrameMs(lin, pxCount, l => cand.Eval(Math.Clamp(l * 80f / 10000f, 0f, 1f)));
        double ProdFrame() => FrameMs(lin, pxCount, l => product(l));

        // 预热 + 中位
        RefFrame(); CandFrame(); ProdFrame();
        var refT = new List<double>(); var candT = new List<double>(); var prodT = new List<double>();
        for (int i = 0; i < reps; i++) { refT.Add(RefFrame()); candT.Add(CandFrame()); prodT.Add(ProdFrame()); }
        double refMs = MedianDouble(refT), candMs = MedianDouble(candT), prodMs = MedianDouble(prodT);
        double speedup = candMs > 0 ? refMs / candMs : 0;
        Console.WriteLine($"\n  ms/帧: ref(pow)={refMs:F1} product.LinearToPQ={prodMs:F1} cand(table)={candMs:F1}  speedup(ref/cand)={speedup:F2}x");

        // ── (b)(c) 全域密扫：码字误差 + 不同码占比 ──
        var acc = SweepAccuracy(sweep, cand, product);
        Console.WriteLine($"\n  全域密扫（L∈[0,1] {sweep:N0} 点）：");
        Console.WriteLine($"    cand 10-bit max={acc.CandMax10} 12-bit max={acc.CandMax12} 不同码(10b)={acc.CandDiff10 / (double)sweep:P3}");
        Console.WriteLine($"    product vs ref 10-bit max={acc.ProdMax10} 12-bit max={acc.ProdMax12}");
        Console.WriteLine($"    weak(粗表正控) 10-bit max={acc.WeakMax10} —— 期望 >1（证明误差扫描是活的）");

        var rows = new List<string>
        {
            "kind,metric,value",
            $"frame_ms,ref_pow,{refMs:F1}",
            $"frame_ms,product_LinearToPQ,{prodMs:F1}",
            $"frame_ms,cand_lut{N},{candMs:F1}",
            $"speedup,ref_over_cand,{speedup:F2}",
            $"max_codeword,cand_10bit,{acc.CandMax10}",
            $"max_codeword,cand_12bit,{acc.CandMax12}",
            $"max_codeword,product_10bit,{acc.ProdMax10}",
            $"max_codeword,product_12bit,{acc.ProdMax12}",
            $"max_codeword,weak_control_10bit,{acc.WeakMax10}",
            $"diff_ratio,cand_10bit,{acc.CandDiff10 / (double)sweep:R}",
            $"diff_ratio,cand_12bit,{acc.CandDiff12 / (double)sweep:R}",
            $"sweep,points,{sweep}",
        };
        var csvPath = WriteCsv(outDir, "bench_pq_table", rows);
        Console.WriteLine($"\nCSV → {csvPath}");

        // ── 判据（fail-closed） ──
        // 1) 正控粗表必须报 max>1；若它也 ≤1 ⇒ 误差扫描是死的 ⇒ 仪表不可信 ⇒ 红。
        if (acc.WeakMax10 <= 1)
        {
            Console.WriteLine("❌ 粗表正控未越界（max≤1）—— 误差扫描恒绿，本次不判");
            return 1;
        }
        // 2) 候选 10-bit max ≤1 = 语义不动（与外科口径一致）。
        bool candOk = acc.CandMax10 <= 1;
        // 3) 计时判别：cand 必须显著快于 ref(pow)。噪声内 ⇒ non-discriminating ⇒ 红。
        bool timingDisc = speedup >= 1.10;
        if (!timingDisc)
        {
            Console.WriteLine($"❌ non-discriminating:timing —— ref/cand 仅 {speedup:F2}x（<1.10），仪表区分不了两种实现");
            return 1;
        }
        if (!candOk)
        {
            Console.WriteLine($"❌ 候选表 10-bit max 码字误差={acc.CandMax10} (>1) —— 此表尺寸/形状会改语义");
            return 1;
        }
        // 4) 产品自身语义守住（当前 pow≈ref，换表后也必须 ≤1）。>1 说明产品实现偏离基准。
        if (acc.ProdMax10 > 1)
        {
            Console.WriteLine($"❌ product.LinearToPQ 10-bit max={acc.ProdMax10} (>1) 偏离 ref —— 产品语义变了");
            return 1;
        }
        Console.WriteLine($"✅ bench-pq-table 通过（候选 10-bit≤1，12-bit max={acc.CandMax12}；{speedup:F2}x；粗表正控越界成立）");
        return 0;
    }

    private static double FrameMs(float[] lin, int pxCount, Func<float, float> pq)
    {
        var sw = Stopwatch.StartNew();
        float acc = 0;
        // 每帧 3 通道 PQ（与外科口径一致）
        for (int p = 0; p < pxCount; p++)
        {
            int i = p * 4;
            acc += pq(lin[i]) + pq(lin[i + 1]) + pq(lin[i + 2]);
        }
        sw.Stop();
        GC.KeepAlive(acc);
        return sw.Elapsed.TotalMilliseconds;
    }

    private readonly record struct PqAcc(int CandMax10, int CandMax12, int ProdMax10, int ProdMax12,
        int WeakMax10, long CandDiff10, long CandDiff12);

    private static PqAcc SweepAccuracy(long sweep, PqLut cand, Func<float, float> product)
    {
        int candMax10 = 0, candMax12 = 0, prodMax10 = 0, prodMax12 = 0, weakMax10 = 0;
        long candDiff10 = 0, candDiff12 = 0;
        for (long k = 0; k < sweep; k++)
        {
            float L = (float)k / (sweep - 1);
            float r = PqRef_L(L);
            // 候选 eval 吃 L（scRgbLinear 那步在真实帧里由上层给；这里对齐扫 L 域）
            float c = cand.Eval(L);
            float wv = PqWeak(L);
            // 扫的是 L∈[0,1]（PQ 输入域）；product 吃 scRgbLinear，反算 L→nits→linear 再喂。
            float linear = Math.Min(L * 10000f / 80f, 100000f);
            float pr = product(linear);

            candMax10 = Math.Max(candMax10, CodewordErr(r, c, 1023));
            candMax12 = Math.Max(candMax12, CodewordErr(r, c, 4095));
            prodMax10 = Math.Max(prodMax10, CodewordErr(r, pr, 1023));
            prodMax12 = Math.Max(prodMax12, CodewordErr(r, pr, 4095));
            weakMax10 = Math.Max(weakMax10, CodewordErr(r, wv, 1023));
            if (Quant(r, 1023) != Quant(c, 1023)) candDiff10++;
            if (Quant(r, 4095) != Quant(c, 4095)) candDiff12++;
        }
        return new PqAcc(candMax10, candMax12, prodMax10, prodMax12, weakMax10, candDiff10, candDiff12);
    }

    private static int Quant(float v, int max) => (int)Math.Round(v * max, MidpointRounding.AwayFromZero);
    private static int CodewordErr(float a, float b, int max) => Math.Abs(Quant(a, max) - Quant(b, max));

    // ═══════════════════════════════════════
    //  批1 正控：8-bit AVIF 的三档必须真的改产物 (--bench-avif-effort)
    // ═══════════════════════════════════════
    /// <summary>
    /// 同一帧、同一质量，只换「压缩强度」，走产品的 8-bit 成品腿编三次。
    /// <para>存在的理由：那条腿以前把接口第 12 参的默认值 <c>Balanced</c> 当成实参用了——
    /// 漏传既不报错也不警告，唯一可见的表现就是"界面三档跑出一模一样的文件"。
    /// 所以这条仪表的判据只有一句：<b>三档产物必须互不相同</b>。相同即退回到本轮修掉的缺陷形状，退 1。</para>
    /// <para>⚠ 这是"档位有没有落到编码器"的判据，<b>不是</b>"哪一档更小更快"的承诺：
    /// NVENC 三档 preset 在本机产物差本来就小（10-31 的 4K 表里 Smallest/Balanced/Fastest
    /// = 968,182 / 965,850 / 1,009,741 B），软件 libaom 才是 14× 那一头。</para>
    /// </summary>
    public static int RunAvifEffort(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", 1024), h = GetIntOpt(args, "--h", 640);
        Console.WriteLine($"═══ [bench-avif-effort] 8-bit AVIF 三档对撞 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h}（后端按 Auto 解析：本机有 NVENC 就是硬件腿）");

        var px = GenBgra(w, h);
        var efforts = new[] { CompressionEffort.Smallest, CompressionEffort.Balanced, CompressionEffort.Fastest };
        var rows = new List<string> { "effort,tag,ms,bytes,sha16,is_avif" };
        var sigs = new List<string>();
        int rc = 0;
        var enc = EncoderFactory.Create(OutputFormat.AVIF);

        foreach (var e in efforts)
        {
            var s = new EncodingSettings
            {
                Format = OutputFormat.AVIF,
                Quality = 60,
                ColorSpaceTag = "System",
                Effort = e,
                // 保底关掉：三档里若某档编码失败退成 PNG，"产物互不相同"就判的是格式差而不是档位差。
                PngFallbackEnabled = false,
            };
            string path = Path.Combine(outDir, $"bench_avif_effort_{e}.avif");
            long ms = -1, bytes = -1;
            string sha = "";
            bool isAvif = false;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var sw = Stopwatch.StartNew();
                enc.EncodeSdrAsync(px, w, h, s, path, default).GetAwaiter().GetResult();
                sw.Stop();
                ms = sw.ElapsedMilliseconds;
                if (File.Exists(path))
                {
                    bytes = new FileInfo(path).Length;
                    var all = File.ReadAllBytes(path);
                    // 容器魔数自查：产物必须是 AVIF（前 12 字节里 "ftyp"）。
                    isAvif = all.Length > 12 && System.Text.Encoding.ASCII.GetString(all, 4, 4) == "ftyp";
                    sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(all))[..16];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {e} 编码失败：{ex.GetType().Name}: {Truncate(ex.Message, 90)}");
                rc = 1;
            }
            sigs.Add(sha);
            rows.Add($"{(int)e},{CompressionEffortMap.ToTag(e)},{ms},{bytes},{sha},{(isAvif ? "yes" : "NO")}");
            Console.WriteLine($"  {CompressionEffortMap.ToTag(e),-10} ms={ms,-6} bytes={bytes,-9} sha16={sha} avif={(isAvif ? "yes" : "NO")}");
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "avif_effort.csv"), string.Join("\n", rows) + "\n");

        if (rc == 0)
        {
            // 正控判据：三个非空签名必须两两不同；出现空签名 = 有档没产出，同样判红。
            bool allPresent = sigs.TrueForAll(x => x.Length > 0);
            int distinct = sigs.Distinct().Count();
            if (!allPresent || distinct != 3)
            {
                Console.WriteLine($"❌ 三档产物{(allPresent ? $"只有 {distinct}/3 种" : "有档没产出")} "
                    + "⇒「压缩强度」没落到这条腿（本轮修的就是这个形状）");
                rc = 1;
            }
            else Console.WriteLine("✅ 三档产物互不相同 ⇒ 档位真的进到本次所选后端");
        }
        return rc;
    }

    // ═══════════════════════════════════════
    //  批5 仪表 5：PNG IDAT 分块（流式）vs 整帧物化（旧形状）(--bench-png-idat)
    // ═══════════════════════════════════════
    /// <summary>
    /// 同一帧、同一档位、同一后端判定，两臂只差在【压缩字节怎么搬到文件里】：
    /// <list type="number">
    /// <item><c>streaming</c> —— 产品现状：ManagedPngEncoder.Encode 边压边写，凑满 64 KiB 落一条 IDAT。</item>
    /// <item><c>single-buffer</c> —— 改动前的形状：反射取同一份扫描线 → DeflateZlib.Compress 整帧压成
    /// 一个 byte[] → 本文件自带的一份"签名+IHDR+一条 IDAT+IEND"写出。参照臂保留的是旧**形状**
    /// （整帧物化 + 裁剪拷贝），不是重写一遍滤波公式（那只会把同一个 bug 抄两遍）。</item>
    /// </list>
    /// <para><b>为什么跑两档 effort 而不是一档</b>：流式只在【内置 zlib-ng 臂】上真正省内存 ——
    /// libdeflate 那边绑的是【一次性】API（libdeflate_zlib_compress，无流式入口），整帧数组无论如何都要
    /// 产出来。所以两臂的分配差按路由分开判：
    /// <c>Quick</c>(level 8) 恒走内置 ⇒ 内存正控在这一格里判；<c>Smallest</c>(level 9) 是本机产品默认档
    /// ⇒ 只判码流与 chunk 布局两条，内存列如实报数并注明路由（在 libdeflate 路由上判"流式更省"
    /// 会是判一个本就不存在的承诺）。</para>
    /// </summary>
    public static int RunPngIdat(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", 1280), h = GetIntOpt(args, "--h", 720);
        int reps = Math.Max(2, GetIntOpt(args, "--reps", 3));
        bool keep = Array.IndexOf(args, "--keep") >= 0;
        const int IdatPayload = 64 * 1024;   // 与 ManagedPngEncoder.IdatPayloadBytes 同值（下方 ② 用它验算）

        var efforts = new[] { CompressionEffort.Smallest, CompressionEffort.Quick };
        Console.WriteLine($"═══ [bench-png-idat] IDAT 分块 vs 整帧物化 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h} reps={reps} effort=[{string.Join(",", efforts)}] 分配口径=当前线程");

        var src = GenBgra(w, h);

        // 反射：private static byte[] BuildRawScanlines(byte[] bgra, int w, int h, int outDepth)
        var rawMi = typeof(ManagedPngEncoder).GetMethod("BuildRawScanlines",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]), typeof(int), typeof(int), typeof(int) }, modifiers: null)
            ?? throw new MissingMethodException(
                "ManagedPngEncoder.BuildRawScanlines(byte[],int,int,int) 签名变了（仪表按此反射）");

        var rows = new List<string>
        {
            "arm,effort,level,route_expected,ms_median,alloc_bytes_median,file_bytes,idat_chunks,stream_bytes",
        };
        // 每档 effort 的判据结果（①②③④ 在下面逐档收口）
        int cells = 0;
        bool anyLayoutDiscriminated = false;      // 至少有一格真的产出了多条 IDAT
        bool anyAllocWindowJudged = false;        // 至少有一格是"内存正控可判"的内置路由
        long allocDeltaBuiltin = long.MinValue;   // 内置路由上的 旧−新 分配差

        foreach (var effort in efforts)
        {
            int level = CompressionEffortMap.PngTiffDeflateLevel(effort);
            // 这一档产品会走哪条后端：level != 9 ⇒ 恒内置（分流只在 Smallest 生效）
            bool routeNative = level == 9 && DeflateZlib.IsAvailable
                && IsCandidatePublic(src, w, h, rawMi);

            // ── 预热：两臂各一次（JIT / 后端探测 / libdeflate 装载都不该算进中位）──
            string warm = Path.Combine(outDir, $"warm_idat_{Guid.NewGuid():N}.png");
            try
            {
                ManagedPngEncoder.Encode(src, w, h, warm, 8, null, null, effort);
                var warmRaw = (byte[])rawMi.Invoke(null, new object[] { src, w, h, 8 })!;
                WriteSingleIdatPng(warm, w, h, DeflateZlib.Compress(warmRaw, level));
            }
            finally { try { File.Delete(warm); } catch { } }

            var aTimes = new List<long>(); var aAlloc = new List<long>();
            var bTimes = new List<long>(); var bAlloc = new List<long>();
            byte[]? streamA = null, streamB = null;
            long bytesA = 0, bytesB = 0;
            int chunksA = 0, chunksB = 0;
            string pathA = Path.Combine(outDir, $"idat_streaming_{Guid.NewGuid():N}.png");
            string pathB = Path.Combine(outDir, $"idat_single_{Guid.NewGuid():N}.png");

            for (int i = 0; i < reps; i++)
            {
                // 臂 A：产品路径（流式分块）
                long g0 = GC.GetAllocatedBytesForCurrentThread();
                var sA = Stopwatch.StartNew();
                ManagedPngEncoder.Encode(src, w, h, pathA, 8, null, null, effort);
                sA.Stop();
                aAlloc.Add(GC.GetAllocatedBytesForCurrentThread() - g0);
                aTimes.Add(sA.ElapsedMilliseconds);
                var fileA = File.ReadAllBytes(pathA);
                bytesA = fileA.Length;
                (streamA, chunksA) = ReadIdat(fileA);
                File.Delete(pathA);

                // 臂 B：改动前的形状（整帧压成一个数组 + 一条 IDAT）
                long g1 = GC.GetAllocatedBytesForCurrentThread();
                var sB = Stopwatch.StartNew();
                var raw = (byte[])rawMi.Invoke(null, new object[] { src, w, h, 8 })!;
                var packed = DeflateZlib.Compress(raw, level);
                WriteSingleIdatPng(pathB, w, h, packed);
                sB.Stop();
                bAlloc.Add(GC.GetAllocatedBytesForCurrentThread() - g1);
                bTimes.Add(sB.ElapsedMilliseconds);
                var fileB = File.ReadAllBytes(pathB);
                bytesB = fileB.Length;
                (streamB, chunksB) = ReadIdat(fileB);
                GC.KeepAlive(packed);
                File.Delete(pathB);
            }

            long msA = Median(aTimes), msB = Median(bTimes);
            long allocA = Median(aAlloc), allocB = Median(bAlloc);
            long allocDelta = allocB - allocA;   // >0 ⇒ 流式臂确实少分配
            cells++;
            string route = routeNative ? "libdeflate" : "zlib-ng";

            Console.WriteLine($"  [{effort} lvl={level} route={route}]");
            Console.WriteLine($"    streaming     ms={msA,-6} file={bytesA,-9} idat_chunks={chunksA,-3} alloc={allocA} B");
            Console.WriteLine($"    single-buffer ms={msB,-6} file={bytesB,-9} idat_chunks={chunksB,-3} alloc={allocB} B");
            Console.WriteLine($"    分配差(旧−新)={allocDelta} B ≈ {FormatBytes(Math.Abs(allocDelta))}/次保存"
                + (routeNative ? "  ← 本轮该路由不落内存收益（libdeflate 无流式 API），只如实报数"
                               : "  ← 内置路由：这一格是内存正控的判点"));

            rows.Add($"streaming,{effort},{level},{route},{msA},{allocA},{bytesA},{chunksA},{streamA?.Length ?? -1}");
            rows.Add($"single-buffer,{effort},{level},{route},{msB},{allocB},{bytesB},{chunksB},{streamB?.Length ?? -1}");

            // ① 码流必须逐字节相同（分块只许动 chunk 边界）
            if (streamA is null || streamB is null || !streamA.AsSpan().SequenceEqual(streamB))
            {
                Console.WriteLine($"❌ [{effort}] 两臂拼接后的 zlib 码流不同（A={streamA?.Length ?? -1}B "
                                  + $"B={streamB?.Length ?? -1}B）—— 分块改到了压缩输出，本轮承诺被破");
                WriteCsv(outDir, "bench_png_idat", rows);
                return 1;
            }
            // ② 只有 chunk 布局能差：A.file == B.file + 12×(A.chunks−1)
            long expectedA = bytesB + 12L * (chunksA - 1);
            if (bytesA != expectedA)
            {
                Console.WriteLine($"❌ [{effort}] 产物大小与 chunk 条数对不上账：A={bytesA} 预期={expectedA}"
                                  + $"（B={bytesB}, A 条数={chunksA}）—— 差的不是分块");
                WriteCsv(outDir, "bench_png_idat", rows);
                return 1;
            }
            if (chunksB != 1)
            {
                Console.WriteLine($"❌ [{effort}] 参照臂本应恰好一条 IDAT，实得 {chunksB} 条 —— 参照臂写坏了，"
                    + "对照失效");
                WriteCsv(outDir, "bench_png_idat", rows);
                return 1;
            }
            // ②b 载荷尺寸就是那个常数：条数必须恰好等于 ⌈码流/64 KiB⌉（多了/少了都是"边界算错"）
            if (streamA is not null)
            {
                int expectedChunks = (streamA.Length + IdatPayload - 1) / IdatPayload;
                if (chunksA != expectedChunks)
                {
                    Console.WriteLine($"❌ [{effort}] 条数与载荷界不符：实得 {chunksA} 条，按 64 KiB 载荷应为 "
                        + $"{expectedChunks} 条（码流 {streamA.Length} B）");
                    WriteCsv(outDir, "bench_png_idat", rows);
                    return 1;
                }
            }
            if (chunksA > 1) anyLayoutDiscriminated = true;

            // ③ 内存正控只在【内置路由】那一格判（另一格的收益不存在，判它是判一个没承诺过的东西）
            if (!routeNative)
            {
                anyAllocWindowJudged = true;
                allocDeltaBuiltin = allocDelta;
                if (allocDelta == 0)
                {
                    Console.WriteLine($"❌ non-discriminating:alloc —— 内置路由（{effort}/level {level}）上两臂中位"
                        + "分配字节【完全相等】：分块没有省下任何中间缓冲，仪表什么都没量到，本次不判");
                    WriteCsv(outDir, "bench_png_idat", rows);
                    return 1;
                }
                if (allocDelta < 0)
                {
                    Console.WriteLine($"❌ non-discriminating:alloc 方向 —— 内置路由上流式臂反而多分配 "
                        + $"{Math.Abs(allocDelta)} B，本轮改动在那条臂上是退步");
                    WriteCsv(outDir, "bench_png_idat", rows);
                    return 1;
                }
            }

            if (keep)
            {
                ManagedPngEncoder.Encode(src, w, h,
                    Path.Combine(outDir, $"keep_png_streaming_{w}x{h}_{effort}.png"), 8, null, null, effort);
                var rawK = (byte[])rawMi.Invoke(null, new object[] { src, w, h, 8 })!;
                WriteSingleIdatPng(Path.Combine(outDir, $"keep_png_single_{w}x{h}_{effort}.png"), w, h,
                    DeflateZlib.Compress(rawK, level));
                File.WriteAllBytes(Path.Combine(outDir, $"keep_png_{w}x{h}_src.bgra"), src);
            }
        }

        var csvPath = WriteCsv(outDir, "bench_png_idat", rows);
        Console.WriteLine($"CSV → {csvPath}");
        if (keep)
            Console.WriteLine($"  --keep → keep_png_streaming_/keep_png_single__{w}x{h}_{string.Join("/", efforts)}.png "
                              + $"+ keep_png_{w}x{h}_src.bgra（外部裁判用）");

        // ④ fail-closed：一格都没跑到 ⇒ 仪表没判任何东西
        if (cells == 0)
        {
            Console.WriteLine("❌ 枚举到 0 个格子 —— 仪表什么都没跑");
            return 1;
        }
        // ⑤ 正控（布局）：两档 effort 里至少一格真正产出多条 IDAT，否则"分块"这件事无从判起
        if (!anyLayoutDiscriminated)
        {
            Console.WriteLine("❌ non-discriminating:layout —— 所有档的码流都 ≤64 KiB（只有 1 条 IDAT），"
                + "两臂 chunk 布局一致 ⇒ 请把 --w/--h 调大或换更大内容");
            return 1;
        }
        // ⑥ 正控（内存）判点存在性：至少一格落在内置路由上，内存收益才有得判
        if (!anyAllocWindowJudged)
        {
            Console.WriteLine("❌ non-discriminating:alloc 判点缺失 —— 没有任何一格走内置路由，"
                + "本表无法对'流式省下整帧缓冲'负责（Quick 档恒走内置，出现这一条说明分流判据改了口径）");
            return 1;
        }
        Console.WriteLine($"✅ bench-png-idat 通过（两档码流逐字节相同；内置路由每次保存少分配 "
                          + $"{FormatBytes(allocDeltaBuiltin)}）");
        Console.WriteLine("   外部裁判（容器验收，必须跑）：py -c \"from PIL import Image;"
            + "a=Image.open(r'<out>\\keep_png_streaming_WxH_Smallest.png').convert('RGBA');"
            + "b=Image.open(r'<out>\\keep_png_single_WxH_Smallest.png').convert('RGBA');"
            + "print(a.size==b.size, a.tobytes()==b.tobytes())\"   ← 必须 True（传 Windows 路径，勿用 /c/…）");
        return 0;
    }

    /// <summary>这一帧会不会被分流判据派给 libdeflate（问的就是产品那把尺：internal
    /// IsLibDeflateCandidate(byte[]) 作用在【已滤波扫描线】上，不是原始 BGRA）。</summary>
    private static bool IsCandidatePublic(byte[] src, int w, int h, MethodInfo rawMi)
    {
        var candMi = typeof(DeflateZlib).GetMethod("IsLibDeflateCandidate",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]) }, modifiers: null)
            ?? throw new MissingMethodException(
                "DeflateZlib.IsLibDeflateCandidate(byte[]) 签名变了（仪表按此反射）");
        var raw = (byte[])rawMi.Invoke(null, new object[] { src, w, h, 8 })!;
        return (bool)candMi.Invoke(null, new object[] { raw })!;
    }

    /// <summary>解析 PNG：把所有 IDAT chunk 的数据首尾相接（PNG 3.5.4：IDAT 可以任意多条，
    /// 拼接后才是完整 zlib 流），并返回条数。非 IDAT 只跳不取。</summary>
    private static (byte[]? Stream, int Chunks) ReadIdat(byte[] png)
    {
        using var ms = new MemoryStream();
        int chunks = 0;
        for (int i = 8; i + 12 <= png.Length;)
        {
            int len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
            string type = Encoding.ASCII.GetString(png, i + 4, 4);
            if (len < 0 || i + 12 + len > png.Length) return (null, chunks);
            if (type == "IDAT") { ms.Write(png, i + 8, len); chunks++; }
            if (type == "IEND") break;
            i += 12 + len;
        }
        return (ms.ToArray(), chunks);
    }

    /// <summary>参照臂的写出器：改动前的形状 —— 一整块压缩字节 + 一条 IDAT。
    /// chunk/CRC 这一段是 bench 自带的第二份实现，它的<b>合法性不在本仪表的判据里</b>
    /// （容器必须由外部解码器裁判）；本仪表只用它做"分配/大小"的对照形状。</summary>
    private static void WriteSingleIdatPng(string path, int w, int h, byte[] idat)
    {
        using var fs = File.Create(path, 1 << 20);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        WriteChunkLocal(fs, "IHDR"u8, ihdr);
        WriteChunkLocal(fs, "IDAT"u8, idat);
        WriteChunkLocal(fs, "IEND"u8, []);
    }

    private static void WriteChunkLocal(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc32Local(type, data));
        output.Write(crcBuf);
    }

    private static readonly uint[] CrcTableLocal = BuildCrcTableLocal();

    private static uint[] BuildCrcTableLocal()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32Local(ReadOnlySpan<byte> type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = CrcTableLocal[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTableLocal[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    // ═══════════════════════════════════════
    //  批5 仪表 6：高高光 PQ 夹具的 deflate 后端分流 (--bench-hdr-routing)
    // ═══════════════════════════════════════
    /// <summary>
    /// <para><b>⚠ 夹具是合成的 PQ 数据，不是真屏幕截图</b>（CSV/report 两列 label 与 real_screen_capture
    /// 就是这个意思）。它回答的是一个具体问题：<b>高光远在纸白之上的 16-bit PQ 内容，落在
    /// 「几乎不可压缩的 UI」与「真实照片」之间哪个区，因而被分流判据（1 MiB 内置 level-1 压缩比
    /// ≥ LibDeflateMinRatio）派给哪个后端？</b> 这条判据今天只有 UI(0.0123) / 真 HDR(0.3208) /
    /// 噪声(0.8369) 三个读数，高高光 PQ 一格从没量过。</para>
    /// <para>夹具构造：scRGB 线性浮点（nits = 50 … 2000 的竖向渐变 + 一块 4000 nits 的高光团 +
    /// 轻微噪声）→ 产品自己的 <c>FormatHelper.HdrToPq16</c>（BT2020，10-bit 左对齐）→
    /// <c>Rgba16ToBgra16Bytes</c> → Encode16 的输入约定。走的是产品 HDR PNG 腿同一串调用。</para>
    /// <para>三臂：<c>forced-builtin</c> = 反射 <c>CompressWithDecision(..., useNative:false)</c>；
    /// <c>forced-libdeflate</c> = 同一入口 <c>useNative:true</c>；<c>auto-route</c> = 产品
    /// <c>ManagedPngEncoder.Encode16</c>（整帧判一次）。两臂强制腿各自给出 bytes + ms。</para>
    /// <para><b>判据</b>：① 没有可用 deflate.dll ⇒ 强制 libdeflate 那条前提不成立 → SKIP(3)，
    /// 明确跳过而不是静默通过。② <b>正控</b>：两臂强制产物必须<b>不同</b>——相同即"换了后端而产物没换"，
    /// 分流是空跑，<c>non-discriminating</c> 退 1。③ 自动腿的产物字节必须恰好等于两强制臂之一，
    /// 且该臂必须由探针比值<b>预言得出</b>（比值 ≥ 阈值 ⇒ 等于 libdeflate 臂，否则等于 builtin 臂）：
    /// 对不上就是"整帧判一次"与"实际压缩走了哪条"分叉。</para>
    /// <para>④ 高高光夹具的探针比值同时写进 CSV 与 stdout —— 那一格才是本轮真正新增的读数。</para>
    /// </summary>
    public static int RunHdrRouting(string[] args)
    {
        string outDir = ResolveOut(args);
        int w = GetIntOpt(args, "--w", 960), h = GetIntOpt(args, "--h", 640);
        int bitDepth = GetIntOpt(args, "--bitdepth", 10);
        int reps = Math.Max(2, GetIntOpt(args, "--reps", 2));
        bool keep = Array.IndexOf(args, "--keep") >= 0;
        int level = CompressionEffortMap.PngTiffDeflateLevel(CompressionEffort.Smallest);

        Console.WriteLine($"═══ [bench-hdr-routing] 合成高高光 PQ 夹具 × deflate 后端分流 ═══ out={outDir}");
        Console.WriteLine($"size={w}x{h} bitDepth={bitDepth} reps={reps} level={level}");
        Console.WriteLine("⚠ 夹具 = 合成 PQ 渐变 + 高光团，【不是真屏幕截图】（CSV 的 fixture 列钉这一句）");

        // 反射：internal static byte[] CompressWithDecision(byte[], int, bool)
        //        internal static bool IsLibDeflateCandidate(byte[])
        var withDecision = typeof(DeflateZlib).GetMethod("CompressWithDecision",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]), typeof(int), typeof(bool) }, modifiers: null)
            ?? throw new MissingMethodException(
                "DeflateZlib.CompressWithDecision(byte[],int,bool) 签名变了（仪表按此反射）");
        var candidate = typeof(DeflateZlib).GetMethod("IsLibDeflateCandidate",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]) }, modifiers: null)
            ?? throw new MissingMethodException(
                "DeflateZlib.IsLibDeflateCandidate(byte[]) 签名变了（仪表按此反射）");
        var raw16Mi = typeof(ManagedPngEncoder).GetMethod("BuildRawScanlines16",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(byte[]), typeof(int), typeof(int), typeof(int) }, modifiers: null)
            ?? throw new MissingMethodException(
                "ManagedPngEncoder.BuildRawScanlines16(byte[],int,int,int) 签名变了（仪表按此反射）");

        var bgra16 = BuildSyntheticPq16(w, h, bitDepth);
        var raw = (byte[])raw16Mi.Invoke(null, new object[] { bgra16, w, h, bitDepth })!;
        bool routedNative = (bool)candidate.Invoke(null, new object[] { raw })!;

        if (!DeflateZlib.IsAvailable)
        {
            Console.WriteLine("⏭ SKIP —— 本进程没有可用 deflate.dll（DeflateZlib.IsAvailable=false）："
                + "强制 libdeflate 那条臂没有前提，只量内置一臂不判分流（退出码 3，不静默通过）");
            var only = new List<string>
            {
                "arm,fixture,real_screen_capture,ms_median,bytes,sha16,probe_routes_native,backend_level",
                $"SKIP_deflate_dll_missing,synthetic_pq_highlight,no,-1,-1,-1,{routedNative},{level}",
                $"probe_only,synthetic_pq_highlight,no,-1,-1,-1,{routedNative},{level}",
            };
            Console.WriteLine($"CSV → {WriteCsv(outDir, "bench_hdr_routing", only)}");
            return 3;
        }

        var rows = new List<string>
        {
            "arm,fixture,real_screen_capture,ms_median,bytes,sha16,probe_ratio_routes_native,backend_level",
        };
        long msBuiltin = 0, msNative = 0, bytesBuiltin = 0, bytesNative = 0;
        string shaBuiltin = "", shaNative = "";
        byte[] pBuiltin, pNative;   // 先声明再走解构赋值（C# 的解构式里不能混"已存在"与"新声明"）

        (long ms, byte[] packed) RunArm(bool useNative)
        {
            var times = new List<long>();
            byte[] last = [];
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                last = (byte[])withDecision.Invoke(null, new object[] { raw, level, useNative })!;
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
            }
            return (Median(times), last);
        }

        // 预热两臂各一次（libdeflate 首调有分配 compressor 的一次性成本，不该算进中位）
        _ = RunArm(false); _ = RunArm(true);
        (msBuiltin, pBuiltin) = RunArm(false);
        (msNative, pNative) = RunArm(true);
        bytesBuiltin = pBuiltin.Length; bytesNative = pNative.Length;
        shaBuiltin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pBuiltin))[..16];
        shaNative = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pNative))[..16];

        // 自动腿：产品整条 Encode16（整帧判一次后端）—— 最后一次的产物就地取回它的 IDAT 码流，
        // 不再"事后再编一遍对照"（那样判的是两次运行是否一致，而不是这次走了哪条）。
        long msAuto = 0; long bytesAuto = 0; string shaAuto = "";
        byte[]? autoStream = null;
        string autoPath = Path.Combine(outDir, $"hdr_route_auto_{Guid.NewGuid():N}.png");
        try
        {
            var times = new List<long>();
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                ManagedPngEncoder.Encode16(bgra16, w, h, autoPath, cicp: null, iccProfile: null,
                    bitDepth: bitDepth, fastest: false, effort: CompressionEffort.Smallest);
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
            }
            msAuto = Median(times);
            var all = File.ReadAllBytes(autoPath);
            bytesAuto = all.Length;
            shaAuto = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(all))[..16];
            autoStream = ReadIdat(all).Stream;
            if (keep)
                File.Copy(autoPath, Path.Combine(outDir, $"keep_hdr_pq_auto_{w}x{h}_{bitDepth}bit.png"),
                    overwrite: true);
        }
        finally { try { if (File.Exists(autoPath)) File.Delete(autoPath); } catch { } }

        rows.Add($"forced-builtin,synthetic_pq_highlight,no,{msBuiltin},{bytesBuiltin},{shaBuiltin},{routedNative},{level}/9");
        rows.Add($"forced-libdeflate,synthetic_pq_highlight,no,{msNative},{bytesNative},{shaNative},{routedNative},10");
        rows.Add($"auto-route,synthetic_pq_highlight,no,{msAuto},{bytesAuto},{shaAuto},{routedNative},"
            + $"{(routedNative ? "libdeflate" : "zlib-ng")}");
        rows.Add("probe,synthetic_pq_highlight,no,-,-,-," + routedNative
            + ",ratio_is_level1_sample_only_no_codeword");

        Console.WriteLine($"  forced-builtin     ms={msBuiltin,-6} bytes={bytesBuiltin,-9} sha16={shaBuiltin}");
        Console.WriteLine($"  forced-libdeflate  ms={msNative,-6} bytes={bytesNative,-9} sha16={shaNative}");
        Console.WriteLine($"  auto-route(产品)   ms={msAuto,-6} file={bytesAuto,-9} sha16={shaAuto}");
        Console.WriteLine($"  探针（内置 level-1 / 1 MiB 采样）判为可外包 = {routedNative}");
        if (keep)
            File.WriteAllBytes(Path.Combine(outDir, $"keep_hdr_pq_raw_{w}x{h}_{bitDepth}bit.bin"), raw);

        var csv = WriteCsv(outDir, "bench_hdr_routing", rows);
        Console.WriteLine($"CSV → {csv}");
        if (keep)
            Console.WriteLine("   外部裁判（容器验收）：py -c \"from PIL import Image;"
                + "i=Image.open(r'<out>\\keep_hdr_pq_auto_WxH_10bit.png');print(i.mode,i.size,i.info.keys())\""
                + "（Windows 路径）—— sBIT/cICP 与 10-bit 左对齐要由外部读端确认，本仪表不自证。");

        // ── 判据 ──
        if (pBuiltin.AsSpan().SequenceEqual(pNative))
        {
            Console.WriteLine("❌ non-discriminating:backend —— 强制两臂产物逐字节相同，"
                + "换后端没换压缩输出 ⇒ 分流这条腿是空跑，本次不判");
            return 1;
        }
        // 解压等价：两条臂必须都能解回同一份扫描线（"承诺=解压后逐字节相同"，与容器无关）
        foreach (var (label, packed) in new[] { ("builtin", pBuiltin), ("libdeflate", pNative) })
        {
            using var zin = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var back = new MemoryStream();
            zin.CopyTo(back);
            if (!back.GetBuffer().AsSpan(0, (int)back.Length).SequenceEqual(raw))
            {
                Console.WriteLine($"❌ {label} 臂解压后与输入扫描线不同 —— 后端产物不再等价，不只是"
                    + "慢一点/大一点的问题");
                return 1;
            }
        }
        // 自动腿必须由探针预言：它的 IDAT 码流必须【逐字节等于】被探针选中的那一臂
        byte[] expected = routedNative ? pNative : pBuiltin;
        if (autoStream is null || !autoStream.AsSpan().SequenceEqual(expected))
        {
            Console.WriteLine($"❌ 自动腿的实际压缩字节与【探针预言的后端】不一致："
                + $"探针判 {(routedNative ? "libdeflate" : "zlib-ng")}，实得 {autoStream?.Length ?? -1} B 而两臂分别是 "
                + $"{pBuiltin.Length}/{pNative.Length} B —— 「整帧判一次」与「真正走了哪条」分叉了，"
                + "于是日志/界面上写的路由是假的");
            return 1;
        }
        Console.WriteLine($"✅ bench-hdr-routing 通过（两臂产物不同且解压等价；自动腿 = 探针所选的 "
            + $"{(routedNative ? "libdeflate" : "zlib-ng")} 臂）");
        return 0;
    }

    /// <summary>合成一份"高光远在纸白之上"的 16-bit PQ 帧（Encode16 的输入约定：B16 G16 R16 A16 大端）。
    /// <para>口径：本机面板实测纸白 ≈ 100 nits、峰值 1000+ nits ⇒ 夹具的绝大部分像素落在
    /// 50…2000 nits，另有一块 ~4000 nits 的高光团，全部高于纸白；nits→scRGB 线性 = nits/80，
    /// PQ 编码交给产品自己的 HdrToPq16（BT2020 + 目标位深左对齐），本函数只造"场景亮度"。</para>
    /// <para>⚠ 这是<b>合成夹具</b>：它量的是"高亮度高光内容的可压缩性形状"，不是任何一张真截图，
    /// 因此它的比值/体积不许写进面向用户的结论。噪声用固定种子 ⇒ 每次跑完全一致。</para></summary>
    private static byte[] BuildSyntheticPq16(int w, int h, int bitDepth)
    {
        var hdr = new float[w * h * 4];
        var rng = new Random(20261031);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 竖向渐变 50 → 2000 nits + 中央一块 ~4000 nits 的高光团 + 轻微噪声
                double nits = 50.0 + 1950.0 * y / Math.Max(1, h - 1);
                double dx = x - w / 2.0, dy = y - h / 3.0;
                double r2 = (dx * dx + dy * dy) / Math.Max(1.0, (w * 0.12) * (h * 0.12));
                if (r2 < 1.0) nits += 2000.0 * (1.0 - r2);
                nits *= 1.0 + 0.002 * (rng.NextDouble() - 0.5);
                double lin = Math.Clamp(nits / 80.0, 0.0, 100000.0); // scRGB 线性（80 nits = 1.0）
                // 三通道按同一亮度略作分离（保留 BT2020 转换要处理的颜色结构）
                int i = (y * w + x) * 4;
                hdr[i] = (float)(lin * 0.92);          // R
                hdr[i + 1] = (float)(lin * 1.00);      // G
                hdr[i + 2] = (float)(lin * 1.06);      // B
                hdr[i + 3] = 1.0f;                     // A
            }
        }
        var frame = new HdrFrameData { Pixels = hdr, Width = w, Height = h };
        var pq16 = FormatHelper.HdrToPq16(frame, "BT2020", bitDepth);
        return FormatHelper.Rgba16ToBgra16Bytes(pq16, w, h);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  仪表 7：--bench-deflate-cap  deflate 缓冲初值 A/B（2026-10-31 裁定 2：让数据说话）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 初值四问：改它会不会改产物 / 各可压缩性上谁省 / 历史型初值（EMA、记住上一帧）相对
    /// "只看这一次输入"的静态估算到底赢在哪 / 这些差值在【交替内容】上成不成立。
    /// <para><b>为什么还要这台</b>：本批把产品里那份 <c>static</c> EMA 比例删了（理由写在
    /// <c>DeflateZlib.EstimatedCapacity</c> 注释里：分配尺寸依赖调用历史，且 AVIF 保底→PNG 那条腿
    /// 与用户真正要的 PNG 保存写的是同一个 static）。裁定要求"不改回来，用测试验证…固定容量 vs
    /// EMA 容量，让数据说话"⇒ EMA 臂【只活在仪表进程里】，产品里没有这份状态；它若真有收益，
    /// 收益多少在这里量出来，而不是靠注释定。</para>
    /// <para><b>六臂</b>：head（改动前形状：<c>new MemoryStream()</c> 无初值 + 末尾 ToArray）、
    /// fixed64k（永远钳到下限 = "干脆不估"）、est（出货形状：反射调产品自己的 EstimatedCapacity）、
    /// ema（α=1/4 跟新值，种子 0.25，照被删代码的更新式）、oracle（把【第一帧】的产物长度当永久
    /// 初值 = 历史型估计能达到的最好情况；E1 单形状那一格就是"完美估计"）、
    /// product（直接调 <see cref="DeflateZlib.CompressBuiltin"/>：证明本仪表的复刻臂就是产品那条路，
    /// 不是另一份实现）。</para>
    /// <para><b>读数一律按形状分列</b>：跨形状取中位会把"哪一档内容赢"糊成一个数（第一跑就糊过：
    /// 交替序列的中位落在 ui 那一档，于是 'est 比 head 多分配' 其实是 ui 档的事实，被读成了全局结论）。
    /// 现在 E2 每个形状各出一行中位，另外单列一次序列【总分配】——EMA 的代价正是要在整段历史上才看得见。</para>
    /// <para><b>四条判据</b>：① 夹具自检 —— ui/photo/noise 三档的实测压缩比必须分别落在
    /// [0,0.05] / [0.10,0.40] / [0.80,2.0]，摊不开就只有两档 ⇒ 先红（"任何单一常数都会在一端错"
    /// 这句话的依据就是这三档同时存在）。② 容量【不进码流】—— 同一输入、五种显式初值（含故意给 8 B
    /// 这种荒谬值）与 E1 六臂产物必须【逐字节相同】；不成立 ⇒ 下面所有"容量只影响成本"的比较都在判
    /// 一个假前提。③ 判别性 —— 至少一个形状上六臂的中位分配【不许全等】，全等 = 仪表什么都没量到
    /// （<c>non-discriminating:alloc</c>，退 1）。④ E2 只报差值与赢家，【不预设方向】：出货是 est，
    /// 哪一格赢家不是 est，那格的差值就是"历史型/固定型初值到底值多少"的数。</para>
    /// <para><b>尺寸口径</b>：这条 byte[] 路径今天的真实消费者是 TIFF 分条（4K 16-bit ≈1 MiB/条）
    /// 与探针样本（恰 1 MiB）；4K 整帧已改走 <c>CompressTo</c> 流式 ⇒ 本表量的是【1 MiB 级一次压缩】
    /// 的成本差，不许读成"每次保存省了多少"（那一格由 <c>--bench-png-idat</c> 判）。</para>
    /// </summary>
    public static int RunDeflateCapacity(string[] args)
    {
        string outDir = ResolveOut(args);
        int stripKiB = Math.Max(64, GetIntOpt(args, "--strip-kib", 1024));
        int reps = Math.Max(3, GetIntOpt(args, "--reps", 3));
        int cycles = Math.Max(2, GetIntOpt(args, "--cycles", 4));
        int level = CompressionEffortMap.PngTiffDeflateLevel(CompressionEffort.Smallest);
        int len = stripKiB * 1024;

        var estMi = typeof(DeflateZlib).GetMethod("EstimatedCapacity",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            new[] { typeof(int) }, modifiers: null)
            ?? throw new MissingMethodException(
                "DeflateZlib.EstimatedCapacity(int) 签名变了或已被删（仪表按此反射，不许静默退化成少一臂）");

        Console.WriteLine($"═══ [bench-deflate-cap] deflate 缓冲初值 A/B ═══ out={outDir}");
        Console.WriteLine($"level={level} 单次输入={stripKiB} KiB  E1: 每臂 {reps} 次可判(+1 预热)  "
            + $"E2: ui→photo→noise 循环 {cycles} 遍/臂（前 3 次预热）");
        Console.WriteLine("⚠ 口径：这条 byte[] 路径 = TIFF 分条 + 探针样本；4K 整帧走流式，不在本表");

        var shapes = new (string Kind, byte[] Data)[]
        {
            ("ui", CapFixture("ui", len)),
            ("photo", CapFixture("photo", len)),
            ("noise", CapFixture("noise", len)),
        };
        string[] arms = ["head", "fixed64k", "est", "ema", "oracle", "product"];
        var rows = new List<string>
        {
            "exp,shape,arm,cap_median,us_median,alloc_median,alloc_total,packed_bytes,ratio_permille",
        };

        // ── 判据①夹具自检：三档必须真摊在三个量级上，否则这张表只有两档，结论的适用面会吹大 ──
        var bands = new (double Lo, double Hi)[] { (0.0, 0.05), (0.10, 0.40), (0.80, 2.0) };
        for (int i = 0; i < shapes.Length; i++)
        {
            double ratio = (double)DeflateZlib.CompressBuiltin(shapes[i].Data, level).Length / len;
            if (ratio < bands[i].Lo || ratio > bands[i].Hi)
            {
                Console.WriteLine($"❌ 夹具 {shapes[i].Kind} 实测压缩比 = {ratio:F4}，不在 "
                    + $"[{bands[i].Lo}, {bands[i].Hi}] ⇒ 三档可压缩性没摊开（本表会退化成两档），先修夹具再谈读数");
                return 1;
            }
            Console.WriteLine($"  夹具自检 {shapes[i].Kind,-6} ratio={ratio:F4} ∈ [{bands[i].Lo}, {bands[i].Hi}] ✅");
        }

        // ── E0 正控：容量不进码流。显式初值含 8 B（荒谬值）与硬膨胀上界，产物必须逐字节等于 product 臂 ──
        int ctrlLen = 128 * 1024;
        int estCtrl = (int)estMi.Invoke(null, new object[] { ctrlLen })!;
        foreach (string kind in new[] { "ui", "noise" })
        {
            byte[] d = CapFixture(kind, ctrlLen);
            byte[] refBytes = DeflateZlib.CompressBuiltin(d, level);
            foreach (int c in new[] { 8, 256, 64 * 1024, estCtrl, DeflateBound(ctrlLen) })
            {
                var packed = CompressWithCap(d, level, c);
                bool same = packed.AsSpan().SequenceEqual(refBytes);
                int permille = (int)((long)packed.Length * 1000 / ctrlLen);
                rows.Add($"E0,{kind},cap={c},-1,-1,-1,-1,{packed.Length},{permille}");
                Console.WriteLine($"  E0 {kind,-6} cap={c,-8} bytes={packed.Length,-8} 与 product 臂逐字节相同={same}");
                if (!same)
                {
                    Console.WriteLine($"❌ 容量【进了码流】：{kind} 上 cap={c} 的产物与 DeflateZlib.CompressBuiltin "
                        + $"不同（{packed.Length} vs {refBytes.Length} B）—— 那么 E1/E2 判的就不是成本差，"
                        + "而是两份不同的压缩结果；本次不判，且产品的'容量只影响缓冲'注释是错的");
                    WriteCsv(outDir, "bench_deflate_cap", rows);
                    return 1;
                }
            }
        }

        // 一臂跑完一串输入。返回【逐次】记录（预热的那几次只更新状态、不进读数）， EMA / oracle 的状态
        // 只活在这里：臂与臂互不影响，每条实验开始时都是新鲜种子。
        List<(string Kind, long Us, long Alloc, int Cap)> Measure(
            string arm, List<(string Kind, byte[] Data)> seq, int warm, out byte[] lastPacked)
        {
            int emaPermille = 250;   // 种子 = 0.25，与出货静态估算同比例（对 EMA 最有利的一种种子）
            int oracleLen = -1;
            var recs = new List<(string Kind, long Us, long Alloc, int Cap)>();
            byte[] last = [];
            for (int n = 0; n < seq.Count; n++)
            {
                var (kind, data) = seq[n];
                int estCap = (int)estMi.Invoke(null, new object[] { data.Length })!;
                int cap = arm switch
                {
                    "head" => -1,
                    "fixed64k" => 64 * 1024,
                    "est" or "product" => estCap,
                    "ema" => (int)Math.Min(DeflateBound(data.Length),
                        Math.Max(256L, (long)data.Length * emaPermille / 1000)),
                    "oracle" => oracleLen > 0
                        ? (int)Math.Min(DeflateBound(data.Length), (long)oracleLen)
                        : 64 * 1024,
                    _ => throw new ArgumentException("未知臂 " + arm),
                };
                long g0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                last = arm switch
                {
                    "product" => DeflateZlib.CompressBuiltin(data, level),
                    "head" => CompressHeadShape(data, level),
                    _ => CompressWithCap(data, level, cap),
                };
                long elapsedUs = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - g0;
                if (n >= warm) recs.Add((kind, elapsedUs, allocated, cap));
                if (arm == "ema")
                    emaPermille = (emaPermille * 3 + (int)((long)last.Length * 1000 / data.Length)) / 4;
                if (arm == "oracle" && oracleLen <= 0) oracleLen = last.Length;
            }
            lastPacked = last;
            if (recs.Count == 0)
                throw new InvalidOperationException($"臂 {arm} 的预热吃掉了全部调用 ⇒ 一次都没量到");
            return recs;
        }

        static (long Us, long Alloc, long Cap, long AllocTotal) Summarize(
            List<(string Kind, long Us, long Alloc, int Cap)> recs)
            => (Median(recs.Select(r => r.Us).ToList()),
                Median(recs.Select(r => r.Alloc).ToList()),
                Median(recs.Select(r => (long)r.Cap).ToList()),
                recs.Sum(r => r.Alloc));

        // ── E1 单一形状：六臂各跑 reps 次；兼作判据②（六臂产物逐字节相同）与③（分配可判） ──
        bool anyAllocSpread = false;
        foreach (var (kind, data) in shapes)
        {
            var allocList = new List<long>();
            var packedByArm = new Dictionary<string, byte[]>();
            foreach (string arm in arms)
            {
                var recs = Measure(arm, Enumerable.Repeat((kind, data), reps + 1).ToList(), 1, out var packed);
                packedByArm[arm] = packed;
                var s = Summarize(recs);
                allocList.Add(s.Alloc);
                int permille = (int)((long)packed.Length * 1000 / data.Length);
                rows.Add($"E1,{kind},{arm},{s.Cap},{s.Us},{s.Alloc},{s.AllocTotal},{packed.Length},{permille}");
                Console.WriteLine($"  E1 {kind,-6} arm={arm,-9} cap={s.Cap,-8} us_med={s.Us,-7} "
                    + $"alloc_med={s.Alloc,-9} bytes={packed.Length,-8} ratio={permille / 1000.0:F4}");
            }
            string? diverged = packedByArm
                .Where(kv => !kv.Value.AsSpan().SequenceEqual(packedByArm["product"]))
                .Select(kv => kv.Key).FirstOrDefault();
            if (diverged is not null)
            {
                Console.WriteLine($"❌ E1 {kind}：{diverged} 臂的产物与 product 臂不同 ⇒ 复刻臂不是产品那条路，"
                    + "下面所有对比判的是两份实现，不作数");
                WriteCsv(outDir, "bench_deflate_cap", rows);
                return 1;
            }
            long spread = allocList.Max() - allocList.Min();
            if (spread > 0) anyAllocSpread = true;
            Console.WriteLine($"    → {kind}：六臂产物逐字节相同 ✅；中位分配极差 = {spread} B "
                + (spread > 0 ? "（可判）" : "（【全等】这一格仪表没量到东西）"));
        }

        // ── E2 交替内容：历史型初值（ema / oracle）vs 只看本次输入的 est —— 读数【按形状分列】 ──
        var alt = new List<(string Kind, byte[] Data)>();
        for (int i = 0; i < cycles * shapes.Length; i++) alt.Add(shapes[i % shapes.Length]);
        int measuredPerShape = alt.Count / shapes.Length - 1;   // 每形状预热一次
        if (measuredPerShape < 2)
        {
            Console.WriteLine($"❌ --cycles={cycles}：每形状只剩 {measuredPerShape} 次可判 ⇒ EMA 的「跟」"
                + "根本没被跑到，本次不判");
            return 1;
        }
        Console.WriteLine($"  E2 交替序列 = {string.Join("→", shapes.Select(s => s.Kind))} × {cycles} "
            + $"（每形状预热 1 次 ⇒ 每形状 {measuredPerShape} 次可判）");
        var e2 = new Dictionary<string, Dictionary<string, (long Us, long Alloc, long Cap, long Total)>>();
        foreach (string arm in arms)
        {
            var recs = Measure(arm, alt, shapes.Length, out _);
            var perShape = shapes.ToDictionary(s => s.Kind, s => Summarize(recs.Where(r => r.Kind == s.Kind).ToList()));
            e2[arm] = perShape;
            long total = recs.Sum(r => r.Alloc);
            foreach (var (k2, s) in perShape)
                rows.Add($"E2,{k2},{arm},{s.Cap},{s.Us},{s.Alloc},{s.AllocTotal},-1,-1");
            rows.Add($"E2,ALL,{arm},-1,-1,-1,{total},-1,-1");
            Console.WriteLine($"  E2 arm={arm,-9} 序列总分配={total}  "
                + string.Join("  ", perShape.Select(kv => $"{kv.Key}:cap={kv.Value.Cap}/us={kv.Value.Us}/alloc={kv.Value.Alloc}")));
        }
        // 差值一律按"出货(est) − 对照"报：正数 = 对照更便宜 ⇒ 那条对照值得重新考虑；负数 = 出货已经更好。
        foreach (var (k2, _) in shapes)
        {
            Console.WriteLine($"    E2 {k2}：");
            foreach (string other in new[] { "head", "fixed64k", "ema", "oracle" })
            {
                long dAlloc = e2["est"][k2].Alloc - e2[other][k2].Alloc;
                long dUs = e2["est"][k2].Us - e2[other][k2].Us;
                Console.WriteLine($"      est − {other,-9} = 中位分配 {dAlloc,11} B，中位耗时 {dUs,8} µs"
                    + (dAlloc > 0 ? "  ← 对照更省内存" : dAlloc < 0 ? "  ← 出货更省内存" : "  ← 内存打平"));
            }
        }
        // est 与 product 走的是【同一条件、同一代码路径】（product 调 CompressBuiltin，est 复刻它）⇒
        // 两者之差就是本表耗时列的噪声下限；耗时差没超过它就不许被读成"容量换了时间"。
        long noiseUs = shapes.Max(sh => Math.Abs(e2["est"][sh.Kind].Us - e2["product"][sh.Kind].Us));
        Console.WriteLine($"  ⚠ 耗时噪声下限 = {noiseUs} µs（est vs product 两条同路臂的最大形状差）"
            + " ⇒ 本表的判据只落在【分配】列；耗时列要用它当尺子，超过才算分得出。");
        long TotalOf(string arm) => e2[arm].Values.Sum(v => v.Total);
        long estTotal = TotalOf("est");
        foreach (string other in new[] { "head", "fixed64k", "ema", "oracle", "product" })
        {
            long otherTotal = TotalOf(other);
            Console.WriteLine($"    E2 整段序列 est − {other,-9} 总分配 = {estTotal - otherTotal} B"
                + (estTotal - otherTotal > 0 ? "  ← 对照整段更省" : "  ← 出货整段更省或打平"));
        }
        // est 与 product 是同一条代码路径的两臂 ⇒ 二者之差 = 本表【分配列】自己的噪声下限。
        // 报出来是给下面那句"谁更省"定标尺：差值不明显超过它的格子一律读成打平，不读成收益。
        long allocFloor = Math.Abs(estTotal - TotalOf("product"));
        Console.WriteLine($"  ⚠ 分配噪声下限 = {allocFloor} B/整段（est vs product 两条同路臂之差）；"
            + $"est vs ema = {estTotal - TotalOf("ema")} B 是它的 "
            + $"{(estTotal - TotalOf("ema")) / Math.Max(1L, allocFloor):F0} 倍"
            + " ⇒ 超过下限才叫分得出，同量级的一律读成打平");

        var csvPath = WriteCsv(outDir, "bench_deflate_cap", rows);
        Console.WriteLine($"CSV → {csvPath}");

        // ── 判据③收口：一格都判不出分配差别 ⇒ 仪表什么都没量到 ──
        if (!anyAllocSpread)
        {
            Console.WriteLine("❌ non-discriminating:alloc —— 三个形状上六臂的中位分配字节【全部相等】："
                + "初值这件事在本尺寸上量不出差别，本表不判，也不许被读成'改初值没代价/没收益'");
            return 1;
        }
        // product 不参与"赢家"评选：它就是 est 那条路，留着只作仪器自证
        string winTotal = new[] { "head", "fixed64k", "est", "ema", "oracle" }
            .OrderBy(a => TotalOf(a)).First();
        Console.WriteLine("✅ bench-deflate-cap 通过（② 容量不进码流：E0 两形状×5 显式初值 + E1 三形状×六臂"
            + "产物逐字节相同；③ 至少一形状分配可判）");
        Console.WriteLine($"   E2 整段序列总分配最省的臂 = {winTotal}（出货=est）。赢家不是 est 时，上面那些差值"
            + "就是「固定/历史型初值值多少」的数；本表不预设方向，本轮不改产品，读数先进 CSV。");
        return 0;
    }

    /// <summary>deflate 的硬膨胀上界（与 <c>DeflateZlib.EstimatedCapacity</c> 的钳位同一式子）。</summary>
    private static int DeflateBound(int n) => n + (n >> 3) + 512;

    /// <summary>复刻 CompressBuiltin 的形状，只把缓冲初值换成参数 —— 用来问"初值值多少"。</summary>
    private static byte[] CompressWithCap(byte[] data, int level, int cap)
    {
        var options = new ZLibCompressionOptions { CompressionLevel = level };
        using var ms = new MemoryStream(cap);
        using (var zlib = new ZLibStream(ms, options, leaveOpen: true)) zlib.Write(data);
        int n = (int)ms.Length;
        var buf = ms.GetBuffer();
        if (buf.Length == n) return buf;
        var r = new byte[n];
        Buffer.BlockCopy(buf, 0, r, 0, n);
        return r;
    }

    /// <summary>改动前的形状：无初值（MemoryStream 从 256 B 倍增）+ 末尾 ToArray 拷贝一次。</summary>
    private static byte[] CompressHeadShape(byte[] data, int level)
    {
        var options = new ZLibCompressionOptions { CompressionLevel = level };
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, options, leaveOpen: true)) zlib.Write(data);
        return ms.ToArray();
    }

    /// <summary>三档可压缩性夹具（确定性，无随机）：ui = 大片纯色 + 细线（deflate 后 ≈0.01）、
    /// photo = 中熵（每 5 字节一个随机字节，其余走平滑渐变 ⇒ ≈0.2，对应 EstimatedCapacity 注释里
    /// "照片实测 0.226"那一端）、noise = xorshift64* 全熵（≈1.0）。
    /// 比例不靠命名猜：跑台前的<b>夹具自检</b>用产品自己的 CompressBuiltin 量出真实比例并卡区间，
    /// 摊不开三档就直接红。</summary>
    private static byte[] CapFixture(string kind, int len)
    {
        var d = new byte[len];
        if (kind == "ui")
        {
            for (int i = 0; i < len; i++)
                d[i] = (byte)(i % 64 == 0 || (i / 64) % 32 == 0 ? (i * 7) % 251 : 0);
        }
        else if (kind == "photo")
        {
            ulong s = 0x2545F4914F6CDD1DUL;
            for (int i = 0; i < len; i++)
            {
                s ^= s << 13; s ^= s >> 7; s ^= s << 17;
                d[i] = i % 5 == 0 ? (byte)(s >> 33) : (byte)((i >> 6) & 0xFF);
            }
        }
        else
        {
            ulong s = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < len; i++)
            {
                s ^= s << 13; s ^= s >> 7; s ^= s << 17;
                d[i] = (byte)(s >> 33);
            }
        }
        return d;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
    private static string CsvSafe(string s)
        => s.Replace(",", ";").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}
