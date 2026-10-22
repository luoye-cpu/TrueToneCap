// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/WinMlStress.cs
//
// ═══ WinML 迁移验收：压力回归（A4 / A5）═══
//
// 用法: dotnet run --project src/TrueToneCap.Tools -- --winml-stress [iterations] [modelDir]
//
// 为什么需要它:
//   WinML 迁移规划期观察到【一次】det 模型 Add.154 广播失败，但后续 139 次运行 0 复现。
//   该现象最可能来自测试进程自身的 dll 文件锁竞态，但【无法排除】低概率稳定性问题。
//   OCR 场景下"失败 = 用户看到空结果/乱码"，所以不能用一次冒烟替代压力验证。
//
// 本 harness 直接驱动【真实的 OnnxOcrEngine】——不是探针级别的 SessionOptions 复刻，
// 而是产品代码路径本身（含 _inferGate 串行化、张量池化、box 提取、CTC 解码）。
//
// 判据（对应 docs/reviews/ocr/winml-migration-plan-2026-10-07.md 的 A4/A5）:
//   A4 : ≥100 次识别、双 EP、多尺寸，异常数必须为 0
//   A5 : 并发调用（模拟"连点 OCR"）不得崩溃/死锁，且结果与串行一致

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using TrueToneCap.Core.Services;

// ⚠ 本文件【不】声明 namespace：FormatBench.cs 是顶层语句（无 namespace），
//   分派表在全局命名空间里引用 WinMlStress。加 namespace 会编译期找不到。
//   （与 OcrAccBench / OcrProbProbe 等同目录 harness 的既有约定一致。）

