# TrueToneCap OCR 并行识别深度分析报告 (2026-08-25)

> 覆盖 GPU (DirectML) + CPU 双后端的并行化方案设计

---

## 1. 现状架构分析

### 1.1 识别流程时序

```
RecognizeAsync(bgra, w, h)
  └─ Task.Run
       ├─ RunDetection(bgra)                    ← 检测: 单次推理 (~50-100ms GPU / ~200-400ms CPU)
       │    └─ _detSession.Run(inputs)
       │         └─ ExtractBoxes → List<Box>    ← N 个文字区域 (典型 5~30 个)
       │
       └─ foreach (box in boxes)                ← ⚠ 串行瓶颈!
            ├─ ct.ThrowIfCancellationRequested()
            ├─ RunRecognition(bgra, w, h, box)  ← 每区域 ~10-30ms (GPU) / ~30-80ms (CPU)
            │    ├─ crop + resize to recH×recW
            │    ├─ input.Buffer.Span.Clear()   ← ⚠ 共享 _recTensor 污染风险
            │    ├─ 预处理填充张量
            │    └─ _recSession.Run(inputs)
            └─ lines.Add(...)
```

### 1.2 关键约束发现

| # | 约束 | 影响 |
|---|------|------|
| **C1** | `_recTensor` 是**共享池化张量**（`_recTensor ??= new DenseTensor<float>(...)`），每次 `RunRecognition` 先 `Clear()` 再填充 → **并行写入会互相污染** | 必须每并行 worker 独立张量，或改为按区域分配 |
| **C2** | `InferenceSession.Run()` 本身**线程安全**（ONNX Runtime 官方保证：同一 Session 可并发 Run）| ✅ 可以多线程调用同一个 `_recSession` |
| **C3** | **DirectML EP 的线程安全**：DML EP 内部有命令队列序列化。并发 `Run()` 在 DML 上会排队执行 — **GPU 并发无收益，反而增加调度开销** | GPU 后端不适合"多线程并行 Run"，适合"批量 batch 推理" |
| **C4** | **CPU EP 的 IntraOpNumThreads = cores-1**：单次 `Run()` 已经吃满所有核心。多线程并发 Run 会争抢线程池 → **CPU 多会话并行收益有限甚至负收益** | CPU 后端应减少并发度或降低 IntraOpNumThreads |
| **C5** | 池化张量 `_detTensor` 同样共享，但检测只跑一次，无并行需求 | 只需解决识别阶段 |
| **C6** | `RunRecognition` 中预处理（crop/resize/归一化）约占每区域耗时的 **40-60%**，纯 CPU 内存操作 | 预处理可以完全并行化（与推理解耦），这是最大的安全并行点 |

### 1.3 耗时分布估算 (4K 截图, 20 个文字区域)

| 阶段 | GPU (DirectML) | CPU EP |
|------|---------------|--------|
| 检测 (1 次) | ~80ms | ~300ms |
| 预处理 × 20 区域 | ~60ms | ~60ms |
| 识别推理 × 20 区域 (串行) | ~250ms | ~900ms |
| CTC 解码 + 字典映射 | ~5ms | ~5ms |
| **总计** | **~395ms** | **~1265ms** |

---

## 2. 方案设计

### 方案 A：GPU 批量识别（Batch Inference）— 推荐 ⭐⭐⭐⭐⭐

**原理**：把 N 个区域的预处理结果堆叠成 `[N, 3, recH, recMaxW]` 一个批次，一次 `Run()` 推理。

```
现状:  N 次 Run([1,3,48,640])  → N 次 kernel 启动开销
批量化: 1 次 Run([N,3,48,640]) → 1 次 kernel 启动, GPU 并行计算 N 个样本
```

**优势**：
- ✅ 完美契合 GPU 特性（DML 对大 batch 吞吐远高于多次小 batch）
- ✅ 无线程安全问题（仍是单线程单 Run）
- ✅ PP-OCRv6 rec 模型原生支持动态 batch（PaddleOCR 导出默认 dynamic_axes batch）
- ✅ 预期加速 **3-8x**（20 区域场景）

