# TrueToneCap — 架构设计文档 (v0.3.0-beta)

> **目标系统**：Windows 11 24H2+ | **技术栈**：C# 14 / WinUI 3 / .NET 11 / WindowsAppSDK 2.3.1 / Vortice.D3D11
>
> **管线审查日期**：2026-08-01 | **总测试**：192/192 ✅ 100%
>
> **迁移计划**：参见 [dotnet11-migration-plan.md](dotnet11-migration-plan.md) (.NET 10 → .NET 11)

---

## 0. 完整管线总览 (v0.3.0-beta)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  [显示器] ──WGC FrameArrived──▶ [PooledSession 三缓冲缓存]                  │
│                                      │                                       │
│                    ┌─────────────────┼──────────────────┐                   │
│                    ▼                 ▼                  ▼                   │
│              [HDR Float16]     [SDR BGRA8]       [ICC Profile]              │
│              R16G16B16A16F     B8G8R8A8          WCS API 异步缓存           │
│              float[]           byte[]            非阻塞: 未命中→后台预热     │
│                    │                 │                  │                   │
│                    ▼                 │                  │                   │
│        ┌─── [GpuToneMapper] ───┐     │                  │                   │
│        │ HLSL PS + D3D11      │     │                  │                   │
│        │ 纹理池化按尺寸复用    │     │                  │                   │
│        │ (CPU Hable 回退)      │     │                  │                   │
│        └───────────────────────┘     │                  │                   │
│                    │                 │                  │                   │
│                    ▼                 ▼                  ▼                   │
│          [CaptureResult DTO] ─────────────────────────────────────────────  │
│          HdrPixels / SdrPixels / GpuTexture / IccProfile                    │
│                    │                                                       │
│                    ▼                                                       │
│          [SelectionOverlay 选区覆盖层]                                      │
│          RegionDetector 窗口检测 / AnnotationManager 标注 / 裁剪            │
│                    │                                                       │
│                    ▼                                                       │
│          [CapturePipelineService] ──── [ColorProfileProvider]              │
│          ├─ ICC 烘焙 (ACES Perceptual)     WCS API + 标准 ICC 生成         │
│          │  ├─ sRGB 目标: 不嵌入 ICC                                      │
│          │  └─ 广色域目标: 嵌入 BT.2020/P3/AdobeRGB ICC                   │
│          ├─ 色域转换 (ColorSpaceConverter)                                  │
│          │  ├─ HDR直通: scRGB→目标色域矩阵→PQ→CICP{primaries,PQ}          │
│          │  └─ Float16→SDR: 色域矩阵→色调映射→gamma→BGRA8+ICC             │
│          ├─ 编码设置 (每格式位深/色度)                                     │
│          └─ 输出路径 (含归档子目录)                                         │
│                    │                                                       │
│                    ▼                                                       │
│          [EncoderFactory → 8 编码器]                                       │
│          ├─ PNG (托管)     8/10/12/16-bit  ICC/CICP 互斥                    │
│          ├─ JPEG LI (jpegli) 8-bit  butteraugli 0.5-3.0                    │
│          ├─ JPEG XL (JxlNet) 8/10/12-bit  HDR: PQ 16-bit                  │
│          ├─ AVIF (MFT>NVENC>QSV>libaom) 8/10/12-bit  HDR: PNG 中转         │
│          │  └─ NVENC GPU 纹理直通路径 (D3D11→NVENC, 跳过 CPU 回读)         │
│          ├─ WebP (libwebp/cwebp) 8-bit  回退→PNG                           │
│          ├─ TIFF (托管)    HDR: 16-bit PQ                                  │
│          ├─ JPEG Gain Map (jpegli+增益图) HDR: 增益比+ICC                  │
│          └─ BMP (托管)     8-bit  无损                                      │
│                    │                                                       │
│                    ▼                                                       │
│          [输出] 文件/剪贴板/Toast/运行报告                                   │
│          ├─ 全屏截图 → 文件 + 剪贴板 + Toast                                │
│          ├─ 选区截图 → 文件 + 剪贴板 + Toast                                │
│          ├─ 静默全屏 → 文件 + Toast (右下角)                                │
│          ├─ OCR/翻译 → OcrPreviewWindow 点对点覆盖                          │
│          └─ 动图录制 → Animated WebP/APNG/GIF/AVIF                          │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 数据流通道

