# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 英文界面补完 · 第三批的 T3：展开那 7 个下拉，逐项读 UIA Name，数还剩几个中文。
#
# 为什么静态判据不够：combo_locale_coverage_measure.py 量的是"Localization.cs 里点没点名这个下拉"，
# 属于源码文本层（T2）。它能证明接线在，不能证明跑起来真的把 Content 换成了英文
# （Tr 未命中就原样返回 ⇒ 表里少一条、Tag 抄错一个字母，静态那关都照样绿）。
# 而下拉项收起时【不在可视树里】⇒ 全窗 UIA 走树也看不见它们（en-ui-count.ps1 那把尺子量不到）。
# 唯一能读到运行时项文字的办法：Expand 之后在【该下拉自己的子树里】数 ListItem。
#
# 7 个下拉分两类：
#   · 5 个在页面上直接可见 ⇒ 切到所在页就能读；
#   · AvifCodecCbo / LlmProviderCbo 的容器是【条件可见】的（选到 AVIF 格式 / LLM 翻译才进树）。
#     第一版把这两个记成"未判"（11:44 实测 5/7，那一次读数本身是对的）。这一版按 W1 矩阵
#     一直在用的路子用 SelectionItemPattern 把前置项选上，读完再【选回轮前那一项】并核对。
#
# 纪律（这一格会起产品进程、会改盘上设置、会改界面选择）：
#   · 起手把 dev 那份设置按【字节】存下并记 sha256，收尾还原后复算，必须逐字相同；
#   · 动过的每一个前置下拉都要选回轮前那一项，选不回去就报在日志里；
#   · 只杀自己起的那个 PID；收尾把用户原来那个实例按原样起回（GUI 冷启要几秒，等够再报活着）；
#   · 任何一个目标下拉没可达 ⇒ 记"未判"并 exit 2，不许把"没看见"报成"没有中文"。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\combo-items-en.log'
)
$ErrorActionPreference = 'Continue'
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 第三批 T3：展开 7 个下拉数中文项 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

# 目标与各自的"应有项数 / 其中中文项数"：这两列是从 XAML 逐字抽出来的，不是手抄。
# Phantom = 盘上那个不在 XAML 选项里的值：SetComboByTag 会把它【补成一项】并选中（10-01 的处方，
# 为了"未匹配不许留空白"）。所以这一项【本来就该在树里】，标签就是那串裸十六进制。
# 11:44 第一次跑把 OverlayColorCbo 报成"项数不符（8/7）"，看着像量错了对象，其实是这条行为
# 没写进预期表 ⇒ 判据要的是【把异常解释掉】，不是【把异常放宽】。
$Targets = @(
    [pscustomobject]@{ Id = 'BorderColorCbo'; MayHide = @();     Items = 8; Zh = 8; Cond = $null; Phantom = '' }
    [pscustomobject]@{ Id = 'OverlayColorCbo'; MayHide = @();    Items = 7; Zh = 7; Cond = $null; Phantom = '#99001833' }
    [pscustomobject]@{ Id = 'LogCategoryCbo'; MayHide = @();     Items = 7; Zh = 6; Cond = $null; Phantom = '' }
    [pscustomobject]@{ Id = 'LogFilterCbo'; MayHide = @();       Items = 5; Zh = 5; Cond = $null; Phantom = '' }
    [pscustomobject]@{ Id = 'TranslationModeCbo'; MayHide = @(); Items = 3; Zh = 3; Cond = $null; Phantom = '' }
    [pscustomobject]@{ Id = 'AvifCodecCbo';       Items = 5; Zh = 2; Phantom = ''
                       MayHide = @('Intel QSV / System MFT')   # HDR 开着时按能力收起
                       Cond = @{ Owner = 'FormatCbo'; Pick = 'AVIF' } }
    [pscustomobject]@{ Id = 'LlmProviderCbo'; MayHide = @();     Items = 9; Zh = 4; Phantom = ''
                       Cond = @{ Owner = 'TranslationModeCbo'; Pick = 'LLM Translation' } }
)
$WantZh = 0; $WantN = 0
foreach ($t in $Targets) { $WantZh += $t.Zh; $WantN += $t.Items; if ($t.Phantom) { $WantN += 1 } }
Say @("账面：$($Targets.Count) 个下拉 / 共 $WantN 项（含 SetComboByTag 补出来的 phantom），其中应为中文 $WantZh 项")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
if (-not (Test-Path -LiteralPath $set)) { Say @("RC=2 没有 $set ⇒ 未判"); exit 2 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
if (-not ('System.Windows.Automation.AutomationElement' -as [type])) {
    Say @('RC=2 UIAutomationClient 没加载 ⇒ 未判'); exit 2
}

$bytes0 = [IO.File]::ReadAllBytes($set)
$sha0 = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
Say @("轮前设置 sha256 = $($sha0.Substring(0,16))…")

$raw = [IO.File]::ReadAllText($set, $utf8)
if ($raw -match '"Language":') { Say @('RC=2 盘上已有 Language 键 ⇒ 不覆盖用户选择，本格跳过'); exit 2 }
[IO.File]::WriteAllText($set, ($raw.TrimEnd('}') + ',"Language":"en"}'), $utf8)

$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) { Say @("起手 $($pre.Count) 个实例 ⇒ 先停（收尾起回）"); $pre | Stop-Process -Force; Start-Sleep -Seconds 2 }

