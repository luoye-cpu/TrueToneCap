// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/FormatBench.cs
// 全格式编码性能基准测试 (控制台)
// 用法: dotnet run --project src/TrueToneCap.Tools/FormatBench.csproj

using System.Diagnostics;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;

// ═══ OCR 准确度基准分派 (--ocr-bench <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-bench")
{
    OcrBench.Run(args);
    return;
}

// ═══ OCR 真实语料基准 (--ocr-bench-real [seed]) — 多样化真实内容+真实截图风格 ═══
if (args.Length >= 1 && args[0] == "--ocr-bench-real")
{
    OcrRealBench.Run(args);
    return;
}

// ═══ OCR 模型诊断 (--ocr-diag <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-diag")
{
    OcrModelDiag.Run(args);
    return;
}

// ═══ OCR 预处理变体对比 (--ocr-pre <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-pre")
{
    OcrPreprocessDiag.Run(args);
    return;
}

// ═══ 引擎级 OCR 诊断 (--ocr-eng <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-eng")
{
    OcrEngineDiag.Run(args);
    return;
}

// ═══ rec 模型单独诊断 (--ocr-rec <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-rec")
{
    OcrRecDiag.Run(args);
    return;
}

// ═══ 检测框级诊断 (--ocr-box <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-box")
{
    OcrBoxDiag.Run(args);
    return;
}

// ═══ 引擎内部框诊断 (--ocr-ebox <modelDir>) ═══
if (args.Length >= 1 && args[0] == "--ocr-ebox")
{
    OcrEngineBoxDiag.Run(args);
    return;
}

// ═══ 产品路径上的 JXL 色彩声明生效性探针 (--jxl-color-product，交接 §44) ═══
if (args.Length >= 1 && args[0] == "--jxl-color-product")
{
    // 本文件其余分派都是裸 `return;` ⇒ 顶层入口是 void，这里只能走 ExitCode 传结论
    Environment.ExitCode = JxlColorProductProbe.Run(args);
    return;
}

// ═══ GainMap 两模式的端到端判据 (--gainmap-color-product，交接 §45) ═══
if (args.Length >= 1 && args[0] == "--gainmap-color-product")
{
    Environment.ExitCode = GainMapColorProductProbe.Run(args);
    return;
}

// ═══ GainMap 两模式的观感对比 (--gainmap-look <rgba> <宽> <高> [目录]，交接 §47) ═══
if (args.Length >= 1 && args[0] == "--gainmap-look")
{
    Environment.ExitCode = GainMapColorProductProbe.RunLook(args);
    return;
}

// ═══ AVIF 后端 × CRF 实测阶梯 (--avif-ladder <png> [目录] …，交接 §48) ═══
if (args.Length >= 1 && args[0] == "--avif-ladder")
{
    Environment.ExitCode = AvifBackendLadder.Run(args);
    return;
}

    // ═══ 宽高比/宽度扫描（整行 vs 分块）(--ocr-ar-sweep [modelDir] [fontSize]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-ar-sweep")
    {
        OcrArSweep.Run(args);
        return;
    }

    // ═══ 根因判别：det 输入宽度 vs 宽高比 (--ocr-ar-probe [modelDir] [fontSize]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-ar-probe")
    {
        OcrArSweep.RunProbe(args);
        return;
    }

    // ═══ 单用例 DirectML 探针 (--ocr-dml-one <字符数> <cap> <split> [字号] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-one")
    {
        OcrArSweep.RunDmlOne(args);
        return;
    }

    // ═══ 跨后端复验 CPU vs DirectML (--ocr-dml-check [modelDir] [fontSize]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-check")
    {
        OcrArSweep.RunProviderCheck(args);
        return;
    }

    // ═══ 噪声回归检查（横线分隔线）(--ocr-noise [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-noise")
    {
        OcrArSweep.RunNoiseCheck(args);
        return;
    }

    // ═══ DetMaxBoxAspect 阈值扫描 (--ocr-asp-sweep [modelDir] [fontSize]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-asp-sweep")
    {
        OcrArSweep.RunAspectSweep(args);
        return;
    }

    // ═══ 产品路径 DirectML 原始推理 (--ocr-dml-raw <w> <h> [cap] [split] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-raw")
    {
        OcrProbProbe.RunDmlRaw(args);
        return;
    }

    // ═══ CPU / DirectML 逐阶段 A/B (--ocr-ab <字符数> <画布高> [字号] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-ab")
    {
        OcrProbProbe.RunAb(args);
        return;
    }

    // ═══ SessionOptions 生命周期对撞 (--ocr-dml-life <mode 0-3> [w] [h] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-life")
    {
        OcrProbProbe.RunDmlLife(args);
        return;
    }

    // ═══ DirectML SessionOptions 逐项定位 (--ocr-dml-opt <level0-5> [w] [h] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-opt")
    {
        OcrProbProbe.RunDmlOpt(args);
        return;
    }

    // ═══ 最小 DirectML 推理探针 (--ocr-dml-min [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-dml-min")
    {
        OcrProbProbe.RunDmlMinimal(args);
        return;
    }

    // ═══ 概率图/过滤链探针 (--ocr-prob <字符数> <画布高度> [字号] [modelDir]) ═══
    if (args.Length >= 1 && args[0] == "--ocr-prob")
    {
        OcrProbProbe.Run(args);
        return;
    }

    // ═══ WinML 迁移压力回归 (--winml-stress [iterations] [modelDir]) ═══
    // 驱动真实 OnnxOcrEngine，双 EP × 多尺寸 × 多轮 + 并发调用。
    // 用途: winml 迁移验收 A4/A5；也可作为 ORT/运行时升级后的常驻回归。
    if (args.Length >= 1 && args[0] == "--winml-stress")
    {
        Environment.ExitCode = WinMlStress.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

// ═══ 标准 ICC 空间实测验证 (--icc-check) ═══
if (args.Length >= 1 && args[0] == "--icc-check")
{
    IccCheck.Run(args);
    return;
}

// ═══ PNG cICP + iCCP 双重声明验证 (--png-check) ═══
if (args.Length >= 1 && args[0] == "--png-check")
{
    PngCheck.Run(args);
    return;
}

// ═══ 格式映射全链路验证 (--fmt-check) ═══
if (args.Length >= 1 && args[0] == "--fmt-check")
{
    FmtCheck.Run(args);
    return;
}

// ═══ AVIF 编码器实测能力 (--avif-caps [--ffprobe]) ═══
// 必须挂在早退区：之后是无条件执行的 4K 全格式 bench。
if (args.Length >= 1 && args[0] == "--avif-caps")
{
    AvifCapsDiag.Run(args);
    return;
}

// ═══ AVIF HDR 路径 PQ 实测 (--avif-hdr-test [--kind nvenc|avifenc|auto]) ═══
// 2026-09-18：HDR 路径此前只有"声明值"约束（SupportsHdrPath + 实测 ≥10-bit），
// 从未做 PQ 实测编码。本命令在真机上用 Float16 scRGB 源走完整 HDR 编码路径，
// 再用外部 ffprobe 交叉验证码流色彩声明（PQ/2020/10-bit）与 clli。
if (args.Length >= 1 && args[0] == "--avif-hdr-test")
{
    AvifHdrDiag.Run(args);
    return;
}

// ═══ HDR AVIF 位深 A/B (--avif-hdr-depth) ═══
// 2026-09-25：用户要求"libaom + HDR 可选 10/12 追求更高精度"。这条改动的落点横跨四处
// （协商函数 / 管线 / PQ 量化 / avifenc 命令行），只看 diff 证明不了"产物真的成了 12-bit"
// ⇒ 判据落在产物上：请求 10 得 p10、请求 12 得 p12、两次字节必须不同、色彩声明不许丢。
if (args.Length >= 1 && args[0] == "--avif-hdr-depth")
{
    AvifHdrDepthProbe.Run();
    return;
}

// ═══ NVENC 的 RGB→YUV 矩阵/摆幅实测 (--nvenc-color-probe [sweep]) ═══
// 批次 8 的前置：NVENC 的矩阵与摆幅是硬件自己定的，声明错了比不声明更坏 ⇒ 先测后写。
// sweep 子模式再把 matrixCoefficients 逐个码点下发，判"驱动听不听这个字段"。
// 产物落在 artifacts/precision/probe/nvenc_color/，分析在 scripts/probes/nvenc_matrix_probe.py。
if (args.Length >= 1 && args[0] == "--nvenc-color-probe")
{
    NvencColorDiag.Run(args.Length >= 2 ? args[1] : null);
    return;
}

// ═══ 生成 4K 测试像素 (彩色渐变) ═══
const int W = 3840, H = 2160;
var bgra = new byte[W * H * 4];
for (int y = 0; y < H; y++)
for (int x = 0; x < W; x++)
{
    int i = (y * W + x) * 4;
    bgra[i]     = (byte)(y * 255 / H);                    // B
    bgra[i + 1] = (byte)(x * 255 / W);                    // G
    bgra[i + 2] = (byte)(((x + y) % 512) * 255 / 512);    // R
    bgra[i + 3] = 255;                                    // A
}
Console.WriteLine($"测试像素: {W}x{H} ({bgra.Length / 1024 / 1024} MB BGRA8)");

// ═══ 色调映射性能基准 (2026-08-09: 验证 sRGB gamma LUT 加速) ═══
var hdrLin = PixelOps.BgraToScrgbLinearFast(bgra, W, H);
var toneParams = new ToneMappingParams(ToneMapMode.SegmentedReinhard, 0, 200, 1000);
for (int i = 0; i < 2; i++) ToneMapper.FloatToSRgbBytes(hdrLin, W, H, toneParams); // 预热
var tsw = Stopwatch.StartNew();
for (int i = 0; i < 5; i++) ToneMapper.FloatToSRgbBytes(hdrLin, W, H, toneParams);
tsw.Stop();
Console.WriteLine($"色调映射 (LUT): {tsw.ElapsedMilliseconds / 5.0:F1} ms/次 (4K)");
Console.WriteLine($"ISA: AVX2={PixelOps.HasAvx2} AVX512={PixelOps.HasAvx512Full} AVX10v1={PixelOps.HasAvx10V1} AVX10v2={PixelOps.HasAvx10V2} FMA={PixelOps.HasFma} AVXVNNI={PixelOps.HasAvxVnni} NEON={PixelOps.HasNeon} VecWidth={PixelOps.BestVectorByteWidth}\n");

// ═══ 全格式测试 ═══
// 注意: 各格式质量语义不同 (JXL/JPEG_LI/GainMap=butteraugli distance 0.x-4.0,
// WebP=百分比, AVIF=CRF, PNG=固定)。必须用各格式合法范围的值, 否则 cjxl/cjpegli 失败。
var formats = new (OutputFormat Fmt, string Name, float Quality, bool Hdr, int TimeoutS)[]
{
    (OutputFormat.PNG,          "PNG (无损)",          100, false, 10),
    (OutputFormat.JPEG_LI,       "JPEG LI",             2.0f, false, 10),
    (OutputFormat.WebP,          "WebP",                90f, false, 10),
    (OutputFormat.AVIF,          "AVIF",                30f, false, 60),
    (OutputFormat.JPEG_XL,       "JPEG XL",             2.0f, false, 60),
    (OutputFormat.JPEG_GAINMAP,  "JPEG GainMap (HDR)",  1.0f, true,  60),
};

string outDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_Bench");
Directory.CreateDirectory(outDir);
Console.WriteLine($"输出目录: {outDir}\n");

Console.WriteLine($"{"格式",-24} {"大小",-10} {"编码时间",-10} {"状态"}");
Console.WriteLine(new string('-', 60));

foreach (var (fmt, name, quality, hdr, timeoutS) in formats)
{
    var encoder = EncoderFactory.Create(fmt);
    var sw = Stopwatch.StartNew();
    string status;
    long fileSize = 0;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutS));
    try
    {
        var settings = new EncodingSettings
        {
            Format = fmt,
            Quality = quality,
            HdrOutput = hdr && encoder.SupportsHdr,
        };

        string ext = fmt switch
        {
            OutputFormat.JPEG_GAINMAP => ".jpg",
            OutputFormat.JPEG_LI => ".jpg",
            OutputFormat.JPEG_XL => ".jxl",
            _ => $".{fmt.ToString().ToLowerInvariant()}"
        };
        string path = Path.Combine(outDir, $"bench_{fmt}{ext}");

        var hdrFrame = hdr && encoder.SupportsHdr
            ? new HdrFrameData
            {
                Pixels = TrueToneCap.Core.PixelOps.BgraToScrgbLinearFast(bgra, W, H),
                Width = W, Height = H
            }
            : null;

        if (hdrFrame is not null)
            await encoder.EncodeAsync(hdrFrame, settings, path, cts.Token);
        else
            await encoder.EncodeSdrAsync(bgra, W, H, settings, path, cts.Token);

        sw.Stop();
        fileSize = new FileInfo(path).Length;
        status = "✅";
    }
    catch (OperationCanceledException)
    {
        sw.Stop();
        status = $"⏱ 超时({timeoutS}s)";
    }
    catch (Exception ex)
    {
        sw.Stop();
        status = $"❌ {ex.Message.Split('\n')[0]}";
    }

    string sizeStr = fileSize > 1024 * 1024
        ? $"{fileSize / 1024.0 / 1024.0:F2} MB"
        : $"{fileSize / 1024.0:F1} KB";
    Console.WriteLine($"{name,-24} {sizeStr,-10} {sw.ElapsedMilliseconds + "ms",-10} {status}");
}

