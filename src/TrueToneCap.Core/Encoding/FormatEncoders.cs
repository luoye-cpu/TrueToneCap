// TrueToneCap.Core/Encoding/FormatEncoders.cs
// 全部编码器 — 零 Magick.NET 依赖
// PNG: 托管编码器 | JPEG LI: jpegli P/Invoke | JXL: cjxl | AVIF: libavif/硬件 | WebP: libwebp | BMP: 托管
using System.Threading.Tasks;
using System.IO;
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
            // HDR: scRGB → 目标色域线性 → PQ → PNG (10/12/16-bit) + cICP
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = s.ColorSpaceTag ?? "sRGB";
                var p16 = FormatHelper.HdrToPq16(f, csTag);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(csTag);
                byte[] cicp = [primaries, 16, 0, 1]; // PQ transfer=16
                int hdrBitDepth = s.OutputBitDepth switch { 10 => 10, 12 => 12, _ => 16 };
                // IHDR 始终为 16-bit，sBIT 标记实际位深 (PNG 3.0 Table 12: color type 6 仅允许 8/16)
                ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, path, cicp: cicp, bitDepth: hdrBitDepth);
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
        // SDR PNG 始终使用 8-bit：8-bit 输入扩展到 10/12/16-bit 只会徒增体积而无精度收益
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (icc, cicp) = FormatHelper.GetColorMetadata(s);
            ManagedPngEncoder.Encode(px, w, h, path, 8, icc, cicp);
        }, ct);
    }
}

// ────── JPEG LI (jpegli — 唯一 JPEG 编码器) ──────
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
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;

            if (!JpegLiNative.IsAvailable)
                throw new InvalidOperationException("JPEG LI 编码需要 cjpegli.exe (Google jpegli)，请将 cjpegli.exe 放入 native/ 目录或系统 PATH。");

            var jpegBytes = JpegLiNative.Encode(px, w, h, s.Quality, s.ChromaSubsampling, icc);
            File.WriteAllBytes(path, jpegBytes);
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
            // HDR JXL: scRGB → 目标色域线性 → PQ 16-bit → NativeJxlEncoder.EncodeHdr
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = s.ColorSpaceTag ?? "sRGB";
                var pq16 = FormatHelper.HdrToPq16(f, csTag);
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
            // HDR AVIF: scRGB → 目标色域线性 → PQ 16-bit → 16-bit PNG 中转 + avifenc
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = s.ColorSpaceTag ?? "sRGB";
                var p16 = FormatHelper.HdrToPq16(f, csTag);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(csTag);
                byte[] cicpHdr = [primaries, 16, 0, 1]; // PQ transfer
                var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.png");
                try
                {
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, tmpPng, cicp: cicpHdr);
                    NativeAvifEncoder.EncodeFile(tmpPng, path, (int)s.Quality, cicpHdr);
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
    { var be = AvifEncoderSelector.Select(s.AvifBackend); await be.EncodeAsync(px, w, h, (int)s.Quality, path, ct, s.ChromaSubsampling, s.OutputBitDepth, s.ColorSpaceTag, s.IccProfile, s.GpuTexture); }
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

// ────── TIFF ──────
public sealed class TiffEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.TIFF;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (100f, 100f, 100f, "无损 (固定)");
    public override string GetQualityDescription(float _) => "无损";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            // HDR TIFF: 16-bit PQ (目标色域线性 → PQ)
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = s.ColorSpaceTag ?? "sRGB";
                var pq16 = FormatHelper.HdrToPq16(f, csTag);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(pq16, f.Width, f.Height);
                ManagedTiffEncoder.Encode(bgra16, f.Width, f.Height, path, 16, s.IccProfile);
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
            ManagedTiffEncoder.Encode(px, w, h, path, s.OutputBitDepth, icc);
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
            // 优先级: libaom (avifenc 嵌入, 最可靠) > MFT (系统硬件) > NVENC > QSV
            // libaom 优先于 MFT，因为 MFT 可能检测为可用但实际编码失败
            if (LibAomAvailable) return _be[AvifEncoderBackend.LibAom];
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

    private static bool LibAomAvailable => _be[AvifEncoderBackend.LibAom].IsAvailable;
}

