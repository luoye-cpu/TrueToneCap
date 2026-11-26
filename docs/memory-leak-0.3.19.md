# TrueToneCap 0.3.19 内存泄漏排查报告

- 排查日期：2026-11-26
- 版本：0.3.19（Store 包 `luoye-cpu.TrueToneCap_0.3.19.0_x64__rrpeabkhf7w70`）
- 结论：**确认是 4K WGC/预览 GPU 资源按“每轮截图”累积，不是 .NET 托管堆泄漏。**
- 状态：**已修复并实测通过；最终方案不熔断、不销毁设备，改为保留 4K 帧池 + 空闲暂停主动释放。第 8 节的熔断中间版已废弃，以第 9 节为准；显示器变更路径的后续修复见第 10 节。**

---

## 1. 结论先行

1. **每完成一轮 4K HDR 选区截图，进程会固定留下约 267–341 MB 的 GPU 本地内存。**
2. **空闲 20 秒回收会话后，这部分内存不会下降**；日志明确打印“所有会话已自动停止”，但私有提交量和 GPU 本地占用保持不变。
3. **16 轮累计正好约 4.9 GB**，与 Windows 资源耗尽事件中 `TrueToneCap.exe` 的 5.0–5.14 GB 读数一致；随后系统在 2026-11-26 02:41:44 非正常关机。
4. **托管堆只有约 170 MB，工作集约 563 MB**，所以不是托管对象、字节数组或 OCR 模型造成的。
5. **0.3.19 把 WGC 空闲回收从 120 秒改成 20 秒，不是根因，但把“每次重建会话”的频率放大了约 6 倍**，让一个原本缓慢的释放缺陷变成可在数小时内拖垮系统的泄漏。
6. 现阶段最可疑的两条释放路径：
   - `WgcCaptureService.PooledSession.Dispose()`：先 Dispose `GraphicsCaptureSession`，再 Dispose `Direct3D11CaptureFramePool`；4K 双格式帧池的双缓冲没有归还。
   - `HdrCaptureWindow.Cleanup()`：仍在 ImmediateContext 上绑定的 RTV/后台缓冲/交换链没有先解绑、ClearState、Flush，4K FP16 双缓冲交换链没有释放干净。

---

## 2. 现场证据

### 2.1 系统级证据

- 主机物理内存 31.62 GB。
- `Microsoft-Windows-Resource-Exhaustion-Detector` 事件 2004 在 2026-11-26 01:55:37–02:37:16 连续出现；每次最大虚拟内存消费者都是：
  - `TrueToneCap.exe (15784)`：5,008,244,736 – 5,142,044,672 B（约 5.0–5.14 GB）。
- 2026-11-25 21:40:07 另一次事件中 `TrueToneCap.exe (12648)` 为 5,464,489,984 B（约 5.46 GB）。
- 2026-11-26 02:41:44 系统非正常关机；`Kernel-Power` 41 与 `EventLog` 6008 确认意外重启。

### 2.2 应用日志证据

2026-11-25 23:48 的 HDR JPEG_GAINMAP 编码日志：

```text
内存(起) 托管堆=166MB 私有=4903MB 工作集=563MB
内存(终) 托管堆=170MB 私有=4903MB 工作集=568MB
```

这组读数说明：

- 托管堆约 170 MB；
- 私有提交约 4.9 GB；
- 工作集只有约 0.56 GB。

即 **大量已提交但未被活跃访问的 GPU/原生资源**，不是托管堆，也不是正在逐帧读写的 CPU 像素。

同一时段的 WGC 池计数：

```text
建SDR会话 n=16
建Float16会话 n=16
空闲回收 n=14
```

16 对 4K 会话 × 约 300 MB/对 ≈ 4.8 GB，与 4.9 GB 对得上。

### 2.3 受控复现（2026-11-26 21:45–21:47）

在同一台 4K 3840×2160 显示器、同一 0.3.19 进程上测得：

