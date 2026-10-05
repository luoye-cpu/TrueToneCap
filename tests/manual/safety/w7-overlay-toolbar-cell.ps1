# 把旧版覆盖层的工具条拍下来：验 15 枚 PathIcon 与其中 5 枚语义修正
#
# 为什么还要单独一格：SelectionOverlay 是【兜底】路径（预览窗建不起来时才走，正常构建到不了），
# 本轮给它换了 15 枚图标、并改了其中 5 枚的语义（识字/椭圆/画笔/文字/马赛克）——
# 那 5 枚只有在这张工具条上才看得见，不拍就等于没验。
# 到达方式用产品自带的诊断开关 TTC_FORCE_LEGACY_OVERLAY=1（§37 加的，报告 §35.5）。
# 纪律：只起/只杀本次 PID；不改设置（OutputPath 用不到，这一格不落盘）；收尾起回用户的实例。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\overlay-toolbar.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\overlay-toolbar.log',
    [int[]]$DragFrom = @(700, 300),
    [int[]]$DragTo = @(1900, 820)
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 旧版覆盖层工具条 · $(Get-Date -Format 'HH:mm:ss') ===")
$dir = Split-Path -Parent $Exe
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class OV
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
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()   { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
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
if (-not ('OV' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    # 单实例 Mutex：起手必须清零。收尾的 finally 会原样起回来，所以这里停掉不是破坏性动作。
    Say @("起手有 $($pre.Count) 个实例 ⇒ 先停（finally 会起回）")
    foreach ($p in $pre) { try { Stop-Process -Id $p.Id -Force } catch { } }
    Start-Sleep -Seconds 2
}

$proc = $null
try {
    $m0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue).Count
    $env:TTC_FORCE_LEGACY_OVERLAY = '1'
    Say @('已设 TTC_FORCE_LEGACY_OVERLAY=1 ⇒ 主动走兜底覆盖层')
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    if ($proc.Id -eq 0) { }
    $hk = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 | Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+S)\b' } | Select-Object -First 1)
    $chord = if ($hk) { ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($hk[0]).Groups[1].Value } else { 'Ctrl+Shift+S' }
    Say @("送 $chord")
    if ($chord -eq 'Ctrl+Shift+S') { [OV]::Chord(0x11, 0x10, 0x53) } else { [OV]::Chord(0x11, 0x12, 0x53) }
    Start-Sleep -Seconds 3
    # 上一版在这里睡固定 3 s 就开拖 ⇒ 覆盖层 23:25:26.4 才「就绪」，而 mouse-down 在 23:25:25.4 就发了，
    # 拖框整个被吞、覆盖层退出，取到的最大窗是主窗体。改成等产品的就绪行，不赌时间。
    $ready = $false
    for ($t = 0; $t -lt 40; $t++) {
        $r = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 |
            Where-Object { $_ -match '覆盖层就绪' })
        if ($r.Count) { $ready = $true; Say @("覆盖层就绪（第 $t 次轮询）：$($r[-1].Substring(0,[Math]::Min(120,$r[-1].Length)))"); break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { Say @('★ 等 20 s 未见「覆盖层就绪」⇒ 未判，不拖框') }
    else {
        Start-Sleep -Milliseconds 800
        $null = [OV]::SetCursorPos($DragFrom[0], $DragFrom[1]); Start-Sleep -Milliseconds 300
        [OV]::LeftDown()
        $mid = @([int](($DragFrom[0] + $DragTo[0]) / 2), [int](($DragFrom[1] + $DragTo[1]) / 2))
        $null = [OV]::SetCursorPos($mid[0], $mid[1]); Start-Sleep -Milliseconds 250
        $null = [OV]::SetCursorPos($DragTo[0], $DragTo[1]); Start-Sleep -Milliseconds 300
        [OV]::LeftUp()
        Say @("已拖框 $($DragFrom -join ',')→$($DragTo -join ',')，等工具条出现 2.5 s")
    }
    Start-Sleep -Milliseconds 2500
    $wins = @([OV]::OfPid([uint32]$proc.Id))
    $big = [IntPtr]::Zero; $bestW = 0
    foreach ($h in $wins) {
        $r = [OV]::RectOf($h)
        if ($r -match '(\d+)x(\d+)$') {
            $w = [int]$matches[1]
            Say @("  窗 hwnd=$h class<$([OV]::ClassOf($h))> $r")
            if ($w -gt $bestW) { $bestW = $w; $big = $h }
        }
    }
    $own = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 |
        Where-Object { $_ -match '兜底|覆盖层|诊断开关|选区完成|回退' })
    foreach ($l in ($own | Select-Object -Last 5)) { Say @("  | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }
    if ($big -eq [IntPtr]::Zero) { Say @('★ 没有窗口 ⇒ 未判') }
    elseif ($bestW -lt 2000) {
        # 覆盖层是全屏置顶窗，但两次实测 EnumWindows+IsWindowVisible 都没把它列进来
        # （一次给到 2560px、下一次只给到 840px 的主窗）⇒ 判据不能建在窗口枚举上。
        # 它既然占满屏幕，直接抓主屏就行，与"谁的窗"无关。
        Say @("最大窗只有 $($bestW)px（覆盖层未被枚举到）⇒ 改用整屏取图，不再挑窗")
        try {
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            # PS 5.1 没有 C# 那种 using(...) 块（上一版把两种语法混了 ⇒ 整脚本解析失败）。
            # 位图与 Graphics 显式 Dispose。
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            try {
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                try {
                    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                    $bmp.Save($Png, [System.Drawing.Imaging.ImageFormat]::Png)
                } finally { $g.Dispose() }
            } finally { $bmp.Dispose() }
            Say @("整屏取图已存（$($b.Width)x$($b.Height)）⇒ $Png")
            Say @('要看的是工具条那排按钮：识字应=A字、椭圆=空心圆、画笔=笔、文字=AA、马赛克=四方格')
        } catch { Say @("★ 整屏取图失败：$($_.Exception.Message)") }
    }
    else {
        $ok = [OV]::Save($big, $Png)
        Say @("取图 $ok（最大窗 $($bestW)px，class<$([OV]::ClassOf($big))>）⇒ $Png")
        Say @('要看的是工具条那排按钮：识字应=A字、椭圆=空心圆、画笔=笔、文字=AA、马赛克=四方格')
    }
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    Remove-Item Env:TTC_FORCE_LEGACY_OVERLAY -ErrorAction SilentlyContinue
    # Esc 用 Chord 会把两个修饰键当按下（Chord 需要两个修饰键位）⇒ 这里直接单键 keybd_event
    try { [OV]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 600
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    Start-Sleep -Seconds 1
    $p2 = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 10; $p2.Refresh()
    Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
}
exit 0
