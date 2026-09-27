param([string[]]$Files)
$ErrorActionPreference = 'Stop'
foreach ($f in $Files) {
  $b = [System.IO.File]::ReadAllBytes($f)
  $sig = ($b[0..7] | ForEach-Object { $_.ToString('X2') }) -join ''
  Write-Host ("=== {0} ===" -f (Split-Path $f -Leaf))
  Write-Host ("  签名 {0}  大小 {1} B" -f $sig, $b.Length)
  $i = 8; $chunks = @()
  while ($i -lt $b.Length) {
    $len = [int]($b[$i]*16777216 + $b[$i+1]*65536 + $b[$i+2]*256 + $b[$i+3])
    $type = [Text.Encoding]::ASCII.GetString($b, $i+4, 4)
    $chunks += ("{0}({1})" -f $type, $len)
    if ($type -eq 'IHDR') {
      $w = [int]($b[$i+8]*16777216 + $b[$i+9]*65536 + $b[$i+10]*256 + $b[$i+11])
      $h = [int]($b[$i+12]*16777216 + $b[$i+13]*65536 + $b[$i+14]*256 + $b[$i+15])
      Write-Host ("  IHDR: {0}x{1}  bitDepth={2} colorType={3} interlace={4}" -f $w,$h,$b[$i+16],$b[$i+17],$b[$i+20])
    }
    if ($type -eq 'cICP') {
      Write-Host ("  cICP: primaries={0} transfer={1} matrix={2} fullRange={3}  (9/16=PQ-BT2020, 1/13=sRGB)" `
        -f $b[$i+8], $b[$i+9], $b[$i+10], $b[$i+11])
    }
    if ($type -eq 'iCCP') {
      $s = $i+8; $e = $s; while ($b[$e] -ne 0) { $e++ }
      $name = [Text.Encoding]::ASCII.GetString($b, $s, $e-$s)
      Write-Host ("  iCCP: 名=`"{0}`" 压缩法={1} 载荷 {2} B" -f $name, $b[$e+1], ($len - ($e-$s) - 2))
    }
    if ($type -eq 'sBIT') {
      $v = @(); for ($k=0; $k -lt $len; $k++) { $v += $b[$i+8+$k] }
      Write-Host ("  sBIT: {0}" -f ($v -join ','))
    }
    if ($type -eq 'IEND') { break }
    $i = $i + 12 + $len
  }
  Write-Host ("  块序: {0}" -f (($chunks | Select-Object -First 9) -join ' '))
}
