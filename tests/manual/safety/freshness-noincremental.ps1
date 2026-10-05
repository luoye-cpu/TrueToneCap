# 2026-09-29 批次 E：把 [1/2] 改成 --no-incremental 之后的两腿实测（跑的是真 verify.ps1，不是副本）。
# 腿 A：只碰 App 的源码 ⇒ 起【完整门禁】。今天两轮假红（gate2/gate4）正是这一格，改完必须【绿】。
# 腿 B：只碰 Core 的源码 ⇒ 起 verify.ps1 -SkipBuild（产物原封不动）。宿主确实落后于输入，必须【红】——
#        这一腿证明我把假红修掉的同时没把牙磨平。
# ⚠ 时间戳用【真实 now】，不许加偏移：上一版把源码钉成"现在 + 2 分钟"，那个未来时间戳是任何
#   真实构建都追不上的 ⇒ 腿 A 读出"仍假红"，其实是夹具造假（freshness-noincremental.log 第一版）。
#   判据那边反而是对的：它就该说"没有宿主的写入时间能晚于一个未来时间"。
param([string]$LogName = 'freshness-noincremental.log')
function Touch([string]$rel) {
    $p = Join-Path $repo $rel
    (Get-Item -LiteralPath $p).LastWriteTime = (Get-Date)
    return (Get-Item -LiteralPath $p).LastWriteTime.ToString('HH:mm:ss.fff')
}
function HostStamps {
    $s = @()
    foreach ($d in @('src\TrueToneCap.Test\bin\Release', 'src\TrueToneCap.Tools\bin\Release')) {
        $full = Join-Path $repo $d
        if (-not (Test-Path -LiteralPath $full)) { $s += "$d=<不存在>"; continue }
        $exe = @(Get-ChildItem -LiteralPath $full -Filter '*.exe' -Recurse -File -ErrorAction SilentlyContinue) |
            Select-Object -First 1
        if (-not $exe) { $s += "$d=<无 exe>"; continue }
        $files = @(Get-ChildItem -LiteralPath $exe.DirectoryName -File -ErrorAction SilentlyContinue)
        $mx = ($files | Measure-Object -Property LastWriteTime -Maximum).Maximum
        $s += ("{0}={1:HH:mm:ss.fff}" -f $d, $mx)
    }
    return ($s -join '  ')
}
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
# ⚠ 这里原本残留了【第二份】Touch（把时间戳钉成 now+2 分钟的那版），而 PowerShell 后定义的覆盖先定义的
#   ⇒ 两腿跑的都是"未来时间戳"，腿 A 必然红、腿 B 必然红，两腿的读数都没有意义。
#   同一个参数有两个写者时，测自己那一半不算验过 —— 这一条已经记在案，本轮又亲手犯了一次。
function Pick($out) { @($out | Where-Object { $_ -match '宿主新鲜度|滞后的宿主|最新的源码|降级为警告' }) }
[System.IO.File]::WriteAllText($log, '', $utf8)
Push-Location $repo

Say @("=== 腿 A：只碰 App 的源码，起完整门禁（含 --no-incremental 自构建） $(Get-Date -Format 'HH:mm:ss') ===")
Say @("   碰过 SettingsService.cs ⇒ $(Touch 'src\TrueToneCap.App\Services\SettingsService.cs')；轮前宿主写入时间：$(HostStamps)")
$a = @(powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 2>&1 | ForEach-Object { "$_" })
Say (@("   腿 A 门禁退出码=$LASTEXITCODE") + (Pick $a))
Say (@("   腿 A 的 RESULT 行：") + @($a | Where-Object { $_ -match '^RESULT suite|总断言|基线已写入' }))

Say @("=== 腿 B：只碰 Core 的源码，-SkipBuild 起门禁（产物不许动） $(Get-Date -Format 'HH:mm:ss') ===")
Say @("   碰过 ⇒ $(Touch 'src\TrueToneCap.Core\Encoding\FormatEncoders.cs')")
$b = @(powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -SkipBuild -SkipPrecision -SkipPublishGates 2>&1 | ForEach-Object { "$_" })
Say (@("   腿 B 退出码=$LASTEXITCODE") + (Pick $b))

Pop-Location
$aTxt = (Pick $a) -join ' '
$bTxt = (Pick $b) -join ' '
$legA = if ($aTxt -match '✅ 宿主新鲜度') { 'PASS(绿)' } elseif ($aTxt -match '滞后的宿主') { 'FAIL(仍假红)' } else { '没读到判据行' }
$legB = if ($bTxt -match '滞后的宿主') { 'PASS(点名滞后宿主)' } elseif ($bTxt -match '✅ 宿主新鲜度') { 'FAIL(牙被磨平)' } else { '没读到判据行' }
Say @("=== 两腿定案 === 腿A(改 App 起全门禁不该红)=$legA   腿B(改 Core 且不重编必须红)=$legB")
Write-Output "RESULT LEG_A=$legA LEG_B=$legB LOG=$log"
