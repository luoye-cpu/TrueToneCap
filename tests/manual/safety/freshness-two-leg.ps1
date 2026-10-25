# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 2026-09-29 批次 E：[1.9/2] 换成"按宿主算输入闭包"之后的两腿实测。
# 腿 A：只碰 App 的源码（不重编）⇒ 新口径必须【绿】——这正是今天反复假红的那一格。
# 腿 B：只碰 Core 的源码（不重编）⇒ 两个宿主都必须【红】——证明我没把尺子放宽到看不见真问题。
# 两腿都带 -SkipBuild：按参数约定，滞后只降级成警告不改退出码 ⇒ 判据读的是日志里那段【点名】，
# 不是退出码。跑完后一律 --no-incremental 全解重建，把时间戳状态复位。
# 宿主跟随调用者（2026-10-26 定版）：本探针默认 pwsh 7.x；要覆盖 5.1 就从 5.1 会话再跑一轮。
param([string]$LogName = 'freshness-two-leg.log')
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
# 宿主跟随调用者（2026-10-26 定版：默认 pwsh 7.x，Windows PowerShell 5.1 基线回退）
$psSelf = (Get-Process -Id $PID).Path
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Touch([string]$rel) {
    $p = Join-Path $repo $rel
    (Get-Item -LiteralPath $p).LastWriteTime = (Get-Date).AddSeconds(5)
    return (Get-Item -LiteralPath $p).LastWriteTime.ToString('HH:mm:ss')
}
[System.IO.File]::WriteAllText($log, '', $utf8)

Push-Location $repo
Say @("=== 腿 0：全解重编，把两份宿主都刷到当前源码之后 $(Get-Date -Format 'HH:mm:ss') ===")
$b = @(& dotnet build TrueToneCap.slnx -c Release --no-incremental -v q --nologo 2>&1 | ForEach-Object { "$_" })
Say @("build exit=$LASTEXITCODE  " + (($b | Where-Object { $_ -match '个错误|已成功|error' } | Select-Object -First 2) -join ' / '))

Say @("=== 腿 A：只碰 App 的源码（不重编），期望新口径判【不早】 ===")
Say @("   碰过: src\TrueToneCap.App\Services\SettingsService.cs  新时间戳 $(Touch 'src\TrueToneCap.App\Services\SettingsService.cs')")
$a = @(& $psSelf -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -SkipBuild -SkipPrecision -SkipPublishGates 2>&1 | ForEach-Object { "$_" })
Say ($a | Where-Object { $_ -match '宿主新鲜度|滞后的宿主|比它新的输入|降级为警告' })

Say @("=== 腿 B：只碰 Core 的源码（不重编），期望两个宿主都被点名 ===")
Say @("   碰过: src\TrueToneCap.Core\Encoding\FormatEncoders.cs  新时间戳 $(Touch 'src\TrueToneCap.Core\Encoding\FormatEncoders.cs')")
$c = @(& $psSelf -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -SkipBuild -SkipPrecision -SkipPublishGates 2>&1 | ForEach-Object { "$_" })
Say ($c | Where-Object { $_ -match '宿主新鲜度|滞后的宿主|比它新的输入|降级为警告' })

Say @("=== 复位：再全解重编一次 $(Get-Date -Format 'HH:mm:ss') ===")
$d = @(& dotnet build TrueToneCap.slnx -c Release --no-incremental -v q --nologo 2>&1 | ForEach-Object { "$_" })
Say @("build exit=$LASTEXITCODE  " + (($d | Where-Object { $_ -match '个错误|已成功|error' } | Select-Object -First 2) -join ' / '))
Pop-Location
Write-Output "RESULT=done LOG=$log"
