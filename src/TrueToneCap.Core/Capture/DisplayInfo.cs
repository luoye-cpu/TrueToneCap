// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Capture/DisplayInfo.cs
// 显示器信息枚举、HDR 检测、鼠标所在显示器定位

using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace TrueToneCap.Core.Capture;

/// <summary>显示器信息，包含 HDR 状态和色彩空间。</summary>
public sealed class DisplayInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public nint MonitorHandle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }

    /// <summary>当前 HDR 是否处于启用状态（DWM 色彩空间为 PQ/HLG）。</summary>
    public bool IsHdr { get; init; }

    /// <summary>显示器硬件是否支持 HDR（即使当前未启用）。
    /// 通过 BitsPerColor >= 10 且 MaxLuminance > 200 nits 判断。</summary>
    public bool SupportsHdr { get; init; }

    // ═══ 2026-09-19 新增：逐显示器"高级颜色（ACM/WCG/HDR）"能力 ═══
    // 为什么要有这三个字段：原先判定"这块屏能不能给出 >8-bit"时，ACM（高级颜色管理）这一条读的是
    // AppServices.Settings.Current.AcmeDetected —— 那是注册表算出来的**系统级单值**。
    // 双屏（一块 10-bit/ACM、一块 8-bit）时，8-bit 副屏也跟着被判"能"，后果是：
    //   ① 为它白建一个 Float16 会话（拿到的其实是被钳到 SDR 范围的内容）；
    //   ② 下游按"有 float 像素 ≡ 高精度源"这条承重不变量声明高位深 ⇒ 产出"容器说 16-bit、源只有 8-bit"的假高精度文件。
    // 现在改由 DISPLAYCONFIG 逐屏查询得到结论（见 DisplayEnumerator.QueryAdvancedColorByDeviceName）。

    /// <summary>对该显示器发起的 DISPLAYCONFIG 高级颜色查询**成功拿到了答复**（false = 驱动/系统没答，
    /// 此时下面两个字段无意义，调用方须回退到旧的系统级 ACM 判据，不能当成"不支持"）。</summary>
    public bool AdvancedColorInfoAvailable { get; init; }

    /// <summary>该显示器**自己**是否被 Windows 判为可扩展范围（高级颜色可用 / 正在以 FP16 scRGB 合成 / ≥10-bit）。</summary>
    public bool SupportsAdvancedColor { get; init; }

    /// <summary>DISPLAYCONFIG 报的该显示器每通道位数（0 = 未答复）。与 DXGI 的 <see cref="BitsPerColor"/> 互为交叉验证。</summary>
    public int AdvancedColorBitsPerChannel { get; init; }

    /// <summary>DWM **此刻**用哪种合成模式渲染该屏：0=SDR(RGB888)、1=广色域(WCG)、2=HDR，
    /// <see cref="ActiveModeUnknown"/>=系统没给这个字段（走的是 Win10 的 type 9 答复）。
    /// <para>这是唯一直接回答"这块屏现在在不在扩展范围里"的逐屏读数。与
    /// <see cref="SupportsAdvancedColor"/> 的区别是决定性的：后者含 advancedColorSupported(b0)，
    /// 那是**系统能力**而非该屏属性 —— 纯 sRGB 8-bit 屏也会报 b0=1，于是窄屏被误判成可出 &gt;8-bit
    /// （2026-09-22 本机双屏实测抓到：800x600 的 SDR 副屏照样建成了 Float16 会话）。</para></summary>
    public int AdvancedColorActiveMode { get; init; } = ActiveModeUnknown;

    /// <summary>ActiveMode 的"未知"哨兵。0 是有意义的值（SDR 合成），不能拿来当缺省。</summary>
    public const int ActiveModeUnknown = 255;

    /// <summary>该屏的高级颜色/广色域**当前是否被用户启用**（type15 的 wideColorUserEnabled ∪ advancedColorActive；type9 取 advancedColorEnabled）。</summary>
    public bool WideColorEnabled { get; init; }

    /// <summary>EDID 硬件原色<b>确证</b>这块屏是广色域面板。仅在 <see cref="NativeGamutKnown"/> 为真时有意义。</summary>
    public bool NativeGamutWide { get; init; }

    /// <summary>是否从 EDID 读到了硬件原色。false = 不知道 ⇒ 既不算广色域确证，<b>也不许据此降级</b>
    /// （与 <c>Unknown</c> 同一取向：拿不准就维持现状）。
    /// <para>为什么非要这一维：ACM 与 DWM 合成模式都是<b>用户开关的镜像</b>，不是面板属性 ——
    /// 2026-09-22 本机实测一块 ΔG=0.0246（普通 100% sRGB）的 AUO 笔电屏，开了 ACM 之后照样是
    /// <c>合成模式=1(WCG)</c> + <c>广色域已启用=是</c>，被上一版判据当成广色域面板并允许声明 BT.2020。</para></summary>
    public bool NativeGamutKnown { get; init; }

    /// <summary>EDID 确证<b>过闸门之后</b>的原生色域标签：sRGB / DisplayP3 / AdobeRGB / BT2020。
    /// <para>它的存在就是为了只有一个口径 —— 这个值既是三档判据看的"宽不宽"，
    /// 也是<b>色域搬运所取的源色域</b>。审查轮（交接 §41）之前这两件事各有判据、且宽松那套赢，
    /// 于是同一块屏被"判窄色域"与"按 P3 搬色"同时命中。</para></summary>
    public string NativeGamutTag { get; init; } = "sRGB";

    /// <summary>EDID 确证过程的可读细节（ΔG 与原色坐标），只给证据行/审计用，不参与判定。</summary>
    public string NativeGamutDetail { get; init; } = "";

    public int BitsPerColor { get; init; } = 8; // 8 (SDR) / 10 (HDR)
    public ColorSpaceType ColorSpace { get; init; }
    public Format SupportedFormat { get; init; }
    public string AdapterName { get; init; } = "";

    /// <summary>显示器峰值亮度（nits）。HDR 显示器通常 > 400 nits。</summary>
    public float MaxLuminance { get; init; }

    public override string ToString() =>
        $"[{Index}] {Name} ({Width}x{Height}) {(IsHdr ? "HDR" : (SupportsHdr ? "HDR Capable" : "SDR"))} {(IsPrimary ? "(主)" : "")}";
}

/// <summary>显示器枚举器。通过 DXGI 枚举所有活动显示器。</summary>
public static partial class DisplayEnumerator
{
    // ────── Win32 鼠标/显示器 API ──────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>获取鼠标当前所在的 HMONITOR。</summary>
    public static nint GetMonitorUnderCursor()
    {
        GetCursorPos(out var pt);
        var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        return hMonitor;
    }

