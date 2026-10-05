# 覆盖层预览"整屏白"的一票区分实测（2026-09-30，维护者肉眼报告：TTC_FORCE_SDR 下预览全白）
#
# 已证的两步（app 日志 19:09:50 那一拍 #nhlu01）：
#   · WGC 交来 3840x2160 的 SDR 帧（「会话启动 … SDR」「首帧就绪 … SDR」）；
#   · SelectionOverlay 把它经 BgraToScrgbLinearFast 交给 HDRPreviewWindow.PresentFrame，
#     日志「GPU 背景窗口 (SDR→scRGB) 已创建」+ GPU背景=True，且没有 PresentFrameCore/Map 那两条 bail 的 Warn。
# ⇒ 剩下的两种成因必须分开：
#   A 源帧本身就是白的 ⇒ 白在捕获侧，呈现链只是诚实呈现；
#   B 源帧有真实内容 ⇒ 白是呈现链（值域/alpha/float→half/色彩声明/MPO）洗出来的。
# 区分办法：按 Enter 让产品【保存选区】。那条路读的是 desktopPixels
# （MainWindow 的 ExtractRegionFromDesktop / overlay.AnnotatedRegionPixels），完全不经过 GPU 背景窗
# ⇒ 产物有内容 = A 被否；产物是白 = A 成立。
#
# 副作用要说白：Enter 按产品自己的 ToolTip 是"保存截图到文件并复制"⇒ 会往设置里的 OutputPath 落一枚文件
#   （本轮先记名字/大小/sha256 再删除），也会写一次剪贴板。
# 纪律：设置按原字节还原并复算 sha256；只杀本次起的 PID；收尾按 Esc；产物目录用前后差集找新文件
#   （不按"目录里第一个"当夹具）；任何一步没成一律记【未判】而不是结论。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$SaveWaitSec = 30,
    # 设置里的 OutputPath 指向本机不存在的盘时，按字节临时改到仓库内落点（收尾原样还原）
    [switch]$NoOverrideOutputPath,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\overlay-frame-probe-cell.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [System.IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 覆盖层源帧实测 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$verdict = '未判'
if (-not (Test-Path -LiteralPath $Exe)) { Say @('RC=2 未判：没有宿主'); Write-Output 'RC=2 没有宿主'; exit 2 }
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 未判：没有同目录设置文件'); Write-Output 'RC=2 没有设置'; exit 2 }

$outPath = $null
try { $outPath = (Get-Content -LiteralPath $set -Raw -Encoding UTF8 | ConvertFrom-Json).OutputPath } catch { }
if ((-not $outPath -or -not (Test-Path -LiteralPath $outPath)) -and (-not $NoOverrideOutputPath)) {
    # 本轮实测撞到：设置里 OutputPath=D:\TTC 而本机没有 D 盘 ⇒ 保存那一步根本落不了文件。
    # 这是用户自己的配置，不改盘上那份的语义 ⇒ 只在内存里按字节替换那一处路径，跑完原样还原。
    Say @("★ OutputPath=「$outPath」不可用（本机没这块盘/目录）⇒ 本轮按字节临时改到仓库内落点，收尾原样还原")
    $tmp = 'C:\PLAN\TrueToneCap\tests\output\frame-probe'
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $raw = [IO.File]::ReadAllText($set)
    $needle = '"OutputPath":"D:\\TTC"'
    $hits = ([regex]::Matches($raw, [regex]::Escape($needle))).Count
    if ($hits -ne 1) {
        Say @("RC=2 未判：按字节改 OutputPath 的锚点命中 $hits 次（要求恰好 1）⇒ 不猜，先人工看那份设置")
        Write-Output 'RC=2 设置锚点不明'; exit 2
    }
    $patched = $raw.Replace($needle, '"OutputPath":"' + ($tmp -replace '\\','\\') + '"')
    [IO.File]::WriteAllText($set, $patched, $utf8)
    $outPath = $tmp
    Say @("已改：OutputPath → $outPath（盘上那份轮后按原字节还原）")
}
if (-not $outPath -or -not (Test-Path -LiteralPath $outPath)) {
    Say @("RC=2 未判：OutputPath=「$outPath」仍不可用 ⇒ 无法定位产物")
    Write-Output 'RC=2 OutputPath 不可用'; exit 2
}
Say @("OutputPath = $outPath")
$sha0 = Sha $set; $bytes0 = [IO.File]::ReadAllBytes($set)
Say @("轮前设置 sha256 = $sha0")
$exts = @('*.png','*.jpg','*.jpeg','*.webp','*.jxl','*.avif','*.tif','*.tiff')
$beforeNames = @(Get-ChildItem -LiteralPath $outPath -File -Include $exts -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Say @("轮前产物目录图像文件 $($beforeNames.Count) 枚（本轮只认差集里新出现的那枚）")
$logMark = 0
if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class W32P
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X; public int Y; }

    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()   { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    // 组合键：按住修饰键 → 点主键 → 松开。0x11=Ctrl 0x10=Shift 0x53='S'
    public static bool Chord(byte mod1, byte mod2, byte main)
    {
        keybd_event(mod1, 0, 0, UIntPtr.Zero);
        keybd_event(mod2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(main, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(mod2, 0, 2, UIntPtr.Zero);
        keybd_event(mod1, 0, 2, UIntPtr.Zero);
        return true;
    }
    public static string Where() { PT p; return GetCursorPos(out p) ? (p.X + "," + p.Y) : "unknown"; }
}
'@
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('W32P' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); Write-Output 'RC=2 类型失败'; exit 2 }
Say @('P/Invoke 类型已就绪 ✓')
$auto = [System.Windows.Automation.AutomationElement]
function Top-Windows([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    return @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}
$rootRect = $auto::RootElement.Current.BoundingRectangle
Say @("UIA 根（虚拟桌面，物理像素）= [$($rootRect.X),$($rootRect.Y) $($rootRect.Width)x$($rootRect.Height)]")

$proc = $null; $newFile = $null
try {
    $env:TTC_FORCE_SDR = '1'
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("已起真身 PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    Say @("触发前该 PID 顶层窗 $(@(Top-Windows $proc.Id).Count) 个")

    $null = [W32P]::Chord(0x11, 0x10, 0x53)
    Say @('已送出 Ctrl+Shift+S')

    $ov = $null; $seen = ''
    $dl = (Get-Date).AddSeconds(14)
    while ((Get-Date) -lt $dl -and $null -eq $ov) {
        foreach ($w in @(Top-Windows $proc.Id)) {
            try {
                $r = $w.Current.BoundingRectangle
                if ($r.Width -ge ($rootRect.Width * 0.9) -and $r.Height -ge ($rootRect.Height * 0.9)) {
                    $ov = $w; $seen = "$($w.Current.ClassName) $($r.Width)x$($r.Height)@$($r.X),$($r.Y)"; break
                }
            } catch { }
        }
        if ($null -eq $ov) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $ov) { Say @('★ 14s 内没出现全屏窗 ⇒ 覆盖层没起来，未判'); throw 'no-overlay' }
    Say @("全屏窗：$seen")
    if ($seen -match 'TTC_HC') { Say @('★ 这是 HdrCaptureWindow（开关没生效）⇒ 这一腿未判'); throw 'wrong-window' }

    $null = [W32P]::SetCursorPos(700, 500); Start-Sleep -Milliseconds 200
    [W32P]::LeftDown()
    $null = [W32P]::SetCursorPos(1400, 900); Start-Sleep -Milliseconds 150
    $null = [W32P]::SetCursorPos(2100, 1300); Start-Sleep -Milliseconds 200
    [W32P]::LeftUp()
    Start-Sleep -Milliseconds 1500
    Say @("已拖框 (700,500)→(2100,1300)，光标现在 $([W32P]::Where())")

    # 先证明"已进入选完状态"再谈保存：产品对"框已选定"这件事本身不打日志（第一次跑就是被这个空档
    # 骗成"Enter 没反应"，其实根本没法区分"没选中"与"选中了但 Enter 没落到按钮上"）。
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $btns = @($ov.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond))
    Say @("覆盖层内按钮 $($btns.Count) 个（0 个 = 拖拽没进入选完状态，后面怎么按都不该有产物）")
    if ($btns.Count -eq 0) { Say @('★ 没有工具条 ⇒ 这一腿未判（不是源帧的问题，是我的手势没落到覆盖层上）'); throw 'no-toolbar' }

    function Find-NewFile {
        $c = @(Get-ChildItem -LiteralPath $outPath -File -Include $exts -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $beforeNames -notcontains $_.FullName })
        if ($c.Count -ge 1) { return ($c | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
        return $null
    }
    function Wait-File([int]$sec, [string]$via) {
        $d = (Get-Date).AddSeconds($sec)
        while ((Get-Date) -lt $d) {
            $f = Find-NewFile
            if ($f) { Say @("产物经「$via」落盘：$f"); return $f }
            Start-Sleep -Milliseconds 400
        }
        return $null
    }

    [W32P]::Tap(0x0D)   # Enter（产品 ToolTip 写"保存截图到文件并复制 (Enter)"）
    $newFile = Wait-File 10 'Enter'
    if (-not $newFile) {
        # Enter 这条路依赖焦点真在覆盖层上；量不出就用 UIA 的 Invoke 直接点第一枚按钮
        # （XAML 里第一枚就是 BtnConfirm=保存 ⇒ 不靠中文名字匹配，避开编码/locale 依赖）
        Say @('★ Enter 之后 10s 没有产物 ⇒ 改用 UIA Invoke 点第一枚按钮（保存）')
        try {
            $inv = $null
            if ($btns[0].TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) {
                $inv.Invoke()
                Say @('   Invoke 已送出')
            } else { Say @('   ★ 第一枚按钮不支持 InvokePattern ⇒ 未判') }
        } catch { Say @("   ★ Invoke 失败：$($_.Exception.Message)") }
        $newFile = Wait-File 25 'Invoke'
    }
    if (-not $newFile) { Say @('★ 两种方式都没有产物 ⇒ 未判（"没量到"绝不能读成"源帧是白的"）'); throw 'no-file' }
    $fi = Get-Item -LiteralPath $newFile
    Say @("新产物 = $newFile  $($fi.Length) B  sha256=$(Sha $newFile)")
    $tail = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
        Where-Object { $_ -match '选区确认保存|编码|降级|保底|失败|阶段3|落盘' })
    Say @("   产品日志（本次范围内 $($tail.Count) 条，取后 8 条）：")
    foreach ($l in ($tail | Select-Object -Last 8)) { Say @("     | $($l.Substring(0,[Math]::Min(150,$l.Length)))") }

    $bmp = New-Object System.Drawing.Bitmap($newFile)
    try {
        $g = 40; $tot = 0; $white = 0; $sR = 0L; $sG = 0L; $sB = 0L; $vals = @{}
        for ($iy = 0; $iy -lt $g; $iy++) {
            for ($ix = 0; $ix -lt $g; $ix++) {
                $px = [int]($ix * ($bmp.Width - 1) / ($g - 1)); $py = [int]($iy * ($bmp.Height - 1) / ($g - 1))
                $c = $bmp.GetPixel($px, $py)
                $tot++; $sR += $c.R; $sG += $c.G; $sB += $c.B
                if ($c.R -eq 255 -and $c.G -eq 255 -and $c.B -eq 255) { $white++ }
                $vals["$($c.R),$($c.G),$($c.B)"] = 1
            }
        }
        $wf = [math]::Round(100.0 * $white / $tot, 2)
        Say @("产物 $($bmp.Width)x$($bmp.Height)，网格 $tot 点：纯白占 $wf% 均值 R$([math]::Round($sR/$tot,1)) G$([math]::Round($sG/$tot,1)) B$([math]::Round($sB/$tot,1)) 不同颜色 $($vals.Count) 种")
        if ($wf -ge 99) { $verdict = "A 成立：源帧就是白的（纯白占 $wf%）⇒ 白在捕获侧，呈现链只是诚实呈现" }
        elseif ($vals.Count -ge 8) { $verdict = "B 成立：源帧有真实内容（$($vals.Count) 种颜色、白占 $wf%）⇒ 白是呈现链洗出来的" }
        else { $verdict = "★ 两头都不像：白占 $wf%、颜色 $($vals.Count) 种 ⇒ 不下结论，补读数" }
        Say @("判词：$verdict")
    } finally { $bmp.Dispose() }
} catch {
    Say @("中断：$($_.Exception.Message)")
} finally {
    Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue
    try { $null = [W32P]::Tap(0x1B) } catch { }
    Start-Sleep -Milliseconds 600
    if ($null -ne $proc) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停止本次起的 PID=$($proc.Id)") } catch { Say @("★ 停止失败：$($_.Exception.Message)") }
    }
    if ($newFile -and (Test-Path -LiteralPath $newFile)) {
        Remove-Item -LiteralPath $newFile -Force -ErrorAction SilentlyContinue
        Say @("本轮产物已删除（自己造的副产物）：$newFile 仍在=$([bool](Test-Path -LiteralPath $newFile))")
    }
    if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $same = (Sha $set) -eq $sha0
    # ⚠ 别把判别句写成 "$(if (A) { '好' } { '坏' })"——少一个 else 两支文本都会进串（记忆里第 52 条，
    #   我在这份新脚本里又犯了一次，19:19 那份日志就是证据）。先算变量再插值。
    if ($same) { $markSet = '与轮前逐字相同 ✓' } else { $markSet = '★ 与轮前不同，立即核对' }
    Say @("设置还原自检：$(Sha $set) ⇒ $markSet")
}
$rc = if ($verdict -like 'A*' -or $verdict -like 'B*') { 0 } else { 2 }
Say @("RC=$rc VERDICT=$verdict")
Write-Output "RC=$rc VERDICT=$verdict"
exit $rc
