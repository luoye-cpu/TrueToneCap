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

                        displays.Add(new DisplayInfo
                        {
                            Index = displays.Count,
                            Name = desc.DeviceName,
                            MonitorHandle = desc.Monitor,
                            X = desc.DesktopCoordinates.Left,
                            Y = desc.DesktopCoordinates.Top,
                            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top,
                            IsPrimary = IsMonitorPrimary(desc.Monitor),
                            IsHdr = isHdr,
                            SupportsHdr = supportsHdr,
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

            int pathSize = 100; // sizeof(DISPLAYCONFIG_PATH_INFO)
            int modeSize = 64;  // sizeof(DISPLAYCONFIG_MODE_INFO)
            var paths = Marshal.AllocHGlobal((int)(pathSize * nPaths));
            var modes = Marshal.AllocHGlobal((int)(modeSize * nModes));
            try
            {
                uint actualPaths = nPaths, actualModes = nModes;
                int qr = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref actualPaths, paths, ref actualModes, modes, out _);
                if (qr != 0)
                    return 0;

                // 遍历路径, 对每个路径查询 SDR 白点
                // DISPLAYCONFIG_PATH_INFO 布局 (100 字节):
                //   [0..19]   sourceInfo (LUID 8 + id 4 + modeInfoIdx 4 + statusFlags 4)
                //   [20..95]  targetInfo (LUID 8 + id 4 + modeInfoIdx 4 + statusFlags 4 + ...)
                //   [96..99]  flags
                for (uint i = 0; i < actualPaths; i++)
                {
                    var pathPtr = paths + (int)(i * pathSize);
                    // targetInfo 从偏移 20 开始:
                    //   adapterId @ 20 (LUID 8), id @ 28, modeInfoIdx @ 32, statusFlags @ 36
                    //   targetSize @ 40, refreshRate @ 48, scanLineOrdering @ 56, ...
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

