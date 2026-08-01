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
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string? lpClassName, string lpWindowName);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(int value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    // 程序化强制设置，确保不被 manifest 忽略或系统兼容性覆盖
    private const int DPI_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public App()
    {
        LogService.InitializeFileLog();
        LogService.Info("App", "应用启动开始");

        // ── 强制逐显示器 V2 DPI 感知（必须在任何窗口创建前调用）──
        SetProcessDpiAwarenessContext(DPI_CONTEXT_PER_MONITOR_AWARE_V2);
        LogService.Debug("App", "DPI 感知设置完成");

        // ── 单实例检测 ──
        s_mutex = new Mutex(true, @"Global\TrueToneCap_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            LogService.Info("App", "已有实例运行，激活窗口并退出");
            // 已有实例运行 → 尝试激活已有窗口
            try
            {
                nint hwnd = FindWindowW(null, "TrueToneCap 设置");
                if (hwnd != nint.Zero) SetForegroundWindow(hwnd);
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

            // WinAppSDK 非打包模式已知非致命警告：主题资源 URI 解析失败
            if (msg.Contains("themeresources.xaml", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Cannot locate resource", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[App] 非致命 XAML 资源警告 (已忽略): {msg}");
                e.Handled = true;
                return;
            }

            var fullMsg = $"TrueToneCap 崩溃:\n\n{msg}\n\n{e.Exception?.StackTrace}";
            LogService.Error("App", $"未处理异常: {msg}", e.Exception);
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
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "TrueToneCap.settings.json");
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
        LogService.Info("App", $"TrueToneCap 启动 v0.2.0, OS={Environment.OSVersion}, 进程提升优先级=High");
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
