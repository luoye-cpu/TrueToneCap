// TrueToneCap.App/WindowPreviewTooltip.xaml.cs
// 窗口预览提示框（QQ 截图式）- 跟随鼠标、延迟显示

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using WinRT.Interop;
using TrueToneCap.Core.Detection;

namespace TrueToneCap.App;

public sealed partial class WindowPreviewTooltip : Window
{
    private bool _isVisible;
    private nint _hwnd;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020; // 鼠标穿透
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    public WindowPreviewTooltip()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // 标题栏透明 + 扩展内容
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // 工具窗口 + 置顶 + 不抢焦点 + 鼠标穿透（事件透传到下方窗口）
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT);

        // 设置窗口大小
        try { appWindow.Resize(new SizeInt32(300, 200)); } catch { }

        // 初始隐藏
        ShowWindow(_hwnd, SW_HIDE);

        Closed += (_, _) => _isVisible = false;
    }

    /// <summary>根据鼠标位置计算最优预览窗口位置（智能避让屏幕边缘）。</summary>
    private static RectInt32 CalculatePreviewPosition(
        double screenX, double screenY, DisplayArea displayArea, int pw, int ph)
    {
        var wa = displayArea.WorkArea;
        int x = (int)screenX + 16;
        int y = (int)screenY + 16;

        // 右侧超出 → 左侧
        if (x + pw > wa.X + wa.Width)
            x = (int)screenX - pw - 16;
        if (x < wa.X)
            x = wa.X;

        // 底部超出 → 顶部
        if (y + ph > wa.Y + wa.Height)
            y = (int)screenY - ph - 16;
        if (y < wa.Y)
            y = wa.Y;

        return new RectInt32(x, y, pw, ph);
    }

    /// <summary>显示窗口预览。</summary>
    public void ShowPreview(DetectedRegion region, double screenX, double screenY)
    {
        // 渲染缩略图
        bool hasThumbnail = false;
        if (region.ThumbnailPixels is not null && region.ThumbnailPixels.Length == 120 * 80 * 4)
        {
            try
            {
                var wb = new WriteableBitmap(120, 80);
                using (var stream = wb.PixelBuffer.AsStream())
                    stream.Write(region.ThumbnailPixels, 0, region.ThumbnailPixels.Length);
                wb.Invalidate();
                ThumbnailImage.Source = wb;
                ThumbnailImage.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                hasThumbnail = true;
            }
            catch
            {
                ThumbnailImage.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
        else
        {
            ThumbnailImage.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        // 文本
        TitleText.Text = string.IsNullOrEmpty(region.Title) ? "未知窗口" : region.Title;
        SizeText.Text = $"{region.Width} × {region.Height}";

        // 定位（智能避让）
        int pw = 300, ph = hasThumbnail ? 200 : 80;
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                var rect = CalculatePreviewPosition(screenX, screenY, displayArea, pw, ph);
                AppWindow.MoveAndResize(rect);
            }
        }
        catch { }

        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _isVisible = true;
    }

    /// <summary>隐藏预览。</summary>
    public void HidePreview()
    {
        ShowWindow(_hwnd, SW_HIDE);
        ThumbnailImage.Source = null;
        _isVisible = false;
    }

    /// <summary>是否正在显示。</summary>
    public bool IsPreviewVisible => _isVisible;
}
