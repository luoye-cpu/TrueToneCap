# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TTC_HC 窗口诊断脚本
param([string]$Action = "check")  # check | sendmouse | cancel

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
    [DllImport("user32.dll")] public static extern IntPtr FindWindowW(string c, string t);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    public struct R { public int L, T, Rt, B; }
}
"@

$hwnd = [W]::FindWindowW("TTC_HC", $null)
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "RESULT: NO_TTC_HC_WINDOW"
    exit 1
}

if ($Action -eq "check") {
    Write-Host "RESULT: HWND=0x$($hwnd.ToString('X'))"
    Write-Host "VISIBLE=$([W]::IsWindowVisible($hwnd))"
    $fg = [W]::GetForegroundWindow()
    Write-Host "FOREGROUND_IS_TTC=$($fg -eq $hwnd) FG=0x$($fg.ToString('X'))"
    $r = New-Object W+R
    [W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    Write-Host "RECT=$($r.L),$($r.T),$($r.Rt),$($r.B) SIZE=$($r.Rt-$r.L)x$($r.B-$r.T)"
    # 检查 Z 序（下一个兄弟窗口）
    $next = [W]::GetWindow($hwnd, 2) # GW_HWNDNEXT
    if ($next -ne [IntPtr]::Zero) {
        $sb = New-Object System.Text.StringBuilder 256
        [W]::GetWindowTextW($next, $sb, 256) | Out-Null
        $cls = New-Object System.Text.StringBuilder 256
        [W]::GetClassNameW($next, $cls, 256) | Out-Null
        Write-Host "NEXT_IN_ZORDER=0x$($next.ToString('X')) class=$($cls.ToString()) title='$($sb.ToString())'"
    } else {
        Write-Host "NEXT_IN_ZORDER=none (topmost)"
    }
    $pid2 = 0
    [W]::GetWindowThreadProcessId($hwnd, [ref]$pid2) | Out-Null
    Write-Host "OWNER_PID=$pid2"
}
elseif ($Action -eq "sendmouse") {
    # 发送 WM_MOUSEMOVE 到窗口中心 + WM_LBUTTONDOWN/UP
    $r = New-Object W+R
    [W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $cx = [int](($r.L + $r.Rt) / 2); $cy = [int](($r.T + $r.B) / 2)
    $lparam = ($cy -shl 16) -bor ($cx -band 0xFFFF)
    # WM_MOUSEMOVE=0x0200, WM_LBUTTONDOWN=0x0201, WM_LBUTTONUP=0x0202
    $m1 = [W]::PostMessageW($hwnd, 0x0200, [IntPtr]::Zero, [IntPtr]$lparam)
    Write-Host "POST_WM_MOUSEMOVE=$m1"
    $m2 = [W]::PostMessageW($hwnd, 0x0201, [IntPtr]1, [IntPtr]$lparam)
    Write-Host "POST_WM_LBUTTONDOWN=$m2"
    $m3 = [W]::PostMessageW($hwnd, 0x0202, [IntPtr]::Zero, [IntPtr]$lparam)
    Write-Host "POST_WM_LBUTTONUP=$m3"
}
elseif ($Action -eq "cancel") {
    # 发送 Esc 键 (WM_KEYDOWN=0x0100, VK_ESC=0x1B)
    $k = [W]::PostMessageW($hwnd, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    Write-Host "POST_ESC=$k"
}
