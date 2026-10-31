# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 正控（2026-10-31）：w1 那格量到"本会话『设置已保存』0 条 + 键还在"，
# 这两个读数同样也能被"持久化整个断了"造出来 —— 而 _formatComboRebuilding 闩锁恰恰是
# 一个"忘了放就会造成这种形态"的东西。所以这一格专门走真实用户那一条路：
# 用 UIA 真的点一次下拉（AVIF），要求盘上键跟着变成 3，并且日志里出现『设置已保存』。
# 键没变 / 保存 0 条 ⇒ 闩锁没放或写侧断了，判红。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$Pick = 3,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-31\positive-control.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

$bytes0 = [IO.File]::ReadAllBytes($set)
Say @("=== 正控 · 真实点选第 $Pick 项 · $(Get-Date -Format 'HH:mm:ss') ===")
Say @("轮前盘上 = $([regex]::Match([IO.File]::ReadAllText($set), '"FormatIndex":\d+').Value)（轮前 $($bytes0.Length) B）")
$logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count

$proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
Say @("PID=$($proc.Id)，等 12 s")
Start-Sleep -Seconds 12
& (Join-Path $PSScriptRoot 'w1-probe-uia.ps1') -Exe $Exe -AttachPid $proc.Id -SettleSec 3 -PickIndex $Pick `
    -Log (Join-Path (Split-Path -Parent $Log) 'positive-control.probe.log')
Start-Sleep -Seconds 3

$ma = [regex]::Match([IO.File]::ReadAllText($set), '"FormatIndex":\d+')
$slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
$saves = @($slice | Where-Object { $_ -match '设置已保存' })
Say @("点选后盘上 = $(if ($ma.Value) { $ma.Value } else { '★ 键不存在' })；本会话『设置已保存』 $($saves.Count) 条")
$want = '"FormatIndex":' + $Pick
$verdict = if ($ma.Value -eq $want -and $saves.Count -ge 1) { "写侧活着：真实点选把键改到 $Pick 并落了盘" }
           elseif ($ma.Value -ne $want) { "★ 键仍是 $($ma.Value) ⇒ 真实用户选择也没落盘 = 闩锁没放/写侧断了" }
           else { "★ 键对了但『设置已保存』0 条 ⇒ 落盘路径可疑，核对是否只改了内存" }
Say @("判词：$verdict")

try { Stop-Process -Id $proc.Id -Force } catch { }
[IO.File]::WriteAllBytes($set, $bytes0)
$shaNow = (New-Object System.Security.Cryptography.SHA256Managed).ComputeHash([IO.File]::ReadAllBytes($set))
$want2 = (New-Object System.Security.Cryptography.SHA256Managed).ComputeHash($bytes0)
$same = $true; for ($i = 0; $i -lt 32; $i++) { if ($shaNow[$i] -ne $want2[$i]) { $same = $false } }
Say @("设置还原：写回轮前 $($bytes0.Length) B ⇒ sha $(if ($same) { '逐字相同 ✓' } else { '★ 不同，立即核对' })")
Write-Output "判词：$verdict"
