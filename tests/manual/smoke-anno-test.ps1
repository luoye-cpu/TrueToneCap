# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap 标注冒烟测试 v7 - 标注前后 PrintWindow 对比
param(
    [string]$Exe = "c:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Debug\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe"
)
$ErrorActionPreference = "Continue"

Get-Process TrueToneCap -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W7 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public struct RECT { public int l, t, r, b; }
    public static IntPtr FindClass(string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, 64);
            if (sb.ToString() == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(120);
    }
    public static void Drag(int x1, int y1, int x2, int y2) {
        SetCursorPos(x1, y1); System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        for (int i = 1; i <= 5; i++) {
            SetCursorPos(x1 + (x2 - x1) * i / 5, y1 + (y2 - y1) * i / 5);
            System.Threading.Thread.Sleep(40);
        }
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(150);
    }
}
"@

# 日志目录必须从【要驱动的那个 exe】推出来，不许另写一条绝对路径。
# 原先这里硬写 bin\x64\Debug\…\log，而实际跑的常常是 Release 树 ⇒ 目录不存在 ⇒
# $before/$after 永远是 0，"轮询有没有新日志"变成永远观察不到变化 —— 判据静默失效
# （2026-09-20 e2e 那轮就是据此判红的）。同源之后这类漂移不可能出现。
$logDir = Join-Path (Split-Path $Exe -Parent) 'log'
if (-not (Test-Path $logDir)) {
    Write-Host "❌ 日志目录不存在: $logDir ⇒ 本脚本靠日志变化判定触发，看不到就等于没判，直接失败（不当通过）"
    exit 1
}
$app = Start-Process $Exe -PassThru
Start-Sleep -Seconds 12

$triggered = $false
for ($i = 0; $i -lt 4 -and -not $triggered; $i++) {
    $before = (Get-ChildItem $logDir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    $beforeCount = 0
    if ($before) { $beforeCount = (Get-Content $before.FullName | Select-String "窗口初始化成功").Count }
    [W7]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait("^+s")
    Start-Sleep -Seconds 4
    $after = (Get-ChildItem $logDir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    if ($after) {
        $afterCount = (Get-Content $after.FullName | Select-String "窗口初始化成功").Count
        if ($afterCount -gt $beforeCount) { $triggered = $true; Write-Host "✅ 截图触发 (尝试 $($i+1))" }
    }
}
if (-not $triggered) { Write-Host "❌ 截图未触发"; Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; exit 1 }

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 10 -and $hwnd -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $hwnd = [W7]::FindClass("TTC_HC") }
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "❌ 未找到 TTC_HC"; Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host "✅ HDR 窗口找到"

$rect = New-Object W7+RECT
[W7]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
function Grab($path) {
    $bmp = New-Object System.Drawing.Bitmap(($rect.r - $rect.l), ($rect.b - $rect.t))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [W7]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($path)
    return $bmp
}

# 1. 框选
[W7]::Drag(100, 100, 900, 700)
Start-Sleep -Milliseconds 500
$b1 = Grab("c:\PLAN\TrueToneCap\scripts\step1_selected.png")
$c = $b1.GetPixel(525, 300); Write-Host ("框选后 (525,300): R={0} G={1} B={2}" -f $c.R, $c.G, $c.B)
$b1.Dispose()

# 2. 点击标注按钮
$btnW = 90; $btnH = 32; $gap = 4; $padX = 6; $padY = 5
$totalW = 6*$btnW + 5*$gap + 2*$padX
$tx = 100 + 400 - [int]($totalW/2); $ty = 700 + 10
if ($ty + 42 + 10 -gt 1440) { $ty = 100 - 42 - 12 }
[W7]::Click(($tx + $padX + 1*($btnW + $gap)), ($ty + $padY))
Start-Sleep -Milliseconds 500
$b2 = Grab("c:\PLAN\TrueToneCap\scripts\step2_annomode.png")
$c = $b2.GetPixel(525, 300); Write-Host ("标注模式 (525,300): R={0} G={1} B={2}" -f $c.R, $c.G, $c.B)
$b2.Dispose()

# 3. 绘制矩形标注
[W7]::Drag(200, 200, 500, 400)
Start-Sleep -Milliseconds 800
$b3 = Grab("c:\PLAN\TrueToneCap\scripts\step3_rect.png")
$c1 = $b3.GetPixel(300, 300); $c2 = $b3.GetPixel(525, 300); $c3 = $b3.GetPixel(450, 450); $c4 = $b3.GetPixel(75, 75)
Write-Host ("矩形角(300,300): R={0} G={1} B={2}" -f $c1.R, $c1.G, $c1.B)
Write-Host ("矩形边(525,300): R={0} G={1} B={2}" -f $c2.R, $c2.G, $c2.B)
Write-Host ("矩形内(450,450): R={0} G={1} B={2}" -f $c3.R, $c3.G, $c3.B)
Write-Host ("选区外(75,75):   R={0} G={1} B={2}" -f $c4.R, $c4.G, $c4.B)
$b3.Dispose()
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
