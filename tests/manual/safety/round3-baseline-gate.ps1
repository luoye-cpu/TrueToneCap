# 2026-09-29 审查轮 · 基线门禁。先拍用户设置指纹，跑 scripts/verify.ps1，收尾比差集。
# 规约来源：门禁自己的退出码必须是本轮最后一步（包装层的 exit 0 会顶掉它）；
# 子进程方式起 verify.ps1 ⇒ & 调用会在 verify.ps1 内 exit 时带走本会话，后置指纹比对就跑不到了。
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo 'tests\output\round3-baseline-gate.log'

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

$before = Get-Fingerprint
$lines = @()
$lines += "=== 轮前设置指纹 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · 共 $($before.Count) 个文件 ==="
foreach ($k in ($before.Keys | Sort-Object)) { $lines += ("  {0}  {1}" -f $before[$k], $k) }
$lines | Set-Content -LiteralPath $log -Encoding UTF8

Push-Location $repo
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\verify.ps1') 2>&1 |
    Tee-Object -FilePath $log -Append
$gate = $LASTEXITCODE
Pop-Location

$after = Get-Fingerprint
$drift = @()
foreach ($k in ($after.Keys | Sort-Object)) {
    if (-not $before.ContainsKey($k)) { $drift += "  新增 $k = $($after[$k])" }
    elseif ($before[$k] -ne $after[$k]) { $drift += "  变了 $k : $($before[$k]) -> $($after[$k])" }
}
foreach ($k in ($before.Keys | Sort-Object)) {
    if (-not $after.ContainsKey($k)) { $drift += "  消失 $k（轮前是 $($before[$k])）" }
}
"=== 轮后设置指纹比对：$(if ($drift.Count -eq 0) { '逐字不变（本轮门禁没改用户机器上的设置）' } else { "$($drift.Count) 处漂移" }) ===" |
    Add-Content -LiteralPath $log
$drift | Add-Content -LiteralPath $log
"GATE_EXIT=$gate" | Add-Content -LiteralPath $log
Write-Output "GATE_EXIT=$gate  SETTINGS_DRIFT=$($drift.Count)"
exit $gate
