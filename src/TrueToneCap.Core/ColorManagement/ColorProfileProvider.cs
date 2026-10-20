// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs
// 通过 WCS API 获取显示器 ICC 配置文件 — 带缓存 + 异步

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>显示器 ICC 配置文件提供器（带缓存 + 异步）。</summary>
public static partial class ColorProfileProvider
{
    // ═══ ICC 缓存：按显示器 EDID 校验和缓存，避免同名不同物理显示器导致错缓存 ═══
    private static readonly ConcurrentDictionary<string, byte[]?> s_iccCache = new();
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> s_asyncCache = new();

    /// <summary>获取显示器唯一缓存键：设备名 + 物理指纹（防止拔插/重排导致同名错缓存）</summary>
    private static string GetMonitorCacheKey(nint monitorHandle)
    {
        var monitorName = GetMonitorName(monitorHandle);
        if (string.IsNullOrEmpty(monitorName)) return "";

        // 物理指纹（三级回退：EDID CRC32 → PnP ID → 设备名）
        string fingerprint = GetMonitorFingerprint(monitorHandle);
        return string.IsNullOrEmpty(fingerprint) ? monitorName : $"{monitorName}#{fingerprint}";
    }

    /// <summary>获取指定显示器的默认 ICC 配置文件内容（同步读缓存，永不阻塞）。</summary>
    /// <remarks>缓存命中立即返回；缓存未命中时启动后台预热并立即返回 null，
    /// 绝不阻塞调用线程（避免 WCS API 数秒级同步调用卡住截图/覆盖层弹出）。
    /// 编码保存时再次调用通常已命中缓存。</remarks>
    public static byte[]? GetDisplayIccProfile(nint monitorHandle)
    {
        try
        {
            var cacheKey = GetMonitorCacheKey(monitorHandle);
            if (string.IsNullOrEmpty(cacheKey)) return null;

            // 缓存命中 → 零开销返回
            if (s_iccCache.TryGetValue(cacheKey, out var cached))
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
            var cacheKey = GetMonitorCacheKey(monitorHandle);
            if (string.IsNullOrEmpty(cacheKey)) return Task.FromResult<byte[]?>(null);

            // 缓存命中 → 立即返回
            if (s_iccCache.TryGetValue(cacheKey, out var cached))
                return Task.FromResult(cached);

            // 异步获取（防止同一显示器并发重复调 WCS）
            return s_asyncCache.GetOrAdd(cacheKey, key =>
                Task.Run(() =>
                {
                    var profile = GetIccProfileFromWcs(key.Split('#')[0]); // 只用设备名查 WCS
                    if (profile is not null)
                    {
                        s_iccCache[key] = profile;
                    }
                    else
                    {
                        // 修复: 失败时不永久缓存 null，允许后续重试
                        s_asyncCache.TryRemove(key, out _);
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
                var cacheKey = GetMonitorCacheKey(handle);
                if (string.IsNullOrEmpty(cacheKey)) continue;
                if (s_iccCache.ContainsKey(cacheKey)) continue;

                // 后台异步获取，不阻塞调用线程
                _ = GetDisplayIccProfileAsync(handle);
            }
            catch { /* 单个显示器失败不影响其他 */ }
        }
    }

    // ═══ sRGB ICC 缓存（完整配置文件，仅加载一次）═══
    private static byte[]? s_srgbIccCache;
    private static readonly object s_srgbLock = new();

    /// <summary>本仓库自产 ICC 的 cprt 文本。它会随<b>用户导出的产物</b>分发，
    /// 所以必须与项目实际许可一致（<c>LICENSE</c> 是 Apache-2.0），且不得含任何第三方版权串。</summary>
    internal const string GeneratedIccCopyright = "Copyright 2026 TrueToneCap contributors - Apache-2.0";

    /// <summary>获取默认 sRGB ICC 配置文件（完整的 IEC 61966-2.1 配置文件）。</summary>
    /// <remarks>
    /// ═══ 2026-09-20 许可合规：只用手写件，不再读系统那份 ═══
    /// 旧策略优先读 <c>System32\spool\drivers\color\sRGB Color Space Profile.icm</c>。两个后果：
    /// ① 那份的 <c>cprt</c> 是 "Copyright (c) 1998 Hewlett-Packard Company"、<c>creator</c> 是 "HP  "，
    ///    它会随<b>用户导出的产物</b>分发（PNG iCCP / TIFF / AVIF colr(prof) / JPEG APP2 全部实测命中）。
    ///    更糟的是 <see cref="PatchSrgbPrimaries"/> 以它为模板 Clone 而只覆写 rXYZ/gXYZ/bXYZ+desc，
    ///    残留标签把 Display P3 / BT.2020 / Adobe RGB 也一并染上 HP 版权串。
    /// ② 模板取自本机系统目录 ⇒ 换一台机器（或该文件缺失/损坏而落到回退分支）产物字节就不同，
    ///    与 <c>build-info.json</c> 主张的可追溯性自相矛盾。
    /// <para>切换是<b>色度学中性</b>的：手写件的原色列、wtpt、TRC 数值本来就照那份实测抄来
    /// （见 <see cref="BuildValidSRgbIcc"/> 的注释），变的只有版权归属与跨机一致性。
    /// 判据：<c>ColorPipelineTests</c> 的 IccProvenance 用例 + 结构判据 <c>IsEmbeddableIcc</c>。</para>
    /// </remarks>
    public static byte[] GetDefaultSRgbIcc()
    {
        if (s_srgbIccCache is not null) return s_srgbIccCache;

        lock (s_srgbLock)
        {
            s_srgbIccCache ??= BuildValidSRgbIcc();
            return s_srgbIccCache;
        }
    }

    /// <summary>构建 sRGB ICC 配置文件 (v2.1) —— 本仓库自产，不引用任何第三方 profile 的字节。</summary>
    /// <remarks>
    /// ═══ 历史坑（2026-09-15 修复）：无限递归 ═══
    /// 本方法曾无条件调用 <see cref="Encoding.IccStore.SRGB"/>，而 <c>IccStore.SRGB</c>
    /// （IccStore.cs:26）反向调用 <see cref="GetDefaultSRgbIcc"/>，后者又回到本方法 ⇒ 构成环。
    /// 加重因素：① <c>lock</c> 可重入，不会自锁；② <c>IccStore._srgb</c> 要等调用返回才置位，
    /// 重入时仍为 null。两者叠加 ⇒ 系统 sRGB ICC 缺失（精简系统 / 文件损坏）时
    /// **必然栈溢出、进程直接崩溃**，且 catch 拦不住（StackOverflowException 不可捕获）。
    /// 当时靠 ThreadStatic 哨兵阻断；现在本方法已完全不回指 IccStore，环从源头消失，哨兵一并删除。
    /// <para>⚠ 不许再把本方法的调用方改成 <c>IccStore.SRGB</c>：那等于把环重新接上。</para>
    /// <para>⚠ <c>internal</c>（原 private）是给判据看的：这份手写件现在是<b>唯一</b>来源，
    /// 它若不合格 ⇒ 广色域声明在所有机器上一起消失 ⇒ 必须被直接断言
    /// （见 <c>Test_IccEmbeddableIsStructuralNotLength</c> 第 ⑧ 项）。</para>
    /// </remarks>
    internal static byte[] BuildValidSRgbIcc()
    {
        // 最终回退: 手写最小有效 sRGB ICC v2.1 配置文件
        // 结构: Header(128) + TagTable(12+10*12=132) + TagData
        using var ms = new MemoryStream(3200);
        using var bw = new BinaryWriter(ms);

        // ═══ 字节序：BinaryWriter 一律写**小端**，而 ICC 全程**大端** ═══
        // 非零整数一律走 WriteBE16/WriteBE32。2026-09-20 修正：本方法此前有 5 处裸
        // bw.Write(int/ushort)（profile 尺寸回填、日期、头部 PCS 照明体、tag 数、
        // tag 表的 offset/size），产出的字节序是反的 ⇒ 这份"兜底 sRGB ICC"从来没合法过。
        // 之所以一直没被发现：GetDefaultSRgbIcc 优先读系统 profile，本方法在正常 Windows 上
        // 根本跑不到（判据 ⑨ 当时也因此是一条假绿灯）。
        // 占位，最后回填 profile size
        WriteBE32(bw, 0); // profile size (offset 0)
        WriteBE32(bw, 0); // preferred CMM
        bw.Write(new byte[] { 0x02, 0x10, 0x00, 0x00 }); // version 2.1.0
        bw.Write("mntr"u8); // device class
        bw.Write("RGB "u8); // color space
        bw.Write("XYZ "u8); // PCS
        // date/time（各字段是大端 ushort）
        WriteBE16(bw, 2024); WriteBE16(bw, 1); WriteBE16(bw, 1);
        WriteBE16(bw, 0); WriteBE16(bw, 0); WriteBE16(bw, 0);
        bw.Write("acsp"u8); // profile file signature
        WriteBE32(bw, 0); // primary platform
        WriteBE32(bw, 0); // profile flags
        WriteBE32(bw, 0); // device manufacturer
        WriteBE32(bw, 0); // device model
        WriteBE32(bw, 0); WriteBE32(bw, 0); // device attributes (8 字节)
        WriteBE32(bw, 0); // rendering intent (perceptual)
        // PCS illuminant (D50): X=0.9642, Y=1.0, Z=0.8249 (s15Fixed16)
        WriteBE32(bw, 0x0000F6D6); WriteBE32(bw, 0x00010000); WriteBE32(bw, 0x0000D32D);
        WriteBE32(bw, 0); // profile creator
        bw.Write(new byte[16]); // profile ID (MD5, 留空)
        bw.Write(new byte[28]); // reserved

        // Tag table: 10 tags
        int tagCount = 10;
        WriteBE32(bw, tagCount);

        // 计算数据区起始 (header 128 + tag count 4 + tags 10*12=120 = 252, 对齐到 4)
        int dataStart = 128 + 4 + tagCount * 12;
        dataStart = (dataStart + 3) & ~3;

        // 准备各标签数据
        byte[] descData = BuildDescTag("sRGB IEC61966-2.1");
        byte[] cprtData = BuildTextTag(GeneratedIccCopyright);
        // ═══ 2026-09-19 缺陷 C：三个原色列必须是 D50 适配值，此前写的是 D65 未适配值 ═══
        // ICC 的 PCS 是 D50 ⇒ rXYZ/gXYZ/bXYZ 必须是"适配到 D50 之后"的列向量。旧写法用的是
        // IEC 61966-2-1 那张【D65 引用】的矩阵 (0.4124/0.2126/0.0193…)，三列之和 = D65 白
        // (0.9505,1,1.0890)，与文件自己声明的 D50 PCS 对不上 ⇒ 任何按标签算 RGB↔XYZ 的实现
        // 都会拿到一张差 ~2-4%（蓝/黄方向）的矩阵。
        // 下面的数值不是凭记忆写的：实测本机 C:\Windows\System32\spool\drivers\color\
        //   sRGB Color Space Profile.icm（微软随系统的 sRGB，v2.1，3144B，17 标签），
        //   rXYZ=(0.4361,0.2225,0.0139) gXYZ=(0.3851,0.7169,0.0971) bXYZ=(0.1431,0.0606,0.7141)，
        //   三列之和=(0.9643,1.0000,0.8251)=D50 白。自检见 ColorPipelineTests 的 SrgbIcc 判据。
        // ⚠ wtpt 保持 D65 原值【不是】漏改：同一份微软 profile 的 wtpt 就是 (0.9505,1.0000,1.0891)
        //   —— 媒体白点按未适配值记录是它的既有做法，与原色列的适配规则不同，别"顺手统一"。
        byte[] wtptData = BuildXyzTag(0.9505f, 1.0f, 1.0891f); // D65 媒体白点（与系统 profile 一致）
        byte[] bkptData = BuildXyzTag(0f, 0f, 0f);
        byte[] rXYZData = BuildXyzTag(0.4361f, 0.2225f, 0.0139f);
        byte[] gXYZData = BuildXyzTag(0.3851f, 0.7169f, 0.0971f);
        byte[] bXYZData = BuildXyzTag(0.1431f, 0.0606f, 0.7141f);
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
            WriteBE32(bw, offsets[i]);
            WriteBE32(bw, tags[i].data.Length);
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
        WriteBE32(bw, totalSize);

        return ms.ToArray();
    }

    /// <summary>BinaryWriter 只会写小端，ICC 要求大端 ⇒ 非零整数一律经这两个辅助。</summary>
    private static void WriteBE32(BinaryWriter bw, int v)
    {
        bw.Write((byte)(v >>> 24)); bw.Write((byte)(v >> 16));
        bw.Write((byte)(v >> 8)); bw.Write((byte)v);
    }

    private static void WriteBE16(BinaryWriter bw, int v)
    {
        bw.Write((byte)(v >> 8)); bw.Write((byte)v);
    }

    private static byte[] BuildDescTag(string text)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("desc"u8); bw.Write(0); // signature + reserved
        var ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        // ⚠️ 2026-08-09 修复: ICC 规范大端, BinaryWriter 小端 → 手动大端
        bw.Write((byte)(ascii.Length >> 24)); bw.Write((byte)(ascii.Length >> 16));
        bw.Write((byte)(ascii.Length >> 8)); bw.Write((byte)ascii.Length);
        bw.Write(ascii);
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
        // ⚠️ 2026-08-09 修复: ICC 规范大端 (s15Fixed16), BinaryWriter 小端 → 手动大端
        foreach (int v in new[] { (int)Math.Round(x * 65536), (int)Math.Round(y * 65536), (int)Math.Round(z * 65536) })
        {
            bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16)); bw.Write((byte)(v >> 8)); bw.Write((byte)v);
        }
        return ms.ToArray();
    }

