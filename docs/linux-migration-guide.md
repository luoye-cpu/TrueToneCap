# TrueToneCap — Linux 迁移指南

> 目标平台: Linux x64 (AMD/Intel) + Linux ARM64 (Snapdragon X / Apple M via Asahi)
> 状态: 接口预留阶段 (v0.2.0)

---

## 1. 迁移策略

### 1.1 分层隔离

```
TrueToneCap.Core (跨平台)
├── Platform/           ← 抽象接口 (已完成)
│   ├── ICaptureBackend.cs
│   ├── IGpuRenderer.cs
│   ├── IPlatformServices.cs
│   └── PlatformFactory.cs
├── Processing/         ← 纯算法 (已跨平台)
│   ├── ToneMapper.cs   (CPU, 无平台依赖)
│   └── PixelOps.cs     (SIMD, 已支持 NEON)
├── Encoding/           ← 大部分跨平台
│   ├── FormatEncoders.cs (Magick.NET 跨平台)
│   └── *Native.cs      (P/Invoke, 需 Linux .so)
├── ColorManagement/    ← 需替换 WCS → lcms2
└── Annotation/         ← 纯逻辑 (已跨平台)

TrueToneCap.App (Windows 专属, 未来拆分)
├── Services/WgcCaptureService.cs  → ICaptureBackend
├── Services/HotkeyManager.cs      → Linux: X11 XGrabKey / Wayland
├── Services/TrayIconManager.cs    → Linux: libappindicator / StatusNotifierItem
└── UI (WinUI 3)                   → Linux: GTK4 / Avalonia / MAUI
```

### 1.2 迁移优先级

| 阶段 | 内容 | 依赖 |
|------|------|------|
| P0 | Core 项目多目标编译 (`net10.0;net10.0-windows`) | csproj 条件编译 |
| P1 | 捕获后端: KMS/DRM (无合成器) 或 PipeWire (Wayland) | libdrm, libpipewire |
| P2 | GPU 渲染: Vulkan 色调映射 | Vulkan SDK, Silk.NET/Vortice.Vulkan |
| P3 | 色彩管理: lcms2 + colord | lcms2, D-Bus |
| P4 | UI 框架: Avalonia / GTK4 | Avalonia 11+ |
| P5 | 系统集成: 热键/托盘/通知 | X11/libappindicator/libnotify |

---

## 2. 平台依赖映射

| Windows 组件 | Linux 替代 | 备注 |
|-------------|-----------|------|
| WGC (Windows.Graphics.Capture) | KMS/DRM + GBM / PipeWire | Wayland 必须用 PipeWire |
| D3D11 + HLSL | Vulkan + GLSL / OpenGL 4.6 | Silk.NET 或 Vortice.Vulkan |
| WCS (mscms.dll) | lcms2 + colord (D-Bus) | ICC v2/v4 兼容 |
| user32.dll (窗口检测) | X11 _NET_CLIENT_LIST / wlroots | Wayland 无全局窗口枚举 |
| RegisterHotKey | X11 XGrabKey / Wayland portal | Wayland 需 xdg-desktop-portal |
| Shell_NotifyIcon (托盘) | StatusNotifierItem / libappindicator | KDE/GNOME 均支持 |
| AppNotificationManager | libnotify / D-Bus Notifications | freedesktop.org 规范 |
| NVENC / QSV (AVIF) | VA-API / V4L2 M2M | NVIDIA: VA-API wrapper |
| Magick.NET | Magick.NET (跨平台) ✅ | 已支持 linux-x64/arm64 |
| ONNX Runtime DirectML | ONNX Runtime (CPU/CUDA/TensorRT) | 替换 DirectML EP |
| JxlNet | JxlNet (跨平台) ✅ | 需 linux .so |

---

## 3. csproj 多目标编译 (P0)

未来 `TrueToneCap.Core.csproj` 应改为:

```xml
<PropertyGroup>
  <TargetFrameworks>net10.0;net10.0-windows10.0.26100.0</TargetFrameworks>
</PropertyGroup>

<!-- Windows 专属依赖 -->
<ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
  <PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
  <PackageReference Include="Vortice.DXGI" Version="3.8.3" />
  <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
</ItemGroup>

<!-- Linux 专属依赖 (未来) -->
<ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
  <PackageReference Include="Silk.NET.Vulkan" Version="2.x" />
  <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.24.4" />
</ItemGroup>

<!-- 跨平台依赖 -->
<ItemGroup>
  <PackageReference Include="Magick.NET-Q16-HDRI-AnyCPU" Version="14.15.0" />
  <PackageReference Include="JxlNet" Version="0.11.2.2" />
</ItemGroup>
```

代码中使用条件编译:

```csharp
#if WINDOWS
using Vortice.Direct3D11;
// Windows 专属实现
#else
// Linux 实现或 throw new PlatformNotSupportedException()
#endif
```

---

## 4. 已完成准备 (v0.2.0)

- [x] `Platform/ICaptureBackend.cs` — 捕获后端抽象
- [x] `Platform/IGpuRenderer.cs` — GPU 渲染抽象
- [x] `Platform/IPlatformServices.cs` — 窗口/色彩/元数据/能力检测抽象
- [x] `Platform/PlatformFactory.cs` — 运行时平台检测 + 工厂
- [x] `PixelOps.cs` — ARM64 NEON 已实现 (Snapdragon X 就绪)
- [x] `ToneMapper.cs` — 纯 CPU 算法，无平台依赖
- [x] `AnnotationManager.cs` — 纯逻辑，无平台依赖
- [x] DI 容器 — 服务通过接口注入，便于替换实现

---

## 5. 注意事项

1. **Wayland 限制**: 无全局窗口枚举/截图 API，必须通过 xdg-desktop-portal (D-Bus) 或 PipeWire
2. **HDR on Linux**: 尚不成熟，KMS 支持 HDR10 metadata 但桌面合成器支持有限 (KWin 6.2+, Mutter 47+)
3. **ARM64 GPU**: Snapdragon X (Adreno) Vulkan 驱动尚不完善，优先 OpenGL ES 回退
4. **Magick.NET**: 已原生支持 linux-x64 和 linux-arm64，无需额外工作
5. **ONNX Runtime**: 替换 DirectML EP 为 CPU EP (通用) 或 CUDA EP (NVIDIA dGPU)
