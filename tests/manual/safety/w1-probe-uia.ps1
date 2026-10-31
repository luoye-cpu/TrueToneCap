# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 探针：主窗在不在 UIA 树里、格式下拉能不能被驱动（决定 W1 矩阵用哪条路选格式）
#
# 为什么要先探：W1a 第一次跑量到"盘上 FormatIndex=2(JPEG XL)、产品自报 格式=PNG"——
# 因为编码用的格式取的是 **FormatCbo.SelectedIndex**（MainWindow.xaml.cs:2528 CurrentFormat()），
# 不是盘上那个键。手写 JSON 选不了格式 ⇒ 矩阵必须走界面那一条，这才既是真用户动作也是真事实源。
# 这一格只回答三件事：① 按 PID 能不能找到主窗（WinUI3 的 MainWindowHandle 恒 0）；
# ② 主窗是可见还是隐藏态；③ FormatCbo 能不能 Expand 并 Select 到指定项。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [int]$PickIndex = 3,
    [switch]$FireCapture,
    [switch]$ReadOnly,
    # 挂到【已经在跑】的那个实例上读，而不是再起一个：产品有单实例守卫
    # （App.xaml.cs 的 Mutex），第二次 Start-Process 只会得到一个立刻退出的进程 ⇒ 0 个顶层窗。
    [int]$AttachPid = 0,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\uia-probe.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== UIA 探针 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · PickIndex=$PickIndex ===")

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PF
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
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
        return h + "/pid=" + pid;
    }
}
'@
$auto = [System.Windows.Automation.AutomationElement]
$root = $auto::RootElement
function Count-Desc($el) {
    try { return @($el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)).Count } catch { return -1 }
}
# UIA 对"此刻没有有效布局"的元素（托盘隐藏态 / 尚未实化的 WinUI 窗）给的是 ±∞ 或 NaN，
# 而 `[int]` 转换会直接抛 "Cannot convert value ∞ to type Int32" —— 2026-10-31 实测：
# 探针在 ShowWindow(SW_RESTORE) 之后正好死在这一行，于是"界面显示哪一个格式"整格没判。
# 仪器不许把待判的读数炸掉：非有限值就原样印出来，读数仍然留在日志里。
# 不用 [double]::IsFinite —— 那是 .NET Core 3+ 才有的，PS 5.1 宿主下会抛 NoSuchMethod。
function Fmt-Rect($r) {
    $bad = [double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y) -or
           [double]::IsInfinity($r.Width) -or [double]::IsInfinity($r.Height) -or
           [double]::IsNaN($r.X) -or [double]::IsNaN($r.Y) -or
           [double]::IsNaN($r.Width) -or [double]::IsNaN($r.Height)
    if ($bad) { return '不可得(∞/NaN：该窗此刻没有有效布局)' }
    return "$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height)"
}

$dir = Split-Path -Parent $Exe
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$logMark = 0; if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }

