# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 批次 T3 最后一格：**选区覆盖层的工具条几何**（2026-09-30，用户授权真实键鼠注入）
#
# 判什么（代码里的规则，SelectionOverlay.xaml.cs:508-526）：
#   PositionToolbarAt 把工具条钳在 tx∈[0, effW-460]、ty∈[0, effH-50]；
#   PositionToolbarNearSelection 先摆到选区右下（x2-460, y2+5），装不下就翻到上边/左边。
#   ⇒ 可观测的不变量：① 工具条必须完整落在屏幕内；② 两次不同的拖拽必须落到【不同位置】
#      （否则"跟随选区"这句只是注释）；③ 按钮组真的存在（0 个按钮 = 覆盖层根本没进入选完状态）。
# 尺子的先天限制，先写在读数里：本脚本进程是 DPI-unaware 的，UIA 给物理像素、
#   SendInput 的绝对坐标按虚拟桌面归一，两者在缩放的 4K 屏上换算一定有误差 ⇒
#   这一格只报【不变量】，不报"工具条离选区右下角多少像素"这种要看真数的数。
# 收尾：Esc 关掉覆盖层、只杀自己起的 PID、还原设置核 sha1、产物落仓库内目录。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'overlay-geometry-cell.log',
    [int]$SettleSec = 12,
    # 上一轮实测：HDR 屏上按截图热键进的是 HdrCaptureWindow（D3D 直绘，UIA 里 0 控件）⇒ 选区那条路走不到。
    # 这个开关就是为这种情况留的：诊断开关 TTC_FORCE_SDR=1 强制走 SDR 预览路径（SelectionOverlay），
    # 顺带把 §15.5 欠的"TTC_FORCE_SDR 那一格"一起结掉。
    [switch]$ForceSdr
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log = Join-Path $repo (Join-Path 'tests\output' $LogName)
$outDir = Join-Path $repo 'tests\output\t3-cells'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$bak = Join-Path $dir 'TrueToneCap.settings.json.ttc-ovbak'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
if (-not (Test-Path -LiteralPath $set)) { Fail "没有 $set" }
if (Test-Path -LiteralPath $bak) { Fail "遗留 $bak ⇒ 先人工核对" }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 覆盖层工具条几何 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Win32Ptr
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    // MOUSEEVENTF_LEFTDOWN=0x0002 / LEFTUP=0x0004
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }

    // ⚠ 不能写 public：参数类型 INPUT 是本类内的嵌套类型（默认 private），
    //   public 方法拿它当参数会触发 CS0051「不一致的可访问性」，整个 Add-Type 编译失败 ⇒ TypeNotFound。
    [DllImport("user32.dll")]
    static extern uint SendInput(uint n, INPUT[] pInputs, int cbSize);

    // 与产品无关，只为按 ESC 关掉覆盖层：键盘事件走 SendInput 而不是 keybd_event（后者在部分环境被忽略）
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    public static bool Key(ushort vk)
    {
        var arr = new INPUT[2];
        arr[0].type = 1; arr[0].ki.wVk = vk; arr[0].ki.dwFlags = 0;
        arr[1].type = 1; arr[1].ki.wVk = vk; arr[1].ki.dwFlags = 0x0002;   // KEYUP
        return SendInput(2, arr, Marshal.SizeOf(typeof(INPUT))) == 2;
    }

    // 一组修饰键 + 主键（MOD_ALT=1 / CONTROL=2 / SHIFT=4 / WIN=8）
    public static bool Chord(uint mods, ushort vk)
    {
        var list = new System.Collections.Generic.List<INPUT>();
        if ((mods & 2) != 0) list.Add(Mk(0x11, false));
        if ((mods & 4) != 0) list.Add(Mk(0x10, false));
        if ((mods & 1) != 0) list.Add(Mk(0x12, false));
        if ((mods & 8) != 0) list.Add(Mk(0x5B, false));
        list.Add(Mk(vk, false)); list.Add(Mk(vk, true));
        if ((mods & 8) != 0) list.Add(Mk(0x5B, true));
        if ((mods & 1) != 0) list.Add(Mk(0x12, true));
        if ((mods & 4) != 0) list.Add(Mk(0x10, true));
        if ((mods & 2) != 0) list.Add(Mk(0x11, true));
        var arr = list.ToArray();
        return SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT))) == (uint)arr.Length;
    }
    static INPUT Mk(ushort vk, bool up)
    {
        return new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u }
        };
    }
}
'@
# 自检：类型没编出来就别往下走（上一轮就是 SendInput 声明成 public 导致 CS0051，
# 整块编译失败，后面每一步都只在报 TypeNotFound，看着像"产品没响应"）。
if (-not ('Win32Ptr' -as [type])) { Fail 'Add-Type 失败：Win32Ptr 不存在 ⇒ 不给结论' }
Say @('P/Invoke 类型已就绪 ✓')

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$escVk = 0x1B

