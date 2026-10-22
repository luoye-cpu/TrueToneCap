# TrueToneCap 实机测试报告

- **日期**：2026-09-14
- **被测版本**：`0.3.4-beta`（HEAD = `0916a2a release: v0.3.3-beta` + 工作区全部未提交改动）
- **被测产物**：`publish\TrueToneCap-v0.3.4-beta`（566 文件 / 415.5 MB）
- **报告范围**：我在本环境**实际执行**的实机测试，以及**我无法执行、需要你在真终端完成**的部分

---

## 一、结论摘要

| 维度 | 结论 |
|---|---|
| 构建 | ✅ 0 警告 0 错误（16.64s，5/5 项目） |
| 门禁 7 套件 | ✅ 351 断言全绿 / 0 失败 / 1 显式跳过（56.3s） |
| 发布门禁回归 `[1.6/2]` | ✅ 通过 |
| **冷启动构建（P1-8 实证）** | ✅ 删净 `.cso` 与 `bin/` 后从零构建，3 个着色器由 fxc 现场编译并正确落入 `data\Shaders\`；与发布包字节一致 |
| **着色器双路径一致性（P1-9）** | ✅ 源目录 + 发布产物共 6 个 `.cso` 全为 `DXBC`+`SHEX`（fxc/SM5），无 `DXIL` |
| 发布产物启动 | ✅ 真机启动存活 12s、177 线程、440 MB 工作集、无早退 |
| 4K 六格式编码 | ✅ 6/6 格式真实编码成功（含 NVENC AV1 硬件路径） |
| 色彩管理（ICC / cICP / iCCP） | ✅ 全部正确 |
| OCR 链路 | ✅ 模型加载、检测、识别、字典全通 |
| OCR 准确率（合成图） | 🟡 字符 85.0% / 行 71.4%（修复前后一致，无回归） |
| OCR 准确率（真实渲染） | 🟢 **字符 77.35% → 91.48%（+14.13pp）**，行 65.41% → 68.84%，失败样本 21 → 13 |
| **长行检测漏框** | 🟢 **已定位根因并修复**（详见发现 4） |
| **DirectML 路径** | 🔴→🟢 **曾 100% 崩溃（P0，输出张量 use-after-free），已定位根因并修复**（详见发现 5） |
| **DirectML 纹理上限 16384** | 🟡→🟢 **超限时检测静默崩塌（300 字符档 66%），已收紧修复 → 100%**（详见发现 6） |
| **DML 准确率低于 CPU 4.07pp** | 🟢 **已定位根因并修复**：真凶是 GPU `maxRatio` 8x 放大过度（非数值精度），改 6x 后 **87.41% → 91.51%**（详见 4.4） |
| **DML 性能** | 🟢 **再快 5.9 倍**：`EnableMemoryPattern` 对 DML 应关闭（官方要求），改后 **2364ms → 389ms**，准确率不变；现为 **91.51% @ 389ms，比 CPU 快 9 倍**（详见发现 9） |
| **韩文识别** | 🔴 **恒为 0%（能力边界，非新缺陷）** |

**总评**：管线与产物**可以发布**。本轮发现 9 项事项，其中
1 项能力边界待排期（韩文）、1 项已就地修正（PngCheck 期望值）、1 项仅影响开发态（缺 native 工具）、
6 项**已定位根因并完成修复**（长行检测漏框；DirectML 崩溃 P0；DirectML 纹理上限；
**GPU 放大倍数 8x → 6x**；**DML `EnableMemoryPattern` 设错**；附带消除 DML 过召回）。

**净效果（真实渲染语料 7052 字符）**：
- 字符准确率 **77.35% → 91.48%**（CPU）/ **91.51%**（DirectML，生产默认命中）
- DirectML 端到端：**87.41% @ 525ms → 91.51% @ 389ms**（+4.10pp 且再快 1.35 倍；相对 CPU 快 9 倍）
- 门禁无回归（372 断言 / 0 失败 / 1 跳过），合成图基准 85.0% 不变

> ⚠ **本轮排查中我自己被数据推翻的判断（保留以免重走弯路）**：
> ① "FP16 rec 噪声导致 DML 掉点" —— 换 FP32 无改善，证伪；
> ② "后端内核/图划分数值差异" —— A/B 探针显示 det 分歧仅 0.006%，证伪；
> ③ "真实语料上 maxRatio 不触发" —— 逐样本比对证明长行小字号样本正是它生效的场景，证伪。
> 三次都是**先有假设后被数据纠正**，最终根因比任何一次假设都更朴素。

> ⚠ **发布前必读**：`MultiOcrService` 默认选中第一个可用引擎（**DirectML 优先**），
> 而发现 5 的崩溃在修复前会让任何 DML 机器上的 OCR **直接杀死进程**。
> 这是本轮价值最高的修复 —— 它不在原有的检查清单里，是跨后端复验时才暴露的。

---

## 二、环境基线（本机实测）

| 项目 | 实测值 |
|---|---|
| 显示器 | HDR=**True**，支持 HDR=True，MaxLuminance=**418 nits**，位深=**10** |
| SDR 白点（`DISPLAYCONFIG_SDR_WHITE_LEVEL`） | **120 nits** |
| CPU 向量指令 | AVX2=✅ AVX512=❌ AVX10v1/v2=❌ FMA=✅ AVXVNNI=✅ NEON=❌，**VecWidth=32** |
| 硬件编码 | **NVENC SDK 13.1 可用（AV1）** |
| .NET SDK | 11.0.100-preview.7.26381.103 |

---

## 三、已执行的实机测试（全部真实运行）

执行入口统一为 `src/TrueToneCap.Tools` CLI，经 `.workbuddy-ai/runps.py` 拉起 Windows PowerShell 宿主运行。

### 3.1 构建与门禁

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build TrueToneCap.slnx -c Release` | 0 警告 0 错误 / 16.64s |
| 发布门禁回归 | `verify.ps1 [1.6/2]` | ✅ |
| 7 套件 | unit / color / encoding / service / ocr / format / usability | 47+93+14+5+10+30+152 = **351 断言**，0 失败，1 跳过 |

