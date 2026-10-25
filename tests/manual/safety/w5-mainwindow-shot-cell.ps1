# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 把主窗抓下来看一眼：XamlControlsResources 每次都加载失败，界面到底长成什么样
#
# 为什么用 PrintWindow(hwnd, hdc, 2) 而不是 CopyFromScreen：
# 前者能把被压住/未在前台的窗完整取下来，不需要 SetForegroundWindow（这台机器上用户在打字时抢前台既抢不动也很失礼）。
# ⚠ 已知坑：带 WS_EX_NOREDIRECTIONBITMAP 的 GPU 自绘窗 PrintWindow 必然全黑 ⇒ 那张黑图不能读成"界面是黑的"。
#   所以这里同时数窗口的 ExStyle 与像素，两件事分开报。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\mainwindow.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\mainwindow-shot.log',
    # PrintWindow=进程外抓被遮挡窗；Screen=直接抓屏幕上那块矩形。
    #   两种方法对同一扇窗给出【不同】的图标读数 ⇒ 才能分清"用户看到的"与"夹具拍到的"
    [ValidateSet('PrintWindow', 'Screen')] [string]$Method = 'PrintWindow'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 主窗取图 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
# ⚠ -ReferencedAssemblies 不能省：PS5.1 的默认引用集里没有 System.Drawing.Imaging，
#   省了就是 COMPILER_ERRORS ⇒ 之后每个 P/Invoke 都会以"类型没编出来"的形式伪装成"没有数据"
#   （下面那道守卫就是为此而写的，它本轮确实拦下了一次）。
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class GW
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static string Rect(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + " " + (r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static string Client(IntPtr h) { R r; return GetClientRect(h, out r) ? ((r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    public static bool BringToTop(IntPtr h) { ShowWindow(h, 5); return BringWindowToTop(h) && SetForegroundWindow(h); }
    public static string RectOf(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + "," + (r.Rr - r.L) + "," + (r.B - r.T)) : "无"; }
    public static bool GrabScreen(string rectCsv, string path)
    {
        string[] a = rectCsv.Split(',');
        using (var bmp = new Bitmap(int.Parse(a[2]), int.Parse(a[3]), PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(int.Parse(a[0]), int.Parse(a[1]), 0, 0, new Size(int.Parse(a[2]), int.Parse(a[3])));
                bmp.Save(path, ImageFormat.Png);
            }
        }
        return true;
    }
    public static long ExStyle(IntPtr h) { return GetWindowLong(h, -20) & 0xFFFFFFFFL; }
    public static bool Save(IntPtr hwnd, string path)
    {
        R rc; if (!GetClientRect(hwnd, out rc)) return false;
        int w = rc.Rr - rc.L, h = rc.B - rc.T;
        if (w <= 0 || h <= 0) return false;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2);      // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
                if (!ok) return false;
                bmp.Save(path, ImageFormat.Png);
                return true;
            }
        }
    }
}
'@
if (-not ('GW' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来（看 Add-Type 的报错，别把编译失败读成"没有窗口"）'); exit 2 }
$auto = [System.Windows.Automation.AutomationElement]

$dir = Split-Path -Parent $Exe
# 起手自检：产品有单实例 Mutex（App.xaml.cs），别人的实例在场时我起的第二个会立刻
# 打一行「已有实例运行」然后退出 ⇒ 那时"0 个顶层窗"是夹具前提不成立，不是产品没界面。
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    Say @("RC=2 未判：起手就有 $($pre.Count) 个 TrueToneCap 实例在跑（$($pre.Id -join ',')）⇒ 单实例 Mutex 会让本次起的进程立刻退出")
    exit 2
}
$proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
try {
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)
    $tops = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $cond))
    $main = $null; $best = -1
    foreach ($w in $tops) {
        $n = -1
        try { $n = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
             [System.Windows.Automation.Condition]::TrueCondition)).Count } catch { }
        $h = [IntPtr]$w.Current.NativeWindowHandle
        Say @("  hwnd=$h class<$($w.Current.ClassName)> 子树=$n 外框=$([GW]::Rect($h)) 客户区=$([GW]::Client($h)) 可见API=$([GW]::IsWindowVisible($h)) ExStyle=0x$([Convert]::ToString([GW]::ExStyle($h), 16))")
        if ($n -gt $best) { $best = $n; $main = $w }
    }
    if (-not $main) { Say @('★ 一个顶层窗都没枚举到 ⇒ 未判'); exit 2 }
    $mh = [IntPtr]$main.Current.NativeWindowHandle
    $null = [GW]::ShowWindow($mh, 9)      # SW_RESTORE：宿主窗可能被外部点击序列最小化，每轮先还原再量
    Start-Sleep -Milliseconds 700
    Say @("主窗 hwnd=$mh 子树=$best 外框=$([GW]::Rect($mh)) 客户区=$([GW]::Client($mh)) ExStyle=0x$([Convert]::ToString([GW]::ExStyle($mh), 16))")
    $noredir = ([GW]::ExStyle($mh) -band 0x00200000L) -ne 0
    Say @("WS_EX_NOREDIRECTIONBITMAP=$noredir $(if ($noredir) { '⇒ PrintWindow 必然全黑，这张图不能当界面读数' })")
    $ok = $true
    if ($Method -eq 'PrintWindow') { $ok = [GW]::Save($mh, $Png) }
    else {
        $null = [GW]::BringToTop($mh)
        Start-Sleep -Milliseconds 900
        $r = [GW]::RectOf($mh)
        Say @("Screen 法抓外框 $r")
        $ok = [GW]::GrabScreen($r, $Png)
    }
    Say @("取图 $ok ⇒ $Png")
    if ($ok) {
        Add-Type -AssemblyName System.Windows.Forms
        $b = New-Object System.Drawing.Bitmap $Png
        $black = 0; $total = $b.Width * $b.Height; $step = 7
        for ($y = 0; $y -lt $b.Height; $y += $step) {
            for ($x = 0; $x -lt $b.Width; $x += $step) {
                $p = $b.GetPixel($x, $y)
                if ($p.R -lt 6 -and $p.G -lt 6 -and $p.B -lt 6) { $black++ }
            }
        }
        $samples = [math]::Ceiling($b.Height / $step) * [math]::Ceiling($b.Width / $step)
        Say @("全黑采样占比 = $([math]::Round(100.0 * $black / $samples, 2))%（$($b.Width)x$($b.Height)，每 $step 像素取一点）")
        $b.Dispose()
    }
} catch {
    Say @("★ 异常：$($_.Exception.Message)")
} finally {
    try { Stop-Process -Id $proc.Id -Force; Say @("已停止本次起的 PID=$($proc.Id)") } catch { }
}
exit 0