```
SDR 路径 (默认):
  WGC BGRA8 byte[] → PooledSession._latestSdr
    → CaptureResult.SdrPixels
    → PreparePixelsWithIcc() → ICC 烘焙 → 编码器 → 文件

HDR 直通路径 (HDR 开启 + 编码器支持):
  WGC Float16 float[] → PooledSession._latestHdr
    → CaptureResult.HdrPixels
    → ColorSpaceConverter.ConvertScrgbToTarget() → 目标色域线性
    → FormatHelper.HdrToPq16() → PQ 16-bit
    → 编码器 (16-bit PNG/AVIF/JXL/TIFF) → CICP{primaries, PQ} → 文件

GPU 纹理直通路径 (NVENC AVIF 专用):
  PooledSession._latestTexture (Default GPU 纹理)
    → CaptureResult.GpuTexture
    → NvencAvifBackend.EncodeAsync()
    → NvEncRegisterResource + MapInputResource → NVENC 硬件编码
    → 文件 (跳过 CPU 回读, 4K 节省 6-15ms)

Float16→SDR 广色域路径 (HDR 关闭 + 广色域目标):
  WGC Float16 float[] → CaptureResult.HdrPixels
    → PrepareFloat16WithIcc()
    → ColorSpaceConverter.ConvertFloat16ToSdrBgra() → 色域矩阵→色调映射→gamma
    → BGRA8 + 目标色域 ICC → 编码器 → 文件

HDR→SDR 回退路径 (编码器不支持 HDR):
  WGC Float16 float[] → CaptureResult.HdrPixels
    → FormatHelper.ToSdr() → ToneMapper.FloatToSRgbBytes() → BGRA8
    → SDR 编码器 → 文件
```

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
    │   │   ├── NativeJxlEncoder.cs       # JXL P/Invoke
    │   │   ├── NativeAvifEncoder.cs      # AVIF P/Invoke (avifenc)
    │   │   ├── NativeWebPEncoder.cs      # WebP P/Invoke (libwebp)
    │   │   ├── ManagedPngEncoder.cs      # 托管 PNG 编码器
    │   │   ├── ManagedJpegEncoder.cs     # 托管 JPEG 编码器 (回退)
    │   │   ├── GpuCapability.cs          # GPU 硬件编码器检测
    │   │   ├── AvifHardwareProbe.cs      # AVIF 硬件探测
    │   │   ├── NvEncoderNative.cs        # NVENC P/Invoke
    │   │   ├── NvencAvifBackend.cs       # NVENC AVIF 后端 (GPU 纹理直通)
    │   │   ├── MftEncoderNative.cs       # MFT 编码器
    │   │   └── QsvEncoderNative.cs       # QSV P/Invoke
    │   ├── Annotation/
    │   │   ├── AnnotationLayer.cs        # 8 种标注图层
    │   │   ├── AnnotationManager.cs      # 命令模式撤销/重做
    │   │   └── Shapes/                   # 具体形状实现
    │   ├── ColorManagement/
    │   │   ├── ColorProfileProvider.cs   # WCS ICC 获取 + sRGB 内置 + ColorSpaceConverter
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
    │   │   ├── FontLoader.cs             # 字体工具（默认回退链 + 用户字体选择）
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

## 2. WGC 捕获服务 (WgcCaptureService)

### 2.1 架构模式

`WgcCaptureService` 是唯一的捕获后端（v0.2.0+ 已移除 GDI 和 DXGI Desktop Duplication）。