Console.WriteLine($"\n✅ 完成 — 文件保存在: {outDir}");

// ═══ 2026-09-17 实机验证（交接 §5-5）：Float16 源 8-bit / 10-bit 双通路对比 ═══
// 用 scRGB 线性光渐变+纹理源（模拟真实 Float16 截图内容），
// 分别走 FloatToSRgbBytes(8-bit BGRA→ARGB 升位深) 与 FloatToArgb10Bytes(真 ARGB10)，
// 产出 .av1 码流 → ffmpeg gray10le 解码 → 阶梯判据 + 体积 + 耗时对比。
if (args.Length >= 1 && args[0] == "--bitdepth-bench")
{
    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║  Float16 源 8-bit / 10-bit 双通路实机对比    ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");

    const int BW = 256, BH = 256, ROWS = 4;        // 64 带 × 4 行，相邻带差 1/1023
    // 源布局与管线一致：RGBA float（每像素 4 通道，i+3 = alpha）
    var hdr = new float[BW * BH * 4];
    for (int y = 0; y < BH; y++)
    {
        int band = y / ROWS;
        float srgb8 = (512 + band) / 1023f;        // 目标 10-bit 灰阶
        float lin = SrgbToLinear(srgb8);           // 反 gamma 回线性光（scRGB 源语义）
        for (int x = 0; x < BW; x++)
        {
            int i = (y * BW + x) * 4;
            float tex = ((x >> 4) & 1) == 1 ? 1.15f : 1.0f;   // 竖条纹理，负载真实压缩内容
            hdr[i] = lin * tex; hdr[i + 1] = lin * tex; hdr[i + 2] = lin * tex;
            hdr[i + 3] = 1f;
        }
    }
    static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    // 源已在 SDR 亮度域：SegmentedReinhard 对 y≤白点直通，等效不压缩（无 None 模式）
    var tp = new ToneMappingParams();
    string tag = "sRGB";

    using var dev = NvEncoderNative.CreateNvidiaDevice()
        ?? throw new InvalidOperationException("无法创建 NVIDIA D3D11 设备");
    using var nv = new NvEncoderNative(dev);

    string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "precision", "probe");
    Directory.CreateDirectory(Path.GetFullPath(dir));
    dir = Path.GetFullPath(dir);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    byte[] px8 = ToneMapper.FloatToSRgbBytes(hdr, BW, BH, tp, tag);
    sw.Stop();
    long t8pre = sw.ElapsedMilliseconds;
    sw.Restart();
    byte[] bs8 = nv.EncodeAv1(px8, BW, BH, 1, 10);
    sw.Stop();
    long t8enc = sw.ElapsedMilliseconds;

    sw.Restart();
    byte[] px10 = ToneMapper.FloatToArgb10Bytes(hdr, BW, BH, tp, tag);
    sw.Stop();
    long t10pre = sw.ElapsedMilliseconds;
    sw.Restart();
    byte[] bs10 = nv.EncodeAv1Argb10(px10, BW, BH, 1);
    sw.Stop();
    long t10enc = sw.ElapsedMilliseconds;

    File.WriteAllBytes(Path.Combine(dir, "real_8bit.av1"), bs8);
    File.WriteAllBytes(Path.Combine(dir, "real_10bit.av1"), bs10);
    File.WriteAllBytes(Path.Combine(dir, "real_8bit_px.bgra"), px8);
    File.WriteAllBytes(Path.Combine(dir, "real_10bit_px.argb10"), px10);

    Console.WriteLine($"    8-bit 通路: 预处理 {t8pre}ms + 编码 {t8enc}ms, 码流 {bs8.Length} B");
    Console.WriteLine($"    10-bit通路: 预处理 {t10pre}ms + 编码 {t10enc}ms, 码流 {bs10.Length} B");

    // ffmpeg 解码两路输出为 gray10le，按带统计
    var ff = @"C:\Windows\ffmpeg-8.0-full_build\bin\ffmpeg.exe";
    if (!File.Exists(ff)) ff = "ffmpeg.exe";
    double[] Decode(string file)
    {
        var raw = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "_dec.raw");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-v error -i \"{file}\" -f rawvideo -pix_fmt gray10le -y \"{raw}\"",
            UseShellExecute = false, RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg: {err.Trim()}");
        var d = File.ReadAllBytes(raw);
        var bandAvg = new double[BH / ROWS];
        for (int b = 0; b < bandAvg.Length; b++)
        {
            long sum = 0; int cnt = 0;
            for (int y = b * ROWS; y < (b + 1) * ROWS; y++)
                for (int x = 0; x < BW; x++)
                { sum += d[(y * BW + x) * 2] | (d[(y * BW + x) * 2 + 1] << 8); cnt++; }
            bandAvg[b] = (double)sum / cnt;
        }
        File.Delete(raw);
        return bandAvg;
    }

    foreach (var (name, file) in new[] { ("8-bit ", "real_8bit.av1"), ("10-bit", "real_10bit.av1") })
    {
        var v = Decode(Path.Combine(dir, file));
        int levels = 1;
        for (int b = 1; b < v.Length; b++) if (v[b] != v[b - 1]) levels++;
        double runs = 1;
        for (int b = 1; b < v.Length; b++) if (v[b] != v[b - 1]) runs++;
        Console.WriteLine($"    [{name}] 阶梯判据: 不同级={levels}/64, 同值平均连续带={v.Length / runs:F2}");
    }

    // 颜色一致性：同一带 8-bit vs 10-bit 解码均值差（应 < 2 LSB@10bit ≈ 0.5 LSB@8bit）
    var v8 = Decode(Path.Combine(dir, "real_8bit.av1"));
    var v10 = Decode(Path.Combine(dir, "real_10bit.av1"));
    double maxDiff = 0;
    for (int b = 0; b < Math.Min(v8.Length, v10.Length); b++)
        maxDiff = Math.Max(maxDiff, Math.Abs(v8[b] - v10[b]));
    Console.WriteLine($"    颜色一致性: 8-bit 与 10-bit 各带均值最大差 = {maxDiff:F1} LSB@10bit");
    Console.WriteLine($"\n✅ 完成 — 产物在: {dir}");
}

