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

    // ═══ 压缩强度三档（2026-10-07 用户口径）═══
    // 三档名按【用户视角的结果】命名（体积/均衡/速度），不按底层参数名 ——
    // 四家参数的方向与量纲都不同（avifenc 越小越慢、cjxl 越大越慢、cwebp 越大越慢、NVENC P 越大越快），
    // 照参数名翻译必然误导。
    public static string CompressionEffort => s_lang == AppLanguage.Chinese ? "压缩强度" : "Compression";
    public static string EffortSmallest => s_lang == AppLanguage.Chinese ? "最小体积（最慢）" : "Smallest file (slowest)";
    public static string EffortBalanced => s_lang == AppLanguage.Chinese ? "均衡（推荐）" : "Balanced (recommended)";
    public static string EffortFastest => s_lang == AppLanguage.Chinese ? "最快" : "Fastest";
    // ═══ 压缩强度三档的说明文案 ═══
    // ⚠ 一律经 Tr() 查表，不许在这里写第二份英文：
    //   本仓有一条判据「同一个中文只许有一种英文说法」，因为显示点各写一份英文时，
    //   切语言后"后写入的那份说了算"，另一份就变成永远不生效的死文案。
    //   我自己第一次就这么写了（属性里手写英文 + 表里另写一份），当场被那条判据抓住。
    public static string EffortDescJxl => Tr("三档只改变编码耗时（cjxl 的 effort），与上方的质量设置无关。实测 4K 截图：最小体积约 0.4~0.9 秒、均衡约 0.3~0.8 秒、最快约 0.3~0.6 秒。⚠ 本格式的档位主要影响速度而非体积：更高的 effort 对部分内容更小、对另一部分反而更大，因此最高档停在 e7，不提供更慢的档位。");
    public static string EffortDescAvif => Tr("只改变编码耗时，不影响质量设置。软件编码器（libaom）差异很大：实测 4K 下最小体积约 7.1 秒 / 93.7 KB、均衡约 0.3 秒 / 123.4 KB、最快约 0.1 秒 / 148.8 KB。最小体积档会同时启用 libaom 的 tune=ssim 以求更小。硬件编码（NVENC）三档都很快，差异小得多。");
    public static string EffortDescWebp => Tr("只改变编码耗时，不影响质量设置。WebP 的档位差异比 JPEG XL / AVIF 小：实测 4K 下三档约 0.4 / 0.7 / 1.0 秒，体积相差约两成。");
    public static string AvifPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    public static string JxlPngSuffix => s_lang == AppLanguage.Chinese ? "额外添加 .png 后缀来提高兼容性" : "Add .png suffix for compatibility";
    // 2026-09-26：GainMap 并入 JPEG LI ⇒ 独立卡片的标题（GainMapOptions）已删，
    // 开关本身的名字是 GainMapToggle，说明仍是 GainMapDesc（挂在开关下方）。
    public static string GainMapToggle => s_lang == AppLanguage.Chinese
        ? "附带增益图（输出 Ultra HDR 照片）" : "Attach gain map (Ultra HDR output)";
    public static string GainMapDesc => s_lang == AppLanguage.Chinese ? "底图恒为 SDR —— 这是 Ultra HDR 的结构，HDR 在增益图里，不在底图的声明里。上面「目标色域」决定底图与增益图共用哪套原色：Rec.709（默认）或 Rec.2020（只有亲手选才会，「跟随显示器」解析成广色域不触发）。这条口径不只管增益图：JPEG 与 WebP 两族格式的默认目标同样是 Rec.709，只有你亲手选 Rec.2020 才产出 Rec.2020。打开此开关时 HDR 输出锁定为开。" : "The base image is always SDR - that is the structure of Ultra HDR: the HDR lives in the gain map, not in the base image declaration. The Target gamut above picks the primaries shared by base and gain map: Rec.709 (default) or Rec.2020 (only an explicit choice does that; Follow display resolving to a wide gamut does not). This rule is not just about the gain map: plain JPEG and WebP also default to Rec.709, and only your explicit pick of Rec.2020 produces Rec.2020. While this is on, HDR output is locked to on.";
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
    public static string FontDesc => s_lang == AppLanguage.Chinese ? "界面字体仅影响应用外观，不影响截图像素。选择后立即生效；该字体未安装时界面会回落到默认字体。" : "Font only affects UI appearance, not captured pixels. Applies immediately; if the font is not installed the UI falls back to the default.";

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
            ["分段 Reinhard — 广色域自动感知缩限到目标色域，确保色彩准确。"] =
                "Segmented Reinhard — wide gamut is perceptually compressed into the target gamut so colours stay accurate.",
            ["自动模式优先使用实测可用的硬件加速；硬件编码器受色度/位深限制，不满足时会自动降级并提示。"] =
                "Auto prefers hardware encoders that were actually measured usable; hardware paths are limited by chroma/bit depth and fall back with a notice.",
            ["AVIF 位深"] = "AVIF bit depth",
            ["位深"] = "Bit depth",
            ["位深按所选编码器能力生效（软件 ≤12-bit、NVENC ≤10-bit、QSV 8-bit），不支持的档位已隐藏；HDR 至少 10-bit，软件编码器（libaom）在 HDR 下还可选 12-bit。8-bit 兼容性最广；>8-bit 仅在源为 Float16（HDR/高级颜色采集）时保留真精度，普通 8-bit 截图恒输出 8-bit。"] =
                "Bit depth follows the selected encoder (software ≤12-bit, NVENC ≤10-bit, QSV 8-bit); unsupported values are hidden. HDR needs at least 10-bit, and libaom also offers 12-bit under HDR. 8-bit has the widest compatibility; above 8-bit keeps real precision only when the source is Float16 (HDR / advanced-colour capture) — ordinary 8-bit screenshots always export 8-bit.",
            ["JPEG 选项"] = "JPEG options",
            // ── 换格式保底开关（2026-09-29 用户口径：接上保底 + 触发必须告知 + 设置里给开关）──
            // 界面里这两串中文是 XAML 写死的，靠这张表被扫描接管；漏一条就是英文界面下的中文残留。
            ["编码失败时改存为 PNG"] = "Fall back to PNG when an encoder fails",
            ["勾上：某一格式的编码器这次失败时，这张图改存成 PNG 并弹提示（格式变了、像素无损，比丢图好）。关掉：失败就明确报错，产物仍然是你选的格式——按后缀或格式名接下游流程时建议关掉。同一容器内的后端切换（如 AVIF 硬件失败转软件）不算换格式，不受这个开关影响。"] =
                "On: if an encoder fails, this shot is saved as PNG with a notice (the format changes, pixels stay lossless — better than losing it). Off: failures are reported as errors and the file stays in the format you picked — turn this off if downstream steps key off the extension or format name. Switching encoders inside the same container (e.g. AVIF hardware falling back to software) is not a format change and is unaffected.",
            ["采样越高色度越准、体积越大：4:4:4 不压缩色度，4:2:0 体积最小（截图里的文字边缘最容易看出差别）。增益图开与不开共用这一项。"] =
                "Higher subsampling means more accurate colour and larger files: 4:4:4 keeps chroma, 4:2:0 is smallest (text edges in screenshots show the difference first). Shared by the gain-map switch, on or off.",
            ["底图恒为 SDR —— 这是 Ultra HDR 的结构，HDR 在增益图里，不在底图的声明里。上面「目标色域」决定底图与增益图共用哪套原色：Rec.709（默认）或 Rec.2020（只有亲手选才会，「跟随显示器」解析成广色域不触发）。这条口径不只管增益图：JPEG 与 WebP 两族格式的默认目标同样是 Rec.709，只有你亲手选 Rec.2020 才产出 Rec.2020。打开此开关时 HDR 输出锁定为开。"] =
                "The base image is always SDR - that is the structure of Ultra HDR: the HDR lives in the gain map, not in the base image declaration. The Target gamut above picks the primaries shared by base and gain map: Rec.709 (default) or Rec.2020 (only an explicit choice does that; Follow display resolving to a wide gamut does not). This rule is not just about the gain map: plain JPEG and WebP also default to Rec.709, and only your explicit pick of Rec.2020 produces Rec.2020. While this is on, HDR output is locked to on.",
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
            ["本档开箱即可用：不填任何密钥，按 Edge → 必应 → MyMemory → Google 的顺序依次尝试免费公共端点（2026-10-23 国内实测前三条各 0.5–1.4 秒出整句译文，Google 在本机超时）。「LLM 翻译 / 视觉翻译」两档里自定义 LLM 排在它们之前。"] =
                "This mode works out of the box: with no key entered it tries the free public endpoints in order — Edge → Bing → MyMemory → Google (measured 2026-10-23 from a mainland-China network: each of the first three returns a full sentence in 0.5–1.4 s; Google timed out here). In the LLM / Vision modes the custom LLM runs before them.",
            ["使用免费公共翻译端点（识别到的文字会发给第三方）"] =
                "Use free public translation endpoints (recognized text is sent to third parties)",
            ["代价要说清，这条默认开着不是隐瞒：① 截图里的文字（可能含 token、账号、聊天记录）会发到微软 / MyMemory 的服务器；② 除 MyMemory 明写允许匿名调用外，其余几家都是各家网页与浏览器前端的自用接口，不是给我们用的公开 API，页面一改版就可能静默断掉；③ 各家限制不同——Edge 免凭据、必应要先抓页面凭据且约 1 小时过期、MyMemory 单条 500 字符且匿名档 5000 字符每自然日（一整屏文字就能用完）。不想外发就取消勾选，然后填下面的凭据，否则本档将无法翻译。"] =
                "The costs, stated plainly — this stays on by default and that is not a concealment: ① text from your screenshots (which may contain tokens, account names or chat history) is sent to Microsoft / MyMemory servers; ② apart from MyMemory, which explicitly permits anonymous calls, the others are each vendor's own web- and browser-frontend endpoints rather than public APIs meant for us, and they can break silently whenever a page changes; ③ the limits differ — Edge needs no credentials, Bing first scrapes page credentials that expire in about an hour, MyMemory allows 500 characters per request and 5000 characters per calendar day anonymously (one full screen of text uses it up). To keep text on this machine, untick this and fill in a credential below — otherwise this mode cannot translate.",
            ["有道 AppID"] = "Youdao AppID",
            ["可选：填了就优先走官方 API（更快更稳，留空则直接走上面的免费端点）"] =
                "Optional: with it filled the official API runs first (faster and steadier); leave empty to go straight to the free endpoints above",
            ["有道密钥"] = "Youdao secret",
            ["AppSecret — 仅本机 DPAPI 加密存储，不写进 settings.json"] =
                "AppSecret — stored only on this machine via DPAPI, never written to settings.json",
            ["凭据从哪来（可选，不是必需）：登录 ai.youdao.com → 控制台创建应用 → 复制「应用ID / 应用密钥」。官方文档写明新用户注册即赠 50 元体验金（常见语种 48 元/百万字符 ⇒ 约 104 万字符），先扣体验金、用完才扣余额；本程序未实测到账时效与是否需要完善实名信息。"] =
                "Where the credentials come from (optional — not required): sign in at ai.youdao.com → create an app in the console → copy the App ID / App secret. Official docs state new registrations receive ¥50 trial credit (common language pairs cost ¥48 per million characters ≈ 1.04 M characters), spent before any balance; this app has not measured credit timing or identity-verification requirements.",
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

            // ── 隐私政策（2026-10-07，商店政策 10.5.1 要求的产品内入口）──
            // 与上面三条同理：英文以 PrivacyPolicyWindow 的
            // CardTitleText / CardDescText / CardButtonText 与窗口内静态文字为准，
            // 表里与实机显示保持一致 ⇒ 英文措辞只有一处说了算。
            ["隐私政策"] = "Privacy policy",
            ["截图与文字识别均在本机完成。仅在你使用翻译功能时会向第三方发送内容——视觉翻译会上传整张截图，请阅读详情。"] =
                "Screenshots and OCR run entirely on this device. Content is sent to third parties only when you use translation — Vision translation uploads the whole screenshot.",
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
            ["这里选的是【目标色域】：像素搬去哪儿、容器就声明成什么，是同一个值。对增益图（JPEG 卡片里的开关）而言，它选的就是【底图】的色彩空间。至于某个格式靠什么通道把声明交给查看器（ICC / 代码点 / 什么都不写），下面「色域映射」按当前格式说。"] = "This selects the TARGET COLOUR SPACE: where pixels are moved to and what the container declares are the same value. For the gain map (the switch inside the JPEG card) it selects the BASE image colour space. Which channel each format uses to carry that declaration (ICC / code points / nothing) is shown per format under Gamut Mapping.",
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
            ["最小体积（最慢）"] = "Smallest file (slowest)",
            ["均衡（推荐）"] = "Balanced (recommended)",
            ["最快"] = "Fastest",
            ["三档只改变编码耗时（cjxl 的 effort），与上方的质量设置无关。实测 4K 截图：最小体积约 0.4~0.9 秒、均衡约 0.3~0.8 秒、最快约 0.3~0.6 秒。⚠ 本格式的档位主要影响速度而非体积：更高的 effort 对部分内容更小、对另一部分反而更大，因此最高档停在 e7，不提供更慢的档位。"] = "Only encoding time changes (cjxl effort); the quality setting above is unaffected. Measured at 4K: about 0.4-0.9 s / 0.3-0.8 s / 0.3-0.6 s for smallest / balanced / fastest. Note: these tiers mainly affect speed, not size - higher effort produces smaller files for some content and larger for others, so the top tier stops at e7 and no slower tier is offered.",
            ["只改变编码耗时，不影响质量设置。软件编码器（libaom）差异很大：实测 4K 下最小体积约 7.1 秒 / 93.7 KB、均衡约 0.3 秒 / 123.4 KB、最快约 0.1 秒 / 148.8 KB。最小体积档会同时启用 libaom 的 tune=ssim 以求更小。硬件编码（NVENC）三档都很快，差异小得多。"] = "Only encoding time changes; the quality setting is unaffected. The software encoder (libaom) differs a lot: measured at 4K about 7.1 s / 93.7 KB, 0.3 s / 123.4 KB, 0.1 s / 148.8 KB for smallest / balanced / fastest. The smallest-file tier also enables libaom's tune=ssim for extra density. Hardware (NVENC) is fast in all three tiers, with far smaller differences.",
            ["只改变编码耗时，不影响质量设置。WebP 的档位差异比 JPEG XL / AVIF 小：实测 4K 下三档约 0.4 / 0.7 / 1.0 秒，体积相差约两成。"] = "Only encoding time changes; the quality setting is unaffected. WebP's tiers differ less than JPEG XL / AVIF: measured at 4K about 0.4 / 0.7 / 1.0 s, with roughly a 20% size spread.",
            ["只改变编码耗时，不影响质量设置。实测 4K 截图：最小体积档约 0.16~0.28 秒、均衡约 0.10~0.17 秒；最小体积档体积小约 2%~5%（它改用 XYB 色彩空间，同样的观感质量下码率更低）。最快档与均衡档的产物相同（本格式没有更快的档位可用）。"] = "Only encoding time changes; the quality setting is unaffected. Measured at 4K: about 0.16-0.28 s for smallest, 0.10-0.17 s for balanced; the smallest-file tier is 2-5% smaller (it switches to the XYB colour space, which needs fewer bits at the same perceived quality). The fastest tier produces the same output as balanced (this format has no faster setting available).",
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
            ["界面字体仅影响应用外观，不影响截图像素。选择后立即生效；该字体未安装时界面会回落到默认字体。"] = "Font only affects UI appearance, not captured pixels. Applies immediately; if the font is not installed the UI falls back to the default.",
            ["通知"] = "Notifications",
            ["截图后提示"] = "Toast on capture",
            ["无感截图后提示"] = "Toast on silent capture",
            ["录制后提示"] = "Toast on recording",
            ["通知位置"] = "Notification position",
            ["行为"] = "Behavior",
            ["开机自动启动"] = "Start with Windows",
            ["关闭时最小化到托盘"] = "Minimize to tray on close",
            ["日志详细度"] = "Log verbosity",
            ["VS-Cap 模式（当前屏幕选区、编辑与贴图）"] = "VS-Cap Mode (on-screen selection, editing and pinning)",
            ["贴图剪贴板图片（Ctrl+Alt+T）"] = "Pin Clipboard Image (Ctrl+Alt+T)",
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
            ["✅ Google jpegli 编码，butteraugli 距离控制质量。8-bit、容器没有 HDR 通道；默认目标色域是 Rec.709 —— 「跟随显示器」即便解析成广色域也不改变这一点，只有亲手选 Rec.2020 才会输出 Rec.2020。Rec.2020 目标靠 ICC 声明（实测产物带 APP2/ICC），Rec.709 目标则【什么都不写】（靠 JFIF 默认）。"] = "✅ Encoded by Google jpegli, quality driven by butteraugli distance. 8-bit, and the container has no HDR channel; the default target gamut is Rec.709 — Follow display resolving to a wide gamut does not change that, and only an explicit pick of Rec.2020 outputs Rec.2020. A Rec.2020 target relies on the ICC declaration (measured output carries APP2/ICC), while a Rec.709 target writes [nothing at all] (falling back to the JFIF default).",
            ["✅ 新一代格式，Modular 模式对截图极优。支持 HDR (Rec.2100 PQ) + 广色域。16-bit 走 16-bit PNG 中转（cjxl 无 -b 参数，sBIT=16 声明实际精度）。"] = "✅ New-generation format; Modular mode is excellent for screenshots. Supports HDR (Rec.2100 PQ) + wide gamut. 16-bit goes through a 16-bit PNG hand-off (cjxl has no -b flag; sBIT=16 declares the real precision).",
            ["✅ 先进格式，支持 HDR（PQ）与广色域 SDR（Rec.2020 原色 + sRGB 传输，colr(nclx) 与 ICC 双标记）。NVENC 可硬件加速（AV1 仅 4:2:0），软件编码器支持 4:4:4 与 12-bit。位深与色度按所选编码器实际能力生效，不支持的组合已隐藏。"] = "✅ Advanced format supporting HDR (PQ) and wide-gamut SDR (Rec.2020 primaries + sRGB transfer, dual-marked by colr(nclx) and ICC). NVENC can hardware-accelerate it (AV1 is 4:2:0 only); the software encoders support 4:4:4 and 12-bit. Bit depth and chroma follow the selected encoder's real capability - unsupported combinations are hidden.",
            ["⚠ 仅 8-bit、容器没有 HDR 通道；默认目标色域是 Rec.709（不写声明）—— 「跟随显示器」即便解析成广色域也不改变这一点，只有亲手选 Rec.2020 才会输出 Rec.2020（靠 ICC 声明）。适合简单分享场景。"] = "⚠ 8-bit only, and the container has no HDR channel; the default target gamut is Rec.709 (no declaration written) — Follow display resolving to a wide gamut does not change that, and only an explicit pick of Rec.2020 outputs Rec.2020 (via the ICC declaration). Suited to casual sharing.",
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
            ["WGC Float16 捕获完整广色域 → 3×3 矩阵搬到目标 → 分段 Reinhard → SDR 输出。"] = "WGC Float16 captures the full wide gamut -> 3x3 matrix to the target -> piecewise Reinhard -> SDR output.",
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
            ["未检测到校色 ICC；目标色域即像素被搬进、容器被声明成的那一套原色（逐格式的声明通道见下方「色域映射」）"] = "No calibrated ICC profile detected; the target gamut is the primaries the pixels are moved into and the container declares (per-format declaration channels are in the Gamut Mapping card below)",
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
            ["ℹ PP-OCRv6 是单模型统一字典（18708 字），不随语言切换。实测中文/英文最准，日文需字高 ≥24px，韩文不支持（会自动改走 Windows OCR 兜底，需系统装韩文语言包）。"] = "ℹ PP-OCRv6 is a single model with one shared dictionary (18,708 glyphs) and does not follow the language setting. Measured best on Chinese/English; Japanese needs glyph height >= 24px; Korean is unsupported (it automatically falls back to Windows OCR, which requires the Korean language pack installed).",
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
