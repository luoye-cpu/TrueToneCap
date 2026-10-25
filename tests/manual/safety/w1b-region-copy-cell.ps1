# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# W1b 选区那条腿：Ctrl+C 到底进没进得去（这一格从未判成过一次）
#
# 现场为什么自相矛盾：09-30 23:17 那一趟，夹具已经读到产品自己写的「选区完成 1445x959 @(…)」，
# 却在最后下了"拖拽没选中"的判词 —— 前提见证已经成立，那条分支逻辑上就死了。
# 三个解释并存，这一格用【同一扇窗、同一次选区】把它们分开：
#   A 键根本没进目标窗（前台归属问题：SetForegroundWindow 的返回值不查不记）
#   B 只有带 Ctrl 的组合进不去（Key() 用 GetAsyncKeyState 现读 Ctrl）
#   C 窗已经自己关了（60 s 空闲自关 / 3 min 硬看门狗）
# 判据顺序：先读前台归属（A 的读数），再按 Enter（已知能落地的对照臂，B 若成立而 A 不成立则 Enter 必红），
# 再按 Ctrl+C（待判的那一发），最后才用工具栏"复制"那枚按钮做鼠标正对照（鼠标靠命中测试，不吃焦点）。
# C 由每一轮"这一轮自己的日志切片"里有没有自关行来排。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$WaitSec = 18,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\w1b-region-copy.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA256) } else { 'ABSENT' } }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== W1b 选区复制腿 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$repoOut = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\shots'
New-Item -ItemType Directory -Force -Path $repoOut | Out-Null

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RB
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h, IntPtr r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()   { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Ctrl(byte main)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static string Fg()
    {
        IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256);
        return h + "/pid=" + pid + "/<" + sb.ToString() + ">";
    }
    public static string Describe(IntPtr h)
    {
        if (!IsWindow(h, IntPtr.Zero)) return "句柄已失效";
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256);
        return h + "/pid=" + pid + "/<" + sb.ToString() + "> 可见=" + IsWindowVisible(h) + " dpi=" + GetDpiForWindow(h);
    }
}
'@
if (-not ('RB' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }
$auto = [System.Windows.Automation.AutomationElement]

# 输出目录：不改就是每格必失败（本机设置指向不存在的 D:\TTC，07:11 已实机量到）
$bytes0 = [IO.File]::ReadAllBytes($set); $sha0 = Sha $set
$raw = [IO.File]::ReadAllText($set)
$out0 = $null; try { $out0 = ($raw | ConvertFrom-Json).OutputPath } catch { }
$anchor = '"OutputPath":"' + ($out0 -replace '\\', '\\') + '"'
if (([regex]::Matches($raw, [regex]::Escape($anchor))).Count -ne 1) { Say @('RC=2 OutputPath 锚点不是恰好一处'); exit 2 }
[IO.File]::WriteAllText($set, $raw.Replace($anchor, '"OutputPath":"' + ($repoOut -replace '\\', '\\') + '"'), $utf8)
Say @("★ OutputPath 按字节临时改到 $repoOut（轮前 $out0 不可用；收尾原样还原）")

function LogCount { if (Test-Path -LiteralPath $appLog) { @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count } else { 0 } }
function Slice([int]$from) { @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $from) }
function Top-Full([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    foreach ($w in @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        try { $r = $w.Current.BoundingRectangle; if ($r.Width -gt 3000) { return $w } } catch { }
    }
    return $null
}
# 一次真实拖拽（中间走两步防被当成单击）；坐标只当"框一个居中的大块"用
function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    $null = [RB]::SetCursorPos($x1, $y1); Start-Sleep -Milliseconds 250
    [RB]::LeftDown()
    $null = [RB]::SetCursorPos([int](($x1 + $x2) / 2), [int](($y1 + $y2) / 2)); Start-Sleep -Milliseconds 200
    $null = [RB]::SetCursorPos($x2, $y2); Start-Sleep -Milliseconds 250
    [RB]::LeftUp(); Start-Sleep -Milliseconds 1200
}

