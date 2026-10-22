# Markdown 文档过时与正确性审查报告

> **日期**：2026-09-14
> **范围**：19 个 `.md` 文件（17 个文档 + 2 个记忆文件）
> **方法**：逐文档对比**当前源码/配置/依赖**的实际状态，验证文件路径、版本号、类名、流程描述
> **结论**：**是，存在大量过时内容**，其中 1 处为**严重错误**（`publish/PACKAGE.md` 描述了已被彻底替换的发布流程），
> 1 处为**方向性矛盾**（`ocr-parallel-analysis.md` 主张的优化已被代码反向修正）。

---

## 一、总览：17 个文档的分级

| 等级 | 含义 | 文档 |
|:---:|------|------|
| 🔴 **严重过时** | 描述了已被替换/删除的流程，照做会出错 | `publish/PACKAGE.md` |
| 🟠 **方向性矛盾** | 主张的优化方案已被代码反向修正，照做会引入缺陷 | `docs/ocr-parallel-analysis.md` |
| 🟡 **部分过时** | 版本号/文件引用/完成状态有明确错误，但主干仍有效 | `docs/architecture-design.md`、`docs/repo-hygiene-review.md`、`docs/roadmap-v0.4.md`、`docs/reviews/dependency/dependency-audit-2026-08-25.md` |
| 🟢 **基本有效** | 分析类文档，结论未见与现状冲突 | `ocr-accuracy-analysis.md`、`ocr-pipeline-review.md`、`performance-analysis.md`、`overall-optimization-2026-08-25.md`、`pipeline-review-2026-08-25.md` |
| ✅ **本次新建** | 2026-09-14 产出，已与现状对齐 | `cicd-pipeline-review`、`dependency-inventory`、`dependency-management`、`git-tracking-audit`、`re-audit` |

> **说明**：5 个 🟢 文档多为 2026-08-25 前后的**性能/管线分析**，
> 属于"当时的分析快照"，其价值在于**分析过程**而非永久正确的结论，
> 文件引用经批量校验 **0 处失效**（见第四节），故判定为"基本有效"。

---

## 二、🔴 严重：`publish/PACKAGE.md` 描述了一条已被删除的发布流程

这是本次审查**最严重的问题** —— 该文档描述的发布管线**已经不存在了**。

### 文档声称（第 161、169-190 行）

```
| 仓库位置 | 发布包目标 | 同步方式 |
| `PLAN/tools/`   | `native/`      | `Publish.ps1` 步骤 5.1 复制 |
| `PLAN/models/`  | `data/Models/` | `Publish.ps1` 步骤 5.2 复制 |
| `src/Core/Resources/` | 嵌入 DLL 运行时提取到 `native/` | 手动同步（与 `PLAN/tools/` 一致） |

> 注意：两个目录必须保持文件一致。
```

### 实际情况（Publish.ps1）

```
[0/6] 验证依赖完整性
[1/6] 准备输出目录
[2/6] 编译并发布
[3/6] 复制原生工具链 → native/        ← 单一事实源: Core/Resources/
[4/6] 复制 OCR 模型 → data/Models/    ← 单一事实源: App/Models/
[5/6] 复制静态文件并校验运行时
[6/6] 生成校验和与构建元数据
```

| 维度 | 文档声称 | 实际 |
|------|---------|------|
| 工具链来源 | `PLAN/tools/` | **`src/TrueToneCap.Core/Resources/`** |
| 模型来源 | `PLAN/models/` | **`src/TrueToneCap.App/Models/`** |
| 步骤编号 | 5.1 / 5.2 | **[3/6] / [4/6]** |
| 同步要求 | 两目录必须人工保持一致 | **单一事实源，无同步需求** |

### 危害

1. **`publish/PLAN/models/` 已在今天（commit `42cc8d8`）被删除** —— 文档仍在说"从 `PLAN/models/` 复制"，
   照此操作会找不到文件。
2. `Publish.ps1` 第 5、97-98 行已明文改为单一事实源，文档与之**完全相反**。
3. "两目录必须保持文件一致"这条**人工同步要求**正是 2026-08-25 仓库规范审查
   指出过的漂移隐患；既然已改为单一事实源，这条说明必须删除。

