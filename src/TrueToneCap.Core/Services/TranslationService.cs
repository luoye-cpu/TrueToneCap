// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationService.cs
// 多后端翻译服务：LLM (OpenAI 兼容) → 有道官方开放平台 → 免费公共端点（Edge / 必应 / MyMemory / Google，默认启用）
// 支持: DeepSeek V4.1 Flash / GLM-5.3-Flash / Qwen3.8-Flash / Kimi K3.1 Pro / GPT-5.4 mini / Claude Sonnet 5 / Gemini 3.8 Flash / Grok 4.7 / Mistral Large 4 / 任意 OpenAI 兼容端点

using System.Collections.Concurrent;
using System.Net;
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
    // ═══ 2026-11-09: 智能限速 + 防频繁请求风控 ═══
    // ⚠️ 关键问题：大量 OCR 行同时翻译时，虽然并发限制为 4，但如果 20 行都走 Edge/Bing，
    //    仍然可能在短时间内触发"每秒多次请求"的风控。
    //    解决方案：增加全局限流计数器，检测短时间内的请求频率，自动退避。
    //
    // 2026-11-16 修复：旧实现的计数器只在"已经超阈值"的分支里自增，正常请求永远不计数，
    // 于是限流器事实上是死代码；并且 LLM 把限流放在 SendAsync 之后，Edge/Bing 干脆没调用。
    // 现在每个请求在【发送前】先占一个窗口槽位；窗口满了就等下一拍再重试。
    private static int s_requestCountInSecond = 0;
    private static DateTime s_lastSecondReset = DateTime.UtcNow;
    private static readonly object s_rateLimitLock = new();

    // 最大每秒请求数（全后端共享；4 路并发 + 重试都在这一条闸门下）
    private const int MAX_REQUESTS_PER_SECOND = 6;

    /// <summary>请求发送前的全局限流闸门。每个请求都必须先在这里占一个槽位。</summary>
    internal static async Task WaitIfRateLimitedAsync(CancellationToken ct)
    {
        // ⚠ 必须在取号前检查：窗口没满时下面会直接 return，原先一条都不检查，
        //    预取消的 token 仍会真的发出请求（有道/Google 的 catch 又会把它当超时吞掉）。
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            int delayMs;
            int observed;
            lock (s_rateLimitLock)
            {
                var now = DateTime.UtcNow;
                if ((now - s_lastSecondReset).TotalSeconds >= 1.0)
                {
                    s_requestCountInSecond = 0;
                    s_lastSecondReset = now;
                }

                observed = s_requestCountInSecond;
                if (observed < MAX_REQUESTS_PER_SECOND)
                {
                    // 取号窗口内再确认一次：避免“刚检查完就被取消”后仍占一个请求名额。
                    ct.ThrowIfCancellationRequested();
                    s_requestCountInSecond = observed + 1;   // 本请求占位；下一次同窗口请求看到的已是 +1
                    return;
                }

                // 窗口已满：等到当前窗口结束，再加一点抖动避免同一刻一起醒来。
                double elapsedMs = (now - s_lastSecondReset).TotalMilliseconds;
                delayMs = (int)Math.Clamp(1000 - elapsedMs, 1, 1000)
                        + Random.Shared.Next(0, 120);
            }

            // ⚠ 不能直接在 lock 内 await（会串行化所有翻译请求）；离开 lock 后再等。
            CoreLog.Debug("Translate",
                $"请求频率过高 ({observed}/{MAX_REQUESTS_PER_SECOND})，退避{delayMs}ms",
                LogCategory.Network);
            await Task.Delay(delayMs, ct);
        }
    }
    
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

    // ═══ 2026-10-29：视觉路单独一个客户端 ═══
    // 共享实例的 15s 是给"逐行短文本"定的；整图 + 推理实测常在 20–60s（本机网关单图 28s），
    // 套 15s 等于让视觉路必然超时。这里只放宽时限，连接复用策略与文本路一致。
    private static readonly HttpClient s_visionHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // ═══ 2026-11-16：LLM 文本路单独一个客户端 ═══
    // 15s 的共享客户端是给免费端点的短请求定的；LLM 在本地网关/长上下文下实测常超过 15s，
    // 旧实现于是把"模型还在算"误判成失败并逐行降级到免费端点，同一屏出现两种译文风格。
    // 这里只放宽时限，连接复用策略与文本路一致；真正的时限由每次请求的链接 CTS 控制。
    private static readonly HttpClient s_llmHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>LLM 文本/批量请求的时限。视觉路另有 120s，不能把两件事混成一个常数。</summary>
    internal static readonly TimeSpan LlmTextRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>视觉请求的时限。用链接 CTS 实现（共享 HttpClient 的 Timeout=∞），
    /// 这样"用户关窗"与"端点太慢"能分开：前者重抛取消，后者给出可读原因。</summary>
    internal static readonly TimeSpan VisionRequestTimeout = TimeSpan.FromSeconds(120);

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

    /// <summary>最近一次批量/视觉翻译中，由“内容覆盖补译”救回的疑似未翻译行数。
    /// <para>与 <see cref="TranslationBatchResult.ContentRetryCount"/> 同源；视觉路没有
    /// batch 结果对象，UI 通过这个读数把“补了几行”写进状态栏。</para></summary>
    public int LastContentRetryCount { get; private set; }

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
        || c.AllowUnofficialTranslateEndpoints
        || c.UseOfflineArgos;

    /// <summary>按当前配置生成"缺什么 / 哪几条路可走"的中文说明，可直接展示。
    /// <para>⚠ 这里出现的每一个【引号内的界面控件名】都必须在界面源码里逐字存在 ——
    /// 判据 <c>Test_FreeEndpointHintNamesARealControl</c> 钉的就是这件事。
    /// 2026-10-23 之前这条写着让用户去勾「允许回退到非官方翻译端点（必应网页版 / Google）」，
    /// 而界面上那条叫「允许回退到 Google 非官方翻译端点」⇒ 用户照着提示找不到那个勾，
    /// 唯一剩下的出路就是去填密钥，"免费档必须填 key"就是这么造出来的。</para>
    /// </summary>
    public static string DescribeBackends(LlmConfig c)
    {
        bool vision = VisionConfigured(c);

        // 只有离线、没有视觉时，离线模式单独成句并放在最前：TranslateAsync 里离线路在
        // 任何在线后端之前直接返回，AllowUnofficialTranslateEndpoints 默认 true 也不改变
        // 这个事实 —— 把免费端点列成"已启用"是在撒谎，按"都没返回结果"的网络口径说话更是误导。
        // 它缺的不是"配置"，是语言包或 Python 运行环境。
        // 「离线翻译包」「安装运行环境」是界面上真实存在的控件文字（判据钉在 UI 源码里）。
        if (c.UseOfflineArgos && !vision)
            return "离线翻译已启用，但本地模型或运行环境未就绪：到 设置 → OCR/翻译 的「离线翻译包」卡片下载语言包；"
                 + "首次使用还需 Python 运行环境，可点该卡片里的「安装运行环境」。";

        var on = new List<string>();
        if (vision) on.Add("视觉翻译已启用（整图直传）");
        if (c.UseOfflineArgos)
            on.Add("离线翻译已启用（本地 Argos，文字不出本机；缺语言包/运行环境时会提示补装）");
        if (c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint)) on.Add("自定义 LLM");
        if (!string.IsNullOrWhiteSpace(c.YoudaoAppKey) && !string.IsNullOrWhiteSpace(c.YoudaoAppSecret)) on.Add("有道官方 API");
        if (c.AllowUnofficialTranslateEndpoints) on.Add("免费公共端点（Edge / 必应 / MyMemory / Google）");

        if (on.Count > 0)
        {
            // 视觉+离线同时为真时，不能再早退成"只有离线"：UI 实际走视觉，错误文案却只讲离线
            // 会让用户以为失败发生在一条没被执行的路径上。这里把已启用的路径全部列出，并写清当前分派顺序。
            if (vision && c.UseOfflineArgos)
                return "已启用的翻译路径：" + string.Join("、", on)
                     + "。当前分派优先级：视觉（整图） > 离线 > LLM 文本批量 > 逐行；"
                     + "视觉失败不会自动改走离线，离线失败不会自动改走视觉。";

            // ⚠ 这里不再猜原因。原先固定缀一句"多半是网络、额度或接口变更，不是没配置"，
            // 而真实原因（401 密钥错 / 有道 errorCode / 超时）由 _lastBackendNote 附在后面 ——
            // 两句会互相矛盾，用户只会相信先看到的那句。原因统一交给 TranslateAsync 拼。
            return "已启用的后端：" + string.Join("、", on) + " —— 它们都没返回结果。";
        }

        return "还没有任何可用的翻译后端，三条路任选一条：\n"
             + "① 设置 → 翻译：勾选「使用免费公共翻译端点（识别到的文字会发给第三方）」—— 不需要任何密钥，"
             + "依次走 Edge / 必应 / MyMemory / Google 这四家免费接口；\n"
             + "② 设置 → 翻译：填有道智云开放平台的 AppID 与密钥（官方 API；密钥只在本机 DPAPI 加密存储）；\n"
             + "③ 设置 → 翻译：把模式切到 LLM，填 DeepSeek / GLM / OpenAI 兼容端点与密钥。";
    }

    /// <summary>
    /// LLM 已勾选但端点为空、同时还有免费/有道可兜底时，给 UI 的显式配置警告。
    /// <para>这不是翻译失败，而是"自动回退已经发生"的提醒：不提示的话，用户会以为
    /// 自己配的 LLM 成功了，实际走的是免费公共端点。离线路径优先时不产生这条提醒。</para>
    /// </summary>
    public static string? LlmEndpointMissingWarning(LlmConfig c)
    {
        if (!c.UseCustomLlm || !string.IsNullOrWhiteSpace(c.ApiEndpoint) || c.UseOfflineArgos)
            return null;

        var fallback = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.YoudaoAppKey) && !string.IsNullOrWhiteSpace(c.YoudaoAppSecret))
            fallback.Add("有道官方 API");
        if (c.AllowUnofficialTranslateEndpoints)
            fallback.Add("免费公共端点（Edge / 必应 / MyMemory / Google）");
        if (fallback.Count == 0) return null;

        return "自定义 LLM 已启用，但 API 端点为空；本次已自动回退到"
             + string.Join(" / ", fallback)
             + "。要使用 LLM，请到 设置 → 翻译 填写端点；否则请关闭 LLM 模式。";
    }

    /// <summary>
    /// 把 LLM 后端的异常归成人能照着做的一句话。
    /// <para>分三类的理由：401/403 是<b>配置</b>问题（密钥或模型名），429 是<b>额度</b>问题，
    /// 只有连不上才是<b>网络</b>问题。混成"网络问题"会让用户去查网线，而真正要改的是密钥框。</para>
    /// </summary>
    internal static string ClassifyLlmFailure(Exception ex) => ex switch
    {
        LlmHttpException h => h.StatusCode switch
        {
            401 => $"自定义 LLM 返回 401（{h.Message}）—— 是 API 密钥不对或该模型未开通，不是网络问题",
            403 => $"自定义 LLM 返回 403（{h.Message}）—— 密钥没有访问该模型的权限，不是网络问题",
            404 or 410 => $"自定义 LLM 返回 {h.StatusCode}（{h.Message}）—— API 路径或模型名不存在，请检查端点和模型名，不是网络问题",
            405 => $"自定义 LLM 返回 405（{h.Message}）—— 该端点不接受这个路径/方法，请检查 API 地址",
            429 => $"自定义 LLM 返回 429（{h.Message}）—— 额度或频率到顶了",
            var code => $"自定义 LLM 返回 HTTP {code}（{h.Message}）",
        },
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
            $"自定义 LLM 超时（连接 10 秒 / 整次请求 {LlmTextRequestTimeout.TotalSeconds:F0} 秒）",
        _ => $"自定义 LLM 失败: {ex.GetType().Name}: {ex.Message}"
    };

    /// <summary>翻译文本。LLM 优先（如已配置）→ 有道官方开放平台 → 免费公共端点
    /// （Edge / 必应 / MyMemory / Google，默认启用，可在设置关闭）。</summary>
    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
        => await TranslateTextAsync(text, targetLang, sourceLang, ct, allowLlm: true).ConfigureAwait(false);

    /// <summary>单行翻译的共用实现。<paramref name="allowLlm"/> 为 false 时跳过 LLM，
    /// 供批量失败后的逐行回退使用，避免同一屏对同一个 LLM 再打 N 次。</summary>
    private async Task<string> TranslateTextAsync(string text, string targetLang,
        string? sourceLang, CancellationToken ct, bool allowLlm)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 0. 一条后端都没配 ⇒ 这不是"网络问题"，别拿网络文案糊用户
        //    （调用方应在扇出多行之前先问 AnyBackendAvailable，这里兜住直接调用者）
        if (!AnyBackendAvailable(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        // 0.4 视觉模式是整图直传路径：单行文本入口也不能把整图模型当逐行文本模型用。
        //     正常 UI 会在 VisionConfigured 时走 TranslateImageWithVisionAsync，这里兜住直接调用者；
        //     与 TranslateBatchAsync 的拒绝保持同一口径。
        if (VisionConfigured(_config))
            throw new TranslationException(
                "视觉模式是整图直传路径，不能调用文本翻译接口；请让 OcrPreviewWindow 走 TranslateImageWithVisionAsync。");

        // 0.5 离线路（2026-11-11 离线翻译包方案）。
        //     语义：用户选离线多半就是为了"文字不出本机"——失败时绝不静默回退到在线后端，
        //     否则隐私承诺变成谎言。原因（缺运行环境/缺语言包/翻译出错）原样抛给 UI。
        //     批量场景（OCR 预览窗）走 TranslateBatchAsync 整批进一个进程，别在这里逐行起进程。
        if (_config.UseOfflineArgos)
        {
            var from = OfflineArgosEngine.NormalizeLang(_config.OfflineSourceLangHint) ?? "en";
            var to = OfflineArgosEngine.NormalizeLang(targetLang) ?? "en";
            var batch = await OfflineArgosEngine.TranslateBatchAsync(new[] { text }, from, to, ct);
            if (batch.FailedCount > 0)
                throw new TranslationException($"离线翻译失败：{batch.FirstError ?? "本地模型返回异常"}");
            return batch.Lines[0];
        }

        // 1. LLM 优先（已配置端点 + Key）
        //    ⚠ 判空必须与 AnyBackendAvailable 一致用 IsNullOrWhiteSpace：空白端点
        //    在免费端点开启时会被 AnyBackendAvailable 救活，但这里若用 IsNullOrEmpty
        //    仍会去构造 "   /chat/completions"，失败后再静默落到免费端点。
        if (allowLlm && _config.UseCustomLlm && !string.IsNullOrWhiteSpace(_config.ApiEndpoint))
        {
            try
            {
                return await TranslateWithLlmAsync(text, targetLang, sourceLang, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户关窗/主动取消：这不是 LLM 失败，不能当成"降级到免费后端"继续跑
                throw;
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
    //  批量翻译（2026-11-16：LLM 结构化批量 + 逐行回退）
    // ═══════════════════════════════════════

    /// <summary>批量翻译结果。<see cref="FallbackCount"/> 是"批量没有交出、并且真的进入过逐行/跨后端
    /// 回退阶段"的行数；Auth 阻断而一次回退都没发生时保持 0。与 <see cref="FailedCount"/>
    /// （最终仍失败、回填原文的行数）分开，UI 才能如实显示"部分行降级"。</summary>
    public sealed record TranslationBatchResult(
        string[] Lines,
        int FailedCount,
        int FallbackCount,
        string? FirstError,
        string Mode)
    {
        /// <summary>最终由不同后端（免费/有道）补译的行数；0 = 全部同 LLM 或全成功。</summary>
        public int DegradedCount { get; init; }

        /// <summary>降级后端的人话名，例如“免费公共端点”“有道官方 API”。</summary>
        public string? DegradedBackend { get; init; }

        /// <summary>批量分片/逐行同 LLM 重试过、且最终成功的行数（不包含跨后端回退）。</summary>
        public int LlmRetryCount { get; init; }

        /// <summary>最终仍疑似整行未翻译的行数（译文等于原文/没有目标语言脚本）。
        /// <para>与 <see cref="FailedCount"/> 分开：这些行不是 null，旧的 FailedCount 口径会把它们
        /// 当成成功；UI 必须把两者都显示出来，才能避免“画面还是原文、状态栏却 ✅”。</para></summary>
        public int UnchangedCount { get; init; }

        /// <summary>最终仍疑似只翻译了一部分的行数（保留整段/明显源文词）。</summary>
        public int PartialCount { get; init; }

        /// <summary>由“内容覆盖补译”救回的行数（不计入 <see cref="LlmRetryCount"/>）。</summary>
        public int ContentRetryCount { get; init; }

        /// <summary>成功按段落合并翻译的连续行组数；0 = 没有形成段落组。</summary>
        public int ParagraphGroupCount { get; init; }

        /// <summary>成功按段落合并翻译覆盖的行数。</summary>
        public int ParagraphGroupRows { get; init; }
    }

    /// <summary>当前配置是否适合走 LLM 文本批量。视觉路与离线路有自己的整批实现，不能混用。</summary>
    public static bool LlmTextBatchConfigured(LlmConfig c) =>
        c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint)
        && !c.UseOfflineArgos && !c.UseVision;

    private const int LlmBatchMaxLines = 8;
    private const int LlmBatchMaxChars = 3000;

    private sealed record LlmBatchChunk(IReadOnlyList<int> Indices);

    /// <summary>同一段落的连续 OCR 行：Indices 是原始下标，Text 是按语言规则拼好的段落。</summary>
    private sealed record ParagraphGroup(int[] Indices, string Text);

    /// <summary>
    /// 批量翻译一组 OCR 行：LLM 按编号/JSON 结构化整批发送；任何缺行、格式坏、超时或 HTTP 失败
    /// 都先尝试同一 LLM（拆小分片/同片重试/逐行），跨后端只作最后兜底，不会把整屏静默换成免费端点。
    /// <para>⚠ 不无条件把整屏拼成一段长文：OCR 行是布局单元，菜单/按钮/读数之间没有语法关系。
    /// 只有“上一行明显没说完、下一行是小写或中日文”的连续行才先按段落请求；输出仍强制
    /// 按原行 <c>id</c> 等数量回填，段落只提供上下文，不改变回填粒度。</para>
    /// <para>离线/视觉模式不调用本方法（它们已有自己的整批路径）；若误调用则明确报错，
    /// 不偷偷按在线文本处理。</para>
    /// </summary>
    public async Task<TranslationBatchResult> TranslateBatchAsync(
        IReadOnlyList<string> lines, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (lines.Count == 0)
            return new TranslationBatchResult(Array.Empty<string>(), 0, 0, null, "空");

        // 全是空白行时没有任何可翻译内容：不要因为“没配后端”把它判成失败。
        if (lines.All(l => string.IsNullOrWhiteSpace(l)))
            return new TranslationBatchResult(lines.Select(l => l ?? "").ToArray(), 0, 0, null, "空");

        if (!AnyBackendAvailable(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        // 与 OcrPreviewWindow 的分派顺序保持一致：视觉 > 离线 > LLM 文本批量 > 逐行。
        // 两处顺序不一致时，“同时残留 Vision/Offline 标记”的旧配置会在这里悄悄走离线批处理，
        // 而 UI 明明走的是视觉路 —— 用户看到的和实际执行的不是同一条链。
        if (VisionConfigured(_config))
            throw new TranslationException(
                "视觉模式是整图直传路径，不能调用文本批量接口；请让 OcrPreviewWindow 走 TranslateImageWithVisionAsync。");

        if (_config.UseOfflineArgos)
        {
            var from = OfflineArgosEngine.NormalizeLang(_config.OfflineSourceLangHint) ?? "en";
            var to = OfflineArgosEngine.NormalizeLang(targetLang) ?? "en";
            var batch = await OfflineArgosEngine.TranslateBatchAsync(lines, from, to, ct);
            return new TranslationBatchResult(batch.Lines, batch.FailedCount, 0, batch.FirstError, "离线批量");
        }

        bool llmBatch = LlmTextBatchConfigured(_config);
        string?[] translated = new string?[lines.Count];
        string? firstError = null;
        int llmRetryCount = 0;

        void NoteFailure(string reason)
        {
            if (firstError is null) firstError = reason;
            CoreLog.Warn("Translate", "LLM 批量失败: " + reason, LogCategory.Network);
        }

        bool llmBlocked = false;
        int paragraphGroupCount = 0;
        int paragraphGroupRows = 0;

        // ═══ 2026-11-27：先按段落后按行 ═══
        // OCR 常把一句长文案折成多行；逐行发送时模型看不到完整句子，代词/术语/语序容易断。
        // 这里把“像同一段”的连续行拼成段落发一次，要求模型按原行 id 拆回逐行译文；
        // 段落调用失败的行不会丢，后面的批量/逐行兜底仍会接住。
        if (llmBatch)
        {
            var paragraphGroups = BuildParagraphGroups(lines);
            var pendingGroups = new List<ParagraphGroup>();
            int pendingRows = 0;
            int pendingChars = 0;

            async Task FlushParagraphBatchAsync()
            {
                if (pendingGroups.Count == 0) return;
                var batchGroups = pendingGroups.ToArray();
                pendingGroups.Clear();
                pendingRows = 0;
                pendingChars = 0;
                if (llmBlocked) return;

                int expectedRows = batchGroups.Sum(g => g.Indices.Length);
                try
                {
                    var got = await TranslateLlmParagraphBatchAsync(
                        batchGroups, lines, targetLang, sourceLang, ct);
                    int offset = 0;
                    foreach (var group in batchGroups)
                    {
                        for (int k = 0; k < group.Indices.Length; k++)
                            translated[group.Indices[k]] = got[offset + k];
                        offset += group.Indices.Length;
                    }
                    paragraphGroupCount += batchGroups.Length;
                    paragraphGroupRows += expectedRows;
                    CoreLog.Info("Translate",
                        $"段落合并成功：{batchGroups.Length} 段 / {expectedRows} 行", LogCategory.Network);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var kind = ClassifyLlmBatchFailure(ex);
                    NoteFailure("段落合并: " + ExplainLlmBatchFailure(ex));
                    CoreLog.Warn("Translate",
                        $"段落合并失败 kind={kind}（{batchGroups.Length} 段 / {expectedRows} 行）：{firstError}",
                        ex, LogCategory.Network);
                    if (kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.Configuration)
                        llmBlocked = true;
                }
            }

            foreach (var group in paragraphGroups)
            {
                ct.ThrowIfCancellationRequested();
                if (llmBlocked) break;
                if (group.Text.Length > LlmBatchMaxChars) continue;   // 过长的段落退回逐行，避免请求超限

                if (pendingGroups.Count > 0
                    && (pendingRows + group.Indices.Length > LlmBatchMaxLines
                        || pendingChars + group.Text.Length > LlmBatchMaxChars))
                    await FlushParagraphBatchAsync();

                pendingGroups.Add(group);
                pendingRows += group.Indices.Length;
                pendingChars += group.Text.Length;
            }
            await FlushParagraphBatchAsync();
        }

        if (llmBatch)
        {
            // 段落组已经交出的行不再发批量；失败的行仍在 translated 里是 null，照常进入分片兜底。
            var alreadyTranslated = new HashSet<int>(
                Enumerable.Range(0, lines.Count).Where(i => translated[i] is not null));
            foreach (var chunk in BuildLlmBatchChunks(lines, alreadyTranslated))
            {
                ct.ThrowIfCancellationRequested();
                if (llmBlocked) continue;   // Auth/429 已判定 LLM 不可用，剩余分片不再打

                var chunkTexts = new string[chunk.Indices.Count];
                for (int i = 0; i < chunk.Indices.Count; i++) chunkTexts[i] = lines[chunk.Indices[i]];

                try
                {
                    var got = await TranslateLlmBatchChunkAsync(chunkTexts, targetLang, sourceLang, ct);
                    for (int i = 0; i < chunk.Indices.Count; i++) translated[chunk.Indices[i]] = got[i];
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var kind = ClassifyLlmBatchFailure(ex);
                    NoteFailure(ExplainLlmBatchFailure(ex));
                    CoreLog.Warn("Translate",
                        $"LLM 批量分片失败 kind={kind}（行 {chunk.Indices[0] + 1}–" +
                        $"{chunk.Indices[chunk.Indices.Count - 1] + 1}）：{firstError}",
                        ex, LogCategory.Network);

                    var outcome = await RecoverLlmBatchChunkAsync(
                        chunkTexts, targetLang, sourceLang, ex, kind, NoteFailure, ct);
                    for (int i = 0; i < chunk.Indices.Count; i++)
                        if (outcome.Lines[i] is not null) translated[chunk.Indices[i]] = outcome.Lines[i];
                    llmRetryCount += outcome.RecoveredRows;
                    if (outcome.Fatal) llmBlocked = true;
                }
            }
        }

        // 批量没交出的行（含未配置 LLM 的逐行模式）先记下来；
        // 后面先尝试同 LLM 逐行，跨后端只作为最后兜底。
        var missing = Enumerable.Range(0, lines.Count)
            .Where(i => !string.IsNullOrWhiteSpace(lines[i]) && translated[i] is null)
            .ToArray();

        int failedCount = 0;
        int degradedCount = 0;
        int perLineLlmRows = 0;
        bool perLineAttempted = false;
        var degradedBackends = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        if (missing.Length > 0 && llmBatch && !llmBlocked)
        {
            perLineAttempted = true;
            // 同 LLM 逐行：并发压到 2，每行只多给一次尝试；只调用 LLM-only 路径，
            // 严禁走 TranslateTextAsync 的完整降级链，否则 LLM 一失败又会偷偷落到免费端点。
            using var sem = new SemaphoreSlim(2);
            var tasks = new Task[missing.Length];
            for (int t = 0; t < missing.Length; t++)
            {
                int i = missing[t];
                tasks[t] = Task.Run(async () =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        translated[i] = await TranslateSingleLineWithLlmOnlyAsync(
                            lines[i], targetLang, sourceLang, ct);
                        Interlocked.Increment(ref perLineLlmRows);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref firstError, ClassifyLlmFailure(ex), null);
                        CoreLog.Warn("Translate", "LLM 逐行重试失败: " + ex.Message, ex, LogCategory.Network);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
            }
            await Task.WhenAll(tasks);
            llmRetryCount += perLineLlmRows;
        }

        // 跨后端兜底：只有同 LLM 批量/逐行都失败、且配置了免费或有道时才走。
        var stillMissing = Enumerable.Range(0, lines.Count)
            .Where(i => !string.IsNullOrWhiteSpace(lines[i]) && translated[i] is null)
            .ToArray();

        bool crossBackendAttempted = false;
        if (stillMissing.Length > 0 && HasNonLlmBackend())
        {
            crossBackendAttempted = true;
            using var sem = new SemaphoreSlim(4);
            var tasks = new Task[stillMissing.Length];
            for (int t = 0; t < stillMissing.Length; t++)
            {
                int i = stillMissing[t];
                tasks[t] = Task.Run(async () =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        // 每行独立实例，成功后可读到 LastBackendServed；避免并发下"最后谁成功"串台。
                        var fallback = new TranslationService(_config);
                        translated[i] = await fallback.TranslateTextAsync(
                            lines[i], targetLang, sourceLang, ct, allowLlm: false);
                        if (llmBatch) Interlocked.Increment(ref degradedCount);
                        degradedBackends.TryAdd(fallback.LastBackendServed ?? "备用翻译引擎", 0);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref firstError, ex.Message, null);
                        CoreLog.Warn("Translate", "跨后端兜底失败: " + ex.Message, ex, LogCategory.Network);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
            }
            await Task.WhenAll(tasks);
        }

        // ═══ 2026-11-27：内容覆盖补译 ═══
        // 到这里“有非空字符串”的行不一定真的翻译了：模型可能整行回显原文、只翻半句，
        // 或从输入对象回显 `text`。这些行不会进入上面的 missing（它只找 null），
        // 所以必须再做一次逐行内容判定；疑似行用同 LLM 单行重试，绝不静默算成功。
        int contentRetryCount = 0;
        if (llmBatch && !llmBlocked)
        {
            var repair = await RepairContentIssuesAsync(
                lines, translated, targetLang, sourceLang, ct);
            contentRetryCount = repair.Recovered;
            if (repair.FirstError is not null)
                Interlocked.CompareExchange(ref firstError, repair.FirstError, null);
        }

        // 最终兜底：仍为空的行保留原文；空白行不算失败。
        bool[] hardFailed = new bool[translated.Length];
        for (int i = 0; i < translated.Length; i++)
        {
            if (translated[i] is null)
            {
                translated[i] = lines[i];
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    failedCount++;
                    hardFailed[i] = true;
                }
            }
        }

        // 内容层计数：与 FailedCount 分开。硬失败行已经回填原文，不应再重复算一次“未翻译”。
        int unchangedCount = 0;
        int partialCount = 0;
        for (int i = 0; i < translated.Length; i++)
        {
            if (hardFailed[i] || string.IsNullOrWhiteSpace(lines[i])) continue;
            var assessment = TranslationQuality.Assess(lines[i], translated[i], targetLang);
            if (assessment.Quality == TranslationLineQuality.Untranslated)
                unchangedCount++;
            else if (assessment.Quality == TranslationLineQuality.Partial)
                partialCount++;
        }

        string? degradedBackend = degradedCount > 0 && !degradedBackends.IsEmpty
            ? string.Join(" / ", degradedBackends.Keys.OrderBy(k => k, StringComparer.Ordinal))
            : null;

        string mode;
        if (!llmBatch) mode = "逐行";
        else if (degradedCount > 0) mode = "LLM 批量 + 跨后端降级";
        else if (perLineAttempted) mode = "LLM 批量 + 同后端逐行";
        else if (llmRetryCount > 0) mode = "LLM 批量 + 同后端重试";
        else mode = "LLM 批量";
        if (contentRetryCount > 0) mode += " + 内容补译";
        if (paragraphGroupCount > 0) mode += " + 段落合并";

        // FallbackCount 的口径是"批量没交出、并且真的进入过回退阶段"的行数。
        // Auth 阻断时 missing 仍是全部行，但同 LLM 逐行被跳过、跨后端可能也没配：
        // 这时记 missing.Length 会让 UI 声称"已按同 LLM 优先策略回退"，实际一次都没试。
        int fallbackCount = llmBatch
            ? (perLineAttempted || crossBackendAttempted ? missing.Length : 0)
            : missing.Length;

        return new TranslationBatchResult(
            translated.Select(s => s ?? "").ToArray(),
            failedCount,
            fallbackCount,
            firstError,
            mode)
        {
            DegradedCount = degradedCount,
            DegradedBackend = degradedBackend,
            LlmRetryCount = llmRetryCount,
            UnchangedCount = unchangedCount,
            PartialCount = partialCount,
            ContentRetryCount = contentRetryCount,
            ParagraphGroupCount = paragraphGroupCount,
            ParagraphGroupRows = paragraphGroupRows,
        };
    }

    /// <summary>批量的分片规则：最多 8 行或 3000 字符，避免一次请求过大/模型漏行。
    /// <para>只收非空白行的<b>原始下标</para>，不按“连续区间 + 非空计数”切 —— 中间夹一个空行时
    /// 区间会跨过它，把后面的行漏出这一片（2026-11-16 自审发现的边界）。</para></summary>
    private static List<LlmBatchChunk> BuildLlmBatchChunks(
        IReadOnlyList<string> lines, IReadOnlySet<int>? skip = null) =>
        BuildLlmBatchChunkIndices(lines, skip).Select(g => new LlmBatchChunk(g)).ToList();

    /// <summary>分片下标（internal 供测试钉边界：空行夹在中间时不许把后面的行漏掉）。
    /// <paramref name="skip"/> 用于段落组已经翻好的行，避免再发一次批量请求。</summary>
    internal static List<int[]> BuildLlmBatchChunkIndices(
        IReadOnlyList<string> lines, IReadOnlySet<int>? skip = null)
    {
        var chunks = new List<int[]>();
        List<int> current = new();
        int chars = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (skip?.Contains(i) == true) continue;
            int len = lines[i].Length;
            if (current.Count >= LlmBatchMaxLines || (current.Count > 0 && chars + len > LlmBatchMaxChars))
            {
                chunks.Add(current.ToArray());
                current = new List<int>();
                chars = 0;
            }
            current.Add(i);
            chars += len;
        }
        if (current.Count > 0) chunks.Add(current.ToArray());
        return chunks;
    }

    /// <summary>
    /// 把连续 OCR 行按“像不像同一段被折行”的分组。只合并有把握的折行：
    /// 上一行足够长且不是句末/标题，下一行是小写英文或中日文；空行、列表项永远断开。
    /// <para>为什么不把整屏直接拼成一段：菜单项、按钮、状态栏读数之间没有语法关系，
    /// 强行合并会让模型把无关文本串成一句话。</para>
    /// </summary>
    internal static List<int[]> BuildParagraphGroupIndices(IReadOnlyList<string> lines)
    {
        var groups = new List<int[]>();
        var current = new List<int>();
        int lastIndex = -1;

        void Flush()
        {
            if (current.Count > 0) groups.Add(current.ToArray());
            current = new List<int>();
            lastIndex = -1;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();   // 空行是最强的段落边界
                continue;
            }

            if (current.Count > 0
                && i == lastIndex + 1
                && current.Count < LlmBatchMaxLines
                && ShouldMergeParagraphLines(lines[lastIndex], line))
            {
                current.Add(i);
                lastIndex = i;
                continue;
            }

            Flush();
            current.Add(i);
            lastIndex = i;
        }
        Flush();
        return groups;
    }

    /// <summary>构造段落组（含拼好的段落文本）。只返回真正需要“按段落翻译”的组。</summary>
    private static List<ParagraphGroup> BuildParagraphGroups(IReadOnlyList<string> lines)
    {
        var groups = new List<ParagraphGroup>();
        foreach (int[] indices in BuildParagraphGroupIndices(lines))
        {
            if (indices.Length < 2) continue;
            var texts = indices.Select(i => lines[i]).ToArray();
            groups.Add(new ParagraphGroup(indices, JoinWrappedTranslationLines(texts)));
        }
        return groups;
    }

    /// <summary>
    /// 判断两行是否像同一段落的折行。保守规则：
    /// 空行/列表项断开；句末标点（.?!。！？；;）断开；标题冒号断开；
    /// 上一行短于 12 字符（多半是 UI 标签）不断开；下一行必须是小写英文或中日文。
    /// </summary>
    private static bool ShouldMergeParagraphLines(string previous, string next)
    {
        string p = previous.Trim();
        string n = next.Trim();
        if (p.Length < 8 || n.Length < 4) return false;
        // 中文短行常靠逗号表示“还没说完”；没有标点的 8~11 字中文多半是标签，不合并。
        // 英文没有这个歧义，8 字以上只要下一行是小写就按折行处理。
        bool hasCjk = p.Any(IsCjkChar);
        if (hasCjk && p.Length < 12
            && !(p.EndsWith("，") || p.EndsWith("、") || p.EndsWith(",")))
            return false;
        if (IsListMarkerLine(p) || IsListMarkerLine(n)) return false;

        // 英文连字符断词/路径断行：无条件接上。
        if (p.EndsWith("-") || p.EndsWith("‐") || p.EndsWith("–")) return true;

        char last = p[^1];
        if (".!?。！？…;；:：".Contains(last)) return false;
        if (n.EndsWith(":") || n.EndsWith("：")) return false;

        bool endsComma = last is ',' or '，' or '、';
        bool endsCue = hasCjk
            ? (p.Length >= 12 || endsComma)
            : (p.Length >= 12 || endsComma || IsEnglishContinuationCue(p));
        // 逗号/顿号后仍是同一句；下一行必须是小写或中日文，避免把 “Value” 这类新标签接进来。
        return endsCue && StartsLowerOrCjk(n);
    }

    private static bool IsEnglishContinuationCue(string p) =>
        Regex.IsMatch(p, @"\b(?:and|or|the|a|an|to|of|in|on|for|with|that|which|who|is|are|was|were|"
            + @"will|would|can|could|should|may|might|must|not|by|from|as|at|but|if|than|then|"
            + @"when|where|while|because|so|this|these|those|its|their|his|her|our|your)$",
            RegexOptions.IgnoreCase);

    private static bool StartsLowerOrCjk(string s)
    {
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || "\"'‘’“”([{（《【".Contains(c)) continue;
            if (char.IsLower(c)) return true;
            if (IsCjkChar(c)) return true;
            return false;
        }
        return false;
    }

    private static bool IsCjkChar(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\u3040' && c <= '\u30FF')
        || (c >= '\u31F0' && c <= '\u31FF');

    private static bool IsListMarkerLine(string s) =>
        Regex.IsMatch(s, @"^\s*(?:[-*•·‣▪◦>]|\d+[.)、]|[（(]\d+[）)]|#+)\s*");

    /// <summary>批量失败的恢复类型；分类决定重试/拆分/跨后端。</summary>
    internal enum LlmBatchFailureKind
    {
        Parse,
        Protocol,
        Timeout,
        RateLimited,
        Auth,
        /// <summary>端点路径/模型不存在等配置错误：拆片和逐行都不会成功，必须立即停止同一 LLM。</summary>
        Configuration,
        Transient,
        Unknown,
    }

    /// <summary>一个分片恢复后的结果：成功回填的行 + 被同 LLM 重试救回的行数 + 是否必须停止 LLM。</summary>
    private sealed record LlmBatchChunkOutcome(
        string?[] Lines,
        int RecoveredRows,
        bool Fatal);

    /// <summary>把异常归一成回退策略可用的类别。用户取消必须先被 catch 过滤，不能走到这里。</summary>
    internal static LlmBatchFailureKind ClassifyLlmBatchFailure(Exception ex) => ex switch
    {
        LlmHttpException { StatusCode: 429 } => LlmBatchFailureKind.RateLimited,
        LlmHttpException { StatusCode: 401 or 403 } => LlmBatchFailureKind.Auth,
        LlmHttpException { StatusCode: 404 or 405 or 410 } => LlmBatchFailureKind.Configuration,
        LlmHttpException { StatusCode: 408 } => LlmBatchFailureKind.Timeout,
        LlmHttpException { StatusCode: >= 500 } => LlmBatchFailureKind.Transient,
        LlmHttpException { StatusCode: 400 or 422 } => LlmBatchFailureKind.Protocol,
        LlmHttpException => LlmBatchFailureKind.Transient,
        TaskCanceledException => LlmBatchFailureKind.Timeout,
        TranslationException => LlmBatchFailureKind.Parse,
        JsonException or KeyNotFoundException => LlmBatchFailureKind.Protocol,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => LlmBatchFailureKind.Auth,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone }
            => LlmBatchFailureKind.Configuration,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
            => LlmBatchFailureKind.RateLimited,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout }
            => LlmBatchFailureKind.Timeout,
        HttpRequestException { StatusCode: var code }
            when code is not null && (int)code >= 500 => LlmBatchFailureKind.Transient,
        HttpRequestException { StatusCode: var code }
            when code is not null && (code == HttpStatusCode.BadRequest
                || code == HttpStatusCode.UnprocessableEntity) => LlmBatchFailureKind.Protocol,
        HttpRequestException => LlmBatchFailureKind.Transient,
        _ => LlmBatchFailureKind.Unknown,
    };

    /// <summary>429 有 Retry-After 就尊重；其余用一个短固定退避，避免重试风暴。</summary>
    private static TimeSpan LlmBatchRetryDelay(Exception ex, TimeSpan fallback)
        => ex is LlmHttpException h && h.RetryAfterSeconds is int seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60))
            : fallback;

    /// <summary>
    /// 对失败分片做一次恢复：
    /// Parse/Protocol/Timeout → 拆小一次；RateLimited → 退避后同片重试一次；
    /// Transient/Unknown → 短退避后同片重试一次；Auth/404/405/410 等配置错误 → Fatal，调用方停止后续 LLM。
    /// </summary>
    private async Task<LlmBatchChunkOutcome> RecoverLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang,
        Exception initial, LlmBatchFailureKind kind, Action<string> noteFailure,
        CancellationToken ct)
    {
        switch (kind)
        {
            case LlmBatchFailureKind.Auth:
            case LlmBatchFailureKind.Configuration:
                return new LlmBatchChunkOutcome(new string?[texts.Count], 0, true);

            case LlmBatchFailureKind.RateLimited:
                await Task.Delay(LlmBatchRetryDelay(initial, TimeSpan.FromSeconds(2)), ct);
                return await RetrySameLlmBatchChunkAsync(
                    texts, targetLang, sourceLang, noteFailure, ct);

            case LlmBatchFailureKind.Parse:
            case LlmBatchFailureKind.Protocol:
            case LlmBatchFailureKind.Timeout:
                if (texts.Count <= 1)
                    return new LlmBatchChunkOutcome(new string?[texts.Count], 0, false);
                return await RetrySplitLlmBatchChunkAsync(
                    texts, targetLang, sourceLang, noteFailure, ct);

            case LlmBatchFailureKind.Transient:
            case LlmBatchFailureKind.Unknown:
            default:
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return await RetrySameLlmBatchChunkAsync(
                    texts, targetLang, sourceLang, noteFailure, ct);
        }
    }

    private async Task<LlmBatchChunkOutcome> RetrySameLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang,
        Action<string> noteFailure, CancellationToken ct)
    {
        try
        {
            var got = await TranslateLlmBatchChunkAsync(texts, targetLang, sourceLang, ct);
            return new LlmBatchChunkOutcome(got, texts.Count, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            noteFailure(ExplainLlmBatchFailure(ex));
            var kind = ClassifyLlmBatchFailure(ex);
            return new LlmBatchChunkOutcome(new string?[texts.Count], 0,
                kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.RateLimited
                    or LlmBatchFailureKind.Configuration);
        }
    }

    private async Task<LlmBatchChunkOutcome> RetrySplitLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang,
        Action<string> noteFailure, CancellationToken ct)
    {
        var result = new string?[texts.Count];
        int recovered = 0;
        bool fatal = false;
        int failedParts = 0, protocolFailedParts = 0;
        int half = texts.Count / 2;
        var parts = new[]
        {
            (Offset: 0, Texts: texts.Take(half).ToArray()),
            (Offset: half, Texts: texts.Skip(half).ToArray()),
        };

        foreach (var part in parts)
        {
            if (fatal) break;
            try
            {
                var got = await TranslateLlmBatchChunkAsync(part.Texts, targetLang, sourceLang, ct);
                for (int i = 0; i < part.Texts.Length; i++)
                    result[part.Offset + i] = got[i];
                recovered += part.Texts.Length;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedParts++;
                noteFailure(ExplainLlmBatchFailure(ex));
                var kind = ClassifyLlmBatchFailure(ex);
                if (kind == LlmBatchFailureKind.Protocol) protocolFailedParts++;
                if (kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.RateLimited
                    or LlmBatchFailureKind.Configuration)
                    fatal = true;
            }
        }

        // 两半都因 400/422 失败且一行都没救回：这不像“某一段太长”，更像模型名/协议/
        // 端点整体不接受请求。继续逐行只会把 8 行放大成十几次必败请求，直接停止同一 LLM。
        // 若只有一半失败，仍保留逐行兜底，避免丢掉另一半可翻译的行。
        if (!fatal && recovered == 0 && failedParts == parts.Length
            && protocolFailedParts == parts.Length)
            fatal = true;

        return new LlmBatchChunkOutcome(result, recovered, fatal);
    }

    /// <summary>只走同一个 LLM 的单行翻译；不进入免费/有道降级链。</summary>
    private Task<string> TranslateSingleLineWithLlmOnlyAsync(
        string text, string targetLang, string? sourceLang, CancellationToken ct,
        bool coverageRepair = false)
        => TranslateWithLlmAsync(text, targetLang, sourceLang, ct, coverageRepair);

    private sealed record ContentRepairResult(int Recovered, string? FirstError);

    /// <summary>一次批量最多补译多少行：模型整屏回显时不能把 40 行放大成 40 次串行请求，
    /// 先补最前面的疑似行，其余如实留在 UnchangedCount/PartialCount 里由状态栏告警。</summary>
    private const int MaxContentRepairLines = 8;

    /// <summary>
    /// 对“非空但内容仍是原文/半译”的行做同 LLM 单行补译。
    /// <para>只处理内容问题，不碰 null 行（null 已由批量/逐行/跨后端链处理）；
    /// 也不跨后端：模型回显原文时偷偷把 OCR 文本发给免费端点，属于另一种产品行为，
    /// 不能藏在“补译”里发生。补不回来就如实计数，让状态栏亮黄灯。</para>
    /// </summary>
    private async Task<ContentRepairResult> RepairContentIssuesAsync(
        IReadOnlyList<string> lines,
        string?[] translated,
        string targetLang,
        string? sourceLang,
        CancellationToken ct)
    {
        if (_config.UseOfflineArgos)
            return new ContentRepairResult(0, null);   // 离线承诺：绝不因内容补译把文本发到网上

        var issues = Enumerable.Range(0, lines.Count)
            .Where(i => !string.IsNullOrWhiteSpace(lines[i])
                && translated[i] is not null
                && TranslationQuality.Assess(lines[i], translated[i], targetLang).IsIssue)
            .Take(MaxContentRepairLines)
            .ToArray();
        if (issues.Length == 0)
            return new ContentRepairResult(0, null);

        int recovered = 0;
        string? firstErrorLocal = null;
        bool fatal = false;
        using var sem = new SemaphoreSlim(2);
        var tasks = new Task[issues.Length];
        for (int t = 0; t < issues.Length; t++)
        {
            int i = issues[t];
            tasks[t] = Task.Run(async () =>
            {
                if (Volatile.Read(ref fatal)) return;
                await sem.WaitAsync(ct);
                try
                {
                    if (Volatile.Read(ref fatal)) return;
                    var repaired = await TranslateSingleLineWithLlmOnlyAsync(
                        lines[i], targetLang, sourceLang, ct, coverageRepair: true);
                    var assessment = TranslationQuality.Assess(lines[i], repaired, targetLang);
                    if (!assessment.IsIssue)
                    {
                        translated[i] = repaired;
                        Interlocked.Increment(ref recovered);
                    }
                    else
                    {
                        // 只记录行号和判定类型，不把 OCR 原文/模型返回写进日志。
                        CoreLog.Warn("Translate",
                            $"内容补译仍未通过：行 {i + 1}（{assessment.Quality}）",
                            LogCategory.Network);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstErrorLocal, ClassifyLlmFailure(ex), null);
                    var kind = ClassifyLlmBatchFailure(ex);
                    if (kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.RateLimited
                        or LlmBatchFailureKind.Configuration)
                        Volatile.Write(ref fatal, true);
                    CoreLog.Warn("Translate", "内容补译失败: " + ex.Message, ex, LogCategory.Network);
                }
                finally
                {
                    sem.Release();
                }
            });
        }
        await Task.WhenAll(tasks);
        return new ContentRepairResult(recovered, firstErrorLocal);
    }

    /// <summary>发一个段落批量请求：paragraphs 给模型完整上下文，lines 保留逐行 id；
    /// 返回与“所有段落行总数”等数量的逐行译文。</summary>
    private async Task<string[]> TranslateLlmParagraphBatchAsync(
        IReadOnlyList<ParagraphGroup> groups, IReadOnlyList<string> allLines,
        string targetLang, string? sourceLang, CancellationToken ct)
    {
        int count = groups.Sum(g => g.Indices.Length);
        var inputLines = new LlmBatchLine[count];
        var paragraphs = new LlmParagraphGroup[groups.Count];
        int lineOffset = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            int[] lineIds = new int[group.Indices.Length];
            for (int k = 0; k < group.Indices.Length; k++)
            {
                int id = lineOffset + k + 1;   // 本次请求内的局部 id，按输出数组回填
                lineIds[k] = id;
                inputLines[lineOffset + k] = new LlmBatchLine(id, allLines[group.Indices[k]]);
            }
            paragraphs[g] = new LlmParagraphGroup(g + 1, group.Text, lineIds);
            lineOffset += group.Indices.Length;
        }

        string inputJson = JsonSerializer.Serialize(
            new LlmParagraphInput(paragraphs, inputLines),
            LlmJsonContext.Default.LlmParagraphInput);

        string systemPrompt = LlmPromptPolicy.BuildParagraphBatchSystemPrompt(
            _config.SystemPrompt, targetLang, count);
        string userPrompt = LlmPromptPolicy.BuildParagraphBatchUserPrompt(
            targetLang, sourceLang, inputJson);
        string model = !string.IsNullOrWhiteSpace(_config.ModelName)
            ? _config.ModelName
            : LlmModelPolicy.DefaultModel;

        var requestBody = new LlmChatRequest(
            Model: model,
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", userPrompt)
            ],
            Temperature: LlmModelPolicy.TemperatureFor(model),
            MaxTokens: 4096
        );

        string content = await SendLlmChatAsync(requestBody, ct);
        var parsed = ParseLlmBatchContent(content, count);
        if (parsed.Lines is null)
            throw new TranslationException(parsed.Error ?? "LLM 段落批量响应不可用");
        ValidateParagraphAlignment(groups, parsed.Lines, allLines);
        return parsed.Lines;
    }

    /// <summary>
    /// 防止模型把整段译文重复塞给组内每一行：源行不同、译文却全相同，多半是“段落压成一条”
    /// 而不是正常的同义重复；这种情况必须退回逐行批量，不能把整段重复显示 N 次。
    /// </summary>
    private static void ValidateParagraphAlignment(
        IReadOnlyList<ParagraphGroup> groups, string[] translations, IReadOnlyList<string> allLines)
    {
        int offset = 0;
        foreach (var group in groups)
        {
            int n = group.Indices.Length;
            if (n > 1)
            {
                int sourceDistinct = group.Indices
                    .Select(i => allLines[i].Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                int translationDistinct = translations
                    .Skip(offset).Take(n)
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (sourceDistinct > 1 && translationDistinct == 1)
                    throw new TranslationException(
                        "LLM 段落响应把整段压成了同一条译文，无法安全拆回原行");
            }
            offset += n;
        }
    }

    /// <summary>发一个 LLM 批量分片，返回与输入等数量的译文；行数/格式不符时抛异常。</summary>
    private async Task<string[]> TranslateLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang, CancellationToken ct)
    {
        string systemPrompt = LlmPromptPolicy.BuildBatchSystemPrompt(
            _config.SystemPrompt, targetLang, texts.Count);

        var input = new LlmBatchLine[texts.Count];
        for (int i = 0; i < texts.Count; i++) input[i] = new LlmBatchLine(i + 1, texts[i]);
        string inputJson = JsonSerializer.Serialize(input, LlmJsonContext.Default.LlmBatchLineArray);

        string userPrompt = LlmPromptPolicy.BuildBatchUserPrompt(targetLang, sourceLang, inputJson);

        string model = !string.IsNullOrWhiteSpace(_config.ModelName)
            ? _config.ModelName
            : LlmModelPolicy.DefaultModel;

        var requestBody = new LlmChatRequest(
            Model: model,
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", userPrompt)
            ],
            Temperature: LlmModelPolicy.TemperatureFor(model),
            MaxTokens: 4096
        );

        string content = await SendLlmChatAsync(requestBody, ct);
        var parsed = ParseLlmBatchContent(content, texts.Count);
        if (parsed.Lines is null)
            throw new TranslationException(parsed.Error ?? "LLM 批量响应不可用");
        return parsed.Lines;
    }

    private static string ExplainLlmBatchFailure(Exception ex) => ex switch
    {
        LlmHttpException => ClassifyLlmFailure(ex),
        HttpRequestException => ClassifyLlmFailure(ex),
        TranslationException => ex.Message,
        TaskCanceledException =>
            $"自定义 LLM 批量超时（> {LlmTextRequestTimeout.TotalSeconds:F0} 秒）",
        _ => $"自定义 LLM 批量失败: {ex.GetType().Name}: {ex.Message}"
    };

    private bool HasNonLlmBackend() =>
        (!string.IsNullOrWhiteSpace(_config.YoudaoAppKey)
         && !string.IsNullOrWhiteSpace(_config.YoudaoAppSecret))
        || _config.AllowUnofficialTranslateEndpoints;

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
            
            // 智能限流：防止短时间内大量请求触发风控
            await WaitIfRateLimitedAsync(linked.Token);
            
            using var request = new HttpRequestMessage(HttpMethod.Post, YoudaoApiUrl)
            {
                // ⚠ 必须写全 System.Text.Encoding：本文件命名空间为 TrueToneCap.Core.Services，
                // 但项目内存在 TrueToneCap.Core.Encoding 命名空间，简写 Encoding 会被解析到它。
                Content = new StringContent(qs, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };

            using var response = await _http.SendAsync(request, linked.Token);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户关窗/主动取消：不是"有道超时"，不能吞掉后继续降级到免费端点。
            throw;
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
    internal string? ParseYoudaoResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _lastBackendNote = "有道响应不是 JSON 对象（响应形态与文档不符）";
                return null;
            }

            // errorCode 在开放平台返回的是字符串（如 "0"），兼容数值形态
            if (!root.TryGetProperty("errorCode", out var ec))
            {
                _lastBackendNote = "有道响应缺少 errorCode（响应形态与文档不符）";
                return null;
            }
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
                {
                    // 只接受字符串项：文档形状是 string[]；null/数字/对象混进来时
                    // GetString() 会抛 InvalidOperationException，不能让它变成“有道异常”。
                    if (item.ValueKind != JsonValueKind.String) continue;
                    sb.Append(item.GetString());
                }
                var result = sb.ToString();
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }

            // errorCode 是 0 却没有可用译文 —— 形态与文档不符，也要给出原因而不是静默降级
            _lastBackendNote = "有道 errorCode 0 但响应里没有非空 translation 数组（响应形态与文档不符）";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _lastBackendNote = "有道响应不是合法 JSON 对象（" + ex.GetType().Name + "）";
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
                
                // 智能限流：防止短时间内大量请求触发风控
                await WaitIfRateLimitedAsync(linked.Token);
                
                var response = await _http.GetStringAsync(url, linked.Token);
                var result = ParseGoogleResponse(response);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    NoteSuccess(label);
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消必须原样上抛；否则调用方会把"关窗"当成"Google 超时"再继续兜底。
                throw;
            }
            catch (OperationCanceledException)
            {
                _lastBackendNote = $"{label} 请求超时（>5 秒）";
                CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            }
            catch (HttpRequestException ex)
            {
                _lastBackendNote = $"{label} 请求失败（{ex.GetType().Name}）";
                CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            }
            catch (Exception ex)
            {
                _lastBackendNote = $"{label} 解析或请求异常（{ex.GetType().Name}）";
                CoreLog.Debug("Translate", _lastBackendNote, LogCategory.Network);
            }
        }

        return null;
    }

    internal static string? ParseGoogleResponse(string json)
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
                        if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
                            && s[0].ValueKind == JsonValueKind.String)
                        {
                            var t = s[0].GetString();
                            if (!string.IsNullOrWhiteSpace(t)) sb.Append(t);
                        }
                    }
                    var r = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(r)) return r;
                }
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sentences", out var s2)
                && s2.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var s in s2.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.Object
                        && s.TryGetProperty("trans", out var t)
                        && t.ValueKind == JsonValueKind.String)
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
            await WaitIfRateLimitedAsync(ct);
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

                await WaitIfRateLimitedAsync(linked.Token);
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
            await WaitIfRateLimitedAsync(linked.Token);
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
            
            // 智能限流：防止短时间内大量请求触发风控
            await WaitIfRateLimitedAsync(linked.Token);
            
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
        string? sourceLang, CancellationToken ct, bool coverageRepair = false)
    {
        string systemPrompt = coverageRepair
            ? LlmPromptPolicy.BuildCoverageRepairSystemPrompt(_config.SystemPrompt, targetLang)
            : LlmPromptPolicy.BuildSingleSystemPrompt(_config.SystemPrompt, targetLang);
        string userPrompt = LlmPromptPolicy.BuildSingleUserPrompt(targetLang, sourceLang, text);

        string model = !string.IsNullOrWhiteSpace(_config.ModelName)
            ? _config.ModelName
            : LlmModelPolicy.DefaultModel;

        // 使用源生成器构建请求体（AOT 兼容，替代匿名类型 + JsonContent.Create）
        var requestBody = new LlmChatRequest(
            Model: model,
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", userPrompt)
            ],
            Temperature: LlmModelPolicy.TemperatureFor(model),
            MaxTokens: 4096
        );

        string content = await SendLlmChatAsync(requestBody, ct);

        // 先验内容再记归因：RequireTranslatedContent 对空/多行/解释性译文要抛，归因不能抢在它前面写"成功"
        var llmDone = RequireTranslatedContent(content);
        NoteSuccess("自定义 LLM");
        return llmDone;
    }

    /// <summary>
    /// 发一次 OpenAI 兼容的 chat/completions 并取出 content。
    /// <para>P0（2026-11-16）：限流必须在<b>发送前</b>执行，LLM 不再和免费短文本共用 15s 客户端；
    /// 用户关窗导致的取消要原样上抛，不能被当成"LLM 超时后降级"。</para>
    /// </summary>
    private async Task<string> SendLlmChatAsync(LlmChatRequest requestBody, CancellationToken ct)
    {
        string endpoint = ChatCompletionsUrl(_config.ApiEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody, LlmJsonContext.Default.LlmChatRequest)
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(LlmTextRequestTimeout);
        try
        {
            await WaitIfRateLimitedAsync(linkedCts.Token);
            using var response = await s_llmHttp.SendAsync(request, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                int? retryAfter = null;
                var retryHeader = response.Headers.RetryAfter;
                if (retryHeader?.Delta is { } delta)
                    retryAfter = (int)Math.Clamp(delta.TotalSeconds, 1, 60);
                else if (retryHeader?.Date is { } date)
                    retryAfter = (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 1, 60);

                string errBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
                throw new LlmHttpException((int)response.StatusCode, retryAfter, errBody);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linkedCts.Token));
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户关窗/取消：不是 LLM 失败，交给 UI 静默收尾
        }
        catch (OperationCanceledException)
        {
            throw new TaskCanceledException(
                $"自定义 LLM 请求超过 {LlmTextRequestTimeout.TotalSeconds:F0} 秒没有返回");
        }
    }

    /// <summary>LLM 返回非 2xx 时保留状态码与 Retry-After，供批量回退分类使用。</summary>
    internal sealed class LlmHttpException : Exception
    {
        public int StatusCode { get; }
        public int? RetryAfterSeconds { get; }

        public LlmHttpException(int statusCode, int? retryAfterSeconds, string body)
            : base($"HTTP {statusCode}")
        {
            StatusCode = statusCode;
            RetryAfterSeconds = retryAfterSeconds;
            _ = body;   // 响应体可能含服务商内部信息，不进异常文本；只保留状态码供分类/UI
        }
    }

    /// <summary>
    /// HTTP 200 而 content 为 JSON null / 空串 / 全空白 ⇒ 这次【没有译文】，不是"低质译文"。
    /// <para>原写法 <c>content?.Trim() ?? text</c> 会把用户自己送出去的源文本原样当作译文返回：
    /// 界面上看到的"那句英文没被翻译"就是它本身，日志里零痕迹 —— 比报错更坏，因为它长得像成功。
    /// 返回空串也只是换一种静默，所以这里抛异常交给 <see cref="TranslateAsync"/> 既有的
    /// <c>catch (Exception)</c>：换下一个后端，并由 <see cref="ClassifyLlmFailure"/> 把这条事实
    /// 写进 _lastBackendNote 与 Warn 日志（该 catch 无类型过滤，任何异常都不会冒到界面变成原始异常文本）。</para>
    /// <para>2026-11-16 起不只判空：自动剥掉代码块/引号/编号/“Translation:”前缀，
    /// 并把多行、解释性文字、拒答语判成失败，避免 HTTP 200 的坏译文被当成成功回填。</para>
    /// </summary>
    internal static string RequireTranslatedContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                "端点回了 HTTP 200，但 choices[0].message.content 是空的（JSON null 或空白）⇒ 这次没有返回译文");

        var sanitized = SanitizeSingleLine(content);
        if (sanitized is null)
            throw new InvalidOperationException(
                "端点回了 HTTP 200，但 content 不是单行译文（含多行、解释性文字或拒答语）⇒ 这次不算翻译成功");
        return sanitized;
    }

    /// <summary>LLM 批量响应的解析结果；<c>Lines</c> 为 null 时 <c>Error</c> 必须给出人话原因。</summary>
    internal sealed record LlmBatchParseResult(string[]? Lines, string? Error);

    /// <summary>
    /// 解析 LLM 批量响应。接受四种常见形状：等长字符串数组、<c>{"translations":[...]}</c>、
    /// 带 <c>id</c> 的对象数组、按编号分行的纯文本。**任何形状最终都必须得到 expectedCount 条译文**；
    /// 单条译文被模型折成多行时会在同一条内合并，编号行允许无编号续行，漏行/多行仍整批退回。
    /// 不允许“按顺序硬套”造成错位覆盖。
    /// </summary>
    internal static LlmBatchParseResult ParseLlmBatchContent(string? raw, int expectedCount)
    {
        if (expectedCount <= 0)
            return new LlmBatchParseResult(null, "批量输入行数为 0，不能解析");
        if (string.IsNullOrWhiteSpace(raw))
            return new LlmBatchParseResult(null, "LLM 批量响应为空");

        string text = StripCodeFence(raw.Trim());
        string? jsonText = ExtractJsonPayload(text);
        if (jsonText is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var parsed = ParseJsonBatchRoot(doc.RootElement, expectedCount);
                if (parsed.Lines is not null) return parsed;
                // JSON 形状不对时继续尝试“编号行”纯文本；若也不成立，报 JSON 那条更具体的原因
                var byLines = ParseNumberedBatchLines(text, expectedCount);
                return byLines.Lines is not null ? byLines : parsed;
            }
            catch (JsonException ex)
            {
                var byLines = ParseNumberedBatchLines(text, expectedCount);
                if (byLines.Lines is not null) return byLines;
                return new LlmBatchParseResult(null,
                    "LLM 批量响应不是合法 JSON（" + ex.GetType().Name + "），也不是等数量的编号行");
            }
        }

        return ParseNumberedBatchLines(text, expectedCount);
    }

    private static LlmBatchParseResult ParseJsonBatchRoot(JsonElement root, int expectedCount)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return ParseJsonBatchArray(root, expectedCount);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
                return ParseJsonBatchArray(tr, expectedCount);
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                return ParseJsonBatchArray(result, expectedCount);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return ParseJsonBatchArray(data, expectedCount);
            return ParseJsonIdMap(root, expectedCount);
        }

        return new LlmBatchParseResult(null, "LLM 批量响应根节点既不是数组也不是对象");
    }

    private static LlmBatchParseResult ParseJsonBatchArray(JsonElement arr, int expectedCount)
    {
        int got = arr.GetArrayLength();
        if (got != expectedCount)
            return new LlmBatchParseResult(null,
                $"LLM 批量响应行数 {got} ≠ 输入 {expectedCount}（可能合并或漏行）");

        // 对象数组：必须每项带 id。模型若返回对象数组却漏掉 id，按位置映射会在顺序变化时静默错行；
        // 这类响应直接拒收，让上层走拆片/逐行，而不是把错误译文贴到别的 UI 行上。
        bool allObjects = true, allHaveId = true;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) { allObjects = false; break; }
            if (!el.TryGetProperty("id", out _)) { allHaveId = false; break; }
        }

        if (allObjects)
        {
            if (!allHaveId)
                return new LlmBatchParseResult(null,
                    "LLM 批量响应的对象数组缺少 id；不能按位置回填（可能静默错行）");
            var map = new Dictionary<int, string>();
            int itemIndex = 0;
            foreach (var el in arr.EnumerateArray())
            {
                int? id = TryReadInt(el.TryGetProperty("id", out var idEl) ? idEl : default);
                if (id is null || id.Value < 1 || id.Value > expectedCount)
                    return new LlmBatchParseResult(null, $"LLM 批量响应第 {itemIndex + 1} 项的 id 非法");
                if (map.ContainsKey(id.Value))
                    return new LlmBatchParseResult(null, $"LLM 批量响应存在重复 id={id}");
                string? value = ReadJsonTranslationText(el);
                var line = SanitizeSingleLine(value);
                if (line is null)
                    return new LlmBatchParseResult(null, $"LLM 批量响应 id={id} 的译文为空/多行/解释性文字");
                map[id.Value] = line;
                itemIndex++;
            }
            if (map.Count != expectedCount)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应有效 id 数 {map.Count} ≠ 输入 {expectedCount}");
            for (int i = 1; i <= expectedCount; i++)
                if (!map.ContainsKey(i))
                    return new LlmBatchParseResult(null, $"LLM 批量响应缺少 id={i}");
            return new LlmBatchParseResult(
                Enumerable.Range(1, expectedCount).Select(i => map[i]).ToArray(), null);
        }

        // 字符串数组：只能按顺序，前提仍是等数量。混合类型或对象数组一律不走位置回填。
        var lines = new string[expectedCount];
        int index = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应第 {index + 1} 行不是字符串（对象数组必须带 id 才能回填）");
            string? value = el.GetString();
            var line = SanitizeSingleLine(value);
            if (line is null)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应第 {index + 1} 行不是可用的单行译文");
            lines[index++] = line;
        }
        return new LlmBatchParseResult(lines, null);
    }

    private static LlmBatchParseResult ParseJsonIdMap(JsonElement obj, int expectedCount)
    {
        var map = new Dictionary<int, string>();
        foreach (var prop in obj.EnumerateObject())
        {
            int? id = int.TryParse(prop.Name, out var n) ? n : null;
            if (id is null || id.Value < 1 || id.Value > expectedCount) continue;   // 非编号字段（如 note）不参与回填
            if (map.ContainsKey(id.Value))
                return new LlmBatchParseResult(null, $"LLM 批量响应存在重复 id={id}");
            string? value = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.ValueKind == JsonValueKind.Object ? ReadJsonTranslationText(prop.Value) : null;
            var line = SanitizeSingleLine(value);
            if (line is null)
                return new LlmBatchParseResult(null, $"LLM 批量响应 id={id} 的译文为空/多行/解释性文字");
            map[id.Value] = line;
        }

        if (map.Count != expectedCount)
            return new LlmBatchParseResult(null,
                $"LLM 批量响应有效 id 数 {map.Count} ≠ 输入 {expectedCount}");
        for (int i = 1; i <= expectedCount; i++)
            if (!map.ContainsKey(i))
                return new LlmBatchParseResult(null, $"LLM 批量响应缺少 id={i}");
        return new LlmBatchParseResult(
            Enumerable.Range(1, expectedCount).Select(i => map[i]).ToArray(), null);
    }

    private static string? ReadJsonTranslationText(JsonElement obj)
    {
        // 先读 translation：若模型把输入对象原样带回来，里面同时有 text（源文）和 translation，
        // 旧顺序会把源文当成译文。value 是部分网关的别名；text 只作为最后兜底。
        foreach (var key in new[] { "translation", "value", "text" })
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static int? TryReadInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>编号行前缀：1. / 1、 / 1) / 1：；但 1.2.3 的 1. 后跟数字，不当编号剥掉。</summary>
    private static readonly string s_numberedBatchLinePrefix =
        @"^\s*(\d+)\s*(?:[、)：:]\s*|\.\s*(?=[^\d]))";

    private static readonly Regex s_numberedBatchLine = new(
        s_numberedBatchLinePrefix + @"(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static LlmBatchParseResult ParseNumberedBatchLines(string text, int expectedCount)
    {
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawLines.Length == 0)
            return new LlmBatchParseResult(null, "LLM 批量响应为空");

        // 有编号时按 id 聚合续行：模型把一条译文折行时，续行没有编号，必须归到上一条。
        bool hasAnyNumbered = rawLines.Any(l => s_numberedBatchLine.IsMatch(l));
        var numbered = new Dictionary<int, List<string>>();
        int currentId = 0;
        int numberedCount = 0;
        bool sawNumbered = false;
        foreach (string line in rawLines)
        {
            var match = s_numberedBatchLine.Match(line);
            if (match.Success)
            {
                sawNumbered = true;
                int id;
                if (!int.TryParse(match.Groups[1].Value, out id))
                    return new LlmBatchParseResult(null, "LLM 批量响应存在非法编号");
                if (id < 1 || id > expectedCount)
                    return new LlmBatchParseResult(null,
                        $"LLM 批量响应编号 id={id} 超出输入范围 1..{expectedCount}");
                if (numbered.ContainsKey(id))
                    return new LlmBatchParseResult(null,
                        $"LLM 批量响应存在重复 id={id}");
                currentId = id;
                numberedCount++;
                numbered[id] = new List<string>();
                string rest = match.Groups["text"].Value.Trim();
                if (rest.Length > 0) numbered[id].Add(rest);
                continue;
            }

            if (sawNumbered)
            {
                // 编号之后的非编号行是续行；但模型常见的“Hope this helps”式尾巴不并进译文。
                if (IsMetaFooterLine(line)) continue;
                numbered[currentId].Add(line);
                continue;
            }

            // 完全没有编号时走下面的等数量逐行分支；有编号时，第一编号之前的正文不能被当前言吞掉。
            if (!hasAnyNumbered) continue;
            if (!IsBatchPreambleLine(line))
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应在第一个编号前出现正文/无法识别的内容：{Truncate(line, 24)}");
        }

        if (!sawNumbered)
        {
            // 没有编号的旧格式：允许前后各一句元话术，但正文仍必须等数量逐行。
            var meaningful = rawLines;
            while (meaningful.Length > expectedCount && IsBatchPreambleLine(meaningful[0]))
                meaningful = meaningful[1..];
            while (meaningful.Length > expectedCount && IsMetaFooterLine(meaningful[^1]))
                meaningful = meaningful[..^1];
            if (meaningful.Length != expectedCount)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应行数 {meaningful.Length} ≠ 输入 {expectedCount}（可能合并或漏行）");

            var plainLines = new string[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                var line = SanitizeSingleLine(meaningful[i]);
                if (line is null)
                    return new LlmBatchParseResult(null,
                        $"LLM 批量响应第 {i + 1} 行不是可用的单行译文");
                plainLines[i] = line;
            }
            return new LlmBatchParseResult(plainLines, null);
        }

        if (numberedCount != expectedCount || numbered.Count != expectedCount)
            return new LlmBatchParseResult(null,
                $"LLM 批量响应有效编号数 {numbered.Count} ≠ 输入 {expectedCount}（可能漏行/合并）");
        for (int i = 1; i <= expectedCount; i++)
            if (!numbered.ContainsKey(i))
                return new LlmBatchParseResult(null, $"LLM 批量响应缺少 id={i}");

        var lines = new string[expectedCount];
        for (int i = 1; i <= expectedCount; i++)
        {
            var joined = JoinWrappedTranslationLines(numbered[i]);
            var line = SanitizeSingleLine(joined);
            if (line is null)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应 id={i} 的译文为空/解释性文字/格式不可用");
            lines[i - 1] = line;
        }
        return new LlmBatchParseResult(lines, null);
    }

    private static bool IsBatchPreambleLine(string line)
    {
        string lower = line.Trim().ToLowerInvariant();
        if (lower.Length == 0) return true;
        if (IsMetaPreambleLine(line)) return true;
        return Regex.IsMatch(lower,
            @"^(?:(?:sure|okay|ok)[,.!]?\s*)?(?:here\s+(?:is|are)|here's|below\s+is)\s+(?:the\s+)?translations?\b")
            || line.Contains("以下是翻译") || line.Contains("译文如下") || line.Contains("翻译如下");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? ExtractJsonPayload(string text)
    {
        int firstArray = text.IndexOf('[');
        int firstObject = text.IndexOf('{');
        if (firstArray >= 0 && (firstObject < 0 || firstArray <= firstObject))
        {
            int last = text.LastIndexOf(']');
            if (last > firstArray) return text[firstArray..(last + 1)];
        }
        if (firstObject >= 0)
        {
            int last = text.LastIndexOf('}');
            if (last > firstObject) return text[firstObject..(last + 1)];
        }
        return null;
    }

    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s.Trim('`').Trim();
        string body = s[(firstNewline + 1)..].Trim();
        if (body.EndsWith("```", StringComparison.Ordinal)) body = body[..^3].Trim();
        return body;
    }

    /// <summary>
    /// 把模型输出收敛成“一条可覆盖 OCR 行的译文”：去代码块、引号、编号、Translation: 前缀；
    /// 解释性文字/拒答语仍拒收。模型把一条译文折成多行时按语言规则合并成一行，
    /// 不再因为“多行”整批判失败；多个编号项则属于批量形状，仍拒收。
    /// </summary>
    internal static string? SanitizeSingleLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        string s = StripCodeFence(content.Trim());

        // 先去掉常见的“以下是翻译”式前缀；\s* 允许模型在冒号后换行。
        s = Regex.Replace(s,
            @"^(?:(?:sure|okay|ok)[,.!]?\s*)?(?:" +
            @"here\s+(?:is|are)\s+(?:the\s+)?translations?|here's\s+the\s+translation|" +
            @"translation|译文|翻译|output)\s*[：:]\s*",
            "", RegexOptions.IgnoreCase);
        s = s.Trim();

        // 网关常把单行翻译包成 ["译文"] / {"translation":"译文"}。
        // JSON 字符串内部的换行必须先拆包再合并，不能按原文直接 split。
        var (isJsonContainer, unwrapped) = TryUnwrapSingleJsonTranslation(s);
        if (isJsonContainer)
        {
            if (unwrapped is null) return null;
            s = unwrapped.Trim();
        }

        string normalizedNewlines = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalizedNewlines
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 多个编号项不是“一条译文折行”，而是批量/多行列表，单行入口不能把它们拼在一起。
        if (rawLines.Length > 1
            && rawLines.Count(l => s_numberedBatchLine.IsMatch(l)) > 1)
            return null;

        if (rawLines.Length > 1)
        {
            // 容忍模型在译文前后加一句“Sure / Hope this helps”，只保留真正的译文行。
            while (rawLines.Length > 1 && IsMetaPreambleLine(rawLines[0]))
                rawLines = rawLines[1..];
            while (rawLines.Length > 1 && IsMetaFooterLine(rawLines[^1]))
                rawLines = rawLines[..^1];
            if (rawLines.Any(IsMetaCommentaryLine))
                return null;
            s = JoinWrappedTranslationLines(rawLines);
        }
        else
        {
            s = normalizedNewlines.Trim();
        }

        // 只去掉“一个”开头的编号；1.2.3 这类版本号不能被当成编号剥成 2.3。
        s = Regex.Replace(s, s_numberedBatchLinePrefix, "");
        s = s.Trim();

        if ((s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal)
             || s.StartsWith("“", StringComparison.Ordinal) && s.EndsWith("”", StringComparison.Ordinal)
             || s.StartsWith("'", StringComparison.Ordinal) && s.EndsWith("'", StringComparison.Ordinal))
            && s.Length >= 2)
            s = s[1..^1].Trim();

        if (s.Length == 0) return null;
        if (s.Contains('\n') || s.Contains('\r')) return null;

        if (IsMetaCommentaryLine(s)) return null;
        return s;
    }

    /// <summary>把一条译文被模型折成的多行合并回一行；中文/日文之间不插空格，
    /// 英文/韩文/中英交界处保留一个空格。连字符、斜杠断行不插空格。</summary>
    private static string JoinWrappedTranslationLines(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }

            char prev = sb[^1];
            char next = line[0];
            if (NeedsSpaceBetween(prev, next)) sb.Append(' ');
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    private static bool NeedsSpaceBetween(char prev, char next)
    {
        if (char.IsWhiteSpace(prev) || char.IsWhiteSpace(next)) return false;
        // 英文跨行断词/路径断行
        if (prev is '-' or '/' or '\\' or '_' or '@') return false;
        // 中文/日文行内折行：两侧都是汉字/假名/中文标点时直接接上
        if (IsCjkNoSpace(prev) && IsCjkNoSpace(next)) return false;
        return true;
    }

    private static bool IsCjkNoSpace(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\u3040' && c <= '\u30FF')
        || (c >= '\u31F0' && c <= '\u31FF')
        || "，。！？；：、）》】”’…".Contains(c);

    /// <summary>单行入口的元话术前缀/拒答检测；合法 UI 文案（Note: takes effect…）不在这里误杀。</summary>
    private static bool IsMetaCommentaryLine(string s)
    {
        string lower = s.Trim().ToLowerInvariant();
        if (lower.Length == 0) return false;

        if (Regex.IsMatch(lower, @"^as an ai(?: language model| assistant)?[.!]?$")
            || Regex.IsMatch(lower, @"^i(?:'m| am) sorry[.!]?$")
            || Regex.IsMatch(lower, @"^i cannot[.!]?$")
            || Regex.IsMatch(lower,
                @"\b(?:i cannot|i can't|i am unable to|i'm unable to)\s+(?:assist|help|translate|comply|continue|provide|do that|fulfill)\b")
            || Regex.IsMatch(lower,
                @"^(?:(?:sure|okay|ok)[,.!]?\s*)?(?:here\s+(?:is|are)|here's|below\s+is)\s+(?:the\s+)?translations?\b")
            || (lower.StartsWith("note:", StringComparison.Ordinal)
                && Regex.IsMatch(lower, @"(?:the original|source text|the translation|translated text|tone|wording|phrasing|literal meaning)"))
            || (lower.StartsWith("explanation:", StringComparison.Ordinal)
                && Regex.IsMatch(lower, @"(?:the original|source text|the translation|translated text|tone|wording|phrasing|literal meaning)"))
            || s.Contains("以下是翻译")
            || s.Contains("译文如下")
            || s.Contains("翻译如下"))
            return true;
        return false;
    }

    private static bool IsMetaPreambleLine(string s)
    {
        string lower = s.Trim().ToLowerInvariant();
        return Regex.IsMatch(lower, @"^(?:sure|okay|ok|certainly|of\s+course)(?:[,.!]|\s|$)")
            || Regex.IsMatch(lower, @"^(?:here|below)\s+(?:is|are)\b");
    }

    private static bool IsMetaFooterLine(string s)
    {
        string lower = s.Trim().ToLowerInvariant();
        return Regex.IsMatch(lower,
            @"^(?:hope\s+this\s+helps|i\s+hope\s+this\s+helps|"
            + @"(?:please\s+)?let\s+me\s+know\s+if|feel\s+free\s+to|"
            + @"if\s+you\s+(?:need|have)\s+(?:any\s+)?(?:questions?|help)|any\s+questions\b)");
    }

    /// <summary>把单行响应外层的一元 JSON 容器拆掉。
    /// <para>返回 <c>IsJsonContainer=false</c> 表示“看起来不是 JSON”（例如占位符 {name}），
    /// 调用方保留原串；<c>IsJsonContainer=true, Value=null</c> 表示“确实是 JSON 容器但形状
    /// 不能当单行译文”，调用方必须拒收。</para></summary>
    private static (bool IsJsonContainer, string? Value) TryUnwrapSingleJsonTranslation(string s)
    {
        char first = s.Length > 0 ? s[0] : '\0';
        char last = s.Length > 0 ? s[^1] : '\0';
        if (!((first == '[' && last == ']') || (first == '{' && last == '}')))
            return (false, null);

        try
        {
            using var doc = JsonDocument.Parse(s);
            JsonElement value = doc.RootElement;
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() != 1) return (true, null);
                value = value[0];
            }
            if (value.ValueKind == JsonValueKind.String)
                return (true, value.GetString());

            if (value.ValueKind != JsonValueKind.Object)
                return (true, null);
            foreach (string key in new[] { "translation", "translated", "value", "text" })
            {
                if (value.TryGetProperty(key, out var prop)
                    && prop.ValueKind == JsonValueKind.String)
                    return (true, prop.GetString());
            }
            return (true, null);
        }
        catch (JsonException)
        {
            // {name} / {0} 这类占位符不是合法 JSON，不能被当成“畸形 JSON 译文”拒收。
            return (false, null);
        }
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
        int expectedLineCount, CancellationToken ct = default,
        IReadOnlyList<string>? sourceLines = null)
    {
        LastContentRetryCount = 0;
        if (!VisionConfigured(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        // OCR 一行都没有时，视觉模型再准也没有可回填的目标；提前拒绝，别先发一次整图请求。
        if (expectedLineCount <= 0)
            throw new TranslationException(
                $"视觉翻译要求 OCR 行数至少为 1（当前 {expectedLineCount}）—— 没有可回填的目标行。");

        if (png.Length > VisionPngMaxBytes)
            throw new TranslationException(
                $"截图 PNG 为 {png.Length / 1024.0 / 1024.0:F1} MB，超过视觉端点单次上限 " +
                $"{VisionPngMaxBytes / 1024 / 1024} MB —— 请缩小选区，或把翻译模式切回 LLM（文字走 OCR 再翻译）。");

        string endpoint = ChatCompletionsUrl(_config.ApiEndpoint);

        string model = !string.IsNullOrWhiteSpace(_config.ModelName) ? _config.ModelName : LlmModelPolicy.DefaultModel;

        // 输出契约交给 LlmPromptPolicy（精确行数/逐行/注入防护/保留规则）。
        // OCR 的文字内容仍刻意【不】进 prompt，否则模型会照着文本翻而不是读图，
        // 这条链路就退化成文本路了；只用 OCR 行数作为对齐锚点。
        string instruction = LlmPromptPolicy.BuildVisionInstruction(
            _config.SystemPrompt, targetLang, expectedLineCount);

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
            Temperature: LlmModelPolicy.TemperatureFor(model),
            MaxTokens: 4096);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody, LlmJsonContext.Default.LlmVisionRequest)
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

        string content;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(VisionRequestTimeout);
        try
        {
            await WaitIfRateLimitedAsync(linkedCts.Token);
            using var response = await s_visionHttp.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linkedCts.Token));
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 端点太慢，不是用户关窗：必须留下一条可读原因，否则调用方只看到"取消"、界面卡在"翻译中"
            _lastBackendNote = $"视觉请求超过 {VisionRequestTimeout.TotalSeconds:F0} 秒没有返回";
            CoreLog.Warn("Translate", "视觉路失败: " + _lastBackendNote, LogCategory.Network);
            throw new TranslationException(
                _lastBackendNote + "。整图直传的耗时随模型与图片大小增长，可缩小选区、换更快的模型，" +
                "或把翻译模式切回 LLM（文字路）。");
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

        // 视觉路只校验“行数对不对”，模型仍可能把某几行原样抄回来（尤其低对比度/按钮短语）。
        // OCR 源文在这里可用，所以做与文本批量同一套逐行内容判定 + 同 LLM 单行补译；
        // 补不回来由 UI 的状态栏按逐行质量报告亮黄灯，而不是把整批算成功。
        if (sourceLines is not null && sourceLines.Count == expectedLineCount)
        {
            var repair = await RepairContentIssuesAsync(
                sourceLines, mapped, targetLang, null, ct);
            LastContentRetryCount = repair.Recovered;
            if (repair.FirstError is not null)
            {
                _lastBackendNote ??= repair.FirstError;
                CoreLog.Warn("Translate", "视觉路内容补译失败: " + repair.FirstError, LogCategory.Network);
            }
        }

        CoreLog.Info("Translate",
            $"视觉路成功: {expectedLineCount} 行" +
            (LastContentRetryCount > 0 ? $"（内容补译 {LastContentRetryCount} 行）" : ""),
            LogCategory.Network);
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
    /// 把模型返回的整段文本切成 expectedLineCount 行。正好等行，或编号+续行能恢复出等数量时返回；
    /// 其余行数偏差返回 null。
    /// <para>容忍 "1. " / "2、" / "3)" 这类编号前缀：模型即使被要求"不要编号"也常会带上，
    /// 去掉编号不改变行数，因此可以安全容忍；无编号且行数偏差无法对齐仍判为不可用。</para>
    /// </summary>
    internal static string[]? MapVisionLines(string? raw, int expectedLineCount)
    {
        if (string.IsNullOrWhiteSpace(raw) || expectedLineCount <= 0) return null;
        var mapped = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSingleLine)
            .ToArray();
        if (mapped.Length == expectedLineCount && mapped.All(l => l is not null))
            return mapped.Select(l => l!).ToArray();

        // 模型在每条译文前带 1./2. 编号、并把同一条译文折成续行时，按编号聚合；
        // 这比“行数不等于 N 就整批放弃”更接近用户要的结果，也保留 id 对齐不串行。
        var numbered = ParseNumberedBatchLines(raw, expectedLineCount);
        return numbered.Lines;
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
        /// 离线翻译模式（2026-11-11 离线翻译包方案）：走 <see cref="OfflineArgosEngine"/>
        /// 的本地 Argos 模型，文本不出本机。
        /// <para>与 设置 → 翻译模式 的 <c>TranslationMode == "Offline"</c> 对应，
        /// 形状与 UseCustomLlm/UseVision 一致（持久化键是字符串模式，运行时是布尔）。</para>
        /// <para>失败语义：离线路<b>不静默回退</b>在线后端 —— 用户选离线通常就是为了
        /// 隐私，回退等于背着用户把文字发出去。失败原因原样抛给 UI。</para>
        /// </summary>
        public bool UseOfflineArgos { get; set; }

        /// <summary>离线翻译的源语言提示（设置里的 OCR 语言 Id，如 "zh-en" / "en-US"）。
        /// 经 <see cref="OfflineArgosEngine.NormalizeLang"/> 归一成 Argos 语言码；
        /// 空/不认识时按 "en" 处理。</summary>
        public string? OfflineSourceLangHint { get; set; }

    /// <summary>
    /// 视觉翻译模式：整张截图直传给支持图片的 OpenAI 兼容端点，一步完成读图+翻译，
    /// 不经 OCR 文本（由 <see cref="TranslateImageWithVisionAsync"/> 实现）。
    /// <para>要求 <see cref="UseCustomLlm"/> 与 <see cref="ApiEndpoint"/> 同时成立，
    /// 且该模型必须支持图片输入 —— 否则端点会回 400，<see cref="ClassifyLlmFailure"/> 会把这句话说明白。</para>
    /// </summary>
    public bool UseVision { get; set; }
    public string ApiEndpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = LlmModelPolicy.DefaultModel;
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
//   · 提供方清单真正生效的地方是 MainWindow.xaml 的 LlmProviderCbo（2026-11-16 起 14 项 + 自定义）
//     与 MainWindow.OcrSettings.cs 的 OnLlmProviderChanged（tag → endpoint/默认模型）；
//   · 模型清单真正生效的地方是 LlmModelCbo 的 ComboBoxItem.Tag（2026-11-16 起 50 个平台模型 + 自定义）；
//   · LlmProviders 全仓唯一的消费者是 OcrServiceTests 的三条断言 ⇒ 门禁绿灯覆盖的是一个
//     用户永远碰不到的表，而三处真表之间的漂移（如 Gemini 在 XAML 是 gemini-2.0-flash、
//     在 LlmProviders 是 gemini-3.5-flash）没有任何判据在看。
// 现在的判据改钉这三处活表是否闭合，见 OcrServiceTests 的「LLM 提供商/模型清单」一节
// （Test 工程只引用 Core，枚举不了 WinUI 控件，所以判据只能读源文件文本）。

