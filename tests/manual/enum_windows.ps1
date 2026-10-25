# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 枚举指定进程的所有窗口
param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class EW {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    public struct R { public int L, T, Rt, B; }
    public static List<string> Result = new List<string>();
    public static void Run(uint targetPid) {
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) {
                var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
                var c = new StringBuilder(256); GetClassNameW(h, c, 256);
                var r = new R(); GetWindowRect(h, out r);
                Result.Add(string.Format("hwnd=0x{0:X} visible={1} class='{2}' title='{3}' rect=({4},{5})-({6},{7})",
                    h.ToInt64(), IsWindowVisible(h), c, t, r.L, r.T, r.Rt, r.B));
            }
            return true;
        }, IntPtr.Zero);
    }
}
"@
[EW]::Run($TargetPid)
[EW]::Result | ForEach-Object { Write-Host $_ }
Write-Host "TOTAL=$([EW]::Result.Count)"
