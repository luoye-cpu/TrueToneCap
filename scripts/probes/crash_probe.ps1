# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

$os = Get-CimInstance Win32_OperatingSystem
$now = Get-Date
"NOW        = $($now.ToString('yyyy-MM-dd HH:mm:ss'))"
"LASTBOOT   = $($os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss'))"
"UPTIME_MIN = $([math]::Round(($now - $os.LastBootUpTime).TotalMinutes, 1))"
""
"==== [1] 非预期关机 / 蓝屏 / 显卡驱动复位（System 日志，近 14 天）===="
# 41 = Kernel-Power（断电或蓝屏后重启）; 1001 = BugCheck/WER; 4101 = 显示驱动停止响应后恢复(TDR); 6008 = 脏关机
$sys = Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $now.AddDays(-14) }
foreach ($e in ($sys | Where-Object { $_.Id -in 41, 1001, 4101, 6008 } | Select-Object -First 20)) {
    $first = ($e.Message -split '[\r\n]+')[0]
    "{0}  Id={1,-5} {2,-46} {3}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $e.Id, $e.ProviderName, $first
}
""
"==== [2] nvlddmkm / amdkmb / dxgkrnl（GPU 驱动侧事件，近 14 天）===="
foreach ($e in ($sys | Where-Object { $_.ProviderName -match 'nvlddmkm|amdkmb|dxgkrnl|Display' } | Select-Object -First 20)) {
    $first = ($e.Message -split '[\r\n]+')[0]
    "{0}  Id={1,-5} {2,-24} {3}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $e.Id, $e.ProviderName, $first
}
""
"==== [3] 进程崩溃（Application 日志 Error 级，近 3 天）===="
$app = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = $now.AddDays(-3) }
foreach ($e in ($app | Select-Object -First 25)) {
    $m = ($e.Message -split '[\r\n]+') | Select-Object -First 3
    "{0}  Id={1,-5} {2}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $e.Id, $e.ProviderName
    foreach ($line in $m) { "        $line" }
}
""
"==== [4] WGC/DXGI 相关的运行时警告（近 3 天，含 Warning）===="
$appw = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $now.AddDays(-3) }
foreach ($e in ($appw | Where-Object { $_.Message -match 'TrueToneCap|nvenc|WGC|DXGI|d3d11|cuda' } | Select-Object -First 15)) {
    $first = ($e.Message -split '[\r\n]+')[0]
    "{0}  Id={1,-5} {2,-34} {3}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $e.Id, $e.ProviderName, $first
}
""
"==== [5] 内存 dump 文件 ===="
foreach ($d in @('C:\Windows\Minidump', 'C:\Windows\MEMORY.DMP', "$env:LOCALAPPDATA\CrashDumps")) {
    if (Test-Path $d) {
        Get-ChildItem $d | Sort-Object LastWriteTime -Descending | Select-Object -First 6 | ForEach-Object {
            "{0}  {1,12}  {2}" -f $_.LastWriteTime.ToString('MM-dd HH:mm:ss'), $_.Length, $_.FullName
        }
    }
}
""
"==== [6] WER 报告存档（近 3 天，含蓝屏与进程崩溃）===="
Get-ChildItem "$env:PROGRAMDATA\Microsoft\Windows\WER\ReportArchive","$env:PROGRAMDATA\Microsoft\Windows\WER\ReportQueue" -Directory |
    Where-Object { $_.LastWriteTime -gt $now.AddDays(-3) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 12 | ForEach-Object {
        "{0}  {1}" -f $_.LastWriteTime.ToString('MM-dd HH:mm:ss'), $_.Name
    }
""
"==== [7] 本机 GPU / 驱动版本 ===="
Get-CimInstance Win32_VideoController | ForEach-Object {
    "{0} | Driver {1} | {2} | VRAM {3}MB" -f $_.Name, $_.DriverVersion, $_.Status, [math]::Round($_.AdapterRAM / 1MB)
}
""
"==== [8] 电源计划里是否禁用了休眠/快速启动（影响 41 号事件解读）===="
$cs = Get-CimInstance Win32_ComputerSystem
"HibernateAvailable = $($cs.HibernationAvailable)"
$os.TotalVisibleMemorySize = $null
"---- done ----"
