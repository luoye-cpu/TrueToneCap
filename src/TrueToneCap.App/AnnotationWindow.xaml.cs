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

    // 画笔轨迹累积（拖动期间逐点追加，完成后一次性提交为 FreehandLayer）
    private readonly List<System.Numerics.Vector2> _penPoints = [];
    // 文字插入位置（图像坐标）
    private System.Numerics.Vector2 _textInsertPos;

    // 保存回调：由 MainWindow 注入
    public Func<byte[], int, int, Task>? OnSaveRequested { get; set; }
    public Func<byte[], int, int, Task>? OnCopyRequested { get; set; }

    public AnnotationWindow(byte[] bgraPixels, int width, int height)
    {
        this.InitializeComponent();
        _rawPixels = bgraPixels;
        _imgW = width; _imgH = height;

        // ── 字体注入（使用用户选择的字体） ──
        var fontFamily = FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily);
        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);

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
        var imagePt = CanvasToImage(canvasPt);

        // 文字工具：点击位置弹输入框，不进入拖拽
        if (_currentTool == "Text")
        {
            _textInsertPos = imagePt;
            ShowTextInput(canvasPt);
            return;
        }

        _dragStart = imagePt;  // 存储为图像坐标
        _isDrawing = true;
        _penPoints.Clear();
        _penPoints.Add(imagePt);
        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        var imageEnd = CanvasToImage(canvasPt);
        if (_currentTool == "Pen")
        {
            // 画笔：累积轨迹点，绘制实时折线预览
            var last = _penPoints[^1];
            if ((imageEnd - last).Length() > 0.5f) _penPoints.Add(imageEnd);
            DrawPenPreview();
            return;
        }
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

        if (_currentTool == "Pen")
        {
            if (_penPoints.Count >= 2)
            {
                _penPoints.Add(imageEnd);
                _annotationManager.AddLayer(new FreehandLayer { Points = [.. _penPoints] });
                RenderAllLayers();
            }
            else AnnotationCanvas.Children.Clear();
            _penPoints.Clear();
            return;
        }
        CommitShape(_dragStart, imageEnd);  // 使用图像坐标存储
    }

    private void DrawPreview(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        // 清除预览层
        AnnotationCanvas.Children.Clear();

        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);

        if (w < 2 && h < 2) return;

        UIElement? element = _currentTool switch
        {
            "Rect" => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Ellipse" => new Ellipse { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Arrow" => CreateArrowElement(start, end),
            _ => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 }
        };

        if (element is Shape shape)
        {
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
        }
        if (element != null)
            AnnotationCanvas.Children.Add(element);
    }

    /// <summary>画笔实时预览：按轨迹点绘制折线。</summary>
    private void DrawPenPreview()
    {
        AnnotationCanvas.Children.Clear();
        if (_penPoints.Count < 2) return;
        var poly = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var p in _penPoints)
        {
            var cp = ImageToCanvas(p);
            poly.Points.Add(new Windows.Foundation.Point(cp.X, cp.Y));
        }
        AnnotationCanvas.Children.Add(poly);
    }

    /// <summary>箭头元素：主线 + 箭头头部。</summary>
    private static UIElement CreateArrowElement(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        var stroke = new SolidColorBrush(Microsoft.UI.Colors.Red);
        var geom = new Microsoft.UI.Xaml.Media.PathGeometry();
        var fig = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = new Windows.Foundation.Point(start.X, start.Y) };
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(end.X, end.Y) });

        // 箭头头部两条短线（与主线 ±30°）
        float dx = end.X - start.X, dy = end.Y - start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        const float ang = MathF.PI / 6f;
        float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
        float ux = len > 1e-3f ? dx / len : 1f, uy = len > 1e-3f ? dy / len : 0f;
        float head = 12f;
        var h1 = new Windows.Foundation.Point(end.X - head * (ux * ca - uy * sa), end.Y - head * (ux * sa + uy * ca));
        var h2 = new Windows.Foundation.Point(end.X - head * (ux * ca + uy * sa), end.Y - head * (-ux * sa + uy * ca));
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = h1 });
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(end.X, end.Y) });
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = h2 });
        geom.Figures.Add(fig);

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geom,
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    // ────────────── 文字输入 ──────────────

    private void ShowTextInput(System.Numerics.Vector2 canvasPt)
    {
        TextInputPanel.Margin = new Thickness(
            Math.Clamp(canvasPt.X + 8, 0, Math.Max(0, AnnotationCanvas.Width - 300)),
            Math.Clamp(canvasPt.Y + 8, 0, Math.Max(0, AnnotationCanvas.Height - 60)), 0, 0);
        TextInputPanel.Visibility = Visibility.Visible;
        AnnoTextInput.Text = "";
        AnnoTextInput.Focus(FocusState.Programmatic);
    }

    private void HideTextInput()
    {
        TextInputPanel.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void OnTextInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OnTextInputOk(null!, null!);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            HideTextInput();
        }
    }

    private void OnTextInputOk(object sender, RoutedEventArgs e)
    {
        string text = AnnoTextInput.Text.Trim();
        if (text.Length > 0)
        {
            _annotationManager.AddLayer(new TextLayer
            {
                X = _textInsertPos.X, Y = _textInsertPos.Y,
                Text = text, FontSize = 16
            });
            RenderAllLayers();
        }
        HideTextInput();
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
            UIElement? element = layer switch
            {
                RectangleLayer r => new Rectangle { Width = r.Width, Height = r.Height, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 255, 0, 0)) },
                EllipseLayer el => new Ellipse { Width = el.RadiusX * 2, Height = el.RadiusY * 2, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
                ArrowLayer al => CreateArrowElement(
                    ImageToCanvas(new System.Numerics.Vector2(al.StartX, al.StartY)),
                    ImageToCanvas(new System.Numerics.Vector2(al.EndX, al.EndY))),
                FreehandLayer fl => CreatePolylineElement(
                    fl.Points.Select(p => ImageToCanvas(p))),
                TextLayer tl => CreateTextElement(tl, ImageToCanvas(new System.Numerics.Vector2(tl.X, tl.Y))),
                MosaicLayer m => new Rectangle { Width = m.Width, Height = m.Height, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(200, 100, 100, 100)) },
                _ => null
            };

            if (element is null) continue;

            // Polyline/Path(箭头)/TextBlock 自带绝对坐标，其余按边界定位
            if (element is not Microsoft.UI.Xaml.Shapes.Polyline
                && element is not Microsoft.UI.Xaml.Shapes.Path
                && element is not TextBlock)
            {
                var bounds = layer.GetBounds();
                var canvasPos = ImageToCanvas(new System.Numerics.Vector2(bounds.Left, bounds.Top));
                var canvasSize = ImageToCanvas(new System.Numerics.Vector2(bounds.Right, bounds.Bottom))
                    - new System.Numerics.Vector2(canvasPos.X, canvasPos.Y);
                if (element is FrameworkElement fe)
                {
                    fe.Width = Math.Abs(canvasSize.X);
                    fe.Height = Math.Abs(canvasSize.Y);
                }
                Canvas.SetLeft(element, canvasPos.X);
                Canvas.SetTop(element, canvasPos.Y);
            }
            AnnotationCanvas.Children.Add(element);
        }
    }

    /// <summary>折线元素（画笔轨迹，Polyline 坐标为画布绝对坐标）。</summary>
    private static Microsoft.UI.Xaml.Shapes.Polyline CreatePolylineElement(IEnumerable<System.Numerics.Vector2> canvasPts)
    {
        var poly = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var p in canvasPts) poly.Points.Add(new Windows.Foundation.Point(p.X, p.Y));
        return poly;
    }

    /// <summary>文字元素（TextBlock，画布绝对定位）。</summary>
    private static TextBlock CreateTextElement(TextLayer tl, System.Numerics.Vector2 canvasPos)
    {
        var tb = new TextBlock
        {
            Text = tl.Text,
            FontSize = tl.FontSize,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
        };
        Canvas.SetLeft(tb, canvasPos.X);
        Canvas.SetTop(tb, canvasPos.Y);
        return tb;
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
            var pixels = await ComposeFinalPixelsAsync();
            if (OnSaveRequested != null && pixels != null)
                await OnSaveRequested(pixels, _imgW, _imgH);
            else InfoTxt.Text = "⚠ 未配置保存回调";
        }
        catch (Exception ex) { InfoTxt.Text = $"❌ {ex.Message}"; }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        InfoTxt.Text = "📋 复制中...";
        try
        {
            var pixels = await ComposeFinalPixelsAsync();
            if (OnCopyRequested != null && pixels != null)
                await OnCopyRequested(pixels, _imgW, _imgH);
            else InfoTxt.Text = "⚠ 未配置复制回调";
        }
        catch (Exception ex) { InfoTxt.Text = $"❌ {ex.Message}"; }
    }

    /// <summary>标注合成移出 UI 线程（大图马赛克/文字合成不再卡界面）。</summary>
    private async Task<byte[]> ComposeFinalPixelsAsync()
    {
        if (_annotationManager.Layers.Count == 0) return _rawPixels;
        return await Task.Run(GetFinalPixels);
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
            AnnotationRasterizer.RenderLayer(result, _imgW, _imgH, layer);
        return result;
    }
}
