# 翻译链路第二轮扩展审查报告（2026-11-16）

> 状态：**本轮审查完成；代码/测试改动随本报告一并提交**。
> 基线：`ea61928`（上一轮组合矩阵与伪造端点回退测试）。
> 范围：单行链路的取消/配置归一、批量结果的回退语义、离线语言码与空批量、视觉 0 行边界，以及有道/Google 响应解析和免费端点错误留痕。
> 验证：Release 构建 **0 警告 / 0 错误**；`--unit-tests` **2178 通过 / 0 失败 / 1 跳过**；`scripts/verify.ps1 -SkipBuild -SkipPublishGates` **8 个套件全部通过，共 2840 断言**；`icc_profile_id_check.py` 因前置条件不足跳过，`--service-tests` 的在线免费端点 E2E 已默认执行并实测通过（Edge 返回中文译文）。
> 关联：上一轮组合矩阵结论见 `docs/reviews/ocr/translation-combination-audit-2026-11-16.md`；回退策略背景见 `docs/reviews/ocr/translation-fallback-improvement-plan-2026-11-16.md`。

---

## 0. 判词

1. **本轮没有发现阻断级缺陷，但确认了 6 类会在特定配置下产生“看起来正常、实际不诚实”的问题**：用户取消被吞、单行配置归一不一致、单行入口不拒绝视觉、空白 API Key 仍写 Bearer、401 批量结果谎称逐行回退、有道/Google 解析对畸形响应不够稳。
2. **这些问题的共同点不是“主路径跑不通”，而是“边缘路径把错误说成成功”或“用户已经取消，程序还在发请求”。** 因此本轮修的是诚实性和取消传播，不改变正常路径的后端优先级。
3. **批量回退语义已改成“真的进入过回退阶段才计数”。** Auth 阻断且没有跨后端时，`FallbackCount=0`、`Mode=LLM 批量`；UI 文案也从“已按同 LLM 优先策略回退”改为“已进入回退阶段”，不再替程序承诺它没做的事。
4. **单行入口与批量入口现在同一口径**：视觉模式明确拒绝、空白端点不算 LLM 可用、空白 Key 不写 `Bearer`。正常 UI 的视觉分派不受影响。
5. **视觉 0 行、离线空列表等边界现在在发请求/查语言包之前拒绝或早退**，避免“先做一次没有意义的昂贵操作，再报一个本可以提前报的错误”。
6. **2026-11-16 三项产品口径已落地**：空白 LLM 端点自动回退时必须显式警告；视觉+离线同时配置时文案列出两条路径并写清优先级；在线免费端点 E2E 不再需要单独开启，默认随 `--service-tests` 执行（仅不能出网的环境用 `TTC_SKIP_LIVE_TRANSLATE=1` 显式关闭）。

---

## 1. 审查方法与边界

### 1.1 代码阅读

| 文件 | 重点 |
|---|---|
| `src/TrueToneCap.Core/Services/TranslationService.cs` | `WaitIfRateLimitedAsync`、`TranslateTextAsync`、`TranslateBatchAsync`、`TryYoudaoAsync`/`ParseYoudaoResponse`、`TryGoogleMultiEndpointAsync`/`ParseGoogleResponse`、`SendLlmChatAsync`、`TranslateImageWithVisionAsync` |
| `src/TrueToneCap.App/OcrPreviewWindow.xaml.cs` | 批量结果快照到状态栏文案的映射 |
| `src/TrueToneCap.Test/CorePipelineTests.cs` | 取消传播、解析器、离线语言归一、空白端点/Key、视觉 0 行、401 结果语义 |

### 1.2 判据类型

1. **真实行为**：预取消 token 调限流器/有道/Google、视觉 0 行、离线空列表、`FakeLlmServer` 的 401 批量。
2. **纯函数/解析器**：有道 8 种响应形态、Google 4 种响应形态、`NormalizeLang` 9 组映射。
3. **源码接线判据**：单行 LLM 条件必须用 `IsNullOrWhiteSpace`、文本/视觉两处空白 Key 不写 Bearer——这类“少一个字符就静默降级”的问题，行为测试很难稳定覆盖。

