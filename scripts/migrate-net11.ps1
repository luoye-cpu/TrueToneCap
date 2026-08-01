# .NET 10 → .NET 11 迁移脚本
# 一键执行阶段一：框架升级 + 基础编译
# 用法: .\scripts\migrate-net11.ps1 [-Apply] [-Revert]
#   -Apply:   执行迁移（默认：预览模式，仅显示将要修改的内容）
#   -Revert:  回滚到 .NET 10

param(
    [switch]$Apply,
    [switch]$Revert
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
$BackupDir = Join-Path $RepoRoot ".net10-backup"

Write-Host "╔══════════════════════════════════════╗"
Write-Host "║  TrueToneCap .NET 10 → 11 迁移脚本 ║"
Write-Host "╚══════════════════════════════════════╝"
Write-Host ""

if ($Revert) {
    # ── 回滚模式 ──
    if (-not (Test-Path $BackupDir)) {
        Write-Host "❌ 未找到备份目录 ($BackupDir)，无法回滚"
        exit 1
    }
    Write-Host "[回滚] 从备份恢复 .csproj 文件..."
    $csprojFiles = @(
        "src\TrueToneCap.App\TrueToneCap.App.csproj",
        "src\TrueToneCap.Core\TrueToneCap.Core.csproj",
        "src\TrueToneCap.Test\TrueToneCap.Test.csproj",
        "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"
    )
    foreach ($relative in $csprojFiles) {
        $src = Join-Path $BackupDir $relative
        $dst = Join-Path $RepoRoot $relative
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
            Write-Host "   ✅ 已恢复: $relative"
        }
    }
    Write-Host "[回滚] 完成！请运行 dotnet restore 恢复 NuGet 包"
    exit 0
}

# ── 迁移模式 ──
Write-Host "[1/4] 备份当前 .csproj 文件..."
$csprojFiles = @(
    "src\TrueToneCap.App\TrueToneCap.App.csproj",
    "src\TrueToneCap.Core\TrueToneCap.Core.csproj",
    "src\TrueToneCap.Test\TrueToneCap.Test.csproj",
    "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"
)
foreach ($relative in $csprojFiles) {
    $src = Join-Path $RepoRoot $relative
    $dst = Join-Path $BackupDir $relative
    $dstDir = Split-Path $dst -Parent
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    Copy-Item $src $dst -Force
    Write-Host "   ✅ 已备份: $relative"
}

# ── 定义替换表 ──
$replacements = @(
    # TargetFramework
    @{ File = "src\TrueToneCap.App\TrueToneCap.App.csproj"; Old = "net10.0-windows10.0.26100.0"; New = "net11.0-windows10.0.26100.0" }
    @{ File = "src\TrueToneCap.Core\TrueToneCap.Core.csproj"; Old = "net10.0-windows10.0.26100.0"; New = "net11.0-windows10.0.26100.0" }
    @{ File = "src\TrueToneCap.Test\TrueToneCap.Test.csproj"; Old = "net10.0-windows10.0.26100.0"; New = "net11.0-windows10.0.26100.0" }
    @{ File = "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"; Old = "net10.0-windows10.0.26100.0"; New = "net11.0-windows10.0.26100.0" }
    # LangVersion
    @{ File = "src\TrueToneCap.App\TrueToneCap.App.csproj"; Old = "<LangVersion>13</LangVersion>"; New = "<LangVersion>14</LangVersion>" }
    @{ File = "src\TrueToneCap.Core\TrueToneCap.Core.csproj"; Old = "<LangVersion>13</LangVersion>"; New = "<LangVersion>14</LangVersion>" }
    @{ File = "src\TrueToneCap.Test\TrueToneCap.Test.csproj"; Old = "<LangVersion>13</LangVersion>"; New = "<LangVersion>14</LangVersion>" }
    @{ File = "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"; Old = "<LangVersion>13</LangVersion>"; New = "<LangVersion>14</LangVersion>" }
    # NuGet 版本更新
    @{ File = "src\TrueToneCap.App\TrueToneCap.App.csproj"; Old = 'Version="10.0.0"'; New = 'Version="11.0.0"' }  # Microsoft.Extensions.DependencyInjection
    @{ File = "src\TrueToneCap.App\TrueToneCap.App.csproj"; Old = 'Version="10.0.10"'; New = 'Version="11.0.0"' } # System.Drawing.Common
    @{ File = "src\TrueToneCap.Test\TrueToneCap.Test.csproj"; Old = 'Version="10.0.10"'; New = 'Version="11.0.0"' } # System.Drawing.Common
)

if (-not $Apply) {
    # ── 预览模式 ──
    Write-Host "[2/4] 预览模式 - 将执行以下替换:"
    Write-Host ""
    foreach ($r in $replacements) {
        $file = Join-Path $RepoRoot $r.File
        if (Select-String -Path $file -Pattern $r.Old -Quiet) {
            Write-Host "   📝 $($r.File)"
            Write-Host "      旧: $($r.Old)"
            Write-Host "      新: $($r.New)"
            Write-Host ""
        } else {
            Write-Host "   ⚠️ $($r.File) — 未找到旧值，可能已更新"
        }
    }
    Write-Host ""
    Write-Host "运行 .\scripts\migrate-net11.ps1 -Apply 来执行迁移"
    Write-Host "运行 .\scripts\migrate-net11.ps1 -Revert 来回滚"
    exit 0
}

# ── 执行模式 ──
Write-Host "[2/4] 执行 .csproj 替换..."
foreach ($r in $replacements) {
    $file = Join-Path $RepoRoot $r.File
    $content = Get-Content $file -Raw
    if ($content -match [regex]::Escape($r.Old)) {
        $content = $content -replace [regex]::Escape($r.Old), $r.New
        Set-Content $file -Value $content -NoNewline
        Write-Host "   ✅ $($r.File): $($r.Old) → $($r.New)"
    } else {
        Write-Host "   ⚠️ $($r.File): 未找到 `"$($r.Old)`"，跳过"
    }
}

Write-Host ""
Write-Host "[3/4] 恢复 NuGet 包..."
Push-Location $RepoRoot
dotnet restore
if ($LASTEXITCODE -ne 0) { Write-Warning "   ⚠️ dotnet restore 失败，请检查 NuGet 源" }
Pop-Location

Write-Host ""
Write-Host "[4/4] 编译验证..."
Push-Location $RepoRoot
dotnet build TrueToneCap.slnx -c Debug
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "✅ 编译成功！请运行测试验证:"
    Write-Host "   dotnet run --project src\TrueToneCap.Test -- --unit-tests"
    Write-Host ""
    Write-Host "📋 后续步骤:"
    Write-Host "   1. 检查 docs/dotnet11-migration-plan.md 查看阶段二~四"
    Write-Host "   2. 更新 Publish.ps1 中的版本号"
    Write-Host "   3. 更新 PACKAGE.md 中的版本号"
} else {
    Write-Host ""
    Write-Host "❌ 编译失败，请检查错误"
    Write-Host "   运行 .\scripts\migrate-net11.ps1 -Revert 来回滚"
}
Pop-Location