using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using WinRT.Interop;
using TrueToneCap.Core.Annotation;

namespace TrueToneCap.App;

public sealed partial class SelectionOverlay : Window
{
    // ── 选区状态 ──
    private Windows.Foundation.Point _startPoint;
    private bool _isDragging;
    private bool _isClick;
    private bool _selectionComplete;

    // ── 标注状态 ──
    private bool _isAnnotating;
    private readonly AnnotationManager _annotationManager = new();
    private string _currentAnnoTool = "Rect";
    private System.Numerics.Vector2 _annoDragStart;
    private bool _isAnnoDrawing;

    public RectInt32 SelectedRect { get; private set; }

    public byte[] DesktopPixels { get; }
    public int DesktopWidth { get; }
    public int DesktopHeight { get; }

    /// <summary>标注完成后的最终像素（调用方在 ActionCompleted 后读取）。</summary>
    public byte[]? AnnotatedRegionPixels { get; private set; }

    public enum ActionResult { Cancel, Confirm, Annotate, Copy, Ocr, Translate }
    public event Action<ActionResult, RectInt32>? ActionCompleted;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_SYSMENU = 0x00080000;

    private readonly int _vx, _vy, _vw, _vh;  // 物理像素
    private double _dpiScale = 1.0;
    private bool _bgRendered;

    // ── 选区在覆盖层中的有效像素位置（用于标注画布定位） ──
    private double _selEffX1, _selEffY1, _selEffW, _selEffH;

    public SelectionOverlay(byte[] desktopPixels, int vx, int vy, int vw, int vh)
    {
        DesktopPixels = desktopPixels;
        DesktopWidth = vw;
        DesktopHeight = vh;
        _vx = vx; _vy = vy; _vw = vw; _vh = vh;

        this.InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        appWindow.IsShownInSwitchers = false;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetWindowLong(hwnd, GWL_STYLE, style);

        this.Activated += (_, _) =>
        {
            _ = SetWindowPos(hwnd, HWND_TOPMOST, vx, vy, vw, vh, SWP_SHOWWINDOW);
            // 获取 DPI 缩放：主路径 GetDpiForWindow，回落 XamlRoot.RasterizationScale
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
                _dpiScale = dpi / 96.0;
            else
                _dpiScale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            System.Diagnostics.Debug.WriteLine($"[SelectionOverlay] DPI={dpi} Scale={_dpiScale:F2}");
            DispatcherQueue.TryEnqueue(() => RootGrid.Focus(FocusState.Keyboard));
            if (!_bgRendered) { _bgRendered = true; RenderDesktopBackground(desktopPixels, vw, vh); }
        };

        try { appWindow.MoveAndResize(new RectInt32(vx, vy, vw, vh)); }
        catch { }

        RootGrid.KeyDown += OnKeyDown;
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerReleased += OnPointerReleased;

        // ── 标注画布鼠标事件（在画布元素上） ──
        AnnotationCanvas.PointerPressed += OnAnnoCanvasPressed;
        AnnotationCanvas.PointerMoved += OnAnnoCanvasMoved;
        AnnotationCanvas.PointerReleased += OnAnnoCanvasReleased;
    }

