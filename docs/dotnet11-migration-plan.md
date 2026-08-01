# TrueToneCap .NET 11 迁移计划

> **创建日期**: 2026-08-01 | **更新日期**: 2026-08-01
> **当前版本**: v0.4.0-dev (net11.0) | **SDK**: 11.0.100-preview.6
> **目标版本**: v0.4.0 (net11.0) | **跟踪文件**: `/memories/session/dotnet11-migration.md`
>
> ✅ **阶段一已完成** — 已升级到 .NET 11 Preview 6
> ✅ **阶段二已完成** — 95 处 DllImport → LibraryImport
> ⏳ **阶段四就绪** — ReadyToRun 已验证可行
> 📋 **AOT 报告**: [aot-compatibility-report.md](aot-compatibility-report.md)

---

## 一、迁移总览

| 项目 | 当前 | 目标 |
|------|------|------|
| TargetFramework | `net10.0-windows10.0.26100.0` | `net11.0-windows10.0.26100.0` |
| C# 版本 | 13 | 14 |
| 发布方式 | Self-contained | Self-contained + ReadyToRun |
| 发布包大小 | ~550 MB | 目标 ~450 MB（阶段二）→ ~300 MB（阶段三） |

---

## 二、迁移范围

### 4 个项目的 .csproj 修改

| 项目 | 文件 | 当前 TargetFramework | 修改项 |
|------|------|---------------------|--------|
| TrueToneCap.App | `src/TrueToneCap.App/TrueToneCap.App.csproj` | `net10.0-windows10.0.26100.0` | TFM + LangVersion + NuGet |
| TrueToneCap.Core | `src/TrueToneCap.Core/TrueToneCap.Core.csproj` | `net10.0-windows10.0.26100.0` | TFM + LangVersion + NuGet |
| TrueToneCap.Test | `src/TrueToneCap.Test/TrueToneCap.Test.csproj` | `net10.0-windows10.0.26100.0` | TFM + LangVersion + NuGet |
| TrueToneCap.Tools | `src/TrueToneCap.Tools/TrueToneCap.Tools.csproj` | `net10.0-windows10.0.26100.0` | TFM + LangVersion |

### NuGet 包更新清单

| 包名 | 当前版本 | 目标版本 | 所属项目 |
|------|---------|---------|---------|
| Microsoft.WindowsAppSDK | 2.3.1 | 2.4+ (等待 .NET 11 兼容版) | App |
| Microsoft.Graphics.Win2D | 1.4.0 | 1.5+ (等待兼容版) | App |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | 11.0.0 | App |
| Vortice.Direct3D11 | 3.8.3 | 3.9+ | Core |
| Vortice.DXGI | 3.8.3 | 3.9+ | Core |
| Vortice.Direct2D1 | 3.8.3 | 3.9+ | App |
| Vortice.Mathematics | 2.1.1 | 2.2+ | App |
| JxlNet | 0.11.2.2 | 0.12+ | Core, Test |
| Magick.NET-Q16-HDRI-AnyCPU | 14.15.0 | 14.16+ | Core |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | 1.25+ | Core |
| System.Drawing.Common | 10.0.10 | 11.0.0 | App, Test |

---

## 三、阶段划分

### 阶段一：框架升级 + 基础编译 ✅ 低风险

**预估工作量**: 1 天
**并行度**: 可全量并行

| 步骤 | 操作 | 文件 | 预计耗时 |
|------|------|------|---------|
| 1.1 | 更新所有 .csproj TargetFramework → `net11.0-windows10.0.26100.0` | 4 个 .csproj | 5 min |
| 1.2 | 更新 LangVersion → `14` | 4 个 .csproj | 5 min |
| 1.3 | 更新 NuGet 包到 .NET 11 兼容版本 | 4 个 .csproj | 15 min |
| 1.4 | 解决编译错误（API 变更适配） | 视情况 | 1~2h |
| 1.5 | 运行 192 个测试全部通过 | — | 10 min |
| 1.6 | 验证 Publish.ps1 打包正常 | — | 10 min |

