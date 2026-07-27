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
    private bool _isExiting;           // 托盘退出标志（跳过最小化）
    private TextBox? _recordingTarget; // 正在录制的快捷键输入框

    // ── 通过 AppServices 访问共享服务（不再本地持有）──
    private WgcCaptureService? _wgcService => AppServices.Wgc;
    private GpuToneMapper? _gpuToneMapper => AppServices.GpuToneMapper;

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

        _uiReady = true;

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

        // ACM 优先：ACM 开启时禁用 ICC 烘焙（避免双重色彩管理）
        if (cap.SystemAcm && _settings.IccBakeEnabled)
            _settings.IccBakeEnabled = false;

        if (_settings.FirstRun)
        {
            _settings.FirstRun = false;
            _settings.HdrEnabled = cap.SystemHdr;
            _settings.IccBakeEnabled = cap.IccBakeAvailable;
            _settings.ColorSpaceIndex = CapabilityService.DetectBestColorSpace(
                cap.SystemHdr, cap.SystemAcm, cap.CustomIcc);
            AppServices.Settings.SaveQuiet();
        }

        // 更新 UI
        DispatcherQueue.TryEnqueue(() =>
        {
            HdrSwitch.IsEnabled = cap.SystemHdr;
            HdrSwitch.IsOn = _settings.HdrEnabled;
            var hdrText = cap.SystemHdr ? $"✅ HDR 已启用 ({cap.DisplayBitDepth}-bit)" : "⚠ HDR 未开启（已禁用）";
            var acmText = cap.SystemAcm ? " | ACM 已启用（系统管理色彩）" : "";
            HdrHintTxt.Text = hdrText + acmText;

            IccBakeSwitch.IsEnabled = cap.IccBakeAvailable;
            IccBakeSwitch.IsOn = _settings.IccBakeEnabled;
            if (cap.SystemAcm)
                IccHintTxt.Text = "ACM 已启用 — Windows 自动管理显示器色彩，ICC 烘焙已禁用。";
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
        DetectAndShowSourceGamut();
        // 主题已在 App.OnLaunched 中初始化，此处仅恢复 ComboBox 选中项
        // Apply engine mode immediately
        MultiOcrService.ForceEngine = _settings.OcrEngineMode switch
        {
            "Gpu" => "DirectML",
            "Cpu" => "PP-OCRv6 (Cpu",
            "Windows" => "Windows OCR",
            _ => null
        };
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
            if (GainMapModeCbo is not null)
                _settings.GainMapMode = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rgb";
            _settings.AvifBackendIndex = AvifBackendCbo.SelectedIndex;
            _settings.AvifChroma = (AvifChromaCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "444";
            _settings.RecordQuality = RecordQualitySld.Value;
            _settings.AnimAvifBackendIndex = 0;
            _settings.ArchiveEnabled = ArchiveChk?.IsChecked == true;
            _settings.ArchiveMode = (ArchiveModeCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Month";
            _settings.FirstRun = false;
            _settings.Language = (LanguageCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh";

            // LLM 设置
            _settings.TranslationMode = (TranslationModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Free";
            _settings.UseCustomLlm = _settings.TranslationMode is "LLM" or "Vision";
            _settings.LlmEndpoint = LlmEndpointTxt.Text;
            _settings.LlmApiKey = LlmApiKeyTxt.Text;
            _settings.LlmModel = LlmModelTxt.Text;
            _settings.LlmSystemPrompt = LlmPromptTxt.Text;
            _settings.TargetLanguage = (TargetLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            _settings.OcrLanguage = (OcrLangCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

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

        // ── 格式专属提示 ──
        FormatHintTxt.Text = format switch
        {
            OutputFormat.PNG => "✅ 无损格式，支持 HDR (cICP Rec.2100 PQ)。截图首选。",
            OutputFormat.JPEG_LI => "✅ Google jpegli 编码，butteraugli 距离控制质量。体积小，兼容性最佳。",
            OutputFormat.JPEG_GAINMAP => "✅ Ultra HDR (ISO 21496-1)，兼容 JPEG。SDR/HDR 自适应。",
            OutputFormat.JPEG_XL => "✅ 新一代格式，Modular 模式对截图极优。支持 HDR。",
            OutputFormat.AVIF => "✅ 先进格式，支持 HDR + 硬件加速编码。默认 4:4:4。",
            OutputFormat.WebP => "⚠ 仅 SDR 8-bit。适合简单分享场景。",
            OutputFormat.BMP => "⚠ 无压缩 BMP。仅限特殊用途。",
            _ => ""
        };

        // ── AVIF 专属选项卡片 ──
        bool isAvif = format == OutputFormat.AVIF;
        AvifOptionsCard.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;

        // ── GainMap 专属选项卡片 ──
        bool isGainMap = format == OutputFormat.JPEG_GAINMAP;
        GainMapOptionsCard.Visibility = isGainMap ? Visibility.Visible : Visibility.Collapsed;
        if (isGainMap)
        {
            var gmTag = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rgb";
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

        // Quality 优先使用已保存值
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
    }

    private void OnGainMapModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rgb";
        GainMapHintTxt.Text = tag == "Gray"
            ? "灰度增益：仅编码亮度差，体积最小。黑白文字/图标场景推荐。"
            : "RGB 增益：三通道独立编码，色彩还原最准确。彩色截图推荐。";
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
        if (HdrSwitch.IsOn) _settings.ColorSpaceIndex = 5;
        else _settings.ColorSpaceIndex = 0;
        ColorCbo.SelectedIndex = _settings.ColorSpaceIndex;
    }

    private void OnColorSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings.AcmeDetected)
        {
            IccHintTxt.Text = "ACM 已启用 — Windows 自动管理显示器色彩，ICC 烘焙已禁用。";
            return;
        }
        if (IccBakeSwitch.IsEnabled)
        {
            var tag = GetSelectedColorSpaceTag();
            bool isSRgb = tag is "System" or "sRGB";
            IccHintTxt.Text = isSRgb
                ? "烘焙目标: sRGB（不嵌入 ICC，sRGB 是通用默认）"
                : $"烘焙目标: {ColorProfileProvider.GetColorSpaceDisplayName(tag)}（将嵌入标准 ICC）";
        }
        UpdateGamutMappingUI();
    }

    /// <summary>检测当前显示器色域并显示在 UI 中。</summary>
    private void DetectAndShowSourceGamut()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (primary is not null)
            {
                string csName = primary.ColorSpace switch
                {
                    global::Vortice.DXGI.ColorSpaceType s when (int)s == 12 => "HDR (BT.2020/PQ)",
                    _ => primary.IsHdr ? "HDR (BT.2020)" : $"SDR ({primary.BitsPerColor}bit)",
                };
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

    /// <summary>更新色域映射 UI。</summary>
    private void UpdateGamutMappingUI()
    {
        var sourceTag = SourceGamutTxt.Text;
        var targetTag = (ColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";

        string targetName = targetTag switch
        {
            "System" => sourceTag.Contains("HDR") ? "sRGB" : sourceTag,
            "sRGB" => "sRGB",
            "DisplayP3" => "Display P3",
            "DCI_P3" => "DCI-P3",
            "AdobeRGB" => "Adobe RGB",
            "BT2020" => "BT.2020",
            _ => "sRGB"
        };
        TargetGamutTxt.Text = targetName;

        bool sourceIsWide = sourceTag.Contains("HDR") || sourceTag.Contains("P3") || sourceTag.Contains("BT.2020");
        bool needsMapping = targetTag != "System" || sourceIsWide;
        MappingArrow.Text = needsMapping ? "→ ACES 缩限到" : "→ 直通（同色域）";
        GamutMapHintTxt.Text = needsMapping
            ? $"ACES 影视标准：{sourceTag} → {targetName}，Perceptual 感知缩限"
            : "当前显示器色域与目标一致，无需转换。";
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
        try
        {
            if (bgra is null || bgra.Length != w * h * 4)
            {
                DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "❌ 像素数据无效");
                return;
            }

            var (format, _) = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
            var encoder = EncoderFactory.Create(format);
            var hdrOutput = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
            var settings = BuildEncodingSettings(format, hdrOutput, null);
            var outDir = GetEffectiveOutputDir();
            var path = BuildOutputPath(format, outDir);

            // ── 全部重操作移到后台线程：ICC 烘焙 + BGRA→scRGB + 编码 ──
            var iccBakeEnabled = IccBakeSwitch.IsOn;
            var colorSpaceTag = GetSelectedColorSpaceTag();

            await Task.Run(() =>
            {
                // ICC 烘焙（委托给 CapturePipelineService）
                var (pixels, iccProfile) = CapturePipelineService.PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
                if (iccProfile is not null)
                    settings.IccProfile = iccProfile;

                if (hdrOutput && encoder.SupportsHdr)
                {
                    var hdrFrame = new HdrFrameData
                    {
                        Pixels = BgraToScrgbLinear(pixels, w, h),
                        Width = w, Height = h,
                        IccProfile = settings.IccProfile
                    };
                    encoder.EncodeAsync(hdrFrame, settings, path).GetAwaiter().GetResult();
                }
                else
                {
                    encoder.EncodeSdrAsync(pixels, w, h, settings, path).GetAwaiter().GetResult();
                }
            });

            sw.Stop();

            DispatcherQueue.TryEnqueue(async () =>
            {
                await CopyFileToClipboardAsync(path);
                ToastService.ShowCaptureSuccess(path, sw.ElapsedMilliseconds);
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ 保存失败: {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
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
            var tmpPath = Path.Combine(Path.GetTempPath(), $"ttc_clip_{Guid.NewGuid():N}.png");

            // ── ICC 烘焙 + 编码全部移到后台线程 ──
            var iccBakeEnabled = IccBakeSwitch.IsOn;
            var colorSpaceTag = GetSelectedColorSpaceTag();

            await Task.Run(() =>
            {
                var (pixels, _) = CapturePipelineService.PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
                var encoder = EncoderFactory.Create(OutputFormat.PNG);
                var settings = new EncodingSettings
                {
                    Format = OutputFormat.PNG, Quality = 100, HdrOutput = false,
                    ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
                };
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

    private async void StartSelectionCapture()
    {
        // ── 防重入 ──
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            System.Diagnostics.Trace.WriteLine("[MainWindow] 截图已在进行中，忽略重复触发");
            return;
        }

        try
        {
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

            // WGC 多显示器拼接捕获
            CaptureResult captureResult;
            try
            {
                captureResult = await _wgcService.CaptureAllMonitorsAsync(new WgcCaptureConfig
                {
                    FrameTimeoutMs = 3000
                });
            }
            catch (Exception ex)
            {
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

            sw.Stop();
            System.Diagnostics.Debug.WriteLine(
                $"[⏱ 端到端] 阶段1-WGC捕获: {captureResult.Width}x{captureResult.Height} {sw.ElapsedMilliseconds}ms");

            // 阶段2: 创建选区覆盖层
            sw.Restart();
            var overlay = new SelectionOverlay(desktopPixels, vx, vy, vw, vh);
            overlay.Activate();

            // 测量覆盖层首次渲染（监听 Activated）
            var overlayReady = new TaskCompletionSource<bool>();
            overlay.Activated += (_, _) =>
            {
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[⏱ 端到端] 阶段2-覆盖层激活: {sw.ElapsedMilliseconds}ms");
                System.Diagnostics.Debug.WriteLine($"[⏱ 端到端] 总耗时(热键→覆盖层可见): {sw.ElapsedMilliseconds + (sw.ElapsedMilliseconds > 0 ? 0 : 0)}ms");
                overlayReady.TrySetResult(true);
            };
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

                    // 优先使用标注合成后的像素
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
                }
                catch (Exception ex)
                {
                    // ═══ async void 安全网：未捕获异常会直接终止进程 ═══
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] ActionCompleted 异常: {ex}");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusTxt.Text = $"❌ {ex.Message}";
                        ToastService.ShowCaptureFailed(ex.Message);
                    });
                }
            };

            overlay.ActionCompleted += (_, _) => Interlocked.Exchange(ref _isCapturing, 0);
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusTxt.Text = $"❌ {ex.Message}";
                ToastService.ShowCaptureFailed(ex.Message);
            });
        }
        finally { Interlocked.Exchange(ref _isCapturing, 0); }
    }

    private async Task CaptureAndOcrFromPixelsAsync(byte[] pixels, int w, int h)
    {
        StatusTxt.Text = "📝 文字提取中...";
        try
        {
            // 确保 OCR 引擎已初始化（首次使用或后台未完成时）
            MultiOcrService.Initialize(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models"));

            var ocrLang = _settings.OcrLanguage;
            var result = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(result.Error))
            { StatusTxt.Text = $"❌ OCR: {result.Error}"; return; }
            if (string.IsNullOrWhiteSpace(result.Text) || (result.Lines is null or { Count: 0 }))
            { StatusTxt.Text = "📝 未检测到文字"; return; }

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
        StatusTxt.Text = "🌐 识别并翻译中...";
        try
        {
            // 确保 OCR 引擎已初始化
            MultiOcrService.Initialize(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models"));

            var ocrLang = _settings.OcrLanguage;
            var ocrResult = await MultiOcrService.RecognizeAsync(pixels, w, h, string.IsNullOrEmpty(ocrLang) ? null : ocrLang);

            if (!string.IsNullOrEmpty(ocrResult.Error) || string.IsNullOrWhiteSpace(ocrResult.Text) || (ocrResult.Lines is null or { Count: 0 }))
            {
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

    /// <summary>GDI 捕获指定屏幕坐标区域。w/h 超过 4096 会自动分块。供外部（OnCaptureNow SDR 路径）使用。</summary>
    [Obsolete("已迁移至 WGC。保留仅供向后兼容。")]
    public static byte[]? CaptureViaGdiStatic(int x, int y, int w, int h)
    {
        return CaptureViaGdi(x, y, w, h, true);
    }

    // ── 截图按钮（选区模式） ──

    private void OnCaptureBtn(object sender, RoutedEventArgs e) => StartSelectionCapture();

    /// <summary>"捕获现在" — WGC 单显示器全屏捕获。</summary>
    private async void OnCaptureNow(object sender, RoutedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
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
            var outDir = GetEffectiveOutputDir();

            // ── WGC 单显示器捕获 ──
            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = hdrOutput,
                FrameTimeoutMs = 3000
            });

            bool actualHdr = captureResult.IsHdr;
            int fw = captureResult.Width, fh = captureResult.Height;

            // ── 编码保存 ──
            var meta = captureResult.SourceDisplay is not null
                ? MetadataCollector.Collect(captureResult.SourceDisplay)
                : null;

            var settings = BuildEncodingSettings(format, actualHdr, meta);
            settings.IccProfile ??= captureResult.IccProfile;

            var encoder = EncoderFactory.Create(format);
            var fullPath = BuildOutputPath(format, outDir);

            if (actualHdr && captureResult.HdrPixels is not null)
            {
                await Task.Run(() =>
                    encoder.EncodeAsync(new HdrFrameData
                    {
                        Pixels = captureResult.HdrPixels,
                        Width = fw, Height = fh,
                        IccProfile = captureResult.IccProfile,
                        Metadata = meta
                    }, settings, fullPath));
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null) throw new InvalidOperationException("无法获取显示像素");
                await Task.Run(() => encoder.EncodeSdrAsync(sdrPixels, fw, fh, settings, fullPath));
            }

            sw.Stop();
            await CopyFileToClipboardAsync(fullPath);
            string status = actualHdr
                ? $"✅ HDR 已保存 ({sw.ElapsedMilliseconds}ms)"
                : $"✅ 已保存 ({sw.ElapsedMilliseconds}ms)";
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = status);
            ToastService.ShowCaptureSuccess(fullPath, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = $"❌ {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
        }
        finally { CaptureBtn.IsEnabled = true; Interlocked.Exchange(ref _isCapturing, 0); }
    }

    /// <summary>获取有效的输出目录（含归档子目录）。</summary>
    private string GetEffectiveOutputDir()
    {
        var outDir = PathTxt.Text;
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        if (_settings.ArchiveEnabled) outDir = GetArchivePath(outDir);
        return outDir;
    }

    /// <summary>构建编码设置。</summary>
    private EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta)
    {
        var avifBackend = AvifBackendCbo.SelectedIndex switch
        { 1 => AvifEncoderBackend.LibAom, 2 => AvifEncoderBackend.Qsv, 3 => AvifEncoderBackend.Nvenc, _ => AvifEncoderBackend.Auto };
        return new EncodingSettings
        {
            Format = format,
            Quality = (float)QualitySld.Value,
            HdrOutput = hdrOutput,
            AvifBackend = avifBackend,
            AvifPngSuffix = AvifPngSuffixChk.IsChecked == true,
            AvifChroma = _settings.AvifChroma,
            DisplayBitDepth = _settings.DisplayBitDepth,
            GainMapMode = _settings.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
            Metadata = meta,
            PreferGpuEncode = true,
            ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
        };
    }
    private string BuildOutputPath(OutputFormat format, string outDir)
    {
        var avifPngSuffix = AvifPngSuffixChk.IsChecked == true;
        var ext = format switch
        {
            OutputFormat.JPEG_LI => ".jpg",
            OutputFormat.JPEG_GAINMAP => ".jpg",
            OutputFormat.JPEG_XL => ".jxl",
            OutputFormat.AVIF => ".avif",
            OutputFormat.WebP => ".webp",
            OutputFormat.BMP => ".bmp",
            _ => ".png"
        };
        if (format == OutputFormat.AVIF && avifPngSuffix) ext += ".png";
        return Path.Combine(outDir, $"{PrefixTxt.Text}{DateTime.Now:yyyyMMdd_HHmmssfff}{ext}");
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
            "Cpu" => "PP-OCRv6 (Cpu",
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
        BehaviorTitle.Text = LocaleManager.Behavior;
        AutoStartChk.Content = LocaleManager.AutoStart;
        MinimizeTrayChk.Content = LocaleManager.MinimizeTray;

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

    // ── sRGB 辅助（保留用于色彩管理）──

    private static float[] BgraToScrgbLinear(byte[] bgra, int w, int h)
        => TrueToneCap.Core.PixelOps.BgraToScrgbLinearFast(bgra, w, h);

    // ── GDI 捕获（已过时，仅保留作为最终回退，不推荐使用）──

    [Obsolete("已迁移至 WGC。保留仅供向后兼容。")]
    private static byte[]? CaptureViaGdi(int x, int y, int w, int h, bool fixAlpha = true)
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

            if (fixAlpha)
            {
                for (int i = 3; i < bytes.Length; i += 4)
                    bytes[i] = 0xFF;
            }
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

    // GDI P/Invoke（仅保留作为最终回退）
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdcD, int xD, int yD, int w, int h, nint hdcS, int xS, int yS, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint cLines, byte[]? lpBits, ref BITMAPINFO lpbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    // ── Win32 窗口子类化（托盘消息处理）──
    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);
    private const int GWLP_WNDPROC = -4;

    private nint _originalWndProc;
    private Win32WndProc? _wndProcDelegate;

    private delegate nint Win32WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    private void SubclassWindowForTray(nint hwnd)
    {
        _wndProcDelegate = WndProcHook;
        _originalWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private nint WndProcHook(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 统一消息转发：托盘 + 热键（避免双重子类化冲突）
        _trayIcon?.HandleTrayMessage(msg, lParam);
        HotkeyManager.HandleHotKeyMessage(msg, wParam);
        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
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

public sealed class AppSettingsData
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
    public string AvifChroma { get; set; } = "444"; // 444 / 422 / 420 (截图推荐444)
    public double RecordQuality { get; set; } = 80;
    public int AnimAvifBackendIndex { get; set; }

    // 归档设置
    public bool ArchiveEnabled { get; set; }
    public string ArchiveMode { get; set; } = "Month"; // Year / Month / Day

    // LLM / 翻译设置
    public bool UseCustomLlm { get; set; }
    public string TranslationMode { get; set; } = "Free"; // Free / LLM / Vision
    public string LlmEndpoint { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "deepseek-chat";
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
    public string GainMapMode { get; set; } = "Rgb";
    /// <summary>界面语言: zh=中文, en=English。</summary>
    public string Language { get; set; } = "zh"; // Rgb / Gray
    /// <summary>OCR 引擎选择: Auto / Gpu / Windows / Cpu。</summary>
    public string OcrEngineMode { get; set; } = "Auto";
    /// <summary>主题模式: Default / Light / Dark / OLED。</summary>
    public string ThemeMode { get; set; } = "Default";

}
