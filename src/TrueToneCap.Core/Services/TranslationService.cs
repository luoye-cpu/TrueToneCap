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
    //  有道翻译（国内首选，免费、免 Key）
    // ═══════════════════════════════════════

    private static readonly string[] s_youdaoKeys =
    [
        "sr_3(QOHT)L2dx#aaGRZO@'C2x}7w3x",
        "YgyPzGhdNMGTPaqLvyzP",
        "n%A-rKaT5fb[Gy?;N,^v@1i5",
    ];

    private async Task<string?> TryYoudaoAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        // 有道语言代码映射
        string sl = MapToYoudaoLang(sourceLang ?? "auto");
        string tl = MapToYoudaoLang(targetLang);

        string saltBase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        // 固定 User-Agent 哈希（有道用 bv 字段做浏览器校验，固定值即可）
        string bv = "4.6";

        foreach (var key in s_youdaoKeys)
        {
            try
            {
                string salt = saltBase + "0";
                string sign = ComputeMd5("fanyideskweb" + text + salt + key);

                var formData = new Dictionary<string, string>
                {
                    ["i"] = text,
                    ["from"] = sl,
                    ["to"] = tl,
                    ["smartresult"] = "dict",
                    ["client"] = "fanyideskweb",
                    ["salt"] = salt,
                    ["sign"] = sign,
                    ["lts"] = saltBase,
                    ["bv"] = bv,
                    ["doctype"] = "json",
                    ["version"] = "2.1",
                    ["keyfrom"] = "fanyi.web",
                    ["action"] = "FY_BY_REALTlME",
                };

                using var content = new FormUrlEncodedContent(formData);
                using var cts5 = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts5.Token);

                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://fanyi.youdao.com/translate_o?smartresult=dict&smartresult=rule")
                {
                    Content = content
                };
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                request.Headers.Add("Referer", "https://fanyi.youdao.com/");
                request.Headers.Add("Cookie", "OUTFOX_SEARCH_USER_ID=-1234567890@127.0.0.1");

                var response = await _http.SendAsync(request, linked.Token);

                var json = await response.Content.ReadAsStringAsync(linked.Token);
                var result = ParseYoudaoResponse(json);
                if (result is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Translate] 有道成功 (key idx)");
                    return result;
                }
            }
            catch (TaskCanceledException) { }
            catch (HttpRequestException) { }
            catch (Exception) { }
        }

        System.Diagnostics.Debug.WriteLine("[Translate] 有道所有 key 均失败");
        return null;
    }

    private static string MapToYoudaoLang(string lang) => lang switch
    {
        "auto" => "AUTO",
        "zh-CN" => "zh-CHS",
        "zh-TW" => "zh-CHT",
        "en" => "en",
        "ja" => "ja",
        "ko" => "ko",
        "fr" => "fr",
        "de" => "de",
        "es" => "es",
        "ru" => "ru",
        "pt" => "pt",
        "it" => "it",
        "vi" => "vi",
        "th" => "th",
        "ar" => "ar",
        _ => lang, // 直接透传其他语言码
    };

    private static string? ParseYoudaoResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("errorCode", out var ec) && ec.GetInt32() != 0)
                return null;

            if (root.TryGetProperty("translateResult", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                var first = results[0];
                if (first.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in first.EnumerateArray())
                    {
                        if (item.TryGetProperty("tgt", out var t))
                            sb.Append(t.GetString());
                    }
                    var result = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(result)) return result;
                }
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

        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = $"Translate from {sl} to {targetLang}:\n\n{text}" }
            },
            temperature = 0.3,
            max_tokens = 4096
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody)
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
