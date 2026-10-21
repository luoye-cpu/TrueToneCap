# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap 实机卡死测试脚本 (2026-08-10)
# 用法: .\test_hang.ps1 [-TargetPid 11960]
param([int]$TargetPid = 0)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class U32 {
    [DllImport("user32.dll")] public static extern int SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public const uint WM_NULL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_S = 0x53;
    public static bool IsResponsive(IntPtr hwnd, uint timeoutMs) {
        UIntPtr res;
        return SendMessageTimeout(hwnd, WM_NULL, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out res) != 0;
    }
    public static void SendCtrlShiftS() {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_S, 0, 0, UIntPtr.Zero);
        keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
"@

if ($TargetPid -eq 0) { $proc = Get-Process TrueToneCap -ErrorAction SilentlyContinue | Select-Object -First 1 } else { $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue }
if (-not $proc) { "❌ TrueToneCap 进程未找到"; exit 1 }

$hwnd = $proc.MainWindowHandle
"进程: $($proc.ProcessName) PID=$($proc.Id) HWND=0x$($hwnd.ToString('X'))"

function Test-UI([string]$label) {
    if ($hwnd -eq 0) { "$label : 无主窗口句柄"; return }
    $ok = [U32]::IsResponsive($hwnd, 1500)
    if ($ok) { "$label : ✅ UI 线程响应正常 (1.5s 内)" }
    else { "$label : ❌ UI 线程无响应 (卡死!)" }
}

Test-UI "启动后"

# 触发截图热键 (Ctrl+Shift+S)
"--- 发送 Ctrl+Shift+S ---"
[U32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 300
[U32]::SendCtrlShiftS()
Start-Sleep -Seconds 3
Test-UI "截图触发后 3s"

Start-Sleep -Seconds 3
Test-UI "截图触发后 6s"

# 查看最新日志 —— 日志目录从**被观察的那个进程自己的路径**推出来（LogDirectory = 程序目录\log）。
# 原先硬写 bin\x64\Debug\…\log：跑 Release 树时目录不存在 ⇒ 这里退化成"无日志目录"一行提示，
# 卡死判据少了整个证据面却仍然跑到底、不报错（同源之后这类漂移不可能出现）。
$logDir = $null
if ($proc -and $proc.Path) { $logDir = Join-Path (Split-Path $proc.Path -Parent) 'log' }
if (-not $logDir -or -not (Test-Path $logDir)) {
    "⚠ 取不到被观察进程的日志目录（进程: $($proc.Path)）⇒ 下面的判定缺少证据面，不要当通过"
} else {
$log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    "--- 日志尾部 ($($log.Name)) ---"
    Get-Content $log.FullName -Tail 25
} else { "无日志文件: $logDir" }
}

"进程 CPU 时间: $([math]::Round($proc.TotalProcessorTime.TotalSeconds,1))s  内存: $([math]::Round($proc.WorkingSet64/1MB,0))MB"
