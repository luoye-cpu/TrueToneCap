# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# scripts/probes/sdr-session-whitelevel-probe.ps1
#
# 8-bit 桌面会话白点语义探针的启动壳 —— 判据本体在产品进程里（TrueToneCap.exe --sdr-semantics-probe，
# 见 src/TrueToneCap.App/Services/SdrSemanticsProbe.cs），这里只负责"起它、拿退出码、把落盘读数印出来"。
#
# 为什么要单独一个壳：这条判据只在【真机 + HDR 开着】才有读数，进不了 headless 门禁；
# 而它问的又是"兜底覆盖层为什么比实时桌面亮"这类只能现场问一次的问题。
# 没有壳的话下一次只会重新发明一遍启动姿势（并且大概率会忘记补丁窗要 -RedirectStandardOutput）。
#
# 用法:  pwsh -File scripts/probes/sdr-session-whitelevel-probe.ps1 [-Exe <TrueToneCap.exe>] [-TimeoutSec 120]
# 退出码: 跟探针一致 —— 0 = 判到了（authored / boosted 二选一），1 = 没判到。
#          「判到了 authored」也是 0：它否证的是"字节已含一次抬升"这一支，不代表画面没问题。
#
# ⚠ 会短暂（约 6 秒）在屏幕左上区域弹一块置顶纯色补丁窗（WS_EX_NOACTIVATE，不抢键盘焦点），
#   探针结束即自行销毁。需要真实桌面会话，不能在服务/无头会话里跑。

param(
    [string]$Exe = '',
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Exe) {
    # 候选顺序：最新发布包 → 最新 bin。两者都按"最新"取，写死包名/TFM 会在清理或升级当天作废。
    # ⚠ 排序看的是旁边的 TrueToneCap.dll，不是 exe：apphost（exe）在增量构建里常常不改写，
    #   拿它当"最新"会挑出一个三天前的包 —— 实测就是这样把老包当新包跑了 120 秒直到超时。
    $cands = @(
        Get-ChildItem (Join-Path $RepoRoot 'publish\TrueToneCap-v*\TrueToneCap.exe') -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $RepoRoot 'src\TrueToneCap.App\bin\x64\*\*\win-x64\TrueToneCap.exe') -ErrorAction SilentlyContinue
    ) | Where-Object { $_ }
    $stampOf = {
        param($e)
        $dll = Join-Path $e.DirectoryName 'TrueToneCap.dll'
        if (Test-Path $dll) { (Get-Item $dll).LastWriteTime } else { $e.LastWriteTime }
    }
    $cands = @($cands | Sort-Object { & $stampOf $_ } -Descending)
    if ($cands.Count -eq 0) {
        Write-Host "找不到 TrueToneCap.exe（先 dotnet build src/TrueToneCap.App，或用 -Exe 指定）"
        exit 1
    }
    $Exe = $cands[0].FullName
}
if (-not (Test-Path $Exe)) { Write-Host "路径不存在: $Exe"; exit 1 }

$exeItem = Get-Item $Exe
$sideBySideDll = Join-Path $exeItem.DirectoryName 'TrueToneCap.dll'

# ── 选了没有这一档的 exe，后果不是"报错"而是"静默当成正常启动跑满超时"（本轮实测踩过）⇒ 先验旗标在不在 ──
if (Test-Path $sideBySideDll) {
    # 字面量在 .NET 元数据里存的是 UTF-16（#US 堆），按 ASCII 搜【必然搜不到】—— 第一版就是这么误判了一次
    # "构建过旧"。按奇偶两种对齐各转一次 Unicode 再搜，才是这条自检该有的样子。
    $bytes = [System.IO.File]::ReadAllBytes($sideBySideDll)
    $needle = '--sdr-semantics-probe'
    $textA = [System.Text.Encoding]::Unicode.GetString($bytes)
    $textB = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    if (-not ($textA.Contains($needle) -or $textB.Contains($needle))) {
        Write-Host "❌ $sideBySideDll 里没有 $needle 这一档（构建过旧）⇒ 先 dotnet build src/TrueToneCap.App 再跑"
        exit 1
    }
} else {
    Write-Host "⚠ 没找到 $sideBySideDll ⇒ 无法确认这枚 exe 带不带探针档（继续跑，但读数若为空先怀疑这里）"
}

$outDir = Join-Path $RepoRoot 'tests\output\app-sdr-semantics-probe'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "探针对象: $Exe"
Write-Host "  代码时间 $(& $stampOf $exeItem)（取同目录 TrueToneCap.dll）—— 补丁窗会置顶约 6 秒，不抢焦点"
$artifact = Join-Path $exeItem.DirectoryName 'sdr-semantics-probe.txt'
if (Test-Path $artifact) { Remove-Item $artifact -Force }   # 拿掉旧读数，避免"这次没跑"读成上一次的结论

$code = $null
$proc = Start-Process -FilePath $Exe -ArgumentList @('--sdr-semantics-probe') -PassThru `
    -WorkingDirectory $exeItem.DirectoryName `
    -RedirectStandardOutput (Join-Path $outDir 'stdout.txt') `
    -RedirectStandardError (Join-Path $outDir 'stderr.txt')
$proc.EnableRaisingEvents = $true
if ($proc.WaitForExit($TimeoutSec * 1000)) { $code = $proc.ExitCode }
else {
    Write-Host "超时 ${TimeoutSec}s 未退出 ⇒ 强杀，判为【没判】"
    try { $proc.Kill() } catch { }
}

if (Test-Path $artifact) {
    Write-Host "`n── 落盘读数（$artifact）──"
    Get-Content $artifact | ForEach-Object { Write-Host $_ }
    Copy-Item $artifact (Join-Path $outDir 'sdr-semantics-probe.txt') -Force
} else {
    Write-Host "★ 探针没有写出读数文件 ⇒ 这一格【没判】"
    if (Test-Path (Join-Path $outDir 'stdout.txt')) {
        Write-Host "── stdout ──"
        Get-Content (Join-Path $outDir 'stdout.txt') | ForEach-Object { Write-Host $_ }
    }
}

if ($null -eq $code) { Write-Host "`n❌ 拿不到退出码 ⇒ 未判"; exit 1 }
if ($code -eq 0) { Write-Host "`n✅ 判到了（退出码 0）。结论与含义见上面的 8-bit / Float16 两行。"; exit 0 }
Write-Host "`n❌ 退出码 $code ⇒ 本轮没判到（自检未过，或读数落在两条假设之间的灰带）。不是「兜底层没问题」。"
exit 1
