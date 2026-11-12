# 交接：色彩声明 CICP 优先（已交付）+ ICC TRC 瘦身（进行中）

> 交接时点：2026-11-12 深夜。会话被用户要求暂停打包。
> 已交付三连提交（4ec35b5 → b700c67 → 7529dc4）全部 verify -All 绿；
> 工作区未提交的 ICC 瘦身改动已在续作中收口，B/C 实验跑出数字、外部规范查证完成（见 §6）。

## 1. 已交付（本轮三提交，全部门禁绿）

| 提交 | 内容 |
|---|---|
| `4ec35b5` | JPEG LI 编码改 ttc_jpegli.dll 进程内主路径（exe 回退保留），逐字节对照门禁 15/15 |
| `b700c67` | ICC 口径收口①：只有非 sRGB 才嵌（sRGB 是 JPEG 隐含默认）——JPEG 家族/GainMap 底图 |
| `7529dc4` | **色彩声明 CICP 优先（全量）**：码点可声明空间（Rec.709/Rec.2020/P3）在有码点通道的容器（PNG cICP / AVIF nclx / JXL color fields）里只写码点、不嵌 ICC；ICC 收缩为码点盲空间（AdobeRGB=旁路防御，非功能）与无码点容器（JPEG 家族/WebP/TIFF）的通道。动图按容器分派（APNG 走 CicpDeclarable 门、WebP 非sRGB 才嵌）。verify -All 8 套件 2730 断言全绿（含两处外部裁判换口径） |

- 决策文档：`docs/reviews/pipeline-review/cicp-first-color-declaration-2026-11-12.md`
  （§3 = 两档×全格式最终产物形态表；§4 = AdobeRGB 分支定位；§5 = 外部裁判同步记录）
- 总判据：`FormatHelper.CicpDeclarable(tag)`（== "AdobeRGB" 即码点盲），doc 注释写明两档契约。
- 记忆已同步：`.workbuddy/memory/2026-11-12.md` 的「CICP 优先口径」条
  （全仓不存在 `truetonecap-repo-gates.md`，旧交接此处指向失准，2026-11-13 订正）。

## 2. 交接时点的工作区快照（现已在 `401271d` 提交，git status 已干净）

> 下面是 2026-11-12 交接时的 `git status` 实录，**是历史快照、不是当前状态**；
> 这些改动连同文案修正、实验脚本、文档回填已在续作中收口为本地提交 `401271d`（未 push，见 §3）。

```
 M src/TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs   ← 三处 ICC 构建器 TRC 共享 offset
 M src/TrueToneCap.Tools/IccCheck.cs                              ← +自产 profile 尺寸台账段
 ?? scripts/probes/icc_slim_variants.py                           ← 激进变体实验（刚修完 bug 未重跑）
```

### 2.1 TRC 共享 offset 的实现（三处构建器同机制，全部已改）

ICC 规范允许多个 tag entry 指向同一 offset（微软系统 sRGB 件与 Apple 自家 profile 都这么写）。
此前 rTRC/gTRC/bTRC 把同一份曲线各写一份：

| 构建器 | 位置 | 之前 | 之后（实测） |
|---|---|---|---|
| `BuildValidSrgbIcc`（SDR 手写件，dataStart=252） | ColorProfileProvider ~:240-270 | 2100B（256 点 curv 524B×3=1572B 重复） | **1052B** |
| `BuildHdrPqIcc`（HDR PQ 件） | ~:1014 | ~25.1KB（4096 点 curv 8204B×3 重复） | **8696B** |
| `IccProfileBuilder.Generate`（判据样张用） | ~:1814 | 同款三写 | 同款共享 |

实现手法：`Dictionary<byte[],int>(ReferenceEqualityComparer.Instance)` 按**数组引用**去重，
同一数组 → 同一 offset、只写一次；写入循环用 `HashSet<byte[]>(同比较器)` 跳过已写块。
三处 TAG 表写 `blocks[i].o` 不变；唯一变化是数据区循环改写 `uniqueBlocks`。

### 2.2 已验证的事实（实测，不是推断）

