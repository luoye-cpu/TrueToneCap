# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap 发布包压缩
#
# 背景（P0-3）:
#   Publish.ps1 只产出目录，不产出分发用的压缩包 —— 压缩一直是人肉步骤，后果有二：
#     1) README 里写的下载文件名（TrueToneCap-v0.3.4-beta-win-x64.zip）与实际产物
#        （…-x64.7z）对不上，文件名靠人记；
#     2) 压缩包在 SHA256SUMS.txt 生成之后才出现，因此**不在校验和覆盖范围内** ——
#        用户下载到的那个文件，恰恰是唯一无法用仓库里的哈希验证的文件。
#
# 本脚本做的事：
#   · 按固定规则命名: TrueToneCap-v<版本>-win-x64.{7z|zip}（版本取自包内 build-info.json）
#   · 7z 可用时优先用 7z（体积显著更小），否则退化为内置 Compress-Archive (zip)
#   · 把压缩包自身的 SHA256 追加进包内 SHA256SUMS.txt，并另存同名 .sha256 文件
#     —— 让"用户下载的那个文件"重新回到可验证范围内
#
# 用法:
#   .\scripts\pack.ps1 -PackageDir publish\TrueToneCap-v0.3.4-beta
#   .\scripts\pack.ps1 -PackageDir <dir> -OutputPath D:\dist\t.7z
#   .\scripts\pack.ps1 -PackageDir <dir> -ForceZip     # 即使有 7z 也用 zip
#
# 退出码: 0 = 成功；1 = 失败。

param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir,

    # 输出压缩包路径；留空则与 PackageDir 同级、按固定规则命名
    [string]$OutputPath = "",

    # 强制使用内置 zip（不调用 7z）
    [switch]$ForceZip = $false,

    # 不把压缩包哈希追加进包内 SHA256SUMS.txt
    [switch]$NoChecksum = $false
)

$ErrorActionPreference = "Stop"

# 双宿主兼容助手（Get-Sha256Hex）—— 为什么不直接用 Get-FileHash，见该文件头注释。
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "ps-compat.ps1")

if (-not (Test-Path $PackageDir)) {
    Write-Host "❌ 发布目录不存在: $PackageDir" -ForegroundColor Red
    exit 1
}
$PackageDir = (Resolve-Path $PackageDir).Path

# ══════════════════════════════════════════════════════════════
#  [1/4] 解析版本（优先取包内 build-info.json —— 那是产物自述的版本）
# ══════════════════════════════════════════════════════════════
Write-Host "[1/4] 解析版本..."

$version = ""
$buildInfoPath = Join-Path $PackageDir "build-info.json"
if (Test-Path $buildInfoPath) {
    try {
        $bi = Get-Content $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($bi.version) { $version = [string]$bi.version }
    } catch {
        Write-Warning "   ⚠️ build-info.json 解析失败: $($_.Exception.Message)"
    }
}
if (-not $version) {
    # 兜底：从目录名 TrueToneCap-v0.3.4-beta / TrueToneCap-0.3.4-beta 中提取
    $leaf = Split-Path $PackageDir -Leaf
    if ($leaf -match 'TrueToneCap-+(v?\d+\.\d+[0-9A-Za-z\.\-+]*)') { $version = $Matches[1] }
}
if (-not $version) {
    Write-Host "❌ 无法确定版本号：包内无 build-info.json，目录名也不含版本号。" -ForegroundColor Red
    Write-Host "   请通过 -OutputPath 显式指定输出路径。" -ForegroundColor Yellow
    exit 1
}
# 目录名/文件名统一带 v 前缀（历史命名习惯）
$versionTag = if ($version -match '^v') { $version } else { "v$version" }
Write-Host "   ✅ 版本: $versionTag"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [2/4] 确定输出路径与压缩方式
# ══════════════════════════════════════════════════════════════
$sevenZip = $null
if (-not $ForceZip) {
    $cmd = Get-Command "7z.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        $sevenZip = $cmd.Source
    } else {
        # 7-Zip 常见安装位置（不硬编码版本号，两个 Program Files 都试）
        $candidates = @()
        foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, ${env:LOCALAPPDATA})) {
            if (-not $root) { continue }
            $candidates += Join-Path $root "7-Zip\7z.exe"
        }
        $sevenZip = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
}

$defaultName = if ($sevenZip) { "TrueToneCap-$versionTag-win-x64.7z" } else { "TrueToneCap-$versionTag-win-x64.zip" }
if (-not $OutputPath) {
    $parent = Split-Path $PackageDir -Parent
    $OutputPath = Join-Path $parent $defaultName
}
$ext = [System.IO.Path]::GetExtension($OutputPath).ToLowerInvariant()

