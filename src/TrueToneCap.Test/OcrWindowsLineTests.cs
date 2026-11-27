// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/OcrWindowsLineTests.cs
// Windows OCR 混合语言路径的行拼接：中文逐词不插空格，行框取词框并集。

using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

internal static class OcrWindowsLineTests
{
    public static void Run(Action<string, bool, string?> check)
    {
        CjkWords_NoSpaces(check);
        EnglishWords_KeepGap(check);
        BaselineVariation_SameLine(check);
        PunctuationBaseline_SameLine(check);
        LargeHorizontalGap_SplitsSegments(check);
        Empty_Safe(check);
    }

    private static void CjkWords_NoSpaces(Action<string, bool, string?> check)
    {
        var words = new List<OcrWord>
        {
            new() { Text = "藏", X = 0, Y = 0, Width = 18, Height = 20 },
            new() { Text = "些", X = 18, Y = 0, Width = 18, Height = 20 },
            new() { Text = "检测", X = 36, Y = 0, Width = 36, Height = 20 },
        };
        var line = OcrService.ComposeWindowsLine(words);
        check("Windows 混合行：中文逐词不插空格", line.Text == "藏些检测", line.Text);
        check("Windows 混合行：行框为词框并集",
            line.X == 0 && line.Y == 0 && line.Width == 72 && line.Height == 20,
            $"{line.X},{line.Y},{line.Width},{line.Height}");
    }

    private static void EnglishWords_KeepGap(Action<string, bool, string?> check)
    {
        var words = new List<OcrWord>
        {
            new() { Text = "This", X = 0, Y = 0, Width = 40, Height = 20 },
            new() { Text = "is", X = 48, Y = 0, Width = 20, Height = 20 },
        };
        var line = OcrService.ComposeWindowsLine(words);
        check("Windows 混合行：英文词距保留", line.Text == "This is", line.Text);
    }

    private static void BaselineVariation_SameLine(Action<string, bool, string?> check)
    {
        // 复刻受控图上的真实失败：同一行末尾的 “a” 比其余词低 11px，
        // 旧固定 8px 会把它甩成第四行；这里必须仍是两行（第二行是下一段）。
        var words = new List<OcrWord>
        {
            new() { Text = "and", X = 60, Y = 235, Width = 73, Height = 36 },
            new() { Text = "ends", X = 152, Y = 235, Width = 97, Height = 36 },
            new() { Text = "a", X = 368, Y = 246, Width = 19, Height = 25 },
            new() { Text = "clear", X = 405, Y = 235, Width = 98, Height = 36 },
            new() { Text = "conclusion.", X = 518, Y = 235, Width = 231, Height = 36 },
            new() { Text = "Next", X = 60, Y = 300, Width = 73, Height = 30 },
        };
        var lines = OcrService.GroupWindowsWords(words);
        check("Windows 混合行：基线差 11px 不另起一行", lines.Count == 2, $"count={lines.Count}");
        check("Windows 混合行：基线偏移的 a 留在原行",
            lines.Count == 2 && lines[0].Text.Contains(" a ", StringComparison.Ordinal),
            lines.Count == 2 ? lines[0].Text : string.Join(" | ", lines.Select(l => l.Text)));
    }

    private static void PunctuationBaseline_SameLine(Action<string, bool, string?> check)
    {
        // 中文设置页实测：逗号/句号被 Windows OCR 放到基线下 12px，
        // 0.6H 会把它们甩成单独的“，。”行；0.75H 必须并回原行。
        var words = new List<OcrWord>
        {
            new() { Text = "外观", X = 0, Y = 0, Width = 40, Height = 20 },
            new() { Text = "，", X = 40, Y = 12, Width = 10, Height = 4 },
            new() { Text = "不", X = 50, Y = 0, Width = 20, Height = 20 },
            new() { Text = "。", X = 70, Y = 12, Width = 10, Height = 4 },
        };
        var lines = OcrService.GroupWindowsWords(words);
        check("Windows 混合行：基线下 12px 的中文标点并回原行", lines.Count == 1, $"count={lines.Count}");
        check("Windows 混合行：中文标点顺序保留",
            lines.Count == 1 && lines[0].Text.Contains("外观，不。", StringComparison.Ordinal),
            lines.Count == 1 ? lines[0].Text : string.Join(" | ", lines.Select(l => l.Text)));
    }

    private static void LargeHorizontalGap_SplitsSegments(Action<string, bool, string?> check)
    {
        // 复刻 settings-cn 底行：左侧 “0 设置已保存”，右侧 792px 外 “区域截图”。
        // 只按 Y 分行会把它们拼成“0 设置已保存区域截图”；必须拆成两个视觉段。
        var words = new List<OcrWord>
        {
            new() { Text = "0", X = 29, Y = 776, Width = 17, Height = 17 },
            new() { Text = "设", X = 55, Y = 776, Width = 18, Height = 16 },
            new() { Text = "置", X = 74, Y = 776, Width = 18, Height = 15 },
            new() { Text = "已", X = 93, Y = 776, Width = 16, Height = 15 },
            new() { Text = "保", X = 109, Y = 775, Width = 18, Height = 17 },
            new() { Text = "存", X = 128, Y = 775, Width = 18, Height = 16 },
            new() { Text = "区", X = 938, Y = 775, Width = 18, Height = 17 },
            new() { Text = "域", X = 957, Y = 774, Width = 21, Height = 19 },
            new() { Text = "截", X = 979, Y = 774, Width = 21, Height = 19 },
            new() { Text = "图", X = 1000, Y = 774, Width = 19, Height = 19 },
        };
        var lines = OcrService.GroupWindowsWords(words);
        check("Windows 混合行：大水平间隙拆成两个视觉段", lines.Count == 2, $"count={lines.Count}");
        check("Windows 混合行：左侧段不吞右侧控件",
            lines.Count == 2 && lines[0].Text.Contains("设置已保存", StringComparison.Ordinal)
                && lines[1].Text.Contains("区域截图", StringComparison.Ordinal),
            string.Join(" | ", lines.Select(l => l.Text)));
    }

    private static void Empty_Safe(Action<string, bool, string?> check)
    {
        var line = OcrService.ComposeWindowsLine([]);
        check("Windows 混合行：空词列表安全", line.Text.Length == 0 && line.Words.Count == 0, line.Text);
    }
}