// ═══ 2026-09-17 实机验证（§5-5 扩展）：Float16 源走各格式真实 EncodeAsync（位深 10）═══
// 目标：验证「双路径全格式接线」——Float16 源应喂给各编码器它能接受的最高质量输入
// （PNG/JXL/TIFF 16-bit 真输入；AVIF 软件 16-bit PNG 中转；WebP/JpegLi 诚实 8）。
// 用 ffprobe 读产物 pix_fmt/bps 佐证，量体积与耗时。
if (args.Length >= 1 && args[0] == "--sdr16-bench")
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║  Float16 源 → 全格式高位深 SDR 通路实测 (10-bit) ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");

    const int BW = 512, BH = 512;
    static float S16ToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    // 渐变 + 竖条纹理（比纯色带更接近真实截图的压缩负载）
    var hdr = new float[BW * BH * 4];
    for (int y = 0; y < BH; y++)
        for (int x = 0; x < BW; x++)
        {
            int i = (y * BW + x) * 4;
            float v = S16ToLinear(0.05f + 0.9f * x / (BW - 1));   // 水平亮度渐变
            float tex = ((x >> 3) & 1) == 1 ? 1.1f : 1.0f;          // 竖条纹理
            hdr[i] = v * tex; hdr[i + 1] = v * tex; hdr[i + 2] = v * tex; hdr[i + 3] = 1f;
        }

    string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "precision", "probe");
    Directory.CreateDirectory(Path.GetFullPath(dir));
    dir = Path.GetFullPath(dir);

    var probe = @"C:\Windows\ffmpeg-8.0-full_build\bin\ffprobe.exe";
    string ProbePixFmt(string file)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = probe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt,bits_per_raw_sample -of csv=p=0 \"{file}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            return o;
        }
        catch { return "(ffprobe 不可用)"; }
    }

    async Task Run(string name, OutputFormat fmt, string ext, float quality)
    {
        var s = new EncodingSettings
        {
            Format = fmt, Quality = quality, OutputBitDepth = 10,
            HdrOutput = false, ColorSpaceTag = "sRGB", AvifEncoderKind = AvifEncoderKind.Auto,
        };
        // 复刻 EncodeHdrFrameAsync 的 SDR 降级分派（AVIF 委托 EncodeAsync 全流程）
        var enc = EncoderFactory.Create(fmt);
        var path = Path.Combine(dir, $"sdr16_{name}.{ext}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await enc.EncodeAsync(new HdrFrameData { Pixels = hdr, Width = BW, Height = BH }, s, path);
            sw.Stop();
            var size = new FileInfo(path).Length;
            Console.WriteLine($"  {name,-10} {size / 1024.0,8:F1} KB  {sw.ElapsedMilliseconds,6} ms   pix_fmt={ProbePixFmt(path)}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  {name,-10} ❌ {ex.GetType().Name}: {ex.Message.Split('\n')[0]}  ({sw.ElapsedMilliseconds} ms)");
        }
    }

    await Run("png10", OutputFormat.PNG, "png", 100f);
    await Run("tiff16", OutputFormat.TIFF, "tiff", 100f);
    await Run("jxl10", OutputFormat.JPEG_XL, "jxl", 1.0f);
    await Run("avif-sw", OutputFormat.AVIF, "avif", 15f);   // Auto→若 NVENC 可用会走 ARGB10
    await Run("webp8", OutputFormat.WebP, "webp", 90f);     // 预期诚实 8-bit

    // 对照：8-bit 请求的 PNG（升位深应被钳回 8）
    {
        var s = new EncodingSettings { Format = OutputFormat.PNG, Quality = 100, OutputBitDepth = 10, HdrOutput = false, ColorSpaceTag = "sRGB" };
        var enc = EncoderFactory.Create(OutputFormat.PNG);
        // byte[] 8-bit 源入口（双路径：应钳 8）
        var bgra8 = ToneMapper.FloatToSRgbBytes(hdr, BW, BH, s.ToneMappingParams, "sRGB");
        var path = Path.Combine(dir, "sdr16_png8src.png");
        await enc.EncodeSdrAsync(bgra8, BW, BH, s, path);
        Console.WriteLine($"  png(8src)  {new FileInfo(path).Length / 1024.0,8:F1} KB   (8-bit 源 + 请求 10 ⇒ 应钳为 8, sBIT=8)");
    }
    Console.WriteLine($"\n✅ 完成 — 产物在: {dir}");
}

