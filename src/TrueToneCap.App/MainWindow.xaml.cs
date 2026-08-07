using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Microsoft.UI;
using WinRT.Interop;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Metadata;
using TrueToneCap.App.Services;
using TrueToneCap.App.Models;
using TrueToneCap.Core.Services;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    private AppSettingsData _settings => AppServices.Settings.Current;
    private TrayIconManager? _trayIcon;
    private readonly List<(OutputFormat Format, string Label)> _formats;
    private volatile int _isCapturing; // 0=idle, 1=busy (防重入)
    private CancellationTokenSource? _captureCts; // 截图/编码取消令牌
    private bool _isExiting;           // 托盘退出标志（跳过最小化）
    private TextBox? _recordingTarget; // 正在录制的快捷键输入框

    // ── 通过 AppServices 访问共享服务（不再本地持有）──
    private WgcCaptureService? _wgcService => AppServices.Wgc;
    private GpuToneMapper? _gpuToneMapper => AppServices.GpuToneMapper;

    public MainWindow(bool isAutostart = false)
    {
        this.InitializeComponent();

        // ═══ 关键修复: _uiReady 在 InitializeComponent 后立即设为 true ═══
        // 但 ApplySettingsToUI 会短暂设为 false 防止事件处理函数覆盖 _settings
        _uiReady = true;

        // ── 拦截窗口关闭 → 最小化到托盘（WinUI 3 必须用 AppWindow.Closing）──
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        // ── 扩展内容到标题栏（消除深色模式顶部白条）──
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        appWindow.Closing += (_, e) =>
        {
            if (_isExiting) return; // 托盘"退出"→正常关闭，不拦截

            // ── 无论是否勾选"最小化到托盘"，都先隐藏窗口 ──
            e.Cancel = true;
            _trayIcon?.MinimizeToTray();

            // 未勾选 → 后台静默退出（不占前台资源）
            if (MinimizeTrayChk.IsChecked != true)
            {
                _isExiting = true;
                // 延迟退出：先让窗口动画完成，再后台保存+清理
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(200);         // 等隐藏动画完成
                    try { SaveSettings(); } catch { }
                    _trayIcon?.RemoveIcon();
                    _trayIcon?.Dispose();
                    AppServices.Shutdown();
                    Environment.Exit(0);
                });
            }
        };

        // ── 字体注入：在 Content 加载完成后递归遍历可视化树 ──
        // 使用用户选择的字体（如果已设置），否则使用默认回退链
        string initialFont = FontLoader.GetEffectiveFontFamily(_settings.FontFamily);
        if (this.Content is FrameworkElement fe)
        {
            if (fe.IsLoaded)
                FontHelper.ApplyFontToVisualTree(fe, initialFont);
            else
                fe.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(fe, initialFont);
        }

        _formats =
        [
            (OutputFormat.PNG, "PNG (无损)"),
            (OutputFormat.JPEG_GAINMAP, "JPEG Gain Map (HDR)"),
            (OutputFormat.JPEG_LI, "JPEG LI"),
            (OutputFormat.JPEG_XL, "JPEG XL"),
            (OutputFormat.AVIF, "AVIF"),
            (OutputFormat.WebP, "WebP"),
            (OutputFormat.TIFF, "TIFF"),
        ];

        FormatCbo.ItemsSource = _formats.Select(f => f.Label).ToList();
        LoadSettings();             // 仅加载配置文件，不做检测

        // ═══ 关键修复: ApplySettingsToUI 期间禁止事件覆盖 _settings ═══
        _uiReady = false;
        ApplySettingsToUI();        // 将配置反映到 UI
        _uiReady = true;

        // 同步窗口主题到 UI（启动时 ApplyTheme 已设置 Application.RequestedTheme，
        // 但窗口内容元素 fe.RequestedTheme 需要单独设置才能生效）
        SyncWindowTheme();
        UpdateQualityPanel();

        StatusTxt.Text = "能力检测中...";

        _trayIcon = new TrayIconManager(this);
        _trayIcon.OnCaptureHotkey = () => DispatcherQueue.TryEnqueue(() => StartSelectionCapture());
        _trayIcon.OnExitApp = () => _isExiting = true;
        _trayIcon.RegisterCaptureHotkey(_settings.Hotkey);

        // 注册无感截图热键
        HotkeyManager.RegisterNamed(this, "silent", _settings.SilentHotkey,
            () => DispatcherQueue.TryEnqueue(() => SilentCapture()),
            ["Ctrl+Alt+Q", "Alt+Shift+Q"]);

        // ── 子类化窗口过程以处理托盘消息 ──
        SubclassWindowForTray(hwnd);

        // 开机自启动状态同步
        try { StartupManager.IsEnabled = _settings.AutoStart; }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[MainWindow] 开机启动注册失败: {ex.Message}"); }

        // ── 开机静默启动：直接缩小到托盘，不显示窗口 ──
        if (isAutostart)
        {
            _trayIcon.MinimizeToTray();
            // 窗口已隐藏，但仍需运行能力检测（确保后续截图正常）
        }

        // ── 异步延迟检测（不阻塞窗口显示） ──
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = RunCapabilityDetectionAsync();
        });

        // ── WGC/GPU 管线已由 AppServices.Initialize() 在 App 启动时完成 ──
        // 仅在 UI 线程预热 WGC 会话池（WGC 需要 STA 消息泵才能收到帧）
        if (_wgcService is not null)
            DispatcherQueue.TryEnqueue(() => _wgcService.WarmupSessions());

        // ── 注册 Toast 通知 ──
        ToastService.Register();

        // ── 初始化 OCR 引擎（后台加载 ONNX 模型，不阻塞窗口显示）──
        // 模型目录自动解析: 应用内嵌 data/Models/ → 用户 %LOCALAPPDATA%
        try
        {
            var modelDir = TrueToneCap.Core.Services.OnnxOcrEngine.ResolveModelDir();
            _ = Task.Run(() => MultiOcrService.Initialize(modelDir));
            System.Diagnostics.Debug.WriteLine($"[MainWindow] OCR 引擎初始化已后台启动, 模型目录: {modelDir}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] OCR 初始化失败: {ex.Message}");
        }

        // ── 应用初始语言 ──
        var initLang = _settings.Language == "en" ? AppLanguage.English : AppLanguage.Chinese;
        LocaleManager.SetLanguage(initLang);

        // ═══ _uiReady 在 ApplySettingsToUI 之后已置为 true ═══

        // ── 订阅实时日志事件 ──
        SubscribeLogEvents();

        // ── 应用本地化文本 + 显示第一页 ──
        ApplyLocale();
        MainNav.SelectedItem = MainNav.MenuItems[0];
        PageOutput.Visibility = Visibility.Visible;
    }

    private async Task RunCapabilityDetectionAsync()
    {
        try
        {
            await Task.Delay(300);
            await DetectAndApplySystemCapabilitiesAsync();
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "就绪");
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"⚠ {ex.Message}");
        }
    }

    /// <summary>一次性检测所有系统能力（HDR/ACM/ICC/色彩空间），更新 UI 和设置。</summary>
    private async Task DetectAndApplySystemCapabilitiesAsync()
    {
        var cap = await AppServices.Capability.DetectAllAsync();
        _settings.AcmeDetected = cap.SystemAcm;
        _settings.NvencAvailable = cap.NvencAvailable;
        _settings.QsvAvailable = cap.QsvAvailable;
        _settings.DisplayBitDepth = cap.DisplayBitDepth;
        _settings.SystemSdrWhiteLevel = cap.DisplayPaperWhiteNits;

        // ACM 不再强制禁用 ICC 烘焙：用户可选择输出到任意色域，
        // ACM 仅保证显示器正确显示，不影响截图输出色彩空间。
        // 输出到非显示器色域时需要 ICC 烘焙来转换像素值。

        if (_settings.FirstRun)
        {
            _settings.FirstRun = false;
            // HDR 硬件支持时默认开启 HDR 模式（即使 Windows HDR 当前未开启）
            _settings.HdrEnabled = cap.SupportsHdr;
            _settings.IccBakeEnabled = cap.IccBakeAvailable;
            _settings.ColorSpaceIndex = CapabilityService.DetectBestColorSpace(
                cap.SystemHdr, cap.SupportsHdr, cap.SystemAcm, cap.CustomIcc);
            AppServices.Settings.SaveQuiet();
        }

        // 更新 UI
        DispatcherQueue.TryEnqueue(() =>
        {
            // 无论 HDR 当前是否开启，只要硬件支持就允许用户切换
            HdrSwitch.IsEnabled = cap.SupportsHdr;
            HdrSwitch.IsOn = _settings.HdrEnabled;

            string hdrText;
            if (cap.SupportsHdr)
            {
                hdrText = cap.SystemHdr
                    ? $"✅ HDR 已启用 ({cap.DisplayBitDepth}-bit)"
                    : $"⚠ HDR 硬件支持但未开启（Windows 显示设置中未开启 HDR）";
            }
            else
            {
                hdrText = "⚠ 此显示器不支持 HDR";
            }
            var acmText = cap.SystemAcm ? " | ACM 已启用（系统管理显示器色彩）" : "";
            HdrHintTxt.Text = hdrText + acmText;

            IccBakeSwitch.IsEnabled = cap.IccBakeAvailable;
            IccBakeSwitch.IsOn = _settings.IccBakeEnabled;
            if (cap.SystemAcm)
                IccHintTxt.Text = "ACM 已启用 — ICC 烘焙可用于输出到非显示器色域（如 BT.2020/P3）。";
            else if (cap.CustomIcc)
                IccHintTxt.Text = $"检测到校色 ICC → 自动烘焙到 {ColorProfileProvider.GetColorSpaceDisplayName(GetSelectedColorSpaceTag())}";
            else
                IccHintTxt.Text = "未检测到校色 ICC；sRGB 目标不嵌入，非 sRGB 嵌入标准 ICC";

            UpdateAvifBackendLabels();
        });
    }

    /// <summary>更新 AVIF 后端列表，标记可用的硬件编码器。</summary>
    private void UpdateAvifBackendLabels()
    {
        foreach (ComboBoxItem item in AvifBackendCbo.Items)
        {
            var tag = item.Tag as string;
            if (tag == "Qsv" && !_settings.QsvAvailable)
                item.Content = "Intel QSV (不可用)";
            else if (tag == "Qsv" && _settings.QsvAvailable)
                item.Content = "Intel QSV ✓";
            else if (tag == "Nvenc" && !_settings.NvencAvailable)
                item.Content = "NVIDIA NVENC (不可用)";
            else if (tag == "Nvenc" && _settings.NvencAvailable)
                item.Content = "NVIDIA NVENC ✓";
        }
    }

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
        ColorCbo.SelectedIndex = Math.Clamp(_settings.ColorSpaceIndex, 0, 6);
        HotkeyTxt.Text = _settings.Hotkey;
        RecordHotkeyTxt.Text = _settings.RecordHotkey;
        SilentHotkeyTxt.Text = _settings.SilentHotkey;
        AutoStartChk.IsChecked = _settings.AutoStart;
        PreviewChk.IsChecked = _settings.ShowPreview;
        MinimizeTrayChk.IsChecked = _settings.MinimizeToTray;
        ToastCaptureChk.IsChecked = _settings.ToastOnCapture;
        ToastSilentChk.IsChecked = _settings.ToastOnSilentCapture;
        ToastRecordChk.IsChecked = _settings.ToastOnRecording;
        SetComboByTag(ToastPositionCbo, _settings.ToastPosition);
        SetComboByTag(OverlayColorCbo, _settings.OverlayColor);
        SetComboByTag(BorderColorCbo, _settings.BorderColor);
        AvifPngSuffixChk.IsChecked = _settings.AvifPngSuffix;
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
        // Gain Map 模式
        if (GainMapModeCbo is not null) SetComboByTag(GainMapModeCbo, _settings.GainMapMode);
    }

    private static void SetComboByTag(ComboBox cbo, string tag)
    {
        foreach (ComboBoxItem item in cbo.Items)
        { if ((string)item.Tag == tag) { item.IsSelected = true; return; } }
    }

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
            _settings.AutoStart = AutoStartChk.IsChecked == true;
            _settings.ShowPreview = PreviewChk.IsChecked == true;
            _settings.MinimizeToTray = MinimizeTrayChk.IsChecked == true;
            _settings.ToastOnCapture = ToastCaptureChk.IsChecked == true;
            _settings.ToastOnSilentCapture = ToastSilentChk.IsChecked == true;
            _settings.ToastOnRecording = ToastRecordChk.IsChecked == true;
            _settings.ToastPosition = (ToastPositionCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "BottomRight";
            _settings.OverlayColor = (OverlayColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#99001833";
            _settings.BorderColor = (BorderColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4488FF";
            _settings.AvifPngSuffix = AvifPngSuffixChk.IsChecked == true;
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
                3 => _settings.BitDepthJpegXl,        // JPEG XL
                4 => _settings.BitDepthAvif,          // AVIF
                6 => _settings.BitDepthTiff,          // TIFF
                _ => 8,                                // 其他格式固定 8-bit
            };

            // 通过 SettingsService 持久化
            AppServices.Settings.Save();

            // 热键 + 自启同步
            try { StartupManager.IsEnabled = _settings.AutoStart; } catch { }
            try { _trayIcon?.RegisterCaptureHotkey(_settings.Hotkey); } catch { }

            StatusTxt.Text = "✅ 设置已保存";
        }
        catch (Exception ex) { StatusTxt.Text = "❌ 保存失败: " + ex.Message; }
    }

    // ── 浏览文件夹 ──

    private async void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) { PathTxt.Text = folder.Path; _settings.OutputPath = folder.Path; AppServices.Settings.SaveQuiet(); }
    }

    // ── 动态格式面板 ──

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateQualityPanel();
        _settings.FormatIndex = FormatCbo.SelectedIndex;
        AppServices.Settings.SaveQuiet();
    }

    private void UpdateQualityPanel()
    {
        if (FormatCbo.SelectedIndex < 0) return;
        var (format, _) = _formats[FormatCbo.SelectedIndex];
        var encoder = EncoderFactory.Create(format);
        var (min, max, def, label) = encoder.GetQualityRange();

        QualityLabel.Text = label;
        QualitySld.Minimum = min;
        QualitySld.Maximum = max;
        QualitySld.SmallChange = 0.1;
        QualitySld.LargeChange = 0.5;
        bool precise = format is OutputFormat.JPEG_LI or OutputFormat.JPEG_XL;
        QualitySld.StepFrequency = precise ? 0.1 : 1.0;
        QualitySld.IsEnabled = format != OutputFormat.PNG;

        // ── 格式专属提示 ──
        FormatHintTxt.Text = format switch
        {
            OutputFormat.PNG => "✅ 无损格式，支持 HDR (cICP Rec.2100 PQ)。截图首选。",
            OutputFormat.JPEG_LI => "✅ Google jpegli 编码，butteraugli 距离控制质量。体积小，兼容性最佳。",
            OutputFormat.JPEG_GAINMAP => "✅ Ultra HDR (ISO 21496-1)，兼容 JPEG。SDR/HDR 自适应。",
            OutputFormat.JPEG_XL => "✅ 新一代格式，Modular 模式对截图极优。支持 HDR。",
            OutputFormat.AVIF => "✅ 先进格式，支持 HDR + 硬件加速编码。默认 4:4:4。",
            OutputFormat.WebP => "⚠ 仅 SDR 8-bit。适合简单分享场景。",
            OutputFormat.TIFF => "✅ TIFF 无损格式，支持 16-bit HDR + ICC 嵌入。适合存档。",
            _ => ""
        };

        // ── 格式专属选项卡片 ──
        bool isAvif = format == OutputFormat.AVIF;
        bool isGainMap = format == OutputFormat.JPEG_GAINMAP;
        AvifOptionsCard.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        GainMapOptionsCard.Visibility = isGainMap ? Visibility.Visible : Visibility.Collapsed;
        PngOptionsCard.Visibility = format == OutputFormat.PNG ? Visibility.Visible : Visibility.Collapsed;
        JpegXlOptionsCard.Visibility = format == OutputFormat.JPEG_XL ? Visibility.Visible : Visibility.Collapsed;
        JpegLiOptionsCard.Visibility = format == OutputFormat.JPEG_LI ? Visibility.Visible : Visibility.Collapsed;
        WebPOptionsCard.Visibility = format == OutputFormat.WebP ? Visibility.Visible : Visibility.Collapsed;
        TiffOptionsCard.Visibility = format == OutputFormat.TIFF ? Visibility.Visible : Visibility.Collapsed;
        // P1-3: AVIF 位深选项卡片（与 AvifOptionsCard 联动）
        if (AvifBitDepthCard is not null)
            AvifBitDepthCard.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        if (isGainMap)
        {
            var gmTag = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray";
            GainMapHintTxt.Text = gmTag == "Gray"
                ? "灰度增益：仅编码亮度差，体积最小。黑白文字/图标场景推荐。"
                : "RGB 增益：三通道独立编码，色彩还原最准确。彩色截图推荐。";
        }

        // AVIF + NVENC/QSV 不支持 CRF=0 无损
        if (isAvif)
        {
            int backendIdx = AvifBackendCbo.SelectedIndex;
            if (backendIdx is 2 or 3 || (backendIdx == 0 && (_settings.NvencAvailable || _settings.QsvAvailable)))
                QualitySld.Minimum = Math.Max(min, 1.0);
        }

        // Quality 优先使用该格式已保存值（每格式独立，切换格式不互相覆盖）
        int fmtIdx = FormatCbo.SelectedIndex;
        double savedQ = _settings.GetQuality(fmtIdx);
        double useQ = (savedQ >= QualitySld.Minimum && savedQ <= QualitySld.Maximum) ? savedQ : def;
        QualitySld.Value = useQ;
        QualityLbl.Text = useQ.ToString("F1");
        QualityTxt.Text = useQ.ToString("F1");
        QualityTxt.Visibility = precise ? Visibility.Visible : Visibility.Collapsed;
        // 长质量描述放到独立整行（避免被窄列遮挡）
        QualityDescTxt.Text = encoder.GetQualityDescription((float)useQ);
        QualityDescTxt.Visibility = Visibility.Visible;
    }

    private void OnAvifBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateQualityPanel();
        _settings.AvifBackendIndex = AvifBackendCbo.SelectedIndex;
        AppServices.Settings.SaveQuiet();
    }

    private void OnGainMapModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray";
        GainMapHintTxt.Text = tag == "Gray"
            ? "灰度增益：仅编码亮度差，体积最小。黑白文字/图标场景推荐。"
            : "RGB 增益：三通道独立编码，色彩还原最准确。彩色截图推荐。";
        _settings.GainMapMode = tag;
        AppServices.Settings.SaveQuiet();
    }

    private void OnArchiveChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ArchiveModePanel.Visibility = ArchiveChk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _settings.ArchiveEnabled = ArchiveChk.IsChecked == true;
        AppServices.Settings.SaveQuiet();
    }

    private void OnRecordQualityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.RecordQuality = Math.Round(RecordQualitySld.Value, 1);
        AppServices.Settings.SaveQuiet();
    }

    private void OnTargetLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.TargetLanguage = (TargetLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
        AppServices.Settings.SaveQuiet();
    }

    private void OnQualityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_uiReady || FormatCbo.SelectedIndex < 0) return;
        var (format, _) = _formats[FormatCbo.SelectedIndex];
        var encoder = EncoderFactory.Create(format);
        double val = Math.Round(QualitySld.Value, 1);
        QualityLbl.Text = val.ToString("F1");
        QualityTxt.Text = val.ToString("F1");
        QualityDescTxt.Text = encoder.GetQualityDescription((float)val);
        // 保存到当前格式的独立质量字段（切换格式不互相覆盖）
        _settings.SetQuality(FormatCbo.SelectedIndex, val);
        AppServices.Settings.SaveQuiet();
    }

    private void OnQualityTextChanged(object sender, TextChangedEventArgs e)
    {
        if (double.TryParse(QualityTxt.Text, out double v))
        {
            v = Math.Clamp(v, QualitySld.Minimum, QualitySld.Maximum);
            QualitySld.Value = v;
            QualityLbl.Text = v.ToString("F1");
        }
    }

    private void OnQualityTextLostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(QualityTxt.Text, out double v))
        {
            v = Math.Clamp(Math.Round(v, 1), QualitySld.Minimum, QualitySld.Maximum);
            QualityTxt.Text = v.ToString("F1");
            QualitySld.Value = v;
            QualityLbl.Text = v.ToString("F1");
            _settings.SetQuality(FormatCbo.SelectedIndex, v);
            AppServices.Settings.SaveQuiet();
        }
        else QualityTxt.Text = QualitySld.Value.ToString("F1");
    }

    // ── HDR + ACM + ICC ──

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.HdrEnabled = HdrSwitch.IsOn;

        // 选 System 时检测显示器真实色域
        if (GetSelectedColorSpaceTag() == "System")
            DetectAndShowSourceGamut();
        UpdateGamutMappingUI();
        AppServices.Settings.SaveQuiet();
    }

    private void OnIccBakeToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.IccBakeEnabled = IccBakeSwitch.IsOn;
        AppServices.Settings.SaveQuiet();
    }

    private void OnColorSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = GetSelectedColorSpaceTag();
        bool hdrOn = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
        bool isSRgb = tag is "System" or "sRGB";

        // 选 System 时检测显示器真实色域
        if (tag == "System")
            DetectAndShowSourceGamut();

        if (_settings.AcmeDetected)
        {
            IccHintTxt.Text = isSRgb
                ? "ACM 已启用 — sRGB 输出无需 ICC 烘焙。"
                : $"ACM 已启用 — ICC 烘焙将像素从显示器色域转换到 {ColorProfileProvider.GetColorSpaceDisplayName(tag)}。";
        }
        else if (IccBakeSwitch.IsEnabled)
        {
            IccHintTxt.Text = isSRgb
                ? "烘焙目标: sRGB（不嵌入 ICC，sRGB 是通用默认）"
                : $"烘焙目标: {ColorProfileProvider.GetColorSpaceDisplayName(tag)}（将嵌入标准 ICC）";
        }
        UpdateGamutMappingUI();
        _settings.ColorSpaceIndex = ColorCbo.SelectedIndex;
        AppServices.Settings.SaveQuiet();
    }

    private void OnOverlayColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.OverlayColor = (OverlayColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#99001833";
        SaveSettingsQuiet();
    }

    private void OnBorderColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.BorderColor = (BorderColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4488FF";
        SaveSettingsQuiet();
    }

    /// <summary>获取当前鼠标所在显示器的原生色域标签（ACM 感知）。</summary>
    private string GetDisplayNativeGamut()
    {
        try
        {
            var monitor = DisplayEnumerator.GetMonitorUnderCursor();
            return ColorProfileProvider.GetDisplayNativeGamutTag(monitor);
        }
        catch { return "sRGB"; }
    }

    /// <summary>检测当前显示器色域并显示在 UI 中（ACM 感知）。</summary>
    private void DetectAndShowSourceGamut()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (primary is not null)
            {
                string csName;
                if (primary.IsHdr)
                {
                    csName = $"HDR (BT.2020/PQ, {primary.BitsPerColor}-bit)";
                }
                else if (primary.SupportsHdr)
                {
                    csName = $"HDR 未开启 (BT.2020 硬件, {primary.BitsPerColor}-bit)";
                }
                else if (_settings.AcmeDetected)
                {
                    // ACM 启用时检测显示器原生色域
                    var nativeGamut = GetDisplayNativeGamut();
                    csName = $"SDR ({nativeGamut}, ACM, {primary.BitsPerColor}bit)";
                }
                else
                {
                    csName = $"SDR (sRGB, {primary.BitsPerColor}bit)";
                }
                SourceGamutTxt.Text = csName;
            }
            else
            {
                SourceGamutTxt.Text = "sRGB (默认)";
            }
        }
        catch
        {
            SourceGamutTxt.Text = "sRGB (默认)";
        }
    }

    /// <summary>更新色域映射 UI（HDR 感知 + ACM 感知）。</summary>
    private void UpdateGamutMappingUI()
    {
        var sourceTag = SourceGamutTxt.Text;
        var targetTag = (ColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        bool hdrOn = HdrSwitch.IsOn && HdrSwitch.IsEnabled;

        // 判断源显示器特性
        bool sourceIsHdr = sourceTag.Contains("HDR (BT.2020/PQ");
        bool sourceIsHdrCapable = sourceTag.Contains("HDR 未开启");
        bool sourceIsAcm = sourceTag.Contains("ACM");
        bool sourceIsWide = sourceIsHdr || sourceIsHdrCapable
            || sourceTag.Contains("P3") || sourceTag.Contains("BT.2020") || sourceTag.Contains("AdobeRGB");

        // 有效目标色域：HDR 开启时 "System" 解析为 BT.2020
        string effectiveTarget = targetTag == "System" && hdrOn ? "BT2020" : targetTag;
        bool targetIsExplicitWide = effectiveTarget is "BT2020" or "DisplayP3" or "DCI_P3" or "AdobeRGB";
        bool targetIsSystem = effectiveTarget == "System";
        bool targetIsSystemWide = targetIsSystem && sourceIsAcm && sourceIsWide;
        bool targetIsWide = targetIsExplicitWide || targetIsSystemWide;

        // 构造目标色域显示名
        string targetName = targetTag switch
        {
            "System" when hdrOn => "BT.2020 (HDR10)",
            "System" when sourceIsHdr => "sRGB (色调映射)",
            "System" when sourceIsAcm && sourceIsWide => sourceTag.Replace("SDR (", "").Replace(", ACM", "").Replace(", 8bit)", "").Replace(", 10bit)", ""),
            "System" => sourceTag.Contains("HDR") ? "sRGB" : sourceTag,
            "sRGB" => "sRGB",
            "DisplayP3" => "Display P3",
            "DCI_P3" => "DCI-P3",
            "AdobeRGB" => "Adobe RGB",
            "BT2020" => "BT.2020",
            _ => "sRGB"
        };
        TargetGamutTxt.Text = targetName;

        if (hdrOn)
        {
            // HDR 开启 → 色域矩阵 → PQ → CICP，保留用户选择的色域
            string matrixDesc = targetTag switch
            {
                "DisplayP3" or "DCI_P3" => "scRGB→P3 矩阵",
                "AdobeRGB" => "scRGB→AdobeRGB 矩阵",
                "BT2020" => "scRGB→BT.2020 矩阵",
                _ => "scRGB→BT.2020 矩阵"
            };
            byte cicpP = targetTag switch
            {
                "DisplayP3" or "DCI_P3" => 12,
                "AdobeRGB" => 1,
                _ => 9  // BT.2020 / System
            };
            MappingArrow.Text = $"→ HDR 直通 ({targetName})";
            GamutMapHintTxt.Text = $"HDR 编码路径：WGC Float16 → {matrixDesc} → PQ ST.2084 → CICP(primaries={cicpP}, transfer=16)。"
                + (targetTag is "DisplayP3" or "DCI_P3" or "AdobeRGB"
                    ? "\n⚠ 注意：HDR 输出使用非标准 HDR10 容器色域。部分播放器/显示器可能无法正确解析。"
                    : "");
        }
        else if (targetIsWide)
        {
            // HDR 关闭 + 广色域目标 → WGC Float16 捕获 → 色域转换 → 色调映射到 SDR
            bool canUseFloat16 = sourceIsWide || sourceIsAcm || _settings.IccBakeEnabled;
            if (canUseFloat16)
            {
                MappingArrow.Text = "→ Float16 广色域捕获 → 色域映射";
                GamutMapHintTxt.Text = $"WGC Float16 捕获完整广色域 → 3×3 矩阵转换到 {targetName} → 色调映射 (Hable) → SDR 输出。";
            }
            else
            {
                MappingArrow.Text = "→ ACES 缩限到";
                GamutMapHintTxt.Text = $"SDR 捕获 → ICC 烘焙 → {targetName}。";
            }
        }
        else
        {
            bool needsMapping = targetIsWide || (targetTag == "System" && (sourceIsHdr || sourceIsHdrCapable));
            MappingArrow.Text = needsMapping ? "→ ACES 缩限到" : "→ 直通（同色域）";
            GamutMapHintTxt.Text = needsMapping
                ? $"SDR 捕获 → ICC 烘焙 → {targetName}。"
                : "当前显示器色域与目标一致，无需转换。";
        }
    }

    // ── 编码辅助（仅在最终保存/复制时触发）──

    /// <summary>ICC 色彩管理（简化模型）：
    /// OFF: 不做任何色彩处理
    /// <summary>从 ColorCbo 获取当前选择的色彩空间标签。</summary>
    private string GetSelectedColorSpaceTag()
    {
        try
        {
            if (ColorCbo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
        }
        catch { }
        return "System";
    }

    private async Task EncodeAndSaveAsync(byte[] bgra, int w, int h)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("MainWindow", $"开始截图流程: {w}x{h} 格式={_formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Format}");

        // 创建新 CTS 前确认旧操作已完成，避免取消时文件写入中断
        if (_captureCts is not null)
        {
            _captureCts.Cancel();
            // 给旧操作一小段时间完成文件写入
            await Task.Delay(50);
        }
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        try
        {
            if (bgra is null || bgra.Length != w * h * 4)
            {
                LogService.Error("MainWindow", $"像素数据无效: bgra={(bgra is null ? "null" : bgra.Length.ToString())} w={w} h={h}");
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 像素数据无效");
                return;
            }

            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
            var iccBakeEnabled = IccBakeSwitch.IsOn;
            var colorSpaceTag = GetSelectedColorSpaceTag();
            LogService.Info("MainWindow", $"编码设置: 格式={format} HDR={hdrOutput} ICC烘焙={iccBakeEnabled} 色域={colorSpaceTag}");
            var settings = BuildEncodingSettings(format, hdrOutput, null);

            // 委托给 CapturePipelineService 执行 ICC 烘焙 + 编码
            LogService.Info("MainWindow", $"委托 CapturePipelineService 执行编码...");
            var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                bgra, w, h, settings, iccBakeEnabled, colorSpaceTag, ct);

            sw.Stop();
            LogService.Info("MainWindow", $"截图保存完成: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds}ms)");

            DispatcherQueue.TryEnqueue(async () =>
            {
                await CopyFileToClipboardAsync(path);
                ShowSaveToast(path, sw.ElapsedMilliseconds);
            });
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("MainWindow", "截图操作已取消");
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "⚠ 操作已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", $"截图保存失败: {ex.Message}", ex);
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ 保存失败: {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
        }
    }

    /// <summary>判断是否应使用 Float16 捕获来获取广色域数据（ACM 感知）。</summary>
    private bool ShouldUseFloat16ForWideGamut()
    {
        if (HdrSwitch.IsOn && HdrSwitch.IsEnabled) return false; // HDR 开启时走 HDR 路径
        var tag = GetSelectedColorSpaceTag();
        if (tag is "BT2020" or "DisplayP3" or "DCI_P3" or "AdobeRGB")
            return true;

        // ACM 启用 + "System" → 如果显示器原生为广色域，也需要 Float16 捕获广色域数据
        if (tag == "System" && _settings.AcmeDetected)
        {
            var nativeGamut = GetDisplayNativeGamut();
            return nativeGamut is not "sRGB";
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  无感截图
    // ═══════════════════════════════════════════════════════════════

    /// <summary>无感截图：按下热键后自动截取当前显示器 → 编码保存 → 复制到剪贴板 → 右下角提示。</summary>
    private async void SilentCapture()
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("SilentCapture", "无感截图启动");
        try
        {
            if (_wgcService is null)
            {
                LogService.Error("SilentCapture", "WGC 捕获服务未初始化");
                return;
            }

            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
            bool useFloat16Wide = ShouldUseFloat16ForWideGamut();
            LogService.Info("SilentCapture", $"捕获配置: 格式={format} HDR={hdrOutput} Float16广色域={useFloat16Wide}");

            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = hdrOutput || useFloat16Wide, // 广色域目标也需要 Float16
                FrameTimeoutMs = 3000
            });

            bool actualHdr = captureResult.IsHdr;
            int fw = captureResult.Width, fh = captureResult.Height;
            var meta = captureResult.SourceDisplay is not null
                ? MetadataCollector.Collect(captureResult.SourceDisplay)
                : null;
            var colorSpaceTag = GetSelectedColorSpaceTag();
            var iccBakeEnabled = IccBakeSwitch.IsOn;

            string path;
            if (actualHdr && captureResult.HdrPixels is not null)
            {
                // HDR 帧存在 → HDR 编码路径 或 Float16 广色域 SDR 路径
                LogService.Info("SilentCapture", $"HDR 帧捕获成功: {fw}x{fh} {(captureResult.IsHdr ? "HDR" : "SDR")}");
                var settings = BuildEncodingSettings(format, actualHdr, meta);
                settings.IccProfile ??= captureResult.IccProfile;

                if (hdrOutput)
                {
                    // HDR 直通编码
                    LogService.Info("SilentCapture", $"HDR 直通编码: {format} {fw}x{fh}");
                    path = await AppServices.Pipeline.EncodeHdrFrameAsync(
                        new HdrFrameData
                        {
                            Pixels = captureResult.HdrPixels,
                            Width = fw, Height = fh,
                            IccProfile = captureResult.IccProfile,
                            Metadata = meta,
                            GpuTexture = captureResult.GpuTexture
                        }, settings);
                }
                else
                {
                    // Float16 广色域 → 色域转换 → 色调映射 → SDR 编码
                    LogService.Info("SilentCapture", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                    var (sdrPixels, iccProfile) = CapturePipelineService.PrepareFloat16WithIcc(
                        captureResult.HdrPixels, fw, fh, iccBakeEnabled, colorSpaceTag,
                        new ToneMappingParams { Mode = ToneMapMode.Aces, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits) });
                    if (iccProfile is not null)
                        settings.IccProfile = iccProfile;
                    settings.HdrOutput = false;
                    path = await AppServices.Pipeline.EncodeAndSaveAsync(
                        sdrPixels, fw, fh, settings, false, colorSpaceTag, default, captureResult.GpuTexture);
                }
            }
            else
            {
                // 纯 SDR 路径
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null)
                {
                    LogService.Warn("SilentCapture", "SDR 像素数据为空，跳过保存");
                    return;
                }
                LogService.Info("SilentCapture", $"SDR 帧捕获成功: {fw}x{fh}");

                var settings = BuildEncodingSettings(format, false, meta);
                settings.IccProfile ??= captureResult.IccProfile;
                path = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, iccBakeEnabled, colorSpaceTag, default, captureResult.GpuTexture);
            }

            sw.Stop();
            LogService.Info("SilentCapture", $"无感截图完成: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds}ms)");
            await CopyFileToClipboardAsync(path);

            DispatcherQueue.TryEnqueue(() => ShowSaveToast(path, sw.ElapsedMilliseconds, "silent"));
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("SilentCapture", "无感截图已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("SilentCapture", $"无感截图失败: {ex.Message}", ex);
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ 无感截图失败: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _isCapturing, 0); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  统一保存提示（右下角 Toast）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>显示保存成功提示（截图/录制/无感截图统一使用）。</summary>
    /// <param name="mode">capture=截图, silent=无感截图, recording=动图录制</param>
    private void ShowSaveToast(string filePath, long elapsedMs, string mode = "capture")
    {
        // 根据模式检查是否启用提示
        bool enabled = mode switch
        {
            "silent" => _settings.ToastOnSilentCapture,
            "recording" => _settings.ToastOnRecording,
            _ => _settings.ToastOnCapture,
        };
        if (!enabled) return;

        var position = _settings.ToastPosition;

        // Windows 自带通知：不创建自定义窗口，直接走 Windows 通知
        if (position == "WindowsNotify")
        {
            ToastService.ShowCaptureSuccess(filePath, elapsedMs);
            return;
        }

        try
        {
            var toast = new SilentCaptureToast(position);
            toast.SetContent("✅ 已保存并复制到剪贴板",
                $"{Path.GetFileName(filePath)}  ({elapsedMs}ms)");
            toast.Activate();
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"提示窗口创建失败: {ex.Message}");
        }

        // 如果未选择 Windows 通知，仍发送（备用）
        ToastService.ShowCaptureSuccess(filePath, elapsedMs);
    }

    /// <summary>将已保存的输出文件复制到剪贴板（直接复制文件，非路径字符串）。</summary>
    private async Task CopyFileToClipboardAsync(string filePath)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetStorageItems(new[] { file });
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            StatusTxt.Text = $"✅ 已保存并复制: {Path.GetFileName(filePath)}";
        }
        catch
        {
            StatusTxt.Text = $"✅ 已保存: {Path.GetFileName(filePath)}";
        }
    }

    private async Task EncodeAndCopyAsync(byte[] bgra, int w, int h)
    {
        try
        {
            var tmpPath = Path.Combine(Path.GetTempPath(), $"ttc_clip_{Guid.NewGuid():N}.png");

            // ── ICC 烘焙 + 编码全部移到后台线程 ──
            var iccBakeEnabled = IccBakeSwitch.IsOn;
            var colorSpaceTag = GetSelectedColorSpaceTag();
            // 解析 "System" 为实际色域，确保编码器正确嵌入 ICC/CICP
            string resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag, false);
            var settings = BuildEncodingSettings(OutputFormat.PNG, false, null);

            await Task.Run(() =>
            {
                var (pixels, iccProfile) = CapturePipelineService.PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
                if (iccProfile is not null)
                    settings.IccProfile = iccProfile;
                // 使用已解析的色域标签，确保 ICC/CICP 策略正确
                settings.ColorSpaceTag = resolvedTag;
                var encoder = EncoderFactory.Create(OutputFormat.PNG);
                encoder.EncodeSdrAsync(pixels, w, h, settings, tmpPath).GetAwaiter().GetResult();
            });

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tmpPath);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetStorageItems(new[] { file });
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            StatusTxt.Text = "📋 已复制到剪贴板";
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ 复制失败: {ex.Message}");
        }
    }

    // ── 选区动作 ──

    // ── 选区截图（QQ 风格，WGC 多显示器拼接）──

    /// <summary>从预捕获桌面像素中提取区域。</summary>
    private static byte[]? ExtractRegionFromDesktop(byte[] full, int fullW, int fullH,
        int vx, int vy, RectInt32 screenRect)
    {
        int rx = screenRect.X - vx;
        int ry = screenRect.Y - vy;
        int rw = screenRect.Width;
        int rh = screenRect.Height;

        if (rx < 0 || ry < 0 || rx + rw > fullW || ry + rh > fullH)
            return null;

        var result = new byte[rw * rh * 4];
        int srcStride = fullW * 4;
        int dstStride = rw * 4;
        for (int row = 0; row < rh; row++)
        {
            int srcOff = ((ry + row) * srcStride) + (rx * 4);
            int dstOff = row * dstStride;
            Buffer.BlockCopy(full, srcOff, result, dstOff, dstStride);
        }
        return result;
    }

    /// <summary>从 HDR 桌面帧中裁剪区域（scRGB linear float[] RGBA）。</summary>
    private static float[]? ExtractHdrRegionFromDesktop(float[] full, int fullW, int fullH,
        int vx, int vy, RectInt32 screenRect)
    {
        int rx = screenRect.X - vx;
        int ry = screenRect.Y - vy;
        int rw = screenRect.Width;
        int rh = screenRect.Height;

        if (rx < 0 || ry < 0 || rx + rw > fullW || ry + rh > fullH)
            return null;

        var result = new float[rw * rh * 4];
        int srcStride = fullW * 4;
        int dstStride = rw * 4;
        for (int row = 0; row < rh; row++)
        {
            int srcOff = ((ry + row) * srcStride) + (rx * 4);
            int dstOff = row * dstStride;
            Array.Copy(full, srcOff, result, dstOff, dstStride);
        }
        return result;
    }

    /// <summary>HDR 编码保存：直接使用 scRGB linear 浮点像素编码为 HDR 格式。
    /// 对齐无感截图路径，传递 ICC、GPU 纹理、色域标签等参数。</summary>
    private async Task EncodeAndSaveHdrAsync(float[] hdrPixels, int w, int h,
        byte[]? iccProfile = null, ID3D11Texture2D? gpuTexture = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("MainWindow", $"HDR 编码启动: {w}x{h} 格式={_formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Format}");

        // 创建新 CTS 前确认旧操作已完成，避免取消时文件写入中断
        if (_captureCts is not null)
        {
            _captureCts.Cancel();
            await Task.Delay(50);
        }
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        try
        {
            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
            var meta = MetadataCollector.Collect(DisplayEnumerator.FindDisplayByMonitor(cursorMonitor));
            var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
            var iccBakeEnabled = IccBakeSwitch.IsOn;
            var colorSpaceTag = GetSelectedColorSpaceTag();
            var settings = BuildEncodingSettings(format, hdrOutput, meta);
            settings.IccProfile ??= iccProfile;

            LogService.Info("MainWindow", $"HDR 编码: {format} {w}x{h} HDR={hdrOutput} ICC烘焙={iccBakeEnabled} 色域={colorSpaceTag}");

            if (hdrOutput)
            {
                // HDR 直通编码（同无感截图路径）
                var path = await AppServices.Pipeline.EncodeHdrFrameAsync(
                    new HdrFrameData
                    {
                        Pixels = hdrPixels,
                        Width = w, Height = h,
                        IccProfile = iccProfile,
                        Metadata = meta,
                        GpuTexture = gpuTexture
                    }, settings, ct);

                sw.Stop();
                LogService.Info("MainWindow", $"HDR 编码完成: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds}ms)");
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await CopyFileToClipboardAsync(path);
                    ShowSaveToast(path, sw.ElapsedMilliseconds);
                });
            }
            else
            {
                // Float16 广色域 → 色域转换 → 色调映射 → SDR 编码（同无感截图路径）
                LogService.Info("MainWindow", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                var (sdrPixels, iccP) = CapturePipelineService.PrepareFloat16WithIcc(
                    hdrPixels, w, h, iccBakeEnabled, colorSpaceTag,
                    new ToneMappingParams { Mode = ToneMapMode.Aces, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits) });
                if (iccP is not null)
                    settings.IccProfile = iccP;
                settings.HdrOutput = false;
                var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, w, h, settings, false, colorSpaceTag, ct, gpuTexture);

                sw.Stop();
                LogService.Info("MainWindow", $"SDR 编码完成: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds}ms)");
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await CopyFileToClipboardAsync(path);
                    ShowSaveToast(path, sw.ElapsedMilliseconds);
                });
            }
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("MainWindow", "HDR 编码已取消");
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "⚠ 操作已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", $"HDR 保存失败: {ex.Message}", ex);
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ HDR 保存失败: {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
        }
    }

    private async void StartSelectionCapture()
    {
        // ── 防重入 ──
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            LogService.Warn("MainWindow", "截图已在进行中，忽略重复触发");
            System.Diagnostics.Trace.WriteLine("[MainWindow] 截图已在进行中，忽略重复触发");
            return;
        }

        bool overlayShown = false;
        try
        {
            LogService.Info("MainWindow", "选区截图启动");
            StatusTxt.Text = "📷 WGC 捕获桌面...";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── 使用 WGC 捕获所有显示器并拼接 ──
            if (_wgcService is null)
            {
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 捕获服务未初始化");
                return;
            }

            // 获取虚拟桌面坐标
            var displays = DisplayEnumerator.EnumerateDisplays();
            int vx = displays.Count > 0 ? displays.Min(d => d.X) : 0;
            int vy = displays.Count > 0 ? displays.Min(d => d.Y) : 0;
            int vw = displays.Count > 0 ? displays.Max(d => d.X + d.Width) - vx : 1920;
            int vh = displays.Count > 0 ? displays.Max(d => d.Y + d.Height) - vy : 1080;

            // WGC 多显示器拼接捕获（SDR 用于预览）
            CaptureResult captureResult;
            try
            {
                LogService.Info("MainWindow", "WGC 多显示器拼接捕获启动...");
                captureResult = await _wgcService.CaptureAllMonitorsAsync(new WgcCaptureConfig
                {
                    FrameTimeoutMs = 3000
                });
                LogService.Info("MainWindow", $"WGC 捕获完成: {captureResult.Width}x{captureResult.Height} HDR={captureResult.IsHdr}");
            }
            catch (Exception ex)
            {
                LogService.Error("MainWindow", $"WGC 捕获失败: {ex.Message}", ex);
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusTxt.Text = $"❌ WGC 捕获失败: {ex.Message}";
                    ToastService.ShowCaptureFailed(ex.Message);
                });
                return;
            }

            var desktopPixels = captureResult.SdrPixels;
            if (desktopPixels is null || desktopPixels.Length != vw * vh * 4)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusTxt.Text = "❌ 桌面捕获数据无效";
                    ToastService.ShowCaptureFailed("桌面捕获数据无效");
                });
                return;
            }

            // ── HDR 捕获：CaptureAllMonitorsAsync 已在内部分支捕获 HDR（单显示器时）──
            float[]? hdrDesktopPixels = captureResult.HdrPixels;
            int hdrW = captureResult.Width, hdrH = captureResult.Height;
            System.Diagnostics.Debug.WriteLine(
                $"[诊断] captureResult: HDR={(hdrDesktopPixels is not null ? $"len={hdrDesktopPixels.Length} {hdrW}x{hdrH}" : "null")} SDR={(desktopPixels is not null ? $"len={desktopPixels.Length}" : "null")}");

            sw.Stop();
            LogService.Info("MainWindow", $"阶段1 WGC捕获完成: {captureResult.Width}x{captureResult.Height} {sw.ElapsedMilliseconds}ms");

            // 阶段2: 截图预览窗口
            sw.Restart();

            bool hasHdr = hdrDesktopPixels is not null;

            // ═══ HDR 路径：全 D3D11 原生窗口（scRGB 正确显示）═══
            if (hasHdr)
            {
                StatusTxt.Text = "🖥️ HDR 预览";
                var sharedDevice = AppServices.Wgc?.GetOrCreateDevice(
                    DisplayEnumerator.GetMonitorUnderCursor());
                using var hdrWnd = new Services.HdrCaptureWindow(sharedDevice);
                bool initOk = hdrWnd.Initialize(vx, vy, vw, vh);

                if (initOk)
                {
                    if (hdrDesktopPixels is null) return;
                    hdrWnd.LoadFrame(hdrDesktopPixels, hdrW, hdrH);
                    hdrWnd.Render();

                    // 等待用户操作
                    var tcs = new TaskCompletionSource<(HdrCaptureAction action, int x, int y, int w, int h)>();
                    hdrWnd.ActionCompleted += (action, ax, ay, aw, ah) =>
                        tcs.TrySetResult((action, ax, ay, aw, ah));
                    var (action, rx, ry, rw, rh) = await tcs.Task;

                    if (action == HdrCaptureAction.Cancel)
                    {
                        DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "就绪");
                        return;
                    }

                    // 从 HDR 帧裁剪选区
                    var hdrRegion = hdrDesktopPixels is not null
                        ? ExtractHdrRegionFromDesktop(hdrDesktopPixels, hdrW, hdrH, vx, vy,
                            new RectInt32(rx, ry, rw, rh))
                        : null;
                    var sdrRegion = desktopPixels is not null
                        ? ExtractRegionFromDesktop(desktopPixels, vw, vh, vx, vy,
                            new RectInt32(rx, ry, rw, rh))
                        : null;

                    switch (action)
                    {
                        case HdrCaptureAction.Save:
                            LogService.Info("MainWindow", "HDR 选区保存");
                            if (hdrRegion is not null)
                                await EncodeAndSaveHdrAsync(hdrRegion, rw, rh,
                                    captureResult.IccProfile, captureResult.GpuTexture);
                            else if (sdrRegion is not null)
                                await EncodeAndSaveAsync(sdrRegion, rw, rh);
                            break;
                        case HdrCaptureAction.Copy:
                            LogService.Info("MainWindow", "HDR 选区复制到剪贴板");
                            if (sdrRegion is not null)
                                await EncodeAndCopyAsync(sdrRegion, rw, rh);
                            break;
                        case HdrCaptureAction.Ocr:
                            LogService.Info("MainWindow", "HDR 选区 OCR 识别");
                            if (sdrRegion is not null)
                                await CaptureAndOcrFromPixelsAsync(sdrRegion, rw, rh);
                            break;
                        case HdrCaptureAction.Translate:
                            LogService.Info("MainWindow", "HDR 选区翻译");
                            if (sdrRegion is not null)
                                await CaptureAndTranslateFromPixelsAsync(sdrRegion, rw, rh);
                            break;
                    }

                    if (MinimizeTrayChk.IsChecked == true
                        && action is HdrCaptureAction.Save or HdrCaptureAction.Copy)
                        DispatcherQueue.TryEnqueue(() => _trayIcon?.MinimizeToTray());

                    return;
                }
                // HDR 窗口初始化失败 → 回退到 SDR 路径
                System.Diagnostics.Debug.WriteLine($"[MainWindow] HDR 窗口失败: {hdrWnd.LastError}，回退 SDR");
                // 释放 HDR 像素引用，防止内存泄漏
                hdrDesktopPixels = null;
            }

            // ═══ SDR 路径：SelectionOverlay（WinUI 3）═══
            StatusTxt.Text = "📷 选区模式";
            if (desktopPixels is null)
            {
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 桌面像素数据为空");
                return;
            }
            var overlay = new SelectionOverlay(desktopPixels, vx, vy, vw, vh);
            overlay.Activate();
            overlayShown = true; // 标记覆盖层已激活，由 ActionCompleted 负责释放锁
            var overlayReady = new TaskCompletionSource<bool>();
            overlay.Activated += (_, _) => overlayReady.TrySetResult(true);
            _ = Task.Run(async () => { await Task.Delay(1000); overlayReady.TrySetResult(false); });

            overlay.ActionCompleted += async (action, rect) =>
            {
                try
                {
                    if (action == SelectionOverlay.ActionResult.Cancel)
                    {
                        DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "就绪");
                        return;
                    }

                    var regionPixels = overlay.AnnotatedRegionPixels
                        ?? ExtractRegionFromDesktop(desktopPixels, vw, vh, vx, vy, rect);

                    if (regionPixels is null)
                    {
                        DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 提取区域失败");
                        return;
                    }

                    switch (action)
                    {
                        case SelectionOverlay.ActionResult.Confirm:
                            LogService.Info("MainWindow", $"选区确认保存: {rect.Width}x{rect.Height}");
                            await EncodeAndSaveAsync(regionPixels, rect.Width, rect.Height);
                            break;
                        case SelectionOverlay.ActionResult.Copy:
                            LogService.Info("MainWindow", "选区复制到剪贴板");
                            await EncodeAndCopyAsync(regionPixels, rect.Width, rect.Height);
                            break;
                        case SelectionOverlay.ActionResult.Ocr:
                            LogService.Info("MainWindow", "选区 OCR 识别");
                            await CaptureAndOcrFromPixelsAsync(regionPixels, rect.Width, rect.Height);
                            break;
                        case SelectionOverlay.ActionResult.Translate:
                            LogService.Info("MainWindow", "选区翻译");
                            await CaptureAndTranslateFromPixelsAsync(regionPixels, rect.Width, rect.Height);
                            break;
                    }

                    if (MinimizeTrayChk.IsChecked == true
                        && action is SelectionOverlay.ActionResult.Confirm or SelectionOverlay.ActionResult.Copy)
                        DispatcherQueue.TryEnqueue(() => _trayIcon?.MinimizeToTray());
                }
                catch (Exception ex)
                {
                    LogService.Error("MainWindow", $"选区动作异常: {ex.Message}", ex);
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] ActionCompleted 异常: {ex}");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusTxt.Text = $"❌ {ex.Message}";
                        ToastService.ShowCaptureFailed(ex.Message);
                    });
                }
                finally
                {
                    // 确保锁释放：覆盖层完成时无论成功/失败都释放防重入锁
                    Interlocked.Exchange(ref _isCapturing, 0);
                }
            };
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusTxt.Text = $"❌ {ex.Message}";
                ToastService.ShowCaptureFailed(ex.Message);
            });
        }
        finally
        {
            // 覆盖层未激活时(即异常/早期返回路径)由外层 finally 释放锁；
            // 覆盖层已激活时由 ActionCompleted 回调负责释放锁，外层不再释放。
            if (!overlayShown)
                Interlocked.Exchange(ref _isCapturing, 0);
        }
    }

    private async Task CaptureAndOcrFromPixelsAsync(byte[] pixels, int w, int h)
    {
        LogService.Info("MainWindow", $"OCR 识别启动: {w}x{h} 语言={_settings.OcrLanguage}");
        StatusTxt.Text = "📝 文字提取中...";
        try
        {
            // 确保 OCR 引擎已初始化（首次使用或后台未完成时）
            var modelDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models");
            MultiOcrService.Initialize(modelDir);
            LogService.Info("MainWindow", $"OCR 引擎初始化, 模型目录: {modelDir}");

            var ocrLang = _settings.OcrLanguage;
            var result = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(result.Error))
            { LogService.Warn("MainWindow", $"OCR 识别错误: {result.Error}"); StatusTxt.Text = $"❌ OCR: {result.Error}"; return; }
            if (string.IsNullOrWhiteSpace(result.Text) || (result.Lines is null or { Count: 0 }))
            { LogService.Info("MainWindow", "OCR 未检测到文字"); StatusTxt.Text = "📝 未检测到文字"; return; }

            DispatcherQueue.TryEnqueue(() =>
            {
                OpenOcrPreviewWindow(pixels, w, h, result, autoTranslate: false);
                StatusTxt.Text = "📝 已打开文字预览窗口";
            });
        }
        catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
    }

    private async Task CaptureAndTranslateFromPixelsAsync(byte[] pixels, int w, int h)
    {
        LogService.Info("MainWindow", $"翻译启动: {w}x{h} 语言={_settings.OcrLanguage}");
        StatusTxt.Text = "🌐 识别并翻译中...";
        try
        {
            // 确保 OCR 引擎已初始化
            var modelDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models");
            MultiOcrService.Initialize(modelDir);
            LogService.Info("MainWindow", $"OCR 引擎初始化(翻译), 模型目录: {modelDir}");

            var ocrLang = _settings.OcrLanguage;
            var ocrResult = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(ocrResult.Error) || string.IsNullOrWhiteSpace(ocrResult.Text) || (ocrResult.Lines is null or { Count: 0 }))
            {
                LogService.Warn("MainWindow", string.IsNullOrEmpty(ocrResult.Error) ? "OCR 未检测到文字" : $"OCR 错误: {ocrResult.Error}");
                StatusTxt.Text = string.IsNullOrEmpty(ocrResult.Error) ? "📝 未检测到文字" : $"❌ {ocrResult.Error}";
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                OpenOcrPreviewWindow(pixels, w, h, ocrResult, autoTranslate: true);
                StatusTxt.Text = "🌐 已打开翻译预览窗口";
            });
        }
        catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
    }

    /// <summary>打开 OCR/翻译独立预览窗口（截图 + 文字点对点覆盖）。</summary>
    private void OpenOcrPreviewWindow(byte[] pixels, int w, int h, OcrResult ocr, bool autoTranslate)
    {
        var cfg = new LlmConfig
        {
            UseCustomLlm = _settings.UseCustomLlm,
            ApiEndpoint = _settings.LlmEndpoint,
            ApiKey = _settings.LlmApiKey,
            ModelName = _settings.LlmModel,
            SystemPrompt = _settings.LlmSystemPrompt,
            TargetLanguage = _settings.TargetLanguage
        };
        var win = new OcrPreviewWindow(pixels, w, h, ocr, cfg, autoTranslate)
        {
            SaveHandler = async (pixels, w, h) =>
            {
                await EncodeAndSaveAsync(pixels, w, h);
            }
        };
        win.Activate();
    }

    private async Task ShowOcrResultAsync(string text)
    {
        var dialog = new ContentDialog
        {
            Title = "📝 文字提取结果",
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            PrimaryButtonText = "复制",
            CloseButtonText = "关闭",
            XamlRoot = this.Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            StatusTxt.Text = "📋 文字已复制";
        }
    }

    private async Task ShowTranslationResultAsync(string original, string translated)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "原文:", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = original, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)) });
        panel.Children.Add(new TextBlock { Text = "译文:", FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(new TextBlock { Text = translated, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var dialog = new ContentDialog
        {
            Title = "🌐 翻译结果",
            Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
            PrimaryButtonText = "复制译文",
            CloseButtonText = "关闭",
            XamlRoot = this.Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(translated);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            StatusTxt.Text = "📋 译文已复制";
        }
    }

    // ── 截图按钮（选区模式） ──

    private void OnCaptureBtn(object sender, RoutedEventArgs e) => StartSelectionCapture();

    /// <summary>"捕获现在" — WGC 单显示器全屏捕获。</summary>
    private async void OnCaptureNow(object sender, RoutedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        LogService.Info("MainWindow", "快速捕获启动 (单显示器全屏)");
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        StatusTxt.Text = "📷 WGC 截图中...";
        CaptureBtn.IsEnabled = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (_wgcService is null)
            {
                StatusTxt.Text = "❌ 捕获服务未初始化";
                return;
            }

            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
            bool useFloat16Wide = ShouldUseFloat16ForWideGamut();

            // ── WGC 单显示器捕获 ──
            LogService.Info("MainWindow", $"WGC 单显示器捕获: HDR={hdrOutput} Float16广色域={useFloat16Wide}");
            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = hdrOutput || useFloat16Wide,
                FrameTimeoutMs = 3000
            });

            ct.ThrowIfCancellationRequested();
            bool actualHdr = captureResult.IsHdr;
            int fw = captureResult.Width, fh = captureResult.Height;
            var meta = captureResult.SourceDisplay is not null
                ? MetadataCollector.Collect(captureResult.SourceDisplay)
                : null;
            var colorSpaceTag = GetSelectedColorSpaceTag();
            var iccBakeEnabled = IccBakeSwitch.IsOn;

            string fullPath;
            if (actualHdr && captureResult.HdrPixels is not null)
            {
                LogService.Info("MainWindow", $"HDR 帧捕获: {fw}x{fh}");
                var settings = BuildEncodingSettings(format, actualHdr, meta);
                settings.IccProfile ??= captureResult.IccProfile;

                if (hdrOutput)
                {
                    // HDR 直通编码
                    LogService.Info("MainWindow", $"HDR 直通编码: {format}");
                    fullPath = await AppServices.Pipeline.EncodeHdrFrameAsync(
                        new HdrFrameData
                        {
                            Pixels = captureResult.HdrPixels,
                            Width = fw, Height = fh,
                            IccProfile = captureResult.IccProfile,
                            Metadata = meta,
                            GpuTexture = captureResult.GpuTexture
                        }, settings, ct);
                }
                else
                {
                    // Float16 广色域 → 色域转换 → 色调映射 → SDR
                    LogService.Info("MainWindow", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                    var (sdrPixels, iccProfile) = CapturePipelineService.PrepareFloat16WithIcc(
                        captureResult.HdrPixels, fw, fh, iccBakeEnabled, colorSpaceTag,
                        new ToneMappingParams { Mode = ToneMapMode.Aces, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits) });
                    if (iccProfile is not null)
                        settings.IccProfile = iccProfile;
                    settings.HdrOutput = false;
                    fullPath = await AppServices.Pipeline.EncodeAndSaveAsync(
                        sdrPixels, fw, fh, settings, false, colorSpaceTag, ct, captureResult.GpuTexture);
                }
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null) throw new InvalidOperationException("无法获取显示像素");
                LogService.Info("MainWindow", $"SDR 帧捕获: {fw}x{fh}");

                var settings = BuildEncodingSettings(format, false, meta);
                settings.IccProfile ??= captureResult.IccProfile;
                fullPath = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, iccBakeEnabled, colorSpaceTag, ct, captureResult.GpuTexture);
            }

            sw.Stop();
            await CopyFileToClipboardAsync(fullPath);
            string status = actualHdr
                ? $"✅ HDR 已保存 ({sw.ElapsedMilliseconds}ms)"
                : $"✅ 已保存 ({sw.ElapsedMilliseconds}ms)";
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = status);
            ShowSaveToast(fullPath, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("MainWindow", "快速捕获已取消");
            sw.Stop();
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "⚠ 操作已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", $"快速捕获失败: {ex.Message}", ex);
            sw.Stop();
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
        }
        finally { CaptureBtn.IsEnabled = true; Interlocked.Exchange(ref _isCapturing, 0); }
    }

    /// <summary>构建编码设置（委托给 CapturePipelineService，减少重复逻辑）。</summary>
    private EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta)
    {
        var tag = GetSelectedColorSpaceTag();
        var settings = AppServices.Pipeline.BuildEncodingSettings(format, hdrOutput, meta, tag, _settings.AcmeDetected);
        // 覆盖 UI 特有的设置
        settings.Quality = (float)QualitySld.Value;
        settings.AvifPngSuffix = AvifPngSuffixChk.IsChecked == true;
        settings.AvifBackend = AvifBackendCbo.SelectedIndex switch
        { 1 => AvifEncoderBackend.LibAom, 2 => AvifEncoderBackend.Qsv, 3 => AvifEncoderBackend.Nvenc, _ => AvifEncoderBackend.Auto };
        return settings;
    }

    // ── 快捷键录制 ──

    private bool _uiReady; // InitializeComponent 完成后才响应 UI 事件

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return; // XAML 初始化期间忽略
        var tag = (OcrEngineCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "OnnxGpu";
        _settings.OcrEngineMode = tag;

        // 直接设置选中的引擎，无自动降级
        if (Enum.TryParse<OcrEngineType>(tag, out var engineType))
        {
            MultiOcrService.SelectedEngineType = engineType;
        }

        // 切换引擎 → 刷新语言列表并重置为默认语言
        PopulateOcrLanguages();
        UpdateOcrEngineStatus();
        try { SaveSettingsQuiet(); } catch { }
    }

    private void OnCategoryChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (!_uiReady) return;
        var tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string ?? "Output";

        PageOutput.Visibility = tag == "Output" ? Visibility.Visible : Visibility.Collapsed;
        PageColor.Visibility = tag == "Color" ? Visibility.Visible : Visibility.Collapsed;
        PageCapture.Visibility = tag == "Capture" ? Visibility.Visible : Visibility.Collapsed;
        PageAI.Visibility = tag == "AI" ? Visibility.Visible : Visibility.Collapsed;
        PageSystem.Visibility = tag == "System" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;

        // 切到日志页时刷新
        if (tag == "Log")
            RefreshLogView();

        // 默认选中第一项
        if (!_uiReady && MainNav.SelectedItem is null)
            MainNav.SelectedItem = MainNav.MenuItems[0];
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = (ThemeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        _settings.ThemeMode = tag;
        var mode = tag switch
        {
            "Light" => AppThemeMode.Light,
            "Dark" => AppThemeMode.Dark,
            "OLED" => AppThemeMode.OLED,
            _ => AppThemeMode.Default,
        };
        App.ApplyTheme(mode);
        SyncWindowTheme();
        try { SaveSettingsQuiet(); } catch { }
    }

    private void OnToastPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.ToastPosition = (ToastPositionCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "BottomRight";
        try { SaveSettingsQuiet(); } catch { }
    }

    /// <summary>格式选项实时保存（位深/色度/后缀等每格式设置变更时触发）。</summary>
    private void OnPerFormatChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        // 直接从事件源读取当前值，避免全量 SaveSettings 的开销
        if (sender is CheckBox cb)
        {
            if (cb.Name == nameof(AvifPngSuffixChk)) _settings.AvifPngSuffix = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastCaptureChk)) _settings.ToastOnCapture = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastSilentChk)) _settings.ToastOnSilentCapture = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastRecordChk)) _settings.ToastOnRecording = cb.IsChecked == true;
            else if (cb.Name == nameof(AutoStartChk)) _settings.AutoStart = cb.IsChecked == true;
            else if (cb.Name == nameof(MinimizeTrayChk)) _settings.MinimizeToTray = cb.IsChecked == true;
            else if (cb.Name == nameof(PreviewChk)) _settings.ShowPreview = cb.IsChecked == true;
        }
        try { SaveSettingsQuiet(); } catch { }
    }

    /// <summary>格式选项 ComboBox 选择变更实时保存。</summary>
    private void OnPerFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        // 直接从事件源 ComboBox 读取值，避免全量 SaveSettings 的开销
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string ?? "";
            if (cb.Name == nameof(BdPngCbo)) _settings.BitDepthPng = int.TryParse(tag, out var v) ? v : 8;
            else if (cb.Name == nameof(BdAvifCbo)) _settings.BitDepthAvif = int.TryParse(tag, out var v) ? v : 10;
            else if (cb.Name == nameof(BdJpegXlCbo)) _settings.BitDepthJpegXl = int.TryParse(tag, out var v) ? v : 10;
            else if (cb.Name == nameof(BdTiffCbo)) _settings.BitDepthTiff = int.TryParse(tag, out var v) ? v : 8;
            else if (cb.Name == nameof(AvifChromaCbo)) _settings.AvifChroma = tag;
            else if (cb.Name == nameof(ChromaJpegXlCbo)) _settings.ChromaJpegXl = tag;
            else if (cb.Name == nameof(ChromaJpegLiCbo)) _settings.ChromaJpegLi = tag;
            else if (cb.Name == nameof(ChromaWebPCbo)) _settings.ChromaWebP = tag;
            else if (cb.Name == nameof(ArchiveModeCbo)) _settings.ArchiveMode = tag;
        }
        try { SaveSettingsQuiet(); } catch { }
    }

    /// <summary>将当前主题同步到窗口内容元素（fe.RequestedTheme 控制 WinUI 控件实际渲染）。</summary>
    private void SyncWindowTheme()
    {
        var mode = App.CurrentTheme;
        var effective = App.ResolveEffectiveTheme(mode);
        if (Content is FrameworkElement fe)
        {
            fe.RequestedTheme = effective switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark or AppThemeMode.OLED => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = (LanguageCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh";
        var lang = tag == "en" ? AppLanguage.English : AppLanguage.Chinese;
        LocaleManager.SetLanguage(lang);
        _settings.Language = tag;
        ApplyLocale();
        UpdateQualityPanel();
        try { SaveSettingsQuiet(); } catch { }
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = (FontCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        // 显示/隐藏自定义输入框
        FontCustomPanel.Visibility = tag == "CUSTOM" ? Visibility.Visible : Visibility.Collapsed;
        if (tag != "CUSTOM")
        {
            _settings.FontFamily = tag;
            ApplyFontToUI();
            try { SaveSettingsQuiet(); } catch { }
        }
    }

    private void OnFontCustomTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.FontFamily = FontCustomTxt.Text.Trim();
        ApplyFontToUI();
        try { SaveSettingsQuiet(); } catch { }
    }

    /// <summary>将当前字体设置应用到整个 UI（递归遍历可视化树）。</summary>
    private void ApplyFontToUI()
    {
        string fontFamily = FontLoader.GetEffectiveFontFamily(_settings.FontFamily);
        if (this.Content is FrameworkElement fe)
            FontHelper.ApplyFontToVisualTree(fe, fontFamily);
    }

    /// <summary>从 _settings.FontFamily 恢复字体下拉框选中项。</summary>
    private void RestoreFontSelection()
    {
        var font = _settings.FontFamily ?? "";
        // 尝试在预定义选项中匹配
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
        // 未匹配 → 选中"自定义"并填入文本
        foreach (ComboBoxItem item in FontCbo.Items)
        {
            if (item.Tag is string t && t == "CUSTOM")
            {
                item.IsSelected = true;
                FontCustomPanel.Visibility = Visibility.Visible;
                FontCustomTxt.Text = font;
                break;
            }
        }
    }

    /// <summary>将本地化文本应用到所有 UI 元素（标签 + 下拉选项）。</summary>
    private void ApplyLocale()
    {
        var l = LocaleManager.CurrentLanguage == AppLanguage.English ? "en" : "zh";

        // ── 标签 ──
        BrandTagline.Text = LocaleManager.CurrentLanguage == AppLanguage.English ? "HDR Screenshot Tool" : "HDR 截图工具";

        NavOutput.Content = LocaleManager.NavOutput;
        NavColor.Content = LocaleManager.NavColor;
        NavCapture.Content = LocaleManager.NavCapture;
        NavAI.Content = LocaleManager.NavAI;
        NavSystem.Content = LocaleManager.NavSystem;
        NavLog.Content = LocaleManager.NavLog;

        PageOutputTitle.Text = LocaleManager.PageOutput;
        BasicOutputTitle.Text = LocaleManager.BasicOutput;
        AvifOptionsTitle.Text = LocaleManager.AvifOptions;
        GainMapOptionsTitle.Text = LocaleManager.GainMapOptions;
        GainMapOptionsDesc.Text = LocaleManager.GainMapDesc;
        GainMapModeLabelTxt.Text = LocaleManager.GainMapModeLabel;
        AvifPngSuffixChk.Content = LocaleManager.AvifPngSuffix;

        PageColorTitle.Text = LocaleManager.PageColor;
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
        // 更新字体下拉选项文本
        foreach (ComboBoxItem item in FontCbo.Items)
        {
            var tag = item.Tag as string ?? "";
            item.Content = tag switch
            {
                "" => LocaleManager.FontDefault,
                "CUSTOM" => LocaleManager.FontCustom,
                _ => item.Content // 保留字体名称不变
            };
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

    private void OnCaptureHotkeyRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(HotkeyTxt);

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

    private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(RecordHotkeyTxt);

    private void OnSilentHotkeyRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(SilentHotkeyTxt);

    private void StartHotkeyRecording(TextBox target)
    {
        _recordingTarget = target;
        target.Text = "";
        target.PlaceholderText = "按下组合键...";
        target.Focus(FocusState.Keyboard);
    }

    private void OnHotkeyRecordKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_recordingTarget is null) return;
        if (!ReferenceEquals(sender, _recordingTarget)) return;

        // 忽略单独的修饰键按下
        var key = e.Key;
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift
            or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows)
            return;

        // 构建快捷键字符串
        var parts = new List<string>();
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        var winState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows);

        if ((ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) parts.Add("Ctrl");
        if ((shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) parts.Add("Shift");
        if ((altState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) parts.Add("Alt");
        if ((winState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) parts.Add("Win");

        string keyName = KeyToString(key);
        if (string.IsNullOrEmpty(keyName)) return;
        parts.Add(keyName);

        string result = string.Join("+", parts);

        // 更新文本框 + 立即重新注册热键
        _recordingTarget.Text = result;
        _recordingTarget.PlaceholderText = "";
        var recordedBox = _recordingTarget;
        _recordingTarget = null;

        e.Handled = true;

        // 根据哪个 TextBox 来更新对应热键
        if (recordedBox == HotkeyTxt)
        {
            _settings.Hotkey = result;
            try { _trayIcon?.RegisterCaptureHotkey(result); } catch { }
        }
        else if (recordedBox == RecordHotkeyTxt)
        {
            _settings.RecordHotkey = result;
        }
        else if (recordedBox == SilentHotkeyTxt)
        {
            _settings.SilentHotkey = result;
            try { HotkeyManager.RegisterNamed(this, "silent", result, () => DispatcherQueue.TryEnqueue(() => SilentCapture()), ["Ctrl+Alt+Q", "Alt+Shift+Q"]); } catch { }
        }
        try { SaveSettingsQuiet(); } catch { }
    }

    private static string KeyToString(Windows.System.VirtualKey key) => key switch
    {
        >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z => key.ToString().ToUpper(),
        >= Windows.System.VirtualKey.Number0 and <= Windows.System.VirtualKey.Number9 => ((int)(key - Windows.System.VirtualKey.Number0)).ToString(),
        >= Windows.System.VirtualKey.NumberPad0 and <= Windows.System.VirtualKey.NumberPad9 => "NumPad" + ((int)(key - Windows.System.VirtualKey.NumberPad0)),
        Windows.System.VirtualKey.F1 => "F1", Windows.System.VirtualKey.F2 => "F2",
        Windows.System.VirtualKey.F3 => "F3", Windows.System.VirtualKey.F4 => "F4",
        Windows.System.VirtualKey.F5 => "F5", Windows.System.VirtualKey.F6 => "F6",
        Windows.System.VirtualKey.F7 => "F7", Windows.System.VirtualKey.F8 => "F8",
        Windows.System.VirtualKey.F9 => "F9", Windows.System.VirtualKey.F10 => "F10",
        Windows.System.VirtualKey.F11 => "F11", Windows.System.VirtualKey.F12 => "F12",
        Windows.System.VirtualKey.Space => "Space",
        Windows.System.VirtualKey.Print => "Print",
        Windows.System.VirtualKey.Snapshot => "PrtSc",
        Windows.System.VirtualKey.Tab => "Tab",
        Windows.System.VirtualKey.Insert => "Insert",
        Windows.System.VirtualKey.Delete => "Delete",
        Windows.System.VirtualKey.Home => "Home",
        Windows.System.VirtualKey.End => "End",
        Windows.System.VirtualKey.PageUp => "PageUp",
        Windows.System.VirtualKey.PageDown => "PageDown",
        Windows.System.VirtualKey.Left => "Left",
        Windows.System.VirtualKey.Right => "Right",
        Windows.System.VirtualKey.Up => "Up",
        Windows.System.VirtualKey.Down => "Down",
        _ => ((int)key).ToString()  // 其他键用数字代码兜底
    };

    // ── 窗口事件 ──

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 自动保存设置
        try { SaveSettings(); } catch { }

        // 如果走到这里，说明 AppWindow.Closing 未被取消（用户选择了真正退出）
        _trayIcon?.RemoveIcon();
        _trayIcon?.Dispose();

        // 确保所有服务正确释放
        AppServices.Shutdown();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e) => SaveSettings();

    // ── 文本框失焦保存（自动保存）──

    private void OnPathLostFocus(object sender, RoutedEventArgs e)
    {
        _settings.OutputPath = PathTxt.Text;
        AppServices.Settings.SaveQuiet();
    }

    private void OnPrefixLostFocus(object sender, RoutedEventArgs e)
    {
        _settings.FileNamePrefix = PrefixTxt.Text;
        AppServices.Settings.SaveQuiet();
    }

    // ── Win32 窗口子类化（托盘消息处理）──
    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);
    private const int GWLP_WNDPROC = -4;

    private nint _originalWndProc;
    private Win32WndProc? _wndProcDelegate;
    private nint _mainWindowHwnd;

    private delegate nint Win32WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    // ── Win32 消息常量 ──
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_SETTINGCHANGE = 0x001A;

    private void SubclassWindowForTray(nint hwnd)
    {
        _mainWindowHwnd = hwnd;
        _wndProcDelegate = WndProcHook;
        _originalWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private nint WndProcHook(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 统一消息转发：托盘 + 热键（避免双重子类化冲突）
        _trayIcon?.HandleTrayMessage(msg, lParam);
        HotkeyManager.HandleHotKeyMessage(msg, wParam);

        // ═══ 显示器配置变更（分辨率/方向/连接/断开）═══
        if (msg == WM_DISPLAYCHANGE)
        {
            LogService.Info("MainWindow", "显示器配置变更 (WM_DISPLAYCHANGE)");
            _ = HandleDisplayChangeAsync();
        }

        // ═══ 系统设置变更（HDR 开关/ACM 开关等）═══
        if (msg == WM_SETTINGCHANGE)
        {
            // wParam 为 SPI_SET* 可区分设置类型，但 HDR 开关不在此列
            // 简单处理：非频繁触发时重新检测
            LogService.Info("MainWindow", "系统设置变更 (WM_SETTINGCHANGE)");
            _ = HandleDisplayChangeAsync();
        }

        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>显示器配置变更处理：重新检测能力并更新 UI。</summary>
    private async Task HandleDisplayChangeAsync()
    {
        try
        {
            // 清除旧的 ICC 缓存和 WGC 会话
            ColorProfileProvider.InvalidateCache();
            _wgcService?.InvalidateSessions();

            // 重新检测
            await DetectAndApplySystemCapabilitiesAsync();

            // 更新源色域显示
            DispatcherQueue.TryEnqueue(() =>
            {
                DetectAndShowSourceGamut();
                UpdateGamutMappingUI();
            });
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", $"显示器变更处理失败: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  运行日志面板
    // ═══════════════════════════════════════════════════════════════

    private string _logFilter = "All";
    private string _logCategoryFilter = "All";
    private string _logSearch = "";
    private readonly List<LogEntry> _logEntries = [];

    /// <summary>刷新日志列表视图。</summary>
    private void RefreshLogView()
    {
        var all = LogService.GetUiEntries();
        _logEntries.Clear();
        _logEntries.AddRange(all);

        ApplyLogFilter();
        UpdateLogStatus();
    }

    private void ApplyLogFilter()
    {
        // 修复: XAML 初始化期间 SelectionChanged 事件触发时控件尚未创建
        if (LogListView is null) return;

        var filtered = _logEntries.AsEnumerable();

        // 级别筛选
        if (_logFilter != "All")
        {
            var level = _logFilter switch
            {
                "Debug" => LogLevel.Debug,
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                _ => (LogLevel?)null,
            };
            if (level.HasValue)
                filtered = filtered.Where(e => e.Level == level.Value);
        }

        // 分类筛选
        if (_logCategoryFilter != "All")
        {
            var cat = _logCategoryFilter switch
            {
                "System" => LogCategory.System,
                "Capture" => LogCategory.Capture,
                "Encoding" => LogCategory.Encoding,
                "UI" => LogCategory.UI,
                "OCR" => LogCategory.OCR,
                "Network" => LogCategory.Network,
                _ => (LogCategory?)null,
            };
            if (cat.HasValue)
                filtered = filtered.Where(e => e.Category == cat.Value);
        }

        // 文本搜索
        if (!string.IsNullOrEmpty(_logSearch))
            filtered = filtered.Where(e =>
                e.Message.Contains(_logSearch, StringComparison.OrdinalIgnoreCase) ||
                e.Tag.Contains(_logSearch, StringComparison.OrdinalIgnoreCase));

        LogListView.ItemsSource = filtered.ToList();
    }

    private void UpdateLogStatus()
    {
        if (LogStatusTxt is not null)
            LogStatusTxt.Text = $"共 {LogService.GetUiEntries().Count} 条日志 | 目录: {LogService.LogDirectory}";
    }

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 修复: XAML 初始化期间 ComboBox 默认选中触发事件，此时其他控件可能尚未创建
        if (LogFilterCbo is null) return;
        _logFilter = (LogFilterCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        _logCategoryFilter = (LogCategoryCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        ApplyLogFilter();
    }

    private void OnLogSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (LogSearchTxt is null) return;
        _logSearch = LogSearchTxt.Text;
        ApplyLogFilter();
    }

    private void OnLogClear(object sender, RoutedEventArgs e)
    {
        LogListView.ItemsSource = null;
        _logEntries.Clear();
        if (LogStatusTxt is not null)
            LogStatusTxt.Text = "日志已清空";
    }

    private void OnLogOpenDir(object sender, RoutedEventArgs e)
    {
        LogService.OpenLogDirectory();
    }

    /// <summary>订阅实时日志推送（在 InitializeComponent 后调用）。</summary>
    private void SubscribeLogEvents()
    {
        LogService.OnLogEntry += entry =>
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _logEntries.Add(entry);
                    // 仅在日志页可见时刷新显示
                    if (PageLog.Visibility == Visibility.Visible)
                    {
                        ApplyLogFilter();
                        UpdateLogStatus();
                    }
                });
            }
            catch { }
        };
    }
}

// ── 字体工具：递归注入字体到所有控件 ──
/// <summary>遍历可视化树，为所有支持 FontFamily 的元素设置字体（绕过 XamlControlsResources 冲突）。</summary>
public static class FontHelper
{
    public static void ApplyFontToVisualTree(DependencyObject? parent, string fontFamily)
    {
        if (parent is null) return;
        // 为 Control / TextBlock 等支持 FontFamily 的元素设置字体
        if (parent is Microsoft.UI.Xaml.Controls.Control ctrl)
            ctrl.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily);
        else if (parent is TextBlock tb)
            tb.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily);

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            ApplyFontToVisualTree(child, fontFamily);
        }
    }
}

// AppSettingsData 已移至 Models/AppSettingsData.cs
