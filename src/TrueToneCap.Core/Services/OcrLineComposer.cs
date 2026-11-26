// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/OcrLineComposer.cs
// OCR det 框 → 视觉行：脚本感知拼接 + 垂直重叠聚类。
//
// 背景（2026-11-27 实机验收）：
//   1. 旧 MergeDetLines 把同一行的多个 det 框用 " " 连接，中文被拆成逐字框时
//      就得到“藏 些 检 测 失 败”这种人为空格；
//   2. 旧聚类只看“框中心点是否落在簇的垂直范围内”，一个高框会把下一行吞进来，
//      一个贴边小碎片又会掉成独立行；
//   3. OcrResult.Text 原先按原始 det 顺序生成，与合并后的 Lines 不同序。
// 本类只处理“框怎么合、字怎么拼、行怎么排”，不碰 rec 模型/字典。

using System.Text;

namespace TrueToneCap.Core.Services;

internal static class OcrLineComposer
{
    // 同一行判定：垂直重叠至少覆盖较矮框的 60%，且中心差不超过较高框的 60%。
    private const double RowMinOverlapRatio = 0.60;
    private const double RowMaxCenterRatio = 0.60;

    // 明显分栏/独立标签的安全阀：gap > max(2.5×行高, 40px) 才拆段。
    // 常规英文词距约 0.25~0.4×行高；中文相邻框通常 gap < 1×行高。
    private const double SegmentGapRatio = 2.5;
    private const double SegmentGapMinPx = 40;

    // 文本拼接：英文词距阈值 0.25H；中英交界阈值 0.5H。
    private const double LatinSpaceRatio = 0.25;
    private const double MixedSpaceRatio = 0.50;

    // 孤立碎片：≤2 字符、与相邻行垂直重叠 ≥0.5、水平 gap ≤1.5H 才并回。
    private const int OrphanMaxChars = 2;
    private const double OrphanMinOverlapRatio = 0.50;
    private const double OrphanMaxGapRatio = 1.50;

    private readonly record struct Frag(string Text, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public double CenterY => Y + Height / 2.0;
    }

    private sealed class Row
    {
        public List<Frag> Items { get; } = [];
        public double Top { get; set; }
        public double Height { get; set; }
        public double Bottom => Top + Height;
        public double CenterY => Top + Height / 2.0;
    }

