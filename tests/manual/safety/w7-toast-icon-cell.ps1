# 把降级/失败浮层拍下来：验证 toast 的四枚语义图标切得对
#
# 为什么值得单独一格：toast 的图标原来是 FontIcon（在这个进程里画成豆腐块），本轮换成
# 四枚预置 PathIcon + 按 tone 切可见性。"换了渲染路径"在 MainWindow 上已证明可行，
# 但【切错档】的风险只有真看见才能排掉——比如 Warning 与 Error 搞反，那是新的误导。
# 触发方式用真实路径：本机设置里 OutputPath=D:\TTC 而这台机器没有 D 盘 ⇒ 无感截图必失败
# ⇒ 产品自己弹 Error 那档浮层。不改设置、不注入参数。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$ToastWaitSec = 25,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\toast-error.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\toast-icons.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 浮层图标取证 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class TS
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static string RectOf(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + " " + (r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static IntPtr[] OfPid(uint pid)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h))
            {
                R r; if (GetWindowRect(h, out r)) { int w = r.Rr - r.L, ht = r.B - r.T; if (w > 120 && w < 900 && ht > 60 && ht < 500) list.Add(h); }
            }
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
if (-not ('TS' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$bytes0 = [IO.File]::ReadAllBytes($set); $sha0 = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
Say @("轮前设置 sha256 = $($sha0.Substring(0,16))…（本格【不改设置】：就用 D:\TTC 这个不可写路径把失败浮层逼出来）")
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) { Say @("RC=2 未判：起手就有 $($pre.Count) 个实例在跑"); exit 2 }

$proc = $null
try {
    $m0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue).Count
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $hk = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0 | Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+Q)' } | Select-Object -First 1)
    $chord = if ($hk) { ([regex]'已注册:\s*(Ctrl\S*\+Q)').Match($hk[0]).Groups[1].Value } else { 'Ctrl+Shift+Q' }
    Say @("送 $chord（无感截图）")
    if ($chord -eq 'Ctrl+Shift+Q') { [TS]::Chord(0x11, 0x10, 0x51) } else { [TS]::Chord(0x11, 0x12, 0x51) }

    $dl = (Get-Date).AddSeconds($ToastWaitSec); $toast = [IntPtr]::Zero; $seen = @()
    while ((Get-Date) -lt $dl) {
        $seen = @([TS]::OfPid([uint32]$proc.Id))
        foreach ($h in $seen) {
            $c = [TS]::ClassOf($h)
            if ($c -ne 'WinUIDesktopWin32WindowClass') { continue }
            $r = [TS]::RectOf($h)
            # 主窗是 1260x840，浮层是小块 ⇒ 取"客户区小于 600 宽"的那块
            if ($r -match '(\d+)x(\d+)$') {
                $w = [int]$matches[1]
                if ($w -lt 600) { $toast = $h; break }
            }
        }
        if ($toast -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 300
    }
    $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0)
    $own = @($slice | Where-Object { $_ -match '截图失败|Toast|通知|浮层|编码失败' })
    Say @("产品自述 $($own.Count) 条：")
    foreach ($l in ($own | Select-Object -Last 5)) { Say @("  | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }
    Say @("本进程小窗候选 = $($seen.Count) 个")
    if ($toast -eq [IntPtr]::Zero) { Say @('★ 没抓到浮层窗 ⇒ 未判（浮层可能已自动消失，或本机这次没走浮层通道）') }
    else {
        Say @("浮层窗 hwnd=$toast class<$([TS]::ClassOf($toast))> 外框=$([TS]::RectOf($toast))")
        $ok = [TS]::Save($toast, $Png)
        Say @("取图 $ok ⇒ $Png（要看的是：错误档应当是【圆圈感叹号】，不是对勾/三角/闪电）")
    }
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    $now = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
    if ($now -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $nowB = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
    $markB = if ($nowB -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' }
    Say @("设置还原自检：$($nowB.Substring(0,16))… ⇒ $markB")
    Start-Sleep -Seconds 1
    $p2 = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 10; $p2.Refresh()
    Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
}
exit 0
