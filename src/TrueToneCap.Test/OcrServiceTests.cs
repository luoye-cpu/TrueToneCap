// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/OcrServiceTests.cs
// OCR 预处理 + LLM 清单测试 — 覆盖 BitmapPreprocessor (纯 CPU) 与三处 LLM 提供商/模型活表的闭合性
// 注: OnnxOcrEngine 核心推理依赖模型/GPU, 不在单元测试范围 (由 OcrAccBench 工具实测)

using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

/// <summary>OCR 预处理 + 翻译服务测试集。</summary>
public static class OcrServiceTests
{
    private static int _passed, _failed;

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap OCR 服务测试");
        Console.WriteLine("══════════════════════════════════════\n");

        // ── BitmapPreprocessor ──
        Console.WriteLine("── BitmapPreprocessor ──");
        Preprocess_ScaleUp_Dimensions();
        Preprocess_Contrast_MinMax();
        Preprocess_Contrast_Uniform();
        Preprocess_ScaleUp_Content();
        Preprocess_AdaptiveThreshold_BlackWhite();
        Preprocess_AutoPreprocess_SmallUpscale();
        Preprocess_AutoPreprocess_HighContrast_NoOp();

        // ── LLM 提供商/模型清单 ──
        Console.WriteLine("\n── LLM 提供商/模型清单 ──");
        LlmProviderTables_AreClosed();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--ocr-tests", _passed, _failed);
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  BitmapPreprocessor
    // ═══════════════════════════════════════

    static void Preprocess_ScaleUp_Dimensions()
    {
        var bgra = new byte[10 * 10 * 4];
        var r = BitmapPreprocessor.ScaleUp(bgra, 10, 10, 2.0f);
        Assert($"ScaleUp 2x: 20x20 (得 {r.Width}x{r.Height})", r.Width == 20 && r.Height == 20 && r.Pixels.Length == 20 * 20 * 4);
    }

