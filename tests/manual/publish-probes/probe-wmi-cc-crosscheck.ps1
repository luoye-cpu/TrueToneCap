# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 独立交叉验证：只走 WmiMonitorColorCharacteristics（驱动自己报的原色，不碰注册表 EDID、不碰我的解码器）
# 目的：证实 TrueToneCap 的 HMONITOR→EDID 逐屏映射读到的原色，与另一条完全独立的机制一致。
$cc = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorColorCharacteristics -ErrorAction SilentlyContinue)
Write-Host "WmiMonitorColorCharacteristics 实例数: $($cc.Count)"
foreach ($c in $cc) {
    if (-not $c.Active) { Write-Host "  $($c.InstanceName): Active=false ⇒ 跳过"; continue }
    $rx = $c.RedX / 255.0; $ry = $c.RedY / 255.0
    $gx = $c.GreenX / 255.0; $gy = $c.GreenY / 255.0
    $bx = $c.BlueX / 255.0; $by = $c.BlueY / 255.0
    $wx = $c.WhiteX / 255.0; $wy = $c.WhiteY / 255.0
    $dg = [math]::Sqrt([math]::Pow($gx - 0.30, 2) + [math]::Pow($gy - 0.60, 2))
    Write-Host ("--- {0} ---" -f $c.InstanceName)
    Write-Host ("    R=({0:F4},{1:F4}) G=({2:F4},{3:F4}) B=({4:F4},{5:F4}) 白=({6:F4},{7:F4})" -f $rx,$ry,$gx,$gy,$bx,$by,$wx,$wy)
    Write-Host ("    ΔG(对 709 绿点) = {0:F4}   ⇒ {1}" -f $dg, $(if ($dg -ge 0.05) {'宽'} else {'不宽'}))
    Write-Host ("    白点是否为 D65 形态(0.3127,0.3290): 误差 {0:F4}" -f [math]::Sqrt([math]::Pow($wx-0.3127,2)+[math]::Pow($wy-0.3290,2)))
}
