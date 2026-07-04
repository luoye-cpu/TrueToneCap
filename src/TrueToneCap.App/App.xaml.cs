using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace TrueToneCap.App;

public partial class App : Application
{
    private static Mutex? s_mutex;

    [DllImport("user32.dll")] static extern int MessageBoxW(nint h, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint FindWindowW(string? lpClassName, string lpWindowName);

    public App()
    {
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 提升进程优先级以减少截图延迟
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; } catch { }

        var window = new MainWindow();
        try
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 820));
        }
        catch { }
        window.Activate();
    }
}