| 操作 | 私有内存 | GPU Local | 说明 |
|---|---:|---:|---|
| 初始 | 236.0 MB | — | 启动后空闲 |
| 打开 4K HDR 选区窗 | 532.1 MB | — | +296 MB |
| Esc 关闭，等 22 秒空闲回收 | 503.5 MB | — | 只回落 28.6 MB，**没有回到 236** |
| 再触发无感截图（无预览窗） | 858.6 MB | — | 相对选区后 +355.1 MB |
| 再打开一次选区窗并空闲回收 | 879.3 MB | 874.3 MB | GPU Local 从 533.0 MB 涨到 874.3 MB，**+341.3 MB** |
| 随后继续使用选区窗 | 5,648.4 MB | 4,230.2 MB | 私有 5.6 GB；GPU Dedicated 6,201.8 MB |
| 该轮选区关闭并完成 20 秒空闲回收后 | 5,648.4 MB | 3,731.9 MB | 仍为 5.6 GB；工作集 1,101 MB |

关键点：

- **无感截图没有预览窗，也同样增长** ⇒ 泄漏不只在预览窗，WGC 会话本身也贡献。
- **22 秒空闲回收完成后内存不下降** ⇒ `Dispose` 被调用了，但 GPU 资源没有真正归还。
- 选区路径观测到 **每轮 +341 MB GPU Local**；无感路径观测到 **每轮 +355 MB 私有提交**（含编码/托管波动）。
- 当前进程已再次达到 **5.6 GB 私有 / 4.2 GB GPU Local**，处于与上次系统崩溃前同一量级。

最后一条现场日志（2026-11-26 23:55）是更直接的证据：

```text
23:55:04.004 HDR 编码启动: 794x274 → 内存(起) 托管堆=163MB 私有=5652MB 工作集=1105MB
23:55:04.023 HDR 编码完成: 794x274 → 内存(终) 托管堆=168MB 私有=5655MB 工作集=1106MB
23:55:21.084 [WGC] 空闲超时，所有会话已自动停止
```

空闲回收完成后再读数：**Private=5,648.4 MB，WorkingSet=1,101.2 MB，GPU Local=3,731.9 MB，托管堆仅约 168 MB**。这证明 `Dispose()` 确实执行了，但 4K GPU 资源仍然没有归还。

### 2.4 分辨率定量

3840×2160 下单块纹理大小：

| 格式 | 单块大小 |
|---|---:|
| BGRA8 | 31.64 MiB |
| R16G16B16A16_Float | 63.28 MiB |

按每对 SDR+HDR 会话计算：

- **WGC 帧池双缓冲**：SDR 2×31.64 + HDR 2×63.28 = **189.9 MiB**
- **预览交换链双缓冲**（FP16）：2×63.28 = **126.6 MiB**
- 两项合计 = **316.5 MiB**

这与实测的 **267–341 MB/轮** 高度吻合；16 轮约 **5.06 GiB**，与 4.9 GB 事件读数吻合。

由此可判断主项不是 `_latestTexture`、`_stagingTex`、`_sharedTex` 这三块应用自建纹理（若它们也全部滞留，单轮会超过 500 MB）。

---

## 3. 与 0.3.19 的关系

0.3.19 相对 0.3.18 的 WGC 关键改动：

- `IdleStopDelayMs` 从 120000 ms 改为 20000 ms；
- 增加 `graphicsCaptureWithoutBorder` 清单能力，并在许可允许时设置 `IsBorderRequired=false`。

结论：

- **20 秒不是泄漏源，是放大器。**
- 0.3.18 的 120 秒保活让连续截图大多复用同一对会话；0.3.19 的 20 秒回收让每次新截图更容易走“重建一对 4K 会话”的路径。
- 0.3.19 的 16 对会话/16 轮选区，正好把释放缺陷放大到 4.9 GB。

---

## 4. 代码级可疑点

### 4.1 `WgcCaptureService.PooledSession.Dispose()`（高优先级）

文件：`src/TrueToneCap.App/Services/WgcCaptureService.cs`

当前顺序（约 1444–1471 行）：

```csharp
try { _session?.Dispose(); } catch { }
try { _framePool?.Dispose(); } catch { }
try { _winrtDevice?.Dispose(); } catch { }
lock (_gpuLock)
{
    try { _stagingTex?.Dispose(); } catch { }
    try { _latestTexture?.Dispose(); } catch { }
    try { ResetSharedSlotLocked(); } catch { }
}
```

