# OCR 行切分 / 中文空格修复计划

- 日期：2026-11-27
- 基线：v0.3.20，`main` / `ec440aa` + `f7a764b`
- 范围：只改 `C:\PLAN\TrueToneCap`；不触碰其他仓库
- 状态：**已实施并验收（2026-11-27）**；验收记录见 `docs/ocr-line-composer-acceptance-2026-11-27.md`

## 1. 结论先行

这次要修的是 **OCR 后处理布局**，不是识别模型本身：

1. `MergeDetLines()` 把同一视觉行的多个 det 框用 `string.Join(" ", ...)` 拼接，导致“藏 些 检 测”这类汉字之间被强行插入空格。
2. `OcrResult.Text` 仍按原始 det 顺序生成，而 `Lines` 已经过行合并；复制全文和部分下游消费者会拿到两套不同顺序。
3. 行聚类只看“中心点是否落在簇的垂直范围内”，遇到高矮不一的框会把相邻行/碎片串成一行，或把同一行的小碎片（如孤立的 `a`、`.`）留成独立行。
4. `one`、`on` 这类词被排到行尾的个例，不能只用“按 X 排序”解释；必须先抓原始框坐标确认是聚类边界、长行拆分还是坐标映射问题，不能盲改阈值。

**不在这轮修**：`EngIish`、`DlspIay`、`rosoft` 这类字符级误识别。它们属于 rec 模型/字典覆盖与图像质量，换后处理规则修不了；本轮只保证“框怎么合、字怎么拼、行怎么排”正确。

### 1.1 实施中新增的根因（2026-11-27）

真实图像验收发现，英文整行会被识别成 `ThisisonelongEnglishsentence,`。根因不在 composer，而在 CTC 解码：PP-OCRv6 rec 输出 **18710 类 = blank(0) + 18708 字典字符 + 1 个尾随空格类**；旧解码把索引 `> dict.Length` 的尾随类直接丢弃。现在把 `dict.Length + 1` 映射为空格，英文词距恢复；汉字间空格仍由 `NormalizeFragment` 清理。该映射已抽成 `OnnxOcrEngine.MapDecodedIndices` 并补单测。

## 2. 现有链路与证据

### 2.1 代码事实

| 位置 | 现状 | 影响 |
|---|---|---|
| `OnnxOcrEngine.cs:495` | `Text = string.Join("\n", allText)`，`allText` 是原始 det 顺序 | `Text` 与 `Lines` 可能不同序；复制全文走 `_ocr.Text` 时顺序错 |
| `OnnxOcrEngine.cs:530-563` | `MergeDetLines` 按中心点落簇，簇内按 X 排序，文本用 `" "` 连接 | 汉字之间被插空格；高矮框会串行；同簇无法表达“列” |
| `OnnxOcrEngine.cs:1250-1282` | 长行按无墨列拆成多个 `IsFragment` 子框送 rec | 识别后必须按原视觉行重新拼；不拼就会多行/多空格 |
| `OcrPreviewWindow.xaml.cs:485-490` | 无译文时直接返回 `_ocr.Text` | 复制全文与预览 `Lines` 可能不一致 |
| `TranslationService.cs:2744-2780` | 已有 `JoinWrappedTranslationLines` / `NeedsSpaceBetween` 的 CJK 规则 | 可抽成共享纯函数，避免两套语言规则漂移 |

### 2.2 验收证据

`docs/translation-paragraph-acceptance-2026-11-27.md` 的真实/受控用例已经给出两类硬证据：

- 中文行出现 `藏 些 检 测 失 败 隐 检 测 了 一` 这种逐字空格；JSON 里 `ocr[]` 来自 `OcrResult.Lines`，不是识别模型直接吐空格。
- 受控 3 行英文段落被切成 4 行，末尾多出一个 `a`；真实截图里还有 `one`、`on` 被排到行尾的个例。

### 2.3 先验证、再改代码的边界

`MergeDetLines` 目前没有原始 det 框测试。第一步必须加一个 **只读诊断导出**（临时/测试用，不默认开启）：

- 对两张真实截图和 `controlled-paragraph-only.png` 导出：`Text / X / Y / Width / Height / IsFragment / 簇编号 / 与前一框的 gap`；
- 判定每个异常属于哪一类：
  - A. 同一行多框 → 空格问题；
  - B. 不同行被中心点串簇 → 行切分/词序问题；
  - C. 长行拆分后未回拼 → 多行问题；
  - D. det 框坐标本身错 → 检测坐标映射问题，不能靠 composer 修；
  - E. rec 字符错 → 本轮不修。
