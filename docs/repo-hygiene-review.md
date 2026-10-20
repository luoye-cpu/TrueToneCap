# 仓库规范审查报告 (2026-08-25)

> ⚠️ **状态更新（2026-09-14）**：本报告的多个发现**已在后续处理完毕**，
> 逐一标注于各条目下方。请以**标注后的状态**为准，勿按原文当作待办重复执行。
>
> 处理记录详见 `docs/git-tracking-audit-2026-09-14.md`。

---

## 🔴 发现的问题

### 1. OCR 模型文件四份完全相同的副本被 git 跟踪（~265MB 浪费）

> ✅ **已解决（2026-09-14）**
> - `models_fp16/`、`models_fp32/`：已删除
> - `publish/PLAN/models/`：已在 commit `42cc8d8` 解除跟踪（与 `App/Models/` 逐字节相同）
> - **git 跟踪中现仅剩 `src/TrueToneCap.App/Models/` 一份**（det 29.7MB + rec 36.6MB）
> - 遗留问题：历史 blob 中含两代模型（FP32 73.1+59.2MB / FP16 36.6+29.7MB），
>   旧代占 ~132MB，需 `git filter-repo` 重写历史才能释放 —— 见 `git-tracking-audit` 报告

**实测确认**：以下 4 个文件 MD5 完全相同（每个 det 29.7MB + rec 36.6MB）：

```
src/TrueToneCap.App/Models/PP-OCRv6_medium_det.onnx     (29.7MB)  ← 现唯一保留
publish/PLAN/models/PP-OCRv6_medium_det.onnx            (29.7MB)  ← 已解除跟踪
publish/PLAN/models_fp16/PP-OCRv6_medium_det.onnx       (29.7MB)  ← 已删除
publish/PLAN/models_fp32/PP-OCRv6_medium_det.onnx       (29.7MB)  ← 已删除
(rec 同样 ×4)
```

更严重的问题：**`models_fp16` 目录里放的是 FP32 文件**（与 `models_fp32` 完全相同）— 目录名与内容不符，误导性极强。而 `App/Models` 中实际使用的是 FP16 量化模型？需要核实 — 若 App/Models 也是 FP32，则 FP16 模型根本不在仓库中。

> ✅ **FP16 疑问已澄清（2026-09-14）**：**在库模型确为 FP16**。
> 实测对比：
> - `official_models/onnx_fp32/`（FP32 原始）：det 59.2MB / rec 73.0MB
> - `src/TrueToneCap.App/Models/`（在库）：det 29.7MB / rec 36.6MB ← 约为前者一半，符合 FP16 量化特征
>
> 转换工具为 `scripts/onnx_fp16_quantize.py`。`publish/PACKAGE.md` 中标注的
> "~59MB / ~73MB" 系误用 FP32 体积，**已于 2026-09-14 更正**。

**建议**：
- ✅ 已做：`models_fp16/`、`models_fp32/` 删除；`publish/PLAN/models/` 解除跟踪
- 📋 待做：历史 blob 重写（见 `git-tracking-audit` 报告）
- 📋 长期：模型不入库，用 `download_models.ps1` 脚本按需下载（脚本已存在！）

### 2. `.gitignore` 第 94 行有可疑条目：单独一个 `0`

> ✅ **已不存在（2026-09-14 核实）**：当前 `.gitignore` 共 147 行，已无单独 `0` 条目。

```
# ── Temp / Accidental files ──
0                    ← 这是误提交产生的文件名, 但作为 ignore 规则会匹配名为 "0" 的文件
*_error.log
*.dmp
```
这是历史事故痕迹（曾意外创建名为 "0" 的文件）。规则本身无害但是代码异味；应查明该条目来源并考虑清理。

### 3. 今日新增的基准输出文件放在 tests/manual 下但命名无规范

`tests/manual/ocr-real-bench.txt` 和 `ocr-real-bench-v2.txt` — 该目录已被 gitignore ✅ 不影响仓库，但 v1/v2 命名无时间戳，多次运行会混乱。**建议**：输出带日期 `ocr-real-bench-20260825-seed20260825.txt`。

---

## 🟡 需要关注的点

### 4. publish/PLAN 与 src/Core/Resources 双份原生工具链

`avifenc.exe`(11.8MB)/`cjpegli.exe`(5.1MB)/`cjxl.exe`(4.5MB) 在两处各存一份：
- `src/TrueToneCap.Core/Resources/` （嵌入 DLL 用）
- `publish/PLAN/tools/` （Publish.ps1 预提取用）

`publish/PACKAGE.md` 已声明"必须保持同步"——人工同步容易漂移。**建议**：Publish.ps1 增加哈希校验，不一致时报错（或直接从 Resources 复制到 PLAN，单一事实源）。

### 5. docs/ 目录今日新增 8 个分析文档（未跟踪状态）

全部是本次会话产出的分析报告。这些文档有价值但部分内容重叠（如 performance-analysis 与 overall-optimization 有交叉）。**建议**：提交前合并整理为 3-4 个主题文档（性能优化/OCR 分析/依赖审计/v0.4 规划），避免文档碎片化。

### 6. HDRPreviewWindow.cs 出现在 git modified 列表但今日未编辑

