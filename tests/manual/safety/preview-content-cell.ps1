# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 预览内容是否真的画出来了 —— 不用眼睛的取证（2026-09-30，用户："你试着通过其他办法分析是否正确"）
#
# 三件事分别由三个读数承担：
#  ① 改道有没有生效 → 全屏窗的【窗类】。SDR 档（TTC_FORCE_SDR=1）改前是 WinUIDesktopWin32WindowClass，
#     改后必须和 HDR 档一样是 TTC_HC（HdrCaptureWindow）。
#  ② 屏幕上是不是"平白一片" → 抓一张全屏图，数【不同颜色种数】与【纯白占比】。
#     维护者眼里的"全白"在这里必须显形，否则这条判据就是空的。
#  ③ 抓法本身有没有失明 → 正对照：两臂都拿同一把尺子量。若某臂读成全黑/全空，那是仪器瞎
#     （直接合成内容对 GDI 抓取可以不可见，本仓 §25.6 就被这事骗过一次），不许读成"产品黑屏"。
#
# 明确不能由这格回答的：亮度绝对值。GDI 屏幕抓取在 HDR 输出上会做 HDR→SDR 转换（并钳位），
# 所以"白点抬到 120nits 那件事"这里量不出来 —— 那一半由 T1 的 18 条 + 审计卡 `呈现帧契约` 那行负责。
#
# 只用键盘：开预览靠产品自己注册的全局热键（先从它日志里读出实际生效的那一档，不猜），
# 收尾按 Esc、只杀本次起的 PID、设置按原字节还原并复算 sha256。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    # 二次截图（2026-10-01 §39）：C 臂曾经读到 92.97% 纯白而日志里没有任何失败痕迹，
    # 要分清"画面真是白的"与"WinUI 首帧还没合成、拍到了页面底色"，只能在同一臂里隔几秒再拍一次。
    # 默认 2 秒：判决以二拍为准（见下面循环里的说明），0 = 退回只看首拍的旧行为。
    [int]$SecondShotSec = 2,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\preview-content-cell.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [System.IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA256) } else { 'ABSENT' } }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$outDir = 'C:\PLAN\TrueToneCap\tests\output\preview-content'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 预览内容取证 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$sha0 = Sha $set; $bytes0 = [IO.File]::ReadAllBytes($set)
Say @("轮前设置 sha256 = $sha0")

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WK
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
}
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
# 尺子从共用库来（判据的对照跑的是同一份实现，不是两份各写一遍）
. 'C:\PLAN\TrueToneCap\tests\manual\safety\preview-stats-lib.ps1'
$auto = [System.Windows.Automation.AutomationElement]
function Top-Full([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    foreach ($w in @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        try { $r = $w.Current.BoundingRectangle; if ($r.Width -gt 3000 -and $r.Height -gt 1500) { return $w } } catch { }
    }
    return $null
}
function Screen-Shot([string]$file) {
    $b = New-Object System.Drawing.Bitmap(3840, 2160)
    try {
        $g = [System.Drawing.Graphics]::FromImage($b)
        try { $g.CopyFromScreen(0, 0, 0, 0, $b.Size) } finally { $g.Dispose() }
        $b.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $b.Dispose() }
}
function Bitmap-Stats([string]$file) { return (Get-BitmapStats -File $file) }   # 尺子在 preview-stats-lib.ps1
function Read-Chord([int]$mark) {
    # 不猜键：从产品自己那句「已注册: …」里读实际生效的那一档（本机 §22 实测过默认键被占 ⇒ 回退到 Ctrl+Alt+S）
    if (-not (Test-Path -LiteralPath $appLog)) { return $null }
    $lines = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $mark |
        Where-Object { $_ -match '已注册: .*(S|Q)\b' })
    $hit = $lines | Where-Object { $_ -match '捕获|S' } | Select-Object -First 1
    if ($null -eq $hit) { return $null }
    if ($hit -match '已注册:\s*(\S+)') { return $Matches[1] }
    return $null
}
function Read-Witness([int]$mark, [string[]]$keys) {
    # 只回读本臂新增的那段日志（按行号切片），把产品自报的分支行原样交出来。
    # 为什么要有这一条：窗类只能说明"哪扇窗在最上面"，说明不了"画面是谁画的"。
    if (-not (Test-Path -LiteralPath $appLog)) { return @() }
    $re = ($keys | ForEach-Object { [regex]::Escape($_) }) -join '|'
    @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $mark |
        Where-Object { $_ -match $re } | Select-Object -First 12)
}
function Send-CaptureHotkey([string]$chord) {
    switch ($chord) {
        'Ctrl+Shift+S' { [WK]::Chord(0x11, 0x10, 0x53); return 'Ctrl+Shift+S' }
        'Ctrl+Alt+S'   { [WK]::Chord(0x11, 0x12, 0x53); return 'Ctrl+Alt+S' }
        default { return $null }
    }
}

