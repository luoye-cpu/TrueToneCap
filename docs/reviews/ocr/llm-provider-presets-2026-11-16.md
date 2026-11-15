# LLM 供应商与模型预设更新（2026-11-16）

> 目标：把 TrueToneCap 设置里的 LLM 供应商/模型从 2025 代预设更新到 **2026 年 11 月**公开可用的 OpenAI 兼容端点与主流模型。
> 检索日期：2026-11-16。模型 ID 以各平台官方文档/模型列表为准；文档改版后需要重新核对。

## 1. 结论

- 设置页由 **8 个固定供应商 + 自定义** 扩到 **14 个固定供应商 + 自定义**。
- 默认模型从已停止推荐的 `deepseek-chat` 改为 **`deepseek-flash`**（DeepSeek V4.1 Flash，原生支持图片输入）。
- 模型下拉由 15 个旧模型项更新为 **50 个平台模型项 + 自定义**（其中新增 OpenRouter 9 项、OpenCode Zen 6 项）。
- 旧设置里的 `deepseek-chat`、`gpt-4o-mini`、`glm-4.7-flash`、`gemini-2.0-flash`、`moonshot-v1-8k` 等模型名会在加载时迁移到新预设；未知模型名保持原样，避免破坏自定义端点。
- Claude 5 / GPT-5 / GPT-6 的请求不再固定发送 `temperature: 0.1`：Anthropic 兼容层要求 Claude 4.7+ 省略该字段，GPT-5/GPT-6 推理模型也不保证接受；其余平台继续使用 0.1。

## 2. 筛选口径

本次只收录满足以下条件的平台：

1. 有公开的 **OpenAI 兼容 `chat/completions`** 入口；
2. 有公开文档可查的模型 ID；
3. 端点能直接作为设置中的“API 端点”使用，不需要额外的原生协议适配层。

因此：

- Claude 通过 Anthropic 的 **OpenAI SDK compatibility** 入口接入；该兼容层官方定位是测试/对比用途，不是长期生产级方案，报告在风险一节保留这一限制。
- Gemini 通过 Google 官方的 **OpenAI compatibility** 入口接入。
- OpenRouter 与 OpenCode Zen 作为聚合平台接入，但**只收录少量主流/免费模型**，不是整库导入：
  - OpenRouter 直接走 `https://openrouter.ai/api/v1`，模型 ID 保留其原生前缀（如 `anthropic/claude-sonnet-5`、`openai/gpt-5.6-terra`）。
  - OpenCode Zen 只收录其官方表中明确走 `chat/completions` 的模型；其 GPT/Claude/Gemini 原生条目分别走 `responses`/`messages`/Google 模型端点，本项目没有这些协议适配层，因此不放进下拉。
  - 聚合平台的免费模型多为限时/限流/可能用于改进模型，不能当成稳定生产后端；报告在风险一节标注。

## 3. 国内平台

| 平台 | API 端点 | 默认模型 | 下拉中的其他模型 | 选择依据 |
|---|---|---|---|---|
| DeepSeek | `https://api.deepseek.com` | `deepseek-flash` | `deepseek-v4-pro` | 官方价格页：`deepseek-flash` = V4.1-Flash-1017，支持 Vision；`deepseek-v4-pro` 不支持 Vision |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-5.3-flash` | `glm-5.3`、`glm-5.3-flashx` | 官方模型概览：GLM-5.3 为旗舰文本，GLM-5.3-Flash 原生多模态 |
| 阿里云百炼 Qwen | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen3.8-flash` | `qwen3.7-plus`、`qwen4-max-preview`、`qwen3-vl-plus` | 百炼模型大全（2026-11-11 更新）：Qwen4 Max Preview / Qwen3.8 Flash / Qwen3.7 Plus |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `deepseek-ai/DeepSeek-V4.1-Flash` | `Qwen/Qwen3.8-27B`、`moonshotai/Kimi-K3` | 平台模型广场的公开模型 ID；具体账户可用性以模型广场为准 |
| Moonshot Kimi | `https://api.moonshot.cn/v1` | `kimi-k3.1-pro` | `kimi-k3.1-pro-highspeed`、`kimi-k3` | 官方模型列表：K3.1 Pro 原生视觉、1M 上下文；K3 为上一代 |
| 火山方舟 Doubao | `https://ark.cn-beijing.volces.com/api/v3` | `doubao-seed-2-1-pro-260915` | `doubao-seed-2-1-lite-260915`、`doubao-seed-2-1-turbo-260628` | 方舟模型列表（2026-10-24 更新）：Seed 2.1 Pro 为推荐多模态模型 |
| MiniMax | `https://api.minimax.cn/v1` | `MiniMax-M3` | `MiniMax-M2.7`、`MiniMax-M2.7-highspeed` | 官方 OpenAI SDK 文档：M3 为最新 M 系列，支持图片/视频输入 |

## 4. 国外平台

