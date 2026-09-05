// TrueToneCap.Core/Services/TranslationService.cs
// 多后端翻译服务：LLM (OpenAI 兼容) → 有道 → Google 自动降级
// 支持: DeepSeek V4 Flash / GLM-4.7-Flash / GPT-4o-mini / GPT-4.1-mini / 任意 OpenAI 兼容端点

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace TrueToneCap.Core.Services;

/// <summary>翻译服务：LLM 优先 → 有道 → Google 自动降级。</summary>
public class TranslationService
{
    private readonly HttpClient _http;
    private readonly LlmConfig _config;

    public TranslationService(LlmConfig config)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _config = config;
    }

    /// <summary>翻译文本。LLM 优先（如已配置），否则有道 → Google 自动降级。</summary>
    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. LLM 优先（已配置端点 + Key）
        if (_config.UseCustomLlm && !string.IsNullOrEmpty(_config.ApiEndpoint))
        {
            try
            {
                return await TranslateWithLlmAsync(text, targetLang, sourceLang, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Translate] LLM 失败，降级到免费后端: {ex.Message}");
            }
        }

        // 2. 有道翻译（国内可用，免费，无需 API Key）
        var youdaoResult = await TryYoudaoAsync(text, targetLang, sourceLang, ct);
        if (youdaoResult is not null)
            return youdaoResult;

        // 3. Google 翻译（海外可用）
        var googleResult = await TryGoogleMultiEndpointAsync(text, targetLang, sourceLang, ct);
        if (googleResult is not null)
            return googleResult;

        // 4. 全部不可用
        throw new TranslationException(
            "所有翻译后端均不可用（可能是网络问题）。\n" +
            "建议：在设置中开启自定义 LLM，填入 DeepSeek / GLM / OpenAI 兼容 API 地址。");
    }

    // ═══════════════════════════════════════
    //  有道翻译（有道智云开放平台官方 API）
    // ═══════════════════════════════════════

    /// <summary>有道智云开放平台 文本翻译 API 端点。</summary>
    private const string YoudaoApiUrl = "https://openapi.youdao.com/api";

    /// <summary>
    /// 有道翻译（需用户在设置中填入开放平台 应用ID/应用密钥）。未配置凭据时直接跳过，
    /// 由调用方降级到 Google。
    /// <para>
    /// ⚠ 历史实现说明：此前使用硬编码的网页端私有签名密钥（逆向 fanyi.youdao.com 所得）
    /// 调用非公开的 translate_o 接口。该做法违反有道服务条款，且密钥一旦轮换功能即静默
    /// 失效，同时把第三方私有密钥硬编码进 Apache-2.0 公开仓库存在合规风险。
    /// 现已改为调用官方开放平台 API，凭据由用户自备并加密存储。
    /// </para>
    /// </summary>
    private async Task<string?> TryYoudaoAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.YoudaoAppKey) ||
            string.IsNullOrWhiteSpace(_config.YoudaoAppSecret))
        {
            System.Diagnostics.Debug.WriteLine("[Translate] 有道未配置凭据，跳过");
            return null;
        }

        string sl = MapToYoudaoLang(sourceLang ?? "auto");
        string tl = MapToYoudaoLang(targetLang);

        // ⚠ 有道智云文本翻译对 q 有长度上限（约 5000 字符，按 UTF-8 计更长）。
        // 超出后接口返回错误码，而本方法的外层会静默降级到 Google —— 用户以为有道生效。
        // 此处显式拒绝并留痕，让降级原因可查。
        const int YoudaoMaxChars = 5000;
        if (text.Length > YoudaoMaxChars)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Translate] 有道: 文本过长 ({text.Length} 字符 > {YoudaoMaxChars})，跳过并降级");
            return null;
        }

        try
        {
            string salt = Guid.NewGuid().ToString("N");
            string curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // v3 签名: SHA256(appKey + input + salt + curtime + appSecret)
            // input 规则: q 长度 ≤ 20 取 q 本身，否则取 前10字符 + q长度 + 后10字符
            string input = text.Length <= 20
                ? text
                : text[..10] + text.Length + text[^10..];
            string sign = ComputeSha256Hex(
                _config.YoudaoAppKey + input + salt + curtime + _config.YoudaoAppSecret);

            var query = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = sl,
                ["to"] = tl,
                ["appKey"] = _config.YoudaoAppKey,
                ["salt"] = salt,
                ["sign"] = sign,
                ["signType"] = "v3",
                ["curtime"] = curtime,
            };
            // 开放平台要求参数做 URL 编码（q 常含中文与换行）
            var qs = string.Join("&", query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var cts8 = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts8.Token);

            using var request = new HttpRequestMessage(HttpMethod.Post, YoudaoApiUrl)
            {
                // ⚠ 必须写全 System.Text.Encoding：本文件命名空间为 TrueToneCap.Core.Services，
                // 但项目内存在 TrueToneCap.Core.Encoding 命名空间，简写 Encoding 会被解析到它。
                Content = new StringContent(qs, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };

            var response = await _http.SendAsync(request, linked.Token);
            var json = await response.Content.ReadAsStringAsync(linked.Token);

            var result = ParseYoudaoResponse(json);
            if (result is not null)
            {
                System.Diagnostics.Debug.WriteLine("[Translate] 有道开放平台成功");
                return result;
            }
            System.Diagnostics.Debug.WriteLine($"[Translate] 有道返回错误: {json}");
        }
        catch (TaskCanceledException) { }
        catch (HttpRequestException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Translate] 有道异常: {ex.Message}");
        }

        return null;
    }

    /// <summary>计算 SHA-256 十六进制摘要（有道 v3 签名用）。</summary>
    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>映射到有道智云开放平台语言代码。开放平台用小写 "auto"（网页端用 "AUTO"）。</summary>
    /// <summary>映射到有道智云开放平台语言代码（开放平台用小写 "auto"，网页端用 "AUTO"）。
    /// <para>
    /// ⚠ 有道只接受固定的语言代码，不接受 "en-US"/"zh-Hans" 这类 BCP-47 区域变体。
    /// 之前 default 分支直接透传原值，导致"目标语言=英语(美国)"时请求被拒（错误码 108/102），
    /// 而翻译链路会静默降级到 Google，用户以为有道已生效。
    /// 现按"取主语言子标签 + 中文特殊处理"归一化。
    /// </para>
    /// </summary>
    private static string MapToYoudaoLang(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "auto";
        // 取 BCP-47 主子标签: "en-US" → "en", "zh-Hans-CN" → "zh"
        var primary = lang.Split('-')[0].ToLowerInvariant();
        // 中文需区分简体/繁体，用区域判断
        if (primary == "zh")
        {
            var upper = lang.ToUpperInvariant();
            if (upper.Contains("TW") || upper.Contains("HK") || upper.Contains("MO") ||
                upper.Contains("HANT") || lang.Contains("繁"))
                return "zh-CHT";
            return "zh-CHS";
        }
        return primary switch
        {
            "auto" => "auto",
            "en" => "en",
            "ja" or "jp" => "ja",
            "ko" or "kr" => "ko",
            "fr" => "fr",
            "de" => "de",
            "es" => "es",
            "ru" => "ru",
            "pt" => "pt",
            "it" => "it",
            "vi" => "vi",
            "th" => "th",
            "ar" => "ar",
            _ => primary, // 主子标签兜底（有道支持的其它代码多为两字母）
        };
    }

    /// <summary>解析有道智云开放平台响应。
    /// 成功: <c>{"errorCode":"0", "translation":["译文"], "query":"原文", ...}</c>
    /// 失败: <c>{"errorCode":"108", ...}</c>（108=无效应用ID，202=签名错误 等）。</summary>
    private static string? ParseYoudaoResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // errorCode 在开放平台返回的是字符串（如 "0"），兼容数值形态
            if (!root.TryGetProperty("errorCode", out var ec))
                return null;
            var code = ec.ValueKind == JsonValueKind.String ? ec.GetString() : ec.ToString();
            if (code != "0")
            {
                System.Diagnostics.Debug.WriteLine($"[Translate] 有道错误码: {code}");
                return null;
            }

            if (root.TryGetProperty("translation", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                var sb = new StringBuilder();
                foreach (var item in results.EnumerateArray())
                    sb.Append(item.GetString());
                var result = sb.ToString();
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string ComputeMd5(string input)
    {
        byte[] hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(32);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ═══════════════════════════════════════
    //  Google 多端点尝试
    // ═══════════════════════════════════════

    private async Task<string?> TryGoogleMultiEndpointAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        string sl = sourceLang ?? "auto";
        string encoded = HttpUtility.UrlEncode(text);

        (string url, string label)[] endpoints =
        [
            ($"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={targetLang}&dt=t&q={encoded}",
             "Google (gtx)"),
            ($"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={sl}&tl={targetLang}&q={encoded}",
             "Google (chrome-ex)"),
        ];

        foreach (var (url, label) in endpoints)
        {
            try
            {
                using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quickCts.Token);
                var response = await _http.GetStringAsync(url, linked.Token);
                var result = ParseGoogleResponse(response);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    System.Diagnostics.Debug.WriteLine($"[Translate] 成功: {label}");
                    return result;
                }
            }
            catch (TaskCanceledException) { }
            catch (HttpRequestException) { }
            catch (Exception) { }
        }

        return null;
    }

    private static string? ParseGoogleResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var sentences = root[0];
                if (sentences.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var s in sentences.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
                        {
                            var t = s[0].GetString();
                            if (!string.IsNullOrWhiteSpace(t)) sb.Append(t);
                        }
                    }
                    var r = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(r)) return r;
                }
            }

            if (root.TryGetProperty("sentences", out var s2))
            {
                var sb = new StringBuilder();
                foreach (var s in s2.EnumerateArray())
                {
                    if (s.TryGetProperty("trans", out var t))
                        sb.Append(t.GetString());
                }
                var r = sb.ToString();
                if (!string.IsNullOrWhiteSpace(r)) return r;
            }
        }
        catch (JsonException) { }
        return null;
    }

    // ═══════════════════════════════════════
    //  LLM 翻译 (OpenAI 兼容 API — 支持 DeepSeek/GLM/GPT 等)
    // ═══════════════════════════════════════

    private async Task<string> TranslateWithLlmAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        string sl = sourceLang ?? "auto-detect";
        string systemPrompt = !string.IsNullOrWhiteSpace(_config.SystemPrompt)
            ? _config.SystemPrompt
            : $"You are a professional translator. Translate the following text to {targetLang}. Only output the translation, no explanations.";

        string model = !string.IsNullOrWhiteSpace(_config.ModelName)
            ? _config.ModelName
            : "deepseek-chat";

        // 构建端点 URL（确保以 /chat/completions 结尾）
        string endpoint = _config.ApiEndpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions"))
            endpoint += "/chat/completions";

        // 使用源生成器构建请求体（AOT 兼容，替代匿名类型 + JsonContent.Create）
        var requestBody = new LlmChatRequest(
            Model: model,
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", $"Translate from {sl} to {targetLang}:\n\n{text}")
            ],
            Temperature: 0.3,
            MaxTokens: 4096
        );

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody, LlmJsonContext.Default.LlmChatRequest)
        };
        if (!string.IsNullOrEmpty(_config.ApiKey))
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content?.Trim() ?? text;
    }
}

