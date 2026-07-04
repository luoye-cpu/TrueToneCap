// TrueToneCap.Core/Services/MultiOcrService.cs
// 多引擎 OCR 路由器（纯内嵌，零外部依赖）
// 默认优先级: ONNX DirectML(GPU) → Windows OCR → ONNX CPU
// 可通过 ForceEngine 手动指定单个引擎

using System.Diagnostics;

namespace TrueToneCap.Core.Services;

public static class MultiOcrService
{
    private static readonly List<IOcrEngine> _engines = [];
    private static bool _initialized;
    private static string _modelDir;

    /// <summary>手动指定的引擎名称（null = 自动降级）。</summary>
    public static string? ForceEngine { get; set; }

    public static IReadOnlyList<IOcrEngine> Engines => _engines;

    /// <summary>根据引擎名称查找（null = 返回 null）。</summary>
    public static IOcrEngine? FindEngine(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return _engines.FirstOrDefault(e =>
            e.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public static void Initialize(string? modelDir = null)
    {
        if (_initialized) return;
        _initialized = true;
        _modelDir = modelDir;

        // 1️⃣ ONNX DirectML Server (GPU, 高精度)
        try { TryAdd(() => new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir), "DirectML"); } catch (Exception ex) { Debug.WriteLine($"[OCR] DirectML 初始化异常: {ex.Message}"); }
        // 2️⃣ ONNX CPU Server (CPU, 高精度)
        try { TryAdd(() => new OnnxOcrEngine(OnnxExecutionProvider.Cpu, modelDir), "CPU"); } catch (Exception ex) { Debug.WriteLine($"[OCR] CPU 初始化异常: {ex.Message}"); }
        // 3️⃣ Windows OCR (兜底)
        _engines.Add(new WindowsOcrEngine());
        Debug.WriteLine("[OCR] Windows OCR 已就绪");

        Debug.WriteLine($"[OCR] 共 {_engines.Count} 个可用引擎: {string.Join(", ", _engines.Select(e => e.Info.Name))}");
    }

    private static void TryAdd(Func<IOcrEngine> factory, string label)
    {
        Debug.WriteLine($"[OCR] 初始化 {label} 引擎...");
        var engine = factory();
        if (engine.Info.IsAvailable)
        {
            _engines.Add(engine);
            Debug.WriteLine($"[OCR] {label} 引擎就绪: {engine.Info.Name}");
        }
        else
        {
            Debug.WriteLine($"[OCR] {label} 引擎不可用");
        }
    }

    /// <summary>
    /// 按优先级获取降级引擎列表:
    /// 1) 手动指定 → 2) GPU → 3) Windows → 4) CPU
    /// </summary>
    private static List<IOcrEngine> GetPriorityEngines()
    {
        // 手动指定单个引擎
        if (!string.IsNullOrEmpty(ForceEngine))
        {
            var forced = FindEngine(ForceEngine);
            if (forced is not null) return [forced];
        }

        // 默认降级链: GPU → Windows → CPU
        var ordered = new List<IOcrEngine>();
        AddIfAvailable(ordered, "ONNX PP-OCRv4-server (DirectML)");   // GPU
        AddIfAvailable(ordered, "Windows OCR");                        // Windows
        AddIfAvailable(ordered, "ONNX PP-OCRv4-server (Cpu)");        // CPU
        // 补齐未匹配的引擎
        foreach (var eng in _engines)
            if (!ordered.Contains(eng)) ordered.Add(eng);
        return ordered;
    }

    private static void AddIfAvailable(List<IOcrEngine> list, string nameContains)
    {
        var eng = FindEngine(nameContains);
        if (eng is not null && eng.Info.IsAvailable) list.Add(eng);
    }

    public static async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_initialized) Initialize();
        var engines = GetPriorityEngines();
        Debug.WriteLine($"[OCR] RecognizeAsync: {w}x{h}, {engines.Count} 个引擎待尝试");
        foreach (var engine in engines)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Debug.WriteLine($"[OCR] 尝试引擎: {engine.Info.Name}");
                var result = await engine.RecognizeAsync(bgra, w, h, lang, ct);
                if (string.IsNullOrEmpty(result.Error) && !string.IsNullOrWhiteSpace(result.Text))
                {
                    Debug.WriteLine($"[OCR] 成功! 引擎={engine.Info.Name}, 文本长度={result.Text.Length}");
                    if (result is OcrResultEx ext) ext.EngineName = engine.Info.Name;
                    return result;
                }
                Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name}: 无结果 (error={result.Error ?? "null"}, textLen={result.Text?.Length ?? 0})");
            }
            catch (Exception ex) { Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name} 异常: {ex.Message}"); }
        }
        Debug.WriteLine("[OCR] 所有引擎均失败");
        return new OcrResult { Error = "所有 OCR 引擎均失败" };
    }
}

internal sealed class WindowsOcrEngine : IOcrEngine
{
    public OcrEngineInfo Info => new("Windows OCR", OcrEngineMode.Cpu, true);
    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
        => await OcrService.ExtractTextAsync(bgra, w, h, lang, ct);
}