**实现要点**：
1. 预处理阶段仍可 `Parallel.For` 各区域独立填充 batch 张量（不同 batch 维度互不干扰）
2. 按 `recW` 分桶（同一宽度 bucket 合并 padding，避免过度 pad 浪费算力）— 可选优化
3. 输出 tensor 形状 `[N, T, C]`，逐样本 CTC 解码（解码可 `Parallel.For`）
4. 大 batch 分块（如每 16 个一批）避免显存峰值

**风险**：
- ⚠ 需验证导出的 ONNX rec 模型是否固定 batch=1（检查输入维度）。若固定需重新导出模型或用 `dynamic_batch` 
- 显存占用：batch 16 × FP16 ≈ 增加几十 MB，可接受

### 方案 B：CPU 多会话并行（Multi-Session）— 仅 CPU 后端推荐 ⭐⭐⭐

**原理**：创建 K 个独立的 `InferenceSession`（各自独立内存 arena），每个 worker 绑定一个会话，`Parallel.ForEach` 处理区域队列。

**关键配置调整**（否则负收益）：
```csharp
// 主会话: IntraOpNumThreads = cores-1 (吃满核心)
// 并行 worker: 每个 session IntraOpNumThreads = max(1, (cores-1)/K)
opts.IntraOpNumThreads = Math.Max(1, (cpuCores - 1) / parallelism);
opts.InterOpNumThreads = 1; // 保持顺序模式
```

**参数建议**：
- K（并行度）= `Math.Clamp(cores / 4, 2, 4)`：4 核→2、8 核→2~3、16 核+→4
- 每个会话独立 arena：`EnableCpuMemArena = true`（默认）
- 区域分配策略：按面积降序排序后轮流分发（长任务先启动，减少尾延迟）

**优势**：
- ✅ 预处理与推理天然流水线化
- ✅ 8 核以上机器预期 **1.5-2.5x**

**风险**：
- ⚠ K 个会话 = K 份模型内存副本？**否** — ORT 同一模型多 Session 共享只读权重页（OS 页缓存级别），实测内存增量 ~10-20MB/session
- ⚠ 小图少区域（<4 个框）时并行开销大于收益 → 设置阈值：boxes.Count >= 4 才启用并行

### 方案 C：GPU+CPU 混合异构（Hybrid Heterogeneous）— 进阶 ⭐⭐

**原理**：检测用 GPU，识别分两路 — 前 K 个大区域走 GPU（批量），剩余小区域走 CPU 会话并行。

**适用场景**：GPU 弱（集显）+ CPU 强的混合设备；或 GPU 正被其他任务占用时的降级路径。

**复杂度高**，仅在 A/B 方案落地后再考虑。

### 方案 D：预处理全并行（无论后端都适用）— 低垂果实 ⭐⭐⭐⭐

**原理**：将 `RunRecognition` 拆成「预处理」和「推理」两段：
1. **并行预处理**：`Parallel.For` 各区域 → 生成各自的 `float[3*recH*recMaxW]` 缓冲
2. **串行推理**：依次喂给 session（GPU 天然排队 / CPU 单会话已满载）

**优势**：
- ✅ 零线程安全问题（不碰共享张量 — 每 worker 自己的缓冲区）
- ✅ GPU/CPU 后端通用，无 DML 序列化损耗
- ✅ 预处理占 ~50% 时间 → 直接省一半串行时间

---

## 3. 推荐落地路线图

```
Phase 1 (立即可做, ~3h):  方案 D 预处理并行 + boxes>=4 阈值门控
                          ↓ 收益: GPU ~1.4x, CPU ~1.3x, 零风险
Phase 2 (中期, ~6h):      方案 A GPU 批量识别 (含 batch 维度验证 + 分桶)
                          ↓ 收益: GPU 场景 ~3-8x
Phase 3 (可选, ~4h):      方案 B CPU 多会话并行 (仅 CPU 引擎启用)
                          ↓ 收益: 多核 CPU ~1.5-2.5x
Phase 4 (远期):           方案 C 异构混合
```

## 4. 具体代码改动清单

### Phase 1: OnnxOcrEngine.cs

