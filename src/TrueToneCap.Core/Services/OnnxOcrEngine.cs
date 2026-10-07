// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/OnnxOcrEngine.cs
// ONNX Runtime 内嵌 OCR 引擎 — PP-OCRv6 medium, FP16 推理, 纯 C# 零 Python 依赖
//
// 模型来源: PaddleOCR PP-OCRv6_medium → ONNX 导出 (FP32) → FP16 量化
//   检测: PP-OCRv6_medium_det.onnx (FP16, ~30MB, 34.5M 参数, Hmean 86.2%)
//   识别: PP-OCRv6_medium_rec.onnx (FP16, ~37MB, 单模型统一字典 18708 字；实测中英最准、
//         日文需字高 ≥24px、韩文 0% —— 字典不含谚文。RecognizeAsync 收下 lang 但不使用它)
//   字典: ppocrv6_dict.txt (多语言统一, ~15000+ chars)
//   量化工具: scripts/onnx_fp16_quantize.py (onnxconverter-common float16)
//
// 后端: DirectML (GPU, FP16 原生) | CPU (FP16 模型 + FP32 计算)
// 降级: DirectML 不可用 → CPU

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Services;

public enum OnnxExecutionProvider { Cpu, DirectML }

/// <summary>PP-OCRv6 medium 模型配置（参数外部化，便于未来版本升级）。</summary>
public sealed record OcrModelConfig(
    string DetFileName,
    string RecFileName,
    string DictFileName,
    int DetLimitSide = 960,
    int RecImgH = 48,
    int RecImgMaxW = 320,
    float DetThreshold = 0.3f,
    float[]? DetMean = null,
    float[]? DetStd = null,
    float[]? RecMean = null,
    float[]? RecStd = null,
    float DetMaxBoxAspect = 400f,
    float DetSplitBoxAspect = 90f)
{
    public float[] DetMeanV => DetMean ?? [0.5f, 0.5f, 0.5f];
    public float[] DetStdV => DetStd ?? [0.5f, 0.5f, 0.5f];
    public float[] RecMeanV => RecMean ?? [0.5f, 0.5f, 0.5f];
    public float[] RecStdV => RecStd ?? [0.5f, 0.5f, 0.5f];

    /// <summary>PP-OCRv6 medium 默认配置（最大最强模型，34.5M 参数）。</summary>
    public static OcrModelConfig Default => new(
        DetFileName: "PP-OCRv6_medium_det.onnx",
        RecFileName: "PP-OCRv6_medium_rec.onnx",
        DictFileName: "ppocrv6_dict.txt",
        DetLimitSide: 960,
        RecImgH: 48,
        RecImgMaxW: 1280,
        DetThreshold: 0.3f,
        DetMean: [0.5f, 0.5f, 0.5f],
        DetStd: [0.5f, 0.5f, 0.5f],
        RecMean: [0.5f, 0.5f, 0.5f],
        RecStd: [0.5f, 0.5f, 0.5f],
        // ⚠ 见 ExtractBoxes 的 maxAspect 注释。原硬编码 50 会把长行【整行丢弃】
        //   （80 字符单行 AR=63.8 → 被当成横线噪声 → "未检测到文字"）。
        //   2026-09-14 端到端实测（OcrArSweep --ocr-asp-sweep, 18px，召回率）：
        //        字符  上限50   上限200   上限400
        //         40   100%     100%     100%
        //         80     0%     100%     100%
        //        130     0%     103%     103%
        //        195     0%     101%     101%
        //        300     0%       0%     101%   ← 只有 400 能覆盖
        //   噪声实测（--ocr-noise，含整幅宽与短分隔线 5 条）：
        //     上限 50 / 200 / 400 / ∞ 的误检行数完全相同（2 行，且该 2 行为
        //     【既有问题】，与上限无关）→ 放宽上限不引入新增误检。
        //   取 400：既覆盖超长行，又保留对极端长条（AR>400，如整幅宽分隔线 AR≈600）的拦截。
        DetMaxBoxAspect: 400f,
        // ⚠ 见 ExtractBoxes 的 splitAspect 注释：>0 时把超宽长框按 X 拆分，
        //   避免整行送 rec 被 RecImgMaxW(1280) 横向挤压。
        //   实测：130 字符 12% → 103%；195 字符 0% → 101%。
        //   90 的取值使 80 字符(AR 64) 保持整行不拆（实测 64/64 全对），
        //   仅在更长的行才拆分。
        DetSplitBoxAspect: 90f);

    /// <summary>PP-OCRv6 检测模型候选文件名（medium 优先，兼容旧命名）。
    /// 注: INT8 量化实验已结论不可用 (动态/静态 PTQ 均崩溃, 仅 QAT 可行)，候选名不包含 INT8。</summary>
    public static string[] DetFallbackNames =>
    [
        "PP-OCRv6_medium_det.onnx",        // FP32/FP16 标准版
        "PP-OCRv6_det.onnx",
        "ch_PP-OCRv6_det_infer.onnx",
        "ch_PP-OCRv6_det_server_infer.onnx",
    ];

    /// <summary>PP-OCRv6 识别模型候选文件名（medium 优先，兼容旧命名）。</summary>
    public static string[] RecFallbackNames =>
    [
        "PP-OCRv6_medium_rec.onnx",        // FP32/FP16 标准版
        "PP-OCRv6_rec.onnx",
        "ch_PP-OCRv6_rec_infer.onnx",
        "ch_PP-OCRv6_rec_server_infer.onnx",
    ];

    /// <summary>PP-OCRv6 字典候选文件名（ppocrv6_dict.txt 为 v6 正确字典）。</summary>
    public static string[] DictFallbackNames =>
    [
        "ppocrv6_dict.txt",
        "ppocr_keys_v2.txt",
    ];
}

public sealed class OnnxOcrEngine : IOcrEngine, IDisposable
{
    private OnnxExecutionProvider _provider;
    private readonly string _modelDir;
    private readonly OcrModelConfig _config;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private string[]? _dict;
    private bool _available;

    // ── 池化张量 (2026-08-09: 避免每次推理重新分配大张量, 截图连拍省分配) ──
    // 检测张量尺寸随输入图变, 尺寸变化时重建; 识别张量尺寸固定 (配置决定)
    // ═══ 并发安全: Recognition 由 Task.Run 串行化, 但显式加锁防御未来并发调用 ═══
    private readonly object _tensorLock = new();
    private DenseTensor<float>? _detTensor;
    private int _detTensorW, _detTensorH;
    private DenseTensor<float>? _recTensor;

    // ═══ 2026-09-13 P0 修复: 推理串行化信号量 ═══
    // OnnxRuntime InferenceSession 非线程安全, 同一 session 并发 Run() 会导致
    // 原生层崩溃 (进程闪退)。用户快速连续触发 OCR (选区识字+翻译连点) 时,
    // 多个 RecognizeAsync 并发执行 → 同一 _recSession/_detSession 并发 Run → 崩溃。
    // 用 SemaphoreSlim(1,1) 串行化整个识别过程, 后续调用排队等待。
    private readonly SemaphoreSlim _inferGate = new(1, 1);

