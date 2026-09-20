// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
using TrueToneCap.Core.Services;
using System.Diagnostics;
using System.IO;

// ── TrueToneCap 测试运行器 ──
//
// 设计说明:
//   各套件以静态 _passed/_failed 独立计数，可在同一进程内顺序执行。
//   唯一例外是 WgcAvifBackendTests：
//     · 它必须独占一个 STA 线程（WGC 帧投递依赖消息泵），
//       且持有 D3D11 设备 / 捕获会话等进程级 GPU 资源，
//       与同进程内的其它测试共享 GPU 状态会产生不可预期的相互干扰。
//   故该套件在 --all 下以「独立子进程」方式运行，其余套件同进程串联。
//
// 退出码: 0 = 全部通过, 非 0 = 存在失败。
//
// ⚠️ 本运行器是自研的控制台程序，【不是】VSTest/xunit 测试项目:
//    · 未引用 Microsoft.NET.Test.Sdk，也未设置 IsTestProject
//    · 因此 `dotnet test` 会以【退出码 0 静默执行零个测试】—— 假绿灯，看似通过实则没跑
//    · 唯一有效入口: dotnet run --project src/TrueToneCap.Test -- <套件参数>
//    · 一次只接受一个套件参数（命中首个即返回）；多套件请用 --all 或逐个调用
//    · CI 集成请解析退出码，以及每个套件末尾由 TestSummary.Report 输出的机器可读行:
//        RESULT suite=<参数> passed=<n> failed=<n> warnings=<n> elapsedMs=<n|-> skipped=<n>
//      各套件的总结行已统一由 TestSummary.cs 产生（2026-09-14），不要再手写总结行。
//
// ⚠️ 未知参数 / 无参数一律以【非 0】退出:
//    运行器一旦"什么都没跑却返回 0"，上层脚本（verify.ps1 / Publish.ps1）就会判定门禁通过。
//    实测 2026-09-14：`--bogus-tests` 曾打印用法后 exit 0，与 verify.ps1 的
//    "未解析到 RESULT 行也算通过"叠加，构成一条完整的假绿灯路径。

const string TestProject = "src/TrueToneCap.Test/TrueToneCap.Test.csproj";

// ── 需在独立进程中运行的套件（独占 GPU/STA 线程资源） ──
var isolatedSuites = new (string Arg, string Name, Func<int> Run)[]
{
    ("--wgc-avif-tests", "WGC 捕获 + AVIF 后端验证", TrueToneCap.Test.WgcAvifBackendTests.RunAll),
};

// ── 可在同进程内串联执行的套件 ──
var chainedSuites = new (string Arg, string Name, Func<int> Run)[]
{
    ("--unit-tests",     "核心单元测试",   TrueToneCap.Test.CorePipelineTests.RunAll),
    ("--color-tests",    "色彩管线精度",   TrueToneCap.Test.ColorPipelineTests.RunAll),
    ("--encoding-tests", "编码管线集成",   TrueToneCap.Test.EncodingIntegrationTests.RunAll),
    ("--service-tests",  "基础设施服务",   TrueToneCap.Test.ServiceTests.RunAll),
    ("--ocr-tests",      "OCR 服务",       TrueToneCap.Test.OcrServiceTests.RunAll),
    ("--format-tests",   "输出结构合法性", TrueToneCap.Test.FormatStructureTests.RunAll),
    ("--usability-tests","综合可用性",     TrueToneCap.Test.UsabilityTests.RunAll),
};

// ═══════════════════════════════════════
//  单套件入口（子进程复用）
//  统一从 chainedSuites / isolatedSuites 派生，避免与上方列表重复维护而漂移。
// ═══════════════════════════════════════
foreach (var s in chainedSuites)
    if (args.Contains(s.Arg)) return s.Run();
foreach (var s in isolatedSuites)
    if (args.Contains(s.Arg)) return s.Run();

