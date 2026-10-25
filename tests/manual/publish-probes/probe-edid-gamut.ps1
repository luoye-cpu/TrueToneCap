# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'Stop'
# ── EDID 原色解码：先自检，再读真机 ──
# 上一版直接从注册表解，三块屏都得到 R=(0.16,0.08) 这种不可能的值 ⇒ 必须用已知真值把
# "解码器错了" 与 "那些 EDID 块本来陈旧/是虚拟设备" 分开，否则拿它回答"副屏是不是只有 709"就是猜。
function Decode-Edid([byte[]]$e) {
  if (-not $e -or $e.Length -lt 128) { return $null }
  $low = $e[25]; $low2 = $e[26]
  $c = @()
  for ($n = 0; $n -lt 8; $n++) {
    if ($n -lt 4) { $lo = ($low -shr (2*$n)) -band 3 } else { $lo = ($low2 -shr (2*($n-4))) -band 3 }
    $c += ((($e[27+$n] -shl 2) -bor $lo) / 1024.0)
  }
  $name = ''
  foreach ($off in 54,72,90,108) {
    if ($e[$off] -eq 0 -and $e[$off+1] -eq 0 -and $e[$off+2] -eq 0xFC) {
      $name = ([Text.Encoding]::ASCII.GetString($e[($off+5)..($off+16)])).Trim()
    }
  }
  $sig = ($e[0..7] | ForEach-Object { $_.ToString('X2') }) -join ''
  return [pscustomobject]@{ Coords=$c; Name=$name; Sig=$sig }
}
function Make-Edid($prim, $name) {
  $e = New-Object byte[] 128
  $head = @(0,80,78,71,13,26,28,254)
  0..7 | ForEach-Object { $e[$_] = [byte]$head[$_] }
  $e[20] = 1; $e[21] = 4
  $lb = 0; $lb2 = 0
  for ($n = 0; $n -lt 8; $n++) {
    $q = [int][math]::Round($prim[$n] * 1024)
    $e[27+$n] = [byte](($q -shr 2) -band 0xFF)
    $lo = $q -band 3
    if ($n -lt 4) { $lb = $lb -bor ($lo -shl (2*$n)) } else { $lb2 = $lb2 -bor ($lo -shl (2*($n-4))) }
  }
  $e[25] = [byte]$lb; $e[26] = [byte]$lb2
  $e[54] = 0; $e[55] = 0; $e[56] = 0; $e[57] = 0xFC; $e[58] = 0
  $nb = [Text.Encoding]::ASCII.GetBytes($name.PadRight(13).Substring(0,13))
  for ($i = 0; $i -lt 13; $i++) { $e[59+$i] = $nb[$i] }
  return ,$e
}

$refs = [ordered]@{
  'BT.709/sRGB' = @(0.6400,0.3300, 0.3000,0.6000, 0.1500,0.0600, 0.3127,0.3290)
  'EBU PDU'     = @(0.6300,0.3400, 0.2950,0.6050, 0.1550,0.0770, 0.3127,0.3290)
  'DCI-P3'      = @(0.6800,0.3200, 0.2650,0.6900, 0.1500,0.0600, 0.3140,0.3510)
  'BT.2020'     = @(0.7080,0.2950, 0.1700,0.7970, 0.1310,0.0460, 0.3127,0.3290)
}
function Area($p) { [double]$a=0; for($i=0;$i -lt 3;$i++){ $j=($i+1)%3; $a += $p[2*$i]*$p[2*$j+1] - $p[2*$j]*$p[2*$i+1] }; [math]::Abs($a)/2 }
function Classify($c) {
  $best=''; $bd=1e9
  foreach ($k in $refs.Keys) {
    $r = $refs[$k]; $d = 0
    for ($i=0;$i -lt 8;$i++) { $d += [math]::Pow($c[$i]-$r[$i],2) }
    $d=[math]::Sqrt($d); if ($d -lt $bd) { $bd=$d; $best=$k }
  }
  return @($best,$bd)
}

Write-Host "=== 自检：喂已知原色，解码必须原样回来 ==="
foreach ($k in $refs.Keys) {
  $d = Decode-Edid (Make-Edid $refs[$k] "SELFTEST")
  $maxErr = 0
  for ($i=0;$i -lt 8;$i++) { $maxErr = [math]::Max($maxErr, [math]::Abs($d.Coords[$i]-$refs[$k][$i])) }
  Write-Host ("  {0,-12} 最大重构造误差 {1:F5}  名={2}  签名={3}  => {4}" -f $k, $maxErr, $d.Name, $d.Sig, $(if ($maxErr -le 0.001) {'PASS'} else {'FAIL'}))
}
Write-Host ""
Write-Host "=== 真机注册表里的 EDID ==="
$n = 0
foreach ($dev in Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\DISPLAY' -ErrorAction SilentlyContinue) {
  foreach ($inst in Get-ChildItem $dev.PSPath -ErrorAction SilentlyContinue) {
    $dp = Join-Path $inst.PSPath 'Device Parameters'
    if (-not (Test-Path $dp)) { continue }
    $e = (Get-ItemProperty -Path $dp -Name EDID -ErrorAction SilentlyContinue).EDID
    if (-not $e) { continue }
    $n++
    $d = Decode-Edid $e
    if ($null -eq $d) { Write-Host "  $($inst.PSChildName): EDID 长度不足 128B"; continue }
    $c = $d.Coords
    $cl = Classify $c
    $areaRatio = (Area $c) / (Area $refs['BT.709/sRGB'])
    Write-Host ("--- {0}  [{1}]  签名={2} 长={3}B ---" -f $(if($d.Name){"$($d.Name)"}else{'(无名字段)'}), $inst.PSChildName, $d.Sig, $e.Length)
    Write-Host ("    R=({0:F4},{1:F4}) G=({2:F4},{3:F4}) B=({4:F4},{5:F4}) 白=({6:F4},{7:F4})" -f $c)
    Write-Host ("    最接近 {0} (距离 {1:F4})   色域面积 = {2:F3}x BT.709" -f $cl[0], $cl[1], $areaRatio)
  }
}
Write-Host "扫到含 EDID 的显示器实例数: $n"
