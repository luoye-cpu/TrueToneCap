# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 批次 T3（#7）第一格：**英文界面的残留计数** —— D4 场景 5 此前是"零次读数"。
# 两条独立尺子，缺一条就只报"半格"：
#   A. 产品自己那句运行时读数（MainWindow.Localization.cs:234-249 的 Locale 日志：命中 0 处 / 残留 N 处）；
#   B. 从进程外用 UIA 走一遍真实的控件树，数 Name 里还剩几个中日韩字符 —— 这是【独立参照】，
#      A 与 B 是两套实现，只有两边都给出数，"残留 N" 才算量过而不是自说自话。
# 纪律：起真身之前把盘上那份设置备份并记 sha1，跑完还原并核指纹；进程只杀自己起的那个 PID。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'en-ui-count.log',
    [int]$SettleSec = 12,
    [int]$Cap = 90,
    # 对照臂用 zh：两趟同一把尺子，中文模式必须显著更"红"，否则说明走树没走到内容
    #（09-30 第一轮就是这么发现的：拿 $all[0] 取到 SoPY_Status 空壳，CJK=0 是空树读出来的假零）。
    [ValidateSet('en', 'zh')] [string]$Lang = 'en'
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA1) } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$bak = Join-Path $dir 'TrueToneCap.settings.json.ttc-enbak'
$today = (Get-Date).ToString('yyyy-MM-dd')
$appLog = Join-Path $dir "log\app_$today.log"
if (-not (Test-Path -LiteralPath $set)) { Fail "没有 $set ⇒ 这一格要在【已有设置】上量，默认值表那一臂另说" }
if (Test-Path -LiteralPath $bak) { Fail "上一轮遗留 $bak ⇒ 先人工核对，不自动覆盖" }
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 英文界面残留计数 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
$sha0 = Sha $set
Say @("轮前设置 sha1: $sha0")
Say @("轮前内容: $([IO.File]::ReadAllText($set))")

