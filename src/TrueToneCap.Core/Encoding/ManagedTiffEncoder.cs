// TrueToneCap.Core/Encoding/ManagedTiffEncoder.cs
// 托管 TIFF 编码器 — 零依赖，支持 8-bit 和 16-bit BGRA

using System.IO.Compression;

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 TIFF 编码器 — 8/16-bit BGRA, Deflate 压缩, ICC 嵌入。</summary>
public static class ManagedTiffEncoder
{
    /// <summary>把 BGRA 像素编码为 TIFF 文件。</summary>
    /// <param name="inputBitDepth">输入像素位深（8 或 16）；null 时按数组长度推断。详见 Stream 重载说明。</param>
    public static void Encode(byte[] bgra, int w, int h, string path, int bitDepth = 8,
        byte[]? iccProfile = null, int? inputBitDepth = null)
    {
        using var fs = File.Create(path);
        Encode(bgra, w, h, fs, bitDepth, iccProfile, inputBitDepth);
    }

    /// <summary>把 BGRA 像素编码为 TIFF 写入流。</summary>
    /// <param name="bgra">像素数据。8-bit 时每像素 4 字节；16-bit 时每像素 8 字节（通道顺序 BGRA、每通道大端）。</param>
    /// <param name="w">图像宽度。</param>
    /// <param name="h">图像高度。</param>
    /// <param name="output">目标流。</param>
    /// <param name="bitDepth">输出位深（8 或 16）。</param>
    /// <param name="iccProfile">可选 ICC 配置文件（长度需 &gt; 128 才会写入）。</param>
    /// <param name="inputBitDepth">
    /// 输入像素位深（8 或 16）。传 null 时按数组长度推断（兼容旧调用方）。
    /// <para>
    /// ⚠ 建议显式传入：按长度推断在"8-bit 数组 + 请求 16-bit 输出"这类组合下虽碰巧正确，
    /// 但语义模糊，一旦调用方复用缓冲（数组容量大于实际数据）就会误判为 16-bit。
    /// </para>
    /// </param>
    public static void Encode(byte[] bgra, int w, int h, Stream output, int bitDepth = 8,
        byte[]? iccProfile = null, int? inputBitDepth = null)
    {
        long pixelCount = (long)w * h;
        if (bgra is null || w <= 0 || h <= 0)
            throw new ArgumentException($"TIFF 参数无效: w={w} h={h}");

        // 输入位深：优先用显式参数，未指定时回退到按长度推断（兼容旧调用）
        bool srcIs16 = inputBitDepth.HasValue
            ? inputBitDepth.Value > 8
            : bgra.Length >= pixelCount * 8;

        long required = srcIs16 ? pixelCount * 8 : pixelCount * 4;
        if (bgra.Length < required)
            throw new ArgumentException(
                $"TIFF 像素数据长度不足: len={bgra.Length} 需要={required} ({(srcIs16 ? "16" : "8")}-bit)");

        using var writer = new BinaryWriter(output);

        // TIFF header: little-endian
        writer.Write((byte)0x49); writer.Write((byte)0x49); // II
        writer.Write((ushort)42);                           // TIFF magic
        writer.Write((uint)8);                              // IFD offset (after header)

        // ── 值区布局（IFD 之后存放"4 字节放不下"的值）──
        // ⚠ 必须先规划好整片值区，再写像素数据。
        // 旧实现把 pixelDataStart 直接取为"写完 BitsPerSample 之后的位置"，
        // 而 StripOffsets/StripByteCounts/XRes/YRes 的值区都排在其后 →
        // 回填时用 Seek 覆盖了压缩数据前 24 字节 → 输出的 TIFF 解压后前若干像素损坏。
        bool hasIcc = iccProfile is { Length: > 128 };
        // ⚠ 必须与下方实际写入的条目数严格一致：256/257/258/259/262/273/277/278/279/282/283/296 = 12 个。
        // 之前写死 10，少算 2 个条目 = 24 字节 → 解析器只读前 10 条，随后把第 11 条
        // 的前 4 字节当作 next-IFD 偏移，且值区起点整体前移 24 字节 → 标签值与像素数据全部错位。
        const int baseTagCount = 12;
        int tagCount = baseTagCount + (hasIcc ? 1 : 0);
        long valueAreaStart = 8 + 2 + tagCount * 12 + 4;   // IFD 结束后即值区起点
        uint bpsOffset = (uint)valueAreaStart;             // BitsPerSample: 4×SHORT = 8
        uint stripOffsetOffset = (uint)(valueAreaStart + 8);   // StripOffsets: LONG = 4
        uint byteCountOffset = stripOffsetOffset + 4;          // StripByteCounts: LONG = 4
        uint xresOffset = byteCountOffset + 4;                 // XResolution: RATIONAL = 8
        uint yresOffset = xresOffset + 8;                      // YResolution: RATIONAL = 8
        long iccOffset = yresOffset + 8;                       // ICCProfile
        long expectedPixelDataStart = hasIcc ? iccOffset + iccProfile!.Length : iccOffset;

        // ── IFD entries ──
        // ⚠ 每个 IFD 条目恒为 12 字节（tag2 + type2 + count4 + value4）
        writer.Write((ushort)tagCount);

        // Tag 256: ImageWidth (LONG count=1 → 内联 4 字节)
        writer.Write((ushort)256); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)w);

        // Tag 257: ImageLength
        writer.Write((ushort)257); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)h);

        // Tag 258: BitsPerSample (SHORT × 4 = 8 字节 > 4 → 写偏移)
        writer.Write((ushort)258); writer.Write((ushort)3); writer.Write((uint)4);
        writer.Write(bpsOffset);

        // Tag 259: Compression (8 = Adobe Deflate)
        WriteShortEntry(writer, 259, 8);

        // Tag 262: PhotometricInterpretation (2 = RGB)
        WriteShortEntry(writer, 262, 2);

        // Tag 273: StripOffsets (值存于值区，稍后回填真实像素数据偏移)
        // ⚠ BitsPerSample 值区恒为 4×ushort = 8 字节，与 bitDepth 无关。
        // 之前写成 4*bitDepth/8，8-bit 输出时算成 4 → 偏移错误，破坏后续结构。
        writer.Write((ushort)273); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write(stripOffsetOffset);

        // Tag 277: SamplesPerPixel (4 = RGBA)
        WriteShortEntry(writer, 277, 4);

        // Tag 278: RowsPerStrip
        writer.Write((ushort)278); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)h);

        // Tag 279: StripByteCounts (值存于值区，稍后回填)
        writer.Write((ushort)279); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write(byteCountOffset);

        // Tag 282: XResolution (RATIONAL = 8 字节 > 4 → 写偏移)
        writer.Write((ushort)282); writer.Write((ushort)5); writer.Write((uint)1);
        writer.Write(xresOffset);

        // Tag 283: YResolution
        writer.Write((ushort)283); writer.Write((ushort)5); writer.Write((uint)1);
        writer.Write(yresOffset);

        // Tag 296: ResolutionUnit (2 = inch)
        WriteShortEntry(writer, 296, 2);

        // ICC Profile tag (if present)
        if (hasIcc)
        {
            // Tag 34675: ICCProfile (UNDEFINED)
            writer.Write((ushort)34675); writer.Write((ushort)7); writer.Write((uint)iccProfile!.Length);
            writer.Write((uint)iccOffset);
        }

        // Next IFD offset (0 = no more IFDs)
        writer.Write((uint)0);

        // ── 值区 ──
        // ⚠ 必须先占满整片值区再写像素数据：否则后续 Seek 回填会覆盖压缩数据。
        // BitsPerSample (4 × SHORT = 8 字节)
        for (int i = 0; i < 4; i++)
            writer.Write((ushort)bitDepth);

        // 占位（随后 Seek 回填）：StripOffsets / StripByteCounts / XRes / YRes
        writer.Write((uint)0);                          // StripOffsets
        writer.Write((uint)0);                          // StripByteCounts
        writer.Write((uint)0); writer.Write((uint)0);   // XResolution 72/1
        writer.Write((uint)0); writer.Write((uint)0);   // YResolution 72/1
        if (hasIcc) writer.Write(iccProfile!);          // ICCProfile

        // ── Strip pixel data (Deflate compressed) ──
        // 此时流位置必须等于预先规划的值区终点，否则说明值区布局与写入顺序不一致
        long pixelDataStart = writer.BaseStream.Position;
        System.Diagnostics.Debug.Assert(pixelDataStart == expectedPixelDataStart,
            $"TIFF 值区布局不一致: 实际={pixelDataStart} 预期={expectedPixelDataStart}");

        var raw = ConvertToRgba(bgra, (int)pixelCount, srcIs16, bitDepth);

        // Deflate compress (SmallestSize 比 Optimal 压缩率高 3-8%，适合截图保存)
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize))
            ds.Write(raw, 0, raw.Length);
        var compressed = ms.ToArray();
        writer.Write(compressed);

        // ── 回填 strip offset 与 byte count ──
        writer.BaseStream.Seek(stripOffsetOffset, SeekOrigin.Begin);
        writer.Write((uint)pixelDataStart);
        writer.BaseStream.Seek(byteCountOffset, SeekOrigin.Begin);
        writer.Write((uint)compressed.Length);

        // ── XResolution / YResolution (72 DPI) ──
        writer.BaseStream.Seek(xresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1); // 72/1
        writer.BaseStream.Seek(yresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1);

        // ICC 已在值区占位阶段写入，此处无需再处理
    }

    /// <summary>写入 SHORT 类型、count=1 的 IFD 条目。
    /// ⚠ TIFF 每个 IFD 条目恒为 12 字节，value 域固定占 4 字节。
    /// SHORT 值只占前 2 字节（II 小端 → 低地址），剩余 2 字节必须是 padding。
    /// 之前只写 2 字节，每个短条目少 2 字节 → 其后所有条目被解析器错位读取 → 文件非法。</summary>
    private static void WriteShortEntry(BinaryWriter w, ushort tag, ushort value)
    {
        w.Write(tag);           // tag   (2)
        w.Write((ushort)3);     // type  (2) = SHORT
        w.Write((uint)1);       // count (4)
        w.Write(value);         // 值占前 2 字节
        w.Write((ushort)0);     // 2 字节 padding → 条目共 12 字节
    }

    /// <summary>BGRA → RGBA，并按需做 8/16-bit 转换。
    /// 16-bit 输入：每像素 8 字节、通道顺序 BGRA、每通道大端（与 PNG 16-bit 中转格式一致，
    /// 见 FormatHelper.Rgba16ToBgra16Bytes）。
    /// 输出恒为 TIFF 的 RGBA 顺序 + 小端（TIFF header 为 II）。
    /// ⚠ 之前 16-bit 路径按 si = i*4 步进（把 16-bit 数据当 8-bit 读）、Alpha 未 *257
    /// → 通道错位且整图近乎全透明。</summary>
    private static byte[] ConvertToRgba(byte[] bgra, int pixelCount, bool srcIs16, int bitDepth)
    {
        bool outIs16 = bitDepth > 8;
        int outStride = outIs16 ? 8 : 4;
        var raw = new byte[pixelCount * outStride];

        for (int i = 0; i < pixelCount; i++)
        {
            ushort r, g, b, a;
            if (srcIs16)
            {
                int si = i * 8;
                b = ReadU16BE(bgra, si);
                g = ReadU16BE(bgra, si + 2);
                r = ReadU16BE(bgra, si + 4);
                a = ReadU16BE(bgra, si + 6);
            }
            else
            {
                int si = i * 4;
                b = (ushort)(bgra[si] * 257);      // 8→16 扩展: v * 65535 / 255
                g = (ushort)(bgra[si + 1] * 257);
                r = (ushort)(bgra[si + 2] * 257);
                a = (ushort)(bgra[si + 3] * 257);
            }

            int di = i * outStride;
            if (outIs16)
            {
                WriteU16LE(raw, di, r);
                WriteU16LE(raw, di + 2, g);
                WriteU16LE(raw, di + 4, b);
                WriteU16LE(raw, di + 6, a);
            }
            else
            {
                raw[di] = (byte)(r >> 8);
                raw[di + 1] = (byte)(g >> 8);
                raw[di + 2] = (byte)(b >> 8);
                raw[di + 3] = (byte)(a >> 8);
            }
        }
        return raw;
    }

    private static ushort ReadU16BE(byte[] buf, int off) => (ushort)((buf[off] << 8) | buf[off + 1]);

    private static void WriteU16LE(byte[] buf, int off, ushort v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)(v >> 8);
    }
}