    // ═══════════════════════════════════════
    //  键盘
    // ═══════════════════════════════════════

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isAnnotating)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    ExitAnnotationMode();
                    e.Handled = true;
                    break;
                case VirtualKey.Z when IsCtrlPressed():
                    OnAnnoUndo(null!, null!);
                    e.Handled = true;
                    break;
                case VirtualKey.Y when IsCtrlPressed():
                    OnAnnoRedo(null!, null!);
                    e.Handled = true;
                    break;
            }
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                Finish(ActionResult.Cancel);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (_selectionComplete) { Finish(ActionResult.Confirm); e.Handled = true; }
                break;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private static bool IsCtrlPressed() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    /// <summary>SafeDPI helper — defends against Activated not firing.</summary>
    private double GetSafeDpiScale()
    {
        if (_dpiScale <= 0.01)
            _dpiScale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (_dpiScale <= 0.01)
            _dpiScale = 1.0;
        return _dpiScale;
    }

    // ---- Selection methods ----

    /// <summary>在选区四边绘制蓝色细线 + 尺寸标签。</summary>
    private void DrawSelectionLines(double x, double y, double w, double h, string sizeText)
    {
        // 四条边线
        SelLineTop.Visibility = Visibility.Visible;
        SelLineTop.Width = w;
        SelLineTop.Margin = new Thickness(x, y, 0, 0);

        SelLineBottom.Visibility = Visibility.Visible;
        SelLineBottom.Width = w;
        SelLineBottom.Margin = new Thickness(x, y + h - 1.5, 0, 0);

        SelLineLeft.Visibility = Visibility.Visible;
        SelLineLeft.Height = h;
        SelLineLeft.Margin = new Thickness(x, y, 0, 0);

        SelLineRight.Visibility = Visibility.Visible;
        SelLineRight.Height = h;
        SelLineRight.Margin = new Thickness(x + w - 1.5, y, 0, 0);

        // 尺寸标签
        SizeLabel.Text = sizeText;
        SizeLabelBorder.Visibility = Visibility.Visible;
        SizeLabelBorder.Margin = new Thickness(x + (w - 60) / 2, y - 22, 0, 0);
    }

    /// <summary>隐藏选区线条。</summary>
    private void HideSelectionLines()
    {
        SelLineTop.Visibility = Visibility.Collapsed;
        SelLineBottom.Visibility = Visibility.Collapsed;
        SelLineLeft.Visibility = Visibility.Collapsed;
        SelLineRight.Visibility = Visibility.Collapsed;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
    }

    private void SelectFullScreen()
    {
        SelectedRect = new RectInt32(_vx, _vy, _vw, _vh);
        _selectionComplete = true;

        double scale = GetSafeDpiScale();
        double effW = _vw / scale, effH = _vh / scale;
        _selEffX1 = 0; _selEffY1 = 0; _selEffW = effW; _selEffH = effH;

        DimOverlay.Visibility = Visibility.Collapsed;
        MaskGrid.Visibility = Visibility.Collapsed;

        DrawSelectionLines(0, 0, effW, effH, $"{_vw} × {_vh}");

        PositionToolbarAt(effW - 460, effH - 55);
        HintText.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Keyboard);
    }

    private void ApplyCustomSelection(double x1, double y1, double x2, double y2)
    {
        double w = x2 - x1, h = y2 - y1;
        _selEffX1 = x1; _selEffY1 = y1; _selEffW = w; _selEffH = h;

        double scale = GetSafeDpiScale();
        SelectedRect = new RectInt32(
            _vx + (int)(x1 * scale),
            _vy + (int)(y1 * scale),
            (int)(w * scale),
            (int)(h * scale));
        _selectionComplete = true;

        DimOverlay.Visibility = Visibility.Collapsed;
        MaskGrid.Visibility = Visibility.Visible;
        MaskLeft.Width = x1;
        MaskTop.Height = y1;
        MaskRight.Width = RootGrid.ActualWidth - x2;
        MaskBottom.Height = RootGrid.ActualHeight - y2;

        DrawSelectionLines(x1, y1, w, h, $"{(int)(w * scale)} × {(int)(h * scale)}");

        PositionToolbarNearSelection(x1, y1, x2, y2);
        HintText.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Keyboard);
    }

    private void PositionToolbarAt(double tx, double ty)
    {
        double scale = GetSafeDpiScale();
        double effW = _vw / scale, effH = _vh / scale;
        tx = Math.Max(0, Math.Min(tx, effW - 460));
        ty = Math.Max(0, Math.Min(ty, effH - 50));
        Toolbar.Margin = new Thickness(tx, ty, 0, 0);
        Toolbar.Visibility = Visibility.Visible;
    }

    private void PositionToolbarNearSelection(double x1, double y1, double x2, double y2)
    {
        double effH = _vh / GetSafeDpiScale();
        double tx = x2 - 460, ty = y2 + 5;
        if (ty + 45 > effH) ty = y1 - 50;
        if (ty < 0) ty = y2 - 50;
        if (tx < 0) tx = x1 + 5;
        PositionToolbarAt(tx, ty);
    }

    // ═══════════════════════════════════════
    //  渲染
    // ═══════════════════════════════════════

    private void RenderDesktopBackground(byte[] bgra, int w, int h)
    {
        try
        {
            var wb = new WriteableBitmap(w, h);
            using (var stream = wb.PixelBuffer.AsStream()) { stream.Write(bgra, 0, bgra.Length); }
            wb.Invalidate();
            DesktopImage.Source = wb;
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            DimOverlay.Visibility = Visibility.Visible;
            HintText.Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Collapsed;
            AnnotationToolbar.Visibility = Visibility.Collapsed;
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            HideSelectionLines();
            MaskGrid.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { HintText.Text = $"⚠ 背景渲染失败: {ex.Message}"; HintText.Visibility = Visibility.Visible; }
    }

    public byte[]? ExtractRegionPixels(RectInt32 screenRect)
    {
        int rx = screenRect.X - _vx, ry = screenRect.Y - _vy;
        int rw = screenRect.Width, rh = screenRect.Height;
        if (rx < 0 || ry < 0 || rx + rw > DesktopWidth || ry + rh > DesktopHeight) return null;

        var result = new byte[rw * rh * 4];
        int srcStride = DesktopWidth * 4, dstStride = rw * 4;
        for (int row = 0; row < rh; row++)
            Buffer.BlockCopy(DesktopPixels, ((ry + row) * srcStride) + (rx * 4), result, row * dstStride, dstStride);
        return result;
    }

    // ═══════════════════════════════════════
    //  鼠标选区
    // ═══════════════════════════════════════

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isAnnotating) return; // 标注模式下不处理选区鼠标
        if (IsDescendantOf(e.OriginalSource as DependencyObject, Toolbar)) return;
        if (IsDescendantOf(e.OriginalSource as DependencyObject, AnnotationToolbar)) return;

        if (_selectionComplete) ResetSelection();
        var pt = e.GetCurrentPoint(RootGrid).Position;
        _startPoint = new Windows.Foundation.Point(pt.X, pt.Y);
        _isDragging = true; _isClick = true;
        HintText.Visibility = Visibility.Collapsed;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _selectionComplete || _isAnnotating) return;
        var pt = e.GetCurrentPoint(RootGrid).Position;
        if (Math.Abs(pt.X - _startPoint.X) > 3 || Math.Abs(pt.Y - _startPoint.Y) > 3) _isClick = false;
        if (_isClick) return;

        double x1 = Math.Min(_startPoint.X, pt.X), y1 = Math.Min(_startPoint.Y, pt.Y);
        double x2 = Math.Max(_startPoint.X, pt.X), y2 = Math.Max(_startPoint.Y, pt.Y);
        double w = x2 - x1, h = y2 - y1;
        if (w < 2 || h < 2) return;

        DimOverlay.Visibility = Visibility.Collapsed;
        MaskGrid.Visibility = Visibility.Visible;
        MaskLeft.Width = x1; MaskTop.Height = y1;
        MaskRight.Width = RootGrid.ActualWidth - x2;
        MaskBottom.Height = RootGrid.ActualHeight - y2;

        DrawSelectionLines(x1, y1, w, h, $"{(int)(w * GetSafeDpiScale())} × {(int)(h * GetSafeDpiScale())}");
        Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _isAnnotating) return;
        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        if (_isClick) { SelectFullScreen(); return; }

        var pt = e.GetCurrentPoint(RootGrid).Position;
        int x1 = (int)Math.Min(_startPoint.X, pt.X), y1 = (int)Math.Min(_startPoint.Y, pt.Y);
        int x2 = (int)Math.Max(_startPoint.X, pt.X), y2 = (int)Math.Max(_startPoint.Y, pt.Y);
        if (x2 - x1 < 10 || y2 - y1 < 10) { SelectFullScreen(); return; }
        ApplyCustomSelection(x1, y1, x2, y2);
    }

    // ═══════════════════════════════════════
    //  工具栏按钮
    // ═══════════════════════════════════════

    private void OnConfirm(object sender, RoutedEventArgs e) => Finish(ActionResult.Confirm);
    private void OnCopy(object sender, RoutedEventArgs e) => Finish(ActionResult.Copy);
    private void OnOcr(object sender, RoutedEventArgs e) => Finish(ActionResult.Ocr);
    private void OnTranslate(object sender, RoutedEventArgs e) => Finish(ActionResult.Translate);
    private void OnCancel(object sender, RoutedEventArgs e) => Finish(ActionResult.Cancel);

    private void OnAnnotate(object sender, RoutedEventArgs e) => EnterAnnotationMode();

    private void ResetSelection()
    {
        _selectionComplete = false; _isDragging = false; _isClick = false;
        DimOverlay.Visibility = Visibility.Visible;
        MaskGrid.Visibility = Visibility.Collapsed;
        MaskLeft.Width = MaskTop.Height = MaskRight.Width = MaskBottom.Height = 0;
        HideSelectionLines();
        Toolbar.Visibility = Visibility.Collapsed;
        AnnotationToolbar.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
    }

    private void Finish(ActionResult result)
    {
        // 标注模式下先退出标注
        if (_isAnnotating) ExitAnnotationMode();

        // 在关闭前将标注合成到像素
        if (result is ActionResult.Confirm or ActionResult.Copy)
            AnnotatedRegionPixels = GetAnnotatedRegionPixels();

        ActionCompleted?.Invoke(result, SelectedRect);
        this.Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_selectionComplete) ActionCompleted?.Invoke(ActionResult.Cancel, default);
    }

    // ═══════════════════════════════════════
    //  标注模式
    // ═══════════════════════════════════════

    private void EnterAnnotationMode()
    {
        _isAnnotating = true;

        // 定位标注画布：覆盖选区区域
        AnnotationCanvas.Width = _selEffW;
        AnnotationCanvas.Height = _selEffH;
        AnnotationCanvas.Margin = new Thickness(_selEffX1, _selEffY1, 0, 0);
        AnnotationCanvas.Visibility = Visibility.Visible;

        // 切换到标注工具栏
        Toolbar.Visibility = Visibility.Collapsed;
        PositionAnnotationToolbar();
        AnnotationToolbar.Visibility = Visibility.Visible;
        UpdateAnnoToolHighlights();

        HintText.Text = "拖拽绘制标注  |  Esc = 退出标注";
        HintText.Visibility = Visibility.Visible;
    }

    private void ExitAnnotationMode()
    {
        _isAnnotating = false;
        _isAnnoDrawing = false;
        AnnotationCanvas.Visibility = Visibility.Collapsed;
        AnnotationCanvas.Children.Clear();
        AnnotationToolbar.Visibility = Visibility.Collapsed;

        Toolbar.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Collapsed;
    }

    private void PositionAnnotationToolbar()
    {
        double scale = GetSafeDpiScale();
        double effH = _vh / scale;
        double tx = _selEffX1 + _selEffW - 434;
        double ty = _selEffY1 + _selEffH + 5;
        if (ty + 45 > effH) ty = _selEffY1 - 50;
        if (ty < 0) ty = _selEffY1 + _selEffH - 50;
        if (tx < 0) tx = _selEffX1 + 5;
        tx = Math.Max(0, Math.Min(tx, (_vw / scale) - 440));
        AnnotationToolbar.Margin = new Thickness(tx, ty, 0, 0);
    }

    // ── 标注工具选择 ──

    private void OnAnnoToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentAnnoTool = tag;
            UpdateAnnoToolHighlights();
        }
    }

    private void UpdateAnnoToolHighlights()
    {
        var btns = new[] { AnnoRectBtn, AnnoEllipseBtn, AnnoArrowBtn, AnnoPenBtn, AnnoTextBtn, AnnoMosaicBtn };
        var tags = new[] { "Rect", "Ellipse", "Arrow", "Pen", "Text", "Mosaic" };
        for (int i = 0; i < btns.Length; i++)
            btns[i].Background = tags[i] == _currentAnnoTool
                ? (Brush)Application.Current.Resources["AccentBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ── 坐标转换（画布有效像素 ↔ 选区原始图像像素） ──

    private System.Numerics.Vector2 CanvasToRegionImage(System.Numerics.Vector2 canvasPt)
    {
        int imgW = SelectedRect.Width, imgH = SelectedRect.Height;
        if (AnnotationCanvas.Width <= 0 || AnnotationCanvas.Height <= 0) return canvasPt;
        return new System.Numerics.Vector2(
            canvasPt.X * imgW / (float)AnnotationCanvas.Width,
            canvasPt.Y * imgH / (float)AnnotationCanvas.Height);
    }

    private System.Numerics.Vector2 ImageToCanvas(System.Numerics.Vector2 imagePt)
    {
        int imgW = SelectedRect.Width, imgH = SelectedRect.Height;
        if (imgW <= 0 || imgH <= 0) return imagePt;
        return new System.Numerics.Vector2(
            imagePt.X * (float)AnnotationCanvas.Width / imgW,
            imagePt.Y * (float)AnnotationCanvas.Height / imgH);
    }

    // ── 标注绘制 ──

    private void OnAnnoCanvasPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAnnotating) return;
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        _annoDragStart = CanvasToRegionImage(canvasPt);
        _isAnnoDrawing = true;
        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void OnAnnoCanvasMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAnnoDrawing) return;
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        DrawAnnoPreview(ImageToCanvas(_annoDragStart), new System.Numerics.Vector2((float)pt.X, (float)pt.Y));
    }

    private void OnAnnoCanvasReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAnnoDrawing) return;
        _isAnnoDrawing = false;
        AnnotationCanvas.ReleasePointerCaptures();
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var endCanvas = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        CommitAnnoShape(_annoDragStart, CanvasToRegionImage(endCanvas));
    }

    private void DrawAnnoPreview(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        AnnotationCanvas.Children.Clear();

        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 2 && h < 2) return;

        Shape? shape = _currentAnnoTool switch
        {
            "Rect" => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Ellipse" => new Ellipse { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            "Arrow" => new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 },
            _ => new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 2 }
        };

        if (shape != null)
        {
            Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y);
            AnnotationCanvas.Children.Add(shape);
        }
    }

    private void CommitAnnoShape(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 3 && h < 3) { AnnotationCanvas.Children.Clear(); return; }

        AnnotationLayer layer = _currentAnnoTool switch
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
        RenderAllAnnoLayers();
    }

    private void RenderAllAnnoLayers()
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
                var canvasPos = ImageToCanvas(new System.Numerics.Vector2(bounds.Left, bounds.Top));
                var canvasSz = ImageToCanvas(new System.Numerics.Vector2(bounds.Right, bounds.Bottom))
                    - new System.Numerics.Vector2(canvasPos.X, canvasPos.Y);
                shape.Width = Math.Abs(canvasSz.X); shape.Height = Math.Abs(canvasSz.Y);
                Canvas.SetLeft(shape, canvasPos.X); Canvas.SetTop(shape, canvasPos.Y);
                AnnotationCanvas.Children.Add(shape);
            }
        }
    }

    // ── 标注撤销/重做 ──

    private void OnAnnoUndo(object sender, RoutedEventArgs e) { _annotationManager.Undo(); RenderAllAnnoLayers(); }
    private void OnAnnoRedo(object sender, RoutedEventArgs e) { _annotationManager.Redo(); RenderAllAnnoLayers(); }
    private void OnAnnoDone(object sender, RoutedEventArgs e) => ExitAnnotationMode();

    // ═══════════════════════════════════════
    //  标注合成
    // ═══════════════════════════════════════

    /// <summary>提取选区像素并合成标注，返回最终 BGRA 字节。</summary>
    public byte[]? GetAnnotatedRegionPixels()
    {
        var region = ExtractRegionPixels(SelectedRect);
        if (region is null) return null;
        if (_annotationManager.Layers.Count == 0) return region;

        int imgW = SelectedRect.Width, imgH = SelectedRect.Height;
        var result = new byte[region.Length];
        Buffer.BlockCopy(region, 0, result, 0, region.Length);

        foreach (var layer in _annotationManager.Layers.Where(l => l.IsVisible))
        {
            var bounds = layer.GetBounds();
            int lx = Math.Max(0, (int)bounds.Left), ly = Math.Max(0, (int)bounds.Top);
            int rx = Math.Min(imgW - 1, (int)bounds.Right), ry = Math.Min(imgH - 1, (int)bounds.Bottom);

            if (layer is MosaicLayer)
            {
                for (int y = ly; y <= ry; y += 6)
                for (int x = lx; x <= rx; x += 6)
                {
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (int dy = 0; dy < 6 && y + dy <= ry; dy++)
                    for (int dx = 0; dx < 6 && x + dx <= rx; dx++)
                    { int idx = ((y + dy) * imgW + (x + dx)) * 4; b += result[idx]; g += result[idx + 1]; r += result[idx + 2]; cnt++; }
                    byte av = (byte)((r + g + b) / (cnt * 3));
                    for (int dy = 0; dy < 6 && y + dy <= ry; dy++)
                    for (int dx = 0; dx < 6 && x + dx <= rx; dx++)
                    { int idx = ((y + dy) * imgW + (x + dx)) * 4; result[idx] = result[idx + 1] = result[idx + 2] = av; }
                }
            }
            else
            {
                int t = 2;
                for (int y = ly; y <= Math.Min(ly + t, ry); y++)
                for (int x = lx; x <= rx; x++)
                { int idx = (y * imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int y = Math.Max(ly, ry - t); y <= ry; y++)
                for (int x = lx; x <= rx; x++)
                { int idx = (y * imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int x = lx; x <= Math.Min(lx + t, rx); x++)
                for (int y = ly; y <= ry; y++)
                { int idx = (y * imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
                for (int x = Math.Max(lx, rx - t); x <= rx; x++)
                for (int y = ly; y <= ry; y++)
                { int idx = (y * imgW + x) * 4; result[idx] = 0; result[idx + 1] = 0; result[idx + 2] = 255; }
            }
        }
        return result;
    }

    // ═══════════════════════════════════════
    //  工具方法
    // ═══════════════════════════════════════

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null) { if (child == parent) return true; child = VisualTreeHelper.GetParent(child); }
        return false;
    }
}

