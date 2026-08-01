// TrueToneCap.Core/Services/LlmJsonContext.cs
// System.Text.Json 源生成器上下文 — LLM 翻译请求体的 AOT 兼容序列化

using System.Text.Json.Serialization;

namespace TrueToneCap.Core.Services;

/// <summary>LLM API 聊天请求体。</summary>
internal sealed record LlmChatRequest(
    string Model,
    LlmChatMessage[] Messages,
    double Temperature,
    [property: JsonPropertyName("max_tokens")] int MaxTokens
);

/// <summary>LLM API 聊天消息。</summary>
internal sealed record LlmChatMessage(string Role, string Content);

/// <summary>JSON 序列化源生成器上下文 — 为 LLM 请求体生成编译时序列化代码。</summary>
[JsonSerializable(typeof(LlmChatRequest))]
internal partial class LlmJsonContext : JsonSerializerContext
{
}