// OcrRealBench.cs — OCR 真实语料基准测试
// 与固定内容的 --ocr-bench 不同, 本测试使用:
//   1. 大量真实场景文本 (代码/UI标签/文档段落/聊天记录/URL/数字表格...)
//   2. 真实截图风格渲染: ClearType 抗锯齿 / 深色模式 / 彩色背景 / 多种字体
//   3. 每次运行随机采样 (可复现种子), 避免固定内容过拟合
// 由 FormatBench --ocr-bench-real [seed] 分派调用

using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using TrueToneCap.Core.Services;

/// <summary>OCR 真实语料基准。</summary>
public static class OcrRealBench
{
    // ═══════════════════════════════════════
    //  真实语料库 (按场景分类)
    // ═══════════════════════════════════════

    static readonly string[][] Corpus =
    [
        // ── 场景1: 编程代码 (开发者截图最高频) ──
        ["public async Task<string> EncodeAsync(byte[] pixels)", "var settings = new EncodingSettings { Quality = 90f };", "if (result.Success) { return result.Value!; }", "await _captureTask.ConfigureAwait(false);"],
        ["for (int i = 0; i < boxes.Count; i++)", "Console.WriteLine($\"字符={cc}/{cn} ({acc:P1})\");", "private static readonly float[] s_byteToFloat = BuildLut();", "return string.Join(\"\\n\", allText);"],
        ["npm install microsoft.ml.onnxruntime.directml", "git commit -m \"fix: ArrayPool DenseTensor slice\"", "dotnet build src/TrueToneCap.App --configuration Release", "#pragma warning disable CS0618 // Obsolete API"],

        // ── 场景2: UI 标签/按钮 (软件界面) ──
        ["保存到文件", "复制到剪贴板", "取消", "应用", "确定"],
        ["文件", "编辑", "视图", "工具", "帮助"],
        ["输出格式:", "质量设置", "HDR 输出已禁用", "ICC 色彩管理", "自动检测显示器"],
        ["Settings saved successfully", "Operation cancelled by user", "Loading models...", "Connection timed out"],

        // ── 场景3: 文档段落 ──
        ["光学字符识别（英语：Optical Character Recognition，OCR）是指对文本资料的图像文件进行分析识别处理，获取文字及版面信息的过程。", "截图工具的核心价值在于忠实还原屏幕上的每一个像素——包括 HDR 高动态范围、广色域色彩空间以及精确的 ICC 色彩管理。"],
        ["The quick brown fox jumps over the lazy dog. This pangram contains every letter of the English alphabet at least once.", "Machine learning models require careful preprocessing to achieve optimal accuracy in production environments."],

        // ── 场景4: 数字/日期/表格 ──
        ["2026-08-25 15:42:07.123", "¥12,345.67", "(86) 138-0013-8000", "SKU: TTC-4K-HDR-PRO"],
        ["CPU 45% | 内存 8.2GB | GPU 78%", "1920x1080 @ 144Hz", "10.0.26200.5670", "+86 010-12345678"],

        // ── 场景5: URL/路径/邮箱 ──
        ["https://github.com/microsoft/onnxruntime", "C:\\Users\\20210\\Documents\\TrueToneCap\\settings.json", "support@truetonecap.example.com", "%LOCALAPPDATA%\\Packages"],

        // ── 场景6: 聊天记录 (社交截图) ──
        ["明天下午3点开会记得带上笔记本", "这个 bug 什么时候能修复？", "好的，我马上发给你 📎", "[图片] [表情] 已读"],
        ["Sure, I'll send it over ASAP 👍", "Can you review my PR?", "Meeting moved to 4pm EST"],

        // ── 场景7: 新闻/资讯标题 ──
        ["微软发布 Windows App SDK 2.4：WinUI 3 性能大幅提升", "NVIDIA RTX 5090 显卡评测：AI 算力翻倍", "OpenAI 推出新一代推理模型 o5-preview"],
        ["Breaking: Major update rolling out to all users next week", "Study shows 40% improvement in recognition accuracy"],

        // ── 场景8: 日文真实内容 ──
        ["こんにちは世界、これはテストです。", "ファイルを保存しました。", "設定を変更しますか？"],
        ["東京都渋谷区神南1-19-11", "お問い合わせ：03-1234-5678"],

        // ── 场景9: 混合排版 (标点+全半角) ──
        ["注意：此操作不可撤销！", "\"引用文本\" —— 出自《示例文档》第3页", "【重要】系统将于凌晨2点维护"],
        ["Q: How does it work? A: Simple!", "Item #1 — $29.99 (was $49.99)"],

        // ── 场景10: 数学/单位 ──
        ["E = mc² ≈ 9×10¹⁶ J/kg", "温度: -273.15°C ~ +100°C", "π ≈ 3.14159265", "∑ f(x)dx → ∞"],
    ];

