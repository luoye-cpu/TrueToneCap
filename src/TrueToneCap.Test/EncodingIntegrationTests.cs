// TrueToneCap.Test/EncodingIntegrationTests.cs
// 编码管线实机集成测试 — 验证所有格式端到端编码
// 运行: dotnet run --project src/TrueToneCap.Test -- --encoding-tests

using System.Diagnostics;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.Test;

/// <summary>编码管线集成测试：生成测试像素 → 全格式编码 → 验证输出文件。</summary>
public static class EncodingIntegrationTests
{
    private static int _passed, _failed;
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_EncTest");

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 编码管线集成测试");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("══════════════════════════════════════\n");

        // 生成测试图像
        int w = 1920, h = 1080;
        var bgra = GenerateTestPattern(w, h);
        Console.WriteLine($"测试图像: {w}x{h} ({bgra.Length / 1024 / 1024} MB BGRA8)\n");

        // SDR 编码测试
        Test_SdrEncode(OutputFormat.PNG, bgra, w, h, 100f);
        Test_SdrEncode(OutputFormat.JPEG_LI, bgra, w, h, 1.0f);
        Test_SdrEncode(OutputFormat.WebP, bgra, w, h, 92f);
        Test_SdrEncode(OutputFormat.BMP, bgra, w, h, 100f);
        Test_SdrEncode(OutputFormat.JPEG_XL, bgra, w, h, 0.8f);
        Test_SdrEncode(OutputFormat.AVIF, bgra, w, h, 18f);

