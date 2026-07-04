// TrueToneCap.Core/Capture/CaptureMode.cs
// 捕获模式定义

namespace TrueToneCap.Core.Capture;

/// <summary>屏幕捕获模式。</summary>
public enum CaptureMode
{
    /// <summary>全屏捕获（指定显示器）。</summary>
    FullScreen,

    /// <summary>区域捕获（用户拖选矩形）。</summary>
    Region,

    /// <summary>窗口捕获（使用 GraphicsCaptureItem / WinRT）。</summary>
    Window,

    /// <summary>自由形状捕获（通过遮罩）。</summary>
    Freeform
}

/// <summary>捕获区域矩形（整数坐标）。</summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height);

/// <summary>捕获选项。</summary>
public sealed record CaptureOptions(
    CaptureMode Mode,
    int DisplayIndex = 0,
    CaptureRect? Region = null,
    nint? WindowHandle = null,
    bool CaptureCursor = true,
    bool PreserveHdr = true
);
