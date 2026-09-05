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
using TrueToneCap.Core.Services;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>静默保存设置（不更新 UI 控件值，直接序列化 _settings）。</summary>
    private void SaveSettingsQuiet() => AppServices.Settings.SaveQuiet();

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
        IccBakeSwitch.IsOn = _settings.IccBakeEnabled;
        // ═══ 2026-08-16 P3-14 修复: ColorCbo 实际 6 项 (索引 0-5), 旧 clamp 到 6 越界
        // 损坏配置 (索引 6) → SelectedIndex=-1 → 静默回退 "System"。
        int maxColorIdx = (ColorCbo.Items.Count > 0) ? ColorCbo.Items.Count - 1 : 5;
        ColorCbo.SelectedIndex = Math.Clamp(_settings.ColorSpaceIndex, 0, maxColorIdx);
        HotkeyTxt.Text = _settings.Hotkey;
        RecordHotkeyTxt.Text = _settings.RecordHotkey;
        SilentHotkeyTxt.Text = _settings.SilentHotkey;
        SaveShortcutTxt.Text = _settings.SaveShortcut;
        CancelShortcutTxt.Text = _settings.CancelShortcut;
        AutoStartChk.IsChecked = _settings.AutoStart;
        PreviewChk.IsChecked = _settings.ShowPreview;
        MinimizeTrayChk.IsChecked = _settings.MinimizeToTray;
        UiAnimationsChk.IsChecked = _settings.EnableUiAnimations;
        ToastCaptureChk.IsChecked = _settings.ToastOnCapture;
        ToastSilentChk.IsChecked = _settings.ToastOnSilentCapture;
        ToastRecordChk.IsChecked = _settings.ToastOnRecording;
        SetComboByTag(ToastPositionCbo, _settings.ToastPosition);
        SetComboByTag(OverlayColorCbo, _settings.OverlayColor);
        SetComboByTag(BorderColorCbo, _settings.BorderColor);
        AvifPngSuffixChk.IsChecked = _settings.AvifPngSuffix;
        JxlPngSuffixChk.IsChecked = _settings.JxlPngSuffix;
        AvifBackendCbo.SelectedIndex = Math.Clamp(_settings.AvifBackendIndex, 0, 3);
        if (AvifChromaCbo is not null) SetComboByTag(AvifChromaCbo, _settings.AvifChroma);
        // 每格式编码选项
        if (BdPngCbo is not null) SetComboByTag(BdPngCbo, _settings.BitDepthPng.ToString());
        if (BdJpegXlCbo is not null) SetComboByTag(BdJpegXlCbo, _settings.BitDepthJpegXl.ToString());
        if (BdAvifCbo is not null) SetComboByTag(BdAvifCbo, _settings.BitDepthAvif.ToString());
        if (BdTiffCbo is not null) SetComboByTag(BdTiffCbo, _settings.BitDepthTiff.ToString());
        if (ChromaJpegXlCbo is not null) SetComboByTag(ChromaJpegXlCbo, _settings.ChromaJpegXl);
        if (ChromaJpegLiCbo is not null) SetComboByTag(ChromaJpegLiCbo, _settings.ChromaJpegLi);
        if (ChromaWebPCbo is not null) SetComboByTag(ChromaWebPCbo, _settings.ChromaWebP);
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
        SetComboByTag(TargetLangCbo, _settings.TargetLanguage);
        SetComboByTag(OcrLangCbo, _settings.OcrLanguage);
        SetComboByTag(OcrEngineCbo, _settings.OcrEngineMode);
        SetComboByTag(ThemeCbo, _settings.ThemeMode);
        SetComboByTag(LanguageCbo, _settings.Language);
        // 字体
        RestoreFontSelection();
        DetectAndShowSourceGamut();
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
    }

    private static void SetComboByTag(ComboBox cbo, string tag)
    {
        foreach (ComboBoxItem item in cbo.Items)
        { if ((string)item.Tag == tag) { item.IsSelected = true; return; } }
    }

    /// <summary>把 UI 控件当前值写回 _settings 并持久化。</summary>
    private void SaveSettings()
    {
        try
        {
            _settings.FormatIndex = FormatCbo.SelectedIndex;
            _settings.Quality = QualitySld.Value;
            _settings.SetQuality(FormatCbo.SelectedIndex, QualitySld.Value);
            _settings.OutputPath = PathTxt.Text;
            _settings.FileNamePrefix = PrefixTxt.Text;
            _settings.HdrEnabled = HdrSwitch.IsOn;
            _settings.IccBakeEnabled = IccBakeSwitch.IsOn;
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
            _settings.ToastOnCapture = ToastCaptureChk.IsChecked == true;
            _settings.ToastOnSilentCapture = ToastSilentChk.IsChecked == true;
            _settings.ToastOnRecording = ToastRecordChk.IsChecked == true;
            _settings.ToastPosition = (ToastPositionCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "BottomRight";
            _settings.OverlayColor = (OverlayColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#99001833";
            _settings.BorderColor = (BorderColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4488FF";
            _settings.AvifPngSuffix = AvifPngSuffixChk.IsChecked == true;
            _settings.JxlPngSuffix = JxlPngSuffixChk.IsChecked == true;
            if (GainMapModeCbo is not null)
                _settings.GainMapMode = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray";
            _settings.AvifBackendIndex = AvifBackendCbo.SelectedIndex;
            _settings.AvifChroma = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            // 每格式编码选项
            _settings.BitDepthPng = int.TryParse((BdPngCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdp) ? bdp : 8;
            _settings.BitDepthJpegXl = int.TryParse((BdJpegXlCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdjxl) ? bdjxl : 10;
            _settings.BitDepthAvif = int.TryParse((BdAvifCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdav) ? bdav : 10;
            _settings.BitDepthJpegLi = 8; // JPEG LI 固定 8-bit
            _settings.BitDepthWebP = 8;   // WebP 固定 8-bit
            _settings.BitDepthTiff = int.TryParse((BdTiffCbo?.SelectedItem as ComboBoxItem)?.Tag as string, out var bdt) ? bdt : 8;
            _settings.BitDepthGainMap = 8; // Gain Map 基于 JPEG，固定 8-bit
            _settings.ChromaPng = "444";   // PNG 是 RGB 无损格式，不支持色度子采样
            _settings.ChromaJpegLi = (ChromaJpegLiCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "420"; // JPEG LI 色度
            _settings.ChromaAvif = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            _settings.ChromaJpegXl = (ChromaJpegXlCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            _settings.ChromaWebP = (ChromaWebPCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "420";
            _settings.ChromaTiff = "444";   // TIFF 无损，固定 4:4:4
            _settings.ChromaGainMap = "420";
            _settings.RecordQuality = RecordQualitySld.Value;
            _settings.AnimAvifBackendIndex = 0;
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
            _settings.TargetLanguage = (TargetLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            _settings.OcrLanguage = (OcrLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            // OutputBitDepth 从每格式位深映射（SaveSettings 中的 BitDepth 已更新）
            _settings.OutputBitDepth = _settings.FormatIndex switch
            {
                0 => _settings.BitDepthPng,         // PNG
                3 => _settings.BitDepthJpegXl,      // JPEG XL
                4 => _settings.BitDepthAvif,        // AVIF
                6 => _settings.BitDepthTiff,        // TIFF
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
