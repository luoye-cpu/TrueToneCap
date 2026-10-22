# TrueToneCap — 架构设计文档

> ⚠️ **本文部分内容已过时（2026-09-14 核实），阅读时请注意：**
>
> | 项 | 文中标注 | 实际 |
> |---|---------|------|
> | 版本 | `v0.3.1-beta` | **v0.3.4-beta** |
> | WindowsAppSDK | `2.3.1` | **2.4.0** |
> | 测试总数 | `192/192` | **373 项断言 / 8 个套件**（2026-09-14 实测通过；<br>`roadmap-v0.4.md` 的 `318` 亦不准确） |
> | 第 1 节项目结构 | — | **6 处文件引用已失效**（见下表，已逐条标注去向） |
> | 第 7 节保存路径 | `SilentCaptureAllAsync` | 现为 `SilentCapture()`（`MainWindow.xaml.cs:823`） |
>
> **第 1 节项目结构树中以下文件当前不存在**（2026-09-14 逐条核实去向）：
>
> | 文中引用 | 现状 | 现在的承担者 |
> |---|---|---|
> | `Directory.Build.targets` | 已改名/拆分 | `Directory.Build.props` + `Directory.Packages.props` |
> | `Processing/GpuEffectProcessor.cs` | **类已删除** | 一度写成"由 `GpuToneMapper.cs` 承担"——那个类本身也于 2026-10-01 被删（见下三行），实际的 HDR→SDR 只有 `Processing/ToneMapper.cs`（CPU） |
> | `Processing/GpuToneMapper.cs` | **文件已删除**（2026-10-01） | 实际的 HDR→SDR 只有 `Processing/ToneMapper.cs`（CPU）。本文**所有图与表**（第 1-2 节的管线总览框、第 3.2 节、结构树、生命周期与服务单例表）里凡出现 `GpuToneMapper` 之处，一律读作"曾存在"，不是当前实现 |
> | `Processing/ShaderLoader.cs` | **文件已删除**（2026-10-01） | 它唯一的调用方就是上面那个类；现在"按名字找字节码"的活路是 `Services/HdrCaptureWindow.cs` 里的 `FindCso` |
> | `Shaders/ToneMapping.hlsl` | **文件已删除**（2026-10-01） | `ShaderCompiler.cs` 的清单曾写它"✅ 存在，运行时消费"，那句是假的。三张名单（`ShaderCompiler.cs` / `Shaders/*.hlsl` / `App.csproj` 的 CompileShaders Inputs）互相对账 + "每条都要有剥注释后的 `.cso` 消费点"，锁在 `CorePipelineTests.Test_ShaderList_HasNoOrphanEntry` |
> | `ColorManagement/GamutMapper.cs` | **类已删除** | 色域映射逻辑上移到 App 层（`MainWindow.ColorManagement.cs` 等） |
> | `Encoding/ManagedJpegEncoder.cs` | **类已删除** | `JpegLiNative.cs`（cjpegli.exe）+ `JpegGainMapEncoder.cs` |
> | `Encoding/NvencAvifBackend.cs` | 文件删除但**类仍在** | 已合并进 `Encoding/FormatEncoders.cs:385` |
> | `Shaders/MosaicEffect.hlsl` | **文件已删除** | 马赛克改为 CPU 光栅化（`Services/AnnotationRasterizer.cs`） |
>
> **文档原未列出、当前确实存在**（已在下方结构树中补入 `FormatEncoders.cs`、
> `OverlayComposite.hlsl`、两个 `.props`；`ShaderLoader.cs` 曾补入、2026-10-01 已删除；**仍未补入**的有）：
> `Encoding/IccStore.cs`、`Encoding/NativeEncoderGuard.cs`、
> `Encoding/ManagedTiffEncoder.cs`、
> `Encoding/AvifHardwareProbe.cs`、`Encoding/GpuCapability.cs`、`src/*/packages.lock.json`。
> （这一行原先还把 `Encoding/ManagedBmpEncoder.cs` 算作"当前确实存在"：它已于 `v0.3.6-beta`(v0.3.6) 删除，
> 且 `CorePipelineTests.cs:4063-4065` 有一条反向断言禁止该文件与 `"BMP"` 字面量再出现 —— 2026-10-17 核对。）
>
> **结构树中带 `✗` 前缀的条目表示「该文件已不存在」**，其后注明现在的承担者，勿再按原名查找。
>
> 其余章节（管线流程、色彩管理、编码架构）经抽查仍与实际代码一致，可继续参考。

