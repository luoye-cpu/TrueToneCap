// OcrPerfBench.cs — OCR 性能基准测试
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-perf <modelDir> [--size small|medium] [--iterations 5]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TrueToneCap.Core.Services;
using TrueToneCap.Core;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

/// <summary>OCR 性能基准。</summary>
public static class OcrPerfBench
{
    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        // 解析可选参数
        string size = "small"; // 默认小图
        int iterations = 5;    // 默认迭代次数
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--size" && i + 1 < args.Length)
                size = args[i + 1];
            else if (args[i] == "--iterations" && i + 1 < args.Length)
                int.TryParse(args[i + 1], out iterations);
        }

        Console.WriteLine($"════════ OCR 性能基准 ════════");
        Console.WriteLine($"模型目录: {modelDir}");
        Console.WriteLine($"模型存在: det={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx"))} " +
            $"rec={File.Exists(Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx"))} " +
            $"dict={File.Exists(Path.Combine(modelDir, "ppocrv6_dict.txt"))}");
        Console.WriteLine($"测试尺寸: {size}");
        Console.WriteLine($"迭代次数: {iterations}\n");

        var engines = new List<(string Name, OnnxOcrEngine Engine)>();
        try { engines.Add(("CPU", new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir))); }
        catch (Exception ex) { Console.WriteLine($"⚠ CPU 初始化失败: {ex.Message}"); }
        try { engines.Add(("GPU(DirectML)", new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir))); }
        catch (Exception ex) { Console.WriteLine($"⚠ GPU(DirectML) 初始化失败: {ex.Message}"); }
        // 注: OnnxExecutionProvider 目前仅支持 Cpu / DirectML。
        // 若需 CUDA，须先扩展枚举并在 OnnxOcrEngine 中实现对应 SessionOptions 分支
        // （并改用 Microsoft.ML.OnnxRuntime.Gpu 包），否则只会静默降级为 CPU。

        if (engines.Count == 0) { Console.WriteLine("❌ 无可用 OCR 引擎"); return; }
        foreach (var (name, eng) in engines)
            Console.WriteLine($"{name}: available={eng.Info.IsAvailable}");

        // 生成测试图像
        int imgW, imgH;
        byte[] bgra;
        if (size == "small")
        {
            // 小图 1920x1080
            imgW = 1920;
            imgH = 1080;
            bgra = GenerateTestImage(imgW, imgH, "文件 视图tf(result.Success)(return,result.Valuel)的过程。");
        }
        else
        {
            // 中等图 3840x2160
            imgW = 3840;
            imgH = 2160;
            bgra = GenerateTestImage(imgW, imgH, "保存到文件 复制到剪贴板 取消 应用 明天下午3点开会记得带上笔记本 好的，我马上发给你□ [图片1已读 文件 编辑 视...");
        }

        Console.WriteLine($"测试图像: {imgW}x{imgH}");

        foreach (var (name, eng) in engines.Where(e => e.Engine.Info.IsAvailable))
        {
            Console.WriteLine($"\n═ {name} ═");
            // 预热（唤醒初始化）
            var warmupSw = Stopwatch.StartNew();
            eng.RecognizeAsync(bgra, imgW, imgH, "zh-en").GetAwaiter().GetResult();
            warmupSw.Stop();
            Console.WriteLine($"  warmup: {warmupSw.ElapsedMilliseconds}ms");

            // 多次运行收集统计
            var times = new List<long>();
            int boxCount = 0;
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = eng.RecognizeAsync(bgra, imgW, imgH, "zh-en").GetAwaiter().GetResult();
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
                if (i == 0) boxCount = result?.Lines?.Count ?? 0; // 首次获取框数
                Console.WriteLine($"  第 {i + 1} 次: {sw.ElapsedMilliseconds}ms | 检测到 {boxCount} 行文字");
            }

            // 统计
            times.Sort();
            long min = times[0];
            long max = times[^1];
            long median = times[times.Count / 2];
            double average = times.Average();
            double stdDev = Math.Sqrt(times.Average(v => Math.Pow(v - average, 2)));
            double throughput = boxCount > 0 ? (boxCount * 1000.0 / average) : 0;

            Console.WriteLine($"  统计 (n={iterations}):");
            Console.WriteLine($"    最小: {min}ms | 最大: {max}ms | 中位数: {median}ms | 平均: {average:F1}ms");
            Console.WriteLine($"    标准差: {stdDev:F1}ms");
            Console.WriteLine($"    检测框数: {boxCount}");
            Console.WriteLine($"    吞吐量: {throughput:F1} boxes/s");
        }

        foreach (var (_, eng) in engines) eng.Dispose();
    }

    static byte[] GenerateTestImage(int width, int height, string text)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.White); // 白色背景

        using var font = new Font("Segoe UI", 48, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.Black);
        // 绘制多行文本，居中
        var lines = text.Split(new[] { '□', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        float y = (height - (lines.Length * font.GetHeight())) / 2;
        foreach (var line in lines)
        {
            var size = g.MeasureString(line.Trim(), font);
            float x = (width - size.Width) / 2;
            g.DrawString(line.Trim(), font, brush, x, y);
            y += font.GetHeight();
        }

        // 转换为 BGRA
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        int stride = data.Stride;
        var bytes = new byte[height * Math.Abs(stride)];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);

        // 确保是 BGRA 顺序（假设输入是 BGRA，但我们生成的是 ARGB）
        for (int i = 0; i < bytes.Length; i += 4)
        {
            byte a = bytes[i];       // A
            byte r = bytes[i + 1];   // R
            byte gVal = bytes[i + 2];   // G
            byte b = bytes[i + 3];   // B
            bytes[i] = b;            // B
            bytes[i + 1] = gVal;        // G
            bytes[i + 2] = r;        // R
            bytes[i + 3] = a;        // A
        }
        return bytes;
    }
}