```csharp
// RecognizeAsync 内改造:
var boxes = RunDetection(bgra, w, h);

// ═══ P2 优化: 并行预处理 + 阈值门控 ═══
var preprocessed = new RecInput[boxes.Count];
if (boxes.Count >= 4)
{
    Parallel.For(0, boxes.Count, i =>
    {
        preprocessed[i] = PreprocessRecognition(bgra, w, h, boxes[i]); // 纯函数, 无共享状态
    });
}
else
{
    for (int i = 0; i < boxes.Count; i++)
        preprocessed[i] = PreprocessRecognition(bgra, w, h, boxes[i]);
}

foreach (var (box, prep) in boxes.Zip(preprocessed))
{
    string text = InferAndDecode(prep);  // 只做 Run + CTC 解码
    ...
}
```

需要重构的点：
- `RunRecognition` 拆为 `PreprocessRecognition`（返回张量数据 + 元信息）和 `InferAndDecode`
- 废弃共享 `_recTensor`（改为每次分配 `float[3*recH*recMaxW]`，约 368KB/区域，可接受；或用 `ArrayPool<float>.Shared`）
- CTC 解码也可并入并行段（输出 tensor 读取是只读的）

### Phase 2: 批量识别核心

```csharp
private const int BatchSize = 16;

// 分块批量
for (int chunkStart = 0; chunkStart < preprocessed.Length; chunkStart += BatchSize)
{
    int n = Math.Min(BatchSize, preprocessed.Length - chunkStart);
    var batchTensor = new DenseTensor<float>([n, 3, recH, recMaxW]);
    
    // 并行填充 batch (各 batch 槽位独立)
    Parallel.For(0, n, j =>
    {
        CopyToBatchSlot(batchTensor, j, preprocessed[chunkStart + j]);
    });

    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", batchTensor) };
    using var results = _recSession!.Run(inputs);  // 一次推理 N 个样本
    var output = results.First().AsTensor<float>();  // [n, T, C]

    // 并行 CTC 解码
    var texts = new string[n];
    Parallel.For(0, n, j => { texts[j] = CtcDecode(output, j); });
}
```

⚠ 前置验证任务：加载 rec 模型后检查 `_recSession.InputMetadata["x"].Dimensions` 是否含 `-1`/动态 batch。若固定为 1，需要：
- 用 `scripts/onnx_fp16_quantize.py` 流程重新导出（`torch.onnx.export(..., dynamic_axes={"x": {0: "batch"}})`）
- 或在报告中标注该限制并回退到方案 D

### Phase 3: CPU 多会话

```csharp
public sealed class CpuRecPool : IDisposable
{
    private readonly InferenceSession[] _sessions;
    public CpuRecPool(string modelPath, int k, int totalCores)
    {
        _sessions = new InferenceSession[k];
        for (int i = 0; i < k; i++)
        {
            var opts = new SessionOptions();
            opts.IntraOpNumThreads = Math.Max(1, (totalCores - 1) / k);
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL; // 保持一致
            _sessions[i] = new InferenceSession(modelPath, opts);
        }
    }
}
```

---

## 5. 决策矩阵总结

| 维度 | 方案A GPU批量 | 方案B CPU多会话 | 方案D 预处理并行 |
|------|--------------|----------------|----------------|
| GPU 收益 | ★★★★★ 3-8x | 不适用 | ★★★ 1.4x |
| CPU 收益 | 需验证 batch 支持 | ★★★ 1.5-2.5x | ★★★ 1.3x |
| 线程安全风险 | 低 | 中（需调线程数） | **零** |
| 实现复杂度 | 中 | 中高 | **低** |
| 前置条件 | 验证模型动态 batch | 无 | 无 |
| 建议优先级 | Phase 2 | Phase 3 | **Phase 1 先做** |

## 6. 验证方式

- 用 Tools 项目现有 `--ocr-bench`（OcrAccBench）跑准确率回归 — 确认并行化不影响结果
- 计时对比脚本：同一测试图在改造前后各跑 10 次取中位数
- 显存监控（方案A）：Task Manager GPU 专用内存增量应 <100MB