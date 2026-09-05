# TrueToneCap 依赖与组件版本审查报告 (2026-08-25)

> 本报告覆盖 TTC 全部依赖项（NuGet 原生 + 传递、.NET SDK、原生工具链、OCR 模型）的当前版本与可用更新。

---

## 1. .NET SDK 与目标框架

| 组件 | 当前版本 | 最新稳定版 | 最新预览版 | 建议 |
|------|---------|-----------|-----------|------|
| **.NET SDK** | 11.0.100-preview.7 | — (无稳定版) | 11.0.100-preview.7 | ⏸️ 保持预览版 |
| **TargetFramework** | `net11.0-windows10.0.26100.0` | — | — | ⏸️ .NET 11 尚无稳定版 |

**分析**：.NET 11 当前仍为 preview 阶段。SDK 已从 preview.6 升级到 preview.7（运行时版本从 26359 变为 26381）。**无稳定版可直接升级**。.NET 11 正式版预计 2025-11 发布。

**风险**：预览版 SDK 可能引入 breaking change；但目前项目编译/测试稳定。

---

## 2. NuGet 顶级包（项目直接引用）

| 包名 | 当前版本 | 最新版本 | 所在项目 | 升级建议 |
|------|---------|---------|---------|---------|
| **Microsoft.WindowsAppSDK** | 2.3.1 | **2.4.0** | App + LogViewer | 🟡 可升级 |
| **Microsoft.Graphics.Win2D** | 1.4.0 | 1.4.0 (最新) | App | ✅ 已最新 |
| **Microsoft.Extensions.DependencyInjection** | 11.0.0-preview.6 | 11.0.0-preview.6 | App | ✅ 已最新 |
| **Vortice.Direct3D11** | 3.8.3 | 3.8.3 (最新) | Core | ✅ 已最新 |
| **Vortice.DXGI** | 3.8.3 | 3.8.3 (最新) | Core | ✅ 已最新 |
| **Microsoft.ML.OnnxRuntime.DirectML** | 1.24.4 | **1.29.0** | Core | 🔴 升级收益大 |
| **System.Drawing.Common** | 8.0.8 | 10.0.11 | Tools | 🟡 可升级 |

---

## 3. NuGet 传递依赖

| 包名 | 当前版本 | 最新版本 | 来源 | 升级建议 |
|------|---------|---------|------|---------|
| **Microsoft.ML.OnnxRuntime.Managed** | 1.24.4 | 1.29.0 | Core DI | 🔴 跟随 ORT 升级 |
| **Microsoft.Web.WebView2** | 1.0.3719.77 | 1.0.4129.50 | WinAppSDK | 🟡 跟随 WinAppSDK |
| **Microsoft.Windows.SDK.BuildTools** | 10.0.26100.4654 | 10.0.28000.2526 | WinAppSDK | 🟡 跟随 WinAppSDK |
| **Microsoft.WindowsAppSDK.Runtime** | 2.3.1 | 2.4.0 | WinAppSDK | 🟡 跟随 WinAppSDK |
| **Microsoft.WindowsAppSDK.WinUI** | 2.3.0 | 2.3.6 | WinAppSDK | 🟡 跟随 WinAppSDK |
| **System.Numerics.Tensors** | 9.0.0 | 10.0.11 | ORT | 🟢 低优先 |
| **Vortice.Mathematics** | 2.1.0 | 2.1.1 | Vortice | 🟢 低优先 |

---

## 4. 原生工具链（内嵌资源）

| 文件 | 当前版本 | 最新版本 | 来源 | 升级建议 |
|------|---------|---------|------|---------|
| **cjxl.exe** (JPEG XL 编码器) | v0.11.2 | v0.11.x (2025-08 检查无更新) | libjxl | ✅ 已最新 |
| **cjpegli.exe** (JPEG LI 编码器) | v1.4.2 | v1.4.x | jpegli | ✅ 已最新 |
| **cwebp.exe** (WebP 编码器) | v1.5.0 | v1.5.x | libwebp | ✅ 已最新 |
| **avifenc.exe** (AVIF 编码器) | v1.4.2 (aom 3.14.1) | v1.4.x | libavif | ✅ 已最新 |

---

## 5. .NET 运行时

| 组件 | 当前版本 | 说明 |
|------|---------|------|
| **.NET Runtime** | 11.0.0-preview.7.26381 | 包含在 SDK 中，自包含发布 |
| **WindowsAppRuntime** | 2.3.1 | WinUI 3 运行时 |
| **C# 语言版本** | 14 | .NET 11 默认 |

---

## 6. OCR 模型

| 文件 | 当前版本 | 说明 |
|------|---------|------|
| **PP-OCRv6_medium_det.onnx** | FP16 medium (v6) | PaddleOCR 最新版 |
| **PP-OCRv6_medium_rec.onnx** | FP16 medium (v6) | PaddleOCR 最新版 |
| **ppocrv6_dict.txt** | v6 统一字典 | ~15000 字符，50+ 语言 |

