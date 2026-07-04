// TrueToneCap.Core/Annotation/AnnotationLayer.cs
// 矢量标注图层系统 — 支持形状、文字、马赛克等

using System.Numerics;

namespace TrueToneCap.Core.Annotation;

/// <summary>简单矩形（浮点坐标）。</summary>
public struct RectF
{
    public float Left, Top, Right, Bottom;
    public RectF(float left, float top, float right, float bottom)
    { Left = left; Top = top; Right = right; Bottom = bottom; }
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>RGBA 颜色。</summary>
public struct Color4
{
    public float R, G, B, A;
    public Color4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
}

/// <summary>标注形状类型。</summary>
public enum ShapeType
{
    Rectangle,
    Ellipse,
    Arrow,
    Freehand,
    Text,
    Mosaic,
    Highlight,
    SequenceNumber
}

/// <summary>标注图层基类。</summary>
public abstract class AnnotationLayer
{
    public Guid Id { get; } = Guid.NewGuid();
    public ShapeType Type { get; protected init; }
    public bool IsVisible { get; set; } = true;
    public float Opacity { get; set; } = 1.0f;
    public int ZOrder { get; set; }

    /// <summary>图层名称（用于图层面板显示）。</summary>
    public string Name => Type switch
    {
        ShapeType.Rectangle => "矩形",
        ShapeType.Ellipse => "椭圆",
        ShapeType.Arrow => "箭头",
        ShapeType.Freehand => "画笔",
        ShapeType.Text => "文字",
        ShapeType.Mosaic => "马赛克",
        ShapeType.Highlight => "高亮",
        ShapeType.SequenceNumber => "序号",
        _ => "未知"
    };

    public abstract AnnotationLayer Clone();
    public abstract RectF GetBounds();
}

/// <summary>笔刷样式。</summary>
public sealed class BrushStyle
{
    public Color4 FillColor { get; set; } = new(1, 1, 1, 0.3f);
    public Color4 StrokeColor { get; set; } = new(1, 0, 0, 1);
    public float StrokeWidth { get; set; } = 2.0f;
    public bool IsFillEnabled { get; set; } = true;
    public bool IsStrokeEnabled { get; set; } = true;
    public float[] DashPattern { get; set; } = [];

    public BrushStyle Clone() => new()
    {
        FillColor = FillColor,
        StrokeColor = StrokeColor,
        StrokeWidth = StrokeWidth,
        IsFillEnabled = IsFillEnabled,
        IsStrokeEnabled = IsStrokeEnabled,
        DashPattern = (float[])DashPattern.Clone()
    };
}

/// <summary>矩形图层。</summary>
public sealed class RectangleLayer : AnnotationLayer
{
    public RectangleLayer() => Type = ShapeType.Rectangle;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CornerRadius { get; set; }
    public BrushStyle Style { get; set; } = new();

    public override AnnotationLayer Clone() => new RectangleLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        CornerRadius = CornerRadius,
        Style = Style.Clone(),
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds() => new(X, Y, X + Width, Y + Height);
}

/// <summary>椭圆图层。</summary>
public sealed class EllipseLayer : AnnotationLayer
{
    public EllipseLayer() => Type = ShapeType.Ellipse;
    public float CenterX { get; set; }
    public float CenterY { get; set; }
    public float RadiusX { get; set; }
    public float RadiusY { get; set; }
    public BrushStyle Style { get; set; } = new();

    public override AnnotationLayer Clone() => new EllipseLayer
    {
        CenterX = CenterX, CenterY = CenterY,
        RadiusX = RadiusX, RadiusY = RadiusY,
        Style = Style.Clone(),
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds() => new(
        CenterX - RadiusX, CenterY - RadiusY,
        CenterX + RadiusX, CenterY + RadiusY);
}

/// <summary>箭头图层。</summary>
public sealed class ArrowLayer : AnnotationLayer
{
    public ArrowLayer() => Type = ShapeType.Arrow;
    public float StartX { get; set; }
    public float StartY { get; set; }
    public float EndX { get; set; }
    public float EndY { get; set; }
    public float ArrowHeadSize { get; set; } = 12f;
    public BrushStyle Style { get; set; } = new();

