// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/AnnotationRasterizer.cs
// 标注图层像素光栅化 — 与 XAML 预览共用同一形状语义，保证"预览所见 = 最终输出"
// 修复: 原实现 Arrow/Pen/Text 预览不可见、合成时画成矩形边框的错位

using System.Numerics;
using TrueToneCap.Core.Annotation;

namespace TrueToneCap.App.Services;

/// <summary>将标注图层光栅化到 BGRA8 像素缓冲（最终文件合成用）。</summary>
internal static class AnnotationRasterizer
{
    // ══════════════════════════════════════════════
    //  图层合成入口
    // ══════════════════════════════════════════════

    /// <summary>把一个图层合成到 BGRA8 像素缓冲（就地修改）。</summary>
    public static void RenderLayer(byte[] result, int imgW, int imgH, AnnotationLayer layer)
    {
        switch (layer)
        {
            case MosaicLayer m:
                RenderMosaic(result, imgW, imgH, m);
                break;
            case MosaicBrushLayer mb:
                RenderMosaicBrush(result, imgW, imgH, mb);
                break;
            case RectangleLayer rl:
                RenderRectOutline(result, imgW, imgH, rl.GetBounds(), rl.Style);
                break;
            case EllipseLayer el:
                RenderEllipseOutline(result, imgW, imgH, el.GetBounds(), el.Style);
                break;
            case ArrowLayer al:
                RenderArrow(result, imgW, imgH, al, al.Style);
                break;
            case FreehandLayer fl:
                RenderPolyline(result, imgW, imgH, fl.Points, fl.Style);
                break;
            case TextLayer tl:
                RenderText(result, imgW, imgH, tl);
                break;
            default:
                // 未知图层类型：安全回退为边框（不丢标注）
                RenderRectOutline(result, imgW, imgH, layer.GetBounds(),
                    layer is RectangleLayer r2 ? r2.Style : new BrushStyle());
                break;
        }
    }

    // ══════════════════════════════════════════════
    //  各形状光栅化
    // ══════════════════════════════════════════════

    private static void RenderRectOutline(byte[] px, int w, int h, RectF bounds, BrushStyle style)
    {
        if (!style.IsStrokeEnabled) return;
        byte r = ToByte(style.StrokeColor.R), g = ToByte(style.StrokeColor.G), b = ToByte(style.StrokeColor.B);
        int t = Math.Max(1, (int)Math.Round(style.StrokeWidth));

        int lx = (int)MathF.Floor(bounds.Left), ly = (int)MathF.Floor(bounds.Top);
        int rx = (int)MathF.Ceiling(bounds.Right) - 1, ry = (int)MathF.Ceiling(bounds.Bottom) - 1;
        if (lx >= w || ly >= h || rx < 0 || ry < 0) return;
        lx = Math.Clamp(lx, 0, w - 1); ly = Math.Clamp(ly, 0, h - 1);
        rx = Math.Clamp(rx, 0, w - 1); ry = Math.Clamp(ry, 0, h - 1);

        // 上/下边
        for (int y = ly; y <= Math.Min(ly + t - 1, ry); y++)
            for (int x = lx; x <= rx; x++) SetPx(px, w, x, y, r, g, b);
        for (int y = Math.Max(ly, ry - t + 1); y <= ry; y++)
            for (int x = lx; x <= rx; x++) SetPx(px, w, x, y, r, g, b);
        // 左/右边（跳过上下已画区域避免重复写）
        for (int x = lx; x <= Math.Min(lx + t - 1, rx); x++)
            for (int y = ly + t; y <= ry - t; y++) SetPx(px, w, x, y, r, g, b);
        for (int x = Math.Max(lx, rx - t + 1); x <= rx; x++)
            for (int y = ly + t; y <= ry - t; y++) SetPx(px, w, x, y, r, g, b);
    }

