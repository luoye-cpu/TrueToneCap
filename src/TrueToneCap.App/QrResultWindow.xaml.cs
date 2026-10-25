// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/QrResultWindow.xaml.cs
// 二维码结果窗口（2026-10-26）：截图区域回看 + 解码载荷明文列表。
//
// ⚠ 本窗口是"识别二维码"功能的全部执行面，安全纪律集中在这里：
//   ① 永不自动打开 —— 构造函数只渲染展示；"打开链接"按钮仅对
//      CanOpenInBrowser == true（Kind==UrlHttp 且无控制字符/双向符）的载荷出现，
//      且点击处理里【再验一遍】（UI 判据可能过期，动作前以代码为准，双重复核）。
//   ② 只渲染 DisplayText（控制字符/双向符已转义成 \uXXXX），绝不渲染 Raw ——
//      屏幕上每个可见字符都真实存在，双向符无法伪装视觉顺序。
//   ③ 复制按钮给 Raw（逐字原样，用户复制出去的就是解码原文）；
//      这与"展示层转义"是两回事：转义只发生在屏幕渲染，不改写数据。
//   ④ 日志不写 payload：所有 LogService 只记动作与计数（PRIVACY.md 口径）。
//   ⑤ 危险 scheme（file:/ms-msdt:/search-ms:…，Follina 类）红牌展示、无打开按钮。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Runtime.InteropServices.WindowsRuntime;
using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Services;
using TrueToneCap.App.Services;
using WinRT.Interop;

namespace TrueToneCap.App;

public sealed partial class QrResultWindow : Window
{
    private readonly byte[] _rawPixels;
    private readonly int _imgW, _imgH;
    private readonly QrScanReport _report;

    /// <summary>这一拍截图的日志关联 ID（由 MainWindow 建窗时传进来，与 OCR 预览窗同一套）。</summary>
    public string? CaptureOpId { get; set; }

