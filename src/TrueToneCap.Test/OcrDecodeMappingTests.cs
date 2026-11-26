// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/OcrDecodeMappingTests.cs
// CTC 类别索引 → 文本的纯函数测试。重点锁住模型尾随空格类（dict.Length+1），
// 它决定英文词距能否保留；汉字间空格由 OcrLineComposer.NormalizeFragment 清理。

using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

internal static class OcrDecodeMappingTests
{
    public static void Run(Action<string, bool, string?> check)
    {
        Mapping_LatinWithTrailingSpace(check);
        Mapping_TrailingSpaceOnly(check);
        Mapping_BlankAndOutOfRangeDropped(check);
        Mapping_NullDictEmpty(check);
        Mapping_CjkAndSpace(check);
    }

    private static void Mapping_LatinWithTrailingSpace(Action<string, bool, string?> check)
    {
        string text = OnnxOcrEngine.MapDecodedIndices([1, 2, 3, 1], ["A", "B"]);
        check("空格类：AB A", text == "AB A", text);
    }

    private static void Mapping_TrailingSpaceOnly(Action<string, bool, string?> check)
    {
        string text = OnnxOcrEngine.MapDecodedIndices([3, 3], ["A", "B"]);
        check("空格类：连续两个空格", text == "  ", $"len={text.Length}");
    }

    private static void Mapping_BlankAndOutOfRangeDropped(Action<string, bool, string?> check)
    {
        string text = OnnxOcrEngine.MapDecodedIndices([0, 4, -1], ["A", "B"]);
        check("blank/越界索引丢弃", text.Length == 0, text);
    }

    private static void Mapping_NullDictEmpty(Action<string, bool, string?> check)
    {
        string text = OnnxOcrEngine.MapDecodedIndices([1, 2], null);
        check("空字典安全", text.Length == 0, text);
    }

    private static void Mapping_CjkAndSpace(Action<string, bool, string?> check)
    {
        string text = OnnxOcrEngine.MapDecodedIndices([1, 2, 3], ["藏", "些"]);
        check("CJK + 空格类保持原样", text == "藏些 ", text);
    }
}
