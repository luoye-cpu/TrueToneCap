# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# shaders/CompileShaders.ps1
# 使用 fxc (SM5.0) 编译 HLSL 着色器为 CSO 字节码
# 2026-08-11: dxc → fxc。dxc 强制 SM6/DXIL，D3D11 CreateShader 报 E_INVALIDARG；
#            fxc 产出 SM5.0 DXBC，D3D11/Vortice 全兼容。
# 前置条件：安装 Windows SDK

param(
    [string]$ShaderDir = $PSScriptRoot,
    [string]$OutputDir = $PSScriptRoot,
    # 钉住 Windows SDK 版本（形如 10.0.26100.0）。
    # 不指定时仍走「枚举取最高」的回退逻辑 —— 那能保证"能编出来"，
    # 但【不能】保证可复现：装了更新 SDK 的机器会产出不同 DXBC。
    # 需要可复现构建（发布/CI）时显式传入，例如:
    #   CompileShaders.ps1 -WinSdkVersion 10.0.26100.0
    [string]$WinSdkVersion = ""
)

$ErrorActionPreference = "Stop"

# 查找 fxc.exe
$fxc = $null
if ($WinSdkVersion) {
    # 2026-09-15 新增：显式钉版本优先。命中即用，未命中【直接失败】而不是悄悄回退
    # —— 静默回退会让「我以为钉住了」变成假象，比不钉更危险。
    $pfx86Pinned = ${env:ProgramFiles(x86)}
    if (-not $pfx86Pinned) { $pfx86Pinned = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86) }
    $pinned = Join-Path $pfx86Pinned "Windows Kits\10\bin\$WinSdkVersion\x64\fxc.exe"
    if (Test-Path $pinned) {
        $fxc = $pinned
        Write-Host "使用钉定的 Windows SDK 版本: $WinSdkVersion"
    } else {
        Write-Error "已指定 -WinSdkVersion $WinSdkVersion，但未找到 $pinned（不做静默回退，避免出现已钉住的假象）。"
        exit 1
    }
}
if (-not $fxc) {
    $fxc = Get-Command "fxc.exe" -ErrorAction SilentlyContinue
}
if (-not $fxc) {
    # 枚举 Windows Kits\10\bin\<version>\x64\fxc.exe，取版本号最高的可用项。
    # ⚠️ 不要写死具体 SDK 版本号（2026-09-14 修正）：原实现硬编码 10.0.26100.0 / 10.0.22621.0，
    #    只装了其他版本（如 10.0.28000.0）的机器会直接"找不到 fxc.exe"而构建失败。
    #    必须与运行时 src/TrueToneCap.App/Services/ShaderCompiler.cs 的 FindFxc() 保持同一套查找逻辑。
    # 优先用 .NET SpecialFolder 而非 $env:ProgramFiles(x86)：后者在部分环境（CI 容器、
    # 精简 shell、被清理过的进程环境）下为 $null，会直接让 Join-Path 抛参数绑定错误，
    # 报出"Cannot bind argument to parameter 'Path'"这种完全误导的信息。
    # 与运行时 ShaderCompiler.FindFxc() 的取法保持一致。
    $pfx86 = ${env:ProgramFiles(x86)}
    if (-not $pfx86) { $pfx86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86) }
    $kitRoot = Join-Path $pfx86 "Windows Kits\10\bin"
    $fxc = Get-ChildItem -Path $kitRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\fxc.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
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

# ══════════════════════════════════════════════════════════════
#  清单一致性自检（2026-09-15 新增）
# ══════════════════════════════════════════════════════════════
# 同一份着色器清单散落在四处，靠人工同步必然漂移：
#   ① 本脚本的 $shaders（构建期编译）
#   ② Shaders\*.hlsl 磁盘文件
#   ③ Services\ShaderCompiler.cs 的 s_shaders（运行时兜底编译）
#   ④ App.csproj 中 CompileShaders Target 的 Inputs（增量构建判定）
#
# 历史教训（2026-09-14 实测）：运行时那一份长期停留在 ps_6_0 / dxc，而构建期早已是
#   ps_5_0 / fxc。故障只在「cso 缺失且运行时触发兜底编译」这条罕见路径上出现 ——
#   产出 D3D11 根本加载不了的 SM6 着色器，且只留一行 Debug 日志。
# 因此在这里硬校验：任一处不同步直接终止构建，而不是留到运行时才炸。
$scriptPairs  = @($shaders | ForEach-Object { "$($_.Input)=$($_.Profile)" } | Sort-Object)
$scriptInputs = @($shaders | ForEach-Object { $_.Input } | Sort-Object)
$diskHlsl     = @(Get-ChildItem -Path $ShaderDir -Filter '*.hlsl' -File | Select-Object -ExpandProperty Name | Sort-Object)

$projDir  = Split-Path $ShaderDir -Parent
$csPath   = Join-Path $projDir 'Services\ShaderCompiler.cs'
$csPairs  = @()
if (Test-Path $csPath) {
    $csText  = Get-Content $csPath -Raw
    $csPairs = @([regex]::Matches($csText, 'new\(\s*"([^"]+\.hlsl)"\s*,\s*"([^"]+)"\s*\)') |
                 ForEach-Object { "$($_.Groups[1].Value)=$($_.Groups[2].Value)" } | Sort-Object)
}

$csprojPath   = Join-Path $projDir 'TrueToneCap.App.csproj'
$csprojInputs = @()
if (Test-Path $csprojPath) {
    $cpText = Get-Content $csprojPath -Raw
    $m = [regex]::Match($cpText, 'Name="CompileShaders"[^>]*Inputs="([^"]+)"')
    if ($m.Success) {
        $csprojInputs = @($m.Groups[1].Value -split ';' | Where-Object { $_ } |
                          ForEach-Object { ($_ -split '\\')[-1] } | Sort-Object)
    }
}

$problems = @()
if (($scriptPairs -join ',') -ne ($csPairs -join ',')) {
    $problems += "构建脚本与 ShaderCompiler.cs 的 (文件, Profile) 不一致:`n        脚本  : $($scriptPairs -join ', ')`n        运行时: $($csPairs -join ', ')"
}
if (($scriptInputs -join ',') -ne ($diskHlsl -join ',')) {
    $problems += "构建脚本清单与 Shaders\*.hlsl 磁盘文件不一致:`n        脚本: $($scriptInputs -join ', ')`n        磁盘: $($diskHlsl -join ', ')"
}
if ($csprojInputs.Count -gt 0 -and (($scriptInputs -join ',') -ne ($csprojInputs -join ','))) {
    $problems += "App.csproj 的 CompileShaders Inputs 与构建脚本清单不一致:`n        csproj: $($csprojInputs -join ', ')`n        脚本  : $($scriptInputs -join ', ')"
}

if ($problems.Count -gt 0) {
    Write-Host "❌ 着色器清单漂移，终止构建:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "   · $p" -ForegroundColor Red }
    Write-Host "   请同步四处: Shaders\CompileShaders.ps1 / Services\ShaderCompiler.cs / App.csproj / Shaders\*.hlsl" -ForegroundColor Yellow
    exit 1
}
Write-Host "   清单自检通过: $($scriptPairs -join ', ')"

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