    /// <summary>通过 HMONITOR 查找对应的 DisplayInfo。</summary>
    public static DisplayInfo? FindDisplayByMonitor(nint hMonitor)
    {
        var displays = EnumerateDisplays();
        var match = displays.FirstOrDefault(d => d.MonitorHandle == hMonitor);

        // HMONITOR 不匹配时尝试找鼠标所在坐标的显示器
        if (match is null)
        {
            GetCursorPos(out var pt);
            match = displays.FirstOrDefault(d =>
                pt.X >= d.X && pt.X < d.X + d.Width &&
                pt.Y >= d.Y && pt.Y < d.Y + d.Height);
        }

        if (match is null)
            match = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        return match;
    }

    // ────── DXGI 枚举 ──────

    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();

        // ═══ 2026-09-19：逐屏高级颜色能力（一次查询，按 GDI 设备名索引，见 QueryAdvancedColorByDeviceName）═══
        // 放在适配器循环**之外**：QueryDisplayConfig 一次就能拿全所有路径，逐屏各查一次反而更贵。
        // 查询失败/没答复时返回空表 ⇒ 每块屏的 AdvancedColorInfoAvailable=false ⇒ 判据退回旧的系统级 ACM，
        // 不会因为"新加的 P/Invoke 不工作"而把任何一块屏误判成不支持。
        var advancedColor = QueryAdvancedColorByDeviceName();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();

