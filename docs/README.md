# docs 索引 / 怎么读这批文档

> 本文件是 `docs/` 的**入口**，2026-10-17 新建。此前全仓没有 docs 索引：
> `README.md:230/:233` 只点名了主台账与一个 `docs/pipeline-review-*.md` glob，
> `CHANGELOG.md:13` 有一句"同目录其它 `docs/*.md`"的泛称。
> 下面的每个数字都是当场量出来的（复查口径见 §八），不要凭本文件的历史版本推断。

## 一、先看规则（本仓已拍板，别绕过）

1. **文件名带日期的 `.md` 是取证快照**：当时为真就是价值所在。发现其中有错**不回写**，
   要纠正在**新文件**里写，或在**无日期的活文档**里更新。改快照 = 伪造历史。
2. **例外只有一条**：`handoff-*` 那两份虽然名字带日期，实际是**跨周追加的运行台账**
   （见下节），按活文档对待。
3. **文档三分工**（2026-09-20 拍板）：
   `README.md` 只写新增能力 + 工程规范；`CHANGELOG.md` 只写变更流水（现象与影响）；
   `docs/handoff-*.md` 写根因、判据设计、变异实验、被推翻的旧结论。
   ⚠ 现状偏离：`CHANGELOG.md` 自己 `:12` 写着"根因分析…都在维护者文档里"，
   而正文 `:451 :521 :682 :691 :921 :1063` 六处（全文"根因/事后分析"命中 14 处）就是根因分析 ⇒ 与自己那句冲突；
   `CHANGELOG.md:13` 还把范围放宽成"其它 `docs/*.md`"——这句与上面那条分工不一致，待你定。
4. **"过时"不是删除理由**。快照的删除只在两种情况下才成立：证明它不记录任何唯一证据，
   且你明确授权。本索引只负责让你一眼看见有什么。

## 二、台账（运行中，不是快照）

| 文件 | 行 | 状态 |
|---|---|---|
| `handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` | 7,887 | §编号已到 **70**，最后追加 2026-10-21；691,101 B，**占 `docs/` 总字节的 30.6%**（次大的是 `pipeline-and-usability-review-2026-09-27.md` 381,691 B；字节口径见 §四 末）。任务账本、拍板记录、被推翻结论都在这里 |
| `handoff-avif-encoder-refactor-2026-09-16.md` | 406 | 前一阶段的交接，已收尾 |
| `CHANGELOG.md`（仓库根） | 216,238 B（blob） | 变更流水；发布记录与 tag 对账也记在这 |

要找"某件事当时为什么这么做/后来怎么翻的"，先在这三份里搜，别去审计快照里找结论。

## 三、活文档（无日期名 ⇒ 有义务跟代码同步）

共 10 份（行数是**本轮改完之后**的实测值）：`architecture-design.md`(891) `ocr-model-landscape-2026.md`(429，只带年份)
`roadmap-v0.4.md`(318) `ocr-parallel-analysis.md`(252) `performance-analysis.md`(241)
`dependency-management.md`(232) `ocr-pipeline-review.md`(168) `repo-hygiene-review.md`(119) `ocr-accuracy-analysis.md`(111) `vscap-mode.md`(45，VS-Cap 可选模式说明)。
（本索引自己不算在内 —— 它也是无日期名，但它是入口不是待同步对象。）

**本次逐条核过的具体不同步点（只有这两份需要动，其余点名符号都能在 `src/` 里搜到）**：

- `architecture-design.md:30` 把 `Encoding/ManagedBmpEncoder.cs` 列进"**当前确实存在**"名单 ⇒ 该文件已于
  `4368eed`(v0.3.6) 删除，且 `CorePipelineTests.cs:4063-4065` 有一条**反向断言**禁止它再出现。已就地更正。
  同一句话里其余 5 个名字（`IccStore.cs` / `NativeEncoderGuard.cs` / `ManagedTiffEncoder.cs` /
  `AvifHardwareProbe.cs` / `GpuCapability.cs`）实测**都还在**，只有 BMP 这一条是假的。