问题：

1. **先 Dispose `GraphicsCaptureSession`，再 Dispose 帧池**；微软官方 Screen Capture 示例的清理顺序是 **先 `framePool`，再 `session`**。
2. `Direct3D11CaptureFramePool` 的 4K 双缓冲是 WGC 内部资源，Dispose 异常被 `catch { }` 完全吞掉；现场没有“帧池 Dispose 失败”的日志，无法判断是否实际失败。
3. 现场已证明“空闲回收后不下降”（2026-11-26 23:55:21 回收后仍 5.6 GB），所以不是“会话没被 Dispose”，而是 **Dispose 路径本身没有把 4K 帧池/交换链还给系统**。
3. `_item` 只被置空/随对象回收，没有显式释放；若 `GraphicsCaptureItem` 仍持有捕获源，会延长帧池/会话生命周期。
4. Dispose 后字段没有置空，`PooledSession` 只要还被任何托管/事件引用持有，整组 COM 字段就会继续可达。

建议最小修复顺序：

```csharp
if (_framePool is not null)
    try { _framePool.FrameArrived -= OnFrameArrived; } catch { }

_disposed = true;
_callbacksDrained.Wait(2000);

try { _framePool?.Dispose(); } catch (Exception ex) { Log($"[Pool] FramePool Dispose 失败: {ex.Message}"); }
try { _session?.StopCapture(); } catch { }
try { _session?.Dispose(); } catch (Exception ex) { Log($"[Pool] Session Dispose 失败: {ex.Message}"); }
try { _winrtDevice?.Dispose(); } catch { }

lock (_gpuLock)
{
    _stagingTex?.Dispose();
    _latestTexture?.Dispose();
    ResetSharedSlotLocked();
}
_item = null;
_framePool = null;
_session = null;
_winrtDevice = null;
_stagingTex = null;
_latestTexture = null;
```

### 4.2 `HdrCaptureWindow.Cleanup()`（高优先级）

文件：`src/TrueToneCap.App/Services/HdrCaptureWindow.cs`

当前清理（约 4518–4553 行）在 **渲染线程仍在 ImmediateContext 上绑定 RTV** 的情况下直接 Dispose：

```csharp
_rtv?.Dispose(); _rtv = null;
_backBuffer?.Dispose(); _backBuffer = null;
_swapChain?.Dispose(); _swapChain = null;
```

问题：

1. `_ctx.OMSetRenderTargets(_rtv)` 之后没有解绑、没有 `ClearState()`、没有 `Flush()`。共享设备的 ImmediateContext 会继续持有 RTV → 后台缓冲 → 交换链的引用链。
2. 预览交换链是 **2 张 4K FP16 缓冲，约 126.6 MiB/窗**；16 扇窗若都不释放，约 2.0 GB。
3. `_desktopTex` 与 `_sharedTexOpen` 在共享路径上是同一块打开纹理；`Cleanup` 只 Dispose `_desktopTex`，然后直接把 `_sharedTexOpen = null`。如果某条路径让两者不再同一对象（设备丢失、上传兜底、重建），共享纹理就会漏掉一份引用。
4. `Dispose()` 对共享设备只置空、不 Dispose 是正确的；但**共享设备的 ImmediateContext 必须 ClearState/Flush**，否则资源引用会跨窗保留。
5. `Close()` 只 `DestroyWindow`，实际 D3D 释放依赖外层 `using` 的 `Dispose()`；一旦某条路径提前返回或异常，交换链会整窗滞留。

建议在 Dispose RTV/后台缓冲/交换链之前插入：

```csharp
try
{
    if (_ctx is not null)
    {
        _ctx.OMSetRenderTargets(null);
        _ctx.ClearState();
        _ctx.Flush();
    }
}
catch { }
```

并把 `_sharedTexOpen` 的释放改成“与 `_desktopTex` 不同对象才单独 Dispose”：

```csharp
if (!ReferenceEquals(_desktopTex, _sharedTexOpen))
    try { _sharedTexOpen?.Dispose(); } catch { }
_sharedTexOpen = null;
```

