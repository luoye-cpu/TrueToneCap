// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/ShaderCompiler.cs
// HLSL → CSO 后台自动编译（启动时检测，缺失时自动编译）

using System.Diagnostics;
using TrueToneCap.Core.Logging;   // LogCategory

namespace TrueToneCap.App.Services;

/// <summary>HLSL 着色器后台编译器。</summary>
public static class ShaderCompiler
{
    private record ShaderDef(string File, string Profile);

    // ⚠️ 本清单曾与 Shaders/ 目录下的实际文件长期漂移（2026-09-14 核查后修正）。
    //    维护规则：新增/删除 .hlsl 源文件时【必须同步这里】，否则
    //    · 源文件存在但列表没有 → 运行时不会自动补编译（csproj 构建期可能已编译，易产生"本地能跑、别人跑不了"）
    //    · 列表有但源文件不存在 → 每次启动打一条 Debug 日志后跳过（静默降级，很难发现）
    //
    //    当前状态（与 src/TrueToneCap.App/Shaders/ 实际文件逐个核对，2026-10-01）：
    //      OverlayComposite.hlsl  ✅ 存在，运行时消费（HdrCaptureWindow 的 GPU 合成，FindCso 按名字取字节码）
    //      FullscreenVS.hlsl      ✅ 存在，运行时消费（同上）
    //
    //    已移除的条目（第二批，2026-10-01）：
    //      ToneMapping.hlsl       这一格此前写的是"✅ 存在，运行时消费"——**那句是假的**。它唯一的加载方
    //                             是 Core 的 GpuToneMapper（ShaderLoader.Load("ToneMapping.hlsl.cso")），
    //                             而 GpuToneMapper 自 2026-09-16 起不再构造（AppServices.cs 当时自己写明
    //                             "【死代码】…属性全仓没有任何调用者"），实际的 HDR→SDR 一直是
    //                             Core/ToneMapper.cs 的 CPU 路径。资产 + GpuToneMapper + ShaderLoader 一并删除。
    //                             留锁：CorePipelineTests.Test_ShaderList_HasNoOrphanEntry —— 清单每条都要有
    //                             "剥掉整行注释后仍按名字找 .cso"的消费点，且清单/磁盘/csproj Inputs 三张名单互相对账。
    //                             （注释本身不算消费方，就是这类注释把"没人加载"洗白了十年份的量级。）
    //
    //    已移除的条目（2026-09-14，源文件本就不存在，留着只会让清单与实际不一致）：
    //      CopyTexture.hlsl       HdrCaptureWindow.cs 会查找 CopyTexture.hlsl.cso，
    //                             找不到则回退 ToneMapping.hlsl.cso；而回退得到的 _ps
    //                             【从未被 PSSetShader 绑定】（该文件唯一的 PSSetShader 绑的是
    //                             _overlayPs），属死代码，移除无行为影响。
    //      MosaicEffect.hlsl      全仓无消费方（马赛克已改 CPU 实现）。
    //    注：HdrCaptureWindow 里的 FindCso 查找逻辑与运行时编译无关，移除条目不影响它。
    private static readonly ShaderDef[] s_shaders =
    [
        // ⚠️ Profile 必须是 SM5.0（ps_5_0 / vs_5_0），与构建期 CompileShaders.ps1 严格一致。
        //    2026-08-11 已把构建期从 dxc 改为 fxc，原因写在 CompileShaders.ps1 头部：
        //      「dxc 强制 SM6/DXIL，D3D11 CreateShader 报 E_INVALIDARG；
        //        fxc 产出 SM5.0 DXBC，D3D11/Vortice 全兼容。」
        //    但【运行时这条路径当时漏改】，一直是 ps_6_0/vs_6_0 + dxc ——
        //    一旦本文件真的触发编译（data/Shaders 下 .cso 缺失且找到 dxc），
        //    会产出 D3D11 根本加载不了的 SM6 着色器，且只在 Debug 输出里留一行失败日志。
        //    2026-09-14 修正为 fxc + SM5.0，与构建期对齐。
        new("OverlayComposite.hlsl", "ps_5_0"),
        new("FullscreenVS.hlsl", "vs_5_0"),
    ];
    private const string s_entry = "main";