- `repo-hygiene-review.md`：`:111` 写 `HDRPreviewWindow.cs`"文件仍存在" ⇒ 已于 `a54bb5c`(v0.3.7) 删除
  （反向锁 `Test_Overlay_HasNoGpuBackgroundPath` 在位，`CorePipelineTests.cs:156/:4689`）；
  `:110` 那条"把 docs 合并成 3-4 份主题文档"与本文第一节的快照规则直接冲突。
  ⇒ 已按它自己的"状态更新"体例追加，原句不删。

⚠ `architecture-design.md` 里那些**带 `✗` 前缀或在标注表里**的缺席文件（`GpuToneMapper.cs`
`ShaderLoader.cs` `ToneMapping.hlsl` `GamutMapper.cs` `GpuEffectProcessor.cs` `ManagedJpegEncoder.cs`
`NvencAvifBackend.cs` `MosaicEffect.hlsl`）是**故意留的"曾存在"标记**，不是漏改——别再去"修"它们。

## 四、主题族（同族读最新那一份即可，旧的是证据不是现状）

| 族 | 成员日期 | 最新（读这份） |
|---|---|---|
| 审查链 `pipeline-review*` | 08-25 / 09-14×4 / 09-15 / 10-04×2 | `pipeline-implementation-review-2026-10-07.md` |
| 可用性 `pipeline-and-usability-review*` `usability-audit*` | 09-15 / 09-27×2 / 10-01×2 / 10-02 | `usability-audit-2026-10-02.md` |
| MSIX / 商店（全部 10-07） | aot / packaging / size / gap / submission / winml | `realmachine-msix-verification-2026-10-07.md`（真机安装实测，mtime 最新） |
| OCR | 08-25×3（活） / 2026 综述 / 10-07×2 | `winml-migration-plan-2026-10-07.md` + `ocr-model-replaceability-2026-10-07.md` |
| 性能 | 08-25（活）/ 10-07 / perf-G | `performance-analysis-2026-10-07.md` |
| 依赖 | 08-25 / 09-14×2 / 10-07 | `dependency-update-analysis-2026-10-07.md` |
| 压缩强度 | 10-07×2 | `encoder-params-and-smallest-tier-2026-10-07.md` |
| 上游 PR | 10-16 | `upstream-pr1-review-2026-10-16.md` |

零散快照（单份，无后继）：`cicd-pipeline-review-09-14` `git-tracking-audit-09-14` `re-audit-09-14`
`real-machine-test-report-09-14` `test-verification-09-14` `docs-staleness-audit-09-14`
`gpu-acceleration-availability-09-16` `root-cause-hdr-multimonitor-09-16` `capture-source-regression-09-16`
`release-review-09-16` `windows-system-notification-09-16` `pending-changes-triage-09-14`。

**体量（稳定口径：不含本索引自己）**：`docs/` 除本文件外 = **64 份 / 30,334 行 / 2,257,559 B（2.15 MiB）**（2026-10-21 在 `f0fc56c` 提交树内实测；
字节按 `git show HEAD:<path>` 的 LF blob 数——主工作树是 CRLF checkout，同一批 64 份从磁盘量是 2,275,008 B，虚高 0.77%），
其中带日期 54 份、无日期 10 份（即 §三 那 10 份活文档）。含本索引是 65 份，行数要加上本文件自己的当前行数；
本文件会被继续编辑，所以"含本索引"的行数不给死数。
（上一轮 2026-09-26 审计记的是"38 份 / 30 份快照 / 15,954 行"；现在行数是它的 **1.90 倍**，那份计数已过期，别拿它做体积或成本判断。
同一天更早一次记的"60 份 / 28,973 行"在 `36374ac` 树里是真数，`f0fc56c` 把 4 份 10-07 快照落了地 ⇒ +4 份 / +1,361 行。）

## 五、孤儿（零入站引用，但仍是证据）

判据 = 除本索引外没有任何跟踪文件写出它的全名（`git ls-files` 全文匹配，2026-10-21 在 `f0fc56c` 树内重跑）：