- `--icc-check` 尺寸台账：SDR sRGB/P3/2020/Adobe 全部 **1052B**；HDR PQ (P3) **8696B**。
- `--color-tests` 全绿（Debug）：矩阵逐值、LUT 256 级、烘焙 sRGB 恒等、PQ TRC 中点等判据全过——
  共享 offset 不改变解析语义。
- **lcms2 对共享 offset 的 buildTransform 实测通过**：从新 TIFF 夹具取出 1052B profile，
  `ImageCms.buildTransform` 成功，灰阶 256 ramp 双向 Δmax=0。共享 offset 的"读开"与"用"两道门都过了。
- `PatchSrgbPrimaries`（:898）按 **tag 签名**定位（非硬编码 offset）⇒ dedup 后照常工作（实测）。
- `IsEmbeddableIcc`（FormatEncoders）TRC 判据只看 12 字节头，注释明写「curv / para 都由 12 字节头起」
  ⇒ para 形态也能过门。

### 2.3 激进实验（已跑完，数字如下；**两档都不采纳**）

`scripts/probes/icc_slim_variants.py` 在 A 档产物字节之上手术出两个变体，
lcms2 双向换算（灰阶 256 ramp + 暗端 0..40 + 16³ RGB 网格）判 ≤1/255：

| 档 | 形态 | 尺寸 | fwd/bwd Δmax（8-bit） | 裁决 |
|---|---|---|---|---|
| A（产品） | 256 点 curv 共享 | **1052B** | 0 / 0 | ✅ 落地 |
| B | 64 点 curv 共享 | 668B（省 384B） | 0 / 0 | ❌ 不采纳（见下） |
| C | para type-3 共享 | 560B（省 492B） | 0 / 0 | ❌ 不采纳（规范越界） |

B 的 Δ=0 不是容差掩盖：另按 16 位精度独立量了曲线插值误差（不改读手，纯数值），
相对精确 sRGB EOTF 的最大误差 **256 点 = 0.78/65535 LSB，64 点 = 6.32/65535 LSB**
（≈8-bit 0.025 LSB）——8-bit 产物确实看不出差，但 16 位通路误差涨了 8 倍，且
"老读手按 256 点假定解析"未验证（U）。ICC WP58 §Curves 明确：8-bit 数据对应 256 点采样，
点数过少在 CMM 线性插值下可能产生 contouring（见 §6）。

C 的 `'para'`（parametricCurveType）由 **ICC White Paper 35** 确证是 **v4 才引入**的类型，
且 ICC.1:2022 (v4.4) 还修正了它的函数公式；把它写进头为 `0x02100000`（v2.1.0）的 profile
属规范越界。lcms2 能读不等于老读手能读，拒读的后果是整份色彩声明丢失。
PQ 件不在变体范围：ST.2084 不是幂函数形，para 表达不了；4096 点 curv 共享就是它的下限形态。

**脚本两个 bug 已修并重跑确认**：
1. `rebuild_with_trc` 漏了 `out[trc_off:] = payload` —— TRC 数据区全 0 ⇒ "lcms 开 profile 能过、
   建 transform 才炸"。教训已写进脚本注释：**读开与用是两道门**。
2. 夹具 glob 按名字排序拿到旧轮次 GUID 文件（旧 2100B 字节）⇒ 已改按 mtime 降序取最新。
   同修 `icc_selfbuilt_referee.py` 的 `srgb_tiffs[0]`（§3 第 1 项）。
3. 顺手修版本打印：头字节 `02100000` 原被 `v%d.%d` 打成 "v2.16"，实为 **v2.1.0**。

## 3. 剩余工作清单（续作已全部收口）

1. ✅ **`icc_selfbuilt_referee.py` mtime 取最新**：`sorted(..., key=os.path.getmtime, reverse=True)`
   并写明原因；`icc_slim_variants.py` 同样口径。重跑裁判 `[RESULT] PASS`。
2. ✅ **重跑变体实验**：A 1052B / B 668B（省 384B）/ C 560B（省 492B），lcms 双向 Δmax 全 0；
   底图为 `--color-tests` 新鲜夹具（`version=v2.1.0`，头 `02100000`）。数字已回填 §2.3。