    /// <summary>确保所有 CSO 文件存在。缺失时后台自动编译。应在应用启动最开始调用。</summary>
    public static void EnsureCompiled(string shaderDir, string outputDir)
    {
        // ═══ 2026-10-31：存在性检查【前置】到找编译器之前 ═══
        // 原实现的第一句就是 FindFxc()（起 `where fxc.exe` 子进程 + WaitForExit(2000)，
        // 失败再 LINQ 枚举 Windows Kits\10\bin 的各版本目录逐个 File.Exists），
        // 而真机 117/117 次启动里两枚 .cso 都已存在（构建期 CompileShaders.ps1 已产出）
        // ⇒ 每次启动都为一条不会走的路径白付一次进程创建（本机静置后第一次创建进程多付约 200ms）。
        // 现在只有【确实缺 .cso 且源文件在】时才去找 fxc。
        // ⚠ 刻意不动编译参数与 SM5.0 口径（见上面 s_shaders 的注释）：换 dxc/ps_6_0 会产出
        //   D3D11 根本加载不了的着色器，那条事实与本条性能无关。
        // 行为差异（都是可观察的）：
        //   · 齐全时不再有机会走到 "fxc.exe 未找到" 那条 Warn —— 缺件的那台机器仍然会走到；
        //   · "…cso 已存在" 的 Info 行数不变（原本每枚已存在的 .cso 也各打一条）。
        var pending = new List<ShaderDef>();

        foreach (var shader in s_shaders)
        {
            var inputPath = Path.Combine(shaderDir, shader.File);
            var outputPath = Path.Combine(outputDir, shader.File + ".cso");

            if (File.Exists(outputPath))
            {
                LogService.Info("Shader", $"{shader.File}.cso 已存在 ({new FileInfo(outputPath).Length} bytes)", LogCategory.Capture);
                continue;
            }

            if (!File.Exists(inputPath))
            {
                LogService.Warn("Shader", $"{shader.File} 源文件未找到", LogCategory.Capture);
                continue;
            }

            pending.Add(shader);
        }

        if (pending.Count == 0) return;

        // 必须与构建期 CompileShaders.ps1 用【同一个编译器】(fxc)，否则产出的 CSO 规格不一致。
        var fxcPath = FindFxc();
        if (fxcPath is null)
        {
            LogService.Warn("Shader", "fxc.exe 未找到（需 Windows SDK），跳过运行时编译（GPU 将回退 CPU）", LogCategory.Capture);
            return;
        }

        Directory.CreateDirectory(outputDir);

        foreach (var shader in pending)
        {
            // 后台编译
            Task.Run(() => CompileShader(fxcPath,
                Path.Combine(shaderDir, shader.File),
                Path.Combine(outputDir, shader.File + ".cso"),
                shader.File, shader.Profile));
        }
    }

    private static void CompileShader(string fxcPath, string input, string output, string name, string profile)
    {
        try
        {
            // fxc 参数风格: /T <profile> /E <entry> /Fo <out> <in>
            var psi = new ProcessStartInfo
            {
                FileName = fxcPath,
                Arguments = $"/T {profile} /E {s_entry} /Fo \"{output}\" \"{input}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);

            if (proc?.ExitCode == 0 && File.Exists(output))
            {
                LogService.Info("Shader", $"✅ {name}.cso 编译成功 ({new FileInfo(output).Length} bytes)", LogCategory.Capture);
            }
            else
            {
                var err = proc?.StandardError.ReadToEnd() ?? "unknown";
                LogService.Warn("Shader", $"❌ {name} 编译失败: {err}", LogCategory.Capture);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Shader", $"❌ {name} 编译异常: {ex.Message}", LogCategory.Capture);
        }
    }

    /// <summary>定位 fxc.exe。候选顺序与构建期 CompileShaders.ps1 保持一致。</summary>
    private static string? FindFxc()
    {
        // 1. PATH 中的 fxc.exe
        try
        {
            var psi = new ProcessStartInfo("where", "fxc.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            var path = proc?.StandardOutput.ReadToEnd()?.Trim().Split('\n')[0].Trim();
            if (File.Exists(path)) return path;
        }
        catch { }

        // 2. 枚举 Windows Kits\10\bin\<version>\x64\fxc.exe，取版本号最高的可用项。
        //    ⚠️ 不要写死具体 SDK 版本号（2026-09-14 修正）：原实现硬编码 10.0.26100.0 / 10.0.22621.0，
        //       只装了其他版本（如 10.0.28000.0）的机器会静默跳过运行时编译，GPU 回退 CPU。
        //       必须与构建期 Shaders/CompileShaders.ps1 保持同一套查找逻辑。
        var kitRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "bin");

        if (Directory.Exists(kitRoot))
        {
            var best = Directory.EnumerateDirectories(kitRoot)
                .Select(d => new { Dir = d, Name = Path.GetFileName(d) })
                .Where(x => Version.TryParse(x.Name, out _))
                .OrderByDescending(x => Version.Parse(x.Name))
                .Select(x => Path.Combine(x.Dir, "x64", "fxc.exe"))
                .FirstOrDefault(File.Exists);

            if (best is not null) return best;
        }

        return null;
    }
}
