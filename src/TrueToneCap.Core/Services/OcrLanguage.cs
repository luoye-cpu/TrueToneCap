// TrueToneCap.Core/Services/OcrLanguage.cs
// OCR 语言定义 — 不同引擎支持不同的语言集合

namespace TrueToneCap.Core.Services;

/// <summary>OCR 语言定义。</summary>
public sealed record OcrLanguage(
    string Id,          // 唯一标识，如 "zh", "en", "ja"
    string DisplayName, // 显示名称，如 "中文", "English"
    string[] SupportedEngines // 支持此语言的引擎类型名称
);

/// <summary>OCR 语言注册表 — 集中管理所有引擎支持的语言。</summary>
public static class OcrLanguages
{
    /// <summary>PP-OCRv6 统一模型支持的语言（50 语言统一字典）。</summary>
    public static readonly OcrLanguage[] OnnxLanguages =
    [
        new("zh-en", "中英混合", ["OnnxGpu", "OnnxCpu"]),
        new("ch", "中文 (简体)", ["OnnxGpu", "OnnxCpu"]),
        new("en", "English", ["OnnxGpu", "OnnxCpu"]),
        new("ja", "日本語", ["OnnxGpu", "OnnxCpu"]),
        new("ko", "한국어", ["OnnxGpu", "OnnxCpu"]),
        new("fr", "Français", ["OnnxGpu", "OnnxCpu"]),
        new("de", "Deutsch", ["OnnxGpu", "OnnxCpu"]),
        new("es", "Español", ["OnnxGpu", "OnnxCpu"]),
        new("pt", "Português", ["OnnxGpu", "OnnxCpu"]),
        new("ru", "Русский", ["OnnxGpu", "OnnxCpu"]),
        new("ar", "العربية", ["OnnxGpu", "OnnxCpu"]),
        new("it", "Italiano", ["OnnxGpu", "OnnxCpu"]),
        new("nl", "Nederlands", ["OnnxGpu", "OnnxCpu"]),
        new("pl", "Polski", ["OnnxGpu", "OnnxCpu"]),
        new("th", "ไทย", ["OnnxGpu", "OnnxCpu"]),
        new("vi", "Tiếng Việt", ["OnnxGpu", "OnnxCpu"]),
    ];

    /// <summary>Windows OCR 支持的语言（需系统安装语言包）。</summary>
    public static readonly OcrLanguage[] WindowsOcrLanguages =
    [
        new("zh-Hans-CN", "中文 (简体)", ["WindowsOcr"]),
        new("zh-Hant-TW", "中文 (繁體)", ["WindowsOcr"]),
        new("en-US", "English (US)", ["WindowsOcr"]),
        new("en-GB", "English (UK)", ["WindowsOcr"]),
        new("ja-JP", "日本語", ["WindowsOcr"]),
        new("ko-KR", "한국어", ["WindowsOcr"]),
        new("fr-FR", "Français", ["WindowsOcr"]),
        new("de-DE", "Deutsch", ["WindowsOcr"]),
        new("es-ES", "Español", ["WindowsOcr"]),
        new("pt-BR", "Português (Brasil)", ["WindowsOcr"]),
        new("ru-RU", "Русский", ["WindowsOcr"]),
        new("it-IT", "Italiano", ["WindowsOcr"]),
        new("nl-NL", "Nederlands", ["WindowsOcr"]),
        new("pl-PL", "Polski", ["WindowsOcr"]),
        new("th-TH", "ไทย", ["WindowsOcr"]),
        new("vi-VN", "Tiếng Việt", ["WindowsOcr"]),
    ];

    /// <summary>获取指定引擎支持的语言列表。</summary>
    public static OcrLanguage[] GetLanguagesForEngine(OcrEngineType engineType) => engineType switch
    {
        OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu => OnnxLanguages,
        OcrEngineType.WindowsOcr => WindowsOcrLanguages,
        _ => OnnxLanguages,
    };

    /// <summary>获取默认语言 ID。</summary>
    public static string GetDefaultLanguage(OcrEngineType engineType) => engineType switch
    {
        OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu => "zh-en",
        OcrEngineType.WindowsOcr => "zh-Hans-CN",
        _ => "zh-en",
    };
}