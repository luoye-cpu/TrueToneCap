// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/QrDecodeService.cs
// 二维码解码 + 处置分类 —— 2026-10-26 新功能（维护者提出："识别二维码，提取二维码信息"）。
//
// 安全立场（为什么这个服务长这样）：
//   ① 本服务【只解码，不执行】。解出的字符串如何处置（展示 / 复制 / 打开）全部由 App 层
//      QrResultWindow 的白名单把关 —— "允许打开"的唯一通道是 Kind == UrlHttp（scheme 严格
//      等于 http/https），且必须由用户显式点击；这里没有任何打开/执行/联网的代码路径。
//   ② 全程纯本机：ZXing.Net 是纯托管解码器，不发起任何网络请求；不做"预取链接标题"之类的
//      网络动作（那会把用户截图里的 URL 泄给第三方）。
//   ③ 日志纪律与 OCR 同一条：调用方只许记录"找到几个码 / 什么尺寸"，payload 文本禁止落日志
//      （隐私口径见 PRIVACY.md —— "任何档位都不会记录截图里的文字内容"）。
//   ④ 二维码是攻击者可以物理投递的载体（打印在纸上/贴在墙上让人截图），所以解码结果一律按
//      敌意输入对待：控制字符与双向控制符（U+202E 一类"视觉伪装"）在 DisplayText 里转成
//      可见的 \uXXXX 转义，并打旗标 —— 展示层绝不原样渲染这些字符。

using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;

namespace TrueToneCap.Core.Services;

/// <summary>单个二维码载荷的处置分类 —— 决定 UI 允许什么动作（本枚举是"能不能打开"的唯一事实源）。</summary>
public enum QrPayloadKind
{
    /// <summary>http/https 链接 —— 唯一允许"在浏览器打开"的一类（仍须用户显式点击，App 层还要再验一遍）。</summary>
    UrlHttp,
    /// <summary>带其它 scheme 的内容（WIFI:/mailto:/各类 app 私有 scheme…）：只展示，永不打开。</summary>
    UrlOtherScheme,
    /// <summary>纯文本（无 scheme 前缀）。</summary>
    Text,
    /// <summary>已知危险 scheme（file:/ms-msdt:/search-ms:/shell:/vbscript:/javascript:…，Follina 类）：
    /// 红牌展示，禁止任何打开动作。</summary>
    DangerousScheme,
}

/// <summary>单个二维码的解码结果。</summary>
public class QrPayload
{
    /// <summary>解码出的原始文本 —— "复制"按钮走这里，逐字原样，不做任何改写。</summary>
    public string Raw { get; set; } = "";
    /// <summary>处置分类。</summary>
    public QrPayloadKind Kind { get; set; }
    /// <summary>展示用文本：控制字符 / 双向控制符 / 零宽字符已转成可见的 \uXXXX，UI 绝不渲染原始版本。</summary>
    public string DisplayText { get; set; } = "";
    /// <summary>含双向覆盖/隔离符（U+202A-202E / U+2066-2069）—— 内容的视觉顺序可以被伪装，UI 应亮警告。</summary>
    public bool HasRtlOverride { get; set; }
    /// <summary>含 C0/C1 控制字符或零宽字符 —— 常见于把恶意 payload 塞进人眼看不见的位置。</summary>
    public bool HasHiddenChars { get; set; }

    /// <summary>
    /// "在浏览器打开"按钮的启用判据（App 层点击时还须再验一遍 —— 双重把关，不依赖单一防线）。
    /// 控制字符出现即禁开：URL 里带 \r\n 属于注入惯技，宁可错杀。
    /// </summary>
    public bool CanOpenInBrowser =>
        Kind == QrPayloadKind.UrlHttp && !HasHiddenChars && !HasRtlOverride;
}

/// <summary>一次解码的报告。</summary>
public class QrScanReport
{
    /// <summary>按画面出现顺序排列的去重载荷（上限 <see cref="QrDecodeService.MaxPayloadCount"/>）。</summary>
    public List<QrPayload> Payloads { get; set; } = [];
    /// <summary>是否找到了至少一个码。</summary>
    public bool Any => Payloads.Count > 0;
}