$proc = $null
try {
    Move-Item -LiteralPath $set -Destination $bak
    $j = [IO.File]::ReadAllText($bak) | ConvertFrom-Json
    $obj = [ordered]@{}
    foreach ($p in $j.PSObject.Properties) { $obj[$p.Name] = $p.Value }
    $obj['Language'] = $Lang
    $obj['SettingsRevision'] = 2
    $obj['GainMapMerged'] = $true
    ($obj | ConvertTo-Json -Compress) | Set-Content -LiteralPath $set -NoNewline -Encoding UTF8
    Say @("写进去的设置: $([IO.File]::ReadAllText($set))")

    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("已起真身 PID=$($proc.Id)（窗口会出现在用户桌面上，$SettleSec s 后取证，最多 $Cap s）")
    Start-Sleep -Seconds $SettleSec

    # ---- 尺子 A：产品自己的运行时读数 ----
    # ⚠ 上一版这里的模式是 '\[Locale'，一行都没匹配到，于是读数打成"0 条"——而产品其实写了：
    #   本仓日志的类目形如 `[系统/Locale]`（分类/Tag 两段），"[Locale" 这个前缀根本不存在。
    #   教训同 §11：负断言用的检索必须先对"已知存在的那条"命中一次才算尺子接通了。
    $prodLines = @(); $encUsed = 'none'
    if (Test-Path -LiteralPath $appLog) {
        foreach ($enc in @('UTF8', 'Unicode')) {
            $raw = @(Get-Content -LiteralPath $appLog -Encoding $enc)
            $try = @($raw | Where-Object { $_ -match '英文模式下仍有|命中 0 处|可视树本地化' })
            if ($try.Count -gt 0) { $prodLines = $try; $encUsed = "$enc（原始 $($raw.Count) 行）"; break }
            if ($enc -eq 'Unicode') { $encUsed = "两种编码都读到 0 行（UTF8 原始 $((Get-Content -LiteralPath $appLog -Encoding UTF8).Count) 行）" }
        }
    }
    Say @("--- 尺子 A：产品自己的 Locale 日志行（$($prodLines.Count) 条；编码判定=$encUsed）---")
    foreach ($l in $prodLines) { Say @("  $l") }

    # ---- 尺子 B：进程外 UIA 独立走树 ----
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $auto = [System.Windows.Automation.AutomationElement]
    $scopeChildren = [System.Windows.Automation.TreeScope]::Children
    $scopeDesc = [System.Windows.Automation.TreeScope]::Descendants
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)

    function Count-Desc($el) {
        try { return $el.FindAll($scopeDesc, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { return -1 }
    }
    # WinUI3 一个进程会挂多个顶层件（宿主窗 / 消息窗 / 隐形状态窗），第一个往往不是主窗
    # —— 上一轮就是拿 $all[0] 拿到了 ClassName=SoPY_Status、子树只有 1 个元素的空壳，
    # 于是"走到 0 个带 Name 的元素"被读成"界面全英文"。这轮改成：列全部、挑子树最大的。
    $roots = @()
    $deadline = (Get-Date).AddSeconds($Cap - $SettleSec)
    while ((Get-Date) -lt $deadline) {
        $roots = @($auto::RootElement.FindAll($scopeChildren, $pidCond))
        if ($roots.Count -gt 0) { break }
        Start-Sleep -Milliseconds 800
    }
    Say @("尺子 B：该 PID 的顶层件 $($roots.Count) 个：")
    $ranked = @()
    foreach ($r in $roots) {
        $n = Count-Desc $r
        $ranked += [pscustomobject]@{ El = $r; Kids = $n; CN = $r.Current.ClassName; Title = $r.Current.Name }
        Say @("   <$($r.Current.ClassName)> 子树 $n 个 标题=[$($r.Current.Name)]")
    }
    $root = @($ranked | Sort-Object Kids -Descending | Select-Object -First 1).El

    function Walk-Names($startEl) {
        $st = New-Object System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]
        $st.Push($startEl)
        $list = New-Object System.Collections.Generic.List[object]
        $seen = 0
        while ($st.Count -gt 0 -and $seen -lt 6000) {
            $e = $st.Pop(); $seen++
            $nm = ''; $cn = ''; $ct = ''
            try { $nm = $e.Current.Name } catch { }
            try { $cn = $e.Current.ClassName } catch { }
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch { }
            if (-not [string]::IsNullOrEmpty($nm)) { $list.Add([pscustomobject]@{ Name = $nm; CT = $ct; CN = $cn; El = $e }) }
            try {
                foreach ($c in $e.FindAll([System.Windows.Automation.TreeScope]::Children,
                        [System.Windows.Automation.Condition]::TrueCondition)) { $st.Push($c) }
            } catch { }
        }
        return ,@($list, $seen)
    }

    if ($null -eq $root) { Say @('⏭ 尺子 B：按 PID 一个顶层件都没找到 ⇒ UIA 没判到（不报数，也不报零）') }
    else {
        $r1 = Walk-Names $root
        $nodes = $r1[0]; $seen = $r1[1]
        Say @("第一趟：走到 $seen 个元素，带 Name 的 $($nodes.Count) 个")
        # 设置页没实化时扫不到东西 ⇒ 先尝试用 SelectionItem 切到设置/首选项那一页（界面词按中英都认）
        $nav = @($nodes | Where-Object { $_.Name -match '设置|Settings|偏好|首选项' } | Select-Object -First 3)
        Say @("可切页候选：$(if ($nav.Count) { ($nav | ForEach-Object { "[$($_.Name)]$($_.CT)" }) -join ' ' } else { '<无>' })")
        foreach ($cand in $nav) {
            try {
                $pat = $cand.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $pat.Select()
                Say @("已 Select() 「$($cand.Name)」 ⇒ 等 3 s 让页面实化")
                Start-Sleep -Seconds 3
                break
            } catch { Say @("Select() 「$($cand.Name)」 失败：$($_.Exception.Message)") }
        }
        $r2 = Walk-Names $root
        $nodes = $r2[0]; $seen = $r2[1]
        $cjkRe = '[\u3000-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff00-\uffef]'
        $cjk = @($nodes | Where-Object { $_.Name -match $cjkRe })
        Say @("尺子 B（最终）：走到 $seen 个元素，带 Name 的 $($nodes.Count) 个，其中含 CJK 的 【$($cjk.Count)】 个")
        $ctrls = @($nodes | Where-Object { $_.Name -match '[A-Za-z]{4}' })
        Say @("   对照（同一次读数里必须存在）：带拉丁词的控件 $($ctrls.Count) 个 ⇒ 为 0 说明这条尺子扫的是空树，上面的 0 不算数")
        foreach ($n in ($cjk | Select-Object -First 40)) {
            $cut = if ($n.Name.Length -gt 46) { $n.Name.Substring(0, 46) + '…' } else { $n.Name }
            Say @("   残留: 「$cut」  <$($n.CT) / $($n.CN)>")
        }
        if ($cjk.Count -gt 40) { Say @("   （其余 $($cjk.Count - 40) 条未打印，计数含全部）") }
    }
    $cjkN = 0; if ($null -ne $cjk) { $cjkN = $cjk.Count }
    $nodeN = 0; if ($null -ne $nodes) { $nodeN = $nodes.Count }
    Write-Output "RC=0 PROD=$($prodLines.Count) NODES=$nodeN CJK=$cjkN"
} finally {
    if ($null -ne $proc) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停止本次起的 PID=$($proc.Id)") }
        catch { Say @("❌ 停止 PID 失败：$($_.Exception.Message)") }
    }
    if (Test-Path -LiteralPath $bak) {
        if (Test-Path -LiteralPath $set) { Remove-Item -LiteralPath $set -Force }
        Move-Item -LiteralPath $bak -Destination $set
        $shaEnd = Sha $set
        Say @("还原自检：原件移回 sha1=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：还原失败，立即核对' })")
    } else { Say @('❌ 备份件不见了 ⇒ 无法自动还原，请立即人工复查 ' + $bak) }
}
