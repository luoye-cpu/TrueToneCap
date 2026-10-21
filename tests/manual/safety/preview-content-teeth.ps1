# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 给"平白一片"这把尺子配正反控制样本（2026-09-30，用户："你试着通过其他办法分析是否正确"）
# 只做两件事，都不碰屏幕：
#   ① 用共用库重算已存下的两臂截图，并把结果与单元日志里那两行对上 ⇒ 证明"内联那份"和"库这一份"等价，
#      后面的对照不是自源自证；
#   ② 造三张合成图（纯白 / 纯黑 / 中灰），走同一把尺子 ⇒ 三条阈值必须各自【能红】。
#      没有这一步，"434 种颜色、白占 14%"这种读数只是看着像对。
$ErrorActionPreference = 'Continue'
. 'C:\PLAN\TrueToneCap\tests\manual\safety\preview-stats-lib.ps1'
$d = 'C:\PLAN\TrueToneCap\tests\output\preview-content'
$log = 'C:\PLAN\TrueToneCap\tests\output\preview-content-cell.log'
$out = 'C:\PLAN\TrueToneCap\tests\output\preview-content-teeth.log'
Add-Type -AssemblyName System.Drawing
[System.IO.File]::WriteAllText($out, '', (New-Object System.Text.UTF8Encoding($false)))
function Say([string[]]$ls) { [IO.File]::AppendAllText($out, (($ls -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false))) }
$rc = 0

Say @("=== 尺子的正反控制 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

# ── ① 重算两臂，并与单元日志里的读数逐值对齐 ──
$logText = @(Get-Content -LiteralPath $log -Encoding UTF8)
foreach ($arm in @('A-ForceSdr', 'B-HdrControl')) {
    $f = Join-Path $d "$arm.png"
    if (-not (Test-Path -LiteralPath $f)) { Say @("★ 缺 $arm.png ⇒ 这一臂无从重算"); $rc = 1; continue }
    $st = Get-BitmapStats -File $f
    $v = Get-StatsVerdict -st $st
    $line = ($logText | Where-Object { $_ -match [regex]::Escape("sha256=") } | Select-Object -First 1)
    Say @("$arm 重算：$($st.W)x$($st.H) 不同颜色 $($st.Kinds) 种 纯白 $($st.WhitePct)% 全黑 $($st.BlackPct)% 均值 $($st.Mean) ⇒ $v")
}
# 与日志的对照要逐值做，不是"看上去一样"
$logPairs = @()
foreach ($m in ([regex]::Matches(($logText -join "`n"), '不同颜色 (\d+) 种  纯白占 ([\d\.]+)%  全黑占 ([\d\.]+)%')) ) {
    $logPairs += , @([int]$m.Groups[1].Value, [double]$m.Groups[2].Value, [double]$m.Groups[3].Value)
}
$recomputed = @()
foreach ($arm in @('A-ForceSdr', 'B-HdrControl')) {
    $f = Join-Path $d "$arm.png"
    if (Test-Path -LiteralPath $f) { $s = Get-BitmapStats -File $f; $recomputed += , @($s.Kinds, $s.WhitePct, $s.BlackPct) }
}
Say @("日志里的读数对 = $($logPairs.Count)，重算的读数对 = $($recomputed.Count)")
$agree = 0
for ($i = 0; $i -lt [Math]::Min($logPairs.Count, $recomputed.Count); $i++) {
    if ($logPairs[$i][0] -eq $recomputed[$i][0] -and [math]::Abs($logPairs[$i][1] - $recomputed[$i][1]) -lt 0.01) { $agree++ }
}
if ($agree -ne $recomputed.Count -or $recomputed.Count -lt 2) {
    Say @("★ 两把尺子不一致（一致 $agree/$($recomputed.Count)）⇒ 内联实现与库实现有分叉，本格的对照作废"); $rc = 1
} else {
    Say @("✅ 库与单元内联实现对全部 $($recomputed.Count) 组逐值一致 ⇒ 下面的对照用的是同一把尺")
}

# ── ② 三张合成图：阈值必须各自能红 ──
function Make-Flat([string]$name, [System.Drawing.Color]$c) {
    $p = Join-Path $d $name
    $b = New-Object System.Drawing.Bitmap(3840, 2160)
    $g = [System.Drawing.Graphics]::FromImage($b)
    try { $g.Clear($c) } finally { $g.Dispose() }
    $b.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    return $p
}
$cases = @(
    @{ Name = '_ctl_white.png'; Color = [System.Drawing.Color]::White; Want = '★ 复现"平白一片"' },
    @{ Name = '_ctl_black.png';  Color = [System.Drawing.Color]::Black; Want = '仪器未判：抓回来近全黑' },
    @{ Name = '_ctl_gray128.png'; Color = [System.Drawing.Color]::FromArgb(255, 128, 128, 128); Want = '★ 无结构（颜色种数过少）' }
)
Say @('--- 合成对照（同尺寸 3840x2160，走同一条判读） ---')
foreach ($c in $cases) {
    $p = Make-Flat $c.Name $c.Color
    $st = Get-BitmapStats -File $p
    $v = Get-StatsVerdict -st $st
    $hit = ($v -eq $c.Want)
    if (-not $hit) { $rc = 1 }
    Say @(("  {0,-16} 颜色 {1} 种 白 {2}% 黑 {3}% ⇒ 判读「{4}」 期望「{5}」 {6}" -f `
        $c.Name, $st.Kinds, $st.WhitePct, $st.BlackPct, $v, $c.Want, $(if ($hit) { '✅ 抓到' } else { '★ 漏判：这条阈值没有牙' })))
}

Say @("RC=$rc")
Write-Output "RC=$rc（$rc 意味着至少一条阈值抓不到它该抓的形状，或两把尺子不一致）"
exit $rc
