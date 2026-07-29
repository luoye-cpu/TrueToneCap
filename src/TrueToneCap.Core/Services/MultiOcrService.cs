// TrueToneCap.Core/Services/MultiOcrService.cs
// 多引擎 OCR 路由器 — 手动选择单一引擎，无自动降级
// 用户明确选择 ONNX GPU / ONNX CPU / Windows OCR 之一
// 切换引擎自动切换对应的可选语言列表

using System.Diagnostics;

namespace TrueToneCap.Core.Services;

/// <summary>OCR 引擎管理器 — 手动选择，无自动降级。</summary>
public static class MultiOcrService
{
    private static readonly List<IOcrEngine> _engines = [];
    private static bool _initialized;
    private static string _modelDir = "";

    /// <summary>当前选中的引擎类型（null = 未选择）。</summary>
    public static OcrEngineType? SelectedEngineType { get; set; }

    /// <summary>所有已注册的引擎。</summary>
    public static IReadOnlyList<IOcrEngine> Engines => _engines;

    /// <summary>获取当前选中的引擎实例。</summary>
    public static IOcrEngine? SelectedEngine
    {
        get
        {
            if (SelectedEngineType is null) return null;
            return _engines.FirstOrDefault(e => e.Info.EngineType == SelectedEngineType.Value);
        }
    }

    /// <summary>获取当前选中引擎支持的语言列表。</summary>
    public static OcrLanguage[] GetSupportedLanguages()
    {
        if (SelectedEngineType is null) return [];
        return OcrLanguages.GetLanguagesForEngine(SelectedEngineType.Value);
    }

    /// <summary>获取当前选中引擎的默认语言 ID。</summary>
    public static string GetDefaultLanguage()
    {
        if (SelectedEngineType is null) return "ch";
        return OcrLanguages.GetDefaultLanguage(SelectedEngineType.Value);
    }

    /// <summary>初始化所有可用引擎（不自动选择，需用户手动设置 SelectedEngineType）。</summary>
    public static void Initialize(string? modelDir = null)
    {
        if (_initialized) return;
        _initialized = true;
        _modelDir = modelDir ?? "";

        Debug.WriteLine($"[OCR] 初始化引擎，模型目录: {_modelDir}");

        // 1️⃣ ONNX PP-OCRv6 DirectML (GPU, FP16)
        try
        {
            var gpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir);
            if (gpuEngine.Info.IsAvailable)
            {
                _engines.Add(gpuEngine);
                Debug.WriteLine("[OCR] DirectML GPU 引擎就绪");
            }
            else
            {
                Debug.WriteLine("[OCR] DirectML GPU 引擎不可用");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[OCR] DirectML 初始化异常: {ex.Message}"); }

        // 2️⃣ ONNX PP-OCRv6 CPU (FP16 模型, FP32 计算)
        try
        {
            var cpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir);
            if (cpuEngine.Info.IsAvailable)
            {
                _engines.Add(cpuEngine);
                Debug.WriteLine("[OCR] CPU 引擎就绪");
            }
            else
            {
                Debug.WriteLine("[OCR] CPU 引擎不可用");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[OCR] CPU 初始化异常: {ex.Message}"); }

        // 3️⃣ Windows OCR (系统 OCR)
        _engines.Add(new WindowsOcrEngine());
        Debug.WriteLine("[OCR] Windows OCR 引擎已注册");

        Debug.WriteLine($"[OCR] 共 {_engines.Count} 个引擎: {string.Join(", ", _engines.Select(e => e.Info.Name))}");

        // 默认选中第一个可用引擎
        if (SelectedEngineType is null && _engines.Count > 0)
        {
            SelectedEngineType = _engines[0].Info.EngineType;
            Debug.WriteLine($"[OCR] 默认选中引擎: {_engines[0].Info.Name}");
        }
    }

    /// <summary>
    /// 使用当前选中的引擎执行 OCR 识别。
    /// 无自动降级 — 如果选中引擎不可用，直接返回错误。
    /// </summary>
    public static async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_initialized) Initialize();

        var engine = SelectedEngine;
        if (engine is null)
            return new OcrResult { Error = "未选择 OCR 引擎" };

        ct.ThrowIfCancellationRequested();
        try
        {
            Debug.WriteLine($"[OCR] 使用引擎: {engine.Info.Name}, 语言: {lang ?? "default"}");
            var result = await engine.RecognizeAsync(bgra, w, h, lang, ct);
            if (string.IsNullOrEmpty(result.Error) && !string.IsNullOrWhiteSpace(result.Text))
            {
                Debug.WriteLine($"[OCR] 成功! 引擎={engine.Info.Name}, 文本长度={result.Text.Length}");
                return result;
            }
            Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name}: 无结果 (error={result.Error ?? "null"})");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name} 异常: {ex.Message}");
            return new OcrResult { Error = $"OCR 识别失败: {ex.Message}" };
        }
    }

    /// <summary>根据引擎类型查找引擎实例。</summary>
    public static IOcrEngine? FindEngine(OcrEngineType type)
    {
        return _engines.FirstOrDefault(e => e.Info.EngineType == type);
    }

    /// <summary>检查指定引擎类型是否可用。</summary>
    public static bool IsEngineAvailable(OcrEngineType type)
    {
        return _engines.Any(e => e.Info.EngineType == type && e.Info.IsAvailable);
    }
}

/// <summary>Windows OCR 引擎。</summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    public OcrEngineInfo Info => new(
        "Windows OCR (系统)",
        OcrEngineMode.Cpu,
        true,
        OcrEngineType.WindowsOcr,
        Version: "System");

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
        => await OcrService.ExtractTextAsync(bgra, w, h, lang, ct);
}
