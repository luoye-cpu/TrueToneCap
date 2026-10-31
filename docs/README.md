# docs 索引 / 怎么读这批文档

> 本文件是 `docs/` 的**入口**，2026-10-17 新建。此前全仓没有 docs 索引：
> `README.md:230/:233` 只点名了主台账与一个 `docs/pipeline-review-*.md` glob，
> `CHANGELOG.md:13` 有一句"同目录其它 `docs/*.md`"的泛称。
> 下面的每个数字都是当场量出来的（复查口径见 §八），不要凭本文件的历史版本推断。
>
> **2026-10-22 目录分层**（推翻同日 §七-1 的"不搬"，由维护者直接下令）：54 份文档按主题搬进
> `docs/reviews/<族>/` 与 `docs/ledger/`，10 份无日期活文档与本索引留在 `docs/` 顶层。
> 搬迁用 `git mv`（rename 可追），**正文一字未改**：只把引用里的路径前缀改了 96 处 / 35 个文件，
> 判据 = 除本索引外的 64 份仍是 **30,388 行**，与分层前记录的行数逐字相等（行内替换不加行）。
> 已量过：没有任何在册脚本、门禁或 CI 用 `docs/*.md` 这类 glob 枚举文档，故不存在"静默少枚举 54 份"。


## 一、先看规则（本仓已拍板，别绕过）

1. **文件名带日期的 `.md` 是取证快照**：当时为真就是价值所在。发现其中有错**不回写**，
   要纠正在**新文件**里写，或在**无日期的活文档**里更新。改快照 = 伪造历史。
2. **例外只有一条**：`ledger/handoff-*` 那两份虽然名字带日期，实际是**跨周追加的运行台账**
   （见下节），按活文档对待。
3. **文档三分工**（2026-09-20 拍板）：
   `README.md` 只写新增能力 + 工程规范；`CHANGELOG.md` 只写变更流水（现象与影响）；
   `docs/ledger/handoff-*.md` 写根因、判据设计、变异实验、被推翻的旧结论。
   ⚠ 现状偏离：`CHANGELOG.md` 自己 `:12` 写着"根因分析…都在维护者文档里"，
   而正文 `:451 :521 :682 :691 :921 :1063` 六处（全文"根因/事后分析"命中 14 处）就是根因分析 ⇒ 与自己那句冲突；
   `CHANGELOG.md:13` 还把范围放宽成"其它 `docs/*.md`"——这句与上面那条分工不一致，待你定。
4. **"过时"不是删除理由**。快照的删除只在两种情况下才成立：证明它不记录任何唯一证据，
   且你明确授权。本索引只负责让你一眼看见有什么。

## 二、台账（运行中，不是快照）

| 文件 | 行 | 状态 |
|---|---|---|
| `docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` | 7,941 | §编号已到 **72**（含 72.6），最后追加 2026-10-22；733,728 B（磁盘口径），**占 `docs/` 总字节的 31.7%**（次大的是 `docs/reviews/usability/pipeline-and-usability-review-2026-09-27.md` 381,709 B；字节口径见 §四 末）。任务账本、拍板记录、被推翻结论都在这里 |
| `docs/ledger/handoff-avif-encoder-refactor-2026-09-16.md` | 406 | 前一阶段的交接，已收尾 |
| `CHANGELOG.md`（仓库根） | 222,344 B（blob） | 变更流水；发布记录与 tag 对账也记在这 |

要找"某件事当时为什么这么做/后来怎么翻的"，先在这三份里搜，别去审计快照里找结论。

## 三、活文档（无日期名 ⇒ 有义务跟代码同步）

共 10 份，**全部留在 `docs/` 顶层**（分层只搬带日期的快照与台账；行数是**本轮改完之后**的实测值）：
`architecture-design.md`(891) `ocr-model-landscape-2026.md`(429，只带年份)
`roadmap-v0.4.md`(318) `ocr-parallel-analysis.md`(252) `performance-analysis.md`(241)
`dependency-management.md`(232) `ocr-pipeline-review.md`(168) `repo-hygiene-review.md`(119) `ocr-accuracy-analysis.md`(111) `vscap-mode.md`（VS-Cap 模式的归档说明：能力已并入主程序，侧车与 Qt 依赖已移除）。
（本索引自己不算在内 —— 它也是无日期名，但它是入口不是待同步对象。）

**本次逐条核过的具体不同步点（只有这两份需要动，其余点名符号都能在 `src/` 里搜到）**：

- `architecture-design.md:30` 把 `Encoding/ManagedBmpEncoder.cs` 列进"**当前确实存在**"名单 ⇒ 该文件已于
  `v0.3.6-beta`(v0.3.6) 删除，且 `CorePipelineTests.cs:4063-4065` 有一条**反向断言**禁止它再出现。已就地更正。
  同一句话里其余 5 个名字（`IccStore.cs` / `NativeEncoderGuard.cs` / `ManagedTiffEncoder.cs` /
  `AvifHardwareProbe.cs` / `GpuCapability.cs`）实测**都还在**，只有 BMP 这一条是假的。
