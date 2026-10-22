# 管线完整审查与可用性分析 · 执行计划（2026-09-27，v2 细化版）

- **基线坐标**：`v0.3.6-beta`。`git status --porcelain` 为空；附注标签 `v0.3.6-beta` 钉在发布提交 `4368eed`，HEAD `ca7ed99` 与之只差文档（交接 §63.6 已证包内容与标签逐字一致）。
- **本文性质**：计划。本轮不改源码、不提交。批次 A 起才动手。
- v1（同日初版）的四个待拍板问题已在本轮自行定死，见 §7；v2 相对 v1 的实质变化是找到了**判据的结构性缺口**（§3），骨架因此重排。

---

## 1. 审查目标（四条，每条都要能判定"答完没"）

| # | 目标 | 什么算答完（合格线） |
|---|---|---|
| **G1** | 管线每一环的**产出**成立：捕获 → 色彩/色调映射 → 编码 → 落盘/剪贴板 | 每个"格式 × 色域 × 位深 × HDR 开关"格子有一枚真实产物，且它的**声明层读数、码流真身、外部解码像素**三方一致；不一致的格子逐条给结论分类 |
| **G2** | **降级是否可见**：产物与用户当时所选不符时，应用有没有说话 | 一张静默降级台账（§5 交付物 D2），每行八个观测列，且 D2/D3 档每点都有"把它逼出来"的触发构造与实测读数，不是读码推断 |
| **G3** | **判据本身可信**：1300+ 条全绿的锁里，有多少其实"没在判" | 逐条标注三级强度（§3），并把"恒 SKIP / 恒真 / 只判源码文本却断言运行时行为"三类空转锁点名列出，给出补牙后的变异实测 |
| **G4** | **可用性评级**：用户在各真实场景拿到的是不是他要的 | 一张分场景表（§4 L6 + 交付物 D4），每格 = 一个产物 + 一条日志行 + 一张证据截图，缺一项不写"健康"；字母评级只在证据齐的格子里给 |

不回答的问题：性能优化、新功能设计、构建/依赖/CI 治理（那是 `docs/reviews/pipeline-review/pipeline-review-2026-09-15.md` 的地盘）。

---

## 2. 范围

### 2.1 在范围内

六条 lane 的全部代码面（§4），以及：`scripts/verify.ps1` 门禁链、`scripts/precision/*.py` 外部裁判、`tests/manual/` 既有驱动件、`Publish.ps1` / `scripts/pack.ps1` 的产物内容面（只看包内容与声明是否相符，不动发布流程）。

### 2.2 明确不在范围内

- 不改色彩管线的**输出形态**（0.3.6 的三档策略与 Rec.2020 收口是已拍板口径，本轮只验证实现是否配得上口径）。
- 不回写历史快照正文：`pipeline-and-usability-review-2026-09-15.md`、`pipeline-review-2026-09-15.md`、`pipeline-e2e-test-plan-2026-09-20.md` 都是带日期的历史；只在 D1 报告里引用并判"已过期"。
- 不 commit / 不 push / 不打 tag（逐次授权，且授权前本机验证要跑完）。
- 不新增产品功能、不新增设置项、不放宽任何现有断言（要放宽先说明并等授权）。

### 2.3 为什么现在重做（基线已漂三版）

| 变化（v0.3.4-beta → v0.3.6-beta） | 对审查的直接影响 |
|---|---|
| 默认编码器 JPEG LI → **PNG**（差量写盘 ⇒ 只影响盘上没有格式键的人） | 默认路径与显式选择路径不再是同一条，"首运行无配置文件"必须单列格子 |
| **格式下拉索引错位事故**（点「JPEG LI」出 JXL、点「WebP」出 TIFF、真 TIFF 选不到），项序数量收成唯一来源 `Core/Settings/FormatOrder.cs` | 界面标签数组与编码侧解释顺序是两面，B 域（手写 `settings.json`）是重点 |
| 目标色域界面只叫 Rec.709 / Rec.2020，**盘上值仍是 `sRGB` / `BT2020`**；AdobeRGB 收口进 Rec.2020 | 名字与键两套 ⇒ 迁移表 / 索引匹配 / 声明文案任一处漏同步就自相矛盾 |
| **JXL HDR + DisplayP3/DCI_P3 改递 PQ ICC**（只动声明不动像素，192 B → 209 B）；两枚 P3 在 HDR 侧仍同字节 | HDR 两格是新口径，要外部裁判独立复算；要不要区分属产品决定（§62.8） |
| AVIF **编码器选项卡整张卡片看不见**：构建/日志/测试全不响，靠"以收起起步的命名控件必须存在点亮它的那一行"才抓住 | 可复用形状：**界面上少了东西而门禁不响**，本轮按同形状扫别的收起卡 |
| 界面中文收成一条机制（显式扫描面 + 表外中文直接报红 + ToolTip 48 处入表） | 英文模式**至今零次真机读数**；告警计数"英文模式下仍有 N 处显示中文"的 N 从未读到过 |
| BMP 从格式面与支持面删除并留反向断言；`publish/` 清理 + 打包规范成文（§55.8） | "门禁测一个用户拿不到的格式"这类残件再扫一遍 |
| **2026-09-25 本机接上真 HDR 屏**（分类 Hdr、EDID ΔG=0.0963、10-bit、418 nits）；`jxlinfo`/`djxl` 本机其实存在（§62.1） | 旧报告里"本机无 HDR 面板 / 本机无 JXL 解码器所以测不了"的条目**全部过期**——本轮第一次能自己量 HDR 侧与屏幕锚定像素 |

