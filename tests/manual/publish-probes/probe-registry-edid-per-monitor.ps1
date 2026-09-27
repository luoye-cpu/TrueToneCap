# 直接按注册表实例路径读 EDID：证实"每块屏各自的原色"确实存在，且与型号/分辨率对得上。
# 这正是 TrueToneCap 修复后所走的同一条数据源，但取值与解码都在本脚本独立完成。
$base = 'SYSTEM\CurrentControlSet\Enum\DISPLAY'
function Get-EdidName([byte[]]$e) {
    foreach ($off in 54,72,90,108) {
        if ($e[$off] -eq 0x00 -and $e[$off+1] -eq 0x00 -and $e[$off+2] -eq 0x00 -and $e[$off+3] -eq 0xFC) {
            $b = $e[($off+5)..($off+17)]
            return (-join ($b | ForEach-Object { if ($_ -ge 0x20 -and $_ -le 0x7E) { [char]$_ } })).Trim()
        }
    }
    return ''
}
foreach ($inst in Get-ChildItem "HKLM:\$base" -ErrorAction SilentlyContinue) {
    $model = $inst.PSChildName
    foreach ($sub in Get-ChildItem $inst.PSPath -ErrorAction SilentlyContinue) {
        $key = "$($sub.PSPath)\Device Parameters"
        if (-not (Test-Path $key)) { continue }
        $edid = (Get-ItemProperty -Path $key -Name EDID -ErrorAction SilentlyContinue).EDID
        if (-not $edid -or $edid.Length -lt 128) { continue }
        if (-not ($edid[0] -eq 0 -and $edid[7] -eq 0)) { continue }
        $b19 = [int]$edid[0x19]
        $rx = ((([int]$edid[0x1B]) * 4) + ($b19 -band 3)) / 1024.0
        $ry = ((([int]$edid[0x1C]) * 4) + (($b19 -shr 2) -band 3)) / 1024.0
        $gx = ((([int]$edid[0x1D]) * 4) + (($b19 -shr 4) -band 3)) / 1024.0
        $gy = ((([int]$edid[0x1E]) * 4) + (($b19 -shr 6) -band 3)) / 1024.0
        $dg = [math]::Sqrt([math]::Pow($gx-0.30,2) + [math]::Pow($gy-0.60,2))
        Write-Host ("--- DISPLAY\{0}\{1} ---" -f $model, $sub.PSChildName)
        Write-Host ("    EDID 名='{0}'" -f (Get-EdidName $edid))
        Write-Host ("    R=({0:F4},{1:F4}) G=({2:F4},{3:F4})  ΔG={4:F4} ⇒ {5}" -f $rx,$ry,$gx,$gy,$dg,$(if($dg -ge 0.05){'宽'}else{'不宽'}))
    }
}
