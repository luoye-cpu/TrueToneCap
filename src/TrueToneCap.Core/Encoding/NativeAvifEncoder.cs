// TrueToneCap.Core/Encoding/NativeAvifEncoder.cs
// AVIF 原生编码器 — 通过 avifenc 命令行工具编码 (libaom 软件路径)
// 硬件编码路径 (MFT/NVENC/QSV) 保持不变

using System.Runtime.InteropServices;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>AVIF 原生编码器 — avifenc 命令行 (libaom)，内嵌在 Resources/avifenc.exe。</summary>
public static class NativeAvifEncoder
{
    // 静态构造器：确保嵌入的 avifenc.exe 已提取到 native/ 目录
    static NativeAvifEncoder()
    {
        NativeLibraryResolver.Initialize();
    }

    private static bool? _available;

    /// <summary>检测 avifenc 是否可用（先在提取目录查找，再回退到 PATH）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeLibraryResolver.Initialize();
                var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
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

    /// <summary>编码 BGRA 像素为 AVIF 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf = 30, string chroma = "444", int bitDepth = 10, byte[]? iccProfile = null)
    {
        Encode(bgra, w, h, path, crf, isHdr: false, chroma, bitDepth, iccProfile);
    }

    /// <summary>编码 BGRA 像素为 AVIF 文件，通过临时 Y4M 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf, bool isHdr, string chroma = "444", int bitDepth = 10, byte[]? iccProfile = null)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用 请将 avifenc.exe 放入 native/ 目录");

        var tmpY4m = Path.Combine(Path.GetTempPath(), $"ttc_avif_{Guid.NewGuid():N}.y4m");
        try
        {
            WriteBgraToY4mFile(bgra, w, h, tmpY4m, chroma);

            var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
            int q = CrfToQuality(crf);

            // 构建 CICP 参数：根据 isHdr 和色彩空间选择
            string cicpArgs = isHdr ? "--cicp 9/16/0 --depth 10" : "--cicp 1/13/0";

            // 色度采样参数
            string chromaArg = chroma switch
            {
                "420" => "--yuv 420",
                "422" => "--yuv 422",
                _ => "--yuv 444"
            };

            // ICC 参数
            string iccArg = "";
            if (iccProfile is { Length: > 128 })
            {
                var iccPath = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                try
                {
                    File.WriteAllBytes(iccPath, iccProfile);
                    iccArg = $"--icc \"{iccPath}\"";
                }
                catch { }
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-q {q} -s 6 {chromaArg} {cicpArgs} {iccArg} \"{tmpY4m}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 avifenc");
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);

