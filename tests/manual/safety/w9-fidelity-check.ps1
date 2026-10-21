# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# B1 的前提校验：14px 下语义图标看不清，到底是【我抽的轮廓失真】还是【尺寸本身就不够】？
#
# 为什么先量这个：抽取脚本 w7-glyph-to-pathdata.ps1 第 8 行写明"以 em=100 取点再 Flatten"，
# 之后归一到 0..20 保留两位小数 ⇒ 量化步长 0.01/20 = 0.05% em。若真是精度问题，
# 提高抽取 em 才有意义；若不是，B1（重抽到 24em）就是在修一个不存在的东西。
#
# 非循环对照：参考侧用 GDI+ DrawString 直接按像素字号画字形（走 hinting + 灰度 AA，
# 完全不碰我抽出来的那串 Data）；被测侧把我抽的 Data 缩到同一视觉尺寸后填充。
# 两侧各自算：墨水量、bbox、以及"最细笔画有几像素宽"。
param(
    [int[]]$Sizes = @(21, 24, 27, 32),          # 14/16/18/21 DIP 在 150% 缩放下的设备像素
    [string]$Xaml = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\SilentCaptureToast.xaml',
    [string]$Out = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\fidelity'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.RegularExpressions;

public static class Fid
{
    // 把 WinUI 的 mini-language（本仓数据只用到 M/L/Z 与 F0 前缀）拼成一条路径、一次填充。
    // 两条教训都写进来了：① 必须拼成一条再 FillPath，逐子图元各填一次会把孔洞填满；
    // ② 不能把整串点一次性交给 GraphicsPath(点阵,类型阵)——子图元的第一个点必须标 0x00，
    //    标错会直接 "Parameter is not valid"（本脚本第一版就死在这里）。
    public static GraphicsPath Parse(string data, float scale)
    {
        var gp = new GraphicsPath();
        gp.FillMode = FillMode.Alternate;
        var cur = new List<PointF>();
        Action flush = delegate
        {
            if (cur.Count > 2) gp.AddPolygon(cur.ToArray());
            cur.Clear();
        };
        foreach (Match m in Regex.Matches(data, @"([MLZ])\s*([^MLZ]*)"))
        {
            string op = m.Groups[1].Value, arg = m.Groups[2].Value.Trim();
            if (op == "M") { flush(); }
            else if (op == "Z") { flush(); continue; }
            if (arg.Length == 0) continue;
            foreach (string tok in arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] ab = tok.Split(',');
                cur.Add(new PointF(float.Parse(ab[0]) * scale, float.Parse(ab[1]) * scale));
            }
        }
        flush();
        return gp;
    }

    public static Rectangle Ink(Bitmap bmp, int thr)
    {
        int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp.GetPixel(x, y), thr))
                { if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
        return maxx < 0 ? Rectangle.Empty : new Rectangle(minx, miny, maxx - minx + 1, maxy - miny + 1);
    }

    public static bool IsInk(Color c, int thr) { return (c.R + c.G + c.B) / 3 < thr; }

    public static int InkCount(Bitmap bmp, int thr)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) if (IsInk(bmp.GetPixel(x, y), thr)) n++;
        return n;
    }

    // 最细笔画：逐行扫，取所有"连续墨水段"长度的最小值（>=1 才算，纯 AA 抖动不算笔画）
    public static int ThinnestRun(Bitmap bmp, int thr)
    {
        int best = int.MaxValue;
        for (int y = 0; y < bmp.Height; y++)
        {
            int run = 0;
            for (int x = 0; x < bmp.Width; x++)
            {
                if (IsInk(bmp.GetPixel(x, y), thr)) run++;
                else { if (run > 0 && run < best) best = run; run = 0; }
            }
            if (run > 0 && run < best) best = run;
        }
        return best == int.MaxValue ? 0 : best;
    }

    public static Bitmap RenderData(string data, int px, string family, string ch)
    {
        int pad = 6;
        var bmp = new Bitmap(px + pad * 2, px + pad * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 0..20 的框 → px 设备像素；与字体侧一样按 em 对齐（字形占满 em 框）
            using (var gp = Parse(data, px / 20f))
                g.FillPath(Brushes.Black, gp);
        }
        return bmp;
    }

    public static Bitmap RenderFont(string family, string ch, int px)
    {
        int pad = 6;
        var bmp = new Bitmap(px * 2 + pad * 2, px * 2 + pad * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var f = new System.Drawing.Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel))
                g.DrawString(ch, f, Brushes.Black, new PointF(pad, pad));
        }
        return bmp;
    }
}
'@
if (-not ('Fid' -as [type])) { Write-Output '★ P/Invoke 类型没编出来 ⇒ 未判'; exit 2 }

# 1) 从 XAML 里取四枚语义图标的 Data 与对应码位
$x = [IO.File]::ReadAllText($Xaml)
$want = @{ 'IconSuccess' = @('E73E', [char]0xE73E); 'IconWarning' = @('E7BA', [char]0xE7BA);
           'IconError' = @('E783', [char]0xE783); 'IconInfo' = @('E946', [char]0xE946) }
$paths = @()
foreach ($k in @('IconSuccess', 'IconWarning', 'IconError', 'IconInfo'))
{
    if ($x -match [regex]::Escape($k) + '[^>]*?Data="([^"]+)"') { $paths += , @($k, $Matches[1]) }
    else { Write-Output "★ 取不到 $k 的 Data ⇒ 判据缺失，不猜"; exit 2 }
}
Write-Output "已从 XAML 取到 $($paths.Count) 枚 Data"

# 2) 尺寸 × 图标 的墨水/笔画对照
$log = @()
foreach ($px in $Sizes)
{
    foreach ($pr in $paths)
    {
        $name = $pr[0]; $data = $pr[1]
        $cp = $want[$name][0]; $ch = $want[$name][1]
        $fb = [Fid]::RenderFont('Segoe Fluent Icons', $ch, $px)
        $bd = [Fid]::RenderData($data, $px, 'Segoe Fluent Icons', $ch)
        $rb = [Fid]::Ink($fb, 200); $rd = [Fid]::Ink($bd, 200)
        $ib = [Fid]::InkCount($fb, 200);               $id = [Fid]::InkCount($bd, 200)
        $tb = [Fid]::ThinnestRun($fb, 200);            $td = [Fid]::ThinnestRun($bd, 200)
        $log += ('{0,-12} {1,3}px  字体直画: 框{2}x{3} 墨{4,5} 最细笔画{5,2}px   我的Data: 框{6}x{7} 墨{8,5} 最细笔画{9,2}px   墨水比={10:F2}' -f `
            $name, $px, $rb.Width, $rb.Height, $ib, $tb, $rd.Width, $rd.Height, $id, $td, $(if ($ib) { $id / $ib } else { 0 }))
        $fb.Dispose(); $bd.Dispose()
    }
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
[IO.File]::WriteAllLines((Join-Path $Out 'fidelity.txt'), $log, (New-Object System.Text.UTF8Encoding($false)))
$log | ForEach-Object { Write-Output $_ }
Write-Output "⇒ 读法：若【墨水比≈1 且两侧最细笔画同量级】⇒ 轮廓忠实，14px 的问题是尺寸不是抽取 ⇒ B1 前提不成立。"
