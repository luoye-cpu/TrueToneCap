# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 英文模式取证：这个产品的英文界面到底能不能用？
#
# 为什么这一格必须最先跑：本轮截过的所有界面图【全部是中文模式】。而审计里 F6 的修法
# （补 35 个下拉项的英文）完全建立在"英文模式本身是好的"这个假设上 —— 从没验过。
# 如果切到英文后大面积仍是中文、或者干脆渲染坏了，补那 35 项就是白做，
# 真正的缺陷是另一回事。先量这个，再决定要不要写字符串。
#
# 切语言的方式：往 dev 那份设置里注入 "Language":"en"（盘上原本【没有这个键】，默认走 zh）。
# 收尾按【字节】还原并复算 sha256 —— 这条链上出过一次事（保存设置会写注册表自启动），
# 所以本格不点任何按钮、不落任何设置，只读图。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\ui-en.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\ui-en.log'
)
$ErrorActionPreference = 'Continue'
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 英文模式取证 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class EN
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string RectOf(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + " " + (r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static IntPtr MainWindowOf(uint pid)
    {
        IntPtr found = IntPtr.Zero; int best = 0;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h) && ClassOf(h) == "WinUIDesktopWin32WindowClass")
            { R r; GetWindowRect(h, out r); int w = r.Rr - r.L; if (w > best) { best = w; found = h; } }
            return true;
        }, IntPtr.Zero);
        return found;
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
if (-not ('EN' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$bytes0 = [IO.File]::ReadAllBytes($set)
$sha0 = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
Say @("轮前设置 sha256 = $($sha0.Substring(0,16))…（本格按字节还原，收尾必须逐字相同）")

$raw = [IO.File]::ReadAllText($set, $utf8)
if ($raw -match '"Language":') { Say @('★ 盘上已有 Language 键 ⇒ 不覆盖用户的选择，本格跳过'); exit 2 }
$patched = $raw.TrimEnd('}') + ',"Language":"en"}'
[IO.File]::WriteAllText($set, $patched, $utf8)
Say @("已注入 `"Language`":`"en`"（注入后读回含 en = $(([IO.File]::ReadAllText($set, $utf8)) -match '"Language":"en"')）")

$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    Say @("起手 $($pre.Count) 个实例 ⇒ 先停（收尾起回）")
    foreach ($p in $pre) { try { Stop-Process -Id $p.Id -Force } catch { } }
    Start-Sleep -Seconds 2
}
$proc = $null
try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    if ($proc.HasExited) { Say @("★ 进程退出 exit=$($proc.ExitCode) ⇒ 未判"); exit 2 }
    $mh = [EN]::MainWindowOf([uint32]$proc.Id)
    if ($mh -eq [IntPtr]::Zero) { Say @('★ 没有主窗 ⇒ 未判'); exit 2 }
    Say @("主窗 $mh $([EN]::RectOf($mh))")
    $ok = [EN]::Save($mh, $Png)
    Say @("取图 $ok ⇒ $Png")
    Say @('读法：①导航/标签是否变英文 ②下拉【框里当前值】是否英文 ③【展开后的选项】是否英文')
    Say @('     ②和③是两件事 —— SetComboItemText 改的是 ComboBoxItem.Content，')
    Say @('     未展开时框里显示的是被选中项的 Content，所以两处都该跟着变；只变一处就是机制半通。')
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    [IO.File]::WriteAllBytes($set, $bytes0)
    $nowB = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
    $markB = if ($nowB -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' }
    Say @("设置还原自检：$($nowB.Substring(0,16))… ⇒ $markB")
    Start-Sleep -Seconds 1
    $p2 = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 10; $p2.Refresh()
    Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
}
exit 0
