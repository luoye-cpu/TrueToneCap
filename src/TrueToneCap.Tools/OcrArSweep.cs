// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// OcrArSweep.cs — 宽高比/宽度扫描诊断
//
// 目的：定位「长行文本检测完全失效（0 框，识别结果为空）」的阈值，
//       并验证「分块检测（chunked detection）」能否修复。
//
// 背景：--ocr-bench-real 的 21 个 <85% 失败样本中，≥7 个识别结果完全为空
//      （0/195、0/133、0/10），怀疑是 det 输入被放大到极端宽度后 DBNet 失效。
//
// ⚠️ DetLimitSide 必须与 OnnxOcrConfig.DetLimitSide 默认值保持一致（当前 960）。
//    注意：OcrModelDiag.cs / OcrEngineDiag.cs / OcrBoxDiag.cs 里硬编码的是 736，
//    与生产默认值 960 不一致 —— 已作为发现项记录，本诊断用生产值。
//
// 用法: --ocr-ar-sweep [modelDir] [fontSize]
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TrueToneCap.Core.Services;

public static class OcrArSweep
{
    // 与 OnnxOcrConfig 默认值对齐（OnnxOcrEngine.cs:26 / :45）
    const int LimitSide = 960;
    // CPU 上限，见 OnnxOcrEngine.RunDetection（GPU 为 8.0）
    const float MaxRatio = 6.0f;
    // 分块后每块的【det 输入宽度】目标（DBNet 训练尺度量级）
    const int TargetResizedW = 1024;
    // 分块重叠，避免把字符从中间切开
    const int OverlapPx = 48;

    public static void Run(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        int fontSize = args.Length >= 3 && int.TryParse(args[2], out var fs) ? fs : 18;

        Console.WriteLine("═══ 宽高比扫描（整行 vs 分块）═══");
        Console.WriteLine($"模型目录: {modelDir}");
        Console.WriteLine($"字号: {fontSize}px   DetLimitSide={LimitSide}   MaxRatio={MaxRatio}   TargetResizedW={TargetResizedW}\n");

        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
        Console.WriteLine($"引擎可用: {engine.Info.IsAvailable}\n");

        int[] counts = [10, 20, 40, 80, 130, 195];
        Console.WriteLine($"{"字符",5} {"源图",11} {"源AR",7} {"det输入",13} {"det输入AR",9} {"期望",5} {"整行",6} {"分块",6}  判定");
        Console.WriteLine(new string('-', 92));

        foreach (int n in counts)
        {
            string text = MakeText(n);
            int expected = text.Replace(" ", "").Length;

            using var bmp = RenderLine(text, fontSize);
            var bgra = BitmapToBgra(bmp);

            ResizeOf(bmp.Width, bmp.Height, out int rw, out int rh);
            double srcAr = (double)bmp.Width / bmp.Height;
            double detAr = (double)rw / rh;

            int full = Rec(engine, bgra, bmp.Width, bmp.Height);
            int chunked = RecChunked(engine, bgra, bmp.Width, bmp.Height, rw);

            string verdict = (full, chunked) switch
            {
                (0, > 0) => "🔴 整行漏检，分块可救",
                (0, 0) => "⚠️ 两者都失败",
                (> 0, _) => "✅ 整行即可",
                _ => "?",
            };

            Console.WriteLine(
                $"{n,5} {$"{bmp.Width}x{bmp.Height}",11} {srcAr,7:F1} {$"{rw}x{rh}",13} {detAr,9:F1} {expected,5} {full,6} {chunked,6}  {verdict}");
        }

        Console.WriteLine("\n说明：");
        Console.WriteLine("  · 「期望/整行/分块」均为【去空格后字符数】，非严格准确率；");
        Console.WriteLine("  · 分块结果含重叠区，可能略微重复计数，仅用于判断「是否从 0 变为非 0」；");
        Console.WriteLine("  · 关键信号是「整行=0 而分块>0」，即检测阶段漏框而非识别错误。");
    }

