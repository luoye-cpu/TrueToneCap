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

        if (RootGrid.IsLoaded)
            FontHelper.ApplyFontToVisualTree(RootGrid, FontLoader.DefaultFontFamily);
        else
            RootGrid.Loaded += (_, _) => FontHelper.ApplyFontToVisualTree(RootGrid, FontLoader.DefaultFontFamily);

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
    //  覆盖层：按 OCR 坐标点对点绘制命中块
    // ═══════════════════════════════════════

    /// <summary>原图像素坐标 → 覆盖层(Canvas)显示坐标的缩放比（Uniform 下 x/y 一致）。</summary>
    private double ScaleX => _imgW > 0 ? OverlayCanvas.Width / _imgW : 1.0;
    private double ScaleY => _imgH > 0 ? OverlayCanvas.Height / _imgH : 1.0;

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (_ocr.Lines is null || _ocr.Lines.Count == 0 || OverlayCanvas.Width <= 0) return;

        double sx = ScaleX, sy = ScaleY;
        for (int i = 0; i < _ocr.Lines.Count; i++)
        {
            var line = _ocr.Lines[i];
            var rect = LineRect(line); // 原图坐标
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            double cx = rect.X * sx, cy = rect.Y * sy, cw = rect.Width * sx, ch = rect.Height * sy;

            var block = new Border
            {
                Width = Math.Max(1, cw), Height = Math.Max(1, ch),
                CornerRadius = new CornerRadius(2),
            };
            Canvas.SetLeft(block, cx);
            Canvas.SetTop(block, cy);

            if (_showTranslation && _translatedLines is not null && i < _translatedLines.Count)
            {
                // 译文模式：浅蓝色背景 + 译文 Viewbox 缩放覆盖（点对点替换原字）
                block.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 200, 230, 255)); // 浅蓝色背景
                block.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 68, 136, 255)); // 蓝色边框
                block.BorderThickness = new Thickness(1);
                var vb = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2, 0, 2, 0) };
                vb.Child = new TextBlock
                {
                    Text = _translatedLines[i],
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                block.Child = vb;
                ToolTipService.SetToolTip(block, _translatedLines[i]);
            }
            else
            {
                // 原文模式：半透明高亮命中块，透出原字
                block.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 255, 235, 59));
                block.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 193, 7));
                block.BorderThickness = new Thickness(1);
                ToolTipService.SetToolTip(block, line.Text);
            }

            int idx = i;
            block.PointerEntered += (_, _) => block.Opacity = 0.75;
            block.PointerExited += (_, _) => block.Opacity = 1.0;
            block.Tapped += (_, _) => CopyLine(idx);
            OverlayCanvas.Children.Add(block);
        }
    }

    /// <summary>取行的原图边界矩形（优先 Words 合并，回退行级坐标）。</summary>
    private static (int X, int Y, int Width, int Height) LineRect(OcrLine line)
    {
        if (line.Words is { Count: > 0 })
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (var w in line.Words)
            {
                x1 = Math.Min(x1, w.X); y1 = Math.Min(y1, w.Y);
                x2 = Math.Max(x2, w.X + w.Width); y2 = Math.Max(y2, w.Y + w.Height);
            }
            if (x2 > x1 && y2 > y1) return (x1, y1, x2 - x1, y2 - y1);
        }
        return (0, 0, 0, 0);
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
            // 逐行并行翻译，保证与 Lines 一一对应（点对点覆盖的前提）
            var translator = new TranslationService(_translationConfig);
            var tasks = _ocr.Lines.Select(l => translator.TranslateAsync(l.Text, _translationConfig.TargetLanguage));
            var results = await Task.WhenAll(tasks);
            _translatedLines = results.ToList();

DispatcherQueue.TryEnqueue(() =>
            {
                _showTranslation = true;
                ModeToggle.IsChecked = true;
                ModeToggle.Content = "显示原文";
                RenderOverlay();
                InfoTxt.Text = $"✅ 翻译完成（{_translatedLines.Count} 行），点击文字块可复制译文";
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
                InfoTxt.Text = $"❌ 翻译失败: {ex.Message}");
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
            ? $"译文覆盖模式 · {n} 行 · 点击文字块复制译文 · 切“显示原文”看高亮"
            : $"原文高亮模式 · {n} 行 · 点击文字块复制原文 · 点“翻译”或切“显示译文”";
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