$results = @()
foreach ($arm in @('A-ForceSdr', 'B-HdrControl', 'C-LegacyOverlay')) {
    Say @("--- 臂 $arm ---")
    $proc = $null; $row = [ordered]@{ Arm = $arm; Class = ''; Shot = ''; Kinds = -1; White = -1; Black = -1; Mean = ''; Note = '' }
    try {
        $mark = 0; if (Test-Path -LiteralPath $appLog) { $mark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
        if ($arm -eq 'A-ForceSdr') { $env:TTC_FORCE_SDR = '1'; Say @('   已设 TTC_FORCE_SDR=1') }
        # C 臂 = 兜底那条路的可达入口（2026-10-01 新加的诊断开关）。
        # RC 故意只看 A/B：C 是【取证】不是【门禁】，它红不该把这格判失败——兜底路本来就不是正路。
        if ($arm -eq 'C-LegacyOverlay') { $env:TTC_FORCE_LEGACY_OVERLAY = '1'; Say @('   已设 TTC_FORCE_LEGACY_OVERLAY=1（强制走兜底覆盖层）') }
        $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
        Say @("   PID=$($proc.Id)，等 $SettleSec s")
        Start-Sleep -Seconds $SettleSec

        $chord = Read-Chord $mark
        if (-not $chord) { $chord = 'Ctrl+Shift+S'; Say @("   ★ 日志里没读到「已注册」那一行，按默认 $chord 试（这一条要标未证）") }
        else { Say @("   日志自报实际注册键 = $chord") }
        $sent = Send-CaptureHotkey $chord
        if (-not $sent) { Say @("   ★ 认不出的键串「$chord」⇒ 本臂未判"); throw 'bad-chord' }
        Say @("   已送出 $sent")

        $w = $null; $dl = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $dl) { $w = Top-Full $proc.Id; if ($w) { break }; Start-Sleep -Milliseconds 400 }
        if (-not $w) { Say @('   ★ 15s 内没有全屏窗 ⇒ 预览没打开，本臂未判'); throw 'no-window' }
        $cls = $w.Current.ClassName; $r = $w.Current.BoundingRectangle
        $row['Class'] = $cls
        Say @("   全屏窗 <$cls> $($r.Width)x$($r.Height) @ $($r.X),$($r.Y)")

        # 见证行：这一臂到底走到了哪条分支，只认产品自己打的字，不靠窗类反推
        foreach ($ln in @(Read-Witness $mark @('诊断开关', '预览窗失败', '覆盖层就绪', 'GPU 背景', 'GPU 呈现', '背景渲染', '预览帧来源'))) {
            Say @("     │ $ln")
        }

        $file = Join-Path $outDir "$arm.png"
        Screen-Shot $file
        $st = Bitmap-Stats $file
        $row['Shot'] = "$arm.png"; $row['Kinds'] = $st.Kinds; $row['White'] = $st.WhitePct; $row['Black'] = $st.BlackPct; $row['Mean'] = $st.Mean
        Say @("   截图 $($st.W)x$($st.H) sha256=$((Sha $file).Substring(0,16)) 不同颜色 $($st.Kinds) 种  纯白占 $($st.WhitePct)%  全黑占 $($st.BlackPct)%  均值 $($st.Mean)")

        # 判读三条阈值（平白/无结构/仪器失明）都在 preview-stats-lib.ps1，与对照跑同一份
        $row['Note'] = Get-StatsVerdict -st $st
        Say @("   首拍 ⇒ $($row['Note'])")

        if ($SecondShotSec -gt 0) {
            # 同一扇窗、同一会话，再等 SecondShotSec 秒重拍一次，并【以二拍为准】判决。
            # 由来（2026-10-01 §39）：C 臂有一次首拍读到 92.97% 纯白、均值 R244——而覆盖层那层
            # #80000000 半黑遮罩会把白压到 ~R127，所以那张"白"根本不是画上去的桌面，
            # 是 HWND 已可枚举、WinUI 首帧还没合成的空档。首拍留着当"首帧状态"的见证，
            # 判据只认稳定后的二拍；两拍都平白才是这一臂真的没有桌面像素。
            Start-Sleep -Seconds $SecondShotSec
            $file2 = Join-Path $outDir "$arm-s2.png"
            Screen-Shot $file2
            $st2 = Bitmap-Stats $file2
            $v2 = Get-StatsVerdict -st $st2
            Say @("   二拍（+$SecondShotSec s，以此为准） $($st2.W)x$($st2.H) 不同颜色 $($st2.Kinds) 种 " +
                  "纯白占 $($st2.WhitePct)% 全黑占 $($st2.BlackPct)% 均值 $($st2.Mean) ⇒ $v2")
            $row['Kinds'] = $st2.Kinds; $row['White'] = $st2.WhitePct
            $row['Black'] = $st2.BlackPct; $row['Mean'] = $st2.Mean; $row['Shot'] = "$arm-s2.png"
            $row['Note'] = "$v2｜首拍 $($st.Kinds)种/$($st.WhitePct)%"
        } else {
            $row['Note'] = Get-StatsVerdict -st $st
        }
        Say @("   ⇒ $($row['Note'])")
    } catch {
        $row['Note'] = "未判：$($_.Exception.Message)"; Say @("   中断 $($row['Note'])")
    } finally {
        Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue
        Remove-Item Env:TTC_FORCE_LEGACY_OVERLAY -ErrorAction SilentlyContinue
        try { [WK]::Tap(0x1B) } catch { }
        Start-Sleep -Milliseconds 800
        if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @('   已停止 PID=' + $proc.Id) } catch { Say @('   ★ 停止失败') } }
        if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    }
    $results += [pscustomobject]$row
    Start-Sleep -Seconds 2
}

