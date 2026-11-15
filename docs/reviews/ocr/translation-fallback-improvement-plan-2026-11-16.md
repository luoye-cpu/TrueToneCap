# LLM 批量翻译回退策略改进计划（2026-11-16）

> 状态：**已全量实现并校验（2026-11-16）**。
> 验证：Release 构建 0 警告 0 错误；`--unit-tests` 2124 通过 / 0 失败 / 1 跳过；
> `scripts/verify.ps1 -SkipPublishGates` 8 套件共 2784 断言通过，1 处 ICC 探针未判。
> 2026-11-16 决策：**显式报错 + 自动回退并行**，不把“报错”和“重试/回退”做成二选一；回退成功也不隐藏错误。
> 基线：`00cb126`（`fix(translate): LLM 文本路改结构化批量并修正限流/超时`）。
> 关联：本轮对话的 LLM 批量回退分析；当前实现入口为
> `src/TrueToneCap.Core/Services/TranslationService.cs` 的 `TranslateBatchAsync`。
> 行号只作当前工作树定位，代码一动即漂；实施时以方法名/锚点为准。

---

## 0. 判词

1. **回退机制保留，回退目标改掉。** 批量失败后先重试同一个 LLM，跨后端（免费/有道）只作为最后兜底。
2. **报错和重试不冲突。** 每次失败先留下显式错误（阶段、失败类型、行号范围、人话原因），再按策略重试或自动回退；401/403 不重试，但必须显式报错并自动跨后端。用户取消是唯一例外：直接抛，不报成后端失败、不重试、不回退。
3. **跨后端回退必须可见。** 结果里增加“降级行数/降级后端”，状态栏不能继续显示普通“翻译完成”。
4. **先做同后端一致性，不新增用户设置。** 默认策略就是“同后端优先”；是否允许跨后端兜底的开关留到后续版本再评估。
5. **离线/视觉路径不动。** 它们已有自己的整批语义，避免一次改三条链路。

---

## 1. 要解决的问题

当前 `TranslateBatchAsync` 的行为：

```text
批量分片失败
  → 该分片所有行进入 missing
  → allowLlmFallback = !HasNonLlmBackend()
  → 默认配置下 HasNonLlmBackend == true（免费端点默认开启）
  → 失败行走 Edge / 必应 / MyMemory / Google / 有道
  → 成功的分片仍用 LLM
```

结果是：一屏里成功分片是 LLM 译文，失败分片是免费端点译文。这是“部分行翻译错误/风格不一致”的翻版，只是从“逐行失败”变成了“整片失败”。

`HasNonLlmBackend()` 当前判据：

```csharp
(!string.IsNullOrWhiteSpace(_config.YoudaoAppKey)
 && !string.IsNullOrWhiteSpace(_config.YoudaoAppSecret))
|| _config.AllowUnofficialTranslateEndpoints
```

因此默认配置下，批量失败几乎总会立刻跨后端，而不是先尝试同一个 LLM。

---

## 2. 目标与非目标

### 2.1 目标

| 编号 | 目标 | 可验收判据 |
|---|---|---|
| G1 | 批量失败后优先同 LLM | 解析/超时失败时，请求序列里先出现同后端重试，不出现免费端点 |
| G2 | 跨后端只作最后兜底 | 只有同 LLM 批量重试 + 逐行重试都失败后，才调用免费/有道 |
| G3 | 跨后端可观测 | `DegradedCount > 0` 时 UI 是警告态，并显示降级行数/原因 |
| G4 | 重试有上界 | 额外 LLM 请求数不超过“失败分片数 × 1 次拆分 + 未解决行数 × 1 次逐行” |
| G5 | 取消不降级 | 用户关窗/取消时直接抛出 `OperationCanceledException`，不进入任何回退 |
| G6 | 不破坏既有安全语义 | 行数不等、空行、重复 id 仍然整片拒收；不按顺序硬套 |

### 2.2 非目标

- 不把 Edge/必应等免费端点改成数组批量（本轮只修 LLM 回退策略）。
- 不改视觉/离线路径。
- 不新增“翻译一致性优先”用户开关；第一版固定默认策略。
- 不做自动切换模型/自动换端点池。

---

## 3. 设计

### 3.1 失败分类

新增内部枚举/分类器（建议放在 `TranslationService` 内，纯静态、可单测）：

```text
LlmBatchFailureKind
  Parse          // TranslationException：行数/空行/多行/解释/id 非法
  Protocol       // 响应不是 JSON、缺 choices、content 为空
  Timeout        // TaskCanceledException 且用户 ct 未取消
  RateLimited    // HTTP 429
  Auth           // HTTP 401/403
  Transient      // HTTP 5xx / 网络异常
  Unknown
```

关键点：

