# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# F4/F5 补仪表格（2026-10-31 批 4 的"先补仪表"那一步）
#
# 这一格量的是【出货前就存在的那台 exe】里今天还能读到的东西：
#   F4 预览窗静置：按一次热键让全屏选区窗静置 IdleSec 秒，期间【不注入任何输入、不动窗口、
#                不截屏】——只采两样：产品自己写的渲染帧号行（诊断档才落盘）+ 本进程 CPU 时间增量。
#                为什么不用逐帧像素复读当仪表：那要往被测区里写东西，测的就不是静置了（§4 纪律）。
#   F5 捕获与会话：--frame-handoff-probe 自带"生产路径选区回读连跑 N 次"的计数，
#                它就是 staging 尺寸来回切的那条路的分母；staging 新建【次数】与 _poolLock
#                持锁【时长】这台 exe 里没有任何计数器 ⇒ 只登记"未量"，不许从日志反推成读数。
#
# 纪律：设置按字节备份 + 复算 sha256，收尾原样还原；只杀本格自己起的 PID；
#       日志档位行必须在报告里自证（"日志详细度 = 诊断"）；跑完把三件事的判据打在报告里。
#
# 用法（pwsh 7；MSYS bash 下 taskkill 的 /F 会被吃掉，所以杀进程在本脚本内部用 PowerShell 做）：
#   pwsh -NoProfile -File tests/manual/safety/f45-meters-cell.ps1 -IdleSec 45
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$IdleSec = 45,
    [int]$BootSec = 14,
    [switch]$Select,
    [switch]$SkipProbe,
    [string]$Report = 'C:\PLAN\TrueToneCap\tests\output\f45-meters-cell.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Report, (($ls -join "`r`n") + "`r`n"), $utf8) }
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA256) } else { 'ABSENT' } }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ('log\app_' + (Get-Date).ToString('yyyy-MM-dd') + '.log')
$bkDir = 'C:\PLAN\TrueToneCap\artifacts\perfmeters_2026-10-31'
$bk = Join-Path $bkDir 'TrueToneCap.settings.json.cell-backup'
$outDir = 'C:\PLAN\TrueToneCap\artifacts\perfmeters_2026-10-31\out'
New-Item -ItemType Directory -Force -Path $bkDir, $outDir, (Split-Path -Parent $Report) | Out-Null
[System.IO.File]::WriteAllText($Report, '', $utf8)
Say @("=== F4/F5 补仪表格 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · exe=$Exe ===")
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 没有同目录设置文件'); exit 2 }

# ── ① 设置按字节备份（先备份，再动任何东西）──
$bytes0 = [IO.File]::ReadAllBytes($set)
[IO.File]::WriteAllBytes($bk, $bytes0)
$sha0 = Sha $set; $shaBk = Sha $bk
Say @("备份前 sha256(现件)=$sha0"); Say @("备份     sha256($bk)=$shaBk")
if ($sha0 -ne $shaBk) { Say @('RC=2 备份与原文件哈希不一致 ⇒ 停手'); exit 2 }