### 修复建议

删除 `publish/PACKAGE.md` 第六节中的"保持同步"表格与注意事项，
改为指向两个单一事实源，并把步骤编号更新为 `[3/6]` / `[4/6]`。

---

## 三、🟠 方向性矛盾：`docs/ocr-parallel-analysis.md` 主张的并行化已被反向修正

### 文档主张（2026-08-25）

该文档系统论证了 OCR 识别阶段的并行化方案：
- 方案 A（GPU 批量识别 Batch Inference）⭐⭐⭐⭐⭐ 推荐
- 方案 B（CPU 多会话并行）⭐⭐⭐
- 明确指出 C1「共享池化张量并行写入会互相污染」、C3「GPU 并发无收益」等约束

### 代码现状（2026-09-13 P0 修复）

`src/TrueToneCap.Core/Services/OnnxOcrEngine.cs`：

```
// ═══ 2026-09-13 P0 修复: 推理串行化信号量 ═══
private readonly SemaphoreSlim _inferGate = new(1, 1);   // 第 104 行
await _inferGate.WaitAsync(ct) ...                        // 第 322 行
```

`src/TrueToneCap.Core/Services/MultiOcrService.cs` 同样加了**全局**信号量 `s_ocrGate`，
注释写明：

> MainWindow 的选区 OCR/翻译可从不同 UI 动作并发触发…**杜绝原生层并发崩溃闪退**。

### 结论

文档主张"并行"，代码已改为"**全面串行**"—— 因为实践中并发 `Run()` 会导致
**原生层崩溃闪退**（不只是文档 C3 说的"无收益"）。

**这份文档若被后人当作待办清单照做，会引入已修复过的崩溃缺陷。**

### 修复建议

在该文档顶部加醒目警示：

```
> ⚠️ 本报告的并行化方案已被 2026-09-13 的 P0 修复**否决**。
> 实测并发 Run() 会导致 ONNX Runtime 原生层崩溃闪退，
> 现已在 OnnxOcrEngine._inferGate 与 MultiOcrService.s_ocrGate 改为全面串行。
> 本报告仅作为「为什么不能并行」的分析存档，不作为实施依据。
```

> 注：文档中 C1/C3/C4/C6 关于"共享张量污染""GPU 并发无收益"的**分析本身仍然正确**，
> 且与最终串行化的决策一致 —— 只是文档的**推荐结论**（方案 A/B）需要作废。

---

## 四、🟡 部分过时（4 篇）

### 4.1 `docs/architecture-design.md`（39KB，最大文档）

**标题声称 `v0.3.1-beta`，实际版本已是 `v0.3.4-beta`。**

| 类别 | 问题 | 证据 |
|------|------|------|
| 版本号 | 头部声明 `WindowsAppSDK 2.3.1` | 实际 `2.4.0` |
| 测试数 | `192/192 ✅ 100%`（第 5、595 行） | 与 `roadmap-v0.4.md` 的 `318` 矛盾（见 4.3） |
| **失效文件引用** | **6 处**（第 1 节项目结构树） | 见下表 |

**6 处失效引用**（批量校验 68 个引用，61 个有效）：

| 文档声称 | 实际情况 |
|---------|---------|
| `Directory.Build.targets` | ❌ 不存在（已改为 `Directory.Build.props`） |
| `Processing/GpuEffectProcessor.cs` | ❌ 不存在 |
| `ColorManagement/GamutMapper.cs` | ❌ 不存在 |
| `Encoding/ManagedJpegEncoder.cs` | ❌ 不存在（实际有 `ManagedBmpEncoder.cs`） |
| `Encoding/NvencAvifBackend.cs` | ❌ 不存在 |
| `Shaders/MosaicEffect.hlsl` | ❌ 不存在（实际只有 ToneMapping/FullscreenVS/OverlayComposite） |

**另有一处方法失效**：第 7 节声称的 6 条保存路径中，
`SilentCaptureAllAsync` **在当前代码中不存在**（其余 5 个方法均存在）。

**缺失的当前结构**（文档未列出但实际存在）：`ShaderLoader.cs`、`NativeEncoderGuard.cs`、
`IccStore.cs`、`ManagedTiffEncoder.cs`、`Directory.Packages.props`、`packages.lock.json` 等。

