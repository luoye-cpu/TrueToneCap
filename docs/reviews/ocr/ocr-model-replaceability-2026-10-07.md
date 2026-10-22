# OCR 模型可更换性分析与选型建议

> 日期：2026-10-07
> 方法：源码审计（只读）+ 官方模型仓实测（下载真实 ONNX 并推理）
> 结论均为**本地实测**，非搜索结果推断。所有哈希/体积/类目数都可复现。
>
> 📎 配套文档：`docs/ocr-model-landscape-2026.md` —— 由并行调研产出，覆盖更广的模型生态（VLM/LiteRT/许可矩阵/量化路线/RapidOCR 规模）。本文聚焦**本仓的可更换性与落地路径**，两份文档结论一致、互为补充。

---

## 0. 一句话结论

**可以更换，而且成本比预期低得多——但有一个"看起来能直接换、实际会静默产生乱码"的陷阱必须处理。**

- 当前 OCR 是 **PP-OCRv6 medium**（det + rec + 18708 字统一字典），架构上**已经留好了换模型的缝**（`OcrModelConfig`），但这道缝**没接完**：生产代码从不传配置，模型文件名是**写死的候选数组**。
- 官方 `PP-OCRv6` 家族**真实存在**（tiny/small/medium 三档），本仓用的 medium 就是官方 medium。
- **最大发现**：官方 `korean_PP-OCRv5_mobile_rec.onnx`（12.8 MB）能让韩文从 **0% → 100%**，实测已复现。
- **陷阱（已澄清）**：CTC 解码写死 `dict[d-1]`（blank=0）。**实测所有 PP-OCR rec 模型（tiny/small/medium/korean/v5）都是"类目数 = 字典 + 2"的同一约定，所以换模型时这条恰好通用**。但代码**没有任何校验**——偏移若变化，不报错、只出全篇偏移乱码。建议加一条断言把静默错误变成显式错误（成本几行代码）。
- **不建议**上 VLM（PaddleOCR-VL 等）：1.79 GB 权重 + 需要 Python/Paddle 运行时，与当前"69 MB 纯 ONNX 零依赖"的定位冲突，且会破坏全部 DirectML 调优。

---

## 1. 当前状态（实测事实）

### 1.1 随包分发的模型

| 文件 | 体积 | 实测输入 | 实测输出 | 类目数 |
|---|---|---|---|---|
| `PP-OCRv6_medium_det.onnx` | 31,146,237 B (FP16) | `x [-1,3,-1,-1]` | `fetch_name_0 [-1,1,-1,-1]` | — |
| `PP-OCRv6_medium_rec.onnx` | 38,399,862 B (FP16) | `x [-1,3,48,-1]` | `fetch_name_0 [-1,-1,18710]` | 18710 |
| `ppocrv6_dict.txt` | 93,655 B | — | — | 18708 行 |

> 实测方式：用本仓的 `Microsoft.ML.OnnxRuntime.DirectML 1.24.4` 直接 `new InferenceSession(...)` 读 `InputMetadata`/`OutputMetadata`。

**注意**：`48` 是**烘焙进计算图**的（`[-1,3,48,-1]`），不是运行时可变维度。换用 `RecImgH != 48` 的模型必须同时改 `OnnxOcrEngine` 的张量构造。

### 1.2 字典字符组成（实测）

| 类别 | 数量 |
|---|---|
| 总行数 | 18708 |
| CJK（U+4E00–9FFF） | 15565 |
| 日文假名（U+3040–30FF） | 180 |
| **谚文音节（U+AC00–D7A3）** | **0** ← 韩文 0% 的根因 |

`docs/ocr-accuracy-analysis.md` 的判断（"不是识别模型的问题，而是字典对韩文覆盖为 0"）**本次实测完全证实**。

### 1.3 一个已被复现的上游 Bug

直接加载**随包的 FP16 rec**（默认 SessionOptions）会抛：

```
onnxruntime::graph_utils::GetIndexFromName ... Attempting to get index by a name which does not exist:
InsertedPrecisionFreeCast_helper.constant.49 for node: Mul.57/SimplifiedLayerNormFusion/
```

