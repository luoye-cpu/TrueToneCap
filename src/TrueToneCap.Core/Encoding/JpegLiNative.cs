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
                    Arguments = "-h",
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
                // cjpegli -h 返回 exit code 1，但我们只关心能否启动
                _available = true;
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
                var psi = new System.Diagnostics.ProcessStartInfo("cjpegli.exe", "-h")
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
    /// <param name="forceBaseline">强制 Baseline Sequential (SOF0)。Gain Map Base JPEG 需要此模式。</param>
    /// <returns>JPEG 字节流 (FFD8...FFD9)。</returns>
    public static byte[] Encode(byte[] bgra, int width, int height, float distance = 1.0f,
        string chromaSubsampling = "444", byte[]? iccProfile = null, bool forceBaseline = false)
    {
        var exePath = GetExePath();

        // 写入临时 PPM 文件（cjpegli 支持 PPM 输入）
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jpegli_{Guid.NewGuid():N}.ppm");
        // 写入临时 ICC 文件
        string? tmpIcc = null;

        try
        {
            WriteBgraToPpm(bgra, width, height, tmpPpm);

            // cjpegli 的 --distance 接受浮点值，允许范围 [0.0, 25.0]
            // 重要: 传整数 90 会被解释为 d90.000（超出范围导致错误）
            // 调用方可能传 quality 值 (0-100)，必须 clamp 到合法范围
            float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);
            string distArg = $"{clampedDist:F4}";
            string chromaArg = chromaSubsampling switch
            {
                "420" => " --chroma_subsampling 420",
                "422" => " --chroma_subsampling 422",
                _ => "" // 444 默认
            };

            // 注意: 此版本 cjpegli 不支持 --icc_profile 参数
            // ICC 通过后处理注入: 编码后读回 JPEG 字节流，插入 APP1 marker
            bool hasNonSrgbIcc = iccProfile is { Length: > 128 };
            if (hasNonSrgbIcc)
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile!);
            }

            // Gain Map 需要 Baseline (SOF0)，常规 JPEG 用 Progressive (SOF2) 获得更小体积
            // --progressive_level 2: 默认 Progressive，体积小 5-10%
            // --progressive_level 0: Baseline，Gain Map 兼容性要求
            string progArg = forceBaseline ? " --progressive_level 0" : ""; // 默认 Progressive(2) 更省体积
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--distance {distArg}{progArg}{chromaArg} \"{tmpPpm}\" \"{tmpPpm}.jpg\"".Trim(),
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

            var jpegBytes = File.ReadAllBytes(outPath);

            // 后处理注入 ICC: 在 SOI 后插入 APP1 segment
            // cjpegli 不支持 --icc_profile，需要手动注入
            if (hasNonSrgbIcc && tmpIcc is not null)
            {
                jpegBytes = InjectIccIntoJpeg(jpegBytes, iccProfile!);
            }

            return jpegBytes;
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            try { File.Delete(tmpPpm + ".jpg"); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>在 JPEG SOI 后注入 ICC Profile 作为 APP1 段。</summary>
    private static byte[] InjectIccIntoJpeg(byte[] jpeg, byte[] iccProfile)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return jpeg;

        // 构建 APP1 marker + ICC 数据
        // APP1 marker: FF E1
        // Segment length: 2 + 2 + ICC_ID(12) + ICC data
        string iccId = "ICC_PROFILE\0";
        int iccIdLen = 12;
        int segLen = 2 + iccIdLen + iccProfile.Length;
        using var ms = new MemoryStream(jpeg.Length + segLen + 2);
        ms.Write(jpeg, 0, 2); // SOI
        // APP1 marker
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE1);
        ms.WriteByte((byte)(segLen >> 8));
        ms.WriteByte((byte)(segLen & 0xFF));
        // ICC identifier
        ms.Write(System.Text.Encoding.ASCII.GetBytes(iccId));
        // ICC data
        ms.Write(iccProfile);
        // Rest of JPEG
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
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
