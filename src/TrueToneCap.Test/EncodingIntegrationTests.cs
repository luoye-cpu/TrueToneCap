// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
    /// <summary>因环境不满足而【显式跳过】的用例数 —— 既不算通过也不算失败。</summary>
    private static int _skipped;
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_EncTest");

    /// <summary>显式跳过一条用例：不计入通过/失败，但必须留下原因（禁止静默通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    public static int RunAll()
    {
        _passed = 0; _failed = 0; _skipped = 0;
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
        Test_SdrEncode(OutputFormat.TIFF, bgra, w, h, 100f);
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

        // 无损编码形状（2026-10-31）：流式 IDAT 的结构判据 + 给外部裁判落夹具
        Test_LosslessShapesAndExternalFixtures();

        // 边界条件
        Test_SdrEncode(OutputFormat.PNG, GenerateTestPattern(1, 1), 1, 1, 100f);
        Test_SdrEncode(OutputFormat.PNG, GenerateTestPattern(3, 3), 3, 3, 100f);
        Test_SdrEncode(OutputFormat.WebP, GenerateTestPattern(2, 2), 2, 2, 90f);

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--encoding-tests", _passed, _failed, 0, null, _skipped);
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
                Skip(name, "编码器不支持 HDR（能力限制）");
                return;
            }

            var settings = new EncodingSettings
            {
                Format = fmt,
                Quality = quality,
                HdrOutput = true,
                ChromaSubsampling = "444",
                OutputBitDepth = 10,
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
            // JPEG Gain Map 应包含双 SOI（Base + 增益图）
            var bytes = File.ReadAllBytes(path);
            int soiCount = 0;
            for (int i = 0; i < bytes.Length - 1; i++)
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8) soiCount++;
            bool hasGainMap = soiCount >= 2;

            // 验证 XMP 段完整（含 hdrgm:Version）
            // 注意: ICC 走 APP2（2026-09-20 修正，见 UsabilityTests.Jpeg_IccLargeProfile），
            //       XMP 才是 APP1；这里遍历所有 APP1 段直到找到 hdrgm XMP。
            bool hasXmpFields = false;
            bool hasIsoMetadata = false;
            for (int i = 0; i < Math.Min(bytes.Length - 4, 20000); i++)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xE1)
                {
                    int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (segLen > 100 && i + 4 + segLen - 2 <= bytes.Length)
                    {
                        var xmp = System.Text.Encoding.UTF8.GetString(bytes, i + 4, segLen - 2);
                        if (xmp.Contains("hdrgm:Version") && xmp.Contains("hdrgm:GainMapMax"))
                        {
                            hasXmpFields = true;
                            break;
                        }
                    }
                    // 跳过该段继续 (不 break, 可能前有 ICC_PROFILE)
                    i += 2 + segLen;
                }
            }
            // ISO 21496-1 二进制元数据: 位于增益图 JPEG (第二个 SOI) 之后, APP2 段命名空间
            for (int i = 0; i < bytes.Length - 40; i++)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xE2)
                {
                    int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (segLen > 30)
                    {
                        var payload = System.Text.Encoding.ASCII.GetString(bytes, i + 4, Math.Min(segLen - 2, 40));
                        if (payload.Contains("urn:iso:std:iso:ts:21496:-1"))
                        {
                            hasIsoMetadata = true;
                            break;
                        }
                    }
                }
            }

            // 检查 Base JPEG 的 SOF 类型（优先 Baseline SOF0=0xC0，但回退路径可接受 SOF1）
            int baseSof = 0;
            for (int i = 2; i < Math.Min(bytes.Length - 2, 2000); i++)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] >= 0xC0 && bytes[i + 1] <= 0xCF)
                { baseSof = bytes[i + 1]; break; }
            }
            bool isBaseline = baseSof == 0xC0;
            Console.WriteLine($"  [SOF] Base SOF=0x{baseSof:X2} ({(isBaseline ? "Baseline ✓" : "非Baseline, 可接受")})");
            // 关键判据: 输出含增益图（双 SOI）。SOF 类型可选。
            bool ok = fi.Length > 0 && hasGainMap && hasXmpFields && hasIsoMetadata;
            Assert(name, ok, $"{fi.Length / 1024.0:F0}KB, 增益图={hasGainMap}, XMP={hasXmpFields}, ISO={hasIsoMetadata}, Baseline={isBaseline}");
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
            var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
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
        // TIFF 的产物扩展名过去写成 ".bmp"（BMP 编码器专项测试的残留），
        // 文件内容一直是真 TIFF ⇒ 现在按真实格式命名，免得产物目录里出现"名叫 bmp 的 TIFF"。
        OutputFormat.TIFF => ".tif",
        OutputFormat.JPEG_GAINMAP => ".jpg",
        _ => ".bin"
    };

    // ═══════════════════════════════════════
    //  无损编码形状（2026-10-31）：流式 IDAT / 分条 TIFF
    // ═══════════════════════════════════════

    /// <summary>
    /// 流式 IDAT 的结构判据 + 给外部裁判（Pillow/tifffile）留夹具。
    /// <para>为什么要单独一条：IDAT 现在按 64 KiB 载荷切成多条，拼接后的 zlib 码流才是图像数据。
    /// "看起来能打开"只说明有解码器肯试 ⇒ 这一条钉三件事：① 确实跨了多条；② 除末条外每条恰 64 KiB
    /// （边界写歪一位就是整幅图错位）；③ 每条的 CRC 由<b>本文件自带的另一份 CRC32</b>复算，
    /// 不借编码器自己那套 CRC ⇒ 编解码同错时这条照样红（审查点名的"自洽 CRC"缺口）。</para>
    /// <para>正控（<see cref="Crc32Test"/> 与翻转字节）：改掉某条 IDAT 的一个字节，
    /// ① 复算 CRC 必须判红，② 拼接后解压必须失败或长度不符 ⇒ 证明多条载荷是【真被拼接使用】的，
    /// 而不是没人读的填充。</para>
    /// </summary>
    static void Test_LosslessShapesAndExternalFixtures()
    {
        const int W = 320, H = 240;
        // 夹具刻意用"压不动"的伪随机料：结构化图案（渐变/文字）压到 <64 KiB 时只会落一条 IDAT，
        // 那样上面三条判据一个都看不见东西 —— 夹具必须比真实素材更苛刻。
        var px = new byte[W * H * 4];
        uint seed = 0x1234ABCD;
        for (int i = 0; i < px.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            px[i] = (byte)(seed >> 24);
        }

        string pngPath = Path.Combine(OutDir, "png_multi_idat.png");
        ManagedPngEncoder.Encode(px, W, H, pngPath, 8);
        var bytes = File.ReadAllBytes(pngPath);

        var (ok, chunks) = SplitPngChunks(bytes);
        Assert("流式 IDAT：PNG 分块能被逐块切开（切不开=判据失明，后面全不算数）", ok,
            ok ? $"{chunks.Count} 条 chunk" : "分块形状不合法");

        var idats = chunks.Where(c => c.Type == "IDAT").ToList();
        Assert("流式 IDAT：产物确实跨多条 IDAT（只有一条时下面两格看不见东西）", idats.Count >= 2,
            $"实得 {idats.Count} 条");
        Assert("流式 IDAT：除末条外每条载荷恰为 64 KiB（边界歪一位=整幅图错位）",
            idats.Count >= 2 && idats.Take(idats.Count - 1).All(c => c.Data.Length == 64 * 1024),
            string.Join("/", idats.Select(c => c.Data.Length)));
        Assert("流式 IDAT：IHDR 打头、IEND 收尾",
            chunks.Count >= 3 && chunks[0].Type == "IHDR" && chunks[^1].Type == "IEND",
            chunks.Count > 0 ? $"{chunks[0].Type}…{chunks[^1].Type}" : "空");

        // ③ 独立 CRC：这份实现只服务判据，与编码器同错就会露出来（表值 0xEDB88320 是公开的 zlib 多项式）
        Assert("流式 IDAT：每条 chunk 的 CRC 用测试自带的另一份 CRC32 复算全对",
            ok && chunks.All(c => Crc32Test(c.TypeBytes, c.Data) == c.Crc),
            ok ? string.Join(",", chunks.Where(c => Crc32Test(c.TypeBytes, c.Data) != c.Crc)
                    .Select(c => c.Type)) : "分块已不合法");

        var payload = new byte[idats.Sum(c => (long)c.Data.Length)];
        int at = 0;
        foreach (var c in idats) { Buffer.BlockCopy(c.Data, 0, payload, at, c.Data.Length); at += c.Data.Length; }
        long inflatedLen;
        try
        {
            using var zs = new System.IO.Compression.ZLibStream(
                new MemoryStream(payload), System.IO.Compression.CompressionMode.Decompress);
            using var sink = new MemoryStream();
            zs.CopyTo(sink);
            inflatedLen = sink.Length;
        }
        catch (Exception ex) { inflatedLen = -1; Console.WriteLine($"  ⚠ 拼接码流解压抛错: {ex.GetType().Name}"); }
        Assert("流式 IDAT：各条载荷【拼接后】是一条能解压的完整 zlib 流，长度=各行(1+宽×4)",
            inflatedLen == (long)H * (1 + W * 4), $"解压得 {inflatedLen}，应为 {(long)H * (1 + W * 4)}");

        // 正控：翻转中间那条 IDAT 的一个载荷字节 ⇒ CRC 与解压必须同时变红
        var corrupted = (byte[])bytes.Clone();
        var mid = idats[Math.Min(1, idats.Count - 1)];
        int flipAt = mid.HeaderEnd + 1 + (mid.Data.Length / 2);   // +1 跳过长度字段? 见 SplitPngChunks 的偏移定义
        corrupted[flipAt] ^= 0x01;
        var (ok2, chunks2) = SplitPngChunks(corrupted);
        bool crcCaught = !ok2 || chunks2.Any(c => Crc32Test(c.TypeBytes, c.Data) != c.Crc);
        bool inflateBroke;
        try
        {
            var p2 = new byte[chunks2.Where(c => c.Type == "IDAT").Sum(c => (long)c.Data.Length)];
            int o = 0;
            foreach (var c in chunks2.Where(c => c.Type == "IDAT"))
            { Buffer.BlockCopy(c.Data, 0, p2, o, c.Data.Length); o += c.Data.Length; }
            using var z2 = new System.IO.Compression.ZLibStream(new MemoryStream(p2),
                System.IO.Compression.CompressionMode.Decompress);
            using var s2 = new MemoryStream();
            z2.CopyTo(s2);
            inflateBroke = s2.Length != (long)H * (1 + W * 4);
        }
        catch { inflateBroke = true; }
        Assert("流式 IDAT 正控：翻掉一条载荷里的一个字节，复算 CRC 与解压必须至少一路判红",
            crcCaught && inflateBroke,
            $"CRC 抓到={crcCaught}，解压/长度坏掉={inflateBroke}（两路都毫无反应=这些字节没人读，判据是空的）");

        // 夹具交给外部裁判（scripts/precision/tiff_png_referee.py）：PIL 独立解一遍像素，
        // 并读一份 16-bit TIFF 验端序与 ExtraSamples —— 判据不许只由产品自己的字节解释自己的字节。
        File.WriteAllBytes(Path.Combine(OutDir, "png_multi_idat.src.bin"), px);
        string tifPath = Path.Combine(OutDir, "tiff16_referee.tif");
        ManagedTiffEncoder.Encode(px, W, H, tifPath, 16, iccProfile: null, inputBitDepth: 8,
            effort: CompressionEffort.Smallest);
        // ⚠ 上面这份的样本恒等于 v×257 ⇒ 每个 uint16 都是 0xNNNN 回文，端序臂在它上【必然不判别】
        //   （10-31 已量到这件事）。所以再备一份真 16-bit 输入的夹具：样本非回文占比 >0 时，
        //   按大端误读才可能变红 —— 裁判脚本必须先证这一点，再宣布端序判据成立。
        var px16 = new byte[W * H * 8];
        uint s16 = 0xBEEF0001;
        int palindromes = 0, total16 = W * H * 4;
        for (int i = 0; i < W * H * 4; i++)
        {
            s16 = s16 * 1664525u + 1013904223u;
            ushort v = (ushort)(s16 >> 16);
            if ((v >> 8) == (v & 0xFF)) palindromes++;
            // 每通道大端（与产品对 16-bit 输入的既有口径一致）
            px16[i * 2] = (byte)(v >> 8);
            px16[i * 2 + 1] = (byte)(v & 0xFF);
        }
        string tif16Path = Path.Combine(OutDir, "tiff16_direct_referee.tif");
        ManagedTiffEncoder.Encode(px16, W, H, tif16Path, 16, iccProfile: null, inputBitDepth: 16,
            effort: CompressionEffort.Smallest);
        Assert("端序裁判夹具：16-bit 直入夹具里非回文样本占多数（全回文=端序臂必然假绿，10-31 量过）",
            palindromes < total16 / 2, $"回文 {palindromes}/{total16}");
        File.WriteAllBytes(Path.Combine(OutDir, "tiff16_direct_referee.src.bin"), px16);
        Assert("外部裁判夹具：16-bit TIFF 与 PNG 夹具都落到盘上（裁判没料可吃就是没判）",
            new FileInfo(tifPath).Length > 1_000 && new FileInfo(pngPath).Length > 1_000
            && new FileInfo(tif16Path).Length > 1_000,
            $"tif8src={new FileInfo(tifPath).Length}B tif16src={new FileInfo(tif16Path).Length}B "
            + $"png={new FileInfo(pngPath).Length}B");
        Console.WriteLine($"  📁 外部裁判夹具目录: {OutDir}"
            + "（png_multi_idat.png / .src.bin / tiff16_referee.tif / tiff16_direct_referee.tif / .src.bin）");
    }

    internal static readonly byte[] PngSignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG 逐块切开：返回 (形状是否合法, 各块)。HeaderEnd = 该块数据起始的绝对偏移。</summary>
    internal static (bool Ok, List<(string Type, byte[] TypeBytes, byte[] Data, uint Crc, int HeaderEnd)> Chunks)
        SplitPngChunks(byte[] b)
    {
        var list = new List<(string Type, byte[] TypeBytes, byte[] Data, uint Crc, int HeaderEnd)>();
        for (int i = 0; i < 8; i++) if (b[i] != PngSignatureBytes[i]) return (false, list);
        int p = 8;
        while (p + 8 <= b.Length)
        {
            int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            if (len < 0 || p + 12 + (long)len > b.Length) return (false, list);
            var type = new byte[4];
            Buffer.BlockCopy(b, p + 4, type, 0, 4);
            var data = new byte[len];
            Buffer.BlockCopy(b, p + 8, data, 0, len);
            uint crc = (uint)((b[p + 8 + len] << 24) | (b[p + 9 + len] << 16)
                | (b[p + 10 + len] << 8) | b[p + 11 + len]);
            list.Add((System.Text.Encoding.ASCII.GetString(type), type, data, crc, p + 8));
            p += 12 + len;
            if (list[^1].Type == "IEND") break;
        }
        return (p == b.Length && list.Count >= 3, list);
    }

    internal static uint Crc32Test(ReadOnlySpan<byte> type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < 4; i++) { c ^= type[i]; c = (c >> 8) ^ CrcTable[(byte)(c & 0xFF)]; }
        for (int i = 0; i < data.Length; i++) { c ^= data[i]; c = (c >> 8) ^ CrcTable[(byte)(c & 0xFF)]; }
        return c ^ 0xFFFFFFFFu;
    }

    internal static readonly uint[] CrcTable = BuildCrcTable();

    internal static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static void Assert(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name} — {detail}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name} — {detail}"); }
    }
}
