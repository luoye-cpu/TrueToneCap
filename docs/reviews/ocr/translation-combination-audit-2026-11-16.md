# 翻译链路扩展审查与组合测试报告（2026-11-16）

> 状态：**本轮审查完成；代码/测试改动随本报告一并提交**（工作树基线 `06614c2`）。
> 范围：LLM 文本批量、视觉、离线 Argos、有道、免费公共端点，以及模式开关、失败类型、回退、取消、空行等组合。
> 验证：Release 构建 **0 警告 / 0 错误**；`--unit-tests` **2145 通过 / 0 失败 / 1 跳过**；
> `scripts/verify.ps1 -SkipPublishGates` **8 个套件全部通过，共 2805 断言**；1 处 ICC 探针因前置条件不足未判，`--service-tests` 的在线免费端点 E2E 默认跳过。
> 关联：`docs/reviews/ocr/translation-fallback-improvement-plan-2026-11-16.md`（回退策略改进，已提交）；本轮不重复该计划的全部背景，只审当前实现的组合行为。

---

## 0. 判词

1. **当前翻译链路整体成立，没有发现阻断级缺陷。** 四种入口（视觉 → 离线 → LLM 文本批量 → 逐行）在正常 UI 配置下可区分，批量失败不会直接整屏换成免费端点。
2. **本轮修正了 5 个明确问题**：Core 批量入口与 UI 的模式优先级不一致、UI 离线预检在 Vision+Offline 残留时先弹离线引导、视觉与有道 HTTP 响应对象未释放、全空白批量被误判“没有可用后端”，以及测试夹具把 OpenAI 响应形状测错。
3. **仍有三项需要产品口径决定，不在本轮擅自改行为**：
   - LLM 模式但端点为空、免费端点默认开启时，UI 会落回逐行免费端点，可能显示普通成功；
   - 单行 `TranslateTextAsync` 仍先离线后 LLM，且没有 Vision 拒绝；
   - `DescribeBackends` 在 `UseOfflineArgos` 时只描述离线，不说明其他在线后端被忽略。
4. **视觉路失败不回退文本路** 是按“用户明确选了视觉模式”保留的既有设计，本轮只验证了“行数不等即拒绝、不错位覆盖”，没有改成自动回退。

---

## 1. 审查方法与边界

### 1.1 代码阅读

| 文件 | 重点 |
|---|---|
| `src/TrueToneCap.Core/Services/TranslationService.cs` | `AnyBackendAvailable`、`DescribeBackends`、`VisionConfigured`、`LlmTextBatchConfigured`、`TranslateAsync`/`TranslateTextAsync`、`TranslateBatchAsync`、`RecoverLlmBatchChunkAsync`、`TranslateImageWithVisionAsync`、`MapVisionLines` |
| `src/TrueToneCap.App/OcrPreviewWindow.xaml.cs` | 翻译前预检、离线预检、视觉/离线/LLM 批量/逐行分派、快照与状态栏文案 |
| `src/TrueToneCap.Core/Services/LlmJsonContext.cs` | `LlmBatchLine`、源生成注册、批量 JSON 形状 |
| `src/TrueToneCap.Core/Services/OfflineArgosEngine.cs` | 离线就绪检查、同语言早退、批量语义 |
| `src/TrueToneCap.Test/CorePipelineTests.cs` | 新增组合矩阵、模式顺序、伪造 LLM 端点行为测试及源码判据 |

### 1.2 三种判据并用

1. **纯函数/配置矩阵**：64 组配置逐组核对 `AnyBackendAvailable`、`VisionConfigured`、`LlmTextBatchConfigured`、`DescribeBackends`，防止“单个谓词都对、组合起来说谎”。
2. **真 HTTP 行为夹具**：本地 `FakeLlmServer`（`TcpListener`，不碰外网）模拟 OpenAI 兼容端点，实际跑批量成功、解析少行、429、401、持续失败、取消。
3. **源码接线判据**：钉住“视觉先于离线”“批量入口与 UI 同序”“视觉/有道响应对象 using 释放”“取消先于通用 catch”等无法只靠行为计时稳定验证的接线。