public sealed class LibAomAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.LibAom;
    public bool IsAvailable => NativeAvifEncoder.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null)
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
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null)
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

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // ═══ GPU 纹理直通路径：NVENC 直接从 D3D11 纹理编码，跳过 CPU 回读 ═══
            if (texture is not null)
            {
                System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: GPU 纹理直通路径");
                var result = NativeEncoderGuard.TryEncode("NVENC_Texture", () =>
                {
                    ID3D11Device? device = null;
                    lock (s_deviceLock) { device = s_cachedD3DDevice; }
                    if (device is null)
                    {
                        System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: 创建新 D3D11 设备...");
                        device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                    }
                    using var nv = new NvEncoderNative(device);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var bs = nv.EncodeAv1FromTexture(texture, w, h, crf);
                    System.Diagnostics.Debug.WriteLine($"[AVIF] ✅ NVENC 纹理直通: {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                    return bs;
                });
                if (result.Success)
                {
                    IvfWriter.WriteAvif(result.Value!, w, h, path);
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 纹理路径失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，回退 CPU 路径");
                // 纹理路径失败，回退到 CPU 像素路径（可能纹理被设备释放）
            }

            // ═══ CPU 像素路径（原有回退）：纹理不可用或纹理路径失败时使用 ═══
            var result2 = NativeEncoderGuard.TryEncode("NVENC", () =>
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

            if (result2.Success)
            {
                IvfWriter.WriteAvif(result2.Value!, w, h, path);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 失败 ({result2.Error?.GetType().Name}: {result2.Error?.Message})，回退 libaom");
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
        }, ct);
    }
}

