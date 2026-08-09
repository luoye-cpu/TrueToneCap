// TrueToneCap.Test/OcrServiceTests.cs
// OCR 预处理 + 翻译服务测试 — 覆盖 BitmapPreprocessor (纯 CPU) 和 LlmProviders (纯数据)
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

        // ── LlmProviders ──
        Console.WriteLine("\n── LlmProviders ──");
        LlmProviders_NonEmpty();
        LlmProviders_UniqueModels();
        LlmProviders_Endpoints();

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
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
    //  LlmProviders
    // ═══════════════════════════════════════

    static void LlmProviders_NonEmpty()
    {
        Assert($"LlmProviders: 提供方非空 ({LlmProviders.All.Length})", LlmProviders.All.Length > 0);
    }

    static void LlmProviders_UniqueModels()
    {
        // 模型 tag 应唯一 (供 UI 下拉选择)
        var tags = LlmProviders.Models.Select(m => m.Tag).ToList();
        bool unique = tags.Count == tags.Distinct().Count();
        Assert($"LlmProviders: 模型 tag 唯一 ({tags.Count})", unique);
    }

    static void LlmProviders_Endpoints()
    {
        // 非自定义提供方应有非空 endpoint 和默认模型
        bool allValid = true;
        foreach (var p in LlmProviders.All)
        {
            if (p.Name == "自定义") continue;
            if (string.IsNullOrEmpty(p.Endpoint) || string.IsNullOrEmpty(p.DefaultModel))
            { allValid = false; break; }
        }
        Assert("LlmProviders: 提供方 endpoint/模型有效", allValid);
    }

    static void Assert(string name, bool condition)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}"); }
    }
}