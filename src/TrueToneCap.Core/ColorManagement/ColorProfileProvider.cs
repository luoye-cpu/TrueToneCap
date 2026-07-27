// TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs
// 通过 WCS API 获取显示器 ICC 配置文件 — 带缓存 + 异步

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>显示器 ICC 配置文件提供器（带缓存 + 异步）。</summary>
public static class ColorProfileProvider
{
    // ═══ ICC 缓存：按显示器设备名缓存，避免每次截图重复调 WCS API ═══
    private static readonly ConcurrentDictionary<string, byte[]?> s_iccCache = new();
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> s_asyncCache = new();

    /// <summary>获取指定显示器的默认 ICC 配置文件内容（同步读缓存，永不阻塞）。</summary>
    /// <remarks>缓存命中立即返回；缓存未命中时启动后台预热并立即返回 null，
    /// 绝不阻塞调用线程（避免 WCS API 数秒级同步调用卡住截图/覆盖层弹出）。
    /// 编码保存时再次调用通常已命中缓存。</remarks>
    public static byte[]? GetDisplayIccProfile(nint monitorHandle)
    {
        try
        {
            var monitorName = GetMonitorName(monitorHandle);
            if (string.IsNullOrEmpty(monitorName)) return null;

            // 缓存命中 → 零开销返回
            if (s_iccCache.TryGetValue(monitorName, out var cached))
                return cached;

            // 缓存未命中 → 后台预热，本次立即返回 null（不阻塞）
            _ = GetDisplayIccProfileAsync(monitorHandle);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>异步获取 ICC（不阻塞调用线程）。首次调用后台获取并缓存。</summary>
    public static Task<byte[]?> GetDisplayIccProfileAsync(nint monitorHandle)
    {
        try
        {
            var monitorName = GetMonitorName(monitorHandle);
            if (string.IsNullOrEmpty(monitorName)) return Task.FromResult<byte[]?>(null);

            // 缓存命中 → 立即返回
            if (s_iccCache.TryGetValue(monitorName, out var cached))
                return Task.FromResult(cached);

            // 异步获取（防止同一显示器并发重复调 WCS）
            return s_asyncCache.GetOrAdd(monitorName, name =>
                Task.Run(() =>
                {
                    var profile = GetIccProfileFromWcs(name);
                    s_iccCache[name] = profile;
                    return profile;
                }));
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    /// <summary>清除 ICC 缓存（显示器配置变更时调用）。</summary>
    public static void InvalidateCache()
    {
        s_iccCache.Clear();
        s_asyncCache.Clear();
    }

    // ═══ sRGB ICC 缓存（完整配置文件，仅加载一次）═══
    private static byte[]? s_srgbIccCache;
    private static readonly object s_srgbLock = new();

    /// <summary>获取默认 sRGB ICC 配置文件（完整的 IEC 61966-2.1 配置文件）。</summary>
    /// <remarks>优先从 Windows 系统目录读取 sRGB Color Space Profile.icm，
    /// 失败时回退到内置的最小有效 sRGB ICC 配置文件。</remarks>
    public static byte[] GetDefaultSRgbIcc()
    {
        if (s_srgbIccCache is not null) return s_srgbIccCache;

        lock (s_srgbLock)
        {
            if (s_srgbIccCache is not null) return s_srgbIccCache;

            // 策略1: 从 Windows 系统色彩目录读取完整 sRGB 配置文件
            try
            {
                var systemIcc = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "spool", "drivers", "color", "sRGB Color Space Profile.icm");
                if (File.Exists(systemIcc))
                {
                    var data = File.ReadAllBytes(systemIcc);
                    if (data.Length > 128) // 有效 ICC 至少 128 字节头
                    {
                        s_srgbIccCache = data;
                        return s_srgbIccCache;
                    }
                }
            }
            catch { /* 回退到内置配置文件 */ }

            // 策略2: 内置最小有效 sRGB ICC 配置文件 (v2.1, 3144 字节)
            // 包含: header + tag table + desc/cprt/wtpt/bkpt/rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC
            s_srgbIccCache = BuildValidSRgbIcc();
            return s_srgbIccCache;
        }
    }

    /// <summary>构建最小但有效的 sRGB ICC 配置文件 (v2.1)。</summary>
    private static byte[] BuildValidSRgbIcc()
    {
        // 使用 Magick.NET 内置的 sRGB 配置文件（完整有效）
        try
        {
            using var img = new ImageMagick.MagickImage();
            img.ColorSpace = ImageMagick.ColorSpace.sRGB;
            var profile = img.GetColorProfile();
            if (profile?.ToByteArray() is { Length: > 128 } bytes)
                return bytes;
        }
        catch { /* 回退到手写最小 ICC */ }

        // 最终回退: 手写最小有效 sRGB ICC v2.1 配置文件
        // 结构: Header(128) + TagTable(12+10*12=132) + TagData
        using var ms = new MemoryStream(3200);
        using var bw = new BinaryWriter(ms);

        // 占位，最后回填 profile size
        bw.Write(0); // profile size (offset 0)
        bw.Write(0); // preferred CMM
        bw.Write(new byte[] { 0x02, 0x10, 0x00, 0x00 }); // version 2.1.0
        bw.Write("mntr"u8); // device class
        bw.Write("RGB "u8); // color space
        bw.Write("XYZ "u8); // PCS
        // date/time
        bw.Write((ushort)2024); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write("acsp"u8); // profile file signature
        bw.Write(0); // primary platform
        bw.Write(0); // profile flags
        bw.Write(0); // device manufacturer
        bw.Write(0); // device model
        bw.Write(0L); // device attributes
        bw.Write(0); // rendering intent (perceptual)
        // PCS illuminant (D50): X=0.9642, Y=1.0, Z=0.8249 (s15Fixed16)
        bw.Write(0x0000F6D6); bw.Write(0x00010000); bw.Write(0x0000D32D);
        bw.Write(0); // profile creator
        bw.Write(new byte[16]); // profile ID (MD5, 留空)
        bw.Write(new byte[28]); // reserved

        // Tag table: 10 tags
        int tagCount = 10;
        bw.Write(tagCount);

        // 计算数据区起始 (header 128 + tag count 4 + tags 10*12=120 = 252, 对齐到 4)
        int dataStart = 128 + 4 + tagCount * 12;
        dataStart = (dataStart + 3) & ~3;

        // 准备各标签数据
        byte[] descData = BuildDescTag("sRGB IEC61966-2.1");
        byte[] cprtData = BuildTextTag("Public Domain");
        byte[] wtptData = BuildXyzTag(0.9505f, 1.0f, 1.0891f); // D65
        byte[] bkptData = BuildXyzTag(0f, 0f, 0f);
        byte[] rXYZData = BuildXyzTag(0.4124f, 0.2126f, 0.0193f);
        byte[] gXYZData = BuildXyzTag(0.3576f, 0.7152f, 0.1192f);
        byte[] bXYZData = BuildXyzTag(0.1805f, 0.0722f, 0.9505f);
        byte[] trcData = BuildCurveTag(); // sRGB tone curve

        var tags = new (string sig, byte[] data)[]
        {
            ("desc", descData), ("cprt", cprtData), ("wtpt", wtptData),
            ("bkpt", bkptData), ("rXYZ", rXYZData), ("gXYZ", gXYZData),
            ("bXYZ", bXYZData), ("rTRC", trcData), ("gTRC", trcData), ("bTRC", trcData),
        };

        // 写入 tag table + 计算偏移
        int curOffset = dataStart;
        var offsets = new int[tagCount];
        for (int i = 0; i < tagCount; i++)
        {
            offsets[i] = curOffset;
            curOffset += (tags[i].data.Length + 3) & ~3; // 4 字节对齐
        }

        for (int i = 0; i < tagCount; i++)
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes(tags[i].sig));
            bw.Write(offsets[i]);
            bw.Write(tags[i].data.Length);
        }

        // 填充到 dataStart
        while (ms.Position < dataStart) bw.Write((byte)0);

        // 写入 tag 数据
        for (int i = 0; i < tagCount; i++)
        {
            bw.Write(tags[i].data);
            while (ms.Position % 4 != 0) bw.Write((byte)0);
        }

        // 回填 profile size
        int totalSize = (int)ms.Position;
        ms.Position = 0;
        bw.Write(totalSize);

        return ms.ToArray();
    }

    private static byte[] BuildDescTag(string text)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("desc"u8); bw.Write(0); // signature + reserved
        var ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        bw.Write(ascii.Length); bw.Write(ascii);
        bw.Write(0); // unicode language code
        bw.Write(0); // unicode count
        bw.Write((ushort)0); bw.Write((ushort)0); // scriptcode code, count
        bw.Write(new byte[67]); // scriptcode string
        return ms.ToArray();
    }

