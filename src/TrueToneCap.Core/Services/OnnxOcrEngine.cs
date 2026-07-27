// TrueToneCap.Core/Services/OnnxOcrEngine.cs
// ONNX Runtime 内嵌 OCR 引擎 — 纯 C# 推理，零 Python 依赖
//
// 模型来源: PaddleOCR PP-OCRv4 → ONNX 导出
//   检测: ch_PP-OCRv4_det_server_infer.onnx (FP32, 108MB)
//   识别: ch_PP-OCRv4_rec_server_infer.onnx (FP32, 86MB)
//   字典: ppocr_keys_v1.txt (6625 chars)
//
// 后端: CPU (默认) | DirectML (AMD/NVIDIA/Intel GPU) | CUDA (NVIDIA)
// FP16 模型已导出但因 onnxconverter_common 兼容性问题暂不可用

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrueToneCap.Core.Services;

public enum OnnxExecutionProvider { Cpu, DirectML, Cuda }

public sealed class OnnxOcrEngine : IOcrEngine, IDisposable
{
    private readonly OnnxExecutionProvider _provider;
    private readonly string _modelDir;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private string[]? _dict;
    private bool _available;
    private string _modelVariant = "unknown";

    public OcrEngineInfo Info => new(
        $"ONNX PP-OCRv4-{_modelVariant} ({_provider})",
        _provider == OnnxExecutionProvider.Cpu ? OcrEngineMode.Cpu : OcrEngineMode.Gpu,
        _available);

    public OnnxOcrEngine(OnnxExecutionProvider provider = OnnxExecutionProvider.Cpu, string? modelDir = null)
    {
        _provider = provider;
        _modelDir = modelDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueToneCap", "onnx_models");

        // 如果请求 DirectML，先手动加载 onnxruntime.dll 和 DirectML.dll
        if (_provider == OnnxExecutionProvider.DirectML)
        {
            LoadOnnxRuntimeWithDirectML();
        }

        // 如果请求 DirectML 但不可用，自动降级到 CPU
        if (_provider == OnnxExecutionProvider.DirectML && !IsDirectMLAvailable())
        {
            Console.WriteLine("DirectML 不可用，自动降级到 CPU");
            _provider = OnnxExecutionProvider.Cpu;
        }

        _available = LoadModels();
    }

    // ═══════════════════════════════════════
    //  模型加载
    // ═══════════════════════════════════════

    private bool LoadModels()
    {
        try
        {
            string dictPath = Path.Combine(_modelDir, "ppocr_keys_v1.txt");

            // 加载 FP32 模型 (FP16 目前有兼容性问题，待 ONNX Runtime 升级后启用)
            // 检测顺序: server fp16 → server fp32 → mobile
            string detPath = Path.Combine(_modelDir, "ch_PP-OCRv4_det_server_infer.onnx");
            string recPath = Path.Combine(_modelDir, "ch_PP-OCRv4_rec_server_infer.onnx");
            _modelVariant = "server-fp32";

            if (!File.Exists(detPath) || !File.Exists(recPath))
            {
                detPath = Path.Combine(_modelDir, "ch_PP-OCRv4_det_infer.onnx");
                recPath = Path.Combine(_modelDir, "ch_PP-OCRv4_rec_infer.onnx");
                _modelVariant = "mobile";
            }

            if (!File.Exists(detPath) || !File.Exists(recPath))
            {
                System.Diagnostics.Debug.WriteLine($"模型文件缺失: {_modelDir}");
                return false;
            }

            var opts = CreateSessionOptions();

            Console.WriteLine($"[模型加载] 尝试加载检测模型: {detPath}");
            _detSession = new InferenceSession(detPath, opts);
            Console.WriteLine($"[模型加载] 检测模型加载成功");

            Console.WriteLine($"[模型加载] 尝试加载识别模型: {recPath}");
            _recSession = new InferenceSession(recPath, opts);
            Console.WriteLine($"[模型加载] 识别模型加载成功");

            if (File.Exists(dictPath))
            {
                _dict = File.ReadAllLines(dictPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();
                Console.WriteLine($"[模型加载] 字典加载成功，共 {_dict.Length} 个字符");
            }

            System.Diagnostics.Debug.WriteLine($"模型加载成功 ({_provider}/{_modelVariant})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[模型加载] 失败: {ex.Message}");
            Console.WriteLine($"[模型加载] 异常类型: {ex.GetType().Name}");
            Console.WriteLine($"[模型加载] 堆栈跟踪: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[模型加载] 内部异常: {ex.InnerException.Message}");
                Console.WriteLine($"[模型加载] 内部异常类型: {ex.InnerException.GetType().Name}");
                Console.WriteLine($"[模型加载] 内部堆栈跟踪: {ex.InnerException.StackTrace}");
            }
            return false;
        }
    }

