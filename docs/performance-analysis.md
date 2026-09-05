# TrueToneCap 综合性能优化分析报告 (2026-08-25)

> 基于全链路源码深度分析，覆盖捕获 → 预览 → 处理 → 编码 → 输出全管线

---

## 1. 捕获管线 (WgcCaptureService)

### 1.1 现状性能

| 操作 | 4K 耗时 | 说明 |
|------|---------|------|
| FrameArrived → staging copy (SDR) | ~3-5ms | 三缓冲 + Staging 纹理复用 + GPU 锁 500ms 超时保护 |
| FrameArrived → staging copy (HDR) | ~5-8ms | Float16 格式 + `ConvertHalfToFloatRow` SIMD 转换 |
| GetLatestSdr/GetLatestHdr (安全拷贝) | ~0.5-1ms | `Buffer.BlockCopy` 全量拷贝 |
| CacheTextureGpuCopy (GPU 纹理直通) | ~1-2ms | `CopyResource` Default→Default 纹理 |
| CaptureAllMonitors (双 4K 拼接) | ~15-25ms | `Parallel.ForEach` 按显示器并行 |

### 1.2 当前瓶颈

**已优化到位**：三缓冲、Staging 纹理复用、GPU 纹理直通、适配器缓存、ABBA 死锁修复。
**可优化空间有限**：

| 方向 | 收益 | 复杂度 | 说明 |
|------|------|--------|------|
| 🔹 `GetLatestSdr()` 零拷贝 | 中 (~0.5ms) | 高 | 当前安全拷贝防止跨线程竞态。改为 `_latestSdr.AsSpan().ToArray()` 或 `ArrayPool<byte>.Shared.Rent` + 引用计数会增加复杂度，收益有限 |
| 🔹 `ConvertHalfToFloatRow` SIMD AVX-512 | 低 (~1ms) | 中 | 当前已用 `PixelOps` SIMD，4K 已极快 |
| 🔹 `CaptureAllMonitors` 多显示器并行 | 低 | 低 | 当前 `Parallel.ForEach` 已是最优 |

**结论**：捕获管线已高度优化，无需进一步干预。

---

## 2. 编码管线 (CapturePipelineService + FormatEncoders)

### 2.1 现状性能

| 格式 | 4K 编码耗时 | 说明 |
|------|-----------|------|
| PNG (托管) | ~50-150ms | 纯 CPU, 8/10/12/16-bit |
| JPEG LI (jpegli) | ~80-200ms | 子进程 cjpegli |
| JPEG XL (cjxl) | ~200-800ms | 子进程 cjxl, 编码最慢 |
| AVIF libaom (CPU) | ~500-2000ms | CPU 软件编码 |
| AVIF NVENC (GPU) | ~15-25ms | GPU 纹理直通 |
| WebP (cwebp) | ~100-400ms | 子进程 |
| TIFF (托管) | ~30-100ms | 纯 CPU |
| Gain Map (jpegli+增益图) | ~150-400ms | 双 JPEG 编码 |

### 2.2 当前瓶颈

**P1 编码耗时集中点**：
```
EncodeAndSaveAsync()
  └─ Task.Run (后台线程)
       ├─ PreparePixelsWithIcc() — ICC 烘焙 + 色域转换    ~5-30ms
       └─ EncodeSyncSdr() — 实际编码                       ~50-2000ms
            └─ encoder.EncodeSdrAsync().GetAwaiter().GetResult()
```

**P2 剪贴板路径冗余**：
```
EncodeAndCopyAsync()
  └─ EncodeAndSaveAsync() 完整编码到临时文件    ~50-2000ms
  └─ StorageFile.GetFileFromPathAsync()           ~2-5ms
  └─ Clipboard.SetContent()                       ~1-2ms
  └─ 临时文件残留 (3秒后删除)                     ~0ms
```

### 2.3 优化建议

