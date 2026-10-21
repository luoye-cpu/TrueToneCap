# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
param([string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe')
$ErrorActionPreference = 'Stop'
$dir = Split-Path $Exe -Parent

$others = @(Get-Process TrueToneCap -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) { Write-Host "ABORT: 已有 $($others.Count) 个实例 (PID $($others.Id -join ','))"; exit 2 }

Add-Type -Namespace N -Name K -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
'@
function Tap([byte]$vk) { [N.K]::keybd_event($vk,0,0,0); Start-Sleep -Milliseconds 60; [N.K]::keybd_event($vk,0,2,0) }
function Chord([byte[]]$vks) { foreach($k in $vks){[N.K]::keybd_event($k,0,0,0); Start-Sleep -Milliseconds 40}
                               $r=@($vks); [Array]::Reverse($r); foreach($k in $r){[N.K]::keybd_event($k,0,2,0); Start-Sleep -Milliseconds 40} }

$log = Join-Path $dir 'log'
New-Item -ItemType Directory -Force -Path $log | Out-Null
$cur = Join-Path $log ("app_{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$pre = if (Test-Path $cur) { (Get-Item $cur).Length } else { 0 }
Write-Host "PRE_OFFSET=$pre  LOG=$cur"

$p = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
Start-Sleep -Seconds 14
Write-Host "发 Ctrl+Shift+S（选区截图）..."
Chord @(0x11,0x10,0x53)
Start-Sleep -Seconds 6
Tap 0x1B                       # 取消覆盖层：不落盘、不写剪贴板
Start-Sleep -Seconds 3
try { Stop-Process -Id $p.Id -Force } catch { Write-Host "结束失败: $($_.Exception.Message)" }
Start-Sleep -Seconds 1
Write-Host "POST_SIZE=$((Get-Item $cur).Length)"