> **目标系统**：Windows 11 24H2+ | **技术栈**：C# 14 / WinUI 3 / .NET 11 / WindowsAppSDK 2.5.1（2026-10-07 由 2.4.0 升级，见 `Directory.Packages.props:22`）/ Vortice.D3D11
>
> **管线审查日期**：2026-08-01（内容基准 v0.3.1-beta）| **总测试**：192/192 ✅ 100%（待核实）
>

---

## 0. 完整管线总览 (v0.3.1-beta)

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
│          ├─ PNG (托管)     8/10/12/16-bit  ICC/CICP 共存                    │
│          ├─ JPEG LI (jpegli) 8-bit  butteraugli 0.5-3.0                    │
│          ├─ JPEG XL (JxlNet) 8/10/12-bit  HDR: PQ 16-bit                  │
│          ├─ AVIF (NVENC>Qsv(=系统MFT)>SVT-AV1>libaom>avifenc) 8/10-bit     │
│          │    ^ 2026-09-20 更正：原写 "MFT>NVENC>QSV>libaom" 顺序与成员都不对；  │
│          │      Auto 还先按色度过滤（420⇒硬件，422/444⇒libaom），HDR 走 Y4M/PNG 中转 │
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
├── Directory.Build.props                 # 全仓库公共 MSBuild 属性（唯一声明处）
├── Directory.Packages.props              # NuGet CPM：依赖版本唯一声明处
│                                         # （原 Directory.Build.targets 已拆分为以上两个 .props）
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
    │   │   ✗ GpuToneMapper.cs          # 【已删除 2026-10-01】生产零调用，HDR→SDR 只有 ToneMapper（CPU）
    │   │   ✗ ShaderLoader.cs           # 【已删除 2026-10-01】唯一调用方是上面那个类；活路是 HdrCaptureWindow.FindCso
    │   │   ✗ GpuEffectProcessor.cs       # 【已移除】GPU 后处理没有接手方（原先写的"由 GpuToneMapper 承担"也已删除）
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
    │   │   ✗ ManagedJpegEncoder.cs       # 【已移除】JPEG 现由 JpegLiNative.cs (cjpegli) 承担
    │   │   ├── GpuCapability.cs          # GPU 硬件编码器检测
    │   │   ├── AvifHardwareProbe.cs      # AVIF 硬件探测
    │   │   ├── NvEncoderNative.cs        # NVENC P/Invoke
    │   │   ├── FormatEncoders.cs         # 编码器注册表 + NvencAvifBackend 等实现
    │   │   ✗ NvencAvifBackend.cs         # 【已合并且改名】类仍在，位于 FormatEncoders.cs:385
    │   │   └── MftEncoderNative.cs       # MFT 编码器（QSV/硬件 AV1 走系统 MFT；原 QsvEncoderNative.cs 已于 2026-09-19 删除：IsAvailable 恒 false、零调用方）
    │   ├── Annotation/
    │   │   ├── AnnotationLayer.cs        # 8 种标注图层
    │   │   ├── AnnotationManager.cs      # 命令模式撤销/重做
    │   │   └── Shapes/                   # 具体形状实现
    │   ├── ColorManagement/
    │   │   ├── ColorProfileProvider.cs   # WCS ICC 获取 + sRGB 内置 + ColorSpaceConverter
    │   │   ✗ GamutMapper.cs              # 【已移除】色域映射上移至 App 层 MainWindow.ColorManagement.cs
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
    │   │   ✗ ToneMapping.hlsl          # 【已删除 2026-10-01】清单里挂着"运行时消费"而没人加载它
    │   │   ├── FullscreenVS.hlsl         # 全屏三角形 VS
    │   │   ├── OverlayComposite.hlsl     # 覆盖层合成 PS（文档中原本缺失此条）
    │   │   ✗ MosaicEffect.hlsl           # 【已移除】马赛克改由 CPU 光栅化 AnnotationRasterizer.cs
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