        for (uint adapterIdx = 0; ; adapterIdx++)
        {
            var hr = factory.EnumAdapters1(adapterIdx, out var adapter);
            if (hr.Failure || adapter is null) break;

            try
            {
                var adapterDesc = adapter.Description;
                for (uint outputIdx = 0; ; outputIdx++)
                {
                    var outHr = adapter.EnumOutputs(outputIdx, out var output);
                    if (outHr.Failure || output is null) break;

                    try
                    {
                        var desc = output.Description;
                        var output6 = output.QueryInterface<IDXGIOutput6>();
                        var desc1 = output6.Description1;

                        // 判断 HDR 当前是否启用: DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12
                        var cs = desc1.ColorSpace;
                        bool isHdr = (int)cs == 12 || (int)cs == 9; // HDR10/ST.2084 or HLG

                        // 判断显示器硬件是否支持 HDR（即使当前 HDR 未开启）:
                        // 标准: BitsPerColor >= 10 且 MaxLuminance > 200 nits
                        // DXGI_OUTPUT_DESC1.MaxLuminance 单位是 nits，HDR 显示器通常 > 400
                        bool supportsHdr = desc1.BitsPerColor >= 10 && desc1.MaxLuminance > 200f;

                        // 如果 ColorSpace 本身就是 PQ/HLG，那肯定支持 HDR
                        if (isHdr) supportsHdr = true;

                        Format fmt = Format.B8G8R8A8_UNorm;

                        // 尝试 Float16 检测
                        if (isHdr)
                        {
                            try { fmt = Format.R16G16B16A16_Float; }
                            catch { fmt = Format.B8G8R8A8_UNorm; }
                        }

                        // 逐屏高级颜色能力：键是 GDI 设备名（"\\.\DISPLAY1"），DXGI 的 DeviceName 就是同一个字符串，
                        // 所以这里能精确落到"这一块屏"头上，不必再借系统级 ACM 单值。
                        // 匹配不到（虚拟显示器 / 名称为空 / 查询失败）⇒ Available=false ⇒ 判据自行回退，不误伤。
                        AdvancedColorInfo ac = default;
                        bool acKnown = false;
                        var acKey = (desc.DeviceName ?? "").Trim();
                        if (acKey.Length > 0)
                            acKnown = advancedColor.TryGetValue(acKey, out ac);

                        // EDID 硬件原色：证明"这块面板本身能不能显示广色域"，与 ACM 开关无关。
                        // 每块屏一次注册表读，量级与上面那次 QueryDisplayConfig 相同，不额外加热点。
                        var native = ColorManagement.ColorProfileProvider.ProbeNativeWideGamut(desc.Monitor);

                        displays.Add(new DisplayInfo
                        {
                            Index = displays.Count,
                            // DXGI 的 DeviceName 标注为可空（虚拟显示器可能给 null）；Name 是非空属性，
                            // 这里必须自己兜住，否则 CS8601。
                            Name = desc.DeviceName ?? "",
                            MonitorHandle = desc.Monitor,
                            X = desc.DesktopCoordinates.Left,
                            Y = desc.DesktopCoordinates.Top,
                            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top,
                            IsPrimary = IsMonitorPrimary(desc.Monitor),
                            IsHdr = isHdr,
                            SupportsHdr = supportsHdr,
                            AdvancedColorInfoAvailable = acKnown && ac.Available,
                            SupportsAdvancedColor = acKnown && ac.Available && ac.SupportsAdvancedColor,
                            AdvancedColorBitsPerChannel = acKnown && ac.Available ? ac.BitsPerChannel : 0,
                            AdvancedColorActiveMode = acKnown && ac.Available ? ac.ActiveMode : DisplayInfo.ActiveModeUnknown,
                            WideColorEnabled = acKnown && ac.Available && ac.WideColorEnabled,
                            NativeGamutWide = native.Wide,
                            NativeGamutKnown = native.Known,
                            NativeGamutTag = native.Tag,
                            NativeGamutDetail = native.Detail,
                            BitsPerColor = (int)desc1.BitsPerColor,
                            ColorSpace = cs,
                            SupportedFormat = fmt,
                            AdapterName = adapterDesc.Description,
                            MaxLuminance = desc1.MaxLuminance
                        });
                    }
                    finally { output.Dispose(); }
                }
            }
            finally { adapter.Dispose(); }
        }
        return displays;
    }

    public static bool IsDisplayHdr(int displayIndex)
    {
        var displays = EnumerateDisplays();
        if (displayIndex < 0 || displayIndex >= displays.Count) return false;
        return displays[displayIndex].IsHdr;
    }

    public static int GetPrimaryDisplayIndex()
    {
        var displays = EnumerateDisplays();
        return displays.FirstOrDefault(d => d.IsPrimary)?.Index ?? 0;
    }

    // ────── SDR 白点 (SdrWhiteLevel) 读取 ──────
    // Windows 系统实际 SDR 白点 (nits), 通过 DISPLAYCONFIG_SDR_WHITE_LEVEL 获取
    // scRGB 中 SDR 内容 = 该系统值 (如 200 nits = 2.5 scRGB), 1.0 scRGB = 80 nits 标称

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel; // 单位: 0.001 nits (如 200000 = 200 nits)
    }

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 0x0000000b;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 1;
    // QueryDisplayConfig flags (wingdi.h)
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathArrayElements, IntPtr pathInfoArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray, out uint topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);

    /// <summary>
    /// 获取系统实际 SDR 白点亮度 (nits)。
    /// 通过 DISPLAYCONFIG_SDR_WHITE_LEVEL API 读取 (Windows 10 2004+)。
    /// 失败时返回 0 (调用方回退到默认值)。
    /// </summary>
    public static int GetSdrWhiteLevel()
    {
        try
        {
            uint nPaths = 0, nModes = 0;
            int bs = GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out nPaths, out nModes);
            if (bs != 0)
                return 0;

            // ═══ 2026-09-19 修正：stride 从 100 改成实测的 72 ═══
            // 按 SDK wingdi.h 逐字段算（x64）：
            //   DISPLAYCONFIG_PATH_SOURCE_INFO = LUID 8 + id 4 + modeInfoIdx 4 + statusFlags 4 = 20
            //   DISPLAYCONFIG_PATH_TARGET_INFO = LUID 8 + id 4 + modeInfoIdx 4 + outputTechnology 4
            //                                    + rotation 4 + scaling 4 + refreshRate 8
            //                                    + scanLineOrdering 4 + targetAvailable 4 + statusFlags 4 = 48
            //   DISPLAYCONFIG_PATH_INFO        = 20 + 48 + flags 4 = 72   （与本文件 DC_PATH_INFO_SIZE 同源）
            // 旧值 100 只是把缓冲区**多分**了（不会越界写），但 `paths + i * pathSize` 从第 2 条路径起
            // 就偏到上一条记录尾部之外 ⇒ 读到伪的 target LUID/id：那条查询要么返回非 0（这台机只有 1 条
            // 路径 ⇒ 永远走不到，所以一直没暴露），要么**查到另一块屏**并返回错的白点。
            // ⚠ 字段偏移 20/24/28 本身是【对的】（targetInfo 从 20 起）—— 本机实测返回 120 nits ✅。
            int pathSize = DC_PATH_INFO_SIZE;
            int modeSize = 64;  // sizeof(DISPLAYCONFIG_MODE_INFO) = infoType 4 + id 4 + LUID 8 + union 48 = 64 ✅
            var paths = Marshal.AllocHGlobal((int)(pathSize * nPaths));
            var modes = Marshal.AllocHGlobal((int)(modeSize * nModes));
            try
            {
                uint actualPaths = nPaths, actualModes = nModes;
                int qr = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref actualPaths, paths, ref actualModes, modes, out _);
                if (qr != 0)
                    return 0;

                // 遍历路径, 对每个路径查询 SDR 白点
                // DISPLAYCONFIG_PATH_INFO 布局 (72 字节，见上面 pathSize 的逐字段算式):
                //   [0..19]   sourceInfo (LUID 8 + id 4 + modeInfoIdx 4 + statusFlags 4)
                //   [20..67]  targetInfo (LUID 8 + id 4 + modeInfoIdx 4 + outputTechnology 4 +
                //                         rotation 4 + scaling 4 + refreshRate 8 + scanLineOrdering 4 +
                //                         targetAvailable 4 + statusFlags 4)
                //   [68..71]  flags
                for (uint i = 0; i < actualPaths; i++)
                {
                    var pathPtr = paths + (int)(i * pathSize);
                    // targetInfo 从偏移 20 开始:
                    //   adapterId @ 20 (LUID 8), id @ 28, modeInfoIdx @ 32, outputTechnology @ 36 …
                    //   注意: SDR 白点查询用 target adapterId + target id
                    uint tgtAdapterLow = (uint)Marshal.ReadInt32(pathPtr, 20);
                    int tgtAdapterHigh = Marshal.ReadInt32(pathPtr, 24);
                    uint tgtId = (uint)Marshal.ReadInt32(pathPtr, 28);

                    var whiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            Type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                            Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                            AdapterId = new LUID { LowPart = tgtAdapterLow, HighPart = tgtAdapterHigh },
                            Id = tgtId
                        }
                    };
                    IntPtr req = Marshal.AllocHGlobal(Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>());
                    try
                    {
                        Marshal.StructureToPtr(whiteLevel, req, false);
                        if (DisplayConfigGetDeviceInfo(req) == 0)
                        {
                            var result = Marshal.PtrToStructure<DISPLAYCONFIG_SDR_WHITE_LEVEL>(req);
                            // SDRWhiteLevel 单位: 80 nits 的倍数 × 1000 (Microsoft 文档)
                            //   nits = SDRWhiteLevel / 1000 × 80
                            //   例: 1000 = 80 nits, 1250 = 100 nits, 2000 = 160 nits
                            int nits = (int)Math.Round(result.SDRWhiteLevel / 1000.0 * 80.0);
                            if (nits > 0) return nits;
                        }
                    }
                    finally { Marshal.FreeHGlobal(req); }
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }
        catch { return 0; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  逐显示器"高级颜色"能力 (2026-09-19)
    //  DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO[_2] + QueryDisplayConfig
    // ══════════════════════════════════════════════════════════════════════
    //
    // 为什么要这一整段：源精度判据的第三条腿（ACM/广色域 SDR）以前读的是注册表算出来的**系统级单值**，
    // 于是"一块 10-bit/ACM + 一块 8-bit"的双屏里，8-bit 副屏也被判成"给得出 >8-bit"：
    // 白建 Float16 会话（内容其实被钳在 SDR 范围），还可能让下游据此声明高位深 ⇒ 假高精度文件。
    // 现在改成逐屏问系统："这块屏自己能不能做高级颜色"。
    //
    // 实测（本机 Win11 25H2，wingdi.h 10.0.26100 为准）：
    //   · header.Id 必须填**目标(target) id**：填源 id 返回 31 (ERROR_GEN_FAILURE)、填 bogus id 同样 31；
    //     填对时 hr=0 且 bitsPerColorChannel/activeColorMode 有真值。故这里只用 target id，不做"两种都试"。
    //   · QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS) 在本机直接返回 87 (ERROR_INVALID_PARAMETER)，
    //     只有 QDC_DATABASE_CURRENT 可用（GetSdrWhiteLevel 早就选了它）⇒ 这里跟它一致，
    //     "当前在用"靠路径自己的 statusFlags + targetAvailable 过滤。
    //   · sizeof(DISPLAYCONFIG_PATH_INFO) = 72（sourceInfo 20 + targetInfo 48 + flags 4），
    //     下面按裸偏移读，所以路径间隔必须是 72 —— 偏移 20/28 与 GetSdrWhiteLevel 里读的完全一致（那边是对的），
    //     但它的**步长**原本写死 100 ⇒ 第 2 条以后的路径会读偏。2026-09-19 已把它改用 DC_PATH_INFO_SIZE。

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 0x00000001;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 0x00000009;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 0x0000000f;

    private const int DC_PATH_INFO_SIZE = 72;      // sizeof(DISPLAYCONFIG_PATH_INFO)
    private const int DC_MODE_INFO_SIZE = 64;      // sizeof(DISPLAYCONFIG_MODE_INFO)
    private const int DC_SRC_ID = 8;               // sourceInfo.id
    private const int DC_SRC_STATUS = 16;          // sourceInfo.statusFlags
    private const int DC_TGT_ADAPTER = 20;         // targetInfo.adapterId (LUID 8 字节)
    private const int DC_TGT_ID = 28;              // targetInfo.id
    private const int DC_TGT_AVAILABLE = 60;       // targetInfo.targetAvailable (BOOL)
    private const int DC_TGT_STATUS = 64;          // targetInfo.statusFlags
    private const uint DISPLAYCONFIG_SOURCE_IN_USE = 0x00000001;
    private const uint DISPLAYCONFIG_TARGET_IN_USE = 0x00000001;

    /// <summary>一块屏的高级颜色能力答复。Available=false 表示"系统没答"，此时其余字段无意义。</summary>
    private readonly record struct AdvancedColorInfo(bool Available, bool SupportsAdvancedColor, int BitsPerChannel,
                                                     int ActiveMode, bool WideColorEnabled);

    /// <summary>一块屏该按哪一档精度/色域对待。</summary>
    public enum DisplayColorClass
    {
        /// <summary>未知（枚举不到该屏，或系统对这块屏没答复）。
        /// 源精度上按"可能可以"处理（放行 Float16），但**不得**据此声称广色域或 HDR。</summary>
        Unknown,
        /// <summary>窄色域 SDR（sRGB）⇒ 只用 8-bit SDR：不建 Float16 会话，也不声明 BT.2020/HDR。</summary>
        NarrowSdr,
        /// <summary>广色域 SDR（正在以 WCG/FP16 合成、用户开了自动颜色管理、或 ≥10-bit 面板）
        /// ⇒ 可用 Float16 源，声明 BT.2020 + **SDR 传输**。</summary>
        WideGamutSdr,
        /// <summary>HDR 生效（PQ/HLG）⇒ Float16 源 + BT.2020 + HDR。</summary>
        Hdr,
    }

    /// <summary>
    /// 【三档分类的唯一出处】这块屏该走 8-bit SDR / 广色域 SDR / HDR。
    /// <para>2026-09-22 收紧的起因（本机双屏实测）：800x600@100% 的纯 SDR 副屏被判成"可出扩展范围"，
    /// 白建了一条 Float16 会话并整单走 HDR 编码分派。根因是旧判据把
    /// <see cref="DisplayInfo.SupportsAdvancedColor"/> 当逐屏证据，而它含
    /// <c>advancedColorSupported</c>(b0) —— 那是**系统能力**，纯 sRGB 屏也报 1。</c></para>
    /// <para>现在只认三样逐屏证据：DWM 的实际合成模式（<see cref="DisplayInfo.AdvancedColorActiveMode"/>）、
    /// 用户对该屏是否启用广色域（<see cref="DisplayInfo.WideColorEnabled"/>）、面板位深/亮度。
    /// "系统能做"不再等于"这块屏在用"。</para>
    /// <para>⚠ Unknown 的两个方向故意不一致，见 <see cref="CanDeliverExtendedRange"/> 与
    /// <see cref="CanClaimWideGamut"/>：查不到时宁可多试一次 Float16（失败只是回退 8-bit），
    /// 但绝不因此授权把容器声明成 BT.2020/HDR —— 误判成不支持 = 该屏永久静默降级，
    /// 而误声明 = 产出"容器说高位深、源只有 8-bit"的假高精度文件，两个都坏但坏法不同。</para>
    /// </summary>
    public static DisplayColorClass ClassifyDisplayColor(DisplayInfo? display, bool systemAcmEnabled)
    {
        if (display is null) return DisplayColorClass.Unknown;      // 枚举不到 ⇒ 不武断降级，也不授权声称
        if (display.IsHdr) return DisplayColorClass.Hdr;            // DXGI 当前就是 PQ/HLG
        // DXGI 侧的硬证据先判，且必须判在逐屏高级颜色答复【之前】：
        // SupportsHdr = BitsPerColor≥10 且峰值 >200nits，是这块面板自己的属性，
        // 不依赖 DISPLAYCONFIG 是否答复。放后面的话，"无答复 + 全局 ACM 关"就会把一块真 10-bit
        // 面板降成窄色域 ⇒ 静默失去浮点源，正是"查不到推不出不支持"那条红线要拦的事（真值表抓到过一次）。
        if (display.SupportsHdr || display.BitsPerColor >= 10) return DisplayColorClass.WideGamutSdr;

        // ★ 面板自证：EDID 硬件原色确证"不宽" ⇒ 这块屏就是 sRGB，与系统答没答复无关。
        // 位置是这条判据的全部意义：放在下面的"有答复"分支【里面】时，Win10 的 type9 答复路径
        // （ActiveMode 未知）会绕开它 —— 一块确证的 sRGB 屏仍会因为"全局 ACM 开着"被判 Unknown，
        // 而 Unknown 在源精度侧是放行的 ⇒ 又白建一条 Float16 会话。提到分支前才堵得住第二台机器。
        // 唯一让道的情况：DWM 此刻就在按 PQ 合成这块屏（mode=2）。那一帧内容确实是 HDR 母版，
        // 把它降成窄色域会让截图错在"输出声明"这个更要命的方向上。
        // 读不到 EDID ⇒ 完全不表态（"不知道"永远推不出"不宽"，与 null 同一取向）。
        bool compositingHdr = display.AdvancedColorInfoAvailable && display.AdvancedColorActiveMode == 2;
        if (!compositingHdr && display.NativeGamutKnown && !display.NativeGamutWide)
            return DisplayColorClass.NarrowSdr;

        if (display.AdvancedColorInfoAvailable)
        {
            // 有逐屏答复 ⇒ 只信逐屏，不再看全局 ACM（这条顺序是 2026-09-19 定下的，保留）。
            if (display.AdvancedColorActiveMode == 2) return DisplayColorClass.Hdr;
            bool inExtendedRange =
                display.AdvancedColorActiveMode is > 0 and < DisplayInfo.ActiveModeUnknown ||  // 1=WCG / 2=HDR；0=SDR 合成【不算】
                display.WideColorEnabled ||
                display.AdvancedColorBitsPerChannel >= 10;
            // 注意这里不再看 SupportsAdvancedColor —— 它含 advancedColorSupported(b0)，那是【系统能力】，
            // 纯 sRGB 屏也报 1。本次收紧改的就是这一处。
            if (!inExtendedRange) return DisplayColorClass.NarrowSdr;

            // DWM 确实在按扩展范围合成这块屏，而"确证不宽"的面板已在上面被拦下 ⇒ 走到这里
            // 要么 EDID 确证是宽的，要么根本没读到（拿不准不降级）。
            // 本机实测：AUO75AC（ΔG=0.0246 的 2560x1600 笔电屏）开了 ACM 后同样 mode=1 +
            // 广色域已启用=是，靠的就是上面那条被它自己的 EDID 退回窄色域。
            return DisplayColorClass.WideGamutSdr;
        }

        // 无答复：ACM 开着 ⇒ Unknown（放行 Float16、不授权声明）；ACM 关着 ⇒ 明确 NarrowSdr。
        return systemAcmEnabled ? DisplayColorClass.Unknown : DisplayColorClass.NarrowSdr;
    }

    /// <summary>
    /// 【源精度判据】这块屏给不给得出 &gt;8-bit 的内容。薄薄一层包在
    /// <see cref="ClassifyDisplayColor"/> 上 —— 单屏路径与多屏拼接路径必须共用同一份判据
    /// （历史上两处各写一份 <c>IsHdr || SupportsHdr</c>，结果同一块屏单屏能拿 float、多屏拿不到）。
    /// <para>Unknown 计入"给得出"：误判成不支持会让该屏在进程内永久失去高精度源（静默降级、用户零感知），
    /// 而多试一次 Float16 的代价只是一条会话 + 一次首帧超时（后者已由软冷却兜住）。</para>
    /// </summary>
    public static bool CanDeliverExtendedRange(DisplayInfo? display, bool systemAcmEnabled) =>
        ClassifyDisplayColor(display, systemAcmEnabled) != DisplayColorClass.NarrowSdr;

    /// <summary>许不许把输出声明成广色域（BT.2020）。与源精度判据**故意不同向**：Unknown 返回 false。
    /// <para>调用方（CapturePipelineService 的钳制点）在 Unknown 时【不施加钳制】，即"拿不准就不改变
    /// 用户的选择"；返回 false 的含义是"这份证据不足以支撑一个广色域声明"，不是"必须降级"。</para></summary>
    public static bool CanClaimWideGamut(DisplayColorClass c) =>
        c is DisplayColorClass.WideGamutSdr or DisplayColorClass.Hdr;

    /// <summary>许不许按 HDR（PQ）编码。同 <see cref="CanClaimWideGamut"/>：Unknown 不授权声称，
    /// 但调用方对 Unknown 不钳制。</summary>
    public static bool CanClaimHdr(DisplayColorClass c) => c is DisplayColorClass.Hdr;

    /// <summary>
    /// 把一块屏的<b>全部判据输入</b>渲染成一行文本，供现场日志与 <c>--display-audit</c> 共用。
    /// <para>为什么要独立出来：分类结果只有一个词（<c>NarrowSdr</c>），而排障要看的是"凭什么"——
    /// 只看一个词，我们还得分不清是 ①驱动没答复、②DWM 此刻按 SDR 合成、③面板真是 8-bit。
    /// 渲染只这一处，日志与探针不会各持一份口径。</para>
    /// </summary>
    public static string DescribeColorEvidence(DisplayInfo? d, bool systemAcmEnabled)
    {
        if (d is null)
            return $"屏信息缺失(枚举不到) 系统级ACM={YesNo(systemAcmEnabled)} ⇒ 分类=Unknown";

        var acmAnswer = d.AdvancedColorInfoAvailable ? YesNo(d.SupportsAdvancedColor) : "无答复";
        string mode = !d.AdvancedColorInfoAvailable ? "未知"
            : d.AdvancedColorActiveMode switch
            {
                0 => "0(SDR RGB888)",
                1 => "1(WCG)",
                2 => "2(HDR)",
                DisplayInfo.ActiveModeUnknown => "未知(老接口无此字段)",
                var m => m.ToString(),
            };
        return $"[{d.Name} 0x{d.MonitorHandle:X}{(d.IsPrimary ? " 主屏" : "")} {d.Width}x{d.Height}] " +
               $"分类={ClassifyDisplayColor(d, systemAcmEnabled)}" +
               $" | DXGI: 色域={d.ColorSpace} IsHdr={YesNo(d.IsHdr)} SupportsHdr={YesNo(d.SupportsHdr)}" +
               $" bits/通道={d.BitsPerColor} 峰值={d.MaxLuminance:F0}nits" +
               $" | DISPLAYCONFIG: 支持高级颜色={acmAnswer} DWM合成模式={mode}" +
               $" 广色域已启用={YesNo(d.WideColorEnabled)} 每通道={d.AdvancedColorBitsPerChannel}bit" +
               // ACM 说"在用扩展范围"，EDID 说"面板原生是什么" —— 两者不一致时以 EDID 为准，
               // 所以这一维必须出现在证据行里，否则读日志的人只能看到被 ACM 撑起来的假广色域。
               $" | EDID原生(确证广色域的唯一依据)={(d.NativeGamutKnown ? (d.NativeGamutWide ? "宽" : "不宽") : "读不到")} {d.NativeGamutDetail}" +
               $" | 系统级ACM(仅兜底/仅影响 Unknown)={YesNo(systemAcmEnabled)}";
    }

    private static string YesNo(bool v) => v ? "是" : "否";

    /// <summary>
    /// 为"多显示器拼接"挑那块提供浮点画布与亮度基准的屏。返回 null ⇒ 本次按全 SDR 拼接。
    /// <para>2026-09-22 收紧，两条都不是"能不能给 &gt;8-bit"这个布尔量能表达的：
    /// ① <c>Unknown</c> 不够格。旧写法走 <see cref="CanDeliverExtendedRange"/>，而它对 Unknown 放行 ——
    ///    等于"逐屏没答复 + 全局 ACM 被某台 HDR 主屏点亮"就能让一块**未被证实**的屏决定整张图的色彩基准，
    ///    与 2026-09-19 记下的"系统级单值误伤 8-bit 副屏"是同一条路。收紧后全局 ACM 选不出源。</para>
    /// <para>② 先比档、同档内才讲主次：<c>Hdr</c> 优先于 <c>WideGamutSdr</c>，而不是"主屏给得出就用主屏"。
    ///    一幅图只能有一种色彩空间和一个亮度基准（拼接处注释就是这个理由），基准取自错的屏 ⇒ 整图偏；
    ///    而"广色域 SDR 主屏 + 真 HDR 副屏"是很常见的接法，旧写法在这种接法上恰好取错。</para>
    /// </summary>
    public static DisplayInfo? SelectHdrSourceDisplay(IReadOnlyList<DisplayInfo> displays, bool systemAcmEnabled)
    {
        DisplayInfo? Pick(DisplayColorClass want)
        {
            DisplayInfo? first = null;
            foreach (var d in displays)
            {
                if (ClassifyDisplayColor(d, systemAcmEnabled) != want) continue;
                if (d.IsPrimary) return d;          // 同档内主屏优先
                first ??= d;
            }
            return first;
        }
        return Pick(DisplayColorClass.Hdr) ?? Pick(DisplayColorClass.WideGamutSdr);
    }

    /// <summary>
    /// 按 GDI 设备名（"\\.\DISPLAY1"，与 DXGI OUTPUT_DESC.DeviceName 同一字符串）索引的逐屏高级颜色能力。
    /// 任何一步失败都返回"已拿到的部分 + 空"，绝不抛异常（EnumerateDisplays 的调用方含无显示器的 headless 环境）。
    /// </summary>
    private static Dictionary<string, AdvancedColorInfo> QueryAdvancedColorByDeviceName()
    {
        var map = new Dictionary<string, AdvancedColorInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            uint nPaths = 0, nModes = 0;
            if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out nPaths, out nModes) != 0 || nPaths == 0)
                return map;

            // 模式数组这里用不到（只按路径遍历），但 QueryDisplayConfig 要求给一块够大的缓冲区，
            // nModes=0 时也要留出 1 个元素的空间，避免 AllocHGlobal(0) 拿到非零句柄却又"零长度"。
            int modesCount = nModes == 0 ? 1 : (int)nModes;
            var paths = Marshal.AllocHGlobal((int)(nPaths * DC_PATH_INFO_SIZE));
            var modes = Marshal.AllocHGlobal(modesCount * DC_MODE_INFO_SIZE);
            try
            {
                uint actualPaths = nPaths, actualModes = nModes;
                if (QueryDisplayConfig(QDC_DATABASE_CURRENT, ref actualPaths, paths,
                        ref actualModes, modes, out _) != 0)
                    return map;

                for (uint i = 0; i < actualPaths; i++)
                {
                    var p = paths + (int)(i * DC_PATH_INFO_SIZE);

                    // DATABASE_CURRENT 会带不上正在用的路径 ⇒ 只要"源在用 + 目标在用 + 目标可用"三条都成立的那条
                    uint srcStatus = (uint)Marshal.ReadInt32(p, DC_SRC_STATUS);
                    uint tgtStatus = (uint)Marshal.ReadInt32(p, DC_TGT_STATUS);
                    int targetAvailable = Marshal.ReadInt32(p, DC_TGT_AVAILABLE);
                    if ((srcStatus & DISPLAYCONFIG_SOURCE_IN_USE) == 0 ||
                        (tgtStatus & DISPLAYCONFIG_TARGET_IN_USE) == 0 ||
                        targetAvailable == 0)
                        continue;

                    var srcAdapter = ReadLuid(p, 0);
                    uint srcId = (uint)Marshal.ReadInt32(p, DC_SRC_ID);
                    var tgtAdapter = ReadLuid(p, DC_TGT_ADAPTER);
                    uint tgtId = (uint)Marshal.ReadInt32(p, DC_TGT_ID);

                    string gdiName = QuerySourceGdiDeviceName(srcAdapter, srcId);
                    if (gdiName.Length == 0 || map.ContainsKey(gdiName)) continue;
                    map[gdiName] = QueryAdvancedColorForTarget(tgtAdapter, tgtId);
                }
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }
        catch
        {
            map.Clear();   // 宁可整体回退到系统级 ACM，也不给半截结果
        }
        return map;
    }

    /// <summary>发一个 DISPLAYCONFIG 设备信息查询包（裸偏移填 header，避免再声明一批结构体）。</summary>
    /// <returns>返回码 0 = 成功（packet 内可读结果）；非 0 = 该系统/驱动不答复。</returns>
    private static bool TryDisplayConfigQuery(uint infoType, int packetSize, LUID adapterId, uint id, IntPtr packet)
    {
        for (int i = 0; i < packetSize; i++) Marshal.WriteByte(packet, i, 0);
        // DISPLAYCONFIG_DEVICE_INFO_HEADER = type(4) + size(4) + adapterId(LUID 8) + id(4)
        Marshal.WriteInt32(packet, 0, unchecked((int)infoType));
        Marshal.WriteInt32(packet, 4, packetSize);
        Marshal.WriteInt32(packet, 8, unchecked((int)adapterId.LowPart));
        Marshal.WriteInt32(packet, 12, adapterId.HighPart);
        Marshal.WriteInt32(packet, 16, unchecked((int)id));
        return DisplayConfigGetDeviceInfo(packet) == 0;
    }

    private static LUID ReadLuid(IntPtr p, int offset) => new LUID
    {
        LowPart = (uint)Marshal.ReadInt32(p, offset),
        HighPart = Marshal.ReadInt32(p, offset + 4)
    };

    /// <summary>该路径的 GDI 设备名（DISPLAYCONFIG_SOURCE_DEVICE_NAME，84 字节：header 20 + WCHAR[32]）。</summary>
    private static string QuerySourceGdiDeviceName(LUID srcAdapter, uint srcId)
    {
        const int size = 84;
        var packet = Marshal.AllocHGlobal(size);
        try
        {
            if (!TryDisplayConfigQuery(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size, srcAdapter, srcId, packet))
                return "";
            return (Marshal.PtrToStringUni(packet + 20, 32) ?? "").Trim('\0').Trim();
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(packet); }
    }

    /// <summary>
    /// 问系统"这一块屏（target）能不能做高级颜色"。
    /// 先试 Win11 的 ADVANCED_COLOR_INFO_2（type 15, 36B，带 activeColorMode，能直接说现在是不是 FP16 scRGB 合成），
    /// 失败再退 Win10 1703+ 的 ADVANCED_COLOR_INFO（type 9, 32B）。两个都失败 ⇒ Available=false（调用方回退）。
    /// </summary>
    private static AdvancedColorInfo QueryAdvancedColorForTarget(LUID adapterId, uint targetId)
    {
        const int size2 = 36;   // header(20) + value(4) + colorEncoding(4) + bitsPerColorChannel(4) + activeColorMode(4)
        const int size1 = 32;   // header(20) + value(4) + colorEncoding(4) + bitsPerColorChannel(4)
        var packet = Marshal.AllocHGlobal(size2);
        try
        {
            // ── type 15 (Win11 GA+) ──
            // value: b0 advancedColorSupported b1 advancedColorActive b3 limitedByPolicy(系统策略强关)
            //        b4 hdrSupported b5 hdrUserEnabled b6 wideColorSupported b7 wideColorUserEnabled
            if (TryDisplayConfigQuery(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2, size2, adapterId, targetId, packet))
            {
                uint value = (uint)Marshal.ReadInt32(packet, 20);
                int bits = Marshal.ReadInt32(packet, 28);
                uint mode = (uint)Marshal.ReadInt32(packet, 32);   // 0=SDR(RGB888 合成) 1=WCG 2=HDR（后两者=FP16 scRGB 合成）
                bool forceDisabled = (value & 0b1000) != 0;
                bool supported = (value & 0b1) != 0 && !forceDisabled;
                // b1=advancedColorActive、b6=wideColorSupported、b7=wideColorUserEnabled。
                // "用户开了广色域"取 b7∪b1：b6 只是系统说"能做"，与 b0 同性质，不构成该屏的正向证据。
                bool active = (value & 0b10) != 0;
                bool wideUserOn = (value & 0b1000_0000) != 0;
                // mode != 0 ⇒ DWM 此刻就在用 FP16 scRGB 合成该屏 ⇒ 源是扩展范围的，这条最直接；
                // bits >= 10 ⇒ 面板实际位深；两者任一成立即可放行，与"系统说支持"是并列的正向证据。
                return new AdvancedColorInfo(true, supported || mode != 0 || bits >= 10, bits,
                                             (int)mode, active || wideUserOn);
            }

            // ── type 9 ──
            // value: b0 advancedColorSupported b1 advancedColorEnabled b2 wideColorEnforced b3 advancedColorForceDisabled
            if (TryDisplayConfigQuery(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, size1, adapterId, targetId, packet))
            {
                uint value = (uint)Marshal.ReadInt32(packet, 20);
                int bits = Marshal.ReadInt32(packet, 28);
                bool supported = (value & 0b1) != 0 && (value & 0b1000) == 0;
                bool activeOrEnforced = (value & 0b10) != 0 || (value & 0b100) != 0;
                // type 9 没有 activeColorMode 这个字段 ⇒ 如实报"未知"，别让调用方把缺字段读成"SDR 合成"
                return new AdvancedColorInfo(true, supported || bits >= 10, bits,
                                             DisplayInfo.ActiveModeUnknown, activeOrEnforced);
            }

            return default;   // Available=false ⇒ 调用方退回系统级 ACM
        }
        catch
        {
            return default;   // 分配失败/平台异常同样按"系统没答复"处理，不误判成"不支持"
        }
        finally { Marshal.FreeHGlobal(packet); }
    }

    // ── DISPLAYCONFIG 结构 (参考 wingdi.h 精确定义, 使用固定 Size 强制布局) ──
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    // DISPLAYCONFIG_VIDEO_SIGNAL_INFO = 32 字节
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    // DISPLAYCONFIG_PATH_TARGET_INFO 实际布局 (76 字节):
    // adapterId(8) + id(4) + modeInfoIdx(4) + statusFlags(4) + targetSize(8)
    // + refreshRate(8) + scanLineOrdering(8) + outputTechnology(4) + rotation(4)
    // + scaling(4) + targetAdapterId(8) + targetId(4) + sourceInfo(4) + flags(4)
    [StructLayout(LayoutKind.Sequential, Size = 76)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
        public DISPLAYCONFIG_2DREGION targetSize;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public DISPLAYCONFIG_RATIONAL scanLineOrdering;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public LUID targetAdapterId;
        public uint targetId;
        public uint sourceInfo;
        public uint flags;
    }

    // DISPLAYCONFIG_PATH_SOURCE_INFO = 20 字节
    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    // DISPLAYCONFIG_PATH_INFO = 20 + 76 + 4 = 100
    [StructLayout(LayoutKind.Sequential, Size = 100)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // DISPLAYCONFIG_MODE_INFO = 64 字节: infoType(4) + id(4) + adapterId(8) + union(48)
// 注意: 不能用 byte[] 字段 (会变成引用), 用固定连续字段
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public uint _union0;
        public uint _union1;
        public uint _union2;
        public uint _union3;
        public uint _union4;
        public uint _union5;
        public uint _union6;
        public uint _union7;
        public uint _union8;
        public uint _union9;
        public uint _union10;
        public uint _union11;
    }

    private static bool IsMonitorPrimary(nint hMonitor)
    {
        var info = new MONITORINFOEX { CbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(hMonitor, ref info) && (info.Flags & MONITORINFOF_PRIMARY) != 0;
    }
}

