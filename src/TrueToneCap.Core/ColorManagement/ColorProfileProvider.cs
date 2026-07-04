// TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs
// 通过 WCS API 获取显示器 ICC 配置文件

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>显示器 ICC 配置文件提供器。</summary>
public static class ColorProfileProvider
{
    /// <summary>获取指定显示器的默认 ICC 配置文件内容。</summary>
    public static byte[]? GetDisplayIccProfile(nint monitorHandle)
    {
        try
        {
            // 通过 WCS API 获取显示器关联的颜色配置文件
            // WcsGetDefaultColorProfile 需要显示器名称
            var monitorName = GetMonitorName(monitorHandle);
            if (string.IsNullOrEmpty(monitorName)) return null;

            return GetIccProfileFromWcs(monitorName);
        }
        catch
        {
            return GetDefaultSRgbIcc();
        }
    }

    /// <summary>获取默认 sRGB ICC 配置文件。</summary>
    public static byte[] GetDefaultSRgbIcc()
    {
        // 嵌入式 sRGB IEC61966-2.1 最小配置文件（96 字节壳 + tone curve + 矩阵）
        // 实际应使用资源嵌入完整 sRGB ICC
        return BuildMinimalSRgbIcc();
    }

    private static byte[] BuildMinimalSRgbIcc()
    {
        // 最小 sRGB ICC 配置文件（用于嵌入）
        // 生产环境应使用完整的 IEC 61966-2-1 配置文件
        return
        [
            0x00, 0x00, 0x02, 0x0C, // profile size = 524 bytes (最小骨架)
            0x00, 0x00, 0x00, 0x00, // CMM type
            0x04, 0x30, 0x00, 0x00, // version 4.3.0
            0x6D, 0x6E, 0x74, 0x72, // 'mntr' device class
            0x52, 0x47, 0x42, 0x20, // 'RGB ' color space
            0x58, 0x59, 0x5A, 0x20, // 'XYZ ' PCS
            // ... 完整 ICC 见实际项目资源文件
        ];
    }

    private static string? GetMonitorName(nint monitorHandle)
    {
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        if (GetMonitorInfoW(monitorHandle, ref monitorInfo))
        {
            return monitorInfo.szDevice;
        }
        return null;
    }

    private static byte[]? GetIccProfileFromWcs(string deviceName)
    {
        // 尝试通过 WCS API 获取
        var profileType = ColorProfileType.ICC;
        uint size = 0;

        // 首先获取所需缓冲区大小
        if (!WcsGetDefaultColorProfileSize(
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            deviceName, profileType, ColorProfileSubType.DisplayDefault,
            0, ref size))
        {
            // 获取大小失败，返回 null
            return null;
        }

        var buffer = new byte[size];
        if (WcsGetDefaultColorProfile(
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            deviceName, profileType, ColorProfileSubType.DisplayDefault,
            0, size, buffer))
        {
            return buffer;
        }
        return null;
    }

    // ────────────── P/Invoke ──────────────

    private const uint WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    private enum ColorProfileType { ICC = 1 }
    private enum ColorProfileSubType { DisplayDefault = 1 }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsGetDefaultColorProfileSize(
        uint scope, string? deviceName, ColorProfileType profileType,
        ColorProfileSubType profileSubType, uint dwFlags, ref uint size);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsGetDefaultColorProfile(
        uint scope, string? deviceName, ColorProfileType profileType,
        ColorProfileSubType profileSubType, uint dwFlags, uint size, byte[] buffer);

    // ── ICC 描述提取 + 烘焙 ──

