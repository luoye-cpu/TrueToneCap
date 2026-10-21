# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 覆盖层【标注子工具条】那 9 枚图标的目视格。
#
# 为什么还要单独一格：主工具条 6 枚已在 overlay-toolbar2.png 里目视确认（保存/标注/复制/识字/翻译/取消
# 形状与语义都对）。剩下 9 枚（矩形/椭圆/画笔/文字/马赛克/撤销/重做/…）藏在点「标注」之后才出现的
# AnnotationToolbar 里。上一轮试过用离线渲染普查全部 32 条轮廓，但那台仪器自检就没过
# （adjudicate-E8B7.png 整张空白），所以它的输出两条方向都不采信 —— 目视仍是唯一可靠读数。
#
# 不用猜坐标点：UIA 直接取「标注」按钮的 BoundingRectangle 再点它的中心，
# 点错按钮的代价（误保存/误复制剪贴板）不由猜测承担。
# 纪律：只起/只杀本次 PID；诊断开关用后即撤；收尾把用户的实例按原样起回。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int[]]$DragFrom = @(700, 300),
    [int[]]$DragTo = @(1900, 820),
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\overlay-annotate.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\overlay-annotate.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 覆盖层标注子工具条 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$dir = Split-Path -Parent $Exe
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class AN
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(200);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string RectOf(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + " " + (r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static IntPtr[] OfPid(uint pid)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
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
                bool ok = PrintWindow(hwnd, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) return false;
                bmp.Save(path, ImageFormat.Png);
                return true;
            }
        }
    }
}
'@
if (-not ('AN' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    Say @("起手 $($pre.Count) 个实例 ⇒ 先停（finally 起回）")
    foreach ($p in $pre) { try { Stop-Process -Id $p.Id -Force } catch { } }
    Start-Sleep -Seconds 2
}
$proc = $null
try {
    $m0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue).Count
    $env:TTC_FORCE_LEGACY_OVERLAY = '1'
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $hk = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 |
        Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+S)\b' } | Select-Object -First 1)
    $chord = if ($hk) { ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($hk[0]).Groups[1].Value } else { 'Ctrl+Shift+S' }
    if ($chord -eq 'Ctrl+Shift+S') { [AN]::Chord(0x11, 0x10, 0x53) } else { [AN]::Chord(0x11, 0x12, 0x53) }
    $ready = $false
    for ($t = 0; $t -lt 40; $t++) {
        if (@(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 |
              Where-Object { $_ -match '覆盖层就绪' }).Count) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { Say @('★ 等 20 s 未见「覆盖层就绪」⇒ 未判'); return }
    Say @('覆盖层就绪')
    Start-Sleep -Milliseconds 800
    [AN]::SetCursorPos($DragFrom[0], $DragFrom[1]); Start-Sleep -Milliseconds 250
    [AN]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [AN]::SetCursorPos($DragTo[0], $DragTo[1]); Start-Sleep -Milliseconds 300
    [AN]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 2500
    Say @("已拖框 $($DragFrom -join ',')→$($DragTo -join ',')")

    # UIA 找「标注」按钮的矩形 —— 点它，不猜坐标
    $auto = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)
    $kids = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    $root = $null; $bestN = -1
    foreach ($k in $kids) {
        $n = try { $k.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { -1 }
        $r = $k.Current.BoundingRectangle
        Say @("  UIA 窗 name=「$($k.Current.Name)」 rect=$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height) 后代=$n")
        if ($n -gt $bestN) { $bestN = $n; $root = $k }
    }
    # 上一版按 ControlType=Button 找 ⇒ 找到的是【主窗】的按钮（Minimize/区域截图/保存设置），
    # 覆盖层工具条是 Border + Tapped 处理，不是 Button 控件，按类型永远找不到。
    # 改成：先按 Name 锁定 SelectionOverlay 那扇窗，再在它的全部后代里按名字找，不限控件类型。
    $ov = @($kids | Where-Object { $_.Current.Name -eq 'SelectionOverlay' })
    Say @("SelectionOverlay 窗数 = $($ov.Count)")
    if ($ov.Count -ne 1) { Say @('★ 不是恰好一扇 SelectionOverlay ⇒ 不猜，未判'); return }
    $all = @($ov[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    Say @("覆盖层后代元素 $($all.Count) 个，带名字的：")
    $named = @($all | Where-Object { $_.Current.Name })
    foreach ($e in $named) {
        $r = $e.Current.BoundingRectangle
        # 内插串里再套 -replace 会把引号配平搞坏（PS 解析器直接报 11 处错）。先算成变量。
        $ct = ($e.Current.ControlType.ProgrammaticName -split '\.')[-1]
        $nm = $e.Current.Name
        Say @("  <$ct> 「$nm」 $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)")
    }
    $btns = @($named)
    foreach ($b in $btns) {
        $r = $b.Current.BoundingRectangle
        Say @("  「$($b.Current.Name)」 $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)")
    }
    $ann = @($all | Where-Object { $_.Current.Name -match '标注' })
    if ($ann.Count -ne 1) { Say @("★ 名字含「标注」的按钮 $($ann.Count) 个（要求 1）⇒ 不点，未判"); return }
    $ar = $ann[0].Current.BoundingRectangle
    $cx = [int]($ar.X + $ar.Width / 2); $cy = [int]($ar.Y + $ar.Height / 2)
    Say @("点「标注」中心 ($cx,$cy)")
    [AN]::Click($cx, $cy)
    Start-Sleep -Milliseconds 2000

    $wins = @([AN]::OfPid([uint32]$proc.Id)); $big = [IntPtr]::Zero; $bestW = 0
    foreach ($h in $wins) { $r = [AN]::RectOf($h); if ($r -match '(\d+)x(\d+)$') { $w = [int]$matches[1]; if ($w -gt $bestW) { $bestW = $w; $big = $h } } }
    if ($big -eq [IntPtr]::Zero -or $bestW -lt 2000) {
        Say @("最大窗 $($bestW)px ⇒ 改用整屏取图")
        $b2 = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($b2.Width, $b2.Height)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try { $g.CopyFromScreen($b2.Location, [System.Drawing.Point]::Empty, $b2.Size); $bmp.Save($Png, [System.Drawing.Imaging.ImageFormat]::Png) }
            finally { $g.Dispose() }
        } finally { $bmp.Dispose() }
        Say @("整屏取图已存 ⇒ $Png")
    } else {
        $ok = [AN]::Save($big, $Png)
        Say @("取图 $ok（$($bestW)px）⇒ $Png")
    }
    Say @('要看的是第二排：矩形/椭圆/画笔/文字/马赛克/撤销/重做 —— 文字应是 AA、马赛克应是四方格')
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    Remove-Item Env:TTC_FORCE_LEGACY_OVERLAY -ErrorAction SilentlyContinue
    try { [AN]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 600
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    Start-Sleep -Seconds 1
    $p2 = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 10; $p2.Refresh()
    Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
}
exit 0
