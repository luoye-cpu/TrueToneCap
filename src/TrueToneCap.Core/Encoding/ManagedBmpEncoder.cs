// TrueToneCap.Core/Encoding/ManagedBmpEncoder.cs
// 托管 BMP 编码器 — 零依赖，BITMAPINFOHEADER V4 (支持 alpha)

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 BMP 编码器 — 32-bit BGRA 输出。</summary>
public static class ManagedBmpEncoder
{
    public static void Encode(byte[] bgra, int w, int h, string path)
    {
        using var fs = File.Create(path);
        Encode(bgra, w, h, fs);
    }

    public static void Encode(byte[] bgra, int w, int h, Stream output)
    {
        int headerSize = 14;  // BITMAPFILEHEADER
        int infoSize = 108;   // BITMAPINFOHEADER V4 (支持 alpha 通道)
        int pixelDataSize = w * h * 4;
        int fileSize = headerSize + infoSize + pixelDataSize;

        var buf = new byte[fileSize];

        // BITMAPFILEHEADER
        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        Write32(buf, 2, (uint)fileSize);
        Write32(buf, 10, (uint)(headerSize + infoSize)); // pixel data offset

        // BITMAPINFOHEADER V4
        Write32(buf, 14, (uint)infoSize);
        Write32(buf, 18, (uint)w);
        Write32(buf, 22, (uint)(-h)); // top-down (负高度)
        Write16(buf, 26, 1);          // planes
        Write16(buf, 28, 32);         // bpp
        Write32(buf, 30, 3);          // BI_BITFIELDS
        Write32(buf, 34, (uint)pixelDataSize);
        Write32(buf, 38, 2835);       // 72 DPI X
        Write32(buf, 42, 2835);       // 72 DPI Y

        // RGBA masks (BGRA order)
        Write32(buf, 54, 0x00FF0000); // R mask
        Write32(buf, 58, 0x0000FF00); // G mask
        Write32(buf, 62, 0x000000FF); // B mask
        Write32(buf, 66, 0xFF000000); // A mask

        // Color space: sRGB (LCS_sRGB = 0x73524742)
        Write32(buf, 70, 0x73524742);

        // Pixel data (BGRA 直接拷贝，BMP 原生格式)
        Array.Copy(bgra, 0, buf, headerSize + infoSize, pixelDataSize);

        output.Write(buf);
    }

    private static void Write32(byte[] buf, int off, uint v)
    { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24); }

    private static void Write16(byte[] buf, int off, ushort v)
    { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); }
}