3. ✅ **文案去字面量（三处级联）**：`FormatColorFacts.cs` / `MainWindow.xaml.cs`
   `CoreColorFactsTexts` / `LocaleManager.cs` ZhToEn 同改为「（实测产物 ICCP 块在）。」，
   英文译文去掉 "a 2100-byte profile"；`MainWindow.xaml.cs:765` 历史注释改为
   「字节数随构建器变化，别抄进文案」。Grep 复核：新句三处各 1 次、旧句三处各 0 次；
   `git diff --check` 干净。
4. ✅ **裁决 B/C**：用户拍板 **保持 A**。依据见 §6 的 ICC 官方查证（WP58 / WP35 /
   ICC.1:2022）；B/C 只留实验记录。
5. ✅ **门禁**：
   - `scripts/verify.ps1 -Configuration Release -SkipPrecision`：**构建 0 警告 0 错误**；
     `[1.6/2]` 发布门禁回归 ✅；`[1.8/2]` 色彩管线 3/3 + `--avif-hdr-depth` ✅；
     `[1.9/2]` 宿主新鲜度、`[1.95/2]` 闸门接线、`[1.96/2]` 日志行格式 8 臂 0 MISMATCH、
     `[1.97/2]` 设置迁移 + XAML 窗构造、`[1.98/2]` 侧车归档 ✅；
     `[2/2]` **8 套件 2730 断言全绿**（unit 2070 / color 291 / encoding 23 / service 2 /
     ocr 12 / qr 47 / format 52 / usability 233），`VERIFY_LASTEXIT=0`。
   - 被 `-SkipPrecision` 连带跳过的 `[1.7/2]` 外部判据与 `[2.5/2]` 无损容器裁判，
     本轮**分组单独跑完，全部 PASS / 唯一 SKIP 是脚本自带的已知前置缺失**：
     chroma_knob / param_sweep / gamut_depth / ramp_levels / cicp_consistency /
     jpeg_icc_readback / icc_readback_matrix / av1c_vs_bitstream（8 条 rc=0）；
     audit_roundtrip（20 文件 0 问题）、wg_container_roundtrip（36 容器不一致 0）、
     tiff_png_referee、gen-third-party-notices --check、combo_locale、
     icc_selfbuilt_referee（lcms PASS）、nvenc_matrix_probe 全部 rc=0；
     `icc_profile_id_check` rc=3 SKIP（本机无带非零 ProfileID 的真实 profile，与基线一致）。
   - ⚠️ 没能拿到「单条命令全绿」的原因见 §4 的两条环境坑，**不是产物问题**：
     `[safe-delete]` 对单轮累计删除 ≥50 个文件要求确认，会截断 `[1.7/2]`；
     `GainMapColorProductProbe.cs:62` 递归删 `.gate-probe-out/`，日志不能放那里。
6. ✅ **CHANGELOG 加条 + 本交接回填**：§2.3 实验数字、§3 收口记录、§4 环境坑、§6 规范查证。
7. ✅ **记忆同步**：`.workbuddy/memory/2026-11-12.md`（共享 offset 手法 + 夹具 mtime 教训 +
   「别把字节数抄进文案」重演一次 + 两条环境坑）；`.gitignore` 增 `.workbuddy/` 避免误提交。

## 4. 雷区（下一次继续必读）

- **目录里旧夹具**：`%TEMP%/TrueToneCap_ColorTest/` 有 25+ 个历史 GUID 文件。任何从那里
  读产物的脚本必须按 mtime 取最新（`icc_selfbuilt_referee.py` 与 `icc_slim_variants.py`
  **均已改**，见 §3 第 1、2 项）。
- **字节数不进文案/判据**：WebP 句的"2100 字节"就是上一个雷（今天刚过期）。IccCheck 台账
  注释里已写明这条纪律。
- **产品形状钉**：`Test_QualityChain_JpegLiLibPathDeliveryChain` 等对 JpegLiNative 参数串的
  形态钉没动；ICC 阈值收口判据（⑧）钉的是结构不是长度，dedup 后仍绿（已实测）。
- **跨格式字节一致**：icc_readback_matrix 的"同 tag 跨格式必须同一份 profile"判据靠的是
  全格式走同一构建器 ⇒ dedup 后天然一致，无需单独处理。
