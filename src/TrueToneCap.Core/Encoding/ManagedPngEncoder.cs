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
        using var fs = File.Create(path);
        Encode(bgra, w, h, fs, bitDepth, iccProfile, cicp);
    }

    /// <summary>快速编码 8-bit PNG（Filter=None + Fastest 压缩）。用于临时中间文件。</summary>
    public static void EncodeFast(byte[] bgra, int w, int h, string path, byte[]? iccProfile = null)
    {
        using var fs = File.Create(path);
        int rowBytes = w * 4;
        fs.Write(PngSignature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(fs, "IHDR"u8, ihdr);

        if (iccProfile is { Length: > 0 })
            WriteChunk(fs, "iCCP"u8, BuildIccpChunk(iccProfile));

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
        WriteChunk(fs, "IDAT"u8, ms.ToArray());

        WriteChunk(fs, "IEND"u8, []);
    }

    /// <summary>编码 BGRA 像素为 PNG 流。</summary>
    public static void Encode(byte[] bgra, int w, int h, Stream output,
        int bitDepth = 8, byte[]? iccProfile = null, byte[]? cicp = null)
    {
        // PNG 3.0 (ISO/IEC 15948:2023) 正式支持 10/12-bit depth
        // 存储时使用 2 字节/通道，但 IHDR.bit_depth 标记为实际位深
        int outDepth = bitDepth switch { 10 => 10, 12 => 12, >= 16 => 16, _ => 8 };
        int bytesPerChannel = outDepth > 8 ? 2 : 1;
        int channels = 4;
        int stride = w * channels * bytesPerChannel;

        output.Write(PngSignature);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;       // bit depth: 8/10/12/16
        ihdr[9] = 6;                     // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当位深 10/12 时标记实际有效位（PNG 3.0 规范要求）
        if (outDepth is 10 or 12)
        {
            byte sbitVal = (byte)outDepth;
            byte[] sbit = [sbitVal, sbitVal, sbitVal, sbitVal]; // RGBA 各通道
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP / iCCP
        if (cicp is { Length: 4 } && iccProfile is null)
            WriteChunk(output, "cICP"u8, cicp);
        if (iccProfile is { Length: > 0 })
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
    /// </summary>
    public static void Encode16(byte[] bgra16, int w, int h, string path, byte[]? cicp = null, int bitDepth = 16)
    {
        using var fs = File.Create(path);
        Encode16(bgra16, w, h, fs, cicp, bitDepth);
    }

    /// <summary>编码真 16-bit BGRA 像素为 16-bit PNG 流。IHDR 始终为 16-bit，</summary>
    public static void Encode16(byte[] bgra16, int w, int h, Stream output, byte[]? cicp = null, int bitDepth = 16)
    {
        int outDepth = bitDepth switch { 10 or 12 => 16, _ => 16 }; // IHDR 仅允许 8/16，HDR 用 16 + sBIT 标记实际位深

        // PNG Signature
        output.Write(PngSignature);

        // IHDR: 16-bit RGBA
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;        // bit depth: 16
        ihdr[9] = 6;                     // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当实际位深不足 16 时标记有效位（PNG 3.0 规范要求）
        if (bitDepth is 10 or 12)
        {
            byte sbitVal = (byte)bitDepth;
            byte[] sbit = [sbitVal, sbitVal, sbitVal, sbitVal];
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);

        // IDAT
        var rawData = BuildRawScanlines16(bgra16, w, h, bitDepth);
        var compressed = CompressDeflate(rawData);
        WriteChunk(output, "IDAT"u8, compressed);

        // IEND
        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>构建 16-bit 扫描线: 输入 BGRA16 BE → 输出 RGBA16 BE + filter byte。
    /// 存储位深始终为 16 (PNG 3.0 Table 12: color type 6 仅允许 8/16)。
    /// <param name="actualBitDepth">实际位深 (10/12/16)，用于缩放。</param></summary>
    /// <param name="actualBitDepth">实际位深 (10/12/16)，用于缩放。</param>
    private static byte[] BuildRawScanlines16(byte[] bgra16, int w, int h, int actualBitDepth = 16)
    {
        int rowBytes = w * 8; // 4 channels × 2 bytes
        var raw = new byte[h * (1 + rowBytes)];

        for (int y = 0; y < h; y++)
        {
            int rawOff = y * (1 + rowBytes);
            raw[rawOff] = 0; // Filter: None

            int srcOff = y * rowBytes;
            int dstOff = rawOff + 1;

            for (int x = 0; x < w; x++)
            {
                int si = srcOff + x * 8;
                int di = dstOff + x * 8;
                // 读取 16-bit big-endian 值
                ushort r = (ushort)((bgra16[si + 4] << 8) | bgra16[si + 5]);
                ushort g = (ushort)((bgra16[si + 2] << 8) | bgra16[si + 3]);
                ushort b = (ushort)((bgra16[si] << 8) | bgra16[si + 1]);
                ushort a = (ushort)((bgra16[si + 6] << 8) | bgra16[si + 7]);

                // 按实际位深缩放：16-bit 容器中存储 10/12-bit 值时左对齐
                if (actualBitDepth < 16)
                {
                    int shift = 16 - actualBitDepth;
                    r = (ushort)(r >> shift);
                    g = (ushort)(g >> shift);
                    b = (ushort)(b >> shift);
                    a = (ushort)(a >> shift);
                    // 然后左移到 MSB 对齐 (sBIT 标记有效位)
                    r = (ushort)(r << shift);
                    g = (ushort)(g << shift);
                    b = (ushort)(b << shift);
                    a = (ushort)(a << shift);
                }

                // 写入 big-endian 16-bit 容器
                raw[di] = (byte)(r >> 8); raw[di + 1] = (byte)r;
                raw[di + 2] = (byte)(g >> 8); raw[di + 3] = (byte)g;
                raw[di + 4] = (byte)(b >> 8); raw[di + 5] = (byte)b;
                raw[di + 6] = (byte)(a >> 8); raw[di + 7] = (byte)a;
            }
        }
        return raw;
    }

    /// <summary>构建原始扫描线数据（含 filter byte）— 自适应滤波优化压缩率。
    /// SDR 路径固定 8-bit。</summary>
    private static byte[] BuildRawScanlines(byte[] bgra, int w, int h, int outDepth)
    {
        int channels = 4;
        int bytesPerChannel = 1; // SDR 路径固定 8-bit (PNG 3.0 Table 12: color type 6 仅允许 8/16)
        int bpp = channels * bytesPerChannel;
        int rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];

        // 临时行缓冲：当前行 RGBA + 上一行 RGBA
        var curRow = new byte[rowBytes];
        var prevRow = new byte[rowBytes];
        var filtered = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            int srcOff = y * w * 4;

            // BGRA → RGBA 转换到 curRow
            if (outDepth == 8)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = x * 4;
                    curRow[di] = bgra[si + 2];     // R
                    curRow[di + 1] = bgra[si + 1]; // G
                    curRow[di + 2] = bgra[si];     // B
                    curRow[di + 3] = bgra[si + 3]; // A
                }
            }
            else
            {
                // outDepth > 8: 16-bit 路径 — 不应当在此路径中 (HDR 使用 Encode16)
                // 作为安全回退，直接拷贝 8-bit 值到 16-bit 容器
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = x * 8;
                    Write16(curRow, di, (ushort)(bgra[si + 2] * 257));
                    Write16(curRow, di + 2, (ushort)(bgra[si + 1] * 257));
                    Write16(curRow, di + 4, (ushort)(bgra[si] * 257));
                    Write16(curRow, di + 6, (ushort)(bgra[si + 3] * 257));
                }
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
                    // 将 byte 视为有符号偏移计算绝对值
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