---

## 7. 重大升级分析

### 7.1 Microsoft.WindowsAppSDK 2.3.1 → 2.4.0

**升级收益**：
- WinUI 3 性能改进（XAML 编译器、渲染管线）
- 新 API 支持（可能的窗口管理改进）
- bug 修复（含传递依赖 WebView2/BuildTools 全链升级）

**风险评估**：
- ⚠️ 主要版本升级（2.3→2.4）可能有 breaking change
- ⚠️ XAML 编译器行为可能变化（当前有 WMC1509 警告）
- ⚠️ WindowsAppSDKSelfContained 布局可能调整 → 发布包结构需验证

**建议**：🟡 **中等优先级**。建议在 .NET 11 正式版发布后一并升级，避免双重预览版不稳定。

### 7.2 Microsoft.ML.OnnxRuntime.DirectML 1.24.4 → 1.29.0

**升级收益**：
- 🚀 **极大性能改进**：1.29 内含 DirectML EP 计算管线优化
- 🚀 **模型兼容性**：可能修复当前 `GraphOptimizationLevel.ORT_DISABLE_ALL` 的限制（需验证 — 若新版修复了 SimplifiedLayerNormFusion bug，可重新启用图优化获得 ~20-30% 推理加速）
- DirectML 1.x → 最新版本：GPU 算子覆盖更全

**风险评估**：
- ⚠️ 跨越 5 个 minor 版本（1.24→1.29）= API 可能有变化
- ⚠️ SessionOptions API 可能调整
- ⚠️ DirectML 运行时系统要求可能提升

**建议**：🔴 **高优先级**。这是收益最大的单项升级 — 5 个版本的提升。建议先在独立分支测试 ORT 1.29 的图优化 bug 是否已修复（这是 2026-08-09 发现的 det 输出全零问题根因）。

### 7.3 ONNX Runtime 图优化修复的潜在收益

**当前问题**：ORT 图优化（`ORT_ENABLE_ALL`）触发 `SimplifiedLayerNormFusion` bug → PP-OCRv6 det 模型输出全零 → OCR 完全失效。当前 workaround 是 `ORT_DISABLE_ALL`（禁用所有图优化），牺牲 ~20-30% 推理性能。

**升级后可能性**：
- 若 ORT 1.29 修复了该 fusion bug → 可改回 `ORT_ENABLE_ALL` → OCR 推理加速 ~20-30%
- 即使未修复，1.29 的基础性能也会有提升

### 7.4 System.Drawing.Common 8.0.8 → 10.0.11 (Tools 项目)

**说明**：仅 Tools 项目使用（生成测试图），不影响主应用。可跳过或跟随 .NET 11 正式版升级。

---

## 8. 升级路线图建议

### 第一步：ORT 1.29.0（最高收益）

```
时机: 现在可尝试 (独立分支测试)
步骤:
1. 创建迁移分支
2. 升级 Microsoft.ML.OnnxRuntime.DirectML 到 1.29.0
3. 验证: ORT_ENABLE_ALL 图优化 → det 模型输出是否正常
4. 若正常 → 改回 ORT_ENABLE_ALL → 跑 --ocr-tests
5. 若异常 → 保持 ORT_DISABLE_ALL, 仍受益于 1.29 基础性能
6. 构建 + 全量测试
```

### 第二步：WinAppSDK 2.4.0（中风险，待时机）

```
时机: .NET 11 正式版发布后 (预计 2025-11)
步骤:
1. 升级 Microsoft.WindowsAppSDK 到 2.4.0
2. 验证 XAML 编译器/发布包结构
3. 全量回归测试
```

### 第三步：其余小版本（低优先）

```
Vortice.Mathematics 2.1.0→2.1.1
System.Numerics.Tensors 9.0.0→10.0.11
```

---

## 9. 总结

| 优先级 | 依赖 | 收益 | 风险 | 时机 |
|--------|------|------|------|------|
| 🔴 高 | **ONNX Runtime 1.24→1.29** | OCR 推理加速 20-30% (若图优化修复) | API 变更 | 独立分支测试后决定 |
| 🟡 中 | **WinAppSDK 2.3→2.4** | WinUI 性能+bugfix | XAML 行为变化 | .NET 11 正式版后 |
| 🟡 中 | System.Drawing.Common 8→10 | 仅 Tools | 低 | 随意 |
| ✅ 最新 | cjxl/cjpegli/cwebp/avifenc | — | — | 已最新 |
| ✅ 最新 | Vortice.Direct3D11/DXGI 3.8.3 | — | — | 已最新 |
| ✅ 最新 | Win2D 1.4.0 | — | — | 已最新 |
| ✅ 最新 | PP-OCRv6 medium | — | — | 已最新 |

**核心建议**：ONNX Runtime 1.29.0 是当前收益最大的升级机会 — 它可能修复 `GraphOptimizationLevel` 的限制，让 OCR 推理再加速 20-30%。建议在独立分支先验证图优化 bug 是否已修复。
