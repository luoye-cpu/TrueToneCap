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

    private static void Empty_Safe(Action<string, bool, string?> check)
    {
        var line = OcrService.ComposeWindowsLine([]);
        check("Windows 混合行：空词列表安全", line.Text.Length == 0 && line.Words.Count == 0, line.Text);
    }
}
