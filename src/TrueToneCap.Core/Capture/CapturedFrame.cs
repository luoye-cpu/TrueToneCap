using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TrueToneCap.Core.Capture;

/// <summary>封装捕获到的帧纹理，提供同步/异步像素读取。</summary>
public sealed class CapturedFrame : IDisposable
{
    private readonly ID3D11Texture2D _texture;
    private bool _disposed;

    internal CapturedFrame(ID3D11Texture2D texture, long lastPresentTime, int width, int height)
    {
        _texture = texture;
        LastPresentTime = lastPresentTime;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public long LastPresentTime { get; }
    public bool IsHdr => _texture.Description.Format == Format.R16G16B16A16_Float;
    public Format Format => _texture.Description.Format;
    public ID3D11Texture2D Texture => _texture;

    /// <summary>异步获取 float 像素（适用于 HDR Float16 格式），正确执行 Half→Float 转换。</summary>
    public Task<float[]> GetFloatPixelsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dev = _texture.Device;
        var ctx = dev.ImmediateContext;

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        var staging = dev.CreateTexture2D(stagingDesc);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ctx.CopyResource(staging, _texture);
                var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int pixelCount = Width * Height * 4;
                var pixels = new float[pixelCount];

                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int halfsPerRow = Width * 4; // R,G,B,A per pixel
                    int srcBytesPerRow = halfsPerRow * 2; // 2 bytes per half-float

                    fixed (float* dst = pixels)
                    {
                        for (int row = 0; row < Height; row++)
                        {
                            byte* srcRow = srcBase + row * srcRowPitch;
                            float* dstRow = dst + row * Width * 4;

                            // 逐像素转换 Half→Float
                            for (int col = 0; col < halfsPerRow; col++)
                            {
                                ushort halfBits = (ushort)(srcRow[col * 2] | (srcRow[col * 2 + 1] << 8));
                                dstRow[col] = HalfToFloat(halfBits);
                            }
                        }
                    }
                }
                ctx.Unmap(staging, 0);
                return pixels;
            }
            finally
            {
                staging.Dispose();
            }
        }, ct);
    }

    /// <summary>IEEE 754 Half (binary16) → Float (binary32) 转换。</summary>
    private static float HalfToFloat(ushort h)
    {
        uint sign = (uint)(h & 0x8000) << 16;
        int exp = (h >> 10) & 0x1F;
        int mant = h & 0x3FF;

        if (exp == 0)
        {
            // 零/次正规数
            if (mant == 0) return BitConverter.Int32BitsToSingle((int)sign);
            // 次正规数：规范化
            exp = -14;
            while ((mant & 0x400) == 0) { mant <<= 1; exp--; }
            mant &= 0x3FF;
            uint bits = sign | ((uint)(exp + 127) << 23) | ((uint)mant << 13);
            return BitConverter.Int32BitsToSingle((int)bits);
        }
        else if (exp == 31)
        {
            // 无穷大/NaN
            uint bits = sign | (0xFFu << 23) | ((uint)mant << 13);
            return BitConverter.Int32BitsToSingle((int)bits);
        }
        else
        {
            // 正规数
            uint bits = sign | ((uint)(exp - 15 + 127) << 23) | ((uint)mant << 13);
            return BitConverter.Int32BitsToSingle((int)bits);
        }
    }

    /// <summary>异步获取 byte 像素（适用于 BGRA8 SDR 格式）。
    /// RTX 5080 驱动 bug: Float16 纹理可能实际存储 BGRA8 数据。</summary>
    public Task<byte[]> GetBytePixelsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dev = _texture.Device;
        var ctx = dev.ImmediateContext;
        var texFormat = _texture.Description.Format;

        // 当纹理报告 Float16 但从 DuplicateOutput1(BGRA8) 创建时，
        // 实际数据可能是 BGRA8（驱动报告格式错误）。按 Float16 映射后手动提取 BGRA8 字节。
        bool maybeMislabeled = texFormat == Format.R16G16B16A16_Float;

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = texFormat, // 用实际格式映射，然后手动提取
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        var staging = dev.CreateTexture2D(stagingDesc);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ctx.CopyResource(staging, _texture);
                var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int totalBytes = Width * Height * 4; // 输出始终 BGRA8 4字节/像素
                var pixels = new byte[totalBytes];

                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int srcBytesPerPixel = maybeMislabeled ? 8 : 4; // Float16=8, BGRA8=4

                    fixed (byte* dst = pixels)
                    {
                        for (int row = 0; row < Height; row++)
                        {
                            byte* srcRow = src + row * srcRowPitch;
                            byte* dstRow = dst + row * Width * 4;
                            if (maybeMislabeled)
                            {
                                // Float16 纹理中提取 BGRA8: 每8字节取前4字节
                                for (int x = 0; x < Width; x++)
                                {
                                    int si = x * 8;
                                    int di = x * 4;
                                    dstRow[di]     = srcRow[si];
                                    dstRow[di + 1] = srcRow[si + 1];
                                    dstRow[di + 2] = srcRow[si + 2];
                                    dstRow[di + 3] = srcRow[si + 3];
                                }
                            }
                            else
                            {
                                Buffer.MemoryCopy(srcRow, dstRow, Width * 4, Width * 4);
                            }
                        }
                    }
                }
                ctx.Unmap(staging, 0);
                return pixels;
            }
            finally
            {
                staging.Dispose();
            }
        }, ct);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _texture.Dispose();
        }
    }
}
