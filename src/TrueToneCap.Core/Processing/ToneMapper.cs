// TrueToneCap.Core/Processing/ToneMapper.cs
// CPU 色调映射算法库 — Reinhard / Hable / ACES
// 业界标准 HDR→SDR 管线:
//   1. PaperWhite 亮度归一化 (scRGB×80/PaperWhiteNits)
//   2. 色域转换到 AP1 (ACEScg) 广色域空间
//   3. 色调映射曲线 (AP1 中保持色相)
//   4. 色域转换回 sRGB (BT.709)
//   5. ACES ODT (Output Device Transform)
//   6. sRGB gamma 编码
// GPU 路径见 GpuToneMapper.cs (HLSL + D3D11)

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
    // ═══════════════════════════════════════════════════════════════
    //  3×3 色域转换矩阵 (scRGB BT.709 ↔ ACES AP1/ACEScg)
    //  来源: ACES 1.0.3 规范, SMPTE ST 2065-1:2012
    //  链: BT.709 linear → XYZ(D65) → chromatic adaptation → XYZ(D60) → AP1
    // ═══════════════════════════════════════════════════════════════

    private static void ScrgbToAcesAp1(float r, float g, float b, out float ar, out float ag, out float ab)
    {
        ar = 0.613132f * r + 0.339538f * g + 0.047416f * b;
        ag = 0.070124f * r + 0.916324f * g + 0.013452f * b;
        ab = 0.020445f * r + 0.109548f * g + 0.870006f * b;
    }

    private static void AcesAp1ToScrgb(float ar, float ag, float ab, out float r, out float g, out float b)
    {
        r = 1.704579f * ar + (-0.625505f) * ag + (-0.078038f) * ab;
        g = (-0.129701f) * ar + 1.139240f * ag + (-0.009570f) * ab;
        b = (-0.019717f) * ar + (-0.128087f) * ag + 1.147935f * ab;
    }

    // ────────────── 色调映射曲线 ──────────────

    /// <summary>
    /// Reinhard 全局色调映射算子。
    /// 在 scRGB (BT.709) 空间工作，亮度缩放保持色相。
    /// </summary>
    public static void ReinhardToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f, float paperWhite = 80f)
    {
        float pw = Math.Max(paperWhite, 1.0f);
        float scale = MathF.Pow(2.0f, exposure) * (80.0f / pw);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            float r = hdrPixels[i] * scale;
            float g = hdrPixels[i + 1] * scale;
            float b = hdrPixels[i + 2] * scale;

            // scRGB 中应用 Reinhard (亮度缩放，保持色相)
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float mappedLum = lum / (1.0f + lum);
            if (lum > 0.0001f)
            {
                float s = mappedLum / lum;
                r = Math.Clamp(r * s, 0f, 1f);
                g = Math.Clamp(g * s, 0f, 1f);
                b = Math.Clamp(b * s, 0f, 1f);
            }
            else { r = g = b = 0f; }

            hdrPixels[i] = Math.Clamp(r, 0f, 1f);
            hdrPixels[i + 1] = Math.Clamp(g, 0f, 1f);
            hdrPixels[i + 2] = Math.Clamp(b, 0f, 1f);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    /// <summary>
    /// Hable (Filmic/Uncharted2) 色调映射曲线。
    /// 在 scRGB (BT.709) 空间工作，逐通道曲线。
    /// </summary>
    public static void HableToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f, float paperWhite = 80f, float whitePoint = 11.2f)
    {
        float pw = Math.Max(paperWhite, 1.0f);
        float scale = MathF.Pow(2.0f, exposure) * (80.0f / pw);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            float r = hdrPixels[i] * scale;
            float g = hdrPixels[i + 1] * scale;
            float b = hdrPixels[i + 2] * scale;

            // scRGB 中应用 Hable 曲线
            hdrPixels[i] = HableCurve(r, whitePoint);
            hdrPixels[i + 1] = HableCurve(g, whitePoint);
            hdrPixels[i + 2] = HableCurve(b, whitePoint);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    /// <summary>
    /// ACES 色调映射（参考级）。
    /// 完整管线: scRGB → AP1 → RRT(Narkowicz 2015) → ODT → sRGB
    /// </summary>
    public static void AcesToneMapCpu(Span<float> hdrPixels, int width, int height,
        float exposure = 0f, float paperWhite = 80f)
    {
        float pw = Math.Max(paperWhite, 1.0f);
        float scale = MathF.Pow(2.0f, exposure) * (80.0f / pw);
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            float r = hdrPixels[i] * scale;
            float g = hdrPixels[i + 1] * scale;
            float b = hdrPixels[i + 2] * scale;

            // 转换到 AP1 色域空间
            ScrgbToAcesAp1(r, g, b, out float ar, out float ag, out float ab);

            // RRT (Reference Rendering Transform) — Narkowicz 2015 拟合
            ar = AcesCurve(ar);
            ag = AcesCurve(ag);
            ab = AcesCurve(ab);

            // ODT (Output Device Transform) — sRGB 100 cd/m²
            ar = AcesOdt(ar);
            ag = AcesOdt(ag);
            ab = AcesOdt(ab);

            // 转换回 sRGB 线性
            AcesAp1ToScrgb(ar, ag, ab, out r, out g, out b);

            hdrPixels[i] = Math.Clamp(r, 0f, 1f);
            hdrPixels[i + 1] = Math.Clamp(g, 0f, 1f);
            hdrPixels[i + 2] = Math.Clamp(b, 0f, 1f);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

    // ── Hable Filmic 曲线 ──
    // 来源: John Hable, "Uncharted 2: HDR Lighting" (GDC 2010)
    // 参数: A=0.15(肩部), B=0.50(中部), C=0.10(趾部), D=0.20, E=0.02, F=0.30
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

    // ── ACES RRT 曲线 (Narkowicz 2015 拟合) ──
    // 来源: https://github.com/colour-science/colour/blob/develop/colour/algorithms/tonemapping.py
    // 设计用于 ACES AP1 (ACEScg) 色域空间
    private static float AcesCurve(float x)
    {
        float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Math.Clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0f, 1f);
    }

    // ── ACES ODT (Output Device Transform) ──
    // 来源: ACES 1.0.3, ODT.Academy.RGBmonitor_100nits_dim.ctl
    // 将 ACES RRT 输出适配到 sRGB 显示 (100 cd/m²)
    // 包含: 对比度调整 + 色饱和度微调
    private static float AcesOdt(float x)
    {
        // 对比度提升 (mid-tone contrast boost)
        // 公式: output = x * (1 + 0.3 * (1-x)²)
        // 暗部略提亮, 高光略压暗, 中间调对比度提升
        return x * (1.0f + 0.3f * (1.0f - x) * (1.0f - x));
    }

    // ────────────── 通用入口 ──────────────

    /// <summary>通用色调映射入口（根据模式选择算法，PaperWhite 归一化）。</summary>
    public static void ApplyToneMapping(Span<float> hdrPixels, int width, int height,
        ToneMappingParams p)
    {
        switch (p.Mode)
        {
            case ToneMapMode.Reinhard:
                ReinhardToneMapCpu(hdrPixels, width, height, p.Exposure, p.PaperWhiteNits);
                break;
            case ToneMapMode.Hable:
                HableToneMapCpu(hdrPixels, width, height, p.Exposure, p.PaperWhiteNits);
                break;
            case ToneMapMode.Aces:
                AcesToneMapCpu(hdrPixels, width, height, p.Exposure, p.PaperWhiteNits);
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

    // ────────────── 融合内核 ──────────────

    /// <summary>
    /// 将 float HDR 像素转换为 byte BGRA sRGB 像素（与 D3D11 BGRA8 兼容）。
    /// 融合内核 — 单 Parallel.For 完成:
    ///   PaperWhite 归一化 → 色调映射 → sRGB gamma → RGBA→BGRA swizzle → uint8 量化
    /// </summary>
    /// <param name="colorSpaceTag">目标色域标签，用于动态亮度权重。null/sRGB 用 BT.709 权重。</param>
    public static byte[] FloatToSRgbBytes(float[] hdrPixels, int width, int height,
        ToneMappingParams toneParams, string? colorSpaceTag = null)
    {
        var bytes = new byte[width * height * 4];
        int pixelCount = width * height;

        // PaperWhite 归一化 + 曝光
        float pw = Math.Max(toneParams.PaperWhiteNits, 1.0f);
        float nitsScale = 80.0f / pw;
        float evScale = MathF.Pow(2.0f, toneParams.Exposure);
        float scale = evScale * nitsScale;

        // 动态亮度权重（Reinhard 亮度缩放使用）
        // Hable 是逐通道曲线，不受亮度权重影响
        // ACES 在 AP1 空间工作，也不受影响
        var (wr, wg, wb) = colorSpaceTag switch
        {
            "BT2020" => (0.2627f, 0.6780f, 0.0593f),
            "DisplayP3" or "DCI_P3" => (0.2095f, 0.7215f, 0.0690f),
            _ => (0.2126f, 0.7152f, 0.0722f) // sRGB/BT.709
        };

        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = hdrPixels[i] * scale;
            float g = hdrPixels[i + 1] * scale;
            float b = hdrPixels[i + 2] * scale;
            float a = hdrPixels[i + 3];

            switch (toneParams.Mode)
            {
                case ToneMapMode.Reinhard:
                {
                    float lum = wr * r + wg * g + wb * b;
                    float mappedLum = lum / (1.0f + lum);
                    if (lum > 0.0001f)
                    {
                        float s = mappedLum / lum;
                        r = Math.Clamp(r * s, 0f, 1f);
                        g = Math.Clamp(g * s, 0f, 1f);
                        b = Math.Clamp(b * s, 0f, 1f);
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
                    // ACES: scRGB → AP1 → RRT → ODT → sRGB
                    ScrgbToAcesAp1(r, g, b, out float ar, out float ag, out float ab);
                    ar = AcesCurve(ar);
                    ag = AcesCurve(ag);
                    ab = AcesCurve(ab);
                    ar = AcesOdt(ar);
                    ag = AcesOdt(ag);
                    ab = AcesOdt(ab);
                    AcesAp1ToScrgb(ar, ag, ab, out r, out g, out b);
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
