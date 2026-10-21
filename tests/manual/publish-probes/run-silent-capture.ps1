# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
param([int]$SecondaryX = 4200, [int]$SecondaryY = 300, [int]$PrimaryX = 1200, [int]$PrimaryY = 700)
$ErrorActionPreference = 'Stop'
$exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe'
$dir = Split-Path $exe -Parent
$out = 'D:\TTC'

$others = @(Get-Process TrueToneCap -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) { Write-Host "ABORT: 已有 $($others.Count) 个实例 (PID $($others.Id -join ','))"; exit 2 }

Add-Type -Namespace NK -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetCursorPos(out P p);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr ctx);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct P { public int X; public int Y; }
'@
# 光标坐标必须是物理像素，否则混合缩放下会点错屏
[NK.Win]::SetProcessDpiAwarenessContext([System.IntPtr](-4)) | Out-Null

function Tap([byte]$vk) { [NK.Win]::keybd_event($vk,0,0,0); Start-Sleep -Milliseconds 60; [NK.Win]::keybd_event($vk,0,2,0) }
function Chord([byte[]]$vks) { foreach($k in $vks){[NK.Win]::keybd_event($k,0,0,0); Start-Sleep -Milliseconds 40}
                               $r=@($vks); [Array]::Reverse($r); foreach($k in $r){[NK.Win]::keybd_event($k,0,2,0); Start-Sleep -Milliseconds 40} }
function Snap($label, $x, $y) {
  [void][NK.Win]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 400
  $g = New-Object NK.Win+P; [void][NK.Win]::GetCursorPos([ref]$g)
  Write-Host ("[$label] 光标请求=({0},{1}) 实际=({2},{3})" -f $x, $y, $g.X, $g.Y)
  $before = @(Get-ChildItem $out -File | ForEach-Object { $_.Name })
  Chord @(0x11,0x10,0x51)                  # Ctrl+Shift+Q = 无感截图
  Start-Sleep -Seconds 12
  $after = @(Get-ChildItem $out -File | ForEach-Object { $_.Name })
  $new = @($after | Where-Object { $before -notcontains $_ })
  foreach ($n in $new) { $fi = Get-Item (Join-Path $out $n)
    Write-Host ("[$label] 新产物 {0}  {1} B  {2}" -f $fi.Name, $fi.Length, $fi.LastWriteTime.ToString('HH:mm:ss')) }
  if ($new.Count -eq 0) { Write-Host "[$label] ⚠ 无新产物" }
}

$orig = New-Object NK.Win+P; [void][NK.Win]::GetCursorPos([ref]$orig)
Write-Host ("原始光标=({0},{1})  收尾会还原" -f $orig.X, $orig.Y)

$p = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
Start-Sleep -Seconds 14
try {
  Snap '副屏0x20001' $SecondaryX $SecondaryY
  Snap '主屏0x100A9' $PrimaryX   $PrimaryY
} finally {
  [void][NK.Win]::SetCursorPos($orig.X, $orig.Y)
  Start-Sleep -Seconds 1
  try { Stop-Process -Id $p.Id -Force } catch { Write-Host "结束失败: $($_.Exception.Message)" }
  $g2 = New-Object NK.Win+P; [void][NK.Win]::GetCursorPos([ref]$g2)
  Write-Host ("光标已还原 -> ({0},{1})" -f $g2.X, $g2.Y)
}
