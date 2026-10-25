# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Core -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WgcMon
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFOEX {
        public int cb; public RECT rc; public RECT rcWork; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE_I32 { public int W, H; }

    [DllImport("user32.dll", SetLastError=true)] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    delegate bool MonEnum(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonEnum cb, IntPtr d);

    [DllImport("combase.dll", CharSet=CharSet.Unicode)] static extern int WindowsCreateString(string s, uint len, out IntPtr h);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr hString, ref Guid iid, out IntPtr factory);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet=CharSet.Unicode, EntryPoint="WindowsGetStringRawBuffer")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", EntryPoint="WindowsDeleteString")]
    static extern int WindowsDeleteString(IntPtr h);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateForMonitorDel(IntPtr self, IntPtr hmon, ref Guid riid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetNameDel(IntPtr self, out IntPtr hstring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSizeDel(IntPtr self, out SIZE_I32 size);

    const string CLS = "Windows.Graphics.Capture.GraphicsCaptureItem";
    static Guid IAF      = new Guid("00000035-0000-0000-C000-000000000046");
    static Guid INTEROP   = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // 真 interop IID
    static Guid CLASS_IID = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // 类 IID，作 riid
    // 投影类挂的那个（本轮实测），拿来当 riid 会得到 E_NOINTERFACE
    static Guid PROJECTION = new Guid("e430267b-debd-3a92-b2c7-287da8bef945");

    static List<IntPtr> mons = new List<IntPtr>();
    static bool Cb(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) { mons.Add(h); return true; }
    static string H(int hr) { return hr < 0 ? ("0x" + hr.ToString("X8")) : "S_OK"; }

    static string Probe(IntPtr interop, IntPtr mon, Guid riid)
    {
        // ⚠ vtable 必须取 **interop 指针** 的，不是 IActivationFactory 的：
        //   IGraphicsCaptureItemInterop 派生自 IUnknown ⇒ 槽 3=CreateForWindow、4=CreateForMonitor。
        //   拿工厂指针的槽 4 去调是越界调用，会直接把宿主进程打崩（本机踩过一次：零输出+退出码 2）。
        IntPtr vtbl = Marshal.ReadIntPtr(interop);
        var fn = (CreateForMonitorDel)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size), typeof(CreateForMonitorDel));
        IntPtr item; int hr = fn(interop, mon, ref riid, out item);
        if (hr < 0 || item == IntPtr.Zero) return H(hr);
        try
        {
            IntPtr ivt = Marshal.ReadIntPtr(item);
            var gsz = (GetSizeDel)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(ivt, 7 * IntPtr.Size), typeof(GetSizeDel));
            var gnm = (GetNameDel)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(ivt, 6 * IntPtr.Size), typeof(GetNameDel));
            SIZE_I32 s; int rs = gsz(item, out s);
            IntPtr hN; int rn = gnm(item, out hN);
            uint len; IntPtr raw = WindowsGetStringRawBuffer(hN, out len);
            string nm = raw == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUni(raw, (int)len);
            WindowsDeleteString(hN);
            return "S_OK item=" + s.W + "x" + s.H + " name=\"" + nm + "\" (get_Size hr=" + H(rs) + ")";
        }
        finally { Marshal.Release(item); }
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        // 每显示器 DPI 感知：不这么做，混合缩放下 GetMonitorInfo 的 rect 是虚拟化过的，
        // 与 item.Size（物理像素）对不上会被误读成"截错屏"。
        bool dpiOk = SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2);
        sb.AppendLine("SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2) = " + dpiOk);

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        sb.AppendLine("可见显示器数 = " + mons.Count);

        IntPtr hs; WindowsCreateString(CLS, (uint)CLS.Length, out hs);
        IntPtr fac; int hrf = RoGetActivationFactory(hs, ref IAF, out fac);
        sb.AppendLine("RoGetActivationFactory -> " + H(hrf));
        IntPtr pInter; int hqi = Marshal.QueryInterface(fac, ref INTEROP, out pInter);
        sb.AppendLine("QI 3628E81B -> " + H(hqi) + (hqi < 0 ? "  ⇒ 后面全免谈" : ""));
        if (hqi < 0) return sb.ToString();
        Console.Write(sb.ToString()); Console.Out.Flush();   // 先交出手上的行，再逐台量

        foreach (var mon in mons)
        {
            var mi = new MONITORINFOEX(); mi.cb = Marshal.SizeOf(typeof(MONITORINFOEX));
            GetMonitorInfo(mon, ref mi);
            int w = mi.rc.R - mi.rc.L, ht = mi.rc.B - mi.rc.T;
            uint dx, dy; GetDpiForMonitor(mon, 0, out dx, out dy);
            Console.WriteLine(string.Format("--- hmonitor=0x{0:X}  name={1}  primary={2} ---",
                mon.ToInt64(), mi.name.TrimStart('\\','.'), (mi.flags & 1) != 0));
            Console.WriteLine(string.Format("    rect=({3},{4})-({5},{6}) {7}x{8}   DPI={9} ({10:F0}%)",
                mon.ToInt64(), mi.name, mi.flags, mi.rc.L, mi.rc.T, mi.rc.R, mi.rc.B, w, ht, dx, dx / 96.0 * 100));
            Console.WriteLine("    CreateForMonitor(类 IID 79C3F95B)   -> " + Probe(pInter, mon, CLASS_IID));
            Console.Out.Flush();
            Console.WriteLine("    CreateForMonitor(投影 GUID e430267b) -> " + Probe(pInter, mon, PROJECTION));
            Console.Out.Flush();
        }
        Marshal.Release(pInter); Marshal.Release(fac);
        return "";
    }
}
'@
[WgcMon]::Run()
