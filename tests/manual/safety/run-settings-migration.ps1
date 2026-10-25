# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 2026-09-29 批次 E：跑 App 侧的 --settings-migration（三条设置自检，毫秒级，无窗口）。
# 判据 = 真退出码；读数 = 进程 stdout 里的 NORMALIZE / SETTINGS_MIGRATION / GAINMAP_MIGRATION 行。
param([string]$LogName = 'settings-migration.log')
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$exe  = Join-Path $repo 'src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe'
$set  = Join-Path $repo 'src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.settings.json'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$out  = Join-Path $repo 'tests\manual\safety\settings-migration'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
[System.IO.File]::WriteAllText($log, '', $utf8)

if (-not (Test-Path -LiteralPath $exe)) { Say @("❌ 找不到 exe: $exe"); Write-Output 'RESULT=no-exe'; exit 1 }
$before = Sha $set
Say @("=== --settings-migration · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("exe: $exe")
Say @("轮前盘上设置 sha1: $before")
if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }

# ⚠ 这里必须用 -Wait：Start-Process 带 -RedirectStandardOutput 时，只给 -PassThru 拿到的对象
#   事后读 ExitCode 是【空】（实测 `EXIT=`，判据就此消失）。-Wait 才会把退出码填回对象。
#   超时保护改由外层（Bash 的 timeout）承担：本模式按设计是毫秒级，卡住就是 bug，要看得见。
$p = Start-Process -FilePath $exe -ArgumentList @('--settings-migration', '--out', $out) -PassThru -Wait `
    -WindowStyle Hidden -RedirectStandardOutput (Join-Path $out 'stdout.txt') -RedirectStandardError (Join-Path $out 'stderr.txt')
if ($null -eq $p.ExitCode) {
    Say @('❌ 退出码读不到 ⇒ 本轮不给结论（不许用"stdout 里有 ok"代替退出码判据）')
    Write-Output 'RESULT=no-exitcode'; exit 1
}
Say @("EXIT=$($p.ExitCode)")
foreach ($f in @('stdout.txt', 'stderr.txt')) {
    $t = Join-Path $out $f
    if (Test-Path -LiteralPath $t) {
        Say @("--- $f ---")
        Say (@(Get-Content -LiteralPath $t -Encoding UTF8) | ForEach-Object { "  $_" })
    }
}
$after = Sha $set
Say @("轮后盘上设置 sha1: $after（轮前 $before）⇒ 本轮进程写盘=$($after -ne $before)")
Write-Output ("RESULT=done EXIT={0} SET_DRIFT={1} LOG={2}" -f $p.ExitCode, ($after -ne $before), $log)
exit $p.ExitCode