---

## 3. 方法（本轮的骨架：判据分三级强度，先认清谁在判）

### 3.1 一条结构性事实（本轮实测得到，它改变了整个计划）

门禁的 1300+ 条断言**跑不到 App 侧的代码**：

- `src/TrueToneCap.Test/*.csproj` 与 `src/TrueToneCap.Tools/*.csproj` **只引用 Core**，没有 `ProjectReference` 到 `TrueToneCap.App`；
- App 侧规则是靠**读源码文本**判的：`CorePipelineTests.cs` 里有 **80 处 `File.ReadAllText`**；
- App 侧真正的无头执行早已存在，但入口在产品进程里：`App/Services/SelfTest.cs` 直接 `new WgcCaptureService()`（`:218`）、`new CapturePipelineService(new SettingsService())`（`:301`）、调 `CapturePipelineService.PreparePixelsWithIcc`（`:134`），CI 用 `& $exe --selftest --out …`（`.github/workflows/release.yml:68-71`）；
- 而 `SelfTest.cs:302` 自己写着用 `new AppSettingsData()` 默认快照、"只看 HDR 落点，不看质量档" ⇒ 真身进程判据**覆盖面不等于用户所选面**。

⇒ 这就是 09-15"368 断言全绿但 4 个 P0 成立"、§57"整张卡片消失而全绿"的**共同根因**：文本哨兵能证明"这行字在源码里出现过"，证明不了"这行代码被跑到过"。界面中文那条链上"代码里出现过 ≠ 会被翻译"是同一形状。

### 3.2 因此本轮所有结论按三级强度定级，报告里每一格必须标明它住在哪一级

| 级 | 怎么判 | 能证什么 | 不能证什么 | 本轮怎么办 |
|---|---|---|---|---|
| **T1 执行判据** | 真跑代码（Core 的 7 套件、Tools 探针、`py -3` 外部裁判读产物） | 数值/容器/码流的正确性 | 不经 App 捕获入口与 UI 组合 | 尽量把结论提级到 T1 |
| **T2 源码文本哨兵** | `File.ReadAllText` 匹配（80 处） | 命名一致性、"必须存在某一行的接线"、翻译表覆盖 | **运行时行为**：字符串在源码里出现过，不代表它被执行 | 逐条问"它断言的是文本还是行为"；断行为的要么提级到 T1/T3，要么在报告里明写"这条只证到文本层" |
| **T3 真机进程判据** | 起产品进程（`--selftest` / `--display-audit-emit` / `--hdr-probe`）或 GUI 驱动 + 外部取证 | App 侧端到端行为、剪贴板、overlay、热键 | 换机不可迁移，结论须绑定本机显示器态 | 优先级最高但最贵，且可能被权限分类器拦（§7 决策 D3） |

**判据形状沿用三方对齐**（09-20 §1，不重设计）：请求（`FileNamePrefix` 写 `E2E<caseId>_` 当关联键 + 一份 `TrueToneCap.settings.json` 快照）／实测（外部解码器 + 像素比对 + 规范算术，不经产品代码）／自述（`log/app_*.log` 全文 + toast + 状态栏 + sentinel）。四类结论里只抓"请求 ≠ 实测且自述沉默"，同时把"请求 == 实测却报错"（假警报）也入账。

**六层独立裁判**沿用 09-20 §3（L1 声明层 / L2 码流 vs 头 / L3 屏幕锚定像素 / L4 外部消费者 / L5 规范算术 / L6 变异实验），每层自带自检；本轮两处更新：**L3 与 HDR 观感在本机可做**、**JXL 的 ICC 不再是"无裁判"**。

**每把新锁都要量牙**：把产品改回旧写法 ⇒ 当场红并点名哪一格，随后还原重编重跑；验收必须包含"门禁那一行写的是【判了】而不是【跳过/没判】"，`exit 0` 不区分这两件事（§62.7）。**断言条数不拿上轮当基线**，一律用批次 A 当场取到的数（§6 P1）。

---

## 4. 范围切分：六条 lane，每片给可执行判据

按文件切；判据（`tests/*.cs`、`scripts/precision/*.py`）与文档留主循环写，不外派。

