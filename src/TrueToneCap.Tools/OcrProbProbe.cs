// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// OcrProbProbe.cs — 概率图 / 连通域过滤链探针
//
// 目的：回答「长行文本为什么一个框都出不来」。
//       ExtractBoxes 有三道过滤，必须知道失效发生在哪一道：
//         A. 概率图本身是死的（模型没激活）        → 模型/前处理问题
//         B. 二值化后没有连通域（无像素 > lowThresh） → 阈值问题
//         C. 几何过滤 (boxW>3 && boxH>3 && area>20, 0.02<AR<50)
//         D. 置信度过滤 (高激活像素占比 > 10%)      → 长行合并导致占比被稀释
//
// ⚠️ 前处理必须与生产一致：DetLimitSide=960（OcrBoxDiag.cs / OcrEngineDiag.cs /
//    OcrModelDiag.cs 里硬编码的 736 与生产默认值不一致，是已知漂移，勿照抄）。
//
// 用法: --ocr-prob <字符数> <画布高度> [字号] [modelDir]
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TrueToneCap.Core.Services;

public static class OcrProbProbe
{
    // 与 OnnxOcrConfig.Default 对齐
    const int LimitSide = 960;
    const float MaxRatio = 6.0f;
    const float DetThreshold = 0.3f;
    const float LowThresh = DetThreshold - 0.05f;   // 0.25，见 RunDetection
    const float BoxThresh = 0.7f;                   // 见 ExtractBoxes
    const float HighRatio = 0.10f;                  // 见 ExtractBoxes

