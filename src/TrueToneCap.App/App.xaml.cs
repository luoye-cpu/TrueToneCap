using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Services;

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

    [DllImport("user32.dll")] static extern int MessageBoxW(nint h, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint FindWindowW(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(int value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    // 程序化强制设置，确保不被 manifest 忽略或系统兼容性覆盖
    private const int DPI_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public App()
    {
        // ── 强制逐显示器 V2 DPI 感知（必须在任何窗口创建前调用）──
        SetProcessDpiAwarenessContext(DPI_CONTEXT_PER_MONITOR_AWARE_V2);

        // ── 单实例检测 ──
        s_mutex = new Mutex(true, @"Global\TrueToneCap_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
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

        // 仅当用户明确选择了 Light/Dark/OLED 时才设定 RequestedTheme
        // Default（跟随系统）→ 不设置，让 WinUI 自动跟随 Windows 主题
        if (initTheme == AppThemeMode.Light)
            RequestedTheme = ApplicationTheme.Light;
        else if (initTheme == AppThemeMode.Dark || initTheme == AppThemeMode.OLED)
            RequestedTheme = ApplicationTheme.Dark;
        // else: Default → 不设置 RequestedTheme，跟随系统

        // ── 加载内嵌字体（优先于 XAML 初始化）──
        FontLoader.LoadBundledFonts();

        // ── 初始化 OCR 引擎（纯内嵌 ONNX + Windows，零外部依赖）──
        _ = Task.Run(() => { try { MultiOcrService.Initialize(); } catch { } });

        this.InitializeComponent();
        this.UnhandledException += (s, e) =>
        {
            var msg = $"TrueToneCap 崩溃:\n\n{e.Exception?.Message}\n\n{e.Exception?.StackTrace}";
            try
            {
                var crashPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TrueToneCap", "crash.log");
                var dir = Path.GetDirectoryName(crashPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(crashPath, msg);
            }
            catch { }
            try { MessageBoxW(0, msg, "TrueToneCap 错误", 0x10); } catch { }
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

    /// <summary>切换应用主题（运行时）。不注入动态资源，完全依赖 fe.RequestedTheme + WinUI 内置主题。</summary>
    public static void ApplyTheme(AppThemeMode mode)
    {
        _currentTheme = mode;
        // 所有主题视觉效果由窗口级 fe.RequestedTheme 控制，不注入动态资源字典
    }

    /// <summary>从 settings.json 加载主题设置（用于构造函数，在 InitializeComponent 前调用）。</summary>
    private static AppThemeMode LoadThemeFromSettings()
    {
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "settings.json");
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
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; } catch { }

        // ── 使用构造函数中已设置的 _currentTheme（RequestedTheme 已在此之前设置）──
        var initTheme = _currentTheme;

        // ── 解析命令行参数 ──
        bool isAutostart = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(isAutostart);

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

        // ── 初始主题资源注入（窗口创建后，统一由 ApplyTheme 管理）──
        ApplyTheme(initTheme);
    }
}
