# Phase F · JPEG LI 默认口径变更（d 1.0→0.5 / 420→444）· 独立对抗验证报告（2026-11-06）

> 验证员：独立结果验证 agent（对抗姿态；只验证不修复、不 commit/push；文件恢复一律 .bak）
> 验证对象：任务 9 改动集（9 处联动，叠加于上一轮已 PASS 的二源覆盖修复集之上，两批均未提交）
> 背景材料：`docs\ledger\handoff-gui-quality-2026-11-06.md` §7（文末追加）、`docs\ledger\quality-chain-evidence-2026-11-06.md`（拍板动机物证）

---

## 最终裁决：**VERDICT: PASS**

---

## 0. 中文概述

**执行流**：Post-fix 基线全量跑（**2043 通过 / 0 失败 / 2 跳过**）→ 篡改注入（ChromaJpegLi 默认注回 "420"，**2041 通过 / 2 失败 / 2 跳过**，双判据正中）→ .bak 逐字节还原 + 清理 → 最终重跑（**2043 通过 / 0 失败 / 2 跳过**，基线恢复）。
**验证方法**：口径落点与不变量用 **Oracle 检查**（diff 逐文件比对声明 + grep 三大不变量 + 新装/老用户两条路端到端源码推演）；回归用**一致性检查**（还原后 2043/0/2 逐字复现）；判据有效性用**篡改注入反证**（防假绿）。
**裁决**：**PASS** —— 9 处联动与声明逐项一致（1 处声明行号坐标偏差 2 行，见 §1 注），历史正文未被改写，三大不变量全部在位且判据钉住，两条默认值抵达路径推演成立，判据经注入实验证明真实咬合，无越权改动。无失败用例，无 .bak 残留。

---

## 1. 逐文件 diff 核对（9 处联动）✅

| # | 声明坐标 | 实际核对 | 裁定 |
|---|---|---|---|
| 1a | AppSettingsData.cs:137 QualityJpegLi=0.5 | `public double QualityJpegLi { get; set; } = 0.5;` + 注释块重写（物证引用：420 下块效应 1.73/1.77 + 色度高频损失 93%） | 一致 |
| 1b | AppSettingsData.cs:352 ChromaJpegLi="444" | `= "444"` + 注释 339-351（"三处同源"纪律 + GAINMAP 分家声明） | 一致 |
| 2 | FormatEncoders.cs:119 def 0.5f | `(0.5f, 3.0f, 0.5f, ButteraugliRange)` + "与字段默认成对，对撞钉住"注释 | 一致 |
| 3 | MainWindow.xaml:161 IsSelected→444 | 444 项加 `IsSelected="True"`、420 项移除（2 插 2 删，精确互换） | 一致 |
| 4 | MainWindow.Settings.cs:423 兜底 ?? "444" | `?? "420"` → `?? "444"` + 拍板注释；AVIF/JXL 兜底行未动 | 一致 |
| 5 | SettingsService.cs:773 fallback "444" | `FixChroma(s.ChromaJpegLi, fallback: "444")` + :785-788 注释同步；FixChroma 形参默认 "420" 保留（签名不惊动，GainMap 迁移语义仍按 420 判） | 一致 |
| 6a | SelfTest.cs:1364 越界回落期望 444 | `Expect("越界","ChromaJpegLi","42",…,"420"→"444")`；ChromaGainMap 期望 420 未动 | 一致 |
| 6b | SelfTest.cs:1511 夹具差集窗口形态 | 夹具删 `QualityJpegLi`/`ChromaJpegLi` 显式键（缺键=没动过=落当前默认）；"搬 GainMap 侧偏好"判据路径完整保留（见 §3） | 一致 |
| 6c | handoff §7 另声明 SelfTest.cs:318-325 注释 | 分家注释同步（"420 ≠ 默认 444（2026-11-06 起）⇒ 红"），代码零改动 | 一致 |
| 7 | CorePipelineTests 两断言改 444 + 注释 | :11159 区 `ChromaJpegLi 默认 420→444` 断言、:11167 区 `Tag='420'→'444' IsSelected` 断言；其余断言（GAINMAP 硬编码 444 锚文本、LI 仍读字段、422/444 档位、ChromaGainMap=420）逐字保留；QualityChain 判据注释同步（测试值 {2.5, 0.5} 不变，注释声明"不判非默认"无弱化） | 一致 |
| 8a | JpegLiNative.cs:114-116 仅注释 | 注释默认档 0.5 口径 + 上轮量纲 Warn 块原样保留；Clamp/参数构造零改动 | 一致 |
| 8b | CapturePipelineService.cs:256-260 仅注释 | 旧口径"JPEG LI 还是默认 420"作废注释；`JPEG_LI => (s.BitDepthJpegLi, s.ChromaJpegLi)` 代码行未动 | 一致 |

