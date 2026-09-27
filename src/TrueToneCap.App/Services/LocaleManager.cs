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
    public static string AvifPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    public static string JxlPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    // 2026-09-26：GainMap 并入 JPEG LI ⇒ 独立卡片的标题（GainMapOptions）已删，
    // 开关本身的名字是 GainMapToggle，说明仍是 GainMapDesc（挂在开关下方）。
    public static string GainMapToggle => s_lang == AppLanguage.Chinese
        ? "附带增益图（输出 Ultra HDR 照片）" : "Attach gain map (Ultra HDR output)";
    public static string GainMapDesc => s_lang == AppLanguage.Chinese ? "底图恒为 SDR —— 这是 Ultra HDR 的结构，HDR 在增益图里，不在底图的声明里。上面「目标色域」决定底图与增益图共用哪套原色：Rec.709（默认）或 Rec.2020（只有亲手选才会，「跟随显示器」解析成广色域不触发）。打开此开关时 HDR 输出锁定为开。" : "The base image is always SDR - that is the structure of Ultra HDR: the HDR lives in the gain map, not in the base image declaration. The Target gamut above picks the primaries shared by base and gain map: Rec.709 (default) or Rec.2020 (only an explicit choice does that; Follow display resolving to a wide gamut does not). While this is on, HDR output is locked to on.";
    public static string GainMapModeLabel => s_lang == AppLanguage.Chinese ? "增益图模式" : "Gain Map Mode";

    // ═══ 2026-09-19：WebP 的「色度采样」下拉已撤掉，改为一句静态说明 ═══
    //   撤掉而非"修好接线"的原因是实测接不上：同一样张只改色度，420 与 444 产物字节完全相同、
    //   ffprobe 恒 yuv420p —— NativeWebPEncoder 没有色度形参，而有损 WebP 的格式定义就是 4:2:0
    //   （无损为 RGB）。所以这条文案说的是"为什么没有选项"，不是选项标签。
    public static string WebPChromaNote => s_lang == AppLanguage.Chinese
        ? "有损 WebP 恒 4:2:0（格式定义）；无损为 RGB。本格式无色度可选，也没有对应设置项。"
        : "Lossy WebP is always 4:2:0 (by format definition); lossless WebP is RGB. This format offers no chroma setting.";

    // ═══ 2026-09-19：PNG / TIFF / JPEG XL 三张位深卡片共用的实话（§4.3 双路径策略）═══
    //   编码行为按既定决策不改（下拉是真生效的），但"升位深不产生信息"必须出现在界面上。
    public static string BitDepthSourceNote => s_lang == AppLanguage.Chinese
        ? "8-bit 截图源不会升位深（升位深不产生信息）；高位深仅对 Float16/HDR 源有意义。"
        : "An 8-bit screenshot source is never given more bit depth (upscaling adds no information); high bit depth is only meaningful for Float16/HDR sources.";

    // ── 色彩页 ──
    public static string PageColor => s_lang == AppLanguage.Chinese ? "色彩设置" : "Color Settings";
    public static string HdrOutput => s_lang == AppLanguage.Chinese ? "HDR 输出" : "HDR Output";
    public static string IccBaking => s_lang == AppLanguage.Chinese ? "ICC 色彩管理" : "ICC Color Mgmt";
    public static string ColorSpace => s_lang == AppLanguage.Chinese ? "色彩空间" : "Color Space";
    public static string IccStrategy => s_lang == AppLanguage.Chinese ? "这里选的是【目标色域】：像素搬去哪儿、容器就声明成什么，是同一个值。对增益图（JPEG 卡片里的开关）而言，它选的就是【底图】的色彩空间。至于某个格式靠什么通道把声明交给查看器（ICC / 代码点 / 什么都不写），下面「色域映射」按当前格式说。" : "This selects the TARGET COLOUR SPACE: where pixels are moved to and what the container declares are the same value. For the gain map (the switch inside the JPEG card) it selects the BASE image colour space. Which channel each format uses to carry that declaration (ICC / code points / nothing) is shown per format under Gamut Mapping.";

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
        ? "简＝只记操作与降级；诊断＝再加每拍快照、回退原因与耗时；全＝逐帧逐子进程，仅排障时用。"
          + "日志写在应用目录 log/ 下按日轮转；换档只影响之后写入的行，诊断档以上量很大，排查完建议调回『简』。"
          + "任何档位都不会记录截图里的文字内容、图像字节或 API 密钥。"
        : "Basic = actions and fallbacks only; Diagnostic = adds per-tick snapshots, fallback reasons and timings; "
          + "Full = per frame and per subprocess, for debugging only. Logs rotate daily under <app dir>/log/. "
          + "A level change only affects lines written afterwards; switch back to Basic when you are done. "
          + "No level ever records recognized text, image bytes or API keys.";

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
    public static string FontDesc => s_lang == AppLanguage.Chinese ? "界面字体仅影响应用外观，不影响截图像素。选择已安装的字体后需重启应用生效。" : "Font only affects UI appearance, not captured pixels. Restart required after change.";

    // ── 状态栏 ──
    public static string Ready => s_lang == AppLanguage.Chinese ? "就绪" : "Ready";
    public static string AreaCapture => s_lang == AppLanguage.Chinese ? "区域截图" : "Area Capture";
    public static string SaveSettings => s_lang == AppLanguage.Chinese ? "保存设置" : "Save Settings";
    public static string CapabilityDetect => s_lang == AppLanguage.Chinese ? "能力检测中..." : "Detecting...";

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
            ["分段 Reinhard — 广色域自动感知缩限到目标色域，确保色彩准确。"] =
                "Segmented Reinhard — wide gamut is perceptually compressed into the target gamut so colours stay accurate.",
            ["自动模式优先使用实测可用的硬件加速；硬件编码器受色度/位深限制，不满足时会自动降级并提示。"] =
                "Auto prefers hardware encoders that were actually measured usable; hardware paths are limited by chroma/bit depth and fall back with a notice.",
            ["AVIF 位深"] = "AVIF bit depth",
            ["位深"] = "Bit depth",
            ["位深按所选编码器能力生效（软件 ≤12-bit、NVENC ≤10-bit、QSV 8-bit），不支持的档位已隐藏；HDR 至少 10-bit，软件编码器（libaom）在 HDR 下还可选 12-bit。8-bit 兼容性最广；>8-bit 仅在源为 Float16（HDR/高级颜色采集）时保留真精度，普通 8-bit 截图恒输出 8-bit。"] =
                "Bit depth follows the selected encoder (software ≤12-bit, NVENC ≤10-bit, QSV 8-bit); unsupported values are hidden. HDR needs at least 10-bit, and libaom also offers 12-bit under HDR. 8-bit has the widest compatibility; above 8-bit keeps real precision only when the source is Float16 (HDR / advanced-colour capture) — ordinary 8-bit screenshots always export 8-bit.",
            ["JPEG 选项"] = "JPEG options",
            ["采样越高色度越准、体积越大：4:4:4 不压缩色度，4:2:0 体积最小（截图里的文字边缘最容易看出差别）。增益图开与不开共用这一项。"] =
                "Higher subsampling means more accurate colour and larger files: 4:4:4 keeps chroma, 4:2:0 is smallest (text edges in screenshots show the difference first). Shared by the gain-map switch, on or off.",
            ["底图恒为 SDR —— 这是 Ultra HDR 的结构，HDR 在增益图里，不在底图的声明里。上面「目标色域」决定底图与增益图共用哪套原色：Rec.709（默认）或 Rec.2020（只有亲手选才会，「跟随显示器」解析成广色域不触发）。打开此开关时 HDR 输出锁定为开。"] =
                "The base image is always SDR - that is the structure of Ultra HDR: the HDR lives in the gain map, not in the base image declaration. The Target gamut above picks the primaries shared by base and gain map: Rec.709 (default) or Rec.2020 (only an explicit choice does that; Follow display resolving to a wide gamut does not). While this is on, HDR output is locked to on.",
            ["PNG 选项"] = "PNG options",
            ["JPEG XL 选项"] = "JPEG XL options",
            ["本格式没有 4:2:2：cjxl 只能整幅 1x1 或 2x2 降采样（不是只减色度），逐轴子采样仅在转码 JPEG 时可用 ⇒ 想要 4:2:2 请改用 JPEG 或 AVIF。"] =
                "This format has no 4:2:2: cjxl can only box-downsample the whole image (1x1 or 2x2), not the chroma alone; per-axis subsampling exists only when transcoding JPEG ⇒ use JPEG or AVIF for 4:2:2.",
            ["8-bit 截图源不会升位深（升位深不产生信息）；高位深仅对 Float16/HDR 源有意义。cjxl 没有独立的位深参数，位深由输入容器决定 ⇒ 10-bit 与 16-bit 都装在 16-bit 容器里，差别在 sBIT 标注与量化步长。"] =
                "An 8-bit screenshot source is never up-scaled (that adds no information); high bit depth matters only for Float16/HDR sources. cjxl has no separate bit-depth parameter — the input container decides, so 10-bit and 16-bit both live in a 16-bit container and differ in the sBIT annotation and quantisation step.",
            ["WebP 选项"] = "WebP options",
            ["TIFF 选项"] = "TIFF options",
            ["无感截图快捷键"] = "Silent capture hotkey",
            ["保存快捷键"] = "Save hotkey",
            ["取消快捷键"] = "Cancel hotkey",
            ["预览界面颜色"] = "Preview colour",
            ["覆盖遮罩颜色"] = "Overlay scrim colour",
            ["选区边框颜色"] = "Selection border colour",
            ["OCR 文字识别"] = "OCR",
            ["框选后在覆盖层点「识字 / 翻译」提取图中文字（不自动触发）。中文、英文最准；日文需字高 ≥24px；韩文不支持，会自动改走系统 OCR 兜底"] =
                "After a region selection, press Read / Translate in the overlay to extract text (never automatic). Best for Chinese and English; Japanese needs glyphs ≥24px; Korean is unsupported and falls back to Windows OCR",
            ["识别语言"] = "Recognition language",
            ["翻译模式"] = "Translation mode",
            ["选择翻译引擎，OCR 识别后的文字将被翻译为目标语言"] =
                "Pick the translation engine; OCR text is translated into the target language",
            ["模式"] = "Mode",
            ["本档顺序：有道智云开放平台（需自备 AppID/密钥）→ 非官方端点（必应网页版 / Google，默认不启用）。「LLM 翻译 / 视觉翻译」两档里自定义 LLM 排在有道之前，失败才降级到这条链。"] =
                "Order in this mode: Youdao Zhiyun open platform (bring your own AppID/secret) → unofficial endpoints (Bing web / Google, off by default). In the LLM / Vision translation modes the custom LLM runs before Youdao and falls back to this chain on failure.",
            ["有道 AppID"] = "Youdao AppID",
            ["智云开放平台 应用 ID（留空则跳过有道）"] = "Zhiyun open-platform app ID (leave empty to skip Youdao)",
            ["有道密钥"] = "Youdao secret",
            ["AppSecret — 仅本机 DPAPI 加密存储，不写进 settings.json"] =
                "AppSecret — stored only on this machine via DPAPI, never written to settings.json",
            ["凭据从哪来：登录 ai.youdao.com → 控制台创建应用 → 复制「应用ID / 应用密钥」。官方文档写明新用户注册即赠 50 元体验金（常见语种 48 元/百万字符 ⇒ 约 104 万字符），先扣体验金、用完才扣余额；本程序未实测到账时效与是否需要完善实名信息。"] =
                "Where the credentials come from: sign in at ai.youdao.com → create an app in the console → copy the App ID / App secret. Official docs state new registrations receive ¥50 trial credit (common language pairs cost ¥48 per million characters ≈ 1.04 M characters), spent before any balance; this app has not measured credit timing or identity-verification requirements.",
            ["允许回退到 Google 非官方翻译端点"] = "Allow fallback to unofficial Google translation endpoints",
            ["这些是各家网页前端的自用接口，不是公开 API：勾选即表示你接受截图里的文字会被发给第三方，且自行承担其服务条款风险。必应网页版免密钥、2026-09-21 国内实测能出整句译文，但它的凭据约 1 小时过期、页面一改版就断；Google 那两个端点同日实测本机连接超时。"] =
                "These are the vendors' own web-frontend endpoints, not public APIs: ticking this means you accept that text from your screenshots is sent to a third party and you carry the terms-of-service risk. Bing web needs no key and produced full-sentence translations in a 2026-09-21 test from a mainland network, but its token expires after about an hour and breaks whenever the page changes; both Google endpoints timed out on this machine the same day.",
            ["LLM 配置"] = "LLM settings",
            ["OCR 识别文字后，通过 LLM API 翻译为高质量译文"] =
                "After OCR, translate the text through an LLM API for higher-quality output",
            ["提供商"] = "Provider",
            ["提示词"] = "Prompt",
            ["留空使用默认翻译提示词..."] = "Leave empty for the default translation prompt…",
            ["字体名称"] = "Font name",
            ["推荐使用前四项（应用内浮层）：可控、肯定可见，且支持打开文件夹/文件/复制路径。「Windows 自带通知」依赖系统登记本应用，非打包运行时可能不显示；此时会自动回退为应用内浮层。"] =
                "The first four (in-app overlays) are recommended: controllable, always visible, and they offer open-folder / open-file / copy-path. “Windows native notifications” relies on the app being registered with the system and may not appear when running unpacked; it then falls back to an in-app overlay.",
            ["启用界面过渡动画（更流畅）"] = "Enable UI transition animations",
            ["日志写在应用目录 log/ 下按日轮转；换档只影响之后写入的行。诊断档以上量很大，排查完建议调回『简』。任何档位都不会记录截图里的文字内容、图像字节或 API 密钥。"] =
                "Logs are written under the app directory in log/, rotated daily; changing the level only affects lines written afterwards. Diagnostic and above are very verbose — switch back to Brief when you are done. No level ever records the text inside a screenshot, image bytes, or API keys.",
            // 这三条的英文以 ThirdPartyNoticesWindow 的 CardTitleText/CardDescText/CardButtonText
            // 为准（那份一直在界面上生效，表里原先写的 "licences" 是第二份真相且已被它压掉）：
            // 表改成与实机显示一致 ⇒ 英文措辞只有一处说了算（判据的歧义锁扫全部 App 源码）。
            ["第三方许可"] = "Third-party notices",
            ["随本应用一并再分发的第三方组件及其许可证全文。"] =
                "License texts of the third-party components redistributed with this app.",
            ["查看第三方许可"] = "View third-party notices",
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
            ["这里选的是【目标色域】：像素搬去哪儿、容器就声明成什么，是同一个值。对增益图（JPEG 卡片里的开关）而言，它选的就是【底图】的色彩空间。至于某个格式靠什么通道把声明交给查看器（ICC / 代码点 / 什么都不写），下面「色域映射」按当前格式说。"] = "This selects the TARGET COLOUR SPACE: where pixels are moved to and what the container declares are the same value. For the gain map (the switch inside the JPEG card) it selects the BASE image colour space. Which channel each format uses to carry that declaration (ICC / code points / nothing) is shown per format under Gamut Mapping.",
            ["色域映射"] = "Gamut Mapping",
            ["显示器色域"] = "Display Gamut",
            ["→ 映射到"] = "→ Map to",
            ["AVIF 选项"] = "AVIF Options",
            ["AVIF 编码器"] = "AVIF encoder",
            ["色度采样"] = "Chroma",
            ["额外添加 .png 后缀来提高兼容性"] = "Add .png suffix for compatibility",
            ["附带增益图（输出 Ultra HDR 照片）"] = "Attach gain map (Ultra HDR output)",
            ["增益图模式"] = "Gain Map Mode",
            ["8-bit 截图源不会升位深（升位深不产生信息）；高位深仅对 Float16/HDR 源有意义。"] = "An 8-bit screenshot source is never given more bit depth (upscaling adds no information); high bit depth is only meaningful for Float16/HDR sources.",
            ["有损 WebP 恒 4:2:0（格式定义）；无损为 RGB。本格式无色度可选，也没有对应设置项。"] = "Lossy WebP is always 4:2:0 (by format definition); lossless WebP is RGB. This format offers no chroma setting.",
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
            ["界面字体仅影响应用外观，不影响截图像素。选择已安装的字体后需重启应用生效。"] = "Font only affects UI appearance, not captured pixels. Restart required after change.",
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
            ["复制图像到剪贴板"] = "Copy image to clipboard",
            ["识别图中文字 (OCR)"] = "Recognise text in the image (OCR)",
            ["识字"] = "OCR",
            ["翻译图中文字"] = "Translate text in the image",
            ["取消选区 (Esc)"] = "Cancel selection (Esc)",
            ["取消"] = "Cancel",
            ["矩形 (R)"] = "Rectangle (R)",
            ["椭圆 (E)"] = "Ellipse (E)",
            ["箭头 (A)"] = "Arrow (A)",
            ["画笔 (P)"] = "Pen (P)",
            ["文字 (T)"] = "Text (T)",
            ["马赛克 (M)"] = "Mosaic (M)",
            ["完成标注 (Esc)"] = "Finish annotating (Esc)",
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
