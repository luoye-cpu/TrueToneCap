"""R2 收尾：把 Core 的 FormatColorFacts.Declaration 改成"模板 + 宿主注入本地化钩子"。

为什么不是"App 侧按片段换"（第二批那个权宜）：这里的 31 条字面量里有 7 条带 {expr}，
运行时整串匹配不上，只能拆成片段 ⇒ 拆完之后每条英文是半句话，拼起来读不通。
改成【先翻模板、再代值】：模板是完整一句，译文里 {0} 放哪儿由译者定，英文才立得住。

Core 不能认识 App 的 LocaleManager ⇒ 钩子作参数传入，默认原样返回。
这一点很关键：Declaration 同时被日志与 SelfTest 那张表用（见文件注释"用于界面与日志"），
它们不传钩子 ⇒ 中文输出逐字不变，只有界面那一路传 LocaleManager.Tr。
"""
import io
import re
import sys

FC = 'src/TrueToneCap.Core/Encoding/FormatColorFacts.cs'
CJK = re.compile(r'[\u3000-\u30ff\u4e00-\u9fff\uff00-\uffef]')

# 31 条：键 = 改完之后的模板（带 {0}），值 = 英文
PAIRS = [
    ("PNG：cICP {0}（原色/传输/矩阵），matrix 恒 0(identity) —— PNG 存 RGB 采样，没有 YUV 往返可声明。",
     "PNG: cICP {0} (primaries/transfer/matrix); matrix is always 0 (identity) - "
     "PNG stores RGB samples, so there is no YUV round-trip to declare."),
    ("HDR 一律不嵌 ICC。", "HDR never embeds ICC."),
    ("{0} 目标另嵌 iCCP，两条通道同一套原色。",
     "The {0} target additionally embeds iCCP; both channels carry the same primaries."),
    ("{0} 目标不嵌 ICC（cICP 已足够）。",
     "The {0} target embeds no ICC (cICP is enough)."),
    ("AVIF：colr(nclx) {0}，matrix 随三原色取 {1} —— 它是 YUV 码流，",
     "AVIF: colr(nclx) {0}, matrix follows the primaries as {1} - it is a YUV bitstream, "),
    ("不能像 PNG 那样写 identity（写 0 时部分解码器自行套错矩阵）。",
     "so it cannot declare identity the way PNG does (with 0, some decoders apply the wrong matrix themselves)."),
    ("HDR 不嵌 ICC。", "HDR embeds no ICC."),
    ("{0} 另可嵌 ICC。", "{0} may additionally embed ICC."),
    ("{0} 不嵌 ICC。", "{0} embeds no ICC."),
    ("JPEG XL：color fields 与 ICC 二选一（同时给时 cjxl 只走 ICC、fields 被丢弃；",
     "JPEG XL: colour fields or ICC, one of the two (given both, cjxl uses only ICC and drops the fields; "),
    ("ICC 能归约成字段就只写字段，归约不了就整份内嵌）。没有 matrix 这一维，",
     "if the ICC reduces to fields only the fields are written, otherwise the whole profile is embedded). "
     "There is no matrix dimension, "),
    ("且有损产物把像素量化进 XYB —— 声明写的是原图色域，不是码流里的通道值。",
     "and lossy output quantises pixels into XYB - the declaration states the source gamut, "
     "not the channel values in the bitstream."),
    ("HDR 走 Rec.2020 原色 + PQ（cjxl 的枚举名 Rec2100PQ，另带 intensity_target）。",
     "HDR uses Rec.2020 primaries + PQ (cjxl's enum name Rec2100PQ, plus intensity_target)."),
    ("{0} 取到可嵌 ICC 时用 ICC，否则用 color fields。",
     "{0} uses ICC when an embeddable ICC is available, otherwise colour fields."),
    ("{0} 走 color fields，不嵌 ICC。", "{0} uses colour fields and embeds no ICC."),
    ("JPEG：只有 ICC 一条声明通道，没有代码点 —— ",
     "JPEG: ICC is the only declaration channel, there are no code points - "),
    ("{0} 原色嵌 APP2/ICC（实测产物里 ICC_PROFILE 与 acsp 都在）。",
     "The {0} primaries are embedded as APP2/ICC (measured output has both ICC_PROFILE and acsp)."),
    ("{0} 目标【什么都不写】，靠 JFIF 默认。",
     "The {0} target writes nothing at all, relying on the JFIF default."),
    ("容器无 HDR 通道 ⇒ 开关会落空成 8-bit SDR；这与【能否声明 Rec.2020 原色】是两件事。",
     "The container has no HDR channel, so the switch falls back to 8-bit SDR; "
     "that is a separate question from whether Rec.2020 primaries can be declared."),
    ("WebP：同样只有 ICC 一条通道 —— ",
     "WebP: likewise ICC is the only channel - "),
    ("{0} 原色经中转 PNG 的 iCCP 带过去，并给 cwebp 传 -metadata icc（实测产物 ICCP 块在，profile 2100 字节）。",
     "The {0} primaries are carried through the intermediate PNG's iCCP, and cwebp gets -metadata icc "
     "(measured output has the ICCP chunk, a 2100-byte profile)."),
    ("{0} 目标【什么都不写】（实测对照组无 ICCP）。",
     "The {0} target writes nothing at all (the measured control group had no ICCP)."),
    ("无 HDR 通道 ⇒ HDR 开关落空成 8-bit SDR，但原色声明照常；缺 cwebp.exe 时整份产物会换成 PNG。",
     "No HDR channel, so the HDR switch falls back to 8-bit SDR, but the primaries declaration still applies; "
     "when cwebp.exe is missing the whole output is replaced by PNG."),
    ("TIFF：没有代码点通道 ⇒ 只靠 ICC，连 Rec.709 也老实嵌一份（不像 JPEG/WebP 能省）。",
     "TIFF: no code-point channel, so ICC only - and even Rec.709 is honestly embedded "
     "(unlike JPEG/WebP, which can omit it)."),
    ("HDR 用带 PQ TRC 的 ICC 声明。", "HDR is declared by an ICC with a PQ TRC."),
    ("SDR 用标准色域 ICC。", "SDR uses the standard-gamut ICC."),
    ("JPEG（附带增益图）：底图【恒为 SDR】且【恒嵌】一份 ICC —— 目标是 {0} ⇒ 底图、增益比、",
     "JPEG (with gain map): the base image is always SDR and always carries an embedded ICC - "
     "with target {0} the base, the gain ratio and "),
    ("声明三件同一套原色；换目标就是换原色，不会把底图变成 HDR（HDR 由 XMP hdrgm + ",
     "the declaration all share one set of primaries; changing the target changes the primaries, "
     "it never turns the base into HDR (HDR is carried by XMP hdrgm + "),
    ("ISO 21496-1 增益图段承载，容器不写 PQ ⇒ 与「目标不是 HDR 档」不冲突）。",
     "the ISO 21496-1 gain-map section, and the container writes no PQ, "
     "so this does not conflict with 'the target is not the HDR tier')."),
    ("无码点", "no code point"),
    ("未知格式：给不出声明通道。", "Unknown format: no declaration channel to give."),
]