- `repo-hygiene-review.md`：`:111` 写 `HDRPreviewWindow.cs`"文件仍存在" ⇒ 已于 `v0.3.7-beta`(v0.3.7) 删除
  （反向锁 `Test_Overlay_HasNoGpuBackgroundPath` 在位，`CorePipelineTests.cs:156/:4689`）；
  `:110` 那条"把 docs 合并成 3-4 份主题文档"与本文第一节的快照规则直接冲突。
  ⇒ 已按它自己的"状态更新"体例追加，原句不删。

⚠ `architecture-design.md` 里那些**带 `✗` 前缀或在标注表里**的缺席文件（`GpuToneMapper.cs`
`ShaderLoader.cs` `ToneMapping.hlsl` `GamutMapper.cs` `GpuEffectProcessor.cs` `ManagedJpegEncoder.cs`
`NvencAvifBackend.cs` `MosaicEffect.hlsl`）是**故意留的"曾存在"标记**，不是漏改——别再去"修"它们。

## 四、主题族 = 目录（同族读最新那一份即可，旧的是证据不是现状）

2026-10-22 起，族名就是目录名。每份目录的实测份数在括号里：

| 目录 | 份数 | 最新（读这份） |
|---|---|---|
| `docs/reviews/pipeline-review/` | 11 | `pipeline-implementation-review-2026-10-07.md` |
| `docs/reviews/repo-governance/` | 10 | 无后继，按名读（CI/跟踪/文档体检/待发分诊那批 09-14 与 09-16 快照） |
| `docs/reviews/usability/` | 7 | `usability-audit-2026-10-02.md` |
| `docs/reviews/msix-store/` | 8 | `realmachine-msix-verification-2026-10-07.md`（真机安装实测，mtime 最新） |
| `docs/reviews/dependency/` | 5 | `dependency-update-analysis-2026-10-07.md` |
| `docs/reviews/performance/` | 9 | `perf-implementation-2026-10-31.md`（审查候选 A/C/D/E 的落地结果、真实样张读数、MSIX/分发与开关；先读这份）→ `pipeline-performance-audit-2026-10-30.md`（全管线审查 + 候选排序 + 判死表）与同日 `pipeline-measurements-2026-10-30.md`（审查 §6 待量清单的补测读数）；落地读数见 `preview-fixed-costs-result-2026-10-30.md`（计划的取证快照是同目录 `preview-fixed-costs-plan-2026-10-30.md`）；更早的取舍见 `region-readback-options-2026-10-30.md` |
| `docs/reviews/compression/` | 3 | `encoder-params-and-smallest-tier-2026-10-07.md` |
| `docs/reviews/capture/` | 3 | `root-cause-hdr-multimonitor-2026-09-16.md` |
| `docs/reviews/ocr/` | 3 | `winml-migration-plan-2026-10-07.md` + `ocr-model-replaceability-2026-10-07.md` |
| `docs/reviews/notification/` | 2 | `windows-system-notification-2026-09-16.md` |
| `docs/reviews/encoder/` | 1 | `avif-encoder-benchmark-2026-09-16.md` |
| `docs/reviews/upstream/` | 1 | `upstream-pr1-review-2026-10-16.md` |
| `docs/ledger/` | 3 | 见 §二（台账，不是快照） |

⚠ 本索引旧版把 `winml-migration-plan` **同时**列进 MSIX 族和 OCR 族（两处都写了它）。
分层必须二选一，本次按内容归进 `reviews/ocr/`（它改的是 OCR 推理后端，与 MSIX 打包无关）。

零散快照的去向：`cicd-pipeline-review` `git-tracking-audit` `re-audit` `real-machine-test-report`
`test-verification` `docs-staleness-audit` `pending-changes-triage` `release-review` → `repo-governance/`；
`gpu-acceleration-availability` `root-cause-hdr-multimonitor` `capture-source-regression` → `capture/`；
`windows-system-notification` `notification-implementation-research` → `notification/`。

