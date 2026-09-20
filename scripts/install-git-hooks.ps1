# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap —— 安装 git 钩子（把 scripts/verify.ps1 挂到 commit / push 上）
#
# 背景（P0-1）:
#   项目此前没有任何自动拦截：Publish.ps1 里的测试门禁【只在人主动发布时】才跑，
#   日常的 commit / push 完全没有校验。本脚本用 core.hooksPath 把钩子挂上，
#   不需要 CI 服务器，也不往 .git/hooks 里拷贝文件（钩子本体入库、可 review、可同步）。
#
# 为什么用 core.hooksPath 而不是往 .git/hooks 里复制:
#   · .git/hooks 不入库 —— 换机器 / 重新克隆后钩子就没了，且没人知道曾经装过
#   · 钩子本体放在 scripts/git-hooks/ 入库，改动能被 review、能随仓库同步
#
# 分工:
#   pre-commit  只构建（约 27s）      挡"编译不过"
#   pre-push    构建 + 7 套件（约 110s）挡"测不过"
#
# 用法:
#   .\scripts\install-git-hooks.ps1              # 安装
#   .\scripts\install-git-hooks.ps1 -Status      # 只看当前状态，不改任何东西
#   .\scripts\install-git-hooks.ps1 -Uninstall   # 摘掉（恢复 git 默认钩子目录）
#
# 绕过（紧急时）:  git commit --no-verify / git push --no-verify

param(
    [switch]$Uninstall,
    [switch]$Status
)

$ErrorActionPreference = "Stop"

$RepoRoot  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$HooksRel  = "scripts/git-hooks"
$HooksDir  = Join-Path $RepoRoot "scripts\git-hooks"

function Get-GitExe {
    $g = Get-Command git -ErrorAction SilentlyContinue
    if (-not $g) { return $null }
    return $g.Source
}

$git = Get-GitExe
if (-not $git) {
    Write-Host "❌ 未找到 git，无法安装钩子。" -ForegroundColor Red
    exit 1
}

Push-Location $RepoRoot
try {
    # ── 状态查询 ──
    $current = (& $git config --get core.hooksPath 2>$null)
    $effective = (& $git rev-parse --git-path hooks 2>$null)

    if ($Status) {
        Write-Host "core.hooksPath = $(if ($current) { $current } else { '(未设置 → 使用 .git/hooks)' })"
        Write-Host "实际生效目录   = $effective"
        Write-Host ""
        foreach ($h in @("pre-commit", "pre-push")) {
            $p = Join-Path $effective $h
            $ok = Test-Path $p
            Write-Host ("  {0} {1,-12} {2}" -f $(if ($ok) { "✅" } else { "❌" }), $h, $(if ($ok) { $p } else { "未找到" }))
        }
        exit 0
    }

    # ── 卸载 ──
    if ($Uninstall) {
        if ($current) {
            & $git config --unset core.hooksPath
            Write-Host "✅ 已移除 core.hooksPath（恢复为默认 .git/hooks）" -ForegroundColor Green
        } else {
            Write-Host "ℹ️  core.hooksPath 本就未设置，无需卸载"
        }
        exit 0
    }

    # ── 安装 ──
    if (-not (Test-Path $HooksDir)) {
        Write-Host "❌ 钩子目录不存在: $HooksDir" -ForegroundColor Red
        exit 1
    }
    $hookFiles = @(Get-ChildItem $HooksDir -File -ErrorAction SilentlyContinue)
    if ($hookFiles.Count -eq 0) {
        Write-Host "❌ 钩子目录为空: $HooksDir" -ForegroundColor Red
        exit 1
    }

    # 若用户此前在 .git/hooks 里放过自定义钩子，切换 hooksPath 会让它们静默失效 —— 必须提示。
    $oldHooks = @()
    $defaultHooksDir = Join-Path $RepoRoot ".git\hooks"
    if (Test-Path $defaultHooksDir) {
        $oldHooks = @(Get-ChildItem $defaultHooksDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike "*.sample" })
    }
    if ($oldHooks.Count -gt 0) {
        Write-Host "⚠️  检测到 .git/hooks 下有 $($oldHooks.Count) 个非示例钩子：" -ForegroundColor Yellow
        foreach ($h in $oldHooks) { Write-Host "     · $($h.Name)" -ForegroundColor DarkGray }
        Write-Host "   设置 core.hooksPath 后这些将【不再执行】。请确认它们已被 scripts/git-hooks/ 覆盖，" -ForegroundColor Yellow
        Write-Host "   否则请手工合并后再继续。" -ForegroundColor Yellow
        $ans = Read-Host "   继续安装? (y/N)"
        if ($ans -notmatch '^[yY]') {
            Write-Host "   已取消，未做任何改动。"
            exit 0
        }
    }

    & $git config core.hooksPath $HooksRel
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ git config 失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }

    # 钩子必须带可执行位，否则 git 会跳过（POSIX 环境；Windows 下 git 靠 shebang 执行，
    # 但同一份仓库在 Linux/macOS 或 WSL 里克隆时没有 x 位就会静默失效）。
    foreach ($f in $hookFiles) {
        $rel = "scripts/git-hooks/$($f.Name)"
        & $git update-index --chmod=+x $rel 2>$null | Out-Null
    }

    $now = (& $git config --get core.hooksPath)
    $eff = (& $git rev-parse --git-path hooks)
    Write-Host ""
    Write-Host "✅ 已挂载钩子目录: $now" -ForegroundColor Green
    Write-Host "   实际生效      : $eff"
    Write-Host ""
    foreach ($f in $hookFiles) {
        Write-Host "   · $($f.Name)"
    }
    Write-Host ""
    Write-Host "验证方式（不产生任何改动）:" -ForegroundColor Cyan
    Write-Host "   TTC_HOOK_DRYRUN=1 sh scripts/git-hooks/pre-commit"
    Write-Host "   TTC_HOOK_DRYRUN=1 sh scripts/git-hooks/pre-push"
    Write-Host "   .\scripts\install-git-hooks.ps1 -Status"
} finally {
    Pop-Location
}
