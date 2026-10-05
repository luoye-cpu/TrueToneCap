# B0 探针的读数格：同一张图里同时含【Style 形式】与【内联形式】的两枚相同轮廓。
#
# 判据设计：MainWindow line 33（导航项"OCR / 翻译"的图标）已改成 <PathIcon Style="{StaticResource …}"/>，
# line 374（AI 翻译页页眉那枚）仍是内联 Data，两枚 Data 逐字相同（脚本 w8-setter-style-probe.py 里有等值断言）。
# 所以选中"OCR / 翻译"页后：
#   两枚都有形状  ⇒ Setter.Value 能带 mini-language ⇒ B0 可以做成有名字的表；
#   页眉有、导航没有 ⇒ 机制不成立 ⇒ B0 撤，改走"内联 + 清单 + 门禁卡"。
# 不需要跨图比较，对照臂就在同一张图上。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$PickName = 'OCR',
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\style-arm.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\style-arm.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== B0 Style 对照臂 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class SA
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
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
if (-not ('SA' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$bytes0 = [IO.File]::ReadAllBytes($set); $sha0 = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    Say @("起手 $($pre.Count) 个实例 ⇒ 先停（finally 起回）")
    foreach ($p in $pre) { try { Stop-Process -Id $p.Id -Force } catch { } }
    Start-Sleep -Seconds 2
}

$proc = $null
try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    $auto = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)
    # 导航失败也要有读数：Style 臂在【默认页就能看到】（它是左侧导航项自己的图标），
    # 只有内联臂（AI 翻译页页眉）才需要翻页。所以先无条件拍一张默认页。
    $w0 = @([SA]::OfPid([uint32]$proc.Id)); $m0h = [IntPtr]::Zero; $b0 = 0
    foreach ($h in $w0) { $r = [SA]::RectOf($h); if ($r -match '(\d+)x(\d+)$') { $w = [int]$matches[1]; if ($w -gt $b0) { $b0 = $w; $m0h = $h } } }
    if ($m0h -ne [IntPtr]::Zero) {
        $ok0 = [SA]::Save($m0h, ($Png -replace '\.png$', '-default.png'))
        Say @("默认页取图 $ok0 ⇒ $(($Png -replace '\.png$','-default.png'))（Style 臂就在这张里）")
    } else { Say @('★ 默认页没有可见窗 ⇒ 少一张读数') }

    # 上一版直接取 FindFirst(Children) 的第一个 ⇒ 拿到的是哪个窗全看 z 序，实测 ListItem=0。
    # 改成枚举该 PID 的全部顶层窗，按"后代数最多"挑主窗，并把每一扇的归属打出来。
    $kids = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    Say @("该 PID 顶层 UIA 窗 $($kids.Count) 扇：")
    $root = $null; $bestN = -1
    foreach ($k in $kids) {
        $n = try { $k.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { -1 }
        $r = $k.Current.BoundingRectangle
        Say @("  name=「$($k.Current.Name)」 class<$($k.Current.ClassName)> rect=$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height) 后代=$n")
        if ($n -gt $bestN) { $bestN = $n; $root = $k }
    }
    if ($null -eq $root) { Say @('★ UIA 一根窗都没拿到 ⇒ 未判'); return }
    Say @("⇒ 取后代最多的那扇做根（后代 $bestN）")
    $items = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    Say @("ListItem 共 $($items.Count) 个：")
    for ($i = 0; $i -lt $items.Count; $i++) { Say @("  [$i] 「$($items[$i].Current.Name)」") }
    $pick = @($items | Where-Object { $_.Current.Name -match [regex]::Escape($PickName) })
    if ($pick.Count -ne 1) { Say @("★ 名字含「$PickName」的 ListItem 有 $($pick.Count) 个（要求恰好 1）⇒ 不猜，未判"); return }
    $pick[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Say @("已 Select「$($pick[0].Current.Name)」⇒ 该页页眉那枚是【内联 Data】，导航项那枚是【Style】")
    Start-Sleep -Milliseconds 1500

    $wins = @([SA]::OfPid([uint32]$proc.Id))
    $mh = [IntPtr]::Zero; $best = 0
    foreach ($h in $wins) { $r = [SA]::RectOf($h); if ($r -match '(\d+)x(\d+)$') { $w = [int]$matches[1]; Say @("  窗 $h $r"); if ($w -gt $best) { $best = $w; $mh = $h } } }
    if ($mh -eq [IntPtr]::Zero) { Say @('★ 无窗 ⇒ 未判'); return }
    $ok = [SA]::Save($mh, $Png)
    Say @("取图 $ok ⇒ $Png")
    Say @('读法：左侧导航"OCR / 翻译"那枚 = Style 臂；右侧页眉"OCR 文字识别"旁那枚 = 内联臂。两枚应当一模一样。')
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    $now = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
    if ($now -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $mark = if ((Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' }
    Say @("设置还原自检：$($mark)（本格不截图不落盘，仍照例复算）")
    Start-Sleep -Seconds 1
    $p2 = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 10; $p2.Refresh()
    Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
}
exit 0
