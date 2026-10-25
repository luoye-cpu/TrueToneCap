# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 探针对象 = publish/ 下【最新的那个包】。原先写死 TrueToneCap-v0.3.4-beta-pipelinefix，
# 而交付包是一次性产物、会被清理（2026-09-20 整洁审计清掉了 7 个历史变体）
# ⇒ 写死包名的探针在清包当天就作废。改成按修改时间取最新，并保留 -Exe 覆盖。
param([string]$Exe = '')

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

if (-not $Exe) {
    $Exe = Get-ChildItem (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) `
                       'publish\TrueToneCap-v*\TrueToneCap.exe') -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Exe -or -not (Test-Path $Exe)) { Write-Host "找不到要探的 TrueToneCap.exe（用 -Exe 指定）"; exit 1 }
Write-Host "探针对象: $Exe"
$p = Start-Process -FilePath $exe -PassThru
Write-Host ("启动 PID={0}" -f $p.Id)
Start-Sleep -Seconds 12

$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if (-not $alive) {
    Write-Host "❌ 进程已退出（退出码见下）"
    try { Write-Host ("ExitCode = {0}" -f $p.ExitCode) } catch { Write-Host "拿不到 ExitCode" }
} else {
    Write-Host ("✅ 进程存活: MainWindowTitle='{0}' 窗口尺寸={1}x{2} 内存={3:N0}KB" -f `
        $alive.MainWindowTitle, $alive.MainWindowHandle, '', ($alive.WorkingSet64/1KB))
}

# 枚举该 PID 的所有顶层窗口（WinUI 的主窗口标题可能为空，靠枚举判断有没有窗口）
$found = New-Object System.Collections.ArrayList
$cb = [W+EnumProc] {
    param($h, $l)
    $pid2 = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $p.Id) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][W]::GetWindowText($h, $sb, $sb.Capacity)
        $len = [W]::GetWindowTextLength($h)
        [void]$found.Add([pscustomobject]@{
            Hwnd = $h; Visible = [W]::IsWindowVisible($h); Title = $sb.ToString(); TitleLen = $len })
    }
    return $true
}
[void][W]::EnumWindows($cb, [IntPtr]::Zero)
Write-Host ("该 PID 的顶层窗口数 = {0}" -f $found.Count)
$found | ForEach-Object { Write-Host ("  hwnd={0} visible={1} title='{2}'" -f $_.Hwnd, $_.Visible, $_.Title) }

$main = $found | Where-Object { $_.Visible } | Select-Object -First 1
if ($main) {
    [void][W]::SetForegroundWindow($main.Hwnd)
    Start-Sleep -Milliseconds 900
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
    $out = "C:\PLAN\TrueToneCap\artifacts\shot_newexe.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("📸 截图已存: {0} ({1}x{2})" -f $out, $bounds.Width, $bounds.Height)
} else {
    Write-Host "没有可见顶层窗口（可能进托盘了）"
}
