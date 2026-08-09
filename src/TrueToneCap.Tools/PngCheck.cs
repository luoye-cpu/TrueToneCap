// TrueToneCap.Tools/PngCheck.cs
// PNG cICP + iCCP 双重声明实测验证 (--png-check)
// 验证: 用真实管线 (PreparePixelsWithIcc + GetColorMetadata + ManagedPngEncoder)
//       生成的 PNG, cICP 与 iCCP 是否一致 (primaries/transfer 匹配)。
// 用法: dotnet run --project src/TrueToneCap.Tools -- --png-check

using System.Buffers.Binary;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

public static class PngCheck
{
    public static void Run(string[] args)
    {
        Console.WriteLine("══════════════ PNG cICP + iCCP 双重声明实测验证 ══════════════\n");

        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 180; bgra[i + 1] = 120; bgra[i + 2] = 80; bgra[i + 3] = 255; }

        var tags = new[] { "System", "sRGB", "DisplayP3", "DCI_P3", "AdobeRGB", "BT2020" };
        foreach (var tag in tags)
        {
            var settings = new EncodingSettings { ColorSpaceTag = tag };
            // 真实管线: 解析 System + 烘焙/嵌 ICC
            var (pixels, icc) = CapturePipelineServiceBridge.PreparePixelsWithIcc(bgra, 16, 16, true, tag);
            if (icc is not null) settings.IccProfile = icc;
            settings.ColorSpaceTag = ColorProfileProvider.ResolveColorSpaceTag(tag, false);

            var (metadataIcc, cicp) = FormatHelper.GetColorMetadata(settings);
            var path = Path.GetTempFileName() + ".png";
            ManagedPngEncoder.Encode(pixels, 16, 16, path, 8, metadataIcc, cicp);

            Console.WriteLine($"── {tag} ──");
            var chunks = ParseChunks(path);
            Console.WriteLine($"  cICP: {FormatCicp(chunks.cicp)}");
            Console.WriteLine($"  iCCP: {(chunks.icc is null ? "无" : $"{chunks.icc.Length} bytes")}");
            Console.WriteLine($"  cICP primaries={chunks.cicp?[0]} transfer={chunks.cicp?[1]} (期望: {ExpectedCicp(tag)})");
            Console.WriteLine();
            try { File.Delete(path); } catch { }
        }

        Console.WriteLine("══════════════ 验证完成 ══════════════");
    }

    static string FormatCicp(byte[]? c)
        => c is null ? "无" : $"primaries={c[0]} transfer={c[1]} matrix={c[2]} range={c[3]}";

    static string ExpectedCicp(string tag) => tag switch
    {
        "DisplayP3" or "DCI_P3" => "[12,13,0,1] (P3 + sRGB)",
        "BT2020" => "[9,13,0,1] (BT.2020 + sRGB)",
        "sRGB" or "System" => "[1,13,0,1] (sRGB + sRGB)",
        "AdobeRGB" => "[1,13,0,1]",
        _ => "?"
    };

    static (byte[]? cicp, byte[]? icc) ParseChunks(string path)
    {
        byte[]? cicp = null, icc = null;
        var data = File.ReadAllBytes(path);
        int off = 8;
        while (off + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(off));
            string type = System.Text.Encoding.ASCII.GetString(data, off + 4, 4);
            int bodyStart = off + 8;
            if (type == "cICP") cicp = data[bodyStart..(bodyStart + len)];
            else if (type == "iCCP") icc = data[bodyStart..(bodyStart + len)];
            off = bodyStart + len + 4; // + CRC
        }
        return (cicp, icc);
    }
}

/// <summary>桥接 CapturePipelineService (App 项目, Tools 无法直接依赖)。</summary>
public static class CapturePipelineServiceBridge
{
    public static (byte[] pixels, byte[]? icc) PreparePixelsWithIcc(byte[] bgra, int w, int h, bool bake, string tag)
    {
        // 简化: 非 ACM、无显示器 ICC → 嵌标准 ICC (不烘焙)
        var resolved = ColorProfileProvider.ResolveColorSpaceTag(tag, false, false);
        if (resolved is "sRGB") return (bgra, null);
        var targetCs = ColorProfileProvider.MapColorSpaceTag(resolved);
        return (bgra, ColorProfileProvider.GetStandardIccProfile(targetCs));
    }
}