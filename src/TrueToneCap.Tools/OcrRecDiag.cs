// OcrRecDiag.cs — rec 模型单独诊断：构造已知文字条，检查解码输出
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-rec <modelDir>
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class OcrRecDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string recPath = Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx");
        if (!File.Exists(recPath)) recPath = Path.Combine(modelDir, "PP-OCRv6_rec.onnx");
        string dictPath = Path.Combine(modelDir, "ppocrv6_dict.txt");
        if (!File.Exists(dictPath)) dictPath = Path.Combine(modelDir, "ppocr_keys_v2.txt");
        Console.WriteLine($"═══ rec 单独诊断: {Path.GetFileName(recPath)} ═══\n");

        var dict = File.ReadAllLines(dictPath);
        Console.WriteLine($"字典: {dict.Length} 字符, 前20: [{string.Join("", dict.Take(20))}]");

        using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var sess = new InferenceSession(recPath, opts);
        Console.WriteLine($"rec 输入: {string.Join(",", sess.InputMetadata.Select(kv => kv.Key + "=" + string.Join("x", kv.Value.Dimensions)))}");
        Console.WriteLine($"rec 输出: {string.Join(",", sess.OutputMetadata.Select(kv => kv.Key + "=" + string.Join("x", kv.Value.Dimensions)))}\n");

        var inputName = sess.InputMetadata.Keys.First();

        // ── 测试 1: 48px 高文字条 (HELLO WORLD) ──
        TestRec(sess, inputName, dict, "HELLO WORLD", 48, 300);
        // ── 测试 2: 48px 高文字条 (你好世界) ──
        TestRec(sess, inputName, dict, "你好世界", 48, 300);
        // ── 测试 3: 纯白图 (应解码为空) ──
        TestBlank(sess, inputName, dict);
    }

    static void TestRec(InferenceSession sess, string inputName, string[] dict, string text, int fontSize, int maxW)
    {
        using var bmp = RenderText(text, fontSize, maxW);
        var bgra = BitmapToBgra(bmp);
        Console.WriteLine($"── '{text}' ({bmp.Width}x{bmp.Height}) ──");

        // rec 预处理: resize H=48 保持纵横比, pad 到 48xW
        int recH = 48, recMaxW = 640;
        float aspect = (float)bmp.Width / bmp.Height;
        int recW = Math.Min((int)Math.Ceiling(recH * aspect), recMaxW);
        recW = Math.Max(4, recW);
        Console.WriteLine($"crop→rec: {bmp.Width}x{bmp.Height} → {recW}x{recH}");

        var input = new DenseTensor<float>(new[] { 1, 3, recH, recW });
        float sx = (float)bmp.Width / recW;
        float sy = (float)bmp.Height / recH;
        for (int dy = 0; dy < recH; dy++)
        {
            int y0 = Math.Clamp((int)(dy * sy), 0, bmp.Height - 1);
            int y1 = Math.Clamp(y0 + 1, 0, bmp.Height - 1);
            float wy = dy * sy - y0;
            for (int dx = 0; dx < recW; dx++)
            {
                int x0 = Math.Clamp((int)(dx * sx), 0, bmp.Width - 1);
                int x1 = Math.Clamp(x0 + 1, 0, bmp.Width - 1);
                float wx = dx * sx - x0;
                // BGR 通道 (BGRA: c=0,1,2)
                for (int c = 0; c < 3; c++)
                {
                    float v00 = bgra[(y0 * bmp.Width + x0) * 4 + c] / 255f;
                    float v01 = bgra[(y0 * bmp.Width + x1) * 4 + c] / 255f;
                    float v10 = bgra[(y1 * bmp.Width + x0) * 4 + c] / 255f;
                    float v11 = bgra[(y1 * bmp.Width + x1) * 4 + c] / 255f;
                    float val = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                    input[0, c, dy, dx] = (val - 0.5f) / 0.5f;
                }
            }
        }

        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var output = results.First().AsTensor<float>();
        int timeSteps = output.Dimensions[1];
        int numClasses = output.Dimensions[2];
        Console.WriteLine($"输出: {timeSteps}x{numClasses}");

        // CTC greedy decode
        var decoded = new List<int>();
        int lastChar = -1;
        float maxConf = 0;
        for (int t = 0; t < timeSteps; t++)
        {
            int maxIdx = 0; float maxVal = float.MinValue;
            for (int c2 = 0; c2 < numClasses; c2++)
            {
                float v = output[[0, t, c2]];
                if (v > maxVal) { maxVal = v; maxIdx = c2; }
            }
            maxConf = Math.Max(maxConf, maxVal);
            if (maxIdx != lastChar && maxIdx > 0)
                decoded.Add(maxIdx);
            lastChar = maxIdx;
        }
        var text2 = string.Concat(decoded.Where(d => d - 1 < dict.Length).Select(d => dict[d - 1]));
        Console.WriteLine($"解码: '{text2}' (max_conf={maxConf:F3}, steps={timeSteps})");

        // 输出前 5 步的 argmax 分布
        var dist = new List<string>();
        for (int t = 0; t < Math.Min(10, timeSteps); t++)
        {
            int maxIdx = 0; float maxVal = float.MinValue;
            for (int c2 = 0; c2 < numClasses; c2++)
            {
                float v = output[[0, t, c2]];
                if (v > maxVal) { maxVal = v; maxIdx = c2; }
            }
            dist.Add($"{t}:idx={maxIdx}({(maxIdx > 0 && maxIdx - 1 < dict.Length ? dict[maxIdx - 1] : "blank")})");
        }
        Console.WriteLine($"前10步: {string.Join(" ", dist)}\n");
    }

    static void TestBlank(InferenceSession sess, string inputName, string[] dict)
    {
        int recH = 48, recW = 64;
        var input = new DenseTensor<float>(new[] { 1, 3, recH, recW });
        // 全白 (1.0-0.5)/0.5 = 1.0
        for (int i = 0; i < input.Length; i++) input.SetValue(i, 1.0f);
        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var output = results.First().AsTensor<float>();
        int timeSteps = output.Dimensions[1];
        int numClasses = output.Dimensions[2];
        var decoded = new List<int>();
        int lastChar = -1;
        for (int t = 0; t < timeSteps; t++)
        {
            int maxIdx = 0; float maxVal = float.MinValue;
            for (int c2 = 0; c2 < numClasses; c2++)
            {
                float v = output[[0, t, c2]];
                if (v > maxVal) { maxVal = v; maxIdx = c2; }
            }
            if (maxIdx != lastChar && maxIdx > 0) decoded.Add(maxIdx);
            lastChar = maxIdx;
        }
        var text2 = string.Concat(decoded.Where(d => d - 1 < dict.Length).Select(d => dict[d - 1]));
        Console.WriteLine($"── 纯白图 ──");
        Console.WriteLine($"解码: '{text2}' (期望为空)\n");
    }

    static Bitmap RenderText(string text, int fontSize, int maxW)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        var sz = g.MeasureString(text, font);
        int pad = 10;
        int w = Math.Min(maxW, (int)Math.Ceiling(sz.Width) + pad * 2);
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
