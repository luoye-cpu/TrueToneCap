// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/OcrPreviewWindow.xaml.cs
// OCR / 翻译独立预览窗口：快速渲染截图 PNG，识别/翻译文字按原图坐标点对点覆盖在图上。
// 原文模式 = 半透明高亮命中块（透出原字）；译文模式 = 实色块 + 译文覆盖（点对点替换）。

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinRT.Interop;
using TrueToneCap.Core.Services;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Logging;   // LogCategory

namespace TrueToneCap.App;

public sealed partial class OcrPreviewWindow : Window
{
    private readonly byte[] _rawPixels;
    private readonly int _imgW, _imgH;
    private readonly OcrResult _ocr;            // Lines/Words 坐标已归一化到原图
    private readonly LlmConfig _translationConfig;
    private Func<byte[], int, int, Task>? _saveHandler; // 主窗口注入的保存逻辑（按主UI设置）
    private bool _translating;
    private bool _savingTranslated;                     // 译文图合成/保存中（防连点重复出文件）

    /// <summary>
    /// 翻译结果快照 —— <b>一次性发布</b>，替代原先分两处写的
    /// <c>_translatedLines</c> + <c>_showTranslation</c>（2026-10-07 审查加固）。
    /// <para>为什么合成一个对象：原先是"先写 <c>_translatedLines</c>、再在另一个
    /// <c>TryEnqueue</c> 里写 <c>_showTranslation = true</c>"。当前调用顺序恰好正确
    /// （先 <c>await Task.WhenAll</c>，再整体入队），但渲染端只要有人在
    /// <c>WhenAll</c> 之前读一次，就会拿到"有译文但还没显示"或"显示了但译文是旧的"这种半成品。
    /// 合成一个不可变快照后，"译文与显示标志永远同时可见"由类型本身保证，
    /// 不再依赖调用方的书写顺序。</para>
    /// </summary>
    private sealed record TranslationSnapshot(
        IReadOnlyList<string> Lines,
        int? DefiniteFailed,
        int FallbackCount,
        string? FirstError);

    private TranslationSnapshot? _translation;

    /// <summary>
    /// <b>显示哪一边</b>（true = 显示译文，false = 显示原文）。
    /// <para>⚠️ 与"有没有译文"是两件事：前者是用户的视图选择，后者由 <see cref="_translation"/>
    /// 是否存在决定。原先只有这一个字段，"有没有译文"要用 <c>_translatedLines != null</c> 现推，
    /// 于是两个概念在 20 处调用点里被混着用 —— 这正是加固时拆开的原因。</para>
    /// </summary>
    private bool _showTranslation;

    /// <summary>是否已有可显示的翻译结果（等价于原先靠 <c>_translatedLines != null</c> 现推的判据）。</summary>
    private bool HasTranslation => _translation is not null;

    /// <summary>
    /// 本窗口在途工作的取消源（2026-10-07 审查修复）。
    /// <para>修的是什么：<c>TranslateAsync</c> 的签名<b>接受</b> <c>CancellationToken</c>，
    /// 但本窗口从来没传过，窗口内也没有任何 CTS，<c>OnClosed</c> 还是空的 ⇒
    /// 关掉预览窗后，最多 N 行翻译请求会继续跑到各自的内部超时
    /// （Google/Bing 每端点 5 s、LLM 视配置），并一直占着 <c>SemaphoreSlim(4)</c> 的槽位。</para>
    /// <para>影响等级（实测确认，不夸大）：<b>不崩、不损坏数据</b> —— 最终 UI 更新走
    /// <c>DispatcherQueue.TryEnqueue</c>，窗口已关时安全 no-op。纯粹是浪费网络与并发槽位。
    /// 但"用户已经关窗了、程序还在替他发请求"，既费流量也让日志里出现无来源的失败记录。</para>
    /// </summary>
    private CancellationTokenSource? _workCts;

    /// <summary>主窗口注入的保存逻辑（按主UI设置）</summary>
    public Func<byte[], int, int, Task>? SaveHandler
    {
        get => _saveHandler;
        set => _saveHandler = value;
    }

    /// <summary>
    /// 这一拍截图的日志关联 ID（由 MainWindow 建窗时传进来）。
    /// <para>本窗口的复制/翻译/保存都是**独立的 UI 事件根**，热键那次 <c>BeginOp</c> 的
    /// <c>AsyncLocal</c> 不会流到这儿 ⇒ 每个入口 <c>ContinueOp(CaptureOpId)</c> 续上，
    /// 报障时"同一次截图 → OCR → 翻译"才挂在同一个 <c>#opId</c> 下。null 时退化成开一个新 ID，
    /// 宁可断开也不把两拍混成一条时间线（见 <c>CoreLog.ContinueOp</c>）。</para>
    /// </summary>
    public string? CaptureOpId { get; set; }

