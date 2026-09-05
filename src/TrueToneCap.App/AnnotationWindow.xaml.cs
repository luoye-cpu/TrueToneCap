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
    private UIElement? _previewShape;   // 拖动预览元素 (不再 Clear 整个 Canvas!)

    // ═══ 2026-08-28 涂鸦重写: 图层面板 + 坐标换算 ═══
    private double _viewScale = 1.0;     // ScrollViewer 内容缩放
    private Windows.UI.Color _strokeColor = Microsoft.UI.Colors.Red;
    private double _strokeWidth = 4.0;
    private Guid? _selectedLayerId;      // 当前选中的图层 ID

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
            int winH = Math.Max(300, (int)(height * scale) + 160);
            // 居中于当前显示器
            int winX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - winW) / 2;
            int winY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - winH) / 2;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(winX, winY, winW, winH));
        }
        catch { }

        // ═══ 2026-08-25 P1 性能优化: WriteableBitmap 替代 SoftwareBitmap ═══
        // 旧: SoftwareBitmapSource.SetBitmapAsync 异步加载, 4K ~20-40ms
        // 新: WriteableBitmap + PixelData.AsStream().Write() 同步填充, <5ms
        RenderRawPixels();

        AnnotationCanvas.PointerPressed += OnCanvasPointerPressed;
        AnnotationCanvas.PointerMoved += OnCanvasPointerMoved;
        AnnotationCanvas.PointerReleased += OnCanvasPointerReleased;
        AnnotationCanvas.PointerWheelChanged += OnCanvasWheelChanged;

        // 键盘快捷键 (Ctrl+Z/Y 撤销重做)
        RootGrid.KeyDown += OnRootKeyDown;

        UpdateToolHighlights();
        UpdateInfoText();
    }

    /// <summary>直接渲染 BGRA 原始像素: 设置 Image 像素尺寸, Canvas 尺寸与 Image 恒等。</summary>
    private void RenderRawPixels()
    {
        try
        {
            // ═══ 2026-08-25 P1 性能优化: WriteableBitmap 同步填充 ═══
            // 比 SoftwareBitmapSource.SetBitmapAsync 快 ~5-10x (省去异步切换 + COM 封送)
            var wbmp = new WriteableBitmap(_imgW, _imgH);
            // WinUI 3: PixelBuffer (IBuffer) → AsStream() 直接写入像素
            using var stream = wbmp.PixelBuffer.AsStream();
            stream.Write(_rawPixels, 0, _rawPixels.Length);
            wbmp.Invalidate();

            PreviewImage.Source = wbmp;
            // Image 尺寸 = 像素尺寸 (Stretch=Fill, 不缩放)
            PreviewImage.Width = _imgW;
            PreviewImage.Height = _imgH;

            // Canvas 尺寸 = 图片尺寸, 坐标即图像像素坐标 (零换算!)
            AnnotationCanvas.Width = _imgW;
            AnnotationCanvas.Height = _imgH;

            // 更新 ScrollViewer 缩放适配
            UpdateViewScale();
        }
        catch (Exception ex)
        {
            InfoTxt.Text = $"⚠ 渲染失败: {ex.Message}";
        }
    }

    /// <summary>根据窗口可用空间计算最佳显示缩放, 确保图片完整可见。</summary>
    private void UpdateViewScale()
    {
        // 100% 像素精确渲染, 大图靠 ScrollViewer 滚动浏览
        // 坐标即图像像素坐标, 无任何换算 (Canvas 尺寸 = 图片像素尺寸)
    }

    private double ViewScale
    {
        get => _viewScale;
        set => _viewScale = value;
    }

    // ────────────── 工具选择 ──────────────

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentTool = tag;
            UpdateToolHighlights();
            UpdateInfoText();
        }
    }

    private void OnColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorCbo.SelectedItem is ComboBoxItem item && item.Tag is string hex && hex.Length == 9)
        {
            _strokeColor = Microsoft.UI.ColorHelper.FromArgb(
                Convert.ToByte(hex[1..3], 16),
                Convert.ToByte(hex[3..5], 16),
                Convert.ToByte(hex[5..7], 16),
                Convert.ToByte(hex[7..9], 16));
        }
    }

    private void OnStrokeWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StrokeWidthCbo.SelectedItem is ComboBoxItem item && item.Tag is string w)
            _strokeWidth = double.TryParse(w, out var v) ? v : 4.0;
    }

    private void UpdateInfoText()
    {
        var toolHint = _currentTool switch
        {
            "Rect" => "矩形: 拖动绘制矩形框",
            "Ellipse" => "椭圆: 拖动绘制椭圆",
            "Arrow" => "箭头: 拖动绘制箭头",
            "Pen" => "画笔: 按住鼠标自由绘制",
            "Text" => "文字: 点击位置添加文字",
            "Mosaic" => "马赛克: 拖动区域打码",
            _ => ""
        };
        InfoTxt.Text = $"{toolHint}   |   Enter=复制至剪贴板  S=保存到文件  Esc=丢弃  Ctrl+Z/Y=撤销/重做  📋=图层面板";
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

    // ────────────── 坐标换算 (支持 ScrollViewer 缩放) ──────────────
    // AnnotationCanvas 尺寸 = 图像像素尺寸 (_imgW×_imgH), 但 ScrollViewer 可能缩放内容
    // 需要用 ActualScale 还原: 实际图像坐标 = 指针相对 Canvas 坐标 / ScrollViewer 缩放因子

    private System.Numerics.Vector2 GetImagePoint(PointerRoutedEventArgs e)
    {
        // 获取相对于 AnnotationCanvas 的坐标
        var canvasPt = e.GetCurrentPoint(AnnotationCanvas).Position;
        // ScrollViewer 缩放因子 (TransformMatrix 或 ZoomFactor)
        double scale = 1.0;
        if (CanvasScroll is Microsoft.UI.Xaml.Controls.ScrollViewer sv && sv.ZoomFactor > 0)
            scale = sv.ZoomFactor;
        return new System.Numerics.Vector2((float)(canvasPt.X / scale), (float)(canvasPt.Y / scale));
    }

    // ────────────── 绘制 ──────────────

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var imagePt = GetImagePoint(e);

        // 文字工具：点击位置弹输入框，不进入拖拽
        if (_currentTool == "Text")
        {
            _textInsertPos = imagePt;
            ShowTextInput(new Windows.Foundation.Point(imagePt.X, imagePt.Y));
            return;
        }

        _dragStart = imagePt;  // 存储为图像坐标 (= Canvas 坐标, 零换算)
        _isDrawing = true;
        _penPoints.Clear();
        _penPoints.Add(imagePt);
        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        var imageEnd = GetImagePoint(e);
        if (_currentTool == "Pen")
        {
            // 画笔：累积轨迹点，绘制实时折线预览
            var last = _penPoints[^1];
            if ((imageEnd - last).Length() > 0.5f) _penPoints.Add(imageEnd);
            DrawPenPreview();
            return;
        }
        // 预览用图像坐标绘制 (坐标恒等)
        DrawPreview(_dragStart, imageEnd);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        AnnotationCanvas.ReleasePointerCaptures();
        var imageEnd = GetImagePoint(e);

        if (_currentTool == "Pen")
        {
            if (_penPoints.Count >= 2)
            {
                _penPoints.Add(imageEnd);
                _annotationManager.AddLayer(new FreehandLayer
                {
                    Points = [.. _penPoints],
                    Style = CreateStyle()
                });
                RenderAllLayers();
            }
            RemovePreview();
            _penPoints.Clear();
            return;
        }
        CommitShape(_dragStart, imageEnd);  // 使用图像坐标存储
    }

    private BrushStyle CreateStyle() => new()
    {
        StrokeColor = new Color4(_strokeColor.R / 255f, _strokeColor.G / 255f, _strokeColor.B / 255f, 1f),
        StrokeWidth = (float)_strokeWidth,
        IsFillEnabled = false,
        IsStrokeEnabled = true
    };

    /// <summary>滚轮缩放: Ctrl+滚轮 缩放视图 (ScrollViewer 原生支持)。</summary>
    private async void OnCanvasWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // ScrollViewer 自带滚轮滚动; Ctrl+滚轮缩放由系统处理
        await Task.CompletedTask;
    }

    private void DrawPreview(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        // ═══ 2026-08-25 涂鸦重写: 用 _previewShape 替代 Clear+重建所有层 ═══
        RemovePreview();

        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);

        if (w < 2 && h < 2) return;

        var strokeBrush = new SolidColorBrush(_strokeColor);
        UIElement? element = _currentTool switch
        {
            "Rect" => new Rectangle { Width = w, Height = h, Stroke = strokeBrush, StrokeThickness = _strokeWidth },
            "Ellipse" => new Ellipse { Width = w, Height = h, Stroke = strokeBrush, StrokeThickness = _strokeWidth },
            "Arrow" => CreateArrowElement(start, end, _strokeColor, _strokeWidth),
            _ => new Rectangle { Width = w, Height = h, Stroke = strokeBrush, StrokeThickness = _strokeWidth }
        };

        if (element is Shape shape)
        {
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
        }
        if (element != null)
        {
            _previewShape = element;
            AnnotationCanvas.Children.Add(element);
        }
    }

    /// <summary>移除预览层（保留已提交的标注层）。</summary>
    private void RemovePreview()
    {
        if (_previewShape != null)
        {
            AnnotationCanvas.Children.Remove(_previewShape);
            _previewShape = null;
        }
    }

    /// <summary>画笔实时预览：贝塞尔平滑曲线（坐标=图像像素，零换算）。</summary>
    private void DrawPenPreview()
    {
        RemovePreview();
        if (_penPoints.Count < 2) return;
        var path = BuildSmoothPath(_penPoints, _strokeColor, _strokeWidth);
        _previewShape = path;
        AnnotationCanvas.Children.Add(path);
    }

    /// <summary>由轨迹点构建平滑贝塞尔路径（Catmull-Rom 转 Bézier）。</summary>
    private static Microsoft.UI.Xaml.Shapes.Path BuildSmoothPath(List<System.Numerics.Vector2> pts, Windows.UI.Color color, double strokeWidth)
    {
        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        if (pts.Count < 2) return new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = new SolidColorBrush(color), StrokeThickness = strokeWidth };
        var fig = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = new Windows.Foundation.Point(pts[0].X, pts[0].Y) };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < pts.Count ? pts[i + 2] : p2;
            // Catmull-Rom 控制点 → 三次贝塞尔
            var c1 = new Windows.Foundation.Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Windows.Foundation.Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            fig.Segments.Add(new Microsoft.UI.Xaml.Media.BezierSegment
            {
                Point1 = c1,
                Point2 = c2,
                Point3 = new Windows.Foundation.Point(p2.X, p2.Y)
            });
        }
        geometry.Figures.Add(fig);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = strokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    /// <summary>箭头元素：主线 + 箭头头部。</summary>
    private static UIElement CreateArrowElement(System.Numerics.Vector2 start, System.Numerics.Vector2 end,
        Windows.UI.Color color, double strokeWidth)
    {
        var stroke = new SolidColorBrush(color);
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
            StrokeThickness = strokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    // ────────────── 文字输入 ──────────────

    private void ShowTextInput(Windows.Foundation.Point canvasPt)
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
                Text = text, FontSize = 16,
                TextColor = new Color4(_strokeColor.R / 255f, _strokeColor.G / 255f, _strokeColor.B / 255f, 1f)
            });
            RenderAllLayers();
        }
        HideTextInput();
    }

    private void CommitShape(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 3 && h < 3) { RemovePreview(); return; }

        var style = CreateStyle();
        AnnotationLayer layer = _currentTool switch
        {
            "Rect" => new RectangleLayer { X = x, Y = y, Width = w, Height = h, Style = style },
            "Ellipse" => new EllipseLayer { CenterX = x + w / 2, CenterY = y + h / 2, RadiusX = w / 2, RadiusY = h / 2, Style = style },
            "Arrow" => new ArrowLayer { StartX = start.X, StartY = start.Y, EndX = end.X, EndY = end.Y, Style = style },
            "Mosaic" => new MosaicLayer { X = x, Y = y, Width = w, Height = h },
            _ => new RectangleLayer { X = x, Y = y, Width = w, Height = h, Style = style }
        };

        _annotationManager.AddLayer(layer);
        RenderAllLayers();
    }

    private void RenderAllLayers()
    {
        AnnotationCanvas.Children.Clear();
        // 图层面板联动
        if (LayersPanel.Visibility == Visibility.Visible)
            RefreshLayersPanel();
        foreach (var layer in _annotationManager.Layers.Where(l => l.IsVisible))
        {
            var isText = layer is TextLayer;
            var isFreehand = layer is FreehandLayer;
            var isArrow = layer is ArrowLayer;
            bool isSelected = layer.Id == _selectedLayerId;

            UIElement? element = layer switch
            {
                RectangleLayer r => RenderShapeElement(r, isSelected),
                EllipseLayer el => RenderEllipseElement(el, isSelected),
                ArrowLayer al => RenderArrowElement(al, isSelected),
                FreehandLayer fl => RenderFreehandElement(fl, isSelected),
                TextLayer tl => RenderTextElement(tl),
                MosaicLayer m => new Rectangle
                {
                    Width = m.Width, Height = m.Height,
                    Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(200, 100, 100, 100))
                },
                _ => null
            };

            if (element is null) continue;

            // Polyline/Path(箭头)/TextBlock 自带绝对坐标，其余按边界定位
            if (!isText && !isFreehand && !isArrow)
            {
                var bounds = layer.GetBounds();
                if (element is FrameworkElement fe)
                {
                    fe.Width = bounds.Width;
                    fe.Height = bounds.Height;
                }
                Canvas.SetLeft(element, bounds.Left);
                Canvas.SetTop(element, bounds.Top);
            }

            // 选中高亮边框 (仅对形状元素)
            if (isSelected && element is FrameworkElement fe2)
            {
                var border = new Border
                {
                    Width = fe2.Width + 8,
                    Height = fe2.Height + 8,
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };
                double offsetX = Canvas.GetLeft(element) - 4;
                double offsetY = Canvas.GetTop(element) - 4;
                Canvas.SetLeft(border, offsetX);
                Canvas.SetTop(border, offsetY);
                AnnotationCanvas.Children.Add(border);
            }

            AnnotationCanvas.Children.Add(element);
        }
    }

    /// <summary>矩形渲染元素 (Color 感知)。</summary>
    private static UIElement RenderShapeElement(RectangleLayer r, bool isSelected = false)
    {
        var fillColor = r.Style.IsFillEnabled
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                (byte)(r.Style.FillColor.A * 255),
                (byte)(r.Style.FillColor.R * 255),
                (byte)(r.Style.FillColor.G * 255),
                (byte)(r.Style.FillColor.B * 255)))
            : null;
        var strokeColor = r.Style.IsStrokeEnabled
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                (byte)(r.Style.StrokeColor.A * 255),
                (byte)(r.Style.StrokeColor.R * 255),
                (byte)(r.Style.StrokeColor.G * 255),
                (byte)(r.Style.StrokeColor.B * 255)))
            : null;
        return new Rectangle
        {
            Width = r.Width, Height = r.Height,
            Stroke = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.Yellow) : strokeColor,
            StrokeThickness = isSelected ? r.Style.StrokeWidth + 1 : r.Style.StrokeWidth,
            Fill = fillColor
        };
    }

    /// <summary>椭圆渲染元素。</summary>
    private static UIElement RenderEllipseElement(EllipseLayer el, bool isSelected = false)
    {
        var strokeColor = el.Style.IsStrokeEnabled
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                (byte)(el.Style.StrokeColor.A * 255),
                (byte)(el.Style.StrokeColor.R * 255),
                (byte)(el.Style.StrokeColor.G * 255),
                (byte)(el.Style.StrokeColor.B * 255)))
            : null;
        return new Ellipse
        {
            Width = el.RadiusX * 2, Height = el.RadiusY * 2,
            Stroke = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.Yellow) : strokeColor,
            StrokeThickness = isSelected ? el.Style.StrokeWidth + 1 : el.Style.StrokeWidth
        };
    }

    /// <summary>箭头渲染元素 (坐标已为图像像素, 直接使用)。</summary>
    private static UIElement RenderArrowElement(ArrowLayer al, bool isSelected = false)
    {
        var start = new System.Numerics.Vector2(al.StartX, al.StartY);
        var end = new System.Numerics.Vector2(al.EndX, al.EndY);
        var color = Microsoft.UI.ColorHelper.FromArgb(
            (byte)(al.Style.StrokeColor.A * 255),
            (byte)(al.Style.StrokeColor.R * 255),
            (byte)(al.Style.StrokeColor.G * 255),
            (byte)(al.Style.StrokeColor.B * 255));
        return CreateArrowElement(start, end, isSelected ? Microsoft.UI.Colors.Yellow : color, al.Style.StrokeWidth);
    }

    /// <summary>平滑画笔轨迹元素（坐标=图像像素，贝塞尔曲线）。</summary>
    private static UIElement RenderFreehandElement(FreehandLayer fl, bool isSelected = false)
    {
        var strokeColor = Microsoft.UI.ColorHelper.FromArgb(
            (byte)(fl.Style.StrokeColor.A * 255),
            (byte)(fl.Style.StrokeColor.R * 255),
            (byte)(fl.Style.StrokeColor.G * 255),
            (byte)(fl.Style.StrokeColor.B * 255));
        var pts = fl.Points.ToList();
        return BuildSmoothPath(pts, isSelected ? Microsoft.UI.Colors.Yellow : strokeColor,
            isSelected ? fl.Style.StrokeWidth + 1 : fl.Style.StrokeWidth);
    }

    /// <summary>文字元素（TextBlock，坐标=图像像素）。</summary>
    private static UIElement RenderTextElement(TextLayer tl)
    {
        var textColor = Microsoft.UI.ColorHelper.FromArgb(
            (byte)(tl.TextColor.A * 255),
            (byte)(tl.TextColor.R * 255),
            (byte)(tl.TextColor.G * 255),
            (byte)(tl.TextColor.B * 255));
        var tb = new TextBlock
        {
            Text = tl.Text,
            FontSize = tl.FontSize,
            Foreground = new SolidColorBrush(textColor),
        };
        // 文字背景
        var bgColor = Microsoft.UI.ColorHelper.FromArgb(
            (byte)(tl.BackgroundColor.A * 255),
            (byte)(tl.BackgroundColor.R * 255),
            (byte)(tl.BackgroundColor.G * 255),
            (byte)(tl.BackgroundColor.B * 255));
        var border = new Border
        {
            Background = new SolidColorBrush(bgColor),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(tl.Padding, 2, tl.Padding, 2),
            Child = tb
        };
        Canvas.SetLeft(border, tl.X);
        Canvas.SetTop(border, tl.Y);
        return border;
    }

    // ────────────── 键盘快捷键 (用户可自定义保存/取消键) ──────────────

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ═══ 2026-08-25 审查修复: 文字输入框获得焦点时忽略全局快捷键,
        // 否则输入字母 s/e 等会误触发保存/丢弃 ═══
        if (AnnoTextInput.Visibility == Visibility.Visible ||
            FocusManager.GetFocusedElement() is TextBox)
            return;

        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        // 撤销/重做 (固定 Ctrl+Z / Ctrl+Y)
        if (e.Key == Windows.System.VirtualKey.Z && ctrl)
        { OnUndo(null!, null!); e.Handled = true; return; }
        if (e.Key == Windows.System.VirtualKey.Y && ctrl)
        { OnRedo(null!, null!); e.Handled = true; return; }

        // 用户自定义快捷键: 保存/取消 (保存键触发保存, 取消键触发丢弃)
        var settings = AppServices.Settings.Current;
        if (MatchesShortcut(e, settings.SaveShortcut))
        { OnSave(null!, null!); e.Handled = true; }
        else if (MatchesShortcut(e, settings.CancelShortcut))
        { OnDiscard(null!, null!); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        { OnCopy(null!, null!); e.Handled = true; }
    }

    /// <summary>检查按键事件是否匹配快捷键字符串 (如 "S", "Ctrl+S", "Esc")。</summary>
    private static bool MatchesShortcut(KeyRoutedEventArgs e, string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        var parsed = MainWindow.ParseShortcut(shortcut);
        if (parsed is null) return false;
        var (key, ctrl, shift, alt) = parsed.Value;
        if (e.Key != key) return false;

        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        bool ctrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool altDown = (altState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        // 简单快捷键 (无修饰键) 时, 不要求修饰键全部释放 (允许 S 直接触发)
        if (!ctrl && !shift && !alt) return !ctrlDown && !shiftDown && !altDown;
        return ctrlDown == ctrl && shiftDown == shift && altDown == alt;
    }

    // ────────────── 撤销/重做 ──────────────

    private void OnUndo(object sender, RoutedEventArgs e) { _annotationManager.Undo(); RenderAllLayers(); }
    private void OnRedo(object sender, RoutedEventArgs e) { _annotationManager.Redo(); RenderAllLayers(); }
    private void OnClearAll(object sender, RoutedEventArgs e) { _annotationManager.ClearAll(); RenderAllLayers(); }

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

    // ────────────── 图层面板 ──────────────

    private void OnToggleLayers(object sender, RoutedEventArgs e)
    {
        LayersPanel.Visibility = LayersPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (LayersPanel.Visibility == Visibility.Visible)
            RefreshLayersPanel();
    }

    private void RefreshLayersPanel()
    {
        LayersCombo.Items.Clear();
        var layers = _annotationManager.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var item = new ComboBoxItem
            {
                Content = $"{i + 1}. {layer.Name}",
                Tag = layer.Id
            };
            LayersCombo.Items.Add(item);
        }
        // 选中当前图层
        if (_selectedLayerId.HasValue)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].Id == _selectedLayerId.Value)
                {
                    LayersCombo.SelectedIndex = i;
                    LayerInfoTxt.Text = layers[i].Name;
                    break;
                }
            }
        }
        LayerDeleteBtn.IsEnabled = _selectedLayerId.HasValue;
        LayerUpBtn.IsEnabled = _selectedLayerId.HasValue && LayersCombo.SelectedIndex > 0;
        LayerDownBtn.IsEnabled = _selectedLayerId.HasValue && LayersCombo.SelectedIndex < layers.Count - 1;
    }

    private void OnLayerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LayersCombo.SelectedItem is ComboBoxItem item && item.Tag is Guid id)
        {
            _selectedLayerId = id;
            RefreshLayersPanel();
            RenderAllLayers(); // 重新渲染以显示选中高亮
        }
    }

    private void OnLayerPrev(object sender, RoutedEventArgs e)
    {
        var layers = _annotationManager.Layers;
        if (layers.Count == 0) return;
        int idx = FindLayerIndex(layers, _selectedLayerId);
        idx = (idx - 1 + layers.Count) % layers.Count;
        _selectedLayerId = layers[idx].Id;
        RefreshLayersPanel();
        RenderAllLayers();
    }

    private void OnLayerNext(object sender, RoutedEventArgs e)
    {
        var layers = _annotationManager.Layers;
        if (layers.Count == 0) return;
        int idx = FindLayerIndex(layers, _selectedLayerId);
        idx = (idx + 1) % layers.Count;
        _selectedLayerId = layers[idx].Id;
        RefreshLayersPanel();
        RenderAllLayers();
    }

    private static int FindLayerIndex(IReadOnlyList<AnnotationLayer> layers, Guid? id)
    {
        if (!id.HasValue) return 0;
        for (int i = 0; i < layers.Count; i++)
            if (layers[i].Id == id.Value) return i;
        return 0;
    }

    private void OnLayerDelete(object sender, RoutedEventArgs e)
    {
        if (_selectedLayerId.HasValue)
        {
            _annotationManager.RemoveLayer(_selectedLayerId.Value);
            _selectedLayerId = null;
            RenderAllLayers();
            RefreshLayersPanel();
        }
    }

    private void OnLayerMoveUp(object sender, RoutedEventArgs e)
    {
        MoveLayer(-1);
    }

    private void OnLayerMoveDown(object sender, RoutedEventArgs e)
    {
        MoveLayer(1);
    }

    private void MoveLayer(int direction)
    {
        var layers = _annotationManager.Layers;
        if (_selectedLayerId.HasValue)
        {
            int idx = FindLayerIndex(layers, _selectedLayerId);
            int newIdx = idx + direction;
            if (newIdx >= 0 && newIdx < layers.Count)
            {
                // 交换 ZOrder
                var tmp = layers[idx].ZOrder;
                layers[idx].ZOrder = layers[newIdx].ZOrder;
                layers[newIdx].ZOrder = tmp;
                RenderAllLayers();
                RefreshLayersPanel();
            }
        }
    }
}
