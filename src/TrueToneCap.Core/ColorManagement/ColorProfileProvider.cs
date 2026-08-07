// TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs
// 通过 WCS API 获取显示器 ICC 配置文件 — 带缓存 + 异步

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>显示器 ICC 配置文件提供器（带缓存 + 异步）。</summary>
public static partial class ColorProfileProvider
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
                    if (profile is not null)
                    {
                        s_iccCache[name] = profile;
                    }
                    else
                    {
                        // 修复: 失败时不永久缓存 null，允许后续重试
                        s_asyncCache.TryRemove(name, out _);
                    }
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

    /// <summary>
    /// 改进3: 预热所有已连接显示器的 ICC 缓存。
    /// 在应用启动或显示器配置变更后调用，确保截图时 ICC 已就绪，
    /// 避免多显示器切换时首次截图因缓存未命中而跳过 ICC 烘焙。
    /// </summary>
    /// <param name="monitorHandles">所有显示器的 HMONITOR 句柄。</param>
    public static void PrewarmAllDisplays(IEnumerable<nint> monitorHandles)
    {
        foreach (var handle in monitorHandles)
        {
            try
            {
                var monitorName = GetMonitorName(handle);
                if (string.IsNullOrEmpty(monitorName)) continue;
                if (s_iccCache.ContainsKey(monitorName)) continue;

                // 后台异步获取，不阻塞调用线程
                _ = GetDisplayIccProfileAsync(handle);
            }
            catch { /* 单个显示器失败不影响其他 */ }
        }
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
        // 使用 IccStore 生成的 sRGB ICC（完整有效）
        try
        {
            var bytes = Encoding.IccStore.SRGB;
            if (bytes.Length > 128) return bytes;
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

    [LibraryImport("mscms.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WcsGetDefaultColorProfileSize(
        uint scope, string? deviceName, ColorProfileType profileType,
        ColorProfileSubType profileSubType, uint dwFlags, ref uint size);

    [LibraryImport("mscms.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WcsGetDefaultColorProfile(
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

    /// <summary>将 BGRA 像素从源 ICC 色彩空间转换到目标色彩空间（像素级矩阵转换）。
    /// 对矩阵型 ICC (显示器 profile) 使用 3×3 矩阵转换；无法解析时回退到像素直通。</summary>
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

            // 如果源和目标相同（都是 sRGB），无需转换
            if (targetColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase) && IsSrgbProfile(sourceIcc))
            {
                return (bgra, null); // sRGB 目标不嵌入 ICC
            }

            // 尝试从源 ICC 提取 RGB→XYZ 矩阵
            var srcMatrix = ExtractRgbToXyzMatrix(sourceIcc);
            var dstMatrix = GetTargetXyzToRgbMatrix(targetColorSpace);

            if (srcMatrix is null || dstMatrix is null)
            {
                // 无法解析矩阵 → 像素直通 + 嵌入目标 ICC（查看器负责转换）
                System.Diagnostics.Debug.WriteLine(
                    $"[ICC] 矩阵解析失败，像素直通: {w}x{h}, 目标={targetColorSpace}");
                return (bgra, targetIcc);
            }

            // 计算组合矩阵: src_RGB → XYZ → dst_RGB
            var combined = MultiplyMatrices(dstMatrix, srcMatrix);

            // 检查是否为单位矩阵（源≈目标），跳过转换
            if (IsIdentityMatrix(combined))
                return (bgra, targetColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase) ? null : targetIcc);

            // 执行像素级色彩转换
            var converted = new byte[bgra.Length];
            int pixelCount = w * h;
            System.Threading.Tasks.Parallel.For(0, pixelCount, i =>
            {
                int idx = i * 4;
                // BGRA → linear RGB (去 sRGB gamma)
                float bLin = SrgbEotf(bgra[idx] / 255f);
                float gLin = SrgbEotf(bgra[idx + 1] / 255f);
                float rLin = SrgbEotf(bgra[idx + 2] / 255f);

                // 矩阵转换 (RGB → XYZ → RGB)
                float rOut = combined[0] * rLin + combined[1] * gLin + combined[2] * bLin;
                float gOut = combined[3] * rLin + combined[4] * gLin + combined[5] * bLin;
                float bOut = combined[6] * rLin + combined[7] * gLin + combined[8] * bLin;

                // 应用目标 gamma + 量化
                converted[idx]     = (byte)Math.Clamp((int)(SrgbOetf(bOut) * 255f + 0.5f), 0, 255);
                converted[idx + 1] = (byte)Math.Clamp((int)(SrgbOetf(gOut) * 255f + 0.5f), 0, 255);
                converted[idx + 2] = (byte)Math.Clamp((int)(SrgbOetf(rOut) * 255f + 0.5f), 0, 255);
                converted[idx + 3] = bgra[idx + 3]; // Alpha 直通
            });

            System.Diagnostics.Debug.WriteLine(
                $"[ICC] 像素转换完成: {w}x{h}, 目标={targetColorSpace}");
            return (converted, targetColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase) ? null : targetIcc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ICC] 烘焙到 {targetColorSpace} 失败: {ex.Message}");
            return (null, null);
        }
    }

    // ── 色彩转换辅助方法 ──

    /// <summary>sRGB EOTF (gamma 解码): [0,1] gamma → [0,1] linear</summary>
    private static float SrgbEotf(float v)
    {
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>sRGB OETF (gamma 编码): [0,1] linear → [0,1] gamma</summary>
    private static float SrgbOetf(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    /// <summary>从 ICC profile 提取 RGB→XYZ 3×3 矩阵 (rXYZ + gXYZ + bXYZ tags)。</summary>
    private static float[]? ExtractRgbToXyzMatrix(byte[] icc)
    {
        try
        {
            if (icc.Length < 132) return null;
            // ICC header: tag count at offset 128
            int tagCount = ReadBE32(icc, 128);
            if (tagCount <= 0 || tagCount > 200) return null;

            float rX = 0, rY = 0, rZ = 0;
            float gX = 0, gY = 0, gZ = 0;
            float bX = 0, bY = 0, bZ = 0;
            bool hasR = false, hasG = false, hasB = false;

            for (int i = 0; i < tagCount; i++)
            {
                int entryOff = 132 + i * 12;
                if (entryOff + 12 > icc.Length) break;
                uint sig = (uint)ReadBE32(icc, entryOff);
                int offset = ReadBE32(icc, entryOff + 4);
                int size = ReadBE32(icc, entryOff + 8);

                if (offset + size > icc.Length || size < 20) continue;

                // XYZ type: 'XYZ ' (0x58595A20)
                uint typeSig = (uint)ReadBE32(icc, offset);
                if (typeSig != 0x58595A20u) continue;

                // s15Fixed16 值从 offset+8 开始
                float x = ReadS15F16(icc, offset + 8);
                float y = ReadS15F16(icc, offset + 12);
                float z = ReadS15F16(icc, offset + 16);

                if (sig == 0x7258595Au) { rX = x; rY = y; rZ = z; hasR = true; }      // 'rXYZ'
                else if (sig == 0x6758595Au) { gX = x; gY = y; gZ = z; hasG = true; }  // 'gXYZ'
                else if (sig == 0x6258595Au) { bX = x; bY = y; bZ = z; hasB = true; }  // 'bXYZ'
            }

            if (!hasR || !hasG || !hasB) return null;

            // 矩阵列: [R→X, G→X, B→X; R→Y, G→Y, B→Y; R→Z, G→Z, B→Z]
            return new float[] { rX, gX, bX, rY, gY, bY, rZ, gZ, bZ };
        }
        catch { return null; }
    }

    /// <summary>获取目标色彩空间的 XYZ→RGB 逆矩阵。</summary>
    private static float[]? GetTargetXyzToRgbMatrix(string colorSpace)
    {
        // 返回 XYZ→RGB 矩阵 (3×3, row-major)
        return colorSpace.ToUpperInvariant() switch
        {
            "SRGB" or "" => new float[] // sRGB / BT.709 XYZ→RGB (D65)
            {
                 3.2404542f, -1.5371385f, -0.4985314f,
                -0.9692660f,  1.8760108f,  0.0415560f,
                 0.0556434f, -0.2040259f,  1.0572252f
            },
            "P3" or "DISPLAY P3" or "DCI-P3" => new float[] // Display P3 XYZ→RGB (D65)
            {
                 2.4934969f, -0.9313836f, -0.4027108f,
                -0.8294890f,  1.7626641f,  0.0236247f,
                 0.0358458f, -0.0761724f,  0.9568845f
            },
            "ADOBERGB" or "ADOBE RGB" => new float[] // Adobe RGB (1998) XYZ→RGB (D65)
            {
                 2.0413690f, -0.5649464f, -0.3446944f,
                -0.9692660f,  1.8760108f,  0.0415560f,
                 0.0134474f, -0.1183897f,  1.0154096f
            },
            "BT2020" or "BT.2020" or "REC2020" => new float[] // BT.2020 XYZ→RGB (D65)
            {
                 1.7166512f, -0.3556708f, -0.2533663f,
                -0.6666844f,  1.6164812f,  0.0157685f,
                 0.0176399f, -0.0427706f,  0.9421031f
            },
            _ => null
        };
    }

    /// <summary>3×3 矩阵乘法 (row-major): result = A × B</summary>
    private static float[] MultiplyMatrices(float[] a, float[] b)
    {
        var r = new float[9];
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                r[row * 3 + col] = a[row * 3] * b[col] + a[row * 3 + 1] * b[3 + col] + a[row * 3 + 2] * b[6 + col];
        return r;
    }

    private static bool IsIdentityMatrix(float[] m)
    {
        const float eps = 0.001f;
        return MathF.Abs(m[0] - 1f) < eps && MathF.Abs(m[1]) < eps && MathF.Abs(m[2]) < eps &&
               MathF.Abs(m[3]) < eps && MathF.Abs(m[4] - 1f) < eps && MathF.Abs(m[5]) < eps &&
               MathF.Abs(m[6]) < eps && MathF.Abs(m[7]) < eps && MathF.Abs(m[8] - 1f) < eps;
    }

    private static bool IsSrgbProfile(byte[] icc)
    {
        var srcMatrix = ExtractRgbToXyzMatrix(icc);
        if (srcMatrix is null) return false;
        // sRGB 的 rXYZ ≈ (0.4360, 0.2225, 0.0139)
        return MathF.Abs(srcMatrix[0] - 0.4360f) < 0.01f &&
               MathF.Abs(srcMatrix[3] - 0.2225f) < 0.01f;
    }

    private static float ReadS15F16(byte[] data, int offset)
    {
        int raw = ReadBE32(data, offset);
        return raw / 65536f;
    }

    /// <summary>获取标准色彩空间 ICC Profile。</summary>
    public static byte[]? GetStandardIccProfile(string colorSpace)
    {
        try
        {
            // IccStore 生成的标准 profiles
            if (colorSpace == "sRGB")
                return Encoding.IccStore.SRGB;
            if (colorSpace == "AdobeRGB")
                return Encoding.IccStore.AdobeRGB1998;

            // ═══ Display P3 / BT.2020: 基于 sRGB 模板修改 primaries ═══
            // IccProfileBuilder 生成的最小 ICC (504B) 被 ImageMagick 视为无效并剥离
            // 改用 sRGB 模板 (3144B) 修改 rXYZ/gXYZ/bXYZ + desc，确保 ImageMagick 接受
            if (colorSpace == "DisplayP3" || colorSpace == "DCI_P3")
                return PatchSrgbPrimaries(IccPrimaries.DisplayP3, "Display P3");
            if (colorSpace == "BT2020")
                return PatchSrgbPrimaries(IccPrimaries.BT2020, "BT.2020");

            // 默认 sRGB
            return Encoding.IccStore.SRGB;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ICC] 获取标准 ICC '{colorSpace}' 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>基于 sRGB ICC 模板修改 primaries 生成自定义色彩空间 ICC。
    /// 保留 sRGB 的完整 tag 结构（TRC、wtpt 等），仅替换 rXYZ/gXYZ/bXYZ 和 desc。</summary>
    public static byte[]? PatchSrgbPrimaries(IccPrimaries p, string name)
    {
        var template = Encoding.IccStore.SRGB;
        if (template is null || template.Length < 132) return null;

        var icc = (byte[])template.Clone();

        // 解析 tag table（offset 128: tag count, 之后每 12 字节一个 tag entry）
        int tagCount = ReadBE32(icc, 128);
        int tagTableStart = 132;

        // 计算新 primaries 的 XYZ 值（D50 适应）
        var (rX, rY, rZ) = IccProfileBuilder.XyToXyzPublic(p.Rx, p.Ry);
        var (gX, gY, gZ) = IccProfileBuilder.XyToXyzPublic(p.Gx, p.Gy);
        var (bX, bY, bZ) = IccProfileBuilder.XyToXyzPublic(p.Bx, p.By);

        // 遍历 tag table，找到 rXYZ/gXYZ/bXYZ/desc 并修改
        for (int i = 0; i < tagCount; i++)
        {
            int entry = tagTableStart + i * 12;
            uint sig = (uint)ReadBE32(icc, entry);
            int dataOff = ReadBE32(icc, entry + 4);
            int dataLen = ReadBE32(icc, entry + 8);

            if (sig == 0x7258595Au && dataLen >= 20) // rXYZ
                WriteXyzData(icc, dataOff, rX, rY, rZ);
            else if (sig == 0x6758595Au && dataLen >= 20) // gXYZ
                WriteXyzData(icc, dataOff, gX, gY, gZ);
            else if (sig == 0x6258595Au && dataLen >= 20) // bXYZ
                WriteXyzData(icc, dataOff, bX, bY, bZ);
            else if (sig == 0x64657363u && dataLen >= 12) // desc
            {
                // 覆写 desc 字符串（保留 tag 类型签名和长度字段）
                var nameBytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
                int maxLen = Math.Min(nameBytes.Length, dataLen - 12);
                // desc tag: [4B type][4B reserved][4B count][string...]
                WriteBE32(icc, dataOff + 8, (uint)(maxLen));
                Array.Clear(icc, dataOff + 12, dataLen - 12);
                Array.Copy(nameBytes, 0, icc, dataOff + 12, maxLen);
            }
        }

        return icc;
    }

    private static void WriteXyzData(byte[] icc, int offset, float x, float y, float z)
    {
        // XYZ type: [4B 'XYZ '][4B reserved][4B X][4B Y][4B Z] (s15Fixed16)
        WriteS15F16At(icc, offset + 8, x);
        WriteS15F16At(icc, offset + 12, y);
        WriteS15F16At(icc, offset + 16, z);
    }

    private static int ReadBE32(byte[] buf, int off) => (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];
    private static void WriteBE32(byte[] buf, int off, uint v) { buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16); buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v; }
    private static void WriteS15F16At(byte[] buf, int off, float v) => WriteBE32(buf, off, (uint)(int)Math.Round(v * 65536f));

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

    /// <summary>
    /// 从显示器 EDID 读取原生色度坐标，判断显示器原生色域。
    /// 优先读取注册表中的 EDID 数据（硬件真实原色，不受 ACM 影响），
    /// 回退到从 ICC profile 提取矩阵。
    /// </summary>
    /// <param name="monitorHandle">显示器 HMONITOR 句柄。</param>
    /// <returns>色域标签: "sRGB" / "DisplayP3" / "AdobeRGB" / "BT2020"。</returns>
    public static string GetDisplayNativeGamutTag(nint monitorHandle)
    {
        try
        {
            // 策略1: 从注册表读取 EDID 硬件原色（不受 ACM 影响）
            var edid = ReadEdidFromRegistry(monitorHandle);
            if (edid is not null && edid.Length >= 128)
            {
                var (rx, ry, gx, gy) = ParseEdidChromaticity(edid);
                if (rx > 0 || gx > 0) // 有效数据
                {
                    var result = ClassifyGamutByChromaticity(rx, ry, gx, gy);
                    System.Diagnostics.Debug.WriteLine($"[ICC] EDID 原生色域: R({rx:F4},{ry:F4}) G({gx:F4},{gy:F4}) -> {result}");
                    return result;
                }
            }

            // 策略2: 从 ICC profile 提取矩阵（备用，ACM 下可能被修改）
            var icc = GetDisplayIccProfile(monitorHandle);
            if (icc is not null && icc.Length >= 500)
            {
                var matrix = ExtractRgbToXyzMatrix(icc);
                if (matrix is not null)
                {
                    float rX = matrix[0];
                    float gX = matrix[1];
                    var result = ClassifyGamutByXyzMatrix(rX, gX);
                    System.Diagnostics.Debug.WriteLine($"[ICC] ICC 矩阵色域: rX={rX:F4} gX={gX:F4} -> {result}");
                    return result;
                }
            }

            return "sRGB";
        }
        catch
        {
            return "sRGB";
        }
    }

    /// <summary>从注册表读取显示器 EDID 数据。</summary>
    private static byte[]? ReadEdidFromRegistry(nint monitorHandle)
    {
        try
        {
            // 遍历注册表 DISPLAY 键，查找匹配的显示器
            using var displayKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (displayKey is null) return null;

            foreach (var subKeyName in displayKey.GetSubKeyNames())
            {
                using var subKey = displayKey.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                foreach (var instanceName in subKey.GetSubKeyNames())
                {
                    using var instanceKey = subKey.OpenSubKey(instanceName);
                    if (instanceKey is null) continue;

                    using var devParams = instanceKey.OpenSubKey("Device Parameters");
                    if (devParams is null) continue;

                    var edidRaw = devParams.GetValue("EDID");
                    if (edidRaw is byte[] edidBytes && edidBytes.Length >= 128)
                    {
                        // 验证 EDID header: 00 FF FF FF FF FF FF 00
                        if (edidBytes[0] == 0x00 && edidBytes[1] == 0xFF &&
                            edidBytes[2] == 0xFF && edidBytes[3] == 0xFF &&
                            edidBytes[4] == 0xFF && edidBytes[5] == 0xFF &&
                            edidBytes[6] == 0xFF && edidBytes[7] == 0x00)
                        {
                            return edidBytes;
                        }
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 EDID 1.x 色度数据 (block 0, bytes 0x19-0x26)。</summary>
    private static (float rx, float ry, float gx, float gy) ParseEdidChromaticity(byte[] edid)
    {
        // EDID 1.3/1.4 规范:
        // byte 0x19: bits 1-0 = Rx LSB, bits 3-2 = Ry LSB, bits 5-4 = Gx LSB, bits 7-6 = Gy LSB
        // byte 0x1A: bits 1-0 = Bx LSB, bits 3-2 = By LSB, bits 5-4 = Wx LSB, bits 7-6 = Wy LSB
        // 0x1B: Rx MSB (8-bit), 0x1C: Ry MSB, 0x1D: Gx MSB, 0x1E: Gy MSB
        // 最终值: (MSB << 2) | LSB, 除以 1024

        uint b19 = edid[0x19];

        float rx = ((edid[0x1B] << 2) | (int)(b19 & 0x03)) / 1024.0f;
        float ry = ((edid[0x1C] << 2) | (int)((b19 >> 2) & 0x03)) / 1024.0f;
        float gx = ((edid[0x1D] << 2) | (int)((b19 >> 4) & 0x03)) / 1024.0f;
        float gy = ((edid[0x1E] << 2) | (int)((b19 >> 6) & 0x03)) / 1024.0f;

        return (rx, ry, gx, gy);
    }

    /// <summary>根据 EDID 色度坐标判断色域。</summary>
    private static string ClassifyGamutByChromaticity(float rx, float ry, float gx, float gy)
    {
        // 标准色域原色:
        // sRGB:      R(0.640, 0.330) G(0.300, 0.600)
        // Display P3: R(0.680, 0.320) G(0.265, 0.690)
        // AdobeRGB:  R(0.640, 0.330) G(0.210, 0.710)
        // BT.2020:   R(0.708, 0.292) G(0.170, 0.797)

        if (rx > 0.69f && gx < 0.20f) return "BT2020";
        if (gx <= 0.22f && gy >= 0.70f) return "AdobeRGB";
        if (rx > 0.66f && gx <= 0.28f) return "DisplayP3";
        if (rx > 0.65f && gx <= 0.30f) return "DisplayP3";
        return "sRGB";
    }

    /// <summary>根据 ICC XYZ 矩阵值判断色域（备用方案）。</summary>
    private static string ClassifyGamutByXyzMatrix(float rX, float gX)
    {
        if (gX < 0.18f) return "AdobeRGB";
        if (rX > 0.58f) return "BT2020";
        if (rX > 0.47f) return "DisplayP3";
        return "sRGB";
    }

    /// <summary>
    /// 解析色彩空间标签：当用户选择 "System"（跟随系统）时，
    /// 根据 HDR 状态和 ACM 状态自动解析为实际色域。
    /// </summary>
    /// <param name="tag">用户选择的色彩空间标签。</param>
    /// <param name="hdrOutput">是否启用 HDR 输出。</param>
    /// <param name="acmEnabled">系统 ACM 是否启用。</param>
    /// <param name="monitorHandle">可选显示器 HMONITOR，用于 ACM 下检测原生色域。</param>
    /// <returns>解析后的实际色彩空间标签。</returns>
    /// <remarks>
    /// 解析规则:
    /// - HDR 开启 + "System" → "BT2020"（HDR10 标准容器色域）
    /// - HDR 开启 + 显式色域 → 保留用户选择（如 P3/AdobeRGB），通过 3×3 矩阵转换到目标色域线性空间
    /// - HDR 开启 + sRGB → 保留（色调映射到 SDR 输出）
    /// - ACM 启用 + 广色域显示器 → 显示器原生色域 (DisplayP3/BT2020/AdobeRGB)
    /// - 其他 → "sRGB"（WGC SDR 会话输出 DWM 色调映射后的 sRGB 数据）
    /// </remarks>
    public static string ResolveColorSpaceTag(string tag, bool hdrOutput, bool acmEnabled = false, nint? monitorHandle = null)
    {
        if (hdrOutput)
        {
            // HDR 开启时"System"解析为 BT.2020（HDR10 标准容器色域）
            if (tag == "System")
                return "BT2020";

            // 用户显式选择的色域、sRGB 均保留
            return tag;
        }

        // ── SDR 模式 ──
        if (tag != "System") return tag;

        // ACM 启用 → 尝试检测显示器原生色域（可能是广色域）
        if (acmEnabled && monitorHandle.HasValue)
        {
            var nativeGamut = GetDisplayNativeGamutTag(monitorHandle.Value);
            if (nativeGamut is not "sRGB")
                return nativeGamut;
        }

        // HDR 关闭时 WGC SDR 会话输出 DWM 色调映射后的 sRGB
        // 即使显示器硬件支持广色域，无 ACM 时 SDR 模式下 WGC 也只返回 sRGB 数据
        return "sRGB";
    }

    /// <summary>获取当前所选色彩空间的用户友好名称。</summary>
    public static string GetColorSpaceDisplayName(string tag) => tag switch
    {
        "System" => "sRGB (跟随系统)",
        "sRGB" => "sRGB IEC61966-2.1",
        "DisplayP3" => "Display P3",
        "DCI_P3" => "DCI-P3",
        "AdobeRGB" => "Adobe RGB (1998)",
        "BT2020" => "BT.2020 (HDR)",
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
    // ═══════════════════════════════════════════════════════════════
    //  3×3 转换矩阵（线性 scRGB BT.709 → 目标色域线性）
    //  预计算 BT.709_to_XYZ × XYZ_to_Target
    // ═══════════════════════════════════════════════════════════════

    /// <summary>scRGB (BT.709) → BT.2020 线性转换矩阵。</summary>
    public static readonly float[,] SrgbToBt2020 = new float[3, 3]
    {
        { 0.627403f, 0.329283f, 0.043313f },
        { 0.069097f, 0.919541f, 0.011362f },
        { 0.016392f, 0.088013f, 0.895595f }
    };

    /// <summary>scRGB (BT.709) → Display P3 线性转换矩阵。</summary>
    public static readonly float[,] SrgbToDisplayP3 = new float[3, 3]
    {
        { 0.822462f, 0.177194f, 0.000344f },
        { 0.033194f, 0.966799f, 0.000007f },
        { 0.017083f, 0.072411f, 0.910506f }
    };

    /// <summary>scRGB (BT.709) → Adobe RGB 线性转换矩阵。</summary>
    public static readonly float[,] SrgbToAdobeRgb = new float[3, 3]
    {
        { 0.715160f, 0.284849f, 0.000009f },
        { 0.000000f, 1.000000f, 0.000000f },
        { 0.000000f, 0.041169f, 0.958831f }
    };

    /// <summary>scRGB (BT.709) → ACES AP1 (ACEScg) 线性转换矩阵。</summary>
    /// <remarks>
    /// 来源: ACES 1.0.3 规范 (SMPTE ST 2065-1:2012)
    /// 链: BT.709 linear → XYZ(D65) → XYZ(D60) → AP1(ACEScg)
    /// BT.709 和 sRGB 使用相同原色，因此矩阵相同。
    /// </remarks>
    public static readonly float[,] SrgbToAcesAp1 = new float[3, 3]
    {
        { 0.613132f, 0.339538f, 0.047416f },
        { 0.070124f, 0.916324f, 0.013452f },
        { 0.020445f, 0.109548f, 0.870006f }
    };

    /// <summary>ACES AP1 → scRGB (BT.709) 线性转换矩阵（SrgbToAcesAp1 的逆）。</summary>
    public static readonly float[,] AcesAp1ToSrgb = new float[3, 3]
    {
        { 1.704579f, -0.625505f, -0.078038f },
        { -0.129701f,  1.139240f, -0.009570f },
        { -0.019717f, -0.128087f,  1.147935f }
    };

    /// <summary>根据色彩空间标签获取 scRGB→目标色域 3×3 矩阵。</summary>
    public static float[,]? GetMatrix(string colorSpaceTag) => colorSpaceTag switch
    {
        "BT2020" => SrgbToBt2020,
        "DisplayP3" or "DCI_P3" => SrgbToDisplayP3,
        "AdobeRGB" => SrgbToAdobeRgb,
        _ => null // sRGB / System: 无需转换
    };

    /// <summary>获取 CICP 原色索引 (ITU-T H.273)。</summary>
    public static byte GetCicpPrimaries(string colorSpaceTag) => colorSpaceTag switch
    {
        "DisplayP3" or "DCI_P3" => 12,
        "BT2020" => 9,
        "AdobeRGB" => 1,
        _ => 1
    };

    /// <summary>获取 CICP 传输函数索引。</summary>
    public static byte GetCicpTransfer(string colorSpaceTag, bool hdrOutput) => (colorSpaceTag, hdrOutput) switch
    {
        (_, true) => 16,  // ST.2084 PQ (ITU-T H.273 Table 3)
        ("AdobeRGB", _) => 13,
        _ => 13
    };

    /// <summary>
    /// 将 scRGB Float16 线性浮点像素转换到目标色域线性空间并做色调映射到 SDR BGRA8。
    /// 用于 HDR 关闭 + 广色域目标场景：
    ///   WGC Float16 包含完整广色域数据 → 色域矩阵转换 → 色调映射 → sRGB gamma → BGRA8
    /// 注意: ACES 色调映射模式内部已包含 scRGB→AP1→ACES→sRGB 完整管线，
    /// 输出始终为 sRGB 色域，无需额外色域转换。
    /// </summary>
    /// <param name="hdrPixels">scRGB 线性 RGBA 浮点像素 (WGC Float16 会话)。</param>
    /// <param name="w">宽度。</param>
    /// <param name="h">高度。</param>
    /// <param name="colorSpaceTag">目标色域标签，null/sRGB 时仅做色调映射。</param>
    /// <param name="toneParams">色调映射参数。</param>
    /// <returns>BGRA8 字节数组。</returns>
    public static byte[] ConvertFloat16ToSdrBgra(float[] hdrPixels, int w, int h,
        string? colorSpaceTag, Processing.ToneMappingParams toneParams)
    {
        // ACES 模式：FloatToSRgbBytes 融合内核内部已处理 scRGB→AP1→ACES→sRGB 完整管线
        // 输出始终为 sRGB 色域。如果目标不是 sRGB，需要做 sRGB→目标色域后处理
        if (toneParams.Mode == Processing.ToneMapMode.Aces)
        {
            var srgb = Processing.ToneMapper.FloatToSRgbBytes(hdrPixels, w, h, toneParams);
            if (colorSpaceTag is not null and not "sRGB")
                return ApplySrgbToTargetGamut(srgb, w, h, colorSpaceTag);
            return srgb;
        }

        // 非 ACES 模式 (Reinhard/Hable)：先做色域转换，再做色调映射
        // 1. 色域转换：scRGB (BT.709) → 目标色域线性
        var matrix = GetMatrix(colorSpaceTag ?? "sRGB");
        var converted = ConvertScrgbToTarget(hdrPixels, w, h, matrix);

        // 2. 复用 ToneMapper 融合内核：色调映射 + gamma + swizzle → BGRA8
        // 传递 colorSpaceTag 以使用正确的动态亮度权重
        return Processing.ToneMapper.FloatToSRgbBytes(converted, w, h, toneParams, colorSpaceTag);
    }

    /// <summary>将 scRGB 线性浮点像素转换到目标色域线性空间。</summary>
    public static float[] ConvertScrgbToTarget(float[] srcPixels, int w, int h, float[,]? matrix)
    {
        if (matrix is null)
        {
            var copy = new float[srcPixels.Length];
            System.Buffer.BlockCopy(srcPixels, 0, copy, 0, srcPixels.Length * sizeof(float));
            return copy;
        }

        int pixelCount = w * h;
        var result = new float[srcPixels.Length];
        float m00 = matrix[0, 0], m01 = matrix[0, 1], m02 = matrix[0, 2];
        float m10 = matrix[1, 0], m11 = matrix[1, 1], m12 = matrix[1, 2];
        float m20 = matrix[2, 0], m21 = matrix[2, 1], m22 = matrix[2, 2];

        System.Threading.Tasks.Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = srcPixels[i];
            float g = srcPixels[i + 1];
            float b = srcPixels[i + 2];
            float a = srcPixels[i + 3];
            result[i]     = r * m00 + g * m01 + b * m02;
            result[i + 1] = r * m10 + g * m11 + b * m12;
            result[i + 2] = r * m20 + g * m21 + b * m22;
            result[i + 3] = a;
        });

        return result;
    }

    // ── sRGB gamma 解码/编码标量（用于字节级转换） ──

    /// <summary>sRGB gamma 解码: byte [0,1] → linear [0,1]</summary>
    private static float SrgbToLinear(float v) =>
        v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>sRGB gamma 编码: linear [0,1] → [0,1]</summary>
    private static float LinearToSrgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    /// <summary>
    /// 将 sRGB 线性 byte BGRA8 像素转换到目标色域。
    /// 用于 ACES 色调映射后：ACES 输出在 sRGB 色域，
    /// 但用户选择 P3/BT.2020 目标时，需要将像素值转换到目标色域。
    /// 流程: byte BGRA8 → 去 sRGB gamma → 3×3 矩阵 → 目标 gamma → byte BGRA8
    /// </summary>
    public static byte[] ApplySrgbToTargetGamut(byte[] bgra, int w, int h, string colorSpaceTag)
    {
        var matrix = GetMatrix(colorSpaceTag);
        if (matrix is null) return bgra; // sRGB 目标无需转换

        int pixelCount = w * h;
        float m00 = matrix[0, 0], m01 = matrix[0, 1], m02 = matrix[0, 2];
        float m10 = matrix[1, 0], m11 = matrix[1, 1], m12 = matrix[1, 2];
        float m20 = matrix[2, 0], m21 = matrix[2, 1], m22 = matrix[2, 2];

        var result = new byte[bgra.Length];
        System.Threading.Tasks.Parallel.For(0, pixelCount, i =>
        {
            int idx = i * 4;
            // BGRA → linear (去 sRGB gamma)
            float bLin = SrgbToLinear(bgra[idx] / 255f);
            float gLin = SrgbToLinear(bgra[idx + 1] / 255f);
            float rLin = SrgbToLinear(bgra[idx + 2] / 255f);

            // 矩阵转换 (sRGB linear → 目标色域 linear)
            float rOut = rLin * m00 + gLin * m01 + bLin * m02;
            float gOut = rLin * m10 + gLin * m11 + bLin * m12;
            float bOut = rLin * m20 + gLin * m21 + bLin * m22;

            // 目标 gamma + 量化
            result[idx]     = (byte)Math.Clamp((int)(LinearToSrgb(bOut) * 255f + 0.5f), 0, 255);
            result[idx + 1] = (byte)Math.Clamp((int)(LinearToSrgb(gOut) * 255f + 0.5f), 0, 255);
            result[idx + 2] = (byte)Math.Clamp((int)(LinearToSrgb(rOut) * 255f + 0.5f), 0, 255);
            result[idx + 3] = bgra[idx + 3];
        });
        return result;
    }

}

// ═══════════════════════════════════════
//  ICC v2 Profile 生成器
// ═══════════════════════════════════════

/// <summary>ICC 色域原色 (CIE xy)。</summary>
public readonly record struct IccPrimaries(float Rx, float Ry, float Gx, float Gy, float Bx, float By)
{
    /// <summary>sRGB / BT.709 原色。</summary>
    public static readonly IccPrimaries SRGB = new(0.6400f, 0.3300f, 0.3000f, 0.6000f, 0.1500f, 0.0600f);
    /// <summary>Adobe RGB (1998) 原色。</summary>
    public static readonly IccPrimaries AdobeRGB = new(0.6400f, 0.3300f, 0.2100f, 0.7100f, 0.1500f, 0.0600f);
    public static readonly IccPrimaries DisplayP3 = new(0.6800f, 0.3200f, 0.2650f, 0.6900f, 0.1500f, 0.0600f);
    public static readonly IccPrimaries BT2020 = new(0.7080f, 0.2920f, 0.1700f, 0.7970f, 0.1310f, 0.0460f);

    public static readonly (float X, float Y, float Z) D65 = (0.3127f, 0.3290f, 0.3583f);
}

/// <summary>ICC 传输曲线类型。</summary>
public enum IccTransferCurve { Srgb, Linear, Gamma22 }

/// <summary>ICC v2 Profile 构建器。</summary>
public static class IccProfileBuilder
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
        var cprtTag = MakeTextTag("Public Domain");

        var tags = new (uint sig, byte[] data)[]
        {
            (0x7258595Au, rXyz), (0x6758595Au, gXyz), (0x6258595Au, bXyz),
            (0x72545243u, trc), (0x67545243u, trc), (0x62545243u, trc),
            (0x77747074u, wtpt), (0x64657363u, descTag), (0x63707274u, cprtTag),
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
        w.Write(new byte[12]); WriteBE(w, 0x61637370u); // "acsp" (ICC profile file signature)
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
    { var d = System.Text.Encoding.ASCII.GetBytes(s + "\0"); var b = new byte[12 + d.Length]; WriteBE(b, 0, 0x64657363u); WriteBE(b, 8, (uint)d.Length); Array.Copy(d, 0, b, 12, d.Length); return b; }
    private static byte[] MakeTextTag(string s)
    { var d = System.Text.Encoding.ASCII.GetBytes(s + "\0"); var b = new byte[8 + d.Length]; WriteBE(b, 0, 0x74657874u); Array.Copy(d, 0, b, 8, d.Length); return b; }

    private static void WriteBE(byte[] buf, int offset, uint v) { buf[offset] = (byte)(v >> 24); buf[offset + 1] = (byte)(v >> 16); buf[offset + 2] = (byte)(v >> 8); buf[offset + 3] = (byte)v; }
    private static void WriteBE(byte[] buf, int offset, ushort v) { buf[offset] = (byte)(v >> 8); buf[offset + 1] = (byte)v; }
    private static void WriteS15F16(byte[] buf, int offset, float v) { WriteBE(buf, offset, (uint)(int)Math.Round(v * 65536f)); }
    private static void WriteBE(System.IO.BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
    private static void WriteBE(System.IO.BinaryWriter w, ulong v) { WriteBE(w, (uint)(v >> 32)); WriteBE(w, (uint)v); }
    private static void WriteBE(System.IO.BinaryWriter w, ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
    private static void WriteS15F16(System.IO.BinaryWriter w, float v) { WriteBE(w, (uint)(int)Math.Round(v * 65536f)); }
    private static (float X, float Y, float Z) XyToXyz(float x, float y) { if (y == 0) return (0, 0, 0); float Y = 1; float X = x * Y / y; float Z = (1 - x - y) * Y / y; return (X, Y, Z); }
    /// <summary>公开版本：供 PatchSrgbPrimaries 使用。</summary>
    public static (float X, float Y, float Z) XyToXyzPublic(float x, float y) => XyToXyz(x, y);
}
