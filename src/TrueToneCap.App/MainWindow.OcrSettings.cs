// TrueToneCap.App/MainWindow.OcrSettings.cs
// MainWindow 的「OCR 引擎 / 翻译 / LLM 配置 UI」职责（partial class 拆分）
//
// 拆分说明（2026-08-29）：MainWindow.xaml.cs 过大，按职责拆分为多个 partial class 文件。
// 本文件承载 OCR 与翻译相关的 UI 逻辑（引擎状态、语言列表、LLM 提供商/模型/模式切换）。
//
// ⚠ partial class 拆分不改变运行时行为：所有成员仍属同一个 MainWindow 类型，
//   私有字段（_settings / _uiReady 等）跨文件直接可见。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.Core.Services;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 2026-08-09: 按引擎类型更新语言 UI。
    /// ONNX (PP-OCRv6) 为 50 语言统一字典 (ppocrv6_dict.txt), 语言选择不影响识别
    /// → 隐藏语言下拉框 + 显示统一模型说明; 仅 Windows OCR 真正按语言工作 → 显示选择。
    /// </summary>
    private void UpdateOcrLanguageVisibility(OcrEngineType engineType)
    {
        if (OcrLangPanel is null || OcrLangHintTxt is null) return;
        bool isWindowsOcr = engineType == OcrEngineType.WindowsOcr;
        OcrLangPanel.Visibility = isWindowsOcr ? Visibility.Visible : Visibility.Collapsed;
        OcrLangHintTxt.Visibility = isWindowsOcr ? Visibility.Collapsed : Visibility.Visible;
        OcrLangHintTxt.Text = isWindowsOcr
            ? ""
            : "ℹ PP-OCRv6 为 50 语言统一字典 (ppocrv6_dict.txt) — 自动识别所有语言，无需选择。";
    }

    private void UpdateOcrEngineStatus()
    {
        if (OcrEngineStatus is null) return;
        var selected = MultiOcrService.SelectedEngine;
        if (selected is not null)
        {
            OcrEngineStatus.Text = selected.Info.IsAvailable
                ? $"✅ 当前: {selected.Info.Name}"
                : $"⚠️ {selected.Info.Name} 不可用";
        }
        else
        {
            OcrEngineStatus.Text = "OCR 引擎探测中...";
        }
    }

    /// <summary>根据当前选中的引擎刷新语言下拉列表。</summary>
    private void PopulateOcrLanguages()
    {
        if (OcrLangCbo is null) return;
        var languages = MultiOcrService.GetSupportedLanguages();
        OcrLangCbo.Items.Clear();
        foreach (var lang in languages)
        {
            OcrLangCbo.Items.Add(new ComboBoxItem
            {
                Tag = lang.Id,
                Content = lang.DisplayName
            });
        }
        // 恢复上次保存的语言，或设置默认语言
        var savedLang = _settings?.OcrLanguage;
        bool found = false;
        if (!string.IsNullOrEmpty(savedLang))
        {
            foreach (ComboBoxItem item in OcrLangCbo.Items)
            {
                if ((string)item.Tag == savedLang) { item.IsSelected = true; found = true; break; }
            }
        }
        if (!found && OcrLangCbo.Items.Count > 0)
        {
            ((ComboBoxItem)OcrLangCbo.Items[0]).IsSelected = true;
        }
    }

    private void OnOcrLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (_settings is not null && OcrLangCbo.SelectedItem is ComboBoxItem item)
        {
            _settings.OcrLanguage = (string)item.Tag;
        }
    }

    // ═══ LLM 提供商/模型切换 ═══
    private void OnLlmProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (LlmProviderCbo.SelectedItem as ComboBoxItem)?.Tag as string;
        var (endpoint, model) = tag switch
        {
            "DeepSeek" => ("https://api.deepseek.com/v1", "deepseek-chat"),
            "DeepSeek-Flash" => ("https://api.deepseek.com/v1", "deepseek-v4-flash"),
            "GLM" => ("https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash"),
            "Gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash"),
            "SiliconFlow" => ("https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-72B-Instruct"),
            "OpenAI" => ("https://api.openai.com/v1", "gpt-4o-mini"),
            "Aliyun" => ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo"),
            "Moonshot" => ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
            _ => ("", "")
        };
        if (tag != "Custom" && !string.IsNullOrEmpty(endpoint))
        {
            LlmEndpointTxt.Text = endpoint;
            // 自动选中对应模型
            foreach (ComboBoxItem item in LlmModelCbo.Items)
            {
                if ((item.Tag as string) == model) { LlmModelCbo.SelectedItem = item; break; }
            }
            LlmModelTxt.Text = model;
        }
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (LlmModelCbo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag != null && tag != "custom")
            LlmModelTxt.Text = tag;
    }

    // ═══ 翻译模式切换 ═══
    private void OnTranslationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // 防止 XAML 初始化阶段控件尚未创建时崩溃
        if (TranslationModeCbo is null) return;

        var tag = (TranslationModeCbo.SelectedItem as ComboBoxItem)?.Tag as string;
        bool showLlm = tag is "LLM" or "Vision";

        if (LlmConfigCard is not null)
            LlmConfigCard.Visibility = showLlm ? Visibility.Visible : Visibility.Collapsed;
        if (FreeModeHint is not null)
            FreeModeHint.Visibility = tag == "Free" ? Visibility.Visible : Visibility.Collapsed;

        if (LlmModeHint is not null)
        {
            if (tag == "LLM")
                LlmModeHint.Text = "OCR 识别文字后，通过 LLM API 翻译为高质量译文";
            else if (tag == "Vision")
                LlmModeHint.Text = "截图直接发送给视觉 LLM，一步完成识别+翻译（实验性，需支持 Vision 的模型）";
        }

        // 同步到设置
        if (_settings is not null)
        {
            _settings.TranslationMode = tag ?? "Free";
            _settings.UseCustomLlm = showLlm;
        }
    }
}
