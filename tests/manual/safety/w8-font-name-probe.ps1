# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 量三件事：注册表里的符号字体友好名、SegoeIcons.ttf 内部真实的名字表、
# 以及本进程（GDI+ / WPF 两条）能不能按这些名字取到字形。
# 目的：② 的根因是"按友好名解析失败"，那就先把"到底注册成了什么名字"量出来。
$ErrorActionPreference = 'Continue'
$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\font-names.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)

Say @('=== 1. HKLM Fonts 注册表里与 Segoe/Icon/Symbol 相关的条目 ===')
$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts'
$props = (Get-Item $k).GetValueNames()
$hits = @($props | Where-Object { $_ -match 'Segoe|Icon|Symbol' })
Say @("注册条目总数 $($props.Count)，命中 Segoe|Icon|Symbol 的 $($hits.Count) 条：")
foreach ($n in $hits) { Say @("  [$n] => $((Get-ItemProperty -Path $k).$n)") }

Say @('')
Say @('=== 2. 反向：注册表里指向 SegoeIcons.ttf 的友好名有哪几个 ===')
$toFile = @($hits | Where-Object { (Get-ItemProperty -Path $k).$_ -match 'SegoeIcons' })
foreach ($n in $toFile) { Say @("  $n") }
Say @("⇒ 共 $($toFile.Count) 个名字挂在同一个文件上")

Say @('')
Say @('=== 3. WPF 的字体工厂能不能列出这三个家族（这是与 WinUI 最接近的一条现成读数）===')
try {
    Add-Type -AssemblyName PresentationCore, WindowsBase
    $fams = @([System.Windows.Media.Fonts]::SystemFontFamilies)
    Say @("WPF 可见字体家族总数 = $($fams.Count)")
    foreach ($probe in @('Segoe MDL2 Assets', 'Segoe Fluent Icons', 'Segoe UI Symbol', 'Segoe UI')) {
        $m = @($fams | Where-Object { $_.Source -eq $probe -or $_.Source -match [regex]::Escape($probe) })
        $src = if ($m.Count) { ($m | ForEach-Object { $_.Source }) -join ' | ' } else { '（无）' }
        Say @("  按名 '$probe' ⇒ $($m.Count) 个匹配：$src")
    }
    # 关键一问：挂在 SegoeIcons.ttf 上的家族，WPF 报出来的 Source 到底是什么
    $byFile = @($fams | Where-Object { $_.Source -match 'SegoeIcons' })
    Say @("  直接以文件路径 'SegoeIcons.ttf' 可寻的家族数 = $($byFile.Count)")
    foreach ($f in $byFile) { Say @("    Source = $($f.Source)  友好名 = $(($f.FamilyNames | ForEach-Object { $_.Value }) -join ',')") }
} catch { Say @("  ★ WPF 探测异常：$($_.Exception.Message)") }

Say @('')
Say @('=== 4. 同一批名字，GDI+ 能不能解析（已知 GDI+ 能画出字形，这里确认它靠哪个名字）===')
try {
    Add-Type -AssemblyName System.Drawing
    foreach ($probe in @('Segoe MDL2 Assets', 'Segoe Fluent Icons', 'Segoe UI Symbol')) {
        $f = New-Object System.Drawing.Font($probe, 16)
        Say @("  '$probe' ⇒ 实际拿到家族名 '$($f.Name)' (GDI+ 找不到时会静默回落到默认字体，回落标志见下一行)")
        $fam = New-Object System.Drawing.FontFamily($probe)
        Say @("    FontFamily('$probe').Name = '$($fam.Name)'  ⇒ 与请求名相同？$($fam.Name -eq $probe)")
    }
} catch { Say @("  ★ GDI+ 探测异常：$($_.Exception.Message)") }
Write-Output "见 $Log"
