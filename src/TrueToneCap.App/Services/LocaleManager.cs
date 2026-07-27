// TrueToneCap.App/Services/LocaleManager.cs
// 双语本地化管理（中文/English），无需 .resx 依赖

namespace TrueToneCap.App.Services;

/// <summary>界面语言。</summary>
public enum AppLanguage { Chinese, English }

/// <summary>静态本地化字符串提供器。</summary>
public static class LocaleManager
{
    private static AppLanguage s_lang = AppLanguage.Chinese;

    public static AppLanguage CurrentLanguage => s_lang;

    public static event Action? LanguageChanged;

    public static void SetLanguage(AppLanguage lang)
    {
        if (s_lang == lang) return;
        s_lang = lang;
        LanguageChanged?.Invoke();
    }

    // ── 导航 ──
    public static string NavOutput => s_lang == AppLanguage.Chinese ? "输出设置" : "Output";
    public static string NavColor => s_lang == AppLanguage.Chinese ? "色彩设置" : "Color";
    public static string NavCapture => s_lang == AppLanguage.Chinese ? "截图与录制" : "Capture";
    public static string NavAI => s_lang == AppLanguage.Chinese ? "AI 翻译" : "AI Translate";
    public static string NavSystem => s_lang == AppLanguage.Chinese ? "系统" : "System";

    // ── 输出页 ──
    public static string PageOutput => s_lang == AppLanguage.Chinese ? "输出设置" : "Output Settings";
    public static string BasicOutput => s_lang == AppLanguage.Chinese ? "基础输出" : "Basic Output";
    public static string Format => s_lang == AppLanguage.Chinese ? "格式" : "Format";
    public static string Quality => s_lang == AppLanguage.Chinese ? "质量" : "Quality";
    public static string SavePath => s_lang == AppLanguage.Chinese ? "保存路径" : "Save Path";
    public static string FilePrefix => s_lang == AppLanguage.Chinese ? "文件名前缀" : "Filename Prefix";
    public static string EnableArchive => s_lang == AppLanguage.Chinese ? "启用文件归档" : "Enable Archive";
    public static string ArchiveMode => s_lang == AppLanguage.Chinese ? "归档方式" : "Archive Mode";
    public static string AvifOptions => s_lang == AppLanguage.Chinese ? "AVIF 选项" : "AVIF Options";
    public static string EncoderBackend => s_lang == AppLanguage.Chinese ? "编码后端" : "Backend";
    public static string ChromaSampling => s_lang == AppLanguage.Chinese ? "色度采样" : "Chroma";
    public static string AvifPngSuffix => s_lang == AppLanguage.Chinese ? "添加 .avif.png 双后缀（兼容旧软件）" : "Add .avif.png suffix (legacy compat)";
    public static string GainMapOptions => s_lang == AppLanguage.Chinese ? "JPEG Gain Map (Ultra HDR)" : "JPEG Gain Map (Ultra HDR)";
    public static string GainMapDesc => s_lang == AppLanguage.Chinese ? "输出兼容 JPEG 的 HDR 照片。SDR 查看器显示基础图，HDR 查看器自动还原完整动态范围。" : "Outputs HDR photos compatible with JPEG. SDR viewers see base image, HDR viewers recover full dynamic range.";
    public static string GainMapModeLabel => s_lang == AppLanguage.Chinese ? "增益图模式" : "Gain Map Mode";

    // ── 色彩页 ──
    public static string PageColor => s_lang == AppLanguage.Chinese ? "色彩设置" : "Color Settings";
    public static string HdrOutput => s_lang == AppLanguage.Chinese ? "HDR 输出" : "HDR Output";
    public static string IccBaking => s_lang == AppLanguage.Chinese ? "ICC 色彩管理" : "ICC Color Mgmt";
    public static string ColorSpace => s_lang == AppLanguage.Chinese ? "色彩空间" : "Color Space";
    public static string IccStrategy => s_lang == AppLanguage.Chinese ? "sRGB 目标不嵌入 ICC（通用默认）；非 sRGB 色域自动嵌入标准 ICC。" : "sRGB target: no ICC (universal default); Non-sRGB: auto-embed standard ICC.";

