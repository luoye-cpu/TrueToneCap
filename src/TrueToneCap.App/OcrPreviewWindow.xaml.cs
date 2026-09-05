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

namespace TrueToneCap.App;

public sealed partial class OcrPreviewWindow : Window
{
    private readonly byte[] _rawPixels;
    private readonly int _imgW, _imgH;
    private readonly OcrResult _ocr;            // Lines/Words 坐标已归一化到原图
    private readonly LlmConfig _translationConfig;
    private Func<byte[], int, int, Task>? _saveHandler; // 主窗口注入的保存逻辑（按主UI设置）
    private List<string>? _translatedLines;     // 与 _ocr.Lines 一一对应；null = 尚未翻译
    private bool _showTranslation;
    private bool _translating;

    /// <summary>主窗口注入的保存逻辑（按主UI设置）</summary>
    public Func<byte[], int, int, Task>? SaveHandler
    {
        get => _saveHandler;
        set => _saveHandler = value;
    }

    public OcrPreviewWindow(byte[] bgraPixels, int width, int height, OcrResult ocr,
        LlmConfig translationConfig, bool autoTranslate = false,
        Func<byte[], int, int, Task>? saveHandler = null)
    {
        this.InitializeComponent();
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

    // ── 防抖：拖拽/缩放时 SizeChanged 高频触发，合并为一次延迟渲染 ──
    private bool _syncPending;
    private void DebouncedSyncOverlay()
    {
        if (_syncPending) return;
        _syncPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _syncPending = false;
            try { SyncOverlayAndRender(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OcrPreview] SyncOverlay 异常: {ex.Message}");
            }
        });
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
        for (int i = 0; i < _ocr.Lines.Count; i++)
        {
            var line = _ocr.Lines[i];
            var rect = LineRect(line);
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            double cx = rect.X * sx, cy = rect.Y * sy, cw = rect.Width * sx, ch = rect.Height * sy;
            bool hasTranslation = _translatedLines is not null && i < _translatedLines.Count;
            string displayText = _showTranslation && hasTranslation
                ? _translatedLines![i]
                : line.Text;
            // 原文模式下若译文已就绪，ToolTip 显示译文
            string tooltipText = hasTranslation
                ? $"原文: {line.Text}\n译文: {_translatedLines![i]}"
                : line.Text;

            // 高亮背景块 (原文模式=半透明蓝, 译文模式=深色不透明)
            byte bgA = _showTranslation ? (byte)235 : (byte)80;
            var bgColor = _showTranslation
                ? Windows.UI.Color.FromArgb(235, 15, 23, 42)    // 深色不透明
                : Windows.UI.Color.FromArgb(80, 56, 120, 210);  // 半透明蓝
            var borderColor = _showTranslation
                ? Windows.UI.Color.FromArgb(120, 56, 189, 248)  // 蓝边框
                : Windows.UI.Color.FromArgb(100, 100, 180, 255); // 浅蓝边框

            var overlay = new Border
            {
                Width = Math.Max(1, cw), Height = Math.Max(1, ch),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                // ═══ 2026-08-25: 文字块可点击复制 + 悬停高亮 ═══
            };
            Canvas.SetLeft(overlay, cx);
            Canvas.SetTop(overlay, cy);

            // ═══ 文字覆盖层 — 使用用户选择的字体，与OCR区域对齐 ═══
            var textBlock = new TextBlock
            {
                Text = displayText,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 240)),
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = Math.Clamp(ch * 0.65, 10.0, 48.0),
                // ═══ 2026-08-25: 使用用户选择的字体覆盖 ═══
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(EffectiveFontFamily),
            };
            overlay.Child = textBlock;

            // 悬停效果：增加亮度
            overlay.PointerEntered += (_, _) =>
            {
                overlay.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248));
                overlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    _showTranslation ? (byte)255 : (byte)120, 30, 60, 180));
            };
            overlay.PointerExited += (_, _) =>
            {
                overlay.BorderBrush = new SolidColorBrush(borderColor);
                overlay.Background = new SolidColorBrush(bgColor);
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
        OverlayToggleTxt.Text = show ? "隐藏覆盖" : "显示覆盖";
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
        string text = (_showTranslation && _translatedLines is not null && idx < _translatedLines.Count)
            ? _translatedLines[idx]
            : _ocr.Lines[idx].Text;
        SetClipboard(text);
        InfoTxt.Text = $"📋 已复制: {Truncate(text, 40)}";
    }

    private async void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        string text = CurrentFullText();
        if (string.IsNullOrWhiteSpace(text)) { InfoTxt.Text = "⚠ 无文字可复制"; return; }
        await ShowTextDialogAsync(text);
    }

    private string CurrentFullText()
    {
        if (_showTranslation && _translatedLines is not null)
            return string.Join("\n", _translatedLines);
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
            Title = _showTranslation ? "📝 译文内容" : "📝 原文内容",
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

    private void OnModeToggleChanged(object sender, RoutedEventArgs e)
    {
        bool want = ModeToggle.IsChecked == true;
        if (want && (_translatedLines is null || _translatedLines.Count == 0))
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

    private async Task TranslateAndShowAsync()
    {
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

        try
        {
            // ═══ P-4/R-5 修复: 并发限制 + 行级容错 ═══
            // 旧实现 Task.WhenAll 无并发限制 → 免费通道 (有道/Google) 可能限流/封 IP;
            // 且任一行异常导致 WhenAll 快速失败 → 全部译文丢失。
            // 新实现: SemaphoreSlim(4) 限并发 + 逐行 try/catch (失败行回填原文, 不影响其他行)。
            var translator = new TranslationService(_translationConfig);
            using var sem = new System.Threading.SemaphoreSlim(4);
            var lines = _ocr.Lines;
            var tasks = new Task<string>[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                var lineText = lines[i].Text;
                tasks[i] = Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { return await translator.TranslateAsync(lineText, _translationConfig.TargetLanguage); }
                    catch { return lineText; } // 行级容错: 失败行显示原文
                    finally { sem.Release(); }
                });
            }
            await Task.WhenAll(tasks);
            _translatedLines = tasks.Select(t => t.Result).ToList();

DispatcherQueue.TryEnqueue(() =>
            {
                _showTranslation = true;
                ModeToggle.IsChecked = true;
                ModeToggle.Content = "显示原文";
                RenderOverlay();
                int failed = _translatedLines.Zip(lines, (t, l) => t == l.Text ? 1 : 0).Sum();
                InfoTxt.Text = failed > 0
                    ? $"⚠ 翻译完成（{failed}/{_translatedLines.Count} 行失败，已保留原文），点击文字块可复制"
                    : $"✅ 翻译完成（{_translatedLines.Count} 行），点击文字块可复制译文";
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 翻译失败：检查是否因为 LLM 不可用但未配置免费后端
                string msg = ex.Message;
                if (msg.Contains("网络") || msg.Contains("timeout") || msg.Contains("连接"))
                    msg = "翻译服务暂时不可用，建议检查网络连接。\n也可在设置中配置 LLM API 获得更稳定的翻译。";
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => this.Close();
    private void OnClosed(object sender, WindowEventArgs args) { }

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
