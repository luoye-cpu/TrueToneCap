// TrueToneCap.App/Services/ShaderCompiler.cs
// HLSL → CSO 后台自动编译（启动时检测，缺失时自动编译）

using System.Diagnostics;

namespace TrueToneCap.App.Services;

/// <summary>HLSL 着色器后台编译器。</summary>
public static class ShaderCompiler
{
    private record ShaderDef(string File, string Profile);

    private static readonly ShaderDef[] s_shaders =
    [
        new("ToneMapping.hlsl", "ps_6_0"),
        new("MosaicEffect.hlsl", "ps_6_0"),
        new("CopyTexture.hlsl", "ps_6_0"),
        new("FullscreenVS.hlsl", "vs_6_0"),
    ];
    private const string s_entry = "main";

    /// <summary>确保所有 CSO 文件存在。缺失时后台自动编译。应在应用启动最开始调用。</summary>
    public static void EnsureCompiled(string shaderDir, string outputDir)
    {
        var dxcPath = FindDxc();
        if (dxcPath is null)
        {
            Debug.WriteLine("[Shader] DXC 编译器未找到，跳过编译（GPU 将回退 CPU）");
            return;
        }

        Directory.CreateDirectory(outputDir);

        foreach (var shader in s_shaders)
        {
            var inputPath = Path.Combine(shaderDir, shader.File);
            var outputPath = Path.Combine(outputDir, shader.File + ".cso");

            if (File.Exists(outputPath))
            {
                Debug.WriteLine($"[Shader] {shader.File}.cso 已存在 ({new FileInfo(outputPath).Length} bytes)");
                continue;
            }

            if (!File.Exists(inputPath))
            {
                Debug.WriteLine($"[Shader] {shader.File} 源文件未找到");
                continue;
            }

            // 后台编译
            Task.Run(() => CompileShader(dxcPath, inputPath, outputPath, shader.File, shader.Profile));
        }
    }

    private static void CompileShader(string dxcPath, string input, string output, string name, string profile)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dxcPath,
                Arguments = $"-T {profile} -E {s_entry} \"{input}\" -Fo \"{output}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);

            if (proc?.ExitCode == 0 && File.Exists(output))
            {
                Debug.WriteLine($"[Shader] ✅ {name}.cso 编译成功 ({new FileInfo(output).Length} bytes)");
            }
            else
            {
                var err = proc?.StandardError.ReadToEnd() ?? "unknown";
                Debug.WriteLine($"[Shader] ❌ {name} 编译失败: {err}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shader] ❌ {name} 编译异常: {ex.Message}");
        }
    }

    private static string? FindDxc()
    {
        // 1. PATH 中的 dxc.exe
        try
        {
            var psi = new ProcessStartInfo("where", "dxc.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            var path = proc?.StandardOutput.ReadToEnd()?.Trim().Split('\n')[0].Trim();
            if (File.Exists(path)) return path;
        }
        catch { }

        // 2. Windows SDK 默认路径
        var sdkPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Windows Kits\10\bin\10.0.22621.0\x64\dxc.exe"),
        };
        foreach (var p in sdkPaths)
            if (File.Exists(p)) return p;

        return null;
    }
}