这正是 `OnnxOcrEngine.CreateSessionOptions()` 里 `disable_simplified_layer_norm_fusion` 那段注释描述的问题（源码 `OnnxOcrEngine.cs:276-288`）。**该 workaround 至今有效且必需**——一旦 `AddSessionConfigEntry` 被 `catch {}` 静默吞掉，OCR 就会整体失效。这是一个应加断言的点，不是可以留白的点。

---

## 2. 更换的难度：三个层次

### 层次 A — 换同家族模型（**小改，1–2 小时**）

换 det/rec 到 PP-OCRv6 的其它档位，或换语言专用 rec 模型（若字典偏移量相同）。

**必须动的点**（已定位到行）：

| 位置 | 内容 |
|---|---|
| `OnnxOcrEngine.cs:81-103` | `DetFallbackNames` / `RecFallbackNames` / `DictFallbackNames` —— **文件发现靠这三个写死数组**，不是靠 `OcrModelConfig.DetFileName`（后者是死字段，加载器从不读） |
| `OnnxOcrEngine.cs:46-77` | `OcrModelConfig.Default` 的阈值/均值方差/`RecImgMaxW` |
| `Publish.ps1:187-191` | `$modelFiles` 恰好列 3 个文件名，缺失即**拒绝发布** |
| `Publish.ps1:240-267` | `models-manifest.json` 的 SHA256 校验 |
| `scripts/verify-package.ps1:177-179` | 发布后按名断言存在 + 体积下限 |
| `download_models.ps1:63-71` | 第三份 SHA256 真值表 |
| `MainWindow.OcrSettings.cs:34` + `LocaleManager.cs:536` | UI 硬编码"18708 字"和"韩文不支持"，换模型后**界面会说谎** |

**好消息**：`TrueToneCap.App.csproj:50,56` 用的是通配 `Models\*.onnx` / `Models\*.txt`，因此**多放一个模型文件不需要改 csproj**。

### 层次 B — 换字典偏移量不同的模型（**必做，否则静默乱码**）

**这是本次分析最重要的技术发现。**

代码写死（`OnnxOcrEngine.cs:812, 832-836`）：

```csharp
if (maxIdx != lastChar && maxIdx > 0 && maxVal >= ctcConfidenceThreshold)  // blank 固定为 0
...
// 字典映射: 模型输出索引 1..N 对应字典 0..N-1 (blank 占索引 0)
return string.Concat(decoded.Where(d => d - 1 < _dict.Length).Select(d => _dict[d - 1]));
```

实测各模型的 `输出类目数 − 字典行数`：

| 模型 | 类目数 | 字典 | 差值 | 正确偏移 |
|---|---|---|---|---|
| 随包 `PP-OCRv6_medium_rec`（FP16） | 18710 | 18708 | **+2** | `dict[d-1]` ✅ 现行为 |
| 官方 `PP-OCRv6_small_rec`（FP32） | 18710 | 18708 | **+2** | `dict[d-1]` |
| 官方 `PP-OCRv6_tiny_rec` | 6906 | 6904 | **+2** | `dict[d-1]` ✅ 实测 `"你好世界Hello"` |
| 官方 `korean_PP-OCRv5_mobile_rec` | **11947** | **11945** | **+2** | `dict[d-1]` |
| 官方 `PP-OCRv5_mobile_rec` | 18385 | 18383 | **+2** | `dict[d-1]` |

