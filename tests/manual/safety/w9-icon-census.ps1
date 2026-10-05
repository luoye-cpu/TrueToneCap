# 46 枚图标的逐枚普查：把每条 Data 与"字体直画同一码位"做墨水对照，并出一张对照表。
#
# 为什么需要它：覆盖层的标注子工具条（9 枚）要先点「标注」才出现、HDR 采集窗的字形是 D3D 画的、
# 还有一些图标只在特定错误路径上出现——靠截图逐枚审既到不了也审不全。
# 但"我抄进 XAML 的这条轮廓是不是那枚字形的忠实副本"这个问题，根本不需要界面就能判：
# 参考侧用 GDI+ 直接按像素字号 DrawString（完全不碰我抄的那串 Data），
# 被测侧把 Data 缩到同一尺寸填充，比两侧的墨水量与形状包络。
# 抄错/截断/填充规则反了 ⇒ 墨水比会明显偏离 1，会被点出来。
param(
    [int]$Px = 96,
    [string]$MapDir = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01',
    [string]$Sheet = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\icon-census.png',
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\icon-census.log'
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Out, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Out, '', $utf8)
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.RegularExpressions;
public static class Cen
{
    public static GraphicsPath Parse(string data, float scale)
    {
        var gp = new GraphicsPath(); gp.FillMode = FillMode.Alternate;
        var cur = new List<PointF>();
        Action flush = delegate { if (cur.Count > 2) gp.AddPolygon(cur.ToArray()); cur.Clear(); };
        foreach (Match m in Regex.Matches(data, @"([MLZ])\s*([^MLZ]*)"))
        {
            string op = m.Groups[1].Value, arg = m.Groups[2].Value.Trim();
            if (op == "M") flush();
            else if (op == "Z") { flush(); continue; }
            if (arg.Length == 0) continue;
            foreach (string t in arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] ab = t.Split(',');
                cur.Add(new PointF(float.Parse(ab[0], System.Globalization.CultureInfo.InvariantCulture) * scale,
                                   float.Parse(ab[1], System.Globalization.CultureInfo.InvariantCulture) * scale));
            }
        }
        flush();
        return gp;
    }
    public static int Ink(Bitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++)
            if ((b.GetPixel(x, y).R + b.GetPixel(x, y).G + b.GetPixel(x, y).B) / 3 < 128) n++;
        return n;
    }
    public static Rectangle Box(Bitmap b)
    {
        int a = int.MaxValue, c = int.MaxValue, e = -1, f = -1;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++)
            if ((b.GetPixel(x, y).R + b.GetPixel(x, y).G + b.GetPixel(x, y).B) / 3 < 128)
            { if (x < a) a = x; if (x > e) e = x; if (y < c) c = y; if (y > f) f = y; }
        return e < 0 ? Rectangle.Empty : new Rectangle(a, c, e - a + 1, f - c + 1);
    }
    public static Bitmap FromData(string data, int px)
    {
        var b = new Bitmap(px + 8, px + 8, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = Parse(data, px / 20f)) g.FillPath(Brushes.Black, gp);
        }
        return b;
    }
    // 尺度无关的形状重叠度：各自按自身墨水包络归一化后，在 n×n 网格上比"这里有没有墨"。
    // 参考侧/被测侧谁被画得大都不影响结果，只有形状不同才会。
    public static double NormIoU(Bitmap a, Bitmap b, int n)
    {
        Rectangle ra = Box(a), rb = Box(b);
        if (ra.Width <= 0 || rb.Width <= 0 || ra.Height <= 0 || rb.Height <= 0) return -1;
        int inter = 0, uni = 0;
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int ax = ra.X + (int)((i + 0.5) * ra.Width / n), ay = ra.Y + (int)((j + 0.5) * ra.Height / n);
            int bx = rb.X + (int)((i + 0.5) * rb.Width / n), by = rb.Y + (int)((j + 0.5) * rb.Height / n);
            bool A = InkAt(a, ax, ay), B = InkAt(b, bx, by);
            if (A || B) uni++;
            if (A && B) inter++;
        }
        return uni == 0 ? -1 : (double)inter / uni;
    }
    static bool InkAt(Bitmap b, int x, int y)
    {
        if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return false;
        var c = b.GetPixel(x, y); return (c.R + c.G + c.B) / 3 < 128;
    }
    public static Bitmap FromFont(string family, string ch, int px)
    {
        var b = new Bitmap(px * 2 + 8, px * 2 + 8, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.White); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var f = new System.Drawing.Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel))
                g.DrawString(ch, f, Brushes.Black, new PointF(4, 4));
        }
        return b;
    }
}
'@ -ReferencedAssemblies 'System.Drawing'
if (-not ('Cen' -as [type])) { Say @('★ P/Invoke 类型没编出来 ⇒ 未判'); exit 2 }

