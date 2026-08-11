# shaders/CompileShaders.ps1
# 使用 fxc (SM5.0) 编译 HLSL 着色器为 CSO 字节码
# 2026-08-11: dxc → fxc。dxc 强制 SM6/DXIL，D3D11 CreateShader 报 E_INVALIDARG；
#            fxc 产出 SM5.0 DXBC，D3D11/Vortice 全兼容。
# 前置条件：安装 Windows SDK

param(
    [string]$ShaderDir = $PSScriptRoot,
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

# 查找 fxc.exe
$fxc = Get-Command "fxc.exe" -ErrorAction SilentlyContinue
if (-not $fxc) {
    $sdkPaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe"
    )
    $fxc = $sdkPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $fxc) {
        Write-Error "找不到 fxc.exe。请安装 Windows SDK。"
        exit 1
    }
}

Write-Host "使用编译器: $fxc"

# 着色器列表 (SM5.0: vs_5_0 / ps_5_0)
$shaders = @(
    @{ Input = "ToneMapping.hlsl";      Entry = "main"; Profile = "ps_5_0" },
    @{ Input = "FullscreenVS.hlsl";     Entry = "main"; Profile = "vs_5_0" },
    @{ Input = "OverlayComposite.hlsl"; Entry = "main"; Profile = "ps_5_0" }
)

# 确保输出目录存在
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

foreach ($s in $shaders) {
    $inputPath  = Join-Path $ShaderDir $s.Input
    $outputPath = Join-Path $OutputDir "$($s.Input).cso"

    if (-not (Test-Path $inputPath)) {
        Write-Warning "跳过: $inputPath (文件不存在)"
        continue
    }

    Write-Host "编译 $($s.Input) → $outputPath"
    $result = & $fxc /T $s.Profile /E $s.Entry /Fo $outputPath $inputPath 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Error "着色器编译失败: $($s.Input)`n$result"
        exit 1
    }
}

Write-Host "✓ 所有着色器编译完成 ($( $shaders.Count ) 个)"