### 1.3 本轮不覆盖

- 真实 LLM 服务商、真实有道凭据的在线 E2E；免费公共端点的 E2E 已默认执行并通过，但真实 LLM/有道凭据仍需单独配置后才能验。
- 完整 GUI 点击矩阵；UI 分派仍以源码判据 + 预览窗既有测试为准。
- 发布门禁：本轮沿用 `-SkipPublishGates`，不冒充通过。

---

## 2. 当前实际链路（本轮修改后）

### 2.1 Core 单行 `TranslateTextAsync`

```text
空白 → 原样返回
AnyBackendAvailable == false → TranslationNotConfiguredException
VisionConfigured → 明确拒绝（视觉是整图直传，不是逐行文本）
UseOfflineArgos → 只走离线；失败直接抛，不回在线
allowLlm && UseCustomLlm && !IsNullOrWhiteSpace(ApiEndpoint) → LLM
→ 有道官方 API
→ Edge → 必应 → MyMemory → Google
→ TranslationException（带 DescribeBackends + 最后一次后端原因）
```

### 2.2 Core 批量 `TranslateBatchAsync`

```text
空列表 / 全空白 → Mode=空，不要求后端
AnyBackendAvailable == false → TranslationNotConfiguredException
VisionConfigured → 明确拒绝
UseOfflineArgos → 离线批量，整批返回
LlmTextBatchConfigured
  → 8 行 / 3000 字符分片
  → 每片一次 LLM 批量
  → 失败分类后同 LLM 恢复：拆分 / 同片重试 / 逐行
  → 仍缺行且配置了免费或有道 → 跨后端兜底
  → 仍失败回填原文
```

### 2.3 关键谓词

```text
AnyBackendAvailable =
  (UseCustomLlm && !IsNullOrWhiteSpace(ApiEndpoint))
  || (有道 AppKey + Secret 齐全)
  || AllowUnofficialTranslateEndpoints
  || UseOfflineArgos

VisionConfigured =
  UseVision && UseCustomLlm && !IsNullOrWhiteSpace(ApiEndpoint)

LlmTextBatchConfigured =
  UseCustomLlm && !IsNullOrWhiteSpace(ApiEndpoint) && !UseOfflineArgos && !UseVision
```

**不变量**：单行入口和批量入口都不能在 `VisionConfigured` 时偷偷走文本模型；空白端点不得被免费端点“救活”后仍去构造 LLM URL。

---

## 3. 本轮确认并修正的问题

### 3.1 用户取消在单行链路上被吞掉

**问题**

- `WaitIfRateLimitedAsync` 在限流窗口未满时直接取号返回，完全不检查 `CancellationToken`；预取消的 token 仍会发出请求。
- 有道 `TryYoudaoAsync` 用 `catch (TaskCanceledException)` 把用户取消当成“请求超时”，记一条原因后继续降级。
- Google 最后兜底 `TryGoogleMultiEndpointAsync` 用 `catch (Exception)` 把取消一并吞掉。

**修正**

- 限流器取号前、取号窗口内各检查一次取消；窗口内再检查是为了避免“刚检查完就被取消”后仍占一个请求名额。
- 有道在 `TaskCanceledException` 之前增加 `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`。
- Google 增加同样的用户取消重抛；超时、HTTP 失败、解析异常分别留 `_lastBackendNote`，不再一条空 catch 混过去。

**判据**：`Test_TranslationCancellationAndParsers` 的限流器、有道、Google 三段预取消测试。

### 3.2 单行入口配置归一不一致

**问题**：`TranslateTextAsync` 的 LLM 条件原为 `!string.IsNullOrEmpty(_config.ApiEndpoint)`，而 `AnyBackendAvailable`/`VisionConfigured`/`LlmTextBatchConfigured` 都用 `IsNullOrWhiteSpace`。空白端点会被免费端点开关“救活”，单行仍去构造 `"   /chat/completions"`，失败后静默落到免费端点。

**修正**：改为 `IsNullOrWhiteSpace`，与三个谓词同口径。

