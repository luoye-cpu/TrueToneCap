// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
    SequenceNumber,
    /// <summary>马赛克（画笔涂抹）：沿笔迹的若干矩形块，而不是一个矩形区域。</summary>
    MosaicBrush
}

/// <summary>标注图层基类。</summary>
public abstract class AnnotationLayer
{
    /// <summary>图层身份。Clone() 必须保留它 —— 撤销/重做与按 Id 更新的命令都靠这份身份，
    /// 每次 Clone 发新 Guid 会让 UpdateLayer 之后再也找不到"那一层"（见 AnnotationManager.UpdateLayer）。</summary>
    public Guid Id { get; protected set; } = Guid.NewGuid();
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
        ShapeType.MosaicBrush => "马赛克（涂抹）",
        ShapeType.Highlight => "高亮",
        ShapeType.SequenceNumber => "序号",
        _ => "未知"
    };

    public abstract AnnotationLayer Clone();
    public abstract RectF GetBounds();

    /// <summary>把基类状态（身份/可见性/不透明度/层序）抄到副本上，供各子类 Clone() 收尾。</summary>
    protected T CopyBaseState<T>(T clone) where T : AnnotationLayer
    {
        clone.Id = Id;
        clone.IsVisible = IsVisible;
        clone.Opacity = Opacity;
        clone.ZOrder = ZOrder;
        return clone;
    }

    /// <summary>把 <paramref name="source"/> 的图层身份抄到本实例上。</summary>
    /// <remarks>给"同一图层的新状态"用：调用方要构造一个新实例喂给 <c>AnnotationManager.UpdateLayer</c>，
    /// 只有身份对得上，撤销/重做与后续按 Id 的更新才找得到它。</remarks>
    public void TakeIdentityFrom(AnnotationLayer source) => Id = source.Id;

    // ────────────── 工厂方法 ──────────────

    public static AnnotationLayer CreateRectangle(float x, float y, float w, float h, string color, float strokeWidth = 2f) => new RectangleLayer
    {
        X = x, Y = y, Width = w, Height = h,
        Style = new BrushStyle { StrokeColor = ParseColor(color), StrokeWidth = strokeWidth, IsFillEnabled = false }
    };

    public static AnnotationLayer CreateEllipse(float cx, float cy, float rx, float ry, string color, float strokeWidth = 2f) => new EllipseLayer
    {
        CenterX = cx, CenterY = cy, RadiusX = rx, RadiusY = ry,
        Style = new BrushStyle { StrokeColor = ParseColor(color), StrokeWidth = strokeWidth, IsFillEnabled = false }
    };

    public static AnnotationLayer CreateArrow(float x1, float y1, float x2, float y2, string color, float strokeWidth = 2f) => new ArrowLayer
    {
        StartX = x1, StartY = y1, EndX = x2, EndY = y2,
        Style = new BrushStyle { StrokeColor = ParseColor(color), StrokeWidth = strokeWidth }
    };

    public static AnnotationLayer CreateText(float x, float y, string text, string color, float fontSize = 16f) => new TextLayer
    {
        X = x, Y = y, Text = text, FontSize = fontSize,
        TextColor = ParseColor(color, 1f)
    };

    public static AnnotationLayer CreateNumber(float x, float y, int number, string color, float fontSize = 20f) => new TextLayer
    {
        X = x, Y = y, Text = number.ToString(), FontSize = fontSize,
        TextColor = ParseColor(color, 1f)
    };

    public static AnnotationLayer CreateBlur(float x, float y, float w, float h, float radius = 10f) => new RectangleLayer
    {
        X = x, Y = y, Width = w, Height = h, Type = ShapeType.Highlight,
        Style = new BrushStyle { IsFillEnabled = false, IsStrokeEnabled = false }
    };

    /// <summary>创建马赛克图层。</summary>
    /// <remarks>
    /// ═══ 2026-09-15 修复：必须返回 MosaicLayer，不能返回 RectangleLayer ═══
    /// 原实现返回 `RectangleLayer` 并把 Type 改成 Mosaic —— 但光栅化
    /// （AnnotationRasterizer.RenderLayer）是按 **CLR 类型** 分派的：
    /// RectangleLayer 会走 RenderRectOutline，而这里的 Style 把 IsStrokeEnabled 设为 false，
    /// 于是直接 return ⇒ **马赛克在最终图像里完全不可见**，只剩一个空图层。
    /// 返回正确的 MosaicLayer 才会进入 RenderMosaic 分支。
    /// （当前该方法仅被测试调用，生产路径尚未触达，属提前排雷。）
    /// </remarks>
    public static AnnotationLayer CreateMosaic(float x, float y, float w, float h, int blockSize = 8) => new MosaicLayer
    {
        X = x, Y = y, Width = w, Height = h, BlockSize = blockSize
    };

    public static AnnotationLayer CreateFreehand((float x, float y)[] points, string color, float strokeWidth = 3f) => new FreehandLayer
    {
        Points = [.. points.Select(p => new Vector2(p.x, p.y))],
        Style = new BrushStyle { StrokeColor = ParseColor(color), StrokeWidth = strokeWidth, IsFillEnabled = false }
    };

    private static Color4 ParseColor(string hex, float alpha = 1f)
    {
        if (hex.StartsWith('#'))
        {
            if (hex.Length == 7)
                return new Color4(
                    int.Parse(hex[1..3], System.Globalization.NumberStyles.HexNumber) / 255f,
                    int.Parse(hex[3..5], System.Globalization.NumberStyles.HexNumber) / 255f,
                    int.Parse(hex[5..7], System.Globalization.NumberStyles.HexNumber) / 255f,
                    alpha);
            if (hex.Length == 9)
                return new Color4(
                    int.Parse(hex[1..3], System.Globalization.NumberStyles.HexNumber) / 255f,
                    int.Parse(hex[3..5], System.Globalization.NumberStyles.HexNumber) / 255f,
                    int.Parse(hex[5..7], System.Globalization.NumberStyles.HexNumber) / 255f,
                    int.Parse(hex[7..9], System.Globalization.NumberStyles.HexNumber) / 255f);
        }
        return new Color4(1, 0, 0, alpha); // 默认红色
    }
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

    public override AnnotationLayer Clone() => CopyBaseState(new RectangleLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        CornerRadius = CornerRadius,
        Style = Style.Clone(),
    });

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

    public override AnnotationLayer Clone() => CopyBaseState(new EllipseLayer
    {
        CenterX = CenterX, CenterY = CenterY,
        RadiusX = RadiusX, RadiusY = RadiusY,
        Style = Style.Clone(),
    });

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

    public override AnnotationLayer Clone() => CopyBaseState(new ArrowLayer
    {
        StartX = StartX, StartY = StartY, EndX = EndX, EndY = EndY,
        ArrowHeadSize = ArrowHeadSize,
        Style = Style.Clone(),
    });

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

    public override AnnotationLayer Clone() => CopyBaseState(new FreehandLayer
    {
        Points = [.. Points],
        Style = Style.Clone(),
    });

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
/// <remarks>
/// ═══ 2026-10-27：文字尺寸只有一把尺 ═══
/// <para><see cref="CachedWidth"/>/<see cref="CachedHeight"/> 是**光栅化位图的实测尺寸**，
/// 由 <c>AnnotationRasterizer.RasterizeText</c>（Win2D CanvasTextLayout）量出后写回。
/// GPU 预览把这块位图原样贴到 <see cref="GetBounds"/> 的内框上，CPU 输出用同一函数重画同样的位图 ⇒
/// 预览与文件里的字号/折行/字宽只可能一致。旧实现预览用固定 256×64 槽位拉伸、输出用
/// <c>字数×字号×1.05</c> 估宽，两边从来不是同一张图。</para>
/// </remarks>
public sealed class TextLayer : AnnotationLayer
{
    public TextLayer() => Type = ShapeType.Text;
    public string Text { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>字号（物理像素，等于选区图像坐标系的单位）。</summary>
    public float FontSize { get; set; } = 24f;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>自动换行宽度（物理像素，0 = 不换行）。编辑时按选区宽度写入，两端量法相同。</summary>
    public float MaxWidth { get; set; }
    public Color4 TextColor { get; set; } = new(1, 1, 1, 1);
    public Color4 BackgroundColor { get; set; } = new(0, 0, 0, 0.6f);
    public float Padding { get; set; } = 6f;

    // 光栅化位图实测尺寸（见类型注释）
    public float CachedWidth { get; set; }
    public float CachedHeight { get; set; }

    public override AnnotationLayer Clone() => CopyBaseState(new TextLayer
    {
        Text = Text, X = X, Y = Y, FontSize = FontSize,
        FontFamily = FontFamily, Bold = Bold, Italic = Italic, MaxWidth = MaxWidth,
        TextColor = TextColor, BackgroundColor = BackgroundColor, Padding = Padding,
        CachedWidth = CachedWidth, CachedHeight = CachedHeight,
    });

    public override RectF GetBounds()
    {
        float w = CachedWidth > 0 ? CachedWidth : Math.Max(1f, Text.Length * FontSize * 1.05f);
        float h = CachedHeight > 0 ? CachedHeight : FontSize * 1.4f;
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

    public override AnnotationLayer Clone() => CopyBaseState(new MosaicLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        BlockSize = BlockSize, BlurAmount = BlurAmount,
    });

    public override RectF GetBounds() => new(X, Y, X + Width, Y + Height);
}

/// <summary>
/// 马赛克（画笔涂抹）图层：只把笔迹经过的方块打码，而不是整片矩形。
/// <para>与 <see cref="MosaicLayer"/> 分开，是因为两者在预览（GPU 常量缓冲）与 CPU 合成里
/// 落的是不同分支：矩形按 bounds 判断，涂抹要按"点到折线的距离"判断。</para>
/// </summary>
public sealed class MosaicBrushLayer : AnnotationLayer
{
    public MosaicBrushLayer() => Type = ShapeType.MosaicBrush;
    public List<Vector2> Points { get; set; } = [];
    public float BlockSize { get; set; } = 10f;
    /// <summary>涂抹直径（像素）。</summary>
    public float BrushWidth { get; set; } = 28f;

    public override AnnotationLayer Clone() => CopyBaseState(new MosaicBrushLayer
    {
        Points = [.. Points], BlockSize = BlockSize, BrushWidth = BrushWidth,
    });

    public override RectF GetBounds()
    {
        if (Points.Count == 0) return new RectF(0, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        float r = BrushWidth / 2f + 2f;
        return new RectF(minX - r, minY - r, maxX + r, maxY + r);
    }
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

    public override AnnotationLayer Clone() => CopyBaseState(new HighlightLayer
    {
        X = X, Y = Y, Width = Width, Height = Height,
        HighlightColor = HighlightColor,
    });

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

    public override AnnotationLayer Clone() => CopyBaseState(new SequenceNumberLayer
    {
        Number = Number, X = X, Y = Y, Radius = Radius,
        CircleColor = CircleColor, TextColor = TextColor,
    });

    public override RectF GetBounds() => new(
        X - Radius, Y - Radius, X + Radius, Y + Radius);
}
