// TrueToneCap.App/Models/AppSettingsData.cs
// 应用设置数据模型 — 独立文件，支持 JSON 序列化（AOT 兼容）
// 所有属性必须有合理的默认值，新属性必须在此处添加默认值

using System.Text.Json.Serialization;

namespace TrueToneCap.App.Models;

/// <summary>应用设置数据模型。</summary>
public sealed class AppSettingsData
{
    // ── 版本（用于设置迁移，每次不兼容改动时递增）──
    public int SettingsVersion { get; set; } = 1;

    // ── 输出格式 (0=PNG, 1=GainMap, 2=JPEG LI, 3=JPEG XL, 4=AVIF, 5=WebP, 6=TIFF) ──
    public int FormatIndex { get; set; }

    // ── 质量 ──
    // 兼容字段：当前格式的质量（保留用于旧配置文件/其他读取点）。
    // 各格式独立质量见 QualityPng/QualityGainMap 等，切换格式时不再互相覆盖。
    public double Quality { get; set; } = 80;

    // ── 每格式独立质量（GetQualityRange 区间内）──
    public double QualityPng { get; set; } = 100;          // 无损 (0-100)
    public double QualityGainMap { get; set; } = 1.0;      // butteraugli 距离 (0.5-3.0)
    public double QualityJpegLi { get; set; } = 1.0;       // butteraugli 距离 (0.5-3.0)
    public double QualityJpegXl { get; set; } = 0.8;       // butteraugli 距离 (0.1-4.0)
    public double QualityAvif { get; set; } = 18;           // CRF (0-63)
    public double QualityWebp { get; set; } = 92;           // 质量 (50-100)
    public double QualityTiff { get; set; } = 100;          // 无损 (0-100)

    // ── 输出路径 ──
    public string OutputPath { get; set; } = "";
    public string FileNamePrefix { get; set; } = "TrueToneCap_";

    // ── 色彩 (0=System, 1=sRGB, 2=DisplayP3, 3=DCI_P3, 4=AdobeRGB, 5=BT.2020) ──
    public int ColorSpaceIndex { get; set; }

    // ── HDR / ICC ──
    public bool HdrEnabled { get; set; } = true;
    public bool IccBakeEnabled { get; set; }

    // ── 热键 ──
    public string Hotkey { get; set; } = "Ctrl+Shift+S";
    public string RecordHotkey { get; set; } = "Ctrl+Shift+G";
    public string SilentHotkey { get; set; } = "Ctrl+Shift+Q";

    // ── 行为 ──
    public bool AutoStart { get; set; }
    public bool ShowPreview { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    // ── AVIF ──
    public bool AvifPngSuffix { get; set; }
    public int AvifBackendIndex { get; set; } // 0=Auto, 1=LibAom, 2=Qsv, 3=Nvenc
    public string AvifChroma { get; set; } = "444";

    // ── 录制 ──
    public double RecordQuality { get; set; } = 80;
    public int AnimAvifBackendIndex { get; set; }

    // ── 归档 ──
    public bool ArchiveEnabled { get; set; }
    public string ArchiveMode { get; set; } = "Month";

    // ── 每格式位深 ──
    public int BitDepthPng { get; set; } = 8;
    public int BitDepthJpegLi { get; set; } = 8;
    public int BitDepthJpegXl { get; set; } = 10;
    public int BitDepthAvif { get; set; } = 10;
    public int BitDepthWebP { get; set; } = 8;
    public int BitDepthTiff { get; set; } = 8;
    public int BitDepthGainMap { get; set; } = 8;

    // ── 每格式色度采样 ──
    public string ChromaPng { get; set; } = "444";
    public string ChromaJpegLi { get; set; } = "420";
    public string ChromaJpegXl { get; set; } = "444";
    public string ChromaAvif { get; set; } = "444";
    public string ChromaWebP { get; set; } = "420";
    public string ChromaTiff { get; set; } = "444";
    public string ChromaGainMap { get; set; } = "420";

    // ── 翻译/LLM ──
    public bool UseCustomLlm { get; set; }
    public string TranslationMode { get; set; } = "Free";
    public string LlmEndpoint { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "deepseek-chat";
    public string LlmSystemPrompt { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string OcrLanguage { get; set; } = "";

    // ── 系统检测（运行时填充，不持久化到用户设置文件）──
    [JsonIgnore]
    public bool AcmeDetected { get; set; }
    [JsonIgnore]
    public bool NvencAvailable { get; set; }
    [JsonIgnore]
    public bool QsvAvailable { get; set; }
    [JsonIgnore]
    public int DisplayBitDepth { get; set; } = 8;
    [JsonIgnore]
    public int OutputBitDepth { get; set; } = 8;
    /// <summary>系统实际 SDR 白点亮度 (nits)，从 DISPLAYCONFIG_SDR_WHITE_LEVEL 读取。
    /// 0 表示未检测到，使用用户 PaperWhiteNits。</summary>
    [JsonIgnore]
    public int SystemSdrWhiteLevel { get; set; }

    // ── 首次运行标记 ──
    // ⚠ 必须持久化（不能 JsonIgnore）：用于"仅首次运行"时应用系统检测默认值。
    // 若忽略该字段，则每次启动 FirstRun 都为 true，
    // 导致 DetectAndApplySystemCapabilitiesAsync 每次都覆盖用户已保存的 HDR/ICC/色域设置。
    public bool FirstRun { get; set; } = true;

    // ── 色调映射 ──
    public string GainMapMode { get; set; } = "Gray";
    public int PaperWhiteNits { get; set; } = 200;
    public int DisplayMaxNits { get; set; } = 1000;

    // ── 界面 ──
    public string Language { get; set; } = "zh";
    public string OcrEngineMode { get; set; } = "OnnxGpu";
    public string ThemeMode { get; set; } = "Default";

    // ── Toast ──
    public bool ToastOnCapture { get; set; } = true;
    public bool ToastOnSilentCapture { get; set; } = true;
    public bool ToastOnRecording { get; set; } = true;
    public string ToastPosition { get; set; } = "BottomRight";

    // ── 预览界面颜色 ──
    public string OverlayColor { get; set; } = "#99001833";
    public string BorderColor { get; set; } = "#FF4488FF";

    // ── 字体选择 ──
    public string FontFamily { get; set; } = "";
}

/// <summary>AppSettingsData 扩展：格式索引 ↔ 每格式质量字段映射。</summary>
public static partial class AppSettingsQuality
{
    /// <summary>按格式索引获取每格式独立质量。</summary>
    /// <param name="formatIndex">0=PNG,1=GainMap,2=JPEGLI,3=JPEGXL,4=AVIF,5=WebP,6=TIFF。</param>
    public static double GetQuality(this AppSettingsData s, int formatIndex) => formatIndex switch
    {
        0 => s.QualityPng,
        1 => s.QualityGainMap,
        2 => s.QualityJpegLi,
        3 => s.QualityJpegXl,
        4 => s.QualityAvif,
        5 => s.QualityWebp,
        6 => s.QualityTiff,
        _ => s.Quality,
    };

    /// <summary>按格式索引设置每格式独立质量。</summary>
    public static void SetQuality(this AppSettingsData s, int formatIndex, double value)
    {
        switch (formatIndex)
        {
            case 0: s.QualityPng = value; break;
            case 1: s.QualityGainMap = value; break;
            case 2: s.QualityJpegLi = value; break;
            case 3: s.QualityJpegXl = value; break;
            case 4: s.QualityAvif = value; break;
            case 5: s.QualityWebp = value; break;
            case 6: s.QualityTiff = value; break;
            default: s.Quality = value; break;
        }
    }
}