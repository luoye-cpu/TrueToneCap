# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# D4 场景 8 的可做部分：**软件同目录那份设置不见了** ⇒ `MigrateFromLegacyLocation` 会不会把老用户接住。
# 只做这一半的理由：%LOCALAPPDATA%\TrueToneCap 那份是工作区外的用户数据，要动它得先要一句话
# （真正的"首运行、两处都没有"那一格因此仍未评，见报告 §18.4/D4 表）。
# 本脚本只动 bin 目录里那份开发用设置，且三条自检：
#   ① 动之前记下 sha1 + 属性，并确认旧位置那份【确实在】（不在就不是这一格）；
#   ② 跑完之后，如果同目录重新长出文件 ⇒ 把它的**内容原样印出来**（这就是这一格的读数），
#      再删掉它、把原件移回，移回后 sha1 必须与①逐字相同，否则当场报"没还原"；
#   ③ 若同目录压根没长文件 ⇒ 说明这条搬移路没走，读数按"未判"报，不许猜成功。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'legacy-migrate-cell.log',
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Continue'
$log = Join-Path 'C:\PLAN\TrueToneCap\tests\output' $LogName
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
# 旧位置的文件名与同目录那枚【同名】（SettingsService.cs:28 `SettingsFileName = "TrueToneCap.settings.json"`）；
# %LOCALAPPDATA%\TrueToneCap 下还躺着一枚更早的 settings.json，别把它当迁移来源。
$legacy = Join-Path $env:LOCALAPPDATA 'TrueToneCap\TrueToneCap.settings.json'
$aside = Join-Path $dir 'TrueToneCap.settings.json.ttc-aside'
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 旧位置搬移格 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
if (-not (Test-Path -LiteralPath $set)) { Fail "同目录那份不在 ⇒ 这一格要从『它在』起算：$set" }
if (-not (Test-Path -LiteralPath $legacy)) { Fail "旧位置那份不在（$legacy）⇒ 没有可搬的来源，这一格不给结论" }
if (Test-Path -LiteralPath $aside) { Fail "上一轮遗留的 $aside 还在 ⇒ 先人工核对再跑，不自动覆盖" }

$sha0 = Sha $set
Say @("同目录 sha1（轮前）: $sha0  属性=$([IO.File]::GetAttributes($set))")
Say @("旧位置 sha1（只读，不动它）: $(Sha $legacy)")
Say @("旧位置内容: $([IO.File]::ReadAllText($legacy))")

try {
    Move-Item -LiteralPath $set -Destination $aside
    if (Test-Path -LiteralPath $set) { Fail "移走之后同目录仍看得到那份 ⇒ 移动没生效，不给结论" }
    Say @("已把同目录那份移到 $aside（同目录现在【无】设置文件）")

    $outDir = 'C:\PLAN\TrueToneCap\tests\manual\safety\legacy-cell'
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $stdout = Join-Path $outDir 'audit-stdout.txt'
    $p = Start-Process -FilePath $Exe -ArgumentList @('--display-audit', '--out', $outDir) `
        -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $outDir 'audit-stderr.txt')
    if ($null -eq $p.ExitCode) { Fail '退出码读不到 ⇒ 不给结论' }
    Say @("退出码 = $($p.ExitCode)")

    $all = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
    foreach ($h in @($all | Where-Object { $_ -match '设置来源|实际读进来|已从|迁移|旧位置|旧版' })) { Say @("  $h") }

    $reappeared = $false
    if (Test-Path -LiteralPath $set) {
        $reappeared = $true     # ★ 必须在删之前记：删完再 Test-Path 恒为 False，那是一句谎（本轮实测犯过）
        $new = [IO.File]::ReadAllText($set)
        Say @("★ 同目录重新长出文件 ⇒ 搬移路走了。长出来的内容：")
        Say @("  $new")
        Say @("  sha1=$(Sha $set)  轮前那份 sha1=$sha0")
        if ($new -eq [IO.File]::ReadAllText($legacy)) { Say @("  ⇒ 与旧位置逐字相同（纯搬运）") }
        else { Say @("  ⇒ 与旧位置【不】逐字相同（搬的同时被归一/剪枝改写过，看上面两份原文）") }
        Remove-Item -LiteralPath $set -Force
    } else {
        Say @("⏭ 同目录没有长出任何设置文件 ⇒ 这条搬移路本轮【未判】（不许读成成功）")
    }
    Write-Output "RC=0 EXIT=$($p.ExitCode) REAPPEARED=$reappeared LOG=$log"
} finally {
    if (-not (Test-Path -LiteralPath $aside)) {
        Say @('❌ 移开的那份不见了 ⇒ 无法自动还原，请立即人工复查 ' + $aside)
    } else {
        if (Test-Path -LiteralPath $set) { Remove-Item -LiteralPath $set -Force }
        Move-Item -LiteralPath $aside -Destination $set
        $shaEnd = Sha $set
        Say @("还原自检②：原件移回，sha1=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：还原失败，请立即核对' })")
    }
    Say @("轮后旧位置 sha1: $(Sha $legacy)（全程未动）")
}