### 4.2 `docs/repo-hygiene-review.md`

三项发现**已被处理但未标记**，读起来像仍未解决：

| 文档声称 | 实际 |
|---------|------|
| 「OCR 模型文件**四份**完全相同的副本（~265MB）」 | ⚠️ 现在**只有 2 份**，且本次 `42cc8d8` 已把 `publish/PLAN/models/` 删除，git 跟踪中**仅剩 `App/Models/` 一份** |
| 「`.gitignore` 第 94 行有可疑条目：单独一个 `0`」 | ❌ 该条目**已不存在**（当前 `.gitignore` 共 147 行） |
| 「删除 `models_fp16/` 和 `models_fp32/` 重复目录」 | ✅ **已执行**（两目录均已不存在） |
| 「核实 FP16 模型实际版本…若全是 FP32，则 FP16 模型根本不在仓库中」 | ✅ **已明确：在库模型是 FP16**（29.7/36.6 MB vs `official_models/onnx_fp32/` 的 59.2/73.0 MB） |

另：文档称 `.gitignore`「126 行」，实际 **147 行**。

### 4.3 `docs/roadmap-v0.4.md`

| 问题 | 说明 |
|------|------|
| 基线过期 | 声称「基线版本 v0.3.2-beta (commit `0dd4215`)」，实际 HEAD 已是 `42cc8d8`，**落后 3 个提交**，版本为 `v0.3.4-beta` |
| 测试基线矛盾 | 声称「318 通过」，`architecture-design.md` 说「192/192」—— **两者都不准**。2026-09-14 实测：8 个套件 **373 项断言**全部通过（详见 `docs/reviews/repo-governance/test-verification-2026-09-14.md`） |
| 任务状态未更新 | 任务 1（PNG 后缀 + JXL）**已完成** —— `MainWindow.xaml` 已存在 `JxlPngSuffixChk`；任务 4（系统字体检测）**已完成** —— `FontLoader.cs` 已实现系统字体枚举 |
| 引用仍有效 | ✅ `AvifPngSuffixChk` 确实在 `MainWindow.xaml` L129，抽查无误 |

### 4.4 `docs/reviews/dependency/dependency-audit-2026-08-25.md`

**已处理** —— 本次已加顶部警示（虚构的 `OnnxRuntime 1.29.0`、过时的 WinAppSDK 2.3.1 等），
指向 `dependency-inventory-2026-09-14.md`。✅ 无需再动。

---

## 五、🟢 基本有效（5 篇）

`ocr-accuracy-analysis.md`、`ocr-pipeline-review.md`、`performance-analysis.md`、
`overall-optimization-2026-08-25.md`、`pipeline-review-2026-08-25.md`

**批量校验结果：文件引用失效 0 处。**

这几篇是 2026-08-25 前后的**性能与管线分析快照**：
- 价值在于**分析过程与量化方法**（时序分解、热点定位、优化排序）
- 具体数字（如"某函数 ~250ms"）随代码演进自然失效，属正常
- 建议：若长期保留，可在顶部加一行「本报告为 2026-08-25 快照，性能数据可能已变化」

> 其中 `ocr-pipeline-review.md` 的 P-4 建议「`SemaphoreSlim(4)` 限制翻译并发」，
> 与 OCR 串行化的思路一致，**未见冲突**。

---

## 六、README.md 抽查结果：✅ 准确

| 声明 | 实际 | 判定 |
|------|------|:---:|
| `v0.3.4-beta` | `Directory.Build.props` = `0.3.4-beta` | ✅ |
| `.NET 11` | `TargetFramework` = `net11.0-windows10.0.26100.0` | ✅ |
| Magick.NET 相关 | 仅出现在**历史变更日志**条目中，源码已零依赖 | ✅ |
| 依赖版本 | 与 `Directory.Packages.props` 一致 | ✅ |

> 注：README 未声明具体依赖版本号，因此不会出现"声称旧版本"的问题 —— 这是合理的写法。

---

## 七、问题汇总与建议行动

