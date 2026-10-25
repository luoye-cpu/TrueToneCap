# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 任务 #13 的前置量：App 侧"进程内判据"到底多贵、会不会写盘（报告 §18 / §26.1 那条老缺口的第一笔数）
# 三个模式各跑一次：--settings-migration / --display-audit / --selftest
# 判据 = 退出码（0 通过）；成本 = 墙钟毫秒；写盘 = 设置文件 sha1 前后对照 + --out 目录新增件清单。
# 收尾：设置文件按原字节还原并复算 sha1；只杀本次起的 PID。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$PerModeTimeoutSec = 150
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo 'tests\output\app-selftest-cost-cell.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($log, (($ls -join "`r`n") + "`r`n"), $utf8) }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA1) } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$bak = Join-Path $dir 'TrueToneCap.settings.json.ttc-costbak'
if (-not (Test-Path -LiteralPath $Exe)) { Fail "没有 $Exe" }
if (Test-Path -LiteralPath $bak) { Fail "遗留备份件 $bak ⇒ 上一轮没收尾干净，先人工看" }
$outRoot = Join-Path $repo 'tests\output\app-selftest'
foreach ($d in @($outRoot)) { if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null } }
[IO.File]::WriteAllText($log, '', $utf8)
Say @("=== App 侧进程内判据 · 耗时与写盘面 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("exe = $Exe")

$sha0 = Sha $set
$bytes0 = if (Test-Path -LiteralPath $set) { [IO.File]::ReadAllBytes($set) } else { $null }
Say @("轮前设置 sha1 = $sha0（$($dir)）")

$report = @()
foreach ($mode in @('--settings-migration', '--display-audit', '--selftest')) {
    $outDir = Join-Path $outRoot ($mode.TrimStart('-'))
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $before = @{}
    if (Test-Path -LiteralPath $outDir) {
        Get-ChildItem -LiteralPath $outDir -File | ForEach-Object { $before[$_.Name] = $_.Length }
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = $null
    try {
        # 用 -PassThru 拿到进程对象，自己 WaitForExit(ms)：Start-Process -Wait 没有超时口径，
        # 一旦某个模式挂住，整轮拿不到读数也报不出红。
        $p = Start-Process -FilePath $Exe -ArgumentList @($mode, '--out', $outDir) -PassThru -WorkingDirectory $dir
        $gone = $p.WaitForExit($PerModeTimeoutSec * 1000)
        $sw.Stop()
        if (-not $gone) {
            Say @("$mode ⇒ ❌ 超时 ${PerModeTimeoutSec}s 未退出 ⇒ 该格记【未判】，不记绿")
            $report += [pscustomobject]@{ Mode = $mode; Exit = '未判'; Ms = $sw.ElapsedMilliseconds }
            try { $p.Kill() } catch { }
            continue
        }
        # GUI 子系统的退出码要等进程真正终止后读才可靠；再取一次
        $code = $p.ExitCode
    } catch {
        $sw.Stop()
        Say @("$mode ⇒ ❌ 起进程/等待失败：$($_.Exception.Message)")
        $report += [pscustomobject]@{ Mode = $mode; Exit = '异常'; Ms = $sw.ElapsedMilliseconds }
        continue
    }
    $after = @{}
    Get-ChildItem -LiteralPath $outDir -File -ErrorAction SilentlyContinue | ForEach-Object { $after[$_.Name] = $_.Length }
    $newFiles = @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
    $grown    = @($after.Keys | Where-Object { $before.ContainsKey($_) -and $after[$_] -ne $before[$_] })
    $shaMid = Sha $set
    Say @("$mode ⇒ 退出码=$code 墙钟=$($sw.ElapsedMilliseconds)ms 超时上限=${PerModeTimeoutSec}s")
    Say @("   --out 目录 $outDir ：新增 $($newFiles.Count) 件 [$($newFiles -join ', ')]；变化 $($grown.Count) 件 [$($grown -join ', ')]")
    Say @("   设置 sha1：$sha0 → $shaMid $(if ($shaMid -ne $sha0) { '★ 该模式会重写设置文件' } else { '（未动）' })")
    $report += [pscustomobject]@{ Mode = $mode; Exit = "$code"; Ms = $sw.ElapsedMilliseconds }
}

# ── 收尾：设置按原字节还原 + 复算 ──
if ($null -ne $bytes0) {
    [IO.File]::WriteAllBytes($set, $bytes0)
    $shaEnd = Sha $set
    Say @("还原自检：原件写回 sha1=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：立即人工核对' })")
} else {
    Say @("轮前没有设置文件 ⇒ 本轮若生成了 $set，属新增，需人工决定留不留")
}
Say @("--- 汇总 ---")
foreach ($r in $report) { Say @("  {0,-22} 退出码={1,-6} 墙钟={2}ms" -f $r.Mode, $r.Exit, $r.Ms) }
Write-Output "RC=0 MODES=$($report.Count) 详情见 $log"