// ═══════════════════════════════════════
//  全部测试
// ═══════════════════════════════════════
if (args.Contains("--all"))
{
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  TrueToneCap 全量测试");
    Console.WriteLine("══════════════════════════════════════\n");

    int failedSuites = 0;
    var results = new List<(string Name, int Exit)>();

    // 1) 同进程串联套件
    foreach (var s in chainedSuites)
    {
        Console.WriteLine($"▶ {s.Name} ({s.Arg})\n");
        int exit;
        try { exit = s.Run(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 套件异常终止: {ex.Message}");
            exit = 1;
        }
        results.Add((s.Name, exit));
        if (exit != 0) failedSuites++;
        Console.WriteLine();
    }

    // 2) 子进程隔离套件
    foreach (var s in isolatedSuites)
    {
        Console.WriteLine($"▶ {s.Name} ({s.Arg}) — 独立子进程\n");
        int exit = RunInChildProcess(s.Arg);
        results.Add((s.Name, exit));
        if (exit != 0) failedSuites++;
        Console.WriteLine();
    }

    // 3) 汇总
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  套件结果汇总");
    Console.WriteLine("══════════════════════════════════════");
    foreach (var r in results)
        Console.WriteLine($"  {(r.Exit == 0 ? "✅" : "❌")} {r.Name,-26} 退出码 {r.Exit}");
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine(failedSuites == 0
        ? $"  ✅ 全部 {results.Count} 个套件通过"
        : $"  ❌ {failedSuites}/{results.Count} 个套件失败");
    Console.WriteLine("══════════════════════════════════════");
    return failedSuites == 0 ? 0 : 1;
}

// ═══════════════════════════════════════
//  显式求助 → 0；未知/无参数 → 1（不得伪装成通过）
// ═══════════════════════════════════════
if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

Console.Error.WriteLine("❌ 未识别的参数（或缺少套件参数）—— 没有执行任何测试。");
if (args.Length > 0) Console.Error.WriteLine($"   收到: {string.Join(' ', args)}");
Console.Error.WriteLine("   使用 --help 查看可用套件；上层门禁请勿把本退出码当作通过。");
PrintUsage();
return 1;

static void PrintUsage()
{
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  TrueToneCap 测试运行器");
    Console.WriteLine("══════════════════════════════════════\n");
    Console.WriteLine($"用法: dotnet run --project {TestProject} -- [选项]\n");
    Console.WriteLine("选项:");
    Console.WriteLine("  --unit-tests       核心单元测试 (PixelOps, ToneMapper, ICC, 标注, 编码器)");
    Console.WriteLine("  --color-tests      色彩管线精度测试");
    Console.WriteLine("  --encoding-tests   编码管线集成测试");
    Console.WriteLine("  --usability-tests  综合可用性测试");
    Console.WriteLine("  --service-tests    基础设施服务测试 (ShaderLoader, NativeLibraryResolver)");
    Console.WriteLine("  --ocr-tests        OCR 服务测试 (BitmapPreprocessor, LLM 提供商/模型清单闭合)");
    Console.WriteLine("  --format-tests     输出结构合法性测试 (TIFF IFD / AVIF box)");
    Console.WriteLine("  --wgc-avif-tests   WGC 真实捕获 + AVIF 各后端编码验证 (需桌面会话)");
    Console.WriteLine("  --all              全部测试 (含全部 8 个套件; WGC 以子进程隔离运行)");
    Console.WriteLine("  --help / -h        显示本帮助\n");
}

// ═══════════════════════════════════════
//  子进程执行辅助
// ═══════════════════════════════════════
static int RunInChildProcess(string suiteArg)
{
    // 定位当前可执行文件。
    // ⚠️ 不能无条件把 Environment.ProcessPath 当"测试宿主"用：
    //    通过 `dotnet run` 启动时它是 dotnet.exe，直接加套件参数会变成
    //    `dotnet.exe --wgc-avif-tests` —— 缺少程序集路径，必然失败
    //    （2026-09-14 修正；此前 Program.cs 的注释错误地声称两种启动方式都适用）。
    string hostPath = Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "TrueToneCap.Test.exe");
    bool isDotnetHost = Path.GetFileNameWithoutExtension(hostPath)
        .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    string exePath = hostPath;
    var extraArgs = new List<string>();
    if (isDotnetHost)
    {
        // dotnet 需要显式给出程序集路径
        string dll = Path.Combine(AppContext.BaseDirectory, "TrueToneCap.Test.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine($"  ❌ 未找到测试程序集: {dll}");
            return 1;
        }
        extraArgs.Add(dll);
    }
    else if (!File.Exists(hostPath))
    {
        // 兜底：宿主路径无效时退回同目录下的 apphost
        exePath = Path.Combine(AppContext.BaseDirectory, "TrueToneCap.Test.exe");
    }

    var psi = new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = false,
        // 保留子进程输出流式到控制台，便于观察失败原因
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };
    foreach (var a in extraArgs) psi.ArgumentList.Add(a);
    psi.ArgumentList.Add(suiteArg);

    if (!File.Exists(exePath))
    {
        Console.WriteLine($"  ❌ 无法定位测试可执行文件: {exePath}");
        Console.WriteLine("     请先执行 dotnet build 生成测试宿主。");
        return 1;
    }

    try
    {
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.WriteLine("  ❌ 子进程启动失败 (Process.Start 返回 null)");
            return 1;
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            Console.WriteLine($"  ⚠️ 子进程退出码: {proc.ExitCode}");
        return proc.ExitCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ 子进程执行异常: {ex.Message}");
        return 1;
    }
}
