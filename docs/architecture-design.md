# TrueToneCap — 高端 Windows 截图软件架构设计

> **目标系统**：Windows 11 24H2+ | **技术栈**：C# 13 / WinUI 3 / .NET 10 / Vortice.Windows / DirectX

---

## 目录

1. [总体架构设计](#1-总体架构设计)
2. [屏幕捕获与 HDR 管线](#2-屏幕捕获与-hdr-管线)
3. [标注引擎设计](#3-标注引擎设计)
4. [多格式编码器](#4-多格式编码器)
5. [色彩管理与 ICC 嵌入](#5-色彩管理与-icc-嵌入)
6. [元数据收集与写入](#6-元数据收集与写入)
7. [动图录制模块](#7-动图录制模块)
8. [构建与部署](#8-构建与部署)
9. [技术难点与解决方案](#9-技术难点与解决方案)

---

## 1. 总体架构设计

### 1.1 项目结构

```
TrueToneCap/
├── TrueToneCap.sln
├── shaders/                              # HLSL 着色器
│   ├── ToneMapping.hlsl                  # Reinhard / Hable 色调映射
│   ├── MosaicEffect.hlsl                 # 马赛克效果
│   └── CompileShaders.ps1               # 编译脚本
├── src/
│   ├── TrueToneCap.Core/                 # 核心类库（无 UI 依赖）
│   │   ├── Capture/
│   │   │   ├── ScreenCapture.cs          # DXGI Desktop Duplication 封装
│   │   │   ├── CapturedFrame.cs          # 捕获帧封装（ID3D11Texture2D）
│   │   │   ├── CaptureMode.cs            # 捕获模式枚举
│   │   │   └── GraphicsCaptureItemInterop.cs  # WinRT GraphicsCaptureItem 互操作
│   │   ├── Annotation/
│   │   │   ├── ShapeLayer.cs             # 矢量标注图层基类
│   │   │   ├── AnnotationEngine.cs       # 标注引擎（图层管理+渲染）
│   │   │   ├── UndoRedoStack.cs          # 命令模式撤销/重做
│   │   │   └── Shapes/                   # 具体形状实现
│   │   │       ├── RectangleShape.cs
│   │   │       ├── EllipseShape.cs
│   │   │       ├── ArrowShape.cs
│   │   │       ├── FreehandShape.cs
│   │   │       ├── TextShape.cs
│   │   │       ├── MosaicShape.cs
│   │   │       ├── HighlightShape.cs
│   │   │       └── NumberShape.cs
│   │   ├── Encoding/
│   │   │   ├── ImageEncoder.cs           # 编码器抽象基类
│   │   │   ├── HdrFrameData.cs           # HDR 帧数据 DTO
│   │   │   ├── PngEncoder.cs
│   │   │   ├── JpegXlEncoder.cs
│   │   │   ├── JpegLiEncoder.cs          # P/Invoke jpegli
│   │   │   ├── AvifEncoder.cs
│   │   │   ├── WebPEncoder.cs
│   │   │   └── EncoderPipeline.cs        # 编码调度与取消
│   │   ├── ColorManagement/
│   │   │   ├── IccProfileProvider.cs     # WCS API 获取显示器 ICC
│   │   │   ├── ColorSpaceConverter.cs    # lcms2 色域转换
│   │   │   └── NativeMethods.Wcs.cs      # WCS P/Invoke 声明
│   │   ├── Metadata/
│   │   │   ├── MetadataCollector.cs      # 元数据收集
│   │   │   └── ExifWriter.cs             # EXIF/XMP 写入
│   │   ├── Recording/
│   │   │   ├── GifRecorder.cs            # 录制主控
│   │   │   ├── FrameBuffer.cs            # 环形缓冲区
│   │   │   ├── FrameDiffer.cs            # 帧差异检测
│   │   │   └── AnimationEncoder.cs       # 动画编码（WebP/APNG/AVIF/GIF）
│   │   ├── Native/
│   │   │   ├── JpegLiNative.cs           # jpegli P/Invoke
│   │   │   ├── Lcms2Native.cs            # lcms2 P/Invoke
│   │   │   └── User32.cs                 # 窗口信息相关 P/Invoke
│   │   └── TrueToneCap.Core.csproj
│   └── TrueToneCap.App/                  # WinUI 3 应用
│       ├── MainWindow.xaml / .xaml.cs
│       ├── App.xaml / .xaml.cs
│       ├── Views/
│       │   ├── CaptureOverlay.xaml       # 截图选区覆盖层
│       │   ├── AnnotationEditor.xaml     # 标注编辑器
│       │   ├── PreviewWindow.xaml        # 预览窗口
│       │   └── RecordingPanel.xaml       # 录制面板
│       ├── ViewModels/
│       │   ├── CaptureViewModel.cs
│       │   ├── AnnotationViewModel.cs
│       │   ├── PreviewViewModel.cs
│       │   └── RecordingViewModel.cs
│       ├── Services/
│       │   └── DxgiDeviceService.cs      # 管理 D3D11Device 单例
│       └── TrueToneCap.App.csproj
└── README.md
```

### 1.2 核心组件类图

```
┌─────────────────────────────────────────────────────────────────┐
│                        WinUI 3 App Layer                        │
│  MainWindow → CaptureOverlay → AnnotationEditor → PreviewWindow │
│       │              │                  │               │       │
│       ▼              ▼                  ▼               ▼       │
│  ┌─────────┐  ┌──────────┐  ┌───────────────┐  ┌────────────┐ │
│  │CaptureVM│  │AnnotateVM│  │  PreviewVM    │  │RecordingVM │ │
│  └────┬────┘  └─────┬────┘  └───────┬───────┘  └─────┬──────┘ │
└───────┼─────────────┼───────────────┼───────────────┼─────────┘
        │             │               │               │
┌───────┴─────────────┴───────────────┴───────────────┴─────────┐
│                       Core Library                             │
│  ┌──────────────┐  ┌─────────────────┐  ┌───────────────────┐ │
│  │ScreenCapture │  │AnnotationEngine │  │  EncoderPipeline  │ │
│  │ (DXGI Dup)   │  │ (Layer Stack)   │  │  (Format Router)  │ │
│  └──────┬───────┘  └───────┬─────────┘  └────────┬──────────┘ │
│         │                  │                      │            │
│  ┌──────┴───────┐  ┌───────┴─────────┐  ┌────────┴──────────┐ │
│  │CapturedFrame │  │ UndoRedoStack   │  │ ImageEncoder(ABS) │ │
│  │(Float16 Tex) │  │ (Command Pat.)  │  │ ├─PngEncoder      │ │
│  └──────────────┘  └─────────────────┘  │ ├─JpegXlEncoder   │ │
│                                         │ ├─JpegLiEncoder   │ │
│  ┌──────────────┐  ┌─────────────────┐  │ ├─AvifEncoder     │ │
│  │ICCProvider   │  │MetadataCollector│  │ └─WebPEncoder     │ │
│  │(WCS P/Invoke)│  │(User32+Process) │  └───────────────────┘ │
│  └──────────────┘  └─────────────────┘                         │
│  ┌──────────────────────────────────────┐                      │
│  │  GifRecorder → FrameBuffer → AnimEnc │                      │
│  └──────────────────────────────────────┘                      │
└────────────────────────────────────────────────────────────────┘
        │                       │
┌───────┴───────────────────────┴────────────────────────────────┐
│                    Native Interop Layer                         │
│  Vortice.DXGI │ Vortice.Direct3D12 │ Vortice.Direct2D          │
│  Magick.NET   │ lcms2 (P/Invoke)   │ jpegli (P/Invoke)        │
└────────────────────────────────────────────────────────────────┘
```

### 1.3 核心数据流

```
[显示器] ──DXGI DuplicateOutput1──▶ [CapturedFrame (R16G16B16A16_Float)]
                                          │
                    ┌─────────────────────┼─────────────────────┐
                    ▼                     ▼                     ▼
              [HDR→SDR 色调映射]    [标注合成]           [保留原始 HDR]
              (GPU Reinhard/Hable)  (Direct2D 离屏纹理)   (供编码使用)
                    │                     │                     │
                    ▼                     ▼                     ▼
              [CanvasControl      [合并到离屏              [ImageEncoder
               预览渲染]           16-bit 浮点纹理]         编码输出]
                                         │                     │
                                         ▼                     ▼
                                   [最终合成帧] ──────▶ [保存文件]
                                                          │
                                   ┌──────────────────────┤
                                   ▼                      ▼
                              [HDR 路径]            [SDR 路径]
                          (JXL/AVIF/PNG-HDR)    (PNG/JPEG-LI/WebP)
                             嵌入 ICC + CICP        色调映射 → sRGB
```

### 1.4 NuGet 包清单

```xml
<!-- 核心包 -->
<PackageReference Include="Vortice.Windows"           Version="3.10.*" />
<PackageReference Include="Vortice.Direct3D12"         Version="3.10.*" />
<PackageReference Include="Vortice.Direct2D1"          Version="3.10.*" />
<PackageReference Include="Vortice.DXGI"              Version="3.10.*" />
<PackageReference Include="Vortice.Dxc"               Version="3.10.*" />  <!-- HLSL 编译器 -->

<!-- 图像编码 -->
<PackageReference Include="Magick.NET-Q16-HDR-AnyCPU" Version="14.*" />
<PackageReference Include="Magick.NET.Core"            Version="14.*" />

<!-- WinUI 3 / Windows App SDK -->
<PackageReference Include="Microsoft.WindowsAppSDK"   Version="1.6.*" />
<PackageReference Include="Microsoft.Graphics.Win2D"  Version="1.3.*" />

<!-- MVVM 工具 -->
<PackageReference Include="CommunityToolkit.Mvvm"     Version="8.*" />
<PackageReference Include="CommunityToolkit.WinUI"    Version="8.*" />

<!-- 其他 -->
<PackageReference Include="Microsoft.Extensions.Logging" Version="9.*" />
<PackageReference Include="System.Threading.Channels" Version="9.*" />
```

---

## 2. 屏幕捕获与 HDR 管线

### 2.1 DXGI Desktop Duplication 初始化

```csharp
// src/TrueToneCap.Core/Capture/ScreenCapture.cs

using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TrueToneCap.Core.Capture;

public sealed class ScreenCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutput6 _output;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly object _lock = new();

    public ScreenCapture(ID3D11Device device, int outputIndex = 0)
    {
        _device = device;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
        using var adapter = factory.EnumAdapters1(0); // 主显卡

        using var output = adapter.EnumOutputs(outputIndex);
        _output = output.QueryInterface<IDXGIOutput6>();

        var desc = _output.Description;
        OutputRect = new RectI(desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                                desc.DesktopCoordinates.Right, desc.DesktopCoordinates.Bottom);

        // 使用 DuplicateOutput1 以支持 HDR (R16G16B16A16_Float)
        _duplication = _output.DuplicateOutput1(
            _device,
            supportedFormats: [(int)Format.R16G16B16A16_Float]
        );
    }

    public RectI OutputRect { get; }

    /// <summary>
    /// 尝试获取下一帧，超时参数控制阻塞时间。
    /// 返回 null 表示超时（无新帧）。
    /// </summary>
    public CapturedFrame? TryAcquireNextFrame(int timeoutMs = 16)
    {
        lock (_lock)
        {
            try
            {
                var result = _duplication.AcquireNextFrame(
                    timeoutInMilliseconds: timeoutMs,
                    out var frameInfo,
                    out var desktopResource
                );

                if (result.Failure)
                    return null;

                using var acquiredTexture = desktopResource.QueryInterface<ID3D11Texture2D>();

                var desc = acquiredTexture.Description;
                var copyDesc = new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R16G16B16A16_Float,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };

                var stagingTexture = _device.CreateTexture2D(copyDesc);
                var context = _device.ImmediateContext;
                context.CopyResource(stagingTexture, acquiredTexture);

                _duplication.ReleaseFrame();

                return new CapturedFrame(
                    stagingTexture,
                    frameInfo.LastPresentTime,
                    new RectI(
                        frameInfo.DirtyRects[0].Left,
                        frameInfo.DirtyRects[0].Top,
                        frameInfo.DirtyRects[0].Right,
                        frameInfo.DirtyRects[0].Bottom
                    ),
                    frameInfo.PointerPosition
                );
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _output.Dispose();
    }
}
```

### 2.2 CapturedFrame 类

```csharp
// src/TrueToneCap.Core/Capture/CapturedFrame.cs

using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TrueToneCap.Core.Capture;

public sealed class CapturedFrame : IDisposable
{
    private readonly ID3D11Texture2D _texture;

    public CapturedFrame(
        ID3D11Texture2D texture,
        long lastPresentTime,
        RectI dirtyRect,
        RawPoint pointerPosition)
    {
        _texture = texture;
        LastPresentTime = lastPresentTime;
        DirtyRect = dirtyRect;
        PointerPosition = pointerPosition;

        var desc = texture.Description;
        Width = desc.Width;
        Height = desc.Height;
        Format = desc.Format; // Format.R16G16B16A16_Float
    }

    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }
    public long LastPresentTime { get; }
    public RectI DirtyRect { get; }
    public RawPoint PointerPosition { get; }

    public ID3D11Texture2D Texture => _texture;

    /// <summary>
    /// 异步获取浮点像素数据（用于 CPU 端处理，如编码）。
    /// 使用暂存纹理 + 异步映射以避免阻塞 GPU 管线。
    /// </summary>
    public async Task<float[]> GetFloatPixelsAsync(CancellationToken ct = default)
    {
        var device = _texture.Device;
        var context = device.ImmediateContext;

        // 创建 CPU 可读暂存纹理
        var stagingDesc = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(stagingDesc);
        context.CopyResource(staging, _texture);

        // Map 在单独线程执行以避免阻塞 UI
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            var pixelCount = Width * Height * 4;
            var pixels = new float[pixelCount];
            unsafe
            {
                fixed (float* dst = pixels)
                {
                    Buffer.MemoryCopy(
                        mapped.DataPointer.ToPointer(),
                        dst,
                        pixelCount * sizeof(float),
                        pixelCount * sizeof(float)
                    );
                }
            }
            context.Unmap(staging, 0);
            return pixels;
        }, ct);
    }

    public void Dispose() => _texture.Dispose();
}
```

### 2.3 图形捕获互操作（GraphicsCaptureItem）

```csharp
// src/TrueToneCap.Core/Capture/GraphicsCaptureItemInterop.cs

using Windows.Graphics.Capture;
using WinRT; // CsWinRT 互操作

namespace TrueToneCap.Core.Capture;

public static class GraphicsCaptureItemInterop
{
    /// <summary>
    /// 通过窗口句柄创建 GraphicsCaptureItem。
    /// 适用于 WinUI 3 窗口捕获场景。
    /// </summary>
    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        // GraphicsCaptureItem 需要从 IGraphicsCaptureItemInterop 工厂创建
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        interop.CreateForWindow(
            hwnd,
            typeof(GraphicsCaptureItem).GUID,
            out var itemPtr
        );
        return GraphicsCaptureItem.FromAbi(itemPtr);
    }

    /// <summary>
    /// 通过显示器句柄创建 GraphicsCaptureItem（监控器捕获）。
    /// </summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        interop.CreateForMonitor(
            hmonitor,
            typeof(GraphicsCaptureItem).GUID,
            out var itemPtr
        );
        return GraphicsCaptureItem.FromAbi(itemPtr);
    }

    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint hwnd, Guid iid, out nint result);
        void CreateForMonitor(nint hmonitor, Guid iid, out nint result);
    }
}
```

### 2.4 GPU 色调映射着色器

```hlsl
// shaders/ToneMapping.hlsl

// 输入：HDR 纹理 SRV (R16G16B16A16_Float, scRGB 线性光)
// 输出：SDR 纹理 RTV (R8G8B8A8_UNorm, sRGB)

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer ToneMappingParams : register(b0)
{
    uint  ToneMapMode;   // 0 = Reinhard, 1 = Hable
    float Exposure;      // 曝光补偿 (默认 0.0)
    float PaperWhiteNits; // 纸白亮度 nits (SDR 通常 80~200)
    float DisplayMaxNits; // 显示器最大亮度
}

struct PSInput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
struct PSOutput { float4 color : SV_TARGET; };

// ── Reinhard 色调映射 ──
float3 ReinhardToneMap(float3 hdr)
{
    float3 mapped = hdr / (hdr + 1.0f);
    return mapped;
}

// ── Hable (Uncharted 2) Filmic 色调映射 ──
float3 HableToneMap(float3 x)
{
    const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 HableFull(float3 hdr)
{
    float3 curr = HableToneMap(hdr);
    float3 whiteScale = 1.0f / HableToneMap(float3(11.2f, 11.2f, 11.2f));
    return curr * whiteScale;
}

// ── Rec.2020 → Rec.709 色域映射（简化矩阵）──
float3 Rec2020ToRec709(float3 color)
{
    return mul(float3x3(
        1.6605f, -0.5876f, -0.0728f,
       -0.1246f,  1.1329f, -0.0083f,
       -0.0182f, -0.1006f,  1.1187f
    ), color);
}

// ── 线性 → sRGB Gamma ──
float3 LinearToSRGB(float3 c)
{
    return select(c <= 0.0031308f,
                  12.92f * c,
                  1.055f * pow(c, 1.0f / 2.4f) - 0.055f);
}

PSOutput main(PSInput input)
{
    float4 hdrColor = InputTexture.Sample(LinearSampler, input.uv);
    float3 linear = hdrColor.rgb * exp2(Exposure);

    // 色域映射：scRGB (Rec.709 primaries) → 如果需要转 Rec.2020
    // 大多数 HDR 显示器为 Rec.2020 primaries，SDR 为 Rec.709
    // 此处简化：假设输入为 scRGB (Rec.709 线性光)

    float3 mapped;
    if (ToneMapMode == 0)
        mapped = ReinhardToneMap(linear);
    else
        mapped = HableFull(linear);

    // Gamma 编码
    float3 srgb = LinearToSRGB(mapped);

    return PSOutput(float4(srgb, hdrColor.a));
}
```

### 2.5 预览界面数据流

```csharp
// src/TrueToneCap.App/Views/PreviewWindow.xaml.cs (关键片段)

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Vortice.Direct3D11;
using Vortice.Dxc;

public sealed partial class PreviewWindow : Window
{
    private readonly CanvasControl _canvas;
    private CapturedFrame? _currentFrame;
    private ID3D11PixelShader? _toneMapShader;

    // ... 初始化 CanvasControl

    private async void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_currentFrame is null) return;

        using var ds = args.DrawingSession;

        // 将 ID3D11Texture2D 包装为 Win2D CanvasBitmap
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
            sender, _currentFrame.Texture
        );

        // 应用色调映射效果（如果着色器已编译）
        if (_toneMapShader is not null)
        {
            using var effect = new PixelShaderEffect(_toneMapShader.ToBytecode())
            {
                Properties =
                {
                    ["ToneMapMode"] = _selectedMode, // 0 或 1
                    ["Exposure"] = 0.0f,
                },
                Source1 = bitmap,
            };
            ds.DrawImage(effect);
        }
        else
        {
            // 无色调映射时直接绘制（可能会过曝）
            ds.DrawImage(bitmap);
        }
    }
}
```

---

## 3. 标注引擎设计

### 3.1 图层基类

```csharp
// src/TrueToneCap.Core/Annotation/ShapeLayer.cs

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace TrueToneCap.Core.Annotation;

public enum LayerType
{
    Rectangle,
    Ellipse,
    Arrow,
    Freehand,
    Text,
    Mosaic,
    Highlight,
    Number
}

public abstract record ShapeLayerRecord(
    Guid Id,
    LayerType Type,
    Rect Bounds,
    Color StrokeColor,
    float StrokeWidth,
    float Opacity,
    int ZOrder
);

public abstract class ShapeLayer : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public abstract LayerType Type { get; }
    public Rect Bounds { get; protected set; }
    public Color StrokeColor { get; set; } = Colors.Red;
    public Color FillColor { get; set; } = Colors.Transparent;
    public float StrokeWidth { get; set; } = 2.0f;
    public float Opacity { get; set; } = 1.0f;
    public int ZOrder { get; set; }
    public bool IsVisible { get; set; } = true;

    /// <summary>将图层绘制到 CanvasDrawingSession（使用 Direct2D）。</summary>
    public abstract void Draw(CanvasDrawingSession ds);

    /// <summary>测试点是否在图层内（用于命中测试）。</summary>
    public abstract bool HitTest(Vector2 point);

    /// <summary>将图层数据序列化为可持久化记录（用于撤销/重做）。</summary>
    public abstract ShapeLayerRecord ToRecord();

    public virtual void Dispose() { }
}
```

### 3.2 矩形形状实现

```csharp
// src/TrueToneCap.Core/Annotation/Shapes/RectangleShape.cs

namespace TrueToneCap.Core.Annotation.Shapes;

public sealed class RectangleShape : ShapeLayer
{
    public override LayerType Type => LayerType.Rectangle;

    public override void Draw(CanvasDrawingSession ds)
    {
        if (!IsVisible) return;

        if (FillColor.A > 0)
        {
            ds.FillRectangle(Bounds, FillColor);
        }
        ds.DrawRectangle(Bounds, StrokeColor, StrokeWidth);
    }

    public override bool HitTest(Vector2 point)
    {
        // 扩展边界以考虑线宽
        float half = StrokeWidth / 2f;
        var expanded = new Rect(
            Bounds.Left - half,
            Bounds.Top - half,
            Bounds.Width + StrokeWidth,
            Bounds.Height + StrokeWidth
        );
        return expanded.Contains(point.X, point.Y);
        // 更精确的判断应检查点到矩形边缘的距离
    }

    public override ShapeLayerRecord ToRecord() =>
        new(Id, Type, Bounds, StrokeColor, StrokeWidth, Opacity, ZOrder);
}
```

### 3.3 命令模式——撤销/重做

```csharp
// src/TrueToneCap.Core/Annotation/UndoRedoStack.cs

namespace TrueToneCap.Core.Annotation;

public interface IAnnotationCommand
{
    void Execute(AnnotationEngine engine);
    void Undo(AnnotationEngine engine);
    string Description { get; }
}

public sealed class AddLayerCommand(ShapeLayer layer) : IAnnotationCommand
{
    public string Description => $"添加 {layer.Type} 图层";

    public void Execute(AnnotationEngine engine) => engine.AddLayerDirect(layer);

    public void Undo(AnnotationEngine engine) => engine.RemoveLayerDirect(layer.Id);
}

public sealed class RemoveLayerCommand(ShapeLayer layer) : IAnnotationCommand
{
    public string Description => $"删除 {layer.Type} 图层";

    public void Execute(AnnotationEngine engine) => engine.RemoveLayerDirect(layer.Id);

    public void Undo(AnnotationEngine engine) => engine.AddLayerDirect(layer);
}

public sealed class ModifyLayerCommand(
    ShapeLayer original,
    ShapeLayerRecord modified,
    ShapeLayerRecord newRecord) : IAnnotationCommand
{
    public string Description => $"修改 {original.Type} 图层";

    public void Execute(AnnotationEngine engine) => engine.ApplyRecord(newRecord);

    public void Undo(AnnotationEngine engine) => engine.ApplyRecord(modified);
}

public sealed class UndoRedoStack
{
    private readonly Stack<IAnnotationCommand> _undoStack = new(capacity: 256);
    private readonly Stack<IAnnotationCommand> _redoStack = new(capacity: 256);

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Execute(IAnnotationCommand command, AnnotationEngine engine)
    {
        command.Execute(engine);
        _undoStack.Push(command);
        _redoStack.Clear(); // 新操作使重做历史失效
    }

    public void Undo(AnnotationEngine engine)
    {
        if (!_undoStack.TryPop(out var cmd)) return;
        cmd.Undo(engine);
        _redoStack.Push(cmd);
    }

    public void Redo(AnnotationEngine engine)
    {
        if (!_redoStack.TryPop(out var cmd)) return;
        cmd.Execute(engine);
        _undoStack.Push(cmd);
    }
}
```

### 3.4 标注引擎核心

```csharp
// src/TrueToneCap.Core/Annotation/AnnotationEngine.cs

using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace TrueToneCap.Core.Annotation;

public sealed class AnnotationEngine
{
    private readonly ConcurrentDictionary<Guid, ShapeLayer> _layers = new();
    private readonly UndoRedoStack _undoRedo = new();
    private CanvasRenderTarget? _offscreenTarget;

    public IReadOnlyCollection<ShapeLayer> Layers => _layers.Values;
    public UndoRedoStack UndoRedo => _undoRedo;

    /// <summary>添加图层（通过命令模式，支持撤销）。</summary>
    public void AddLayer(ShapeLayer layer)
        => _undoRedo.Execute(new AddLayerCommand(layer), this);

    /// <summary>移除图层。核心将其标记为不可见（保留数据便于重做）。</summary>
    public void RemoveLayer(Guid id)
    {
        if (_layers.TryGetValue(id, out var layer))
            _undoRedo.Execute(new RemoveLayerCommand(layer), this);
    }

    // 内部直接操作方法（供命令调用，不走撤销栈）
    internal void AddLayerDirect(ShapeLayer layer) => _layers[layer.Id] = layer;
    internal void RemoveLayerDirect(Guid id) => _layers.TryRemove(id, out _);
    internal void ApplyRecord(ShapeLayerRecord record)
    {
        if (_layers.TryGetValue(record.Id, out var layer))
        {
            layer.Bounds = record.Bounds;
            layer.StrokeColor = record.StrokeColor;
            layer.StrokeWidth = record.StrokeWidth;
            layer.Opacity = record.Opacity;
            layer.ZOrder = record.ZOrder;
        }
    }

    /// <summary>
    /// 将所有可见图层渲染到离屏纹理。
    /// 目标格式为 R16G16B16A16_Float 以保持 HDR 精度。
    /// </summary>
    public CanvasRenderTarget CompositeToOffscreen(
        ICanvasResourceCreator resourceCreator,
        int width,
        int height,
        CanvasBitmap background)
    {
        _offscreenTarget ??= new CanvasRenderTarget(
            resourceCreator,
            width, height,
            96.0f, // DPI
            Microsoft.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float,
            CanvasAlphaMode.Premultiplied
        );

        using var ds = _offscreenTarget.CreateDrawingSession();
        ds.Clear(Windows.UI.Colors.Transparent);

        // 先绘制背景截图
        ds.DrawImage(background);

        // 按 Z-order 排序并绘制所有可见图层
        foreach (var layer in _layers.Values
                     .Where(l => l.IsVisible)
                     .OrderBy(l => l.ZOrder))
        {
            layer.Draw(ds);
        }

        return _offscreenTarget;
    }

    public void Dispose()
    {
        _offscreenTarget?.Dispose();
        foreach (var layer in _layers.Values)
            layer.Dispose();
    }
}
```

---

## 4. 多格式编码器

### 4.1 编码器抽象基类

```csharp
// src/TrueToneCap.Core/Encoding/ImageEncoder.cs

using ImageMagick;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// HDR 帧数据 DTO，包含浮点像素和 ICC 配置。
/// </summary>
public sealed record HdrFrameData(
    float[] LinearRGBA,       // 线性光 scRGB 浮点像素 (R,G,B,A 交错)
    int Width,
    int Height,
    byte[]? IccProfile,       // 显示器 ICC 配置文件字节
    Dictionary<string, string>? ExifMetadata,
    string? XmpMetadata
)
{
    public int PixelCount => Width * Height;
    public int ChannelCount => 4;
}

public record EncodeProgress(int PercentComplete, string CurrentStage);

public abstract class ImageEncoder
{
    public abstract string FormatName { get; }
    public abstract bool SupportsHDR { get; }

    /// <summary>
    /// 异步编码，支持进度报告和取消。
    /// 返回编码后的字节数组（可直接写入文件）。
    /// </summary>
    public abstract Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// 将线性 scRGB 浮点像素转换为 MagickImage。
    /// 如果格式不支持 HDR，自动应用 SDR 转换。
    /// </summary>
    protected MagickImage CreateMagickImage(HdrFrameData data, bool forceSdr)
    {
        if (!SupportsHDR || forceSdr)
        {
            // SDR 路径：色调映射 + 转换为 sRGB 8-bit
            return CreateSdrImage(data);
        }

        // HDR 路径：保留浮点数据
        var settings = new PixelReadSettings(
            data.Width, data.Height,
            StorageType.Float,
            PixelMapping.RGBA
        );
        var pixelBytes = new byte[data.PixelCount * 4 * sizeof(float)];
        Buffer.BlockCopy(data.LinearRGBA, 0, pixelBytes, 0, pixelBytes.Length);

        var image = new MagickImage(pixelBytes, settings);
        image.ColorSpace = ColorSpace.scRGB;
        image.ColorType = ColorType.TrueColorAlpha;

        // 嵌入 ICC
        if (data.IccProfile is { Length: > 0 })
        {
            image.SetProfile(new ImageProfile(data.IccProfile));
        }

        return image;
    }

    protected static MagickImage CreateSdrImage(HdrFrameData data)
    {
        // 简化 SDR 转换：线性→sRGB tone mapping + gamma
        var srgbPixels = new byte[data.PixelCount * 4];
        for (int i = 0; i < data.PixelCount; i++)
        {
            int offset = i * 4;
            float r = Math.Clamp(LinearToSRGB(ToneMapReinhard(data.LinearRGBA[offset])), 0, 1);
            float g = Math.Clamp(LinearToSRGB(ToneMapReinhard(data.LinearRGBA[offset + 1])), 0, 1);
            float b = Math.Clamp(LinearToSRGB(ToneMapReinhard(data.LinearRGBA[offset + 2])), 0, 1);
            float a = Math.Clamp(data.LinearRGBA[offset + 3], 0, 1);

            srgbPixels[offset] = (byte)(r * 255.0f);
            srgbPixels[offset + 1] = (byte)(g * 255.0f);
            srgbPixels[offset + 2] = (byte)(b * 255.0f);
            srgbPixels[offset + 3] = (byte)(a * 255.0f);
        }

        var settings = new PixelReadSettings(
            data.Width, data.Height,
            StorageType.Char,
            PixelMapping.RGBA
        );
        return new MagickImage(srgbPixels, settings);
    }

    private static float ToneMapReinhard(float x) => x / (x + 1.0f);
    private static float LinearToSRGB(float c) =>
        c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
}
```

### 4.2 PNG HDR 编码器

```csharp
// src/TrueToneCap.Core/Encoding/PngEncoder.cs

using ImageMagick;

namespace TrueToneCap.Core.Encoding;

public sealed class PngEncoder : ImageEncoder
{
    public override string FormatName => "PNG";
    public override bool SupportsHDR => true; // HDR PNG 草案

    public override async Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(10, "创建 PNG 图像..."));

            using var image = CreateMagickImage(data, forceSdr: false);

            // HDR PNG: 16-bit PQ 编码
            image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "16");
            image.Settings.SetDefine(MagickFormat.Png, "color-type", "6"); // RGBA

            // 嵌入 cICP 块（ITU-T T.35 H.273）
            if (data.IccProfile is { Length: > 0 })
            {
                image.SetProfile(new ImageProfile("icc", data.IccProfile));
            }

            // 设置色彩空间元数据
            image.SetAttribute("png:cICP", "9/16/0"); // Rec.2020 primaries, PQ transfer

            progress?.Report(new(90, "写入 PNG 流..."));
            image.Format = MagickFormat.Png48; // 48-bit PNG
            ct.ThrowIfCancellationRequested();

            return image.ToByteArray();
        }, ct);
    }
}
```

### 4.3 JPEG XL 编码器

```csharp
// src/TrueToneCap.Core/Encoding/JpegXlEncoder.cs

using ImageMagick;
// 注意：需要 Magick.NET 的 JXL 编解码器集成

namespace TrueToneCap.Core.Encoding;

public sealed class JpegXlEncoder : ImageEncoder
{
    public override string FormatName => "JPEG XL";
    public override bool SupportsHDR => true;

    public override async Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(new(5, "创建 JPEG XL HDR 图像..."));
            using var image = CreateMagickImage(data, forceSdr: false);

            // JXL 原生支持浮点 HDR
            image.Settings.SetDefine(MagickFormat.Jxl, "effort", "7");     // 编码质量
            image.Settings.SetDefine(MagickFormat.Jxl, "lossless", "false");

            // 色彩空间：线性 Rec.2020 / scRGB
            image.SetAttribute("jxl:color-space", "RGB");
            image.SetAttribute("jxl:transfer-function", "Linear");

            if (data.IccProfile is { Length: > 0 })
            {
                image.SetProfile(new ImageProfile("icc", data.IccProfile));
            }

            // 写入 XMP 元数据
            if (data.XmpMetadata is not null)
            {
                image.SetProfile(new ImageProfile("xmp", System.Text.Encoding.UTF8.GetBytes(data.XmpMetadata)));
            }

            image.Format = MagickFormat.Jxl;
            progress?.Report(new(90, "编码 JXL..."));

            return image.ToByteArray();
        }, ct);
    }
}
```

### 4.4 JPEG LI 编码器（P/Invoke jpegli）

```csharp
// src/TrueToneCap.Core/Encoding/JpegLiEncoder.cs

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// P/Invoke 调用 Google jpegli 库进行高质量 JPEG 编码。
/// jpegli 提供比 libjpeg-turbo 更好的压缩率和视觉质量。
/// </summary>
public sealed unsafe class JpegLiEncoder : ImageEncoder
{
    public override string FormatName => "JPEG (LI)";
    public override bool SupportsHDR => false; // 传统 JPEG 不支持 HDR

    public override async Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        // JPEG LI 仅支持 8-bit sRGB
        progress?.Report(new(10, "转换为 sRGB..."));
        using var sdrImage = CreateSdrImage(data);
        progress?.Report(new(40, "获取像素缓冲区..."));

        // 获取 8-bit sRGB 交错像素
        var pixelBytes = sdrImage.ToByteArray(MagickFormat.Rgba);
        // MagickImage.ToByteArray 返回原始像素，重构为 RGB
        using var rgbImage = new MagickImage(pixelBytes!, new PixelReadSettings(
            data.Width, data.Height, StorageType.Char, "RGB"));
        var rgbBytes = rgbImage.ToByteArray(MagickFormat.Rgb);

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(60, "调用 jpegli 编码器..."));

            fixed (byte* pRgb = rgbBytes!)
            {
                return JpegLiNative.EncodeRgb(
                    pRgb,
                    data.Width,
                    data.Height,
                    quality: 90,
                    chromaSubsampling: 2 // 4:2:0
                );
            }
        }, ct);
    }
}
```

```csharp
// src/TrueToneCap.Core/Native/JpegLiNative.cs

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.Native;

public static unsafe class JpegLiNative
{
    // jpegli 通常打包在 libjxl.dll 中
    private const string JpegLiDll = "jxl.dll"; // 或 "libjxl.dll"

    /// <summary>
    /// 使用 jpegli 编码 RGB 8-bit 交错像素数据。
    /// 返回已编码的 JPEG 字节流。
    /// </summary>
    [DllImport(JpegLiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JpegliEncodeRgb(
        byte* input,
        int width,
        int height,
        int quality,
        int chromaSubsampling,
        byte** output,
        out nuint outputSize
    );

    public static byte[] EncodeRgb(
        byte* rgbPixels,
        int width,
        int height,
        int quality = 90,
        int chromaSubsampling = 2)
    {
        byte* outputPtr = null;
        nuint outputSize;

        int result = JpegliEncodeRgb(
            rgbPixels, width, height,
            quality, chromaSubsampling,
            &outputPtr, out outputSize
        );

        if (result != 0 || outputPtr == null)
            throw new InvalidOperationException($"jpegli 编码失败，错误码: {result}");

        var buffer = new byte[(int)outputSize];
        Marshal.Copy((nint)outputPtr, buffer, 0, (int)outputSize);

        // 释放 jpegli 分配的内存（调用 libjxl 的 free）
        // 注意：这里需要根据 jpegli 的实际内存管理机制处理
        Marshal.FreeHGlobal((nint)outputPtr);

        return buffer;
    }

    // ── 备用方案：如果 jpegli 无直接 C API，可通过 libjxl 的 JxlEncoder 间接使用 ──
    //
    // 实际项目中，jpegli 可能通过以下路径集成：
    // 1. 编译 libjxl 时启用 JPEGLI_ENCODE_JPEG 并使用 C API 封装
    // 2. 使用 Magick.NET 的实验性 JPEG LI 集成（如果版本支持）
    // 3. 通过 Process 调用外部 jpegli CLI 工具（性能较差，不推荐）
    //
    // 推荐方案：编写一个轻量级 C++/CLI 桥接 DLL，封装 jpegli 的 C++ API：
    //
    // // JpegLiBridge.cpp (C++/CLI)
    // #include <jxl/encode.h>
    // #include <jxl/jpegli_encode.h>
    //
    // public ref class JpegLiBridge {
    // public:
    //     static array<byte>^ Encode(array<byte>^ rgb, int w, int h, int q) { ... }
    // };
    //
    // 详见 docs/jpegli-integration.md
}
```

### 4.5 AVIF 编码器

```csharp
// src/TrueToneCap.Core/Encoding/AvifEncoder.cs

using ImageMagick;

namespace TrueToneCap.Core.Encoding;

public sealed class AvifEncoder : ImageEncoder
{
    public override string FormatName => "AVIF";
    public override bool SupportsHDR => true;

    public override async Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(new(10, "创建 AVIF HDR 图像..."));
            using var image = CreateMagickImage(data, forceSdr: false);

            // AVIF HDR 设置
            image.Settings.SetDefine(MagickFormat.Avif, "depth", "12");    // 12-bit
            image.Settings.SetDefine(MagickFormat.Avif, "lossless", "false");
            image.Settings.SetDefine(MagickFormat.Avif, "speed", "6");    // 编码速度（0=最慢/最优）

            // 色彩空间信息（将通过 CICP 嵌入）
            image.SetAttribute("avif:colour.primaries", "9");   // Rec.2020
            image.SetAttribute("avif:transfer", "16");           // PQ (SMPTE ST 2084)
            image.SetAttribute("avif:matrix.coefficients", "0"); // RGB

            // ICC 配置文件
            if (data.IccProfile is { Length: > 0 })
            {
                image.SetProfile(new ImageProfile("icc", data.IccProfile));
            }

            // EXIF 元数据
            if (data.ExifMetadata is not null)
            {
                foreach (var (key, value) in data.ExifMetadata)
                {
                    image.SetAttribute($"exif:{key}", value);
                }
            }

            image.Format = MagickFormat.Avif;
            progress?.Report(new(90, "编码 AVIF..."));

            return image.ToByteArray();
        }, ct);
    }
}
```

### 4.6 WebP 编码器

```csharp
// src/TrueToneCap.Core/Encoding/WebPEncoder.cs

using ImageMagick;

namespace TrueToneCap.Core.Encoding;

public sealed class WebPEncoder : ImageEncoder
{
    public override string FormatName => "WebP";
    public override bool SupportsHDR => false;

    public override async Task<byte[]> EncodeAsync(
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(new(10, "创建 WebP SDR 图像..."));
            using var image = CreateSdrImage(data); // 强制 SDR

            image.Format = MagickFormat.WebP;
            image.Settings.Quality = 92;
            image.Settings.SetDefine(MagickFormat.WebP, "method", "6"); // 最优压缩
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", "false");

            // EXIF 元数据
            if (data.ExifMetadata is not null)
            {
                foreach (var (key, value) in data.ExifMetadata)
                {
                    image.SetAttribute($"exif:{key}", value);
                }
            }

            progress?.Report(new(90, "编码 WebP..."));
            return image.ToByteArray();
        }, ct);
    }
}
```

### 4.7 编码管道（调度与取消）

```csharp
// src/TrueToneCap.Core/Encoding/EncoderPipeline.cs

using System.Collections.Concurrent;

namespace TrueToneCap.Core.Encoding;

public sealed class EncoderPipeline
{
    private readonly Dictionary<string, ImageEncoder> _encoders = new()
    {
        ["png"] = new PngEncoder(),
        ["jxl"] = new JpegXlEncoder(),
        ["jpegli"] = new JpegLiEncoder(),
        ["avif"] = new AvifEncoder(),
        ["webp"] = new WebPEncoder(),
    };

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeEncodes = new();

    public ImageEncoder GetEncoder(string format)
    {
        if (!_encoders.TryGetValue(format.ToLowerInvariant(), out var encoder))
            throw new NotSupportedException($"不支持的格式: {format}");
        return encoder;
    }

    /// <summary>
    /// 异步编码并返回结果，支持取消。
    /// </summary>
    public async Task<byte[]> EncodeAsync(
        string format,
        HdrFrameData data,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var encoder = GetEncoder(format);
        var encodeId = Guid.NewGuid();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeEncodes[encodeId] = linkedCts;

        try
        {
            var result = await encoder.EncodeAsync(data, progress, linkedCts.Token);
            return result;
        }
        finally
        {
            _activeEncodes.TryRemove(encodeId, out _);
        }
    }

    /// <summary>取消所有进行中的编码。</summary>
    public void CancelAll()
    {
        foreach (var (id, cts) in _activeEncodes)
        {
            try { cts.Cancel(); } catch { }
        }
        _activeEncodes.Clear();
    }
}
```

---

## 5. 色彩管理与 ICC 嵌入

### 5.1 获取显示器 ICC 配置文件

```csharp
// src/TrueToneCap.Core/ColorManagement/IccProfileProvider.cs

using System.Runtime.InteropServices;
using Windows.Win32; // 或手动 P/Invoke

namespace TrueToneCap.Core.ColorManagement;

public static class IccProfileProvider
{
    /// <summary>
    /// 获取当前显示器默认 ICC 配置文件内容。
    /// </summary>
    public static byte[]? GetMonitorIccProfile(nint hmonitor)
    {
        // 通过 WCS API 获取显示器关联的 ICC 配置文件
        // 方法1: WcsGetDefaultColorProfile (Windows 10 1903+)
        // 方法2: GetICMProfile (传统 GDI API，兼容性好)

        // 方法2: 使用 GetICMProfile（更简单可靠）
        using var dc = GetDC(hmonitor);
        if (dc == nint.Zero) return null;

        try
        {
            uint size = 0;
            GetICMProfile(dc, ref size, null);

            if (size == 0) return null;

            var buffer = new char[size];
            if (!GetICMProfile(dc, ref size, buffer))
                return null;

            var profilePath = new string(buffer, 0, (int)size - 1); // 去除 null 终止符
            return File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null;
        }
        finally
        {
            ReleaseDC(hmonitor, dc);
        }
    }

    // ── P/Invoke 声明 ──

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetICMProfile(nint hdc, ref uint size, char[]? filename);

    // ── WCS API（Windows 10 1903+ 推荐方式）──

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfile(
        ColorProfileScope scope,
        [MarshalAs(UnmanagedType.LPWStr)] string? deviceName,
        ColorProfileType profileType,
        ColorProfileSubType profileSubType,
        uint dwFlags,
        [MarshalAs(UnmanagedType.LPWStr)] out string profileName,
        out uint profileNameSize
    );

    private enum ColorProfileScope : uint { SystemWide = 0, PerUser = 1 }
    private enum ColorProfileType : uint { Device = 0x00000001, Workspace = 0x00000002 }
    private enum ColorProfileSubType : uint { DisplayDefault = 0x00000001 }
}
```

### 5.2 lcms2 色域转换

```csharp
// src/TrueToneCap.Core/ColorManagement/ColorSpaceConverter.cs

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>
/// 通过 P/Invoke lcms2 实现色域转换流水线。
/// </summary>
public sealed unsafe class ColorSpaceConverter : IDisposable
{
    private readonly nint _transform; // cmsHTRANSFORM
    private readonly int _pixelCount;

    public ColorSpaceConverter(byte[] srcIcc, byte[] dstIcc, int pixelCount)
    {
        _pixelCount = pixelCount;

        fixed (byte* pSrcIcc = srcIcc, pDstIcc = dstIcc)
        {
            var srcProfile = cmsOpenProfileFromMem(pSrcIcc, (uint)srcIcc.Length);
            var dstProfile = cmsOpenProfileFromMem(pDstIcc, (uint)dstIcc.Length);

            if (srcProfile == nint.Zero || dstProfile == nint.Zero)
                throw new InvalidOperationException("无法加载 ICC 配置文件");

            // 浮点 RGBA 格式：TYPE_RGBA_FLT
            _transform = cmsCreateTransform(
                srcProfile,
                Lcms2Native.TYPE_RGBA_FLT,
                dstProfile,
                Lcms2Native.TYPE_RGBA_FLT,
                Lcms2Native.INTENT_PERCEPTUAL,
                cmsUInt32Number: 0
            );

            cmsCloseProfile(srcProfile);
            cmsCloseProfile(dstProfile);
        }

        if (_transform == nint.Zero)
            throw new InvalidOperationException("无法创建色彩转换变换");
    }

    /// <summary>scRGB 线性光 → sRGB（通过 ICC 配置文件）</summary>
    public void ConvertScRgbToSRgb(float[] pixels)
    {
        fixed (float* p = pixels)
        {
            cmsDoTransform(_transform, p, p, (uint)_pixelCount);
        }
    }

    /// <summary>scRGB 线性光 → PQ (SMPTE ST 2084) 用于 HDR 输出</summary>
    public void ConvertScRgbToPQ(float[] pixels)
    {
        // PQ 转换通常需要通过特定的 ICC 配置文件（如 Rec.2020-PQ）
        // 或直接在着色器中执行 ST 2084 EOTF
        fixed (float* p = pixels)
        {
            cmsDoTransform(_transform, p, p, (uint)_pixelCount);
        }
    }

    public void Dispose() => cmsDeleteTransform(_transform);

    private const string Lcms2Dll = "liblcms2-2.dll";

    [DllImport(Lcms2Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint cmsOpenProfileFromMem(void* data, uint size);

    [DllImport(Lcms2Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint cmsCreateTransform(
        nint inputProfile, uint inputFormat,
        nint outputProfile, uint outputFormat,
        uint intent, uint flags
    );

    [DllImport(Lcms2Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsDoTransform(nint transform, void* input, void* output, uint count);

    [DllImport(Lcms2Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsCloseProfile(nint profile);

    [DllImport(Lcms2Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void cmsDeleteTransform(nint transform);
}

// 辅助常量
internal static class Lcms2Native
{
    // lcms2 像素格式定义
    public const uint TYPE_RGBA_FLT = (4 << 0) | (1 << 3) | (4 << 7);  // FLOAT_SH(1) | COLORSPACE_SH(PT_RGB)
    public const uint TYPE_RGBA_8 = (4 << 0) | (0 << 3) | (4 << 7);

    // 渲染意图
    public const uint INTENT_PERCEPTUAL = 0;
    public const uint INTENT_RELATIVE_COLORIMETRIC = 1;
    public const uint INTENT_SATURATION = 2;
    public const uint INTENT_ABSOLUTE_COLORIMETRIC = 3;
}
```

### 5.3 ICC 嵌入示例（Magick.NET）

```csharp
// 在编码器中嵌入 ICC：

// PNG / JXL / AVIF 编码时
if (iccProfile is { Length: > 0 })
{
    // Magick.NET 支持嵌入 ICC 配置文件
    var profile = new ImageProfile("icc", iccProfile);
    image.SetProfile(profile);

    // 也可以设置色彩空间描述（会被写入文件头）
    image.SetAttribute("exif:ColorSpace", "1"); // sRGB=1, Uncalibrated=65535
}

// 对于 JPEG XL，设置原生色彩编码
image.SetAttribute("jxl:color.primaries", "9");     // Rec.2020
image.SetAttribute("jxl:white.point", "1");           // D65
image.SetAttribute("jxl:transfer.function", "16");    // SMPTE ST 2084 (PQ)
```

---

## 6. 元数据收集与写入

### 6.1 元数据收集器

```csharp
// src/TrueToneCap.Core/Metadata/MetadataCollector.cs

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrueToneCap.Core.Metadata;

public sealed record ScreenCaptureMetadata(
    DateTime TimestampUtc,
    string? ForegroundWindowTitle,
    string? ForegroundProcessName,
    int ForegroundProcessId,
    string? MonitorName,
    int MonitorWidth,
    int MonitorHeight,
    bool IsHdrEnabled,
    int CaptureX,
    int CaptureY,
    int CaptureWidth,
    int CaptureHeight,
    int CursorX,
    int CursorY,
    string? CursorType
);

public static class MetadataCollector
{
    /// <summary>收集当前截图上下文的所有元数据。</summary>
    public static ScreenCaptureMetadata Collect(
        nint hmonitor,
        Rect captureRect,
        Point cursorPos)
    {
        var fgHwnd = GetForegroundWindow();

        // 获取前台窗口标题
        var titleBuffer = new char[256];
        GetWindowText(fgHwnd, titleBuffer, titleBuffer.Length);
        var title = new string(titleBuffer).TrimEnd('\0');

        // 获取进程信息
        GetWindowThreadProcessId(fgHwnd, out var pid);
        string? processName = null;
        try { processName = Process.GetProcessById((int)pid).ProcessName; }
        catch { /* 进程可能已退出 */ }

        // 获取显示器信息
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        GetMonitorInfoW(hmonitor, ref monitorInfo);

        // 查询 HDR 状态
        bool isHdr = IsHdrEnabled(hmonitor);

        // 获取光标类型
        string? cursorType = GetCursorTypeName();

        return new ScreenCaptureMetadata(
            TimestampUtc: DateTime.UtcNow,
            ForegroundWindowTitle: title,
            ForegroundProcessName: processName,
            ForegroundProcessId: (int)pid,
            MonitorName: monitorInfo.szDevice,
            MonitorWidth: monitorInfo.rcMonitor.Width,
            MonitorHeight: monitorInfo.rcMonitor.Height,
            IsHdrEnabled: isHdr,
            CaptureX: captureRect.X,
            CaptureY: captureRect.Y,
            CaptureWidth: captureRect.Width,
            CaptureHeight: captureRect.Height,
            CursorX: cursorPos.X,
            CursorY: cursorPos.Y,
            CursorType: cursorType
        );
    }

    private static bool IsHdrEnabled(nint hmonitor)
    {
        // 使用 DXGI 查询 HDR 状态
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory7>();
        for (int i = 0; ; i++)
        {
            var result = factory.EnumAdapters1(i, out var adapter);
            if (result.Failure) break;
            using (adapter)
            {
                for (int j = 0; ; j++)
                {
                    var outResult = adapter.EnumOutputs(j, out var output);
                    if (outResult.Failure) break;
                    using (output)
                    {
                        var desc = output.Description;
                        if (desc.Monitor == hmonitor)
                        {
                            var output6 = output.QueryInterface<Vortice.DXGI.IDXGIOutput6>();
                            var desc1 = output6.Description1;
                            return desc1.ColorSpace == Vortice.DXGI.ColorSpaceType.RgbFullG2084NoneP2020
                                || desc1.ColorSpace == Vortice.DXGI.ColorSpaceType.RgbFullG10NoneP709;
                        }
                    }
                }
            }
        }
        return false;
    }

    // ── P/Invoke ──

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RECT(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private static string? GetCursorTypeName()
    {
        var info = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info)) return null;
        return info.hCursor switch
        {
            65539 => "Arrow",
            65541 => "IBeam",
            65543 => "Wait",
            65545 => "Cross",
            65547 => "UpArrow",
            65557 => "Hand",
            65559 => "SizeAll",
            65561 => "SizeNWSE",
            65563 => "SizeNESW",
            65565 => "SizeWE",
            65567 => "SizeNS",
            _ => "Custom"
        };
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
}
```

### 6.2 EXIF/XMP 写入

```csharp
// src/TrueToneCap.Core/Metadata/ExifWriter.cs

using System.Text;
using System.Xml.Linq;
using ImageMagick;

namespace TrueToneCap.Core.Metadata;

public static class ExifWriter
{
    /// <summary>将收集的元数据写入 MagickImage 的 EXIF 和 XMP 属性。</summary>
    public static void Write(MagickImage image, ScreenCaptureMetadata meta)
    {
        // ── EXIF 标签 ──
        image.SetAttribute("exif:DateTimeOriginal",
            meta.TimestampUtc.ToString("yyyy:MM:dd HH:mm:ss"));

        image.SetAttribute("exif:Software", "TrueToneCap/1.0");

        if (meta.ForegroundWindowTitle is not null)
        {
            image.SetAttribute("exif:ImageDescription", meta.ForegroundWindowTitle);
        }

        // 自定义 EXIF 标签（MakerNote 区域）
        image.SetAttribute("exif:MakerNote",
            $"Process:{meta.ForegroundProcessName}|PID:{meta.ForegroundProcessId}");

        // ── XMP 元数据（更丰富的结构）──
        var xmp = BuildXmp(meta);
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);
        image.SetProfile(new ImageProfile("xmp", xmpBytes));

        // ── PNG 文本块（用于 PNG 格式）──
        image.SetAttribute("png:Software", "TrueToneCap/1.0");
        image.SetAttribute("png:Description", meta.ForegroundWindowTitle ?? "");
        image.SetAttribute("png:TimeStamp", meta.TimestampUtc.ToString("O"));
    }

    private static string BuildXmp(ScreenCaptureMetadata meta)
    {
        var nsXmp = "http://ns.adobe.com/xap/1.0/";
        var nsTtc = "http://trueonecap.dev/ns/1.0/";

        var doc = new XDocument(
            new XElement(XName.Get("xmpmeta", nsXmp),
                new XElement(XName.Get("RDF", "http://www.w3.org/1999/02/22-rdf-syntax-ns#"),
                    new XElement(XName.Get("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#"),
                        new XAttribute(XName.Get("about", "http://www.w3.org/1999/02/22-rdf-syntax-ns#"), ""),

                        // 时间戳（精确到微秒）
                        new XElement(XName.Get("CaptureTime", nsTtc),
                            meta.TimestampUtc.ToString("O")),

                        // 窗口信息
                        new XElement(XName.Get("ForegroundWindow", nsTtc),
                            new XAttribute("title", meta.ForegroundWindowTitle ?? ""),
                            new XAttribute("process", meta.ForegroundProcessName ?? ""),
                            new XAttribute("pid", meta.ForegroundProcessId)),

                        // 显示器信息
                        new XElement(XName.Get("Monitor", nsTtc),
                            new XAttribute("name", meta.MonitorName ?? ""),
                            new XAttribute("width", meta.MonitorWidth),
                            new XAttribute("height", meta.MonitorHeight),
                            new XAttribute("hdrEnabled", meta.IsHdrEnabled)),

                        // 截图区域
                        new XElement(XName.Get("CaptureRect", nsTtc),
                            new XAttribute("x", meta.CaptureX),
                            new XAttribute("y", meta.CaptureY),
                            new XAttribute("width", meta.CaptureWidth),
                            new XAttribute("height", meta.CaptureHeight)),

                        // 光标信息
                        new XElement(XName.Get("Cursor", nsTtc),
                            new XAttribute("x", meta.CursorX),
                            new XAttribute("y", meta.CursorY),
                            new XAttribute("type", meta.CursorType ?? "unknown"))
                    )
                )
            )
        );

        return doc.Declaration?.ToString() + Environment.NewLine + doc.ToString();
    }
}
```

---

## 7. 动图录制模块

### 7.1 环形帧缓冲区

```csharp
// src/TrueToneCap.Core/Recording/FrameBuffer.cs

using System.Threading.Channels;

namespace TrueToneCap.Core.Recording;

public readonly record struct RecordedFrame(
    float[] Pixels,
    int Width,
    int Height,
    long TimestampQpc // QueryPerformanceCounter 时间戳
);

/// <summary>
/// 基于 BoundedChannel 的环形帧缓冲区，线程安全。
/// 当缓冲区满时，丢弃最旧的帧以避免内存无限增长。
/// </summary>
public sealed class FrameBuffer
{
    private readonly Channel<RecordedFrame> _channel;

    public FrameBuffer(int capacity = 300) // 10秒 @ 30fps
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        };
        _channel = Channel.CreateBounded<RecordedFrame>(options);
    }

    public ValueTask WriteAsync(RecordedFrame frame, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(frame, ct);

    public IAsyncEnumerable<RecordedFrame> ReadAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);

    public int Count => _channel.Reader.Count;
}
```

### 7.2 帧差异检测

```csharp
// src/TrueToneCap.Core/Recording/FrameDiffer.cs

namespace TrueToneCap.Core.Recording;

public sealed class FrameDiffer
{
    private float[]? _previous;
    private readonly float _threshold;
    private readonly int _skipCheckModulo; // 每 N 帧做一次完整 diff

    /// <param name="threshold">差异阈值（0~1），超过触发写入。</param>
    /// <param name="skipCheckModulo">每隔多少帧做完整检查（1=每帧检查）。</param>
    public FrameDiffer(float threshold = 0.005f, int skipCheckModulo = 2)
    {
        _threshold = threshold;
        _skipCheckModulo = Math.Max(1, skipCheckModulo);
    }

    /// <summary>返回 true 表示帧有明显变化，应写入。</summary>
    public bool IsSignificantChange(float[] current, int pixelCount, int frameIndex)
    {
        if (_previous is null || _previous.Length != current.Length)
        {
            _previous = current.ToArray();
            return true; // 第一帧总是写入
        }

        // 每 N 帧抽样检查以提升性能
        if (frameIndex % _skipCheckModulo != 0 && frameIndex > 2)
            return true; // 不检查时默认保留

        float diff = ComputeDiff(_previous, current, pixelCount);
        return diff > _threshold;
    }

    /// <summary>更新参考帧。</summary>
    public void UpdateReference(float[] frame)
    {
        _previous = frame.ToArray();
    }

    /// <summary>计算两帧的均方误差（MSE），使用抽样加速。</summary>
    private static float ComputeDiff(float[] a, float[] b, int pixelCount)
    {
        float sum = 0;
        int step = 4; // 每 4 个像素抽样 1 个

        for (int i = 0; i < pixelCount * 4; i += 16) // 16 字节 = 4 个通道 × 4 像素/step
        {
            float dr = a[i] - b[i];
            float dg = a[i + 1] - b[i + 1];
            float db = a[i + 2] - b[i + 2];
            sum += dr * dr + dg * dg + db * db;
        }

        int samples = pixelCount / step;
        return sum / (samples * 3); // 每个样本 3 通道平均
    }
}
```

### 7.3 录制主控

```csharp
// src/TrueToneCap.Core/Recording/GifRecorder.cs

using System.Diagnostics;

namespace TrueToneCap.Core.Recording;

public sealed class GifRecorder : IDisposable
{
    private readonly ScreenCapture _capture;
    private readonly FrameBuffer _buffer;
    private readonly FrameDiffer _differ;
    private readonly int _fps;
    private CancellationTokenSource? _recordCts;
    private Task? _recordTask;
    private volatile bool _isRecording;

    public GifRecorder(ScreenCapture capture, int fps = 15, int bufferCapacity = 300)
    {
        _capture = capture;
        _buffer = new(bufferCapacity);
        _differ = new(threshold: 0.003f);
        _fps = fps;
    }

    public bool IsRecording => _isRecording;

    public event Action<int>? FrameCountChanged;

    /// <summary>开始录制。</summary>
    public void Start()
    {
        if (_isRecording) return;
        _isRecording = true;
        _recordCts = new CancellationTokenSource();

        _recordTask = Task.Run(async () =>
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var sw = Stopwatch.StartNew();
            int frameIndex = 0;

            while (!_recordCts.Token.IsCancellationRequested)
            {
                var frame = _capture.TryAcquireNextFrame(timeoutMs: (int)frameInterval.TotalMilliseconds);
                if (frame is null) continue;

                var pixels = await frame.GetFloatPixelsAsync(_recordCts.Token);
                if (_differ.IsSignificantChange(pixels, frame.Width * frame.Height, frameIndex))
                {
                    await _buffer.WriteAsync(
                        new RecordedFrame(pixels, frame.Width, frame.Height, sw.ElapsedTicks),
                        _recordCts.Token
                    );
                    _differ.UpdateReference(pixels);
                    FrameCountChanged?.Invoke(_buffer.Count);
                }

                frame.Dispose();
                frameIndex++;

                // 帧率控制
                var expected = frameIndex * frameInterval.TotalMilliseconds;
                var actual = sw.Elapsed.TotalMilliseconds;
                var delay = (int)(expected - actual);
                if (delay > 0)
                    await Task.Delay(delay, _recordCts.Token);
            }
        }, _recordCts.Token);
    }

    /// <summary>停止录制，返回帧缓冲区供编码。</summary>
    public async Task<List<RecordedFrame>> StopAsync()
    {
        if (!_isRecording) return [];
        _isRecording = false;
        _recordCts?.Cancel();

        if (_recordTask is not null)
        {
            try { await _recordTask; } catch (OperationCanceledException) { }
        }

        var frames = new List<RecordedFrame>();
        await foreach (var frame in _buffer.ReadAllAsync(CancellationToken.None))
        {
            frames.Add(frame);
            if (frames.Count >= _buffer.Count)
                break;
        }
        return frames;
    }

    public void Dispose()
    {
        _recordCts?.Cancel();
        _recordCts?.Dispose();
        _capture.Dispose();
    }
}
```

### 7.4 动画编码器

```csharp
// src/TrueToneCap.Core/Recording/AnimationEncoder.cs

using ImageMagick;

namespace TrueToneCap.Core.Recording;

public enum AnimationFormat { WebP, APNG, AVIF, GIF }

public sealed class AnimationEncoder
{
    /// <summary>
    /// 将录制帧集合编码为动画文件。
    /// 在后台线程池执行，不阻塞 UI。
    /// </summary>
    public async Task<byte[]> EncodeAsync(
        List<RecordedFrame> frames,
        AnimationFormat format,
        int fps,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var collection = new MagickImageCollection();
            int total = frames.Count;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var frame = frames[i];

                var settings = new PixelReadSettings(
                    frame.Width, frame.Height,
                    StorageType.Float,
                    PixelMapping.RGBA
                );

                var pixelBytes = new byte[frame.Width * frame.Height * 4 * sizeof(float)];
                Buffer.BlockCopy(frame.Pixels, 0, pixelBytes, 0, pixelBytes.Length);

                var image = new MagickImage(pixelBytes, settings);
                image.AnimationDelay = 100 / fps; // 百分之一秒

                // SDR 转换（动画格式通常不支持 HDR）
                image.ColorSpace = ColorSpace.sRGB;
                image.ColorType = ColorType.TrueColorAlpha;

                collection.Add(image);
                progress?.Report((i + 1) * 100 / total);
            }

            // 根据格式设置优化选项
            switch (format)
            {
                case AnimationFormat.WebP:
                    collection.Format = MagickFormat.WebP;
                    break;
                case AnimationFormat.APNG:
                    collection.Format = MagickFormat.Apng;
                    break;
                case AnimationFormat.AVIF:
                    collection.Format = MagickFormat.Avif;
                    break;
                case AnimationFormat.GIF:
                    collection.Format = MagickFormat.Gif;
                    collection.Optimize();        // 帧间优化
                    collection.OptimizeTransparency();
                    break;
            }

            return collection.ToByteArray();
        }, ct);
    }
}
```

---

## 8. 构建与部署

### 8.1 完整 csproj 配置

```xml
<!-- src/TrueToneCap.App/TrueToneCap.App.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.26100.0</TargetPlatformMinVersion>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- WinUI 3 设置 -->
    <UseWinUI>true</UseWinUI>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <!-- 应用标识 -->
    <ApplicationTitle>TrueToneCap</ApplicationTitle>
    <ApplicationId>com.trueonecap.app</ApplicationId>
    <ApplicationDisplayVersion>1.0.0</ApplicationDisplayVersion>
    <PublisherDisplayName>TrueToneCap</PublisherDisplayName>

    <!-- Native AOT 准备（实验性） -->
    <!-- <PublishAot>true</PublishAot> -->
    <!-- <InvariantGlobalization>true</InvariantGlobalization> -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.241114004" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1742" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="CommunityToolkit.WinUI.UI" Version="7.1.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\TrueToneCap.Core\TrueToneCap.Core.csproj" />
  </ItemGroup>

  <!-- 原生 DLL 复制 -->
  <ItemGroup>
    <Content Include="..\..\native\libjxl\*.dll">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <Link>%(Filename)%(Extension)</Link>
    </Content>
    <Content Include="..\..\native\lcms2\*.dll">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <Link>%(Filename)%(Extension)</Link>
    </Content>
  </ItemGroup>

</Project>
```

```xml
<!-- src/TrueToneCap.Core/TrueToneCap.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Vortice.Windows" Version="3.10.0" />
    <PackageReference Include="Vortice.Direct3D12" Version="3.10.0" />
    <PackageReference Include="Vortice.Direct2D1" Version="3.10.0" />
    <PackageReference Include="Vortice.DXGI" Version="3.10.0" />
    <PackageReference Include="Vortice.Dxc" Version="3.10.0" />
    <PackageReference Include="Vortice.Mathematics" Version="2.0.0" />

    <PackageReference Include="Magick.NET-Q16-HDR-AnyCPU" Version="14.4.0" />
    <PackageReference Include="Magick.NET.Core" Version="14.4.0" />

    <PackageReference Include="Microsoft.Graphics.Win2D" Version="1.3.1" />

    <PackageReference Include="System.Threading.Channels" Version="9.0.0" />
  </ItemGroup>

</Project>
```

### 8.2 着色器编译脚本

```powershell
# shaders/CompileShaders.ps1
param(
    [string]$ShaderDir = $PSScriptRoot,
    [string]$OutputDir = "$PSScriptRoot\..\src\TrueToneCap.App\Shaders"
)

$dxc = "dxc.exe" # 需安装 Windows SDK 或 DirectXShaderCompiler

$shaders = @(
    @{ Input = "ToneMapping.hlsl"; Entry = "main"; Profile = "ps_6_0" },
    @{ Input = "MosaicEffect.hlsl"; Entry = "main"; Profile = "ps_6_0" }
)

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

foreach ($s in $shaders) {
    $inputPath = Join-Path $ShaderDir $s.Input
    $outputPath = Join-Path $OutputDir "$($s.Input).cso"

    Write-Host "编译 $($s.Input) → $outputPath"
    & $dxc -T $s.Profile -E $s.Entry -Fo $outputPath $inputPath

    if ($LASTEXITCODE -ne 0) {
        Write-Error "着色器编译失败: $($s.Input)"
        exit 1
    }
}

Write-Host "✓ 所有着色器编译完成"
```

### 8.3 Windows 11 24H2 兼容性

| 功能 | 最低版本 | 说明 |
|------|---------|------|
| DXGI DuplicateOutput1 (HDR) | Win 10 2004 | 24H2 完全支持 |
| GraphicsCaptureItem (WinRT) | Win 10 1903 | WGC API，24H2 优化了 HDR 捕获 |
| WCS API (WcsGetDefaultColorProfile) | Win Vista | 24H2 有 ACM 增强 |
| Windows App SDK 1.6+ | Win 11 21H2 | 24H2 提供最佳兼容性 |
| Native AOT (.NET 10) | .NET 10 RC+ | 实验性，不推荐用于 WinUI 3 |

---

## 9. 技术难点与解决方案

### 9.1 Vortice 与 WinUI 3 集成

**问题**：Vortice 使用原生 COM 指针，WinUI 3 CanvasControl 使用 Win2D 封装。两者需要互操作。

**解决方案**：
```csharp
// Vortice ID3D11Texture2D → Win2D CanvasBitmap
using Vortice.Direct3D11;

public static CanvasBitmap ToCanvasBitmap(
    this ID3D11Texture2D texture,
    ICanvasResourceCreator resourceCreator)
{
    // 方法1：通过 IDirect3DSurface 桥接
    using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
    var surface = CanvasBitmap.CreateFromDirect3D11Surface(
        resourceCreator, dxgiSurface);

    // 方法2：通过共享句柄（跨进程/跨设备）
    // using var sharedTexture = CreateSharedTexture(texture);
    // ...

    return surface;
}
```

**注意事项**：
- Win2D 和 Vortice 必须共享同一个 D3D11Device，否则无法互操作。
- 建议在应用启动时创建全局单例 `D3D11Device`，供所有组件使用。
- `ID3D11DeviceContext` 上的操作不是线程安全的，需要使用锁保护。

### 9.2 多显示器 HDR/SDR 混合环境

**问题**：一台 HDR 显示器 + 一台 SDR 显示器时，截图可能来自不同色彩空间的显示器。

**解决方案**：
1. 捕获时记录每帧来自哪个显示器（通过 `IDXGIOutput` 标识）。
2. 检索对应显示器的 DXGI 色彩空间信息（`IDXGIOutput6.Description1.ColorSpace`）。
3. 对每个显示器维护独立的 ICC 配置文件和色彩空间变换矩阵。
4. 编码时，根据源显示器色彩空间选择适当的输出编码路径。

```csharp
public readonly record struct DisplayColorInfo(
    ColorSpaceType ColorSpace,
    bool IsHdr,
    Vector3 DisplayLuminance, // MaxLuminance, MinLuminance, MaxFullFrameLuminance
    byte[]? IccProfile
);

public static class DisplayInfoCache
{
    private static readonly ConcurrentDictionary<nint, DisplayColorInfo> _cache = new();

    public static DisplayColorInfo GetInfo(nint hmonitor)
    {
        return _cache.GetOrAdd(hmonitor, static hm =>
        {
            // 通过 DXGI 获取色彩空间和亮度信息
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
            // ... 遍历适配器和输出，匹配 hmonitor
            return new DisplayColorInfo(
                ColorSpaceType.RgbFullG22NoneP709, // SDR 默认
                false,
                new Vector3(270, 0.5f, 270),
                IccProfileProvider.GetMonitorIccProfile(hm)
            );
        });
    }
}
```

### 9.3 性能优化建议

1. **纹理拷贝最小化**
   - 使用 `ID3D11DeviceContext.CopySubresourceRegion` 仅拷贝脏矩形区域。
   - 标注渲染到离屏纹理后，使用同一纹理供预览和编码，避免二次拷贝。

2. **编码并行化**
   - 多格式同时导出时，使用 `Parallel.ForEachAsync` 并行编码。
   - JPEG LI 编码使用独立的 `Task.Run` 避免阻塞线程池。

3. **GPU 内存管理**
   - 使用 `ID3D11Texture2D` 池化复用，减少分配开销。
   - 及时释放暂存纹理，使用 `using` 或 `DisposeAsync`。

4. **录制性能**
   - 帧差异检测使用 SIMD 加速（`System.Numerics.Vector<float>`）。
   - 环形缓冲区使用 `System.Threading.Channels` 实现无锁生产者-消费者。
   - 编码阶段使用 `MagickImageCollection` 的流式写入，避免一次性加载所有帧到内存。

### 9.4 各格式 HDR 支持现状

| 格式 | HDR 支持程度 | 限制 |
|------|-------------|------|
| PNG | 草案阶段 | 多数观看器不支持 HDR PNG；仅少数浏览器（Chrome 116+）支持 |
| JPEG XL | 原生完整 | 浏览器支持有限（Chrome 已移除，Firefox 实验性） |
| JPEG (LI) | 不支持 | jpegli 仅改进 8-bit 压缩，无 HDR 能力 |
| AVIF | 原生完整 | 浏览器支持好（Chrome 85+），但桌面查看器支持参差 |
| WebP | 不支持 | 仅 8-bit |

**推荐策略**：默认导出 AVIF (HDR) + PNG (SDR) 双格式，提供 JPEG XL 作为专业选项。

### 9.5 jpegli 集成方案

由于 jpegli 没有稳定的公共 C API，推荐以下集成路径（按优先级）：

1. **通过 libjxl 的 JxlEncoder API 间接使用 jpegli**
   - libjxl 内置了 jpegli 编码路径，通过 `JxlEncoderSetUseJpegli` 开关启用。
   - 编写小型 C++/CLI 桥接 DLL 封装此功能。

2. **使用 Magick.NET 的实验性 JPEG LI 支持**
   - 检查 Magick.NET Q16-HDR 的 changelog，高版本可能已集成 jpegli。
   - 如支持，直接使用 `image.Settings.SetDefine("jpeg:encoder", "jpegli")`。

3. **作为外部进程调用**
   - 编译 `cjpegli` 命令行工具（来自 libjxl 项目）。
   - 通过 `System.Diagnostics.Process` 管道传入像素数据。
   - 性能较差，不推荐用于生产环境。

---

## 附录 A：WinUI 3 主窗口框架

```xml
<!-- src/TrueToneCap.App/MainWindow.xaml -->
<Window x:Class="TrueToneCap.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:canvas="using:Microsoft.Graphics.Canvas.UI.Xaml"
        Title="TrueToneCap" 
        ExtendsContentIntoTitleBar="True"
        Height="900" Width="1400">

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="48"/>  <!-- 工具栏 -->
            <RowDefinition Height="*"/>   <!-- 预览区 -->
            <RowDefinition Height="Auto"/> <!-- 状态栏 -->
        </Grid.RowDefinitions>

        <!-- 工具栏 -->
        <StackPanel Grid.Row="0" Orientation="Horizontal"
                    Background="{ThemeResource CardBackgroundFillColorDefault}"
                    Padding="12,8" Spacing="8">
            <Button x:Name="CaptureBtn" Click="OnCaptureClick">
                <SymbolIcon Symbol="Camera"/> 截图
            </Button>
            <Button x:Name="RecordBtn" Click="OnRecordClick">
                <SymbolIcon Symbol="Video"/> 录制
            </Button>
            <AppBarSeparator/>
            <ComboBox x:Name="FormatCombo" Width="120"
                      SelectedIndex="0">
                <ComboBoxItem>AVIF (HDR)</ComboBoxItem>
                <ComboBoxItem>JPEG XL (HDR)</ComboBoxItem>
                <ComboBoxItem>PNG (HDR)</ComboBoxItem>
                <ComboBoxItem>WebP</ComboBoxItem>
                <ComboBoxItem>JPEG (LI)</ComboBoxItem>
            </ComboBox>
            <Button x:Name="SaveBtn" Click="OnSaveClick">
                <SymbolIcon Symbol="Save"/> 保存
            </Button>
            <AppBarSeparator/>
            <Button x:Name="UndoBtn" Click="OnUndoClick">
                <SymbolIcon Symbol="Undo"/>
            </Button>
            <Button x:Name="RedoBtn" Click="OnRedoClick">
                <SymbolIcon Symbol="Redo"/>
            </Button>
        </StackPanel>

        <!-- 预览 / 标注 画布 -->
        <canvas:CanvasControl x:Name="PreviewCanvas"
                               Grid.Row="1"
                               Draw="OnCanvasDraw"
                               CreateResources="OnCanvasCreateResources"/>

        <!-- 状态栏 -->
        <Border Grid.Row="2" Padding="12,6"
                Background="{ThemeResource CardBackgroundFillColorDefault}">
            <TextBlock x:Name="StatusText" Text="就绪"/>
        </Border>
    </Grid>
</Window>
```

---

## 附录 B：解决方案文件

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.12.0.0
MinimumVisualStudioVersion = 17.0.0.0

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "TrueToneCap.Core",
    "src\TrueToneCap.Core\TrueToneCap.Core.csproj",
    "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
EndProject

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "TrueToneCap.App",
    "src\TrueToneCap.App\TrueToneCap.App.csproj",
    "{F1E2D3C4-B5A6-9870-FEDC-BA0987654321}"
EndProject

Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|x64 = Debug|x64
        Debug|ARM64 = Debug|ARM64
        Release|x64 = Release|x64
        Release|ARM64 = Release|ARM64
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Debug|x64.ActiveCfg = Debug|x64
        {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Release|x64.ActiveCfg = Release|x64
        {F1E2D3C4-B5A6-9870-FEDC-BA0987654321}.Debug|x64.ActiveCfg = Debug|x64
        {F1E2D3C4-B5A6-9870-FEDC-BA0987654321}.Release|x64.ActiveCfg = Release|x64
    EndGlobalSection
EndGlobal
```

---

> **文档版本**：v1.0 | **最后更新**：2026-07-02 | **作者**：TrueToneCap 架构团队
