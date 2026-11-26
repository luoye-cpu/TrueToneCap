// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationStatusFormatter.cs
//
// 预览窗左下角状态栏的纯文本口径。抽到 Core 不是为了复用字符串，而是为了让
// “内容疑似未翻译时绝不能打 ✅”变成可单测的行为，而不是只能靠 grep XAML/code-behind。

namespace TrueToneCap.Core.Services;

/// <summary>把硬失败/内容疑似/回退/降级/原因拼成状态栏文案。</summary>
public static class TranslationStatusFormatter
{
    /// <param name="totalLines">OCR 行数（不是译文数组长度：数组短了也要按 OCR 行数告警）。</param>
    /// <param name="failedLines">null→回填原文的硬失败行数。</param>
    /// <param name="contentIssueLines">非空但疑似未翻译/半译的行数（与硬失败不重叠）。</param>
    public static string Build(
        int totalLines,
        int failedLines,
        int contentIssueLines,
        int fallbackCount,
        int llmRetryCount,
        int contentRetryCount,
        int degradedCount,
        string? degradedBackend,
        string? firstError)
    {
        string fallbackNote = fallbackCount > 0
            ? $"；批量未交出 {fallbackCount} 行，已进入回退阶段"
            : "";
        string retryNote = llmRetryCount > 0
            ? $"；{llmRetryCount} 行经同 LLM 重试成功"
            : "";
        string contentRetryNote = contentRetryCount > 0
            ? $"；{contentRetryCount} 行经内容补译恢复"
            : "";
        string degradedNote = degradedCount > 0
            ? $"；其中 {degradedCount} 行由 {degradedBackend ?? "备用翻译引擎"} 跨后端补译" +
              "（风格可能与其余行不一致）"
            : "";
        string why = firstError is null ? "" : $"（原因示例：{firstError}）";
        bool hadExplicitError = firstError is not null;

        if (failedLines == 0 && contentIssueLines == 0)
        {
            return !hadExplicitError && degradedCount == 0
                ? $"✅ 翻译完成（{totalLines} 行）{fallbackNote}{retryNote}{contentRetryNote}，点击文字块可复制译文"
                : $"⚠ 翻译完成（{totalLines} 行），但曾发生错误或降级" +
                  $"{fallbackNote}{retryNote}{contentRetryNote}{degradedNote}{why}，点击文字块可复制译文";
        }

        if (failedLines == totalLines && contentIssueLines == 0)
        {
            return $"❌ 翻译失败：{totalLines}/{totalLines} 行都没出译文 " +
                   $"{fallbackNote}{retryNote}{degradedNote}{why}";
        }

        if (failedLines == 0)
        {
            return $"⚠ 翻译完成（{contentIssueLines}/{totalLines} 行疑似未翻译或不完整）" +
                   $"{fallbackNote}{retryNote}{contentRetryNote}{degradedNote}{why}，点击文字块可复制";
        }

        if (contentIssueLines == 0)
        {
            return $"⚠ 翻译完成（{failedLines}/{totalLines} 行失败，已保留原文）" +
                   $"{fallbackNote}{retryNote}{degradedNote}{why}，点击文字块可复制";
        }

        return $"⚠ 翻译完成（{failedLines}/{totalLines} 行失败，{contentIssueLines}/{totalLines} 行疑似未翻译或不完整）" +
               $"{fallbackNote}{retryNote}{contentRetryNote}{degradedNote}{why}，点击文字块可复制";
    }
}