// ═══ 广色域 SDR 兼容性样张（2026-09-19，交接 §4.5 风险① / §16.4 遗留）═══
// 目的：把【产品自己写出的】cICP + iCCP 双标记文件交给真实查看器（Edge / Chrome / 系统查看器），
// 判定它们是按标记解释、还是当 sRGB 直读（→ 偏色）。
// 图案刻意用【有彩色】：中性灰在"只换原色、白点同为 D65"的矩阵变换下几乎不变，
// 拿它做判据会得出"标记认不认都一样"的假结论 —— 必须用 R/G/B 这类受原色矩阵影响的色块。
// 取样坐标与判据由 scripts/precision/wg_compat_check.py 负责（本命令只负责产出真实字节）。
if (args.Length >= 1 && args[0] == "--wg-samples")
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║  广色域 SDR 双标记样张（scRGB → sRGB / P3 / BT.2020）║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");

    const int PW = 160, PH = 120, COLS = 4;
    // 线性 scRGB 色块：4 个原色 + 2 个中性（对照组）+ 2 个混合色
    var patches = new (string Name, float R, float G, float B)[]
    {
        ("red-mid", 0.20f, 0.00f, 0.00f), ("green-mid", 0.00f, 0.20f, 0.00f),
        ("blue-mid", 0.00f, 0.00f, 0.20f), ("red-high", 0.80f, 0.00f, 0.00f),
        ("gray-18", 0.18f, 0.18f, 0.18f), ("gray-50", 0.50f, 0.50f, 0.50f),
        ("cyan-mid", 0.00f, 0.35f, 0.35f), ("orange", 0.55f, 0.25f, 0.10f),
    };
    int rows = (patches.Length + COLS - 1) / COLS;
    int imgW = PW * COLS, imgH = PH * rows;
    var px = new float[imgW * imgH * 4];
    for (int i = 0; i < patches.Length; i++)
    {
        var p = patches[i];
        for (int y = (i / COLS) * PH; y < (i / COLS + 1) * PH; y++)
            for (int x = (i % COLS) * PW; x < (i % COLS + 1) * PW; x++)
            {
                int k = (y * imgW + x) * 4;
                px[k] = p.R; px[k + 1] = p.G; px[k + 2] = p.B; px[k + 3] = 1f;
            }
    }
    Console.WriteLine($"  图案 {imgW}x{imgH}，{patches.Length} 块（{PW}x{PH}），块心: "
        + string.Join(" ", patches.Select((p, i) => $"{p.Name}=({(i % COLS) * PW + PW / 2},{(i / COLS) * PH + PH / 2})")));

    string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "precision", "probe", "wg");
    dir = Path.GetFullPath(dir);
    Directory.CreateDirectory(dir);

    int wgMissing = 0;
    foreach (var tag in new[] { "sRGB", "DisplayP3", "BT2020" })
    {
        // PNG 是无损参照（判据以它为准）；AVIF 走软件 libaom，容器才会写 colr(nclx)+iinf；
        // JXL / TIFF 只有 ICC 一条路（cjxl 无 "BT.2020+sRGB" 组合、TIFF 无 CICP 机制）⇒ 一并产出供对照。
        var jobs = new (OutputFormat Fmt, string Ext, float Q)[]
        {
            (OutputFormat.PNG, "png", 100f), (OutputFormat.AVIF, "avif", 60f),
            (OutputFormat.JPEG_XL, "jxl", 1f), (OutputFormat.TIFF, "tiff", 100f),
            // ═══ 2026-09-29 审查轮 §13.4：这两列以前根本不在"第三方解码像素"这一层里 ═══
            // WebP 只有 ICCP 一条声明通道、JPEG LI 与增益图底图共用同一套码流 ⇒ 矩阵或量化单独
            // 在它们身上走偏时，只比 PNG/AVIF/JXL/TIFF 的这层判据照不出来（09-19 的 TIFF 事故同族）。
            (OutputFormat.WebP, "webp", 90f), (OutputFormat.JPEG_LI, "jpg", 1f),
        };
        // 位深轴：这层原来只有一档（10-bit）⇒ "位深下拉同时决定做不做矩阵"那一族（09-19 P1-1）
        // 在 WebP / JPEG LI 上没有对照。8-bit 自成一组（前缀带 d08）⇒ 裁判按 split("_")[0] 分组，
        // 每组各有自己的 PNG 基准，不会拿 8-bit 去比 10-bit 的基准。
        foreach (var (cell, depth) in new (string Cell, int Depth)[] { (tag, 10), ($"{tag}d08", 8) })
        {
            foreach (var (fmt, ext, q) in jobs)
            {
                var s = new EncodingSettings
                {
                    Format = fmt, Quality = q, OutputBitDepth = depth, HdrOutput = false,
                    ColorSpaceTag = tag,
                    AvifEncoderKind = AvifEncoderKind.LibAom,   // 避开硬件，容器元数据走软件路径
                };
                var path = Path.Combine(dir, $"{cell}_{fmt.ToString().ToLowerInvariant()}.{ext}");
                try
                {
                    await EncoderFactory.Create(fmt).EncodeAsync(
                        new HdrFrameData { Pixels = px, Width = imgW, Height = imgH }, s, path);
                    Console.WriteLine($"  ✅ {Path.GetFileName(path),-26} {new FileInfo(path).Length / 1024.0,7:F1} KB");
                }
                catch (Exception ex)
                {
                    wgMissing++;
                    Console.WriteLine($"  ❌ {Path.GetFileName(path),-26} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
            }
        }
    }
    // 少产出一格 = 这一层的覆盖范围悄悄缩水（判据只会看到"少几个文件"而不会红）。
    // 所以这里必须留非零痕迹，让 [1.7/2] 的 fail-closed 接得住。
    if (wgMissing > 0)
    {
        Console.WriteLine($"\n❌ 有 {wgMissing} 个格子没产出样张 ⇒ 跨容器像素层的覆盖不完整，本轮不算判过");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine($"\n✅ 样张已产出 → {dir}\n"
            + "   判据: py scripts/precision/wg_container_roundtrip.py（第三方解码器比像素，已进门禁 [1.7/2]）\n"
            + "         py scripts/precision/wg_compat_check.py（读回元数据与存储像素，再与查看器渲染值比对）");
    }
}

// ═══ 容器结构审计（2026-09-19）═══
// 起因：TIFF 的 P0（StripOffsets 写成值区指针 + tag 8 配裸 deflate）在 8 轮门禁里一直是绿的，
// 因为仓库里的 TIFF 解析器复刻了写入器自己的结构假设（交接 §3 纪律的容器层版本）。
// 本命令把【每一条编码路径】都产出一份同一图案的文件，交给 ffmpeg 这个外部解码器判活：
// SDR 要求"同色域跨容器像素一致"，HDR 要求"PQ 元数据齐备 + 非全黑 + 灰阶单调"。
// 覆盖：7 种输出格式 × SDR/HDR × 软件与硬件（含 NVENC 的 IvfWriter 容器路径，此前只验过
// "av1C 读回位深/色度"，没验过像素内容）。
if (args.Length >= 1 && args[0] == "--container-audit")
{
    Console.WriteLine("╔════════════════════════════════════════════════╗");
    Console.WriteLine("║  全格式容器审计（外部解码器判活）              ║");
    Console.WriteLine("╚════════════════════════════════════════════════╝");

    const int PW = 160, PH = 120, ACOLS = 4;
    // 与 --wg-samples 同一套块坐标，方便共用判据脚本；必须含有彩色（中性灰在原色矩阵下几乎不变）
    var apatches = new (string Name, float R, float G, float B)[]
    {
        ("red-mid", 0.20f, 0.00f, 0.00f), ("green-mid", 0.00f, 0.20f, 0.00f),
        ("blue-mid", 0.00f, 0.00f, 0.20f), ("red-high", 0.80f, 0.00f, 0.00f),
        ("gray-18", 0.18f, 0.18f, 0.18f), ("gray-50", 0.50f, 0.50f, 0.50f),
        ("cyan-mid", 0.00f, 0.35f, 0.35f), ("orange", 0.55f, 0.25f, 0.10f),
    };
    int arows = (apatches.Length + ACOLS - 1) / ACOLS;
    int aW = PW * ACOLS, aH = PH * arows;

    // HDR 版把亮度抬到 scRGB 扩展范围（>1.0），否则 PQ 通路等于没被压到
    float[] Build(bool hdr)
    {
        var a = new float[aW * aH * 4];
        for (int i = 0; i < apatches.Length; i++)
        {
            var p = apatches[i];
            float k = hdr ? 4f : 1f;
            for (int y = (i / ACOLS) * PH; y < (i / ACOLS + 1) * PH; y++)
                for (int x = (i % ACOLS) * PW; x < (i % ACOLS + 1) * PW; x++)
                {
                    int j = (y * aW + x) * 4;
                    a[j] = p.R * k; a[j + 1] = p.G * k; a[j + 2] = p.B * k; a[j + 3] = 1f;
                }
        }
        return a;
    }
    var sdrPx = Build(hdr: false);
    var hdrPx = Build(hdr: true);

    string adir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "precision", "probe", "audit");
    adir = Path.GetFullPath(adir);
    Directory.CreateDirectory(adir);

    var jobs = new (string Name, OutputFormat Fmt, string Ext, bool Hdr, AvifEncoderKind Kind, string Tag, float Q)[]
    {
        // ── SDR：8 位深档，全格式 ──
        // ⚠ JPEG_LI / JPEG_GAINMAP 的 Quality 刻度是 **butteraugli 距离**（0.5-3.0，越小越好），
        //   不是 0-100 的质量分（见各自 GetQualityRange）。传 90 会被 clamp 到 25 = 最差档，
        //   看起来像"色彩变换有 bug"，其实是判据用错了刻度（2026-09-19 踩过）。
        ("sdr_png_srgb",      OutputFormat.PNG,          "png",  false, AvifEncoderKind.Auto,    "sRGB",   100f),
        ("sdr_tiff_srgb",     OutputFormat.TIFF,         "tiff", false, AvifEncoderKind.Auto,    "sRGB",   100f),
        ("sdr_jxl_srgb",      OutputFormat.JPEG_XL,      "jxl",  false, AvifEncoderKind.Auto,    "sRGB",   1.0f),
        ("sdr_webp_srgb",     OutputFormat.WebP,         "webp", false, AvifEncoderKind.Auto,    "sRGB",   90f),
        ("sdr_jpegtt_srgb",   OutputFormat.JPEG_LI,      "jpg",  false, AvifEncoderKind.Auto,    "sRGB",   1.0f),
        ("sdr_gainmap_srgb",  OutputFormat.JPEG_GAINMAP, "jpg",  false, AvifEncoderKind.Auto,    "sRGB",   1.0f),
        ("sdr_avifauto_srgb", OutputFormat.AVIF,         "avif", false, AvifEncoderKind.Auto,    "sRGB",   60f),
        ("sdr_avifsw_srgb",   OutputFormat.AVIF,         "avif", false, AvifEncoderKind.LibAom,  "sRGB",   60f),
        ("sdr_avifnv_srgb",   OutputFormat.AVIF,         "avif", false, AvifEncoderKind.Nvenc,   "sRGB",   60f),
        ("sdr_png_bt2020",    OutputFormat.PNG,          "png",  false, AvifEncoderKind.Auto,    "BT2020", 100f),
        // 生产形状：§11.2 会把 Float16+SDR 的 OutputBitDepth 提到 16，这里照做，
        // 以便审计"16-bit 容器到底声明了多少有效位"（PNG sBIT / JXL bits_per_raw_sample）。
        ("sdr_png16_srgb",    OutputFormat.PNG,          "png",  false, AvifEncoderKind.Auto,    "sRGB",   100f),
        ("sdr_jxl16_srgb",    OutputFormat.JPEG_XL,      "jxl",  false, AvifEncoderKind.Auto,    "sRGB",   1.0f),
        ("sdr_tiff16_srgb",   OutputFormat.TIFF,         "tiff", false, AvifEncoderKind.Auto,    "sRGB",   100f),
        ("sdr_avifsw_bt2020", OutputFormat.AVIF,         "avif", false, AvifEncoderKind.LibAom,  "BT2020", 60f),
        ("sdr_tiff_bt2020",   OutputFormat.TIFF,         "tiff", false, AvifEncoderKind.Auto,    "BT2020", 100f),
        // ── HDR：PQ16 直通，含硬件容器 ──
        ("hdr_png",           OutputFormat.PNG,          "png",  true,  AvifEncoderKind.Auto,    "BT2020", 100f),
        ("hdr_tiff",          OutputFormat.TIFF,         "tiff", true,  AvifEncoderKind.Auto,    "BT2020", 100f),
        ("hdr_jxl",           OutputFormat.JPEG_XL,      "jxl",  true,  AvifEncoderKind.Auto,    "BT2020", 1.0f),
        ("hdr_avifsw",        OutputFormat.AVIF,         "avif", true,  AvifEncoderKind.LibAom,  "BT2020", 60f),
        ("hdr_avifnv",        OutputFormat.AVIF,         "avif", true,  AvifEncoderKind.Nvenc,   "BT2020", 60f),
    };

    foreach (var (name, fmt, ext, hdr, kind, tag, q) in jobs)
    {
        var s = new EncodingSettings
        {
            Format = fmt, Quality = q, HdrOutput = hdr, ColorSpaceTag = tag,
            // 名字里带 "16" 的样张走生产形状（§11.2 的 Float16+SDR ⇒ 16），其余按 10 请求
            OutputBitDepth = name.Contains("16") ? 16 : 10, AvifEncoderKind = kind,
        };
        var path = Path.Combine(adir, name + "." + ext);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await EncoderFactory.Create(fmt).EncodeAsync(
                new HdrFrameData { Pixels = hdr ? hdrPx : sdrPx, Width = aW, Height = aH }, s, path);
            sw.Stop();
            Console.WriteLine($"  ✅ {name,-20} {ext,-5} {new FileInfo(path).Length / 1024.0,8:F1} KB  {sw.ElapsedMilliseconds,5} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ❌ {name,-20} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }
    Console.WriteLine($"\n✅ 审计样张已产出 → {adir}\n   下一步: py scripts/precision/audit_roundtrip.py");
}

// ═══ 参数生效扫描（2026-09-19）═══
// 读码只能证明"代码把值传下去了"，不能证明"编码器真的按这个值改变输出"。
// 本命令逐个旋钮扫一遍（quality / chroma / bit-depth / color-space），
// 判据由 scripts/precision/param_sweep_check.py 用外部 ffprobe 读回产物来判定。
// 特别要抓的两类事故：① 参数被静默改写（如 Quality 刻度是距离却当质量用）；
// ② 参数被接受但不生效（如请求 422 只给 420，或 --depth 12 被回退 8-bit）。
if (args.Length >= 1 && args[0] == "--param-sweep")
{
    const int SW = 256, SH = 256;
    var px = new float[SW * SH * 4];
    for (int y = 0; y < SH; y++)
        for (int x = 0; x < SW; x++)
        {
            int i = (y * SW + x) * 4;
            float v = 0.05f + 0.9f * x / (SW - 1);
            float tex = ((x >> 3) & 1) == 1 ? 1.15f : 1f;
            px[i] = v * tex; px[i + 1] = v / tex; px[i + 2] = 0.9f - v; px[i + 3] = 1f;
        }

    string sdir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "artifacts", "precision", "probe", "sweep"));
    Directory.CreateDirectory(sdir);
    // ═══ 产出前先清干净自己的目录（2026-09-29 实测的必要）═══
    // 门禁的读法是"文件名前缀命中 = 这一格成功了"（param_sweep_check 判 c_avifsvt_444 的
    // "按预期失败 / 竟然成功了"就看有没有那个文件）。而这里过去【只覆盖同名文件、从不删旧的】⇒
    // 上一轮留下的产物会替下一轮作答：本轮把换格式保底的默认值改错的那一次，在目录里多写了一枚
    // c_avifsvt_444.png，之后即使代码改回来了，那一格仍被上一轮的尸体判成"竟然成功了"。
    // 陈尸不删，"失败格"的判据就永远可能读到别人的结果 ⇒ 生成者负责让自己目录里只有本次产物。
    foreach (var stale in Directory.GetFiles(sdir))
    {
        try { File.Delete(stale); }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 清不掉上一轮的样张 {Path.GetFileName(stale)}：{ex.GetType().Name} {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }
    }

    // name, format, ext, avifKind, quality, chroma, depth, tag
    var rows = new (string Name, OutputFormat Fmt, string Ext, AvifEncoderKind Kind, float Q, string Chroma, int Depth, string Tag)[]
    {
        // ── quality 扫描（注意：JPEG_XL / JPEG_LI / GAINMAP 的 Quality 是 butteraugli 距离，越小越好）──
        ("q_avifsw_10",  OutputFormat.AVIF,  "avif", AvifEncoderKind.LibAom, 10f,  "420", 8,  "sRGB"),
        ("q_avifsw_50",  OutputFormat.AVIF,  "avif", AvifEncoderKind.LibAom, 50f,  "420", 8,  "sRGB"),
        ("q_avifsw_90",  OutputFormat.AVIF,  "avif", AvifEncoderKind.LibAom, 90f,  "420", 8,  "sRGB"),
        ("q_webp_10",    OutputFormat.WebP,  "webp", AvifEncoderKind.Auto,   10f,  "420", 8,  "sRGB"),
        ("q_webp_50",    OutputFormat.WebP,  "webp", AvifEncoderKind.Auto,   50f,  "420", 8,  "sRGB"),
        ("q_webp_90",    OutputFormat.WebP,  "webp", AvifEncoderKind.Auto,   90f,  "420", 8,  "sRGB"),
        ("q_jpegli_d05", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto,   0.5f, "444", 8,  "sRGB"),
        ("q_jpegli_d10", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto,   1.0f, "444", 8,  "sRGB"),
        ("q_jpegli_d30", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto,   3.0f, "444", 8,  "sRGB"),
        ("q_jxl_d05",    OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto,  0.5f, "444", 8,  "sRGB"),
        ("q_jxl_d10",    OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto,  1.0f, "444", 8,  "sRGB"),
        ("q_jxl_d30",    OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto,  3.0f, "444", 8,  "sRGB"),
        // ── 色度扫描（AVIF 软件必须真给 422/444；硬件只该给 420）──
        ("c_avifsw_420", OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "420", 10, "sRGB"),
        ("c_avifsw_422", OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "422", 10, "sRGB"),
        ("c_avifsw_444", OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "444", 10, "sRGB"),
        ("c_avifsvt_444", OutputFormat.AVIF, "avif", AvifEncoderKind.SvtAv1, 60f, "444", 10, "sRGB"),
        ("c_avifnv_444", OutputFormat.AVIF, "avif", AvifEncoderKind.Nvenc,  60f, "444", 10, "sRGB"),
        // ═══ 2026-09-19 色度扫描扩到非 AVIF 格式 ═══
        // 动机：AVIF 上刚抓到"参数写了没人读/静默降级"，同样的问题必须逐格式验。
        // 判据两条：① 只有色度不同的两张样张**字节完全相同** ⇒ 这个开关根本没进编码器；
        //          ② ffprobe 读回的 pix_fmt 是否等于请求值（不能只信我们自己的解析）。
        ("c_jpegli_420", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto, 1.0f, "420", 8, "sRGB"),
        ("c_jpegli_422", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto, 1.0f, "422", 8, "sRGB"),
        ("c_jpegli_444", OutputFormat.JPEG_LI, "jpg", AvifEncoderKind.Auto, 1.0f, "444", 8, "sRGB"),
        ("c_jxl_420",    OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "420", 10, "sRGB"),
        ("c_jxl_444",    OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 10, "sRGB"),
        ("c_webp_420",   OutputFormat.WebP, "webp", AvifEncoderKind.Auto,   90f, "420", 8, "sRGB"),
        ("c_webp_444",   OutputFormat.WebP, "webp", AvifEncoderKind.Auto,   90f, "444", 8, "sRGB"),
        ("c_gm_420",     OutputFormat.JPEG_GAINMAP, "jpg", AvifEncoderKind.Auto, 1.0f, "420", 8, "sRGB"),
        ("c_gm_444",     OutputFormat.JPEG_GAINMAP, "jpg", AvifEncoderKind.Auto, 1.0f, "444", 8, "sRGB"),
        ("c_tiff_420",   OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto,  100f, "420", 8, "sRGB"),
        ("c_tiff_444",   OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto,  100f, "444", 8, "sRGB"),
        // ── 位深扫描 ──
        // ═══ 2026-09-19（§29 P1-1/P1-2 的判据）：同一张图只在"输出位深"上不同 ═══
        // 断言两件事：① 广色域矩阵**与位深无关**（两档 cICP primaries 必须同为 12）；
        //             ② 两档像素只差最后一次量化（≤1 LSB）。
        // 旧实现里 8-bit 分支根本不做矩阵 ⇒ 第 ① 条必红；这正是这组样张存在的理由。
        ("gd_png_p3_d8",  OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 8,  "DisplayP3"),
        ("gd_png_p3_d16", OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 16, "DisplayP3"),
        ("gd_jxl_p3_d8",  OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 8,  "DisplayP3"),
        ("gd_jxl_p3_d16", OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 16, "DisplayP3"),
        ("gd_tiff_p3_d8",  OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto, 100f, "444", 8,  "DisplayP3"),
        ("gd_tiff_p3_d16", OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto, 100f, "444", 16, "DisplayP3"),
        ("d_png_8",  OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 8,  "sRGB"),
        ("d_png_10", OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 10, "sRGB"),
        ("d_png_12", OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 12, "sRGB"),
        ("d_png_16", OutputFormat.PNG,  "png",  AvifEncoderKind.Auto, 100f, "444", 16, "sRGB"),
        ("d_tiff_8",  OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto, 100f, "444", 8,  "sRGB"),
        ("d_tiff_10", OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto, 100f, "444", 10, "sRGB"),
        ("d_tiff_16", OutputFormat.TIFF, "tiff", AvifEncoderKind.Auto, 100f, "444", 16, "sRGB"),
        ("d_avifsw_8",  OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "444", 8,  "sRGB"),
        ("d_avifsw_12", OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "444", 12, "sRGB"),
        ("d_avifnv_10", OutputFormat.AVIF, "avif", AvifEncoderKind.Nvenc,  60f, "420", 10, "sRGB"),
        ("d_jxl_16", OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 16, "sRGB"),
        // JXL 无 -b 参数：位深只决定中转格式（PPM vs 16-bit PNG）⇒ 10 与 12 应产出同一文件
        ("d_jxl_10", OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 10, "sRGB"),
        ("d_jxl_12", OutputFormat.JPEG_XL, "jxl", AvifEncoderKind.Auto, 1.0f, "444", 12, "sRGB"),
        // ── 色域扫描（cICP / iCCP 是否按 tag 写）──
        ("cs_png_srgb",   OutputFormat.PNG, "png",  AvifEncoderKind.Auto, 100f, "444", 10, "sRGB"),
        ("cs_png_p3",     OutputFormat.PNG, "png",  AvifEncoderKind.Auto, 100f, "444", 10, "DisplayP3"),
        ("cs_png_bt2020", OutputFormat.PNG, "png",  AvifEncoderKind.Auto, 100f, "444", 10, "BT2020"),
        ("cs_png_adobe",  OutputFormat.PNG, "png",  AvifEncoderKind.Auto, 100f, "444", 10, "AdobeRGB"),
        ("cs_avif_p3",    OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "444", 10, "DisplayP3"),
        // ⚠ 关键一格：AdobeRGB 在 H.273 无码点 ⇒ 软件路径故意跳过 --cicp，色彩只能靠 ICC 声明。
        //   而 NativeAvifEncoder.EncodeFile 没有 iccProfile 形参，ICC 只在 16-bit 中转 PNG 里
        //   ⇒ 若 avifenc 不把中转 PNG 的 iCCP 带进容器，这张图就【完全没有色彩声明】。
        ("cs_avif_adobe", OutputFormat.AVIF, "avif", AvifEncoderKind.LibAom, 60f, "444", 10, "AdobeRGB"),
    };

    foreach (var (name, fmt, ext, kind, q, chroma, depth, tag) in rows)
    {
        var s = new EncodingSettings
        {
            Format = fmt, Quality = q, HdrOutput = false,
            // 目标标签先过【产品那一份收口】再编码：AdobeRGB ⇒ BT2020（2026-09-27 用户口径）。
            // 宿主以前原样塞 tag，等于让判据去判一个界面产不出来的形态 —— 七个容器各自还留着
            // 自己的 Adobe 出路，于是"改一处收口"就红成跨容器不一致。现在两侧同源的收口。
            ColorSpaceTag = TrueToneCap.Core.ColorManagement.ColorProfileProvider.MapAdobeTargetToWide(tag),
            OutputBitDepth = depth, ChromaSubsampling = chroma, AvifEncoderKind = kind,
        };
        var path = Path.Combine(sdir, name + "." + ext);
        try
        {
            await EncoderFactory.Create(fmt).EncodeAsync(
                new HdrFrameData { Pixels = px, Width = SW, Height = SH }, s, path);
            Console.WriteLine($"  ✅ {name,-16} q={q,-5} {chroma} d{depth,-3} {tag,-8} {new FileInfo(path).Length,9} B");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {name,-16} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }
    Console.WriteLine($"\n✅ 扫描样张已产出 → {sdir}\n   下一步: py scripts/precision/param_sweep_check.py");
}

// ═══ 2026-09-19 §29 代理指标②：高精度源 → 【生产编码器全链】→ 数"实际解出多少级" ═══
// 为什么 --param-sweep 不够：它的样张是平滑渐变 + 纹理，能验"参数有没有进码流""两档位深是否只差
// 最后一次量化"，但【看不出容器实际交付了几级】—— 渐变相邻级差好几个 LSB，8-bit 也照样"看着连续"。
// 这里换成 64 个**紧邻台阶**（相邻带在 10-bit 刻度上只差 1）：
//   · 真给到 ≥10-bit ⇒ 64 级基本都数得出来；
//   · 中途被钳回 8-bit ⇒ 相邻台阶并进同一级，只剩十几个。
// 同一条判据此前只有裸 NVENC 探针有（CorePipelineTests 的 ARGB10 台阶判据），本命令把它推到
// EncoderFactory 的完整生产路径上，逐格式逐位深各一张。判据在 scripts/precision/ramp_levels.py。
if (args.Length >= 1 && args[0] == "--ramp-sweep")
{
    const int Bands = 64, RowsPerBand = 8, RW = 256;
    const int RH = Bands * RowsPerBand;
    var px = new float[RW * RH * 4];

    // 台阶放在中间调（10-bit 编码值 256..319）。为什么不能贴着 0 或 1：
    // sRGB 曲线在暗端是线性段（间隔被放大）、亮端是平台（间隔被压缩），两端都会让 8-bit"看起来分得开"
    // ⇒ 判据失去分辨力。中间调处每 1/1024 的差别只值 ~0.4 个 8-bit LSB，对"精度止步 8-bit"最苛刻。
    static double Decode(double e)
        => e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);
    for (int b = 0; b < Bands; b++)
    {
        // 生产入口吃的是【线性 scRGB float】⇒ 先按编码域定台阶，再反解成线性值
        float lin = (float)Decode((256.0 + b) / 1023.0);
        for (int y = b * RowsPerBand; y < (b + 1) * RowsPerBand; y++)
            for (int x = 0; x < RW; x++)
            {
                int i = (y * RW + x) * 4;
                px[i] = lin; px[i + 1] = lin; px[i + 2] = lin; px[i + 3] = 1f;
            }
    }

    string rdir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "artifacts", "precision", "probe", "ramp"));
    Directory.CreateDirectory(rdir);

    // 名字里带 nv 的是硬件通路：无 GPU / 无 NVENC 时下面会 ❌，判据侧按 SKIP 处理（不算失败）
    var rows = new (string Name, OutputFormat Fmt, string Ext, AvifEncoderKind Kind, float Q, string Chroma, int Depth)[]
    {
        ("r_png_d8",     OutputFormat.PNG,     "png",  AvifEncoderKind.Auto,   100f, "444", 8),
        ("r_png_d10",    OutputFormat.PNG,     "png",  AvifEncoderKind.Auto,   100f, "444", 10),
        ("r_png_d16",    OutputFormat.PNG,     "png",  AvifEncoderKind.Auto,   100f, "444", 16),
        ("r_tiff_d8",    OutputFormat.TIFF,    "tiff", AvifEncoderKind.Auto,   100f, "444", 8),
        ("r_tiff_d16",   OutputFormat.TIFF,    "tiff", AvifEncoderKind.Auto,   100f, "444", 16),
        ("r_jxl_d8",     OutputFormat.JPEG_XL, "jxl",  AvifEncoderKind.Auto,   1.0f, "444", 8),
        ("r_jxl_d16",    OutputFormat.JPEG_XL, "jxl",  AvifEncoderKind.Auto,   1.0f, "444", 16),
        ("r_avifsw_d8",  OutputFormat.AVIF,    "avif", AvifEncoderKind.LibAom, 60f,  "444", 8),
        ("r_avifsw_d10", OutputFormat.AVIF,    "avif", AvifEncoderKind.LibAom, 60f,  "444", 10),
        ("r_avifnv_d8",  OutputFormat.AVIF,    "avif", AvifEncoderKind.Nvenc,  60f,  "420", 8),
        ("r_avifnv_d10", OutputFormat.AVIF,    "avif", AvifEncoderKind.Nvenc,  60f,  "420", 10),
    };

    foreach (var (name, fmt, ext, kind, q, chroma, depth) in rows)
    {
        var s = new EncodingSettings
        {
            Format = fmt, Quality = q, HdrOutput = false, ColorSpaceTag = "sRGB",
            OutputBitDepth = depth, ChromaSubsampling = chroma, AvifEncoderKind = kind,
        };
        var path = Path.Combine(rdir, name + "." + ext);
        try
        {
            await EncoderFactory.Create(fmt).EncodeAsync(
                new HdrFrameData { Pixels = px, Width = RW, Height = RH }, s, path);
            Console.WriteLine($"  ✅ {name,-14} {chroma} d{depth,-3} {new FileInfo(path).Length,9} B");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {name,-14} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }
    Console.WriteLine($"\n✅ 台阶样张已产出 → {rdir}\n   下一步: py scripts/precision/ramp_levels.py");
}