    public OcrPreviewWindow(byte[] bgraPixels, int width, int height, OcrResult ocr,
        LlmConfig translationConfig, bool autoTranslate = false,
        Func<byte[], int, int, Task>? saveHandler = null)
    {
        this.InitializeComponent();
        // 崩溃根因（2026-10-21 探针实测，台账 §70）：`IsChecked="True"` 写在 XAML 标记里会在加载期抛
        // `XamlParseException / 0x802B000A`，内层原文是
        // "Failed to assign to property 'Microsoft.UI.Xaml.Controls.Primitives.ToggleButton.IsChecked'"。
        // 同一个值改成 InitializeComponent 之后在代码里赋 ⇒ 加载过得去（默认档仍是"显示覆盖层"）。
        // 原先这个异常从 DispatcherQueue 回调里抛出且没人接，才升级成原生 fail-fast（进程直接消失）。
        // 回归由门禁 [1.97/2] 的 --window-probe 与 Test_OcrPreviewWindow_ConstructionGuarded 两处理住。
        OverlayToggle.IsChecked = true;
        MainWindow.HookLocaleSweep(this.Content as Microsoft.UI.Xaml.FrameworkElement);
        // 窗口标题不在可视树里，只能显式翻译。
        Title = LocaleManager.Tr(Title);
        _rawPixels = bgraPixels;
        _imgW = width; _imgH = height;
        _ocr = ocr;
        _translationConfig = translationConfig;
        _saveHandler = saveHandler;

        // ── 字体注入（使用用户选择的字体） ──
        var fontFamily = FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily);
        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, fontFamily);

        // 智能窗口尺寸（参考 AnnotationWindow）
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            int maxW = area.WorkArea.Width - 80, maxH = area.WorkArea.Height - 120;
            double scale = Math.Min(1.0, Math.Min((double)maxW / width, (double)maxH / height));
            int winW = Math.Max(480, (int)(width * scale) + 80);
            int winH = Math.Max(320, (int)(height * scale) + 120);
            appWindow.MoveAndResize(new RectInt32(
                area.WorkArea.X + (area.WorkArea.Width - winW) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - winH) / 2, winW, winH));
        }
        catch { }

        RenderImage();
        // 仅在渲染未出错时更新状态文字
        if (InfoTxt.Text is not null && !InfoTxt.Text.StartsWith("⚠"))
            UpdateInfo();

        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { this.Close(); e.Handled = true; }
        };

        // 翻译入口：窗口打开后自动触发翻译并切到译文覆盖模式
        if (autoTranslate)
            _ = TranslateAndShowAsync();
    }

    // ═══════════════════════════════════════
    //  渲染截图（同步 WriteableBitmap，避免 SoftwareBitmapSource 异步初始化问题）
    // ═══════════════════════════════════════

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
            PreviewImage.Stretch = Stretch.Uniform;
            PreviewImage.MaxWidth = _imgW;
            PreviewImage.MaxHeight = _imgH;

            // 覆盖层尺寸与图片实际显示尺寸同步，保证坐标点对点
            // 使用防抖避免拖拽时高频 SizeChanged 导致原生层重入崩溃
            PreviewImage.SizeChanged += (_, _) => DebouncedSyncOverlay();
            // 首帧布局完成后渲染一次覆盖层
            PreviewImage.Loaded += (_, _) => SyncOverlayAndRender();
        }
        catch (Exception ex)
        {
            InfoTxt.Text = $"⚠ 渲染失败: {ex.Message}";
        }
    }

    // ── 节流：拖拽/缩放时 SizeChanged 每帧触发，而覆盖层要按行重建 Border+TextBlock+Brush。
    // 原实现只合并同一批事件（一个 enqueue），拖拽中仍是每帧全量重建。这里限制到每 100ms
    // 最多一次，并补一次尾随刷新保证最终对齐。
    private long _lastSyncTick;
    private int _syncTrailingScheduled;
    private void DebouncedSyncOverlay()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSyncTick) < 100)
        {
            if (Interlocked.CompareExchange(ref _syncTrailingScheduled, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    Interlocked.Exchange(ref _syncTrailingScheduled, 0);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        Interlocked.Exchange(ref _lastSyncTick, Environment.TickCount64);
                        TrySyncOverlay();
                    });
                });
            }
            return;
        }

        Interlocked.Exchange(ref _lastSyncTick, now);
        DispatcherQueue.TryEnqueue(TrySyncOverlay);
    }

    private void TrySyncOverlay()
    {
        try { SyncOverlayAndRender(); }
        catch (Exception ex)
        {
            LogService.Warn("OcrPreview", $"SyncOverlay 异常: {ex.Message}", LogCategory.UI);
        }
    }

    private void SyncOverlayAndRender()
    {
        if (PreviewImage.ActualWidth > 0 && PreviewImage.ActualHeight > 0)
        {
            OverlayCanvas.Width = PreviewImage.ActualWidth;
            OverlayCanvas.Height = PreviewImage.ActualHeight;
            RenderOverlay();
        }
    }

    // ═══════════════════════════════════════
    //  覆盖层：按 OCR 坐标点对点绘制文字覆盖
    // ═══════════════════════════════════════

    /// <summary>原图像素坐标 → 覆盖层(Canvas)显示坐标的缩放比。</summary>
    private double ScaleX => _imgW > 0 ? OverlayCanvas.Width / _imgW : 1.0;
    private double ScaleY => _imgH > 0 ? OverlayCanvas.Height / _imgH : 1.0;

    // ═══ 2026-08-25: 用户选择的字体（注入到文字覆盖层） ═══
    private string _fontFamily = "";
    private string EffectiveFontFamily => string.IsNullOrWhiteSpace(_fontFamily)
        ? FontLoader.DefaultFontFamily : _fontFamily;

    // ═══ 2026-11-11 性能审计 P2：行几何缓存 ═══
    // 每行的覆盖矩形 / 字号锚 / 原文垫底色只依赖 (_rawPixels, det 框, 原文文本) —— 与
    // 显示缩放和"原文/译文"模式无关，单次 OCR 结果内完全静态。此前 resize（节流后仍
    // ~10Hz）、模式切换、翻译完成的每次 RenderOverlay 都对每行重跑一次全分辨率墨迹
    // 扫描 + 中位色采样（4K 30 行 ≈ 2-6M 像素/次，UI 线程 10-40ms）。缓存后这些路径
    // 只剩 XAML 量测。缓存里的 LineGeometry 同时供导出（Compose）复用，一次扫描两处受益。
    private sealed record PreviewLineGeom(OcrTextOverlay.LineGeometry Geom, Windows.UI.Color BgOriginal);
    private List<PreviewLineGeom?>? _lineGeom;

    /// <summary>惰性构建行几何缓存（首次 RenderOverlay 一次付清；此后 resize/切换零像素扫描）。</summary>
    private List<PreviewLineGeom?> BuildLineGeometry()
    {
        var list = new List<PreviewLineGeom?>(_ocr.Lines.Count);
        foreach (var line in _ocr.Lines)
        {
            var rect0 = LineRect(line);
            if (rect0.Width <= 0 || rect0.Height <= 0) { list.Add(null); continue; }
            // 字号锚与覆盖矩形：与 OcrTextOverlay.ComputeCover（Compose 的缓存退路）逐字同一套
            // 函数与参数 —— 预览是事实源，导出未命中缓存时走同款计算，两处永不漂移。
            float referencePx = OcrTextOverlay.EstimateOriginalFontPx(line.Text, rect0.Width, rect0.Height);
            var est = OcrTextOverlay.ExpandRectToInk(line.Text, rect0, _imgH);
            var ink = OcrTextOverlay.MeasureInkRect(_rawPixels, _imgW, _imgH,
                (est.X - 10, est.Y, est.Width + 20, est.Height));
            ink = (Math.Max(0, ink.X - 4), Math.Max(0, ink.Y - 4), ink.Width + 8, ink.Height + 8);
            var rect = OcrTextOverlay.UnionRect(est, ink);
            if (rect.Y + rect.Height > _imgH) rect.Height = _imgH - rect.Y;
            // 原文垫底色：块内中位色按整窗暗化折算（darkOverlay 是 α90 黑 ⇒ 底色 ×165/255）
            var bgRaw = OcrTextOverlay.MedianBlockColor(_rawPixels, _imgW, _imgH,
                rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
            var bg = Windows.UI.Color.FromArgb(255,
                (byte)(bgRaw.R * 165 / 255), (byte)(bgRaw.G * 165 / 255), (byte)(bgRaw.B * 165 / 255));
            list.Add(new PreviewLineGeom(
                new OcrTextOverlay.LineGeometry(est, ink, rect, referencePx), bg));
        }
        return list;
    }

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (_ocr.Lines is null || _ocr.Lines.Count == 0 || OverlayCanvas.Width <= 0) return;

        double canvasW = OverlayCanvas.Width, canvasH = OverlayCanvas.Height;
        double sx = ScaleX, sy = ScaleY;

        // 获取用户字体
        _fontFamily = AppServices.Settings.Current.FontFamily;

        // ═══ 第一层：整体标暗（轻度暗化，保留原图可见性） ═══
        var darkOverlay = new Border
        {
            Width = canvasW, Height = canvasH,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(darkOverlay, 0);
        Canvas.SetTop(darkOverlay, 0);
        OverlayCanvas.Children.Add(darkOverlay);

        // ═══ 第二层：逐行文字覆盖 ═══
        var lineGeoms = _lineGeom ??= BuildLineGeometry();
        for (int i = 0; i < _ocr.Lines.Count; i++)
        {
            var line = _ocr.Lines[i];
            var geom = lineGeoms[i];
            if (geom is null) continue;

            // 几何与锚、原文垫底色全部来自缓存（BuildLineGeometry，一次付清）
            var rect = geom.Geom.Cover;
            float referencePx = geom.Geom.ReferencePx;

            double cw = rect.Width * sx, ch = rect.Height * sy;
            // 快照式读取：译文与"是否显示译文"来自同一个不可变对象，不会再出现
            // "标志已置位但译文还是旧的"这类半成品（见 TranslationSnapshot 的说明）。
            var snap = _translation;
            bool showTranslation = snap is not null;
            bool hasTranslation = snap is not null && i < snap.Lines.Count;
            string displayText = showTranslation && hasTranslation
                ? snap!.Lines[i]
                : line.Text;
            // 原文模式下若译文已就绪，ToolTip 显示译文
            string tooltipText = hasTranslation
                ? $"原文: {line.Text}\n译文: {snap!.Lines[i]}"
                : line.Text;

            // 高亮背景块：原文模式 = 采样局部底色的不透明垫底（盖掉原字防重影，色随底图融入）。
            // 2026-11-11 实机第二轮：α80 半透明让原字透出，与覆盖字叠成错位双影；
            // 实机第三轮：α245 仍有 3.9% 透出 —— 平坦底上 6 luma 的残影肉眼可辨（(510,145)=154
            // 实测 = 0.961×160 的理论 bleed 值）⇒ 全不透明。垫底色在 BuildLineGeometry 采样
            // （×165/255 暗化折算，darkOverlay 是 α90 黑），此处只按垫底亮度取对比字色。
            byte fg;
            Windows.UI.Color bgColor;
            if (showTranslation)
            {
                bgColor = Windows.UI.Color.FromArgb(235, 15, 23, 42);    // 深色不透明
                fg = 240;
            }
            else
            {
                bgColor = geom.BgOriginal;
                int bgLuma = (bgColor.R * 299 + bgColor.G * 587 + bgColor.B * 114) / 1000;
                fg = bgLuma >= 136 ? (byte)20 : (byte)240;
            }
            var borderColor = showTranslation
                ? Windows.UI.Color.FromArgb(120, 56, 189, 248)  // 蓝边框
                : Windows.UI.Color.FromArgb(100, 100, 180, 255); // 浅蓝边框

            // ═══ 文字覆盖层 — 使用用户选择的字体，与OCR区域对齐 ═══
            //
            // 2026-10-29：译文普遍比原文长，原先 NoWrap + 省略号会把后半句吃掉。
            // 改成"按框宽折行 + 自适应字号"，且字号由 OcrTextOverlay.FitFontSize 在【图像像素空间】
            // 算好再乘回显示缩放 —— 导出的覆盖图调的是同一个函数，预览与产物不会两套排版。
            // ═══ 2026-11-11（实机排障）：字号锚 = EstimateOriginalFontPx 从【原文】反推 ═══
            // 旧口径 0.65×框高让覆盖字恒为原字的 4~6 成（det 框高缩水实锚，见 OcrTextOverlay）；
            // 新口径覆盖字 ≥ 原字（用户口径"至少应该不比原有的字体小"）。
            float fitPx = OcrTextOverlay.FitFontSize(displayText, EffectiveFontFamily,
                Math.Max(8, rect.Width - 8), Math.Max(8, rect.Height - 8), referencePx);

            double marginS = Math.Max(1, 4 * sx);
            var textBlock = new TextBlock
            {
                Text = displayText,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, fg, fg, fg)),
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                // 原文模式禁折行：显示文本就是块里的原字，锚定字号下本就单行放下；
                // 折行会让 ±1px 的排版差把词拆成"OC/R"堆叠（实机第二轮抓到）。
                // 译文模式保留折行：译文普遍更长，折行是设计行为。
                TextWrapping = showTranslation ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(marginS, 0, marginS, 0),
                // 只防 0/负值：这里若给个"最小 6 DIP"，小图缩放后预览的字就会比导出的字大一圈，
                // 正好把"预览所见 = 产物"这条纪律破坏掉（导出按图像像素画，比例必须一致）
                FontSize = Math.Max(1.0, fitPx * sy),
                // ═══ 2026-08-25: 使用用户选择的字体覆盖 ═══
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(EffectiveFontFamily),
            };

            // ═══ 2026-11-11（实机第三轮）：块宽高 = max(墨迹覆盖, 文字实测) —— 不再拍估计值 ═══
            // 用户的真实字体（如中文字体渲染拉丁文）字幅比 0.55em 估计值宽，锚定字号下末字符
            // 被块边裁掉（"OCl"/"CHEC!"，实机截图）。XAML Measure 给的是真实字体度量 ——
            // 块取两者较大值，中心保持对准墨迹块中心，再收拢进画布。
            textBlock.Measure(showTranslation
                ? new Windows.Foundation.Size(Math.Max(1, cw - 2 * marginS), double.PositiveInfinity)
                : new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = Math.Max(cw, textBlock.DesiredSize.Width + 2);   // +2：Border 左右边框各 1
            double bh = Math.Max(ch, textBlock.DesiredSize.Height + 2);
            double left = Math.Max(0, Math.Min(rect.X * sx + cw / 2 - bw / 2, Math.Max(0, canvasW - bw)));
            double top = Math.Max(0, Math.Min(rect.Y * sy + ch / 2 - bh / 2, Math.Max(0, canvasH - bh)));

            var overlay = new Border
            {
                Width = Math.Max(1, bw), Height = Math.Max(1, bh),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                // ═══ 2026-08-25: 文字块可点击复制 + 悬停高亮 ═══
            };
            Canvas.SetLeft(overlay, left);
            Canvas.SetTop(overlay, top);
            overlay.Child = textBlock;

            // 悬停效果：边框增亮（块底已是不透明垫底色，保持不动，只亮边框）
            overlay.PointerEntered += (_, _) =>
            {
                overlay.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248));
            };
            overlay.PointerExited += (_, _) =>
            {
                overlay.BorderBrush = new SolidColorBrush(borderColor);
            };

            ToolTipService.SetToolTip(overlay, tooltipText);
            int idx = i;
            overlay.Tapped += (_, _) => CopyLine(idx);
            OverlayCanvas.Children.Add(overlay);
        }
    }

    // ═══ 2026-08-25: 覆盖层显示/关闭开关 ═══
    private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
    {
        bool show = OverlayToggle.IsChecked == true;
        OverlayCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // 两态都有英文说法：原先右侧写的是中文"显示覆盖"，于是英文模式下"关着覆盖层"
        // 这一态反而显示中文（复核轮抓到，交接 §55.6 剩余缺口第一条）。
        OverlayToggleTxt.Text = show ? LocaleManager.Tr("隐藏覆盖") : "Show overlay";
    }

    /// <summary>取行的原图边界矩形，带 4px padding。</summary>
    private static (int X, int Y, int Width, int Height) LineRect(OcrLine line)
    {
        const int pad = 4; // 4px padding，确保文字被完全覆盖

        // 策略1: 从 Words 合并边界框 + padding
        if (line.Words is { Count: > 0 })
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (var w in line.Words)
            {
                x1 = Math.Min(x1, w.X); y1 = Math.Min(y1, w.Y);
                x2 = Math.Max(x2, w.X + w.Width); y2 = Math.Max(y2, w.Y + w.Height);
            }
            if (x2 > x1 && y2 > y1)
                return (Math.Max(0, x1 - pad), Math.Max(0, y1 - pad),
                        x2 - x1 + pad * 2, y2 - y1 + pad * 2);
        }
        // 策略2: 使用 OcrLine 自身的坐标 + padding
        if (line.Width > 0 && line.Height > 0)
            return (Math.Max(0, line.X - pad), Math.Max(0, line.Y - pad),
                    line.Width + pad * 2, line.Height + pad * 2);
        // 策略3: 整图 1/4 居中区域（确保覆盖层始终可见，不会完全透明）
        return (0, 0, 1, 1);
    }

    // ═══════════════════════════════════════
    //  交互：复制 / 切换 / 翻译 / 保存
    // ═══════════════════════════════════════

    private void CopyLine(int idx)
    {
        var snap = _translation;
        string text = (snap is not null && idx < snap.Lines.Count)
            ? snap.Lines[idx]
            : _ocr.Lines[idx].Text;
        SetClipboard(text);
        InfoTxt.Text = $"📋 已复制: {Truncate(text, 40)}";
    }

    private async void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        using var _op = CoreLog.ContinueOp(CaptureOpId);
        string text = CurrentFullText();
        if (string.IsNullOrWhiteSpace(text)) { InfoTxt.Text = "⚠ 无文字可复制"; return; }
        await ShowTextDialogAsync(text);
    }

    private string CurrentFullText()
    {
        var snap = _translation;
        if (snap is not null)
            return string.Join("\n", snap.Lines);
        return _ocr.Text;
    }

    /// <summary>弹出可选中部分文字并复制的对话框。</summary>
    private async Task ShowTextDialogAsync(string text)
    {
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            MaxHeight = 500,
            FontSize = 14,
        };

        var dialog = new ContentDialog
        {
            Title = HasTranslation ? "📝 译文内容" : "📝 原文内容",
            Content = new ScrollViewer { Content = textBox, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "复制全部",
            SecondaryButtonText = "复制选中",
            CloseButtonText = "关闭",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            SetClipboard(text);
            InfoTxt.Text = $"📋 已复制全部（{text.Length} 字）";
        }
        else if (result == ContentDialogResult.Secondary)
        {
            if (!string.IsNullOrEmpty(textBox.SelectedText))
            {
                SetClipboard(textBox.SelectedText);
                InfoTxt.Text = $"📋 已复制选中文字（{textBox.SelectedText.Length} 字）";
            }
            else
            {
                SetClipboard(text);
                InfoTxt.Text = $"📋 未选中文字，已复制全部（{text.Length} 字）";
            }
        }
    }

    // ═══ 离线翻译的未就绪引导（2026-11-11 离线翻译包方案）═══

    /// <summary>离线未就绪时用户的走向。</summary>
    private enum OfflineFallbackAction { Abort, ProceedOnline, ProceedOffline }

    /// <summary>
    /// 离线翻译前置检查未通过时的引导弹窗：
    /// <list type="bullet">
    /// <item>缺语言包 → [开始下载]（进度就地显示，成功后直接继续离线翻译）/ [改用在线翻译] / [取消]</item>
    /// <item>缺运行环境 → [改用在线翻译] / [取消]（装运行环境去主窗口设置里做，弹窗里不引导长任务）</item>
    /// </list>
    /// <para>弹窗构造期可能还没挂上 XamlRoot（自动翻译在构造函数里就开始跑）——
    /// ShowAsync 抛异常时退回纯状态栏提示，绝不因为引导失败把窗口搞崩。</para>
    /// </summary>
    private async Task<OfflineFallbackAction> HandleOfflineNotReadyAsync(
        OfflineArgosEngine.ReadinessResult ready, CancellationToken ct)
    {
        LogService.Warn("OcrPreview",
            $"离线翻译未就绪：{ready.State}（{ready.From}→{ready.To}，Python={ready.PythonFound} argos={ready.ArgosAvailable}）",
            LogCategory.Network);

        string title, detail;
        if (ready.State == OfflineArgosEngine.ReadinessState.ModelMissing)
        {
            title = LocaleManager.Tr("未找到离线翻译模型");
            // 长句拆两条（词表键值不允许反斜杠转义），代码里拼 —— 与词表判据对齐
            detail = string.Format(LocaleManager.Tr(
                        "缺少语言包 {0}。下载后即可完全离线翻译，文本不会离开本机。"),
                    ready.PackageIdentifier ?? $"{ready.From}_{ready.To}")
                + "\n\n" + LocaleManager.Tr("首次使用还需 Python 运行环境；下载期间请勿关闭本窗口。");
        }
        else
        {
            title = LocaleManager.Tr("离线翻译运行环境未就绪");
            detail = ready.PythonFound
                ? LocaleManager.Tr("已检测到 Python，但缺 argostranslate 包。可到主窗口 设置 → OCR/翻译 的「离线翻译包」卡片点「安装运行环境」，或本次改用在线翻译。")
                : LocaleManager.Tr("未检测到 Python。离线翻译需要 Python 3.10+ 与 argostranslate；可到主窗口设置里安装，或本次改用在线翻译。");
        }

        var progressBar = new ProgressBar { Maximum = 1.0, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        stack.Children.Add(progressBar);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = stack,
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
        };
        bool canDownloadHere = ready.State == OfflineArgosEngine.ReadinessState.ModelMissing;
        if (canDownloadHere)
        {
            dialog.PrimaryButtonText = LocaleManager.Tr("开始下载");
            dialog.SecondaryButtonText = LocaleManager.Tr("改用在线翻译");
            dialog.CloseButtonText = LocaleManager.Tr("取消");
        }
        else
        {
            dialog.PrimaryButtonText = LocaleManager.Tr("改用在线翻译");
            dialog.CloseButtonText = LocaleManager.Tr("取消");
        }

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            LogService.Warn("OcrPreview", $"离线就绪弹窗无法显示（{ex.Message}），退回状态栏提示");
            InfoTxt.Text = "⚠ " + detail;
            ResetTranslateUi();
            return OfflineFallbackAction.Abort;
        }

        // "改用在线翻译"按钮：ModelMissing 时在 Secondary，RuntimeMissing 时在 Primary ——
        // 统一按 canDownloadHere 翻译结果，不再复制一遍按钮语义。
        if (result == (canDownloadHere ? ContentDialogResult.Secondary : ContentDialogResult.Primary))
        {
            SwitchToOnlineModeQuiet();
            InfoTxt.Text = "🌐 翻译中...";
            return OfflineFallbackAction.ProceedOnline;
        }
        if (canDownloadHere && result == ContentDialogResult.Primary)
        {
            // ═══ 就地下载：进度条与状态栏同时更新；成功后直接继续离线翻译 ═══
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            InfoTxt.Text = "⬇ 正在下载语言包...";
            var manager = new TranslationPackageManager();
            var download = await manager.DownloadPackageAsync(
                ready.PackageIdentifier ?? $"{ready.From}_{ready.To}",
                (done, total) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (total > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = Math.Min(1.0, (double)done / total);
                        InfoTxt.Text = $"⬇ {done / 1048576.0:F0}MB / {total / 1048576.0:F0}MB（{(double)done / total:P0}）";
                    }
                    else
                    {
                        InfoTxt.Text = $"⬇ {done / 1048576.0:F0} MB";
                    }
                }),
                ct);
            dialog.Hide();
            if (download.Success)
            {
                InfoTxt.Text = "🌐 翻译中...";
                return OfflineFallbackAction.ProceedOffline;
            }
            LogService.Warn("OcrPreview", $"离线语言包下载失败：{download.Message}", LogCategory.Network);
            InfoTxt.Text = "❌ " + download.Message;
            ResetTranslateUi();
            return OfflineFallbackAction.Abort;
        }
        ResetTranslateUi();
        return OfflineFallbackAction.Abort;
    }

    /// <summary>本次翻译放弃后把 UI 复位（按钮可再点、翻译态清掉）。</summary>
    private void ResetTranslateUi()
    {
        _translating = false;
        TranslateBtn.IsEnabled = true;
    }

    /// <summary>用户在引导里选了"改用在线翻译"：本次窗口内立即切在线，并把模式写回设置。
    /// <para>写回 "Free" 不动用户的 LLM/Vision 凭据 —— 那些字段在设置里独立存放，
    /// 模式只是"当前选了哪条路"的指针（SaveSettings 的形状也是这样）。</para></summary>
    private void SwitchToOnlineModeQuiet()
    {
        _translationConfig.UseOfflineArgos = false;
        try
        {
            var s = AppServices.Settings.Current;
            s.TranslationMode = "Free";
            s.UseCustomLlm = false;
            AppServices.Settings.SaveQuiet();
            LogService.Info("OcrPreview", "离线未就绪 → 本次及后续改用在线免费翻译（已写回设置）");
        }
        catch (Exception ex)
        {
            LogService.Warn("OcrPreview", "在线回退的设置落盘失败: " + ex.Message);
        }
    }

    private void OnModeToggleChanged(object sender, RoutedEventArgs e)
    {
        bool want = ModeToggle.IsChecked == true;
        // "有没有译文"看快照，"显示哪一边"看这个开关 —— 两者刻意分开：
        // 快照一旦发布就不再变（不可变），而用户可以在原文/译文之间来回切。
        if (want && !HasTranslation)
        {
            // 译文尚未就绪 → 触发翻译，完成后自动切到译文模式
            ModeToggle.IsChecked = false;
            _ = TranslateAndShowAsync();
            return;
        }
        _showTranslation = want;
        ModeToggle.Content = want ? "显示原文" : "显示译文";
        RenderOverlay();
        UpdateInfo();
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e) => await TranslateAndShowAsync();

    /// <summary>
    /// 把当前截图编成 8-bit PNG 到内存，供视觉路一次性上传（不落盘、不留临时文件）。
    /// <para>固定 8-bit：视觉模型读不出位深差异，而 16-bit PNG 会让 base64 体积翻倍
    /// 去撞端点的请求体上限。</para>
    /// </summary>
    private byte[] EncodePngForVision()
    {
        using var ms = new MemoryStream();
        TrueToneCap.Core.Encoding.ManagedPngEncoder.Encode(_rawPixels, _imgW, _imgH, ms, bitDepth: 8);
        return ms.ToArray();
    }

    private async Task TranslateAndShowAsync()
    {
        // 翻译是另一个 UI 根（自动翻译走 Loaded，点按钮走 Click）⇒ 两种入口都在这条链上，续一次就够
        using var _op = CoreLog.ContinueOp(CaptureOpId);
        if (_translating) return;
        if (_ocr.Lines is null || _ocr.Lines.Count == 0)
        {
            DispatcherQueue.TryEnqueue(() => InfoTxt.Text = "⚠ 无文字可翻译");
            return;
        }

        _translating = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            TranslateBtn.IsEnabled = false;
            InfoTxt.Text = "🌐 翻译中...";
        });

        // ═══ 本次翻译的取消源（2026-10-07 审查修复）═══
        // 关窗（OnClosed）会 Cancel 它 ⇒ 在途 HTTP 请求立即结束，
        // 不再"用户已经关窗了、程序还在替他发请求"。
        // 每次翻译新建一个：上次已完成的 CTS 不该复用（复用会让新工作一开始就处于已取消态）。
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _workCts, cts);
        try { previous?.Dispose(); } catch { /* 已被关窗路径释放过：忽略 */ }
        var ct = cts.Token;

        // ═══ 先问一句"到底有没有可用后端"（2026-09-21 告知整改）═══
        // 扇出 N 行之前不先判断，N 行各自注定失败，而下面的行级容错会把原因全吞掉 ——
        // 用户看到的就是"⚠ 翻译完成（N/N 行失败，已保留原文）"：既没原因，也没出路。
        // ⚠ 2026-10-23 补一条 Warn 落盘：这句话原先只进状态栏，而状态栏会被下一次点击盖掉，
        //    日志里查不到"我点了翻译为什么没反应"这件事 —— "成功照打、失败无痕"的那一类盲区。
        if (!TranslationService.AnyBackendAvailable(_translationConfig))
        {
            string why = TranslationService.DescribeBackends(_translationConfig);
            LogService.Warn("OcrPreview", $"翻译未开始：一条后端都没配。{why.Replace("\n", "；")}", LogCategory.Network);
            _translating = false;   // 2026-11-11 补：原先忘了复位，一次"没配置"就永久卡住本窗口的翻译
            DispatcherQueue.TryEnqueue(() =>
            {
                TranslateBtn.IsEnabled = true;
                InfoTxt.Text = "⚠ " + why;
            });
            return;
        }

        // ═══ 离线模式的前置检查（2026-11-11 离线翻译包方案）═══
        // 在扇出之前问"语言包/运行环境在不在"：缺了就弹引导（可就地下载语言包，
        // 或改用在线翻译），而不是让 N 行各自失败再把原因吞进"N/N 行失败"里。
        // 弹窗/下载期间的取消（关窗）会走到 ct —— 下载进程与翻译任务都会被收掉。
        if (_translationConfig.UseOfflineArgos)
        {
            var readiness = OfflineArgosEngine.CheckReady(
                _translationConfig.OfflineSourceLangHint, _translationConfig.TargetLanguage);
            if (readiness.State != OfflineArgosEngine.ReadinessState.Ready)
            {
                var action = await HandleOfflineNotReadyAsync(readiness, ct);
                if (action == OfflineFallbackAction.Abort) return;
                if (action == OfflineFallbackAction.ProceedOnline)
                    _translationConfig.UseOfflineArgos = false;   // 继续往下走在线路（下方 Vision/文本分派自然接住）
                // ProceedOffline：语言包已就地下载完，UseOfflineArgos 保持 true，由下方分派接住
            }
        }

        try
        {
            var translator = new TranslationService(_translationConfig);
            var lines = _ocr.Lines;
            string? firstError = null;      // 行级容错不许吞掉原因，留一份给状态栏
            int? definiteFailed = null;     // 非 null = 这条路自己知道失败了几行，不必靠"译文==原文"猜
            int fallbackCount = 0;          // LLM 批量没交出、改走逐行的行数（只作可见性说明）
            List<string> translated;

            if (TranslationService.VisionConfigured(_translationConfig))
            {
                // ═══ 视觉路：整图像素直传，一次请求出全部行 ═══
                // 刻意不把 OCR 文本塞进 prompt —— 那样模型会照着文本翻而不是读图，
                // 这条链路就退化成下面的文本路了。
                // 行数对不上时 TranslateImageWithVisionAsync 自己抛 TranslationException，
                // 由本方法的 catch 如实显示；绝不按顺序硬套，错位覆盖比失败更难被发现。
                byte[] png;
                // 4K PNG 编码是纯 CPU 活，别压在 UI 线程上（翻译窗口会整段无响应）
                try { png = await Task.Run(EncodePngForVision); }
                catch (Exception ex)
                {
                    throw new TranslationException($"截图编码 PNG 失败，无法走视觉路：{ex.Message}");
                }
                translated = (await translator.TranslateImageWithVisionAsync(
                    png, _translationConfig.TargetLanguage, lines.Count, ct)).ToList();
                // 视觉路要么整批交出 N 行、要么抛异常（见 TranslateImageWithVisionAsync），
                // 所以这里不存在"某行失败"。不显式记这一笔的话，下面那句按
                // "译文 == 原文"数失败的兜底统计会把专有名词/同语言的正常行算成失败。
                definiteFailed = 0;
            }
            else if (_translationConfig.UseOfflineArgos)
            {
                // ═══ 离线路：整批进一个本地 Python 进程（2026-11-11 离线翻译包方案）═══
                // 不走下面的逐行扇出：Argos 每个进程要加载模型（1-3 s），N 行各起一个进程
                // 就是 N 次加载。一个进程装下整批（stdin 逐行进、JSON 数组出），
                // 行级容错在桥内做：失败行回填原文并计入 FailedCount —— 语义与视觉路
                // 相同（整批返回或整批抛异常），definiteFailed 记真实失败数。
                var from = OfflineArgosEngine.NormalizeLang(_translationConfig.OfflineSourceLangHint) ?? "en";
                var to = OfflineArgosEngine.NormalizeLang(_translationConfig.TargetLanguage) ?? "en";
                var batch = await OfflineArgosEngine.TranslateBatchAsync(
                    lines.Select(l => l.Text).ToArray(), from, to, ct);
                translated = batch.Lines.ToList();
                definiteFailed = batch.FailedCount;
                if (batch.FirstError is not null) firstError = batch.FirstError;
            }
            else if (TranslationService.LlmTextBatchConfigured(_translationConfig))
            {
                // ═══ LLM 文本路：整批带编号 JSON 发送，按行回填 ═══
                // 2026-11-16：旧实现把 N 行分别发给 LLM，单行缺上下文 + 无行数约束，
                // 模型容易漏词/扩写/带解释；批量接口在 Core 内做等行数校验，失败分片才逐行回退。
                var batch = await translator.TranslateBatchAsync(
                    lines.Select(l => l.Text).ToArray(),
                    _translationConfig.TargetLanguage, null, ct);
                translated = batch.Lines.ToList();
                definiteFailed = batch.FailedCount;
                fallbackCount = batch.FallbackCount;
                if (batch.FirstError is not null) firstError = batch.FirstError;
            }
            else
            {
            // ═══ P-4/R-5 修复: 并发限制 + 行级容错 ═══
            // 旧实现 Task.WhenAll 无并发限制 → 免费通道 (Edge / 必应 / MyMemory) 可能限流/封 IP;
            // 且任一行异常导致 WhenAll 快速失败 → 全部译文丢失。
            // 新实现: SemaphoreSlim(4) 限并发 + 逐行 try/catch (失败行回填原文, 不影响其他行)。
            using var sem = new System.Threading.SemaphoreSlim(4);
            var tasks = new Task<string>[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                var lineText = lines[i].Text;
                tasks[i] = Task.Run(async () =>
                {
                    // ⚠️ WaitAsync 必须带 token：否则关窗后仍会排队等槽位，
                    //    整个扇出要等前一批请求跑完才退出（这正是本次要修的行为）。
                    await sem.WaitAsync(ct);
                    try { return await translator.TranslateAsync(lineText, _translationConfig.TargetLanguage, null, ct); }
                    // ⚠️ 取消必须【先于】通用 catch —— 否则 OperationCanceledException 会被
                    //    当成"这一行翻译失败"，把 firstError 写成"操作已取消"，
                    //    于是关窗在状态栏里显示成"某行失败"。取消不是行级失败，要让它抛上去，
                    //    由 TranslateAndShowAsync 的 catch(OperationCanceledException) 静默收尾。
                    catch (OperationCanceledException) { throw; }
                    // 行级容错保留（P-4/R-5：一行失败不许带走全部译文），但原因要记下来
                    catch (Exception ex)
                    {
                        System.Threading.Interlocked.CompareExchange(ref firstError, ex.Message, null);
                        return lineText;
                    }
                    finally { sem.Release(); }
                });
            }
            await Task.WhenAll(tasks);
            translated = tasks.Select(t => t.Result).ToList();
            }

            // ═══ 取消后不要再发布结果 ═══
            // 窗口已关/已取消时发布快照没有意义，而且会让"关窗后仍在改状态"这件事
            // 从"浪费网络"升级成"改内存 + 入队"。真正的干净退出点在这里。
            ct.ThrowIfCancellationRequested();

            // ═══ 一次性发布（2026-10-07 加固）═══
            // 译文、失败行数、首个错误合成一个不可变对象，赋值是单次引用写入 ⇒
            // 渲染端读到的永远是"完整的一份"，不会再出现半成品。
            _translation = new TranslationSnapshot(translated, definiteFailed, fallbackCount, firstError);

DispatcherQueue.TryEnqueue(() =>
            {
                _showTranslation = true;
                // 译文就绪 ⇒ 「保存译文图」才有意义（之前点它只会得到一条"还没有译文"）
                SaveTranslatedBtn.IsEnabled = true;
                // ═══ 2026-11-11 性能审计 P2：不要双渲染 ═══
                // IsChecked 置 true 会同步 raise Checked → OnModeToggleChanged → RenderOverlay
                // + UpdateInfo 各一次；原先紧随其后的显式 RenderOverlay() 是第二次全量重建。
                // 只有"开关已在译文档"（重翻译场景）时事件不触发，才需要显式刷新新快照。
                bool toggleWasChecked = ModeToggle.IsChecked == true;
                ModeToggle.IsChecked = true;
                ModeToggle.Content = "显示原文";
                if (toggleWasChecked) { RenderOverlay(); UpdateInfo(); }
                var snap = _translation;
                if (snap is null) return;                 // 理论不可达（上面刚赋值），但读快照而非字段更稳
                int total = snap.Lines.Count;
                // "译文 == 原文"只是文本路的近似判据：同语言或专有名词会被算成失败。
                // 视觉路自己知道结果（整批成功或整批抛异常），走 definiteFailed。
                int failed = snap.DefiniteFailed ??
                    snap.Lines.Zip(lines, (t, l) => t == l.Text ? 1 : 0).Sum();
                string fallbackNote = snap.FallbackCount > 0
                    ? $"；其中 {snap.FallbackCount} 行走逐行回退"
                    : "";
                string why = snap.FirstError is null ? "" : $"（原因示例：{snap.FirstError}）";
                InfoTxt.Text = failed switch
                {
                    0 => $"✅ 翻译完成（{total} 行）{fallbackNote}，点击文字块可复制译文",
                    // 全行皆败不叫"完成"——原先这里仍打 ✅ 语气的"翻译完成"，是把故障说成成功
                    _ when failed == total => $"❌ 翻译失败：{total}/{total} 行都没出译文 {fallbackNote}{why}",
                    _ => $"⚠ 翻译完成（{failed}/{total} 行失败，已保留原文）{fallbackNote}{why}，点击文字块可复制",
                };
            });
        }
        catch (OperationCanceledException)
        {
            // ═══ 关窗导致的取消：不是错误，不要报成"翻译失败" ═══
            // 这条分支若也走下面的通用文案，用户会看到"❌ 翻译服务暂时不可用"——
            // 而事实是他自己关掉了窗口。实测这条路径原先不存在，取消会落进 catch(Exception)。
            LogService.Info("OcrPreviewWindow", "翻译已取消（窗口关闭）");
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 不再靠关键词猜测去改写原因：TranslationException 的正文本身就是"缺什么 / 为什么失败"
                // （见 TranslationService.DescribeBackends）。原先这段会把我们刚给出的说明
                // 覆盖成一句更模糊的"建议检查网络连接"——等于把告知又盖回去。
                string msg = ex is TranslationException
                    ? ex.Message
                    : $"翻译服务暂时不可用：{ex.Message}\n可在 设置 → 翻译 配置有道官方凭据或自定义 LLM。";
                InfoTxt.Text = $"❌ {msg}";
            });
        }
        finally
        {
            _translating = false;
            DispatcherQueue.TryEnqueue(() => TranslateBtn.IsEnabled = true);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        using var _op = CoreLog.ContinueOp(CaptureOpId);
        if (_saveHandler is null)
        {
            InfoTxt.Text = "⚠ 保存功能未配置";
            return;
        }

        try
        {
            await _saveHandler(_rawPixels, _imgW, _imgH);
            InfoTxt.Text = "✅ 已按主界面设置保存";
        }
        catch (Exception ex)
        {
            InfoTxt.Text = $"❌ 保存失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 保存"当前画面"：译文模式把译文画到截图像素上，原文模式就是原图。
    /// <para>与「保存原图」的区别只有一个：喂给管线的像素可能多了一层译文文字。落盘、剪贴板、
    /// 格式协商全部复用 <see cref="_saveHandler"/>，不在这个窗口里另写一套。</para>
    /// <para>为什么跟随 <see cref="_showTranslation"/> 而不总是烘焙译文：按钮上写的是"当前"，
    /// 用户切到原文视图后点它，得到的必须是他正看着的那一屏，否则这个标签就是假的。</para>
    /// </summary>
    private async void OnSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        using var _op = CoreLog.ContinueOp(CaptureOpId);
        var snap = _translation;
        if (snap is null) { InfoTxt.Text = "⚠ 还没有译文可保存"; return; }
        if (_saveHandler is null) { InfoTxt.Text = "⚠ 保存功能未配置"; return; }
        if (_savingTranslated) return;

        _savingTranslated = true;
        SaveTranslatedBtn.IsEnabled = false;
        bool bake = _showTranslation;     // 只有正在看译文时才烘焙文字
        InfoTxt.Text = bake ? "💾 正在合成译文图..." : "💾 正在保存当前画面（原图）...";
        try
        {
            byte[] composed;
            if (bake)
            {
                var lines = _ocr.Lines;
                if (lines is null || lines.Count == 0) { InfoTxt.Text = "⚠ 没有可覆盖的文字行"; return; }
                var boxes = new (int X, int Y, int Width, int Height)[lines.Count];
                for (int i = 0; i < lines.Count; i++) boxes[i] = LineRect(lines[i]);
                if (boxes.Length != snap.Lines.Count)
                    LogService.Warn("OcrPreview",
                        $"译文行数({snap.Lines.Count})与识别行数({boxes.Length})不一致，只覆盖前 {Math.Min(boxes.Length, snap.Lines.Count)} 行",
                        LogCategory.UI);

                string font = EffectiveFontFamily;
                var originals = new string[lines.Count];
                for (int i = 0; i < lines.Count; i++) originals[i] = lines[i].Text;
                // 2026-11-11 性能审计 P2：把预览缓存的行几何交给 Compose —— 同一批 (像素, det 框)
                // 的墨迹扫描在预览已经付过一次，导出不再重复（未渲染过覆盖层时为 null，Compose 就地算）。
                var geomForCompose = _lineGeom is null
                    ? null
                    : _lineGeom.Select(p => p?.Geom).ToList();
                // 合成（clone + Win2D 光栅化 + 混合）整段放后台：4K 一次 clone 33MB，压 UI 线程会整窗无响应
                composed = await Task.Run(() =>
                    OcrTextOverlay.Compose(_rawPixels, _imgW, _imgH, boxes, snap.Lines, font, originals,
                        geomForCompose));
                LogService.Info("OcrPreview", $"译文覆盖合成完成: {_imgW}x{_imgH} {boxes.Length} 行", LogCategory.UI);
            }
            else
            {
                composed = _rawPixels;    // 与原图保存同源（管线内部会自己拷贝，不改入参）
            }

            await _saveHandler(composed, _imgW, _imgH);
            InfoTxt.Text = bake
                ? "✅ 当前画面（含译文）已保存（格式跟随主窗口设置）"
                : "✅ 当前画面（原图）已保存（格式跟随主窗口设置）";
        }
        catch (Exception ex)
        {
            LogService.Error("OcrPreview", $"保存当前画面失败: {ex.Message}", ex, LogCategory.UI);
            InfoTxt.Text = $"❌ 保存当前画面失败: {ex.Message}";
        }
        finally
        {
            _savingTranslated = false;
            SaveTranslatedBtn.IsEnabled = HasTranslation;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => this.Close();

    /// <summary>
    /// 关窗时取消在途翻译（2026-10-07 审查修复）。
    /// <para>原先这里是空的 —— 于是关掉预览窗后，最多 N 行翻译请求会继续跑到各自的内部超时
    /// （Google/Bing 每端点 5 s、LLM 视配置），并一直占着 <c>SemaphoreSlim(4)</c> 的槽位。
    /// 用户已经关窗、程序还在替他发请求，既费流量也在日志里留下无来源的失败记录。</para>
    /// <para>⚠️ 只 Cancel 不 Dispose：翻译链仍在跑（正在 await 这些请求），
    /// 此刻 Dispose 会让它们抛 <see cref="ObjectDisposedException"/> 而不是
    /// <see cref="OperationCanceledException"/> —— 那就把"用户主动取消"变成了"异常"。
    /// 由链自己在 finally 里收尾（下次翻译会换新 CTS，旧的在那里释放）。</para>
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        try { _workCts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放：无需再取消 */ }
    }

    // ═══════════════════════════════════════
    //  工具
    // ═══════════════════════════════════════

    private void UpdateInfo()
    {
        int n = _ocr.Lines?.Count ?? 0;
        InfoTxt.Text = _showTranslation
            ? $"译文覆盖模式 · {n} 行 · 用户字体渲染 · 点击文字块复制译文 · 左上角可隐藏覆盖层"
            : $"原文高亮模式 · {n} 行 · 识别文字以用户字体覆盖显示 · 点击文字块复制原文 · 点「翻译」切译文";
    }

    private static void SetClipboard(string text)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s.Replace("\n", " ") : s.Replace("\n", " ")[..max] + "…";
}
