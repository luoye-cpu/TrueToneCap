$a=0.822462; $b=0.177194; $c=0.000344
$d=0.033194; $e=0.966799; $f=0.000007
$g=0.017083; $h=0.072411; $i=0.910506

# P3 red in scRGB (BT.709 primaries)
$r=1.344; $s=-0.288; $t=-0.056
$rr=$r*$a+$s*$b+$t*$c
$gg=$r*$d+$s*$e+$t*$f
$bb=$r*$g+$s*$h+$t*$i
Write-Host "=== P3红(1.344,-0.288,-0.056)→P3矩阵 ==="
Write-Host "R=$([Math]::Round($rr,4)) G=$([Math]::Round($gg,4)) B=$([Math]::Round($bb,4))"
Write-Host "P3绿通道(G)是否为负值? $($gg -lt 0)"

# BT.2020 red in scRGB → P3 matrix
$r=0.81; $s=0.045; $t=0.145
$rr=$r*$a+$s*$b+$t*$c
$gg=$r*$d+$s*$e+$t*$f
$bb=$r*$g+$s*$h+$t*$i
Write-Host "`n=== BT.2020红(0.81,0.045,0.145)→P3矩阵 ==="
Write-Host "R=$([Math]::Round($rr,4)) G=$([Math]::Round($gg,4)) B=$([Math]::Round($bb,4))"
$hasNeg = ($rr -lt 0) -or ($gg -lt 0) -or ($bb -lt 0)
Write-Host "包含负值? $hasNeg"

# BT.709→BT.2020 matrix
$a=0.627403; $b=0.329283; $c=0.043313
$d=0.069097; $e=0.919541; $f=0.011362
$g=0.016392; $h=0.088013; $i=0.895595

# P3 red in scRGB → BT.2020
$r=1.344; $s=-0.288; $t=-0.056
$rr=$r*$a+$s*$b+$t*$c
$gg=$r*$d+$s*$e+$t*$f
$bb=$r*$g+$s*$h+$t*$i
Write-Host "`n=== P3红(1.344,-0.288,-0.056)→BT.2020矩阵 ==="
Write-Host "R=$([Math]::Round($rr,4)) G=$([Math]::Round($gg,4)) B=$([Math]::Round($bb,4))"
$hasNeg = ($rr -lt 0) -or ($gg -lt 0) -or ($bb -lt 0)
Write-Host "包含负值? $hasNeg"

# Check all BT.709 [0,1] colors → BT.2020
$negCount=0; $total=0
$vals = @(0, 0.2, 0.5, 0.8, 1.0)
foreach ($rr in $vals) { foreach ($gg in $vals) { foreach ($bb in $vals) {
    $rOut=$rr*$a+$gg*$b+$bb*$c
    $gOut=$rr*$d+$gg*$e+$bb*$f
    $bOut=$rr*$g+$gg*$h+$bb*$i
    if (($rOut -lt 0) -or ($gOut -lt 0) -or ($bOut -lt 0)) { $negCount++ }
    $total++
}}}
Write-Host "`n=== BT.709[0,1]^3→BT.2020: $total色, 负值=$negCount ==="

# HDR 1000nit scene: saturated red
$r=3.0; $s=0.0; $t=0.0
$rr=$r*$a+$s*$b+$t*$c
$gg=$r*$d+$s*$e+$t*$f
$bb=$r*$g+$s*$h+$t*$i
Write-Host "`n=== HDR纯红(3,0,0)→BT.2020 ==="
Write-Host "R=$([Math]::Round($rr,4)) G=$([Math]::Round($gg,4)) B=$([Math]::Round($bb,4))"
$hasNeg = ($rr -lt 0) -or ($gg -lt 0) -or ($bb -lt 0)
Write-Host "包含负值? $hasNeg"