// ═══ 同一内容跨格式/跨后端 · 色彩声明一致性样张（2026-09-19 §29 代理指标④）═══
// 判据在 scripts/precision/cicp_consistency.py：把每张样张的 cICP / colr / ICC 用外部工具读回来，
// 按 tag 分组断言"同一张图在各容器里的声明不许自相矛盾"。
// 为什么 --param-sweep 不够：它判的是【每一行等不等于被告知的值】，这条判【行与行之间矛不矛盾】。
// 两者不等价，漏掉的那半正好会出事故 —— 每行各自都"达标"，但 PNG 写 primaries=12、
// AVIF 写 12 又叠一份 ICC、WebP 只带 ICC 而 ffprobe 映射成 1，拼起来就是同一张截图
// 在三个查看器里三种颜色；逐行看谁都没错，只有跨行比对能红。
// 所以本模式的硬要求是：**像素内容逐字节相同，只有格式与色域 tag 不同**。
// 文件名即契约（cc_<fmt>_<tag>.<ext>）：判据只靠解析文件名取格式与 tag，改名字等于改判据。
if (args.Length >= 1 && args[0] == "--cicp-sweep")
{
    const int CW = 64, CH = 64, Cell = 8;   // 64/8 = 8 个色块宽，(64/2)/8 = 4 个明度档
    var px = new float[CW * CH * 4];

    // 护栏，不是装饰：源是线性 scRGB float（1.0 = SDR 白），必须非负且 ≤1。
    // 生产浮点链是 色域矩阵 → 分段 Reinhard → gamma → 量化一次，y≤1 段直通；
    // 一旦留了负值或 >1 的爆值，高光压缩与裁剪就会按各格式不同的量化路径给结果 ——
    // 那部分差异跟色彩声明毫无关系，却把"跨格式逐像素比对"这半条判据污染成噪声。
    static float Chan(float v) => Math.Clamp(Math.Abs(v), 0f, 1f);

    // 图案分两类，缺一不可：上半中性灰阶、下半高饱和原色块。
    // 理由是矩阵的作用面：scRGB→目标色域的 3×3 每【行】行和为 1 ⇒ 中性轴 (v,v,v) 乘完不变，
    // 只有有彩像素必然被搬动（实测最大搬幅 0.18~0.37 线性，sRGB 纯红 → P3 的 0.822/0.033/0.017）。
    // 于是判据能分开两问：
    //   中性像素 ⇒ "不该动的有没有动"（tag 被覆写回 sRGB、矩阵被跳过，会让中性跟着变）；
    //   有彩像素 ⇒ "该动的有没有动"（容器声明 DisplayP3 而像素仍是 sRGB = 撒谎的 cICP）。
    // 只做其一都会漏：全灰看不出矩阵没跑，全彩分不清差异来自声明还是来自编码噪声。
    // "不变"是到 ~2e-5 相对量（三张矩阵里行和最偏的是 AdobeRGB 的 1.000018），
    // 比 10-bit 一级（≈1e-3）还低两个量级 —— 判据给中性像素留这个量级的容差即可，
    // 再松就松到能放过"矩阵真的没跑"（那一档差异是 0.18 量级），就失去分辨力了。
    // 色块取纯色且整块平坦：4:2:0 与有损通路下块中心噪声底极低，跨格式比对才有分辨力。
    var chip = new (float R, float G, float B)[]
    {
        (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f), (1f, 0f, 1f),
        (1f, 1f, 0f), (0f, 1f, 1f), (1f, 0.08f, 0.02f), (0.05f, 0.06f, 1f),
    };
    // 四档明度都留在直通段内（不触高光压缩），暗档也不能太暗：
    // 矩阵搬动的绝对幅度随像素值线性缩放，太暗就没有一格能跨过有损通路的噪声底。
    var level = new float[] { 1f, 0.72f, 0.5f, 0.3f };

    for (int y = 0; y < CH; y++)
        for (int x = 0; x < CW; x++)
        {
            float r, g, b;
            if (y < CH / 2)
            {
                // 偶数行铺 64 级连续渐变（跨格式比单调性），奇数行铺 8 级平坦平台（比逐点数值）
                float v = (y & 1) == 0 ? (float)x / (CW - 1) : (float)(x / Cell) / (CW / Cell - 1);
                r = g = b = v;
            }
            else
            {
                var c = chip[x / Cell];
                float k = level[(y - CH / 2) / Cell];
                r = c.R * k; g = c.G * k; b = c.B * k;
            }
            int i = (y * CW + x) * 4;
            px[i] = Chan(r); px[i + 1] = Chan(g); px[i + 2] = Chan(b); px[i + 3] = 1f;
        }

    string cdir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "artifacts", "precision", "probe", "cicp"));
    Directory.CreateDirectory(cdir);

    // 行表由【格式表 × tag 表】投影而成，而不是手写 28 行：文件名是判据唯一的取参入口，
    // 手抄 28 次就有 28 处能把 fmt / ext / tag 配错，而配错的样张在判据里读起来
    // 完全像一个真实缺陷（比如 .jpg 后缀装了 WebP 码流），排查成本远高于一张表。
    var fmts = new (string Key, OutputFormat Fmt, string Ext, AvifEncoderKind Kind, float Q)[]
    {
        ("png",    OutputFormat.PNG,     "png",  AvifEncoderKind.Auto,   100f),
        ("tiff",   OutputFormat.TIFF,    "tiff", AvifEncoderKind.Auto,   100f),
        ("jxl",    OutputFormat.JPEG_XL, "jxl",  AvifEncoderKind.Auto,   1.0f),   // Quality 是 butteraugli 距离
        ("jpegli", OutputFormat.JPEG_LI, "jpg",  AvifEncoderKind.Auto,   1.0f),
        ("webp",   OutputFormat.WebP,    "webp", AvifEncoderKind.Auto,   90f),
        ("avifsw", OutputFormat.AVIF,    "avif", AvifEncoderKind.LibAom, 60f),
        // 硬件通路：无 GPU / 无 NVENC AVIF 时【整行】抛异常。这是预期内的可失败行，
        // 必须走下面的 try/catch 只打 ❌ 继续 —— 它不能把其余 24 张软件样张一起带崩，
        // 判据侧对 avifnv 一行按 SKIP 处理（缺产物不算失败，但软件行缺产物算回归）。
        ("avifnv", OutputFormat.AVIF,    "avif", AvifEncoderKind.Nvenc,  60f),
    };
    // adobe 这一格 2026-09-27 起走【产品同一份收口】（用户口径：AdobeRGB 塞进 Rec.2020）。
    // 以前这里是把 "AdobeRGB" 原样塞进 EncodingSettings，绕过了 ColorProfileProvider
    // .CollapseSelectableTag —— 而产品路径（CapturePipelineService 用 ColorDecision 解析）
    // 永远拿不到 Adobe 这个目标值。结果是"样张判的是一个用户根本做不到的形态"：
    // 七个容器里 Adobe 各有出路（PNG/AVIF 靠 ICC、JXL 原先退 sRGB），改一处就红一片。
    // 现在收口后再编码：adobe 列应当与 bt2020 列同声明（primaries=9），
    // 这一格因此变成"收口有没有在每个容器里都生效"的回归位，而不是第四种口径。
    var tags = new (string Key, string Tag)[]
    {
        ("srgb",   "sRGB"),
        ("p3",     "DisplayP3"),
        ("bt2020", "BT2020"),
        ("adobe",  "AdobeRGB"),
    };

    // 28 次编码共用同一个 px 数组（浮点链只读它、矩阵段另起新数组）⇒ 内容逐字节相同。
    // 色度/位深固定 444 / 10：本模式只动"格式 + 声明"这两个自变量，其余旋钮一律钉死，
    // 否则跨行差异既有来自声明的、又有来自码流参数的，判据就分不出是谁错了。
    var rows = new List<(string Name, OutputFormat Fmt, string Ext, AvifEncoderKind Kind,
        float Q, string Chroma, int Depth, string Tag)>(fmts.Length * tags.Length);
    foreach (var f in fmts)
        foreach (var t in tags)
            rows.Add(($"cc_{f.Key}_{t.Key}", f.Fmt, f.Ext, f.Kind, f.Q, "444", 10, t.Tag));

    foreach (var (name, fmt, ext, kind, q, chroma, depth, tag) in rows)
    {
        var s = new EncodingSettings
        {
            Format = fmt, Quality = q, HdrOutput = false,
            // 目标标签先过【产品那一份收口】再编码：AdobeRGB ⇒ BT2020（2026-09-27 用户口径）。
            // 宿主以前原样塞 tag，等于让判据去判一个界面产不出来的形态 —— 七个容器各自还留着
            // 自己的 Adobe 出路，于是"改一处收口"就红成跨容器不一致。现在两侧同源的收口。
            ColorSpaceTag = TrueToneCap.Core.ColorManagement.ColorProfileProvider.MapAdobeTargetToWide(tag),
            OutputBitDepth = depth, ChromaSubsampling = chroma, AvifEncoderKind = kind,
        };
        var path = Path.Combine(cdir, name + "." + ext);
        try
        {
            await EncoderFactory.Create(fmt).EncodeAsync(
                new HdrFrameData { Pixels = px, Width = CW, Height = CH }, s, path);
            Console.WriteLine($"  ✅ {name,-20} {ext,-5} {tag,-10} q={q,-5} {chroma} d{depth,-3} {new FileInfo(path).Length,9} B");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {name,-20} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }
    Console.WriteLine($"\n✅ 声明一致性样张已产出 → {cdir}\n   下一步: py scripts/precision/cicp_consistency.py");
}
