// shaders/ToneMapping.hlsl
// HDR -> SDR tone mapping shader (Reinhard / Hable / SegmentedReinhard)
// ACES 已移除 (2026-08-08): 生产恒用分段 Reinhard, 无激活路径。可行性见 docs/architecture-design.md
// Compile: dxc -T ps_6_0 -E main ToneMapping.hlsl -Fo ToneMapping.cso

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer ToneMappingParams : register(b0)
{
    uint  ToneMapMode;
    float Exposure;
    float PaperWhiteNits;
    float DisplayMaxNits;
}

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Reinhard tone mapping (scRGB space, hue-preserving luminance scaling)
float3 ReinhardToneMap(float3 hdr)
{
    float lum = dot(hdr, float3(0.2126f, 0.7152f, 0.0722f));
    float mappedLum = lum / (1.0f + lum);
    float scale = (lum > 0.0001f) ? (mappedLum / lum) : 0.0f;
    return saturate(hdr * scale);
}

// Hable (Uncharted 2) Filmic tone mapping (scRGB space)
float3 HableCurve(float3 x)
{
    const float A = 0.15f, B = 0.50f, C = 0.10f;
    const float D = 0.20f, E = 0.02f, F = 0.30f;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 HableToneMap(float3 hdr)
{
    float3 curr = HableCurve(hdr);
    float3 whiteScale = 1.0f / HableCurve(float3(11.2f, 11.2f, 11.2f));
    return curr * whiteScale;
}

// 分段 Reinhard (GainMap 同款, 2026-08-08 统一):
// y≤1 直通 (SDR 白点内保真) → smoothstep 过渡 (消除跳变) → Reinhard 压缩 (高光)
float SegmentedReinhardMap(float y, float headroom)
{
    if (y <= 1.0f) return saturate(y);
    float h2 = headroom * headroom;
    float rY = (1.0f + y / h2) / (1.0f + y) * y;   // ReinhardMap(y, headroom)
    const float eps = 0.25f;
    if (y < 1.0f + eps)
    {
        float t = (y - 1.0f) / eps;
        float s = t * t * (3.0f - 2.0f * t);        // smoothstep
        return (1.0f - s) + s * rY;                  // 混合直通与 Reinhard
    }
    return rY;
}

// 分段 Reinhard 色调映射 (亮度缩放保持色相, 与 CPU 融合内核一致)
float3 SegmentedReinhardToneMap(float3 hdr)
{
    float pw = max(PaperWhiteNits, 80.0f);
    float headroom = max(DisplayMaxNits, 1.0f) / pw;
    float maxY = max(hdr.r, max(hdr.g, hdr.b));
    float maxSdr = SegmentedReinhardMap(maxY, headroom);
    float scale = (maxY > 1e-6f) ? (maxSdr / maxY) : 0.0f;
    return saturate(hdr * scale);
}

// Linear -> sRGB gamma (with negative protection)
float3 LinearToSRGB(float3 c)
{
    float3 clamped = max(c, 0.0f);
    float3 low = 12.92f * clamped;
    float3 high = 1.055f * pow(clamped, 1.0f / 2.4f) - 0.055f;
    return select(clamped <= 0.0031308f, low, high);
}

PSOutput main(PSInput input)
{
    float4 hdrColor = InputTexture.Sample(LinearSampler, input.uv);
    float pw = max(PaperWhiteNits, 1.0f);
    float nitsScale = 80.0f / pw;
    float3 lin = hdrColor.rgb * exp2(Exposure) * nitsScale;

    float3 mapped;
    if (ToneMapMode == 0)
        mapped = ReinhardToneMap(lin);
    else if (ToneMapMode == 2)
        mapped = SegmentedReinhardToneMap(lin);   // 分段 Reinhard (GainMap 同款)
    else
        mapped = HableToneMap(lin);

    float3 srgb = LinearToSRGB(saturate(mapped));
    float a = saturate(hdrColor.a);
    return PSOutput(float4(srgb, a));
}