**体量（稳定口径：不含本索引自己）**：`docs/` 除本文件外 = **64 份 / 30,388 行 / 磁盘 2,310,697 B**（2026-10-22 分层**之后**实测）。
分层前的对照数：同一批 64 份是 30,388 行 / 磁盘 2,309,524 B（blob 口径 2,292,021 B，主工作树 CRLF 虚高 0.76%）
⇒ **行数一字未变**，字节 +1,173 = 96 处路径前缀替换的净增，证明搬家没动正文。
其中带日期 54 份（52 份进 `reviews/*`，2 份是 `ledger/` 台账）、无日期 10 份（即 §三 那 10 份活文档）。含本索引是 65 份；
本文件会被继续编辑，所以"含本索引"的行数不给死数。
（上一轮 2026-09-26 审计记的是"38 份 / 30 份快照 / 15,954 行"；现在行数是它的 **1.90 倍**，那份计数已过期，别拿它做体积或成本判断。
同一天更早一次记的"60 份 / 28,973 行"在 `ce1e9e7` 树里是真数，`20a3198` 把 4 份 10-07 快照落了地 ⇒ +4 份 / +1,361 行。）
后又一轮（2026-10-21 本次提交）把台账 §71（26 KB 地板史）从 gate-baseline.json 搬进主台账 ⇒ +13 行 / +27,440 B，份数不变。

**2026-10-30 重新量了一次**（口径同上，`find docs -name '*.md' ! -name README.md`）：**71 份 / 31,717 行 / 磁盘 2,411,710 B**。
与上面那句 64 份的差是 **+7 份 / +1,329 行 / +101,013 B**，对得上账：`git log --diff-filter=A --since=2026-10-23` 数到
10-22 之后新落地 **6 份**，本轮 `reviews/performance/region-readback-options-2026-10-30.md` 再加 **1 份**（150 行 / 12,473 B）。

同日晚上第三次量（`find` + `wc -l/-c`，同一口径）：**75 份 / 32,417 行 / 磁盘 2,464,667 B**。
与上面 71 份的差是 **+4 份 / +700 行 / +52,957 B**，逐份对得上：
`preview-fixed-costs-plan-2026-10-30.md`（228 行 / 17,302 B）、`preview-fixed-costs-result-2026-10-30.md`（123 行 / 10,172 B）、
`pipeline-performance-audit-2026-10-30.md`（200 行 / 16,061 B）、`pipeline-measurements-2026-10-30.md`（149 行 / 9,422 B）。
本轮只按实测把 §四 的**份数**列更正了 6 行（dependency 4→5、msix-store 7→8、ocr 2→3、performance 3→4、
repo-governance 8→10、ledger 2→3）；此后同日两轮又新增 4 份，performance 4→8；
**"最新（读这份）"那一列除 performance 外没有逐族重判**——
族里新增了哪几份、该读哪一份，得看内容而不是文件名，本轮没做这件事，别把那一列当现状。

2026-10-31 第四次量（同一口径）：**76 份 / 32,665 行 / 磁盘 2,481,671 B**。
与上面 75 份的差是 **+1 份 / +248 行 / +17,004 B**，正好是
`perf-implementation-2026-10-31.md`（248 行 / 17,004 B）；performance 族 8→9，其余族未动。

同日第五次量（写完真实样张 / MSIX / 签名结果之后，同一口径）：**76 份 / 32,715 行 / 磁盘 2,486,299 B**。
与第四次量的差是 **份数不变 / +50 行 / +4,628 B**，全部来自
`perf-implementation-2026-10-31.md`（248→298 行，17,004→21,632 B；§4 策略改为 level 10 + 0.05 阈值，
补真实样张、产品 A/B、MSIX 与签名/杀软边界，§5 门禁表与 §7 未做项同批更新）。

## 五、孤儿（零入站引用，但仍是证据）

判据 = 除本索引外没有任何跟踪文件写出它的全名（`git ls-files` 全文匹配，2026-10-21 在 `20a3198` 树内重跑；
**2026-10-22 分层后按同一判据再跑一次 = 仍是 8 份，名单逐字相同** ⇒ 搬家没改变谁被引用）：

`reviews/compression/compression-effort-3tier-design` · `reviews/msix-store/aot-and-store-publishing-analysis`
· `reviews/notification/windows-system-notification` · `reviews/pipeline-review/pipeline-independent-review`
· `reviews/pipeline-review/pipeline-review-e2e` · `reviews/pipeline-review/pipeline-review-plan`
· `reviews/repo-governance/docs-staleness-audit` · `reviews/repo-governance/release-review`（均带各自日期后缀）

⚠ 这条读数本身有个坑，本轮踩过：第一版尺子忘了把 `docs/README.md` 排除出引用者，结果只报 6 份
——因为我在 §四 的"零散快照去向"里点名了 `docs-staleness-audit` 与 `windows-system-notification`，
索引自己把这两份从孤儿名单里捞了出来。**判据里"除本索引外"这个限定词必须落到脚本里**，不然每改一次索引，孤儿数就漂一次。

⚠ 两处别扭：`docs-staleness-audit-2026-09-14.md` 自己点名了一堆本仓文档，却只被本索引提到——
它就是当年那次"文档体检"的结论，**要查历史判定先看它**；`real-machine-test-report-2026-09-14` 与 `upstream-pr1-review-2026-10-16`
本轮从孤儿名单里摘掉（前者被 `docs/roadmap-v0.4.md` 点名 1 处，后者被主台账 + `roadmap-v0.4.md` + 本索引共 4 处点名，随 v0.3.9-beta 入库后已不再无人引用）。

