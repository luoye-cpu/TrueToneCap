# TrueToneCap GPU 加速可用性分析报告

> ⚠ **时效标注（2026-09-20 整洁审计）**：本文三条核心结论已被代码取代，读时请对照——
> ① `:16`/`:40`/`:71` 的"**QSV 恒不可用、MFT 实际不可达、永远走不到**"：现在 `Qsv` 候选就是走
> 系统 MFT 实现（`FormatEncoders.cs:706-749`，日志标签 `QSV_MFT`/`MFT` 共用崩溃哨兵 key `"MFT"`），
> 独立的 `QsvEncoderNative.cs` 已删除。⚠ 但"可达"只是代码层面：**本机没有 AV1 MFT，这条至今没有跑通过的产物**
> （只有结构断言，见交接 §32.9-4）。
> ② "硬件 NVENC 不能声明色彩/只能降级"：交接 §32.7 实测驱动**按 H.273 号域执行** `matrixCoefficients`，
> 现四字段（primaries/transfer/matrix/range）一起下发，硬件 HDR 还补了 `clli`（§32.9-3）。
> ③ `:40` "所有降级都是静默的，Release 下用户完全看不到"：已有 `EncoderFallbackNotifier.cs` +
> `CapturePipelineService.cs:660-667` 出 Toast。
> ④ `:22-35` 的六道闸门表**缺一道**：Auto 现在先按色度过滤（4:2:0⇒硬件，4:2:2/4:4:4⇒libaom）。
> 仍然成立的部分：能力探测"原先恒为假绿"的诊断与修法。

- 日期：2026-09-16
- 范围：全仓所有宣称/隐含"GPU 加速 / 硬件加速"的路径
- 方法：静态代码分析（逐条读码），标注【确认】/【推断】/【需实机验证】
- 结论性质：本报告判断的是**代码层面是否可达**；"可达"不等同于"真机上一定生效"（见第 6 章）

---

## 0. 执行摘要

**系统宣称的 GPU 加速，实际大部分不可用或不可信。**

| 领域 | 结论 |
|---|---|
| **AVIF 硬件编码** | 三条硬件后端中 **QSV 恒不可用、MFT 实际不可达**，NVENC 是唯一活路，但被 6 道闸门拦截且**任何一道失败都静默退回 libaom** |
| **能力探测** | 原先**恒为假绿**（已修复）：只判"是否产出有效文件"，而失败会回退软件并照样产出有效文件 |
| **渲染侧** | GPU 合成真实生效；**GpuToneMapper 是死代码**；HDR 覆盖层**仅单显示器可用**；MPO/VRR 纯诊断、无实际加速 |
| **OCR** | 默认 DirectML，但引擎未注册时会**整体失败而非回退**（已修复） |

**一句话**：当前真正稳定生效的 GPU 加速只有"AVIF 的 NVENC（条件苛刻）"与"覆盖层的 GPU 合成"两项；其余要么死代码、要么不可用、要么报告不可信。

---

## 1. AVIF 编码后端：四条路径逐一判定

### 1.1 NVENC（NVIDIA 硬件编码）— 唯一理论可达，但闸门重重

【确认】`EncodeAsync`（`FormatEncoders.cs`）的完整前置条件：

| # | 条件 | 不满足时 |
|---|---|---|
| a | `HardwareEncodingEnabled`（默认 true） | 走 libaom |
| b | `w ≥ 128 && h ≥ 128`（`MinNvencDimension`） | 走 libaom，不置失败缓存 |
| c | `EncoderCrashSentinel.IsBlocked("NVENC")` 为假 | 置失败缓存 + libaom |
| d | `s_nvencFailed` 为假 | libaom |
| e | D3D11 设备可用（否则自建，再失败用 `CreateNvidiaDevice`） | libaom |
| f | 纹理直通另有条件：`GpuTexture != null && !s_texturePathFailed` | 退 CPU 像素路径 |

**所有降级都是静默的**——仅有 `Debug.WriteLine`，Release 下用户完全看不到。

【确认】显式选择也不保证生效：`AvifEncoderSelector.Select` 在 `IsAvailable == false` 时直接换成 libaom，而 UI 仍显示"NVENC"。

【确认】NVENC **不依赖共享 D3D11 设备**：为 null 时会自建，因此"重新编码一张已有图"也能走 NVENC。

### 1.2 QSV — 原为恒不可用，本轮已改为走 MFT 实现

**原状**【确认】`QsvEncoderNative.IsAvailable` 硬编码返回 `false`。停用原因是具体的技术缺陷，不是偷懒：

