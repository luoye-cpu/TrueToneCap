# 发包后审查发现（0.3.20 → 0.3.21 前）

日期：2026-11-27
审查范围：`ec440aa`（v0.3.20 源码基线）之后的 8 个提交，以及当前未提交修复。
原则：不沿用旧验收文档的 48 OCR / 2976 断言结论；所有结论以当前源码、当前构建和真实图像复跑为准。

## 1. 结论先行

### 1.1 当前源码/工作树

- Release 构建：**0 警告 / 0 错误**。
- `--ocr-tests`：**65 通过 / 0 失败**。
- 全量 `scripts/verify.ps1 -Configuration Release`：**8 套件 2993 通过 / 0 失败**；另有 **2 处外部判据未判**（ICC ProfileID 样本缺失、tiff/png 外部裁判缺 numpy/tifffile/Pillow），未计为通过。
- 真实图复跑（ONNX 默认路径）：
  - `controlled-paragraph-only.png`：3 行，`TEXT_EQ_LINES=True`，`CJK_SPACE=0`，`GEOMETRY_BAD=0`，段落组 1 组 3 行。
  - `settings-cn.png`：15 行，`TEXT_EQ_LINES=True`，`CJK_SPACE=0`，`GEOMETRY_BAD=0`，段落组 0。
  - `TrueToneCap_20261127_025010178.jpg`（4K）：165 行，`TEXT_EQ_LINES=True`，`CJK_SPACE=0`，`GEOMETRY_BAD=0`，段落组 14 组 31 行。
  - `TTC_OCR_LEGACY_LINE_MERGE=1`：12 行，`TEXT_EQ_LINES=True`，`CJK_SPACE=3`（旧行为，符合回退预期），`GEOMETRY_BAD=0`。

### 1.2 发布判断

- **源码候选：可以进入 0.3.21 版本的“版本坐标 + 重建”阶段。**
  修复已提交为 `7c9173b`；但这不等于“所有 OCR 问题已修复”：Windows OCR 的中文字符质量仍有明显错误，见 F8。
- **0.3.20 包：不可作为“已修复 OCR”的交付物。**
  当前 7z/MSIX 里的 `TrueToneCap.Core.dll` 是 OCR 修复前的旧二进制，见 F1。
- **0.3.21 包：尚未具备打包条件。**
  版本坐标仍是 0.3.20，CHANGELOG 顶部是 `[未发布]`，没有 `v0.3.20` tag，见 F7。

## 2. 阻断/高优先问题

### F1（P0，发布完整性）0.3.20 包里的 Core DLL 早于 OCR 修复

证据：

- 7z：`publish/TrueToneCap-v0.3.20-win-x64.7z` 内 `TrueToneCap.Core.dll` 为 **742,400 B**，时间 2026-11-27 03:14:30；7z 本身 03:21。
- MSIX：`publish/store-0.3.20/TrueToneCap.App_0.3.20.0_x64.msix` 内同一个 DLL 也是 **742,400 B**，时间 03:19。
- 当前源码 Release 构建的 Core DLL 为 **759,808 B**，包含 `OcrLineComposer` / `MapDecodedIndices`；旧包内 ASCII 扫描不到这两个符号。
- OCR 修复提交时间为 **05:01 之后**（`3603275` 05:01:19、`b609008` 05:11:00、`86aa93a` 05:26:48），晚于 03:19/03:21 的包。

影响：现在安装 0.3.20 的用户拿到的仍是旧 OCR 行为；`CHANGELOG` 的 `[未发布]` 却已经声称修复，存在“用户以为已修复、实际没有”的发布缺口。

结论：**必须重建 0.3.20/0.3.21 包；不能用现有 7z/MSIX 直接发。**

### F2（P1，已修复候选）Windows 混合路径固定 8px 行距会甩掉单词

证据：