## 六、断链：曾经只有一条，已修

| 出处 | 指向 | 处置 |
|---|---|---|
| `scripts/migrate-net11.ps1:135` | `docs/dotnet11-migration-plan.md`（该文档在 `c4591a7` 被删） | **已改**（2026-10-17）：脚本现在给出可取回原文的命令 `git show c4591a7^:docs/dotnet11-migration-plan.md`（实测 RC=0，标题 "TrueToneCap .NET 11 迁移计划"） |

**以下 7 组曾被一轮审计当成"断链"报回来，逐条核过都不是**（写在这免得第三轮又报）：

- `scripts/install-hooks.ps1`、`Pack-Msix.ps1`：文中都是"**或**提供 / **或**独立的"式**提案句**，
  后来分别落地为 `scripts/install-git-hooks.ps1` 与 `scripts/pack.ps1`。提案不是坏链。
- `SECURITY.md`：`cicd-pipeline-review-2026-09-14.md:621/:667` 是"仓库**没有**它，建议加"；
  `dependency-update-analysis-2026-10-07.md:199` 指的是 **libjxl 自己的** `SECURITY.md`。
- `scripts/e2e/ttc-e2e.ps1`：09-20 计划提议建，09-27 计划 `:159` 明确"**不新建**，改为扩 `SelfTest.cs`"
  ⇒ 这是**决策记录**，目录本来就不该存在。
- `MODEL_LICENSES.md`、`default_models.yaml`（`ocr-model-landscape-2026.md`）：都是 **RapidOCR 上游仓库**的文件，
  本仓历史里从未有过（`git log --all -- MODEL_LICENSES.md` = 0 笔）。
- `src/Core/Resources`（`repo-hygiene-review.md:66`）：**标题里的缩写**，紧跟的正文写的是全路径
  `src/TrueToneCap.Core/Resources/`。
- `tests/manual/ocr-real-bench.txt`：那条是**改名建议**，且已执行——磁盘实名
  `ocr-real-bench-20260825-seed20260825.txt`。
- 主台账里的 `Core/Services/NativeProcess.cs`、`tests/output/user-settings-035beta.json`：
  两者在 git 历史里**从未被跟踪过**（0 笔），是当时一次性探针/证据文件 ⇒ 台账叙述，不是坏链。

⚠ 方法教训（本轮我自己踩的）：**"目标不存在"必须和"文中是断言还是提案"一起判**，
只看 grep 命中 + 文件缺失会造出一整批假缺陷。
带日期的快照即便真含过期句子也**不回写**（规则一）。

## 七、待你拍的六件（都是取舍，不是清理）—— 2026-10-22 六件全部处置完毕

1. **54 份快照要不要真挪进 `docs/reviews/<族>/` —— 2026-10-22 上午拍板"不搬"，同日晚上按维护者新令推翻：搬。**
   原拍板理由（保留原文以免历史失踪）：
   （`CHANGELOG.md` 21、主台账 13、`handoff-avif-encoder-refactor` 7；2026-10-21 在 `20a3198` 树内按 `git ls-files` 全文重跑，
   只数写成 `docs/<带日期文件名>` 的引用，"文件指自己"已排除——本索引列的是不带前缀的短名，所以它自己贡献 0 处）。
   台账 §54.4 记的"22 处"已过期 4 倍。
   ⇒ 现状维持不搬：分层已由本索引 §三/§四 给出，搬家只换来一次全仓引用改写（其中台账与快照里的 `名字:行号` 引用会整片错位）。
   **推翻后的实测更正**：那句"`名字:行号` 引用会整片错位"是**过宽的**——错位只在**内容被编辑**时发生，
   而 `git mv` 不改变任何一行，所以行号引用照旧有效。真正的代价只有一项：路径前缀改写。
   实测量 = **96 处 / 35 个文件**（旧口径按 `docs/<名>.md` 数得 21+13+7=41 处，是只数了三份文件的子集，漏了 `.cs` 注释、`SECURITY.md`、CI 与 `docs/README.md` 自己）。
   搬家后两条负样本都过了：对 54 份搬走的文件查旧扁平路径 = **0 处残留**；对 10 份活文档报出的"45 处残留"是我的探针把没搬的文件也算进去造成的假数，已撤回。
   另确认：**没有任何在册脚本 / 门禁 / CI 用 `docs/*.md` 这类 glob 枚举文档**（命中 11 处全是文字引用），
   所以分层不会造成"少枚举 54 份然后静默判绿"。