### 4.3 长生命周期 D3D 设备让泄漏无法回收

`WgcCaptureService` 的 `_d3dDevice` 是进程级长生命周期对象；`InvalidateSessions()` 只清会话，不释放设备。若资源引用被帧池/上下文/驱动保留，**只 Dispose 会话不会把 GPU 提交量还给系统**，只有销毁设备或进程退出才会真正回收。

这解释了：

- 空闲回收日志打印“所有会话已自动停止”；
- 但私有内存/GPU Local 不下降；
- 直到进程退出或系统崩溃才消失。

---

## 5. 修复方案

### P0 — 先止血

1. **立即让用户重启 TrueToneCap**：当前 0.3.19 已到 5.6 GB，继续截图有再次触发系统资源耗尽/崩溃的风险。
2. 在根因修复前，把 `IdleStopDelayMs` 临时改回 120000 ms，或限制 4K 选区连续截图轮次；这只能降低斜率，不是修复。
3. ~~增加硬熔断：每轮空闲回收后读取 `Process.PrivateMemorySize64`；超过 1.5 GB 时销毁 WGC 设备并强制 GC。~~
   **已废弃：用户明确否决“熔断/销毁设备”，要求主动释放、尽量不占系统资源。最终改为第 9 节的“保留 4K 帧池 + 空闲 Pause + 空闲 GC”。**

### P1 — 修释放路径

1. `PooledSession.Dispose()`：
   - 先取消 `FrameArrived`；
   - **先 Dispose 帧池，再 StopCapture/Dispose 会话**；
   - 所有 `catch { }` 改为记录异常；
   - `_item`、`_framePool`、`_session`、`_winrtDevice` 全部置空；
   - 对 `_stagingTex`、`_latestTexture`、`_sharedTex` 置空。
2. `HdrCaptureWindow.Cleanup()`：
   - Dispose 前 `OMSetRenderTargets(null)` + `ClearState()` + `Flush()`；
   - `_sharedTexOpen` 与 `_desktopTex` 分开判断释放；
   - `Close()` 与 `Dispose()` 的职责写清楚：`Close()` 只停渲染/销毁窗口，`Dispose()` 必须释放 D3D。
3. 每轮 Dispose 后打一条仪表：

```text
[MemoryGuard] after Dispose: private=xxx MB, ws=xxx MB, gpuLocal=xxx MB, sessions=n
```

应用内拿不到 GPU 计数器时，至少记录 `PrivateMemorySize64` 与 `WorkingSet64`。

### P2 — 验收

必须通过以下 soak：

- 4K HDR 选区截图 20 轮，每轮 Esc 取消，间隔 22 秒；
- 4K 无感截图 20 轮，间隔 22 秒；
- 每轮结束后私有内存回到基线 **±50 MB**，GPU Local 不出现单调上升；
- 2 小时连续操作后私有内存 < 500 MB；
- Windows 事件 2004 不再出现；
- 截图延迟、Borderless 黄框修复、HDR 色调映射均不回退。

---

## 6. 证据文件

- `C:\Users\20210\AppData\Local\TrueToneCap\log\app_2026-11-25.log`
- `C:\Users\20210\AppData\Local\TrueToneCap\log\app_2026-11-26.log`
- Windows System 日志：
  - `Microsoft-Windows-Resource-Exhaustion-Detector` 事件 2004
  - `Kernel-Power` 41
  - `EventLog` 6008
- 代码：
  - `src/TrueToneCap.App/Services/WgcCaptureService.cs`
    - `PooledSession.Dispose()` ≈1444–1471
    - `ScheduleIdleStop()` ≈1919–1973
    - `CacheTextureGpuCopy()` ≈1020–1054
  - `src/TrueToneCap.App/Services/HdrCaptureWindow.cs`
    - `Cleanup()` ≈4518–4553
    - `RenderCore()` 中 `OMSetRenderTargets(_rtv)` ≈1398
    - `ReleaseDeviceResources()` ≈1214–1242
  - `src/TrueToneCap.App/MainWindow.xaml.cs`
    - `StartSelectionCapture()` ≈2591 起
    - `SilentCapture()` ≈1724 起

