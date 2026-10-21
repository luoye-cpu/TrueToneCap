# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'Stop'
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Topo
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFOEX {
        public int cb; public RECT rc; public RECT rcWork; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name; }

    // DEVMODEW 真实布局：wingdi.h 处于 pshpack2 中 => Pack=2
    [StructLayout(LayoutKind.Sequential, Pack=2, CharSet=CharSet.Unicode)]
    public struct DEVMODEW {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; // 64 B
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;           // 8 B -> 72
        public uint dmFields;                                                          // 76
        public int dmPositionX, dmPositionY;                                           // 84
        public uint dmDisplayOrientation, dmDisplayFixedOutput;                        // 92
        public short dmColor, dmDuplex, dmYResolution, dmTTOption;                     // 100
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmCollate_dmFormName_PLACEHOLDER; // 见下注
    }
    // 注：dmCollate 之后还有 dmFormName[32](64B)+dmLogPixels(2)+dmBitsPerPel(4)+dmPelsWidth(4)+dmPelsHeight(4)
    //     => dmPelsWidth 在偏移 176、dmPelsHeight 在 180。与其把整条抄错，不如直接按偏移取字节。

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DISPLAY_DEVICE {
        public int size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string deviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string deviceString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string deviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string deviceKey;
        public uint state; }

    delegate bool MonEnum(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonEnum cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, int i, ref DISPLAY_DEVICE d, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, IntPtr dm);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetSystemMetrics(int index);

    static List<IntPtr> mons = new List<IntPtr>();
    static bool Cb(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) { mons.Add(h); return true; }

    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    static string ModeRes(string dev)
    {
        // DEVMODEW 布局靠手算有 4 字节风险 => 不猜，把候选偏移全打出来，
        // 由调用方拿已知的 monitor rect 反向对账，谁等于 rect 谁才是 dmPelsWidth/Height。
        IntPtr buf = Marshal.AllocHGlobal(400);
        try {
            for (int i = 0; i < 400; i++) Marshal.WriteByte(buf, i, 0);
            if (!EnumDisplaySettingsW(dev, -1, buf)) return "EnumDisplaySettings 失败"; // ENUM_CURRENT_SETTINGS=-1
            var p = new StringBuilder();
            for (int off = 164; off <= 184; off += 4)
                p.AppendFormat("[{0}]={1} ", off, Marshal.ReadInt32(buf, off));
            return p.ToString().TrimEnd();
        } finally { Marshal.FreeHGlobal(buf); }
    }

    public static string Run()
    {
        SetProcessDPIAware();
        var sb = new StringBuilder();
        sb.AppendLine("进程已 SetProcessDPIAware()（系统级）=> GetMonitorInfo 的 rect 为物理像素");
        sb.AppendLine(string.Format("虚拟桌面(系统级): 原点=({0},{1}) 尺寸={2}x{3}",
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)));
        mons.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        sb.AppendLine("EnumDisplayMonitors 返回可见显示器数 = " + mons.Count);

        foreach (var h in mons)
        {
            var mi = new MONITORINFOEX(); mi.cb = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (!GetMonitorInfo(h, ref mi)) { sb.AppendLine("  GetMonitorInfo 失败"); continue; }
            int w = mi.rc.R - mi.rc.L, ht = mi.rc.B - mi.rc.T;
            int cx = (mi.rc.L + mi.rc.R) / 2, cy = (mi.rc.T + mi.rc.B) / 2;
            sb.AppendLine(string.Format("hmonitor=0x{0:X}  name={1}  rect=({2},{3})-({4},{5})  {6}x{7}  primary={8}  中心=({9},{10})",
                h.ToInt64(), mi.name, mi.rc.L, mi.rc.T, mi.rc.R, mi.rc.B, w, ht, (mi.flags & 1) != 0, cx, cy));
            sb.AppendLine("   EnumDisplaySettings 当前模式 = " + ModeRes(mi.name) + "   [与 WGC item.Size 应相等]");

            var dd = new DISPLAY_DEVICE(); dd.size = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (EnumDisplayDevices(mi.name, 0, ref dd, 0)) {
                sb.AppendLine(string.Format("   适配器: active={0}  id={1}", (dd.state & 1) != 0, dd.deviceID.TrimEnd('\0')));
                var t = new DISPLAY_DEVICE(); t.size = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(mi.name, 0, ref t, 1)) {
                    var s = t.deviceID.TrimEnd('\0');
                    sb.AppendLine("   目标设备ID = " + s.Split('#').Length + " 段: " + s);
                    int du = s.IndexOf("UID");
                    if (du > 0) sb.AppendLine("   ↳ DisplayId 的 (Luid, Id) 里的 Id = " + s.Substring(du + 4));
                } else sb.AppendLine("   ⚠ EnumDisplayDevices(DEVICE_ID=1) 失败");
            }

            bool self = (cx >= mi.rc.L && cx < mi.rc.R && cy >= mi.rc.T && cy < mi.rc.B);
            sb.AppendLine("   [几何] 中心点落在自身 rect 内 = " + self);
            foreach (var h2 in mons) {
                if (h2 == h) continue;
                var m2 = new MONITORINFOEX(); m2.cb = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(h2, ref m2)) continue;
                if (cx >= m2.rc.L && cx < m2.rc.R && cy >= m2.rc.T && cy < m2.rc.B)
                    sb.AppendLine("   ⚠ [几何歧义] 中心点 0x" + h.ToInt64().ToString("X") + " 落进了 0x" + h2.ToInt64().ToString("X"));
            }
        }
        return sb.ToString();
    }
}
'@
[Topo]::Run()
