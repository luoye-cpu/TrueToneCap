# Phase F · GUI 质量选项修复集 · 独立对抗验证报告（2026-11-06）

> 验证员：独立结果验证 agent（对抗姿态，只验证不修复、不 commit/push）
> 验证对象：本轮 8 文件修复集（工作区未提交状态）+ 6 个 Test_QualityChain_* 判据
> 背景材料：`docs\ledger\handoff-gui-quality-2026-11-06.md`、`docs\ledger\quality-chain-evidence-2026-11-06.md`

---

## 最终裁决：**VERDICT: PASS**

（2026-11-06 晚间补充：验证完成后确认无 .bak/篡改残留，最终基线重跑 2043/0/2 恢复，报告未做任何代码改动。）

---

## 0. 中文概述

**执行流**：Post-fix 基线全量跑（2043 通过 / 0 失败 / 2 跳过）→ 双篡改注入（判据灵敏度实验，2039 通过 / 4 失败 / 2 跳过）→ .bak 逐字节还原 + .bak 清理 → 最终重跑（2043 通过 / 0 失败 / 2 跳过，基线恢复）。
**验证方法**：修复真实性与缺陷消除用 **Oracle 检查**（git diff 逐文件比对声明 + 全仓残余 grep + forceFormat 场景源码级推演 + 行为级断言取证）；回归用**一致性检查**（2043/0/2 基线在还原后逐字不变）；判据有效性用**篡改注入反证**（防假绿）。
**裁决**：**PASS** —— 100% 测试通过（2043/0/2，两轮一致），6 个新判据 76 断言全部真实执行且经注入实验证明"真能抓红"，协议/序列化/枚举 Tag 逐字未变，改动集无越界、零 commit/push。无任何失败用例，无 .bak 残留。

---

## 1. 修复真实性（逐文件 diff 核对）✅

| 文件 | 声明 | 实际 diff | 裁定 |
|---|---|---|---|
| MainWindow.xaml.cs | 删滑块二源覆盖、EncodingUiState 删 Quality 字段、快照不再采集 | record 删 `float Quality`（:3241 原）、`CaptureEncodingUiState` 删 `Quality: (float)QualitySld.Value`（:3366 原）、BuildEncodingSettings 删 `settings.Quality = ui.Quality;`（:3404 原）+ 收口注释 | 一致 |
| MainWindow.Settings.cs | 删死键镜像写入 | 删 `_settings.Quality = QualitySld.Value;`（:363 原）→ 注释；`SetQuality(CurrentFormat(), …)` 保留 | 一致 |
| JpegLiNative.cs | 主路 Clamp 前加量纲 Warn；EncodeGray 仅注释 | :125-130 新增 `if (distance < 0/ > 25) CoreLog.Warn("JPEGLI", …)`；:319-325 EncodeGray 仅注释（声明"理论不可达=死代码"不 Warn） | 一致 |
| NativeAvifEncoder.cs | 命令行 Debug 留痕，参数零改动 | RunPngViaStdin(:303) + EncodeFile(:493) 各加一条 `CoreLog.Debug("AVIF", …)`，psi.Arguments 组装逐字未动 | 一致 |
| NativeWebPEncoder.cs | 同上 | :90-93 加 `CoreLog.Debug("WEBP", …)` + `using TrueToneCap.Core.Logging`（CoreLog 之必需）；`-q {(int)quality}` 组装逐字未动 | 一致 |
| AppSettingsData.cs | 遗留死键注释，字段/序列化/默认值零改动 | :117-122 仅注释替换；`public double Quality { get; set; } = 80;`（现 :123）逐字在位 | 一致 |
| CompressionEffort.cs | 仅注释口径收口 | Smallest summary 与枚举 doc 注释改写；`Smallest=0/Balanced=1/Fastest=2/Quick=3` 与成员集零改动（现 :72/75/78/101） | 一致 |
| CorePipelineTests.cs | 新增 6 判据 76 断言 | 6 个 Test_QualityChain_*（:10134/:10222/:10290/:10331/:10401/:10471）+ 注册点 :210-215 | 一致 |