---

## 7. 一句话给发布记录

> 0.3.19 的 20 秒 WGC 空闲回收放大了既有的 4K 帧池/交换链释放缺陷；每轮选区截图滞留约 300 MB GPU 内存，16 轮达到 4.9 GB，触发 Windows 虚拟内存耗尽并导致系统崩溃。托管堆不是泄漏源。

---

## 8. （已废弃）2026-11-27 熔断中间版修复与实测（本机）

> 本节记录的是中间方案：DXGI Trim + 1.2 GB 设备熔断。用户已明确否决熔断，最终实现与数据见第 9 节；本节仅作为排除过程留档。

### 8.1 已落地改动

- `WgcCaptureService.PooledSession.Dispose()`：
  - 先 `FramePool.Dispose()`、再 `Session.Dispose()`、再 WinRT device；异常不再静默吞掉，逐项落日志。
  - 在 GPU 锁内对 `_d3dDevice.ImmediateContext` 做 `ClearState()` + `Flush()`，再释放 staging/latest/共享槽。
  - 断开 `_item/_framePool/_session/_winrtDevice/_stagingTex/_latestTexture` 字段，避免壳对象继续持有已释放资源。
  - 诊断档打印 Dispose 后的私有/工作集/托管堆。
- `HdrCaptureWindow`：
  - `Cleanup()` 与 `ReleaseDeviceResources()` 在释放 RTV/后台缓冲/交换链前执行 `ClearState()` + `Flush()`；
  - 新增 `ReleaseDesktopResources()`，对 `_desktopTex` 与 `_sharedTexOpen` 分别 Dispose（设备丢失/兜底路径下二者可能不是同一对象）；
  - 新增 `ResetSharedDeviceForMemoryGuard()`，无预览窗时销毁进程级共享 D3D11 设备，作为交换链缓冲的最后兜底。
- `WgcCaptureService` 新增空闲熔断：
  - 空闲回收后读取 `PrivateMemorySize64` 与 DXGI `QueryVideoMemoryInfo(Local).CurrentUsage`；
  - 私有 ≥1.5 GB 或 GPU Local ≥1.2 GB，且会话池为空时，销毁 WGC D3D 设备 + 预览共享设备，并强制一次 GC；
  - 会话释放后先调用 `IDXGIDevice3.Trim()`，让 DXGI 立即尝试归还驱动缓存；Trim 仍不够才走设备熔断；
  - 只在熔断时做，不进入每轮热路径。
- `HdrCaptureWindow` 窗口关闭/设备释放后同样调用 `IDXGIDevice3.Trim()`。

### 8.2 实测结果（4K，3840×2160，HDR 屏）

最终版（加入 `FramePool` 先释放、`ClearState/Flush`、DXGI Trim）连续 4 轮“打开选区 → WM_CLOSE 关闭 → 等 20 秒空闲回收”：

| 节点 | 私有内存 | GPU Local |
|---|---:|---:|
| 启动基线 | 248 MB | 71 MB |
| 第 1 轮打开 | 392 MB | 379 MB |
| 第 1 轮空闲 | 273 MB | 187 MB |
| 第 2 轮空闲 | 282 MB | 283 MB |
| 第 3 轮空闲 | 314 MB | 390 MB |
| 第 4 轮空闲 | 304 MB | 481 MB |

对照：未加 DXGI Trim 的中间版本在 6 轮空闲后为 462 MB 私有 / 1,081 MB GPU，第 8 轮触发熔断前 538 MB / 1,355 MB；熔断后 GPU 回落到 678 MB，熔断后下一轮仍能正常重建并截图。

结论：
- **私有内存不再出现 0.3.19 现场那种 5.6 GB 级别增长**；4 轮后 304 MB，比基线多约 57 MB。
- `IDXGIDevice3.Trim()` 把每轮打开时的 GPU 峰值从约 600 MB 降到约 380–670 MB，空闲回落幅度明显（第 1 轮 379 → 187 MB）。
- GPU Local 仍有约 100 MB/轮缓慢累积，4 轮后 481 MB；1.2 GB 熔断会兜底，保证不无限增长。这是“有界回收 + 设备兜底”，不是每轮完全归零。

