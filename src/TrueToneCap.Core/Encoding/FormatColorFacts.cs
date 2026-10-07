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
/// <para><b>2026-10-06 新增：JPEG（含增益图）与 WebP 的<b>默认</b>目标就是 Rec.709</b> ——
/// 这两族格式的 Rec.2020 产物只有在用户<b>显式</b>把下拉拨到 Rec.2020 时才会出现；
/// 「跟随显示器」在宽色域屏 / HDR 屏上解析出的 Rec.2020 <b>不再</b>对它们生效（会被收口回 Rec.709）。
/// 为什么单拎出来说：过去"跟随显示器"是<b>推断</b>出来的目标，用户没选过 Rec.2020，产物却是 Rec.2020，
/// 而 JPEG/WebP 的 Rec.2020 只能靠嵌一份大 ICC 承载 —— 用户拿到的是自己没要求过、播放器还未必认的色域。
/// 于是这两族格式的口径改成"<b>默认不替用户升级色域，要 Rec.2020 请明说</b>"；
/// 这与上面那句「sRGB 目标什么都不写」并不冲突：默认档收口后<b>就是</b> sRGB 目标，照样什么都不写。
/// ⚠ 被否决的替代方案：「干脆让 JPEG/WebP 也跟随显示器写 Rec.2020，只把文案改软」——
/// 那等于把"用户没选过"的升级留在产物里，只把话说得更好听，属于改标签不改行为，不采纳。</para>
/// <para><b>代码点不在这里另算一遍</b>：PNG 那组直接转调编码器用的 <see cref="FormatHelper.BuildSdrCicp"/> /
/// <see cref="FormatHelper.BuildHdrCicp"/>；AVIF 的 primaries/transfer 用同一组常数、matrix 问
/// <see cref="FormatHelper.AvifMatrixCoefficients"/>（<c>NvencAvifCicp</c> 问的也是它）
/// ⇒ 界面上的数字就是产物里的数字。</para>
/// </summary>
public static class FormatColorFacts
{
    /// <summary>
    /// （决定 × 格式）⇒ HDR 的真实交付形态。判定顺序即优先级，写死在这里以免两处各排一次。
    /// <para>═══ 2026-10-06：默认目标改成 Rec.709 之后，这个顺序为什么<b>一个字都不用动</b> ═══</para>
    /// <para>新口径下，JPEG/WebP 在 HDR 屏上选「跟随显示器」(System) 时，
    /// <see cref="ColorDecision.TargetTag"/> 已经是 <c>sRGB</c>（不再是过去被推断出来的 BT2020），
    /// 而 <see cref="ColorDecision.HdrOutput"/> 仍为真 —— 即"屏幕授权了 HDR，但目标是窄色域"。</para>
    /// <para>此时 ② 在 ③ 之前命中：这两族格式 <see cref="ImageEncoder.SupportsHdr"/> 为假
    /// ⇒ 结果是 <see cref="HdrDelivery.BlockedByFormat"/>，而<b>不是</b> <see cref="HdrDelivery.BlockedBySrgbTarget"/>。
    /// 这是<b>想要的</b>结果，理由是两者说出的原因不同、用户能采取的动作也不同：
    /// BlockedByFormat 说的是"这个格式根本没有 HDR 通道，换格式才行"（换 PNG/AVIF/JXL/TIFF 即可拿到 PQ）；
    /// BlockedBySrgbTarget 说的是"目标色域与 PQ 声明互斥，改目标才行"（换 Rec.2020 即可）。
    /// 对 JPEG/WebP 而言，把目标从 Rec.2020 改成 Rec.709 <b>并不能</b>让它们写得出 PQ ——
    /// 病根是格式本身没有那条通道，所以必须报格式，报目标就是把人往错的方向指。</para>
    /// <para>⚠ 被否决的替代方案：把 ③ 提到 ② 前面，让这两族格式在 HDR 屏上统一报 BlockedBySrgbTarget。
    /// 否决原因是它会给出一句<b>用户照做也没用</b>的提示，并且会把 ③ 的语义
    /// （"目标与声明互斥"）稀释成"目标不宽"。</para>
    /// <para>顺带确认：<see cref="OutputFormat.JPEG_GAINMAP"/> 在 sRGB 与 BT2020 <b>两档下都</b>返回
    /// <see cref="HdrDelivery.GainMap"/> —— 它的 base 层恒为 SDR、按格式约定恒写 sRGB/BT.709 原色，
    /// "目标不是 HDR 档"对它本来就不构成冲突，所以上面的默认改动<b>不该</b>、也确实<b>没有</b>波及它。</para>
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
    /// <para><paramref name="tr"/> 是宿主注入的本地化钩子：Core 不认识 App 的 LocaleManager，
    /// 而这条文案同时进界面与日志 ⇒ 只能由调用方决定翻不翻。默认<b>原样返回</b>，
    /// 所以日志、<c>SelfTest</c> 那张表与任何不传钩子的调用方，输出逐字不变；
    /// 只有「色域映射」卡片传 <c>LocaleManager.Tr</c>。</para>
    /// <para>带数值的那几条写成 <c>{0}</c> 模板：先翻模板再代值，
    /// 译文里占位符放哪儿由译者定 —— 否则英文只能按中文语序拼，读不通。</para>
    /// </summary>
    /// <param name="format">已经由"下拉 + 增益图开关"翻译过的格式（<c>JPEG_GAINMAP</c> 是一种格式，不是一面旗）。</param>
    /// <param name="targetTag">收口后的目标标签，只有 <c>sRGB</c> / <c>BT2020</c>。
    /// <para>⚠ 2026-10-06：JPEG / WebP 的<b>默认</b>目标已是 Rec.709 ⇒ 这两族格式在默认档下
    /// 拿到的就是 <c>sRGB</c>，Rec.2020 只在用户显式选择时才会传进来（收口发生在 <c>ColorDecision.Build</c>，
    /// 本函数只如实转述，不自己再收一遍）。</para></param>
    /// <param name="writesPq">本次容器到底许不许写 PQ —— 由 <see cref="Delivery"/> 给，不许由调用方猜开关。</param>
    /// <param name="tr">模板 → 当前语言；省略即保持中文原文。</param>
    public static string Declaration(OutputFormat format, string targetTag, bool writesPq,
                                     Func<string, string>? tr = null)
    {
        Func<string, string> T = tr ?? (static s => s);
        string L(string template, params object[] args) =>
            args.Length == 0 ? T(template) : string.Format(T(template), args);
        bool wide = targetTag != "sRGB";
        string PngCicp()
        {
            byte[]? c = writesPq ? FormatHelper.BuildHdrCicp(targetTag)
                                 : FormatHelper.BuildSdrCicp(targetTag);
            return c is null ? L("无码点") : $"{c[0]}/{c[1]}/{c[2]}";
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
            FormatHelper.CicpPrimariesBt2020 => "9(Rec.2020)",
            _ => "1(Rec.709)",
        };

        // ═══ 2026-09-27 用户口径：面向用户的色域名字只有 Rec.709 / Rec.2020 两个 ═══
        // 这一段文案原先混着写 "sRGB"、"BT.709"、"BT.2020"、"广色域"、"非 sRGB" 五种叫法，
        // 而它们指的同一样东西（用户下拉里那一档）；增益图那一格最典型 —— 底图恒为 SDR，
        // 却被写成"全走 BT.2020"，读起来像容器在声明 HDR。
        // ⚠ 改的是【叫法】：`targetTag` 仍是 "sRGB"/"BT2020"（那是盘上那个值与 ICC 生成的键），
        //   CICP 码点数字照旧印出来 —— 码点 1 的正式名是 BT.709 原色 + transfer 13 是 sRGB 曲线，
        //   这一档叫 Rec.709 说的是原色，传递函数那一维由数字自己说话，不靠标签暗示。
        string TargetName() => wide ? "Rec.2020" : "Rec.709";

        return format switch
        {
            OutputFormat.PNG =>
                L("PNG：cICP {0}（原色/传输/矩阵），matrix 恒 0(identity) —— PNG 存 RGB 采样，没有 YUV 往返可声明。", PngCicp())
                + (writesPq ? L("HDR 一律不嵌 ICC。")
                            : wide ? L("{0} 目标另嵌 iCCP，两条通道同一套原色。", TargetName())
                                   : L("{0} 目标不嵌 ICC（cICP 已足够）。", TargetName())),

            OutputFormat.AVIF =>
                L("AVIF：colr(nclx) {0}，matrix 随三原色取 {1} —— 它是 YUV 码流，", AvifCicp(), MatrixName())
                + L("不能像 PNG 那样写 identity（写 0 时部分解码器自行套错矩阵）。")
                + (writesPq ? L("HDR 不嵌 ICC。") : wide ? L("{0} 另可嵌 ICC。", TargetName()) : L("{0} 不嵌 ICC。", TargetName())),

            OutputFormat.JPEG_XL =>
                L("JPEG XL：color fields 与 ICC 二选一（同时给时 cjxl 只走 ICC、fields 被丢弃；")
                + L("ICC 能归约成字段就只写字段，归约不了就整份内嵌）。没有 matrix 这一维，")
                + L("且有损产物把像素量化进 XYB —— 声明写的是原图色域，不是码流里的通道值。")
                + (writesPq ? L("HDR 走 Rec.2020 原色 + PQ（cjxl 的枚举名 Rec2100PQ，另带 intensity_target）。")
                            : wide ? L("{0} 取到可嵌 ICC 时用 ICC，否则用 color fields。", TargetName())
                                   : L("{0} 走 color fields，不嵌 ICC。", TargetName())),

            OutputFormat.JPEG_LI =>
                L("JPEG：只有 ICC 一条声明通道，没有代码点 —— ")
                + (wide ? L("{0} 原色嵌 APP2/ICC（实测产物里 ICC_PROFILE 与 acsp 都在）。", TargetName())
                        : L("{0} 目标【什么都不写】，靠 JFIF 默认。", TargetName()))
                // ═══ 2026-10-06 用户口径：这一族格式的默认目标就是 Rec.709 ═══
                // 为什么必须写进卡片：过去"跟随显示器"会在这类格式上解析出 Rec.2020，
                // 而用户从没选过它 —— 产物里出现一个没人要求过的色域，属于"界面没交代过的行为"。
                // ⚠ 措辞纪律（照抄会翻车）：这里只能写 `Rec.709 目标`；写成 `sRGB 目标`/`目标 sRGB`
                //   会命中 Test_VisibleToastAndHintStrings_UseCurrentNames 的旧叫法清单（CorePipelineTests:7199），
                //   而 `BT.709`/`BT.2020` 这两个带点的旧叫法又被 CorePipelineTests:7024-7027 全域封杀。
                + L("本格式的默认目标就是 Rec.709：跟随显示器也好、默认档也好，都不会自动升到 Rec.2020 —— "
                  + "只有用户把目标【显式】选成 Rec.2020 时才会写出 Rec.2020 原色。")
                // ⚠ 2026-09-27：这句原先容易被读成"这格式不能碰 Rec.2020"。分开说清：
                //   没有的是 HDR 通道，广色域 SDR 的原色声明是有的（就是上面那条 ICC 通路）。
                + L("容器无 HDR 通道 ⇒ 开关会落空成 8-bit SDR；这与【能否声明 Rec.2020 原色】是两件事。"),

            OutputFormat.WebP =>
                L("WebP：同样只有 ICC 一条通道 —— ")
                + (wide ? L("{0} 原色经中转 PNG 的 iCCP 带过去，并给 cwebp 传 -metadata icc（实测产物 ICCP 块在，profile 2100 字节）。", TargetName())
                        : L("{0} 目标【什么都不写】（实测对照组无 ICCP）。", TargetName()))
                // ═══ 2026-10-06：与 JPEG 同一条默认口径，两处必须说成同一件事 ═══
                // 为什么和 JPEG 分开写而不是抽公共句：两者的"什么都不写"细节不同
                //（JPEG 靠 JFIF 默认，WebP 靠实测对照组），抽公共句反而两头都说不准。
                // ⚠ 措辞纪律同上：只写 `Rec.709 目标`，不写 `sRGB 目标`/`目标 sRGB`/`BT.709`/`BT.2020`。
                + L("本格式的默认目标同样是 Rec.709：跟随显示器或默认档都只给 Rec.709，"
                  + "要 Rec.2020 得由用户【显式】指定 —— 与 JPEG 同一条规则，两族格式不再各自为政。")
                + L("无 HDR 通道 ⇒ HDR 开关落空成 8-bit SDR，但原色声明照常；缺 cwebp.exe 时整份产物会换成 PNG。"),

            OutputFormat.TIFF =>
                L("TIFF：没有代码点通道 ⇒ 只靠 ICC，连 Rec.709 也老实嵌一份（不像 JPEG/WebP 能省）。")
                + (writesPq ? L("HDR 用带 PQ TRC 的 ICC 声明。") : L("SDR 用标准色域 ICC。")),

            OutputFormat.JPEG_GAINMAP =>
                L("JPEG（附带增益图）：底图【恒为 SDR】且【恒嵌】一份 ICC —— 目标是 {0} ⇒ 底图、增益比、", TargetName())
                + L("声明三件同一套原色；换目标就是换原色，不会把底图变成 HDR（HDR 由 XMP hdrgm + ")
                + L("ISO 21496-1 增益图段承载，容器不写 PQ ⇒ 与「目标不是 HDR 档」不冲突）。"),

            _ => L("未知格式：给不出声明通道。"),
        };
    }
}