Say @('--- 对照与判决 ---')
$a = $results | Where-Object { $_.Arm -eq 'A-ForceSdr' }; $b = $results | Where-Object { $_.Arm -eq 'B-HdrControl' }
$okClass = ($a.Class -eq 'TTC_HC' -and $b.Class -eq 'TTC_HC')
Say @("① 窗类：A=<$($a.Class)> B=<$($b.Class)> ⇒ 两臂同一张界面 $(if ($okClass) {'✓'} else {'★ 不同 ⇒ 改道没生效'})")
$okBoth = ($a.Note -like 'OK*' -and $b.Note -like 'OK*')
# ⚠ 判别句先进变量再插值：写成 "$(if (A) { '好' } { '坏' })" 少一个 else，两支文本都会进串（同一条坑我今日第三次犯）
if ($okBoth) { $markBoth = '✓' } else { $markBoth = '★ 至少一臂没有画面（先分辨是仪器还是产品）' }
Say @("② 内容：A[$($a.Kinds) 种/白 $($a.White)%] B[$($b.Kinds) 种/白 $($b.White)%] ⇒ 两臂都抓到画面 $markBoth")
$c = $results | Where-Object { $_.Arm -eq 'C-LegacyOverlay' }
if ($c) {
    if ($c.Note -like 'OK*') { $markC = '画得出画面' } else { $markC = '★ 无画面或未判（判词见上一行）' }
    Say @("③ 兜底那侧（C 臂 = 强制走旧覆盖层）：窗类 <$($c.Class)> [$($c.Kinds) 种/白 $($c.White)%/黑 $($c.Black)%]，" +
          "正路 B 臂 [$($b.Kinds) 种/白 $($b.White)%] ⇒ $markC")
    Say @("   本格不对 B/C 两档下判决：它只把『兜底到底画不画得出东西』从推断变成读数，删不删仍是产品取舍。")
}
$rc = if ($okClass -and $okBoth) { 0 } else { 1 }
$same = (Sha $set) -eq $sha0
if ($same) { $markSet = '与轮前逐字相同 ✓' } else { $markSet = '★ 与轮前不同，立即核对' }
Say @("设置还原自检：$(Sha $set) ⇒ $markSet")
Say @("RC=$rc  （亮度绝对值不在本格的覆盖面内：GDI 抓取在 HDR 输出上会做 HDR→SDR 转换并钳位）")
Write-Output "RC=$rc A=<$($a.Class)>/$($a.Kinds)种/$($a.White)% B=<$($b.Class)>/$($b.Kinds)种/$($b.White)%"
exit $rc
