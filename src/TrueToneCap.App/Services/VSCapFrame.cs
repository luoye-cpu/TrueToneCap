using System.Runtime.InteropServices;

namespace TrueToneCap.App.Services;

// Little-endian TTC1: magic, width, height, kind (BGRA8=0 / scRGB RGBA32F=1), physical x/y.
internal sealed record VSCapFrame(int Width, int Height, byte[]? SdrPixels, float[]? HdrPixels)
{
    private const uint Magic = 0x31435454;

    public void Write(string path, int x, int y)
    {
        Validate();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Magic); writer.Write(Width); writer.Write(Height);
        writer.Write(HdrPixels is null ? 0 : 1); writer.Write(x); writer.Write(y);
        if (HdrPixels is not null) writer.Write(MemoryMarshal.AsBytes(HdrPixels.AsSpan()));
        else writer.Write(SdrPixels!);
    }

    public static VSCapFrame Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid TTC frame signature");
        int w = reader.ReadInt32(), h = reader.ReadInt32(), kind = reader.ReadInt32();
        reader.ReadInt32(); reader.ReadInt32();
        if (w <= 0 || h <= 0 || kind is not (0 or 1)) throw new InvalidDataException("Invalid TTC frame header");
        long bytes = (long)w * h * (kind == 1 ? 16 : 4);
        if (bytes > int.MaxValue || reader.BaseStream.Length - 24 != bytes)
            throw new InvalidDataException("Invalid TTC frame size");
        var pixels = reader.ReadBytes((int)bytes);
        if (kind == 0) return new(w, h, pixels, null);
        var floats = new float[checked(w * h * 4)];
        pixels.CopyTo(MemoryMarshal.AsBytes(floats.AsSpan()));
        return new(w, h, null, floats);
    }

    private void Validate()
    {
        long samples = (long)Width * Height * 4;
        if (Width <= 0 || Height <= 0 || (HdrPixels is null ? SdrPixels?.LongLength != samples : HdrPixels.LongLength != samples))
            throw new InvalidDataException("Invalid TTC frame pixels");
    }
}
