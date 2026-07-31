// TrueToneCap.Core/Encoding/ManagedTiffEncoder.cs
// 托管 TIFF 编码器 — 零依赖，支持 8-bit 和 16-bit BGRA

using System.IO.Compression;

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 TIFF 编码器 — 8/16-bit BGRA, Deflate 压缩, ICC 嵌入。</summary>
public static class ManagedTiffEncoder
{
    public static void Encode(byte[] bgra, int w, int h, string path, int bitDepth = 8, byte[]? iccProfile = null)
    {
        using var fs = File.Create(path);
        Encode(bgra, w, h, fs, bitDepth, iccProfile);
    }

    public static void Encode(byte[] bgra, int w, int h, Stream output, int bitDepth = 8, byte[]? iccProfile = null)
    {
        using var writer = new BinaryWriter(output);

        // TIFF header: little-endian
        writer.Write((byte)0x49); writer.Write((byte)0x49); // II
        writer.Write((ushort)42);                           // TIFF magic
        writer.Write((uint)8);                               // IFD offset (after header)

        // ── IFD entries ──
        // Count tags
        int tagCount = 10 + (iccProfile is { Length: > 128 } ? 1 : 0);
        writer.Write((ushort)tagCount);

        // Tag 256: ImageWidth
        writer.Write((ushort)256); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)w);

        // Tag 257: ImageLength
        writer.Write((ushort)257); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)h);

        // Tag 258: BitsPerSample (每通道位深)
        writer.Write((ushort)258); writer.Write((ushort)3); writer.Write((uint)4);
        uint bpsOffset = (uint)(8 + 2 + tagCount * 12 + 4);
        writer.Write(bpsOffset);

        // Tag 259: Compression (8=Deflate)
        writer.Write((ushort)259); writer.Write((ushort)3); writer.Write((uint)1);
        writer.Write((ushort)8);

        // Tag 262: PhotometricInterpretation (2=RGB)
        writer.Write((ushort)262); writer.Write((ushort)3); writer.Write((uint)1);
        writer.Write((ushort)2);

        // Tag 273: StripOffsets
        writer.Write((ushort)273); writer.Write((ushort)4); writer.Write((uint)1);
        uint stripOffsetOffset = (uint)(8 + 2 + tagCount * 12 + 4 + 4 * bitDepth / 8);
        writer.Write(stripOffsetOffset);

        // Tag 277: SamplesPerPixel (4=RGBA)
        writer.Write((ushort)277); writer.Write((ushort)3); writer.Write((uint)1);
        writer.Write((ushort)4);

        // Tag 278: RowsPerStrip
        writer.Write((ushort)278); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)h);

        // Tag 279: StripByteCounts
        writer.Write((ushort)279); writer.Write((ushort)4); writer.Write((uint)1);
        uint byteCountOffset = stripOffsetOffset + 4;
        writer.Write(byteCountOffset);

        // Tag 282: XResolution
        writer.Write((ushort)282); writer.Write((ushort)5); writer.Write((uint)1);
        uint xresOffset = byteCountOffset + 4;
        writer.Write(xresOffset);

        // Tag 283: YResolution
        writer.Write((ushort)283); writer.Write((ushort)5); writer.Write((uint)1);
        uint yresOffset = xresOffset + 8;
        writer.Write(yresOffset);

        // Tag 296: ResolutionUnit (2=inch)
        writer.Write((ushort)296); writer.Write((ushort)3); writer.Write((uint)1);
        writer.Write((ushort)2);

        // ICC Profile tag (if present)
        if (iccProfile is { Length: > 128 })
        {
            // Tag 34675: ICCProfile
            writer.Write((ushort)34675); writer.Write((ushort)7); writer.Write((uint)iccProfile.Length);
            uint iccOffset = yresOffset + 8;
            writer.Write(iccOffset);
        }

        // Next IFD offset (0 = no more IFDs)
        writer.Write((uint)0);

        // ── BitsPerSample values ──
        for (int i = 0; i < 4; i++)
            writer.Write((ushort)bitDepth);

        // ── Strip pixel data (Deflate compressed) ──
        long pixelDataStart = writer.BaseStream.Position;

        // Convert BGRA byte → RGBA based on bit depth
        int pixelCount = w * h;
        byte[] raw;
        if (bitDepth > 8)
        {
            // 16-bit: 8-bit BGRA → 16-bit RGBA big-endian (scaled *257)
            raw = new byte[pixelCount * 8];
            for (int i = 0; i < pixelCount; i++)
            {
                int si = i * 4, di = i * 8;
                raw[di] = (byte)(bgra[si + 2] * 257 >> 8);     // R high
                raw[di + 1] = (byte)(bgra[si + 2] * 257);       // R low
                raw[di + 2] = (byte)(bgra[si + 1] * 257 >> 8); // G high
                raw[di + 3] = (byte)(bgra[si + 1] * 257);       // G low
                raw[di + 4] = (byte)(bgra[si] * 257 >> 8);      // B high
                raw[di + 5] = (byte)(bgra[si] * 257);            // B low
                raw[di + 6] = (byte)(bgra[si + 3]);              // A high
                raw[di + 7] = 0;                                 // A low
            }
        }
        else
        {
            // 8-bit: BGRA → RGBA
            raw = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                int si = i * 4, di = i * 4;
                raw[di] = bgra[si + 2];     // R
                raw[di + 1] = bgra[si + 1]; // G
                raw[di + 2] = bgra[si];     // B
                raw[di + 3] = bgra[si + 3]; // A
            }
        }

        // Deflate compress
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal))
            ds.Write(raw, 0, raw.Length);
        var compressed = ms.ToArray();
        writer.Write(compressed);

        // ── Write strip offset and byte count ──
        long endPos = writer.BaseStream.Position;
        writer.BaseStream.Seek(stripOffsetOffset, SeekOrigin.Begin);
        writer.Write((uint)pixelDataStart);
        writer.BaseStream.Seek(byteCountOffset, SeekOrigin.Begin);
        writer.Write((uint)compressed.Length);

        // ── XResolution / YResolution (72 DPI) ──
        writer.BaseStream.Seek(xresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1); // 72/1
        writer.BaseStream.Seek(yresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1);

        // ── ICC Profile ──
        if (iccProfile is { Length: > 128 })
        {
            uint iccOff = yresOffset + 8;
            writer.BaseStream.Seek(iccOff, SeekOrigin.Begin);
            writer.Write(iccProfile);
        }
    }
}