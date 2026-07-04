// TrueToneCap.Core/Services/OcrService.cs
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace TrueToneCap.Core.Services;

/// <summary>Windows 内置 OCR 文字提取服务（带预处理管线提升准确度）</summary>
public static class OcrService
{
    /// <summary>从 BGRA8 像素数据中提取文字（自动预处理）</summary>
    public static async Task<OcrResult> ExtractTextAsync(byte[] bgraPixels, int width, int height,
        string? languageTag = null, CancellationToken ct = default)
    {
        // ── Pass 1: 原始图像 ──
        var result1 = await RunOcrAsync(bgraPixels, width, height, languageTag, ct);

        // ── Pass 2: 自动预处理后图像 ──
        var preprocessed = BitmapPreprocessor.AutoPreprocess(bgraPixels, width, height);
        if (preprocessed.Mode != BitmapPreprocessor.PreprocessMode.None)
        {
            var result2 = await RunOcrAsync(preprocessed.Pixels, preprocessed.Width,
                preprocessed.Height, languageTag, ct);

            // 选择更长的结果（通常更准确）
            if (!string.IsNullOrEmpty(result2.Text) &&
                (string.IsNullOrEmpty(result1.Text) || result2.Text.Length > result1.Text.Length))
            {
                result2.Mode = preprocessed.Mode.ToString();
                return result2;
            }
        }

        if (string.IsNullOrEmpty(result1.Text) && string.IsNullOrEmpty(result1.Error))
            result1.Error = "未检测到文字（尝试放大截图区域或增强对比度）";

        return result1;
    }

    private static async Task<OcrResult> RunOcrAsync(byte[] bgraPixels, int width, int height,
        string? languageTag, CancellationToken ct)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        try
        {
            bitmap.CopyFromBuffer(bgraPixels.AsBuffer());

            var engine = string.IsNullOrEmpty(languageTag)
                ? OcrEngine.TryCreateFromUserProfileLanguages()
                : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));

            if (engine is null)
                return new OcrResult { Text = "", Error = "无法创建 OCR 引擎（请安装语言包）" };

            var result = await engine.RecognizeAsync(bitmap);

            return new OcrResult
            {
                Text = result.Text,
                Lines = result.Lines.Select(l => new OcrLine
                {
                    Text = l.Text,
                    Words = l.Words.Select(w => new OcrWord
                    {
                        Text = w.Text,
                        X = (int)w.BoundingRect.X,
                        Y = (int)w.BoundingRect.Y,
                        Width = (int)w.BoundingRect.Width,
                        Height = (int)w.BoundingRect.Height
                    }).ToList()
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            return new OcrResult { Text = "", Error = $"OCR 异常: {ex.Message}" };
        }
        finally { bitmap.Dispose(); }
    }

    /// <summary>获取可用的 OCR 语言列表</summary>
    public static List<string> GetAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();
        }
        catch { return ["zh-Hans", "en-US"]; }
    }
}

public class OcrResult
{
    public string Text { get; set; } = "";
    public string? Error { get; set; }
    public string? Mode { get; set; }  // 预处理模式
    public List<OcrLine> Lines { get; set; } = [];
}

public class OcrLine
{
    public string Text { get; set; } = "";
    public List<OcrWord> Words { get; set; } = [];
}

public class OcrWord
{
    public string Text { get; set; } = "";
    public int X, Y, Width, Height;
}
