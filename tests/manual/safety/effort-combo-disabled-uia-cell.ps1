# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 压缩强度下拉对 QSV/MFT 后端"渲染成禁用并点名"的**进程外 UIA 验收格**。
#
# 为什么静态判据不够：CorePipelineTests.cs:4824-4832 那两条（⑥/⑦）钉的是
#   ① `MainWindow.xaml.cs` 里有 `AvifEncoderCapabilities.EffortApplies(kind)` 这次调用；
#   ② 界面字面量与 LocaleManager 的键逐字相同。
# 两条都在源码文本层 ⇒ 能证明接线在，**不能证明跑起来那根下拉真的灰了、那句理由真的上屏了**。
# WinUI 的 `IsEnabled` 落到 XAML 属性系统后还有一次样式/模板层的机会把它盖回去（同族先例：
# w9-combo-items-en-cell.ps1 的 phantom 项 —— SetComboByTag 会补一项出来，静态表里根本没有）。
# 所以这一格只干一件事：从进程外用 UIA 读**渲染后的事实**。
#
# 两条已经踩过的坑，这一格按坑的口径写（见下面 C1/C2/C3 三条读数的分工）：
#   · 坑一：**WinUI 的 IsEnabled=false 不挡命中测试**，而 ScrollIntoView 一个孤立项还会把
#     那个容器**重新点亮**。⇒ "我点得动 / 我能 Expand" **不是**"它是启用的"。
#     本格的判据只有 UIA 的 `IsEnabled` 属性本身；Expand 与 ScrollIntoView 的结果一律记成
#     **对照**（证明这台仪器的读数不来自"能不能点"），不参与判定。
#   · 坑二：产品可以收进托盘，托盘态下任何读数都不该让判据随**焦点**变。
#     ⇒ 本格自己 ShowWindow 把窗抬起来，并**先证明它在屏上**再读；全程不调用
#     SetForegroundWindow、不点鼠标、不发键盘。前台窗是谁只记进日志，不进任何判定。
#   · 本机桌面同时有 4 个顶层窗 ⇒ 只按 NativeWindowHandle 或只按 PID 都可能选错对象
#     （WinUI3 的 MainWindowHandle 恒 0，托盘态还能留下 0 子树的幽灵窗）。
#     ⇒ 匹配三件一起用：PID + Name（窗标题）+ ClassName，再用子树元素数消歧，
#     并把最终选中的句柄回读一次 `GetWindowThreadProcessId` 证明它确实属于本 PID。
#
# 纪律：会改界面选择 ⇒ 起手按【字节】备份 dev 那份设置并记 sha256，收尾还原后复算必须逐字相同；
#       只杀自己起的那个 PID；任何一根目标控件不可达就记**未判**（RC=2），
#       不许把"没看见"读成"没禁用"。哈希走 .NET 直取，不 dot-source ps-compat.ps1，
#       也不用 Get-FileHash（PSModulePath 遮蔽的坑见 scripts/ps-compat.ps1 头注）⇒ 本格不 dot-source 任何库，自包含。
#
# 用法（由维护者对新构建的 exe 亲手跑；本格的作者没有跑过产品，也没跑过任何构建）：
#   powershell -NoProfile -File tests\manual\safety\effort-combo-disabled-uia-cell.ps1
#   powershell -NoProfile -File ... -AttachPid <在跑的实例 PID>   # 产品有单实例守卫，别起第二个
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\precommit-2026-10-31\effort-combo-disabled-uia.log',
    [int]$SettleSec = 12,
    [int]$AttachPid = 0,
    [string]$WinName = 'TrueToneCap',
    # 界面语言两把名字都能认：XAML 里的中文 Content 与 LocaleManager 的英文映射逐条对上。
    [string[]]$AutoNames = @('自动', 'Auto'),
    [string[]]$QsvNames = @('Intel QSV / 系统 MFT', 'Intel QSV / System MFT')
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$logDir = Split-Path -Parent $Log
if (-not (Test-Path -LiteralPath $logDir)) { $null = New-Item -ItemType Directory -Force -Path $logDir }
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 压缩强度下拉禁用 · UIA 验收 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

