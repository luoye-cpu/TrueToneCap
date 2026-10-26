---
title: "SDR Fallback Overlay 背景过曝问题 — 交接文档"
date: 2026-10-26
status: in_progress
author: luoye-cpu
related:
  - handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md
  - §64.12 (D3D window 单一化)
  - §34.6-3 (UI contract 重复)
  - §35.4 (C档判语：兜底层不删)
---

## 1. 任务背景与现状

### 1.1 上一步已完成（已提交/未提交）
| 事项 | 状态 | 备注 |
|------|------|------|
| HDR 二维码按钮门禁修复 | ✅ 已提交 (ba94826) | 单测 baseline 99→106→45 |
| 双覆盖层架构分析 | ✅ 完成（文档化） | 结论：09-30 起仅一套 D3D 窗；SelectionOverlay 仅休眠兜底 |
| ToolbarContract 契约单源化 | ✅ 代码完成，未提交 | 1977 门禁全绿；两条渲染路径冒烟通过 |
| **当前：SDR 兜底层背景过曝** | 🔄 **进行中** | 用户在冒烟中亲眼看到过曝 |

### 1.2 环境快照
- **机器**：单显示器 `\\.\DISPLAY1 2560x1440 Primary=True`（HDR 开启）
- **分支**：`main`（本地 8 个未推送提交，含当前契约重构）
- **运行实例**：已清理（PID 43628 killed），无残留进程
- **环境变量**：`TTC_FORCE_LEGACY_OVERLAY` 用户级已清（`Remove-ItemProperty HKCU:\Environment`）
- **日志位置**：`<exe dir>\log\app_2026-10-26.log`（今日日志为空，因冒烟实例过早退出）

---

## 2. 问题复现与现象

### 2.1 复现路径
```bash
# 必须在同一 bash 进程注入环境变量，再经 cmd→runas 启动（setx 不传递）
export TTC_FORCE_LEGACY_OVERLAY=1
cmd //c "runas /trustlevel:0x20000 \"$EXE\""
```
- 热键 `Ctrl+Shift+S` → 屏幕中心单击（全屏选区）→ 工具栏出现
- **用户观测**：兜底覆盖层背景**明显过曝**（比实时桌面亮 ~1.5×）

### 2.2 关键代码链（已确认）
| 位置 | 代码 | 说明 |
|------|------|------|
| `MainWindow.xaml.cs:2137` | `var desktopPixels = captureResult.SdrPixels;` | 兜底层直接拿 8-bit 字节数组 |
| `MainWindow.xaml.cs:2195-2203` | D3D 路径：`BgraToScrgbForDisplay(..., hdrOutputActive: sysHdrNow, sdrWhiteNits)` | **仅 HDR 输出激活时**乘 `sdrScale`（≈2.0） |
| `SelectionOverlay.xaml.cs:~499` | `RenderDesktopBackground` → `WriteableBitmap` 显示 | 8-bit 窗口，DWM 自动做 SDR-white boost |

### 2.3 核心假设（待验证）
> **HDR 屏幕上，WGC 的 8-bit SDR 会话返回的像素已是 *display-referred*（已按系统 SDR 白点缩放）**。  
> D3D 路径的 `BgraToScrgbForDisplay` **假设输入是 *authored sRGB*（white=255 ⇔ 80 nits）**，仅在 `hdrOutputActive=true` 时乘 `sdrScale`。  
> 兜底层把同一份字节直接给 8-bit XAML 窗口，**DWM 再做一次 SDR-white boost** → 双重提亮 → 过曝。

---

## 3. 需要验证的证据链

### 3.1 WGC 8-bit 会话在 HDR 模式下的真实输出
- **单显示器路径** `WgcCaptureService.cs:1196-1199`：`sdrPixels = sdrSession.GetLatestSdr()` —— 独立 8-bit 会话，与 FP16 并行
- **多显示器路径** `WgcCaptureService.cs:1542-1545`：显式乘 `sdrScale`（`SDR 内容亮度对齐` 注释承认这是必要的）
- **矛盾点**：单显示器路径**没有**显式乘 `sdrScale`，直接把 `sdrPixels` 封入 `CaptureResult`（`:1281`）
- **文档线索**：账本 §3604-3753 记载“HDR 开启时 DWM 合成模式仍为 0(SDR RGB888) + 8bit”，暗示 8-bit 会话可能被系统预增亮

