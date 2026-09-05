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
            if (s.OutputBitDepth <= 8)
            {
                // 用户选择 8-bit → 色调映射到 SDR，走 SDR 编码路径
                var d = FormatHelper.ToSdrAndDegrade(f, s);
                await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
            }
            else
            {
                // HDR: scRGB → 目标色域线性 → PQ → PNG (10/12/16-bit) + cICP
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var csTag = s.ColorSpaceTag ?? "sRGB";
                    int hdrBitDepth = s.OutputBitDepth switch { 10 => 10, 12 => 12, _ => 16 };
                    // HdrToPq16 直接量化到目标位深，再左对齐到 16-bit 容器
                    var p16 = FormatHelper.HdrToPq16(f, csTag, hdrBitDepth);
                    var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                    byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(csTag);
                    byte[] cicp = [primaries, 16, 0, 1]; // PQ transfer=16
                    // ═══ 2026-08-09 修复: HDR 不嵌 ICC ═══
                    // PNG 3.0 规范: cICP 优先于 iCCP。嵌入的 iCCP (标准 ICC, sRGB TRC)
                    // 与 PQ 像素矛盾 → 按 iCCP 解码的查看器图像错误。CICP 声明足够。
                    byte[]? icc = null;
                    // IHDR 始终为 16-bit，sBIT 标记实际位深 (PNG 3.0 Table 12: color type 6 仅允许 8/16)
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, path, cicp: cicp, iccProfile: icc, bitDepth: hdrBitDepth);
                }, ct);
            }
        }
        else
        {
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }

    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // 完全尊重用户设置的 OutputBitDepth
        int bitDepth = s.OutputBitDepth switch
        {
            10 => 10,
            12 => 12,
            >= 16 => 16,
            _ => 8,
        };
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (icc, cicp) = FormatHelper.GetColorMetadata(s);
            ManagedPngEncoder.Encode(px, w, h, path, bitDepth, icc, cicp);
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
    {
        // JPEG LI 不支持 HDR，始终降级到 SDR (ToSdrAndDegrade 统一色调映射 + sRGB 覆盖)
        var d = FormatHelper.ToSdrAndDegrade(f, s);
        await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;

            if (!JpegLiNative.IsAvailable)
                throw new InvalidOperationException("JPEG LI 编码需要 cjpegli.exe (Google jpegli)，请将 cjpegli.exe 放入 native/ 目录、PLAN/tools/ 目录或系统 PATH。");

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
                // JXL HDR 固定 16-bit 全精度
                var pq16 = FormatHelper.HdrToPq16(f, csTag, 16);
                // ═══ 2026-08-10 修复(2): intensity_target = 显示器 HDR 峰值 ═══
                // 截图内容的主控目标 = 源显示器 (截的就是显示器显示的)。
                // PQ 是绝对亮度编码 (码值→绝对 nits), intensity_target 语义 =
                // "内容亮度上限" = 显示器峰值。查看器据此做 tone-map:
                // - 显示器能力 ≥ intensity_target → 绝对亮度直显 (SDR 白点 200nits 正确)
                // - 显示器能力 <  → 按比例压缩
                // 错误历史:
                //   恒 10000 → 查看器把 200nits SDR 内容在 10000nits 容器 tone-map → 极暗
                //   内容实际峰值(822) → 部分查看器 re-normalize 内容峰值→显示器峰值
                //     → SDR 白点被抬亮 → 发灰 (用户指出的问题)
                // 正确: 显示器峰值 (DisplayMaxNits 系统检测优先) + 内容峰值兜底防 clip
                float intensityTarget = s.ToneMappingParams.DisplayMaxNits;
                if (intensityTarget <= 0f) intensityTarget = 1000f; // 兜底: HDR 显示器常见峰值
                float contentPeak = FormatHelper.ComputePeakNits(pq16);
                if (intensityTarget < contentPeak) intensityTarget = contentPeak;
                // ═══ 2026-08-09 修复: JXL HDR 不嵌 ICC ═══
                // `-x color_space=Rec2100PQ` 已声明 BT.2020 + PQ + intensity_target。
                // 嵌入 ICC (标准 ICC, sRGB TRC) 会覆盖 color fields → PQ 传输丢失 →
                // 解码器按 sRGB gamma 解码 PQ 值 → 图像错误。
                NativeJxlEncoder.EncodeHdr(pq16, f.Width, f.Height, path, s.Quality, null, intensityTarget);
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdrAndDegrade(f, s);
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
                // AVIF HDR 固定 10-bit 全精度
                var p16 = FormatHelper.HdrToPq16(f, csTag, 10);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(csTag);
                byte[] cicpHdr = [primaries, 16, 0, 1]; // PQ transfer
                // ═══ 2026-08-09 修复: HDR 不嵌 ICC (同 PNG, CICP 优先声明) ═══
                byte[]? iccHdr = null;
                var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.png");
                try
                {
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, tmpPng, cicp: cicpHdr, iccProfile: iccHdr);
                    // ═══ 2026-08-10 修复: AVIF HDR clli = 显示器 HDR 峰值 (同 JXL) ═══
                    // MaxCLL 语义 = 内容亮度上限 = 显示器峰值。恒 10000 → SDR 内容极暗。
                    float peakNits = s.ToneMappingParams.DisplayMaxNits;
                    if (peakNits <= 0f) peakNits = 1000f;
                    float contentPeakAvif = FormatHelper.ComputePeakNits(p16);
                    if (peakNits < contentPeakAvif) peakNits = contentPeakAvif;
                    NativeAvifEncoder.EncodeFile(tmpPng, path, (int)s.Quality, cicpHdr, csTag, peakNits);
                }
                finally { try { File.Delete(tmpPng); } catch { } }
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdrAndDegrade(f, s);
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
    {
        // WebP 不支持 HDR，始终降级到 SDR (ToSdrAndDegrade 统一色调映射 + sRGB 覆盖)
        var d = FormatHelper.ToSdrAndDegrade(f, s);
        await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
    }
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
                // TIFF HDR 固定 16-bit 全精度
                var pq16 = FormatHelper.HdrToPq16(f, csTag, 16);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(pq16, f.Width, f.Height);
                // ═══ 2026-08-09 修复: TIFF HDR 嵌 PQ ICC ═══
                // TIFF 无 CICP 机制, 必须嵌 ICC。但标准 ICC 是 sRGB TRC, 与 PQ 像素矛盾。
                // 用 PQ TRC ICC (GetHdrStandardIccProfile), 否则解码器按 sRGB gamma 解码 PQ 值。
                byte[]? iccHdr = ColorManagement.ColorProfileProvider.GetHdrStandardIccProfile(csTag);
                // 显式声明输入为 16-bit，避免依赖数组长度推断
                ManagedTiffEncoder.Encode(bgra16, f.Width, f.Height, path, 16, iccHdr, inputBitDepth: 16);
            }, ct);
        }
        else
        {
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
            // SDR 输入恒为 8-bit BGRA（每像素 4 字节），显式声明避免按长度推断
            ManagedTiffEncoder.Encode(px, w, h, path, s.OutputBitDepth, icc, inputBitDepth: 8);
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
            var encoders = GpuCapability.DetectEncoders();

            // ⚠ 必须同时校验 SupportsAv1：GpuCapability 已通过 DeviceId 范围与适配器名称
            // 精确判定 AV1 编码能力，而 Available 仅表示"NVENC 会话可创建"
            // （RTX 30 的 HEVC 可用即返回 true），并不代表支持 AV1 编码。
            // 缺少该过滤会导致 RTX 30 及更早显卡被选中 → AV1 编码失败 → 回退软件编码，
            // 白白经历一次硬件初始化开销。
            var nv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.NVENC && e.Available && e.SupportsAv1);
            var qsv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.QSV && e.Available && e.SupportsAv1);

            // ═══ 2026-08-30 修复: Auto 模式下硬件编码从未被选中 ═══
            // 原实现把 libaom 置于最高优先级，而 avifenc.exe 是**随程序内嵌发布**的，
            // 因此 LibAomAvailable 恒为 true —— NVENC/QSV 分支成为永远走不到的死代码。
            // 后果：即使配备 RTX 4090，4K AVIF 也要跑约 22 秒软件编码。
            //
            // 现调整为「有明确 AV1 能力的硬件优先」：
            //   · 硬件后端内部均有完善回退（NvencAvifBackend 失败 → AvifFallbackHelper → libaom），
            //     故优先尝试硬件不会降低可靠性，最坏情况等价于原行为。
            //   · SupportsAv1 为 false 的设备（如 RTX 30）不会被选中，避免无谓的失败回退。
            //   · DetectEncoders() 结果有缓存，探测开销只发生一次。
            if (nv is not null) return _be[AvifEncoderBackend.Nvenc];
            if (qsv is not null) return _be[AvifEncoderBackend.Qsv];

            // 无可用硬件 AV1 编码器 → 软件路径
            // libaom 优先于 MFT：MFT 可能检测为可用但实际编码失败
            if (LibAomAvailable) return _be[AvifEncoderBackend.LibAom];
            if (s_mftBackend.IsAvailable) return s_mftBackend;
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
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, isHdr: false, chroma: chroma, bitDepth: displayBitDepth, iccProfile: icc, colorSpaceTag: colorSpaceTag);
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
                IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag);
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

    // ═══ 2026-08-25 P2 性能优化: NVENC 失败缓存 ═══
    // 纹理路径失败后缓存结果, 避免后续每次调用都重试纹理直通路径.
    // 场景: 用户第一次截图纹理路径失败 (设备/驱动状态), 后续每次仍重试 → 浪费.
    private static volatile bool s_texturePathFailed;
    private static volatile bool s_nvencFailed;

    public bool IsAvailable
    {
        get
        {
            try
            {
                if (s_nvencFailed) return false; // 快速短路: 上次已失败
                var avail = NvEncoderNative.IsAvailable;
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC IsAvailable: {avail}");
                return avail;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 检测异常: {ex.Message}");
                s_nvencFailed = true;
                return false;
            }
        }
    }

    /// <summary>重置失败缓存 (驱动恢复/显示器切换时调用)。</summary>
    public static void ResetFailureCache()
    {
        s_texturePathFailed = false;
        s_nvencFailed = false;
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
            ResetFailureCache(); // 新设备 = 重置缓存
        }
    }

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 快速短路: 若 NVENC 完全不可用, 直接回退 libaom
            if (s_nvencFailed)
            {
                System.Diagnostics.Debug.WriteLine("[AVIF] NVENC 已缓存失败, 直接回退 libaom");
                AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
                return;
            }

            // 获取共享 D3D11 设备 (纹理路径 + CPU 路径共用)
            ID3D11Device? device = null;
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            bool ownDevice = false;
            if (device is null)
            {
                System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: 创建新 D3D11 设备...");
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                ownDevice = true;
            }

            try
            {
            // ═══ GPU 纹理直通路径 (仅当纹理可用且未缓存失败) ═══
            if (texture is not null && !s_texturePathFailed)
            {
                System.Diagnostics.Debug.WriteLine("[AVIF] NVENC: GPU 纹理直通路径");
                var result = NativeEncoderGuard.TryEncode("NVENC_Texture", () =>
                {
                    using var nv = new NvEncoderNative(device!);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var bs = nv.EncodeAv1FromTexture(texture, w, h, crf);
                    System.Diagnostics.Debug.WriteLine($"[AVIF] ✅ NVENC 纹理直通: {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                    return bs;
                });
                if (result.Success)
                {
                    IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag);
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 纹理路径失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，缓存失败+回退");
                s_texturePathFailed = true; // 缓存失败, 后续不再尝试纹理路径
            }

            // ═══ CPU 像素路径 (纹理不可用/纹理路径失败时使用) ═══
            var result2 = NativeEncoderGuard.TryEncode("NVENC", () =>
            {
                using var nv = new NvEncoderNative(device!);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = nv.EncodeAv1(bgra, w, h, crf);
                System.Diagnostics.Debug.WriteLine($"[AVIF] ✅ NVENC AV1: {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                return bs;
            });

            // ═══ 2026-08-31: 共享设备失败时改用 N 卡专用设备重试 ═══
            // 混合显卡系统（Intel 核显 + NVIDIA 独显）上，WGC 注入的共享设备可能位于核显，
            // 无法打开 NVENC 会话 → 此前直接回退软件编码，4K AVIF 需 20+ 秒。
            // CPU 像素路径不依赖纹理所属设备，故可改用一块真正的 N 卡设备重试。
            if (!result2.Success && !ownDevice)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AVIF] NVENC 共享设备失败 ({result2.Error?.Message})，尝试 N 卡专用设备...");
                using var nvDevice = NvEncoderNative.CreateNvidiaDevice();
                if (nvDevice is not null)
                {
                    var result3 = NativeEncoderGuard.TryEncode("NVENC_Nvidia", () =>
                    {
                        using var nv = new NvEncoderNative(nvDevice);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var bs = nv.EncodeAv1(bgra, w, h, crf);
                        System.Diagnostics.Debug.WriteLine(
                            $"[AVIF] ✅ NVENC AV1 (N 卡设备): {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB");
                        return bs;
                    });
                    if (result3.Success)
                    {
                        IvfWriter.WriteAvif(result3.Value!, w, h, path, colorSpaceTag);
                        return;
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[AVIF] NVENC N 卡设备亦失败: {result3.Error?.Message}");
                }
            }

            if (result2.Success)
            {
                IvfWriter.WriteAvif(result2.Value!, w, h, path, colorSpaceTag);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 失败 ({result2.Error?.GetType().Name}: {result2.Error?.Message})，回退 libaom");
            s_nvencFailed = true; // 缓存失败, 后续不再尝试 NVENC
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
            }
            finally
            {
                // 自己创建的设备用完释放 (共享设备由调用方管理)
                if (ownDevice && device is not null)
                {
                    lock (s_deviceLock) { if (s_cachedD3DDevice == null) s_cachedD3DDevice = device; else device.Dispose(); }
                }
            }
        }, ct);
    }

    /// <summary>从纹理直接编码 HEVC (预留, 未来 HEIF 支持)。</summary>
    public static byte[] EncodeHevcFromTexture(ID3D11Texture2D texture, int w, int h, int qp, ID3D11Device? device = null)
    {
        if (device is null)
        {
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            if (device is null)
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        using var nv = new NvEncoderNative(device);
        return nv.EncodeHevcFromTexture(texture, w, h, qp);
    }

    /// <summary>从像素缓冲编码 HEVC (预留, 未来 HEIF 支持)。</summary>
    public static byte[] EncodeHevc(byte[] bgra, int w, int h, int qp, ID3D11Device? device = null)
    {
        if (device is null)
        {
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            if (device is null)
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        using var nv = new NvEncoderNative(device);
        return nv.EncodeHevc(bgra, w, h, qp);
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
                IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag);
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
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, isHdr: false, chroma: chroma, bitDepth: displayBitDepth, iccProfile: icc, colorSpaceTag: colorSpaceTag);
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
    /// <summary>路径中禁止出现的字符（会破坏命令行引号配对）。</summary>
    private const string InvalidPathChars = "\"\n\r\0";

    /// <summary>校验传给原生命令行编码器的路径参数，防止命令行引号被破坏。
    /// <para>
    /// 原生编码器（avifenc / cwebp）通过 Process.Start 拼接命令行，路径若含
    /// 引号/换行会破坏参数引号配对，导致参数注入或编码到错误路径。
    /// Windows 文件名本身不允许这些字符，正常路径不会被误伤。
    /// </para>
    /// </summary>
    public static void ValidateNativePath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"路径参数为空: {paramName}");
        // 用 span 重载（IndexOfAny 的定参重载最多支持 3 个）
        if (path.AsSpan().IndexOfAny(InvalidPathChars) >= 0)
            throw new ArgumentException($"路径参数含非法字符（引号/换行/空字符）: {paramName}");
    }

    public static byte[] ToSdr(HdrFrameData f, EncodingSettings s) => Processing.ToneMapper.FloatToSRgbBytes(f.Pixels, f.Width, f.Height, s.ToneMappingParams, s.ColorSpaceTag);

    /// <summary>
    /// HDR→SDR 降级辅助 (2026-08-09): 将 HDR 帧色调映射为 SDR BGRA8 并改写 settings 为 SDR 路径。
    /// 统一 PNG/JXL/AVIF/TIFF/JPEG_LI/WebP 编码器中的重复降级模式。
    /// ToSdr 输出恒为 sRGB 色域，因此覆盖 ColorSpaceTag。</summary>
    public static byte[] ToSdrAndDegrade(HdrFrameData f, EncodingSettings s)
    {
        var d = ToSdr(f, s);
        s.ColorSpaceTag = "sRGB";
        s.HdrOutput = false;
        return d;
    }

    /// <summary>根据 EncodingSettings 计算 ICC 和 CICP 元数据。</summary>
    public static (byte[]? icc, byte[]? cicp) GetColorMetadata(EncodingSettings s)
    {
        bool isSRgb = s.ColorSpaceTag is null or "System" or "sRGB";
        byte[]? icc = (!isSRgb && s.IccProfile is { Length: > 400 }) ? s.IccProfile : null;

        // ═══ AdobeRGB 无标准 CICP primaries code (ITU-T H.273 Table 2) ═══
        // GetCicpPrimaries("AdobeRGB") 返回 1 (BT.709/sRGB), 但 cICP 优先于 iCCP,
        // 写错误的 cICP primaries=1 会覆盖 AdobeRGB iCCP → 色彩丢失。
        // 修复: AdobeRGB 不写 cICP, 仅靠 iCCP 声明 (否则 cICP 误标 sRGB)。
        if (s.ColorSpaceTag == "AdobeRGB")
            return (icc, null);

        // 始终写入 cICP chunk（PNG 3.0 RFC 9327 推荐两者都写）
        // cICP 优先于 iCCP，但两者都写可确保向现代和老旧解码器的兼容性
        byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(s.ColorSpaceTag ?? "sRGB");
        byte transfer = ColorManagement.ColorSpaceConverter.GetCicpTransfer(s.ColorSpaceTag ?? "sRGB", hdrOutput: false);
        byte[] cicp = [primaries, transfer, 0, 1];

        return (icc, cicp);
    }

    /// <summary>HDR scRGB → PQ 16-bit RGBA 数组。
    /// 根据目标位深直接量化到正确精度，然后左对齐到 16-bit 容器。
    /// 确保 16-bit 容器中真正存放的是 10/12-bit 数据（左对齐），而非完整 16-bit 数据被截断。</summary>
    /// <param name="f">HDR 帧数据（scRGB 线性浮点像素）。</param>
    /// <param name="colorSpaceTag">目标色域标签，null/sRGB 时不转换。</param>
    /// <param name="bitDepth">目标位深: 10/12/16。10-bit 量化到 0-1023 后左移 6 位。</param>
    public static ushort[] HdrToPq16(HdrFrameData f, string? colorSpaceTag = null, int bitDepth = 16)
    {
        int pixelCount = f.Width * f.Height;

        // ── 根据目标位深确定量化范围 ──
        // 10-bit: 0-1023, 左移到 16-bit 容器高位 (<<6)
        // 12-bit: 0-4095, 左移到 16-bit 容器高位 (<<4)
        // 16-bit: 0-65535, 全精度
        int maxValue = bitDepth switch
        {
            10 => 1023,
            12 => 4095,
            _ => 65535,
        };
        int shift = bitDepth switch
        {
            10 => 6,
            12 => 4,
            _ => 0,
        };

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

            // PQ 编码 → 直接量化到目标位深 → 左对齐到 16-bit 容器
            float pqR = LinearToPQ(r);
            float pqG = LinearToPQ(g);
            float pqB = LinearToPQ(b);
            a = Math.Clamp(a, 0f, 1f);

            // 量化到 maxValue 范围，然后左移到 16-bit 容器高位
            // 10-bit: 0-1023 → <<6 → 0-65520 (高位 10-bit 有效)
            // 12-bit: 0-4095 → <<4 → 0-65520 (高位 12-bit 有效)
            // 16-bit: 0-65535 → 直通
            int qiR = (int)Math.Round(pqR * maxValue);
            int qiG = (int)Math.Round(pqG * maxValue);
            int qiB = (int)Math.Round(pqB * maxValue);
            int qiA = (int)Math.Round(Math.Clamp(a, 0f, 1f) * maxValue);

            p16[i]     = (ushort)Math.Clamp(qiR << shift, 0, 65535);
            p16[i + 1] = (ushort)Math.Clamp(qiG << shift, 0, 65535);
            p16[i + 2] = (ushort)Math.Clamp(qiB << shift, 0, 65535);
            p16[i + 3] = (ushort)Math.Clamp(qiA << shift, 0, 65535);
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

    /// <summary>从 PQ16 数组计算内容实际峰值亮度 (nits)。
    /// ═══ 2026-08-10: 用于 JXL `--intensity_target` ═══
    /// 截图内容通常是 SDR 桌面 (峰值 200-800 nits), 恒传 10000 会让 tone-map
    /// 查看器把 SDR 内容显示得极暗。传实际峰值 → 查看器正确映射。
    /// clamp 到 [203, 10000] 保证合法 (libjxl 要求 ≥ 255, 保守用 203)。
    /// 含 Alpha 通道 (65535) 不计入峰值。</summary>
    public static float ComputePeakNits(ushort[] pq16)
    {
        int maxCode = 0;
        // 步进采样加速: 4K 图像 3300 万码值, 全扫约 10ms
        for (int i = 0; i < pq16.Length; i += 4)
        {
            int c = pq16[i];
            if (c > maxCode) maxCode = c;
            c = pq16[i + 1];
            if (c > maxCode) maxCode = c;
            c = pq16[i + 2];
            if (c > maxCode) maxCode = c;
        }
        if (maxCode <= 0) return 203f;
        // PQ EOTF: 码值 → nits
        double vv = maxCode / 65535.0;
        double vm = Math.Pow(vv, 1.0 / PQ_m2);
        double num = Math.Max(vm - PQ_c1, 0.0);
        double den = PQ_c2 - PQ_c3 * vm;
        float nits = (float)(Math.Pow(num / den, 1.0 / PQ_m1) * 10000.0);
        return Math.Clamp(nits, 203f, 10000f);
    }

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
