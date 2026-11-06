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
    /// <summary>
    /// 「ffmpeg 路径」这一格【回显成功过】没有 —— 保存侧的回写必须有这道闩。
    /// <para>为什么这一格要单独设闩，而热键、输出路径那些不用的理由：空串在别的格是"没填"，
    /// 在 <see cref="AppSettingsData.FfmpegPath"/> 却是<b>合法值</b>（= 自动解析）。
    /// 于是"控件还没回填就保存"这一类事故在这格变成了<b>不可察觉的数据丢失</b>：
    /// 用户在 settings.json 里手改过路径，若 <see cref="ApplySettingsToUI"/> 有任何一句在回显之前
    /// 抛异常（它前半段有迁移计算、有 <c>SaveSettingsQuiet()</c> 落盘，都可能抛），
    /// 而退出路径的 <c>SaveSettings()</c> 是被 <c>try { … } catch { }</c> 吞掉的
    /// （MainWindow.xaml.cs 的关闭回调两处），那一格会以空串写回去 ——
    /// 界面上什么都没显示，盘上那行手改的值也没了，两个现象互相掩盖。</para>
    /// <para>本仓对同族问题的既定答案是"控件状态不可信时【不写】、保留盘上原值"
    /// （见 <c>SaveSettings</c> 里 OverlayColorCbo 那条 2026-10-01 的注释）。这里照同一个方向做：
    /// 没回显过 ⇒ 不回写。代价是"回显之前用户就改了这格"会丢一次编辑，
    /// 但那条路径要求用户在构造期那一瞬之内改到一个还没显示的输入框里，不可达；
    /// 而"静默清掉手改路径"是可达且无痕的。</para>
    /// </summary>
    private bool _ffmpegPathEchoed;

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
        // 435ms 那一格（Brief 档实测）到底是哪一段贵：决定"能不能把某一段搬到首帧之后"。
        // 分段口径 = 下面四个 m 标记之间的间隔，四段之和必须等于本方法总时长。
        var swApply = System.Diagnostics.Stopwatch.StartNew();
        FormatCbo.SelectedIndex = Math.Clamp(_settings.FormatIndex, 0, _formats.Count - 1);
        QualitySld.Minimum = 0; QualitySld.Maximum = 100;
        PathTxt.Text = _settings.OutputPath;
        if (string.IsNullOrEmpty(PathTxt.Text))
            PathTxt.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        PrefixTxt.Text = _settings.FileNamePrefix;
        if (string.IsNullOrEmpty(PrefixTxt.Text)) PrefixTxt.Text = "TrueToneCap_";
        SetHdrSwitchSilently(_settings.HdrEnabled);
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
        long m1 = swApply.ElapsedMilliseconds;   // ↑ 基础：格式/质量/路径/热键/开关/托盘/提示
        // ═══ 2026-09-16 重构：单一「AVIF 编码器」下拉 ═══
        // 恢复上次选择。软件编码器（libaom/svt）由 SelectByKind 按需解析，
        // 不再写全局静态 Codec（P2-5 修复，见 Core 侧）。
        SetComboByTag(AvifCodecCbo, _settings.AvifEncoder);
        // 2026-11-03：AVIF 的 HDR 承载方式（默认 Pq）
        if (AvifHdrModeCbo is not null) SetComboByTag(AvifHdrModeCbo, _settings.AvifHdrMode);
        // ═══ 2026-10-30：这一句实测吃掉 478ms（启动回显四段里 95%）═══
        // 它逐个问 `IsKindAvailable`，软件那几种要真去探测 avifenc.exe（进程创建本机冷启动约 200ms 一档）。
        // 而它只改下拉的**文字后缀**（"（本机不可用）"），不参与任何读取：格式/编码器的实际选择走
        // `_settings.AvifEncoder`。⇒ 推到首帧之后再刷，窗口先出来；探测结果本身仍有进程内缓存，
        // 后面真要用时不会重付。队列还没就绪就直接跑（标签宁可晚一点，不能永远不刷）。
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateAvifEncoderLabels))
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
        // 2026-09-26：GainMapChromaCbo 已随合并删除 —— 增益图不再另设色度下拉。
        // 2026-11-04 更新：增益图底图恒 4:4:4（管线硬编码），ChromaJpegLiCbo 只服务普通
        // JPEG LI 照片档，开启增益图时它会被禁用并说明原因（见 UpdateQualityPanelCore）。
        // ChromaGainMap 只剩迁移用的历史值，界面不再显示它（回显一个用户看不见的字段
        // = 制造"改了没反应"的错觉）。增益图模式的下拉在下方回显。
        // 2026-09-19：原此处回显 ChromaWebPCbo 一并删除 —— WebP 的色度下拉已从界面撤掉
        //   （有损 WebP 恒 4:2:0 是格式定义、无损为 RGB，编码器也没有色度形参，
        //    回显一个不可能生效的值等于继续撒谎）。详见 AppSettingsData 同日期注释。
        RecordQualitySld.Value = _settings.RecordQuality;
        // 动图录制三件套回显（2026-11-02 D 流第 4 步）。Tag 契约见 AppSettingsData.AnimFormat 注释；
        // SetComboByTag 大小写不敏感、未命中补项 —— Normalize 已把 AnimFormat 收口成 Tag 拼法，
        // 正常路径到不了补项分支（补项 = 白名单与 XAML 漂移的可见警报，Warn 会说）。
        if (AnimFormatCbo is not null) SetComboByTag(AnimFormatCbo, _settings.AnimFormat);
        if (RecordFpsCbo is not null) SetComboByTag(RecordFpsCbo, _settings.RecordFps.ToString());
        if (MaxDurationCbo is not null) SetComboByTag(MaxDurationCbo, _settings.MaxDurationSeconds.ToString());
        // ffmpeg 路径输入框回显（2026-11-03 遗留 3 收口）：这一格此前只存在于 settings.json，
        // 用户得关掉 app 手编文件。现在界面上直接可填、可清空（清空 = 回到自动解析）。
        // ⚠ 空值也要回显成空 —— 不要在这里补任何兜底文字，否则"没配过"会变成"配了一个假路径"。
        // ⚠ 回显与回写之间要一道闩（见 _ffmpegPathEchoed）：本方法前半段任何一句抛异常都会让
        //   这一格停在"空的"，而退出路径的 SaveSettings() 是 try/catch 吞掉的 ⇒ 空串会被当成
        //   用户意图写回盘上，把 settings.json 里那行手改的路径静默抹掉。
        if (FfmpegPathTxt is not null)
        {
            FfmpegPathTxt.Text = _settings.FfmpegPath;
            _ffmpegPathEchoed = true;
        }
        if (ArchiveChk is not null) ArchiveChk.IsChecked = _settings.ArchiveEnabled;
        if (ArchiveModeCbo is not null) { SetComboByTag(ArchiveModeCbo, _settings.ArchiveMode); ArchiveModePanel.Visibility = _settings.ArchiveEnabled ? Visibility.Visible : Visibility.Collapsed; }
        long m2 = swApply.ElapsedMilliseconds;   // ↑ 编码选项：AVIF 编码器/位深/色度/归档

        // LLM 设置
        SetComboByTag(TranslationModeCbo, _settings.TranslationMode);
        OnTranslationModeChanged(TranslationModeCbo, null!);
        LlmEndpointTxt.Text = _settings.LlmEndpoint;
        LlmApiKeyTxt.Text = _settings.LlmApiKey;
        LlmModelTxt.Text = _settings.LlmModel;
        LlmPromptTxt.Text = _settings.LlmSystemPrompt;
        // 免费公共端点开关（默认开）+ 有道官方凭据（可选加速项）——整组只在 Free 模式下显形
        YoudaoKeyTxt.Text = _settings.YoudaoAppKey;
        YoudaoSecretTxt.Text = _settings.YoudaoAppSecret;
        FreeEndpointsChk.IsChecked = _settings.AllowUnofficialTranslateEndpoints;
        SetComboByTag(LogVerbosityCbo, _settings.LogVerbosity);
        ApplyLogVerbosity();   // 设置读进来才谈得上档位：在那之前的启动日志固定按 Brief 落
        SetComboByTag(TargetLangCbo, _settings.TargetLanguage);
        SetComboByTag(OcrLangCbo, _settings.OcrLanguage);
        SetComboByTag(OcrEngineCbo, _settings.OcrEngineMode);
        SetComboByTag(ThemeCbo, _settings.ThemeMode);
        SetComboByTag(LanguageCbo, _settings.Language);
        long m3 = swApply.ElapsedMilliseconds;   // ↑ 翻译/LLM/日志档位/语言/主题
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
        // 元数据承载方式回显已删（2026-11-03 二次收口：恒写 ISO 21496-1，不做用户抉择）

        // ═══ 压缩强度档位（2026-10-07；2026-10-29 扩到 PNG / TIFF 并分成两组）═══
        // 逐格式恢复各自的档位。SetComboByTag 已处理"未实化容器上写 IsSelected 是空操作"
        // 与"大小写不敏感"两件事，这里直接用即可。
        // ⚠ 两组下拉格数不同（AVIF / JPEG XL 三格，PNG / TIFF / WebP / JPEG LI 两格），
        //   所以这里的六行是"逐格式点名"而不是扫一遍可视树：卡片默认 Collapsed，
        //   项不在可视树里时扫不到 ⇒ 只能显式写全六个（漏一个 = 重启后那一格落回 XAML 的默认档）。
        if (EffortAvifCbo is not null) SetComboByTag(EffortAvifCbo, _settings.EffortAvif);
        if (EffortJpegXlCbo is not null) SetComboByTag(EffortJpegXlCbo, _settings.EffortJpegXl);
        if (EffortWebpCbo is not null) SetComboByTag(EffortWebpCbo, _settings.EffortWebp);
        if (EffortJpegLiCbo is not null) SetComboByTag(EffortJpegLiCbo, _settings.EffortJpegLi);
        if (EffortPngCbo is not null) SetComboByTag(EffortPngCbo, _settings.EffortPng);
        if (EffortTiffCbo is not null) SetComboByTag(EffortTiffCbo, _settings.EffortTiff);
        LogService.Info("Settings",
            $"启动: 回显分段 = 基础 {m1} + 编码选项 {m2 - m1} + 翻译/语言 {m3 - m2}"
            + $" + OCR/字体/其余 {swApply.ElapsedMilliseconds - m3}ms");
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
            // 2026-11-06 收尾：不再镜像滑块值进遗留死键 Quality —— GetQuality/SetQuality 的
            // default 分支在当前 7 格枚举下不可达，继续写它只会持续污染死键（误导读它的人）。
            // 真正的写入点就是下一行 SetQuality 的每格式字段。盘上已残留的旧 "Quality" 键
            // 无读取方、无害；差集序列化剪不掉它（内存值仍是从文件读进来的旧值 ≠ 默认 80），
            // 待未来 SettingsRevision 迁移摘除契约时自然消失。
            _settings.SetQuality(CurrentFormat(), QualitySld.Value);
            _settings.OutputPath = PathTxt.Text;
            _settings.FileNamePrefix = PrefixTxt.Text;
            // 增益图态下这个开关是被【锁开】的 UI 状态（见 UpdateQualityPanel），不是用户选择：
            // 读它会把手开增益图那一刻的 true 当成意图落盘，之后关掉增益图 HDR 还一直是开。
            if (CurrentFormat() != OutputFormat.JPEG_GAINMAP)
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
            // 元数据承载方式落回设置已删（2026-11-03 二次收口：恒写 ISO 21496-1，不做用户抉择）
            // 兜底必须与 AppSettingsData.AvifEncoder 的默认【同源】（2026-11-04 默认 Auto→LibAom）。
            // ⚠ 接线审查抓到本轮漏改：这里一度还是 ?? "Auto" ⇒ 控件未回填那一瞬保存会把默认漂回 Auto，
            //   而 Auto 在 444 下会被能力协商降回 420（正是本轮要消除的形态）。判据见
            //   Test_Settings_ComboFallbacksMatchDefaults（遍历所有 combo 字段，兜底≠默认即红）。
            _settings.AvifEncoder = (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "LibAom";
            // 2026-11-03：AVIF 的 HDR 承载方式落回设置。兜底必须是 Pq（传统底图写 PQ）——
            // 那是本轮之前 AVIF 唯一的 HDR 路，也是硬件加速唯一支持的形状；兜到 GainMap
            // 会让"控件没回填就保存"的用户静默拿到另一条编码器路径。
            if (AvifHdrModeCbo is not null)
                _settings.AvifHdrMode = (AvifHdrModeCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Pq";
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
            _settings.ChromaJpegLi = (ChromaJpegLiCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444"; // JPEG LI 色度
            // 兜底值必须与 AppSettingsData 的字段默认一致 —— 下拉还没回填的那一瞬保存下去的就是这个兜底值。
            // ⚠ 接线审查抓到本轮漏改：AVIF 默认已于 2026-11-04 改为 444，这里一度还停在 ?? "420"
            //   （原注释写"2026-09-19 改回 420"也过期了）。兜底漂回 420 ⇒ 控件未回填那一瞬会把默认漂错。
            //   由 Test_Settings_ComboFallbacksMatchDefaults 遍历兜底↔默认，漂即红。
            //   2026-11-06（用户拍板）：JPEG LI 色度默认 420→444，兜底随字段默认一并改到 ?? "444"。
            _settings.ChromaAvif = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            _settings.ChromaJpegXl = (ChromaJpegXlCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            // 2026-09-26：不再写 ChromaGainMap —— 控件已随"GainMap 并入 JPEG LI"删除。
            //   2026-11-04 更新：增益图态色度【既不读 ChromaGainMap、也不读 ChromaJpegLi】——
            //   管线对 JPEG_GAINMAP 硬编码 444（见 CapturePipelineService 的 switch），普通 JPEG LI
            //   才读 ChromaJpegLi。留着写 ChromaGainMap 这行等于让一个看不见的下拉的兜底值
            //   每次保存都覆盖盘上的历史值。
            // ═══ 2026-09-19 清理：原这两行硬编码赋值已随字段删除 ═══
            //   ChromaWebP（本轮连同下拉一起撤掉）、ChromaTiff（界面上本就没有下拉，
            //   每次保存却被改回固定值）——写进去也没有任何编码器读它，属"只写不读"，
            //   留着等于在设置模型里维持一份与真实码流无关的假配置。理由见 AppSettingsData。
            //   （ChromaGainMap 当天曾一并删除，随后用户定为"默认 420、可手动 444"⇒ 已恢复为真设置。）
            _settings.RecordQuality = RecordQualitySld.Value;
            // 动图录制三件套（2026-11-02 D 流第 4 步）：从控件 Tag 落回设置，形状照 BitDepth 系
            // 的 int.TryParse 模板。兜底值必须与 AppSettingsData 默认一致（下拉未回填那一瞬保存的就是它）。
            _settings.AnimFormat = (AnimFormatCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "WebP";
            _settings.RecordFps = int.TryParse((RecordFpsCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var recordFps) ? recordFps : 15;
            _settings.MaxDurationSeconds = int.TryParse((MaxDurationCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var maxDuration) ? maxDuration : 30;
            // ═══ FfmpegPath 现在有界面了（2026-11-03 遗留 3）⇒ 从输入框回写 ═══
            // 空串是合法值（= 自动解析），所以这里不能像别的下拉那样写 `?? 兜底`：
            // 清空输入框 = 用户主动要求回到自动解析，补兜底会把"清空"变成"清不掉"。
            // 清洗链保持与 SettingsService.Normalize 同形（Trim → 剥引号 → TrimEnd(';') → Trim），
            // 两处都做是刻意的：这里管"刚敲进去的"，Normalize 管"盘上读回来的"。
            // ⚠ _ffmpegPathEchoed 这道闩与上面 OverlayColorCbo 那条"没有选中项就保留盘上原值"
            //   同一个理由，只是触发条件不同：那一格怕"下拉没回填 ⇒ 兜底值覆盖"，
            //   这一格怕"回显根本没跑 ⇒ 空的控件被当成用户意图"。未回显 ⇒ 不写，
            //   否则手改在 settings.json 里的路径会被静默清掉（空串在这格是合法值，清得掉）。
            if (FfmpegPathTxt is not null && _ffmpegPathEchoed)
                _settings.FfmpegPath = NormalizeFfmpegPathInput(FfmpegPathTxt.Text);
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
            _settings.AllowUnofficialTranslateEndpoints = FreeEndpointsChk.IsChecked == true;
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
            // ═══ 2026-11-03（遗留 3）：手填的 ffmpeg 路径要当场给个说法 ═══
            // 为什么不能只靠录制入口那句"未找到 ffmpeg"：那句话要到用户按热键开始录时才出现，
            // 而"路径填错了"这件事在按下保存的那一刻就已经确定了 —— 让用户等一次录制失败才知道，
            // 等于把一次可立即纠正的笔误拖成一次产品故障。
            // ⚠ 只警告、不拒绝保存：文件可能在保存之后才被放到那个位置（下载中 / 从别的机器拷），
            //   而 FfmpegLocator 本来就按"配置无效 ⇒ 回退自动解析"工作（台账 §5.2 第 3 步）。
            //   这里改了「✅ 设置已保存」那句文案，把警告一并写进同一行状态，不再弹额外对话框。
            string saveStatus = "✅ 设置已保存";
            var ffmpegIssue = FfmpegLocator.DescribePathIssue(_settings.FfmpegPath);
            if (ffmpegIssue is not null)
            {
                LogService.Warn("Settings", $"ffmpeg 路径不可用：{ffmpegIssue}");
                saveStatus = "⚠️ 设置已保存，但 ffmpeg 路径不可用：" + ffmpegIssue;
            }
            else if (_settings.FfmpegPath.Length > 0)
            {
                // 路径有效 ⇒ 丢掉旧缓存，下次录制按新路径重新解析（否则会继续用上次缓存的那一枚）
                FfmpegLocator.Invalidate();
            }

            // 热键 + 自启同步
            // ⚠ 之前这两处静默失败：设置界面上的开关已勾选并提示"已保存"，
            // 但系统里自启项/热键实际未生效 —— 用户看到的与实际状态不一致且无从察觉。
            SyncStartupAndHotkey();

            SetStatus(saveStatus);
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