> Intel 没有 oneVPL 的官方 .NET 绑定（官方仅提供 C/C++ 头文件），而该文件里的
> `mfxVideoParam` / `mfxInfoMFX` / `mfxFrameSurface1` 等 P/Invoke 结构体是**自造的**，
> 忽略了官方定义中的 `reserved[]` 前置字段与 union 布局 ⇒ 参数会被写进保留区，
> `MFXVideoENCODE_Init` 拿到垃圾（CodecId ≈ 0）。此外 `TargetKbps` 用 `ushort`
> 承载 4K 码率必然溢出，且缺少 drain 循环。

**本轮实现**：改走 **Windows Media Foundation (MFT)**，而不是重写 oneVPL 的 P/Invoke。理由：

| 对比项 | oneVPL P/Invoke | MFT |
|---|---|---|
| 维护方 | 需自行按头文件维护结构体 | 微软维护的 COM 接口 |
| 版本风险 | 头文件版本差异即错位 | 无 |
| 厂商覆盖 | 仅 Intel | Intel（即 Quick Sync）/ NVIDIA / AMD 通吃 |
| 本机可验证性 | 需 Intel GPU + oneVPL 实测 | 由系统统一调度 |

在 Intel 显卡上，MFT 会调度到 Quick Sync —— 即名副其实的 QSV；在其它厂商上则调度到各自的硬件编码器。系统没有 AV1 MFT 时 `IsAvailable` 返回 false，正常回退 libaom。

**实测结果**：⚠️ **本机无 AV1 MFT 编码器**（已由新增的环境自适应用例确认，输出"跳过"而非伪装通过）⇒ QSV 路径在本机仍会回退软件编码，**实际编码效果需在支持 AV1 MFT 的机器上验证**。新增用例 `Test_MftAv1EncodeIfAvailable` 在有 MFT 的环境下会执行**真实编码**并断言产出非空码流——不是只查 `IsAvailable`（那正是此前"探测假绿"的教训）。

### 1.3 MFT（Windows Media Foundation）—【确认】实际不可达

实现存在且完整，但 `AvifEncoderBackend` 枚举**没有 MFT 成员**，只作为 `libaom 不可用`时的兜底。而 avifenc.exe 随发布包内嵌，libaom 始终可用 ⇒ **MFT 永远走不到**。

### 1.4 libaom（软件）—【确认】实际生效路径

所有硬件路径失败后的终点。4K AVIF 约 22s（硬件约 0.12s，差约 180 倍）。

---

## 2. 能力探测的"假绿"（已修复）

### 问题

【确认】`AvifHardwareProbe.ProbeSingle` 的判据是：

```csharp
bool valid = File.Exists(tmpPath) && new FileInfo(tmpPath).Length > 100;
```

但后端任一步失败都会 `FallbackToLibAom`，**照样写出一个有效 AVIF 文件** ⇒ 判据**恒成立** ⇒ 探测永远返回"可用"。

后果：`CapabilityService` 报告 `NvencAvailable = true`、UI 显示 ✓，而实际可能一次都没用过硬件。这是**对外承诺与真实行为不一致**的典型。

### 修复

1. `NvencAvifBackend` 新增 `LastEncodeUsedHardware`：仅在真正由 NVENC 产出码流时置位（入口先归零）。
2. 探测改为：文件有效 **且** `LastEncodeUsedHardware` 为真，否则判为不可用。

【确认】该标志仅在 NVENC 后端的三条成功路径置位（纹理直通 / N 卡设备 / CPU 像素路径）。

---

## 3. 渲染侧 GPU 路径

| 路径 | 状态 | 说明 |
|---|---|---|
| **GPU 合成**（`OverlayComposite`） | ✅ **真实生效** | CSO 随发布包分发；`TTC_GPU_COMPOSITE=0` 可强制 CPU；降级仅日志，用户不可见 |
| **`GpuToneMapper`** | ❌ **死代码** | `AppServices.cs:74` 构造并注册 DI，但 `AppServices.GpuToneMapper` 全仓无调用者。HDR→SDR 实际走 CPU 的 `ToneMapper` |
| **`_ps`（ToneMapping PS）** | ❌ **死代码** | `HdrCaptureWindow.cs:347` 创建后从未 `PSSetShader`（全文只绑 `_overlayPs`） |
| **HDR 覆盖层** | ⚠️ **仅单显示器** | 见下 |
| **`HDRPreviewWindow`** | ⚠️ **名不副实** | SDR 选区路径下传的 `_hdrPixels` 为 null，实际执行 SDR→scRGB 转换；窗口名叫 HDR，内容不是 HDR 源 |
| **MPO / VRR** | ❌ **无实际加速** | 仅诊断输出；`Present` 恒为 `Present(0)`，从未使用 tearing/VRR 路径 |