/// <summary>翻译异常。</summary>
public class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

/// <summary>LLM API 配置（支持 OpenAI 兼容端点）。</summary>
public class LlmConfig
{
    public bool UseCustomLlm { get; set; }
    public string ApiEndpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = "deepseek-chat";
    public string SystemPrompt { get; set; } = "";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";

    /// <summary>有道智云开放平台 应用ID（AppKey）。为空则跳过有道后端，降级到 Google。</summary>
    public string YoudaoAppKey { get; set; } = "";

    /// <summary>有道智云开放平台 应用密钥（AppSecret，仅用于本地计算签名）。</summary>
    public string YoudaoAppSecret { get; set; } = "";
}

/// <summary>预置 LLM 提供商端点（供 UI 下拉选择）— 2026-07 更新。</summary>
public static class LlmProviders
{
    public record ProviderInfo(string Name, string Endpoint, string DefaultModel, string PlaceholderKey);

    public static readonly ProviderInfo[] All =
    [
        new("OpenRouter (免费自动路由)", "https://openrouter.ai/api/v1", "openrouter/auto-beta", "sk-or-..."),
        new("硅基流动", "https://api.siliconflow.cn/v1", "tencent/Hunyuan-MT-7B", "sk-..."),
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash", "sk-..."),
        new("DeepSeek (Pro)", "https://api.deepseek.com/v1", "deepseek-v4-pro", "sk-..."),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash", "your-api-key"),
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash", "AIza..."),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini", "sk-..."),
        new("阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo", "sk-..."),
        new("Moonshot", "https://api.moonshot.cn/v1", "moonshot-v1-8k", "sk-..."),
        new("Anthropic Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-20250514", "sk-ant-..."),
        new("自定义", "", "", ""),
    ];

    /// <summary>所有可用模型名称（供 UI 下拉）— 2026-07 更新。</summary>
    public static readonly (string Tag, string Label)[] Models =
    [
        // OpenRouter 自动路由 (免费，按实际模型计费，无额外费用)
        ("openrouter/auto-beta", "Auto Router (OpenRouter 免费路由)"),
        // OpenRouter 免费模型
        ("meta-llama/llama-4-scout:free", "Llama 4 Scout (免费)"),
        ("google/gemini-2.5-flash-preview:free", "Gemini 2.5 Flash (免费)"),
        ("deepseek/deepseek-v4-flash:free", "DeepSeek V4 Flash (免费)"),
        // 硅基流动 (聚合平台, 有免费额度)
        ("tencent/Hunyuan-MT-7B", "混元翻译 MT-7B (硅基流动·免费·33语言)"),
        ("deepseek-ai/DeepSeek-V3", "DeepSeek V3 (硅基流动)"),
        ("deepseek-ai/DeepSeek-R1", "DeepSeek R1 (硅基流动)"),
        ("Qwen/Qwen2.5-72B-Instruct", "Qwen2.5-72B (硅基流动)"),
        ("Qwen/Qwen3-235B-A22B", "Qwen3-235B (硅基流动)"),
        // DeepSeek 官方 (1M 上下文, 支持思考模式)
        ("deepseek-v4-flash", "DeepSeek V4 Flash"),
        ("deepseek-v4-pro", "DeepSeek V4 Pro"),
        // 智谱 GLM
        ("glm-4.7-flash", "GLM-4.7 Flash (智谱)"),
        ("glm-4-flash", "GLM-4 Flash (智谱)"),
        // Google Gemini (OpenAI 兼容端点)
        ("gemini-3.5-flash", "Gemini 3.5 Flash (Google)"),
        ("gemini-3-pro", "Gemini 3 Pro (Google)"),
        ("gemini-2.5-flash", "Gemini 2.5 Flash (Google)"),
        // OpenAI
        ("gpt-4.1-mini", "GPT-4.1 mini"),
        ("gpt-4.1-nano", "GPT-4.1 nano"),
        ("gpt-4o-mini", "GPT-4o mini"),
        // 阿里云百炼
        ("qwen-turbo", "Qwen Turbo (阿里)"),
        ("qwen-plus", "Qwen Plus (阿里)"),
        ("qwen-max", "Qwen Max (阿里)"),
        // Moonshot
        ("moonshot-v1-8k", "Moonshot V1 8K"),
        ("moonshot-v1-128k", "Moonshot V1 128K"),
        // Anthropic Claude
        ("claude-sonnet-4-20250514", "Claude Sonnet 4"),
        ("claude-3-5-haiku-20241022", "Claude 3.5 Haiku"),
        // 自定义
        ("custom", "自定义模型"),
    ];
}
