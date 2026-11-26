# OCR 行合并与中文空格修复验收

- 日期：2026-11-27
- 基线：`main` / `8a5834a`（v0.3.20 发布后）
- 范围：只改 `C:\PLAN\TrueToneCap`；未触碰其他仓库
- 状态：**已实现、已单测、已真实图像验收、已提交 Git（`3603275` + 本记录所在提交）；未版本化**

## 1. 结论

本轮修的是 **OCR 后处理与 CTC 解码**，不是识别模型：

1. `OcrLineComposer` 按垂直重叠率聚类视觉行，替换旧的“中心点落簇”；旧实现会把不同窗口/列的独立控件按中心点串成一行，也会把中文逐字框用空格连接。
2. 行内按 X 排序；只有水平 gap 超过 `max(2.5×行高, 40px)` 才拆成独立视觉段。常规英文词距不再被拆成独立行。
3. 文本拼接脚本感知：汉字/假名之间不插空格；中文标点、开括号、连字符旁不插空格；英文按 `0.25H` 词距、中英交界按 `0.5H` 判定。
4. `OcrResult.Text` 改为由合并后的 `Lines` 生成，`Text` 与 `Lines` 同序；预览复制全文也优先走 `Lines`。
5. 额外发现并修复 **CTC 尾随空格类被丢弃**：PP-OCRv6 rec 输出 18710 类 = blank(0) + 18708 字典字符 + 1 个空格类。旧解码把最后一类直接过滤，导致英文整行变成 `ThisisonelongEnglish…`。现在最后一类映射为空格，英文词距恢复，汉字间仍由 composer 归一化为 0 空格。

## 2. 代码改动

| 文件 | 改动 |
|---|---|
| `src/TrueToneCap.Core/Services/OcrLineComposer.cs` | 新增；视觉行聚类、分段、脚本感知拼接、孤立碎片回收、`Text` 构建 |
| `src/TrueToneCap.Core/Services/OnnxOcrEngine.cs` | `MergeDetLines` 委托 composer；保留 `MergeDetLinesLegacy` 回退；`Text` 由 `Lines` 生成；CTC 尾随空格类映射；`TTC_OCR_DUMP_BOXES=1` 只读诊断 |
| `src/TrueToneCap.Core/Services/OcrService.cs` | Windows OCR 混合语言路径改用 `OcrLineComposer.JoinWords` 分行拼接，中文逐词不插空格；行框取词框并集 |
| `src/TrueToneCap.App/OcrPreviewWindow.xaml.cs` | `CurrentFullText()` 无译文时优先从 `_ocr.Lines` 拼全文，避免两套顺序 |
| `src/TrueToneCap.Test/OcrLineComposerTests.cs` | 新增 19 条 composer 断言 |
| `src/TrueToneCap.Test/OcrDecodeMappingTests.cs` | 新增 5 条 CTC 类别映射断言，锁住尾随空格类 |
| `src/TrueToneCap.Test/OcrWindowsLineTests.cs` | 新增 4 条 Windows 混合行断言：中文不插空格、英文词距、行框并集、空列表安全 |
| `src/TrueToneCap.Test/OcrServiceTests.cs` | 把上述两组测试挂进现有 `--ocr-tests`，不新增套件参数 |

回退开关：

```text
TTC_OCR_LEGACY_LINE_MERGE=1  # 回到旧 MergeDetLines
TTC_OCR_DUMP_BOXES=1        # 导出合并前 det 框到 %TEMP%\ttc-ocr-boxes-*.tsv
```

## 3. 真实图像验收

验收宿主：`artifacts/ocr-acceptance/Probe.csproj`（引用当前 Core；真实 PP-OCRv6 模型；仅做 OCR，不调 LLM）。日志：`artifacts/ocr-acceptance/probe-acceptance.log` 与 `probe-acceptance-legacy.log`。

### 3.1 受控英文三行段落

依据：`controlled-paragraph-only.png`，1700×620。

```text
[1] This is one long English sentence,
[2] which continues on the next visual line
[3] and ends with a clear conclusion.
TEXT_EQ_LINES=True CJK_SPACE=0
GEOMETRY_BAD=0 PARAGRAPH_GROUPS=1 GROUP_ROWS=3
```

- 旧实现同图也是 3 行，但单词之间无空格（`Thisisonelong…`）；本轮解码修复后恢复词距。
- 3 行、无孤立 `a` 行；`one` 在正确词位。
- 翻译段落组：1 组 3 行。

