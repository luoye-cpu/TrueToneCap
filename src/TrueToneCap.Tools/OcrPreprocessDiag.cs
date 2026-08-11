// OcrPreprocessDiag.cs — det 输入预处理变体对比：找出模型期望的正确预处理
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-pre <modelDir>
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class OcrPreprocessDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        Console.WriteLine($"═══ det 预处理变体对比: {detPath} ═══\n");

        // 生成带文字的图 (白底黑字, 48px)
        using var bmp = RenderText("HELLO WORLD", 48);
        var bgra = BitmapToBgra(bmp);
        int w = bmp.Width, h = bmp.Height;
        Console.WriteLine($"测试图: {w}x{h} (48px HELLO WORLD)\n");

        // 缩放: 长边 736 等比例
        float ratio = Math.Min(w, h) < 736 ? 736f / Math.Min(w, h) : 1f;
        ratio = Math.Min(ratio, 4f);
        int rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);

        using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var sess = new InferenceSession(detPath, opts);
        var inputName = sess.InputMetadata.Keys.First();

        // ── 变体矩阵 ──
        var variants = new (string Name, bool Bgr, float Mean, float Std)[]
        {
            ("BGR mean0.5 std0.5 (rapidocr)",  true,  0.5f, 0.5f),
            ("RGB  mean0.5 std0.5",            false, 0.5f, 0.5f),
            ("BGR mean0.0 std1.0 (raw)",       true,  0.0f, 1.0f),
            ("RGB  mean0.0 std1.0 (raw)",      false, 0.0f, 1.0f),
            ("BGR mean0.406 std0.225",         true,  0.406f, 0.225f),
            ("RGB  mean0.406 std0.225",        false, 0.406f, 0.225f),
            ("BGR mean0.485 std0.229",         true,  0.485f, 0.229f),
            ("RGB  mean0.485 std0.229",        false, 0.485f, 0.229f),
        };

        foreach (var (name, bgr, mean, std) in variants)
        {
            var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });
            for (int c = 0; c < 3; c++)
            {
                int srcC = bgr ? c : 2 - c; // BGR: c=0→B,1→G,2→R; RGB: c=0→R,1→G,2→B
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        int sx = Math.Min(w - 1, x * w / rw);
                        int sy = Math.Min(h - 1, y * h / rh);
                        int off = (sy * w + sx) * 4;
                        input[0, c, y, x] = (bgra[off + srcC] / 255f - mean) / std;
                    }
            }
            using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
            var output = results.First().AsTensor<float>();
            float sum = 0, maxV = float.MinValue;
            int over03 = 0, over01 = 0;
            long total = output.Length;
            for (int i = 0; i < total; i++)
            {
                float v = output.GetValue(i);
                sum += v; maxV = Math.Max(maxV, v);
                if (v > 0.3f) over03++;
                if (v > 0.1f) over01++;
            }
            Console.WriteLine($"{name,-32} max={maxV:F3} avg={sum / total:F4} >0.3: {over03}  >0.1: {over01}");
        }
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
