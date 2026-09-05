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
            AvifChroma = "444",
            DisplayBitDepth = 8,
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