git status 显示 `M src/TrueToneCap.App/Services/HDRPreviewWindow.cs`，但本会话未改过此文件。可能是行尾符(CRLF/LF)或之前会话的未提交改动。**建议**：提交前 `git diff` 核实，若仅行尾差异可还原。

---

## ✅ 规范良好的方面

| 检查项 | 结果 |
|--------|------|
| .gitignore 完整性 | ✅ ~~126 行~~ → **147 行**（2026-09-14），覆盖 bin/obj/publish/log/crash/签名密钥等 |
| tests/manual 隔离 | ✅ 55 个调试脚本全部在 tests/manual/（gitignore 内，不上传）|
| scripts 目录 | ✅ 仅 2 个正式工具（migrate-net11.ps1 / onnx_fp16_quantize.py）|
| 无敏感文件 | ✅ 无 *.pfx/*.snk/API Key 文件（glm_* 含 Key 的教训已在记忆中）|
| 大文件管控 | ✅ 除模型/工具链外无 >1MB 杂物 |
| 仓库根目录 | ✅ 干净（10 个条目，全是必要文件）|
| .raw/.tmp/.bak | ✅ 无残留 |
| git 仓库大小 | ⚠️ 168.5MB（主要是模型×4 + 工具链×2 所致，见问题1/4）|
| docs 归档 | ✅ 分析报告统一放 docs/ |

---

## 📋 建议行动清单

| 优先 | 行动 | 状态 |
|------|------|------|
| 🔴 | 删除 `publish/PLAN/models_fp16/` 和 `models_fp32/` 重复目录 | ✅ 已完成 |
| 🔴 | 核实 FP16 模型实际版本 | ✅ 已澄清：在库为 FP16（29.7/36.6MB） |
| 🔴 | 解除 `publish/PLAN/models/` 重复跟踪 | ✅ 已完成（commit `42cc8d8`） |
| 🔴 | 解除 `publish/PLAN/tools/` 重复跟踪 | 📋 **待做**（9 个二进制与 Core/Resources 逐字节相同，27MB） |
| 🔴 | 历史 blob 重写（释放 ~132MB 旧代模型） | 📋 待评估（会改 SHA，需确认协作影响） |
| 🟡 | Publish.ps1 加工具链哈希校验 (Resources vs PLAN/tools) | ⚠️ **已无必要** —— 已改为单一事实源（Core/Resources），不再有双份同步 |
| 🟡 | 提交前整理 docs/ 8 个文档 → 合并为 3-4 个主题文档 | 📋 待做（现已有 17 个文档，含 5 个 2026-09-14 新增） |
| 🟡 | `git diff HDRPreviewWindow.cs` 核实是否行尾噪音 | 📋 待核实（文件仍存在） |
| 🟢 | 清理 .gitignore L94 的 `0` 条目 | ✅ 已不存在 |
| 🟢 | 基准输出文件名加时间戳规范 | 📋 待做 |

**长期建议**：模型文件迁移到 Git LFS 或纯下载方案（download_models.ps1 已具备雏形），仓库体积可从 168MB 降至 <40MB。

> **2026-09-14 更新**：仓库实际仍为 **170 MB**（即使已解除 models 跟踪），
> 因为**历史 blob 未回收**。解除 `publish/PLAN/tools/` + 一次历史重写后可达 ~40MB 目标。
> 详见 `docs/git-tracking-audit-2026-09-14.md`。

---

## 状态更新（2026-10-17 复核，逐条实测）

本文是无日期的活文档，按本仓规则有义务跟代码同步；以下只改**仍然为假的那几句**，原文其余部分不动。

| 出处 | 原文说法 | 实测现状 |
|---|---|---|
| `:111` | `HDRPreviewWindow.cs`"文件仍存在" | **已删除**（`a54bb5c` = v0.3.7-beta，B 档覆盖层改 CPU-only）。反向锁在位：`Test_Overlay_HasNoGpuBackgroundPath`（调用 `CorePipelineTests.cs:156`，定义 `:4689`）⇒ 这一格不用再去"核实行尾噪音" |
| `:110` | "现已有 17 个文档，建议提交前整理 docs/ 8 个文档 → 合并为 3-4 个主题文档" | 现有 **63 份 / 29,779 行**；且这条**行动方向已被规则推翻**：带日期的 `.md` 是取证快照，**不合并、不回写**。分层改由新建的 `docs/README.md` 索引承担（零引用改写的代价换清晰） |
| `:108/:115-119` | 仓库 168→170 MB，解除跟踪 + 历史重写后可降到 <40 MB | 本机 `.git` 实测 **252 MB**；GitHub API `size = 160,196 KB`（≈156 MB）。目标未达成，原因仍是**历史 blob 未回收** |

顺带两条**本次核过、确认不是问题**的（避免下一轮又被当成缺陷报回来）：

- `:60` 提的 `ocr-real-bench.txt` → 该建议**已执行**，磁盘实名 `tests/manual/ocr-real-bench-20260825-seed20260825.txt`；
  该目录整体已被 `.gitignore:203` 一类规则覆盖（实测未跟踪未忽略文件 = 0）。
- `:66` 标题里的 `src/Core/Resources` 只是**标题缩写**，紧跟的正文写的是正确的
  `src/TrueToneCap.Core/Resources/` ⇒ 不是断链，别去"修路径"。
