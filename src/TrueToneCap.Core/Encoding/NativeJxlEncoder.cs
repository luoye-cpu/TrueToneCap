// TrueToneCap.Core/Encoding/NativeJxlEncoder.cs
// JPEG XL 原生编码器 — 通过 cjxl.exe 命令行工具编码
// 内嵌在 Resources/cjxl.exe，启动时自动提取到 native/ 目录
// 与 avifenc/cwebp/cjpegli 相同的子进程模式，崩溃隔离 + 自动回退

using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>JPEG XL 原生编码器 — 通过 cjxl.exe 命令行工具编码。</summary>
public static class NativeJxlEncoder
{
    private static bool? _available;

    /// <summary>检测 cjxl 是否可用（先在 native 目录查找，再回退到 PATH）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeLibraryResolver.Initialize();
                var exePath = GetExePath();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) { _available = false; return false; }
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);
                _available = proc.ExitCode == 0;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    private static string GetExePath()
    {
        try { return NativeLibraryResolver.GetExePath("cjxl.exe"); }
        catch
        {
            // 回退到 PATH
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cjxl.exe", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0) return "cjxl.exe";
                }
            }
            catch { }
            throw new DllNotFoundException("[JXL] cjxl.exe 未找到。请将 cjxl.exe 放入 native/ 目录或系统 PATH。");
        }
    }

    /// <summary>编码 BGRA 像素为 JXL 文件 (SDR)。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        float distance = 1.0f, int bitDepth = 10, byte[]? iccProfile = null)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_jxl_{Guid.NewGuid():N}.png");
        string? tmpIcc = null;

        try
        {
            // 写入临时 PNG（8-bit 快速编码，带可选 ICC）
            ManagedPngEncoder.EncodeFast(bgra, w, h, tmpPng, iccProfile);

            var exePath = GetExePath();

            // 色彩空间参数
            string colorArg;
            if (iccProfile is { Length: > 128 })
            {
                // ICC 已嵌入临时 PNG 的 iCCP chunk，cjxl 会自动保留
                // 同时通过 -x icc_pathname= 显式传递以保正确性
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                colorArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }
            else
            {
                // 无 ICC → 显式标记 sRGB 色彩空间
                colorArg = "-x color_space=sRGB";
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {distance:F4} -e 9 {colorArg} --lossless_jpeg=0 \"{tmpPng}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60_000);

            if (proc.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"[JXL] cjxl 编码失败 (exit={proc.ExitCode}) {stderr.Trim()}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>编码 16-bit HDR RGBA 像素为 JXL 文件 (PQ, Rec2100)。</summary>
    public static void EncodeHdr(ushort[] rgba16, int w, int h, string path,
        float distance = 1.0f, byte[]? iccProfile = null, float intensityTarget = 10000f)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_jxl_hdr_{Guid.NewGuid():N}.png");
        string? tmpIcc = null;

        try
        {
            // 写入临时 16-bit PNG (RGBA16 → BGRA16 大端)
            var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(rgba16, w, h);
            ManagedPngEncoder.Encode16(bgra16, w, h, tmpPng);

            var exePath = GetExePath();

            // ICC 参数
            string iccArg = "";
            if (iccProfile is { Length: > 128 })
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                iccArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {distance:F4} -e 9 {iccArg} -x color_space=Rec2100PQ --intensity_target={intensityTarget:F0} --lossless_jpeg=0 \"{tmpPng}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60_000);

            if (proc.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"[JXL] cjxl HDR 编码失败 (exit={proc.ExitCode}) {stderr.Trim()}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }
}
