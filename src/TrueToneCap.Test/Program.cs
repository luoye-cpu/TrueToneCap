using TrueToneCap.Core.Services;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  TrueToneCap ONNX OCR 综合分析");
Console.WriteLine("══════════════════════════════════════\n");

// ── 1. 模型基本信息 ──
Console.WriteLine("── 1. 模型文件 ──");
string modelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TrueToneCap", "onnx_models");
foreach (var f in Directory.GetFiles(modelDir, "*.onnx"))
{
    var fi = new FileInfo(f);
    Console.WriteLine($"  {fi.Name}: {fi.Length / 1024.0 / 1024.0:F1} MB");
}
Console.WriteLine();

// ── Helper: create test image ──
byte[] CreateTestImage(int w, int h, string text, float fontSize = 18)
{
    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.White);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Regular);
    using var brush = new SolidBrush(Color.Black);
    g.DrawString(text, font, brush, 10, 10);

    var rect = new Rectangle(0, 0, w, h);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bytes = new byte[data.Stride * h];
    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
    bmp.UnlockBits(data);
    return bytes;
}

// ── 测试用例 ──
var testCases = new (string name, string text, int w, int h, float fontSize)[]
{
    ("纯英文", "TrueToneCap Screenshot Tool 2026", 800, 60, 16),
    ("纯中文", "真色截图工具版本二〇二六", 500, 60, 18),
    ("中英混合", "TrueToneCap 真色截图 2026年", 600, 60, 18),
    ("数字", "12345.67890 ABC-001", 450, 50, 16),
    ("小文字", "small text hello world 小字测试", 500, 40, 12),
};

var images = testCases.Select(tc => (
    tc.name,
    tc.text,
    tc.w,
    tc.h,
    data: CreateTestImage(tc.w, tc.h, tc.text, tc.fontSize)
)).ToArray();

// ── 2 & 3. 推理基准测试 ──
void RunFullBench(string epLabel, OnnxExecutionProvider provider)
{
    Console.WriteLine($"── {epLabel} ──");
    Console.Write($"  初始化引擎... ");
    using var engine = new OnnxOcrEngine(provider);
    bool avail = engine.Info.IsAvailable;
    Console.WriteLine(avail ? $"OK ({engine.Info.Name})" : "FAIL");
    if (!avail) { Console.WriteLine(); return; }

    int warmup = 2, runs = 5;

    foreach (var (name, expected, w, h, data) in images)
    {
        // Warmup
        for (int i = 0; i < warmup; i++)
            engine.RecognizeAsync(data, w, h).Wait();

        // Timed
        var sw = Stopwatch.StartNew();
        OcrResult? last = null;
        for (int i = 0; i < runs; i++)
            last = engine.RecognizeAsync(data, w, h).Result;
        sw.Stop();
        double avgMs = sw.ElapsedMilliseconds / (double)runs;

        string match = last?.Text?.Trim() == expected.Trim() ? "✓" : "✗";
        Console.WriteLine($"  [{match}] {name,-8} | avg {avgMs,6:F0}ms | \"{last?.Text?.Replace("\n"," ")}\"");
    }
    Console.WriteLine();
}

var providers = new[] {
    ("2. CPU 推理", OnnxExecutionProvider.Cpu),
    ("3. GPU (DirectML) 推理", OnnxExecutionProvider.DirectML),
};

foreach (var (label, ep) in providers)
    RunFullBench(label, ep);

// ── 4. 首次加载耗时 ──
Console.WriteLine("── 4. 首次加载延迟 ──");
foreach (var ep in new[] { OnnxExecutionProvider.Cpu, OnnxExecutionProvider.DirectML })
{
    string label = ep == OnnxExecutionProvider.Cpu ? "CPU" : "DirectML";
    Console.Write($"  {label}: init... ");
    var sw2 = Stopwatch.StartNew();
    using var eng = new OnnxOcrEngine(ep);
    sw2.Stop();
    Console.WriteLine($"{sw2.ElapsedMilliseconds}ms | Available={eng.Info.IsAvailable}");
}

Console.WriteLine("\n══════════════════════════════════════");
Console.WriteLine("  测试完成");
Console.WriteLine("══════════════════════════════════════");