### 3.2 对比基准：D3D 路径的正确处理
```csharp
// PixelOps.cs:541-583  BgraToScrgbForDisplay
float scale = hdrOutputActive && sdrWhiteNits > 0
    ? sdrWhiteNits / NominalSdrReferenceWhiteNits  // ≈ 200/80 = 2.5
    : 1f;  // 非 HDR 输出：不乘，靠 DWM 钳位
```
- HDR 输出激活 → scRGB 1.0 = 80 nits → 必须 ×(SdrWhite/80) 才能与周围 HDR 内容亮度一致
- 非 HDR 输出 → scRGB 1.0 = 显示器最大白 → **禁止再乘**，否则被 DWM 钳位成纯白

### 3.3 兜底层的实际契约
- 8-bit `WriteableBitmap` + 普通 WinUI 窗口 = **非 HDR 输出表面**
- DWM 会把 255 映射到 **系统 SDR 白点**（~200 nits），而非 80 nits
- 若 `SdrPixels` 已是 display-referred（255 ≈ 200 nits），再经 DWM boost → 255 → 300+ nits = 过曝

---

## 4. 计划的修复方向（两条可行路径，二选一）

### 方案 A：兜底层拿「未增亮」的 authored sRGB 字节（推荐）
- 在 `WgcCaptureService.cs` 单显示器路径给 `CaptureResult.SdrPixels` 前，**反向除以 `sdrScale`**（若检测到 HDR 输出激活）
- 使兜底层与 D3D 路径的 `BgraToScrgbForDisplay(..., hdrOutputActive=false)` 语义对齐：输入 authored sRGB，靠 DWM 做唯一的 boost

### 方案 B：兜底层显式做色调映射到 authored sRGB
- 在 `MainWindow.xaml.cs` 兜底分支把 `desktopPixels` 过一遍 `BgraToScrgbLinearFast → ×(1/sdrScale) → LinearToSrgb`（或直接用现成 `ToneMapper` 逆向）
- 优点：不改捕获层；缺点：多一次全帧转换，且依赖 `sysHdrNow` 运行时值

### 共同前置：必须先**量化**当前 `SdrPixels` 的白点位置
- 写一个最小探针：启动应用 → 触发捕获 → 采样 `SdrPixels` 中纯白区域（任务栏/标题栏）的均值
- 对比：`255` (authored) vs `~255` (display-referred) vs `~127` (若已被钳位)

---

## 5. 待办清单（按顺序）

| # | 任务 | 产出 | 备注 |
|---|------|------|------|
| 1 | **写探针脚本** `scripts/probes/sdr-session-whitelevel-probe.ps1` | 能在 HDR 开启时打印 `SdrPixels` 纯白区均值 | 复用现有 `launch_window_probe.ps1` 模式 |
| 2 | 运行探针，记录实测数值 | 截图/日志证据 | 需用户配合（弹窗覆盖其工作区） |
| 3 | 根据实测决定方案 A/B 并实现 | 代码变更（`WgcCaptureService.cs` 或 `MainWindow.xaml.cs`） | 优先方案 A（捕获层修正，零运行时开销） |
| 4 | 重建 + 全门禁（8 套，≥1977） | `dotnet test` 全绿 | 预提交钩子会跑 build，先 `taskkill` 实例 |
| 5 | 冒烟复验：`TTC_FORCE_LEGACY_OVERLAY=1` 对比背景亮度 | 视觉确认不再过曝 | 简短自清理 |
| 6 | 撰写账本补记：QR 死路径事件 + 契约单源化决策 + 兜底层过曝修复 | `docs/ledger/...md` | 合并到现有 `handoff-avif-...md` 或新文件 |
| 7 | 提交（含契约重构 + 过曝修复） | `git commit` | 仅在用户明确要求时 push |

---

