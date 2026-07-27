// TrueToneCap.Core/Processing/ToneMapper.cs
// CPU 色调映射算法库 — Reinhard / Hable / ACES + sRGB gamma + 融合内核
// GPU 路径见 GpuToneMapper.cs（HLSL + D3D11）

using System.Threading.Tasks;

namespace TrueToneCap.Core.Processing;

/// <summary>色调映射算法。</summary>
public enum ToneMapMode
{
    Reinhard = 0,
    Hable = 1,
    Aces = 2
}

/// <summary>色调映射参数。</summary>
public record struct ToneMappingParams(
    ToneMapMode Mode = ToneMapMode.Hable,
    float Exposure = 0.0f,
    float PaperWhiteNits = 80f,
    float DisplayMaxNits = 1000f
);

/// <summary>CPU 色调映射算法集 — 将 HDR scRGB 转换为 SDR sRGB。</summary>
public static class ToneMapper
{

    // ────────────── CPU 回退路径（无需 GPU 编译时使用） ──────────────

    /// <summary>在 CPU 上执行 Reinhard 色调映射（回退路径）。</summary>
    public static void ReinhardToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f, float paperWhite = 80f)
    {
        float evScale = MathF.Pow(2.0f, exposure);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            // R, G, B 乘以曝光
            float r = hdrPixels[i] * evScale;
            float g = hdrPixels[i + 1] * evScale;
            float b = hdrPixels[i + 2] * evScale;

            // Reinhard 全局算子
            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float mappedLum = luminance / (1.0f + luminance);

            if (luminance > 0.0001f)
            {
                float scale = mappedLum / luminance;
                hdrPixels[i] = Math.Clamp(r * scale, 0f, 1f);
                hdrPixels[i + 1] = Math.Clamp(g * scale, 0f, 1f);
                hdrPixels[i + 2] = Math.Clamp(b * scale, 0f, 1f);
            }
            else
            {
                hdrPixels[i] = hdrPixels[i + 1] = hdrPixels[i + 2] = 0f;
            }
            // Alpha 保留
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    /// <summary>在 CPU 上执行 Hable (Filmic) 色调映射。</summary>
    public static void HableToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f, float whitePoint = 11.2f)
    {
        float evScale = MathF.Pow(2.0f, exposure);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            float r = hdrPixels[i] * evScale;
            float g = hdrPixels[i + 1] * evScale;
            float b = hdrPixels[i + 2] * evScale;

            hdrPixels[i] = HableCurve(r, whitePoint);
            hdrPixels[i + 1] = HableCurve(g, whitePoint);
            hdrPixels[i + 2] = HableCurve(b, whitePoint);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    /// <summary>ACES 近似色调映射（CPU）。</summary>
    public static void AcesToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f)
    {
        float evScale = MathF.Pow(2.0f, exposure);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            float r = hdrPixels[i] * evScale;
            float g = hdrPixels[i + 1] * evScale;
            float b = hdrPixels[i + 2] * evScale;

            // ACES 拟合 (Narkowicz 2015)
            hdrPixels[i] = AcesCurve(r);
            hdrPixels[i + 1] = AcesCurve(g);
            hdrPixels[i + 2] = AcesCurve(b);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    private static float HableCurve(float x, float whitePoint)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f;
        const float D = 0.20f, E = 0.02f, F = 0.30f;
        float numerator = x * (A * x + C * B) + D * E;
        float denominator = x * (A * x + B) + D * F;
        float result = (numerator / denominator) - (E / F);

        float whiteScale = 1.0f / HableCurveWhite(whitePoint);
        return Math.Clamp(result * whiteScale, 0f, 1f);
    }

    private static float HableCurveWhite(float x) =>
        ((x * (0.15f * x + 0.10f * 0.50f) + 0.20f * 0.02f) /
         (x * (0.15f * x + 0.50f) + 0.20f * 0.30f)) - (0.02f / 0.30f);

    private static float AcesCurve(float x)
    {
        float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Math.Clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0f, 1f);
    }

    /// <summary>通用色调映射入口（根据模式选择算法）。</summary>
    public static void ApplyToneMapping(Span<float> hdrPixels, int width, int height,
        ToneMappingParams p)
    {
        switch (p.Mode)
        {
            case ToneMapMode.Reinhard:
                ReinhardToneMapCpu(hdrPixels, width, height, p.Exposure, p.PaperWhiteNits);
                break;
            case ToneMapMode.Hable:
                HableToneMapCpu(hdrPixels, width, height, p.Exposure);
                break;
            case ToneMapMode.Aces:
                AcesToneMapCpu(hdrPixels, width, height, p.Exposure);
                break;
        }
    }

    // ────────────── sRGB 编码（线性 → gamma） ──────────────

    /// <summary>线性 RGB → sRGB gamma 编码。</summary>
    public static void LinearToSRgb(Span<float> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            float c = pixels[i];
            pixels[i] = c <= 0.0031308f
                ? 12.92f * c
                : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
        }
    }

    /// <summary>将 float HDR 像素转换为 byte BGRA sRGB 像素（与 D3D11 BGRA8 兼容）。
    /// P1 优化: 融合内核 — 直接从源数组读取，逐像素完成色调映射 + gamma + swizzle，
    /// 避免 132MB (4K) 中间 float 数组拷贝。</summary>
    public static byte[] FloatToSRgbBytes(float[] hdrPixels, int width, int height,
        ToneMappingParams toneParams)
    {
        var bytes = new byte[width * height * 4];
        int pixelCount = width * height;
        float evScale = MathF.Pow(2.0f, toneParams.Exposure);

        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = hdrPixels[i] * evScale;
            float g = hdrPixels[i + 1] * evScale;
            float b = hdrPixels[i + 2] * evScale;
            float a = hdrPixels[i + 3];

            // 色调映射（融合，无需中间数组）
            switch (toneParams.Mode)
            {
                case ToneMapMode.Reinhard:
                {
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    float mappedLum = lum / (1.0f + lum);
                    if (lum > 0.0001f)
                    {
                        float scale = mappedLum / lum;
                        r = Math.Clamp(r * scale, 0f, 1f);
                        g = Math.Clamp(g * scale, 0f, 1f);
                        b = Math.Clamp(b * scale, 0f, 1f);
                    }
                    else { r = g = b = 0f; }
                    break;
                }
                case ToneMapMode.Hable:
                    r = HableCurve(r, 11.2f);
                    g = HableCurve(g, 11.2f);
                    b = HableCurve(b, 11.2f);
                    break;
                case ToneMapMode.Aces:
                    r = AcesCurve(r);
                    g = AcesCurve(g);
                    b = AcesCurve(b);
                    break;
            }

            // sRGB gamma 编码（融合）
            r = LinearToSRgbScalar(r);
            g = LinearToSRgbScalar(g);
            b = LinearToSRgbScalar(b);
            a = Math.Clamp(a, 0f, 1f);

            // Swizzle: Float RGBA → Byte BGRA (D3D11 BGRA8 格式)
            bytes[i]     = (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);
            bytes[i + 1] = (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255);
            bytes[i + 2] = (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255);
            bytes[i + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
        });
        return bytes;
    }

    /// <summary>标量 sRGB gamma（用于融合内核内部）。</summary>
    private static float LinearToSRgbScalar(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
    }
}