2. **`TrimmerRoots.xml` + `App.csproj` 这一对 —— 2026-10-22 已处置：改成条件加载**（既没摘掉知识，也没假装它在起作用）：原来是 csproj 无条件 `<TrimmerRootDescriptor Include=…>`，
   但全仓 `PublishTrimmed/PublishAot/TrimMode/IsAotCompatible` **零命中**，`Publish.ps1:408-409` 自己写明
   "`CorePublishAot` 没有任何 csproj 消费"，且 `docs/roadmap-v0.4.md` 里 **AOT/裁剪 0 次提及**
   ⇒ 今天完全不起作用。要么成对摘掉（改构建输入，6 行 + 1 文件），要么把 `-CoreAot` 真接通让它有意义。
   两臂实测（`dotnet msbuild -getItem:TrimmerRootDescriptor`）：默认 → `[]`；`-p:PublishTrimmed=true` → 1 条且 FullPath 指向仓库根那份 xml。
   没有选"成对摘掉"（会删掉将来开裁剪要保哪些类型这条知识），也没选"把 `-CoreAot` 真接通"（那是产品级改动，另议）。
3. **一次性脚本归置 + SPDX 头 —— 2026-10-22 已处置**：先补齐（`add-spdx-headers.py` 实跑 86 个自有文件各 +2 行，逐文件"剥掉新增行后与原字节相同"全过；
   上游逐字副本 `VSCap/upstream/DesktopCapture.h` 我一度误盖了 Apache 章，已还原并在工具里加 `VENDORED_PREFIXES` 豁免——盖上去既误署版权又破坏 INDEX.json 的"逐字副本"声明），
   再补判据（新卡 `Test_SpdxHeaders_OwnSourceFilesAreStamped`，口径 = `git ls-files`；摘掉头会红，实测红 1 条）；
   三个一次性脚本（`migrate_forensic_paths.py`（文档自述"一次性脚本"，`.workbuddy-ai` 引用已 21→0）、
   `add-spdx-headers.py`（⚠ 本轮重测：按它自己的口径 `.c/.cs/.h/.hlsl/.ps1/.py` 命中 **262 个跟踪文件，其中 87 个没有 SPDX 头**——
   含 `src/` 下 4 个 `.cs`（`MarkdownDocumentWindow` / `PrivacyPolicyWindow.xaml.cs` / `ThirdPartyNoticesWindow.xaml.cs` / `CoreLog`）
   和我自己写的 `gen-third-party-notices.py`、`mutation-verify.ps1`；门禁没有任何一格管 SPDX 头，`CorePipelineTests.cs:3035` 那条管的是
   `licenses/INDEX.json` 的表达式，不是源码头。原先这里写的"464 个跟踪文件都已带头"是错的）
   、`migrate-net11.ps1`（net11 迁移已完成；它的断链本轮已修）
   —— 结论：补齐后**原地留用**，不建 `scripts/retired/`。它们都是可复跑工具，且被文档点名引用。
   ⚠ `export_onnx_fp32.py` 文档里**一次都没提**，而配套的 `onnx_fp16_quantize.py` 有 3 处文档记录 ⇒ 要么补出处要么一起处置。
4. **`scripts/verify-package-content.ps1` 零调用点 —— 2026-10-22 已接入 `release.yml`**（排在 `id: locate` 之后），
   并由 `verify-publish-gates.ps1` 新增的静态臂 H 钉住（第一版 H 是假绿——锚点匹到了步骤 title 和注释，删掉调用行照样绿；改成只认调用行后变异当场红）。
   顺带修掉一条从没被执行过的死路：release job 的 `Publish.ps1` 调用缺 `-NoVSCap`，在没 Qt 的自托管 runner 上会在 [0/6] 第一步就中止。
