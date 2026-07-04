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
using TrueToneCap.Core.Services;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    private AppSettingsData _settings = new();
    private TrayIconManager? _trayIcon;
    private readonly List<(OutputFormat Format, string Label)> _formats;
    private volatile int _isCapturing; // 0=idle, 1=busy (防重入)
    private bool _isExiting;           // 托盘退出标志（跳过最小化）
    private TextBox? _recordingTarget; // 正在录制的快捷键输入框

    public MainWindow(bool isAutostart = false)
    {
        this.InitializeComponent();

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
                    FontLoader.UnloadBundledFonts();
                    Environment.Exit(0);
                });
            }
        };

        // ── 字体注入：在 Content 加载完成后递归遍历可视化树 ──
        if (this.Content is FrameworkElement fe)
        {
            if (fe.IsLoaded)
                FontHelper.ApplyFontToVisualTree(fe, FontLoader.DefaultFontFamily);
            else
                fe.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(fe, FontLoader.DefaultFontFamily);
        }

        _formats =
        [
            (OutputFormat.PNG, "PNG (无损)"),
            (OutputFormat.JPEG_GAINMAP, "JPEG Gain Map (HDR)"),
            (OutputFormat.JPEG_LI, "JPEG LI"),
            (OutputFormat.JPEG_XL, "JPEG XL"),
            (OutputFormat.AVIF, "AVIF"),
            (OutputFormat.WebP, "WebP"),
            (OutputFormat.BMP, "BMP"),
        ];

        FormatCbo.ItemsSource = _formats.Select(f => f.Label).ToList();
        LoadSettings();             // 仅加载配置文件，不做检测
        ApplySettingsToUI();        // 将配置反映到 UI
        UpdateQualityPanel();

        StatusTxt.Text = "能力检测中...";

        _trayIcon = new TrayIconManager(this);
        _trayIcon.OnCaptureHotkey = () => DispatcherQueue.TryEnqueue(() => StartSelectionCapture());
        _trayIcon.OnExitApp = () => _isExiting = true;
        _trayIcon.RegisterCaptureHotkey(_settings.Hotkey);

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

        _uiReady = true;

        // ── 默认显示第一页（输出设置）──
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
        var (sysHdr, sysAcm) = DetectSystemDisplayState();
        bool customIcc = false;
        try
        {
            customIcc = await Task.Run(DetectCustomIccProfile).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { }

        // AVIF 硬件编码器实测（独立模块 AvifHardwareProbe）
        try
        {
            var probe = await Task.Run(() => AvifHardwareProbe.Result).WaitAsync(TimeSpan.FromSeconds(10));
            _settings.NvencAvailable = probe.NvencAvailable;
            _settings.QsvAvailable = probe.QsvAvailable;
        }
        catch { /* 超时 = 全部不可用 */ }

        _settings.AcmeDetected = sysAcm;
        bool firstRun = _settings.FirstRun;

        if (firstRun)
        {
            _settings.FirstRun = false;
            _settings.HdrEnabled = sysHdr;
            _settings.IccBakeEnabled = customIcc;        // 有自定义 ICC 时默认启用烘焙
            _settings.ColorSpaceIndex = DetectBestColorSpace(sysHdr, sysAcm, customIcc);
            SaveSettingsQuiet();
        }

        // 更新 UI
        DispatcherQueue.TryEnqueue(() =>
        {
            HdrSwitch.IsEnabled = sysHdr;
            HdrSwitch.IsOn = _settings.HdrEnabled;
            // 获取显示器位深
            var displays = DisplayEnumerator.EnumerateDisplays();
            int bitDepth = displays.FirstOrDefault(d => d.IsHdr)?.BitsPerColor
                ?? displays.FirstOrDefault()?.BitsPerColor ?? 8;
            _settings.DisplayBitDepth = bitDepth;
            var hdrText = sysHdr ? $"✅ HDR 已启用 ({bitDepth}-bit)" : "⚠ HDR 未开启（已禁用）";
            var acmText = sysAcm ? " | ACM 已启用" : "";
            HdrHintTxt.Text = hdrText + acmText;

            IccBakeSwitch.IsEnabled = customIcc;
            IccBakeSwitch.IsOn = _settings.IccBakeEnabled;
            IccHintTxt.Text = customIcc
                ? "检测到自定义 ICC 校色文件，可启用烘焙"
                : "未检测到自定义 ICC 文件（系统使用默认 sRGB），烘焙不可用";

            // GPU 编码器可用性标记
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

    /// <summary>根据 HDR/ACM/ICC 状态决定最佳色彩空间默认值。</summary>
    private static int DetectBestColorSpace(bool hdr, bool acm, bool customIcc)
    {
        if (hdr) return 5;                     // HDR → BT.2020
        if (acm) return 0;                     // ACM → 跟随系统动态管理
        if (customIcc) return 0;               // 自定义 ICC 校色 → 跟随系统（ICC profile 决定色域映射）
        return 0;                               // 默认 → 跟随系统（sRGB 由系统管理）
    }

    /// <summary>静默保存设置（不更新 UI 控件值，直接序列化 _settings）。</summary>
    private void SaveSettingsQuiet()
    {
        try
        {
            File.WriteAllText(GetSettingsPath(),
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── 设置 ──

    private void LoadSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _settings = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new();
            }
        }
        catch { _settings = new AppSettingsData(); }
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
        AutoStartChk.IsChecked = _settings.AutoStart;
        PreviewChk.IsChecked = _settings.ShowPreview;
        MinimizeTrayChk.IsChecked = _settings.MinimizeToTray;
        AvifPngSuffixChk.IsChecked = _settings.AvifPngSuffix;
        AvifBackendCbo.SelectedIndex = Math.Clamp(_settings.AvifBackendIndex, 0, 3);
        if (AvifChromaCbo is not null) SetComboByTag(AvifChromaCbo, _settings.AvifChroma);
        RecordQualitySld.Value = _settings.RecordQuality;
        if (ArchiveChk is not null) ArchiveChk.IsChecked = _settings.ArchiveEnabled;
        if (ArchiveModeCbo is not null) { SetComboByTag(ArchiveModeCbo, _settings.ArchiveMode); ArchiveModePanel.Visibility = _settings.ArchiveEnabled ? Visibility.Visible : Visibility.Collapsed; }

        // LLM 设置
        UseLlmChk.IsChecked = _settings.UseCustomLlm;
        LlmEndpointTxt.Text = _settings.LlmEndpoint;
        LlmApiKeyTxt.Text = _settings.LlmApiKey;
        LlmModelTxt.Text = _settings.LlmModel;
        LlmPromptTxt.Text = _settings.LlmSystemPrompt;
        SetComboByTag(TargetLangCbo, _settings.TargetLanguage);
        SetComboByTag(OcrLangCbo, _settings.OcrLanguage);
        SetComboByTag(OcrEngineCbo, _settings.OcrEngineMode);
        SetComboByTag(ThemeCbo, _settings.ThemeMode);
        // 主题已在 App.OnLaunched 中初始化，此处仅恢复 ComboBox 选中项
        // Apply engine mode immediately
        MultiOcrService.ForceEngine = _settings.OcrEngineMode switch
        {
            "Gpu" => "DirectML",
            "Cpu" => "ONNX PP-OCRv4-server (Cpu)",
            "Windows" => "Windows OCR",
            _ => null
        };
        // Gain Map 模式
        if (GainMapModeCbo is not null) SetComboByTag(GainMapModeCbo, _settings.GainMapMode);
    }

    /// <summary>检测系统显示状态：HDR 是否启用、ACM 是否启用。</summary>
    private static (bool hdr, bool acm) DetectSystemDisplayState()
    {
        bool hdr = false, acm = false;
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            hdr = displays.Any(d => d.IsHdr);

            // ACM 检测：Windows 11 24H2+ 通过注册表
            // HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\ICM\AcmeEnabled
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\ICM");
            if (key is not null)
            {
                var val = key.GetValue("AcmeEnabled");
                acm = val is int i && i != 0;
            }
        }
        catch { }
        return (hdr, acm);
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
            _settings.OutputPath = PathTxt.Text;
            _settings.FileNamePrefix = PrefixTxt.Text;
            _settings.HdrEnabled = HdrSwitch.IsOn;
            _settings.IccBakeEnabled = IccBakeSwitch.IsOn;
            _settings.ColorSpaceIndex = ColorCbo.SelectedIndex;
            _settings.Hotkey = HotkeyTxt.Text;
            _settings.RecordHotkey = RecordHotkeyTxt.Text;
            _settings.AutoStart = AutoStartChk.IsChecked == true;
            _settings.ShowPreview = PreviewChk.IsChecked == true;
            _settings.MinimizeToTray = MinimizeTrayChk.IsChecked == true;
            _settings.AvifPngSuffix = AvifPngSuffixChk.IsChecked == true;
            // Gain Map 模式
            if (GainMapModeCbo is not null)
                _settings.GainMapMode = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rgb";
            _settings.AvifBackendIndex = AvifBackendCbo.SelectedIndex;
            _settings.AvifChroma = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "420";
            _settings.RecordQuality = RecordQualitySld.Value;
            _settings.AnimAvifBackendIndex = 0;
            _settings.ArchiveEnabled = ArchiveChk?.IsChecked == true;
            _settings.ArchiveMode = (ArchiveModeCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Month";
            _settings.AcmeDetected = _settings.AcmeDetected;
            _settings.FirstRun = false;

            // LLM 设置
            _settings.UseCustomLlm = UseLlmChk.IsChecked == true;
            _settings.LlmEndpoint = LlmEndpointTxt.Text;
            _settings.LlmApiKey = LlmApiKeyTxt.Text;
            _settings.LlmModel = LlmModelTxt.Text;
            _settings.LlmSystemPrompt = LlmPromptTxt.Text;
            _settings.TargetLanguage = (TargetLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            _settings.OcrLanguage = (OcrLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            // 序列化到 JSON
            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            string path = GetSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);

            // 热键 + 自启同步
            try { StartupManager.IsEnabled = _settings.AutoStart; } catch { }
            try { _trayIcon?.RegisterCaptureHotkey(_settings.Hotkey); } catch { }

            StatusTxt.Text = "✅ 设置已保存";
        }
        catch (Exception ex) { StatusTxt.Text = "❌ 保存失败: " + ex.Message; }
    }

    private static string GetSettingsPath()
    {
        // 仅保存到 EXE 同目录
        return Path.Combine(AppContext.BaseDirectory, "TrueToneCap.settings.json");
    }

    // ── 浏览文件夹 ──

    private async void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) PathTxt.Text = folder.Path;
    }

    // ── 动态格式面板 ──

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateQualityPanel();

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

        bool isAvif = format == OutputFormat.AVIF;
        AvifPngSuffixChk.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        AvifBackendPanel.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        if (AvifChromaPanel is not null)
        {
            bool isLibAom = isAvif && (AvifBackendCbo.SelectedItem as ComboBoxItem)?.Tag as string == "LibAom";
            AvifChromaPanel.Visibility = isLibAom ? Visibility.Visible : Visibility.Collapsed;
        }

        // JPEG Gain Map 增益图模式选择（仅当格式为 JPEG_GAINMAP 时显示）
        if (GainMapModePanel is not null)
            GainMapModePanel.Visibility = format == OutputFormat.JPEG_GAINMAP ? Visibility.Visible : Visibility.Collapsed;

        // AVIF + NVENC/QSV 不支持 CRF=0 无损，限制最小值为 1（必须在设置 Value 之前）
        if (isAvif)
        {
            int backendIdx = AvifBackendCbo.SelectedIndex;
            if (backendIdx is 2 or 3 || (backendIdx == 0 && (_settings.NvencAvailable || _settings.QsvAvailable)))
                QualitySld.Minimum = Math.Max(min, 1.0);
        }

        // Quality 优先使用已保存值（在有效范围内），否则用默认值
        double savedQ = _settings.Quality;
        double useQ = (savedQ >= QualitySld.Minimum && savedQ <= QualitySld.Maximum) ? savedQ : def;
        QualitySld.Value = useQ;
        QualityLbl.Text = encoder.GetQualityDescription((float)useQ);
        QualityTxt.Text = useQ.ToString("F1");
        QualityTxt.Visibility = precise ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAvifBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateQualityPanel();
        bool isLibAom = (AvifBackendCbo.SelectedItem as ComboBoxItem)?.Tag as string == "LibAom";
        if (AvifChromaPanel is not null)
            AvifChromaPanel.Visibility = isLibAom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnArchiveChanged(object sender, RoutedEventArgs e)
        => ArchiveModePanel.Visibility = ArchiveChk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>根据归档模式生成子目录路径。</summary>
    private string GetArchivePath(string baseDir)
    {
        var now = DateTime.Now;
        string mode = _settings.ArchiveMode;
        string sub = mode switch
        {
            "Year" => now.ToString("yyyy"),
            "Day" => now.ToString("yyyy-MM-dd"),
            _ => now.ToString("yyyy-MM"), // Month (default)
        };
        string full = Path.Combine(baseDir, sub);
        Directory.CreateDirectory(full);
        return full;
    }

    private void OnQualityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (FormatCbo.SelectedIndex < 0) return;
        var (format, _) = _formats[FormatCbo.SelectedIndex];
        var encoder = EncoderFactory.Create(format);
        double val = Math.Round(QualitySld.Value, 1);
        QualityLbl.Text = encoder.GetQualityDescription((float)val);
        QualityTxt.Text = val.ToString("F1");
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
        { v = Math.Clamp(Math.Round(v, 1), QualitySld.Minimum, QualitySld.Maximum); QualityTxt.Text = v.ToString("F1"); QualitySld.Value = v; }
        else QualityTxt.Text = QualitySld.Value.ToString("F1");
    }

    // ── HDR + ACM + ICC ──

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        _settings.HdrEnabled = HdrSwitch.IsOn;
        // 用户手动切 HDR → 建议同步色彩空间
        if (HdrSwitch.IsOn) _settings.ColorSpaceIndex = 5; // HDR → BT.2020
        else _settings.ColorSpaceIndex = 0;                // SDR → 跟随系统
        ColorCbo.SelectedIndex = _settings.ColorSpaceIndex;
    }

    /// <summary>检测当前显示器是否使用自定义 ICC 配置文件（独立于 sRGB 判断）。</summary>
    private static bool DetectCustomIccProfile()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            foreach (var d in displays)
            {
                var icc = ColorProfileProvider.GetDisplayIccProfile(d.MonitorHandle);
                // 任何 > 500 字节的真实 ICC 配置文件均视为自定义 ICC
                // 原因：工厂校准/用户校色/系统默认 ICC 都应触发烘焙以保证色彩准确
                if (icc is not null && icc.Length > 500)
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ── 编码辅助（仅在最终保存/复制时触发）──

    /// <summary>获取当前显示器 ICC + 如果启用烘焙则预先烘焙像素。</summary>
    private byte[]? PreparePixelsWithIccBake(byte[] bgra, int w, int h)
    {
        if (!IccBakeSwitch.IsOn) return bgra;
        try
        {
            var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
            var icc = ColorProfileProvider.GetDisplayIccProfile(cursorMonitor);
            if (icc is not null && icc.Length > 500)
            {
                var baked = ColorProfileProvider.BakeIccToSrgb(bgra, w, h, icc);
                if (baked is not null) return baked;
            }
        }
        catch { }
        return bgra;
    }

    private async Task EncodeAndSaveAsync(byte[] bgra, int w, int h)
    {
        try
        {
            if (bgra is null || bgra.Length != w * h * 4)
            {
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 像素数据无效");
                return;
            }
            bgra = PreparePixelsWithIccBake(bgra, w, h) ?? bgra;
            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var encoder = EncoderFactory.Create(format);
            var avifBackend = AvifBackendCbo.SelectedIndex switch { 1 => AvifEncoderBackend.LibAom, 2 => AvifEncoderBackend.Qsv, 3 => AvifEncoderBackend.Nvenc, _ => AvifEncoderBackend.Auto };
            var avifPngSuffix = AvifPngSuffixChk.IsChecked == true;
            var settings = new EncodingSettings
            {
                Format = format, Quality = (float)QualitySld.Value, HdrOutput = _settings.HdrEnabled,
                AvifBackend = avifBackend, AvifPngSuffix = avifPngSuffix,
                AvifChroma = _settings.AvifChroma,
                DisplayBitDepth = _settings.DisplayBitDepth,
                GainMapMode = _settings.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
                ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
            };
            var outDir = PathTxt.Text;
            if (string.IsNullOrWhiteSpace(outDir))
                outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            // 归档子目录
            if (_settings.ArchiveEnabled)
                outDir = GetArchivePath(outDir);

            var ext = format switch { OutputFormat.JPEG_LI => ".jpg", OutputFormat.JPEG_GAINMAP => ".jpg", OutputFormat.JPEG_XL => ".jxl", OutputFormat.AVIF => ".avif", OutputFormat.WebP => ".webp", OutputFormat.BMP => ".bmp", _ => ".png" };
            if (format == OutputFormat.AVIF && avifPngSuffix) ext += ".png";
            var path = Path.Combine(outDir, $"{PrefixTxt.Text}{DateTime.Now:yyyyMMdd_HHmmssfff}{ext}");
            await Task.Run(() => encoder.EncodeSdrAsync(bgra, w, h, settings, path));

            // 将输出文件复制到剪贴板（可在资源管理器/微信/QQ 粘贴）
            await CopyFileToClipboardAsync(path);
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ 保存失败: {ex.Message}");
        }
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
            bgra = PreparePixelsWithIccBake(bgra, w, h) ?? bgra;
            // 编码为 PNG 到临时文件用于剪贴板
            var encoder = EncoderFactory.Create(OutputFormat.PNG);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.PNG, Quality = 100, HdrOutput = false,
                ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
            };
            var tmpPath = Path.Combine(Path.GetTempPath(), $"ttc_clip_{Guid.NewGuid():N}.png");
            await Task.Run(() => encoder.EncodeSdrAsync(bgra, w, h, settings, tmpPath));

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

    // ── 选区动作（零编码延迟：原始像素直接传递）──

    // ── 选区截图（QQ 风格） ──

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    /// <summary>捕获整个虚拟桌面（所有显示器）。超过 4096 时自动分块拼接。</summary>
    private static byte[]? CaptureFullVirtualDesktop()
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vw <= 0 || vh <= 0) return null;

        const int maxDim = 4096;

        if (vw <= maxDim && vh <= maxDim)
            return CaptureViaGdi(vx, vy, vw, vh);

        // 分块拼接
        var full = new byte[vw * vh * 4];
        for (int tileY = 0; tileY < vh; tileY += maxDim)
        {
            int tileH = Math.Min(maxDim, vh - tileY);
            for (int tileX = 0; tileX < vw; tileX += maxDim)
            {
                int tileW = Math.Min(maxDim, vw - tileX);
                var tile = CaptureViaGdi(vx + tileX, vy + tileY, tileW, tileH);
                if (tile is null) return null;
                int fullStride = vw * 4, tileStride = tileW * 4;
                for (int row = 0; row < tileH; row++)
                {
                    Buffer.BlockCopy(tile, row * tileStride, full, ((tileY + row) * fullStride) + (tileX * 4), tileStride);
                }
            }
        }
        return full;
    }

    /// <summary>从预捕获桌面像素中提取区域。（原地、零拷贝语义）</summary>
    private static byte[]? ExtractRegionFromDesktop(byte[] full, int fullW, int fullH,
        int vx, int vy, RectInt32 screenRect)
    {
        int rx = screenRect.X - vx;
        int ry = screenRect.Y - vy;
        int rw = screenRect.Width;
        int rh = screenRect.Height;

        if (rx < 0 || ry < 0 || rx + rw > fullW || ry + rh > fullH)
            return null; // 越界

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

    private void StartSelectionCapture()
    {
        // ── 防重入：截图进行中时忽略热键 ──
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            System.Diagnostics.Trace.WriteLine("[MainWindow] 截图已在进行中，忽略重复触发");
            return;
        }

        try
        {
            StatusTxt.Text = "📷 捕获桌面...";

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            var desktopPixels = CaptureFullVirtualDesktop();
            if (desktopPixels is null || desktopPixels.Length != vw * vh * 4)
            {
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 桌面捕获失败");
                return;
            }

            var overlay = new SelectionOverlay(desktopPixels, vx, vy, vw, vh);
            overlay.Activate();

            overlay.ActionCompleted += async (action, rect) =>
            {
                if (action == SelectionOverlay.ActionResult.Cancel)
                {
                    DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "就绪");
                    return;
                }

                // 优先使用标注合成后的像素（标注已在覆盖层内完成）
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
                        await EncodeAndSaveAsync(regionPixels, rect.Width, rect.Height);
                        break;
                    case SelectionOverlay.ActionResult.Copy:
                        await EncodeAndCopyAsync(regionPixels, rect.Width, rect.Height);
                        break;
                    case SelectionOverlay.ActionResult.Ocr:
                        await CaptureAndOcrFromPixelsAsync(regionPixels, rect.Width, rect.Height);
                        break;
                    case SelectionOverlay.ActionResult.Translate:
                        await CaptureAndTranslateFromPixelsAsync(regionPixels, rect.Width, rect.Height);
                        break;
                }

                // 截图完成 → 仅保存/复制时缩回托盘
                if (MinimizeTrayChk.IsChecked == true
                    && action is SelectionOverlay.ActionResult.Confirm or SelectionOverlay.ActionResult.Copy)
                    DispatcherQueue.TryEnqueue(() => _trayIcon?.MinimizeToTray());
            };

            // ── 截图完成 / 取消时释放锁 ──
            overlay.ActionCompleted += (_, _) => Interlocked.Exchange(ref _isCapturing, 0);
        }
        finally { Interlocked.Exchange(ref _isCapturing, 0); }
    }

    private async Task CaptureAndOcrFromPixelsAsync(byte[] pixels, int w, int h)
    {
        StatusTxt.Text = "📝 文字提取中...";
        try
        {
            var ocrLang = _settings.OcrLanguage;
            var result = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(result.Error))
                StatusTxt.Text = $"❌ OCR: {result.Error}";
            else if (string.IsNullOrWhiteSpace(result.Text))
                StatusTxt.Text = "📝 未检测到文字";
            else
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try { await ShowOcrResultAsync(result.Text); }
                    catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
                });
        }
        catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
    }

    private async Task CaptureAndTranslateFromPixelsAsync(byte[] pixels, int w, int h)
    {
        StatusTxt.Text = "🌐 翻译中...";
        try
        {
            var ocrLang = _settings.OcrLanguage;
            var ocrResult = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(ocrResult.Error) || string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                StatusTxt.Text = string.IsNullOrEmpty(ocrResult.Error) ? "📝 未检测到文字" : $"❌ {ocrResult.Error}";
                return;
            }

            var config = new LlmConfig
            {
                UseCustomLlm = _settings.UseCustomLlm,
                ApiEndpoint = _settings.LlmEndpoint,
                ApiKey = _settings.LlmApiKey,
                ModelName = _settings.LlmModel,
                SystemPrompt = _settings.LlmSystemPrompt,
                TargetLanguage = _settings.TargetLanguage
            };
            var translator = new TranslationService(config);
            var translated = await translator.TranslateAsync(ocrResult.Text, _settings.TargetLanguage);

            DispatcherQueue.TryEnqueue(async () =>
            {
                try { await ShowTranslationResultAsync(ocrResult.Text, translated); }
                catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
            });
        }
        catch (Exception ex) { StatusTxt.Text = $"❌ {ex.Message}"; }
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

    /// <summary>GDI 捕获指定屏幕坐标区域。w/h 超过 4096 会自动分块。供外部（OnCaptureNow SDR 路径）使用。</summary>
    public static byte[]? CaptureViaGdiStatic(int x, int y, int w, int h)
    {
        const int maxDim = 4096;
        if (w <= maxDim && h <= maxDim)
            return CaptureViaGdi(x, y, w, h);

        // 分块
        var full = new byte[w * h * 4];
        for (int ty = 0; ty < h; ty += maxDim)
        {
            int th = Math.Min(maxDim, h - ty);
            for (int tx = 0; tx < w; tx += maxDim)
            {
                int tw = Math.Min(maxDim, w - tx);
                var tile = CaptureViaGdi(x + tx, y + ty, tw, th);
                if (tile is null) return null;
                int fStride = w * 4, tStride = tw * 4;
                for (int row = 0; row < th; row++)
                {
                    Buffer.BlockCopy(tile, row * tStride, full, ((ty + row) * fStride) + (tx * 4), tStride);
                }
            }
        }
        return full;
    }

    /// <summary>GDI 捕获指定屏幕坐标区域（单块，<=4096）。</summary>
    private static byte[]? CaptureRegionBytes(int x, int y, int w, int h)
        => CaptureViaGdi(x, y, w, h);

    // ── 截图按钮（选区模式） ──

    private void OnCaptureBtn(object sender, RoutedEventArgs e) => StartSelectionCapture();

    private async void OnCaptureNow(object sender, RoutedEventArgs e)
    {
        // ── 防重入 ──
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        StatusTxt.Text = "📷 截图中...";
        CaptureBtn.IsEnabled = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
        var quality = (float)QualitySld.Value;
        var outDir = PathTxt.Text;
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        // 归档子目录
        if (_settings.ArchiveEnabled)
            outDir = GetArchivePath(outDir);
        var prefix = PrefixTxt.Text;
        var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
        var avifBackend = AvifBackendCbo.SelectedIndex switch { 1 => AvifEncoderBackend.LibAom, 2 => AvifEncoderBackend.Qsv, 3 => AvifEncoderBackend.Nvenc, _ => AvifEncoderBackend.Auto };
        var avifPngSuffix = AvifPngSuffixChk.IsChecked == true;
        var showPreview = PreviewChk.IsChecked == true;

        try
        {
            // ── 使用显式 STA 线程（WinRT Windows.Graphics.Capture 需要 STA）──
            var tcs = new TaskCompletionSource<(string?, bool)>();
            var captureThread = new Thread(() =>
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;

                using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
                var displayInfo = DisplayEnumerator.FindDisplayByMonitor(cursorMonitor);
                if (displayInfo is null) throw new InvalidOperationException("找不到当前显示器");

                bool actualHdr = hdrOutput;
                float[]? hdrPixels = null;
                byte[]? sdrBytes = null;
                int fw = displayInfo.Width, fh = displayInfo.Height;
                byte[]? icc = null;

                if (actualHdr)
                {
                    // ── 原生 HDR: Windows.Graphics.Capture (WinRT) ──
                    using var winrtCap = new WinRtScreenCapture(device, cursorMonitor);
                    if (!winrtCap.Initialize())
                        throw new InvalidOperationException("HDR 输出失败: Windows.Graphics.Capture 初始化不成功");
                    var hdrFrame = winrtCap.TryAcquireNextFrameAsync(2000).GetAwaiter().GetResult();
                    if (hdrFrame is null)
                        throw new InvalidOperationException("HDR 输出失败: 获取 HDR 帧超时");
                    fw = hdrFrame.Width; fh = hdrFrame.Height;
                    hdrPixels = hdrFrame.GetFloatPixelsAsync().GetAwaiter().GetResult();
                    bool allZero = true;
                    for (int i = 0; i < Math.Min(hdrPixels.Length, 400); i++)
                    { if (hdrPixels[i] != 0f) { allZero = false; break; } }
                    if (allZero)
                        throw new InvalidOperationException("HDR 输出失败: WinRT 返回全零 Float16 帧");
                }
                else
                {
                    // ── SDR: DXGI + GDI 回退 ──
                    using (var capture = ScreenCapture.CreateForMonitor(cursorMonitor, forceSdr: true))
                    using (var frame = capture.TryAcquireNextFrame(500))
                    {
                        if (frame is null) throw new TimeoutException("截图超时");
                        fw = frame.Width; fh = frame.Height;
                        icc = ColorProfileProvider.GetDisplayIccProfile(displayInfo.MonitorHandle);
                        sdrBytes = frame.IsHdr
                            ? CaptureViaGdiStatic(displayInfo.X, displayInfo.Y, fw, fh)
                            : frame.GetBytePixelsAsync().GetAwaiter().GetResult();
                        if (sdrBytes is null) throw new InvalidOperationException("SDR 截图失败");
                    }
                }

                // 编码
                var meta = MetadataCollector.Collect(displayInfo, null!);
                var settings = new EncodingSettings
                {
                    Format = format, Quality = quality,
                    HdrOutput = actualHdr, IccProfile = icc, Metadata = meta,
                    PreferGpuEncode = true, AvifBackend = avifBackend,
                    AvifPngSuffix = avifPngSuffix,
                    AvifChroma = _settings.AvifChroma,
                    DisplayBitDepth = _settings.DisplayBitDepth,
                    GainMapMode = _settings.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
                    ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
                };
                var encoder = EncoderFactory.Create(format);

                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                var ext = format switch { OutputFormat.JPEG_LI => ".jpg", OutputFormat.JPEG_GAINMAP => ".jpg", OutputFormat.JPEG_XL => ".jxl", OutputFormat.AVIF => ".avif", OutputFormat.WebP => ".webp", OutputFormat.BMP => ".bmp", _ => ".png" };
                if (format == OutputFormat.AVIF && avifPngSuffix) ext += ".png";
                var fullPath = Path.Combine(outDir, $"{prefix}{DateTime.Now:yyyyMMdd_HHmmssfff}{ext}");

                if (hdrPixels is not null)
                    encoder.EncodeAsync(new HdrFrameData { Pixels = hdrPixels, Width = fw, Height = fh, IccProfile = icc }, settings, fullPath).GetAwaiter().GetResult();
                else
                    encoder.EncodeSdrAsync(sdrBytes!, fw, fh, settings, fullPath).GetAwaiter().GetResult();

                tcs.SetResult((fullPath, actualHdr));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            captureThread.SetApartmentState(ApartmentState.STA);
            captureThread.IsBackground = true;
            captureThread.Start();

            var (resultPath, wasHdr) = await tcs.Task;

            sw.Stop();
            string status = wasHdr ? $"✅ HDR 已保存 ({sw.ElapsedMilliseconds}ms)" : $"✅ 已保存 ({sw.ElapsedMilliseconds}ms)";
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = status);
            // 「捕获现在」不自动缩回托盘（仅截图热键/按钮触发时才缩回）
        }
        catch (Exception ex)
        {
            sw.Stop();
            StatusTxt.Text = $"❌ {ex.Message}";
        }
        finally { CaptureBtn.IsEnabled = true; Interlocked.Exchange(ref _isCapturing, 0); }
    }

    // ── 快捷键录制 ──

    private bool _uiReady; // InitializeComponent 完成后才响应 UI 事件

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return; // XAML 初始化期间忽略
        var tag = (OcrEngineCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        _settings.OcrEngineMode = tag;

        // 映射到 MultiOcrService 的强制引擎
        MultiOcrService.ForceEngine = tag switch
        {
            "Gpu" => "DirectML",
            "Cpu" => "ONNX PP-OCRv4-server (Cpu)",
            "Windows" => "Windows OCR",
            _ => null  // "Auto" → 不强制, 自动降级
        };

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

        // 解析有效主题（Default → 跟随系统）
        var effective = App.ResolveEffectiveTheme(mode);

        // 设置窗口级主题
        if (Content is FrameworkElement fe)
        {
            fe.RequestedTheme = effective switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark or AppThemeMode.OLED => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        try { SaveSettingsQuiet(); } catch { }
    }

    private void UpdateOcrEngineStatus()
    {
        if (OcrEngineStatus is null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var eng in MultiOcrService.Engines)
        {
            if (eng?.Info is null) continue;
            sb.Append(eng.Info.IsAvailable ? "✅ " : "⚠️ ");
            sb.Append(eng.Info.Name);
            if (eng.Info.Version is not null) sb.Append($" v{eng.Info.Version}");
            sb.Append("  ");
        }
        OcrEngineStatus.Text = sb.Length > 0 ? sb.ToString().Trim() : "OCR 引擎探测中...";
    }

    private void OnCaptureHotkeyRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(HotkeyTxt);

    private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(RecordHotkeyTxt);

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
        if (sender != _recordingTarget) return;

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
        FontLoader.UnloadBundledFonts();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e) => SaveSettings();

    // ── HDR 辅助: BGRA → scRGB linear ──

    private static float[] BgraToScrgbLinear(byte[] bgra, int w, int h)
    {
        int pixelCount = w * h;
        var linear = new float[pixelCount * 4];
        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float b = SRgbToLinear(bgra[i] / 255f);
            float g = SRgbToLinear(bgra[i + 1] / 255f);
            float r = SRgbToLinear(bgra[i + 2] / 255f);
            float a = bgra[i + 3] / 255f;
            linear[i] = r;
            linear[i + 1] = g;
            linear[i + 2] = b;
            linear[i + 3] = a;
        });
        return linear;
    }

    private static float SRgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    // ── GDI 回退 ──

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdcD, int xD, int yD, int w, int h, nint hdcS, int xS, int yS, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint cLines, byte[]? lpBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private static byte[]? CaptureViaGdi(int x, int y, int w, int h)
    {
        nint hdcScreen = 0, hdcMem = 0, hBitmap = 0, hOld = 0;
        try
        {
            hdcScreen = GetDC(0);
            if (hdcScreen == 0) return null;
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, w, h);
            if (hBitmap == 0) return null;
            hOld = SelectObject(hdcMem, hBitmap);
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SRCCOPY))
                return null;

            var bytes = new byte[w * h * 4];
            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -h,
                    biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
                }
            };
            GetDIBits(hdcMem, hBitmap, 0, (uint)h, bytes, ref bi, DIB_RGB_COLORS);

            // ── 关键修复: GetDIBits 在部分 GPU 上将 Alpha 填充为 0x00 ──
            // 导致 SoftwareBitmap(Bgra8, Premultiplied) → 全透明 → 白色窗口背景透出
            for (int i = 3; i < bytes.Length; i += 4)
                bytes[i] = 0xFF;

            return bytes;
        }
        catch { return null; }
        finally
        {
            if (hOld != 0) SelectObject(hdcMem, hOld);
            if (hBitmap != 0) DeleteObject(hBitmap);
            if (hdcMem != 0) DeleteDC(hdcMem);
            if (hdcScreen != 0) ReleaseDC(0, hdcScreen);
        }
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

