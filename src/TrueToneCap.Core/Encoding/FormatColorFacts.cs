// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/FormatColorFacts.cs
// "这一次截图在这个格式里到底交付成什么形态、容器又能把色彩声明写成什么" 的唯一口径。

using TrueToneCap.Core.Capture;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// HDR 开关打开后<b>实际</b>交付的形态 —— 五种，不是一种。
/// <para>为什么要有它：界面曾经只有一种画法 —— <c>HdrSwitch.IsOn</c> 为真就写"HDR 直通"。
/// 而产品里"HDR 开"至少会被三样东西收口（屏幕证据不足 / 目标是 sRGB / 这个格式根本没有 HDR 通道），
/// 每种收口的产物都不一样，卡片却全说"直通"。类型存在的意义就是让"HDR 开"不再能推出"写 PQ"。</para>
/// </summary>
public enum HdrDelivery
{
    /// <summary>容器如实写 PQ（cICP / colr(nclx) / color fields / PQ-TRC ICC，通道因格式而异）。</summary>
    PqContainer,

    /// <summary>增益图形态：底图是 SDR JPEG，HDR 由高光增益承载，<b>容器不写 PQ</b>。
    /// <para>它与 <see cref="BlockedBySrgbTarget"/> 的区别正是这条规则过去误伤的地方：
    /// Ultra HDR 的 base layer 按约定就是 sRGB/BT.709，"目标是 sRGB"对增益图不构成冲突。</para></summary>
    GainMap,

    /// <summary>屏幕证据不足 ⇒ <see cref="ColorDecision.HdrOutput"/> 已被收口，本次按 SDR 色调映射。</summary>
    BlockedByDisplay,

    /// <summary>目标被解析/收口成 sRGB ⇒ sRGB 容器装不下 PQ 声明，本次按 SDR 色调映射。</summary>
    BlockedBySrgbTarget,

    /// <summary>这个格式没有 HDR 通道（<see cref="ImageEncoder.SupportsHdr"/> 为假），本次按 SDR 色调映射。</summary>
    BlockedByFormat,
}

/// <summary>
/// 每格式的<b>色彩形状</b>：HDR 能不能交付、以什么形态；色彩声明走哪条通道、代码点是多少。
/// <para><b>为什么需要"每格式"这一维</b>（2026-09-26 用户指令）：目标色域只有 sRGB / BT.2020 两档，
/// 但"这两档在容器里长什么样"逐格式不同 —— PNG 存 RGB 采样，matrix 槽写 identity(0)；
/// AVIF 是 YUV 码流，matrix 只能随三原色取 BT.709(1) 或 BT.2020 NCL(9)；JPEG LI 与 WebP 只有 ICC
/// 一条通道、且 sRGB 目标<b>什么都不写</b>；TIFF 没有代码点通道，连 sRGB 也要老实嵌一份 ICC；
/// JPEG XL 的 color fields 与 ICC 二选一；增益图靠 XMP + ISO 21496-1 段。
/// 所以"HDR 直通吗 / 声明成什么"不能按开关回答，必须按（决定 × 格式）回答。</para>
/// <para><b>代码点不在这里另算一遍</b>：PNG 那组直接转调编码器用的 <see cref="FormatHelper.BuildSdrCicp"/> /
/// <see cref="FormatHelper.BuildHdrCicp"/>；AVIF 的 primaries/transfer 用同一组常数、matrix 问
/// <see cref="FormatHelper.AvifMatrixCoefficients"/>（<c>NvencAvifCicp</c> 问的也是它）
/// ⇒ 界面上的数字就是产物里的数字。</para>
/// </summary>
public static class FormatColorFacts
{
    /// <summary>
    /// （决定 × 格式）⇒ HDR 的真实交付形态。判定顺序即优先级，写死在这里以免两处各排一次。
    /// </summary>
    public static HdrDelivery Delivery(ColorDecision decision, OutputFormat format)
    {
        // ① 屏幕授权：Build 里已经收口过（HdrOutput=false），这里只负责说出原因。
        if (!decision.HdrOutput) return HdrDelivery.BlockedByDisplay;
        // ② 容器有没有 HDR 通道 —— JPEG LI / WebP 在这里出局，产物是 8-bit SDR 色调映射。
        if (!EncoderFactory.Create(format).SupportsHdr) return HdrDelivery.BlockedByFormat;
        // ③ sRGB 目标与 PQ 声明互斥（PQ 是 HDR 的传输函数，容器却声明 sRGB ⇒ 自相矛盾）。
        //    增益图除外：它的 base 层【恒为】SDR，HDR 在增益图里，sRGB base 是格式约定而非降级。
        if (decision.TargetTag == "sRGB" && format != OutputFormat.JPEG_GAINMAP)
            return HdrDelivery.BlockedBySrgbTarget;
        return format == OutputFormat.JPEG_GAINMAP ? HdrDelivery.GainMap : HdrDelivery.PqContainer;
    }