- 只有 A/B/C 进入 composer 修复；D 单独开检测映射修复；E 记录为模型层残余。

## 3. 修复设计

### 3.1 新增 `OcrLineComposer`（Core，纯函数）

新增 `src/TrueToneCap.Core/Services/OcrLineComposer.cs`，输入原始 `OcrLine` 碎片，输出视觉行：

1. **碎片展开**：把每个 `OcrLine.Words` 展开为带坐标的 `Fragment`；没有 `Words` 的用行框兜底。
2. **行聚类**：按垂直重叠率而不是“中心点落范围”：
   - `overlap = min(bottom) - max(top)`；
   - 仅当 `overlap >= 0.60 * min(height)` 且中心差 `<= 0.60 * max(height)` 才归入同一行；
   - 行锚点用中位 top/bottom/height，不用整簇并集，避免一个高框把下一行吞进来。
3. **行内排序与分段**：同一行内按 X 升序；相邻框 `gap = next.X - (prev.X + prev.Width)`。
   - 默认 **不拆列**，仍保持“一行一块”，避免把 UI 表格行拆成大量词芯片；
   - 当 `gap > max(2.5 * 行高中位数, 40px)` 时，才切成独立视觉段；这是“明显分栏/独立标签”的安全阀，不是常规词距。
4. **文本拼接**：用脚本感知的 `JoinFragments`：
   - 汉字/假名 ↔ 汉字/假名：**永不插空格**；
   - 中文标点前、开括号后、`- / \ @ _` 后：不插；
   - 英文↔英文：`gap >= 0.25 * 行高` 才插一个空格，`gap` 很小视为被拆开的单词，直接拼；
   - 中英交界：`gap >= 0.5 * 行高` 才插空格，否则直接拼；
   - 先 `Trim`，保留片段内部已有的英文空格，压缩连续空格；
   - 额外把“汉字 + 空格 + 汉字”“汉字 + 空格 + 中文标点”归一化掉。
5. **孤立碎片**：一行只有一个 ≤2 字符片段，且与相邻行的垂直重叠 ≥0.5、水平 gap ≤1.5 行高时，并入相邻行；否则保留，避免误吞真实的单字按钮/标点。
6. **Text 一致性**：`result.Text = string.Join("\n", result.Lines.Select(l => l.Text))`，不再用 `allText`。

### 3.2 `MergeDetLines` 的处置

保留 `MergeDetLines` 作为兼容入口，内部委托 `OcrLineComposer.Compose`；等所有调用点迁移并跑完回归后再删旧实现。这样不破坏 Test 的 internal 调用，也不让旧逻辑和新的 composer 同时存在两套行为。

### 3.3 UI 侧

- `OcrPreviewWindow.CurrentFullText()` 在无译文时改为 `string.Join("\n", _ocr.Lines.Select(l => l.Text))`，不再依赖 `OcrResult.Text`。
- 预览覆盖仍用 `OcrLine.Words`；composer 必须保留每个 fragment 的原始坐标，行框取并集，避免覆盖块错位。
- 翻译段落分组不需要改：它本来就用 `Lines`；空格和行序修正后会自然受益。

### 3.4 配置/开关

默认启用新 composer。为了出现回归时能快速回退，加一个内部环境变量：

```text
TTC_OCR_LEGACY_LINE_MERGE=1  → 走旧 MergeDetLines
```

仅用于诊断/回退，不暴露到设置页；验收通过后下一版删除。

## 4. 测试矩阵

### 4.1 纯单元（不依赖 GPU/模型）

新增 `OcrLineComposerTests.cs`，挂到现有 `--ocr-tests` 套件，不新增套件参数：

| 用例 | 输入 | 期望 |
|---|---|---|
| 汉字逐字框 | 8 个单字框，同一基线 | `藏些检测失败隐检测了一`，无汉字间空格 |
| 英文拆词 | `En` + `glish`，gap≈0 | `English`，不插空格 |
| 英文词距 | `This` + `is`，gap≈0.4H | `This is`，正好一个空格 |
| 中文标点 | `你好` + `，` | `你好，` |
| 两行不串 | y=0/30，h=20 | 两行，不合并 |
| 高矮框不串 | 高框 h=60 跨两行 | 不得把下一行吞入 |
| 孤立 `a` | 段落行 + 基线附近单字符 | 不新增第 4 行 |
| 明显分栏 | 两个段 gap=3H | 切成两段 |
| Text 不变式 | 任意输入 | `Text == join(Lines)` |
| 空/单框 | 空列表/单框 | 原样、不抛异常 |