- **Bash heredoc 的 `\n`**：往 python 里写含 `\n` 的字符串时用 Edit 工具直改更稳
  （heredoc 转义踩过一次：f-string 被拆成真换行）。
- **verify 偶发单红**：本日出现过一次 `--usability-tests` 单套件 1 失败（231/232），
  三轮复现 + 顺序连跑全绿 ⇒ 判偶发（本机 360 主动防御扫新文件 / 编码器瞬时超时），
  不是本改动引入。再遇到先重跑再深挖。
- **⚠ `[safe-delete]` 累计删除阈值（2026-11-12 续作实测）**：WorkBuddy 沙箱对**单次工具调用
  内**累计删除 ≥50 个文件要求确认，会静默截断 `verify.ps1` 的 `[1.7/2]`：
  `sweep(52) + ramp(11) + cicp(28) + audit(20) + wg(36)` 远超阈值，表现为 audit/wg 假红，
  而**单独跑同一条判据（含同一 `--samples` 参数）全绿**。
  处理：把样张目录先 `Rename-Item` 移走（不删文件），或 `-SkipPrecision` 跑 8 套件后
  把外部判据分组单独跑；**不要据此改产品或判据容差**。
- **⚠ verify 日志别放 `.gate-probe-out/`**：`GainMapColorProductProbe.cs:62` 每轮
  `Directory.Delete(dir, recursive: true)` 删掉整个 `.gate-probe-out`；`Tee-Object` 把日志
  写那里会锁住文件 ⇒ 探针 `IOException`（退出码 -532462766）假红。日志放 `artifacts/`。
- **⚠ Bash 的 `python` ≠ 门禁的 `python`**：Git Bash 里 `python` 是 WorkBuddy 自带 3.13
  （**无 Pillow**），`verify.ps1` 在 PowerShell 里用的是系统 Python 3.12（Pillow 12.3.0）。
  单独复跑 `scripts/precision/*.py` / `scripts/probes/*.py` 要用 **`py -3`**，否则会看到
  `ModuleNotFoundError: No module named 'PIL'` 这种假失败。
- **WP58/PDF 留档别放 `.gate-probe-out/`**：同上会被删；放 `artifacts/icc_refs/`（已 gitignore）。

## 5. 关键决策记录（为什么是现在这个形态）

- **CICP 优先 + AdobeRGB 旁路防御**：用户三连裁决（当日）确认产品只输出 Rec.709/Rec.2020
  两档；AdobeRGB 分支保留是因为判据样张宿主绕过 UI 直赋 tag（仓库既有 7 处防御点同源），
  删掉它 = 旁路标签来了产出零声明错件。
- **TRC 共享而非换曲线**：共享 offset 是零数值变化的纯布局优化（判据全绿才敢动）；
  换曲线（B/C）引入数值差异，必须过 lcms 双向 ≤1/255 才考虑。
- **para 不直接采纳**：数学精确但规范越界（v4 tag 写进 v2 件），仓库纪律是"读手 honored"
  ——lcms 过了不够，老读手兼容面未验证，且正当做法要连 desc/cprt 一起迁 v4。

## 6. 外部规范查证：ICC 官方真的有「更小的 ICC」吗（用户 2026-11-12 提问）

证据分级：**E2 = ICC/ISO 官方原文**；**E1 = 本机实测**；**U = 未验证**。
本轮联网直取 color.org 原文（PDF 与抽取正文留档于 `artifacts/icc_refs/`，该目录被 gitignore；
**不要放 `.gate-probe-out/`——`GainMapColorProductProbe.cs:62` 每轮会递归删掉整个目录**）。

**有，但不是给「嵌入图像的小 sRGB 件」用的。** 用户记的应该是
**ICC White Paper 58《Minimizing the size of output profiles》，2026-03，Introductory**
（https://media.color.org/icc_white_paper_58_minimizing_profile_size.pdf）。
它点名的体积敏感场景原文是：*"Exchange of colour images and graphics on the web"*、
*"Embedding profiles in PDF/A files"*、*"Variable data printing workflows"*，
并说明是为了 *"data transmission over networks and impact on web performance"*（E2）。
但**通篇针对 output（CMYK LUT）profile**：ICC registry 里 CMYK 件平均 **3.45MB**（0.7–8.4MB），
NTNU 学生实验把 FOGRA39 基准 192KB 压到 **<50KB**（删非必需 tag / 三 intent 共指同一 offset /
曲线换 2–8 点 / LUT 降 8-bit；8-bit 那步让 ΔE*ab 均值 0.72→1.28）（E2）。

