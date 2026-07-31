// TrueToneCap.Core/Encoding/NativeJxlEncoder.cs
// JPEG XL 原生编码器 — 通过 JxlNet NuGet 包 (内置 jxl.dll, 零手动部署)
// 支持: 8/10/12-bit, modular/VarDCT, ICC 嵌入

using JxlNet;

namespace TrueToneCap.Core.Encoding;

/// <summary>JPEG XL 原生编码器 — 通过 JxlNet API 直接调用。</summary>
public static unsafe class NativeJxlEncoder
{
    /// <summary>检测 JXL 编码器是否可用。</summary>
    public static bool IsAvailable
    {
        get
        {
            try { var e = Jxl.JxlEncoderCreate(null); if (e != null) { Jxl.JxlEncoderDestroy(e); return true; } }
            catch { }
            return false;
        }
    }

    /// <summary>编码 16-bit HDR RGBA 像素为 JXL 文件 (PQ, BT.2020)。</summary>
    public static void EncodeHdr(ushort[] rgba16, int w, int h, string path,
        float distance = 1.0f, byte[]? iccProfile = null, float intensityTarget = 10000f)
    {
        var encoder = Jxl.JxlEncoderCreate(null);
        if (encoder == null) throw new InvalidOperationException("[JXL] JxlEncoderCreate 失败");

        try
        {
            var bi = new JxlBasicInfo
            {
                xsize = (uint)w, ysize = (uint)h,
                bits_per_sample = 16,
                exponent_bits_per_sample = 0, // 整型 16-bit (PQ 编码)
                num_color_channels = 3,
                intensity_target = intensityTarget,
                min_nits = 0f,
                relative_to_max_display = 0,
                orientation = JxlOrientation.JXL_ORIENT_IDENTITY
            };

            var result = Jxl.JxlEncoderSetBasicInfo(encoder, &bi);
            if (result != JxlEncoderStatus.JXL_ENC_SUCCESS)
                throw new InvalidOperationException($"[JXL] SetBasicInfo 失败: {result}");

            // ═══ 设置色彩编码为 PQ / BT.2020 ═══
            // 使用 JxlEncoderSetColorEncoding 替代 BasicInfo.color_encoding
            var colorEncoding = new JxlColorEncoding
            {
                color_space = JxlColorSpace.JXL_COLOR_SPACE_RGB,
                white_point = JxlWhitePoint.JXL_WHITE_POINT_D65,
                primaries = JxlPrimaries.JXL_PRIMARIES_2100,
                transfer_function = JxlTransferFunction.JXL_TRANSFER_FUNCTION_PQ,
                rendering_intent = JxlRenderingIntent.JXL_RENDERING_INTENT_RELATIVE,
            };
            var ceResult = Jxl.JxlEncoderSetColorEncoding(encoder, &colorEncoding);
            if (ceResult != JxlEncoderStatus.JXL_ENC_SUCCESS)
                System.Diagnostics.Debug.WriteLine($"[JXL] SetColorEncoding 警告: {ceResult}");

            // ICC Profile 嵌入 (BT.2100 PQ)
            if (iccProfile is { Length: > 128 })
            {
                fixed (byte* iccPtr = iccProfile)
                    Jxl.JxlEncoderSetICCProfile(encoder, iccPtr, (nuint)iccProfile.Length);
            }

            Jxl.JxlEncoderUseContainer(encoder, 1);
            var fs = Jxl.JxlEncoderFrameSettingsCreate(encoder, null);
            Jxl.JxlEncoderSetFrameDistance(fs, Math.Clamp(distance, 0.0f, 15f));
            Jxl.JxlEncoderFrameSettingsSetOption(fs, JxlEncoderFrameSettingId.JXL_ENC_FRAME_SETTING_EFFORT, 9);

            // RGBA16 → RGB16 (去 Alpha)
            int n = w * h;
            var rgb16 = new ushort[n * 3];
            for (int i = 0; i < n; i++)
            {
                int s = i * 4, d = i * 3;
                rgb16[d] = rgba16[s];         // R
                rgb16[d + 1] = rgba16[s + 1]; // G
                rgb16[d + 2] = rgba16[s + 2]; // B
            }

            var fmt16 = new JxlPixelFormat { num_channels = 3, data_type = JxlDataType.JXL_TYPE_UINT16, endianness = JxlEndianness.JXL_NATIVE_ENDIAN, align = 1 };
            fixed (ushort* p = rgb16)
                Jxl.JxlEncoderAddImageFrame(fs, &fmt16, p, (nuint)(rgb16.Length * 2));

            Jxl.JxlEncoderCloseInput(encoder);

            var outBuf = new List<byte>(65536);
            var chunk = new byte[65536];
            while (true)
            {
                JxlEncoderStatus status;
                fixed (byte* p = chunk)
                {
                    byte* o = p;
                    nuint avail = 65536;
                    status = Jxl.JxlEncoderProcessOutput(encoder, &o, &avail);
                    int written = 65536 - (int)avail;
                    if (written > 0) outBuf.AddRange(chunk.AsSpan(0, written).ToArray());
                }
                if (status == JxlEncoderStatus.JXL_ENC_SUCCESS) break;
                if (status != JxlEncoderStatus.JXL_ENC_NEED_MORE_OUTPUT)
                    throw new InvalidOperationException($"[JXL] HDR 编码错误: {status}");
            }

            File.WriteAllBytes(path, outBuf.ToArray());
        }
        finally
        {
            Jxl.JxlEncoderDestroy(encoder);
        }
    }

