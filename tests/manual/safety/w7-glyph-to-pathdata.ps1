# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 把 Segoe 符号字体的字形取成 XAML Path Data（离线工具，不改产品）
#
# 为什么走 GDI+ 而不是我手写几何：手写 8 枚图标等于自己设计一套视觉，观感得你重判；
# 而 GDI+ 能解析 Segoe MDL2 Assets（本轮已实测它画得出齿轮），AddString 直接把字形
# 转成 GraphicsPath ⇒ 取到的是【系统本来要画的那枚轮廓】，只是换了渲染路径（Path 而非字体）。
# CheckBox 的勾在这个进程里一直是 Path 画的且显示正常 ⇒ 这条渲染路径可用。
#
# 精度：以 em=100 取点再 Flatten 成折线，最后按字形包围盒归一到 0..20 的坐标系并保留两位小数。
param(
    [string[]]$Codes = @('E713'),
    [string]$Family = 'Segoe MDL2 Assets',
    [double]$Box = 20,
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\glyph-paths.txt'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sb = New-Object System.Text.StringBuilder
# 用 -File 调用时 bash 把 `E8B7,E722` 当成【一个】参数传进来 ⇒ [string[]] 只拿到一个含逗号的元素，
# 于是 ToInt32 抛 FormatException。这里自己再切一次，两种传法都吃。
$Codes = @($Codes | ForEach-Object { $_ -split '[,;\s]+' } | Where-Object { $_ })
foreach ($hex in $Codes) {
    $cp = [Convert]::ToInt32($hex, 16)
    $ch = [char]::ConvertFromUtf32($cp)

    $f = New-Object System.Drawing.Font($Family, 100, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fmt = [System.Drawing.StringFormat]::GenericTypographic
    $gp.AddString($ch, $Family, [int][System.Drawing.FontStyle]::Regular, 100, (New-Object System.Drawing.PointF(0, 0)), $fmt)
    $gp.Flatten()
    $pts = $gp.PathPoints
    $types = $gp.PathTypes
    $b = $gp.GetBounds()
    $gp.Dispose(); $f.Dispose()

    if ($b.Width -le 0 -or $b.Height -le 0) {
        [void]$sb.AppendLine("$hex ⇒ ★ 取到空包围盒（字体没解析出这个字形）")
        continue
    }
    # 按较长边归一到 Box，并居中到 (Box/2, Box/2)
    $s = $Box / [Math]::Max($b.Width, $b.Height)
    $offX = ($Box - $b.Width * $s) / 2 - $b.X * $s
    $offY = ($Box - $b.Height * $s) / 2 - $b.Y * $s

    $segs = New-Object System.Collections.Generic.List[string]
    $figStart = $false
    for ($i = 0; $i -lt $pts.Count; $i++) {
        $t = [int]$types[$i] -band 0x03
        $x = [Math]::Round($pts[$i].X * $s + $offX, 2)
        $y = [Math]::Round($pts[$i].Y * $s + $offY, 2)
        if ($t -eq 0) { $segs.Add("M$x,$y") } else { $segs.Add("L$x,$y") }
        if (([int]$types[$i] -band 0x30) -eq 0x20) { $segs.Add('Z') }   # 图元结束
    }
    $data = ($segs -join ' ')
    [void]$sb.AppendLine("$hex 点数=$($pts.Count) 包围盒=$([Math]::Round($b.Width,1))x$([Math]::Round($b.Height,1)) 数据长度=$($data.Length)")
    [void]$sb.AppendLine("$hex DATA $data")
}
[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "已写 $Out"
Get-Content -LiteralPath $Out -Encoding UTF8 | ForEach-Object { if ($_.Length -gt 120) { $_.Substring(0,120) + ' …' } else { $_ } }
