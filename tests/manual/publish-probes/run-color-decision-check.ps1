# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 落一枚真产物再读回它的声明：验证"窄屏 + 跟随显示器 + 烘焙关"这个**用户当前默认设置**下，
# 一次无感截图在文件里到底写了什么色域声明。无 UI 交互、不动鼠标指针。
param([string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe')
$ErrorActionPreference = 'Stop'
$dir = Split-Path $Exe -Parent

$others = @(Get-Process TrueToneCap -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) { Write-Host "ABORT: 已有 $($others.Count) 个实例 (PID $($others.Id -join ','))"; exit 2 }

$sf = Join-Path $env:LOCALAPPDATA 'TrueToneCap\TrueToneCap.settings.json'
$j = Get-Content $sf -Raw | ConvertFrom-Json
$outDir = $j.OutputPath
if ([string]::IsNullOrWhiteSpace($outDir)) { $outDir = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'TrueToneCap' }
Write-Host ("当前设置: ColorSpaceIndex={0}(0=跟随显示器)  IccBakeEnabled={1}  BitDepthPng={2}  HdrEnabled={3}  FormatIndex={4}" -f `
    $j.ColorSpaceIndex, $j.IccBakeEnabled, $j.BitDepthPng, $j.HdrEnabled, $j.FormatIndex)
Write-Host "输出目录: $outDir"
if (-not (Test-Path $outDir)) { Write-Host "ABORT: 输出目录不存在"; exit 2 }
$before = @(Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })

Add-Type -Namespace N2 -Name K2 -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
'@
function Chord([byte[]]$vks) { foreach($k in $vks){[N2.K2]::keybd_event($k,0,0,0); Start-Sleep -Milliseconds 40}
                               $r=@($vks); [Array]::Reverse($r); foreach($k in $r){[N2.K2]::keybd_event($k,0,2,0); Start-Sleep -Milliseconds 40} }

$p = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
Start-Sleep -Seconds 15
Write-Host "发 Ctrl+Shift+Q（无感截图，立即落盘）..."
Chord @(0x11,0x10,0x51)     # Ctrl, Shift, Q
Start-Sleep -Seconds 12
$after = @(Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$new = @($after | Where-Object { $before -notcontains $_ })
Write-Host "新增 PNG 数: $($new.Count)"
foreach ($f in $new) { Write-Host "  $f" }
try { Stop-Process -Id $p.Id -Force } catch { }
Start-Sleep -Seconds 1
if ($new.Count -gt 0) {
    Write-Host "`n=== 读回声明（外部工具 ffprobe，不信本仓库解析器）==="
    $ff = 'C:\Windows\ffmpeg-8.0-full_build\bin\ffprobe.exe'
    foreach ($f in $new) {
        & $ff -hide_banner -i $f 2>&1 | Select-String -Pattern 'Stream|color' | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host "`n=== 本仓库解码器读 cICP/iCCP（与上面互为对照）==="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probe-png-color.ps1') -Files $new
}