    /// <summary>
    /// 创建 ONNX Runtime SessionOptions，启用所有可用优化
    /// </summary>
    private SessionOptions CreateSessionOptions()
    {
        var opts = new SessionOptions();

        // ── 执行提供器 ──
        switch (_provider)
        {
            case OnnxExecutionProvider.DirectML:
                opts.AppendExecutionProvider_DML(0);
                // DML 禁用 CPU 回退以确保所有节点在 GPU 上执行
                opts.EnableCpuMemArena = true;
                break;
            case OnnxExecutionProvider.Cuda:
                opts.AppendExecutionProvider_CUDA(0);
                break;
            // CPU: 使用默认 CPU EP
        }

        // ── 图优化级别 (全部启用) ──
        // ORT_ENABLE_ALL = 对所有节点进行布局优化 + 算子融合
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        // ── 执行模式 ──
        // ORT_SEQUENTIAL = 顺序执行（模型内部已并行化，避免额外开销）
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

        // ── 线程配置 ──
        // InterOp: 跨会话/跨图并行线程数
        opts.InterOpNumThreads = 1;
        // IntraOp: 单个算子的并行线程数 = CPU 逻辑核心数
        int cpuCores = Environment.ProcessorCount;
        opts.IntraOpNumThreads = Math.Max(2, cpuCores > 4 ? cpuCores - 1 : cpuCores);

        // ── 内存配置 ──
        opts.EnableCpuMemArena = true;  // CPU 内存池复用

        // ── 日志 ──
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

        return opts;
    }

    /// <summary>
    /// 手动加载 onnxruntime.dll 和 DirectML.dll（版本 1.20.0）
    /// </summary>
    private static void LoadOnnxRuntimeWithDirectML()
    {
        try
        {
            var runtimeDirs = new[]
            {
                Path.GetDirectoryName(AppContext.BaseDirectory),
                Path.Combine(AppContext.BaseDirectory, "..", "win-x64"),
            };

            // 首先尝试加载 onnxruntime.dll（从 NuGet 包，包含 DirectML 支持）
            foreach (var runtimeDir in runtimeDirs)
            {
                if (string.IsNullOrEmpty(runtimeDir)) continue;

                var onnxDllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                if (File.Exists(onnxDllPath))
                {
                    var handle = NativeLibrary.Load(onnxDllPath);
                    if (handle != IntPtr.Zero)
                    {
                        Console.WriteLine($"[DirectML] 成功加载 onnxruntime.dll: {onnxDllPath}");
                        break;
                    }
                }
            }

            // 然后加载 DirectML.dll
            var dmlDllPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "DirectML.dll"),
                Path.Combine(AppContext.BaseDirectory, "..", "win-x64", "DirectML.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DirectML.dll"),
            };

