# TrueToneCap 软件整体性能可用优化分析 (2026-08-25 综合报告)

> 覆盖启动 → 捕获 → 选区 → 标注 → OCR → 编码 → 输出 → 退出 全生命周期。在今日已有的 P1/P2 优化基础上，识别新发现的资源管理、竞态、UX 反馈等可改进点。

---

## 1. 今日已有优化基线 ✓

| 领域 | 项目 | 状态 |
|------|------|------|
| 选区延迟 | SelectionOverlay 先关窗后编码 | ✅ 已修复 |
| 标注预览 | AnnotationWindow WriteableBitmap 同步填充 | ✅ 已优化 |
| HDR 预览 | HdrCaptureWindow 首帧限速跳过 + 纹理预创建 | ✅ 已优化 |
| 状态反馈 | StatusTxt 去抖 50ms 合并 + 慢编码 500ms 提示 | ✅ 已优化 |
| 编码 | PNG 行并行化 + NVENC 失败缓存 | ✅ 已优化 |
| OCR | 并行预处理 + 批量 GPU 推理 | ✅ 已优化 |

---

## 2. 新发现的问题（按优先级）

### P1: CancellationTokenSource 泄漏（中等影响）

**现象**：`SilentCapture` 路径（L1143）与 `OnCaptureNow` 路径（L1998）每次截图 `_captureCts = new CancellationTokenSource()` 之前 **未 Dispose 旧的 CTS**。

对比 `EncodeAndSaveAsync` 和 `StartSelectionCapture` 路径都有 `await _captureTask` 确保旧任务完成后才创建新 CTS，但 SilentCapture 和 OnCaptureNow **不 await 就直接赋值** → 旧 CTS 成为孤儿对象。

**影响**：用户高频连续截图（如自动化脚本触发 10+ 次静默截图）时，旧 CTS 的 internal Timer + linked callbacks 不及时回收 → 频繁 GC 堆积。单次截图内存增量极小（CTS <1KB），但高速场景下 CTS 和关联 delegate 列表可间接延长 Gen1/Gen2 GC。

**修复**：赋值新 CTS 前 Dispose 旧的：
```csharp
_captureCts?.Cancel();
_captureCts?.Dispose();  // ← 加这一行
_captureCts = new CancellationTokenSource();
```

---

### P2: OCR 并行预处理异常未归还 ArrayPool 缓冲（低中影响）

**现象**：`RecognizeAsync` 的 `Parallel.For` 内若某区域 `PreprocessRecognition` 抛异常（如 `bgra` 越界），`ReturnPrepBuffers` 只在 `texts[] finally` 中调用 —— 但数组中该位置为 `null`，其他已填充的位置仍然归还，逻辑正确。

**但**：若 Parallel.For 整体抛 `OperationCanceledException`（`ct.ThrowIfCancellationRequested()` 在并行体内），执行直接跳到外层 `catch` → `ReturnPrepBuffers(preps)` 被跳过 → 所有已分配的 ArrayPool 缓冲泄漏（每区域 ~368KB）。

**当前 catch 逻辑**：
```csharp
catch (Exception ex)
{
    return new OcrResult { Error = $"ONNX 异常: {ex.Message}" };  // ← 不经过 finally
}
```

但 `preps` 变量在 try 内部声明 → finally 无法访问。需要将 `preps` 提升到 try 外部。

**修复**：
```csharp
// try 外部声明
RecInput?[]? preps = null;
try
{
    var prepsLocal = new RecInput?[boxes.Count];
    Parallel.For(... prepsLocal ...);
    preps = prepsLocal;
    // 推理...
}
catch (Exception ex) { return new OcrResult { Error = ... }; }
finally { if (preps is not null) ReturnPrepBuffers(preps); }
```

---

### P3: _recSession?.Dispose() 无异常保护（低影响）

**现象**：`OnnxOcrEngine.Dispose()` 中：
```csharp
_detSession?.Dispose();
_recSession?.Dispose();
```
未 try/catch。若 det Dispose 抛异常 → rec 不释放 → 模型显存泄漏（DirectML 场景）。

**修复**：各加 try/catch。

---

### P4: HdrCaptureWindow Dispose 遗漏 _pooledStaging

**现象**：`Cleanup()` 中释放了 `_desktopSrv`/`_desktopTex` 等，但搜索后发现 `_pooledStaging` 在 Cleanup 中可能遗漏（虽然 `Dispose` 方法释放了大量字段）。

**当前情况**：检查发现 Cleanup 已释放，但新加的预创建纹理在 `Initialize` 失败路径（即 `return false` 之前的 `catch`）可能未释放已创建的纹理。

**修复**：在 Initialize 异常 catch 中释放已创建的 `_desktopTex`/`_pooledStaging`。

---

### P5: 无感截图 (SilentCapture) 未等待旧 _captureTask

**现象**（L1143）：
```csharp
_captureCts?.Cancel();
_captureCts = new CancellationTokenSource();  // ← 没有await _captureTask
var ct = _captureCts.Token;
```

对比 `OnCaptureNow`（L1993-1998）有 `await _captureTask`。

**影响**：连续两次无感截图间隔极短时，旧任务可能还在编码 → 两个编码任务并发 → 文件名时间戳重复 → 输出文件覆盖。

**修复**：在 `SilentCapture` 中也 `await _captureTask`。

---

### P6: Toast 通知位置追焦（UX 改进）

**当前**：Toast 固定在屏幕角落。多显示器场景下截图后用户视线在当前显示器上，Toast 可能出现在另一个显示器的角落。

**改进**：读取截图来源显示器的工作区矩形 → Toast 显示在截图所在显示器。

---

### P7: 无 OCR 结果时的"未检测到文字"提示太弱

**当前**：OCR 返回空时 `StatusTxt.Text = "📝 未检测到文字"`。用户可能错过这个小字提示。

**改进**：增加 ContentDialog 弹窗或更显眼的反馈，特别是用户手动触发 OCR 时。

---

## 3. 已验证良好的架构（无需改动）

| 组件 | 评估 |
|------|------|
| WGC 三缓冲池化 | ✅ 帧年龄 <16ms, 空闲 15s 自动释放 |
| 设置序列化 | ✅ JSON + 异步保存 (SaveQuiet) |
| 编码器崩溃隔离 | ✅ NativeEncoderGuard.TryEncode + 自动回退链 |
| 电源管理 | ✅ PowerManager.PreventSleep/AllowSleep |
| 主题/语言/字体 | ✅ 异步加载, 无阻塞 |
| 托盘/热键/看门狗 | ✅ 硬看门狗 + 超时兜底 |
| AnnotationManager 线程安全 | ✅ _lock + GetLayersSnapshot 快照 |

---

## 4. 总结

**核心性能已优化到位**：用户感知的"卡顿"主要来源（选区确定延迟、窗口打开慢、编码无反馈）在今日已全部修复。

**新发现的 6 个问题中**：
- **P1（CTS 泄漏）和 P5（SilentCapture 未等待旧任务）** 应立即修复 — 高频自动化场景下影响明显
- **P2（ArrayPool 泄漏）** 应修复 — 取消路径下每次泄漏 ~368KB/区域
- 其余为低风险/UX 改进
