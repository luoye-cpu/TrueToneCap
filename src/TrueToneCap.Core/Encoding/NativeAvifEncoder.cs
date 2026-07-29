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
        Encode(bgra, w, h, path, crf, isHdr: false, chroma, bitDepth);
    }

    /// <summary>编码 BGRA 像素为 AVIF 文件，通过临时 Y4M 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf, bool isHdr, string chroma = "444", int bitDepth = 10)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用 请将 avifenc.exe 放入 native/ 目录");

        var tmpY4m = Path.Combine(Path.GetTempPath(), $"ttc_avif_{Guid.NewGuid():N}.y4m");
        try
        {
            WriteBgraToY4mFile(bgra, w, h, tmpY4m);

            var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
            int q = CrfToQuality(crf);
            var hdrArgs = isHdr ? "--cicp 9 16 0 --depth 10" : "";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-q {q} -s 6 {hdrArgs} --input-format y4m \"{tmpY4m}\" \"{path}\"".Trim(),
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

    /// <summary>将 BGRA 像素写入 Y4M 文件。</summary>
    private static void WriteBgraToY4mFile(byte[] bgra, int w, int h, string y4mPath)
    {
        using (var fs = new FileStream(y4mPath, FileMode.Create, FileAccess.Write))
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{w} H{h} F1:1 Ip A0:0 C444\nFRAME\n");
            fs.Write(header, 0, header.Length);
            int pixelCount = w * h;
            for (int i = 0; i < pixelCount; i++)
            {
                int si = i * 4;
                byte b = bgra[si];
                byte g = bgra[si + 1];
                byte r = bgra[si + 2];
                int y  = (66 * r + 129 * g + 25 * b + 128) / 256 + 16;
                int cb = (-38 * r - 74 * g + 112 * b + 128) / 256 + 128;
                int cr = (112 * r - 94 * g - 18 * b + 128) / 256 + 128;
                fs.WriteByte((byte)Math.Clamp(y, 16, 235));
                fs.WriteByte((byte)Math.Clamp(cb, 16, 240));
                fs.WriteByte((byte)Math.Clamp(cr, 16, 240));
            }
        }
    }

    /// <summary>从 CRF (0-63) 转换为 avifenc 质量值 (0-100, 100=无损)。</summary>
    private static int CrfToQuality(int crf) =>
        (int)Math.Clamp(100 - (crf * 100.0 / 63.0), 0, 100);

    /// <summary>直接从 PNG 文件编码为 AVIF（兼容旧接口，通过 avifenc WIC 读取 PNG）。</summary>
    public static void EncodeFile(string pngPath, string avifPath, int crf = 30)
    {
        EncodeFile(pngPath, avifPath, crf, isHdr: false);
    }

    /// <summary>直接从 PNG 文件编码为 AVIF，支持 HDR 参数。</summary>
    public static void EncodeFile(string pngPath, string avifPath, int crf, bool isHdr)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用");

        var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
        int q = CrfToQuality(crf);
        var hdrArgs = isHdr ? "--cicp 9 16 0 --depth 10" : "";
        // 使用 avifenc 直接读取 PNG 文件（通过 WIC，不依赖 libpng）
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-q {q} -s 6 {hdrArgs} --input-format png \"{pngPath}\" \"{avifPath}\"".Trim(),
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
                Arguments = $"-q {q} -s 6 --depth 10 --cicp 9 16 0 --input-format y4m \"{tmpY4m}\" \"{path}\"".Trim(),
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