/// <summary>MFT AV1 编码后端 — 使用 Windows 系统内置硬件编码器 (NVIDIA/Intel/AMD 自适应)。</summary>
public sealed class MftAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Auto;
    public bool IsAvailable => MftEncoderNative.IsAv1MftAvailable;

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null)
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

    /// <summary>将 HDR 帧转换为 SDR BGRA8，写入预分配的目标缓冲区（避免额外分配）。</summary>
    public static void ToSdr(HdrFrameData f, EncodingSettings s, byte[] destination)
    {
        var result = Processing.ToneMapper.FloatToSRgbBytes(f.Pixels, f.Width, f.Height, s.ToneMappingParams);
        if (result.Length == destination.Length)
            Buffer.BlockCopy(result, 0, destination, 0, result.Length);
    }

    /// <summary>根据 EncodingSettings 计算 ICC 和 CICP 元数据。</summary>
    public static (byte[]? icc, byte[]? cicp) GetColorMetadata(EncodingSettings s)
    {
        bool isSRgb = s.ColorSpaceTag is null or "System" or "sRGB";
        byte[]? icc = (!isSRgb && s.IccProfile is { Length: > 400 }) ? s.IccProfile : null;

        // CICP: 有 ICC 时不写 cICP（互斥策略）
        byte[]? cicp = null;
        if (icc is null)
        {
            byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(s.ColorSpaceTag ?? "sRGB");
            byte transfer = ColorManagement.ColorSpaceConverter.GetCicpTransfer(s.ColorSpaceTag ?? "sRGB", hdrOutput: false);
            cicp = [primaries, transfer, 0, 1];
        }

        return (icc, cicp);
    }

    /// <summary>HDR scRGB → PQ 16-bit RGBA 数组（精确 10→16 bit 映射）。
    /// 在 PQ 编码前先将 scRGB (BT.709) 转换到目标色域线性空间，
    /// 确保像素值色域与 CICP/ICC 元数据一致。
    /// 融合色域转换 + PQ 编码，消除中间 float[] 分配（4K 节省 ~33MB 临时内存）。</summary>
    /// <param name="f">HDR 帧数据（scRGB 线性浮点像素）。</param>
    /// <param name="colorSpaceTag">目标色域标签，null/sRGB 时不转换。</param>
    public static ushort[] HdrToPq16(HdrFrameData f, string? colorSpaceTag = null)
    {
        int pixelCount = f.Width * f.Height;

        // ═══ 色域转换矩阵（scRGB BT.709 → 目标色域线性）═══
        var matrix = ColorManagement.ColorSpaceConverter.GetMatrix(colorSpaceTag ?? "sRGB");
        float m00 = 0, m01 = 0, m02 = 0;
        float m10 = 0, m11 = 0, m12 = 0;
        float m20 = 0, m21 = 0, m22 = 0;
        bool hasMatrix = false;
        if (matrix is not null)
        {
            hasMatrix = true;
            m00 = matrix[0, 0]; m01 = matrix[0, 1]; m02 = matrix[0, 2];
            m10 = matrix[1, 0]; m11 = matrix[1, 1]; m12 = matrix[1, 2];
            m20 = matrix[2, 0]; m21 = matrix[2, 1]; m22 = matrix[2, 2];
        }

        var src = f.Pixels;
        var p16 = new ushort[pixelCount * 4];
        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = src[i];
            float g = src[i + 1];
            float b = src[i + 2];
            float a = src[i + 3];

            // 融合色域转换（无矩阵时直通，无中间 float[] 分配）
            if (hasMatrix)
            {
                float rr = r * m00 + g * m01 + b * m02;
                float gg = r * m10 + g * m11 + b * m12;
                float bb = r * m20 + g * m21 + b * m22;
                r = rr; g = gg; b = bb;
            }

            // PQ 编码
            float pqR = LinearToPQ(r);
            float pqG = LinearToPQ(g);
            float pqB = LinearToPQ(b);
            a = Math.Clamp(a, 0f, 1f);
            // M5 fix: 精确 10→16 bit 映射 (pq10 * 65535 / 1023)，而非 *64 截断
            p16[i]     = (ushort)Math.Clamp((int)Math.Round(pqR * 65535f), 0, 65535);
            p16[i + 1] = (ushort)Math.Clamp((int)Math.Round(pqG * 65535f), 0, 65535);
            p16[i + 2] = (ushort)Math.Clamp((int)Math.Round(pqB * 65535f), 0, 65535);
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

// ────── 工厂（无状态编码器单例缓存，减少 GC 压力）──────
public static class EncoderFactory
{
    private static readonly PngEncoder s_png = new();
    private static readonly JpegLiEncoder s_jpegLi = new();
    private static readonly JpegXlEncoder s_jpegXl = new();
    private static readonly AvifEncoder s_avif = new();
    private static readonly WebPEncoder s_webp = new();
    private static readonly TiffEncoder s_tiff = new();
    private static readonly JpegGainMapEncoder s_gainMap = new();

    public static ImageEncoder Create(OutputFormat f) => f switch
    { OutputFormat.PNG => s_png, OutputFormat.JPEG_LI => s_jpegLi, OutputFormat.JPEG_XL => s_jpegXl, OutputFormat.AVIF => s_avif, OutputFormat.WebP => s_webp, OutputFormat.TIFF => s_tiff, OutputFormat.JPEG_GAINMAP => s_gainMap, _ => s_png };
    public static OutputFormat Parse(string n) => n?.ToUpperInvariant() switch
    { "PNG" => OutputFormat.PNG, "JPEG LI" or "JPEGLI" => OutputFormat.JPEG_LI, "JPEG XL" or "JXL" => OutputFormat.JPEG_XL, "AVIF" => OutputFormat.AVIF, "WEBP" => OutputFormat.WebP, "TIFF" or "TIF" => OutputFormat.TIFF, "JPEG GAINMAP" or "JPEGGAINMAP" or "GAINMAP" or "ULTRAHDR" => OutputFormat.JPEG_GAINMAP, _ => OutputFormat.PNG };
}
