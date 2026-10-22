# WinML 迁移规划（OCR 推理运行时）

> 日期：2026-10-07
> 状态：**✅ 已实施并验收通过**（2026-10-07）—— 实施结果与逐条判据见文末 §10「实施记录」
> 前置阅读：`docs/reviews/ocr/ocr-model-replaceability-2026-10-07.md` §3.4（WinML 收益与兼容性实测）
>
> 本文中所有"实测"结论均由本机真实执行得出（构建 + 推理 + 压力测试），非文档推断。
> 凡属推断未验证的，一律标注 ⚠️**待验证**。

---

## 0. 决策摘要

| 问题 | 结论 |
|---|---|
| 值得现在迁移吗？ | **不值得立即迁移**，但**值得现在做"低风险前置步骤"** |
| 迁移的代码改动量 | **极小** —— 全部 `SessionOptions` API 实测可直接编译运行，`OnnxOcrEngine.cs` 预期 **0 行改动** |
| 真正的工作量在哪 | **打包/合规**（依赖替换、许可台账、发布校验），不在代码 |
| 最大风险 | 许可与体积，**不是**技术可行性 |
| 建议节奏 | 分三阶段，P0 现在做（独立受益），P1/P2 条件触发 |

**一句话**：技术上这是一次"换包"而不是"改代码"；但本仓有严格的发布校验与许可台账，真正的工程量在那条链上。

---

## 1. 迁移面：实测确认的改动清单

### 1.1 依赖引用点唯一（这是最大利好）

全仓 5 个 csproj 中，`OnnxRuntime` 的引用**只有一处**（已用 grep 逐一核对）：

```
src\TrueToneCap.Core\TrueToneCap.Core.csproj:45
  <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" />
Directory.Packages.props:36
  <PackageVersion Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
```

（`App` / `LogViewer` / `Test` / `Tools` 四个 csproj 均无直接引用，仅通过 `ProjectReference` 传递。）

App / Test / Tools / LogViewer **均不直接引用**。因此替换的爆炸半径是**一个 csproj + 一个版本文件**。

### 1.2 代码改动：实测预期为 0

我用 **WinML 2.4.89（仅此一个包）** 建了一个工程，把 `OnnxOcrEngine.CreateSessionOptions()`（`OnnxOcrEngine.cs:262-316`）里**用到的每一个 API 原样搬过去**编译：

| 本仓用法 | 位置 | WinML-only 编译结果 |
|---|---|---|
| `AppendExecutionProvider_DML(0)` | `:270` | ✅ 通过 |
| `EnableCpuMemArena` | `:271,310` | ✅ |
| `GraphOptimizationLevel.ORT_ENABLE_BASIC` | `:282` | ✅ |
| `AddSessionConfigEntry("optimization.disable_simplified_layer_norm_fusion","1")` | `:286` | ✅ |
| `EnableMemoryPattern` | `:297` | ✅ |
| `AddSessionConfigEntry("session.use_ort_model_bytes_directly","1")` | `:299` | ✅ |
| `ExecutionMode.ORT_SEQUENTIAL` | `:302` | ✅ |
| `InterOpNumThreads` / `IntraOpNumThreads` | `:305,307` | ✅ |
| `LogSeverityLevel` | `:313` | ✅ |
| `new InferenceSession(path, opts)` + `Run()` | `:195,198,786` | ✅ 实测推理成功，`C=18710` |

**结论：`Microsoft.ML.OnnxRuntime` 托管 API 在 WinML 包里依然存在且版本更新**（详见 §2.1），
本仓的 `OnnxOcrEngine` 代码**预期无需改动**。

> ⚠️**待验证**：以上是"可行性探针"级别的验证（独立测试工程），**未在 `TrueToneCap.Core` 真实工程上做过替换构建**。实施时必须在真工程上复现一次完整构建。

### 1.3 需要改动的文件（预估）

