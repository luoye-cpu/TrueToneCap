# 翻译/PNG 审查问题的完整修复（2026-11-16）

## 结论

本轮把过去 72 小时审查中“会影响正确性/可靠性”的问题收敛为四类修复，并补了
PNG「优」档 16-bit 的真实往返判据：

1. 收紧单行译文的拒答/解释性判断，避免合法英文 UI 文案被误拒；
2. 把 404/405/410 判为不可恢复的配置错误，并限制 400/422 的拆片—逐行放大；
3. 对象数组缺 `id` 时不再按位置静默回填；
4. 旧模型名迁移只作用于空端点/已知预设端点，不再改写自定义网关；
5. 补 `Encode16 + Optimal + omitAlpha` 的像素、bpp、cICP 与容器结构判据。

修复后 Release 构建 0 警告、0 错误；8 个测试套件共 2905 条断言通过。
外部 precision 判据因本机缺少 Pillow/PIL 依赖、发布门禁因进程占用，按既有环境
限制显式跳过，未计入“通过”。

## 逐项修复

### 1. `SanitizeSingleLine`：从“包含关键词”改为“完整拒答/明确元话术”

修复前：

```csharp
if (lower.Contains("as an ai")
    || lower.Contains("i cannot")
    || lower.Contains("i'm sorry")
    || lower.StartsWith("note:"))
    return null;
```

目标语言是英文时，以下合法 UI 文案会被误拒：

- `I cannot open the file`
- `Note: takes effect after saving`
- `I'm sorry, something went wrong`

误拒会触发拆片、逐行重试，极端情况下再触发跨后端降级，形成“原文没问题、链路自己
把请求放大”的假故障。

修复后只拒收：

- 完整的 `I'm sorry.` / `I cannot.` / `As an AI ...`；
- 明确的拒答动词组合，如 `I cannot assist/translate/comply/...`；
- 以 `Here is the translation` 开头的元话术；
- 以 `Note:` / `Explanation:` 开头且包含明确元话术（`the original`、`source text`、
  `the translation`、`tone`、`wording` 等）的内容；
- 中文 `以下是翻译` / `译文如下` / `翻译如下`。

同时补了正例与反例：合法英文能力提示、`Note:` 标签、含 `source file` 的警告必须
通过；真正的拒答句仍必须拒收。

### 2. 批量失败分类：404 不再拆片，400 不再无界放大

`LlmBatchFailureKind` 新增 `Configuration`，映射：

- HTTP 404 / 405 / 410（以及 `HttpRequestException` 对应状态）→ `Configuration`；
- 401 / 403 → `Auth`；
- 429 → `RateLimited`；
- 408 → `Timeout`；
- 5xx → `Transient`；
- 400 / 422 → `Protocol`。

`Configuration` 与 `Auth` 一样直接返回 `Fatal`，调用方停止后续拆片、逐行和跨后端
尝试。模型名/端点路径写错时只发一次请求，不再把 8 行放大成十几次必败请求。

400/422 保留一次拆片语义；新增判据：如果两半都因 `Protocol` 失败且一行都没救回，
则停止逐行重试。若只有一半失败，仍保留逐行兜底，避免丢掉另一半可翻译的行。

行为判据使用本地 `FakeLlmServer`：

- 404：`RequestCount == 1`，`FailedCount == 2`，`FallbackCount == 0`，错误里保留 404；
- 400：批量 1 次 + 拆片 2 次，`RequestCount == 3`，不进入逐行放大。

### 3. `ParseJsonBatchArray`：对象数组必须带 `id`

修复前对象数组全部缺 `id` 时，会按位置回填。模型若返回对象数组、漏掉 `id` 且改变
顺序，解析器仍会接受，造成静默错行。

修复后：

- 对象数组只要有一项缺 `id`，整批拒收；
- 对象/字符串混合数组拒绝位置回填；
- 字符串数组仍按顺序回填（这是提示词允许的兼容形态）；
- 带 `id` 的对象数组继续按 `id` 回填，不信任顺序。

补了“对象数组漏 id 必须拒收”“混合数组必须拒收”两条判据。

### 4. 模型迁移：只改预设端点，不碰自定义网关