function Top-Windows([int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pid_)
    return @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}
function Buttons-In($win) {
    try {
        $c = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        return @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c))
    } catch { return @() }
}
function Union-Rect($els) {
    $x1 = [double]::MaxValue; $y1 = [double]::MaxValue; $x2 = [double]::MinValue; $y2 = [double]::MinValue
    $n = 0
    foreach ($e in $els) {
        try {
            $r = $e.Current.BoundingRectangle
            if ($r.Width -le 0 -or $r.Height -le 0) { continue }
            $n++
            if ($r.X -lt $x1) { $x1 = $r.X }; if ($r.Y -lt $y1) { $y1 = $r.Y }
            if (($r.X + $r.Width) -gt $x2) { $x2 = $r.X + $r.Width }
            if (($r.Y + $r.Height) -gt $y2) { $y2 = $r.Y + $r.Height }
        } catch { }
    }
    if ($n -eq 0) { return $null }
    return [pscustomobject]@{ X = $x1; Y = $y1; W = ($x2 - $x1); H = ($y2 - $y1); N = $n }
}

$proc = $null
try {
    $rootRect = $auto::RootElement.Current.BoundingRectangle
    Say @("UIA 根（虚拟桌面，物理像素）= [$($rootRect.X),$($rootRect.Y) $($rootRect.Width)x$($rootRect.Height)]")
    if ($rootRect.Width -le 0) { Fail 'UIA 拿不到桌面矩形 ⇒ 坐标没有参照，不给结论' }

    $sha0 = Sha $set
    Say @("轮前设置 sha1: $sha0")
    Move-Item -LiteralPath $set -Destination $bak
    $j = [IO.File]::ReadAllText($bak) | ConvertFrom-Json
    $obj = [ordered]@{}
    foreach ($p in $j.PSObject.Properties) { $obj[$p.Name] = $p.Value }
    $obj['OutputPath'] = $outDir
    $obj['Hotkey'] = 'Ctrl+Shift+S'
    $obj['SettingsRevision'] = 2
    ($obj | ConvertTo-Json -Compress) | Set-Content -LiteralPath $set -NoNewline -Encoding UTF8

    $logMark = 0
    if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
    if ($ForceSdr) { $env:TTC_FORCE_SDR = '1'; Say @('已设 TTC_FORCE_SDR=1（诊断开关：强制走 SDR 预览 = SelectionOverlay 那条路）') }
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("已起真身 PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    $base = @(Top-Windows $proc.Id)
    Say @("覆盖层出现之前，该 PID 有 $($base.Count) 个顶层窗")

    function Ensure-Overlay([string]$tag) {
        # 先证明覆盖层真的起来了再动鼠标：按一次捕获热键，然后找一个"占满屏幕"的新顶层窗。
        # 没确认到就绝不下左键 —— 否则我点的是用户的桌面，不是产品的覆盖层（那会造成真实误操作）。
        $sent = [Win32Ptr]::Chord(2 -bor 4, 0x53)          # Ctrl+Shift+S
        Say @("   触发：按 Ctrl+Shift+S（Chord 送出=$sent）")
        $deadline = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $deadline) {
            $ws = @(Top-Windows $proc.Id)
            foreach ($w in $ws) {
                try {
                    $r = $w.Current.BoundingRectangle
                    if ($r.Width -ge ($rootRect.Width * 0.9) -and $r.Height -ge ($rootRect.Height * 0.9)) {
                        $cn = $w.Current.ClassName
                        Say @("   全屏窗已出现 <$($cn)> $($r.Width)x$($r.Height) at $($r.X),$($r.Y)")
                        # 上一轮实测：HDR 屏上按截图热键进的是 HdrCaptureWindow（类名 TTC_HC，D3D 直绘），
                        # 它的 UIA 子树里一个控件都没有 ⇒ 那一格机制性不可判。这里把它单独认出来，
                        # 别把"TTC_HC 里 0 个按钮"错报成"选区工具条有问题"。
                        if ($cn -eq 'TTC_HC') {
                            Say @('   ★ 这是 HdrCaptureWindow（TTC_HC），不是选区覆盖层 ⇒ 工具条几何在这一路上不可判')
                            Say @('     （顺带就是 §55 那条"字烘进位图、UIA 全盲"的现场证据：全屏 3840x2160、控件 0 个）')
                        }
                        return $w
                    }
                } catch { }
            }
            Start-Sleep -Milliseconds 400
        }
        Say @('   ★ 12 s 内没出现全屏覆盖层 ⇒ 拒绝下键（这一腿未判，且绝不往桌面上点）')
        return $null
    }

    function Dump-Tree($win, [string]$tag) {
        try {
            $all = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition))
            Say @("   [$tag] 子树共 $($all.Count) 个元素，前 24 个：")
            foreach ($e in ($all | Select-Object -First 24)) {
                try {
                    $r = $e.Current.BoundingRectangle
                    $nm = $e.Current.Name; if ($nm.Length -gt 30) { $nm = $nm.Substring(0, 30) + '…' }
                    Say @("      <$($e.Current.ControlType.ProgrammaticName)> 「$nm」 $($r.Width)x$($r.Height) @ $($r.X),$($r.Y)")
                } catch { }
            }
            return $all.Count
        } catch { Say @("   [$tag] 走树失败：$($_.Exception.Message)"); return -1 }
    }

    function Drag-And-Measure([string]$tag, [int]$ax, [int]$ay, [int]$bx, [int]$by) {
        Say @("--- 用例 $tag：($ax,$ay) → ($bx,$by) ---")
        $ov = Ensure-Overlay $tag
        if ($null -eq $ov) { return $null }
        $ok1 = [Win32Ptr]::SetCursorPos($ax, $ay)
        Start-Sleep -Milliseconds 250
        [Win32Ptr]::LeftDown()
        # 中间点：WinUI 的拖动要收到 WM_MOUSEMOVE 才算"拉出一个框"，一步到位可能被当成单击
        $mx = [int](($ax + $bx) / 2); $my = [int](($ay + $by) / 2)
        $null = [Win32Ptr]::SetCursorPos($mx, $my); Start-Sleep -Milliseconds 150
        $null = [Win32Ptr]::SetCursorPos($bx, $by); Start-Sleep -Milliseconds 150
        [Win32Ptr]::LeftUp()
        Start-Sleep -Milliseconds 900

        $btns = @(Buttons-In $ov)
        $u = Union-Rect $btns
        Say @("   覆盖层内按钮 $($btns.Count) 个（工具条出现 ⇒ 已进入选完状态）")
        # 逐枚报尺寸：上一版只把【有尺寸的】那些并进包围盒 ⇒ 被挤出窗口的那几枚悄悄从判据里消失，
        # 于是"6 枚里只有 4 枚量到尺寸"这件事读起来像 PASS（2026-09-30 实测撞到，见报告 §33.4）。
        foreach ($e in $btns) {
            try {
                $r = $e.Current.BoundingRectangle
                $nm = $e.Current.Name; if ($nm.Length -gt 12) { $nm = $nm.Substring(0, 12) }
                $mark = if ($r.Width -le 0 -or $r.Height -le 0) { '★ 无量到尺寸' } else { '' }
                Say @("      「$nm」 $($r.Width)x$($r.Height) @ $($r.X),$($r.Y) $mark")
            } catch { Say @("      读单个按钮失败：$($_.Exception.Message)") }
        }
        if ($btns.Count -eq 0) { $null = Dump-Tree $ov '按钮为 0，改列全部子元素' }
        if ($null -eq $u) {
            Say @('   ⏭ 量不到按钮包围盒 ⇒ 这一腿未判（覆盖层可能根本没进入"选完"状态）')
        } else {
            $inX = ($u.X -ge $rootRect.X) -and (($u.X + $u.W) -le ($rootRect.X + $rootRect.Width))
            $inY = ($u.Y -ge $rootRect.Y) -and (($u.Y + $u.H) -le ($rootRect.Y + $rootRect.Height))
            # 严格版不变量：每一枚按钮都必须有尺寸且整组落在屏内 —— 少一枚就是★，不许悄悄降格成"没看见=不在"
            $sized = $u.N
            $allSized = ($sized -eq $btns.Count)
            $verdict = if ($allSized -and $inX -and $inY) { 'PASS' } else { '★ FAIL：工具条没完整落在屏内' }
            $why = if (-not $allSized) { "只有 $sized/$($btns.Count) 枚量到尺寸（其余被挤出窗口或未布局）" } else { "全部 $sized 枚有尺寸" }
            Say @("   按钮组包围盒 = $($u.X),$($u.Y) 尺寸 $($u.W)x$($u.H)（$why）")
            Say @("   不变量①完整落在屏幕内：横向 $inX 纵向 $inY 尺寸齐全 $allSized ⇒ $verdict")
        }
        # 关掉这一轮的选区状态：ESC 通常取消覆盖层
        $null = [Win32Ptr]::Key($escVk)
        Start-Sleep -Milliseconds 600
        return $u
    }

    # 用例一：从左上往右下拖一个大框（工具条应在选区右下，且必须落在屏幕内）
    $a = Drag-And-Measure 'A 左上→右下大框' `
        ([int]($rootRect.Width * 0.08)) ([int]($rootRect.Height * 0.08)) `
        ([int]($rootRect.Width * 0.55)) ([int]($rootRect.Height * 0.55))
    # 用例二：贴着右下角拖（这一支最可能越界：ty+45>effH 要翻到上边）
    $b = Drag-And-Measure 'B 贴着右下' `
        ([int]($rootRect.Width * 0.70)) ([int]($rootRect.Height * 0.70)) `
        ([int]($rootRect.Width * 0.97)) ([int]($rootRect.Height * 0.97))

    # 用例三（对照臂）：选区右端离屏幕右边很远 ⇒ 若 B 少两枚的原因是"钳制常数把工具条顶出屏"，
    # 这一支应当 6 枚齐全；若这一支也少，就与位置无关（另有成因，不许把 B 的读数归因到钳制上）。
    $c = Drag-And-Measure 'C 对照·离右边缘远' `
        ([int]($rootRect.Width * 0.12)) ([int]($rootRect.Height * 0.15)) `
        ([int]($rootRect.Width * 0.35)) ([int]($rootRect.Height * 0.45))

    Say @('--- 不变量②：两次位置必须不同（否则「跟随选区」只是注释）---')
    if ($null -ne $a -and $null -ne $b) {
        $diff = [math]::Abs($a.X - $b.X) + [math]::Abs($a.Y - $b.Y)
        Say @("   A=($($a.X),$($a.Y)) B=($($b.X),$($b.Y)) 位移和=$([math]::Round($diff,1)) ⇒ $(if ($diff -gt 5) { 'PASS：确实跟着选区走' } else { '★ FAIL：两次落在同一处' })")
    } else { Say @('   ⏭ 至少一次没量到 ⇒ 这一条不判') }

    $tail = @()
    if (Test-Path -LiteralPath $appLog) {
        $tail = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
            Where-Object { $_ -match '覆盖层|选区|Overlay|工具|标注' })
    }
    Say @("--- 日志腿（本次范围内 $($tail.Count) 条，最多印 12 条）---")
    foreach ($l in ($tail | Select-Object -Last 12)) { Say @("  $l") }
    Write-Output "RC=0 CASE_A=$($null -ne $a) CASE_B=$($null -ne $b) CASE_C=$($null -ne $c) PID=$($proc.Id)"
} finally {
    if ($ForceSdr) { Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue }
    try { $null = [Win32Ptr]::Key($escVk) } catch { }
    if ($null -ne $proc) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停止本次起的 PID=$($proc.Id)") }
        catch { Say @("❌ 停止失败：$($_.Exception.Message)") }
    }
    if (Test-Path -LiteralPath $bak) {
        if (Test-Path -LiteralPath $set) { Remove-Item -LiteralPath $set -Force }
        Move-Item -LiteralPath $bak -Destination $set
        $shaEnd = Sha $set
        Say @("还原自检：原件移回 sha1=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：立即核对' })")
    } else { Say @('❌ 备份件不见了 ⇒ 立即人工复查 ' + $bak) }
}
