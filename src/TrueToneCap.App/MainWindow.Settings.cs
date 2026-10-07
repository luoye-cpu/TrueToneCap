// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/MainWindow.Settings.cs
// MainWindow 的"设置加载/应用/保存"职责（partial class 拆分）
//
// 拆分说明（2026-08-29）：
//   MainWindow.xaml.cs 已达 150KB+ / 3200+ 行，混杂 UI 事件、编码调度、色彩管理、
//   设置、OCR/翻译等职责，难以维护。现按职责拆分为多个 partial class 文件。
//   本文件承载「设置」相关逻辑：把 _settings 与 UI 控件双向同步。
//
// ⚠ partial class 拆分不改变运行时行为：所有成员仍属同一个 MainWindow 类型，
//   私有字段（_settings / _formats / _trayIcon 等）跨文件直接可见。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.App.Models;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Logging;   // LogDetail / ApplyDetailLevel（日志详细度档位）
using TrueToneCap.Core.Services;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>静默保存设置（不更新 UI 控件值，直接序列化 _settings）。</summary>
    private void SaveSettingsQuiet() => AppServices.Settings.SaveQuiet();

    /// <summary>
    /// 把设置里的日志档位真正落到 CoreLog。
    /// <para>档位模型（<c>CoreLog.SetDetail</c>）早就存在，但没有任何入口 ⇒
    /// <c>ApplyDetailLevel</c> 零调用方、Debug/Trace 永远进不了日志文件，
    /// 而 <c>LogService</c> 的注释却写着"见设置页日志详细度"。这个方法就是把它接上。</para>
    /// <para>下拉的 Tag 直接用枚举名（Brief / Diagnose / Trace），不另设第二套词表 ——
    /// 两套名字迟早会漂移。未识别的取值一律退回 <c>Brief</c>（而不是抛）：这一项来自磁盘上的
    /// settings.json，手改坏了一个字不该让应用起不来。</para>
    /// </summary>
    private void ApplyLogVerbosity()
    {
        LogDetail detail = _settings.LogVerbosity switch
        {
            "Diagnose" => LogDetail.Diagnose,
            "Trace" => LogDetail.Trace,
            _ => LogDetail.Brief,
        };
        LogService.ApplyDetailLevel(detail);
    }

    /// <summary>日志详细度下拉改变（同 OnTargetLangChanged：LoadSettings 阶段不能落盘）。</summary>
    private void OnLogVerbosityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.LogVerbosity = (LogVerbosityCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Brief";
        ApplyLogVerbosity();          // 先应用再存盘：ApplyDetailLevel 会往日志里写抬头，那条得用新档位
        SaveSettingsQuiet();
    }

    /// <summary>
    /// 硬件编码开关（用户点击）。
    ///
    /// 2026-09-15：硬件编码器的原生崩溃在 .NET 上不可捕获，崩溃后由
    /// EncoderCrashSentinel 自动降级。这里给两个出口：
    ///   · 取消勾选 → 主动关闭硬件加速，彻底回避该路径；
    ///   · 重新勾选 → 清除崩溃哨兵 + 重置失败缓存，恢复硬件编码。
    /// ⚠️ 绑定 Click 而不是 Checked/Unchecked：后者在 LoadSettings 赋 IsChecked 时同样触发，
    /// ═══ 2026-09-18（用户指令）：「硬件编码」复选框已移除 ═══
    /// 编码器下拉已能显式选择硬件（NVENC/QSV），复选框与之语义重复。
    /// 硬件总闸恒开（Core 层 HardwareEncodingEnabled 默认 true），崩溃哨兵自动
    /// 降级保护仍在；如需禁用硬件，用户直接选择软件编码器即可。
    /// </summary>

    // ── 设置 ──

    private void LoadSettings()
    {
        // 设置已由 AppServices.Initialize() → SettingsService.Load() 加载
        // 此处无需重复加载
    }

    /// <summary>将 _settings 中的值应用到 UI 控件。</summary>
    private void ApplySettingsToUI()
    {
        FormatCbo.SelectedIndex = Math.Clamp(_settings.FormatIndex, 0, _formats.Count - 1);
        QualitySld.Minimum = 0; QualitySld.Maximum = 100;
        PathTxt.Text = _settings.OutputPath;
        if (string.IsNullOrEmpty(PathTxt.Text))
            PathTxt.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        PrefixTxt.Text = _settings.FileNamePrefix;
        if (string.IsNullOrEmpty(PrefixTxt.Text)) PrefixTxt.Text = "TrueToneCap_";
        HdrSwitch.IsOn = _settings.HdrEnabled;
        // IccBakeSwitch 已随开关一并删除（色彩管理不可关，见 CapturePipelineService.PreparePixelsWithIcc）
        // ═══ 色域下拉的持久化单位是【索引】，而项数从 6 缩到 3 ⇒ 不能只 clamp ═══
        // 直接按新项数 clamp 会把老配置静默换掉：旧索引 2/3/4 是 DisplayP3/DCI_P3/AdobeRGB，
        // 在新表里分别落在 sRGB/BT2020/sRGB —— 用户没碰过任何设置，输出色域却变了。
        // 正确做法是"旧表取标签 → 用 Core 的收口规则归一 → 在新表找索引"，
        // 且收口规则与产品路径同一份事实源（ColorProfileProvider.CollapseSelectableTag），
        // 这里不另写一遍"哪些值并到 BT.2020"。迁移是幂等的：新表标签本身就在白名单里。
        var legacyTags = new[] { "System", "sRGB", "DisplayP3", "DCI_P3", "AdobeRGB", "BT2020" };
        var currentTags = new[] { "System", "sRGB", "BT2020" };
        int legacyIdx = Math.Clamp(_settings.ColorSpaceIndex, 0, legacyTags.Length - 1);
        string wantTag = TrueToneCap.Core.ColorManagement.ColorProfileProvider.CollapseSelectableTag(legacyTags[legacyIdx]);
        int newIdx = Array.IndexOf(currentTags, wantTag);
        if (newIdx < 0) newIdx = 0;
        if (legacyTags[legacyIdx] != wantTag)
            LogService.Info("Settings",
                $"色域设置迁移：{_settings.ColorSpaceIndex}({legacyTags[legacyIdx]}) → {newIdx}({wantTag})"
                + "；旧值已从界面移除，按 Rec.2020 输出（像素会真搬进 Rec.2020，不是只改标签）");
        // 迁移结果立刻落回设置对象：否则文件里长期留着旧索引（实测本机就是 5），
        // 每次启动都要重算一遍，将来项序再变一次就得再多背一份历史表。
        if (_settings.ColorSpaceIndex != newIdx)
        {
            _settings.ColorSpaceIndex = newIdx;
            // 只改对象不改文件，上面这句注释的承诺就是空话 —— 旧索引仍躺在盘上，重算一次都不省。
            // 这里落盘是安全的：Load()/Normalize() 早已完成，_uiReady 还是 false ⇒ 写进去的就是对象本身。
            // 实机 A/B（摘掉本行）：值仍会被启动过程中另一次 +7ms 的落盘带进文件 ⇒ 这一行不是"修可见丢失"，
            // 而是把上面那句注释的承诺变成无条件成立，不再依赖"别的 handler 恰好会存一次"。
            SaveSettingsQuiet();
        }
        int maxColorIdx = (ColorCbo.Items.Count > 0) ? ColorCbo.Items.Count - 1 : currentTags.Length - 1;
        ColorCbo.SelectedIndex = Math.Clamp(newIdx, 0, maxColorIdx);
        HotkeyTxt.Text = _settings.Hotkey;
        RecordHotkeyTxt.Text = _settings.RecordHotkey;
        SilentHotkeyTxt.Text = _settings.SilentHotkey;
        SaveShortcutTxt.Text = _settings.SaveShortcut;
        CancelShortcutTxt.Text = _settings.CancelShortcut;
        AutoStartChk.IsChecked = _settings.AutoStart;
        PreviewChk.IsChecked = _settings.ShowPreview;
        MinimizeTrayChk.IsChecked = _settings.MinimizeToTray;
        UiAnimationsChk.IsChecked = _settings.EnableUiAnimations;
        // 换格式保底开关（2026-09-29）：默认勾上 = 与这次改动之前的行为一致
        PngFallbackChk.IsChecked = _settings.PngFallbackEnabled;
        ToastCaptureChk.IsChecked = _settings.ToastOnCapture;
        ToastSilentChk.IsChecked = _settings.ToastOnSilentCapture;
        ToastRecordChk.IsChecked = _settings.ToastOnRecording;
        SetComboByTag(ToastPositionCbo, _settings.ToastPosition);
        SetComboByTag(OverlayColorCbo, _settings.OverlayColor);
        SetComboByTag(BorderColorCbo, _settings.BorderColor);
        AvifPngSuffixChk.IsChecked = _settings.AvifPngSuffix;
        JxlPngSuffixChk.IsChecked = _settings.JxlPngSuffix;
        // ═══ 2026-09-16 重构：单一「AVIF 编码器」下拉 ═══
        // 恢复上次选择。软件编码器（libaom/svt）由 SelectByKind 按需解析，
        // 不再写全局静态 Codec（P2-5 修复，见 Core 侧）。
        SetComboByTag(AvifCodecCbo, _settings.AvifEncoder);
        UpdateAvifEncoderLabels();
        // ═══ 2026-09-18：「硬件编码」复选框已移除（与编码器下拉重复）═══
        // 硬件总闸注入恒为 true（Core 默认值），崩溃哨兵降级保护仍在 Core 层生效。
        NvencAvifBackend.HardwareEncodingEnabled = _settings.HardwareEncodingEnabled;
        // 回显必须与管线同源（ChromaAvif），否则"界面显示的值"与"实际使用的值"会分叉
        if (AvifChromaCbo is not null) SetComboByTag(AvifChromaCbo, _settings.ChromaAvif);
        // 每格式编码选项
        if (BdPngCbo is not null) SetComboByTag(BdPngCbo, _settings.BitDepthPng.ToString());
        if (BdJpegXlCbo is not null) SetComboByTag(BdJpegXlCbo, _settings.BitDepthJpegXl.ToString());
        if (BdAvifCbo is not null) SetComboByTag(BdAvifCbo, _settings.BitDepthAvif.ToString());
        if (BdTiffCbo is not null) SetComboByTag(BdTiffCbo, _settings.BitDepthTiff.ToString());
        if (ChromaJpegXlCbo is not null) SetComboByTag(ChromaJpegXlCbo, _settings.ChromaJpegXl);
        if (ChromaJpegLiCbo is not null) SetComboByTag(ChromaJpegLiCbo, _settings.ChromaJpegLi);
        // 2026-09-26：GainMapChromaCbo 已随合并删除 —— 增益图态的色度就是 ChromaJpegLiCbo 那一条，
        // 管线对 JPEG_GAINMAP 也改读 ChromaJpegLi。ChromaGainMap 只剩历史值，界面不再显示它
        //（回显一个用户看不见的字段 = 制造"改了没反应"的错觉）。增益图模式的下拉在下方回显。
        // 2026-09-19：原此处回显 ChromaWebPCbo 一并删除 —— WebP 的色度下拉已从界面撤掉
        //   （有损 WebP 恒 4:2:0 是格式定义、无损为 RGB，编码器也没有色度形参，
        //    回显一个不可能生效的值等于继续撒谎）。详见 AppSettingsData 同日期注释。
        RecordQualitySld.Value = _settings.RecordQuality;
        if (ArchiveChk is not null) ArchiveChk.IsChecked = _settings.ArchiveEnabled;
        if (ArchiveModeCbo is not null) { SetComboByTag(ArchiveModeCbo, _settings.ArchiveMode); ArchiveModePanel.Visibility = _settings.ArchiveEnabled ? Visibility.Visible : Visibility.Collapsed; }

        // LLM 设置
        SetComboByTag(TranslationModeCbo, _settings.TranslationMode);
        OnTranslationModeChanged(TranslationModeCbo, null!);
        LlmEndpointTxt.Text = _settings.LlmEndpoint;
        LlmApiKeyTxt.Text = _settings.LlmApiKey;
        LlmModelTxt.Text = _settings.LlmModel;
        LlmPromptTxt.Text = _settings.LlmSystemPrompt;
        // 有道官方开放平台凭据 + Google 非官方端点开关（整组只在 Free 模式下显形）
        YoudaoKeyTxt.Text = _settings.YoudaoAppKey;
        YoudaoSecretTxt.Text = _settings.YoudaoAppSecret;
        UnofficialGoogleChk.IsChecked = _settings.AllowUnofficialTranslateEndpoints;
        SetComboByTag(LogVerbosityCbo, _settings.LogVerbosity);
        ApplyLogVerbosity();   // 设置读进来才谈得上档位：在那之前的启动日志固定按 Brief 落
        SetComboByTag(TargetLangCbo, _settings.TargetLanguage);
        SetComboByTag(OcrLangCbo, _settings.OcrLanguage);
        SetComboByTag(OcrEngineCbo, _settings.OcrEngineMode);
        SetComboByTag(ThemeCbo, _settings.ThemeMode);
        SetComboByTag(LanguageCbo, _settings.Language);
        // 字体
        RestoreFontSelection();
        RefreshColorMappingUi();
        // 主题已在 App.OnLaunched 中初始化，此处仅恢复 ComboBox 选中项
        // Apply engine mode immediately
        if (Enum.TryParse<OcrEngineType>(_settings.OcrEngineMode, out var engineType))
        {
            MultiOcrService.SelectedEngineType = engineType;
        }
        PopulateOcrLanguages();
        // 2026-08-09: 启动时按引擎类型更新语言 UI (ONNX 统一字典隐藏语言选择)
        if (Enum.TryParse<OcrEngineType>(_settings.OcrEngineMode, out var initEngineType))
            UpdateOcrLanguageVisibility(initEngineType);
        // Gain Map 模式
        if (GainMapModeCbo is not null) SetComboByTag(GainMapModeCbo, _settings.GainMapMode);

        // ═══ 压缩强度三档（2026-10-07）═══
        // 逐格式恢复各自的档位。SetComboByTag 已处理"未实化容器上写 IsSelected 是空操作"
        // 与"大小写不敏感"两件事，这里直接用即可。
        if (EffortAvifCbo is not null) SetComboByTag(EffortAvifCbo, _settings.EffortAvif);
        if (EffortJpegXlCbo is not null) SetComboByTag(EffortJpegXlCbo, _settings.EffortJpegXl);
        if (EffortWebpCbo is not null) SetComboByTag(EffortWebpCbo, _settings.EffortWebp);
        if (EffortJpegLiCbo is not null) SetComboByTag(EffortJpegLiCbo, _settings.EffortJpegLi);
    }

    private static void SetComboByTag(ComboBox cbo, string tag)
    {
        // 0 个选项的下拉不是"值域漂移"：OcrLangCbo 在 ONNX 引擎下本来就没有可选项
        // （PopulateOcrLanguages 会 Clear）。以前这种也照报一条 Warn ⇒ 真漂移（OverlayColor）
        // 被固定噪声埋掉，两条警告混在一起没人分得清哪条要修。
        if (cbo.Items.Count == 0) return;

        // 大小写与校验侧同一把尺：Normalize/IsOneOf 用的是 OrdinalIgnoreCase，这里若区分大小写，
        // 白名单写 "RGB" 而 XAML 挂 "Rgb" 时值能一路活到界面、下拉却匹配不到任何项
        // （CorePipelineTests 那条"白名单 vs Tag"的判据注释里记的就是这个形状）。
        // ⚠ 选中一律走【控件级 SelectedIndex】，不要 item.IsSelected = true：
        //   11:18 实机量到，OverlayColorCbo 在启动时所在的页还没实化，对未实化的容器写
        //   IsSelected 是空操作 ⇒ 补进去的那一项在树里（8 项、含原值）却"选中=无"，界面仍是空白。
        //   这与本轮那条格式 P1 同一族：控件级状态在实化前后语义不同。
        for (int i = 0; i < cbo.Items.Count; i++)
        {
            if (cbo.Items[i] is ComboBoxItem item
                && string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            { cbo.SelectedIndex = i; return; }
        }
        // 找不到匹配项 ⇒ 下拉会是空白（没有任何选中项），而且下一次保存会把它静默改成
        // 兜底值（各字段的 `?? "444"` 那类）。这条 Warn 就是让"配置里的值域 > 界面项"
        // 这种漂移变得看得见：2026-09-26 的默认值缺失就是靠空白暴露的。
        LogService.Warn("Settings",
            $"设置值「{tag}」在下拉 {cbo.Name ?? cbo.GetType().Name} 的 {cbo.Items.Count} 个选项里没有对应项"
            + " ⇒ 已按原值补一项显示并保留（2026-10-01 起不再留空白、也不再被兜底值覆盖），但值域漂移要核对");
        // 未匹配时不许留空白。空白的那一瞬就是保存侧 `?? 字面量` 把用户存的值改掉的时机
        // ——报告 §3 那条 P1 的完整形状：界面显示默认 ⇒ 写回默认 ⇒ 差量写盘把那个键删了。
        // 把"盘上那个值"本身补成一项并选中 ⇒ 界面显示的就是实际生效的，保存也不会换掉它。
        // Content 只用原始值本身（一串 #AARRGGBB / 语言码），不新增中文串 ⇒ 不碰翻译表那条生路。
        if (!string.IsNullOrEmpty(tag))
        {
            cbo.Items.Add(new ComboBoxItem { Tag = tag, Content = tag });
            cbo.SelectedIndex = cbo.Items.Count - 1;
        }
    }

    /// <summary>
    /// 打开「第三方许可」窗口（设置 → 系统 → 第三方许可）。
    ///
    /// 为什么走窗口而不是 ContentDialog：许可全文动辄上千行，需要自己的可缩放窗口
    /// 才能滚到底（对话框的 MaxHeight 会把长文本压成一小块滚动区）。
    /// 单实例与关闭清场都由 ThirdPartyNoticesWindow 自己登记（见其 ShowOrActivate / OnClosed），
    /// 故这里不新增 MainWindow 侧的窗口字段 —— 主窗口侧不留任何需要退出时清理的状态。
    ///
    /// ⚠ 开窗失败必须显式告知：XAML 加载 / AppWindow 取用都可能抛，
    ///   静默失败的表现是"点了没反应"，用户无从判断是没入口还是坏了。
    /// </summary>
    private void OnOpenThirdPartyNotices(object sender, RoutedEventArgs e)
    {
        try
        {
            ThirdPartyNoticesWindow.ShowOrActivate();
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", "打开第三方许可窗口失败", ex);
            SetStatus("❌ 无法打开第三方许可窗口: " + ex.Message);
        }
    }

    /// <summary>
    /// 打开隐私政策窗口（2026-10-07）。
    /// <para>为什么必须有这个入口：微软商店政策 <b>10.5.1</b> 对 Win32 / Desktop Bridge 产品
    /// <b>强制</b>要求提供隐私政策，且要求"用户能够访问"。官方允许两种方式 ——
    /// 填 Partner Center 的 URL，或在产品内提供入口。本仓两个都做：
    /// URL 是提交表单的必填项，而产品内入口不会因域名/托管变化而失效。</para>
    /// <para>为什么这个应用确实需要（不是走过场）：多数功能在本地，但<b>视觉翻译会把整张截图
    /// （PNG）上传到用户配置的第三方 LLM</b>（Core/Services/TranslationService.cs:854-855）——
    /// 这属于"访问个人信息"，正是 10.5.1 要求披露的行为。PRIVACY.md 逐条写明了
    /// 哪些功能不出设备、哪些模式外发什么、发给谁、如何关闭。</para>
    /// <para>⚠ 开窗失败必须显式告知：静默失败的表现是"点了没反应"，
    /// 用户无从判断是没入口还是坏了（同 <see cref="OnOpenThirdPartyNotices"/>）。</para>
    /// </summary>
    private void OnOpenPrivacyPolicy(object sender, RoutedEventArgs e)
    {
        try
        {
            PrivacyPolicyWindow.ShowOrActivate();
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", "打开隐私政策窗口失败", ex);
            SetStatus("❌ 无法打开隐私政策窗口: " + ex.Message);
        }
    }

    /// <summary>把 UI 控件当前值写回 _settings 并持久化。</summary>
    private void SaveSettings()
    {
        try
        {
            _settings.FormatIndex = FormatCbo.SelectedIndex;
            _settings.Quality = QualitySld.Value;
            _settings.SetQuality(CurrentFormat(), QualitySld.Value);
            _settings.OutputPath = PathTxt.Text;
            _settings.FileNamePrefix = PrefixTxt.Text;
            _settings.HdrEnabled = HdrSwitch.IsOn;
            _settings.ColorSpaceIndex = ColorCbo.SelectedIndex;
            _settings.Hotkey = HotkeyTxt.Text;
            _settings.RecordHotkey = RecordHotkeyTxt.Text;
            _settings.SilentHotkey = SilentHotkeyTxt.Text;
            _settings.SaveShortcut = SaveShortcutTxt.Text;
            _settings.CancelShortcut = CancelShortcutTxt.Text;
            _settings.AutoStart = AutoStartChk.IsChecked == true;
            _settings.ShowPreview = PreviewChk.IsChecked == true;
            _settings.MinimizeToTray = MinimizeTrayChk.IsChecked == true;
            _settings.EnableUiAnimations = UiAnimationsChk.IsChecked == true;
            _settings.PngFallbackEnabled = PngFallbackChk.IsChecked == true;
            _settings.ToastOnCapture = ToastCaptureChk.IsChecked == true;
            _settings.ToastOnSilentCapture = ToastSilentChk.IsChecked == true;
            _settings.ToastOnRecording = ToastRecordChk.IsChecked == true;
            _settings.ToastPosition = (ToastPositionCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "BottomRight";
            // 2026-10-01：没有选中项时【保留盘上原值】，不写兜底。原兜底字面量 "#99001833" 既不在下拉的
            // 7 个选项里、也与 AppSettingsData.OverlayColor 的默认 #80000000 不同向 —— 它自己就是
            // "存进去就再也显示不出来"的那个值（本机盘上正是它）。配合 SetComboByTag 补项，这条路不再改用户数据。
            if (OverlayColorCbo.SelectedItem is ComboBoxItem overlayColorItem)
                _settings.OverlayColor = (string)overlayColorItem.Tag;
            _settings.BorderColor = (BorderColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4488FF";
            _settings.AvifPngSuffix = AvifPngSuffixChk.IsChecked == true;
            _settings.JxlPngSuffix = JxlPngSuffixChk.IsChecked == true;
            if (GainMapModeCbo is not null)
                _settings.GainMapMode = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray";
            _settings.AvifEncoder = (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
            // ═══ 2026-09-18：复选框已移除，硬件总闸恒开（保持字段以兼容旧 settings.json）═══
            _settings.HardwareEncodingEnabled = true;
            // 每格式编码选项
            _settings.BitDepthPng = int.TryParse((BdPngCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdp) ? bdp : 8;
            _settings.BitDepthJpegXl = int.TryParse((BdJpegXlCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdjxl) ? bdjxl : 10;
            _settings.BitDepthAvif = int.TryParse((BdAvifCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdav) ? bdav : 10;
            _settings.BitDepthJpegLi = 8; // JPEG LI 固定 8-bit
            _settings.BitDepthWebP = 8;   // WebP 固定 8-bit
            _settings.BitDepthTiff = int.TryParse((BdTiffCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdt) ? bdt : 8;
            _settings.BitDepthGainMap = 8; // Gain Map 基于 JPEG，固定 8-bit
            // （原 ChromaPng 硬编码赋值已删除：该字段于 2026-09-15 清理，
            //   PNG 为 RGB 无损格式，本就不支持色度子采样）
            _settings.ChromaJpegLi = (ChromaJpegLiCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "420"; // JPEG LI 色度
            // 兜底值必须与 AppSettingsData 的字段默认一致 —— 下拉还没回填的那一瞬保存下去的就是这个兜底值。
            // AVIF 的默认已于 2026-09-19 改回 420（此处原注释还写着"444 视觉无损"，属过期口径）。
            _settings.ChromaAvif = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "420";
            _settings.ChromaJpegXl = (ChromaJpegXlCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            // 2026-09-26：不再写 ChromaGainMap —— 控件已随"GainMap 并入 JPEG LI"删除，
            //   管线对增益图态改读 ChromaJpegLi（上面那一条）。留着这行等于让一个看不见的
            //   下拉的兜底值每次保存都覆盖盘上的历史值。
            // ═══ 2026-09-19 清理：原这两行硬编码赋值已随字段删除 ═══
            //   ChromaWebP（本轮连同下拉一起撤掉）、ChromaTiff（界面上本就没有下拉，
            //   每次保存却被改回固定值）——写进去也没有任何编码器读它，属"只写不读"，
            //   留着等于在设置模型里维持一份与真实码流无关的假配置。理由见 AppSettingsData。
            //   （ChromaGainMap 当天曾一并删除，随后用户定为"默认 420、可手动 444"⇒ 已恢复为真设置。）
            _settings.RecordQuality = RecordQualitySld.Value;
            // （原 AnimAvifBackendIndex 硬编码赋值已删除：该字段于 2026-09-15 清理，
            //   它无 UI、无消费方，写 0 只是制造"录制 AVIF 后端可配置"的假象）
            _settings.ArchiveEnabled = ArchiveChk?.IsChecked == true;
            _settings.ArchiveMode = (ArchiveModeCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Month";
            _settings.FirstRun = false;
            _settings.ShowPreview = PreviewChk.IsChecked == true;
            _settings.OcrEngineMode = (OcrEngineCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "OnnxGpu";
            _settings.ThemeMode = (ThemeCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
            _settings.Language = (LanguageCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh";

            // 字体
            var fontTag = (FontCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _settings.FontFamily = fontTag == "CUSTOM" ? FontCustomTxt.Text.Trim() : fontTag;

            // LLM 设置
            _settings.TranslationMode = (TranslationModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Free";
            _settings.UseCustomLlm = _settings.TranslationMode is "LLM" or "Vision";
            _settings.LlmEndpoint = LlmEndpointTxt.Text;
            _settings.LlmApiKey = LlmApiKeyTxt.Text;
            _settings.LlmModel = LlmModelTxt.Text;
            _settings.LlmSystemPrompt = LlmPromptTxt.Text;
            _settings.YoudaoAppKey = YoudaoKeyTxt.Text.Trim();
            _settings.YoudaoAppSecret = YoudaoSecretTxt.Text.Trim();
            _settings.AllowUnofficialTranslateEndpoints = UnofficialGoogleChk.IsChecked == true;
            _settings.LogVerbosity = (LogVerbosityCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Brief";
            _settings.TargetLanguage = (TargetLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            // 同上：OcrLangCbo 在 ONNX 引擎下是空的（没有可选项），旧写法每次保存都把用户存过的语言抹成 ""
            // ⇒ 切回 Windows OCR 时只能落回列表第一项，"我明明选过繁体中文"这件事没有任何线索。
            if (OcrLangCbo.SelectedItem is ComboBoxItem ocrLangItem)
                _settings.OcrLanguage = (string)ocrLangItem.Tag;

            // OutputBitDepth 从每格式位深映射（SaveSettings 中的 BitDepth 已更新）
            // ⚠ 按【格式枚举】取，不按 FormatIndex 取：索引表 2026-09-26 少了一项（GainMap 并入 JPEG LI），
            //   而这里原先写的是 `0/3/4/6 =>` —— 换序即错位，且错位后仍然"看起来正常"。
            _settings.OutputBitDepth = CurrentFormat() switch
            {
                OutputFormat.PNG => _settings.BitDepthPng,
                OutputFormat.JPEG_XL => _settings.BitDepthJpegXl,
                OutputFormat.AVIF => _settings.BitDepthAvif,
                OutputFormat.TIFF => _settings.BitDepthTiff,
                _ => 8,                              // 其他格式固定 8-bit
            };

            // 通过 SettingsService 持久化
            AppServices.Settings.Save();

            // 热键 + 自启同步
            // ⚠ 之前这两处静默失败：设置界面上的开关已勾选并提示"已保存"，
            // 但系统里自启项/热键实际未生效 —— 用户看到的与实际状态不一致且无从察觉。
            SyncStartupAndHotkey();

            SetStatus("✅ 设置已保存");
        }
        catch (Exception ex) { SetStatus("❌ 保存失败: " + ex.Message); }
    }

    /// <summary>同步开机自启与全局热键到系统。
    /// 失败必须显式告知用户：否则设置界面显示"已保存"而系统侧实际未生效。</summary>
    private void SyncStartupAndHotkey()
    {
        try
        {
            StartupManager.IsEnabled = _settings.AutoStart;
            LogService.Info("MainWindow", $"开机自启已{(StartupManager.IsEnabled ? "启用" : "禁用")}");
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", "开机自启设置失败（界面状态可能与实际不符）", ex);
            SetStatus("⚠ 开机自启设置失败");
        }

        try
        {
            _trayIcon?.RegisterCaptureHotkey(_settings.Hotkey);
            LogService.Info("MainWindow", $"热键已注册: {_settings.Hotkey}");
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", "热键注册失败（可能被其他程序占用）", ex);
            SetStatus($"⚠ 热键注册失败: {_settings.Hotkey}");
        }
    }

}