## 6. 关键文件速查（改动点）

| 文件 | 相关行 | 角色 |
|------|--------|------|
| `src/TrueToneCap.App/Models/ToolbarContract.cs` | 全文 | **新增** 契约单源（7 主按钮 + 10 标注工具 + hint 槽位） |
| `src/TrueToneCap.App/Services/HdrCaptureWindow.cs` | 22-27, 975-990, 1656-1709 | 消费契约；图集新增 hint 槽 28-34；预热同源 |
| `src/TrueToneCap.App/Shaders/OverlayComposite.hlsl` | `subBase`, `TextSubs[28+hover]` | GPU 逐钮 hint 修复 |
| `src/TrueToneCap.App/SelectionOverlay.xaml(.cs)` | `BuildMainToolbar`, `OnMainToolbarAction` | SDR 兜底按钮生成 |
| `src/TrueToneCap.App/MainWindow.xaml.cs` | 2137, 2195-2203, 2234, 2338 | 帧源契约、兜底分支入口 |
| `src/TrueToneCap.App/Services/LocaleManager.cs` | ~425 | 统一 hint 键（含新 QR hint） |
| `src/TrueToneCap.Test/CorePipelineTests.cs` | `Test_ToolbarContract_SingleSource` | 16 断言守护契约单源 |
| `src/TrueToneCap.Core/PixelOps.cs` | 522-583 | `BgraToScrgbForDisplay` 语义权威 |

---

## 7. 风险与约束（必读）

| 约束 | 说明 |
|------|------|
| **用户正在使用机器** | 任何冒烟必须 <30 秒、自清理、不留残留热键/覆盖层 |
| **QR 安全红线** | 解码内容永不自动执行、仅 http/https 白名单、纯文本显示、不入日志、无预取网络、马赛克门控所有像素出口 |
| **Dev settings 可能含 LLM Key** | 仅结构检查（键名/个数），绝不 dump 值 |
| **预提交钩子跑全量 build** | 提交前必须 `taskkill` 运行实例，否则 DLL 锁死 |
| **`.NET 11` / WinUI 3 / x64 / Win11 24H2+** | 目标框架固定，不降级 |
| **未经用户明确要求不得 push** | 本地 8 个提交含契约重构，push 会触发预推送全门禁 |

---

## 8. 给下一位 Agent 的启动指引

```bash
# 1. 进入仓库
cd C:\PLAN\TrueToneCap

# 2. 确认当前状态
git status          # 应显示：ToolbarContract.cs 新增、多文件修改、无 untracked probe
dotnet build        # 应全绿（若 DLL 锁先 taskkill）

# 3. 先读核心代码建立心智模型
#   - PixelOps.cs:541-583  (BgraToScrgbForDisplay)
#   - WgcCaptureService.cs:1195-1281  (单显示器捕获，SdrPixels 来源)
#   - MainWindow.xaml.cs:2137, 2195-2203  (两条路径分歧点)

# 4. 运行/编写探针量化白点（见 §5 任务 1-2）
# 5. 按实测结果实施修复（见 §4 方案 A/B）
# 6. 之后按 §5 任务 3-7 走完
```

---

## 9. 尚未处理的遗留项（不阻塞本任务）

- [ ] 账本补记：QR 按钮落在死路径的完整复盘（docs agent 确认“文档侧无补记”）
- [ ] 契约单源化决策记录（含 C 档判语“不合并渲染器、不删除兜底”）
- [ ] Store 发布身份、MSIX 实机安装、商店截图
- [ ] .NET 11 GA（2026-11-10）收尾
- [ ] 用户手工 QR 全链路验收

---

> **核心判据**：兜底层过曝的本质是 **「8-bit 捕获会话在 HDR 模式下的输出语义未被显式规范化」**。D3D 路径通过 `BgraToScrgbForDisplay` 显式契约化了「输入 authored sRGB、仅 HDR 输出时乘白点」；兜底层缺失了对应的「反向归一化」或「显式 authored sRGB 保证」。修复点应在捕获层（方案 A）或兜底入口（方案 B）二选一，以探针实测为准。