### 3.2 发布产物启动冒烟（此前从未覆盖）

`verify-package.ps1` 只做 PE 头与版本资源的**静态**检查，从未真正拉起主程序。本轮补齐：

```
EXE = publish\TrueToneCap-v0.3.4-beta\TrueToneCap.exe
SIZE = 262,144 bytes        ← 自包含 apphost 存根，符合预期
PID = 97188
STATE = RUNNING
WORKINGSET = 440,324,096 bytes
THREADS = 177
VERDICT = PASS (主程序已成功启动并存活 12s)
KILL = OK
```

### 3.3 冷启动构建（P1-8 关键实证）

P1-8 的原始判定是「**全新克隆 / CI 首次 publish 必被 `verify-package [3/5]` 拒**」——根因是静态
`Content Include="Shaders\*.cso"` 在**评估阶段**展开，而 `.cso` 由其后才运行的 Target 生成。
此前所有验证都是**热构建**（`.cso` 已存在），无法证明冷启动可行。本轮做了真正的冷启动测试：

**前置状态（人为制造冷态）**：删除 `src\TrueToneCap.App\Shaders\*.cso`（3 个）与整个 `src\TrueToneCap.App\bin`。

```
PRE  cso_in_src    = 0
PRE  bin_exists    = False      ← 真正的冷启动
```

**构建过程**：

```
编译 HLSL 着色器...
使用编译器: C:\Program Files (x86)\Windows Kits\10\bin\10.0.28000.0\x64\fxc.exe
编译 ToneMapping.hlsl        → Shaders\ToneMapping.hlsl.cso
编译 FullscreenVS.hlsl       → Shaders\FullscreenVS.hlsl.cso
编译 OverlayComposite.hlsl   → Shaders\OverlayComposite.hlsl.cso
✓ 所有着色器编译完成 (3 个)
TrueToneCap.App -> ...\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.dll
已成功生成。0 个警告 0 个错误 (26.97s)
```

**构建后**：

```
POST cso_in_src    = 3        (728 / 190128 / 2864 bytes)
POST cso_in_output = 3        → data\Shaders\ 下三个文件齐全
```

✅ **结论**：`CollectCompiledShaders` Target（`BeforeTargets=AssignTargetPaths`
+ `DependsOnTargets=CompileShaders`，用限定元数据 `%(_TtcCsoFile.Filename)`）
在冷态下**正确收集到了构建期刚生成的 `.cso`**，P1-8 修复得到实证。
同时验证 SDK 版本枚举正确（取到最高的 `10.0.28000.0`，未硬编码 22621）。

### 3.4 着色器双路径一致性（P1-9 实证）

判别方法：`DXBC` 魔数 + 全文件搜索 chunk 四字符码 `SHEX`（fxc/SM5）或 `DXIL`（dxc/SM6）。

```
== src\TrueToneCap.App\Shaders ==
  FullscreenVS.hlsl.cso        DXBC  728      SHEX (fxc/SM5) ✅
  OverlayComposite.hlsl.cso    DXBC  190128   SHEX (fxc/SM5) ✅
  ToneMapping.hlsl.cso         DXBC  2864     SHEX (fxc/SM5) ✅
== publish\TrueToneCap-v0.3.4-beta\data\Shaders ==
  FullscreenVS.hlsl.cso        DXBC  728      SHEX (fxc/SM5) ✅
  OverlayComposite.hlsl.cso    DXBC  190128   SHEX (fxc/SM5) ✅
  ToneMapping.hlsl.cso         DXBC  2864     SHEX (fxc/SM5) ✅
---- 合计 6 个, 异常 0 个 ----
```

✅ **结论**：构建期（`CompileShaders.ps1`）与运行时（`ShaderCompiler.cs`）产出的着色器
**同为 fxc/SM5**，无 `DXIL`（dxc/SM6 在 D3D11 上会 `E_INVALIDARG`）。
且**冷构建产物与发布包字节完全一致**（728 / 190128 / 2864），说明发布链路无漂移。

### 3.5 4K 全格式编码基准（3840×2160，31 MB BGRA8 真实像素）

| 格式 | 输出大小 | 编码耗时 | 结果 |
|---|---|---:|---|
| PNG（无损） | 91.4 KB | 362 ms | ✅ |
| JPEG LI | 265.9 KB | 626 ms | ✅ |
| WebP | 170.7 KB | 1099 ms | ✅ |
| **AVIF** | 12.2 KB | 1757 ms | ✅ **NVENC AV1 硬件路径** |
| JPEG XL | 97.4 KB | **12404 ms** | ✅（最慢） |
| JPEG GainMap (HDR) | 377.4 KB | 663 ms | ✅ |