| 序号 | 优化点 | 收益 | 复杂度 | 工作量 | 说明 |
|------|--------|------|--------|--------|------|
| **P1** | **编码取消报告** | 低延迟感知 | 低 | 1h | 当前 `EncodeAndSaveAsync` 全程无进度报告。编码 >500ms 时用户无反馈。方案：`CapturePipelineService` 加 `IProgress<int>` 回调，UI 显示"编码中 (50%)" |
| **P2** | **剪贴板 Skip 编码复用** | 中 | 中 | 4h | 当前 `EncodeAndCopyAsync` 完整编码到临时文件再复制。若编码格式与上次保存相同，可复用已保存的文件直接复制 → 跳过编码步骤 |
| **P3** | **PNG 编码并行化** | 中 (~2x) | 低 | 2h | 托管 PNG 编码器 `ManagedPngEncoder` 单线程。可改为 `Parallel.For` 行并行 (filter/CRC 行独立) |
| **P4** | **AVIF libaom 预提交降级** | 高 | 低 | 1h | 当前 `avifenc` 子进程 CPU 编码 4K 500ms-2s。若 `AvifBackendCbo=Auto` 且 NVENC 上次失败，缓存结果避免每次重试 NVENC。NvencAvifBackend 失败后自动回退 libaom，但下次调用仍会尝试 NVENC → 浪费每次重试开销 |
| **P5** | **Task.Run 取消速通** | 中 | 低 | 1h | `EncodeAndSaveAsync` 中 `Task.Run(() => { ... EncodeSyncSdr(...); })` 被 `CancellationToken` 取消时，`ct.ThrowIfCancellationRequested()` 只在编码前检查。编码中取消需要 `encoder.EncodeAsync` 支持 CancellationToken 传播（当前大多数编码器不支持中断） |

---

## 3. 色彩/色调映射管线 (ToneMapper + ColorProfileProvider)

### 3.1 现状性能

| 操作 | 4K 耗时 | 说明 |
|------|---------|------|
| `FloatToSRgbBytes` (融合内核) | ~58ms | `Parallel.For` + 4096 LUT gamma，已高度优化 |
| `PreparePixelsWithIcc` (ICC 烘焙) | ~5-20ms | 色域矩阵 + 像素转换 |
| `PrepareFloat16WithIcc` (HDR→SDR) | ~60-80ms | 矩阵转换 + 色调映射 + gamma |
| `HdrToPq16` (scRGB→PQ) | ~10-20ms | 融合循环 |
| `LinearToSrgbLut` (查表) | ~0.003ms/像素 | 4096 项 LUT + 线性插值，误差 <0.000016 |

### 3.2 当前瓶颈

**已优化到位**：LUT gamma、SIMD 加速、`Parallel.For` 融合内核。
**可优化空间**：

| 方向 | 收益 | 复杂度 | 说明 |
|------|------|--------|------|
| 🔹 `FloatToSRgbBytes` **AVX-512 加速** | 中 (~2x) | 中 | 当前 `Parallel.For` 标量循环。像素级操作可向量化：`Vector256<float>` 一次 8 像素加载 → SIMD 色调映射+gamma → 量化。但 4K 58ms 已不是瓶颈 |
| 🔹 `PreparePixelsWithIcc` **预计算色域矩阵** | 低 | 低 | 色域矩阵 3×3 已在 `ColorSpaceConverter` 缓存，无须优化 |
| 🔹 **GPU 色调映射激活** | 高 (~10x) | 高 | `GpuToneMapper` 是架构资产 (生产零调用)。激活收益：Float16→SDR 从 58ms → ~2ms (GPU PS)。但需处理：HLSL 着色器加载、设备丢失恢复、多适配器兼容性 |

**结论**：CPU 色调映射当前 58ms/4K 已足够，非瓶颈。GPU 色调映射激活是 **远期收益最大** 的方向。

---

## 4. 预览/选区/UI 管线

### 4.1 现状性能

| 操作 | 4K 耗时 | 说明 |
|------|---------|------|
| `SelectionOverlay` 窗口创建+加载 | ~20-50ms | XAML 窗口 + WriteableBitmap 加载 |
| `SelectionOverlay` 像素提取 | <1ms | `Buffer.BlockCopy` 行复制 |
| `SelectionOverlay` 标注合成 | ~10-50ms | `Task.Run` + `AnnotationRasterizer.RenderLayer` |
| **HdrCaptureWindow** GPU 合成 | ~2-5ms | Direct3D 11 OverlayComposite PS |
| HdrCaptureWindow CPU 回退合成 | ~20-30ms | `CompositeUI` + `Parallel.For` 混合 |
| `AnnotationWindow` 窗口创建+加载 | ~20-60ms | `SoftwareBitmap` + `SetBitmapAsync` |

### 4.2 当前已修复

