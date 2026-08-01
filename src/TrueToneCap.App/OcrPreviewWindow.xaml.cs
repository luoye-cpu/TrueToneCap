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
    //  覆盖层：按 OCR 坐标点对点绘制命中块
    // ═══════════════════════════════════════

    /// <summary>原图像素坐标 → 覆盖层(Canvas)显示坐标的缩放比。</summary>
    /// <remarks>当 OverlayCanvas 尺寸与 PreviewImage 渲染尺寸一致时，x/y 缩放比相同。</remarks>
    private double ScaleX => _imgW > 0 ? OverlayCanvas.Width / _imgW : 1.0;
    private double ScaleY => _imgH > 0 ? OverlayCanvas.Height / _imgH : 1.0;

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (_ocr.Lines is null || _ocr.Lines.Count == 0 || OverlayCanvas.Width <= 0) return;

        double canvasW = OverlayCanvas.Width, canvasH = OverlayCanvas.Height;
        double sx = ScaleX, sy = ScaleY;

        // ═══ 第一层：整体标暗（轻度暗化，保留原图可见性）═══
        var darkOverlay = new Border
        {
            Width = canvasW, Height = canvasH,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(darkOverlay, 0);
        Canvas.SetTop(darkOverlay, 0);
        OverlayCanvas.Children.Add(darkOverlay);

        // ═══ 第二层：逐行渲染 ═══
        for (int i = 0; i < _ocr.Lines.Count; i++)
        {
            var line = _ocr.Lines[i];
            var rect = LineRect(line);
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            double cx = rect.X * sx, cy = rect.Y * sy, cw = rect.Width * sx, ch = rect.Height * sy;
            bool hasTranslation = _translatedLines is not null && i < _translatedLines.Count;
            string translated = hasTranslation ? _translatedLines![i] : "";

            if (_showTranslation && hasTranslation)
            {
                // ═══ 译文模式：条形覆盖块，刚好覆盖原文区域 ═══
                var bar = new Border
                {
                    Width = Math.Max(1, cw), Height = Math.Max(1, ch),
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 15, 23, 42)), // 深色不透明底
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 56, 189, 248)),
                    BorderThickness = new Thickness(1),
                };
                Canvas.SetLeft(bar, cx);
                Canvas.SetTop(bar, cy);

                // 译文文字：直接设置字体大小，不使用 Viewbox
                var textBlock = new TextBlock
                {
                    Text = translated,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 240)),
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(6, 0, 6, 0),
                    // 字体大小与 OCR 框高度自适应：最小 10px，最大 48px
                    FontSize = Math.Clamp(ch * 0.7, 10.0, 48.0),
                };
                bar.Child = textBlock;
                ToolTipService.SetToolTip(bar, $"原文: {line.Text}\n译文: {translated}");

                int idx = i;
                bar.Tapped += (_, _) => CopyLine(idx);
                OverlayCanvas.Children.Add(bar);
            }
            else
            {
                // ═══ 原文模式：透明边框标记 + 悬停显示译文预览 ═══
                var block = new Border
                {
                    Width = Math.Max(1, cw), Height = Math.Max(1, ch),
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 56, 189, 248)),
                    BorderThickness = new Thickness(1),
                };
                Canvas.SetLeft(block, cx);
                Canvas.SetTop(block, cy);

                // 悬停时：如果有译文，显示译文预览条；否则微亮
                int idx = i;
                TextBlock? hoverLabel = null;

                block.PointerEntered += (_, _) =>
                {
                    block.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248));
                    if (hasTranslation && !string.IsNullOrEmpty(translated))
                    {
                        // 悬停显示译文预览条（覆盖在原文上方）
                        block.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 15, 23, 42));
                        if (hoverLabel is null)
                        {
                            hoverLabel = new TextBlock
                            {
                                Text = translated,
                                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 240)),
                                FontSize = Math.Max(10, ch * 0.7),
                                TextWrapping = TextWrapping.NoWrap,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(4, 0, 4, 0),
                            };
                            block.Child = hoverLabel;
                        }
                    }
                    else
                    {
                        block.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 56, 189, 248));
                    }
                };
                block.PointerExited += (_, _) =>
                {
                    block.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    block.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 56, 189, 248));
                    block.Child = null;
                    hoverLabel = null;
                };
                block.Tapped += (_, _) => CopyLine(idx);

                // 默认 ToolTip 显示原文
                ToolTipService.SetToolTip(block, hasTranslation ? $"{line.Text} → {translated}" : line.Text);
                OverlayCanvas.Children.Add(block);
            }
        }
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