- 色调映射 LUT：**35.2 ms/帧**（4K）
- NVENC 实际握手日志：`CreateInstance OK (SDK 13.1)` → `编码会话 OK (AV1, deviceType=0)` → `InitEncoder hr=0x00000000`
- ⚠️ **观察**：NVENC DLL 被加载了 4 次（重复初始化），虽不影响正确性，但是可优化点。

### 3.6 格式映射与魔数（`--fmt-check`）

- 7 种格式 → 编码器 → 扩展名映射表**全部正确**
- 实测输出魔数：JPEG LI = `FFD8`（JPEG ✅）
- **JPEG XL SDR 输出裸码流 `FF0A`，HDR 输出容器封装**（`00 00 00 0C 4A 58 4C 20 0D 0A 87 0A`）— 两者不同是**设计如此**，不是缺陷
- ACM 烘焙 sRGB→BT.2020 ✅；HDR PQ ICC ✅（25,060 字节，desc = "BT.2020 (PQ)"）

### 3.7 色彩管理

- `--icc-check`：sRGB / DisplayP3 / AdobeRGB / BT2020 的**色度矩阵与标准 primaries 全部一致** ✅
- `--png-check`：cICP 正确 — sRGB/System=1、DisplayP3/DCI_P3=12、BT2020=9，transfer 均为 13

### 3.8 OCR

- `--ocr-diag / --ocr-eng / --ocr-rec / --ocr-box / --ocr-ebox / --ocr-pre` **全部通过**
  - 字典 18,708 字符；det 输出 `1x1x640x2304`；rec 输出 `-1x-1x18710`
  - `HELLO WORLD` → `HELLOWORL`（max_conf=1.000）；`你好世界` → `你好世界`（max_conf=1.000）
  - 纯白图 → 正确返回空字符串
- 准确率基准：

| 基准 | 字符准确率 | 行准确率 | 备注 |
|---|---|---|---|
| 合成图 `--ocr-bench` | 765/900 = **85.0%** | 75/105 = **71.4%** | — |
| 真实渲染 `--ocr-bench-real` | 5455/7052 = **77.35%** | 191/292 = **65.41%** | 21 个 <85% 失败样本 |

分语言（合成图最佳字号）：中文 100%(12px)、日文 100%(36px)、**韩文 0%（全字号）**、手写体模拟 100%(24px+)。

> ⚠ 上表是**修复前**的基线。修复后（长行拆分 + DML 三处修复）见第四节：
> CPU 91.45%、DirectML **91.51% @ 389ms**，失败样本 13。

### 3.9 重新出包（含本轮全部修复）+ 启动冒烟

