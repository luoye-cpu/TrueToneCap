// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationService.cs
// 多后端翻译服务：LLM (OpenAI 兼容) → 有道官方开放平台 → 免费公共端点（Edge / 必应 / MyMemory / Google，默认启用）
// 支持: DeepSeek V4 Flash / GLM-4.7-Flash / GPT-4o-mini / GPT-4.1-mini / 任意 OpenAI 兼容端点

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
        // 离线模式单独成句、且必须放在最前：TranslateAsync 里离线路在任何在线后端之前
        // 直接返回，AllowUnofficialTranslateEndpoints 默认 true 也不改变这个事实 ——
        // 把免费端点列成"已启用"是在撒谎，按"都没返回结果"的网络口径说话更是误导。
        // 它缺的不是"配置"，是语言包或 Python 运行环境。
        // 「离线翻译包」「安装运行环境」是界面上真实存在的控件文字（判据钉在 UI 源码里）。
        if (c.UseOfflineArgos)
            return "离线翻译已启用，但本地模型或运行环境未就绪：到 设置 → OCR/翻译 的「离线翻译包」卡片下载语言包；"
                 + "首次使用还需 Python 运行环境，可点该卡片里的「安装运行环境」。";

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
        LlmHttpException h => h.StatusCode switch
        {
            401 => $"自定义 LLM 返回 401（{h.Message}）—— 是 API 密钥不对或该模型未开通，不是网络问题",
            403 => $"自定义 LLM 返回 403（{h.Message}）—— 密钥没有访问该模型的权限，不是网络问题",
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
        if (allowLlm && _config.UseCustomLlm && !string.IsNullOrEmpty(_config.ApiEndpoint))
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

    /// <summary>批量翻译结果。<see cref="FallbackCount"/> 是"批量没有交出、改走逐行"的行数，
    /// 与 <see cref="FailedCount"/>（逐行也失败、回填原文的行数）分开，UI 才能如实显示"部分行降级"。</summary>
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
    }

    /// <summary>当前配置是否适合走 LLM 文本批量。视觉路与离线路有自己的整批实现，不能混用。</summary>
    public static bool LlmTextBatchConfigured(LlmConfig c) =>
        c.UseCustomLlm && !string.IsNullOrWhiteSpace(c.ApiEndpoint)
        && !c.UseOfflineArgos && !c.UseVision;

    private const int LlmBatchMaxLines = 8;
    private const int LlmBatchMaxChars = 3000;

    private sealed record LlmBatchChunk(IReadOnlyList<int> Indices);

    /// <summary>
    /// 批量翻译一组 OCR 行：LLM 按编号/JSON 结构化整批发送；任何缺行、格式坏、超时或 HTTP 失败
    /// 都先尝试同一 LLM（拆小分片/同片重试/逐行），跨后端只作最后兜底，不会把整屏静默换成免费端点。
    /// <para>⚠ 不把多行拼成一段长文：OCR 行是布局单元，拼文会让模型合并/漏行/改顺序；
    /// 这里用带 <c>id</c> 的 JSON 数组并强制返回等数量数组，逐行校验后回填。</para>
    /// <para>离线/视觉模式不调用本方法（它们已有自己的整批路径）；若误调用则明确报错，
    /// 不偷偷按在线文本处理。</para>
    /// </summary>
    public async Task<TranslationBatchResult> TranslateBatchAsync(
        IReadOnlyList<string> lines, string targetLang, string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (lines.Count == 0)
            return new TranslationBatchResult(Array.Empty<string>(), 0, 0, null, "空");

        if (!AnyBackendAvailable(_config))
            throw new TranslationNotConfiguredException(DescribeBackends(_config));

        if (_config.UseOfflineArgos)
        {
            var from = OfflineArgosEngine.NormalizeLang(_config.OfflineSourceLangHint) ?? "en";
            var to = OfflineArgosEngine.NormalizeLang(targetLang) ?? "en";
            var batch = await OfflineArgosEngine.TranslateBatchAsync(lines, from, to, ct);
            return new TranslationBatchResult(batch.Lines, batch.FailedCount, 0, batch.FirstError, "离线批量");
        }

        if (VisionConfigured(_config))
            throw new TranslationException(
                "视觉模式是整图直传路径，不能调用文本批量接口；请让 OcrPreviewWindow 走 TranslateImageWithVisionAsync。");

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
        if (llmBatch)
        {
            foreach (var chunk in BuildLlmBatchChunks(lines))
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
        var degradedBackends = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        if (missing.Length > 0 && llmBatch && !llmBlocked)
        {
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

        if (stillMissing.Length > 0 && HasNonLlmBackend())
        {
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

        // 最终兜底：仍为空的行保留原文；空白行不算失败。
        for (int i = 0; i < translated.Length; i++)
        {
            if (translated[i] is null)
            {
                translated[i] = lines[i];
                if (!string.IsNullOrWhiteSpace(lines[i])) failedCount++;
            }
        }

        string? degradedBackend = degradedCount > 0 && !degradedBackends.IsEmpty
            ? string.Join(" / ", degradedBackends.Keys.OrderBy(k => k, StringComparer.Ordinal))
            : null;

        string mode;
        if (!llmBatch) mode = "逐行";
        else if (degradedCount > 0) mode = "LLM 批量 + 跨后端降级";
        else if (missing.Length > 0) mode = "LLM 批量 + 同后端逐行";
        else if (llmRetryCount > 0) mode = "LLM 批量 + 同后端重试";
        else mode = "LLM 批量";

        return new TranslationBatchResult(
            translated.Select(s => s ?? "").ToArray(),
            failedCount,
            missing.Length,
            firstError,
            mode)
        {
            DegradedCount = degradedCount,
            DegradedBackend = degradedBackend,
            LlmRetryCount = llmRetryCount,
        };
    }

    /// <summary>批量的分片规则：最多 8 行或 3000 字符，避免一次请求过大/模型漏行。
    /// <para>只收非空白行的<b>原始下标</para>，不按“连续区间 + 非空计数”切 —— 中间夹一个空行时
    /// 区间会跨过它，把后面的行漏出这一片（2026-11-16 自审发现的边界）。</para></summary>
    private static List<LlmBatchChunk> BuildLlmBatchChunks(IReadOnlyList<string> lines) =>
        BuildLlmBatchChunkIndices(lines).Select(g => new LlmBatchChunk(g)).ToList();

    /// <summary>分片下标（internal 供测试钉边界：空行夹在中间时不许把后面的行漏掉）。</summary>
    internal static List<int[]> BuildLlmBatchChunkIndices(IReadOnlyList<string> lines)
    {
        var chunks = new List<int[]>();
        List<int> current = new();
        int chars = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
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

    /// <summary>批量失败的恢复类型；分类决定重试/拆分/跨后端。</summary>
    internal enum LlmBatchFailureKind
    {
        Parse,
        Protocol,
        Timeout,
        RateLimited,
        Auth,
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
        LlmHttpException { StatusCode: 408 } => LlmBatchFailureKind.Timeout,
        LlmHttpException { StatusCode: >= 500 } => LlmBatchFailureKind.Transient,
        LlmHttpException { StatusCode: 400 or 422 } => LlmBatchFailureKind.Protocol,
        LlmHttpException => LlmBatchFailureKind.Transient,
        TaskCanceledException => LlmBatchFailureKind.Timeout,
        TranslationException => LlmBatchFailureKind.Parse,
        JsonException or KeyNotFoundException => LlmBatchFailureKind.Protocol,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => LlmBatchFailureKind.Auth,
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
    /// Transient/Unknown → 短退避后同片重试一次；Auth/429 最终失败 → Fatal，调用方停止后续 LLM。
    /// </summary>
    private async Task<LlmBatchChunkOutcome> RecoverLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang,
        Exception initial, LlmBatchFailureKind kind, Action<string> noteFailure,
        CancellationToken ct)
    {
        switch (kind)
        {
            case LlmBatchFailureKind.Auth:
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
                kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.RateLimited);
        }
    }

    private async Task<LlmBatchChunkOutcome> RetrySplitLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang,
        Action<string> noteFailure, CancellationToken ct)
    {
        var result = new string?[texts.Count];
        int recovered = 0;
        bool fatal = false;
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
                noteFailure(ExplainLlmBatchFailure(ex));
                var kind = ClassifyLlmBatchFailure(ex);
                if (kind is LlmBatchFailureKind.Auth or LlmBatchFailureKind.RateLimited)
                    fatal = true;
            }
        }
        return new LlmBatchChunkOutcome(result, recovered, fatal);
    }

    /// <summary>只走同一个 LLM 的单行翻译；不进入免费/有道降级链。</summary>
    private Task<string> TranslateSingleLineWithLlmOnlyAsync(
        string text, string targetLang, string? sourceLang, CancellationToken ct)
        => TranslateWithLlmAsync(text, targetLang, sourceLang, ct);

    /// <summary>发一个 LLM 批量分片，返回与输入等数量的译文；行数/格式不符时抛异常。</summary>
    private async Task<string[]> TranslateLlmBatchChunkAsync(
        IReadOnlyList<string> texts, string targetLang, string? sourceLang, CancellationToken ct)
    {
        string sl = sourceLang ?? "auto-detect";
        string basePrompt = !string.IsNullOrWhiteSpace(_config.SystemPrompt)
            ? _config.SystemPrompt
            : $"You are a professional translator. Translate the following text to {targetLang}.";
        string systemPrompt = basePrompt
            + "\n\nYou are translating a batch of numbered OCR lines. "
            + "Return ONLY a valid JSON array of exactly " + texts.Count + " strings, in input order. "
            + "Each string must be the translation of the corresponding input line. "
            + "Do not merge, split, reorder, omit, or add lines. Do not return explanations, notes, "
            + "quotes, numbering, markdown fences, or the source text. "
            + "Preserve numbers, URLs, code identifiers, and proper nouns.";

        var input = new LlmBatchLine[texts.Count];
        for (int i = 0; i < texts.Count; i++) input[i] = new LlmBatchLine(i + 1, texts[i]);
        string inputJson = JsonSerializer.Serialize(input, LlmJsonContext.Default.LlmBatchLineArray);

        string userPrompt =
            $"Translate from {sl} to {targetLang}. Return exactly {texts.Count} translated strings "
            + "as a JSON array, in the same order as the input. Input:\n" + inputJson;

        var requestBody = new LlmChatRequest(
            Model: !string.IsNullOrWhiteSpace(_config.ModelName) ? _config.ModelName : "deepseek-chat",
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", userPrompt)
            ],
            Temperature: 0.1,
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
        string? sourceLang, CancellationToken ct)
    {
        string sl = sourceLang ?? "auto-detect";
        string basePrompt = !string.IsNullOrWhiteSpace(_config.SystemPrompt)
            ? _config.SystemPrompt
            : $"You are a professional translator. Translate the following text to {targetLang}.";
        string systemPrompt = basePrompt
            + "\n\nTranslate exactly one OCR line. Return exactly one line of translated text. "
            + "Do not return explanations, notes, quotes, code fences, numbering, or the source text. "
            + "Do not merge or split lines.";

        string model = !string.IsNullOrWhiteSpace(_config.ModelName)
            ? _config.ModelName
            : "deepseek-chat";

        // 使用源生成器构建请求体（AOT 兼容，替代匿名类型 + JsonContent.Create）
        var requestBody = new LlmChatRequest(
            Model: model,
            Messages: [
                new LlmChatMessage("system", systemPrompt),
                new LlmChatMessage("user", $"Translate from {sl} to {targetLang}:\n\n{text}")
            ],
            Temperature: 0.1,
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
        if (!string.IsNullOrEmpty(_config.ApiKey))
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
            : base(FormatMessage(statusCode, body))
        {
            StatusCode = statusCode;
            RetryAfterSeconds = retryAfterSeconds;
        }

        private static string FormatMessage(int statusCode, string body)
        {
            string s = (body ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) return $"HTTP {statusCode}";
            return $"HTTP {statusCode}: {(s.Length > 160 ? s[..160] : s)}";
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
    /// 带 <c>id</c> 的对象数组、按编号分行的纯文本。**任何形状都必须等数量且逐行可校验**，
    /// 否则整批退回逐行 —— 不允许“按顺序硬套”造成错位覆盖。
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

        // 对象数组且每项都有 id：按 id 回填，不信任数组顺序。
        bool allObjects = true, allHaveId = true;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) { allObjects = false; break; }
            if (!el.TryGetProperty("id", out _)) { allHaveId = false; break; }
        }

        if (allObjects && allHaveId)
        {
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

        // 字符串数组（或没有 id 的对象数组）：只能按顺序，前提仍是等数量。
        var lines = new string[expectedCount];
        int index = 0;
        foreach (var el in arr.EnumerateArray())
        {
            string? value = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.ValueKind == JsonValueKind.Object ? ReadJsonTranslationText(el) : null;
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
        foreach (var key in new[] { "text", "translation", "value" })
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

    private static LlmBatchParseResult ParseNumberedBatchLines(string text, int expectedCount)
    {
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawLines.Length != expectedCount)
            return new LlmBatchParseResult(null,
                $"LLM 批量响应行数 {rawLines.Length} ≠ 输入 {expectedCount}（可能合并或漏行）");

        var lines = new string[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            string withoutNumber = Regex.Replace(rawLines[i], @"^\s*\d+\s*[.、)：:]\s*", "");
            var line = SanitizeSingleLine(withoutNumber);
            if (line is null)
                return new LlmBatchParseResult(null,
                    $"LLM 批量响应第 {i + 1} 行不是可用的单行译文");
            lines[i] = line;
        }
        return new LlmBatchParseResult(lines, null);
    }

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
    /// 把模型输出收敛成"一条可覆盖 OCR 行的译文"：去代码块、引号、编号、Translation: 前缀；
    /// 多行/解释性文字/拒答语返回 null（由调用方判失败并换后端）。
    /// </summary>
    internal static string? SanitizeSingleLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        string s = StripCodeFence(content.Trim());

        s = Regex.Replace(s,
            @"^(?:here\s+is\s+the\s+translation|here's\s+the\s+translation|translation|译文|翻译|output)\s*[：:]\s*",
            "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^\s*\d+\s*[.、)：:]\s*", "");
        s = s.Trim();

        if ((s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal)
             || s.StartsWith("“", StringComparison.Ordinal) && s.EndsWith("”", StringComparison.Ordinal)
             || s.StartsWith("'", StringComparison.Ordinal) && s.EndsWith("'", StringComparison.Ordinal))
            && s.Length >= 2)
            s = s[1..^1].Trim();

        if (s.Length == 0) return null;
        if (s.Contains('\n') || s.Contains('\r')) return null;

        string lower = s.ToLowerInvariant();
        if (lower.Contains("as an ai")
            || lower.Contains("i cannot")
            || lower.Contains("i'm sorry")
            || lower.Contains("here is the translation")
            || lower.Contains("here's the translation")
            || lower.StartsWith("note:")
            || lower.StartsWith("explanation:")
            || s.Contains("以下是翻译")
            || s.Contains("译文如下")
            || s.Contains("翻译如下"))
            return null;

        return s;
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
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(VisionRequestTimeout);
        try
        {
            await WaitIfRateLimitedAsync(linkedCts.Token);
            var response = await s_visionHttp.SendAsync(request, linkedCts.Token);
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
        var mapped = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSingleLine)
            .ToArray();
        return mapped.Length == expectedLineCount && mapped.All(l => l is not null)
            ? mapped.Select(l => l!).ToArray()
            : null;
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

