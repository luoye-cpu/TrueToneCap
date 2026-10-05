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
        Test_TranslationLive_BingEndToEnd();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--service-tests", _passed, _failed, 0, null, _skipped);
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  翻译链路端到端（会真的外发一条请求 ⇒ 默认跳过）
    // ═══════════════════════════════════════

    /// <summary>
    /// 真发一次翻译请求，验证 C# 侧的必应链路端到端能用。
    /// <para>为什么必须有这条：unit 套件里那十条必应判据全是**离线**的（组装 URL、解析样本响应、
    /// 读源码接线），它们证明不了"那三个正则今天在真页面上还抓得到东西、表单真能被接受"。
    /// 而这恰恰是最容易无声坏掉的一环 —— 上游一改版，链路上线当天不会有任何人知道。</para>
    /// <para>默认 <b>SKIP</b>：门禁不许依赖出网（与 <c>scripts/probes</c> 三条判据同一约定，
    /// 那里也是"要网就手动跑"）。跑法：<c>TTC_LIVE_TRANSLATE=1 dotnet run … -- --service-tests</c>。
    /// 外发的是下面这一句合成文本，不含任何用户内容。</para>
    /// </summary>
    static void Test_TranslationLive_BingEndToEnd()
    {
        if (Environment.GetEnvironmentVariable("TTC_LIVE_TRANSLATE") != "1")
        {
            Skip("翻译链路端到端（必应）",
                "需显式 TTC_LIVE_TRANSLATE=1（会向第三方发一条合成文本），门禁默认不跑");
            return;
        }

        const string src = "Please save the file before exiting.";
        var svc = new TranslationService(new LlmConfig { AllowUnofficialTranslateEndpoints = true });
        string? got = null, err = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { got = svc.TranslateAsync(src, "zh-CN").GetAwaiter().GetResult(); }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
        sw.Stop();

        // 把实际拿到的译文印出来：这条用例的意义就是"人眼看一眼真东西"，
        // 只印一个 ✅ 等于把证据又折回成断言本身。
        Console.WriteLine($"  ⓘ 实发 \"{src}\" → 收到 \"{got}\"（{sw.ElapsedMilliseconds} ms，"
            + $"闸门开且未配任何凭据 ⇒ 走的必应）");

        bool han = got is not null && got.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        Assert("必应端到端：闸门开 ⇒ 拿到中文译文（不是原样回显）",
            got is not null && got.Trim() != src && han, err ?? $"译文={got}");
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