# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 把为验证停掉的产品实例按原样起回来（不带任何诊断环境变量、不带参数）。
$ErrorActionPreference = 'Continue'
$d = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64'
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) { Write-Output ("已有 $($pre.Count) 个实例在跑（PID $(($pre | ForEach-Object { $_.Id }) -join ',')) ⇒ 不再起"); exit 0 }
foreach ($n in @('TTC_FORCE_SDR', 'TTC_FORCE_LEGACY_OVERLAY')) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
$p = Start-Process -FilePath (Join-Path $d 'TrueToneCap.exe') -WorkingDirectory $d -PassThru
Start-Sleep -Seconds 10
$p.Refresh()
Write-Output ("已起回：PID=$($p.Id) 活着=$(-not $p.HasExited)")
# 上一版写成 (Join-Path $d 'log\app_') + (Get-Date).ToString(...)：PS 把整个表达式当一条命令的
# 参数解析，$log 落成 null ⇒ Get-Content 直接报参数校验错。拼接要先算成独立变量。
$day = (Get-Date).ToString('yyyy-MM-dd')
$log = Join-Path $d ("log\app_$day.log")
$tail = @(Get-Content -LiteralPath $log -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 3)
foreach ($l in $tail) { Write-Output ('  | ' + $l.Substring(0, [Math]::Min(140, $l.Length))) }
