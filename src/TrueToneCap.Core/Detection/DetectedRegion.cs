// TrueToneCap.Core/Detection/DetectedRegion.cs
// 自动识别区域 DTO

namespace TrueToneCap.Core.Detection;

/// <summary>检测到的屏幕区域。</summary>
public sealed class DetectedRegion
{
    /// <summary>区域在虚拟桌面中的像素坐标 X。</summary>
    public int X { get; init; }
    /// <summary>区域在虚拟桌面中的像素坐标 Y。</summary>
    public int Y { get; init; }
    /// <summary>区域像素宽度。</summary>
    public int Width { get; init; }
    /// <summary>区域像素高度。</summary>
    public int Height { get; init; }

    /// <summary>窗口标题（仅 UIA 检测时有值）。</summary>
    public string? Title { get; init; }
    /// <summary>窗口类名（仅 UIA 检测时有值）。</summary>
    public string? ClassName { get; init; }

    /// <summary>检测来源。</summary>
    public RegionSource Source { get; init; }

    /// <summary>面积（Width × Height）。</summary>
    public int Area => Width * Height;

    /// <summary>在虚拟桌面坐标系下的矩形。</summary>
    public System.Drawing.Rectangle Rect => new(X, Y, Width, Height);

    public override string ToString() =>
        $"[{Source}] {X},{Y} {Width}x{Height}" + (Title is { Length: > 0 } t ? $" \"{t}\"" : "");
}

/// <summary>区域检测来源。</summary>
public enum RegionSource
{
    /// <summary>Windows UI Automation / EnumWindows。</summary>
    Uia,
    /// <summary>边缘检测。</summary>
    Edge,
}
