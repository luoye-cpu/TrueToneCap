// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/QrDecodeTests.cs
// 二维码解码套件 —— 解码往返（真编码器产码，不靠手工画图）+ 处置分类 + 展示转义 + 旋转/守卫。
//
// 设计说明：
//   · 用包内自带的 ZXing.QrCode.QRCodeWriter 生成真二维码位图再喂给 QrDecodeService ——
//     端到端往返是唯一能同时证明"编码参数、像素格式（BGRA32）、多码检测器"接对了的方式。
//   · 分类与转义是纯函数，逐条对撞；重点盯"打开动作的唯一通道是 UrlHttp"这条防线。
//   · ToString 泄漏哨兵：QrPayload 一旦被改成 record，默认 ToString 会把 Raw 带出去，
//     任何日志里的 $"{payload}" 都会泄露二维码内容 —— 这条哨兵当场翻车。

using TrueToneCap.Core.Services;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace TrueToneCap.Test;

/// <summary>二维码解码测试集。</summary>
public static class QrDecodeTests
{
    private static int _passed, _failed;
    private static int _skipped;

    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}{(detail != null ? $": {detail}" : "")}"); }
    }

    public static int RunAll()
    {
        _passed = 0; _failed = 0; _skipped = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 二维码解码测试");
        Console.WriteLine("══════════════════════════════════════\n");

        Test_RoundTrip_SingleCode();
        Test_RoundTrip_MultipleCodes();
        Test_RoundTrip_InvertedCode();
        Test_RotatedImage_StillDecodes();
        Test_Rotate90Cw_PixelMapping();
        Test_Classify_WhitelistOnlyHttpOpens();
        Test_Classify_DangerousSchemesGetRedFlag();
        Test_Classify_TrickyInputsNeverBecomeHttp();
        Test_DisplayText_EscapesInvisibleCharacters();
        Test_CanOpenInBrowser_GatesOnCleanliness();
        Test_ToStringNeverLeaksPayload();
        Test_Guards_BadParametersReturnEmpty();
        Test_Dedupe_SameCodeReportedOnce();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--qr-tests", _passed, _failed, 0, null, _skipped);
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  位图工厂：BitMatrix → BGRA32 缓冲（黑模块/白底，与截图同格式）
    // ═══════════════════════════════════════

    private static byte[] BitMatrixToBgra(BitMatrix m, bool invert = false)
    {
        var buf = new byte[m.Width * m.Height * 4];
        for (int y = 0; y < m.Height; y++)
        {
            for (int x = 0; x < m.Width; x++)
            {
                bool black = m[x, y] ^ invert;
                byte v = black ? (byte)0 : (byte)255;
                int i = (y * m.Width + x) * 4;
                buf[i] = v; buf[i + 1] = v; buf[i + 2] = v; buf[i + 3] = 255; // BGRA：B/G/R 同值，A 满
            }
        }
        return buf;
    }

    private static BitMatrix EncodeQr(string content, int size)
    {
        var writer = new QRCodeWriter();
        // ⚠ 不给 CHARACTER_SET 的话 writer 会按 ISO-8859-1 编码，CJK 被【就地替换成 '?'】——
        //   解出来的 "??" 其实是忠实还原（坏在生成端）。真实世界的中文码都是 UTF-8，按此对齐。
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 4,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        };
        return writer.encode(content, BarcodeFormat.QR_CODE, size, size, hints);
    }

    /// <summary>把两张码画进同一张白底画布（左上/右上各一），构造多码场景。</summary>
    private static byte[] TwoCodesCanvas(BitMatrix a, BitMatrix b, int canvasW, int canvasH, int oy)
    {
        var buf = new byte[canvasW * canvasH * 4];
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = 255; }
        void Blit(BitMatrix m, int ox)
        {
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                    if (m[x, y])
                    {
                        int p = ((oy + y) * canvasW + ox + x) * 4;
                        buf[p] = 0; buf[p + 1] = 0; buf[p + 2] = 0; buf[p + 3] = 255;
                    }
        }
        Blit(a, 10);
        Blit(b, canvasW - 10 - a.Width);
        return buf;
    }

    // ═══════════════════════════════════════
    //  往返
    // ═══════════════════════════════════════

    private static void Test_RoundTrip_SingleCode()
    {
        // http 与纯文本各来一发 —— 覆盖 RGBLuminanceSource(BGRA32) 的字节序接法
        foreach (var content in new[] { "https://example.com/ttc", "TrueToneCap QR round trip 你好" })
        {
            var bm = EncodeQr(content, 300);
            var report = QrDecodeService.Decode(BitMatrixToBgra(bm), bm.Width, bm.Height);
            Assert($"往返解码：『{(content.Length > 20 ? content[..20] + "…" : content)}』",
                report.Payloads.Count == 1 && report.Payloads[0].Raw == content,
                $"got [{report.Payloads.Count}]: {string.Join(" | ", report.Payloads.Select(p => p.Raw))}");
        }
    }

    private static void Test_RoundTrip_MultipleCodes()
    {
        var a = EncodeQr("https://a.example/1", 200);
        var b = EncodeQr("https://b.example/2", 200);
        var canvas = TwoCodesCanvas(a, b, 460, 240, 20);
        var report = QrDecodeService.Decode(canvas, 460, 240);
        var raws = report.Payloads.Select(p => p.Raw).ToHashSet(StringComparer.Ordinal);
        Assert("多码检测：同图两个码都拿到", raws.SetEquals(["https://a.example/1", "https://b.example/2"]),
            $"got [{report.Payloads.Count}]: {string.Join(" | ", report.Payloads.Select(p => p.Raw))}");
        Assert("多码检测：两个码的分类都是 UrlHttp",
            report.Payloads.All(p => p.Kind == QrPayloadKind.UrlHttp));
    }

    private static void Test_RoundTrip_InvertedCode()
    {
        // 白码黑底（深色主题截图里的常见形态）—— 走 ALSO_INVERTED hint
        var content = "https://example.com/inv";
        var bm = EncodeQr(content, 300);
        var report = QrDecodeService.Decode(BitMatrixToBgra(bm, invert: true), bm.Width, bm.Height);
        Assert("反色码（白码黑底）也能解出", report.Payloads.Count == 1 && report.Payloads[0].Raw == content,
            $"got [{report.Payloads.Count}]: {string.Join(" | ", report.Payloads.Select(p => p.Raw))}");
    }

    private static void Test_RotatedImage_StillDecodes()
    {
        var content = "https://example.com/rot";
        var bm = EncodeQr(content, 300);
        var rotated = QrDecodeService.Rotate90Cw(BitMatrixToBgra(bm), bm.Width, bm.Height);
        var report = QrDecodeService.Decode(rotated, bm.Height, bm.Width); // 宽高对调
        Assert("旋转 90° 的码仍可解出", report.Payloads.Count == 1 && report.Payloads[0].Raw == content,
            $"got [{report.Payloads.Count}]: {string.Join(" | ", report.Payloads.Select(p => p.Raw))}");
    }

    private static void Test_Rotate90Cw_PixelMapping()
    {
        // 3×2 合成图，用不同灰度标记四个角 —— 直接对撞映射公式
        // 顺时针 90°：dst(dx,dy) ← src(dy, srcH-1-dx)
        var src = new byte[3 * 2 * 4];
        void Put(int x, int y, byte v) { int i = (y * 3 + x) * 4; src[i] = v; src[i + 1] = v; src[i + 2] = v; src[i + 3] = 255; }
        Put(0, 0, 10); Put(1, 0, 20); Put(2, 0, 30);
        Put(0, 1, 40); Put(1, 1, 50); Put(2, 1, 60);
        var dst = QrDecodeService.Rotate90Cw(src, 3, 2);
        byte Get(int x, int y, byte[] b) { int i = (y * 2 + x) * 4; return b[i]; }
        Assert("旋转映射：dst(0,0)=src(0,1)", Get(0, 0, dst) == 40);
        Assert("旋转映射：dst(1,0)=src(0,0)", Get(1, 0, dst) == 10);
        Assert("旋转映射：dst(0,1)=src(1,1)", Get(0, 1, dst) == 50);
        Assert("旋转映射：dst(1,1)=src(1,0)", Get(1, 1, dst) == 20);
    }

    // ═══════════════════════════════════════
    //  处置分类 —— "能不能打开"的唯一事实源
    // ═══════════════════════════════════════

    private static void Test_Classify_WhitelistOnlyHttpOpens()
    {
        Assert("https → UrlHttp", QrDecodeService.Classify("https://example.com/x") == QrPayloadKind.UrlHttp);
        Assert("http → UrlHttp", QrDecodeService.Classify("http://example.com/") == QrPayloadKind.UrlHttp);
        Assert("大写 HTTPS:// 也算 http（scheme 大小写不敏感）", QrDecodeService.Classify("HTTPS://EXAMPLE.COM/") == QrPayloadKind.UrlHttp);
        Assert("纯文本 → Text", QrDecodeService.Classify("你好，世界 1234567890") == QrPayloadKind.Text);
        Assert("WIFI: 配置串 → UrlOtherScheme（展示，不打开）",
            QrDecodeService.Classify("WIFI:T:WPA;S:home;P:password;;") == QrPayloadKind.UrlOtherScheme);
        Assert("mailto: → UrlOtherScheme", QrDecodeService.Classify("mailto:a@b.c") == QrPayloadKind.UrlOtherScheme);
        Assert("带凭据的 https 仍是 UrlHttp（打开时由浏览器处理 userinfo）",
            QrDecodeService.Classify("https://user:pass@example.com/") == QrPayloadKind.UrlHttp);
    }

    private static void Test_Classify_DangerousSchemesGetRedFlag()
    {
        string[] dangerous =
        {
            "file:///C:/Windows/System32/calc.exe",
            "ms-msdt:-id PCWDiagnostic",
            "search-ms:query=evil&crumb=location",
            "shell:Downloads",
            "vbscript:MsgBox(1)",
            "javascript:alert(1)",
            "data:text/html,<script>alert(1)</script>",
            "ms-settings:windows-defender",
        };
        foreach (var d in dangerous)
            Assert($"危险 scheme 红牌：{d.Split(':').First()}…",
                QrDecodeService.Classify(d) == QrPayloadKind.DangerousScheme, $"got {QrDecodeService.Classify(d)}");
    }

    private static void Test_Classify_TrickyInputsNeverBecomeHttp()
    {
        // 这些形态都不是浏览器会当 URL 处理的写法 —— 宁可当 Text 展示，也不给"可打开"的判定
        Assert("前导空格 https:// → 不算 UrlHttp", QrDecodeService.Classify(" https://evil.example") == QrPayloadKind.Text);
        Assert("https 空格冒号斜杠 → 不算 UrlHttp", QrDecodeService.Classify("https ://evil.example") == QrPayloadKind.Text);
        Assert("反斜杠序列 https:\\evil → 算 UrlHttp（浏览器会把 \\ 规一化为 /，这就是它字面声称的 URL）",
            QrDecodeService.Classify("https:\\\\evil.example") == QrPayloadKind.UrlHttp);
        Assert("协议相对形态 //evil → 不算 UrlHttp", QrDecodeService.Classify("//evil.example/x") == QrPayloadKind.Text);
        // 位置 0 的 scheme 检查：前缀字符不是字母 → 不是 scheme
        Assert("以数字开头 → Text", QrDecodeService.Classify("1234567890") == QrPayloadKind.Text);
    }

    // ═══════════════════════════════════════
    //  展示转义
    // ═══════════════════════════════════════

    private static void Test_DisplayText_EscapesInvisibleCharacters()
    {
        var clean = QrDecodeService.ToDisplayText("https://example.com/ok", out bool rtl, out bool hidden);
        Assert("干净 URL：DisplayText == Raw 且无旗标", clean == "https://example.com/ok" && !rtl && !hidden);

        var bidi = QrDecodeService.ToDisplayText("abc\u202Emno", out rtl, out hidden);
        Assert("RLO (U+202E)：打 RTL 旗标", rtl && hidden);
        Assert("RLO：原字符不原样出现、以转义形式可见", !bidi.Contains('\u202E') && bidi.Contains("\\u202E"),
            $"got: {bidi}");

        var crlf = QrDecodeService.ToDisplayText("http://x\r\nHost: evil", out rtl, out hidden);
        Assert("CRLF 注入惯技：打隐藏字符旗标且转义可见", hidden && crlf.Contains("\\u000D") && crlf.Contains("\\u000A"),
            $"got: {crlf.Replace("\r", "<CR>").Replace("\n", "<LF>")}");

        var zw = QrDecodeService.ToDisplayText("to\u200Bken", out rtl, out hidden);
        Assert("零宽空格：转义可见（防『看到的 token 与复制的 token 不一致』）",
            hidden && !zw.Contains('\u200B') && zw.Contains("\\u200B"));
    }

    private static void Test_CanOpenInBrowser_GatesOnCleanliness()
    {
        var ok = new QrPayload { Raw = "https://example.com/", Kind = QrPayloadKind.UrlHttp };
        Assert("干净 http：允许打开", ok.CanOpenInBrowser);

        var dirty = new QrPayload
        {
            Raw = "https://example.com/\rX",
            Kind = QrPayloadKind.UrlHttp,
            DisplayText = QrDecodeService.ToDisplayText("https://example.com/\rX", out _, out bool h),
            HasHiddenChars = h,
        };
        Assert("http 但含控制字符：禁止打开", !dirty.CanOpenInBrowser);

        var danger = new QrPayload { Raw = "file:///C:/x", Kind = QrPayloadKind.DangerousScheme };
        Assert("file: 红牌：禁止打开", !danger.CanOpenInBrowser);

        var other = new QrPayload { Raw = "WIFI:T:WPA;;;", Kind = QrPayloadKind.UrlOtherScheme };
        Assert("其它协议：禁止打开", !other.CanOpenInBrowser);
    }

    private static void Test_ToStringNeverLeaksPayload()
    {
        // 哨兵：QrPayload 若被改成 record，默认 ToString 会把 Raw 带出去 ——
        // 任何日志里一句 $"{payload}" 就泄露二维码内容。现在是 class（ToString = 类型名），必须保持。
        var p = new QrPayload { Raw = "SECRET-QR-CONTENT-0123", Kind = QrPayloadKind.UrlHttp };
        Assert("QrPayload.ToString() 不携带内容（防 record 化后日志泄露）",
            !p.ToString()!.Contains("SECRET-QR-CONTENT-0123"), $"got: {p}");

        var r = new QrScanReport();
        r.Payloads.Add(p);
        Assert("QrScanReport.ToString() 不携带内容",
            !r.ToString()!.Contains("SECRET-QR-CONTENT-0123"), $"got: {r}");
    }

    private static void Test_Guards_BadParametersReturnEmpty()
    {
        var ok = QrDecodeService.Decode(BitMatrixToBgra(EncodeQr("x", 200)), 200, 200);
        Assert("对照组：正常输入有结果", ok.Any);

        Assert("宽高为 0 → 空报告不抛异常", !QrDecodeService.Decode(new byte[16], 0, 200).Any);
        Assert("缓冲小于 w*h*4 → 空报告不抛异常", !QrDecodeService.Decode(new byte[100], 50, 50).Any);
        Assert("null 缓冲 → 空报告不抛异常", !QrDecodeService.Decode(null!, 10, 10).Any);

        // 没有码的图（纯白）→ 空报告
        var blank = new byte[200 * 200 * 4];
        for (int i = 0; i < blank.Length; i += 4) { blank[i] = 255; blank[i + 1] = 255; blank[i + 2] = 255; blank[i + 3] = 255; }
        Assert("纯白图 → 空报告", !QrDecodeService.Decode(blank, 200, 200).Any);
    }

    private static void Test_Dedupe_SameCodeReportedOnce()
    {
        // 同一个码画两遍 —— 多码检测器在相邻切片上可能重复报，服务必须去重
        var bm = EncodeQr("https://dupe.example/1", 160);
        var canvas = TwoCodesCanvas(bm, bm, 380, 200, 20);
        var report = QrDecodeService.Decode(canvas, 380, 200);
        Assert("同码两份只报一条", report.Payloads.Count == 1 && report.Payloads[0].Raw == "https://dupe.example/1",
            $"got [{report.Payloads.Count}]: {string.Join(" | ", report.Payloads.Select(p => p.Raw))}");
    }
}