5. **磁盘回收 —— 2026-10-21 已按你两句（"移除它们""只留最新整包"）处置掉大头**：
   · `.git` **253 MB → 166 MB**：那 89 MB `AppPackages`（0.3.7 MSIX + 17 个安装脚本）先整目录导出到
     `publish/msix-0.3.7-from-backup-2026-10-21/`（18 文件 / 89,312,957 B，MSIX sha256 `263b3ce1…`，与 ref 内 blob 逐字节相等），
     再 `git update-ref -d refs/backup/main-pre-strip` + `git gc --prune=now`；`7da0b2c` 现已不可达，`refs/` 只剩 `main`。
   · `publish/` **2.21 GB → 905 MB**：0.3.9-beta 包已按 tag 指向的提交重出（413 MB / 611 文件，`build-info.json`
     `commitSha` 与 tag 提交逐字相等、`dirtyWorktree=false`；FileVersion 已随 `-Version` 覆盖为 0.3.9.0），`-KeepLast 1` 清掉 0.3.6 两套与 0.3.7 目录（1,220 MB），
     另手删两个 7z（242 MB）与 `_scratch`（397 MB）。现存=0.3.9 包 413 + `PLAN` 182 + `TTC日志查看器` 225 + 导出的 MSIX 85。
   · `artifacts/` **2,866 MB → 444 MB**（拆 wt-gm / wt-pr1 与补丁/快照清理，见第 6 件）。
   · `src` **1,850 MB → 93 MB**、`tests` **209 MB → 151 MB**：删掉 src/tests 下全部 `bin`/`obj`（16 处）、
     `src/TrueToneCap.App/AppPackages`（221 MB，是另一次 0.3.7 构建，与导出的那枚 sha 不同），
     以及 `tests/output` 里 6 个**零引用**条目（`round69-precommit` 29 MB + 三个 `uncommitted-2026-10-03*.patch` + `handoff.pre-*`）；
     被跟踪文档点名的产物（`realmachine-2026-10-01` 被 23 处引用等）一个没动。
   · **OCR 模型按你的要求持续保留**：`src/TrueToneCap.App/Models/*.onnx`（66 MB，跟踪件）、`official_models/`（132 MB）、
     包内 `data/Models/`（66 MB）都在，`download_models.ps1` 不需要重跑。
     ⇒ **10-23 变更**：`official_models/` 已按"清理上古内容"删除。它不是孤本——`export_onnx_fp32.py:10` 的 `BASE` 就是它，
     而它要的输入 `PP-OCRv6_medium_{det,rec}_infer/` Paddle 原件早就不在盘上；`paddle2onnx`/`paddlepaddle`/`onnx`
     三个模块本机都能 import，原件属公开模型库。跟踪件 `src/…/Models/*.onnx` 与包内 `data/Models/` **未动**。
   · 合计：工作区（不含 `.git`）**4.79 GB → 1.69 GB**；含 `.git` **5.04 GB → 1.86 GB**。
6. **`artifacts/wt-gm` 里那处未提交改动 —— 2026-10-21 已处置（按"弃 + 存档"）**：改动本体是
   `src/TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs` 在分离 HEAD `9ff00ad` 上把 `RescaleGainMap` 的因子 4 改成可读
   `TTC_GM_FACTOR`，文件里自己写明"体积实验专用、不进工作树"⇒ 是实验旋钮不是待落地功能（体积数已由 `pipeline-and-usability-review-2026-09-27.md` §⑧ 记下）。
   拆 worktree 前已存档：`artifacts/backup-consolidated/wt-gm-uncommitted-2026-10-21/`（改后文件逐字节副本 sha256 `374aea30…` + `uncommitted.patch`，
   实测 `git apply -p1 --check` 打到当前 main 通过、`--3way` Applied cleanly）。之后 `git worktree remove --force artifacts/wt-gm` 与
   `.../wt-pr1` 都已执行，两个基座 `9ff00ad`/`ce1e9e7` 经 `merge-base --is-ancestor` 证仍是 main 的祖先 ⇒ 没丢任何独有提交。
   `artifacts/` 由 2,866.7 MB 降到 444.1 MB。

## 八、这些数字怎么重跑