### 3.1 多显示器 HDR —— 原为重大限制，本轮已实现

**原状**【确认】`WgcCaptureService.cs:1043` 只有 `displays.Count == 1` 才 `PreferHdr = true`；多显示器分支不设，只建 SDR 会话、`HdrPixels = null` ⇒ **多屏用户永远拿不到 HDR 数据**，HDR 编码与预览全线静默降级为 SDR。

**实现方案**（用户指定"以第一个活跃显示器为准"，其余由我设计）：

| 决策点 | 选择 | 理由 |
|---|---|---|
| HDR 源 | **主显示器优先**；主显示器不支持时退选第一个支持 HDR 的 | 对应"第一个活跃显示器"；`DisplayInfo` 有 `IsPrimary`/`IsHdr`/`SupportsHdr` |
| 其余显示器 | 仍走 SDR，但**上转换**到 scRGB 线性后参与 HDR 拼接 | 一张图只能有一种色彩空间，不存在"局部 HDR"；不统一会导致亮度基准不一致 |
| HDR 会话建立时机 | 放在 `Parallel.ForEach` **之前** | `GetOrCreateDevice` 无锁，放进并行循环会与其它显示器的设备创建并发，存在重建/悬垂设备竞态 |
| HDR 源区域填充 | 循环外串行覆盖 | 避免并发访问同一个 HDR 会话 |
| 失败处理 | 任一步失败 → 放弃**整图** HDR，保留 SDR | 半张 HDR 比全 SDR 更糟；SDR 结果始终不受影响 |

关键约束：`CopyFloatRegion` 逐行夹紧边界——多显示器分辨率/DPI 不一致时，某显示器的实际帧尺寸未必等于其逻辑尺寸，不夹紧会写越界。

**注意**：多显示器**选区截图**（对每个显示器单独 `CaptureMonitorAsync`）本就走 `PreferHdr`，此前已支持 HDR；本轮补上的是"**全部显示器拼接**"这一场景。

⚠️ 需实机验证：本机单显示器，多屏 HDR 拼接无法验证。

---

## 4. AI（OCR）侧

【确认】默认 `OcrEngineMode = "OnnxGpu"`（DirectML），`MultiOcrService` 优先注册 DML 引擎。

【确认】**探测很浅**：`OnnxOcrEngine.cs:318-333` 只做 `AppendExecutionProvider_DML(0)`，**不建会话、不跑推理**。注册成功不代表推理可用。

### 假绿（已修复）

【确认】原实现：选了 `OnnxGpu` 但该引擎未注册 ⇒ `SelectedEngine` 为 null ⇒ 直接返回"未选择 OCR 引擎"，OCR 整体不可用，状态栏一直卡在"OCR 引擎探测中..."，**不会回退到 CPU 或 Windows OCR**。

修复：`SelectedEngine` 为 null 时回退到第一个已注册引擎（注册顺序即优先级）。

---

## 5. 可用性总表

| 路径 | 有调用链 | 启用条件 | 实际生效 | 降级目标 | 用户可见 |
|---|:---:|---|:---:|---|---|
| NVENC AVIF | ✅ | 6 道闸门全通 | 【推断】理论可达，待实机验证 | libaom → PNG | ❌ 仅 Debug |
| NVENC 纹理直通 | ✅ | 上条 + GPU 纹理 + 未缓存失败 | 仅全屏且未烘焙 ICC | CPU 像素路径 | ❌ |
| QSV | ✅ | — | ❌ `IsAvailable` 恒 false | libaom | ❌ |
| MFT | ✅ | libaom 不可用（实际不会） | ❌ 不可达 | libaom | ❌ |
| libaom | ✅ | 总是 | ✅（实际生效） | PNG | 仅后缀变化 |
| GPU 合成 | ✅ | CSO 存在 + 环境变量未关 | ✅ | CPU CompositeUI | ❌ |
| GpuToneMapper | ❌ 死代码 | — | ❌ | — | — |
| `_ps` ToneMapping PS | ❌ 死代码 | — | ❌ | — | — |
| HDR 拼接（多显示器） | ✅ | 主显示器支持 HDR | ✅ 本轮实现 | 全 SDR | ❌（建议加提示） |
| HDRPreviewWindow | ✅ | 选区路径 | ⚠️ 实际是 SDR→scRGB | CPU WriteableBitmap | ❌ |
| OCR DirectML | ✅ | 默认 OnnxGpu | 【需实机验证】 | CPU EP / Windows OCR | ⚠️ 已修假绿 |
| WGC D3D11 设备 | ✅ | 每次捕获 | ✅ | 重建/默认适配器 | ❌ |
| Win2D 文字光栅 | ✅ | 始终 | ✅（仅服务文字） | 无 | — |
| MPO / VRR | 仅诊断 | — | ❌ 恒 Present(0) | — | ❌ |