**上批文件隔离核验**：MainWindow.xaml.cs（18/3=21 行）、CompressionEffort.cs（23/4=27 行）、NativeAvifEncoder.cs（9/0）、NativeWebPEncoder.cs（6/0）numstat 与上轮验证时逐字节口径一致，且 xaml.cs 的 diff 中无本批内容（唯一 "0.5" 匹配为上批已有注释）——本批未触碰上批文件。

**handoff 历史正文核验**：`git diff --numstat` = **31 插入 / 0 删除**，唯一 hunk `@@ -181,3 +181,34 @@`（文末追加 §7/§7.2）——§0-§6 历史正文未被改写 ✅。

**声明坐标偏差（非缺陷）**：不变量"管线 GAINMAP 硬编码 444"声明在 CapturePipelineService.cs:266，实际 :268（本批 :256-260 注释改动使后续行号漂移 2 行）。该行本身零改动、测试判据用锚文本（`pipeText.Contains("OutputFormat.JPEG_GAINMAP => (s.BitDepthJpegLi, \"444\")")`）而非行号，不受影响。

## 2. 端到端推演（新默认值抵达编码器）✅

**新装用户路**（`CreateDefaults() => new()`，SettingsService.cs:69——字段初始化器即唯一默认源）：
`QualityJpegLi=0.5` + `ChromaJpegLi="444"` → 管线 `BuildEncodingSettings`（cs:252 `JPEG_LI => (s.BitDepthJpegLi, s.ChromaJpegLi)`、cs:294 `Quality = GetQuality(JPEG_LI)`）→ `JpegLiNative.Encode(…, 0.5, chroma:"444")` → 命令行 `--distance 0.5000 --chroma_subsampling 444`。
UI 同步：滑块初值/越界回落 def = `GetQualityRange().Default = 0.5f`（FormatEncoders.cs:119，与字段成对、判据对撞）；下拉首帧 = XAML `IsSelected="True"` 落 444 项 + 运行时回显 `SetComboByTag(ChromaJpegLiCbo, _settings.ChromaJpegLi)`（MainWindow.Settings.cs:186，老用户 420 照实显示）。

**老用户路（差集缺键文件）**：反序列化缺键 → 落 C# 字段默认 0.5/"444" → Normalize（:460 `ClampTo(0.5,3.0,dft)` 合法不动、:773 FixChroma("444") 合法不动）→ 同上抵达编码器。**缺键 = 新默认自动生效，正是拍板意图。**

**老用户路（显式存过 1.0/420）**：键在盘上 → 反序列化 1.0/420 → 不覆盖（尊重用户选择）、回显 420。

**迁移影响**（`MigrateGainMapMerge` 零改动，SettingsService.cs:655-667）：`Take("质量",…,d.QualityJpegLi,…)` 与色度判定 :663 均以 `d = CreateDefaults()` 的**当前默认**为参照。差集窗口老文件（缺键）判 `toCur == def` 成立 → 搬 GainMap 侧偏好 ✅；整份写回老文件（钉 1.0/420）判 `toCur != def` → 不搬——即 handoff §7 已如实声明的"已知近似"，方向安全（不搬 ≠ 搬错，保住用户文件现状），且 SelfTest 夹具改后该边界被显式钉住。

## 3. 不变量核实（三件套）✅

1. **GAINMAP 管线硬编码 444**：CapturePipelineService.cs:**268** `OutputFormat.JPEG_GAINMAP => (s.BitDepthJpegLi, "444")` 零改动；判据"管线对 JPEG_GAINMAP …色度恒 444"绿。
2. **ChromaGainMap="420" 不动**：字段（AppSettingsData.cs:362）、Normalize 回落（SettingsService.cs:782 `fallback: "420"`）、迁移读取（:663-666）、判据 :11160"仍默认 420"全在位——迁移"是否等于默认"判定所依赖的锚未被漂移。
3. **QualityGainMap=1.0 不动**：字段（AppSettingsData.cs:135）零改动，测试值对撞（判据①中 JPEG_GAINMAP 测试值 2.5 ≠ 默认 1）依旧成立。

