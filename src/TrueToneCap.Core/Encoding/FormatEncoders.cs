// TrueToneCap.Core/Encoding/FormatEncoders.cs
// 全部编码器 — 零 Magick.NET 依赖
// PNG: 托管编码器 | JPEG LI: jpegli P/Invoke | JXL: cjxl | AVIF: libavif/硬件 | WebP: libwebp | BMP: 托管
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.Core.Encoding;

// ────── PNG ──────
public sealed class PngEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.PNG;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (100f, 100f, 100f, "无损 (固定 100%)");
    public override string GetQualityDescription(float _) => "无损";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            // HDR: scRGB → PQ 16-bit PNG + cICP (C1 fix: 真 16-bit 精度)
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var p16 = FormatHelper.HdrToPq16(f);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte[] cicp = [9, 16, 0, 1]; // BT.2020 + ST.2084 PQ
                ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, path, cicp: cicp);
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdr(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (icc, cicp) = FormatHelper.GetColorMetadata(s);
            ManagedPngEncoder.Encode(px, w, h, path, s.OutputBitDepth, icc, cicp);
        }, ct);
    }
}

// ────── JPEG (ManagedJpegEncoder — 真正的基线 JPEG) ──────
public sealed class JpegLiEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_LI;
    public override bool SupportsHdr => false;
    public override (float, float, float, string) GetQualityRange() => (0.5f, 3.0f, 1.0f, "butteraugli 距离 (0.5-3.0)");
    public override string GetQualityDescription(float q) => $"距离: {q:F1} (越小越清晰)";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { var d = FormatHelper.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // butteraugli 距离 → JPEG 质量百分比
            int quality = (int)Math.Clamp(100f - (s.Quality - 0.5f) * 28f, 10, 100);
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
            ManagedJpegEncoder.Encode(px, w, h, path, quality, icc, s.ChromaSubsampling);
        }, ct);
    }
}

// ────── JPEG XL ──────
public sealed class JpegXlEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_XL;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (0.1f, 4.0f, 0.8f, "butteraugli 距离 (越小越清晰)");
    public override string GetQualityDescription(float q) => q <= 0.1f ? "近无损" : $"距离: {q:F1}";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            // HDR JXL: scRGB → PQ 16-bit → NativeJxlEncoder.EncodeHdr
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var pq16 = FormatHelper.HdrToPq16(f);
                // pq16 是 RGBA16 格式，直接传入 EncodeHdr
                var icc = s.IccProfile;
                NativeJxlEncoder.EncodeHdr(pq16, f.Width, f.Height, path, s.Quality, icc, 10000f);
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdr(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
            NativeJxlEncoder.Encode(px, w, h, path, s.Quality, s.OutputBitDepth, icc);
        }, ct);
    }
}

// ────── AVIF (动态后端) ──────
public sealed class AvifEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.AVIF;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (0f, 63f, 18f, "CRF (0=无损, 63=最低)");
    public override string GetQualityDescription(float q) => q <= 0 ? "无损" : $"CRF: {(int)q}";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            // HDR AVIF: 16-bit PNG 中转 + avifenc (C1 fix: 真 16-bit 中间 PNG)
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var p16 = FormatHelper.HdrToPq16(f);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte[] cicp = [9, 16, 0, 1];
                var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.png");
                try
                {
                    // C1 fix: 使用 Encode16 保留完整 16-bit 精度
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, tmpPng, cicp: cicp);
                    // avifenc 直接读取 16-bit PNG 文件，标记 HDR 以注入 CICP 元数据
                    NativeAvifEncoder.EncodeFile(tmpPng, path, (int)s.Quality, isHdr: true);
                }
                finally { try { File.Delete(tmpPng); } catch { } }
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdr(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    { var be = AvifEncoderSelector.Select(s.AvifBackend); await be.EncodeAsync(px, w, h, (int)s.Quality, path, ct, s.ChromaSubsampling, s.OutputBitDepth, s.ColorSpaceTag, s.IccProfile); }
}