/// <summary>
/// 一次截图的<b>唯一色彩决定</b>：像素此刻在哪个色域、要搬到哪个色域、允许声明成什么、HDR 许不许写。
/// <para><b>为什么要有这个类型</b>（2026-09-22 审查轮，交接 §41）：在它之前，同一次截图里"目标色域"
/// 被算了两次、用四套阈值、问两块屏 ——
/// 声明侧走 <see cref="DisplayEnumerator.ClassifyDisplayColor"/>（EDID ΔG 闸门 + 逐屏 DISPLAYCONFIG），
/// 像素侧走 <c>GetDisplayNativeGamutTag</c>（宽松阈值，且被拿去做<b>源矩阵</b>），
/// 两边各自 <c>ResolveColorSpaceTag</c> 一次（一处传真实 hdrOutput、一处恒 false），
/// 且都问光标所在屏而非被截的那块屏。结果是同一块窄色域副屏上能同时命中
/// "判窄色域 ⇒ 不搬"与"按 P3 搬色 ⇒ 声明 sRGB"，两头错且互相抵消不了。
/// 本类型不是重构癖：它是让那四处<b>只能有一个口径</b>的最小形状。</para>
/// <para>用法纪律：在捕获完成后构造一次，<b>源屏由调用方给</b>（Core 不去猜光标屏），
/// 然后把同一个实例交给像素路与声明路。任何一侧都不许再自己解析用户下拉。</para>
/// </summary>
public sealed class ColorDecision
{
    /// <summary>被截的那块屏。null = 无从得知该屏的逐屏证据 ⇒ 分类给 Unknown：
    /// 源精度侧放行（多试一次 Float16 无害），声明侧不授权，也<b>不拿光标屏顶替</b>。</summary>
    public DisplayInfo? Source { get; init; }

