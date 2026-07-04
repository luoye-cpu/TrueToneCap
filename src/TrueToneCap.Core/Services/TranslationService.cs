// TrueToneCap.Core/Services/TranslationService.cs
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace TrueToneCap.Core.Services;

/// <summary>翻译服务：多后端自动降级（有道 → Google → LLM），适应不同网络环境。</summary>
public class TranslationService
{
    private readonly HttpClient _http;
    private readonly LlmConfig _config;

    public TranslationService(LlmConfig config)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _config = config;
    }

    /// <summary>翻译文本。LLM 优先（如已配置），否则有道 → Google 自动降级。</summary>
    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. 自定义 LLM 优先
        if (_config.UseCustomLlm && !string.IsNullOrEmpty(_config.ApiEndpoint))
            return await TranslateWithLlmAsync(text, targetLang, sourceLang, ct);

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
            "建议：在设置中开启自定义 LLM，填入 DeepSeek / OpenAI 兼容 API 地址。");
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
                using var cts5 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts5.Token);

                var response = await _http.PostAsync(
                    "https://fanyi.youdao.com/translate_o?smartresult=dict&smartresult=rule",
                    content, linked.Token);

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
    //  LLM 翻译 (OpenAI 兼容 API)
    // ═══════════════════════════════════════

    private async Task<string> TranslateWithLlmAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        string sl = sourceLang ?? "auto-detect";
        string systemPrompt = _config.SystemPrompt
            ?? $"You are a professional translator. Translate the following text to {targetLang}. Only output the translation, no explanations.";

        var requestBody = new
        {
            model = _config.ModelName ?? "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = $"Translate from {sl} to {targetLang}:\n\n{text}" }
            },
            temperature = 0.3,
            max_tokens = 2000
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
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

/// <summary>LLM API 配置</summary>
public class LlmConfig
{
    public bool UseCustomLlm { get; set; }
    public string ApiEndpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = "gpt-4o-mini";
    public string SystemPrompt { get; set; } = "";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
}
