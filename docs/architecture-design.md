# TrueToneCap — 架构设计文档

> **目标系统**：Windows 11 24H2+ | **技术栈**：C# 13 / WinUI 3 / .NET 10 / WindowsAppSDK 2.3.1 / Vortice.D3D11

---

## 1. 项目结构

```
TrueToneCap/
├── TrueToneCap.slnx                      # 解决方案（slx 格式）
├── Directory.Build.targets               # SDK 10 兼容修复（PRI 生成禁用）
├── Publish.ps1                           # 发布脚本
├── docs/
│   └── architecture-design.md            # 本文件
├── publish/
│   └── PACKAGE.md                        # 打包说明
└── src/
    ├── TrueToneCap.Core/                 # 核心类库（无 UI 依赖）
    │   ├── Capture/
    │   │   └── DisplayInfo.cs            # 显示器枚举 + HDR 检测
    │   ├── Processing/
    │   │   ├── GpuToneMapper.cs          # GPU 色调映射（HLSL + D3D11）
    │   │   ├── GpuEffectProcessor.cs     # GPU 后处理（马赛克等）
    │   │   └── ToneMapper.cs             # CPU 色调映射（Reinhard/Hable/ACES）
    │   ├── Encoding/
    │   │   ├── ImageEncoder.cs           # 编码器抽象 + EncodingSettings
    │   │   ├── FormatEncoders.cs         # PNG/JpegLI/JXL/AVIF/WebP 实现
    │   │   ├── JpegGainMapEncoder.cs     # JPEG Gain Map (Ultra HDR)
    │   │   ├── JpegLiNative.cs           # jpegli P/Invoke
    │   │   ├── GpuCapability.cs          # GPU 硬件编码器检测
    │   │   ├── AvifHardwareProbe.cs      # AVIF 硬件探测
    │   │   ├── MftEncoderNative.cs       # MFT 编码器
    │   │   ├── NvEncoderNative.cs        # NVENC P/Invoke
    │   │   └── QsvEncoderNative.cs       # QSV P/Invoke
    │   ├── Annotation/
    │   │   ├── AnnotationLayer.cs        # 8 种标注图层
    │   │   ├── AnnotationManager.cs      # 命令模式撤销/重做
    │   │   └── Shapes/                   # 具体形状实现
    │   ├── ColorManagement/
    │   │   ├── ColorProfileProvider.cs   # WCS ICC 获取 + sRGB 内置
    │   │   └── GamutMapper.cs            # ACES 色域缩限
    │   ├── Metadata/
    │   │   └── MetadataCollector.cs      # 前台窗口/光标/显示器元数据
    │   ├── Detection/
    │   │   ├── RegionDetector.cs         # 窗口区域自动检测
    │   │   └── DetectedRegion.cs         # 检测结果 DTO
    │   ├── Services/
    │   │   ├── MultiOcrService.cs        # 多引擎 OCR 路由
    │   │   ├── OnnxOcrEngine.cs          # ONNX DirectML/CPU OCR
    │   │   ├── OcrService.cs             # Windows OCR 封装
    │   │   ├── TranslationService.cs     # LLM 翻译
    │   │   └── BitmapPreprocessor.cs     # OCR 预处理
    │   ├── PixelOps.cs                   # 多 ISA 像素加速（AVX-512/AVX2/NEON）
    │   └── TrueToneCap.Core.csproj
    ├── TrueToneCap.App/                  # WinUI 3 应用
    │   ├── App.xaml / .xaml.cs           # 应用入口 + 主题
    │   ├── MainWindow.xaml / .xaml.cs    # 设置主窗口
    │   ├── SelectionOverlay.xaml / .cs   # 选区覆盖层 + 标注
    │   ├── AnnotationWindow.xaml / .cs   # 独立标注窗口
    │   ├── OcrPreviewWindow.xaml / .cs   # OCR/翻译预览
    │   ├── Services/
    │   │   ├── AppServices.cs            # 服务定位器（DI）
    │   │   ├── WgcCaptureService.cs      # WGC 池化捕获服务
    │   │   ├── SettingsService.cs        # 设置持久化
    │   │   ├── CapabilityService.cs      # 系统能力检测
    │   │   ├── CapturePipelineService.cs # 编码管线调度
    │   │   ├── AnimationRecorder.cs      # 动图录制
    │   │   ├── LogService.cs             # 统一日志
    │   │   ├── ToastService.cs           # Toast 通知
    │   │   ├── TrayIconManager.cs        # 系统托盘
    │   │   ├── HotkeyManager.cs          # 全局热键
    │   │   ├── FontLoader.cs             # 内嵌字体
    │   │   ├── LocaleManager.cs          # 多语言
    │   │   ├── StartupManager.cs         # 开机自启
    │   │   └── ShaderCompiler.cs         # 着色器编译
    │   ├── Models/
    │   │   └── CaptureResult.cs          # 捕获结果 DTO
    │   ├── Shaders/
    │   │   ├── ToneMapping.hlsl          # 色调映射 PS
    │   │   ├── FullscreenVS.hlsl         # 全屏三角形 VS
    │   │   ├── MosaicEffect.hlsl         # 马赛克 PS
    │   │   └── CompileShaders.ps1        # dxc 编译脚本
    │   └── TrueToneCap.App.csproj
    ├── TrueToneCap.Test/                 # 单元测试
    │   ├── CorePipelineTests.cs          # PixelOps/ToneMapper/ICC 测试
    │   └── Program.cs                    # OCR 基准 + --unit-tests 入口
    ├── TrueToneCap.Tools/                # 格式编码基准测试
    │   └── FormatBench.cs
    └── TestDirectML/                     # DirectML 可用性验证
        └── Program.cs
```