    public static void Run(string[] args)
    {
        int nChars = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 80;
        int canvasH = args.Length >= 3 && int.TryParse(args[2], out var ch) ? ch : 64;
        int fontSize = args.Length >= 4 && int.TryParse(args[3], out var fs) ? fs : 18;
        string modelDir = args.Length >= 5 && args[4] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        if (!File.Exists(detPath)) { Console.WriteLine($"模型不存在: {detPath}"); return; }

        string text = MakeText(nChars);
        using var bmp = RenderLine(text, fontSize, canvasH);
        var bgra = BitmapToBgra(bmp);
        int w = bmp.Width, h = bmp.Height;

        // ── 复刻 RunDetection 缩放 ──
        float ratio;
        if (Math.Min(w, h) < LimitSide)
        {
            ratio = (float)LimitSide / Math.Min(w, h);
            ratio = Math.Min(ratio, MaxRatio);
        }
        else ratio = 1.0f;
        int rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
        float ratioW = (float)w / rw, ratioH = (float)h / rh;

        Console.WriteLine("═══ 概率图 / 过滤链探针 ═══");
        Console.WriteLine($"内容: {nChars} 字符 (去空格 {text.Replace(" ", "").Length})   字号 {fontSize}px");
        Console.WriteLine($"源图: {w}x{h}   det 输入: {rw}x{rh}   ratio={ratio:F3}  detAR={(double)rw / rh:F2}");

        using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var sess = new InferenceSession(detPath, opts);
        string inputName = sess.InputMetadata.Keys.First();
        var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });

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
        Console.WriteLine($"det 输出: {oh}x{ow}\n");

        // ── A. 概率图统计 ──
        var probMap = new float[oh * ow];
        var bitmap = new byte[oh * ow];
        float maxV = float.MinValue; double sumV = 0;
        int nLow = 0, nThr = 0, nHigh = 0;
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float v = output[[0, 0, y, x]];
                probMap[y * ow + x] = v;
                bitmap[y * ow + x] = v > LowThresh ? (byte)255 : (byte)0;
                if (v > maxV) maxV = v;
                sumV += v;
                if (v > LowThresh) nLow++;
                if (v > DetThreshold) nThr++;
                if (v > BoxThresh) nHigh++;
            }

        Console.WriteLine("── A. 概率图 ──");
        Console.WriteLine($"  max={maxV:F4}  mean={sumV / (oh * ow):F4}");
        Console.WriteLine($"  >{LowThresh:F2}: {nLow}  >{DetThreshold:F2}: {nThr}  >{BoxThresh:F2}: {nHigh}  (总像素 {oh * ow})");
        Console.WriteLine($"  判定: {(nLow == 0 ? "🔴 B — 概率图全死，二值化后无任何前景" : "✅ 有前景像素")}\n");

        if (nLow == 0) return;

        // ── B/C/D. 连通域 + 过滤链 ──
        var visited = new bool[ow * oh];
        int compTotal = 0, passGeo = 0, passConf = 0, passAll = 0;
        var rows = new List<string>();

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
                    if (probMap[cy * ow + cx] > BoxThresh) highCount++;
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx >= 0 && nx < ow && ny >= 0 && ny < oh)
                        {
                            int nidx = ny * ow + nx;
                            if (bitmap[nidx] > 0 && !visited[nidx]) { visited[nidx] = true; queue.Enqueue((nx, ny)); }
                        }
                    }
                }

                compTotal++;
                int boxW = maxX - minX, boxH = maxY - minY;
                float ar = boxH > 0 ? (float)boxW / boxH : 0;
                float hr = totalCount > 0 ? (float)highCount / totalCount : 0;

                string reason;
                if (boxW <= 3 || boxH <= 3 || boxW * boxH <= 20) reason = "C 几何(太小)";
                else if (ar >= 50f || ar <= 0.02f) { reason = "C 几何(AR越界)"; passGeo++; }
                else { passGeo++; if (hr < HighRatio) { reason = "D 置信度(高激活占比不足)"; } else { passConf++; passAll++; reason = "✅ 通过"; } }

                if (compTotal <= 12 || reason == "✅ 通过")
                    rows.Add($"  #{compTotal,-3} 框{(boxW + 1)}x{(boxH + 1),-5} AR={ar,7:F1}  像素={totalCount,-8} 高激活占比={hr,6:P1}  → {reason}");
            }
        }

        Console.WriteLine("── B/C/D. 连通域与过滤链 ──");
        foreach (var r in rows) Console.WriteLine(r);
        Console.WriteLine($"\n  连通域总数={compTotal}  过几何={passGeo}  过置信度={passConf}  最终出框={passAll}");

        string verdict = passAll == 0
            ? (compTotal == 0 ? "🔴 无连通域" : "🔴 全部被 C 或 D 过滤")
            : "✅ 有框输出";
        Console.WriteLine($"  结论: {verdict}");
    }

    /// <summary>
    /// 最小 DirectML 推理探针：绕开全部产品代码，只做
    /// 「建会话 → 喂一个小张量 → Run() → 读输出形状」。
    /// 用于区分 DirectML 崩溃是【环境/驱动】还是【产品代码】问题：
    /// 若这里也崩 → 与产品代码无关（环境或 ORT-DML 本身）；
    /// 若这里正常而引擎崩 → 产品侧用法问题。
    /// 用法: --ocr-dml-min [modelDir]
    /// </summary>
    public static void RunDmlMinimal(string[] args)
    {
        string modelDir = args.Length >= 2 && args[1] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        if (!File.Exists(detPath)) { Console.WriteLine($"模型不存在: {detPath}"); return; }

        int size = args.Length >= 3 && int.TryParse(args[2], out var sz) ? sz : 96;
        int sizeH = args.Length >= 4 && int.TryParse(args[3], out var sh) ? sh : size;
        Console.WriteLine("═══ 最小 DirectML 推理探针 ═══");
        Console.WriteLine($"模型: {Path.GetFileName(detPath)}   测试尺寸: {size}x{sizeH}");

        // [A] 先跑 CPU 基线，确认模型/ORT 本身没问题
        try
        {
            using var cpuOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
            using var cpuSess = new InferenceSession(detPath, cpuOpts);
            string name = cpuSess.InputMetadata.Keys.First();
            var dims = cpuSess.InputMetadata[name].Dimensions;
            Console.WriteLine($"[CPU] 会话创建成功，输入名={name} 维={string.Join("x", dims)}");

            int h = sizeH, w = size;
            var t = new DenseTensor<float>(new[] { 1, 3, h, w });
            for (int c = 0; c < 3; c++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) t[0, c, y, x] = 0f;
            using var res = cpuSess.Run(new[] { NamedOnnxValue.CreateFromTensor(name, t) });
            var o = res.First().AsTensor<float>();
            Console.WriteLine($"[CPU] Run 成功，输出 {o.Dimensions[2]}x{o.Dimensions[3]} ✅");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CPU] 失败: {ex.GetType().Name}: {ex.Message}");
        }

        // [B] 再跑 DirectML（与产品一致的用法）
        Console.WriteLine("[DML] 准备创建会话…");
        Console.Out.Flush();
        try
        {
            var dmlOpts = new SessionOptions();
            dmlOpts.AppendExecutionProvider_DML(0);
            dmlOpts.EnableCpuMemArena = true;
            Console.WriteLine($"[DML] AppendExecutionProvider_DML 成功（注意：产品里的 IsDirectMLAvailable 只走到这一步）");
            Console.Out.Flush();

            using var dmlSess = new InferenceSession(detPath, dmlOpts);
            string dmlName = dmlSess.InputMetadata.Keys.First();
            Console.WriteLine("[DML] 会话创建成功，准备 Run…");
            Console.Out.Flush();

            int h = sizeH, w = size;
            var t = new DenseTensor<float>(new[] { 1, 3, h, w });
            for (int c = 0; c < 3; c++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) t[0, c, y, x] = 0f;
            using var res = dmlSess.Run(new[] { NamedOnnxValue.CreateFromTensor(dmlName, t) });
            var o = res.First().AsTensor<float>();
            Console.WriteLine($"[DML] Run 成功，输出 {o.Dimensions[2]}x{o.Dimensions[3]} ✅");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DML] 失败: {ex.GetType().Name}: {ex.Message}");
        }

        Console.Out.Flush();
        Console.WriteLine("结论：若 [CPU] ✅ 而 [DML] 崩溃 → 与产品代码无关，属环境/驱动/ORT-DML 问题。");
    }

    /// <summary>
    /// DirectML SessionOptions 逐项累加定位。裸 ORT + DML 在产品的真实 det 形状下
    /// 全部通过（已验证到 5952x512），故崩溃必来自某个 SessionOptions 配置。
    /// 用法: --ocr-dml-opt <level 0-5> [w] [h] [modelDir]
    ///   level 0 裸默认
    ///         1 +GraphOptimizationLevel=ORT_ENABLE_BASIC
    ///         2 +optimization.disable_simplified_layer_norm_fusion=1
    ///         3 +EnableMemoryPattern=true
    ///         4 +session.use_ort_model_bytes_directly=1
    ///         5 +ExecutionMode/线程/日志（= 产品完整配置）
    /// </summary>
    public static void RunDmlOpt(string[] args)
    {
        int level = args.Length >= 2 && int.TryParse(args[1], out var lv) ? lv : 5;
        int w = args.Length >= 3 && int.TryParse(args[2], out var ww) ? ww : 1152;
        int h = args.Length >= 4 && int.TryParse(args[3], out var hh) ? hh : 512;
        string modelDir = args.Length >= 5 && args[4] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");
        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");

        var opts = new SessionOptions();
        var applied = new List<string>();

        if (level >= 0)
        {
            opts.AppendExecutionProvider_DML(0);
            applied.Add("DML(0)");
        }
        if (level >= 1)
        {
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
            applied.Add("ORT_ENABLE_BASIC");
        }
        if (level >= 2)
        {
            try { opts.AddSessionConfigEntry("optimization.disable_simplified_layer_norm_fusion", "1"); applied.Add("noLayerNormFusion"); }
            catch { applied.Add("noLayerNormFusion(不支持)"); }
        }
        if (level >= 3) { opts.EnableMemoryPattern = true; applied.Add("EnableMemoryPattern"); }
        if (level >= 4)
        {
            try { opts.AddSessionConfigEntry("session.use_ort_model_bytes_directly", "1"); applied.Add("use_ort_model_bytes_directly"); }
            catch { applied.Add("use_ort_model_bytes_directly(不支持)"); }
        }
        if (level >= 5)
        {
            opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            opts.InterOpNumThreads = 1;
            int cores = Environment.ProcessorCount;
            opts.IntraOpNumThreads = Math.Max(2, cores > 4 ? cores - 1 : cores);
            opts.EnableCpuMemArena = true;
            opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            applied.Add("SEQ/threads/log");
        }

        Console.WriteLine($"═══ DML SessionOptions 定位 level={level} 尺寸={w}x{h} ═══");
        Console.WriteLine($"配置: {string.Join(", ", applied)}");
        Console.Out.Flush();

        using var sess = new InferenceSession(detPath, opts);
        string name = sess.InputMetadata.Keys.First();
        Console.WriteLine("会话创建成功，准备 Run…");
        Console.Out.Flush();

        var t = new DenseTensor<float>(new[] { 1, 3, h, w });
        using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(name, t) });
        var o = res.First().AsTensor<float>();
        Console.WriteLine($"✅ Run 成功，输出 {o.Dimensions[2]}x{o.Dimensions[3]}");
        Console.Out.Flush();
    }

    /// <summary>
    /// 用真实 OnnxOcrEngine + DirectML 跑一张指定尺寸的纯合成图（不渲染文字），
    /// 用于判断「产品路径下是否存在任何能成功的 DirectML 推理」。
    /// 用法: --ocr-dml-raw <w> <h> [cap] [split] [modelDir]
    /// </summary>
    public static void RunDmlRaw(string[] args)
    {
        int w = args.Length >= 2 && int.TryParse(args[1], out var ww) ? ww : 96;
        int h = args.Length >= 3 && int.TryParse(args[2], out var hh) ? hh : 96;
        float cap = args.Length >= 4 && float.TryParse(args[3], out var c) ? c : 400f;
        float split = args.Length >= 5 && float.TryParse(args[4], out var s) ? s : 90f;
        string modelDir = args.Length >= 6 && args[5] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        // 白底 + 中部一条黑带，保证 det 有可检测内容
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool dark = y > h / 3 && y < h * 2 / 3 && (x / 40) % 2 == 0;
                byte v = dark ? (byte)0 : (byte)255;
                bgra[o] = v; bgra[o + 1] = v; bgra[o + 2] = v; bgra[o + 3] = 255;
            }

        Console.WriteLine($"═══ 产品路径 DirectML 原始推理 {w}x{h} ═══");
        Console.Out.Flush();

        var cfg = OcrModelConfig.Default with { DetMaxBoxAspect = cap, DetSplitBoxAspect = split };
        using var engine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir, cfg);
        Console.WriteLine($"引擎: {engine.Info.Name} 可用={engine.Info.IsAvailable} 模式={engine.Info.Mode}");
        Console.Out.Flush();
        if (!engine.Info.IsAvailable) { Console.WriteLine("引擎不可用"); return; }

        Console.WriteLine("准备 RecognizeAsync…");
        Console.Out.Flush();
        var r = engine.RecognizeAsync(bgra, w, h, "zh-en").GetAwaiter().GetResult();
        Console.WriteLine($"✅ 完成 Error={(r.Error ?? "(无)")} 文本长度={(r.Text ?? "").Length}");
        Console.Out.Flush();
    }

    static string MakeText(int n)
    {
        const string unit = "The quick brown fox jumps over the lazy dog ";
        var sb = new System.Text.StringBuilder();
        while (sb.Length < n) sb.Append(unit);
        return sb.ToString(0, n).TrimEnd();
    }

    static Bitmap RenderLine(string text, int fontSize, int forceH)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        var size = g.MeasureString(text, font);
        int w = (int)Math.Ceiling(size.Width) + 60;
        int h = forceH > 0 ? Math.Max(forceH, (int)Math.Ceiling(font.GetHeight()) + 40)
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

    // ═══════════════════════════════════════════════════════════════
    //  CPU / DirectML 逐阶段 A/B
    //
    //  背景：完整真实语料（292 行，seed 1409）上 CPU 91.48% vs DirectML 87.41%，
    //  且已证伪"模型 FP16 精度"这一解释（换 FP32 模型 DML 仍是 87.59%）。
    //  ⇒ 必须定位差异出在【检测 det】还是【识别 rec】。
    //
    //  做法：**同一份**输入张量分别送两个 EP（SessionOptions 除 EP 外完全一致），
    //  比较 det 概率图（重点是二值化是否分歧 —— 那直接决定框）与 rec 的 argmax 序列。
    //
    //  用法: --ocr-ab <字符数> <画布高> [字号] [modelDir]
    // ═══════════════════════════════════════════════════════════════

    private static SessionOptions MakeOpts(bool dml)
    {
        var o = new SessionOptions();
        if (dml) { o.AppendExecutionProvider_DML(0); o.EnableCpuMemArena = true; }
        o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
        try { o.AddSessionConfigEntry("optimization.disable_simplified_layer_norm_fusion", "1"); } catch { }
        o.EnableMemoryPattern = true;
        try { o.AddSessionConfigEntry("session.use_ort_model_bytes_directly", "1"); } catch { }
        o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        o.InterOpNumThreads = 1;
        o.IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 1 : Environment.ProcessorCount);
        o.EnableCpuMemArena = true;
        o.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        return o;
    }

    public static void RunAb(string[] args)
    {
        int nChars = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 80;
        int canvasH = args.Length >= 3 && int.TryParse(args[2], out var ch) ? ch : 64;
        int fontSize = args.Length >= 4 && int.TryParse(args[3], out var fs) ? fs : 18;
        string modelDir = args.Length >= 5 && args[4] is string m && m.Length > 0
            ? m : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        string recPath = Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx");
        if (!File.Exists(recPath)) recPath = Path.Combine(modelDir, "PP-OCRv6_rec.onnx");
        if (!File.Exists(detPath) || !File.Exists(recPath))
        { Console.WriteLine($"模型不存在: {modelDir}"); return; }

        string text = MakeText(nChars);
        using var bmp = RenderLine(text, fontSize, canvasH);
        var bgra = BitmapToBgra(bmp);
        int w = bmp.Width, h = bmp.Height;

        Console.WriteLine("═══ CPU / DirectML 逐阶段 A/B ═══");
        Console.WriteLine($"内容: {nChars} 字符   字号 {fontSize}px   源图 {w}x{h}");
        Console.WriteLine($"det: {Path.GetFileName(detPath)}   rec: {Path.GetFileName(recPath)}\n");

        // ─────────── 1. det 输入（两 EP 共用同一份张量）───────────
        float ratio = Math.Min((float)LimitSide / Math.Min(w, h), MaxRatio);
        int rw = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int rh = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
        float ratioW = (float)w / rw, ratioH = (float)h / rh;

        var detIn = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        for (int y = 0; y < rh; y++)
        {
            float sy = y * ratioH; int y0 = Math.Clamp((int)sy, 0, h - 1); int y1 = Math.Clamp(y0 + 1, 0, h - 1); float wy = sy - y0;
            for (int x = 0; x < rw; x++)
            {
                float sx = x * ratioW; int x0 = Math.Clamp((int)sx, 0, w - 1); int x1 = Math.Clamp(x0 + 1, 0, w - 1); float wx = sx - x0;
                for (int c = 0; c < 3; c++)
                {
                    float v00 = bgra[(y0 * w + x0) * 4 + c] / 255f, v01 = bgra[(y0 * w + x1) * 4 + c] / 255f;
                    float v10 = bgra[(y1 * w + x0) * 4 + c] / 255f, v11 = bgra[(y1 * w + x1) * 4 + c] / 255f;
                    float val = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                    detIn[0, c, y, x] = (val - 0.5f) / 0.5f;
                }
            }
        }
        Console.WriteLine($"det 输入: {rw}x{rh} (两 EP 完全相同)\n");

        (float[] Buf, int Oh, int Ow) RunDet(InferenceSession s)
        {
            string nm = s.InputMetadata.Keys.First();
            using var res = s.Run(new[] { NamedOnnxValue.CreateFromTensor(nm, detIn) });
            var o = res.First().AsTensor<float>();
            int oh = o.Dimensions[2], ow = o.Dimensions[3];
            var buf = new float[oh * ow];              // ⚠ 拷出到托管内存（零拷贝视图离开 using 即失效）
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++) buf[y * ow + x] = o[[0, 0, y, x]];
            return (buf, oh, ow);
        }

        (float[] Buf, int Oh, int Ow) dCpu, dDml;
        using (var s = new InferenceSession(detPath, MakeOpts(false))) { dCpu = RunDet(s); }
        using (var s = new InferenceSession(detPath, MakeOpts(true))) { dDml = RunDet(s); }
        var detCpu = dCpu.Buf; var detDml = dDml.Buf;
        int ohh = dCpu.Oh, oww = dCpu.Ow;

        Console.WriteLine("── 1. 检测 det：概率图对比 ──");
        double Diff(float[] a, float[] b, out double maxAbs, out int nBinDiff, out int nHiCpu, out int nHiDml)
        {
            double sum = 0; maxAbs = 0; nBinDiff = 0; nHiCpu = 0; nHiDml = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = Math.Abs(a[i] - b[i]);
                sum += d; if (d > maxAbs) maxAbs = d;
                bool bc = a[i] > LowThresh, bd = b[i] > LowThresh;
                if (bc) nHiCpu++; if (bd) nHiDml++;
                if (bc != bd) nBinDiff++;
            }
            return sum / a.Length;
        }
        double meanAbs = Diff(detCpu, detDml, out double maxAbsD, out int binDiff, out int hiC, out int hiD);
        Console.WriteLine($"  输出像素: {ohh}x{oww} = {detCpu.Length}");
        Console.WriteLine($"  >{LowThresh:F2} 前景像素: CPU={hiC}  DML={hiD}  差={hiD - hiC}");
        Console.WriteLine($"  **二值化分歧像素: {binDiff}** (占 {100.0 * binDiff / detCpu.Length:F3}%)  ← 直接决定框是否一致");
        Console.WriteLine($"  概率值差异: 平均 |Δ|={meanAbs:F6}  最大 |Δ|={maxAbsD:F6}");
        Console.WriteLine($"  判定: {(binDiff == 0 ? "✅ det 完全一致 → 差异【不在】检测阶段" : "🔴 det 存在分歧 → 差异【在】检测阶段")}\n");

        // ─────────── 2. rec 输入（两 EP 共用同一份张量）───────────
        var cfg = OcrModelConfig.Default;
        int recH = cfg.RecImgH, recMaxW = cfg.RecImgMaxW;
        int recW = Math.Max(4, Math.Min((int)Math.Ceiling(recH * (float)w / h), recMaxW));
        var recIn = new DenseTensor<float>(new[] { 1, 3, recH, recMaxW });
        for (int dy = 0; dy < recH; dy++)
        {
            float sy = (float)dy / recH * h; int y0 = Math.Clamp((int)sy, 0, h - 1); int y1 = Math.Clamp(y0 + 1, 0, h - 1); float wy = sy - y0;
            for (int dx = 0; dx < recW; dx++)
            {
                float sx = (float)dx / recW * w; int x0 = Math.Clamp((int)sx, 0, w - 1); int x1 = Math.Clamp(x0 + 1, 0, w - 1); float wx = sx - x0;
                for (int c = 0; c < 3; c++)
                {
                    float v00 = bgra[(y0 * w + x0) * 4 + c] / 255f, v01 = bgra[(y0 * w + x1) * 4 + c] / 255f;
                    float v10 = bgra[(y1 * w + x0) * 4 + c] / 255f, v11 = bgra[(y1 * w + x1) * 4 + c] / 255f;
                    float val = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                    recIn[0, c, dy, dx] = (val - 0.5f) / 0.5f;
                }
            }
        }

        (int[] Arg, float[] MaxV) RunRec(InferenceSession s)
        {
            string nm = s.InputMetadata.Keys.First();
            using var res = s.Run(new[] { NamedOnnxValue.CreateFromTensor(nm, recIn) });
            var o = res.First().AsTensor<float>();
            int T = o.Dimensions[1], C = o.Dimensions[2];
            var arg = new int[T]; var mv = new float[T];
            for (int t = 0; t < T; t++)
            {
                int bi = 0; float bv = float.MinValue;
                for (int c = 0; c < C; c++) { float v = o[[0, t, c]]; if (v > bv) { bv = v; bi = c; } }
                arg[t] = bi; mv[t] = bv;
            }
            return (arg, mv);
        }

        int[] argCpu; float[] mvCpu, mvDml;
        using (var s = new InferenceSession(recPath, MakeOpts(false))) { (argCpu, mvCpu) = RunRec(s); }
        int[] argDml;
        using (var s = new InferenceSession(recPath, MakeOpts(true))) { (argDml, mvDml) = RunRec(s); }

        int nDiff = 0; double sumMv = 0, maxMv = 0;
        for (int t = 0; t < Math.Min(argCpu.Length, argDml.Length); t++)
        {
            if (argCpu[t] != argDml[t]) nDiff++;
            double d = Math.Abs(mvCpu[t] - mvDml[t]); sumMv += d; if (d > maxMv) maxMv = d;
        }
        Console.WriteLine("── 2. 识别 rec：CTC 序列对比 ──");
        Console.WriteLine($"  时间步 T: CPU={argCpu.Length}  DML={argDml.Length}");
        Console.WriteLine($"  **argmax 分歧步数: {nDiff}/{Math.Min(argCpu.Length, argDml.Length)}** (占 {100.0 * nDiff / Math.Max(1, Math.Min(argCpu.Length, argDml.Length)):F2}%)");
        for (int t = 0; t < Math.Min(12, Math.Min(argCpu.Length, argDml.Length)); t++)
            Console.WriteLine($"    t={t,2}:  CPU argmax={argCpu[t],-6} (p={mvCpu[t]:F4})   DML argmax={argDml[t],-6} (p={mvDml[t]:F4}){(argCpu[t] != argDml[t] ? "   ← 分歧" : "")}");
        Console.WriteLine($"  最大概率差异: 平均 |Δ|={sumMv / Math.Max(1, Math.Min(argCpu.Length, argDml.Length)):F6}  最大 |Δ|={maxMv:F6}\n");

        // ═══ 关键：CTC 阈值 0.5 穿越统计 ═══
        // 生产代码: if (maxIdx != lastChar && maxIdx > 0 && maxVal >= 0.5f) 保留该字符
        // 若两 EP 的置信度在 0.5 附近抖动，字符就会在一端被【丢弃】、另一端被【保留】。
        const float CtcThr = 0.5f;
        int lostOnDml = 0, gainedOnDml = 0, nNonBlank = 0, nNearThr = 0;
        for (int t = 0; t < Math.Min(argCpu.Length, argDml.Length); t++)
        {
            if (argCpu[t] > 0 || argDml[t] > 0) nNonBlank++;
            if (Math.Abs(mvCpu[t] - CtcThr) < 0.15f || Math.Abs(mvDml[t] - CtcThr) < 0.15f) nNearThr++;
            if (mvCpu[t] >= CtcThr && mvDml[t] < CtcThr) lostOnDml++;
            if (mvDml[t] >= CtcThr && mvCpu[t] < CtcThr) gainedOnDml++;
        }
        Console.WriteLine("  ── CTC 阈值 (0.5) 穿越统计 ──");
        Console.WriteLine($"    非 blank 步数: {nNonBlank}     置信度落在 0.5±0.15 的『脆弱区』: {nNearThr}");
        Console.WriteLine($"    **CPU 保留 / DML 丢弃: {lostOnDml}**      DML 保留 / CPU 丢弃: {gainedOnDml}");
        Console.WriteLine($"    ⇒ 净损失字符 ≈ {lostOnDml - gainedOnDml}（若该数显著，即 4pp 差距的直接来源）\n");

        double binRatio = 100.0 * binDiff / detCpu.Length;
        Console.WriteLine("── 结论 ──");
        Console.WriteLine($"  det: 二值化分歧 {binRatio:F4}%（{(binRatio < 0.01 ? "✅ 实质一致" : "🔴 实质分歧")}）");
        Console.WriteLine($"  rec: argmax 分歧 {100.0 * nDiff / Math.Max(1, Math.Min(argCpu.Length, argDml.Length)):F2}%，" +
            $"置信度最大 |Δ|={maxMv:F4}（{(maxMv > 0.1 ? "🔴 足以跨越 0.5 阈值" : "✅ 不足以跨越阈值")}）");
        if (binRatio < 0.01 && maxMv > 0.1)
            Console.WriteLine("  ⇒ **差异集中在识别 rec，且机制是「置信度抖动跨越固定 CTC 阈值 0.5」**，非检测问题。");
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════════════════
    //  SessionOptions 生命周期对撞实验
    //
    //  假设：产品 OnnxOcrEngine.LoadModels() 里 SessionOptions 是局部变量，
    //        返回后即不可达 → GC 终结化 SafeHandle → 释放原生 OrtSessionOptions
    //        → InferenceSession 内部仍持有其指针 → 首次 Run 时堆损坏 →
    //        AccessViolationException（不可捕获，进程直接死）。
    //
    //  mode: 0 = DirectML + opts 脱离作用域   1 = DirectML + opts KeepAlive
    //        2 = CPU      + opts 脱离作用域   3 = CPU      + opts KeepAlive
    //  用法: --ocr-dml-life <mode> [w] [h] [modelDir]
    // ═══════════════════════════════════════════════════════════════

    private static SessionOptions? s_keepOpts;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static InferenceSession CreateSessionDetached(string path, bool dml, bool keep)
    {
        var opts = new SessionOptions();
        if (dml)
        {
            opts.AppendExecutionProvider_DML(0);
            opts.EnableCpuMemArena = true;
        }
        var sess = new InferenceSession(path, opts);
        if (keep) s_keepOpts = opts;   // 生根，阻止终结化
        return sess;                    // keep=false 时 opts 在此返回后即不可达
    }

    public static void RunDmlLife(string[] args)
    {
        int mode = args.Length >= 2 && int.TryParse(args[1], out var m) ? m : 0;
        int w = args.Length >= 3 && int.TryParse(args[2], out var ww) ? ww : 96;
        int h = args.Length >= 4 && int.TryParse(args[3], out var hh) ? hh : 96;
        string modelDir = args.Length >= 5 && args[4] is string md && md.Length > 0
            ? md : Path.Combine(AppContext.BaseDirectory, "data", "Models");

        string detPath = Path.Combine(modelDir, "PP-OCRv6_medium_det.onnx");
        if (!File.Exists(detPath)) detPath = Path.Combine(modelDir, "PP-OCRv6_det.onnx");
        if (!File.Exists(detPath)) { Console.WriteLine($"模型不存在: {detPath}"); return; }

        bool dml = mode < 2;
        bool keep = (mode % 2) == 1;

        Console.WriteLine($"═══ SessionOptions 生命周期实验 mode={mode} " +
            $"({(dml ? "DirectML" : "CPU")}, opts {(keep ? "KeepAlive" : "脱离作用域")}) {w}x{h} ═══");
        Console.Out.Flush();

        var sess = CreateSessionDetached(detPath, dml, keep);
        string name = sess.InputMetadata.Keys.First();
        Console.WriteLine($"会话已创建，输入名={name}");
        Console.Out.Flush();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Console.WriteLine("已强制 GC + 等待终结器");
        Console.Out.Flush();

        var t = new DenseTensor<float>(new[] { 1, 3, h, w });
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) t[0, c, y, x] = 0f;
        Console.WriteLine("张量填充完成，准备 Run…");
        Console.Out.Flush();

        using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(name, t) });
        var o = res.First().AsTensor<float>();
        Console.WriteLine($"✅ Run 成功，输出 {o.Dimensions[2]}x{o.Dimensions[3]}");
        Console.Out.Flush();
    }
}