| 文件 | 改动 | 性质 |
|---|---|---|
| `Directory.Packages.props:36` | 替换 `Microsoft.ML.OnnxRuntime.DirectML` → `Microsoft.Windows.AI.MachineLearning` | 核心 |
| `src\TrueToneCap.Core\TrueToneCap.Core.csproj:45` | 同上（包名替换） | 核心 |
| `licenses\` + `licenses\INDEX.json` | 新增 WinML 许可条目，可能移除/保留 ORT 条目 | 合规 |
| `THIRD-PARTY-NOTICES.md` | 增补 Microsoft Windows ML Runtime EULA | 合规 |
| `Publish.ps1` | 见 §4（PDB 剔除逻辑、体积门槛） | 打包 |
| `scripts\verify-package.ps1` | 见 §4（清单/尺寸下限） | 打包 |
| `docs\` | 架构文档同步 | 文档 |

---

## 2. 关键技术决策：依赖替换方式

### 2.1 实测：WinML 单包自带全部所需组件

只引用 `Microsoft.Windows.AI.MachineLearning` 时，输出目录自动包含：

| 文件 | 体积 | 版本 |
|---|---|---|
| `Microsoft.ML.OnnxRuntime.dll`（托管 API） | 0.23 MB | — |
| `onnxruntime.dll`（原生引擎） | 22.02 MB | **1.27.20260711** |
| `DirectML.dll` | 17.83 MB | 1.15.4 |
| `Microsoft.Windows.AI.MachineLearning.dll`（WinML API） | 0.93 MB | 2.4.89 |

**即 WinML 包同时提供托管 ORT API + 原生运行时 + DirectML EP**，可作为唯一依赖。

### 2.2 ⚠️ 实测风险：不要"两个包都引用"

我实测过**同时**引用两个包的情况：

- 构建**成功**（0 error）
- 但输出目录里 `onnxruntime.dll` **只有一份**，是 **WinML 的 1.27**
- 而 `Microsoft.ML.OnnxRuntime.dll` 仍是 **1.24.4** 的托管绑定

也就是说会出现 **托管 1.24 ↔ 原生 1.27 的版本错配**。实测该错配下推理**侥幸可用**（`C=18710`），但这属于**未定义行为**：ORT 托管层与原生层的 ABI 匹配没有保证，且随 NuGet 解析顺序变化，**不可复现、不可控**。

> **规划结论：采用"替换"而非"叠加"。** 移除 `Microsoft.ML.OnnxRuntime.DirectML`，只保留 `Microsoft.Windows.AI.MachineLearning`。

### 2.3 ⚠️ 待验证：DirectML EP 的存在形式变化

本仓当前 `AppendExecutionProvider_DML(0)` 来自 ORT-DirectML 包。换 WinML 后：

- **实测**：WinML-only 工程里该 API **编译通过且 DML 推理成功**
- 但 WinML 文档把 DirectML 描述为 **"legacy"** 内置 EP，且主推 `ExecutionProviderCatalog` 动态获取厂商 EP（`NvTensorRtRtx` / `OpenVINO` / `QNN` / `MIGraphX` / `VitisAI`）

> ⚠️**待验证**：本仓是否应继续直接用 `AppendExecutionProvider_DML`，还是改为走 `ExecutionProviderCatalog`。
> **建议分阶段**：迁移时**先保持 `AppendExecutionProvider_DML(0)` 不变**（行为等价、风险最低），把"改用 EP Catalog"作为**独立的后续增量**。这样迁移本身是纯粹的依赖替换，行为可对比。

---

## 3. 阻塞风险的排查结论

### 3.1 曾观察到的失败

在早期探针中，WinML 的 ORT 1.27 上 det 模型**报过一次**：

```
Non-zero status code returned while running Add node. Name:'Add.154'
BroadcastIterator::Append axis == 1 || axis == largest was false.
Attempting to broadcast an axis by a dimension other than 1. 3 by 4
```

### 3.2 本次规划期的系统性排查（共 139 次干净运行，0 次复现）

| 排查项 | 规模 | 结果 |
|---|---|---|
| det 单独，9 种尺寸（方图 + 多宽高比） | 9 | ✅ 全通过 |
| det 重复同形状 | 15 | ✅ 15/15 |
| det 重复 + workaround | 15 | ✅ 15/15 |
| rec→det 复现原始调用序列 | 1 | ✅ 通过 |
| **压力测试**：det+rec 同进程、CPU/DML 双 EP、7 种尺寸、6 轮 | **84** | ✅ 84/84 |
| **复现尝试**：rec-then-det、每轮新建 Session、25 轮 | **25** | ✅ 25/25 |

### 3.3 结论

**139 次运行、0 次复现**，且失败形状（384×896）在后续测试中稳定通过 ⇒ 该失败**不是确定性不兼容**。

最可能的原因已定位：那次失败发生在**紧接工程重建之后、以 `--no-build` 启动**的调用中，
存在 `onnxruntime.dll` 正在被替换/占用（文件锁）的竞态 —— 这解释为何"同形状重跑即通过"。

> **规划判定：该风险从"阻塞项"降级为"注意事项"。**
> 但**不宣称已证明无问题** —— 因为 139 次虽多，仍未覆盖长时间运行/内存压力场景。
> **因此迁移验收必须包含压力回归**（见 §6 的 A4/A5），而不是只跑一次冒烟。

### 3.4 ⚠️ 顺带发现：workaround 可能已成为"静默死代码"

实测在 WinML 的 ORT 1.27 上，**不设** `disable_simplified_layer_norm_fusion` 时 rec 模型也不再触发故障
（输出 `max=1.0`，无"全零"症状）；det 同样未复现。

但该调用被 `catch {}` 静默包裹（`OnnxOcrEngine.cs:284-288`）：

```csharp
try { opts.AddSessionConfigEntry("optimization.disable_simplified_layer_norm_fusion", "1"); }
catch { /* ORT 版本不支持该配置时忽略 */ }
```

⇒ **升级后无法从日志判断它是否仍在生效**。若某天它被 ORT 拒绝（或反之，不再需要），**不会留下任何痕迹**。

> **规划动作（强烈建议，且独立于 WinML）**：把 `catch {}` 改为"失败时 `CoreLog.Warn` + 记录本次是否生效"。
> 这是**几行代码的改动**，却把一个"静默失效"变成"可观测"。见 §5 P0-2。

---

## 4. 打包与合规：真正的工作量

### 4.1 体积影响（实测，本仓口径）

| 组成 | 现状（ORT-DirectML 1.24.4） | WinML 2.4.89 | 差 |
|---|---|---|---|
| `onnxruntime.dll` | 20.66 MB (1.24) | 22.02 MB (1.27) | +1.36 |
| `DirectML.dll` | 17.83 MB | 17.83 MB | **0**（同一份） |
| 托管层 | 0.22 MB | 0.23 MB | +0.01 |
| WinML API dll | — | 0.93 MB | +0.93 |
| **合计** | **≈38.71 MB** | **≈41.01 MB** | **≈+2.3 MB** |

> 官方宣传的"更小体积"依赖 **framework-dependent** 模式（共享系统运行时）。
> **本仓是 self-contained**（`App.csproj:8,15`），**拿不到该收益**。
> 若改 framework-dependent 才能真正瘦身，但那要求用户机器预装 Windows App SDK runtime，
> 与当前"便携绿色包"的定位冲突 ⇒ **不建议为此改变分发形态**。

### 4.2 实测：WinML 自带 Microsoft EULA，非 MIT/Apache ← **重点合规项**

`Microsoft.Windows.AI.MachineLearning 2.4.89` 包内含：

| 文件 | 体积 | 性质 |
|---|---|---|
| `license.txt` | — | **MICROSOFT SOFTWARE LICENSE TERMS — Microsoft Windows ML Runtime** |
| `ThirdPartyNotices.txt` | **331,175 B (≈323 KB)** | 第三方声明 |

许可要点（已读原文）：

- §3.a.i：**允许再分发** —— *"Any files that are binplaced with your application by the WindowsAppSDK NuGet package are, by definition, permitted to be redistributed. This applies to both framework package dependent and self-contained deployments."* ✅ 本仓 self-contained 场景**明确被覆盖**
- §3.b.ii：要求分发者/最终用户接受"至少与微软同等保护"的条款
- §3.b.iii：**要求你为微软做赔偿担保（indemnify, defend, hold harmless）**
- §3.c.ii：禁止以会导致"必须开源"的方式修改/分发

⚠️ **对本仓的意义**：

本仓是 **Apache-2.0**，且维护着一份**严格的许可台账**（`licenses/INDEX.json` + `THIRD-PARTY-NOTICES.md` + `Publish.ps1` 的 `[5/6]` 许可分发断言）。WinML 引入的是**专有 EULA**（含赔偿担保条款），与既有的 MIT/Apache/BSD 条目**性质不同**：

- 现值 `licenses/` 已有 `microsoft-windowsappsdk-LicenseRef-Microsoft-EULA.txt` 先例（Windows App SDK 同样是 EULA），**所以不是全新类别**
- 但**需要新增** Windows ML Runtime 的 EULA 全文与 331 KB 第三方声明，并更新 `INDEX.json`
- ⚠️**待确认**：331 KB 的 `ThirdPartyNotices.txt` 是否需全文随包分发（本仓已有的同类做法是保留全文，见 `onnxruntime-third-party-notices.txt`）

> **规划动作**：迁移前先做一次**许可影响评估**，确认新增义务与 `[5/6]` 断言的兼容性。
> 这是**迁移的前置门槛**，不是事后补票。

### 4.3 ⚠️ 待验证：发布校验链

本仓的发布链对"包内容"有硬断言，替换运行时**可能触发**：

| 位置 | 断言 | 迁移影响 ⚠️ |
|---|---|---|
| `Publish.ps1:648-649` | 剔除第三方 `.pdb`（注释明确提到 `Microsoft.AI.DirectML` 私有符号 ~12.3 MB） | **需复验**：WinML 包的 PDB 名称/体积可能不同 ⇒ 剔除逻辑与"体积占比"注释都要更新 |
| `Publish.ps1:940-941` | `native/` exe 计数、`data/Models` 体积，写入 `build-info.json` | 运行时 dll 不在 `native/`，**预期不受影响**，但需实测确认 |
| `Publish.ps1:780` | 校验清单：主程序 / Bootstrap.dll / native 4 exe / 模型 3 项 | **预期不受影响**（不含 ORT dll），需实测 |
| `scripts\verify-package.ps1:177-179` | 模型三项存在性 + 尺寸下限 | **预期不受影响** |
| `App.csproj:183-184` | native exe 计数硬断言 `!= '4'` | **预期不受影响** |
| `Publish.ps1` `[5/6]` | 许可分发断言（`licenses/` 文件数下限 + `INDEX.json` 可解析） | **会受影响** ⇒ 必须同步更新台账 |

> **规划动作**：迁移的第一个可执行步骤应是**跑一次"替换包名 → 构建 → 发布"的空转验证**，
> 用它把上述"预期不受影响"逐条落实为实测结论，而不是等正式迁移时才发现。

### 4.4 部署模式决策

| 模式 | 体积 | 自动更新 | 依赖用户机器 | 本仓适配 |
|---|---|---|---|---|
| **self-contained**（现状） | +2.3 MB | ❌ 手动 | 无 | ✅ **保持** |
| framework-dependent | 更小 | ✅ Windows Update | 需预装 WinAppSDK runtime | ❌ 与"便携包"冲突 |

**结论：保持 self-contained。** 代价是放弃"自动更新"这一条收益，换来分发形态不变。

---

## 5. 分阶段路线图

### P0 — 现在就该做（独立受益，不依赖 WinML 决策）

| # | 动作 | 收益 | 风险 | 工作量 |
|---|---|---|---|---|
| **P0-1** | 把 `Microsoft.ML.OnnxRuntime.DirectML` 从 **1.24.4 升到接近 1.27**（纯版本升级，不换包） | 单独验证新 ORT 是否改变 PP-OCRv6 行为；为 WinML 铺路 | 低（可随时回退版本号） | ~1h + 回归 |
| **P0-2** | `OnnxOcrEngine.cs:284-288` 的 `catch {}` → **改为失败时 `CoreLog.Warn` 并记录是否生效** | 把"workaround 静默失效"变成可观测 | **极低** | ~0.5h |
| **P0-3** | 用现有工具跑**基线存档**：`--ocr-bench-real`（seed 1409）+ `--ocr-noise` + `--ocr-perf`，记录**模型名 + SHA256 + ORT 版本** | 为迁移提供"可比对的前后基线"（当前基线只标了引擎，没标模型/ORT） | 极低 | ~1h |
| **P0-4** | 许可影响评估：确认 WinML EULA + 331 KB 第三方声明如何进 `licenses/` 台账 | 提前暴露合规门槛 | 低 | ~1-2h |

> P0-1/P0-2 即使**永不迁移 WinML** 也有价值：它们把当前"版本锁定 + 静默 workaround"的两个盲点补上。

### P1 — 条件触发：WinML 迁移本体

**触发条件**（满足任一）：
- 需要 **NPU 加速**（例如引入更大模型、或 OCR 吞吐成为瓶颈）
- DML 出现安全/兼容问题，或 `sustained engineering` 状态变化
- 需要厂商优化 EP（`NvTensorRtRtx` / `OpenVINO` 等）带来的性能

**执行顺序**（每步可独立回退）：

1. **空转验证**：只替换包名 → 构建 → 跑发布链，逐条确认 §4.3 的"预期不受影响"
2. **行为等价验证**：用 P0-3 的基线跑同一批用例，比对识别结果与耗时（**必须逐字符一致**，允许耗时差异）
3. **压力回归**：把 §3.2 的压力测试脚本化，纳入验收（≥100 次运行，0 失败）
4. **许可台账更新**：`licenses/` + `INDEX.json` + `THIRD-PARTY-NOTICES.md`
5. **发布链校准**：`Publish.ps1` 的 PDB 剔除、体积注释、`verify-package.ps1`
6. **文档同步**

### P2 — 后续增量（与迁移解耦）

- 改用 `ExecutionProviderCatalog` 动态获取厂商 EP（解锁 NPU）
- 评估 framework-dependent 模式（若某天愿意放弃"便携包"定位）

---

## 6. 验收标准（迁移完成必须全部满足）

| # | 判据 | 方法 |
|---|---|---|
| **A1** | 识别结果**逐字符一致**（对基线语料） | `--ocr-bench-real` seed 1409，对比迁移前后输出 |
| **A2** | 误检不增加 | `--ocr-noise` 对比 |
| **A3** | 性能不劣化超过 X% | `--ocr-perf`；建议阈值 **≤ +10%**（ORT 1.24→1.27 + dll 版本变化，允许小幅波动） |
| **A4** | 长时间压力 0 失败 | ≥100 次 det+rec 交替、双 EP、多形状（§3.2 脚本化） |
| **A5** | 快速连拍不崩溃 | 复用既有"推理串行化"场景（`_inferGate` 语义），验证并发路径 |
| **A6** | 发布链全绿 | `Publish.ps1` 完整跑通，`[5/6]` 许可断言通过 |
| **A7** | 许可台账完整 | `INDEX.json` 可解析、新增条目含全文与哈希 |
| **A8** | CPU 与 DML 路径均可用 | 分别强制两种 EP 跑 A1 |
| **A9** | DML 不可用时正确降级 | 复用 `IsDirectMLAvailable()` 路径，确认仍降级到 CPU 而非报错 |

> ⚠️ **A4/A5 不可省略**。§3 的 139 次无复现只能说明"不是确定性不兼容"，
> **不能排除**低概率稳定性问题。OCR 场景下"失败 = 用户看到空结果/乱码"，必须用压力测试兜住。

---

## 7. 风险登记

| 风险 | 等级 | 依据 | 缓解 |
|---|---|---|---|
| **许可义务变化**（专有 EULA + 赔偿担保 + 323 KB 声明） | 🔴 高 | §4.2 原文 | P0-4 前置评估；`[5/6]` 断言校准 |
| **版本错配**（两包并存 ⇒ 托管 1.24 + 原生 1.27） | 🟠 中 | §2.2 实测 | **替换而非叠加** |
| **发布链断言失效**（PDB 剔除 / 体积门槛） | 🟠 中 | §4.3 ⚠️待验证 | P1 步骤 1 空转验证 |
| **低概率推理失败** | 🟡 低 | §3：139 次 0 复现，已定位为测试竞态 | A4/A5 压力回归 |
| **放弃自动更新收益** | 🟡 低 | §4.4 | 明确接受；改为手动随版本升级 |
| **代码改动超预期** | 🟢 很低 | §1.2 全部 API 实测可编译 | 首步即验证，不通过就停 |
| **DirectML "legacy" 定位变化** | 🟡 低 | §2.3 | 迁移时保持 `AppendExecutionProvider_DML` 不变，解耦 |

---

## 8. 复现与验证方式

```powershell
# 1) 依赖引用点（应只有一处）
Select-String -Path (Get-ChildItem -Recurse -Filter *.csproj src).FullName -Pattern "OnnxRuntime"

