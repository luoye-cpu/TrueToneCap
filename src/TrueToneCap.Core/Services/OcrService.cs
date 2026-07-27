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
                // 预处理若改变了尺寸（如 ScaleUp 2x），坐标处于放大坐标系，
                // 必须按比例归一化回原图坐标，否则 OCR 预览窗口的"点对点覆盖"会对不齐。
                if (preprocessed.Width != width || preprocessed.Height != height)
                {
                    float sx = (float)width / preprocessed.Width;
                    float sy = (float)height / preprocessed.Height;
                    foreach (var line in result2.Lines)
                        foreach (var word in line.Words)
                        {
                            word.X = (int)(word.X * sx);
                            word.Y = (int)(word.Y * sy);
                            word.Width = (int)Math.Max(1, word.Width * sx);
                            word.Height = (int)Math.Max(1, word.Height * sy);
                        }
                }
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
        // ── 中英混合模式: 同时运行中文+英文引擎，按位置合并 ──
        if (languageTag == "zh-en")
            return await RunMixedLanguageOcrAsync(bgraPixels, width, height, ct);

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        try
        {
            bitmap.CopyFromBuffer(bgraPixels.AsBuffer());

            OcrEngine? engine = null;
            if (!string.IsNullOrEmpty(languageTag))
            {
                engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));
            }
            else
            {
                // 优先尝试中文（简体），确保中文截图可识别
                engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"));
                // 回退到用户配置语言
                engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            }

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

    // ═══════════════════════════════════════
    //  中英混合 OCR: 双引擎并行 + 位置合并
    // ═══════════════════════════════════════

    /// <summary>
    /// 中英混合模式：同时运行 zh-Hans-CN 和 en-US 两个 Windows OCR 引擎，
    /// 按词级别位置去重合并，确保中英文都能被正确识别。
    /// 合并策略：以中文引擎结果为基准，补充英文引擎中不重叠的英文词。
    /// </summary>
    private static async Task<OcrResult> RunMixedLanguageOcrAsync(byte[] bgraPixels, int width, int height,
        CancellationToken ct)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        try
        {
            bitmap.CopyFromBuffer(bgraPixels.AsBuffer());

            var zhEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"));
            var enEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

            // 至少一个引擎可用
            if (zhEngine is null && enEngine is null)
                return new OcrResult { Text = "", Error = "无法创建 OCR 引擎（请安装中文和英文语言包）" };

            // 并行运行两个引擎
            Task<Windows.Media.Ocr.OcrResult?> zhTask = zhEngine is not null
                ? zhEngine.RecognizeAsync(bitmap).AsTask(ct)
                : Task.FromResult<Windows.Media.Ocr.OcrResult?>(null);
            Task<Windows.Media.Ocr.OcrResult?> enTask = enEngine is not null
                ? enEngine.RecognizeAsync(bitmap).AsTask(ct)
                : Task.FromResult<Windows.Media.Ocr.OcrResult?>(null);

            await Task.WhenAll(zhTask, enTask);
            ct.ThrowIfCancellationRequested();

            var zhResult = zhTask.Result;
            var enResult = enTask.Result;

            // 收集所有词（带来源标记）
            var allWords = new List<(OcrWord Word, string Source)>();

            if (zhResult is not null)
            {
                foreach (var line in zhResult.Lines)
                    foreach (var w in line.Words)
                        allWords.Add((new OcrWord
                        {
                            Text = w.Text,
                            X = (int)w.BoundingRect.X,
                            Y = (int)w.BoundingRect.Y,
                            Width = (int)w.BoundingRect.Width,
                            Height = (int)w.BoundingRect.Height
                        }, "zh"));
            }

            if (enResult is not null)
            {
                foreach (var line in enResult.Lines)
                    foreach (var w in line.Words)
                        allWords.Add((new OcrWord
                        {
                            Text = w.Text,
                            X = (int)w.BoundingRect.X,
                            Y = (int)w.BoundingRect.Y,
                            Width = (int)w.BoundingRect.Width,
                            Height = (int)w.BoundingRect.Height
                        }, "en"));
            }

            // 去重合并：如果两个词的位置重叠 >50%，保留文本更长的那个
            var merged = MergeOverlappingWords(allWords);

            // 按 Y → X 排序，重建行结构
            merged.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

            var lines = new List<OcrLine>();
            var currentLine = new List<OcrWord>();
            int lastY = -100;
            const int lineGap = 8; // Y 坐标差 <8px 视为同一行

            foreach (var word in merged)
            {
                if (currentLine.Count > 0 && Math.Abs(word.Y - lastY) > lineGap)
                {
                    lines.Add(new OcrLine { Text = string.Join(" ", currentLine.Select(w => w.Text)), Words = [.. currentLine] });
                    currentLine = [];
                }
                currentLine.Add(word);
                lastY = word.Y;
            }
            if (currentLine.Count > 0)
                lines.Add(new OcrLine { Text = string.Join(" ", currentLine.Select(w => w.Text)), Words = [.. currentLine] });

            return new OcrResult
            {
                Text = string.Join("\n", lines.Select(l => l.Text)),
                Lines = lines,
                Mode = "Mixed(zh+en)"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new OcrResult { Text = "", Error = $"混合 OCR 异常: {ex.Message}" };
        }
        finally { bitmap.Dispose(); }
    }

    /// <summary>位置去重：重叠 >50% 的词保留文本更长的。</summary>
    private static List<OcrWord> MergeOverlappingWords(List<(OcrWord Word, string Source)> words)
    {
        var result = new List<OcrWord>();
        var used = new bool[words.Count];

        for (int i = 0; i < words.Count; i++)
        {
            if (used[i]) continue;
            var best = words[i].Word;
            used[i] = true;

            for (int j = i + 1; j < words.Count; j++)
            {
                if (used[j]) continue;
                if (OverlapRatio(best, words[j].Word) > 0.5f)
                {
                    used[j] = true;
                    // 保留文本更长的（通常更准确）
                    if (words[j].Word.Text.Length > best.Text.Length)
                        best = words[j].Word;
                }
            }
            result.Add(best);
        }
        return result;
    }

    /// <summary>计算两个词框的重叠面积比（相对于较小框）。</summary>
    private static float OverlapRatio(OcrWord a, OcrWord b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x2 <= x1 || y2 <= y1) return 0f;

        float overlap = (x2 - x1) * (y2 - y1);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float minArea = Math.Min(areaA, areaB);
        return minArea > 0 ? overlap / minArea : 0f;
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
