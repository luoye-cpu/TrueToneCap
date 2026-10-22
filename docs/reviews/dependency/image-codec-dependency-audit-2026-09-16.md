# 图像编码依赖库更新审计

- 日期：2026-09-16
- 方法：`dotnet list package --outdated`（NuGet 官方源）+ 本机二进制实际版本 + 上游 release 查询

---

## 结论摘要

| 类别 | 数量 | 有可用更新 |
|---|---|---|
| NuGet 包 | 8 | **1 个**（System.Drawing.Common 10.0.11 → 10.0.12，补丁级） |
| 原生编码器 | 3 | **1 个**（libjxl 0.11.2 → 0.12.0，但官方标注为评估版） |
| 硬件编码 SDK | 3 | 均由驱动/系统提供，不受本项目控制 |

**整体：依赖基本是最新的**，仅两个可选更新，且都有保留理由（详见下文建议）。

---

## 一、NuGet 包（`dotnet list package --outdated` 实测）

| 包 | 当前 | 最新 | 更新 | 说明 |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.4.0 | 2.4.0 | — | WinUI 3 / AppNotification / AppWindow |
| Microsoft.Graphics.Win2D | 1.4.0 | 1.4.0 | — | 文字光栅化（标注层） |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | 10.0.12 | — | DI 容器 |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | 1.24.4 | — | OCR 推理（DirectML EP） |
| Vortice.Direct3D11 | 3.8.3 | 3.8.3 | — | D3D11 互操作 |
| Vortice.DXGI | 3.8.3 | 3.8.3 | — | DXGI 互操作 |
| MozJpegSharp.Core | 3.0.8 | 3.0.8 | — | JPEG（托管） |
| **System.Drawing.Common** | **10.0.11** | **10.0.12** | **✅ 有** | 补丁级更新（仅 Test / Tools 项目引用） |

> 注：`System.Drawing.Common` 只在 Test 与 Tools 项目使用，主程序（App/Core）并未引用，因此这个更新对发布产物**没有影响**。

---

## 二、原生编码器（发布时随包分发，本机二进制实测版本）

| 工具 | 当前 | 上游最新 | 状态 |
|---|---|---|---|
| **avifenc**（libavif） | **1.4.2** | 1.4.2（2026-05-26） | ✅ 已是最新 |
| └ `aom`（AV1 **编码器**） | 3.14.1 | 3.14.1（libavif 1.4.2 内置） | ✅ |
| └ `dav1d`（AV1 解码器） | 1.5.3 | — | ✅ |
| └ `libyuv` | 1924 | 1924 | ✅ |
| **cjxl / cjpegli**（libjxl） | **0.11.2** | **0.12.0** | ⚠️ 有更新 |
| **cwebp**（libwebp） | **1.5.0** | 1.5.0（1.6.0 目前仅 `-rc1`） | ✅ 最新稳定版 |

### 关于 libjxl 0.12.0

官方 release 页面标注：

> "This release is for evaluation purposes and may contain bugs…"

**建议暂不升级**。理由：
- 0.11.2 稳定，JPEG XL 链路（含 GainMap）已验证通过
- 0.12.0 是评估版，且跨 minor 版本（0.11 → 0.12）行为可能变化
- 升级需重跑 GainMap / 色彩链路基准，成本高于收益

若要升级，需：替换嵌入的 `cjxl.exe` / `cjpegli.exe` → 更新 SHA256 校验值 → 重跑编码与色彩测试。

---

## 三、硬件编码（不随包分发，由驱动/系统提供）

| 路径 | 来源 | 当前 | 说明 |
|---|---|---|---|
| **NVENC** | NVIDIA 驱动 | SDK 13.1 | 驱动更新即升级，本项目不分发 |
| **MFT** | Windows 系统 | — | `MediaFoundation` AV1/HEVC 编码器，系统更新即升级 |
| **QSV** | Intel oneVPL / MFX | — | `QsvEncoderNative.cs` 已停用，现走 MFT（跨厂商） |

---

## 四、值得关注的观察：AVIF 慢的真正原因

本次审计发现一个与"更新"无关、但影响很大的事实：

```
avifenc --version
  Version: 1.4.2 (dav1d [dec]:1.5.3-0-gb546257, aom [enc]:3.14.1)
```

**AVIF 编码实际用的是 `libaom 3.14.1`** —— 它是参考实现，以兼容性著称但速度慢。这直接解释了此前实测的现象：

> 4K HDR AVIF 编码耗时约 **24 秒**（AV1 硬件路径因只接受 8-bit BGRA 输入而用不上）

### 可选优化方向（非必需）

libavif 1.4.2 的构建脚本内已包含 **SVT-AV1 v4.1.0**（见其 release notes 的 `LocalSvt.cmake: v4.1.0`）。若重新构建 avifenc 时启用 SVT-AV1，同画质下编码速度通常显著快于 libaom。

代价：需自行构建 avifenc（当前是拿现成二进制），并重新验证 AVIF 的色彩/元数据行为（ICC、CICP、GainMap）。

⇒ **建议列为独立的优化议题，而不是"依赖更新"**。当前 AVIF 功能正确，只是慢；且已有降级提示机制。

---

## 五、建议汇总

| 项 | 建议 |
|---|---|
| System.Drawing.Common 10.0.12 | 可更（仅 Test/Tools，影响面为零） |
| libjxl 0.12.0 | **暂不更**（评估版 + 跨 minor，等正式版再评估） |
| libavif / libwebp / NVENC / MFT | 已最新，无需动作 |
| AVIF 编码速度（libaom） | 另立优化议题，可评估 SVT-AV1 |