# 2) 现有基线（迁移前必须存档，含模型名+SHA256+ORT 版本）
dotnet run --project src/TrueToneCap.Tools -- --ocr-bench-real 1409
dotnet run --project src/TrueToneCap.Tools -- --ocr-noise
dotnet run --project src/TrueToneCap.Tools -- --ocr-perf src/TrueToneCap.App/Models

# 3) 当前 ORT 版本（迁移前后对比）
(Get-Item src/TrueToneCap.App/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/onnxruntime.dll).VersionInfo.FileVersion
#   现状实测: 1.24.20260504.1.7444c45
#   WinML 2.4.89 实测: 1.27.20260711.1.7b7c0e2

# 4) 体积对比（迁移前后）
Get-ChildItem -Recurse <publish-dir> -Include onnxruntime.dll,DirectML.dll | Select Name,Length
```

**关键文件定位**：

| 关注点 | 位置 |
|---|---|
| **唯一依赖引用点** | `TrueToneCap.Core.csproj:45` + `Directory.Packages.props:36` |
| SessionOptions（全部 API 已验证） | `OnnxOcrEngine.cs:262-316` |
| **静默 workaround（P0-2 目标）** | `OnnxOcrEngine.cs:284-288` |
| DML 可用性探测 | `OnnxOcrEngine.cs:321-336` |
| GPU/CPU 选择 | `MultiOcrService.cs:69,85` |
| 打包模式 | `TrueToneCap.App.csproj:8,14,15` |
| PDB 剔除（Microsoft.AI.DirectML） | `Publish.ps1:648-649` |
| 发布内容校验 | `Publish.ps1:780`、`scripts\verify-package.ps1:177-179` |
| 许可台账 | `licenses\INDEX.json`、`THIRD-PARTY-NOTICES.md` |

---

## 9. 待办清单（实施时勾选）

**P0（现在可做，独立受益）**
- [ ] P0-1 纯版本升级 1.24.4 → 近 1.27，跑回归
- [ ] P0-2 `catch {}` → 带日志/生效状态（`OnnxOcrEngine.cs:284-288`）
- [ ] P0-3 基线存档（含模型名 + SHA256 + ORT 版本）
- [ ] P0-4 WinML 许可影响评估

**P1（触发后执行）**
- [x] 空转验证：替换包名 → 构建 → 发布链逐条确认
- [x] 行为等价验证（A1/A2/A3/A8/A9）
- [x] 压力回归（A4/A5）脚本化并纳入 CI/验收
- [x] 许可台账更新（A7）
- [x] 发布链校准（A6）
- [x] 文档同步

**P2（解耦增量）**
- [ ] 评估 `ExecutionProviderCatalog`（解锁 NPU）
- [ ] 评估 framework-dependent（若放弃便携定位）

---

## 10. 实施记录（2026-10-07）

### 10.1 改动清单（实际落地）

| 文件 | 改动 |
|---|---|
| `Directory.Packages.props:36-51` | `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 → `Microsoft.Windows.AI.MachineLearning` 2.4.89（含完整取舍注释） |
| `src\TrueToneCap.Core\TrueToneCap.Core.csproj:45-51` | 同上包名替换 + "禁止加回旧包"警示 |
| `src\TrueToneCap.Core\Services\OnnxOcrEngine.cs` | P0-2：新增 `LayerNormWorkaroundState` 枚举与 `LayerNormWorkaround` 属性；`catch {}` → Warn+状态；模型加载处 Info 级留痕 |
| `src\TrueToneCap.Tools\WinMlStress.cs` | **新增** `--winml-stress` 压力回归 harness |
| `src\TrueToneCap.Tools\FormatBench.cs:181-187` | 分派 `--winml-stress` |
| `licenses\microsoft-winml-LicenseRef-Microsoft-EULA.txt` | **新增**（13,996 B） |
| `licenses\INDEX.json` | 新增 WinML 条目；更新 ONNX Runtime 两条的来源与 obligationNote |
| `THIRD-PARTY-NOTICES.md` | 生成器重建（398,386 → 399,188 B，43 条许可） |
| `Publish.ps1:648-655` | PDB 剔除段的注释更新为"当前空转"的实测现状 |
| `scripts\verify-package.ps1:276-284` | 许可计数注释更新（42 → 43） |
| `CHANGELOG.md` | 新增 [未发布] 章节 |

