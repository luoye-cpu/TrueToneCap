# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 端到端：SDR 档改道之后，选区→编码→落盘→读回声明，整条链路还能不能用（2026-09-30）
#
# 为什么要这一格：改道只证到"打开的是同一张窗、屏幕上不是平的"，
# 但保存那条腿以前在 SDR 档走的是覆盖层的 ActionCompleted，现在走的是 HDR 窗那一支
# （hdrRegion=null ⇒ ComposeAnnotatedSdr ⇒ EncodeAndSaveAsync）——**换了喂法的编码路径必须自己跑一遍**。
# 读回来的东西分三问，全部可机判：
#   ① 文件落了吗、能解码吗（ffprobe 独立解码器，不用产品自己那一套）；
#   ② 里面是有结构的画面，还是平白一片（与 preview-content-cell 同一把尺 preview-stats-lib）；
#   ③ 它的【色彩声明】与内容范围对不对得上 —— 声明成 PQ/BT.2020 而整帧没有任何超纸白样本，
#      就是"声明比交付强"，这一条只报数，不擅自改口径（三档策略是拍板过的）。
#
# 会动到你的：一次真实鼠标拖拽 + 一次 Enter（约 40 秒）。只用产品自己的动作，不注入编码参数。
# 输出目录：设置里那份 OutputPath 若不可写/不存在，按字节临时改到仓库内，收尾原样还原（产品会自己建目录，
# 但本机设置指向 D:\TTC 而这台机器没有 D 盘 ⇒ 不改就每次保存必失败）。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$SaveWaitSec = 45,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\preview-save-e2e-cell.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }
. 'C:\PLAN\TrueToneCap\tests\manual\safety\preview-stats-lib.ps1'

$ffprobe = 'C:\PLAN\FFmpeg\ffprobe.exe'
$ffmpeg  = 'C:\PLAN\FFmpeg\ffmpeg.exe'
$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$repoOut = 'C:\PLAN\TrueToneCap\tests\output\preview-e2e'
New-Item -ItemType Directory -Force -Path $repoOut | Out-Null
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 保存端到端 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$rc = 1
foreach ($t in @($ffprobe, $ffmpeg)) {
    if (-not (Test-Path -LiteralPath $t)) { Say @("RC=2 未判：外部裁判不在位 $t（不用产品自己的解码器自证）"); Write-Output 'RC=2 缺 ffprobe'; exit 2 }
}
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 未判：没有同目录设置文件'); exit 2 }

$sha0 = Sha $set; $bytes0 = [IO.File]::ReadAllBytes($set)
Say @("轮前设置 sha256 = $sha0")
$outPath = $null
try { $outPath = (Get-Content -LiteralPath $set -Raw -Encoding UTF8 | ConvertFrom-Json).OutputPath } catch { }
if (-not $outPath -or -not (Test-Path -LiteralPath $outPath)) {
    $raw = [IO.File]::ReadAllText($set)
    $anchor = '"OutputPath":"' + ($outPath -replace '\\','\\') + '"'
    $hits = ([regex]::Matches($raw, [regex]::Escape($anchor))).Count
    if ($hits -ne 1) { Say @("RC=2 未判：OutputPath 锚点命中 $hits 次（要求 1）⇒ 不猜"); exit 2 }
    $patched = $raw.Replace($anchor, '"OutputPath":"' + ($repoOut -replace '\\','\\') + '"')
    [IO.File]::WriteAllText($set, $patched, $utf8)
    $outPath = $repoOut
    Say @("★ 设置里的 OutputPath 不可用 ⇒ 本轮按字节改到 $repoOut（收尾原样还原；产品自己会建目录，但那台机器没 D 盘）")
} else {
    Say @("OutputPath = $outPath（可直接用）")
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WE
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X; public int Y; }
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
    public static string Where() { PT p; return GetCursorPos(out p) ? (p.X + "," + p.Y) : "unknown"; }
}
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('WE' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }
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
Say @("轮前产物 $($beforeNames.Count) 枚（只认差集里新出现的那枚）")
$logMark = 0; if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }

