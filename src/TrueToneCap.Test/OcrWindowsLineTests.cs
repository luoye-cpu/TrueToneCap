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

    private static void Empty_Safe(Action<string, bool, string?> check)
    {
        var line = OcrService.ComposeWindowsLine([]);
        check("Windows 混合行：空词列表安全", line.Text.Length == 0 && line.Words.Count == 0, line.Text);
    }
}
