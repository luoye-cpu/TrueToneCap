# TrueToneCap 全面管线审查报告 (2026-08-25 当日改动)

> 审查范围：今日全部 22 个源文件改动（涂鸦重写、OCR 并行识别、覆盖层、性能优化、依赖升级、资源管理修复）
> 验证基线：构建 0 警告 0 错误 | 全量测试通过 | 应用冒烟正常

---

## 1. 改动清单（按功能域）

| 功能域 | 文件 | 改动内容 |
|--------|------|---------|
| 涂鸦重写 | AnnotationWindow.xaml(.cs) | 坐标零换算、颜色/粗细、增量预览 |
| 选区性能 | SelectionOverlay.xaml.cs | 先关窗后编码 |
| OCR 并行 | OnnxOcrEngine.cs | 预处理并行 + GPU 批量推理 + ArrayPool |
| OCR 预处理 | BitmapPreprocessor.cs | 背景反转 + 积分图二值化 |
| OCR 覆盖层 | OcrPreviewWindow.xaml(.cs) | 字体覆盖 + 左上角开关 |
| 系统字体 | FontLoader.cs, MainWindow | EnumFontFamiliesEx 枚举 |
| 快捷键 | AppSettingsData.cs, MainWindow, SelectionOverlay, AnnotationWindow | 保存/取消自定义 |
| UI 动画 | AnimationHelper.cs (新), MainWindow, AppSettingsData | 页面过渡可开关 |
| PNG 后缀 | CapturePipelineService.cs, ImageEncoder.cs, LocaleManager | JXL+AVIF |
| 性能优化 | HdrCaptureWindow.cs, MainWindow, ManagedPngEncoder.cs, FormatEncoders.cs | 首帧/去抖/进度/并行/NVENC 缓存 |
| 资源修复 | MainWindow.xaml.cs, OnnxOcrEngine.cs | CTS Dispose ×4 / SilentCapture await / ArrayPool 泄漏 / Dispose 链 |
| 依赖升级 | 4 个 csproj | WinAppSDK 2.4.0 / System.Drawing 10 |

---

## 2. 审查发现的问题

### 🔴 问题 A：OCR 批量推理的 `CtcDecode` 维度假设风险（低概率，已有兜底）

`CtcDecode(output, sampleIdx)` 用三维索引 `output[sampleIdx, t, c]`，并取 `Dimensions[^1]` 作为类别数。

**场景**：若 PP-OCR rec 模型实际输出为 **2D `[T,C]`**（batch=1 时部分导出会压掉 batch 维），批量推理时 `[n,T,C]` 三维索引正确，但**串行路径**（batch=1）输出可能是 `[T,C]` 二维 → `output[0, t, c]` 会越界。

**缓解现状**：
- 串行路径传 `sampleIdx=0`，若输出是 `[1,T,C]` 则正确
- 批量路径 catch 已兜底回退逐区域 — 但逐区域也走同一 CtcDecode → 若维度不匹配会再次失败

**建议修复**：CtcDecode 开头加维度自适应：
```csharp
// 输出可能为 [T,C](2D)/[n,T,C](3D) — batch=1 导出有时压掉 batch 维
int t0 = output.Dimensions.Length == 3 ? sampleIdx : 0;
```

### 🟡 问题 B：SilentCapture 新增 `await _captureTask` 的 UI 阻塞窗口

`SilentCapture` 由全局热键在 UI 线程触发。新增的 `await _captureTask` 若旧任务编码耗时 2s（libaom AVIF），热键处理协程虽是异步等待（不卡 UI 渲染），但 `_isCapturing` 锁被持有期间用户按 Esc 无法取消旧编码（取消只对新任务生效）。

**评估**：可接受 — 与 `OnCaptureNow` 行为一致，且防文件覆盖的收益 > 理论上的取消延迟。无需修改。

### 🟡 问题 C：OCR 预处理 v2 的 mode 标记语义漂移

`AutoPreprocess` 中深色背景反转后将 mode 设为 `EnhanceContrast`，但该路径实际执行的是 `Invert`。下游 `OcrService` 用 `Mode != None` 判断是否需要坐标归一化，mode 具体值仅用于日志 — 功能无影响。

**建议**：未来给 PreprocessMode 加 `Inverted = 5` 枚举值以精确记录。低优先。

### ✅ 已验证正确的关键点

1. **ArrayPool 双重归还防护**：内层 finally 归还后置 `preps = null`，外层 finally 判空跳过 — 正确防止了 ArrayPool 二次归还污染其他租借方的严重问题
2. **use-after-return 检查**：`ReturnPrepBuffers` 在推理返回后才调用；`session.Run()` 同步完成消费 — 无悬垂引用
3. **CTS Dispose ×4 路径**：EncodeAndSaveAsync/SilentCapture/HdrAsync/OnCaptureNow 全部 `Cancel → await 旧任务 → Dispose → new` 序列正确
4. **涂鸦坐标零换算**：Canvas 尺寸=图像像素，Pointer 位置即像素坐标；Row 2 画布/Row 3 状态栏布局正确
5. **WriteableBitmap**：WinUI3 `PixelBuffer.AsStream()` + `Invalidate()` API 使用正确
6. **PNG 并行化**：阶段1行独立转换并行、阶段2滤波串行（Up/Average/Paeth 依赖上一行），prevRow 直接引用 rgbaRows[y] 无别名问题
7. **NVENC 失败缓存**：volatile 双标志 + SetSharedD3DDevice 重置；自建设备 finally 归还逻辑正确
8. **SelectionOverlay 先关窗**：`_finished` 先置位防 OnClosed 兜底 Cancel；async void 关窗后 await 安全
9. **依赖升级**：WinAppSDK 2.4 干净重建无 XAML 编译错误；ORT DirectML 保持 1.24.4（1.29 不存在 DML 变体，已加注释防误升）

---

## 3. 修复项

### 修复 A：CtcDecode 维度自适应（立即实施）

```csharp
private string CtcDecode(Tensor<float> output, int sampleIdx)
{
    // batch=1 导出有时压掉 batch 维 ([T,C]) — 自适应起始索引
    int b = output.Dimensions.Length >= 3 ? sampleIdx : 0;
    int timeSteps = output.Dimensions[^2];
    int numClasses = output.Dimensions[^1];
    ...
    float v = output.Dimensions.Length >= 3 ? output[b, t, c] : output[t, c];
}
```

### （可选，暂缓）问题 C 的枚举扩展

---

## 4. 结论

今日改动整体质量良好：22 个文件的功能改动均构建/测试/冒烟三关通过，资源管理修复（CTS/ArrayPool/Dispose链）逻辑严密。唯一需要立即修复的是 **CtcDecode 维度自适应**（防御性修复，防止特定模型导出形态下 OCR 失效）。