### 4.2 真实图像回归

用现有受控图与两张真实截图：

- `controlled-paragraph-only.png`：3 行，词序 `This is one long English sentence,` / `which continues on the next visual line` / `and ends with a clear conclusion.`；允许 `EngIish` 之类字符错，但不得把 `one`/`on` 排到行尾，不得多出孤立 `a` 行。
- `controlled-paragraph.png`：段落 3 行 + 4 个 UI 标签，标签不并入段落；段落组 `groups=1 rows=3` 保持。
- 真实截图：`CJK + 空白 + CJK` 正则计数为 0；`Lines` 与 `Text` 完全同序；覆盖框不重叠、不越界。
- 翻译质量：同样输入下 `PartialCount` 不得高于基线；状态栏不得因空格被误判为“半译”。

### 4.3 性能/资源

- composer 只做 O(n log n) 排序 + 线性扫描，`n` 为 det 框数（通常 <50）；目标 <1ms。
- 不新增 GPU/ONNX 会话、不改变 4K 帧池；OCR 总耗时不得比基线增加 >10%。

## 5. 实施顺序

1. **P0 诊断**：加只读框导出，抓 3 组样本，确认 A/B/C/D/E 分类；确定 `gap` 阈值初值。
2. **P1 文本拼接**：新增 `JoinFragments` + 单测；只改 `MergeDetLines` 的拼接，不动聚类。先消掉汉字空格和 Text 不一致。
3. **P2 行聚类**：换成垂直重叠率 + 中位锚点；补两行/高矮框/孤立碎片用例。
4. **P3 接入 UI**：`CurrentFullText` 改 Lines；确认覆盖几何和翻译行数不变。
5. **P4 真实验收**：跑受控图 + 两张真实截图 + 预览窗手工点击；记录前后行数、空格数、状态栏。
6. **P5 收口**：更新验收文档与 CHANGELOG；Release 构建 + `--ocr-tests` + 全量套件；通过后再考虑 0.3.21 打包。

预计：实现 4–6h，真机验收 1–2h；不含模型字符纠错。

## 6. 风险与回退

| 风险 | 处理 |
|---|---|
| 过宽的 gap 阈值把正常词距当成分栏 | 默认 `2.5H/40px` 双阈值，只用真实样本校准；先不拆列 |
| 过窄的阈值让 UI 表格多列粘成一行 | 保留分段安全阀；验收看行数与覆盖块 |
| 单字符合并误吞真实标签 | 只合并 ≤2 字符且垂直重叠 ≥0.5、水平 gap ≤1.5H 的碎片；不满足就保留 |
| 汉字无空格规则影响中英混排 | 中英交界用 gap 判定，只有紧邻才直接拼；英文内部保留词距 |
| 旧预览覆盖依赖行框 | composer 保留每个 fragment 的 `Words`，行框只做并集 |
| 字符级误识别仍存在 | 明确列为模型层残余，不用规则硬纠 |

回退：`TTC_OCR_LEGACY_LINE_MERGE=1` 立即回到旧合并；代码级回退只需改 `OnnxOcrEngine` 一行调用。

## 7. 验收判据（可执行）

- [ ] 单测新增用例全过；`--ocr-tests` 0 失败。
- [ ] `OcrResult.Text` 与 `Lines` 文本逐行相等（ONNX 路径）。
- [ ] 受控 3 行段落输出 3 行，无孤立 `a` 行；`one`/`on` 回到正确词位。
- [ ] 真实截图 `CJK\s+CJK` 计数为 0；逐字空格消失。
- [ ] 预览覆盖框仍然逐行贴合，点击复制全文与可见行一致。
- [ ] 翻译段落组仍为 1 组 3 行；状态栏 ✅/⚠ 判据不被空格污染。
- [ ] 全量 8 套件 0 失败；Release 0 警告 0 错误。

## 8. 明确不做

- 不换 rec 模型、不改字典、不改 det 阈值/放大倍数。
- 不做字符级纠错（`I/l`、`rn/m`、中文错字）。
- 不做多栏文章的自动阅读顺序（本轮只处理 UI 行；真要做另开列检测）。
- 不把 `PartialCount` 的模型误识别算到本轮修复里；它需要单独评估。