    private sealed class LineCandidate
    {
        public string Text { get; set; } = "";
        public List<Frag> Fragments { get; set; } = [];
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>把合并后的视觉行拼成全文；Text 必须与 Lines 同序。</summary>
    internal static string BuildText(IEnumerable<OcrLine> lines)
    {
        var parts = new List<string>();
        foreach (var line in lines)
        {
            string text = NormalizeFragment(line.Text);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    /// <summary>原始 det 行 → 视觉行。输入行可以是一框一行，也可以带 Words。</summary>
    internal static List<OcrLine> Compose(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return [];

        var fragments = Expand(lines);
        if (fragments.Count == 0) return [];

        var rows = BuildRows(fragments);
        var candidates = new List<LineCandidate>();

        foreach (var row in rows.OrderBy(r => r.Top).ThenBy(r => r.CenterY))
        {
            var ordered = row.Items.OrderBy(f => f.X).ThenBy(f => f.Y).ToList();
            if (ordered.Count == 0) continue;

            double medianH = Median(ordered.Select(f => (double)f.Height));
            var segment = new List<Frag>();
            foreach (var frag in ordered)
            {
                if (segment.Count > 0)
                {
                    var prev = segment[^1];
                    double gap = frag.X - prev.Right;
                    double splitAt = Math.Max(SegmentGapRatio * medianH, SegmentGapMinPx);
                    if (gap > splitAt)
                    {
                        AddCandidate(candidates, segment, medianH);
                        segment = [];
                    }
                }
                segment.Add(frag);
            }
            AddCandidate(candidates, segment, medianH);
        }

        MergeOrphans(candidates);
        return candidates
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .Select(ToOcrLine)
            .Where(l => l.Text.Length > 0)
            .ToList();
    }

    /// <summary>测试/单行调用：把一组 OcrWord 按同一视觉行拼成文本。</summary>
    internal static string JoinWords(IReadOnlyList<OcrWord> words, double rowHeight)
    {
        var frags = new List<Frag>(words.Count);
        foreach (var word in words)
        {
            string text = NormalizeFragment(word.Text);
            if (text.Length == 0) continue;
            frags.Add(new Frag(text, word.X, word.Y, word.Width, word.Height));
        }
        return JoinFragments(frags, rowHeight);
    }

    // ═══════════════════════════════════════
    //  碎片展开 / 行聚类
    // ═══════════════════════════════════════

    private static List<Frag> Expand(IReadOnlyList<OcrLine> lines)
    {
        var fragments = new List<Frag>();
        foreach (var line in lines)
        {
            if (line.Words.Count > 0)
            {
                foreach (var word in line.Words)
                {
                    string text = NormalizeFragment(word.Text);
                    if (text.Length == 0) continue;
                    fragments.Add(new Frag(text, word.X, word.Y,
                        Math.Max(1, word.Width), Math.Max(1, word.Height)));
                }
            }
            else
            {
                string text = NormalizeFragment(line.Text);
                if (text.Length == 0) continue;
                fragments.Add(new Frag(text, line.X, line.Y,
                    Math.Max(1, line.Width), Math.Max(1, line.Height)));
            }
        }
        return fragments;
    }

    private static List<Row> BuildRows(List<Frag> fragments)
    {
        // 同 top 时先让小框建立行锚点，避免一个跨两行的高框先把两行并成一行。
        var sorted = fragments
            .OrderBy(f => f.Y)
            .ThenBy(f => f.Height)
            .ThenBy(f => f.X)
            .ToList();

        var rows = new List<Row>();
        foreach (var frag in sorted)
        {
            Row? best = null;
            double bestOverlap = 0;
            foreach (var row in rows)
            {
                double overlap = Math.Min(frag.Bottom, row.Bottom) - Math.Max(frag.Y, row.Top);
                if (overlap <= 0) continue;
                double minH = Math.Min(frag.Height, row.Height);
                if (minH <= 0 || overlap < RowMinOverlapRatio * minH) continue;
                double centerDelta = Math.Abs(frag.CenterY - row.CenterY);
                if (centerDelta > RowMaxCenterRatio * Math.Max(frag.Height, row.Height)) continue;
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = row;
                }
            }

            if (best is null)
            {
                var row = new Row { Top = frag.Y, Height = frag.Height };
                row.Items.Add(frag);
                rows.Add(row);
            }
            else
            {
                best.Items.Add(frag);
                RecomputeAnchor(best);
            }
        }
        return rows;
    }

    private static void RecomputeAnchor(Row row)
    {
        if (row.Items.Count == 0) return;
        row.Top = Median(row.Items.Select(f => (double)f.Y));
        row.Height = Math.Max(1, Median(row.Items.Select(f => (double)f.Height)));
    }

    private static void AddCandidate(List<LineCandidate> output, List<Frag> fragments, double rowHeight)
    {
        if (fragments.Count == 0) return;
        var ordered = fragments.OrderBy(f => f.X).ThenBy(f => f.Y).ToList();
        string text = JoinFragments(ordered, rowHeight);
        if (text.Length == 0) return;

        int x1 = ordered.Min(f => f.X);
        int y1 = ordered.Min(f => f.Y);
        int x2 = ordered.Max(f => f.Right);
        int y2 = ordered.Max(f => f.Bottom);
        output.Add(new LineCandidate
        {
            Text = text,
            Fragments = ordered,
            X = x1,
            Y = y1,
            Width = Math.Max(1, x2 - x1),
            Height = Math.Max(1, y2 - y1),
        });
    }

    // ═══════════════════════════════════════
    //  文本拼接
    // ═══════════════════════════════════════

    private static string JoinFragments(IReadOnlyList<Frag> fragments, double rowHeight)
    {
        if (fragments.Count == 0) return "";
        double h = rowHeight > 0 ? rowHeight : Median(fragments.Select(f => (double)f.Height));
        if (h <= 0) h = 1;

        var sb = new StringBuilder();
        Frag? previous = null;
        foreach (var frag in fragments)
        {
            string text = NormalizeFragment(frag.Text);
            if (text.Length == 0) continue;

            if (sb.Length > 0 && previous is not null)
            {
                char prev = LastChar(sb);
                char next = text[0];
                double gap = Math.Max(0, frag.X - previous.Value.Right);
                if (NeedsSpaceBetween(prev, next, gap, h)) sb.Append(' ');
            }
            sb.Append(text);
            previous = frag;
        }
        return sb.ToString().Trim();
    }

    private static bool NeedsSpaceBetween(char prev, char next, double gap, double rowHeight)
    {
        if (char.IsWhiteSpace(prev) || char.IsWhiteSpace(next)) return false;
        if (IsOpeningPunctuation(prev) || IsClosingPunctuation(next)) return false;
        if (prev is '-' or '‐' or '–' or '/' or '\\' or '@' or '_') return false;

        // 汉字/假名/中文标点之间永不插空格 —— 这是旧实现“逐字空格”的根因。
        if (IsCjkNoSpace(prev) && IsCjkNoSpace(next)) return false;

        // 中英交界：只有真正拉开词距才插空格；紧邻的“详情 s/”不拆。
        if (IsCjkNoSpace(prev) || IsCjkNoSpace(next))
            return gap >= MixedSpaceRatio * rowHeight;

        // 英文/数字之间：gap 小于词距视为 det 把同一个词拆开，直接拼。
        return gap >= LatinSpaceRatio * rowHeight;
    }

    private static char LastChar(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(sb[i])) return sb[i];
        return '\0';
    }

