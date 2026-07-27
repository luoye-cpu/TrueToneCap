// TrueToneCap.Core/Platform/IPlatformServices.cs
// 平台服务抽象 — 窗口检测、色彩管理、系统集成的跨平台接口
// Windows 实现: user32.dll / mscms.dll / Registry
// Linux 未来实现: X11/Wayland / lcms2 / freedesktop

namespace TrueToneCap.Core.Platform;

/// <summary>
/// 窗口/区域检测抽象。
/// Windows: user32.dll EnumWindows + GetWindowRect
/// Linux 未来: X11 _NET_CLIENT_LIST / Wayland xdg-foreign
/// </summary>
public interface IWindowDetector
{
    /// <summary>获取光标位置下的窗口区域。</summary>
    DetectedRegion? GetRegionAtPoint(int x, int y);

    /// <summary>枚举所有可见窗口区域。</summary>
    IReadOnlyList<DetectedRegion> EnumerateVisibleRegions();
}

/// <summary>检测到的窗口区域。</summary>
public sealed record DetectedRegion(
    nint WindowHandle,
    string Title,
    int X, int Y,
    int Width, int Height);

/// <summary>
/// 色彩配置文件提供者抽象。
/// Windows: WCS API (mscms.dll) + 系统 sRGB.icm
/// Linux 未来: lcms2 + /usr/share/color/icc + colord (D-Bus)
/// </summary>
public interface IColorProfileProvider
{
    /// <summary>获取指定显示器的 ICC 配置文件。</summary>
    byte[]? GetDisplayIccProfile(nint displayHandle);

    /// <summary>获取标准色彩空间的 ICC 配置文件。</summary>
    byte[] GetStandardIccProfile(string colorSpace);

    /// <summary>将像素从源 ICC 烘焙到目标色彩空间。</summary>
    (byte[]? pixels, byte[]? targetIcc) BakeIccToTarget(
        byte[] bgra, int w, int h, byte[] sourceIcc, string targetColorSpace);
}

/// <summary>
/// 系统元数据收集器抽象。
/// Windows: user32 GetForegroundWindow + GetCursorInfo
/// Linux 未来: X11 _NET_ACTIVE_WINDOW / Wayland xdg-activation
/// </summary>
public interface IMetadataCollector
{
    /// <summary>获取前台窗口标题。</summary>
    string? GetForegroundWindowTitle();

    /// <summary>获取光标位置。</summary>
    (int X, int Y) GetCursorPosition();

    /// <summary>获取光标所在显示器句柄。</summary>
    nint GetMonitorUnderCursor();
}

/// <summary>
/// 平台能力检测抽象。
/// Windows: Registry + DXGI + SetupAPI
/// Linux 未来: /sys/class/drm + vulkaninfo + /proc/cpuinfo
/// </summary>
public interface ICapabilityDetector
{
    /// <summary>系统是否启用 HDR。</summary>
    bool IsSystemHdrEnabled { get; }

    /// <summary>系统是否启用自动色彩管理。</summary>
    bool IsAutoColorManagementEnabled { get; }

    /// <summary>检测可用的硬件编码器。</summary>
    IReadOnlyList<HardwareEncoderInfo> DetectHardwareEncoders();
}

/// <summary>硬件编码器信息。</summary>
public sealed record HardwareEncoderInfo(
    string Name,
    HardwareEncoderType Type,
    bool Available);

/// <summary>硬件编码器类型。</summary>
public enum HardwareEncoderType
{
    /// <summary>NVIDIA NVENC。</summary>
    Nvenc,
    /// <summary>Intel QSV。</summary>
    Qsv,
    /// <summary>AMD AMF。</summary>
    Amf,
    /// <summary>Linux VA-API (Intel/AMD)。</summary>
    VaApi,
    /// <summary>Linux V4L2 M2M (ARM SoC)。</summary>
    V4L2M2M,
}
