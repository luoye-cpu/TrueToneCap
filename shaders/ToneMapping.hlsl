// shaders/ToneMapping.hlsl
// HDR → SDR 色调映射着色器（Reinhard / Hable）
// 编译命令: dxc -T ps_6_0 -E main ToneMapping.hlsl -Fo ToneMapping.cso

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer ToneMappingParams : register(b0)
{
    uint  ToneMapMode;       // 0=Reinhard, 1=Hable
    float Exposure;          // EV 曝光补偿
    float PaperWhiteNits;    // SDR 纸白亮度 (nits)
    float DisplayMaxNits;    // HDR 显示器最大亮度
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

// ── Reinhard 色调映射（简单、快速）──
float3 ReinhardToneMap(float3 hdr)
{
    // Reinhard 全局算子
    return hdr / (hdr + 1.0f);
}

// ── Hable (Uncharted 2) Filmic 色调映射 ──
float3 HableCurve(float3 x)
{
    const float A = 0.15f;
    const float B = 0.50f;
    const float C = 0.10f;
    const float D = 0.20f;
    const float E = 0.02f;
    const float F = 0.30f;

    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 HableToneMap(float3 hdr)
{
    float3 curr = HableCurve(hdr);
    float3 whiteScale = 1.0f / HableCurve(float3(11.2f, 11.2f, 11.2f));
    return curr * whiteScale;
}

// ── 线性 → sRGB Gamma ──
float3 LinearToSRGB(float3 c)
{
    // 使用 select 避免分支
    return (c <= 0.0031308f) ? (12.92f * c) : (1.055f * pow(c, 1.0f / 2.4f) - 0.055f);
}

// ── 主入口 ──
PSOutput main(PSInput input)
{
    float4 hdrColor = InputTexture.Sample(LinearSampler, input.uv);

    // 曝光调整
    float3 linear = hdrColor.rgb * exp2(Exposure);

    // 色调映射
    float3 mapped;
    if (ToneMapMode == 0)
        mapped = ReinhardToneMap(linear);
    else
        mapped = HableToneMap(linear);

    // Gamma 编码到 sRGB
    float3 srgb = LinearToSRGB(saturate(mapped));

    return PSOutput(float4(srgb, hdrColor.a));
}