| 优化 | 机制 | 效果 |
|------|------|------|
| **持久会话池** | 后台 `GraphicsCaptureSession` 持续接收帧 | 截图零延迟取最新帧 |
| **会话 Key** | `(nint HMONITOR, bool IsHdr)` 元组 | 避免碰撞 |
| **三缓冲零拷贝** | write/ready/consumed 三级缓冲，`Interlocked.Exchange` 原子交换 | 消除竞争条件 |
| **Staging 纹理复用** | 按尺寸缓存 D3D11 staging texture | 避免每帧重新创建 |
| **GPU 纹理直通** | 保留 WGC 帧纹理引用供 NVENC 直通 | 节省 ~264MB PCIe 带宽 |
| **空闲自动停止** | 15 秒无截图释放所有会话 | 节省 GPU 资源 |
| **HDR 能力缓存** | 已知不支持 HDR 的显示器跳过 HDR 会话创建 | 减少启动延迟 |
| **实例级 D3D11 锁** | 每个 WgcCaptureService 实例各自加锁 | 多显示器不互相阻塞 |

### 2.2 捕获模式

| 模式 | 格式 | 像素格式 | 用途 |
|------|------|---------|------|
| SDR | B8G8R8A8UIntNormalized | byte[] BGRA8 | 默认截图、选区、录制 |
| HDR | R16G16B16A16Float | float[] scRGB 线性 | HDR 输出 + 广色域 SDR |

### 2.3 捕获路径

| 路径 | 方法 | 用途 |
|------|------|------|
| 池化快速路径 | `PooledSession.GetLatestSdr()` | 已有帧 → 零延迟返回 |
| 等待首帧 | `PooledSession.WaitForFirstFrame()` | 首次/线程问题回退 |
| 一次性捕获 | `OneShotCapture()` | 池完全失败时 STA 线程回退 |
| 无锁读取 | `TryGetLatestFrame()` | 动图录制（不获取 s_captureLock） |
| 多显示器拼接 | `CaptureAllMonitorsInternalAsync()` | 分别捕获后按虚拟坐标拼接 |

### 2.4 并发保护

| 锁 | 作用域 | 用途 |
|----|--------|------|
| `s_captureLock` | 进程级 `SemaphoreSlim(1,1)` | 防止并发截图 |
| `_d3dContextLock` | 实例级 | 保护所有 D3D11 GPU 操作 |
| `_gpuLock` | 实例级 (PooledSession 共享) | 保护纹理拷贝/读取 |
| `_poolLock` | 实例级 | 保护会话池字典 |
| `_frameLock` | 实例级 (PooledSession 内) | 保护帧缓存读写 |

### 2.5 数据流

```
CaptureMonitorAsync(config)
  ├─ s_captureLock.WaitAsync(0) → 失败则抛出 "已有另一个捕获进行中"
  ├─ GetOrCreateSdrSession(targetMonitor) → SDR 会话
  │   ├─ HasFrame → GetLatestSdr() → byte[] (零延迟)
  │   └─ !HasFrame → WaitForFirstFrame(timeout) → 超时则抛出
  ├─ PreferHdr → GetOrCreateHdrSession(targetMonitor) → Float16 会话
  │   ├─ HDR 能力缓存检查 → 不支持则返回 null
  │   └─ HasFrame → GetLatestHdr() → float[]
  ├─ GetLatestTexture() → GPU 纹理 (NVENC 直通)
  ├─ ColorProfileProvider.GetDisplayIccProfile() → ICC (非阻塞)
  ├─ ScheduleIdleStop() → 15s 后自动释放会话
  └─ 返回 CaptureResult { SdrPixels, HdrPixels, GpuTexture, IccProfile, ... }
```

---

## 3. GPU 渲染管线 (GpuToneMapper + ToneMapper)

### 3.1 着色器