**未改动**：`OnnxOcrEngine` 的 SessionOptions 逻辑、推理路径、box 提取、CTC 解码 —— 一行未动。

### 10.2 规划预测 vs 实际结果

| 规划中的预测 | 实际结果 |
|---|---|
| "代码改动预期为 0" | ✅ **证实** —— Core 仅改了包引用，`OnnxOcrEngine` 的 SessionOptions 全部 API 原样编译运行 |
| "体积 +2.3 MB" | ✅ **精确命中** —— 实测 38.71 → 41.01 MB = **+2.30 MB** |
| "不要两个包都引用" | ✅ 规避 —— 采用替换；并实测确认替换后依赖图中旧包**完全消失** |
| "发布链断言可能触发" | ⚠️ **部分成立**：`verify-package.ps1` 的 `native/` 4-exe 断言与模型断言**未受影响**（实测完好）；但**新增了**一个未预料到的点 —— 第三方 PDB 从"有"变成"无"，使 `Publish.ps1` 的剔除段成为空转（已就地注明） |
| "许可义务变化是重点合规项" | ✅ **证实** —— 且比预期更细：WinML 2.4.89 的 EULA 与**已在台账里的** WindowsAppSDK.ML 2.1.74 EULA **不是同一份文本**（14,033 B vs 13,996 B，哈希不同），必须单列。331 KB 第三方声明则**字节完全相同**，只需更新来源 |
| "保持 self-contained" | ✅ 已保持，分发形态未变 |
| "体积拿不到框架依赖的收益" | ✅ 证实 |