NEW_METHOD = '''    /// <summary>
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
    /// <param name="targetTag">收口后的目标标签，只有 <c>sRGB</c> / <c>BT2020</c>。</param>
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
                // ⚠ 2026-09-27：这句原先容易被读成"这格式不能碰 Rec.2020"。分开说清：
                //   没有的是 HDR 通道，广色域 SDR 的原色声明是有的（就是上面那条 ICC 通路）。
                + L("容器无 HDR 通道 ⇒ 开关会落空成 8-bit SDR；这与【能否声明 Rec.2020 原色】是两件事。"),

            OutputFormat.WebP =>
                L("WebP：同样只有 ICC 一条通道 —— ")
                + (wide ? L("{0} 原色经中转 PNG 的 iCCP 带过去，并给 cwebp 传 -metadata icc（实测产物 ICCP 块在，profile 2100 字节）。", TargetName())
                        : L("{0} 目标【什么都不写】（实测对照组无 ICCP）。", TargetName()))
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
'''


def main():
    t = io.open(FC, encoding='utf-8', newline='').read()
    assert '\r\n' not in t, '这份 Core 文件应当是 LF'
    start = t.index('    /// <summary>\n    /// 该格式在当前')
    assert t.rstrip().endswith('}')
    new = t[:start] + NEW_METHOD
    # 自检：改完之后 Core 这一份里不该再有"没被 L() 包住的中文字面量"
    left = [m.group(1) for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"', new)
            if CJK.search(m.group(1))]
    unwrapped = []
    for lit in left:
        i = new.index('"' + lit + '"')
        head = new[max(0, i - 12):i]
        if not (head.endswith('L(') or head.endswith('T(')):
            unwrapped.append(lit)
    doc = [u for u in unwrapped if True]
    # 注释里的中文不算（上面 finditer 会吃到 // 注释与 ///），先剥掉行注释再判
    stripped = re.sub(r'^\s*//.*$', '', new, flags=re.M)
    real = [m.group(1) for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"', stripped) if CJK.search(m.group(1))]
    bad = []
    for lit in real:
        i = stripped.index('"' + lit + '"')
        if not stripped[max(0, i - 3):i].endswith('L('):
            bad.append(lit)
    print('改完仍没被 L() 包住的中文文案：', len(bad))
    for b in bad:
        print('   ', repr(b[:70]))
    keys = [z for z, _ in PAIRS]
    miss = [r for r in real if not any(k in r or r in k for k in keys)]
    print('PAIRS 没覆盖到的中文文案：', len(miss))
    for m in miss:
        print('   ', repr(m[:70]))
    if bad or miss or doc and False:
        print('❌ 有遗漏 ⇒ 不落盘')
        return 1
    io.open(FC, 'w', encoding='utf-8', newline='').write(new)
    print('已落盘', FC, len(new), '字符')
    return 0


if __name__ == '__main__':
    sys.exit(main())
