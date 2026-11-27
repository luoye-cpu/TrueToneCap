// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
        ct.ThrowIfCancellationRequested();

        // ── Pass 1: 原始图像 ──
        var result1 = await RunOcrAsync(bgraPixels, width, height, languageTag, ct);

        // ── Pass 2: Windows OCR 专用预处理（2x + 对比度，不做二值化）──
        var preprocessed = BitmapPreprocessor.PrepareForWindowsOcr(bgraPixels, width, height);
        if (preprocessed.Mode != BitmapPreprocessor.PreprocessMode.None)
        {
            var result2 = await RunOcrAsync(preprocessed.Pixels, preprocessed.Width,
                preprocessed.Height, languageTag, ct);

            // 不按 Text.Length 选较长结果：阈值/放大产生的碎片行会让“更长”变成“更差”。
            // 用有效字母/数字为主、孤立标点为负分的质量分；同分时保留原图结果。
            bool usePass2 = string.IsNullOrEmpty(result1.Text);
            if (!usePass2 && !string.IsNullOrEmpty(result2.Text))
                usePass2 = OcrQualityScore(result2) > OcrQualityScore(result1);

            if (usePass2 && !string.IsNullOrEmpty(result2.Text))
            {
                // 预处理若改变了尺寸（如 ScaleUp 2x），坐标处于放大坐标系，
                // 必须按比例归一化回原图坐标，否则 OCR 预览窗口的"点对点覆盖"会对不齐。
                if (preprocessed.Width != width || preprocessed.Height != height)
                {
                    float sx = (float)width / preprocessed.Width;
                    float sy = (float)height / preprocessed.Height;
                    foreach (var line in result2.Lines)
                    {
                        line.X = (int)(line.X * sx);
                        line.Y = (int)(line.Y * sy);
                        line.Width = (int)Math.Max(1, line.Width * sx);
                        line.Height = (int)Math.Max(1, line.Height * sy);
                        foreach (var word in line.Words)
                        {
                            word.X = (int)(word.X * sx);
                            word.Y = (int)(word.Y * sy);
                            word.Width = (int)Math.Max(1, word.Width * sx);
                            word.Height = (int)Math.Max(1, word.Height * sy);
                        }
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

    /// <summary>
    /// OCR 结果质量分：有效字母/数字为主，孤立标点/单字符碎片为负分。
    /// 只用于在“原图 vs 2x 增强”两个 pass 之间择优，不参与最终文本拼接。
    /// </summary>
    private static int OcrQualityScore(OcrResult result)
    {
        int score = 0;
        foreach (var line in result.Lines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) continue;
            int meaningful = 0;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) meaningful++;
                else if (c == '\uFFFD') score -= 10;
            }
            score += meaningful * 2;
            if (meaningful == 0) score -= 8;      // “，。”“．”这类纯标点噪声行
            if (text.Length <= 1) score -= 2;     // 单个“0”“，”通常不是完整行
        }
        return score;
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

            // 把请求的标签印进错文，否则"请安装语言包"无法区分"标签形态不对"与"包真没装"。
            if (engine is null)
                return new OcrResult
                {
                    Text = "",
                    Error = "无法创建 OCR 引擎（语言 " +
                            $"{(string.IsNullOrWhiteSpace(languageTag) ? "用户配置文件语言" : languageTag)} 未安装语言包）"
                };

            // ⚠ 必须把 ct 递进去：AsTask(ct) 原先缺失，调用方取消后这里仍会跑到引擎返回为止
            // （实测 300ms 取消 ⇒ 实际 307ms 完成并返回全文，取消形同不存在）。
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct);

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
                    {
                        // 中文截图里 en-US 引擎会吐“iAmæ(RF”“，。”这类非拉丁碎片；
                        // 只接受纯 ASCII 字母/数字/常见标点的英文补充，避免污染中文行。
                        string text = w.Text.Trim();
                        if (!IsSafeEnglishSupplement(text)) continue;
                        allWords.Add((new OcrWord
                        {
                            Text = w.Text,
                            X = (int)w.BoundingRect.X,
                            Y = (int)w.BoundingRect.Y,
                            Width = (int)w.BoundingRect.Width,
                            Height = (int)w.BoundingRect.Height
                        }, "en"));
                    }
            }

            // 去重合并：如果两个词的位置重叠 >50%，保留文本更长的那个
            var merged = MergeOverlappingWords(allWords);
            var lines = GroupWindowsWords(merged);

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

    private static double MedianOf(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    /// <summary>
    /// 把 Windows OCR 同一视觉行的词拼成 OcrLine。文本走 composer 的脚本感知拼接，
    /// 避免中文逐词之间被 `string.Join(" ")` 插空格；行框取词框并集，供预览覆盖定位。
    /// </summary>
    internal static OcrLine ComposeWindowsLine(List<OcrWord> words)
    {
        var ordered = words.OrderBy(w => w.X).ThenBy(w => w.Y).ToList();
        if (ordered.Count == 0)
            return new OcrLine { Text = "", Words = [] };
        double rowHeight = ordered.Select(w => (double)w.Height).OrderBy(h => h).ElementAt(ordered.Count / 2);
        string text = OcrLineComposer.JoinWords(ordered, rowHeight);

        int x1 = ordered.Min(w => w.X);
        int y1 = ordered.Min(w => w.Y);
        int x2 = ordered.Max(w => w.X + w.Width);
        int y2 = ordered.Max(w => w.Y + w.Height);
        return new OcrLine
        {
            Text = text,
            X = x1,
            Y = y1,
            Width = Math.Max(1, x2 - x1),
            Height = Math.Max(1, y2 - y1),
            Words = ordered,
        };
    }

    /// <summary>
    /// 把 Windows OCR 的词按视觉行分组。固定 8px 对 1700px 受控图已经不够：
    /// 同一视觉行里单词的 Y 会随基线/字号摆动 10px 以上，结果把句中的 “a” 甩成单独一行。
    /// 按词高中位数给行内基线容差，下限仍保留 8px，避免极小字号被噪声并到相邻行。
    /// </summary>
    internal static List<OcrLine> GroupWindowsWords(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0) return [];
        var ordered = words
            .OrderBy(w => w.Y)
            .ThenBy(w => w.X)
            .ToList();
        // 只用有文字的词估行高；中文标点框高度常只有 4–11px，混进中位数
        // 会把容差压到 8–9px，正好再次把标点甩成单独一行。
        var textWords = ordered.Where(w => w.Text.Any(char.IsLetterOrDigit)).ToList();
        double medianWordH = MedianOf((textWords.Count > 0 ? textWords : ordered).Select(w => (double)w.Height));
        // 0.75H：中文标点（逗号/句号）常被 Windows OCR 放到基线下方 10–12px，
        // 0.6H 会把它们甩成单独一条“，。”行；0.75H 仍低于正常行距（通常 ≥1.5H）。
        int lineGap = (int)Math.Max(8, medianWordH * 0.75);

        var lines = new List<OcrLine>();
        var currentLine = new List<OcrWord>();
        var rows = new List<List<OcrWord>>();
        foreach (var word in ordered)
        {
            if (currentLine.Count > 0)
            {
                double rowY = MedianOf(currentLine.Select(w => (double)w.Y));
                if (Math.Abs(word.Y - rowY) > lineGap)
                {
                    rows.Add(currentLine);
                    currentLine = [];
                }
            }
            currentLine.Add(word);
        }
        if (currentLine.Count > 0)
            rows.Add(currentLine);

        // 同一视觉行里可能并排两个独立控件（中文设置页底行就是
        // “0 设置已保存” + 极右侧“区域截图”，中间空 792px）。只按 Y 分行
        // 会把它们拼成一句；再按水平大间隙拆成视觉段。
        foreach (var row in rows)
            foreach (var segment in SplitWindowsSegments(row))
                lines.Add(ComposeWindowsLine(segment));
        return lines;
    }

    /// <summary>按水平大间隙拆 Windows 同一视觉行；阈值与 composer 的视觉段一致。</summary>
    private static List<List<OcrWord>> SplitWindowsSegments(List<OcrWord> words)
    {
        var ordered = words.OrderBy(w => w.X).ThenBy(w => w.Y).ToList();
        if (ordered.Count == 0) return [];
        double medianH = MedianOf(ordered.Select(w => (double)w.Height));
        // 不用 composer 的 2.5H/40px：受控段落里 “and ends ... a clear” 本来就是
        // 同一句，119px 词距仍应保留；只有 240px/8H 以上的横向空洞才视为两个控件。
        double threshold = Math.Max(240, medianH * 8);
        var segments = new List<List<OcrWord>>();
        var current = new List<OcrWord>();
        foreach (var word in ordered)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                double gap = word.X - (last.X + last.Width);
                if (gap > threshold)
                {
                    segments.Add(current);
                    current = [];
                }
            }
            current.Add(word);
        }
        if (current.Count > 0) segments.Add(current);
        return segments;
    }

    /// <summary>en-US 引擎补充词只接受纯 ASCII 文本；中文/乱码碎片一律不进混合结果。</summary>
    private static bool IsSafeEnglishSupplement(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || char.IsWhiteSpace(c)) continue;
            if (" -_./:%()[]{}'\"&+".Contains(c)) continue;
            return false;
        }
        return true;
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
            string bestSource = words[i].Source;
            used[i] = true;

            for (int j = i + 1; j < words.Count; j++)
            {
                if (used[j]) continue;
                // 同一引擎的词已经是独立词框，不能再按重叠去重；否则中文标点
                //（如“外观，”里的逗号）会被前一个汉字吞掉。
                if (words[j].Source == words[i].Source) continue;
                if (OverlapRatio(best, words[j].Word) > 0.5f)
                {
                    used[j] = true;
                    // 中文引擎为主：重叠时 en-US 不能覆盖 zh 的词（HDR → I-IDR 就是这么坏的）。
                    if (bestSource == "zh" && words[j].Source == "en") continue;
                    if (bestSource == "en" && words[j].Source == "zh")
                    {
                        best = words[j].Word;
                        bestSource = "zh";
                        continue;
                    }
                    // 只有文本更长的候选才替换；同一来源已在上面的 continue 排除。
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
}

public class OcrResult
{
    public string Text { get; set; } = "";
    public string? Error { get; set; }
    public string? Mode { get; set; }  // 预处理模式
    public List<OcrLine> Lines { get; set; } = [];
    /// <summary>兜底来源引擎 (2026-08-25: ONNX 无结果时 fallback Windows OCR, 记录原引擎类型)。</summary>
    public string? FallbackFrom { get; set; }
}

public class OcrLine
{
    public string Text { get; set; } = "";
    public List<OcrWord> Words { get; set; } = [];
    /// <summary>行级原图边界框（回退用，当 Words 为空时使用）。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class OcrWord
{
    public string Text { get; set; } = "";
    public int X, Y, Width, Height;
}
