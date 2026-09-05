# OCR 管线性能与可靠性综合分析 (2026-08-25 深夜)

> 覆盖：引擎初始化 → 预处理 → 检测 → 框提取 → 识别 → 解码 → 引擎调度/fallback → UI 集成 → 翻译
> 前提：今日已完成并行预处理+批量推理、韩文 fallback、ArrayPool/DenseTensor 修复。本报告聚焦**尚未实施**的优化点。

---

## 1. 全链路时序（现状）

```
用户触发 OCR
  └─ MainWindow.CaptureAndOcrFromPixelsAsync (UI 线程发起)
       ├─ Task.Run(Initialize) ← ⚠A 每次都 Task.Run 包裹 (已短路, 但仍有调度开销)
       ├─ MultiOcrService.RecognizeAsync
       │    └─ OnnxOcrEngine.RecognizeAsync → Task.Run
       │         ├─ RunDetection (det 推理 ~80-300ms)
       │         │    ├─ 双线性缩放 Parallel.For ← 已优化
       │         │    ├─ session.Run ← 单次
       │         │    └─ ExtractBoxes ← ⚠B BFS 连通域 + O(n²) IoU 去重
       │         ├─ Parallel.For 预处理 ← 已优化
       │         ├─ 批量/串行推理 ← 已优化
       │         └─ CTC 解码 ← 已验证无需过滤
       │    └─ [空结果] → Windows OCR fallback ← 今日新增
       └─ OpenOcrPreviewWindow
            └─ [翻译] Task.WhenAll(逐行 TranslateAsync) ← ⚠C 无并发限制
```

---

## 2. 性能优化点

### P-1: ExtractBoxes BFS 在大图上是隐性热点 🔴

**现状**：`ExtractBoxes` 对 det 输出图（如 640×2304 = 147 万像素）逐像素扫描 + BFS 连通域。
- `visited` 数组每次调用分配 `w*h` bool
- BFS 用 `Queue<(int,int)>`（元组装箱到堆）— 大连通域（长行文字可达数万像素）入队/出队开销大
- `new[] { (cx+1,cy), ... }` 每像素分配一个 4 元素数组 → GC 压力巨大（147 万次 × 4 元组）

**估算**：4K 截图 det 后处理占整条链路 **15-25%** 时间（~20-50ms），且产生大量 Gen0/Gen1 垃圾。

**优化方案**：
```csharp
// 1. 栈替代队列 + Span 邻居 (零分配 BFS)
Span<(int, int)> stack = stackalloc (int, int)[4096]; // 溢出则降级 Queue
// 或迭代式 flood fill 用位运算标记

// 2. visited 用 bit array 替代 bool[]
// 3. 邻居遍历硬编码 4 个 if 而非数组分配:
if (cx + 1 < w && ...) { TryVisit(cx + 1, cy); }
if (cx > 0 && ...) { TryVisit(cx - 1, cy); }
...
```
**收益**：后处理耗时 -60~70%（~15-35ms），GC 分配 -90%。工作量 ~2h。

### P-2: IoU 去重 O(n²) — 低优先 🟢

框数通常 <50，O(n²) 实际开销可忽略。仅当框数 >200 时才值得排序后滑动窗口去重。**暂不动**。

### P-3: det 输出的二值化+probMap 双循环可合并 🟡

当前两个循环分别写 probMap 和 bitmap，可合并为单循环减少一次全图像素遍历：
```csharp
for (int i = 0; i < oh*ow; i++) {
    float v = output[...];  // 注意索引计算合并
    probMap[i] = v;
    bitmap[i] = v > threshold ? (byte)255 : (byte)0;
}
```
收益小（~2-5ms）。工作量 0.5h。

### P-4: 翻译并发无限制 — 可能触发限流 🟡

`Task.WhenAll(_ocr.Lines.Select(l => translator.TranslateAsync(...)))`：
- LLM 免费接口（有道/Google free）对并发有限制；20 行同时请求可能被限流/封 IP
- DeepSeek 等 API 有 RPM 限制

**方案**：`SemaphoreSlim(4)` 限制并发为 4，或分批 WhenAll：
```csharp
using var sem = new SemaphoreSlim(4);
var tasks = lines.Select(async l => { await sem.WaitAsync(); try { return await Translate(l); } finally { sem.Release(); } });
```
**收益**：翻译成功率提升（尤其免费通道）；单次延迟略增。工作量 1h。

### P-5: Windows OCR 的 SoftwareBitmap 创建开销 🟡

`RunOcrAsync` 每次创建 `SoftwareBitmap` + `CopyFromBuffer`（全量拷贝）。Windows OCR fallback 场景下：
- Pass1（原图）+ Pass2（AutoPreprocess 后）各创建一次 → 2 次全量拷贝
- 若 AutoPreprocess 为 None（高对比度图），Pass2 可跳过 SoftwareBitmap 创建

**现状检查**：Pass2 已有 `Mode != None` 门控 ✅ 已合理。但 Pass1 失败后仍会跑 Pass2 — 合理（兜底）。**暂不动**。

### P-6: 初始化路径的重复 Task.Run 🟢