Write-Host "[2/4] 压缩方式..."
if ($sevenZip) {
    Write-Host "   ✅ 使用 7-Zip: $sevenZip"
} else {
    Write-Host "   ℹ️  未找到 7-Zip，使用内置 Compress-Archive (zip) —— 体积会明显更大"
    Write-Host "      安装 7-Zip 后重跑可自动切换到 .7z（输出名会随之改变）"
}
Write-Host "   📄 输出: $OutputPath"
Write-Host ""

if (Test-Path $OutputPath) {
    Write-Host "   🧹 删除已存在的同名压缩包"
    Remove-Item -Force $OutputPath
}

# ══════════════════════════════════════════════════════════════
#  [3/4] 压缩
# ══════════════════════════════════════════════════════════════
Write-Host "[3/4] 压缩中..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()

try {
    if ($sevenZip) {
        # a = 添加；-t7z/-tzip 由扩展名决定；-mx=9 极限压缩；-bso0/-bsp0 静默
        $type = if ($ext -eq '.zip') { 'zip' } else { '7z' }
        & $sevenZip a "-t$type" "-mx=9" "-bso0" "-bsp0" $OutputPath (Join-Path $PackageDir '*') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z 退出码 $LASTEXITCODE" }
    } else {
        # Compress-Archive 需要把内容列出来；用 -Path 指向目录内所有项
        Compress-Archive -Path (Join-Path $PackageDir '*') -DestinationPath $OutputPath -CompressionLevel Optimal -Force
    }
} catch {
    Write-Host "❌ 压缩失败: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$sw.Stop()
if (-not (Test-Path $OutputPath)) {
    Write-Host "❌ 压缩完成但找不到输出文件: $OutputPath" -ForegroundColor Red
    exit 1
}
$arcSize = (Get-Item $OutputPath).Length
Write-Host "   ✅ $([math]::Round($arcSize / 1MB, 1)) MB （$([math]::Round($sw.Elapsed.TotalSeconds, 1))s）"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [4/4] 把压缩包自身纳入校验和
# ══════════════════════════════════════════════════════════════
Write-Host "[4/4] 校验和..."
$hash = (Get-Sha256Hex $OutputPath).ToLowerInvariant()
$arcName = Split-Path $OutputPath -Leaf
Write-Host "   SHA256: $hash"

if (-not $NoChecksum) {
    # 1) 追加进包内 SHA256SUMS.txt：让"用户下载的那个文件"回到可验证范围
    #    （包内其它条目是相对路径，压缩包位于包外，故这里只记文件名）
    #    ⚠️ 读写格式必须与 Publish.ps1 [6/6] 完全一致：UTF-8 无 BOM + LF + 正斜杠 + 两个空格。
    #    原先这里用 Set-Content -Encoding UTF8 回写，会把 Publish.ps1 刚产出的无 BOM/LF 文件
    #    重新写成 BOM+CRLF —— 追加一步就把通用工具可校验性又弄丢了。故改用 .NET 显式写盘。
    $sumsPath = Join-Path $PackageDir "SHA256SUMS.txt"
    if (Test-Path $sumsPath) {
        # Get-Content 对无 BOM 的 UTF-8 同样正确解码；-join "`n" 保证回写仍是纯 LF
        $existing = @(Get-Content $sumsPath -Encoding UTF8 | Where-Object { $_.Trim() })
        # 避免重复追加（重跑时先剔除同名旧条目）
        $kept = @($existing | Where-Object { $_ -notmatch [regex]::Escape($arcName) })
        $kept += "$hash  $arcName"
        $sumsText = ($kept -join "`n") + "`n"
        [System.IO.File]::WriteAllText(
            [string]$sumsPath, [string]$sumsText,
            (New-Object System.Text.UTF8Encoding $false))
        Write-Host "   ✅ 已追加进 $sumsPath"
    } else {
        Write-Warning "   ⚠️ 包内无 SHA256SUMS.txt（发布未完成？），跳过追加"
    }

    # 2) 另存独立 .sha256：便于单独分发时携带
    #    同样按 coreutils 惯例写（无 BOM + LF），否则 `sha256sum -c x.7z.sha256` 仍会因 BOM 失败。
    [System.IO.File]::WriteAllText(
        [string]"$OutputPath.sha256",
        [string]"$hash  $arcName`n",
        (New-Object System.Text.UTF8Encoding $false))
    Write-Host "   ✅ 已生成 $arcName.sha256"
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  打包完成                                        ║"
Write-Host "║  $arcName"
Write-Host "║  $([math]::Round($arcSize / 1MB, 1)) MB"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""
Write-Host "验证方式（下载侧）:"
Write-Host "  certutil -hashfile `"$arcName`" SHA256"
Write-Host "  Get-FileHash `"$arcName`" -Algorithm SHA256"
# SHA256SUMS.txt / .sha256 现为 UTF-8 无 BOM + LF + 正斜杠，通用工具可直接整包校验
Write-Host "  sha256sum -c $arcName.sha256        # Linux / macOS / Git-Bash"
Write-Host ""
exit 0