| 文件 | 类型 | 用途 |
|------|------|------|
| `FullscreenVS.hlsl` | vs_6_0 | SV_VertexID 全屏三角形（无 VB / IB） |
| `ToneMapping.hlsl` | ps_6_0 | Reinhard/Hable 色调映射 + sRGB gamma |
| `MosaicEffect.hlsl` | ps_6_0 | 马赛克像素化 |

编译：`CompileShaders.ps1`（需要 Windows SDK dxc.exe）

### 3.2 GpuToneMapper (GPU 路径)

| 属性 | 值 |
|------|-----|
| 输入 | float[] HDR scRGB 线性 |
| 输出 | byte[] BGRA8 SDR sRGB |
| 着色器加载 | 文件系统 → 嵌入资源双重回退 |
| 纹理池化 | 输入/输出/staging 按尺寸缓存复用 |
| 常量缓冲区 | Mode(uint) + Exposure(float) + PaperWhite(float) + MaxNits(float) |
| 回退 | 着色器缺失或 GPU 失败 → CPU ToneMapper |

流程：
```
Float32[] → PixelOps.ConvertHalfToFloatRow → Float16 纹理上传
  → PS 渲染 (Hable/Reinhard 色调映射 + sRGB gamma)
  → BGRA8 读回 → byte[]
```

### 3.3 CPU ToneMapper (融合内核)

`ToneMapper.FloatToSRgbBytes` 在单个 `Parallel.For` 中完成 4 个步骤：

1. **曝光调整** (evScale = 2^exposure)
2. **色调映射** (Reinhard / Hable / ACES)
3. **sRGB gamma 编码**
4. **RGBA → BGRA swizzle + 量化**

无中间 float 数组拷贝（4K 节省 132MB）。

### 3.4 3 种色调映射算法

| 算法 | 特点 | 公式 |
|------|------|------|
| Reinhard | 全局算子，基于亮度缩放 | L / (1+L) |
| Hable (Filmic) | Uncharted 2 风格曲线 | 默认白点 11.2 |
| ACES | Narkowicz 2015 近似拟合 | (x*(2.51x+0.03))/(x*(2.43x+0.59)+0.14) |

---

## 4. 色彩管理管线 (ColorProfileProvider + CapturePipelineService)

### 4.1 ICC 获取

| 机制 | 行为 |
|------|------|
| WCS API (`mscms.dll`) | 获取显示器 ICC 配置文件 |
| 非阻塞缓存 | 首次未命中 → 后台预热，立即返回 null（绝不阻塞截图） |
| PrewarmAllDisplays | 启动时异步预热所有显示器 ICC |
| 内置 sRGB ICC | 系统文件 → 嵌入 v2.1 → 手写最小 ICC 三重回退 |
| 缓存失效 | InvalidateCache() 显示器配置变更时调用 |

### 4.2 色彩转换

```
显示器 ICC → ACES Perceptual 烘焙 → 目标色域
  ├─ sRGB 目标: 不嵌入 ICC
  └─ 广色域目标 (P3/AdobeRGB/BT.2020): 嵌入标准 ICC
```

### 4.3 ACM 集成

- ACM 开启时 ICC 烘焙**不再禁用**（v0.3.0-beta 修复）
- `CapabilityService.DetectAllAsync()` 检测 ACM 状态
- `ShouldUseFloat16ForWideGamut()` → HDR 关闭时自动选 Float16 广色域路径

### 4.4 ColorSpaceConverter (色域矩阵)

| 矩阵 | 用途 |
|------|------|
| `SrgbToBt2020` | scRGB (BT.709) → BT.2020 线性 |
| `SrgbToDisplayP3` | scRGB → Display P3 线性 |
| `SrgbToAdobeRgb` | scRGB → AdobeRGB 线性 |

### 4.5 数据流验证

