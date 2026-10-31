# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 一格：盘上写的格式，重启之后还剩什么？（区分"没读回来"与"读回来又被覆写"）
#
# 起因：06:49 与 06:54 两趟各量到"盘上 FormatIndex=2/3，界面显示 PNG、编码也用 PNG"，
# 而那个 3 是【产品自己】在 06:54:27 写进文件的 ⇒ 不是手写 JSON 的水土不服，是它自己的往返断了。
# 这一格把它收到两个独立读数上：先按字节写 FormatIndex=5(TIFF)，起进程、只读界面、停进程，
# 再看①盘上那个键还在不在、②界面选中项的下标等不等于 5 —— 三种世界读数是不同的：
#   键还在 + 界面=5 ⇒ 往返闭合（2026-10-31 修复后的期望）
#   键还在 + 界面≠5 ⇒ 只是"启动时没把设置反映到下拉"（读侧断）
#   键变成 0        ⇒ 启动那两次"设置已保存"把用户选择覆写掉了（读写都断，更严重）
# ⚠ "键还在"这一条单独看不算过：闩锁忘了放会造成同样的读数（写侧整个断）。
#   真实点选那一条路的正控在 w1-format-persist-positive-control.ps1。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$Write = 5,
    [int]$SettleSec = 12,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\format-persist.log'
)
$ErrorActionPreference = 'Continue'
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
Say @("=== 格式往返 · 写 FormatIndex=$Write · $(Get-Date -Format 'HH:mm:ss') ===")

$bytes0 = [IO.File]::ReadAllBytes($set)
$raw = [IO.File]::ReadAllText($set)
$m = [regex]::Matches($raw, '"FormatIndex":\d+')
if ($m.Count -gt 1) { Say @("RC=2 未判：FormatIndex 锚点命中 $($m.Count) 次"); exit 2 }
$shaOrig = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
Say @("轮前盘上 = $(if ($m.Count) { $m[0].Value } else { '★ 键不存在（= 默认 PNG）' })（sha256 前 16 = $($shaOrig.Substring(0,16))）")
if ($m.Count -eq 1) {
    [IO.File]::WriteAllText($set, $raw.Replace($m[0].Value, '"FormatIndex":' + $Write), $utf8)
} else {
    # 键缺失本身就是上一轮缺陷的产物 —— 这里按"插在首个 { 之后"补回去，并断言只有一处 {
    if (([regex]::Matches($raw, '\{')).Count -ne 1) { Say @('RC=2 未判：键缺失且无法安全插入'); exit 2 }
    [IO.File]::WriteAllText($set, $raw.Replace('{', ('{"FormatIndex":' + $Write + ',')), $utf8)
    Say @('  （轮前那个键不存在 ⇒ 本次是【插入】而非替换；这件事本身记进判词）')
}
Say @("按字节写成 FormatIndex=$Write")

$logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count
$proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
Say @("PID=$($proc.Id)，等 $SettleSec s")
Start-Sleep -Seconds $SettleSec

# 只读界面：挂到已经起来的那个实例（产品有单实例守卫，另起一个只会拿到 0 个窗口）
& (Join-Path $PSScriptRoot 'w1-probe-uia.ps1') -Exe $Exe -ReadOnly -AttachPid $proc.Id -SettleSec 3 `
    -Log (Join-Path (Split-Path -Parent $Log) 'format-persist.probe.log')
Say @("界面读数（探针那份日志的 -ReadOnly 行）：")
$pl = Join-Path (Split-Path -Parent $Log) 'format-persist.probe.log'
foreach ($l in @(Get-Content -LiteralPath $pl -Encoding UTF8 | Where-Object { $_ -match 'ReadOnly|没有一项|选中=True' })) { Say @("  | $l") }
# 界面选中项的下标从探针列出的 `[i] 「名」 选中=True` 那一行取 —— 用下标而不是名字跟盘上的
# FormatIndex 对撞，是因为名字要再过一层标签表，而这一格要证的恰恰是"索引被解释成了哪一格"。
# $null（探针崩/窗口不可达）与 -1（一项都没选）是两种不同的没判，分开写。
$selIdx = $null
foreach ($l in @(Get-Content -LiteralPath $pl -Encoding UTF8)) {
    if ($l -match '^\s*\[(\d+)\]\s+「.+」\s+选中=True') { $selIdx = [int]$Matches[1] }
    elseif ($l -match '没有一项被选中') { $selIdx = -1 }
}

# 让它自己退出（走 OnClosed/Save 那条路），再看盘
try { $null = $proc.MainWindowHandle } catch { }
Start-Sleep -Seconds 1
try { [void]$proc.CloseMainWindow() } catch { Say @("★ CloseMainWindow 失败: $($_.Exception.Message)") }
Start-Sleep -Seconds 4
if (-not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force; Say @('窗口关不掉 ⇒ 强杀（本次起的 PID）') } catch { } }

$rawAfter = [IO.File]::ReadAllText($set)
$ma = [regex]::Match($rawAfter, '"FormatIndex":\d+')
Say @("停进程后盘上 = $($ma.Value)")
$slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
$saveLines = @($slice | Where-Object { $_ -match '设置已保存' })
Say @("本会话『设置已保存』 $($saveLines.Count) 条")
$fmtWarn = @($slice | Where-Object { $_ -match '格式|Format|下拉|空白|契约' })
foreach ($l in $fmtWarn) { Say @("  | $($l.Substring(0, [Math]::Min(150, $l.Length)))") }

# 三格判词（2026-10-31 从两格改过来）：轮前这条判据只有"键在不在"一个读数，
# 于是"键还在"同时兼容"界面显示对了"和"界面根本没反映"两种世界 —— 后者正是它要抓的缺陷形态，
# 分不清就等于没判。现在盘上键与界面选中下标分开取，两条都对才叫往返闭合。
$keyKept = ($ma.Value -eq ('"FormatIndex":' + $Write))
$uiTxt = if ($null -eq $selIdx) { '★ 界面那一格没判（探针日志里没有选中项行）' }
         elseif ($selIdx -lt 0) { '★ 界面一项都没选中' }
         elseif ($selIdx -eq $Write) { "界面选中第 $selIdx 项" }
         else { "★ 界面选中第 $selIdx 项 ≠ 盘上 $Write" }
$verdict = if (-not $keyKept) { "盘上键被改成 $($ma.Value) ⇒ 启动那几次『设置已保存』把用户选择覆写了（读写两侧都断）" }
           elseif ($null -eq $selIdx) { "键还在，$uiTxt ⇒ 只证到写侧，读侧待补" }
           elseif ($selIdx -ne $Write) { "键还在但 $uiTxt ⇒ 读侧断（启动没把设置反映到下拉）" }
           else { "往返闭合：盘上键没被覆写（仍 $Write），$uiTxt；本会话『设置已保存』 $($saveLines.Count) 条" }
Say @("判词：$verdict")
# 还原轮前字节，并用轮前那份字节自己复算一把 sha 当尺
[IO.File]::WriteAllBytes($set, $bytes0)
$shaNow = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
$want = [System.BitConverter]::ToString((New-Object System.Security.Cryptography.SHA256Managed).ComputeHash($bytes0)).Replace('-', '')
Say @("设置还原：写回轮前 $($bytes0.Length) B ⇒ sha $shaNow $(if ($shaNow -eq $want) { '与轮前那份字节复算逐字相同 ✓' } else { '★ 与轮前不同，立即核对' })")
Write-Output "判词：$verdict"
exit 0
