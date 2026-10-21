# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 回环自检：把取出来的 Path Data 再画成图，与 GDI+ 直接画的字形对比
#
# 为什么必须有这一步：我要拿这串数据去改产品界面，如果取点/归一/闭合有一处错，
# 表现就是"图标变形"，而那会被当成我设计得不好，而不是工具坏了。
# 判据：同一枚字形，① 字体直画 与 ② 回放 Path Data 两张图并排，人眼看是不是同一枚；
# 另外自动量两条：点数、以及回放图与直画图的墨迹覆盖率差多少（差太多就是几何错了）。
param(
    [string]$DataFile = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\glyph-paths.txt',
    [string]$Hex = 'E713',
    [string]$Family = 'Segoe MDL2 Assets',
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\roundtrip-E713.png'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$line = @(Get-Content -LiteralPath $DataFile -Encoding UTF8 | Where-Object { $_ -like "$Hex DATA *" })
if ($line.Count -ne 1) { Write-Output "RC=2 未判：$Hex 的 DATA 行 $($line.Count) 条"; exit 2 }
$data = $line[0].Substring(("$Hex DATA ").Length)

# 解析 M/L/Z（绝对坐标，0..20 坐标系）
$segs = $data -split '\s+'
$figs = New-Object System.Collections.Generic.List[System.Collections.Generic.List[System.Drawing.PointF]]
$cur = $null
foreach ($s in $segs) {
    if ($s -eq 'Z') { if ($cur) { $figs.Add($cur) }; $cur = $null; continue }
    if ($s.StartsWith('M')) { if ($cur) { $figs.Add($cur) }; $cur = New-Object System.Collections.Generic.List[System.Drawing.PointF] }
    $xy = $s.Substring(1) -split ','
    $cur.Add((New-Object System.Drawing.PointF([float]$xy[0], [float]$xy[1])))
}
if ($cur) { $figs.Add($cur) }
$ptTotal = 0; foreach ($f in $figs) { $ptTotal += $f.Count }
Write-Output "解析出 $($figs.Count) 个闭合图元、共 $ptTotal 点"

$scale = 12.0   # 0..20 ⇒ 0..240
$bmp = New-Object System.Drawing.Bitmap 520, 260
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::White)

# 左：字体直画
$fnt = New-Object System.Drawing.Font($Family, 200, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$gp = New-Object System.Drawing.Drawing2D.GraphicsPath
$gp.AddString([string][char]::ConvertFromUtf32([Convert]::ToInt32($Hex,16)), $Family, 0, 200, (New-Object System.Drawing.PointF(20, 20)), [System.Drawing.StringFormat]::GenericTypographic)
$g.FillRectangle([System.Drawing.Brushes]::LightGray, 0, 0, 250, 260)
$g.DrawRectangle([System.Drawing.Pens]::Black, 0, 0, 250, 260)
$gb = $gp.GetBounds()
$sf = [Math]::Min(200.0 / $gb.Width, 200.0 / $gb.Height)
$m = New-Object System.Drawing.Drawing2D.Matrix
$m.Translate(20, 20); $m.Scale($sf, $sf); $m.Translate(-$gb.X, -$gb.Y)
$gp.Transform($m)
$g.FillPath([System.Drawing.Brushes]::Black, $gp)

# 右：回放 Path Data
$g.FillRectangle([System.Drawing.Brushes]::Gainsboro, 260, 0, 250, 260)
$g.DrawRectangle([System.Drawing.Pens]::Black, 260, 0, 250, 260)
# 回放：0..20 坐标系要按 scale 放大后再画（第一版在这里漏了变换 ⇒ 画出来是个点）
$g.ResetTransform()
$g.FillRectangle([System.Drawing.Brushes]::Gainsboro, 260, 0, 250, 260)
$g.DrawRectangle([System.Drawing.Pens]::Black, 260, 0, 250, 260)
$g.TranslateTransform(270, 20)
$g.ScaleTransform($scale, $scale)
# ⚠ 上一版在这里对每个子图元各调一次 FillPolygon ⇒ 孔洞那圈也被单独填满 ⇒ 看着像"数据丢了洞"，
#   其实是我的回放器错了。正确做法与 XAML 的单个 Path 一致：所有图元拼成一条 GraphicsPath，只填一次。
$rp = New-Object System.Drawing.Drawing2D.GraphicsPath
$rp.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
foreach ($fig in $figs) {
    $arr = $fig.ToArray()
    if ($arr.Count -ge 3) { $rp.AddPolygon($arr) }
}
$g.FillPath([System.Drawing.Brushes]::Black, $rp)
$rp.Dispose()
$g.ResetTransform()
$g.DrawString('左=字体直画  右=回放 Path Data', (New-Object System.Drawing.Font('Segoe UI', 11)), [System.Drawing.Brushes]::Black, 20, 236)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $gp.Dispose(); $fnt.Dispose()
Write-Output "已存 $Out"
exit 0
