// TrueToneCap.App/SilentCaptureToast.xaml.cs
// 无感截图完成通知 - 右下角可关闭提示

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace TrueToneCap.App;

public sealed partial class SilentCaptureToast : Window
{
    private DispatcherTimer? _autoCloseTimer;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    public SilentCaptureToast()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // 扩展内容到标题栏（消除深色模式顶部白条）
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // 设置窗口大小
        appWindow.Resize(new SizeInt32(320, 80));

        // 设置为工具窗口 + 置顶 + 不抢焦点
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);

        // Mica 背景（如果可用）
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { }

        // 定位到右下角
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (displayArea is not null)
        {
            var workArea = displayArea.WorkArea;
            var x = workArea.X + workArea.Width - 340;
            var y = workArea.Y + workArea.Height - 100;
            appWindow.Move(new PointInt32(x, y));
        }

        // 5秒后自动关闭
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoCloseTimer.Tick += (_, _) => Close();
        _autoCloseTimer.Start();
    }

    public void SetContent(string title, string body)
    {
        TitleTxt.Text = title;
        BodyTxt.Text = body;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }
}
