// TrueToneCap.App/Models/CaptureResult.cs
// 统一捕获结果 DTO — 封装 WGC 捕获的 HDR/SDR 数据

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.App.Models;

/// <summary>捕获结果：包含像素数据和元数据。</summary>
public sealed class CaptureResult : IDisposable
{
    private bool _disposed;

    /// <summary>HDR 浮点像素 (R16G16B16A16_Float → float[])，SDR 捕获时为 null。</summary>
    public float[]? HdrPixels { get; init; }

    /// <summary>SDR 字节像素 (BGRA8)，HDR 捕获时为 null。</summary>
    public byte[]? SdrPixels { get; init; }

    /// <summary>是否为 HDR 捕获。</summary>
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
    }
}