- 受控图 `controlled-paragraph-only.png` 走 Windows OCR，修复前输出 4 行，第 4 行只有 `a`；真实词框里第三行的 `a` 与同 line 词基线差约 11px，超过固定 `lineGap=8px`。
- 修复为 `OcrService.GroupWindowsWords`（`src/TrueToneCap.Core/Services/OcrService.cs:256`）：按词高中位数 ×0.6、下限 8px 给行内容差，并用当前行 Y 中位数做锚点；受控图复跑 **4→3 行**，`TEXT_EQ_LINES=True`，`CJK_SPACE=0`，`GEOMETRY_BAD=0`。
- 回归测试：`OcrWindowsLineTests.BaselineVariation_SameLine`。

结论：修复候选有效；已提交 `7c9173b`。

### F3（P1，已修复候选）ASCII 闭引号后的英文词距被删除

证据：

- `NormalizeFragment` 的开/闭标点集合同时包含 ASCII `"` 和 `'`；`He said "hello" to me` 会被压成 `He said "hello"to me`，`'quoted' text` 同型。
- 修复：开/闭标点集合移除 ASCII 引号；新增 `CleanAsciiQuoteSpacing`（`OcrLineComposer.cs:325`），按两侧邻接形态判开/闭引号，保留闭引号后英文词距，处理 `5" display`、`'hello'to`、`It's` / `students'` / `O'Reilly's` 等边界。
- 真实 `quotes-test.png` 复跑 3 行，`He said "hello" to me.`、`She said 'goodbye' now.`、`5" display and 3' cable.` 全部正确；`TEXT_EQ_LINES=True`。

结论：修复候选有效；已提交 `7c9173b`。

### F4（P1，已修复候选）全角中文标点后仍会插空格

这是本轮在修 F3 之后继续查出的问题，旧 `CJK_SPACE` 指标（只统计“汉字-空白-汉字”）没有覆盖它。

证据：

- Windows OCR 中文设置页复跑输出 `像素； 未安`；`；` 是 U+FF1B，不在原 `IsCjkNoSpace` 的 U+3000–303F / U+FF61–FF9D 范围，所以被当作“非 CJK”并插入空格。
- 修复：`IsCjkNoSpace` 补全 U+FF01–FF0F、U+FF1A–FF20、U+FF3B–FF40、U+FF5B–FF5E，以及 U+FE10–FE1F / U+FE30–FE4F；`NeedsSpaceBetween` 和 `NormalizeFragment` 增加“CJK 闭标点后不插空格”规则（`OcrLineComposer.cs:271`、`:309`）。
- 复跑 Windows 设置页：`像素；未安`；单测覆盖 `中文，English`、`Hello. World`、`像素； 未安装` 等边界。

结论：修复候选有效；已提交 `7c9173b`。

## 3. 中优先问题

### F5（P2，已修复候选）legacy 回退下 `Text != join(Lines)`

旧 `BuildText` 对 `line.Text` 再做一次 `NormalizeFragment`，legacy 路径下 `settings-cn.png` 为 12 行、`TEXT_EQ_LINES=False`。改为直接按 `Lines` 文本拼接（`OcrLineComposer.cs:65`）后，legacy 复跑 `TEXT_EQ_LINES=True`，CJK 空格仍为 3（旧行为）。

结论：修复候选有效；已提交 `7c9173b`。

### F6（P2，已修复候选）Windows 预处理放大时行框没有随词框缩放

`OcrService.ExtractTextAsync` 在 `preprocessed.Width/Height` 与原因尺寸不同时，原先只缩放 `line.Words`，没有缩放 `line.X/Y/Width/Height`；预览覆盖会错位。已在缩放块同时更新行框（`OcrService.cs:36–53`）。

结论：静态缺陷已修；已提交 `7c9173b`；当前真实图 `GEOMETRY_BAD=0`。

### F7（P1，发布准备）版本坐标和发布文档仍是 0.3.20

- `Directory.Build.props`：`Version=0.3.20`、`AssemblyVersion=0.3.20.0`、`FileVersion=0.3.20.0`。
- `src/TrueToneCap.App/Package.appxmanifest`：`Identity/@Version=0.3.20.0`。
- `README.md` 徽章 `v0.3.20`。
- `git tag` 里没有 `v0.3.20`；修复提交 `7c9173b` 之后 `main` 领先 `origin/main`（本地未 push）。
- 工作树仅剩未跟踪的审查计划 `docs/reviews/ocr/post-release-review-plan-2026-11-27.md`；产品代码/测试已提交。
- `CHANGELOG` 顶部是 `[未发布] — 2026-11-27`，不是可发布条目。

