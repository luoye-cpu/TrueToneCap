// OcrBoxDiag.cs — 框级诊断：复刻完整 RunDetection 流程，打印框坐标 + crop 统计
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-box <modelDir>
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class OcrBoxDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        Console.WriteLine($"═══ 框级诊断: {Path.GetFileName(detPath)} ═══\n");

        // 小图 HELLO WORLD (OcrBench 同款)
        using var bmp = RenderText("HELLO WORLD", 48);
        var bgra = BitmapToBgra(bmp);
        int w = bmp.Width, h = bmp.Height;
        Console.WriteLine($"图: {w}x{h}\n");

        // 复刻 RunDetection 预处理
        int limitSide = 736;
        float ratio;
        if (Math.Min(w, h) < limitSide)
        {
            ratio = (float)limitSide / Math.Min(w, h);
            ratio = Math.Min(ratio, 6.0f);
        }
        else ratio = 1.0f;
        int rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
        float ratioW = (float)w / rw;
        float ratioH = (float)h / rh;
        Console.WriteLine($"resize: {rw}x{rh} ratioW={ratioW:F4} ratioH={ratioH:F4}");

        using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var sess = new InferenceSession(detPath, opts);
        var inputName = sess.InputMetadata.Keys.First();
        var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });

        // 双线性 + 归一化 (同引擎)
        for (int y = 0; y < rh; y++)
        {
            float srcYf = y * ratioH;
            int y0 = Math.Clamp((int)srcYf, 0, h - 1);
            int y1 = Math.Clamp(y0 + 1, 0, h - 1);
            float wy = srcYf - y0;
            for (int x = 0; x < rw; x++)
            {
                float srcXf = x * ratioW;
                int x0 = Math.Clamp((int)srcXf, 0, w - 1);
                int x1 = Math.Clamp(x0 + 1, 0, w - 1);
                float wx = srcXf - x0;
                for (int c = 0; c < 3; c++)
                {
                    float v00 = bgra[(y0 * w + x0) * 4 + c] / 255f;
                    float v01 = bgra[(y0 * w + x1) * 4 + c] / 255f;
                    float v10 = bgra[(y1 * w + x0) * 4 + c] / 255f;
                    float v11 = bgra[(y1 * w + x1) * 4 + c] / 255f;
                    float val = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                    input[0, c, y, x] = (val - 0.5f) / 0.5f;
                }
            }
        }

        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var output = results.First().AsTensor<float>();
        int oh = output.Dimensions[2], ow = output.Dimensions[3];
        Console.WriteLine($"det 输出: {oh}x{ow}");

        // 二值化 (threshold=0.3) + probMap
        float threshold = 0.3f;
        var probMap = new float[oh * ow];
        var bitmap = new byte[oh * ow];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float v = output[[0, 0, y, x]];
                probMap[y * ow + x] = v;
                bitmap[y * ow + x] = v > threshold ? (byte)255 : (byte)0;
            }

        // BFS 连通域 (同引擎 ExtractBoxes)
        var visited = new bool[ow * oh];
        var boxes = new List<(float X1, float Y1, float X2, float Y2, int HighCount, int TotalCount)>();
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int idx = y * ow + x;
                if (bitmap[idx] == 0 || visited[idx]) continue;
                int minX = x, maxX = x, minY = y, maxY = y;
                int highCount = 0, totalCount = 0;
                var queue = new Queue<(int, int)>();
                queue.Enqueue((x, y));
                visited[idx] = true;
                while (queue.Count > 0 && queue.Count < ow * oh)
                {
                    var (cx, cy) = queue.Dequeue();
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    totalCount++;
                    if (probMap[cy * ow + cx] > 0.7f) highCount++;
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx >= 0 && nx < ow && ny >= 0 && ny < oh)
                        {
                            int nidx = ny * ow + nx;
                            if (bitmap[nidx] > 0 && !visited[nidx]) { visited[nidx] = true; queue.Enqueue((nx, ny)); }
                        }
                    }
                }
                int boxW = maxX - minX, boxH = maxY - minY;
                if (boxW > 3 && boxH > 3 && boxW * boxH > 20)
                {
                    float aspectRatio = (float)boxW / boxH;
                    if (aspectRatio < 50f && aspectRatio > 0.02f)
                    {
                        boxes.Add((minX * ratioW, minY * ratioH, (maxX + 1) * ratioW, (maxY + 1) * ratioH, highCount, totalCount));
                    }
                }
            }
        }

        Console.WriteLine($"原始连通域(过滤前): 统计完毕, 输出框数: {boxes.Count}\n");
        foreach (var b in boxes)
        {
            float ratio2 = b.TotalCount > 0 ? (float)b.HighCount / b.TotalCount : 0;
            Console.WriteLine($"框: ({b.X1:F0},{b.Y1:F0})-({b.X2:F0},{b.Y2:F0}) {b.X2 - b.X1:F0}x{b.Y2 - b.Y1:F0} 高激活占比={ratio2:P1}");

            // crop 内容分析: 黑色像素占比 (文字是黑的)
            int cx1 = Math.Max(0, (int)b.X1), cy1 = Math.Max(0, (int)b.Y1);
            int cx2 = Math.Min(w - 1, (int)b.X2), cy2 = Math.Min(h - 1, (int)b.Y2);
            int darkCount = 0, sampleCount = 0;
            for (int yy = cy1; yy <= cy2; yy += 2)
                for (int xx = cx1; xx <= cx2; xx += 2)
                {
                    int off = (yy * w + xx) * 4;
                    if (bgra[off] < 100 && bgra[off + 1] < 100 && bgra[off + 2] < 100) darkCount++;
                    sampleCount++;
                }
            Console.WriteLine($"   crop 黑色像素占比: {(sampleCount > 0 ? (double)darkCount / sampleCount : 0):P1}");
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
