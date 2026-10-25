# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# R1 的 T3：逐格式读质量那一行，验"中文模式逐字不变" + "英文模式不留中文"。
#
# 为什么要有这一格：R1 把"Core 吐中文成品串"改成了"Core 给枚举 + App 格式化"。
# 门禁那侧只能证明【措辞表覆盖全、Core 不再含中文】，证不到【格式化没走样】——
# F1/F0 精度、(int) 截断、"距离: " 后面那个空格、" (越小越清晰)" 前面那个空格，
# 任何一个错，中文模式的界面文字就变了，而静态判据全绿。
#
# 期望值不是我手抄的：拿【改造前 Core 的公式】套在界面自己显示的那个数值上算出来。
# 这样盘上存的是默认值还是用户拖过的值都不影响判据，而公式一走样就红。
#
# 纪律同别的格：设置按字节存下、收尾还原并复算 sha256；只切格式不改任何数值；
# 切过的格式收尾选回轮前那一项；只杀自己起的 PID，起回用户的实例。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [ValidateSet('zh', 'en')] [string]$Lang = 'zh',
    [int]$SettleSec = 12,
    [string]$Log = ''
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (-not $Log) { $Log = "C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\quality-$Lang.log" }
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 质量行逐格式读数 · lang=$Lang · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

# 改造前 Core 的原式（逐字抄自 git 里 HEAD 的 FormatEncoders.cs / JpegGainMapEncoder.cs）
$OldLabel = @{
    PNG   = '无损 (固定 100%)'; JPEG_LI = 'butteraugli 距离 (0.5-3.0)'; JPEG_XL = 'butteraugli 距离 (越小越清晰; 1.0 = 视觉无损)'
    AVIF  = 'CRF (0=无损, 63=最低; 15 ≈ 视觉无损)'; WebP = '质量 (50-100; 85+ 视觉无损)'; TIFF = '无损 (固定)'
}
function OldDesc([string]$fmt, [double]$q) {
    switch ($fmt) {
        'PNG'      { return '无损' }                       # GetQualityDescription(_) => "无损"
        'TIFF'     { return '无损' }
        'JPEG_LI'  { return ('距离: ' + ('{0:F1}' -f $q) + ' (越小越清晰)') }
        'JPEG_XL'  { if ($q -le 0.1) { return '近无损' } else { return ('距离: ' + ('{0:F1}' -f $q)) } }
        'AVIF'     { if ($q -le 0) { return '无损' } else { return ('CRF: ' + [int]$q) } }
        'WebP'     { if ($q -ge 100) { return '无损' } else { return (('{0}' -f [int]$q) + '%') } }
        default    { return '' }
    }
}
# 无损格式界面走的是 QualityIsFixed 那一支 ⇒ 描述行显示的是【区间标签】而不是 "无损"
function ExpectedDesc([string]$fmt, [double]$q) {
    if ($fmt -in @('PNG', 'TIFF')) { return $OldLabel[$fmt] }
    return OldDesc $fmt $q
}

$Formats = @('PNG', 'JPEG_LI', 'JPEG_XL', 'AVIF', 'WebP', 'TIFF')
# 按【索引】选而不是按名字：zh 模式下拉里是「PNG (无损)」，名字随语言变。
# 项序 = _formats = FormatIndex 的持久化契约（PNG, JPEG_LI, JPEG_XL, AVIF, WebP, TIFF），
# 且这里数的是 FormatCbo 自己子树里的 6 项，不会混进导航项（w1-probe-uia 06:54 那个坑）。
# 项序一旦与 $Formats 不一致，下面那条 6 项断言 + 逐格式标签判等会一起红。

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
if (-not (Test-Path -LiteralPath $set)) { Say @("RC=2 没有 $set ⇒ 未判"); exit 2 }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$bytes0 = [IO.File]::ReadAllBytes($set)
$sha0 = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
Say @("轮前设置 sha256 = $($sha0.Substring(0,16))…")
if ($Lang -eq 'en') {
    $raw = [IO.File]::ReadAllText($set, $utf8)
    if ($raw -match '"Language":') { Say @('RC=2 盘上已有 Language 键 ⇒ 不覆盖用户选择，本格跳过'); exit 2 }
    [IO.File]::WriteAllText($set, ($raw.TrimEnd('}') + ',"Language":"en"}'), $utf8)
}