## 3. 色调映射管线（ToneMapper，CPU；GPU 分支 2026-10-01 已删除）

### 3.1 着色器

| 文件 | 类型 | 用途 |
|------|------|------|
| `FullscreenVS.hlsl` | vs_5_0 | SV_VertexID 全屏三角形（无 VB / IB） |
| `OverlayComposite.hlsl` | ps_5_0 | 覆盖层合成（HDR 窗的 GPU 合成路径） |
| ~~`ToneMapping.hlsl`~~ | ~~ps_5_0~~ | **已删除（2026-10-01）** —— 唯一加载方 `GpuToneMapper` 自 2026-09-16 起不再构造，属"清单里有、运行时没人加载"的死资产 |
| ~~`MosaicEffect.hlsl`~~ | — | **已移除** —— 马赛克改由 CPU 光栅化 |
| ~~`CopyTexture.hlsl`~~ | — | **从未存在** —— 当年 `HdrCaptureWindow` 找不到它会回退去加载 `ToneMapping.hlsl.cso`，而回退来的 PS 从没被 `PSSetShader` 绑定（2026-09-14 核实） |

编译：`Shaders/CompileShaders.ps1` 用 **fxc**（不是 dxc）产出 SM5.0/DXBC —— dxc 强制 SM6/DXIL，
D3D11 `CreateShader` 会报 `E_INVALIDARG`；构建期与 `ShaderCompiler.cs` 的 Profile 必须同档（2026-09-14 对齐）。

### 3.2 ~~GpuToneMapper (GPU 路径)~~ —— 已于 2026-10-01 删除

这一小节原先描述的是 `Core/Processing/GpuToneMapper.cs`：输入 float[] HDR scRGB 线性、输出 BGRA8、
`ShaderLoader` 两级回退取字节码、纹理按尺寸池化、常量缓冲区 4 个字段、失败回退 CPU。
**该类连同 `ShaderLoader.cs` 与 `ToneMapping.hlsl` 一起删除**，理由不是性能选型，是它从没被执行过：
`AppServices.cs` 在 2026-09-16 就停止构造它并写明"【死代码】…属性全仓没有任何调用者"，
而"清单写着运行时消费"这句假话一直留在 `ShaderCompiler.cs` 的注释里。
实际的 HDR→SDR 只有下一小节那条 CPU 路径（`ToneMapper`）。
今天的锁：`CorePipelineTests.Test_ShaderList_HasNoOrphanEntry`（着色器清单每条都要有剥注释后的消费点，
且清单/磁盘/csproj Inputs 三张名单互相对账）。要重做 GPU 色调映射，请从
`Services/HdrCaptureWindow.cs` 的活路（自建交换链 + `FindCso`）起步，别复活"启动时构造、运行中没人读"那个形状。

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
| PNG | 托管编码器 | ✅ | 无损 | ✅ (444) | 8/10/12/16 | ICC/CICP 共存 |
| JPEG LI | jpegli P/Invoke | ❌ | butteraugli 0.5-3.0 | ✅ 420/422/444 | 8 | `JpegLiNative`；<br>原「回退→ManagedJpegEncoder」已失效，该类已移除 |
| JPEG Gain Map | jpegli + 增益图 | ✅ | butteraugli 距离 | ❌ | 8 | HDR: 增益比+ICC 嵌入 |
| JPEG XL | JxlNet | ✅ | butteraugli 0.1-4.0 | ❌ (API 内部) | 8/10/12 | HDR: PQ 16-bit |
| AVIF | NVENC > Qsv(=系统 MFT) > SVT-AV1 > libaom > avifenc（Auto 先按色度过滤：420⇒硬件、422/444⇒libaom） | ✅ | CRF 0-63 | ✅ 420/422/444 | 8/10（硬件固定 10-bit；12-bit 从未实现） | HDR: PNG/Y4M 中转 |
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
| `GetColorMetadata` | ICC/CICP 共存策略 |
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
| 2 | `SilentCapture()`（原 `SilentCaptureAllAsync`） | 静默热键 | 全桌面，右下角 Toast |
| 3 | `EncodeAndCopyAsync` | 选区复制 | 复用完整保存管线 (BuildEncodingSettings + PreparePixelsWithIcc + 编码), 输出到临时目录, 与正式保存完全一致 |
| 4 | `StartSelectionCapture` | 选区热键/按钮 | WGC → 覆盖层 → 4 种动作 |
| 5 | `OnCaptureNow` | 全屏按钮 | ⚠ **2026-09-20 实测不可达**：`MainWindow.xaml` 里对 `CaptureNow` 零命中，
|   |   | 即该入口没有 XAML 绑定 ⇒ 方法还在（`MainWindow.xaml.cs:2221`）但按不到。接线还是删除待拍板（交接 §32.9-6）|
| 6 | `CaptureAndOcrFromPixelsAsync` | OCR/翻译 | 调用 OCR 管线 |

