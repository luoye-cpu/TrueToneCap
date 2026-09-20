// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/ServiceTests.cs
// 基础设施服务测试 — ShaderLoader / NativeLibraryResolver

using System.Reflection;
using TrueToneCap.Core.Services;
using TrueToneCap.Core.Processing;

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

        // ShaderLoader
        Test_ShaderLoader_LoadKnownShader();
        Test_ShaderLoader_LoadNonExistent();
        Test_ShaderLoader_CacheWorks();

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
    //  ShaderLoader
    // ═══════════════════════════════════════

    static void Test_ShaderLoader_LoadKnownShader()
    {
        // ShaderLoader.Load 具备两级解析策略（文件系统 data/Shaders/ → 嵌入资源），
        // 二者均不可达时返回 null 属于「设计内」行为。
        // 但把「null 或有内容」都判为通过等于恒真断言，会掩盖真实回归，
        // 因此这里改为断言可观测的契约：
        //   1) 调用不得抛异常（加载器的核心承诺）
        //   2) 返回非 null 时必须是合法 DXBC 字节码（魔数 'D' 'X' 'B' 'C'）
        // 若环境确实提供了着色器，则必须能通过字节码校验。
        byte[]? bytes = null;
        bool threw = false;
        try { bytes = ShaderLoader.Load("ToneMapping.hlsl.cso"); }
        catch (Exception ex)
        {
            threw = true;
            Assert("ShaderLoader: ToneMapping 调用不应抛异常", false, ex.Message);
        }
        if (threw) return;

        if (bytes is null)
        {
            // 未找到属「环境不满足」（测试项目不产出着色器产物），必须显式跳过。
            // 2026-09-15 修正：此处原为 Assert(..., true) 恒真断言，会把「字节码校验从未执行」
            // 伪装成通过 —— DXBC 魔数校验与缓存引用相等两条真实断言因此长期空转。
            Console.WriteLine("  ⓘ ShaderLoader: ToneMapping.hlsl.cso 未找到（文件系统与嵌入资源均无）");
            Skip("ShaderLoader 字节码校验", "测试环境无着色器产物（data/Shaders/ToneMapping.hlsl.cso 缺失）");
        }
        else
        {
            Assert("ShaderLoader: ToneMapping 返回非空字节码", bytes.Length > 0);
            Assert("ShaderLoader: ToneMapping 具备 DXBC 魔数",
                   bytes.Length >= 4 && bytes[0] == (byte)'D' && bytes[1] == (byte)'X'
                                    && bytes[2] == (byte)'B' && bytes[3] == (byte)'C');
        }
    }

    static void Test_ShaderLoader_LoadNonExistent()
    {
        // 不存在的着色器应返回 null（而非抛异常）
        byte[]? bytes = null;
        try
        {
            bytes = ShaderLoader.Load("NonExistent.shader.cso");
        }
        catch (Exception ex)
        {
            Assert("ShaderLoader: 不存在应返回 null 而非抛异常", false, ex.Message);
            return;
        }
        Assert("ShaderLoader: 不存在返回 null", bytes is null);
    }

    static void Test_ShaderLoader_CacheWorks()
    {
        // 缓存契约：同一名称连续两次加载应返回同一数组实例（引用相等）。
        // 原断言 `first is null || ReferenceEquals(...)` 在 first 为 null 时恒真，
        // 此处改为仅在确有内容时校验引用相等，无内容时明确标注为「缓存未生效（无数据）」。
        ShaderLoader.ClearCache();
        var first = ShaderLoader.Load("ToneMapping.hlsl.cso");
        var second = ShaderLoader.Load("ToneMapping.hlsl.cso");

        if (first is null)
        {
            Console.WriteLine("  ⓘ ShaderLoader: 无着色器数据，缓存路径未被执行");
            Assert("ShaderLoader: 重复加载均为 null（一致）", second is null);
        }
        else
        {
            Assert("ShaderLoader: 第二次加载命中缓存（引用相等）", ReferenceEquals(first, second));

            // 清除缓存后应重新加载出等价（非同一实例）的内容
            ShaderLoader.ClearCache();
            var third = ShaderLoader.Load("ToneMapping.hlsl.cso");
            Assert("ShaderLoader: ClearCache 后重新加载内容一致",
                   third is not null && third.Length == first.Length
                   && third.AsSpan().SequenceEqual(first));
        }
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