**排障记录**：git diff 输出中 CompressionEffort.cs 第二 hunk 显示乱码，经 Python 字节级检测（UTF-8 decode OK、无乱码字节序列）+ Read 复核确认为 **git bash 终端显示层编码问题，文件内容无损**，非缺陷。

**改动集边界**：`git status --porcelain` = 8 个 M + 2 个 untracked（`.resultverify/`=本验证 scenario 记录、`docs\ledger\quality-chain-evidence-2026-11-06.md`=声明中的证据报告）。`artifacts\quality-probe\` 被 gitignore（声明已知）。**无越界文件、无 XAML 布局改动、无协议文件（EncodingSettings/AppJsonContext/SettingsService）改动。**

## 2. 残余消费/写入方全仓 grep ✅

- `settings.Quality =`：产品代码 **0 处**活跃覆盖；仅存于修复注释（xaml.cs:3410）与测试判据/注释（CorePipelineTests :10088/:10208/:10450）。
- `ui.Quality`：产品代码 0 处代码行；仅 xaml.cs:3410 注释 + 测试注释。判据③的"全文件剥注释后零出现"断言保证注释形式不算数。
- `QualitySld.Value` 残余 5 处全部合法且有明确身份：MainWindow.Settings.cs:368（保存时写每格式字段 SetQuality）、xaml.cs:878（UpdateQualityPanel 回填）、:1348（OnQualityChanged → SetQuality）、:1501/:1512（质量文本框 ↔ 滑块双向同步，LostFocus 路 :1514 同样落 SetQuality）。滑块唯一出口 = 写每格式字段，无任何直进编码设置的通道。
- `_settings.Quality =`（死键写入）：全仓 **0 处**，镜像写入删除彻底。
- 读取侧唯一残留 `settings.Quality:F2`（xaml.cs:3421 日志）为合法读。

## 3. 缺陷消除 · forceFormat 场景源码级推演 ✅

**场景**：当前格式 WebP（滑块显示 50-100 区间）→ 贴图窗"保存为 AVIF"（xaml.cs:2989 传 `OutputFormat.AVIF`）。

修复后链路（全部亲读核对）：
1. `EncodeAndSaveAsync` :1625-1626：`ui = CaptureEncodingUiState()`（**快照无 Quality 成员，滑块值物理上进不来**）→ `ui = ui with { Format = AVIF }`。
2. `ExecuteEncodeSaveAsync` :1671/:1683：`format = ui.Format = AVIF` → `BuildEncodingSettings(AVIF, …, ui, …)`。
3. MainWindow.BuildEncodingSettings :3406-3407 委托管线 → :3409-3419 仅注释，**无任何 Quality 写入**。
4. 管线 `CapturePipelineService.cs:294`：`Quality = (float)GetPerFormatQuality(s, AVIF)`；:594-595 `GetPerFormatQuality = s.GetQuality(format)` → AppSettingsData 键表 `OutputFormat.AVIF => s.QualityAvif`。
5. ⇒ **质量取 QualityAvif 字段（默认 20，CRF 量纲）**，滑块的 WebP 量纲值（50-100）完全不可达；AVIF 编码器 `CrfToQuality(20) → -q 68`，量纲正确。

修复前对照：`settings.Quality = ui.Quality`（滑块 50-100）→ CRF 50-100 → `-q 0~20` 极差画质（实证报告 §1.3 可达形态）。**量纲错位路径已消除，且无等价替代路径**（残余 grep + 推演双确认）。

行为级佐证（同套件内）：判据②真跑 cjpegli/cwebp 命令行逐字匹配（`--distance 2.5000`/`-q 55`）、AVIF 换算方向单调钉死；判据④真灌 PNG 量纲 100/负值 -5 → Warn 留痕 + 产物有效。

## 4. 回归判据有效性（防假绿实验）✅

**基线**：`dotnet run --project src\TrueToneCap.Test -- --unit-tests --no-incremental` → **2043 通过 / 0 失败 / 2 跳过**（与声明一致）。输出中 6 判据逐断言可追踪（"质量落点"29 条、"JPEG LI/AVIF/WebP"16 条、"二源钉"11 条、"量纲防御"5 条 + 行为级 5 条，共 76+ 条全绿；2 跳过为 MFT AV1 等环境性 Skip，与本修复无关）。

**双篡改注入**（备份一律 .bak，禁 git checkout）：
| 篡改 | 文件 | 内容 | 结果 |
|---|---|---|---|
| 1（判据③） | MainWindow.xaml.cs | BuildEncodingSettings 方法体注入 `settings.Quality = (float)QualitySld.Value;`（滑块二源现役等价形态，可编译） | ❌ "二源钉: 方法体内不存在 settings.Quality = / ui.Quality（3404 覆盖不复活）" 变红；相邻断言不受误伤 |
| 2（判据④） | JpegLiNative.cs | 删除量纲 Warn 块（if + CoreLog.Warn） | ❌ 三连红：形态钉"钳制发生时必发 Warn" + 行为级"distance=100 触发 Warn（实得 Warn 0 条）" + "distance=-5 触发 Warn（实得 Warn 0 条）"——"实得 0 条"证明 Core DLL 真重编、行为级真被执行 |

→ 总计 **2039 通过 / 4 失败 / 2 跳过**：4 红全部正中注入点、零误伤、零假绿。**判据真能抓篡改。**

**还原与恢复**：`cp .bak` 回两文件 → `cmp` 逐字节一致 → .bak 删除 → 篡改标记验证不存在 → 最终重跑 **2043 通过 / 0 失败 / 2 跳过**，与篡改前基线逐字一致。**无 .bak 残留**（最终 `git status` 与 ls 双确认）。

## 5. 协议/兼容性零破坏 ✅

- **设置序列化契约**：AppSettingsData 类仅注释改动；`AppJsonContext.cs:13 [JsonSerializable(typeof(AppSettingsData))]` 反射源生成上下文未动 ⇒ JSON 键集逐字不变；死键 `"Quality"` 仍可反序列化（老文件兼容），默认值 80 未变。
- **EncodingSettings 协议**：定义文件不在改动集（git status 证实）。
- **编码器参数组装**：三个编码器 diff 仅新增 Debug 日志/JPEG LI Warn；`--distance {distArg}`、`-q {q}`、`lossless ? "-lossless" : $"-q {(int)quality}"`、`Math.Clamp(distance, 0.0f, 25.0f)` 全部逐字在位（判据②④的形态钉亦逐字断言）。
- **枚举 Tag**：`Smallest=0/Balanced=1/Fastest=2/Quick=3` 未动，Tag 字符串映射（CompressionEffortMap）不在改动集。

## 6. 无越权改动 ✅

- 本轮修复集 8 文件全部 **unstaged/uncommitted**；`git log -1` HEAD = `f529306`（docs handoff，2026-11-06 18:12，上一轮会话产物）。
- 本地领先 origin/main 仅 f529306 一个提交（上轮交接文档入库），**本轮修复零 commit、零 push**；stash 空。
- 未执行任何 git checkout / reset / 修复性代码改动；验证全程只报告不改（唯一工作区写入 = 本报告与 `.resultverify\verification_scenario.json` 场景记录）。

---

## 7. 结论

**VERDICT: PASS**

五项验证目标全过：修复与声明逐字一致（无超出、无遗漏）；forceFormat 量纲错位路径经源码级推演确认消除；2043/0/2 基线两轮复现一致；判据经注入实验证明非假绿（4 红正中目标）；协议/枚举/序列化逐字未变；无越权改动。无失败用例需列出。遗留观察（非缺陷、不在本轮范围）：f529306 交接文档尚未 push，属上一轮会话节奏，交由用户决定。