结论：直接执行 `Publish.ps1 -Version 0.3.21` 会得到版本坐标不一致的 MSIX；必须先同步这四处，并把 `[未发布]` 收口为 0.3.21 条目。**这是打包前置阻断项。**

### F8（P2，未修）Windows OCR 中文字符质量仍明显不合格

同一张 `settings-cn.png`：

- ONNX 默认路径：15 行，文本基本正确。
- Windows 混合路径：6 行，`MODE=Threshold`，字符错误明显（`I-IDR截图工具`、`只蟛外观`、`河图后提0` 等）；这不是 composer 的行合并/空格问题，而是 Windows 语言包/识别质量问题。
- `ExtractTextAsync` 在预处理结果胜出时还会把 `result2.Mode="Mixed(zh+en)"` 覆盖成 `Threshold`（`OcrService.cs:55`），只是诊断标记错误，但会误导“走的哪条路径”的判断。

结论：**不要用 Windows OCR 的这张图结果作为 composer 验收通过证据**；如果 0.3.21 要宣称 Windows OCR 可用，需要单独修质量或明确降级提示。

### F9（P2，未修）`TTC_OCR_DUMP_BOXES=1` 把完整 OCR 文本写到 `%TEMP%`

`OnnxOcrEngine.DumpRawBoxes`（`OnnxOcrEngine.cs:584`）在开关开启时把每行 `text` 写入 `%TEMP%\ttc-ocr-boxes-*.tsv`。默认关闭，但没有 UI 提示、没有保留期限/自动清理。若诊断开关被支持人员打开，截图文字（可能含敏感信息）会留在临时目录。

结论：发布前至少要在文档/支持流程中写明“仅诊断、用完删除”；若要更严格，改成只导出几何、不导出文本。

### F10（P2，契约风险）`MapDecodedIndices` 把 `dict.Length+1` 硬编码为空格类

`OnnxOcrEngine.cs:1158` 假设模型输出类别 = blank + `dict.Length` 字典项 + 1 个尾随空格类。当前 PP-OCRv6 模型/字典实测成立（字典 18,708 项，rec 18,710 类），但换模型/换字典就会错映射。应把“模型类别数 / 空格类索引”做成模型 manifest 的显式字段，而不是代码常量。

### F11（P2，残余风险）`MergeOrphans` / 分段阈值仍可能误并

- `MergeOrphans`（`OcrLineComposer.cs:421`）：≤2 字符、垂直重叠 ≥0.5、gap ≤1.5H 的短片段会并入相邻行；在 UI 里两个独立短标签（如 `[1]` 与 `5 8`）可能被拼成一行。
- 分段阈值 `max(2.5×行高,40px)` 对小字（40px 可能相当于 2–3 个字符宽）偏宽，对大字偏窄；它按视觉 gap 判断，无法区分“同一段落的词距”和“两栏/两个控件”。
- `BuildRows` 的 0.60 重叠/中心比例对超高框、旋转框、上下标仍可能吞行；已有测试覆盖“高框不吞下一行”，但没有真实旋转/上下标语料。

结论：这些是设计上的启发式残余，不是本轮已证实的确定性 bug；0.3.21 前至少保留现有回归，不宜再扩大阈值改动。

### F12（P3，已知限制）Unicode Extension B 代理对未覆盖

`IsCjkNoSpace` 是 `char` 级，Extension B 及以上的代理对不在范围；当前 PP-OCRv6 字典也没有这些字（实测字典 18,708 项中 Extension B 样本 0）。因此对当前模型不构成实际漏空格，但不能宣称“所有 Unicode 汉字”覆盖。

### F13（P3，性能未判）`BuildRows` / `MergeOrphans` 没有 p50/p95 压力读数

`BuildRows` 为 O(n²)，`MergeOrphans` 多轮扫描最坏 O(n³)。4K 真实图 165 行可接受，但没有 1000+ 框的密集页面基准；0.3.21 若遇到复杂 IDE/网页截图，可能卡在 composer 而不是模型。

