// TrueToneCap.Tools/FmtCheck.cs
// 格式映射实测验证 (--fmt-check)
// 验证: UI 格式列表 (_formats) 索引 → OutputFormat 枚举 → 编码器 → 输出魔数 全链路
// 用法: dotnet run --project src/TrueToneCap.Tools -- --fmt-check

using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

public static class FmtCheck
{
    public static void Run(string[] args)
    {
        Console.WriteLine("══════════════ 格式映射全链路实测验证 ══════════════\n");

        // ═══ 2026-08-10: 显示器 HDR 峰值 + SDR 白点检测验证 ═══
        // intensity_target 语义 = 内容主控峰值 = 源显示器峰值 (截图截的就是显示器显示的)
        Console.WriteLine("── 显示器亮度检测 (intensity_target / SDR 白点依据) ──");
        try
        {
            int sdrWhite = TrueToneCap.Core.Capture.DisplayEnumerator.GetSdrWhiteLevel();
            Console.WriteLine($"SDR 白点 (DISPLAYCONFIG_SDR_WHITE_LEVEL): {sdrWhite} nits {(sdrWhite > 0 ? "✅" : "❌ 检测失败(回退 200)")}");
            var displays = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays();
            foreach (var d in displays)
            {
                Console.WriteLine($"显示器: HDR={d.IsHdr} 支持HDR={d.SupportsHdr} MaxLuminance={d.MaxLuminance:F0} nits 位深={d.BitsPerColor}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"显示器检测: ❌ {ex.Message}"); }
        Console.WriteLine();

        // 模拟 MainWindow._formats (UI 列表, 与 MainWindow.xaml.cs 一致)
        var formats = new (OutputFormat F, string Label)[]
        {
            (OutputFormat.PNG, "PNG (无损)"),
            (OutputFormat.JPEG_GAINMAP, "JPEG Gain Map (HDR)"),
            (OutputFormat.JPEG_LI, "JPEG LI"),
            (OutputFormat.JPEG_XL, "JPEG XL"),
            (OutputFormat.AVIF, "AVIF"),
            (OutputFormat.WebP, "WebP"),
            (OutputFormat.TIFF, "TIFF"),
        };

        Console.WriteLine($"{"UI索引",-6} {"标签",-22} {"枚举",-16} {"编码器",-20} {"HDR",-5} {"扩展名"}");
        Console.WriteLine(new string('-', 80));
        foreach (var (f, label) in formats)
        {
            var enc = EncoderFactory.Create(f);
            var ext = GetExt(f);
            Console.WriteLine($"{Array.IndexOf(formats, (f, label)),-6} {label,-22} {f,-16} {enc.GetType().Name,-20} {enc.SupportsHdr,-5} {ext}");
        }

        Console.WriteLine("\n── 实测编码输出魔数 ──");
        var bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 120; bgra[i + 1] = 160; bgra[i + 2] = 200; bgra[i + 3] = 255; }

        // JPEG LI: 应输出 JPEG (FFD8)
        try
        {
            var jpeg = JpegLiNative.Encode(bgra, 64, 64, 1.0f, "444", null);
            bool isJpeg = jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8;
            bool isJxl = jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0x0A;
            Console.WriteLine($"JPEG LI (cjpegli): {jpeg.Length}B  魔数={jpeg[0]:X2}{jpeg[1]:X2}  JPEG={(isJpeg ? "✅" : "❌")}  JXL={(isJxl ? "❌误判!" : "否")}");
        }
        catch (Exception ex) { Console.WriteLine($"JPEG LI: ❌ {ex.Message}"); }

        // JXL: 应输出 JXL (FF0A) — 覆盖 bitDepth=8 和 bitDepth=10 (无感截图失败场景)
        foreach (var bd in new[] { 8, 10 })
        {
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"fmtcheck_{Guid.NewGuid():N}.jxl");
                // 模拟无感截图: JpegXlEncoder.EncodeSdrAsync 传 s.Quality + s.OutputBitDepth
                NativeJxlEncoder.Encode(bgra, 64, 64, tmp, 1.0f, bd, null);
                var jxlBytes = File.ReadAllBytes(tmp);
                bool isJxl = jxlBytes.Length > 2 && jxlBytes[0] == 0xFF && jxlBytes[1] == 0x0A;
                Console.WriteLine($"JPEG XL (cjxl) bitDepth={bd}: {jxlBytes.Length}B  魔数={jxlBytes[0]:X2}{jxlBytes[1]:X2}  JXL={(isJxl ? "✅" : "❌")}");
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex) { Console.WriteLine($"JPEG XL bitDepth={bd}: ❌ {ex.Message}"); }
        }

        // JXL HDR (EncodeHdr): 16-bit PNG 中转 + Rec2100PQ
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"fmtcheck_hdr_{Guid.NewGuid():N}.jxl");
            var pq16 = new ushort[64 * 64 * 4];
            for (int i = 0; i < pq16.Length; i += 4)
            { pq16[i] = 20000; pq16[i + 1] = 15000; pq16[i + 2] = 10000; pq16[i + 3] = 65535; }
            NativeJxlEncoder.EncodeHdr(pq16, 64, 64, tmp, 1.0f, null, 10000f);
            var jxlBytes = File.ReadAllBytes(tmp);
            string head = string.Join(" ", jxlBytes.Take(12).Select(x => x.ToString("X2")));
            // JXL 两种合法魔数: 裸 codestream (FF0A) 或 container (00..4A584C20 0D0A870A)
            bool isJxl = (jxlBytes.Length > 2 && jxlBytes[0] == 0xFF && jxlBytes[1] == 0x0A)
                || (jxlBytes.Length > 12 && jxlBytes[4] == (byte)'J' && jxlBytes[5] == (byte)'X' && jxlBytes[6] == (byte)'L' && jxlBytes[7] == (byte)' ');
            Console.WriteLine($"JPEG XL HDR (cjxl): {jxlBytes.Length}B  前12字节={head}  JXL={(isJxl ? "✅" : "❌")}");
            try { File.Delete(tmp); } catch { }
        }
        catch (Exception ex) { Console.WriteLine($"JPEG XL HDR: ❌ {ex.Message}"); }

        // ═══ ACM 烘焙验证 (2026-08-09): sRGB 像素 → BT.2020 目标 (BakeIccToTarget) ═══
        Console.WriteLine("\n── ACM 烘焙 (sRGB → BT.2020) ──");
        try
        {
            var srgb = new byte[4 * 4 * 4];
            for (int i = 0; i < srgb.Length; i += 4)
            { srgb[i] = 0; srgb[i + 1] = 0; srgb[i + 2] = 255; srgb[i + 3] = 255; } // 纯红
            var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
            var (baked, icc) = ColorProfileProvider.BakeIccToTarget(srgb, 4, 4, srgbIcc, "BT2020");
            bool ok = baked is not null && icc is { Length: > 128 };
            Console.WriteLine($"BakeIccToTarget(sRGB→BT2020): baked={(baked is not null ? "✅" : "❌")} icc={(icc is { Length: > 128 } ? "✅" : "❌")}");
            if (baked is not null)
            {
                // 纯红 sRGB → BT.2020 后应保持红色调 (R 显著高于 B/G)
                Console.WriteLine($"  像素[0]: B={baked[0]} G={baked[1]} R={baked[2]} (期望 R 最高, 红色保持)");
                ok &= baked[2] > baked[0] && baked[2] > baked[1];
            }
            Console.WriteLine($"ACM sRGB→BT2020 烘焙: {(ok ? "✅" : "❌")}");
        }
        catch (Exception ex) { Console.WriteLine($"ACM 烘焙: ❌ {ex.Message}"); }

        // ═══ HDR PQ ICC 验证 (2026-08-09): TIFF HDR 用的 PQ TRC ICC ═══
        Console.WriteLine("\n── HDR PQ ICC (GetHdrStandardIccProfile) ──");
        try
        {
            var pqIcc = ColorProfileProvider.GetHdrStandardIccProfile("BT2020");
            bool okIcc = pqIcc is { Length: > 128 };
            Console.WriteLine($"GetHdrStandardIccProfile(BT2020): {(okIcc ? "✅" : "❌")} len={(pqIcc?.Length ?? 0)}");
            if (okIcc)
            {
                // 验证 desc 和 TRC 存在
                var desc = ColorProfileProvider.GetIccDescription(pqIcc!);
                Console.WriteLine($"  desc: \"{desc}\"");
                // 保存到临时文件供 py 解析
                var tmp = Path.Combine(Path.GetTempPath(), "ttc_hdr_bt2020.icc");
                File.WriteAllBytes(tmp, pqIcc!);
                Console.WriteLine($"  已导出: {tmp}");
            }
            Console.WriteLine($"HDR PQ ICC: {(okIcc ? "✅" : "❌")}");
        }
        catch (Exception ex) { Console.WriteLine($"HDR PQ ICC: ❌ {ex.Message}"); }

        Console.WriteLine("\n══════════════ 验证完成 ══════════════");
    }

    static string GetExt(OutputFormat f) => f switch
    {
        OutputFormat.JPEG_LI => ".jpg",
        OutputFormat.JPEG_GAINMAP => ".jpg",
        OutputFormat.JPEG_XL => ".jxl",
        OutputFormat.AVIF => ".avif",
        OutputFormat.WebP => ".webp",
        OutputFormat.TIFF => ".tiff",
        _ => ".png"
    };
}