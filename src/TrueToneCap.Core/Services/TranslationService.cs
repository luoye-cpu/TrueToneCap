// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationService.cs
// 多后端翻译服务：LLM (OpenAI 兼容) → 有道官方开放平台 → 免费公共端点（Edge / 必应 / MyMemory / Google，默认启用）
// 支持: DeepSeek V4 Flash / GLM-4.7-Flash / GPT-4o-mini / GPT-4.1-mini / 任意 OpenAI 兼容端点

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using TrueToneCap.Core.Logging;   // CoreLog / LogCategory（本文件的日志出口，Release 下也落得到盘）

namespace TrueToneCap.Core.Services;

/// <summary>翻译服务：LLM 优先（如已配置）→ 有道官方开放平台（如已填凭据）→ 免费公共端点（Edge / 必应 / MyMemory / Google，默认启用）。</summary>
public class TranslationService
{
    // ═══ 2026-09-15 修复：HttpClient 改为进程级共享 ═══
    // 原实现在【构造函数】里 new HttpClient(handler)，而调用方
    // （OcrPreviewWindow.xaml.cs 的翻译入口）是每次点击翻译就 new 一个 TranslationService
    // ⇒ 每翻译一次就新建一个 HttpClient 及其连接池，旧的要等 GC + TIME_WAIT 才释放，
    // 反复翻译会持续堆积 TCP 连接，最终可能耗尽端口。这是 .NET 的经典陷阱：
    // HttpClient 的设计意图是"长期存活并复用"，而不是"用完即弃"。
    // 改为静态单例后，DNS 变更由 PooledConnectionLifetime 兜底（连接定期重建）。
    private static readonly HttpClient s_http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),       // 定期重建连接，避免陈旧连接 + DNS 变更不生效
        MaxConnectionsPerServer = 8,                             // 限制单主机并发连接数
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;
    private readonly LlmConfig _config;

    /// <summary>
    /// 最近一次后端失败的**人话**原因（如"有道 errorCode 108：AppID 不存在或输错"）。
    /// <para>为什么要有它：多行并发翻译时逐行容错会把原因吞掉，用户只看到"N/N 行失败"。
    /// 这个字段被 <see cref="TranslateAsync"/> 拼进最终异常，也被 <c>OcrPreviewWindow</c>
    /// 当状态栏的"原因示例"。并发写是 last-writer-wins，不要求精确 —— 它只是诊断提示。</para>
    /// </summary>
    private string? _lastBackendNote;

    /// <summary>最近一次<b>成功</b>是哪个后端交出的译文（失败路径不写，保持上一个值不动）。
    /// <para>为什么需要一个字段而不是只写日志：成功那行是 <c>CoreLog.Debug</c>，而默认详细度是
    /// <c>Brief</c>（Debug 不进盘），于是"这条译文到底是 Edge 还是必应给的"在用户的日志里
    /// 完全查不到 —— 而这条恰恰是排障时最想问的一句（各家限额与失效形态都不一样）。
    /// 端到端用例（<c>ServiceTests.Test_FreeTranslateEndpointsLive</c>）靠它做归因。</para>
    /// <para><c>internal</c> 而不是 public：Test 工程有 <c>InternalsVisibleTo</c>，
    /// 而这只是诊断读数，不该变成产品 API（同 <c>SettingsService.Normalize</c> 的处理）。</para>
    /// </summary>
    private string? _lastBackendServed;
    internal string? LastBackendServed => _lastBackendServed;

    /// <summary>最近一次失败的留痕句。<b>成功时不清空</b> —— 这正是这条链的排障入口：
    /// 降级链会静默吃掉前面每一档的失败，用户与测试都只看到"最后一档成功了"。
    /// 端到端用例要靠它回答"第一档为什么没跑成"（2026-10-23 就是这么逮到
    /// "离线形状判据全绿、而真发请求时 Edge 失败降到必应"的）。</summary>
    internal string? LastAttemptNote => _lastBackendNote;

    /// <summary>成功出口的统一记账：留下"哪个后端交出的"，并按原有的详细度写 Debug 行。</summary>
    private void NoteSuccess(string label)
    {
        _lastBackendServed = label;
        CoreLog.Debug("Translate", $"成功: {label}", LogCategory.Network);
    }

    /// <summary>
    /// 有道智云错误码 → 人话。<b>只收录有据的码</b>，其余原样报码、不猜含义。
    /// <para>101 / 108 是 2026-09-21 本机实发请求验出来的（无凭据 → 101，假 AppID → 108）；
    /// 202 是仓库既有注释里写的"签名错误"。<b>未收录</b>的 102/103/105 等网上流传的解释
    /// 没有实测或官方文档背书，宁缺毋滥 —— 额度耗尽到底是哪个码，也要等拿到真凭据实测后才能写。</para>
    /// </summary>
    internal static string ExplainYoudaoError(string? code) => code switch
    {
        "101" => "有道 errorCode 101：请求没带 AppID（设置 → 翻译 里的「有道 AppID」是空的）",
        "108" => "有道 errorCode 108：AppID 不存在或输错",
        "202" => "有道 errorCode 202：签名校验失败（多为密钥不对，或本机时间偏差过大）",
        _ => string.IsNullOrEmpty(code)
            ? "有道返回里没有 errorCode，无法判定原因"
            : $"有道 errorCode {code}：本程序未收录该码的解释（官方错误码表见 ai.youdao.com 文档）",
    };

    public TranslationService(LlmConfig config)
    {
        _http = s_http;   // 复用共享实例（保留字段以便后续按需替换/测试注入）
        _config = config;
    }

    /// <summary>
    /// 当前配置下是否至少有一条后端<b>可能</b>工作（不看网络通不通，只看有没有配）。
    /// <para>给 UI 在扇出 N 行请求<b>之前</b>用：不先问这一句的话，N 行各自注定失败，
    /// 而逐行容错会把原因全吞掉，用户只看到"N/N 行失败"。</para>
    /// </summary>
    public static bool AnyBackendAvailable(LlmConfig c) =>
        (c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint))
        || (!string.IsNullOrWhiteSpace(c.YoudaoAppKey) && !string.IsNullOrWhiteSpace(c.YoudaoAppSecret))
        || c.AllowUnofficialTranslateEndpoints;

    /// <summary>按当前配置生成"缺什么 / 哪几条路可走"的中文说明，可直接展示。
    /// <para>⚠ 这里出现的每一个【引号内的界面控件名】都必须在界面源码里逐字存在 ——
    /// 判据 <c>Test_FreeEndpointHintNamesARealControl</c> 钉的就是这件事。
    /// 2026-10-23 之前这条写着让用户去勾「允许回退到非官方翻译端点（必应网页版 / Google）」，
    /// 而界面上那条叫「允许回退到 Google 非官方翻译端点」⇒ 用户照着提示找不到那个勾，
    /// 唯一剩下的出路就是去填密钥，"免费档必须填 key"就是这么造出来的。</para>
    /// </summary>
    public static string DescribeBackends(LlmConfig c)
    {
        var on = new List<string>();
        if (c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint)) on.Add("自定义 LLM");
        if (!string.IsNullOrWhiteSpace(c.YoudaoAppKey) && !string.IsNullOrWhiteSpace(c.YoudaoAppSecret)) on.Add("有道官方 API");
        if (c.AllowUnofficialTranslateEndpoints) on.Add("免费公共端点（Edge / 必应 / MyMemory / Google）");

        if (on.Count > 0)
            // ⚠ 这里不再猜原因。原先固定缀一句"多半是网络、额度或接口变更，不是没配置"，
            // 而真实原因（401 密钥错 / 有道 errorCode / 超时）由 _lastBackendNote 附在后面 ——
            // 两句会互相矛盾，用户只会相信先看到的那句。原因统一交给 TranslateAsync 拼。
            return "已启用的后端：" + string.Join("、", on) + " —— 它们都没返回结果。";

        return "还没有任何可用的翻译后端，三条路任选一条：\n"
             + "① 设置 → 翻译：勾选「使用免费公共翻译端点（识别到的文字会发给第三方）」—— 不需要任何密钥，"
             + "依次走 Edge / 必应 / MyMemory / Google 这四家免费接口；\n"
             + "② 设置 → 翻译：填有道智云开放平台的 AppID 与密钥（官方 API；密钥只在本机 DPAPI 加密存储）；\n"
             + "③ 设置 → 翻译：把模式切到 LLM，填 DeepSeek / GLM / OpenAI 兼容端点与密钥。";
    }

    /// <summary>
    /// 把 LLM 后端的异常归成人能照着做的一句话。
    /// <para>分三类的理由：401/403 是<b>配置</b>问题（密钥或模型名），429 是<b>额度</b>问题，
    /// 只有连不上才是<b>网络</b>问题。混成"网络问题"会让用户去查网线，而真正要改的是密钥框。</para>
    /// </summary>
    internal static string ClassifyLlmFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } h =>
            $"自定义 LLM 返回 401（{h.Message}）—— 是 API 密钥不对或该模型未开通，不是网络问题",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } h =>
            $"自定义 LLM 返回 403（{h.Message}）—— 密钥没有访问该模型的权限，不是网络问题",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } h =>
            $"自定义 LLM 返回 429（{h.Message}）—— 额度或频率到顶了",
        HttpRequestException { StatusCode: var code } h when code is not null =>
            $"自定义 LLM 返回 HTTP {(int)code}（{h.Message}）—— 400 多半是模型名填错或该端点不支持图片输入",
        HttpRequestException h =>
            $"自定义 LLM 连不上：{h.Message}",
        TaskCanceledException =>
            "自定义 LLM 超时（连接 10 秒 / 整次请求 15 秒）",
        _ => $"自定义 LLM 失败: {ex.GetType().Name}: {ex.Message}"
    };

    /// <summary>翻译文本。LLM 优先（如已配置）→ 有道官方开放平台 → 非官方端点（必应 / Google，默认关闭）。</summary>
    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 0. 一条后端都没配 ⇒ 这不是"网络问题"，别拿网络文案糊用户
        //    （调用方应在扇出多行之前先问 AnyBackendAvailable，这里兜住直接调用者）
        if (!AnyBackendAvailable(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        // 1. LLM 优先（已配置端点 + Key）
        if (_config.UseCustomLlm && !string.IsNullOrEmpty(_config.ApiEndpoint))
        {
            try
            {
                return await TranslateWithLlmAsync(text, targetLang, sourceLang, ct);
            }
            catch (Exception ex)
            {
                // 不能只写日志：LLM 是唯一没有把原因落进 _lastBackendNote 的后端，
                // 于是"密钥填错(401)"和"网线拔了"在用户那边是同一句话。实测：
                // 配了 DeepSeek 端点 + 无效 key ⇒ 最终提示写成"多半是网络、额度或接口变更，
                // 不是没配置"，而 401 那句真话只在调试输出里闪一下就没了。
                _lastBackendNote = ClassifyLlmFailure(ex);
                CoreLog.Warn("Translate", "LLM 失败，降级到免费后端: " + _lastBackendNote, ex, LogCategory.Network);
            }
        }

        // 2. 有道翻译（官方开放平台 API；需用户自备 AppKey/AppSecret，未填则跳过）
        var youdaoResult = await TryYoudaoAsync(text, targetLang, sourceLang, ct);
        if (youdaoResult is not null)
            return youdaoResult;

        // 3. 免费公共端点（默认启用；用户可在 设置 → 翻译 里关掉）。
        //    排序按 2026-10-23 本机（中国大陆网络）实测：
        //      Edge 468–577 ms / 免凭据 / 单条 4 万字符仍可用  ⇒ 放最前
        //      必应 1.06–1.20 s / 要先抓前端凭据、约 1 h 过期   ⇒ 第二
        //      MyMemory 0.81–1.41 s / 免凭据但单条 500 字符、官方明写 5000 字符/天 ⇒ 兜底
        //      Google 本机直接超时（两个口各挂 5 s）              ⇒ 垫底，保留只为海外机器
        //    反序的代价是每一行都要白等前面那几条的超时，多行并发时按行数翻倍。
        var edgeResult = await TryEdgeFreeEndpointAsync(text, targetLang, sourceLang, ct);
        if (edgeResult is not null)
            return edgeResult;

        var bingResult = await TryBingWebEndpointAsync(text, targetLang, sourceLang, ct);
        if (bingResult is not null)
            return bingResult;

        var myMemoryResult = await TryMyMemoryEndpointAsync(text, targetLang, sourceLang, ct);
        if (myMemoryResult is not null)
            return myMemoryResult;

        var googleResult = await TryGoogleMultiEndpointAsync(text, targetLang, sourceLang, ct);
        if (googleResult is not null)
            return googleResult;

        // 4. 配了的后端全都失败了（"一条都没配"走上面 step 0，两者不能混成一个文案）
        //    必须带上最后一次尝试给出的原因：没有它，"网络不通"与"AppID 填错了"是同一句话。
        throw new TranslationException(DescribeBackends(_config)
            + (_lastBackendNote is null ? "" : $"\n最后一次尝试的原因：{_lastBackendNote}"));
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
            _lastBackendNote ??= "有道未配置凭据（AppID 或密钥为空）";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
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
            _lastBackendNote = $"有道: 文本过长 ({text.Length} 字符 > {YoudaoMaxChars})，已跳过并降级";
            CoreLog.Warn("Translate", _lastBackendNote, LogCategory.Network);
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
                NoteSuccess("有道开放平台");
                return result;
            }
            // ⚠ 不把响应原文写进日志：错误响应里可能带 query/输入串，那正是 OCR 原文。
            //   原因已经由 ParseYoudaoResponse 归一成人话存进 _lastBackendNote（含 errorCode）。
            CoreLog.Warn("Translate", "有道返回不可用结果：" + (_lastBackendNote ?? "未给出原因"), LogCategory.Network);
        }
        catch (TaskCanceledException) { _lastBackendNote = "有道请求超时"; }
        catch (HttpRequestException ex) { _lastBackendNote = "有道网络失败: " + ex.GetType().Name; }
        catch (Exception ex)
        {
            CoreLog.Warn("Translate", "有道请求异常", ex, LogCategory.Network);
            _lastBackendNote = "有道异常: " + ex.GetType().Name;
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
    /// 失败: <c>{"errorCode":"108", ...}</c>。</summary>
    /// <remarks>
    /// 改成实例方法是为了能把"为什么失败"写进 <see cref="_lastBackendNote" /> ——
    /// 原先这里只把 errorCode 丢给 <c>Debug.WriteLine</c>（Release 下无处可去），
    /// 外层于是静默降到 Google：用户填错 AppID 和"有道正常工作"在界面上长得一模一样。
    /// </remarks>
    private string? ParseYoudaoResponse(string json)
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
                _lastBackendNote = ExplainYoudaoError(code);
                CoreLog.Warn("Translate", _lastBackendNote, LogCategory.Network);
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

            // errorCode 是 0 却没有可用译文 —— 形态与文档不符，也要给出原因而不是静默降级
            _lastBackendNote = "有道 errorCode 0 但响应里没有非空 translation 数组（响应形态与文档不符）";
        }
        catch (JsonException ex)
        {
            _lastBackendNote = "有道响应不是合法 JSON（" + ex.GetType().Name + "）";
        }
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
    //  Google 端点（非官方，默认关闭）
    // ═══════════════════════════════════════

    /// <summary>
    /// Google 的两个<b>非官方</b>端点：<c>client=gtx</c>（网页版）与 <c>client=dict-chrome-ex</c>
    /// （Chrome 划词）都是给 Google 自家前端用的，不是公开 API —— 不带 key 调用不符合其服务条款。
    /// <para>2026-09-20 许可合规审计（B-8）：README 曾写"只走官方开放平台 API"，而这两个端点
    /// 一直是默认降级路径的一部分 ⇒ 声明与实现矛盾。现收进一个<b>默认关闭</b>的开关后面：
    /// 关着的时候本方法返回空数组，<see cref="TryGoogleMultiEndpointAsync"/> 直接不调用任何网络。
    /// 官方 Cloud Translation API 需要用户自备 key，届时应新增一条 <c>client=</c> 无关的
    /// <c>/v3/projects/.../translations</c> 路径，而不是复用这里的端点。</para>
    /// </summary>
    internal static (string url, string label)[] UnofficialGoogleEndpoints(
        bool allowed, string text, string targetLang, string? sourceLang)
    {
        if (!allowed) return [];

        string sl = sourceLang ?? "auto";
        string encoded = HttpUtility.UrlEncode(text);
        return
        [
            ($"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={targetLang}&dt=t&q={encoded}",
             "Google (gtx)"),
            ($"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={sl}&tl={targetLang}&q={encoded}",
             "Google (chrome-ex)"),
        ];
    }

    private async Task<string?> TryGoogleMultiEndpointAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        var endpoints = UnofficialGoogleEndpoints(
            _config.AllowUnofficialTranslateEndpoints, text, targetLang, sourceLang);
        if (endpoints.Length == 0)
        {
            // 留痕而不是静默：否则"翻译偶尔不走 Google"在这台机器上无从解释
            _lastBackendNote ??= FreeEndpointsDisabledNote;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }

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
                    NoteSuccess(label);
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
    //  必应网页翻译端点（非官方，与 Google 共用同一条 opt-in 闸门）
    // ═══════════════════════════════════════

    /// <summary>
    /// 必应<b>网页版</b>的翻译端点：先抓 <see cref="BingCredsPageUrl"/> 的 HTML 拿
    /// <c>IG / IID / key / token</c>，再 POST <c>/ttranslatev3</c>。免密钥、能整句。
    /// <para>2026-09-21 本机（中国大陆网络）实测取证：<c>scripts/probes/bing_free_endpoint_probe.py</c>
    /// （8/8 判据，英→中、中→英、连打 5 次全成，1.1–2.4 s/句）。同期 Google 那两个端点 12 s 超时
    /// ⇒ 这条是**当前唯一在国内可用的免密整句路</b>，所以排在 Google 前面。</para>
    /// <para>⚠ 它不是公开 API：是必应网页前端的自用接口，微软未授权第三方调用；勾选框一栏必须把这句
    /// 和"原文会离开本机"一起写明。token 页内 TTL 约 1 小时，页面改版则三个正则同时失效 ——
    /// 那属于"随时可能静默断"的依赖，绝不能升级成默认路径。</para>
    /// </summary>
    private const string BingCredsPageUrl = "https://www.bing.com/translator";

    /// <summary>必应对无 UA 的请求会给不同形状的页面，抓不到常量数组 ⇒ 与探针脚本用同一个浏览器 UA。</summary>
    private const string BingBrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly Regex s_bingIgRe = new("IG:\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex s_bingIidRe = new("data-iid=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex s_bingHelperRe =
        new(@"params_AbusePreventionHelper\s*=\s*([^\]]+\])", RegexOptions.Compiled);

    internal sealed record BingCreds(string Host, string Ig, string Iid, string Key, string Token, DateTimeOffset Expires);

    /// <summary>进程级凭据缓存。一次点击翻译会并发发多行（SemaphoreSlim(4)），
    /// 逐行去重抓 ~650 KB 的页面不可接受；提前 5 分钟判过期，避免刚好卡在边界上失败。</summary>
    private static BingCreds? s_bingCreds;
    private static readonly SemaphoreSlim s_bingCredsLock = new(1, 1);

    /// <summary>本程序的目标语言码 → 必应的语言码。实测只需收 zh 一支：
    /// 必应认 <c>zh-Hans</c> / <c>zh-Hant</c>，而设置页存的是 <c>zh-CN</c> / <c>zh-TW</c>；
    /// 其余（en/ja/ko/fr/de/es/ru/ar…）必应与本程序用同一种写法，原样传。</summary>
    internal static string MapToBingLang(string lang)
    {
        string primary = lang.Split('-')[0].ToLowerInvariant();
        if (primary != "zh") return lang;
        bool traditional = lang.Contains("TW", StringComparison.OrdinalIgnoreCase)
                        || lang.Contains("HK", StringComparison.OrdinalIgnoreCase)
                        || lang.Contains("Hant", StringComparison.OrdinalIgnoreCase);
        return traditional ? "zh-Hant" : "zh-Hans";
    }

    /// <summary>组装一次必应请求。<b>闸门关着时返回 null，连凭据页都不抓</b>
    /// （"关着但不发请求"与"关着、发了又忽略结果"是两回事，后者等于没关）。
    /// <para>⚠ host 必须是抓凭据页时**落地后的那个子域**：POST 打在 www.bing.com 会中 302，
    /// 而跟随重定向会把 form body 丢掉 ⇒ 响应看着是 "HTTP 200 + 空 body"，
    /// 与"端点根本没服务"同形（实测现场见探针脚本的 T1）。</para>
    /// <para>⚠ <c>text</c> 只能有一个：实测同名三个 text 参数只翻第一条，另两句静默丢失。</para>
    /// </summary>
    internal static (string url, KeyValuePair<string, string>[] form, string label)? BingWebRequest(
        bool allowed, BingCreds creds, string text, string targetLang, string? sourceLang)
    {
        if (!allowed) return null;
        string url = $"https://{creds.Host}/ttranslatev3?isVertical=1&&IG={creds.Ig}&IID={creds.Iid}";
        var form = new[]
        {
            new KeyValuePair<string, string>("text", text),
            new KeyValuePair<string, string>("fromLang", string.IsNullOrWhiteSpace(sourceLang) ? "auto-detect" : sourceLang),
            new KeyValuePair<string, string>("to", MapToBingLang(targetLang)),
            new KeyValuePair<string, string>("key", creds.Key),
            new KeyValuePair<string, string>("token", creds.Token),
        };
        return (url, form, "必应 (ttranslatev3)");
    }

    /// <summary>解析必应响应。<b>只看响应体的实质内容，不看 HTTP 状态码</b>：
    /// 凭据不合法时必应回的是 <c>HTTP 200 + {"statusCode":205}</c>（实测）。
    /// 返回 (译文或 null, 人话原因)；原因会被拼进最终异常，不能是空串。</summary>
    internal static (string? text, string note) ParseBingResponse(string body)
    {
        string s = body.Trim();
        if (s.Length == 0) return (null, "必应响应为空（多半是 POST 打在 www.bing.com 被 302 掉了 form body）");
        if (s[0] != '[' && s[0] != '{') return (null, $"必应响应不是 JSON（{s.Length} B，形如网页）");
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string code = root.TryGetProperty("statusCode", out var sc) ? sc.ToString() : "?";
                return (null, $"必应体内 statusCode={code}（凭据过期或被拒，需重抓凭据页）");
            }
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return (null, "必应响应是空数组");
            var item = root[0];
            if (item.TryGetProperty("translations", out var tr)
                && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0
                && tr[0].TryGetProperty("text", out var t))
            {
                string? text = t.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return (text, "成功");
                return (null, "必应 translations[0].text 为空");
            }
            return (null, "必应响应里没有 translations 数组（形状与实测不符）");
        }
        catch (JsonException)
        {
            return (null, "必应响应 JSON 解析失败");
        }
    }

    /// <summary>抓（或复用）必应的前端凭据。失败时把原因写进 <see cref="_lastBackendNote"/> 并返回 null。</summary>
    private async Task<BingCreds?> GetBingCredsAsync(CancellationToken ct)
    {
        var fresh = s_bingCreds;
        if (fresh is not null && fresh.Expires > DateTimeOffset.UtcNow) return fresh;

        await s_bingCredsLock.WaitAsync(ct);
        try
        {
            fresh = s_bingCreds;
            if (fresh is not null && fresh.Expires > DateTimeOffset.UtcNow) return fresh;

            using var req = new HttpRequestMessage(HttpMethod.Get, BingCredsPageUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", BingBrowserUa);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
            using var resp = await s_http.SendAsync(req, ct);
            string html = await resp.Content.ReadAsStringAsync(ct);

            string ig = s_bingIgRe.Match(html) is { Success: true } m1 ? m1.Groups[1].Value : "";
            string iid = s_bingIidRe.Match(html) is { Success: true } m2 ? m2.Groups[1].Value : "";
            string key = "", token = "";
            int ttlMs = 3_600_000;
            if (s_bingHelperRe.Match(html) is { Success: true } hm)
            {
                try
                {
                    using var arr = JsonDocument.Parse(hm.Groups[1].Value);
                    foreach (var el in arr.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            string v = el.GetString() ?? "";
                            if (v.Length >= 16 && !v.All(char.IsDigit) && token.Length == 0) token = v;
                        }
                        else if (el.ValueKind is JsonValueKind.Number)
                        {
                            long n = el.GetInt64();
                            if (n > 0 && n < 100_000_000_000_000 && key.Length == 0
                                && n > 1_000_000_000_000) key = n.ToString();      // 毫秒时间戳形状
                            else if (n is >= 1_000 and <= 86_400_000) ttlMs = (int)n;  // TTL 形状
                        }
                    }
                }
                catch (JsonException) { /* 交给下面的"抓不到凭据"统一留痕 */ }
            }

            if (ig.Length == 0 || iid.Length == 0 || key.Length == 0 || token.Length == 0)
            {
                // 页面改版（三个常量任一改名/挪走）就是这里的形态 —— 明写出来，别静默降级
                _lastBackendNote = $"必应凭据抓取失败（IG/IID/key/token 缺失，页面 HTML {html.Length} B）";
                CoreLog.Warn("Translate", _lastBackendNote, LogCategory.Network);
                return null;
            }

            // HttpClient 跟随重定向后，RequestMessage.RequestUri 就是落地地址（国内通常 cn.bing.com）
            string host = resp.RequestMessage?.RequestUri?.Host ?? "www.bing.com";
            var creds = new BingCreds(host, ig, iid, key, token,
                DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Max(60_000, ttlMs - 300_000)));
            s_bingCreds = creds;
            CoreLog.Debug("Translate", $"必应凭据已抓取: host={host}, 有效 {ttlMs / 1000}s", LogCategory.Network);
            return creds;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastBackendNote = "必应凭据页抓取失败（" + ex.GetType().Name + "）";
            CoreLog.Warn("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }
        finally
        {
            s_bingCredsLock.Release();
        }
    }

    private async Task<string?> TryBingWebEndpointAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        if (!_config.AllowUnofficialTranslateEndpoints)
        {
            _lastBackendNote ??= FreeEndpointsDisabledNote;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }

        // 凭据过期只重抓一次：第二次循环前把缓存清掉，避免一次翻译里反复抓 650 KB 页面
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var creds = await GetBingCredsAsync(ct);
            if (creds is null) return null;

            var built = BingWebRequest(true, creds, text, targetLang, sourceLang);
            if (built is null) return null;

            try
            {
                using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quickCts.Token);
                using var req = new HttpRequestMessage(HttpMethod.Post, built.Value.url)
                {
                    Content = new FormUrlEncodedContent(built.Value.form)
                };
                req.Headers.TryAddWithoutValidation("User-Agent", BingBrowserUa);
                req.Headers.TryAddWithoutValidation("Referer", $"https://{creds.Host}/translator");
                req.Headers.TryAddWithoutValidation("Origin", $"https://{creds.Host}");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");

                using var resp = await s_http.SendAsync(req, linked.Token);
                string body = await resp.Content.ReadAsStringAsync(linked.Token);
                var (translation, note) = ParseBingResponse(body);
                if (translation is not null)
                {
                    NoteSuccess(built.Value.label);
                    return translation;
                }

                _lastBackendNote = "必应: " + note;
                bool credsRejected = note.Contains("statusCode");
                if (!credsRejected || attempt == 1) break;
                s_bingCreds = null;   // 被拒 ⇒ 当作过期重抓一次（真过期与页面改版在这一条上分不开，留痕即可）
                CoreLog.Debug("Translate", "必应凭据被拒，重抓凭据后再试一次: " + note, LogCategory.Network);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastBackendNote = "必应请求失败（" + ex.GetType().Name + "）";
                CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
                break;   // 网络级失败重试一次不会变好，交给下一个端点
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户取消要往上传，不能吞成"这个后端失败"
            }
            catch (OperationCanceledException)
            {
                _lastBackendNote = "必应请求超时（>12 秒）";
                CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
                break;
            }
        }

        return null;
    }

    // ═══════════════════════════════════════
    //  Edge 免费端点（浏览器内置翻译口；2026-10-23 本机实测免凭据可用）
    // ═══════════════════════════════════════

    private const string EdgeTranslateUrl = "https://edge.microsoft.com/translate/translatetext";

    /// <summary>
    /// Edge 免费端点必须带一个<b>非空</b> <c>User-Agent</c>，否则一条都发不出去。
    /// <para>2026-10-23 用 <c>http.client</c> 精确控制头重测（前一版结论用的是 urllib，
    /// 它<b>自己偷偷注入了</b> <c>User-Agent: Python-urllib/3.12</c>，于是"我没设 UA"被误读成
    /// "这个端点不需要 UA"；.NET 的 <c>HttpClient</c> 真的不带 ⇒ 第一档当场 400、静默降到必应，
    /// 读数可由 <c>scripts/probes/edge_mymemory_free_endpoint_probe.py</c> 第 1 节复跑（它用
    /// <c>http.client</c> 而不是 urllib —— 后者会替自己注入 UA，正是这条被读错的原因）：</para>
    /// <list type="bullet">
    /// <item><description>完全不带 UA、或带一个空值 UA ⇒ 都是 400
    /// <c>Client Browser Version not supported</c>；</description></item>
    /// <item><description>而 <c>curl/8.9.0</c> 与 <c>Python-urllib/3.12</c> 都返回 200
    /// ⇒ 它<b>只判这个头存在且非空，不解析浏览器版本</b>。</description></item>
    /// </list>
    /// <para>所以这里发的是诚实的产品标识，而不是冒充 Chrome：既然版本解析并不存在，
    /// 冒充就只是把"哪天它真开始挑版本"这件事变成一条只有抓包才能发现的静默依赖。
    /// 这句 UA 不带版本号是刻意的 —— 它会随每次发布漂移，而这里没有任何东西读它。</para>
    /// </summary>
    internal const string EdgeClientUa = "TrueToneCap/desktop-translation-client";

    /// <summary>
    /// Edge 浏览器内置翻译的调用口：<b>不要 key、不要 token、也不要任何 cookie</b>，
    /// 但<b>要一个非空的 <see cref="EdgeClientUa"/></b>（见那句的说明）。
    /// <para>两条写错就 400、而 <b>400 的响应体是空的</b>（服务端不给原因）的形状约束：</para>
    /// <list type="number">
    /// <item><description>body 必须是<b>裸字符串数组</b> <c>["句子"]</c>。包成对象（<c>[{"text":…}]</c>、
    /// <c>[{"Text":…}]</c>）或写成 <c>{"text":…}</c> 全部 400。这一格当初漏掉时，
    /// 我据此写下过"Edge 系免认证不成立"的错判 —— 形态矩阵要从服务端那句
    /// <c>Cannot deserialize the current JSON object … requires a JSON array</c> 倒推补齐。</description></item>
    /// <item><description>自动检测源语言 = <b>不发 <c>from</c> 参数</b>；发 <c>from=auto</c> /
    /// <c>from=AUTO</c> 都是 400。⇒ 不能照抄 Google 那条 <c>sl = sourceLang ?? "auto"</c> 的写法。</description></item>
    /// </list>
    /// <para>语言码无需映射：App 下拉里的 <c>zh-CN</c> / <c>zh-TW</c> 等原样传都返回正确语种译文
    /// （必应那边要的 <see cref="MapToBingLang"/> 在这一条上是多余的）。批量实测与请求逐条对齐，
    /// 但本服务仍是逐行发（与必应同一决定，不依赖批量形态）。</para>
    /// <para>容量：单条 40,000 字符仍出译文，200,000 字符被拒并明写
    /// <c>Request exceeds the maximum allowed translation size.</c> ⇒ 上限落在两者之间，未穷尽。</para>
    /// </summary>
    internal static (string url, string body, string ua, string label)? EdgeFreeEndpointRequest(
        bool allowed, string text, string targetLang, string? sourceLang)
    {
        if (!allowed) return null;
        string url = $"{EdgeTranslateUrl}?&to={HttpUtility.UrlEncode(targetLang)}"
                   + (string.IsNullOrWhiteSpace(sourceLang)
                          ? ""                                   // 自动检测：整个参数不发，见上面第 2 条
                          : "&from=" + HttpUtility.UrlEncode(sourceLang));
        string body = JsonSerializer.Serialize(new[] { text }, LlmJsonContext.Default.StringArray);
        return (url, body, EdgeClientUa, "Edge (translatetext)");
    }

    /// <summary>解析 Edge 响应 <c>[{"translations":[{"text":"…"}]}]</c>。
    /// 返回 (译文或 null, 人话原因)；参数被拒时体是空的，所以状态码必须进原因句，
    /// 否则用户看到的只是"没有译文"。</summary>
    internal static (string? text, string note) ParseEdgeResponse(int status, string body)
    {
        string s = body.Trim();
        if (status != 200)
            return (null, $"Edge 返回 HTTP {status}"
                + (s.Length == 0
                    ? "（体为空：实测这就是请求形状不对的形态 —— body 要是裸字符串数组，且不能发 from=auto）"
                    : $"：{s[..Math.Min(s.Length, 120)]}"));

        if (s.Length == 0) return (null, "Edge 响应体为空");
        if (s[0] != '[' && s[0] != '{') return (null, $"Edge 响应不是 JSON（{s.Length} B，形如网页）");
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return (null, "Edge 响应是空数组或不是数组");
            var item = root[0];
            if (item.TryGetProperty("translations", out var tr)
                && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0
                && tr[0].TryGetProperty("text", out var t))
            {
                string? text = t.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return (text, "成功");
                return (null, "Edge translations[0].text 为空");
            }
            return (null, "Edge 响应里没有 translations 数组（形状与实测不符）");
        }
        catch (JsonException)
        {
            return (null, "Edge 响应 JSON 解析失败");
        }
    }

    private async Task<string?> TryEdgeFreeEndpointAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        var built = EdgeFreeEndpointRequest(
            _config.AllowUnofficialTranslateEndpoints, text, targetLang, sourceLang);
        if (built is null)
        {
            _lastBackendNote ??= FreeEndpointsDisabledNote;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }

        try
        {
            using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quickCts.Token);
            using var req = new HttpRequestMessage(HttpMethod.Post, built.Value.url)
            {
                // ⚠ 必须写全限定 System.Text.Encoding：本命名空间下有个 TrueToneCap.Core.Encoding
                //    命名空间，裸写 Encoding 会绑到它上面（CS0234）。
                Content = new StringContent(built.Value.body, System.Text.Encoding.UTF8, "application/json")
            };
            // ⚠ 少这一个头就一条都发不出去：400 "Client Browser Version not supported"
            //    （.NET 的 HttpClient 默认连 UA 都不发，而 urllib 会替自己加一个 —— 这就是
            //    第一轮"离线形状全绿、真发请求时第一档静默失效"的根因，见 EdgeClientUa 的说明）
            req.Headers.TryAddWithoutValidation("User-Agent", built.Value.ua);
            using var resp = await s_http.SendAsync(req, linked.Token);
            string body = await resp.Content.ReadAsStringAsync(linked.Token);
            var (translation, note) = ParseEdgeResponse((int)resp.StatusCode, body);
            if (translation is not null)
            {
                NoteSuccess(built.Value.label);
                return translation;
            }

            _lastBackendNote = "Edge: " + note;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户取消要往上传，不能吞成"这个后端失败"
        }
        catch (OperationCanceledException)
        {
            _lastBackendNote = "Edge 请求超时（>8 秒）";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastBackendNote = "Edge 请求失败（" + ex.GetType().Name + "）";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }

        return null;
    }

    // ═══════════════════════════════════════
    //  MyMemory 免费端点（翻译记忆库 + MT 兜底；官方文档明写允许匿名调用）
    // ═══════════════════════════════════════

    private const string MyMemoryApiUrl = "https://api.mymemory.translated.net/get";

    /// <summary>
    /// 单条请求的文本上限：<b>500 个字符</b>。
    /// <para>官方 spec 页写的是 "Max 500 bytes"，实测边界落在<b>字符</b>上：ASCII 501 字符即被拒，
    /// 而中文 300 字符＝900 UTF-8 字节照常通过（复跑：<c>scripts/probes/edge_mymemory_free_endpoint_probe.py</c> 第 3 节）
    /// ⇒ 谁要按字节切这句话，会把能过的文本白切一半。</para>
    /// </summary>
    internal const int MyMemoryMaxChars = 500;

    /// <summary>官方 usagelimits 页明写的匿名额度：免费 5000 字符/天（带 <c>de=</c> 邮箱 50000）。
    /// 一整屏 OCR 就吃满一天 ⇒ 这一条只配当兜底，排在 Edge 与必应之后。</summary>
    internal const int MyMemoryAnonymousCharsPerDay = 5000;

    /// <summary>组装 MyMemory 匿名请求。<paramref name="allowed"/> 为 false 时返回 null，
    /// <b>连请求都不发</b>（"关着但不发"与"发了但忽略结果"是两回事）。
    /// 文本超过 <see cref="MyMemoryMaxChars"/> 时同样返回 null，由调用方留痕说明——
    /// 发出去只会换来一句写在译文栏里的拒因。</summary>
    internal static (string url, string label)? MyMemoryEndpointRequest(
        bool allowed, string text, string targetLang, string? sourceLang)
    {
        if (!allowed || text.Length > MyMemoryMaxChars) return null;
        string sl = string.IsNullOrWhiteSpace(sourceLang) ? "autodetect" : sourceLang;
        return ($"{MyMemoryApiUrl}?q={HttpUtility.UrlEncode(text)}&langpair={HttpUtility.UrlEncode(sl)}|{HttpUtility.UrlEncode(targetLang)}",
                "MyMemory (匿名档)");
    }

    /// <summary>解析 MyMemory 响应。
    /// <para>⚠ 被长度/额度拒绝时它回的是 <b>HTTP 200 + 体内 <c>responseStatus</c> 非 200</b>，
    /// 而拒因被塞进 <c>responseData.translatedText</c>（实测见过
    /// <c>QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500</c>）。
    /// 只看状态码会把它当成功，只看译文栏会把拒因当译文 ⇒ 两者都必须判（与必应体内 205 同族）。</para>
    /// </summary>
    internal static (string? text, string note) ParseMyMemoryResponse(string body)
    {
        string s = body.Trim();
        if (s.Length == 0) return (null, "MyMemory 响应体为空");
        if (s[0] != '{') return (null, $"MyMemory 响应不是 JSON（{s.Length} B，形如网页）");
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            // 真件两种形状都见过：成功时是数字 200，拒绝时是字符串 "403"。
            // ⚠ 必须先验 ValueKind 再取：对 Null 调 GetString() 抛的是 InvalidOperationException，
            //    而下面只兜 JsonException —— 那会变成"翻译服务自己崩一下"而不是"这个后端失败"。
            int status = -1;
            if (root.TryGetProperty("responseStatus", out var st))
            {
                if (st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var num)) status = num;
                else if (st.ValueKind == JsonValueKind.String && int.TryParse(st.GetString(), out var txt)) status = txt;
            }
            string? text = root.TryGetProperty("responseData", out var rd)
                           && rd.TryGetProperty("translatedText", out var tt) ? tt.GetString() : null;

            if (status != 200)
                return (null, $"MyMemory 体内 responseStatus={status}"
                    + (string.IsNullOrWhiteSpace(text) ? "" : $"（译文栏里写的是：{text[..Math.Min(text.Length, 80)]}）"));
            if (string.IsNullOrWhiteSpace(text)) return (null, "MyMemory 响应里没有 responseData.translatedText");
            if (text.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                || text.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
                return (null, "MyMemory 匿名额度或单条长度到顶：" + text[..Math.Min(text.Length, 80)]);
            return (text, "成功");
        }
        catch (JsonException)
        {
            return (null, "MyMemory 响应 JSON 解析失败");
        }
    }

    private async Task<string?> TryMyMemoryEndpointAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct)
    {
        if (!_config.AllowUnofficialTranslateEndpoints)
        {
            _lastBackendNote ??= FreeEndpointsDisabledNote;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }
        if (text.Length > MyMemoryMaxChars)
        {
            _lastBackendNote ??= $"MyMemory 未参与：文本 {text.Length} 字符 > 该端点单条 {MyMemoryMaxChars} 字符上限";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            return null;
        }

        var built = MyMemoryEndpointRequest(true, text, targetLang, sourceLang);
        if (built is null) return null;

        try
        {
            using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quickCts.Token);
            string body = await _http.GetStringAsync(built.Value.url, linked.Token);
            var (translation, note) = ParseMyMemoryResponse(body);
            if (translation is not null)
            {
                NoteSuccess(built.Value.label);
                return translation;
            }

            _lastBackendNote = "MyMemory: " + note;
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _lastBackendNote = "MyMemory 请求超时（>8 秒）";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastBackendNote = "MyMemory 请求失败（" + ex.GetType().Name + "）";
            CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
        }

        return null;
    }

    /// <summary>免费端点被关掉时的留痕句。<b>引号里的标签必须与界面上那条勾选逐字相同</b>，
    /// 否则就会出现"提示让用户去找一个界面上不存在的控件"这种分叉 ——
    /// 2026-10-23 之前这条写的是「允许回退到非官方翻译端点（必应网页版 / Google）」，
    /// 而界面上真实叫「允许回退到 Google 非官方翻译端点」，用户照着提示找不到那个勾，
    /// 唯一看得见的出路就剩去注册拿密钥。判据：<c>Test_FreeEndpointHintNamesARealControl</c>。</summary>
    internal const string FreeEndpointsDisabledNote =
        "免费公共翻译端点未启用（需在设置 → 翻译 里勾选「使用免费公共翻译端点（识别到的文字会发给第三方）」）";

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
        string endpoint = ChatCompletionsUrl(_config.ApiEndpoint);

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

        // 先验内容再记归因：RequireTranslatedContent 对空译文要抛，归因不能抢在它前面写"成功"
        var llmDone = RequireTranslatedContent(content);
        NoteSuccess("自定义 LLM");
        return llmDone;
    }

    /// <summary>
    /// HTTP 200 而 content 为 JSON null / 空串 / 全空白 ⇒ 这次【没有译文】，不是"低质译文"。
    /// <para>原写法 <c>content?.Trim() ?? text</c> 会把用户自己送出去的源文本原样当作译文返回：
    /// 界面上看到的"那句英文没被翻译"就是它本身，日志里零痕迹 —— 比报错更坏，因为它长得像成功。
    /// 返回空串也只是换一种静默，所以这里抛异常交给 <see cref="TranslateAsync"/> 既有的
    /// <c>catch (Exception)</c>：换下一个后端，并由 <see cref="ClassifyLlmFailure"/> 把这条事实
    /// 写进 _lastBackendNote 与 Warn 日志（该 catch 无类型过滤，任何异常都不会冒到界面变成原始异常文本）。</para>
    /// </summary>
    internal static string RequireTranslatedContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                "端点回了 HTTP 200，但 choices[0].message.content 是空的（JSON null 或空白）⇒ 这次没有返回译文");

        return content.Trim();
    }

    // ═══════════════════════════════════════
    //  视觉翻译（整图直传支持 vision 的 OpenAI 兼容端点）
    // ═══════════════════════════════════════

    /// <summary>
    /// 单次上传的 PNG 上限（字节）。base64 后约 ×1.34，再叠加端点侧的请求体上限；
    /// 超限【直接报错】而不是偷偷缩图或退回文本路 —— 缩图会改变识别结果，
    /// 退回文本路则让用户以为走的是视觉模型。
    /// </summary>
    internal const int VisionPngMaxBytes = 6 * 1024 * 1024;

    /// <summary>视觉路是否真的可用：既要选了 Vision 模式，也要有可用的 OpenAI 兼容端点。</summary>
    public static bool VisionConfigured(LlmConfig c) =>
        c.UseVision && c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint);

    /// <summary>
    /// 整图直传视觉模型，一步完成"读图 + 翻译"：不经 OCR 文本，模型看到的是像素。
    /// <para>返回的数组与图中文字行【按顺序一一对应】，供预览窗口点对点覆盖。
    /// 行数对不上时抛 <see cref="TranslationException"/> 而不是错位回填 —— 错位会把
    /// 第一行的译文画到第五行上，那比失败更难发现。</para>
    /// </summary>
    public async Task<string[]> TranslateImageWithVisionAsync(byte[] png, string targetLang,
        int expectedLineCount, CancellationToken ct = default)
    {
        if (!VisionConfigured(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        if (png.Length > VisionPngMaxBytes)
            throw new TranslationException(
                $"截图 PNG 为 {png.Length / 1024.0 / 1024.0:F1} MB，超过视觉端点单次上限 " +
                $"{VisionPngMaxBytes / 1024 / 1024} MB —— 请缩小选区，或把翻译模式切回 LLM（文字走 OCR 再翻译）。");

        string endpoint = ChatCompletionsUrl(_config.ApiEndpoint);

        string model = !string.IsNullOrWhiteSpace(_config.ModelName) ? _config.ModelName : "gpt-4o-mini";

        // 提示词只要"逐行、按顺序、不带编号"这三件事；OCR 的文字内容刻意【不】进 prompt，
        // 否则模型会照着文本翻而不是读图，那这条链路就退化成文本路了。
        string instruction =
            (string.IsNullOrWhiteSpace(_config.SystemPrompt)
                ? "你是截图翻译引擎。读出图片里的文字并翻译成目标语言。"
                : _config.SystemPrompt + "\n") +
            $"目标语言：{targetLang}。" +
            $"图中约 {expectedLineCount} 行文字：请按图中从上到下、从左到右的顺序输出等数量的行，" +
            "每行一条译文；不要编号、不要解释、不要合并或拆分行、不要输出原文。";

        var requestBody = new LlmVisionRequest(
            Model: model,
            Messages:
            [
                new LlmVisionMessage("user",
                [
                    new LlmContentPart("text", instruction, null),
                    new LlmContentPart("image_url", null,
                        new LlmImageUrl("data:image/png;base64," + Convert.ToBase64String(png)))
                ])
            ],
            Temperature: 0.1,
            MaxTokens: 4096);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody, LlmJsonContext.Default.LlmVisionRequest)
        };
        if (!string.IsNullOrEmpty(_config.ApiKey))
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

        string content;
        try
        {
            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (Exception ex) when (ex is not TranslationException and not OperationCanceledException)
        {
            // 与文本路同一套分类：400 常意味着这个模型根本不吃图片
            _lastBackendNote = ClassifyLlmFailure(ex);
            CoreLog.Warn("Translate", "视觉路失败: " + _lastBackendNote, ex, LogCategory.Network);
            throw new TranslationException(
                _config.ModelName + " 的视觉请求没有成功。" + _lastBackendNote +
                "。若该端点不支持图片输入，请把翻译模式切回 LLM（文字路）。");
        }

        var mapped = MapVisionLines(content, expectedLineCount);
        if (mapped is null)
        {
            int got = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            CoreLog.Warn("Translate",
                $"视觉路返回行数与图中不符（{got} vs {expectedLineCount}）→ 放弃逐行覆盖", LogCategory.Network);
            throw new TranslationException(
                $"视觉模型返回了 {got} 行，图中是 {expectedLineCount} 行，行数对不上就不做逐行覆盖" +
                "（错位覆盖比失败更难发现）。可重试一次，或切回 LLM 文字路。");
        }

        CoreLog.Info("Translate", $"视觉路成功: {expectedLineCount} 行", LogCategory.Network);
        return mapped;
    }

    /// <summary>
    /// OpenAI 兼容端点的 chat/completions 完整 URL。文本路与视觉路共用这一处，
    /// 避免"两路各拼一遍"—— 这类复制最后总会有一半忘记改。
    /// </summary>
    internal static string ChatCompletionsUrl(string apiEndpoint)
    {
        var e = apiEndpoint.TrimEnd('/');
        return e.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? e
            : e + "/chat/completions";
    }

    /// <summary>
    /// 把模型返回的整段文本切成 expectedLineCount 行。正好等行才返回，否则 null。
    /// <para>容忍 "1. " / "2、" / "3)" 这类编号前缀：模型即使被要求"不要编号"也常会带上，
    /// 去掉编号不改变行数，因此可以安全容忍；而任何行数偏差都必须判为不可用。</para>
    /// </summary>
    internal static string[]? MapVisionLines(string? raw, int expectedLineCount)
    {
        if (string.IsNullOrWhiteSpace(raw) || expectedLineCount <= 0) return null;
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s*\d+\s*[.、)：:]\s*", ""))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        return lines.Length == expectedLineCount ? lines : null;
    }
}

