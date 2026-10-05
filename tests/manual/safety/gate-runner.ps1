# 2026-09-29 审查轮 · 门禁包装器。
# 三件事：① 先拍用户设置指纹（测试与产品共用配置 ⇒ 跑测试能改用户机器）；
#        ② 子进程方式起 scripts/verify.ps1，**门禁自己的退出码**当本轮退出码；
#        ③ 收尾比差集并把整份日志落成【单一编码的 UTF-8】。
# 为什么第 ③ 条要专门处理：2026-09-29 第一次跑时 `Set-Content -Encoding UTF8` 写前段、
# `Tee-Object -Append` 写后段（PS5.1 的 Tee 是 Unicode）⇒ 一份文件两种编码 ⇒
# grep 报 "Binary file matches"，负断言就此读不到东西，正是本仓反复踩的"尺子自己失效"形状。
param(
    # 日志名可换 ⇒ 本轮基线那份（round3-baseline-gate.log）是证据件，不能被后一轮的清空动作抹掉
    [string]$LogName = 'round3-gate.log',
    # 传给 scripts/verify.ps1 的参数（默认无参 = 全量门禁；包装器自检时给 '-BuildOnly'）
    [string[]]$VerifyArgs = @()
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Log([string[]]$lines) {
    [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8)
}
function Get-Sha1Of([string]$p) {
    if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' }
}
function Get-Fingerprint {
    $h = @{}
    $ldir = Join-Path $env:LOCALAPPDATA 'TrueToneCap'
    if (Test-Path -LiteralPath $ldir) {
        Get-ChildItem -LiteralPath $ldir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -eq '.json' } |
            ForEach-Object { $h[$_.FullName] = Get-Sha1Of $_.FullName }
    }
    Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*.settings.json' } |
        ForEach-Object { $h[$_.FullName] = Get-Sha1Of $_.FullName }
    return $h
}

[System.IO.File]::WriteAllText($log, '', $utf8)   # 每轮从空文件起，别把上一轮的读数留在尺子里
$before = Get-Fingerprint
$pre = @("=== 轮前设置指纹 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · 共 $($before.Count) 个文件 ===")
foreach ($k in ($before.Keys | Sort-Object)) { $pre += ("  {0}  {1}" -f $before[$k], $k) }
Write-Log $pre

Push-Location $repo
# ⚠ 这里必须真的把 $VerifyArgs 传下去。第一版收了参数却没接进调用 ⇒ "-WriteGateBaseline 没写基线"
#   这件事只能从结果反推，等于一个"能传但不起作用"的假旋钮（本轮自己造的第三个尺子问题）。
$raw = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\verify.ps1') @VerifyArgs 2>&1
$gate = $LASTEXITCODE
Pop-Location
Write-Log (@($raw | ForEach-Object { "$_" }))

$after = Get-Fingerprint
$drift = @()
foreach ($k in ($after.Keys | Sort-Object)) {
    if (-not $before.ContainsKey($k)) { $drift += "  新增 $k = $($after[$k])" }
    elseif ($before[$k] -ne $after[$k]) { $drift += "  变了 $k : $($before[$k]) -> $($after[$k])" }
}
foreach ($k in ($before.Keys | Sort-Object)) {
    if (-not $after.ContainsKey($k)) { $drift += "  消失 $k（轮前是 $($before[$k])）" }
}
Write-Log (@("=== 轮后设置指纹比对：$(if ($drift.Count -eq 0) { '逐字不变（本轮门禁没改用户机器上的设置）' } else { "$($drift.Count) 处漂移" }) ===") + $drift)
Write-Log (@("GATE_EXIT=$gate"))
Write-Output "GATE_EXIT=$gate  SETTINGS_DRIFT=$($drift.Count)  LOG=$log"
exit $gate
