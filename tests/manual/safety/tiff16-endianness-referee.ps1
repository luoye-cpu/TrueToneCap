# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# tests/manual/safety/tiff16-endianness-referee.ps1
#
# 16-bit TIFF【字节序】外部裁判 cell —— 补上 2026-10-31 性能批（ManagedTiffEncoder 多 strip 化）
# 唯一没关掉的那个缺口：当时端序对照臂"按 LE 读和按 BE 读都得到 Δ=0"，被判 UNVERIFIED。
#
# 为什么会那样（本 cell 实测并钉死）：
#   ① --bench-tiff 的 16-bit 源是 ExpandTo16BE(GenBgra) = 每通道 v*257 ⇒ 每个样本都是 0xNNNN 回文
#      ⇒ 交换字节序在数学上是恒等 ⇒ 那个 fixture 上【任何】裁判都不可能红（不是裁判坏了，是料坏了）；
#   ② Pillow 的 np.asarray 会把 16-bit TIFF/PNG【降到 8-bit RGBA】（dtype uint8）⇒ 拿它比端序
#      等于在比一个已经把低字节扔掉的东西，LE/BE 无从谈起。
#   ⇒ 修法：换一份非回文的产品 16-bit 字节（产品自己的 Float16→16-bit TIFF 腿 --sdr16-bench），
#     并用 tifffile / ffmpeg 这两个真 16-bit 外部解码器裁判。
#
# 判据（全部由 tiff16_endianness_referee.py 打印两侧实际数字）：
#   required-green 变红 或 required-red 变绿 ⇒ 退出码非 0。
#   常驻自检 Y6：把"声明 II 却存大端"的缺陷形状直接喂进两条裁判，必须至少一条咬红；
#   咬不动 = 本 cell 自己就是瞎的 = 判红。
#
# 用法：
#   pwsh -File tests/manual/safety/tiff16-endianness-referee.ps1            # 全跑（含 4K 三档）
#   pwsh -File tests/manual/safety/tiff16-endianness-referee.ps1 -Fast      # 只跑端序臂（秒级）
#   pwsh -File tests/manual/safety/tiff16-endianness-referee.ps1 -Regen     # 强制重生成 fixture
# 结论走退出码：0=通过 / 1=有臂不符合 / 2=前置缺失（缺 exe / 缺 python 依赖）。

param(
    [switch]$Fast,
    [switch]$Regen,
    [string]$Repo = 'C:\PLAN\TrueToneCap',
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.Tools\bin\Release\net11.0-windows10.0.26100.0\TrueToneCap.Tools.exe'
)
$ErrorActionPreference = 'Continue'
$benchDir = Join-Path $Repo 'artifacts\bench'
$probeDir = Join-path $Repo 'artifacts\precision\probe'
$py = Join-Path $PSScriptRoot 'tiff16_endianness_referee.py'

if (-not (Test-Path $py)) { Write-Host "前置缺失: 找不到 $py"; exit 2 }
if (-not (Get-Command py -ErrorAction SilentlyContinue)) { Write-Host '前置缺失: py 不可用'; exit 2 }

function Get-Fixture {
    param([string]$Kind)
    # fixture 只由【已构建的产品代码】产出（bench exe，不是门禁）：缺件或 -Regen 时重生成。
    $need16 = Join-Path $benchDir 'keep_tiff_3840x2160_16bit_Smallest.tif'
    $needY  = Join-Path $probeDir 'sdr16_tiff16.tiff'
    if ($Kind -eq 'bench') { $targets = @($need16, (Join-Path $benchDir 'keep_tiff_3840x2160_16bit_src.bgra'), (Join-Path $benchDir 'keep_tiff_3840x2160_8bit_Smallest.tif'), (Join-Path $benchDir 'keep_tiff_3840x2160_8bit_src.bgra')) ; $sw = '--bench-tiff --keep' }
    else { $targets = @($needY, (Join-Path $probeDir 'sdr16_png10.png')); $sw = '--sdr16-bench' }
    $missing = @($targets | Where-Object { -not (Test-Path $_) })
    if ($Regen -or $missing.Count -gt 0) {
        if (-not (Test-Path $Exe)) { Write-Host "前置缺失: 找不到 $Exe（fixture 需由产品代码产出，不许手搓字节）"; exit 2 }
        Write-Host "重生成 fixture [$sw] -> $($missing -join ', ')"
        & $Exe $sw.Split(' ') 2>&1 | Select-Object -Last 3
        if ($LASTEXITCODE -ne 0) { Write-Host "fixture 生成失败 rc=$LASTEXITCODE"; exit 2 }
    }
}

if (-not $Fast) { Get-Fixture 'bench' }
Get-Fixture 'probe'

$argv = @($py, '--repo', $Repo)
if ($Fast) { $argv += '--fast' }
Write-Host "═══ [tiff16-endianness-referee] py $($argv[1..99] -join ' ') ═══"
& py @argv
$rc = $LASTEXITCODE
Write-Host "═══ cell 退出码 = $rc （0=端序臂有牙齿且产品字节全绿）═══"
exit $rc
