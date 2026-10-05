# 候选字形对照表：把一批码点用 GDI+ 画成带标签的格子，供人眼挑
#
# 为什么要它：我要给「OCR / 翻译」和「运行日志」换两枚语义对的图标，而 MDL2 码位的含义
# 凭记忆猜必错（本轮已经吃过一次：E945 我以为是"翻译"、E8D4 我以为是"日志"，画出来是闪电和图钉）。
# GDI+ 能正常解析 Segoe MDL2 Assets（实测），所以直接把它画出来看。
param(
    [string]$Codes = 'E945,E8D4,E774,E164,E12B,E7C3,E9D2,E8A1,EA37,E7BC,E9F9,E8FD,E13B,E8F1,E76F,E8F4,E713,E8BB,E8C8,E838,E8E5,E73E,E7BA,E783,E946,E8FB,E70F,E72A,E74E,E7A6,E7A7,E7F1,E84F,E8A9,E8B7,E8BB,E8D8,E909',
    [string]$Family = 'Segoe MDL2 Assets',
    [int]$Cell = 74,
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\lane0-contact-sheet.png'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$list = @($Codes -split '[,;\s]+' | Where-Object { $_ })
$cols = 8
$rows = [Math]::Ceiling($list.Count / $cols)
$bmp = New-Object System.Drawing.Bitmap ($cols * $Cell), ($rows * $Cell)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::White)

$fGlyph = New-Object System.Drawing.Font($Family, 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fLabel = New-Object System.Drawing.Font('Consolas', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
for ($i = 0; $i -lt $list.Count; $i++) {
    $hex = $list[$i]
    $cx = ($i % $cols) * $Cell; $cy = [Math]::Floor($i / $cols) * $Cell
    $g.DrawRectangle([System.Drawing.Pens]::LightGray, $cx, $cy, $Cell - 1, $Cell - 1)
    try {
        $ch = [string][char]::ConvertFromUtf32([Convert]::ToInt32($hex, 16))
        $g.DrawString($ch, $fGlyph, [System.Drawing.Brushes]::Black, ($cx + 18), ($cy + 4))
    } catch {
        $g.DrawString('?', $fLabel, [System.Drawing.Brushes]::Red, ($cx + 20), ($cy + 12))
    }
    $g.DrawString($hex, $fLabel, [System.Drawing.Brushes]::DarkSlateGray, ($cx + 4), ($cy + 44))
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $fGlyph.Dispose(); $fLabel.Dispose()
Write-Output "已写 $Out（$($list.Count) 格，${cols}×${rows}）"