- `SendLlmChatAsync` 目前用 `EnsureSuccessStatusCode()`，必须显式保留 HTTP 状态码（自定义异常或 `HttpRequestException.StatusCode`），否则 429/401/5xx 只能靠字符串猜。
- 用户取消（`ct.IsCancellationRequested`）在分类前直接重抛；不要归入 `Timeout`。
- `Parse` 与 `Protocol` 都按“可修复的格式问题”处理，不按“后端不可用”处理。

### 3.2 重试流水线

对每个批量分片按以下顺序执行，任何阶段成功即停止：

```text
① 原分片 LLM 批量
   ├─ 成功 → 回填，结束
   ├─ Parse / Timeout / Protocol
   │    → ② 拆成更小分片（最多一次；4 行 → 2 行；1 行 → 逐行）重试
   │    → 仍失败 → ③ 逐行同 LLM
   ├─ RateLimited
   │    → 退避一次（优先 Retry-After，否则 2s 左右；受全局限流器约束）
   │    → 仍失败 → 直接进入 ④（不再逐行打 LLM，避免放大 429）
   ├─ Auth
   │    → 不重试，但写入显式错误（失败类型 + 人话原因 + 行范围）→ 自动进入 ④
   ├─ Transient / Unknown
   │    → 同分片重试一次（短退避）→ 仍失败 → ③
   └─ 用户取消 → 直接抛，结束
② 拆分重试
③ 逐行同 LLM
   并发 2（不要沿用 4）；每行最多 1 次额外尝试；只调用 LLM-only 路径
   ⚠ 不能走 TranslateTextAsync 的完整降级链，否则 LLM 一失败又偷偷落到免费端点
④ 跨后端兜底
   仅当 ③ 仍有未解决行，且 HasNonLlmBackend() 为 true
   并发 4；allowLlm:false；每行一次；成功行计入 DegradedCount
⑤ 仍失败 → 回填原文，FailCount++，记录 FirstError
```

#### 3.2.1 为什么 Parse/Timeout 要拆分

- 行数/解释性文字错误，多半是“一次给太多行”导致模型合并/漏行；拆小比换后端更可能修复，而且保持同一模型风格。
- 60 秒超时后原样重发 8 行，可能再次 60 秒；拆成 4 行/2 行能显著降低单次生成时长。

#### 3.2.2 为什么 Auth/RateLimited 不走逐行 LLM，但仍要报错

- 401/403 是凭据问题，逐行重试只会得到 N 次同样失败；所以不重试，但必须把“LLM 鉴权失败”作为显式错误写进结果与日志，再自动跨后端兜底。
- 429 是并发/频控问题，逐行放大请求会继续 429；应退避一次后直接进入跨后端或失败，同样要保留显式错误原因。

#### 3.2.3 逐行同 LLM 的实现边界

当前 `TranslateWithLlmAsync` 已经只做 LLM、失败即抛；批量路径应直接调用它（或抽一个同名包装），不要调用 `TranslateTextAsync(... allowLlm:true)`，因为后者内部 catch 后还会继续走免费端点。

建议新增：

```csharp
private async Task<string> TranslateSingleLineWithLlmOnlyAsync(
    string text, string targetLang, string? sourceLang, CancellationToken ct)
```

内部只调用 `TranslateWithLlmAsync`，不 catch（或只 catch 后重抛）。

### 3.3 结果契约

在保留现有公开构造兼容性的前提下，给 `TranslationBatchResult` 增加初始化属性：

```csharp
public sealed record TranslationBatchResult(
    string[] Lines,
    int FailedCount,
    int FallbackCount,
    string? FirstError,
    string Mode)
{
    /// 最终由不同后端（免费/有道）补译的行数；0 = 全部同 LLM 或全成功
    public int DegradedCount { get; init; }

    /// 降级后端的人话名，例如“免费公共端点”“有道官方 API”
    public string? DegradedBackend { get; init; }

    /// 同 LLM 批量/逐行重试过、但最终仍成功的行数（仅日志/UI 说明用）
    public int LlmRetryCount { get; init; }
}
```

语义必须写清：

| 字段 | 含义 |
|---|---|
| `FallbackCount` | 批量没有交出、进入逐行阶段的行数（同 LLM 或跨后端都算） |
| `DegradedCount` | `FallbackCount` 中最终由**不同后端**补译的行数 |
| `FailedCount` | 最终回填原文、没有译文的行数 |
| `FirstError` | 第一个显式失败原因；**即使跨后端回退成功也不得清空**，供 UI/日志显式报错 |
| `Mode` | `LLM 批量` / `LLM 批量 + 同后端逐行` / `LLM 批量 + 跨后端降级` / `逐行` |

`DegradedCount` 必须是 `FallbackCount` 的子集，且 `FailedCount` 与二者不重叠。

