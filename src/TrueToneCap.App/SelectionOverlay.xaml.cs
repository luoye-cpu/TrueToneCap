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
using TrueToneCap.Core.Detection;
using TrueToneCap.App.Services;

namespace TrueToneCap.App;

public sealed partial class SelectionOverlay : Window
{
    // ── 选区状态 ──
    private Windows.Foundation.Point _startPoint;
    private bool _isDragging;
    private bool _isClick;
    private bool _selectionComplete;
    private bool _finished; // 防止 ActionCompleted 双重触发

    // ── 1 分钟无操作超时自动取消（防止覆盖层卡死无法操作）──
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timeoutTimer;
    private readonly System.Diagnostics.Stopwatch _idleWatch = System.Diagnostics.Stopwatch.StartNew();
    private const int SelectionTimeoutMs = 60 * 1000; // 1 分钟无操作 → 自动取消

    // ── 硬看门狗：3 分钟无论什么情况强制退出（UI 线程卡死时兜底）──
    private System.Threading.Timer? _hardWatchdog;
    private const int HardExitMs = 3 * 60 * 1000; // 3 分钟硬上限
    private volatile bool _closed; // 窗口是否已关闭（看门狗线程检查，不依赖 UI 线程）

    // ── 自动识别区域 ──
    private List<DetectedRegion> _detectedRegions = [];
    private int _hoveredRegionIndex = -1;

    // ── 标注状态 ──
    private bool _isAnnotating;
    private readonly AnnotationManager _annotationManager = new();
    private string _currentAnnoTool = "Rect";
    private System.Numerics.Vector2 _annoDragStart;
    private bool _isAnnoDrawing;
    // 画笔轨迹累积（拖动期间逐点追加，完成后一次性提交为 FreehandLayer）
    private readonly List<System.Numerics.Vector2> _penPoints = [];
    // 文字插入位置（选区图像坐标）
    private System.Numerics.Vector2 _textInsertPos;

    public RectInt32 SelectedRect { get; private set; }

    public byte[] DesktopPixels { get; }
    public int DesktopWidth { get; }
    public int DesktopHeight { get; }

    /// <summary>标注完成后的最终像素（调用方在 ActionCompleted 后读取）。</summary>
    public byte[]? AnnotatedRegionPixels { get; private set; }