`aot-and-store-publishing-analysis` · `compression-effort-3tier-design` · `docs-staleness-audit`
· `pipeline-independent-review` · `pipeline-review-e2e` · `pipeline-review-plan` · `release-review`
· `windows-system-notification`（均带各自日期后缀）

⚠ 两处别扭：`docs-staleness-audit-2026-09-14.md` 自己点名了一堆本仓文档，却只被本索引提到——
它就是当年那次"文档体检"的结论，**要查历史判定先看它**；`real-machine-test-report-2026-09-14` 与 `upstream-pr1-review-2026-10-16`
本轮从孤儿名单里摘掉（前者被 `docs/roadmap-v0.4.md` 点名 1 处，后者被主台账 + `roadmap-v0.4.md` + 本索引共 4 处点名，随 v0.3.9-beta 入库后已不再无人引用）。

## 六、断链：曾经只有一条，已修

| 出处 | 指向 | 处置 |
|---|---|---|
| `scripts/migrate-net11.ps1:135` | `docs/dotnet11-migration-plan.md`（该文档在 `0aece78` 被删） | **已改**（2026-10-17）：脚本现在给出可取回原文的命令 `git show 0aece78^:docs/dotnet11-migration-plan.md`（实测 RC=0，标题 "TrueToneCap .NET 11 迁移计划"） |

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

## 七、待你拍的六件（都是取舍，不是清理）

1. **54 份快照要不要真挪进 `docs/reviews/<族>/`**：实测成本 = **94 处 `docs/<文件名>` 路径引用 / 跨 35 个跟踪文件**
   （`CHANGELOG.md` 20、主台账 13、`handoff-avif-encoder-refactor` 7；2026-10-21 在 `f0fc56c` 树内按 `git ls-files` 全文重跑，
   只数写成 `docs/<带日期文件名>` 的引用，"文件指自己"已排除——本索引列的是不带前缀的短名，所以它自己贡献 0 处）。
   台账 §54.4 记的"22 处"已过期 4 倍。
   现状=不挪，靠本索引分层（零引用改写）。
2. **`TrimmerRoots.xml` + `App.csproj:131-136` 这一对**：csproj 无条件 `<TrimmerRootDescriptor Include=…>`，
   但全仓 `PublishTrimmed/PublishAot/TrimMode/IsAotCompatible` **零命中**，`Publish.ps1:408-409` 自己写明
   "`CorePublishAot` 没有任何 csproj 消费"，且 `docs/roadmap-v0.4.md` 里 **AOT/裁剪 0 次提及**
   ⇒ 今天完全不起作用。要么成对摘掉（改构建输入，6 行 + 1 文件），要么把 `-CoreAot` 真接通让它有意义。
   ⚠ 不能只删 xml——csproj 那行会指向空路径。
3. **一次性脚本归置**：`migrate_forensic_paths.py`（文档自述"一次性脚本"，`.workbuddy-ai` 引用已 21→0）、
   `add-spdx-headers.py`（⚠ 本轮重测：按它自己的口径 `.c/.cs/.h/.hlsl/.ps1/.py` 命中 **262 个跟踪文件，其中 87 个没有 SPDX 头**——
   含 `src/` 下 4 个 `.cs`（`MarkdownDocumentWindow` / `PrivacyPolicyWindow.xaml.cs` / `ThirdPartyNoticesWindow.xaml.cs` / `CoreLog`）
   和我自己写的 `gen-third-party-notices.py`、`mutation-verify.ps1`；门禁没有任何一格管 SPDX 头，`CorePipelineTests.cs:3035` 那条管的是
   `licenses/INDEX.json` 的表达式，不是源码头。原先这里写的"464 个跟踪文件都已带头"是错的）
   、`migrate-net11.ps1`（net11 迁移已完成；它的断链本轮已修）
   —— 退役它之前要么先把它跑完补齐那 87 个，要么原地留着当可复跑工具；直接退役等于把缺口永久留在原地。
   ⚠ `export_onnx_fp32.py` 文档里**一次都没提**，而配套的 `onnx_fp16_quantize.py` 有 3 处文档记录 ⇒ 要么补出处要么一起处置。
