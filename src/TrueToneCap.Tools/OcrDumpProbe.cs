// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// OcrDumpProbe.cs — OCR 分行/词框/字号探针（2026-11-11 排障用，--ocr-dump 分派）
// 目的：复现预览端渲染输入 —— 引擎到底返回了哪些"行"、每行词框几何，
//       连同 OcrPreviewWindow.LineRect 与 FitFontSize 将拿到的参数一起打印，
//       供「OCR 预览字号偏小/挤压」问题用真实数字定位。
// 用法: dotnet run --project src/TrueToneCap.Tools -- --ocr-dump <png> <x> <y> <w> <h> [modelDir] [gpu]

using System.Drawing;
using System.Drawing.Imaging;
using TrueToneCap.Core.Services;

public static class OcrDumpProbe
{
    public static void Run(string[] args)
    {
        if (args.Length < 6)
        {
            Console.WriteLine("用法: --ocr-dump <png> <x> <y> <w> <h> [modelDir] [gpu]");
            return;
        }
        string png = args[1];
        int rx = int.Parse(args[2]), ry = int.Parse(args[3]);
        int rw = int.Parse(args[4]), rh = int.Parse(args[5]);
        string modelDir = args.Length >= 7 && args[6].Length > 0
            ? args[6]
            : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        bool gpu = args.Length >= 8 && args[7].Equals("gpu", StringComparison.OrdinalIgnoreCase);

        using var src = new Bitmap(png);
        using var region = src.Clone(new Rectangle(rx, ry, rw, rh), src.PixelFormat);
        var bgra = BitmapToBgra(region, out int w, out int h);
        Console.WriteLine($"区域: ({rx},{ry}) {rw}x{rh} → 引擎输入 {w}x{h}");

        var provider = gpu ? OnnxExecutionProvider.DirectML : OnnxExecutionProvider.Cpu;
        using var engine = new OnnxOcrEngine(provider, modelDir);
        Console.WriteLine($"引擎: {provider} available={engine.Info.IsAvailable}");

        var result = engine.RecognizeAsync(bgra, w, h, "en-US").GetAwaiter().GetResult();
        Console.WriteLine($"全文: {(result.Text ?? "").Replace("\n", " ⏎ ")}");
        Console.WriteLine($"行数: {result.Lines.Count}\n");

        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            Console.WriteLine($"── 行[{i}] Text=\"{line.Text}\"");
            Console.WriteLine($"    Line.XYWH=({line.X},{line.Y},{line.Width},{line.Height})");
            foreach (var wd in line.Words)
                Console.WriteLine($"    word \"{wd.Text}\" ({wd.X},{wd.Y},{wd.Width},{wd.Height})");

            // 复刻 OcrPreviewWindow.LineRect 策略1（词合并 bbox + 4px pad）
            if (line.Words is { Count: > 0 })
            {
                int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
                foreach (var wd in line.Words)
                {
                    x1 = Math.Min(x1, wd.X); y1 = Math.Min(y1, wd.Y);
                    x2 = Math.Max(x2, wd.X + wd.Width); y2 = Math.Max(y2, wd.Y + wd.Height);
                }
                var rect = (X: Math.Max(0, x1 - 4), Y: Math.Max(0, y1 - 4), W: x2 - x1 + 8, H: y2 - y1 + 8);
                Console.WriteLine($"    LineRect(词合并+pad)=({rect.X},{rect.Y}) {rect.W}x{rect.H}");
                float startPx = rect.H * 0.65f;
                Console.WriteLine($"    FitFontSize 参数: maxW={rect.W - 8} maxH={rect.H - 8} startPx={startPx:0.#} " +
                                  $"floor=现口径 max(12,{startPx:0.#})={Math.Max(12, startPx):0.#} " +
                                  $"拟口径 (H-8)×1.25={(rect.H - 8) * 1.25f:0.#}");
            }
        }
        Console.WriteLine("done");
    }

    private static byte[] BitmapToBgra(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);
        return buf;
    }
}
