// TrueToneCap.Core/Services/OnnxOcrEngine.cs
// ONNX Runtime 内嵌 OCR 引擎 — PP-OCRv6 medium, FP16 推理, 纯 C# 零 Python 依赖
//
// 模型来源: PaddleOCR PP-OCRv6_medium → ONNX 导出 (FP32) → FP16 量化
//   检测: PP-OCRv6_medium_det.onnx (FP16, ~30MB, 34.5M 参数, Hmean 86.2%)
//   识别: PP-OCRv6_medium_rec.onnx (FP16, ~37MB, 50 语言统一)
//   字典: ppocrv6_dict.txt (多语言统一, ~15000+ chars)
//   量化工具: scripts/onnx_fp16_quantize.py (onnxconverter-common float16)
//
// 后端: DirectML (GPU, FP16 原生) | CPU (FP16 模型 + FP32 计算)
// 降级: DirectML 不可用 → CPU

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;

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
    float[]? RecStd = null)
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
        DetLimitSide: 736,
        RecImgH: 48,
        RecImgMaxW: 640,
        DetThreshold: 0.3f,
        DetMean: [0.5f, 0.5f, 0.5f],
        DetStd: [0.5f, 0.5f, 0.5f],
        RecMean: [0.5f, 0.5f, 0.5f],
        RecStd: [0.5f, 0.5f, 0.5f]);

    /// <summary>PP-OCRv6 检测模型候选文件名（medium 优先，兼容旧命名）。</summary>
    public static string[] DetFallbackNames =>
    [
        "PP-OCRv6_medium_det.onnx",
        "PP-OCRv6_det.onnx",
        "ch_PP-OCRv6_det_infer.onnx",
        "ch_PP-OCRv6_det_server_infer.onnx",
    ];

    /// <summary>PP-OCRv6 识别模型候选文件名（medium 优先，兼容旧命名）。</summary>
    public static string[] RecFallbackNames =>
    [
        "PP-OCRv6_medium_rec.onnx",
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
    private DenseTensor<float>? _detTensor;
    private int _detTensorW, _detTensorH;
    private DenseTensor<float>? _recTensor;

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
            Debug.WriteLine("[OCR] DirectML 不可用，降级到 CPU");
            _provider = OnnxExecutionProvider.Cpu;
        }

        _available = LoadModels();
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
                Debug.WriteLine($"[模型加载] PP-OCRv6 模型缺失: {_modelDir}");
                Debug.WriteLine($"[模型加载] 期望文件: {OcrModelConfig.DetFallbackNames[0]} + {OcrModelConfig.RecFallbackNames[0]}");
                return false;
            }

            if (dictPath is null)
            {
                Debug.WriteLine($"[模型加载] 字典文件缺失，OCR 将无法解码");
                return false;
            }

            var opts = CreateSessionOptions();

            Debug.WriteLine($"[模型加载] 检测模型: {Path.GetFileName(detPath)}");
            _detSession = new InferenceSession(detPath, opts);

            Debug.WriteLine($"[模型加载] 识别模型: {Path.GetFileName(recPath)}");
            _recSession = new InferenceSession(recPath, opts);

            _dict = File.ReadAllLines(dictPath);
            Debug.WriteLine($"[模型加载] 字典: {Path.GetFileName(dictPath)} ({_dict.Length} 字符)");

            Debug.WriteLine($"[模型加载] PP-OCRv6 medium 就绪 ({_provider}, FP16)");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[模型加载] 失败: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Debug.WriteLine($"[模型加载] 内部异常: {ex.InnerException.Message}");
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
        // 用 ORT_DISABLE_ALL 完全禁用图优化, 牺牲少量性能换取正确结果。
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;

        // ── FP16 优化 ──
        // 启用内存模式加速 FP16 tensor 分配
        opts.EnableMemoryPattern = true;
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
            Debug.WriteLine("[DirectML] 检测成功：DirectML 可用");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DirectML] 不可用：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════
    //  OCR 识别
    // ═══════════════════════════════════════

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_available || _detSession is null || _recSession is null)
            return new OcrResult { Error = "ONNX 引擎不可用" };

        // ═══ P0-2: 识别超时看门狗 (防 DirectML 死锁) ═══
        // DirectML 推理可能卡死不抛异常, 导致 UI 永久卡死。在此加总超时 (默认 60s)。
        const int OcrTimeoutSeconds = 60;
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
            float maxRatio = _provider == OnnxExecutionProvider.Cpu ? 6.0f : 8.0f;
            ratio = Math.Min(ratio, maxRatio);
        }
        else
        {
            ratio = 1.0f;
        }
        int resizeW = Math.Max((int)Math.Round(w * ratio / 32) * 32, 32);
        int resizeH = Math.Max((int)Math.Round(h * ratio / 32) * 32, 32);
        float ratioW = (float)w / resizeW;
        float ratioH = (float)h / resizeH;

        // 池化检测张量 (尺寸变化时重建, 否则复用)
        if (_detTensor is null || _detTensorW != resizeW || _detTensorH != resizeH)
        {
            _detTensor = new DenseTensor<float>([1, 3, resizeH, resizeW]);
            _detTensorW = resizeW;
            _detTensorH = resizeH;
        }
        var input = _detTensor;
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
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        Tensor<float>? output = null;
        try
        {
            using var results = _detSession!.Run(inputs);
            output = results.First().AsTensor<float>();
        }
        catch (Exception ex) when (ex is AccessViolationException || ex.GetType().Name.Contains("SEH"))
        {
            Debug.WriteLine($"[OCR] 检测推理 SEH 崩溃: {ex.GetType().Name} → 返回空框");
            return [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] 检测推理异常: {ex.Message} → 返回空框");
            return [];
        }

        // 后处理: 二值化 + 膨胀 + 置信度过滤
        int oh = output.Dimensions[2], ow = output.Dimensions[3];

        // 保存原始概率图（用于置信度过滤）
        var probMap = new float[oh * ow];
        var bitmap = new byte[oh * ow];
        for (int y2 = 0; y2 < oh; y2++)
            for (int x2 = 0; x2 < ow; x2++)
            {
                float v = output[[0, 0, y2, x2]];
                probMap[y2 * ow + x2] = v;
                bitmap[y2 * ow + x2] = v > threshold ? (byte)255 : (byte)0;
            }

        // ═══ 不使用膨胀 ═══
        // medium 模型概率图质量已足够好，膨胀反而导致大图上相邻行合并
        // rapidocr 的 use_dilation 主要针对 small/mobile 模型的断裂问题
        var dilated = bitmap; // 直接使用二值化结果

        // 缩放回原始坐标（传入概率图用于置信度过滤）
        var boxes = ExtractBoxes(dilated, ow, oh, ratioW, ratioH, probMap);
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
        int padX = Math.Max(2, bw / 10);  // 水平边距 10%
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
        // 池化识别张量 (尺寸固定 recH×recMaxW, 创建一次复用)
        _recTensor ??= new DenseTensor<float>([1, 3, recH, recMaxW]);
        var input = _recTensor;
        // 复用前清零 padding 区 (右侧 dx>=recW 残留旧数据会污染推理)
        input.Buffer.Span.Clear();
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
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        Tensor<float>? output = null;
        try
        {
            using var results = _recSession!.Run(inputs);
            output = results.First().AsTensor<float>();
        }
        catch (Exception ex) when (ex is AccessViolationException || ex.GetType().Name.Contains("SEH"))
        {
            Debug.WriteLine($"[OCR] 识别推理 SEH 崩溃: {ex.GetType().Name} → 丢弃该区域");
            return "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] 识别推理异常: {ex.Message} → 丢弃该区域");
            return "";
        }

        // CTC greedy decode (PP-OCRv6 多语言统一字典)
        // 不做置信度过滤，仅用 greedy argmax + 去重
        int timeSteps = output.Dimensions[1];
        int numClasses = output.Dimensions[2];
        var decoded = new List<int>();
        int lastChar = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            int maxIdx = 0;
            float maxVal = float.MinValue;
            for (int c2 = 0; c2 < numClasses; c2++)
            {
                float v = output[[0, t, c2]];
                if (v > maxVal) { maxVal = v; maxIdx = c2; }
            }
            // 跳过 blank(0) 和重复
            if (maxIdx != lastChar && maxIdx > 0)
                decoded.Add(maxIdx);
            lastChar = maxIdx;
        }

        // 字典映射
        if (_dict is null) return "";
        return string.Concat(decoded
            .Where(d => d - 1 < _dict.Length)
            .Select(d => _dict[d - 1]));
    }

    // ═══════════════════════════════════════
    //  边界框提取（简化连通域分析）
    // ═══════════════════════════════════════

    private static List<Box> ExtractBoxes(byte[] bitmap, int w, int h,
        float scaleX, float scaleY, float[]? probMap = null)
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
                    if (aspectRatio < 50f && aspectRatio > 0.02f)
                    {
                        // 不扩展框（rec 预处理已有 padding 补偿边缘裁剪）
                        boxes.Add(new Box(
                            minX * scaleX, minY * scaleY,
                            (maxX + 1) * scaleX, (maxY + 1) * scaleY));
                    }
                }
            }
        }

        // 按 Y 坐标排序（从上到下）
        boxes.Sort((a, b) => a.Y1.CompareTo(b.Y1));

        // ═══ 框去重：移除 IoU > 0.3 的重叠框（防止 unclip 扩展导致重复识别）═══
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

    public void Dispose()
    {
        _detSession?.Dispose();
        _recSession?.Dispose();
    }

    private record struct Box(float X1, float Y1, float X2, float Y2);
}