| 平台 | API 端点 | 默认模型 | 下拉中的其他模型 | 选择依据 |
|---|---|---|---|---|
| OpenAI | `https://api.openai.com/v1` | `gpt-5.4-mini` | `gpt-6.1-terra`、`gpt-5.5`、`gpt-5.4-nano` | 官方 Models 页当前推荐 GPT-5.5 / GPT-5.4 mini / GPT-5.4 nano；发布说明确认 GPT-6.1 Terra 已以 `gpt-6.1-terra` 进入 API |
| Anthropic Claude | `https://api.anthropic.com/v1/` | `claude-sonnet-5` | `claude-opus-5`、`claude-fable-5`、`claude-haiku-4-5` | 官方 Models overview：当前模型为 Fable 5 / Opus 5 / Sonnet 5 / Haiku 4.5；OpenAI compatibility 入口支持 `image_url` |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta/openai` | `gemini-3.8-flash` | `gemini-3.1-flash-lite`、`gemini-4-argon` | 官方 OpenAI compatibility 页示例使用 `gemini-3.8-flash`，并确认图片理解；模型页列出 Argon / 3.1 Flash-Lite |
| xAI Grok | `https://api.x.ai/v1` | `grok-4.7` | — | 官方 Models 页（2026-09-21 更新）：Grok 4.7 为当前旗舰 |
| Mistral | `https://api.mistral.ai/v1` | `mistral-large-latest` | `mistral-small-latest` | 官方 Models overview：Mistral Large 4（v26.10）为当前多模态旗舰，Mistral Small 4 为轻量档 |

> 说明：`gpt-5.4-mini` 是本次 OpenAI 的默认项，原因是官方 Models 页仍把它列为低延迟/低成本推荐，且支持图片输入；`gpt-6.1-terra` 虽然更新，但 API 可用性和账户权限可能随组织而异，所以列为可选而不是默认。

## 4.5 聚合平台（新增）

| 平台 | API 端点 | 默认模型 | 下拉中的其他模型 | 选择依据 |
|---|---|---|---|---|
| OpenRouter | `https://openrouter.ai/api/v1` | `google/gemini-3.8-flash` | `openrouter/auto`、`openrouter/free`、`anthropic/claude-sonnet-5`、`openai/gpt-5.6-terra`、`deepseek/deepseek-v4.1-flash`、`x-ai/grok-4.7`、`moonshotai/kimi-k3.1-pro`、`nvidia/nemotron-3.5-super-vl:free` | 官方 API 明确 OpenAI-compatible；模型目录 API 可查到以上 ID，所选模型均支持图片输入；`openrouter/free` 是免费模型路由，`nvidia/...:free` 是具体免费多模态模型 |
| OpenCode Zen | `https://opencode.ai/zen/v1` | `deepseek-v4.1-flash` | `mistral-large-4`、`qwen3.8-max`、`deepseek-v4.1-flash-free`、`glm-5.3-flash-free`、`nemotron-3.5-lightning-free` | 官方 Zen 端点表和 `/zen/v1/models` 明确这些模型走 `chat/completions`；GPT/Claude/Gemini 原生条目走 `responses`/`messages`/Google 端点，未纳入 |

OpenRouter 的 `auto` / `free` 是路由模型而非单一模型：`auto` 自动挑选，`free` 只路由到免费模型。它们适合试用，不建议作为翻译的固定生产默认；OpenRouter 默认选用 `google/gemini-3.8-flash` 是为了成本和视觉能力可控。

OpenCode Zen 的 `glm-5.3-flash`、`kimi-k3` 等模型与直连平台同名；为避免全局扁平下拉出现重复 Tag，本项目保留直连平台条目，不再重复加入聚合层。

## 5. 代码落点与迁移

生效的三处活表：

1. `src/TrueToneCap.App/MainWindow.xaml`
   - `LlmProviderCbo`：14 个固定供应商 + 自定义
   - `LlmModelCbo`：50 个平台模型 + 自定义
2. `src/TrueToneCap.App/MainWindow.OcrSettings.cs`
   - `OnLlmProviderChanged`：tag → (endpoint, 默认模型)
3. `src/TrueToneCap.Core/Services/LlmModelPolicy.cs`（新增）
   - `DefaultModel = "deepseek-flash"`
   - `Normalize()`：旧模型名迁移
4. 默认值
   - `AppSettingsData.LlmModel`
   - `TranslationService.LlmConfig.ModelName`
   - 文本/视觉链路的三处 fallback
5. 文档
   - `docs/architecture-design.md`：明确只实现 OpenAI 兼容形状，Claude/Gemini 走兼容层
   - `PRIVACY.md`：补充新增平台示例

旧名迁移映射（节选）：

```text
deepseek-chat                  -> deepseek-flash
deepseek-v4-flash              -> deepseek-flash
glm-4.7-flash / glm-4-flash    -> glm-5.3-flash
gemini-2.0-flash / 2.5-flash   -> gemini-3.8-flash
gemini-2.5-pro                 -> gemini-4-argon
gpt-4o-mini / gpt-4.1-mini     -> gpt-5.4-mini
gpt-4.1-nano                   -> gpt-5.4-nano
moonshot-v1-8k                 -> kimi-k3.1-pro
qwen-turbo / qwen-plus         -> qwen3.8-flash
Qwen/Qwen2.5-72B-Instruct      -> Qwen/Qwen3.8-27B
deepseek-ai/DeepSeek-V3/R1     -> deepseek-ai/DeepSeek-V4.1-Flash
```

