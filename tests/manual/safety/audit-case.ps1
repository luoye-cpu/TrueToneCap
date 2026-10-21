# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 2026-09-29 E 批 · 用 --display-audit 跑 B 域格子（无头，起一次真身进程/格）。
# 每格做四件事：① 换上进手的 settings.json ② 起 --display-audit 取【真退出码】
# ③ 把审计卡的"管线落点"段原样落到日志 ④ 比 settings/凭据库指纹（V6/V7 的副作用面）。
# ⚠ 起 GUI 子系统进程拿退出码必须 Start-Process -Wait（`& $exe` 不等它 —— 记忆点过名）。
param(
    [string]$Case = 'control',
    [string]$SettingsJson = '',      # 空 = 不动盘上那份（对照臂）
    [string]$SettingsJsonFile = ''   # ⚠ 首选这个：从 bash 传 JSON 时引号会被 shell 处理一层，
                                       #   实测 '{\"SettingsRevision\":2}' 会原样落进文件 ⇒ 那是【坏文件】
                                       #   而不是"我写的那份设置"，整格读数作废（本轮真踩过一次）。
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$bin  = Join-Path $repo 'src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64'
$exe  = Join-Path $bin 'TrueToneCap.exe'
$set  = Join-Path $bin 'TrueToneCap.settings.json'
$bkp  = Join-Path $repo 'tests\manual\safety\settings-backup\TrueToneCap.settings.json'
$out  = Join-Path $repo ("tests\manual\safety\audit-" + $Case)
$log  = Join-Path $repo ("tests\output\audit-" + $Case + ".log")
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }

# 凭据库：App 用 DPAPI 存在 %LOCALAPPDATA%\TrueToneCap 下（文件名以实际枚举为准）
$secDir = Join-Path $env:LOCALAPPDATA 'TrueToneCap'
$secFiles = @(Get-ChildItem -LiteralPath $secDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike '*.json' -and $_.Name -notlike '*.log' -and $_.Name -notlike '*.png' })

[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 审计轮 · 格子 $Case · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

if ($SettingsJsonFile -ne '') {
    # ⚠ 2026-09-29 实测：文件名少打一个连字符 ⇒ 这条静默走到"对照臂"分支，卡片于是报告
    #   "盘上设置未改动 + 开关=True"，看起来像产品没生效。夹具取不到就必须【拒绝出结论】，
    #   绝不能退化成"那就不改设置"这一格 —— 那是另一格的名字。
    if (-not (Test-Path -LiteralPath $SettingsJsonFile)) {
        Say @("❌ 夹具文件不存在: $SettingsJsonFile ⇒ 本格不给结论（不许静默降级成对照臂）")
        Write-Output 'RESULT=no-fixture'; exit 1
    }
    $SettingsJson = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $SettingsJsonFile).ProviderPath)
    Say @("从夹具读到 $($SettingsJson.Length) 字符: $SettingsJsonFile")
}
if ($SettingsJson -ne '') {
    # 落盘前先自己解析一遍：坏 JSON 会安静地变成"产品用了默认值表"，那读数就不是关于我这一格的设置的了。
    $jsonErr = $null
    try { $null = $SettingsJson | ConvertFrom-Json } catch { $jsonErr = $_.Exception.Message }
    if ($jsonErr) {
        Say @("⚠ 传进来的 JSON 解析不过：$jsonErr")
        Say @('   ⇒ 这一格量的是【坏文件时产品怎么办】（会回落默认值表），不是「我写的那份设置」。')
        Say @("   要测真设置请用 -SettingsJsonFile；仍要测坏文件就照旧，但读数按这一句解释。")
    }
    $selfCheck = if ($jsonErr) { 'INVALID（本格按坏文件读数）' } else { 'VALID' }
    Say @("JSON 自检：$selfCheck")
    Set-Content -LiteralPath $set -Value $SettingsJson -NoNewline -Encoding UTF8
    Say @("写入的盘上设置: $SettingsJson")
} else {
    Say @('盘上设置: 未改动（对照臂，用现有那份）')
}
$setBefore = Sha $set
$secBefore = ($secFiles | ForEach-Object { "$($_.Name)=$(Sha $_.FullName)" }) -join ' '
Say @("轮前 settings sha1: $setBefore")
Say @("轮前凭据库件: $(if ($secFiles.Count) { $secBefore } else { '<无独立凭据件>' })")

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$p = Start-Process -FilePath $exe -ArgumentList @('--display-audit', '--out', $out) -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(120000)) {
    Say @('❌ 120 秒没退 ⇒ 杀掉，本格不给结论')
    try { $p.Kill($true) } catch { }
    Say @('AUDIT_EXIT=killed'); Write-Output 'RESULT=killed'; exit 1
}
Say @("AUDIT_EXIT=$($p.ExitCode)")