    /// <summary>系统级 ACM（注册表算出来的单值）。逐屏证据齐了也不许拿它顶替逐屏判据。</summary>
    public bool SystemAcm { get; init; }

    /// <summary>三档分类，决定"能不能声明广色域/HDR"。</summary>
    public DisplayEnumerator.DisplayColorClass ScreenClass { get; init; }

    /// <summary>输入的 8-bit 像素<b>真实所在</b>的色域，即色域搬运所取的源。已过 ΔG 闸门。</summary>
    public string SourceTag { get; init; } = "sRGB";

    /// <summary>输出声明与目标色域共用的一份：<b>搬要去哪儿 + 容器写什么，是同一个值</b>。</summary>
    public string TargetTag { get; init; } = "sRGB";

    /// <summary>钳制之后的 HDR 开关（容器许不许写 PQ）。</summary>
    public bool HdrOutput { get; init; }

    /// <summary>是否发生过"广色域目标被收口成 sRGB"。只为日志/审计，不参与判定。</summary>
    public bool GamutClamped { get; init; }

    /// <summary>是否发生过"HDR 声称被收口成 SDR 色调映射"。</summary>
    public bool HdrClamped { get; init; }

    /// <summary>逐屏证据行（判定输入的原样读数），任何一次收口都要把它打进日志。</summary>
    public string Evidence { get; init; } = "";

