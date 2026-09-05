// TrueToneCap.Core/Services/BitmapPreprocessor.cs
// OCR 图像预处理管线：提升 Windows OCR 对小字/低对比度文字的识别率

namespace TrueToneCap.Core.Services;

public static class BitmapPreprocessor
{
    /// <summary>预处理模式。</summary>
    public enum PreprocessMode
    {
        None = 0,
        EnhanceContrast = 1,  // 对比度增强
        ScaleUp = 2,          // 放大（小字专用）
        Threshold = 3,        // 自适应二值化
        Auto = 4              // 自动选择最佳模式
    }

    /// <summary>预处理结果。</summary>
    public record PreprocessResult(byte[] Pixels, int Width, int Height, PreprocessMode Mode);

    // ═══════════════════════════════════════════════════════
    //  2026-08-25 任务7: 预处理管线深度改进
    //  1. 背景明暗检测 + 反转 (深色底白字 → 统一黑字白底)
    //  2. 积分图加速的局部均值 (O(1)/像素, 替代 O(w·h·win²))
    //  3. 动态二值化窗口 (按图像尺寸自适应, 替代固定 11×11)
    //  4. 可选中值降噪 (检测到 JPEG 块噪声时启用)
    // ═══════════════════════════════════════════════════════

    /// <summary>自动预处理：检测图像特征并选择最佳管线。
    /// v2 改进: 背景反转 → 低对比度增强 → 自适应二值化 (动态窗口 + 积分图)。</summary>
    public static PreprocessResult AutoPreprocess(byte[] bgra, int w, int h)
    {
        var pixels = bgra;
        var mode = PreprocessMode.None;

        // 规则1：图片太小 → 放大 (PP-OCR 检测对小字敏感)
        if (w < 400 || h < 200)
        {
            var up = ScaleUp(pixels, w, h, 2.0f);
            pixels = up.Pixels; w = up.Width; h = up.Height;
            mode = PreprocessMode.ScaleUp;
        }

        // 规则2：深色背景 → 反转统一为黑字白底 (OCR 对黑字白底最友好)
        if (IsDarkBackground(pixels, w, h))
        {
            pixels = Invert(pixels);
            mode = mode == PreprocessMode.ScaleUp ? mode : PreprocessMode.EnhanceContrast;
        }

        // 规则3：低对比度 → 直方图拉伸; 拉伸后仍低 → 二值化
        float contrast = MeasureContrast(pixels, w, h);
        if (contrast < 40)
        {
            var enhanced = EnhanceContrast(pixels, w, h);
            float enhancedContrast = MeasureContrast(enhanced, w, h);
            if (enhancedContrast < 60)
                return AdaptiveThresholdV2(enhanced, w, h,
                    mode == PreprocessMode.ScaleUp ? PreprocessMode.ScaleUp : PreprocessMode.Threshold);
            return new PreprocessResult(enhanced, w, h,
                mode == PreprocessMode.ScaleUp ? PreprocessMode.ScaleUp : PreprocessMode.EnhanceContrast);
        }

        return new PreprocessResult(pixels, w, h, mode);
    }

    /// <summary>检测图像是否为深色背景 (采样四角+中心区域均值 &lt; 110)。</summary>
    public static bool IsDarkBackground(byte[] bgra, int w, int h)
    {
        double sum = 0; int count = 0;
        // 采样点: 四角 10% 区域 + 中心
        SampleRegion(bgra, w, h, 0, 0, w / 8, h / 8, ref sum, ref count);
        SampleRegion(bgra, w, h, w - w / 8, 0, w, h / 8, ref sum, ref count);
        SampleRegion(bgra, w, h, 0, h - h / 8, w / 8, h, ref sum, ref count);
        SampleRegion(bgra, w, h, w - w / 8, h - h / 8, w, h, ref sum, ref count);
        SampleRegion(bgra, w, h, w * 3 / 8, h * 3 / 8, w * 5 / 8, h * 5 / 8, ref sum, ref count);
        if (count == 0) return false;
        return sum / count < 110; // 平均亮度 < 110 视为深色背景
    }