> ✅ **实测结论：目前所有已测的 PP-OCR det/rec 模型（含 tiny 档）都是同一约定"类目数 = 字典 + 2"，`dict[d-1]` 通用正确。** 这意味着更换同族模型时**字典偏移量通常不用改**——这是好消息。
>
> ⚠ 但这属于**经验规律而非代码保证**：代码里没有任何地方校验它，`_dict[d-1]` 是裸算式。偏移若变化（未来版本、或 `use_space_char` 变体），**代码不报错、只输出全篇偏移乱码**——上面 `offset 0` / `offset -2` 两行就是这个失败模式的实测样子。因此加载时加一条 `numClasses - dict.Length` 断言，成本极低而收益是"把静默错误变成显式错误"。
>
> 📌 另一个实测教训：用**错误的字典文件**去解 tiny 模型时，输出是看似合理的 `"1"` —— 不报错、不崩溃。这类静默错误正是断言存在的理由。
>
> 🔍 **顺带澄清一个疑点**：`1 + 18708 = 18709 ≠ 18710` 看似差一，实为**已解释**——`OnnxOcrEngine.cs:790` 的注释写明了 `blank(0) + 18708 字典 + 1 尾随特殊类 = 18710`。实测在 tiny 模型上观察到 argmax 触及 `idx=6905`（= 6904 + 1），证实那个尾随类**确实会被激活**。因此 `dict[d-1]` 的边界守卫（`d - 1 < _dict.Length`，`:835`）是必要且正确的：尾随类会被过滤掉，不会越界。

实测对照（同一张渲染图，贪心 CTC 解码）：

```
korean_rec.onnx      dict[d-1] => "안녕하세요세계"     ← 正确
korean_rec.onnx      dict[d]   => "앉녖학섹욕섹곅"     ← 偏移一格的典型乱码
PP-OCRv6_medium_rec  dict[d-1] => ""                  ← 韩文全丢
```

**所以"换模型"的正确做法不是改名，而是把偏移量变成配置项**（例如 `OcrModelConfig.CtcDictOffset`），并在加载时**断言 `numClasses - dict.Length` 落在预期集合内**，不匹配就明确报错而不是静默出垃圾。

### 层次 C — 换架构（VLM，**不建议**）

| 方案 | 许可 | 体积 | 运行时 | 适配本仓 |
|---|---|---|---|---|
| PaddleOCR-VL / -0.9B | Apache-2.0 | **1.79 GB** safetensors | 需 Python/Paddle + custom_code | ❌ |
| dots.ocr | — | ~1.7B 级 | 需 transformers | ❌ |
| olmOCR-7B | — | 7B 级 | 需 GPU 大量显存 | ❌ |
| Surya | GPL 系 | 数百 MB | 需 Torch | ❌ 许可也不兼容 |

**为什么不建议**：
1. 权重 1.79 GB vs 现在整包模型 69 MB，**体积 ×26**，与"纯 ONNX 零 Python 依赖"（`OnnxOcrEngine.cs:4` 明确写着）的核心卖点冲突。
2. 不是 `1×3×H×W` → `1×T×C` 的 DBNet+CTC 管线。`ExtractBoxes`（连通域）、CTC 解码、BGR/mean-0.5 预处理**全部不适用**，等于新写一个引擎。
3. `OcrEngineType` 枚举按**名字**持久化，且 `SettingsService.cs:432` 有白名单 `FitTag(..., "OnnxCpu","OnnxGpu","WindowsOcr")` —— 新增枚举值若不改白名单，会被**静默改回 OnnxGpu**。
4. 现有 SessionOptions（`ORT_SEQUENTIAL`、`InterOpNumThreads=1`、禁用 LayerNorm fusion）是为小 CNN 调的，会**拖慢** transformer 解码器。

**更好的替代**：本仓已有 **Vision 模式**（`MainWindow.OcrSettings.cs:139-156`）——整张图直传给支持视觉的 LLM 完成识别+翻译。需要"理解版面/表格/公式"时应强化这条云路径，而不是往本地塞 1.8 GB。

---

## 3. 官方可选模型清单（全部实测体积，可下载）

### 3.1 PP-OCRv6 家族 —— **确认存在，本仓用的就是官方 medium**

来源：`PaddlePaddle/PP-OCRv6_{tiny,small,medium}_{det,rec}_onnx`（HuggingFace）

| 模型 | 体积 | 说明 |
|---|---|---|
| `PP-OCRv6_tiny_det` | 1.7 MB | 极小 |
| `PP-OCRv6_tiny_rec` | 4.3 MB | 字典 6904（**偏移需复核**） |
| `PP-OCRv6_small_det` | 9.4 MB | 轻量档 |
| `PP-OCRv6_small_rec` | 20.2 MB | 字典 18708 |
| `PP-OCRv6_medium_det` | 59.2 MB (FP32) | **= 本仓 `official_models/onnx_fp32/` 那份，62032837 B 逐字节吻合** |
| `PP-OCRv6_medium_rec` | 73.0 MB (FP32) | 字典 18708 |

