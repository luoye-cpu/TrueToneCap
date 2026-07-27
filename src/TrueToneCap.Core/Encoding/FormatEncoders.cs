// TrueToneCap.Core/Encoding/FormatEncoders.cs
using System.Threading.Tasks;
using ImageMagick;
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
    { if (s.HdrOutput) await H.SaveHdr(f, path, MagickFormat.Png, s, ct); else { var d = H.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); } }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default) => await H.SaveSdr(px, w, h, path, MagickFormat.Png, s, ct);
}

// ────── JPEG LI ──────
public sealed class JpegLiEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_LI;
    public override bool SupportsHdr => false;
    public override (float, float, float, string) GetQualityRange() => (0.5f, 3.0f, 1.0f, "butteraugli 距离 (0.5-3.0)");
    public override string GetQualityDescription(float q) => $"距离: {q:F1} (越小越清晰)";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { var d = H.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // jpegli 原生 butteraugli 距离编码（唯一 JPEG 路径）
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var jpegli = new JpegLiNative();
            var jpegBytes = jpegli.Encode(px, w, h, s.Quality);
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
    { if (s.HdrOutput) await H.SaveHdr(f, path, MagickFormat.Jxl, s, ct); else { var d = H.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); } }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default) => await H.SaveSdr(px, w, h, path, MagickFormat.Jxl, s, ct);
}

// ────── AVIF (动态后端) ──────
public sealed class AvifEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.AVIF;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (0f, 63f, 18f, "CRF (0=无损, 63=最低)");
    public override string GetQualityDescription(float q) => q <= 0 ? "无损" : $"CRF: {(int)q}";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { if (s.HdrOutput) await H.SaveHdr(f, path, MagickFormat.Avif, s, ct); else { var d = H.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); } }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    { var be = AvifEncoderSelector.Select(s.AvifBackend); await be.EncodeAsync(px, w, h, (int)s.Quality, path, ct, s.AvifChroma, s.DisplayBitDepth); }
}

// ────── WebP ──────
public sealed class WebPEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.WebP;
    public override bool SupportsHdr => false;
    public override (float, float, float, string) GetQualityRange() => (50f, 100f, 92f, "质量 (50-100)");
    public override string GetQualityDescription(float q) => q >= 100 ? "无损" : $"{(int)q}%";
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    { var d = H.ToSdr(f, s); await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct); }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default) => await H.SaveSdr(px, w, h, path, MagickFormat.WebP, s, ct);
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
    public bool IsAvailable => true;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var ps = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA);
            using var img = new MagickImage();
            img.ReadPixels(bgra, ps);
            img.Format = MagickFormat.Avif;
            img.Quality = (uint)Math.Clamp(100 - crf * 100 / 63, 0, 100);
            H.ApplyQualityDefinesPublic(img, MagickFormat.Avif, chroma, displayBitDepth);
            img.Write(path, MagickFormat.Avif);
        }, ct);
    }
}

public sealed class QsvAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Qsv;
    public bool IsAvailable => QsvEncoderNative.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var qsv = new QsvEncoderNative();
                var bs = qsv.EncodeAv1(bgra, w, h, crf);
                IvfWriter.WriteAvif(bs, w, h, path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF] QSV 失败 ({ex.Message})，回退 libaom");
                var fallback = new LibAomAvifBackend();
                fallback.EncodeAsync(bgra, w, h, crf, path, ct).GetAwaiter().GetResult();
            }
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

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
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

                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC DLL: {NvEncoderNative.IsDllPresent}");
                System.Diagnostics.Debug.WriteLine($"[AVIF] 驱动 SDK: 0x{NvEncoderNative.ProbeApiVersion():X8}");

                using var nv = new NvEncoderNative(device);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = nv.EncodeAv1(bgra, w, h, crf);
                System.Diagnostics.Debug.WriteLine($"[AVIF] ✅ NVENC AV1: {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length/1024}KB");
                IvfWriter.WriteAvif(bs, w, h, path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 失败 ({ex.GetType().Name}: {ex.Message})，回退 libaom");
                var fallback = new LibAomAvifBackend();
                fallback.EncodeAsync(bgra, w, h, crf, path, ct).GetAwaiter().GetResult();
            }
        }, ct);
    }
}

