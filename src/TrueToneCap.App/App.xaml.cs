using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Services;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.App;

/// <summary>应用主题模式。</summary>
public enum AppThemeMode
{
    /// <summary>跟随系统。</summary>
    Default,
    /// <summary>浅色（全白）。</summary>
    Light,
    /// <summary>深色（全黑）。</summary>
    Dark,
    /// <summary>OLED 纯黑。</summary>
    OLED
}

public partial class App : Application
{
    private static Mutex? s_mutex;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint h, string text, string caption, uint type);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
    // ⚠ 必须显式指定 W 后缀入口点：user32.dll 只导出 RegisterWindowMessageA / W，
    // 没有无后缀版本。默认按方法名解析会失败并抛 EntryPointNotFoundException，
    // 该异常发生在 App 构造函数早期（未处理异常处理器尚未生效）→ WinUI 捕获后
    // FailFast (0xC000027B)，进程以 0xC0000409 退出，表现为"启动即崩溃"。
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string lpString);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SendMessageTimeout(nint hWnd, uint msg, nuint wParam, nint lParam,
        uint fuFlags, uint uTimeout, out nint lpdwResult);
    // ⚠ DPI_AWARENESS_CONTEXT 是指针尺寸类型（x64 下 8 字节），不能声明为 int。
    // 用 int 传递时 RCX 高 32 位被零扩展，得到无效上下文句柄。
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    private const nuint HWND_BROADCAST = 0xFFFF;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>自定义跨进程消息：请求已运行的实例激活其主窗口。
    /// <para>
    /// RegisterWindowMessage 保证不同进程解析出相同的消息 ID。
    /// 用广播代替 <c>FindWindow(null, "TrueToneCap 设置")</c>：后者依赖本地化后的窗口标题，
    /// 界面切换为英文后标题变为 "TrueToneCap Settings"，查找失败 → 单实例激活静默失效。
    /// </para>
    /// </summary>
    internal static uint ActivateMessage { get; private set; }

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    // 程序化强制设置，确保不被 manifest 忽略或系统兼容性覆盖
    private const nint DPI_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public App()
    {
        LogService.InitializeFileLog();
        LogService.Info("App", "应用启动开始");

        // ═══ P0: 进程级异常兜底 ═══
        // WinUI 的 Application.UnhandledException 只能捕获 XAML 消息循环线程的异常；
        // async void 方法 (SilentCapture/StartSelectionCapture/OnCaptureNow 等)、
        // 线程池线程的未捕获异常会绕过它 → 直接 FailFast (0xC000027B)。
        // 此处注册 AppDomain 级兜底，把所有濒死异常写入 crash.log 后再决定去留。
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null)
                {
                    LogService.Error("App", $"AppDomain 未处理异常 (IsTerminating={e.IsTerminating}): {ex.Message}", ex);
                    WriteCrashLog("AppDomain", ex.ToString());
                }
                else
                {
                    WriteCrashLog("AppDomain", e.ExceptionObject.ToString() ?? "(unknown)");
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                var agg = e.Exception;
                var ex = agg?.GetBaseException();
                if (agg != null && ex != null)
                {
                    LogService.Error("App", $"未被观察的 Task 异常: {ex.Message}", agg);
                    e.SetObserved();
                }
            };
        }
        catch { } // 注册失败不影响启动

        // ── 强制逐显示器 V2 DPI 感知（必须在任何窗口创建前调用）──
        SetProcessDpiAwarenessContext(DPI_CONTEXT_PER_MONITOR_AWARE_V2);
        LogService.Debug("App", "DPI 感知设置完成");

        // ── 单实例检测 ──
        // 注册激活消息需在可能广播之前完成
        ActivateMessage = RegisterWindowMessage("TrueToneCap_ActivateInstance_v1");
        s_mutex = new Mutex(true, @"Global\TrueToneCap_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            LogService.Info("App", "已有实例运行，激活窗口并退出");
            // 已有实例运行 → 尝试激活已有窗口
            // ⚠ 不能按窗口标题查找：标题随界面语言变化（中文"TrueToneCap 设置"/
            // 英文"TrueToneCap Settings"），切换语言后单实例激活会静默失效。
            // 改为向所有顶级窗口广播自定义消息，由主窗口自报身份。
            try
            {
                BroadcastActivateExistingInstance();
            }
            catch { }
            s_mutex.Dispose();
            Environment.Exit(0);
            return;
        }

        // ── 初始化主题（必须在 InitializeComponent 之前设置 RequestedTheme）──
        var initTheme = LoadThemeFromSettings();
        _currentTheme = initTheme;
        LogService.Info("App", $"主题初始化: {initTheme} → {ResolveEffectiveTheme(initTheme)}");

        // WinUI 3 非打包应用不设置 RequestedTheme 时默认深色，不会自动跟随系统
        // 因此 Default 模式下必须主动检测系统主题并显式设置
        var effectiveTheme = ResolveEffectiveTheme(initTheme);
        RequestedTheme = effectiveTheme is AppThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        // ── 着色器预热：后台静默编译缺失的 CSO（最早时机，不阻塞启动）──
        _ = Task.Run(() =>
        {
            try
            {
                var shaderDir = Path.Combine(AppContext.BaseDirectory, "data", "Shaders");
                LogService.Info("App", "着色器预热启动");
                ShaderCompiler.EnsureCompiled(shaderDir, shaderDir);
                LogService.Info("App", "着色器预热完成");
            }
            catch (Exception ex) { LogService.Warn("App", $"着色器预热异常: {ex.Message}"); }
        });

        // ── 初始化 OCR 引擎（纯内嵌 ONNX + Windows，零外部依赖）──
        _ = Task.Run(() => { try { MultiOcrService.Initialize(); LogService.Info("App", "OCR 引擎后台初始化"); } catch (Exception ex) { LogService.Warn("App", $"OCR 初始化异常: {ex.Message}"); } });

        // ── 初始化 jpegli 编码器（jxl.dll）──
        _ = Task.Run(() =>
        {
            try { JpegLiNative.Initialize(); LogService.Info("App", "jpegli 编码器初始化"); }
            catch (Exception ex) { LogService.Warn("App", $"jpegli 初始化失败: {ex.Message}"); }
        });

        this.InitializeComponent();

        // ── 安全加载 WinUI 主题资源（代码中 try-catch，避免 XAML 期间原生崩溃）──
        try
        {
            Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] XamlControlsResources 加载失败 (非致命): {ex.Message}");
        }

        // ── 注册全局异常处理器 ──
        this.UnhandledException += (s, e) =>
        {
            var msg = e.Exception?.Message ?? "";
            CountStartupFailure();

            // WinAppSDK 非打包模式已知非致命警告：主题资源 URI 解析失败
            if (msg.Contains("themeresources.xaml", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Cannot locate resource", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[App] 非致命 XAML 资源警告 (已忽略): {msg}");
                e.Handled = true;
                return;
            }

            var fullMsg = $"TrueToneCap 崩溃:\n\n{msg}\n\n{e.Exception?.StackTrace}";
            LogService.Error("App", $"未处理异常: {msg}", e.Exception ?? new Exception(msg));

            // ══ 反复崩溃保护 ══
            // 之前无条件 e.Handled = true，导致进程带着可能已损坏的状态继续运行：
            // 例如渲染线程每帧抛同一异常时，用户只看到 MessageBox 风暴而程序已失去功能。
            // 此处统计失败次数，超过阈值则记录后让进程退出（由 Windows 错误报告接管）。
            if (ShouldAbortOnRepeatedFailure())
            {
                LogService.Error("App",
                    $"短时间内未处理异常已达 {FailureThreshold} 次，进程将退出以避免在损坏状态下继续运行");
                try { MessageBoxW(0, "TrueToneCap 反复发生错误，即将退出。\n\n详情见 %LOCALAPPDATA%\\TrueToneCap\\crash.log", "TrueToneCap 错误", 0x10); }
                catch { }
                Environment.FailFast("TrueToneCap: 反复发生未处理异常", e.Exception ?? new Exception(msg));
            }

            try
            {
                var crashPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TrueToneCap", "crash.log");
                var dir = Path.GetDirectoryName(crashPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(crashPath, fullMsg);
            }
            catch { }
            try { MessageBoxW(0, fullMsg, "TrueToneCap 错误", 0x10); } catch { }
            e.Handled = true;
        };
    }

    // ══ 反复崩溃保护 ══
    // 未处理异常可能来自每帧执行的渲染循环：此时"忽略并继续"会让程序在已损坏的
    // 状态下空转（黑屏/静默失败），用户无从判断。故统计时间窗内的失败次数，
    // 达到阈值即主动退出，由系统错误报告留下现场。
    private const int FailureThreshold = 5;
    private static readonly TimeSpan s_failureWindow = TimeSpan.FromSeconds(30);
    private static int s_failureCount;
    private static DateTime s_firstFailureAt;

    private static void CountStartupFailure()
    {
        var now = DateTime.UtcNow;
        if (s_failureCount == 0 || now - s_firstFailureAt > s_failureWindow)
        {
            s_firstFailureAt = now;      // 超出时间窗则重新计数
            s_failureCount = 0;
        }
        Interlocked.Increment(ref s_failureCount);
    }

    private static bool ShouldAbortOnRepeatedFailure() =>
        Volatile.Read(ref s_failureCount) >= FailureThreshold;

    /// <summary>把濒死异常详情追加写入 crash.log（供事后排查）。</summary>
    private static void WriteCrashLog(string source, string detail)
    {
        try
        {
            var crashPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "crash.log");
            var dir = Path.GetDirectoryName(crashPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{detail}\n\n";
            File.AppendAllText(crashPath, line);
        }
        catch { }
    }

    /// <summary>向所有顶级窗口广播"激活"消息，由已运行实例的主窗口响应并前置。
    /// 使用 SendMessageTimeout 而非 SendMessage：避免某个无响应的窗口拖住本进程退出。</summary>
    private static void BroadcastActivateExistingInstance()
    {
        if (ActivateMessage == 0) return;
        SendMessageTimeout((nint)HWND_BROADCAST, ActivateMessage, 0, 0,
            SMTO_ABORTIFHUNG | SMTO_NORMAL, 1500, out _);
    }

    private static AppThemeMode _currentTheme = AppThemeMode.Default;
    public static AppThemeMode CurrentTheme => _currentTheme;

    /// <summary>检测 Windows 系统是否为深色主题。</summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1=浅色, 0=深色
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; } // 默认深色
    }

    /// <summary>将 Default 模式解析为实际主题（跟随系统）。</summary>
    public static AppThemeMode ResolveEffectiveTheme(AppThemeMode mode)
    {
        if (mode == AppThemeMode.Default)
            return IsSystemDarkTheme() ? AppThemeMode.Dark : AppThemeMode.Light;
        return mode;
    }

    /// <summary>切换应用主题（运行时）。设置 Application.RequestedTheme 全局生效。</summary>
    public static void ApplyTheme(AppThemeMode mode)
    {
        _currentTheme = mode;
        var effective = ResolveEffectiveTheme(mode);
        if (Current is App app)
        {
            app.RequestedTheme = effective == AppThemeMode.Light
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
        }
    }

    /// <summary>从 settings.json 加载主题设置（用于构造函数，在 InitializeComponent 前调用）。</summary>
    /// <remarks>注意: 必须与 SettingsService 的保存路径一致，即 AppContext.BaseDirectory。</remarks>
    private static AppThemeMode LoadThemeFromSettings()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "TrueToneCap.settings.json");
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ThemeMode", out var themeProp))
                {
                    return themeProp.GetString() switch
                    {
                        "Light" => AppThemeMode.Light,
                        "Dark" => AppThemeMode.Dark,
                        "OLED" => AppThemeMode.OLED,
                        _ => AppThemeMode.Default,
                    };
                }
            }
        }
        catch { }
        return AppThemeMode.Default;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 提升进程优先级以减少截图延迟
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; LogService.Info("App", "进程优先级已提升为 High"); } catch { }
        // 从程序集读取版本（避免每次发布忘记更新硬编码字符串）
        string ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        LogService.Info("App", $"TrueToneCap 启动 v{ver}, OS={Environment.OSVersion}, 进程提升优先级=High");
        LogService.Info("App", $"命令行: {string.Join(" ", Environment.GetCommandLineArgs())}");
        // ── 初始化应用服务（Settings / Capability / Pipeline / WGC / GPU）──
        LogService.Info("App", "初始化应用服务 (DI 容器)...");
        AppServices.Initialize();
        LogService.Info("App", "应用服务初始化完成");

        // ── 使用构造函数中已设置的 _currentTheme（RequestedTheme 已在此之前设置）──
        var initTheme = _currentTheme;

        // ── 解析命令行参数 ──
        bool isAutostart = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(isAutostart);
        LogService.Info("App", $"主窗口已创建, 自动启动={isAutostart}");

        // ── 显式设置窗口内容主题（Application.RequestedTheme 可能不被所有控件继承）──
        var effective = ResolveEffectiveTheme(initTheme);
        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = effective switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark or AppThemeMode.OLED => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        if (!isAutostart)
        {
            try
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1260, 840));
            }
            catch { }
            window.Activate();
        }
        // 开机启动：不显示窗口，由 MainWindow 构造函数中自动缩放到托盘
        LogService.Info("App", isAutostart ? "开机自启动模式，不显示窗口" : "窗口已激活");
    }
}