| 优先 | 文档 | 问题 | 建议动作 |
|:---:|------|------|---------|
| 🔴 P0 | `publish/PACKAGE.md` | 描述了已被删除的发布流程（`PLAN/` → 单一事实源） | 重写第六节，更新步骤为 [3/6]/[4/6]，删除"两目录同步"要求 |
| 🟠 P0 | `docs/ocr-parallel-analysis.md` | 主张并行化，但代码已因崩溃改为全串行 | 加顶部警示：方案已否决，仅作存档 |
| ✅ P1<br>（已解决） | `docs/architecture-design.md` | 版本过时（v0.3.1/WinAppSDK 2.3.1）+ 6 处失效文件 + 1 处失效方法 + 测试数矛盾 | **已完成**：6 处失效文件逐条核实去向并写入对照表（含 `NvencAvifBackend` 实为合并进 `FormatEncoders.cs` 的澄清）；结构树补入 `ShaderLoader.cs`/`FormatEncoders.cs`/`OverlayComposite.hlsl`/两个 `.props`，移除项标 `✗`；失效方法更正为 `SilentCapture()`；测试数统一为实测 **373** |
| 🟡 P1 | `docs/repo-hygiene-review.md` | 3 项发现已被处理但未标记 | 标注"已处理"，更新份数（4→1）与行数（126→147），补 FP16 结论 |
| 🟡 P2 | `docs/roadmap-v0.4.md` | 基线落后 3 提交；任务 1/4 已完成；测试数矛盾 | 更新基线，标记已完成任务，统一测试数字 |
| 🟢 P2 | 5 篇分析文档 | 快照性质，数字会自然过时 | 可选：顶部加"数据快照日期"说明 |

---

## 八、审查结论

**是的，存在大量过时内容，且不只是"版本号旧了"这种小问题。**

最需要警惕的两条，都属于**"照着文档做会出错"**而非"文档读起来旧"：

1. **`publish/PACKAGE.md` 描述了一条已经不存在的发布管线** —— 它指向的
   `PLAN/models/` 目录今天刚被删除，步骤编号和数据源全部对不上。
2. **`ocr-parallel-analysis.md` 推荐的并行化已被实践否决** —— 并发 `Run()`
   会导致原生层崩溃，代码已改为全面串行；这份文档若被当待办，会重新引入崩溃。

其余问题集中在**架构文档**（6 处失效文件引用）与**仓库规范报告**（3 项已处理未标记），
属于内容维护滞后，主干分析仍可参考。

**优先修 P0 那两条**，它们是唯一可能造成实际损害的部分。

---

## 附：核查方法（可复用）

```bash
# 1) 批量校验文档中的文件引用是否真实存在（最有效的一招）
python - <<'EOF'
import re,os
have=set(); base={}
for root,dirs,files in os.walk('.'):
    dirs[:]=[d for d in dirs if d not in ('.git','obj','bin','node_modules','.vs')]
    for f in files:
        rp=os.path.relpath(os.path.join(root,f),'.').replace('\\','/'); have.add(rp)
        base.setdefault(f,[]).append(rp)
txt=open('<doc>.md',encoding='utf-8').read()
for m in sorted(set(re.findall(r'[A-Za-z0-9_\-/\.]+\.(?:cs|xaml|ps1|hlsl|json|md|csproj|props)\b',txt))):
    if m in have or any(h.endswith('/'+m) for h in have): continue
    if os.path.basename(m) in base: continue
    print('失效引用:',m)
EOF

# 2) 抽取文档声称的版本号，与 Directory.Packages.props / Directory.Build.props 对比
grep -oE "net[0-9]+\.[0-9]+|WindowsAppSDK [0-9.]+|[0-9]+\.[0-9]+\.[0-9]+" <doc>

# 3) 检查文档声称的类/方法是否在源码中存在
grep -rn "<MethodName>" src/ --include="*.cs"

# 4) 跨文档交叉验证数字（如测试通过数）
grep -rn "[0-9]\{2,\} *\(通过\|pass\)" docs/*.md
```

**核心经验**：
- **文件引用校验**（第 1 招）能自动抓出绝大多数过时，投入产出比最高。
- **文档声称的"最佳实践"必须与代码演进对照** —— 本次最严重的两条（PACKAGE.md 流程、
  OCR 并行化）都不是靠版本号能发现的，必须读代码才能确认方向已被反转。
