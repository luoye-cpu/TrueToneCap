# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# R3 的 T3：注册表里已有自启动项时，起一个 dev 构建会不会把它删掉/改指。
#
# 旧代码在 MainWindow 构造路径里做 StartupManager.IsEnabled = _settings.AutoStart，
# 而 GetAppPath() 取 Environment.ProcessPath ⇒ 本机盘上 AutoStart=false，
# 于是【任何一次启动】都会 DeleteValue 掉用户真正注册过的那一项。
# 这一格就是拿一个哨兵值去撞这条路径：修之前必被删，修之后必须原样还在。
#
# 只动 HKCU\...\Run 下名为 TrueToneCap 的那一个值；起手记下它原本在不在、值是什么，
# 收尾按原样还原（原本没有就删干净），并复算回读。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\recovery-2026-10-03\autostart-guard.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== R3 自启动守卫 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'TrueToneCap'
$before = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
Say @("起手该值 = $(if ($null -eq $before) { '<不存在>' } else { "「$before」" })")
if ($before) { Say @('★ 用户已注册自启动 ⇒ 本格不改写它，只做"起一次看会不会被删"的读数后原样放回') }

$sentinel = '"C:\TTC-R3-SENTINEL\not-real\TrueToneCap.exe" --autostart'
$rc = 2
$proc = $null
try {
    Set-ItemProperty -Path $key -Name $name -Value $sentinel
    $placed = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
    Say @("已放哨兵 = $(if ($placed -eq $sentinel) { '✓ 写入成功' } else { "❌ 回读不等：$placed" })")
    if ($placed -ne $sentinel) { $rc = 2; throw '哨兵没写进去，未判' }

    $dir = Split-Path -Parent $Exe
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("起了 dev 构建 PID=$($proc.Id)，等 14 s 让它走完构造路径")
    Start-Sleep -Seconds 14

    $after = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -eq $after) {
        Say @('❌ 哨兵【被删了】⇒ 启动路径仍在写注册表，R3 没修住')
        $rc = 1
    } elseif ($after -ne $sentinel) {
        Say @("❌ 哨兵被改指到「$after」⇒ 启动路径仍会把它漂到最后的 exe，R3 没修住")
        $rc = 1
    } else {
        Say @('✓ 哨兵逐字还在 ⇒ 启动路径已经不写注册表了')
        $lg = Join-Path $dir "log\app_$(Get-Date -Format 'yyyy-MM-dd').log"
        if (Test-Path -LiteralPath $lg) {
            $warn = @(Get-Content -LiteralPath $lg -Encoding UTF8 | Select-Object -Last 400 |
                      Where-Object { $_ -match '开机自启动的实际状态' })
            Say @("  一致性告警行数（本次起的那一实例应留一条）= $($warn.Count)")
            if ($warn.Count) { Say @("   「$($warn[-1].Substring(0,[Math]::Min(150,$warn[-1].Length)))」") }
        }
        $rc = 0
    }
}
catch {
    Say @("RC=2 未判：$($_.Exception.Message)")
    $rc = 2
}
finally {
    if ($null -ne $proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { } }
    Start-Sleep -Seconds 2
    if ($null -eq $before) { Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $key -Name $name -Value $before }
    $restored = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
    $same = if ($null -eq $before -and $null -eq $restored) { $true } else { $restored -eq $before }
    Say @("还原：该值 = $(if ($null -eq $restored) { '<不存在>' } else { "「$restored」" }) ⇒ 与起手$(if ($same) {'一致 ✓'} else {'❌ 不一致'}))"
    )
    if (-not $same -and $rc -eq 0) { $rc = 3 }
    Say @("RC=$rc")
}
Write-Output "RC=$rc"
exit $rc
