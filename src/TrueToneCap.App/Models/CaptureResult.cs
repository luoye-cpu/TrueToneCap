// TrueToneCap.App/Models/CaptureResult.cs
// 统一捕获结果 DTO — 封装 WGC 捕获的 HDR/SDR 数据

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Processing;
using Vortice.Direct3D11;

namespace TrueToneCap.App.Models;

/// <summary>捕获结果：包含像素数据和元数据。</summary>
public sealed class CaptureResult : IDisposable
{
    private bool _disposed;

    /// <summary>HDR 浮点像素 (R16G16B16A16_Float → float[])，无 HDR 时为 null。</summary>
    public float[]? HdrPixels { get; init; }

    /// <summary>SDR 字节像素 (BGRA8)，用于预览。</summary>
    public byte[]? SdrPixels { get; init; }

    /// <summary>GPU 纹理（D3D11 纹理引用，用于 GPU 直通编码路径）。
    /// 当此字段不为 null 时，编码器应优先使用纹理而非 CPU 像素数组，
    /// 避免 GPU→CPU→GPU 往返。</summary>
    public ID3D11Texture2D? GpuTexture { get; init; }

    /// <summary>是否有 HDR 浮点数据。</summary>
    public bool IsHdr => HdrPixels is not null;

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>显示器 ICC Profile（从 WCS 获取）。</summary>
    public byte[]? IccProfile { get; set; }

    /// <summary>捕获来源显示器信息。</summary>
    public DisplayInfo? SourceDisplay { get; set; }

    /// <summary>捕获耗时（毫秒）。</summary>
    public long CaptureTimeMs { get; set; }

    /// <summary>获取可用于显示/编码的 BGRA8 像素。HDR 帧会自动执行 GPU 色调映射。</summary>
    public byte[]? GetDisplayPixels()
    {
        if (SdrPixels is not null) return SdrPixels;
        if (HdrPixels is not null)
            return TrueToneCap.Core.Processing.ToneMapper.FloatToSRgbBytes(HdrPixels, Width, Height,
                new TrueToneCap.Core.Processing.ToneMappingParams { Mode = TrueToneCap.Core.Processing.ToneMapMode.Hable });
        return null;
    }

    /// <summary>获取可用于显示/编码的 BGRA8 像素（异步 GPU 路径）。</summary>
    public async Task<byte[]?> GetDisplayPixelsAsync(TrueToneCap.Core.Processing.GpuToneMapper? gpuMapper = null)
    {
        if (SdrPixels is not null) return SdrPixels;
        if (HdrPixels is not null && gpuMapper is not null)
        {
            return await gpuMapper.ToneMapToSdrAsync(HdrPixels, Width, Height,
                new TrueToneCap.Core.Processing.ToneMappingParams { Mode = TrueToneCap.Core.Processing.ToneMapMode.Hable });
        }
        return GetDisplayPixels();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (GpuTexture as IDisposable)?.Dispose(); } catch { }
    }
}