MainWindow 两处 `await Task.Run(() => MultiOcrService.Initialize(modelDir))` — Initialize 有 `_initialized` 短路，但每次 OCR 仍付一次 Task.Run 调度（~0.1ms）。微不足道，但可简化为直接同步调用（短路后无阻塞风险）。**可选**。

### P-7: rec 批量推理的 batch 张量分配 🟡

`RunBatchRecognition` 每批次 `new DenseTensor<float>([n,3,48,640])`（~368KB×n）。连续 OCR 时可按最大 batch 缓存复用。收益 ~1-2ms/次。低优先。

---

## 3. 可靠性优化点

### R-1: ExtractBoxes 的 BFS 上限保护有 bug 🟡

```csharp
while (queue.Count > 0 && queue.Count < w * h)
```
条件 `queue.Count < w*h` 意图防死循环，但**语义错误**：队列长度 ≠ 已访问像素数。大连通域（长行文字）队列峰值可能远超 w*h？不会 — 但当 queue.Count 达到 w*h 时会提前终止 BFS → 剩余像素未访问 → 框被截断。实际上 visited 数组已保证不重复入队，BFS 天然终止，此条件冗余且有害。**应删除** `&& queue.Count < w * h`。

### R-2: Windows OCR fallback 未传递语言参数语义差异 🟡

`MultiOcrService` fallback 调 `winOcr.RecognizeAsync(bgra, w, h, lang, ct)` — lang 传的是 ONNX 的语言标签（如 "ch"），而 Windows OCR 期望 BCP-47（"zh-Hans-CN"）。`OcrService.RunOcrAsync` 内部对 null 走 zh-Hans-CN 默认 — 但 "ch" 这种标签 `TryCreateFromLanguage` 会失败返回 null → "无法创建 OCR 引擎" 错误。

**修复**：fallback 时做语言映射或传 null：
```csharp
string? winLang = lang switch {
    "ch" or "zh" => "zh-Hans-CN",
    "en" => "en-US",
    "korean" or "ko" => "ko-KR",
    _ => null  // 用户配置语言由 Windows OCR 默认逻辑处理
};
```
**这是今天新增 fallback 路径的实际缺陷**，需修复。工作量 0.5h。

### R-3: RecognizeAsync 的 ct 取消在 fallback 后未重查 🟢

fallback 成功路径正常。取消令牌在 Task.Run 内部由框架传播 — 已足够。

### R-4: MultiOcrService._engines 非线程安全的读取 🟢

Initialize 只跑一次（`_initialized` 守卫），后续只读 — 实际安全。但 `_engines` 是 `List<T>`，理论上有发布竞态（Initialize 在 Task.Run 中完成，主线程可能看到未完全发布的列表）。.NET 内存模型下静态字段写入有 release 语义 — 实际安全。**不动**。

### R-5: OcrPreviewWindow 翻译失败时行级错误不可见 🟢

`TranslateAndShowAsync` 整体 catch — 单行失败会导致全部失败（WhenAll 快速失败）。改用 `Task.WhenAll` 的容错版本（逐行捕获，失败的行显示原文）更健壮。与 P-4 一并做。

### R-6: det 会话的输入形状变化时张量重建抖动 🟢

`_detTensor` 尺寸随输入图变 → 不同尺寸截图交替时会反复重建张量（每次 ~几 MB 分配）。截图场景尺寸通常固定（同显示器），影响小。**不动**。

---

## 4. 推荐实施清单

| # | 类型 | 项目 | 收益 | 工作量 | 优先 |
|---|------|------|------|--------|------|
| 1 | 可靠性 | **R-2: fallback 语言标签映射** | 修复韩文 fallback 可能失效的实际缺陷 | 0.5h | 🔴 立即 |
| 2 | 可靠性 | **R-1: 删除 BFS 错误终止条件** | 消除长行框截断风险 | 0.1h | 🔴 立即 |
| 3 | 性能 | **P-1: ExtractBoxes 零分配化** | 后处理 -60% (~15-35ms), GC -90% | 2h | 🟡 |
| 4 | 可靠性 | **P-4/R-5: 翻译并发限制+行级容错** | 免费通道成功率↑, 单行失败不拖垮整体 | 1h | 🟡 |
| 5 | 性能 | P-3: 二值化双循环合并 | ~2-5ms | 0.5h | 🟢 |
| 6 | 性能 | P-6/P-7 微优化 | <2ms | 0.5h | 🟢 |

**总工作量（前 4 项）：约 3.5-4h**

---

## 5. 不建议动的部分

| 组件 | 理由 |
|------|------|
| det/rec 推理本身 | ONNX Session 已是最优配置（DML EP + FP16）|
| 并行预处理/批量推理 | 今日刚做完并经基准验证 |
| ArrayPool 缓冲策略 | 刚修复 DenseTensor 兼容问题，稳定压倒一切 |
| CTC 解码过滤 | 今日实测证伪为负优化（logit 差值不稳定） |
| det 放大倍数/clamp | 今日实测证伪（75%→59%）|
| Windows OCR Pass1/Pass2 结构 | 已有合理的 Mode 门控 |