### 架构修复 (v0.3.0-beta)

| 问题 | 状态 | 修复方式 |
|------|------|---------|
| 代码重复 (4条路径) | ✅ **已修复** | 统一委托 `CapturePipelineService.EncodeAndSaveAsync` 重载 |
| 无 CancellationToken | ✅ **已修复** | 所有重载接受 CancellationToken |
| HDR 路径不一致 | ✅ **已修复** | 统一通过 CapturePipelineService 走 HDR/SDR 双路径 |
| 剪贴板复制色彩不一致 | ✅ **已修复** (2026-08-09) | `EncodeAndCopyAsync` 不再独立简化实现 (曾漏传 ACM → System 解析错误 + 固定 PNG), 改为复用 `EncodeAndSaveAsync` 输出到临时目录, 复制完整成品 |
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
       │   └─ GpuToneMapper (单例, 着色器加载, 可选)   ← 2026-10-01 已删除，从未被调用
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
| ~~GpuToneMapper~~ | ~~单例~~ | — | **2026-10-01 已删除**（从未构造、属性无人读；见第 3.2 节） |

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
| GamutMapper (色域映射) | ⚠ 2026-09-20 更正：该类**已删除**（本文 `:19`/`:181` 已自认），此行为历史计数；色域映射现由 `ColorManagement/ColorSpaceConverter` + `FormatHelper.PrepareSdrLinearFloat` 承担，目标色域界面只 3 项（跟随显示器/sRGB/BT.2020）|

| AnnotationManager (标注) | 7 | 8种图层, 撤销/重做, 边界条件 |
| FormatEncoders (编码器) | 28 | 7格式创建, SDR组合, HDR输出, 质量范围 |
| JPEG专项 | 13 | 7种质量, 3种色度, ICC嵌入, SOI/EOI验证 |
| PNG专项 | 7 | 8/10/12/16-bit, ICC/CICP共存 |
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
- 内置 sRGB ICC：一律由 `BuildValidSRgbIcc` 现场生成（v2.1）。**不读**系统
  `sRGB Color Space Profile.icm` —— 那份的 `cprt`/`creator` 是第三方（HP）的，会以 iCCP/APP2/AVIF
  `colr(prof)` 的形式随用户产物分发，且模板取自本机意味着换机器产物字节就变（2026-09-20 合规修正）

### 6.2 色彩管线

```
显示器 ICC → ACES Perceptual 烘焙 → 目标色域
  ├─ sRGB 目标：不嵌入 ICC
  └─ 广色域目标（P3/AdobeRGB/BT.2020）：嵌入标准 ICC
```

标准 ICC primaries 为 **D50 适应 + 白点归一** 的 XYZ（`PrimariesToD50Xyz`：
xy→XYZ → 白点缩放 → Bradford D65→D50），TRC 保留 sRGB EOTF（目标 primaries +
sRGB gamma，与像素编码自洽）。

### 6.3 ACM 集成（2026-08-09 方案A）

ACM（Auto Color Management）启用时，系统已接管显示器色彩管理：

- **Float16 路径**（WGC scRGB）：`FloatToSRgbBytes` 不转换色域矩阵，输出恒为
  sRGB 色域（BT.709 primaries + sRGB gamma）→ **恒嵌 sRGB**（不嵌广色域 ICC，
  否则像素/ICC 矛盾）。应用输出 sRGB 内容，由 ACM 系统映射到显示器广色域。
