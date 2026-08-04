// shaders/ToneMapping.hlsl
// HDR -> SDR tone mapping shader (Reinhard / Hable / ACES)
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

// scRGB (BT.709) -> ACES AP1 (ACEScg) 3x3 matrix
float3 SrgbToAp1(float3 c)
{
    float3 r;
    r.x = 0.613132f * c.x + 0.339538f * c.y + 0.047416f * c.z;
    r.y = 0.070124f * c.x + 0.916324f * c.y + 0.013452f * c.z;
    r.z = 0.020445f * c.x + 0.109548f * c.y + 0.870006f * c.z;
    return r;
}

// ACES AP1 -> sRGB (BT.709) inverse matrix
float3 Ap1ToSrgb(float3 c)
{
    float3 r;
    r.x = 1.704579f * c.x - 0.625505f * c.y - 0.078038f * c.z;
    r.y = -0.129701f * c.x + 1.139240f * c.y - 0.009570f * c.z;
    r.z = -0.019717f * c.x - 0.128087f * c.y + 1.147935f * c.z;
    return r;
}

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

// ACES RRT (Narkowicz 2015) + ODT - needs AP1 conversion
float3 ACESToneMap(float3 hdr)
{
    float3 ap1 = SrgbToAp1(hdr);
    const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
    float3 m = (ap1 * (a * ap1 + b)) / (ap1 * (c * ap1 + d) + e);
    m = saturate(m);
    m = m * (1.0f + 0.3f * (1.0f - m) * (1.0f - m));
    return Ap1ToSrgb(m);
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
        mapped = ACESToneMap(lin);
    else
        mapped = HableToneMap(lin);

    float3 srgb = LinearToSRGB(saturate(mapped));
    float a = saturate(hdrColor.a);
    return PSOutput(float4(srgb, a));
}