# TrueToneCap 资源风险审计与修复记录（WGC 之外）

> 日期：2026-11-27
> 范围：`src/TrueToneCap.App`、`src/TrueToneCap.Core` 的 GPU/纹理、模型会话、编码任务、录制缓冲、窗口/缓存生命周期。
> 结论：审计中的 **P0 显示器变更路径、P1 OCR 空闲卸载、P1 连拍编码上限** 已实现；P0 与 OCR 卸载已做真机回归，编码上限通过构建与单元测试，未做连拍压力实测。P2/P3 边界按原审计保留。

## 1. 风险与状态

| 级别 | 风险 | 状态 | 最终处理 |
|---|---|---|---|
| **P0** | 显示器变更后 4K 帧池按次 +190 MB | **已修复并实测** | 尺寸未变时保留现有 WGC session/framePool，不再 `Recreate`；只有尺寸真的变化才完整重建 |
| **P1** | OCR 模型会话常驻，不随空闲释放 | **已修复并实测卸载** | 30s 周期检查，连续空闲（默认 300s）后卸载 Onnx 引擎；懒加载重建 |
| **P1** | 无感截图编码任务无上限 | **已实现** | 捕获前检查待编码任务数，达到 4 直接拒绝并 Toast；不分配新 CaptureResult |
| **P2** | 动图录制约 1 GB 有界峰值 | 未改 | 有界峰值，不是按轮泄漏；后续可补低内存提示 |
| **P2** | HDR 渲染线程卡死时故意不释放 D3D 对象 | 未改 | 安全优先的已知取舍，进程退出回收 |
| **P3** | 语言包 HttpClient / `s_textCache` / `_activeHdrWindow` 残留 | 未改 | 低风险，后续顺手处理 |

## 2. P0：显示器变更路径

### 2.1 关键结论

WGC 的 4K 帧池缓冲一旦分配，**Dispose session / framePool / D3D device、`IDXGIDevice3.Trim()`、等待 RCW 终结器、`FramePool.Recreate` 都不能让驱动提前归还**；下一次截图只要新建或 Recreate 一份 4K 双缓冲，就会再增加约 190 MB。反复 `WM_DISPLAYCHANGE` 的真正问题不是“旧会话没释放”，而是**每次变更都重建了帧池**。

因此最终策略是：

- 收到 `WM_DISPLAYCHANGE` 后，`WgcCaptureService.ResetForDisplayChangeAsync()` 先拿 `s_captureLock`；
- 对每条会话比较 `GetMonitorInfo` 的当前显示器矩形与进入 WGC 时的 `_width/_height`；
- **尺寸未变**：session / framePool / device 原样保留，只让 WGC 自己处理这次配置通知；不调用 `Recreate`、不新建帧池；
- **尺寸真的变了**：退回完整重建（清会话 + 释放设备 + 下一次新建）；
- 无论哪条路径，都清空 `_reuseSdr/_reuseHdr` 并执行一次 `TrimGpuAllocations()`。

### 2.2 失败过的路径（保留作为回归边界）

同一进程、4K HDR：

| 尝试 | 结果 |
|---|---|
| 只 `InvalidateSessions()`（原始） | GPU 45.8 → 253.7 → 444.9 MB |
| 清会话 + 释放 D3D 设备 + Trim | 71.0 → 255.7 → 460.8 → 660.4 MB |
| 再加 RCW 终结器 / 断开 `PooledSession._d3dDevice` | 仍然每轮约 +190 MB |
| `FramePool.Recreate`（保留 session） | 262.4 → 449.4 → 630.8 → 822.0 MB |
| `FramePool.Recreate` 后换新 item/session | `CreateCaptureSession` 返回 `E_UNEXPECTED`（灾难性故障） |
| **尺寸未变时完全不碰 session/framePool** | **262.4 → 262.4 → 262.4 → 251.9 MB** |

### 2.3 最终真机回归

启动基线：私有 247.5 MB，GPU Local 71.2 MB。先做一轮无感截图建立 4K 帧池，之后连续 3 次模拟 `WM_DISPLAYCHANGE`，每次变更后做一轮无感截图并等 27 秒：

| 节点 | 私有内存 | GPU Local | 截图 |
|---|---:|---:|---|
| 初始截图后 | 216.0 MB | 262.4 MB | 成功 |
| 变更 1 | 215.5 MB | 262.4 MB | 575 ms |
| 变更 2 | 217.7 MB | 262.4 MB | 384 ms |
| 变更 3 | 220.8 MB | 251.9 MB | 388 ms |

日志逐条确认“显示器变更但尺寸未变，会话/帧池原样保留 3840x2160 SDR/HDR”，且每轮都有“首帧就绪 + 无感截图完成”。**GPU 不再按轮 +190 MB**。

### 2.4 残余边界

- 真实分辨率/旋转导致尺寸变化时，仍会走一次完整设备重建，承担一次约 190 MB 的 4K 缓冲分配；WGC 没有“保留会话但替换尺寸且归还旧缓冲”的 API。
- 连续多次真实改分辨率仍可能按次数累积；建议后续在 UI 层对短时间重复的 `WM_DISPLAYCHANGE` 做去抖，并避免把 DPI/色彩配置变更误判成尺寸变化。
- 本轮的模拟消息不改变实际分辨率，覆盖的是“同尺寸配置通知”这一最常见路径；真实改分辨率路径只做了代码走查，未现场切分辨率。

## 3. P1：OCR 模型会话空闲卸载

### 3.1 实现

`MultiOcrService` 新增：