    // ═══ LayerNorm fusion workaround 的生效状态（2026-10-07 可观测化）═══
    // 背景: `optimization.disable_simplified_layer_norm_fusion` 曾用 `catch {}` 静默吞掉失败。
    //   实测（2026-10-07, ORT 1.27/WinML）：该配置在旧版 ORT 上是【必需】的（不设它时
    //   SimplifiedLayerNormFusion 会破坏 PP-OCRv6 输出 → det 近似全 0 → 识别全空），
    //   但在新版上可能已不再需要。静默吞掉意味着：升级 ORT 后【无法从日志判断它是否仍在生效】，
    //   一旦某个 ORT 版本开始拒绝该 key，OCR 会静默失效而日志里没有任何线索。
    // ⇒ 记录三态并回传，供日志与自检断言使用。
    /// <summary>LayerNorm fusion workaround 的应用结果。</summary>
    public enum LayerNormWorkaroundState
    {
        /// <summary>尚未创建过 SessionOptions。</summary>
        NotAttempted,
        /// <summary>配置已被 ORT 接受（预期状态）。</summary>
        Applied,
        /// <summary>ORT 拒绝该配置（本版本不支持）—— 需排查是否导致输出异常。</summary>
        Rejected,
    }

    /// <summary>本引擎实例最近一次 SessionOptions 创建时 LayerNorm workaround 的结果。</summary>
    public LayerNormWorkaroundState LayerNormWorkaround { get; private set; }
        = LayerNormWorkaroundState.NotAttempted;

    // ── byte→float LUT (2026-08-09: 消除预处理每像素 /255f 除法, 内存带宽优化) ──
    private static readonly float[] s_byteToFloat = BuildByteToFloatLut();

