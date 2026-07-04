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

    /// <summary>自动预处理：检测图像特征并选择最佳管线。</summary>
    public static PreprocessResult AutoPreprocess(byte[] bgra, int w, int h)
    {
        // 规则1：图片太小 → 放大
        if (w < 400 || h < 200)
            return ScaleUp(bgra, w, h, 2.0f);

        // 规则2：检测对比度 → 是否需要增强
        float contrast = MeasureContrast(bgra, w, h);
        if (contrast < 40) // 低对比度
        {
            var enhanced = EnhanceContrast(bgra, w, h);
            // 对比度增强后可能仍需二值化
            float enhancedContrast = MeasureContrast(enhanced, w, h);
            if (enhancedContrast < 60)
                return AdaptiveThreshold(enhanced, w, h);
            return new PreprocessResult(enhanced, w, h, PreprocessMode.EnhanceContrast);
        }

        // 高对比度 → 不预处理
        return new PreprocessResult(bgra, w, h, PreprocessMode.None);
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
