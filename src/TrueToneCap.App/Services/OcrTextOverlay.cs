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
    /// ⚠ 2026-11-09: 8px 对高分辨率屏仍太小，用户体验差；提至 12px 以平衡"适应多尺寸框"与"可读性"。
    private const float MinFontPx = 12f;

    /// <summary>测量结果缓存：预览每次重绘、导出每行都要问一次字号，而同一行的答案不会变。</summary>
    private static readonly Dictionary<(string Text, string Family, int MaxW, int MaxH), float> s_fitCache = new();

    /// <summary>
    /// 在一个 <paramref name="maxW"/>×<paramref name="maxH"/> 的框里为这段文字选字号。
    /// <paramref name="startPx"/> 是【原字号估计】（调用方用 <see cref="EstimateOriginalFontPx"/> 从原文反推），
    /// 语义是下限：放得下就取能放下的最大字号（≤ 下限），放不下也退回下限 ——
    /// 溢出交给折行，绝不缩过原字号。
    /// </summary>
    public static float FitFontSize(string text, string fontFamily, int maxW, int maxH, float startPx)
    {
        if (maxW <= 0 || maxH <= 0 || string.IsNullOrWhiteSpace(text)) return MinFontPx;

        var key = (text, fontFamily, maxW, maxH);
        lock (s_fitCache)
            if (s_fitCache.TryGetValue(key, out float cached)) return cached;

        // ═══ 2026-11-11（实机排障）：锚从"框高×0.65"换成"原字号估计" ═══
        // 旧口径 startPx = 0.65×框高的问题：det 检测框高度在截图上系统性缩水
        // （实测 40px 字的框高 16px、20px 字的框高 8px ≈ 真实字高的 0.6 倍），
        // 叠乘 0.65 后覆盖字恒为原字的 4~6 成 —— 这就是"OCR 后的字非常小"的实锚。
        // 新口径：startPx = EstimateOriginalFontPx(原文, 框W, 框H)，floor = startPx，
        // 覆盖字 ≥ 原字（用户口径"至少应该不比原有的字体小"）。
        float referenceSize = Math.Max(MinFontPx, startPx);

        // 先试参考字号本身（放得下就用它），放不下再按 0.9 递减找能放下的；
        // 但结果一律不低于参考字号 —— 溢出优于缩字。
        float size = Math.Clamp(referenceSize, MinFontPx, Math.Max(MinFontPx, maxH));
        float result = MinFontPx;

        while (size >= MinFontPx)
        {
            if (MeasureHeight(text, fontFamily, size, maxW) <= maxH) { result = size; break; }
            size *= 0.9f;
        }

        // 确保结果不低于参考字号（如果文本太短能放大）或不小于最小限制
        result = Math.Max(result, referenceSize);

        lock (s_fitCache)
        {
            if (s_fitCache.Count > 512) s_fitCache.Clear();   // 防无界增长
            s_fitCache[key] = result;
        }
        return result;
    }

    /// <summary>按框宽折行后的实测高度（像素）。Win2D 不可用时退回"字数×字号"的粗估。</summary>
    private static float MeasureHeight(string text, string fontFamily, float fontSize, int maxW)    {
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
    /// 从【原文文本 + 检测框】反推原字号（图像像素）—— 覆盖字的下限锚。
    /// </summary>
    /// <remarks>
    /// 为什么不用框高：det 检测框（DBNet 系）在截图上会把字高削到真实值的 ~0.6 倍
    /// （2026-11-11 实测：40px 字框高 16px、20px 字框高 8px），以框高为锚覆盖字必然偏小。
    /// 宽度锚稳得多：识别框的宽与文本排版宽同源，框内宽 ÷ 逐字符宽度单位 ≈ 原字号 em。
    /// 实测两行基准（40px/20px Consolas Bold）误差都在 ±10% 内，且方向是"宁大勿小"。
    /// </remarks>
    public static float EstimateOriginalFontPx(string originalText, int rectW, int rectH)
    {
        if (string.IsNullOrWhiteSpace(originalText) || rectW <= PadPx * 2) return MinFontPx;
        double units = TextWidthUnits(originalText);
        if (units <= 0) return MinFontPx;
        float emByWidth = (float)((rectW - PadPx * 2) / units);
        // 荒谬值防护：em 不会超过框高的 ~2.6 倍（det 框再缩水也包着字）
        float cap = rectH * 2.6f;
        return Math.Clamp(emByWidth, MinFontPx, Math.Max(MinFontPx, cap));
    }

    /// <summary>文本排版宽度（em 单位合计）：CJK/全角 1.0，其余 0.55，空白 0.5。</summary>
    private static double TextWidthUnits(string text)
    {
        double units = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { units += 0.5; continue; }
            units += IsFullWidthChar(ch) ? 1.0 : 0.55;
        }
        return units;
    }

    /// <summary>
    /// 把 det 框扩到能罩住整段原字墨迹 —— 覆盖块垫底的先决条件。
    /// </summary>
    /// <remarks>
    /// 2026-11-11 实机第二轮：det 框高 ≈ 0.6×真实字高 ⇒ 原字从块上下探出，与覆盖字
    /// 形成"重影"（暗底浅字内容尤其不可读）。按原字号估计值把块竖向扩到 1.45×em
    /// （单行高），横向扩到排版宽 + 行内留白，中心不动。imgH&gt;0 时按图高收底。
    /// </remarks>
    public static (int X, int Y, int Width, int Height) ExpandRectToInk(
        string originalText, (int X, int Y, int Width, int Height) rect, int imgH = 0)
    {
        float refPx = EstimateOriginalFontPx(originalText, rect.Width, rect.Height);
        // ×1.10 宽向余量：锚定字号与真实排版的 ±2% 差（加粗/微调/AA）不能把字挤进折行
        int estH = (int)Math.Ceiling(refPx * 1.45f);
        int estW = (int)Math.Ceiling(TextWidthUnits(originalText) * refPx * 1.10) + PadPx * 2;
        int nw = Math.Max(rect.Width, estW);
        int nh = Math.Max(rect.Height, estH);
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        int nx = Math.Max(0, cx - nw / 2);
        int ny = Math.Max(0, cy - nh / 2);
        if (imgH > 0 && ny + nh > imgH) ny = Math.Max(0, imgH - nh);
        return (nx, ny, nw, nh);
    }

    /// <summary>
    /// 块底中位色（BGRA 数组，坐标越界自动收拢）。字迹占比 &lt; 50% 时中位数即背景色
    /// —— 拿它当覆盖块的垫底色，块与周边图像融为一体，原字被干净盖掉。
    /// </summary>
    public static (byte B, byte G, byte R) MedianBlockColor(
        byte[] px, int w, int h, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
        var bs = new List<byte>(); var gs = new List<byte>(); var rs = new List<byte>();
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                int i = (y * w + x) * 4;
                bs.Add(px[i]); gs.Add(px[i + 1]); rs.Add(px[i + 2]);
            }
        if (bs.Count == 0) return (0, 0, 0);
        bs.Sort(); gs.Sort(); rs.Sort();
        int m = bs.Count / 2;
        return (bs[m], gs[m], rs[m]);
    }

    /// <summary>不透明矩形填充（BGRA，越界自动收拢）。Compose 垫底用。</summary>
    private static void FillRect(byte[] px, int w, int h,
        (int X, int Y, int Width, int Height) r, byte b, byte g, byte r8)
    {
        int x1 = Math.Min(w, r.X + r.Width), y1 = Math.Min(h, r.Y + r.Height);
        for (int y = Math.Max(0, r.Y); y < y1; y++)
            for (int x = Math.Max(0, r.X); x < x1; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r8; px[i + 3] = 255;
            }
    }

    /// <summary>CJK/全角判定（宽度单位 1.0em；其余按 0.55em 计）。</summary>
    private static bool IsFullWidthChar(char c) => c >= 0x1100 && (
        c <= 0x115F ||                                    // Hangul Jamo
        c == 0x2329 || c == 0x232A ||
        (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||    // CJK 部首～彝文
        (c >= 0xAC00 && c <= 0xD7A3) ||                   // Hangul 音节
        (c >= 0xF900 && c <= 0xFAFF) ||                   // CJK 兼容表意
        (c >= 0xFE30 && c <= 0xFE4F) ||
        (c >= 0xFF00 && c <= 0xFF60) ||                   // 全角形式
        (c >= 0xFFE0 && c <= 0xFFE6));

    /// <summary>
    /// 墨迹实测包围盒：在给定窗口内扫与块底色亮度差 ≥60 的像素。阈值法对
    /// 深字浅底 / 浅字深底都成立；找不到墨迹（均匀色块）时原样返回 <paramref name="scan"/>。
    /// </summary>
    /// <remarks>2026-11-11 实机第三轮：det 框横向也缩水（~0.92×）且宽度单位是估计值，
    /// 垫底块按估计值画会短于真实墨迹 ⇒ 原字从块边露出细边。墨迹以像素实测为准，
    /// 不再依赖任何字体度量常数。</remarks>
    public static (int X, int Y, int Width, int Height) MeasureInkRect(
        byte[] px, int w, int h, (int X, int Y, int Width, int Height) scan)
    {
        int sx0 = Math.Max(0, scan.X), sy0 = Math.Max(0, scan.Y);
        int sx1 = Math.Min(w, scan.X + scan.Width), sy1 = Math.Min(h, scan.Y + scan.Height);
        if (sx1 <= sx0 || sy1 <= sy0) return scan;
        var bg = MedianBlockColor(px, w, h, sx0, sy0, sx1, sy1);
        int bgLuma = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = sy0; y < sy1; y++)
            for (int x = sx0; x < sx1; x++)
            {
                int i = (y * w + x) * 4;
                int luma = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000;
                if (Math.Abs(luma - bgLuma) >= 60)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        if (maxX < minX) return scan;
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>两矩形并集。</summary>
    public static (int X, int Y, int Width, int Height) UnionRect(
        (int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width);
        int y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// 把译文逐行画到 <paramref name="bgra"/> 的副本上并返回（不修改入参：调用方可能还要再存原图）。
    /// <para>2026-11-11 实机第二轮：先垫块底再画字。原先"只画文字、不画底块"时，译文与
    /// 原字墨迹叠成重影（导出图与预览同病）；现在每行先用块底中位色垫不透明底
    /// （ExpandRectToInk 罩住墨迹），再在其上画字 —— 与预览的块渲染同口径。</para>
    /// <para>必须跑在后台线程：4K 图一次 clone 33MB，Win2D 光栅化也不是零成本。</para>
    /// <para><paramref name="originalTexts"/> 是与 boxes 同序的原文文本，供字号锚反推原字号
    /// （2026-11-11 起覆盖字以原字号为下限；为 null 时退回框高×0.65 的旧锚）。</para>
    /// </summary>
    public static byte[] Compose(byte[] bgra, int w, int h,
        IReadOnlyList<(int X, int Y, int Width, int Height)> boxes,
        IReadOnlyList<string> texts, string fontFamily, IReadOnlyList<string>? originalTexts = null)
    {
        var dst = (byte[])bgra.Clone();
        int count = Math.Min(boxes.Count, texts.Count);
        for (int i = 0; i < count; i++)
        {
            string original = originalTexts is not null && i < originalTexts.Count
                ? originalTexts[i] : texts[i];
            if (string.IsNullOrWhiteSpace(original)) continue;

            // det 框高缩水 ⇒ 先把块扩到罩住墨迹，垫底才有意义；再用像素实测墨迹包围盒
            // 校正（det 横向也缩水，估计值会短几个像素 ⇒ 原字从块边露细边）。
            var cover = ExpandRectToInk(original, boxes[i], imgH: h);
            var ink = MeasureInkRect(bgra, w, h,
                (cover.X - 10, cover.Y, cover.Width + 20, cover.Height));
            ink = (Math.Max(0, ink.X - PadPx), Math.Max(0, ink.Y - PadPx), ink.Width + PadPx * 2, ink.Height + PadPx * 2);
            cover = UnionRect(cover, ink);
            if (cover.Width <= 8 || cover.Height <= 8) continue;
            int maxW = cover.Width - PadPx * 2;
            int maxH = cover.Height - PadPx * 2;

            float referencePx = EstimateOriginalFontPx(original, boxes[i].Width, boxes[i].Height);
            float size = FitFontSize(texts[i], fontFamily, maxW, maxH, referencePx);
            if (AnnotationRasterizer.RasterizeText(texts[i], size, fontFamily, maxWidth: maxW)
                is not { } glyphs) continue;
            var (glyphPixels, gw, gh) = glyphs;

            // ═══ 2026-11-11（实机第三轮）：按真实字形尺寸再扩块 —— 不裁字 ═══
            // 用户真实字体字幅可能比宽度单位估计宽，RasterizeText 返回的 gw/gh 是实测排版尺寸；
            // 块取两者较大值并以墨迹块中心保持居中（与预览的 XAML Measure 同口径）。
            int finW = Math.Max(cover.Width, gw + PadPx * 2);
            int finH = Math.Max(cover.Height, gh + PadPx * 2);
            if (finW != cover.Width || finH != cover.Height)
            {
                int cx2 = cover.X + cover.Width / 2, cy2 = cover.Y + cover.Height / 2;
                int nx = Math.Max(0, cx2 - finW / 2);
                int ny = Math.Max(0, cy2 - finH / 2);
                if (ny + finH > h) ny = Math.Max(0, h - finH);
                cover = (nx, ny, finW, finH);
            }

            // 垫底：块底中位色（不透明），文字颜色按垫底色取对比 —— 深底白字/浅底近黑。
            // 顺序纪律：先定 final cover → 采样 → 垫底 → 画字；垫底若在扩块前画，
            // 扩出来的条带没有垫底，字就叠在原字上（重影回流）。
            var bg = MedianBlockColor(dst, w, h, cover.X, cover.Y, cover.X + cover.Width, cover.Y + cover.Height);
            FillRect(dst, w, h, cover, bg.B, bg.G, bg.R);
            int luma = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;
            var (r, g, b) = luma >= 136 ? ((byte)20, (byte)20, (byte)20) : ((byte)240, (byte)240, (byte)240);

            // 与预览一致：水平左对齐（行内留白），垂直居中；高于框就贴顶，别把字顶出下一行
            int dx = cover.X + PadPx;
            int dy = cover.Y + Math.Max(0, (cover.Height - gh) / 2);
            AnnotationRasterizer.BlendBitmap(dst, w, h, glyphPixels, gw, gh, dx, dy, r, g, b);
        }
        return dst;
    }
}