    private static byte[] BuildTextTag(string text)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("text"u8); bw.Write(0);
        bw.Write(System.Text.Encoding.ASCII.GetBytes(text + "\0"));
        return ms.ToArray();
    }

    private static byte[] BuildXyzTag(float x, float y, float z)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("XYZ "u8); bw.Write(0);
        bw.Write((int)(x * 65536)); bw.Write((int)(y * 65536)); bw.Write((int)(z * 65536));
        return ms.ToArray();
    }

    private static byte[] BuildCurveTag()
    {
        // sRGB tone curve: 256 采样点
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("curv"u8); bw.Write(0);
        bw.Write(256); // count
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            float linear = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            bw.Write((ushort)Math.Clamp((int)(linear * 65535 + 0.5f), 0, 65535));
        }
        return ms.ToArray();
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

    /// <summary>使用 Magick.NET + ACES 感知意图将 BGRA 像素从源 ICC 烘焙到目标色彩空间。
    /// 影视标准 ACES 系统：Perceptual 意图，视觉效果最优。</summary>
    public static (byte[]? pixels, byte[]? targetIcc) BakeIccToTarget(
        byte[] bgra, int w, int h, byte[] sourceIcc, string targetColorSpace)
    {
        try
        {
            var targetIcc = GetStandardIccProfile(targetColorSpace);
            if (targetIcc is null)
            {
                System.Diagnostics.Debug.WriteLine($"[ICC] 目标色彩空间 '{targetColorSpace}' 无标准 ICC，回退 sRGB");
                targetIcc = GetStandardIccProfile("sRGB");
                if (targetIcc is null) return (null, null);
            }

            var ps = new ImageMagick.PixelReadSettings((uint)w, (uint)h,
                ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);
            using var img = new ImageMagick.MagickImage();
            img.ReadPixels(bgra, ps);
            img.SetProfile(new ImageMagick.ColorProfile(sourceIcc));

            // ACES 标准：Perceptual 感知渲染意图
            img.RenderingIntent = ImageMagick.RenderingIntent.Perceptual;
            img.TransformColorSpace(new ImageMagick.ColorProfile(targetIcc));

            var nativePixels = img.ToByteArray(ImageMagick.MagickFormat.Bgra);
            var result = new byte[w * h * 4];
            System.Buffer.BlockCopy(nativePixels, 0, result, 0, result.Length);

            System.Diagnostics.Debug.WriteLine(
                $"[ICC] ACES 烘焙: {w}x{h}, {sourceIcc.Length}B → {targetColorSpace}");
            return (result, targetIcc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ICC] 烘焙到 {targetColorSpace} 失败: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>获取标准色彩空间 ICC Profile。</summary>
    public static byte[]? GetStandardIccProfile(string colorSpace)
    {
        try
        {
            // Magick.NET 内置 profiles
            if (colorSpace == "sRGB")
                return ImageMagick.ColorProfiles.SRGB.ToByteArray();
            if (colorSpace == "AdobeRGB")
                return ImageMagick.ColorProfiles.AdobeRGB1998.ToByteArray();

            // 自定义生成的 ICC v2 profiles (Display P3, BT.2020)
            if (colorSpace == "DisplayP3" || colorSpace == "DCI_P3")
                return IccProfileBuilder.Generate(IccPrimaries.DisplayP3, IccTransferCurve.Gamma22);
            if (colorSpace == "BT2020")
                return IccProfileBuilder.Generate(IccPrimaries.BT2020, IccTransferCurve.Gamma22);

            // 默认 sRGB
            return ImageMagick.ColorProfiles.SRGB.ToByteArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ICC] 获取标准 ICC '{colorSpace}' 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>将色彩空间标签映射到标准名称。</summary>
    public static string MapColorSpaceTag(string tag) => tag switch
    {
        "System" => "sRGB",
        "sRGB" => "sRGB",
        "DisplayP3" => "DisplayP3",
        "DCI_P3" => "DisplayP3",
        "AdobeRGB" => "AdobeRGB",
        "BT2020" => "BT2020",
        _ => "sRGB"
    };

    /// <summary>使用 Magick.NET 将 BGRA 像素通过 ICC 配置文件烘焙为 sRGB（便捷方法）。
    /// 烘焙后像素值已转换为 sRGB，不应再嵌入 ICC profile。</summary>
    public static byte[]? BakeIccToSrgb(byte[] bgra, int w, int h, byte[] iccProfile)
    {
        var (pixels, _) = BakeIccToTarget(bgra, w, h, iccProfile, "sRGB");
        return pixels;
    }

    /// <summary>获取当前所选色彩空间的用户友好名称。</summary>
    public static string GetColorSpaceDisplayName(string tag) => tag switch
    {
        "System" => "sRGB (跟随系统)",
        "sRGB" => "sRGB IEC61966-2.1",
        "DisplayP3" => "Display P3",
        "DCI_P3" => "DCI-P3",
        "AdobeRGB" => "Adobe RGB (1998)",
        "BT2020" => "BT.2020",
        _ => "sRGB"
    };

    /// <summary>检测 ICC 是否与标准 sRGB 不同（用于判断是否需要烘焙）。
    /// 比较 ICC 的 MD5 或关键标签值。简单启发式：>2KB 且非系统默认。</summary>
    public static bool IsNonStandardIcc(byte[]? icc)
    {
        if (icc is null || icc.Length < 2048) return false;
        // 系统默认 sRGB IEC61966-2.1 通常约 3KB，但内含特定签名
        // 简单判断：如果描述中包含 "sRGB" 且无厂商标记 → 系统默认
        var desc = GetIccDescription(icc);
        if (desc is not null)
        {
            // 常见系统默认描述
            if (desc.Contains("sRGB IEC61966-2.1") && !desc.Contains("Calibrated"))
                return false; // 系统默认 sRGB
        }
        return true; // 自定义 ICC
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

// ═══════════════════════════════════════
//  ICC v2 Profile 生成器
// ═══════════════════════════════════════

/// <summary>ICC 色域原色 (CIE xy)。</summary>
internal readonly record struct IccPrimaries(float Rx, float Ry, float Gx, float Gy, float Bx, float By)
{
    public static readonly IccPrimaries DisplayP3 = new(0.6800f, 0.3200f, 0.2650f, 0.6900f, 0.1500f, 0.0600f);
    public static readonly IccPrimaries BT2020 = new(0.7080f, 0.2920f, 0.1700f, 0.7970f, 0.1310f, 0.0460f);

    public static readonly (float X, float Y, float Z) D65 = (0.3127f, 0.3290f, 0.3583f);
}

/// <summary>ICC 传输曲线类型。</summary>
internal enum IccTransferCurve { Srgb, Linear, Gamma22 }

/// <summary>ICC v2 Profile 构建器。</summary>
internal static class IccProfileBuilder
{
    public static byte[] Generate(IccPrimaries p, IccTransferCurve curve)
    {
        var (rX, rY, rZ) = XyToXyz(p.Rx, p.Ry);
        var (gX, gY, gZ) = XyToXyz(p.Gx, p.Gy);
        var (bX, bY, bZ) = XyToXyz(p.Bx, p.By);
        var (wX, wY, wZ) = XyToXyz(IccPrimaries.D65.X, IccPrimaries.D65.Y);

        var rXyz = MakeXyzTag(rX, rY, rZ);
        var gXyz = MakeXyzTag(gX, gY, gZ);
        var bXyz = MakeXyzTag(bX, bY, bZ);
        var wtpt = MakeXyzTag(wX, wY, wZ);

        var trc = MakeTrcTag();
        string descStr = $"TrueToneCap {p}";
        var descTag = MakeDescTag(descStr);

        var tags = new (uint sig, byte[] data)[]
        {
            (0x7258595Au, rXyz), (0x6758595Au, gXyz), (0x6258595Au, bXyz),
            (0x72545243u, trc), (0x67545243u, trc), (0x62545243u, trc),
            (0x77747074u, wtpt), (0x64657363u, descTag),
        };

        int headerSize = 128, tagTableSize = 4 + tags.Length * 12;
        int Align4(int v) => (v + 3) / 4 * 4;
        int off = headerSize + tagTableSize;
        var blocks = new List<(int o, byte[] d)>();
        foreach (var t in tags) { blocks.Add((off, t.data)); off += Align4(t.data.Length); }
        int total = off;

        using var ms = new System.IO.MemoryStream(total);
        var w = new System.IO.BinaryWriter(ms);
        WriteBE(w, (uint)total);
        w.Write(new byte[4]);
        WriteBE(w, 0x02300000u);
        WriteBE(w, 0x6D6E7472u); WriteBE(w, 0x52474220u); WriteBE(w, 0x58595A20u);
        w.Write(new byte[12]); WriteBE(w, 0x61636220u);
        w.Write(new byte[4]); w.Write(new byte[4]); w.Write(new byte[4]); w.Write(new byte[4]);
        WriteBE(w, 0UL); WriteBE(w, 0x00000001u);
        WriteS15F16(w, 0.9642f); WriteS15F16(w, 1.0f); WriteS15F16(w, 0.8249f);
        w.Write(new byte[4]); w.Write(new byte[16]); w.Write(new byte[28]);

        WriteBE(w, (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            WriteBE(w, tags[i].sig);
            WriteBE(w, (uint)blocks[i].o);
            WriteBE(w, (uint)tags[i].data.Length);
        }
        foreach (var b in blocks) { w.Write(b.d); int padLen = Align4(b.d.Length) - b.d.Length; if (padLen > 0) w.Write(new byte[padLen]); }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] MakeXyzTag(float x, float y, float z)
    { var b = new byte[20]; WriteBE(b, 0, 0x58595A20u); WriteS15F16(b, 8, x); WriteS15F16(b, 12, y); WriteS15F16(b, 16, z); return b; }
    private static byte[] MakeTrcTag()
    { var b = new byte[20]; WriteBE(b, 0, 0x70617261u); WriteBE(b, 8, (ushort)0); WriteS15F16(b, 12, 2.2f); return b; }
    private static byte[] MakeDescTag(string s)
    { var d = System.Text.Encoding.ASCII.GetBytes(s + "\0"); var b = new byte[8 + d.Length]; WriteBE(b, 0, 0x64657363u); Array.Copy(d, 0, b, 8, d.Length); return b; }

    private static void WriteBE(byte[] buf, int offset, uint v) { buf[offset] = (byte)(v >> 24); buf[offset + 1] = (byte)(v >> 16); buf[offset + 2] = (byte)(v >> 8); buf[offset + 3] = (byte)v; }
    private static void WriteBE(byte[] buf, int offset, ushort v) { buf[offset] = (byte)(v >> 8); buf[offset + 1] = (byte)v; }
    private static void WriteS15F16(byte[] buf, int offset, float v) { WriteBE(buf, offset, (uint)(int)Math.Round(v * 65536f)); }
    private static void WriteBE(System.IO.BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
    private static void WriteBE(System.IO.BinaryWriter w, ulong v) { WriteBE(w, (uint)(v >> 32)); WriteBE(w, (uint)v); }
    private static void WriteBE(System.IO.BinaryWriter w, ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
    private static void WriteS15F16(System.IO.BinaryWriter w, float v) { WriteBE(w, (uint)(int)Math.Round(v * 65536f)); }
    private static (float X, float Y, float Z) XyToXyz(float x, float y) { if (y == 0) return (0, 0, 0); float Y = 1; float X = x * Y / y; float Z = (1 - x - y) * Y / y; return (X, Y, Z); }
}
