// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/MainWindow.Localization.cs
// MainWindow 的「本地化 / 字体」职责（partial class 拆分）
//
// 拆分说明（2026-08-29）：MainWindow.xaml.cs 过大，按职责拆分为多个 partial class 文件。
// 本文件承载本地化文本应用与字体下拉框相关逻辑。
//
// ⚠ partial class 拆分不改变运行时行为：所有成员仍属同一个 MainWindow 类型，
//   私有字段（_settings / FontCbo 等）跨文件直接可见。

using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.App.Services;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>将本地化文本应用到所有 UI 元素（标签 + 下拉选项）。</summary>
    private void ApplyLocale()
    {
        var l = LocaleManager.CurrentLanguage == AppLanguage.English ? "en" : "zh";

        // ── 标签 ──
        // BrandTagline / PageOutputTitle / AvifCodecLabelTxt 这三格原先各自写着
        // `CurrentLanguage == English ? "English" : "中文"`，和 LocaleManager.ZhToEn 是同一句话的
        // 第二份真相，而且英文侧与表里不一致（"Output & Color" vs 表里 "Output & Colour"）：
        // 英文模式下构造期写的是三元那份，扫描器见当前值不在表里就放弃接管 ⇒ 表里那条英文是死文案。
        // 现在三格只由"表 + 可视树扫描"给（判据的歧义锁也已扩到全部 App 源码，见 §55.6）。
        NavOutput.Content = LocaleManager.NavOutput;
        NavCapture.Content = LocaleManager.NavCapture;
        NavAI.Content = LocaleManager.NavAI;
        NavSystem.Content = LocaleManager.NavSystem;
        NavLog.Content = LocaleManager.NavLog;

        BasicOutputTitle.Text = LocaleManager.BasicOutput;
        AvifOptionsTitle.Text = LocaleManager.AvifOptions;
        // ═══ 2026-09-18：「硬件编码」复选框已移除（与编码器下拉语义重复），本地化条目删除 ═══
        // 2026-09-26：GainMapOptionsTitle 已随独立卡片删除（增益图并入 JPEG LI 卡片，
        // 标题沿用该卡片的「JPEG LI 选项」，说明文字见 GainMapOptionsDesc）。
        GainMapEnableChk.Content = LocaleManager.GainMapToggle;
        GainMapOptionsDesc.Text = LocaleManager.GainMapDesc;
        GainMapModeLabelTxt.Text = LocaleManager.GainMapModeLabel;
        AvifPngSuffixChk.Content = LocaleManager.AvifPngSuffix;
        JxlPngSuffixChk.Content = LocaleManager.JxlPngSuffix;

        // ═══ 2026-09-19：WebP 色度下拉已撤掉，改为一句随语言刷新的静态说明 ═══
        // XAML 里的中文只是设计器兜底，运行时以这里为准 —— 否则切到英文界面仍是中文。
        if (WebPChromaNoteTxt is not null) WebPChromaNoteTxt.Text = LocaleManager.WebPChromaNote;
        // PNG / TIFF / JPEG XL 三张位深卡片共用同一句（§4.3 双路径策略：不改行为，只把事实写进界面）
        if (BdPngNoteTxt is not null) BdPngNoteTxt.Text = LocaleManager.BitDepthSourceNote;
        if (BdTiffNoteTxt is not null) BdTiffNoteTxt.Text = LocaleManager.BitDepthSourceNote;
        if (BdJpegXlNoteTxt is not null) BdJpegXlNoteTxt.Text = LocaleManager.BitDepthJxlNote;

        ColorSpaceLabel.Text = LocaleManager.ColorSpace;
        GamutMapTitle.Text = LocaleManager.GamutMapTitle;
        SourceGamutLabel.Text = LocaleManager.SourceGamut;
        IccStrategyHintTxt.Text = LocaleManager.IccStrategy;

        PageCaptureTitle.Text = LocaleManager.PageCapture;
        PreviewChk.Content = LocaleManager.PreviewThumb;

        PageSystemTitle.Text = LocaleManager.PageSystem;
        AppearanceTitle.Text = LocaleManager.Appearance;
        // 字体
        FontTitle.Text = LocaleManager.FontTitle;
        FontLabel.Text = LocaleManager.FontLabel;
        FontDesc.Text = LocaleManager.FontDesc;
        FontCustomTxt.PlaceholderText = LocaleManager.FontCustomPlaceholder;
        // 更新字体下拉选项默认项文本 (系统字体名称保持原样)
        foreach (ComboBoxItem item in FontCbo.Items)
        {
            var tag = item.Tag as string ?? "";
            if (tag == "") item.Content = LocaleManager.FontDefault;
        }
        BehaviorTitle.Text = LocaleManager.Behavior;
        AutoStartChk.Content = LocaleManager.AutoStart;
        MinimizeTrayChk.Content = LocaleManager.MinimizeTray;

        // ── 日志详细度：Tag 才是档位语义，这里只换显示文案 ──
        if (LogVerbosityLabel is not null) LogVerbosityLabel.Text = LocaleManager.LogVerbosity;
        if (LogVerbosityHint is not null) LogVerbosityHint.Text = LocaleManager.LogVerbosityHint;
        if (LogVerbosityCbo is not null)
        {
            foreach (var obj in LogVerbosityCbo.Items)
            {
                if (obj is not ComboBoxItem item) continue;
                item.Content = (item.Tag as string) switch
                {
                    "Diagnose" => LocaleManager.LogVerbosityDiagnose,
                    "Trace" => LocaleManager.LogVerbosityFull,
                    _ => LocaleManager.LogVerbosityBrief,
                };
            }
        }

        // ── 第三方许可入口 ──
        // 文案的单一事实源在 ThirdPartyNoticesWindow（同一句中英在这里和窗口里不会分叉）：
        // LocaleManager 不在本次改动范围内，故不往里加条目，改由窗口侧暴露同组属性。
        if (LicensesTitle is not null) LicensesTitle.Text = ThirdPartyNoticesWindow.CardTitleText;
        if (LicensesDescTxt is not null) LicensesDescTxt.Text = ThirdPartyNoticesWindow.CardDescText;
        if (OpenLicensesBtn is not null) OpenLicensesBtn.Content = ThirdPartyNoticesWindow.CardButtonText;

        // ── Toast 通知本地化 ──
        ToastTitle.Text = LocaleManager.ToastTitle;
        ToastCaptureChk.Content = LocaleManager.ToastOnCapture;
        ToastSilentChk.Content = LocaleManager.ToastOnSilent;
        ToastRecordChk.Content = LocaleManager.ToastOnRecording;
        ToastPositionLabel.Text = LocaleManager.ToastPositionLabel;
        // 更新 Toast 位置下拉选项文本
        SetComboItemText(ToastPositionCbo, "BottomRight", LocaleManager.ToastPosBottomRight);
        SetComboItemText(ToastPositionCbo, "TopRight", LocaleManager.ToastPosTopRight);
        SetComboItemText(ToastPositionCbo, "TopLeft", LocaleManager.ToastPosTopLeft);
        SetComboItemText(ToastPositionCbo, "BottomLeft", LocaleManager.ToastPosBottomLeft);
        SetComboItemText(ToastPositionCbo, "WindowsNotify", LocaleManager.ToastPosWindows);

        StatusTxt.Text = LocaleManager.Ready;
        CaptureBtn.Content = LocaleManager.AreaCapture;
        SaveSettingsBtn.Content = LocaleManager.SaveSettings;

        // ── 格式下拉：只调用唯一的构建口，本地化【不】再持有任何清单 ──
        // 这里过去另抄了一份 7 项标签数组去覆盖 ItemsSource，于是 §52 把增益图从 `_formats`
        // 摘掉之后整列错位：标签「JPEG Gain Map (HDR)」编 JPEG LI、「JPEG LI」编 JPEG XL、
        // 「JPEG XL」编 AVIF、「AVIF」编 WebP、「WebP」编 TIFF、末尾「BMP」被钳成 TIFF，
        // 真正的 TIFF 选不到 —— 7 个可见项里 5 个"点谁都不是谁"，且构造期就生效，不是切语言才犯。
        // 现在项序/项数/标签全部由 MainWindow.RebuildFormatCombo() 一处决定，
        // 并在那里自带一次显示层↔编码层的一致性自检（错位会在日志里留 Error）。
        RebuildFormatCombo();

        // ── 硬编码 ComboBox Items：按 Tag 匹配更新 Content ──
        // ═══ 2026-09-16 重构：单一「AVIF 编码器」下拉（原"编码后端"下拉已删除）═══
        // 各项 Content 由 UpdateAvifEncoderLabels() 依据能力表动态生成（含可用性与色度/位深）。

        SetComboItemText(AvifChromaCbo, "444", LocaleManager.AvifChroma444);
        // 2026-09-26：GainMap 色度的标签与三档文案已随控件一并删除（并入 JPEG LI 后共用那一条下拉）。
        SetComboItemText(AvifChromaCbo, "422", LocaleManager.AvifChroma422);
        SetComboItemText(AvifChromaCbo, "420", LocaleManager.AvifChroma420);

        SetComboItemText(GainMapModeCbo, "Rgb", LocaleManager.GmRgb);
        SetComboItemText(GainMapModeCbo, "Gray", LocaleManager.GmGray);

        // 下拉里只剩这三档（2026-09-19 收窄、2026-09-27 改名 Rec.709/Rec.2020）。
        // DisplayP3 / DCI_P3 / AdobeRGB 那三行 SetComboItemText 已删 —— 它们找的 Tag 在 XAML 里
        // 早就不存在，SetComboItemText 找不到就静默返回 ⇒ 留着是"看起来还在维护六档"的假象。
        SetComboItemText(ColorCbo, "System", LocaleManager.CsSystem);
        SetComboItemText(ColorCbo, "sRGB", LocaleManager.CsSRgb);
        SetComboItemText(ColorCbo, "BT2020", LocaleManager.CsBT2020);

        SetComboItemText(AnimFormatCbo, "GIF", LocaleManager.AnimGif);
        SetComboItemText(AnimFormatCbo, "APNG", LocaleManager.AnimApng);
        SetComboItemText(AnimFormatCbo, "AVIF", LocaleManager.AnimAvif);

        SetComboItemText(RecordFpsCbo, "10", "10 fps");    // FPS values are universal
        SetComboItemText(RecordFpsCbo, "15", "15 fps");
        SetComboItemText(RecordFpsCbo, "20", "20 fps");
        SetComboItemText(RecordFpsCbo, "30", "30 fps");

        SetComboItemText(MaxDurationCbo, "15", LocaleManager.Dur15);
        SetComboItemText(MaxDurationCbo, "30", LocaleManager.Dur30);
        SetComboItemText(MaxDurationCbo, "60", LocaleManager.Dur60);
        SetComboItemText(MaxDurationCbo, "120", LocaleManager.Dur120);

        SetComboItemText(ThemeCbo, "Default", LocaleManager.ThemeDefault);
        SetComboItemText(ThemeCbo, "Light", LocaleManager.ThemeLight);
        SetComboItemText(ThemeCbo, "Dark", LocaleManager.ThemeDark);
        SetComboItemText(ThemeCbo, "OLED", LocaleManager.ThemeOled);

        // LanguageCbo: "zh" ↔ "Chinese", "en" ↔ "English"
        SetComboItemText(LanguageCbo, "zh", LocaleManager.LangChinese);
        SetComboItemText(LanguageCbo, "en", LocaleManager.LangEnglish);

        // ═══ 2026-09-15 修复：Tag 必须与 XAML 中的实际取值一致 ═══
        // 原先写 "Auto"/"Gpu"/"Windows"/"Cpu"，而 MainWindow.xaml:325-327 的
        // ComboBoxItem Tag 实为 "OnnxGpu"/"OnnxCpu"/"WindowsOcr" ⇒ 一条都匹配不到，
        // SetComboItemText 静默 no-op ⇒ 切到英文后这三个选项仍显示中文硬编码文本。
        // 另："Auto" 在 XAML 中本就不存在（下拉只有三项），故移除。
        SetComboItemText(OcrEngineCbo, "OnnxGpu", LocaleManager.OcrGpu);
        SetComboItemText(OcrEngineCbo, "OnnxCpu", LocaleManager.OcrCpu);
        SetComboItemText(OcrEngineCbo, "WindowsOcr", LocaleManager.OcrWindows);

        SetComboItemText(LlmModelCbo, "gpt-4o-mini", LocaleManager.LlmGpt4oMini);
        SetComboItemText(LlmModelCbo, "deepseek-chat", LocaleManager.LlmDeepSeek);
        SetComboItemText(LlmModelCbo, "gpt-4.1-mini", LocaleManager.LlmGpt41Mini);
        SetComboItemText(LlmModelCbo, "custom", LocaleManager.LlmCustom);

        SetComboItemText(TargetLangCbo, "zh-CN", LocaleManager.TlChinese);
        SetComboItemText(TargetLangCbo, "en", LocaleManager.TlEnglish);
        SetComboItemText(TargetLangCbo, "ja", LocaleManager.TlJapanese);

        // "跟随系统" 这一项在 OcrLanguage 两份列表里都不存在 ⇒ 原先这条 Tag 传空串，
        // 永远匹配不到任何项（静默死调用）。要支持它得先加语言项，不是先加翻译。
        SetComboItemText(OcrLangCbo, "zh-en", LocaleManager.OlMixed);
        SetComboItemText(OcrLangCbo, "zh-Hans-CN", LocaleManager.TlChinese);   // 原写 "zh-Hans"：两份列表里都没有这个 ID
        SetComboItemText(OcrLangCbo, "en-US", LocaleManager.TlEnglish);

        // 归档模式
        if (ArchiveModeCbo is not null)
        {
            SetComboItemText(ArchiveModeCbo, "Year", LocaleManager.ArchiveYear);
            SetComboItemText(ArchiveModeCbo, "Month", LocaleManager.ArchiveMonth);
            SetComboItemText(ArchiveModeCbo, "Day", LocaleManager.ArchiveDay);
        }


        // ── 2026-10-03 英文界面补完 · 第三批：过去一次都没接线的 7 个下拉 ──
        // BorderColorCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(BorderColorCbo, "#FF4488FF", LocaleManager.Tr("天蓝"));
        SetComboItemText(BorderColorCbo, "#FF00FF88", LocaleManager.Tr("翠绿"));
        SetComboItemText(BorderColorCbo, "#FFFF6644", LocaleManager.Tr("橙红"));
        SetComboItemText(BorderColorCbo, "#FFFF44AA", LocaleManager.Tr("粉红"));
        SetComboItemText(BorderColorCbo, "#FFFFCC00", LocaleManager.Tr("金黄"));
        SetComboItemText(BorderColorCbo, "#FFAA66FF", LocaleManager.Tr("紫色"));
        SetComboItemText(BorderColorCbo, "#FFFFFFFF", LocaleManager.Tr("纯白"));
        SetComboItemText(BorderColorCbo, "#FF00FFCC", LocaleManager.Tr("青色"));

        // OverlayColorCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(OverlayColorCbo, "#80000000", LocaleManager.Tr("灰色半透明"));
        SetComboItemText(OverlayColorCbo, "#99000000", LocaleManager.Tr("纯黑半透明"));
        SetComboItemText(OverlayColorCbo, "#991A1A2E", LocaleManager.Tr("深紫半透明"));
        SetComboItemText(OverlayColorCbo, "#990D1B2A", LocaleManager.Tr("深海蓝"));
        SetComboItemText(OverlayColorCbo, "#992D2D2D", LocaleManager.Tr("深灰半透明"));
        SetComboItemText(OverlayColorCbo, "#80FFFFFF", LocaleManager.Tr("白色半透明"));
        SetComboItemText(OverlayColorCbo, "#99003322", LocaleManager.Tr("深绿半透明"));

        // LogCategoryCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(LogCategoryCbo, "All", LocaleManager.Tr("全部"));
        SetComboItemText(LogCategoryCbo, "System", LocaleManager.Tr("⚙️ 系统"));
        SetComboItemText(LogCategoryCbo, "Capture", LocaleManager.Tr("📷 捕获"));
        SetComboItemText(LogCategoryCbo, "Encoding", LocaleManager.Tr("💾 编码"));
        SetComboItemText(LogCategoryCbo, "UI", LocaleManager.Tr("🖥️ 界面"));
        SetComboItemText(LogCategoryCbo, "Network", LocaleManager.Tr("🌐 网络"));

        // LogFilterCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(LogFilterCbo, "All", LocaleManager.Tr("全部"));
        SetComboItemText(LogFilterCbo, "Debug", LocaleManager.Tr("调试"));
        SetComboItemText(LogFilterCbo, "Info", LocaleManager.Tr("信息"));
        SetComboItemText(LogFilterCbo, "Warning", LocaleManager.Tr("警告"));
        SetComboItemText(LogFilterCbo, "Error", LocaleManager.Tr("错误"));

        // LlmProviderCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(LlmProviderCbo, "GLM", LocaleManager.Tr("智谱 GLM"));
        SetComboItemText(LlmProviderCbo, "SiliconFlow", LocaleManager.Tr("硅基流动"));
        SetComboItemText(LlmProviderCbo, "Aliyun", LocaleManager.Tr("阿里云百炼"));
        SetComboItemText(LlmProviderCbo, "Custom", LocaleManager.Tr("自定义"));

        // TranslationModeCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(TranslationModeCbo, "Free", LocaleManager.Tr("免费翻译"));
        SetComboItemText(TranslationModeCbo, "LLM", LocaleManager.Tr("LLM 翻译"));
        SetComboItemText(TranslationModeCbo, "Vision", LocaleManager.Tr("视觉翻译"));

        // ═══ 压缩强度三档（2026-10-07；PNG / TIFF 于 2026-10-29 加入这一组）═══
        // 每张卡片各有自己的一份，且卡片本身按格式显隐（Collapsed 时项不在可视树里）
        // ⇒ 与上面几个下拉同理：必须按 Tag 逐个点名，不能指望扫描器。
        // ⚠ 这些下拉共用同一组 Tag/文案，所以循环里是一次写六遍 —— 有意为之：
        //   一旦将来某个格式的档位含义分化（例如 WebP 只给两档），各自的文字就能独立改。
        // 两档格式（PNG/TIFF/WebP/JPEG LI）：均衡（默认，= 参数拉满）+ 快速
        foreach (var effortCbo in new[] { EffortPngCbo, EffortTiffCbo, EffortWebpCbo, EffortJpegLiCbo })
        {
            if (effortCbo is null) continue;
            SetComboItemText(effortCbo, "Smallest", LocaleManager.Tr("均衡"));
            SetComboItemText(effortCbo, "Quick", LocaleManager.Tr("快速"));
        }
        // 三档格式（AVIF/JPEG XL）：极高 / 均衡（默认）/ 快速
        foreach (var effortCbo in new[] { EffortAvifCbo, EffortJpegXlCbo })
        {
            if (effortCbo is null) continue;
            SetComboItemText(effortCbo, "Smallest", LocaleManager.Tr("极高"));
            SetComboItemText(effortCbo, "Balanced", LocaleManager.Tr("均衡"));
            SetComboItemText(effortCbo, "Fastest", LocaleManager.Tr("快速"));
        }

        // AvifCodecCbo：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。
        SetComboItemText(AvifCodecCbo, "Auto", LocaleManager.Tr("自动"));
        // Tag="Qsv" 那一项【刻意不在这里翻】：UpdateAvifEncoderLabels() 会按能力表重写每一个
        // 非 Auto 项的 Content（Auto 被它显式跳过），这里写了也会被盖掉 ⇒ 是死写。
        // 后端名与"（本机不可用）"后缀改由 CoreAvifBackendTexts + CoreText 在那一处翻。
        // 11:57 的 UIA 展开读数里这一项因 HDR 不支持被收起（5 项只数到 4），所以静态那关
        // 与运行时都没暴露它——是读代码读出来的，不是量出来的。

        // ── 色域映射卡片（含本文件刚写下的 Localization 文案，必须最后刷一次覆盖）──
        RefreshColorMappingUi();

        // ── XAML 里写死的中文：按 LocaleManager.ZhToEn 扫一遍可视树（2026-09-26，交接 §55）──
        // 放在所有显式赋值之后：显式接线的控件已经被正确设置，扫描只负责剩下那批没人接的。
        ApplyLocaleTreeSafely(Content);
    }

    /// <summary>
    /// 可视树扫描的入口，包一层 try：本地化不是正确性路径，
    /// 任何一颗奇怪的树（null/未实化/自定义控件内部结构）都不该让设置页打不开。
    /// 但"英文模式一条都没翻到"必须是可见的 —— 那等于英文界面不可信，记 Warn。
    /// </summary>
    private static void ApplyLocaleTreeSafely(DependencyObject? root)
    {
        int touched;
        try { touched = ApplyLocaleTree(root); }
        catch (System.Exception ex)
        {
            LogService.Warn("Locale", $"可视树本地化扫描中断（已翻 {LocaleManager.CurrentLanguage} 模式）：{ex.Message}");
            return;
        }
        if (LocaleManager.CurrentLanguage != AppLanguage.English) return;
        // 树还没实化时"命中 0 处"是必然的，不是信号 —— 构造期那一次扫描按定义扫不到东西，
        // 每次都记一条 Warn 就等于养出一条没人读的告警。只在 IsLoaded 之后才报。
        if (root is not Microsoft.UI.Xaml.FrameworkElement fe || !fe.IsLoaded) return;
        // 英文模式下的两条运行时读数 —— 只记录，不改判决（门禁的静态判据管不到"只有跑起来才知道"的树）：
        // · 命中 0 处：根不对，此时英文界面不可信；
        // · 残留 N 处：某个扫得到的控件仍是中文 = 用户看得见的漏翻（对照表缺条目 / 扫描面缺控件）。
        if (touched == 0 && LocaleManager.ZhToEn.Count > 0)
            LogService.Warn("Locale",
                $"英文模式扫描命中 0 处（对照表 {LocaleManager.ZhToEn.Count} 条）"
                + " ⇒ 页面已实化却一条没翻，英文界面此时不可信");

        s_residueNames.Clear();
        frameworkOwned = 0;
        int residue = ApplyLocaleCollect(root, onlyResidue: true);
        if (residue > 0)
            LogService.Warn("Locale",
                $"英文模式下仍有 {residue} 处【我们自己的】控件显示中文 ⇒ 对照表缺条目或扫描面缺控件；"
                + "残留清单（最多列 8 条）：" + string.Join(" | ", s_residueNames.Take(8)));
        else if (frameworkOwned > 0)
            LogService.Info("Locale",
                $"另有 {frameworkOwned} 处是框架自带、随 OS 显示语言取词（更多/开/关），"
                + "与本应用的英文设置无关，不计入残留");
    }

    private static readonly System.Text.RegularExpressions.Regex s_cjk =
        new("[\u4e00-\u9fff]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 框架自带、随 <b>OS 显示语言</b> 取词的控件文字：CommandBar 的溢出按钮「更多」、
    /// ToggleSwitch 默认的「开 / 关」。全仓 XAML/.cs 里搜不到这三串（15:16 实测），
    /// 它们是 WinUI 自己资源表里的词 —— 本应用的 AppLanguage 与 OS 显示语言是两件事，
    /// 所以这几处【不可能】靠对照表清掉。混进残留计数就等于养出一条永远读不到底的告警，
    /// 故单独计数、单独说明；真漏翻（我们自己的串）才是那条 Warn。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> s_frameworkOwnedText =
        new(System.StringComparer.Ordinal) { "更多", "开", "关" };

    /// <summary>本轮扫到的残留控件身份，只给日志用（不参与判决）。</summary>
    private static readonly System.Collections.Generic.List<string> s_residueNames = new();
    private static int frameworkOwned;

    private static int ApplyLocaleCollect(DependencyObject? root, bool onlyResidue)
    {
        if (root is null) return 0;
        int n = 0;
        if (onlyResidue)
        {
            // 必须与 LocalizeElement 用同一个匹配规则：这里原先写的是精确类型相等，
            // 于是声明在上限类型 FrameworkElement 上的 ToolTip 一格都不算 ⇒
            // 残留计数恰好漏掉"副窗口 48 处悬停说明"这一整类，报出来的 N 是假的低。
            foreach (var slot in SweepSurface)
            {
                if (!MatchesSurface(root, slot.Control)) continue;
                var txt = slot.Get(root) ?? "";
                if (!s_cjk.IsMatch(txt)) continue;
                if (s_frameworkOwnedText.Contains(txt)) { frameworkOwned++; continue; }
                n++;
                // 光报一个 N 没法修：过去每一轮都要再人肉找一遍"到底是哪 5 处"。
                // 这里顺手把控件类型 + 属性 + 前 40 字带进日志，N 与清单同源于同一张扫描面。
                s_residueNames.Add($"{root.GetType().Name}.{slot.Property} = 「{(txt.Length > 40 ? txt[..40] + "…" : txt)}」");
                break;
            }
        }
        int children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
            n += ApplyLocaleCollect(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), onlyResidue);
        return n;
    }

    /// <summary>每个元素首次见到的"XAML 原文"，按属性名分槽（TextBox 同时有 Header 与 PlaceholderText）。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        DependencyObject, System.Collections.Generic.Dictionary<string, string>> s_xamlOriginal = new();

    private static int ApplyLocaleTree(DependencyObject? root)
    {
        if (root is null) return 0;
        int touched = LocalizeElement(root) ? 1 : 0;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            touched += ApplyLocaleTree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return touched;
    }

    /// <summary>
    /// 扫描面：一行 = （XAML 里出现的控件类型，它承载文字的属性）。运行时扫描和门禁判据
    /// <c>Test_Xaml_HardcodedChineseIsLocalized</c> 读的就是这同一张表。
    /// <para>原先这里写的是 <c>switch</c> 类型模式，有两个没人管的坑：
    /// ① NumberBox 继承 TextBox ⇒ 编译器只能报 CS8510 让我手调臂序；
    /// ② 表外的控件类型（当时就有 NavigationViewItem，5 个导航项）静默落到 <c>_ =&gt; false</c>，
    /// 于是"对照表里有译文、扫描器根本不看这个控件"，英文界面留中文而判据照绿。</para>
    /// <para>改成声明表之后：① 这类坑不存在（没有臂序可言）；② 新控件必须在表里显式加一行才会被扫，
    /// 漏加的结果是门禁报红，不是静默不翻。匹配规则见 <see cref="MatchesSurface"/>。</para>
    /// </summary>
    private static readonly (System.Type Control, string Property,
        System.Func<DependencyObject, string?> Get, System.Action<DependencyObject, string> Set)[] SweepSurface =
    {
        (typeof(TextBlock), nameof(TextBlock.Text),
            el => ((TextBlock)el).Text, (el, v) => ((TextBlock)el).Text = v),
        (typeof(CheckBox), "Content",
            el => ((CheckBox)el).Content as string, (el, v) => ((CheckBox)el).Content = v),
        (typeof(RadioButton), "Content",
            el => ((RadioButton)el).Content as string, (el, v) => ((RadioButton)el).Content = v),
        // 副窗口（标注 / OCR 预览 / 选区覆盖层 / 缩略图 toast / 第三方许可）的按钮正文是
        // <Button><TextBlock Text="…"/></Button> 这种"裸文字子元素"形态（TextBlock.Text 那行负责），
        // ToolTip 则全写在 XAML 上 ⇒ 这一项缺了，那几个窗口的提示条英文模式全是中文。
        // 用 ToolTipService 的附加属性读写：任何 FrameworkElement 都能挂 ToolTip，
        // 扫的是运行时元素类型，不再区分 ButtonBase 还是别的。
        (typeof(FrameworkElement), "ToolTipService.ToolTip",
            el => Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(el) as string,
            (el, v) => Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(el, v)),
        (typeof(Button), "Content",
            el => ((Button)el).Content as string, (el, v) => ((Button)el).Content = v),
        (typeof(Microsoft.UI.Xaml.Controls.NavigationViewItem), "Content",
            el => ((Microsoft.UI.Xaml.Controls.NavigationViewItem)el).Content as string,
            (el, v) => ((Microsoft.UI.Xaml.Controls.NavigationViewItem)el).Content = v),
        (typeof(ToggleSwitch), "Header",
            el => ((ToggleSwitch)el).Header as string, (el, v) => ((ToggleSwitch)el).Header = v),
        (typeof(TextBox), "Header",
            el => ((TextBox)el).Header as string, (el, v) => ((TextBox)el).Header = v),
        (typeof(TextBox), nameof(TextBox.PlaceholderText),
            el => ((TextBox)el).PlaceholderText, (el, v) => ((TextBox)el).PlaceholderText = v),
        (typeof(NumberBox), "Header",
            el => ((NumberBox)el).Header as string, (el, v) => ((NumberBox)el).Header = v),
        (typeof(AppBarButton), "Label",
            el => ((AppBarButton)el).Label, (el, v) => ((AppBarButton)el).Label = v),
    };

    /// <summary>
    /// 给一个窗口的内容树挂上本地化扫描。XAML 写死的中文只有这一条出路：
    /// <c>LocaleManager.ZhToEn</c> 有对照 + 控件类型在 <c>SweepSurface</c> 里 ⇒ 英文模式才会变英文。
    /// <para>构造期 <c>ApplyLocale()</c> 那一次扫描并不可靠 —— 那时可视树尚未实化；
    /// <c>Loaded</c> 是唯一能确定"元素已生成、属性读得到"的时刻，所以每个窗口都必须挂这一钩。
    /// 漏挂的窗口在英文模式下整窗留中文（本轮 5 个副窗口就是这么漏的）。</para>
    /// </summary>
    internal static void HookLocaleSweep(Microsoft.UI.Xaml.FrameworkElement? root)
    {
        if (root is null) return;
        root.Loaded += (_, _) => ApplyLocaleTreeSafely(root);
        // 已经过了 Loaded（例如构造完才调用、或页面从未卸载过）：立刻补扫一次，别等下一次 Loaded。
        if (root.IsLoaded) ApplyLocaleTreeSafely(root);
    }

    /// <summary>
    /// 扫描面一条声明是否适用于这个元素。
    /// <para>XAML 里写的是标签名，运行时元素却是派生类（<c>&lt;Button&gt;</c> 的运行时类型是 Button，
    /// 不是 FrameworkElement），而附加属性（ToolTipService.ToolTip）按基类链注册 ⇒
    /// 必须"声明类型能容纳该元素"，不能只看运行时精确类型。只看精确类型时 ToolTip 那条声明
    /// 一次也不会生效，而判据照样绿 —— 本轮门禁第一次跑出来的读数就是它（副窗口 48 处悬停说明）。</para>
    /// <para>放宽不带进错误类型：<c>Get/Set</c> 各自强转到自己声明的类型，写错编译期就拦下。
    /// 真正兜住"界面出现新控件"的是判据那条：XAML 里带中文的标签名必须在扫描面里出现，
    /// 漏声明就报红，不是静默不翻。</para>
    /// </summary>
    private static bool MatchesSurface(DependencyObject el, System.Type declared)
        => declared.IsAssignableFrom(el.GetType());

    /// <summary>
    /// 扫描面的 (控件类型, 文字属性) 视图 —— 给门禁判据读，让判据读的是"扫描器实际会看哪些控件"，
    /// 而不是判据自己另抄一份清单（两份清单会各自漂移，这正是本轮在修的病）。
    /// <para>返回字符串而不是 <c>Type</c>：测试项目只引用 Core，拿不到 WinUI 投影类型，
    /// 而 XAML 的标签名本来就是字符串 ⇒ 两侧按类型短名对齐（与 <c>MatchesSurface</c> 读的同一份声明）。</para>
    /// </summary>
    internal static IReadOnlyList<(string Control, string Property)> SweepSurfaceNames()
        => SweepSurface.Select(s => (s.Control.Name, s.Property)).ToList();

    private static bool LocalizeElement(DependencyObject el)
    {
        bool touched = false;
        foreach (var slot in SweepSurface)
            if (MatchesSurface(el, slot.Control))
                touched |= Localize(el, slot.Property, () => slot.Get(el), v => slot.Set(el, v));
        return touched;
    }

    /// <summary>
    /// 把 <paramref name="owner"/> 的某个文字属性翻成当前语言。
    /// <para>只在"这句话确实是 XAML 里写死的那条中文"时接管：动态写入的文字（状态栏、日志行、
    /// 色域映射卡片那几行）不登记原文，于是永远不会被这张表覆写 —— 否则每次切语言都会把
    /// "⏳ 正在编码 PNG…"刷回"就绪"。</para>
    /// </summary>
    private static bool Localize(DependencyObject owner, string prop,
        System.Func<string?> get, System.Action<string> set)
    {
        var map = s_xamlOriginal.GetOrCreateValue(owner);
        if (!map.TryGetValue(prop, out var original))
        {
            var first = get();
            if (first is null || !LocaleManager.IsLocalizableLiteral(first)) return false;
            map[prop] = original = first;
        }
        var want = LocaleManager.Tr(original);
        var now = get();
        // 当前值仍是"原文 / 本表译文"之一 ⇒ 这句话还归本地化管，可以覆写；
        // 已经被别的代码改过 ⇒ 放手，那是运行时状态，不是静态文案。
        if (now != original && now != LocaleManager.EnOf(original)) return false;
        if (now == want) return false;
        set(want);
        return true;
    }

    /// <summary>根据 Tag 查找 ComboBoxItem 并更新其文本内容。</summary>
    private static void SetComboItemText(ComboBox cbo, string tag, string text)
    {
        if (cbo is null) return;
        foreach (ComboBoxItem item in cbo.Items)
        {
            if (item.Tag is string t && t == tag)
            {
                item.Content = text;
                return;
            }
        }
    }

    /// <summary>从 _settings.FontFamily 恢复字体下拉框选中项。</summary>
    private void RestoreFontSelection()
    {
        // ═══ 2026-08-25 系统字体检测: 动态枚举系统已安装字体重建下拉框 ═══
        PopulateFontComboBox();

        var font = _settings.FontFamily ?? "";
        foreach (ComboBoxItem item in FontCbo.Items)
        {
            var tag = item.Tag as string ?? "";
            if (tag == font)
            {
                item.IsSelected = true;
                FontCustomPanel.Visibility = Visibility.Collapsed;
                return;
            }
        }
    }

    /// <summary>动态填充字体下拉框 (默认 + 系统枚举字体 + 自定义入口)。</summary>
    private void PopulateFontComboBox()
    {
        var selectedTag = (FontCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FontCbo.Items.Clear();

        // 默认项
        var defaultItem = new ComboBoxItem { Tag = "", Content = LocaleManager.CurrentLanguage == AppLanguage.English ? "Default (Microsoft YaHei)" : "默认 (微软雅黑)" };
        FontCbo.Items.Add(defaultItem);

        // 枚举系统字体 (后台线程, 避免阻塞 UI)
        IReadOnlyList<Services.FontInfo>? fonts = null;
        try { fonts = Services.FontLoader.GetSystemFonts(); } catch { }

        if (fonts is not null)
        {
            foreach (var f in fonts)
                FontCbo.Items.Add(new ComboBoxItem { Tag = f.Name, Content = f.DisplayName });
        }

        // 自定义入口
        FontCbo.Items.Add(new ComboBoxItem { Tag = "CUSTOM", Content = LocaleManager.FontCustom });

        // 恢复选中项
        foreach (ComboBoxItem item in FontCbo.Items)
        {
            if ((item.Tag as string ?? "") == selectedTag)
            { item.IsSelected = true; return; }
        }
    }
}
