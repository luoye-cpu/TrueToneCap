# 2026-09-29 批次 E：[1.9/2] 换成"按宿主算输入闭包"之后的两腿实测（跑真 verify.ps1，不跑副本）。
# 腿 A：只碰 App 的源码（不重编）⇒ 必须【绿】——这是今天反复假红的那一格（gate2/gate4 两轮）。
# 腿 B：只碰 Core 的源码（不重编）⇒ 两个宿主都必须【红】——证明尺子没被放宽到看不见真问题。
# 时间戳一律挪到"现在 + 2 分钟"：上一版只加 5 秒，被腿 A 那轮套件写宿主目录的时间盖过去 ⇒
# 读数落在噪声里，这一条本身就是要记的 harness 教训。
# 两腿都带 -SkipBuild：按参数约定滞后只降级成警告、不改退出码 ⇒ 判据读日志里的【点名】。
param([string]$LogName = 'freshness-two-leg2.log')
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Touch([string]$rel) {
    $p = Join-Path $repo $rel
    (Get-Item -LiteralPath $p).LastWriteTime = (Get-Date).AddMinutes(2)
    return (Get-Item -LiteralPath $p).LastWriteTime.ToString('HH:mm:ss')
}
function Build([string]$why) {
    $b = @(& dotnet build TrueToneCap.slnx -c Release --no-incremental -v q --nologo 2>&1 | ForEach-Object { "$_" })
    Say @("$why build exit=$LASTEXITCODE  " + (($b | Where-Object { $_ -match '个错误|已成功|error' } | Select-Object -First 2) -join ' / '))
}
function LegRun([string]$tag) {
    $o = @(& powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -SkipBuild -SkipPrecision -SkipPublishGates 2>&1 | ForEach-Object { "$_" })
    $hits = @($o | Where-Object { $_ -match '宿主新鲜度|滞后的宿主|比它新的输入|降级为警告' })
    Say (@("   [$tag] 退出码=$($o.Count) 行输出，判据行：") + $hits)
    return @($hits -join ' ')
}
[System.IO.File]::WriteAllText($log, '', $utf8)
Push-Location $repo

Say @("=== 腿 0：全解重编，把两份宿主都刷到当前源码之后 $(Get-Date -Format 'HH:mm:ss') ===")
Build '腿0'

Say @("=== 腿 A：只碰 App 的源码（不重编），期望判【不早】（旧口径在这里假红） ===")
Say @("   碰过 src\TrueToneCap.App\Services\SettingsService.cs ⇒ $(Touch 'src\TrueToneCap.App\Services\SettingsService.cs')")
$aTxt = LegRun 'A'

Say @("=== 腿 B：只碰 Core 的源码（不重编），期望两个宿主都被点名 ===")
Say @("   碰过 src\TrueToneCap.Core\Encoding\FormatEncoders.cs ⇒ $(Touch 'src\TrueToneCap.Core\Encoding\FormatEncoders.cs')")
$bTxt = LegRun 'B'

Say @("=== 复位：再全解重编一次 $(Get-Date -Format 'HH:mm:ss') ===")
Build '复位'
Pop-Location

$legA = if ($aTxt -match '✅ 宿主新鲜度') { 'PASS(绿，符合期望)' } elseif ($aTxt -match '滞后的宿主') { 'FAIL(仍假红)' } else { '没读到判据行' }
$legB = if ($bTxt -match '滞后的宿主') { 'PASS(两个宿主被点名)' } elseif ($bTxt -match '✅ 宿主新鲜度') { 'FAIL(没抓到 Core 改动)' } else { '没读到判据行' }
Say @("=== 两腿定案 ===  腿A(改 App 不该红)=$legA   腿B(改 Core 必须红)=$legB")
Write-Output "RESULT LEG_A=$legA LEG_B=$legB LOG=$log"