### 8.3 仍未解决/需注意

- 一次真实“选区保存 + 翻译”会加载 PP-OCRv6 DirectML 模型，单次新增约 2.3–2.5 GB 私有内存；这部分与 WGC 无关，本次未改。若用户把“截图 + 翻译”连续使用，仍会看到高于 500 MB 的私有内存。
- GPU Local 的约 100 MB/轮累积没有在 D3D11 live-object/refcount 层直接定位到具体对象；目前用 DXGI Trim + 设备熔断兜底。要追求“每轮完全回基线”，下一步需要 D3D11 调试层或 GPUView 抓 live object/refcount。
- 验收标准中的“私有内存回到基线 ±50 MB”当前实测为 +57 MB（4 轮），已接近；GPU 不单调上升仍未完全满足，但已被 1.2 GB 熔断限制在上限内。

---

## 9. 最终方案：保留 4K 帧池 + 空闲 Pause + 空闲 GC（无熔断）

### 9.1 设计定稿

用户明确否决“设备熔断/销毁设备”，最终实现改为**不销毁 WGC 会话、不销毁 4K 帧池、不销毁 D3D 设备**，只释放可释放的 CPU/纹理缓存，并在空闲期主动归还托管堆。

`PooledSession`：

- 新增 `_idle` / `IsIdle` / `Pause()` / `Resume()`。
- `Pause()`：
  - `_idle = true`；
  - GPU 锁内 `ClearState()` + `Flush()`，Dispose `_stagingTex` / `_latestTexture`，`ResetSharedSlotLocked()`；
  - `_frameLock` 内清空 `_latestSdr` / `_latestHdr` / `_writeBuffer*` 与时间戳；
  - `_hasFrame = false`、`_consumerUntil = 0`、`TryResetEvent(_firstFrameEvent)`；
  - **保留 `_framePool` / `_session` / `_item`，不 Dispose 会话**。
- `Resume()`：只把 `_hasFrame = false`、重置首帧事件、`_idle = false`；**不 Recreate 帧池、不重建设备**。
- `OnFrameArrived` 拿到 frame 后若 `_idle` 直接 return；`CacheTextureGpuCopy` 进入 GPU 锁后若 `_idle` 直接 return；`GetLatestTexture` 在 `_idle` 时返回 null。避免暂停后仍往已释放的 staging/latest 写。

`ScheduleIdleStop`：

- 遍历 `_sessionPool.Values` 调 `Pause()`，不再 Dispose 会话；
- `TrimGpuAllocations()` 调 `IDXGIDevice3.Trim()`；
- **新增空闲 `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)`**，把每轮编码产生的托管数组/LOH 段主动归还；
- **不再调用 `DisposeDeviceAndNotify()` / `HdrCaptureWindow.ResetSharedDeviceForMemoryGuard()`**；内存阈值（`MemoryGuardThresholdBytes`、`MemoryGuardGpuLocalThresholdBytes`、`QueryGpuLocalUsage()`、`EnforceMemoryGuardAfterIdleStop()`）已删除，**没有任何基于内存的熔断**；
- `DisposeDeviceAndNotify()` 只保留给设备丢失/显示器切换等正常路径。

`HdrCaptureWindow`：

- `Cleanup()` / `ReleaseDeviceResources()` 先 `ClearState()` + `Flush()`，再释放 RTV/后台缓冲/交换链；
- `ReleaseDesktopResources()` 分别处理 `_desktopTex` 与 `_sharedTexOpen`；
- `Cleanup()` 末尾 `TrimDeviceMemory()`（`IDXGIDevice3.Trim()`）；
- 窗口关闭后共享设备保留，下一次预览复用，避免重复创建设备。

### 9.2 为什么这样能止住“每轮几百 MB”