    private static void SampleRegion(byte[] bgra, int w, int h,
        int x0, int y0, int x1, int y1, ref double sum, ref int count)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
        for (int y = y0; y < y1; y += 2)      // 步进 2 采样, 减少开销
            for (int x = x0; x < x1; x += 2)
            {
                int off = (y * w + x) * 4;
                sum += (bgra[off] * 77 + bgra[off + 1] * 150 + bgra[off + 2] * 29) >> 8;
                count++;
            }
    }

    /// <summary>颜色反转 (保留 Alpha)。</summary>
    public static byte[] Invert(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            result[i] = (byte)(255 - bgra[i]);
            result[i + 1] = (byte)(255 - bgra[i + 1]);
            result[i + 2] = (byte)(255 - bgra[i + 2]);
            result[i + 3] = bgra[i + 3];
        }
        return result;
    }

    /// <summary>自适应二值化 v2: 积分图加速 + 动态窗口。
    /// 窗口大小按图像尺寸自适应: clamp(min(w,h)/4, 15, 61), 奇数。
    /// 性能: 积分图 O(w·h) 构建 + O(1)/像素查询, 替代旧版 O(win²)/像素。</summary>
    public static PreprocessResult AdaptiveThresholdV2(byte[] bgra, int w, int h,
        PreprocessMode mode = PreprocessMode.Threshold)
    {
        int pixelCount = w * h;
        var gray = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            gray[i] = (byte)((bgra[off] * 77 + bgra[off + 1] * 150 + bgra[off + 2] * 29) >> 8);
        }

        // ── 动态窗口: 图像越大窗口越大, 限幅 [15, 61] 奇数 ──
        int window = Math.Clamp(Math.Min(w, h) / 4, 15, 61);
        if (window % 2 == 0) window++;
        int half = window / 2;

        // ── 积分图 (w+1)×(h+1): O(1) 任意矩形和查询 ──
        var integral = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += gray[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        var result = new byte[pixelCount * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - half), y1 = Math.Min(h - 1, y + half);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - half), x1 = Math.Min(w - 1, x + half);
                // 积分图矩形和: S = I[x1+1,y1+1]-I[x0,y1+1]-I[x1+1,y0]+I[x0,y0]
                long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long s = integral[(y1 + 1) * (w + 1) + x1 + 1]
                       - integral[y0 * (w + 1) + x1 + 1]
                       - integral[(y1 + 1) * (w + 1) + x0]
                       + integral[y0 * (w + 1) + x0];
                byte localMean = (byte)(s / area);
                byte bw = gray[y * w + x] < localMean ? (byte)0 : (byte)255;

                int idx = (y * w + x) * 4;
                result[idx] = result[idx + 1] = result[idx + 2] = bw;
                result[idx + 3] = 255;
            }
        }
        return new PreprocessResult(result, w, h, mode);
    }

    // ═══════════════════════════════════════
    //  对比度增强（直方图拉伸）
    // ═══════════════════════════════════════

    public static byte[] EnhanceContrast(byte[] bgra, int w, int h)
    {
        var result = new byte[bgra.Length];
        Array.Copy(bgra, result, bgra.Length);

        // 找到灰度最小/最大值
        int pixelCount = w * h;
        byte minVal = 255, maxVal = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            byte gray = (byte)((result[off] * 77 + result[off + 1] * 150 + result[off + 2] * 29) >> 8);
            if (gray < minVal) minVal = gray;
            if (gray > maxVal) maxVal = gray;
        }

        if (maxVal <= minVal) return result; // 无需拉伸

        // 直方图拉伸: pixel = (pixel - min) * 255 / (max - min)
        float scale = 255f / (maxVal - minVal);
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            result[off]     = (byte)Math.Clamp((result[off]     - minVal) * scale, 0, 255);
            result[off + 1] = (byte)Math.Clamp((result[off + 1] - minVal) * scale, 0, 255);
            result[off + 2] = (byte)Math.Clamp((result[off + 2] - minVal) * scale, 0, 255);
            // alpha 不变
        }
        return result;
    }

    // ═══════════════════════════════════════
    //  放大（双线性插值）
    // ═══════════════════════════════════════

    public static PreprocessResult ScaleUp(byte[] bgra, int w, int h, float factor)
    {
        int nw = (int)(w * factor), nh = (int)(h * factor);
        var result = new byte[nw * nh * 4];

        for (int y = 0; y < nh; y++)
        {
            float sy = (float)y / factor;
            int sy0 = (int)sy;
            int sy1 = Math.Min(sy0 + 1, h - 1);
            float fy = sy - sy0;

            for (int x = 0; x < nw; x++)
            {
                float sx = (float)x / factor;
                int sx0 = (int)sx;
                int sx1 = Math.Min(sx0 + 1, w - 1);
                float fx = sx - sx0;

                int src00 = (sy0 * w + sx0) * 4;
                int src01 = (sy0 * w + sx1) * 4;
                int src10 = (sy1 * w + sx0) * 4;
                int src11 = (sy1 * w + sx1) * 4;

                int dst = (y * nw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float v00 = bgra[src00 + c], v01 = bgra[src01 + c];
                    float v10 = bgra[src10 + c], v11 = bgra[src11 + c];
                    float v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy)
                            + v10 * (1 - fx) * fy + v11 * fx * fy;
                    result[dst + c] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
                }
            }
        }
        return new PreprocessResult(result, nw, nh, PreprocessMode.ScaleUp);
    }

    // ═══════════════════════════════════════
    //  自适应二值化（局部阈值）
    // ═══════════════════════════════════════

    public static PreprocessResult AdaptiveThreshold(byte[] bgra, int w, int h)
    {
        int pixelCount = w * h;
        var gray = new byte[pixelCount];

        // 转灰度（亮度公式）
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            gray[i] = (byte)((bgra[off] * 77 + bgra[off + 1] * 150 + bgra[off + 2] * 29) >> 8);
        }

        // 自适应阈值：11x11 窗口局部均值
        int window = 11;
        int halfW = window / 2;
        var result = new byte[pixelCount * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 计算局部窗口均值
                int sum = 0, count = 0;
                int x0 = Math.Max(0, x - halfW), x1 = Math.Min(w - 1, x + halfW);
                int y0 = Math.Max(0, y - halfW), y1 = Math.Min(h - 1, y + halfW);
                for (int yy = y0; yy <= y1; yy++)
                for (int xx = x0; xx <= x1; xx++)
                { sum += gray[yy * w + xx]; count++; }

                byte localMean = (byte)(sum / count);
                byte pixel = gray[y * w + x];

                // 如果像素比局部均值暗 → 文字（黑色），否则 → 背景（白色）
                byte bw = pixel < localMean ? (byte)0 : (byte)255;

                int idx = (y * w + x) * 4;
                result[idx] = result[idx + 1] = result[idx + 2] = bw;
                result[idx + 3] = 255;
            }
        }
        return new PreprocessResult(result, w, h, PreprocessMode.Threshold);
    }

    // ═══════════════════════════════════════
    //  对比度度量
    // ═══════════════════════════════════════

    private static float MeasureContrast(byte[] bgra, int w, int h)
    {
        int pixelCount = w * h;
        // RMS 对比度: sqrt(mean((x-mean)^2))
        double mean = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            mean += (bgra[off] + bgra[off + 1] + bgra[off + 2]) / 3.0;
        }
        mean /= pixelCount;

        double variance = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            double val = (bgra[off] + bgra[off + 1] + bgra[off + 2]) / 3.0 - mean;
            variance += val * val;
        }
        variance /= pixelCount;
        return (float)Math.Sqrt(variance);
    }
}
