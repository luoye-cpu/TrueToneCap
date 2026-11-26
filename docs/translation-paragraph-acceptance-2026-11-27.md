# 翻译段落合并实机验收报告

- 日期：2026-11-27
- 版本：0.3.20（提交 `ec440aa`；下述验收原始记录产自 0.3.19 工作区）
- 后续修复计划：`docs/ocr-line-composer-fix-plan-2026-11-27.md`
- 结论：**真实 OCR + 真实 Sensenova 端点的段落合并链路通过**。受桌面自动化权限限制，未完成鼠标点击式 UI 走查；但 Core 管线、状态栏文案、App 真机捕获自测均已实测。

## 0. 状态更新（2026-11-27 03:45）

本文原始验收完成时，翻译改动尚未提交，Release 构建还被 `WgcCaptureService` 的 `SetBorderRequired` 与 XAML `WMC9999` 阻塞。此后这些改动已随 `ec440aa` 一并提交并升到 0.3.20：

- 翻译段落合并、逐行质量判定、状态栏文案已进入提交 `ec440aa`；
- Release 全量校验通过：8 个套件 **2940 条断言通过、0 失败**，另有 2 处外部判据跳过；
- 发布包 `--selftest` 退出码 0（10 ok / 1 skip / 0 fail）；
- 原文的“未提交”“Release 被阻塞”等表述仅代表当时的验收现场，不再是当前状态。


## 1. 验收方式

因为 `computer_use_tool` 的主桌面授权超时，无法自动操作 App 窗口。改为两条真实链路验收：

1. **无头真实管线**：临时项目 `C:\Users\20210\AppData\Local\Temp\ttc-real-acceptance`，引用最新 `TrueToneCap.Core`；使用 App Debug 输出目录中的真实 PP-OCRv6 ONNX 模型，真实 `MultiOcrService.RecognizeAsync()`，再用 DPAPI 解出的用户真实 `LlmApiKey` 调用 `https://token.sensenova.cn/v1` 的 `sensenova-6.8-flash`。
2. **App 真机自测**：运行最新 Debug `TrueToneCap.exe --selftest --out ...`，验证 WGC/HDR 捕获和编码链路。

## 2. 关键结果

### 2.1 段落合并（主用例：3 行折行 + 4 个 UI 标签）

OCR 得到 7 行：

```text
[1] This is long EngIish that sentence a
[2] continues the visual line and next ext on n
[3] endS With clear conclusion. a
[4] Settings
[5] DlspIay
[6] Microsoft rosoft Store ℃
[7] ThlS PC
```

`TranslationBatchResult`：

```text
mode=LLM 批量 + 内容补译 + 段落合并
groups=1 rows=3
failed=0 unchanged=0 partial=0 contentRetry=1 fallback=0 error=none
```

回填译文：

```text
[1] 这是一段较长的英文，该句子
[2] 延续视觉行，下一段文本
[3] 以明确的结论结束。
[4] 设置
[5] 显示
[6] 微软商店 ℃
[7] 此电脑
```

状态栏文案（`TranslationStatusFormatter.Build` 实测）：

```text
✅ 翻译完成（7 行）；1 行经内容补译恢复，点击文字块可复制译文
```

判定：
- 3 行折行确实被合成 1 个段落组，共覆盖 3 行；
- 模型按原行 id 回填，没有把整段重复成 3 条；
- 4 个标签没有被误并入段落；
- 真实端点下 `contentRetry=1`，最终所有行都有译文，状态栏为 ✅；
- OCR 本身仍有 `EngIish`、`DlspIay`、`rosoft` 等识别噪声，但段落上下文让译文仍保持语义连贯。这是 OCR 层问题，不是段落合并回填问题。

### 2.2 纯段落用例的状态栏行为

另一张 1700×620、48px 大字测试图只放 3 行英文折行。OCR 把第一行末尾的一个 `a` 单独切成第 4 行：

```text
groups=1 rows=3
failed=0 unchanged=1 partial=0 contentRetry=0
状态栏：⚠ 翻译完成（1/4 行疑似未翻译或不完整）
```

该用例证明：**当 OCR 切出无法翻译的碎片时，状态栏不会伪装成 ✅**；段落合并仍然正常形成 1 组 3 行。问题在 OCR 行切分，不在段落合并。

### 2.3 真实截图回放

用 App 实际输出目录中的 `TrueToneCap_20261127_021337477.jpg` 和 `TrueToneCap_20261126_235503989.jpg` 做真实 OCR：

- 真实截图的 PP-OCR 输出存在明显的中文字符间空格、词序错乱和行切分错误；
- 翻译结果被质量判定为 `untranslated=1` / `partial=6` 等，状态栏正确显示 ⚠，没有出现“原文未翻却显示成功”；
- 这验证了本轮新增的逐行质量判定和状态栏口径在真实坏 OCR 上仍然会报警。

真实截图的 OCR 质量问题属于识别层，建议单独排查行框排序/中文空格；不应把它记到段落合并翻译头上。

## 3. App 真机自测

运行：

```powershell
TrueToneCap.exe --selftest --out %TEMP%\ttc-selftest-latest
```

结果：

```text
11/11 通过，ok=10 skip=1 missingBackend=0 fail=0，退出码 0
HDR_SOURCE：3840x2160 捕获 412ms，目标屏 DISPLAY1，HDR=yes，10bit，峰值 1015nits
```

说明最新 Debug 二进制的真实 WGC/HDR 捕获链路可用；但这不覆盖 OcrPreviewWindow 的鼠标点击流程。

## 4. 未完成/限制

- **未完成鼠标点击式 UI 验收**：主桌面自动化授权超时，无法自动打开截图工具条点“翻译”并肉眼确认预览窗。上述状态栏文案由 UI 实际调用的同一 `TranslationStatusFormatter` 生成，但不是窗口截图。
- **Release 构建仍被无关改动阻塞**：`WgcCaptureService.cs(139,25) CS1061 SetBorderRequired` 与 XAML `WMC9999`；Debug 构建和运行正常。该问题与本轮翻译改动无关。（历史现场；已在 0.3.20 提交中消除，当前 Release 构建 0 警告 / 0 错误）
- 翻译改动尚未提交、未发版。（历史现场；已随 `ec440aa` 提交并升到 0.3.20）

## 5. 证据路径

- 验收日志：`%TEMP%\ttc-real-acceptance-final3.log`
- 验收 JSON：`%TEMP%\ttc-real-acceptance-output\*.json`
- App 自测：`%TEMP%\ttc-selftest-latest\selftest.json`
- App 运行日志：`%LOCALAPPDATA%\TrueToneCap\log\app_2026-11-27.log`
- 全量测试：`%TEMP%\ttc-all-paragraph-final3.log`（11 套件，3121 通过 / 0 失败 / 1 跳过；当前发布门禁为 8 套件 2940 通过 / 0 失败 / 2 项外部判据跳过）

## 6. 结论

段落合并功能在真实 OCR 行和真实 Sensenova 端点下**确实触发并正确回填**；UI 状态栏在全部行成功时为 ✅，在 OCR 碎片/坏识别导致内容问题时为 ⚠。当前可以认为**翻译段落合并的实机链路验收通过**；剩余风险集中在 OCR 行切分/识别质量、桌面 UI 点击验收和 Release 构建的 WGC 遗留错误。
