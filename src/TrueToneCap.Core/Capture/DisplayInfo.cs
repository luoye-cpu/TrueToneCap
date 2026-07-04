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
    public bool IsHdr { get; init; }
    public int BitsPerColor { get; init; } = 8; // 8 (SDR) / 10 (HDR)
    public ColorSpaceType ColorSpace { get; init; }
    public Format SupportedFormat { get; init; }
    public string AdapterName { get; init; } = "";

    public override string ToString() =>
        $"[{Index}] {Name} ({Width}x{Height}) {(IsHdr ? "HDR" : "SDR")} {(IsPrimary ? "(主)" : "")}";
}

/// <summary>显示器枚举器。通过 DXGI 枚举所有活动显示器。</summary>
public static class DisplayEnumerator
{
    // ────── Win32 鼠标/显示器 API ──────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

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

                        // 判断 HDR: DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12
                        var cs = desc1.ColorSpace;
                        bool isHdr = (int)cs == 12; // HDR10 / ST.2084
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
                            BitsPerColor = (int)desc1.BitsPerColor,
                            ColorSpace = cs,
                            SupportedFormat = fmt,
                            AdapterName = adapterDesc.Description
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

    private static bool IsMonitorPrimary(nint hMonitor)
    {
        var info = new MONITORINFOEX { CbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(hMonitor, ref info) && (info.Flags & MONITORINFOF_PRIMARY) != 0;
    }
}
