# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'Stop'
# ═══ 用 WMI 拿【当前连接】显示器的原始 EDID，直接读面板原色 ═══
# 为什么走 WMI 而不是注册表 Enum\DISPLAY\...\EDID：后者会留下拔出/虚拟设备的陈旧条目，
# 上一版就是被它骗了（三块屏都解出 R=(0.16,0.08) 这种不可能的值，且全是"无名字段"）。
# 纪律：解码器必须先过"已知真值自检"，自检不过就不许拿它的输出去回答任何问题。

function Decode-Edid {
    param([Parameter(Mandatory=$true)][byte[]]$e)
    if ($e.Length -lt 128) { return $null }
    $low = [int]$e[25]; $low2 = [int]$e[26]
    $c = New-Object 'double[]' 8
    for ($n = 0; $n -lt 8; $n++) {
        if ($n -lt 4) { $lo = ($low -shr (2*$n)) -band 3 } else { $lo = ($low2 -shr (2*($n-4))) -band 3 }
        $c[$n] = ((([int]$e[27+$n]) -shl 2) -bor $lo) / 1024.0
    }
    $name = ''
    # 描述符 18 字节：[0..1]=0、[2]=0、[3]=标签、[4]=0、[5..17]=文本 ⇒ 标签在 off+3
    foreach ($off in 54,72,90,108) {
        if ($e[$off] -eq 0 -and $e[$off+1] -eq 0 -and $e[$off+3] -eq 0xFC) {
            $name = [Text.Encoding]::ASCII.GetString($e[($off+5)..($off+17)]).Trim()
        }
    }
    return [pscustomobject]@{ Coords=$c; Name=$name; Sig=(($e[0..7] | ForEach-Object { $_.ToString('X2') }) -join '') }
}

function Make-Edid {
    param([Parameter(Mandatory=$true)][double[]]$Prim,
          [Parameter(Mandatory=$true)][string]$Name,
          [int]$Tag = 0xFC,
          [int]$TextAt = 5)
    $e = New-Object byte[] 128
    @(0x00,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x00) | ForEach-Object -Begin { $i=0 } -Process { $e[$i]=[byte]$_; $i++ }
    $e[20]=1; $e[21]=4
    $lb=0; $lb2=0
    for ($n = 0; $n -lt 8; $n++) {
        $q = [int][math]::Round($Prim[$n] * 1024)
        $e[27+$n] = [byte](($q -shr 2) -band 0xFF)
        $lo = $q -band 3
        if ($n -lt 4) { $lb = $lb -bor ($lo -shl (2*$n)) } else { $lb2 = $lb2 -bor ($lo -shl (2*($n-4))) }
    }
    $e[25]=[byte]$lb; $e[26]=[byte]$lb2
    $e[54]=0; $e[55]=0; $e[56]=0; $e[57]=[byte]$Tag; $e[58]=0
    $txt = $Name.PadRight(13).Substring(0,13)
    $nb = [Text.Encoding]::ASCII.GetBytes($txt)
    for ($i = 0; $i -lt 13; $i++) { $e[59+$i] = $nb[$i] }
    return $e
}

$refs = [ordered]@{
  'BT.709/sRGB' = @(0.6400,0.3300, 0.3000,0.6000, 0.1500,0.0600, 0.3127,0.3290)
  'EBU PDU'     = @(0.6300,0.3400, 0.2950,0.6050, 0.1550,0.0770, 0.3127,0.3290)
  'DCI-P3'      = @(0.6800,0.3200, 0.2650,0.6900, 0.1500,0.0600, 0.3140,0.3510)
  'BT.2020'     = @(0.7080,0.2950, 0.1700,0.7970, 0.1310,0.0460, 0.3127,0.3290)
}
function Area([double[]]$p) {
    [double]$a = 0
    for ($i=0; $i -lt 3; $i++) { $j=($i+1)%3; $a += $p[2*$i]*$p[2*$j+1] - $p[2*$j]*$p[2*$i+1] }
    return [math]::Abs($a)/2
}
function Classify([double[]]$c) {
    $best=''; $bd=1e9
    foreach ($k in $refs.Keys) {
        $r=[double[]]$refs[$k]; $d=0
        for ($i=0;$i -lt 8;$i++) { $d += [math]::Pow($c[$i]-$r[$i],2) }
        $d=[math]::Sqrt($d); if ($d -lt $bd) { $bd=$d; $best=$k }
    }
    return @($best,$bd)
}

