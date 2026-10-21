# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# LogViewer 行解析器的对撞判据（2026-09-29 审查轮 §10.3 V12 的取证 + 修复后的复验）。
# 尺子从源码里【现取】正则，不抄常数：抄一份进脚本 ⇒ 改了 LogParser.cs 而脚本还在测旧的那份，
# 就会造出"全绿但功能坏"。
# ⚠ 第一版按「@"…" 后紧跟 RegexOptions」匹配，我破掉一条正则并在行尾留注释，它就【静默跳过那条】
#   去判另一条 ⇒ 假绿。现在按变量名锚定、取不到就退非零：尺子看不见自己的输入时必须报错而不是给结论。
# 四条臂 × 两条正则：① 真机 log 里不带 opId 的行 ⇒ 命中（对照：格式本来就对得上）
#                  ② 同一条行加 `#a1b2c3 ` ⇒ 命中（本轮修的正是这一格）
#                  ③ 缺 [分类/Tag] 的坏行 ⇒ 不命中（防空转：一条什么都收的宽松版也是绿）
#                  ④ 无时间戳的堆栈行 ⇒ 不命中
$ErrorActionPreference = 'Continue'
function Fail([string]$msg) { Write-Output "RC=1 $msg"; exit 1 }

# ═══ 仓库根从脚本自己的位置推，不硬编码 ═══
# 原先写死 'C:\PLAN\TrueToneCap'：换一个 checkout 路径或换一台机器，这条【阻塞门禁步】恒定 RC=1，
# 红的是脚本的路径而不是产品 —— 判据不可移植就等于它在那台机器上从不存在。
# 留一个环境变量口子（TTC_REPO_ROOT）给"把脚本单独拷走取证"的场合，优先级高于推导值。
$repo = if ($env:TTC_REPO_ROOT) { $env:TTC_REPO_ROOT }
        else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
if (-not $repo -or -not (Test-Path -LiteralPath (Join-Path $repo 'src'))) {
    Fail "定位不到仓库根（$repo 下面没有 src）⇒ 不给结论"
}
$parserSrc = Join-Path $repo 'src\TrueToneCap.LogViewer\Services\LogParser.cs'
$writerSrc = Join-Path $repo 'src\TrueToneCap.App\Services\LogService.cs'
# ═══ 真机日志按【最新】取，不写死日期 ═══
# 原先钉死 …\log\app_2026-09-28.log：日志按天轮转 ⇒ 次日起这条判据 RC=1，同样是"红的是日历"。
# 但对照臂【仍然只认真实产物】：一条都找不到就退非零，绝不退化成我自己手写的样例行
# （手写的行只要和读方正则同构，就能把一条过宽的正则洗成绿的）。
$logCands = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TrueToneCap.App\bin') -Filter 'app_*.log' `
    -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
$logFile = if ($logCands.Count -gt 0) { $logCands[0].FullName } else { '' }
Write-Output "候选真机日志 $($logCands.Count) 份，选中: $(if ($logFile) { "$logFile（$($logCands[0].LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))）" } else { '<无>' })"

if (-not (Test-Path -LiteralPath $parserSrc)) { Fail "读不到 $parserSrc" }
if (-not (Test-Path -LiteralPath $writerSrc)) { Fail "读不到 $writerSrc" }
$src = Get-Content -LiteralPath $parserSrc -Raw

# ── ① 取两条头正则 ──
$names = @('s_headerRegex', 's_headerNoCallerRegex')
$probes = @()
foreach ($n in $names) {
    $m = [regex]::Match($src, ([regex]::Escape($n) + '\s*=\s*new\(\s*(?:@"((?:""|[^"])*)"|"((?:\\.|[^"])*)")'))
    if (-not $m.Success) { Fail "取不到 $n 的 pattern ⇒ 这条尺子没接到真身，不给结论" }
    $raw = if ($m.Groups[1].Success) { $m.Groups[1].Value -replace '""', '"' } else { $m.Groups[2].Value }
    Write-Output "取到 $n 的 pattern: $raw"
    try { $probes += ,@($n, (New-Object System.Text.RegularExpressions.Regex($raw))) }
    catch { Fail "$n 的 pattern 编译失败: $($_.Exception.Message)" }
}
if ($probes.Count -ne $names.Count) { Fail "抽到的正则数 = $($probes.Count)（期望 $($names.Count)）" }

# ── ② 写方格式核对：opId 段的形状确实是 `#<id> ` ──
$writer = Get-Content -LiteralPath $writerSrc -Raw
if ($writer -notmatch 'OpDisplay\s*=>\s*string\.IsNullOrEmpty\(OpId\)\s*\?\s*""\s*:\s*\$"#\{OpId\}\s') {
    Fail 'LogService.cs 的 OpDisplay 写法与预期不符（应为 "#<id> "）⇒ 读方正则的可选段要重新对'
}
Write-Output '写方 OpDisplay = "#{OpId} "（已核）'

# ── ③ 组四臂 ──
$good = New-Object System.Collections.Generic.List[string]
if ($logFile -and (Test-Path -LiteralPath $logFile)) {
    foreach ($l in (Get-Content -LiteralPath $logFile -Encoding UTF8)) {
        if ($l -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s+\[\w+\s*\]\s+\[') { $good.Add($l); if ($good.Count -ge 1) { break } }
    }
}
if ($good.Count -lt 1) {
    Fail '真机日志里找不到一条不带 opId 的常规行 ⇒ 对照臂失效，本脚本不给结论。
      要么这台机器还没跑过产品（bin 下没有 log），要么选中那份是空的/被轮转清掉了 ——
      补一份真实产物再来，不要改用手写样例行糊过这一格。'
}
$withOp = $good[0] -replace '^(\d{2}:\d{2}:\d{2}\.\d{3}\s+\[\w+\s*\])\s+', '$1 #a1b2c3 '
if ($withOp -eq $good[0]) { Fail '夹具没把 #opId 前缀加上去（两串相同）⇒ 被试臂是空转' }
$noBrackets = ($good[0] -replace '\[[^/\]]+/[^\]]+\]\s*', '')
$cases = @(
    @{ Name = '对照臂：不带 opId 的常规行';       Line = $good[0];    Want = $true },
    @{ Name = '被试臂：同一条行加 #a1b2c3 前缀';  Line = $withOp;     Want = $true },
    @{ Name = '负样本：缺 [分类/Tag]';            Line = $noBrackets; Want = $false },
    @{ Name = '负样本：堆栈行（无时间戳）';       Line = 'at TrueToneCap.App.MainWindow.SilentCapture()'; Want = $false }
)

$bad = 0
foreach ($pr in $probes) {
    $nm = $pr[0]; $rx = $pr[1]
    foreach ($c in $cases) {
        $hit = $rx.IsMatch($c.Line)
        $ok = ($hit -eq $c.Want)
        if (-not $ok) { $bad++ }
        Write-Output ("  {0} [{1}] {2}（命中={3} 期望={4}）" -f $(if ($ok) { '✅' } else { '❌' }),
            $nm, $c.Name, $hit, $c.Want)
    }
}
Write-Output "ARMS=$($probes.Count * $cases.Count) MISMATCH=$bad"
if ($bad -gt 0) { Fail "$bad 条臂与期望不符 ⇒ 写方与读方的行格式对不上（日志查看器会静默丢行）" }
Write-Output 'RC=0 两条头正则都与写方行格式互认（含 #opId 段），且负样本仍被拒'
exit 0
