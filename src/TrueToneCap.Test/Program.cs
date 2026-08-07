using TrueToneCap.Core.Services;
using System.Diagnostics;

// ── 单元测试入口 ──
if (args.Contains("--unit-tests"))
{
    return TrueToneCap.Test.CorePipelineTests.RunAll();
}

// ── 编码集成测试入口 ──
if (args.Contains("--encoding-tests"))
{
    return TrueToneCap.Test.EncodingIntegrationTests.RunAll();
}

// ── 综合可用性测试入口 ──
if (args.Contains("--usability-tests"))
{
    return TrueToneCap.Test.UsabilityTests.RunAll();
}

// ── 色彩管线精度测试入口 ──
if (args.Contains("--color-tests"))
{
    return TrueToneCap.Test.ColorPipelineTests.RunAll();
}

// ── 服务测试入口 ──
if (args.Contains("--service-tests"))
{
    return TrueToneCap.Test.ServiceTests.RunAll();
}

// ── 全部测试入口 ──
if (args.Contains("--all"))
{
    int totalExit = 0;
    totalExit += TrueToneCap.Test.CorePipelineTests.RunAll();
    Console.WriteLine();
    totalExit += TrueToneCap.Test.ColorPipelineTests.RunAll();
    Console.WriteLine();
    totalExit += TrueToneCap.Test.EncodingIntegrationTests.RunAll();
    Console.WriteLine();
    totalExit += TrueToneCap.Test.ServiceTests.RunAll();
    Console.WriteLine();
    totalExit += TrueToneCap.Test.UsabilityTests.RunAll();
    Console.WriteLine("\n══════════════════════════════════════");
    Console.WriteLine(totalExit == 0 ? "  ✅ 全部测试通过" : "  ❌ 存在失败测试");
    Console.WriteLine("══════════════════════════════════════");
    return totalExit;
}

Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  TrueToneCap 测试运行器");
Console.WriteLine("══════════════════════════════════════\n");
Console.WriteLine("用法: dotnet run --project src/TrueToneCap.Test -- [选项]\n");
Console.WriteLine("选项:");
Console.WriteLine("  --unit-tests       核心单元测试 (PixelOps, ToneMapper, ICC, 标注, 编码器)");
Console.WriteLine("  --color-tests      色彩管线精度测试");
Console.WriteLine("  --encoding-tests   编码管线集成测试");
Console.WriteLine("  --usability-tests  综合可用性测试");
Console.WriteLine("  --service-tests    基础设施服务测试 (ShaderLoader, NativeLibraryResolver)");
Console.WriteLine("  --all              全部测试\n");
return 0;