| lane | 文件面 | 要回答 | 可执行判据（这片算完的条件） |
|---|---|---|---|
| **L1 捕获** | `App/Services/WgcCaptureService.cs`、`Core/Capture/DisplayInfo.cs`、`App/Services/HotkeyManager.cs`、`Core/Detection/RegionDetector.cs` | 帧来源与像素形态（BGRA8 / scRGB / Float16）怎么选、什么时候整块丢弃退 SDR；HLG 只被当 PQ 要不要判 | ① 每屏各拍一张，产物尺寸与该屏物理像素逐屏对上（防 09-15 P0-A 家族回退）；② Float16 冷却 10 张那条（`:837-856`）用连拍序列量出"同样设置先出 HDR 后出 SDR"的翻转点；③ 会话尺寸不匹配（`:1071-1077`）的退 SDR 有几种触发路径，各自给日志级别 |
| **L2 色彩与色调映射** | `Core/ColorManagement/ColorProfileProvider.cs`、`Core/Encoding/IccStore.cs`、`Core/Processing/ToneMapper.cs` / `GpuToneMapper.cs` / `ShaderLoader.cs`、`App/Shaders/ToneMapping.hlsl`、`App/MainWindow.ColorManagement.cs` | CPU 与 GPU 曲线是否仍一致（09-15 P0-C 的同族漂移会不会再来）；面板 EDID/ICC/WCS 三源取档失败时给用户什么；声明与像素同不同向 | ① CPU↔HLSL 逐点数值比对（y∈[0,4]，y=1 附近加密），红则给差值表；② 拔掉面板 ICC 关联制造 WCS 取档失败，读回产物 ICC 与界面提示；③ 每个下拉色域 Tag → 矩阵非 null 的穷尽锁仍在且有牙（复跑变异） |
| **L3 编码与容器声明** | `Core/Encoding/` 全部（`FormatEncoders.cs`、`Native{Avif,Jxl,WebP}Encoder.cs`、`JpegLiNative.cs`、`JpegGainMapEncoder.cs`、`Managed{Png,Tiff}Encoder.cs`、`MftEncoderNative.cs`、`NvEncoderNative.cs`、`AvifEncoder{Capabilities,Probe}.cs`、`AvifHardwareProbe.cs`、`GpuCapability.cs`、`EncoderCrashSentinel.cs`、`EncoderFallbackNotifier.cs`、`FormatColorFacts.cs`、`AtomicFileWriter.cs`）、`Core/Settings/FormatOrder.cs` | 每种格式在每档下**声明写了什么、码流里是什么、外部解码得到什么**；后端协商与静默 forfeit 硬件加速的代价；保底路径返回的是不是真文件 | ① 全矩阵样张逐格三层读数，不符项逐条给 T 级；② 后端阶梯按 4 后端 × 档位复算并与盘上产物 md5 对齐；③ 每条"编码后返回值 `File.Exists` 且后缀=声明格式"的契约锁覆盖全部保底分支；④ 子进程超时都有 `Kill(entireProcessTree)`——已量到 `NativeAvifEncoder.cs:283,289` 与 `NativeEncoderGuard.cs:56` 有，**`NativeJxlEncoder.cs` / `JpegLiNative.cs` / `NativeWebPEncoder.cs` 三处没有**，补锁 + 补牙 |
| **L4 输出与剪贴板** | `App/Services/CapturePipelineService.cs`（落盘与目的地段）、`App/MainWindow.xaml.cs`（复制 / Toast / 状态栏段）、`App/Services/ToastService.cs`、`App/SilentCaptureToast.xaml.cs` | 命名与归档、重名覆盖、剪贴板各目的地可用性、通知通道自身吞事件 | ① 已量到命名点 `CapturePipelineService.cs:501` = `{FileNamePrefix}{yyyyMMdd_HHmmssfff}{ext}`，全仓无存在性检测 ⇒ 连拍/并发同毫秒必须实测出覆盖，再定修复形状；② 剪贴板双通道对 6 种格式逐个测"粘进聊天框/Word/画图有没有反应"，失败时状态栏与日志各写了什么；③ toast 每进程只弹一次（`s_hwFallbackNotified`）与 `EncoderFallbackNotifier` 单槽读后即清，按 D0 档单独定性 |
| **L5 设置与能力协商** | `App/Services/SettingsService.cs`、`App/Models/AppSettingsData.cs`、`Core/Settings/SettingsDiffPruner.cs` / `SettingsWriteProbe.cs`、`App/Services/CapabilityService.cs` / `SecretStore.cs` / `StartupManager.cs`、`Core/Services/NativeLibraryResolver.cs` | 差量写盘与修订号交还门、只读安装目录、缺键回落、`Normalize()` 只在启动跑一次留下的 B 域入口、native 释放失败后的能力面 | ① 设置读的是**同目录那一份**已确认（`SettingsService.cs:26` = `AppContext.BaseDirectory`），但盘上另有 `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json`(1461 B,09-16) 与 `settings.json`(117 B,09-24) 两份遗留件 ⇒ 逐一定性是谁写的、会不会被读到；② 只读目录那把锁（`CorePipelineTests.cs:2670` 的 denied 目录）复跑并补"保存失败必须可见"的行为判据；③ 手改 JSON 越界值（`FormatIndex=99/-1`、`ColorSpaceIndex>2`、`QualityAvif=0`、`Archive=Week`）逐个量"界面显示 vs 实际编码" |
| **L6 UI 与后处理可用性** | `App/MainWindow.xaml` + `MainWindow.{Settings,OcrSettings,Localization}.cs`、`SelectionOverlay.xaml.cs`、`AnnotationWindow` / `Annotation{Manager,Layer}.cs` / `AnnotationRasterizer.cs`、`OcrPreviewWindow`、`HdrCaptureWindow.cs` / `HDRPreviewWindow.cs`、`TrayIconManager.cs`、`Core/Services/{MultiOcrService,OnnxOcrEngine,OcrService,TranslationService,BitmapPreprocessor}.cs` | 每张"以收起起步"的卡都有点亮行吗；两套 overlay 的键鼠差异；标注层在哪些目的地被合成；OCR 引擎静默换、看门狗丢弃不取消；翻译三档里 `Vision` 有没有代码路径、凭据有没有 UI 入口 | ① 收起卡判据复跑（09-15 记 16 控件/孤儿 0）并把每张卡的实际可见尺寸量出来（布局判据要量出货构型，不能只读设计尺寸）；② 英文模式跑一次，把"N 处显示中文"那个计数第一次读出来；③ 打马赛克后按识字，量 OCR 读的是合成前还是合成后的像素；④ 逐功能"热键注册在哪一行"表（`RecordHotkey` 只有文本管道那条要复核现状） |