**判据**：`Test_TranslationModeOrder` 的源码断言 + `Test_TranslationBackendAvailabilityIsHonest` 的“空白端点不算可用”“空白端点不启用 LLM 文本批量”。

### 3.3 单行入口未拒绝视觉模式

**问题**：批量入口会明确拒绝 `VisionConfigured`，单行入口原会直接走文本 LLM。正常 UI 不会把视觉模式送进单行，但旧配置残留或外部直接调用时，行为不一致。

**修正**：在 `AnyBackendAvailable` 之后、离线路之前增加同口径 `TranslationException`，说明视觉是整图直传路径，必须由 `TranslateImageWithVisionAsync` 接。

**判据**：`Test_TranslationModeOrder` 新增“单行入口也必须拒绝视觉模式”。

### 3.4 空白 API Key 仍写 `Bearer` 头

**问题**：文本路与视觉路都用 `!string.IsNullOrEmpty(_config.ApiKey)`。`ApiKey="   "` 时会发出 `Authorization: Bearer    `，端点返回 401，用户却以为密钥正确。

**修正**：两处都改 `IsNullOrWhiteSpace`。

**判据**：`Test_LlmBatchAndRateLimitWiring` 的源码断言，要求两处同口径。

### 3.5 批量 401/Auth 结果语义不实

**问题**：`Auth` 导致 `llmBlocked=true` 时不会走同 LLM 逐行，但 `FallbackCount` 仍记全部缺行、`Mode` 仍写“LLM 批量 + 同后端逐行”。UI 看到 `FallbackCount>0` 就会声称“已按同 LLM 优先策略回退”，实际一次逐行都没试。

**修正**

- 引入 `perLineAttempted`：只有真正进入“缺行且 `llmBatch && !llmBlocked`”的逐行阶段才置 true。
- 引入 `crossBackendAttempted`：只有 `stillMissing.Length > 0 && HasNonLlmBackend()` 才置 true。
- `FallbackCount = llmBatch ? (perLineAttempted || crossBackendAttempted ? missing.Length : 0) : missing.Length`。
- `Mode` 的同后端逐行分支改判 `perLineAttempted`，不再拿 `missing.Length` 推断。
- 401 且无跨后端时：`FallbackCount=0`、`Mode="LLM 批量"`、`FailedCount=全行`、`FirstError` 保留 401。
- UI 回退文案改为“已进入回退阶段”，不再承诺“已按同 LLM 优先策略回退”。

**判据**：`Test_TranslationFallbackAgainstFakeLlm` 的 401 用例增加 `FallbackCount==0 && Mode=="LLM 批量"`；原有“只请求一次、不拆片、不逐行重试”断言保留。

### 3.6 有道/Google 响应解析不够健壮

**有道**：`ParseYoudaoResponse` 改为 `internal` 便于直接测试；非对象根、缺少 `errorCode`、`translation` 内非字符串项/空数组/非法 JSON 均返回 `null` 并写 `_lastBackendNote`，不再抛 `InvalidOperationException` 变成“有道异常”。

**Google**：`ParseGoogleResponse` 改为 `internal`；两种真实形状（gtx 嵌套数组、`sentences` 对象数组）都只接受字符串项；非字符串、畸形 JSON、非对象根返回 `null`。

**判据**：`Test_TranslationCancellationAndParsers` 的有道 8 条、Google 4 条解析断言。

### 3.7 视觉 0 行边界

**问题**：`TranslateImageWithVisionAsync` 在 `expectedLineCount <= 0` 时原先仍会发整图请求，再在行数校验处失败。

**修正**：在 `VisionConfigured` 检查后提前抛 `TranslationException`，说明“没有可回填的目标行”。

**判据**：`Test_VisionTranslationIsWired` 新增 0 行视觉拒绝。

---

## 4. 批量结果语义矩阵（修正后）