未知模型名不会被改写，避免用户自定义端点被静默破坏。

## 6. 请求兼容性

`LlmJsonContext` 的 `temperature` 改为可空，并在为 null 时由源生成器省略字段：

- `claude-*` / `anthropic/claude-*`：省略。Anthropic OpenAI compatibility 文档明确 Claude 4.7+ 对小于 1 的 temperature 返回 400，要求 omit；聚合平台的前缀模型按最后一段模型名识别。
- `gpt-5*` / `gpt-6*` / `openai/gpt-5*` / `openai/gpt-6*`：省略。OpenAI 的 GPT-5/GPT-6 为推理模型，不保证接受 Chat Completions 的 temperature。
- `openrouter/auto` / `openrouter/free`：省略，路由下游模型不保证接受该字段。
- 其余平台：保留 `0.1` 的翻译温度（包括 OpenCode 的 DeepSeek/GLM/Qwen 等）。

这避免了“新模型预设在第一次请求就 400”，同时不改变旧平台的行为。

## 7. 验证

- Release 构建：0 警告 / 0 错误。
- `--unit-tests`：2194 通过 / 0 失败 / 1 跳过（新增 16 条断言：旧名迁移、空值回落、未知模型保留、Claude/GPT temperature 省略、聚合前缀 temperature 省略、JSON 字段省略）。
- `--service-tests`：4 通过 / 0 失败；默认配置不填凭据时 E2E 收到中文译文（`Please save the file before exiting.` → `请在退出前保存文件。`，1047 ms，后端归因 Edge）。
- `scripts/verify.ps1 -SkipBuild -SkipPublishGates`：8 个套件全部通过，共 2856 断言（最终一轮 79.5 s）。其中 unit 2194、color 291、encoding 23、service 4、ocr 12、qr 47、format 52、usability 233；另有 1 处 `icc_profile_id_check.py` 因缺带非零 ProfileID 的前置样张未判（与本次 LLM 改动无关）。

## 8. 风险与未采纳项

- 模型 ID 会随平台迭代下线/改名；本预设是 2026-11-16 的静态快照，不包含自动发现机制。
- Anthropic 的 OpenAI 兼容层官方声明“主要面向测试/能力对比，不是长期生产级方案”；若后续要做 Claude 原生能力（PDF、citations、prompt caching、thinking），需要单独的 Anthropic Messages API 适配层。
- Gemini、Claude 的原生协议没有接入；当前只用其 OpenAI 兼容入口。
- 聚合平台的模型 ID 带前缀或路由语义；本项目的下拉是全局扁平列表，因此只收录少量固定 ID，不导入整库。OpenRouter/OpenCode Zen 的免费模型可能限时、限流或用于改进模型，不建议作为稳定生产后端。
- OpenCode Zen 的 GPT/Claude/Gemini 原生条目使用 `responses` / `messages` / Google 端点，本项目只支持 `chat/completions`，所以没有加入这些模型。
- 未在 CI 中调用真实 API；本轮只验证请求形状和本地单测，不消耗用户密钥。

## 9. 来源

- DeepSeek Models & Pricing：<https://api-docs.deepseek.com/quick_start/pricing>
- 智谱模型概览：<https://docs.bigmodel.cn/cn/guide/start/model-overview>
- 智谱 OpenAI API 兼容：<https://docs.bigmodel.cn/cn/guide/develop/openai/introduction>
- 阿里云百炼模型大全：<https://help.aliyun.com/zh/model-studio/models>
- 硅基流动快速上手：<https://docs.siliconflow.cn/cn/userguide/quickstart>
- Kimi 模型列表：<https://platform.kimi.com/docs/models>
- 火山方舟模型列表：<https://docs.volcengine.com/docs/82379/1330310>
- MiniMax OpenAI SDK：<https://platform.minimaxi.com/docs/api-reference/text-openai-api>
- OpenAI Models：<https://platform.openai.com/docs/models>
- OpenAI 模型发布说明：<https://help.openai.com/zh-hans-cn/articles/9624314-model-release-notes>
- Anthropic Models overview：<https://platform.claude.com/docs/en/about-claude/models/overview>
- Anthropic OpenAI SDK compatibility：<https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk>
- Gemini OpenAI compatibility：<https://ai.google.dev/gemini-api/docs/openai>
- xAI Models：<https://docs.x.ai/docs/models>
- Mistral Models overview：<https://docs.mistral.ai/getting-started/models/models_overview/>
- OpenRouter API 模型目录：<https://openrouter.ai/api/v1/models>
- OpenRouter 免费模型集合：<https://openrouter.ai/collections/free-models>
- OpenCode Zen 文档：<https://opencode.ai/docs/zen/>
- OpenCode Zen 模型目录：<https://opencode.ai/zen/v1/models>