/// <summary>
/// 二维码解码服务（QR only）。输入 BGRA32 像素缓冲（与截图管线同格式），
/// 输出经过安全分类的载荷列表。无状态、线程安全（每次调用新建解码器）、纯本机零外发。
/// </summary>
public static class QrDecodeService
{
    /// <summary>单张图最多采信的码数 —— 防病态图打爆结果窗。</summary>
    public const int MaxPayloadCount = 16;

    /// <summary>判定为危险 scheme 的前缀（小写、不含冒号）。
    /// ⚠ 这份名单只决定"红牌警告"的展示强度，不决定"能不能打开"——
    ///   打开与否只看 Kind == UrlHttp（白名单制）；名单漏了新危险 scheme 也只是少个红牌，不破坏防线。</summary>
    private static readonly HashSet<string> DangerousSchemes = new(StringComparer.Ordinal)
    {
        // 本地文件/进程执行
        "file", "shell", "folder",
        // Follina 类诊断/搜索投递（2022-2023 实战案例）
        "ms-msdt", "search-ms", "ms-officecmd",
        // Office 协议句柄（可带参数拉起文档 + 宏）
        "ms-word", "ms-excel", "ms-powerpoint", "ms-visio", "ms-access", "ms-publisher", "ms-project", "ms-onsite",
        // 脚本/数据 URI
        "vbscript", "javascript", "jscript", "data", "blob", "mhtml", "res",
        // 系统面板与安装/跳转类（钓鱼常用：直开设置页/伪装商店页）
        "ms-settings", "ms-windows-store", "intent", "market", "appdata",
    };