    /// <summary>
    /// 阈值扫描：不同 DetMaxBoxAspect 下，各长度长行的端到端识别率。
    /// 用于确定「放宽 AR 上限」是否足以修复，以及放宽到多少不会引入噪声/质量下降。
    /// </summary>
    public static void RunAspectSweep(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        int fontSize = args.Length >= 3 && int.TryParse(args[2], out var fs) ? fs : 18;

        int[] counts = [40, 80, 130, 195, 300];
        // (宽高比上限, 长行拆分阈值)；拆分 0 = 禁用
        (float Cap, float Split)[] combos =
        [
            (50f, 0f),     // 旧行为（基线）
            (200f, 90f),   // 当前生产默认值
            (400f, 90f),
            (1000f, 90f),
        ];

        Console.WriteLine("═══ 长行修复阈值扫描（端到端）═══");
        Console.WriteLine($"字号: {fontSize}px\n");
        Console.WriteLine($"{"字符",6} {"期望",6} " +
            string.Join("", combos.Select(c => $"{"上限" + FmtCap(c.Cap) + "/拆" + FmtCap(c.Split),16}")));
        Console.WriteLine(new string('-', 20 + combos.Length * 16));

        // 预渲染所有长度（避免每个 cap 重复渲染）
        var samples = counts.Select(n =>
        {
            string t = MakeText(n);
            var bmp = RenderLine(t, fontSize);
            return (N: n, Expected: t.Replace(" ", "").Length, Bmp: bmp, Bgra: BitmapToBgra(bmp));
        }).ToList();

        var table = new Dictionary<int, List<string>>();
        foreach (int n in counts) table[n] = [];

        foreach (var (cap, split) in combos)
        {
            var cfg = OcrModelConfig.Default with
            {
                DetMaxBoxAspect = cap,
                DetSplitBoxAspect = split,
            };
            using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir, cfg);

            foreach (var s in samples)
            {
                var r = engine.RecognizeAsync(s.Bgra, s.Bmp.Width, s.Bmp.Height, "zh-en")
                             .GetAwaiter().GetResult();
                string text = string.IsNullOrEmpty(r.Error) ? (r.Text ?? "") : "";
                int chars = text.Replace(" ", "").Replace("\n", "").Length;
                int lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                float acc = s.Expected > 0 ? (float)chars / s.Expected : 0;
                table[s.N].Add($"{chars,4}/{lines,-3}{acc,6:P0}{(chars == 0 ? " ❌" : "  "),4}");
            }
        }

        foreach (int n in samples.Select(s => s.N))
        {
            int exp = samples.First(s => s.N == n).Expected;
            Console.WriteLine($"{n,6} {exp,6} " + string.Join("", table[n].Select(x => $"{x,16}")) + "   ← 字符数/行数/召回");
        }

