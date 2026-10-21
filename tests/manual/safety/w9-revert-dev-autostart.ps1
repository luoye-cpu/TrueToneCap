# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 撤掉本轮验证过程种下的开机自启动项。
#
# 事实链（都有日志/注册表实证）：
#   · HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run 的 TrueToneCap 值指向
#     C:\PLAN\TrueToneCap\src\...\win-x64\TrueToneCap.exe --autostart —— 是【dev 构建】的路径；
#   · dev 那份 TrueToneCap.settings.json 里 AutoStart = True；
#   · 产品日志 app_2026-10-03.log 08:53:23 记着「主窗口已创建, 自动启动=True」→「开机自启动模式，不显示窗口」
#     ⇒ 机器重启后它真的以无窗口方式起来了，双击 exe 又因单实例被吞 ⇒ 看起来像"崩溃/没反应"。
#   · 写入点是 StartupManager（RunKey），而它只在 SaveSettings() 里被同步 ⇒ 本轮某格验证
#     真按了「保存设置」，就把 dev 构建注册进了用户的开机启动。
#
# 只删【指向 dev 路径】的那一条；如果值指向别处（用户自己设的发布版），一律不动并如实报告。
param([switch]$DryRun)
$ErrorActionPreference = 'Continue'
$Log = 'C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\autostart-cleanup.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== 撤除 dev 开机自启动 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · DryRun=$DryRun ===")

$run = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$v = (Get-ItemProperty $run -ErrorAction SilentlyContinue).TrueToneCap
Say @("现值 = $(if ($v) { $v } else { '（不存在）' })")
if (-not $v) { Say @('没有该值 ⇒ 无需撤除'); exit 0 }

$devMarker = 'C:\PLAN\TrueToneCap\src\'
if ($v -notmatch [regex]::Escape($devMarker)) {
    Say @("★ 值不指向 dev 路径 ⇒ 这是用户自己设的，【不动】。请人工确认。")
    exit 0
}
if ($DryRun) { Say @('DryRun：只报告，不删除'); exit 0 }

Remove-ItemProperty -Path $run -Name 'TrueToneCap'
$after = (Get-ItemProperty $run -ErrorAction SilentlyContinue).TrueToneCap
Say @("删除后读回 = $(if ($after) { "★ 还在：$after" } else { '（已不存在）✓' })")

# 盘上那份 dev 设置也要改回 false，否则下次跑起来又会把键写回去
$set = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.settings.json'
if (Test-Path -LiteralPath $set) {
    $raw = [IO.File]::ReadAllText($set)
    $m = [regex]::Match($raw, '"AutoStart":(true|false)')
    Say @("设置里 AutoStart 原值 = $($m.Groups[1].Value)")
    if ($m.Success -and $m.Groups[1].Value -eq 'true') {
        $new = $raw.Replace('"AutoStart":true', '"AutoStart":false')
        [IO.File]::WriteAllText($set, $new, $utf8)
        $back = [regex]::Match([IO.File]::ReadAllText($set), '"AutoStart":(true|false)').Groups[1].Value
        Say @("改后读回 AutoStart = $back")
    }
}
Say @('★ 这条同时是【产品发现】：StartupManager 把"哪个构建最后按过保存"写进开机启动，')
Say @('   无任何提示 ⇒ 任何人跑一次开发版并保存设置，就会把用户的自启动指向一个测试构建。')
Write-Output "见 $Log"
