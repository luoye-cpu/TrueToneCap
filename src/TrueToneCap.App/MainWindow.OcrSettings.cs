// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using WinRT.Interop;             // WindowNative（本地导入的 FileOpenPicker 要挂 HWND）
using TrueToneCap.Core.Logging;   // CoreLog（下载结果落盘）
using TrueToneCap.Core.Models;    // TranslationPackageInfo（行状态文案要用 FormatSize）
using TrueToneCap.Core.Services;
using TrueToneCap.App.Services;   // LocaleManager（OCR 语言提示要接进对照表，2026-10-03）

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 2026-08-09: 按引擎类型更新语言 UI。
    /// ONNX (PP-OCRv6) 是统一字典单模型，语言参数不改变识别结果（OnnxOcrEngine 收下 lang 后从不使用）
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
            : LocaleManager.Tr("ℹ PP-OCRv6 是单模型、不随语言切换；中英文最准，日文需字高 ≥24px，韩文自动改走系统 OCR。");
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
            // 必须当场落盘：本程序按"开机自启 + 最小化到托盘"常驻数天，
            // 而全量 SaveSettings() 只在托盘【退出】那条路径上跑（MainWindow.xaml.cs:234）。
            // 少这一行时，用户选的 OCR 语言只活在内存里 —— 崩溃/注销/直接关机都会静默丢失，
            // 表现是"我明明改过，重启又变回去"，且日志里没有任何线索。
            SaveSettingsQuiet();
        }
    }

    // ═══ LLM 提供商/模型切换 ═══
    private void OnLlmProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (LlmProviderCbo.SelectedItem as ComboBoxItem)?.Tag as string;
        var (endpoint, model) = tag switch
        {
            // 2026-11-16 供应商预设：只列 OpenAI 兼容 chat/completions 入口。
            // 改这里必须同步 MainWindow.xaml 的 LlmProviderCbo/LlmModelCbo 与 OcrServiceTests.LlmProviderTables_AreClosed。
            "DeepSeek" => ("https://api.deepseek.com", "deepseek-flash"),
            "GLM" => ("https://open.bigmodel.cn/api/paas/v4", "glm-5.3-flash"),
            "Aliyun" => ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3.8-flash"),
            "SiliconFlow" => ("https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V4.1-Flash"),
            "Moonshot" => ("https://api.moonshot.cn/v1", "kimi-k3.1-pro"),
            "Volcengine" => ("https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2-1-pro-260915"),
            "MiniMax" => ("https://api.minimax.cn/v1", "MiniMax-M3"),
            "OpenAI" => ("https://api.openai.com/v1", "gpt-5.4-mini"),
            "Anthropic" => ("https://api.anthropic.com/v1/", "claude-sonnet-5"),
            "Gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash"),
            "xAI" => ("https://api.x.ai/v1", "grok-4.7"),
            "Mistral" => ("https://api.mistral.ai/v1", "mistral-large-latest"),
            // 聚合平台：OpenRouter 模型 ID 带 provider 前缀；OpenCode Zen 只选 chat/completions 的模型。
            "OpenRouter" => ("https://openrouter.ai/api/v1", "google/gemini-3.8-flash"),
            "OpenCode" => ("https://opencode.ai/zen/v1", "deepseek-v4.1-flash"),
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
        // 有道凭据输入 + 非官方端点开关整组一起显隐（藏在面板里，否则 LLM 模式下会多出一排无关控件）
        if (FreeModePanel is not null)
            FreeModePanel.Visibility = tag == "Free" ? Visibility.Visible : Visibility.Collapsed;

        // 离线翻译包卡片（2026-11-11 离线翻译包方案）：仅 Offline 模式展开。
        // 展开时顺带刷运行环境状态与语言包列表 —— 探测要起子进程，放后台任务里。
        // 显隐本身不带 _uiReady 闸（与上面 LlmConfigCard 同级）：ApplySettingsToUI 回显
        // 也要走到这里，否则"上次退出时选的 Offline"重启后卡片不展开。
        bool showOffline = tag == "Offline";
        if (OfflineTranslateCard is not null)
            OfflineTranslateCard.Visibility = showOffline ? Visibility.Visible : Visibility.Collapsed;
        if (showOffline)
        {
            _ = RefreshArgosRuntimeStatusAsync();
            RefreshOfflinePackages();
        }

        if (LlmModeHint is not null)
        {
            if (tag == "LLM")
                LlmModeHint.Text = "OCR 识别文字后，通过 LLM API 翻译为高质量译文";
            else if (tag == "Vision")
                LlmModeHint.Text = "整张截图直传给支持图片的 LLM，一次完成识别+翻译。";
        }

        // 同步到设置
        //
        // ⚠ 必须带 _uiReady 守卫（与 OnOcrLangChanged / OnLogVerbosityChanged 同一道闸）：
        // XAML 里「免费翻译」那一项写着 IsSelected="True"，这会在 InitializeComponent 期间
        // 就触发一次 SelectionChanged —— 那时 _settings 已是读盘后的对象，_uiReady 还是 false。
        // 少了这个守卫，实测（2026-10-29）：盘上 TranslationMode=Vision 启动后被改成 Free
        // 并当场落盘（差量写盘把该键连同 UseCustomLlm 一起剪掉），用户选过的 LLM/视觉翻译
        // 每次重启都退回免费翻译，界面上看不出任何报错。
        // ApplySettingsToUI 里那次显式调用照旧执行：上面的显隐/提示同步不需要落盘，也必须在回显时跑。
        if (_settings is not null && _uiReady)
        {
            _settings.TranslationMode = tag ?? "Free";
            _settings.UseCustomLlm = showLlm;
            SaveSettingsQuiet();   // 同上：翻译模式是用户挑的后端，不能只活在内存里等托盘退出
        }
    }

    // ═══ 离线翻译包（2026-11-11 离线翻译包方案，实施方案 Phase 2/3）═══
    //
    // 交互形状：卡片里一个 ItemsControl，每行 = 语言对 + 状态 + [删除][下载]，
    // 下载进度直接写行内控件（行 VM 无 INPC —— 列表刷新时整表重建，运行中的进度
    // 走 FindName 拿控件改，这是本仓库 code-behind 惯例的延伸）。

    /// <summary>语言包管理器懒加载实例。目录默认 %LOCALAPPDATA%\TrueToneCap\translation-models，
    /// 与离线引擎共用同一份解析（OfflineArgosEngine.EffectiveModelsDir）。</summary>
    private TranslationPackageManager? _pkgManager;
    private TranslationPackageManager PkgManager => _pkgManager ??= new TranslationPackageManager();

    /// <summary>离线卡片一次只跑一个后台任务（下载/导入/装运行环境互斥）。
    /// 行内按钮禁用只锁得住自己那一行，跨行还得这道闸。</summary>
    private bool _offlineBusy;

    /// <summary>语言包列表的行数据。用普通 Binding（不走 x:Bind/INPC）：
    /// 列表每次刷新整表重建，运行中的动态内容直接改行内控件。</summary>
    private sealed class OfflinePackageRow
    {
        public required TranslationPackageInfo Source { get; init; }
        public string Identifier => Source.Identifier;
        // 显示名不走 ZhToEn 词表：模型自带双语名（NameChinese/NameEnglish），
        // 进词表反而会变成死键（死键判据扫不到 Tr(变量) 这种调用点）。
        public string DisplayName => LocaleManager.CurrentLanguage == AppLanguage.Chinese
            ? Source.NameChinese
            : Source.NameEnglish;
        public string StatusText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public Visibility DeleteVisibility => Source.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>重建语言包列表。整表重建是刻意的选择：状态变化点（装完/删完/导入完）
    /// 都是低频动作，重建一行数据比给行 VM 补 INPC 便宜且不会漏刷新。</summary>
    private void RefreshOfflinePackages()
    {
        if (OfflinePackageList is null) return;
        var rows = new List<OfflinePackageRow>();
        foreach (var pkg in PkgManager.AvailablePackages)
        {
            rows.Add(new OfflinePackageRow
            {
                Source = pkg,
                StatusText = pkg.IsInstalled
                    ? $"{LocaleManager.Tr("已安装")} · {TranslationPackageInfo.FormatSize(pkg.SizeBytes)}"
                    : pkg.SizeBytes > 0
                        ? $"{LocaleManager.Tr("约")} {TranslationPackageInfo.FormatSize(pkg.SizeBytes)}"
                        : LocaleManager.Tr("待下载"),
                ActionText = pkg.IsInstalled ? LocaleManager.Tr("已安装") : LocaleManager.Tr("下载"),
            });
        }
        OfflinePackageList.ItemsSource = rows;
    }

    /// <summary>刷运行环境状态。探测要起 Python 子进程（冷态 ~1s），放 Task.Run，
    /// 回来时窗口可能已关 —— 引用再判空。</summary>
    private async Task RefreshArgosRuntimeStatusAsync()
    {
        if (ArgosRuntimeStatusTxt is null) return;
        ArgosRuntimeStatusTxt.Text = LocaleManager.Tr("检测运行环境…");
        var status = await Task.Run(() => OfflineArgosEngine.GetRuntimeStatus());
        if (ArgosRuntimeStatusTxt is null) return;
        ArgosRuntimeStatusTxt.Text = status.PythonFound
            ? status.ArgosAvailable
                ? LocaleManager.Tr("✅ Python + argostranslate 就绪")
                : LocaleManager.Tr("⚠ 已装 Python，缺 argostranslate 包")
            : LocaleManager.Tr("⚠ 未检测到 Python");
        if (InstallArgosRuntimeBtn is not null)
            InstallArgosRuntimeBtn.Visibility = status.ArgosAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnInstallArgosRuntimeClick(object sender, RoutedEventArgs e)
    {
        if (_offlineBusy) return;
        _offlineBusy = true;
        InstallArgosRuntimeBtn.IsEnabled = false;
        ArgosRuntimeStatusTxt.Text = LocaleManager.Tr("⏳ 正在安装 argostranslate（数分钟，取决于网络）…");
        SetOfflineMsg("");
        try
        {
            var result = await OfflineArgosEngine.EnsureRuntimeAsync();
            ArgosRuntimeStatusTxt.Text = result.Success
                ? LocaleManager.Tr("✅ Python + argostranslate 就绪")
                : result.Message;
            if (!result.Success) SetOfflineMsg(result.Message);
        }
        catch (OperationCanceledException)
        {
            ArgosRuntimeStatusTxt.Text = LocaleManager.Tr("⚠ 未检测到 Python");
        }
        finally
        {
            _offlineBusy = false;
            if (InstallArgosRuntimeBtn is not null)
            {
                InstallArgosRuntimeBtn.IsEnabled = true;
                InstallArgosRuntimeBtn.Visibility = Visibility.Visible;
            }
        }
    }

    private async void OnDownloadPackageClick(object sender, RoutedEventArgs e)
    {
        if (_offlineBusy) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not OfflinePackageRow row) return;
        if (PkgManager.IsPackageInstalled(row.Identifier))
        {
            SetOfflineMsg(LocaleManager.Tr("✅ 该语言包已安装，无需重复下载"));
            return;
        }

        var root = FindRowRoot(fe);
        var (bar, statusTxt, downloadBtn, deleteBtn) = TryGetRowControls(root);
        // ⚠ 局部变量名照抄控件名：进度条在行 DataTemplate 里（x:Name 不生成 codegen 字段，
        // 经 FindName 取得），而"每个 Collapsed 控件必须有代码点亮"的门禁按 `PkgProgressBar.Visibility`
        // 这个形状找点亮代码 —— 这里就是真正点亮它的那行，让判据能追到。
        var PkgProgressBar = bar;
        _offlineBusy = true;
        if (downloadBtn is not null) downloadBtn.IsEnabled = false;
        if (deleteBtn is not null) deleteBtn.IsEnabled = false;
        if (PkgProgressBar is not null) { PkgProgressBar.Visibility = Visibility.Visible; PkgProgressBar.IsIndeterminate = true; }
        if (statusTxt is not null) statusTxt.Text = LocaleManager.Tr("下载中…");
        SetOfflineMsg("");

        try
        {
            var result = await PkgManager.DownloadPackageAsync(row.Identifier, (done, total) =>
            {
                // 进度回调在线程池上触发 → 必须经调度队列回 UI 线程改控件
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (PkgProgressBar is null) return;
                    if (total > 0)
                    {
                        PkgProgressBar.IsIndeterminate = false;
                        PkgProgressBar.Value = Math.Min(1.0, (double)done / total);
                        if (statusTxt is not null)
                            statusTxt.Text = $"{done / 1048576.0:F0}MB / {total / 1048576.0:F0}MB（{(double)done / total:P0}）";
                    }
                    else if (statusTxt is not null)
                    {
                        statusTxt.Text = $"{done / 1048576.0:F0} MB";
                    }
                });
            });
            SetOfflineMsg((result.Success ? "" : "❌ ") + result.Message);
            CoreLog.Info("OfflineTranslate", $"[UI] {row.Identifier} 下载结果：{result.Message}");
        }
        finally
        {
            _offlineBusy = false;
            RefreshOfflinePackages();   // 整表重建会换掉行内控件引用，禁用态自然复位
        }
    }

    private void OnUninstallPackageClick(object sender, RoutedEventArgs e)
    {
        if (_offlineBusy) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not OfflinePackageRow row) return;
        bool ok = PkgManager.UninstallPackage(row.Identifier);
        SetOfflineMsg(ok
            ? LocaleManager.Tr("🗑 已删除语言包")
            : (LocaleManager.Tr("❌ 删除失败") + "：" + row.Identifier));
        if (ok) RefreshOfflinePackages();
    }

    private async void OnImportPackageClick(object sender, RoutedEventArgs e)
    {
        if (_offlineBusy) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".argosmodel");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            _offlineBusy = true;
            SetOfflineMsg(LocaleManager.Tr("⏳ 正在导入…"));
            var result = await PkgManager.InstallFromFileAsync(file.Path);
            SetOfflineMsg((result.Success ? "" : "❌ ") + result.Message);
            if (result.Success) RefreshOfflinePackages();
        }
        catch (Exception ex)
        {
            SetOfflineMsg("❌ " + ex.Message);
        }
        finally
        {
            _offlineBusy = false;
        }
    }

    private void OnOpenModelsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PkgManager.ModelsDir);
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(PkgManager.ModelsDir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetOfflineMsg("❌ " + ex.Message);
        }
    }

    private void SetOfflineMsg(string msg)
    {
        if (OfflinePkgMsgTxt is null) return;
        OfflinePkgMsgTxt.Text = msg;
        OfflinePkgMsgTxt.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>从模板内任意元素向上找 DataTemplate 根（它的 namescope 持有行内 x:Name 元素）。
    /// 根的父级是 ItemsControl 生成的 ContentPresenter。</summary>
    private static FrameworkElement? FindRowRoot(DependencyObject start)
    {
        DependencyObject cur = start;
        while (true)
        {
            var parent = VisualTreeHelper.GetParent(cur);
            if (parent is null) return cur as FrameworkElement;
            if (parent is ContentPresenter) return cur as FrameworkElement;
            cur = parent;
        }
    }

    private static (ProgressBar? Bar, TextBlock? Status, Button? Download, Button? Delete) TryGetRowControls(FrameworkElement? root) => (
        root?.FindName("PkgProgressBar") as ProgressBar,
        root?.FindName("PkgStatusTxt") as TextBlock,
        root?.FindName("PkgDownloadBtn") as Button,
        root?.FindName("PkgDeleteBtn") as Button);
}