**并行纪律**（§62.7 付过钱的三条）：两条 lane 同抢一份 `obj/bin` 时，派工那一刻就把 `ArtifactsPath` 讲定或改串行；lane 收回后**先 `ls` + sha 再看它说了什么**，然后跑构建；lane 报告里的"已接进门禁"要用那一行日志的【判了/没判】定案。

---

## 5. 交付物（8 件，每件有定义与合格线）

| # | 交付物 | 落点 | 合格线 |
|---|---|---|---|
| **D1** | 审查报告（本轮唯一完整交付） | `docs/reviews/usability/pipeline-and-usability-review-2026-09-27.md` | 每个结论指到 `tests/output/*.log` 或具体判据脚本；每格标 T1/T2/T3 强度；含"本轮我自己造成的事"与"仍需眼睛/仍需实机"两节 |
| **D2** | **静默降级台账**（核心件） | 报告内表 + `tests/output/degrade-ledger-2026-09-27.md` | 每行 8 列：位置 / 触发构造 / 实际行为 / 有 toast? / 进 `app_*.log`? / 只有 `Debug.WriteLine`? / 外部能否推断 / 结论分类。D2、D3 档**每点都要有实测读数**，只有读码推断的行明写"未逼出" |
| **D3** | 热格子矩阵结果表 | 报告内表 | 20 格（A 域 UI 选得出的 / B 域只能从 JSON 进来的）逐格给"决定性实验的实际读数"，不许写"读码认为" |
| **D4** | 可用性分场景评级 | 报告内表 | 场景集见 §8；每格三件证据齐才给字母评级，缺件写"未评" |
| **D5** | 新锁与牙的实测记录 | `tests/output/` 若干 log | 每条新锁一段：接上时绿、改回旧写法时红且点名、还原后绿；三段读数齐全才算有牙 |
| **D6** | 台账重判表 | 报告内一节 | 09-15 的 P1×18 + P2 逐条「已修 / 已撤 / 仍开 / 作废」+ 当前证据行；交接挂账 #45/#27/#36/#26/#16/#52 与 §62.8 三条并入 |
| **D7** | 交接文档新开一轮（§64） | `docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` 追加 | 未修项按 P0/P1/P2 入账，每条附证据数字与"下一步能直接执行的那一句"；**只追加，不回写旧节** |
| **D8** | （可选，需 D3 级授权）P0 修复补丁 | 工作树，不提交 | 每项修复配一条 T1 或 T3 判据 + 变异实测；改完跑全量门禁；**不 commit** |

---

## 6. 批次、实测成本与顺序

| 批 | 内容 | 产出 | 成本 |
|---|---|---|---|
| **A** | 前置 P1–P7 全跑：基线门禁 + 显示器态登记 + 外部裁判可达性 + 残留快照 | 基线记录（见下方"批次 A 实测"） | **实测 191.6 s**（全量 `scripts/verify.ps1`，默认 7 套件、不带 `-IncludeDesktop`）：`[1/2]` 构建 31.5 s（0 警告）＋ `[2/2]` 1365 断言 48.7 s ＋ `[1.6]–[1.9]` 约 111 s（这一段是两个端点相减推的，不是逐步实测） |

### 批次 A 实测（2026-09-27 本轮当场取，出处 `tests/output/plan-baseline-gate.log`）