> 本仓分发的是 FP16 量化版（31 MB / 38 MB），约为 FP32 的一半。

**这直接支撑一个"快速/精确"双档设置**：`--ocr-perf` 工具（`OcrPerfBench.cs:4`）**已经预留了 `--size small|medium` 参数**，说明这个方向早就在设计里。

### 3.2 语言专用 rec（PP-OCRv5 mobile，全部 Apache-2.0）

来源：`PaddlePaddle/{lang}_PP-OCRv5_mobile_rec_onnx`

| 语言 | 体积 | 备注 |
|---|---|---|
| **korean** | **12.8 MB** | 字典 11945，**含全部 11172 个谚文音节** ✅ 实测可用 |
| latin | 7.7 MB | |
| cyrillic | 7.7 MB | |
| arabic | 7.6 MB | |
| devanagari | 7.5 MB | |
| el (希腊) | 7.4 MB | |
| eslav | 7.5 MB | |
| en | 7.5 MB | |
| ta / te / th | 7.5 MB | 泰米尔/泰卢固/泰 |

**日文**：PP-OCRv5 无独立 `japan` rec（日文由 `PP-OCRv6` 统一字典的 180 个假名覆盖）。当前"日文需字高 ≥24px"的问题应走**小字召回**优化，不是换模型。

### 3.2b 第二条分发渠道：RapidOCR（ModelScope，**国内可直连**）

本仓 `download_models.ps1` 头部已把 rapidocr 列为方案 1，且实测该渠道**无需代理**即可访问，对本项目的中国用户场景很关键。

`RapidAI/RapidOCR` `v3.9.2`，`onnx/` 目录下：

| 路径 | 体积 |
|---|---|
| `PP-OCRv6/det/PP-OCRv6_det_tiny.onnx` | 1,829,618 B |
| `PP-OCRv6/det/PP-OCRv6_det_small.onnx` | 9,929,594 B |
| `PP-OCRv6/det/PP-OCRv6_det_medium.onnx` | 62,119,454 B |
| `PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx` | 4,489,813 B |
| `PP-OCRv6/rec/PP-OCRv6_rec_small.onnx` | 21,234,383 B |
| `PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx` | 76,629,984 B |
| `PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile.onnx` | 13,488,748 B |
| `PP-OCRv5/rec/{latin,cyrillic,arabic,devanagari,el,eslav,en,ta,te,th}_PP-OCRv5_rec_mobile.onnx` | 7.8–8.1 MB 各 |
| `PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx` | 16,631,306 B |
| `PP-OCRv5/rec/ch_PP-OCRv5_rec_server.onnx` | 84,577,022 B |

**与 HuggingFace 的体积差异**（如 medium det 62,119,454 vs 62,032,837）说明这是**独立导出的产物**，不是同一文件的镜像。因此**换源时必须重新固化 SHA256**——本仓的校验体系（`models-manifest.json` + `download_models.ps1` + `Publish.ps1`）正是为此设计的，不能沿用旧哈希。

**推荐**：内置模型继续用 HuggingFace 官方源（与现有哈希一致）；**按需下载的语言模型走 RapidOCR/ModelScope 镜像**，对国内用户更可靠。

### 3.3 关键实测：韩文 0% → 100%

同一渲染管线、同一预处理（BGR、H=48、mean/std=0.5）、同一 `dict[d-1]` 解码：

| 输入文本 | `korean_PP-OCRv5_mobile_rec` | 当前 `PP-OCRv6_medium_rec` |
|---|---|---|
| `안녕하세요 세계` | **`안녕하세요세계`** ✅ | `` （空） ❌ |
| `한국어 OCR 테스트` | **`한국어OCR테스트`** ✅ | `` （空） ❌ |
| `Hello World` | `HelloWorld` ✅ | `HelloWorld` ✅ |

（空格被 CTC blank 吞掉是两边共有的既有行为，与本次选型无关。）

**DirectML 也验证通过**（本地实机执行，非推断）：