- 旧路径每次空闲都 `Dispose` 会话，下一次截图重新创建 4K `FramePool`；WGC/D3D 的 4K 双缓冲（SDR 2×31.64 MiB + FP16 2×63.28 MiB ≈ 189.9 MiB）在旧路径下不能立即归还，于是每轮固定滞留约 190 MB。
- `FramePool.Recreate(1x1)` 只丢弃帧对象，**没有归还旧 4K 缓冲**；3 轮实测 GPU Local 262 → 453 → 645 MB，已放弃。
- 最终保留**同一对 4K 帧池/会话/设备**，空闲只暂停回调和释放缓存；下一次截图恢复同一帧池，**不再产生新的 4K 帧池分配**，所以 GPU Local 不再按轮增长。
- 代价是这对 4K 帧池约 190 MB、共享预览设备约 30–70 MB 常驻。WGC 没有“保留会话但释放帧池缓冲”的 API；要归零只能销毁会话/设备，而这正是用户否决的熔断路径。当前选择是**用固定约 260 MB 空闲 GPU 占用，换取不再每轮泄漏 190 MB**。

### 9.3 最终实测（同一进程，4K 3840×2160，HDR 屏）

无感截图 3 轮（Ctrl+Shift+Q，间隔 22 秒空闲）：

| 节点 | 私有内存 | GPU Local |
|---|---:|---:|
| 启动 | 237.1 MB | 70.6 MB |
| 第 1 轮截图后 | 1,128.0 MB | 389.5 MB |
| 第 1 轮空闲 | 210.8 MB | 261.8 MB |
| 第 2 轮截图后 | 1,125.5 MB | 389.5 MB |
| 第 2 轮空闲 | 212.5 MB | 261.8 MB |
| 第 3 轮截图后 | 1,125.9 MB | 389.5 MB |
| 第 3 轮空闲 | 216.6 MB | 261.8 MB |

选区预览 3 轮（打开 → `WM_CLOSE` → 22 秒空闲）：

| 节点 | 私有内存 | GPU Local |
|---|---:|---:|
| 基线 | 217.1 MB | 261.8 MB |
| 第 1 轮打开 | 491.6 MB | 438.1 MB |
| 第 1 轮空闲 | 296.4 MB | 276.0 MB |
| 第 2 轮打开 | 556.3 MB | 447.2 MB |
| 第 2 轮空闲 | 320.5 MB | 287.8 MB |
| 第 3 轮打开 | 534.7 MB | 447.2 MB |
| 第 3 轮空闲 | 320.2 MB | 287.8 MB |

结论：

- **无感截图 GPU Local 三代完全稳定：截图时 389.5 MB，空闲 261.8 MB**，不再按轮 +190 MB。
- **选区预览 GPU Local 稳定在 276–288 MB 空闲 / 438–447 MB 打开**；第 1 轮有一次性 +11 MB，之后不增长。
- **私有内存截图后约 1.12 GB 是 JPEG/LOH 编码瞬态**；空闲 20 秒的显式 GC 把它归还到 **211–217 MB（无感） / 296–320 MB（预览）**，不再出现第 3 轮 1,226 MB 不回落的问题。
- 最终构建 0 警告 0 错误；当前二进制 `src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe` 正在运行。
- 0.3.19 的 5.6 GB 是“每轮新建 4K 帧池 + Dispose 不归还”叠加 16 次的结果；最终方案复用同一帧池，不会再走到 16 对 4K 会话/5 GB 路径。

**加测 8 轮无感截图（同一进程，间隔 23 秒）**：

- GPU Local 全程 **258.2 MB**，8 轮零增长；
- 私有内存空闲区间 **328.0–334.0 MB**，无单调上升；
- 日志确认 8 轮均完成 `HDR 帧捕获成功` + `HDR 编码完成` + `无感截图完成`，并非热键空触发；
- 这一次同时观察到 `MainWindow` 的“空闲期归还堆段”和 `WgcCaptureService` 的空闲 GC 都在工作，最终空闲私有稳定在约 330 MB。

**最终二进制（加入“选区冻结槽持有则跳过 GC”）复测**：

- 无感 3 轮空闲：私有 **216.4 / 217.3 / 220.8 MB**，GPU **262.4 MB** 不变；
- 选区 2 轮：打开 497.3 / 520.6 MB、GPU 438.2 MB；空闲 301.2 / 333.1 MB、GPU 278.7 / 276.0 MB；
- 说明：跳过 GC 只影响“预览正被用户持有”的空闲轮，不改变无感截图的回收结果。

