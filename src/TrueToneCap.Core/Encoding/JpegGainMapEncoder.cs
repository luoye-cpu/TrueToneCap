// TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs
// JPEG Gain Map (Ultra HDR) — ISO 21496-1 兼容实现
//
// 完整链路：
//   HDR scRGB float 像素
//     ├─ 色域转换 (scRGB→目标色域线性)
//     ├─ Tone Map → SDR BGRA → Base JPEG (jpegli)
//     ├─ 逐像素计算增益比 → 增益图 (Gray 或 RGB)
//     └─ 增益图 JPEG 编码 → MPF 封装 → 输出 .jpg 文件
//
// 增益图类型：
//   Gray: log2(HDR_luminance / SDR_luminance) → 单通道灰度图
//   RGB:  log2(HDR_channel / SDR_channel)  → 三通道彩色增益图

using System.IO;
using System.Text;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;

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
            var jpegBytes = JpegLiNative.Encode(sdrPixels, width, height, settings.Quality, settings.ChromaSubsampling, icc);
            File.WriteAllBytes(outputPath, jpegBytes);
        }, ct);
    }

    // ═══════════════════════════════════════
    //  Gain Map 编码主流程
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

            // Base JPEG 是色调映射后的 SDR 图像，不应嵌入广色域 ICC
            byte[]? icc = null;

            // ── 1. Tone Map HDR → SDR (BGRA8 for Base JPEG) ──
            byte[] sdrBgra = ToneMapper.FloatToSRgbBytes(hdrPixels, w, h, settings.ToneMappingParams);
            ct.ThrowIfCancellationRequested();

            // ── 2. SDR 线性浮点值（用于增益比计算）──
            // 注意: ApplyToneMapping 内部已做 PaperWhite 归一化 + 色调映射
            // 输出在 sRGB 线性空间 [0,1] 范围
            float[] sdrLinear = new float[pixelCount * 4];
            Array.Copy(hdrPixels, sdrLinear, hdrPixels.Length);
            ToneMapper.ApplyToneMapping(sdrLinear, w, h, settings.ToneMappingParams);
            ct.ThrowIfCancellationRequested();

            // ── 3. HDR 归一化到与 SDR 相同的 PaperWhite 空间 ──
            // 增益比要求 HDR 和 SDR 在同一个线性空间
            // HDR: scRGB raw (1.0 = 80 nits)
            // SDR: tone_mapped(scRGB * 80/PaperWhite)
            // 因此 HDR 也要乘以 80/PaperWhite 才能与 SDR 比较
            float pw = Math.Max(settings.ToneMappingParams.PaperWhiteNits, 1.0f);
            float pwScale = 80.0f / pw;
            float[] hdrNorm = new float[pixelCount * 4];
            for (int i = 0; i < pixelCount * 4; i++)
                hdrNorm[i] = hdrPixels[i] * pwScale;

            // ── 4. 编码 Base JPEG ──
            byte[] baseJpegBytes = EncodeToJpegBytesSafe(sdrBgra, w, h, settings.Quality, icc);
            ct.ThrowIfCancellationRequested();

            // 5. 计算增益图
            var gainMapMode = settings.GainMapMode;
            byte[] gainMapPixels = ComputeGainMap(hdrNorm, sdrLinear, w, h, gainMapMode);
            ct.ThrowIfCancellationRequested();

            // 6. 增益图缩放 + 编码
            int gainMapJpegQuality = 85;
            byte[] gainMapScaled = RescaleGainMap(gainMapPixels, w, h, gainMapMode, out int gmSW, out int gmSH);
            byte[] gainMapJpegBytes = EncodeGainMapToJpegBytesSafe(gainMapScaled, gmSW, gmSH,
                gainMapMode, gainMapJpegQuality);
            ct.ThrowIfCancellationRequested();

            // 7. MPF 封装
            WriteJpegGainMapFile(baseJpegBytes, gainMapJpegBytes, w, h, gmSW, gmSH,
                gainMapMode, outputPath);

            System.Diagnostics.Debug.WriteLine(
                $"[GainMap] 输出: {w}x{h}, 增益图: {gmSW}x{gmSH} ({gainMapMode}), " +
                $"Base={baseJpegBytes.Length / 1024}KB, GainMap={gainMapJpegBytes.Length / 1024}KB");
        }, ct);
    }

    // ═══════════════════════════════════════
    //  JPEG 编码（崩溃隔离 + 回退）
    // ═══════════════════════════════════════

    private static byte[] EncodeToJpegBytesSafe(byte[] bgra, int w, int h, float distance, byte[]? icc)
    {
        var result = NativeEncoderGuard.TryEncode("GainMap_BaseJPEG", () =>
        {
            return JpegLiNative.Encode(bgra, w, h, distance, "444", icc);
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
            return JpegLiNative.Encode(bgra, w, h, distance, "444", null);
        });
        if (result.Success) return result.Value!;
        throw new InvalidOperationException($"[GainMap] Gain Map JPEG 编码失败: {result.Error?.Message}");
    }

    // ═══════════════════════════════════════
    //  逐像素增益比计算
    // ═══════════════════════════════════════

    /// <summary>
    /// 计算增益图像素。
    /// Gray 模式：gain = log2(max(HDR_lum / SDR_lum, 1.0))，单通道 8-bit。
    /// RGB 模式：gain_c = log2(max(HDR_c / SDR_c, 1.0))，三通道 8-bit。
    /// 增益值映射为 8-bit：gain_byte = (log_gain + 8) / 16 * 255（clamp 到 0-255）。
    /// </summary>
    private static byte[] ComputeGainMap(float[] hdrPixels, float[] sdrLinear, int w, int h,
        GainMapMode mode)
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
                // 亮度增益（BT.2020 权重）
                float hLum = 0.2627f * hR + 0.6780f * hG + 0.0593f * hB;
                float sLum = 0.2627f * sR + 0.6780f * sG + 0.0593f * sB;
                float ratio = hLum / Math.Max(sLum, eps);
                float logGain = MathF.Log2(Math.Max(ratio, 1.0f));
                gain[i] = LogGainToByte(logGain);
            }
            else
            {
                // 三通道独立增益
                int off = i * 3;
                gain[off]     = LogGainToByte(MathF.Log2(Math.Max(hR / Math.Max(sR, eps), 1.0f)));
                gain[off + 1] = LogGainToByte(MathF.Log2(Math.Max(hG / Math.Max(sG, eps), 1.0f)));
                gain[off + 2] = LogGainToByte(MathF.Log2(Math.Max(hB / Math.Max(sB, eps), 1.0f)));
            }
        });

        return gain;
    }

    /// <summary>log2(gain) → 8-bit：映射 [-4, +4] → [0, 255]。</summary>
    private static byte LogGainToByte(float logGain)
    {
        float clamped = Math.Clamp(logGain, -4f, 4f);
        return (byte)((clamped + 4f) / 8f * 255f);
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
    //  MPF + XMP 封装
    // ═══════════════════════════════════════

    /// <summary>将 Base JPEG 和 Gain Map JPEG 封装为符合 ISO 21496-1 的单文件 JPEG。</summary>
    private static void WriteJpegGainMapFile(byte[] baseJpeg, byte[] gainMapJpeg,
        int baseW, int baseH, int gmW, int gmH, GainMapMode mode, string outputPath)
    {
        byte[] xmp = BuildXmpMetadata(baseW, baseH, gmW, gmH, mode);

        // 1. Base JPEG 去掉 EOI（因为后续要嵌入 APP1/APP2）
        int baseLen = TrimTrailingEoi(baseJpeg);

        // 2. Gain Map JPEG 保留完整 SOI 和 EOI（MPF 要求每张图是完整 JPEG）
        //    完整的 Gain Map JPEG 包含 SOI...EOI
        int gmLen = gainMapJpeg.Length;

        // 3. 构建 MPF（big-endian，包含正确的偏移量）
        //    Gain Map 数据偏移 = Base JPEG + APP1 + APP2
        int mpfDataLen = 58; // BuildMpf 输出的固定长度
        int gmOffset = baseLen
            + 2 + 2 + xmp.Length       // APP1: marker(2) + length(2) + data
            + 2 + 2 + mpfDataLen;       // APP2: marker(2) + length(2) + data
        byte[] mpf = BuildMpf(gmOffset);

        // 4. 构建完整文件
        //    结构: Base(无EOI) + APP1(XMP) + APP2(MPF) + GainMap(完整含EOI) + 最终EOI
        using var ms = new MemoryStream(baseLen + 100 + xmp.Length + mpf.Length + gmLen + 2);
        ms.Write(baseJpeg, 0, baseLen);
        WriteAppSegmentMem(ms, 0xE1, xmp);
        WriteAppSegmentMem(ms, 0xE2, mpf);
        ms.Write(gainMapJpeg, 0, gmLen); // 完整 Gain Map JPEG（含 EOI）
        ms.WriteByte(0xFF);              // 最终 EOI
        ms.WriteByte(0xD9);

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    private static int TrimTrailingEoi(byte[] jpeg)
    {
        int len = jpeg.Length;
        if (len >= 2 && jpeg[len - 2] == 0xFF && jpeg[len - 1] == 0xD9)
            return len - 2;
        return len;
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

    /// <summary>构建正确的 MPF（Multi-Picture Format）数据，大端序。</summary>
    private static byte[] BuildMpf(int mpfOffset)
    {
        // 使用手动大端写入，确保规范兼容
        var ms = new MemoryStream(58);
        var bw = new BinaryWriter(ms);

        // MPF identifier: "MPF\0"
        bw.Write((byte)0x4D); bw.Write((byte)0x50); bw.Write((byte)0x46); bw.Write((byte)0x00);

        // TIFF header (Big Endian)
        bw.Write((byte)0x4D); bw.Write((byte)0x4D); // MM = Big Endian
        bw.Write((byte)0x00); bw.Write((byte)0x2A); // TIFF magic (42)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x08); // offset to IFD = 8

        // IFD: 2 entries
        bw.Write((byte)0x00); bw.Write((byte)0x02); // entry count = 2

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

        // Next IFD offset = 0 (no more IFDs)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);

        // Individual Image Data Entry for Image 0: Base JPEG (offset = 0, same file)
        bw.Write((byte)0x02); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // attribute
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // offset = 0

        // Individual Image Data Entry for Image 1: Gain Map JPEG
        bw.Write((byte)0x02); bw.Write((byte)0x00); bw.Write((byte)0x02); bw.Write((byte)0x00); // attribute (Gain Map)
        // offset (big-endian)
        bw.Write((byte)(mpfOffset >> 24));
        bw.Write((byte)(mpfOffset >> 16));
        bw.Write((byte)(mpfOffset >> 8));
        bw.Write((byte)mpfOffset);

        return ms.ToArray();
    }

    /// <summary>构建 ISO 21496-1 兼容的 XMP 元数据。</summary>
    private static byte[] BuildXmpMetadata(int baseW, int baseH, int gmW, int gmH, GainMapMode mode)
    {
        string gmType = mode == GainMapMode.Gray
            ? "urn:iso:std:iso:21496:-1:schema:gainmap:type:luminance"
            : "urn:iso:std:iso:21496:-1:schema:gainmap:type:color";

        string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:gm=\"urn:iso:std:iso:21496:-1:schema:gainmap\">" +
            "<gm:Version>1.0</gm:Version>" +
            "<gm:Type>" + gmType + "</gm:Type>" +
            "<gm:Width>" + gmW + "</gm:Width>" +
            "<gm:Height>" + gmH + "</gm:Height>" +
            "<gm:MinGain>0.0625</gm:MinGain>" +
            "<gm:MaxGain>16.0</gm:MaxGain>" +
            "<gm:Gamma>1.0</gm:Gamma>" +
            "<gm:OffsetSDR>0.015625</gm:OffsetSDR>" +
            "<gm:OffsetHDR>0.015625</gm:OffsetHDR>" +
            "<gm:BaseRenditionIsHDR>False</gm:BaseRenditionIsHDR>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";

        return System.Text.Encoding.UTF8.GetBytes(xmp);
    }
}