### 1.3 本轮不覆盖

- 真实 LLM 服务商、真实有道凭据、真实免费公共端点的在线 E2E；`--service-tests` 的翻译 E2E 需要 `TTC_LIVE_TRANSLATE=1`，默认不向第三方发文本。
- 完整 GUI 人工点击矩阵；UI 分派与状态栏通过源码判据 + 预览窗既有测试覆盖。
- 发布门禁：本轮沿用 `-SkipPublishGates`，不冒充通过。

---

## 2. 当前实际链路

### 2.1 UI 分派（`OcrPreviewWindow.TranslateAndShowAsync`）

```text
AnyBackendAvailable == false
  → 直接提示“缺什么/哪几条路可走”，不发任何请求

UseOfflineArgos && !VisionConfigured
  → 离线就绪检查；缺语言包/运行环境时弹引导
     - Abort：结束
     - ProceedOnline：UseOfflineArgos=false，继续在线分派
     - ProceedOffline：保持离线，下方离线分支接住

VisionConfigured
  → TranslateImageWithVisionAsync（整图 PNG 直传）

else UseOfflineArgos
  → OfflineArgosEngine.TranslateBatchAsync（整批一个本地进程）

else LlmTextBatchConfigured
  → TranslationService.TranslateBatchAsync（LLM 结构化批量）

else
  → 逐行 TranslateAsync，并发 4，行级 try/catch，失败回填原文
```

### 2.2 Core 单行 `TranslateTextAsync`

```text
空白 → 原样返回
AnyBackendAvailable == false → TranslationNotConfiguredException
UseOfflineArgos → 只走离线；失败直接抛，不回在线
allowLlm && UseCustomLlm && 端点非空 → LLM
→ 有道官方 API
→ Edge → 必应 → MyMemory → Google
→ TranslationException（带 DescribeBackends + 最后一次后端原因）
```

### 2.3 Core 批量 `TranslateBatchAsync`

```text
空列表 → Mode=空
全空白 → 原样返回，FailedCount=0，Mode=空（不要求后端）
AnyBackendAvailable == false → TranslationNotConfiguredException
VisionConfigured → 明确拒绝（视觉不能走文本批量接口）
UseOfflineArgos → 离线批量，整批返回
LlmTextBatchConfigured
  → 按 8 行/3000 字符分片
  → 每片一次 LLM 批量
  → 失败分类后同 LLM 恢复：拆分 / 同片重试 / 逐行
  → 仍缺行且配置了免费或有道 → 跨后端兜底
  → 仍失败回填原文
```

### 2.4 关键谓词

```text
AnyBackendAvailable =
  (UseCustomLlm && 端点非空)
  || (有道 AppKey + Secret 齐全)
  || AllowUnofficialTranslateEndpoints
  || UseOfflineArgos

VisionConfigured =
  UseVision && UseCustomLlm && 端点非空

LlmTextBatchConfigured =
  UseCustomLlm && 端点非空 && !UseOfflineArgos && !UseVision
```

**优先级断言**：UI 与 Core 批量入口都必须是 **视觉 > 离线 > LLM 文本批量 > 逐行**；离线预检也必须先让视觉。

---

## 3. 配置组合矩阵（64 组）

维度：`UseCustomLlm`（2）× 端点空/非空（2）× `UseVision`（2）× `UseOfflineArgos`（2）× `AllowUnofficialTranslateEndpoints`（2）× 有道凭据（2）= **64 组**。

