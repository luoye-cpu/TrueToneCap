// TrueToneCap.Core/Services/ToolchainHelper.cs
// 原生工具链辅助 — 统一子进程检测 + 路径查找
// 消除 JpegLiNative/NativeJxlEncoder/NativeAvifEncoder/NativeWebPEncoder 中的重复检测代码

using System.Diagnostics;

namespace TrueToneCap.Core.Services;

/// <summary>原生工具链辅助：统一子进程可用性检测。</summary>
public static class ToolchainHelper
{
    /// <summary>检测指定原生工具是否可用（通过子进程启动测试）。</summary>
    /// <param name="exeName">可执行文件名，如 "cjpegli.exe"。</param>
    /// <param name="testArgs">检测用的命令行参数，如 "-h"。</param>
    /// <param name="successExitCodes">视为成功的退出码集合。默认仅 0。</param>
    public static bool CheckAvailable(string exeName, string testArgs, params int[] successExitCodes)
    {
        try
        {
            NativeLibraryResolver.Initialize();
            var exePath = NativeLibraryResolver.GetExePath(exeName);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = testArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (successExitCodes.Length == 0)
                return proc.ExitCode == 0;
            return Array.Exists(successExitCodes, c => c == proc.ExitCode);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取原生工具的可执行文件路径。</summary>
    public static string GetExePath(string exeName)
    {
        return NativeLibraryResolver.GetExePath(exeName);
    }
}