    public enum ActionResult { Cancel, Confirm, Annotate, Copy, Ocr, Translate }
    public event Action<ActionResult, RectInt32>? ActionCompleted;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);
    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly int _vx, _vy, _vw, _vh;  // 物理像素
    private double _dpiScale = 1.0;
    private bool _bgRendered;

    // ── 选区在覆盖层中的有效像素位置（用于标注画布定位） ──
    private double _selEffX1, _selEffY1, _selEffW, _selEffH;

    // ── HDR 背景窗口（可选，当 HDR 捕获可用时使用） ──
    private HdrPreviewWindow? _hdrBgWnd;
    private float[]? _hdrPixels;
    private int _hdrW, _hdrH;

    /// <summary>SDR 构造：使用桌面 BGRA 像素作为背景。</summary>
    public SelectionOverlay(byte[] desktopPixels, int vx, int vy, int vw, int vh)
        : this(desktopPixels, vx, vy, vw, vh, null, 0, 0) { }

    /// <summary>HDR 构造：额外接收 HDR 浮点帧用于 HDR 背景窗口。</summary>
    public SelectionOverlay(byte[] desktopPixels, int vx, int vy, int vw, int vh,
        float[]? hdrPixels, int hdrW, int hdrH)
    {
        DesktopPixels = desktopPixels;
        DesktopWidth = vw;
        DesktopHeight = vh;
        _vx = vx; _vy = vy; _vw = vw; _vh = vh;
        _hdrPixels = hdrPixels;
        _hdrW = hdrW;
        _hdrH = hdrH;
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

        // ═══ Win32 窗口样式设置（非致命：失败时不影响覆盖层基本功能）═══
        try
        {
            int style = GetWindowLongW(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
            SetWindowLongW(hwnd, GWL_STYLE, style);

            // 移除任务栏图标 + 防止意外激活（保持为透明覆盖层）
            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE;
            exStyle &= ~WS_EX_APPWINDOW;
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelectionOverlay] 窗口样式设置失败（非致命）: {ex.Message}");
        }

        this.Activated += (_, _) =>
        {
            // 全屏无边框覆盖：覆盖整个虚拟桌面（支持负坐标多屏）
            _ = SetWindowPos(hwnd, HWND_TOPMOST, vx, vy, vw, vh,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            // ═══ 创建 GPU 背景窗口 (在覆盖层下方, DWM 透明穿透可见) ═══
            // 2026-08-09: 统一 HDR(float) 与 SDR(BGRA8) 走 GPU 呈现, 消除 WriteableBitmap CPU 合成
            // 2026-08-10 保护: ① 改用 GetSharedDevice() 只取缓存设备 — 绝不用 GetOrCreateDevice(光标显示器),
            //    否则多屏/光标移动时旧设备被 Dispose, WGC 会话挂起 → UI 等 GPU 锁 → 整个应用卡死
            //    ② 呈现移出 UI 线程 (Task.Run + 3s 超时) — UI 线程绝不等待无限时的 GPU 操作
            //    ③ 失败回退 CPU 渲染 (RenderDesktopBackground), 覆盖层功能永不因 GPU 问题不可用
            bool gpuBgOk = false;
            try
            {
                var sharedDevice = AppServices.Wgc?.GetSharedDevice();
                _hdrBgWnd = new HdrPreviewWindow(sharedDevice, AppServices.Wgc?.D3dContextLock);
                int bgW = _hdrW > 0 ? _hdrW : vw;
                int bgH = _hdrH > 0 ? _hdrH : vh;
                if (_hdrBgWnd.Initialize(vx, vy, bgW, bgH))
                {
                    // 呈现放后台线程，UI 线程最多等 3s（正常 <100ms）；锁内部还有 500ms TryEnter 保护
                    var pixels = _hdrPixels;
                    int pw = _hdrW, ph = _hdrH;
                    var bgWnd = _hdrBgWnd;
                    bool presentOk = false;
                    try
                    {
                        presentOk = Task.Run(() =>
                        {
                            if (pixels is not null && pw > 0 && ph > 0)
                            {
                                bgWnd.PresentFrame(pixels, pw, ph);
                                LogService.Info("Overlay", $"GPU 背景窗口 (HDR) 已创建 {pw}x{ph}", LogCategory.Capture);
                            }
                            else if (desktopPixels is not null)
                            {
                                // 2026-08-11: SDR 数据转 scRGB 线性后走统一 Float16 呈现 —
                                // DWM 色域感知管线处理，广色域正确性不依赖 ACM
                                var scrgb = TrueToneCap.Core.PixelOps.BgraToScrgbLinearFast(desktopPixels, vw, vh);
                                bgWnd.PresentFrame(scrgb, vw, vh);
                                LogService.Info("Overlay", $"GPU 背景窗口 (SDR→scRGB) 已创建 {vw}x{vh}", LogCategory.Capture);
                            }
                            return true;
                        }).Wait(3000);
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("Overlay", $"GPU 呈现异常: {ex.Message}", LogCategory.Capture);
                    }
                    if (!presentOk)
                        LogService.Warn("Overlay", "GPU 呈现超时 (3s)，回退 CPU 渲染", LogCategory.Capture);

                    gpuBgOk = presentOk;
                }
                else
                {
                    _hdrBgWnd.Dispose();
                    _hdrBgWnd = null;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Overlay", $"GPU 背景创建失败: {ex.Message}", LogCategory.Capture);
                _hdrBgWnd?.Dispose();
                _hdrBgWnd = null;
            }

            // 防御: 背景窗口创建后再置顶覆盖层, 确保覆盖层始终在背景窗口之上接收鼠标事件
            _ = SetWindowPos(hwnd, HWND_TOPMOST, vx, vy, vw, vh,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            // 全透明窗口：margins 全部 -1 使整个客户区透明，
            // 让下层 HDR 独立窗口的 scRGB 内容可见
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            // 获取 DPI 缩放：主路径 GetDpiForWindow，回落 XamlRoot.RasterizationScale
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
                _dpiScale = dpi / 96.0;
            else
                _dpiScale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            System.Diagnostics.Debug.WriteLine($"[SelectionOverlay] DPI={dpi} Scale={_dpiScale:F2} GPU背景={gpuBgOk}");
            LogService.Info("Overlay", $"覆盖层就绪 DPI={dpi} Scale={_dpiScale:F2} GPU背景={gpuBgOk} 虚拟桌面={vw}x{vh}@({vx},{vy})", LogCategory.UI);

            DispatcherQueue.TryEnqueue(() => RootGrid.Focus(FocusState.Keyboard));

            // 启动 3 分钟无操作超时（窗口真正显示后开始计时）
            if (_timeoutTimer is { IsRunning: false } && !_finished)
                _timeoutTimer.Start();

            if (!_bgRendered)
            {
                _bgRendered = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // 2026-08-09: GPU 背景成功时跳过 WriteableBitmap CPU 合成
                if (!gpuBgOk && desktopPixels is not null)
                    RenderDesktopBackground(desktopPixels, vw, vh);
                System.Diagnostics.Debug.WriteLine($"[⏱ Overlay] 背景渲染: {sw.ElapsedMilliseconds}ms (GPU={gpuBgOk})");
                LogService.Info("Overlay", $"背景渲染 {sw.ElapsedMilliseconds}ms (GPU={gpuBgOk})", LogCategory.Capture);
                DetectAndRenderRegions();
            }
        };

        try { appWindow.MoveAndResize(new RectInt32(vx, vy, vw, vh)); }
        catch { }

        RootGrid.KeyDown += OnKeyDown;
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerReleased += OnPointerReleased;

        // ── 字体注入（使用用户选择的字体） ──
        var fontFamily = FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily);
        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);

        // ── 标注画布鼠标事件（在画布元素上） ──
        AnnotationCanvas.PointerPressed += OnAnnoCanvasPressed;
        AnnotationCanvas.PointerMoved += OnAnnoCanvasMoved;
        AnnotationCanvas.PointerReleased += OnAnnoCanvasReleased;

        // ── 1 分钟无操作超时自动取消 ──
        _timeoutTimer = DispatcherQueue.CreateTimer();
        _timeoutTimer.Interval = TimeSpan.FromSeconds(5);
        _timeoutTimer.IsRepeating = true;
        _timeoutTimer.Tick += (_, _) =>
        {
            if (_idleWatch.ElapsedMilliseconds >= SelectionTimeoutMs)
            {
                LogService.Info("Overlay", "1 分钟无操作，自动取消", LogCategory.Capture);
                Finish(ActionResult.Cancel);
            }
        };

        // ── 硬看门狗：3 分钟后无论什么情况强制退出（线程池线程，UI 卡死也能触发）──
        _hardWatchdog = new System.Threading.Timer(HardWatchdogTick, null, HardExitMs, Timeout.Infinite);
    }

    /// <summary>硬看门狗回调（线程池线程执行，不依赖 UI 消息泵）。</summary>
    private void HardWatchdogTick(object? state)
    {
        if (_closed || _finished) return; // 窗口已正常关闭
        System.Diagnostics.Debug.WriteLine("[SelectionOverlay] 硬看门狗触发: 3 分钟强制退出");

        // 先尝试优雅关闭（UI 线程活着时正常走 Finish(Cancel)）
        try
        {
            DispatcherQueue.TryEnqueue(() => Finish(ActionResult.Cancel));
        }
        catch { }

        // 给 UI 线程 5 秒处理时间；仍未关闭说明 UI 卡死 → 强制退出进程
        Thread.Sleep(5000);
        if (!_closed && !_finished)
        {
            System.Diagnostics.Debug.WriteLine("[SelectionOverlay] UI 无响应，强制退出进程");
            try { LogService.Error("SelectionOverlay", "硬看门狗: UI 无响应，强制退出"); } catch { }
            Environment.Exit(0);
        }
    }

    /// <summary>重置超时计时（任何用户交互时调用）。</summary>
    private void TouchIdle() => _idleWatch.Restart();

    /// <summary>外部强制取消（应用退出时由 MainWindow 调用，触发 ActionCompleted + 关闭）。</summary>
    public void Cancel() => Finish(ActionResult.Cancel);

    // ═══════════════════════════════════════
    //  键盘
    // ═══════════════════════════════════════

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        TouchIdle();
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

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
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
        // 钳制遮罩尺寸为非负，防止布局未完成(ActualWidth=0)或坐标越界导致负值/NaN → WinRT set_Width 崩溃
        MaskLeft.Width = Math.Max(0, x1);
        MaskTop.Height = Math.Max(0, y1);
        MaskRight.Width = Math.Max(0, RootGrid.ActualWidth - x2);
        MaskBottom.Height = Math.Max(0, RootGrid.ActualHeight - y2);

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
        TouchIdle();
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
        TouchIdle();
        if (_selectionComplete || _isAnnotating) return;
        var pt = e.GetCurrentPoint(RootGrid).Position;

        // ── 智能窗口框选：鼠标自由移动时实时高亮检测到的窗口区域 ──
        if (!_isDragging && _detectedRegions.Count > 0)
        {
            UpdateRegionHover(pt.X, pt.Y);
            // 悬停到区域时更新提示文字
            if (_hoveredRegionIndex >= 0)
            {
                var r = _detectedRegions[_hoveredRegionIndex];
                HintText.Text = $"🪟 {r.Title ?? "窗口"} ({r.Width}×{r.Height})  单击选中  |  拖拽自定义框选  |  Esc 取消";
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                HintText.Text = "单击 = 全屏  |  拖拽 = 框选  |  悬停窗口 = 智能选中  |  Esc = 取消";
                HintText.Visibility = Visibility.Visible;
            }
            return;
        }

        if (!_isDragging) return;

        if (Math.Abs(pt.X - _startPoint.X) > 3 || Math.Abs(pt.Y - _startPoint.Y) > 3) _isClick = false;

        // ── 拖拽中但尚未移动超过阈值：仍高亮区域 ──
        if (_isClick && _detectedRegions.Count > 0)
        {
            UpdateRegionHover(pt.X, pt.Y);
        }

        if (_isClick) return;

        double x1 = Math.Min(_startPoint.X, pt.X), y1 = Math.Min(_startPoint.Y, pt.Y);
        double x2 = Math.Max(_startPoint.X, pt.X), y2 = Math.Max(_startPoint.Y, pt.Y);
        double w = x2 - x1, h = y2 - y1;
        if (w < 2 || h < 2) return;

        // 开始拖拽 → 隐藏区域提示
        ClearRegionHints();
        HintText.Visibility = Visibility.Collapsed;

        DimOverlay.Visibility = Visibility.Collapsed;
        MaskGrid.Visibility = Visibility.Visible;
        MaskLeft.Width = Math.Max(0, x1); MaskTop.Height = Math.Max(0, y1);
        MaskRight.Width = Math.Max(0, RootGrid.ActualWidth - x2);
        MaskBottom.Height = Math.Max(0, RootGrid.ActualHeight - y2);

        DrawSelectionLines(x1, y1, w, h, $"{(int)(w * GetSafeDpiScale())} × {(int)(h * GetSafeDpiScale())}");
        Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TouchIdle();
        if (!_isDragging || _isAnnotating) return;
        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        // ── 单击：优先磁吸到识别区域 ──
        if (_isClick)
        {
            if (_hoveredRegionIndex >= 0 && _hoveredRegionIndex < _detectedRegions.Count)
            {
                SelectDetectedRegion(_detectedRegions[_hoveredRegionIndex]);
                return;
            }
            SelectFullScreen();
            return;
        }

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

    private async void Finish(ActionResult result)
    {
        if (_finished) return; // 防止双重触发
        _finished = true;
        _closed = true; // 通知硬看门狗已响应（防止误杀）

        // 停止超时定时器 + 硬看门狗
        _timeoutTimer?.Stop();
        _timeoutTimer = null;
        _hardWatchdog?.Dispose();
        _hardWatchdog = null;
        _regionRefreshTimer?.Dispose();
        _regionRefreshTimer = null;

        // 标注模式下先退出标注
        if (_isAnnotating) ExitAnnotationMode();

        // 在关闭前将标注合成到像素（后台线程执行，避免大图马赛克/文字合成阻塞 UI）
        if (result is ActionResult.Confirm or ActionResult.Copy)
        {
            if (_annotationManager.Layers.Count > 0)
            {
                // 超时保护：合成卡死（如 Win2D 共享设备异常）时 10s 后跳过合成，
                // 保证 ActionCompleted 必然触发、防重入锁必然释放（用户感知=不卡死）
                var composeTask = Task.Run(GetAnnotatedRegionPixels);
                var done = await Task.WhenAny(composeTask, Task.Delay(10000));
                if (done == composeTask)
                {
                    AnnotatedRegionPixels = await composeTask;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SelectionOverlay] 标注合成超时 (10s)，跳过合成直接关闭");
                    AnnotatedRegionPixels = ExtractRegionPixels(SelectedRect); // 无标注像素兜底
                }
            }
            else
                AnnotatedRegionPixels = ExtractRegionPixels(SelectedRect);
        }

        // 关闭 HDR 背景窗口
        _hdrBgWnd?.Close();
        _hdrBgWnd?.Dispose();
        _hdrBgWnd = null;

        ActionCompleted?.Invoke(result, SelectedRect);
        try { this.Close(); } catch { /* 窗口可能已被系统关闭 */ }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true; // 通知硬看门狗已关闭（防止误杀）

        // 停止超时定时器 + 硬看门狗
        _timeoutTimer?.Stop();
        _timeoutTimer = null;
        _hardWatchdog?.Dispose();
        _hardWatchdog = null;
        _regionRefreshTimer?.Dispose();
        _regionRefreshTimer = null;

        // 关闭 HDR 背景窗口
        _hdrBgWnd?.Close();
        _hdrBgWnd?.Dispose();
        _hdrBgWnd = null;

        // 窗口被系统关闭（Alt+F4 等）时兜底触发 Cancel，保证 MainWindow 防重入锁必然释放
        if (!_finished)
        {
            _finished = true;
            ActionCompleted?.Invoke(ActionResult.Cancel, default);
        }
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

        // 文字工具：点击位置弹输入框，不进入拖拽
        if (_currentAnnoTool == "Text")
        {
            _textInsertPos = CanvasToRegionImage(canvasPt);
            ShowTextInput(canvasPt);
            return;
        }

        _annoDragStart = CanvasToRegionImage(canvasPt);
        _isAnnoDrawing = true;
        _penPoints.Clear();
        _penPoints.Add(_annoDragStart);
        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void OnAnnoCanvasMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAnnoDrawing) return;
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var canvasPt = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);
        if (_currentAnnoTool == "Pen")
        {
            // 画笔：累积轨迹点，绘制实时折线预览
            var imagePt = CanvasToRegionImage(canvasPt);
            var last = _penPoints[^1];
            if ((imagePt - last).Length() > 0.5f) _penPoints.Add(imagePt);
            DrawPenPreview();
            return;
        }
        DrawAnnoPreview(ImageToCanvas(_annoDragStart), canvasPt);
    }

    private void OnAnnoCanvasReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAnnoDrawing) return;
        _isAnnoDrawing = false;
        AnnotationCanvas.ReleasePointerCaptures();
        var pt = e.GetCurrentPoint(AnnotationCanvas).Position;
        var endCanvas = new System.Numerics.Vector2((float)pt.X, (float)pt.Y);

        if (_currentAnnoTool == "Pen")
        {
            if (_penPoints.Count >= 2)
            {
                _penPoints.Add(CanvasToRegionImage(endCanvas));
                _annotationManager.AddLayer(new FreehandLayer { Points = [.. _penPoints] });
                RenderAllAnnoLayers();
            }
            else AnnotationCanvas.Children.Clear();
            _penPoints.Clear();
            return;
        }
        CommitAnnoShape(_annoDragStart, CanvasToRegionImage(endCanvas));
    }

    private void DrawAnnoPreview(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        AnnotationCanvas.Children.Clear();

        float x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
        float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 2 && h < 2) return;

        UIElement? element = _currentAnnoTool switch
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

    /// <summary>箭头元素：主线 + 箭头头部（画布绝对坐标）。</summary>
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
            RenderAllAnnoLayers();
        }
        HideTextInput();
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
                var canvasSz = ImageToCanvas(new System.Numerics.Vector2(bounds.Right, bounds.Bottom))
                    - new System.Numerics.Vector2(canvasPos.X, canvasPos.Y);
                if (element is FrameworkElement fe)
                {
                    fe.Width = Math.Abs(canvasSz.X);
                    fe.Height = Math.Abs(canvasSz.Y);
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
            AnnotationRasterizer.RenderLayer(result, imgW, imgH, layer);
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

    // ═══════════════════════════════════════
    //  QQ截图式窗口智能识别
    // ═══════════════════════════════════════

    // 单一高亮边框（QQ截图风格：只显示当前悬停窗口的边框）
    private Border? _activeHighlight;
    private Border? _windowTooltip;
    private TextBlock? _tooltipText;
    private System.Threading.Timer? _regionRefreshTimer;

    /// <summary>后台检测窗口区域（排除覆盖层自身）+ 每 3 秒定时刷新（QQ截图式实时感知窗口变化）。</summary>
    private async void DetectAndRenderRegions()
    {
        try
        {
            var selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // 排除覆盖层自身 + 下层 HDR 背景窗口（避免把背景窗口识别为"窗口"）
            var exclude = new HashSet<nint> { selfHwnd };
            if (_hdrBgWnd?.Hwnd is nint bgHwnd && bgHwnd != 0) exclude.Add(bgHwnd);
            _detectedRegions = await Task.Run(() =>
                RegionDetector.DetectAll(DesktopPixels, _vx, _vy, _vw, _vh, exclude));

            DispatcherQueue.TryEnqueue(() =>
            {
                int uia = _detectedRegions.Count(r => r.Source == RegionSource.Uia);
                int edge = _detectedRegions.Count(r => r.Source == RegionSource.Edge);
                System.Diagnostics.Debug.WriteLine(
                    $"[SelectionOverlay] 窗口检测完成: {_detectedRegions.Count} 个 (UIA={uia}, Edge={edge})");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelectionOverlay] 区域检测失败: {ex.Message}");
        }

        // 定时刷新（每 3 秒, 未选中时; 响应新开/关闭/移动窗口）
        _regionRefreshTimer?.Dispose();
        _regionRefreshTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var selfHwnd2 = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var exclude2 = new HashSet<nint> { selfHwnd2 };
                if (_hdrBgWnd?.Hwnd is nint bgHwnd2 && bgHwnd2 != 0) exclude2.Add(bgHwnd2);
                var regions = RegionDetector.DetectAll(DesktopPixels, _vx, _vy, _vw, _vh, exclude2);
                if (!_selectionComplete && !_isDragging)
                {
                    _detectedRegions = regions;
                    if (_hoveredRegionIndex >= regions.Count) _hoveredRegionIndex = -1;
                }
            }
            catch { }
        }, null, 3000, 3000);
    }

    /// <summary>QQ截图式：仅高亮当前悬停的窗口（最小面积优先），显示标题提示。</summary>
    private void UpdateRegionHover(double mouseX, double mouseY)
    {
        double scale = GetSafeDpiScale();
        if (scale <= 0.01) scale = 1.0;

        // 将鼠标坐标转换为屏幕坐标
        int screenX = _vx + (int)(mouseX * scale);
        int screenY = _vy + (int)(mouseY * scale);

        // 使用最小面积优先命中测试（选中最内层窗口）
        int newHover = RegionDetector.FindSmallestRegionAt(_detectedRegions, screenX, screenY);

        if (newHover == _hoveredRegionIndex) return;
        _hoveredRegionIndex = newHover;

        // 清除旧高亮
        if (_activeHighlight is not null)
        {
            RegionHintCanvas.Children.Remove(_activeHighlight);
            _activeHighlight = null;
        }
        if (_windowTooltip is not null)
        {
            RegionHintCanvas.Children.Remove(_windowTooltip);
            _windowTooltip = null;
            _tooltipText = null;
        }

        if (newHover < 0)
        {
            HintText.Text = "单击 = 全屏  |  拖拽 = 框选  |  悬停窗口 = 智能选中  |  Esc = 取消";
            return;
        }

        var region = _detectedRegions[newHover];
        double rx = (region.X - _vx) / scale;
        double ry = (region.Y - _vy) / scale;
        double rw = region.Width / scale;
        double rh = region.Height / scale;

        // 绘制高亮边框（QQ截图风格：蓝色粗边框 + 半透明填充）
        _activeHighlight = new Border
        {
            Width = rw, Height = rh,
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(230, 50, 130, 246)),
            BorderThickness = new Thickness(2.5),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 50, 130, 246)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_activeHighlight, rx);
        Canvas.SetTop(_activeHighlight, ry);
        RegionHintCanvas.Children.Add(_activeHighlight);

        // 窗口标题 + 尺寸提示（显示在窗口左上角）
        string label = $"{region.Title ?? "窗口"}  ({region.Width}×{region.Height})";
        _tooltipText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(6, 2, 6, 2),
        };
        _windowTooltip = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(220, 30, 30, 30)),
            CornerRadius = new CornerRadius(3),
            Child = _tooltipText,
            IsHitTestVisible = false,
        };
        // 提示框定位：窗口左上角上方
        double tipY = ry - 22;
        if (tipY < 0) tipY = ry + 2; // 放不下就放窗口内部顶部
        Canvas.SetLeft(_windowTooltip, rx);
        Canvas.SetTop(_windowTooltip, tipY);
        RegionHintCanvas.Children.Add(_windowTooltip);

        // 更新底部提示
        HintText.Text = $"🪟 {region.Title}  |  单击选中  |  拖拽自定义  |  Esc 取消";
    }

    /// <summary>清除所有区域提示。</summary>
    private void ClearRegionHints()
    {
        _hoveredRegionIndex = -1;
        if (_activeHighlight is not null)
        {
            RegionHintCanvas.Children.Remove(_activeHighlight);
            _activeHighlight = null;
        }
        if (_windowTooltip is not null)
        {
            RegionHintCanvas.Children.Remove(_windowTooltip);
            _windowTooltip = null;
            _tooltipText = null;
        }
    }

    /// <summary>选中一个自动识别的区域（QQ截图式单击选中）。</summary>
    private void SelectDetectedRegion(DetectedRegion region)
    {
        double scale = GetSafeDpiScale();
        if (scale <= 0.01) scale = 1.0;

        double rx = (region.X - _vx) / scale;
        double ry = (region.Y - _vy) / scale;
        double rw = region.Width / scale;
        double rh = region.Height / scale;

        ClearRegionHints();
        ApplyCustomSelection(rx, ry, rx + rw, ry + rh);
    }
}