$proc = $null
try {
    $m0 = LogCount
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $cl = @(Slice $m0 | Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+S)\b' } | Select-Object -First 1)
    $chord = if ($cl) { ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($cl[0]).Groups[1].Value } else { 'Ctrl+Shift+S' }
    Say @("区域截图热键 = $chord $(if ($cl) { '（日志现读）' })")

    # ── 第 1 轮：Enter（已知能落地的对照臂）──
    Say @("")
    Say @("──── 轮 1：Enter = 保存（对照臂）────")
    $m1 = LogCount
    if ($chord -eq 'Ctrl+Shift+S') { [RB]::Chord(0x11, 0x10, 0x53) } else { [RB]::Chord(0x11, 0x12, 0x53) }
    $w = $null; $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl) { $w = Top-Full $proc.Id; if ($w) { break }; Start-Sleep -Milliseconds 400 }
    if (-not $w) { Say @('★ 没等到全屏窗 ⇒ 未判'); throw 'no-window' }
    $hwnd = [IntPtr]$w.Current.NativeWindowHandle
    $r = $w.Current.BoundingRectangle
    Say @("全屏窗 <$($w.Current.ClassName)> hwnd=$hwnd 外框=$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height) dpi=$([RB]::GetDpiForWindow($hwnd))")
    Say @("拖框前前台窗 = $([RB]::Fg())  ← 目标窗 $hwnd")
    Drag 900 600 2400 1500
    $sel = @(Slice $m1 | Where-Object { $_ -match '选区完成 (\d+)x(\d+) @\(窗口内 (\d+),(\d+)\)' } | Select-Object -First 1)
    if (-not $sel) { Say @('★ 本轮切片里没有「选区完成」⇒ 拖拽确实没选中，本格未判'); throw 'no-selection' }
    $sm = ([regex]'选区完成 (\d+)x(\d+) @\(窗口内 (\d+),(\d+)\)').Match($sel[0])
    $selW = [int]$sm.Groups[1].Value; $selH = [int]$sm.Groups[2].Value
    $selX = [int]$sm.Groups[3].Value; $selY = [int]$sm.Groups[4].Value
    Say @("选区完成（产品自报）= ${selW}x${selH} @($selX,$selY) ⇒ 前提成立")
    Say @("选区后前台窗 = $([RB]::Fg())  目标窗还在吗 = $([RB]::Describe($hwnd))")
    $before = @(Get-ChildItem -LiteralPath $repoOut -File -Filter '*.png' | ForEach-Object { $_.FullName })
    [RB]::Tap(0x0D); Say @('已按 Enter')
    $dl2 = (Get-Date).AddSeconds($WaitSec); $done1 = $null
    while ((Get-Date) -lt $dl2 -and -not $done1) {
        $h = @(Slice $m1 | Where-Object { $_ -match '编码完成|选区保存|自动关闭|看门狗|Cancel' } | Select-Object -Last 1)
        if ($h) { $done1 = $h[0] } else { Start-Sleep -Milliseconds 400 }
    }
    if ($done1) { Say @("轮 1 落地行：$($done1.Substring(0, [Math]::Min(150, $done1.Length)))") }
    else { Say @("★ 轮 1（Enter）$WaitSec s 内无落地行 ⇒ 连对照臂都不动，键盘入口整体不通") }
    $newPng = @(); $dlp = (Get-Date).AddSeconds($WaitSec)
    while ((Get-Date) -lt $dlp -and -not $newPng.Count) {
        $newPng = @(Get-ChildItem -LiteralPath $repoOut -File -Filter '*.png' | Where-Object { $before -notcontains $_.FullName })
        if (-not $newPng.Count) { Start-Sleep -Milliseconds 400 }
    }
    Say @("轮 1 新产物 = $(if ($newPng.Count) { Split-Path -Leaf $newPng[0].FullName } else { '★ 无（' + $WaitSec + ' s 内没落盘）' })")

    # ── 第 2 轮：Ctrl+C（待判的那一发）──
    # 两轮之间必须等上一轮真正收尾：上一张还在编码时再按热键会被挡掉
    # （07:23 那一趟就是这么"第 2 轮没等到全屏窗"的，不是产品坏了）。
    Say @("")
    Say @("等轮 1 收尾…")
    $fin = $null; $dlf = (Get-Date).AddSeconds($WaitSec)
    while ((Get-Date) -lt $dlf -and -not $fin) {
        $hf = @(Slice $m1 | Where-Object { $_ -match '编码完成|保存失败|截图失败|已取消|取消截图|窗口关闭' } | Select-Object -Last 1)
        if ($hf) { $fin = $hf[0] } else { Start-Sleep -Milliseconds 400 }
    }
    Say @("轮 1 收尾行 = $(if ($fin) { $fin.Substring(0, [Math]::Min(150, $fin.Length)) } else { '★ 没有收尾行' })")
    Start-Sleep -Seconds 2
    Say @("──── 轮 2：Ctrl+C = 只复制────")
    $m2 = LogCount
    if ($chord -eq 'Ctrl+Shift+S') { [RB]::Chord(0x11, 0x10, 0x53) } else { [RB]::Chord(0x11, 0x12, 0x53) }
    $w2 = $null; $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl) { $w2 = Top-Full $proc.Id; if ($w2) { break }; Start-Sleep -Milliseconds 400 }
    if (-not $w2) { Say @('★ 第 2 轮没等到全屏窗'); throw 'no-window-2' }
    $hwnd2 = [IntPtr]$w2.Current.NativeWindowHandle
    Drag 800 500 2200 1400
    $sel2 = @(Slice $m2 | Where-Object { $_ -match '选区完成' } | Select-Object -First 1)
    Say @("轮 2 选区见证 = $(if ($sel2) { $sel2[0].Substring(0, [Math]::Min(120, $sel2[0].Length)) } else { '★ 没有' })")
    Say @("按 Ctrl+C 前：前台窗 = $([RB]::Fg())  目标窗 = $([RB]::Describe($hwnd2))  本进程是否前台 = $((([RB]::Fg()) -match [string]$hwnd2))")
    $cbImg0 = $null; try { $cbImg0 = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
    [RB]::Ctrl(0x43); Say @('已按 Ctrl+C')
    $dl3 = (Get-Date).AddSeconds($WaitSec); $done2 = $null
    while ((Get-Date) -lt $dl3 -and -not $done2) {
        $h = @(Slice $m2 | Where-Object { $_ -match '编码完成|已复制|复制到剪贴板失败|位图通道不可用|自动关闭|看门狗|Cancel' } | Select-Object -Last 1)
        if ($h) { $done2 = $h[0] } else { Start-Sleep -Milliseconds 400 }
    }
    $own2 = @(Slice $m2 | Where-Object { $_ -match '复制|剪贴板|编码|关闭|看门狗|降级|保底' })
    Say @("轮 2 产品自述 $($own2.Count) 条：")
    foreach ($l in ($own2 | Select-Object -Last 6)) { Say @("  | $($l.Substring(0, [Math]::Min(150, $l.Length)))") }
    $cbImg = $null; $cbFiles = @()
    try { $cbImg = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
    try { $cbFiles = @([System.Windows.Forms.Clipboard]::GetFileDropList()) } catch { }
    Say @("轮 2 剪贴板：位图 $(if ($cbImg) { "$($cbImg.Width)x$($cbImg.Height)" } else { '无' }) 文件 $($cbFiles.Count) 个")
    $verdict2 = if (-not $done2) { 'no-witness' } elseif ($cbImg) { 'copy-ok(位图在)' } else { 'copy-file-only' }
    Say @("轮 2 判词 = $verdict2")

    # ── 第 3 轮（只在轮 2 没动静时才跑）：鼠标点工具栏「复制」当正对照 ──
    if ($verdict2 -eq 'no-witness') {
        Say @("")
        Say @("──── 轮 3：鼠标点工具栏「复制」（区分『键没进去』与『复制链路坏了』）────")
        $w3 = $null; $dl = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $dl) { $w3 = Top-Full $proc.Id; if ($w3) { break }; Start-Sleep -Milliseconds 400 }
        if (-not $w3) { Say @('★ 第 3 轮没有全屏窗（可能已被 60 s 自关收掉）⇒ C 成立') }
        else {
            $dpi = [RB]::GetDpiForWindow([IntPtr]$w3.Current.NativeWindowHandle)
            if ($dpi -ne 96) { Say @("★ 目标窗 dpi=$dpi（非 96）⇒ 工具栏坐标要按设备像素换算，本格不猜，记未判") }
            else {
                # 与产品同一套布局常数（GetToolbarLayout，_uiScale=1）：btnW=90 gap=4 padX=6 padY=5 btnH=32，n=6
                $btnW = 90; $gap = 4; $padX = 6; $padY = 5; $btnH = 32; $n = 6
                $totalW = $n * $btnW + ($n - 1) * $gap + $padX * 2
                $totalH = $btnH + $padY * 2
                $winW = [int]$w3.Current.BoundingRectangle.Width; $winH = [int]$w3.Current.BoundingRectangle.Height
                $tx = [Math]::Min([Math]::Max($selX + [int]($selW / 2) - [int]($totalW / 2), 4), $winW - $totalW - 4)
                $ty = $selY + $selH + 10
                if ($ty + $totalH + 10 -gt $winH) { $ty = $selY - $totalH - 12 }
                if ($ty -lt 0) { $ty = 4 }
                $cx = $tx + $padX + 2 * ($btnW + $gap) + [int]($btnW / 2)   # 第 3 枚 = 复制
                $cy = $ty + $padY + [int]($btnH / 2)
                $m3 = LogCount
                Say @("按 selX=$selX selY=$selY W=$selW H=$selH 推出 复制 按钮中心 = ($cx,$cy)（工具栏左上 ($tx,$ty)，$totalW x $totalH）")
                $null = [RB]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 300
                [RB]::LeftDown(); Start-Sleep -Milliseconds 60; [RB]::LeftUp()
                Say @('已点下')
                $dl4 = (Get-Date).AddSeconds($WaitSec); $done3 = $null
                while ((Get-Date) -lt $dl4 -and -not $done3) {
                    $h = @(Slice $m3 | Where-Object { $_ -match '选区复制到剪贴板|编码完成|位图通道' } | Select-Object -Last 1)
                    if ($h) { $done3 = $h[0] } else { Start-Sleep -Milliseconds 400 }
                }
                Say @("轮 3 落地行 = $(if ($done3) { $done3.Substring(0, [Math]::Min(150, $done3.Length)) } else { '★ 也没有 ⇒ 不是键盘入口的问题，鼠标也进不去' })")
                $cb3 = $null; try { $cb3 = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
                Say @("轮 3 剪贴板位图 = $(if ($cb3) { "$($cb3.Width)x$($cb3.Height)" } else { '无' })")
                if ($done3) { Say @('判词：复制链路活着，坏的是【键盘入口】（A 或 B）') }
            }
        }
    }
} catch {
    Say @("★ 中断：$($_.Exception.Message)")
} finally {
    try { [RB]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 600
    if ($proc) { try { Stop-Process -Id $proc.Id -Force; Say @("已停止本次起的 PID=$($proc.Id)") } catch { } }
    if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    Say @("设置还原自检：$(Sha $set) ⇒ $(if ((Sha $set) -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' })")
    try { if ($cbImg0) { [System.Windows.Forms.Clipboard]::SetImage($cbImg0) } } catch { Say @('★ 剪贴板还原失败（如实记）') }
}
exit 0