```
[1/2] 构建 TrueToneCap.slnx        0 个警告      已用时间 00:00:31.54
[1.6/2] 发布门禁回归               通过
[1.7/2] 外部判据                   通过（另有 1 处"判了，通过"的独立行）
[1.8/2] 色彩管线判据               3/3 全部判了且通过      ← 不是 SKIP，这条读数是 §62.7 那把"从未转动的锁"的体检
[1.9/2] 宿主新鲜度                 通过
[2/2]   7 套件 1365 断言 (48.7 s)   exit=0，全程 wall 191.6 s
   --unit-tests       869 / 0 / 跳过 1
   --color-tests      213 / 0
   --encoding-tests    14 / 0
   --service-tests      4 / 0 / 跳过 2
   --ocr-tests         12 / 0
   --format-tests      43 / 0
   --usability-tests  210 / 0 / 跳过 1
```

自检与记账：

- 七套相加 869+213+14+4+12+43+210 = **1365**，与汇总行一致（求和对得上才敢当基线用）。
- **本轮基线就是 1365**，且**不拿它当后续轮次的预期值**——每批收尾重新取，条数变动要能逐条对上（§54 那轮"−7 BMP +4 新锁 = 1317"的形状）。
- **4 处跳过**（unit 1、service 2、usability 1）＝这轮有 4 件事**没判**。C 批起第一条要办的就是逐条问"这 4 个 skip 是环境够不到，还是判据本身恒跳过"。
- 用户配置指纹：跑前跑后两份 `%LOCALAPPDATA%\TrueToneCap\*.settings.json` 的 sha1 **逐字不变**（`5c9fef8a…` / `40589a55…`，记录在 `tests/output/preflight-settings.sha1`）⇒ 本轮门禁没有改用户机器上的设置（§9 P10 的义务）。
- 构建实测 31.5 s，比 `verify.ps1` 注释里 `-BuildOnly` 的"约 27 s"略高；注释里"全量约 110 s"与本轮 191.6 s 差了一截——**那个旧注释已过期，本轮不改它**（属另一份报告的地盘），只在报告里记这条差异。
| **B** | **补既有件，不新建 harness**：把覆盖面加进 `App/Services/SelfTest.cs` 已有的 case 表（它已经在 new `CapturePipelineService`/`WgcCaptureService`），把"用户所选"喂进去而不是 `new AppSettingsData()` 默认快照 | selftest case 矩阵 + `--display-audit-emit` 逐屏真件 | 中；比新建 GUI 驱动器便宜得多（v1 曾把 B 批估成本轮最贵，此处**显式撤回那个估计**） |
| **C** | L1–L6 六条 lane 静态深读（找形状：静默返回宽松默认值 / 双实现漂移 / 声明与像素不同向 / 收起卡缺点亮行 / T2 冒充 T1） | 每 lane 一份候选缺陷表，每条带文件:行 + 拟用哪一级判据 | 可与 G 并行 |
| **D** | 六层图像正确性实跑（全矩阵样张） | 逐格三层读数表 | 主体 |
| **E** | 20 热格子 + pairwise 覆盖 | D3 表 | 主体 |
| **F** | 降级台账（约 68 点，D0/D1/D2/D3 四档）逐点逼出 | D2 表 | 最贵的一块：D2/D3 大多要靠构造环境（移走 `native\{avifenc,cwebp,cjxl,cjpegli}`、预置 sentinel、`TTC_FORCE_SDR=1`、断网制造 WCS 失败） |
| **G** | 09-15 台账重判（纯读码） | D6 表 | 低 |
| **H** | 汇总成文 + 交接入账 | D1 + D7 | 主循环写 |

顺序：A → B → C/G（并行）→ D → E → F → H。T 级缺口在 A 批就登记，H 批报告首页先给"哪些结论只到 T2/T3 缺证据"，别让读者以为全绿就是全对。

---

## 7. 六个决策（本轮定死，不再回头请示；每条给可逆性与回退）

| # | 决策 | 取舍与理由 | 可逆性 |
|---|---|---|---|
| **D1 交付形态** | 审 + 写 + 台账为默认闭环；**修复只做判据明确的 P0，且改在工作树不提交** | 改源码是可逆本地动作，提交不是。每项修复必须自带变异实测（D5 形状），否则不算修完 | 回退：`git checkout -- <file>` 前必须先另存副本 + sha 核对（§54 记过一次"用 checkout 还原把当天所有改动一起吃掉"） |
| **D2 基础设施** | **不新建** `scripts/e2e/ttc-e2e.ps1`；改为扩 `SelfTest.cs` 既有 case 表 | 既有机制已经能在无头态跑真编码器与真 WGC，缺的是覆盖面不是入口（先找既有机制做最小改动） | 新增 case 是纯加法，可单条撤 |
| **D3 起不起产品进程** | 默认**不起**。批次 B/F 需要真身进程时，先明说要起哪一条命令、判据与超时，放行后只跑那一条 | `SelfTest` 住在 WinExe 里，无头复算就得起 `TrueToneCap.exe`；权限分类器会拦产品进程启动，且本机还有并发会话抢 `bin` 的风险。CI 那条 `--selftest` 路径写进报告，但不在本机擅自复跑 | 不起 = 报告里明写"这一格缺 T3 证据" |
| **D4 要眼睛的两类** | 排最后（H 批之后），我出**对照产物 + 数值表**，观感结论等你一句 | 英文界面目视（#36/#26）与 HDR 观感（#16/#52）。HDR 屏已接上，能出对照件；"好不好"不由我写 | 不排 = 这两格在 D4 表里留"未评" |
| **D5 每拍降级流水账** | **不加**（沿用 09-20 §9 我的倾向，并给理由） | 三方对齐已能不依赖应用自述抓出 D1/D2/D3；加日志要动已发布代码并按"一条提交=一个版本"并版。F 批结束时按"实际抓不到的比例"再定 | 纯决定，无改动 |
| **D6 `--cicp` 改变编码决策的根因** | **排进 D 批**一起做 | 它现在是一条"结论对、机制不明"的挂账（§62.8），而 D 批本来就要跑后端阶梯与逐格读数，边际成本低 | 查不清就在报告里明写"机制未定，结论不受影响" |

