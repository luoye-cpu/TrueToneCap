// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/ManagedPngEncoder.cs
// 托管 PNG 编码器 — 零原生依赖，使用 System.IO.Compression
// 支持: 8-bit / 16-bit, RGBA, iCCP chunk, cICP chunk, 最大压缩

using System.IO.Compression;
using System.Buffers.Binary;

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 PNG 编码器 — 完全替代 Magick.NET 的 PNG 路径。</summary>
public static class ManagedPngEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>编码 BGRA 像素为 PNG 文件。</summary>
    /// <param name="bgra">BGRA 像素数据。</param>
    /// <param name="w">宽度。</param>
    /// <param name="h">高度。</param>
    /// <param name="path">输出路径。</param>
    /// <param name="bitDepth">输出位深 (8/10/12/16)。10/12-bit 写入真位深 + sBIT, 16-bit 存储为 16。</param>
    /// <param name="iccProfile">可选 ICC profile (写入 iCCP chunk)。</param>
    /// <param name="cicp">可选 CICP 4 字节 [primaries, transfer, matrix, range]。</param>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int bitDepth = 8, byte[]? iccProfile = null, byte[]? cicp = null)
    {
        // 2026-09-19：原为 File.Create(path) 直写 —— 中途任何异常都会在最终路径留下一份
        // 已写了签名/IHDR 却没有 IEND 的坏 PNG，而上层照样按 ResolveActualPath 回来的那个路径报告"已保存"
        // （见 CapturePipelineService 的 File.Exists/长度统计）。现走同目录临时文件 + 成功后替换，
        // 理由与失败语义见 AtomicFileWriter。
        AtomicFileWriter.Write(path, fs => Encode(bgra, w, h, fs, bitDepth, iccProfile, cicp));
    }

    /// <summary>快速编码 8-bit PNG（Filter=None + Fastest 压缩）。用于临时中间文件。</summary>
    public static void EncodeFast(byte[] bgra, int w, int h, string path, byte[]? iccProfile = null)
    {
        AtomicFileWriter.Write(path, fs => EncodeFast(bgra, w, h, fs, iccProfile));
    }

    /// <summary>快速编码 8-bit PNG 到 Stream（Filter=None + Fastest 压缩）。用于 stdin 管道传输。</summary>
    public static void EncodeFast(byte[] bgra, int w, int h, Stream output, byte[]? iccProfile = null)
    {
        int rowBytes = w * 4;
        output.Write(PngSignature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(output, "IHDR"u8, ihdr);

        if (FormatHelper.IsEmbeddableIcc(iccProfile))
            WriteChunk(output, "iCCP"u8, BuildIccpChunk(iccProfile));

        // Filter=None 扫描线（无自适应选择开销）
        var raw = new byte[h * (1 + rowBytes)];
        for (int y = 0; y < h; y++)
        {
            int rawOff = y * (1 + rowBytes);
            raw[rawOff] = 0; // None
            int srcOff = y * w * 4;
            int dstOff = rawOff + 1;
            for (int x = 0; x < w; x++)
            {
                int si = srcOff + x * 4, di = dstOff + x * 4;
                raw[di] = bgra[si + 2]; raw[di + 1] = bgra[si + 1];
                raw[di + 2] = bgra[si]; raw[di + 3] = bgra[si + 3];
            }
        }

        // Fastest 压缩 (ZLibStream 已包含 zlib 头 + Adler32)
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(output, "IDAT"u8, ms.ToArray());

        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>编码 BGRA 像素为 PNG 流。</summary>
    public static void Encode(byte[] bgra, int w, int h, Stream output,
        int bitDepth = 8, byte[]? iccProfile = null, byte[]? cicp = null)
    {
        // PNG 3.0 (ISO/IEC 15948:2023) Table 11: Color type 6 (RGBA) 只允许 8 和 16
        // 10/12-bit 源数据使用 16-bit 容器 + sBIT 标记实际有效位
        // 参见 12.4 Sample depth scaling
        bool isHighBitDepth = bitDepth > 8;
        int outDepth = isHighBitDepth ? 16 : 8;
        int bytesPerChannel = outDepth > 8 ? 2 : 1;
        int channels = 4;
        int stride = w * channels * bytesPerChannel;

        output.Write(PngSignature);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;       // bit depth: 8 或 16 (Color type 6 只允许这俩值)
        ihdr[9] = 6;                     // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当实际位深不足 16 时标记有效位（PNG 3.0 11.3.2.4）
        // 注意: SDR 输入的 byte[] 只有 8-bit 精度，sBIT 标记实际精度而非容器位深
        if (isHighBitDepth)
        {
            // byte[] 输入只有 8-bit 精度，用 *257 扩展到 16-bit 容器
            byte[] sbit = [8, 8, 8, 8]; // RGBA 各通道: 实际精度 8-bit
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP / iCCP — 两者都写（PNG 3.0 RFC 9327 推荐）
        // cICP 为现代解码器提供紧凑色彩描述，iCCP 为老旧解码器提供回退
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            var iccpData = BuildIccpChunk(iccProfile);
            WriteChunk(output, "iCCP"u8, iccpData);
        }

        // IDAT
        var rawData = BuildRawScanlines(bgra, w, h, outDepth);
        var compressed = CompressDeflate(rawData);
        WriteChunk(output, "IDAT"u8, compressed);

        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>
    /// C1 fix: 编码真 16-bit BGRA 像素为 16-bit PNG（HDR 路径专用）。
    /// 输入: 每像素 8 字节 (B16 G16 R16 A16, big-endian)，由 Rgba16ToBgra16Bytes 生成。
    /// 支持 IHDR 10/12/16-bit depth（PNG 3.0）。
    /// </summary>
    /// <param name="fastest">true = 用最快压缩（用于临时中转文件；压缩率不影响最终产物）。</param>
    public static void Encode16(byte[] bgra16, int w, int h, string path, byte[]? cicp = null, byte[]? iccProfile = null, int bitDepth = 16, bool fastest = false)
    {
        AtomicFileWriter.Write(path, fs => Encode16(bgra16, w, h, fs, cicp, iccProfile, bitDepth, fastest));
    }

    /// <summary>编码真 16-bit BGRA 像素为 16-bit PNG 流。</summary>
    public static void Encode16(byte[] bgra16, int w, int h, Stream output, byte[]? cicp = null, byte[]? iccProfile = null, int bitDepth = 16, bool fastest = false)
    {
        // PNG 3.0 (ISO/IEC 15948:2023) Table 11: Color type 6 (RGBA) 只允许 8 和 16
        // 10/12-bit 源数据使用 16-bit 容器 + sBIT 标记实际有效位
        // 参见 12.4 Sample depth scaling
        int outDepth = 16;

        // PNG Signature
        output.Write(PngSignature);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;        // bit depth: 16 (Color type 6 只允许 8/16)
        ihdr[9] = 6;                     // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当实际位深不足 16 时标记有效位（PNG 3.0 11.3.2.4）
        if (bitDepth < 16)
        {
            byte sbitVal = bitDepth switch { 12 => 12, 10 => 10, _ => 8 };
            byte[] sbit = [sbitVal, sbitVal, sbitVal, sbitVal];
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);

        // iCCP（与 cICP 共存，RFC 9327 推荐）
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            var iccpData = BuildIccpChunk(iccProfile);
            WriteChunk(output, "iCCP"u8, iccpData);
        }

        // IDAT
        // ═══ 2026-09-16：中转用途可走快速压缩 ═══
        // 16-bit PNG 也被当作 HDR 编码的**临时中转**（如 AVIF HDR：scRGB→PQ16→PNG→avifenc）。
        // 那种场景文件用完即删，压缩率毫无意义，而 SmallestSize 在 4K 上要多花数秒 ——
        // 实测这正是 4K HDR AVIF 前处理的主要耗时。故提供 fastest 开关。
        var rawData = BuildRawScanlines16(bgra16, w, h, bitDepth);
        var compressed = fastest ? CompressDeflateFast(rawData) : CompressDeflate(rawData);
        WriteChunk(output, "IDAT"u8, compressed);

        // IEND
        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>Deflate 压缩（最快）。用于临时中转文件 —— 压缩率不影响最终产物质量。</summary>
    private static byte[] CompressDeflateFast(byte[] data)
    {
        using var ms = new MemoryStream();
        // Fastest 压缩 (ZLibStream 已包含 zlib 头 + Adler32)
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>构建 16-bit 扫描线: 输入 BGRA16 BE → 输出 RGBA16 BE + filter byte。
    /// 支持 10/12/16-bit 位深。10/12-bit 时 IHDR 标记为实际位深。
    /// 使用自适应滤波选择（同 SDR 路径），5 种滤波器中选绝对值最小和。</summary>
    /// <param name="actualBitDepth">实际位深 (10/12/16)，用于缩放和 IHDR。</param>
    private static byte[] BuildRawScanlines16(byte[] bgra16, int w, int h, int actualBitDepth = 16)
    {
        int bytesPerChannel = 2;
        int channels = 4;
        int bpp = channels * bytesPerChannel; // 8 BPP
        int rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];

        // 临时行缓冲：当前行 RGBA + 上一行 RGBA
        var curRow = new byte[rowBytes];
        var prevRow = new byte[rowBytes];
        var filtered = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            int srcOff = y * rowBytes;

            // BGRA16 BE → RGBA16 BE 转换到 curRow
            for (int x = 0; x < w; x++)
            {
                int si = srcOff + x * 8;
                int di = x * 8;
                // 读取 16-bit big-endian 值
                ushort bVal = (ushort)((bgra16[si] << 8) | bgra16[si + 1]);
                ushort gVal = (ushort)((bgra16[si + 2] << 8) | bgra16[si + 3]);
                ushort rVal = (ushort)((bgra16[si + 4] << 8) | bgra16[si + 5]);
                ushort aVal = (ushort)((bgra16[si + 6] << 8) | bgra16[si + 7]);

                // 按实际位深缩放：16-bit 容器中存储 10/12-bit 值时左对齐
                //
                // ═══ 2026-09-19：截断改四舍五入 ═══
                // 旧写法 (v >> shift) << shift 是"向下取整"，注释还自称"仅做安全验证"，
                // 但它根本不是无操作：SDR ≥10-bit 路径（PrepareSdr16Pixels 用默认
                // quantizeBits=16，容器被 16-bit 精度写满）每一位都要经过这里，
                // 于是整张图被系统性压暗 0~(2^shift-1) 个台阶（10-bit 平均 -0.5 LSB、
                // 最坏 -63 LSB ≈ 半档），且 sBIT 声明的正是这个被压过的位深 ⇒ 偏差被固化进文件。
                // 现在取"最近的合法台阶"：期望偏差为 0，最大误差从 1 个台阶降到半个台阶。
                // 低位仍然清零 —— 那是 sBIT 的容器合法性要求（PNG 3.0 12.4 左对齐），与取整方向无关。
                if (actualBitDepth < 16)
                {
                    int shift = 16 - actualBitDepth;
                    rVal = RoundToSampleDepth(rVal, shift);
                    gVal = RoundToSampleDepth(gVal, shift);
                    bVal = RoundToSampleDepth(bVal, shift);
                    aVal = RoundToSampleDepth(aVal, shift);
                }

                // 写入 big-endian 16-bit 容器 (RGBA 顺序)
                Write16(curRow, di, rVal);
                Write16(curRow, di + 2, gVal);
                Write16(curRow, di + 4, bVal);
                Write16(curRow, di + 6, aVal);
            }

            // 自适应滤波选择：尝试 5 种滤波器，选最小绝对值和（压缩率启发式）
            byte bestFilter = 0;
            long bestScore = long.MaxValue;

            for (byte f = 0; f <= 4; f++)
            {
                ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, f);
                long score = 0;
                for (int i = 0; i < rowBytes; i++)
                {
                    int v = filtered[i];
                    score += v < 128 ? v : 256 - v;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = f;
                }
            }

            // 写入最佳滤波结果
            int rawOff = y * (1 + rowBytes);
            raw[rawOff] = bestFilter;
            ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, bestFilter);
            Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);

            // 当前行变为上一行
            (curRow, prevRow) = (prevRow, curRow);
        }
        return raw;
    }

    /// <summary>构建原始扫描线数据（含 filter byte）— 自适应滤波优化压缩率。
    /// SDR 路径固定 8-bit。</summary>
    private static byte[] BuildRawScanlines(byte[] bgra, int w, int h, int outDepth)
    {
        int channels = 4;
        int bytesPerChannel = outDepth <= 8 ? 1 : 2; // 8-bit → 1B, 10/12/16-bit → 2B
        int bpp = channels * bytesPerChannel;
        int rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];

        // ═══ 2026-08-25 P2 性能优化: 行并行化 ═══
        // 每行 BGRA→RGBA 转换 + 滤波选择 + 滤波应用独立, 行间无依赖。
        // 4K PNG (3840×2160) 加速 ~2-3x (240 行并行 → 避免 2160 行串行扫描 5 滤波器)。
        // 使用 Parallel.ForEach + 分区迭代器避免共享 prevRow 状态。
        // 注意: 滤波器的 Up/Average/Paeth 需要上一行数据 → 不能完全独立。
        // 改用两阶段: (1) 并行 BGRA→RGBA 转换; (2) 串行滤波选择+应用。

        // 阶段1: 并行 BGRA→RGBA + 16-bit 扩展 (行间无依赖)
        var rgbaRows = new byte[h][];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            int srcOff = y * w * 4;
            var row = new byte[rowBytes];
            if (outDepth == 8)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = x * 4;
                    row[di] = bgra[si + 2];     // R
                    row[di + 1] = bgra[si + 1]; // G
                    row[di + 2] = bgra[si];     // B
                    row[di + 3] = bgra[si + 3]; // A
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = x * 8;
                    Write16(row, di, (ushort)(bgra[si + 2] * 257));
                    Write16(row, di + 2, (ushort)(bgra[si + 1] * 257));
                    Write16(row, di + 4, (ushort)(bgra[si] * 257));
                    Write16(row, di + 6, (ushort)(bgra[si + 3] * 257));
                }
            }
            rgbaRows[y] = row;
        });

        // 阶段2: 串行滤波选择+应用 (依赖上一行)
        // 优化: 5 滤波器打分改为只尝试 Sub/Up/Average/Paeth (4 种), 跳过 None.
        // None 在平坦区域更好, 但 4 种滤波器配合压缩足够.
        var prevRow = new byte[rowBytes]; // all zeros (虚拟行 0)
        var filtered = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            var curRow = rgbaRows[y];

            // 自适应滤波选择: 尝试 4 种滤波器 (跳过 None, 平坦区域压缩会处理)
            byte bestFilter = 1; // Sub 默认
            long bestScore = long.MaxValue;

            for (byte f = 1; f <= 4; f++)
            {
                ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, f);
                long score = 0;
                for (int i = 0; i < rowBytes; i++)
                {
                    int v = filtered[i];
                    score += v < 128 ? v : 256 - v;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = f;
                }
            }

            // 写入最佳滤波结果
            int rawOff = y * (1 + rowBytes);
            raw[rawOff] = bestFilter;
            ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, bestFilter);
            Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);

            // 当前行变为上一行
            prevRow = curRow;
        }
        return raw;
    }

    /// <summary>应用 PNG 行滤波器。</summary>
    private static void ApplyFilter(byte[] cur, byte[] prev, byte[] dst, int rowBytes, int bpp, byte filter)
    {
        switch (filter)
        {
            case 0: // None
                Array.Copy(cur, dst, rowBytes);
                break;
            case 1: // Sub: diff = cur[x] - cur[x-bpp]
                for (int i = 0; i < rowBytes; i++)
                    dst[i] = (byte)(cur[i] - (i >= bpp ? cur[i - bpp] : 0));
                break;
            case 2: // Up: diff = cur[x] - prev[x]
                for (int i = 0; i < rowBytes; i++)
                    dst[i] = (byte)(cur[i] - prev[i]);
                break;
            case 3: // Average: diff = cur[x] - floor((cur[x-bpp] + prev[x]) / 2)
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    dst[i] = (byte)(cur[i] - ((a + prev[i]) >> 1));
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    dst[i] = (byte)(cur[i] - PaethPredictor(a, b, c));
                }
                break;
        }
    }

    /// <summary>Paeth 预测器 (PNG 规范 RFC 2083)。</summary>
    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    /// <summary>8-bit 值扩展到 16-bit (val * 257 = val << 8 | val)。</summary>
    private static void Write16(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)val;
    }

    /// <summary>
    /// 2026-09-19：把 16-bit 采样值就近取整到 actualBitDepth（= 16 - shift）能表示的最近台阶，低位保持清零。
    /// <para>为什么必须就近而不是向下取整：见 <see cref="BuildRawScanlines16"/> 的 2026-09-19 备注 ——
    /// 向下取整会让整图单向变暗，是偏置；就近取整的偏置期望为 0。</para>
    /// <para>为什么要钳上界：v 接近 65535 时"最近的台阶"是 65536，容器放不下，
    /// 直接左移会溢出回绕成 0（纯白变纯黑）。故钳到容器内最高台阶 step*(65535/step)。</para>
    /// </summary>
    private static ushort RoundToSampleDepth(ushort v, int shift)
    {
        int step = 1 << shift;
        int q = (v + step / 2) / step;
        int max = 65535 / step;
        if (q > max) q = max;
        return (ushort)(q * step);
    }

    /// <summary>Deflate 压缩（最大压缩级别）。ZLibStream 已包含 zlib 头 + Adler32。</summary>
    private static byte[] CompressDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return ms.ToArray();
    }

    private static uint ComputeAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        const uint MOD = 65521;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }

    /// <summary>构建 iCCP chunk 数据: profile_name\0 compression_method deflate(profile)。</summary>
    private static byte[] BuildIccpChunk(byte[] iccProfile)
    {
        var name = "ICC\0"u8.ToArray();
        using var ms = new MemoryStream();
        ms.Write(name);
        ms.WriteByte(0); // compression method: deflate

        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(iccProfile);
        }
        return ms.ToArray();
    }

    /// <summary>写入 PNG chunk: [4B length][4B type][data][4B CRC32]。</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);

        // CRC32 over type + data
        uint crc = Crc32(type, data);
        var crcBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