    /// <summary>用户下拉的<b>原样</b>取值（含 "System"）。只有一个消费者需要它：
    /// GainMap 要区分"用户手动要 BT.2020"与"跟随显示器在 P3 屏上自动解析出 BT.2020"。</summary>
    public string? RequestedTag { get; init; }

    /// <summary>目标色域是显式选的广色域（不是 "System" 解析来的）。
    /// <para>Ultra HDR 的 base layer 按约定是 sRGB/Display-P3，所以增益图默认全在 BT.709 里；
    /// 但用户手动选了 BT.2020 时，他要的是"整张增益图都在 2020"—— 那种模式下 base、增益图、
    /// 声明三件一起走 2020（见 <c>JpegGainMapEncoder</c>）。自动档不算。</para></summary>
    public bool ExplicitWideTarget { get; init; }

    /// <summary>这块屏的 EDID 原生原色<b>读到了没有</b>。false ⇒ <see cref="SourceTag"/> 只是"没有反证"
    /// 的默认 sRGB，像素路可以据此选择回退到"用显示器 ICC 当源"那条老路，而不是假装确证过。</summary>
    public bool NativeGamutKnown => Source?.NativeGamutKnown ?? false;

    /// <summary>这次截图<b>到底需不需要搬色</b>。色彩管理不再有开关（2026-09-22 用户拍板：
    /// 默认就映射进标准色域），于是"要不要真的跑一遍矩阵"必须由决定自己回答，而不是由一个 UI 布尔回答。
    /// <para>两个消费方：①像素路 —— false 时矩阵是恒等变换，跑了也只是白烧一次 CPU；
    /// ②8-bit 的 GPU 纹理直通 —— 只有不需要搬色时才许直通，因为直通会绕开 CPU 侧矩阵，
    /// 而 NVENC 下发的 H.273 <c>matrixCoefficients</c> 是 YUV↔RGB 系数、<b>不是</b>色域转换。</para></summary>
    public bool NeedsMapping => SourceTag != TargetTag;

