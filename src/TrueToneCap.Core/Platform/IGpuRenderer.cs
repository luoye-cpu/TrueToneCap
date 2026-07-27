// TrueToneCap.Core/Platform/IGpuRenderer.cs
// GPU 渲染抽象 — 为 Linux (Vulkan/OpenGL) 迁移预留接口
// Windows 实现: GpuToneMapper / GpuEffectProcessor (D3D11 + HLSL)
// Linux 未来实现: VulkanToneMapper / OpenGLToneMapper

namespace TrueToneCap.Core.Platform;

/// <summary>
/// GPU 渲染器接口 — 色调映射和后处理效果的跨平台抽象。
/// Windows: D3D11 + HLSL (GpuToneMapper)
/// Linux 未来: Vulkan + GLSL / OpenGL 4.6 + GLSL
/// </summary>
public interface IGpuRenderer : IDisposable
{
    /// <summary>渲染器名称（D3D11 / Vulkan / OpenGL）。</summary>
    string RendererName { get; }

    /// <summary>GPU 渲染是否可用。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// HDR → SDR 色调映射。
    /// 输入: scRGB linear float[] (RGBA)
    /// 输出: BGRA8 byte[]
    /// </summary>
    byte[] ToneMapHdrToSdr(float[] hdrPixels, int width, int height,
        Processing.ToneMappingParams toneParams);

    /// <summary>
    /// 马赛克效果。
    /// 输入/输出: BGRA8 byte[]
    /// </summary>
    void ApplyMosaic(byte[] pixels, int width, int height, int blockSize);
}
