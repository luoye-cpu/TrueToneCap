// TrueToneCap.Core/Processing/ToneMapper.cs
// CPU 色调映射算法库 — Reinhard / Hable / SegmentedReinhard (GainMap 同款)
// 色彩语义:
//   - 输入: scRGB 线性 (BT.709 原色 + 线性 gamma, 1.0 = 80 nits, Windows HDR 合成空间)
//   - 输出: sRGB (BT.709 原色 + sRGB gamma) — 图片/截图标准
//   - 注意: sRGB 与 BT.709 共享相同 primaries, 区别仅在 transfer;
//     图片输出用 sRGB (CICP transfer code 13), 非纯 BT.709 (视频 OETF code 1)
// 管线:
//   1. PaperWhite 亮度归一化 (scRGB×80/PaperWhiteNits)
//   2. 色调映射曲线 (SegmentedReinhard 亮度缩放保持色相)
//   3. sRGB gamma 编码
// ACES 已移除 (2026-08-08): 生产恒用分段 Reinhard, 无激活路径。可行性见 docs/architecture-design.md
// GPU 路径见 GpuToneMapper.cs (HLSL + D3D11)

using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace TrueToneCap.Core.Processing;

/// <summary>色调映射算法。</summary>
public enum ToneMapMode
{
    Reinhard = 0,
    Hable = 1,
    /// <summary>分段 Reinhard (GainMap 同款): y≤SDR白点直通, smoothstep 过渡, 高光 Reinhard 压缩。</summary>
    SegmentedReinhard = 2
}

/// <summary>色调映射参数。</summary>
public record struct ToneMappingParams(
    ToneMapMode Mode = ToneMapMode.SegmentedReinhard,
    float Exposure = 0.0f,
    float PaperWhiteNits = 200f,
    float DisplayMaxNits = 1000f
);

/// <summary>CPU 色调映射算法集 — 将 HDR scRGB 转换为 SDR sRGB。</summary>
public static class ToneMapper
{
    // ═══════════════════════════════════════════════════════
    //  linear sRGB → 8-bit 查找表 (4096 项 + 线性插值)
    //  替代 MathF.Pow(c, 1/2.4) 标量调用 — 最热路径 (FloatToSRgbBytes/LinearToBgra8)
    //  精度: 4096 段插值 < 0.1 LSB (8-bit 输出), 视觉无损
    //  SIMD/ISA 无关, 所有平台共享; 配合 JIT 向量化
    // ═══════════════════════════════════════════════════════

    // 将 linear [0,1] 均匀划分为 LUT_SIZE 段, 每段内线性插值
    private const int SRGB_LUT_SIZE = 4096;
    private static readonly float[] s_srgbLinearLut = BuildSrgbLinearLut();

    private static float[] BuildSrgbLinearLut()
    {
        var lut = new float[SRGB_LUT_SIZE + 1];
        for (int i = 0; i <= SRGB_LUT_SIZE; i++)
        {
            float lin = i / (float)SRGB_LUT_SIZE;
            lut[i] = lin <= 0.0031308f
                ? 12.92f * lin
                : 1.055f * MathF.Pow(lin, 1.0f / 2.4f) - 0.055f;
        }
        return lut;
    }