# 判定状态：Fail = 判出来了且不合格；Undecided = 判不了（一律非零退出，不许当"没问题"）。
$Fail = 0; $Undecided = 0
function Add-Fail([string]$why) { $script:Fail++; Say @("★ 不合格：$why") }
function Add-Undecided([string]$why) { $script:Undecided++; Say @("★ 未判：$why") }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EFF
{
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static string Fg()
    {
        IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
        return h + "/pid=" + pid;
    }
    public static string OwnedRect(IntPtr h)
    {
        RECT r; if (!GetWindowRect(h, out r)) return "GetWindowRect 失败";
        return (r.R - r.L) + "x" + (r.B - r.T) + "@" + r.L + "," + r.T;
    }
}
'@
$auto = [System.Windows.Automation.AutomationElement]
$root = $auto::RootElement
$TSDesc = [System.Windows.Automation.TreeScope]::Descendants
$CBType = [System.Windows.Automation.ControlType]::ComboBox
$LIType = [System.Windows.Automation.ControlType]::ListItem

# UIA 对"此刻没有有效布局"的窗（托盘隐藏 / 尚未实化）给 ±∞ 或 NaN，[int] 转换直接抛
# —— w1-probe-uia.ps1:58-70 踩过。仪器不许把待判的读数炸掉。
function Fmt-Rect($r) {
    $bad = [double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y) -or
           [double]::IsInfinity($r.Width) -or [double]::IsInfinity($r.Height) -or
           [double]::IsNaN($r.X) -or [double]::IsNaN($r.Y) -or
           [double]::IsNaN($r.Width) -or [double]::IsNaN($r.Height)
    if ($bad) { return '不可得(∞/NaN：该窗此刻没有有效布局)' }
    return "$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height)"
}
function Count-Desc($el) {
    try { return @($el.FindAll($TSDesc, [System.Windows.Automation.Condition]::TrueCondition)).Count } catch { return -1 }
}
function Find-ById($scope, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)
    try { return $scope.FindFirst($TSDesc, $cond) } catch { return $null }
}
# 判据唯一来源：UIA IsEnabled 属性。取不到就是未判，绝不退化成"能不能点"。
function Get-IsEnabled([string]$id) {
    $el = Find-ById $main $id
    if ($el -is [System.Windows.Automation.AutomationElement]) {
        try { return [pscustomobject]@{ Ok = $true; El = $el; En = [bool]$el.Current.IsEnabled;
                                        Off = [bool]$el.Current.IsOffscreen; Class = $el.Current.ClassName } } catch { }
    }
    return [pscustomobject]@{ Ok = $false; El = $null; En = $null; Off = $null; Class = '' }
}
function Sha256-Bytes([byte[]]$b) {
    $h = [System.Security.Cryptography.SHA256]::Create()
    try { ($h.ComputeHash($b) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $h.Dispose() }
}
# 展开一个下拉并在【它自己的子树里】按候选名选一项（全窗数项会串台，见 w1-probe-uia.ps1:129-133）。
# 返回轮前选中项名；选不到返回 $null 让调用方记未判。
function Select-ByNames($cbo, [string[]]$names, [string]$label) {
    if ($cbo -eq $null) { Add-Undecided "$label 这个下拉本身不在树里"; return $null }
    $ec = $null
    try { $ec = $cbo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) }
    catch { Add-Undecided "$label 取不到 ExpandCollapse 模式：$($_.Exception.Message)"; return $null }
    $items = @()
    try { $ec.Expand() } catch { Add-Undecided "$label Expand 抛：$($_.Exception.Message)"; return $null }
    Start-Sleep -Milliseconds 900
    # 轮前那一项：收尾要选回去
    $prior = ''
    $pre = @($cbo.FindAll($TSDesc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $LIType))))
    foreach ($p in $pre) {
        try { if ($p.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { $prior = $p.Current.Name; break } } catch { }
    }
    $items = @($cbo.FindAll($TSDesc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $LIType))))
    $namesList = ($items | ForEach-Object { "「$($_.Current.Name)」" }) -join ' '
    Say @("$label 展开后可达 ListItem $($items.Count) 个：$namesList")
    $hit = $null
    # 逐字相等在本机匹配不上：QSV 那一项在能力探测不可用时会被界面追加后缀
    #（实得「Intel QSV / 系统 MFT（本机不可用）」）⇒ 改按前缀认，并把后缀原样记进日志，
    # 免得下一次又把它读成"这一项不存在"而报假指控（2026-10-31 就错过一次）。
    foreach ($n in $names) {
        foreach ($it in $items) {
            $nm = $it.Current.Name
            if ($nm -eq $n -or $nm.StartsWith($n, [System.StringComparison]::Ordinal)) { $hit = $it; break }
        }
        if ($hit -ne $null) { break }
    }
    if ($hit -ne $null -and $hit.Current.Name -ne ($names | Where-Object { $hit.Current.Name.StartsWith($_) } | Select-Object -First 1)) {
        Say @("  （认到的是带后缀的形态：「$($hit.Current.Name)」）")
    }
    if ($hit -eq $null) { try { $ec.Collapse() } catch { }; Add-Undecided "$label 里没有候选名（$($names -join ' / ')）；本机 QSV 项可能被 HDR 收起或被能力表摘掉"; return $null }
    try { $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch { try { $ec.Collapse() } catch { }; Add-Undecided "$label Select「$($hit.Current.Name)」抛：$($_.Exception.Message)"; return $null }
    Start-Sleep -Milliseconds 700
    try { $ec.Collapse() } catch { }
    Say @("$label 已选到「$($hit.Current.Name)」")
    return $prior
}

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$setBak = $null; $setHash0 = ''
if (Test-Path -LiteralPath $set) {
    $bytes = [IO.File]::ReadAllBytes($set); $setHash0 = Sha256-Bytes $bytes
    $setBak = Join-Path $env:TEMP ("ttc-effortcell-" + [Guid]::NewGuid().ToString('N') + ".settings.json")
    [IO.File]::WriteAllBytes($setBak, $bytes)
    Say @("盘上设置已按字节备份 sha256=$setHash0 → $setBak")
} else { Say @('⚠ 没有 settings.json（本格仍要跑：禁用是渲染事实，与盘上键无关）') }