    // 渲染风格 (模拟真实截图多样性)
    enum RenderStyle { LightAntiAlias, DarkMode, ColoredBackground, GrayText, SmallUiLabel }

    static readonly (string FontName, FontStyle Style)[] Fonts =
    [
        ("Segoe UI", FontStyle.Regular),
        ("Microsoft YaHei UI", FontStyle.Regular),
        ("Consolas", FontStyle.Regular),
        ("Arial", FontStyle.Regular),
        ("Tahoma", FontStyle.Regular),
        ("Segoe UI", FontStyle.Bold),
        ("Microsoft YaHei UI", FontStyle.Bold),
    ];

    public static void Run(string[] args)
    {
        // 可复现随机: 默认 seed=当天日期, 也可指定
        int seed = args.Length >= 2 && int.TryParse(args[1], out var s) ? s : DateTime.Now.Day * 100 + DateTime.Now.Month;
        var rng = new Random(seed);

        string modelDir = Path.Combine(AppContext.BaseDirectory, "data", "Models");
        // 兼容源码树直接调用: 回退到 App 项目 Models 目录
        if (!File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx")))
            modelDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TrueToneCap.App", "Models");

        Console.WriteLine($"════════ OCR 真实语料基准 ════════");
        Console.WriteLine($"模型目录: {modelDir}");
        Console.WriteLine($"随机种子: {seed} (同种子结果可复现)");
        Console.WriteLine($"语料组数: {Corpus.Length} | 每组采样 2 种风格 × 2 种字号\n");

        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
        if (!engine.Info.IsAvailable) { Console.WriteLine("❌ CPU OCR 引擎不可用"); return; }
        Console.WriteLine($"引擎: PP-OCRv6 (CPU)\n");

        double totalCC = 0, totalCN = 0;
        int totalLC = 0, totalLN = 0;
        var failures = new List<string>();

        foreach (var lines in Corpus)
        {
            // 每语料组随机选 2 种风格 × 2 种字号 = 4 个样本
            for (int sample = 0; sample < 4; sample++)
            {
                var style = (RenderStyle)rng.Next(0, 5);
                int fontSize = style == RenderStyle.SmallUiLabel
                    ? rng.Next(9, 13)                       // 小字 UI: 9-12px
                    : new[] { 12, 14, 16, 18, 22, 28 }[rng.Next(0, 6)];
                var (fontName, fontStyle) = Fonts[rng.Next(0, Fonts.Length)];

                var bmp = RenderLines(lines, fontName, fontStyle, fontSize, style, rng, out var imgW, out var imgH);
                var bgra = BitmapToBgra(bmp);
                bmp.Dispose();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = engine.RecognizeAsync(bgra, imgW, imgH).GetAwaiter().GetResult();
                sw.Stop();

                var ocrText = result.Text ?? "";
                var (cc, cn) = CalcCharAccuracy(ocrText, lines);
                totalCC += cc; totalCN += cn;

                bool anyLineOk = false;
                foreach (var line in lines)
                {
                    totalLN++;
                    if (FuzzyContains(Normalize(ocrText), Normalize(line))) { totalLC++; anyLineOk = true; }
                }
                _ = anyLineOk; // 行命中详情已在 failures 中体现

                double acc = cn > 0 ? (double)cc / cn : 0;
                string status = acc switch { >= 0.99 => "✅", >= 0.85 => "🟡", _ => "❌" };
                if (acc < 0.85)
                    failures.Add($"{status} [{style} {fontSize}px {fontName.Split(' ')[0]}] 期望={Trunc(string.Join("|", lines), 40)} 识别={Trunc(ocrText.Replace("\n", "⏎"), 40)} ({cc}/{cn})");

                Console.WriteLine($"{status} [{style,-17} {fontSize,2}px] 字符={cc}/{cn} ({acc,6:P1}) {sw.ElapsedMilliseconds,4}ms | {Trunc(ocrText.Replace("\n", " ⏎ "), 46)}");
            }
        }

        Console.WriteLine($"\n════════ 汇总 ════════");
        Console.WriteLine($"总字符准确率: {totalCC}/{totalCN} ({(totalCN > 0 ? totalCC / totalCN : 0):P2})");
        Console.WriteLine($"总行准确率:   {totalLC}/{totalLN} ({(totalLN > 0 ? (double)totalLC / totalLN : 0):P2})");
        Console.WriteLine($"失败样本数 (<85%): {failures.Count}");

        if (failures.Count > 0)
        {
            Console.WriteLine($"\n── 失败样本明细 ──");
            foreach (var f in failures.Take(15))
                Console.WriteLine(f);
            if (failures.Count > 15)
                Console.WriteLine($"... 及其他 {failures.Count - 15} 个");
        }
    }

    // ═══════════════════════════════════════
    //  真实截图风格渲染
    // ═══════════════════════════════════════

    static Bitmap RenderLines(string[] lines, string fontName, FontStyle fontStyle, int fontSize,
        RenderStyle style, Random rng, out int w, out int h)
    {
        using var font = new Font(fontName, fontSize, fontStyle, GraphicsUnit.Pixel);
        int textH;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            int maxW = 0;
            foreach (var line in lines)
                maxW = Math.Max(maxW, (int)Math.Ceiling(pg.MeasureString(line, font).Width));
            textH = (int)Math.Ceiling(font.GetHeight());
            int pad = 16;
            w = maxW + pad * 2;
            h = textH * lines.Length + pad * 2;
        }

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);