| 组合（代表性状态） | `AnyBackendAvailable` | `VisionConfigured` | `LlmTextBatchConfigured` | UI 实际分派 | `DescribeBackends` |
|---|---:|---:|---:|---|---|
| LLM + 端点 + Vision | true | **true** | false | 视觉 | 有在线后端说明；若同时 `UseOfflineArgos`，当前早退为离线说明（见 §6.3） |
| Vision 标记但端点为空 | 取决于免费/有道/离线 | false | false | 逐行（若免费/有道可用） | 只列真正可用的后端，不提“视觉配置不完整” |
| 离线（无 Vision） | true | false | false | 离线批量 | 只描述离线缺包/缺运行环境，不列在线后端 |
| Vision + 离线 + 端点 | true | true | false | 视觉 | 当前仍可能返回离线说明（文案与分派不同序，见 §6.3） |
| LLM + 端点，无 Vision/离线 | true | false | **true** | LLM 文本批量 | 列出 LLM（+ 有道/免费，若启用） |
| LLM 标记但端点为空，免费开 | true（免费） | false | false | 逐行 → 免费端点 | 只列免费/有道，不提示“LLM 配置不完整”（见 §6.1） |
| 免费单独开 | true | false | false | 逐行 → 免费端点 | 列出免费公共端点 |
| 有道单独配置 | true | false | false | 逐行 → 有道 | 列出有道官方 API |
| 全关、无凭据 | false | false | false | 不开始，直接提示三条出路 | “还没有任何可用” |

### 3.1 不变量（测试逐组核对）

- `LlmTextBatchConfigured` 在 Vision/Offline 为真时必须为 false；端点为空必须为 false。
- `VisionConfigured` 必须同时要求 `UseVision`、`UseCustomLlm`、端点非空；缺一不可。
- `AnyBackendAvailable` 不得把“只有 AppKey 没有 Secret”算成有道可用。
- `DescribeBackends` 在离线模式不得把免费/LLM 列成“已启用”；无后端时不得甩锅网络，必须给出三条出路。
- 免费端点默认开启，是“可用”的默认来源；但它不应改变 `LlmTextBatchConfigured` 的判定。

### 3.2 分片边界（同组测试）

| 输入 | 期望 |
|---|---|
| `["a", "", "b"]` | 1 片，下标 `0,2`；空行不占编号、不把后面的行漏掉 |
| 9 行 | 8 + 1 两片 |
| 单行 3001 字符 + 第二行 | 两片，超长行不被截断、下一行另起一片 |
| 全空白 / 空列表 | `Mode=空`、`FailedCount=0`、不要求后端 |

---

## 4. 失败类型与回退矩阵

### 4.1 批量失败分类

| 分类 | 触发 | 恢复动作 | 是否停止后续 LLM |
|---|---|---|---|
| `Parse` | 行数不等、空行、多行、解释、重复 id | 拆小一次；1 行则交给逐行 | 否 |
| `Protocol` | 非法 JSON、缺 `choices`、400/422 | 拆小一次；1 行则交给逐行 | 否 |
| `Timeout` | 超过 60s 且用户未取消 | 拆小一次 | 否 |
| `RateLimited` | HTTP 429 | 按 `Retry-After`（无则 2s）退避，同片重试一次；再失败停止 LLM | 是（第二次失败后） |
| `Auth` | 401/403 | 不重试；显式报错后跨后端 | 是 |
| `Transient` | 5xx、网络异常 | 1s 退避，同片重试一次；再失败逐行 | 否 |
| `Unknown` | 其他异常 | 同 `Transient` | 否 |
| 用户取消 | `ct.IsCancellationRequested` | 直接抛；不重试、不回退 | 是（终止） |

### 4.2 真实 HTTP 夹具结果

| 场景 | 请求序列/结果 | 断言 |
|---|---|---|
| 批量成功 + 空白行 | 1 次批量；`译1 / 空 / 译2` | `Failed=0, Degraded=0, FirstError=null, Mode=LLM 批量, req=1` |
| 4 行批量只回 1 行 | 初始 1 次 → 拆 2+2 两次；全部救回 | `LlmRetryCount=4, Failed=0, Degraded=0, Mode=同后端重试, req=3` |
| 持续解析失败 | 初始 + 拆 2+2 + 逐行 4 次；4 行全部救回，不跨后端 | `Fallback=4, LlmRetryCount=4, Degraded=0, Mode=同后端逐行, req=7` |
| 401 | 只发 1 次；不拆片、不逐行 | `Failed=2, Degraded=0, LlmRetryCount=0, req=1, FirstError 含 401` |
| 429 + `Retry-After: 1` | 第 1 次 429 → 退避 → 同片第 2 次成功 | `Failed=0, LlmRetryCount=2, req=2, FirstError 仍含 429` |
| 预取消 | 一个请求都不发 | `OperationCanceledException`，`req=0` |

