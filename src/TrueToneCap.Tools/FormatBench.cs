// TrueToneCap.Tools/FormatBench.cs
// 全格式编码性能基准测试 (控制台)
// 用法: dotnet run --project src/TrueToneCap.Tools/FormatBench.csproj

using System.Diagnostics;
using TrueToneCap.Core.Encoding;

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

// ═══ 全格式测试 ═══
var formats = new (OutputFormat Fmt, string Name, float Quality, bool Hdr, int TimeoutS)[]
{
    (OutputFormat.PNG,          "PNG (无损)",          100, false, 10),
    (OutputFormat.JPEG_LI,       "JPEG LI",             2.0f, false, 10),
    (OutputFormat.WebP,          "WebP",                90f, false, 10),
    (OutputFormat.AVIF,          "AVIF",                30f, false, 60),
    (OutputFormat.JPEG_XL,       "JPEG XL",             2.0f, false, 60),
    (OutputFormat.JPEG_GAINMAP,  "JPEG GainMap (HDR)",  90f, true,  60),
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