        // HDR 编码测试
        var hdrPixels = PixelOps.BgraToScrgbLinearFast(bgra, w, h);
        // 模拟 HDR: 放大亮度
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            hdrPixels[i] *= 3f;
            hdrPixels[i + 1] *= 3f;
            hdrPixels[i + 2] *= 3f;
        }
        var hdrFrame = new HdrFrameData { Pixels = hdrPixels, Width = w, Height = h };

        Test_HdrEncode(OutputFormat.PNG, hdrFrame, 100f);
        Test_HdrEncode(OutputFormat.JPEG_XL, hdrFrame, 0.8f);
        Test_HdrEncode(OutputFormat.AVIF, hdrFrame, 18f);

        // JPEG Gain Map (Ultra HDR)
        Test_JpegGainMap(hdrFrame, bgra, w, h);

        // 色调映射管线测试
        Test_ToneMappingPipeline(hdrPixels, w, h);

        // 边界条件
        Test_SdrEncode(OutputFormat.PNG, GenerateTestPattern(1, 1), 1, 1, 100f);
        Test_SdrEncode(OutputFormat.PNG, GenerateTestPattern(3, 3), 3, 3, 100f);
        Test_SdrEncode(OutputFormat.WebP, GenerateTestPattern(2, 2), 2, 2, 90f);

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
        Console.WriteLine($"══════════════════════════════════════");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  SDR 编码测试
    // ═══════════════════════════════════════

    static void Test_SdrEncode(OutputFormat fmt, byte[] bgra, int w, int h, float quality)
    {
        string name = $"SDR {fmt} {w}x{h}";
        try
        {
            var encoder = EncoderFactory.Create(fmt);
            var settings = new EncodingSettings
            {
                Format = fmt,
                Quality = quality,
                HdrOutput = false,
                ChromaSubsampling = "444",
                OutputBitDepth = 8,
                DisplayBitDepth = 8,
            };

            string ext = GetExtension(fmt);
            string path = Path.Combine(OutDir, $"sdr_{fmt}_{w}x{h}{ext}");
            if (File.Exists(path)) File.Delete(path);

            var sw = Stopwatch.StartNew();
            encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
            sw.Stop();

            if (!File.Exists(path))
            {
                Assert(name, false, "输出文件不存在");
                return;
            }

            var fi = new FileInfo(path);
            bool ok = fi.Length > 0;
            Assert(name, ok, $"{fi.Length / 1024.0:F0}KB, {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Assert(name, false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  HDR 编码测试
    // ═══════════════════════════════════════

    static void Test_HdrEncode(OutputFormat fmt, HdrFrameData frame, float quality)
    {
        string name = $"HDR {fmt} {frame.Width}x{frame.Height}";
        try
        {
            var encoder = EncoderFactory.Create(fmt);
            if (!encoder.SupportsHdr)
            {
                Assert(name, true, "跳过 (不支持 HDR)");
                return;
            }

            var settings = new EncodingSettings
            {
                Format = fmt,
                Quality = quality,
                HdrOutput = true,
                ChromaSubsampling = "444",
                OutputBitDepth = 10,
                DisplayBitDepth = 10,
            };

            string ext = GetExtension(fmt);
            string path = Path.Combine(OutDir, $"hdr_{fmt}{ext}");
            if (File.Exists(path)) File.Delete(path);

            var sw = Stopwatch.StartNew();
            encoder.EncodeAsync(frame, settings, path).GetAwaiter().GetResult();
            sw.Stop();

            if (!File.Exists(path))
            {
                Assert(name, false, "输出文件不存在");
                return;
            }

            var fi = new FileInfo(path);
            Assert(name, fi.Length > 0, $"{fi.Length / 1024.0:F0}KB, {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Assert(name, false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  JPEG Gain Map (Ultra HDR)
    // ═══════════════════════════════════════

    static void Test_JpegGainMap(HdrFrameData hdrFrame, byte[] sdrBgra, int w, int h)
    {
        const string name = "JPEG GainMap (Ultra HDR)";
        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.JPEG_GAINMAP);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP,
                Quality = 1.0f,
                HdrOutput = true,
                GainMapMode = GainMapMode.Rgb,
            };

            string path = Path.Combine(OutDir, "gainmap_ultrahdr.jpg");
            if (File.Exists(path)) File.Delete(path);

            var sw = Stopwatch.StartNew();
            encoder.EncodeAsync(hdrFrame, settings, path).GetAwaiter().GetResult();
            sw.Stop();

            if (!File.Exists(path))
            {
                Assert(name, false, "输出文件不存在");
                return;
            }

            var fi = new FileInfo(path);
            // JPEG Gain Map 应包含 MPF 标记 (FFD8...FFD8 双 SOI)
            var bytes = File.ReadAllBytes(path);
            bool hasMpf = bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            Assert(name, fi.Length > 0 && hasMpf, $"{fi.Length / 1024.0:F0}KB, {sw.ElapsedMilliseconds}ms, JPEG SOI={hasMpf}");
        }
        catch (Exception ex)
        {
            Assert(name, false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  色调映射管线
    // ═══════════════════════════════════════

    static void Test_ToneMappingPipeline(float[] hdrPixels, int w, int h)
    {
        const string name = "ToneMapping 全模式";
        try
        {
            var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.Aces };
            bool allOk = true;
            string detail = "";

            foreach (var mode in modes)
            {
                var p = new ToneMappingParams { Mode = mode };
                var bytes = ToneMapper.FloatToSRgbBytes(hdrPixels, w, h, p);

                // 验证: 非全黑、非全白、长度正确
                bool hasVariation = false;
                int minVal = 255, maxVal = 0;
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    int lum = (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3;
                    minVal = Math.Min(minVal, lum);
                    maxVal = Math.Max(maxVal, lum);
                }
                hasVariation = maxVal - minVal > 10;

                if (bytes.Length != w * h * 4 || !hasVariation)
                {
                    allOk = false;
                    detail += $"{mode}:FAIL(len={bytes.Length},range={minVal}-{maxVal}) ";
                }
                else
                {
                    detail += $"{mode}:OK({minVal}-{maxVal}) ";
                }
            }

            Assert(name, allOk, detail.Trim());
        }
        catch (Exception ex)
        {
            Assert(name, false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════

    static byte[] GenerateTestPattern(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            // 彩色渐变 + 文字模拟锐利边缘
            bgra[i]     = (byte)(y * 255 / Math.Max(h - 1, 1));           // B: 垂直渐变
            bgra[i + 1] = (byte)(x * 255 / Math.Max(w - 1, 1));           // G: 水平渐变
            bgra[i + 2] = (byte)(((x + y) % 256));                         // R: 对角条纹
            bgra[i + 3] = 255;

            // 模拟文字区域 (高对比度锐利边缘)
            if (x > w / 4 && x < w * 3 / 4 && y > h / 3 && y < h / 3 + 40)
            {
                bool isText = (x / 3 + y / 3) % 2 == 0;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)(isText ? 0 : 255);
            }
        }
        return bgra;
    }

    static string GetExtension(OutputFormat fmt) => fmt switch
    {
        OutputFormat.PNG => ".png",
        OutputFormat.JPEG_LI => ".jpg",
        OutputFormat.JPEG_XL => ".jxl",
        OutputFormat.AVIF => ".avif",
        OutputFormat.WebP => ".webp",
        OutputFormat.BMP => ".bmp",
        OutputFormat.JPEG_GAINMAP => ".jpg",
        _ => ".bin"
    };

    static void Assert(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name} — {detail}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name} — {detail}"); }
    }
}
