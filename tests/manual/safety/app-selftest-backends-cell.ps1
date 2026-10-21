# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# App 侧进程内自测：把"后端可执行文件缺失"与"真失败"分账的【前后对照】取证片段
#
# 为什么单独写一份、而不直接用 app-selftest-cost-cell.ps1：
#   那份每次运行会把 tests\output\app-selftest-cost-cell.log 整段重写（它第 24 行
#   `[IO.File]::WriteAllText($log, '', $utf8)`），而这一格要的是"改之前 failed=2/退 1、
#   改之后退 0/missingBackend=N"两枚读数并排放在一起 —— 覆盖掉 2026-09-30 那轮的唯一
#   before 证据，等于把自己判据的对照基线删了。这里落到自己的日志，不动别人的。
#
# 判据 = 退出码（0 通过）+ progress.txt 里逐格式那一行的 status 词表（ok/fail/skip/missingBackend）。
# 纪律沿用 cost-cell 那份：--out 一律指进仓库（%TEMP% 有清理器，历史证据丢过），
# 设置文件按原字节还原并复算 sha256，超时记【未判】而不是绿。
param(
    [string]$Exe = '',
    [string[]]$Modes = @('--settings-migration', '--selftest'),
    [int]$PerModeTimeoutSec = 150,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\app-selftest-backends-cell.log'
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }

# ── exe 定位：与 scripts\verify.ps1 同一套"枚举 + 按 Configuration 过滤 + 取最新"，不硬编码 TFM ──
if (-not $Exe) {
    $Exe = (Get-ChildItem (Join-Path $repo 'src\TrueToneCap.App\bin') -Filter 'TrueToneCap.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains 'Release' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not (Test-Path -LiteralPath $Exe)) { Say @("RC=1 没有 $Exe"); Write-Output "RC=1 没有 $Exe"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$outRoot = Join-Path $repo 'tests\output\app-selftest-backends'
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== App 侧后端缺失分账 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("exe = $Exe")

$sha0 = Sha $set
$bytes0 = if (Test-Path -LiteralPath $set) { [IO.File]::ReadAllBytes($set) } else { $null }
Say @("轮前设置 sha256 = $sha0")

$report = @()
foreach ($mode in $Modes) {
    $outDir = Join-Path $outRoot ($mode.TrimStart('-'))
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $prog = Join-Path $outDir 'progress.txt'
    # 每次先清 progress.txt：SelfTest 是 AppendAllText，不清就分不清哪几行是本轮的
    if (Test-Path -LiteralPath $prog) { Remove-Item -LiteralPath $prog -Force }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = $null
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList @($mode, '--out', $outDir) -PassThru -WorkingDirectory $dir
        $gone = $p.WaitForExit($PerModeTimeoutSec * 1000)
        $sw.Stop()
        if (-not $gone) {
            Say @("$mode ⇒ ❌ 超时 ${PerModeTimeoutSec}s 未退出 ⇒ 该格记【未判】，不记绿")
            $report += [pscustomobject]@{ Mode = $mode; Exit = '未判'; Ms = $sw.ElapsedMilliseconds }
            try { $p.Kill() } catch { }
            continue
        }
        $code = $p.ExitCode
    } catch {
        $sw.Stop()
        Say @("$mode ⇒ ❌ 起进程/等待失败：$($_.Exception.Message)")
        $report += [pscustomobject]@{ Mode = $mode; Exit = '异常'; Ms = $sw.ElapsedMilliseconds }
        continue
    }
    $shaMid = Sha $set
    Say @("$mode ⇒ 退出码=$code 墙钟=$($sw.ElapsedMilliseconds)ms 超时上限=${PerModeTimeoutSec}s")
    Say @("   --out = $outDir")
    Say @("   设置 sha256：$sha0 → $shaMid $(if ($shaMid -ne $sha0) { '★ 该模式重写了设置文件' } else { '（未动）' })")
    if (Test-Path -LiteralPath $prog) {
        foreach ($l in (Get-Content -LiteralPath $prog -Encoding UTF8)) { Say @("   | $l") }
    } else {
        Say @("   | （没有 progress.txt ⇒ 这一轮连一步都没落盘，读数不可信）")
    }
    $report += [pscustomobject]@{ Mode = $mode; Exit = "$code"; Ms = $sw.ElapsedMilliseconds }
}

if ($null -ne $bytes0) {
    $shaEnd = Sha $set
    if ($shaEnd -ne $sha0) {
        [IO.File]::WriteAllBytes($set, $bytes0)
        $shaEnd = Sha $set
    }
    Say @("还原自检：原件 sha256=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：立即人工核对' })")
} else {
    if (Test-Path -LiteralPath $set) {
        Remove-Item -LiteralPath $set -Force
        Say @("轮前没有设置文件，本轮生成了 ⇒ 已删除，恢复'本来就没有'的状态")
    } else {
        Say @("轮前没有设置文件，本轮也没生成 ⇒ 无东西可还原")
    }
}
Say @("--- 汇总 ---")
foreach ($r in $report) { Say @("  {0,-22} 退出码={1,-6} 墙钟={2}ms" -f $r.Mode, $r.Exit, $r.Ms) }
Write-Output "RC=0 MODES=$($report.Count) 详情见 $Log"
Write-Output (Get-Content -LiteralPath $Log -Raw -Encoding UTF8)