// ────── WebP ──────
public sealed class WebPEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.WebP;
    public override bool SupportsHdr => false;
    public override (float, float, float, string) GetQualityRange() => (50f, 100f, 92f, "质量 (50-100)");
    public override string GetQualityDescription(float q) => q >= 100 ? "无损" : $"{(int)q}%";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { var d = FormatHelper.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
                NativeWebPEncoder.Encode(px, w, h, path, s.Quality, s.Quality >= 100, icc);
            }
            catch (DllNotFoundException)
            {
                // libwebp 不可用时回退到 PNG
                System.Diagnostics.Debug.WriteLine("[WebP] libwebp 不可用，回退 PNG");
                var pngPath = Path.ChangeExtension(path, ".png");
                var (icc2, cicp) = FormatHelper.GetColorMetadata(s);
                ManagedPngEncoder.Encode(px, w, h, pngPath, s.OutputBitDepth, icc2, cicp);
            }
        }, ct);
    }
}

// ────── BMP ──────
public sealed class BmpEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.BMP;
    public override bool SupportsHdr => false;
    public override (float, float, float, string) GetQualityRange() => (100f, 100f, 100f, "无损 (固定)");
    public override string GetQualityDescription(float _) => "无损";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { var d = FormatHelper.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ManagedBmpEncoder.Encode(px, w, h, path);
        }, ct);
    }
}

// ────── AVIF 后端 ──────
public static class AvifEncoderSelector
{
    private static readonly Dictionary<AvifEncoderBackend, IAvifEncoder> _be = new()
    {
        [AvifEncoderBackend.LibAom] = new LibAomAvifBackend(),
        [AvifEncoderBackend.Qsv] = new QsvAvifBackend(),
        [AvifEncoderBackend.Nvenc] = new NvencAvifBackend(),
    };
    private static readonly MftAvifBackend s_mftBackend = new();

    public static IAvifEncoder Select(AvifEncoderBackend pref)
    {
        if (pref == AvifEncoderBackend.Auto)
        {
            // 优先级: MFT (系统硬件) > NVENC raw API > QSV > libaom
            if (s_mftBackend.IsAvailable) return s_mftBackend;

            var encoders = GpuCapability.DetectEncoders();
            var nv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.NVENC && e.Available);
            var qsv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.QSV && e.Available);
            if (nv is not null) return _be[AvifEncoderBackend.Nvenc];
            if (qsv is not null) return _be[AvifEncoderBackend.Qsv];
            return _be[AvifEncoderBackend.LibAom];
        }
        var b = _be.GetValueOrDefault(pref) ?? _be[AvifEncoderBackend.LibAom];
        return b.IsAvailable ? b : _be[AvifEncoderBackend.LibAom];
    }
}

public sealed class LibAomAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.LibAom;
    public bool IsAvailable => NativeAvifEncoder.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (colorSpaceTag is not (null or "System" or "sRGB")) ? iccProfile : null;
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, chroma, displayBitDepth, icc);
        }, ct);
    }
}

public sealed class QsvAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Qsv;
    public bool IsAvailable => QsvEncoderNative.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // ═══ P/Invoke 崩溃隔离：CSE (AccessViolation/SEH) 安全 ═══
            var result = NativeEncoderGuard.TryEncode("QSV", () =>
            {
                using var qsv = new QsvEncoderNative();
                return qsv.EncodeAv1(bgra, w, h, crf);
            });

            if (result.Success)
            {
                IvfWriter.WriteAvif(result.Value!, w, h, path);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[AVIF] QSV 失败 ({result.Error?.Message})，回退 libaom");
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
        }, ct);
    }
}

public sealed class NvencAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Nvenc;
    public bool IsAvailable
    {
        get
        {
            try
            {
                var avail = NvEncoderNative.IsAvailable;
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC IsAvailable: {avail}");
                return avail;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 检测异常: {ex.Message}");
                return false;
            }
        }
    }

    // 缓存的 D3D11 设备 (避免每次编码创建新设备)
    private static ID3D11Device? s_cachedD3DDevice;
    private static readonly object s_deviceLock = new();

    /// <summary>设置共享 D3D11 设备 (由 WgcCaptureService 或 MainWindow 调用)。</summary>
    public static void SetSharedD3DDevice(ID3D11Device device)
    {
        lock (s_deviceLock)
        {
            s_cachedD3DDevice?.Dispose();
            s_cachedD3DDevice = device;
        }
    }

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // ═══ P/Invoke 崩溃隔离：CSE (AccessViolation/SEH) 安全 ═══
            var result = NativeEncoderGuard.TryEncode("NVENC", () =>
            {
                ID3D11Device? device = null;
                lock (s_deviceLock) { device = s_cachedD3DDevice; }

                if (device is null)
                {
                    System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: 创建新 D3D11 设备...");
                    device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: 复用共享 D3D11 设备");
                }

                using var nv = new NvEncoderNative(device);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = nv.EncodeAv1(bgra, w, h, crf);
                System.Diagnostics.Debug.WriteLine($"[AVIF] ✅ NVENC AV1: {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                return bs;
            });

            if (result.Success)
            {
                IvfWriter.WriteAvif(result.Value!, w, h, path);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，回退 libaom");
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
        }, ct);
    }
}