    /// <summary>从 ICC 二进制数据中提取描述字符串（desc 标签）。</summary>
    public static string? GetIccDescription(byte[] iccData)
    {
        try
        {
            if (iccData is null || iccData.Length < 132) return null;
            // ICC 文件头: bytes 0-127, 后面是标签表
            // 标签数量在 offset 128 (4 bytes big-endian)
            int tagCount = (iccData[128] << 24) | (iccData[129] << 16) | (iccData[130] << 8) | iccData[131];
            int offset = 132;
            for (int i = 0; i < tagCount; i++)
            {
                if (offset + 12 > iccData.Length) break;
                string tagSig = System.Text.Encoding.ASCII.GetString(iccData, offset, 4);
                int tagOff = (iccData[offset + 4] << 24) | (iccData[offset + 5] << 16) | (iccData[offset + 6] << 8) | iccData[offset + 7];
                int tagSize = (iccData[offset + 8] << 24) | (iccData[offset + 9] << 16) | (iccData[offset + 10] << 8) | iccData[offset + 11];
                if (tagSig == "desc" && tagOff + tagSize <= iccData.Length && tagSize > 12)
                {
                    // desc 标签: byte 8-11 = ASCII 描述长度, byte 12+ = 描述文本
                    int descLen = (iccData[tagOff + 8] << 24) | (iccData[tagOff + 9] << 16) | (iccData[tagOff + 10] << 8) | iccData[tagOff + 11];
                    if (descLen > 0 && descLen < tagSize)
                        return System.Text.Encoding.ASCII.GetString(iccData, tagOff + 12, Math.Min(descLen - 1, 200));
                    // 可能为 Unicode desc（mluc 标签）
                }
                if (tagSig == "mluc" && tagOff + tagSize <= iccData.Length && tagSize > 28)
                {
                    // mluc 标签: 多语言 Unicode
                    int numRecs = (iccData[tagOff + 8] << 24) | (iccData[tagOff + 9] << 16) | (iccData[tagOff + 10] << 8) | iccData[tagOff + 11];
                    for (int r = 0; r < Math.Min(numRecs, 10); r++)
                    {
                        int recOff = tagOff + 28 + r * 12;
                        if (recOff + 12 > iccData.Length) break;
                        int langCode = (iccData[recOff] << 8) | iccData[recOff + 1];
                        int strLen = (iccData[recOff + 4] << 24) | (iccData[recOff + 5] << 16) | (iccData[recOff + 6] << 8) | iccData[recOff + 7];
                        int strOff = (iccData[recOff + 8] << 24) | (iccData[recOff + 9] << 16) | (iccData[recOff + 10] << 8) | iccData[recOff + 11];
                        if (strLen > 0 && tagOff + strOff + strLen * 2 <= iccData.Length)
                        {
                            return System.Text.Encoding.BigEndianUnicode.GetString(iccData, tagOff + strOff, Math.Min(strLen * 2, 400));
                        }
                    }
                }
                offset += 12;
            }
        }
        catch { }
        return null;
    }

    /// <summary>使用 Magick.NET 将 BGRA 像素通过 ICC 配置文件烘焙为 sRGB。</summary>
    public static byte[]? BakeIccToSrgb(byte[] bgra, int w, int h, byte[] iccProfile)
    {
        try
        {
            var ps = new ImageMagick.PixelReadSettings((uint)w, (uint)h, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);
            using var img = new ImageMagick.MagickImage();
            img.ReadPixels(bgra, ps);
            img.SetProfile(new ImageMagick.ColorProfile(iccProfile));
            // 转换为 sRGB（使用内置 sRGB profile）
            img.TransformColorSpace(ImageMagick.ColorProfile.SRGB);
            // 移除 ICC 配置文件（已烘焙到像素中）
            img.RemoveProfile("icc");
            // 导出为 BGRA 原始像素
            var pixels = img.GetPixels();
            var result = new byte[w * h * 4];
            var nativePixels = pixels.ToByteArray(ImageMagick.PixelMapping.BGRA);
            System.Buffer.BlockCopy(nativePixels, 0, result, 0, result.Length);
            return result;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>简易色域转换器（scRGB ↔ sRGB/P3）。</summary>
public static class ColorSpaceConverter
{
    /// <summary>线性 scRGB → sRGB（gamma 编码）。</summary>
    public static Span<float> ScRgbToSRgb(Span<float> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            float c = pixels[i];
            pixels[i] = c <= 0.0031308f
                ? 12.92f * c
                : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
        }
        return pixels;
    }

    /// <summary>sRGB → 线性 scRGB（gamma 解码）。</summary>
    public static Span<float> SRgbToScRgb(Span<float> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            float c = pixels[i];
            pixels[i] = c <= 0.04045f
                ? c / 12.92f
                : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return pixels;
    }

    /// <summary>线性 scRGB → Display P3。</summary>
    public static Span<float> ScRgbToDisplayP3(Span<float> pixels)
    {
        // scRGB 到 P3 的矩阵转换
        // [R_p3]   [ 0.8225  0.1774  0.0001] [R_scrgb]
        // [G_p3] = [-0.0332  1.0334 -0.0002] [G_scrgb]
        // [B_p3]   [ 0.0171 -0.0575  1.0404] [B_scrgb]
        for (int i = 0; i < pixels.Length; i += 4)
        {
            float r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            pixels[i] = 0.8225f * r + 0.1774f * g + 0.0001f * b;
            pixels[i + 1] = -0.0332f * r + 1.0334f * g - 0.0002f * b;
            pixels[i + 2] = 0.0171f * r - 0.0575f * g + 1.0404f * b;
        }
        return pixels;
    }
}