    /// <summary>
    /// 该格式在当前（目标色域, 是否写 PQ）下<b>真实</b>会写下的色彩声明，一句话。
    /// <para>用于界面与日志；不参与编码判定。</para>
    /// </summary>
    /// <param name="format">已经由"下拉 + 增益图开关"翻译过的格式（<c>JPEG_GAINMAP</c> 是一种格式，不是一面旗）。</param>
    /// <param name="targetTag">收口后的目标标签，只有 <c>sRGB</c> / <c>BT2020</c>。</param>
    /// <param name="writesPq">本次容器到底许不许写 PQ —— 由 <see cref="Delivery"/> 给，不许由调用方猜开关。</param>
    public static string Declaration(OutputFormat format, string targetTag, bool writesPq)
    {
        bool wide = targetTag != "sRGB";
        string PngCicp()
        {
            byte[]? c = writesPq ? FormatHelper.BuildHdrCicp(targetTag)
                                 : FormatHelper.BuildSdrCicp(targetTag);
            return c is null ? "无码点" : $"{c[0]}/{c[1]}/{c[2]}";
        }
        // AVIF 与 PNG 共用 primaries/transfer 常数，matrix 却必须换成 YUV 那一档 ⇒ 不能直接印 BuildXxxCicp。
        string AvifCicp()
        {
            byte p = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(targetTag);
            return $"{p}/{(writesPq ? FormatHelper.CicpTransferPq : FormatHelper.CicpTransferSrgb)}"
                 + $"/{FormatHelper.AvifMatrixCoefficients(p)}";
        }
        string MatrixName() => ColorManagement.ColorSpaceConverter.GetCicpPrimaries(targetTag) switch
        {
            FormatHelper.CicpPrimariesBt2020 => "9(BT.2020 NCL)",
            _ => "1(BT.709)",
        };

        return format switch
        {
            OutputFormat.PNG =>
                $"PNG：cICP {PngCicp()}（原色/传输/矩阵），matrix 恒 0(identity) —— PNG 存 RGB 采样，没有 YUV 往返可声明。"
                + (writesPq ? "HDR 一律不嵌 ICC。"
                            : wide ? "广色域另嵌 iCCP，两条通道同一套原色。"
                                   : "sRGB 目标不嵌 ICC（cICP 已足够）。"),

            OutputFormat.AVIF =>
                $"AVIF：colr(nclx) {AvifCicp()}，matrix 随三原色取 {MatrixName()} —— 它是 YUV 码流，"
                + "不能像 PNG 那样写 identity（写 0 时部分解码器自行套错矩阵）。"
                + (writesPq ? "HDR 不嵌 ICC。" : wide ? "另可嵌 ICC。" : "sRGB 不嵌 ICC。"),

            OutputFormat.JPEG_XL =>
                "JPEG XL：color fields 与 ICC 二选一（同时给会被一方覆盖 —— 实测 cjxl 把承载不了的 ICC 归到最近的枚举）。"
                + (writesPq ? "HDR 只接受 BT.2020 家族（Rec2100PQ + intensity_target）。"
                            : wide ? "BT.2020 取到可嵌 ICC 时用 ICC，否则用 color fields。"
                                   : "sRGB 走 color fields，不嵌 ICC。"),

            OutputFormat.JPEG_LI =>
                "JPEG LI：只有 ICC 一条声明通道，没有代码点 —— "
                + (wide ? "BT.2020 嵌 APP2/ICC。" : "sRGB 目标【什么都不写】，靠 JFIF 默认。")
                + "容器无 HDR 通道 ⇒ HDR 只会以 8-bit SDR 色调映射产出。",

            OutputFormat.WebP =>
                "WebP：同样只有 ICC 一条通道 —— "
                + (wide ? "BT.2020 经中转 PNG 的 iCCP 带过去。" : "sRGB 目标【什么都不写】。")
                + "无 HDR 通道；缺 cwebp.exe 时整份产物会换成 PNG。",

            OutputFormat.TIFF =>
                "TIFF：没有代码点通道 ⇒ 只靠 ICC，连 sRGB 也老实嵌一份（不像 JPEG/WebP 能省）。"
                + (writesPq ? "HDR 用带 PQ TRC 的 ICC 声明。" : "SDR 用标准色域 ICC。"),

            OutputFormat.JPEG_GAINMAP =>
                "增益图：底图【恒嵌】一份 ICC（默认 sRGB；显式选 BT.2020 时 base/增益/声明三件同走 2020），"
                + "HDR 由 XMP hdrgm + ISO 21496-1 增益图段承载，容器不写 PQ ⇒ 与「目标是 sRGB」不冲突。",

            _ => "未知格式：给不出声明通道。",
        };
    }
}