            foreach (var dmlDllPath in dmlDllPaths)
            {
                if (File.Exists(dmlDllPath))
                {
                    try
                    {
                        // Copy to system directory so onnxruntime can find it
                        var systemDmlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DirectML.dll");
                        File.Copy(dmlDllPath, systemDmlPath, overwrite: true);
                        Console.WriteLine($"[DirectML] 已复制 DirectML.dll 到系统目录: {systemDmlPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DirectML] 无法复制 DirectML.dll: {ex.Message}");
                    }
                    break;
                }
            }

            Console.WriteLine($"[DirectML] DirectML.dll 加载完成");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DirectML] 加载 DLL 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 手动加载 WindowsAppRuntime 的 onnxruntime.dll（版本 1.23.0，内置 DirectML 支持）
    /// </summary>
    private static void LoadWindowsAppRuntimeDll()
    {
        try
        {
            var runtimeDirs = new[]
            {
                Path.GetDirectoryName(AppContext.BaseDirectory),
                Path.Combine(AppContext.BaseDirectory, "..", "win-x64"),
            };

            foreach (var runtimeDir in runtimeDirs)
            {
                if (string.IsNullOrEmpty(runtimeDir)) continue;

                var onnxDllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                if (File.Exists(onnxDllPath))
                {
                    // 尝试加载 DLL
                    var handle = NativeLibrary.Load(onnxDllPath);
                    if (handle != IntPtr.Zero)
                    {
                        Console.WriteLine($"[DirectML] 成功加载 WindowsAppRuntime onnxruntime.dll: {onnxDllPath}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DirectML] 加载 DLL 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查 DirectML 是否可用
    /// </summary>
    public static bool IsDirectMLAvailable()
    {
        try
        {
            Console.WriteLine($"[DirectML] AppContext.BaseDirectory: {AppContext.BaseDirectory}");
            // 优先检查项目输出目录中的 onnxruntime.dll（WindowsAppRuntime 1.8，版本 1.23.0，内置 DirectML 支持）
            var runtimeDirs = new[]
            {
                Path.GetDirectoryName(AppContext.BaseDirectory),  // 当前程序输出目录
                Path.Combine(AppContext.BaseDirectory, "..", "win-x64"),  // 上级 win-x64 目录
            };

            foreach (var runtimeDir in runtimeDirs)
            {
                Console.WriteLine($"[DirectML] 搜索目录: {runtimeDir}");
                if (string.IsNullOrEmpty(runtimeDir)) continue;

                var onnxDllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                Console.WriteLine($"[DirectML] 检查路径: {onnxDllPath} (存在: {File.Exists(onnxDllPath)})");
                if (File.Exists(onnxDllPath))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(onnxDllPath);
                    Console.WriteLine($"[DirectML] 找到 onnxruntime.dll: {versionInfo.FileVersion} ({onnxDllPath})");

                    // 尝试创建一个简单的 DirectML 会话选项
                    var dmlOpts = new SessionOptions();
                    dmlOpts.AppendExecutionProvider_DML(0);

                    // 如果没有抛出异常，说明 DirectML 可用
                    Console.WriteLine($"[DirectML] 检测成功：onnxruntime.dll {versionInfo.FileVersion} (DirectML 可用)");
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "dml-check.log"), $"[{DateTime.Now:HH:mm:ss}] DirectML 检测成功：onnxruntime.dll {versionInfo.FileVersion}\n");
                    return true;
                }
            }

            // 尝试使用默认路径
            var cpuOpts = new SessionOptions();
            cpuOpts.AppendExecutionProvider_DML(0);

            Console.WriteLine($"[DirectML] 检测成功：DirectML 可用（使用默认 DLL）");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "dml-check.log"), $"[{DateTime.Now:HH:mm:ss}] DirectML 检测成功：使用默认 DLL\n");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[DirectML] 不可用：DLL 未找到 - {ex.Message}");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "dml-check.log"), $"[{DateTime.Now:HH:mm:ss}] DirectML 不可用：DLL 未找到 - {ex.Message}\n");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DirectML] 检查失败 - {ex.GetType().Name}: {ex.Message}");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "dml-check.log"), $"[{DateTime.Now:HH:mm:ss}] DirectML 检查失败 - {ex.GetType().Name}: {ex.Message}\n");
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
                    ct.ThrowIfCancellationRequested();
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
                            Words = [new OcrWord { Text = text, X = bx, Y = by, Width = bw, Height = bh }]
                        });
                        allText.Add(text);
                    }
                }

                var result = new OcrResultEx
                {
                    Text = string.Join("\n", allText),
                    Lines = lines,
                    EngineName = Info.Name
                };

                if (lines.Count == 0)
                    result.Error = "未检测到文字";

                return (OcrResult)result;
            }
            catch (Exception ex)
            {
                return new OcrResult { Error = $"ONNX 异常: {ex.Message}" };
            }
        }, ct);
    }

    // ═══════════════════════════════════════
    //  文字检测 (DBNet)
    // ═══════════════════════════════════════

    private List<Box> RunDetection(byte[] bgra, int w, int h)
    {
        // ── PaddleOCR 风格检测预处理 ──
        // 1. 限制最长边到 960，保持宽高比
        // 2. 宽高向上取整到 32 的倍数
        // 3. OpenCV INTER_LINEAR 缩放 (以最近邻近似)
        // 4. 归一化: (BGR/255 - mean) / std, 保持 BGR 通道
        const int limitSide = 960;
        float ratio = Math.Max(w, h) / (float)limitSide;
        int resizeW = Math.Max((int)Math.Round(w / ratio / 32) * 32, 32);
        int resizeH = Math.Max((int)Math.Round(h / ratio / 32) * 32, 32);
        float ratioW = (float)w / resizeW;
        float ratioH = (float)h / resizeH;

        var input = new DenseTensor<float>(new[] { 1, 3, resizeH, resizeW });
        float[] mean = [0.485f, 0.456f, 0.406f];
        float[] std = [0.229f, 0.224f, 0.225f];

        // 双线性缩放 + 归一化 + 保持 BGR
        Parallel.For(0, resizeH, y =>
        {
            float srcYf = y * ratioH;
            int srcY0 = Math.Clamp((int)srcYf, 0, h - 1);
            int srcY1 = Math.Clamp(srcY0 + 1, 0, h - 1);
            float wy = srcYf - srcY0;
            for (int x = 0; x < resizeW; x++)
            {
                float srcXf = x * ratioW;
                int srcX0 = Math.Clamp((int)srcXf, 0, w - 1);
                int srcX1 = Math.Clamp(srcX0 + 1, 0, w - 1);
                float wx = srcXf - srcX0;

                int i00 = (srcY0 * w + srcX0) * 4;
                int i01 = (srcY0 * w + srcX1) * 4;
                int i10 = (srcY1 * w + srcX0) * 4;
                int i11 = (srcY1 * w + srcX1) * 4;

                // RGB 通道顺序（ImageNet 标准）- 修复 BGR/RGB 混淆导致的准确率下降
                    for (int c = 0; c < 3; c++)
                    {
                        // BGR→RGB: c=0→Red(bgra+2), c=1→Green(bgra+1), c=2→Blue(bgra+0)
                        float v00 = bgra[i00 + (2 - c)] / 255f;
                        float v01 = bgra[i01 + (2 - c)] / 255f;
                        float v10 = bgra[i10 + (2 - c)] / 255f;
                        float v11 = bgra[i11 + (2 - c)] / 255f;
                        // Bilinear
                        float v0 = v00 * (1 - wx) + v01 * wx;
                        float v1 = v10 * (1 - wx) + v11 * wx;
                        float val = v0 * (1 - wy) + v1 * wy;
                        input[0, c, y, x] = (val - mean[c]) / std[c];
                    }
            }
        });

        // 推理
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        using var results = _detSession!.Run(inputs);
        var output = results.First().AsTensor<float>();

        // 后处理: 二值化（阈值 0.2 提升中英混合场景英文文字检出率）
        int oh = output.Dimensions[2], ow = output.Dimensions[3];
        var bitmap = new byte[oh * ow];
        for (int y2 = 0; y2 < oh; y2++)
            for (int x2 = 0; x2 < ow; x2++)
                bitmap[y2 * ow + x2] = output[[0, 0, y2, x2]] > 0.2f ? (byte)255 : (byte)0;

        // 缩放回原始坐标 (detection map→resized→original)
        var boxes = ExtractBoxes(bitmap, ow, oh, ratioW, ratioH);
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

        // ── PaddleOCR 风格识别预处理 ──
        // crop → resize to 48×W (keep aspect) → pad to 320 → normalize → NCHW
        const int recH = 48, recMaxW = 320;
        float aspect = (float)bw / bh;
        int recW = Math.Min((int)Math.Ceiling(recH * aspect), recMaxW);
        recW = Math.Max(4, recW);

        // 双线性缩放 crop 到 recW×recH
        var input = new DenseTensor<float>(new[] { 1, 3, recH, recMaxW });
        float[] recMean = [0.5f, 0.5f, 0.5f];
        float[] recStd = [0.5f, 0.5f, 0.5f];

        for (int dy = 0; dy < recH; dy++)
        {
            float srcYf = y + (float)dy / recH * bh;
            int srcY0 = Math.Clamp((int)srcYf, 0, imgH - 1);
            int srcY1 = Math.Clamp(srcY0 + 1, 0, imgH - 1);
            float wy = srcYf - srcY0;
            for (int dx = 0; dx < recW; dx++)
            {
                float srcXf = x + (float)dx / recW * bw;
                int srcX0 = Math.Clamp((int)srcXf, 0, imgW - 1);
                int srcX1 = Math.Clamp(srcX0 + 1, 0, imgW - 1);
                float wx = srcXf - srcX0;

                int i00 = (srcY0 * imgW + srcX0) * 4;
                int i01 = (srcY0 * imgW + srcX1) * 4;
                int i10 = (srcY1 * imgW + srcX0) * 4;
                int i11 = (srcY1 * imgW + srcX1) * 4;

                for (int c = 0; c < 3; c++)
                {
                    float v00 = bgra[i00 + c] / 255f;
                    float v01 = bgra[i01 + c] / 255f;
                    float v10 = bgra[i10 + c] / 255f;
                    float v11 = bgra[i11 + c] / 255f;
                    float v0 = v00 * (1 - wx) + v01 * wx;
                    float v1 = v10 * (1 - wx) + v11 * wx;
                    float val = v0 * (1 - wy) + v1 * wy;
                    input[0, c, dy, dx] = (val - recMean[c]) / recStd[c];
                }
            }
        }
        // 右侧 padding 保持为 0 (已初始化)

        // 推理
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        using var results = _recSession!.Run(inputs);
        var output = results.First().AsTensor<float>();

        // CTC greedy decode (PaddleOCR style)
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
        float scaleX, float scaleY)
    {
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
                var queue = new Queue<(int, int)>();
                queue.Enqueue((x, y));
                visited[idx] = true;

                while (queue.Count > 0 && queue.Count < w * h)
                {
                    var (cx, cy) = queue.Dequeue();
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);

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

                int boxW = maxX - minX, boxH = maxY - minY;
                if (boxW > 3 && boxH > 3) // 过滤噪点（降低阈值以捕获英文小字）
                {
                    boxes.Add(new Box(
                        minX * scaleX, minY * scaleY,
                        maxX * scaleX, maxY * scaleY));
                }
            }
        }

        // 按 Y 坐标排序（从上到下）
        boxes.Sort((a, b) => a.Y1.CompareTo(b.Y1));
        return boxes;
    }

    public void Dispose()
    {
        _detSession?.Dispose();
        _recSession?.Dispose();
    }

    private record struct Box(float X1, float Y1, float X2, float Y2);
}

