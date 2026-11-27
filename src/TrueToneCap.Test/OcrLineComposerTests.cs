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
        Normalize_AsciiQuotes(check);
        Kana_NoSpaces(check);
        JapaneseBrackets_Normalize(check);
        HalfwidthKana_NoSpaces(check);
        CompatibilityIdeograph_NoSpaces(check);
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

        var punctuationThenLatin = new[]
        {
            Word("中文，", 0, 0, 45, 20),
            Word("English", 55, 0, 60, 20),
        };
        string mixed = OcrLineComposer.JoinWords(punctuationThenLatin, 20);
        check("中文标点后接英文：不额外插空格", mixed == "中文，English", mixed);
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
        check("全角分号后空格消除",
            OcrLineComposer.NormalizeFragment("像素； 未安装") == "像素；未安装",
            OcrLineComposer.NormalizeFragment("像素； 未安装"));
        check("全角标点后接英文也不插空格",
            OcrLineComposer.NormalizeFragment("中文， English") == "中文，English",
            OcrLineComposer.NormalizeFragment("中文， English"));
        check("英文句号后词距不被误删",
            OcrLineComposer.NormalizeFragment("Hello. World") == "Hello. World",
            OcrLineComposer.NormalizeFragment("Hello. World"));
        check("全角逗号/括号/句号旁空格消除",
            OcrLineComposer.NormalizeFragment("中文 ， （测试） 。") == "中文，（测试）。",
            OcrLineComposer.NormalizeFragment("中文 ， （测试） 。"));
        check("全角空格（U+3000）也归一化",
            OcrLineComposer.NormalizeFragment("你\u3000好") == "你好",
            OcrLineComposer.NormalizeFragment("你\u3000好"));
        check("不换行空格（NBSP）也归一化",
            OcrLineComposer.NormalizeFragment("你\u00A0好") == "你好",
            OcrLineComposer.NormalizeFragment("你\u00A0好"));
        check("日文左括号后空格消除",
            OcrLineComposer.NormalizeFragment("「 A") == "「A",
            OcrLineComposer.NormalizeFragment("「 A"));
        check("日文右括号前空格消除",
            OcrLineComposer.NormalizeFragment("A 」") == "A」",
            OcrLineComposer.NormalizeFragment("A 」"));
    }

    private static void Normalize_AsciiQuotes(Action<string, bool, string?> check)
    {
        string quoted = "He said \"hello\" to me";
        check("ASCII 双引号后的词距保留", OcrLineComposer.NormalizeFragment(quoted) == quoted,
            OcrLineComposer.NormalizeFragment(quoted));
        string single = "It's a 'quoted' word";
        check("ASCII 单引号后的词距保留", OcrLineComposer.NormalizeFragment(single) == single,
            OcrLineComposer.NormalizeFragment(single));
        check("ASCII 双引号内侧空格消除",
            OcrLineComposer.NormalizeFragment("\" hello \"") == "\"hello\"",
            OcrLineComposer.NormalizeFragment("\" hello \""));
        check("ASCII 单引号内侧空格消除",
            OcrLineComposer.NormalizeFragment("She said ' goodbye ' now") == "She said 'goodbye' now",
            OcrLineComposer.NormalizeFragment("She said ' goodbye ' now"));
        check("ASCII 单引号撇号不误判",
            OcrLineComposer.NormalizeFragment("It's O'Reilly's students' work") == "It's O'Reilly's students' work",
            OcrLineComposer.NormalizeFragment("It's O'Reilly's students' work"));
        check("英寸符号后的词距保留",
            OcrLineComposer.NormalizeFragment("5\" display") == "5\" display",
            OcrLineComposer.NormalizeFragment("5\" display"));
        check("闭引号后紧贴英文词也补回词距",
            OcrLineComposer.NormalizeFragment("He said \"hello\"to me") == "He said \"hello\" to me",
            OcrLineComposer.NormalizeFragment("He said \"hello\"to me"));
        check("词尾闭引号紧贴下一词也补回词距",
            OcrLineComposer.NormalizeFragment("hello\"to") == "hello\" to",
            OcrLineComposer.NormalizeFragment("hello\"to"));
        check("单引号闭引号后紧贴英文词也补回词距",
            OcrLineComposer.NormalizeFragment("She said 'hello'to me") == "She said 'hello' to me",
            OcrLineComposer.NormalizeFragment("She said 'hello'to me"));
        check("中文引号/省略号旁空格消除",
            OcrLineComposer.NormalizeFragment("“ 你好 ” 世界… 再见") == "“你好”世界…再见",
            OcrLineComposer.NormalizeFragment("“ 你好 ” 世界… 再见"));
    }

    private static void Kana_NoSpaces(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("こ", 0, 0, 18, 20),
            Word("れ", 26, 0, 18, 20),   // gap 8 = 0.4H，英文会插空格，假名不应插
            Word("は", 52, 0, 18, 20),
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("平假名：0.4H 词距也不插空格", text == "これは", text);
    }

    private static void JapaneseBrackets_Normalize(Action<string, bool, string?> check)
    {
        string text = OcrLineComposer.NormalizeFragment("「 これ 」");
        check("日文括号：内侧空格消除", text == "「これ」", text);
    }

    private static void HalfwidthKana_NoSpaces(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("ｺ", 0, 0, 18, 20),
            Word("ﾚ", 26, 0, 18, 20),   // gap 8 = 0.4H
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("半角片假名：0.4H 词距也不插空格", text == "ｺﾚ", text);
    }

    private static void CompatibilityIdeograph_NoSpaces(Action<string, bool, string?> check)
    {
        var words = new[]
        {
            Word("\uF900", 0, 0, 18, 20),   // 﨑（CJK 兼容表意文字）
            Word("野", 26, 0, 18, 20),      // gap 8 = 0.4H
        };
        string text = OcrLineComposer.JoinWords(words, 20);
        check("兼容汉字：0.4H 词距也不插空格", text == "\uF900野", text);
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