/// <summary>MFT AV1 编码后端 — 使用 Windows 系统内置硬件编码器 (NVIDIA/Intel/AMD 自适应)。</summary>
public sealed class MftAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Auto;
    public bool IsAvailable => MftEncoderNative.IsAv1MftAvailable;

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // ═══ P/Invoke 崩溃隔离：CSE (AccessViolation/SEH) 安全 ═══
            var result = NativeEncoderGuard.TryEncode("MFT", () =>
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF MFT] 编码 {w}x{h} CRF={crf}...");
                using var mft = new MftEncoderNative(w, h, useAv1: true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = mft.Encode(bgra);
                System.Diagnostics.Debug.WriteLine($"[AVIF MFT] ✅ {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                return bs;
            });

            if (result.Success)
            {
                IvfWriter.WriteAvif(result.Value!, w, h, path);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[AVIF MFT] 失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，回退 libaom");
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
        }, ct);
    }
}

// ────── AVIF 回退辅助 ──────
file static class AvifFallbackHelper
{
    /// <summary>安全回退到 libaom 软件编码。libaom 本身也做异常隔离，防止回退链断裂。</summary>
    public static void FallbackToLibAom(byte[] bgra, int w, int h, int crf, string path,
        CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var icc = (colorSpaceTag is not (null or "System" or "sRGB")) ? iccProfile : null;
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, chroma, displayBitDepth, icc);
        }
        catch (Exception ex)
        {
            // libaom 也失败 — 最终回退到 PNG 保底，确保截图不丢失
            System.Diagnostics.Debug.WriteLine($"[AVIF] ⚠️ libaom 回退也失败 ({ex.Message})，最终回退 PNG");
            try
            {
                var pngPath = Path.ChangeExtension(path, ".png");
                ManagedPngEncoder.Encode(bgra, w, h, pngPath, 8);
                System.Diagnostics.Debug.WriteLine($"[AVIF] PNG 保底成功: {pngPath}");
            }
            catch (Exception pngEx)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF] ❌ PNG 保底也失败: {pngEx.Message}");
                throw new AggregateException("所有 AVIF 编码后端均失败", ex, pngEx);
            }
        }
    }
}

// ────── 辅助 ──────
public static class FormatHelper
{
    public static byte[] ToSdr(HdrFrameData f, EncodingSettings s) => Processing.ToneMapper.FloatToSRgbBytes(f.Pixels, f.Width, f.Height, s.ToneMappingParams);

    /// <summary>根据 EncodingSettings 计算 ICC 和 CICP 元数据。</summary>
    public static (byte[]? icc, byte[]? cicp) GetColorMetadata(EncodingSettings s)
    {
        bool isSRgb = s.ColorSpaceTag is null or "System" or "sRGB";
        byte[]? icc = (!isSRgb && s.IccProfile is { Length: > 400 }) ? s.IccProfile : null;

        // CICP: 有 ICC 时不写 cICP（互斥策略）
        byte[]? cicp = null;
        if (icc is null)
        {
            (byte primaries, byte transfer) = s.ColorSpaceTag switch
            {
                "DisplayP3" or "DCI_P3" => ((byte)12, (byte)13),
                "BT2020" => ((byte)9, (byte)1),
                "AdobeRGB" => ((byte)1, (byte)13),  // N13 fix: transfer=13 (sRGB/sYCC)，原 0 是 CICP 保留值
                _ => ((byte)1, (byte)13) // sRGB / System
            };
            cicp = [primaries, transfer, 0, 1];
        }

        return (icc, cicp);
    }

