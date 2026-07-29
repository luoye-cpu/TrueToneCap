// TrueToneCap.Core/Encoding/ManagedJpegEncoder.cs
// 纯托管基线 JPEG 编码器 — 零外部依赖，输出标准 JPEG (FFD8...FFD9)
// 支持: 8-bit, 4:4:4/4:2:0 色度, ICC APP2 嵌入, 可配置质量
// 截图优化: 默认 4:4:4 (无色度子采样), 浮点 DCT, 优化量化表

namespace TrueToneCap.Core.Encoding;

/// <summary>纯托管基线 JPEG 编码器。</summary>
public static class ManagedJpegEncoder
{
    // 标准 JPEG 量化表 (Annex K)
    private static readonly byte[] StdLuminance =
    [
        16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55,
        14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62,
        18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92,
        49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99
    ];

    private static readonly byte[] StdChrominance =
    [
        17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99,
        24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99
    ];

    // 标准 Huffman 表
    private static readonly byte[] DcLumBits = [0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0];
    private static readonly byte[] DcLumVals = [0,1,2,3,4,5,6,7,8,9,10,11];
    private static readonly byte[] AcLumBits = [0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,0x7d];
    private static readonly byte[] AcLumVals =
    [
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08,0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16,0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
        0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,
        0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,
        0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
        0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    ];
    private static readonly byte[] DcChromBits = [0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0];
    private static readonly byte[] DcChromVals = [0,1,2,3,4,5,6,7,8,9,10,11];
    private static readonly byte[] AcChromBits = [0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,0x77];
    private static readonly byte[] AcChromVals =
    [
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,
        0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34,0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
        0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,
        0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,
        0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
        0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    ];

    /// <summary>编码 BGRA 像素为标准基线 JPEG。</summary>
    /// <param name="bgra">BGRA 像素数据。</param>
    /// <param name="w">宽度。</param>
    /// <param name="h">高度。</param>
    /// <param name="path">输出路径。</param>
    /// <param name="quality">质量 1-100。</param>
    /// <param name="iccProfile">可选 ICC profile (APP2 嵌入)。</param>
    /// <param name="chroma">色度采样: "444" / "422" / "420"。</param>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int quality = 92, byte[]? iccProfile = null, string chroma = "444")
    {
        var data = EncodeToBytes(bgra, w, h, quality, iccProfile, chroma);
        File.WriteAllBytes(path, data);
    }

    /// <summary>编码 BGRA 像素为 JPEG 字节数组。</summary>
    public static byte[] EncodeToBytes(byte[] bgra, int w, int h, int quality = 92, byte[]? iccProfile = null, string chroma = "444")
    {
        quality = Math.Clamp(quality, 1, 100);

        // 计算量化表
        var lumQt = ScaleQuantTable(StdLuminance, quality);
        var chromQt = ScaleQuantTable(StdChrominance, quality);

        // 构建 Huffman 编码表
        var dcLumHuff = BuildHuffTable(DcLumBits, DcLumVals);
        var acLumHuff = BuildHuffTable(AcLumBits, AcLumVals);
        var dcChromHuff = BuildHuffTable(DcChromBits, DcChromVals);
        var acChromHuff = BuildHuffTable(AcChromBits, AcChromVals);

        using var ms = new MemoryStream(w * h); // 预估大小

        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // APP0 (JFIF)
        WriteApp0(ms);

        // APP2 (ICC Profile)
        if (iccProfile is { Length: > 0 })
            WriteIccApp2(ms, iccProfile);

        // DQT
        WriteDqt(ms, 0, lumQt);
        WriteDqt(ms, 1, chromQt);

        // SOF0 (Baseline)
        WriteSof0(ms, w, h, chroma);

        // DHT
        WriteDht(ms, 0x00, DcLumBits, DcLumVals);
        WriteDht(ms, 0x10, AcLumBits, AcLumVals);
        WriteDht(ms, 0x01, DcChromBits, DcChromVals);
        WriteDht(ms, 0x11, AcChromBits, AcChromVals);

        // SOS
        WriteSos(ms);

        // 编码扫描数据
        var bitWriter = new BitWriter(ms);
        EncodeScan(bgra, w, h, lumQt, chromQt, dcLumHuff, acLumHuff, dcChromHuff, acChromHuff, bitWriter, chroma);
        bitWriter.Flush();

        // EOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);

        return ms.ToArray();
    }