$proc = $null; $pidT = 0; $owned = $false
try {
    if ($AttachPid -gt 0) {
        $pidT = $AttachPid
        $owned = $false
        Say @("挂到已有实例 PID=$pidT（不另起进程：产品有单实例守卫）")
    } else {
        $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
        $pidT = $proc.Id; $owned = $true
        Say @("PID=$pidT，等 $SettleSec s")
    }
    if (-not $owned) { Start-Sleep -Seconds $SettleSec }

    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pidT)
    $tops = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    Say @("按 PID 枚举到顶层窗 $($tops.Count) 个：")
    foreach ($w in $tops) {
        $r = $w.Current.BoundingRectangle
        Say @("  id=$($w.Current.AutomationId) class<$($w.Current.ClassName)> name=「$($w.Current.Name)」 rect=$(Fmt-Rect $r) 可见=$($w.Current.IsOffscreen) 子树=$(Count-Desc $w)")
    }
    # 主窗 = 子树元素数最大的那块（不是枚举第一个）
    $main = $null; $best = -1
    foreach ($w in $tops) { $c = Count-Desc $w; if ($c -gt $best) { $best = $c; $main = $w } }
    if (-not $main) { Say @('★ 一个顶层窗都没枚举到 ⇒ 探针未判'); exit 2 }
    Say @("选中主窗（子树最大 = $best 个元素）: class<$($main.Current.ClassName)>")

    # ① 隐藏态就 ShowWindow(SW_RESTORE=9) 再量（记忆：宿主窗会被外部点击序列意外最小化）
    $r0 = $main.Current.BoundingRectangle
    if ($r0.Width -lt 100 -or $main.Current.IsOffscreen) {
        Say @("★ 主窗 rect=$(Fmt-Rect $r0) offscreen=$($main.Current.IsOffscreen) ⇒ ShowWindow(SW_RESTORE) 后再看")
        foreach ($w in $tops) { $null = [PF]::ShowWindow($w.Current.NativeWindowHandle, 9) }
        Start-Sleep -Milliseconds 900
        $r0 = $main.Current.BoundingRectangle
        Say @("  还原后 rect=$(Fmt-Rect $r0)")
    }

    $cbs = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox))))
    Say @("可视树里的 ComboBox $($cbs.Count) 个：")
    foreach ($c in $cbs) {
        $v = ''
        try { $vp = $c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $v = $vp.Current.Value } catch { }
        Say @("  id=$($c.Current.AutomationId) name=「$($c.Current.Name)」 value=「$v」")
    }

    $target = $null
    foreach ($c in $cbs) { if ($c.Current.AutomationId -eq 'FormatCbo') { $target = $c } }
    if (-not $target) { Say @('★ 没找到 AutomationId=FormatCbo ⇒ 下拉不可达，本探针只证到这一步'); exit 2 }

    $ec = $null
    try { $ec = $target.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { Say @("★ ExpandCollapse 取不到: $($_.Exception.Message)"); exit 2 }
    Say @("FormatCbo 当前 ExpandCollapseState=$($ec.Current.ExpandCollapseState)")
    $ec.Expand(); Start-Sleep -Milliseconds 900
    # ⚠ 必须在 ComboBox 自己的子树里数 ListItem，不能在全窗找：
    #   06:54 实测全窗 FindAll 数到 17 项 = 格式 6 项 + 导航页 6 项 + 又一份格式 6 项（同一批元素从两条路径可见），
    #   拿全窗索引 [3] 命中 AVIF 只是运气，换成 [7] 就会点到导航项。
    $items = @($target.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    Say @("在 FormatCbo 子树内展开后数到 ListItem $($items.Count) 个：")
    $pick = $null
    for ($i = 0; $i -lt $items.Count; $i++) {
        $sel = ''
        try { $sel = $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { }
        Say @("  [$i] 「$($items[$i].Current.Name)」 选中=$sel")
        if ($i -eq $PickIndex) { $pick = $items[$i] }
    }
    if ($ReadOnly) {
        $cur = @($items | Where-Object { try { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false } })
        Say @("-ReadOnly：盘上 FormatIndex 应当对应的那一项 = 「$(if ($cur.Count) { $cur[0].Current.Name } else { '★ 没有一项被选中' })」（共 $($cur.Count) 项被选中）")
        try { $ec.Collapse() } catch { }
        Say @('探针结论见上面读数（-ReadOnly 不改动任何界面状态）')
        return
    }
    if (-not $pick) { Say @("★ 展开后数到 $($items.Count) 项，取不到索引 $PickIndex ⇒ 未判"); $ec.Collapse(); exit 2 }
    try {
        $pick.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Say @("已 Select 第 $PickIndex 项「$($pick.Current.Name)」 ⇒ 不是 Invoke（WinUI 的 NavigationViewItem 是 ListItem，取 Invoke 会静默不干活）")
    } catch { Say @("★ Select 抛错: $($_.Exception.Message)"); exit 2 }
    Start-Sleep -Milliseconds 700
    try { $ec.Collapse() } catch { }

    if ($FireCapture) {
        $hk = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
            Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+Q)' } | Select-Object -First 1)
        $chord = if ($hk) { ([regex]'已注册:\s*(Ctrl\S*\+Q)').Match($hk[0]).Groups[1].Value } else { 'Ctrl+Shift+Q' }
        Say @("送 $chord（$(if ($hk) { '日志现读' } else { '默认，未证实' })）；按前前台窗 $([PF]::Fg())")
        if ($chord -eq 'Ctrl+Shift+Q') { [PF]::Chord(0x11, 0x10, 0x51) } else { [PF]::Chord(0x11, 0x12, 0x51) }
        $dl = (Get-Date).AddSeconds(30); $done = $null
        while ((Get-Date) -lt $dl -and -not $done) {
            $hit = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
                Where-Object { $_ -match '捕获配置|编码完成' })
            if ($hit.Count -ge 2) { $done = $hit } else { Start-Sleep -Milliseconds 500 }
        }
        Say @("产品自述（本次会话）：")
        foreach ($l in @($hit | Select-Object -Last 6)) { Say @("  | $($l.Substring(0, [Math]::Min(150, $l.Length)))") }
    }
    Say @('探针结论见上面四问的读数')
} catch {
    Say @("★ 探针异常：$($_.Exception.Message)")
} finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force; Say @("已停止本次起的 PID=$($proc.Id)") } catch { } }
}
exit 0
