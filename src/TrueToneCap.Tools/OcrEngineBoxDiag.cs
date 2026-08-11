// OcrEngineBoxDiag.cs — 反射引擎内部，打印 RunDetection 真实框结果
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-ebox <modelDir>
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using TrueToneCap.Core.Services;

public static class OcrEngineBoxDiag
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        Console.WriteLine($"═══ 引擎内部框诊断: {modelDir} ═══\n");

        using var bmp = RenderText("HELLO WORLD", 48);
        var bgra = BitmapToBgra(bmp);
        int w = bmp.Width, h = bmp.Height;
        Console.WriteLine($"图: {w}x{h}\n");

        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
        Console.WriteLine($"引擎可用: {engine.Info.IsAvailable}");

        // 反射调用私有 RunDetection
        var method = typeof(OnnxOcrEngine).GetMethod("RunDetection", BindingFlags.NonPublic | BindingFlags.Instance);
        Console.WriteLine($"RunDetection 方法: {(method is null ? "MISSING" : "找到")}");
        if (method is null) return;

        var boxes = (System.Collections.IEnumerable)method.Invoke(engine, new object[] { bgra, w, h })!;
        int count = 0;
        foreach (var box in boxes)
        {
            count++;
            var props = box.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var fields = box.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var vals = new List<string>();
            foreach (var f in fields)
                vals.Add($"{f.Name}={f.GetValue(box):F1}");
            foreach (var p in props)
                if (p.GetIndexParameters().Length == 0)
                    vals.Add($"{p.Name}={p.GetValue(box):F1}");
            Console.WriteLine($"框{count}: {string.Join(" ", vals)}");
        }
        Console.WriteLine($"总框数: {count}\n");

        // 再跑一次完整识别对比
        var result = engine.RecognizeAsync(bgra, w, h, "zh-en").GetAwaiter().GetResult();
        Console.WriteLine($"完整识别: '{(string.IsNullOrEmpty(result.Text) ? "(空)" : result.Text)}' error={result.Error ?? "null"}");
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