    public override AnnotationLayer Clone() => new ArrowLayer
    {
        StartX = StartX, StartY = StartY, EndX = EndX, EndY = EndY,
        ArrowHeadSize = ArrowHeadSize,
        Style = Style.Clone(),
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds()
    {
        float minX = Math.Min(StartX, EndX) - ArrowHeadSize;
        float minY = Math.Min(StartY, EndY) - ArrowHeadSize;
        float maxX = Math.Max(StartX, EndX) + ArrowHeadSize;
        float maxY = Math.Max(StartY, EndY) + ArrowHeadSize;
        return new RectF(minX, minY, maxX, maxY);
    }
}

/// <summary>自由笔迹图层。</summary>
public sealed class FreehandLayer : AnnotationLayer
{
    public FreehandLayer() => Type = ShapeType.Freehand;
    public List<Vector2> Points { get; set; } = [];
    public BrushStyle Style { get; set; } = new();

    public override AnnotationLayer Clone() => new FreehandLayer
    {
        Points = [.. Points],
        Style = Style.Clone(),
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds()
    {
        if (Points.Count == 0) return new RectF(0, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new RectF(minX - 5, minY - 5, maxX + 5, maxY + 5);
    }
}

/// <summary>文字图层。</summary>
public sealed class TextLayer : AnnotationLayer
{
    public TextLayer() => Type = ShapeType.Text;
    public string Text { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float FontSize { get; set; } = 16f;
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public Color4 TextColor { get; set; } = new(1, 1, 1, 1);
    public Color4 BackgroundColor { get; set; } = new(0, 0, 0, 0.6f);
    public float Padding { get; set; } = 6f;

    // 编辑后缓存的文本边界
    public float CachedWidth { get; set; }
    public float CachedHeight { get; set; }

    public override AnnotationLayer Clone() => new TextLayer
    {
        Text = Text, X = X, Y = Y, FontSize = FontSize,
        FontFamily = FontFamily, TextColor = TextColor,
        BackgroundColor = BackgroundColor, Padding = Padding,
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds()
    {
        float w = CachedWidth > 0 ? CachedWidth : 100f;
        float h = CachedHeight > 0 ? CachedHeight : FontSize + Padding * 2;
        return new RectF(X, Y, X + w + Padding * 2, Y + h + Padding * 2);
    }
}

/// <summary>马赛克图层。</summary>
public sealed class MosaicLayer : AnnotationLayer
{
    public MosaicLayer() => Type = ShapeType.Mosaic;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float BlockSize { get; set; } = 10f;
    public float BlurAmount { get; set; } = 0.3f;

    public override AnnotationLayer Clone() => new MosaicLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        BlockSize = BlockSize, BlurAmount = BlurAmount,
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds() => new(X, Y, X + Width, Y + Height);
}

/// <summary>高亮图层（半透明黄色覆盖）。</summary>
public sealed class HighlightLayer : AnnotationLayer
{
    public HighlightLayer() => Type = ShapeType.Highlight;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public Color4 HighlightColor { get; set; } = new(1, 1, 0, 0.4f);

    public override AnnotationLayer Clone() => new HighlightLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        HighlightColor = HighlightColor,
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds() => new(X, Y, X + Width, Y + Height);
}

/// <summary>序号标注图层。</summary>
public sealed class SequenceNumberLayer : AnnotationLayer
{
    public SequenceNumberLayer() => Type = ShapeType.SequenceNumber;
    public int Number { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; } = 14f;
    public Color4 CircleColor { get; set; } = new(1, 0, 0, 1);
    public Color4 TextColor { get; set; } = new(1, 1, 1, 1);

    public override AnnotationLayer Clone() => new SequenceNumberLayer
    {
        Number = Number, X = X, Y = Y, Radius = Radius,
        CircleColor = CircleColor, TextColor = TextColor,
        IsVisible = IsVisible, Opacity = Opacity, ZOrder = ZOrder
    };

    public override RectF GetBounds() => new(
        X - Radius, Y - Radius, X + Radius, Y + Radius);
}
