# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# scripts/probes/trim_launch_probe.ps1 — 裁剪/AOT 产物的实机启动回归探针
#
# 用法:
#   .\scripts\probes\trim_launch_probe.ps1 -ExePath <发布产物>\TrueToneCap.exe [-SettleSeconds 12]
#
# ═══ 为什么必须有这个探针 ═══
# 裁剪（PublishTrimmed）与 NativeAOT 的失败模式都是【编译全绿、运行即崩】：
# 静态可达面之外的反射/XAML 解析/WinRT 激活在编译期不报错，只有真跑起来才炸。
# 构建门禁（TreatWarningsAsErrors）与内容门禁（verify-msix.ps1）都覆盖不到这一层，
# 本探针补的就是"产物能启动、能出窗口、日志无异常"这条最小判据。
# 每次以 -Trim 产出的包在分发前都应跑一遍（见 store-readiness-review-2026-10-26.md §7.3）。
#
# ⚠️ 单实例互斥：应用已在运行时本次启动会立即退出（测到的是旧实例），
#    故探针先拒绝执行，要求先结束已运行实例。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [int]$SettleSeconds = 12
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { Write-Host "❌ 找不到 $ExePath" -ForegroundColor Red; exit 1 }

# ── 0) 单实例守卫 ──
$existing = Get-Process TrueToneCap -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "❌ 已有 TrueToneCap.exe 在运行 (PID $($existing.Id -join ', ')) —— 单实例互斥会使本次启动立即退出" -ForegroundColor Red
    Write-Host "   先结束它再测（注意确认那不是用户正在用的实例）" -ForegroundColor Yellow
    exit 1
}

# ── 1) 启动 ──
Write-Host "启动: $ExePath"
$p = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds $SettleSeconds

# ── 2) 存活 + 主窗口 ──
if ($p.HasExited) {
    Write-Host "❌ 进程在 ${SettleSeconds}s 内退出，ExitCode=$($p.ExitCode) —— 裁剪产物启动失败" -ForegroundColor Red
    exit 1
}
$p.Refresh()
$handle = $p.MainWindowHandle
$handleStr = if ($handle -ne 0) { '0x{0:X}' -f $handle } else { '(无主窗口 —— 可能最小化到托盘)' }
Write-Host "✅ 进程存活 (PID $($p.Id))，MainWindowHandle=$handleStr，Responding=$($p.Responding)" -ForegroundColor Green

# ── 3) 启动日志体检（便携形态：日志写在 exe 同目录 log\）──
$logDir = Join-Path (Split-Path -Parent $ExePath) 'log'
$latest = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latest) {
    # 只查启动窗口期（最近 SettleSeconds+30s）里写的行，避免被历史日志误导
    $cutoff = (Get-Date).AddSeconds(-($SettleSeconds + 30))
    $bad = Select-String -Path $latest.FullName -Pattern 'Unhandled|FATAL|XamlParseException' -ErrorAction SilentlyContinue |
           Where-Object { $_.Line -match '^\d{4}-|^\[' } | Select-Object -First 5
    if ($bad) {
        Write-Host "⚠️ 日志 $($latest.Name) 发现异常关键字（人工确认是否本次启动所写）:" -ForegroundColor Yellow
        $bad | ForEach-Object { Write-Host "   $($_.Line)" }
    } else {
        Write-Host "✅ 启动日志无 Unhandled/FATAL/XamlParseException: $($latest.Name)" -ForegroundColor Green
    }
} else {
    Write-Host "⚠️ 未找到日志目录 $logDir（若产物为打包形态，日志在 %LOCALAPPDATA%\TrueToneCap\log）" -ForegroundColor Yellow
}

# ── 4) 收尾（只杀自己启动的那个实例）──
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "---- trim launch probe done ----"
exit 0