    /// <summary>双向控制符（视觉顺序伪装）：U+202A-202E 嵌入/覆盖类 + U+2066-2069 隔离类。</summary>
    private static bool IsBidiControl(char c) =>
        (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

    /// <summary>零宽/不可见格式字符（U+200B-200F、U+2060-206F 的零宽部分、U+FEFF）。</summary>
    private static bool IsInvisibleFormat(char c) =>
        (c >= '\u200B' && c <= '\u200F') || (c >= '\u2060' && c <= '\u2064') || c == '\uFEFF';

    /// <summary>从 BGRA32 像素缓冲解码二维码。找不到时返回空载荷列表（不抛异常）。
    /// 尺寸/缓冲不一致等参数错误同样返回空 —— 解码是"尽力而为"的旁路功能，不值得让截图流程炸掉。</summary>
    public static QrScanReport Decode(byte[] bgraPixels, int width, int height)
    {
        var report = new QrScanReport();
        if (width <= 0 || height <= 0 || bgraPixels is null ||
            bgraPixels.Length < (long)width * height * 4)
            return report;

        // 三段尝试：原样 → 反相（白码黑底，深色主题截图常见）→ 顺时针转 90°。
        // 180/270 由首扫的 FinderPattern 检测天然覆盖（它对四个朝向等价），
        // 反相与 90° 是实测中较容易漏的两档。
        var first = DecodeOnce(bgraPixels, width, height, invert: false);
        if (first.Count > 0) { Fill(report, first); return report; }

        var inverted = DecodeOnce(bgraPixels, width, height, invert: true);
        if (inverted.Count > 0) { Fill(report, inverted); return report; }

        var rotated = Rotate90Cw(bgraPixels, width, height);
        var second = DecodeOnce(rotated, height, width, invert: false);
        if (second.Count > 0) { Fill(report, second); return report; }

        return report;
    }

    private static List<string> DecodeOnce(byte[] bgra, int w, int h, bool invert)
    {
        var results = new List<string>();
        try
        {
            ZXing.LuminanceSource luminance = new RGBLuminanceSource(bgra, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
            // 反相 pass：包一层 InvertedLuminanceSource，而不是指望 ALSO_INVERTED hint ——
            // 该 hint 由高层 BarcodeReader 包装器实现，直接调 reader 时【无人消费】（实测 0 结果）。
            if (invert) luminance = new InvertedLuminanceSource(luminance);
            var bitmap = new BinaryBitmap(new HybridBinarizer(luminance));
            var hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.TRY_HARDER] = true,
                // 无 ECI 段时的默认字符集按行业惯例取 UTF-8（手机/浏览器同款假设）。
                // 带 ECI 段的码仍以 ECI 为准，不受此影响 —— 中文二维码不出现"你好"变"??"。
                [DecodeHintType.CHARACTER_SET] = "UTF-8",
            };
            var found = new QRCodeMultiReader().decodeMultiple(bitmap, hints);
            foreach (var r in found)
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                results.Add(r.Text);
                if (results.Count >= MaxPayloadCount) break;
            }
        }
        catch
        {
            // 解码器对"没有码"的输出就是异常（NotFoundException 派生）——按零结果处理。
            // 其它意外异常同样吞掉：这是旁路功能，宁可少结果也不让截图流程炸掉。
        }
        return results;
    }

    /// <summary>BGRA32 缓冲顺时针旋转 90°（纯函数，测试直接对撞角点像素）。</summary>
    public static byte[] Rotate90Cw(byte[] src, int srcW, int srcH)
    {
        int dstW = srcH, dstH = srcW;
        var dst = new byte[srcW * srcH * 4];
        for (int dy = 0; dy < dstH; dy++)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                // dst(dx,dy) ← src(dy, srcH-1-dx)（顺时针旋转的逆映射）
                int s = ((srcH - 1 - dx) * srcW + dy) * 4;
                int d = (dy * dstW + dx) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    private static void Fill(QrScanReport report, List<string> raws)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in raws)
        {
            if (!seen.Add(raw)) continue; // 多码检测器在相邻切片上可能重复报同一个码
            report.Payloads.Add(new QrPayload
            {
                Raw = raw,
                Kind = Classify(raw),
                DisplayText = ToDisplayText(raw, out bool rtl, out bool hidden),
                HasRtlOverride = rtl,
                HasHiddenChars = hidden,
            });
            if (report.Payloads.Count >= MaxPayloadCount) break;
        }
    }

    /// <summary>处置分类。规则刻意写窄：识别"scheme"用最保守的定义（ASCII 字母开头、
    /// 位置 0 起步、无前置空白），宁可把怪串当 Text 也不误放行。</summary>
    public static QrPayloadKind Classify(string raw)
    {
        var scheme = TryGetScheme(raw);
        if (scheme is null) return QrPayloadKind.Text;
        if (scheme == "http" || scheme == "https") return QrPayloadKind.UrlHttp;
        if (DangerousSchemes.Contains(scheme)) return QrPayloadKind.DangerousScheme;
        return QrPayloadKind.UrlOtherScheme;
    }

    /// <summary>提取位置 0 起步的 scheme（小写）。格式：[a-zA-Z][a-zA-Z0-9+\-.]* 后跟 ':'。
    /// 注意 http 中转站惯技 "https ://evil" / " https://evil"（带空格/前置空白）都【不匹配】→ Text，
    /// 因为它们不是浏览器会当 URL 处理的形态；展示给用户看原样字符串就够了。</summary>
    public static string? TryGetScheme(string raw)
    {
        if (raw.Length < 2) return null;
        int i = 0;
        if (!IsSchemeFirst(raw[0])) return null;
        for (i = 1; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == ':') break;
            if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '-' || c == '.')) return null;
        }
        if (i == raw.Length || i < 1) return null; // 没有冒号 = 纯文本
        return raw[..i].ToLowerInvariant();
    }

    private static bool IsSchemeFirst(char c) => char.IsAsciiLetter(c);

    /// <summary>把 payload 转成"所见即所是"的展示文本：C0/C1 控制字符、双向控制符、
    /// 零宽字符一律转成 \uXXXX 可见转义 —— 用户看到的每个字符都真实存在于 Raw 里，顺序也不会被伪装。</summary>
    public static string ToDisplayText(string raw, out bool hasRtlOverride, out bool hasHiddenChars)
    {
        hasRtlOverride = false;
        hasHiddenChars = false;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            bool bidi = IsBidiControl(c);
            bool invisible = IsInvisibleFormat(c);
            bool control = c < ' ' || c == '\x7F' || (c >= '\u0080' && c <= '\u009F');
            if (bidi) hasRtlOverride = true;
            if (bidi || invisible || control) hasHiddenChars = true;

            if (bidi || invisible || control)
                sb.Append("\\u").Append(((int)c).ToString("X4"));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