### 4.3 跨后端与显式错误

- 跨后端只在“同 LLM 批量/逐行都失败、仍缺行、且 `HasNonLlmBackend()` 为 true”时触发；并发 4，`allowLlm:false`，逐行调用完整免费/有道链。
- 跨后端成功计入 `DegradedCount`，但不覆盖 `FirstError`；UI 状态栏在 `DegradedCount>0` 或 `FirstError!=null` 时必须是警告态，不能只打普通 ✅。
- 视觉路失败不回退文本路；行数不等于 `expectedLineCount` 即抛 `TranslationException`，不做按顺序硬套。
- 离线失败不回退在线；这是“文字不出本机”的隐私承诺，缺包/缺运行环境原文抛给 UI。

### 4.4 UI 状态栏分支（源码判据）

```text
failed==0 && FirstError==null && DegradedCount==0
  → ✅ 翻译完成
failed==0 && (FirstError!=null || DegradedCount>0)
  → ⚠ 翻译完成，但曾发生错误或降级；显示回退/重试/降级计数和原因示例
failed==total
  → ❌ 翻译失败：全行都没出译文
0<failed<total
  → ⚠ 翻译完成（部分行失败，已保留原文）
```

---

## 5. 本轮修正的问题

### 5.1 Core 批量入口与 UI 模式优先级不一致

**问题**：`TranslateBatchAsync` 原先先判断 `UseOfflineArgos`、后判断 `VisionConfigured`，而 UI 是“视觉优先”。旧配置同时残留 `UseVision`/`UseOfflineArgos` 时，UI 走视觉，批量入口却走离线；离线缺包时用户会看到“离线翻译包未就绪”，但实际 UI 并没有走离线。

**修正**：把 `VisionConfigured` 检查移到 `UseOfflineArgos` 之前，并加注释锁定两处必须同序。

**判据**：`Test_TranslationModeOrder` 同时检查 UI 与 Core 的源码顺序；行为上对 Vision+Offline 同时为真调用批量入口，必须抛“视觉模式”错误，不能偷偷走离线。

### 5.2 UI 离线预检先于视觉

**问题**：`if (_translationConfig.UseOfflineArgos)` 会在 Vision/Offline 同时残留时先弹离线安装引导，实际下方分派却走视觉。

**修正**：`if (_translationConfig.UseOfflineArgos && !TranslationService.VisionConfigured(_translationConfig))`。

**判据**：`Test_TranslationModeOrder` 的 UI 源码断言 + 既有预览窗翻译测试。

### 5.3 视觉 / 有道 HTTP 响应对象未释放

**问题**：视觉路 `var response = await s_visionHttp.SendAsync(...)`、有道路 `var response = await _http.SendAsync(...)` 未 `using`，每次请求泄漏一个 `HttpResponseMessage`。

**修正**：两处都改为 `using var response`。

**判据**：`Test_VisionTranslationIsWired` 新增“视觉 HTTP 响应对象必须用 using 释放”；`Test_LlmBatchAndRateLimitWiring` 新增“有道 HTTP 响应对象必须用 using 释放”。

### 5.4 全空白批量被误判“没有可用后端”

**问题**：`TranslateBatchAsync` 在 `lines.Count == 0` 后有 `AnyBackendAvailable` 检查；全空白行没有任何可翻译内容，却会因为没配后端抛 `TranslationNotConfiguredException`。

**修正**：在空列表之后增加全空白早退：原样返回空白，`FailedCount=0`，`Mode=空`，不要求后端。