修复前 `SettingsService.Normalize` 无条件调用
`LlmModelPolicy.Normalize(s.LlmModel)`。用户自建网关仍在提供 `deepseek-chat` /
`gpt-4o-mini` 时，设置一加载就会被静默改成 `deepseek-flash` / `gpt-5.4-mini`。

修复后：

- `LlmModelPolicy.Normalize(model, apiEndpoint)` 增加端点参数；
- 空端点保持旧行为（老设置继续迁移）；
- 只有主机名命中已知预设（DeepSeek / GLM / DashScope / SiliconFlow / Moonshot /
  Volcengine / MiniMax / OpenAI / Anthropic / Gemini / xAI / Mistral / OpenRouter /
  OpenCode）才迁移；
- 自定义主机、无法解析的端点原样保留模型名；
- `SettingsService` 传入 `s.LlmEndpoint`，并补源码接线判据。

### 5. PNG「优」档 16-bit 往返

新增 `Png_Optimal16_Roundtrip`：

- 输入 16-bit RGBA，调用 `Encode16(..., effort: Optimal, omitAlpha: true)`；
- 用独立 PNG 读取器校验 IHDR 为 16-bit / colorType 2；
- 校验 cICP 原样保留、16-bit 容器不写 sBIT；
- 校验 IDAT/IEND 结构；
- 反滤波后 RGB 逐采样与源值相等，直接咬住 16-bit RGB 的 `bpp=6`（不是 RGBA16 的 8）。

该判据会抓到“分带 brute / Entropy / 固定 Sub/Paeth 候选把 bpp 当成 8”的错位。

### 6. 命名/注释清理

`FitTwoTierEffort` 已不再只服务两档，重命名为 `FitEffortMenu`；更新了设置迁移、
模型字段和测试中的相关注释，明确当前菜单是：

- PNG 三档：`Optimal / Smallest / Quick`，默认 `Smallest`；
- TIFF / WebP 两档：`Smallest / Quick`；
- AVIF / JPEG XL 三档：`Smallest / Balanced / Fastest`；
- JPEG LI 的 XYB/传统是独立色彩空间开关，不再属于压缩强度档。

## 验证

命令：

```powershell
dotnet build "C:\PLAN\TrueToneCap\TrueToneCap.slnx" -c Release --no-incremental -v:minimal
& "C:\PLAN\TrueToneCap\scripts\verify.ps1" -SkipPublishGates -SkipPrecision
```

结果：

- Release 构建：0 警告 / 0 错误；
- 8 个测试套件全部通过，共 2905 条断言：
  - `--unit-tests` 2238 通过 / 0 失败 / 1 跳过；
  - `--color-tests` 291；
  - `--encoding-tests` 23；
  - `--service-tests` 4；
  - `--ocr-tests` 12；
  - `--qr-tests` 47；
  - `--format-tests` 52；
  - `--usability-tests` 238。
- 新增判据均已实跑：404/Configuration、400/Protocol、缺 id 对象数组、合法英文 UI
  文案、自定义端点保留、16-bit Optimal omitAlpha。

## 仍未闭环/已知限制

1. **外部 precision 判据未判**：本机 Python 缺 Pillow/PIL、lcms2 依赖，`-SkipPrecision`
   下外部解码器读回未执行。这是环境缺口，不是本轮代码通过。
2. **发布门禁未跑**：`TrueToneCap.exe` 可能处于运行状态，按 `-SkipPublishGates`
   跳过；本轮没有发布、没有 push。
3. **供应商/模型预设没有 live 冒烟**：仓库内没有真实 API 密钥，Anthropic/Gemini 的
   OpenAI 兼容入口、GPT-5/6 的 `max_tokens` 兼容性仍是外部风险；静态预设表闭合判据
   不能替代真实请求。
4. **超长单行仍不二次拆分**：`BuildLlmBatchChunkIndices` 对超过 3000 字符的单行仍
   自成一片。拆行会改变 OCR 行的语义与译文合并方式，本轮没有在未确认产品口径前
   改动；当前通过“批量失败后不无界放大”限制了它的请求成本，但没有消除单行上下文
   超限的可能。
5. **400 的语义仍无法只凭状态码完全区分**：本轮已限制“两半都失败且零救回”时停止
   逐行，但不能保证所有服务商的 400 都按同一语义返回。