        // ── 背景 + 前景色 (模拟真实 UI) ──
        Color bg, fg;
        switch (style)
        {
            case RenderStyle.DarkMode:
                bg = Color.FromArgb(rng.Next(24, 40), rng.Next(24, 40), rng.Next(28, 48));   // 深灰蓝
                fg = Color.FromArgb(rng.Next(220, 256), rng.Next(220, 256), rng.Next(225, 256)); // 近白
                break;
            case RenderStyle.ColoredBackground:
                // 彩色 UI (如蓝底白字/米黄底黑字)
                if (rng.Next(2) == 0) { bg = Color.FromArgb(30, 90, 170); fg = Color.White; }
                else { bg = Color.FromArgb(253, 246, 227); fg = Color.FromArgb(60, 50, 40); }
                break;
            case RenderStyle.GrayText:
                bg = Color.White;
                fg = Color.FromArgb(rng.Next(110, 160), rng.Next(110, 160), rng.Next(110, 160)); // 灰字 (次要信息)
                break;
            default: // LightAntiAlias / SmallUiLabel
                bg = Color.White; fg = Color.Black;
                break;
        }
        g.Clear(bg);

        // ClearType 抗锯齿 (真实 Windows 渲染默认值; 与旧 bench 的 SingleBitPerPixel 相反!)
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var brush = new SolidBrush(fg);
        for (int i = 0; i < lines.Length; i++)
            g.DrawString(lines[i], font, brush, 16, 16 + i * textH);

        return result;
    }

    static byte[] BitmapToBgra(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var raw = new byte[stride * data.Height];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(data);

        var bgra = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++)
            Buffer.BlockCopy(raw, y * stride, bgra, y * bmp.Width * 4, bmp.Width * 4);
        return bgra;
    }

    // ═══════════════════════════════════════
    //  评估辅助
    // ═══════════════════════════════════════

    static string Normalize(string s) =>
        s.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "")
         .Replace("　", "").Trim().ToLowerInvariant();

    /// <summary>字符级准确度: 基于 LCS (最长公共子序列), 容忍插入/删除而非仅位置对齐。
    /// 真实文本中漏一个字会导致旧实现后续全部错位, LCS 更公平。</summary>
    static (int cc, int cn) CalcCharAccuracy(string ocrText, string[] expectedLines)
    {
        var a = Normalize(ocrText);
        var b = Normalize(string.Concat(expectedLines));
        if (b.Length == 0) return (0, 0);
        int lcs = LcsLength(a, b);
        return (lcs, b.Length);
    }

    static int LcsLength(string a, string b)
    {
        // 空间优化: 两行滚动数组
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return prev[b.Length];
    }

    /// <summary>模糊行匹配: 行文本被完整包含 (归一化后) 即算命中; 允许首尾截断差异。</summary>
    static bool FuzzyContains(string ocrNorm, string lineNorm)
    {
        if (lineNorm.Length == 0) return false;
        if (ocrNorm.Contains(lineNorm)) return true;
        // 行长时容忍 80% 子串命中 (检测框可能截断超长行)
        if (lineNorm.Length > 30)
        {
            var head = lineNorm[..Math.Min(20, lineNorm.Length)];
            var tail = lineNorm[^Math.Min(20, lineNorm.Length)..];
            return ocrNorm.Contains(head) && ocrNorm.Contains(tail);
        }
        return false;
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