4. **`scripts/verify-package-content.ps1` 零调用点**（CI 两份 workflow 都不引用）：接进 `release.yml`，
   还是从 README 的规范段摘出去——它 2026-09-26 是"从临时目录提升入库"的，删它等于自食那个决定。
5. **磁盘回收（都在忽略区，可再生）**：`src/bin+obj` 1.5 GB、`artifacts/` 2.5 GB（**含本轮那个手测包**，
   测完再清）、`tests/output` 85 MB、`publish/_scratch` 161 MB、`official_models` 132 MB（重下要走
   `download_models.ps1`，代价=OCR 首次要重下模型）；另有本机 `.git` 里那 89 MB `AppPackages` 只挂在
   `refs/backup/main-pre-strip` 上（远端已无），删该 ref + `git gc --prune=now` 可回收，
   代价=失去那次出包的逐字退回。
6. **`artifacts/wt-gm` 里有一处未提交改动**：`src/TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs`
   在分离 HEAD `ca7ed99` 上被改过 ⇒ 可能是没落地的工作，本轮**没碰**，请你确认留还是弃。

## 八、这些数字怎么重跑

```bash
# 份数（不含本索引）= 64
ls docs/*.md | grep -vc '^docs/README.md$'                        # 磁盘口径；docs/README.md 自己不数
# 行数 / 字节（blob 口径，与 §四 那组 30,334 行 / 2,257,559 B 逐字相等，2026-10-21 实跑对过）
for f in $(git ls-files docs | grep -v '^docs/README.md$'); do git cat-file blob "HEAD:$f"; done | wc -l -c
# 活文档（无 2026-MM-DD 后缀）：上面这条给 11 是含了本索引，去掉就是 §四 的 10 份
ls docs/*.md | grep -v '2026-[0-9][0-9]-[0-9][0-9]'
# 入站引用 / 孤儿：把每个 docs/<名> 在所有跟踪文本文件里搜一遍（排除构建副本、排除文件指自己）
# 断链：文中 token 命中 + 目标存在性两条一起判，只命中一条会误报
```

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
| 不是死文件（我一度误报） | 13 个 `Assets/*.png` | 其中 7 个 `targetsize-*` 变体**由 `gen-msix-assets.py:138-144` 用 f-string 动态生成**，我的 grep 只搜了字面名 ⇒ 缺席断言的尺子太窄。文档记的"13 个资产可复现生成"是对的 |
| 同上 | `licenses/*.txt` 44 份 | 全部被 `licenses/INDEX.json` 点名（未点名 0 份） |
| 惯例读取，非"无引用" | `.gitattributes`、`NuGet.config`、`.editorconfig`、`global.json` | git/MSBuild/NuGet 按约定读它们，不会有文本引用 ⇒ "零引用"对这类文件没有意义 |
| 有出处的工具，未动 | `gen-msix-assets.py`、`publish-with-prebuilt.ps1`、`onnx_fp16_quantize.py`、`verify-package-content.ps1`、`migrate-net11.ps1` | 文档分别记为"可复现生成 / 新增入口 / 正式工具 / 产物内容核对 / 仅 2 个正式工具之一"。`verify-package-content.ps1` 的问题是**没有调用方**（§七-4），不是该删 |
| 设计如此 | `tests/manual/**`(82 跟踪)、`scripts/probes/**`(16)、`scripts/precision/**`(21) | 手动取证件，本来就"无人调用"；计数口径 = `git ls-files`（`f0fc56c` 从 `tests/manual` 删掉 29 份零调用 harness，之前是 111）。`tests/manual` 磁盘 `os.walk` 195 文件 = 82 跟踪 + 113 生成件（`__pycache__`/输出），其中**未跟踪且未忽略 = 0**（`git ls-files --others --exclude-standard` 实测） |

⇒ 结论：**这个仓没有成片的死文件**。剩下的"精简"都是**取舍**而不是清理，全部列在 §七 等你拍。