```
HDR 开启 + BT.2020:
  scRGB float[] → ColorSpaceConverter.ConvertScrgbToTarget("BT2020")
    → FormatHelper.HdrToPq16() → PQ 16-bit
    → CICP { primaries=BT.2020, transfer=PQ }
    → 编码器 → 文件 ✅

HDR 关闭 + BT.2020:
  WGC Float16 → PrepareFloat16WithIcc()
    → ColorSpaceConverter.ConvertFloat16ToSdrBgra()
    → 色域矩阵 → 色调映射 → sRGB gamma → BGRA8
    → 嵌入 BT.2020 ICC → 编码器 → 文件 ✅

HDR 关闭 + sRGB:
  WGC BGRA8 → 传统 SDR 路径 → 编码器 → 文件 ✅
```

---

## 5. 编码管线 (FormatEncoders + CapturePipelineService)

### 5.1 编码器矩阵

| 格式 | 后端 | HDR | 质量参数 | 色度控制 | 位深 | 备注 |
|------|------|-----|---------|---------|------|------|
| PNG | 托管编码器 | ✅ | 无损 | ✅ (444) | 8/10/12/16 | ICC/CICP 互斥 |
| JPEG LI | jpegli P/Invoke | ❌ | butteraugli 0.5-3.0 | ✅ 420/422/444 | 8 | 回退→ManagedJpegEncoder |
| JPEG Gain Map | jpegli + 增益图 | ✅ | butteraugli 距离 | ❌ | 8 | HDR: 增益比+ICC 嵌入 |
| JPEG XL | JxlNet | ✅ | butteraugli 0.1-4.0 | ❌ (API 内部) | 8/10/12 | HDR: PQ 16-bit |
| AVIF | MFT>NVENC>QSV>libaom | ✅ | CRF 0-63 | ✅ 420/422/444 | 8/10/12 | HDR: PNG 中转 |
| WebP | libwebp/cwebp | ❌ | 质量 50-100 | ❌ (简单 API) | 8 | 回退→PNG |
| TIFF | 托管编码器 | ✅ | 无损 | ❌ | 8/16 | HDR: 16-bit PQ |
| BMP | 托管编码器 | ❌ | 无损 | ❌ | 8 | — |

### 5.2 AVIF 后端优先级

```
Auto → MFT (系统硬件) > NVENC (NVIDIA) > QSV (Intel) > libaom (CPU)
```

每个后端都有 **崩溃隔离** (`NativeEncoderGuard.TryEncode`) 和 **自动回退链**。
最终回退: libaom → PNG 保底，确保截图不丢失。

### 5.3 NVENC GPU 纹理直通

```
WGC 帧纹理 (D3D11)
  → PooledSession.CacheTextureGpuCopy → Default 纹理缓存
  → CaptureResult.GpuTexture
  → NvencAvifBackend.EncodeAsync
    → NvEncRegisterResource + MapInputResource
    → NVENC 直接读取 D3D11 纹理，跳过 CPU 回读
    → 4K 节省约 6-15ms GPU→CPU 同步等待
```

### 5.4 HDR 编码路径

```
scRGB float[] → LinearToPQ (ST.2084) → ushort[] PQ16 → RGBA16→BGRA16 → 16-bit PNG 中间文件 → 格式编码
```

| 格式 | HDR 路径 |
|------|---------|
| PNG HDR | 直接 16-bit PNG + sBIT (实际位深) + CICP (BT.2020 + PQ) |
| AVIF HDR | 16-bit PNG 中转 + avifenc 进程调用 |
| JXL HDR | NativeJxlEncoder.EncodeHdr (PQ 16-bit) |
| TIFF HDR | 16-bit PQ 像素 → 写入 TIFF |

### 5.5 FormatHelper 辅助

| 方法 | 用途 |
|------|------|
| `HdrToPq16` | scRGB → 色域矩阵 → PQ 16-bit (精确 10→16 bit 映射) |
| `Rgba16ToBgra16Bytes` | RGBA 16-bit → BGRA 16-bit 大端字节序 |
| `GetColorMetadata` | ICC/CICP 互斥策略 |
| `ToSdr` | HDR→SDR 转换 (FloatToSRgbBytes + 格式参数) |

