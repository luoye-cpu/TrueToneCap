// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/ServiceTests.cs
// 基础设施服务测试 — NativeLibraryResolver

using System.Reflection;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

/// <summary>基础设施服务测试集。</summary>
public static class ServiceTests
{
    private static int _passed, _failed;
    /// <summary>因环境不满足而【显式跳过】的用例数 —— 既不算通过也不算失败。</summary>
    private static int _skipped;

    /// <summary>显式跳过一条用例：不计入通过/失败，但必须留下原因（禁止静默通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    public static int RunAll()
    {
        _passed = 0; _failed = 0; _skipped = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 服务测试");
        Console.WriteLine("══════════════════════════════════════\n");

        // NativeLibraryResolver
        Test_NativeLibraryResolver_Initializes();
        Test_NativeLibraryResolver_GetExePath_ThrowsOnMissing();

        // 翻译链路端到端（默认跳过，见方法注释）
        Test_TranslationLive_FreeEndpointsEndToEnd();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--service-tests", _passed, _failed, 0, null, _skipped);
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  翻译链路端到端（会真的外发一条请求 ⇒ 默认跳过）
    // ═══════════════════════════════════════

    /// <summary>
    /// 真发一次翻译请求，验证 <b>C# 侧的免费公共端点降级链端到端能用</b>（2026-10-23 起链首是
    /// Edge <c>translatetext</c>，其后依次必应 / MyMemory / Google）。
    /// <para>为什么必须有这条：unit 套件里那一堆免费端点判据全是<b>离线</b>的（组装 URL、
    /// 解析样本响应、读源码接线），它们证明不了"Edge 那个裸字符串数组今天真被服务器接受、
    /// 必应那三个正则今天在真页面上还抓得到东西"。而这恰恰是最容易无声坏掉的一环 ——
    /// 上游一改版，链路上线当天不会有任何人知道。2026-10-23 就实测过一次"离线形状全绿、
    /// 而线上从没验过"：把 <c>Content-Type</c> 之外的那些浏览器头剥掉之后才确认它并不需要，
    /// 这句话只能由真发一次请求来说。</para>
    /// <para>默认 <b>SKIP</b>：门禁不许依赖出网（与 <c>scripts/probes</c> 三条判据同一约定，
    /// 那里也是"要网就手动跑"）。跑法：<c>TTC_LIVE_TRANSLATE=1 dotnet run … -- --service-tests</c>。
    /// 外发的是下面这一句合成文本，不含任何用户内容。</para>
    /// </summary>
    static void Test_TranslationLive_FreeEndpointsEndToEnd()
    {
        if (Environment.GetEnvironmentVariable("TTC_LIVE_TRANSLATE") != "1")
        {
            Skip("翻译链路端到端（免费公共端点）",
                "需显式 TTC_LIVE_TRANSLATE=1（会向第三方发一条合成文本），门禁默认不跑");
            return;
        }

        const string src = "Please save the file before exiting.";
        // ⚠ 2026-10-23 之后这一条测的是【默认配置】：什么都不填就是免费档开箱能翻的路径。
        //    原先写"闸门开且未配任何凭据 ⇒ 走的必应"，现在降级链的第一条是 Edge，
        //    所以归因交给 LastBackendServed 报实测真值，不再由注释替它认领。
        var svc = new TranslationService(new LlmConfig());
        string? got = null, err = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { got = svc.TranslateAsync(src, "zh-CN").GetAwaiter().GetResult(); }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
        sw.Stop();

        // 把实际拿到的译文与"哪家给的"一起印出来：这条用例的意义就是"人眼看一眼真东西"，
        // 只印一个 ✅ 等于把证据又折回成断言本身。
        Console.WriteLine($"  ⓘ 实发 \"{src}\" → 收到 \"{got}\"（{sw.ElapsedMilliseconds} ms，"
            + $"默认配置、未填任何凭据 ⇒ 交出译文的后端 = {svc.LastBackendServed ?? "（未归因）"}；"
            + $"最后一次失败留痕 = {svc.LastAttemptNote ?? "（无）"}）");

        bool han = got is not null && got.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        Assert("免费端点端到端：默认配置不填任何凭据 ⇒ 拿到中文译文（不是原样回显）",
            got is not null && got.Trim() != src && han, err ?? $"译文={got}");
        // 归因这一条单独判：它保证上面那句"拿到了"能追到是哪个后端，
        // 否则四家轮着成功/失败时，这条绿灯什么都说明不了。
        Assert("免费端点端到端：成功那一次说得出是哪个后端交出的（Debug 行在默认 Brief 档不落盘）",
            !string.IsNullOrEmpty(svc.LastBackendServed), svc.LastBackendServed ?? "<null>");
    }

    // ═══════════════════════════════════════
    //  NativeLibraryResolver
    // ═══════════════════════════════════════

    static void Test_NativeLibraryResolver_Initializes()
    {
        try
        {
            NativeLibraryResolver.Initialize();
            Assert("NativeLibraryResolver: 初始化不崩溃", true);
        }
        catch (Exception ex)
        {
            Assert($"NativeLibraryResolver: 初始化异常: {ex.Message}", false);
        }
    }

    static void Test_NativeLibraryResolver_GetExePath_ThrowsOnMissing()
    {
        try
        {
            // 不存在的 exe 应抛出异常
            NativeLibraryResolver.GetExePath("nonexistent_tool_xyz.exe");
            Assert("NativeLibraryResolver: 不存在应抛出异常", false);
        }
        catch (DllNotFoundException)
        {
            Assert("NativeLibraryResolver: 不存在抛出 DllNotFoundException", true);
        }
        catch (Exception ex)
        {
            Assert($"NativeLibraryResolver: 异常类型错误: {ex.GetType().Name}", false);
        }
    }

    // ═══════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════

    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}{(detail != null ? $": {detail}" : "")}"); }
    }
}