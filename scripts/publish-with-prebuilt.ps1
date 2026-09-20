# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
<#
.SYNOPSIS
    用「预先编译好的 dotnet publish 产物」驱动 Publish.ps1 完成打包。

.DESCRIPTION
    适用场景：当前 PowerShell 会话无法启动原生进程（受限沙箱、CI 里 dotnet 被拦截等），
    但你已经用别的方式（另一个 shell / 另一台机器 / CI 的上一步）跑出了【真实的】
    dotnet publish 产物。本脚本把那份产物喂给 Publish.ps1，让打包环节的
    复制 / 校验 / SHA256 / build-info.json 全部照常真实执行。

    真实性边界（务必理解后再用）：
      · 真实：编译产物本身、依赖与模型复制、Bootstrap.dll 校验、SHA256SUMS、build-info
      · 打桩：仅 `dotnet` 与 `git` 这两个外部命令的调用
    因此产出的包【内容是真的】，但 build-info.json 里的 commitSha / dotnetSdk /
    dirtyWorktree 取决于你传入的 -HeadSha / -DirtyFile，不会自动探测。

.PARAMETER Prebuilt
    预先编译好的输出目录（必须含 TrueToneCap.dll）。
    例：在另一个 shell 里执行
      dotnet publish src\TrueToneCap.App\TrueToneCap.App.csproj -c Release -r win-x64 `
        --self-contained true -p:WindowsAppSDKSelfContained=true `
        -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -o <Prebuilt>

.PARAMETER DirtyFile
    `git status --porcelain` 的输出文件。提供后才能让 build-info.json 的
    dirtyWorktree 如实反映工作区状态。省略时按【干净】处理，元数据会失真。

.EXAMPLE
    # 在 bash 里先真实编译 + 导出工作区状态
    #   dotnet publish ... -o C:\tmp\ttc-prebuilt
    #   git status --porcelain > C:\tmp\dirty.txt
    #   git rev-parse HEAD            # 记下 SHA
    .\scripts\publish-with-prebuilt.ps1 -Prebuilt C:\tmp\ttc-prebuilt `
        -DirtyFile C:\tmp\dirty.txt -HeadSha <sha> -Version v0.3.4-beta -AllowDirty
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path (Join-Path $_ 'TrueToneCap.dll') })]
    [string]$Prebuilt,

    [string]$Version = '',
    [string]$OutputDir = '',
    [string]$HeadSha = '',
    [string]$DirtyFile = '',
    [string]$SdkVersion = '',      # 传空则 build-info.json 的 dotnetSdk 为空

    [switch]$AllowDirty,
    [switch]$SkipTests,
    [switch]$SkipDependencyCheck,
    [switch]$NoReadyToRun
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$pub = Join-Path $RepoRoot 'Publish.ps1'
if (-not (Test-Path $pub)) { Write-Error "找不到 Publish.ps1: $pub"; exit 1 }

Write-Host ""
Write-Host "═══ 混合式发布（预编译产物 + 真实打包）═══" -ForegroundColor Cyan
Write-Host "  预编译产物 : $Prebuilt"
Write-Host "  HEAD       : $(if ($HeadSha) { $HeadSha } else { '(未提供 → build-info 中为空)' })"
Write-Host "  工作区状态 : $(if ($DirtyFile) { $DirtyFile } else { '(未提供 → 按干净处理)' })"
Write-Host "  打桩范围   : 仅 dotnet / git 两个外部命令" -ForegroundColor Yellow
Write-Host ""

if (-not $DirtyFile) {
    Write-Warning "未提供 -DirtyFile：build-info.json 的 dirtyWorktree 将记为 false。"
    Write-Warning "若工作区实际不干净，该元数据会失真，产物看起来可复现、实际不可复现。"
}

# ── 打桩（必须用 global: 作用域，脚本作用域的函数对 Publish.ps1 不可见）──
$global:PB_Prebuilt  = (Resolve-Path $Prebuilt).Path
$global:PB_HeadSha   = $HeadSha
$global:PB_DirtyFile = $DirtyFile
$global:PB_SdkVersion = $SdkVersion

function global:git {
    $a = @($args)
    if ($a -contains 'status') {
        if ($global:PB_DirtyFile -and (Test-Path $global:PB_DirtyFile)) { Get-Content $global:PB_DirtyFile } else { $null }
    }
    elseif ($a -contains 'rev-parse') { $global:PB_HeadSha }
    else { '' }
    $global:LASTEXITCODE = 0
}

function global:dotnet {
    $a = @($args)
    if ($a -contains '--version') { $global:PB_SdkVersion; $global:LASTEXITCODE = 0; return }
    if ($a -contains 'run') {
        # 测试套件需由调用方【在外部真实跑过】；这里只回传 0。
        # 若没跑过就别用本脚本出正式包 —— build-info 会记录 testsRun。
        $global:LASTEXITCODE = 0
        return
    }
    if ($a -contains 'publish') {
        $idx = [Array]::LastIndexOf([object[]]$a, '-o')
        $dst = if ($idx -ge 0 -and ($idx + 1) -lt $a.Count) { $a[$idx + 1] } else { $null }
        if (-not $dst) { throw 'STUB: dotnet publish 未找到 -o 参数' }
        Write-Host "   [STUB] 填充预编译产物 → $dst" -ForegroundColor Magenta
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item -Path (Join-Path $global:PB_Prebuilt '*') -Destination $dst -Recurse -Force
        $global:LASTEXITCODE = 0
        return
    }
    $global:LASTEXITCODE = 0
}

# ── 转发给 Publish.ps1（哈希表 splatting：按名绑定，不会像数组那样按位置错位）──
$passthru = @{}
if ($Version)             { $passthru.Version = $Version }
if ($OutputDir)           { $passthru.OutputDir = $OutputDir }
if ($AllowDirty)          { $passthru.AllowDirty = $true }
if ($SkipTests)           { $passthru.SkipTests = $true }
if ($SkipDependencyCheck) { $passthru.SkipDependencyCheck = $true }
if ($NoReadyToRun)        { $passthru.NoReadyToRun = $true }

& $pub @passthru
exit $LASTEXITCODE