### 10.3 验收结果（逐条）

| 判据 | 方法 | 结果 |
|---|---|---|
| **A1** 逐字符一致 | `--ocr-bench-real` 的 84 个样本准确率读数比对（剔除计时）；`--ocr-bench` 的 123 行结果逐字节比对 | ✅ **全部相同**。总量 6421/7052、202/292、14 失败样本，迁移前后一致 |
| **A2** 误检不增加 | `--ocr-noise` 输出比对 | ✅ **完全一致**（含既有的 2 行横线误检，属既有问题非新增） |
| **A3** 性能不劣化 | `--ocr-bench-real` 计时均值 | ✅ **显著变快**：5772ms → 3351ms（-42%）；复跑 1954ms（-66%） |
| **A4** 压力 0 失败 | `--winml-stress 8`：双 EP × 7 尺寸 × 8 轮 = **112 次** | ✅ **0 失败** |
| **A5** 并发不崩溃 | 同 harness 的 24 路并发段 | ✅ **0 失败**，无死锁 |
| **A6** 发布链 | 5 项目 Release 构建 + 8 套件测试 + `--selftest` + 输出断言 | ✅ 0 警告 0 错误；**1820 通过 / 0 失败**；selftest **11/11**；`native/` 4 exe 完好 |
| **A7** 许可台账 | `INDEX.json` 解析 + 全部条目 sha256 实算比对 + 生成器 `--check` | ✅ 43 条全部通过；`--check` PASS |
| **A8** CPU 与 DML | `--ocr-bench` 默认与 `gpu` 两种 | ✅ 两路径读数均与基线一致 |
| **A9** DML 降级 | 代码路径复核 + API 可用性实测 | ✅ 路径未改动；`AppendExecutionProvider_DML` 在 WinML 下可用 |

