// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/LlmModelPolicy.cs
// LLM 模型预设的默认值、旧名迁移与请求参数兼容策略。
//
// 这里刻意不保存“平台清单”：真正生效的供应商/模型下拉在 MainWindow.xaml，
// 端点映射在 MainWindow.OcrSettings.cs。本文件只处理跨版本必须保持一致的两件事：
//   1. 旧设置里的模型名迁移到 2026-11 预设中的模型 ID；
//   2. 对不接受 temperature 的模型省略该字段，避免新预设一上来就 400。

namespace TrueToneCap.Core.Services;

public static class LlmModelPolicy
{
    /// <summary>2026-11 的默认文本/视觉模型。DeepSeek V4.1 Flash 原生支持图片输入。</summary>
    public const string DefaultModel = "deepseek-flash";

    /// <summary>翻译链路使用的低随机性温度；仅对接受该参数的模型发送。</summary>
    public const double TranslationTemperature = 0.1;

    /// <summary>
    /// 旧模型名 → 2026-11 预设模型 ID。查找不区分大小写；未命中的模型原样保留，
    /// 因为用户可能正在使用一个尚未收录的兼容端点，不能擅自改写。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-chat"] = "deepseek-flash",
        ["deepseek-v4-flash"] = "deepseek-flash",
        ["deepseek-v4-flash-vision-exp"] = "deepseek-flash",
        ["glm-4.7-flash"] = "glm-5.3-flash",
        ["glm-4.7-flashx"] = "glm-5.3-flash",
        ["glm-4-flash"] = "glm-5.3-flash",
        ["gemini-2.0-flash"] = "gemini-3.8-flash",
        ["gemini-2.5-flash"] = "gemini-3.8-flash",
        ["gemini-2.5-pro"] = "gemini-4-argon",
        ["gpt-4o-mini"] = "gpt-5.4-mini",
        ["gpt-4.1-mini"] = "gpt-5.4-mini",
        ["gpt-4.1-nano"] = "gpt-5.4-nano",
        ["moonshot-v1-8k"] = "kimi-k3.1-pro",
        ["qwen-turbo"] = "qwen3.8-flash",
        ["qwen-plus"] = "qwen3.8-flash",
        ["Qwen/Qwen2.5-72B-Instruct"] = "Qwen/Qwen3.8-27B",
        ["deepseek-ai/DeepSeek-V3"] = "deepseek-ai/DeepSeek-V4.1-Flash",
        ["deepseek-ai/DeepSeek-R1"] = "deepseek-ai/DeepSeek-V4.1-Flash",
        ["kimi-k2.5"] = "kimi-k3.1-pro",
        ["kimi-k2.6"] = "kimi-k3.1-pro",
        ["kimi-k2.7-code"] = "kimi-k3.1-pro",
        ["MiniMax-M2.5"] = "MiniMax-M3",
        ["MiniMax-M2.5-highspeed"] = "MiniMax-M3",
        ["grok-4.5"] = "grok-4.7",
    };

    /// <summary>
    /// 已知预设端点的主机名。旧模型名迁移只应作用于这些平台；用户自建网关
    /// （例如仍在提供 deepseek-chat 或 gpt-4o-mini 的兼容层）必须原样保留，
    /// 否则设置加载会把本来可用的自定义配置静默改成预设模型。
    /// </summary>
    private static readonly HashSet<string> KnownPresetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.deepseek.com",
        "open.bigmodel.cn",
        "dashscope.aliyuncs.com",
        "api.siliconflow.cn",
        "api.siliconflow.com",
        "api.moonshot.cn",
        "api.moonshot.ai",
        "ark.cn-beijing.volces.com",
        "api.minimax.cn",
        "api.minimax.io",
        "api.openai.com",
        "api.anthropic.com",
        "generativelanguage.googleapis.com",
        "api.x.ai",
        "api.mistral.ai",
        "openrouter.ai",
        "opencode.ai",
        "api.opencode.ai",
    };

    /// <summary>
    /// 设置加载时的模型名归一。空值回落到当前默认模型；旧名仅在空端点或已知预设端点上
    /// 按 <see cref="LegacyModels"/> 迁移；自定义端点上的同名模型必须原样保留。
    /// </summary>
    public static string Normalize(string? model, string? apiEndpoint = null)
    {
        var trimmed = model?.Trim() ?? "";
        if (trimmed.Length == 0) return DefaultModel;
        if (!LegacyModels.TryGetValue(trimmed, out var migrated)) return trimmed;
        return IsPresetEndpoint(apiEndpoint) ? migrated : trimmed;
    }

    /// <summary>空端点沿用旧行为；只有已知预设主机才参与迁移，未知/自建端点一律保留。</summary>
    private static bool IsPresetEndpoint(string? apiEndpoint)
    {
        if (string.IsNullOrWhiteSpace(apiEndpoint)) return true;
        Uri? uri;
        try { uri = new Uri(apiEndpoint.Trim()); }
        catch (UriFormatException) { return false; }
        var host = uri.Host;
        foreach (var preset in KnownPresetHosts)
            if (host.Equals(preset, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + preset, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 返回请求应携带的 temperature；<c>null</c> 表示 JSON 中省略该字段。
    /// <para>Anthropic 的 OpenAI 兼容层明确要求 Claude 4.7+ 省略 temperature（小于 1 会 400）；
    /// OpenAI 的 GPT-5/GPT-6 推理模型也不保证接受该字段。聚合平台会把模型 ID 写成
    /// <c>anthropic/claude-sonnet-5</c> 或 <c>openai/gpt-5.6-terra</c>，因此按最后一段
    /// 模型名判断，不能只看整串前缀。OpenRouter 的两个路由模型不保证下游接受 temperature，
    /// 也一并省略。</para>
    /// </summary>
    public static double? TemperatureFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return TranslationTemperature;
        var trimmed = model.Trim();
        if (trimmed.Equals("openrouter/auto", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("openrouter/free", StringComparison.OrdinalIgnoreCase)) return null;

        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (leaf.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return null;
        if (leaf.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)) return null;
        return TranslationTemperature;
    }
}
