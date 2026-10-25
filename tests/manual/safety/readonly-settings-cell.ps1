# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# F 批剩余构造之二：**设置文件被标只读 ⇒ 整进程怎么办**（§15.5 列的"只读目录"那一格的 App 侧）。
# 探针本身早就有 T1（CorePipelineTests.Test_SettingsWriteProbe_RealProbe：只读文件 + ACL 拒写的
# WindowsApps 两条臂都实测）⇒ 这里补的是"起一次真身，看用户对这一格能看见什么"。
# 三条自检：
#   ① 设完只读位必须【读回来确认】是 ReadOnly（attrib 在个别卷上会被策略吃掉）；
#   ② finally 里摘掉只读位并读回确认 Normal，同时核 sha1 前后一致 —— 这一格要求进程【一个字节都没写】，
#      因为 CanPersist=false 时它本该既不读也不写；
#   ③ 判据取自进程退出码 + 卡片那两行原话，不取自我的推断。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'readonly-settings-cell.log',
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Continue'
$log = Join-Path 'C:\PLAN\TrueToneCap\tests\output' $LogName
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA1) } else { 'ABSENT' } }

if (-not (Test-Path -LiteralPath $Exe)) { Fail "找不到 exe: $Exe" }
$set = Join-Path (Split-Path -Parent $Exe) 'TrueToneCap.settings.json'
if (-not (Test-Path -LiteralPath $set)) { Fail "找不到设置文件 $set ⇒ 这一格量的是【文件在但存不下】，没文件就不是这一格" }
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 只读设置格 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("exe: $Exe"); Say @("设置: $set")

$sha0 = Sha $set
Say @("轮前 sha1: $sha0")
Say @("轮前属性: $([IO.File]::GetAttributes($set))")
try {
    [IO.File]::SetAttributes($set, [IO.File]::GetAttributes($set) -bor [IO.FileAttributes]::ReadOnly)
    $attr = [IO.File]::GetAttributes($set)
    Say @("自检①：写入只读位后读回 = $attr")
    if (-not ($attr -band [IO.FileAttributes]::ReadOnly)) { Fail "只读位没生效 ⇒ 注入为空，这一格不给结论" }

    $outDir = 'C:\PLAN\TrueToneCap\tests\manual\safety\readonly-cell'
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $stdout = Join-Path $outDir 'audit-stdout.txt'
    $p = Start-Process -FilePath $Exe -ArgumentList @('--display-audit', '--out', $outDir) `
        -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $outDir 'audit-stderr.txt')
    if ($null -eq $p.ExitCode) { Fail '退出码读不到 ⇒ 不给结论' }
    Say @("退出码 = $($p.ExitCode)")

    $all = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
    $hit = @($all | Where-Object { $_ -match '可持久化|设置来源|实际读进来|存不下|不读取也不保存|默认值表' })
    Say @("--- 卡片/日志里与持久化相关的行（$($hit.Count) 行；全量见 $stdout）---")
    foreach ($h in $hit) { Say @("  $h") }
    if ($hit.Count -lt 1) { Fail '整份输出里没有一句讲到持久化 ⇒ 这一格没被说出，读数不成立' }
    Write-Output "RC=0 EXIT=$($p.ExitCode) HITS=$($hit.Count) LOG=$log"
} finally {
    try {
        [IO.File]::SetAttributes($set, ([IO.File]::GetAttributes($set) -band (-bnot [IO.FileAttributes]::ReadOnly)))
        $after = [IO.File]::GetAttributes($set)
        $sha1 = Sha $set
        Say @("收尾自检②：属性读回 = $after（须为轮前那一项，不含 ReadOnly）")
        Say @("轮后 sha1: $sha1 ⇒ $(if ($sha1 -eq $sha0) { '进程没写盘（这一格本就不该写）✓' } else { '★ 只读状态下盘上竟然变了' })")
        if ($after -band [IO.FileAttributes]::ReadOnly) { Say @('❌ 只读位没摘掉，请立即手工复查') }
    } catch { Say @("❌ 收尾失败：$($_.Exception.Message)") }
}