**对我们这类件，WP58 的四条原文反而全部支持「保持 A」：**

1. **量级**：*"The older 'Matrix/TRC' profiles can encode just a 3x3 matrix and parameters
   for a set of one-dimensional curves … The new CICP tag enables the profile to simply carry
   code points … All these methods can yield a profile of around 1Kb in size."*（E2）
   ⇒ 我们的 **1052B 正好就是官方说的紧凑件量级**，不是还能砍一半的起点。
2. **共享 offset 有官方依据**：*"it allows multiple intents to have the same offset, so that
   a single AToB tag can be used for two or even all three required intents"*（E2）
   ⇒ A 档从「微软系统件这么写」的经验证据升格为规范明文；本机实测系统件
   `rTRC/gTRC/bTRC` 同指 offset 1084 是 E1 佐证。
3. **点数**：*"for output devices the sampling precision should be equal to or less than the
   data precision of the output channels (e.g. 256 points for 8-bit data)"*，
   且警告 *"using a very small number of points … could possibly lead to perceptible errors,
   such as contouring"* 在 CMM 线性插值下（E2）⇒ **B 档 64 点正是它警告的方向**。
   （同页 21/42 点那句是 CMYK 的 A-curves 经验值，别错套到 sRGB TRC 上。）
4. **合规边界**：*"a v4 ICC profile with only a single unique rendering intent would violate
   the Perceptual Reference Medium requirements"*（E2）——官方自己也在强调瘦身不能越过规范。

**「流媒体/视频」那条线是 CICP tag，不是更小的 profile**：ICC.1:2022（profile version
**4.4.0.0**，对应 ISO 15076-1:2025）新增 `cicpTag` / `cicpType`，tag 签名 `'cicp'`，
12 字节（4B 类型 + 4B 保留 + 4 个 uint8：ColourPrimaries / TransferCharacteristics /
MatrixCoefficients / VideoFullRangeFlag），码点取自 ITU-T H.273 = ISO/IEC 23091-2。
color.org v4spec 原文：*"It includes a Coding Independent Code Points (CICP) tag and type
for video signal identification, which supports HDR processing"*；ICC.2 修订案原文：
*"The CICP tag supports HDR workflows by enabling standard video encoding parameters to be
encoded as metadata in an ICC profile"*（E2）。
⇒ 我们的 **CICP 优先**（容器层直接写 PNG cICP 4 字节 / AVIF nclx / JXL color fields）
与 ICC 为视频生态加的这条通道同向，且比「在 ~1KB profile 里再塞一个 cicp tag」更省。

**C 档的规范判断从推断升为 E2**：ICC White Paper 35 原文 *"In Version 4 of the ICC Profile
Specification, parametricCurveType was introduced as an alternative to curveType"*；
ICC.1:2022 变更表还列了 *"parametricCurveType functions corrected"*。
⇒ `'para'` 是 v4 专有类型，写进 v2.1.0 头（`0x02100000`）确属越界，不是"可能不兼容"。

**未来可选项（现在不动）**：WP58 §Compression 提到 ISO/IEC 18181-1:2024 定义 JPEG XL 里
用 Brotli 自定义字典无损压缩 ICC profile，但 *"At this time ICC makes no recommendation on
profile compression … under evaluation"*（E2）。iccMAX 的 `'cenc'` 指向外部色彩编码同理，
需要整套读手支持，U。

**结论**：不存在可直接换用的「ICC 官方更小的 sRGB 嵌入件」；WP58 针对的是 CMYK output 件。
官方对紧凑 Matrix/TRC 件的目标就是 ~1KB，且明文认可共享 offset、建议 8-bit 配 256 点曲线
——**A（1052B）已在官方推荐形态上，B/C 都不采纳**。

