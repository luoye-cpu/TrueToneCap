// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
            var (pixels, icc, targetTag) = CapturePipelineServiceBridge.PreparePixelsWithIcc(bgra, 16, 16, true, tag);
            if (icc is not null) settings.IccProfile = icc;
            // ★ 声明取的是【同一份决定】给出的目标，不再在这里第二次解析下拉
            settings.ColorSpaceTag = targetTag;

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
        // ⚠️ 2026-09-14 实测修正: 此前期望 "[1,13,0,1]"，与实测输出（cICP 无）不符，
        //    易被误读为缺陷。实际 H.273 cICP 的 primaries 枚举没有 Adobe RGB 码点
        //    （1=BT.709/sRGB，9=BT.2020，12=P3），而 Adobe RGB(1998) 的 G 原色
        //    (0.21,0.71) 与 BT.709 的 (0.30,0.60) 不同，映射为 1 会造成色彩误判。
        //    正确行为是省略 cICP、改嵌 iCCP（PNG 规范下 iCCP 优先）。
        "AdobeRGB" => "不适用 (H.273 无 AdobeRGB 码点, 仅 iCCP)",
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
    /// <summary>
    /// 与 App 层同名函数<b>共用同一份色彩决定</b>（Core 的 <c>ColorDecision</c>）。
    /// <para>旧写法在这里另抄一遍简化规则（"非 ACM、无显示器 ICC → 嵌标准 ICC (不烘焙)"），
    /// 于是这个自称"真实管线"的自检测的其实不是真实管线 —— 正是 §41.6 说的"没有那一个点"的第五份。
    /// 现在解析与收口只在 ColorDecision 里做一次，这里只把那份决定翻译成像素与 ICC。</para>
    /// <para><c>source=null, systemAcm=false</c> 与本工具旧语义逐位一致（旧代码就把 acm 硬编码成 false），
    /// 且不触碰任何真实显示器 ⇒ 保持 headless 可跑。</para>
    /// </summary>
    public static (byte[] pixels, byte[]? icc, string targetTag) PreparePixelsWithIcc(byte[] bgra, int w, int h, bool bake, string tag)
    {
        // ⚠ 有意不传第 5 个 format 参数（= null）：本桥接没有"本次要落盘的格式"这个上下文，
        //    它被调用的目的是把一枚【已存在的 tag】翻译成像素与 ICC，格式由调用方那边的
        //    PngCheck 自己解析产物后判定。传 OutputFormat.PNG 虽然结果相同（PNG 不在
        //    ColorDecision.DefaultsToRec709 的白名单里），但那会把"我确实知道这次编的是 PNG"
        //    写进签名 —— 一个本工具并不掌握的事实。不传 = 维持 2026-10-06 之前的行为。
        var decision = TrueToneCap.Core.Capture.ColorDecision.Build(null, tag, false, false);
        if (!bake || decision.TargetTag is "sRGB") return (bgra, null, decision.TargetTag);
        var targetCs = ColorProfileProvider.MapColorSpaceTag(decision.TargetTag);
        // 源==目标 ⇒ 像素本就在目标色域，只嵌配套 ICC、不搬动
        if (decision.SourceTag == decision.TargetTag)
            return (bgra, ColorProfileProvider.GetStandardIccProfile(targetCs), decision.TargetTag);
        var srcIcc = ColorProfileProvider.GetStandardIccProfile(decision.SourceTag);
        if (srcIcc is null)
            return (bgra, ColorProfileProvider.GetStandardIccProfile(targetCs), decision.TargetTag);
        var (baked, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, srcIcc, targetCs);
        // 烘焙失败 ⇒ 像素没动，声明跟着退到源标签（App 层的 D-1 同一取向：声明必须说像素的真话）
        return baked is null
            ? (bgra, srcIcc, decision.SourceTag)
            : (baked, targetIcc, decision.TargetTag);
    }
}