| 模型 | CPU | DirectML | 结论 |
|---|---|---|---|
| korean rec（12.8 MB） | 10.9 ms/次 | **9.1 ms/次**（20 次均值） | 两者都可用 |
| PP-OCRv6 medium rec FP32 | 39.1 ms/次 | **6.6 ms/次** | 预期行为，DML 优势明显 |

DML 下 `s.Run` 正常返回 `[1, 40, 11947]`，无需额外算子适配。Session 初始化 127–260 ms，与现有引擎同量级。

> ⚠ **一个战略风险（由并行的模型调研独立发现，值得记录）**：微软 ORT 官方文档已声明 **DirectML 进入 sustained engineering（维护模式）**，新功能开发转向 **WinML**。本仓 `OnnxOcrEngine` 的 GPU 路径正是 DML（`OcrExecutionProvider.DirectML`，`OnnxOcrEngine.cs:270`）。WinML 暴露**相同的 ORT API**，因此迁移成本低，但应作为**规划项**列入 roadmap，而不是等到 DML 停止更新时才发现。

详细评估见下节 §3.4（已实测验证，非推断）。

### 3.4 WinML 迁移评估（本次实测）

#### 3.4.1 官方原文（已核实，非二手转述）

微软 ORT 官方 DirectML 文档原文：

> **Note: DirectML is in sustained engineering.** DirectML continues to be supported, but new feature development has moved to **WinML** for Windows-based ONNX Runtime deployments. WinML provides the same ONNX Runtime APIs while dynamically selecting the best execution provider based on your hardware.

来源：[ORT DirectML Execution Provider](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)

**关键澄清**：`sustained engineering` = **继续支持、修复 bug、不新增功能**。**不是弃用（deprecated），不是停止服务**。DML 目前锁在 DirectML 1.15.2 + ONNX opset 20。因此这是**规划项**，不是紧急项。

#### 3.4.2 WinML 的收益（逐条对照本仓实测）

| 收益 | 本仓实测结论 |
|---|---|
| **同一套 ORT API，代码零改动** | ✅ **实测证实**：WinML 2.4.89 直接跑通本仓随包的 `PP-OCRv6_medium_rec.onnx`，`new InferenceSession(...)`、`InputMetadata`、`Run()` **全部原样可用**，输出 `C=18710` 与 NuGet 1.24.4 一致。官方称 "Same ONNX APIs — no changes to your existing ONNX Runtime code" |
| **运行时可随 Windows Update 更新** | ✅ 仅**框架依赖（framework-dependent）**模式可得。本仓当前是 self-contained → **拿不到**这条 |
| **不随包分发 EP，动态获取厂商 EP** | ✅ 可 `ExecutionProviderCatalog` 按需下载 QNN/OpenVINO/NvTensorRtRtx/MIGraphX/VitisAI → **拿到 NPU 加速**（当前 DML 完全用不上 NPU） |
| **更小的应用体积** | ⚠ **本仓几乎中性**：现装 `onnxruntime.dll` 20.66 MB + `DirectML.dll` 17.83 MB + 托管层 0.22 MB ≈ **38.7 MB**；WinML self-contained 实测 `onnxruntime.dll` 22.02 MB + `DirectML.dll` 17.83 MB + `Microsoft.Windows.AI.MachineLearning.dll` 0.93 MB ≈ **40.8 MB**。**差约 +2 MB**（注意 `DirectML.dll` 是同一份 17.83 MB） |
| **更新的 ORT** | ✅ 实测：WinML 2.4.89 内含 **ORT 1.27.20260711**，而本仓 NuGet 锁在 **1.24.4**（1.24.20260504）。**领先 3 个小版本** |
| **芯片厂商优化 EP（性能）** | ✅ NVIDIA 走 `NvTensorRtRtx`、Intel 走 `OpenVINO` 等，官方称"on par with dedicated SDKs"。但**需 Win11 24H2+**且逐硬件验证 |

#### 3.4.3 兼容性清单（对照本仓真实约束）

