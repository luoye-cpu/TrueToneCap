# shaders/CompileShaders.ps1
# 使用 DirectXShaderCompiler (dxc) 编译 HLSL 着色器为 CSO 字节码
# 前置条件：安装 Windows SDK 或单独安装 dxc.exe

param(
    [string]$ShaderDir = $PSScriptRoot,
    [string]$OutputDir = "$PSScriptRoot\..\src\TrueToneCap.App\Shaders"
)

$ErrorActionPreference = "Stop"

# 查找 dxc.exe
$dxc = Get-Command "dxc.exe" -ErrorAction SilentlyContinue
if (-not $dxc) {
    # 尝试 Windows SDK 默认路径
    $sdkPaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\dxc.exe"
    )
    $dxc = $sdkPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $dxc) {
        Write-Error "找不到 dxc.exe。请安装 Windows SDK 或 DirectXShaderCompiler。"
        exit 1
    }
}

Write-Host "使用编译器: $dxc"

# 着色器列表
$shaders = @(
    @{
        Input  = "ToneMapping.hlsl"
        Entry  = "main"
        Profile = "ps_6_0"
    },
    @{
        Input  = "MosaicEffect.hlsl"
        Entry  = "main"
        Profile = "ps_6_0"
    }
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
    $result = & $dxc -T $s.Profile -E $s.Entry -Fo $outputPath $inputPath 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Error "着色器编译失败: $($s.Input)`n$result"
        exit 1
    }
}

Write-Host "✓ 所有着色器编译完成 ($( $shaders.Count ) 个)"