---

## 6. 局限与待验证

本报告判断的是**代码层面可达性**，以下无法从代码断定，需实机验证：

1. **NVENC 在真实驱动下是否真能产出硬件码流**。历史上有多次"会话能开但编码必败"的修复记录，说明该路径脆弱。建议实测：对比 4K AVIF 编码耗时（硬件 ~0.12s vs 软件 ~22s）即可判断。
2. **`s_nvencFailed` 在真实会话中的触发率**。
3. **DirectML OCR 的实际可用性**（注册成功 ≠ 推理可用）。
4. **多显示器拼接时的 D3D 设备竞态**：`GetOrCreateDevice` 无锁，却被 `Parallel.ForEach` 并发调用。

---

## 7. 建议（按性价比）

| 优先级 | 项 | 说明 |
|---|---|---|
| 1 | ~~多显示器补 HDR~~ **本轮已实现** | 主显示器为 HDR 源，其余 SDR 上转换后拼接；需多屏实机验证 |
| 2 | ~~编码降级改为用户可见~~ **本轮已实现** | 见下方实施说明 |
| 3 | ~~清理死代码~~ **本轮已实现** | `_ps` 已删除；`GpuToneMapper` 停止启动构造并标注 |
| 4 | ~~QSV 要么实现要么从 UI 移除~~ **本轮已实现** | 禁用并标注"暂不可用"（保留索引以免设置错位） |
| 5 | **MFT 加入枚举或删除实现** | 保留不可达的实现只会误导 |
| 6 | **`HDRPreviewWindow` 更名或补 HDR 源** | 名称与实际内容不符 |

---

## 附二：通知系统改进（2026-09-16）

### 现状问题

1. **位置设置存在但部分失效**：`ToastPosition` 有 5 个选项（含 `WindowsNotify`），但 `SettingsService.Normalize` 白名单只列了 4 个 ⇒ 选"Windows 系统通知"后每次启动都被改回右下角 —— **与 `OcrEngineMode` 完全同型的缺陷**（两处字符串常量不同步）。
2. **降级提示忽略位置设置**：`ShowCaptureDegraded` 只发系统通知，用户在设置里选了四角位置也不生效。
3. **观感**：图标固定为绿色对勾（降级/失败也显示成"成功"）、`✕` 是文本而非 Fluent 图标、字体未显式指定、无动画、`Resize(320,80)` 与定位常量 `340×100` 不一致导致贴边偏移。

### 已实施

| 项 | 说明 |
|---|---|
| 白名单补齐 | `Normalize` 加入 `WindowsNotify` |
| 统一通知入口 | `ToastService.ShowToast(title, body, tone)`：按设置决定走应用内浮层还是系统通知 |
| 位置提供器注入 | `ToastService.PositionProvider`（`SettingsService.Current` 是实例属性，静态类拿不到，用委托解耦） |
| 四角定位 | `SilentCaptureToast` 支持 BottomRight/TopRight/TopLeft/BottomLeft，尺寸与定位常量统一（`360×116`，边距 16） |
| 语义图标徽章 | Success/Warning/Error/Info 四态，圆形徽章 + Segoe Fluent Icons；**降级用警告黄而非绿色对勾** |
| 字体 | 显式 `Segoe UI Variable Display/Text → Segoe UI` 回退链 |
| 交互 | 180ms 淡入（CubicEase）、悬停暂停自动关闭、关闭按钮改用 `&#xE8BB;` |

### 两种通知的区别（用户明确指出二者不一样，这是对的）

| | Windows 系统通知 | 应用内浮层 |
|---|---|---|
| 渲染方 | 系统 | 本应用自绘窗口 |
| 位置 | 固定右下角，不可调 | 四角可定位 |
| 通知中心 | ✅ 进，可事后回看 | ❌ 不进 |
| 样式 | 系统统一，只能提供标题/正文 | 可控 |
| 消失 | 系统策略 | 5 秒后自动关闭（悬停暂停） |

