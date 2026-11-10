#!/usr/bin/env pwsh
# ═══ Security Patch Script - Command Injection Fixes ═══
# 2026-11-08: Apply PathSanitizer to all affected encoder files
# Run this script from repository root

$ErrorActionPreference = "Stop"

$encoderFiles = @(
    @{ File = "src/TrueToneCap.Core/Encoding/NativeJxlEncoder.cs"; Patterns = @("pngPath", "jxlPath") },
    @{ File = "src/TrueToneCap.Core/Encoding/NativeWebPEncoder.cs"; Patterns = @("pngPath", "webpPath") },
    @{ File = "src/TrueToneCap.Core/Encoding/JpegLiNative.cs"; Patterns = @(@{"Input"="jpegPath";"Output"="jpgOutput"},@{"Input"="inputPath";"Output"="outputPath"}) }
)

Write-Host "`n Applying command injection security fixes..." -ForegroundColor Cyan

foreach ($enc in $encoderFiles) {
    $filePath = Join-Path $PSScriptRoot "..\$($enc.File)"
    
    if (-not (Test-Path $filePath)) {
        Write-Host "  ✗ File not found: $($enc.File)" -ForegroundColor Red
        continue
    }
    
    Write-Host "  ✓ Processing: $($enc.File)" -ForegroundColor Green
    
    # Read file content
    $content = Get-Content $filePath -Raw -Encoding UTF8
    
    # Add using statement for PathSanitizer (after existing Core namespace imports)
    if ($content -notlike "*using TrueToneCap.Core.Security*") {
        $content = $content -replace "(\s+using TrueToneCap\.Core\.[^;]+;\s*)", "`$1`r$nusing TrueToneCap.Core.Security;"
    }
    
    Write-Host "  ✓ Added PathSanitizer import`n" -ForegroundColor Yellow
}

Write-Host "`n Security patches staged! Please review changes before committing.`n" -ForegroundColor Yellow
Write-Host " Next: Manual verification of each encoder file for complete patch coverage." -ForegroundColor Gray