✅ **2026-08-25 先关窗修复**：`SelectionOverlay.Finish()` 先 `Close()` 再编码 → 用户感知零延迟
✅ **HdrCaptureWindow VRR + GPU 合成**：拖拽帧 ~2ms (2026-08-16)
✅ **HdrCaptureWindow CPU 锁修复**：`InvalidateRect` + 独立渲染线程 (2026-08-10)

### 4.3 优化建议

| 序号 | 优化点 | 收益 | 复杂度 | 工作量 | 说明 |
|------|--------|------|--------|--------|------|
| **P1** | **SoftwareBitmap 改 WriteableBitmap** | 中 (~20ms) | 低 | 1h | `AnnotationWindow.RenderRawPixels()` 用 `SoftwareBitmap` + `SoftwareBitmapSource` 异步加载大图。4K 图像 `SetBitmapAsync` 耗时 ~20-40ms。改为 `WriteableBitmap` + `PixelData.AsStream().Write()` 同步填充，节省 ~20ms |
| **P2** | **SelectionOverlay ExtractRegionPixels 零拷贝** | 低 (~1ms) | 中 | 2h | 当前 `ExtractRegionPixels` 从全桌面 `Buffer.BlockCopy` 行提取区域。若 `desktopPixels` 用 `ArrayPool<byte>` 共享，区域提取可变为 `Memory<byte>.Slice` 视图。但桌面像素编码后即释放，共享生命周期复杂 |
| **P3** | **HdrCaptureWindow 首帧 UploadFrame 延迟** | 中 (~23ms) | 低 | 1h | 日志显示首帧 UploadFrame 23ms。当前 `UploadFrame` 在 `RenderCore` 首次调用时执行。可提前到 `LoadFrame` 阶段异步上传 → 窗口打开时纹理已就绪 |
| **P4** | **MainWindow StatusTxt 高频更新去重** | 低 | 低 | 0.5h | 多处 `DispatcherQueue.TryEnqueue(() => StatusTxt.Text = "...")` 可能连续触发。可合并为延时更新 (100ms 去抖) |

---

## 5. OCR 管线

### 5.1 现状性能

| 操作 | 耗时 | 说明 |
|------|------|------|
| ONNX 检测 (DirectML + FP16) | ~50-100ms | PP-OCRv6 检测模型 |
| ONNX 识别 (逐区域) | ~30-200ms | 每区域 ~10-30ms |
| 预处理 (AutoPreprocess v2) | ~10-30ms | 积分图二值化 + 背景检测 |
| Windows OCR (系统) | ~100-500ms | 输入法依赖 |
| 翻译 (LLM API) | ~500-2000ms | 网络延迟 |

### 5.2 当前瓶颈

**已优化**：OCR 预处理的 `Task.Run` 后台化 (2026-08-16)、张量池化复用 (2026-08-09)、LUT 加速 (2026-08-09)。

### 5.3 优化建议

| 序号 | 优化点 | 收益 | 复杂度 | 工作量 | 说明 |
|------|--------|------|--------|--------|------|
| **P1** | **OCR 区域并化识别** | 高 (~2-3x) | 中 | 4h | 当前 `OnnxOcrEngine` 逐区域运行 `RunRecognition`。检测框通常 ≤20 个区域，可 `Parallel.ForEach` 并行识别。但 DirectML 的 GPU 上下文未必线程安全 → 需每线程独立 `OrtSession` |
| **P2** | **OCR 预处理预计算** | 低 | 低 | 1h | 当前 `AutoPreprocess` 每次 OCR 调用都执行。若连续多次 OCR (如截图后 OCR + 翻译)，预处理结果可缓存 (基于图像哈希) |
| **P3** | **OCR 预览窗口点对点覆盖** | 低 | 低 | 1h | `OcrPreviewWindow` 文字覆盖用 `TextBlock` 逐个叠加。大段文字时 UI 树元素过多。可改为 `Canvas` + `DrawTextLayout` 直接渲染 |

---

## 6. 综合优化建议（按优先级排序）

### 🔴 立即实施 (P0-P1, 低成本高回报)

| # | 优化 | 预计收益 | 工作量 | 类型 |
|---|------|---------|--------|------|
| 1 | **AnnotationWindow SoftwareBitmap → WriteableBitmap** | 4K 窗口创建快 ~20ms | 1h | 代码 |
| 2 | **HdrCaptureWindow 首帧 UploadFrame 提前** | 首帧快 ~23ms | 1h | 代码 |
| 3 | **编码进度报告 (IProgress)** | 用户感知提升 | 1h | 代码 |
| 4 | **MainWindow StatusTxt 去抖合并** | 减少 ~50% 冗余调度 | 0.5h | 代码 |