    // ── 截图页 ──
    public static string PageCapture => s_lang == AppLanguage.Chinese ? "截图与录制" : "Capture & Record";
    public static string Screenshot => s_lang == AppLanguage.Chinese ? "截图" : "Screenshot";
    public static string Hotkey => s_lang == AppLanguage.Chinese ? "快捷键" : "Hotkey";
    public static string Record => s_lang == AppLanguage.Chinese ? "录制" : "Record";
    public static string PreviewThumb => s_lang == AppLanguage.Chinese ? "截图后右下角缩略图预览" : "Show thumbnail preview";
    public static string AnimationRecord => s_lang == AppLanguage.Chinese ? "动图录制" : "Animation";
    public static string AnimFormat => s_lang == AppLanguage.Chinese ? "格式" : "Format";
    public static string FrameRate => s_lang == AppLanguage.Chinese ? "帧率" : "Frame Rate";
    public static string MaxDuration => s_lang == AppLanguage.Chinese ? "最大时长" : "Max Duration";

    // ── AI 页 ──
    public static string PageAI => s_lang == AppLanguage.Chinese ? "AI 翻译" : "AI Translate";
    public static string UseCustomLlm => s_lang == AppLanguage.Chinese ? "使用自定义 LLM 翻译" : "Use Custom LLM";
    public static string ApiEndpoint => s_lang == AppLanguage.Chinese ? "API 端点" : "API Endpoint";
    public static string ApiKey => s_lang == AppLanguage.Chinese ? "API 密钥" : "API Key";
    public static string Model => s_lang == AppLanguage.Chinese ? "模型" : "Model";
    public static string CustomModel => s_lang == AppLanguage.Chinese ? "自定义模型" : "Custom Model";
    public static string SystemPrompt => s_lang == AppLanguage.Chinese ? "系统提示词" : "System Prompt";
    public static string TargetLang => s_lang == AppLanguage.Chinese ? "目标语言" : "Target Language";
    public static string OcrLang => s_lang == AppLanguage.Chinese ? "OCR 语言" : "OCR Language";
    public static string OcrEngine => s_lang == AppLanguage.Chinese ? "OCR 引擎" : "OCR Engine";
    public static string OcrEngineLabel => s_lang == AppLanguage.Chinese ? "引擎" : "Engine";

    // ── 系统页 ──
    public static string PageSystem => s_lang == AppLanguage.Chinese ? "系统" : "System";
    public static string Appearance => s_lang == AppLanguage.Chinese ? "外观" : "Appearance";
    public static string Theme => s_lang == AppLanguage.Chinese ? "主题" : "Theme";
    public static string Language => s_lang == AppLanguage.Chinese ? "语言" : "Language";
    public static string Behavior => s_lang == AppLanguage.Chinese ? "行为" : "Behavior";
    public static string AutoStart => s_lang == AppLanguage.Chinese ? "开机自动启动" : "Start with Windows";
    public static string MinimizeTray => s_lang == AppLanguage.Chinese ? "关闭时最小化到托盘" : "Minimize to tray on close";

    // ── 状态栏 ──
    public static string Ready => s_lang == AppLanguage.Chinese ? "就绪" : "Ready";
    public static string AreaCapture => s_lang == AppLanguage.Chinese ? "区域截图" : "Area Capture";
    public static string SaveSettings => s_lang == AppLanguage.Chinese ? "保存设置" : "Save Settings";
    public static string CapabilityDetect => s_lang == AppLanguage.Chinese ? "能力检测中..." : "Detecting...";