    private static float[] BuildByteToFloatLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++) lut[i] = i / 255f;
        return lut;
    }

    public OcrEngineInfo Info => new(
        $"PP-OCRv6 ({_provider}, FP16)",
        _provider == OnnxExecutionProvider.Cpu ? OcrEngineMode.Cpu : OcrEngineMode.Gpu,
        _available,
        _provider == OnnxExecutionProvider.DirectML ? OcrEngineType.OnnxGpu : OcrEngineType.OnnxCpu,
        Version: "PP-OCRv6");

    public OnnxOcrEngine(OnnxExecutionProvider provider = OnnxExecutionProvider.Cpu,
        string? modelDir = null, OcrModelConfig? config = null)
    {
        _provider = provider;
        _modelDir = modelDir ?? ResolveModelDir();
        _config = config ?? OcrModelConfig.Default;

        // DirectML 不可用 → 降级 CPU
        if (_provider == OnnxExecutionProvider.DirectML && !IsDirectMLAvailable())
        {
            CoreLog.Warn("OCR", "DirectML 不可用，降级到 CPU", LogCategory.OCR);
            _provider = OnnxExecutionProvider.Cpu;
        }

        _available = LoadModels();
        CoreLog.Info("OCR", $"模型加载: {(_available ? "成功 (FP16)" : "失败")}", LogCategory.OCR);
    }

    // ═══════════════════════════════════════
    //  模型加载 (PP-OCRv6, 多候选文件名发现)
    // ═══════════════════════════════════════

    private bool LoadModels()
    {
        try
        {
            // ── 发现模型文件（支持多种导出命名）──
            string? detPath = FindFirstExisting(OcrModelConfig.DetFallbackNames);
            string? recPath = FindFirstExisting(OcrModelConfig.RecFallbackNames);
            string? dictPath = FindFirstExisting(OcrModelConfig.DictFallbackNames);

            if (detPath is null || recPath is null)
            {
                CoreLog.Warn("模型加载", $"PP-OCRv6 模型缺失: {_modelDir}", LogCategory.OCR);
                CoreLog.Warn("模型加载", $"期望文件: {OcrModelConfig.DetFallbackNames[0]} + {OcrModelConfig.RecFallbackNames[0]}", LogCategory.OCR);
                return false;
            }

            if (dictPath is null)
            {
                CoreLog.Warn("模型加载", "字典文件缺失，OCR 将无法解码", LogCategory.OCR);
                return false;
            }

            var opts = CreateSessionOptions();

            CoreLog.Info("模型加载", $"检测模型: {Path.GetFileName(detPath)}", LogCategory.OCR);
            _detSession = new InferenceSession(detPath, opts);

            CoreLog.Info("模型加载", $"识别模型: {Path.GetFileName(recPath)}", LogCategory.OCR);
            _recSession = new InferenceSession(recPath, opts);

            _dict = File.ReadAllLines(dictPath);
            CoreLog.Info("模型加载", $"字典: {Path.GetFileName(dictPath)} ({_dict.Length} 字符)", LogCategory.OCR);

            // ⚠ LayerNorm workaround 的生效状态必须留在【信息级】日志里：
            //   它是"OCR 是否能出正确结果"的隐性前提，只在 Debug 级会随日志级别被过滤掉，
            //   而升级 ORT/切换运行时（如迁 WinML）后正是靠这一行判断是否需要它。
            CoreLog.Info("模型加载",
                $"LayerNorm fusion workaround: {LayerNormWorkaround}" +
                (LayerNormWorkaround == LayerNormWorkaroundState.Rejected
                    ? " ⚠ 未被接受 —— 若出现 det 输出近全 0 / 识别全空，即为此因" : ""),
                LogCategory.OCR);

            CoreLog.Info("模型加载", $"PP-OCRv6 medium 就绪 ({_provider}, FP16)", LogCategory.OCR);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("模型加载", $"失败 ({ex.GetType().Name})", ex, LogCategory.OCR);
            if (ex.InnerException is not null)
                CoreLog.Warn("模型加载", "内部异常", ex.InnerException, LogCategory.OCR);
            return false;
        }
    }

    /// <summary>
    /// 模型目录解析优先级:
    /// 1. 应用内嵌 (AppContext.BaseDirectory/data/Models/) — Release 发布
    /// 2. 应用内嵌 (AppContext.BaseDirectory/Models/) — Debug 构建
    /// 3. 用户目录 (%LOCALAPPDATA%/TrueToneCap/onnx_models/) — 用户手动放置/更新
    /// </summary>
    public static string ResolveModelDir()
    {
        // 优先: Release 发布路径 (data/Models/)
        string bundledDir = Path.Combine(AppContext.BaseDirectory, "data", "Models");
        if (Directory.Exists(bundledDir) && HasModelFiles(bundledDir))
            return bundledDir;

        // Debug 构建路径 (Models/)
        string debugDir = Path.Combine(AppContext.BaseDirectory, "Models");
        if (Directory.Exists(debugDir) && HasModelFiles(debugDir))
            return debugDir;

        // 回退: 用户本地目录 (手动下载/更新模型)
        string userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueToneCap", "onnx_models");
        return userDir;
    }

    /// <summary>检查目录中是否包含至少一个 ONNX 模型文件。</summary>
    private static bool HasModelFiles(string dir)
    {
        foreach (var name in OcrModelConfig.DetFallbackNames)
            if (File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    /// <summary>在模型目录中按候选名顺序查找第一个存在的文件。</summary>
    private string? FindFirstExisting(string[] candidates)
    {
        foreach (var name in candidates)
        {
            var path = Path.Combine(_modelDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// 创建 ONNX Runtime SessionOptions — FP16 优化配置
    /// </summary>
    private SessionOptions CreateSessionOptions()
    {
        var opts = new SessionOptions();

        // ── 执行提供器 ──
        switch (_provider)
        {
            case OnnxExecutionProvider.DirectML:
                opts.AppendExecutionProvider_DML(0);
                opts.EnableCpuMemArena = true;
                break;
            // CPU: 使用默认 CPU EP (FP16 模型在 CPU 上自动提升为 FP32 计算)
        }

        // ── 图优化: ──
        // 2026-08-09 修复: SimplifiedLayerNormFusion 图优化在 ONNX Runtime 破坏
        // PP-OCRv6 检测/识别模型输出 (det 输出几乎全 0 → 识别全空)。
        // 2026-09-06 改进: 用 ORT_ENABLE_BASIC (启用安全优化: 常量折叠/公共子表达式消除等)
        //   同时显式禁用 LayerNorm fusion, 恢复 GPU 加速能力 (DirectML kernel 可用)。
        //   ⚠ 与 ORT_DISABLE_ALL 相比, 预期 DirectML 推理从 10s+ 恢复至 <500ms。
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
        // 显式禁用 SimplifiedLayerNormFusion (ORT 官方配置项, 破坏 PP-OCRv6 输出的元凶)
        //
        // ⚠ 2026-10-07: 原实现是 `catch { }` 静默吞掉。后果 —— 升级 ORT 后如果一个版本开始
        //   拒绝这个 key，OCR 会静默失效（det 近似全 0 → 识别全空），而日志里没有任何线索。
        //   实测 WinML/ORT 1.27 上该配置已非必需，说明"是否需要它"会随版本变化 ⇒ 必须可观测。
        //   现在：成功记 Debug，失败记 Warn（含原因）并落到 LayerNormWorkaround 状态位。
        try
        {
            opts.AddSessionConfigEntry("optimization.disable_simplified_layer_norm_fusion", "1");
            LayerNormWorkaround = LayerNormWorkaroundState.Applied;
            CoreLog.Debug("OCR", "LayerNorm fusion workaround 已生效", LogCategory.OCR);
        }
        catch (Exception ex)
        {
            LayerNormWorkaround = LayerNormWorkaroundState.Rejected;
            CoreLog.Warn("OCR",
                $"LayerNorm fusion workaround 被 ORT 拒绝（{ex.GetType().Name}）—— " +
                "本版本可能不再支持该配置项；若随后出现 det 输出接近全 0 / 识别全空，即为此因",
                ex, LogCategory.OCR);
        }

        // ── FP16 优化 ──
        // 启用内存模式加速 FP16 tensor 分配
        //
        // ⚠ 2026-09-15：DirectML **必须关闭** memory pattern。
        //   ORT 官方 DirectML 文档要求 `DisableMemPattern` + `ORT_SEQUENTIAL`
        //   （DML EP 不支持 memory pattern）。此前对所有 EP 一律设 true 是错的。
        //   实测（292 行真实语料，seed 1409，maxRatio=6 统一后）：见下方结论。
        opts.EnableMemoryPattern = _provider != OnnxExecutionProvider.DirectML;
        // 允许 FP16 计算（DirectML 原生支持; CPU EP 自动提升精度）
        opts.AddSessionConfigEntry("session.use_ort_model_bytes_directly", "1");

        // ── 执行模式 ──
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

        // ── 线程配置 ──
        opts.InterOpNumThreads = 1;
        int cpuCores = Environment.ProcessorCount;
        opts.IntraOpNumThreads = Math.Max(2, cpuCores > 4 ? cpuCores - 1 : cpuCores);

        // ── 内存配置 ──
        opts.EnableCpuMemArena = true;

        // ── 日志 ──
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

        return opts;
    }

    /// <summary>
    /// 检查 DirectML 是否可用（通过尝试创建 DML SessionOptions）
    /// </summary>
    public static bool IsDirectMLAvailable()
    {
        try
        {
            // 尝试创建 DirectML 会话选项 — 如果 DML EP 不可用会抛异常
            var dmlOpts = new SessionOptions();
            dmlOpts.AppendExecutionProvider_DML(0);
            CoreLog.Info("DirectML", "检测成功：DirectML 可用", LogCategory.OCR);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("DirectML", $"不可用（{ex.GetType().Name}）", ex, LogCategory.OCR);
            return false;
        }
    }

    // ═══════════════════════════════════════
    //  OCR 识别
    // ═══════════════════════════════════════

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        // 引擎已释放（如应用退出时）—— 直接拒绝，不要再去排 _inferGate。
        // 否则会拿到门、却在 RecognizeCoreAsync 中撞上 null session。
        if (_disposed)
            return new OcrResult { Error = "ONNX 引擎已释放" };
        if (!_available || _detSession is null || _recSession is null)
            return new OcrResult { Error = "ONNX 引擎不可用" };

        // ═══ 2026-09-13 P0 修复: 串行化等待 (防并发 Run 崩溃) ═══
        // OnnxRuntime InferenceSession 非线程安全, 并发 Run() → 原生崩溃闪退。
        // 在进入推理前获取信号量, 同一引擎实例同一时刻只允许一次识别。
        // 可取消等待: 用户取消/超时时不再排队。
        await _inferGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RecognizeCoreAsync(bgra, w, h, lang, ct).ConfigureAwait(false);
        }
        finally
        {
            _inferGate.Release();
        }
    }

    /// <summary>识别主体 (调用方须持有 _inferGate, 保证 session 不并发)。</summary>
    private async Task<OcrResult> RecognizeCoreAsync(byte[] bgra, int w, int h,
        string? lang, CancellationToken ct)
    {
        // ═══ P0-2: 识别超时看门狗 (防 DirectML 死锁) ═══
        // DirectML 推理可能卡死不抛异常, 导致 UI 永久卡死。在此加总超时。
        //
        // ⚠️ 2026-09-15 修正: 原为 60s，而外层 MultiOcrService.OcrTimeout 只有 45s。
        //    外层先超时时，被放弃的本任务仍持有 _inferGate 直到内层看门狗触发，
        //    下一次 OCR 请求会在 gate 上排队最多 15s（表现为「OCR 卡住」）。
        //    内层阈值必须【严格小于】外层，才能保证内层先走完取消路径并释放 gate。
        //    修改任一处时请同步核对另一处（外层: MultiOcrService.OcrTimeout）。
        const int OcrTimeoutSeconds = 40;
        using var ocrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ocrCts.CancelAfter(TimeSpan.FromSeconds(OcrTimeoutSeconds));
        var token = ocrCts.Token;

        return await Task.Run(() =>
        {
            try
            {
                // 1. 检测文字区域
                var boxes = RunDetection(bgra, w, h);

                // 2. 逐区域识别
                var lines = new List<OcrLine>();
                var allText = new List<string>();
                foreach (var box in boxes)
                {
                    token.ThrowIfCancellationRequested(); // 用带超时的 token
                    string text = RunRecognition(bgra, w, h, box);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // 回填行级边界框（原图坐标），供 OCR 预览窗口"点对点覆盖"定位。
                        // box 坐标已由 ExtractBoxes 映射回原图坐标系。
                        int bx = Math.Max(0, (int)box.X1);
                        int by = Math.Max(0, (int)box.Y1);
                        int bw = Math.Max(1, (int)(box.X2 - box.X1));
                        int bh = Math.Max(1, (int)(box.Y2 - box.Y1));
                        lines.Add(new OcrLine
                        {
                            Text = text,
                            X = bx, Y = by, Width = bw, Height = bh,
                            Words = [new OcrWord { Text = text, X = bx, Y = by, Width = bw, Height = bh }]
                        });
                        allText.Add(text);
                    }
                }

                var result = new OcrResult
                {
                    Text = string.Join("\n", allText),
                    Lines = lines,
                };

                if (lines.Count == 0)
                    result.Error = "未检测到文字";

                return (OcrResult)result;
            }
            catch (OperationCanceledException)
            {
                return new OcrResult { Error = $"OCR 识别超时（超过 {OcrTimeoutSeconds}s），DirectML 可能卡死" };
            }
            catch (Exception ex)
            {
                return new OcrResult { Error = $"ONNX 异常: {ex.Message}" };
            }
        }, token);
    }

    // ═══════════════════════════════════════
    //  文字检测 (DBNet, PP-OCRv6)
    // ═══════════════════════════════════════

    private List<Box> RunDetection(byte[] bgra, int w, int h)
    {
        // ── PP-OCRv6 检测预处理 (匹配 rapidocr limit_type=min) ──
        // 1. 最短边 < limitSide 时放大到 limitSide，否则不缩放
        // 2. 限制最大放大倍数（防止小图过度放大导致检测模型框定位不准）
        //    GPU: 8x（DirectML 处理大图快，但过大图检测精度下降）
        //    CPU: 6x（平衡性能与准确率）
        // 3. 宽高取整到 32 的倍数
        // 4. 归一化: (RGB/255 - mean) / std  (PP-OCRv6: mean=0.5, std=0.5)
        int limitSide = _config.DetLimitSide;
        float ratio;
        if (Math.Min(w, h) < limitSide)
        {
            ratio = (float)limitSide / Math.Min(w, h);
            // ═══ 放大上限：两个 EP 统一取 6.0f ═══
            // ⚠ 2026-09-14 实证（完整真实语料 292 行，同种子 1409，同一批样本）：
            //        DirectML + 8x（原值）: 87.41% (6164/7052)  失败样本 21   525ms/张
            //        DirectML + 6x        : 91.51% (6453/7052)  失败样本 13  2364ms/张
            //        CPU      + 6x        : 91.48% (6451/7052)  失败样本 13  3502ms/张
            //   ⇒ 8x 的"6.7 倍速度优势"有相当部分来自【检测漏框后少跑 rec】，是虚假收益；
            //     6x 下 DirectML 准确率追平 CPU，仍快 1.48 倍，且此前 130/195 字符档
            //     的过召回（109%/115%）随之消失，与 CPU 完全一致（103%/101%）。
            //   ⇒ 统一取 6.0f：CPU/GPU 行为一致、准确率最优、消除两后端结果分歧。
            //   注：极端宽图在 6x 下仍可能突破 DirectML 纹理上限，见下方收紧保护。
            const float maxRatio = 6.0f;
            ratio = Math.Min(ratio, maxRatio);
        }
        else
        {
            ratio = 1.0f;
        }
        int resizeW = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int resizeH = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);

        // ⚠ DirectML (D3D12) 纹理边长硬上限 16384。超过后 ORT 不报错，但检测质量
        //   静默崩塌 —— 2026-09-14 实测（18px 单行，当时 maxRatio=8）：
        //        字符   det 输入        DirectML 识别率   CPU 识别率
        //        130    9344x512        109%             101%
        //        195   13792x512        115%             100%
        //        300   20832x512  ← 超限   66%  ← 崩塌       100%
        //   触发条件：图像短边 < DetLimitSide(960) 且极宽，放大后突破上限
        //   （典型如又宽又矮的截图区域）。⚠ 即使 maxRatio 已统一降到 6.0f，
        //   宽度 > 2730 且短边 < 160 的图仍会触发，保护必须保留。
        //   此处按比例收紧到上限以内；
        //   ratioW/ratioH 由【最终】resizeW/resizeH 反算，非等比收紧也不会错位。
        if (_provider == OnnxExecutionProvider.DirectML &&
            (resizeW > MaxDmlTextureDim || resizeH > MaxDmlTextureDim))
        {
            float k = (float)MaxDmlTextureDim / Math.Max(resizeW, resizeH);
            resizeW = Math.Max((int)(resizeW * k / 32) * 32, 32);
            resizeH = Math.Max((int)(resizeH * k / 32) * 32, 32);
            CoreLog.Warn("OCR", $"det 输入超 DirectML 上限，收紧至 {resizeW}x{resizeH}", LogCategory.OCR);
        }
        else if ((long)resizeW * resizeH > MaxCpuTensorPixels)
        {
            // ═══ 2026-09-15 修复：CPU 路径此前完全没有尺寸上限 ═══
            // 上面的收紧只在 DirectML 分支内，CPU 直接沿用放大后的尺寸，
            // 于是"选一条细长区域"就会申请数百 MB 张量并长时间卡死。
            // 按总像素数等比收紧（而非边长），对正常的宽图/高图影响更小。
            float k = MathF.Sqrt((float)MaxCpuTensorPixels / ((long)resizeW * resizeH));
            resizeW = Math.Max((int)(resizeW * k / 32) * 32, 32);
            resizeH = Math.Max((int)(resizeH * k / 32) * 32, 32);
            CoreLog.Warn("OCR", $"det 输入像素超 CPU 上限，收紧至 {resizeW}x{resizeH}", LogCategory.OCR);
        }

        float ratioW = (float)w / resizeW;
        float ratioH = (float)h / resizeH;

        // 池化检测张量 (尺寸变化时重建, 否则复用) —— 锁保护张量重建
        DenseTensor<float> input;
        lock (_tensorLock)
        {
            if (_detTensor is null || _detTensorW != resizeW || _detTensorH != resizeH)
            {
                _detTensor = new DenseTensor<float>([1, 3, resizeH, resizeW]);
                _detTensorW = resizeW;
                _detTensorH = resizeH;
            }
            input = _detTensor;
        }
        float[] mean = _config.DetMeanV;
        float[] std = _config.DetStdV;
        var lut = s_byteToFloat;

        // 二值化阈值 = DetThreshold (rapidocr 标准 0.3)
        // ⚠ 2026-08-10 修复: 之前减半为 0.15, 背景噪声(0.15~0.3)全被二值化为前景,
        //   与文字连通成巨大区域后平均置信度被拉低, boxThresh 过滤把所有框删光 → "未检测到文字"
        float threshold = _config.DetThreshold;

        // ═══ 分离式双线性 (2026-08-09: 预计算坐标权重 + 中间行缓冲, 减少重复计算) ═══
        // 1. 预计算水平/垂直坐标权重 (避免每像素重复整除/取余)
        var x0t = new int[resizeW];
        var x1t = new int[resizeW];
        var wxt = new float[resizeW];
        for (int x = 0; x < resizeW; x++)
        {
            float srcXf = x * ratioW;
            int x0 = Math.Clamp((int)srcXf, 0, w - 1);
            x0t[x] = x0;
            x1t[x] = Math.Clamp(x0 + 1, 0, w - 1);
            wxt[x] = srcXf - x0;
        }
        var y0t = new int[resizeH];
        var y1t = new int[resizeH];
        var wyt = new float[resizeH];
        for (int y = 0; y < resizeH; y++)
        {
            float srcYf = y * ratioH;
            int y0 = Math.Clamp((int)srcYf, 0, h - 1);
            y0t[y] = y0;
            y1t[y] = Math.Clamp(y0 + 1, 0, h - 1);
            wyt[y] = srcYf - y0;
        }

        // 2. 水平缩放: 每行 srcY0/srcY1 生成中间行缓冲 (3 通道)
        //    中间行 [srcY][x][c] — 复用内层垂直合并
        // ⚠ 2026-08-10 修复: 缓冲必须声明在 Parallel.For 内部(每任务独立)!
        //   之前声明在外部, 多线程同时写同一缓冲区 → 数据竞争 → det 输入错乱
        //   → 检测框位置错误/识别垃圾 (rec 单独测试正常, 端到端失败)

        // 3. 垂直合并 + 归一化 (并行每输出行)
        Parallel.For(0, resizeH, y =>
        {
            var srcRow0 = new float[resizeW * 3];
            var srcRow1 = new float[resizeW * 3];
            int sy0 = y0t[y], sy1 = y1t[y];
            float wy = wyt[y];
            int row0Base = sy0 * w * 4;
            int row1Base = sy1 * w * 4;

            // 水平缩放当前 2 条源行 → 中间缓冲
            for (int x = 0; x < resizeW; x++)
            {
                int x0 = x0t[x], x1t_i = x1t[x];
                float wx = wxt[x];
                int i0 = row0Base + x0 * 4;
                int i1 = row0Base + x1t_i * 4;
                int j0 = row1Base + x0 * 4;
                int j1 = row1Base + x1t_i * 4;
                int ob = x * 3;
                for (int c = 0; c < 3; c++)
                {
                    float h0 = lut[bgra[i0 + c]] * (1 - wx) + lut[bgra[i1 + c]] * wx;
                    float h1 = lut[bgra[j0 + c]] * (1 - wx) + lut[bgra[j1 + c]] * wx;
                    srcRow0[ob + c] = h0;
                    srcRow1[ob + c] = h1;
                }
            }

            // 垂直合并 + 归一化 → 张量
            for (int x = 0; x < resizeW; x++)
            {
                int ob = x * 3;
                for (int c = 0; c < 3; c++)
                {
                    float val = srcRow0[ob + c] * (1 - wy) + srcRow1[ob + c] * wy;
                    input[0, c, y, x] = (val - mean[c]) / std[c];
                }
            }
        });

        // 推理 - 添加 SEH 保护防止 DirectML 原生崩溃
        //
        // ⚠ 2026-09-14 P0 修复 (use-after-free):
        //   `AsTensor<float>()` 返回的是【零拷贝视图】——它直接指向 ORT 拥有的原生输出
        //   缓冲区，并不复制数据。而 `using var results` 的释放在 try 块结束时触发，
        //   一旦离开该块，output 背后的内存即被 ORT 回收。
        //   旧代码把 output 带出 try 块再读取 —— 典型的 use-after-free：
        //     · CPU EP: 输出来自 CPU memory arena，释放后并未立即归还 OS，
        //              读到的数据"碰巧"仍然有效 → 长期未被发现；
        //     · DirectML EP: 输出缓冲区被真正释放/解除映射 → 读取触发
        //              AccessViolationException (0xC0000005)。该异常在 .NET 中不可捕获，
        //              进程直接终止（退出码 -1073741819），try/catch 完全无效。
        //   ⇒ 必须在 results 存活期间把需要的数据拷进托管数组。
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        float[]? probMap = null;
        byte[]? bitmap = null;
        int oh = 0, ow = 0;
        try
        {
            using var results = _detSession!.Run(inputs);
            var output = results.First().AsTensor<float>();

            // 后处理: 双阈值二值化 + 置信度过滤
            // ═══ 2026-09-06: 双阈值融合
            //   lowThresh (0.25) 高召回 → 小字/低对比文本不漏检
            //   highThresh (0.35) 高精度 → 强文字区域 (用于置信度过滤的 boxThresh)
            //   二值图用 lowThresh 生成 (召回优先), 噪声交由 ExtractBoxes 的置信度过滤剔除。
            oh = output.Dimensions[2];
            ow = output.Dimensions[3];

            // 保存原始概率图（用于置信度过滤）—— 拷出到托管内存
            probMap = new float[oh * ow];
            bitmap = new byte[oh * ow];
            float lowThresh = threshold - 0.05f;      // 0.25 召回优先
            for (int y2 = 0; y2 < oh; y2++)
                for (int x2 = 0; x2 < ow; x2++)
                {
                    float v = output[[0, 0, y2, x2]];
                    probMap[y2 * ow + x2] = v;
                    // 召回优先二值化 (低阈值)
                    bitmap[y2 * ow + x2] = v > lowThresh ? (byte)255 : (byte)0;
                }
        }
        // ⛔ 2026-09-15: 本分支在 .NET (Core/5+/11) 上【不可达】—— CSE 进程内不可捕获，
        //    真崩溃时进程直接终止（实测退出码 -1073741819，见 tests/manual/cse-probe）。
        //    保留仅作文档与未来兼容，【不要】把它当作"崩溃已兜底"。
        //    真正的修复是上面「在 results 存活期内完成拷贝」这一写法本身。
        catch (Exception ex) when (ex is AccessViolationException || ex.GetType().Name.Contains("SEH"))
        {
            CoreLog.Warn("OCR", $"检测推理 SEH 崩溃（{ex.GetType().Name}）→ 返回空框", ex, LogCategory.OCR);
            return [];
        }
        catch (Exception ex)
        {
            CoreLog.Warn("OCR", "检测推理异常 → 返回空框", ex, LogCategory.OCR);
            return [];
        }

        if (probMap is null || bitmap is null)
            return [];

        // ═══ 不使用膨胀 ═══
        // medium 模型概率图质量已足够好，膨胀反而导致大图上相邻行合并
        // rapidocr 的 use_dilation 主要针对 small/mobile 模型的断裂问题
        var dilated = bitmap; // 直接使用二值化结果

        // 缩放回原始坐标（传入概率图用于置信度过滤）
        var boxes = ExtractBoxes(dilated, ow, oh, ratioW, ratioH, probMap,
            _config.DetMaxBoxAspect, _config.DetSplitBoxAspect);
        return boxes;
    }

    // ═══════════════════════════════════════
    //  文字识别 (SVTR_LCNet/CRNN)
    // ═══════════════════════════════════════

    private string RunRecognition(byte[] bgra, int imgW, int imgH, Box box)
    {
        int x = Math.Max(0, (int)box.X1);
        int y = Math.Max(0, (int)box.Y1);
        int bw = Math.Min(imgW - x, (int)(box.X2 - box.X1));
        int bh = Math.Min(imgH - y, (int)(box.Y2 - box.Y1));
        if (bw <= 0 || bh <= 0) return "";

        // ═══ 裁剪边距 padding（防止文字边缘被截断）═══
        // ⚠ 长行拆分出的子框不能用「框宽的 10%」做水平边距：相邻子框各自外扩 10%
        //   （130 字符实测约 340px）会让识别区域互相重叠 → 相邻文本被识别两次
        //   （召回率 113% 的重复字符即由此而来）。
        //   子框改用「按字高封顶」。
        //
        //   ⚠ 实测（2026-09-14，18px）：边距取值【不是】过召回的主因 ——
        //     · DirectML 130/195 字符档：min(bw/10, bh) 与 bh/4 结果完全相同
        //       （113/104、179/156）⇒ DML 的过召回来自 FP16 rec，不是边距；
        //     · 但真实渲染基准 --ocr-bench-real 上，bh/4 比 min(bw/10, bh)
        //       【少 5 个字符】（6446 vs 6451 / 7052，两次运行均可复现）。
        //     ⇒ 取 min(bw/10, bh)：有实测优势，且同样杜绝了 10% 的巨额重叠。
        int padX = box.IsFragment
            ? Math.Max(2, Math.Min(bw / 10, bh))
            : Math.Max(2, bw / 10);       // 水平边距 10%
        int padY = Math.Max(2, bh / 5);   // 垂直边距 20%
        int cropX = Math.Max(0, x - padX);
        int cropY = Math.Max(0, y - padY);
        int cropW = Math.Min(imgW - cropX, bw + padX * 2);
        int cropH = Math.Min(imgH - cropY, bh + padY * 2);
        if (cropW <= 0 || cropH <= 0) return "";

        // ── PP-OCRv6 识别预处理 ──
        // crop → resize to H×W (keep aspect) → pad to MaxW → normalize → NCHW
        int recH = _config.RecImgH;
        int recMaxW = _config.RecImgMaxW;
        float aspect = (float)cropW / cropH;
        int recW = Math.Min((int)Math.Ceiling(recH * aspect), recMaxW);
        recW = Math.Max(4, recW);

        // 双线性缩放 crop 到 recW×recH
        // 池化识别张量 (尺寸固定 recH×recMaxW, 创建一次复用) —— 锁保护
        DenseTensor<float> input;
        lock (_tensorLock)
        {
            _recTensor ??= new DenseTensor<float>([1, 3, recH, recMaxW]);
            input = _recTensor;
            // 复用前清零 padding 区 (右侧 dx>=recW 残留旧数据会污染推理)
            input.Buffer.Span.Clear();
        }
        float[] recMean = _config.RecMeanV;
        float[] recStd = _config.RecStdV;
        var lut = s_byteToFloat;

        // 预计算水平坐标权重 (避免每 dx 重复整除/取余)
        var rx0 = new int[recW];
        var rx1 = new int[recW];
        var rwx = new float[recW];
        for (int dx = 0; dx < recW; dx++)
        {
            float srcXf = cropX + (float)dx / recW * cropW;
            int x0 = Math.Clamp((int)srcXf, 0, imgW - 1);
            rx0[dx] = x0;
            rx1[dx] = Math.Clamp(x0 + 1, 0, imgW - 1);
            rwx[dx] = srcXf - x0;
        }

        for (int dy = 0; dy < recH; dy++)
        {
            float srcYf = cropY + (float)dy / recH * cropH;
            int srcY0 = Math.Clamp((int)srcYf, 0, imgH - 1);
            int srcY1 = Math.Clamp(srcY0 + 1, 0, imgH - 1);
            float wy = srcYf - srcY0;
            int row0Base = srcY0 * imgW * 4;
            int row1Base = srcY1 * imgW * 4;
            for (int dx = 0; dx < recW; dx++)
            {
                int x0 = rx0[dx], x1 = rx1[dx];
                float wx = rwx[dx];

                int i00 = row0Base + x0 * 4;
                int i01 = row0Base + x1 * 4;
                int i10 = row1Base + x0 * 4;
                int i11 = row1Base + x1 * 4;

                // PP-OCRv6 识别模型期望 BGR 通道顺序 (inference.yml: img_mode=BGR)
                // BGRA 内存布局 [B=0,G=1,R=2,A=3] → 直接取 c=0,1,2 即为 BGR
                for (int c = 0; c < 3; c++)
                {
                    float v00 = lut[bgra[i00 + c]];
                    float v01 = lut[bgra[i01 + c]];
                    float v10 = lut[bgra[i10 + c]];
                    float v11 = lut[bgra[i11 + c]];
                    float v0 = v00 * (1 - wx) + v01 * wx;
                    float v1 = v10 * (1 - wx) + v11 * wx;
                    float val = v0 * (1 - wy) + v1 * wy;
                    input[0, c, dy, dx] = (val - recMean[c]) / recStd[c];
                }
            }
        }
        // 右侧 padding 保持为 0 (已初始化)

        // 推理 - 添加 SEH 保护防止 DirectML 原生崩溃
        // ⚠ 同 RunDetection 的 P0 use-after-free 修复：output 是零拷贝视图，
        //   必须在 results 存活期间把 logits 拷进托管数组（CPU 下"碰巧能读"掩盖了多年）。
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        List<int>? decoded = null;
        try
        {
            using var results = _recSession!.Run(inputs);
            var output = results.First().AsTensor<float>();

            // ═══ 2026-09-06 修复: CTC 置信度过滤 — maxVal < 0.5 视为噪声丢弃 (防乱码)
            // ═══ 2026-09-06 验证结论: 模型输出 18710 类 = blank(0) + 18708 字典字符 + 1 尾随特殊类
            //   映射 _dict[d-1]: d=1→`!`, d=2→`"`, ... (模型导出用无空格字典, 空格输出为 blank)
            //
            // ⚠ 解码必须在此（results 存活期间）完成：output 是零拷贝视图，离开 using 后
            //   内存即被 ORT 回收。这里【不】额外拷出 logits —— 每个框约
            //   320×18710×4B ≈ 24 MB，多框截图下会造成显著的内存压力与 GC 抖动。
            int timeSteps = output.Dimensions[1];
            int numClasses = output.Dimensions[2];
            var d2 = new List<int>();
            int lastChar = -1;
            const float ctcConfidenceThreshold = 0.5f;

            for (int t = 0; t < timeSteps; t++)
            {
                int maxIdx = 0;
                float maxVal = float.MinValue;
                for (int c2 = 0; c2 < numClasses; c2++)
                {
                    float v = output[[0, t, c2]];
                    if (v > maxVal) { maxVal = v; maxIdx = c2; }
                }
                // 跳过 blank(0) 和重复; 低置信度丢弃
                if (maxIdx != lastChar && maxIdx > 0 && maxVal >= ctcConfidenceThreshold)
                    d2.Add(maxIdx);
                lastChar = maxIdx;
            }
            decoded = d2;
        }
        // ⛔ 2026-09-15: 同检测分支，CSE 在 .NET 上不可捕获，本分支不可达（保留仅作文档）。
        catch (Exception ex) when (ex is AccessViolationException || ex.GetType().Name.Contains("SEH"))
        {
            CoreLog.Warn("OCR", $"识别推理 SEH 崩溃（{ex.GetType().Name}）→ 丢弃该区域", ex, LogCategory.OCR);
            return "";
        }
        catch (Exception ex)
        {
            CoreLog.Warn("OCR", "识别推理异常 → 丢弃该区域", ex, LogCategory.OCR);
            return "";
        }

        if (decoded is null) return "";

        // 字典映射: 模型输出索引 1..N 对应字典 0..N-1 (blank 占索引 0)
        if (_dict is null) return "";
        return string.Concat(decoded
            .Where(d => d - 1 < _dict.Length)
            .Select(d => _dict[d - 1]));
    }

    // ═══════════════════════════════════════
    //  边界框提取（简化连通域分析）
    // ═══════════════════════════════════════

    // 长行拆分下限：det 空间下框高至少这么多才拆。
    // 真实文字在 det 空间的核心高度实测 ≥12（缩放 1.29x）～62（缩放 6x）；
    // 低于此的基本是 1~2px 的横线/边框噪声，不应拆开（拆了反而更容易漏过噪声过滤）。
    private const int MinSplitBoxH = 6;
    // 单个连通域最多拆成多少块（防止异常宽区域产生海量子框拖垮性能）
    private const int MaxSplitParts = 32;

    /// <summary>DirectML (D3D12) 纹理边长硬上限，见 RunDetection 中的收紧保护。</summary>
    private const int MaxDmlTextureDim = 16384;

    /// <summary>
    /// CPU 路径的检测输入总像素上限。
    /// <para>CPU 不走 D3D12 纹理，不受 16384 边长限制，但检测张量常驻托管堆
    /// （1×3×H×W×4B）。细长选区（如 6000×150，短边 &lt; 960 触发 6 倍放大）
    /// 会得到约 36000×896 ≈ 3226 万像素 ≈ <b>387 MB</b>，之后还要逐行再分配缓冲
    /// ⇒ 内存暴涨并长时间卡死。</para>
    /// 12M 像素约 150 MB，对常规截图（1920×1080 ≈ 2M）毫无影响，只兜住极端宽高比。
    /// </summary>
    private const long MaxCpuTensorPixels = 12_000_000L;

    /// <summary>子框置信度检查：子框内 &gt;boxThresh 的像素占比须 &gt;highRatio。</summary>
    private static bool SubBoxConfident(float[] probMap, int w, int x1, int y1, int x2, int y2)
    {
        const float boxThresh = 0.7f;
        const float highRatio = 0.10f;
        int high = 0, total = 0;
        for (int y = y1; y <= y2; y++)
            for (int x = x1; x < x2; x++)
            {
                total++;
                if (probMap[y * w + x] > boxThresh) high++;
            }
        return total == 0 || (float)high / total >= highRatio;
    }

    /// <param name="probMap">原始概率图，用于置信度过滤。</param>
    /// <param name="maxAspect">
    /// 连通域框的宽高比上限，超出即判为噪声（横线/边框）丢弃。
    /// ⚠️ 2026-09-14 实测（OcrProbProbe）：DBNet 概率图质量很好（max≈1.0、
    /// 高激活占比 93~95%），但长行文字会粘连成【一个】连通域：
    ///   40 字符 → 框 1867x67  AR=28.3 → 通过，识别 32/32 全对
    ///   80 字符 → 框 3892x62  AR=63.8 → 被 AR&lt;50 丢弃 → "未检测到文字"
    ///   80 字符(高画布) → 框 841x12 AR=76.4 → 同样被丢弃
    /// 即该上限是长行整行漏检的根因。值太小误杀真实长行，太大放过横线噪声。
    /// </param>
    /// <param name="splitAspect">
    /// 长行拆分阈值：连通域框 AR 超过此值时，按 X 方向拆成多个 AR ≤ 该值的子框分别送 rec。
    /// ⚠️ 2026-09-14 实测：整行粘连成单框时，rec 阶段会被 RecImgMaxW(1280) 横向挤压 ——
    ///   80 字符(AR 64) 挤压约 2.4x，仍 64/64 全对；
    ///   130 字符(AR 104) 挤压约 3.9x → 只识别出 12/104。
    ///   拆分后每块挤压可控；代价是一个视觉行可能变成多个 OcrLine。
    /// 传 0 表示禁用拆分（旧行为）。
    /// </param>
    private static List<Box> ExtractBoxes(byte[] bitmap, int w, int h,
        float scaleX, float scaleY, float[]? probMap = null,
        float maxAspect = 50f, float splitAspect = 0f)
    {
        const float boxThresh = 0.7f;    // 高激活像素占比阈值 (强文字区域像素应 ≥70% 概率)
        const float highRatio = 0.10f;   // 连通域内 >boxThresh 像素占比须 >10% (抗背景噪声拉低均值)

        var boxes = new List<Box>();
        var visited = new bool[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (bitmap[idx] == 0 || visited[idx]) continue;

                // BFS 找连通域
                int minX = x, maxX = x, minY = y, maxY = y;
                int highCount = 0; int totalCount = 0;
                var queue = new Queue<(int, int)>();
                queue.Enqueue((x, y));
                visited[idx] = true;

                while (queue.Count > 0 && queue.Count < w * h)
                {
                    var (cx, cy) = queue.Dequeue();
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);

                    // 统计高激活像素 (用于置信度过滤, 抗背景噪声)
                    if (probMap is not null)
                    {
                        totalCount++;
                        if (probMap[cy * w + cx] > boxThresh) highCount++;
                    }

                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                        {
                            int nidx = ny * w + nx;
                            if (bitmap[nidx] > 0 && !visited[nidx])
                            {
                                visited[nidx] = true;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                }

                // ═══ 置信度过滤: 高激活像素占比 (替代平均分, 避免背景噪声拉低均值) ═══
                if (probMap is not null && totalCount > 0)
                {
                    float ratio = (float)highCount / totalCount;
                    if (ratio < highRatio) continue;
                }

                int boxW = maxX - minX, boxH = maxY - minY;
                if (boxW > 3 && boxH > 3 && boxW * boxH > 20)
                {
                    float aspectRatio = (float)boxW / boxH;
                    if (aspectRatio < maxAspect && aspectRatio > 0.02f)
                    {
                        // 不扩展框（rec 预处理已有 padding 补偿边缘裁剪）
                        if (splitAspect > 0f && aspectRatio > splitAspect && boxH >= MinSplitBoxH)
                        {
                            // ═══ 长行按 X 拆分 ═══
                            // 整行送 rec 会被 RecImgMaxW(1280) 挤压到不可读，故切成
                            // AR ≤ splitAspect 的子框分别识别。
                            // ⚠ 切点必须落在【无墨列】（字符/单词间隙），否则会把字符劈成两半；
                            //   找不到无墨列时退回等分。
                            int parts = Math.Min((int)Math.Ceiling(aspectRatio / splitAspect), MaxSplitParts);
                            int spanW = maxX - minX + 1;
                            float step = (float)spanW / parts;

                            // 逐列统计该连通域行范围内是否有前景（无墨列 = 可切）
                            var colInk = new bool[spanW];
                            for (int cx2 = minX; cx2 <= maxX; cx2++)
                                for (int cy2 = minY; cy2 <= maxY; cy2++)
                                    if (bitmap[cy2 * w + cx2] != 0) { colInk[cx2 - minX] = true; break; }

                            var cuts = new int[parts + 1];
                            cuts[0] = minX;
                            cuts[parts] = maxX + 1;
                            int window = Math.Max(1, (int)(step / 2));
                            for (int i = 1; i < parts; i++)
                            {
                                int ideal = minX + (int)(i * step);
                                int cut = ideal;
                                int best = int.MaxValue;
                                for (int d = 0; d <= window; d++)
                                {
                                    foreach (int cand in new[] { ideal - d, ideal + d })
                                    {
                                        if (cand <= minX + 1 || cand >= maxX) continue;
                                        if (colInk[cand - minX]) continue;
                                        int dist = Math.Abs(cand - ideal);
                                        if (dist < best) { best = dist; cut = cand; }
                                    }
                                }
                                cuts[i] = cut;
                            }

                            for (int i = 0; i < parts; i++)
                            {
                                float sx1 = cuts[i], sx2 = cuts[i + 1];
                                if (sx2 - sx1 <= 1) continue;

                                // 子框独立做置信度过滤，避免拆分后的空白子段混入
                                if (probMap is not null &&
                                    !SubBoxConfident(probMap, w, (int)sx1, minY, (int)sx2, maxY))
                                    continue;

                                boxes.Add(new Box(
                                    sx1 * scaleX, minY * scaleY,
                                    sx2 * scaleX, (maxY + 1) * scaleY, IsFragment: true));
                            }
                        }
                        else
                        {
                            boxes.Add(new Box(
                                minX * scaleX, minY * scaleY,
                                (maxX + 1) * scaleX, (maxY + 1) * scaleY));
                        }
                    }
                }
            }
        }

        // 按 Y 坐标排序（从上到下）
        boxes.Sort((a, b) => a.Y1.CompareTo(b.Y1));

        // ═══ 框去重：移除重叠过高的框（防止 unclip 扩展导致重复识别）═══
        // ⚠️ 2026-09-15 修正：原注释写"IoU > 0.3"，而下方实际判据是 iou > 0.15f。
        //    该阈值属启发式参数：调大会漏字（相邻长行子框外扩后略有交叠即被整块丢弃），
        //    调小会保留重复框导致重复识别。
        //    **改动必须以 OCR 准确率基准实测为依据**（见 tools --ocr-acc-bench，
        //    当前 CPU 约 91.5%、DML 约 91.5%），不得凭直觉调整。
        //    此处仅修正注释与代码不一致，刻意不改动数值。
        var deduped = new List<Box>();
        foreach (var box in boxes)
        {
            bool isDuplicate = false;
            foreach (var existing in deduped)
            {
                float ix1 = Math.Max(box.X1, existing.X1);
                float iy1 = Math.Max(box.Y1, existing.Y1);
                float ix2 = Math.Min(box.X2, existing.X2);
                float iy2 = Math.Min(box.Y2, existing.Y2);
                if (ix2 > ix1 && iy2 > iy1)
                {
                    float inter = (ix2 - ix1) * (iy2 - iy1);
                    float areaA = (box.X2 - box.X1) * (box.Y2 - box.Y1);
                    float areaB = (existing.X2 - existing.X1) * (existing.Y2 - existing.Y1);
                    float iou = inter / (areaA + areaB - inter + 1e-6f);
                    if (iou > 0.15f) { isDuplicate = true; break; }
                }
            }
            if (!isDuplicate) deduped.Add(box);
        }

        return deduped;
    }

    /// <summary>是否已释放。置位后新请求直接拒绝，杜绝"门被重新打开后访问已释放 session"。</summary>
    private volatile bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;   // 重复 Dispose 无副作用
        _disposed = true;

        // ═══ 2026-09-13 P0 加固: 释放前等待在途推理完成 ═══
        // 若 Dispose 与推理并发 (应用退出时后台识别仍在跑), 释放 session
        // 会让在途 Run() 访问已释放的原生对象 → 闪退。
        //
        // ═══ 2026-09-15 修复: 不要再 Release 这扇门 ═══
        // 原实现在 Wait 成功之后又调用 Release —— 等于把刚关上的门重新打开：
        // 此前已通过可用性检查、正在 _inferGate 上排队的调用会被放行，
        // 进入 RecognizeCoreAsync 时 _detSession 已是 null ⇒ NullReferenceException，
        // 又被上游 catch 吞成一句"ONNX 异常"，表现为一次莫名其妙的 OCR 失败。
        // 另外原代码在 Wait【超时】时（根本没拿到信号量）也无条件 Release，
        // 会抛 SemaphoreFullException 再被 catch 吞掉 —— 注释写的是
        // "若获取超时则不应释放"，与代码实际行为相反。
        // 正确做法：Dispose 后保持门关闭，并靠 _disposed 直接拦掉新请求。
        try { _inferGate.Wait(TimeSpan.FromSeconds(5)); } catch { /* 等待超时仍继续释放 */ }
        try { _detSession?.Dispose(); } catch { }
        try { _recSession?.Dispose(); } catch { }
        _detSession = null;
        _recSession = null;
        // 注意：此处【故意不调用 _inferGate.Release()】—— 详见上方说明。
    }

    /// <param name="IsFragment">是否为长行拆分的子框（影响 rec 裁剪边距，见 RunRecognition）。</param>
    private record struct Box(float X1, float Y1, float X2, float Y2, bool IsFragment = false);
}