### 3.4 UI 可见性

`OcrPreviewWindow` 的 `TranslationSnapshot` 增加 `DegradedCount`、`DegradedBackend`、`LlmRetryCount`。状态栏规则：

```text
failed == 0 && degraded == 0 && FirstError == null
  → ✅ 翻译完成（N 行）[；其中 K 行走同 LLM 逐行回退]
failed == 0 && degraded == 0 && FirstError != null
  → ⚠ 翻译完成（N 行），但批量曾失败并已同 LLM 重试成功；
    显式显示 FirstError；不得只显示 ✅
failed == 0 && degraded > 0
  → ⚠ 翻译完成（N 行），其中 D 行由备用引擎补译，风格可能与其余行不同；
    显式显示“LLM 失败原因：...”和“已自动回退”，不得只显示 ✅
failed > 0 && degraded > 0
  → ⚠/❌ 失败 + 降级说明 + 显式原因
failed > 0 && degraded == 0
  → ⚠/❌ 失败 + 显式原因
failed == 0 && fallback > 0 && degraded == 0
  → 全部同 LLM；只提示同 LLM 逐行回退，不报降级
```

要求：

- `DegradedCount > 0` 时不得使用普通 ✅ 成功文案，也不能把 `FirstError` 清零或只写日志。
- 文案只含计数、后端名和错误摘要，不把模型原文/响应体写进状态栏。
- 现有“点击文字块可复制译文”保留。
- “显式报错”同时落日志（`CoreLog.Warn` 级别）和 UI；不能因为自动回退成功就把错误降级成 Debug。

### 3.5 日志

每个分片一行结构化日志（不记录译文正文）：

```text
stage=batch kind=Parse chunk=1-8 action=split rows=8
stage=batch kind=RateLimited chunk=9-16 action=fallback after=1
stage=single kind=None rows=3 backend=same-llm
stage=fallback backend=free rows=8
```

用途：

- 线上能回答“这一屏到底有没有跨后端”；
- 测试失败时能看出重试预算是否被突破；
- 不泄露用户文本。

---

## 4. 实施阶段

### 阶段 0：尺子先行（不改行为）

1. 在 `CorePipelineTests.cs` 增加纯函数判据：
   - `ClassifyLlmBatchFailure` 的 401/403/429/5xx/timeout/parse/user-cancel 分类。
   - `BuildLlmBatchChunks` 的空行边界（已有，保留）。
   - `ParseLlmBatchContent` 的既有案例（已有，保留）。
2. 增加策略判据：给定 failure kind + chunk 行数 + 是否有非 LLM 后端，断言下一个动作。
3. 这一步不改变运行时行为；先让“决策表”可测。

**门禁**：构建 0/0；`--unit-tests` 全绿。

### 阶段 1：Core 契约与分类

1. `SendLlmChatAsync` 显式保留 HTTP 状态码，新增可识别的异常/字段。
2. 增加 `LlmBatchFailureKind` 与 `ClassifyLlmBatchFailure(Exception, ct)`。
3. `TranslationBatchResult` 增加 `DegradedCount/DegradedBackend/LlmRetryCount` 初始化属性。
4. 不改现有 `TranslateBatchAsync` 的调用顺序；先让新字段有默认值，旧 UI 仍可编译。

**门禁**：构建 0/0；现有单测全绿。

### 阶段 2：重写回退编排

1. 把 `TranslateBatchAsync` 的“missing → 逐行”抽成三个私有阶段：
   - `RetryLlmBatchChunksAsync`
   - `RetryLlmPerLineAsync`
   - `FallbackOtherBackendsAsync`
2. 删除 `allowLlmFallback = !HasNonLlmBackend()` 的旧决策；跨后端只在第三阶段出现。
3. 拆分重试：`Parse/Timeout/Protocol` 最多拆一层；1 行时直接逐行。
4. 429：退避一次后直接跨后端，并显式记录错误；401/403：不重试，但显式报错（UI/日志）后自动跨后端，不得吞成仅日志。
5. 逐行同 LLM 并发 2，每行最多一次额外尝试；不再调用完整 `TranslateTextAsync`。
6. 跨后端 `allowLlm:false`，并发 4；成功后累加 `DegradedCount` 与后端名；`FirstError` 保留，供 UI 显式报错。
7. 用户取消在每个 await 点重抛；取消不写入后端失败原因、不触发回退。

### 阶段 3：UI 接线

1. `TranslationSnapshot` 增加降级字段。
2. 预览窗读取 `batch.DegradedCount` / `batch.DegradedBackend`。
3. 状态栏按 §3.4 分支；`DegradedCount > 0` 用警告态。
4. 源码判据更新：不得再出现“批量失败 → 立即免费端点”的旧接线。