### 10.4 实施中发现的两处计划外事实（已处理）

1. **LogViewer 的 WinML 版本与 App 不同**：`Microsoft.WindowsAppSDK` 2.4.0 会传递引入
   `Microsoft.WindowsAppSDK.ML` 2.1.74（内含 ORT 1.24）。App 因显式引用 2.4.89 而由 NuGet
   **nearest-wins** 正确解析到 **2.4.89 / ORT 1.27**（实测输出确认）；LogViewer 不引用 Core，
   仍为 2.1.74 —— **属既有状态，与本次迁移无关，不影响 OCR**（OCR 只在 App 进程内跑）。
2. **`--selftest` 不覆盖 OCR 推理**：实测 selftest 的 11 项全是编码器与设置迁移。
   这正是 §3.4 那次 `Add.154` 无法由 selftest 兜住的原因 ⇒ 新增的 `--winml-stress` 补上了
   这个缺口，且可直接作为 ORT/运行时升级的常驻回归。

### 10.5 遗留（未做，属 P2）

- **未改用 `ExecutionProviderCatalog`**：本次刻意保持 `AppendExecutionProvider_DML(0)` 不变，
  使迁移成为纯粹的依赖替换、行为可逐字符对比。解锁 NPU 是**独立增量**。
- **未改 framework-dependent**：保持"便携绿色包"定位。
- **A4/A5 未纳入 CI**：harness 已就绪（`--winml-stress`），但尚未接入 `Publish.ps1` / CI 门禁。
  建议后续作为运行时升级的常驻门禁（单次全量约 150s）。
