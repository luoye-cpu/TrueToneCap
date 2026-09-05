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
        BrandTagline.Text = LocaleManager.CurrentLanguage == AppLanguage.English ? "HDR Screenshot Tool" : "HDR 截图工具";

        NavOutput.Content = LocaleManager.NavOutput;
        NavCapture.Content = LocaleManager.NavCapture;
        NavAI.Content = LocaleManager.NavAI;
        NavSystem.Content = LocaleManager.NavSystem;
        NavLog.Content = LocaleManager.NavLog;

        PageOutputTitle.Text = LocaleManager.CurrentLanguage == AppLanguage.English ? "Output & Color" : "输出与色彩";
        BasicOutputTitle.Text = LocaleManager.BasicOutput;
        AvifOptionsTitle.Text = LocaleManager.AvifOptions;
        GainMapOptionsTitle.Text = LocaleManager.GainMapOptions;
        GainMapOptionsDesc.Text = LocaleManager.GainMapDesc;
        GainMapModeLabelTxt.Text = LocaleManager.GainMapModeLabel;
        AvifPngSuffixChk.Content = LocaleManager.AvifPngSuffix;
        JxlPngSuffixChk.Content = LocaleManager.JxlPngSuffix;

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

        // ── 格式下拉（重建列表保留选中项） ──
        int fmtIdx = FormatCbo.SelectedIndex;
        var fmtLabels = new[]
        {
            LocaleManager.FmtPng, LocaleManager.FmtJpegGainMap, LocaleManager.FmtJpegLi,
            LocaleManager.FmtJpegXl, LocaleManager.FmtAvif, LocaleManager.FmtWebP, LocaleManager.FmtBmp
        };
        FormatCbo.ItemsSource = fmtLabels.ToList();
        FormatCbo.SelectedIndex = Math.Clamp(fmtIdx, 0, fmtLabels.Length - 1);

        // ── 硬编码 ComboBox Items：按 Tag 匹配更新 Content ──
        SetComboItemText(AvifBackendCbo, "Auto", LocaleManager.AvifAuto);
        SetComboItemText(AvifBackendCbo, "LibAom", LocaleManager.AvifLibAom);
        SetComboItemText(AvifBackendCbo, "Qsv", LocaleManager.AvifQsv);
        SetComboItemText(AvifBackendCbo, "Nvenc", LocaleManager.AvifNvenc);

        SetComboItemText(AvifChromaCbo, "444", LocaleManager.AvifChroma444);
        SetComboItemText(AvifChromaCbo, "422", LocaleManager.AvifChroma422);
        SetComboItemText(AvifChromaCbo, "420", LocaleManager.AvifChroma420);

        SetComboItemText(GainMapModeCbo, "Rgb", LocaleManager.GmRgb);
        SetComboItemText(GainMapModeCbo, "Gray", LocaleManager.GmGray);

        SetComboItemText(ColorCbo, "System", LocaleManager.CsSystem);
        SetComboItemText(ColorCbo, "sRGB", LocaleManager.CsSRgb);
        SetComboItemText(ColorCbo, "DisplayP3", LocaleManager.CsDisplayP3);
        SetComboItemText(ColorCbo, "DCI_P3", LocaleManager.CsDciP3);
        SetComboItemText(ColorCbo, "AdobeRGB", LocaleManager.CsAdobeRgb);
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

        SetComboItemText(OcrEngineCbo, "Auto", LocaleManager.OcrAuto);
        SetComboItemText(OcrEngineCbo, "Gpu", LocaleManager.OcrGpu);
        SetComboItemText(OcrEngineCbo, "Windows", LocaleManager.OcrWindows);
        SetComboItemText(OcrEngineCbo, "Cpu", LocaleManager.OcrCpu);

        SetComboItemText(LlmModelCbo, "gpt-4o-mini", LocaleManager.LlmGpt4oMini);
        SetComboItemText(LlmModelCbo, "deepseek-chat", LocaleManager.LlmDeepSeek);
        SetComboItemText(LlmModelCbo, "gpt-4.1-mini", LocaleManager.LlmGpt41Mini);
        SetComboItemText(LlmModelCbo, "custom", LocaleManager.LlmCustom);

        SetComboItemText(TargetLangCbo, "zh-CN", LocaleManager.TlChinese);
        SetComboItemText(TargetLangCbo, "en", LocaleManager.TlEnglish);
        SetComboItemText(TargetLangCbo, "ja", LocaleManager.TlJapanese);

        SetComboItemText(OcrLangCbo, "", LocaleManager.OlSystem);
        SetComboItemText(OcrLangCbo, "zh-en", LocaleManager.OlMixed);
        SetComboItemText(OcrLangCbo, "zh-Hans", LocaleManager.TlChinese);
        SetComboItemText(OcrLangCbo, "en-US", LocaleManager.TlEnglish);

        // 归档模式
        if (ArchiveModeCbo is not null)
        {
            SetComboItemText(ArchiveModeCbo, "Year", LocaleManager.ArchiveYear);
            SetComboItemText(ArchiveModeCbo, "Month", LocaleManager.ArchiveMonth);
            SetComboItemText(ArchiveModeCbo, "Day", LocaleManager.ArchiveDay);
        }

        // ── 更新色域映射 UI ──
        DetectAndShowSourceGamut();
        UpdateGamutMappingUI();
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