    /// <summary>HDR scRGB → PQ 16-bit RGBA 数组（精确 10→16 bit 映射）。</summary>
    public static ushort[] HdrToPq16(HdrFrameData f)
    {
        int pixelCount = f.Width * f.Height;
        var p16 = new ushort[pixelCount * 4];
        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = LinearToPQ(f.Pixels[i]);
            float g = LinearToPQ(f.Pixels[i + 1]);
            float b = LinearToPQ(f.Pixels[i + 2]);
            float a = Math.Clamp(f.Pixels[i + 3], 0f, 1f);
            // M5 fix: 精确 10→16 bit 映射 (pq10 * 65535 / 1023)，而非 *64 截断
            p16[i]     = (ushort)Math.Clamp((int)Math.Round(r * 65535f), 0, 65535);
            p16[i + 1] = (ushort)Math.Clamp((int)Math.Round(g * 65535f), 0, 65535);
            p16[i + 2] = (ushort)Math.Clamp((int)Math.Round(b * 65535f), 0, 65535);
            p16[i + 3] = (ushort)Math.Clamp((int)Math.Round(a * 65535f), 0, 65535);
        });
        return p16;
    }

    /// <summary>RGBA 16-bit 数组 → BGRA 16-bit 字节数组（大端，供 PNG 16-bit 编码使用）。</summary>
    /// <remarks>C1 fix: 保留完整 16-bit 精度，不再截断为 8-bit。</remarks>
    public static byte[] Rgba16ToBgra16Bytes(ushort[] rgba16, int w, int h)
    {
        int pixelCount = w * h;
        var bgra16 = new byte[pixelCount * 8]; // 4 channels × 2 bytes
        for (int i = 0; i < pixelCount; i++)
        {
            int si = i * 4;
            int di = i * 8;
            // BGRA 16-bit big-endian (PNG 网络字节序)
            WriteU16BE(bgra16, di, rgba16[si + 2]);     // B
            WriteU16BE(bgra16, di + 2, rgba16[si + 1]); // G
            WriteU16BE(bgra16, di + 4, rgba16[si]);     // R
            WriteU16BE(bgra16, di + 6, rgba16[si + 3]); // A
        }
        return bgra16;
    }

    private static void WriteU16BE(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)val;
    }

    // ST.2084 PQ 常数 (SMPTE ST.2084)
    private const float PQ_m1 = 2610f / 16384f;
    private const float PQ_m2 = 2523f / 32f;
    private const float PQ_c1 = 3424f / 4096f;
    private const float PQ_c2 = 2413f / 128f;
    private const float PQ_c3 = 2392f / 128f;

    /// <summary>scRGB 线性光 → PQ (ST.2084) 感知量化编码。</summary>
    internal static float LinearToPQ(float scRgbLinear)
    {
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, PQ_m1);
        float numerator = PQ_c1 + PQ_c2 * Lp;
        float denominator = 1f + PQ_c3 * Lp;
        return MathF.Pow(numerator / denominator, PQ_m2);
    }
}

// ────── 工厂 ──────
public static class EncoderFactory
{
    public static ImageEncoder Create(OutputFormat f) => f switch
    { OutputFormat.PNG => new PngEncoder(), OutputFormat.JPEG_LI => new JpegLiEncoder(), OutputFormat.JPEG_XL => new JpegXlEncoder(), OutputFormat.AVIF => new AvifEncoder(), OutputFormat.WebP => new WebPEncoder(), OutputFormat.BMP => new BmpEncoder(), OutputFormat.JPEG_GAINMAP => new JpegGainMapEncoder(), _ => new PngEncoder() };
    public static OutputFormat Parse(string n) => n?.ToUpperInvariant() switch
    { "PNG" => OutputFormat.PNG, "JPEG LI" or "JPEGLI" => OutputFormat.JPEG_LI, "JPEG XL" or "JXL" => OutputFormat.JPEG_XL, "AVIF" => OutputFormat.AVIF, "WEBP" => OutputFormat.WebP, "BMP" => OutputFormat.BMP, "JPEG GAINMAP" or "JPEGGAINMAP" or "GAINMAP" or "ULTRAHDR" => OutputFormat.JPEG_GAINMAP, _ => OutputFormat.PNG };
}