---

## 2. 核心数据流（WGC 管线）

```
[显示器] ──WGC FrameArrived──▶ [PooledSession 最新帧缓存]
                                      │
                    ┌─────────────────┼──────────────────┐
                    ▼                 ▼                  ▼
              [HDR Float16]     [SDR BGRA8]      [ICC Profile]
              R16G16B16A16F     B8G8R8A8          WCS API 缓存
                    │                 │                  │
                    ▼                 │                  │
            [GpuToneMapper]           │                  │
            FullscreenVS + PS         │                  │
            (CPU Hable 回退)          │                  │
                    │                 │                  │
                    ▼                 ▼                  │
              [SelectionOverlay 选区覆盖层]              │
              RegionDetector 窗口检测                    │
              AnnotationManager 标注                    │
                    │                                  │
                    ▼                                  ▼
              [CapturePipelineService] ◀──── [MetadataCollector]
              ├─ ICC 烘焙 (ACES Perceptual)    前台窗口/光标/显示器
              ├─ PNG (Magick.NET)
              ├─ JPEG LI (jpegli P/Invoke)
              ├─ JPEG XL (JxlNet)
              ├─ AVIF (MFT > NVENC > QSV > libaom)
              ├─ WebP (Magick.NET)
              ├─ JPEG Gain Map (Ultra HDR)
              └─ BMP
                    │
                    ▼
              [Toast 通知 + 剪贴板]
```

---

## 3. WGC 捕获服务

### 3.1 架构

`WgcCaptureService` 是唯一的捕获后端（v0.2.0+ 已移除 GDI 和 DXGI Desktop Duplication）。

核心设计：
- **持久会话池**：后台 `GraphicsCaptureSession` 持续接收帧，截图时零延迟取最新帧
- **会话 Key**：`(nint HMONITOR, bool IsHdr)` 元组，避免碰撞
- **像素缓冲复用**：PooledSession 内部按尺寸复用 byte[]/float[]，避免 60fps 下每帧分配
- **Staging 纹理复用**：D3D11 staging 纹理按尺寸缓存
- **空闲自动停止**：15 秒无截图则释放所有会话
- **HDR 能力缓存**：已知不支持 HDR 的显示器跳过 HDR 会话创建

### 3.2 捕获模式

| 模式 | 格式 | 用途 |
|------|------|------|
| SDR | B8G8R8A8UIntNormalized | 默认截图、选区、录制 |
| HDR | R16G16B16A16Float | HDR 显示器 + HDR 输出格式 |

### 3.3 多显示器

- 单显示器：`CaptureMonitorAsync(config)` — 从池化会话取最新帧
- 全桌面拼接：`CaptureAllMonitorsAsync(config)` — 分别捕获 → 按虚拟屏幕坐标拼接

---

## 4. GPU 渲染管线

### 4.1 着色器

| 文件 | 类型 | 用途 |
|------|------|------|
| `FullscreenVS.hlsl` | vs_6_0 | SV_VertexID 全屏三角形（无 VB） |
| `ToneMapping.hlsl` | ps_6_0 | Reinhard/Hable 色调映射 + sRGB gamma |
| `MosaicEffect.hlsl` | ps_6_0 | 马赛克像素化 |

编译：`CompileShaders.ps1`（需要 Windows SDK dxc.exe）

### 4.2 GpuToneMapper

- 输入：float[] HDR 像素（scRGB linear）
- 输出：byte[] BGRA8 SDR 像素
- 流程：Float32→Float16 上传（PixelOps SIMD）→ PS 渲染 → BGRA8 读回
- 纹理池化：输入/输出/staging 按尺寸缓存复用
- 回退：着色器缺失或 GPU 失败时自动使用 CPU ToneMapper

### 4.3 CPU ToneMapper（融合内核）

`ToneMapper.FloatToSRgbBytes` 在单个 Parallel.For 中完成：
1. 曝光调整
2. 色调映射（Reinhard/Hable/ACES）
3. sRGB gamma 编码
4. RGBA→BGRA swizzle + 量化