    private static void RenderEllipseOutline(byte[] px, int w, int h, RectF bounds, BrushStyle style)
    {
        if (!style.IsStrokeEnabled) return;
        byte r = ToByte(style.StrokeColor.R), g = ToByte(style.StrokeColor.G), b = ToByte(style.StrokeColor.B);
        float cx = (bounds.Left + bounds.Right) / 2f, cy = (bounds.Top + bounds.Bottom) / 2f;
        float rx = Math.Max(0.5f, bounds.Width / 2f), ry = Math.Max(0.5f, bounds.Height / 2f);
        int t = Math.Max(1, (int)Math.Round(style.StrokeWidth));

        int steps = (int)Math.Clamp((rx + ry) * 2f, 16, 4096);
        var prev = new Vector2(cx + rx, cy);
        for (int i = 1; i <= steps; i++)
        {
            float a = MathF.Tau * i / steps;
            var cur = new Vector2(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
            DrawLine(px, w, h, prev.X, prev.Y, cur.X, cur.Y, r, g, b, t);
            prev = cur;
        }
    }

    private static void RenderArrow(byte[] px, int w, int h, ArrowLayer al, BrushStyle style)
    {
        if (!style.IsStrokeEnabled) return;
        byte r = ToByte(style.StrokeColor.R), g = ToByte(style.StrokeColor.G), b = ToByte(style.StrokeColor.B);
        int t = Math.Max(1, (int)Math.Round(style.StrokeWidth));
        float head = Math.Max(6f, al.ArrowHeadSize);

        DrawLine(px, w, h, al.StartX, al.StartY, al.EndX, al.EndY, r, g, b, t);

        float dx = al.EndX - al.StartX, dy = al.EndY - al.StartY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return;
        float ux = dx / len, uy = dy / len;
        // 箭头头部两条线，与主线夹角 ±30°
        const float ang = MathF.PI / 6f;
        float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
        var h1 = new Vector2(al.EndX - head * (ux * ca - uy * sa), al.EndY - head * (ux * sa + uy * ca));
        var h2 = new Vector2(al.EndX - head * (ux * ca + uy * sa), al.EndY - head * (-ux * sa + uy * ca));
        DrawLine(px, w, h, al.EndX, al.EndY, h1.X, h1.Y, r, g, b, t);
        DrawLine(px, w, h, al.EndX, al.EndY, h2.X, h2.Y, r, g, b, t);
    }

    private static void RenderPolyline(byte[] px, int w, int h, IReadOnlyList<Vector2> pts, BrushStyle style)
    {
        if (!style.IsStrokeEnabled || pts.Count < 2) return;
        byte r = ToByte(style.StrokeColor.R), g = ToByte(style.StrokeColor.G), b = ToByte(style.StrokeColor.B);
        int t = Math.Max(1, (int)Math.Round(style.StrokeWidth));
        for (int i = 1; i < pts.Count; i++)
            DrawLine(px, w, h, pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y, r, g, b, t);
    }

    // ══════════════════════════════════════════════
    //  马赛克块均值的光照空间
    // ══════════════════════════════════════════════
    // 预览（OverlayComposite.hlsl 的 MosaicBlockMean）在 scRGB 线性基上取均值，输出这条 CPU 路拿到的
    // 是 BGRA8 sRGB 字节。直接在编码值上求均值会把块压暗：黑白棋盘格在编码值上均值 127，
    // 线性光均值却该编码成 188 —— 预览亮、文件暗，同一张图两副样子。
    // 所以两处求均值都改成"逐样本解到线性 → 求均值 → 编码回一次"。
    private static readonly float[] s_srgbToLinear = BuildSrgbToLinear();

    private static float[] BuildSrgbToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    private static byte LinearToSrgbByte(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255f), 0, 255);
    }

    private static void RenderMosaic(byte[] px, int w, int h, MosaicLayer m)
    {        int lx = Math.Max(0, (int)MathF.Floor(m.X)), ly = Math.Max(0, (int)MathF.Floor(m.Y));
        int rx = Math.Min(w - 1, (int)MathF.Ceiling(m.X + m.Width) - 1);
        int ry = Math.Min(h - 1, (int)MathF.Ceiling(m.Y + m.Height) - 1);
        if (lx >= w || ly >= h || rx < 0 || ry < 0) return;
        int block = Math.Max(2, (int)MathF.Round(m.BlockSize));

        // ═══ 2026-09-15 修复：并行粒度错误导致数据竞争 ═══
        // 原实现 Parallel.For(ly, ry+1, ...) —— 并行单位是【每个 y】，
        // 而 blockY = y/block*block 会让连续 block 个 y 映射到【同一个块行】，
        // 于是多个线程并发处理同一批像素：每个线程先"读整块求均值"再"写整块"，
        // 读写是两个独立循环，一个线程完全可能读到另一线程【部分写入】的块
        // ⇒ 求出的均值是脏数据，且结果随线程调度变化、不可复现。
        // 原注释"块间互不重叠，按行并行安全"是错的：块【行】在 y 方向是重叠的。
        // 现改为按【块行】并行：每个块行恰好由一个线程处理，块行之间互不重叠。
        int firstBlockY = ly / block * block;
        int blockRows = (ry - firstBlockY) / block + 1;
        Parallel.For(0, blockRows, k =>
        {
            int blockY = firstBlockY + k * block;
            // x 也必须从整块边界起：原来从图层左边 lx 起，x/y 两个方向的网格原点不一致，
            // 与预览（按图像原点取块）永远错位半格
            for (int x = lx / block * block; x <= rx; x += block)
            {
                int bx2 = Math.Min(x + block - 1, rx);
                int by2 = Math.Min(blockY + block - 1, ry);
                float r2 = 0, g2 = 0, b2 = 0;
                int cnt = 0;
                for (int yy = blockY; yy <= by2; yy++)
                    for (int xx = x; xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        b2 += s_srgbToLinear[px[idx]]; g2 += s_srgbToLinear[px[idx + 1]]; r2 += s_srgbToLinear[px[idx + 2]];
                        cnt++;
                    }
                if (cnt == 0) continue;
                // ⚠ 逐通道求均值，不能把三个通道合成一个灰值：预览（着色器）一直是彩色块平均，
                // 输出却落成灰度块 —— 同一张图两副样子（2026-10-28 实测保存的 PNG 里涂抹带 86%
                // 的像素 r=g=b）。马赛克是"看不清"，不是"去色"。
                byte bAvg = LinearToSrgbByte(b2 / cnt), gAvg = LinearToSrgbByte(g2 / cnt), rAvg = LinearToSrgbByte(r2 / cnt);
                // 求均值必须整块（与预览同一网格），写入只落在图层矩形内：首块列/块行可能起在
                // lx/ly 左边，照搬整块写会把马赛克涂出矩形外最多 block-1 像素（预览不外溢）
                int wx0 = Math.Max(x, lx), wy0 = Math.Max(blockY, ly);
                for (int yy = wy0; yy <= by2; yy++)
                    for (int xx = wx0; xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        px[idx] = bAvg; px[idx + 1] = gAvg; px[idx + 2] = rAvg;
                    }
            }
        });
    }

    /// <summary>马赛克（涂抹）：只把"块中心落在笔迹半径内"的方块打码，与 shader 的 type 7 同一判据。</summary>
    private static void RenderMosaicBrush(byte[] px, int w, int h, MosaicBrushLayer m)
    {
        if (m.Points.Count == 0) return;
        var b = m.GetBounds();
        int lx = Math.Max(0, (int)MathF.Floor(b.Left)), ly = Math.Max(0, (int)MathF.Floor(b.Top));
        int rx = Math.Min(w - 1, (int)MathF.Ceiling(b.Right) - 1);
        int ry = Math.Min(h - 1, (int)MathF.Ceiling(b.Bottom) - 1);
        if (lx > rx || ly > ry) return;
        int block = Math.Max(2, (int)MathF.Round(m.BlockSize));
        float rad = Math.Max(1f, m.BrushWidth / 2f);

        int firstBlockY = ly / block * block;
        for (int blockY = firstBlockY; blockY <= ry; blockY += block)
        {
            for (int x = lx / block * block; x <= rx; x += block)
            {
                // 块中心到折线的最近距离：> 半径就整块跳过（不产生"擦边"的块）
                float cx = x + block / 2f, cy = blockY + block / 2f;
                float best = float.MaxValue;
                for (int i = 1; i < m.Points.Count; i++)
                    best = Math.Min(best, DistToSegment(cx, cy, m.Points[i - 1], m.Points[i]));
                if (m.Points.Count == 1) best = Vector2.Distance(new Vector2(cx, cy), m.Points[0]);
                if (best > rad) continue;

                int bx2 = Math.Min(x + block - 1, w - 1), by2 = Math.Min(blockY + block - 1, h - 1);
                float r2 = 0, g2 = 0, b2 = 0;
                int cnt = 0;
                for (int yy = Math.Max(0, blockY); yy <= by2; yy++)
                    for (int xx = Math.Max(0, x); xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        b2 += s_srgbToLinear[px[idx]]; g2 += s_srgbToLinear[px[idx + 1]]; r2 += s_srgbToLinear[px[idx + 2]];
                        cnt++;
                    }
                if (cnt == 0) continue;
                byte bAvg = LinearToSrgbByte(b2 / cnt), gAvg = LinearToSrgbByte(g2 / cnt), rAvg = LinearToSrgbByte(r2 / cnt);
                for (int yy = Math.Max(0, blockY); yy <= by2; yy++)
                    for (int xx = Math.Max(0, x); xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        px[idx] = bAvg; px[idx + 1] = gAvg; px[idx + 2] = rAvg;
                    }
            }
        }
    }

    private static float DistToSegment(float px, float py, Vector2 a, Vector2 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        float t = len2 > 1e-6f ? Math.Clamp(((px - a.X) * abx + (py - a.Y) * aby) / len2, 0f, 1f) : 0f;
        float dx = px - (a.X + abx * t), dy = py - (a.Y + aby * t);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static void RenderText(byte[] px, int w, int h, TextLayer tl)    {
        if (RasterizeText(tl.Text, tl.FontSize, tl.FontFamily, tl.Bold, tl.Italic, tl.MaxWidth)
            is not var (bgra, bw, bh)) return;

        float pad = Math.Max(2f, tl.Padding);
        int bgW = bw + (int)(pad * 2), bgH = bh + (int)(pad * 2);
        int bgX = (int)tl.X, bgY = (int)tl.Y;

        // 半透明背景
        var bg = tl.BackgroundColor;
        byte bgR = ToByte(bg.R), bgG = ToByte(bg.G), bgB = ToByte(bg.B);
        float bgA = Math.Clamp(bg.A, 0f, 1f);
        for (int y = Math.Max(0, bgY); y < Math.Min(h, bgY + bgH); y++)
            for (int x = Math.Max(0, bgX); x < Math.Min(w, bgX + bgW); x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(px[i] * (1 - bgA) + bgB * bgA);
                px[i + 1] = (byte)(px[i + 1] * (1 - bgA) + bgG * bgA);
                px[i + 2] = (byte)(px[i + 2] * (1 - bgA) + bgR * bgA);
            }

        // 前景文字（alpha 混合）
        BlendBitmap(px, w, h, bgra, bw, bh,
            bgX + (int)pad, bgY + (int)pad,
            ToByte(tl.TextColor.R), ToByte(tl.TextColor.G), ToByte(tl.TextColor.B));
    }

    // ══════════════════════════════════════════════
    //  基础图元
    // ══════════════════════════════════════════════

    /// <summary>粗细线（圆头端帽，数值稳定按长度步进）。</summary>
    private static void DrawLine(byte[] px, int w, int h, float x1, float y1, float x2, float y2,
        byte r, byte g, byte b, int thickness)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f)
        {
            PlotDot(px, w, h, x1, y1, r, g, b, thickness / 2f);
            return;
        }
        int steps = Math.Max(1, (int)MathF.Ceiling(len));
        float rad = thickness / 2f;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            PlotDot(px, w, h, x1 + dx * t, y1 + dy * t, r, g, b, rad);
        }
    }

    /// <summary>圆形笔触点。</summary>
    private static void PlotDot(byte[] px, int w, int h, float cx, float cy, byte r, byte g, byte b, float rad)
    {
        int x0 = (int)MathF.Floor(cx - rad), x1 = (int)MathF.Ceiling(cx + rad);
        int y0 = (int)MathF.Floor(cy - rad), y1 = (int)MathF.Ceiling(cy + rad);
        float rad2 = rad * rad;
        for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
            {
                float ddx = x + 0.5f - cx, ddy = y + 0.5f - cy;
                if (ddx * ddx + ddy * ddy <= rad2)
                    SetPx(px, w, x, y, r, g, b);
            }
    }

    /// <summary>把 Win2D 光栅化文字位图（BGRA8, alpha=覆盖率）混合到目标（着色）。</summary>
    public static void BlendBitmap(byte[] dst, int imgW, int imgH,
        byte[] bmp, int bmpW, int bmpH, int dstX, int dstY, byte r, byte g, byte b)
    {
        for (int row = 0; row < bmpH; row++)
        {
            int dy = dstY + row;
            if (dy < 0 || dy >= imgH) continue;
            for (int col = 0; col < bmpW; col++)
            {
                int dx = dstX + col;
                if (dx < 0 || dx >= imgW) continue;
                int si = (row * bmpW + col) * 4;
                byte alpha = bmp[si + 3];
                if (alpha < 8) continue;
                float a = alpha / 255f;
                int di = (dy * imgW + dx) * 4;
                dst[di] = (byte)(dst[di] * (1 - a) + b * a);
                dst[di + 1] = (byte)(dst[di + 1] * (1 - a) + g * a);
                dst[di + 2] = (byte)(dst[di + 2] * (1 - a) + r * a);
            }
        }
    }

    // ══════════════════════════════════════════════
    //  文字位图（Win2D, 缓存）
    // ══════════════════════════════════════════════

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Text, float Size, string Family, bool Bold, bool Italic, int MaxW),
        (byte[] Bgra, int W, int H)> s_textCache = new();

    /// <summary>文字位图缓存条数上限（超限整张清空，见 <see cref="RasterizeText"/> 里那句判据）。</summary>
    private const int TextCacheCap = 256;

    /// <summary>进程级共享的 Win2D 设备（懒加载，永不释放）。</summary>
    /// <remarks>
    /// ═══ 2026-09-15 修复：共享设备不能被 Dispose ═══
    /// <para><c>CanvasDevice.GetSharedDevice()</c> 返回的是【进程级共享】设备，
    /// 其生命周期由 Win2D 自行管理 —— 所有权不属于调用方。原实现写成
    /// <c>using var device = GetSharedDevice()</c>，在方法结束时释放了这个不属于自己的对象；
    /// 后续调用便可能命中已释放的设备（异常被上层 catch 吞掉，
    /// 表现为"文字图层只剩背景框、没有字"）。</para>
    /// 采用懒加载而非静态字段直接初始化：保持与原先一致的"首次用到才创建"时机，
    /// 且创建失败时由调用方的 try/catch 处理，不会升级成 TypeInitializationException。
    /// </remarks>
    private static Microsoft.Graphics.Canvas.CanvasDevice? s_sharedDevice;
    private static readonly object s_sharedDeviceLock = new();
    internal static Microsoft.Graphics.Canvas.CanvasDevice SharedCanvasDevice
    {
        get
        {
            lock (s_sharedDeviceLock)
                return s_sharedDevice ??= Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        }
    }

    /// <summary>
    /// 用 Win2D 光栅化文字为 BGRA8 位图（白字, alpha=覆盖率, 合成时着色）。线程安全（共享设备）。
    /// <para>尺寸由 <c>CanvasTextLayout.LayoutBounds</c> 实测（不是字数×字号估算），预览图集与最终输出
    /// 都调这一处 ⇒ 两边的字号/折行/字宽是同一张位图。<paramref name="maxWidth"/> &gt; 0 时按该宽度折行。</para>
    /// </summary>
    public static (byte[] Bgra, int W, int H)? RasterizeText(string text, float fontSize, string fontFamily,
        bool bold = false, bool italic = false, float maxWidth = 0f)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var key = (text, fontSize, fontFamily, bold, italic, (int)MathF.Round(maxWidth));
        if (s_textCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var device = SharedCanvasDevice;   // 共享设备：不 Dispose（见属性说明）
            var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                WordWrapping = maxWidth > 1f
                    ? Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.Wrap
                    : Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Top,
            };
            // 100000 = 不折行时的"足够宽"，LayoutBounds 仍会给出文本真实宽度
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(
                device, text, format, maxWidth > 1f ? maxWidth : 100000f, 100000f);
            var lb = layout.LayoutBounds;
            // +2：左右/上下各留 1px，给抗锯齿边缘与斜体伸出留位（预览与输出同加，不引入偏差）
            int w = Math.Max(8, (int)Math.Ceiling(lb.Width) + 2);
            int h = Math.Max(8, (int)Math.Ceiling(lb.Height) + 2);
            using var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96);
            using var ds = rt.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.DrawTextLayout(layout, 1, 1, Microsoft.UI.Colors.White);
            ds.Flush();
            var result = (rt.GetPixelBytes(), w, h);
            // ═══ 2026-10-30 修复：满了整张清空，而不是"从此不再写" ═══
            // 旧写法是 if (Count < 256) 才写 ⇒ 一旦到 256 条，这一句就永远不成立：既不逐出、
            // 也不入库，缓存只剩 :397 那次 TryGetValue 恒 miss ⇒ 之后每一次文字光栅化都重画一遍。
            // "防无界增长"因此变成了"防有缓存"。清空的代价是下一轮重画一次工作集（个位数条目），
            // 换来的是上限仍然成立。
            if (s_textCache.Count >= TextCacheCap) s_textCache.Clear();
            s_textCache[key] = result;
            return result;
        }
        catch
        {
            return null; // Win2D 不可用时文字图层仅背景框（不崩溃）
        }
    }

    /// <summary>把实测位图尺寸写回图层（图集/命中/合成三处都靠它算外框）。</summary>
    public static bool ApplyMetrics(TextLayer tl)
    {
        if (RasterizeText(tl.Text, tl.FontSize, tl.FontFamily, tl.Bold, tl.Italic, tl.MaxWidth) is not var (_, w, h))
            return false;
        tl.CachedWidth = w;
        tl.CachedHeight = h;
        return true;
    }

    // ══════════════════════════════════════════════
    //  工具
    // ══════════════════════════════════════════════

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f), 0, 255);

    private static void SetPx(byte[] px, int w, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * w + x) * 4;
        px[i] = b; px[i + 1] = g; px[i + 2] = r;
    }
}