| 检查项 | 要求 | 本仓实际 | 结论 |
|---|---|---|---|
| 目标框架 | `net8.0-windows10.0.18362.0`+ | **`net11.0-windows10.0.26100.0`** | ✅ 远超 |
| .NET 版本 | .NET 8+（.NET 6 无 `Microsoft.ML.OnnxRuntime` API） | **.NET 11** | ✅ |
| 硬件 EP（NPU/厂商 GPU） | Win11 **24H2 (build 26100)**+ | TFM 即 `26100.0` | ✅ **完全吻合** |
| 打包形态 | `WindowsPackageType=None`（非打包）受支持 | **`None`**（`App.csproj:14`） | ✅ |
| 自包含 | `WindowsAppSDKSelfContained=true` | **已是 `true`**（`App.csproj:15`） | ✅ **已就绪** |
| Windows App SDK | 1.8.1+（主包方式） | **2.4.0** | ✅ |
| 架构 | x64 / ARM64 | win-x64 | ✅ |
| CPU/GPU(DirectML) EP | 所有受支持 Windows 版本 | — | ✅ **无需 24H2** |

**结论：本仓的 TFM 与部署配置恰好已经满足 WinML 的全部前置条件**，唯一需要决策的是 self-contained vs framework-dependent。

#### 3.4.4 实测风险：一个未能确定性复现的推理失败

在 WinML 的 ORT 1.27 上，我**观察到一次** det 模型 `Add.154` 广播失败：

```
Non-zero status code returned while running Add node. Name:'Add.154'
BroadcastIterator::Append axis == 1 || axis == largest was false.
Attempting to broadcast an axis by a dimension other than 1. 3 by 4
```

但它**无法确定性复现**——后续验证：

| 测试 | 结果 |
|---|---|
| det 单独、9 种输入尺寸（方图 + 各种宽高比） | **全部 OK**（含失败时的 384×896） |
| det 重复 15 次（不带 workaround） | **15/15 OK** |
| det 重复 15 次（带 workaround） | **15/15 OK** |
| rec→det 复现原始调用序列 | **OK** |
| rec 输出（不带 workaround） | `max=1.0`，**无全零症状** |

**诚实的结论**：这是**一次未被复现的瞬时失败**，最可能来自当次进程状态（该次运行紧接在重建之后、以 `--no-build` 启动，存在 `onnxruntime.dll` 被替换/占用的竞态），而非模型与 WinML 的确定性不兼容。**但我无法排除它是 WinML ORT 1.27 的稳定性问题**——因此记为**待验证风险**，而非"已确认无问题"。

⚠ **这也意味着一个重要的实践建议**：本仓现有的 `disable_simplified_layer_norm_fusion` workaround（`OnnxOcrEngine.cs:286`）**在 WinML 的 ORT 1.27 上可能已不需要**（rec 在该版本不再触发故障、det 也未复现）。但该调用被 `catch {}` 静默包裹（`:288`），**升版本后无法从日志判断它是否还在起作用**。这是升级到 WinML/新 ORT 时应补的断言。

#### 3.4.5 建议

**不建议现在迁移。** 理由：

1. **收益当前不落地**：本仓已经是 self-contained（拿不到自动更新）、体积几乎中性（+2 MB）、TFM 已达标。真正的增量收益是**NPU 加速**和**免维护 EP**，而 OCR 模型（rec 6.6–10.9 ms/次）**远未到需要 NPU 的程度**——瓶颈在 det 与 IO，不在 rec 吞吐。
2. **DML 仍在被支持**：`sustained engineering` ≠ 弃用。没有安全或功能缺口。
3. **存在未解释的瞬时推理失败**（§3.4.4），在 OCR 这种"失败即用户可见乱码/空结果"的场景，稳定性优先。

**建议做法**：
- **短期**：保持 DML，但把 DML 的 `sustained engineering` 状态记入 roadmap（与已有的 `docs/roadmap-v0.4.md` 对齐）。
- **中期（推荐动作）**：做一次**纯版本升级**——把 `Microsoft.ML.OnnxRuntime.DirectML` 从 1.24.4 提到接近 1.27，验证 `disable_simplified_layer_norm_fusion` 是否仍必需（顺手把 `catch {}` 改为带日志/断言）。这一步**风险极低**且独立于 WinML 决策。
- **触发迁移的条件**：需要 NPU 加速（如引入更大的模型）、或 DML 出现安全/兼容问题、或需要厂商优化 EP。届时迁移真正的工作量在**打包模式切换**（self-contained → framework-dependent 需要用户机器上有 Windows App SDK runtime，与"便携绿色包"的定位冲突），而非代码。

