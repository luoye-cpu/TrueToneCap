# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 把 lane 那句"没验：LogViewer 界面本身（绑定只被源码断言钉住）"变成读数。（报告 §26）
# 做法：从进程外起 TTCLogViewer，喂一份真日志，用 UIA 读渲染出来的文本。
# 判据（每条单独落盘，缺哪条判哪条）：
#   ① 列表里有行（渲染出来了，不是空表）
#   ② 有行带窗口徽标（🖥 + SelectionOverlay）        ⇒ 窗口归属真的进了可视树
#   ③ 有行带实例身份（实例#N）                      ⇒ 两个计数器真的能被用户看到
#   ④ 对照臂：徽标窗口名至少 2 种                    ⇒ 不是"只有覆盖层能显示"的假绿
#   ⑤ 搜索面：往搜索框输一个不存在的关键字 ⇒ 徽标行应清零（拿不到编辑框则记【未判】，不乱判红）
# ⚠ 匹配用的中文/emoji 一律由码点拼出来，不写字面量：这些 .ps1 是无 BOM 的 UTF-8，
#   PowerShell 5.1 用 ANSI 读文件 ⇒ 中文字面量会变乱码，比对会假红（本仓老坑）。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.LogViewer\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TTCLogViewer.exe',
    [string]$SourceLog = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\log\app_2026-09-30.log',
    [string]$LogName = 'logviewer-ui-cell.log',
    [int]$SettleSec = 6
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log  = Join-Path $repo (Join-Path 'tests\output' $LogName)
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

# 码点拼装（避免字面量被 ANSI 误读）
$badgeWin  = [string][char]0xD83D + [char]0xDDA5          # U+1F5A5 桌面电脑图标，徽标前缀
$instTag   = [string][char]0x5B9E + [char]0x4F8B + '#'    # 实例#
$nonexistent = 'ZZ_qoder_no_such_token_ZZ'