---

## 6. PixelOps 多 ISA 加速

### 6.1 ISA 层级

| ISA 层级 | 条件 | 应用 |
|----------|------|------|
| AVX-512 BW (512-bit) | 大数据集 >128KB | FixAlphaChannel, ConvertHalfToFloatRow |
| AVX-512 VL (256-bit) | Ice Lake+, Zen 4+ | BgraToScrgbLinearFast |
| AVX2 (256-bit) | Haswell+, Zen 1+ | 通用向量路径 |
| ARM64 NEON | 预留实现 | FixAlphaChannelNeon |
| JIT 自动向量化 | 通用回退 | 标量路径 |

### 6.2 关键函数

| 函数 | SIMD 加速 | 用途 |
|------|----------|------|
| `FixAlphaChannel` | AVX-512/AVX2/NEON | WGC alpha=0 → 255 |
| `BgraToScrgbLinearFast` | AVX-512/AVX2 | sRGB byte → linear float (LUT + SIMD) |
| `ConvertHalfToFloatRow` | AVX-512/AVX2 | Float16 → Float32 (D3D11 读回) |
| `DownsampleToGray` | AVX2 | 灰度缩略图 |
| `EdgeProjections` | AVX2 | 边缘检测投影 |

---

## 7. MainWindow 6 条保存路径 (v0.3.0-beta)

| # | 方法 | 触发方式 | 特点 |
|---|------|---------|------|
| 1 | `EncodeAndSaveAsync` | 选区确认 | 委托 CapturePipelineService，完整 ICC + 编码 |
| 2 | `SilentCaptureAllAsync` | 静默热键 | 全桌面，右下角 Toast |
| 3 | `EncodeAndCopyAsync` | 选区复制 | 仅 PNG 到剪贴板 |
| 4 | `StartSelectionCapture` | 选区热键/按钮 | WGC → 覆盖层 → 4 种动作 |
| 5 | `OnCaptureNow` | 全屏按钮 | 单显示器，HDR/SDR 双路径 |
| 6 | `CaptureAndOcrFromPixelsAsync` | OCR/翻译 | 调用 OCR 管线 |

### 架构修复 (v0.3.0-beta)

| 问题 | 状态 | 修复方式 |
|------|------|---------|
| 代码重复 (4条路径) | ✅ **已修复** | 统一委托 `CapturePipelineService.EncodeAndSaveAsync` 重载 |
| 无 CancellationToken | ✅ **已修复** | 所有重载接受 CancellationToken |
| HDR 路径不一致 | ✅ **已修复** | 统一通过 CapturePipelineService 走 HDR/SDR 双路径 |
| BuildEncodingSettings 去重 | ✅ **已修复** | MainWindow 委托给 CapturePipelineService |

---

## 8. 动图录制管线 (AnimationRecorder)

### 8.1 架构

| 组件 | 机制 |
|------|------|
| 共享 WgcCaptureService | 不创建独立 D3D11 设备 |
| 有界 Channel | 30 帧上限, DropOldest 防 OOM |
| PeriodicTimer | 精确帧定时，替代 Thread.Sleep |
| 帧差异检测 | 5000 点采样，阈值过滤静态帧 |
| ICC 色彩管理 | 每帧写入前经 CapturePipelineService.PreparePixelsWithIcc |
| ffmpeg 进程 | 编码 Animated WebP / APNG / AVIF / GIF |

### 8.2 数据流

```
StartRecording()
  → RecordLoop (PeriodicTimer)
    → TryGetLatestFrame() (无锁读取)
    → HasChange() 差异检测
    → _frameChannel.Writer.TryWrite() (有界缓冲)

StopAndEncodeAsync()
  → 读取 Channel 所有帧
  → 每帧 PreparePixelsWithIcc() → 写入临时 PNG
  → ffmpeg 编码动图
  → 回退: 第一帧 PNG 保底
```

