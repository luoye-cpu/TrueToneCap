// shaders/CopyTexture.hlsl
// 简单纹理直通拷贝着色器 — 用于 HDR 预览（不做任何色彩转换）
// 编译: dxc -T ps_6_0 -E main CopyTexture.hlsl -Fo CopyTexture.hlsl.cso

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    return InputTexture.Sample(LinearSampler, input.uv);
}
