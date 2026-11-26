// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/OcrLineComposerTests.cs
// OCR 视觉行合并：脚本感知拼接、跨行/碎片、分栏与 Text 不变式。

using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

internal static class OcrLineComposerTests
{
    public static void Run(Action<string, bool, string?> check)
    {
        CjkChars_NoSpaces(check);
        LatinSplitWord_NoSpace(check);
        LatinWordGap_OneSpace(check);
        CjkPunctuation_NoSpace(check);
        Rows_DoNotMerge(check);
        TallBox_DoesNotSwallowNextRow(check);
        OrphanFragment_MergesIntoRow(check);
        ObviousColumns_Split(check);
        Text_MatchesLines(check);
        EmptyAndSingle_Safe(check);
        Normalize_CjkAndLatinSpaces(check);
    }

    private static void CjkChars_NoSpaces(Action<string, bool, string?> check)
    {
        string[] chars = ["藏", "些", "检", "测", "失", "败", "隐", "检", "测", "了", "一"];
        var words = new List<OcrWord>();
        for (int i = 0; i < chars.Length; i++)
            words.Add(Word(chars[i], i * 18, 0, 18, 20));
        string text = OcrLineComposer.JoinWords(words, 20);
        check("汉字逐字框：不插空格", text == "藏些检测失败隐检测了一", text);
        check("汉字逐字框：CJK 邻接空格计数为 0",
            !System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u4E00-\u9FFF]\s+[\u4E00-\u9FFF]"), text);
    }

    private static void LatinSplitWord_NoSpace(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("En", 0, 0, 20, 20),
            Word("glish", 20, 0, 50, 20),
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("英文拆词：gap=0 直接拼", text == "English", text);
    }

    private static void LatinWordGap_OneSpace(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("This", 0, 0, 40, 20),
            Word("is", 48, 0, 20, 20),
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("英文词距：0.4H 插一个空格", text == "This is", text);
    }

    private static void CjkPunctuation_NoSpace(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("你好", 0, 0, 40, 20),
            Word("，", 40, 0, 20, 20),
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("中文标点：不留空格", text == "你好，", text);
    }

    private static void Rows_DoNotMerge(Action<string, bool, string?> check)
    {
        var lines = new[]
        {
            Line("第一行", 0, 0, 100, 20),
            Line("第二行", 0, 30, 100, 20),
        };
        var result = OcrLineComposer.Compose(lines);
        check("两行不串：输出 2 行", result.Count == 2, $"count={result.Count}");
        check("两行不串：文本保持", result.Count == 2
            && result[0].Text == "第一行" && result[1].Text == "第二行",
            result.Count == 2 ? result[0].Text + " / " + result[1].Text : "");
    }

    private static void TallBox_DoesNotSwallowNextRow(Action<string, bool, string?> check)
    {
        var lines = new[]
        {
            Line("Top", 0, 0, 40, 20),
            Line("Big", 50, 0, 40, 60),
            Line("Bottom", 0, 30, 100, 20),
        };
        var result = OcrLineComposer.Compose(lines);
        check("高框不吞下一行：仍为 2 行", result.Count == 2, $"count={result.Count}");
        check("高框不吞下一行：Bottom 独立",
            result.Count == 2 && result.Any(l => l.Text.Contains("Bottom", StringComparison.Ordinal)),
            string.Join(" | ", result.Select(l => l.Text)));
    }

    private static void OrphanFragment_MergesIntoRow(Action<string, bool, string?> check)
    {
        var lines = new[]
        {
            Line("Hello world", 0, 0, 200, 20),
            Line("a", 210, 2, 10, 16),
        };
        var result = OcrLineComposer.Compose(lines);
        check("孤立碎片：不新增一行", result.Count == 1, $"count={result.Count}");
        check("孤立碎片：并入原行", result.Count == 1
            && result[0].Text.Contains("Hello world", StringComparison.Ordinal)
            && result[0].Text.EndsWith("a", StringComparison.Ordinal),
            result.Count == 1 ? result[0].Text : "");
    }

    private static void ObviousColumns_Split(Action<string, bool, string?> check)
    {
        var lines = new[]
        {
            Line("Left", 0, 0, 40, 20),
            Line("Right", 160, 0, 40, 20),
        };
        var result = OcrLineComposer.Compose(lines);
        check("明显分栏：拆成 2 行", result.Count == 2, $"count={result.Count}");
        check("明显分栏：顺序为 Left→Right",
            result.Count == 2 && result[0].Text == "Left" && result[1].Text == "Right",
            string.Join(" | ", result.Select(l => l.Text)));
    }

    private static void Text_MatchesLines(Action<string, bool, string?> check)
    {
        var lines = new[]
        {
            Line("一 二", 0, 0, 80, 20),
            Line("Hello   world", 0, 30, 120, 20),
        };
        var composed = OcrLineComposer.Compose(lines);
        string text = OcrLineComposer.BuildText(composed);
        check("Text 与 Lines 同序", text == string.Join("\n", composed.Select(l => l.Text)),
            text);
    }

    private static void EmptyAndSingle_Safe(Action<string, bool, string?> check)
    {
        check("空输入不抛异常", OcrLineComposer.Compose([]).Count == 0, null);
        var single = OcrLineComposer.Compose([Line("单框", 0, 0, 50, 20)]);
        check("单框原样保留", single.Count == 1 && single[0].Text == "单框",
            single.Count == 1 ? single[0].Text : $"count={single.Count}");
    }

    private static void Normalize_CjkAndLatinSpaces(Action<string, bool, string?> check)
    {
        check("单片段汉字空格归一化",
            OcrLineComposer.NormalizeFragment("你 好") == "你好",
            OcrLineComposer.NormalizeFragment("你 好"));
        check("英文连续空格压缩",
            OcrLineComposer.NormalizeFragment("Hello   world") == "Hello world",
            OcrLineComposer.NormalizeFragment("Hello   world"));
        check("中文标点前空格消除",
            OcrLineComposer.NormalizeFragment("你好 ，") == "你好，",
            OcrLineComposer.NormalizeFragment("你好 ，"));
    }

    private static OcrLine Line(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        X = x, Y = y, Width = width, Height = height,
        Words = [Word(text, x, y, width, height)],
    };

    private static OcrWord Word(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        X = x, Y = y, Width = width, Height = height,
    };
}
