# PowerShell script to analyze PNG chunk data
# Reads PNG file chunks and prints their contents

param(
    [string]$Dir = "$env:TEMP\TrueToneCap_PngDump"
)

function Read-PngChunks {
    param([string]$Path)
    
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        # Read signature
        $sig = New-Object byte[] 8
        $fs.Read($sig, 0, 8) | Out-Null
        
        $chunks = @()
        while ($true) {
            # Read length
            $lenBuf = New-Object byte[] 4
            if ($fs.Read($lenBuf, 0, 4) -lt 4) { break }
            [array]::Reverse($lenBuf)
            $len = [System.BitConverter]::ToUInt32($lenBuf, 0)
            
            # Read type
            $typeBuf = New-Object byte[] 4
            $fs.Read($typeBuf, 0, 4) | Out-Null
            $type = [System.Text.Encoding]::ASCII.GetString($typeBuf)
            
            # Read data
            $data = New-Object byte[] $len
            if ($len -gt 0) { $fs.Read($data, 0, $len) | Out-Null }
            
            # Read CRC
            $crcBuf = New-Object byte[] 4
            $fs.Read($crcBuf, 0, 4) | Out-Null
            
            $chunks += @{ Type = $type; Length = $len; Data = $data }
            
            if ($type -eq "IEND") { break }
        }
        return $chunks
    } finally { $fs.Close() }
}

function Format-Hex {
    param([byte[]]$Data, [int]$Max = 16)
    $s = ""
    for ($i = 0; $i -lt [Math]::Min($Data.Length, $Max); $i++) {
        $s += "{0:X2} " -f $Data[$i]
    }
    return $s.Trim()
}

$files = Get-ChildItem -Path $Dir -Filter "*.png" | Sort-Object Name

foreach ($f in $files) {
    Write-Host "`n" ("=" * 70) -ForegroundColor Cyan
    Write-Host "FILE: $($f.Name) ($($f.Length) bytes)" -ForegroundColor Yellow
    Write-Host ("=" * 70)
    
    $chunks = Read-PngChunks -Path $f.FullName
    
    for ($i = 0; $i -lt $chunks.Length; $i++) {
        $c = $chunks[$i]
        Write-Host "`n  [$i] $($c.Type) ($($c.Length) bytes)" -ForegroundColor Green
        
        switch ($c.Type) {
            "IHDR" {
                $w = [System.BitConverter]::ToUInt32(@($c.Data[3],$c.Data[2],$c.Data[1],$c.Data[0]), 0)
                $h = [System.BitConverter]::ToUInt32(@($c.Data[7],$c.Data[6],$c.Data[5],$c.Data[4]), 0)
                $bd = $c.Data[8]
                $ct = $c.Data[9]
                $colorNames = @{0="Greyscale";2="Truecolor";3="Indexed";4="Greyscale+Alpha";6="Truecolor+Alpha"}
                Write-Host "       Width: $w, Height: $h"
                Write-Host "       Bit depth: $bd"
                Write-Host "       Color type: $ct ($($colorNames[$ct]))"
                
                if ($ct -eq 6 -and $bd -notin @(8,16)) {
                    Write-Host "       ⚠️  INVALID: Color type 6 only allows bit depth 8 or 16!" -ForegroundColor Red
                } else {
                    Write-Host "       ✅ Valid combination" -ForegroundColor Green
                }
            }
            "sBIT" {
                $vals = @($c.Data | ForEach-Object { $_ })
                Write-Host "       sBIT: $($vals[0])/$($vals[1])/$($vals[2])/$($vals[3]) significant bits (R/G/B/A)"
            }
            "cICP" {
                $prim = $c.Data[0]; $tf = $c.Data[1]; $mat = $c.Data[2]; $fr = $c.Data[3]
                $primNames = @{1="BT.709/sRGB";9="BT.2020";12="Display P3"}
                $tfNames = @{1="BT.709";13="sRGB";16="ST.2084 PQ";18="HLG"}
                Write-Host "       Primaries: $prim ($($primNames[$prim]))"
                Write-Host "       Transfer:  $tf ($($tfNames[$tf]))"
                Write-Host "       Matrix: $mat, Full Range: $fr"
            }
            "IDAT" {
                # Check if data is all zeros (compressed empty content)
                $allZero = ($c.Data | Where-Object { $_ -ne 0 } | Measure-Object).Count -eq 0
                Write-Host "       IDAT: $($c.Length) bytes compressed"
                if ($allZero) { Write-Host "       ⚠️  ALL ZEROS! Image data is empty!" -ForegroundColor Red }
            }
            "IEND" { Write-Host "       End of image" }
            default {
                Write-Host "       Data: $(Format-Hex $c.Data)"
            }
        }
    }
}

Write-Host "`n" ("=" * 70) -ForegroundColor Cyan
Write-Host "ANALYSIS COMPLETE" -ForegroundColor Yellow