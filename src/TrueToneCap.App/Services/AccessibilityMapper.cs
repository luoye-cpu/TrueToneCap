// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// ═══ WCAG 2.1 AA Accessibility Mapping Service ═══
// 2026-11-08: Centralized automation properties for all UI controls
// This service provides non-breaking accessibility enhancements to existing XAML

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TrueToneCap.App.Services;

/// <summary>
/// Provides centralized accessibility property mappings for WinUI controls
/// </summary>
public static class AccessibilityMapper
{
    private static readonly Dictionary<string, ControlAccessibilityInfo> s_mappings = new();

    static AccessibilityMapper()
    {
        // Output Settings Section
        Register("FormatCbo", "输出格式选择器", 
            "从 PNG、JPEG、AVIF 等格式中选择截图保存格式");
        
        Register("QualitySld", "图像质量滑块",
            "拖动滑块调整图像压缩质量（范围 1-100）",
            labeledBy: "QualityLabel", valueBinding: "Value");
        
        Register("QualityTxt", "质量数值输入框",
            "直接输入 1-100 的质量值");
        
        Register("PathTxt", "保存路径文本框",
            "设置截图文件的保存目录，支持绝对或相对路径");
        
        Register("PrefixTxt", "文件名前缀输入框",
            "设置导出文件的命名前缀");
        
        Register("ArchiveChk", "启用文件归档复选框",
            "勾选后自动按日期组织截图到子文件夹");
        
        Register("ArchiveModeCbo", "归档方式选择器",
            "选择按年/按月/按日组织文件");
        
        // Capture Tab
        Register("HotkeyTxt", "截图快捷键设置框",
            "自定义屏幕截图的组合键快捷键");
        
        Register("CaptureHotkeyRecordBtn", "录制截图快捷键按钮",
            "点击后等待按下新的组合键");
        
        Register("PreviewChk", "预览缩略图复选框",
            "在右下角显示截图预览图片");
        
        // AI/OCR Tab
        Register("OcrEngineCbo", "OCR 引擎选择器",
            "选择文字识别引擎：GPU/CPU/系统默认");
        
        Register("TranslationModeCbo", "翻译模式选择器",
            "选择在线翻译或本地离线翻译");
        
        Register("TargetLangCbo", "目标语言选择器",
            "选择 OCR 文字需要翻译成的目标语言");
        
        // System Tab
        Register("ThemeCbo", "主题颜色选择器",
            "选择深色/浅色/OLED 灰应用外观");
        
        Register("LanguageCbo", "界面语言选择器",
            "切换界面语言：跟随系统/中文/English（跟随系统 = 系统语言是中文则中文，否则英文）");
        
        Register("ToastPositionCbo", "通知位置选择器",
            "选择提示弹窗的四个角位置");
    }

    private static void Register(string controlId, string name, string helpText, 
        string? labeledBy = null, string? valueBinding = null)
    {
        s_mappings[controlId] = new ControlAccessibilityInfo
        {
            Name = name,
            HelpText = helpText,
            LabeledBy = labeledBy,
            ValueBinding = valueBinding
        };
    }

    public static bool TryGetAutomationProperties(string controlId, out ControlAccessibilityInfo info)
    {
        return s_mappings.TryGetValue(controlId, out info!);
    }

    /// <summary>
    /// Applies all registered accessibility properties to a control by ID
    /// </summary>
    public static void ApplyToControl(UIElement control)
    {
        if (control is FrameworkElement element && 
            !string.IsNullOrEmpty(element.Name) && 
            TryGetAutomationProperties(element.Name, out var info))
        {
            AutomationProperties.SetName(element, info.Name);
            AutomationProperties.SetHelpText(element, info.HelpText);
            
            // Note: labeledBy would need XAML reference resolution - handled separately in code-behind
            LogService.Debug("Accessibility", $"Applied '{info.Name}' to '{element.Name}'");
        }
    }

    /// <summary>
    /// Batch apply accessibility properties to all controls in a panel
    /// </summary>
    public static async Task ApplyToPanelAsync(StackPanel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is FrameworkElement fe)
                ApplyToControl(fe);
        }
    }
}

/// <summary>
/// Accessibility information for a single control
/// </summary>
public sealed record ControlAccessibilityInfo
{
    public string Name { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string? LabeledBy { get; set; }
    public string? ValueBinding { get; set; }
}
