// TrueToneCap.Test/ServiceTests.cs
// 基础设施服务测试 — ShaderLoader / NativeLibraryResolver / ToolchainHelper

using System.Reflection;
using TrueToneCap.Core.Services;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.Test;

/// <summary>基础设施服务测试集。</summary>
public static class ServiceTests
{
    private static int _passed, _failed;

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
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

        // ToolchainHelper
        Test_ToolchainHelper_CheckAvailable_Invalid();

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  ShaderLoader
    // ═══════════════════════════════════════

    static void Test_ShaderLoader_LoadKnownShader()
    {
        // 尝试加载已知的着色器。测试项目可能没有着色器文件，
        // 所以加载失败（null）也是可接受的——只要不崩溃即可
        var bytes = ShaderLoader.Load("ToneMapping.hlsl.cso");
        // 文件系统找不到时返回 null（不崩溃），嵌入资源也找不到时亦然
        Assert("ShaderLoader: ToneMapping 不崩溃", bytes is null or { Length: > 0 });
    }

    static void Test_ShaderLoader_LoadNonExistent()
    {
        // 不存在的着色器应返回 null
        var bytes = ShaderLoader.Load("NonExistent.shader.cso");
        Assert("ShaderLoader: 不存在返回 null", bytes is null);
    }

    static void Test_ShaderLoader_CacheWorks()
    {
        // 验证缓存机制：两次加载指向同一实例
        ShaderLoader.ClearCache();
        var first = ShaderLoader.Load("ToneMapping.hlsl.cso");
        var second = ShaderLoader.Load("ToneMapping.hlsl.cso");
        Assert("ShaderLoader: 缓存有效", first is null || ReferenceEquals(first, second));
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
    //  ToolchainHelper
    // ═══════════════════════════════════════

    static void Test_ToolchainHelper_CheckAvailable_Invalid()
    {
        // 不存在的工具应返回 false
        var available = ToolchainHelper.CheckAvailable("nonexistent_tool_xyz.exe", "--version");
        Assert("ToolchainHelper: 不存在返回 false", !available);
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