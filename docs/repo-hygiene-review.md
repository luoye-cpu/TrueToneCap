# 仓库规范审查报告 (2026-08-25)

---

## 🔴 发现的问题

### 1. OCR 模型文件四份完全相同的副本被 git 跟踪（~265MB 浪费）

**实测确认**：以下 4 个文件 MD5 完全相同（每个 det 29.7MB + rec 36.6MB）：

```
src/TrueToneCap.App/Models/PP-OCRv6_medium_det.onnx     (29.7MB)
publish/PLAN/models/PP-OCRv6_medium_det.onnx            (29.7MB) ← 重复
publish/PLAN/models_fp16/PP-OCRv6_medium_det.onnx       (29.7MB) ← 重复
publish/PLAN/models_fp32/PP-OCRv6_medium_det.onnx       (29.7MB) ← 重复
(rec 同样 ×4)
```

更严重的问题：**`models_fp16` 目录里放的是 FP32 文件**（与 `models_fp32` 完全相同）— 目录名与内容不符，误导性极强。而 `App/Models` 中实际使用的是 FP16 量化模型？需要核实 — 若 App/Models 也是 FP32，则 FP16 模型根本不在仓库中。

**建议**：
- `models_fp16/`、`models_fp32/` 二选一保留（或都删，只留 `models/`）
- 长期方案：模型不入库，用 `download_models.ps1` 脚本按需下载（脚本已存在！），配合 Git LFS 或发布时下载
- 短期：删除重复的 `models_fp16`、`models_fp32` 两目录（省 ~133MB 仓库体积）

### 2. `.gitignore` 第 94 行有可疑条目：单独一个 `0`

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
| .gitignore 完整性 | ✅ 126 行，覆盖 bin/obj/publish/log/crash/签名密钥等 |
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

| 优先 | 行动 | 效果 |
|------|------|------|
| 🔴 | 删除 `publish/PLAN/models_fp16/` 和 `models_fp32/` 重复目录 | 省 ~133MB |
| 🔴 | 核实 FP16 模型实际版本; 若仓库中全是 FP32, 更新 download_models.ps1 说明 | 消除目录名误导 |
| 🟡 | Publish.ps1 加工具链哈希校验 (Resources vs PLAN/tools) | 防同步漂移 |
| 🟡 | 提交前整理 docs/ 8 个文档 → 合并为 3-4 个主题文档 | 避免文档碎片化 |
| 🟡 | `git diff HDRPreviewWindow.cs` 核实是否行尾噪音 | 保持提交干净 |
| 🟢 | 清理 .gitignore L94 的 `0` 条目 | 代码卫生 |
| 🟢 | 基准输出文件名加时间戳规范 | 可追溯性 |

**长期建议**：模型文件迁移到 Git LFS 或纯下载方案（download_models.ps1 已具备雏形），仓库体积可从 168MB 降至 <40MB。
