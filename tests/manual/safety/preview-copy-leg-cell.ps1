# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 复制这条腿（SDR 改道后）：选区 → Ctrl+C → 剪贴板里到底有没有那张位图
#
# 为什么单独跑：§22 量过的是"无感截图 → 剪贴板"那一条写者；改道后选区复制走的是
# HDR 窗那一支（MainWindow:2073 `HDR 选区复制到剪贴板`），是**另一个写者**。
# 而且这一步顺带会写文件（`已保存并复制`）⇒ 它和我们上一格撞到的同一件事相关：
# 设置里 OutputPath 指向本机不存在的盘时，连"只复制"也会受影响。这条要量出来，不是猜。
#
# 纪律同前：确认到本 PID 的全屏窗才动手；键位从产品日志现读；等它自报完成行再读剪贴板
# （§22 的教训：不等完成行 = 我读早了，会把 race 读成产品缺陷）；设置按字节还原并复算 sha256；
# 收尾 Esc、只杀本次起的 PID；把剪贴板尽量还原回去（还原不了就如实记，不假装成功）。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$WaitSec = 30,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\preview-copy-leg-cell.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }
. 'C:\PLAN\TrueToneCap\tests\manual\safety\preview-stats-lib.ps1'
Add-Type -AssemblyName System.Windows.Forms

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$repoOut = 'C:\PLAN\TrueToneCap\tests\output\preview-copy'
New-Item -ItemType Directory -Force -Path $repoOut | Out-Null
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 选区复制这条腿 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$rc = 2
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 没有同目录设置文件'); exit 2 }
$sha0 = Sha $set; $bytes0 = [IO.File]::ReadAllBytes($set)

# 输出目录：设置里那份不可用时按字节改到仓库内（产品会自建目录，但那台机器没有 D 盘）
$outPath = $null
try { $outPath = (Get-Content -LiteralPath $set -Raw -Encoding UTF8 | ConvertFrom-Json).OutputPath } catch { }
if (-not $outPath -or -not (Test-Path -LiteralPath $outPath)) {
    $raw = [IO.File]::ReadAllText($set)
    $anchor = '"OutputPath":"' + ($outPath -replace '\\','\\') + '"'
    if (([regex]::Matches($raw, [regex]::Escape($anchor))).Count -ne 1) {
        Say @('RC=2 OutputPath 锚点不是恰好一处 ⇒ 不猜'); exit 2
    }
    [IO.File]::WriteAllText($set, $raw.Replace($anchor, '"OutputPath":"' + ($repoOut -replace '\\','\\') + '"'), $utf8)
    $outPath = $repoOut
    Say @("★ OutputPath 不可用 ⇒ 本轮临时改到 $repoOut（收尾原样还原）")
} else { Say @("OutputPath = $outPath") }

