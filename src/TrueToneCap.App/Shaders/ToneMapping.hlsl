// shaders/ToneMapping.hlsl
// HDR → SDR 色调映射着色器（Reinhard / Hable / ACES）
// 编译命令: dxc -T ps_6_0 -E main ToneMapping.hlsl -Fo ToneMapping.cso

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer ToneMappingParams : register(b0)
{
    uint  ToneMapMode;       // 0=Reinhard, 1=Hable, 2=ACES
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

// ── Reinhard 色调映射（亮度缩放，与 CPU 路径一致）──
float3 ReinhardToneMap(float3 hdr)
{
    // Rec.709 亮度系数
    float lum = dot(hdr, float3(0.2126f, 0.7152f, 0.0722f));
    float mappedLum = lum / (1.0f + lum);
    // 亮度缩放：保持色彩比例，避免高饱和色偏移
    float scale = (lum > 0.0001f) ? (mappedLum / lum) : 0.0f;
    return saturate(hdr * scale);
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

// ── ACES Filmic 色调映射 (Narkowicz 2015 拟合) ──
float3 ACESToneMap(float3 hdr)
{
    const float a = 2.51f;
    const float b = 0.03f;
    const float c = 2.43f;
    const float d = 0.59f;
    const float e = 0.14f;
    return saturate((hdr * (a * hdr + b)) / (hdr * (c * hdr + d) + e));
}

// ── 线性 → sRGB Gamma ──
float3 LinearToSRGB(float3 c)
{
    // DXC 要求向量条件使用 select() 而非三元运算符
    float3 low = 12.92f * c;
    float3 high = 1.055f * pow(c, 1.0f / 2.4f) - 0.055f;
    return select(c <= 0.0031308f, low, high);
}

// ── 主入口 ──
PSOutput main(PSInput input)
{
    float4 hdrColor = InputTexture.Sample(LinearSampler, input.uv);

    // 曝光调整
    float3 exposed = hdrColor.rgb * exp2(Exposure);

    // 色调映射
    float3 mapped;
    if (ToneMapMode == 0)
        mapped = ReinhardToneMap(exposed);
    else if (ToneMapMode == 2)
        mapped = ACESToneMap(exposed);
    else
        mapped = HableToneMap(exposed);

    // Gamma 编码到 sRGB
    float3 srgb = LinearToSRGB(saturate(mapped));

    return PSOutput(float4(srgb, hdrColor.a));
}