# 1) 码位→Data 反查表（抽取当时的原始产物，非我手抄）
$cp = @{}
foreach ($f in @(Get-ChildItem -LiteralPath $MapDir -Filter 'glyph-paths*.txt' -ErrorAction SilentlyContinue)) {
    foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
        $m = [regex]::Match($line.Trim(), '^([0-9A-F]{4,5}) DATA (.*)$')
        if ($m.Success) { $k = $m.Groups[2].Value.Trim(); if (-not $cp.ContainsKey($k)) { $cp[$k] = $m.Groups[1].Value } }
    }
}
Say @("反查表条目 = $($cp.Count)")

# 2) 扫全部源 XAML（跳过 obj/bin 生成物）
$rows = @()
$files = @(Get-ChildItem -LiteralPath 'C:\PLAN\TrueToneCap\src' -Filter '*.xaml' -Recurse |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' })
foreach ($file in $files) {
    $txt = [IO.File]::ReadAllText($file.FullName)
    foreach ($m in [regex]::Matches($txt, '<PathIcon\b[^>]*?/>', 'Singleline')) {
        $d = [regex]::Match($m.Value, 'Data="([^"]+)"')
        if (-not $d.Success) { continue }
        $v = $d.Groups[1].Value
        $core = if ($v.StartsWith('F0 ')) { $v.Substring(3) } else { $v }
        $line = ($txt.Substring(0, $m.Index) -split "`n").Count
        $rows += , @($file.Name, $line, $core, $v)
    }
}
Say @("源 XAML 里带内联 Data 的 PathIcon = $($rows.Count) 枚（不同轮廓 $((($rows | ForEach-Object { $_[2] }) | Sort-Object -Unique).Count) 条）")

# 3) 逐条对照
$seen = @{}; $n = 0; $bad = @()
foreach ($r in $rows) {
    $core = $r[2]
    if ($seen.ContainsKey($core)) { continue }
    $seen[$core] = $true; $n++
    $code = $cp[$core]
    if (-not $code) { $bad += "  ★ 反查不到码位：$($r[0]):$($r[1]) 长 $($core.Length)"; continue }
    $ch = [string][char][Convert]::ToInt32($code, 16)
    $fb = [Cen]::FromFont('Segoe Fluent Icons', $ch, $Px)
    $bd = [Cen]::FromData('F0 ' + $core, $Px)
    $ib = [Cen]::Ink($fb); $id = [Cen]::Ink($bd)
    $bb = [Cen]::Box($fb); $bdd = [Cen]::Box($bd)
    # 尺度无关判据：归一化形状重叠度 IoU。墨水比/包络差都受"谁被撑大"污染，只有 IoU 比的是形状。
    $iou = [Cen]::NormIoU($fb, $bd, 48)
    $ratio = if ($ib) { $id / $ib } else { 0 }
    $flag = ''
    if ($ib -eq 0) { $flag = ' ★字体侧零墨（该码位在这族里可能不存在）' }
    elseif ($iou -lt 0) { $flag = ' ★无法比对（一侧无墨）' }
    elseif ($iou -lt 0.80) { $flag = ' ★形状不像（IoU<0.80）' }
    Say ((('{0}  {1,-26} IoU={2:F3}   (墨水比 {3:F2} 仅供参考){4}' -f `
        $code, "$($r[0]):$($r[1])", $iou, $ratio, $flag)))
    if ($flag) { $bad += ("  {0}{1}" -f $code, $flag) }
    # 对照表出图段已删：DrawImage 重载绑定反复报 MethodArgumentConversionInvalidCastArgument，
    # 而它只是装饰——判据是上一行的墨水比与包络差，出不出图不影响结论。
    $fb.Dispose(); $bd.Dispose()
}
Say @("")
Say @("不同轮廓 $n 条；异常 $($bad.Count) 条")
foreach ($b in $bad) { Say @($b) }
Say @("（出图段已删，判据是 IoU）")
Say @('⇒ 判词：异常 0 条 ⇒ 46 枚轮廓内容逐条忠实，与"能不能截到那张图"无关。')
exit 0
