// OcrAccBench.cs — OCR 准确度/可靠性基准测试
// 测试: 不同字体大小 / 不同语言 / 手写体模拟
// 由 FormatBench --ocr-bench 分派调用
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-bench <modelDir>

using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using TrueToneCap.Core.Services;

/// <summary>OCR 准确度/可靠性基准。</summary>
public static class OcrBench
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        Console.WriteLine($"════════ OCR 准确度基准 ════════");
        Console.WriteLine($"模型目录: {modelDir}");
        Console.WriteLine($"模型存在: det={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx"))} " +
            $"rec={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx"))} " +
            $"dict={File.Exists(Path.Combine(modelDir, "ppocrv6_dict.txt"))}\n");

        var engines = new List<(string Name, OnnxOcrEngine Engine)>();
        try { engines.Add(("GPU(DirectML)", new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir))); }
        catch (Exception ex) { Console.WriteLine($"⚠ GPU 初始化失败: {ex.Message}"); }
        try { engines.Add(("CPU", new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir))); }
        catch (Exception ex) { Console.WriteLine($"⚠ CPU 初始化失败: {ex.Message}"); }

        if (engines.Count == 0) { Console.WriteLine("❌ 无可用 OCR 引擎"); return; }
        foreach (var (name, eng) in engines)
            Console.WriteLine($"{name}: available={eng.Info.IsAvailable}");

        var testCases = new List<(string Name, string[] Lines, bool Handwritten)>
        {
            ("英文大写", ["HELLO WORLD", "QUICK BROWN FOX", "OCR TEST 123"], false),
            ("英文小写", ["hello world", "the quick brown fox", "optical character recognition"], false),
            ("中文简体", ["你好世界", "光学字符识别", "截图工具测试"], false),
            ("中英混合", ["Hello 你好", "OCR 识别测试", "TrueToneCap 2026"], false),
            ("日文", ["こんにちは", "日本語テスト", "文字認識"], false),
            ("韩文", ["안녕하세요", "한국어 테스트", "문자인식"], false),
            ("手写体模拟", ["Hello World", "人工智能", "12345"], true),
        };

        int[] fontSizes = [12, 16, 24, 36, 48];

        double totalCharCorrect = 0, totalCharCount = 0;
        int totalLineCorrect = 0, totalLineCount = 0;

        foreach (var (name, lines, handwritten) in testCases)
        {
            Console.WriteLine($"\n── {name} ──");
            double bestAcc = 0; int bestSize = 0;
            foreach (int size in fontSizes)
            {
                var bmp = RenderLines(lines, size, handwritten, out var imgW, out var imgH);
                var bgra = BitmapToBgra(bmp);
                bmp.Dispose();

                // 优先 CPU 引擎 (验证 DirectML 图优化问题的规避)
                var cpuEng = engines.FirstOrDefault(e => e.Name == "CPU");
                var engine = cpuEng.Name is not null ? cpuEng.Engine : engines[0].Engine;
                var result = engine.RecognizeAsync(bgra, imgW, imgH, "zh-en").GetAwaiter().GetResult();

                var ocrText = result.Text ?? "";
                var (cc, cn, lc, ln) = CalcAccuracy(ocrText, lines);
                double acc = cn > 0 ? (double)cc / cn : 0;

                totalCharCorrect += cc; totalCharCount += cn;
                totalLineCorrect += lc; totalLineCount += ln;

                if (acc > bestAcc) { bestAcc = acc; bestSize = size; }

                Console.WriteLine($"  {size}px: 字符={cc}/{cn} ({acc:P1}) 行={lc}/{ln} | 识别: {(ocrText.Length > 60 ? ocrText[..60] + "..." : ocrText)}");
            }
            Console.WriteLine($"  ★ 最佳: {bestSize}px → {bestAcc:P1}");
        }

        Console.WriteLine($"\n════════ 汇总 ════════");
        Console.WriteLine($"总字符准确率: {totalCharCorrect}/{totalCharCount} ({(totalCharCount > 0 ? (double)totalCharCorrect / totalCharCount : 0):P1})");
        Console.WriteLine($"总行准确率: {totalLineCorrect}/{totalLineCount} ({(totalLineCount > 0 ? (double)totalLineCorrect / totalLineCount : 0):P1})");

        foreach (var (_, eng) in engines) eng.Dispose();
    }

    static Bitmap RenderLines(string[] lines, int fontSize, bool handwritten, out int w, out int h)
    {
        using var font = new Font(
            handwritten ? "Segoe Print" : "Segoe UI",
            fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        int maxW = 0;
        foreach (var line in lines)
        {
            var sz = g.MeasureString(line, font);
            maxW = Math.Max(maxW, (int)Math.Ceiling(sz.Width));
        }
        int textH = (int)Math.Ceiling(font.GetHeight());
        int pad = 20;
        w = maxW + pad * 2;
        h = textH * lines.Length + pad * 2;

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var rg = Graphics.FromImage(result))
        {
            rg.Clear(Color.White);
            // 无抗锯齿 (SingleBitPerPixel) 更接近 OCR 训练数据 (印刷/截图)
            rg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(Color.Black);
            for (int i = 0; i < lines.Length; i++)
                rg.DrawString(lines[i], font, brush, pad, pad + i * textH);
        }
        return result;
    }

    static byte[] BitmapToBgra(Bitmap bmp)
    {
        var bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = bmpData.Stride;
        var raw = new byte[stride * bmpData.Height];
        Marshal.Copy(bmpData.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(bmpData);

        var bgra = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++)
            Buffer.BlockCopy(raw, y * stride, bgra, y * bmp.Width * 4, bmp.Width * 4);
        return bgra;
    }

    static (int cc, int cn, int lc, int ln) CalcAccuracy(string ocrText, string[] expectedLines)
    {
        var ocr = ocrText.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
        var expected = string.Concat(expectedLines).Replace(" ", "");

        int cn = expected.Length, cc = 0;
        for (int i = 0; i < Math.Min(ocr.Length, expected.Length); i++)
            if (ocr[i] == expected[i]) cc++;

        int lc = 0, ln = expectedLines.Length;
        var ocrNoSpace = ocrText.Replace(" ", "");
        foreach (var line in expectedLines)
            if (ocrNoSpace.Contains(line.Replace(" ", ""))) lc++;
        return (cc, cn, lc, ln);
    }
}