| 场景 | `FailedCount` | `FallbackCount` | `Mode` | 说明 |
|---|---:|---:|---|---|
| 批量成功 | 0 | 0 | `LLM 批量` | 正常路径 |
| 批量少行 → 拆片救回 | 0 | 0 | `LLM 批量 + 同后端重试` | 未进入逐行阶段，`FallbackCount` 不虚增 |
| 批量持续解析失败 → 同 LLM 逐行救回 | 0 | 缺行数 | `LLM 批量 + 同后端逐行` | 真进入过逐行 |
| 同 LLM 逐行仍缺 → 跨后端成功 | 0 | 缺行数 | `LLM 批量 + 跨后端降级` | `DegradedCount>0`，`FirstError` 保留 |
| 401/Auth，无跨后端 | 全行 | **0** | **`LLM 批量`** | 不谎称逐行；UI 警告态由 `FirstError` 触发 |
| 401/Auth，有跨后端 | 跨后端失败行 | 缺行数 | `LLM 批量 + 跨后端降级` | 真的进入跨后端才计 |
| 非 LLM 批量（逐行模式） | 最终失败行 | 缺行数 | `逐行` | 沿用原口径 |

**不变量**：`FallbackCount` 不能再作为“缺行数”的同义词；它表示“批量没交出，并且真的进入过回退阶段”。`Mode` 也不能仅凭 `missing.Length>0` 推断“同后端逐行”。

---

## 5. 离线 / 视觉 / 有道 / 免费端点分支判据

### 5.1 离线

- `NormalizeLang`：`null`/空白 → `null`；`en-US`/`ja-JP`/`pt-BR` → 前缀；`zh-Hans-CN` → `zh`；`zh-en`/`en-zh` → `en`；`zh-TW`/`zh-Hant-HK` → `zt`；未知码 → `null`。
- 离线空列表批量：原样返回，不检查语言包/运行时。
- 仍未补：`CheckReady` 的 ModelMissing/RuntimeMissing 语义、桥脚本行数不符/退出码/超时的独立行为判据。

### 5.2 视觉

- `VisionConfigured` 必须同时要求 `UseVision`、`UseCustomLlm`、端点非空白。
- `expectedLineCount<=0` 在发请求前拒绝。
- 视觉失败仍不自动回退文本路；这是“用户明确选视觉”的既有设计。
- 视觉 HTTP 响应对象已 `using` 释放（上一轮已覆盖，本轮未回退）。

### 5.3 有道

- 只有 `errorCode=0` 且 `translation` 里至少一个非空字符串才返回译文。
- 非字符串项跳过；全是非字符串/空数组返回 `null` 并留原因。
- 缺少 `errorCode`、非对象根、非法 JSON 都返回 `null`，不抛给外层变成“有道异常”。
- 用户取消优先于 `TaskCanceledException`，不把取消记成超时。

### 5.4 Google / 免费端点

- gtx 形状只接受 `[0]` 为字符串；sentences 形状只接受 `trans` 为字符串。
- 非字符串、畸形 JSON、非对象根返回 `null`。
- 每个端点失败都写 `_lastBackendNote`（超时/HTTP/解析），不再空 catch 后继续下一条。
- 用户取消原样上抛，不继续遍历端点。
- 免费端点开关默认开启；空白 LLM 端点不会被算作可用 LLM，但免费/有道仍可作为显式兜底。这个“静默降级”现在由 `LlmEndpointMissingWarning` + UI 警告态兜住（见 §7.1）。

---

## 6. 本轮新增/更新的可执行判据

**新增 33 条断言，另强化 1 条既有断言**：

| 判据 | 覆盖 |
|---|---|
| `Test_TranslationModeOrder` | 单行入口拒绝视觉；单行 LLM 条件使用 `IsNullOrWhiteSpace`；UI 把空白 LLM 端点警告写入 `firstError` 并落 Warn |
| `Test_TranslationFallbackAgainstFakeLlm` | 401：`FallbackCount==0`、`Mode=="LLM 批量"`（强化原有 401 断言） |
| `Test_TranslationCancellationAndParsers` | 限流器预取消、有道路径预取消、Google 私有方法预取消、有道 8 种解析、Google 4 种解析 |
| `Test_OfflineLanguageNormalization` | `NormalizeLang` 9 组映射、离线空列表早退 |
| `Test_TranslationBackendAvailabilityIsHonest` | 空白端点不算可用、不启用 LLM 文本批量；空白端点+免费/有道兜底的显式警告；离线优先不误报 |
| `Test_LlmBatchAndRateLimitWiring` | 文本/视觉两处空白 API Key 不写 Bearer |
| `Test_VisionTranslationIsWired` | `expectedLineCount=0` 在发请求前拒绝 |
| `Test_TranslationLive_FreeEndpointsEndToEnd` | 默认执行在线免费端点 E2E，真实拿到中文译文并归因到具体后端 |

