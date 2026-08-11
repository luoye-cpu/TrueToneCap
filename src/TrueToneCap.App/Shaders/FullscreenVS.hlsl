// FullscreenVS.hlsl
// 全屏三角形顶点着色器 — 使用 SV_VertexID 程序化生成顶点
// 无需顶点缓冲区，Draw(3, 0) 即可覆盖整个视口
// 编译: dxc -T vs_6_0 -E main FullscreenVS.hlsl -Fo FullscreenVS.cso

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOutput main(uint vid : SV_VertexID)
{
    VSOutput output;
    // 全屏三角形: 3 个顶点覆盖整个 NDC [-1,1] 范围
    // 2026-08-11 修复: 绕序改为顺时针 (CW) — D3D11 默认剔除 CCW 背面,
    // 原 CCW 顺序导致三角形被整体剔除 → 画面全黑 (此前从未真机运行过)
    // vid=0: (-1,-1) uv(0,1)  左下
    // vid=1: (-1, 3) uv(0,-1) 上
    // vid=2: ( 3,-1) uv(2,1)  右
    output.pos = float4(
        (vid == 2) ?  3.0f : -1.0f,
        (vid == 1) ?  3.0f : -1.0f,
        0.0f, 1.0f);
    output.uv = float2(
        (vid == 2) ?  2.0f :  0.0f,
        (vid == 1) ? -1.0f :  1.0f);
    return output;
}
