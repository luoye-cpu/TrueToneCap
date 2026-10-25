# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Core -Language CSharp -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class WgcProbe
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string s, uint len, out IntPtr h);
    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr hString, ref Guid iid, out IntPtr factory);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateForMonitorDel(IntPtr thisPtr, IntPtr hmon, ref Guid riid, out IntPtr result);
    // IGraphicsCaptureItem : IInspectable(槽0..2) => get_DisplayName=3, get_Size=4
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetNameDel(IntPtr thisPtr, out IntPtr hstring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSizeDel(IntPtr thisPtr, out SIZE_I32 size);
    [StructLayout(LayoutKind.Sequential)] public struct SIZE_I32 { public int W, H; }

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet=CharSet.Unicode, EntryPoint="WindowsGetStringRawBuffer")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", EntryPoint="WindowsDeleteString")]
    static extern int WindowsDeleteString(IntPtr h);

    const string CLS = "Windows.Graphics.Capture.GraphicsCaptureItem";
    static Guid IAF      = new Guid("00000035-0000-0000-C000-000000000046");
    static Guid BOGUS    = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // = IGraphicsCaptureItem（类默认接口）
    static Guid REALINTOP = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // = IGraphicsCaptureItemInterop

    static string H(int hr) { return hr < 0 ? ("0x" + hr.ToString("X8")) : ("S_OK/0x" + hr.ToString("X8")); }

    public static string Run()
    {
        var sb = new StringBuilder();
        IntPtr hs; int hr = WindowsCreateString(CLS, (uint)CLS.Length, out hs);
        sb.AppendLine("WindowsCreateString      -> " + H(hr));
        IntPtr fac; hr = RoGetActivationFactory(hs, ref IAF, out fac);
        sb.AppendLine("RoGetActivationFactory   -> " + H(hr) + "  fac=" + (fac != IntPtr.Zero));
        if (fac == IntPtr.Zero) return sb.ToString();

        IntPtr p1, p2;
        int r1 = Marshal.QueryInterface(fac, ref BOGUS, out p1);
        int r2 = Marshal.QueryInterface(fac, ref REALINTOP, out p2);
        sb.AppendLine("QI 79C3F95B (代码用的)    -> " + H(r1) + "  ptr=" + (p1 != IntPtr.Zero));
        sb.AppendLine("QI 3628E81B (头文件认定)  -> " + H(r2) + "  ptr=" + (p2 != IntPtr.Zero));

        if (p2 == IntPtr.Zero) return sb.ToString();
        IntPtr vtbl = Marshal.ReadIntPtr(p2);
        // IUnknown 占 0..2，CreateForWindow=3，CreateForMonitor=4
        IntPtr slot4 = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
        var fn = (CreateForMonitorDel)Marshal.GetDelegateForFunctionPointer(slot4, typeof(CreateForMonitorDel));
        POINT pt; GetCursorPos(out pt);
        IntPtr mon = MonitorFromPoint(pt, 1 /*MONITOR_DEFAULTTONEAREST*/);
        sb.AppendLine("hmonitor                 = 0x" + mon.ToInt64().ToString("X"));
        IntPtr item;
        int r3 = fn(p2, mon, ref BOGUS, out item);   // riid 传类 IID，与现代码一致
        sb.AppendLine("CreateForMonitor(3628E81B)   -> " + H(r3) + "  item=" + (item != IntPtr.Zero));
        Console.Write(sb.ToString()); sb.Clear(); Console.Out.Flush();   // 先交出手上的行，再碰 item 的 vtable
        if (item != IntPtr.Zero)
        {
            try
            {
                IntPtr ivt = Marshal.ReadIntPtr(item);
                IntPtr pName = Marshal.ReadIntPtr(ivt, 6 * IntPtr.Size);
                IntPtr pSize = Marshal.ReadIntPtr(ivt, 7 * IntPtr.Size);
                Console.WriteLine("  vtable=0x" + ivt.ToString("X") + "  slot6(name)=0x" + pName.ToString("X") + "  slot7(size)=0x" + pSize.ToString("X"));
                Console.Out.Flush();
                var gsz = (GetSizeDel)Marshal.GetDelegateForFunctionPointer(pSize, typeof(GetSizeDel));
                SIZE_I32 s; int rs = gsz(item, out s);
                Console.WriteLine("   get_Size hr=" + H(rs) + " -> " + s.W + "x" + s.H); Console.Out.Flush();
                var gnm = (GetNameDel)Marshal.GetDelegateForFunctionPointer(pName, typeof(GetNameDel));
                IntPtr hName; int rn = gnm(item, out hName);
                uint len; IntPtr raw = WindowsGetStringRawBuffer(hName, out len);
                string nm = (raw == IntPtr.Zero) ? "(null)" : Marshal.PtrToStringUni(raw, (int)len);
                Console.WriteLine("   get_DisplayName hr=" + H(rn) + " len=" + len + " -> \"" + nm + "\"");
                WindowsDeleteString(hName);
                Marshal.Release(item);
            }
            catch (Exception ex) { Console.WriteLine("   ⚠ item 内省失败: " + ex.GetType().Name + " :: " + ex.Message); }
        }
        if (p1 != IntPtr.Zero) Marshal.Release(p1);
        Marshal.Release(p2);
        Marshal.Release(fac);
        return sb.ToString();
    }
}
'@
[WgcProbe]::Run()