**判据**：`Test_TranslationModeOrder` 的“全空白批量直接原样返回”“空列表批量返回空结果”。

### 5.5 测试夹具把 OpenAI 响应形状测错

**问题**：`FakeLlmServer` 最初直接返回裸 JSON 数组，生产代码按 OpenAI `chat/completions` 外壳读 `choices[0].message.content`，导致所有批量测试在 `GetProperty("choices")` 处失败；随后统计批量行数时又直接正则扫外层 body，看不到被转义进 `content` 的 `Id`（编码器写作 `\u0022`），把 2/4 行误判成 1 行，制造“1+1 重试”的假失败。

**修正**：夹具返回 `{"choices":[{"message":{"content":"..."}}]}`；先把 body 里的 `\u0022` 和 `\"` 解码，再统计 `"Id":数字`；单行请求的 `content` 返回纯文本，批量请求返回 JSON 数组。

**结论**：这不是生产逻辑失败；修好后失败从 3 降到 0，新增行为判据才真正测到“拆片/重试/退避/取消”。

---

## 6. 仍未决 / 需要产品口径

### 6.1 LLM 模式但端点为空，免费端点默认开启

**现状**：`LlmTextBatchConfigured=false`，UI 落进逐行 `TranslateAsync`；`AnyBackendAvailable` 因免费端点默认开启而为 true；最终免费端点成功，状态栏可能显示普通 ✅，没有任何“LLM 配置不完整”的提示。

**判断**：这是当前最值得下一步处理的“静默降级”。它与“显式报错 + 自动回退”的既定口径不完全一致：自动回退可以保留，但错误不应被隐藏。

**建议**（本轮未改，避免擅自改变用户模式选择）：在 UI 分派前检测 `UseCustomLlm && 端点为空 && !UseVision && !UseOfflineArgos`，把“LLM 已启用但端点为空，将使用免费公共端点/有道”写入 `firstError` 或独立警告字段；翻译仍可自动走免费端点，但状态栏必须是警告态。

### 6.2 单行 `TranslateTextAsync` 仍先离线后 LLM，且没有 Vision 拒绝

**现状**：正常 UI 的 `TranslationMode` 互斥时不可达；但旧配置同时残留 Vision/Offline 或外部直接调用单行入口时，单行路径可能与 UI 视觉分派不一致。

**建议**：若要把一致性做到底，在单行入口加与批量入口相同的 Vision 拒绝，并把离线/LLM 顺序与 UI 对齐；需同时补一条“单行入口不能偷偷走视觉/离线”的判据。本轮只修了批量入口和 UI 预检，单行入口未改。

### 6.3 `DescribeBackends` 的离线早退

**现状**：`UseOfflineArgos` 为真时直接返回离线缺包/缺运行环境说明，不列 LLM/有道/免费。单独离线配置下这是正确的隐私口径；但 Vision+Offline 同时为真时，UI 实际走视觉，说明却仍是离线，文案与分派不同序。

**建议**：至少让 `DescribeBackends` 在 `VisionConfigured` 时优先说明视觉路，或明确写“离线标记仍在，但视觉优先，其他在线后端被忽略”。本轮只记录，未改文案。

### 6.4 视觉失败不回退文本路

**现状**：视觉路失败直接抛 `TranslationException`，由状态栏显示，让用户切模式；不自动回退文本路。

**判断**：这是既有设计，合理：用户选视觉是为了整图语义，偷偷回退文本会让人以为走的是视觉模型。本轮保留，仅验证“行数不等即拒绝、不错位覆盖”。

### 6.5 在线 E2E 未跑

`--service-tests` 的免费公共端点在线翻译默认跳过；本轮没有设置 `TTC_LIVE_TRANSLATE=1`，因此“真实第三方端点今天是否仍可用”没有结论。需要时单独跑：

```powershell
$env:TTC_LIVE_TRANSLATE = "1"
src\TrueToneCap.Test\bin\Release\net11.0-windows10.0.26100.0\TrueToneCap.Test.exe --service-tests
```