        foreach (var s in samples) s.Bmp.Dispose();
        Console.WriteLine("\n判读：「❌」= 整行漏检。目标是所有长度都非零、召回尽量接近 100%、且行数不失控。");
    }

    /// <summary>
    /// 单用例 DirectML 探针：AccessViolationException 会杀死整个进程，无法在同进程内
    /// 循环多个用例，故一次只跑一个，由外部脚本逐档调用并收集退出码。
    /// 用法: --ocr-dml-one <字符数> <cap> <split> [fontSize] [modelDir]
    /// 输出: DML1 chars=<n>/<expected> lines=<k> det=<w>x<h>
    /// </summary>
    public static void RunDmlOne(string[] args)
    {
        int n = args.Length >= 2 && int.TryParse(args[1], out var v) ? v : 40;
        float cap = args.Length >= 3 && float.TryParse(args[2], out var c) ? c : 400f;
        float split = args.Length >= 4 && float.TryParse(args[3], out var s) ? s : 90f;
        int fontSize = args.Length >= 5 && int.TryParse(args[4], out var fs) ? fs : 18;
        string modelDir = args.Length >= 6 && args[5] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        string text = MakeText(n);
        int expected = text.Replace(" ", "").Length;
        using var bmp = RenderLine(text, fontSize);
        var bgra = BitmapToBgra(bmp);

        // 打印该用例在 DirectML 下的 det 输入尺寸（MaxRatio=8，与 CPU 的 6 不同）
        int limitSide = 960; float maxRatio = 8.0f;
        float ratio = Math.Min(limitSide / (float)Math.Min(bmp.Width, bmp.Height), maxRatio);
        int rw = Math.Max((int)Math.Round(bmp.Width * ratio / 32) * 32, 32);
        int rh = Math.Max((int)Math.Round(bmp.Height * ratio / 32) * 32, 32);
        Console.WriteLine($"DML1 src={bmp.Width}x{bmp.Height} det={rw}x{rh} ratio={ratio:F2} pixels={rw * rh}");

        var cfg = OcrModelConfig.Default with { DetMaxBoxAspect = cap, DetSplitBoxAspect = split };
        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir, cfg);
        if (!engine.Info.IsAvailable) { Console.WriteLine("DML1 引擎不可用"); return; }

        var r = engine.RecognizeAsync(bgra, bmp.Width, bmp.Height, "zh-en").GetAwaiter().GetResult();
        string got = string.IsNullOrEmpty(r.Error) ? (r.Text ?? "") : "";
        int chars = got.Replace(" ", "").Replace("\n", "").Length;
        int lines = got.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Console.WriteLine($"DML1 chars={chars}/{expected} lines={lines}");
    }

    /// <summary>
    /// 跨后端复验：生产默认走 DirectML（MultiOcrService 默认选中第一个可用引擎），
    /// 而本轮基准都跑在 CPU 上。两者 MaxRatio 不同（CPU 6x / GPU 8x），det 输入尺度
    /// 差异很大，必须确认长行修复在 DirectML 路径上同样成立、且无回归。
    /// 用法: --ocr-dml-check [modelDir] [fontSize]
    /// </summary>
    public static void RunProviderCheck(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        int fontSize = args.Length >= 3 && int.TryParse(args[2], out var fs) ? fs : 18;

        int[] counts = [40, 80, 130, 195, 300];
        (string Name, OnnxExecutionProvider P)[] provs =
        [
            ("CPU", OnnxExecutionProvider.Cpu),
            ("DirectML", OnnxExecutionProvider.DirectML),
        ];
        // 旧参数 = 修复前（上限 50 / 不拆分）；新参数 = 修复后生产默认（400 / 90）
        (float Cap, float Split, string Label)[] cfgs =
        [
            (50f, 0f, "旧"),
            (400f, 90f, "新"),
        ];

        Console.WriteLine("═══ 跨后端复验（长行修复）═══");
        Console.WriteLine($"字号: {fontSize}px   旧=上限50/不拆分   新=上限400/拆分90\n");

        var samples = counts.Select(n =>
        {
            string t = MakeText(n);
            var bmp = RenderLine(t, fontSize);
            return (N: n, Expected: t.Replace(" ", "").Length, Bmp: bmp, Bgra: BitmapToBgra(bmp));
        }).ToList();

        Console.WriteLine($"{"字符",6} {"期望",6} " + string.Join("", provs.SelectMany(p =>
            cfgs.Select(c => $"{p.Name + "-" + c.Label,14}"))));
        Console.WriteLine(new string('-', 14 + provs.Length * cfgs.Length * 14));

        var results = samples.ToDictionary(s => s.N, _ => new List<string>());

        foreach (var (pname, prov) in provs)
        {
            foreach (var (cap, split, label) in cfgs)
            {
                OnnxOcrEngine engine;
                try
                {
                    var cfg = OcrModelConfig.Default with
                    {
                        DetMaxBoxAspect = cap,
                        DetSplitBoxAspect = split,
                    };
                    engine = new OnnxOcrEngine(prov, modelDir, cfg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{pname}] 引擎创建失败: {ex.Message}");
                    foreach (var s in samples) results[s.N].Add($"{"n/a",14}");
                    continue;
                }

                using (engine)
                {
                    if (!engine.Info.IsAvailable)
                    {
                        Console.WriteLine($"[{pname}] 引擎不可用，跳过");
                        foreach (var s in samples) results[s.N].Add($"{"不可用",14}");
                        continue;
                    }

                    foreach (var s in samples)
                    {
                        string text;
                        try
                        {
                            var r = engine.RecognizeAsync(s.Bgra, s.Bmp.Width, s.Bmp.Height, "zh-en")
                                         .GetAwaiter().GetResult();
                            text = string.IsNullOrEmpty(r.Error) ? (r.Text ?? "") : "";
                        }
                        catch (Exception ex) { text = ""; Console.WriteLine($"    [异常] {ex.Message}"); }

                        int chars = text.Replace(" ", "").Replace("\n", "").Length;
                        int lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                        float acc = s.Expected > 0 ? (float)chars / s.Expected : 0;
                        results[s.N].Add($"{chars,4}/{lines,-3}{acc,6:P0}");
                    }
                }
            }
        }

        foreach (var s in samples)
            Console.WriteLine($"{s.N,6} {s.Expected,6} " + string.Join("", results[s.N]));

        foreach (var s in samples) s.Bmp.Dispose();
        Console.WriteLine("\n判读：DirectML-新 一列应与 CPU-新 接近且全部非零；DirectML-旧 应复现漏检。");
    }

    /// <summary>
    /// 噪声回归检查：放宽 DetMaxBoxAspect 的唯一风险是放过横线噪声（分隔线/边框），
    /// 这正是原硬编码 50 存在的原因。本用例用「纯横线图」和「文字+横线图」
    /// 验证各上限下是否会产出虚假行。
    /// </summary>
    public static void RunNoiseCheck(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        Console.WriteLine("═══ 噪声回归检查（横线分隔线）═══\n");

        string[] lines =
        [
            "Short line",
            "The quick brown fox jumps over the lazy dog while the cat sleeps",
            "Another medium length line of text here",
        ];

        // ⚠ 短分隔线（标题下的短线）AR 约 50~150，正好落在旧上限 50 与新上限 200 之间，
        //   是放宽上限后最主要的误检风险点，必须覆盖。
        using var rulesOnly = RenderScene(1200, 400, null, 18);
        using var withText = RenderScene(1200, 400, lines, 18);
        var (rb, wb) = (BitmapToBgra(rulesOnly), BitmapToBgra(withText));

        float[] caps = [50f, 200f, 400f, 1e9f];
        Console.WriteLine($"{"AR上限",10} {"纯横线图(应0行)",20} {"文字+横线图(应3行)",24}");
        Console.WriteLine(new string('-', 58));

        foreach (float cap in caps)
        {
            var cfg = OcrModelConfig.Default with { DetMaxBoxAspect = cap, DetSplitBoxAspect = 90f };
            using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir, cfg);

            var r1 = engine.RecognizeAsync(rb, rulesOnly.Width, rulesOnly.Height, "zh-en").GetAwaiter().GetResult();
            var r2 = engine.RecognizeAsync(wb, withText.Width, withText.Height, "zh-en").GetAwaiter().GetResult();

            int n1 = CountLines(r1), n2 = CountLines(r2);
            Console.WriteLine($"{FmtCap(cap),10} {n1 + " 行" + (n1 == 0 ? " ✅" : " ⚠️ 误检"),20} {n2 + " 行" + (n2 >= 3 ? " ✅" : " ⚠️ 漏行"),24}");

            if (n1 > 0)
            {
                var t = (r1.Text ?? "").Replace("\n", " ⏎ ");
                Console.WriteLine($"           误检内容: {(t.Length > 60 ? t[..60] : t)}");
            }
        }

        Console.WriteLine("\n判读：纯横线图应保持 0 行；文字图应 ≥3 行。二者同时满足的上限即为安全值。");
    }

    static int CountLines(OcrResult r)
    {
        if (!string.IsNullOrEmpty(r.Error)) return 0;
        return (r.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    static Bitmap RenderScene(int w, int h, string[]? lines, int fontSize)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);

            // 整幅宽分隔线（AR 极大）
            g.FillRectangle(brush, 0, 120, w, 2);
            g.FillRectangle(brush, 0, 180, w, 4);
            // 短分隔线（AR 约 50~150）—— 放宽上限后的主要误检风险
            g.FillRectangle(brush, 100, 240, 300, 3);   // AR=100
            g.FillRectangle(brush, 500, 300, 200, 2);   // AR=100
            g.FillRectangle(brush, 100, 340, 600, 5);   // AR=120
            // 短竖线（对照：AR<1，任何上限下都不应被误判为文字行）
            g.FillRectangle(brush, 900, 100, 3, 150);

            if (lines is not null)
            {
                using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                int ty = 20;
                foreach (string ln in lines)
                {
                    g.DrawString(ln, font, brush, 20, ty);
                    ty += fontSize + 12;
                }
            }
        }
        return bmp;
    }

    static string FmtCap(float c) => c > 1e8f ? "=∞" : ((int)c).ToString();

    /// <summary>
    /// 根因判别：宽度 vs 宽高比。
    /// 固定内容（80 字符）与固定画布宽度，只改画布高度。
    /// 由于 PP-OCRv6 是 limit_type=min（短边放大），在 h≤160 时
    /// ratio 恒为上限 6，故 det 输入【宽度恒定 4448】，只有 AR 变化
    /// （11.6 / 7.7 / 5.8 / 4.6）。若 h=64 失败而 h≥128 成功，
    /// 即可证明主因是「宽高比」而非「宽度」。
    /// </summary>
    public static void RunProbe(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        int fontSize = args.Length >= 3 && int.TryParse(args[2], out var fs) ? fs : 18;

        Console.WriteLine("═══ 根因判别：det 输入宽度 vs 宽高比 ═══");
        Console.WriteLine($"字号: {fontSize}px   内容固定 80 字符   仅改变画布高度\n");

        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
        Console.WriteLine($"引擎可用: {engine.Info.IsAvailable}\n");

        string text = MakeText(80);
        int expected = text.Replace(" ", "").Length;

        int[] heights = [64, 96, 128, 160, 192, 256, 384, 512, 742, 960];
        Console.WriteLine($"{"画布",11} {"det输入",13} {"det输入AR",9} {"期望",5} {"实得",6}  判定");
        Console.WriteLine(new string('-', 62));

        foreach (int h in heights)
        {
            using var bmp = RenderLine(text, fontSize, h);
            var bgra = BitmapToBgra(bmp);
            ResizeOf(bmp.Width, bmp.Height, out int rw, out int rh);
            double detAr = (double)rw / rh;
            int got = Rec(engine, bgra, bmp.Width, bmp.Height);

            string verdict = got > 0 ? "✅ 检出" : "🔴 漏检（0 框）";
            Console.WriteLine(
                $"{bmp.Width + "x" + bmp.Height,11} {$"{rw}x{rh}",13} {detAr,9:F1} {expected,5} {got,6}  {verdict}");
        }

        Console.WriteLine("\n判读：h=64/96/128/160 的 det 输入宽度都是 4448，只有 AR 不同。");
        Console.WriteLine("      → 若其中出现「AR 大的失败、AR 小的成功」，则主因是宽高比。");
    }

    // ── 复刻 OnnxOcrEngine.RunDetection 的缩放策略 ──
    static void ResizeOf(int w, int h, out int rw, out int rh)
    {
        float ratio;
        if (Math.Min(w, h) < LimitSide)
        {
            ratio = (float)LimitSide / Math.Min(w, h);
            ratio = Math.Min(ratio, MaxRatio);
        }
        else ratio = 1.0f;

        rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
    }

    static int Rec(OnnxOcrEngine engine, byte[] bgra, int w, int h)
    {
        try
        {
            var r = engine.RecognizeAsync(bgra, w, h, "zh-en").GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(r.Error)) return 0;
            return (r.Text ?? "").Replace(" ", "").Replace("\n", "").Length;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [异常] {ex.Message}");
            return -1;
        }
    }

    /// <summary>按 det 输入宽度目标反推源图分块宽度，逐块识别后累加。</summary>
    static int RecChunked(OnnxOcrEngine engine, byte[] bgra, int w, int h, int rw)
    {
        float ratio = (float)rw / w;                       // 缩放系数（近似，rw 已取整到 32）
        int chunkW = Math.Max(32, (int)(TargetResizedW / ratio));
        if (chunkW >= w) return Rec(engine, bgra, w, h);   // 不需要分块

        int step = Math.Max(1, chunkW - OverlapPx);
        int total = 0;
        for (int x = 0; x < w; x += step)
        {
            int cw = Math.Min(chunkW, w - x);
            if (cw <= 0) break;
            var crop = Crop(bgra, w, x, 0, cw, h);
            int n = Rec(engine, crop, cw, h);
            if (n > 0) total += n;
        }
        return total;
    }

    static byte[] Crop(byte[] src, int srcW, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src, ((y + row) * srcW + x) * 4, dst, row * w * 4, w * 4);
        return dst;
    }

    static string MakeText(int n)
    {
        const string unit = "The quick brown fox jumps over the lazy dog ";
        var sb = new System.Text.StringBuilder();
        while (sb.Length < n) sb.Append(unit);
        return sb.ToString(0, n).TrimEnd();
    }

    /// <param name="forceH">指定画布高度（根因判别用）；&lt;=0 时按字号自动。</param>
    static Bitmap RenderLine(string text, int fontSize, int forceH = 0)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        var size = g.MeasureString(text, font);
        int w = (int)Math.Ceiling(size.Width) + 60;
        int h = forceH > 0
            ? Math.Max(forceH, (int)Math.Ceiling(font.GetHeight()) + 40)
            : (int)Math.Ceiling(font.GetHeight()) + 40;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var rg = Graphics.FromImage(bmp))
        {
            rg.Clear(Color.White);
            rg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(Color.Black);
            rg.DrawString(text, font, brush, 30, 20);
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
