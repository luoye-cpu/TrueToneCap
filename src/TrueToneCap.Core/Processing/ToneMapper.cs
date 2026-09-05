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
    /// 分段色调映射核心（GainMap 与所有格式的 HDR→SDR 路径共用同款曲线）。
    /// 输入 y = scRGB / sdrWhiteScrgb（PaperWhite 归一化后，SDR 白点 = 1.0），即该像素的最大通道值。
    /// 输出为 SDR 显示值 [0, 1]，调用方再按比例缩放其余通道以保持色相。
    /// </summary>
    /// <param name="y">归一化亮度（最大通道值），SDR 白点 = 1.0。</param>
    /// <param name="headroom">HDR 峰值 / SDR 白点（nits 比值）。当前实现不参与运算，仅为兼容既有调用方保留。</param>
    /// <param name="eps">兼容保留参数。</param>
    /// <returns>SDR 显示值，范围 [0, 1]。</returns>
    /// <remarks>
    /// ═══ 2026-08-30 修复：消除 SDR 白点处的亮度断崖 ═══
    /// <para>
    /// <b>旧实现的缺陷</b>：y ≥ 1+eps 分支直接套用 libultrahdr 的
    /// <c>ReinhardMap(y,h) = (1 + y/h²)/(1 + y) · y</c>，其 <c>R(1) = (1 + 1/h²)/2</c>。
    /// 以典型参数 headroom=5 计，<c>R(1) = 0.52</c>，而左分支（y ≤ 1 直通）返回 <b>1.0</b>。
    /// 过渡区的 smoothstep 只是在 1.0 与 R(y) 之间插值，而 R 在 y ∈ [1, 1.25] 仍只有 0.52~0.58，
    /// 于是 f 在 y=1 处由 1.0 骤降后再缓慢回升 —— <b>函数非单调</b>。
    /// </para>
    /// <para>
    /// <b>视觉后果</b>：亮度刚超过 SDR 白点的像素，输出反而比 SDR 白更暗（降幅达 42%，
    /// 且 headroom 越大越深）。在高光边缘形成一圈"暗环"伪影，天空、灯光、反光场景尤其明显。
    /// </para>
    /// <para>
    /// <b>新实现的数学依据</b>：8-bit SDR 的输出上界为 1.0。在
    /// "SDR 白点保真（f(1)=1.0）" + "输出不超过上界（f ≤ 1）" + "单调不减"
    /// 三重约束下，<c>f(y&gt;1) ≡ 1.0</c>（饱和）是<b>唯一解</b>。
    /// 超出 SDR 白点的能量改由 GainMap 的增益图承载（<c>gain = log2(y / f(y)) = log2(y)</c>），
    /// HDR 查看器据其完整还原；纯 SDR 输出则体现为高光削平，属 SDR 显示 HDR 的固有限制。
    /// </para>
    /// <para>
    /// 该行为同时保证：① SDR 内容（y ≤ 1）100% 保真，SDR 与 HDR 查看器观感一致；
    /// ② GainMap 的 SDR 区域增益恒为 0（符合 ISO 21496-1）；③ 全程单调不减，无暗环。
    /// </para>
    /// </remarks>
    public static float SegmentedReinhardMap(float y, float headroom, float eps = 0.25f)
    {
        // SDR 范围（含白点）：恒等映射 → SDR 内容完全保真，GainMap 增益为 0
        if (y <= 1.0f) return Math.Clamp(y, 0f, 1f);

        // 高光：单调饱和到 SDR 上界 1.0。
        // 超出部分由 GainMap 增益图还原；纯 SDR 输出则为高光削平。
        // （headroom / eps 不参与运算 —— 详见 XML 备注中的数学推导）
        _ = headroom; _ = eps;
        return 1.0f;
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
