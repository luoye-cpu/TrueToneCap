# 判据（不碰产品）：用 GDI+ 直接画 E713，看这台机器的符号字体本身能不能出字形
#
# 为什么要这一发：前面已经排掉 XAML 层（三种符号字体、裸 TextBlock、导航栏内外都一样）、
# 排掉 DWriteCore（挪走 dll 应用照跑、照样豆腐块）、排掉取图假象（PrintWindow 与直接抓屏一致）。
# 剩下的可能是"字体本身/系统回落数据坏了"，那就与本产品无关，也不该由产品的修法去兜。
# 用 System.Drawing（GDI+）画一次：它是另一条完全独立的文字栈，能画出来就说明字体可用，
# 问题回到 XAML 文字栈；画不出来就是机器侧的字体问题，产品层修不了。
param(
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\gdiplus-glyph.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\gdiplus-glyph.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== GDI+ 画符号字形 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Add-Type -AssemblyName System.Drawing

$families = @('Segoe MDL2 Assets', 'Segoe Fluent Icons', 'Segoe UI Symbol', 'Microsoft YaHei')
foreach ($fam in $families) {
    $probe = [char]0xE713   # Settings 齿轮
    try {
        $f = New-Object System.Drawing.Font($fam, 28, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $bmp = New-Object System.Drawing.Bitmap 120, 60
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::White)
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.DrawString([string]$probe, $f, [System.Drawing.Brushes]::Black, 8, 8)
        # 数一下墨迹：只有 .notdef 方框时墨迹像素会明显偏少且呈"框"形
        $ink = 0
        for ($y = 0; $y -lt $bmp.Height; $y += 2) {
            for ($x = 0; $x -lt $bmp.Width; $x += 2) {
                $p = $bmp.GetPixel($x, $y)
                if ($p.R -lt 128 -and $p.G -lt 128 -and $p.B -lt 128) { $ink++ }
            }
        }
        $real = $f.Name
        $g.Dispose(); $bmp.Dispose(); $f.Dispose()
        Say @("  $fam ⇒ 字体名解析为「$real」 墨迹采样 $ink 点")
    } catch {
        Say @("  $fam ⇒ ★ 构造失败：$($_.Exception.Message)")
    }
}
# 再单独存一张 E713 的图，供人眼对比（齿轮 vs 方框）
try {
    $f2 = New-Object System.Drawing.Font('Segoe MDL2 Assets', 40, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $b2 = New-Object System.Drawing.Bitmap 200, 80
    $g2 = [System.Drawing.Graphics]::FromImage($b2)
    $g2.Clear([System.Drawing.Color]::White)
    $g2.DrawString([string][char]0xE713, $f2, [System.Drawing.Brushes]::Black, 10, 8)
    $b2.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g2.Dispose(); $b2.Dispose()
    Say @("已存对照图 ⇒ $Out（应是一枚齿轮）")
} catch { Say @("★ 存图失败：$($_.Exception.Message)") }
exit 0