```bash
# 份数（不含本索引）= 64   ⚠ 2026-10-22 分层后 `ls docs/*.md` 只会给出顶层的 11 份，必须递归
find docs -name '*.md' ! -name README.md -type f | wc -l           # → 64
# 行数 / 字节（磁盘 CRLF 口径 = §四 的 30,388 行 / 2,310,697 B）
find docs -name '*.md' ! -name README.md -type f -exec cat {} + | wc -l -c
# blob（LF）口径：要在提交后按 HEAD 的新路径量；提交前用 git show HEAD:<旧路径>，两口径别混
for f in $(git ls-files docs | grep -v '^docs/README.md$'); do git cat-file blob "HEAD:$f"; done | wc -l -c
# 每个族目录的份数（= §四 那张表）
git ls-files docs | sed 's|^docs/||' | awk -F/ 'NF>2{print $1"/"$2}NF==2{print "TOP:"$1}' | sort | uniq -c | sort -rn
# 活文档（无 2026-MM-DD 后缀）：递归数给 11 = 10 份活文档 + 本索引
find docs -name '*.md' ! -name 'README.md' -type f | grep -v '2026-[0-9][0-9]-[0-9][0-9]'
# 搬走的文件有没有残留旧路径（判据 = 0）：拿 git diff --name-status 的 R 行逐个 basename 反查
# 入站引用 / 孤儿：把每个文件名在所有跟踪文本里搜一遍（排除构建副本、排除文件指自己）
# 断链：文中 token 命中 + 目标存在性两条一起判，只命中一条会误报
```

⚠ **搬家本身踩过的一次尺子事故，记在这**：分层后第一版残留检查把 `docs/architecture-design.md`
这类**根本没搬**的活文档也算成"残留 45 处"，差点被判成分层失败。负断言的候选集必须是
**实际搬走的那 54 个名字**（从 `git diff --cached -M --name-status` 的 `R` 行取），不是 docs 下全部文件。

⚠ **行号引用换算**：2026-10-22 给 86 个自有源码文件各加了 2 行 SPDX 头 ⇒ 那之前写成 `名字:行号` 的引用一律 **+2 才是今天的行号**
（受影响 5 个文件 / 16 处，全在带日期快照里，按"快照不回写"原则没有改）。引用这些串时先按这个偏移折一下。

⚠ **自指提醒**（上一轮就栽过一次）：本文件自己也在 `docs/` 里，所以 §四 用的是**不含本索引**的稳定口径；
若改成"含我"的口径，每编辑一次这份索引数字就会漂一次 —— 那不是数据变了，是尺子动了。

## 九、"零消费方"普查结果（2026-10-17，别重做）

尺子：拿每个跟踪文件的 basename 去所有跟踪文本（csproj/props/slnx/yml/ps1/py/cs/xaml/json…）里找引用；
**正控先过**（`Publish.ps1` 22 处、`scripts/verify.ps1` 23 处、`pack.ps1` 2 处、`global.json` 1 处、
`TrimmerRoots.xml` 1 处都被认出来）⇒ 尺子活着，再看候选。

候选 **100 个**（零调用方），逐个回判后**只有 1 个是真死文件**：

| 判定 | 对象 | 依据 |
|---|---|---|
| **已删** | `dotnet-tools.json`（55 B，`tools:{}` 空清单） | 本仓自己的审计早就写过："位于仓库根（正确位置是 `.config/`）且 `tools:{}` 为空 → **死配置**"；全仓无任何 `dotnet tool restore`，`verify.ps1:158` 的 `dotnet format` 是 SDK 自带。删后构建 0 警告 0 错误、7 套件 **1839 通过 / 0 失败 / 2 跳过**，与删前逐项同值 |
| 不是死文件（我一度误报） | 13 个 `Assets/*.png`（**2026-10-22 补全后为 48 个**） | 其中 7 个 `targetsize-*` 变体当时**由 `gen-msix-assets.py` 用 f-string 动态拼名生成**，我的 grep 只搜了字面名 ⇒ 缺席断言的尺子太窄。10-22 按官方限定符表补全到 48 个（`.scale-<N>` 显示缩放族 + `.targetsize-<N>_altform-unplated` 无底板族），生成处现在是 [scripts/gen-msix-assets.py:165-186](../scripts/gen-msix-assets.py) 的一张数据表；携带靠 `Microsoft.WinUI.props:35` 的 `Content Include="Assets/**/*.*"` 通配，**csproj 不需要加条目**（实测构建输出 48 个与源逐字相同） |
| 同上 | `licenses/*.txt` 44 份 | 全部被 `licenses/INDEX.json` 点名（未点名 0 份） |
| 惯例读取，非"无引用" | `.gitattributes`、`NuGet.config`、`.editorconfig`、`global.json` | git/MSBuild/NuGet 按约定读它们，不会有文本引用 ⇒ "零引用"对这类文件没有意义 |
| 有出处的工具，未动 | `gen-msix-assets.py`、`publish-with-prebuilt.ps1`、`onnx_fp16_quantize.py`、`verify-package-content.ps1`、`migrate-net11.ps1` | 文档分别记为"可复现生成 / 新增入口 / 正式工具 / 产物内容核对 / 仅 2 个正式工具之一"。`verify-package-content.ps1` 的问题是**没有调用方**（§七-4），不是该删 |
| 设计如此 | `tests/manual/**`(82 跟踪)、`scripts/probes/**`(16)、`scripts/precision/**`(21) | 手动取证件，本来就"无人调用"；计数口径 = `git ls-files`（`20a3198` 从 `tests/manual` 删掉 29 份零调用 harness，之前是 111）。`tests/manual` 磁盘 `os.walk` 195 文件 = 82 跟踪 + 113 生成件（`__pycache__`/输出），其中**未跟踪且未忽略 = 0**（`git ls-files --others --exclude-standard` 实测） |

⇒ 结论：**这个仓没有成片的死文件**。剩下的"精简"都是**取舍**而不是清理，全部列在 §七 等你拍。

## 十、被忽略的本地产物层：谁能重跑、门禁观不观测它（2026-10-22 分层同轮新增）

> **10-23 上古内容大扫除**：`artifacts/` 只留 `precision/`（门禁观测）与 `quality-ladder/v2/`（探针按路径读
> `v2/src.png`）；`tests/output/`、`tests/manual/safety/*/` 整层清空；`official_models/`（132 MB FP32 缓存）删除。
> 净回收 **274.1 MB / 230 项**，工作区（不含 `.git`）2.07 → 1.71 GB。
> 判据不是"谁提到过它"而是**谁把它当输出写**——跟踪文档里那些"读数取自某份 log"的溯源引用不构成运行期依赖，
> 清空后重跑门禁全绿即是证明。细节与那笔孤本账记在台账 §76。