$proc = $null; $newFile = $null; $verdicts = @()
try {
    $env:TTC_FORCE_SDR = '1'
    Say @('已设 TTC_FORCE_SDR=1 ⇒ 走本轮新接入的 SDR 呈现帧')
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    $chordLine = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
        Where-Object { $_ -match '已注册: Ctrl\S*\+S\b' } | Select-Object -First 1)
    if ($chordLine) {
        $chord = ([regex]'已注册:\s*(Ctrl\S*\+S)').Match($chordLine[0]).Groups[1].Value
        Say @("日志自报实际注册键 = $chord（不猜）")
    } else { $chord = 'Ctrl+Shift+S'; Say @("★ 没读到「已注册」那行，按默认 $chord 试") }
    if ($chord -eq 'Ctrl+Shift+S') { [WE]::Chord(0x11, 0x10, 0x53) } else { [WE]::Chord(0x11, 0x12, 0x53) }
    Say @("已送出 $chord")

    $w = $null; $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl) { $w = Top-Full $proc.Id; if ($w) { break }; Start-Sleep -Milliseconds 400 }
    if (-not $w) { Say @('★ 没等到全屏窗 ⇒ 未判'); throw 'no-window' }
    $cls = $w.Current.ClassName
    Say @("全屏窗 <$cls>")
    if ($cls -ne 'TTC_HC') { $verdicts += "★ 窗类 <$cls>：SDR 档仍在走旧覆盖层，改道没生效"; throw 'wrong-route' }

    # 一次真实拖拽（中间走两步，避免被当成单击）
    $null = [WE]::SetCursorPos(900, 600); Start-Sleep -Milliseconds 250
    [WE]::LeftDown()
    $null = [WE]::SetCursorPos(1650, 1050); Start-Sleep -Milliseconds 200
    $null = [WE]::SetCursorPos(2400, 1500); Start-Sleep -Milliseconds 250
    [WE]::LeftUp()
    Start-Sleep -Milliseconds 1200
    Say @("已拖框 (900,600)→(2400,1500)，光标现在 $([WE]::Where())（本进程 DPI-unaware ⇒ 落点会被虚拟化，坐标只当'框了一个居中的大块'用）")

    [WE]::Tap(0x0D)   # Enter = 保存并复制（HDR 窗自己的 WndProc 处理键盘）
    Say @('已按 Enter')
    $dl2 = (Get-Date).AddSeconds($SaveWaitSec)
    while ((Get-Date) -lt $dl2 -and -not $newFile) {
        $c = @(Get-ChildItem -LiteralPath $outPath -File -Include $exts -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $beforeNames -notcontains $_.FullName })
        if ($c.Count -ge 1) { $newFile = ($c | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
        else { Start-Sleep -Milliseconds 500 }
    }
    if (-not $newFile) { Say @("★ ${SaveWaitSec}s 内没有新产物 ⇒ 这一格未判（不据此说'保存坏了'）"); throw 'no-file' }
    $fi = Get-Item -LiteralPath $newFile
    $hNew = (Sha $newFile).Substring(0,16)
    Say @("新产物 = $newFile  $($fi.Length) B  sha256=$hNew")
    $tail = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
        Where-Object { $_ -match '选区|开始截图流程|格式=|编码|降级|保底|已保存|失败|HDR' })
    Say @("   产品日志（本次范围内 $($tail.Count) 条，取后 12 条）：")
    foreach ($l in ($tail | Select-Object -Last 12)) { Say @("     | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }

    # ── ① 外部裁判：能不能解码、尺寸多少 ──
    $json = & $ffprobe -v error -show_entries stream=codec_name,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries -of json $newFile 2>&1
    if ("$LASTEXITCODE" -ne '0') { $verdicts += "★ ffprobe 拒绝解码（exit=$LASTEXITCODE）⇒ 产物不可用"; throw 'probe-fail' }
    $s = ($json | ConvertFrom-Json).streams[0]
    Say @("① ffprobe：$($s.codec_name) $($s.width)x$($s.height) pix_fmt=$($s.pix_fmt) range=$($s.color_range) 空间=$($s.color_space) 传输=$($s.color_transfer) 原色=$($s.color_primaries)")
    $verdicts += "① 外部裁判可解码 ⇒ 产物落地且格式有效（$($s.codec_name) $($s.width)x$($s.height) $($s.pix_fmt)）"
    $side = & $ffprobe -v error -select_streams v:0 -show_entries frame_side_data_list -of json $newFile 2>&1
    $sideTxt = ($side -join ' ')
    $hasIcc = $sideTxt -match 'icc_profile'
    Say @("   ICC 侧数据存在=$hasIcc")

    # ── ② 结构：先经 ffmpeg 换成 8-bit BMP，再用与另一格同一把尺（避开 16-bit 容器读法坑）──
    $bmp = Join-Path $repoOut 'decoded8.bmp'
    if (Test-Path -LiteralPath $bmp) { Remove-Item -LiteralPath $bmp -Force }
    & $ffmpeg -v error -y -i $newFile -pix_fmt rgb24 $bmp | Out-Null
    if ("$LASTEXITCODE" -ne '0' -or -not (Test-Path -LiteralPath $bmp)) {
        $verdicts += "★ ffmpeg 读不出 8-bit 副本（exit=$LASTEXITCODE）⇒ 内容没判"
    } else {
        $st = Get-BitmapStats -File $bmp
        $v = Get-StatsVerdict -st $st
        Say @("② 内容：$($st.W)x$($st.H) 不同颜色 $($st.Kinds) 种 纯白 $($st.WhitePct)% 全黑 $($st.BlackPct)% 均值 $($st.Mean) ⇒ $v")
        $verdicts += "② 内容判读：$v（颜色 $($st.Kinds) 种 / 白 $($st.WhitePct)% / 黑 $($st.BlackPct)%）"
    }

    # ── ③ 声明 vs 内容范围：HDR 档的证据【只能取传输特性】 ──
    # 第一版我把"原色=bt2020"也算作"声明成 HDR"，于是把拍板过的第二档（广色域→BT2020+SDR）
    # 误报成 ★ —— 判据的覆盖面比它声称的事由宽（报告 §34.9 记这条）。
    $hdrDeclared = ($s.color_transfer -eq 'smpte2084') -or ($s.color_transfer -eq 'smpte-st-2084')
    Say @("③ 声明：传输=$($s.color_transfer) 原色=$($s.color_primaries) 位深由 pix_fmt=$($s.pix_fmt) 推 ⇒ 传输为 PQ 才算 HDR 档=$hdrDeclared")
    if ($hdrDeclared) {
        $verdicts += ("③ ★ 传输被声明成 PQ（{0}）而这一档的像素源是 SDR(BGRA→scRGB)；" -f $s.color_transfer) +
                     "本格没量『超纸白样本占比』（要 float 解码那条读法），只把不一致点出来"
    } else {
        $verdicts += ("③ 传输={0} ⇒ SDR 档；原色={1} 与产品日志那句『HDR=False/BlockedByDisplay、位深=8、色域=BT2020』一致（正是拍板的第二档：广色域→BT2020+SDR）" `
            -f $s.color_transfer, $s.color_primaries)
    }
    $rc = 0
} catch {
    Say @("中断：$($_.Exception.Message)")
    if ($verdicts.Count -eq 0) { $verdicts += "未判：$($_.Exception.Message)" }
} finally {
    Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue
    try { [WE]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 800
    if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @('已停止本次起的 PID=' + $proc.Id) } catch { Say @('★ 停止失败') } }
    if ($newFile -and (Test-Path -LiteralPath $newFile)) {
        Remove-Item -LiteralPath $newFile -Force -ErrorAction SilentlyContinue
        Say @("本轮产物已删除：$newFile 仍在=$([bool](Test-Path -LiteralPath $newFile))")
    }
    if (Test-Path -LiteralPath (Join-Path $repoOut 'decoded8.bmp')) { Remove-Item -LiteralPath (Join-Path $repoOut 'decoded8.bmp') -Force -ErrorAction SilentlyContinue }
    if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $same = (Sha $set) -eq $sha0
    if ($same) { $markSet = '与轮前逐字相同 ✓' } else { $markSet = '★ 与轮前不同，立即核对'; $rc = 2 }
    Say @("设置还原自检：$(Sha $set) ⇒ $markSet")
}
Say @('--- 判词 ---')
foreach ($v in $verdicts) { Say @("  $v") }
Say @("RC=$rc  外部裁判=ffprobe/ffmpeg（C:\PLAN\FFmpeg，非产品自带解码器）")
Write-Output "RC=$rc  $($verdicts -join ' ｜ ')"
exit $rc
