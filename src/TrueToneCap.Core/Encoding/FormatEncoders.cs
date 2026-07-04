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
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default) => await H.SaveSdr(px, w, h, path, MagickFormat.Jpeg, s, ct);
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
    { [AvifEncoderBackend.LibAom] = new LibAomAvifBackend(), [AvifEncoderBackend.Qsv] = new QsvAvifBackend(), [AvifEncoderBackend.Nvenc] = new NvencAvifBackend() };

    public static IAvifEncoder Select(AvifEncoderBackend pref)
    {
        if (pref == AvifEncoderBackend.Auto)
        {
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
    public bool IsAvailable => NvEncoderNative.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var nv = new NvEncoderNative(D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport));
                var bs = nv.EncodeAv1(bgra, w, h, crf);
                IvfWriter.WriteAvif(bs, w, h, path);
            }
            catch (Exception ex)
            {
                // NVENC 失败时自动回退到 libaom
                System.Diagnostics.Debug.WriteLine($"[AVIF] NVENC 失败 ({ex.Message})，回退 libaom");
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
            img.Quality = (uint)Math.Clamp((int)s.Quality, 0, 100);

            // ── 格式专属画质增强 + 位深选项 ──
            ApplyQualityDefines(img, fmt, s.AvifChroma, s.DisplayBitDepth);

            if (s.IccProfile is { Length: > 0 })
            {
                try { img.SetProfile(new ColorProfile(s.IccProfile)); } catch { }
            }
            img.Write(path, fmt);
        }, ct);
    }

    /// <summary>为各格式设置画质增强 + 位深保护（避免色彩降级）。</summary>
    private static void ApplyQualityDefines(MagickImage img, MagickFormat fmt, string avifChroma = "420", int displayBitDepth = 8)
    {
        // ── 高色深显示器：提升输出位深防止色彩降级 ──
        // SDR 源为 8-bit，但 10/12-bit 显示器 + 有损编码会导致条带效应
        // 将输出位深提升至显示器匹配级别可消除量化误差
        bool highDepth = displayBitDepth >= 10;

        if (fmt == MagickFormat.WebP)
        {
            // WebP 仅支持 8-bit，无法提升位深
            img.Settings.SetDefine(MagickFormat.WebP, "method", "6");
            img.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");
            img.Settings.SetDefine(MagickFormat.WebP, "lossless", "false");
            img.Settings.SetDefine(MagickFormat.WebP, "exact", "true");
            img.Settings.SetDefine(MagickFormat.WebP, "pass", "1");
            img.Settings.SetDefine(MagickFormat.WebP, "filter-strength", "0");
            img.Settings.SetDefine(MagickFormat.WebP, "sharpness", "0");
        }
        else if (fmt == MagickFormat.Avif)
        {
            img.Settings.SetDefine(MagickFormat.Avif, "speed", "0");
            string chroma = avifChroma switch { "422" => "422", "444" => "444", _ => "420" };
            img.Settings.SetDefine(MagickFormat.Avif, "chroma", chroma);
            img.Settings.SetDefine(MagickFormat.Avif, "tiles", "0");
            img.Settings.SetDefine(MagickFormat.Avif, "enable-chroma-deltaq", "1");
            // 10/12-bit 显示器 → 10-bit AVIF（libaom/QSV/NVENC 均支持 10-bit）
            if (highDepth) img.Depth = 10;
        }
        else if (fmt == MagickFormat.Jxl)
        {
            img.Settings.SetDefine(MagickFormat.Jxl, "effort", "7");
            img.Settings.SetDefine(MagickFormat.Jxl, "decoding_speed", "0");
            img.Settings.SetDefine(MagickFormat.Jxl, "modular", "1");
            // 10/12-bit 显示器 → 16-bit JPEG XL（原生支持高精度）
            if (highDepth) img.Depth = 16;
        }
        else if (fmt == MagickFormat.Jpeg)
        {
            // JPEG 仅 8-bit
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4");
            img.Settings.SetDefine(MagickFormat.Jpeg, "dct", "float");
            img.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
        }
        else if (fmt == MagickFormat.Png)
        {
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            img.Settings.SetDefine(MagickFormat.Png, "compression-filter", "1");
            img.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
            // 10/12-bit 显示器 → 16-bit PNG
            if (highDepth) img.Depth = 16;
        }
        // BMP: 8-bit only, no defines needed
    }

    public static void ApplyQualityDefinesPublic(MagickImage img, MagickFormat fmt, string avifChroma = "420", int displayBitDepth = 8)
        => ApplyQualityDefines(img, fmt, avifChroma, displayBitDepth);

    // ═══════════════════════════
    // HDR PNG: scRGB → PQ (ST.2084), 10-bit, cICP Rec.2100 PQ
    // ═══════════════════════════

    // ST.2084 PQ 常数
    private const float PQ_m1 = 0.1593017578125f;   // 2610/16384
    private const float PQ_m2 = 78.84375f;           // 2523/4096 * 128
    private const float PQ_c1 = 0.8359375f;           // 3424/4096
    private const float PQ_c2 = 18.8515625f;          // 2413/4096 * 32
    private const float PQ_c3 = 18.6875f;             // 2392/4096 * 32

    /// <summary>scRGB 线性光 → PQ (ST.2084) 感知编码。
    /// scRGB 1.0 = 80 nits, PQ 输入归一化到 0-10000 nits 范围。</summary>
    private static float LinearToPQ(float scRgbLinear)
    {
        // scRGB → nits: 1.0 = 80 nits
        float nits = scRgbLinear * 80f;
        // 归一化到 0-10000 nits
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        // ST.2084 PQ
        float Lp = MathF.Pow(L, PQ_m1);
        float numerator = PQ_c1 + PQ_c2 * Lp;
        float denominator = 1f + PQ_c3 * Lp;
        return MathF.Pow(numerator / denominator, PQ_m2);
    }

    /// <summary>写入 HDR 图像（PNG/JPEG XL/AVIF）。scRGB float → 10-bit PQ → 16-bit PNG + cICP。</summary>
    public static async Task SaveHdr(HdrFrameData f, string path, MagickFormat fmt, EncodingSettings s, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 1. scRGB linear → PQ + 10-bit 量化 (Parallel.For)
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

            // 2. 创建 MagickImage (16-bit RGBA)
            var ps = new PixelReadSettings((uint)f.Width, (uint)f.Height, StorageType.Short, PixelMapping.RGBA);
            using var img = new MagickImage();
            img.ReadPixels(bytes, ps);
            img.Format = fmt;

            // 3. PNG 特定：嵌入 cICP 元数据 (Rec.2100 PQ)
            if (fmt == MagickFormat.Png)
            {
                // cICP 数据: Primaries=9(BT.2020), Transfer=16(ST.2084), Matrix=0(RGB), Range=1(Full)
                byte[] cIcp = [9, 16, 0, 1];
                string cIcpHex = Convert.ToHexStringLower(cIcp);
                img.Settings.SetDefine(MagickFormat.Png, "chunk-cICP", cIcpHex);
                // 确保 16-bit RGB 输出
                img.Settings.SetDefine(MagickFormat.Png, "bit-depth", "16");
                img.Settings.SetDefine(MagickFormat.Png, "color-type", "6"); // RGBA
            }

            img.Quality = (uint)Math.Clamp((int)s.Quality, 0, 100);

            // 4. ICC profile (若提供)
            if (f.IccProfile is { Length: > 0 })
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