    /// <summary>
    /// linear sRGB [0,1] → [0,1] gamma (查表 + 线性插值, 替代 MathF.Pow)。
    /// 查询表已预计算, 此函数仅做索引 + 插值, 无 Pow 调用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearToSrgbLut(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        float scaled = c * SRGB_LUT_SIZE;
        int idx = (int)scaled;
        if (idx >= SRGB_LUT_SIZE) return s_srgbLinearLut[SRGB_LUT_SIZE];
        float frac = scaled - idx;
        return s_srgbLinearLut[idx] + (s_srgbLinearLut[idx + 1] - s_srgbLinearLut[idx]) * frac;
    }

    /// <summary>linear sRGB → 8-bit byte (查表 + 插值 + 量化, 融合封装)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToSrgbByte(float c)
    {
        float gamma = LinearToSrgbLut(c);
        return (byte)(gamma * 255f + 0.5f);
    }

    // ────────────── 色调映射曲线 ──────────────

    /// <summary>
    /// Reinhard 全局色调映射算子。
    /// 在 scRGB 空间 (BT.709 原色 + 线性 gamma) 工作，亮度缩放保持色相。
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
    /// 在 scRGB 空间 (BT.709 原色 + 线性 gamma) 工作，逐通道曲线。
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
            case ToneMapMode.SegmentedReinhard:
                // 分段 Reinhard (GainMap 同款): PaperWhite 归一化后按 maxY 亮度缩放保持色相
                {
                    float pw = Math.Max(p.PaperWhiteNits, 1.0f);
                    float scale = MathF.Pow(2.0f, p.Exposure) * (80.0f / pw);
                    float headroom = Math.Max(p.DisplayMaxNits, 1.0f) / Math.Max(p.PaperWhiteNits, 1.0f);
                    for (int i = 0; i < hdrPixels.Length; i += 4)
                    {
                        float r = hdrPixels[i] * scale;
                        float g = hdrPixels[i + 1] * scale;
                        float b = hdrPixels[i + 2] * scale;
                        float maxY = Math.Max(Math.Max(r, g), b);
                        float maxSdr = SegmentedReinhardMap(maxY, headroom);
                        float s = maxY > 1e-6f ? maxSdr / maxY : 0f;
                        hdrPixels[i]     = Math.Clamp(r * s, 0f, 1f);
                        hdrPixels[i + 1] = Math.Clamp(g * s, 0f, 1f);
                        hdrPixels[i + 2] = Math.Clamp(b * s, 0f, 1f);
                        hdrPixels[i + 3] = Math.Clamp(hdrPixels[i + 3], 0f, 1f);
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// 分段 Reinhard 映射核心 (GainMap 与所有格式 HDR→SDR 共用同款曲线)。
    /// 输入: y = scRGB / sdrWhiteScrgb（SDR 白点 = 1.0, 即 PaperWhite 归一化后）。
    /// 分段 (修复过曝的关键):
    ///   - y ≤ 1.0 (SDR 内容): 完全直通 → 增益恒 1x
    ///   - 1.0 &lt; y &lt; 1+eps: smoothstep 混合直通与 Reinhard（单调无过冲, 消除 SDR 白点亮度跳变）
    ///   - y ≥ 1+eps: Reinhard 压缩 (libultrahdr ReinhardMap 公式)
    /// 公式: ReinhardMap(y, headroom) = (1 + y/headroom²) / (1 + y) × y
    ///       ReinhardMap(headroom) = 1.0 (HDR 峰值恰好映射 SDR 白点)
    /// 说明: 曾用 Hermite 插值追求 C1, 但端点斜率差过大 (直通 1.0 vs R'(1)≈0.26)
    ///       导致过渡区内部负斜率过冲 (非单调) — 已废弃, 改用 smoothstep 混合。
    /// </summary>
    public static float SegmentedReinhardMap(float y, float headroom, float eps = 0.25f)
    {
        if (y <= 1.0f) return Math.Clamp(y, 0f, 1f);
        float h = Math.Max(headroom, 1.0f);
        float h2 = h * h;
        // ReinhardMap(y, headroom) = (1 + y/headroom²) / (1 + y) × y
        float rY = (1.0f + y / h2) / (1.0f + y) * y;
        if (y < 1.0f + eps)
        {
            // smoothstep 混合: f(y) = (1-s)·1.0 + s·R(y), s = smoothstep((y-1)/eps)
            float t = (y - 1.0f) / eps;
            float s = t * t * (3.0f - 2.0f * t);
            return (1.0f - s) * 1.0f + s * rY;
        }
        return rY;
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
        // SegmentedReinhard 在目标色域线性空间按亮度缩放，使用目标色域权重
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
                case ToneMapMode.SegmentedReinhard:
                {
                    // 分段 Reinhard (GainMap 同款, 2026-08-08 统一):
                    // 输入已按 PaperWhite 归一化 (scale=80/pw), 即 y = scRGB/sdrWhiteScrgb
                    // (SDR 白点 = 1.0)。y≤1 直通, smoothstep 过渡, 高光 Reinhard 压缩,
                    // 亮度缩放保持色相。headroom = DisplayMaxNits/PaperWhiteNits (nits 比值)。
                    float maxY = Math.Max(Math.Max(r, g), b);
                    float headroom = Math.Max(toneParams.DisplayMaxNits, 1f)
                        / Math.Max(toneParams.PaperWhiteNits, 1f);
                    float maxSdr = SegmentedReinhardMap(maxY, headroom);
                    float s = maxY > 1e-6f ? maxSdr / maxY : 0f;
                    r = Math.Clamp(r * s, 0f, 1f);
                    g = Math.Clamp(g * s, 0f, 1f);
                    b = Math.Clamp(b * s, 0f, 1f);
                    break;
                }
            }

            // sRGB gamma 编码 + RGBA→BGRA swizzle + uint8 量化 (查表, 替代 MathF.Pow)
            bytes[i]     = LinearToSrgbByte(b);
            bytes[i + 1] = LinearToSrgbByte(g);
            bytes[i + 2] = LinearToSrgbByte(r);
            bytes[i + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
        });
        return bytes;
    }

    /// <summary>标量 sRGB gamma（用于融合内核外部）。已改用查表 (LinearToSrgbLut)。</summary>
    public static float LinearToSRgbScalarPub(float c) => LinearToSrgbLut(c);

    private static float LinearToSRgbScalar(float c) => LinearToSrgbLut(c);
}