---

## 4. 顺带发现的一个真实调优机会：det 归一化参数与官方不符

官方 `PP-OCRv6_medium_det` 的 `inference.yml` 写的是 **ImageNet 归一化**：

```yaml
NormalizeImage:
  mean: [0.485, 0.456, 0.406]
  scale: 1./255.
  std:  [0.229, 0.224, 0.225]
```

而本仓 `OcrModelConfig.Default`（`OnnxOcrEngine.cs:54-55`）写的是 **`DetMean = DetStd = [0.5, 0.5, 0.5]`**。

实测两种归一化对 det 概率图的影响（同一张 251×63 渲染图，det 输入 1504×384）：

| 归一化 | max | avg | >0.3 前景占比 |
|---|---|---|---|
| `0.5/0.5`（当前） | 0.9961 | 0.1455 | 15.86% |
| ImageNet（官方） | 1.0000 | 0.2042 | **20.86%** |

**ImageNet 归一化的前景响应高约 31%**。DBNet 对这种尺度差异有容忍度（两者都能检出文字，所以不是致命 bug），但更强的前景响应通常意味着**小字/低对比度边缘更容易越过阈值**——正对应已知的"9–11px UI 小字召回弱"。

⚠ 这是**假设而非结论**：需要按本仓既有方法（`--ocr-noise` 查误检、`--ocr-bench-real` 292 行语料、seed 1409）实测召回与误检的净变化后再决定。**不要未经基准就改**，`DetMaxBoxAspect=400`/`DetSplitBoxAspect=90` 那组值就是被实测推翻过原值的（见 `OnnxOcrEngine.cs:58-77`）。

---

## 5. 建议（按性价比排序）

### 建议 1（强烈推荐）：修韩文 —— 加装 12.8 MB 韩文 rec

**收益**：韩文 0% → 100%，这是 `docs/ocr-accuracy-analysis.md` 里占**总失败数 93%** 的单一类别。
**成本**：12.8 MB 体积，一次性 ~4–8h 工程。
**做法**：不要覆盖主 rec。按需懒加载第二个 rec 会话 + 第二个字典，仅在语言为韩文时启用。

**必须同时处理的坑**（源码已定位）：
1. **字典偏移量参数化**（见 §2 层次 B）——这是防静默乱码的关键。
2. `IsResultTrustworthy`（`MultiOcrService.cs:370-376`）里"韩文结果不含谚文即判失败"的启发式：**有了真韩文模型后，这条会误杀正确结果并触发无谓的 Windows OCR 往返**。需按"是否已加载韩文模型"条件化。
3. `_recTensor`（`OnnxOcrEngine.cs:122`）按 `_config.RecImgMaxW` 定型且**跨会话复用**——两个 rec 模型若 `RecImgMaxW` 不同会串味。
4. `_inferGate`（`:129`）与 `_tensorLock`（`:119`）目前都假设**单会话**，加第二会话要重新审视锁的粒度。
5. 保留现有 Windows OCR 兜底（对未装韩文模型的用户仍需它）。

**关于体积**：可以**不内置**，改为首次选韩文时按需下载（`download_models.ps1` 已有 SHA256 校验框架可复用）。SHA256（本次实测）：
```
korean_PP-OCRv5_mobile_rec onnx : 92F0B7785E64FC9090106A241CF4C1EB97472824558272751B88A2A4476D3A08
```

### 建议 2（推荐）：把模型目录/档位做成用户可选项

