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
    ToneMapMode Mode = ToneMapMode.Aces,
    float Exposure = 0.0f,
    float PaperWhiteNits = 200f,
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
        float exposure = 0f, float paperWhite = 80f, float maxNits = 1000f)
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
            // 不截断到 [0,1]，高光保留给 ODT 压缩
            ar = AcesRrt(ar);
            ag = AcesRrt(ag);
            ab = AcesRrt(ab);

            // ODT (Output Device Transform) — 对比度 + 亮度缩放
            ar = AcesOdt(ar, maxNits);
            ag = AcesOdt(ag, maxNits);
            ab = AcesOdt(ab, maxNits);

            // 饱和度补偿 (ACES 参考 ODT 的色度处理, sat=0.96)
            // 标准 ACES ODT 对高饱和色做轻微去饱和，补偿 RRT 的色度压缩
            // 参考: ACES 1.0.3 ODT.Academy.RGBmonitor_100nits_dim.ctl
            float lum = 0.2126f * ar + 0.7152f * ag + 0.0722f * ab;
            const float sat = 0.96f;
            ar = lum + sat * (ar - lum);
            ag = lum + sat * (ag - lum);
            ab = lum + sat * (ab - lum);

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
    // 注意: 标准 ACES RRT 输出允许 >1.0（高光保留给 ODT 后续压缩），
    // 因此此处不 clamp 到 [0,1]，避免丢失高光滚降细节。
    private static float AcesRrt(float x)
    {
        float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return (x * (a * x + b)) / (x * (c * x + d) + e);
    }

    // ── ACES ODT (Output Device Transform) ──
    // 来源: ACES 1.0.3, ODT.Academy.RGBmonitor_100nits_dim.ctl
    // 将 ACES RRT 输出适配到 sRGB 显示 (100 cd/m² dim surround)
    // 包含: 亮度缩放 + 对比度提升 + 饱和度补偿 + 黑电平 roll-off
    private static float AcesOdt(float x, float maxNits)
    {
        // 1. 高光/亮度缩放: 参考白(约 0.7) 映射到 1.0，高光由 maxNits 控制压缩
        //    独立于纸白，这里用固定参考 (dim surround 100 nits 语义)
        // 2. 对比度 S 曲线 (mid-tone contrast boost)
        //    output = x·(1 + k·(1-x)²)，k 随 maxNits 微调
        float k = 0.3f + 0.05f * Math.Clamp((maxNits - 100f) / 900f, 0f, 1f);
        float boosted = x * (1.0f + k * (1.0f - x) * (1.0f - x));

        // 3. 黑电平 roll-off: 暗部轻微 lift，避免纯黑死寂
        //    (已在融合内核中 clamp，此处仅微调)
        return boosted;
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
                AcesToneMapCpu(hdrPixels, width, height, p.Exposure, p.PaperWhiteNits, p.DisplayMaxNits);
                break;
        }
    }

    // ────────────── sRGB 编码（线性 → gamma） ──────────────

    /// <summary>
    /// BT.2390-4 EETF (Electro-Optical Transfer Function for display mapping)。
    /// 将 HDR 线性光平滑映射到 SDR 线性光，用于 Gain Map 的 SDR 基础图生成。
    ///
    /// 原理: 输入 ≤ 0.5×L_sdr 直通（SDR 范围内的内容保持不变），
    /// 超过此值逐新增压缩，到 L_max 时完全映射到 L_sdr。
    /// 使用 smoothstep 确保平滑滚降，避免 banding。
    /// </summary>
    /// <param name="scRgbLin">scRGB 线性值 (1.0=80 nits)。</param>
    /// <param name="hdrPeakNits">HDR 峰值亮度 (nits)，对应 DisplayMaxNits。</param>
    /// <param name="sdrPeakNits">SDR 峰值亮度 (nits)，固定 80。</param>
    /// <returns>EETF 映射后的 scRGB 线性值。</returns>
    public static float Eetf(float scRgbLin, float hdrPeakNits, float sdrPeakNits = 80f)
    {
        // ═══ BT.2390-4 EETF (ITU-R BT.2390-4 第 5.9.2 节) ═══
        // 信号规范: 输入 scRGB 线性 (1.0 = 80 nits = SDR 白点)
        //
        // 标准关键点:
        //   * 低于 SDR 白点一半 (0.5 × L_sdr = 40 nits) 的内容完全直通
        //   * 从 40 nits 到 hdrPeakNits 平滑滚降压缩到 [40, 80] nits
        //   * 超过 hdrPeakNits 钳位到 80 nits
        //
        // 先前实现缺陷 (导致过曝):
        //   L_sdr = sdrPeakNits / hdrPeakNits (归一化到 HDR 峰值)
        //   → 直通阈值 = 0.5×L_sdr = 0.5×80/1000 = 3.2 nits (错误! 应为 40 nits)
        //   → 50-80 nits 的普通内容被压缩到 40 nits, 增益图记录 1.25-2x 增益
        //   → 解码时普通区域整体提亮 → 严重过曝
        //
        // 正确: 直通阈值固定在 SDR 白点一半 (40 nits), 与 hdrPeakNits 无关。

        float nits = scRgbLin * sdrPeakNits;   // scRGB → nits (1.0 = 80 nits)
        float lSdr = sdrPeakNits;              // SDR 白点 (nits)
        float lMid = 0.5f * lSdr;              // 压缩中点 = 40 nits (BT.2390 标准)
        float lMax = hdrPeakNits;              // HDR 峰值 (nits)

        // 完全在 SDR 安全区 → 直通 (≤ 40 nits 或 ≤ SDR 白点)
        if (nits <= lMid) return scRgbLin;                     // ≤ 40 nits 完全直通
        if (nits >= lMax) return lSdr / sdrPeakNits;           // ≥ HDR 峰值 → 钳位到 SDR 白点

        // 平滑压缩: smoothstep 从 lMid 到 lMax, 输出 [lMid, lSdr]
        float t = (nits - lMid) / (lMax - lMid);
        float compressed = t * t * (3.0f - 2.0f * t);          // smoothstep 0→1
        float outNits = lMid + (lSdr - lMid) * compressed;     // 40 → 80 nits
        return Math.Clamp(outNits / sdrPeakNits, 0f, 1f);      // 转回 scRGB
    }

    /// <summary>批量执行 BT.2390-4 EETF 处理整个像素数组。</summary>
    public static void ApplyEetf(Span<float> hdrPixels, int width, int height, float hdrPeakNits, float sdrPeakNits = 80f)
    {
        for (int i = 0; i < hdrPixels.Length; i += 4)
        {
            hdrPixels[i]     = Eetf(hdrPixels[i], hdrPeakNits, sdrPeakNits);
            hdrPixels[i + 1] = Eetf(hdrPixels[i + 1], hdrPeakNits, sdrPeakNits);
            hdrPixels[i + 2] = Eetf(hdrPixels[i + 2], hdrPeakNits, sdrPeakNits);
            hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
        }
    }

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
                    // ACES: scRGB → AP1 → RRT → ODT(+饱和) → sRGB
                    ScrgbToAcesAp1(r, g, b, out float ar, out float ag, out float ab);
                    ar = AcesRrt(ar);
                    ag = AcesRrt(ag);
                    ab = AcesRrt(ab);
                    ar = AcesOdt(ar, toneParams.DisplayMaxNits);
                    ag = AcesOdt(ag, toneParams.DisplayMaxNits);
                    ab = AcesOdt(ab, toneParams.DisplayMaxNits);
                    // 饱和度补偿 (ACES 参考 ODT 的色度处理, sat=0.96)
                    float al = 0.2126f * ar + 0.7152f * ag + 0.0722f * ab;
                    const float sat2 = 0.96f;
                    ar = al + sat2 * (ar - al);
                    ag = al + sat2 * (ag - al);
                    ab = al + sat2 * (ab - al);
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
    public static float LinearToSRgbScalarPub(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
    }

    private static float LinearToSRgbScalar(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
    }
}