$card = Get-ChildItem -LiteralPath $out -File -Filter '*.md' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $card) { $card = Get-ChildItem -LiteralPath $out -File -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($null -eq $card) {
    Say @('❌ 审计卡没落盘（目录空）⇒ 本格不给结论')
    Write-Output 'RESULT=no-card'; exit 1
}
$cardText = @(Get-Content -LiteralPath $card.FullName -Encoding UTF8)
Say @("审计卡: $($card.Name)，行数 $($cardText.Count)")
# 卡片里的「管线落点」从 2026-09-30（台账 §17.6 第 1 条修完）起分成两段：与屏无关的那段排在
# 逐屏循环【之前】（整轮一次），逐屏那 4 条格式探针仍在各自屏名下。
# ⚠ 老写法是"第一个命中 + 固定 22 行"⇒ 全局段一挪到循环之前，窗口就整块落在枚举区里，
#   逐屏落点那 4 条探针从证据日志里消失（本轮实测到才改）。两段各截，且找不到就点名，不静默。
$gi = @($cardText | Select-String -Pattern '管线落点·全局' | Select-Object -First 1)
if ($gi.Count -eq 0) {
    Say @('★ 卡片里没有「管线落点·全局」那一行 ⇒ 要么宿主是旧的、要么这段被挪走了：下面的落点读数不可信')
} else {
    $g0 = $gi[0].LineNumber - 1
    $gEnd = $g0
    while ($gEnd -lt ($cardText.Count - 2) -and $cardText[$gEnd + 1] -match '\S') { $gEnd++ }
    Say @('--- 卡片「管线落点·全局」段（原样，到第一个空行为止） ---')
    Say ($cardText[$g0..$gEnd] | ForEach-Object { "  $_" })
}
$pi = @($cardText | Select-String -Pattern '管线落点\(真调')
if ($pi.Count -eq 0) { Say @('★ 卡片里没有逐屏「管线落点」段（实得 0 处）⇒ 本屏那 4 条格式探针没判') }
Say @("--- 逐屏「管线落点」段：实得 $($pi.Count) 处（每屏一处），每处截到本屏「实得源/捕获」那行为止 ---")
foreach ($m in $pi) {
    $i = $m.LineNumber - 1
    $j = $i
    while ($j -lt ($cardText.Count - 2) -and ($cardText[$j] -notmatch '^\s*(实得源|捕获:)') -and ($j - $i) -lt 20) { $j++ }
    Say ($cardText[$i..$j] | ForEach-Object { "  $_" })
}
# ⚠ 必须在还原【之前】拍下盘上内容：V6 那条次序修复的唯一决定性读法就是"迁移触发时，
#   被钉回盘上的是归一后的值还是原始越界值"（修前 = 99 会被原样写回并盖章）。
Say @("轮后盘上内容（还原前）: $(Get-Content -LiteralPath $set -Raw)")
# ⚠ 这个 sha 必须在【还原之前】算。第一版把 RESULT 行的漂移判据写在还原之后，于是它比的是
#   "备份 vs 我写进去的那份" ⇒ 凡是用 -SettingsJson 的格子恒为 True，把"进程没改设置"读成"改了"。
$setAfter = Sha $set
Say @("轮后 settings sha1: $setAfter（轮前 $setBefore）")
$secAfter = ($secFiles | ForEach-Object { "$($_.Name)=$(Sha $_.FullName)" }) -join ' '
Say @("轮后凭据库件: $(if ($secFiles.Count) { $secAfter } else { '<无独立凭据件>' })")

# 还原：先按备份 md5 还原盘上设置，再核指纹
Copy-Item -LiteralPath $bkp -Destination $set -Force
$restoreOk = (Sha $set) -eq (Sha $bkp)
Say @("还原盘上设置: $([bool]$restoreOk)")
Write-Output ("RESULT=ok EXIT={0} RESTORED={1} PROC_WRITTEN={2} SECRETS={3}" -f `
    $p.ExitCode, $restoreOk, ($setAfter -ne $setBefore), $secFiles.Count)
