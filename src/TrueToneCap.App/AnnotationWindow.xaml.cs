// TrueToneCap.App/AnnotationWindow.xaml.cs
// 全窗口截图预览与标注编辑器 (接受原始 BGRA 像素, 零编码延迟)

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinRT.Interop;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Encoding;
using TrueToneCap.App.Services;

namespace TrueToneCap.App;

public sealed partial class AnnotationWindow : Window
{
    // 原始 BGRA 像素数据（标注期间在内存中保持未压缩）
    private readonly byte[] _rawPixels;
    private readonly int _imgW, _imgH;
    private readonly AnnotationManager _annotationManager = new();
    private string _currentTool = "Rect";
    private System.Numerics.Vector2 _dragStart;
    private bool _isDrawing;
    private readonly List<UIElement> _renderedShapes = [];

    // 保存回调：由 MainWindow 注入
    public Func<byte[], int, int, Task>? OnSaveRequested { get; set; }
    public Func<byte[], int, int, Task>? OnCopyRequested { get; set; }

    public AnnotationWindow(byte[] bgraPixels, int width, int height)
    {
        this.InitializeComponent();
        _rawPixels = bgraPixels;
        _imgW = width; _imgH = height;

        // ── 字体注入 ──
        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, FontLoader.DefaultFontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, FontLoader.DefaultFontFamily);

        // ── 智能窗口尺寸：基于当前显示器工作区，图片自适应 ──
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            int maxW = displayArea.WorkArea.Width - 80;
            int maxH = displayArea.WorkArea.Height - 120;