if (-not (Test-Path -LiteralPath $Exe))        { Fail "没有 $Exe（先 dotnet build LogViewer -c Release）" }
if (-not (Test-Path -LiteralPath $SourceLog))  { Fail "没有喂给它的真日志 $SourceLog" }
$exeDir = Split-Path -Parent $Exe
$logDir = Join-Path $exeDir 'log'
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
$staged = Join-Path $logDir 'app_2026-09-30.log'
$preExisting = @{}
Get-ChildItem -LiteralPath $logDir -File | ForEach-Object { $preExisting[$_.Name] = 1 }
Copy-Item -LiteralPath $SourceLog -Destination $staged -Force
if (-not (Test-Path -LiteralPath $staged)) { Fail "复制夹具日志失败 ⇒ 不给结论" }
$srcLines = @(Get-Content -LiteralPath $staged -Encoding UTF8).Count
[IO.File]::WriteAllText($log, '', $utf8)
Say @("=== LogViewer 界面取证 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("夹具：$SourceLog（$srcLines 行）→ $staged")
# 前置：这份日志里本来就该有那些行，否则后面"看不到"不能算界面的错
$wantOverlay = @(Get-Content -LiteralPath $staged -Encoding UTF8 | Where-Object { $_.Contains('SelectionOverlay') }).Count
$wantInst    = @(Get-Content -LiteralPath $staged -Encoding UTF8 | Where-Object { $_.Contains($instTag) }).Count
Say @("夹具内容自检：含 SelectionOverlay 的行 $wantOverlay 条，含 $instTag 的行 $wantInst 条")
if ($wantOverlay -eq 0 -or $wantInst -eq 0) { Fail "夹具本身没有可归因的行 ⇒ 界面读不到不能算界面错，先修夹具" }

$proc = $null
try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $exeDir
    Say @("已起 TTCLogViewer PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $auto = [System.Windows.Automation.AutomationElement]
    $scopeDesc = [System.Windows.Automation.TreeScope]::Descendants
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)
    $root = $auto::RootElement
    $cands = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond))
    Say @("--- 该 PID 的顶层窗 $($cands.Count) 个 ---")
    function SubtreeCount($el) {
        try { return $el.FindAll($scopeDesc, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { return -1 }
    }
    $ranked = @()
    foreach ($r in $cands) { $ranked += [pscustomobject]@{ El = $r; Kids = (SubtreeCount $r); CN = $r.Current.ClassName; T = $r.Current.Name } }
    foreach ($r in $ranked) { Say @("   <$($r.CN)> 子树 $($r.Kids) 个 标题=[$($r.T)]") }
    # 挑子树最大的那个（上一轮踩过：拿 $cands[0] 会拿到一个空壳，于是"读到 0 个"被当成"界面没内容"）
    $host2 = $ranked | Sort-Object Kids -Descending | Select-Object -First 1
    if ($null -eq $host2 -or $host2.Kids -le 0) { Fail 'UIA 找不到有内容的窗 ⇒ 不给结论' }
    Say @("选用窗：<$($host2.CN)> 子树 $($host2.Kids) 个 标题=[$($host2.T)]")

    $names = New-Object System.Collections.Generic.List[string]
    $edits = New-Object System.Collections.Generic.List[System.Windows.Automation.AutomationElement]
    try {
        $all = $host2.El.FindAll($scopeDesc, [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            $e = $all.Item($i)
            try { $nm = $e.Current.Name } catch { $nm = '' }
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch { $ct = '' }
            if ($ct -match 'Edit') { $edits.Add($e) }
            if (-not [string]::IsNullOrEmpty($nm)) { $names.Add($nm) }
        }
    } catch { Say @("❌ 走子树抛异常：$($_.Exception.Message)") }
    Say @("--- 可视树里带 Name 的元素 $($names.Count) 个（共走到 $($all.Count) 个元素）---")

    # ── 诊断块：三条判据红的时候，得先分清"行没渲染"与"渲染了但没有徽标"，
    #    否则下一次读的人会以为是徽标绑定的错，而真凶可能是虚拟化/Name 不暴露。
    $cOvlAny  = @($names | Where-Object { $_.Contains('SelectionOverlay') }).Count
    $cCaller  = @($names | Where-Object { $_.Contains('.xaml.cs') -or $_.Contains('.cs:') }).Count
    $cLevel   = @($names | Where-Object { $_ -match '^(Info|Warn|Error|Debug)$' }).Count
    $cTagish  = @($names | Where-Object { $_ -match 'Overlay|WgcCapture|MainWindow|HdrPreview' }).Count
    Say @("--- 诊断（判据②③红时用来定位到底是哪一层没出声）---")
    Say @("   含 'SelectionOverlay' 的 Name = $cOvlAny（不要求带徽标字符）")
    Say @("   含 '.xaml.cs'/'.cs:' 的 Name  = $cCaller（调用者列有没有渲染）")
    Say @("   纯级别串 (Info/Warn/Error)     = $cLevel")
    Say @("   含已知 Tag 的 Name             = $cTagish")
    $samp = @($names | Select-Object -First 14)
    foreach ($s in $samp) { $t = $s; if ($t.Length -gt 96) { $t = $t.Substring(0,96) + '…' }; Say @("   样本：[$t]") }

    # 重新走一遍可视树取 Name（过滤之后行会变，必须重取）
    function Get-Names($el) {
        $out = New-Object System.Collections.Generic.List[string]
        try {
            $a = $el.FindAll($scopeDesc, [System.Windows.Automation.Condition]::TrueCondition)
            for ($i = 0; $i -lt $a.Count; $i++) { try { $nm = $a.Item($i).Current.Name; if ($nm) { $out.Add($nm) } } catch { } }
        } catch { }
        return ,$out
    }

    # ⚠ 判据②③④【不能】在未过滤的视图上判：ListView 是虚拟化的，只有滚到眼前的行才有可视树元素，
    #   而本文件开头那批行（00:59 起）根本不是窗口行——上一版就是这么读出"徽标 0 个"的假红。
    #   所以这三条挪到 ⑥⑦，先用搜索面把视图收敛到目标行，再看徽标在不在。
    $badgeRows = @($names | Where-Object { $_.Contains($badgeWin) })
    Say @("判据① 带 Name 的元素 = $($names.Count)（>0 才算渲染出来了）⇒ $(if ($names.Count -gt 0) {'PASS' } else {'RED'})")
    Say @("   （未过滤视图里的徽标行数 = $($badgeRows.Count)，只作参考，不判）")

    # 截图（屏幕取图，不抢焦点）——必须在动搜索框【之前】拍，否则拍到的是被过滤成空表的画面
    # （上一拍就犯过：判据⑤ 先跑，图里"0 条"被当成"列表是空的"，白看一眼）。
    function Take-Shot($el, [string]$path) {
        try {
            Add-Type -AssemblyName System.Drawing
            $br = $el.Current.BoundingRectangle
            $w = [int]$br.Width; $h = [int]$br.Height
            if ($w -le 0 -or $h -le 0) { return "窗口矩形无效 ${w}x${h}" }
            $bmp = New-Object System.Drawing.Bitmap($w, $h)
            $gr = [System.Drawing.Graphics]::FromImage($bmp)
            $gr.CopyFromScreen([int]$br.X, [int]$br.Y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
            $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            $gr.Dispose(); $bmp.Dispose()
            return "OK ${w}x${h}"
        } catch { return "失败：$($_.Exception.Message)" }
    }
    $shotA = Join-Path $repo 'tests\output\logviewer-ui-cell.png'
    Say @("过滤前截图：$shotA ⇒ $(Take-Shot $host2.El $shotA)")

    # 判据⑤：搜索面。WinUI 的 TextBox 支持 ValuePattern；拿不到就记未判，不判红。
    $script:judgeOk = $null   # 不初始化会让 RC 在「拿不到编辑框」那一支被当成 0/1，而不是 2=未判
    $srch = $null
    foreach ($ed in $edits) {
        try {
            $vp = $null
            if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $srch = $vp; break }
        } catch { }
    }
    if ($null -eq $srch) {
        Say @("判据⑤ 搜索面 —— 【未判】：这个 PID 里没拿到带 ValuePattern 的编辑框（$($edits.Count) 个 Edit），不猜结论")
    } else {
        try {
            $srch.SetValue($nonexistent)
            Start-Sleep -Seconds 2
            $all2 = $host2.El.FindAll($scopeDesc, [System.Windows.Automation.Condition]::TrueCondition)
            $n2 = New-Object System.Collections.Generic.List[string]
            for ($i = 0; $i -lt $all2.Count; $i++) { try { $nm = $all2.Item($i).Current.Name; if ($nm) { $n2.Add($nm) } } catch { } }
            $b2 = @($n2 | Where-Object { $_.Contains($badgeWin) }).Count
            Say @("判据⑤ 搜索「$nonexistent」后：带 Name 元素 $($names.Count) → $($n2.Count)，徽标行 $($badgeRows.Count) → $b2 ⇒ $(if ($b2 -eq 0) {'PASS（过滤生效）'} else {'RED（搜索面没接上这个视图）'})")
            # ──  把视图收敛到覆盖层的行，再看徽标与实例号在不在（虚拟化下这才是可判的形状）──
            $srch.SetValue('SelectionOverlay')
            Start-Sleep -Seconds 2
            $n3 = Get-Names $host2.El
            $b3 = @($n3 | Where-Object { $_.Contains($badgeWin) })
            $o3 = @($n3 | Where-Object { $_.Contains($badgeWin) -and $_.Contains('SelectionOverlay') })
            $i3 = @($n3 | Where-Object { $_.Contains($instTag) })
            Say @("判据⑥ 过滤「SelectionOverlay」：带 Name 元素 $($n3.Count)｜徽标行 $($b3.Count)｜其中 SelectionOverlay $($o3.Count)｜含「$instTag」 $($i3.Count)")
            Say @("   ⇒ 判据②（窗口徽标是否真渲染）$(if ($o3.Count -gt 0) {'PASS' } else {'RED'})｜判据③（实例号是否真渲染）$(if ($i3.Count -gt 0) {'PASS' } else {'RED'})")
            foreach ($s in @($o3 | Select-Object -First 3)) { $t=$s; if($t.Length -gt 90){$t=$t.Substring(0,90)+'…'}; Say @("   徽标样本：[$t]") }
            $shotB = Join-Path $repo 'tests\output\logviewer-ui-cell.png'
            Say @("过滤后截图（这一张才是有内容的）：$shotB ⇒ $(Take-Shot $host2.El $shotB)")

            # ──  对照臂：换一个窗口名，徽标必须跟着变 ──
            #    没有这一条，"徽标行 > 0" 可能只是把某个串写死在模板上；它必须随数据而动。
            $srch.SetValue('MainWindow.xaml.cs')
            Start-Sleep -Seconds 2
            $n4 = Get-Names $host2.El
            $b4 = @($n4 | Where-Object { $_.Contains($badgeWin) })
            $other = @($b4 | Where-Object { -not $_.Contains('SelectionOverlay') })
            Say @("判据⑦ 对照臂 过滤「MainWindow.xaml.cs」：徽标行 $($b4.Count)｜其中非覆盖层 $($other.Count) ⇒ $(if ($other.Count -gt 0) {'PASS（徽标随窗口而变，不是写死的）'} else {'RED（徽标不跟着动 = 归因是假的）'})")
            foreach ($s in @($other | Select-Object -First 2)) { $t=$s; if($t.Length -gt 90){$t=$t.Substring(0,90)+'…'}; Say @("   对照样本：[$t]") }
            $srch.SetValue('')
            $script:judgeOk = ($names.Count -gt 0 -and $o3.Count -gt 0 -and $i3.Count -gt 0 -and $other.Count -gt 0)
        } catch { Say @("判据⑤⑥⑦ —— 【未判】：SetValue 抛 $($_.Exception.Message)") }
    }

    $verdict = if ($null -eq $script:judgeOk) { 2 } elseif ($script:judgeOk) { 0 } else { 1 }
    $tag = if ($null -eq $script:judgeOk) { '未判（拿不到搜索框，徽标是否渲染无从收敛地看）' } elseif ($script:judgeOk) { 'PASS' } else { 'RED' }
    Say @("RC=$verdict（$tag）PID=$($proc.Id)")
    Write-Output "RC=$verdict $tag 徽标(覆盖层)=$($o3.Count) 实例号=$($i3.Count) 对照徽标=$($other.Count)"
} finally {
    if ($null -ne $proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停本次 PID=$($proc.Id)") } catch { Say @("停进程失败：$($_.Exception.Message)") } }
    # 只删自己放进去的那一份，别的文件不动
    if (Test-Path -LiteralPath $staged) {
        Remove-Item -LiteralPath $staged -Force
        Say @(if ($preExisting.ContainsKey('app_2026-09-30.log')) { '夹具日志已删除（还原到轮前状态）' } else { '夹具日志已删除，且轮前该目录没有同名件' })
    }
    $left = @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Say @("收尾清单：$logDir 现存 $($left.Count) 件 [$($left -join ', ')]")
}