    // ── 格式提示 ──
    public static string GetFormatHint(string formatTag) => s_lang switch
    {
        AppLanguage.English => formatTag switch
        {
            "PNG" => "✅ Lossless, HDR via cICP Rec.2100 PQ. Best for screenshots.",
            "JPEG_LI" => "✅ Google jpegli, butteraugli distance. Small size, best compatibility.",
            "JPEG_GAINMAP" => "✅ Ultra HDR (ISO 21496-1), JPEG-compatible HDR.",
            "JPEG_XL" => "✅ Next-gen format, Modular mode great for screenshots. HDR.",
            "AVIF" => "✅ Advanced format, HDR + HW encode. Default 4:4:4.",
            "WebP" => "⚠ SDR 8-bit only. Simple sharing.",
            "BMP" => "⚠ Uncompressed. Special use only.",
            _ => ""
        },
        _ => formatTag switch
        {
            "PNG" => "✅ 无损格式，支持 HDR (cICP Rec.2100 PQ)。截图首选。",
            "JPEG_LI" => "✅ Google jpegli 编码，butteraugli 距离控制质量。体积小，兼容性最佳。",
            "JPEG_GAINMAP" => "✅ Ultra HDR (ISO 21496-1)，兼容 JPEG。SDR/HDR 自适应。",
            "JPEG_XL" => "✅ 新一代格式，Modular 模式对截图极优。支持 HDR。",
            "AVIF" => "✅ 先进格式，支持 HDR + 硬件加速编码。默认 4:4:4。",
            "WebP" => "⚠ 仅 SDR 8-bit。适合简单分享场景。",
            "BMP" => "⚠ 无压缩 BMP。仅限特殊用途。",
            _ => ""
        }
    };

    // ── 格式标签 ──
    public static string FmtPng => s_lang == AppLanguage.Chinese ? "PNG (无损)" : "PNG (Lossless)";
    public static string FmtJpegGainMap => s_lang == AppLanguage.Chinese ? "JPEG Gain Map (HDR)" : "JPEG Gain Map (HDR)";
    public static string FmtJpegLi => "JPEG LI";
    public static string FmtJpegXl => "JPEG XL";
    public static string FmtAvif => "AVIF";
    public static string FmtWebP => "WebP";
    public static string FmtBmp => "BMP";

    // ── AVIF 后端 ──
    public static string AvifAuto => s_lang == AppLanguage.Chinese ? "自动检测" : "Auto Detect";
    public static string AvifLibAom => s_lang == AppLanguage.Chinese ? "libaom (软件)" : "libaom (Software)";
    public static string AvifQsv => "Intel QSV";
    public static string AvifNvenc => "NVIDIA NVENC";

    // ── AVIF 色度 ──
    public static string AvifChroma444 => s_lang == AppLanguage.Chinese ? "4:4:4 ✓ 截图推荐" : "4:4:4 ✓ Best for screenshots";
    public static string AvifChroma422 => "4:2:2";
    public static string AvifChroma420 => s_lang == AppLanguage.Chinese ? "4:2:0 (体积最小)" : "4:2:0 (Smallest file)";

    // ── GainMap 模式 ──
    public static string GmRgb => s_lang == AppLanguage.Chinese ? "RGB 增益（色彩准确，体积较大）" : "RGB Gain (color accurate, larger)";
    public static string GmGray => s_lang == AppLanguage.Chinese ? "灰度增益（体积最优，亮度还原）" : "Gray Gain (smallest, luma only)";

    // ── 色域映射 ──
    public static string GamutMapTitle => s_lang == AppLanguage.Chinese ? "色域映射" : "Gamut Mapping";
    public static string SourceGamut => s_lang == AppLanguage.Chinese ? "显示器色域" : "Display Gamut";
    public static string MappingArrow => s_lang == AppLanguage.Chinese ? "→ 映射到" : "→ Map to";
    public static string RenderingIntent => s_lang == AppLanguage.Chinese ? "渲染意图" : "Rendering Intent";

