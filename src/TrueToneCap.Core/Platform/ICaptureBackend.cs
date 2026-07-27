// TrueToneCap.Core/Platform/ICaptureBackend.cs
// 平台捕获后端抽象 — 为 Linux (KMS/DRM, PipeWire, X11) 迁移预留接口
// Windows 实现: WgcCaptureService (App 层)
// Linux 未来实现: KmsCaptureBackend / PipeWireCaptureBackend

namespace TrueToneCap.Core.Platform;

/// <summary>捕获帧数据（平台无关）。</summary>
public sealed record CapturedFrame(
    byte[] Pixels,
    int Width,
    int Height,
    PixelFormat Format,
    bool IsHdr,
    object? NativeTexture = null);

/// <summary>像素格式。</summary>
public enum PixelFormat
{
    /// <summary>BGRA8 SDR (Windows D3D11 B8G8R8A8)。</summary>
    Bgra8,
    /// <summary>RGBA16F HDR (scRGB linear)。</summary>
    Rgba16Float,
    /// <summary>RGBA8 (Linux 常见格式)。</summary>
    Rgba8,
}

/// <summary>显示器信息（平台无关）。</summary>
public sealed record DisplayInfo(
    nint Handle,
    string DeviceName,
    int X, int Y,
    int Width, int Height,
    bool IsPrimary,
    bool IsHdr,
    int BitsPerColor);

/// <summary>
/// 平台捕获后端接口。
/// Windows: WGC (Windows.Graphics.Capture)
/// Linux 未来: KMS/DRM (无合成器) / PipeWire (Wayland) / X11 SHM
/// </summary>
public interface ICaptureBackend : IDisposable
{
    /// <summary>后端名称（用于日志和 UI 显示）。</summary>
    string BackendName { get; }

    /// <summary>是否支持 HDR 捕获。</summary>
    bool SupportsHdr { get; }

    /// <summary>枚举所有活动显示器。</summary>
    IReadOnlyList<DisplayInfo> EnumerateDisplays();

    /// <summary>捕获指定显示器的当前帧。</summary>
    Task<CapturedFrame> CaptureDisplayAsync(nint displayHandle, bool preferHdr, CancellationToken ct = default);

    /// <summary>捕获所有显示器（拼接为虚拟桌面）。</summary>
    Task<CapturedFrame> CaptureAllDisplaysAsync(bool preferHdr, CancellationToken ct = default);
}