/// <summary>MFT AV1 编码后端 — 使用 Windows 系统内置硬件编码器 (NVIDIA/Intel/AMD 自适应)。</summary>
public sealed class MftAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Auto;
    public bool IsAvailable => MftEncoderNative.IsAv1MftAvailable;

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF MFT] 编码 {w}x{h} CRF={crf}...");
                using var mft = new MftEncoderNative(w, h, useAv1: true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = mft.Encode(bgra);
                System.Diagnostics.Debug.WriteLine($"[AVIF MFT] ✅ {sw.ElapsedMilliseconds}ms {bs.Length/1024}KB");
                IvfWriter.WriteAvif(bs, w, h, path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AVIF MFT] 失败 ({ex.GetType().Name}: {ex.Message})，回退 libaom");
                var fallback = new LibAomAvifBackend();
                fallback.EncodeAsync(bgra, w, h, crf, path, ct).GetAwaiter().GetResult();
            }
        }, ct);
    }
}

// ────── 辅助 ──────
internal static class H
{
    public static byte[] ToSdr(HdrFrameData f, EncodingSettings s) => Processing.ToneMapper.FloatToSRgbBytes(f.Pixels, f.Width, f.Height, s.ToneMappingParams);

    public static async Task SaveSdr(byte[] bgra, int w, int h, string path, MagickFormat fmt, EncodingSettings s, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var ps = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA);
            using var img = new MagickImage();
            img.ReadPixels(bgra, ps);
            img.Format = fmt;

            // ── 质量映射：区分 butteraugli 距离 (0.5-4.0) 和百分比 (0-100) ──
            uint quality;
            if (fmt == MagickFormat.Jpeg && s.Quality <= 5.0f)
            {
                // JPEG LI / GainMap: butteraugli 距离 → JPEG 质量百分比
                //   距离 0.5 → 质量 100 (近无损)
                //   距离 1.0 → 质量 92
                //   距离 3.0 → 质量 30
                quality = (uint)Math.Clamp((int)(100f - (s.Quality - 0.5f) * 28f), 10, 100);
            }
            else if (fmt == MagickFormat.Jxl && s.Quality <= 5.0f)
            {
                // JPEG XL: butteraugli 距离 → 保留浮点（JXL 原生支持）
                quality = (uint)Math.Clamp((int)(s.Quality * 30f), 1, 100);
            }
            else
            {
                quality = (uint)Math.Clamp((int)s.Quality, 0, 100);
            }
            img.Quality = quality;

            // ── 格式专属画质增强 + 位深选项 ──
            ApplyQualityDefines(img, fmt, s.AvifChroma, s.DisplayBitDepth);

