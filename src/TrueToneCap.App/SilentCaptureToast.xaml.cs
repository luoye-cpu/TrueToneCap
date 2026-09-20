// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/SilentCaptureToast.xaml.cs
// 应用内通知浮层 —— 支持四角定位（BottomRight / TopRight / TopLeft / BottomLeft / WindowsNotify）

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Logging;   // LogCategory

namespace TrueToneCap.App;

/// <summary>通知语义：决定图标与配色。</summary>
public enum ToastTone
{
    Success,   // 成功（绿）
    Warning,   // 警告/降级（黄）
    Error,     // 失败（红）
    Info       // 普通信息（蓝）
}

public sealed partial class SilentCaptureToast : Window
{
    private DispatcherTimer? _autoCloseTimer;
    private string? _filePath;

    // ═══ 多通知堆叠 ═══
    // 连续截图时若都定位到同一坐标，后一条会完全盖住前一条，
    // 用户会以为只弹了一次。参考 ShareX / Windows 通知中心：按槽位纵向错开，
    // 并限制同时显示的数量，超出则关闭最旧的。
    private static readonly List<SilentCaptureToast> s_active = new();
    private const int MaxVisible = 3;
    private const int Gap = 8;

    private AppWindow? _appWindow;
    private Microsoft.UI.WindowId _windowId;
    private string _position = "BottomRight";

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    // 去掉系统标题栏（及其最小/最大化/关闭按钮）：
    // 卡片自带关闭按钮，系统的那排按钮既多余又会挤占内容区顶部。
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;