---

## 8. 可用性场景集（D4 表的行）

单屏 SDR 存文件（默认已是 PNG）· 单屏 **HDR**（本机有 HDR 面板，首次可自量）· 广色域面板 + 自动档解析（EDID ΔG≥0.05 门槛与三档策略）· 多屏 / 混合 DPI 选区 · **英文界面**（零次读数）· 无感连拍（并发/取消/编码失败的可见性）· OCR 三引擎 + 翻译三档 · **首运行盘上无配置文件** · 只读安装目录（Program Files）· 剪贴板到各目的地 · 托盘与热键冲突回退链。

---

## 9. 前置与执行纪律（A 批清单，命令都核过）

| # | 事项 | 命令 / 判据 |
|---|---|---|
| P1 | 基线绿 | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 [-IncludeDesktop]` ⇒ `[1/2]` 构建 → `[1.5/2]` 格式（仅报告）→ `[1.6/2]` 发布门禁回归 → `[1.7/2]` 外部判据 → `[1.8/2]` 色彩管线判据（**无跳过开关**）→ `[1.9/2]` 宿主新鲜度 → `[2/2]` 7 套件（加 `-IncludeDesktop` 追加 `--wgc-avif-tests`）。**不把 §62.9 的 1365 当本轮基线**（本轮 A 批自己跑出来也正好是 1365——那是两次独立取数撞上了，不是抄来的；后续每批仍当场重取） |
| P2 | 宿主新鲜度会误伤 | `[1.9/2]` 是全局判据：只改一句注释也会因 Tools 宿主时间戳滞后**整轮作废**（§62.9）。处方：源码类改动攒一批，需要时 `dotnet build --no-incremental` 全解重编 |
| P3 | 外部裁判可达 | 用 `py -3`（本机 `python` 是商店桩，exit 49 无输出）；探测 ffprobe / `avifdec` / Pillow+lcms2 / imagecodecs / Edge headless / WIC + `scripts/precision/precision_env.py` 的候选位 |
| P4 | jxl 工具链 | `jxlinfo.exe`/`djxl.exe` 的运行时 DLL 在 `build/lib/<Config>/` 而非 exe 同目录，直接跑退 `0xC0000135`；A 批复核候选位与子进程 PATH 追加是否仍生效 |
| P5 | 显示器态登记 | `--hdr-probe` 取本机读数写进报告首页；结论绑定"这台机"，换机不可迁移（真 MFT/QSV 那一列本机量不到，§62.8） |
| P6 | 残留清场 | 开跑前对 `%TEMP%\ttc_avif_*.png`、`%TEMP%\TrueToneCap_Clip\`、`.%name%.%guid%.tmp`、`%LOCALAPPDATA%\TrueToneCap\encoder-crash.*.sentinel`、`%TEMP%\nvenc_diag.txt` 拍快照，收尾比差集——孤儿文件本身就是结论 |
| P7 | 长跑环境 | 关自动睡眠与锁屏（WGC 在会话离开时拿不到帧，会把超时误判成产品缺陷）；每拍前确认会话 active |
| P8 | 判据不依赖焦点 | GUI 取证用 `PrintWindow(hwnd,…,2)` 抓被遮挡窗、`PostMessage` 不动用户光标；**不许**用"按键后标题变没变"这类依赖 `Form.ActiveForm` 的判据 |
| P9 | 若要出包 | `scripts/pack.ps1` **必须给 `-OutputPath` 新目录**（不给会覆盖同版本号变体的正式包；`-OutputDir` 是 `Remove-Item -Recurse` ⇒ 别指向含用户 log/设置的旧目录）；树净不带 `-AllowDirty`；出包提交与标签之间若只差文档，先证差异非构建输入 |
| P10 | 执行纪律 | `dotnet test` 禁用；构建失败或产物是旧的 ⇒ 该轮所有 `RESULT` 作废；管道末端退出码不作数（`${PIPESTATUS[0]}`）；MSYS bash 会静默破坏 PowerShell ⇒ 判据脚本从 PowerShell 侧起；**测试与产品共用配置 ⇒ 跑测试能改用户机器**：A 批已先存两份设置文件的 sha1（`tests/output/preflight-settings.sha1`），每轮收尾断言"还原后指纹不变" |

---

## 10. 风险与不可覆盖项（报告首页要先讲这几条）

1. **T3 缺口**：不起产品进程就不可能证到 App 侧端到端行为；本计划把这类格子明写"缺证据"，不拿 T2 的绿当 T3 的绿。
2. **换机不可迁移**：QSV/MFT 那一列本机量不到（本机 Intel 适配器不接 AV1 编码）；HDR 结论绑定当前面板态。
3. **共享机器归因**：并发会话会抢同一份 `bin` 与同一台显示器；全屏置顶窗会让光标型测试不可做。每轮读数先按进程段归轮。
4. **观感类结论不由我下**：#16/#52/#36/#26 需要眼睛。
5. **n<5 的偶发不写成因果**：崩溃与判红分开计数，退出码是正数 NTSTATUS 的坑要防。

---

## 附：一轮的合格线（收尾判据）

- 每个写进报告的结论都指到 `tests/output/*.log` 或具体判据脚本；**没跑过的批次不许写成结论**，估算值不混进实测读数。
- 新锁量过牙；SKIP 与判红分开计数，"这轮没判"不算通过。
- 报告保留"本轮我自己造成的事"一节（交接 §54–§63 的既有形状）。
- 收尾必须有证据数字，且是当场重跑的。

---

## 状态更新（同日，计划批准后）

- **基线现值 1369 断言 / 2 处跳过**（不是本文 §9 P1 与批次 A 里写的 1365——那两处属"取数当时的记录"，按规矩不回写）。逐轮出处：`tests/output/lock-fix-gate.log`（1368）→ `tests/output/n6fix-gate2.log`（1369）。
- **决策 D3 已被授权覆盖**：用户 2026-09-28 给出"1.起 / 2.做 / 3.做"，故三件无头真身读数已取（`--selftest` 10/10、`--hdr-probe` 4/4、`--display-audit-emit`），两处一行级修法已落地，N1 的尺已按仓里先例 `git add -f` 暂存。**仍缺的 T3**：英文界面目视、剪贴板粘贴结果、overlay/热键——这三面要起一次带窗口的会话。
- **新增一条方法学问卷**（报告 §6.1）：真身探针的**数值随当时桌面内容变化**（同一条 `HDR_SOURCE` 两次 PASS 靠的不是同一支判据），D 批要复核它的阈值与 SKIP 带。
- 执行细节、读数与我本轮造成的事，全部在 `docs/reviews/usability/pipeline-and-usability-review-2026-09-27.md`，本文不再复述。

---

## 状态更新（2026-09-29，v3 · 批次 C 十条 lane 全部派工）

### 接手时的现状（不当本轮读数用）

- 上一轮终态：`tests/output/batchD-gate3.log` ⇒ `GATE_EXIT=0`、**1379 断言 / 2 处点名跳过**（源码最后一次改动 16:52 < 该轮 16:55，所以它是当前树的有效基线，但本轮仍在当场重跑一次当锚）。
- 已完成批次：A（基线）、G（旧 P1×18 重判）、B 首件（`SelfTest` 管线落点喂真设置 + 退出码 fail-closed）、D 首件（HDR 精度判据的白点锚，测出一次假绿并修）、C 首件（4 处 SKIP 定性 + ShaderLoader 锁补牙）、§6 的 T3 面。**未开始**：批次 C 全量、D 全矩阵、E 热格子、F 降级台账、H 成文。
- 工作树有四处未提交改动（`SelfTest.cs` / `UsabilityTests.cs` / `TrueToneCap.Test.csproj` / `scripts/verify.ps1`）+ 一份已暂存的 `tests/manual/combo_locale_coverage_measure.py`。开工前已整份存成 `tests/manual/safety/uncommitted-2026-09-29.patch`（43 KB）。**本轮不 commit、不 push、不打 tag**。

### 十条 lane 的文件归属（按文件切，互不重叠）

| lane | 文件面 | 主要交付 |
|---|---|---|
| L1 捕获 | `WgcCaptureService.cs`、`HotkeyManager.cs`、`RegionDetector/DetectedRegion.cs`、`AnimationRecorder.cs`、`PowerManager.cs`、`D3DDeviceLost.cs` | 选路/退档/冷却翻转点/超时并发 |
| L2 色彩与色调映射 | `Core/Capture/DisplayInfo.cs`(含 ColorDecision)、`ColorProfileProvider.cs`、`IccStore.cs`、`ToneMapper/GpuToneMapper/ShaderLoader/ShaderCompiler.cs`、`App/Shaders/*.hlsl`、`MainWindow.ColorManagement.cs`、`PixelOps.cs` | 三源取档失败面 + **CPU↔HLSL 常数对照表** |
| L3a 编码声明与容器 | `FormatEncoders.cs`、`FormatColorFacts.cs`、`Managed{Png,Tiff}Encoder.cs`、`AtomicFileWriter.cs`、`ImageEncoder.cs`、`Core/Settings/FormatOrder.cs`、`MetadataCollector.cs` | **声明写入点表**（D 批全矩阵的输入） |
| L3b 编码后端与硬件 | `Native{Avif,Jxl,WebP}Encoder.cs`、`JpegLiNative.cs`、`NvEncoderNative.cs`、`MftEncoderNative.cs`、`NativeEncoderGuard.cs`、`AvifEncoder{Capabilities,Probe}.cs`、`AvifHardwareProbe.cs`、`GpuCapability.cs`、`EncoderCrashSentinel.cs`、`EncoderFallbackNotifier.cs`、`JpegGainMapEncoder.cs`、`NativeLibraryResolver.cs` | 超时 Kill × 降级留痕对照表 + 参数拼装边界 |
| L4 输出与目的地 | `CapturePipelineService.cs`、`MainWindow.xaml.cs`（只报截图/落盘/剪贴板/toast/状态栏段）、`ToastService.cs`、`SilentCaptureToast.xaml.cs`、`AppServices.cs`、`LogService.cs`、`Core/Logging/CoreLog.cs`、`Models/CaptureResult.cs` | **失败可见性台账**（F 批的核心输入） |
| L5 设置与能力协商 | `SettingsService.cs`、`Models/AppSettingsData.cs`、`SettingsDiffPruner.cs`、`SettingsWriteProbe.cs`、`CapabilityService.cs`、`SecretStore.cs`、`StartupManager.cs`、`AppJsonContext.cs` | **B 域越界值行为表**（E 批热格子的输入） |
| L6a 交互与标注 | `MainWindow.xaml`、`MainWindow.{Settings,OcrSettings}.cs`、`SelectionOverlay.xaml(.cs)`、`AnnotationWindow.xaml(.cs)`、`Core/Annotation/*`、`AnnotationRasterizer.cs`、`TrayIconManager.cs`、`App.xaml.cs` | 收起卡清单 + 动作→反馈对照表 |
| L6b 预览与本地化 | `LocaleManager.cs`、`MainWindow.Localization.cs`、`HdrCaptureWindow.cs`、`HDRPreviewWindow.cs`、`OcrPreviewWindow.xaml(.cs)`、`ThirdPartyNoticesWindow.xaml.cs`、`FontLoader.cs`、`AnimationHelper.cs`、`TrueToneCap.LogViewer/*` | 扫描面盲区清单 + `.cs` 中文拼串完整清单 |
| L6c OCR 与翻译 | `Core/Services/{MultiOcrService,OnnxOcrEngine,OcrService,IOcrEngine,OcrEngineType,OcrLanguage,BitmapPreprocessor,TranslationService,LlmJsonContext}.cs` | 引擎/后端决策表（所选→实跑→留什么痕） |
| L7 判据体检 | `src/TrueToneCap.Test/*.cs`、`scripts/verify.ps1`、`scripts/precision/*.py`、`scripts/probes/*`、`.github/workflows/release.yml` | **G3 名录**：T2 冒充 T1 的完整清单 / 恒真·恒跳·无牙 / App 侧执行覆盖率 / 孤儿裁判 |

交界约定：`DisplayInfo.cs` 归 L2（ColorDecision 是色彩决策），L1 只引用不报；`MainWindow.xaml.cs` 归 L4，界面控件与本地化分别归 L6a/L6b；native 后端归 L3b、声明层归 L3a，两者在"保底分支返回值后缀"这条契约上交接。

### 派工纪律（本轮实行的三条）

1. **lane 全只读**：不许 build、不许跑测试、不许起产品进程——主循环那轮基线门禁占着同一份 `bin`，lane 一动就把它正在量的宿主时间戳改掉（`[1.9/2]` 会作废整轮）。lane 期间主循环只写 `docs/` 与 `tests/manual/safety/`，不碰 `src/`。
2. **每条候选缺陷的格式是硬的**：`文件:行` + 触发构造 + 用户可见性（toast / 进 `app_*.log` / 只有 `Debug.WriteLine` / 完全静默）+ P 级 + T 级（T1 执行 / T2 文本哨兵 / T3 真身或真机）。没有行号的推测不收。
3. **lane 的总结论一律降级为候选**：本项目付过学费的形状是"lane 末句会误报盘上实况"。收回后逐条回源读，最重的 2–3 条主循环自己复跑取证，负断言要用第二条独立命令复跑同一模式；报告里凡只到 lane 的行都带【lane】标记，不当已确证写进结论。

### 后续批次顺序（不变）

C（收拢 + 复核）→ D 全矩阵三层读数 → E 热格子 → F 降级台账 → 补牙（G3 名录落地）→ T3 缺口面（英文界面计数读全 / overlay·热键实机 / 剪贴板真实目的地，这三面要逐次报备起进程）→ H 成文 + 交接 §64。