    /// <summary>编码 BGRA 像素为 JXL 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        float distance = 1.0f, int bitDepth = 10, byte[]? iccProfile = null)
    {
        var encoder = Jxl.JxlEncoderCreate(null);
        if (encoder == null) throw new InvalidOperationException("[JXL] JxlEncoderCreate 失败");

        try
        {
            var bi = new JxlBasicInfo
            {
                xsize = (uint)w, ysize = (uint)h,
                // 位深: 8-bit 输入 → bits_per_sample=8; 10/12-bit 输入 → 对应位深
                bits_per_sample = (uint)Math.Clamp(bitDepth, 8, 16),
                intensity_target = 255f,
                num_color_channels = 3,
                uses_original_profile = 1, // 输入已是 sRGB gamma，不做内部色彩转换
                orientation = JxlOrientation.JXL_ORIENT_IDENTITY
            };
            Jxl.JxlEncoderSetBasicInfo(encoder, &bi);

            // ═══ ICC Profile 嵌入 ═══
            if (iccProfile is { Length: > 128 })
            {
                fixed (byte* iccPtr = iccProfile)
                    Jxl.JxlEncoderSetICCProfile(encoder, iccPtr, (nuint)iccProfile.Length);
            }

            // 使用容器格式（支持元数据）
            Jxl.JxlEncoderUseContainer(encoder, 1);

            var fs = Jxl.JxlEncoderFrameSettingsCreate(encoder, null);
            Jxl.JxlEncoderSetFrameDistance(fs, Math.Clamp(distance, 0.0f, 15f));

            // 设置 effort (压缩努力)
            Jxl.JxlEncoderFrameSettingsSetOption(fs, JxlEncoderFrameSettingId.JXL_ENC_FRAME_SETTING_EFFORT, 9);

            // BGRA → RGB (根据位深选择 8-bit 或 16-bit 路径)
            int n = w * h;
            int outBitDepth = Math.Clamp(bitDepth, 8, 16);

            if (outBitDepth > 8)
            {
                // 10/12/16-bit: 8-bit 输入扩展到 16-bit (val * 257)
                var rgb16 = new ushort[n * 3];
                for (int i = 0; i < n; i++)
                {
                    int s = i * 4, d = i * 3;
                    rgb16[d] = (ushort)(bgra[s + 2] * 257);     // R
                    rgb16[d + 1] = (ushort)(bgra[s + 1] * 257); // G
                    rgb16[d + 2] = (ushort)(bgra[s] * 257);     // B
                }
                var fmt16 = new JxlPixelFormat { num_channels = 3, data_type = JxlDataType.JXL_TYPE_UINT16, endianness = JxlEndianness.JXL_NATIVE_ENDIAN, align = 1 };
                fixed (ushort* p = rgb16)
                    Jxl.JxlEncoderAddImageFrame(fs, &fmt16, p, (nuint)(rgb16.Length * 2));
            }
            else
            {
                // 8-bit 路径
                var rgb = new byte[n * 3];
                for (int i = 0; i < n; i++)
                { int s = i * 4, d = i * 3; rgb[d] = bgra[s + 2]; rgb[d + 1] = bgra[s + 1]; rgb[d + 2] = bgra[s]; }
                var fmt = new JxlPixelFormat { num_channels = 3, data_type = JxlDataType.JXL_TYPE_UINT8, endianness = JxlEndianness.JXL_NATIVE_ENDIAN, align = 1 };
                fixed (byte* p = rgb)
                    Jxl.JxlEncoderAddImageFrame(fs, &fmt, p, (nuint)rgb.Length);
            }

            Jxl.JxlEncoderCloseInput(encoder);

            // 输出
            var outBuf = new List<byte>(65536);
            var chunk = new byte[65536];
            while (true)
            {
                JxlEncoderStatus status;
                fixed (byte* p = chunk)
                {
                    byte* o = p;
                    nuint avail = 65536;
                    status = Jxl.JxlEncoderProcessOutput(encoder, &o, &avail);
                    int written = 65536 - (int)avail;
                    if (written > 0) outBuf.AddRange(chunk.AsSpan(0, written).ToArray());
                }
                if (status == JxlEncoderStatus.JXL_ENC_SUCCESS) break;
                if (status != JxlEncoderStatus.JXL_ENC_NEED_MORE_OUTPUT)
                    throw new InvalidOperationException($"[JXL] 编码错误: {status}");
            }

            File.WriteAllBytes(path, outBuf.ToArray());
        }
        finally
        {
            Jxl.JxlEncoderDestroy(encoder);
        }
    }
}