    // ── 量化表缩放 ──
    private static byte[] ScaleQuantTable(byte[] baseTable, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var result = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int val = (baseTable[i] * scale + 50) / 100;
            result[i] = (byte)Math.Clamp(val, 1, 255);
        }
        return result;
    }

    // ── Huffman 表构建 ──
    private static (ushort[] codes, byte[] sizes) BuildHuffTable(byte[] bits, byte[] vals)
    {
        var codes = new ushort[256];
        var sizes = new byte[256];
        ushort code = 0;
        int idx = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++)
            {
                codes[vals[idx]] = code;
                sizes[vals[idx]] = (byte)len;
                code++;
                idx++;
            }
            code <<= 1;
        }
        return (codes, sizes);
    }

    // ── 扫描数据编码 ──
    private static void EncodeScan(byte[] bgra, int w, int h,
        byte[] lumQt, byte[] chromQt,
        (ushort[] codes, byte[] sizes) dcLum, (ushort[] codes, byte[] sizes) acLum,
        (ushort[] codes, byte[] sizes) dcChrom, (ushort[] codes, byte[] sizes) acChrom,
        BitWriter bw, string chroma = "444")
    {
        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;
        var block = new float[64];
        var quantized = new int[64];

        if (chroma == "420")
        {
            // 4:2:0 — MCU 16x16: 4Y + 1Cb + 1Cr
            int mcuW = (w + 15) / 16;
            int mcuH = (h + 15) / 16;
            for (int mcuRow = 0; mcuRow < mcuH; mcuRow++)
            {
                for (int mcuCol = 0; mcuCol < mcuW; mcuCol++)
                {
                    int bx = mcuCol * 16, by = mcuRow * 16;
                    // 4 Y blocks (2x2)
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            ExtractBlock(bgra, w, h, bx + dx * 8, by + dy * 8, 0, block);
                            Fdct(block); Quantize(block, lumQt, quantized);
                            prevDcY = EncodeDcBlock(bw, quantized, prevDcY, dcLum, acLum);
                        }
                    // Cb (2x2 下采样)
                    ExtractBlockDownsampled(bgra, w, h, bx, by, 2, 2, 1, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCb = EncodeDcBlock(bw, quantized, prevDcCb, dcChrom, acChrom);
                    // Cr (2x2 下采样)
                    ExtractBlockDownsampled(bgra, w, h, bx, by, 2, 2, 2, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCr = EncodeDcBlock(bw, quantized, prevDcCr, dcChrom, acChrom);
                }
            }
        }
        else if (chroma == "422")
        {
            // 4:2:2 — MCU 16x8: 2Y + 1Cb + 1Cr
            int mcuW = (w + 15) / 16;
            int mcuH = (h + 7) / 8;
            for (int mcuRow = 0; mcuRow < mcuH; mcuRow++)
            {
                for (int mcuCol = 0; mcuCol < mcuW; mcuCol++)
                {
                    int bx = mcuCol * 16, by = mcuRow * 8;
                    // 2 Y blocks (2x1)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        ExtractBlock(bgra, w, h, bx + dx * 8, by, 0, block);
                        Fdct(block); Quantize(block, lumQt, quantized);
                        prevDcY = EncodeDcBlock(bw, quantized, prevDcY, dcLum, acLum);
                    }
                    // Cb (2x1 水平下采样)
                    ExtractBlockDownsampled(bgra, w, h, bx, by, 2, 1, 1, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCb = EncodeDcBlock(bw, quantized, prevDcCb, dcChrom, acChrom);
                    // Cr (2x1 水平下采样)
                    ExtractBlockDownsampled(bgra, w, h, bx, by, 2, 1, 2, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCr = EncodeDcBlock(bw, quantized, prevDcCr, dcChrom, acChrom);
                }
            }
        }
        else
        {
            // 4:4:4 — 每个 MCU 是 8x8
            int mcuW = (w + 7) / 8;
            int mcuH = (h + 7) / 8;
            for (int mcuRow = 0; mcuRow < mcuH; mcuRow++)
            {
                for (int mcuCol = 0; mcuCol < mcuW; mcuCol++)
                {
                    int bx = mcuCol * 8, by = mcuRow * 8;
                    ExtractBlock(bgra, w, h, bx, by, 0, block);
                    Fdct(block); Quantize(block, lumQt, quantized);
                    prevDcY = EncodeDcBlock(bw, quantized, prevDcY, dcLum, acLum);
                    ExtractBlock(bgra, w, h, bx, by, 1, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCb = EncodeDcBlock(bw, quantized, prevDcCb, dcChrom, acChrom);
                    ExtractBlock(bgra, w, h, bx, by, 2, block);
                    Fdct(block); Quantize(block, chromQt, quantized);
                    prevDcCr = EncodeDcBlock(bw, quantized, prevDcCr, dcChrom, acChrom);
                }
            }
        }
    }

    /// <summary>从 BGRA 提取下采样的 8x8 色度块（用于 4:2:0 / 4:2:2）。</summary>
    /// <param name="stepX">水平下采样因子 (2 = 每 2 像素取 1)。</param>
    /// <param name="stepY">垂直下采样因子 (2 = 每 2 行取 1)。</param>
    private static void ExtractBlockDownsampled(byte[] bgra, int w, int h, int bx, int by, int stepX, int stepY, int channel, float[] block)
    {
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = 0; dy < stepY; dy++)
                {
                    for (int dx = 0; dx < stepX; dx++)
                    {
                        int x = Math.Min(bx + c * stepX + dx, w - 1);
                        int y = Math.Min(by + r * stepY + dy, h - 1);
                        int idx = (y * w + x) * 4;
                        float b = bgra[idx], g = bgra[idx + 1], rr = bgra[idx + 2];
                        sum += channel switch
                        {
                            0 => 0.299f * rr + 0.587f * g + 0.114f * b - 128f,
                            1 => -0.1687f * rr - 0.3313f * g + 0.5f * b,
                            _ => 0.5f * rr - 0.4187f * g - 0.0813f * b
                        };
                        count++;
                    }
                }
                block[r * 8 + c] = sum / count;
            }
        }
    }

    /// <summary>从 BGRA 提取 8x8 块并转换为 YCbCr。</summary>
    private static void ExtractBlock(byte[] bgra, int w, int h, int bx, int by, int channel, float[] block)
    {
        // 内部块快速路径：无需边界检查
        if (bx + 8 <= w && by + 8 <= h)
        {
            ExtractBlockFast(bgra, w, bx, by, channel, block);
            return;
        }

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                int x = Math.Min(bx + c, w - 1);
                int y = Math.Min(by + r, h - 1);
                int idx = (y * w + x) * 4;
                float b = bgra[idx], g = bgra[idx + 1], rr = bgra[idx + 2];

                float val = channel switch
                {
                    0 => 0.299f * rr + 0.587f * g + 0.114f * b - 128f,        // Y
                    1 => -0.1687f * rr - 0.3313f * g + 0.5f * b,              // Cb
                    _ => 0.5f * rr - 0.4187f * g - 0.0813f * b                // Cr
                };
                block[r * 8 + c] = val;
            }
        }
    }

    /// <summary>内部块快速提取（无边界检查）。</summary>
    private static void ExtractBlockFast(byte[] bgra, int w, int bx, int by, int channel, float[] block)
    {
        int rowStride = w * 4;
        for (int r = 0; r < 8; r++)
        {
            int rowBase = (by + r) * rowStride + bx * 4;
            int dstOff = r * 8;
            for (int c = 0; c < 8; c++)
            {
                int idx = rowBase + c * 4;
                float b = bgra[idx], g = bgra[idx + 1], rr = bgra[idx + 2];
                block[dstOff + c] = channel switch
                {
                    0 => 0.299f * rr + 0.587f * g + 0.114f * b - 128f,
                    1 => -0.1687f * rr - 0.3313f * g + 0.5f * b,
                    _ => 0.5f * rr - 0.4187f * g - 0.0813f * b
                };
            }
        }
    }

    /// <summary>快速 DCT (AAN 算法) — 行 DCT + 转置 + 行 DCT（避免 stride=8 列访问）。</summary>
    private static void Fdct(float[] block)
    {
        // 行 DCT (stride=1, 连续内存, JIT 可自动向量化)
        for (int i = 0; i < 8; i++)
            Fdct1D(block, i * 8, 1);

        // 8×8 转置（使列 DCT 也变为连续内存访问）
        Transpose8x8(block);

        // 列 DCT（转置后变为行 DCT）
        for (int i = 0; i < 8; i++)
            Fdct1D(block, i * 8, 1);

        // 转置回原始布局
        Transpose8x8(block);
    }

    /// <summary>8×8 矩阵原地转置。</summary>
    private static void Transpose8x8(float[] d)
    {
        for (int r = 0; r < 8; r++)
        {
            for (int c = r + 1; c < 8; c++)
            {
                (d[r * 8 + c], d[c * 8 + r]) = (d[c * 8 + r], d[r * 8 + c]);
            }
        }
    }

    private static void Fdct1D(float[] d, int off, int stride)
    {
        float t0 = d[off] + d[off + 7 * stride];
        float t7 = d[off] - d[off + 7 * stride];
        float t1 = d[off + stride] + d[off + 6 * stride];
        float t6 = d[off + stride] - d[off + 6 * stride];
        float t2 = d[off + 2 * stride] + d[off + 5 * stride];
        float t5 = d[off + 2 * stride] - d[off + 5 * stride];
        float t3 = d[off + 3 * stride] + d[off + 4 * stride];
        float t4 = d[off + 3 * stride] - d[off + 4 * stride];

        float t10 = t0 + t3, t13 = t0 - t3;
        float t11 = t1 + t2, t12 = t1 - t2;

        d[off] = t10 + t11;
        d[off + 4 * stride] = t10 - t11;

        float z1 = (t12 + t13) * 0.707106781f;
        d[off + 2 * stride] = t13 + z1;
        d[off + 6 * stride] = t13 - z1;

        float t20 = t4 + t5, t21 = t5 + t6, t22 = t6 + t7;
        float z5 = (t20 - t22) * 0.382683433f;
        float z2 = 0.541196100f * t20 + z5;
        float z4 = 1.306562965f * t22 + z5;
        float z3 = t21 * 0.707106781f;

        float t30 = t7 + z3, t31 = t7 - z3;
        d[off + 5 * stride] = t31 + z2;
        d[off + 3 * stride] = t31 - z2;
        d[off + stride] = t30 + z4;
        d[off + 7 * stride] = t30 - z4;
    }

    private static void Quantize(float[] block, byte[] qt, int[] output)
    {
        // 蝶形 DCT 产生未归一化系数（比正交 DCT 大 64 倍 = 8×8）
        // 预计算倒数避免 64 次除法
        for (int i = 0; i < 64; i++)
            output[i] = (int)MathF.Round(block[i] * s_quantReciprocals[qt[i]]);
    }

    // 预计算 1/(64*qt) 倒数表 (qt 范围 1-255)
    private static readonly float[] s_quantReciprocals = BuildQuantReciprocals();
    private static float[] BuildQuantReciprocals()
    {
        var table = new float[256];
        for (int i = 1; i < 256; i++)
            table[i] = 1.0f / (64f * i);
        return table;
    }

    // Zigzag 顺序
    private static readonly int[] Zigzag =
    [
        0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,
        12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
        35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,
        58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63
    ];

    private static int EncodeDcBlock(BitWriter bw, int[] quantized, int prevDc,
        (ushort[] codes, byte[] sizes) dcHuff, (ushort[] codes, byte[] sizes) acHuff)
    {
        int dc = quantized[0] - prevDc;

        // DC 系数
        int dcCat = Category(dc);
        bw.WriteHuff(dcHuff.codes[dcCat], dcHuff.sizes[dcCat]);
        if (dcCat > 0) bw.WriteBits(dc >= 0 ? dc : dc + (1 << dcCat) - 1, dcCat);

        // AC 系数 (zigzag)
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int ac = quantized[Zigzag[i]];
            if (ac == 0) { zeroRun++; continue; }

            while (zeroRun >= 16)
            {
                bw.WriteHuff(acHuff.codes[0xF0], acHuff.sizes[0xF0]); // ZRL
                zeroRun -= 16;
            }

            int acCat = Category(ac);
            int symbol = (zeroRun << 4) | acCat;
            bw.WriteHuff(acHuff.codes[symbol], acHuff.sizes[symbol]);
            bw.WriteBits(ac >= 0 ? ac : ac + (1 << acCat) - 1, acCat);
            zeroRun = 0;
        }

        if (zeroRun > 0)
            bw.WriteHuff(acHuff.codes[0x00], acHuff.sizes[0x00]); // EOB

        return quantized[0];
    }

    private static int Category(int val)
    {
        if (val == 0) return 0;
        int abs = Math.Abs(val);
        int cat = 0;
        while (abs > 0) { cat++; abs >>= 1; }
        return cat;
    }

    // ── JPEG 段写入 ──

    private static void WriteApp0(Stream s)
    {
        s.WriteByte(0xFF); s.WriteByte(0xE0);
        WriteU16(s, 16); // length
        s.Write("JFIF\0"u8);
        s.WriteByte(1); s.WriteByte(1); // version 1.1
        s.WriteByte(0); // density units: none
        WriteU16(s, 1); WriteU16(s, 1); // density
        s.WriteByte(0); s.WriteByte(0); // thumbnail
    }

    private static void WriteIccApp2(Stream s, byte[] icc)
    {
        const int maxChunk = 65519;
        int chunks = (icc.Length + maxChunk - 1) / maxChunk;
        for (int i = 0; i < chunks; i++)
        {
            int offset = i * maxChunk;
            int len = Math.Min(maxChunk, icc.Length - offset);
            s.WriteByte(0xFF); s.WriteByte(0xE2);
            WriteU16(s, (ushort)(2 + 14 + len));
            s.Write("ICC_PROFILE\0"u8);
            s.WriteByte((byte)(i + 1));
            s.WriteByte((byte)chunks);
            s.Write(icc, offset, len);
        }
    }

    private static void WriteDqt(Stream s, int tableId, byte[] qt)
    {
        s.WriteByte(0xFF); s.WriteByte(0xDB);
        WriteU16(s, 67); // 2 + 1 + 64
        s.WriteByte((byte)tableId); // precision=0 (8-bit), id
        // 按 zigzag 顺序写入
        for (int i = 0; i < 64; i++)
            s.WriteByte(qt[Zigzag[i]]);
    }

    private static void WriteSof0(Stream s, int w, int h, string chroma)
    {
        s.WriteByte(0xFF); s.WriteByte(0xC0);
        WriteU16(s, 17); // 2 + 1 + 2 + 2 + 1 + 3*3
        s.WriteByte(8); // precision
        WriteU16(s, (ushort)h);
        WriteU16(s, (ushort)w);
        s.WriteByte(3); // components

        // Y 采样因子: 4:2:0 → 2x2, 4:2:2 → 2x1, 4:4:4 → 1x1
        byte ySampling = chroma switch
        {
            "420" => 0x22, // H=2, V=2
            "422" => 0x21, // H=2, V=1
            _ => 0x11      // H=1, V=1 (4:4:4)
        };

        // Y: id=1, sampling, qt=0
        s.WriteByte(1); s.WriteByte(ySampling); s.WriteByte(0);
        // Cb: id=2, sampling=1x1, qt=1
        s.WriteByte(2); s.WriteByte(0x11); s.WriteByte(1);
        // Cr: id=3, sampling=1x1, qt=1
        s.WriteByte(3); s.WriteByte(0x11); s.WriteByte(1);
    }

    private static void WriteDht(Stream s, byte tableClass, byte[] bits, byte[] vals)
    {
        s.WriteByte(0xFF); s.WriteByte(0xC4);
        WriteU16(s, (ushort)(2 + 1 + 16 + vals.Length));
        s.WriteByte(tableClass);
        s.Write(bits);
        s.Write(vals);
    }

    private static void WriteSos(Stream s)
    {
        s.WriteByte(0xFF); s.WriteByte(0xDA);
        WriteU16(s, 12); // 2 + 1 + 3*2 + 3
        s.WriteByte(3); // components
        s.WriteByte(1); s.WriteByte(0x00); // Y: dc=0, ac=0
        s.WriteByte(2); s.WriteByte(0x11); // Cb: dc=1, ac=1
        s.WriteByte(3); s.WriteByte(0x11); // Cr: dc=1, ac=1
        s.WriteByte(0); s.WriteByte(63); s.WriteByte(0); // Ss, Se, Ah/Al
    }

    private static void WriteU16(Stream s, int v)
    { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    // ── 位写入器 ──
    private sealed class BitWriter(Stream stream)
    {
        private int _buffer;
        private int _bitCount;

        public void WriteBits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                _bitCount++;
                if (_bitCount == 8)
                {
                    stream.WriteByte((byte)_buffer);
                    if (_buffer == 0xFF) stream.WriteByte(0x00); // 字节填充
                    _buffer = 0;
                    _bitCount = 0;
                }
            }
        }

        public void WriteHuff(ushort code, byte size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((code >> i) & 1);
                _bitCount++;
                if (_bitCount == 8)
                {
                    stream.WriteByte((byte)_buffer);
                    if (_buffer == 0xFF) stream.WriteByte(0x00);
                    _buffer = 0;
                    _bitCount = 0;
                }
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                _buffer <<= (8 - _bitCount);
                stream.WriteByte((byte)_buffer);
                if ((byte)_buffer == 0xFF) stream.WriteByte(0x00);
            }
        }
    }
}