    static void Preprocess_Contrast_MinMax()
    {
        // 低对比度图 (灰度 50-100) → 增强后应扩展到更大范围
        int w = 4, h = 4;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int off = i * 4;
            byte v = (byte)(50 + (i * 12) % 50); // 50-99
            bgra[off] = bgra[off + 1] = bgra[off + 2] = v;
            bgra[off + 3] = 255;
        }
        var enhanced = BitmapPreprocessor.EnhanceContrast(bgra, w, h);
        // 最小值应被拉伸到 0, 最大值到 255
        byte min = 255, max = 0;
        for (int i = 0; i < w * h; i++)
        {
            min = Math.Min(min, enhanced[i * 4]);
            max = Math.Max(max, enhanced[i * 4]);
        }
        Assert($"EnhanceContrast: 范围扩展 (min={min}, max={max})", max > 200 && min < 60);
    }

    static void Preprocess_Contrast_Uniform()
    {
        // 全灰图 → 无拉伸 (返回原样)
        int w = 4, h = 4;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int off = i * 4;
            bgra[off] = bgra[off + 1] = bgra[off + 2] = 128;
            bgra[off + 3] = 255;
        }
        var enhanced = BitmapPreprocessor.EnhanceContrast(bgra, w, h);
        Assert($"EnhanceContrast: 全灰不崩溃 (值={enhanced[0]})", enhanced[0] == 128);
    }

    static void Preprocess_ScaleUp_Content()
    {
        // 纯红像素双线性放大 → 仍为红
        int w = 2, h = 2;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int off = i * 4;
            bgra[off] = 0; bgra[off + 1] = 0; bgra[off + 2] = 255; bgra[off + 3] = 255;
        }
        var r = BitmapPreprocessor.ScaleUp(bgra, w, h, 2.0f);
        // 中心像素应仍为红 (R 高, B/G 低)
        int mid = (r.Height / 2) * r.Width * 4 + (r.Width / 2) * 4;
        Assert($"ScaleUp: 内容保留 (R={r.Pixels[mid + 2]})", r.Pixels[mid + 2] > 200 && r.Pixels[mid] < 60);
    }

    static void Preprocess_AdaptiveThreshold_BlackWhite()
    {
        // 黑白文字图 → 二值化后只含 0 或 255
        int w = 20, h = 20;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int off = (y * w + x) * 4;
            bool isText = (x >= 5 && x <= 8 && y >= 5 && y <= 15); // 竖条 = 文字
            byte v = isText ? (byte)0 : (byte)255;
            bgra[off] = bgra[off + 1] = bgra[off + 2] = v;
            bgra[off + 3] = 255;
        }
        var r = BitmapPreprocessor.AdaptiveThreshold(bgra, w, h);
        bool allBinary = true;
        for (int i = 0; i < w * h; i++)
        {
            byte v = r.Pixels[i * 4];
            if (v != 0 && v != 255) { allBinary = false; break; }
        }
        Assert($"AdaptiveThreshold: 二值化 (全部 0/255)", allBinary);
    }

    static void Preprocess_AutoPreprocess_SmallUpscale()
    {
        // 小图 (<400x200) → 应放大
        int w = 100, h = 100;
        var bgra = new byte[w * h * 4];
        var r = BitmapPreprocessor.AutoPreprocess(bgra, w, h);
        Assert($"AutoPreprocess: 小图放大 (得 {r.Width}x{r.Height})", r.Width == w * 2 && r.Height == h * 2);
    }

    static void Preprocess_AutoPreprocess_HighContrast_NoOp()
    {
        // 高对比度大图 → 不预处理 (Mode=None)
        int w = 500, h = 300;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int off = i * 4;
            byte v = (i % 2 == 0) ? (byte)0 : (byte)255; // 黑白交替 → 高对比度
            bgra[off] = bgra[off + 1] = bgra[off + 2] = v;
            bgra[off + 3] = 255;
        }
        var r = BitmapPreprocessor.AutoPreprocess(bgra, w, h);
        Assert($"AutoPreprocess: 高对比度不处理 (Mode={r.Mode})", r.Mode == BitmapPreprocessor.PreprocessMode.None);
    }

    // ═══════════════════════════════════════
    //  LLM 提供商/模型清单（三处活表必须闭合）
    // ═══════════════════════════════════════

    /// <summary>
    /// 生效的清单只有三处：XAML 的 LlmProviderCbo、XAML 的 LlmModelCbo、
    /// OnLlmProviderChanged 里那张 tag→(endpoint, 默认模型) 的映射。
    /// <para>原先这一节测的是 Core 里的 <c>LlmProviders.All/Models</c>（11 提供方 / 27 模型），
    /// 而那张表全仓唯一的消费者就是这三条断言本身 ⇒ 门禁绿的是一个用户碰不到的表，
    /// 三处真表之间的漂移反而没人看（例：Gemini 在 XAML 是 gemini-2.0-flash、
    /// 在 LlmProviders 是 gemini-3.5-flash）。Core 那张表已删，判据搬到活表上。
    /// Test 工程只引用 Core，枚举不了 WinUI 控件 ⇒ 只能读源文件文本，与仓库既有判据同法。</para>
    /// </summary>
    static void LlmProviderTables_AreClosed()
    {
        var xaml = ReadRepoFile("src", "TrueToneCap.App", "MainWindow.xaml");
        var handler = ReadRepoFile("src", "TrueToneCap.App", "MainWindow.OcrSettings.cs");
        if (xaml is null || handler is null)
        {
            Assert("LLM 清单判据：源文件在位（不在则说明文件被移动，判据当红不当绿）", false);
            return;
        }

        var providers = TagsOf(xaml, "LlmProviderCbo");
        var models = TagsOf(xaml, "LlmModelCbo");
        var map = HandlerMap(handler);

        Assert($"下拉里的每个提供方都在 handler 里有映射（提供方 {providers.Length} 项）",
            providers.Length >= 5
            && providers.Where(p => p != "Custom")
                .All(p => map.Any(m => m.tag == p)),
            string.Join(",", providers.Where(p => p != "Custom" && !map.Any(m => m.tag == p))));

        Assert("handler 里没有下拉已删掉的僵尸 case",
            map.All(m => providers.Contains(m.tag)),
            string.Join(",", map.Select(m => m.tag).Except(providers)));

        Assert("handler 注入的默认模型必须是模型下拉里存在的 Tag",
            map.All(m => m.model.Length == 0 || models.Contains(m.model)),
            string.Join(",", map.Where(m => m.model.Length > 0 && !models.Contains(m.model))
                                .Select(m => m.tag + "->" + m.model)));

        Assert($"模型下拉的 Tag 唯一（{models.Length} 项）",
            models.Length == models.Distinct().Count(),
            string.Join(",", models.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => g.Key)));

        Assert("非自定义提供方的 endpoint 必须是 https 且非空",
            map.Where(m => m.tag != "Custom").All(m => m.endpoint.StartsWith("https://")),
            string.Join(",", map.Where(m => m.tag != "Custom" && !m.endpoint.StartsWith("https://"))
                                .Select(m => m.tag + "=" + m.endpoint)));
    }

    static readonly string Qc = ((char)34).ToString();

    /// <summary>取某个 ComboBox 块内全部 ComboBoxItem 的 Tag。</summary>
    static string[] TagsOf(string xaml, string comboName)
    {
        int start = xaml.IndexOf("x:Name=" + Qc + comboName + Qc, StringComparison.Ordinal);
        if (start < 0) return [];
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        if (end < 0) return [];
        var list = new List<string>();
        foreach (var line in xaml[start..end].Split('\n'))
        {
            int i = line.IndexOf("Tag=" + Qc, StringComparison.Ordinal);
            if (i < 0) continue;
            int j = line.IndexOf(Qc, i + 5, StringComparison.Ordinal);
            if (j < 0) continue;
            list.Add(line.Substring(i + 5, j - i - 5));
        }
        return list.ToArray();
    }

    /// <summary>取 OnLlmProviderChanged 里 "tag" =&gt; ("endpoint", "model") 形态的映射行。</summary>
    static (string tag, string endpoint, string model)[] HandlerMap(string code)
    {
        var list = new List<(string, string, string)>();
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.Trim();
            int arrow = line.IndexOf("=> (", StringComparison.Ordinal);
            if (arrow <= 0 || !line.StartsWith(Qc)) continue;
            int tagEnd = line.IndexOf(Qc, 1, StringComparison.Ordinal);
            if (tagEnd < 0) continue;
            string tag = line.Substring(1, tagEnd - 1);
            int q1 = line.IndexOf(Qc, arrow + 1, StringComparison.Ordinal);
            int q2 = q1 < 0 ? -1 : line.IndexOf(Qc, q1 + 1, StringComparison.Ordinal);
            int q3 = q2 < 0 ? -1 : line.IndexOf(Qc, q2 + 1, StringComparison.Ordinal);
            int q4 = q3 < 0 ? -1 : line.IndexOf(Qc, q3 + 1, StringComparison.Ordinal);
            if (q4 < 0) continue;
            list.Add((tag, line.Substring(q1 + 1, q2 - q1 - 1), line.Substring(q3 + 1, q4 - q3 - 1)));
        }
        return list.ToArray();
    }

    /// <summary>从测试运行目录向上找仓库内文件。</summary>
    static string? ReadRepoFile(params string[] parts)
    {
        var rel = Path.Combine(parts);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, rel);
            if (File.Exists(cand)) return File.ReadAllText(cand);
        }
        return null;
    }

    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}"); }
    }
}