---

## 9. OCR/翻译管线

### 9.1 OCR 引擎

| 引擎 | 后端 | 用途 |
|------|------|------|
| ONNX DirectML | PP-OCRv6 | GPU 加速 OCR |
| ONNX CPU | PP-OCRv6 | CPU 回退 |
| Windows OCR | Windows.Media.Ocr | 系统 OCR |

### 9.2 翻译

- LLM 翻译 (OpenAI/Claude/Gemini/DeepSeek 兼容 API)
- 独立预览窗口 `OcrPreviewWindow`，文字点对点覆盖

---

## 10. 应用启动流程 (AppServices)

```
App.OnLaunched
  └─ AppServices.Initialize() [Microsoft.Extensions.DependencyInjection]
       ├─ LogService 初始化 (文件日志, 按日轮转)
       ├─ DI 容器注册 (ServiceCollection)
       │   ├─ SettingsService (单例, JSON 持久化)
       │   ├─ CapabilityService (单例, HDR/ACM/ICC/编码器检测)
       │   ├─ CapturePipelineService (单例, ICC + 编码调度)
       │   ├─ WgcCaptureService (单例, D3D11 + 会话池, 可选)
       │   └─ GpuToneMapper (单例, 着色器加载, 可选)
       ├─ WGC/GPU 初始化 (失败不阻塞启动)
       │   ├─ NvencAvifBackend.SetSharedD3DDevice() 共享设备
       │   └─ 日志记录 GPU 色调映射可用性
       ├─ BuildServiceProvider()
       └─ MainWindow 构造
            ├─ 托盘图标 + 热键注册
            ├─ 能力检测 (异步延迟)
            ├─ OCR 引擎初始化 (后台)
            └─ WGC 会话预热
```

### 服务生命周期

| 服务 | 生命周期 | 可 null |
|------|---------|---------|
| SettingsService | 单例 | ❌ |
| CapabilityService | 单例 | ❌ |
| CapturePipelineService | 单例 | ❌ |
| WgcCaptureService | 单例 | ✅ (初始化失败时) |
| GpuToneMapper | 单例 | ✅ (着色器缺失时) |

---

## 11. 并发模型

### 11.1 锁层级

```
s_captureLock (SemaphoreSlim 1,1) — 防止并发截图
  └─ _poolLock — 保护会话池字典
  └─ _d3dContextLock (实例级) — 保护 D3D11 ImmediateContext
       └─ _gpuLock (实例级, PooledSession 共享) — 保护纹理拷贝/读取
            └─ _frameLock (PooledSession 内) — 保护帧缓存读写
```

### 11.2 线程模型

| 线程 | 用途 | 同步方式 |
|------|------|---------|
| WGC FrameArrived 回调 | 帧写入 | 双缓冲交换 + _frameLock |
| UI 线程 (MainWindow) | 覆盖层/标注 | DispatcherQueue |
| 截图线程 | 读取帧 + 编码 | Task.Run + CancellationToken |
| 录制线程 | 动图帧读取 | Channel 无锁读取 |
| 编码线程 | 格式编码 | Task.Run + PowerManager 阻止睡眠 |

---

## 12. 测试覆盖

### 12.1 测试总览

```
测试套件              通过/总数   结果
─────────────────────────────────────────
单元测试              30/30      ✅ 100%
综合可用性测试       148/148     ✅ 100%
编码集成测试          14/14      ✅ 100%
─────────────────────────────────────────
总计                 192/192     ✅ 100%
```

### 12.2 测试覆盖的组件