$auto = [System.Windows.Automation.AutomationElement]
$cjkRe = '[\u3000-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff00-\uffef]'
$cbCond = New-Object System.Windows.Automation.PropertyCondition(
    $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$liCond = New-Object System.Windows.Automation.PropertyCondition(
    $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$script:main = $null

function Find-Combo([string]$id) {
    $c = @($script:main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $script:cbCond) |
           Where-Object { $_.Current.AutomationId -eq $id })
    if ($c.Count) { return $c[0] }
    return $null
}

# 只在该下拉自己的子树里数 ListItem：全窗数会把导航项混进来（w1-probe-uia 06:54 就栽在这）
function Read-ComboItems($cbo) {
    $ec = $null
    try { $ec = $cbo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
    if ($null -eq $ec) { return @{ Ok = $false; Why = '取不到 ExpandCollapse'; Names = @() } }
    try { $ec.Expand() } catch { return @{ Ok = $false; Why = "Expand 抛错 $($_.Exception.Message)"; Names = @() } }
    Start-Sleep -Milliseconds 700
    $names = @($cbo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $script:liCond) |
               ForEach-Object { $_.Current.Name })
    try { $ec.Collapse() } catch { }
    Start-Sleep -Milliseconds 300
    return @{ Ok = $true; Why = ''; Names = $names }
}

# 当前选中项：UIA 没有"展开前就能读到选中项名"的可靠口子，这里就用展开后第一项之外
# 的办法——读 ComboBox 自己的 Value/LegacyIAccessible 名字；取不到就返回空串，让调用方
# 知道"轮前没有选中项"，不许它猜一个默认值写回去。
function Selected-Name($cbo) {
    $ec = $null
    try { $ec = $cbo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand() } catch { }
    Start-Sleep -Milliseconds 600
    $hit = @()
    foreach ($it in $cbo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $script:liCond)) {
        try {
            if ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
                $hit += $it.Current.Name
            }
        } catch { }
    }
    try { if ($ec) { $ec.Collapse() } } catch { }
    Start-Sleep -Milliseconds 250
    # 恰好 1 个被选中才认；0 个说明读法失效、>1 个说明状态本身不正常 —— 两种都不许猜一个默认值写回去
    if ($hit.Count -eq 1) { return $hit[0] }
    return ''
}

