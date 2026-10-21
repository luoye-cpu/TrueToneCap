# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 共用的截图统计尺子（2026-09-30）
# 为什么单拎出来：`preview-content-cell.ps1` 里那份内联实现如果和"判据有没有牙"那次验证用的是两份代码，
# 对照就不算数（同源自证）。现在两边都走这一份，并用"重算值必须等于日志值"当场核一次两版等价。
#
# 阈值口径（三条各挡一种失效，都不是审美判断）：
#   BlackPct ≥ 98  ⇒ 【仪器未判】：GDI 抓取碰到直接合成/MPO 通道会拿不到东西，不能读成"产品黑屏"
#                    （§25.6 就因为把 PrintWindow 抓不到当成判据红过一次）
#   WhitePct ≥ 95  ⇒ 【平白一片】：维护者肉眼报的那个症状
#   Kinds   <  8   ⇒ 【无结构】：既不是画面也不是纯色的第三种坏
Add-Type -AssemblyName System.Drawing

function Get-BitmapStats {
    param([string]$File, [int]$Grid = 60)
    $bmp = New-Object System.Drawing.Bitmap($File)
    try {
        $tot = 0; $white = 0; $black = 0; $sR = 0L; $sG = 0L; $sB = 0L; $vals = @{}
        for ($iy = 0; $iy -lt $Grid; $iy++) {
            for ($ix = 0; $ix -lt $Grid; $ix++) {
                $px = [int]($ix * ($bmp.Width - 1) / ($Grid - 1))
                $py = [int]($iy * ($bmp.Height - 1) / ($Grid - 1))
                $c = $bmp.GetPixel($px, $py)
                $tot++; $sR += $c.R; $sG += $c.G; $sB += $c.B
                if ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250) { $white++ }
                if ($c.R -le 5 -and $c.G -le 5 -and $c.B -le 5) { $black++ }
                $vals["$($c.R),$($c.G),$($c.B)"] = 1
            }
        }
        return [pscustomobject]@{
            File = $File; W = $bmp.Width; H = $bmp.Height; Tot = $tot; Kinds = $vals.Count
            WhitePct = [math]::Round(100.0 * $white / $tot, 2)
            BlackPct = [math]::Round(100.0 * $black / $tot, 2)
            Mean = "R$([math]::Round($sR/$tot,1)) G$([math]::Round($sG/$tot,1)) B$([math]::Round($sB/$tot,1))"
        }
    } finally { $bmp.Dispose() }
}

function Get-StatsVerdict {
    param($st)
    if ($st.BlackPct -ge 98) { return '仪器未判：抓回来近全黑' }
    if ($st.WhitePct -ge 95) { return '★ 复现"平白一片"' }
    if ($st.Kinds -lt 8)     { return '★ 无结构（颜色种数过少）' }
    return 'OK：有结构、非平白'
}
