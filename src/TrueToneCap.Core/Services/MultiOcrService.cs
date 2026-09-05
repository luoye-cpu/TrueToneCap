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

    /// <summary>OCR 识别总超时（秒）。防止 DirectML/ONNX 卡死时 UI 永久等待。
    /// 超过后返回超时错误（用户可见），由 fallback 逻辑尝试 Windows OCR。</summary>
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 使用当前选中的引擎执行 OCR 识别。
    /// ═══ 2026-08-25 优化: ONNX 引擎空结果时自动 fallback 到 Windows OCR ═══
    /// 背景: PP-OCRv6 统一字典对韩文覆盖极少 (0%), 韩文学符全被误识别。
    /// Windows OCR (系统托管) 原生支持韩文/其他 Windows 语言包。
    /// 触发条件: 选中 ONNX 引擎 + 结果为空/全空白 + Windows OCR 可用。
    /// ═══ 2026-09-04 加固: 总超时看门狗 — ONNX 推理若死锁 (不抛异常)，
    ///    await 会永久挂起导致 UI 卡死；包一层 Task.WhenAny 强制超时返回。
    /// </summary>
    public static async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_initialized) Initialize();

        var engine = SelectedEngine;
        if (engine is null)
            return new OcrResult { Error = "未选择 OCR 引擎" };

        ct.ThrowIfCancellationRequested();

        // ═══ 总超时看门狗: 整个识别(含 fallback)不得超过 OcrTimeout ═══
        // 用 TaskCompletionSource 在超时或取消时短路，避免 await 永久挂起
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OcrTimeout);
        var timeoutCt = timeoutCts.Token;

        try
        {
            Debug.WriteLine($"[OCR] 使用引擎: {engine.Info.Name}, 语言: {lang ?? "default"}");
            var result = await RunWithTimeout(engine, bgra, w, h, lang, timeoutCt);
            if (string.IsNullOrEmpty(result.Error) && !string.IsNullOrWhiteSpace(result.Text))
            {
                Debug.WriteLine($"[OCR] 成功! 引擎={engine.Info.Name}, 文本长度={result.Text.Length}");
                return result;
            }

            // ═══ fallback: ONNX 无结果 → Windows OCR (韩文等字典外语言) ═══
            // ⚠ R-2 修复: 必须转换语言标签 — ONNX 用短标签 ("ch"/"ko"),
            //   Windows OCR 需要 BCP-47 ("zh-Hans-CN"/"ko-KR")。直接传会 TryCreateFromLanguage
            //   失败 → fallback 形同虚设。
            if (engine.Info.EngineType is OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu)
            {
                var winOcr = FindEngine(OcrEngineType.WindowsOcr);
                if (winOcr is not null && winOcr.Info.IsAvailable)
                {
                    Debug.WriteLine($"[OCR] ONNX 无结果 → fallback Windows OCR");
                    var fallback = await RunWithTimeout(winOcr, bgra, w, h, MapLangToBcp47(lang), timeoutCt);
                    if (!string.IsNullOrWhiteSpace(fallback.Text))
                    {
                        fallback.FallbackFrom = engine.Info.EngineType.ToString();
                        Debug.WriteLine($"[OCR] ✅ Windows OCR 兜底成功: {fallback.Text.Length} 字");
                        return fallback;
                    }
                }
            }

            Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name}: 无结果 (error={result.Error ?? "null"})");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (timeoutCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 超时触发 (非用户取消) → 返回明确错误，UI 可见
                Debug.WriteLine($"[OCR] ⚠ OCR 识别超时 ({OcrTimeout.TotalSeconds:F0}s)，返回超时错误");
                return new OcrResult { Error = $"OCR 识别超时（>{OcrTimeout.TotalSeconds:F0} 秒），可能为 DirectML 卡死" };
            }
            return new OcrResult { Error = "OCR 识别已取消" };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] 引擎 {engine.Info.Name} 异常: {ex.Message}");

            // ═══ fallback: 引擎异常也尝试 Windows OCR ═══
            if (engine.Info.EngineType is OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu)
            {
                var winOcr = FindEngine(OcrEngineType.WindowsOcr);
                if (winOcr is not null && winOcr.Info.IsAvailable)
                {
                    try
                    {
                        Debug.WriteLine($"[OCR] ONNX 异常 → fallback Windows OCR");
                        var fallback = await RunWithTimeout(winOcr, bgra, w, h, MapLangToBcp47(lang), timeoutCt);
                        if (!string.IsNullOrWhiteSpace(fallback.Text))
                        {
                            fallback.FallbackFrom = engine.Info.EngineType.ToString();
                            return fallback;
                        }
                    }
                    catch { /* Windows OCR 也失败则返回原始错误 */ }
                }
            }
            return new OcrResult { Error = $"OCR 识别失败: {ex.Message}" };
        }
    }

    /// <summary>带超时的引擎调用：超过 OcrTimeout 或外部取消时短路返回超时错误。</summary>
    private static async Task<OcrResult> RunWithTimeout(IOcrEngine engine, byte[] bgra, int w, int h,
        string? lang, CancellationToken timeoutCt)
    {
        var task = engine.RecognizeAsync(bgra, w, h, lang, timeoutCt);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeoutCt));
        if (completed != task)
            return new OcrResult { Error = "OCR 引擎超时" }; // 超时短路，主流程捕获
        return await task;
    }

    /// <summary>根据引擎类型查找引擎实例。</summary>
    public static IOcrEngine? FindEngine(OcrEngineType type)
    {
        return _engines.FirstOrDefault(e => e.Info.EngineType == type);
    }

    /// <summary>
    /// ═══ R-2 修复: ONNX 短语言标签 → Windows OCR BCP-47 标签映射 ═══
    /// ONNX (PP-OCR) 用 "ch"/"en"/"korean" 等短标签; Windows OCR 的
    /// OcrEngine.TryCreateFromLanguage 需要 BCP-47 ("zh-Hans-CN" 等)。
    /// 无法映射时返回 null → OcrService 走默认语言逻辑 (zh-Hans-CN → 用户配置)。
    /// </summary>
    internal static string? MapLangToBcp47(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return lang.Trim().ToLowerInvariant() switch
        {
            // 中文
            "ch" or "zh" or "chinese" or "chs" => "zh-Hans-CN",
            "cht" or "zh-tw" or "zh-hant" => "zh-Hant-TW",
            // 英文
            "en" or "english" => "en-US",
            // 日文
            "jp" or "japan" or "japanese" => "ja-JP",
            // 韩文
            "korean" or "kor" => "ko-KR",
            // 法/德/西/俄/葡/意
            "french" or "fr" or "fra" => "fr-FR",
            "german" or "de" or "deu" => "de-DE",
            "spanish" or "es" or "spa" => "es-ES",
            "russian" or "ru" or "rus" => "ru-RU",
            "portuguese" or "pt" => "pt-BR",
            "italian" or "it" or "ita" => "it-IT",
            // 已是 BCP-47 格式 (含 '-') → 原样返回
            var s when s.Contains('-') => lang,
            // 未知短标签 → null 走默认
            _ => null,
        };
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
