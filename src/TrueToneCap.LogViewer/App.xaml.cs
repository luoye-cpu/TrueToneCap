// TrueToneCap.LogViewer/App.xaml.cs
using Microsoft.UI.Xaml;

namespace TrueToneCap.LogViewer;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 深色主题: 通过 MainWindow 资源设置 (Application.RequestedTheme 有兼容问题)
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            // 诊断: 写启动错误文件 (窗口失败时唯一可见途径)
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
                File.WriteAllText(Path.Combine(dir, "viewer_error.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] 启动失败 HR=0x{ex.HResult:X8}: {ex}\n");
            }
            catch { }
        }
    }
}