**SelfTest.cs:1511 夹具改动未弱化判据**：三臂结构完整——①gmUser 断言 `a.QualityJpegLi == 2.5 && a.ChromaJpegLi == "444"`（差集夹具下默认 0.5/444 ≠ 2.5，迁移必须真"搬"才绿；若迁移误判"两侧都改过"而不搬，同样红——比旧夹具覆盖面更宽）；②liUser 保留 LI 侧 0.8；③idempotent 盖章幂等。逻辑零改动，仅夹具形态贴近差集序列化现实。

## 4. 回归测试 ✅

`dotnet run --project src\TrueToneCap.Test -- --unit-tests --no-incremental` → **2043 通过 / 0 失败 / 2 跳过**（两轮：改动后基线 + 篡改还原后，逐字一致；2 跳过为 MFT AV1 等环境性 Skip）。口径判据执行痕迹可追踪：
- `JpegLi 编码器默认 0.5 落在区间 [0.5,3]` + `设置模型与编码器一致（0.5 vs 0.5）`（QualityDefaultsSingleSource 对撞钉）
- `JPEG_LI 测试值 2.5 … ≠默认 0.5`（判据①测试值口径已随默认更新）
- `distance=0.5 的命令行留痕逐字出现 --distance 0.5000`
- `增益图态实际生效的色度字段 ChromaJpegLi 默认 444` / `JPEG LI 色度下拉存在且默认落在 444 项` / `遗留字段 ChromaGainMap 仍默认 420` / `管线对 JPEG_GAINMAP …恒 444` / `管线对 JPEG_LI 仍读 s.ChromaJpegLi`

## 5. 判据灵敏度抽查（防假绿）✅

**篡改**：`AppSettingsData.cs:352` 默认 `"444"` → `"420"`（+ 篡改标记注释；.bak 先行备份）。
**结果**：**2041 通过 / 2 失败 / 2 跳过**，2 红全部正中、零误伤：
1. ❌ `Test_Settings_ComboFallbacksMatchDefaults`（:9867 正则交集比对）——drift 消息直指注入点：`ChromaJpegLi: 默认=420 兜底=444`
2. ❌ `Test_GainMap_ChromaIsRealSetting`（:11159）——`增益图态实际生效的色度字段 ChromaJpegLi 默认 444` 断言红

**"三处同源，改一处必改三处"纪律的两条对撞判据均真实咬合**（第三处 XAML IsSelected 由另一断言钉住）。"漏改兜底"正是 2026-11-04 AVIF 踩过的同型缺陷，此判据已实证可抓。

**还原与恢复**：`cp .bak` → `cmp` 逐字节一致 → `.bak` 删除 → 篡改标记消除 → 重跑 **2043 通过 / 0 失败 / 2 跳过**。全程未用 git checkout，无 .bak 残留（最终 git status + 篡改标记 grep 双确认）。

## 6. 无越权改动 ✅

- HEAD = `f529306`（上轮交接文档，`git log origin/main..HEAD` 仅此 1 个未 push 提交，属上轮节奏）；**本轮两批改动集 14 个 M 文件全部 unstaged、零 commit、零 push**；stash 空。
- 改动集边界：14 M = 上批 8 文件 + 本批 6 新文件（handoff.md、MainWindow.xaml、CapturePipelineService.cs、SelfTest.cs、SettingsService.cs、FormatEncoders.cs）；4 untracked = .resultverify/（验证记录）、quality-chain-evidence / quality-slider-wiring / verify-phase-f-quality-fix（声明与验证产物）。**无越界文件。**
- 验证全程只报告不改（唯一写入 = 本报告与 .resultverify 场景记录），未执行任何 git checkout/reset。

---

## 7. 结论

**VERDICT: PASS**

六项验证目标全过：9 处联动与声明一致（含 handoff §7 追加声明而任务描述未列的 SelfTest.cs:318-325 注释）；handoff §0-§6 历史正文零改写（31/0 纯追加）；新装/老用户两条路 0.5/444 均正确抵达 `--distance/--chroma_subsampling` 构造且 UI 三处同源；三大不变量在位且被判据钉住；2043/0/2 两轮一致；判据经注入实验证明真实咬合（2 红正中 + 还原后基线恢复）。无失败用例需列出。

**观察项（非缺陷，供台账）**：①声明坐标 CapturePipelineService.cs:266 实际 :268（本批注释致行号漂移，锚文本判据不受影响）；②迁移"当前默认近似当时默认"对 2026-09-24 前整份写回文件不搬 GainMap 侧偏好——handoff §7 已声明的固有近似，方向安全且人群极小，判据夹具已显式钉住该边界。