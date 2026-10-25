# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 判据臂：把 DWriteCore.dll 挪开，看 XAML 引擎是否回落到系统 DirectWrite
#
# 为什么用"挪文件"而不是改配置：本轮在包与 props/targets 里都没找到 DWriteCore 的开关
# （grep Microsoft.WindowsAppSDK 2.4.0 的 .props/.targets 零命中），而引擎到底是不是靠这个 dll 走
# DWriteCore，只有把它拿走才算得出来。挪文件零源码改动、可逆、按 sha256 复算还原。
# 三种读数都有意义：① 图标出来了 ⇒ 根因就在 DWriteCore；② 仍是豆腐块 ⇒ 出局，转去查字体注册/回落；
# ③ 应用起不来 ⇒ 引擎确实依赖它，同样排除了"随手关掉"这条路。
param(
    [string]$Bin = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\dwritecore-arm.log'
)
$ErrorActionPreference = 'Continue'
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== DWriteCore 判据臂 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dll = Join-Path $Bin 'DWriteCore.dll'
$off = Join-Path $Bin 'DWriteCore.dll.off'
if (-not (Test-Path -LiteralPath $dll)) { Say @('RC=2 未判：DWriteCore.dll 本来就不在位'); exit 2 }
$sha0 = (Get-TtcFileHashHex -LiteralPath $dll -Algorithm SHA256)
Say @("轮前 DWriteCore.dll sha256 = $($sha0.Substring(0,16))… $((Get-Item -LiteralPath $dll).Length) B")

# 1) 关掉在跑的实例（托盘常驻，CloseMainWindow 常常退不干净 ⇒ 按授权强杀，收尾起回）
$killed = @()
foreach ($q in @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)) {
    try { [void]$q.CloseMainWindow() } catch { }
    Start-Sleep -Milliseconds 800
    if (-not $q.HasExited) { try { Stop-Process -Id $q.Id -Force; $killed += $q.Id } catch { } }
}
# 关键：Stop-Process 之后进程不保证已经消失。上一版没等就往下跑 ⇒ 内层夹具按自己的起手自检
# 拒跑（"起手就有 1 个实例在跑"），而本脚本把这次【前提不成立】误读成"应用起不来 ⇒ 引擎依赖 DWriteCore"。
# 判据臂必须等前提真的成立，且要把"夹具拒跑"和"产品起不来"分成两种判词。
$dl = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $dl) {
    $still = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
    if (-not $still.Count) { break }
    Start-Sleep -Milliseconds 500
}
$still = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
Say @("已关闭的实例 PID = $(if ($killed.Count) { $killed -join ',' } else { '（本来就没有在跑的）' })；等退出后仍在跑的 = $($still.Count)")
if ($still.Count) { Say @('RC=2 未判：还有实例占着单实例 Mutex ⇒ 本臂不成立，不据此下任何产品判词'); exit 2 }

$rc = 2
try {
    Move-Item -LiteralPath $dll -Destination $off -Force
    Say @("已挪开 ⇒ $off")
    & (Join-Path $PSScriptRoot 'w5-mainwindow-shot-cell.ps1') `
        -Log (Join-Path (Split-Path -Parent $Log) 'shot-nodwritecore.log') `
        -Png (Join-Path (Split-Path -Parent $Log) 'mainwindow-nodwritecore.png')
    $after = @(Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Log) 'shot-nodwritecore.log') -Encoding UTF8)
    foreach ($l in ($after | Where-Object { $_ -match '取图|主窗 hwnd|RC=2|异常|全黑' })) { Say @("  | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }
    $refused = [bool]($after | Where-Object { $_ -match 'RC=2 未判：起手就有' })
    $alive = [bool]($after | Where-Object { $_ -match '主窗 hwnd=' })
    $shot  = [bool]($after | Where-Object { $_ -match '取图 True' })
    if ($refused) { Say @('判词：本臂未跑成（内层夹具按起手自检拒跑）⇒ 对产品什么都不说明') }
    elseif (-not $alive) { Say @('判词＝③ 应用起不来 ⇒ 引擎确实依赖 DWriteCore') }
    elseif (-not $shot) { Say @('判词：取图失败，本臂未判') }
    else { Say @('判词＝图已取 ⇒ 看 mainwindow-nodwritecore.png：图标出来了就是 ①，仍是豆腐块就是 ②') }
    if ($alive -and $shot) { $rc = 0 }
} finally {
    if (Test-Path -LiteralPath $off) { Move-Item -LiteralPath $off -Destination $dll -Force }
    $sha1 = (Get-TtcFileHashHex -LiteralPath $dll -Algorithm SHA256)
    Say @("还原：$($sha1.Substring(0,16))… ⇒ $(if ($sha1 -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' })")
    # 2) 按原样把用户的实例起回去
    $p = Start-Process -FilePath (Join-Path $Bin 'TrueToneCap.exe') -WorkingDirectory $Bin -PassThru
    Start-Sleep -Seconds 12; $p.Refresh()
    Say @("实例已按原样起回：PID=$($p.Id) 活着=$(-not $p.HasExited)")
}
exit $rc