    /// <summary>这块屏该不该建 Float16 源。与 <see cref="TargetTag"/> 同源于一次分类，不重复判。</summary>
    public bool ExtendedRangeAuthorized
        => DisplayEnumerator.CanDeliverExtendedRange(Source, SystemAcm);

    /// <summary>
    /// 构造一次决定。<paramref name="source"/> 必须是<b>被截那块屏</b>；传 null 表示不知道，
    /// 此时按"逐屏证据缺失"处理（分类给 Unknown ⇒ 放行源精度但不授权声明），不去猜光标屏。
    /// </summary>
    public static ColorDecision Build(
        DisplayInfo? source, string? requestedTag, bool hdrOutput, bool systemAcm)
    {
        var cls = DisplayEnumerator.ClassifyDisplayColor(source, systemAcm);
        bool classKnown = cls != DisplayEnumerator.DisplayColorClass.Unknown;
        var collapse = new System.Func<string?, string>(t =>
            ColorManagement.ColorProfileProvider.CollapseSelectableTag(t));

        // ① HDR 声称：只有确证处于 HDR 档的屏才许写 PQ。Unknown 不授权（但见调用侧：不施加钳制）。
        bool hdr = hdrOutput;
        bool hdrClamped = false;
        if (hdr && !DisplayEnumerator.CanClaimHdr(cls))
        {
            hdr = false;
            hdrClamped = true;
        }

        // ② 目标色域：先按用户下拉解析（"跟随显示器"在这里落地），
        //    再【在解析之后】施加广色域钳制 —— 顺序是这条决定的要点：
        //    钳制若写在解析之前、且像旧实现那样显式放过 "System"，默认档就完全不经钳制
        //    （§41.2：用户不改下拉 ⇒ 窄屏照样拿到 BT.2020 声明）。
        string target;
        if (string.IsNullOrWhiteSpace(requestedTag) || requestedTag == "System")
        {
            target = hdr ? "BT2020"
                : systemAcm && source?.NativeGamutWide == true ? collapse(source!.NativeGamutTag)
                : "sRGB";
        }
        else target = collapse(requestedTag);

        bool gamutClamped = false;
        if (classKnown && !DisplayEnumerator.CanClaimWideGamut(cls) && target != "sRGB")
        {
            target = "sRGB";
            gamutClamped = true;
        }

        // ③ 源色域：只有"系统确实把这块屏按扩展范围在合成"时，8-bit 字节才可能不在 sRGB 里。
        // 取 EDID 确证过的细标签（不 collapse —— 源是 DisplayP3 就该按 DisplayP3 反解）。
        // 读不到 EDID 时 NativeGamutTag 恒为 "sRGB" ⇒ 按 sRGB 搬、按搬后的结果声明，两边仍然一致。
        string src = "sRGB";
        if (systemAcm && cls is DisplayEnumerator.DisplayColorClass.WideGamutSdr
                                          or DisplayEnumerator.DisplayColorClass.Hdr
                            && source?.NativeGamutKnown == true && source.NativeGamutWide)
            src = source.NativeGamutTag;

        return new ColorDecision
        {
            Source = source,
            SystemAcm = systemAcm,
            ScreenClass = cls,
            SourceTag = src,
            TargetTag = target,
            HdrOutput = hdr,
            GamutClamped = gamutClamped,
            HdrClamped = hdrClamped,
            RequestedTag = requestedTag,
            // "显式要广色域" = 下拉本身就不是 System/空，且收口之后确实是个广色域。
            // 注意用的是收口前的原样值：System 在 P3 屏上也会解析成 BT2020，但那不是用户的手。
            ExplicitWideTarget = !string.IsNullOrWhiteSpace(requestedTag) && requestedTag != "System"
                                 && collapse(requestedTag) != "sRGB",
            Evidence = DisplayEnumerator.DescribeColorEvidence(source, systemAcm),
        };
    }

    /// <summary>审计/日志用的一行。把"决定"本身而不是各侧局部变量打出来，才看得出两侧有没有分叉。</summary>
    public string Describe() =>
        $"分类={ScreenClass} 源色域={SourceTag} 目标={TargetTag} HDR={(HdrOutput ? "写PQ" : "SDR")}" +
        (GamutClamped ? " [广色域已收口]" : "") + (HdrClamped ? " [HDR已收口]" : "");
}


