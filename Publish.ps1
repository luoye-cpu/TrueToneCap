# TrueToneCap Publish Script
# 完整发布流程：编译 → 收集资源 → 打包
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish\TrueToneCap-v0.1.1-beta"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "╔══════════════════════════════════╗"
Write-Host "║  TrueToneCap v0.1 Beta 发布脚本 ║"
Write-Host "╚══════════════════════════════════╝"
Write-Host ""

# ── 1. Clean & Build ──
Write-Host "[1/4] 编译 Release..."
Push-Location $RepoRoot
taskkill /F /IM TrueToneCap.exe 2>$null
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue

dotnet publish src\TrueToneCap.App\TrueToneCap.App.csproj `
    -c $Configuration -r $Runtime `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# ── 2. Copy PRI (compiled XAML resources) ──
Write-Host "[2/4] 复制 PRI 资源..."
$BinDir = "src\TrueToneCap.App\bin\$Configuration\net10.0-windows10.0.26100.0\$Runtime"
$priFile = Get-ChildItem $BinDir -Filter "TrueToneCap.pri" -ErrorAction SilentlyContinue
if ($priFile) {
    Copy-Item $priFile.FullName $OutputDir -Force
    Write-Host "   TrueToneCap.pri ($([math]::Round($priFile.Length/1KB,1)) KB)"
} else {
    Write-Warning "   TrueToneCap.pri not found in bin, checking obj..."
    $objPri = Get-ChildItem "src\TrueToneCap.App\obj\$Configuration" -Recurse -Filter "TrueToneCap.pri" | Select-Object -First 1
    if ($objPri) {
        Copy-Item $objPri.FullName $OutputDir -Force
        Write-Host "   TrueToneCap.pri from obj ($([math]::Round($objPri.Length/1KB,1)) KB)"
    }
}

# ── 3. Copy XBF (compiled binary XAML) ──
Write-Host "[3/4] 复制 XBF 文件..."
$xbfFiles = Get-ChildItem $BinDir -Filter "*.xbf" -ErrorAction SilentlyContinue
if ($xbfFiles) {
    Copy-Item "$BinDir\*.xbf" $OutputDir -Force
    Write-Host "   $($xbfFiles.Count) XBF files copied"
}

# ── 4. Embed Windows App Runtime MSIX ──
Write-Host "[4/4] 嵌入 Windows App Runtime..."
$NuGetRoot = "$env:USERPROFILE\.nuget\packages"
$SdkDir = Get-ChildItem "$NuGetRoot\microsoft.windowsappsdk" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$MsixDir = "$($SdkDir.FullName)\tools\MSIX\$Runtime"
if (Test-Path $MsixDir) {
    Copy-Item "$MsixDir\*.msix" $OutputDir -Force
    Copy-Item "$MsixDir\MSIX.inventory" $OutputDir -Force
    Write-Host "   $((Get-ChildItem "$MsixDir\*.msix").Count) MSIX packages"
} else {
    Write-Warning "   MSIX not found at $MsixDir"
}

# ── Copy static files ──
Copy-Item README.md, LICENSE $OutputDir -Force

# ── Cleanup ──
Remove-Item $OutputDir\*.pdb -Force -ErrorAction SilentlyContinue

# ── Summary ──
$size = (Get-ChildItem $OutputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
$files = (Get-ChildItem $OutputDir -Recurse -File).Count
Pop-Location

Write-Host ""
Write-Host "╔══════════════════════════════════╗"
Write-Host "║  发布完成!                       ║"
Write-Host "║  $([math]::Round($size,1)) MB  |  $files files         ║"
Write-Host "║  $OutputDir ║"
Write-Host "╚══════════════════════════════════╝"