**门禁条件**:
- [ ] `dotnet build` 零错误通过
- [ ] 192 个测试全部通过
- [ ] 功能验证：截图/编码/OCR/翻译 正常

---

### 阶段二：DllImport → LibraryImport 迁移 🟡 中风险

**预估工作量**: 2~3 天
**依赖**: 无（自 .NET 7 起支持，可在 .NET 10 上先行完成 ✅）

**迁移范围**: 全部 50+ 处 `[DllImport]`，按文件分组并行处理

| 文件 | DLL | 数量 | 优先级 | 说明 |
|------|-----|------|--------|------|
| `MainWindow.xaml.cs` | user32, gdi32 | 10 | P0 | 大部分备用 GDI 回退，迁移后需验证 |
| `SelectionOverlay.xaml.cs` | user32, dwmapi | 6 | P0 | 窗口交互核心 |
| `DisplayInfo.cs` | user32 | 4 | P0 | 显示器枚举，影响截图 |
| `RegionDetector.cs` | user32, dwmapi | 8 | P0 | 窗口检测，影响选区 |
| `MetadataCollector.cs` | user32 | 6 | P1 | 元数据，不影响核心功能 |
| `ColorProfileProvider.cs` | user32, mscms | 3 | P0 | ICC 获取，影响色彩管理 |
| `App.xaml.cs` | user32 | 4 | P1 | 启动流程，不影响截图 |
| `WindowPreviewTooltip.xaml.cs` | user32 | 3 | P2 | 预览工具提示 |
| `SilentCaptureToast.xaml.cs` | user32 | 2 | P2 | Toast 通知 |
| `HdrCaptureWindow.cs` | user32 | 3 | P1 | HDR 窗口 |

**迁移模式**:
```csharp
// 旧 (DllImport)
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
private static extern nint FindWindowW(string? lpClassName, string lpWindowName);

// 新 (LibraryImport)
[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
private static partial nint FindWindowW(string? lpClassName, string lpWindowName);
```

**门禁条件**:
- [ ] 所有 `DllImport` 替换为 `LibraryImport`
- [ ] 功能验证：截图/选区/ICC/窗口检测 全部正常
- [ ] 192 个测试全部通过

---

### 阶段三：ARM64 SVE 支持 🟡 中风险

**预估工作量**: 1~2 天
**依赖**: 阶段一完成（需要 .NET 11 SVE API）

**修改范围**:

| 文件 | 改动 |
|------|------|
| `PixelOps.cs` | 新增 `HasSve` 检测 + SVE 路径 |
| `PixelOps.cs` | `FixAlphaChannel` 新增 SVE 分支 |
| `PixelOps.cs` | `BgraToScrgbLinearFast` 新增 SVE 分支 |
| `PixelOps.cs` | `ConvertHalfToFloatRow` 新增 SVE 分支 |
| 测试 | 新增 SVE 路径测试 |

```csharp
// PixelOps.cs 新增
public static bool HasSve => Sve.IsSupported; // .NET 11 正式 API

// 策略更新
public static int BestVectorByteWidth =>
    HasAvx10_512 ? 64 :
    HasSve ? 64 :              // SVE 可变长度，按 64B 优化
    HasAvx512VL ? 32 :
    HasAvx2 ? 32 :
    HasNeon ? 16 :
    HasVector128 ? 16 :
    4;
```

**门禁条件**:
- [ ] `PixelOps` 所有函数新增 SVE 路径
- [ ] ARM64 设备基准测试通过
- [ ] 192 个测试全部通过

---

### 阶段四：ReadyToRun 编译优化 🟡 中风险

**预估工作量**: 1 天
**依赖**: 阶段一完成

```xml
<!-- TrueToneCap.App.csproj 新增 -->
<PropertyGroup>
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishReadyToRunComposite>true</PublishReadyToRunComposite>
</PropertyGroup>
```