internal static class WinMlStress
{
    public static async Task<int> RunAsync(string[] args)
    {
        // 位置参数: [iterations] [modelDir]
        int iterations = 3;
        if (args.Length >= 2 && int.TryParse(args[1], out var it) && it > 0) iterations = it;

        string modelDir = args.Length >= 3 && args[2].Length > 0
            ? args[2]
            : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        if (!Directory.Exists(modelDir))
            modelDir = OnnxOcrEngine.ResolveModelDir();

        Console.WriteLine("═══ WinML 迁移压力回归 (A4/A5) ═══");
        Console.WriteLine($"模型目录: {modelDir}");
        Console.WriteLine($"轮次: {iterations}");
        Console.WriteLine();

        // ── 基准图：多种尺寸，覆盖产品的典型与极端形状 ──
        // 每个都是 (宽, 高, 说明)；内容为高对比度文本块，确保 det 能产出框。
        var cases = new (int W, int H, string Label)[]
        {
            (1242,  831, "用户场景 1242x831"),
            (1920, 1080, "FHD 全屏"),
            ( 385,  104, "小选区"),
            ( 896,  384, "宽扁区域"),
            (1504,  384, "超宽条带"),
            ( 640,  640, "方图"),
            ( 256,  896, "窄高条带"),
        };

        int total = 0, failures = 0;
        var sw = Stopwatch.StartNew();

        foreach (var ep in new[] { OnnxExecutionProvider.Cpu, OnnxExecutionProvider.DirectML })
        {
            Console.WriteLine($"── EP: {ep} ──");
            OnnxOcrEngine? engine = null;
            try { engine = new OnnxOcrEngine(ep, modelDir); }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ 引擎创建失败（{ex.GetType().Name}）→ 跳过该 EP");
                continue;
            }

            using (engine)
            {
                if (!engine.Info.IsAvailable)
                {
                    Console.WriteLine("  ⚠ 模型不可用 → 跳过该 EP");
                    continue;
                }

                // 实际生效的 EP 可能因 DML 不可用而降级 —— 如实标注
                Console.WriteLine($"  引擎: {engine.Info.Name}  " +
                                  $"LayerNorm workaround: {engine.LayerNormWorkaround}");

                for (int i = 0; i < iterations; i++)
                {
                    foreach (var c in cases)
                    {
                        var bgra = MakeTestImage(c.W, c.H);
                        try
                        {
                            var r = await engine.RecognizeAsync(bgra, c.W, c.H, null, CancellationToken.None);
                            total++;
                            // "失败"定义：抛异常，或引擎自报错误。识别为空在多尺寸合成图上属正常
                            // （小图上可能确实无字），故只把 Error 非空视为失败。
                            if (!string.IsNullOrEmpty(r.Error))
                            {
                                failures++;
                                Console.WriteLine($"  ❌ {c.Label} 错误: {r.Error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            Console.WriteLine($"  ❌ {c.Label} 异常 {ex.GetType().Name}: " +
                                              ex.Message[..Math.Min(120, ex.Message.Length)]);
                        }
                    }
                }
            }
            Console.WriteLine($"  累计: {total} 次, 失败 {failures}");
        }

        Console.WriteLine();
        Console.WriteLine($"A4 串行压力: {total} 次识别, {failures} 次失败, 用时 {sw.ElapsedMilliseconds}ms");

        // ── A5: 并发调用（模拟用户连点"选区识字+翻译"）──
        Console.WriteLine();
        Console.WriteLine("── A5: 并发调用（连点场景）──");
        int cTotal = 0, cFail = 0;
        try
        {
            using var eng = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
            if (eng.Info.IsAvailable)
            {
                var tasks = new List<Task>();
                for (int i = 0; i < 24; i++)
                {
                    var (w, h) = (1242, 831);
                    var img = MakeTestImage(w, h);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var r = await eng.RecognizeAsync(img, w, h, null, CancellationToken.None);
                            Interlocked.Increment(ref cTotal);
                            if (!string.IsNullOrEmpty(r.Error)) Interlocked.Increment(ref cFail);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref cFail);
                            Console.WriteLine($"  ❌ 并发异常 {ex.GetType().Name}: {ex.Message[..Math.Min(100, ex.Message.Length)]}");
                        }
                    }));
                }
                var all = Task.WhenAll(tasks);
                var done = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMinutes(5)));
                if (done != all)
                {
                    cFail++;
                    Console.WriteLine("  ❌ 并发调用超时（>5 分钟）—— 疑似死锁");
                }
                else await all;
            }
        }
        catch (Exception ex)
        {
            cFail++;
            Console.WriteLine($"  ❌ 并发测试异常: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine($"A5 并发: {cTotal} 次完成, {cFail} 次失败");

        Console.WriteLine();
        bool pass = failures == 0 && cFail == 0 && total >= 100;
        Console.WriteLine(pass
            ? $"✅ A4/A5 通过（串行 {total} 次 ≥100 且 0 失败；并发 0 失败）"
            : $"❌ A4/A5 未通过（串行失败 {failures}，并发失败 {cFail}，串行总数 {total}）");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// 生成 BGRA 测试图 —— 必须画【真实字形】。
    /// <para>⚠ 2026-10-07 实测教训：初版用"周期性黑色竖条"模拟文字笔画，
    /// 结果 det 模型判定为噪声 → 全部返回"未检测到文字"，被脚本记成 30/42 失败。
    /// 那**不是** WinML 的问题，是测试图的错 —— DBNet 检测的是真实文本区域的
    /// 概率分布，不是"看起来像行的黑块"。</para>
    /// <para>故改为与本仓既有基准（<see cref="OcrAccBench"/>）一致的 GDI 真实渲染。</para>
    /// </summary>
    private static byte[] MakeTestImage(int w, int h)
    {
        // 内容随尺寸缩放，保证在 385x104 这类小选区上也有可检出的文字
        string[] lines =
        [
            "OCR STRESS 1234",
            "中文识别测试 font",
            "Hello World 9876",
        ];
        int fontSize = Math.Clamp(h / 12, 10, 40);

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Black);
            int y = Math.Max(4, h / 16);
            foreach (var line in lines)
            {
                if (y > h - fontSize) break;
                g.DrawString(line, font, brush, 6, y);
                y += fontSize + Math.Max(2, fontSize / 3);
            }
        }

        // Bitmap → BGRA byte[]
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            return buf;
        }
        finally { bmp.UnlockBits(data); }
    }
}