无中间 float 数组拷贝（4K 节省 132MB）。

---

## 5. 多格式编码器

### 5.1 编码器矩阵

| 格式 | HDR | 后端 | 质量参数 |
|------|-----|------|---------|
| PNG | ✅ | Magick.NET | 无损 |
| JPEG LI | ❌ | jpegli P/Invoke | butteraugli 距离 0.5-3.0 |
| JPEG Gain Map | ✅ | jpegli + 增益图 | butteraugli 距离 |
| JPEG XL | ✅ | JxlNet | butteraugli 距离 0.1-4.0 |
| AVIF | ✅ | MFT/NVENC/QSV/libaom | CRF 0-63 |
| WebP | ❌ | Magick.NET | 质量 50-100 |
| BMP | ❌ | 原生 | 无 |

### 5.2 AVIF 后端优先级

```
Auto → MFT (系统硬件) > NVENC (NVIDIA) > QSV (Intel) > libaom (CPU)
```

检测：`GpuCapability.DetectEncoders()` — DXGI 适配器枚举 + 名称/DeviceId 双策略。

---

## 6. 色彩管理

### 6.1 ICC 获取

`ColorProfileProvider`：
- WCS API (`mscms.dll`) 获取显示器 ICC
- 非阻塞缓存：首次调用后台预热，立即返回 null
- 内置 sRGB ICC：优先系统 `sRGB Color Space Profile.icm`，回退到内置有效 ICC v2.1

### 6.2 色彩管线

```
显示器 ICC → ACES Perceptual 烘焙 → 目标色域
  ├─ sRGB 目标：不嵌入 ICC
  └─ 广色域目标（P3/AdobeRGB/BT.2020）：嵌入标准 ICC
```

ACM（Auto Color Management）开启时自动禁用 ICC 烘焙。

---

## 7. 动图录制

`AnimationRecorder`：
- 共享 `WgcCaptureService`（不创建独立设备）
- 有界 `Channel`（≤30 帧，DropOldest）防 OOM
- `PeriodicTimer` 精确帧定时
- 帧差异检测（采样 5000 点，阈值过滤）
- 输出：Animated WebP / APNG / AVIF / GIF（Magick.NET）

---

## 8. 服务层架构

```
App.OnLaunched
  └─ AppServices.Initialize()
       ├─ LogService.InitializeFileLog()
       ├─ SettingsService.Load()
       ├─ CapabilityService (HDR/ACM/ICC/编码器检测)
       ├─ CapturePipelineService (ICC + 编码调度)
       ├─ WgcCaptureService (D3D11 设备 + 会话池)
       └─ GpuToneMapper (着色器加载)
```

MainWindow 通过 `AppServices.*` 访问所有服务，不再本地持有。

---

## 9. NuGet 依赖

| 包 | 版本 | 项目 |
|----|------|------|
| Microsoft.WindowsAppSDK | 2.3.1 | App |
| Microsoft.Graphics.Win2D | 1.4.0 | App |
| Vortice.Direct3D11 | 3.8.3 | Core |
| Vortice.DXGI | 3.8.3 | Core |
| Magick.NET-Q16-HDRI-AnyCPU | 14.15.0 | Core |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | Core |
| JxlNet | 0.11.2.2 | Core |
| System.Drawing.Common | 10.0.10 | Test |

---

## 10. 构建与发布

```powershell
# 编译
dotnet build TrueToneCap.slnx -c Debug

# 编译着色器（需要 Windows SDK dxc.exe）
cd src/TrueToneCap.App/Shaders
.\CompileShaders.ps1

# 发布（self-contained）
dotnet publish src/TrueToneCap.App/TrueToneCap.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsAppSDKSelfContained=true `
  -o publish/TrueToneCap-v{version}

# 单元测试
dotnet run --project src/TrueToneCap.Test -- --unit-tests
```

---

## 11. 关键技术决策

| 决策 | 理由 |
|------|------|
| WGC 唯一捕获后端 | Win11 24H2+ 目标，WGC 支持 HDR + 无边框 + 零延迟池化 |
| SV_VertexID 全屏三角形 | 无需顶点缓冲区，减少 GPU 状态切换 |
| 融合色调映射内核 | 消除 132MB 中间数组拷贝，4K 下显著降低延迟 |
| 多 ISA PixelOps | AVX-512 VL/BW → AVX2 → NEON → 标量，JIT 常量折叠零开销 |
| 有界 Channel 录制 | 防止 4K@60fps 录制 OOM（上限 ~1GB） |
| AppServices 服务定位器 | WinUI 3 无内置 DI，轻量静态单例足够 |
| LogService 统一日志 | 替代散落的 Debug/Trace/Console.WriteLine |