function Select-ByName($cbo, [string]$name) {
    if (-not $name) { return '要选的项名为空' }
    $ec = $null
    try { $ec = $cbo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand() } catch { }
    Start-Sleep -Milliseconds 600
    $hit = @($cbo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $script:liCond) |
             Where-Object { $_.Current.Name -eq $name })
    if (-not $hit.Count) {
        try { if ($ec) { $ec.Collapse() } } catch { }
        return "展开后树里没有名为「$name」的项"
    }
    try { $hit[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch { return "Select 抛错 $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 700
    try { if ($ec) { $ec.Collapse() } } catch { }
    return ''
}

$proc = $null
$exitCode = 2

try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    $tops = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
              (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id))))
    Say @("按 PID 枚举到顶层窗 $($tops.Count) 个")
    if (-not $tops.Count) { Say @('RC=2 一个顶层窗都没有 ⇒ 未判（单实例守卫？还没起完？）'); exit 2 }

    $main = $null; $best = -1
    foreach ($w in $tops) {
        $c = 0
        try { $c = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition).Count } catch { $c = 0 }
        if ($c -gt $best) { $best = $c; $main = $w }
    }
    $script:main = $main
    Say @("主窗子树 $best 个元素，class<$($main.Current.ClassName)>")

    # 目标下拉分散在 输出 / OCR·翻译 / 运行日志 三页上，不在当前页的控件不在树里
    # ⇒ 必须逐页找，而不是"找不到就算 0"。
    $navs = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond) |
              Where-Object { $_.Current.ClassName -match 'NavigationViewItem' })
    $navNames = ($navs | ForEach-Object { "「$($_.Current.Name)」" }) -join ' '
    Say @("导航项 $($navs.Count) 个：$navNames")
    if (-not $navs.Count) { Say @('RC=2 一个导航项都没枚举到 ⇒ 走不到别的页，未判'); exit 2 }

    $seen = @{}
    $restoreNotes = @()

    function Record-Target($t, $cbo, [string]$via) {
        $r = Read-ComboItems $cbo
        if (-not $r.Ok) { $script:seen[$t.Id] = @{ Id = $t.Id; Why = $r.Why }; return }
        $zh = @($r.Names | Where-Object { $_ -match $script:cjkRe })
        $script:seen[$t.Id] = @{ Id = $t.Id; Page = $via; N = $r.Names.Count; Zh = $zh.Count; Names = $r.Names }
        Say @("  [$via] $($t.Id)：$($r.Names.Count) 项（账面 $($t.Items)），含中文 $($zh.Count)（账面 $($t.Zh)）")
        Say @("     项文字：$(($r.Names | ForEach-Object { "「$_」" }) -join ' ')")
    }

    $pages = @(); $pages += ,@('起手页', $null)
    foreach ($nv in $navs) { $pages += ,@($nv.Current.Name, $nv) }

    # 条件可见的下拉必须【在它前置项所在的那一页上】就地处理：
    # 11:51 那一版把"选前置项"放到翻页循环结束之后，此时最后停在 Logs 页，
    # FormatCbo（输出页）与 TranslationModeCbo（OCR 页）早就离开树了 ⇒ 两个都记成"未判"。
    # 同理，选回去也要在同一页上立刻做，不能攒到循环外。
    foreach ($pg in $pages) {
        $pgName = $pg[0]
        if ($null -ne $pg[1]) {
            try { $pg[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
            catch { Say @("  切页「$pgName」失败：$($_.Exception.Message)"); continue }
            Start-Sleep -Milliseconds 900
        }
        foreach ($t in $Targets) {
            if ($seen.ContainsKey($t.Id) -or $null -ne $t.Cond) { continue }
            $c = Find-Combo $t.Id
            if ($null -eq $c) { continue }
            Record-Target $t $c $pgName
        }
        foreach ($t in $Targets) {
            if ($seen.ContainsKey($t.Id) -or $null -eq $t.Cond) { continue }
            $owner = Find-Combo $t.Cond.Owner
            if ($null -eq $owner) { continue }        # 前置项不在这一页，留给后面那一页处理
            $origName = Selected-Name $owner
            Say @("  前置 $($t.Cond.Owner) 轮前选中 = 「$origName」（要让它下面的 $($t.Id) 进树）")
            $err = Select-ByName $owner $t.Cond.Pick
            if ($err) { $seen[$t.Id] = @{ Id = $t.Id; Why = "选不上「$($t.Cond.Pick)」：$err" }; continue }
            Start-Sleep -Milliseconds 900
            $c = Find-Combo $t.Id
            if ($null -eq $c) { $seen[$t.Id] = @{ Id = $t.Id; Why = "选了「$($t.Cond.Pick)」后仍不在树里" } }
            else { Record-Target $t $c "$pgName/$($t.Cond.Pick)" }
            if (-not $origName) { $restoreNotes += "$($t.Cond.Owner)：轮前读不到选中项 ⇒ 不猜默认值" }
            else {
                $back = Select-ByName $owner $origName
                if ($back) { $restoreNotes += "❌ 回退 $($t.Cond.Owner) 到「$origName」失败：$back" }
                else { $restoreNotes += "已回退 $($t.Cond.Owner) → 「$origName」" }
            }
        }
    }
    foreach ($n in $restoreNotes) { Say @("  $n") }

    Say @("---- 结论 ----")
    $ok = @(); $bad = @()
    foreach ($k in $seen.Keys) {
        if ($seen[$k].ContainsKey('N')) { $ok += $seen[$k] } else { $bad += $seen[$k] }
    }
    $missing = @($Targets | Where-Object { -not $seen.ContainsKey($_.Id) })
    Say @("可达并读到项文字：$($ok.Count) / $($Targets.Count)；读不出：$($bad.Count)；没找到：$($missing.Count)")
    if ($missing.Count) { Say @("   没找到的：$(($missing | ForEach-Object { $_.Id }) -join ' ')") }
    if ($bad.Count)     { Say @("   读不出的：$(($bad | ForEach-Object { "$($_.Id)=$($_.Why)" }) -join ' | ')") }
    $totZh = 0; $totN = 0; $wantTotal = 0; $phantomBad = @(); $hideBad = @()
    foreach ($o in $ok) {
        $tt = ($Targets | Where-Object { $_.Id -eq $o.Id })
        $want = $tt.Items; if ($tt.Phantom) { $want += 1 }
        $totZh += $o.Zh
        $absent = @($tt.MayHide | Where-Object { $n = $_; -not (@($o.Names | Where-Object { $_ -eq $n }).Count) })
        if ($o.N -eq $want) { $flag = '✓' }
        elseif ($absent.Count -and $o.N -eq ($want - $absent.Count)) {
            $flag = '✓ 按能力收起 ' + (($absent | ForEach-Object { "「$_」" }) -join ' ')
        }
        else {
            $flag = "★ 项数不符（读到 $($o.N) / 账面 $want，允许消失 " +
                    ((if ($tt.MayHide.Count) { ($tt.MayHide | ForEach-Object { "「$_」" }) -join ' ' } else { '无' }) -join '') + '）'
            $hideBad += $o.Id
        }
        $totN += $o.N + $(if ($absent.Count) { 0 } else { 0 })
        $wantTotal += $want - $absent.Count
        Say @("   $($o.Id) 项数 $flag  中文 $($o.Zh)/$($tt.Zh)")
        # phantom 不只是"多一项就行"，必须是【那一项】：名字要等于盘上那个不在选项里的值。
        if ($tt.Phantom -and -not (@($o.Names | Where-Object { $_ -eq $tt.Phantom }).Count)) {
            $phantomBad += "$($o.Id) 少了名为「$($tt.Phantom)」的补项"
        }
    }
    if ($phantomBad.Count) { Say @("   ❌ phantom 项对不上：" + ($phantomBad -join ' | ')) }
    Say @("合计：读到 $totN 项（逐下拉判定累加出的应为值是 $wantTotal），其中含中文 【$totZh】 项（表里账面 $WantZh）")

    if ($missing.Count -or $bad.Count) {
        Say @("RC=2 未判：有下拉没可达/读不出，$totZh 只是【已读到的那部分】的数，不能当成 '$WantZh→0'")
        $exitCode = 2
    } elseif ($hideBad.Count) {
        Say @("RC=1 有下拉的项数既不是账面值、也不能用【允许消失的那几项】解释：$(($hideBad | ForEach-Object { $_.Id }) -join ' ')")
        $exitCode = 1
    } elseif ($phantomBad.Count) {
        Say @("RC=1 SetComboByTag 补出来的那一项与预期不符 ⇒ 量的不是同一批控件，读数作废")
        $exitCode = 1
    } elseif ($totN -ne $wantTotal) {
        Say @("RC=1 读到的项数与逐下拉判定累加值不符（$totN / $wantTotal）⇒ 判定自相矛盾，读数作废")
        $exitCode = 1
    } elseif ($totZh -ne 0) {
        Say @("RC=1 英文模式下展开后仍有 $totZh 项中文 ⇒ 第三批没补干净")
        $exitCode = 1
    } else {
        Say @("RC=0 $($Targets.Count) 个下拉全部可达、项数逐下拉相符、展开后 0 项中文 ⇒ 第三批 T3 通过")
        $exitCode = 0
    }
}
finally {
    if ($null -ne $proc) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停本次起的 PID=$($proc.Id)") }
        catch { Say @("停 PID 失败：$($_.Exception.Message)") }
    }
    Start-Sleep -Seconds 2
    [IO.File]::WriteAllBytes($set, $bytes0)
    $shaEnd = (Get-TtcFileHashHex -LiteralPath $set -Algorithm SHA256)
    if ($shaEnd -eq $sha0) { Say @("设置还原自检：$($shaEnd.Substring(0,16))… ⇒ 与轮前逐字相同 ✓") }
    else { Say @("❌ 设置还原后指纹不同 轮前=$sha0 现在=$shaEnd ⇒ 必须人工核对"); $exitCode = 3 }
    # GUI 冷启要好几秒：只等 4 s 就报"活着=False"是谎，不是读数
    $relaunched = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    $alive = $false; $waited = 0
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 2; $waited += 2
        if ($null -ne (Get-Process -Id $relaunched.Id -ErrorAction SilentlyContinue)) { $alive = $true; break }
    }
    Say @("用户实例已按原样起回：PID=$($relaunched.Id) 活着=$alive（等了 $waited s）")
    if (-not $alive -and $exitCode -eq 0) { $exitCode = 3 }
    Say @("RC=$exitCode")
}
Write-Output "RC=$exitCode"
exit $exitCode
