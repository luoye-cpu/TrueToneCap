// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
using TrueToneCap.Core.Settings;   // FormatOrder：FormatIndex 的持久化契约表（界面格式表必须逐项等于它）
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Metadata;
using TrueToneCap.Core.Annotation;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Logging;   // LogLevel / LogCategory（定义已移到 Core）
using TrueToneCap.App.Models;
using TrueToneCap.Core.Services;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    private AppSettingsData _settings => AppServices.Settings.Current;
    private TrayIconManager? _trayIcon;
    // 顺序就是 AppSettingsData.FormatIndex 的持久化契约（见 Core 的 FormatOrder）。
    // 2026-09-26：这里【不再携带标签】—— 标签只有 LocaleManager.GetFormatLabel 一个来源。
    // 过去这张表带一份中文 Label、本地化时又另抄一份 7 项数组，两份都能改、改一份就错位
    // （详见交接 §54.1：7 个可见项里 5 个"点谁都不是谁"）。
    private readonly List<OutputFormat> _formats;
    private volatile int _isCapturing; // 0=idle, 1=busy (防重入, 仅保护捕获阶段)
    /// <summary>
    /// 当前这一拍截图的日志关联 ID（2026-09-21 接 OpId 时加）。
    /// <para>存成字段而不是只靠 <c>AsyncLocal</c>：热键按下 → 选区 → 用户点保存/OCR/翻译，
    /// 中间隔着好几次彼此独立的 UI 事件回调，执行上下文不会自动跨过它们。
    /// 根上 <c>BeginOp()</c> 记下 ID，后续每个根 <c>ContinueOp(_captureOpId)</c> 续上，
    /// 报障时才能把"这几行属于同一次截图"读出来。</para>
    /// </summary>
    private string? _captureOpId;
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
        _ = task.ContinueWith(t =>
        {
            lock (_pendingLock) _pendingEncodeTasks.Remove(t);
            ScheduleIdleHeapReturn();   // ← 队列空了就顺手把堆段还给 OS
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // ═══ 空闲期把 GC 已回收但没归还的堆段还给系统（2026-09-20 实测）═══
    // 现象：5 张 4K HDR 连拍后私有字节 1475MB → 3157MB，静置 40s 不降，看着像泄漏。
    // 但同一段日志里的托管堆在最后一次回收后从 2232MB 掉回 682MB ⇒ 涨的不是"还被引用的对象"，
    // 而是"GC 收完没 decommit 的已提交段"：工作站 GC 只在内存压力下才主动归还，
    // 而截图工具的日常就是"造 280MB 瞬时分配 → 空闲很久"，永远等不到那次压力回收。
    // 所以主动在空闲点做一次 Aggressive 收集。判据（托管堆 vs 私有字节）写在
    // CapturePipelineService.EncodeHdrFrameAsync 的"内存(起)/内存(终)"两行日志里。
    //
    // ⚠ 三道约束，缺一条就会变成"卡用户下一次截图"：
    //   ① 只在【没有捕获在途 && 没有编码任务在跑】时做；
    //   ② 每轮空闲只做一次（_heapReturnScheduled 自旋门闩）；
    //   ③ 延迟 8s —— 连拍用户的手速远快于此，真正空闲下来才动手。
    //   blocking: true 是有意的：非阻塞时 decommit 还没做完就采样，量不出归还效果；
    //   而此刻已确认没有在途工作，一次全量压缩的百毫秒级停顿落在空闲期。
    private int _heapReturnScheduled;

    private bool IsCaptureBusy()
    {
        if (_isCapturing != 0) return true;   // 字段本身就是 volatile，直接读；套 Volatile.Read 反而触发 CS0420
        lock (_pendingLock) return _pendingEncodeTasks.Count > 0;
    }

    private void ScheduleIdleHeapReturn()
    {
        if (Interlocked.CompareExchange(ref _heapReturnScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                if (IsCaptureBusy()) return;

                const long MB = 1024 * 1024;
                long before = GC.GetTotalMemory(forceFullCollection: false);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true);
                long after = GC.GetTotalMemory(forceFullCollection: false);
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                LogService.Info("内存",
                    $"空闲期归还堆段：托管堆 {before / MB}MB → {after / MB}MB；"
                    + $"私有 {self.PrivateMemorySize64 / MB}MB 工作集 {self.WorkingSet64 / MB}MB");
            }
            catch (Exception ex)
            {
                LogService.Warn("内存", $"空闲期归还失败: {ex.GetType().Name} {ex.Message}");
            }
            finally { Interlocked.Exchange(ref _heapReturnScheduled, 0); }
        });
    }
    private bool _isExiting;           // 托盘退出标志（跳过最小化）
    private SelectionOverlay? _activeSelectionOverlay;   // 当前活动选区覆盖层（应用退出时强制关闭）
    private Services.HdrCaptureWindow? _activeHdrWindow; // 当前活动 HDR 覆盖窗口（应用退出时强制关闭）
    private AnnotationWindow? _activeAnnotationWindow;   // 当前活动标注窗口（应用退出时强制关闭）
    private OcrPreviewWindow? _activeOcrPreview;         // 当前活动 OCR 预览窗口 (2026-08-16 P3-20 新增)
    private TextBox? _recordingTarget; // 正在录制的快捷键输入框
    private string _hdrSystemHint = ""; // 系统 HDR 状态基础提示 (DetectAndApplySystemCapabilitiesAsync 写入)
    private bool _hdrHardwareSupported; // 硬件是否支持 HDR (能力检测写入, 供格式联动禁用开关)

    // ═══ 2026-09-17：AVIF 选项「按能力暴露」═══
    // 编码器：HDR 时硬件两项（NVENC / QSV）被隐藏，记住用户原来的选择，关掉 HDR 后恢复，
    // 避免"开一次 HDR 就把用户挑的 NVENC 永久改成 Auto"（SaveSettings 会无条件从 UI 回写）。
    private string? _avifEncoderHiddenByHdr;
    // 防重入：改选会触发 SelectionChanged → UpdateQualityPanel → 再次进入本方法。
    private bool _avifOptionsUpdating;
    // ═══ 2026-09-18：AVIF 实测能力探测的"在途"集合 ═══
    // 探测要真编码若干张样张（可能起 avifenc 子进程），而 UpdateQualityPanel 会被
    // 切格式 / 切编码器 / 切 HDR / 能力检测反复调用 ⇒ 必须保证同 kind 只起飞一次。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<AvifEncoderKind, bool> _capsProbing = new();

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
        bool SupportsWideGamut, // 能否输出广色域 CICP/ICC (GainMap 见 §45：显式选 BT.2020 时整条增益图搬进 2020)
        bool IccEmbeddable,     // 能否嵌入 ICC 元数据
        // 2026-09-19 清理：原 BitDepths / Chroma 两个数组已删除。二者全仓零读取（纯写不读），
        //   且内容与实测结论冲突：WebP 行写着 ["444","420"]，而有损 WebP 格式定义就是 4:2:0
        //   （同一样张只改色度，产物字节完全相同 —— 交接 §27）。留着会让人以为界面下拉的合法集合
        //   由这张表决定，实际各格式的可用组合在 UpdateAvifOptionAvailability / 各编码路径里。
        string Hint);           // 格式提示文本

    private static readonly Dictionary<OutputFormat, FormatCapability> s_formatCaps = new()
    {
        [OutputFormat.PNG] = new(OutputFormat.PNG,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "✅ 无损格式，支持 HDR (cICP Rec.2100 PQ) + 广色域 ICC。截图首选。"),
        [OutputFormat.JPEG_GAINMAP] = new(OutputFormat.JPEG_GAINMAP,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "✅ Ultra HDR (ISO 21496-1)：SDR 查看器显示基础图，HDR 查看器还原完整动态范围。目标色域就是【底图】的色彩空间：Rec.709 或显式 Rec.2020（三件一起搬，底图本身仍是 SDR）。"),
        [OutputFormat.JPEG_LI] = new(OutputFormat.JPEG_LI,
            SupportsHdr: false, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "✅ Google jpegli 编码，butteraugli 距离控制质量。8-bit、容器没有 HDR 通道；默认目标色域是 Rec.709 —— 「跟随显示器」即便解析成广色域也不改变这一点，只有亲手选 Rec.2020 才会输出 Rec.2020。Rec.2020 目标靠 ICC 声明（实测产物带 APP2/ICC），Rec.709 目标则【什么都不写】（靠 JFIF 默认）。"),
        [OutputFormat.JPEG_XL] = new(OutputFormat.JPEG_XL,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "✅ 新一代格式，Modular 模式对截图极优。支持 HDR (Rec.2100 PQ) + 广色域。16-bit 走 16-bit PNG 中转（cjxl 无 -b 参数，sBIT=16 声明实际精度）。"),
        [OutputFormat.AVIF] = new(OutputFormat.AVIF,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            // ═══ 2026-09-18 更新：位深与色度的实际能力取决于所选编码器，提示必须说清边界 ═══
            // · 色度：软件 420/422/444；硬件（NVENC / QSV）AV1 仅 420。
            // · 位深：软件 ≤12-bit（avifenc --depth）；NVENC ≤10-bit（真 10-bit 输入）；
            //   QSV 8-bit（NV12 输入）。HDR 恒 10-bit。
            // · 真精度：>8-bit 只在「源为 Float16 + 编码器支持真 10-bit 输入」时保留源精度，
            //   否则是容器级升位深（交接 §4.3）。
            Hint: "✅ 先进格式，支持 HDR（PQ）与广色域 SDR（Rec.2020 原色 + sRGB 传输，colr(nclx) 与 ICC 双标记）。NVENC 可硬件加速（AV1 仅 4:2:0），软件编码器支持 4:4:4 与 12-bit。位深与色度按所选编码器实际能力生效，不支持的组合已隐藏。"),
        [OutputFormat.WebP] = new(OutputFormat.WebP,
            SupportsHdr: false, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "⚠ 仅 8-bit、容器没有 HDR 通道；默认目标色域是 Rec.709（不写声明）—— 「跟随显示器」即便解析成广色域也不改变这一点，只有亲手选 Rec.2020 才会输出 Rec.2020（靠 ICC 声明）。适合简单分享场景。"),
        [OutputFormat.TIFF] = new(OutputFormat.TIFF,
            SupportsHdr: true, SupportsWideGamut: true, IccEmbeddable: true,
            Hint: "✅ TIFF 无损格式，支持 16-bit HDR + ICC 嵌入。适合存档。"),
    };

    private static FormatCapability GetFormatCap(OutputFormat fmt)
        => s_formatCaps.TryGetValue(fmt, out var c) ? c : new FormatCapability(fmt, false, false, true, "");


    // ── 通过 AppServices 访问共享服务（不再本地持有）──
    private WgcCaptureService? _wgcService => AppServices.Wgc;

    public MainWindow(bool isAutostart = false)
    {
        this.InitializeComponent();

        // XAML 写死中文的按表翻译要在【可视树真的建起来】之后再扫一次：ApplyLocale() 在构造期跑，
        // 那时页面还没实化，VisualTreeHelper 走不到 ⇒ 在根元素 Loaded 后补扫一次，
        // 避免"英文模式静默半套"（扫不到就是扫不到，不能当已经翻译了）。
        HookLocaleSweep(Content as Microsoft.UI.Xaml.FrameworkElement);

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

        // 项序 = FormatIndex 的持久化契约；增删或换序必须同步 Core 的 FormatOrder
        // （那里有旧表→新表的一次性重映射），否则老配置的裸索引会静默指向别的格式。
        // 2026-09-26：摘掉「JPEG Gain Map (HDR)」，它改成 JPEG LI 面板里的开关（见 GainMapEnabled）。
        _formats =
        [
            OutputFormat.PNG,
            OutputFormat.JPEG_LI,
            OutputFormat.JPEG_XL,
            OutputFormat.AVIF,
            OutputFormat.WebP,
            OutputFormat.TIFF,
        ];

        RebuildFormatCombo();
        LoadSettings();             // 仅加载配置文件，不做检测

        // ═══ 关键修复: ApplySettingsToUI 期间禁止事件覆盖 _settings ═══
        _uiReady = false;
        ApplySettingsToUI();        // 将配置反映到 UI
        _uiReady = true;

        // 同步窗口主题到 UI（启动时 ApplyTheme 已设置 Application.RequestedTheme，
        // 但窗口内容元素 fe.RequestedTheme 需要单独设置才能生效）
        SyncWindowTheme();
        UpdateQualityPanel();

        // ── 应用初始语言：必须排在【任何一句界面文字被取出来】之前 ──
        // 原先它在下面 OCR 初始化之后才跑，于是 :288 那句 SetStatus 取到的是
        // LocaleManager.CapabilityDetect 的【中文那一支】，之后再没人重刷 ⇒
        // 英文模式下状态栏启动那几秒一直是「能力检测中...」（15:23 UIA 实测抓到）。
        // 挪到这里即可：LocaleManager.LanguageChanged 全仓零订阅，早发一次没有副作用。
        LocaleManager.SetLanguage(_settings.Language == "en" ? AppLanguage.English : AppLanguage.Chinese);

        SetStatus(LocaleManager.CapabilityDetect);   // 原先直写中文，绕过了表里已有的一条

        _trayIcon = new TrayIconManager(this);
        _trayIcon.OnCaptureHotkey = () => DispatcherQueue.TryEnqueue(() => StartSelectionCapture());
        _trayIcon.OnExitApp = () => _isExiting = true;
        _trayIcon.RegisterCaptureHotkey(_settings.Hotkey);
        ApplyVSCapGate();   // 「VS-Cap 模式」关着就不该抢注全局 Ctrl+Alt+T（见 MainWindow.VSCap.cs）

        // 注册无感截图热键
        HotkeyManager.RegisterNamed(this, "silent", _settings.SilentHotkey,
            () => DispatcherQueue.TryEnqueue(() => SilentCapture()),
            ["Ctrl+Alt+Q", "Alt+Shift+Q"]);

        // ── 子类化窗口过程以处理托盘消息 ──
        SubclassWindowForTray(hwnd);

        // 开机自启动：这里【只读不写】。
        // 旧写法是启动时 StartupManager.IsEnabled = _settings.AutoStart —— 那是一次注册表【写】，
        // 而 GetAppPath() 取 Environment.ProcessPath，于是"最后启动过的那个 exe"就成了自启动项：
        //   · AutoStart=false 的任何一个 dev 构建起来一次，就把用户已注册的自启动【删掉】；
        //   · AutoStart=true 时则把它改指到那个构建的输出目录（10-02 实测就是这样被改的）。
        // 写这件事只该发生在用户真的动过那个开关之后 —— 那一条在 SyncStartupAndHotkey() 里，
        // 由「保存设置」触发且失败会显式告知。启动路径改成只做一致性检查并报警。
        try
        {
            bool registered = StartupManager.IsEnabled;
            if (registered != _settings.AutoStart)
                LogService.Warn("Startup",
                    $"开机自启动的实际状态（{(registered ? "已注册" : "未注册")}）与设置里的 "
                    + $"AutoStart={_settings.AutoStart} 不一致；本进程不自动改写注册表"
                    + "（否则会随最后启动的那个构建漂移），请在设置页按一次「保存设置」以对齐");
        }
        catch (Exception ex) { LogService.Warn("Startup", $"读取开机自启动状态失败: {ex.Message}"); }

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
        // 注入通知位置提供器：应用内浮层需要知道用户选的是四角中的哪一个
        // （SettingsService.Current 是实例属性，静态的 ToastService 拿不到，故用委托解耦）
        ToastService.PositionProvider = () => _settings.ToastPosition;

        // ── 初始化 OCR 引擎（后台加载 ONNX 模型，不阻塞窗口显示）──
        // 模型目录自动解析: 应用内嵌 data/Models/ → 用户 %LOCALAPPDATA%
        try
        {
            var modelDir = TrueToneCap.Core.Services.OnnxOcrEngine.ResolveModelDir();
            _ = Task.Run(() => MultiOcrService.Initialize(modelDir));
            LogService.Info("MainWindow", $"OCR 引擎初始化已后台启动, 模型目录: {modelDir}", LogCategory.OCR);
        }
        catch (Exception ex)
        {
            LogService.Warn("MainWindow", $"OCR 初始化失败: {ex.Message}", LogCategory.OCR);
        }

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
            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = LocaleManager.Ready);
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
        _settings.SystemSdrWhiteLevel = cap.DisplayPaperWhiteNits;
        _settings.SystemMaxNits = cap.DisplayMaxNits;

        // 色彩管理自 2026-09-22 起【不再有开关】：默认把像素映射进标准色域（sRGB / BT.2020），
        // 所以下面首运行时既不设也不推导"烘焙开关"。ACM 只保证显示器正确显示，不影响截图输出色域。

        if (_settings.FirstRun)
        {
            _settings.FirstRun = false;
            // HDR 硬件支持时默认开启 HDR 模式（即使 Windows HDR 当前未开启）
            _settings.HdrEnabled = cap.SupportsHdr;
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
                    ? Trf("✅ HDR 已启用（{0}-bit）", cap.DisplayBitDepth)
                    : LocaleManager.Tr("⚠ HDR 硬件支持但未开启（Windows 显示设置中未开启 HDR）");
            }
            else
            {
                hdrText = LocaleManager.Tr("⚠ 此显示器不支持 HDR");
            }
            var acmText = cap.SystemAcm ? LocaleManager.Tr(" | ACM 已启用（系统管理显示器色彩）") : "";
            // 显示系统 SDR 白点 (GainMap 亮度基准) — 帮助用户匹配第三方查看器
            string sdrWhiteText = cap.DisplayPaperWhiteNits > 0
                ? Trf(" | SDR 白点 {0} nits", cap.DisplayPaperWhiteNits)
                : "";
            // 显示 HDR 峰值亮度 (GainMap headroom 依据, DXGI MaxLuminance)
            string maxNitsText = cap.DisplayMaxNits > 0 && cap.SupportsHdr
                ? Trf(" | 峰值 {0} nits", cap.DisplayMaxNits)
                : "";
            HdrHintTxt.Text = hdrText + acmText + sdrWhiteText + maxNitsText;
            _hdrSystemHint = hdrText + acmText + sdrWhiteText + maxNitsText; // 供格式 HDR 联动拼接

            // GainMap 亮度基准提示 (SDR 白点 + 行业标准 203 + Windows 滑块建议)
            UpdateGainMapHint(cap.DisplayPaperWhiteNits);

            // 色彩管理不可关 ⇒ 这里只报"会做什么"，不再有开关与"是否可用"的置灰逻辑。
            if (cap.SystemAcm)
                IccHintTxt.Text = LocaleManager.Tr("ACM 已启用 — 像素会映射到所选目标色域（Rec.709 / Rec.2020）。");
            else if (cap.CustomIcc)
                IccHintTxt.Text = Trf("检测到校色 ICC → 映射到 {0}", ColorProfileProvider.GetColorSpaceDisplayName(GetSelectedColorSpaceTag()));
            else
                IccHintTxt.Text = LocaleManager.Tr("未检测到校色 ICC；目标色域即像素被搬进、容器被声明成的那一套原色（逐格式的声明通道见下方「色域映射」）");

            UpdateAvifEncoderLabels();

            // 按当前格式刷新 HDR 开关状态 + 联动提示 (格式不支持 HDR 时禁用)
            UpdateQualityPanel();
        });
    }

    private void UpdateQualityPanelForAvifHardware()
    {
        // Auto 模式：本机任一硬件 AV1 编码器可用即视为走硬件；手动模式按所选种类判断。
        var kind = AvifEncoderCapabilities.Parse(
            (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto");
        // ═══ 2026-09-19（用户决定）：Auto 现在【按色度过滤】⇒ 必须问解析结果 ═══
        // 请求 422/444 时 Auto 会选 libaom（硬件给不出广色度），此时仍是软件编码。
        // 这里走 PickAvailableKind 而不是自己写"色度==420 且有硬件"——
        // 后者会变成 Auto 规则的第二处事实源，改一处漏一处（本节上方那段历史就是这么来的）。
        var resolved = kind == AvifEncoderKind.Auto
            ? AvifEncoderSelector.PickAvailableKind(_settings.ChromaAvif, _settings.BitDepthAvif)
            : kind;

        // CRF 下限只有一个事实源：能力表。NvEncoderNative 写 constQP 前问的是同一张表，
        // 所以"界面划不到 0"与"管线送不出 0"不会各走各的（settings.json 遗留值走后者）。
        QualitySld.Minimum = Math.Max(QualitySld.Minimum,
                                      AvifEncoderCapabilities.MinCrf(resolved));
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

    /// <summary>
    /// 质量面板的程序性刷新入口 —— 整段过程<b>不得回写设置</b>（判据见 <see cref="_qualityPanelUpdating"/>）。
    /// </summary>
    private void UpdateQualityPanel()
    {
        _qualityPanelUpdating = true;
        try { UpdateQualityPanelCore(); }
        finally { _qualityPanelUpdating = false; }
    }

    private void UpdateQualityPanelCore()
    {
        if (FormatCbo.SelectedIndex < 0) return;
        var format = _formats[FormatCbo.SelectedIndex];
        var encoder = EncoderFactory.Create(format);
        var (min, max, def, scale) = encoder.GetQualityRange();

        QualityLabel.Text = ScaleText(scale);
        QualitySld.Minimum = min;
        QualitySld.Maximum = max;
        QualitySld.SmallChange = 0.1;
        QualitySld.LargeChange = 0.5;
        // GainMap 也是 butteraugli 距离 (0.5-3.0, 与 JPEG LI 同类型) → 支持小数步进 + 手动输入
        bool precise = format is OutputFormat.JPEG_LI or OutputFormat.JPEG_XL or OutputFormat.JPEG_GAINMAP;
        QualitySld.StepFrequency = precise ? 0.1 : 1.0;

        // 无损格式没有"质量"这个参数（区间退化成一点）⇒ 整行收起，而不是留一根
        // 划不动的滑块 + 只能填 100 的输入框。判据问 Core 的 QualityIsFixed，
        // 不在此点名格式：旧写法 `format != OutputFormat.PNG` 就漏掉了同样是 (100,100) 的 TIFF。
        bool noQualityKnob = encoder.QualityIsFixed;
        QualityRow.Visibility = noQualityKnob ? Visibility.Collapsed : Visibility.Visible;
        QualitySld.IsEnabled = !noQualityKnob;

        // ── 格式专属提示 (2026-08-08: 能力表驱动) ──
        var cap = GetFormatCap(format);
        // 翻译只能发生在这里，不能发生在 s_formatCaps 的声明处：那张表是 static readonly
        // + 编译期常量，在声明处翻会把语言冻结在类型初始化那一刻，而它与 SetLanguage() 的先后没有保证。
        // Tr() 未命中就原样返回 ⇒ 中文模式下这两条与改动前逐字相同。
        int sdrWhite = _settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80;
        FormatHintTxt.Text = CurrentFormat() == OutputFormat.JPEG_GAINMAP
            ? LocaleManager.Tr("✅ Ultra HDR (ISO 21496-1)。编码基准 = 系统 SDR 白点 ") + sdrWhite
              + LocaleManager.Tr(" nits。兼容查看器（Chrome/照片）自动对齐；若查看器固定 203 nits，请手动把参考白度调到 ") + sdrWhite + "。"
            : LocaleManager.Tr(cap.Hint);

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
                // 这个格式没有 HDR 通道 ⇒ 开关置灰。措辞只说【这一档真实能做到什么】：
                // 可选目标色域 2026-09-19 起只剩 sRGB / BT.2020（P3、Adobe RGB 已从界面移除），
                // 而"ICC 烘焙开关"2026-09-22 也删了 —— 原先那句还在教用户去开一个不存在的开关、
                // 选一个选不到的色域。"Gain Map"现在也不是格式而是 JPEG LI 下的开关，不能列进"换格式"的建议里。
                // ⚠ 2026-10-06：原来那句"广色域目标仍然生效"已不准确 —— JPEG 与 WebP 的默认档
                //   从这轮起就是 Rec.709（Core 的 ColorDecision.DefaultsToRec709），「跟随显示器」
                //   解析成广色域【不会】让它们输出 Rec.2020。所以这里必须把条件说全：
                //   宽色域只在你亲手选 Rec.2020 时才生效。
                fmtHdrWarn = _hdrHardwareSupported
                    ? "\n🔒 当前格式没有 HDR 通道（容器写不了 PQ）— HDR 开关已禁用，产物是 8-bit SDR。"
                      + (cap.SupportsWideGamut
                          ? " Rec.2020 目标仍然生效（这类格式只靠 ICC 声明）—— 但只有亲手选 Rec.2020 才会输出 Rec.2020，默认档「跟随显示器」一律按 Rec.709 输出。"
                          : "")
                      + " 要 HDR 请选 PNG / JPEG XL / AVIF / TIFF，或在 JPEG 下打开「附带增益图」。"
                    : "\n🔒 此显示器不支持 HDR — HDR 开关已禁用。";
            }
            else
            {
                fmtHdrWarn = "";
            }
            // 原 wideWarn（"GainMap 的 Base 恒为 sRGB，目标色域不影响像素"）已删 —— §45/§46 后该句为假，
            // 且它是全表唯一的 SupportsHdr && !SupportsWideGamut 组合，条件恒假即死分支。
            HdrHintTxt.Text = _hdrSystemHint + fmtHdrWarn;
        }

        // ── 格式专属选项卡片 ──
        bool isAvif = format == OutputFormat.AVIF;
        // 2026-09-26（用户口径）：GainMap 不再是独立格式，而是 JPEG LI 卡片里的一个开关。
        // 判据只有一个来源（_settings.GainMapEnabled）；编码侧的格式翻译在 CurrentFormat() 里做，
        // 两处各判一次就会出现"界面按开关显示、产物却是另一种格式"的对不上账。
        bool isJpegLi = format == OutputFormat.JPEG_LI;
        bool gainMapOn = isJpegLi && _settings.GainMapEnabled;

        // 开关本身只在 JPEG LI 下出现；增益图专属的两行（说明 + 模式）只在开关打开时出现。
        // IsChecked 是程序赋值 —— OnGainMapToggled 里靠 _qualityPanelUpdating 同一手法挡住回写。
        if (GainMapEnableChk is not null)
        {
            GainMapEnableChk.Visibility = isJpegLi ? Visibility.Visible : Visibility.Collapsed;
            GainMapEnableChk.IsChecked = _settings.GainMapEnabled;
        }
        bool showGm = gainMapOn;
        if (GainMapOptionsDesc is not null) GainMapOptionsDesc.Visibility = showGm ? Visibility.Visible : Visibility.Collapsed;
        if (GainMapModeRow is not null) GainMapModeRow.Visibility = showGm ? Visibility.Visible : Visibility.Collapsed;
        if (GainMapHintTxt is not null) GainMapHintTxt.Visibility = showGm ? Visibility.Visible : Visibility.Collapsed;

        // ═══ 2026-09-27：这里曾经加过"当前格式没有 HDR 通道 ⇒ 把 Rec.2020 那一档禁选" ═══
        // 撤掉了，因为实测把前提打掉了：JPEG LI 与 WebP 确实【没有】HDR 通道（SupportsHdr=false，
        // HDR 开关会落空成 8-bit SDR），但它们都有一条能用的【广色域 SDR】声明通道 ——
        // 取 artifacts/precision/cicp 那组产品自己产出的样张读字节：
        //   cc_webp_bt2020.webp  2644 B，ICCP 块在偏移 30，里面 2100 字节 profile（acsp 在 74）
        //   cc_webp_srgb.webp     524 B，无 ICCP（sRGB 目标本来就不该写，这一格是对照组）
        //   cc_jpegli_bt2020.jpg 2949 B，APP2/ICC_PROFILE + acsp 在 56
        //   cc_jpegli_srgb.jpg    833 B，无 ICC
        // ⇒ 把 Rec.2020 对这两个格式禁掉，砍掉的是"实测成立的能力"，还会与三档策略里
        //   "广色域面板 ⇒ BT2020 + SDR" 那条已拍板的口径正面冲突（自动档照样会解析成 2020，
        //   于是变成"界面不让你选、产物却在做"的第三种不一致）。
        // 描述不清的部分改到文案里去解决（FormatColorFacts 的 JPEG LI / WebP 两行现在明说
        // "没有 HDR 通道 ≠ 不能声明 Rec.2020 原色"）。

        // ═══ 增益图态的 HDR 锁定 (2026-08-09 起)；色域锁定已于 2026-09-23 解除 ═══
        // 增益图就是 Ultra HDR ⇒ HDR 开关必须锁住开启（关掉就没有增益图，这个形态不成立）。
        // 但【色域不再锁定】：JpegGainMapEncoder 真的读 ColorSpaceTag ——
        //   Rec.709 ⇒ base/增益比/声明三件同在 Rec.709 原色；显式 Rec.2020 ⇒ 整条增益图搬进 2020。
        //   这正是"色彩空间影响的是底图的色彩空间"，合并成开关后这条语义原样保留。
        // 锁定仍只作用于 UI、不写 _settings（2026-08-16 P2-7 的教训：状态粘住会永久翻转用户偏好）。
        if (gainMapOn)
        {
            HdrSwitch.IsEnabled = false;
            HdrSwitch.IsOn = true;
            if (ColorCbo is not null) ColorCbo.IsEnabled = true;
            if (HdrHintTxt is not null)
            {
                HdrHintTxt.Text = _hdrSystemHint
                    + "\n🔒 增益图为 Ultra HDR：HDR 输出锁定开启。上面「目标色域」决定底图与增益图共用哪套原色"
                    + " —— 默认档 Rec.709（「跟随显示器」解析成广色域也一样）⇒ 两者同在 Rec.709 原色；"
                    + "只有亲手选 Rec.2020，才是底图、增益比、声明三件同搬 Rec.2020。"
                    + "底图本身恒为 SDR，HDR 由增益图还原，不写在底图的声明里。";
            }
        }
        else
        {
            HdrSwitch.IsEnabled = _hdrHardwareSupported && cap.SupportsHdr;
            if (ColorCbo is not null) ColorCbo.IsEnabled = true;
        }

        // ═══ 色彩页那张卡跟着面板一起刷：HDR 开关刚被上面的格式联动改过（禁用/锁定），
        // 卡片读的正是编码路径用的同一个谓词，晚一步刷新就会显示"HDR 直通"而产物是 SDR。
        // 放在这里是【唯一】一处：切格式、切增益图开关、切 HDR、显示器变更都先经过本函数，
        // 不必在每个事件处理器里各记一遍（历史上漏的就是 OnGainMapToggled —— 开关一开，
        // 目标行还按 JPEG LI 说话，而产物已经是底图 + 增益图）。═══
        // ═══ 色彩页那张卡跟着面板一起刷，而且是【唯一】一处刷新点 ═══
        // 这张卡要说的是"HDR 开关 + 目标色域 + 当前格式 ⇒ 产物长什么样"，而上面刚把 HDR 的
        // 可用性与增益图锁定的最终状态定下来（置灰、锁开）。放在它前面就会读到被覆盖前的值；
        // 放进各 handler 则要求每个改这三样控件的入口都记得调用 —— 增益图开关正是这样漏掉过一次。
        // 卡片读的正是编码路径用的同一个谓词，晚一步刷新就会显示"HDR 直通"而产物是 SDR。
        RefreshColorMappingUi();

        PngOptionsCard.Visibility = format == OutputFormat.PNG ? Visibility.Visible : Visibility.Collapsed;
        JpegXlOptionsCard.Visibility = format == OutputFormat.JPEG_XL ? Visibility.Visible : Visibility.Collapsed;
        JpegLiOptionsCard.Visibility = isJpegLi ? Visibility.Visible : Visibility.Collapsed;
        WebPOptionsCard.Visibility = format == OutputFormat.WebP ? Visibility.Visible : Visibility.Collapsed;
        TiffOptionsCard.Visibility = format == OutputFormat.TIFF ? Visibility.Visible : Visibility.Collapsed;
        // ═══ 2026-09-27 修回归：这一行在 cde6168（0.3.5-beta 发布提交）里被删掉了 ═══
        // `AvifOptionsCard` 在 XAML 里是 Visibility="Collapsed"，全仓【只有这里】会点亮它，
        // 于是 AVIF 的「编码器 / 色度采样 / .png 后缀」三行在任何状态下都不再出现，
        // 只有后来拆出去的 AvifBitDepthCard 还亮着 —— 用户看到的就是"AVIF 没有编码器可选"。
        // 门禁现在有一条锁：以 Collapsed 起步的 *Card 必须能在代码里找到它的 .Visibility 赋值。
        AvifOptionsCard.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        // P1-3: AVIF 位深选项卡片（与 AvifOptionsCard 联动）
        if (AvifBitDepthCard is not null)
            AvifBitDepthCard.Visibility = isAvif ? Visibility.Visible : Visibility.Collapsed;
        if (gainMapOn && GainMapHintTxt is not null)
        {
            var gmTag = (GainMapModeCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray";
            GainMapHintTxt.Text = gmTag == "Gray"
                ? "灰度增益：仅编码亮度差，体积最小。黑白文字/图标场景推荐。"
                : "RGB 增益：三通道独立编码，色彩还原最准确。彩色截图推荐。";
        }

        // AVIF + NVENC/QSV 不支持 CRF=0 无损
        if (isAvif)
        {
            // ═══ 2026-09-16 重构：按单一「AVIF 编码器」下拉判断硬件路径（NVENC/QSV 不支持无损）═══
            UpdateQualityPanelForAvifHardware();
            // ═══ 2026-09-17：切格式 / 切 HDR 后刷新色度、位深可用性 ═══
            // 放在此处而非分支外：HdrSwitch.IsEnabled 已在上面按当前格式更新完毕。
            UpdateAvifOptionAvailability();
            // ═══ 2026-09-18：后台实测该编码器的能力，回来再刷一次（下拉按实测值收敛）═══
            RefreshAvifMeasuredCaps();
        }

        // Quality 优先使用该格式已保存值（每格式独立，切换格式不互相覆盖）
        double savedQ = _settings.GetQuality(CurrentFormat());
        double useQ = (savedQ >= QualitySld.Minimum && savedQ <= QualitySld.Maximum) ? savedQ : def;
        QualitySld.Value = useQ;
        QualityLbl.Text = useQ.ToString("F1");
        QualityTxt.Text = useQ.ToString("F1");
        QualityTxt.Visibility = precise ? Visibility.Visible : Visibility.Collapsed;
        // 长质量描述放到独立整行（避免被窄列遮挡）。写入点只有一个：SetQualityDescription，
        // 它对无损格式给 range.Label、对有损格式给 GetQualityDescription。
        SetQualityDescription(encoder, useQ);
        QualityDescTxt.Visibility = Visibility.Visible;
    }

    // ═══ 2026-09-16 重构：单一「AVIF 编码器」下拉（Auto/Qsv/LibAom/SvtAv1/Nvenc；
    //     Avifenc 于 2026-09-24 降为内部值，不再是用户可选项）═══
    // Auto 由能力表硬件优先挑选；手动选择尊重用户。原「编码后端 + 软件编码器」两层已合并。
    private void OnAvifEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        _settings.AvifEncoder = tag;
        // 软件编码器（libaom/svt）由 SelectByKind 按调用解析（P2-5 修复：
        // 不再写全局静态 NativeAvifEncoder.Codec，避免并发编码任务互相覆盖）。
        UpdateAvifEncoderLabels();
        // ═══ 2026-09-17：换编码器 ⇒ 色度 / 位深的可用组合随之变化，必须同步刷新 ═══
        UpdateAvifOptionAvailability();
        UpdateQualityPanel();
        AppServices.Settings.SaveQuiet();
    }

    /// <summary>更新「AVIF 编码器」下拉项：标记本机可用性，并动态显示该编码器能力（色度/位深）。</summary>
    /// <summary>
    /// 编码器下拉的项文字：<b>只有名字</b>，外加"本机不可用"这一个状态标记。
    /// <para>2026-09-25（用户口径）：原先这里把整张规格表拼进项文字
    /// （<c>libaom · 420/422/444 / SDR ≤12-bit · HDR 10-bit · 真 10-bit 输入（实测）✓</c>），
    /// 在窄下拉里必然截断，而且这些信息灰字 <c>AvifCodecHintTxt</c> 本来就按当前所选编码器动态给了一份
    /// （可用色度 / 可用位深 / 实测 vs 声明 / 真 10-bit 输入 / 不可用原因）
    /// —— 同一件事写两处就一定会漂，用户在下拉里看到的只是第三份缩写。</para>
    /// <para>Auto 项刻意跳过：能力表里没有 Auto 这一档，<c>AvifEncoderCapabilities.Caps</c> 查不到时
    /// 会回落成 libaom 的能力，照名字改会把「自动」变成「libaom」。Auto 的显示名就是 XAML 里的「自动」。</para>
    /// </summary>
    private void UpdateAvifEncoderLabels()
    {
        if (AvifCodecCbo is null) return;
        foreach (ComboBoxItem item in AvifCodecCbo.Items)
        {
            if (item.Tag as string is not { } tag || tag == "Auto") continue;
            var kind = AvifEncoderCapabilities.Parse(tag);
            string name = AvifEncoderCapabilities.Caps(kind).DisplayName;
            // 名字与状态后缀都是中文成品串（前者来自 Core 的能力表）⇒ 英文模式下要过一层 CoreText，
            // 否则英文界面会出现 "libaom（本机不可用）" 这种半中半英。中文模式原样返回。
            item.Content = CoreText(name, CoreAvifBackendTexts)
                + (AvifEncoderSelector.IsKindAvailable(kind)
                    ? "" : CoreText("（本机不可用）", CoreAvifBackendTexts));
        }
    }

    /// <summary>
    /// 该编码器是否支持【真 10-bit 输入】（决定 >8-bit 位深是否真的保留源精度）。
    /// <para>直接查后端实例上的 <c>IAvifEncoder.SupportsTrue10BitInput</c>，UI 不另立一份
    /// "谁支持"的清单（能力表与实际实现一旦分叉，UI 就会先撒谎）。
    /// 只对具体种类查询：<c>Nvenc</c> 返回的是缓存单例、无探测开销；<c>Auto</c> 不查，
    /// 否则会在 UI 线程触发硬件可用性探测。</para>
    /// </summary>
    private static bool SupportsTrue10BitInput(AvifEncoderKind kind)
        => kind is AvifEncoderKind.Nvenc
           && AvifEncoderSelector.SelectByKind(kind).SupportsTrue10BitInput;

    // ═══ 2026-09-17 联动（第二版）：按能力"暴露"，而不是"置灰" ═══
    // 与上一版（item.IsEnabled = false）的区别：不支持的组合直接从下拉里消失，
    // 用户根本点不到，而不是"能点到但变灰、截图那一刻才降级提示"（事后告知 → 事前阻止）。
    //
    // 三条暴露规则：
    //   · 色度：Caps(kind).Supports{420,422,444}。硬件（NVENC / QSV）只有 420 ⇒ 422/444 隐藏。
    //   · 位深：HDR 恒 10-bit（软件 avifenc --depth 10 硬编码；NVENC HDR 直通同为 10-bit）；
    //     SDR 按 SdrBitDepthFor（软件 10/12；NVENC 实测 10，QSV 8）。
    //   · 编码器：HDR 时隐藏不支持 HDR 路径的种类 —— 2026-09-18 起**只剩 QSV**
    //     （NV12 8-bit 输入，喂不进 PQ 像素）。NVENC 的 HDR 10-bit 直通已接通，不再隐藏；
    //     判断统一走 AvifEncoderCapabilities.SupportsHdrPath，不要在本方法里按"是否硬件"另判一次。
    //
    // ⚠ 选中项被隐藏时必须【先改选、后隐藏】并同步写 _settings：
    //   选项消失了却还留着旧选择 ⇒ SaveSettings 会把 SelectedItem 为 null 兜底成
    //   "Auto" / 10 / 444，用户的设置被静默改写（与「色度单一事实源」同型缺陷）。
    private void UpdateAvifOptionAvailability()
    {
        if (!_uiReady || _avifOptionsUpdating) return;
        if (FormatCbo.SelectedIndex < 0 || FormatCbo.SelectedIndex >= _formats.Count) return;
        if (_formats[FormatCbo.SelectedIndex] != OutputFormat.AVIF) return;

        _avifOptionsUpdating = true;
        try
        {
            bool hdr = HdrSwitch.IsOn && HdrSwitch.IsEnabled;

            // ── ① 编码器下拉：HDR 时隐藏不支持 HDR 路径的种类（现只剩 QSV），并记住原选择 ──
            //    以便关 HDR 后恢复，避免"开一次 HDR 就把用户挑的 QSV 永久改成 Auto"。
            if (AvifCodecCbo is not null)
            {
                AvifCodecCbo.IsEnabled = true;
                string curEncTag = (AvifCodecCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
                // ═══ 2026-09-17 修复：旧记录在用户改选后必须作废 ═══
                // 旧实现只在"当前项是硬件"时写入 _avifEncoderHiddenByHdr，永不清除。
                // 于是用户在 HDR 下改选 Auto / libaom（它们支持 HDR）后，字段里仍是旧的硬件名；
                // 一关 HDR 就被无条件"恢复"回那个用户早已放弃的硬件编码器 —— 静默改回用户的选择。
                // 现在只要当前选择支持 HDR，就清除记录（用户已主动选了别的）。
                if (hdr)
                {
                    if (AvifEncoderCapabilities.SupportsHdrPath(AvifEncoderCapabilities.Parse(curEncTag)))
                        _avifEncoderHiddenByHdr = null;
                    else
                        _avifEncoderHiddenByHdr = curEncTag;
                }

                SetItemsVisible(AvifCodecCbo, item => !hdr
                    || AvifEncoderCapabilities.SupportsHdrPath(
                        AvifEncoderCapabilities.Parse(item.Tag as string ?? "Auto")));

                if (!hdr && _avifEncoderHiddenByHdr is { Length: > 0 } restore)
                {
                    _avifEncoderHiddenByHdr = null;
                    foreach (ComboBoxItem item in AvifCodecCbo.Items)
                        if (item.Tag as string == restore) { item.IsSelected = true; break; }
                }
            }

            // 编码器可能刚被改选 ⇒ 必须重新读一次 kind
            var kind = AvifEncoderCapabilities.Parse(
                (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto");

            // ── ② 色度：按能力暴露 ──
            if (AvifChromaCbo is not null)
            {
                AvifChromaCbo.IsEnabled = true;
                var (chromaChanged, chromaTag) = SetItemsVisible(AvifChromaCbo,
                    item => ChromaSupported(kind, item.Tag as string ?? "420"));
                if (chromaChanged && chromaTag.Length > 0)
                    _settings.ChromaAvif = chromaTag;   // 选项已消失 ⇒ 必须同步，否则被兜底成 420/444
            }

            // ── ③ 位深：HDR 恒 10；SDR 按能力 ──
            if (BdAvifCbo is not null)
            {
                BdAvifCbo.IsEnabled = true;
                var (depthChanged, depthTag) = SetItemsVisible(BdAvifCbo, item =>
                    DepthSupported(kind, int.TryParse(item.Tag as string, out var bd) ? bd : 8, hdr));
                if (depthChanged && int.TryParse(depthTag, out var nbd))
                    _settings.BitDepthAvif = nbd;
            }

            // ── ④ 提示：说明当前实际可用的组合 ──
            if (AvifCodecHintTxt is null) return;
            string chromas = string.Join(" / ", new[] { "420", "422", "444" }.Where(c => ChromaSupported(kind, c)));
            string depths = string.Join(" / ", new[] { 8, 10, 12 }
                .Where(d => DepthSupported(kind, d, hdr)).Select(d => $"{d}-bit"));

            // ═══ 2026-09-18 修正：位深"是不是真的"取决于两件事，缺一不可 ═══
            //   ① 源：只有 Float16（HDR / 高级颜色采集）才带 >8-bit 精度；普通 8-bit 截图
            //      升位深不产生信息（交接 §4.3 Case B）。
            //   ② 编码器：只有支持【真 10-bit 输入】的后端（当前 NVENC）能把源精度送进编码器；
            //      其余后端的 10/12-bit 是容器级升位深 —— 文件按所选位深声明，源精度仍 8-bit。
            // 两者都满足才谈得上"真高位深"，提示必须如实区分，否则就是新一轮"看起来生效了"。
            if (hdr)
            {
                // HDR 不再是"恒走 avifenc"：NVENC 已接通 HDR 10-bit 直通（PQ 10-bit 输入 +
                // 容器 colr 声明 PQ，AV1 仅 4:2:0），失败自动回退 avifenc 软件路径；
                // 不支持 HDR 路径的只剩 QSV（NV12 8-bit 输入），已在下拉中隐藏。
                bool nvencHdr = kind is AvifEncoderKind.Nvenc or AvifEncoderKind.Auto;
                AvifCodecHintTxt.Text =
                    $"🔒 HDR 至少 10-bit（PQ 的分层要求），该编码器 HDR 可用位深：{depths}；其余档位已隐藏。"
                  + (nvencHdr ? " NVENC 可硬件直通（AV1 仅 4:2:0），失败自动回退 avifenc 软件路径。" : "")
                  + " QSV 不支持 HDR 已隐藏。"
                  + $" 可用色度 {chromas}。";
            }
            else if (kind == AvifEncoderKind.Auto)
            {
                // 自动模式下"会挑中谁"只有运行时才知道 —— 实测过就把解析结果直接告诉用户，
                // 否则用户看到的可用组合只是"所有编码器的并集"，并不知道实际会走哪一条。
                var m = AvifEncoderProbe.Get(kind);
                string pick = m.Measured && m.ResolvedKind is { } r
                    ? $"本机实测会解析为 {AvifEncoderCapabilities.Caps(r).DisplayName}"
                    : "按 NVENC → QSV → libaom → SVT-AV1 → avifenc 挑第一个可用的编码器（硬件优先；软件兜底取实测更通用且更快的 libaom，见能力表 PickAuto 注释）";
                AvifCodecHintTxt.Text =
                    $"自动模式：{pick}；"
                  + $"可用位深 {depths}，可用色度 {chromas}。挑中硬件时超出其能力的参数会在截图时降级并提示。"
                  + " >8-bit 仅在「源为 Float16」且「挑中的编码器支持真 10-bit 输入」时保留源精度，"
                  + "否则是容器级升位深；普通 8-bit 截图恒输出 8-bit（升位深不产生信息）。";
            }
            else
            {
                var caps = AvifEncoderCapabilities.Caps(kind);
                var m = AvifEncoderProbe.Get(kind);
                string truth = (m.Measured ? m.True10BitInput : SupportsTrue10BitInput(kind))
                    ? "支持真 10-bit 输入 ⇒ Float16 源可保留 >8-bit 精度。"
                    : ">8-bit 为容器级升位深：文件按所选位深声明，源精度仍 8-bit。";
                // ═══ 实测 vs 声明必须说清 ═══
                // 可用组合来自实测（真编码 + 解析产物 av1C）时是"这台机器上确认过"；
                // 后台还没跑完时来自静态能力表，必须显式告诉用户这是"按能力表"，不是实测。
                string src = m.Measured
                    ? "（以下为**本机实测**：请求该组合后解析产物确认真的产出）"
                    : "（以下按静态能力表，后台实测完成后会自动收敛）";
                AvifCodecHintTxt.Text =
                    // 软件/硬件 与"只有该编码器才有的限制"（UiNote）原来都塞在下拉项文字里，
                    // 2026-09-25 起归到这条灰字行 —— 见 UpdateAvifEncoderLabels 的理由。
                    $"{caps.DisplayName}（{(caps.IsHardware ? "硬件加速" : "软件编码")}）："
                  + (caps.UiNote.Length > 0 ? caps.UiNote + " " : "")
                  + $"可用色度 {chromas}，可用位深 {depths}。{truth}{src}"
                  + " 普通 8-bit 截图恒输出 8-bit。"
                  // ═══ "不可用"必须给出可行动的原因 ═══
                  // SVT-AV1 尤其典型：本机内嵌 avifenc 未编译 svt 时，它既不可用、
                  // 也无法实测 —— 只写"不可用"用户会以为是自己配置错了。
                  + (AvifEncoderSelector.IsKindAvailable(kind)
                        ? ""
                        : " ⚠ 本机不可用"
                          + (kind == AvifEncoderKind.SvtAv1
                             && NativeAvifEncoder.SvtUnavailableReason is { } why
                                ? $"（{why}）"
                                : "")
                          + "，截图时将自动回退并提示。");
            }
        }
        finally { _avifOptionsUpdating = false; }
    }

    /// <summary>
    /// 该色度是否被当前编码器支持。
    /// <para>【实测优先】：<see cref="AvifEncoderProbe"/> 真编码过并解析了产物 av1C 时，
    /// 以实测结果为准（静态表只是"文档说它能"，实测才是"它真产出了"）。
    /// 未实测（后台还没跑完 / 后端不可用）时回落静态能力表 ——
    /// ⚠ 回落方向必须是"按静态表放行"，不能当"不支持"，否则会把可用选项误隐藏。</para>
    /// <para>Auto 一律视为支持（具体编码器运行时才定）。</para>
    /// </summary>
    private static bool ChromaSupported(AvifEncoderKind kind, string chroma)
    {
        var m = AvifEncoderProbe.Get(kind);
        if (m.Measured) return m.SupportsChroma(chroma);
        if (kind == AvifEncoderKind.Auto) return true;
        var c = AvifEncoderCapabilities.Caps(kind);
        return chroma switch
        {
            "444" => c.Supports444,
            "422" => c.Supports422,
            "420" => c.Supports420,
            _ => false,
        };
    }

    /// <summary>
    /// ═══ 2026-09-18：实测能力探测是重操作（真编码 + 可能起 avifenc 子进程）═══
    /// 只能在后台跑一次；完成后回 UI 线程刷新下拉与提示。
    /// <c>_capsProbing</c> 保证同 kind 只起飞一次（UpdateQualityPanel 会被频繁调用）。
    /// </summary>
    private void RefreshAvifMeasuredCaps()
    {
        if (FormatCbo.SelectedIndex < 0 || FormatCbo.SelectedIndex >= _formats.Count) return;
        if (_formats[FormatCbo.SelectedIndex] != OutputFormat.AVIF) return;

        var kind = AvifEncoderCapabilities.Parse(
            (AvifCodecCbo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto");
        if (AvifEncoderProbe.IsMeasured(kind)) return;
        if (!_capsProbing.TryAdd(kind, true)) return;   // 已有在途探测

        _ = Task.Run(() =>
        {
            try { AvifEncoderProbe.EnsureMeasured(kind); }
            finally { _capsProbing.TryRemove(kind, out _); }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_uiReady) return;
                UpdateAvifEncoderLabels();
                UpdateAvifOptionAvailability();
            });
        });
    }

    /// <summary>
    /// 该位深在当前场景（HDR / SDR）下是否【真的会产出】。实测优先，未实测回落静态能力表。
    /// </summary>
    private static bool DepthSupported(AvifEncoderKind kind, int bitDepth, bool hdr)
    {
        var m = AvifEncoderProbe.Get(kind);
        if (hdr)
        {
            // HDR：10-bit 是**下限**（PQ 的分层要求），上限按编码器 —— libaom 可选 12-bit，
            // 硬件（NVENC / SVT）到 10。2026-09-25 前这里写成 `bitDepth == HdrBitDepth`，
            // 于是能力表明明给 libaom 声明了 12，下拉里 12-bit 却永远被隐藏。
            // 实测过则再要求：该编码器支持 HDR 路径，且上限取实测与表里的较小者。
            int ceil = AvifEncoderCapabilities.HdrBitDepthFor(kind);
            if (m.Measured)
                return m.HdrPath
                    && bitDepth >= AvifEncoderCapabilities.HdrBitDepth
                    && bitDepth <= Math.Max(AvifEncoderCapabilities.HdrBitDepth,
                                            Math.Min(ceil, m.MaxBitDepth));
            return bitDepth >= AvifEncoderCapabilities.HdrBitDepth && bitDepth <= ceil;
        }
        // SDR：实测值优先；否则按能力表（libaom 12、svt/avifenc/NVENC 10、QSV 8）
        if (m.Measured) return bitDepth <= m.MaxBitDepth;
        if (kind == AvifEncoderKind.Auto) return bitDepth <= AvifEncoderCapabilities.SdrMaxBitDepth;
        return bitDepth <= AvifEncoderCapabilities.SdrBitDepthFor(kind);
    }

    /// <summary>
    /// 按谓词隐藏（<see cref="Visibility.Collapsed"/>）/ 显示（Visible）ComboBox 的项。
    /// <para>若当前选中项即将被隐藏 ⇒ <b>先</b>改选到第一个可见项（此时它仍是 Visible，
    /// <c>IsSelected</c> 一定生效），<b>再</b>统一应用可见性 —— 避免"选中项处于折叠态"
    /// 这一 WinUI 未定义行为，也避免 SelectedItem 变 null 后被 SaveSettings 兜底成默认值。</para>
    /// </summary>
    /// <returns>(是否发生改选, 改选后的 Tag)；未改选时返回当前选中项的 Tag。</returns>
    private static (bool Changed, string Tag) SetItemsVisible(ComboBox cbo, Func<ComboBoxItem, bool> isVisible)
    {
        var visible = new List<bool>(cbo.Items.Count);
        ComboBoxItem? fallback = null;
        foreach (ComboBoxItem item in cbo.Items)
        {
            bool v = isVisible(item);
            visible.Add(v);
            if (v) fallback ??= item;      // 保持 XAML 声明顺序，取第一个可见项
        }

        var cur = cbo.SelectedItem as ComboBoxItem;
        int curIdx = cbo.SelectedIndex;
        bool curWillHide = cur is null || curIdx < 0 || (curIdx < visible.Count && !visible[curIdx]);
        bool changed = false;
        string tag = cur?.Tag as string ?? "";
        if (curWillHide && fallback is not null)
        {
            fallback.IsSelected = true;
            changed = true;
            tag = fallback.Tag as string ?? "";
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var item = (ComboBoxItem)cbo.Items[i];
            item.Visibility = visible[i] ? Visibility.Visible : Visibility.Collapsed;
            item.IsEnabled = true;   // 清掉上一版"置灰"逻辑可能残留的 IsEnabled=false
        }
        return (changed, tag);
    }

    /// <summary>
    /// 增益图开关 —— 2026-09-26 起 GainMap 唯一的入口（它不再是输出格式下拉里的一项）。
    /// <para>⚠ 必须挡程序赋值：<c>UpdateQualityPanelCore</c> 每次刷新都会回写 <c>IsChecked</c>，
    /// 而 WinUI 的 CheckBox 在程序改它时同样抛 Checked/Unchecked。不挡就是 §49 那条缺陷的复现
    /// —— 程序赋值被当成用户意图写进设置（那次是滑块，这次是复选框，同一个机制）。</para>
    /// </summary>
    private void OnGainMapToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _qualityPanelUpdating) return;
        _settings.GainMapEnabled = GainMapEnableChk.IsChecked == true;
        AppServices.Settings.SaveQuiet();
        // HDR 锁定、增益图那两行的可见性、提示文案都随之变化 —— 走同一个面板刷新入口，
        // 不在这里另写一遍可见性判断（那会变成第二处事实源）。
        UpdateQualityPanel();
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
        // _qualityPanelUpdating：程序性赋值（启动 / 切格式 / 切 HDR / 切编码器）不是用户意图，
        // 不回写设置 —— 否则 UpdateQualityPanel 前半段的钳制结果会被后半段当成用户输入读回去。
        if (!_uiReady || _qualityPanelUpdating || FormatCbo.SelectedIndex < 0) return;
        var format = _formats[FormatCbo.SelectedIndex];
        var encoder = EncoderFactory.Create(format);
        double val = Math.Round(QualitySld.Value, 1);
        QualityLbl.Text = val.ToString("F1");
        QualityTxt.Text = val.ToString("F1");
        SetQualityDescription(encoder, val);
        // 保存到当前格式的独立质量字段（切换格式不互相覆盖）
        _settings.SetQuality(CurrentFormat(), val);
        AppServices.Settings.SaveQuiet();
    }

    /// <summary>
    /// 质量文案的<b>措辞与数值格式化在 App 这一层</b>。Core 只回答"是哪一种刻度 / 是哪一类读数"
    /// （<see cref="QualityScale"/> 与 <see cref="QualityDesc"/>），不再吐中文成品串。
    /// <para>为什么要有这一处：本地化表（<see cref="LocaleManager.ZhToEn"/>）只存在于 App，
    /// 而过去 Core 直接把 "质量 (50-100; 85+ 视觉无损)"、<c>距离: {q:F1}</c> 这类用户可见文案
    /// 拼好再交给界面 ⇒ App 只能往回翻，带数值的那两条连整串匹配都做不到、只能按片段换。
    /// 反向哨兵 <c>Test_QualityTexts_CoreStringsAreAllCovered</c> 现在扫的是
    /// Core 那两个方法的函数体：再出现中文字面量就红并点名。</para>
    /// <para>中文模式与改造前<b>逐字相同</b>：数值仍用 CurrentCulture 格式化、精度仍是 F1/F0、
    /// <c>(int)</c> 截断位置也没变。刻意没顺手换成 InvariantCulture——那会改动中文模式的输出，
    /// 是另一件事，不混在这次里做。</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<QualityScale, string> ScaleLabels =
        new Dictionary<QualityScale, string>
    {
        [QualityScale.LosslessFixed100] = "无损 (固定 100%)",
        [QualityScale.LosslessFixed] = "无损 (固定)",
        [QualityScale.QualityPercent] = "质量 (50-100; 85+ 视觉无损)",
        [QualityScale.Crf] = "CRF (0=无损, 63=最低; 15 ≈ 视觉无损)",
        [QualityScale.ButteraugliRange] = "butteraugli 距离 (0.5-3.0)",
        [QualityScale.ButteraugliDistanceRange] = "butteraugli 距离 (越小越清晰; 1.0 = 视觉无损)",
    };

    /// <summary>
    /// Core 的 FormatColorFacts.Declaration 产出的、<b>App 会往回翻</b>的声明通道文案。
    /// 逐字抄自 <c>FormatEncoders</c> 同目录下的 <c>FormatColorFacts.cs</c>；译文在
    /// <see cref="LocaleManager.ZhToEn"/>。两边一一对由
    /// <c>Test_QualityTexts_CoreStringsAreAllCovered</c> 的第三段钉住：Core 新增一句中文
    /// 而这里没跟，判据就红并点名。带 <c>{0}</c> 的是模板 —— 先翻模板再代值。
    /// </summary>
    private static readonly string[] CoreColorFactsTexts =
    {
        "PNG：cICP {0}（原色/传输/矩阵），matrix 恒 0(identity) —— PNG 存 RGB 采样，没有 YUV 往返可声明。",
        "HDR 一律不嵌 ICC。",
        "{0} 目标另嵌 iCCP，两条通道同一套原色。",
        "{0} 目标不嵌 ICC（cICP 已足够）。",
        "AVIF：colr(nclx) {0}，matrix 随三原色取 {1} —— 它是 YUV 码流，",
        "不能像 PNG 那样写 identity（写 0 时部分解码器自行套错矩阵）。",
        "HDR 不嵌 ICC。",
        "{0} 另可嵌 ICC。",
        "{0} 不嵌 ICC。",
        "JPEG XL：color fields 与 ICC 二选一（同时给时 cjxl 只走 ICC、fields 被丢弃；",
        "ICC 能归约成字段就只写字段，归约不了就整份内嵌）。没有 matrix 这一维，",
        "且有损产物把像素量化进 XYB —— 声明写的是原图色域，不是码流里的通道值。",
        "HDR 走 Rec.2020 原色 + PQ（cjxl 的枚举名 Rec2100PQ，另带 intensity_target）。",
        "{0} 取到可嵌 ICC 时用 ICC，否则用 color fields。",
        "{0} 走 color fields，不嵌 ICC。",
        "JPEG：只有 ICC 一条声明通道，没有代码点 —— ",
        "{0} 原色嵌 APP2/ICC（实测产物里 ICC_PROFILE 与 acsp 都在）。",
        "{0} 目标【什么都不写】，靠 JFIF 默认。",
        "本格式的默认目标就是 Rec.709：跟随显示器也好、默认档也好，都不会自动升到 Rec.2020 —— ",
        "只有用户把目标【显式】选成 Rec.2020 时才会写出 Rec.2020 原色。",
        "容器无 HDR 通道 ⇒ 开关会落空成 8-bit SDR；这与【能否声明 Rec.2020 原色】是两件事。",
        "WebP：同样只有 ICC 一条通道 —— ",
        "{0} 原色经中转 PNG 的 iCCP 带过去，并给 cwebp 传 -metadata icc（实测产物 ICCP 块在，profile 2100 字节）。",
        "{0} 目标【什么都不写】（实测对照组无 ICCP）。",
        "本格式的默认目标同样是 Rec.709：跟随显示器或默认档都只给 Rec.709，",
        "要 Rec.2020 得由用户【显式】指定 —— 与 JPEG 同一条规则，两族格式不再各自为政。",
        "无 HDR 通道 ⇒ HDR 开关落空成 8-bit SDR，但原色声明照常；缺 cwebp.exe 时整份产物会换成 PNG。",
        "TIFF：没有代码点通道 ⇒ 只靠 ICC，连 Rec.709 也老实嵌一份（不像 JPEG/WebP 能省）。",
        "HDR 用带 PQ TRC 的 ICC 声明。",
        "SDR 用标准色域 ICC。",
        "JPEG（附带增益图）：底图【恒为 SDR】且【恒嵌】一份 ICC —— 目标是 {0} ⇒ 底图、增益比、",
        "声明三件同一套原色；换目标就是换原色，不会把底图变成 HDR（HDR 由 XMP hdrgm + ",
        "ISO 21496-1 增益图段承载，容器不写 PQ ⇒ 与「目标不是 HDR 档」不冲突）。",
        "无码点",
        "未知格式：给不出声明通道。",
    };

    /// <summary>滑块上方那一行的质量轴标签。</summary>
    private static string ScaleText(QualityScale scale)
        => ScaleLabels.TryGetValue(scale, out var zh) ? LocaleManager.Tr(zh) : scale.ToString();

    /// <summary>质量描述那一行的短句。Kind 落到没模板的成员时返回枚举名：界面会露出一个怪词，
    /// 但那条判据先一步红，不留"看着像英文"的静默回落。</summary>
    private static string QualityText(QualityDesc d) => d.Kind switch
    {
        QualityDescKind.Lossless => LocaleManager.Tr("无损"),
        QualityDescKind.NearLossless => LocaleManager.Tr("近无损"),
        QualityDescKind.Distance => LocaleManager.Tr("距离: ") + d.Value.ToString("F1"),
        QualityDescKind.DistanceWithNote => LocaleManager.Tr("距离: ") + d.Value.ToString("F1")
            + LocaleManager.Tr(" (越小越清晰)"),
        QualityDescKind.Crf => "CRF: " + ((int)d.Value).ToString(),
        QualityDescKind.Percent => ((int)d.Value).ToString() + "%",
        QualityDescKind.PlainNumber => d.Value.ToString("F0"),
        _ => d.Kind.ToString(),
    };

    /// <summary>
    /// AVIF「编码器」下拉的项文字来自 <c>AvifEncoderCapabilities.Caps(kind).DisplayName</c>，
    /// 那是 Core 能力表里的<b>中文成品串</b>（<c>Intel QSV / 系统 MFT</c>、
    /// <c>avifenc（软件 · 由 avifenc 自选编码器）</c>），而 <c>UpdateAvifEncoderLabels</c>
    /// 还会往名字后面拼一个中文状态后缀「（本机不可用）」。
    /// 这份清单就是"App 会翻哪些后端名/后缀"，由
    /// <c>Test_QualityTexts_CoreStringsAreAllCovered</c> 的第二段枚举钉住。
    /// </summary>
    private static readonly string[] CoreAvifBackendTexts =
    {
        "avifenc（软件 · 由 avifenc 自选编码器）",
        "Intel QSV / 系统 MFT",
        "（本机不可用）",
    };

    /// <summary>
    /// Core 产出的中文成品串 → 当前语言。两条通道：整串精确匹配，或按 <paramref name="known"/>
    /// 里的已知片段换（Core 用 $"" 拼出来的那种带数值的，整串永远匹配不上）。
    /// <para>换完还剩中文 ⇒ 出现了清单没覆盖的新文案，宁可退回整句中文也不给半中半英；
    /// 漏了哪一条由 <c>Test_QualityTexts_CoreStringsAreAllCovered</c> 报，不靠这里兜。</para>
    /// </summary>
    private static string CoreText(string fromCore, string[] known)
    {
        if (string.IsNullOrEmpty(fromCore) || LocaleManager.CurrentLanguage != AppLanguage.English)
            return fromCore;
        var exact = LocaleManager.Tr(fromCore);
        if (!string.Equals(exact, fromCore, StringComparison.Ordinal)) return exact;
        var s = fromCore;
        foreach (var frag in known)
            if (s.Contains(frag, StringComparison.Ordinal))
                s = s.Replace(frag, LocaleManager.Tr(frag), StringComparison.Ordinal);
        return s.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? fromCore : s;
    }

    /// <summary>质量描述行的<b>唯一</b>写入点。</summary>
    /// <remarks>
    /// 为什么要有这一个函数：以前 <c>UpdateQualityPanel</c> 与 <c>OnQualityChanged</c> 各写一次，
    /// 而后者只会写 <c>GetQualityDescription</c> ⇒ 无损格式那句"无损 (固定 100%)"随时可能被
    /// 一次滑块赋值冲成一个孤零零的"100"。当时它没出事纯靠调用顺序（面板先收起、640 行最后写），
    /// 而"靠顺序才正确"正是本仓库反复踩过的那类隐性耦合。现在两处都问这同一个函数，
    /// 且它对同一输入给同一输出 ⇒ 谁后写都一样。
    /// 无损格式（区间退化成一点，见 <c>ImageEncoder.QualityIsFixed</c>）整行已收起，
    /// 描述行必须说清"这格式根本没有质量参数"，而不是显示一个动不了的数值。
    /// </remarks>
    private void SetQualityDescription(ImageEncoder encoder, double quality)
        => QualityDescTxt.Text = encoder.QualityIsFixed
            ? ScaleText(encoder.GetQualityRange().Scale)
            : QualityText(encoder.GetQualityDescription((float)quality));

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
            _settings.SetQuality(CurrentFormat(), v);
            AppServices.Settings.SaveQuiet();
        }
        else QualityTxt.Text = QualitySld.Value.ToString("F1");
    }

    // ── HDR + ACM + ICC ──

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.HdrEnabled = HdrSwitch.IsOn;

        // 色彩页的卡片由 UpdateQualityPanel 内部统一刷新（它要先看 HDR 被格式联动改成什么状态）
        // HDR 开关影响格式 HDR 联动提示 (GainMap 色域说明) — 刷新当前格式面板
        UpdateQualityPanel();
        // ═══ 2026-09-17：HDR 开关直接决定 AVIF 走哪条编码路径 ═══
        // HDR 分支不用「编码器 / 位深 / 色度」三项设置，必须同步刷新其可用性。
        // UpdateQualityPanel 内部在 isAvif 分支也会调用，此处重复调用是幂等的纯 UI 刷新，
        // 保留显式调用以免将来 UpdateQualityPanel 重构后丢失联动。
        UpdateAvifOptionAvailability();
        AppServices.Settings.SaveQuiet();
    }

    private void OnColorSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var tag = GetSelectedColorSpaceTag();
        bool isSRgb = tag is "System" or "sRGB";

        // ⚠ 这里只说"ACM 状态 + 要不要搬色"，【不再复述 ICC 嵌不嵌】：
        //   sRGB 这个目标在几个格式里根本不留声明（JPEG LI/WebP 什么都不写），TIFF 却连 sRGB
        //   也老实嵌一份 ICC。"声明通道"这类逐格式不同的事实在 Core 的 FormatColorFacts 里只有一份，
        //   由下方「色域映射」卡片按当前格式印出来。原先这行写死"不嵌入 ICC"，
        //   于是同一页两张卡对同一枚文件给出两种说法。
        if (_settings.AcmeDetected)
        {
            IccHintTxt.Text = isSRgb
                ? "ACM 已启用 —— 目标 Rec.709：像素与声明同为 Rec.709 原色（它是通用默认色域）。"
                : $"ACM 已启用 —— 把像素从显示器色域映射到 {ColorProfileProvider.GetColorSpaceDisplayName(tag)}。";
        }
        else
        {
            IccHintTxt.Text = isSRgb
                ? "目标 Rec.709（通用默认色域）"
                : $"目标 {ColorProfileProvider.GetColorSpaceDisplayName(tag)}：像素搬进去，声明与像素同一套原色。";
        }
        // 色彩页的卡片同样由 UpdateQualityPanel 统一刷新（见其内部的唯一调用点）
        // 色域变化影响 GainMap 色域说明提示
        UpdateQualityPanel();
        _settings.ColorSpaceIndex = ColorCbo.SelectedIndex;
        AppServices.Settings.SaveQuiet();
    }

    private void OnOverlayColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        // 这个字段有【两个写者】：这里，和 MainWindow.Settings.cs 的整表保存。10-01 那次只改了
        // 后者，这一处仍留着 ?? 兜底 ⇒ 盘上至今是那个不在 7 个选项里的脏值（下拉靠补项才显示出来）。
        // 同一个处方重下一遍：没有选中项就保留盘上原值，不许把写死的颜色再生产一次。
        if (OverlayColorCbo.SelectedItem is ComboBoxItem overlayColorItem)
            _settings.OverlayColor = (string)overlayColorItem.Tag;
        SaveSettingsQuiet();
    }

    private void OnBorderColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.BorderColor = (BorderColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4488FF";
        SaveSettingsQuiet();
    }

    // 注: 色彩页的渲染（RefreshColorMappingUi / GetSelectedColorSpaceTag）在 MainWindow.ColorManagement.cs

    // ── 编码辅助（仅在最终保存/复制时触发）──

    private async Task EncodeAndSaveAsync(byte[] bgra, int w, int h)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("MainWindow", $"开始截图流程: {w}x{h} 格式={CurrentFormat()}"
            + (_settings.GainMapEnabled ? "（JPEG + 增益图）" : ""));

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
        // ⚠ 格式名必须在【UI 线程】上取好再交给 Timer：SetStatus 自己会 DispatcherQueue.TryEnqueue，
        //   但插值表达式是在回调里求值的 —— 原先在后台线程读 FormatCbo.SelectedIndex（XAML 控件），
        //   属于跨线程访问，慢编码（JXL/libaom 4K ≥500ms，正是这个提示要出现的场景）时才走到这条路。
        string fmtLabel = LocaleManager.GetFormatLabel(
            _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)]);
        var progressTimer = new System.Threading.Timer(_ =>
        {
            SetStatus($"⏳ 正在编码 {fmtLabel} ...");
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
            var colorSpaceTag = ui.ColorSpaceTag;
            LogService.Info("MainWindow", $"编码设置: 格式={format} HDR={hdrOutput} 色域={colorSpaceTag}");
            // ═══ 2026-09-19 D-2：本入口的源是 byte[] BGRA8 ⇒ HDR 直通在事实上不可能 ═══
            // EncodeAndSaveAsync 恒走 SDR 路径并把容器位深钳到 8（CPS:429/442）。原先把 UI 的 hdrOutput
            // 当编码场景传进去，于是 AVIF 白跑一遍 HDR 协商（位深被钉 10、QSV 被改写 avifenc 并弹
            // "硬件不支持 HDR"提示），随后又被钳回 8 —— 提示与日志都在描述一次并未发生的 HDR 编码。
            // 本入口的像素没有"来自哪块屏"的信息（粘贴/预览/区域合成）⇒ 按光标屏决定，全窗口只此一处。
            var decision = DecisionForCursorScreen(ui, hdrOutput: false);
            var settings = BuildEncodingSettings(format, hdrOutput: false, null, ui, decision);

            // 委托给 CapturePipelineService 执行色域映射 + 编码
            LogService.Info("MainWindow", $"委托 CapturePipelineService 执行编码... {decision.Describe()}");
            var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                bgra, w, h, settings, decision, ct);

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
        // 这一拍的关联 ID。编码阶段是被"后台跟踪的任务"接走的，而那个任务在本作用域里被启动 ⇒
        // ExecutionContext 会把 ID 一起带进去（AsyncLocal 随执行上下文流动），后台任务里不该再开新 ID。
        using var silentOp = CoreLog.BeginOp();
        _captureOpId = silentOp.Id;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("SilentCapture", "无感截图启动");

        try
        {
            if (_wgcService is null)
            {
                LogService.Error("SilentCapture", "WGC 捕获服务未初始化");
                // 热键按下去必须有回响：原先只写日志 ⇒ 用户以为"截图了但没找到文件"。
                // 文案与另两根入口（选区/整屏）用的是同一句，不新开一条界面中文。
                SetStatus("❌ 捕获服务未初始化");
                ToastService.ShowCaptureFailed("捕获服务未初始化");
                return;
            }

            // ⚠ 无感截图由 UI 线程启动：在此采集 UI 状态快照，供后台编码线程使用。
            // 后台线程访问 WinUI 控件会抛异常并被静默吞掉（详见 EncodingUiState 注释）。
            var ui = CaptureEncodingUiState();
            var format = ui.Format;
            var hdrOutput = ui.HdrEnabled;
            // 这里不再打印"Float16 广色域"开关：§4.3 之后源精度只由显示器能力判定（PreferHdr 恒 true，
            // 服务侧 CanDeliverExtendedRange 决定给不给得出 >8-bit）。旧的那个开关只剩这行日志在消费，
            // 打出来反而让人以为"False ⇒ 不会走高精度链"（实测本机就打印了 False 却拿到 Float16 帧）。
            // 真实源精度由紧随其后的"HDR/SDR 帧捕获成功"那行报，那才是事实。
            LogService.Info("SilentCapture", $"捕获配置: 格式={format} HDR输出={hdrOutput} 源精度=按显示器能力判定");

            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                // ═══ 2026-09-19（用户修正 §4.3 双路径策略）：源精度只由显示器能力决定 ═══
                // 旧写法是 `PreferHdr = hdrOutput || useFloat16Wide` —— 用"这次想输出 HDR/广色域吗"
                // 去决定"用什么精度的源"，于是 10-bit/ACM 显示器上只要用户没开 HDR 输出，
                // 抓取就直接落到 8-bit BGRA，后续任何色域矩阵/色调映射都只能在 8-bit 数据上做。
                // 现在恒请求高精度：给得出就用 Float16 scRGB，给不出由 WgcCaptureService 的
                // 能力判定（CanDeliverExtendedRange）跳过并负缓存 ⇒ 不会白付一次首帧超时。
                // 输出位深是【另一件事】，由用户的位深下拉决定（见 ExecuteSilentEncodeAsync）。
                PreferHdr = true,
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
                await ExecuteSilentEncodeAsync(captureResultRef, sw, hdrOutput, format, ui);
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
        bool hdrOutput, OutputFormat format, EncodingUiState ui)
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
            string path;

            if (actualHdr && captureResult.HdrPixels is not null)
            {
                LogService.Info("SilentCapture", $"HDR 帧捕获成功: {fw}x{fh} {(captureResult.IsHdr ? "HDR" : "SDR")}");
                // ═══ 2026-09-19 缺陷 D-2：源精度事实 ≠ 编码场景，两件事必须分开传 ═══
                // 原先这里传的是 actualHdr（"WGC 这次给没给出 Float16"）。但自 §4.3 修正后 PreferHdr 恒为
                // true ⇒ 能力屏上 actualHdr 恒真 ⇒ BuildEncodingSettings 一直按 HDR 场景协商：AVIF 位深被
                // CPS:248 无条件钉成 10（用户要的 8/12-bit 永远不可达，与 CPS:509-519 的解耦声明直接冲突），
                // QSV 还会被改写 avifenc、并为一次【非 HDR】截图弹"不支持 HDR 输出"的提示。
                // 场景的定义是"这次真的走 HDR 直通吗"——由用户开关与格式决定，与源精度无关。
                bool hdrScenario = hdrOutput || format == OutputFormat.JPEG_GAINMAP;
                var decision = DecisionFor(captureResult, colorSpaceTag, hdrScenario, ui.Settings.AcmeDetected, format);
                var settings = BuildEncodingSettings(format, hdrScenario, meta, ui, decision);
                settings.IccProfile ??= captureResult.IccProfile;

                if (hdrScenario)
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
                    // ═══ 2026-09-19（§28/§29）：Float16 源 + SDR 目标 → 走编码器的 SDR 分派 ═══
                    // 编码器内部现在把两件事分开：①中转/输入精度由【源是不是 Float16】决定
                    //   （`FormatHelper.IsHighPrecisionSource`）；②容器位深由用户的位深下拉决定。
                    // 色域矩阵与色调映射都在浮点里做完，最后量化一次 ⇒ 即便选 8-bit 也不丢广色域。
                    // （旧注释提到的 PrepareFloat16WithIcc 已删除：那条路先量化到 8-bit 再处理，
                    //   且从不做矩阵变换，正是 §29 要消灭的形态。派生 SDR 像素不带 GPU 纹理。）
                    // 本分支不再需要手动 settings.HdrOutput = false —— D-2 修复后传进 BuildEncodingSettings
                    // 的是 hdrScenario（本分支即 false），标志本来就是 false；旧那行是在补偿
                    // "把源精度当编码场景"造成的错位。
                    LogService.Info("SilentCapture", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                    path = await AppServices.Pipeline.EncodeHdrFrameAsync(
                        new HdrFrameData
                        {
                            Pixels = captureResult.HdrPixels,
                            Width = fw, Height = fh,
                            IccProfile = settings.IccProfile,
                            Metadata = meta,
                            GpuTexture = null
                        }, settings);
                }
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null)
                {
                    LogService.Warn("SilentCapture", "SDR 像素数据为空，跳过保存");
                    // 与 ExecuteEncodeSaveAsync 里同一失败用的是同一句文案，不新开界面中文
                    SetStatus("❌ 像素数据无效");
                    ToastService.ShowCaptureFailed("像素数据无效");
                    return;
                }
                LogService.Info("SilentCapture", $"SDR 帧捕获成功: {fw}x{fh}");

                var decision = DecisionFor(captureResult, colorSpaceTag, false, ui.Settings.AcmeDetected, format);
                var settings = BuildEncodingSettings(format, false, meta, ui, decision);
                settings.IccProfile ??= captureResult.IccProfile;
                // 纹理直通与否交给管线按"这次要不要搬色"决定（见 EncodeAndSaveAsync 里那条判据）
                path = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, decision, CancellationToken.None, captureResult.GpuTexture);
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
            // 与选区那根的失败分支同形（状态栏 ❌ + 失败通知）。
            // SetStatus 内部已 DispatcherQueue.TryEnqueue ⇒ 后台线程直接调即可，不必再自己 marshal。
            SetStatus($"❌ 保存失败: {ex.Message}");
            ToastService.ShowCaptureFailed(ex.Message);
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

        // 「截图后右下角缩略图预览」这个开关过去【没有任何消费者】：取消勾选之后，
        // 右下角那块带「打开文件夹」按钮的自定义浮层照样弹 —— 界面写着它管这件事，实际没人读它。
        // 它管的就是下面这段自定义浮层，所以关掉时退回 Windows 自带通知：
        // 用户仍然知道截图成功，只是不要那块浮层。（不是"什么都不发"——那会把
        // ToastOnCapture 的语义一起吃掉，那是另一个开关。）
        if (!_settings.ShowPreview && position != "WindowsNotify")
        {
            ToastService.ShowCaptureSuccess(filePath, elapsedMs);
            return;
        }

        // Windows 自带通知：不创建自定义窗口，直接走 Windows 通知
        if (position == "WindowsNotify")
        {
            ToastService.ShowCaptureSuccess(filePath, elapsedMs);
            return;
        }

        try
        {
            var toast = new SilentCaptureToast(position);
            // 传入 filePath：浮层会显示"打开文件夹"（在资源管理器中选中该文件）
            toast.SetContent("✅ 已保存并复制到剪贴板",
                $"{Path.GetFileName(filePath)}  ({elapsedMs}ms)",
                ToastTone.Success, filePath);
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

            // ═══ 2026-09-15 修复：补剪贴板位图通道 ═══
            // 原实现只有 SetStorageItems（文件对象）→ 只能"粘贴为文件"。
            // 微信 / QQ / Word / 画图 / 网页输入框只接受位图 ⇒ Ctrl+V **完全无反应**，
            // 而这正是截图工具最高频的诉求（截图 → 粘贴）。
            // SetBitmap 接受 RandomAccessStreamReference，系统按需解码为 CF_DIB 等位图格式。
            // 注：AVIF / JPEG XL 等格式系统剪贴板未必能解码，此时 SetBitmap 会抛异常 ——
            // 捕获后仍保留文件通道，不因位图失败而丢失已经可用的文件复制。
            bool bitmapOk = false;
            try
            {
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                bitmapOk = true;
            }
            catch (Exception bmpEx)
            {
                LogService.Warn("MainWindow", $"剪贴板位图通道不可用（保留文件复制，粘贴到聊天框/画图可能无效）: {bmpEx.Message}");
            }

            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            SetStatus(bitmapOk
                ? $"✅ 已保存并复制: {Path.GetFileName(filePath)}"
                : $"✅ 已保存并复制（仅文件，位图不可用）: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            // 原实现为空 catch：复制失败时用户看到的是"已复制"，实际什么都没发生。
            LogService.Warn("MainWindow", $"复制到剪贴板失败: {ex.Message}");
            SetStatus($"✅ 已保存（复制到剪贴板失败）: {Path.GetFileName(filePath)}");
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
            var colorSpaceTag = ui.ColorSpaceTag;

            // 用与正式保存相同的设置构建 (含 System→实际色域的 ACM 感知解析)
            // 剪贴板入口同样"复用完整保存管线"⇒ 也要给一份决定；这里无源屏信息 ⇒ 光标屏（与旧行为一致）。
            var decision = DecisionForCursorScreen(ui, ui.HdrEnabled);
            var settings = BuildEncodingSettings(ui.Format, ui.HdrEnabled, null, ui, decision);

            // 输出到临时目录 (不写入用户正式输出目录)
            var tmpDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_Clip");
            Directory.CreateDirectory(tmpDir);

            // ═══ 2026-08-16 P2-5 修复: 剪贴板复制接入 CTS (旧为 default) ═══
            var ct = _captureCts?.Token ?? CancellationToken.None;

            var path = await AppServices.Pipeline.EncodeAndSaveAsync(
                bgra, w, h, settings, decision,
                ct, null, tmpDir);

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetStorageItems(new[] { file });

            // 位图通道（同 CopyFileToClipboardAsync）：没有它就无法粘贴到聊天框/画图/Word
            bool bitmapOk = false;
            try
            {
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                bitmapOk = true;
            }
            catch (Exception bmpEx)
            {
                LogService.Warn("MainWindow", $"剪贴板位图通道不可用（保留文件复制）: {bmpEx.Message}");
            }

            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            SetStatus(bitmapOk ? "📋 已复制到剪贴板" : "📋 已复制（仅文件，位图不可用）");

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
        LogService.Info("MainWindow", $"HDR 编码启动: {w}x{h} 格式={CurrentFormat()}");

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
            var colorSpaceTag = ui.ColorSpaceTag;
            // D-2（同 ExecuteSilentEncodeAsync）：本入口的源恒为 Float16 ⇒ 编码场景只由用户开关与格式决定，
            // 不能把"有浮点像素"当"要输出 HDR"传，否则 HDR 关时 AVIF 位深被 HDR 协商钉成 10。
            bool hdrScenario = hdrOutput || format == OutputFormat.JPEG_GAINMAP;
            // 本入口的源是"内存里的一块 Float16"，没有 CaptureResult ⇒ 只能按光标屏决定（同旧行为）。
            // 格式必须传：本入口是"把内存里那一帧重新编码成 format"，容器口径（JPEG 家族/WebP 的
            // Rec.709 默认档）与下面 BuildEncodingSettings 收的是同一个 format。
            var decision = ColorDecision.Build(
                DisplayEnumerator.FindDisplayByMonitor(cursorMonitor),
                colorSpaceTag, hdrScenario, ui.Settings.AcmeDetected, format);
            var settings = BuildEncodingSettings(format, hdrScenario, meta, ui, decision);
            // 注入调用方带进来的 ICC（色彩管理不可关 ⇒ 不再有"仅烘焙开启时"这个条件）
            settings.IccProfile ??= iccProfile;

            LogService.Info("MainWindow", $"HDR 编码: {format} {w}x{h} HDR={hdrScenario} {decision.Describe()}");

            // ═══ 2026-08-10 修复: GainMap 格式只要有 HDR 帧就走主路径 ═══
            // (与 SilentCapture 一致: GainMap 需要原始 HDR 帧生成增益图,
            //  与 HDR 开关无关; 旧逻辑走 SDR 降级 → 无增益图)
            if (hdrScenario)
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
                // Float16 scRGB 源 + SDR 目标 → 委托编码器 SDR 分派（§11 位深策略），
                // 与无感截图路径一致。派生选区像素不带 GPU 纹理。
                // 不再需要手动 settings.HdrOutput = false（D-2 后传进去的就是 hdrScenario=false）。
                LogService.Info("MainWindow", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                var path = await AppServices.Pipeline.EncodeHdrFrameAsync(
                    new HdrFrameData
                    {
                        Pixels = hdrPixels,
                        Width = w, Height = h,
                        IccProfile = settings.IccProfile,
                        Metadata = meta,
                        GpuTexture = null // P1-3: 选区像素不携带桌面纹理
                    }, settings, ct);

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
            // 这一发【没有失败】——它是被上面那道防重入闸拦下的。原先借 ShowCaptureFailed 的通道，
            // 标题写死 "❌ 截图失败" ⇒ 用户看到一条红色失败通知，通知正文却写着"请稍候"，
            // 标题与正文互相打脸（2026-09-30 造 GPU 失败腿时从日志里撞见同一毫秒的
            // 「忽略重复触发」+「已提交系统通知: ❌ 截图失败」，才注意到这条）。
            // 改走统一入口 + Info 色：措辞与语义对齐，且尊重用户设的"通知位置"
            // （ShowCaptureFailed 走的是老的 Show(dict) 直发系统通知，会忽略该设置——那是另一条账，见报告 §28）。
            ToastService.ShowToast("⏳ 截图进行中", "上一次截图仍在处理中，请稍候", ToastTone.Info);
            return;
        }

        bool overlayShown = false;
        try
        {
            // ── 这一拍的关联 ID ──
            // 在防重入闸之后才开：重复触发时被拦下的那一发不该抢走正在处理的那拍的 ID。
            using var captureOp = CoreLog.BeginOp();
            _captureOpId = captureOp.Id;

            LogService.Info("MainWindow", "选区截图启动");
            SetStatus("📷 WGC 捕获桌面...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── 使用 WGC 捕获所有显示器并拼接 ──
            if (_wgcService is null)
            {
                SetStatus("❌ 捕获服务未初始化");
                return;
            }

            if (_settings.UseVSCap)
            {
                await StartVSCapCaptureAsync();
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
                // 2026-09-19：这里原先传 hdrOutputCap || useFloat16WideCap（输出意图决定源精度），
                // 但 CaptureAllMonitorsAsync 内部本就恒置 PreferHdr=true ⇒ 那两个局部变量是死输入
                // （读代码时容易误以为"选区路径由开关决定"）。改为显式传 true，与实际行为一致；
                // 是否真的建 Float16 会话由 WgcCaptureService.CanDeliverExtendedRange 按显示器能力决定。
                captureResult = await _wgcService.CaptureAllMonitorsAsync(new WgcCaptureConfig
                {
                    FrameTimeoutMs = 3000,
                    PreferHdr = true,
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
            LogService.Debug("诊断",
                $"captureResult: HDR={(hdrDesktopPixels is not null ? $"len={hdrDesktopPixels.Length} {hdrW}x{hdrH}" : "null")} SDR={(desktopPixels is not null ? $"len={desktopPixels.Length}" : "null")}", LogCategory.Capture);

            // ═══ 2026-08-16 P1-1 修复: 释放 GPU 纹理引用 ═══
            // SdrPixels/HdrPixels/IccProfile 是托管数组, Dispose 不影响后续使用;
            // GpuTexture (COM 引用) 在该路径无消费方 (预览窗口用 LoadFrame 像素),
            // 且 P1-3 已禁止选区保存传桌面纹理 → 此处安全释放。
            captureResult.Dispose();

            sw.Stop();
            LogService.Info("MainWindow", $"阶段1 WGC捕获完成: {captureResult.Width}x{captureResult.Height} {sw.ElapsedMilliseconds}ms");

            // ── 诊断开关: TTC_FORCE_SDR=1 只用 SDR(BGRA) 帧做预览 ──
            // 2026-09-30 起这个开关的语义收窄了一档：它过去同时改变了【界面】（把用户从
            // HdrCaptureWindow 换到 SelectionOverlay 那套），那正是"跑诊断时屏幕上跳出一张旧界面"
            // 的来源，也让工具条几何一类的判据在两套装束间不可比。现在选区界面只有一条，
            // 这个开关只决定像素源：置 null ⇒ 走 BGRA→scRGB 那条呈现帧。
            bool forceSdr = Environment.GetEnvironmentVariable("TTC_FORCE_SDR") == "1";
            if (forceSdr)
            {
                LogService.Warn("MainWindow", "诊断开关 TTC_FORCE_SDR=1: 预览只用 SDR(BGRA) 帧", LogCategory.Capture);
                hdrDesktopPixels = null;
            }

            // 阶段2: 截图预览窗口
            sw.Restart();

            // ═══ 选区预览只有一张界面：HdrCaptureWindow（2026-09-30 用户口径"以 HDR 下的界面为准，
            // SDR 与 HDR 的用户体验统一"）═══
            // 原先 hasHdr==false 会另开一套 WinUI 覆盖层（SelectionOverlay）。那套覆盖层【没有任何
            // 让窗变透明的机制】（全 App 工程里 SystemBackdrop 只在 SilentCaptureToast 出现过一次），
            // 而 GPU 供帧用的是另一扇被压在它下面的窗 ⇒ "GPU 成功"这一档真正画出来的是不透明页底色
            // （维护者肉眼看到的"全白"），反倒是失败时那段会亲手把像素画进可见表面的 CPU 兜底有画面。
            // 统一到这张自绘 D3D 窗以后，"画面从哪儿来"两条路变成同一件事，工具条钳制也一并换成
            // 按实测宽度算的那份（原先覆盖层写死 460，而它自己的工具条实测约 932 DIP ⇒ 贴右边缘必然溢出屏幕）。
            //
            // 帧来源仍分两档，界面只有一条：
            //   · HDR 帧：WGC 的 Float16 场景参考数据（1.0 = 名义 80 nits，抬到系统白点是 OS 那一层做的）；
            //   · SDR 帧：BGRA→scRGB，参考白与 alpha 由 PixelOps.BgraToScrgbForDisplay 负责 ——
            //     乘白点只允许发生在"HDR 输出正激活"那一档（普通屏上 DWM 对 FP16 scRGB 做数值钳位，
            //     "everything outside of the [0, 1] numeric range is clipped"，乘了就把白削成一团白），
            //     且 A 一律写 1：这张窗是 AlphaMode.Ignore + flip + WS_EX_NOREDIRECTIONBITMAP，
            //     而源帧的 A 由驱动决定，不参与"这一帧长什么样"。
            bool hdrFrame = hdrDesktopPixels is not null;
            var (sysHdrNow, _, _) = CapabilityService.DetectDisplayState();
            int sdrWhiteNits = DisplayEnumerator.GetSdrWhiteLevel();
            float[]? presentFrame = hdrFrame
                ? hdrDesktopPixels
                : desktopPixels is not null
                    ? TrueToneCap.Core.PixelOps.BgraToScrgbForDisplay(
                          desktopPixels, hdrW, hdrH, hdrOutputActive: sysHdrNow, sdrWhiteNits: sdrWhiteNits)
                    : null;
            LogService.Info("MainWindow",
                $"预览帧来源={(hdrFrame ? "HDR(Float16)" : "SDR(BGRA→scRGB)")} " +
                $"HDR输出={sysHdrNow} SDR白点={sdrWhiteNits}nits " +
                $"呈现缩放={(hdrFrame ? "n/a(OS 抬)"
                    : sysHdrNow && sdrWhiteNits > 0 ? $"{sdrWhiteNits / 80f:0.###}" : "1(不乘)")}",
                LogCategory.Capture);
            if (presentFrame is null)
            {
                // 两档帧都没有 ⇒ 不只是"这次没截成"，必须留痕（老代码在这里只改一句状态串，日志一个字没有）
                LogService.Warn("MainWindow", "预览无可用帧（HDR 与桌面像素均为空）⇒ 本次选区中止", LogCategory.Capture);
                SetStatus("❌ 桌面像素数据为空");
                return;
            }

            {
                StatusTxt.Text = LocaleManager.SelectionPreview;
                // 独立 D3D11 设备：渲染线程独占 context，不与 WGC 共享设备竞争
                using var hdrWnd = new Services.HdrCaptureWindow();
                _activeHdrWindow = hdrWnd; // 注册：应用退出时强制关闭
                LogService.Info("MainWindow", "预览窗口初始化...");
                bool initOk = hdrWnd.Initialize(vx, vy, vw, vh);
                LogService.Info("MainWindow", $"预览窗口初始化 {(initOk ? "成功" : $"失败: {hdrWnd.LastError}")}");

                // ═══ 诊断开关 TTC_FORCE_LEGACY_OVERLAY=1：把"兜底"那条路变成可达路 ═══
                // 2026-09-30 把 SDR 预览统一到这张自绘窗之后，`SelectionOverlay` 只剩"预览窗初始化失败"
                // 这一个入口，而全 App 没有任何开关能造出这个失败（明细报告 §35.5）⇒ 那 1662 + 613 行
                // 在正常构建里根本到不了，于是"兜底到底能不能用"这类判断只能去引**改道前的历史二进制**，
                // 那批读数又恰好全带 TTC_FORCE_SDR=1（同一个会话里连"预览帧来源"这行都没有）。
                // 这个开关不改动任何默认行为：只有显式置 1 才主动关掉刚建好的预览窗去走兜底，
                // 目的是让那条路可以被主动进入、被取证，而不是靠猜。
                if (initOk && Environment.GetEnvironmentVariable("TTC_FORCE_LEGACY_OVERLAY") == "1")
                {
                    LogService.Warn("MainWindow",
                        "诊断开关 TTC_FORCE_LEGACY_OVERLAY=1: 主动关闭已建好的预览窗 ⇒ 改走兜底覆盖层（正常构建不会到这里）",
                        LogCategory.Capture);
                    hdrWnd.Close();        // 正常收尾路径就是 Close() → using 作用域 Dispose()，两步都幂等
                    _activeHdrWindow = null;
                    initOk = false;
                }

                if (initOk)
                {
                    hdrWnd.LoadFrame(presentFrame, hdrW, hdrH);
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
                            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = LocaleManager.Ready);
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
                            // 与保存/复制共用同一份合成结果：识字不许绕过标注去读原始桌面像素
                            if (ComposeAnnotatedSdr(hdrWnd, sdrRegion, rw, rh) is { } outPx3)
                                await CaptureAndOcrFromPixelsAsync(outPx3, rw, rh);
                            break;
                        case HdrCaptureAction.Translate:
                            LogService.Info("MainWindow", "HDR 选区翻译");
                            if (ComposeAnnotatedSdr(hdrWnd, sdrRegion, rw, rh) is { } outPx4)
                                await CaptureAndTranslateFromPixelsAsync(outPx4, rw, rh);
                            break;
                    }

                    if (MinimizeTrayChk.IsChecked == true
                        && action is HdrCaptureAction.Save or HdrCaptureAction.Copy)
                        DispatcherQueue.TryEnqueue(() => _trayIcon?.MinimizeToTray());

                    return;
                }
                // 预览窗初始化失败 → 回退到旧版 WinUI 覆盖层（最后兜底，不再是静默降级）。
                // 原因必须内联在这一条：上一行那条带堆栈的是 Info，Brief 档不落盘，
                // 若这里只写"见紧邻日志"，用户手上的 log 文件里就没有原因了。取首行 = 异常类型+消息。
                var fallbackReason = (hdrWnd.LastError ?? "未知原因").Split('\n')[0].Trim();
                LogService.Warn("MainWindow",
                    $"预览窗失败，回退旧版覆盖层：{fallbackReason}", LogCategory.Capture);
                // 2026-09-30：这条以前只有 log —— 用户按了键，屏幕上一张"长得不一样的"界面出来了，
                // 却没有任何一句话告诉他"这不是平时那张"（报告 §33.3 记的就是这个空档）。
                // 走统一入口 + Overlay 通道（与 ShowCaptureDegraded 同形），文案按语言取。
                ToastService.ShowToast(LocaleManager.PreviewFallbackTitle,
                    $"{LocaleManager.PreviewFallbackHint}：{fallbackReason}",
                    ToastTone.Warning, ToastChannel.Overlay);
                // 这里原先还有一句 `hdrDesktopPixels = null;`，注释写"以免下面误判成还有 HDR 路可走"。
                // 2026-10-01 复核：这一支之后全文再无读取（最近的读点在 :2063，在它上面）⇒ 那是句死赋值，
                // 注释描述的防护并不存在。删赋值留此说明（报告 §40 采纳项 2）。
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
                // 这是**另一个 UI 根**（覆盖层在自己那边回调），AsyncLocal 不会从 StartSelectionCapture
                // 流过来 ⇒ 必须显式续到那一拍的 ID，否则"选区确认→编码→落盘"这几行会挂在新 ID 上。
                using var op = CoreLog.ContinueOp(_captureOpId);
                try
                {
                    if (action == SelectionOverlay.ActionResult.Cancel)
                    {
                        if (!_isExiting)
                            DispatcherQueue.TryEnqueue(() => StatusTxt.Text = LocaleManager.Ready);
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
            // 模型目录必须与启动期那两处走同一个解析函数：Initialize 只会生效一次，
            // 谁先跑就用谁的目录。原先这里硬编码 %LOCALAPPDATA%\TrueToneCap\onnx_models
            // ——那既不是发布路径(data\Models)也不是 Debug 路径(Models)，干净安装的机器上
            // 该目录是空的 ⇒ 这次调用一旦抢在启动初始化之前，ONNX 两档全部注册失败，
            // 只剩中文很弱的 Windows OCR，而且用户看不出自己降级了。
            MultiOcrService.Initialize(OnnxOcrEngine.ResolveModelDir());
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
            // 回调体【不在】上面那个 try/catch 里 —— 那个只包住"入队"这个动作。
            // 2026-10-21 的崩溃就是这么走的：开窗异常从这里逃出去 → 没人接 → 升级成原生 fail-fast，
            // 托管侧连一行日志都没有（台账 §70）。这里接住它，至少让失败可见。
            try
            {
                OpenOcrPreviewWindow(pixels, w, h, result, autoTranslate: false);
                StatusTxt.Text = "📝 已打开文字预览窗口";
            }
            catch (Exception ex)
            { LogService.Error("MainWindow", $"打开文字预览窗失败: {ex.Message}", ex); StatusTxt.Text = $"❌ {ex.Message}"; }
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
            // 模型目录必须与启动期那两处走同一个解析函数：Initialize 只会生效一次，
            // 谁先跑就用谁的目录。原先这里硬编码 %LOCALAPPDATA%\TrueToneCap\onnx_models
            // ——那既不是发布路径(data\Models)也不是 Debug 路径(Models)，干净安装的机器上
            // 该目录是空的 ⇒ 这次调用一旦抢在启动初始化之前，ONNX 两档全部注册失败，
            // 只剩中文很弱的 Windows OCR，而且用户看不出自己降级了。
            MultiOcrService.Initialize(OnnxOcrEngine.ResolveModelDir());
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
            // 与识字那路同一条纪律：回调体不受外层 try/catch 保护，逃出去就是原生 fail-fast（台账 §70）。
            try
            {
                OpenOcrPreviewWindow(pixels, w, h, ocrResult, autoTranslate: true);
                StatusTxt.Text = "🌐 已打开翻译预览窗口";
            }
            catch (Exception ex)
            { LogService.Error("MainWindow", $"打开翻译预览窗失败: {ex.Message}", ex); StatusTxt.Text = $"❌ {ex.Message}"; }
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
            TargetLanguage = _settings.TargetLanguage,
            // ═══ 2026-09-15 修复：有道凭据从未注入 ═══
            // LlmConfig 本就有这两个属性（TranslationService.cs:413/416），但构造时从未赋值
            // ⇒ TranslationService.cs:88 的判空【必然成立】⇒ 有道后端永不执行：
            // 用户填了 AppKey/AppSecret 也毫无作用，还被静默降级到 Google，界面无任何提示。
            YoudaoAppKey = _settings.YoudaoAppKey,
            YoudaoAppSecret = _settings.YoudaoAppSecret,
            // Google 的非官方端点默认不启用（ToS + 不外发 OCR 原文），仅当用户在设置里勾选
            AllowUnofficialTranslateEndpoints = _settings.AllowUnofficialTranslateEndpoints,
            // 视觉模式：整图直传给支持图片的端点，不经 OCR 文本（实现在
            // TranslationService.TranslateImageWithVisionAsync —— 这条曾经是零实现的死开关）
            UseVision = _settings.TranslationMode == "Vision"
        };
        var win = new OcrPreviewWindow(pixels, w, h, ocr, cfg, autoTranslate)
        {
            // 预览窗口的复制/翻译/保存是各自独立的 UI 根 ⇒ 把这一拍的 ID 交下去，让它们续到同一条时间线
            CaptureOpId = _captureOpId,
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

            // ── WGC 单显示器捕获 ──
            // 源精度不在此决定：PreferHdr 恒 true，由服务侧的逐屏能力判定给不给得出 >8-bit，
            // 实际拿到什么精度的帧由下面的"HDR/SDR 帧捕获成功"报 —— 这里只报用户意图。
            LogService.Info("MainWindow", $"WGC 单显示器捕获: HDR输出={hdrOutput} 源精度=按显示器能力判定");
            var captureResult = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
            {
                PreferHdr = true,   // 2026-09-19：源精度由能力决定，不由输出意图决定（§4.3 修正）
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

            if (actualHdr && captureResult.HdrPixels is not null)
            {
                LogService.Info("MainWindow", $"HDR 帧捕获: {fw}x{fh}");
                // D-2（同 ExecuteSilentEncodeAsync）：源精度事实 ≠ 编码场景。传 hdrScenario，
                // 否则 HDR 关时 AVIF 的容器位深会被 HDR 协商无条件钉成 10-bit。
                bool hdrScenario = hdrOutput || format == OutputFormat.JPEG_GAINMAP;
                var decision = DecisionFor(captureResult, colorSpaceTag, hdrScenario, ui.Settings.AcmeDetected, format);
                var settings = BuildEncodingSettings(format, hdrScenario, meta, ui, decision);
                settings.IccProfile ??= captureResult.IccProfile;

                // ═══ 2026-08-10 修复: GainMap 格式只要有 HDR 帧就走主路径 ═══
                // (与 SilentCapture 一致: GainMap 需要原始 HDR 帧生成增益图)
                if (hdrScenario)
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
                    // Float16 scRGB 源 + SDR 目标 → 委托编码器 SDR 分派（§11 位深策略），
                    // 与无感截图路径一致。派生像素不带 GPU 纹理。
                    // 不再需要手动 settings.HdrOutput = false（D-2 后传进去的就是 hdrScenario=false）。
                    LogService.Info("MainWindow", $"Float16 广色域 SDR 转换: 色域={colorSpaceTag}");
                    fullPath = await AppServices.Pipeline.EncodeHdrFrameAsync(
                        new HdrFrameData
                        {
                            Pixels = captureResult.HdrPixels,
                            Width = fw, Height = fh,
                            IccProfile = settings.IccProfile,
                            Metadata = meta,
                            GpuTexture = null // 派生 SDR 像素不带 GPU 纹理
                        }, settings, ct);
                }
            }
            else
            {
                var sdrPixels = captureResult.SdrPixels ?? captureResult.GetDisplayPixels();
                if (sdrPixels is null) throw new InvalidOperationException("无法获取显示像素");
                LogService.Info("MainWindow", $"SDR 帧捕获: {fw}x{fh}");

                var decision = DecisionFor(captureResult, colorSpaceTag, false, ui.Settings.AcmeDetected, format);
                var settings = BuildEncodingSettings(format, false, meta, ui, decision);
                settings.IccProfile ??= captureResult.IccProfile;
                // 纹理直通交给管线判：要搬色就必须过 CPU 矩阵（P1-2 那条"烘焙开启就禁直通"的规则
                // 现在由 ColorDecision.NeedsMapping 精确表达，不再靠一个 UI 布尔顺带决定）
                fullPath = await AppServices.Pipeline.EncodeAndSaveAsync(
                    sdrPixels, fw, fh, settings, decision, ct, captureResult.GpuTexture);
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
        string ColorSpaceTag,
        float Quality,
        bool AvifPngSuffix,
        bool JxlPngSuffix,
        // 按下那一刻的整份设置快照：管线要从中读每格式位深/色度/编码器选择，
        // 不得回落到活的 SettingsService.Current（见 AppSettingsData.Snapshot 的说明）。
        // ⚠ 这里【不再】单列一个从 AvifCodecCbo 解析的 AvifEncoderKind —— 它曾只进日志、
        //   从不参与编码，等于"编码器选择"同时存在两个事实源（控件 / settings.AvifEncoder）。
        //   现已统一由管线从本快照的 Settings.AvifEncoder 解析（§26.4 P1）。
        AppSettingsData Settings);

    /// <summary>
    /// 当前该编成哪个格式：<b>唯一</b>把"下拉选择 + 增益图开关"翻译成 <see cref="OutputFormat"/> 的地方。
    /// <para>JPEG LI 且增益图开关打开 ⇒ 内部仍走 <see cref="OutputFormat.JPEG_GAINMAP"/> 那条编码路径
    /// （base JPEG + 增益图、扩展名 .jpg、强制 HDR）。翻译只允许发生在本函数：
    /// 若日志、快照、面板各自再判一次开关，就会出现"界面显示 JPEG LI、实际按 GainMap 编"这类对不上账。</para>
    /// </summary>
    private OutputFormat CurrentFormat()
    {
        var f = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
        return f == OutputFormat.JPEG_LI && _settings.GainMapEnabled
            ? OutputFormat.JPEG_GAINMAP : f;
    }

    /// <summary>
    /// 格式下拉的<b>唯一</b>构建口：项序、项数、标签全在这里决定一次；
    /// 构造期与 <c>ApplyLocale()</c> 走的是同一个函数 ⇒ 两条路不可能给出两份清单。
    /// <para>为什么要有它（2026-09-26，交接 §54.1）：本地化过去自己抄了一份 7 项标签数组去
    /// 覆盖 <c>ItemsSource</c>，而编码侧按 <c>_formats</c> 解释索引。摘掉增益图那一项之后
    /// 整列错位，且这条数组在构造期末尾就执行一次 —— 每个用户每次开机看到的都是错标签。</para>
    /// </summary>
    private void RebuildFormatCombo()
    {
        // ⚠ 换 ItemsSource 会把 SelectedIndex 打回 -1（WinUI 的行为），而本函数每次
        //    ApplyLocale() 都会跑一次 —— 构造期它排在 ApplySettingsToUI()【之后】，
        //    于是"刚按设置选好的格式"被抹成空白：2026-09-26 用户报的"默认选项缺失"就是这个。
        //    所以选中项必须由本函数自己取回来，不能指望调用顺序。
        int keep = FormatCbo.SelectedIndex;
        FormatCbo.ItemsSource = _formats
            .Select(f => LocaleManager.GetFormatLabel(f))
            .ToList();
        // keep<0 的回落必须取 _settings.FormatIndex，不能取 DefaultIndex：
        // 构造期第一次调用（LoadSettings 之前）控件尚未实化，SelectedIndex 就是 -1，
        // 那时盘上读进来的用户选择还在 _settings 里 ⇒ 拿 DefaultIndex 会把它抹成 PNG；
        // 而下面这一次赋值会触发 OnFormatChanged（此刻 _uiReady 已是 true），
        // 于是"界面显示 PNG"之外还会连带 `_settings.FormatIndex = 0` + SaveQuiet() 写盘
        // —— 用户选的格式不仅下次开机失效，连文件里那个键都被删掉（2026-10-01 实机量到：
        // 产品自己写入 FormatIndex=3，重启后界面回到 PNG，再保存后键整个消失）。
        FormatCbo.SelectedIndex = Math.Clamp(
            keep < 0 ? _settings.FormatIndex : keep, 0, FormatOrder.MaxIndex);
        CheckFormatComboContract();
    }

    /// <summary>
    /// 显示层 ↔ 编码层 ↔ 持久化契约的<b>三方一致性自检</b>。
    /// <para>只在运行时兜底，<b>不静默修复</b>：不一致就往默认日志档留一条 Error（带两边实际读数），
    /// 因为"悄悄改回来"正是这类缺陷能活很多轮的原因 —— 界面上后来是对的，日志里没人看。</para>
    /// <para>三格里第 ②③ 格与 UI 实化时机无关，构造期就能判；第 ① 格要等 <c>Items</c> 真的生成，
    /// 未实化（Count 为 0）时跳过而不报错 —— 否则这个自检自己就是个假红灯源。</para>
    /// </summary>
    private void CheckFormatComboContract()
    {
        // ② App 的项序必须逐项等于 Core 的顺序表：这两张表一个是界面、一个是 FormatIndex 的
        //    持久化契约，谁漂了都会在老配置上"静默指向别的格式"（§52 已经栽过一次）。
        if (_formats.Count != FormatOrder.UiFormats.Length)
            LogService.Error("MainWindow",
                $"界面格式数 {_formats.Count} 与 Core FormatOrder.UiFormats "
                + $"{FormatOrder.UiFormats.Length} 不一致 ⇒ FormatIndex 的解释已开始分叉");
        else
            for (int i = 0; i < _formats.Count; i++)
                if (_formats[i] != FormatOrder.UiFormats[i])
                    LogService.Error("MainWindow",
                        $"格式表第 {i} 项分叉：界面是 {_formats[i]}、Core 契约是 {FormatOrder.UiFormats[i]}"
                        + $" ⇒ 老配置存的 FormatIndex={_settings.FormatIndex} 会按另一张表解释");

        // ③ 配置里的索引落到两张表上必须给出同一个格式（LoadSettings 之后才有意义）
        var byIndex = FormatOrder.FormatAt(_settings.FormatIndex);
        var byList = _formats[Math.Clamp(FormatCbo.SelectedIndex, 0, _formats.Count - 1)];
        if (FormatCbo.SelectedIndex >= 0 && byList != byIndex && !(byList == OutputFormat.JPEG_LI && _settings.GainMapEnabled))
            LogService.Error("MainWindow",
                $"FormatIndex={_settings.FormatIndex} 按 Core 契约是 {byIndex}、按下拉选中项却是 {byList}"
                + "（增益图开关打开时 JPEG 这一格会翻译成增益图，属预期，不报）");

        // ① 显示项与 `_formats` 逐项对得上（抓"又冒出一份清单"）
        var items = FormatCbo.Items;
        if (items.Count == 0) return;             // 未实化：不据此报错
        if (items.Count != _formats.Count)
        {
            LogService.Error("MainWindow",
                $"格式下拉项数 {items.Count} 与编码侧 {_formats.Count} 不一致（{string.Join("/", _formats)}）"
                + " ⇒ 选中项会被解释成别的格式，检查 RebuildFormatCombo 的调用点");
            return;
        }
        for (int i = 0; i < _formats.Count; i++)
        {
            var shown = items[i] as string;
            var want = LocaleManager.GetFormatLabel(_formats[i]);
            if (shown == want) continue;
            LogService.Error("MainWindow",
                $"格式下拉第 {i} 项错位：显示「{shown}」、应为「{want}」（该位置编码为 {_formats[i]}）"
                + " ⇒ 有第二份格式清单冒出来了");
        }
    }

    /// <summary>在 UI 线程采集当前编码相关的 UI 状态。仅供 UI 线程调用。</summary>
    private EncodingUiState CaptureEncodingUiState() => new(
        Format: CurrentFormat(),
        // 增益图态必然出 HDR：开关就是"HDR 的 JPEG 载体"，与面板上把 HDR 置灰强制打开是同一口径，
        // 在这里再兜一次，避免任何没走 UI 那条路径的入口（粘贴/剪贴板另存）漏掉。
        HdrEnabled: (HdrSwitch.IsOn && HdrSwitch.IsEnabled) || CurrentFormat() == OutputFormat.JPEG_GAINMAP,
        ColorSpaceTag: GetSelectedColorSpaceTag(),
        Quality: (float)QualitySld.Value,
        AvifPngSuffix: AvifPngSuffixChk.IsChecked == true,
        JxlPngSuffix: JxlPngSuffixChk.IsChecked == true,
        Settings: _settings.Snapshot());

    /// <summary>构建编码设置（委托给 CapturePipelineService，减少重复逻辑）。
    /// <para><paramref name="ui"/> 必须是 UI 线程采集的快照（见 <see cref="CaptureEncodingUiState"/>）；
    /// 本方法可能运行在后台编码线程，不得直接访问任何 WinUI 控件。</para></summary>
    /// <summary>
    /// 为<b>没有</b> <see cref="CaptureResult"/> 的入口（粘贴/预览/剪贴板另存）按光标屏构造色彩决定。
    /// <para>有捕获结果的入口一律走 <see cref="DecisionFor"/>，用被截那块屏 —— 光标屏顶替源屏是
    /// 2026-09-22 审查轮记在案的缺口（交接 §41.4）。这里保留光标语义只是因为这些入口本来就没有源屏概念，
    /// 且它与"用户正在看哪块屏"在这些场景下确实同义。全窗口只有这一处猜光标屏。</para>
    /// </summary>
    private ColorDecision DecisionForCursorScreen(EncodingUiState ui, bool hdrOutput)
        => DecisionForDisplay(null, ui, hdrOutput, ui.Format);

    /// <summary>色彩决定按<b>给定的那块屏</b>算一次；<paramref name="source"/> 为 null 才退回光标屏。
    /// <para>像素在进程外绕了一圈回来的入口（VS-Cap 侧车）手里可能带着源屏（截图路），也可能真没有
    /// （剪贴板贴图路）。两条路必须共用同一个 <c>Build</c> 调用点，且 <paramref name="format"/> 由调用方给
    /// —— 侧车工具栏点名要 PNG/AVIF，跟 <c>ui.Format</c> 不是一回事，用后者会让决定与容器不同源。</para></summary>
    private ColorDecision DecisionForDisplay(DisplayInfo? source, EncodingUiState ui, bool hdrOutput, OutputFormat format)
        => ColorDecision.Build(
            source ?? DisplayEnumerator.FindDisplayByMonitor(DisplayEnumerator.GetMonitorUnderCursor()),
            ui.ColorSpaceTag, hdrOutput, ui.Settings.AcmeDetected, format);

    /// <summary>有捕获结果时：色彩决定按<b>被截那块屏</b>算一次，像素路与声明路共用同一份。
    /// <para><paramref name="format"/> 是本次要落盘的那个格式，必须由调用方给：JPEG 家族 / WebP
    /// 的"默认档 = Rec.709"是<b>格式口径</b>（Core 的 <c>ColorDecision.DefaultsToRec709</c>），
    /// 与源屏证据无关。每个调用点手里的 <c>format</c> 都是同一个 <c>ui.Format</c> 快照 ——
    /// 也正是随后 BuildEncodingSettings 收下的那一个 ⇒ 决定与容器同源。</para></summary>
    private static ColorDecision DecisionFor(CaptureResult captureResult, string? tag, bool hdrOutput,
        bool acm, OutputFormat format)
        => ColorDecision.Build(captureResult.SourceDisplay, tag, hdrOutput, acm, format);

    private EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta,
        EncodingUiState ui, ColorDecision? decision = null)
    {
        // ⚠ 位深/色度/AVIF 编码器选择/每格式质量等参数由管线从【快照】里读，不能再给它活的设置对象：
        //   本方法常在后台编码线程执行，而 UI 事件会在这一秒内就地改写字段（原实现正是如此）。
        var settings = AppServices.Pipeline.BuildEncodingSettings(
            format, hdrOutput, meta, ui.Settings, ui.ColorSpaceTag, ui.Settings.AcmeDetected, decision);
        // 覆盖 UI 特有的设置（全部取自快照，不读控件）
        settings.Quality = ui.Quality;
        LogService.Info("MainWindow",
            $"BuildEncodingSettings: 质量={settings.Quality:F2} 色域={ui.ColorSpaceTag} AVIF编码器={settings.AvifEncoderKind}");
        settings.AvifPngSuffix = ui.AvifPngSuffix;
        settings.JxlPngSuffix = ui.JxlPngSuffix;
        return settings;
    }

    // ── 快捷键录制 ──

    private bool _uiReady; // InitializeComponent 完成后才响应 UI 事件

    /// <summary>
    /// 质量面板正在被<b>程序</b>刷新（启动、切格式、切 HDR、切编码器）。
    /// <para>为什么必须有它：<c>RangeBase</c> 在赋 <c>Minimum</c>/<c>Maximum</c> 时会就地钳制
    /// <c>Value</c> 并抛 <c>ValueChanged</c>，而 <c>OnQualityChanged</c> 把每一次 ValueChanged
    /// 都当成"用户拖了滑块"去写 <c>_settings.SetQuality(...)</c>。于是
    /// <c>UpdateQualityPanelCore</c> 后半段"从 _settings 读回用户值"（<c>GetQuality</c>）
    /// 读到的已经是它自己前半段写进去的钳制结果 —— 实机：存 <c>QualityGainMap=2.0</c>
    /// （区间 0.5–3.0 的中段，既非端点也越界），启动后界面与文件双双变成 0.5。
    /// 一个方法的前半段把自己的输入改掉了，这正是"靠调用顺序才正确"的那类隐性耦合。</para>
    /// <para>与 <c>_avifOptionsUpdating</c> 同一手法：只挡程序赋值，用户拖动时该标志恒为 false。</para>
    /// </summary>
    private bool _qualityPanelUpdating;

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
            else if (cb.Name == nameof(PngFallbackChk)) _settings.PngFallbackEnabled = cb.IsChecked == true;
            else if (cb.Name == nameof(PreviewChk)) _settings.ShowPreview = cb.IsChecked == true;
            else if (cb.Name == nameof(VSCapModeChk)) { _settings.UseVSCap = cb.IsChecked == true; ApplyVSCapGate(); }
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
            // ═══ 2026-09-17 修复：必须写 ChromaAvif（管线读取的字段）═══
            // 原写 AvifChroma，而 CapturePipelineService 读的是 ChromaAvif
            // ⇒ 改了色度后实时保存不生效（要等到全量 SaveSettings 才写入），
            //   且界面回显读的又是 AvifChroma，看起来"改成功了"——最难察觉的一类失效。
            else if (cb.Name == nameof(AvifChromaCbo)) _settings.ChromaAvif = tag;
            else if (cb.Name == nameof(ChromaJpegXlCbo)) _settings.ChromaJpegXl = tag;
            else if (cb.Name == nameof(ChromaJpegLiCbo)) _settings.ChromaJpegLi = tag;
            // ═══ 2026-10-07：压缩强度三档（AVIF / JPEG XL / WebP）═══
            // ⚠ 必须写管线真正读的那三个字段（EffortAvif / EffortJpegXl / EffortWebp）——
            //   写个同义的别名字段就会重现上面 ChromaAvif 那次"改成功了但不生效"的失效。
            //   写坏值不抛：CompressionEffortMap.Parse 回落到均衡档，不会变成最慢档。
            else if (cb.Name == nameof(EffortAvifCbo)) _settings.EffortAvif = tag;
            else if (cb.Name == nameof(EffortJpegXlCbo)) _settings.EffortJpegXl = tag;
            else if (cb.Name == nameof(EffortWebpCbo)) _settings.EffortWebp = tag;
            // JPEG LI 与增益图共用这一栏（管线对 JPEG_GAINMAP 也读 EffortJpegLi）
            else if (cb.Name == nameof(EffortJpegLiCbo)) _settings.EffortJpegLi = tag;
            // 2026-09-26：原 GainMapChromaCbo 分支已删除 —— 增益图并入 JPEG LI 后，
            //   色度就是上面 ChromaJpegLiCbo 那一条（管线对 JPEG_GAINMAP 也改读 ChromaJpegLi）。
            //   两个下拉写同一个字段 = 刚被合并掉的那种分裂。
            // 2026-09-19：原 nameof(ChromaWebPCbo) 分支已删除 —— WebP 色度下拉撤掉了
            //   （有损 WebP 恒 4:2:0 由格式定义，cwebp 侧没有色度参数 ⇒ 选了也不生效）。
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

    /// <summary>
    /// Win32 <c>FLASHWINFO</c> 的逐字镜像。<b>首字段必须是 cbSize</b>，且要等于结构体大小。
    /// <para>这里原先漏了 cbSize，直接从 hWnd 开始 ⇒ 整块自 offset 0 错位：系统把句柄低位
    /// 当成 cbSize、把它自己 offset 8 处的 hwnd 读成我写的 dwFlags/uCount、
    /// 把它 offset 16 处的 dwFlags 读成 0（= FLASHW_STOP，"停止闪烁"），
    /// 并按 32 字节去读一个 24 字节的结构 ⇒ 越界多读 8 字节。
    /// 结果是 FlashWindowEx 在任何机器上都必返 FALSE，而 FALSE 被 `_ =` 吞掉
    /// ——这条改动此前被记成"本机不可验"，实际是【改动本身无效】，与硬件无关。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWinInfo
    {
        public uint cbSize;
        public nint hWnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWinInfo pwfi);

    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。
    /// 在窗口过程（UI 线程）中执行，可直接操作 WinUI 控件。</summary>
    private void ActivateFromOtherInstance()
    {
        try
        {
            // 这行原本是 Debug ⇒ 默认 Brief 档根本不落盘，"广播没收到"与"收到了但被前台锁拒绝"
            // 这两种故障在日志里长得一模一样。要判 F3 必须先能区分它们。
            LogService.Info("MainWindow", "收到单实例激活广播");
            DispatcherQueue.TryEnqueue(() =>
            {
                // 最小化/隐藏时先恢复
                if (AppWindow.Presenter is OverlappedPresenter p &&
                    p.State == OverlappedPresenterState.Minimized)
                    p.Restore();
                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();
                // 即使发送侧已经 AllowSetForegroundWindow，系统仍可能拒绝后台进程的前台切换。
                // 闪任务栏是文档允许的兜底：抢不到前台时至少让用户知道该点哪里 ——
                // "没抢到"和"什么都没发生"对用户是两回事。
                var fhwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var fi = new FlashWinInfo
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashWinInfo>(),
                    hWnd = fhwnd,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 0,          // 带 FLASHW_TIMERNOFG 时系统忽略 uCount
                    dwTimeout = 0,       // 0 ⇒ 用光标闪烁频率
                };
                // 返回值必须落盘：它就是"这处改动到底生效没有"的唯一焦点无关读数。
                // 修结构体之前它恒为 False（且没人看），修之后应当为 True。
                var flashed = FlashWindowEx(ref fi);
                LogService.Info("MainWindow",
                    $"FlashWindowEx ok={flashed} cbSize={fi.cbSize} hwnd=0x{fhwnd:X}");
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

            // 源色域与映射说明：一次刷新、一份决定（换屏后光标屏的分类可能整个变档）
            DispatcherQueue.TryEnqueue(RefreshColorMappingUi);
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
        // ⚠ 图标整棵子树都不进 —— 这是报告 §11-② 追了三天的根因，10-03 用三臂实测定死。
        //   FontIcon / SymbolIcon 派生自 Control，而字形是靠它【模板里那个内部 TextBlock】画的；
        //   这条 sweep 跑在 Loaded 上、写局部值（优先级高于样式），一路递归下去就把那个
        //   TextBlock 的字体换成界面字体（默认回退链 Microsoft YaHei, Segoe UI, sans-serif，
        //   里面没有 PUA 码位）⇒ 整窗图标全成豆腐块。
        //   三臂读数（同一枚 FontIcon，Segoe Fluent Icons U+E713）：
        //     ① 无守卫 → 方框            font-probe-before.png
        //     ② 只跳过 IconElement 本身、仍往下递归 → 仍是方框   font-probe-after.png
        //        那一臂同时量到图标自己的 FontFamily.Source 已经是 "Segoe Fluent Icons"
        //        （日志 FontProbe 14:10:26）⇒ 属性值没被改，坏在渲染它的那个内部 TextBlock。
        //        只看①②会把根因判错；是②这个"修了但没修对"的读数指到了模板内部。
        //     ③ 整棵子树 return → 真齿轮  font-probe-subtree.png
        //   这也解释了为什么 8 种 XAML 字体寻址全部失败：寻址写的是图标那一层，
        //   而每一臂都被 sweep 递归压到了里面那一层 TextBlock 上。
        if (parent is Microsoft.UI.Xaml.Controls.IconElement)
            return;   // 连子树一起跳过：只跳过 IconElement 本身等于没修
        else if (parent is Microsoft.UI.Xaml.Controls.Control ctrl)
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
