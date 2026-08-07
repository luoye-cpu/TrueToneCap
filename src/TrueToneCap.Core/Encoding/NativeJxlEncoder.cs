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
    /// <param name="bitDepth">目标位深 (8/10/12/16)。通过 cjxl 的 -b 传递容器位深。
    /// 注意: 输入为 8-bit byte[]，像素数据本身只有 8-bit 精度，位深仅影响容器声明。
    /// 若需真正 >8-bit 精度，请使用 EncodeHdr（接收 16-bit 数据）。</param>
    public static void Encode(byte[] bgra, int w, int h, string path,
        float distance = 1.0f, int bitDepth = 10, byte[]? iccProfile = null)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        // cjxl v0.11.2 读取某些 PNG 文件失败，改用 PPM 中间格式（与 JpegLiNative 相同策略）
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jxl_{Guid.NewGuid():N}.ppm");
        string? tmpIcc = null;

        try
        {
            // 写入临时 PPM 文件
            WriteBgraToPpm(bgra, w, h, tmpPpm);

            var exePath = GetExePath();

            // 色彩空间参数
            string colorArg;
            if (iccProfile is { Length: > 128 })
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                colorArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }
            else
            {
                // 无 ICC → 显式标记 sRGB 色彩空间
                colorArg = "-x color_space=sRGB";
            }

            // 容器位深: 8-bit 源强制 8-bit; 否则按请求位深 (仅容器声明, 数据仍为 8-bit)
            int effectiveBits = bitDepth is 10 or 12 or 16 ? bitDepth : 8;
            string bitsArg = effectiveBits == 8 ? "" : $" -b {effectiveBits}";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {distance:F4} -e 9{bitsArg} {colorArg} \"{tmpPpm}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            var result = NativeEncoderGuard.TryEncode("JXL", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
                var stderr = proc.StandardError.ReadToEnd();
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(60_000);

                if (proc.ExitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[JXL] cjxl 编码失败 (exit={proc.ExitCode}) {stderr.Trim()}");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[JXL] cjxl 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>编码 16-bit HDR RGBA 像素为 JXL 文件 (PQ, Rec2100)。</summary>
    /// <remarks>
    /// ⚠ 关键: 输入 <paramref name="rgba16"/> 必须是 **PQ 编码** 后的值（由 FormatHelper.HdrToPq16 生成）。
    /// cjxl 的 `-x color_space=Rec2100PQ` 将输入视为 display-referred PQ 值，
    /// 直接存储，不会再做 PQ 转换。因此必须用 16-bit PNG 中间文件（整数，无歧义）。
    /// 不要改用 PFM 浮点容器：PFM 会被 cjxl 当作线性光重新做 PQ 编码，导致双重编码、高光丢失。
    /// </remarks>
    public static void EncodeHdr(ushort[] rgba16, int w, int h, string path,
        float distance = 1.0f, byte[]? iccProfile = null, float intensityTarget = 10000f)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        // HDR 需要 16-bit 精度，用 16-bit PNG 中间文件（RGBA16 → BGRA16 大端）。
        // 不使用 PFM：PFM 浮点容器会被 cjxl 当作线性光，导致 PQ 双重编码回归。
        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_jxl_hdr_{Guid.NewGuid():N}.png");
        string? tmpIcc = null;

        try
        {
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
                Arguments = $"-d {distance:F4} -e 9 {iccArg} -x color_space=Rec2100PQ --intensity_target={intensityTarget:F0} \"{tmpPng}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            var result = NativeEncoderGuard.TryEncode("JXL_HDR", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
                var stderr = proc.StandardError.ReadToEnd();
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(60_000);

                if (proc.ExitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[JXL] cjxl HDR 编码失败 (exit={proc.ExitCode}) {stderr.Trim()}");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[JXL] cjxl HDR 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    // ═══════════════════════════════════════
    //  中间格式写入辅助
    // ═══════════════════════════════════════

    /// <summary>BGRA8 → PPM (用于 SDR cjxl 输入)。</summary>
    private static void WriteBgraToPpm(byte[] bgra, int w, int h, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new System.IO.StreamWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write($"P6\n{w} {h}\n255\n");
        writer.Flush();

        int pixelCount = w * h;
        for (int i = 0; i < pixelCount; i++)
        {
            int si = i * 4;
            fs.WriteByte(bgra[si + 2]); // R
            fs.WriteByte(bgra[si + 1]); // G
            fs.WriteByte(bgra[si]);     // B
        }
    }
}