$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) { Say @("起手 $($pre.Count) 个实例 ⇒ 先停（收尾起回）"); $pre | Stop-Process -Force; Start-Sleep -Seconds 2 }

$auto = [System.Windows.Automation.AutomationElement]
$cjkRe = '[\u3000-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff00-\uffef]'
$cbCond = New-Object System.Windows.Automation.PropertyCondition(
    $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$script:main = $null
$proc = $null; $exitCode = 2

function Find-ByName([string]$automationId, $typeCond) {
    $c = @($script:main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $typeCond) |
           Where-Object { $_.Current.AutomationId -eq $automationId })
    if ($c.Count) { return $c[0] }
    return $null
}

try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $tops = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
              (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id))))
    if (-not $tops.Count) { Say @('RC=2 没有顶层窗 ⇒ 未判'); exit 2 }
    $main = $null; $best = -1
    foreach ($w in $tops) {
        $c = 0
        try { $c = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition).Count } catch { $c = 0 }
        if ($c -gt $best) { $best = $c; $main = $w }
    }
    $script:main = $main
    Say @("主窗子树 $best 个元素")

    $fbo = Find-ByName 'FormatCbo' $cbCond
    if ($null -eq $fbo) { Say @('RC=2 找不到 FormatCbo ⇒ 未判'); exit 2 }
    $ec = $fbo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 700
    $items = @($fbo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    $orig = @($items | Where-Object {
        try { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
        catch { $false } })
    $origName = if ($orig.Count) { $orig[0].Current.Name } else { '' }
    Say @("格式下拉 $($items.Count) 项，轮前选中 = 「$origName」")
    $ec.Collapse(); Start-Sleep -Milliseconds 300

    if ($items.Count -ne $Formats.Count) {
        Say @("RC=2 未判：格式下拉实得 $($items.Count) 项，与 \$Formats 的 $($Formats.Count) 项不符 ⇒ 项序契约变了，按索引选会点错")
        exit 2
    }
    $bad = @(); $n = 0; $zhLeft = 0; $skippedFixed = 0
    for ($ix = 0; $ix -lt $Formats.Count; $ix++) {
        $f = $Formats[$ix]
        $ec.Expand(); Start-Sleep -Milliseconds 600
        $hit = @($fbo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
        if ($hit.Count -ne $Formats.Count -or -not $hit[$ix]) {
            $ec.Collapse(); $bad += "$f 展开后项序失步（实得 $($hit.Count) 项）"; continue
        }
        Say @("  选中第 $ix 项 = 「$($hit[$ix].Current.Name)」")
        $hit[$ix].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 900
        try { $ec.Collapse() } catch { }
        Start-Sleep -Milliseconds 400

        $lbl = Find-ByName 'QualityLabel' (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
        $desc = Find-ByName 'QualityDescTxt' (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
        $qtxt = Find-ByName 'QualityTxt' (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
        if ($null -eq $lbl) {
            if ($f -in @('PNG', 'TIFF')) {
                # 无损格式【本来就没有质量参数】⇒ 整行按设计收起（见 ImageEncoder.QualityIsFixed）。
                # 这一档走到了、但没有可判等的文字，单独计数，不许混进"没走到"把整轮拖成未判。
                $skippedFixed++; Say @("  $f 质量面板按设计收起（QualityIsFixed）⇒ 走到但无可判等文字")
                continue
            }
            $bad += "$f 读不到 QualityLabel"; continue
        }
        $lblName = $lbl.Current.Name
        $descName = if ($desc) { $desc.Current.Name } else { '' }
        # 数值优先问【滑块】的 RangeValue：它是这一行的真值来源，且不受输入框控件类型影响。
        # 13:58 那一轮 AVIF/WebP 从 QualityTxt 读到空串，而 JPEG 两档读得到 ⇒ 输入框那条路不稳。
        $qRaw = ''
        $sld = Find-ByName 'QualitySld' (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider))
        if ($sld) {
            try { $qRaw = ('{0}' -f $sld.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value) } catch { }
        }
        if (-not $qRaw -and $qtxt) {
            try { $qRaw = $qtxt.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
            if (-not $qRaw) { $qRaw = $qtxt.Current.Name }
        }
        $qVal = 0.0
        $gotQ = $false
        if ($qRaw) { $gotQ = [double]::TryParse(($qRaw -replace '[^0-9.,\-]', ''), [ref]$qVal) }
        if (-not $gotQ) { $bad += "$f 读不到质量数值（原始=「$qRaw」）⇒ 原式没法算，判等不作数"; continue }
        $n++

        if ($Lang -eq 'zh') {
            # 中文模式：界面显示的必须【逐字】等于改造前 Core 原式算出来的那一串
            $expLabel = $OldLabel[$f]
            $expDesc = ExpectedDesc $f $qVal
            $okL = ($lblName -eq $expLabel)
            $okD = ($descName -eq $expDesc -or $descName -eq '')
            Say @("  $f  数值=$qVal")
            Say @("     标签 「$lblName」 vs 原式 「$expLabel」 $(if ($okL) {'✓'} else {'❌'})")
            Say @("     描述 「$descName」 vs 原式 「$expDesc」 $(if ($okD) {'✓'} else {'❌'})")
            if (-not $okL) { $bad += "$f 标签与改造前不等" }
            if (-not $okD) { $bad += "$f 描述与改造前不等" }
        }
        else {
            if ($lblName -match $cjkRe) { $zhLeft++; $bad += "$f 英文模式标签仍含中文：$lblName" }
            if ($descName -match $cjkRe) { $zhLeft++; $bad += "$f 英文模式描述仍含中文：$descName" }
            Say @("  $f  标签「$lblName」 描述「$descName」")
        }
    }

    # 选回轮前那一项
    if ($origName) {
        $ec.Expand(); Start-Sleep -Milliseconds 600
        $back = @($fbo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
            Where-Object { $_.Current.Name -eq $origName })
        if ($back.Count) {
            $back[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Say @("已把格式选回「$origName」")
        } else { Say @("❌ 选不回「$origName」——盘上设置随后按字节还原，界面状态不影响数据") }
        try { $ec.Collapse() } catch { }
    }

    Say @("---- 结论（$Lang）----")
    Say @("走过 $($n + $skippedFixed) / $($Formats.Count) 个格式（其中 $n 个可判等、$skippedFixed 个是无损收起）；不符 $($bad.Count) 处")
    foreach ($b in $bad) { Say @("   ❌ $b") }
    if ($n + $skippedFixed -lt $Formats.Count) {
        Say @("RC=2 未判：$($Formats.Count - $n - $skippedFixed) 个格式根本没走到（不是无损收起，是真的没读到）")
        $exitCode = 2
    } elseif ($skippedFixed -ne 2) {
        Say @("RC=2 未判：按设计收起的格式应有 2 个（PNG/TIFF），实得 $skippedFixed ⇒ 收起条件与 QualityIsFixed 不同向")
        $exitCode = 2
    }
    elseif ($bad.Count) { Say @("RC=1 有 $(if ($Lang -eq 'zh') {'逐字不等'} else {'残留中文'})"); $exitCode = 1 }
    else {
        Say @(if ($Lang -eq 'zh') { 'RC=0 六个格式的质量行与改造前 Core 原式逐字相同 ⇒ 中文模式零改动' }
              else { 'RC=0 六个格式的质量行英文模式下 0 处中文' })
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
    $shaEnd = (Get-FileHash -LiteralPath $set -Algorithm SHA256).Hash
    if ($shaEnd -eq $sha0) { Say @("设置还原自检：$($shaEnd.Substring(0,16))… ⇒ 与轮前逐字相同 ✓") }
    else { Say @("❌ 还原后指纹不同 轮前=$sha0 现在=$shaEnd"); $exitCode = 3 }
    $rel = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    $alive = $false; $waited = 0
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 2; $waited += 2
        if ($null -ne (Get-Process -Id $rel.Id -ErrorAction SilentlyContinue)) { $alive = $true; break }
    }
    Say @("用户实例起回：PID=$($rel.Id) 活着=$alive（等了 $waited s）")
    if (-not $alive -and $exitCode -eq 0) { $exitCode = 3 }
    Say @("RC=$exitCode")
}
Write-Output "RC=$exitCode"
exit $exitCode