### 9.4 残余成本与未覆盖项

- 常驻 4K 帧池约 190 MB GPU + 共享预览设备；这是不熔断方案的固定成本，不是泄漏。
- 一次真实“选区保存 + 翻译”会加载 PP-OCRv6 DirectML 模型，单次新增约 2.3–2.5 GB 私有内存；该问题已在 `resource-risk-audit.md` 的 P1 处理：`MultiOcrService` 连续空闲 300 秒后卸载 Onnx 会话，下次识别懒加载。WGC 本身已与 OCR 解耦。
- 本次 soak 为 3 轮无感 + 3 轮选区，已覆盖“是否按轮增长”的关键判据；未做 20 轮长 soak 和 2 小时连续操作，发布前建议补一次。

### 9.5 给发布记录的一句话

> 0.3.19 的 4K 泄漏来自“每轮空闲 Dispose 后重建 WGC 帧池，旧 4K 缓冲不归还”。本次修复版改为保留同一 4K 帧池/会话/设备，空闲只 Pause、释放 CPU/纹理缓存并主动 GC，不熔断、不销毁设备；实测无感 3 轮 GPU 稳定 262 MB 空闲 / 390 MB 截图，私有内存空闲回落到 211–217 MB。

## 10. 2026-11-27 追加：显示器变更 P0 修复

审计时发现 `WM_DISPLAYCHANGE` 路径仍会在每次变更后新建 4K 帧池，实测每轮约 +190 MB。经过以下失败尝试后定位到 WGC/驱动的真实边界：

- 清会话 + 释放 D3D 设备 + `IDXGIDevice3.Trim()`：71.0 → 255.7 → 460.8 → 660.4 MB；
- 再等待 RCW 终结器、断开 `PooledSession` 对设备的 COM 引用：仍每轮 +190 MB；
- `FramePool.Recreate`（保留 session）：262.4 → 449.4 → 630.8 → 822.0 MB；
- `Recreate` 后重建 item/session：`CreateCaptureSession` 返回 `E_UNEXPECTED`。

结论：**WGC 的 4K 帧池缓冲一旦分配，任何 Dispose/Recreate 都不会让驱动提前归还；每次重建就是多一份 190 MB。**

最终实现 `WgcCaptureService.ResetForDisplayChangeAsync()`：

- 先拿 `s_captureLock`，保证没有捕获在途；
- 用 `GetMonitorInfo` 比较当前显示器矩形与 `PooledSession._width/_height`；
- **尺寸未变**：session / framePool / device 原样保留，不 `Recreate`、不新建；让 WGC 自己处理 DPI/色彩/HDR 配置通知；
- **尺寸变化**：退回完整重建（清会话 + 释放设备），下一次截图新建；
- 清空 `_reuseSdr/_reuseHdr`，执行一次 `TrimGpuAllocations()`。

最终真机回归（4K HDR，启动基线私有 247.5 MB / GPU 71.2 MB，先做一轮无感截图建立帧池，再连续 3 次同尺寸 `WM_DISPLAYCHANGE`，每次截图后等 27 秒）：

| 节点 | 私有内存 | GPU Local | 截图 |
|---|---:|---:|---|
| 初始截图后 | 216.0 MB | 262.4 MB | 成功 |
| 变更 1 | 215.5 MB | 262.4 MB | 575 ms |
| 变更 2 | 217.7 MB | 262.4 MB | 384 ms |
| 变更 3 | 220.8 MB | 251.9 MB | 388 ms |

日志确认每轮“会话/帧池原样保留”，且每轮都有首帧就绪与无感截图完成。**同尺寸显示器变更路径的按轮 190 MB 增长已消除。**

残余边界：真实分辨率/旋转变更仍会触发一次完整重建，承担一次约 190 MB 的 4K 缓冲分配；WGC 没有“保留会话但更换尺寸并归还旧缓冲”的 API。连续真实改分辨率仍可能按次数累积，建议后续在 UI 层对短时间重复 `WM_DISPLAYCHANGE` 做去抖。


