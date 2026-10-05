# 2026-09-29 审查轮 · 单套件包装器（跑一条判据而不必等整轮门禁）。
# 与 gate-runner.ps1 同一纪律：先全解重编（避免 [1.9/2] 那种"宿主比源码旧"的自伤）、
# 日志落成单一 UTF-8、收尾拍设置指纹。
param(
    [string]$Suite = '--unit-tests',
    [string]$LogName = 'suite.log'
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha1Of([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Fingerprint {
    $h = @{}
    Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*.settings.json' } |
        ForEach-Object { $h[$_.FullName] = Sha1Of $_.FullName }
    return $h
}

[System.IO.File]::WriteAllText($log, '', $utf8)
$before = Fingerprint
Say @("=== 单套件 $Suite · 轮前设置指纹 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · $($before.Count) 个文件 ===")

Push-Location $repo
$build = @(dotnet build TrueToneCap.slnx -c Release -v q --nologo 2>&1 | ForEach-Object { "$_" })
$bc = $LASTEXITCODE
Say @("--- 全解重编 exit=$bc ---")
Say ($build | Where-Object { $_ -match 'error|警告|warning|Build succeeded|已成功|个错误' })
if ($bc -ne 0) { Say @("BUILD_FAILED exit=$bc"); Pop-Location; Write-Output "RESULT=build-failed LOG=$log"; exit 1 }

$all = @(Get-ChildItem (Join-Path $repo 'src\TrueToneCap.Test\bin') -Filter 'TrueToneCap.Test.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -split '\\' -contains 'Release' } |
    Sort-Object LastWriteTime -Descending)
# 与常驻门禁【同一条选择规则】（bin 下按 Release 段过滤、取最近写入的一份），否则"单套件绿"与
# "门禁绿"可能判的是两个二进制。bin 里确实同时躺着 bin\Release 与 bin\x64\Release 两份 ⇒
# 这里把两份的 SHA256 都落进日志：它们内容不同时看得见，而不是靠"哪一个碰巧最新"。
if ($all.Count -lt 1) {
    Say @('❌ 一枚候选宿主都没枚到 ⇒ 本轮不给结论')
    Pop-Location; Write-Output "RESULT=no-exe LOG=$log"; exit 1
}
$testExe = $all[0]
Say @("宿主候选 $($all.Count) 份，选用最新写入的那份（与 verify.ps1 同规则）:")
foreach ($c in $all) {
    $sha = (Get-FileHash -LiteralPath $c.FullName -Algorithm SHA256).Hash
    Say @("   $($c.LastWriteTime.ToString('MM-dd HH:mm:ss'))  $sha  $($c.FullName)" +
          $(if ($c.FullName -eq $testExe.FullName) { '   <= 选中' } else { '' }))
}
Say @("宿主: $($testExe.FullName)")
$raw = @(& $testExe.FullName $Suite 2>&1 | ForEach-Object { "$_" })
$code = $LASTEXITCODE
Say $raw
Say @("SUITE_EXIT=$code")
$after = Fingerprint
$drift = @()
foreach ($k in ($after.Keys | Sort-Object)) {
    if (-not $before.ContainsKey($k)) { $drift += "  新增 $k = $($after[$k])" }
    elseif ($before[$k] -ne $after[$k]) { $drift += "  变了 $k : $($before[$k]) -> $($after[$k])" }
}
Say (@("=== 轮后设置指纹：$(if ($drift.Count -eq 0) { '逐字不变' } else { "$($drift.Count) 处漂移" }) ===") + $drift)
Pop-Location
$resultLine = ($raw | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1)
Write-Output "RESULT=ok EXIT=$code  $resultLine  DRIFT=$($drift.Count) LOG=$log"
exit $code
