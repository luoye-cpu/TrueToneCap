// TrueToneCap.Core/Encoding/NativeWebPEncoder.cs
// WebP 编码器 — 通过 cwebp.exe 命令行工具编码 (内嵌在 Resources/cwebp.exe)
// 支持: 有损/无损, 8-bit, ICC 嵌入 (通过 cwebp 的 -metadata icc)
// 回退: cwebp.exe 不可用时自动回退到 PNG

using System.IO;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>WebP 编码器 — 通过 cwebp.exe 命令行工具。</summary>
public static class NativeWebPEncoder
{
    private static bool? _available;

    /// <summary>检测 cwebp.exe 是否可用（先在提取目录查找，再回退到 PATH）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeLibraryResolver.Initialize();
                var exePath = NativeLibraryResolver.GetExePath("cwebp.exe");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) { _available = false; return false; }
                // 必须读取 stdout/stderr，否则管道缓冲区满会导致进程挂起
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);
                _available = proc.ExitCode == 0;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>编码 BGRA 像素为 WebP 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        float quality = 90f, bool lossless = false, byte[]? iccProfile = null)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[WebP] cwebp.exe 不可用");

        // 写临时 PNG（使用最快压缩，仅为中间文件）
        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_{Environment.CurrentManagedThreadId:x8}.png");
        try
        {
            ManagedPngEncoder.EncodeFast(bgra, w, h, tmpPng, iccProfile);

            var exePath = NativeLibraryResolver.GetExePath("cwebp.exe");
            var qualityArg = lossless ? "-lossless" : $"-q {(int)quality}";
            var iccArg = iccProfile is { Length: > 0 } ? "-metadata icc" : "";
            var args = $"{qualityArg} {iccArg} -mt \"{tmpPng}\" -o \"{path}\"";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var result = NativeEncoderGuard.TryEncode("WebP", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cwebp");
                var stderrTask = proc.StandardError.ReadToEndAsync();
                proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(30_000))
                {
                    proc.Kill();
                    throw new InvalidOperationException("[WebP] cwebp 超时 (30s)");
                }
                stderrTask.GetAwaiter().GetResult();

                if (proc.ExitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[WebP] cwebp 失败 (exit={proc.ExitCode})");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[WebP] cwebp 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
        }
    }
}
