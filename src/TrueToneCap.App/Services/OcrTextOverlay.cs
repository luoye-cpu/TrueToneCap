// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/OcrTextOverlay.cs
// 译文覆盖合成：把翻译后的文字按 OCR 行矩形画到截图像素上（只画文字、不加底块）。
//
// 与预览共用同一套排版：预览的文字块（OcrPreviewWindow.RenderOverlay）与这里的输出
// 都调 FitFontSize，字号/折行只在一处决定 —— 否则"预览里放得下、导出被裁掉"必然会再犯。

using Microsoft.Graphics.Canvas.Text;

namespace TrueToneCap.App.Services;

internal static class OcrTextOverlay
{
    /// <summary>行内左右留白（图像像素）。与 OcrPreviewWindow.LineRect 的 4px padding 同值。</summary>
    private const int PadPx = 4;

    /// <summary>自适应字号的下限：再小就不成字了，宁可让它贴边裁掉一点。</summary>
    private const float MinFontPx = 8f;

    /// <summary>测量结果缓存：预览每次重绘、导出每行都要问一次字号，而同一行的答案不会变。</summary>
    private static readonly Dictionary<(string Text, string Family, int MaxW, int MaxH), float> s_fitCache = new();

    /// <summary>
    /// 在一个 <paramref name="maxW"/>×<paramref name="maxH"/> 的框里为这段文字选最大可用字号：
    /// 从 <paramref name="startPx"/> 起按 0.9 递减，直到按框宽折行后的实测高度放得下。
    /// <para>为什么从 <c>框高×0.65</c> 起：这是预览沿用至今的视觉比例（字高约占行高 2/3）。
    /// 译文普遍比原文长，照搬这个字号就会出框 —— 原实现用省略号截断，现在改成先折行、再缩字号。</para>
    /// </summary>
    public static float FitFontSize(string text, string fontFamily, int maxW, int maxH, float startPx)
    {
        if (maxW <= 0 || maxH <= 0 || string.IsNullOrWhiteSpace(text)) return MinFontPx;

        var key = (text, fontFamily, maxW, maxH);
        lock (s_fitCache)
            if (s_fitCache.TryGetValue(key, out float cached)) return cached;

        // 起始字号不超过框高：单行文字再大也放不进比自己还矮的框
        float size = Math.Clamp(startPx, MinFontPx, Math.Max(MinFontPx, maxH));
        float result = MinFontPx;
        while (size >= MinFontPx)
        {
            if (MeasureHeight(text, fontFamily, size, maxW) <= maxH) { result = size; break; }
            size *= 0.9f;
        }

        lock (s_fitCache)
        {
            if (s_fitCache.Count > 512) s_fitCache.Clear();   // 防无界增长
            s_fitCache[key] = result;
        }
        return result;
    }

    /// <summary>按框宽折行后的实测高度（像素）。Win2D 不可用时退回"字数×字号"的粗估。</summary>
    private static float MeasureHeight(string text, string fontFamily, float fontSize, int maxW)
    {
        try
        {
            var format = new CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontSize = fontSize,
                WordWrapping = CanvasWordWrapping.Wrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top,
            };
            using var layout = new CanvasTextLayout(
                AnnotationRasterizer.SharedCanvasDevice, text, format, maxW, float.MaxValue);
            return (float)layout.LayoutBounds.Height;
        }
        catch
        {
            // 每字符约 0.6 个字号的宽度估算 —— 只影响退路下的字号选择，不影响是否崩溃
            int perLine = Math.Max(1, (int)(maxW / (fontSize * 0.6f)));
            int lines = Math.Max(1, (text.Length + perLine - 1) / perLine);
            return lines * fontSize * 1.2f;
        }
    }

    /// <summary>
    /// 把译文逐行画到 <paramref name="bgra"/> 的副本上并返回（不修改入参：调用方可能还要再存原图）。
    /// <para>只画文字，不画底块。为了让文字在任何背景上都读得出来，颜色按"文字底下那块的平均亮度"
    /// 逐行选：深底用白字，浅底用近黑 —— 这是不加底块时唯一不靠猜的办法。</para>
    /// <para>必须跑在后台线程：4K 图一次 clone 33MB，Win2D 光栅化也不是零成本。</para>
    /// </summary>
    public static byte[] Compose(byte[] bgra, int w, int h,
        IReadOnlyList<(int X, int Y, int Width, int Height)> boxes,
        IReadOnlyList<string> texts, string fontFamily)
    {
        var dst = (byte[])bgra.Clone();
        int count = Math.Min(boxes.Count, texts.Count);
        for (int i = 0; i < count; i++)
        {
            var (bx, by, bw, bh) = boxes[i];
            string text = texts[i];
            if (string.IsNullOrWhiteSpace(text) || bw <= 8 || bh <= 8) continue;

            int maxW = bw - PadPx * 2;
            int maxH = bh - PadPx * 2;
            float size = FitFontSize(text, fontFamily, maxW, maxH, bh * 0.65f);
            if (AnnotationRasterizer.RasterizeText(text, size, fontFamily, maxWidth: maxW)
                is not { } glyphs) continue;
            var (glyphPixels, gw, gh) = glyphs;

            // 与预览一致：水平左对齐（行内留白），垂直居中；高于框就贴顶，别把字顶出下一行
            int dx = bx + PadPx;
            int dy = by + Math.Max(0, (bh - gh) / 2);
            var (r, g, b) = ContrastColor(dst, w, h, glyphPixels, gw, gh, dx, dy);
            AnnotationRasterizer.BlendBitmap(dst, w, h, glyphPixels, gw, gh, dx, dy, r, g, b);
        }
        return dst;
    }

    /// <summary>按字迹覆盖到的像素算底下那块的平均亮度，选对比色：深底白字 / 浅底近黑。</summary>
    private static (byte R, byte G, byte B) ContrastColor(byte[] px, int w, int h,
        byte[] glyphs, int gw, int gh, int dx, int dy)
    {
        long sum = 0, n = 0;
        for (int row = 0; row < gh; row++)
        {
            int y = dy + row;
            if (y < 0 || y >= h) continue;
            for (int col = 0; col < gw; col++)
            {
                int x = dx + col;
                if (x < 0 || x >= w) continue;
                int si = (row * gw + col) * 4;
                if (glyphs[si + 3] < 16) continue;          // 只有字迹覆盖处参与取样
                int di = (y * w + x) * 4;
                // BGRA，按 Rec.601 亮度
                sum += (px[di + 2] * 299 + px[di + 1] * 587 + px[di] * 114) / 1000;
                n++;
            }
        }
        long luma = n > 0 ? sum / n : 0;
        return luma < 128 ? ((byte)250, (byte)250, (byte)250) : ((byte)20, (byte)20, (byte)20);
    }
}