    // ═══ 尺寸：固定 360×116 ═══
    // 曾改为"按内容测量自适应"，但实测在 Loaded 时 Measure 会返回 0，
    // 窗口被压到最小高度后标题栏按钮占掉大半、内容完全看不见。
    // 通知内容规模可控（标题 ≤2 行 + 正文 ≤3 行），固定高度更可靠。
    // "大窗口套小横幅"的真正原因不在窗口尺寸，而在卡片内部布局（见 XAML 的 RowDefinitions）。
    private const int ToastW = 480;      // 原 360 偏窄，长文件名/路径会被截断显示不全
    private const int ToastH = 144;      // 含操作区（"打开文件夹"）的高度
    private const int Margin = 16;       // 距屏幕边缘


    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    /// <summary>创建 Toast 通知窗口。</summary>
    /// <param name="position">通知位置: BottomRight / TopRight / TopLeft / BottomLeft / WindowsNotify</param>
    public SilentCaptureToast(string position = "BottomRight")
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // ═══ 无边框 / 无标题栏：用 OverlappedPresenter 的标准做法 ═══
        //
        // 前两版都错了：
        //   ① ExtendsContentIntoTitleBar = true —— 只是把内容"扩展"到标题栏区域，
        //      标题栏那块（含按钮）依然存在并压在内容上方；
        //   ② 手动清 WS_CAPTION / WS_SYSMENU —— WinUI 仍会保留一块标题栏区域，
        //      结果就是"文字被遮住一半"。
        // 正确做法：让 presenter 明确声明"不要边框、不要标题栏"，由 WinUI 自己把
        // 客户区完整交还给内容。
        try
        {
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsAlwaysOnTop = true;    // 置顶
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            appWindow.SetPresenter(presenter);
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"presenter 设置失败（非致命）: {ex.Message}", LogCategory.UI);
        }

        appWindow.Resize(new SizeInt32(ToastW, ToastH));

        // 辅助：工具窗口（不出现在任务栏）+ 不抢焦点
        try
        {
            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"窗口样式设置失败（非致命）: {ex.Message}", LogCategory.UI);
        }

        // Mica 背景（如果可用）
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { }

        _appWindow = appWindow;
        _windowId = windowId;
        _position = position;

        // 入列并定位（槽位 = 当前在列表中的序号）
        lock (s_active)
        {
            while (s_active.Count >= MaxVisible)
            {
                var oldest = s_active[0];
                s_active.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }
            s_active.Add(this);
        }
        PlaceAt(s_active.IndexOf(this));

        // 关闭后让剩余通知回位，避免中间留下"空洞"
        this.Closed += (_, _) =>
        {
            lock (s_active) { s_active.Remove(this); }
            Relayout();
        };

        // 淡入：避免生硬弹出（原实现无动画，闪现感强）
        try
        {
            RootGrid.Opacity = 0;
            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, RootGrid);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);

            // 同时从边缘滑入（仅有淡入会显得"凭空出现"，缺少方向感）
            var slide = new DoubleAnimation
            {
                From = 32, To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, SlideTransform);
            Storyboard.SetTargetProperty(slide, "X");
            sb.Children.Add(slide);

            sb.Begin();
        }
        catch { RootGrid.Opacity = 1; }

        // 5 秒后自动关闭
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoCloseTimer.Tick += (_, _) => Close();
        _autoCloseTimer.Start();
    }

    /// <summary>设置通知内容与语义（默认 Success）。</summary>
    public void SetContent(string title, string body, ToastTone tone = ToastTone.Success, string? filePath = null)
    {
        TitleTxt.Text = title;
        BodyTxt.Text = body;
        SetupAction(filePath);

        // 时间戳：Windows 通知卡片的标准元素（原来没有，无法判断通知是何时产生的）
        TimeTxt.Text = DateTime.Now.ToString("HH:mm");

        // 语义图标 + 前景色。Windows 11 通知卡片的图标是**着色的符号**，
        // 而非徽章 —— 语义靠颜色区分：成功绿 / 警告黄 / 错误红 / 信息蓝。
        // 原来所有通知都是同一个绿色对勾，降级/失败也显示成"成功"是明显误导。
        var (glyph, brushKey) = tone switch
        {
            ToastTone.Warning => ("\uE7BA", "SystemFillColorCautionBrush"),
            ToastTone.Error => ("\uE783", "SystemFillColorCriticalBrush"),
            ToastTone.Info => ("\uE946", "SystemFillColorAttentionBrush"),
            _ => ("\uE73E", "SystemFillColorSuccessBrush"),
        };

        IconGlyph.Glyph = glyph;
        try
        {
            if (Application.Current.Resources.TryGetValue(brushKey, out var res) && res is Brush b)
                IconGlyph.Foreground = b;
        }
        catch
        {
            // 主题资源缺失时退化为硬编码色，保证始终可见
            IconGlyph.Foreground = new SolidColorBrush(tone switch
            {
                ToastTone.Warning => Microsoft.UI.Colors.Orange,
                ToastTone.Error => Microsoft.UI.Colors.IndianRed,
                ToastTone.Info => Microsoft.UI.Colors.SteelBlue,
                _ => Microsoft.UI.Colors.SeaGreen,
            });
        }
    }

    /// <summary>按槽位定位：同一位置的通知纵向错开，避免互相覆盖。</summary>
    private void PlaceAt(int slot)
    {
        try
        {
            if (_appWindow is null) return;
            var da = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Primary);
            if (da is null) return;
            var wa = da.WorkArea;

            int offset = slot * (ToastH + Gap);
            int x = _position switch
            {
                "TopLeft" => wa.X + Margin,
                "BottomLeft" => wa.X + Margin,
                _ => wa.X + wa.Width - ToastW - Margin,          // BottomRight / TopRight / WindowsNotify
            };
            int y = _position switch
            {
                "TopRight" or "TopLeft" => wa.Y + Margin + offset,             // 自上而下排
                _ => wa.Y + wa.Height - ToastH - Margin - offset,              // 自下而上排
            };
            _appWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    /// <summary>某条关闭后，让剩下的通知重新排布。</summary>
    private static void Relayout()
    {
        lock (s_active)
        {
            for (int i = 0; i < s_active.Count; i++) s_active[i].PlaceAt(i);
        }
    }

    /// <summary>操作区（"打开文件夹"）仅在确实拿到文件且文件还在时才显示。</summary>
    private void SetupAction(string? filePath)
    {
        _filePath = filePath;
        bool show = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
        ActionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        // 注：WinUI 的 RoutedEventArgs 没有 Handled（那是 WPF 的）。
        // 阻止按压冒泡到卡片（卡片点击=关闭）交给 OnActionPointerPressed 处理。
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            // /select, 会在资源管理器中打开所在文件夹并选中该文件
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_filePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"打开文件夹失败: {ex.Message}", LogCategory.UI);
        }
        Close();
    }

    /// <summary>用系统默认程序打开该文件。</summary>
    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"打开文件失败: {ex.Message}", LogCategory.UI);
        }
        Close();
    }

    /// <summary>复制文件完整路径。刻意不关闭通知 —— 方便接着点其它按钮。</summary>
    private async void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_filePath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            // 就地反馈：否则点了没动静，用户不知道到底复制成功没有
            CopyPathLabel.Text = "已复制";
            await System.Threading.Tasks.Task.Delay(1200);
            CopyPathLabel.Text = "复制路径";
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"复制路径失败: {ex.Message}", LogCategory.UI);
        }
    }

    /// <summary>阻止按钮上的按压冒泡到卡片（卡片点击 = 关闭），否则按钮点不动。</summary>
    private void OnActionPointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

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

    // 鼠标悬停时暂停自动关闭：用户正在读（尤其是较长的降级说明）时不该被强行关掉
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _autoCloseTimer?.Stop();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => _autoCloseTimer?.Start();
}