# ── ② 把这一轮推到【诊断】档 + 输出目录改到仓库内（本机没有 D 盘）──
# 诊断档是必须的：Debug 行（渲染帧号、渲染循环总帧数、staging/回读结算）在简档根本不落盘。
$raw = [IO.File]::ReadAllText($set)
$inj = '"LogVerbosity":"Diagnose","OutputPath":' + (ConvertTo-Json $outDir -Compress)
if ($raw -match '"LogVerbosity"') { Say @('RC=2 设置里已有 LogVerbosity 键 ⇒ 不覆盖别人的值，停手'); exit 2 }
$open = $raw.LastIndexOf('}')
$new = $raw.Substring(0, $open) + ',' + $inj + '}'
[IO.File]::WriteAllText($set, $new, $utf8)
Say @("已注入 $inj（收尾按字节还原）")
try { $chkVerb = (Get-Content -LiteralPath $set -Raw -Encoding UTF8 | ConvertFrom-Json).LogVerbosity } catch { $chkVerb = '解析失败' }
Say @("读回核对：LogVerbosity=$chkVerb")

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class F45
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()   { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
# 只认类名 TTC_HC（预览窗本体）——尺寸阈值不可靠：主窗 1260x840 逻辑尺寸在 4K/高缩放下也能过"大窗"筛，
# 把它当成预览窗就会把"静置"测到主窗上。找不到时把该 PID 的窗都列出来，报告里看得见为什么未判。
function Find-Preview([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    $all = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    foreach ($w in $all) {
        try { if ($w.Current.ClassName -eq 'TTC_HC') { return $w } } catch { }
    }
    return $null
}
function List-Windows([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    foreach ($w in @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        try {
            $r = $w.Current.BoundingRectangle
            $fmt = { param($v) if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { '不可得(∞/NaN)' } else { "$v" } }
            Say @("  窗 <$($w.Current.ClassName)> 尺寸=$(& $fmt $r.Width)x$(& $fmt $r.Height) 名=$($w.Current.Name)")
        } catch { Say @("  窗属性读取失败：$($_.Exception.Message)") }
    }
}
function Tail([int]$mark) { @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $mark) }

$proc = $null
try {
    # ── ③ 静置这一臂：起进程 → 一次热键 → 什么都不做 IdleSec 秒 → Esc ──
    $logMark = 0; if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("① 已起 PID=$($proc.Id)，等 ${BootSec}s 到主窗口")
    Start-Sleep -Seconds $BootSec
    $t = Tail $logMark
    $verbLine = @($t | Where-Object { $_ -match '日志详细度' } | Select-Object -First 1)
    Say @("档位自证：$(if ($verbLine) { $verbLine[0] } else { '★ 日志里没有详细度行 ⇒ 这一轮的档不明' })")
    $cl = @($t | Where-Object { $_ -match '已注册: Ctrl\S*\+S\b' } | Select-Object -First 1)
    $chord = if ($cl) { ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($cl[0]).Groups[1].Value } else { 'Ctrl+Shift+S' }
    Say @("热键 $chord（$(if ($cl) { '从日志现读' } else { '默认值，日志未证实' })）⇒ 只按这一次，之后不注入任何输入")
    if ($chord -eq 'Ctrl+Shift+S') { [F45]::Chord(0x11, 0x10, 0x53) } else { [F45]::Chord(0x11, 0x12, 0x53) }
    $w = $null; $dl = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl) { $w = Find-Preview $proc.Id; if ($w) { break }; Start-Sleep -Milliseconds 400 }
    if (-not $w) { Say @('★ 没等到类名 TTC_HC 的预览窗 ⇒ 静置这一臂未判'); List-Windows $proc.Id; throw 'no-window' }
    $cls = $w.Current.ClassName
    Say @("全屏窗类名 <$cls>（TTC_HC = 预览窗本体）")

    $idleWall = 0; $cpuDeltaMs = 0; $nFrames = 0; $frameFloor = 0
    if ($Select) {
        # ── 选区这一臂：拖框 → 等"选区完成"见证行 → Enter 保存 → 等编码完成行 ──
        # 起手前等 4s + 走中间点（preview-copy-leg-cell 那一格的实测教训：一步到位会被当成单击/被首帧吃掉）
        Start-Sleep -Seconds 4
        $null = [F45]::SetCursorPos(1000, 700); Start-Sleep -Milliseconds 400
        [F45]::LeftDown(); Start-Sleep -Milliseconds 300
        foreach ($pt in @(@(1350,900), @(1700,1100), @(2050,1300), @(2400,1500))) {
            $null = [F45]::SetCursorPos($pt[0], $pt[1]); Start-Sleep -Milliseconds 220
        }
        [F45]::LeftUp(); Start-Sleep -Milliseconds 1200
        Say @('② 已拖框 (1000,700)→(2400,1500)')
        $selWitness = $null; $dl2 = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $dl2 -and -not $selWitness) {
            $hit = @(Tail $logMark | Where-Object { $_ -match '选区完成' })
            if ($hit) { $selWitness = $hit; break }
            Start-Sleep -Milliseconds 400
        }
        if ($selWitness) { Say @("选区完成见证：$($selWitness[0])") } else { Say @('★ 没有"选区完成"见证行 ⇒ 这一臂未判（不猜选区尺寸）') }
        $p0 = Get-Process -Id $proc.Id; $wall0 = Get-Date
        [F45]::Tap(0x0D)   # Enter = 完成/保存
        Say @('② 已发 Enter（保存）')
        $doneLine = $null; $dl3 = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $dl3 -and -not $doneLine) {
            $hit = @(Tail $logMark | Where-Object { $_ -match '编码完成|保存到剪贴板|已保存' })
            if ($hit) { $doneLine = $hit[0]; break }
            Start-Sleep -Milliseconds 500
        }
        $p1 = Get-Process -Id $proc.Id; $p1.Refresh(); $p0.Refresh()
        $cpuDeltaMs = ($p1.TotalProcessorTime - $p0.TotalProcessorTime).TotalMilliseconds
        $idleWall = [math]::Round(((Get-Date) - $wall0).TotalSeconds, 2)
        Say @("保存腿：墙钟 ${idleWall}s，进程 CPU 增量 $([math]::Round($cpuDeltaMs,1))ms；完成行 $(if ($doneLine) { $doneLine } else { '★ 90s 内没有 ⇒ 未判' })")
    } else {
        # 静置开始：只采 CPU 时间增量与墙钟；期间一次输入都不发
        $p0 = Get-Process -Id $proc.Id
        $cpu0 = $p0.TotalProcessorTime; $wall0 = Get-Date
        Say @("② 静置 ${IdleSec}s 起（$($wall0.ToString('HH:mm:ss.fff'))），期间零输入、零截屏")
        $sampled = 0
        for ($i = 0; $i -lt $IdleSec * 4; $i++) { Start-Sleep -Milliseconds 250; $sampled++ }
        $wall1 = Get-Date
        $p1 = Get-Process -Id $proc.Id
        $p1.Refresh(); $p0.Refresh()   # 性能计数器不 Refresh 会读到同一份快照（13:26 那一臂读到 0ms 就是这个原因）
        $cpuDeltaMs = ($p1.TotalProcessorTime - $cpu0).TotalMilliseconds
        $idleWall = [math]::Round(($wall1 - $wall0).TotalSeconds, 2)
        Say @("② 静置结束：墙钟 ${idleWall}s，进程 CPU 增量 $([math]::Round($cpuDeltaMs, 1))ms（采样 $sampled 拍，只读不写）")
    }
    [F45]::Tap(0x1B)   # Esc：让预览窗自己走关闭路径（结算行/渲染循环退出行都在这条路上）
    Say @('③ 已发一发 Esc（唯一一次其它注入），等 3s')
    Start-Sleep -Seconds 3
    $idleMark = $logMark
    $proc.Refresh()
    if (-not $proc.HasExited) { $null = taskkill /F /PID $proc.Id; Say @("已 taskkill /F /PID $($proc.Id)（本格自己起的进程）") }
    Start-Sleep -Milliseconds 800

    # ── ④ 从日志里取这台 exe 已有的仪表行 ──
    $t = Tail $idleMark
    $frameLines = @($t | Where-Object { $_ -match '渲染帧 #\d+' })
    $exitLine = @($t | Where-Object { $_ -match '渲染循环退出' })
    $detect = @($t | Where-Object { $_ -match '窗口检测完成' })
    $sessions = @($t | Where-Object { $_ -match '会话启动: |空闲超时|所有会话已' })
    $initLines = @($t | Where-Object { $_ -match '窗口初始化 |窗口构造|首帧 UploadFrame|首帧合成 ' })
    $winClose = @($t | Where-Object { $_ -match '窗口关闭 ' })
    $sdrWindow = @($t | Where-Object { $_ -match 'SDR 回读窗口|整帧回读|兜底|选区回读' })
    $saveLines = @($t | Where-Object { $_ -match '选区完成|开始编码|编码完成|HDR 编码启动|编码设置|选区截图启动|WGC 捕获完成|复制' })
    Say @(('── ' + $(if ($Select) { '选区/保存这一臂' } else { '静置这一臂' }) + '的仪表行（原样，含时间戳）──'))
    foreach ($l in @($frameLines + $exitLine + $detect + $sessions + $initLines + $winClose + $sdrWindow + $saveLines)) { Say @("  $l") }
    if ($Select) {
        $m = [regex]'选区完成[^0-9]*(\d+)\D+(\d+)'
        $s0 = @($saveLines | Where-Object { $_ -match '选区完成' } | Select-Object -First 1)
        if ($s0 -and $m.IsMatch([string]$s0[0])) {
            Say @("读数：选区尺寸 $($m.Match([string]$s0[0]).Groups[1].Value)x$($m.Match([string]$s0[0]).Groups[2].Value)（见证行原文见上）")
        }
        Say @("读数：保存腿墙钟 ${idleWall}s、进程 CPU 增量 $([math]::Round($cpuDeltaMs,1))ms（【估算·口径=整进程】，编码子进程不计在内）")
    }
    $nFrames = 0
    if ($exitLine) { $m = [regex]'总帧数=(\d+)'; if ($m.IsMatch($exitLine[0])) { $nFrames = [int]$m.Match($exitLine[0]).Groups[1].Value } }
    # 退出行缺席（进程被杀在渲染线程写行之前）⇒ 用最后一条帧号行作"至少这么多帧"的下界
    $frameFloor = 0
    if ($frameLines) { $m2 = [regex]'渲染帧 #(\d+)'; $s = [string]$frameLines[-1]; if ($m2.IsMatch($s)) { $frameFloor = [int]$m2.Match($s).Groups[1].Value } }
    if ($nFrames -eq 0 -and $frameFloor -gt 0) { $nFrames = $frameFloor; Say @("读数：渲染循环退出行缺席 ⇒ 帧数取最后一条帧号行的下界 $frameFloor") }
    if (-not $Select) {
        Say @("读数：静置 ${idleWall}s 内渲染帧数=$nFrames ⇒ 平摊 $([math]::Round($nFrames / [math]::Max($idleWall, 0.001), 3)) 帧/s（每 3s 醒一次的话应≈$([math]::Round($idleWall / 3, 1)) 帧）")
        $wakes = [math]::Max([int][math]::Round($idleWall / 3), 1)
        Say @("读数：同一区间进程 CPU 增量 $([math]::Round($cpuDeltaMs, 1))ms ÷ $wakes 次静置节拍 ⇒ 每次 ≈$([math]::Round($cpuDeltaMs / $wakes, 2))ms（【估算·口径=整进程】含 WGC 回调线程与 UI 线程，不等于 RenderCore 单帧）")
    }
    if ($frameLines.Count -ge 2) {
        $rx = [regex]'(\d\d:\d\d:\d\d\.\d\d\d).*渲染帧 #(\d+)'
        $a = $rx.Match([string]$frameLines[0]); $b = $rx.Match([string]$frameLines[-1])
        if ($a.Success -and $b.Success) {
            $na = [int]$a.Groups[2].Value; $nb = [int]$b.Groups[2].Value
            $dt = ([datetime]('2000-01-01 ' + $b.Groups[1].Value)) - ([datetime]('2000-01-01 ' + $a.Groups[1].Value))
            Say @("行距判据：#$na @ $($a.Groups[1].Value) → #$nb @ $($b.Groups[1].Value)，跨 $($nb - $na) 帧用 $([math]::Round($dt.TotalMilliseconds,0))ms ⇒ 每帧平摊 $([math]::Round($dt.TotalMilliseconds / [math]::Max(($nb - $na), 1), 0))ms（这台 exe 每 10 帧才写一行，此列是【区间平摊】）")
        }
    } else {
        Say @("★ 帧号行不足 2 条 ⇒ 区间平摊未量")
    }

    # ── ⑤ F5 那一臂：现有仪器能给出的"生产路径选区回读"次数 ──
    # ⚠ FrameHandoffProbe 的 Say 只写 Console + 镜像进 app 日志，【不】自己落读数文件；
    #   早期版本这里按文件名 glob 过 artifacts\frame_handoff*，会把 10-30 那批的旧文件读成"本轮读数"
    #   ——那是假证据。现在：① 探针 stdout 重定向到本轮专属文件；② 只认"晚于本轮开始"的旧命名文件。
    if (-not $SkipProbe) {
        $pm = 0; if (Test-Path -LiteralPath $appLog) { $pm = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
        $runStart = Get-Date
        $probeStdout = Join-Path $bkDir ("frame_handoff_probe_stdout_" + (Get-Date).ToString('yyyy-MM-dd_HHmmss') + ".log")
        $pp = Start-Process -FilePath $Exe -ArgumentList '--frame-handoff-probe' -PassThru -WorkingDirectory $dir `
            -RedirectStandardOutput $probeStdout -RedirectStandardError ($probeStdout + '.err')
        Say @("④ 已起 --frame-handoff-probe PID=$($pp.Id)，stdout→$probeStdout")
        if (-not $pp.WaitForExit(180000)) { $null = taskkill /F /PID $pp.Id; Say @('★ 探针 180s 未退出 ⇒ 已按 PID 杀掉') }
        $prc = if ($pp.HasExited) { $pp.ExitCode } else { '未退' }
        Say @("④ 探针退出码=$prc（0=全条臂判完）")
        $pt = Tail $pm
        foreach ($l in @($pt | Where-Object { $_ -match '臂|选区回读|整帧回读|共享槽' } | Select-Object -Last 24)) { Say @("  [日志] $l") }
        if (Test-Path -LiteralPath $probeStdout) {
            foreach ($l in @(Get-Content -LiteralPath $probeStdout -Encoding UTF8 | Where-Object { $_ -match '臂⑤|臂⑥|臂⑧|臂⑨|臂⑩' })) { Say @("  [stdout] $l") }
        }
        $art = @(Get-ChildItem -LiteralPath 'C:\PLAN\TrueToneCap\artifacts' -Filter 'frame_handoff*' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt $runStart } | Sort-Object LastWriteTime | Select-Object -Last 1)
        if ($art) { Say @("探针本轮读数文件：$($art[0].FullName)") } else { Say @('（本轮没有新的 frame_handoff* 文件——探针只写 stdout/日志，属预期）') }
    }
    $rc = 0
} catch {
    Say @("★ 异常：$($_.Exception.Message)")
    $rc = 1
} finally {
    if ($proc -and -not $proc.HasExited) { try { $null = taskkill /F /PID $proc.Id } catch { } }
    # ── ⑥ 设置按字节还原 + 复算哈希（两端都打印）──
    [IO.File]::WriteAllBytes($set, $bytes0)
    $shaEnd = Sha $set
    Say @("还原后 sha256=$shaEnd")
    Say @("备份前 sha256=$sha0")
    if ($shaEnd -eq $sha0) { Say @('★ 设置按字节还原一致（PASS）') } else { Say @('★★ 还原后哈希不一致 ⇒ 立刻人工核对') ; $rc = 1 }
}
Say @("RC=$rc")
Write-Host "RC=$rc 报告: $Report"
exit $rc
