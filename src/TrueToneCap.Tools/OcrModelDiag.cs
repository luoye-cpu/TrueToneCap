// OcrModelDiag.cs — OCR 模型诊断：dump det/rec 输入输出签名 + det 输出统计
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-diag <modelDir>
using Microsoft.ML.OnnxRuntime;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class OcrModelDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        Console.WriteLine($"═══ OCR 模型诊断: {modelDir} ═══");

        var detPath = FindModel(modelDir, "PP-OCRv6_medium_det.onnx", "PP-OCRv6_det.onnx", "ch_PP-OCRv6_det_infer.onnx");
        var recPath = FindModel(modelDir, "PP-OCRv6_medium_rec.onnx", "PP-OCRv6_rec.onnx", "ch_PP-OCRv6_rec_infer.onnx");
        var dictPath = FindModel(modelDir, "ppocrv6_dict.txt", "ppocr_keys_v2.txt");

        Console.WriteLine($"det: {detPath ?? "MISSING"}");
        Console.WriteLine($"rec: {recPath ?? "MISSING"}");
        Console.WriteLine($"dict: {dictPath ?? "MISSING"}");
        if (detPath is null || recPath is null) return;

        if (dictPath is not null)
        {
            var lines = File.ReadAllLines(dictPath);
            Console.WriteLine($"字典: {lines.Length} 字符, 前10: [{string.Join(",", lines.Take(10))}]");
        }

        // ═══ det 模型签名 ═══
        try
        {
            using var detOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var detSession = new InferenceSession(detPath, detOpts);
            Console.WriteLine($"\n── det 输入 ──");
            foreach (var kv in detSession.InputMetadata)
                Console.WriteLine($"  {kv.Key}: {kv.Value.ElementType} {string.Join("x", kv.Value.Dimensions)}");
            Console.WriteLine($"── det 输出 ──");
            foreach (var kv in detSession.OutputMetadata)
                Console.WriteLine($"  {kv.Key}: {kv.Value.ElementType} {string.Join("x", kv.Value.Dimensions)}");
        }
        catch (Exception ex) { Console.WriteLine($"det 签名失败: {ex.Message}"); }

        // ═══ rec 模型签名 ═══
        try
        {
            using var recOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var recSession = new InferenceSession(recPath, recOpts);
            Console.WriteLine($"\n── rec 输入 ──");
            foreach (var kv in recSession.InputMetadata)
                Console.WriteLine($"  {kv.Key}: {kv.Value.ElementType} {string.Join("x", kv.Value.Dimensions)}");
            Console.WriteLine($"── rec 输出 ──");
            foreach (var kv in recSession.OutputMetadata)
                Console.WriteLine($"  {kv.Key}: {kv.Value.ElementType} {string.Join("x", kv.Value.Dimensions)}");
        }
        catch (Exception ex) { Console.WriteLine($"rec 签名失败: {ex.Message}"); }

        // ═══ det 输出统计: 用一张黑白文字图直接推理 ═══
        try
        {
            using var bmp = RenderText("HELLO WORLD", 48);
            var bgra = BitmapToBgra(bmp);
            int w = bmp.Width, h = bmp.Height;
            Console.WriteLine($"\n── det 推理测试 {w}x{h} ──");

            // 直接调 OnnxOcrEngine 的检测（通过反射不可行，走完整识别）
            using var engine = new TrueToneCap.Core.Services.OnnxOcrEngine(
                TrueToneCap.Core.Services.OnnxExecutionProvider.Cpu, modelDir);
            Console.WriteLine($"引擎可用: {engine.Info.IsAvailable}");

            // 预处理: 归一化 (0.5,0.5)
            int limitSide = 736;
            float ratio = Math.Min(w, h) < limitSide ? (float)limitSide / Math.Min(w, h) : 1f;
            ratio = Math.Min(ratio, 6f);
            int rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
            int rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
            Console.WriteLine($"resize: {w}x{h} → {rw}x{rh} (ratio={ratio:F2})");

            using var detOpts2 = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var detSession = new InferenceSession(detPath, detOpts2);
            var dims = new int[] { 1, 3, rh, rw };
            var input = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(dims);
            // 简单最近邻填充 (白底黑字) — 多维索引 [b][c][y][x]
            for (int c = 0; c < 3; c++)
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        int sx = Math.Min(w - 1, x * w / rw);
                        int sy = Math.Min(h - 1, y * h / rh);
                        int off = (sy * w + sx) * 4;
                        input[0, c, y, x] = (bgra[off + c] / 255f - 0.5f) / 0.5f;
                    }
            var results = detSession.Run(new[] { NamedOnnxValue.CreateFromTensor(detSession.InputMetadata.Keys.First(), input) });
            var output = results.First().AsTensor<float>();
            var dimsStr = string.Join("x", output.Dimensions.ToArray());
            Console.WriteLine($"det 输出形状: {dimsStr}");
            float sum = 0, maxV = float.MinValue, minV = float.MaxValue;
            int over05 = 0;
            int total = checked((int)output.Length);
            for (int i = 0; i < total; i++)
            {
                float v = output.GetValue(i);
                sum += v; maxV = Math.Max(maxV, v); minV = Math.Min(minV, v);
                if (v > 0.5f) over05++;
            }
            Console.WriteLine($"det 输出统计: min={minV:F3} max={maxV:F3} avg={sum / total:F4} >0.5的像素={over05}/{total} ({100.0 * over05 / total:F2}%)");
        }
        catch (Exception ex) { Console.WriteLine($"det 推理失败: {ex}"); }
    }

    static string? FindModel(string dir, params string[] names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static Bitmap RenderText(string text, int fontSize)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        var sz = g.MeasureString(text, font);
        int pad = 20;
        int w = (int)Math.Ceiling(sz.Width) + pad * 2;
        int h = (int)Math.Ceiling(font.GetHeight()) + pad * 2;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var rg = Graphics.FromImage(bmp))
        {
            rg.Clear(Color.White);
            rg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(Color.Black);
            rg.DrawString(text, font, brush, pad, pad);
        }
        return bmp;
    }

    static byte[] BitmapToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var result = new byte[bmp.Width * bmp.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, result, 0, result.Length);
        bmp.UnlockBits(data);
        return result;
    }
}