/// <summary>翻译异常。</summary>
public class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

/// <summary>
/// "没有任何可用的翻译后端被配置"—— 与"后端都试过了但失败（网络/限流/接口变更）"必须分开。
/// <para>混在一起的表现是：用户什么都没配，却收到一句"可能是网络问题"，于是要么去查网线、
/// 要么以为软件坏了。这一条异常给 UI 一个不需要猜的分支。</para>
/// </summary>
public sealed class TranslationNotConfiguredException : TranslationException
{
    public TranslationNotConfiguredException(string message) : base(message) { }
}

/// <summary>LLM API 配置（支持 OpenAI 兼容端点）。</summary>
public class LlmConfig
{
    public bool UseCustomLlm { get; set; }

    /// <summary>
    /// 视觉翻译模式：整张截图直传给支持图片的 OpenAI 兼容端点，一步完成读图+翻译，
    /// 不经 OCR 文本（由 <see cref="TranslateImageWithVisionAsync"/> 实现）。
    /// <para>要求 <see cref="UseCustomLlm"/> 与 <see cref="ApiEndpoint"/> 同时成立，
    /// 且该模型必须支持图片输入 —— 否则端点会回 400，<see cref="ClassifyLlmFailure"/> 会把这句话说明白。</para>
    /// </summary>
    public bool UseVision { get; set; }
    public string ApiEndpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = "deepseek-chat";
    public string SystemPrompt { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    // 源语言字段已删除（2026-09-21）：全仓零赋值零读取，源语言实际由各家后端 auto 检测
    // （有道 "auto" / LLM "auto-detect" / 必应 detect）。留着只会让人以为可以指定源语言。
    // 真要支持，得连"设置 → 翻译"里的源语言下拉一起做，而不是补一个没人读的属性。

    /// <summary>有道智云开放平台 应用ID（AppKey）。为空则跳过有道后端，降级到 Google。</summary>
    public string YoudaoAppKey { get; set; } = "";

    /// <summary>有道智云开放平台 应用密钥（AppSecret，仅用于本地计算签名）。</summary>
    public string YoudaoAppSecret { get; set; } = "";

    /// <summary>
    /// 是否允许把文本发给<b>免费公共翻译端点</b>：Edge 浏览器内置口 <c>translatetext</c>、
    /// 必应网页版 <c>ttranslatev3</c>（要抓前端凭据）、MyMemory 匿名档、Google 的两个网页前端口。
    /// <para>默认 <b>true</b>（2026-10-23 用户口径："不应该让用户手动填 KEY，
    /// 免费端点应该考虑各种免费开放端点"）。这推翻的是 2026-09-21 那条"不得升成默认路径"的上限 ——
    /// 当时定的默认关是拿必应/Google 的 ToS 与"改版即断"当理由，而免费档因此在默认配置下
    /// <b>一条都不通</b>，用户想翻译就只能自己去注册拿密钥。</para>
    /// <para>不变的代价要如实写给用户，且必须在界面上出现：这些接口会把截图里的文字（可能含
    /// token、身份证号、聊天记录）发给第三方，且除 MyMemory 外都不是给我们用的公开 API。
    /// 所以界面上这条是<b>可关</b>的 opt-out，不是需要同意才用的 opt-in。</para>
    /// <para>关掉时这几条连请求都不会发，不是"发了但忽略结果"。</para>
    /// <para>键名 <c>AllowUnofficialTranslateEndpoints</c> 自 v0.3.x 起就是持久化键，
    /// 覆盖范围从"Google 两口"扩到现在这四条时都没改过名 —— 改名会让老配置读不到自己
    /// 原本的值，这一层的迁移已经够多了（见 <c>SettingsService</c> 的同名门）。</para>
    /// <para>判据：<c>CorePipelineTests.Test_FreeTranslateEndpointsAreOnByDefault</c>
    /// 与 <c>Test_UnofficialTranslateEndpointsAreOptIn</c>（后者钉的是"关掉 ⇒ 真不发请求"）。</para>
    /// </summary>
    public bool AllowUnofficialTranslateEndpoints { get; set; } = true;

}

// ═══════════════════════════════════════
//  提供商清单：单一事实源在 UI 侧，这里不再留第二份表
// ═══════════════════════════════════════
//
// 这里曾经有一个 public static class LlmProviders（11 个提供方 / 27 个模型），2026-09-21 删除。
// 删除的理由不是"没人用"这一条，而是它是**第二份事实源**：
//   · 提供方清单真正生效的地方是 MainWindow.xaml 的 LlmProviderCbo（9 项）
//     与 MainWindow.OcrSettings.cs 的 OnLlmProviderChanged（tag → endpoint/默认模型）；
//   · 模型清单真正生效的地方是 LlmModelCbo 的 ComboBoxItem.Tag；
//   · LlmProviders 全仓唯一的消费者是 OcrServiceTests 的三条断言 ⇒ 门禁绿灯覆盖的是一个
//     用户永远碰不到的表，而三处真表之间的漂移（如 Gemini 在 XAML 是 gemini-2.0-flash、
//     在 LlmProviders 是 gemini-3.5-flash）没有任何判据在看。
// 现在的判据改钉这三处活表是否闭合，见 OcrServiceTests 的「LLM 提供商/模型清单」一节
// （Test 工程只引用 Core，枚举不了 WinUI 控件，所以判据只能读源文件文本）。

