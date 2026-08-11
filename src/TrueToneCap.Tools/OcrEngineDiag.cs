// OcrEngineDiag.cs — 引擎级诊断：完全复现 RunDetection 路径，对比独立推理
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-eng <modelDir>
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TrueToneCap.Core.Services;

public static class OcrEngineDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        Console.WriteLine($"═══ 引擎级 OCR 诊断: {modelDir} ═══\n");

        // 1. 用户场景: 1242x831 区域 (和用户日志一致) — 大号文字
        TestImage(modelDir, "用户场景 1242x831", 1242, 831, 40, ["HELLO WORLD", "OCR TEST 123", "你好世界 测试"]);
        Console.WriteLine();
        // 2. 小图场景: 385x104 (OcrBench 用的图)
        TestImage(modelDir, "小图 385x104", 385, 104, 48, ["HELLO WORLD"]);
    }

    static void TestImage(string modelDir, string name, int w, int h, int fontSize, string[] lines)
    {
        Console.WriteLine($"── {name} ({w}x{h}) ──");
        using var bmp = RenderLines(lines, fontSize, w, h);
        var bgra = BitmapToBgra(bmp);

        // A. 引擎完整识别 (CPU)
        try
        {
            using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
            var result = engine.RecognizeAsync(bgra, bmp.Width, bmp.Height, "zh-en").GetAwaiter().GetResult();
            Console.WriteLine($"引擎识别: {(string.IsNullOrEmpty(result.Text) ? "(空)" : result.Text.Replace("\n", " | "))}  error={result.Error ?? "null"}");
        }
        catch (Exception ex) { Console.WriteLine($"引擎识别异常: {ex.Message}"); }

        // B. 独立 det 推理 (完全复现 RunDetection 参数: limitSide=736, maxRatio=6)
        try
        {
            string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
            if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
            int limitSide = 736;
            float ratio;
            if (Math.Min(bmp.Width, bmp.Height) < limitSide)
            {
                ratio = (float)limitSide / Math.Min(bmp.Width, bmp.Height);
                ratio = Math.Min(ratio, 6.0f);
            }
            else ratio = 1.0f;
            int rw = Math.Max((int)Math.Round(bmp.Width * ratio / 32) * 32, 32);
            int rh = Math.Max((int)Math.Round(bmp.Height * ratio / 32) * 32, 32);
            Console.WriteLine($"det resize: {bmp.Width}x{bmp.Height} → {rw}x{rh} (ratio={ratio:F2})");

            using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var sess = new InferenceSession(detPath, opts);
            var inputName = sess.InputMetadata.Keys.First();
            var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });
            // 双线性填充 (同引擎)
            float ratioW = (float)bmp.Width / rw;
            float ratioH = (float)bmp.Height / rh;
            for (int y = 0; y < rh; y++)
            {
                float srcYf = y * ratioH;
                int y0 = Math.Clamp((int)srcYf, 0, bmp.Height - 1);
                int y1 = Math.Clamp(y0 + 1, 0, bmp.Height - 1);
                float wy = srcYf - y0;
                for (int x = 0; x < rw; x++)
                {
                    float srcXf = x * ratioW;
                    int x0 = Math.Clamp((int)srcXf, 0, bmp.Width - 1);
                    int x1 = Math.Clamp(x0 + 1, 0, bmp.Width - 1);
                    float wx = srcXf - x0;
                    for (int c = 0; c < 3; c++)
                    {
                        float v00 = bgra[(y0 * bmp.Width + x0) * 4 + c] / 255f;
                        float v01 = bgra[(y0 * bmp.Width + x1) * 4 + c] / 255f;
                        float v10 = bgra[(y1 * bmp.Width + x0) * 4 + c] / 255f;
                        float v11 = bgra[(y1 * bmp.Width + x1) * 4 + c] / 255f;
                        float val = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                        input[0, c, y, x] = (val - 0.5f) / 0.5f;
                    }
                }
            }
            using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
            var output = results.First().AsTensor<float>();
            float sum = 0, maxV = float.MinValue;
            int over03 = 0, over05 = 0, over07 = 0;
            long total = output.Length;
            for (int i = 0; i < total; i++)
            {
                float v = output.GetValue(i);
                sum += v; maxV = Math.Max(maxV, v);
                if (v > 0.3f) over03++;
                if (v > 0.5f) over05++;
                if (v > 0.7f) over07++;
            }
            Console.WriteLine($"det 统计: max={maxV:F3} avg={sum / total:F4} >0.3:{over03} >0.5:{over05} >0.7:{over07}");
        }
        catch (Exception ex) { Console.WriteLine($"det 独立推理异常: {ex.Message}"); }
    }

    static Bitmap RenderLines(string[] lines, int fontSize, int maxW, int maxH)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        int textH = (int)Math.Ceiling(font.GetHeight());
        int pad = 30;
        int w = maxW, h = Math.Min(maxH, textH * lines.Length + pad * 2);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var rg = Graphics.FromImage(bmp))
        {
            rg.Clear(Color.White);
            rg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(Color.Black);
            for (int i = 0; i < lines.Length; i++)
                rg.DrawString(lines[i], font, brush, pad, pad + i * textH);
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