- `s_idleTimer`：`Initialize()` 成功后启动，30 秒一拍；
- `s_lastOcrUseTick`：每次 `RecognizeAsync` 进入/结束刷新；
- 默认空闲阈值 **300 秒**，可用 `TTC_OCR_IDLE_UNLOAD_SECONDS=5..3600` 覆盖；
- `TryReleaseIdleOnnxEngines()`：空闲达标且拿到 `s_ocrGate.Wait(0)` 后，从 `_engines` 移除 `OnnxGpu/OnnxCpu` 并 `Dispose()`，重置 `_gpuBuiltDegraded`，保留 `SelectedEngineType` 与 Windows OCR；下次 OCR 懒加载；
- `Shutdown()`：停 Timer 并释放 Onnx 会话，`AppServices.Shutdown()` 已接入；
- `SelectedEngine` / `Engines` / `FindEngine` / `IsEngineAvailable` 加 `s_initLock`；`OnnxOcrEngine.Dispose()` 追加 `_detTensor/_recTensor/_recBatchTensor/_dict = null`。

### 3.2 真机验证

用 `TTC_OCR_EAGER_GPU=1` 强制启动期构建 DirectML，`TTC_OCR_IDLE_UNLOAD_SECONDS=5` 缩短阈值：

```text
02:51:56.691 [OCR] DirectML GPU 引擎就绪
02:51:56.691 [OCR] 空闲卸载已启用：连续 5 秒无 OCR 则释放 Onnx 会话
02:52:26.791 [OCR] 空闲 30s：已卸载 1 个 Onnx 会话（PP-OCRv6 (DirectML, FP16)）；下次识别懒加载
```

进程读数：私有 313.1 MB、GPU Local 85.0 MB，卸载后无崩溃。30 秒是 Timer 的首拍；默认 300 秒阈值下，实际卸载窗口为 300–330 秒。

### 3.3 未覆盖

- 未在真机上再触发一次 OCR 验证“卸载后懒加载重建”的完整链路；
- 未验证 GPU→CPU 切换后的双会话卸载场景（代码路径会一并移除两类 Onnx 引擎）。

## 4. P1：无感截图编码任务上限

`MainWindow` 新增 `MaxPendingEncodeTasks = 4` 与 `PendingEncodeCount()`。`SilentCapture()` 在创建 `CaptureResult` 之前检查：

- 已有 4 个待编码任务 ⇒ 写 Warn 日志并 `ToastService.ShowCaptureFailed("已有多个截图正在编码，请稍后再试")`，直接返回；
- 不再先捕获 126 MB 4K 私有帧再发现队列满；
- 任务完成仍由 `TrackPendingTask` 的 ContinueWith 从 `_pendingEncodeTasks` 移除。

未做“慢格式连按 5 次”的真机压力实测；构建与单元测试通过。

## 5. P2：动图录制峰值

- `AnimationRecorder` 的帧通道有界：`MaxBufferedFrames=30`，4K BGRA8 单帧 31.64 MiB ⇒ 约 **1 GB** 上限。
- `_encodedFrames` 从通道读出同一批数组，编码期间继续持有；ffmpeg 子进程另有内存。
- 这是**有界峰值**，不是按轮泄漏；`EncodeFrames()` 有 `finally` 清空并删除临时目录。

## 6. P2：HDR 渲染线程卡死

- `HdrCaptureWindow.StopRenderThread()` 在 `Join(2000)` 失败时设置 `_renderThreadStuck=true`，跳过后续 D3D 资源释放，避免 use-after-free 的 AccessViolation。
- 单个被放弃的窗口可能留下数百 MB；只在设备丢失/驱动挂起时发生，进程退出回收。
- 保持“安全优先”取舍，不改成强制释放；后续可记录次数并提示重启应用。

## 7. P3 与已核对正常项

低风险未改：

- `OcrPreviewWindow` 下载语言包时每次 `new TranslationPackageManager()`，内部 `HttpClient` 未 Dispose；重复下载会留下连接池/TIME_WAIT。
- `HdrCaptureWindow.s_textCache` 无容量上限，但 key 只来自固定工具栏文本。
- `_activeHdrWindow` 异常路径可能残留已释放窗口引用。

已核对正常：

- `HdrCaptureWindow.s_windows` 销毁时 `TryRemove`；定时器在 `Cleanup()` Dispose；
- `PinWindow.s_windows` 销毁时移除；
- `LogService` UI 环 2000 条、`CoreLog` 早期队列 4000 条，均有上限；
- `TranslationService` 的 3 个 `HttpClient` 是进程级静态复用；
- WGC 的 `s_hdrCapabilityCache` / 软冷却表在显示器变更时清空；
- `HdrCaptureWindow` 的交换链/后台缓冲/桌面纹理/池化 staging 在 `ReleaseDeviceResources` 中逐项释放。

## 8. 验证与残余风险

- 构建：`dotnet build src/TrueToneCap.App/TrueToneCap.App.csproj -c Release -p:Platform=x64`，**0 警告 0 错误**。
- 单元测试：`dotnet run --project src/TrueToneCap.Test/TrueToneCap.Test.csproj -c Release -p:Platform=x64 -- --unit-tests`，**2273 通过、0 失败、1 跳过**。
- P0 真机：3 次同尺寸 `WM_DISPLAYCHANGE` 后 GPU **262.4 / 262.4 / 251.9 MB**，截图均成功。
- OCR 真机：5 秒阈值下 30 秒检查拍成功卸载 DirectML 会话。
- 未做：真实分辨率切换、OCR 卸载后重建、编码队列连拍压力、长 soak。

> 后续若继续处理 P2/P3，优先顺序是：真实尺寸变更去抖 → OCR 重建链路 → 编码压力测试 → 录制峰值提示。
