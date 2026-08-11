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

    private static void RenderMosaic(byte[] px, int w, int h, MosaicLayer m)
    {
        int lx = Math.Max(0, (int)MathF.Floor(m.X)), ly = Math.Max(0, (int)MathF.Floor(m.Y));
        int rx = Math.Min(w - 1, (int)MathF.Ceiling(m.X + m.Width) - 1);
        int ry = Math.Min(h - 1, (int)MathF.Ceiling(m.Y + m.Height) - 1);
        if (lx >= w || ly >= h || rx < 0 || ry < 0) return;
        int block = Math.Max(2, (int)MathF.Round(m.BlockSize));

        // 块间互不重叠，按行并行安全
        Parallel.For(ly, ry + 1, y =>
        {
            int blockY = y / block * block;
            for (int x = lx; x <= rx; x += block)
            {
                int bx2 = Math.Min(x + block - 1, rx);
                int by2 = Math.Min(blockY + block - 1, ry);
                int r2 = 0, g2 = 0, b2 = 0, cnt = 0;
                for (int yy = blockY; yy <= by2; yy++)
                    for (int xx = x; xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        b2 += px[idx]; g2 += px[idx + 1]; r2 += px[idx + 2]; cnt++;
                    }
                if (cnt == 0) continue;
                byte av = (byte)((r2 + g2 + b2) / (cnt * 3));
                for (int yy = blockY; yy <= by2; yy++)
                    for (int xx = x; xx <= bx2; xx++)
                    {
                        int idx = (yy * w + xx) * 4;
                        px[idx] = px[idx + 1] = px[idx + 2] = av;
                    }
            }
        });
    }

    private static void RenderText(byte[] px, int w, int h, TextLayer tl)
    {
        var bmp = RasterizeText(tl.Text, tl.FontSize, tl.FontFamily);
        if (bmp is not var (bgra, bw, bh)) return;

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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, float Size, string Family), (byte[] Bgra, int W, int H)> s_textCache = new();

    /// <summary>用 Win2D 光栅化文字为 BGRA8 位图（白字, alpha=覆盖率, 合成时着色）。线程安全（共享设备）。</summary>
    public static (byte[] Bgra, int W, int H)? RasterizeText(string text, float fontSize, string fontFamily)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var key = (text, fontSize, fontFamily);
        if (s_textCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            using var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            // 尺寸估算：中文全角字符 ≈ 1em 宽，行高 ≈ 1.6em
            int w = Math.Max(8, (int)MathF.Ceiling(text.Length * fontSize * 1.05f));
            int h = Math.Max(8, (int)MathF.Ceiling(fontSize * 1.7f));
            using var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96);
            using var ds = rt.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.DrawText(text, 0, 0, w, h, Microsoft.UI.Colors.White,
                new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
                    VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Top,
                });
            ds.Flush();
            var result = (rt.GetPixelBytes(), w, h);
            if (s_textCache.Count < 256) // 防无界增长
                s_textCache[key] = result;
            return result;
        }
        catch
        {
            return null; // Win2D 不可用时文字图层仅背景框（不崩溃）
        }
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