$proc = $null; $pidT = 0; $owned = $false; $main = $null; $hSelected = [IntPtr]::Zero
try {
    if ($AttachPid -gt 0) {
        $pidT = $AttachPid; $owned = $false
        Say @("挂到已有实例 PID=$pidT（产品有单实例守卫，另起只会得到一个立刻退出的进程）")
    } else {
        if (-not (Test-Path -LiteralPath $Exe)) { Add-Undecided "找不到 $Exe"; throw 'stop' }
        $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
        $pidT = $proc.Id; $owned = $true
        Say @("PID=$pidT，等 $SettleSec s")
    }
    Start-Sleep -Seconds $SettleSec

    # ── ① 选窗：PID + Name + ClassName 三件一起用，句柄只做消歧后的记账 ──
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $pidT)
    $tops = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    Say @("按 PID 枚举到顶层窗 $($tops.Count) 个（本机桌面另有别的进程的窗，所以不拿句柄当筛选主键）：")
    foreach ($w in $tops) {
        Say @("  h=$($w.Current.NativeWindowHandle) class<$($w.Current.ClassName)> name=「$($w.Current.Name)」 rect=$(Fmt-Rect $w.Current.BoundingRectangle) 子树=$(Count-Desc $w)")
    }
    $named = @($tops | Where-Object { $_.Current.Name -eq $WinName })
    if ($named.Count -eq 0) { Add-Undecided "本 PID 下没有 Name=「$WinName」的顶层窗"; throw 'stop' }
    Say @("Name 命中 $($named.Count) 个 ⇒ 再按 WinUI 类名收一遍")
    $cls = @($named | Where-Object { $_.Current.ClassName -match 'WinUI' })
    if ($cls.Count -eq 0) { $cls = $named; Say @('  ⚠ 一个类名含 WinUI 的都没有 ⇒ 退回用 Name 命中集（记进日志，不放宽判定）') }
    $best = -1
    foreach ($w in $cls) { $c = Count-Desc $w; if ($c -gt $best) { $best = $c; $main = $w } }
    if ($main -eq $null -or $best -le 0) { Add-Undecided "候选窗子树元素数=$best ⇒ 没有一个是真的主窗"; throw 'stop' }
    $hSelected = [IntPtr]$main.Current.NativeWindowHandle
    $refPid = [uint32]0
    $own = [EFF]::GetWindowThreadProcessId($hSelected, [ref]$refPid)
    Say @("选中主窗 h=$hSelected class<$($main.Current.ClassName)> 子树=$best；GetWindowThreadProcessId 回读=$own")

    # ── ② 先证明在屏上，再读任何东西（不依赖焦点：全程不 SetForegroundWindow） ──
    $onScreen = $false
    for ($try = 1; $try -le 4 -and -not $onScreen; $try++) {
        $r = $main.Current.BoundingRectangle
        $min = [EFF]::IsIconic($hSelected)
        Say @("第 $try 次：rect=$(Fmt-Rect $r) offscreen=$($main.Current.IsOffscreen) IsIconic=$min 可见=$([EFF]::IsWindowVisible($hSelected)) 前台窗=$([EFF]::Fg())")
        if ($r.Width -lt 200 -or $main.Current.IsOffscreen -or $min) {
            $null = [EFF]::ShowWindow($hSelected, 9)   # SW_RESTORE：最小化/托盘隐藏态都走这一条
            Start-Sleep -Milliseconds 1200
            $null = [EFF]::ShowWindow($hSelected, 5)   # SW_SHOW：只点亮可见性，不动 z 序以外的东西
            Start-Sleep -Milliseconds 800
            $r = $main.Current.BoundingRectangle
        }
        $fresh = $null
        foreach ($w in @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
            if ([IntPtr]$w.Current.NativeWindowHandle -eq $hSelected) { $fresh = $w }
        }
        if ($fresh -ne $null) { $main = $fresh }
        $r2 = $main.Current.BoundingRectangle
        $finite = -not ([double]::IsInfinity($r2.Width) -or [double]::IsNaN($r2.Width))
        $onScreen = ($finite -and $r2.Width -ge 200 -and -not $main.Current.IsOffscreen -and [EFF]::IsWindowVisible($hSelected))
    }
    if (-not $onScreen) { Add-Undecided "ShowWindow 四次之后窗仍不在屏上 ⇒ 任何 UIA 读数都不可信"; throw 'stop' }
    Say @("✅ 在屏证据成立：rect=$(Fmt-Rect $main.Current.BoundingRectangle) offscreen=$($main.Current.IsOffscreen) IsIconic=$([EFF]::IsIconic($hSelected)) 可见=$([EFF]::IsWindowVisible($hSelected))")

    # ── ③ 前置：格式选到 AVIF，否则 AvifOptionsCard 是 Collapsed，EffortAvifCbo 根本不在树里 ──
    $fmt = Find-ById $main 'FormatCbo'
    if ($fmt -eq $null) { Add-Undecided 'AutomationId=FormatCbo 不可达'; throw 'stop' }
    $priorFmt = Select-ByNames $fmt @('AVIF') 'FormatCbo'
    if ($priorFmt -eq $null) { throw 'stop' }

    $codec = Find-ById $main 'AvifCodecCbo'
    if ($codec -eq $null) { Add-Undecided 'AutomationId=AvifCodecCbo 不可达（AVIF 选项卡没进树？）'; throw 'stop' }

    # ── C0 前置：把 HDR 关掉，否则「Intel QSV / 系统 MFT」这一项不在下拉里 ──
    # MainWindow.xaml.cs:937 按 SupportsHdrPath(kind) 收项，而 QSV 的 HDR 支持是 false ⇒
    # HDR 开着时那一项被藏起来，UIA 的 FindAll 看不见它。本机第一跑正是这种形态：
    # 候选名里没有 QSV ⇒ 本格把"没换成后端"读成"界面没渲染禁用"，报了两句假指控。
    # ⇒ 开着就关、收尾还原；这条前置本身不进判据。
    $hdrSw = Find-ById $main 'HdrSwitch'
    $hdrWasOn = $false
    if ($hdrSw -eq $null) { Add-Undecided 'AutomationId=HdrSwitch 不可达 ⇒ 没法把下拉摆回"QSV 可见"的形态'; throw 'stop' }
    try {
        $tg = $hdrSw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $hdrWasOn = ($tg.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        if ($hdrWasOn) {
            $tg.Toggle(); Start-Sleep -Milliseconds 800
            Say @('C0 前置：HDR 原为开 ⇒ 关掉（QSV/MFT 项只在 SDR 下露出来），收尾还原')
        } else { Say @('C0 前置：HDR 已是关 ⇒ 下拉里应当能看到 QSV/MFT 项') }
    } catch { Add-Undecided "HdrSwitch 取不到 Toggle 模式：$($_.Exception.Message)"; throw 'stop' }

    # ── C1 正控：Auto 档必须读到 IsEnabled=true。读不到 true 说明这台仪器自己就瞎了 ⇒ 未判，不判合格 ──
    $priorEnc = Select-ByNames $codec $AutoNames 'AvifCodecCbo(Auto)'
    if ($priorEnc -eq $null) { throw 'stop' }
    $c1 = Get-IsEnabled 'EffortAvifCbo'
    if (-not $c1.Ok) { Add-Undecided 'AutomationId=EffortAvifCbo 不可达 ⇒ 渲染层读不到，本格什么都没判'; throw 'stop' }
    Say @("C1 正控 Auto：EffortAvifCbo IsEnabled=$($c1.En)（类名<$($c1.Class)> offscreen=$($c1.Off)）")
    if (-not $c1.En) { Add-Undecided 'C1 正控不合格：Auto 档就读到 false ⇒ 这台仪器分不清"禁用"与"永远 false"，后续读数作废'; throw 'stop' }
    Say @('C1 ✅ Auto 档 IsEnabled=true（说明下面那次 false 是后端换来的，不是元素天生/取错对象）')

    # ── C2 判据：选到 QSV/MFT 之后，UIA IsEnabled 必须变 false ──
    # ⚠ 目标后端选不到时必须【停判】，不许接着往下读：本机第一跑就是这里出错的形态 ——
    #   这台机器是 RTX 5080 且 HDR 开着，下拉里没有「Intel QSV / 系统 MFT」这一项
    #   （MainWindow.xaml.cs:937 按 SupportsHdrPath 收项，QSV 的 HDR 支持是 false），
    #   Select-ByNames 已经记了未判，而老写法把 $null 丢掉继续判 ⇒ 报出
    #   "界面没把禁用真的渲染出来"这句【假指控】。判不了就说判不了。
    $qsvPrior = Select-ByNames $codec $QsvNames 'AvifCodecCbo(QSV/MFT)'
    if ($qsvPrior -eq $null) { throw 'stop' }
    $c2 = Get-IsEnabled 'EffortAvifCbo'
    if (-not $c2.Ok) { Add-Undecided '换档之后 EffortAvifCbo 反而不在树里 ⇒ 判不了'; throw 'stop' }
    Say @("C2 判据 QSV/MFT：EffortAvifCbo IsEnabled=$($c2.En)（offscreen=$($c2.Off)）")
    if ($c2.En) { Add-Fail 'QSV/MFT 档下 UIA IsEnabled 仍为 true ⇒ 界面没把禁用真的渲染出来' }
    else { Say @('C2 ✅ UIA IsEnabled=false —— 判据只看这一条属性') }

    # ── C3 对照（不参与判定）：坑一的两条实测形态 ──
    #    IsEnabled=false 不挡命中测试 ⇒ Expand 仍可能成功；ScrollIntoView 还可能把容器点亮。
    #    这两条读数一律记成"对照"，并显式声明它们不推翻 C2。
    $ecEl = $c2.El
    $expandNote = '取不到模式'
    try {
        $ecp = $ecEl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ecp.Expand(); Start-Sleep -Milliseconds 500
        $expandNote = "Expand 调用后状态=$($ecp.Current.ExpandCollapseState)"
        try { $ecp.Collapse() } catch { }
    } catch { $expandNote = "Expand 抛：$($_.Exception.Message)" }
    $afterScroll = '取不到 ScrollItem'
    try {
        $sip = $ecEl.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
        $sip.ScrollIntoView(); Start-Sleep -Milliseconds 600
        $re = Get-IsEnabled 'EffortAvifCbo'
        if ($re.Ok) { $afterScroll = "ScrollIntoView 之后 IsEnabled=$($re.En)" }
    } catch { }
    Say @("C3 对照（★不作判据★）：禁用态下 $expandNote；$afterScroll")
    Say @('  ↑ 坑一：这两条即使"看着能点/被点亮"，也不改变 C2 的 IsEnabled=false 判定；判据不取点击结果。')

    # ── C4 那句理由必须上屏（禁用要能说清为什么被禁） ──
    $hint = Find-ById $main 'AvifCodecHintTxt'
    if ($hint -eq $null) { Add-Undecided 'AutomationId=AvifCodecHintTxt 不可达 ⇒ 没法判"点名"那半条' }
    else {
        $txt = $hint.Current.Name
        $acc = ''
        try { $acc = $hint.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern).Current.Value } catch { }
        if ([string]::IsNullOrEmpty($txt)) { $txt = $acc }
        Say @("C4 读数：Name=「$txt」；LegacyIAccessible=「$acc」")
        if ([string]::IsNullOrWhiteSpace($txt)) { Add-Undecided 'C4 那句提示的 Name 与 LegacyIAccessible 都读空 ⇒ 判不了"点名"' }
        else {
            $okZh = $txt.Contains('该后端没有压缩强度入口')
            $okEn = $txt.Contains('no compression-effort control')
            if ($okZh -or $okEn) { Say @("C4 ✅ 后缀已渲染（$(if ($okZh) { '中文口径' } else { '英文口径' })）") }
            else { Add-Fail 'QSV/MFT 档下界面提示里没有那句"该后端没有压缩强度入口 / no compression-effort control" ⇒ 灰了但没点名' }
        }
    }

    # ── 收尾：把两处前置选择选回轮前那一项，并复算设置字节 ──
    # 把 C0 关掉的 HDR 摆回去（"收尾还原"这句承诺得真做；盘上字节另有还原，这里补的是界面态）
    if ($hdrWasOn) {
        try {
            $tg2 = (Find-ById $main 'HdrSwitch').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tg2.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
                $tg2.Toggle(); Start-Sleep -Milliseconds 600
                Say @('还原：HDR 已摆回开')
            } else { Say @('还原：HDR 本就是开（中途被别处摆回）') }
        } catch { Say @("★ 还原 HDR 抛：$($_.Exception.Message)（不另立判据，盘上字节仍有按字节还原兜底）") }
    }
    if ($priorEnc) { Select-ByNames (Find-ById $main 'AvifCodecCbo') @($priorEnc) '还原 AvifCodecCbo' | Out-Null }
    $back = Get-IsEnabled 'EffortAvifCbo'
    if ($back.Ok) { Say @("还原后 EffortAvifCbo IsEnabled=$($back.En)（应当回到 True；只记账，不另立判据）") }
    if ($priorFmt) { Select-ByNames (Find-ById $main 'FormatCbo') @($priorFmt) '还原 FormatCbo' | Out-Null }
} catch {
    if ($Fail -eq 0 -and $Undecided -eq 0) { Add-Undecided "流程抛出：$($_.Exception.Message)" }
    else { Say @("流程在判定之后抛出：$($_.Exception.Message)") }
} finally {
    if ($setBak -and (Test-Path -LiteralPath $set) -and (Test-Path -LiteralPath $setBak)) {
        $hashNow = Sha256-Bytes ([IO.File]::ReadAllBytes($set))
        if ($hashNow -ne $setHash0) {
            [IO.File]::WriteAllBytes($set, [IO.File]::ReadAllBytes($setBak))
            $hashAfter = Sha256-Bytes ([IO.File]::ReadAllBytes($set))
            Say @("设置被本次改选动过（$hashNow）⇒ 已还原，复算 $hashAfter $(if ($hashAfter -eq $setHash0) { '= 起手值 ✅' } else { '★ 仍不同，人工核对' })")
        } else { Say @('设置字节逐字未变 ✅') }
        try { Remove-Item -LiteralPath $setBak -Force } catch { }
    }
    if ($owned -and $proc) { try { Stop-Process -Id $proc.Id -Force; Say @("已停本次起的 PID=$($proc.Id)") } catch { } }
    Say @("末态：Fail=$Fail Undecided=$Undecided 选中窗句柄=$hSelected")
}
if ($Undecided -gt 0) { Say @('RC=2 未判（有一格什么都没判出来，不许读成通过）'); exit 2 }
if ($Fail -gt 0) { Say @('RC=1 判出不合格'); exit 1 }
Say @('RC=0 C1 正控 + C2 判据 + C4 点名 全部成立'); exit 0
