// TrueToneCap.Core/Encoding/JpegLiNative.cs
// Google jpegli 编码器 — 基于 JxlNet NuGet 包 (内置 jxl.dll, 零手动部署)

using JxlNet;

namespace TrueToneCap.Core.Encoding;

/// <summary>jpegli 编码器（JxlNet 内置 jxl.dll）。</summary>
public sealed unsafe class JpegLiNative : IDisposable
{
    private JxlEncoderStruct* _encoder;
    private bool _disposed;
    private static bool s_initialized;

    public static bool IsAvailable
    {
        get
        {
            if (!s_initialized)
            {
                try { var e = Jxl.JxlEncoderCreate(null); if (e != null) { Jxl.JxlEncoderDestroy(e); s_initialized = true; } }
                catch { }
            }
            return s_initialized;
        }
    }

    public static void Initialize()
    {
        if (IsAvailable)
            System.Diagnostics.Debug.WriteLine("[JpegLiNative] jpegli (JxlNet) 就绪");
    }

    public JpegLiNative()
    {
        _encoder = Jxl.JxlEncoderCreate(null);
        if (_encoder == null) throw new InvalidOperationException("jpegli 创建失败");
    }

    /// <summary>BGRA8→JPEG。distance: butteraugli (0.5≈无损, 1.0=高质量)。</summary>
    public byte[] Encode(byte[] bgra, int width, int height, float distance = 1.0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bi = new JxlBasicInfo
        {
            xsize = (uint)width, ysize = (uint)height,
            bits_per_sample = 8, intensity_target = 255f,
            num_color_channels = 3,
            orientation = JxlOrientation.JXL_ORIENT_IDENTITY
        };
        Jxl.JxlEncoderSetBasicInfo(_encoder, &bi);

        // jpegli 模式: 不用容器 + 存 JPEG metadata
        Jxl.JxlEncoderUseContainer(_encoder, 0);
        Jxl.JxlEncoderStoreJPEGMetadata(_encoder, 1);

        var fs = Jxl.JxlEncoderFrameSettingsCreate(_encoder, null);
        Jxl.JxlEncoderSetFrameDistance(fs, Math.Clamp(distance, 0.1f, 15f));

        // BGRA→RGB
        int n = width * height;
        var rgb = new byte[n * 3];
        for (int i = 0; i < n; i++)
        { int s = i * 4, d = i * 3; rgb[d] = bgra[s + 2]; rgb[d + 1] = bgra[s + 1]; rgb[d + 2] = bgra[s]; }

        var fmt = new JxlPixelFormat { num_channels = 3, data_type = JxlDataType.JXL_TYPE_UINT8, endianness = JxlEndianness.JXL_NATIVE_ENDIAN, align = 1 };
        fixed (byte* p = rgb)
            Jxl.JxlEncoderAddImageFrame(fs, &fmt, p, (nuint)rgb.Length);

        Jxl.JxlEncoderCloseInput(_encoder);

        var outBuf = new List<byte>(65536);
        var chunk = new byte[65536];
        while (true)
        {
            JxlEncoderStatus status;
            fixed (byte* p = chunk)
            {
                byte* o = p;
                nuint avail = 65536;
                status = Jxl.JxlEncoderProcessOutput(_encoder, &o, &avail);
                int written = 65536 - (int)avail;
                if (written > 0) outBuf.AddRange(chunk.AsSpan(0, written).ToArray());
            }
            // JXL_ENC_SUCCESS = 编码完成; JXL_ENC_NEED_MORE_OUTPUT = 缓冲区满需继续
            if (status == JxlEncoderStatus.JXL_ENC_SUCCESS) break;
            if (status != JxlEncoderStatus.JXL_ENC_NEED_MORE_OUTPUT) break; // 错误
        }
        return outBuf.ToArray();
    }

    public void Dispose()
    { if (!_disposed) { _disposed = true; if (_encoder != null) { Jxl.JxlEncoderDestroy(_encoder); _encoder = null; } } }
}
