// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// shaders/ToneMapping.hlsl
// HDR -> SDR tone mapping shader (Reinhard / Hable / SegmentedReinhard)
// ACES 已移除 (2026-08-08): 生产恒用分段 Reinhard, 无激活路径。可行性见 docs/architecture-design.md
// Compile: fxc /T ps_5_0 /E main ToneMapping.hlsl /Fo ToneMapping.hlsl.cso
// ⚠️ 编译器是 fxc (SM5.0) 而非 dxc —— dxc 产出的 SM6/DXIL 会被 D3D11 CreateShader
//    以 E_INVALIDARG 拒绝。本注释曾误写为 dxc ps_6_0，与 Shaders/CompileShaders.ps1
//    及 App/Services/ShaderCompiler.cs 的实际配置相反，2026-09-15 修正。

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

// 分段 Reinhard — 必须与 CPU 侧 ToneMapper.SegmentedReinhardMap 严格一致
//
// ═══ 2026-09-15 修复：消除 SDR 白点处的亮度断崖（暗环）═══
// 旧实现：y>1 时套用 ReinhardMap(y,h) = (1 + y/h²)/(1 + y)·y，其 R(1) ≈ 0.52（headroom=5），
//         而左分支 f(1) = 1.0；过渡区仅用 smoothstep 在 1.0 与 R(y) 之间插值，
//         而 R 在 y∈[1,1.25] 仍只有 0.52~0.58 ⇒ 函数在 y=1 处**先降后升**（非单调），
//         高光边缘出现一圈"暗环"伪影，天空/灯光/反光场景尤其明显。
// 新实现：在「SDR 白点保真 f(1)=1」「输出不超上界 f≤1」「单调不减」三重约束下，
//         f(y>1) ≡ 1.0（饱和）是**唯一解**。超出 SDR 白点的能量由 GainMap 承载
//         （gain = log2(y / f(y)) = log2(y)），HDR 查看器据其完整还原。
//
// ⚠️ CPU 侧（Core/Processing/ToneMapper.cs:236-246）已于 2026-08-30 完成同样修正，
//    本文件当时未同步 ⇒ GPU 预览与 CPU 输出长期不一致，且注释仍自称"与 CPU 一致"。
//    本文件由 HdrCaptureWindow.cs:346 直接加载（GpuToneMapper 类虽未被调用，
//    但 HdrCaptureWindow 绕开它直连 cso），故该漂移**真实生效**，不是死代码。
//    headroom 仅为与 CPU 侧保持签名兼容而保留，不参与运算。
float SegmentedReinhardMap(float y, float headroom)
{
    if (headroom <= 0.0f || y <= 1.0f) return saturate(y);
    return 1.0f;
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
    // fxc 兼容: select → 三元运算符 (SM5.0)
    return (clamped <= 0.0031308f) ? low : high;
}

float4 main(PSInput input) : SV_TARGET
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
    return float4(srgb, a); // fxc 兼容: 直接返回 float4 (不支持 struct 构造)
}