            if (proc.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={proc.ExitCode}) {stderr.Trim()}");
        }
        finally
        {
            try { File.Delete(tmpY4m); } catch { }
        }
    }

    /// <summary>将 BGRA 像素写入 Y4M 文件，支持色度采样。</summary>
    private static void WriteBgraToY4mFile(byte[] bgra, int w, int h, string y4mPath, string chroma = "444")
    {
        using (var fs = new FileStream(y4mPath, FileMode.Create, FileAccess.Write))
        {
            // Y4M 色度标记
            string y4mChroma = chroma switch
            {
                "420" => "C420jpeg",
                "422" => "C422jpeg",
                _ => "C444jpeg"
            };
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{w} H{h} F1:1 Ip A0:0 {y4mChroma}\nFRAME\n");
            fs.Write(header, 0, header.Length);

            int pixelCount = w * h;
            if (chroma == "444")
            {
                // 4:4:4 — 每像素 3 字节，BT.709 全范围矩阵
                for (int i = 0; i < pixelCount; i++)
                {
                    int si = i * 4;
                    byte b = bgra[si];
                    byte g = bgra[si + 1];
                    byte r = bgra[si + 2];
                    // BT.709 全范围 (JPEG 风格)
                    int y  = (int)( 0.299f   * r + 0.587f   * g + 0.114f   * b + 0.5f);
                    int cb = (int)(-0.168736f * r - 0.331264f * g + 0.5f     * b + 128.5f);
                    int cr = (int)( 0.5f     * r - 0.418688f * g - 0.081312f * b + 128.5f);
                    fs.WriteByte((byte)Math.Clamp(y, 0, 255));
                    fs.WriteByte((byte)Math.Clamp(cb, 0, 255));
                    fs.WriteByte((byte)Math.Clamp(cr, 0, 255));
                }
            }
            else if (chroma == "422")
            {
                // 4:2:2 — 水平 2× 下采样
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        int si = (row * w + col) * 4;
                        byte b = bgra[si];
                        byte g = bgra[si + 1];
                        byte r = bgra[si + 2];
                        int y = (int)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f);
                        fs.WriteByte((byte)Math.Clamp(y, 0, 255));
                        if (col % 2 == 0)
                        {
                            int cb = (int)(-0.168736f * r - 0.331264f * g + 0.5f * b + 128.5f);
                            int cr = (int)(0.5f * r - 0.418688f * g - 0.081312f * b + 128.5f);
                            fs.WriteByte((byte)Math.Clamp(cb, 0, 255));
                            fs.WriteByte((byte)Math.Clamp(cr, 0, 255));
                        }
                    }
                }
            }
            else // 420
            {
                // 4:2:0 — 2×2 块下采样
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        int si = (row * w + col) * 4;
                        byte b = bgra[si];
                        byte g = bgra[si + 1];
                        byte r = bgra[si + 2];
                        int y = (int)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f);
                        fs.WriteByte((byte)Math.Clamp(y, 0, 255));
                        if (row % 2 == 0 && col % 2 == 0)
                        {
                            // 2×2 块平均
                            float sumR = 0, sumG = 0, sumB = 0;
                            int cnt = 0;
                            for (int dy = 0; dy < 2; dy++)
                            {
                                for (int dx = 0; dx < 2; dx++)
                                {
                                    int sx = Math.Min(col + dx, w - 1);
                                    int sy = Math.Min(row + dy, h - 1);
                                    int si2 = (sy * w + sx) * 4;
                                    sumR += bgra[si2 + 2];
                                    sumG += bgra[si2 + 1];
                                    sumB += bgra[si2];
                                    cnt++;
                                }
                            }
                            float avgR = sumR / cnt, avgG = sumG / cnt, avgB = sumB / cnt;
                            int cb = (int)(-0.168736f * avgR - 0.331264f * avgG + 0.5f * avgB + 128.5f);
                            int cr = (int)(0.5f * avgR - 0.418688f * avgG - 0.081312f * avgB + 128.5f);
                            fs.WriteByte((byte)Math.Clamp(cb, 0, 255));
                            fs.WriteByte((byte)Math.Clamp(cr, 0, 255));
                        }
                    }
                }
            }
        }
    }

    /// <summary>从 CRF (0-63) 转换为 avifenc 质量值 (0-100, 100=无损)。</summary>
    private static int CrfToQuality(int crf) =>
        (int)Math.Clamp(100 - (crf * 100.0 / 63.0), 0, 100);

    /// <summary>直接从 PNG 文件编码为 AVIF，支持 CICP 参数。</summary>
    public static void EncodeFile(string pngPath, string avifPath, int crf, byte[]? cicp = null)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用");

        var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
        int q = CrfToQuality(crf);

        // 从 cicp 字节数组构建参数，默认 sRGB
        string cicpArgs = "--cicp 1/13/0";
        if (cicp is { Length: 4 })
        {
            cicpArgs = $"--cicp {cicp[0]}/{cicp[1]}/{cicp[2]}";
            if (cicp[1] == 16) // ST.2084 PQ → 10-bit
                cicpArgs += " --depth 10";
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-q {q} -s 6 {cicpArgs} \"{pngPath}\" \"{avifPath}\"".Trim(),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("无法启动 avifenc");
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);
        if (proc.ExitCode != 0 || !File.Exists(avifPath))
            throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={proc.ExitCode}) {stderr.Trim()}");
    }

    /// <summary>从 scRGB linear float[] 编码 HDR AVIF 文件（PQ + BT.2020 + 10-bit）。</summary>
    public static void EncodeHdrFromScrgb(float[] scRgbLinear, int w, int h, string path, int crf)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用");

        // scRGB linear → PQ 10-bit → 临时 Y4M 文件 → avifenc
        var tmpY4m = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.y4m");
        try
        {
            // 写入 Y4M 文件（10-bit 大端序）
            using (var fs = new FileStream(tmpY4m, FileMode.Create, FileAccess.Write))
            {
                // 写入 Y4M 头（使用二进制写入避免 StreamWriter 缓冲问题）
                byte[] header = System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{w} H{h} F1:1 Ip A0:0 C444p10\nFRAME\n");
                fs.Write(header, 0, header.Length);
                int pixelCount = w * h;
                for (int i = 0; i < pixelCount; i++)
                {
                    int si = i * 4;
                    float rLin = scRgbLinear[si];
                    float gLin = scRgbLinear[si + 1];
                    float bLin = scRgbLinear[si + 2];
                    int y10 = FloatToPq10(rLin, gLin, bLin, out int cb10, out int cr10);
                    WriteU16BE(fs, y10);
                    WriteU16BE(fs, cb10);
                    WriteU16BE(fs, cr10);
                }
            }

            var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
            int q = CrfToQuality(crf);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-q {q} -s 6 --depth 10 --cicp 9/16/0 \"{tmpY4m}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 avifenc");
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60_000);
            if (proc.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={proc.ExitCode}) {stderr.Trim()}");
        }
        finally
        {
            try { File.Delete(tmpY4m); } catch { }
        }
    }

    private static void WriteU16BE(Stream s, int val)
    {
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)val);
    }

    /// <summary>scRGB linear RGB → BT.2020 PQ 10-bit YCbCr (BT.2020 矩阵)。</summary>
    private static int FloatToPq10(float r, float g, float b, out int cb, out int cr)
    {
        // scRGB linear (BT.709 原色) → BT.2020 RGB 色域转换
        // 矩阵: ITU-R BT.709 linear → ITU-R BT.2020 linear (D65→D65)
        float r2020 = 0.627404f * r + 0.329283f * g + 0.043313f * b;
        float g2020 = 0.069097f * r + 0.919541f * g + 0.011362f * b;
        float b2020 = 0.016391f * r + 0.088013f * g + 0.895596f * b;

        // PQ 编码 (ST.2084)
        float pqR = LinearToPq(r2020);
        float pqG = LinearToPq(g2020);
        float pqB = LinearToPq(b2020);

        // BT.2020 非恒定亮度 YCbCr (10-bit 范围)
        float yVal  = 0.2627f * pqR + 0.6780f * pqG + 0.0593f * pqB;
        float cbVal = (-0.2627f * pqR - 0.6780f * pqG + 0.9407f * pqB) / 1.8814f;
        float crVal = (0.9373f * pqR - 0.6780f * pqG - 0.0593f * pqB) / 1.4746f;

        // 量化到 10-bit (0-1023)
        int yInt  = (int)Math.Round(Math.Clamp(yVal, 0f, 1f) * 1023f);
        cb = (int)Math.Round(Math.Clamp(cbVal * 0.5f + 0.5f, 0f, 1f) * 1023f);
        cr = (int)Math.Round(Math.Clamp(crVal * 0.5f + 0.5f, 0f, 1f) * 1023f);
        return yInt;
    }

    /// <summary>scRGB linear → PQ (ST.2084) 编码。</summary>
    private static float LinearToPq(float linear)
    {
        float nits = Math.Max(linear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, 2610f / 16384f);
        float num = 3424f / 4096f + (2413f / 128f) * Lp;
        float den = 1f + (2392f / 128f) * Lp;
        return MathF.Pow(num / den, 2523f / 32f);
    }
}