    private static byte[] BuildCurveTag()
    {
        // sRGB tone curve: 256 采样点
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("curv"u8); bw.Write(0);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)1); bw.Write((byte)0); // count=256 大端
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            float linear = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            var u16 = (ushort)Math.Clamp((int)(linear * 65535 + 0.5f), 0, 65535);
            bw.Write((byte)(u16 >> 8)); bw.Write((byte)(u16 & 0xFF));
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

    // ── EDID 获取（用于物理显示器标识，防止缓存键碰撞）──

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    /// <summary>
    /// 获取显示器物理指纹（三级回退：EDID CRC32 → PnP ID → 设备名），用于缓存键区分不同物理显示器。
    /// <para>
    /// 设计权衡：
    /// 1. EDID CRC32 (注册表) = 面板级唯一标识，含序列号/制造周/白点，最精确。
    ///    注册表路径: HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<PnP_ID>\<instance>\Device Parameters\EDID
    /// 2. PnP ID = 型号级标识 (如 DEL27A0B)，同型号不同面板会碰撞，但比设备名稳定。
    /// 3. 设备名 (\\.\DISPLAY1) = 逻辑索引，拔插/重排会变，仅作最后兜底。
    /// </para>
    /// </summary>
    private static string GetMonitorFingerprint(nint monitorHandle)
    {
        try
        {
            var monitorInfo = new MONITORINFOEXW();
            monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
            if (!GetMonitorInfoW(monitorHandle, ref monitorInfo)) return "";

            // 与 EDID 读取同一条纪律：一个设备名下可能挂着多台监视器（内屏 + 外接），
            // 取 index 0 会把指纹算到没在用的那一台上 ⇒ 缓存键跟着错（见 TryGetActiveMonitor）。
            if (!TryGetActiveMonitor((monitorInfo.szDevice ?? "").Trim(), 0u, out var dd)) return "";

            string deviceId = dd.DeviceID ?? "";
            string pnpId = "";

            // ── Step 1: 解析 PnP ID (从 DeviceID "MONITOR\<PnP_ID>\<instance>" 或 "DISPLAY\<PnP_ID>\<instance>") ──
            var segments = deviceId.Split('\\');
            if (segments.Length >= 2 && !string.IsNullOrEmpty(segments[1]))
            {
                pnpId = segments[1];
            }
            else if (segments.Length >= 3 && !string.IsNullOrEmpty(segments[2]))
            {
                // 罕见格式: "ROOT\<PnP_ID>\<something>"
                pnpId = segments[2];
            }

            // ── Step 2: 尝试读注册表 EDID (最精确：面板序列号+制造周+白点色度) ──
            if (!string.IsNullOrEmpty(pnpId))
            {
                try
                {
                    // 枚举该 PnP ID 下所有实例子键
                    using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnpId}");
                    if (enumKey is not null)
                    {
                        foreach (var subName in enumKey.GetSubKeyNames())
                        {
                            using var instanceKey = enumKey.OpenSubKey(subName);
                            if (instanceKey?.OpenSubKey("Device Parameters")?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                            {
                                uint crc = 0xFFFFFFFF;
                                foreach (var b in edid.Take(128))
                                    crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
                                return (~crc).ToString("X8");  // EDID CRC32
                            }
                        }
                    }
                }
                catch { /* 忽略注册表访问错误，继续降级 */ }
            }

            // ── Step 3: 回退到 PnP ID ──
            if (!string.IsNullOrEmpty(pnpId))
                return pnpId;

            // ── Step 4: 最后兜底：设备名 (\\.\DISPLAYx) ──
            if (!string.IsNullOrEmpty(monitorInfo.szDevice))
                return monitorInfo.szDevice;

            // ── Step 5: 兜底尾声：DeviceID 末段十六进制片段 ──
            foreach (var part in deviceId.Split('\\', '&').Reverse())
            {
                if (part.Length >= 8 && uint.TryParse(part[^8..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint hash))
                    return hash.ToString("X8");
            }
        }
        catch { }
        return "";
    }

    // CRC32 表 (标准多项式 0xEDB88320) - 供 EDID CRC32 计算
    private static readonly uint[] Crc32Table = new uint[256];
    static ColorProfileProvider()
    {
        const uint poly = 0xEDB88320;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            Crc32Table[i] = crc;
        }
    }

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
                CoreLog.Warn("ICC", $"目标色彩空间 '{targetColorSpace}' 无标准 ICC，回退 sRGB", LogCategory.Encoding);
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

            // ═══ 2026-09-19 缺陷 C 的真正根因：两个操作数不在同一个 PCS 里 ═══
            // ExtractRgbToXyzMatrix 读出的是 **D50 PCS** 下的列（ICC 规范强制；已实测微软
            // sRGB Color Space Profile.icm: rXYZ=(0.4361,0.2225,0.0139)，三列之和=D50 白），
            // 而 GetTargetXyzToRgbMatrix 给的是 **D65 引用** 的 sRGB/P3/Adobe/BT.2020 逆矩阵
            // （IEC/常见文献那一组）。旧实现直接把两者相乘 ⇒ 复合矩阵里藏了一次未做的
            // 白点适配（蓝/黄方向系统性偏移），而它偏偏又要靠 IsIdentityMatrix 去判"源≈目标可跳过"
            // —— 同一张 sRGB 源 profile 本该判成单位阵跳过，实际因为差着适配而不去跳过。
            // 这里把源矩阵先换算到 D65 引用，两个操作数才真正同域。
            // 数值来源（不是凭记忆）：A = D50列 · inv(D65列)，两组列向量分别取自
            //   IEC 61966-2-1 与本机上表实测的微软 profile，再取 inv(A)；取证与三条自检
            //   （白点必须映到白点）在 scripts/probes/d50_adaptation_derive.py，
            //   行为侧回归见 ColorPipelineTests.BakeIcc_SrgbToSrgb_IsExactIdentity。
            if (srcMatrix is not null)
            {
                float[] d50ToD65Pcs =
                [
                     0.9554865f, -0.0230830f,  0.0632982f,
                    -0.0285193f,  1.0101327f,  0.0210870f,
                     0.0122368f, -0.0204462f,  1.3303371f,
                ];
                srcMatrix = MultiplyMatrices(d50ToD65Pcs, srcMatrix);
            }

            if (srcMatrix is null || dstMatrix is null)
            {
                // ═══ 2026-09-19 缺陷 D-1：转换没做成就不能返回"目标 ICC"═══
                // 旧写法是 `return (bgra, targetIcc)` 并注释"查看器负责转换"—— 可查看器拿到的是
                // "源 = 目标色域"的声明：像素还留在源色域，却被标成目标广色域，查看器按此解释反而
                // 更偏（sRGB 像素标成 P3 会被渲染得过饱和）。这里没有任何"我做了转换"的事实支撑，
                // 所以如实返回 icc=null，由调用方按像素真实所在色域降级声明（见 PreparePixelsWithIcc）。
                // ⚠ 与下方 IsIdentityMatrix 那一支不同：那一支像素【确实】已在目标色域，嵌目标 ICC 是对的。
                CoreLog.Warn("ICC",
                    $"矩阵解析失败，像素未转换（不返回目标 ICC）: {w}x{h}, 目标={targetColorSpace}", LogCategory.Encoding);
                TrueToneCap.Core.Encoding.EncoderFallbackNotifier.LastReason =
                    $"ICC 色彩转换未能完成（源/目标矩阵不可解析，目标={targetColorSpace}）"
                    + "⇒ 本张按像素实际所在色域声明，未做色域搬运";
                return (bgra, null);
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

            CoreLog.Debug("ICC", $"像素转换完成: {w}x{h}, 目标={targetColorSpace}", LogCategory.Encoding);
            return (converted, targetColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase) ? null : targetIcc);
        }
        catch (Exception ex)
        {
            // 带 ex 重载 ⇒ 堆栈进日志。原先只写 ex.Message 且只在 Debug 输出，
            // 异常类型与出处全部丢失（M8 记的"cause is Debug-only"）。
            CoreLog.Warn("ICC", $"烘焙到 {targetColorSpace} 失败，本张退回未转换像素", ex, LogCategory.Encoding);
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

    /// <summary>归一化色彩空间键：忽略大小写、空格、连字符、下划线。</summary>
    /// <remarks>
    /// ═══ 历史坑（2026-09-15 修复）═══
    /// 全仓实际流通的色彩空间标签是 <c>"DisplayP3"</c> / <c>"DCI_P3"</c>
    /// （见 MainWindow.ColorManagement.cs、PngCheck.cs、本文件 PatchSrgbPrimaries 等处），
    /// 而原实现直接用 <c>ToUpperInvariant()</c> 去匹配 <c>"DISPLAY P3"</c>（含空格）与
    /// <c>"DCI-P3"</c>（连字符）—— <c>"DISPLAYP3"</c> 与 <c>"DCI_P3"</c> 均不匹配，
    /// 于是恒返回 null：像素**直通不转换**，但 ICC 仍按 P3 嵌入，
    /// 像素内容与色彩声明自相矛盾 → 静默偏色且无任何提示。
    /// 凡是比较色彩空间字符串的地方都必须走本归一化，不要各自 ToUpperInvariant。
    /// </remarks>
    internal static string NormalizeColorSpaceKey(string colorSpace)
    {
        if (string.IsNullOrEmpty(colorSpace)) return string.Empty;

        Span<char> buf = stackalloc char[colorSpace.Length];
        int n = 0;
        foreach (var ch in colorSpace)
        {
            if (ch is ' ' or '-' or '_') continue; // 忽略分隔差异
            buf[n++] = ch;
        }
        return new string(buf[..n]).ToUpperInvariant();
    }

    /// <summary>获取目标色彩空间的 XYZ→RGB 逆矩阵。</summary>
    /// <remarks>internal 以便测试做"UI 色域 Tag 全集 → 矩阵"的穷尽性校验（防 P0-D 类漏匹配回归）。</remarks>
    internal static float[]? GetTargetXyzToRgbMatrix(string colorSpace)
    {
        // 返回 XYZ→RGB 矩阵 (3×3, row-major)
        // 比较前先归一化 —— 详见 NormalizeColorSpaceKey 备注中的历史坑说明。
        return NormalizeColorSpaceKey(colorSpace) switch
        {
            "SRGB" or "" => new float[] // sRGB / BT.709 XYZ→RGB (D65)
            {
                 3.2404542f, -1.5371385f, -0.4985314f,
                -0.9692660f,  1.8760108f,  0.0415560f,
                 0.0556434f, -0.2040259f,  1.0572252f
            },
            "P3" or "DISPLAYP3" or "DCIP3" => new float[] // Display P3 XYZ→RGB (D65)
            {
                 2.4934969f, -0.9313836f, -0.4027108f,
                -0.8294890f,  1.7626641f,  0.0236247f,
                 0.0358458f, -0.0761724f,  0.9568845f
            },
            "ADOBERGB" or "ADOBERGB1998" => new float[] // Adobe RGB (1998) XYZ→RGB (D65)
            {
                 2.0413690f, -0.5649464f, -0.3446944f,
                -0.9692660f,  1.8760108f,  0.0415560f,
                 0.0134474f, -0.1183897f,  1.0154096f
            },
            "BT2020" or "REC2020" => new float[] // BT.2020 XYZ→RGB (D65)
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
            CoreLog.Warn("ICC", $"获取标准 ICC '{colorSpace}' 失败", ex, LogCategory.Encoding);
            return null;
        }
    }

    /// <summary>基于本仓库自产的 sRGB ICC 模板修改 primaries 生成自定义色彩空间 ICC。
    /// 保留模板的完整 tag 结构（TRC、wtpt 等），仅替换 rXYZ/gXYZ/bXYZ 和 desc。</summary>
    /// <remarks>
    /// 模板<b>必须</b>来自 <see cref="BuildValidSRgbIcc"/>，不能来自 <c>IccStore.SRGB</c> 的系统 profile：
    /// 本方法只覆写 4 个标签，其余标签原样保留 ⇒ 用系统那份当模板就等于把 HP 的 cprt/creator
    /// 塞进 Display P3 / BT.2020 / Adobe RGB 的产物里（2026-09-20 许可合规审计实测四类容器全命中）。
    /// 详见 <see cref="GetDefaultSRgbIcc"/> 的同名注释。
    /// </remarks>
    public static byte[]? PatchSrgbPrimaries(IccPrimaries p, string name)
    {
        var template = BuildValidSRgbIcc();
        if (template is null || template.Length < 132) return null;

        var icc = (byte[])template.Clone();

        // 解析 tag table（offset 128: tag count, 之后每 12 字节一个 tag entry）
        int tagCount = ReadBE32(icc, 128);
        int tagTableStart = 132;

        // 计算新 primaries 的 XYZ 值 (D50 适应 + 白点归一, ICC 标准)
        // ⚠️ 2026-08-09 修复: 旧 XyToXyzPublic 用 Y=1 无 D50 适应 → 色域错误 (P3 红 2.125)
        //    ICC rXYZ/gXYZ/bXYZ 需 D50 adapted + 白点归一 (白点 Y=1) 的正确 XYZ
        var (rX, rY, rZ, gX, gY, gZ, bX, bY, bZ) =
            PrimariesToD50Xyz(p.Rx, p.Ry, p.Gx, p.Gy, p.Bx, p.By);

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

    /// <summary>获取 HDR (PQ) 标准 ICC Profile — 目标色域 primaries + ST.2084 PQ TRC。
    /// 用于 TIFF 等无 CICP 机制的格式：像素是 PQ 编码 (HdrToPq16)，ICC 必须声明 PQ 传输，
    /// 否则解码器按 sRGB gamma 解码 PQ 值 → 图像错误。
    /// 注意: 不要用于 JXL/PNG/AVIF — 它们用 color fields/CICP 声明更可靠,
    /// 且 JXL 的 ICC 会覆盖 color fields 导致 PQ 传输丢失。</summary>
    public static byte[]? GetHdrStandardIccProfile(string colorSpace)
    {
        try
        {
            IccPrimaries p = colorSpace switch
            {
                "DisplayP3" or "DCI_P3" => IccPrimaries.DisplayP3,
                "BT2020" => IccPrimaries.BT2020,
                "AdobeRGB" => IccPrimaries.AdobeRGB,
                _ => IccPrimaries.SRGB,
            };
            string name = colorSpace switch
            {
                "DisplayP3" or "DCI_P3" => "Display P3 (PQ)",
                "BT2020" => "BT.2020 (PQ)",
                "AdobeRGB" => "Adobe RGB (PQ)",
                _ => "sRGB (PQ)",
            };
            return BuildHdrPqIcc(p, name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>完整构建 HDR PQ ICC (v2, 矩阵型): 目标色域 primaries (D50) + PQ EOTF 曲线。
    /// 不能用 sRGB 模板打补丁: 模板 TRC 空间仅 256 点, 无法容纳 4096 点 PQ LUT。</summary>
    private static byte[]? BuildHdrPqIcc(IccPrimaries p, string name)
    {
        // primaries → D50 XYZ (与 PatchSrgbPrimaries 同算法)
        var (rX, rY, rZ, gX, gY, gZ, bX, bY, bZ) =
            PrimariesToD50Xyz(p.Rx, p.Ry, p.Gx, p.Gy, p.Bx, p.By);

        byte[] rXyz = BuildXyzTag(rX, rY, rZ);
        byte[] gXyz = BuildXyzTag(gX, gY, gZ);
        byte[] bXyz = BuildXyzTag(bX, bY, bZ);
        byte[] wtpt = BuildXyzTag(0.9642f, 1.0f, 0.8249f);   // D50
        byte[] trc = BuildPqCurveTag();                      // 4096 点 PQ EOTF
        byte[] desc = BuildDescTag(name);
        byte[] cprt = BuildTextTag(GeneratedIccCopyright);

        var tags = new (uint sig, byte[] data)[]
        {
            (0x64657363u, desc), (0x63707274u, cprt),   // desc, cprt
            (0x77747074u, wtpt),                        // wtpt
            (0x7258595Au, rXyz), (0x6758595Au, gXyz), (0x6258595Au, bXyz),
            (0x72545243u, trc), (0x67545243u, trc), (0x62545243u, trc),
        };

        int headerSize = 128, tagTableSize = 4 + tags.Length * 12;
        int Align4(int v) => (v + 3) / 4 * 4;
        int off = headerSize + tagTableSize;
        var blocks = new List<(int o, byte[] d)>();
        foreach (var t in tags) { blocks.Add((off, t.data)); off += Align4(t.data.Length); }
        int total = off;

        using var ms = new MemoryStream(total);
        using var bw = new BinaryWriter(ms);
        // ICC 规范为大端序, BinaryWriter 是小端 → 手动大端写入
        static void W32(BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        static void W16(BinaryWriter w, ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
        W32(bw, (uint)total);                  // profile size
        W32(bw, 0);                            // preferred CMM
        bw.Write(new byte[] { 0x02, 0x10, 0x00, 0x00 }); // version 2.1.0
        bw.Write("mntr"u8);                    // device class: monitor
        bw.Write("RGB "u8);                    // color space
        bw.Write("XYZ "u8);                    // PCS
        W16(bw, 2026); W16(bw, 8); W16(bw, 9);
        W16(bw, 0); W16(bw, 0); W16(bw, 0);
        bw.Write("acsp"u8);                    // signature
        W32(bw, 0); W32(bw, 0); W32(bw, 0); W32(bw, 0);
        bw.Write(new byte[8]); W32(bw, 0);
        W32(bw, 0x0000F6D6); W32(bw, 0x00010000); W32(bw, 0x0000D32D); // D50 illuminant
        W32(bw, 0);
        bw.Write(new byte[16]); bw.Write(new byte[28]);

        W32(bw, (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            W32(bw, tags[i].sig); W32(bw, (uint)blocks[i].o); W32(bw, (uint)tags[i].data.Length);
        }
        foreach (var b in blocks)
        {
            bw.Write(b.d);
            int pad = Align4(b.d.Length) - b.d.Length;
            if (pad > 0) bw.Write(new byte[pad]);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>构建 ST.2084 PQ EOTF 曲线标签 (curv, 4096 点, 归一化到 10000 nits = 1.0)。
    /// 值: V ∈ [0,1] → PQ EOTF → L ∈ [0,1] (10000 nits 归一)。</summary>
    private static byte[] BuildPqCurveTag()
    {
        const int points = 4096;
        var data = new byte[12 + points * 2];
        // 'curv' type signature
        data[0] = (byte)'c'; data[1] = (byte)'u'; data[2] = (byte)'r'; data[3] = (byte)'v';
        // reserved 4 bytes (0)
        // count (4096)
        WriteBE32(data, 8, (uint)points);
        // PQ EOTF (ST.2084): L = ((max(V^(1/m) - c1, 0)) / (c2 - c3*V^(1/m)))^(1/n)
        const double m = 78.84375, n = 0.1593017578125;
        const double c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        for (int i = 0; i < points; i++)
        {
            double v = i / (double)(points - 1);
            double vm = Math.Pow(v, 1.0 / m);
            double num = Math.Max(vm - c1, 0.0);
            double den = c2 - c3 * vm;
            double l = den > 0 ? Math.Pow(num / den, 1.0 / n) : 0.0;
            l = Math.Clamp(l, 0.0, 1.0);
            ushort u16 = (ushort)Math.Round(l * 65535.0);
            data[12 + i * 2] = (byte)(u16 >> 8);
            data[12 + i * 2 + 1] = (byte)(u16 & 0xFF);
        }
        return data;
    }

    /// <summary>
    /// 计算 ICC primaries 的 D50-adapted XYZ 值 (白点归一到 Y=1)。
    /// ICC rXYZ/gXYZ/bXYZ tag 记录的是 D50 PCS 下的实际 XYZ (白点 Y=1)。
    /// 算法: 1) xy (Y=1) → 线性 XYZ; 2) 构建 RGB→XYZ D65 矩阵并白点缩放;
    ///       3) Bradford D65→D50 chromatic adaptation。
    /// </summary>
    public static (float RX, float RY, float RZ, float GX, float GY, float GZ, float BX, float BY, float BZ)
        PrimariesToD50Xyz(float rx, float ry, float gx, float gy, float bx, float by)
    {
        // xy → XYZ (Y=1)
        static (float X, float Y, float Z) Xy(double x, double y)
        {
            if (y == 0) return (0, 0, 0);
            double Y = 1.0;
            double X = x * Y / y;
            double Z = (1.0 - x - y) * Y / y;
            return ((float)X, (float)Y, (float)Z);
        }
        var d65 = Xy(0.3127, 0.3290); // D65 白点
        var d50 = Xy(0.3457, 0.3585); // D50 白点

        // 3×3 矩阵列 = 各 primary XYZ (Y=1)
        Span<double> colR = stackalloc double[] { Xy(rx, ry).Item1, Xy(rx, ry).Item2, Xy(rx, ry).Item3 };
        Span<double> colG = stackalloc double[] { Xy(gx, gy).Item1, Xy(gx, gy).Item2, Xy(gx, gy).Item3 };
        Span<double> colB = stackalloc double[] { Xy(bx, by).Item1, Xy(bx, by).Item2, Xy(bx, by).Item3 };

        // 解 s 使 s_R·colR + s_G·colG + s_B·colB = D65 (白点归一)
        // 即求 C·s = w65, C = [colR colG colB]
        double[,] C = new double[3, 3]
        {
            { colR[0], colG[0], colB[0] },
            { colR[1], colG[1], colB[1] },
            { colR[2], colG[2], colB[2] }
        };
        double[] s = Solve3x3(C, new double[] { d65.X, d65.Y, d65.Z });
        for (int i = 0; i < 3; i++) { colR[i] *= s[0]; colG[i] *= s[1]; colB[i] *= s[2]; }

        // Bradford D65→D50 adaptation (就地修改列)
        BradfordAdapt(colR, colG, colB, d65, d50);

        return ((float)colR[0], (float)colR[1], (float)colR[2],
                (float)colG[0], (float)colG[1], (float)colG[2],
                (float)colB[0], (float)colB[1], (float)colB[2]);
    }

    /// <summary>Bradford chromatic adaptation (D65→D50) 应用于 RGB→XYZ 矩阵列。</summary>
    private static void BradfordAdapt(
        Span<double> colR, Span<double> colG, Span<double> colB,
        (float X, float Y, float Z) srcWhite, (float X, float Y, float Z) dstWhite)
    {
        // Bradford cone response matrix
        ReadOnlySpan<double> MA = new double[]
        {
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296
        };
        // source/dest white in cone space
        double[] srcCone = new double[3], dstCone = new double[3];
        for (int i = 0; i < 3; i++)
        {
            srcCone[i] = MA[i * 3 + 0] * srcWhite.X + MA[i * 3 + 1] * srcWhite.Y + MA[i * 3 + 2] * srcWhite.Z;
            dstCone[i] = MA[i * 3 + 0] * dstWhite.X + MA[i * 3 + 1] * dstWhite.Y + MA[i * 3 + 2] * dstWhite.Z;
        }
        // adaptation matrix M = inv(MA) · diag(dstCone/srcCone) · MA
        double[,] AD = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                AD[i, j] = MA[i * 3 + j] * (dstCone[i] / srcCone[i]);
        // M = MA⁻¹ · AD  (MA is not orthogonal, need inverse)
        double[,] MAinv = Invert3x3(MA);
        double[,] M = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += MAinv[i, k] * AD[k, j];
                M[i, j] = sum;
            }

        Apply3x3(M, colR, colG, colB);
    }

    private static void Apply3x3(double[,] M, Span<double> colR, Span<double> colG, Span<double> colB)
    {
        Apply3x3ToColumn(M, colR);
        Apply3x3ToColumn(M, colG);
        Apply3x3ToColumn(M, colB);
    }

    private static void Apply3x3ToColumn(double[,] M, Span<double> col)
    {
        var orig = new[] { col[0], col[1], col[2] };
        for (int i = 0; i < 3; i++)
        {
            double sum = 0;
            for (int k = 0; k < 3; k++) sum += M[i, k] * orig[k];
            col[i] = sum;
        }
    }

    private static double[] Solve3x3(double[,] A, double[] b)
    {
        // 高斯消元求解 3×3 线性方程组 Ax=b
        double[,] m = (double[,])A.Clone();
        double[] v = (double[])b.Clone();
        double[,] aug = new double[3, 4];
        for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) aug[i, j] = m[i, j]; aug[i, 3] = v[i]; }
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(aug[r, col]) > Math.Abs(aug[piv, col])) piv = r;
            for (int c = 0; c < 4; c++) (aug[col, c], aug[piv, c]) = (aug[piv, c], aug[col, c]);
            double d = aug[col, col];
            if (Math.Abs(d) < 1e-12) continue;
            for (int c = 0; c < 4; c++) aug[col, c] /= d;
            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = aug[r, col];
                for (int c = 0; c < 4; c++) aug[r, c] -= f * aug[col, c];
            }
        }
        return new[] { aug[0, 3], aug[1, 3], aug[2, 3] };
    }

    private static double[,] Invert3x3(ReadOnlySpan<double> m)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double[,] inv = new double[3, 3];
        inv[0, 0] = (e * i - f * h) / det; inv[0, 1] = (c * h - b * i) / det; inv[0, 2] = (b * f - c * e) / det;
        inv[1, 0] = (f * g - d * i) / det; inv[1, 1] = (a * i - c * g) / det; inv[1, 2] = (c * d - a * f) / det;
        inv[2, 0] = (d * h - e * g) / det; inv[2, 1] = (b * g - a * h) / det; inv[2, 2] = (a * e - b * d) / det;
        return inv;
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
            // 策略1: EDID 硬件原色 + 广色域闸门（与三档判据同源，见 ProbeNativeWideGamut）。
            // ⚠ 闸门就在这一步里面：不宽的面板直接得到 "sRGB"，不会再走下面那套宽松阈值。
            var probed = ProbeNativeWideGamut(monitorHandle);
            if (probed.Known)
            {
                CoreLog.Info("ICC", $"EDID 原生色域: {probed.Detail} -> {probed.Tag}", LogCategory.Capture);
                return probed.Tag;
            }

            // 策略2: 从 ICC profile 提取矩阵（EDID 读不到时的兜底，ACM 下可能被修改）
            // 原先这里另立一个 `Length >= 500` 的字面量（§31.3-9 数出来的第四套阈值）。
            // 它想问的其实是"这份 profile 里有没有可解析的 RGB→XYZ 矩阵"，
            // 而那正是下面 ExtractRgbToXyzMatrix 自己会回答的（取不到就 return null）
            // ⇒ 长度这道 proxy 只会误杀合法的紧凑件（实测最小合法 matrix profile 396 B），删掉。
            var icc = GetDisplayIccProfile(monitorHandle);
            if (icc is not null)
            {
                var matrix = ExtractRgbToXyzMatrix(icc);
                if (matrix is not null)
                {
                    float rX = matrix[0];
                    float gX = matrix[1];
                    var result = ClassifyGamutByXyzMatrix(rX, gX);
                    CoreLog.Info("ICC", $"ICC 矩阵色域: rX={rX:F4} gX={gX:F4} -> {result}", LogCategory.Capture);
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

    /// <summary>
    /// 绿点相对 BT.709 绿点要"绿出去"这么多才算确证的广色域面板。
    /// <para>为什么用绿点而不是"色域面积"：面积要蓝坐标，而绝大多数广色域面板（P3 / AdobeRGB）
    /// 蓝点与 709 几乎重合，真正拉开差距的就是 G。而且 EDID 解析出来的现成坐标只有 R/G 两点。</para>
    /// <para>2026-09-22 本机两块屏实测把门限夹出来（注册表 EDID 与逐屏审计两边读数一致）：
    /// AUO75AC / BOE B160QAN02.7（2560x1600，100% sRGB 笔电屏）ΔG=0.0246；
    /// PHLC38F / Philips 27M2N5800P（3840x2160，走 HDR 那路）ΔG=0.0963。
    /// 门限上下各有约 2× 余量（0.05/0.0246=2.03，0.0963/0.05=1.93），不脆。</para>
    /// <para>标准参考点（同样按对 709 绿点的欧氏距离算）：DCI-P3 的 G=(0.265,0.690) ⇒ ΔG=0.0966
    /// （本机那块真 P3 屏的 0.0963 与之几乎重合，说明判据量到的就是"面板原生到 P3"）；
    /// AdobeRGB 的 G=(0.210,0.710) ⇒ ΔG=0.1421；BT.2020 的 G=(0.170,0.797) ⇒ ΔG=0.2360。
    /// 部分覆盖的 P3 屏，绿点拉伸大致按覆盖率线性缩水，故 0.05 落在这个尺子的低段，
    /// 只挡住"连一半都不到"的窄屏，不误伤主流广色域面板。</para>
    /// </summary>
    internal const float GreenStretchForWide = 0.05f;

    /// <summary>
    /// 按 <b>EDID 硬件原色</b>回答两个问题：这块屏<b>是不是</b>广色域面板（三态：宽 / 确证不宽 / 读不到），
    /// 以及<b>原生色域标签</b>。标签与闸门是同一次判定的两个输出 —— 这是刻意的：
    /// <para>2026-09-22 审查轮之前它们是两套判据（本函数走 <see cref="ClassifyGamutByChromaticity"/>
    /// 的 <c>rx&gt;0.65 且 gx≤0.30</c>，三档判据走 ΔG≥<see cref="GreenStretchForWide"/>），
    /// 而本函数的返回值正是**色域搬运所取的源色域** ⇒ 本机那块 ΔG=0.0246 的普通 sRGB 屏被宽松阈值
    /// 报成 DisplayP3，同一块屏又被三档判据判成窄色域：一份说不用搬、一份说按 P3 搬。
    /// 现在标签必须先过闸门，不过就只能是 "sRGB"。</para>
    /// <para>与 <see cref="GetDisplayNativeGamutTag"/> 的分工：本函数是唯一事实源，那个函数在 EDID
    /// 读不到时还允许走 ICC 矩阵兜底（兜底只能给标签，不能翻案"确证不宽"）。</para>
    /// </summary>
    /// <param name="monitorHandle">这块屏的 HMONITOR。<b>必须是真实句柄</b>：无效句柄一律返回
    /// "不知道"，绝不回落到别块屏的 EDID（详见 <see cref="ReadEdidFromRegistry"/>）。</param>
    /// <summary>
    /// 按 <b>EDID 硬件原色</b>确证这块屏是不是广色域面板，并给出**与闸门同源**的原生色域标签。
    /// <para><see cref="Tag"/> 的规则：过了 <see cref="GreenStretchForWide"/> 才允许是
    /// DisplayP3 / AdobeRGB / BT2020，过不了就是 "sRGB" —— 不存在"标签说 P3 而闸门说不宽"的中间态。
    /// 这条是 2026-09-22 审查轮（交接 §41）补的：闸门与标签原先两套阈值并存，
    /// 宽松那套（<c>rx&gt;0.65 且 gx≤0.30</c>）会把本机那块 ΔG=0.0246 的普通 sRGB 屏报成 DisplayP3，
    /// 而它正是**色域搬运的源矩阵**所取的值 ⇒ 同一块屏被判窄色域、却被按 P3 搬色。</para>
    /// <para>只用 EDID，不走 ICC 兜底：ICC 矩阵在 ACM 下可能被校准/改写，不能用来证明"面板原生是宽的"。</para>
    /// </summary>
    internal static (bool Known, bool Wide, string Tag, string Detail) ProbeNativeWideGamut(nint monitorHandle)
    {
        try
        {
            var edid = ReadEdidFromRegistry(monitorHandle, out var edidSource);
            if (edid is null || edid.Length < 128) return (false, false, "sRGB", "无 EDID");
            var (rx, ry, gx, gy) = ParseEdidChromaticity(edid);
            if (rx <= 0 && gx <= 0) return (false, false, "sRGB", "EDID 原色字段为空");
            double dg = GreenStretchFromGreen(gx, gy);
            // EDID 来源要跟着读数一起出：读错屏时，卡片上就能看见这份原色是从哪个设备实例拿的
            // （2026-10-21 那次"外接 P3 屏被判成窄色域"，缺的就是这一格读数）。
            return (true, dg >= GreenStretchForWide, NativeGamutTagFor(rx, ry, gx, gy),
                    $"ΔG={dg:F4} G=({gx:F4},{gy:F4}) R=({rx:F4},{ry:F4}) EDID来源={edidSource}");
        }
        catch (Exception ex)
        {
            return (false, false, "sRGB", $"EDID 探测异常 {ex.GetType().Name}");
        }
    }

    /// <summary>绿点到 BT.709 绿点 (0.3000, 0.6000) 的欧氏距离 —— 闸门量的就是这个。</summary>
    internal static double GreenStretchFromGreen(double gx, double gy)
    {
        const double srgbGx = 0.3000, srgbGy = 0.6000;
        return Math.Sqrt((gx - srgbGx) * (gx - srgbGx) + (gy - srgbGy) * (gy - srgbGy));
    }

    /// <summary>
    /// 闸门与细分类的<b>唯一落点</b>：给 EDID 解出的原色，返回"确证过的原生标签"。
    /// <para>不宽 ⇒ 只可能是 "sRGB"（不许凭 <see cref="ClassifyGamutByChromaticity"/> 的宽松阈值
    /// 猜成 P3）；宽 ⇒ 再细分是 P3 / AdobeRGB / BT2020。</para>
    /// <para>单独成函数的理由是能让判据被<b>真机量到的坐标</b>直接测：闸门原先只在
    /// <c>ProbeNativeWideGamut(nint)</c> 里面，而那个要真实 HMONITOR ⇒ 单测无法覆盖，
    /// 结果就是"去掉闸门"没有任何断言会红（2026-09-22 审查轮的突变实验实测到这一点）。</para>
    /// </summary>
    internal static string NativeGamutTagFor(double rx, double ry, double gx, double gy)
        => GreenStretchFromGreen(gx, gy) >= GreenStretchForWide
            ? ClassifyGamutByChromaticity((float)rx, (float)ry, (float)gx, (float)gy)
            : "sRGB";

    /// <summary>
    /// 按 <b>HMONITOR 对应的设备实例路径</b>精确读取该显示器的 EDID。
    /// <para>⚠ 旧实现完全忽略 <c>monitorHandle</c>：它遍历 <c>Enum\DISPLAY</c> 返回<b>第一个</b>头部合法的
    /// EDID。多显示器下每块屏都拿到同一份 —— 2026-09-22 本机实测：主屏（Philips PHLC38F，ΔG≈0.099
    /// 的真 P3）读到的是副屏（AUO75AC，ΔG≈0.025 的普通 sRGB）的原色。这与 0.3.3 那次
    /// "TryCreateFromDisplayId 忽略入参、恒取 DisplayArea.Primary" 是同一形状的缺陷；
    /// 影响面不止三档判据，<see cref="GetDisplayNativeGamutTag"/> 一直在吃这份错数据。</para>
    /// <para>匹配链：HMONITOR → <c>GetMonitorInfo</c> 取 "\.\DISPLAYn" → <see cref="TryGetActiveMonitor"/>
    /// （<c>EDD_GET_DEVICE_INTERFACE_NAME</c>）取 <c>\\?\DISPLAY#厂商型号#实例路径#{GUID}</c> →
    /// 换成注册表路径 <c>DIRECTORY\厂商型号\实例路径</c>。取不到就返回 null（= 不知道），
    /// <b>绝不退化成"随便拿一块屏的 EDID"</b> —— 拿错比拿不到危险得多。</para>
    /// <para><paramref name="source"/> 回给调用方"这份 EDID 是从哪个设备实例读的"，
    /// 由 <see cref="ProbeNativeWideGamut"/> 打进逐屏证据行 ⇒ 读错屏在卡片上就看得见，
    /// 不必再靠人去比对注册表。</para>
    /// </summary>
    private static byte[]? ReadEdidFromRegistry(nint monitorHandle, out string source)
    {
        source = "";
        var instancePath = GetMonitorRegistryInstancePath(monitorHandle);
        if (instancePath is null) return null;
        source = instancePath;
        try
        {
            using var devParams = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instancePath}\Device Parameters");
            if (devParams?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return null;
            // 验证 EDID header: 00 FF FF FF FF FF FF 00
            if (edid[0] != 0x00 || edid[7] != 0x00) return null;
            for (int i = 1; i < 7; i++) if (edid[i] != 0xFF) return null;
            return edid;
        }
        catch { return null; }
    }

    /// <summary>
    /// 取 "\.\DISPLAYn" 这个 GDI 设备名下<b>当前活动</b>那一台监视器的信息。
    /// <para>为什么不许再取 index 0（2026-10-21 本机实测）：一个设备名下可以挂多台监视器，
    /// 笔记本 + 外接屏就是这一形状 —— <c>\\.\DISPLAY1</c> 的 Monitor0 是 <c>AUO75AC</c>
    /// （<c>DISPLAY_DEVICE_ACTIVE</c> 为<b>假</b>，内屏此刻没在用），Monitor1 才是
    /// <c>SAC2765</c>（活动）。按 index 0 读 ⇒ 外接 P3 屏吃到了内屏的 EDID，
    /// 三档判据把"宽"判成"不宽"，色域搬运的源矩阵跟着取错。</para>
    /// <para>一个都没有活动标志 ⇒ 返回 false（= 不知道）。调用方一律走"不知道"那条路，
    /// 不许退回"拿第一条"。</para>
    /// </summary>
    /// <param name="flags">0 = 要 <c>DeviceID="MONITOR\…"</c> 形态；
    /// <c>EDD_GET_DEVICE_INTERFACE_NAME(1)</c> = 要 <c>\\?\DISPLAY#…#{GUID}</c> 接口路径形态。</param>
    private static bool TryGetActiveMonitor(string deviceName, uint flags, out DISPLAY_DEVICEW active)
    {
        const uint DisplayDeviceActive = 0x00000001;
        for (uint i = 0; i < 8; i++)
        {
            var dd = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevicesW(deviceName, i, ref dd, flags)) break;
            if ((dd.StateFlags & DisplayDeviceActive) == 0) continue;
            active = dd;
            return true;
        }
        active = default;
        return false;
    }

    /// <summary>HMONITOR → <c>DISPLAY\厂商型号\实例路径</c>；任一环节失败返回 null。</summary>
    private static string? GetMonitorRegistryInstancePath(nint monitorHandle)
    {
        try
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(monitorHandle, ref mi)) return null;
            var deviceName = (mi.szDevice ?? "").Trim();
            if (deviceName.Length == 0) return null;

            // EDD_GET_DEVICE_INTERFACE_NAME = 1 ⇒ DeviceID 是 \\?\DISPLAY#...#{GUID} 形态的设备接口路径
            if (!TryGetActiveMonitor(deviceName, 1u, out var dd)) return null;
            var id = (dd.DeviceID ?? "").Trim();
            if (id.StartsWith(@"\\?\")) id = id[4..];
            int hash = id.IndexOf("#{", StringComparison.Ordinal);
            if (hash > 0) id = id[..hash];
            id = id.Replace('#', '\\');
            return id.Length > 0 ? id : null;
        }
        catch { return null; }
    }

    /// <summary>解析 EDID 1.x 色度数据 (block 0, bytes 0x19-0x22)。
    /// <para>internal 只为让判据能拿真机 EDID 的那 10 个字节直接喂它 —— 这套取位原先没有任何
    /// 断言观测，改错了不会有人知道（<c>Test_Edid_ChromaticityBitsMatchSpec</c>）。</para></summary>
    internal static (float rx, float ry, float gx, float gy) ParseEdidChromaticity(byte[] edid)
    {
        // EDID A/B 表 3.6「Chromaticity Coordinates」的排法（每个坐标 10 位 = 高 8 位 + 低 2 位）:
        //   0x19: 7-6 = Gx[1:0]  5-4 = Gy[1:0]  3-2 = Bx[1:0]  1-0 = By[1:0]
        //   0x1A: 7-6 = Rx[1:0]  5-4 = Ry[1:0]  3-2 = Wx[1:0]  1-0 = Wy[1:0]
        //   0x1B..0x22 的高 8 位依次是 Rx Ry Gx Gy Bx By Wx Wy
        // ⚠ 这一版之前这里把四个低 2 位全从 0x19 里取（Rx 取到 By、Ry 取到 Bx、Gx 取到 Gy、
        //   Gy 取到 Gx），且注释把规范抄反了 —— 于是"低位"实际是别人的低位。
        // 判据（<c>Test_Edid_ChromaticityBitsMatchSpec</c>）用一条物理事实裁决两种取法：
        // 本机三块真屏（AUO75AC / PHLC38F / SAC2765）按本版解出的白点都是 W=(0.3135,0.3291)，
        // 与 D65 (0.3127,0.3290) 差 0.0008；旧取法读不出白点，且把 ΔG 报成 0.0246/0.0963/0.0880
        // （本版 0.0234/0.0989/0.0867）。单坐标最大偏差 3/1024≈0.0029 —— 闸门是 0.05，
        // 贴着阈值的屏会被这点错位翻面，所以它不是"误差不大就不用管"。
        float Low(int hiByte, int loByte, int shift)
            => (edid[hiByte] * 4 + ((edid[loByte] >> shift) & 0x03)) / 1024.0f;

        float rx = Low(0x1B, 0x1A, 6);
        float ry = Low(0x1C, 0x1A, 4);
        float gx = Low(0x1D, 0x19, 6);
        float gy = Low(0x1E, 0x19, 4);

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
    /// 用户可选色彩空间的收口（2026-09-19 产品决定：界面只留 <b>sRGB</b> 与 <b>BT.2020</b>，
    /// 外加"跟随显示器"）。收口发生在<b>色域矩阵之前</b>，所以它是"把像素搬进 BT.2020"，
    /// 不是"把 P3 像素标成 BT.2020" —— 搬运由 <c>PrepareSdrLinearFloat</c> 的
    /// <c>GetMatrix("BT2020")</c>（浮点路）或 <c>BakeIccToTarget</c>（8-bit 烘焙路）承担，
    /// 两者都保证声明与像素同源。BT.2020 比 Display P3 / Adobe RGB 更宽 ⇒ 不裁色域、不偏色。
    /// <para>⚠ 这里收的是<b>用户可选的目标</b>，不是仓库表达色域的能力：
    /// Display P3 / DCI_P3 / Adobe RGB 的矩阵、ICC 模板与 H.273 映射全部保留（Core 侧仍可达，
    /// 判据样张 <c>cc_*_p3</c> / <c>cc_*_adobe</c> 也继续覆盖它们），因为"检测到的显示器原生色域"
    /// 与降级路径仍要能如实描述像素。删掉表达能力的代价是 P3 面板上的截图只能错着标。</para>
    /// </summary>
    public static string CollapseSelectableTag(string? tag) => tag switch
    {
        null or "" or "System" => "System",
        "sRGB" or "BT2020" => tag,
        // DisplayP3 / DCI_P3 / AdobeRGB 与任何未知字符串 ⇒ 用户当初要的是"广色域"
        _ => "BT2020",
    };

    /// <summary>
    /// 解析色彩空间标签：当用户选择 "System"（跟随系统）时，
    /// 根据 HDR 状态和 ACM 状态自动解析为实际色域。
    /// </summary>
    /// <param name="tag">用户选择的色彩空间标签。</param>
    /// <param name="hdrOutput">是否启用 HDR 输出。</param>
    /// <param name="acmEnabled">系统 ACM 是否启用。</param>
    /// <param name="monitorHandle">可选显示器 HMONITOR，用于 ACM 下检测原生色域。</param>
    /// <returns>解析后的实际色彩空间标签（已按 <see cref="CollapseSelectableTag"/> 收口）。</returns>
    /// <remarks>
    /// 解析规则（2026-09-19 起，目标标签只剩 sRGB / BT.2020 两种）:
    /// - HDR 开启 + "System" → "BT2020"（HDR10 标准容器色域）
    /// - HDR 开启 + 显式色域 → 收口后保留（BT.2020；sRGB 由上层降级为 SDR 输出）
    /// - ACM 启用 + 广色域显示器 → <b>"BT2020"</b>（原先直接返回检测到的原生色域，
    ///   如 DisplayP3/AdobeRGB；现在把像素搬进 BT.2020 后按 BT.2020 声明）
    /// - 其他 → "sRGB"（WGC SDR 会话输出 DWM 色调映射后的 sRGB 数据）
    /// </remarks>
    public static string ResolveColorSpaceTag(string tag, bool hdrOutput, bool acmEnabled = false, nint? monitorHandle = null)
    {
        tag = CollapseSelectableTag(tag);   // ← 用户可选面的唯一收口点
        if (hdrOutput)
        {
            // HDR 开启时"System"解析为 BT.2020（HDR10 标准容器色域）
            if (tag == "System")
                return "BT2020";

            // 收口后这里只可能是 sRGB / BT2020；sRGB 的 HDR 降级在上层（BuildEncodingSettings）做
            return tag;
        }

        // ── SDR 模式 ──
        if (tag != "System") return tag;

        // ACM 启用 → 检测显示器原生色域；非 sRGB 的面板一律并到 BT.2020（见 CollapseSelectableTag）
        if (acmEnabled && monitorHandle.HasValue)
        {
            var nativeGamut = GetDisplayNativeGamutTag(monitorHandle.Value);
            if (nativeGamut is not "sRGB")
                return CollapseSelectableTag(nativeGamut);
        }

        // HDR 关闭时 WGC SDR 会话输出 DWM 色调映射后的 sRGB
        // 即使显示器硬件支持广色域，无 ACM 时 SDR 模式下 WGC 也只返回 sRGB 数据
        return "sRGB";
    }

    /// <summary>
    /// 编码入口的"AdobeRGB ⇒ Rec.2020"映射（2026-09-27 用户口径）。
    /// <para><b>与 <see cref="CollapseSelectableTag"/> 的区别</b>：那个管的是【界面可选项】，
    /// 把 P3 / DCI_P3 / Adobe 全都并进 BT2020；这里只管一件事实 —— AdobeRGB 作为目标
    /// 一律按 Rec.2020 交付，其余标签原样通过。DisplayP3 / DCI_P3 因此仍有自己的忠实出路
    /// （cICP primaries=12、P3 的 ICC），判据表里 p3=12 那条期望也还成立。</para>
    /// <para>为什么产品路径其实看不到这个映射：UI 的可选项早就是三档（跟随显示器 / Rec.709 / Rec.2020），
    /// 老配置里的 Adobe 索引也已被 <c>legacyTags</c> 迁移收口。仍然要它，是因为
    /// ① 判据样张宿主会直接给 <c>EncodingSettings.ColorSpaceTag</c> 赋值（绕过 UI），
    /// ② JXL 一类编码器被直接调用时不能各写各的版本。以前 JXL 分支自己写的是"退回 sRGB"，
    /// 于是"Adobe 到底进哪一档"在两处有不同答案 —— 见交接 §56.4 与 §59。</para>
    /// </summary>
    public static string MapAdobeTargetToWide(string tag) =>
        tag == "AdobeRGB" ? "BT2020" : tag;

    /// <summary>获取当前所选色彩空间的用户友好名称。
    /// <para>2026-09-27 用户口径：可选的两档一律叫 <b>Rec.709 / Rec.2020</b>，不再在同一段话里
    /// 被写成 "sRGB"、"BT.2020"、"非 sRGB" 三种名字 —— 尤其 "BT.2020 (HDR)" 那种写法是错的：
    /// Rec.2020 是<b>原色</b>，HDR 是<b>传递函数/动态范围</b>，两件事分属不同维度
    /// （SDR 广色域走的就是 Rec.2020 原色 + SDR 传递函数）。
    /// Display P3 / DCI-P3 / Adobe RGB 三行留着：这里也报<b>面板自己的原生色域</b>
    /// （<c>src.NativeGamutTag</c>），那是实测事实、不是用户可选项。</para></summary>
    public static string GetColorSpaceDisplayName(string tag) => tag switch
    {
        "System" => "跟随显示器",
        "sRGB" => "Rec.709",
        "DisplayP3" => "Display P3",
        "DCI_P3" => "DCI-P3",
        "AdobeRGB" => "Adobe RGB",
        "BT2020" => "Rec.2020",
        _ => "Rec.709"
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

    /// <summary>scRGB (BT.709 原色 + 线性 gamma) → BT.2020 线性转换矩阵。
    /// 注意: 矩阵只依赖 primaries (sRGB/scRGB/BT.709 原色相同), 与 gamma 无关。</summary>
    public static readonly float[,] SrgbToBt2020 = new float[3, 3]
    {
        { 0.627403f, 0.329283f, 0.043313f },
        { 0.069097f, 0.919541f, 0.011362f },
        { 0.016392f, 0.088013f, 0.895595f }
    };

    /// <summary>scRGB (BT.709 原色 + 线性 gamma) → Display P3 线性转换矩阵。</summary>
    public static readonly float[,] SrgbToDisplayP3 = new float[3, 3]
    {
        { 0.822462f, 0.177194f, 0.000344f },
        { 0.033194f, 0.966799f, 0.000007f },
        { 0.017083f, 0.072411f, 0.910506f }
    };

    /// <summary>scRGB (BT.709 原色 + 线性 gamma) → Adobe RGB 线性转换矩阵。</summary>
    public static readonly float[,] SrgbToAdobeRgb = new float[3, 3]
    {
        { 0.715160f, 0.284849f, 0.000009f },
        { 0.000000f, 1.000000f, 0.000000f },
        { 0.000000f, 0.041169f, 0.958831f }
    };

    /// <summary>根据色彩空间标签获取 scRGB→目标色域 3×3 矩阵。</summary>
    public static float[,]? GetMatrix(string colorSpaceTag) => colorSpaceTag switch
    {
        "BT2020" => SrgbToBt2020,
        "DisplayP3" or "DCI_P3" => SrgbToDisplayP3,
        "AdobeRGB" => SrgbToAdobeRgb,
        _ => null // sRGB / System: 无需转换
    };

    /// <summary>获取 CICP 原色索引 (ITU-T H.273 Table 2)。
    /// sRGB/BT.709 共享相同 primaries → code 1; DisplayP3=12; BT2020=9; AdobeRGB=1。</summary>
    public static byte GetCicpPrimaries(string colorSpaceTag) => colorSpaceTag switch
    {
        "DisplayP3" or "DCI_P3" => 12,
        "BT2020" => 9,
        "AdobeRGB" => 1,
        _ => 1
    };

    /// <summary>获取 CICP 传输函数索引 (ITU-T H.273 Table 3)。
    /// HDR=true → 16 (ST.2084 PQ); SDR → 13 (sRGB transfer)/1 (BT.709 视频 OETF)。
    /// 图片输出用 sRGB (code 13), 非 BT.709 视频 (code 1)。</summary>
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
    /// 注意: 统一流程为色域转换 + 分段 Reinhard 色调映射
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
        // 2026-08-08: 统一流程为色域转换 + 分段 Reinhard 色调映射。
        // 统一流程: 先做色域转换 (scRGB → 目标色域线性), 再做色调映射 (分段 Reinhard)。
        // 1. 色域转换：scRGB (BT.709 原色 + 线性 gamma) → 目标色域线性
        var matrix = GetMatrix(colorSpaceTag ?? "sRGB");
        var converted = ConvertScrgbToTarget(hdrPixels, w, h, matrix);

        // 2. 复用 ToneMapper 融合内核：色调映射 + gamma + swizzle → BGRA8
        //    分段 Reinhard (GainMap 同款) 为亮度缩放保持色相, 在目标色域线性空间同样正确
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

    // ═══ 2026-09-19 删除 ApplySrgbToTargetGamut(byte[] bgra, int w, int h, string tag) ═══
    // 为什么不能这么写：它是一条 8-bit → float → 3×3 矩阵 → 8-bit 的链路，而传进来的字节
    // 已经被上游量化过一次了。色域矩阵是"往窄色域里挤"的线性运算，结果普遍落在两个相邻
    // 8-bit 台阶之间，再量化第二次就是【双重量化】—— 每个通道各吃一次 ±0.5 LSB，
    // 渐变带上直接可见台阶/条带，且误差不可复原。
    // 本仓库的精度链要求"只量化一次"（见 FormatHelper.PrepareSdrLinearFloat 的备注）：
    // 要做色域收敛就在还在浮点域时做（ConvertFloat16ToSdrBgra / ConvertScrgbToTarget），
    // 或者干脆只改容器声明（写 P3/BT.2020 的 cICP / iCCP，不动像素）。
    // 删除前 src/ 下已无任何生产调用方（grep 只剩自身声明，唯一的"调用者"是
    // ColorPipelineTests 里为它写的那条用例 —— 自我循环引用，覆盖的是永不执行的代码）。
    // 留这段注释是防止有人把它当"便捷函数"重新加回来。
}

// ═══════════════════════════════════════
//  ICC v2 Profile 生成器
// ═══════════════════════════════════════

/// <summary>ICC 色域原色 (CIE xy)。</summary>
public readonly record struct IccPrimaries(float Rx, float Ry, float Gx, float Gy, float Bx, float By)
{
    /// <summary>sRGB / BT.709 原色 (sRGB 与 BT.709 共享相同 primaries, 仅 transfer 不同)。
    /// 图片输出用 sRGB (BT.709 原色 + sRGB gamma); scRGB (Windows HDR 合成) 用 BT.709 原色 + 线性 gamma。</summary>
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
        var cprtTag = MakeTextTag(ColorProfileProvider.GeneratedIccCopyright);

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