---

## 7. 本轮新增/更新的可执行判据

| 判据 | 覆盖 |
|---|---|
| `Test_TranslationCombinationMatrix` | 64 组配置的 `AnyBackendAvailable` / `VisionConfigured` / `LlmTextBatchConfigured` / `DescribeBackends`；分片空行、8+1、3001 字符 |
| `Test_TranslationModeOrder` | UI 与 Core 的视觉→离线→批量顺序；Vision+Offline 行为；全空白/空列表；离线同语言 |
| `Test_TranslationFallbackAgainstFakeLlm` | 批量成功、解析少行拆 2+2、401、429、持续失败逐行、预取消 |
| `Test_LlmBatchFailureClassification` | 401/403/429/超时/Parse/Protocol/5xx/400/无状态码网络异常 |
| `Test_VisionTranslationIsWired` | 视觉 HTTP 响应 `using` 释放 |
| `Test_LlmBatchAndRateLimitWiring` | 有道响应 `using`、限流发送前、同 LLM 优先、跨后端最后兜底、取消过滤 |
| `Test_LlmBatchAndRateLimitWiring` / 预览窗判据 | UI 快照字段、警告态不再显示普通 ✅ |

---

## 8. 验证记录

### 8.1 构建

```powershell
dotnet build TrueToneCap.slnx -c Release -v:m
```

结果：**0 警告 / 0 错误**。

### 8.2 单元测试

```powershell
src\TrueToneCap.Test\bin\Release\net11.0-windows10.0.26100.0\TrueToneCap.Test.exe --unit-tests
```

结果：**2145 通过 / 0 失败 / 1 跳过**（跳过项为“本机无 AV1 MFT 编码器”）。日志：`artifacts/unit-tests-translation-combinations-final2.log`。

### 8.3 全量校验

```powershell
$python = "C:\Users\20210\AppData\Local\Programs\Python\Python312\python.exe"
Set-Alias -Name python -Value $python -Scope Global
.\scripts\verify.ps1 -SkipPublishGates
```

结果：**8 个套件全部通过，共 2805 断言**；`icc_profile_id_check.py` 因前置条件不足跳过（1 处未判，不计为通过）；发布门禁本轮跳过。日志：`artifacts/verify-translation-combinations-final.log`。

| 套件 | 结果 |
|---|---|
| `--unit-tests` | 2145 通过 / 0 失败 / 1 跳过 |
| `--color-tests` | 291 通过 / 0 失败 |
| `--encoding-tests` | 23 通过 / 0 失败 |
| `--service-tests` | 2 通过 / 0 失败 / 1 跳过（在线翻译 E2E 需 `TTC_LIVE_TRANSLATE=1`） |
| `--ocr-tests` | 12 通过 / 0 失败 |
| `--qr-tests` | 47 通过 / 0 失败 |
| `--format-tests` | 52 通过 / 0 失败 |
| `--usability-tests` | 233 通过 / 0 失败 |

---

## 9. 结论

- **默认配置（LLM + 端点，免费端点默认开启）**：文本批量走 LLM；批量失败先同 LLM 拆分/重试/逐行，跨后端只在最后；错误保留、降级可见。
- **视觉配置**：整图直传；行数不等即拒绝；不退回文本路；响应对象已释放。
- **离线配置**：整批一个本地进程；缺包/缺环境在 UI 预检阶段引导；失败不回在线。
- **免费/有道配置**：只作为逐行路径或批量最后兜底；批量跨后端会计入 `DegradedCount` 并保留 `FirstError`。
- **空行/取消/分片边界**：全空白批量不要求后端；空行不占编号、不导致漏行；取消不发请求、不重试、不回退。
- **剩余风险**：§6.1 的“LLM 端点空 + 免费兜底静默成功”最值得下一轮修；§6.2/§6.3 属于配置残留/文案口径；§6.4 是既定设计；§6.5 需要显式开启在线 E2E 才能给真实第三方端点结论。
