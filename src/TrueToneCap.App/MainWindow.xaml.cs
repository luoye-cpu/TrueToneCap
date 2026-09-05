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
using TrueToneCap.Core.Annotation;
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
    private volatile int _isCapturing; // 0=idle, 1=busy (防重入, 仅保护捕获阶段)
    private CancellationTokenSource? _captureCts; // 截图/编码取消令牌
    // ═══ 2026-08-16 P2-6 修复: 记录在途编码任务, 新操作/退出前等待其完成 ═══
    // 旧实现 Cancel+Delay(50) 不保证旧编码写入完成 → 截断文件; 且共享 CTS 会让
    // 标注窗口/OCR 预览的保存与进行中的截图互相 Cancel。
    private Task? _captureTask;
    // ═══ 2026-08-25 发布版修复: 后台持续编码 ═══
    // 旧任务 (如 JXL 24s 编码) 不应阻塞新截图 — 编码在后台持续完成。
    // _pendingEncodeTasks 跟踪所有在途编码任务, 退出时等待全部完成。
    // 各任务独立 CancellationTokenSource (旧任务不被新任务取消)。
    private readonly List<Task> _pendingEncodeTasks = [];
    private readonly object _pendingLock = new();
    private void TrackPendingTask(Task task)
    {
        lock (_pendingLock) _pendingEncodeTasks.Add(task);
        // 任务完成后自动从列表移除 (cleanup, 避免内存泄漏)
        _ = task.ContinueWith(t => { lock (_pendingLock) _pendingEncodeTasks.Remove(t); }, TaskContinuationOptions.ExecuteSynchronously);
    }
    private bool _isExiting;           // 托盘退出标志（跳过最小化）
    private SelectionOverlay? _activeSelectionOverlay;   // 当前活动选区覆盖层（应用退出时强制关闭）
    private Services.HdrCaptureWindow? _activeHdrWindow; // 当前活动 HDR 覆盖窗口（应用退出时强制关闭）
    private AnnotationWindow? _activeAnnotationWindow;   // 当前活动标注窗口（应用退出时强制关闭）
    private OcrPreviewWindow? _activeOcrPreview;         // 当前活动 OCR 预览窗口 (2026-08-16 P3-20 新增)
    private TextBox? _recordingTarget; // 正在录制的快捷键输入框
    private string _hdrSystemHint = ""; // 系统 HDR 状态基础提示 (DetectAndApplySystemCapabilitiesAsync 写入)
    private bool _hdrHardwareSupported; // 硬件是否支持 HDR (能力检测写入, 供格式联动禁用开关)

    // ═══ 2026-08-25 P1 性能优化: StatusTxt 去抖 ═══
    // 多个编码/捕获路径连续 TryEnqueue 状态更新时合并为最后一次, 减少冗余 UI 调度。
    private int _statusDebounceTick;
    private System.Threading.Timer? _statusDebounceTimer;
    private void SetStatus(string text)
    {
        var tick = Interlocked.Increment(ref _statusDebounceTick);
        _statusDebounceTimer?.Dispose();
        _statusDebounceTimer = new System.Threading.Timer(_ =>
        {
            // 过期的定时器回调跳过 (已有更新的状态在等待)
            if (Interlocked.CompareExchange(ref _statusDebounceTick, tick, tick) != tick) return;
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = text);
        }, null, 50, Timeout.Infinite);
    }

    /// <summary>格式能力描述 (2026-08-08 重构: 统一能力表替代硬编码 switch)。</summary>
    private sealed record FormatCapability(
        OutputFormat Format,
        bool SupportsHdr,       // 编码器原生 HDR 支持 (JPEG LI/WebP 不支持)
        bool SupportsWideGamut, // 能否输出广色域 CICP/ICC (GainMap Base 恒 sRGB)
        bool IccEmbeddable,     // 能否嵌入 ICC 元数据
        int[] BitDepths,        // 支持的位深 (null/8 表示固定)
        string[] Chroma,        // 支持的色度采样 (空 = 固定 4:4:4)
        string Hint);           // 格式提示文本

    private static readonly Dictionary<OutputFormat, FormatCapability> s_formatCaps = new()
    {
        [OutputFormat.PNG] = new(OutputFormat.PNG,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8, 10, 12, 16], Chroma: [],
            Hint: "✅ 无损格式，支持 HDR (cICP Rec.2100 PQ) + 广色域 ICC。截图首选。"),
        [OutputFormat.JPEG_GAINMAP] = new(OutputFormat.JPEG_GAINMAP,
            SupportsHdr: true, SupportsWideGamut: false, IccEmbeddable: true,
            BitDepths: [8], Chroma: [],
            Hint: "✅ Ultra HDR (ISO 21496-1)：SDR 查看器显示基础图，HDR 查看器还原完整动态范围。Base 恒为 sRGB。"),
        [OutputFormat.JPEG_LI] = new(OutputFormat.JPEG_LI,
            SupportsHdr: false, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8], Chroma: ["444", "422", "420"],
            Hint: "✅ Google jpegli 编码，butteraugli 距离控制质量。8-bit 不支持 HDR，但支持广色域 (P3/Adobe RGB) ICC 嵌入。"),
        [OutputFormat.JPEG_XL] = new(OutputFormat.JPEG_XL,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8, 10, 12], Chroma: ["444", "420"],
            Hint: "✅ 新一代格式，Modular 模式对截图极优。支持 HDR (Rec.2100 PQ) + 广色域。"),
        [OutputFormat.AVIF] = new(OutputFormat.AVIF,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8, 10, 12], Chroma: ["444", "422", "420"],
            Hint: "✅ 先进格式，支持 HDR + 硬件加速编码。默认 4:4:4。"),
        [OutputFormat.WebP] = new(OutputFormat.WebP,
            SupportsHdr: false, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8], Chroma: ["444", "420"],
            Hint: "⚠ 仅 8-bit，不支持 HDR (但支持广色域 ICC 嵌入)。适合简单分享场景。"),
        [OutputFormat.TIFF] = new(OutputFormat.TIFF,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            BitDepths: [8, 16], Chroma: [],
            Hint: "✅ TIFF 无损格式，支持 16-bit HDR + ICC 嵌入。适合存档。"),
    };

    private static FormatCapability GetFormatCap(OutputFormat fmt)
        => s_formatCaps.TryGetValue(fmt, out var c) ? c : new FormatCapability(fmt, false, false, true, [8], [], "");


    // ── 通过 AppServices 访问共享服务（不再本地持有）──
    private WgcCaptureService? _wgcService => AppServices.Wgc;

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

        SetStatus("能力检测中...");

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
            SetStatus($"⚠ {ex.Message}");
        }
    }

    /// <summary>
    /// 更新 GainMap 卡片的亮度基准提示: 当前系统 SDR 白点 + 行业标准 203 nits +
    /// 对应 Windows 'SDR 内容亮度' 滑块建议值。
    /// 滑块映射 (本机实测 13 点采样, 完美线性): nits = 80 + 4 × 滑块 (0-100 → 80-480 nits,
    /// 每格 4 nits)。经 5→100, 39→236 两次独立采样交叉验证一致。
    /// 203 nits (libultrahdr kSdrWhiteNits, Android/影视生态) → 滑块 31。
    /// 注: 此线性映射为 Windows 11 HDR 滑块常见行为 (80~480 nits), 若个别驱动非线性
    /// 请以应用实时显示的系统 SDR 白点为准微调。
    /// </summary>
    private void UpdateGainMapHint(int sdrWhiteNits)
    {
        if (GainMapHintTxt is null) return;
        const float kIndustryStdNits = 203f; // libultrahdr kSdrWhiteNits
        // 本机实测线性映射: nits = 80 + 4 × 滑块 → 滑块 = (nits - 80) / 4
        int currentSlider = sdrWhiteNits > 80
            ? (int)Math.Round((sdrWhiteNits - 80) / 4.0)
            : 0;
        int targetSlider = (int)Math.Round((kIndustryStdNits - 80) / 4.0); // = 31

        string text = sdrWhiteNits > 0
            ? $"当前系统 SDR 白点: {sdrWhiteNits} nits"
            : "当前系统 SDR 白点: 未检测到 (使用设置值)";
        text += $" | 行业标准: {kIndustryStdNits:0} nits (Android/影视, libultrahdr)";
        text += $"\nWindows 'SDR 内容亮度' 滑块: 当前约 {currentSlider} | 目标 {kIndustryStdNits:0} nits ≈ 滑块 {targetSlider}";
        text += " (实测线性: nits=80+4×滑块, 0-100→80-480)";
        if (sdrWhiteNits > 0 && Math.Abs(sdrWhiteNits - kIndustryStdNits) > 20)
        {
            text += $"\n💡 与行业标准偏差较大: 建议将滑块调到 {targetSlider} (≈{kIndustryStdNits:0} nits), 调完重启应用生效";
        }
        GainMapHintTxt.Text = text;
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
        _settings.SystemMaxNits = cap.DisplayMaxNits;

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
            _hdrHardwareSupported = cap.SupportsHdr;
            // 无论 HDR 当前是否开启，只要硬件支持就允许用户切换 (格式联动在 UpdateQualityPanel)
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
            // 显示系统 SDR 白点 (GainMap 亮度基准) — 帮助用户匹配第三方查看器
            string sdrWhiteText = cap.DisplayPaperWhiteNits > 0
                ? $" | SDR 白点 {cap.DisplayPaperWhiteNits} nits"
                : "";
            // 显示 HDR 峰值亮度 (GainMap headroom 依据, DXGI MaxLuminance)
            string maxNitsText = cap.DisplayMaxNits > 0 && cap.SupportsHdr
                ? $" | 峰值 {cap.DisplayMaxNits} nits"
                : "";
            HdrHintTxt.Text = hdrText + acmText + sdrWhiteText + maxNitsText;
            _hdrSystemHint = hdrText + acmText + sdrWhiteText + maxNitsText; // 供格式 HDR 联动拼接

            // GainMap 亮度基准提示 (SDR 白点 + 行业标准 203 + Windows 滑块建议)
            UpdateGainMapHint(cap.DisplayPaperWhiteNits);

            IccBakeSwitch.IsEnabled = cap.IccBakeAvailable;
            IccBakeSwitch.IsOn = _settings.IccBakeEnabled;
            if (cap.SystemAcm)
                IccHintTxt.Text = "ACM 已启用 — ICC 烘焙可用于输出到非显示器色域（如 BT.2020/P3）。";
            else if (cap.CustomIcc)
                IccHintTxt.Text = $"检测到校色 ICC → 自动烘焙到 {ColorProfileProvider.GetColorSpaceDisplayName(GetSelectedColorSpaceTag())}";
            else
                IccHintTxt.Text = "未检测到校色 ICC；sRGB 目标不嵌入，非 sRGB 嵌入标准 ICC";

            UpdateAvifBackendLabels();

            // 按当前格式刷新 HDR 开关状态 + 联动提示 (格式不支持 HDR 时禁用)
            UpdateQualityPanel();
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

    // 注: 设置相关方法（LoadSettings / ApplySettingsToUI / SetComboByTag / SaveSettings /
    // SaveSettingsQuiet / SyncStartupAndHotkey）已迁移至 MainWindow.Settings.cs
    // （MainWindow.xaml.cs 过大，按职责拆分为多个 partial class 文件）。

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
        // GainMap 也是 butteraugli 距离 (0.5-3.0, 与 JPEG LI 同类型) → 支持小数步进 + 手动输入
        bool precise = format is OutputFormat.JPEG_LI or OutputFormat.JPEG_XL or OutputFormat.JPEG_GAINMAP;
        QualitySld.StepFrequency = precise ? 0.1 : 1.0;
        QualitySld.IsEnabled = format != OutputFormat.PNG;

        // ── 格式专属提示 (2026-08-08: 能力表驱动) ──
        var cap = GetFormatCap(format);
        FormatHintTxt.Text = format == OutputFormat.JPEG_GAINMAP
            ? $"✅ Ultra HDR (ISO 21496-1)。编码基准 = 系统 SDR 白点 {(_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80)} nits。兼容查看器（Chrome/照片）自动对齐；若查看器固定 203 nits，请手动把参考白度调到 {(_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80)}。"
            : cap.Hint;

        // ── HDR 能力联动 (2026-08-08: 格式能力表驱动) ──
        // 系统 HDR 状态提示 + 格式不支持 HDR 时的降级警告 + GainMap 色域说明
        if (HdrHintTxt is not null)
        {
            // 格式不支持 HDR → 禁用 HDR 开关 (所有编码路径均用 IsOn&&IsEnabled 判断 → 自动 SDR 降级)
            // 硬件不支持 → 同样禁用
            HdrSwitch.IsEnabled = _hdrHardwareSupported && cap.SupportsHdr;
            string fmtHdrWarn;
            if (!cap.SupportsHdr)
            {
                // 8-bit 不支持 HDR (PQ 传输), 但支持广色域 SDR (ICC 嵌入)
                fmtHdrWarn = _hdrHardwareSupported
                    ? "\n🔒 当前格式为 8-bit，不支持 HDR 输出 — HDR 开关已禁用。"
                      + (cap.SupportsWideGamut
                          ? " 支持广色域 (P3/Adobe RGB) ICC 嵌入：ICC 烘焙开关开启 + 目标色域选 P3/Adobe RGB 时输出广色域 SDR。"
                          : "")
                      + " 如需 HDR 请选 PNG/JPEG XL/AVIF/TIFF/Gain Map。"
                    : "\n🔒 此显示器不支持 HDR — HDR 开关已禁用。";
            }
            else
            {
                fmtHdrWarn = "";
            }
            string wideWarn = (cap.SupportsHdr && !cap.SupportsWideGamut && HdrSwitch.IsOn)
                ? "\nℹ Gain Map 的 Base 恒为 sRGB (增益图为相对编码)，目标色域设置不影响其像素色域。"
                : "";
            HdrHintTxt.Text = _hdrSystemHint + fmtHdrWarn + wideWarn;
        }

        // ── 格式专属选项卡片 ──
        bool isAvif = format == OutputFormat.AVIF;
        bool isGainMap = format == OutputFormat.JPEG_GAINMAP;

        // ═══ GainMap 色域/HD R 锁定 (2026-08-09) ═══
        // GainMap 管线恒在 scRGB/sRGB 空间 (Base 恒 sRGB, 增益图是相对编码),
        // 目标色域选择 (P3/BT.2020) 对 GainMap 无效 → 锁定为 BT.2020。
        // GainMap 是 Ultra HDR → 锁定 HDR 开启 (不可关)。
        if (isGainMap)
        {
            // ═══ 2026-08-16 P2-7 修复: 锁定只作用于 UI, 不写 _settings ═══
            // 旧实现写 _settings.HdrEnabled=true / _settings.ColorSpaceIndex=5 并持久化,
            // 切走 GainMap 后不恢复 → 用户 HDR-off 偏好被永久翻转 (状态粘住)。
            // 现在仅 UI 锁定: IsEnabled=false + IsOn=true; 保存设置走 UI 值,
            // 切走后 UpdateQualityPanel 恢复真实用户偏好。
            HdrSwitch.IsEnabled = false;
            HdrSwitch.IsOn = true;
            if (ColorCbo is not null)
            {
                ColorCbo.IsEnabled = false;
                // UI 显示 BT.2020 但不改 _settings (GetSelectedColorSpaceTag 读取 UI, 已生效; 保存时由 ApplySettingsToUI 同步)
                if (ColorCbo.SelectedIndex != 5)
                    ColorCbo.SelectedIndex = 5;
            }
            // 更新提示: 色域锁定说明
            if (HdrHintTxt is not null)
            {
                string colorLockHint = "\n🔒 Gain Map 为 Ultra HDR：目标色域恒为 BT.2020 (Base 为 sRGB 兼容层)，HDR 输出已锁定开启。";
                HdrHintTxt.Text = _hdrSystemHint + colorLockHint;
            }
        }
        else
        {
            // 非 GainMap 格式: 恢复 HDR 开关和色域选择可用性
            HdrSwitch.IsEnabled = _hdrHardwareSupported && cap.SupportsHdr;
            if (ColorCbo is not null) ColorCbo.IsEnabled = true;
        }

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
        // HDR 开关影响格式 HDR 联动提示 (GainMap 色域说明) — 刷新当前格式面板
        UpdateQualityPanel();
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
        // 色域变化影响 GainMap 色域说明提示
        UpdateQualityPanel();
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

    // 注: 色彩管理方法（GetDisplayNativeGamut / DetectAndShowSourceGamut /
    // UpdateGamutMappingUI / GetSelectedColorSpaceTag）已迁移至 MainWindow.ColorManagement.cs

    // ── 编码辅助（仅在最终保存/复制时触发）──

    private async Task EncodeAndSaveAsync(byte[] bgra, int w, int h)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("MainWindow", $"开始截图流程: {w}x{h} 格式={_formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Format}");

        // ═══ 2026-08-16 P2-6 修复: 等待旧操作完成 (替代 Cancel+Delay) ═══
        // 新 CTS: 取消旧令牌 (不再共用 token 互相干扰), 等待旧编码任务自然结束。
        _captureCts?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; } catch { /* 旧操作失败不影响新操作 */ }
        }
        _captureCts?.Dispose();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        // ⚠ 本方法运行在 UI 线程：在切入后台编码前采集 UI 状态快照，
        // 后台线程不得访问 WinUI 控件（详见 EncodingUiState 注释）。
        var ui = CaptureEncodingUiState();
        // 记录本次编码任务供后续等待 (自包含, 不引用外层闭包并发的字段)
        var task = ExecuteEncodeSaveAsync(bgra, w, h, sw, ct, ui);
        _captureTask = task;

        // ═══ 2026-08-25 P1 优化: 慢编码进度提示 ═══
        // JPEG XL/libaom AVIF 等 CPU 编码器 4K 可达 0.5-2s, 无反馈会让用户不确定是否成功。
        // 500ms 未完成 → 状态栏显示"编码中"; 编码完成后由 Toast/成功状态覆盖。
        var progressTimer = new System.Threading.Timer(_ =>
        {
            SetStatus($"⏳ 正在编码 {_formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Label} ...");
        }, null, 500, Timeout.Infinite);

        try
        {
            await task;
        }
        finally
        {
            progressTimer.Dispose();
        }
    }

    /// <summary>实际编码执行体 (P2-6 拆分, 便于 _captureTask 跟踪)。</summary>
    private async Task ExecuteEncodeSaveAsync(byte[] bgra, int w, int h,
        System.Diagnostics.Stopwatch sw, CancellationToken ct, EncodingUiState ui)
    {
        try
        {
            if (bgra is null || bgra.Length != w * h * 4)
            {
                LogService.Error("MainWindow", $"像素数据无效: bgra={(bgra is null ? "null" : bgra.Length.ToString())} w={w} h={h}");
                SetStatus("❌ 像素数据无效");
                return;
            }

            // ⚠ 本方法运行在后台线程 —— 全部取值来自 UI 快照，不得访问 WinUI 控件
            var format = ui.Format;
            var hdrOutput = ui.HdrEnabled;
            var iccBakeEnabled = ui.IccBakeEnabled;
            var colorSpaceTag = ui.ColorSpaceTag;
            LogService.Info("MainWindow", $"编码设置: 格式={format} HDR={hdrOutput} ICC烘焙={iccBakeEnabled} 色域={colorSpaceTag}");
            var settings = BuildEncodingSettings(format, hdrOutput, null, ui);

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
        // ═══ 2026-08-25 发布版修复: 后台持续编码模型 ═══
        // 旧: 防重入拦截 + await 旧任务 → 24s JXL 编码阻塞新截图
        // 新: 捕获阶段防重入 (快, <1s), 编码阶段后台跟踪, 不阻塞新截图。
        // 捕获阶段: WGC 捕获 + 像素提取, 完成后立刻释放锁。
        // 编码阶段: 作为后台任务跟踪 (TrackPendingTask), 退出时等待全部完成。
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            // 仍有可能被 StartSelectionCapture 的覆盖层交互阶段阻塞 (<1s 捕获 + 覆盖层)
            // 但不再被 24s 编码阻塞
            LogService.Warn("SilentCapture", "捕获阶段忙，请稍后再试");
            ToastService.ShowCaptureFailed("截图捕获中，请稍后再试");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("SilentCapture", "无感截图启动");

        try
        {
            if (_wgcService is null)
            {
                LogService.Error("SilentCapture", "WGC 捕获服务未初始化");
                return;
            }

            // ⚠ 无感截图由 UI 线程启动：在此采集 UI 状态快照，供后台编码线程使用。
            // 后台线程访问 WinUI 控件会抛异常并被静默吞掉（详见 EncodingUiState 注释）。
            var ui = CaptureEncodingUiState();
            var format = ui.Format;
            var hdrOutput = ui.HdrEnabled;
            bool useFloat16Wide = ShouldUseFloat16ForWideGamut();
            LogService.Info("SilentCapture", $"捕获配置: 格式={format} HDR={hdrOutput} Float16广色域={useFloat16Wide}");

            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = hdrOutput || useFloat16Wide, // 广色域目标也需要 Float16
                FrameTimeoutMs = 3000
            });

            // ═══ P0: 捕获降级给用户明确提示 ═══
            if (!string.IsNullOrEmpty(captureResult.DegradationWarning))
            {
                LogService.Warn("SilentCapture", captureResult.DegradationWarning);
                ToastService.ShowCaptureDegraded(captureResult.DegradationWarning);
            }

            // ═══ 2026-08-25 发布版修复: 编码改为后台任务, 不阻塞新截图 ═══
            // 捕获完成后立即释放 _isCapturing 锁, 编码在后台持续完成。
            // 编码任务加入 _pendingEncodeTasks 供退出时等待。
            var captureResultRef = captureResult; // 捕获引用供闭包使用
            var encodeTask = Task.Run(async () =>
            {
                await Task.Yield();
                await ExecuteSilentEncodeAsync(captureResultRef, sw, hdrOutput, useFloat16Wide, format, ui);
            });
            TrackPendingTask(encodeTask);
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("SilentCapture", "无感截图已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("SilentCapture", $"无感截图失败: {ex.Message}", ex);
            SetStatus($"❌ 无感截图失败: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _isCapturing, 0); }
    }

    /// <summary>无感截图编码执行体 (后台任务, 不阻塞 UI 线程)。</summary>
    private async Task ExecuteSilentEncodeAsync(CaptureResult captureResult, System.Diagnostics.Stopwatch sw,
        bool hdrOutput, bool useFloat16Wide, OutputFormat format, EncodingUiState ui)
    {
        try
        {
            bool actualHdr = captureResult.IsHdr;
            int fw = captureResult.Width, fh = captureResult.Height;
            var meta = captureResult.SourceDisplay is not null
                ? MetadataCollector.Collect(captureResult.SourceDisplay)
                : null;
            // ⚠ 后台线程：取自 UI 快照，不得访问 WinUI 控件
            var colorSpaceTag = ui.ColorSpaceTag;
            var iccBakeEnabled = ui.IccBakeEnabled;
            string path;

            if (actualHdr && captureResult.HdrPixels is not null)
            {
                LogService.Info("SilentCapture", $"HDR 帧捕获成功: {fw}x{fh} {(captureResult.IsHdr ? "HDR" : "SDR")}");
                var settings = BuildEncodingSettings(format, actualHdr, meta, ui);
                if (iccBakeEnabled)
                    settings.IccProfile ??= captureResult.IccProfile;

                if (hdrOutput || format == OutputFormat.JPEG_GAINMAP)
                {
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
                    LogService.Info("SilentCapture", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                    var (sdrPixels, iccProfile) = CapturePipelineService.PrepareFloat16WithIcc(
                        captureResult.HdrPixels, fw, fh, iccBakeEnabled, colorSpaceTag,
                        new ToneMappingParams { Mode = ToneMapMode.SegmentedReinhard, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits), DisplayMaxNits = (_settings.SystemMaxNits > 0 ? _settings.SystemMaxNits : _settings.DisplayMaxNits) });
                    if (iccProfile is not null)
                        settings.IccProfile = iccProfile;
                    settings.HdrOutput = false;
                    path = await AppServices.Pipeline.EncodeAndSaveAsync(
                        sdrPixels, fw, fh, settings, false, colorSpaceTag, CancellationToken.None, null);
                }
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null)
                {
                    LogService.Warn("SilentCapture", "SDR 像素数据为空，跳过保存");
                    return;
                }
                LogService.Info("SilentCapture", $"SDR 帧捕获成功: {fw}x{fh}");

                var settings = BuildEncodingSettings(format, false, meta, ui);
                if (iccBakeEnabled)
                    settings.IccProfile ??= captureResult.IccProfile;
                var gpuTex = iccBakeEnabled ? null : captureResult.GpuTexture;
                path = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, iccBakeEnabled, colorSpaceTag, CancellationToken.None, gpuTex);
            }

            sw.Stop();
            LogService.Info("SilentCapture", $"无感截图完成: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds}ms)");
            await CopyFileToClipboardAsync(path);
            // 后台编码完成后仍需回到 UI 线程显示 Toast
            DispatcherQueue.TryEnqueue(() => ShowSaveToast(path, sw.ElapsedMilliseconds, "silent"));
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("SilentCapture", "无感截图编码已取消");
        }
        catch (Exception ex)
        {
            LogService.Error("SilentCapture", $"无感截图编码失败: {ex.Message}", ex);
        }
        finally
        {
            try { captureResult.Dispose(); } catch { }
        }
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
            return; // ═══ 2026-08-16 P2-9 修复: 自定义 toast 成功后不再发系统通知 (双通知去重) ═══
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"提示窗口创建失败: {ex.Message}");
        }

        // 自定义 toast 创建失败 → 回退系统通知 (备用)
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
            SetStatus($"✅ 已保存并复制: {Path.GetFileName(filePath)}");
        }
        catch
        {
            SetStatus($"✅ 已保存: {Path.GetFileName(filePath)}");
        }
    }

    private async Task EncodeAndCopyAsync(byte[] bgra, int w, int h)
    {
        try
        {
            // ═══ 2026-08-09: 剪贴板复制复用完整保存管线 (与正式保存完全一致的色彩/ICC/ACM 处理) ═══
            // 之前独立实现 ResolveColorSpaceTag(colorSpaceTag, false) 未传 ACM → System 解析错误,
            // 且只编码 PNG 简化路径 → 与最终输出不一致。
            // 现改为: 复用 EncodeAndSaveAsync (BuildEncodingSettings 传 ACM + PreparePixelsWithIcc 传 ACM),
            // 仅输出到临时目录, 然后复制该完整文件。剪贴板得到与最终输出完全一致的成品。
            // UI 线程：采集快照（统一入口，避免散落的控件读取）
            var ui = CaptureEncodingUiState();
            var iccBakeEnabled = ui.IccBakeEnabled;
            var colorSpaceTag = ui.ColorSpaceTag;

            // 用与正式保存相同的设置构建 (含 System→实际色域的 ACM 感知解析)
            var settings = BuildEncodingSettings(ui.Format, ui.HdrEnabled, null, ui);

            // 输出到临时目录 (不写入用户正式输出目录)
            var tmpDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_Clip");
            Directory.CreateDirectory(tmpDir);

            // ═══ 2026-08-16 P2-5 修复: 剪贴板复制接入 CTS (旧为 default) ═══
            var ct = _captureCts?.Token ?? CancellationToken.None;

            var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                bgra, w, h, settings, iccBakeEnabled, colorSpaceTag,
                ct, null, tmpDir);

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetStorageItems(new[] { file });
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            SetStatus("📋 已复制到剪贴板");

            // ═══ 2026-08-16 P2-11 修复: 清理剪贴板临时文件 (旧实现从不清理, %TEMP% 累积) ═══
            // 剪贴板引用 StorageFile, 立刻删除会使剪贴板失效 → 延后 3s 让分配器安全释放句柄。
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                // 删除失败（文件仍被剪贴板占用）会永久残留在 %TEMP%\TrueToneCap_Clip
                catch (Exception ex)
                {
                    LogService.Warn("MainWindow", $"剪贴板临时文件清理失败，将残留: {path} - {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 复制失败: {ex.Message}");
        }
    }

    // ── 选区动作 ──

    /// <summary>
    /// HDR 窗口内联标注合成：有标注层时把标注光栅化到 SDR 区域像素
    /// （与 SDR 路径同用 AnnotationRasterizer，保证输出一致）。
    /// </summary>
    private static byte[]? ComposeAnnotatedSdr(Services.HdrCaptureWindow hdrWnd, byte[]? sdrRegion, int rw, int rh)
    {
        if (hdrWnd.AnnoManager.LayerCount == 0 || sdrRegion is null) return sdrRegion;
        var result = new byte[sdrRegion.Length];
        Buffer.BlockCopy(sdrRegion, 0, result, 0, sdrRegion.Length);
        foreach (var layer in hdrWnd.AnnoManager.Layers.Where(l => l.IsVisible))
            Services.AnnotationRasterizer.RenderLayer(result, rw, rh, layer);
        return result;
    }

    /// <summary>打开独立标注窗口（HDR 路径的"标注"动作：SDR 预览 + 标注 + 保存/复制）。</summary>
    private void OpenAnnotationWindow(byte[] regionPixels, int w, int h)
    {
        try
        {
            var annoWindow = new AnnotationWindow(regionPixels, w, h);
            _activeAnnotationWindow = annoWindow;
            annoWindow.OnSaveRequested = async (pixels, pw, ph) =>
            {
                await EncodeAndSaveAsync(pixels, pw, ph);
            };
            annoWindow.OnCopyRequested = async (pixels, pw, ph) =>
            {
                await EncodeAndCopyAsync(pixels, pw, ph);
            };
            annoWindow.Closed += (_, _) => _activeAnnotationWindow = null;
            annoWindow.Activate();
        }
        catch (Exception ex)
        {
            LogService.Error("MainWindow", $"打开标注窗口失败: {ex.Message}", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                SetStatus($"❌ 标注窗口失败: {ex.Message}");
                ToastService.ShowCaptureFailed(ex.Message);
            });
        }
    }

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
    /// 对齐无感截图路径，传递 ICC、色域标签等参数。
    /// ═══ 2026-08-16 P1-3 修复: 不再接收 gpuTexture — 输入是选区裁剪后的区域像素,
    /// 而 captureResult.GpuTexture 是整块桌面纹理, NVENC 按区域 w/h 编码会取纹理
    /// 左上角相同尺寸区域 → 内容错误。区域裁剪后纹理一律不可用, 走 CPU 像素路径。</summary>
    private async Task EncodeAndSaveHdrAsync(float[] hdrPixels, int w, int h,
        byte[]? iccProfile = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("MainWindow", $"HDR 编码启动: {w}x{h} 格式={_formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Format}");

        // ═══ 2026-08-16 P2-6 修复: 等待旧操作完成 (替代 Cancel+Delay) ═══
        _captureCts?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; } catch { /* 忽略旧任务异常 */ }
        }
        _captureCts?.Dispose();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        try
        {
            // UI 线程：采集快照（统一入口，避免散落的控件读取）
            var ui = CaptureEncodingUiState();
            var format = ui.Format;
            var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
            var meta = MetadataCollector.Collect(DisplayEnumerator.FindDisplayByMonitor(cursorMonitor));
            var hdrOutput = ui.HdrEnabled;
            var iccBakeEnabled = ui.IccBakeEnabled;
            var colorSpaceTag = ui.ColorSpaceTag;
            var settings = BuildEncodingSettings(format, hdrOutput, meta, ui);
            // ═══ 2026-08-16 P1-4 修复: 仅烘焙开启时注入传入的 ICC ═══
            if (iccBakeEnabled)
                settings.IccProfile ??= iccProfile;

            LogService.Info("MainWindow", $"HDR 编码: {format} {w}x{h} HDR={hdrOutput} ICC烘焙={iccBakeEnabled} 色域={colorSpaceTag}");

            // ═══ 2026-08-10 修复: GainMap 格式只要有 HDR 帧就走主路径 ═══
            // (与 SilentCapture 一致: GainMap 需要原始 HDR 帧生成增益图,
            //  与 HDR 开关无关; 旧逻辑走 SDR 降级 → 无增益图)
            if (hdrOutput || format == OutputFormat.JPEG_GAINMAP)
            {
                // HDR 直通编码（同无感截图路径）
                var path = await AppServices.Pipeline.EncodeHdrFrameAsync(
                    new HdrFrameData
                    {
                        Pixels = hdrPixels,
                        Width = w, Height = h,
                        IccProfile = iccProfile,
                        Metadata = meta,
                        GpuTexture = null // P1-3: 选区像素不携带桌面纹理
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
                    new ToneMappingParams { Mode = ToneMapMode.SegmentedReinhard, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits), DisplayMaxNits = (_settings.SystemMaxNits > 0 ? _settings.SystemMaxNits : _settings.DisplayMaxNits) });
                if (iccP is not null)
                    settings.IccProfile = iccP;
                settings.HdrOutput = false;
                var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, w, h, settings, false, colorSpaceTag, ct, null); // P1-3: 选区像素不带纹理

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
        // ═══ 2026-08-25 发布版修复: 拦截时 Toast 反馈 (旧实现仅 Trace, 用户完全无感) ═══
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            LogService.Warn("MainWindow", "截图已在进行中，忽略重复触发");
            ToastService.ShowCaptureFailed("上一次截图仍在处理中，请稍候");
            return;
        }

        bool overlayShown = false;
        try
        {
            LogService.Info("MainWindow", "选区截图启动");
            SetStatus("📷 WGC 捕获桌面...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── 使用 WGC 捕获所有显示器并拼接 ──
            if (_wgcService is null)
            {
                SetStatus("❌ 捕获服务未初始化");
                return;
            }

            // 获取虚拟桌面坐标
            var displays = DisplayEnumerator.EnumerateDisplays();
            int vx = displays.Count > 0 ? displays.Min(d => d.X) : 0;
            int vy = displays.Count > 0 ? displays.Min(d => d.Y) : 0;
            int vw = displays.Count > 0 ? displays.Max(d => d.X + d.Width) - vx : 1920;
            int vh = displays.Count > 0 ? displays.Max(d => d.Y + d.Height) - vy : 1080;

            // WGC 多显示器拼接捕获（SDR 用于预览; 2026-08-16 P2-8 修复: 加 PreferHdr,
            // 使选区路径与 SilentCapture/OnCaptureNow 一致 — 单显示器时能获得 HDR/广色域帧)
            CaptureResult captureResult;
            try
            {
                LogService.Info("MainWindow", "WGC 多显示器拼接捕获启动...");
                bool hdrOutputCap = HdrSwitch.IsOn && HdrSwitch.IsEnabled;
                bool useFloat16WideCap = ShouldUseFloat16ForWideGamut();
                captureResult = await _wgcService.CaptureAllMonitorsAsync(new WgcCaptureConfig
                {
                    FrameTimeoutMs = 3000,
                    PreferHdr = hdrOutputCap || useFloat16WideCap
                });
                LogService.Info("MainWindow", $"WGC 捕获完成: {captureResult.Width}x{captureResult.Height} HDR={captureResult.IsHdr}");

                // ═══ P0: 捕获降级给用户明确提示 ═══
                if (!string.IsNullOrEmpty(captureResult.DegradationWarning))
                {
                    LogService.Warn("MainWindow", captureResult.DegradationWarning);
                    ToastService.ShowCaptureDegraded(captureResult.DegradationWarning);
                }
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
                captureResult.Dispose(); // ═══ 2026-08-16 P1-1: 提前释放 GPU 纹理 ═══
                return;
            }

            // ── HDR 捕获：CaptureAllMonitorsAsync 已在内部分支捕获 HDR（单显示器时）──
            float[]? hdrDesktopPixels = captureResult.HdrPixels;
            int hdrW = captureResult.Width, hdrH = captureResult.Height;
            System.Diagnostics.Debug.WriteLine(
                $"[诊断] captureResult: HDR={(hdrDesktopPixels is not null ? $"len={hdrDesktopPixels.Length} {hdrW}x{hdrH}" : "null")} SDR={(desktopPixels is not null ? $"len={desktopPixels.Length}" : "null")}");

            // ═══ 2026-08-16 P1-1 修复: 释放 GPU 纹理引用 ═══
            // SdrPixels/HdrPixels/IccProfile 是托管数组, Dispose 不影响后续使用;
            // GpuTexture (COM 引用) 在该路径无消费方 (预览窗口用 LoadFrame 像素),
            // 且 P1-3 已禁止选区保存传桌面纹理 → 此处安全释放。
            captureResult.Dispose();

            sw.Stop();
            LogService.Info("MainWindow", $"阶段1 WGC捕获完成: {captureResult.Width}x{captureResult.Height} {sw.ElapsedMilliseconds}ms");

            // ── 诊断开关: TTC_FORCE_SDR=1 强制走 SDR 预览路径 (SelectionOverlay), 用于测试/排查 ──
            bool forceSdr = Environment.GetEnvironmentVariable("TTC_FORCE_SDR") == "1";
            if (forceSdr)
            {
                LogService.Warn("MainWindow", "诊断开关 TTC_FORCE_SDR=1: 强制走 SDR 预览路径");
                hdrDesktopPixels = null;
            }

            // 阶段2: 截图预览窗口
            sw.Restart();

            bool hasHdr = hdrDesktopPixels is not null;

            // ═══ HDR 路径：全 D3D11 原生窗口（scRGB 正确显示）═══
            if (hasHdr)
            {
                StatusTxt.Text = "🖥️ HDR 预览";
                // 独立 D3D11 设备：渲染线程独占 context，不与 WGC 共享设备竞争
                using var hdrWnd = new Services.HdrCaptureWindow();
                _activeHdrWindow = hdrWnd; // 注册：应用退出时强制关闭
                LogService.Info("MainWindow", "HDR 预览窗口初始化...");
                bool initOk = hdrWnd.Initialize(vx, vy, vw, vh);
                LogService.Info("MainWindow", $"HDR 预览窗口初始化 {(initOk ? "成功" : $"失败: {hdrWnd.LastError}")}");

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
                    _activeHdrWindow = null; // 窗口已完成动作并关闭

                    if (action == HdrCaptureAction.Cancel)
                    {
                        if (!_isExiting)
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
                            // 2026-08-11 A方案: 窗口内联标注后, 有标注层时输出带标注的 SDR 图像
                            if (hdrWnd.AnnoManager.LayerCount == 0 && hdrRegion is not null)
                                // ═══ 2026-08-16 P1-3: 选区像素不带桌面纹理 (区域≠纹理内容) ═══
                                await EncodeAndSaveHdrAsync(hdrRegion, rw, rh, captureResult.IccProfile);
                            else if (ComposeAnnotatedSdr(hdrWnd, sdrRegion, rw, rh) is { } outPx1)
                                await EncodeAndSaveAsync(outPx1, rw, rh);
                            break;
                        case HdrCaptureAction.Annotate:
                            // 2026-08-11 A方案: 标注已在窗口内内联完成, 此分支为兼容兜底
                            LogService.Info("MainWindow", "HDR 选区标注（窗口内联，兜底打开独立窗口）");
                            if (sdrRegion is not null)
                                OpenAnnotationWindow(sdrRegion, rw, rh);
                            break;
                        case HdrCaptureAction.Copy:
                            LogService.Info("MainWindow", "HDR 选区复制到剪贴板");
                            if (ComposeAnnotatedSdr(hdrWnd, sdrRegion, rw, rh) is { } outPx2)
                                await EncodeAndCopyAsync(outPx2, rw, rh);
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
            _activeSelectionOverlay = overlay; // 注册：应用退出时强制关闭
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
                        if (!_isExiting)
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
                    _activeSelectionOverlay = null; // 覆盖层已关闭，取消注册
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
        // ═══ 2026-08-16 P2-13 修复: 后台初始化 ONNX 模型 (不阻塞 UI 线程) ═══
        // 启动时已 _ = Task.Run(Initialize), 此处等待其完成; Initialize 内部 _initialized
        // 短路, 重复调用无害。旧实现同步调用在 UI 线程加载模型 → 首次 OCR 明显卡顿。
        await Task.Run(() =>
        {
            var modelDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models");
            MultiOcrService.Initialize(modelDir);
        });
        LogService.Info("MainWindow", "OCR 引擎已就绪");

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
        catch (Exception ex) { LogService.Error("MainWindow", $"OCR 失败: {ex.Message}", ex); StatusTxt.Text = $"❌ {ex.Message}"; }
    }

    private async Task CaptureAndTranslateFromPixelsAsync(byte[] pixels, int w, int h)
    {
        LogService.Info("MainWindow", $"翻译启动: {w}x{h} 语言={_settings.OcrLanguage}");
        StatusTxt.Text = "🌐 识别并翻译中...";
        try
        {
        // ═══ 2026-08-16 P2-13 修复: 后台初始化 ONNX 模型 (不阻塞 UI 线程) ═══
        await Task.Run(() =>
        {
            var modelDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "onnx_models");
            MultiOcrService.Initialize(modelDir);
        });
        LogService.Info("MainWindow", "OCR 引擎已就绪(翻译)");

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
        catch (Exception ex) { LogService.Error("MainWindow", $"翻译识别失败: {ex.Message}", ex); StatusTxt.Text = $"❌ {ex.Message}"; }
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
        // ═══ 2026-08-16 P3-20 修复: 登记 OCR 预览窗口, 应用退出时强制关闭 ═══
        // OcrPreviewWindow 与 AnnotationWindow 同属预览窗口族, 用独立字段跟踪。
        _activeOcrPreview = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_activeOcrPreview, win)) _activeOcrPreview = null; };
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
        // ═══ 2026-08-25 发布版修复: 防重入拦截不再静默 ═══
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            LogService.Warn("MainWindow", "快速捕获已在进行中，忽略重复触发");
            ToastService.ShowCaptureFailed("上一次截图仍在处理中，请稍候");
            return;
        }
        LogService.Info("MainWindow", "快速捕获启动 (单显示器全屏)");
        _captureCts?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* 旧任务超时不阻塞 */ }
        }
        _captureCts?.Dispose();
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

            // UI 线程：采集快照（await 之后若回到非 UI 线程也能安全使用）
            var ui = CaptureEncodingUiState();
            var format = ui.Format;
            var hdrOutput = ui.HdrEnabled;
            bool useFloat16Wide = ShouldUseFloat16ForWideGamut();

            // ── WGC 单显示器捕获 ──
            LogService.Info("MainWindow", $"WGC 单显示器捕获: HDR={hdrOutput} Float16广色域={useFloat16Wide}");
            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = hdrOutput || useFloat16Wide,
                FrameTimeoutMs = 3000
            });

            // ═══ P0: 捕获降级给用户明确提示 ═══
            if (!string.IsNullOrEmpty(captureResult.DegradationWarning))
            {
                LogService.Warn("MainWindow", captureResult.DegradationWarning);
                ToastService.ShowCaptureDegraded(captureResult.DegradationWarning);
            }

            string fullPath;
            bool actualHdr;
            try
            {
            ct.ThrowIfCancellationRequested();
            actualHdr = captureResult.IsHdr;
            int fw = captureResult.Width, fh = captureResult.Height;
            var meta = captureResult.SourceDisplay is not null
                ? MetadataCollector.Collect(captureResult.SourceDisplay)
                : null;
            var colorSpaceTag = ui.ColorSpaceTag;
            var iccBakeEnabled = ui.IccBakeEnabled;

            if (actualHdr && captureResult.HdrPixels is not null)
            {
                LogService.Info("MainWindow", $"HDR 帧捕获: {fw}x{fh}");
                var settings = BuildEncodingSettings(format, actualHdr, meta, ui);
                // ═══ 2026-08-16 P1-4 修复: 仅烘焙开启时注入显示器 ICC (同 SilentCapture) ═══
                if (iccBakeEnabled)
                    settings.IccProfile ??= captureResult.IccProfile;

                // ═══ 2026-08-10 修复: GainMap 格式只要有 HDR 帧就走主路径 ═══
                // (与 SilentCapture 一致: GainMap 需要原始 HDR 帧生成增益图)
                if (hdrOutput || format == OutputFormat.JPEG_GAINMAP)
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
                        new ToneMappingParams { Mode = ToneMapMode.SegmentedReinhard, PaperWhiteNits = (_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : _settings.PaperWhiteNits), DisplayMaxNits = (_settings.SystemMaxNits > 0 ? _settings.SystemMaxNits : _settings.DisplayMaxNits) });
                    if (iccProfile is not null)
                        settings.IccProfile = iccProfile;
                    settings.HdrOutput = false;
                    // ═══ 2026-08-16 P1-2 修复: 派生像素不带 GPU 纹理 (同 SilentCapture) ═══
                    fullPath = await AppServices.Pipeline.EncodeAndSaveAsync(
                        sdrPixels, fw, fh, settings, false, colorSpaceTag, ct, null);
                }
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null) throw new InvalidOperationException("无法获取显示像素");
                LogService.Info("MainWindow", $"SDR 帧捕获: {fw}x{fh}");

                var settings = BuildEncodingSettings(format, false, meta, ui);
                // ═══ 2026-08-16 P1-4 修复: 仅烘焙开启时注入显示器 ICC ═══
                if (iccBakeEnabled)
                    settings.IccProfile ??= captureResult.IccProfile;
                // ═══ 2026-08-16 P1-2 修复: 烘焙开启时像素已转换 ≠ 纹理, 禁用纹理直通 ═══
                var gpuTex = iccBakeEnabled ? null : captureResult.GpuTexture;
                fullPath = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, iccBakeEnabled, colorSpaceTag, ct, gpuTex);
            }
            }
            finally
            {
                // ═══ 2026-08-16 P1-1 修复: 释放 GPU 纹理引用 ═══
                captureResult.Dispose();
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

    /// <summary>编码所需的 UI 状态快照。
    /// <para>
    /// ⚠ 必须在 UI 线程构造（见 <see cref="CaptureEncodingUiState"/>）。
    /// </para>
    /// <para>
    /// 背景：此前后台编码线程直接读取 ComboBox / ToggleSwitch / Slider 等 WinUI 控件。
    /// 跨线程访问 WinUI 依赖属性会抛异常，而 <c>GetSelectedColorSpaceTag()</c> 内的空
    /// <c>catch {}</c> 把异常吞掉后静默返回 "System" —— 用户选了 Display P3 / BT.2020，
    /// 无感截图却输出 System 色域，且界面无任何提示。ICC 烘焙同理被静默关闭。
    /// 改为在 UI 线程一次性快照取值，后台线程只读取不可变快照。
    /// </para>
    /// </summary>
    private sealed record EncodingUiState(
        OutputFormat Format,
        bool HdrEnabled,
        bool IccBakeEnabled,
        string ColorSpaceTag,
        float Quality,
        bool AvifPngSuffix,
        bool JxlPngSuffix,
        AvifEncoderBackend AvifBackend);

    /// <summary>在 UI 线程采集当前编码相关的 UI 状态。仅供 UI 线程调用。</summary>
    private EncodingUiState CaptureEncodingUiState() => new(
        Format: _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)].Format,
        HdrEnabled: HdrSwitch.IsOn && HdrSwitch.IsEnabled,
        IccBakeEnabled: IccBakeSwitch.IsOn,
        ColorSpaceTag: GetSelectedColorSpaceTag(),
        Quality: (float)QualitySld.Value,
        AvifPngSuffix: AvifPngSuffixChk.IsChecked == true,
        JxlPngSuffix: JxlPngSuffixChk.IsChecked == true,
        AvifBackend: AvifBackendCbo.SelectedIndex switch
        {
            1 => AvifEncoderBackend.LibAom,
            2 => AvifEncoderBackend.Qsv,
            3 => AvifEncoderBackend.Nvenc,
            _ => AvifEncoderBackend.Auto,
        });

    /// <summary>构建编码设置（委托给 CapturePipelineService，减少重复逻辑）。
    /// <para><paramref name="ui"/> 必须是 UI 线程采集的快照（见 <see cref="CaptureEncodingUiState"/>）；
    /// 本方法可能运行在后台编码线程，不得直接访问任何 WinUI 控件。</para></summary>
    private EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta,
        EncodingUiState ui)
    {
        var settings = AppServices.Pipeline.BuildEncodingSettings(format, hdrOutput, meta, ui.ColorSpaceTag, _settings.AcmeDetected);
        // 覆盖 UI 特有的设置（全部取自快照，不读控件）
        settings.Quality = ui.Quality;
        LogService.Info("MainWindow",
            $"BuildEncodingSettings: 质量={settings.Quality:F2} 色域={ui.ColorSpaceTag} AVIF后端={ui.AvifBackend}");
        settings.AvifPngSuffix = ui.AvifPngSuffix;
        settings.JxlPngSuffix = ui.JxlPngSuffix;
        settings.AvifBackend = ui.AvifBackend;
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
        UpdateOcrLanguageVisibility(engineType);
        UpdateOcrEngineStatus();
        try { SaveSettingsQuiet(); } catch { }
    }

    // 注: UpdateOcrLanguageVisibility 已迁移至 MainWindow.OcrSettings.cs

    private void OnCategoryChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (!_uiReady) return;
        var tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string ?? "Output";

        PageOutput.Visibility = tag == "Output" ? Visibility.Visible : Visibility.Collapsed;
        PageCapture.Visibility = tag == "Capture" ? Visibility.Visible : Visibility.Collapsed;
        PageAI.Visibility = tag == "AI" ? Visibility.Visible : Visibility.Collapsed;
        PageSystem.Visibility = tag == "System" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;

        // ═══ 2026-08-25 任务6: 页面切换过渡动画 ═══
        var activePage = tag switch
        {
            "Output" => PageOutput,
            "Capture" => PageCapture,
            "AI" => PageAI,
            "System" => PageSystem,
            "Log" => PageLog,
            _ => null
        };
        if (activePage is not null)
            Services.AnimationHelper.PageTransition(activePage);

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
            else if (cb.Name == nameof(JxlPngSuffixChk)) _settings.JxlPngSuffix = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastCaptureChk)) _settings.ToastOnCapture = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastSilentChk)) _settings.ToastOnSilentCapture = cb.IsChecked == true;
            else if (cb.Name == nameof(ToastRecordChk)) _settings.ToastOnRecording = cb.IsChecked == true;
            else if (cb.Name == nameof(AutoStartChk)) _settings.AutoStart = cb.IsChecked == true;
            else if (cb.Name == nameof(MinimizeTrayChk)) _settings.MinimizeToTray = cb.IsChecked == true;
            else if (cb.Name == nameof(UiAnimationsChk)) _settings.EnableUiAnimations = cb.IsChecked == true;
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

    // 注: 字体方法（RestoreFontSelection / PopulateFontComboBox）与本地化方法
    // （ApplyLocale / SetComboItemText）已迁移至 MainWindow.Localization.cs

    private void OnCaptureHotkeyRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(HotkeyTxt);

    // 注: OCR/翻译 UI 方法（UpdateOcrEngineStatus / PopulateOcrLanguages / OnOcrLangChanged /
    // OnLlmProviderChanged / OnLlmModelChanged / OnTranslationModeChanged）
    // 已迁移至 MainWindow.OcrSettings.cs

    private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(RecordHotkeyTxt);

    private void OnSilentHotkeyRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(SilentHotkeyTxt);

    private void OnSaveShortcutRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(SaveShortcutTxt);

    private void OnCancelShortcutRecordClick(object sender, RoutedEventArgs e)
        => StartHotkeyRecording(CancelShortcutTxt);

    /// <summary>解析快捷键字符串为 VirtualKey + 修饰键 (供覆盖层/标注窗口匹配按键)。</summary>
    public static (Windows.System.VirtualKey Key, bool Ctrl, bool Shift, bool Alt)? ParseShortcut(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        bool ctrl = false, shift = false, alt = false;
        Windows.System.VirtualKey? key = null;
        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": ctrl = true; break;
                case "SHIFT": shift = true; break;
                case "ALT": alt = true; break;
                case "WIN": break;
                default:
                    if (!TryMapKey(p, out var vk)) return null;
                    key = vk;
                    break;
            }
        }
        return key is not null ? (key.Value, ctrl, shift, alt) : null;
    }

    private static bool TryMapKey(string name, out Windows.System.VirtualKey key)
    {
        switch (name.ToUpperInvariant())
        {
            case "ESC" or "ESCAPE": key = Windows.System.VirtualKey.Escape; return true;
            case "ENTER": key = Windows.System.VirtualKey.Enter; return true;
            case "SPACE": key = Windows.System.VirtualKey.Space; return true;
            case "TAB": key = Windows.System.VirtualKey.Tab; return true;
            case "DELETE" or "DEL": key = Windows.System.VirtualKey.Delete; return true;
            case "INSERT" or "INS": key = Windows.System.VirtualKey.Insert; return true;
            case "HOME": key = Windows.System.VirtualKey.Home; return true;
            case "END": key = Windows.System.VirtualKey.End; return true;
            case "PAGEUP": key = Windows.System.VirtualKey.PageUp; return true;
            case "PAGEDOWN": key = Windows.System.VirtualKey.PageDown; return true;
            default: break;
        }
        // 字母
        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
        { key = Windows.System.VirtualKey.A + (char.ToUpperInvariant(name[0]) - 'A'); return true; }
        // 数字
        if (name.Length == 1 && char.IsDigit(name[0]))
        { key = Windows.System.VirtualKey.Number0 + (name[0] - '0'); return true; }
        // F1-F12
        if (name.Length <= 3 && name[0] == 'F' &&
            int.TryParse(name[1..], out var fnum) && fnum >= 1 && fnum <= 12)
        { key = Windows.System.VirtualKey.F1 + (fnum - 1); return true; }
        key = default;
        return false;
    }

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
        else if (recordedBox == SaveShortcutTxt)
        {
            _settings.SaveShortcut = result;
        }
        else if (recordedBox == CancelShortcutTxt)
        {
            _settings.CancelShortcut = result;
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
        // ── 应用退出：强制关闭可能仍打开的全屏选区覆盖层（避免残留遮挡屏幕/挂起）──
        try { _activeSelectionOverlay?.Cancel(); } catch { }
        try { _activeHdrWindow?.RequestCancel(); } catch { }
        try { _activeAnnotationWindow?.Close(); } catch { }
        try { _activeOcrPreview?.Close(); } catch { } // ═══ 2026-08-16 P3-20: OCR 预览窗口一并关闭 ═══
        _activeSelectionOverlay = null;
        _activeHdrWindow = null;
        _activeAnnotationWindow = null;
        _activeOcrPreview = null;

        // ═══ 2026-08-16 P2-6 修复: 退出时取消在途编码并短等完成 (防截断文件) ═══
        // ═══ 2026-08-25 发布版修复: 等待全部后台编码任务 (旧任务不再被 Cancel) ═══
        try
        {
            _captureCts?.Cancel();
            // 等待所有后台编码任务完成 (最多 5s, 避免 JXL 24s 卡死退出)
            Task[] pending;
            lock (_pendingLock) { pending = [.. _pendingEncodeTasks]; }
            if (pending.Length > 0)
                Task.WaitAll(pending, TimeSpan.FromSeconds(5));
            if (_captureTask is not null)
                _captureTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* 退出路径不抛异常 */ }
        try { _captureCts?.Dispose(); } catch { }
        _captureCts = null;

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

        // ═══ 单实例激活：第二个实例经 HWND_BROADCAST 发来 ═══
        if (App.ActivateMessage != 0 && msg == App.ActivateMessage)
        {
            ActivateFromOtherInstance();
            return 0;
        }

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

    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。
    /// 在窗口过程（UI 线程）中执行，可直接操作 WinUI 控件。</summary>
    private void ActivateFromOtherInstance()
    {
        try
        {
            LogService.Debug("MainWindow", "收到单实例激活广播");
            DispatcherQueue.TryEnqueue(() =>
            {
                // 最小化/隐藏时先恢复
                if (AppWindow.Presenter is OverlappedPresenter p &&
                    p.State == OverlappedPresenterState.Minimized)
                    p.Restore();
                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("MainWindow", $"单实例激活失败: {ex.Message}");
        }
    }

    // ═══ 2026-08-16 P3-18 修复: 显示器变更信号量去重 (WM_DISPLAYCHANGE 风暴防并发) ═══
    private int _displayChangeRunning;

    /// <summary>显示器配置变更处理：重新检测能力并更新 UI。</summary>
    private async Task HandleDisplayChangeAsync()
    {
        // 并发去重: 前一个还在执行则直接跳过 (旧实现风暴时并发执行, 竞争写 _settings/缓存)
        if (Interlocked.CompareExchange(ref _displayChangeRunning, 1, 0) != 0)
        {
            LogService.Debug("MainWindow", "显示器变更处理已在运行，跳过重复触发");
            return;
        }
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
        finally
        {
            Interlocked.Exchange(ref _displayChangeRunning, 0); // 允许下一次变更处理
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  运行日志面板
    // ═══════════════════════════════════════════════════════════════

    private string _logFilter = "All";
    private string _logCategoryFilter = "All";
    private string _logSearch = "";
    private readonly List<LogEntry> _logEntries = [];
    // 2026-08-11: 显示顺序 + 自动滚动开关
    private bool _logNewestFirst = true;   // 最新在前 (默认)
    private bool _logAutoScroll = true;    // 自动滚动到最新 (默认)

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

        IEnumerable<LogEntry> filtered = _logEntries.AsEnumerable();

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
                e.Tag.Contains(_logSearch, StringComparison.OrdinalIgnoreCase) ||
                e.CallerDisplay.Contains(_logSearch, StringComparison.OrdinalIgnoreCase));

        // 2026-08-11: 最新在前 — 逆序显示 (最新一条在顶部)
        var list = filtered.ToList();
        if (_logNewestFirst)
            list.Reverse();

        bool atBottom = _logAutoScroll; // 记录当前是否应保持在最新位置
        var scrollViewer = FindScrollViewer(LogListView);
        double oldOffset = scrollViewer?.VerticalOffset ?? 0;
        double oldExtent = scrollViewer?.ScrollableHeight ?? 0;
        bool wasAtEnd = oldOffset >= oldExtent - 2; // 之前在底部

        LogListView.ItemsSource = list;

        // 自动滚动: 开关开 且 (之前就在底部 或 首次加载) → 滚到最新位置
        if (_logAutoScroll && scrollViewer is not null && list.Count > 0)
        {
            bool shouldScroll = wasAtEnd || _logNewestFirst; // 最新在前时总是保持顶部
            if (shouldScroll)
                scrollViewer.ChangeView(null, _logNewestFirst ? 0 : scrollViewer.ScrollableHeight, null);
        }
        _ = atBottom;
    }

    /// <summary>查找 ListView 内部的 ScrollViewer。</summary>
    private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnLogOrderChanged(object sender, RoutedEventArgs e)
    {
        if (LogNewestFirstSwitch is null) return;
        _logNewestFirst = LogNewestFirstSwitch.IsOn;
        ApplyLogFilter();
    }

    private void OnLogAutoScrollChanged(object sender, RoutedEventArgs e)
    {
        if (LogAutoScrollSwitch is null) return;
        _logAutoScroll = LogAutoScrollSwitch.IsOn;
        ApplyLogFilter();
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
        // 初始填充（订阅前已产生的日志）
        try
        {
            _logEntries.AddRange(LogService.GetUiEntries());
        }
        catch { }

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

        // 日志页首次打开时刷新（可能未经过实时推送的旧条目）
        NavLog.PointerPressed += (_, _) =>
        {
            if (PageLog.Visibility == Visibility.Visible)
                RefreshLogView();
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
