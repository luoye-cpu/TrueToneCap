// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
    public static string NavColor => s_lang == AppLanguage.Chinese ? "色彩管理" : "Color";
    public static string NavCapture => s_lang == AppLanguage.Chinese ? "截图与录制" : "Capture";
    public static string NavAI => s_lang == AppLanguage.Chinese ? "OCR / 翻译" : "OCR / Translate";
    public static string NavSystem => s_lang == AppLanguage.Chinese ? "系统设置" : "System";
    public static string NavLog => s_lang == AppLanguage.Chinese ? "运行日志" : "Logs";

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

    // 压缩强度档位的文案只有一处事实源：下拉项写在 MainWindow.xaml，逐 Tag 的英文改写走
    // MainWindow.Localization.cs 的 Tr("<中文>") 查下面的对照表。档位名按【用户视角的结果】排,
    // 不按底层参数名（avifenc 越小越慢、cjxl 越大越慢、cwebp 越大越慢、NVENC P 越大越快、
    // zlib 越大越慢、cjpegli 是个布尔开关）—— 映射本身见 CompressionEffortMap。
    public static string AvifPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    public static string JxlPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    // 2026-09-26：GainMap 并入 JPEG LI ⇒ 独立卡片的标题（GainMapOptions）已删，
    // 开关本身的名字是 GainMapToggle，说明仍是 GainMapDesc（挂在开关下方）。
    public static string GainMapToggle => s_lang == AppLanguage.Chinese
        ? "附带增益图（输出 Ultra HDR 照片）" : "Attach gain map (Ultra HDR output)";
    public static string GainMapDesc => s_lang == AppLanguage.Chinese ? "底图恒为 SDR，HDR 在增益图里。「目标色域」决定底图与增益图的原色；打开此开关时 HDR 输出锁定为开。" : "The base image is always SDR; the HDR lives in the gain map. Target gamut sets the primaries for base and gain map; with this on, HDR output is locked on.";
    public static string GainMapModeLabel => s_lang == AppLanguage.Chinese ? "增益图模式" : "Gain Map Mode";

    // ═══ 2026-09-19：WebP 的「色度采样」下拉已撤掉，改为一句静态说明 ═══
    //   撤掉而非"修好接线"的原因是实测接不上：同一样张只改色度，420 与 444 产物字节完全相同、
    //   ffprobe 恒 yuv420p —— NativeWebPEncoder 没有色度形参，而有损 WebP 的格式定义就是 4:2:0
    //   （无损为 RGB）。所以这条文案说的是"为什么没有选项"，不是选项标签。
    public static string WebPChromaNote => s_lang == AppLanguage.Chinese
        ? "有损恒为 4:2:0，无损为 RGB；本格式没有色度选项。"
        : "Lossy is always 4:2:0, lossless is RGB; this format has no chroma option.";

    // ═══ 2026-09-19：PNG / TIFF / JPEG XL 三张位深卡片共用的实话（§4.3 双路径策略）═══
    //   编码行为按既定决策不改（下拉是真生效的），但"升位深不产生信息"必须出现在界面上。
    //   2026-10-30：JXL 单独一条 —— cjxl 没有独立位深参数，10/16-bit 同装 16-bit 容器，
    //   只差 sBIT 标注；共用的短句会把这条区别抹掉。
    public static string BitDepthSourceNote => s_lang == AppLanguage.Chinese
        ? "8-bit 源不会升位深；高位深只对 Float16/HDR 源有效。"
        : "An 8-bit source gains no bit depth; high bit depth only matters for Float16/HDR sources.";

    /// <summary>JPEG XL 专用：多一句"10/16-bit 同装 16-bit 容器，只差 sBIT"。</summary>
    public static string BitDepthJxlNote => s_lang == AppLanguage.Chinese
        ? "8-bit 源不会升位深；高位深只对 Float16/HDR 源有效。10-bit 与 16-bit 都用 16-bit 容器，差别只在 sBIT 标注。"
        : "An 8-bit source gains no bit depth; high bit depth only matters for Float16/HDR sources. 10-bit and 16-bit both use a 16-bit container and differ only in the sBIT tag.";

    // ── 色彩页 ──
    public static string PageColor => s_lang == AppLanguage.Chinese ? "色彩设置" : "Color Settings";
    public static string HdrOutput => s_lang == AppLanguage.Chinese ? "HDR 输出" : "HDR Output";
    public static string IccBaking => s_lang == AppLanguage.Chinese ? "ICC 色彩管理" : "ICC Color Mgmt";
    public static string ColorSpace => s_lang == AppLanguage.Chinese ? "色彩空间" : "Color Space";
    public static string IccStrategy => s_lang == AppLanguage.Chinese ? "目标色域同时决定像素与容器声明。" : "Target gamut sets both the pixels and the container declaration.";

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

    // ── 日志详细度（Core.LogDetail 三档；档位决定 Debug/Trace 进不进日志文件）──
    // ── 日志详细度（Core.LogDetail 三档；档位决定 Debug/Trace 进不进日志文件）──
    // 2026-09-25（用户口径）：下拉项只写档位名，解释一律走下面的灰字提示行 ——
    // 项文字会被选中后长期占着窄下拉，塞说明既截断又与灰字行重复。
    public static string LogVerbosity => s_lang == AppLanguage.Chinese ? "日志详细度" : "Log verbosity";
    public static string LogVerbosityBrief => s_lang == AppLanguage.Chinese ? "简" : "Basic";
    public static string LogVerbosityDiagnose => s_lang == AppLanguage.Chinese ? "诊断" : "Diagnostic";
    public static string LogVerbosityFull => s_lang == AppLanguage.Chinese ? "全" : "Full";
    public static string LogVerbosityHint => s_lang == AppLanguage.Chinese
        ? "档位越高记录越细，只影响之后写入的行；任何档位都不记录截图文字、图像字节或密钥。"
        : "Higher tiers log more detail and only affect lines written afterwards; no tier records screenshot text, image bytes or keys.";

    // ── Toast 通知 ──
    public static string ToastTitle => s_lang == AppLanguage.Chinese ? "通知" : "Notifications";
    public static string ToastOnCapture => s_lang == AppLanguage.Chinese ? "截图后提示" : "Toast on capture";
    public static string ToastOnSilent => s_lang == AppLanguage.Chinese ? "无感截图后提示" : "Toast on silent capture";
    public static string ToastOnRecording => s_lang == AppLanguage.Chinese ? "录制后提示" : "Toast on recording";
    public static string ToastPositionLabel => s_lang == AppLanguage.Chinese ? "通知位置" : "Notification position";
    public static string ToastPosBottomRight => s_lang == AppLanguage.Chinese ? "右下角" : "Bottom Right";
    public static string ToastPosTopRight => s_lang == AppLanguage.Chinese ? "右上角" : "Top Right";
    public static string ToastPosTopLeft => s_lang == AppLanguage.Chinese ? "左上角" : "Top Left";
    public static string ToastPosBottomLeft => s_lang == AppLanguage.Chinese ? "左下角" : "Bottom Left";
    public static string ToastPosWindows => s_lang == AppLanguage.Chinese ? "Windows 自带通知" : "Windows Notification";

    // ── 字体 ──
    public static string FontTitle => s_lang == AppLanguage.Chinese ? "界面字体" : "Interface Font";
    public static string FontLabel => s_lang == AppLanguage.Chinese ? "字体" : "Font";
    public static string FontDefault => s_lang == AppLanguage.Chinese ? "默认 (微软雅黑)" : "Default (Microsoft YaHei)";
    public static string FontCustom => s_lang == AppLanguage.Chinese ? "自定义..." : "Custom...";
    public static string FontCustomPlaceholder => s_lang == AppLanguage.Chinese ? "输入系统已安装的字体名称" : "Enter installed font name";
    public static string FontDesc => s_lang == AppLanguage.Chinese ? "界面字体只影响外观，不影响截图像素；未安装时回落到默认字体。" : "The interface font affects appearance only, never captured pixels; if it is not installed, the default is used.";

    // ── 状态栏 ──
    public static string Ready => s_lang == AppLanguage.Chinese ? "就绪" : "Ready";
    public static string AreaCapture => s_lang == AppLanguage.Chinese ? "区域截图" : "Area Capture";
    public static string SaveSettings => s_lang == AppLanguage.Chinese ? "保存设置" : "Save Settings";
    public static string CapabilityDetect => s_lang == AppLanguage.Chinese ? "能力检测中..." : "Detecting...";
    // 2026-09-30：选区预览统一到 HDR 那张自绘窗之后，SDR 与 HDR 用同一句状态提示
    // （原先两句分别写死 "🖥️ HDR 预览" 与 "📷 选区模式"，都是 .cs 里的中文直写）。
    public static string SelectionPreview => s_lang == AppLanguage.Chinese ? "🖼️ 选区预览" : "Selection preview";

    // ── 格式提示 ──
    // ── 格式提示 ──
    // 2026-09-26 删除 `GetFormatHint(string formatTag)`：全仓零调用点（`grep -rn GetFormatHint src/`
    // 只有它自己的声明），而它的内容还停在旧口径上 —— 例如 "AVIF：支持 HDR + 硬件加速编码。默认 4:4:4"
    // 与 §49/§51 实测结论相反（Auto 是默认、硬件受色度与位深限制、软件档才是 4:4:4），
    // 且根本没有 TIFF 一档。逐格式的真实说明只有一个来源：`MainWindow.xaml.cs` 的 s_formatCaps.Hint，
    // 加上「色域映射」卡片按格式印的声明通道（Core 的 FormatColorFacts.Declaration）。
    // 留着一份没人读、内容又过时的表，就是下一轮文档不同构的源头。


    /// <summary>
    /// XAML 里写死的中文 → 英文对照表（2026-09-26，交接 §55）。
    /// <para>为什么用"整表扫可视树"而不是一句句手工接线：过去 57 条界面文字在代码里<b>没有任何英文对照</b>
    /// —— 每条都要配 x:Name + 在 <c>ApplyLocale()</c> 里手写一句赋值，太贵，于是长期只做了少数几条，
    /// 而 README 把"界面语言"当在售能力写。加一行就生效，缺行则门禁
    /// （<c>Test_Xaml_HardcodedChineseIsLocalized</c>）直接红。</para>
    /// <para>表的键 = XAML 里那条原文，所以门禁能机械核对"哪些字面量还没英文对照"。
    /// 译文按界面语境写（短句、无句末空格），长说明保留原有的 ⇒/「」等符号。</para>
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> ZhToEn =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["截图设置"] = "Capture",
            ["色彩与 HDR"] = "Colour & HDR",
            ["目标色域"] = "Target gamut",
            ["检测中..."] = "Detecting…",
            ["广色域自动缩限到目标色域。"] = "Wide gamut is clamped into the target gamut.",
            ["自动优先硬件编码器；色度或位深不满足时自动降级并提示。"] =
                "Auto prefers hardware encoders and falls back with a notice when chroma or bit depth is unsupported.",
            ["AVIF 位深"] = "AVIF bit depth",
            ["位深"] = "Bit depth",
            ["上限取决于编码器：软件 ≤12-bit，NVENC ≤10-bit，QSV 8-bit；HDR 至少 10-bit。8-bit 源恒输出 8-bit。"] =
                "The cap follows the encoder: software ≤12-bit, NVENC ≤10-bit, QSV 8-bit; HDR needs at least 10-bit. An 8-bit source always exports 8-bit.",
            ["JPEG 选项"] = "JPEG options",
            // ── 换格式保底开关（2026-09-29 用户口径：接上保底 + 触发必须告知 + 设置里给开关）──
            // 界面里这两串中文是 XAML 写死的，靠这张表被扫描接管；漏一条就是英文界面下的中文残留。
            ["编码失败时改存为 PNG"] = "Fall back to PNG when an encoder fails",
            ["开：编码失败时改存为 PNG 并提示（格式变了，像素无损）。关：失败直接报错，产物仍是所选格式。"] =
                "On: a failed encode is saved as PNG with a notice (the format changes, pixels stay lossless). Off: the failure is reported and the output stays in the chosen format.",
            ["采样越高色度越准、体积越大：4:4:4 不压缩色度，4:2:0 体积最小。"] =
                "More chroma means more accurate colour and larger files: 4:4:4 keeps full chroma, 4:2:0 is smallest.",
            ["底图恒为 SDR，HDR 在增益图里。「目标色域」决定底图与增益图的原色；打开此开关时 HDR 输出锁定为开。"] =
                "The base image is always SDR; the HDR lives in the gain map. Target gamut sets the primaries for base and gain map; with this on, HDR output is locked on.",
            ["PNG 选项"] = "PNG options",
            ["JPEG XL 选项"] = "JPEG XL options",
            ["本格式只有 4:4:4 与 4:2:0；需要 4:2:2 请改用 JPEG 或 AVIF。"] =
                "This format offers only 4:4:4 and 4:2:0; use JPEG or AVIF for 4:2:2.",
            ["8-bit 源不会升位深；高位深只对 Float16/HDR 源有效。10-bit 与 16-bit 都用 16-bit 容器，差别在 sBIT 标注。"] =
                "An 8-bit source gains no bit depth; high bit depth only matters for Float16/HDR sources. 10-bit and 16-bit both use a 16-bit container and differ only in the sBIT annotation.",
            ["WebP 选项"] = "WebP options",
            ["TIFF 选项"] = "TIFF options",
            ["无感截图快捷键"] = "Silent capture hotkey",
            ["保存快捷键"] = "Save hotkey",
            ["取消快捷键"] = "Cancel hotkey",
            ["预览界面颜色"] = "Preview colour",
            ["覆盖遮罩颜色"] = "Overlay scrim colour",
            ["选区边框颜色"] = "Selection border colour",
            ["OCR 文字识别"] = "OCR",
            ["框选后点「识字 / 翻译」提取文字，不会自动触发；中英文最准。"] =
                "After a region selection, click Read / Translate in the overlay; it never runs automatically and is most accurate for Chinese and English.",
            ["识别语言"] = "Recognition language",
            ["翻译模式"] = "Translation mode",
            ["识别出的文字会按所选模式译为目标语言"] =
                "Recognised text is translated into the target language in the selected mode",
            ["模式"] = "Mode",
            ["无需密钥：按 Edge → 必应 → MyMemory → Google 依次尝试免费公共端点。"] =
                "No key needed: free public endpoints are tried in order — Edge → Bing → MyMemory → Google.",
            ["使用免费公共翻译端点（识别到的文字会发给第三方）"] =
                "Use free public translation endpoints (recognized text is sent to third parties)",
            ["截图里的文字会发给微软 / MyMemory 等第三方，这些端点也可能随时失效。不想外发就取消勾选，并填下面的凭据。"] =
                "Text from your screenshots goes to third parties such as Microsoft / MyMemory, and these endpoints may stop working at any time. To keep text local, untick this and fill in a credential below.",
            ["有道 AppID"] = "Youdao AppID",
            ["可选：填了优先走官方 API（更快更稳）"] =
                "Optional: the official API is used first when filled (faster, steadier)",
            ["有道密钥"] = "Youdao secret",
            ["仅在本机加密保存，不会上传"] =
                "Encrypted and stored on this device; never uploaded",
            ["登录 ai.youdao.com 创建应用，复制 AppID 与密钥。"] =
                "Sign in at ai.youdao.com, create an app, then copy the App ID and secret.",
            ["LLM 配置"] = "LLM settings",
            ["OCR 识别文字后，通过 LLM API 翻译为高质量译文"] =
                "After OCR, translate the text through an LLM API for higher-quality output",
            ["提供商"] = "Provider",
            ["提示词"] = "Prompt",
            ["留空使用默认翻译提示词..."] = "Leave empty for the default translation prompt…",
            ["字体名称"] = "Font name",
            ["前四项是应用内浮层，始终可见；「Windows 自带通知」不可用时自动回退为浮层。"] =
                "The first four are in-app overlays and always visible; Windows notifications fall back to an overlay when unavailable.",
            ["启用界面过渡动画（更流畅）"] = "Enable UI transition animations",
            ["档位越高记录越细，只影响之后写入的行；任何档位都不记录截图文字、图像字节或密钥。"] =
                "Higher tiers log more detail and only affect lines written afterwards; no tier records screenshot text, image bytes or keys.",
            // 这三条的英文以 ThirdPartyNoticesWindow 的 CardTitleText/CardDescText/CardButtonText
            // 为准（那份一直在界面上生效，表里原先写的 "licences" 是第二份真相且已被它压掉）：
            // 表改成与实机显示一致 ⇒ 英文措辞只有一处说了算（判据的歧义锁扫全部 App 源码）。
            ["第三方许可"] = "Third-party notices",
            ["随本应用一并再分发的第三方组件及其许可证全文。"] =
                "License texts of the third-party components redistributed with this app.",
            ["查看第三方许可"] = "View third-party notices",

            // ── 隐私政策（2026-10-07，商店政策 10.5.1 要求的产品内入口）──
            // 与上面三条同理：英文以 PrivacyPolicyWindow 的
            // CardTitleText / CardDescText / CardButtonText 与窗口内静态文字为准，
            // 表里与实机显示保持一致 ⇒ 英文措辞只有一处说了算。
            ["隐私政策"] = "Privacy policy",
            ["截图与文字识别均在本机完成；只有翻译会把内容发往第三方，视觉翻译发送整张截图。"] =
                "Screenshots and OCR run entirely on this device; content is sent to third parties only by translation, and Vision translation sends the whole screenshot.",
            ["查看隐私政策"] = "View privacy policy",
            // 窗口内静态文字（PrivacyPolicyWindow.xaml 的头/脚/降级面）
            ["本应用的数据处理方式（只读展示）"] = "How this app handles your data (read only).",
            ["未找到 PRIVACY.md"] = "PRIVACY.md was not found",
            ["TrueToneCap 隐私政策"] = "TrueToneCap Privacy Policy",

            ["清空"] = "Clear",
            ["打开日志文件夹"] = "Open log folder",
            ["级别:"] = "Level:",
            ["分类:"] = "Category:",
            ["搜索日志..."] = "Search logs…",
            ["最新在前"] = "Newest first",
            ["自动滚动"] = "Auto-scroll",
            ["日志目录: 请查看 log/ 文件夹"] = "Log directory: see the log/ folder",
            ["HDR 截图工具"] = "HDR screenshot tool",
            ["输出设置"] = "Output",
            ["OCR / 翻译"] = "OCR / Translate",
            ["系统设置"] = "System",
            ["运行日志"] = "Logs",
            ["输出与色彩"] = "Output & Colour",
            ["基础输出"] = "Basic Output",
            ["格式"] = "Format",
            ["质量"] = "Quality",
            ["保存路径"] = "Save Path",
            ["文件名前缀"] = "Filename Prefix",
            ["启用文件归档"] = "Enable Archive",
            ["归档方式"] = "Archive Mode",
            ["HDR 输出"] = "HDR Output",
            ["目标色域同时决定像素与容器声明。"] = "Target gamut sets both the pixels and the container declaration.",
            ["色域映射"] = "Gamut Mapping",
            ["显示器色域"] = "Display Gamut",
            ["→ 映射到"] = "→ Map to",
            ["AVIF 选项"] = "AVIF Options",
            ["AVIF 编码器"] = "AVIF encoder",
            ["色度采样"] = "Chroma",
            // ═══ 压缩强度三档（2026-10-07）═══
            // ⚠ 键必须与 XAML 里的中文【逐字相同】：本仓的本地化闸门是拿 XAML 的中文属性
            //   去 ZhToEn 表里做精确查找（CorePipelineTests 的 XAML 中文覆盖判据），
            //   差一个全角括号就会报"未翻译"。改 XAML 文案时必须同步改这里。
            ["压缩强度"] = "Compression",
            // 档位显示名（2026-10-29 用户定名）：两档格式用「均衡 / 快速」，三档格式用「极高 / 均衡 / 快速」。
            // ⚠ 名字里不含参数深度：两档格式的「均衡」其实就是拉满那一档（见下面各条说明）。
            ["极高"] = "Maximum",
            ["均衡"] = "Balanced",
            ["快速"] = "Fast",
            ["只影响编码耗时与文件体积，不影响质量。软件路改 avifenc 的 speed，硬件路改 NVENC 的 preset。"] = "Only encoding time and file size change; quality is untouched. The software path changes avifenc's speed, the hardware path NVENC's preset.",
            ["只影响压缩耗时，不影响质量。档位越高越慢，体积不一定更小。"] = "Only compression time changes; quality is untouched. Higher tiers are slower, and not necessarily smaller.",
            ["只影响压缩耗时与体积，不影响质量。均衡=-m 6，快速=-m 5。"] = "Only compression time and size change; quality is untouched. Balanced = -m 6, Fast = -m 5.",
            ["均衡开 XYB 色彩空间，快速不开；两档质量相同。"] = "Balanced turns on the XYB colour space, Fast does not; both tiers keep the same quality.",
            ["无损：只影响压缩耗时与文件体积，像素不变。均衡=压缩最强，快速=更快。"] = "Lossless: only compression time and file size change, pixels stay identical. Balanced = strongest compression, Fast = quicker.",
            ["额外添加 .png 后缀来提高兼容性"] = "Add .png suffix for compatibility",
            ["附带增益图（输出 Ultra HDR 照片）"] = "Attach gain map (Ultra HDR output)",
            ["增益图模式"] = "Gain Map Mode",
            ["灰度增益：只编码亮度差，体积最小，适合文字与图标。"] = "Gray gain: encodes luminance only, smallest files, best for text and icons.",
            ["RGB 增益：三通道独立编码，色彩更准，适合彩色截图。"] = "RGB gain: encodes each channel separately, more accurate colour, best for colour screenshots.",
            ["8-bit 源不会升位深；高位深只对 Float16/HDR 源有效。"] = "An 8-bit source gains no bit depth; high bit depth only matters for Float16/HDR sources.",
            ["有损恒为 4:2:0，无损为 RGB；本格式没有色度选项。"] = "Lossy is always 4:2:0, lossless is RGB; this format has no chroma option.",
            ["截图与录制"] = "Capture",
            ["截图"] = "Screenshot",
            ["快捷键"] = "Hotkey",
            ["录制"] = "Record",
            ["截图后右下角缩略图预览"] = "Show thumbnail preview",
            ["动图录制"] = "Animation",
            ["帧率"] = "Frame Rate",
            ["最大时长"] = "Max Duration",
            ["AI 翻译"] = "AI Translate",
            ["引擎"] = "Engine",
            ["目标语言"] = "Target Language",
            ["API 端点"] = "API Endpoint",
            ["API 密钥"] = "API Key",
            ["模型"] = "Model",
            ["自定义模型"] = "Custom Model",
            ["系统"] = "System",
            ["外观"] = "Appearance",
            ["主题"] = "Theme",
            ["语言"] = "Language",
            ["界面字体"] = "Interface Font",
            ["字体"] = "Font",
            ["输入系统已安装的字体名称"] = "Enter installed font name",
            ["界面字体只影响外观，不影响截图像素；未安装时回落到默认字体。"] = "The interface font affects appearance only, never captured pixels; if it is not installed, the default is used.",
            ["通知"] = "Notifications",
            ["截图后提示"] = "Toast on capture",
            ["无感截图后提示"] = "Toast on silent capture",
            ["录制后提示"] = "Toast on recording",
            ["通知位置"] = "Notification position",
            ["行为"] = "Behavior",
            ["开机自动启动"] = "Start with Windows",
            ["关闭时最小化到托盘"] = "Minimize to tray on close",
            ["日志详细度"] = "Log verbosity",
            ["就绪"] = "Ready",
            // ── 以下 67 条来自五个副窗口（标注 / OCR 预览 / 选区覆盖层 / 截图缩略图 toast / 第三方许可）：
            //    原先一条英文都没有，英文模式下整窗留中文（2026-09-26，交接 §55）。
            //    三条窗口标题（TrueToneCap 标注 / 文字识别 / 第三方许可）也在这里，但它们不在可视树里，
            //    由各自构造器显式 Tr(Title) —— 门禁判据按"标题不在 XAML 属性里"单独放行这三条。──
            ["矩形 (拖动绘制)"] = "Rectangle (drag to draw)",
            ["椭圆 (拖动绘制)"] = "Ellipse (drag to draw)",
            ["箭头 (拖动绘制)"] = "Arrow (drag to draw)",
            ["画笔 (按住自由绘制)"] = "Pen (hold to draw freely)",
            ["文字 (点击位置添加)"] = "Text (click to place)",
            ["马赛克 (拖动区域打码)"] = "Mosaic (drag to mask an area)",
            ["标注颜色"] = "Annotation colour",
            ["线条粗细"] = "Stroke width",
            ["撤销 (Ctrl+Z)"] = "Undo (Ctrl+Z)",
            ["重做 (Ctrl+Y)"] = "Redo (Ctrl+Y)",
            ["清空所有标注"] = "Clear all annotations",
            ["显示/隐藏图层面板"] = "Show / hide the layer panel",
            ["保存图片到文件 (S)"] = "Save image to file (S)",
            ["保存"] = "Save",
            ["复制图像到剪贴板 (Enter)"] = "Copy image to clipboard (Enter)",
            ["复制"] = "Copy",
            ["丢弃截图 (Esc)"] = "Discard screenshot (Esc)",
            ["丢弃"] = "Discard",
            ["图层:"] = "Layer:",
            ["上一个图层"] = "Previous layer",
            ["下一个图层"] = "Next layer",
            ["删除选中图层"] = "Delete selected layer",
            ["上移图层"] = "Move layer up",
            ["下移图层"] = "Move layer down",
            ["选择图层"] = "Select layer",
            ["输入标注文字"] = "Enter annotation text",
            ["确认文字 (Enter)"] = "Confirm text (Enter)",
            ["选择工具后在图像上拖动以添加标注"] = "Pick a tool, then drag on the image to add an annotation",
            ["TrueToneCap 标注"] = "TrueToneCap Annotate",
            ["切换原文高亮 / 译文覆盖"] = "Toggle original highlight / translation overlay",
            ["显示译文"] = "Show translation",
            ["翻译识别到的文字"] = "Translate the recognised text",
            ["翻译"] = "Translate",
            ["复制全部文字"] = "Copy all text",
            ["复制全部"] = "Copy all",
            ["保存截图"] = "Save screenshot",
            ["保存原图"] = "Save original",
            ["保存当前"] = "Save current view",
            ["保存选区原图（不含文字覆盖层）"] = "Save the original region without any text overlay",
            ["保存当前画面：译文模式会把译文画进图里，原文模式等于原图（格式跟随主窗口）"] =
                "Save what you see: in translation mode the text is baked into the image; in original mode it equals the original (format follows the main window)",
            ["关闭 (Esc)"] = "Close (Esc)",
            ["关闭"] = "Close",
            ["显示 / 关闭文字覆盖层"] = "Show / hide the text overlay",
            ["隐藏覆盖"] = "Hide overlay",
            ["点击文字块复制该行 · 切换「显示译文」查看翻译 · Esc 关闭"] =
                "Click a text block to copy that line · use 「Show translation」 · Esc closes",
            ["TrueToneCap 文字识别"] = "TrueToneCap Text Recognition",
            ["单击 = 全屏  |  拖拽 = 框选  |  Esc = 取消"] =
                "Click = full screen  |  Drag = select region  |  Esc = cancel",
            ["保存截图到文件并复制 (Enter)"] = "Save screenshot to file and copy (Enter)",
            ["标注选区（矩形/箭头/马赛克等）"] = "Annotate the selection (rectangle / arrow / mosaic …)",
            ["标注"] = "Annotate",
            ["识别图中文字 (OCR)"] = "Recognise text in the image (OCR)",
            // ── 贴图（2026-10-27 合并上游 PR #1 的置顶贴图窗）──
            ["贴图"] = "Pin",
            ["贴图到屏幕（滚轮缩放、右键菜单）"] = "Pin the image on screen (wheel to zoom, right-click menu)",
            ["保存为 PNG"] = "Save as PNG",
            ["保存为 AVIF"] = "Save as AVIF",
            ["隐藏工具栏"] = "Hide toolbar",
            ["显示工具栏"] = "Show toolbar",
            ["销毁"] = "Dismiss",
            ["识字"] = "OCR",
            ["翻译图中文字"] = "Translate text in the image",
            // ── 二维码识别（2026-10-26）：工具栏新按钮 + 结果窗（QrResultWindow）──
            // 2026-10-26 契约单源化：工具条悬停提示统一为 ToolbarContract 的措辞（两端共用），
            // 旧键"识别二维码 (QR)"随 XAML 静态提示一起退役（反向孤儿判据会盯着它不许再回来）。
            ["识别图中二维码（区域照常保存+复制）"] = "Recognise QR codes in the image (the region is still saved and copied)",
            ["二维码"] = "QR",
            ["TrueToneCap 二维码"] = "TrueToneCap QR Code",
            ["解码在本机完成 · 程序不会自动打开任何内容 · 点「打开链接」才启动浏览器"] =
                "Decoded locally · the app never opens anything on its own · the browser starts only when you press 「Open link」",
            ["检测到 {0} 个二维码（解码在本机完成，不会自动打开任何内容）"] =
                "Detected {0} QR code(s) — decoded locally, nothing is opened automatically",
            ["链接"] = "Link",
            ["其它协议（不打开）"] = "Other scheme (never opened)",
            ["危险协议"] = "Dangerous scheme",
            ["文本"] = "Text",
            ["⚠ 内容含控制字符或双向覆盖符（反向显示类），屏幕上已按可见转义显示 —— 该内容的视觉顺序可能与实际不同，复制前请留意。"] =
                "⚠ This content contains control characters or bidi overrides (visual-order tricks); they are shown as visible escapes here — what you see may not match reality. Check before copying.",
            ["⚠ 此内容携带可执行/系统跳转类协议（file: / ms-msdt: / shell: 等），程序已禁止任何打开动作 —— 仅作展示。"] =
                "⚠ This payload carries an executable/system-jump scheme (file: / ms-msdt: / shell: …); every open action is blocked — display only.",
            ["复制内容"] = "Copy content",
            ["复制解码原文（逐字原样）"] = "Copy the decoded text verbatim",
            ["打开链接"] = "Open link",
            ["在默认浏览器中打开该链接（仅 http/https）"] = "Open this link in the default browser (http/https only)",
            ["✓ 已复制（原样，未改写）"] = "✓ Copied (verbatim)",
            ["复制失败"] = "Copy failed",
            ["已在默认浏览器打开"] = "Opened in the default browser",
            ["打开链接失败"] = "Failed to open the link",
            ["预览渲染失败: {0}"] = "Preview render failed: {0}",
            ["取消选区 (Esc)"] = "Cancel selection (Esc)",
            ["提取区域失败"] = "Failed to extract the region",
            ["预览窗无法创建"] = "Could not create the preview window",
            ["取消"] = "Cancel",
            ["完成"] = "Done",
            ["打开文件夹"] = "Open folder",
            ["打开文件"] = "Open file",
            ["复制路径"] = "Copy path",
            ["在资源管理器中打开并选中该文件"] = "Reveal the file in Explorer",
            ["用默认程序打开该文件"] = "Open the file with its default program",
            ["复制文件完整路径"] = "Copy the full file path",
            ["第三方软件许可"] = "Third-party software licenses",
            ["随本应用一并再分发的第三方组件及其许可证全文（只读展示）"] =
                "Full texts of the third-party components redistributed with this app (read only).",
            ["未找到 THIRD-PARTY-NOTICES.md"] = "THIRD-PARTY-NOTICES.md not found",
            ["TrueToneCap 第三方许可"] = "TrueToneCap Third-party notices",
            ["区域截图"] = "Area Capture",
            ["保存设置"] = "Save Settings",
        // ── 2026-10-03 英文界面补完 · 第一批：落地页 Format 提示（实机取证 ui-en.png）──
            ["✅ 无损格式，支持 HDR (cICP Rec.2100 PQ) + 广色域 ICC。截图首选。"] = "✅ Lossless; supports HDR (cICP Rec.2100 PQ) + wide-gamut ICC. Recommended for screenshots.",
            ["✅ Ultra HDR (ISO 21496-1)：SDR 查看器显示基础图，HDR 查看器还原完整动态范围。目标色域就是【底图】的色彩空间：Rec.709 或显式 Rec.2020（三件一起搬，底图本身仍是 SDR）。"] = "✅ Ultra HDR (ISO 21496-1): SDR viewers show the base image, HDR viewers restore the full dynamic range. The target gamut IS the base image's colour space - Rec.709 or explicit Rec.2020 (all three travel together; the base image stays SDR).",
            ["✅ 8-bit，容器没有 HDR 通道；质量由 butteraugli 距离控制。默认目标 Rec.709，显式选 Rec.2020 才输出 Rec.2020。"] = "✅ 8-bit, and the container has no HDR channel; quality is driven by the butteraugli distance. The default target is Rec.709 — Rec.2020 is written only when you pick it explicitly.",
            ["✅ Modular 模式对截图最优，支持 HDR 与广色域；16-bit 声明真实精度。"] = "✅ Modular mode is best for screenshots, with HDR and wide-gamut support; 16-bit declares the real precision.",
            ["✅ 支持 HDR（PQ）与广色域；NVENC 硬件加速仅 4:2:0，软件可 4:4:4 与 12-bit。位深与色度按所选编码器生效。"] = "✅ Supports HDR (PQ) and wide gamut; NVENC acceleration is 4:2:0 only, software allows 4:4:4 and 12-bit. Bit depth and chroma follow the selected encoder.",
            ["⚠ 仅 8-bit，容器没有 HDR 通道；默认目标 Rec.709，显式选 Rec.2020 才输出 Rec.2020。适合简单分享。"] = "⚠ 8-bit only, and the container has no HDR channel; the default target is Rec.709 and Rec.2020 is written only when you pick it explicitly. Suited to casual sharing.",
            ["✅ TIFF 无损格式，支持 16-bit HDR + ICC 嵌入。适合存档。"] = "✅ Lossless TIFF, supports 16-bit HDR + embedded ICC. Suited to archiving.",
            ["✅ Ultra HDR (ISO 21496-1)。编码基准 = 系统 SDR 白点 "] = "✅ Ultra HDR (ISO 21496-1). Encoding reference = system SDR white point ",
            [" nits。兼容查看器（Chrome/照片）自动对齐；若查看器固定 203 nits，请手动把参考白度调到 "] = " nits. Compatible viewers (Chrome / Photos) align automatically; if yours is fixed at 203 nits, set the reference white manually to ",
        // ── 2026-10-03 英文界面补完 · 第二批：Core 质量文案（键见 MainWindow.CoreQualityTexts）──
            ["butteraugli 距离 (越小越清晰; 1.0 = 视觉无损)"] = "butteraugli distance (lower = sharper; 1.0 = visually lossless)",
            ["CRF (0=无损, 63=最低; 15 ≈ 视觉无损)"] = "CRF (0=lossless, 63=lowest; 15 ≈ visually lossless)",
            ["butteraugli 距离 (0.5-3.0)"] = "butteraugli distance (0.5-3.0)",
            ["质量 (50-100; 85+ 视觉无损)"] = "Quality (50-100; 85+ is visually lossless)",
            ["无损 (固定 100%)"] = "Lossless (fixed 100%)",
            [" (越小越清晰)"] = " (lower = sharper)",
            ["无损 (固定)"] = "Lossless (fixed)",
            ["距离: "] = "Distance: ",
            ["近无损"] = "Near-lossless",
            ["无损"] = "Lossless",
        // ── 2026-10-03 英文界面补完 · 第三批：7 个下拉的 35 个中文项（键取自 XAML 逐字）──
            ["Intel QSV / 系统 MFT"] = "Intel QSV / System MFT",
            ["LLM 翻译"] = "LLM Translation",
            ["智谱 GLM"] = "Zhipu GLM",
            ["⚙️ 系统"] = "⚙️ System",
            ["深灰半透明"] = "Translucent Dark Grey",
            ["深紫半透明"] = "Translucent Deep Purple",
            ["深绿半透明"] = "Translucent Dark Green",
            ["灰色半透明"] = "Translucent Grey",
            ["白色半透明"] = "Translucent White",
            ["纯黑半透明"] = "Translucent Black",
            ["阿里云百炼"] = "Alibaba Cloud Bailian",
            ["🖥️ 界面"] = "🖥️ Interface",
            ["免费翻译"] = "Free Translation",
            ["硅基流动"] = "SiliconFlow",
            ["视觉翻译"] = "Vision Translation",
            ["🌐 网络"] = "🌐 Network",
            ["💾 编码"] = "💾 Encoding",
            ["📷 捕获"] = "📷 Capture",
            ["深海蓝"] = "Deep Sea Blue",
            ["自定义"] = "Custom",
            ["信息"] = "Info",
            ["全部"] = "All",
            ["天蓝"] = "Sky Blue",
            ["橙红"] = "Orange Red",
            ["粉红"] = "Pink",
            ["紫色"] = "Purple",
            ["纯白"] = "Pure White",
            ["翠绿"] = "Emerald",
            ["自动"] = "Auto",
            ["警告"] = "Warning",
            ["调试"] = "Debug",
            ["金黄"] = "Gold",
            ["错误"] = "Error",
            ["青色"] = "Cyan",
        // ── 2026-10-03 英文界面补完 · 第三批附：AVIF 后端名与状态后缀（键见 MainWindow.CoreAvifBackendTexts）──
            ["avifenc（软件 · 由 avifenc 自选编码器）"] = "avifenc (software - codec chosen by avifenc)",
            ["（本机不可用）"] = " (not available on this machine)",
        // ── 2026-10-03 英文界面补完 · 第四批：色域映射卡的运行时拼串（R2）──
            [" —— 面板支持 HDR，系统未开启"] = " — panel supports HDR, not enabled in the system",
            ["8-bit 源 → ICC/矩阵搬到目标 → SDR 输出。"] = "8-bit source -> ICC/matrix to the target -> SDR output.",
            ["EDID 未读到原色（不猜）"] = "primaries not read from EDID (not guessed)",
            ["WGC Float16 → 色域矩阵 → PQ ST.2084。"] = "WGC Float16 -> gamut matrix -> PQ ST.2084.",
            ["WGC Float16 → 分段 Reinhard 出 SDR 底图，另算一张增益图；HDR 在增益图里，不在容器声明里。"] = "WGC Float16 -> piecewise Reinhard into the SDR base image, plus a separate gain map; the HDR lives in the gain map, not in the container declaration.",
            ["广色域源按目标色域映射后，色调映射为 SDR。"] = "The wide-gamut source is mapped into the target gamut, then tone-mapped to SDR.",
            ["→ HDR 直通（容器写 PQ）"] = "-> HDR passthrough (container writes PQ)",
            ["→ 增益图（SDR 底图 + gain map）"] = "-> gain map (SDR base + gain map)",
            ["→ 直通（同色域）"] = "-> passthrough (same gamut)",
            ["→ 色域映射到"] = "-> gamut mapped to",
            ["→ 色调映射为 SDR（sRGB 容器装不下 PQ）"] = "-> tone-mapped to SDR (an sRGB container cannot carry PQ)",
            ["→ 色调映射为 SDR（屏幕证据不授权 HDR 声称）"] = "-> tone-mapped to SDR (per-display evidence does not authorise an HDR claim)",
            ["→ 色调映射为 SDR（此格式没有 HDR 通道）"] = "-> tone-mapped to SDR (this format has no HDR channel)",
            ["本次按 SDR 色调映射：HDR 开关仍按你的设置保留，只是这块屏的逐屏证据不足以声明 PQ。"] = "Tone-mapped to SDR this run: the HDR switch is kept as you set it; it is only this panel's per-display evidence that is not enough to declare PQ.",
            ["本次按 SDR 色调映射：目标被解析/收口成 Rec.709，而 PQ 只能与 Rec.2020 原色一起声明。"] = "Tone-mapped to SDR this run: the target resolved/narrowed to Rec.709, and PQ can only be declared together with Rec.2020 primaries.",
            ["本次按 SDR 色调映射：这个格式的容器没有 HDR 通道（HDR 开关会落空，产物是 8-bit SDR）。"] = "Tone-mapped to SDR this run: this format's container has no HDR channel (the HDR switch has no effect; the output is 8-bit SDR).",
            ["源与目标同一套原色 ⇒ 矩阵是恒等变换，本次不跑搬色。"] = "Source and target share the same primaries => the matrix is an identity transform; no colour transfer runs this time.",
            ["读不到被截屏的逐屏证据 ⇒ 不授权广色域/HDR 声称（仍放行 Float16 源）"] = "per-display evidence of the captured screen unavailable => no wide-gamut/HDR claim authorised (Float16 sources still pass through)",
        // ── 2026-10-03 英文界面补完 · 第四批：色域映射卡的运行时拼串（R2）──
            ["HDR（{0}，PQ，面板原生 {1}）"] = "HDR ({0}, PQ, panel-native {1})",
            ["SDR sRGB（{0}）"] = "SDR sRGB ({0})",
            ["SDR 广色域（{0}，{1}，ACM）"] = "SDR wide gamut ({0}, {1}, ACM)",
            ["{0}（底图色域）"] = "{0} (base image gamut)",
            ["⚠ HDR 声称已被收口 ⇒ {0}"] = "⚠ The HDR claim was narrowed => {0}",
            ["⚠ 广色域声明已被逐屏证据收口为 Rec.709 ⇒ {0}"] = "⚠ The wide-gamut claim was narrowed to Rec.709 by per-display evidence => {0}",
            ["逐屏证据不足（{0}）⇒ 不授权广色域/HDR 声称"] = "Insufficient per-display evidence ({0}) => no wide-gamut/HDR claim authorised",
        // ── 2026-10-03 英文界面补完 · 第四批附：能力状态行（R2）──
            ["⚠ 此显示器不支持 HDR"] = "⚠ This display does not support HDR",
            ["⚠ HDR 硬件支持但未开启（Windows 显示设置中未开启 HDR）"] = "⚠ HDR is supported by the hardware but not enabled (not turned on in Windows display settings)",
            [" | ACM 已启用（系统管理显示器色彩）"] = " | ACM enabled (the system manages display colour)",
            ["ACM 已启用 — 像素会映射到所选目标色域（Rec.709 / Rec.2020）。"] = "ACM enabled - pixels are mapped to the selected target gamut (Rec.709 / Rec.2020).",
            ["未检测到校色 ICC；目标色域即像素与容器声明所用的原色。"] = "No calibrated ICC profile detected; the target gamut is the primaries used for both the pixels and the container declaration",
            ["✅ HDR 已启用（{0}-bit）"] = "✅ HDR enabled ({0}-bit)",
            [" | SDR 白点 {0} nits"] = " | SDR paper white {0} nits",
            [" | 峰值 {0} nits"] = " | peak {0} nits",
            ["检测到校色 ICC → 映射到 {0}"] = "Calibrated ICC detected -> mapped to {0}",
        // ── 2026-10-03 英文界面补完 · 第五批：Core FormatColorFacts 的声明通道文案（R2 收尾）──
            ["PNG：cICP {0}（原色/传输/矩阵），matrix 恒 0(identity) —— PNG 存 RGB 采样，没有 YUV 往返可声明。"] = "PNG: cICP {0} (primaries/transfer/matrix); matrix is always 0 (identity) - PNG stores RGB samples, so there is no YUV round-trip to declare.",
            ["HDR 一律不嵌 ICC。"] = "HDR never embeds ICC.",
            ["{0} 目标另嵌 iCCP，两条通道同一套原色。"] = "The {0} target additionally embeds iCCP; both channels carry the same primaries.",
            ["{0} 目标不嵌 ICC（cICP 已足够）。"] = "The {0} target embeds no ICC (cICP is enough).",
            ["AVIF：colr(nclx) {0}，matrix 随三原色取 {1} —— 它是 YUV 码流，"] = "AVIF: colr(nclx) {0}, matrix follows the primaries as {1} - it is a YUV bitstream, ",
            ["不能像 PNG 那样写 identity（写 0 时部分解码器自行套错矩阵）。"] = "so it cannot declare identity the way PNG does (with 0, some decoders apply the wrong matrix themselves).",
            ["HDR 不嵌 ICC。"] = "HDR embeds no ICC.",
            ["{0} 另可嵌 ICC。"] = "{0} may additionally embed ICC.",
            ["{0} 不嵌 ICC。"] = "{0} embeds no ICC.",
            ["JPEG XL：color fields 与 ICC 二选一（同时给时 cjxl 只走 ICC、fields 被丢弃；"] = "JPEG XL: colour fields or ICC, one of the two (given both, cjxl uses only ICC and drops the fields; ",
            ["ICC 能归约成字段就只写字段，归约不了就整份内嵌）。没有 matrix 这一维，"] = "if the ICC reduces to fields only the fields are written, otherwise the whole profile is embedded). There is no matrix dimension, ",
            ["且有损产物把像素量化进 XYB —— 声明写的是原图色域，不是码流里的通道值。"] = "and lossy output quantises pixels into XYB - the declaration states the source gamut, not the channel values in the bitstream.",
            ["HDR 走 Rec.2020 原色 + PQ（cjxl 的枚举名 Rec2100PQ，另带 intensity_target）。"] = "HDR uses Rec.2020 primaries + PQ (cjxl's enum name Rec2100PQ, plus intensity_target).",
            ["{0} 取到可嵌 ICC 时用 ICC，否则用 color fields。"] = "{0} uses ICC when an embeddable ICC is available, otherwise colour fields.",
            ["{0} 走 color fields，不嵌 ICC。"] = "{0} uses colour fields and embeds no ICC.",
            ["JPEG：只有 ICC 一条声明通道，没有代码点 —— "] = "JPEG: ICC is the only declaration channel, there are no code points - ",
            ["{0} 原色嵌 APP2/ICC（实测产物里 ICC_PROFILE 与 acsp 都在）。"] = "The {0} primaries are embedded as APP2/ICC (measured output has both ICC_PROFILE and acsp).",
            ["{0} 目标【什么都不写】，靠 JFIF 默认。"] = "The {0} target writes nothing at all, relying on the JFIF default.",
            // 2026-10-06：JPEG / WebP 的【默认目标就是 Rec.709】这条新口径，Core 侧劈成两句 L(…) 发出
            //（模板即运行时字符串，故这里必须逐字拆开抄，不能合成一句）。
            ["本格式的默认目标就是 Rec.709：跟随显示器也好、默认档也好，都不会自动升到 Rec.2020 —— "] = "This format's default target is Rec.709: neither Follow display nor the default tier ever auto-upgrades to Rec.2020 - ",
            ["只有用户把目标【显式】选成 Rec.2020 时才会写出 Rec.2020 原色。"] = "Rec.2020 primaries are written only when the user picks Rec.2020 [explicitly] as the target.",
            ["容器无 HDR 通道 ⇒ 开关会落空成 8-bit SDR；这与【能否声明 Rec.2020 原色】是两件事。"] = "The container has no HDR channel, so the switch falls back to 8-bit SDR; that is a separate question from whether Rec.2020 primaries can be declared.",
            ["WebP：同样只有 ICC 一条通道 —— "] = "WebP: likewise ICC is the only channel - ",
            ["{0} 原色经中转 PNG 的 iCCP 带过去，并给 cwebp 传 -metadata icc（实测产物 ICCP 块在，profile 2100 字节）。"] = "The {0} primaries are carried through the intermediate PNG's iCCP, and cwebp gets -metadata icc (measured output has the ICCP chunk, a 2100-byte profile).",
            ["{0} 目标【什么都不写】（实测对照组无 ICCP）。"] = "The {0} target writes nothing at all (the measured control group had no ICCP).",
            ["本格式的默认目标同样是 Rec.709：跟随显示器或默认档都只给 Rec.709，"] = "This format's default target is likewise Rec.709: Follow display and the default tier both give Rec.709 only, ",
            ["要 Rec.2020 得由用户【显式】指定 —— 与 JPEG 同一条规则，两族格式不再各自为政。"] = "and Rec.2020 has to be [explicitly] specified by the user - the same rule as JPEG, so the two families no longer go their own way.",
            ["无 HDR 通道 ⇒ HDR 开关落空成 8-bit SDR，但原色声明照常；缺 cwebp.exe 时整份产物会换成 PNG。"] = "No HDR channel, so the HDR switch falls back to 8-bit SDR, but the primaries declaration still applies; when cwebp.exe is missing the whole output is replaced by PNG.",
            ["TIFF：没有代码点通道 ⇒ 只靠 ICC，连 Rec.709 也老实嵌一份（不像 JPEG/WebP 能省）。"] = "TIFF: no code-point channel, so ICC only - and even Rec.709 is honestly embedded (unlike JPEG/WebP, which can omit it).",
            ["HDR 用带 PQ TRC 的 ICC 声明。"] = "HDR is declared by an ICC with a PQ TRC.",
            ["SDR 用标准色域 ICC。"] = "SDR uses the standard-gamut ICC.",
            ["JPEG（附带增益图）：底图【恒为 SDR】且【恒嵌】一份 ICC —— 目标是 {0} ⇒ 底图、增益比、"] = "JPEG (with gain map): the base image is always SDR and always carries an embedded ICC - with target {0} the base, the gain ratio and ",
            ["声明三件同一套原色；换目标就是换原色，不会把底图变成 HDR（HDR 由 XMP hdrgm + "] = "the declaration all share one set of primaries; changing the target changes the primaries, it never turns the base into HDR (HDR is carried by XMP hdrgm + ",
            ["ISO 21496-1 增益图段承载，容器不写 PQ ⇒ 与「目标不是 HDR 档」不冲突）。"] = "the ISO 21496-1 gain-map section, and the container writes no PQ, so this does not conflict with 'the target is not the HDR tier').",
            ["无码点"] = "no code point",
            ["未知格式：给不出声明通道。"] = "Unknown format: no declaration channel to give.",
        // ── 2026-10-03 第五批附：OCR 语言提示（R2 收尾）
            ["ℹ PP-OCRv6 是单模型、不随语言切换；中英文最准，日文需字高 ≥24px，韩文自动改走系统 OCR。"] = "ℹ PP-OCRv6 is a single model and does not follow the language setting; best on Chinese/English, Japanese needs glyph height ≥24px, Korean falls back to Windows OCR.",
        };

    /// <summary>这条中文有没有英文对照（= 是不是"界面写死、等着被翻译"的那批）。</summary>
    public static bool IsLocalizableLiteral(string? zh) =>
        zh is not null && ZhToEn.ContainsKey(zh);

    /// <summary>按当前语言取一写死中文的显示值；中文模式或表里没有时原样返回。</summary>
    public static string Tr(string zh) =>
        s_lang == AppLanguage.English && ZhToEn.TryGetValue(zh, out var en) ? en : zh;

    /// <summary>英文译文（用于判断"这条文字是不是本表翻译出来的"，从而允许来回切换语言）。</summary>
    public static string? EnOf(string zh) => ZhToEn.TryGetValue(zh, out var en) ? en : null;

    // ── 格式标签 ──
    // 只有这一处知道"某个格式在下拉里叫什么"；下拉的**顺序与数量**由 `_formats` 决定
    // （见 MainWindow.Localization.cs 里 GetFormatLabel 的调用点），这里不许再排一次队。
    public static string FmtPng => s_lang == AppLanguage.Chinese ? "PNG (无损)" : "PNG (Lossless)";
    // ⚠ 这一档对用户就叫 **JPEG**（2026-09-27 用户口径）：增益图已经并进来了，下拉里只有这一个
    //    JPEG 选项，"JPEG LI" 是编码器实现名（jpegli）与内部枚举名 —— 把它当格式名卖给用户，
    //    读起来就像界面上还有两种 JPEG。实现名留给代码、日志与判据消息。
    public static string FmtJpegLi => "JPEG";
    public static string FmtJpegXl => "JPEG XL";
    public static string FmtAvif => "AVIF";
    public static string FmtWebP => "WebP";
    public static string FmtTiff => "TIFF";

    /// <summary>
    /// 格式下拉的标签。<para>2026-09-26 删掉的两条：`FmtJpegGainMap`（增益图并入 JPEG LI 的开关，
    /// 不再是界面格式）、`FmtBmp`（BMP 只有 Core 侧编码器，界面与工厂都没有它）。</para>
    /// <para>默认分支给枚举名而不是空串：将来往 `_formats` 里加格式却忘了配标签，
    /// 界面上会看见一个陌生的英文枚举名 —— 那比一个空白项更容易被发现。</para>
    /// </summary>
    public static string GetFormatLabel(TrueToneCap.Core.Encoding.OutputFormat f) => f switch
    {
        TrueToneCap.Core.Encoding.OutputFormat.PNG => FmtPng,
        TrueToneCap.Core.Encoding.OutputFormat.JPEG_LI => FmtJpegLi,
        TrueToneCap.Core.Encoding.OutputFormat.JPEG_XL => FmtJpegXl,
        TrueToneCap.Core.Encoding.OutputFormat.AVIF => FmtAvif,
        TrueToneCap.Core.Encoding.OutputFormat.WebP => FmtWebP,
        TrueToneCap.Core.Encoding.OutputFormat.TIFF => FmtTiff,
        _ => f.ToString(),
    };

    // ── AVIF 后端 ──
    public static string AvifAuto => s_lang == AppLanguage.Chinese ? "自动检测" : "Auto Detect";
    public static string AvifLibAom => s_lang == AppLanguage.Chinese ? "libaom (软件)" : "libaom (Software)";
    public static string AvifQsv => "Intel QSV";
    public static string AvifNvenc => "NVIDIA NVENC";

    // ── AVIF 色度 ──
    // 2026-09-25（用户口径）：下拉项只留采样名。原先括号里写的"默认 · Auto 可走硬件加速"
    // "仅软件编码器"这类内容，运行时已由 AvifCodecHintTxt 那行灰字按【当前所选编码器】动态给出
    // （可用色度 X / 可用位深 Y + 降级真相），写在项上既重复又会在窄下拉里截断。
    // ⚠ 这三条必须与 MainWindow.xaml 里 AvifChromaCbo 的同 Tag 文本一致，
    //    一致性由 CorePipelineTests 的"默认色度"用例逐 Tag 对撞（运行时这条会覆盖 XAML）。
    public static string AvifChroma444 => "4:4:4";
    public static string AvifChroma422 => "4:2:2";
    public static string AvifChroma420 => "4:2:0";

    // ── GainMap 模式 ──
    // 取舍说明（体积 / 亮度还原）在灰字 GainMapHintTxt 里按所选模式动态给，不进项文字。
    public static string GmRgb => s_lang == AppLanguage.Chinese ? "RGB 增益" : "RGB Gain";
    public static string GmGray => s_lang == AppLanguage.Chinese ? "灰度增益" : "Gray Gain";

    // ── 色域映射 ──
    public static string GamutMapTitle => s_lang == AppLanguage.Chinese ? "色域映射" : "Gamut Mapping";
    public static string SourceGamut => s_lang == AppLanguage.Chinese ? "显示器色域" : "Display Gamut";
    public static string MappingArrow => s_lang == AppLanguage.Chinese ? "→ 映射到" : "→ Map to";
    public static string RenderingIntent => s_lang == AppLanguage.Chinese ? "渲染意图" : "Rendering Intent";

    // ── 色彩空间选项 ──
    // 2026-09-27 用户口径：可选的两档一律叫 Rec.709 / Rec.2020（Tag 仍是 "sRGB"/"BT2020"，
    // 那是 legacyTags 迁移与 SetComboByTag 的匹配键，不能跟着显示名一起改）。
    // Rec.709 这一档写进容器的是 primaries=1 + transfer=13(sRGB) —— 说清这件事是灰字的活，
    // 不塞进项文字（项文字只写身份，见 2026-09-25 那条决定）。
    // ⚠ 名字从 Core 那一处要（GetColorSpaceDisplayName），不在这里再抄一份字面量：
    //   下拉标签、卡片"→ 映射到"那行、灰字句子此前各自写死过一遍，三份随时会漂。
    // DisplayP3 / DCI_P3 / AdobeRGB 三项已从界面移除 ⇒ 它们的标签属性一并删掉，
    // 留着只会让人以为下拉还有这些档（Core 里的矩阵/ICC 能力仍然保留，那是另一回事）。
    public static string CsSystem => s_lang == AppLanguage.Chinese ? "跟随显示器" : "Follow Display";
    public static string CsSRgb => TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetColorSpaceDisplayName("sRGB");
    public static string CsBT2020 => TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetColorSpaceDisplayName("BT2020");

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