# 剪贴板快照（尽量还原；还原不了就记下来，不假装）
$cbText0 = $null; $cbImage0 = $null
try { $cbText0 = [System.Windows.Forms.Clipboard]::GetText() } catch { }
try { $cbImage0 = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
Say @("轮前剪贴板：文本 $(if ($cbText0) { "「$($cbText0.Substring(0,[Math]::Min(30,$cbText0.Length)))…」" } else { '无' })  位图 $(if ($cbImage0) { "$($cbImage0.Width)x$($cbImage0.Height)" } else { '无' })")

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WC
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()   { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    // 单修饰键：Ctrl+C 这种两键组合别拿 Chord 凑（Chord 要两个修饰键位，硬填会把同一个键按两次）
    public static void Ctrl(byte main)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('WC' -as [type])) { Say @('RC=2 P/Invoke 类型没编出来'); exit 2 }
$auto = [System.Windows.Automation.AutomationElement]
function Top-Full([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    foreach ($w in @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        try { $r = $w.Current.BoundingRectangle; if ($r.Width -gt 3000 -and $r.Height -gt 1500) { return $w } } catch { }
    }
    return $null
}
$exts = @('*.png','*.jpg','*.jpeg','*.webp','*.jxl','*.avif','*.tif','*.tiff')
$beforeNames = @(Get-ChildItem -LiteralPath $outPath -File -Include $exts -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$logMark = 0; if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }

$proc = $null; $imgFile = $null
try {
    $env:TTC_FORCE_SDR = '1'
    Say @('已设 TTC_FORCE_SDR=1（与上一格同一档：SDR 呈现帧）')
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $cl = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark | Where-Object { $_ -match '已注册: Ctrl\S*\+S\b' } | Select-Object -First 1)
    $chord = if ($cl) { ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($cl[0]).Groups[1].Value } else { 'Ctrl+Shift+S' }
    Say @("送出 $chord（$(if ($cl) {'从日志现读'} else {'默认，未从日志证实'})）")
    if ($chord -eq 'Ctrl+Shift+S') { [WC]::Chord(0x11, 0x10, 0x53) } else { [WC]::Chord(0x11, 0x12, 0x53) }
    $w = $null; $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl) { $w = Top-Full $proc.Id; if ($w) { break }; Start-Sleep -Milliseconds 400 }
    if (-not $w) { Say @('★ 没等到全屏窗 ⇒ 未判'); throw 'no-window' }
    $cls = $w.Current.ClassName
    Say @("全屏窗 <$cls>")
    if ($cls -ne 'TTC_HC') { Say @('★ 还在走旧覆盖层 ⇒ 改道没生效，未判'); throw 'wrong-route' }

    # 上一轮这三次手势：22:43 那次成了（文件落地），23:10 / 23:13 都没选中。差别只在"窗出现后到按下左键"
    # 的间隔（成功那次约 2.6 s，失败那两次约 1 s）⇒ 这里把起手前的等待钉到 4 s，并多走几步中间点，
    # 让 WM_MOUSEMOVE 序列更像真实拖拽（一步到位容易被当成单击/被首帧渲染吃掉）。
    Start-Sleep -Seconds 4
    $null = [WC]::SetCursorPos(1000, 700); Start-Sleep -Milliseconds 400
    [WC]::LeftDown();                     Start-Sleep -Milliseconds 300
    foreach ($pt in @(@(1350,900), @(1700,1100), @(2050,1300), @(2400,1500))) {
        $null = [WC]::SetCursorPos($pt[0], $pt[1]); Start-Sleep -Milliseconds 220
    }
    [WC]::LeftUp();                       Start-Sleep -Milliseconds 1500
    Say @('已拖框 (1000,700)→(2400,1500)（起手前等 4 s，四步中间点）')
    # 前提见证：产品现在会写「选区完成 WxH」（HdrCaptureWindow.cs 里那行 Info）。
    # 没有它就无法区分"键没进来"与"压根没选中"——上一版只能追一发 Enter 反推，那是把结论盖在猜上。
    $selWitness = $null; $dw = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $dw -and -not $selWitness) {
        $hit = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark | Where-Object { $_ -match '选区完成' })
        if ($hit.Count) { $selWitness = $hit[0] } else { Start-Sleep -Milliseconds 400 }
    }
    if (-not $selWitness) { Say @('★ 没有「选区完成」那行 ⇒ 前提不成立（我的拖拽没选中），本格未判，直接收') ; throw 'no-selection' }
    Say @("前提成立：$(($selWitness -replace '^.*?选区完成','选区完成').Substring(0,[Math]::Min(60,($selWitness -replace '^.*?选区完成','选区完成').Length)))")
    [WC]::Ctrl(0x43)   # Ctrl+C ⇒ HDR 窗那一支的 Copy（HdrCaptureWindow.cs:2140，只在 _selComplete 时生效）
    Say @('已送出 Ctrl+C')

    # 完成行的读法要能【分清两种失败】，否则一次未判什么都说不清：
    #   只等 Ctrl+C ⇒ "没等到" 既可能是拖拽没选中，也可能是这个键没进到那扇窗。
    #   所以第二段故意再按一次 Enter（上一格已证明 Enter 在这条路上能出 `HDR 选区保存`）：
    #     Enter 有反应而 Ctrl+C 没有 ⇒ 问题在键入口；两个都没反应 ⇒ 我的手势没落到选区上，属未判。
    function Wait-Line([int]$sec, [string]$pattern) {
        $d = (Get-Date).AddSeconds($sec)
        while ((Get-Date) -lt $d) {
            $raw = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
            $hit = @($raw | Where-Object { $_ -match $pattern })
            if ($hit.Count) { return $hit[0] }
            Start-Sleep -Milliseconds 400
        }
        return $null
    }
    $half = [Math]::Max(8, [int]($WaitSec / 2))
    # 判"复制成没成"最直接的读数是剪贴板本身（轮前是那张 506x508），日志行只当旁证
    $cbChanged = $null
    $d3 = (Get-Date).AddSeconds($half)
    while ((Get-Date) -lt $d3 -and -not $cbChanged) {
        try {
            $im = [System.Windows.Forms.Clipboard]::GetImage()
            if ($im) {
                if (-not $cbImage0 -or $im.Width -ne $cbImage0.Width -or $im.Height -ne $cbImage0.Height) {
                    $cbChanged = "$($im.Width)x$($im.Height)"
                }
                $im.Dispose()
            }
        } catch { }
        if (-not $cbChanged) { Start-Sleep -Milliseconds 400 }
    }
    if ($cbChanged) { Say @("剪贴板已换成 $cbChanged（轮前 $(if ($cbImage0) { "$($cbImage0.Width)x$($cbImage0.Height)" } else { '无位图' })）⇒ 复制这条腿有反应") }
    $copyLine = Wait-Line 3 'HDR 选区复制到剪贴板|选区复制到剪贴板|复制失败'
    if ($copyLine -or $cbChanged) {
        if ($copyLine) { Say @("Ctrl+C 的完成行：$(($copyLine -split ' ')[0..1] -join ' ') …") }
        if ($cbChanged) { Say @("判据取自剪贴板本身：已换成 $cbChanged") }
    }
    else {
        Say @("★ Ctrl+C 后 ${half}s 内没有完成行 ⇒ 追一发 Enter 做判别（Enter 在上一格是能落地的）")
        [WC]::Tap(0x0D)
        $saveLine = Wait-Line $half 'HDR 选区保存|开始截图流程|选区确认保存'
        if ($saveLine) {
            Say @("Enter 有反应而 Ctrl+C 没有 ⇒ **这条路上的 Ctrl+C 入口不通**（不是拖拽问题）")
            $script:verdict = 'KEY-DEAD'
        } else {
            Say @('Enter 也没有任何选区反应 ⇒ 是【拖拽没选中】，本格未判，不许读成产品缺陷')
            $script:verdict = 'NO-SELECTION'
        }
        throw 'copy-not-observed'
    }
    Start-Sleep -Milliseconds 800
    $img = $null
    try { $img = [System.Windows.Forms.Clipboard]::GetImage() } catch { Say @("读剪贴板失败：$($_.Exception.Message)") }
    if ($img) {
        $imgFile = Join-Path $repoOut 'clipboard.png'
        $img.Save($imgFile, [System.Drawing.Imaging.ImageFormat]::Png)
        Say @("剪贴板有位图：$($img.Width)x$($img.Height) 格式=$($img.PixelFormat) ⇒ 存 $imgFile")
        $st = Get-BitmapStats -File $imgFile
        $v = Get-StatsVerdict -st $st
        Say @("  内容：颜色 $($st.Kinds) 种 纯白 $($st.WhitePct)% 全黑 $($st.BlackPct)% 均值 $($st.Mean) ⇒ $v")
        if ($st.Kinds -lt 8 -or $st.WhitePct -ge 95) { Say @('★ 剪贴板里是平/无结构'); }
        else { $rc = 0; Say @('✅ 复制这条腿可用：剪贴板里有该选区的位图且有结构') }
        $img.Dispose()
    } else { Say @('★ 完成行有了但剪贴板里没有位图 ⇒ 复制这条腿不可用') }
    $newFiles = @(Get-ChildItem -LiteralPath $outPath -File -Include $exts -Recurse -ErrorAction SilentlyContinue | Where-Object { $beforeNames -notcontains $_.FullName })
    Say @("顺带落盘的新产物：$($newFiles.Count) 枚 $(($newFiles | ForEach-Object { $_.Name }) -join ', ')")
    $rawTail = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
        Where-Object { $_ -match '复制|剪贴板|编码完成|失败|降级|预览帧来源|UploadFrame' })
    foreach ($l in ($rawTail | Select-Object -Last 8)) { Say @("  | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }
} catch { Say @("中断：$($_.Exception.Message)") } finally {
    Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue
    try { [WC]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 700
    if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @('已停止本次起的 PID=' + $proc.Id) } catch { Say @('★ 停止失败') } }
    try {
        if ($cbImage0) { [System.Windows.Forms.Clipboard]::SetImage($cbImage0); Say @('剪贴板已还原成轮前那张位图') }
        elseif ($cbText0) { [System.Windows.Forms.Clipboard]::SetText($cbText0); Say @('剪贴板已还原成轮前那段文本') }
        else { Say @('轮前剪贴板是空的 ⇒ 不还原（也没东西可还原）') }
    } catch { Say @("★ 剪贴板还原失败：$($_.Exception.Message)（本轮确实动过它）") }
    if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $same = (Sha $set) -eq $sha0
    if ($same) { $m = '与轮前逐字相同 ✓' } else { $m = '★ 与轮前不同，立即核对'; $rc = 2 }
    Say @("设置还原自检：$(Sha $set) ⇒ $m")
}
Say @("RC=$rc")
Write-Output "RC=$rc  $imgFile"
exit $rc
