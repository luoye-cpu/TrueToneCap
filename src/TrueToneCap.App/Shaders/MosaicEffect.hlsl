// shaders/MosaicEffect.hlsl
// 马赛克效果着色器（像素化 + 模糊混合）

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer MosaicParams : register(b0)
{
    float2 BlockSize;       // 马赛克块大小 (x, y)
    float  BlurAmount;      // 模糊强度 (0~1)
    float  AspectRatio;     // 宽高比校正
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

PSOutput main(PSInput input)
{
    float2 uv = input.uv;

    // 像素化：将 UV 坐标量化到块边界
    float2 blockUV = floor(uv * BlockSize) / BlockSize;

    // 在块中心采样
    float2 centerUV = blockUV + (0.5f / BlockSize);

    // 如果模糊 > 0，在块内做简单平均
    float4 color;
    if (BlurAmount > 0.001f)
    {
        float2 halfBlock = (0.5f / BlockSize) * BlurAmount;
        color = InputTexture.Sample(LinearSampler, centerUV);

        // 4 点采样混合
        float4 c1 = InputTexture.Sample(LinearSampler, centerUV + float2(halfBlock.x, 0));
        float4 c2 = InputTexture.Sample(LinearSampler, centerUV - float2(halfBlock.x, 0));
        float4 c3 = InputTexture.Sample(LinearSampler, centerUV + float2(0, halfBlock.y));
        float4 c4 = InputTexture.Sample(LinearSampler, centerUV - float2(0, halfBlock.y));

        color = (color * 0.6f + (c1 + c2 + c3 + c4) * 0.1f);
    }
    else
    {
        // 纯像素化：点采样
        color = InputTexture.Sample(LinearSampler, centerUV);
    }

    return PSOutput(color);
}