internal sealed class AppSettingsData
{
    public int FormatIndex { get; set; }
    public double Quality { get; set; } = 80;
    public string OutputPath { get; set; } = "";
    public string FileNamePrefix { get; set; } = "TrueToneCap_";
    public bool HdrEnabled { get; set; } = true;
    public bool IccBakeEnabled { get; set; }
    public int ColorSpaceIndex { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+S";
    public string RecordHotkey { get; set; } = "Ctrl+Shift+G";
    public bool AutoStart { get; set; }
    public bool ShowPreview { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AvifPngSuffix { get; set; }
    public int AvifBackendIndex { get; set; }
    public string AvifChroma { get; set; } = "420"; // 420 / 422 / 444
    public double RecordQuality { get; set; } = 80;
    public int AnimAvifBackendIndex { get; set; }

    // 归档设置
    public bool ArchiveEnabled { get; set; }
    public string ArchiveMode { get; set; } = "Month"; // Year / Month / Day

    // LLM / 翻译设置
    public bool UseCustomLlm { get; set; }
    public string LlmEndpoint { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o-mini";
    public string LlmSystemPrompt { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string OcrLanguage { get; set; } = "";

    // 自动检测标志
    public bool AcmeDetected { get; set; }
    public bool FirstRun { get; set; } = true;
    public bool NvencAvailable { get; set; }
    public bool QsvAvailable { get; set; }
    public int DisplayBitDepth { get; set; } = 8;
    /// <summary>JPEG Gain Map 增益图模式: Rgb 彩色增益 / Gray 灰度增益。</summary>
    public string GainMapMode { get; set; } = "Rgb"; // Rgb / Gray
    /// <summary>OCR 引擎选择: Auto / Gpu / Windows / Cpu。</summary>
    public string OcrEngineMode { get; set; } = "Auto";
    /// <summary>主题模式: Default / Light / Dark / OLED。</summary>
    public string ThemeMode { get; set; } = "Default";

}