            // 按图片比例缩放窗口，但不超过显示器工作区
            double scale = Math.Min(1.0, Math.Min((double)maxW / width, (double)maxH / height));
            int winW = Math.Max(400, (int)(width * scale) + 80);
            int winH = Math.Max(300, (int)(height * scale) + 120);
            // 居中于当前显示器
            int winX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - winW) / 2;
            int winY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - winH) / 2;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(winX, winY, winW, winH));
        }
        catch { }

        // 零延迟显示：直接渲染 BGRA 像素到 WriteableBitmap
        RenderRawPixels();

        AnnotationCanvas.PointerPressed += OnCanvasPointerPressed;
        AnnotationCanvas.PointerMoved += OnCanvasPointerMoved;
        AnnotationCanvas.PointerReleased += OnCanvasPointerReleased;

        // 键盘快捷键
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            { OnCopy(null!, null!); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.S)
            {
                // 简化：不检查 Ctrl 修饰键（与 Enter 区分即可）
                OnSave(null!, null!); e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            { OnDiscard(null!, null!); e.Handled = true; }
        };

        UpdateToolHighlights();
        InfoTxt.Text = "Enter=复制至剪贴板  S=保存到文件  Esc=丢弃";
    }

    /// <summary>直接渲染 BGRA 原始像素（零编码延迟）。图片自动适配窗口大小。</summary>
    private async void RenderRawPixels()
    {
        try
        {
            var swBmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _imgW, _imgH);
            swBmp.CopyFromBuffer(_rawPixels.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(swBmp);
            PreviewImage.Source = source;
            // 不设置 Width/Height，让 Stretch="Uniform" 自然适配容器
            // 让 Image 随窗口缩放，保持完整显示
            PreviewImage.Stretch = Stretch.Uniform;
            PreviewImage.MaxWidth = _imgW;
            PreviewImage.MaxHeight = _imgH;

            // ── AnnotationCanvas 尺寸同步为图片实际显示尺寸 ──
            PreviewImage.SizeChanged += (s, e) =>
            {
                AnnotationCanvas.Width = PreviewImage.ActualWidth;
                AnnotationCanvas.Height = PreviewImage.ActualHeight;
            };

            swBmp.Dispose();
        }
        catch (Exception ex)
        {
            InfoTxt.Text = $"⚠ 渲染失败: {ex.Message}";
        }
    }

    // ────────────── 工具选择 ──────────────

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentTool = tag;
            UpdateToolHighlights();
            InfoTxt.Text = tag switch
            {
                "Rect" => "矩形: 拖动绘制矩形框",
                "Ellipse" => "椭圆: 拖动绘制椭圆",
                "Arrow" => "箭头: 拖动绘制箭头",
                "Pen" => "画笔: 按住鼠标自由绘制",
                "Text" => "文字: 点击位置添加文字",
                "Mosaic" => "马赛克: 拖动区域打码",
                _ => ""
            };
        }
    }

    private void UpdateToolHighlights()
    {
        var btns = new[] { RectBtn, EllipseBtn, ArrowBtn, PenBtn, TextBtn, MosaicBtn };
        var tags = new[] { "Rect", "Ellipse", "Arrow", "Pen", "Text", "Mosaic" };
        for (int i = 0; i < btns.Length; i++)
            btns[i].Background = tags[i] == _currentTool
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ────────────── 坐标缩放（Canvas 显示尺寸 → 原始图片像素） ──────────────

    /// <summary>将 Canvas 坐标转换为原始图像像素坐标。</summary>
    private System.Numerics.Vector2 CanvasToImage(System.Numerics.Vector2 canvasPt)
    {
        if (AnnotationCanvas.Width <= 0 || AnnotationCanvas.Height <= 0)
            return canvasPt;
        float sx = _imgW / (float)AnnotationCanvas.Width;
        float sy = _imgH / (float)AnnotationCanvas.Height;
        return new System.Numerics.Vector2(canvasPt.X * sx, canvasPt.Y * sy);
    }

    /// <summary>将原始图像像素坐标转换为 Canvas 坐标。</summary>
    private System.Numerics.Vector2 ImageToCanvas(System.Numerics.Vector2 imagePt)
    {
        if (_imgW <= 0 || _imgH <= 0)
            return imagePt;
        float sx = (float)AnnotationCanvas.Width / _imgW;
        float sy = (float)AnnotationCanvas.Height / _imgH;
        return new System.Numerics.Vector2(imagePt.X * sx, imagePt.Y * sy);
    }

    // ────────────── 绘制 ──────────────

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        _dragStart = CanvasToImage(canvasPt);  // 存储为图像坐标
        _isDrawing = true;
        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        var imageEnd = CanvasToImage(canvasPt);
        // 预览用 Canvas 坐标绘制
        DrawPreview(ImageToCanvas(_dragStart), canvasPt);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        AnnotationCanvas.ReleasePointerCaptures();
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        var imageEnd = CanvasToImage(canvasPt);
        CommitShape(_dragStart, imageEnd);  // 使用图像坐标存储
    }

    private void DrawPreview(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        // 清除预览层
        AnnotationCanvas.Children.Clear();

        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);

        if (w < 2 && h < 2) return;

        Shape? shape = _currentTool switch
        {
            "Rect" => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Ellipse" => new Ellipse { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Arrow" => new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            _ => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 }
        };

        if (shape != null)
        {
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
            AnnotationCanvas.Children.Add(shape);
        }
    }

    private void CommitShape(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 3 && h < 3) { AnnotationCanvas.Children.Clear(); return; }

        AnnotationLayer layer = _currentTool switch
        {
            "Rect" => new RectangleLayer { X = x, Y = y, Width = w, Height = h },
            "Ellipse" => new EllipseLayer { CenterX = x + w / 2, CenterY = y + h / 2, RadiusX = w / 2, RadiusY = h / 2 },
            "Arrow" => new ArrowLayer { StartX = start.X, StartY = start.Y, EndX = end.X, EndY = end.Y },
            "Pen" => new FreehandLayer { Points = [start, end] },
            "Text" => new TextLayer { X = x, Y = y, Text = "标注", FontSize = 16 },
            "Mosaic" => new MosaicLayer { X = x, Y = y, Width = w, Height = h },
            _ => new RectangleLayer { X = x, Y = y, Width = w, Height = h }
        };

        _annotationManager.AddLayer(layer);
        RenderAllLayers();
    }

    private void RenderAllLayers()
    {
        AnnotationCanvas.Children.Clear();
        foreach (var layer in _annotationManager.Layers.Where(l => l.IsVisible))
        {
            Shape? shape = layer switch
            {
                RectangleLayer r => new Rectangle { Width = r.Width, Height = r.Height, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 255, 0, 0)) },
                EllipseLayer el => new Ellipse { Width = el.RadiusX * 2, Height = el.RadiusY * 2, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
                MosaicLayer m => new Rectangle { Width = m.Width, Height = m.Height, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(200, 100, 100, 100)) },
                _ => null
            };

            if (shape != null)
            {
                var bounds = layer.GetBounds();
                // 图像坐标 → Canvas 显示坐标
                var canvasPos = ImageToCanvas(new System.Numerics.Vector2(bounds.Left, bounds.Top));
                var canvasSize = ImageToCanvas(new System.Numerics.Vector2(bounds.Right, bounds.Bottom))
                    - new System.Numerics.Vector2(canvasPos.X, canvasPos.Y);
                shape.Width = Math.Abs(canvasSize.X);
                shape.Height = Math.Abs(canvasSize.Y);
                Canvas.SetLeft(shape, canvasPos.X);
                Canvas.SetTop(shape, canvasPos.Y);
                AnnotationCanvas.Children.Add(shape);
            }
        }
    }

    // ────────────── 撤销/重做 ──────────────

    private void OnUndo(object sender, RoutedEventArgs e) { _annotationManager.Undo(); RenderAllLayers(); }
    private void OnRedo(object sender, RoutedEventArgs e) { _annotationManager.Redo(); RenderAllLayers(); }

    // ────────────── 保存/复制/丢弃（显式触发编码） ──────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        InfoTxt.Text = "💾 编码中...";
        try
        {
            if (OnSaveRequested != null)
                await OnSaveRequested(GetFinalPixels(), _imgW, _imgH);
            else InfoTxt.Text = "⚠ 未配置保存回调";
        }
        catch (Exception ex) { InfoTxt.Text = $"❌ {ex.Message}"; }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        InfoTxt.Text = "📋 复制中...";
        try
        {
            if (OnCopyRequested != null)
                await OnCopyRequested(GetFinalPixels(), _imgW, _imgH);
            else InfoTxt.Text = "⚠ 未配置复制回调";
        }
        catch (Exception ex) { InfoTxt.Text = $"❌ {ex.Message}"; }
    }

    private void OnDiscard(object sender, RoutedEventArgs e) { CloseWindow(); }
    private void OnClosed(object sender, WindowEventArgs args) { CloseWindow(); }
    private void CloseWindow() { try { this.Close(); } catch { } }

    /// <summary>获取最终带标注的像素（标注合成到原始像素上，仅在编码时调用）</summary>
    private byte[] GetFinalPixels()
    {
        if (_annotationManager.Layers.Count == 0) return _rawPixels;
        var result = new byte[_rawPixels.Length];
        Buffer.BlockCopy(_rawPixels, 0, result, 0, _rawPixels.Length);

        foreach (var layer in _annotationManager.Layers.Where(l => l.IsVisible))
        {
            var bounds = layer.GetBounds();
            int lx = Math.Max(0, (int)bounds.Left), ly = Math.Max(0, (int)bounds.Top);
            int rx = Math.Min(_imgW - 1, (int)bounds.Right), ry = Math.Min(_imgH - 1, (int)bounds.Bottom);

            if (layer is MosaicLayer)
            {
                for (int y = ly; y <= ry; y += 6)
                for (int x = lx; x <= rx; x += 6)
                {
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (int dy = 0; dy < 6 && y + dy <= ry; dy++)
                    for (int dx = 0; dx < 6 && x + dx <= rx; dx++)
                    { int idx = ((y + dy) * _imgW + (x + dx)) * 4; b += result[idx]; g += result[idx + 1]; r += result[idx + 2]; cnt++; }
                    byte av = (byte)((r + g + b) / (cnt * 3));
                    for (int dy = 0; dy < 6 && y + dy <= ry; dy++)
                    for (int dx = 0; dx < 6 && x + dx <= rx; dx++)
                    { int idx = ((y + dy) * _imgW + (x + dx)) * 4; result[idx] = result[idx + 1] = result[idx + 2] = av; }
                }
            }
            else
            {
                int t = 2;
                for (int y = ly; y <= Math.Min(ly + t, ry); y++)
                for (int x = lx; x <= rx; x++)
                { int idx = (y * _imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int y = Math.Max(ly, ry - t); y <= ry; y++)
                for (int x = lx; x <= rx; x++)
                { int idx = (y * _imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int x = lx; x <= Math.Min(lx + t, rx); x++)
                for (int y = ly; y <= ry; y++)
                { int idx = (y * _imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int x = Math.Max(lx, rx - t); x <= rx; x++)
                for (int y = ly; y <= ry; y++)
                { int idx = (y * _imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
            }
        }
        return result;
    }
}