**收益**: 启动时间 ~1.5s → ~0.8s，发布包大小增加 ~10%
**风险**: 极低，ReadyToRun 是成熟技术

---

### 阶段五：Native AOT 可行性评估 🔴 高风险（v0.5.0+ 规划）

**预估工作量**: 1~2 周
**依赖**: 阶段二完成（LibraryImport 是 AOT 前提）

**AOT 障碍清单**:

| 障碍 | 影响范围 | 解决方案 |
|------|---------|---------|
| WinUI 3 XAML 运行时绑定 | 所有 XAML 窗口 | 需 WinAppSDK + WinUI 3 AOT 支持 |
| WinRT 互操作 (COM) | WGC 捕获, OCR | 需 CsWinRT AOT 兼容 |
| `System.Text.Json` 序列化 | SettingsService | 配置源生成器 `[JsonSourceGenerationOptions]` |
| 反射查找编码器 | EncoderFactory | 改用 `[GeneratedJsonSerializer]` 或手动注册 |
| `Magick.NET` 原生绑定 | 色域映射 | 不变，原生 DLL 与 AOT 无关 |
| `ONNX Runtime` P/Invoke | OCR 引擎 | 不变，原生 DLL 与 AOT 无关 |

**建议**: 等 .NET 11 正式版 + WinAppSDK 2.4+ 对 AOT 的官方支持后再评估。

---

## 四、迁移风险矩阵

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| NuGet 包不兼容 .NET 11 | 低 | 高 | 先检查各包 NuGet 页面，等待官方更新 |
| WinAppSDK 2.3.1 不兼容 | 低 | 高 | 等 2.4+ 预览版，或暂时锁定版本 |
| LibraryImport 行为差异 | 中 | 中 | 逐条测试，尤其是 `SetLastError`, `CharSet` |
| SVE API 变动 | 中 | 低 | 等 .NET 11 RC 版本确认 API |
| 编译后 XAML 绑定失败 | 低 | 高 | 需完整运行测试，验证所有窗口 |
| 发布包体积增大 | 低 | 低 | ReadyToRun 会增加 ~10%，可接受 |

---

## 五、测试验证清单

### 编译验证
- [ ] `dotnet build` 零错误
- [ ] `dotnet build` 零警告（P/Invoke 相关）
- [ ] 192 个单元测试全部通过

### 功能验证
- [ ] 窗口启动正常（MainWindow 加载）
- [ ] 全屏截图正常（SDR）
- [ ] 全屏截图正常（HDR，若有 HDR 显示器）
- [ ] 选区截图正常
- [ ] 静默截图正常
- [ ] 动图录制正常
- [ ] ICC 色彩管理正常
- [ ] 所有格式编码正常（PNG/JPG/AVIF/WebP/XL/Gain Map/TIFF）
- [ ] OCR 识别正常
- [ ] 翻译正常
- [ ] 托盘图标正常
- [ ] 热键注册正常

### 性能验证
- [ ] 4K 截图延迟 < 200ms
- [ ] 编码延迟与 .NET 10 持平或更低
- [ ] 内存占用无明显增加

---

## 六、回滚方案

如迁移后出现严重问题，回滚步骤：

1. **git revert** 所有 .csproj 和代码更改
2. 恢复 `TargetFramework` 到 `net10.0-windows10.0.26100.0`
3. 恢复 `LangVersion` 到 `13`
4. 恢复 NuGet 包版本
5. 运行测试确认恢复正常

---

## 七、时间线建议

```
第 1 周: 阶段一（框架升级）
第 2 周: 阶段二（LibraryImport 迁移）
第 3 周: 阶段三（SVE 支持）
第 4 周: 阶段四（ReadyToRun）+ 综合测试
---
第 5~6 周: 发布 v0.4.0 + 用户反馈收集
---
未来: 阶段五（AOT 评估，v0.5.0+）
```