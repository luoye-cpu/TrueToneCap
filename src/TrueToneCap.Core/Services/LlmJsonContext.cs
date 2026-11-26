// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/LlmJsonContext.cs
// System.Text.Json 源生成器上下文 — LLM 翻译请求体的 AOT 兼容序列化

using System.Text.Json.Serialization;

namespace TrueToneCap.Core.Services;

/// <summary>LLM API 聊天请求体。</summary>
internal sealed record LlmChatRequest(
    string Model,
    LlmChatMessage[] Messages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature,
    [property: JsonPropertyName("max_tokens")] int MaxTokens
);

/// <summary>LLM API 聊天消息。</summary>
internal sealed record LlmChatMessage(string Role, string Content);

/// <summary>LLM 文本批量输入的一行：<c>id</c> 从 1 开始，模型必须按这个编号返回。</summary>
internal sealed record LlmBatchLine(int Id, string Text);

/// <summary>一个段落组：<c>text</c> 是把该组 OCR 行拼成的连续段落，<c>lineIds</c> 是组内各行的 id。
/// <para>段落翻译不是让模型把多条源文合并成一条结果，而是让模型先看到完整段落，
/// 再把译文按同样的 id 对齐回每一行；<c>lines</c> 数组仍用于逐行校验/回填。</para></summary>
internal sealed record LlmParagraphGroup(int Id, string Text, int[] LineIds);

/// <summary>段落批量输入：<c>paragraphs</c> 给模型看连续段落，<c>lines</c> 保留逐行 id 映射。</summary>
internal sealed record LlmParagraphInput(LlmParagraphGroup[] Paragraphs, LlmBatchLine[] Lines);

/// <summary>
/// 多模态消息的内容片段（OpenAI 兼容形态）：一条 user 消息 = 一段文字 + 一张图，
/// 图以 <c>image_url.url</c> 的 <c>data:image/png;base64,...</c> 内联。
/// <para>为什么单开一套类型而不给 <see cref="LlmChatMessage"/> 的 Content 加个可空图片字段：
/// 源生成器对"content 有时是字符串、有时是数组"这种联合类型要 [JsonPolymorphic] 一整套配置，
/// 拆成两个请求体反而各自直白。</para>
/// </summary>
internal sealed record LlmContentPart(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("image_url")] LlmImageUrl? ImageUrl);

/// <summary>多模态片段里的图片地址（本项目只用到 data: 内联形态）。</summary>
internal sealed record LlmImageUrl(string Url);

/// <summary>视觉请求体：content 是片段数组。</summary>
internal sealed record LlmVisionMessage(string Role, LlmContentPart[] Content);

/// <summary>视觉翻译请求体。</summary>
internal sealed record LlmVisionRequest(
    string Model,
    LlmVisionMessage[] Messages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature,
    [property: JsonPropertyName("max_tokens")] int MaxTokens
);

/// <summary>
/// JSON 序列化源生成器上下文 — 为 LLM 请求体生成编译时序列化代码。
/// <para>⚠ <c>PropertyNamingPolicy = CamelCase</c> 不是风格问题而是【能不能用】的问题：
/// System.Text.Json 默认按 CLR 属性名原样写出，源生成也不例外 ⇒ 曾经发出去的是
/// <c>{"Model":...,"Messages":[{"Role":...,"Content":[{"Type":...}]}]}</c>，
/// 而 OpenAI 兼容端点只认小写键。实测判据：
/// <c>CorePipelineTests.Test_VisionTranslationIsWired</c> 直接断言序列化后的字节形状
/// —— 这条链路本机没有可用密钥，发不出去的部分只能这样钉。</para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LlmChatRequest))]
[JsonSerializable(typeof(LlmVisionRequest))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(LlmBatchLine[]))]
[JsonSerializable(typeof(LlmParagraphInput))]
internal partial class LlmJsonContext : JsonSerializerContext
{
}