    /// <summary>归一化片段：去换行/首尾空白、压缩空格、消掉汉字之间与中文标点旁的空格。</summary>
    internal static string NormalizeFragment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\t')
            {
                if (sb.Length == 0 || sb[^1] == ' ') continue;
                char prev = sb[^1];
                char? next = NextNonSpace(text, i + 1);

                if (IsOpeningPunctuation(prev)) continue;
                if (next is not null && IsClosingPunctuation(next.Value)) continue;
                if (next is not null && IsCjkNoSpace(prev) && IsCjkNoSpace(next.Value)) continue;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static char? NextNonSpace(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) return text[i];
        return null;
    }

    private static bool IsCjkNoSpace(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\u3040' && c <= '\u30FF')
        || (c >= '\u31F0' && c <= '\u31FF')
        || "，。！？；：、）》】”’…".Contains(c);

    private static bool IsOpeningPunctuation(char c) => "\"'‘“([{（《【".Contains(c);

    private static bool IsClosingPunctuation(char c) => "\"'))]}）》】”’，。！？；：、,.;:!?".Contains(c);

    // ═══════════════════════════════════════
    //  孤立碎片 / 输出
    // ════════════════════��══════════════════

    private static void MergeOrphans(List<LineCandidate> candidates)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                var orphan = candidates[i];
                if (orphan.Text.Length > OrphanMaxChars) continue;

                LineCandidate? target = null;
                double bestGap = double.MaxValue;
                foreach (var other in candidates)
                {
                    if (ReferenceEquals(other, orphan)) continue;
                    double overlap = Math.Min(orphan.Y + orphan.Height, other.Y + other.Height)
                        - Math.Max(orphan.Y, other.Y);
                    double minH = Math.Min(orphan.Height, other.Height);
                    if (minH <= 0 || overlap < OrphanMinOverlapRatio * minH) continue;

                    double gap = HorizontalGap(orphan, other);
                    double maxGap = OrphanMaxGapRatio * Math.Max(1, other.Height);
                    if (gap > maxGap) continue;
                    if (gap < bestGap)
                    {
                        bestGap = gap;
                        target = other;
                    }
                }

                if (target is null) continue;
                MergeInto(target, orphan);
                candidates.RemoveAt(i);
                changed = true;
                break;
            }
        }
    }

    private static double HorizontalGap(LineCandidate a, LineCandidate b)
    {
        if (a.X > b.X + b.Width) return a.X - (b.X + b.Width);
        if (b.X > a.X + a.Width) return b.X - (a.X + a.Width);
        return 0;
    }

    private static void MergeInto(LineCandidate target, LineCandidate orphan)
    {
        var fragments = target.Fragments.Concat(orphan.Fragments)
            .OrderBy(f => f.X).ThenBy(f => f.Y).ToList();
        double h = Math.Max(1, Median(fragments.Select(f => (double)f.Height)));
        target.Text = JoinFragments(fragments, h);
        target.Fragments = fragments;
        target.X = fragments.Min(f => f.X);
        target.Y = fragments.Min(f => f.Y);
        target.Width = Math.Max(1, fragments.Max(f => f.Right) - target.X);
        target.Height = Math.Max(1, fragments.Max(f => f.Bottom) - target.Y);
    }

    private static OcrLine ToOcrLine(LineCandidate candidate)
    {
        var words = candidate.Fragments
            .OrderBy(f => f.X).ThenBy(f => f.Y)
            .Select(f => new OcrWord
            {
                Text = f.Text,
                X = f.X,
                Y = f.Y,
                Width = f.Width,
                Height = f.Height,
            })
            .ToList();
        return new OcrLine
        {
            Text = candidate.Text,
            X = candidate.X,
            Y = candidate.Y,
            Width = candidate.Width,
            Height = candidate.Height,
            Words = words,
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