    // ── 色彩空间选项 ──
    public static string CsSystem => s_lang == AppLanguage.Chinese ? "跟随显示器（自动）" : "Follow Display (Auto)";
    public static string CsSRgb => "sRGB";
    public static string CsDisplayP3 => "Display P3";
    public static string CsDciP3 => "DCI-P3";
    public static string CsAdobeRgb => "Adobe RGB";
    public static string CsBT2020 => "BT.2020";

    // ── 渲染意图选项 ──
    public static string RiRelative => s_lang == AppLanguage.Chinese ? "相对比色（截图推荐）" : "Relative Colorimetric (recommended)";
    public static string RiPerceptual => s_lang == AppLanguage.Chinese ? "感知（色彩过渡自然）" : "Perceptual (smooth gradients)";
    public static string RiSaturation => s_lang == AppLanguage.Chinese ? "饱和度优先" : "Saturation (vivid)";

    // ── 动图格式 ──
    public static string AnimGif => "GIF";
    public static string AnimApng => "APNG";
    public static string AnimAvif => s_lang == AppLanguage.Chinese ? "动画 AVIF" : "Animated AVIF";

    // ── 帧率 ──
    public static string Fps10 => "10 fps";
    public static string Fps15 => "15 fps";
    public static string Fps20 => "20 fps";
    public static string Fps30 => "30 fps";

    // ── 时长 ──
    public static string Dur15 => s_lang == AppLanguage.Chinese ? "15 秒" : "15 sec";
    public static string Dur30 => s_lang == AppLanguage.Chinese ? "30 秒" : "30 sec";
    public static string Dur60 => s_lang == AppLanguage.Chinese ? "60 秒" : "60 sec";
    public static string Dur120 => s_lang == AppLanguage.Chinese ? "120 秒" : "120 sec";

    // ── 主题 ──
    public static string ThemeDefault => s_lang == AppLanguage.Chinese ? "跟随系统" : "Follow System";
    public static string ThemeLight => s_lang == AppLanguage.Chinese ? "浅色" : "Light";
    public static string ThemeDark => s_lang == AppLanguage.Chinese ? "深色" : "Dark";
    public static string ThemeOled => s_lang == AppLanguage.Chinese ? "OLED 灰" : "OLED Gray";

    // ── 语言选项 ──
    public static string LangChinese => s_lang == AppLanguage.Chinese ? "中文" : "Chinese";
    public static string LangEnglish => "English";

    // ── OCR 引擎 ──
    public static string OcrAuto => s_lang == AppLanguage.Chinese ? "自动 (GPU → Win → CPU)" : "Auto (GPU → Win → CPU)";
    public static string OcrGpu => "ONNX GPU";
    public static string OcrWindows => "Windows OCR";
    public static string OcrCpu => "ONNX CPU";

    // ── LLM 模型 ──
    public static string LlmGpt4oMini => "GPT-4o mini";
    public static string LlmDeepSeek => "DeepSeek V3";
    public static string LlmGpt41Mini => "GPT-4.1 mini";
    public static string LlmCustom => s_lang == AppLanguage.Chinese ? "自定义" : "Custom";

    // ── 翻译目标语言 ──
    public static string TlChinese => s_lang == AppLanguage.Chinese ? "中文" : "Chinese";
    public static string TlEnglish => "English";
    public static string TlJapanese => "日本語";

    // ── OCR 识别语言 ──
    public static string OlSystem => s_lang == AppLanguage.Chinese ? "跟随系统" : "Follow System";
    public static string OlMixed => s_lang == AppLanguage.Chinese ? "中英混合" : "Chinese + English";

    // ── 归档模式 ──
    public static string ArchiveYear => s_lang == AppLanguage.Chinese ? "按年" : "By Year";
    public static string ArchiveMonth => s_lang == AppLanguage.Chinese ? "按月" : "By Month";
    public static string ArchiveDay => s_lang == AppLanguage.Chinese ? "按日" : "By Day";
}
