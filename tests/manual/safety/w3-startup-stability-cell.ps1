# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 启动稳定性：连起 N 次，崩溃与"没崩"分开计数
#
# 起因：07:18 那一趟 W1b 里产品起来 ~15 s 就没了，WER 归档给出
#   故障模块 nvwgf2umx.dll（NVIDIA 用户态驱动） 异常代码 c00000fd（STACK_OVERFLOW）
# 一次不能定因果（本仓规矩：n<5 的偶发不写成结论），所以这里只做一件事：
# 反复"起进程 → 等 → 数它还活不活"，并把 WER 归档目录里【本轮新增】的 TrueToneCap 条目当第二把尺。
# 两把尺都对上才算量过；只有进程退出没有归档 ⇒ 可能是我自己杀的，要写清楚。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$Trials = 6,
    [int]$AliveSec = 18,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\startup-stability.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 启动稳定性 $Trials 次 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$wer = 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive'
function WerCount { @(Get-ChildItem -LiteralPath $wer -Directory -Filter 'AppCrash_TrueToneCap*' -ErrorAction SilentlyContinue).Count }
$wer0 = WerCount
Say @("轮前 WER 里 AppCrash_TrueToneCap* 归档 = $wer0 个")
$dir = Split-Path -Parent $Exe
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

$crash = 0; $ok = 0; $unknown = 0
for ($i = 1; $i -le $Trials; $i++) {
    $m0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue).Count
    $p = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Start-Sleep -Seconds $AliveSec
    $alive = $true
    try { $p.Refresh(); $alive = -not $p.HasExited } catch { $alive = $false }
    $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Skip $m0)
    $last = if ($slice.Count) { ($slice[-1]).Substring(0, [Math]::Min(120, $slice[-1].Length)) } else { '（本段无日志）' }
    $wc = WerCount
    if ($alive) {
        $ok++
        Say @("[$i] 活着 pid=$($p.Id) 日志 $($slice.Count) 行；最后一行：$last")
        try { Stop-Process -Id $p.Id -Force } catch { Say @("  ★ 停止失败（本次起的 PID，不影响判词）") }
    } else {
        $code = ''
        try { $code = 'exit=' + $p.ExitCode } catch { $code = 'exit=读不到' }
        if ($wc -gt $wer0) { $crash++; $wer0 = $wc; Say @("[$i] ★ 进程已退出 $code，WER 归档数升到 $wc ⇒ 记一次崩溃") }
        else { $unknown++; Say @("[$i] 进程已退出 $code，但 WER 无新归档 ⇒ 记『未定』（也可能归档还没落盘）") }
        Say @("     最后一行日志：$last")
    }
    Start-Sleep -Seconds 2
}
Say @("")
Say @("合计：$Trials 次 ⇒ 活着 $ok / 崩溃 $crash / 未定 $unknown")
Say @("轮末 WER 归档 = $wer0 个（轮前 $([int](($wer0 - ($crash)))) 起）")
Write-Output "TRIALS=$Trials ok=$ok crash=$crash unknown=$unknown"
exit 0