## 7. 2026-11-13 续作：门禁台账收口 + 完整门禁复测（A 档不动）

本轮**只改判据台账与文档**，产物代码与 ICC 曲线字节一个没动（A = 1052B 保持不变）。

- **修了什么**：`verify.ps1` 的 `[1.7/2]` 里，附加四条判据（含 `icc_profile_id_check`）与
  `nvenc_matrix_probe` 在 rc=3 SKIP 时原本只打一行黄字，不写 `GateGaps` ⇒ 既不进末屏
  「本轮判据未覆盖」清单，也不计入结论行「另有 N 处未判」。现与 `$checks` 循环同口径逐条点名。
  `GateGaps` 不参与 exit code（`:1471`），故**阻断行为零变化**。
- **文档订正**：§1 记忆路径、§2 标题、§4:121 的 `referee 待改`；`scripts/precision/README.md:98`
  的「递空表 ⇒ HDR 层恒 SKIP」（`:708`/`:737` 早已接线）；`verify.ps1` 的 hooksPath 历史注释
  （实测 `core.hooksPath=scripts/git-hooks`）。
- **单条完整门禁实测（2026-11-13）**：`verify.ps1 -Configuration Release -WriteGateBaseline`
  ⇒ 构建 **0 警告 0 错误**、`[1.6/2]` ✅、`[1.7/2]` 前 9 条 checks + extra 四条 + NVENC
  **全部判绿**，但第 10 条 `wg_container_roundtrip.py` **判红（rc=1）** ⇒ 整条 `exit 1`；
  `-WriteGateBaseline` 按脚本保护**没有写基线**，复核 `gate-baseline.json` 仍是
  `2625 / 2026-11-05 20:17:13`，未被污染。
- **根因（E1，实证）**：日志里 wg 的 stdout 停在 patch 值表中段、**没有一致性表格和 `[RESULT]`**
  ⇒ 子进程被中途截断，不是判据判红。机制是 `wg_container_roundtrip.py:43` 与
  `audit_roundtrip.py:44` 都在 `artifacts/precision/decode` 里逐文件 `os.remove(raw)`
  （36 + 20 次），叠加样张生成的目录清空，单次工具调用累计删除越过 WorkBuddy
  `[safe-delete]` ≥50 阈值 ⇒ 静默截断。用**同一批样张、同一个 `--samples` 参数单独复跑**
  `wg_container_roundtrip.py` ⇒ `rc=0`，`[RESULT] 不一致容器数 = 0，解码失败/全黑 = 0`，
  30 组跨容器比对全部 ✅（最大差 0..3，容差 1/8）。**判据本身没问题，不是产物回归**。
- **补跑后的完整证据**：`verify.ps1 -Configuration Release -SkipPrecision` ⇒
  8 套件 **2730 断言 / 0 失败**、`VERIFY_LASTEXIT=0`，末屏如实列出 2 处未判；
  这两格用同一批新鲜样张分组补跑：8 条样张判据 rc=0、`audit_roundtrip`（20 文件 0 问题）
  rc=0、`wg_container_roundtrip` rc=0、`tiff_png_referee`（13 条判据 0 不过）rc=0；
  完整门禁那轮里 `gen-third-party-notices --check`、`combo_locale`、
  `icc_selfbuilt_referee`（lcms PASS）、`nvenc_matrix_probe` 均 ✅；
  唯一 rc=3 SKIP 是 `icc_profile_id_check`（本机无带非零 ProfileID 的真实 profile，长期已知）。
- **仍未闭环、如实留账**：① 单条完整 `verify.ps1` 全绿在 WorkBuddy 会话内仍拿不到；
  ② `gate-baseline.json` 保持 2625 未收编——脚本要求「完整且合法的一轮」才允许
  `-WriteGateBaseline`，`-SkipPrecision` 轮不算，本轮完整轮又 `exit 1`。下一轮拿到合法完整绿
  再收编 2730（差 105 条：unit +96 / color +1 / usability +8）。③ 上面 §3 第 5 项与 §4 的
  `[safe-delete]` 雷区继续有效，**不要据此改产品或判据容差**。
- **提交**：本地提交，未 push；不建 tag、不发版。