            // ── ICC 嵌入：仅嵌入有效标准 ICC（>2KB），sRGB 目标不嵌入 ──
            if (s.IccProfile is { Length: > 2000 })
            {
                try { img.SetProfile(new ColorProfile(s.IccProfile)); } catch { }
            }
            img.Write(path, fmt);
        }, ct);
    }

    /// <summary>为各格式设置画质增强 + 位深保护。
    /// 截图场景特点：大量文字锐利边缘、平面UI色块、合成内容（非自然照片）。
    /// 优化目标：文字清晰度 > 色度保真 > 渐变平滑 > 文件大小。</summary>
    private static void ApplyQualityDefines(MagickImage img, MagickFormat fmt, string avifChroma = "444", int displayBitDepth = 8)
    {
        bool highDepth = displayBitDepth >= 10;

        if (fmt == MagickFormat.WebP)
        {
            // ── WebP 截图优化：锐利YUV + 高质量alpha ──
            img.Settings.SetDefine(MagickFormat.WebP, "method", "6");            // 最慢=最优压缩
            img.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");   // 无损alpha
            img.Settings.SetDefine(MagickFormat.WebP, "lossless", "false");
            img.Settings.SetDefine(MagickFormat.WebP, "pass", "1");             // 单趟编码
            img.Settings.SetDefine(MagickFormat.WebP, "filter-strength", "0");   // 不过滤（保留锐度）
            img.Settings.SetDefine(MagickFormat.WebP, "sharpness", "7");         // 最高锐度(0-7)
            img.Settings.SetDefine(MagickFormat.WebP, "use-sharp-yuv", "1");     // 锐利YUV转换
            img.Settings.SetDefine(MagickFormat.WebP, "preprocessing", "0");     // 无预处理（保留细节）
            img.Settings.SetDefine(MagickFormat.WebP, "exact", "true");          // 精确像素
        }
        else if (fmt == MagickFormat.Avif)
        {
            // ── AVIF 截图优化：4:4:4色度 + SSIM调优 ──
            img.Settings.SetDefine(MagickFormat.Avif, "speed", "0");             // 最慢=最优
            // 截图必须 4:4:4！4:2:0 会让彩色文字严重渗色
            string chroma = avifChroma switch { "422" => "422", "420" => "420", _ => "444" };
            img.Settings.SetDefine(MagickFormat.Avif, "chroma", chroma);
            img.Settings.SetDefine(MagickFormat.Avif, "tiles", "0");             // 单tile（截图不大）
            img.Settings.SetDefine(MagickFormat.Avif, "enable-chroma-deltaq", "1"); // 色度增量量化
            img.Settings.SetDefine(MagickFormat.Avif, "enable-qm", "1");         // 量化矩阵（平面区域更优）
            // tune: PSNR=0, SSIM=1, Butteraugli=2 — SSIM对文字边缘更好
            img.Settings.SetDefine(MagickFormat.Avif, "tune", "1");
            if (highDepth) img.Depth = 10;
        }
        else if (fmt == MagickFormat.Jxl)
        {
            // ── JPEG XL 截图优化：Modular模式(无损平面区) + VarDCT(纹理区) ──
            img.Settings.SetDefine(MagickFormat.Jxl, "effort", "9");             // 最高压缩努力
            img.Settings.SetDefine(MagickFormat.Jxl, "decoding_speed", "0");     // 最优解码速度
            img.Settings.SetDefine(MagickFormat.Jxl, "modular", "1");            // Modular模式(截图神器)
            // 允许VarDCT处理自然图像部分，Modular处理平面UI
            img.Settings.SetDefine(MagickFormat.Jxl, "lossless", "false");
            // 保留极高高频（文字边缘），距离值由外部 Quality 控制
            if (highDepth) img.Depth = 16;
        }
        else if (fmt == MagickFormat.Jpeg)
        {
            // ── JPEG 截图优化：4:4:4 + 浮点DCT + 优化哈夫曼 ──
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4"); // 无色度子采样
            img.Settings.SetDefine(MagickFormat.Jpeg, "dct", "float");            // 浮点DCT更精确
            img.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");  // 优化哈夫曼表
            // 使用平直量化表减少低频条带（flat quantization for UI gradients）
            img.Settings.SetDefine(MagickFormat.Jpeg, "quantum-table", "4");
        }
        else if (fmt == MagickFormat.Png)
        {
            // ── PNG 截图优化：最大压缩 + 过滤策略 ──
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");         // 最大压缩
            img.Settings.SetDefine(MagickFormat.Png, "compression-filter", "1");        // 自适应过滤
            img.Settings.SetDefine(MagickFormat.Png, "compression-strategy", "1");      // Z_RLE(对平面色块极优)
            img.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");    // 排除非必要chunk
            if (highDepth) img.Depth = 16;
        }
        // BMP: 无压缩，无需参数
    }

    public static void ApplyQualityDefinesPublic(MagickImage img, MagickFormat fmt, string avifChroma = "444", int displayBitDepth = 8)
        => ApplyQualityDefines(img, fmt, avifChroma, displayBitDepth);

    // ═══════════════════════════
    // HDR 编码: scRGB → PQ (ST.2084), 10-bit, cICP Rec.2100 PQ
    // CICP (Coding-Independent Code Points) per ITU-T H.273 / ISO 23091-2
    //   Primaries=9 (BT.2020), Transfer=16 (ST.2084 PQ), Matrix=0 (RGB), Range=1 (Full)
    // ═══════════════════════════

    // ST.2084 PQ 常数 (SMPTE ST.2084)
    private const float PQ_m1 = 2610f / 16384f;
    private const float PQ_m2 = 2523f / 32f;
    private const float PQ_c1 = 3424f / 4096f;
    private const float PQ_c2 = 2413f / 128f;
    private const float PQ_c3 = 2392f / 128f;

    /// <summary>scRGB 线性光 → PQ (ST.2084) 感知量化编码。
    /// 输入: scRGB linear light (1.0 ≈ 80 nits SDR white, >1.0 = HDR highlights)
    /// 输出: 0..1 范围的 PQ 编码值</summary>
    internal static float LinearToPQ(float scRgbLinear)
    {
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, PQ_m1);
        float numerator = PQ_c1 + PQ_c2 * Lp;
        float denominator = 1f + PQ_c3 * Lp;
        return MathF.Pow(numerator / denominator, PQ_m2);
    }

    /// <summary>生成 Rec.2020 PQ ICC 骨架（仅作元数据占位，非完整色彩转换）。
    /// HDR 图像应以 CICP 为主要信号。ICC 仅用于不支持 CICP 的旧查看器。</summary>
    internal static byte[] BuildHdrIccProfile()
    {
        // 不生成不完整的 ICC — 错误的 ICC 比没有 ICC 更危险
        // HDR 信号通过 CICP (ITU-T H.273) 传递：
        //   PNG: cICP chunk, JXL: cicp-* defines, AVIF: heif:cicp-*
        System.Diagnostics.Debug.WriteLine("[ICC] HDR 模式：使用 CICP 信号，不嵌入 ICC");
        return []; // 空 = 不嵌入
    }

    /// <summary>写入 HDR 图像。scRGB float → 10-bit PQ → 16-bit 容器 + cICP/ICC。
    /// 策略: 优先 CICP (PNG/JXL/AVIF), 不支持的回退 ICC Profile。</summary>
    public static async Task SaveHdr(HdrFrameData f, string path, MagickFormat fmt, EncodingSettings s, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 1. scRGB linear → PQ 10-bit → 16-bit 容器
            int pixelCount = f.Width * f.Height;
            var p16 = new ushort[pixelCount * 4];
            Parallel.For(0, pixelCount, pi =>
            {
                int i = pi * 4;
                float r = LinearToPQ(f.Pixels[i]);
                float g = LinearToPQ(f.Pixels[i + 1]);
                float b = LinearToPQ(f.Pixels[i + 2]);
                float a = Math.Clamp(f.Pixels[i + 3], 0f, 1f);
                p16[i]     = (ushort)((int)Math.Round(r * 1023f) * 64);
                p16[i + 1] = (ushort)((int)Math.Round(g * 1023f) * 64);
                p16[i + 2] = (ushort)((int)Math.Round(b * 1023f) * 64);
                p16[i + 3] = (ushort)((int)Math.Round(a * 1023f) * 64);
            });

            var bytes = new byte[p16.Length * 2];
            Buffer.BlockCopy(p16, 0, bytes, 0, bytes.Length);

            var ps = new PixelReadSettings((uint)f.Width, (uint)f.Height, StorageType.Short, PixelMapping.RGBA);
            using var img = new MagickImage();
            img.ReadPixels(bytes, ps);
            img.Format = fmt;
            img.Quality = (uint)Math.Clamp((int)s.Quality, 0, 100);

            // 2. CICP HDR 元数据 (ITU-T H.273): Primaries=9, Transfer=16(ST.2084), Matrix=0, Range=1
            byte[] cIcp = [9, 16, 0, 1];
            string cIcpHex = Convert.ToHexStringLower(cIcp);

            switch (fmt)
            {
                case MagickFormat.Png:
                    img.Settings.SetDefine(MagickFormat.Png, "chunk-cICP", cIcpHex);
                    img.Settings.SetDefine(MagickFormat.Png, "bit-depth", "16");
                    img.Settings.SetDefine(MagickFormat.Png, "color-type", "6");
                    break;
                case MagickFormat.Jxl:
                    img.Settings.SetDefine(MagickFormat.Jxl, "cicp-primaries", "9");
                    img.Settings.SetDefine(MagickFormat.Jxl, "cicp-tf", "16");
                    break;
                case MagickFormat.Avif:
                    img.Settings.SetDefine(MagickFormat.Avif, "heif:cicp-primaries", "9");
                    img.Settings.SetDefine(MagickFormat.Avif, "heif:cicp-tf", "16");
                    img.Settings.SetDefine(MagickFormat.Avif, "heif:cicp-matrix", "0");
                    img.Settings.SetDefine(MagickFormat.Avif, "heif:cicp-range", "1");
                    break;
            }

            // 3. ICC Profile: 仅嵌入有效标准 ICC（sRGB 目标已在上游过滤）
            if (f.IccProfile is { Length: > 2000 })
            {
                try { img.SetProfile(new ColorProfile(f.IccProfile)); } catch { }
            }

            img.Write(path, fmt);
        }, ct);
    }
}

// ────── 工厂 ──────
public static class EncoderFactory
{
    public static ImageEncoder Create(OutputFormat f) => f switch
    { OutputFormat.PNG => new PngEncoder(), OutputFormat.JPEG_LI => new JpegLiEncoder(), OutputFormat.JPEG_XL => new JpegXlEncoder(), OutputFormat.AVIF => new AvifEncoder(), OutputFormat.WebP => new WebPEncoder(), OutputFormat.JPEG_GAINMAP => new JpegGainMapEncoder(), _ => new PngEncoder() };
    public static OutputFormat Parse(string n) => n?.ToUpperInvariant() switch
    { "PNG" => OutputFormat.PNG, "JPEG LI" or "JPEGLI" => OutputFormat.JPEG_LI, "JPEG XL" or "JXL" => OutputFormat.JPEG_XL, "AVIF" => OutputFormat.AVIF, "WEBP" => OutputFormat.WebP, "BMP" => OutputFormat.BMP, "JPEG GAINMAP" or "JPEGGAINMAP" or "GAINMAP" or "ULTRAHDR" => OutputFormat.JPEG_GAINMAP, _ => OutputFormat.PNG };
}