### 3.2 受控段落 + 4 个 UI 标签

依据：`controlled-paragraph.png`，1100×720。

```text
[1] This is a long English sentence that
[2] continues on the next visual line and
[3] ends with a clear conclusion.
[4] Settings
[5] Display
[6] Microsoft Store
[7] This PC
TEXT_EQ_LINES=True CJK_SPACE=0
GEOMETRY_BAD=0 PARAGRAPH_GROUPS=1 GROUP_ROWS=3
```

段落 3 行 + 4 个标签保持分离；标签没有被并入段落，段落组仍为 1 组 3 行。

### 3.3 中文设置页

依据：`settings-cn.png`，1242×831（早期中文设置页截图）。

| 指标 | 旧 `MergeDetLinesLegacy` | 新 `OcrLineComposer` |
|---|---|---|
| 行数 | 12 | 15 |
| `CJK\s+CJK` 计数 | 3 | **0** |
| `Text == join(Lines)` | **False** | **True** |
| 覆盖几何越界 | 0 | 0 |
| 段落组（多行组） | 0 | 0 |

旧实现的两类错误在新实现中消失：

```text
旧: 主题 浅色 / 语言 跟随系统 / 设置已保存 区域截图
新: 主题 / 浅色 / 语言 / 跟随系统 / 设置已保存 / 区域截图
```

新实现行数更多，是把旧实现误并的独立控件拆回独立行，不是同一段落被拆碎。

### 3.4 真实 4K 截图

依据：`TrueToneCap_20261127_025010178.jpg`，3840×2160（App 窗口 + 桌面 + IDE）。

| 指标 | 旧 `MergeDetLinesLegacy` | 新 `OcrLineComposer` |
|---|---|---|
| 行数 | 85 | 165 |
| `CJK\s+CJK` 计数 | 16 | **0** |
| `Text == join(Lines)` | **False** | **True** |
| 覆盖几何越界 | 0 | 0 |
| 多行段落组 | 0 | 14 组 / 31 行 |

旧实现第一行把顶部多个窗口标题栏串成：

```text
1 5 8 IDE Space 0 Space - CodeArts Agent 限时福利 C 0 o X 7 Y X
```

新实现按实际水平间隔分成独立行；英文词距恢复（`Space - CodeArts Agent`）。行数增加来自“拆开旧实现误并的不同窗口/列控件”，不是把同一段中文/英文重新切碎。

## 4. 自动化验证

- `--ocr-tests`：**40 通过 / 0 失败**（7 条预处理 + 19 条 composer + 5 条 LLM 清单 + 5 条解码映射 + 4 条 Windows 混合行）。
- 全量 `scripts/verify.ps1`：**8 个套件 2968 通过 / 0 失败**；2 处外部判据跳过（`icc_profile_id_check.py` 无带非零 ProfileID 的候选 ICC；`tiff_png_referee.py` 缺 numpy/tifffile/Pillow），未计为通过。
- Release 构建：**0 警告 / 0 错误**。
- 既有 `--usability-tests` 238 条断言通过，包含 `OcrPreviewWindow` 构造探针；覆盖块几何在真实图像验收中 0 越界。

## 5. 未修 / 已知残余

- `EngIish`、`DlspIay`、`rosoft` 等 rec 字符错误仍属模型/字典覆盖，不在本轮后处理范围。
- 多栏文章的自动阅读顺序未做；本轮只保证同一视觉行/独立控件不被错误串并。
- 真实 4K 截图里图标、任务栏小字符仍可能形成单字符行；它们没有足够水平邻接证据，归为独立 UI 元素，不做强制合并。
- 翻译仍可能把模型字符错误带入译文；本轮只保证行序、空格、段落组输入正确。

## 6. 验收判据

- [x] `OcrResult.Text` 与 `Lines` 逐行相等（ONNX 路径，真实图像 4/4 通过）。
- [x] 受控 3 行段落输出 3 行、无孤立 `a` 行、`one` 在正确词位。
- [x] 中文设置页 `CJK\s+CJK` 计数 0；旧实现的 3 处逐字空格消失。
- [x] 真实 4K 截图 `CJK\s+CJK` 计数 0。
- [x] 覆盖框全部在原图内、词框在行框内（`GEOMETRY_BAD=0`）。
- [x] 翻译段落组：受控段落 1 组 3 行；真实截图 14 组 31 行。
- [x] `--ocr-tests` 40 通过 / 0 失败；全量 2968 通过 / 0 失败；Release 0 警告 0 错误。