**收益**：最高性价比的架构改动。当前 `ResolveModelDir()`（`OnnxOcrEngine.cs:221-238`）**不接受任何参数**，`%LOCALAPPDATA%\TrueToneCap\onnx_models` 是唯一"用户可影响"的缝隙，而且是隐式约定、**只有两个内置目录都找不到模型时才生效**。
**做法**：新增 `OcrModelDir` 设置项 + 目录选择器；`LoadModels()` 改为从 `_config` 读文件名，而不是写死的 `DetFallbackNames`。
**坑**：`MultiOcrService.Initialize` 的 `_initialized` 是**一次性闩锁**（`:61`），改设置必须重启或新增 `Reinitialize()`（要处理 `_engines` 释放与并发）；新增设置字段还要登记进 `AppJsonContext.cs`（AOT 源生成 JSON）并考虑 `SettingsService.FitTag` 白名单。

### 建议 3（推荐）：加"快速/精确"档位

用官方 tiny/small/medium 三档（§3.1）做设置项。`OcrPerfBench --size small|medium` 已存在，基准设施**现成**。适合低配机或"连续截图识字"场景。

### 建议 4（可选，需实测）：det 归一化对齐官方 ImageNet 值

见 §4。**必须先跑基准再改**。

### 建议 5（不建议）：引入 VLM OCR

见 §2 层次 C。体积与运行时依赖是硬伤，且本仓已有 Vision 模式覆盖这条路。

---

## 6. 落地前建议补齐的工程保障

本仓 `src/TrueToneCap.Tools/` 已有 **21 个 OCR 诊断/基准 CLI**（`FormatBench.cs:12-179` 分派），设施相当完整。但换模型前建议补三件事：

1. **字典/偏移量断言**（最重要）。加载时校验 `numClasses - dict.Length`，不匹配就**明确失败**而不是静默出乱码。`--ocr-rec`（`OcrRecDiag.cs`）目前只**打印** `numClasses`（`:83-84`），应升级为断言。
2. **自动发现模型**。所有诊断工具都写死 `Path.Combine(modelDir, "PP-OCRv6_medium_rec.onnx")`（如 `OcrRecDiag.cs:17-20`），跑候选模型必须先改工具。应复用 `FindFirstExisting`。
3. **在基准输出里记录模型名 + SHA256**。现有 `OcrAccBench` 标注了**引擎**（`:46`）却没标注**模型文件**——注释里那些 91.51%/91.48% 的基线（`OnnxOcrEngine.cs:459-460`）**只对当前 medium 成立**，换模型后全部失效，且从输出里看不出来。

---

## 7. 复现方式

本次结论均可复现：

```powershell
# 1. 模型体积/存在性（无需下载，HEAD 请求）
Invoke-WebRequest "https://huggingface.co/PaddlePaddle/korean_PP-OCRv5_mobile_rec_onnx/resolve/main/inference.onnx" -Method Head

# 2. 官方元数据（含 dict 与归一化参数）
Invoke-WebRequest "https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/resolve/main/inference.yml"

# 3. 输入/输出签名 + 类目数
#    用 Microsoft.ML.OnnxRuntime 1.24.4 读 InputMetadata/OutputMetadata
#    注意：加载随包 FP16 rec 必须设 optimization.disable_simplified_layer_norm_fusion=1
```

**关键文件定位**：

| 关注点 | 位置 |
|---|---|
| 候选文件名（写死） | `OnnxOcrEngine.cs:81-103` |
| 模型配置默认值 | `OnnxOcrEngine.cs:46-77` |
| 模型加载 | `OnnxOcrEngine.cs:170-213` |
| 目录解析（无参数） | `OnnxOcrEngine.cs:221-238` |
| SessionOptions / LayerNorm workaround | `OnnxOcrEngine.cs:262-316` |
| **CTC 解码 / blank=0 / dict[d-1]** | `OnnxOcrEngine.cs:789-836` |
| 韩文可信度启发式 | `MultiOcrService.cs:370-395` |
| 引擎注册（3 档） | `MultiOcrService.cs:57-116` |
| Windows OCR 双 pass | `OcrService.cs:15-58` |
| 发布期模型名 + SHA 校验 | `Publish.ps1:187-191, 240-267` |
| 设置枚举白名单 | `SettingsService.cs:432` |
| UI 硬编码"18708 字/韩文不支持" | `MainWindow.OcrSettings.cs:34` |