**分工**（用户选定）：
- **可追溯类** → 系统通知：无感截图完成、录制完成、失败（事后可能要回看）
- **即时反馈类** → 应用内浮层：编码降级提示、OCR 结果、翻译结果（需要立刻看到，不必留存）

实现为 `ToastChannel` 枚举 + `ShowToast(..., channel)`：
- 用户显式选 `WindowsNotify` → 一律走系统（尊重选择）
- 事件标注 `ToastChannel.System` → 即便设了四角也走系统（保证可追溯）

**未强制改动的考虑**：`ShowCaptureSuccess` 等原本由 `MainWindow` 按位置设置处理（四角→浮层，WindowsNotify→系统）。若强行改成 System 通道，会让"通知位置"设置对截图成功失效，反而违背用户的显式选择。故保留原行为，改为在设置界面说明差异。

### 关于"右下角"

两种模式都满足：系统通知本身在右下角；浮层默认 `BottomRight` 也是右下角。区别在于是否进通知中心、能否移动。

---

## 附：本轮已实施的修复

| # | 项 | 位置 |
|---|---|---|
| — | 能力探测假绿（文件有效 ≠ 硬件产出） | `AvifHardwareProbe.cs`、`NvencAvifBackend.LastEncodeUsedHardware` |
| — | OCR 选中引擎不可用时回退，而非整体失败 | `MultiOcrService.cs` |
| — | 探测样张尺寸与 NVENC 下限绑定 | `AvifHardwareProbe.cs`（ProbeSize 256 + Debug.Assert） |
| **1** | 多显示器 HDR 拼接（主显示器为 HDR 源，其余 SDR 上转换） | `WgcCaptureService.CaptureAllMonitorsInternalAsync` + `SelectHdrSourceDisplay` / `CopyFloatRegion` |
| **2** | 硬件→软件降级用户可见 | `NvencAvifBackend.LastFallbackReason` + `CapturePipelineService.NotifyHardwareFallbackIfAny` |
| **3** | 死代码清理 | 删除 `_ps`（字段/创建/重建/释放）；`GpuToneMapper` 停止启动构造 |
| **4** | QSV 选项禁用并标注 | `MainWindow.xaml` AvifBackendCbo |

### 建议 2 的实施细节

**为什么必须提示**：4K AVIF 硬件约 0.12s vs 软件约 22s。原实现全程只有 `Debug.WriteLine`，Release 下无声 —— 用户只会觉得"软件变慢了"，既不知原因也不知能否恢复。

**机制**：
1. Core 层 `NvencAvifBackend.LastFallbackReason`：在三类"用户预期硬件可用"的回退点置位——崩溃哨兵、失败缓存、编码失败（并按输入不适用/硬件不可用语种区分文案）。方法入口归零。
2. App 层 `CapturePipelineService.NotifyHardwareFallbackIfAny()`：复用已有的 `ToastService.ShowCaptureDegraded` 通道（原本用于捕获降级）+ `LogService.Warn`（UI 日志面板可见）。

**刻意不提示的场景**：用户主动关闭硬件开关；图像小于最小尺寸（小图软件编码本就无损失）。避免把"正常行为"变成噪音。

**节流**：每会话只弹一次 Toast（`s_hwFallbackNotified`）——连续截图时反复弹窗是干扰，不是帮助。但每次都会写日志。

### 建议 3 的实施细节

- **`_ps`**：确认是死代码（创建后从未 `PSSetShader`，唯一绑定点是 `_overlayPs`）。已删除字段声明、创建、重建路径创建、及各 Dispose 处释放。
- **`GpuToneMapper`**：类保留（未来可接入），但**停止在 AppServices 启动时构造**——它构造时会读磁盘着色器字节码，而没有任何调用者，等于每次启动都为一条不存在的路径付费。同时修正了 `HdrCaptureWindow.cs` 中"真正承担色调映射的是 GpuToneMapper"的**反向注释**（实际走 CPU 的 `ToneMapper`）。

### 建议 4 的实施细节

禁用而非删除，原因是**索引兼容性**：`AvifBackendIndex` 的映射为 `0=Auto / 1=LibAom / 2=Qsv / 3=Nvenc`，删除该项会让已保存为 3（NVENC）的用户错位到别的后端。若将来真正实现 QSV，去掉 `IsEnabled="False"` 即可。

### 验证

```
构建：0 个警告 / 0 个错误
测试：8 套件 446 断言全绿
```