Write-Host "=== 自检（解码器必须先过这一关）==="
$ok = $true
foreach ($k in $refs.Keys) {
    $nm = ("SELFTEST-" + $k); $fx = Make-Edid -Prim $refs[$k] -Name $nm
    $d  = Decode-Edid -e $fx
    if ($null -eq $d) { Write-Host "  $k -> Decode 返回 null，FAIL"; $ok=$false; continue }
    $maxErr = 0
    for ($i=0;$i -lt 8;$i++) { $maxErr = [math]::Max($maxErr, [math]::Abs($d.Coords[$i]-$refs[$k][$i])) }
    $pass = ($maxErr -le 0.001) -and ($d.Name -eq $nm.Substring(0,13).Trim()) -and ($d.Sig -eq '00FFFFFFFFFFFF00')
    if (-not $pass) { $ok = $false }
    Write-Host ("  {0,-12} 误差={1:F5} 名='{2}' 签名={3}  => {4}" -f $k,$maxErr,$d.Name,$d.Sig, $(if($pass){'PASS'}else{'FAIL'}))
}
if (-not $ok) { Write-Host "`n❌ 自检未过 ⇒ 下面真机读数一律不作为结论依据"; exit 1 }

Write-Host "`n=== 真机：只取当前【激活】的显示器 ==="
$mons = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue)
Write-Host "root\wmi\WmiMonitorID 实例数: $($mons.Count)"
$cc = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorColorCharacteristics -ErrorAction SilentlyContinue)
foreach ($m in $mons) {
    if (-not $m.Active) { Write-Host "  $($m.InstanceName): Active=false ⇒ 跳过"; continue }
    $mn = ($m.InstanceName -replace '_0$','')
    $r = Invoke-CimMethod -Namespace root\wmi -ClassName WmiMonitorDescriptorMethods `
                          -MethodName WmiGetMonitorRawEEdidV1Block -Arguments @{ MonitorInstanceName = $mn; BlockId = 0 }
    if ($r.ReturnValue -ne 0 -or -not $r.BlockContent) {
        Write-Host "--- $mn : 取块失败 ReturnValue=$($r.ReturnValue)"; continue }
    # BlockContent 就是那 128 字节基块本身（uint8[]）
    $base = @($r.BlockContent | ForEach-Object { [byte]$_ })
    $d = Decode-Edid -e $base
    $c = $d.Coords; $cl = Classify $c
    $ratio = (Area $c) / (Area ([double[]]$refs['BT.709/sRGB']))
    Write-Host ("--- {0}  (实例 {1}，块长 {2}B) ---" -f $(if($d.Name){$d.Name}else{'(EDID 无名)'}), $mn, $base.Length)
    Write-Host ("    R=({0:F4},{1:F4}) G=({2:F4},{3:F4}) B=({4:F4},{5:F4}) 白=({6:F4},{7:F4})" -f $c[0],$c[1],$c[2],$c[3],$c[4],$c[5],$c[6],$c[7])
    Write-Host ("    最接近 {0} (距离 {1:F4})   色域面积 = {2:F3}x BT.709" -f $cl[0], $cl[1], $ratio)
    # 第二个独立来源：驱动自己报的颜色特性（不参与我的解码，纯交叉验证）
    $mine = $cc | Where-Object { $_.InstanceName -eq $m.InstanceName -or $_.Active -and ($_.InstanceName -like "$mn*") } | Select-Object -First 1
    if ($mine) {
        $kv = $mine.PSObject.Properties | Where-Object { $_.Name -notmatch '^Cim|^Pri|^Qual' } |
              ForEach-Object { "$($_.Name)=$($_.Value)" }
        Write-Host ("    交叉验证 WmiMonitorColorCharacteristics: {0}" -f ($kv -join '  '))
    }
}