---

## 7. 产品口径落地与仍未决

### 7.1 空白 LLM 端点 + 免费/有道兜底：显式警告（已落地）

**原问题**：`LlmTextBatchConfigured=false`，UI 落进逐行 `TranslateAsync`；`AnyBackendAvailable` 因免费端点默认开启而为 true；最终免费端点成功，状态栏可能普通 ✅，没有任何“LLM 配置不完整”提示。

**落地**：新增 `TranslationService.LlmEndpointMissingWarning(LlmConfig)`：

- 仅当 `UseCustomLlm && 端点空白 && !UseOfflineArgos` 时返回警告；
- 按实际兜底路径点名“免费公共端点（Edge / 必应 / MyMemory / Google）”和/或有道官方 API；
- 无任何非 LLM 兜底时返回 `null`，由 `AnyBackendAvailable` 的“没有可用后端”路径处理；
- 离线优先时不误报——用户不会看到“去改一条本次不执行的路径”。

UI 在分派前调用该方法并 `LogService.Warn`；翻译成功且没有真实错误时，把它写进 `firstError`，状态栏走 `⚠ 翻译完成...` 警告分支；如果本次已有真实错误，保留真实错误，不被配置提醒盖住。

**判据**：`Test_TranslationBackendAvailabilityIsHonest` 覆盖免费兜底、有道兜底、离线优先、端点非空/LLM 未启用；`Test_TranslationModeOrder` 钉住 UI 的 `configWarning → firstError` 接线。

### 7.2 视觉+离线：文案全部显示（已落地）

**原问题**：`DescribeBackends` 在 `UseOfflineArgos` 为真时直接返回离线缺包/缺运行环境说明，不列视觉/LLM/有道/免费。单独离线配置下这是正确隐私口径；但 Vision+Offline 同时为真时，UI 实际走视觉，说明却仍是离线。

**落地**：`DescribeBackends` 现在先判 `VisionConfigured`：

- 只有离线、没有视觉：保留原来的离线单独说明，不虚构免费端点；
- 视觉+离线同时配置：输出“已启用的翻译路径”，同时列出「视觉翻译已启用（整图直传）」「离线翻译已启用（本地 Argos，文字不出本机；缺语言包/运行环境时会提示补装）」以及已配置的 LLM/有道/免费端点；
- 同一句写清分派优先级：「视觉（整图） > 离线 > LLM 文本批量 > 逐行」，并说明视觉失败不会自动改走离线、离线失败不会自动改走视觉。

**判据**：`Test_TranslationCombinationMatrix` 的 64 组配置现在区分“仅离线”和“视觉+离线”两种文案：后者必须同时含视觉、离线、优先级，并按配置出现/不出现 LLM/有道/免费。

### 7.3 视觉失败是否自动回退文本路

**现状**：视觉失败直接抛 `TranslationException`，由状态栏显示，让用户切模式；不自动回退文本路。

**判断**：既有设计合理：用户选视觉是为了整图语义，偷偷回退文本会让人以为走的是视觉模型。本轮保留，只验证 0 行/超限/行数不等的拒绝。

### 7.4 离线就绪与桥脚本异常仍缺独立判据

本轮补了 `NormalizeLang` 与空批量，但 `CheckReady` 的 ModelMissing/RuntimeMissing 语义、桥脚本行数不符/退出码/超时的独立行为判据仍未补；这些属于离线包的下一层回归。

### 7.5 在线 E2E：默认执行（已落地）