### 阶段 4：验证

| 门 | 命令 | 通过判据 |
|---|---|---|
| 构建 | `dotnet build TrueToneCap.slnx -c Release -v:m` | 0 警告 0 错误 |
| 单测 | `TrueToneCap.Test.exe --unit-tests` | `failed=0` |
| 全量 | `scripts/verify.ps1` | 8 套件全绿；发布门禁若被本地运行实例阻塞需明确记录 |
| 回归 | `Test_OcrPreviewTranslation_CancellationAndSnapshot` | 取消不触发回退，快照字段完整 |
| 回归 | `Test_LlmBatchAndRateLimitWiring` | 发送前限流、批量优先、降级字段接线 |
| 回归 | `Test_ReviewRoundFixes_HaveTeeth` | 单行校验调用点仍被钉住 |

---

## 5. 验收矩阵

| 场景 | 期望请求序列 | 期望结果 |
|---|---|---|
| 全部分片成功 | 每片 1 次 LLM 批量 | `Fallback=0, Degraded=0, Failed=0` |
| 某片行数错 | 原片 1 次 → 拆分 2 次 → 仍坏行逐行 LLM | 优先同 LLM；不出现免费端点，除非逐行也失败 |
| 某片 60s 超时 | 原片 1 次 → 拆分重试 → 逐行 LLM | 同 LLM 优先；超时预算有界 |
| 429 一次 | 原片 1 次 → 退避 1 次 → 跨后端 | `Degraded>0`，状态栏警告 + 显式错误原因 |
| 401/403 | 原片 1 次 → 不重试 → 跨后端 | `Degraded>0`，状态栏 + Warn 日志显式写“鉴权失败” |
| 回退成功但错误存在 | 批量失败 → 同 LLM/跨后端成功 | `Failed=0` 仍必须显示 `FirstError` 与降级说明，不得只显示 ✅ |
| 网络断开 | 原片 1 次 → 同片重试 1 次 → 逐行 LLM → 跨后端 | 每阶段有界 |
| 只配 LLM、无免费 | 批量失败 → 拆分/逐行 LLM → 原文 | `Degraded=0`，`Failed>0` |
| 免费关闭、有道未配 | 同上 | 不得调用任何免费端点 |
| 成功分片 + 失败分片 | 成功片保持 LLM；失败片先同 LLM | 不出现“整屏换后端” |
| 用户关窗 | 立即取消 | 不产生回退请求，不发布快照 |
| 空白行 | 不发送 | 不影响分片/计数 |

---

## 6. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 重试增加延迟/费用 | 拆分只做一层；逐行并发 2；每行最多一次额外尝试 |
| 429 被逐行放大 | 429 不进入逐行 LLM，直接跨后端 |
| 401 被静默跨后端 | 分类为 Auth：不重试，但 UI + Warn 日志显式报错，同时自动跨后端并标记降级 |
| 解析过严导致更多失败 | 保持“行数/ID 安全优先”，但先拆小重试；不放宽按顺序硬套 |
| 公开 record 改动破坏调用方 | 用 `init` 属性增量扩展，不改主构造参数 |
| UI 文案判据变红 | 同步更新 `Test_LlmBatchAndRateLimitWiring` 与状态栏相关源码锁 |
| 逐行 LLM 仍偷偷降级 | 新路径只调用 `TranslateWithLlmAsync`，禁止调用完整 `TranslateTextAsync` |
| 只改文档没改行为 | 每阶段都有构建/单测门禁；最终 `verify` 必须跑 |

---

## 7. 回退与不做

- **回退方式**：本改进是一个可独立回滚的提交集合；不涉及数据迁移、配置格式或用户设置，回滚只需 revert 对应提交。
- **明确不做**：
  - 不把免费端点改成批量；
  - 不把视觉/离线路径纳入统一回退；
  - 不在第一版增加“允许跨后端”的用户开关；
  - 不为“提高可用性”而牺牲同后端一致性默认值。

---

## 8. 完成定义

1. `DegradedCount` 在默认免费端点开启、LLM 批量解析失败时不再恒为 0；
2. 批量失败后的第一动作是同 LLM 重试，而不是免费端点；
3. 跨后端发生时状态栏是警告态且计数/原因可见；
4. 自动回退成功也不清空 `FirstError`：UI 与 Warn 日志都保留显式错误；
5. 401/403 不重试，但既显式报错又自动回退；
6. 用户取消不触发任何回退；
7. Release 构建 0 警告 0 错误；
8. `--unit-tests` 与 `scripts/verify.ps1` 通过（发布门禁若被运行实例阻塞，单独记录，不冒充通过）；
9. 计划中的验收矩阵至少覆盖“解析失败 / 429 / 401 / 超时 / 只配 LLM / 取消”六类。