### F14（P3，未判）外部判据仍有 2 处未判

- `icc_profile_id_check.py`：5 个候选 ICC 的 ProfileID 均为零，缺有效样本。
- `tiff_png_referee.py`：缺 numpy / tifffile / Pillow，外部读回未做。

这两项不是本轮 OCR 改动引入，但全量门禁不能宣称“全部判据通过”；发布说明应保留“未判”字样。

## 4. 已提交修复清单（`7c9173b`）

已提交：

- `src/TrueToneCap.Core/Services/OcrLineComposer.cs`
- `src/TrueToneCap.Core/Services/OcrService.cs`
- `src/TrueToneCap.Test/OcrLineComposerTests.cs`
- `src/TrueToneCap.Test/OcrWindowsLineTests.cs`
- `CHANGELOG.md`
- `docs/ocr-line-composer-acceptance-2026-11-27.md`
- `docs/reviews/ocr/post-release-review-findings-2026-11-27.md`（本文件）

未跟踪：

- `docs/reviews/ocr/post-release-review-plan-2026-11-27.md`（审查框架，尚未收口；不影响打包）

## 5. 0.3.21 打包前置清单

1. **已完成**：修复提交 `7c9173b`；工作树仅剩未跟踪的审查计划。打包前确认这一点。
2. **同步版本坐标**：`Directory.Build.props` 三行、`Package.appxmanifest` 的 `Identity/@Version`、`README` 徽章统一为 `0.3.21` / `0.3.21.0`。
3. **收口 CHANGELOG**：把 `[未发布]` 改为 `[0.3.21] — 2026-11-27`，写入 65 OCR / 2993 全量 / 2 处未判，以及 Windows 质量残余（不要写成“Windows OCR 已修复”）。
4. **重跑门禁**：`scripts/verify.ps1 -Configuration Release`；真实图复跑 ONNX/Windows/legacy 三路径；确认 Core DLL 大小/符号与旧包不同。
5. **重建 7z/MSIX**：打包后从包内解出 `TrueToneCap.Core.dll`，确认包含 `OcrLineComposer` / `MapDecodedIndices`；MSIX 清单 `Identity/@Version` 必须为 `0.3.21.0`。
6. **冒烟/签名**：安装 MSIX，确认 OCR 预览、复制全文、翻译段落组、回退开关都正常。
7. **打 tag**：包验证通过后再打 `v0.3.21`；不要 force-push，不要用旧 0.3.20 包替代。

## 6. 证据文件

- `artifacts/verify-post-review-final2.log`：2993 / 0 / 2 skipped。
- `artifacts/verify-ocr-punct3.log`：65 OCR 测试。
- `artifacts/ocr-acceptance/probe-onnx-final.log`：受控 3 行、设置页 15 行。
- `artifacts/ocr-acceptance/probe-4k-final.log`：4K 165 行。
- `artifacts/ocr-acceptance/probe-legacy-final.log`：legacy 12 行 / CJK 3 / Text 一致。
- `artifacts/ocr-acceptance/probe-windows-current-controlled.log`：Windows 受控 3 行。
- `artifacts/ocr-acceptance/probe-windows-punct.log`：Windows 设置页 6 行 / `；` 空格已消除。
- `artifacts/ocr-acceptance/probe-quotes-real.log`：英文引号 3 行。
- `artifacts/ocr-acceptance/probe-japanese.log`：日文假名 4 行。
- `artifacts/ocr-acceptance/pkg-0.3.20-core/TrueToneCap.Core.dll`：旧包 Core DLL 742,400 B。
- `artifacts/ocr-acceptance/pkg-0.3.20-msix/AppxManifest.xml`：`Version=0.3.20.0`。
- 当前 Release Core DLL：`src/TrueToneCap.Core/bin/Release/net11.0-windows10.0.26100.0/TrueToneCap.Core.dll`，759,808 B。

## 7. 一句话给发包人

**先把工作树修复提交、版本坐标改成 0.3.21、重建包；现有 0.3.20 包里的 OCR 仍是旧的，不能拿它当“已修复”交付。**