`--service-tests` 的免费公共端点在线翻译不再需要 `TTC_LIVE_TRANSLATE=1`；默认随套件执行。不能出网的环境用 `TTC_SKIP_LIVE_TRANSLATE=1` 显式关闭，关闭会写进 skipped 并在门禁汇总里点名，而不是静默消失。

**实测**：默认配置、未填任何凭据，发 `"Please save the file before exiting."`，1.0 s 收到 `"请在退出前保存文件。"`，归因后端 `Edge (translatetext)`；套件 `4 通过 / 0 失败 / 0 跳过`。

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

结果：**2178 通过 / 0 失败 / 1 跳过**（跳过项为“本机无 AV1 MFT 编码器”）。

### 8.3 全量校验

```powershell
$python = "C:\Users\20210\AppData\Local\Programs\Python\Python312\python.exe"
Set-Alias -Name python -Value $python -Scope Global
.\scripts\verify.ps1 -SkipBuild -SkipPublishGates
```

结果：**8 个套件全部通过，共 2840 断言**；`icc_profile_id_check.py` 因前置条件不足跳过（1 处未判，不计为通过）；发布门禁本轮跳过；在线免费端点 E2E 已默认执行并通过。

| 套件 | 结果 |
|---|---|
| `--unit-tests` | 2178 通过 / 0 失败 / 1 跳过 |
| `--color-tests` | 291 通过 / 0 失败 |
| `--encoding-tests` | 23 通过 / 0 失败 |
| `--service-tests` | 4 通过 / 0 失败（在线翻译 E2E 默认执行） |
| `--ocr-tests` | 12 通过 / 0 失败 |
| `--qr-tests` | 47 通过 / 0 失败 |
| `--format-tests` | 52 通过 / 0 失败 |
| `--usability-tests` | 233 通过 / 0 失败 |

### 8.4 构建/校验过程中的环境问题

本轮校验曾因 C 盘空间不足导致构建输出被截断，出现过 `MSB3021/MSB3027 磁盘空间不足` 与 `TrueToneCap.exe`/`TrueToneCap.Test.exe` 找不到；在清出可再生成空间后重新构建，8 套件与 App 设置迁移判据全部恢复通过。这不是代码回归，但说明“磁盘剩余空间”应作为跑 `verify.ps1` 的前置条件记录。

另一次 `-SkipBuild` 首跑出现“Tools 宿主比测试源码旧”的宿主新鲜度警告；按脚本建议做 `dotnet build ... --no-incremental` 后，2 个宿主均不早于 181 份构建输入，最终全量校验未再带该警告。

---

## 9. 结论

- **默认配置（LLM + 端点，免费端点默认开启）**：文本批量走 LLM；批量失败先同 LLM 拆分/重试/逐行，跨后端只在最后；401 不再谎称逐行回退。LLM 已勾选但端点空白时，自动回退仍发生，但状态栏和日志必须显式警告。
- **视觉配置**：整图直传；0 行/超限/行数不等在发请求或写盘前拒绝；不退回文本路；响应对象已释放。
- **视觉+离线同时配置**：文案同时列出视觉与离线两条路径并写清「视觉 > 离线 > LLM 文本批量 > 逐行」；不会再只返回离线说明。
- **离线配置**：整批一个本地进程；语言码归一与空批量已补判据；失败不回在线。
- **免费/有道配置**：只作为逐行路径或批量最后兜底；解析器对畸形响应返回 `null` 并留原因；用户取消不被当成超时。
- **在线 E2E**：默认执行，实测 Edge 免费端点返回中文译文；不能出网时用 `TTC_SKIP_LIVE_TRANSLATE=1` 显式关闭并计入 skipped。
- **空行/取消/分片边界**：全空白批量不要求后端；空行不占编号、不导致漏行；取消不发请求、不重试、不回退。
- **剩余风险**：§7.4 的离线 `CheckReady`/桥脚本异常仍缺独立行为判据；§7.3 视觉失败不回退文本路是既定设计；真实 LLM 服务商/真实有道凭据的在线 E2E 仍需单独配置凭据后验。
