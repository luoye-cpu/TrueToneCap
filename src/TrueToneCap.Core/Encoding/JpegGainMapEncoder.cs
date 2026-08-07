// TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs
// JPEG Gain Map (Ultra HDR) — ISO 21496-1 + Ultra HDR v1 双元数据兼容实现
//
// 全新管线 (2026-08-06): 以 JPEG LI 为基底 + BT.2390-4 EETF 标准映射
// 2026-08-07 修复: 标准 hdrgm XMP + ISO 21496-1 二进制元数据 + 标准 MPF attribute
//
//   HDR scRGB 线性浮点 (1.0 = 80 nits)
//     ├─ BT.2390-4 EETF 显示映射 → SDR 线性（同一 scRGB 空间）
//     │     └─ sRGB gamma → BGRA8 → jpegli → Base JPEG
//     ├─ 增益比 = HDR_linear / SDR_linear（基于【同一个】EETF 映射空间）
//     │     └─ log2 编码 [0,+4] → 1/4 降采样 → jpegli → 增益图 JPEG
//     └─ 封装: Base(APP1: hdrgm XMP + APP2: MPF) + GainMap(APP2: ISO 21496-1)
//
// 核心设计原则:
//   * Base 与增益图基于同一个 BT.2390 EETF 映射，保证一致性（无偏色/过曝）
//   * 用 BT.2390-4 EETF 标准显示映射替代通用色调映射器（Reinhard/Hable/ACES）
//   * 全部使用 jpegli (JPEG LI) 编码，移除 ultrahdr_app 外部依赖
//   * 双元数据: hdrgm XMP (Ultra HDR v1 解码器) + ISO 21496-1 二进制 (ISO 解码器)
//
// 增益图类型：
//   Gray: log2(HDR_luminance / SDR_luminance) → 单通道灰度图
//   Rgb:  log2(HDR_channel / SDR_channel)     → 三通道彩色增益图

using System.IO;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.Core.Encoding;

/// <summary>增益图类型。</summary>
public enum GainMapMode { Gray, Rgb }