`artifacts/`、`publish/` 的非在册部分、`tests/output/`、`tests/manual/safety/*/` 整目录被忽略，
对新克隆既跑不了也验不了——所以这张表**记的是角色，不是清单**（清单每天在变，记下来必烂）。

| 落点 | 谁产的 | 门禁观测它吗 | 删了怎么回 |
|---|---|---|---|
| `artifacts/precision/<kind>` 与 `artifacts/precision/probe/<kind>` | `src/TrueToneCap.Tools` 的样张落点 + `scripts/precision/*.py` | **是**——`scripts/verify.ps1:406-438` 把 `probe/<kind>` 拷进 `<kind>` 再判，`:584` 另读 `probe/nvenc_color` | 跑一次门禁即重生；**删这个目录会让色彩判据集体失明** |
| `artifacts/quality-ladder/v2/src.png` | 手工放的 4K 底图 | 间接——`scripts/probes/avif_backend_ladder_measure.py:171` 按路径读它 | 该探针手跑；`artifacts/quality-ladder/report-ladder.py` 族可重出其余 |
| ~~`artifacts/pr1-review/`~~ | 10-16 那次 PR 审查的取证现场 | 否 | **10-23 已清**；命令与读数都在 `docs/reviews/upstream/upstream-pr1-review-2026-10-16.md` 里，现场文件没了 |
| ~~`artifacts/t3-2026-09-28/` `display-audits/` `edid-fix/` `png-hdr-meta/` `ui-captures/`~~ | 各自那轮的 App 自检 / `--display-audit` 落点 | 否 | **10-23 已清**：由 `tests/manual/safety/*-cell.ps1` 与 App 的 `--display-audit` 重跑即回 |
| ~~`artifacts/cleanup-20260926/`~~ | 09-26 那次删除的 `quarantine.tar.gz`（137 件） | 否 | **10-23 按"清理上古内容"删除**。删前实测这 137 件从未进过 git（三条独立验证，见台账 §76）⇒ 它是孤本而不是 git 的重复备份，从此无处可取；它们的结论都还在 `docs/` 里 |
| ~~`artifacts/backup-consolidated/`~~ | 曾存 §七-6 那处未提交改动的副本 | — | **2026-10-22 已删**：备份构筑一律不许存在；那条旋钮的实测数已由 `docs/reviews/usability/pipeline-and-usability-review-2026-09-27.md` §⑧ 记在文档里 |
| `publish/PLAN/{tools,fonts}` | `Publish.ps1:212,214` 的 `$toolsSrc/$modelsSrc` 从 `src/…/Resources` 与 `src/…/Models` 取源 | **是**——门禁的能力探测把 `publish/PLAN/tools/ultrahdr_app.exe` 当第二候选 | 重跑发布链；`tools-manifest.json` 有逐件 sha256 |
| `publish/TrueToneCap-v0.3.9-beta/` | 按 `v0.3.9-beta` tag 重出的正式包 | 发布期校验读它的 `build-info.json` | 重新出包（`-OutputDir` 是 `Remove-Item -Recurse`，别指着旧目录跑） |
| ~~`publish/msix-0.3.7-from-backup-2026-10-21/`~~ | §七-5 从已 prune 的对象导出的 0.3.7 MSIX | — | **2026-10-22 已删**：它是 `*-from-backup` 型备份构筑；真要这个包，按 `v0.3.7-beta` tag 重跑 `-Msix` 即可（构建产物不逐字节相同，但源码在树上） |
| `tests/output/` `tests/manual/safety/*/` | harness 的 `--out` 落点 | 部分（`app-settings-migration`/`app-window-probe` 由门禁自己写） | 10-23 整层清空。**实测门禁会自己重建这两个目录**：清空后跑一轮 = 1871 断言 / 0 失败，`tests/output/` 里长出那两项 |

⚠ **本机的 `du` 不可信，别拿它当体积尺**：本轮 `du -sk publish/PLAN` 给 38,232 KB，
而它的子目录 `du -sk publish/PLAN/tools` 单独给 186,093 KB——父比子小，自相矛盾。
逐文件逻辑求和（PowerShell `Measure-Object Length -Sum` 或 Python `os.walk`）两处都给 181.7 MB / 17 个文件，
与 10-21 台账记的"PLAN 182"一致 ⇒ 本节的体积数一律用逻辑求和口径，`du` 读数作废。

⚠ 本轮两次自查纠正：① 分层后第一版残留检查把**没搬**的 10 份活文档也算成"残留 45 处"，差点误判分层失败；
② "`tests/manual` 有 18 个遗留脚本"是用 `*.py|*.ps1|*.cs` glob 数的，里面混着 6 个在册文件，
真实未跟踪遗留要问 `git ls-files --others --ignored --exclude-standard`。**负断言的候选集要从 ref 取，不能从 glob 取。**