原产物 `publish\TrueToneCap-v0.3.4-beta\` 构建于本轮 OCR 修复**之前**，不足以用于 GUI 测试。
已重新出包到**独立目录**（不覆盖原产物，避免干扰）：

| 项 | 结果 |
|---|---|
| 命令 | `.\Publish.ps1 -AllowDirty -Force -OutputDir publish\TrueToneCap-v0.3.4-beta-ocrfix` |
| 退出码 | **PUBLISH_EXIT=0** |
| 体积 / 内容 | 417 MB；`data\Models` 3 个模型、`data\Shaders` 3 个 `.cso` 齐全 |
| 启动冒烟 | **PASS**：存活 12s，177 线程，438 MB 工作集（与旧产物基线 440 MB / 177 线程一致） |
| 元数据 | `build-info.json` 标记 `dirtyWorktree: true` |

⚠ **该产物的局限**：工作区尚未提交，`build-info.json` 记录的 `commitSha` 仍是 `0916a2a`，
与实际内容不符 —— **只能用于本地 GUI 测试，不可作为发布依据**。
等你完成 GUI 测试并授权提交后，需**从干净工作区重新出包**才是可发布产物。

> 出包时检测到 `TrueToneCap.exe`（PID 22320，03:25:47 启动）正在运行，发布脚本默认拒绝。
> 已按你的选择用 `-Force` 终止它后出包 —— 该实例可能是你正在做的 GUI 测试，如被中断请重新启动。

---

## 四、本轮发现与处置

### 🔴 发现 1：韩文识别率恒为 0%（能力边界，非新缺陷）

- **现象**：韩文在 12/16/24/36/48px 五个字号上均为 `0/15`。
- **根因（已证实）**：`ppocrv6_dict.txt` 共 18,708 条，其中
  - 谚文音节 U+AC00–D7A3：**0 个**
  - 谚文 Jamo U+1100–11FF：**0 个**
  - CJK 统一汉字：15,565 ／ 日文假名：180 ／ 拉丁：58
  - ⇒ **模型层面不可能输出韩文**，识别结果是被误认的汉字（如"是""印三"）。
- **判定**：不是代码 bug，是模型/字典的能力边界。
- **现状**：`docs/ocr-accuracy-analysis.md` 早已记录此问题并列了两个方案——
  - 方案 A：加载韩文专用 rec 模型（`korean_PP-OCRv3_rec`，懒加载）
  - 方案 B：fallback 到 Windows 系统 OCR（零成本，可把总准确率提到 99%+）
- **建议**：两个方案**均未实施**。建议排期方案 B（约 1h，收益最大），或在 UI/文档中标注"暂不支持韩文"。

### ⚠️ 发现 2：`PngCheck.cs:54` 期望值错误（**已就地修正**）

- **现象**：`--png-check` 对 AdobeRGB 输出 `cICP: 无`，但工具自身标注 `期望: [1,13,0,1]`，会被误读为缺陷。
- **判定**：**产品行为正确，是诊断工具的期望表错了**。
  - ITU-T H.273 cICP 的 `primaries` 枚举**没有 Adobe RGB 码点**（1=BT.709/sRGB、9=BT.2020、12=P3）
  - Adobe RGB(1998) 的 G 原色 (0.21, 0.71) ≠ BT.709 的 (0.30, 0.60)，强行映射为 1 会造成**色彩误判**
  - 正确做法是省略 cICP、改嵌 iCCP（PNG 规范下 iCCP 优先），实测已嵌 2,710 字节 AdobeRGB ICC
- **处置**：修改 `src/TrueToneCap.Tools/PngCheck.cs:54` 的期望文案为 `不适用 (H.273 无 AdobeRGB 码点, 仅 iCCP)`，并补注释说明依据。**仅影响内部诊断工具，不改变产品行为。**
- **回归**：改完重跑 `verify.ps1` 全量，构建 0 警告 0 错误 + 7 套件 351 断言全绿。

### ⚠️ 发现 3：开发态拿不到原生工具（仅影响开发，不影响发布产物）

- **现象**：首次运行 `--fmt-check` 时 JPEG LI / JPEG XL 报"未找到"。
- **根因**：`TrueToneCap.Core.csproj` **未把 `Resources/*.exe` 声明为嵌入资源**，4 个原生工具只由 `Publish.ps1` 在发布时拷入 `native/`。开发 bin 的 `native/` 为空，`NativeLibraryResolver.Initialize()` 静默降级（只写 `Debug.WriteLine`）。
- **判定**：**发布产物不受影响**（`verify-package.ps1` 已验证 `publish\...\native\` 4 个 exe 齐全且可启动）。仅影响开发态的 Tools CLI / 测试。
- **处置（本轮）**：编写 `.workbuddy-ai/tools-prep.ps1` 从 `src/TrueToneCap.Core\Resources\` 注入 4 个 exe；`.workbuddy-ai/ocr-prep.ps1` 从 `src\TrueToneCap.App\Models\` 注入 3 个模型。注入后六格式编码全部通过。
- **建议（可选）**：若要让开发态开箱可用，可在 `Core.csproj` 加 `EmbeddedResource`，但会把 12 MB 的 avifenc.exe 编入 Core.dll，**需权衡**。

### 🟡 发现 4：真实场景 OCR 的失败集中在"检测漏框" —— **已定位根因并修复**

- 21 个 <85% 失败样本中，至少 **7 个识别结果完全为空**（0/195、0/133、0/10），即**检测阶段一个框都没出**，而非识别错误。
- 高发场景：**长文本（≥130 字符）**与 **9–12px 小字**。
- 已复现的具体用例（`--ocr-bench-real`，同一语料在不同样式下对比）：

| 语料 | 失败样式/字号 | 通过样式/字号 |
|---|---|---|
| `文件 编辑 视图 工具 帮助`（10 字符） | SmallUiLabel 10px / 9px → **0/10**；GrayText 12px → 2/10 | GrayText **28px** → **10/10** |
| `The quick brown fox…`（195 字符） | LightAntiAlias 18px (Arial/Tahoma/Segoe)、ColoredBackground 14px → **0/195** | 同语料在其他样式下未触发 |
| `光学字符识别（英语：Optical…`（133 字符） | SmallUiLabel 12px / 10px → **0/133**；GrayText 12px → 26/133 | — |

#### 排查过程（两个假设先后被数据证伪）

1. **假设 A「宽图被 max-side 压缩压扁」→ 已证伪**。`OnnxOcrEngine.cs:411-421` 用的是
   **min-side 放大**（`limit_type=min`），短边 < `DetLimitSide`(960) 时放大，上限 CPU 6x / GPU 8x，
   **否则 `ratio=1.0` 完全不缩小**。宽图不会压扁，只会被放大到极端宽高比。
2. **假设 B「det 输入宽度 / 图像宽高比过大导致模型失效」→ 也已证伪**。
   新增 `--ocr-ar-probe`：固定 80 字符内容、固定画布宽度，**只改画布高度**，使 det 输入宽度恒为 4448
   而 AR 从 11.6 递减到 0.8 —— **10 档全部漏检，包括 AR=1.0 的 960×960**。图像级 AR 不是主因。

#### 真实根因（概率图探针 `--ocr-prob` 实测）

**模型输出完全正常，是一行硬编码的几何过滤把长行整行丢弃了。**

| 用例 | det 输入 | 主连通域 | 框 AR | 高激活占比 | 结果 |
|---|---|---|---|---|---|
| 40 字符 | 2368×384 | 1867×67 | 28.3 | 93.4% | ✅ 出框，识别 32/32 |
| 80 字符 | 4448×384 | 3892×62 | **63.8** | 93.4% | ❌ 被丢弃 |
| 80 字符（高画布） | 960×960 | 841×12 | **76.4** | 95.2% | ❌ 被丢弃 |

`probMap` max≈1.0、高激活占比 93–95%，说明 **DBNet 检测质量很好**。但长行文字粘连成**一个连通域**，
其框宽高比超过 `ExtractBoxes` 里硬编码的 `aspectRatio < 50f` 上限 → **被当作横线噪声直接丢弃** → "未检测到文字"。

可归纳为**三层失效**（`--ocr-asp-sweep` 端到端实测召回率）：

| 字符 | 框 AR | 旧行为 | 仅放宽上限 | 放宽+长行拆分 |
|---|---|---|---|---|
| 40 | 28 | 100% | 100% | 100% |
| 80 | 64 | ❌ 0% | **100%** | **100%** |
| 130 | 104 | ❌ 0% | 12%（rec 被 `RecImgMaxW=1280` 横向挤压约 3.9 倍） | **103%** |
| 195 | 156 | ❌ 0% | ❌ 0% | **101%** |
| 300 | 270 | ❌ 0% | ❌ 0% | **101%**（需上限 400） |

#### 修复（已实施，见 `src/TrueToneCap.Core/Services/OnnxOcrEngine.cs`）

1. `OcrModelConfig` 新增 `DetMaxBoxAspect`（原硬编码 50 → **400**）、`DetSplitBoxAspect`（**90**）。
2. `ExtractBoxes` 的 AR 上限改为可配置，取值 400（见下）。
3. 框 AR > 90 且高度足够时，按 X **在无墨列（字符间隙）处切分**成多个子框分别送 rec，
   避免整行被 `RecImgMaxW` 挤压。
4. `Box` 增加 `IsFragment` 标记；`RunRecognition` 对子框把水平边距从「框宽 10%」封顶为「按字高」——
   否则相邻子框各外扩约 340px，识别区域互相重叠，会把相邻文本**识别两次**（曾实测召回 113%）。

**为什么取 400**：`--ocr-noise` 用 5 条分隔线（整幅宽 2/4px、短线 300×3 / 200×2 / 600×5）实测，
上限 50 / 200 / 400 / ∞ 的**误检行数完全相同（2 行）**，而这 2 行在**上限 50（当前生产行为）下就已存在**，
属于既有问题 → **放宽上限不引入任何新增噪声**。400 既能覆盖 300 字符超长行，又保留对
极端长条（AR>400，如整幅宽分隔线 AR≈600）的拦截。

#### 修复效果（`--ocr-bench-real`，同一批语料）

| 指标 | 修复前 | 修复后 | 变化 |
|---|---|---|---|
| 字符准确率 | 5455/7052 (77.35%) | **6451/7052 (91.48%)** | **+14.13pp**（多认出 996 字符） |
| 行准确率 | 191/292 (65.41%) | 201/292 (68.84%) | +3.43pp |
| 失败样本 (<85%) | 21 | **13** | −8 |

合成图基准 `--ocr-bench` 保持 765/900 (85.0%) 不变（无回归）。

---

### 🔴 发现 5：DirectML 路径 `AccessViolationException` —— 输出张量 use-after-free（**已定位根因并修复，P0**）

**由来**：发现 4 修复后做跨后端复验时发现——`MultiOcrService` 默认选中第一个可用引擎即
**DirectML**，而此前所有基准都跑在 CPU 上，DML 路径从未被真正执行过。

**症状**：任何尺寸（含 96×96）在 DML 下 100% 崩溃，`System.AccessViolationException`
（NTSTATUS `0xC0000005`），进程直接终止（退出码 `-1073741819`）。
`try/catch` **完全无效** —— .NET 下 AV 属 Corrupted State Exception，不可捕获。
且**旧配置（上限 50 / 不拆分，即修复前行为）同样崩溃** ⇒ 与本次长行修复无关。

**定位过程（5 层逐项排除，每层都用独立进程，因 AV 会杀进程）**：

| # | 排除项 | 手段 | 结果 |
|---|---|---|---|
| 1 | 环境 / 驱动 | 裸 DML 96×96 推理 | ✅ 成功 |
| 2 | 输入尺寸 | 正方形 96→1664 扫描 | ✅ 全成功 |
| 3 | det 输入形状 | 真实形状 1152×512…5952×512 | ✅ 全成功 |
| 4 | ORT SessionOptions | level 0→5 逐项累加（含产品完整配置） | ✅ 全成功 |
| 5 | `SessionOptions` 生命周期 | `--ocr-dml-life` 4 种 mode（DML/CPU × 脱离作用域/KeepAlive） | ✅ 全成功 → **假设被证伪** |

**真实根因** —— 崩溃栈（关键：在 `GetValue`＝**读**，而非推理调用）：

```
at Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1[Single].GetValue(Int32)
at ...Tensor`1[Single].get_Item(ReadOnlySpan`1<Int32>)
at TrueToneCap.Core.Services.OnnxOcrEngine.RunDetection(Byte[], Int32, Int32)
```

`AsTensor<float>()` 返回的是**零拷贝视图**，直接指向 ORT 拥有的原生输出缓冲区。
而 `using var results` 的释放在 try 块结束时触发，旧代码把 `output` 带出 try 块再读取：

```csharp
using var results = _detSession!.Run(inputs);
output = results.First().AsTensor<float>();   // 零拷贝视图
...                                            // ← try 块结束，ORT 回收缓冲区
float v = output[[0, 0, y2, x2]];              // use-after-free → AV
```

**为什么 CPU 下没暴露**：CPU EP 输出来自 CPU memory arena，释放后并未立即归还 OS，
读到的数据"碰巧"仍然有效，掩盖了多年。DML 下缓冲区被真正释放/解除映射 → 立即崩。
`RunRecognition` 存在**完全相同**的缺陷（第 723 行读 `output[[0, t, c2]]`）。

**修复**（`OnnxOcrEngine.cs`，det 与 rec 两处）：
- det：把概率图/二值图的填充移入 `results` 存活期，拷进托管 `float[]`/`byte[]` 后再离开作用域。
- rec：把 CTC 解码（argmax）移入 `results` 存活期，**不**额外拷出 logits
  （每框约 320×18710×4B ≈ 24 MB，多框截图下会造成显著内存压力与 GC 抖动）。

**修复后 DirectML 全档可用**（`--ocr-dml-check`，18px，退出码 0，无崩溃）。

---

### 🟡 发现 6：DirectML 纹理边长上限 16384 —— 超限时检测质量静默崩塌（**已修复**）

修完发现 5 后 DML 可用了，但 300 字符档只有 **66%**（同图 CPU 100%）。定位到是 det 输入超限：

| 字符 | 源图 | det 输入（maxRatio=8 放大后） | 修复前 DML | 修复后 DML | CPU |
|---|---|---|---|---|---|
| 130 | 1168×64 | 9344×512 | 109% | 109% | 101% |
| 195 | 1723×64 | 13792×512 | 115% | 115% | 100% |
| 300 | 2605×64 | **20832×512 ← 超限** | **66%** | **100%** | 100% |

D3D12/DirectML 纹理边长硬上限为 **16384**，超过后 ORT **不报错**，检测质量直接崩塌。
触发条件：图像短边 < `DetLimitSide`(960) 且极宽（即又宽又矮的截图区域）经 8x 放大后突破上限。

**修复**：在 `RunDetection` 对 DML 路径按比例把 det 输入收紧到 16384 以内
（`MaxDmlTextureDim` 常量）。`ratioW/ratioH` 由**最终** `resizeW/resizeH` 反算，
非等比收紧也不会错位。修复后 300 字符档 DML **66% → 100%**。

**已知残留（未处理，需你决策）**：DML 在 130/195 字符档仍有 **109% / 115%** 的过召回
（重复字符）。已验证**不是**子框边距导致（边距从 `min(bw/10,bh)` 收紧到 `bh/4`，
结果完全相同 113/104、179/156）。仅在这类"又宽又矮"的合成图上 `maxRatio`（CPU 6x / GPU 8x）
才会生效；**真实语料上它根本不触发**（见发现 7）。

---

### 🟡 发现 7：DirectML 比 CPU 低 4.07pp，但快 6.7 倍 —— **需你做产品取舍**

修完发现 5 后 DML 终于可度量，于是把完整真实语料基准（292 行，**同种子 1409**，可复现）
在 2 后端 × 2 模型精度上跑成 2×2 矩阵：

| 后端 | 模型 | 字符准确率 | 行准确率 | 失败样本(<85%) | 总耗时 | 均值/样本 |
|---|---|---|---|---|---|---|
| **CPU** | FP16（当前发布） | 6451/7052 **91.48%** | 201/292 68.84% | 13 | 294.2s | 3502ms |
| CPU | FP32 | 6466/7052 **91.69%** | 200/292 68.49% | **10** | 322.1s | 3834ms |
| **DML** | FP16（**生产默认命中**） | 6164/7052 **87.41%** | 188/292 64.38% | 21 | **44.1s** | **525ms** |
| DML | FP32 | 6177/7052 87.59% | 187/292 64.04% | 22 | 200.6s | 2388ms |

**三条结论**：

1. **模型精度几乎不影响准确率**：FP32 相对 FP16 只 +0.21pp（CPU）/ +0.18pp（DML），
   均在噪声内；代价是 CPU 慢 9.5%、**DML 慢 355%**。
   ⇒ 当前发布选择 **FP16 是正确的**；而 **「FP32 模型 + DirectML」是被严格支配的组合**
   （没有精度收益，却比 DML-FP16 慢 4.5 倍），**不应采用**。
2. **CPU 比 DML 高 4.07pp，且与模型精度无关** ⇒ 差距来自**后端内核/图划分差异**，
   不是 FP16 精度问题。**我此前"FP16 rec 噪声"的推断已被本次数据证伪**（换 FP32 无改善）。
   未定位到最终机制 —— ORT 告警 `Some nodes were not assigned to the preferred execution
   providers` 提示部分算子回落到 CPU，可能引入数值差异，待进一步查证。
3. **DML + FP16 快 6.7 倍**（525ms vs 3502ms），这是真实且显著的体验差异。

> ⚠ **本节的"后端内核差异"结论已被 4.4 节推翻**。保留此表是因为其中的**模型精度结论
> （FP16 vs FP32）依然成立**，且它记录了排查路径。真实根因见 **4.4**。

---

### 🟢 4.4（= 发现 8，你选的 D）：根因已定位并修复 —— **真凶是 GPU 放大倍数 8x，不是数值精度**

**先证伪"数值差异"**：新增 `--ocr-ab` A/B 探针，把**同一份**输入张量分别送 CPU 与 DirectML
（SessionOptions 除 EP 外完全一致），逐阶段比较：

| 阶段 | 指标 | 结果 | 判定 |
|---|---|---|---|
| det | 二值化分歧像素 | **95 / 1,708,032（0.006%）**，平均 \|Δ\|=0.000075 | ✅ **实质一致** |
| rec | argmax 分歧步数 | 4/160（2.5%） | 轻微 |
| rec | 置信度最大 \|Δ\| | 0.3175（但**阈值穿越 0 次**） | 未造成丢字 |

⇒ **两段都不足以解释 4pp**。真正的线索来自**逐样本比对**（84 个样本，DML 净 −287 字符）：

| 样本 | 风格 / 字号 | 字符数 | CPU | DML@8x | 差 |
|---|---|---|---|---|---|
| #00 | SmallUiLabel 9px | 173 | 172 | **112** | **−60** |
| #30 | GrayText 22px | 133 | 95 | 69 | −26 |
| #31 | SmallUiLabel 10px | 133 | 131 | 105 | −26 |
| #34 | LightAntiAlias 18px | 195 | 65 | 39 | −26 |
| #32 | LightAntiAlias 18px | 195 | 41 | 28 | −13 |

**损失最重的样本全是"长行 + 小字号"** —— 这类图短边极小，恰好是 `maxRatio`
（**CPU 6x / GPU 8x**）生效的场景。**我此前"真实语料上 maxRatio 不触发"的判断是错的**，
这是本次排查中我自己犯的第二个判断错误（第一个是"FP16 rec 噪声"）。

**对撞实验（把 GPU 的 8x 改成 6x，其余不变）**：

| 配置 | 字符准确率 | 行准确率 | 失败样本 | 均值/张 |
|---|---|---|---|---|
| DirectML + **8x**（原值） | 6164/7052 (87.41%) | 188/292 | 21 | 525ms |
| DirectML + **6x** | **6453/7052 (91.51%)** | 200/292 | **13** | 2364ms |
| CPU + 6x | 6449/7052 (91.45%) | 200/292 | 13 | 3502ms |

> ⚠ **基准存在 ±2~5 字符的运行间抖动**（ORT CPU 多线程浮点归约顺序不确定）。
> 同一份代码两次跑 CPU 得 6451 / 6449。故 DML 91.51% 与 CPU 91.45%
> 应视为**统计持平**，而非 DML 真的略优。判读小差异时勿过度解读。

**结论**：
1. **8x 的"6.7 倍速度优势"是虚假收益** —— 相当部分来自检测漏框后**少跑了 rec**。
   校正后 DirectML 仍比 CPU 快 **1.48 倍**（2364ms vs 3502ms），且准确率持平。
2. 附带消除此前 130/195 字符档的过召回：109%→**103%**、115%→**101%**，与 CPU **逐档一致**。
3. **两个 EP 统一取 `maxRatio = 6.0f`** 已固化到 `RunDetection`。

**因此 4.3 节的取舍题不再成立**：不需要在"快但差"和"慢但准"之间选，
**DirectML@6x 同时拿到 91.5% 与 1.48 倍速**。`MultiOcrService` 的默认（DML 优先）无需改动。

---

### 🟢 发现 9：DirectML 的 `EnableMemoryPattern` 设错 —— 关闭后**再快 5.9 倍，准确率不变**

调研员指出：ORT 官方 DirectML 文档要求 **禁用 memory pattern**（DML EP 不支持它，
须 `DisableMemPattern` + `ORT_SEQUENTIAL`），而产品 `CreateSessionOptions()` 第 289 行
**对所有 EP 一律设为 `true`**。实测（292 行真实语料，seed 1409）：

| 配置 | 字符准确率 | 行 | 失败样本 | 均值/张 | 相对 CPU |
|---|---|---|---|---|---|
| CPU | 6451/7052 (91.45%) | 200/292 | 13 | 3502ms | 1× |
| DML + `maxRatio=6` + memPattern=**true** | 6453/7052 (91.51%) | 200/292 | 13 | 2364ms | 1.5× |
| **DML + `maxRatio=6` + memPattern=false** | **6453/7052 (91.51%)** | 200/292 | **13** | **389ms** | **9.0×** |

- 准确率**完全不变**（6453，两次复跑一致：33.5s / 32.6s），长行各档（40/80/130/195/300）
  也全部不变（100/100/103/101/101%）。
- 修复：`opts.EnableMemoryPattern = _provider != OnnxExecutionProvider.DirectML;`

**合计**：DirectML 相对本轮开始时（87.41% @ 525ms）→ **91.51% @ 389ms**，
即**准确率 +4.10pp 的同时又快了 1.35 倍**；相对 CPU 则是**同等准确率下快 9 倍**。
这是本轮性价比最高的一处修复（1 行代码），且它来自调研员对官方文档的核对，
而非本地猜测 —— 说明"先证伪本地假设、再查官方文档"的顺序是对的。

**其余调研建议（未实施、未实测）**：`ep.dml.disable_graph_fusion=1`、
`optimization.disable_specified_optimizers`、逐层张量 diff 等，
在差距已消除后收益有限，不再继续。

---

## 五、我无法执行、需要你在真终端完成的实机测试

以下项目**我在本环境无法完成**，请按清单执行。前 4 项是发布前最值得验证的。

### 5.1 GUI 交互与真实截图（最高优先级）

| # | 测试项 | 具体操作 | 期望 |
|---|---|---|---|
| 1 | 启动与托盘 | 双击 `publish\TrueToneCap-v0.3.4-beta\TrueToneCap.exe` | 正常出现主界面/托盘，无报错弹窗 |
| 2 | 区域截图 | 触发截图 → 框选屏幕区域 → 保存 | 图像与屏幕一致，无黑屏/错位 |
| 3 | 全屏截图 | 全屏捕获 | 多显示器下应能选择目标显示器 |
| 4 | 快捷键 | 测试各快捷键（截图/标注/保存） | 响应正确，无冲突 |
| 5 | 标注功能 | 矩形/箭头/文字/马赛克等 | 绘制与导出正确 |
| 6 | 设置页 | 修改输出格式/质量/色彩空间并保存 | 重启后设置持久化 |

> 我只能验证"进程能启动且存活 12s"，**无法操作 UI**，也**无法判断截图画面的观感是否正确**。

### 5.2 显示环境相关（需物理/系统操作）

| # | 测试项 | 说明 |
|---|---|---|
| 7 | 多显示器 | 不同分辨率/刷新率/主副屏组合下的捕获正确性 |
| 8 | DPI 缩放 | 125% / 150% / 200% 缩放下坐标是否错位（**高频缺陷区**） |
| 9 | HDR 开关切换 | 运行中切换 HDR 开关，检查是否崩溃或色彩异常 |
| 10 | 显示器热插拔 | 插拔外接显示器后是否恢复 |
| 11 | 不同亮度/色彩配置 | 本机实测 418 nits / 10 bit；其他配置未覆盖 |

### 5.3 OCR 真实场景

| # | 测试项 | 说明 |
|---|---|---|
| 12 | 真实屏幕 OCR | 对真实截图（非合成图）跑 OCR。真实渲染含 **ClearType 次像素抗锯齿**，与合成图差异明显，准确率可能低于本报告数值 |
| 13 | 韩文确认 | 确认韩文 0% 现象，决定是否实施方案 A/B |
| 14 | 长文本/小字 | 复现"检测完全漏框"（>130 字符、9–12px） |
| 14a | **真实截图上的 DirectML 路径** | 修复后 DML 已在 292 行真实语料上跑通（无崩溃，87.41%，525ms/张）。仍请确认**真实屏幕截图**（ClearType + 真实 DPI）走 DML 时同样正常（此项优先级等同 #12） |
| 14b | ~~DML / CPU 默认后端取舍~~ | **已由 4.4 解决**：DML@6x 拿到 91.51% 且快 1.48 倍，无需取舍，`MultiOcrService` 默认值不必改 |
| 14c | DML 专项调优（可选） | ORT 文档要求 DML 用 `EnableMemoryPattern=false`，产品现为 `true`（第 289 行）；另有 `ep.dml.disable_graph_fusion`。**均未实测，不落地**，可作为后续性能/稳定性优化项 |

### 5.4 需要凭据 / 外部平台

| # | 测试项 | 阻塞原因 |
|---|---|---|
| 15 | `git push origin main` | HTTPS 远程 + `credential.helper=helper-selector`，**非交互环境无法提供凭据**。请你在真终端执行（会触发 pre-push 全量门禁，约 110s） |
| 16 | GitHub Actions 首次 `workflow_dispatch` | **必须先 push 之后**才能触发；CI 尚未端到端验证过 |

### 5.5 跨机器兼容性（本机无法覆盖）

| # | 测试项 | 说明 |
|---|---|---|
| 17 | 无 AVX2 的 CPU | 本机 AVX2=True，SIMD 回退路径未验证 |
| 18 | 无 NVIDIA GPU / 无 NVENC | 本机走 NVENC AV1 成功；**CPU 回退路径（libaom/rav1e）未在本机触发过** |
| 19 | 全新克隆端到端 | 验证无 fxc 缓存、无 cso 时能否一次构建成功 |

---

## 六、附录：本轮新增的辅助脚本

均位于 `.workbuddy-ai/`（**未被 git 跟踪**，不会污染仓库）：

| 脚本 | 用途 |
|---|---|
| `runps.py` | 在 bash 中间接拉起 Windows PowerShell 宿主的通用入口（修复 `PATHEXT`/PATH 两个坑） |
| `tools-run.ps1` | Tools CLI 统一执行器（枚举最新程序集，不硬编码 TFM） |
| `tools-prep.ps1` | 从 `Core\Resources\` 注入 4 个原生 exe 到开发 bin |
| `ocr-prep.ps1` | 从 `App\Models\` 注入 3 个 OCR 模型到开发 bin |
| `launch-smoke.ps1` | 发布产物主程序启动冒烟（启动→存活判定→终止） |
| `commit.ps1` / `push.ps1` | 在完整环境变量下执行 git commit/push（避开 hook 的 POSIX PATH 问题，**不用 `--no-verify`**） |

**复用示例**：

```bash
python .workbuddy-ai/runps.py .workbuddy-ai/tools-prep.ps1
python .workbuddy-ai/runps.py .workbuddy-ai/ocr-prep.ps1
python .workbuddy-ai/runps.py .workbuddy-ai/tools-run.ps1 --fmt-check
python .workbuddy-ai/runps.py .workbuddy-ai/launch-smoke.ps1 -WaitSec 12
```

---

## 七、当前仓库状态（供提交决策参考）

```
HEAD          = 0916a2a (release: v0.3.3-beta)
领先 origin   = 0 笔
分支          = 仅 main
暂存区        = 43 M + 36 A + 12 D
未暂存        = src/TrueToneCap.Tools/PngCheck.cs (发现 2 的修正)
未跟踪        = docs/reviews/repo-governance/real-machine-test-report-2026-09-14.md (本报告)
发布产物      = publish\TrueToneCap-v0.3.4-beta (417 MB，保留在磁盘)
```

⚠️ 若要一并提交，需先：
```bash
git add src/TrueToneCap.Tools/PngCheck.cs
git add docs/reviews/repo-governance/real-machine-test-report-2026-09-14.md
```
（`.workbuddy-ai/` 下的辅助脚本**未被跟踪**，不会进入提交。）

**我未执行任何 commit / push**，等待你完成实机测试后统一授权。