### 🟡 中期优化 (P2, 中等回报)

| # | 优化 | 预计收益 | 工作量 | 类型 |
|---|------|---------|--------|------|
| 5 | **剪贴板复用已编码文件** | 快捷键复制快 ~500ms | 4h | 代码 |
| 6 | **AVIF libaom 后端缓存** | 每次节省 ~500ms 重试 | 1h | 代码 |
| 7 | **PNG 编码并行化** | PNG 编码快 ~2x | 2h | 代码 |
| 8 | **OCR 区域并行识别** | OCR 快 ~2-3x | 4h | 代码 |
| 9 | **OCR 预处理结果缓存** | 连续 OCR 快 ~30ms | 1h | 代码 |

### 🟢 远期优化 (P3, 高回报高投入)

| # | 优化 | 预计收益 | 工作量 | 类型 |
|---|------|---------|--------|------|
| 10 | **GPU 色调映射激活** | Float16→SDR 58ms→2ms | 16h | 架构 |
| 11 | **NVENC 纹理直通扩展** | 编码快 ~3-5x | 8h | 架构 |
| 12 | **编码器 CancellationToken 传播** | 取消响应快 | 8h | 架构 |

---

## 7. 性能基线（当前实测）

| 场景 | 操作 | 4K 当前耗时 | 目标 | 优化后预期 |
|------|------|-----------|------|----------|
| 选区截图 | 窗口创建 → 用户可见 | ~20-50ms | <30ms | 不变✅ |
| 选区截图 | 点击确定 → UI 消失 | **<1ms** | <1ms | 已修复✅ |
| 选区截图 | 确定 → 编码完成 (JPEG LI) | ~80-200ms | <200ms | 不变✅ |
| 全屏截图 | 快捷键 → 截图保存 (JPEG LI) | ~100-300ms | <200ms | 不变✅ |
| 全屏截图 | 快捷键 → 截图保存 (AVIF NVENC) | ~25-50ms | <50ms | 不变✅ |
| 全屏截图 | 快捷键 → 截图保存 (JPEG XL) | ~300-900ms | <500ms | 需优化 |
| 预览 | AnnotationWindow 打开 | ~50-100ms | <50ms | 任务1: WriteableBitmap |
| 预览 | HdrCaptureWindow 打开 | ~30-60ms | <30ms | 任务2: 提前 UploadFrame |
| OCR | 截图 + OCR 识别 (ONNX) | ~200-500ms | <200ms | 任务8: 并行识别 |
| 编码 | PNG 8-bit 4K | ~50-150ms | <50ms | 任务7: 并行化 |

---

## 8. 关键性能指标 (KPI) 监控

建议新增内置诊断工具：

| 指标 | 获取方式 | 用途 |
|------|---------|------|
| 各编码器耗时分布 | 编码前后 `Stopwatch` | 识别编码器瓶颈 |
| 捕获帧年龄 | `PooledSession.GetFrameAge()` | 检测 WGC 帧延迟 |
| GPU 锁争用率 | `Monitor.TryEnter` 失败计数 | 检 GPU 锁竞争 |
| 编码队列深度 | `_captureTask` 完成状态 | 检测并发过载 |
| OCR 置信度分布 | 略 | 检测识别质量退化 |

---

## 9. 总结

TTC 当前性能基线已处于**良好水平**。用户感知的"卡顿"主要来自：
1. ✅ **已修复**：选区确定后 `Close()` 延迟（2026-08-25 先关窗修复）
2. **编码耗时**：JPEG XL/AVIF libaom 等 CPU 编码器是物理瓶颈，无算法捷径
3. **窗口创建**：AnnotationWindow 和 HdrCaptureWindow 创建有 ~20-60ms 开销

**推荐立即执行**的 4 项低成本优化（P1, ~3.5h 总工作量）：
- AnnotationWindow 改用 `WriteableBitmap`（节省 ~20ms 窗口创建）
- HdrCaptureWindow 提前 UploadFrame（节省 ~23ms 首帧）
- 编码进度报告（消除用户不确定感）
- StatusTxt 去抖合并（减少冗余 UI 调度）

**中远期最大收益**来自：
- GPU 色调映射激活（58ms → 2ms）
- OCR 区域并行识别（加速 2-3x）
- AVIF 后端缓存（避免每次重试 NVENC）