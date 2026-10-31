# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 启动关键路径 A/B：DirectML(OCR GPU) 会话【按需建】vs【启动即建】。
#
# 起因（2026-10-31 批3）：MultiOcrService 原先在启动期无条件 new 一枚 DirectML 会话，
# 而它挂在主窗口构造尾巴上 ⇒ 每一次开机都替"用户会不会点识字"付一次模型加载。
# 改成默认按需（TTC_OCR_EAGER_GPU=1 回旧行为）之后，收益必须有实机读数，不能只讲道理。
#
# 口径（三条都是刻意的）：
#  · 被测量取产品自己的「启动: 进程到主窗口 Nms」，不取墙钟差 —— 后者会把
#    "本机静置 ≥5s 后第一次创建进程多付 ~200ms" 这段与产品无关的开销算进来。
#  · A/B 交替跑（A B A B …）而不是先跑完 A 再跑完 B：机器在一天里会漂，交替让漂移摊到两臂。
#  · 臂身份必须从日志里读出来（A 有「按需构建」行 / B 有「引擎就绪」行）。读不出来就是
#    环境变量没生效 ⇒ 两臂同一个世界 ⇒ 本 A/B 判【未判】，不许把"两臂相同"写成"改动无收益"。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$Rounds = 3,
    [int]$SettleSec = 15,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-31\startup-ab.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")

$bytes0 = if (Test-Path -LiteralPath $set) { [IO.File]::ReadAllBytes($set) } else { $null }
Say @("=== 启动 A/B · $Rounds 轮交替 · $(Get-Date -Format 'HH:mm:ss') ===")
Say @("exe=$Exe")
Say @("设置轮前 $(if ($bytes0) { "$($bytes0.Length) B" } else { '不存在' })")

function TtMs([string]$line) {
    $m = [regex]::Match($line, '^(\d+):(\d+):(\d+)\.(\d+)')
    if (-not $m.Success) { return $null }
    return [int]$m.Groups[1].Value * 3600000 + [int]$m.Groups[2].Value * 60000 +
           [int]$m.Groups[3].Value * 1000 + [int]$m.Groups[4].Value
}

function Run-One([bool]$Eager, [string]$Arm) {
    $mark = if (Test-Path -LiteralPath $appLog) { @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count } else { 0 }
    if ($Eager) { $env:TTC_OCR_EAGER_GPU = '1' } else { Remove-Item Env:TTC_OCR_EAGER_GPU -ErrorAction SilentlyContinue }
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Start-Sleep -Seconds $SettleSec
    try { Stop-Process -Id $proc.Id -Force } catch { }
    Remove-Item Env:TTC_OCR_EAGER_GPU -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $mark)
    $toWindow = @($slice | Where-Object { $_ -match '启动: 进程到主窗口 (\d+)ms' } | Select-Object -First 1)
    $n = if ($toWindow.Count) { [int][regex]::Match($toWindow[0], '进程到主窗口 (\d+)ms').Groups[1].Value } else { -1 }
    $warm0 = @($slice | Where-Object { $_ -match '着色器预热启动' } | Select-Object -First 1)
    $warm1 = @($slice | Where-Object { $_ -match '着色器预热完成' } | Select-Object -First 1)
    $gap = if ($warm0.Count -and $warm1.Count) { (TtMs $warm1[0]) - (TtMs $warm0[0]) } else { '不可得' }
    $lazy = @($slice | Where-Object { $_ -match 'DirectML GPU 会话按需构建' }).Count
    $built = @($slice | Where-Object { $_ -match 'DirectML GPU 引擎就绪|DirectML GPU 引擎不可用|DirectML 初始化异常' }).Count
    $ocrLine = @($slice | Where-Object { $_ -match '\[OCR' } | Select-Object -First 1)
    $idn = if ($Eager) { if ($built -ge 1) { '身份✓(启动即建)' } else { '★身份✘(没看到引擎就绪行)' } }
           else { if ($lazy -ge 1) { '身份✓(按需)' } else { '★身份✘(没看到按需行)' } }
    Say @(("[{0}] 进程到主窗口 = {1} ms；着色器预热启动→完成 = {2} ms；臂身份 {3}" -f $Arm, $(if ($n -ge 0) { $n } else { '★没读到' }), $gap, $idn))
    foreach ($l in @($slice | Where-Object { $_ -match 'OCR|着色器预热' } | Select-Object -First 4)) {
        Say @("      | " + $l.Substring(0, [Math]::Min(120, $l.Length)))
    }
    return $n
}

$a = @(); $b = @()
for ($i = 1; $i -le $Rounds; $i++) {
    $a += Run-One $false "A$i 懒建(默认)"
    $b += Run-One $true   "B$i 启动即建(TTC_OCR_EAGER_GPU=1)"
}
function Med([int[]]$xs) { $s = @($xs | Where-Object { $_ -ge 0 } | Sort-Object); if (-not $s.Count) { return '不可得' }; if ($s.Count % 2) { return $s[[int](($s.Count - 1) / 2)] } else { return [math]::Round(($s[$s.Count/2 - 1] + $s[$s.Count/2]) / 2.0, 1) } }
Say @("")
Say @("A 懒建   各次 = $(($a -join ',')) → 中位 $(Med $a) ms（-1=那一轮没读到读数，不计入中位）")
Say @("B 启动即建 各次 = $(($b -join ',')) → 中位 $(Med $b) ms")
$dirty = $false
if ($bytes0) {
    $now = [IO.File]::ReadAllBytes($set)
    if ($now.Length -ne $bytes0.Length) { $dirty = $true }
    else { for ($i = 0; $i -lt $now.Length; $i++) { if ($now[$i] -ne $bytes0[$i]) { $dirty = $true; break } } }
    if ($dirty) { [IO.File]::WriteAllBytes($set, $bytes0); Say @("★ 设置被这几轮改动 ⇒ 已按原字节还原") }
    else { Say @("设置逐字节未变 ✓") }
}
Write-Output ("A(懒建) 中位 $(Med $a) ms vs B(启动即建) 中位 $(Med $b) ms · 各次 A=$(($a -join ',')) B=$(($b -join ','))")
