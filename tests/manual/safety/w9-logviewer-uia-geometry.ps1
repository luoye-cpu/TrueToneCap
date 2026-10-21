# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 用 UIA 量 LogViewer 工具条每枚按钮的矩形 —— 判"重叠"和"出界"要数字，不要目视。
#
# 为什么不用截图当判据：取图进程（PowerShell）没声明 DPI 感知，GetClientRect/GetWindowRect
# 拿到的是被 Windows 虚拟化过的坐标（实测 Resize(1280,800) 读成 853x533，正好差 1.5 倍），
# 于是"顶到右缘"这种读数分不清是产品布局问题还是夹具假象。
# UIA 的 BoundingRectangle 走的是另一条路（DIP、且由被查询进程自己报），不受这个虚拟化影响。
#
# 修复前的预期形状：6 个元素全落在同一列 ⇒ 它们的 X 起点应当彼此相同（叠在一起）。
# 修复后的预期形状：X 严格递增、互不重叠、且都在窗口内。
param([string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\logviewer-uia.log')
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Out, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Out, '', $utf8)
Say @("=== LogViewer 工具条 UIA 几何 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.LogViewer\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TTCLogViewer.exe'
$proc = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path -Parent $exe)
try {
    Start-Sleep -Seconds 8
    $auto = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)
    $kids = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    $root = $null; $best = -1
    foreach ($k in $kids) {
        $n = try { $k.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { -1 }
        if ($n -gt $best) { $best = $n; $root = $k }
    }
    if ($null -eq $root) { Say @('★ 没拿到窗 ⇒ 未判'); exit 2 }
    $wr = $root.Current.BoundingRectangle
    Say @("窗口 name=「$($root.Current.Name)」 UIA 矩形 = $([int]$wr.X),$([int]$wr.Y) $([int]$wr.Width)x$([int]$wr.Height)（DIP）")

    $names = @('RefreshBtn', 'OpenDirBtn', 'LevelFilterCbo', 'SearchTxt', 'ClearOldBtn', 'ClearAllBtn')
    $rows = @()
    foreach ($nm in $names) {
        $el = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $nm))))
        if ($el.Count -eq 0) { Say @("  $nm ⇒ ★ UIA 树里找不到（AutomationId 没透出来？）"); continue }
        $r = $el[0].Current.BoundingRectangle
        $rows += , @($nm, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
        Say @(("  {0,-15} X={1,5} Y={2} W={3,4} H={4}  右缘={5,5}" -f $nm, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, ([int]$r.X + [int]$r.Width)))
    }
    # 判据一：两两不重叠（X 区间不相交）
    $ov = @()
    for ($i = 0; $i -lt $rows.Count; $i++) {
        for ($j = $i + 1; $j -lt $rows.Count; $j++) {
            $a = $rows[$i]; $b = $rows[$j]
            if ($a[1] -lt ($b[1] + $b[3]) -and $b[1] -lt ($a[1] + $a[3])) { $ov += "$($a[0])×$($b[0])" }
        }
    }
    # 判据二：都在窗口内
    $over = @($rows | Where-Object { $_.Count -ge 5 -and (($_[1] + $_[4]) -gt ([int]($wr.X + $wr.Width))) })
    Say @("")
    Say @("重叠对 = $($ov.Count) 个$(if ($ov.Count) { '：' + ($ov -join ', ') } else { '（无）' })")
    Say @("右缘越界 = $($over.Count) 个$(if ($over.Count) { '：' + (($over | ForEach-Object { $_[0] }) -join ', ') } else { '（无）' })")
    $xseq = @($rows | ForEach-Object { $_[1] })
    $inc = $true; for ($i = 1; $i -lt $xseq.Count; $i++) { if ($xseq[$i] -le $xseq[$i - 1]) { $inc = $false } }
    Say @("X 严格递增 = $inc")
    Say @("⇒ 判词：重叠 0 且 越界 0 且 递增 ⇒ 工具条布局成立；任一不满足 ⇒ 仍坏")
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    Say @('本格拉起的工具进程已关闭（主程序实例未触碰）')
}
exit 0