    public QrResultWindow(byte[] bgraPixels, int width, int height, QrScanReport report)
    {
        this.InitializeComponent();
        MainWindow.HookLocaleSweep(this.Content as Microsoft.UI.Xaml.FrameworkElement);
        // 窗口标题不在可视树里，只能显式翻译。
        Title = LocaleManager.Tr(Title);
        _rawPixels = bgraPixels;
        _imgW = width; _imgH = height;
        _report = report;

        // ── 字体注入（与 OCR 预览窗同一模式） ──
        var fontFamily = FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily);
        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);

        SmartResize();
        RenderImage();

        InfoTxt.Text = string.Format(
            LocaleManager.Tr("检测到 {0} 个二维码（解码在本机完成，不会自动打开任何内容）"),
            _report.Payloads.Count);

        foreach (var p in _report.Payloads)
            PayloadList.Children.Add(BuildPayloadCard(p));

        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { this.Close(); e.Handled = true; }
        };
    }

    // ═══════════════════════════════════════
    //  载荷卡片（动态构建 —— 内容逐条不同，XAML 静态布局写不下）
    // ═══════════════════════════════════════

    private UIElement BuildPayloadCard(QrPayload p)
    {
        var kindLabel = p.Kind switch
        {
            QrPayloadKind.UrlHttp => LocaleManager.Tr("链接"),
            QrPayloadKind.UrlOtherScheme => LocaleManager.Tr("其它协议（不打开）"),
            QrPayloadKind.DangerousScheme => LocaleManager.Tr("危险协议"),
            _ => LocaleManager.Tr("文本"),
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            // 红牌只给 DangerousScheme；UrlOtherScheme 黄牌（不可打开但要留神）；其余中性
            BorderBrush = p.Kind switch
            {
                QrPayloadKind.DangerousScheme => new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                QrPayloadKind.UrlOtherScheme => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
                _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
            },
        };
        var stack = new StackPanel { Spacing = 8 };
        card.Child = stack;

        // ── 第一行：类别徽标 ──
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = kindLabel,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = p.Kind == QrPayloadKind.DangerousScheme
                ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(headerRow);

        // ── 警告行（隐藏字符 / 双向符）──
        if (p.HasHiddenChars || p.HasRtlOverride)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocaleManager.Tr("⚠ 内容含控制字符或双向覆盖符（反向显示类），屏幕上已按可见转义显示 —— 该内容的视觉顺序可能与实际不同，复制前请留意。"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
            });
        }
        if (p.Kind == QrPayloadKind.DangerousScheme)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocaleManager.Tr("⚠ 此内容携带可执行/系统跳转类协议（file: / ms-msdt: / shell: 等），程序已禁止任何打开动作 —— 仅作展示。"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            });
        }

        // ── 内容：只读等宽 TextBox —— 展示 DisplayText（转义版），可选中可滚动，杜绝截断藏内容 ──
        var content = new TextBox
        {
            Text = p.DisplayText,
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 180,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            FontSize = 13,
        };
        // 只读 TextBox 默认主题仍亮可用态，压一下背景以示"这是展示面"
        content.Style = (Style)Application.Current.Resources["DefaultTextBoxStyle"];
        stack.Children.Add(content);

        // ── 动作行 ──
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(MakeActionButton(
            LocaleManager.Tr("复制内容"), "复制解码原文（逐字原样）", OnCopyClick, p));
        if (p.CanOpenInBrowser)
            actions.Children.Add(MakeActionButton(
                LocaleManager.Tr("打开链接"), "在默认浏览器中打开该链接（仅 http/https）", OnOpenLinkClick, p));
        stack.Children.Add(actions);
        return card;
    }

    private static Button MakeActionButton(string text, string tooltip, RoutedEventHandler onClick, QrPayload payload)
    {
        var b = new Button { Height = 32, Padding = new Thickness(12, 0, 12, 0) };
        b.Content = text;
        ToolTipService.SetToolTip(b, tooltip);
        b.Click += onClick;
        b.Tag = payload;
        return b;
    }

    // ═══════════════════════════════════════
    //  动作
    // ═══════════════════════════════════════

    private static QrPayload? PayloadOf(object sender) =>
        (sender as FrameworkElement)?.Tag as QrPayload;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var p = PayloadOf(sender);
        if (p is null) return;
        using var op = CoreLog.ContinueOp(CaptureOpId);
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(p.Raw);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            StatusTxt.Text = LocaleManager.Tr("✓ 已复制（原样，未改写）");
        }
        catch (Exception ex)
        {
            // 不把 payload 带进日志
            LogService.Error("QrResult", $"复制失败: {ex.Message}");
            StatusTxt.Text = LocaleManager.Tr("复制失败");
        }
    }

    private void OnOpenLinkClick(object sender, RoutedEventArgs e)
    {
        var p = PayloadOf(sender);
        if (p is null) return;
        using var op = CoreLog.ContinueOp(CaptureOpId);

        // ── 双重把关之二：点击时以代码再验一遍，不信任 UI 判据 ──
        if (!p.CanOpenInBrowser) return;

        // Uri 解析：绝对 URI 要求合法 host —— \r\n 注入形态在这里直接 TryCreate 失败
        if (!Uri.TryCreate(p.Raw, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;
        if (uri.Host.Length == 0) return;

        try
        {
            // 只记动作不记 URL（日志纪律：payload 不落日志）
            LogService.Info("QrResult", "用户点击打开链接（http/https 白名单已复核）");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            StatusTxt.Text = LocaleManager.Tr("已在默认浏览器打开");
        }
        catch (Exception ex)
        {
            LogService.Error("QrResult", $"打开链接失败: {ex.Message}");
            StatusTxt.Text = LocaleManager.Tr("打开链接失败");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => this.Close();

    private void OnClosed(object sender, WindowEventArgs args) { }

    // ═══════════════════════════════════════
    //  渲染（与 OCR 预览窗同款：同步 WriteableBitmap）
    // ═══════════════════════════════════════

    private void SmartResize()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            int winW = Math.Min(980, Math.Max(760, area.WorkArea.Width - 160));
            int winH = Math.Min(720, Math.Max(520, area.WorkArea.Height - 160));
            appWindow.MoveAndResize(new RectInt32(
                area.WorkArea.X + (area.WorkArea.Width - winW) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - winH) / 2, winW, winH));
        }
        catch { }
    }

    private void RenderImage()
    {
        try
        {
            var wb = new WriteableBitmap(_imgW, _imgH);
            using (var stream = wb.PixelBuffer.AsStream())
            {
                stream.Write(_rawPixels, 0, _rawPixels.Length);
            }
            wb.Invalidate();
            PreviewImage.Source = wb;
        }
        catch (Exception ex)
        {
            StatusTxt.Text = string.Format(LocaleManager.Tr("预览渲染失败: {0}"), ex.Message);
        }
    }
}