- **SDR byte[] 路径**（WGC 显示器色域像素）：
  - 目标 = 显示器原生色域 / sRGB → 跳过烘焙，嵌标准 ICC（或 sRGB 不嵌）
  - 目标 ≠ 显示器色域 → 仍做烘焙（显示器 ICC → 目标色域），避免像素/ICC 矛盾

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
       │   └─ GpuToneMapper (单例, 可选, 着色器加载)   ← 2026-10-01 已删除，从未被调用
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

---

## 12. ACES 色调映射 — 可行性说明 (2026-08-08 移除)

### 背景
ACES 色调映射曾作为 HDR→SDR 的参考级曲线（`ToneMapMode.Aces`），包含：
- **AP1 广色域工作空间**（scRGB → 3×3 → ACEScg 处理 → 逆矩阵回 sRGB）
- **参考级 ODT**（`ODT.Academy.RGBmonitor_100nits_dim.ctl`，对比度 S 曲线 + 饱和度补偿）
- **RRT**（Narkowicz 2015 拟合，肩部滚降不截断高光）

### 移除原因 (2026-08-08)
1. **生产零激活**：UI 无色调映射模式选择器，所有路径恒用 `SegmentedReinhard`（GainMap 同款）。
2. **一致性架构确立**：2026-08-08 统一所有格式 HDR→SDR 为 `SegmentedReinhardMap` 单一核心，
   GainMap Base 与其他格式降级数学等价。ACES 是另一条曲线，开放会破坏一致性。
3. **GainMap 约束**：增益比 `HDR/SDR` 必须在同一映射空间，ACES 的相对压缩不满足增益≥1 约束，
   GainMap 无法用 ACES。
4. **维护成本**：AP1 矩阵 + RRT/ODT 需 CPU（C#）+ GPU（HLSL）双份同步，且无激活调用方。

### 已清理内容
- `ToneMapper.cs`：`Aces` 枚举值、`ScrgbToAcesAp1`/`AcesAp1ToScrgb` 矩阵、
  `AcesToneMapCpu`/`AcesRrt`/`AcesOdt`、`ApplyToneMapping` 与融合内核的 ACES case。
- `ToneMapping.hlsl`：`SrgbToAp1`/`Ap1ToSrgb`、`ACESRrt`/`ACESOdt`/`ACESToneMap`、main 的 mode 2 分支。
- `ColorProfileProvider.cs`：`SrgbToAcesAp1`/`AcesAp1ToSrgb` 矩阵。
- 测试：4 个 ACES 用例、`ColorMatrix_SrgbToAcesAp1_Roundtrip`、所有模式数组的 `Aces` 项。
- `GamutMapper.cs`/`MainWindow`/`CapabilityService` 中 ACES 注释。

### 复活路径 (未来"色调映射风格"选项)
若需为用户提供"影视感"曲线，可零成本复活 ACES：
1. **恢复枚举值**：`ToneMapMode.Aces = 2`（HLSL 已有 mode 2 槽位映射到 SegmentedReinhard，需改回）。
2. **恢复函数**：`AcesToneMapCpu`/`AcesRrt`/`AcesOdt` + AP1 矩阵（本文档保留算法出处）。
3. **同步 GPU**：HLSL 恢复 `ACESToneMap` + main 分支。
4. **约束**：GainMap 仍强制 `SegmentedReinhard`（相对编码约束）；仅其他格式可选 ACES 风格。
5. **UI**：新增"色调映射风格"下拉（直通/标准/影视）。

### 算法参考（原文摘录存档）
- **AP1 矩阵**（ACES 1.0.3, SMPTE ST 2065-1:2012）：
  `scRGB→AP1`: `[0.613132 0.339538 0.047416; 0.070124 0.916324 0.013452; 0.020445 0.109548 0.870006]`
  `AP1→scRGB`（逆）: `[1.704579 -0.625505 -0.078038; -0.129701 1.139240 -0.009570; -0.019717 -0.128087 1.147935]`
- **RRT**（Narkowicz 2015）：`y = x(ax+b) / (x(cx+d)+e)`，`a=2.51 b=0.03 c=2.43 d=0.59 e=0.14`
- **ODT**：`boosted = x·(1 + k·(1-x)²)`，`k = 0.3 + 0.05·clamp((maxNits-100)/900, 0, 1)`；饱和度补偿 `sat=0.96`
