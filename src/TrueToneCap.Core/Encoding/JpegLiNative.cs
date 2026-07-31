// TrueToneCap.Core/Encoding/JpegLiNative.cs
// Google jpegli 编码器 — 通过 cjpegli 命令行工具编码
// 内嵌在 Resources/cjpegli.exe，运行时自动提取到 native/ 目录
// 与现有 avifenc.exe/cwebp.exe 相同的嵌入式工具链模式

using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// jpegli 编码器 — 通过 cjpegli 命令行工具编码。
/// 内嵌在 Resources/cjpegli.exe，启动时自动提取到 native/ 目录。
/// 与 avifenc 相同的子进程调用模式，保证了崩溃隔离和兼容性。
/// </summary>
public static class JpegLiNative
{
    private static bool? _available;

    /// <summary>检测 cjpegli 是否可用（先在 native 目录查找，再回退到 PATH）。</summary>
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

    /// <summary>初始化检测。</summary>
    public static void Initialize()
    {
        if (IsAvailable)
            System.Diagnostics.Debug.WriteLine("[JpegLiNative] cjpegli 就绪");
        else
            System.Diagnostics.Debug.WriteLine("[JpegLiNative] ❌ cjpegli 不可用，JPEG 编码将失败");
    }

    private static string GetExePath()
    {
        try { return NativeLibraryResolver.GetExePath("cjpegli.exe"); }
        catch
        {
            // 回退到 PATH
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cjpegli.exe", "--version")
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
                    if (proc.ExitCode == 0) return "cjpegli.exe";
                }
            }
            catch { }
            throw new DllNotFoundException("[jpegli] cjpegli.exe 未找到。请将 cjpegli.exe 放入 native/ 目录或系统 PATH。");
        }
    }

    /// <summary>BGRA8→JPEG 编码（支持色度采样和 ICC 嵌入）。</summary>
    /// <param name="bgra">BGRA8 像素数据。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="distance">butteraugli 距离 (0.5~=无损, 1.0=高质量, 3.0=低质量)。</param>
    /// <param name="chromaSubsampling">色度采样: "444" / "422" / "420"。</param>
    /// <param name="iccProfile">可选 ICC Profile。</param>
    /// <returns>JPEG 字节流 (FFD8...FFD9)。</returns>
    public static byte[] Encode(byte[] bgra, int width, int height, float distance = 1.0f,
        string chromaSubsampling = "444", byte[]? iccProfile = null)
    {
        var exePath = GetExePath();

        // 写入临时 PPM 文件（cjpegli 支持 PPM 输入）
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jpegli_{Guid.NewGuid():N}.ppm");
        // 写入临时 ICC 文件
        string? tmpIcc = null;

        try
        {
            WriteBgraToPpm(bgra, width, height, tmpPpm);

            int distQ4 = (int)Math.Round(Math.Clamp(distance, 0.1f, 15f) * 4);
            string chromaArg = chromaSubsampling switch
            {
                "420" => "--chroma_subsampling 420",
                "422" => "--chroma_subsampling 422",
                _ => "" // 444 默认
            };

            string iccArg = "";
            if (iccProfile is { Length: > 128 })
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                iccArg = $"--icc_profile \"{tmpIcc}\"";
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--distance {distQ4} {chromaArg} {iccArg} \"{tmpPpm}\" \"{tmpPpm}.jpg\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 cjpegli");
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);

            string outPath = tmpPpm + ".jpg";
            if (proc.ExitCode != 0 || !File.Exists(outPath))
                throw new InvalidOperationException($"[jpegli] cjpegli 失败 (exit={proc.ExitCode}) {stderr.Trim()}");

            return File.ReadAllBytes(outPath);
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            try { File.Delete(tmpPpm + ".jpg"); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>将 BGRA8 像素写入 PPM 文件（彩色 P6 格式）。</summary>
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