| 组件 | 测试项数 | 覆盖详情 |
|------|---------|---------|
| PixelOps (多ISA加速) | 12 | FixAlphaChannel, BgraToScrgbLinear, 降采样, ISA检测 |
| ToneMapper (色调映射) | 13 | 3种算法, 曝光, 融合内核, 边界值, 确定性 |
| ColorProfileProvider (ICC) | 13 | 5种标准ICC, 色彩空间映射, 缓存一致性 |
| GamutMapper (色域映射) | 9 | 3种算法, ICC烘焙, 4种目标色域 |
| AnnotationManager (标注) | 7 | 8种图层, 撤销/重做, 边界条件 |
| FormatEncoders (编码器) | 28 | 7格式创建, SDR组合, HDR输出, 质量范围 |
| JPEG专项 | 13 | 7种质量, 3种色度, ICC嵌入, SOI/EOI验证 |
| PNG专项 | 7 | 8/10/12/16-bit, ICC/CICP互斥 |
| BMP专项 | 7 | 6种尺寸, BM签名验证 |
| Gain Map | 5 | Gray/RGB模式, 质量设置 |
| FormatHelper | 11 | HdrToPq16, Rgba16ToBgra16, GetColorMetadata, ToSdr |
| 边界条件 | 9 | 0x0尺寸, 空数据, 极端质量值 |
| 综合管线 | 3 | HDR→SDR, ICC/CICP策略 |
| 编码集成(全尺寸) | 14 | 端到端编码+解码验证 |

---

## 13. 系统依赖

| 依赖 | 状态 | 解决方案 |
|------|------|---------|
| WebP 编码 | ✅ **已内置** | cwebp.exe (1.5.0 x64) 嵌入在 Resources/cwebp.exe |
| AVIF 编码 | ⚠ 部分依赖 | avifenc 自动检测 native/ 目录和 PATH |
| ffmpeg (动图录制) | ⚠ 部分依赖 | 自动检测 PATH；不可用时回退到单帧 PNG |
| Intel QSV 不可用 | ⚠ 可接受 | 自动回退到 libaom (CPU) |
| NVENC 加载但无编码 | ⚠ 可接受 | 自动回退到 libaom (CPU) |

**内置机制**：`NativeLibraryResolver` 在 `AppServices.Initialize()` 时自动初始化，
提取嵌入的原生资源到 `native/` 目录，并将该目录加入 PATH。

---

## 14. 已知问题

| 问题 | 严重性 | 说明 |
|------|--------|------|
| JXL HDR 回退 | P3 | NativeJxlEncoder 仅支持 8-bit，需等 JxlNet 更新 |
| Linux 后端 | P4 | PlatformFactory 5 处 TODO (预留) |
| JPEG LI DCT 归一化 | P4 | AAN DCT /64 近似，非 T.81 精确，视觉可接受 |

---

## 15. NuGet 依赖

| 包 | 版本 | 项目 |
|----|------|------|
| Microsoft.WindowsAppSDK | 2.3.1 | App |
| Microsoft.Graphics.Win2D | 1.4.0 | App |
| Vortice.Direct3D11 | 3.8.3 | Core |
| Vortice.DXGI | 3.8.3 | Core |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | Core |
| JxlNet | 0.11.2.2 | Core |
| System.Drawing.Common | 10.0.10 | Test |

---

## 16. 构建与发布

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
  └─ AppServices.Initialize()  [Microsoft.Extensions.DependencyInjection]
       ├─ ServiceCollection 注册
       │   ├─ SettingsService (单例, 加载 JSON 配置)
       │   ├─ CapabilityService (单例, HDR/ACM/ICC/编码器检测)
       │   ├─ CapturePipelineService (单例, ICC + 编码调度)
       │   ├─ WgcCaptureService (单例, 可选, D3D11 设备 + 会话池)
       │   └─ GpuToneMapper (单例, 可选, 着色器加载)
       └─ BuildServiceProvider()
```

MainWindow 通过 `AppServices.*` 静态门面访问服务（底层为 IServiceProvider）。
DI 容器自动管理 IDisposable 单例的生命周期（Shutdown 时统一释放）。

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
