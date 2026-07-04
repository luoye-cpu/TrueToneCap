// TrueToneCap.Core/Services/OcrService.cs
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace TrueToneCap.Core.Services;

/// <summary>Windows 内置 OCR 文字提取服务</summary>
public static class OcrService
{
    /// <summary>从 BGRA8 像素数据中提取文字</summary>
    public static async Task<OcrResult> ExtractTextAsync(byte[] bgraPixels, int width, int height,
        string? languageTag = null, CancellationToken ct = default)
    {
        // 使用 CopyFromBuffer 写入像素（避免 COM 互操作 / unsafe 指针问题）
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        bitmap.CopyFromBuffer(bgraPixels.AsBuffer());

        // OCR 引擎
        var engine = string.IsNullOrEmpty(languageTag)
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));

        if (engine is null)
            return new OcrResult { Text = "", Error = "无法创建 OCR 引擎（请安装语言包）" };

        var result = await engine.RecognizeAsync(bitmap);
        bitmap.Dispose();

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

    /// <summary>获取可用的 OCR 语言列表</summary>
    public static List<string> GetAvailableLanguages()
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Select(l => l.LanguageTag)
            .ToList();
    }
}

public class OcrResult
{
    public string Text { get; set; } = "";
    public string? Error { get; set; }
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