/// <summary>JPEG Gain Map (Ultra HDR) 编码器。
/// 仅支持从 HDR 源数据编码（需要原始浮点像素计算增益比）。
/// SDR 输入将直接回退为 JPEG LI 编码。</summary>
public sealed class JpegGainMapEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_GAINMAP;
    public override bool SupportsHdr => true;
    public override (float, float, float, string) GetQualityRange() => (0.5f, 3.0f, 1.0f, "butteraugli 距离 (0.5-3.0)");
    public override string GetQualityDescription(float q) => $"距离: {q:F1}";

    // ── 编码入口 ──

    public override async Task EncodeAsync(HdrFrameData frame, EncodingSettings settings,
        string outputPath, CancellationToken ct = default)
    {
        if (!settings.HdrOutput)
        {
            // SDR 模式：回退为普通 JPEG LI（通过统一编码器，带 ICC 和色域支持）
            // 先做色域转换 + 色调映射
            System.Diagnostics.Debug.WriteLine("[GainMap] ⚠ HDR 未开启，Gain Map 降级为普通 JPEG（需要 HDR 数据才能生成增益图）");
            var sdr = FormatHelper.ToSdr(frame, settings);
            await EncodeSdrAsync(sdr, frame.Width, frame.Height, settings, outputPath, ct);
            return;
        }
        await EncodeGainMapAsync(frame, settings, outputPath, ct);
    }

    public override async Task EncodeSdrAsync(byte[] sdrPixels, int width, int height,
        EncodingSettings settings, string outputPath, CancellationToken ct = default)
    {
        // GainMap SDR 回退: 通过 jpegli 输出标准 JPEG（无回退，必须可用）
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!JpegLiNative.IsAvailable)
                throw new InvalidOperationException("JPEG Gain Map 编码需要 cjpegli.exe (Google jpegli)，请将 cjpegli.exe 放入 native/ 目录、PLAN/tools/ 目录或系统 PATH。");
            var icc = (settings.ColorSpaceTag is not (null or "System" or "sRGB")) ? settings.IccProfile : null;
            // settings.Quality 是 butteraugli 距离 (GetQualityRange: 0.5-3.0)，直接使用
            float distance = Math.Clamp(settings.Quality, 0.5f, 25.0f);
            var jpegBytes = JpegLiNative.Encode(sdrPixels, width, height, distance, settings.ChromaSubsampling, icc, forceBaseline: true);
            File.WriteAllBytes(outputPath, jpegBytes);
        }, ct);
    }

    // ═══════════════════════════════════════
    //  Gain Map 编码主流程（BT.2390-4 EETF）
    // ═══════════════════════════════════════

    private async Task EncodeGainMapAsync(HdrFrameData frame, EncodingSettings settings,
        string outputPath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int w = frame.Width, h = frame.Height;
            int pixelCount = w * h;
            float[] hdrPixels = frame.Pixels;

            // ── 0. HDR 峰值亮度 + SDR 白点（headroom 计算输入）──
            //     ═══ Windows scRGB 语义 (Microsoft 文档权威确认) ═══
            //     WGC Float16 捕获: scRGB 线性, 1.0 = 80 nits (标称参考白, scene-referred)
            //     但 SDR 内容在 HDR 桌面被 DWM 提升到 SdrWhiteLevel (通常 200 nits = 2.5 scRGB)
            //     所以: SDR 内容捕获值 = 2.5 scRGB (200 nits), 不是 1.0!
            //     直通阈值必须是 SDR 白点 (PaperWhiteNits/80 对应的 scRGB 值),
            //     否则 2.5 scRGB 的 SDR 内容会被错误压缩 → Base 发灰 + 增益虚高 → 过曝。
            float hdrPeakNits = settings.ToneMappingParams.DisplayMaxNits > 0
                ? settings.ToneMappingParams.DisplayMaxNits
                : 1000f;
            float sdrWhiteNits = Math.Max(settings.ToneMappingParams.PaperWhiteNits, 80f);
            // scRGB 中 SDR 白点的位置: PaperWhiteNits / 80 (如 200/80 = 2.5)
            float sdrWhiteScrgb = sdrWhiteNits / 80f;
            // headroom: HDR 峰值相对 SDR 白点 (如 1000/200 = 5.0)
            float headroom = hdrPeakNits / sdrWhiteNits;

            // ── 1. 分段 Reinhard 色调映射: HDR scRGB → SDR 线性 ──
            //     输入: scRGB (1.0 = 80 nits), 先归一化到 SDR 白点相对空间
            //       y = scRGB / sdrWhiteScrgb  (SDR 白点 = 1.0)
            //     输出: [0, 1.0] 相对 SDR 白点 (解码器理解的 Base 亮度)
            //     分段: y ≤ 1.0 (SDR 内容) 完全直通 → 增益 1x
            //           y > 1.0 (真 HDR 高光) Reinhard 压缩 → 增益 >1x
            float[] sdrLinear = ReinhardToSdr(hdrPixels, w, h, headroom, sdrWhiteScrgb);
            ct.ThrowIfCancellationRequested();

            // ── 2. Base 像素: sdrLinear (0..1.0 相对 SDR 白点) → sRGB gamma → BGRA8 ──
            byte[] sdrBgra = LinearToBgra8(sdrLinear, w, h);
            ct.ThrowIfCancellationRequested();

            // ── 3. 编码 Base JPEG (jpegli, Baseline) ──
            float baseDist = Math.Clamp(settings.Quality, 0.5f, 25.0f);
            byte[] baseJpegBytes = EncodeToJpegBytesSafe(sdrBgra, w, h, baseDist, null);
            ct.ThrowIfCancellationRequested();

            // ── 4. 增益比计算（统一到 SDR 白点相对线性空间）──
            var gainMapMode = settings.GainMapMode;
            // HDR scRGB (1.0=80 nits) → SDR 白点相对空间 (与 sdrLinear 一致)
            float invSdrWhite = 1.0f / Math.Max(sdrWhiteScrgb, 1.0f);
            float[] normHdr = new float[hdrPixels.Length];
            for (int pi = 0; pi < pixelCount; pi++)
            {
                int o = pi * 4;
                normHdr[o]     = hdrPixels[o] * invSdrWhite;
                normHdr[o + 1] = hdrPixels[o + 1] * invSdrWhite;
                normHdr[o + 2] = hdrPixels[o + 2] * invSdrWhite;
                normHdr[o + 3] = hdrPixels[o + 3];
            }
            // 最大 log2 增益 = log2(headroom)（HDR 峰值相对 SDR 白点）
            float maxLog2Gain = MathF.Log2(Math.Max(headroom, 1.0f));
            byte[] gainMapPixels = ComputeGainMap(normHdr, sdrLinear, w, h, gainMapMode, maxLog2Gain);
            ct.ThrowIfCancellationRequested();

            // ── 5. 增益图降采样 + 编码 (jpegli) ──
            //    用户已选 butteraugli 距离 → 映射回 0-100 质量：
            //    distance 0.5→100, 3.0→50, clamp 到 [50,100] 保底
            float gmDistance = Math.Clamp(settings.Quality, 0.5f, 3.0f);
            int gainMapJpegQuality = (int)Math.Round(100f - (gmDistance - 0.5f) / 2.5f * 50f);
            gainMapJpegQuality = Math.Clamp(gainMapJpegQuality, 50, 100);
            byte[] gainMapScaled = RescaleGainMap(gainMapPixels, w, h, gainMapMode, out int gmSW, out int gmSH);
            byte[] gainMapJpegBytes = EncodeGainMapToJpegBytesSafe(gainMapScaled, gmSW, gmSH,
                gainMapMode, gainMapJpegQuality);
            ct.ThrowIfCancellationRequested();

            // ── 6. MPF + XMP 封装（增益范围基于实际 headroom）──
            WriteJpegGainMapFile(baseJpegBytes, gainMapJpegBytes, w, h, gmSW, gmSH,
                gainMapMode, headroom, outputPath);

            System.Diagnostics.Debug.WriteLine(
                $"[GainMap] 输出: {w}x{h}, 增益图: {gmSW}x{gmSH} ({gainMapMode}), " +
                $"Base={baseJpegBytes.Length / 1024}KB, GainMap={gainMapJpegBytes.Length / 1024}KB, " +
                $"headroom={headroom:F1}");
        }, ct);
    }

    /// <summary>
    /// 分段 Reinhard 色调映射: HDR scRGB → SDR 线性 (0..1.0, 1.0=SDR 白点)。
    /// 输入: scRGB 线性 (1.0 = 80 nits, Windows scene-referred)
    /// 归一化: y = scRGB / sdrWhiteScrgb (SDR 白点 = 1.0)
    /// 分段 (修复过曝的关键):
    ///   - y ≤ 1.0 (SDR 内容, ≤ PaperWhiteNits): 完全直通 → 增益恒 1x
    ///   - y > 1.0 (真 HDR 高光): Reinhard 压缩 (libultrahdr 公式)
    /// 输出: [0, 1.0] 相对 SDR 白点 (解码器理解的 Base 亮度)
    /// </summary>
    private static float[] ReinhardToSdr(float[] hdrPixels, int w, int h, float headroom,
        float sdrWhiteScrgb)
    {
        int pixelCount = w * h;
        var sdr = new float[hdrPixels.Length];
        float headroomSq = headroom * headroom;
        float invSdrWhite = 1.0f / Math.Max(sdrWhiteScrgb, 1.0f);

        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;

            // HDR scRGB 线性 (1.0 = 80 nits) → 归一化到 SDR 白点相对空间
            float r = hdrPixels[i] * invSdrWhite;
            float g = hdrPixels[i + 1] * invSdrWhite;
            float b = hdrPixels[i + 2] * invSdrWhite;
            float a = hdrPixels[i + 3];

            float maxY = Math.Max(Math.Max(r, g), b);

            if (maxY <= 1.0f)
            {
                // SDR 范围 (≤ SDR 白点): 完全直通, 无压缩, 增益 1x
                sdr[i]     = Math.Clamp(r, 0f, 1f);
                sdr[i + 1] = Math.Clamp(g, 0f, 1f);
                sdr[i + 2] = Math.Clamp(b, 0f, 1f);
                sdr[i + 3] = Math.Clamp(a, 0f, 1f);
                return;
            }

            // HDR 高光 (> SDR 白点): Reinhard 压缩, 保持色相
            // ReinhardMap(y, headroom) = (1 + y/headroom²) / (1 + y) × y
            float maxSdr = (1.0f + maxY / headroomSq) / (1.0f + maxY) * maxY;

            // 保持色相缩放 (高光压缩到 [Reinhard(1.0), 1.0] 范围)
            float scale = maxSdr / maxY;
            sdr[i]     = Math.Clamp(r * scale, 0f, 1f);
            sdr[i + 1] = Math.Clamp(g * scale, 0f, 1f);
            sdr[i + 2] = Math.Clamp(b * scale, 0f, 1f);
            sdr[i + 3] = Math.Clamp(a, 0f, 1f);
        });
        return sdr;
    }

    /// <summary>BT.2390 EETF 映射后的 SDR 线性 → sRGB gamma → BGRA8。</summary>
    private static byte[] LinearToBgra8(float[] linear, int w, int h)
    {
        int pixelCount = w * h;
        var bgra = new byte[pixelCount * 4];
        System.Threading.Tasks.Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = ToneMapper.LinearToSRgbScalarPub(Math.Clamp(linear[i], 0f, 1f));
            float g = ToneMapper.LinearToSRgbScalarPub(Math.Clamp(linear[i + 1], 0f, 1f));
            float b = ToneMapper.LinearToSRgbScalarPub(Math.Clamp(linear[i + 2], 0f, 1f));
            bgra[i]     = (byte)(b * 255f + 0.5f);
            bgra[i + 1] = (byte)(g * 255f + 0.5f);
            bgra[i + 2] = (byte)(r * 255f + 0.5f);
            bgra[i + 3] = 255;
        });
        return bgra;
    }

    // ═══════════════════════════════════════
    //  JPEG 编码（崩溃隔离 + 回退）
    // ═══════════════════════════════════════

    private static byte[] EncodeToJpegBytesSafe(byte[] bgra, int w, int h, float distance, byte[]? icc)
    {
        var result = NativeEncoderGuard.TryEncode("GainMap_BaseJPEG", () =>
        {
            return JpegLiNative.Encode(bgra, w, h, distance, "444", icc, forceBaseline: true);
        });
        if (result.Success) return result.Value!;
        throw new InvalidOperationException($"[GainMap] Base JPEG 编码失败: {result.Error?.Message}");
    }

    private static byte[] EncodeGainMapToJpegBytesSafe(byte[] pixels, int w, int h,
        GainMapMode mode, int quality)
    {
        // 增益图像素 → BGRA (jpegli 输入格式)
        byte[] bgra = new byte[w * h * 4];
        if (mode == GainMapMode.Gray)
        {
            for (int i = 0; i < w * h; i++)
            {
                byte v = pixels[i];
                int off = i * 4;
                bgra[off] = v; bgra[off + 1] = v; bgra[off + 2] = v; bgra[off + 3] = 255;
            }
        }
        else
        {
            for (int i = 0; i < w * h; i++)
            {
                int off = i * 4;
                bgra[off] = pixels[i * 3 + 2];     // B
                bgra[off + 1] = pixels[i * 3 + 1]; // G
                bgra[off + 2] = pixels[i * 3];     // R
                bgra[off + 3] = 255;
            }
        }

        // quality (1-100) → butteraugli distance: 100→0.5, 50→3.0
        float distance = Math.Clamp(0.5f + (100 - quality) * 0.05f, 0.5f, 5.0f);

        var result = NativeEncoderGuard.TryEncode("GainMap_GainMapJPEG", () =>
        {
            return JpegLiNative.Encode(bgra, w, h, distance, "444", null, forceBaseline: true);
        });
        if (result.Success) return result.Value!;
        throw new InvalidOperationException($"[GainMap] Gain Map JPEG 编码失败: {result.Error?.Message}");
    }

    // ═══════════════════════════════════════
    //  逐像素增益比计算
    // ═══════════════════════════════════════

    /// <summary>
    /// 计算增益图像素。
    /// 输入: hdrPixels = 原始 HDR scRGB 线性，sdrLinear = Reinhard 色调映射后的 SDR 线性。
    /// 两者在同一线性空间 (1.0=80 nits)，增益比 = HDR / SDR 反映色调映射压缩的额外亮度。
    /// Reinhard 是单调压缩，保证 SDR ≤ HDR → 增益恒 ≥ 1 → log2(gain) ∈ [0, log2(headroom)]。
    /// Gray 模式：gain = log2(max(HDR_lum / SDR_lum, 1.0))，单通道 8-bit。
    /// RGB 模式：gain_c = log2(max(HDR_c / SDR_c, 1.0))，三通道 8-bit。
    /// 增益值映射为 8-bit：gain_byte = log_gain / maxLog2 * 255（映射 [0,maxLog2] → [0,255]，全精度）。
    /// </summary>
    private static byte[] ComputeGainMap(float[] hdrPixels, float[] sdrLinear, int w, int h,
        GainMapMode mode, float maxLog2Gain)
    {
        int pixelCount = w * h;
        int channels = mode == GainMapMode.Gray ? 1 : 3;
        byte[] gain = new byte[pixelCount * channels];

        Parallel.For(0, pixelCount, i =>
        {
            int hdrOff = i * 4;
            float hR = hdrPixels[hdrOff];
            float hG = hdrPixels[hdrOff + 1];
            float hB = hdrPixels[hdrOff + 2];

            float sR = sdrLinear[hdrOff];
            float sG = sdrLinear[hdrOff + 1];
            float sB = sdrLinear[hdrOff + 2];

            float eps = 0.001f; // 防止除零

            if (mode == GainMapMode.Gray)
            {
                // 亮度增益（scRGB/BT.709 权重 — HDR 和 SDR 都在 scRGB 线性空间）
                float hLum = 0.2126f * hR + 0.7152f * hG + 0.0722f * hB;
                float sLum = 0.2126f * sR + 0.7152f * sG + 0.0722f * sB;
                float ratio = hLum / Math.Max(sLum, eps);
                float logGain = MathF.Log2(Math.Max(ratio, 1.0f));
                gain[i] = LogGainToByte(logGain, maxLog2Gain);
            }
            else
            {
                // 三通道独立增益
                int off = i * 3;
                gain[off]     = LogGainToByte(MathF.Log2(Math.Max(hR / Math.Max(sR, eps), 1.0f)), maxLog2Gain);
                gain[off + 1] = LogGainToByte(MathF.Log2(Math.Max(hG / Math.Max(sG, eps), 1.0f)), maxLog2Gain);
                gain[off + 2] = LogGainToByte(MathF.Log2(Math.Max(hB / Math.Max(sB, eps), 1.0f)), maxLog2Gain);
            }
        });

        return gain;
    }

    /// <summary>log2(gain) → 8-bit：映射 [0, maxLog2Gain] → [0, 255]（Reinhard 保证增益 ≥ 1）。</summary>
    private static byte LogGainToByte(float logGain, float maxLog2Gain)
    {
        if (maxLog2Gain <= 0f) maxLog2Gain = 1f;
        float clamped = Math.Clamp(logGain, 0f, maxLog2Gain);
        return (byte)(clamped / maxLog2Gain * 255f);
    }

    // ═══════════════════════════════════════
    //  增益图缩放（1/4 分辨率降采样）
    // ═══════════════════════════════════════

    /// <summary>
    /// 增益图降采样为 1/4 分辨率（Google Ultra HDR 规范）。
    /// 使用 ceiling 除法确保不丢失右/下边缘像素。
    /// 例: 1921×1081 → 481×271 (最后列/行只平均余数像素)。
    /// </summary>
    private static byte[] RescaleGainMap(byte[] src, int w, int h, GainMapMode mode,
        out int outW, out int outH)
    {
        // Ceiling 除法: (w+3)/4 确保覆盖所有源像素
        int oW = (w + 3) / 4;
        int oH = (h + 3) / 4;
        outW = oW; outH = oH;
        int channels = mode == GainMapMode.Gray ? 1 : 3;
        byte[] dst = new byte[outW * outH * channels];

        Parallel.For(0, oH, dy =>
        {
            for (int dx = 0; dx < oW; dx++)
            {
                int sx = dx * 4;
                int sy = dy * 4;
                int ex = Math.Min(sx + 4, w);
                int ey = Math.Min(sy + 4, h);
                int count = 0;
                float[] sum = new float[channels];

                for (int y = sy; y < ey; y++)
                for (int x = sx; x < ex; x++)
                {
                    int si = y * w * channels + x * channels;
                    for (int c = 0; c < channels; c++)
                        sum[c] += src[si + c];
                    count++;
                }

                int di = dy * oW * channels + dx * channels;
                for (int c = 0; c < channels; c++)
                    dst[di + c] = (byte)(sum[c] / count);
            }
        });

        return dst;
    }

    // ═══════════════════════════════════════
    //  MPF + XMP + ISO 21496-1 封装
    // ═══════════════════════════════════════

    /// <summary>将 Base JPEG 和 Gain Map JPEG 封装为符合 ISO 21496-1 + Ultra HDR v1 的单文件 JPEG。</summary>
    /// <remarks>
    /// 正确结构（对齐 Google libultrahdr jpegr.cpp）:
    ///   主图 (Base): [SOI][APP1: XMP (hdrgm+GContainer)][DQT/SOF/DHT...][APP2: MPF][SOS][扫描数据][EOI]
    ///   增益图 (GainMap JPEG): [SOI][APP2: ISO 21496-1 (命名空间+二进制元数据)][DQT/SOF/DHT...][SOS][扫描数据][EOI]
    ///   最终 EOI
    /// 关键: XMP/MPF 必须插在 Base JPEG 的 SOS **之前**；ISO 元数据必须插在增益图 JPEG 的 SOI **之后**。
    /// 双元数据: XMP (hdrgm, Ultra HDR v1 解码器) + ISO 21496-1 二进制 (APP2, ISO 解码器)。
    /// </remarks>
    private static void WriteJpegGainMapFile(byte[] baseJpeg, byte[] gainMapJpeg,
        int baseW, int baseH, int gmW, int gmH, GainMapMode mode, float headroom, string outputPath)
    {
        byte[] xmp = BuildXmpMetadata(baseW, baseH, gmW, gmH, mode, headroom);
        byte[] iso = BuildIso21496Metadata(mode, headroom);

        // 1. 定位 Base JPEG 的 SOS (FFDA) 标记位置
        int sosIndex = FindSosIndex(baseJpeg);
        if (sosIndex < 0)
        {
            // 找不到 SOS（异常），回退旧逻辑：仅写入 Base JPEG
            File.WriteAllBytes(outputPath, baseJpeg);
            return;
        }

        // 2. Base JPEG 头部段 = [SOI ... SOS 之前]，不含 SOS
        //    SOS 段 = 2 字节 marker + 2 字节长度 + 参数
        int sosLen = 2 + ((baseJpeg[sosIndex + 2] << 8) | baseJpeg[sosIndex + 3]);
        int baseHeaderLen = sosIndex;                       // 头部段（到 SOS 前）
        int baseScanStart = sosIndex + sosLen;              // 扫描数据起点
        int baseScanLen = baseJpeg.Length - baseScanStart;  // 扫描数据（含 EOI）

        // 3. 将 ISO 21496-1 元数据插入增益图 JPEG: [SOI][APP2: ISO][增益图数据(去SOI)]
        byte[] gainMapWithIso = InsertIsoIntoSegment(gainMapJpeg, iso);

        // 4. 构建 MPF（big-endian）
        //    Gain Map 偏移 = Base头部 + APP1(XMP) + APP2(MPF) + SOS段 + Base扫描数据
        int app1Total = 2 + 2 + xmp.Length;       // marker(2) + length(2) + data
        int mpfDataLen = 86;                      // BuildMpf 输出固定长度
        int app2Total = 2 + 2 + mpfDataLen;       // marker(2) + length(2) + data
        int gmOffset = baseHeaderLen + app1Total + app2Total + sosLen + baseScanLen;
        byte[] mpf = BuildMpf(gmOffset, gainMapWithIso.Length);

        // 5. 构建完整文件
        //    结构: Base头部 + APP1(XMP) + APP2(MPF) + SOS + Base扫描(含EOI)
        //        + GainMap(含ISO段, 完整含EOI) + 最终EOI
        using var ms = new MemoryStream(baseJpeg.Length + app1Total + app2Total + gainMapWithIso.Length + 2);
        // 写入 Base 头部段（SOI...SOS 之前）
        ms.Write(baseJpeg, 0, baseHeaderLen);
        // 插入 APP1 (XMP) + APP2 (MPF)
        WriteAppSegmentMem(ms, 0xE1, xmp);
        WriteAppSegmentMem(ms, 0xE2, mpf);
        // 写入 SOS 段 + Base 扫描数据（含 EOI）
        ms.Write(baseJpeg, sosIndex, sosLen + baseScanLen);
        // 写入完整 Gain Map JPEG（含 ISO 段 + 自己的 SOI...EOI）
        ms.Write(gainMapWithIso, 0, gainMapWithIso.Length);
        // 最终 EOI
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD9);

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    /// <summary>ISO 21496-1 命名空间 (libultrahdr kIsoNameSpace)。</summary>
    private const string IsoNamespace = "urn:iso:std:iso:ts:21496:-1";

    /// <summary>将 ISO 21496-1 元数据作为 APP2 段插入增益图 JPEG 的 SOI 之后。</summary>
    private static byte[] InsertIsoIntoSegment(byte[] gainMapJpeg, byte[] isoData)
    {
        // 增益图必须以 SOI 开头
        if (gainMapJpeg.Length < 2 || gainMapJpeg[0] != 0xFF || gainMapJpeg[1] != 0xD8)
            return gainMapJpeg;

        // APP2 段 payload = 命名空间(含 null 终止符) + 二进制元数据
        byte[] ns = System.Text.Encoding.ASCII.GetBytes(IsoNamespace);
        int payloadLen = ns.Length + 1 + isoData.Length; // namespace + '\0' + metadata
        using var ms = new MemoryStream(gainMapJpeg.Length + 2 + 2 + payloadLen);
        ms.Write(gainMapJpeg, 0, 2); // SOI
        // APP2 marker + length
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE2);
        int segLen = payloadLen + 2;
        ms.WriteByte((byte)(segLen >> 8));
        ms.WriteByte((byte)(segLen & 0xFF));
        // 命名空间 + null 终止符
        ms.Write(ns, 0, ns.Length);
        ms.WriteByte(0x00);
        // 二进制元数据
        ms.Write(isoData, 0, isoData.Length);
        // 剩余增益图数据（去 SOI）
        ms.Write(gainMapJpeg, 2, gainMapJpeg.Length - 2);
        return ms.ToArray();
    }

    /// <summary>在 JPEG 字节流中定位 SOS (FFDA) 标记的偏移。</summary>
    private static int FindSosIndex(byte[] jpeg)
    {
        int i = 2; // 跳过 SOI
        while (i < jpeg.Length - 4)
        {
            if (jpeg[i] == 0xFF)
            {
                byte m = jpeg[i + 1];
                if (m == 0xDA) return i; // SOS
                if (m == 0xD9) break;    // 意外 EOI，无 SOS
                // 跳过带长度参数的标记段
                if (m != 0x01 && !(m >= 0xD0 && m <= 0xD7))
                {
                    int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
                    i += 2 + len;
                }
                else i += 2;
            }
            else i++;
        }
        return -1;
    }

    private static void WriteAppSegmentMem(MemoryStream ms, byte marker, byte[] data)
    {
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        int len = data.Length + 2;
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)(len & 0xFF));
        ms.Write(data, 0, data.Length);
    }

    /// <summary>构建符合 CIPA DC-007 (MPF) 的 MPF 数据，大端序。</summary>
    /// <remarks>
    /// attribute 值对齐 Google libultrahdr (multipictureformat.h):
    ///   kMPEntryAttributeFormatJpeg = 0x000000, kMPEntryAttributeTypePrimary = 0x030000
    /// 主图 (Primary):  attribute = 0x030000 (JPEG 格式 + Primary 类型)
    /// 增益图 (GainMap): attribute = 0x000000 (JPEG 格式，无额外类型)
    /// </remarks>
    /// <param name="mpfOffset">Gain Map JPEG 数据在文件中的偏移量。</param>
    /// <param name="mpfSize">Gain Map JPEG 数据大小（字节）。</param>
    private static byte[] BuildMpf(int mpfOffset, int mpfSize)
    {
        // MPF APP2 数据布局 (相对数据起始):
        //   offset 0:  "MPF\0" (4)
        //   offset 4:  TIFF header "MM" + 42 + offset_to_IFD(8) (8)
        //   offset 12: IFD entry count (2) = 3
        //   offset 14: Entry0 MPFVersion (12)
        //   offset 26: Entry1 NumberOfImages (12)
        //   offset 38: Entry2 MPEntry (12)
        //   offset 50: next IFD (4) = 0
        //   offset 54: Image Data Entry 0 (16)
        //   offset 70: Image Data Entry 1 (16)
        //   offset 86: 总长度
        var ms = new MemoryStream(86);
        var bw = new BinaryWriter(ms);

        // MPF identifier: "MPF\0"
        bw.Write((byte)0x4D); bw.Write((byte)0x50); bw.Write((byte)0x46); bw.Write((byte)0x00);

        // TIFF header (Big Endian)
        bw.Write((byte)0x4D); bw.Write((byte)0x4D); // MM = Big Endian
        bw.Write((byte)0x00); bw.Write((byte)0x2A); // TIFF magic (42)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x08); // offset to IFD = 8

        // IFD: 3 entries (MPFVersion + NumberOfImages + MPEntry)
        bw.Write((byte)0x00); bw.Write((byte)0x03); // entry count = 3

        // Entry 0: MPFVersion (0xB000) = "0100"
        bw.Write((byte)0xB0); bw.Write((byte)0x00); // tag
        bw.Write((byte)0x00); bw.Write((byte)0x07); // type: UNDEFINED
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x04); // count = 4
        bw.Write((byte)0x30); bw.Write((byte)0x31); bw.Write((byte)0x30); bw.Write((byte)0x30); // "0100"

        // Entry 1: NumberOfImages (0xB001) = 2
        bw.Write((byte)0xB0); bw.Write((byte)0x01); // tag
        bw.Write((byte)0x00); bw.Write((byte)0x04); // type: LONG
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x01); // count = 1
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x02); // value = 2 images

        // Entry 2: MPEntry (0xB002) — 指向 Individual Image Data 列表
        bw.Write((byte)0xB0); bw.Write((byte)0x02); // tag
        bw.Write((byte)0x00); bw.Write((byte)0x07); // type: UNDEFINED
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x20); // count = 32 (2 entries * 16 bytes)
        // value 指向 IFD 后的数据区 (相对 MPF 数据起始 = 54)
        int entryOffset = 54;
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)entryOffset);

        // Next IFD offset = 0 (no more IFDs)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);

        // ── Individual Image Data Entries (32 bytes: 2 entries × 16 bytes) ──

        // Image 0: Base JPEG (Primary, attribute = 0x030000, big-endian)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x03); bw.Write((byte)0x00); // attribute: JPEG + Primary
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // offset = 0 (same file)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // size = 0 (entire file)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // reserved

        // Image 1: Gain Map JPEG (attribute = 0x000000: JPEG 格式，无额外类型)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // attribute: JPEG
        // offset (big-endian)
        bw.Write((byte)(mpfOffset >> 24));
        bw.Write((byte)(mpfOffset >> 16));
        bw.Write((byte)(mpfOffset >> 8));
        bw.Write((byte)mpfOffset);
        // size (big-endian)
        bw.Write((byte)(mpfSize >> 24));
        bw.Write((byte)(mpfSize >> 16));
        bw.Write((byte)(mpfSize >> 8));
        bw.Write((byte)mpfSize);
        // reserved
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);

        return ms.ToArray();
    }

    /// <summary>构建符合 Ultra HDR v1 (Adobe hdrgm) 规范的 XMP 元数据。</summary>
    /// <remarks>
    /// 命名空间: http://ns.adobe.com/hdr-gain-map/1.0/ (前缀 hdrgm)
    /// 关键语义 (Android Ultra HDR 规范 v1.1):
    ///   - hdrgm:GainMapMin/Max 存储 map_min_log2/map_max_log2 (log2 值)，不是线性增益！
    ///     像素编码: gain_byte = log2(HDR/SDR) / maxLog2 * 255, 范围 [0, maxLog2] → [0, 255]
    ///     (Reinhard 保证 SDR ≤ HDR, 增益恒 ≥ 1, 故 GainMapMin = log2(1) = 0)
    ///     GainMapMax = log2(headroom) = log2(HDR峰值/SDR白点)
    ///     解码公式: log_boost = min*(1-recovery) + max*recovery; HDR = (SDR+off)*2^log_boost - off
    ///   - hdrgm:HDRCapacityMax = GainMapMax (log2), HDRCapacityMin = max(GainMapMin, 0)
    ///   - BaseRenditionIsHDR = False (主图为 SDR)
    /// </remarks>
    public static byte[] BuildXmpMetadata(int baseW, int baseH, int gmW, int gmH, GainMapMode mode,
        float headroom = 12.5f)
    {
        // log2 增益映射范围（与 ComputeGainMap/LogGainToByte 一致）
        const float gainMinLog2 = 0.0f;
        float gainMaxLog2 = MathF.Log2(Math.Max(headroom, 1.0f)); // log2(HDR峰值/SDR白点)
        const float offset = 0.015625f; // 1/64，规范推荐值
        const float gamma = 1.0f;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " hdrgm:Version=\"1.0\">" +
            "<Container:Directory>" +
            "<rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\"/>" +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "<hdrgm:GainMapMin>" + gainMinLog2.ToString("0.0", inv) + "</hdrgm:GainMapMin>" +
            "<hdrgm:GainMapMax>" + gainMaxLog2.ToString("0.00", inv) + "</hdrgm:GainMapMax>" +
            "<hdrgm:Gamma>" + gamma.ToString("0.0", inv) + "</hdrgm:Gamma>" +
            "<hdrgm:OffsetSDR>" + offset.ToString("0.000000", inv) + "</hdrgm:OffsetSDR>" +
            "<hdrgm:OffsetHDR>" + offset.ToString("0.000000", inv) + "</hdrgm:OffsetHDR>" +
            "<hdrgm:HDRCapacityMin>0</hdrgm:HDRCapacityMin>" +
            "<hdrgm:HDRCapacityMax>" + gainMaxLog2.ToString("0.00", inv) + "</hdrgm:HDRCapacityMax>" +
            "<hdrgm:BaseRenditionIsHDR>False</hdrgm:BaseRenditionIsHDR>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";

        return System.Text.Encoding.UTF8.GetBytes(xmp);
    }

    /// <summary>构建 ISO 21496-1 二进制增益图元数据 (APP2 段数据)。</summary>
    /// <remarks>
    /// 对齐 Google libultrahdr (gainmapmetadata.cpp) 的 ISO 21496-1 二进制封装:
    ///   [min_version: u16 BE = 0][writer_version: u16 BE = 0][flags: u8]
    ///   flags: bit7=multi-channel, bit6=use base color space, bit2=backward, bit3=common denominator
    ///   非 common denominator 模式 (各字段独立分母):
    ///     [baseHdrHeadroomN: u32][baseHdrHeadroomD: u32][alternateHdrHeadroomN: u32][alternateHdrHeadroomD: u32]
    ///     [每通道: gainMapMinN: s32, gainMapMinD: u32, gainMapMaxN: s32, gainMapMaxD: u32,
    ///              gainMapGammaN: u32, gainMapGammaD: u32, baseOffsetN: s32, baseOffsetD: u32,
    ///              alternateOffsetN: s32, alternateOffsetD: u32]
    /// 关键: 各字段分母必须精确 (gamma=1/1, offset=1/64, min/max=1), 
    ///   不能共用公共分母 — 否则 gamma 会被错误解析 (如 1/64=0.015625 导致解码过亮!)
    /// 语义: gainMapMin/Max = log2 值; headroom = log2(capacity)
    /// </remarks>
    public static byte[] BuildIso21496Metadata(GainMapMode mode, float headroom = 12.5f)
    {
        // 与 BuildXmpMetadata 一致的元数据值
        // gainMapMin/Max = log2 值, 用精确分数表示 (与 XMP 完全一致!)
        // 关键: 像素用 maxLog2Gain = log2(headroom) 编码 (如 3.32)
        //   ISO 必须用相同值, 否则解码器解释像素偏大 → 过亮
        //   用头分子/分母精确表示: log2(headroom) × 100 / 100
        const int gainMapMinLog2 = 0;    // log2(1.0) — Reinhard 保证增益 ≥ 1
        int gainMapMaxLog2N = (int)MathF.Round(MathF.Log2(Math.Max(headroom, 1.0f)) * 100f); // 如 332
        const int gainMapMaxLog2D = 100; // 分母 100 → 3.32
        const int gammaN = 1;            // gamma = 1.0
        const int gammaD = 1;            // gamma 分母 = 1 (必须! 不能用公共 64)
        const int offsetN = 1;           // offset = 1/64
        const int offsetD = 64;
        const int gainMinD = 1;          // min 分母 = 1 (整数 0)
        const int baseHeadroomN = 0;     // baseHdrHeadroom = 2^0 = 1.0 (capacity_min)
        const int baseHeadroomD = 1;
        int alternateHeadroomN = gainMapMaxLog2N; // alternateHdrHeadroom = log2(headroom) 精确
        const int alternateHeadroomD = 100;

        bool multiChannel = mode == GainMapMode.Rgb;
        int channels = multiChannel ? 3 : 1;

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        // min_version = 0, writer_version = 0 (u16 BE)
        WriteBe16(bw, 0);
        WriteBe16(bw, 0);

        // flags: bit7=multi-channel, bit6=useBaseColorSpace(0), bit2=backward(0)
        // 注意: 不设 bit3 (common denominator) — 各字段用独立分母
        byte flags = 0;
        if (multiChannel) flags |= 0x80;
        bw.Write(flags);

        // 非 common denominator 模式: headroom 各带分母
        WriteBe32(bw, (uint)baseHeadroomN);
        WriteBe32(bw, (uint)baseHeadroomD);
        WriteBe32(bw, (uint)alternateHeadroomN);
        WriteBe32(bw, (uint)alternateHeadroomD);

        for (int c = 0; c < channels; c++)
        {
            WriteBe32(bw, unchecked((uint)gainMapMinLog2));  // gainMapMin numerator (s32)
            WriteBe32(bw, (uint)gainMinD);                   // gainMapMin denominator
            WriteBe32(bw, unchecked((uint)gainMapMaxLog2N)); // gainMapMax numerator (s32)
            WriteBe32(bw, (uint)gainMapMaxLog2D);            // gainMapMax denominator (100)
            WriteBe32(bw, (uint)gammaN);                     // gamma numerator
            WriteBe32(bw, (uint)gammaD);                     // gamma denominator = 1
            WriteBe32(bw, unchecked((uint)offsetN));         // baseOffset numerator (s32)
            WriteBe32(bw, (uint)offsetD);                    // baseOffset denominator = 64
            WriteBe32(bw, unchecked((uint)offsetN));         // alternateOffset numerator (s32)
            WriteBe32(bw, (uint)offsetD);                    // alternateOffset denominator = 64
        }

        return ms.ToArray();
    }

    private static void WriteBe16(BinaryWriter bw, ushort value)
    {
        bw.Write((byte)(value >> 8));
        bw.Write((byte)(value & 0xFF));
    }

    private static void WriteBe32(BinaryWriter bw, uint value)
    {
        bw.Write((byte)(value >> 24));
        bw.Write((byte)(value >> 16));
        bw.Write((byte)(value >> 8));
        bw.Write((byte)(value & 0xFF));
    }
}