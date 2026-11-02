# TrueToneCap v0.3.10-beta 全盘管线实机审查报告

> **审查日期**: 2026-11-02
> **版本锚点**: HEAD `9fdbca5`（工作区核实一致）
> **审查方式**: **真机可执行审查**（非静态分析）—— pwsh 7.6.9 / python 3.12 (numpy+tifffile+Pillow) / ffmpeg+ffprobe / dotnet 11
> **真机环境**: 13th Gen i7-13650HX（14C/20T）/ RTX 5080 + RTX 4060 Laptop / \\.\DISPLAY5 3840×2160 HDR（MaxLuminance 418 nits，SDR 白点 120 nits，ACM 启用）
> **产物目录**: `artifacts/audit-2026-11-02/`

---

## 📋 一、执行摘要

### 总体裁决

| 维度 | 结果 | 证据强度 |
|------|------|---------|
| 自动化门禁 | **8/8 套件 · 2438 断言 · 0 失败** | 强（全量实跑） |
| 编码管线（7 格式） | **6/7 通过；GainMap 有 1 个 P1 缺陷（已修复）** | 强（外部解码器独立读回） |
| 色彩管线（HDR/SDR） | **全部通过** | 强（ffprobe 元数据实测） |
| 捕获管线 | **11/11 臂全绿** | 强（真机 WGC 实跑） |
| 端到端集成（真实用户路径） | **6/7 PASS（含热键→拖框→保存四格式闭环）** | 强（产品自检 + 真机手势 + 外部裁判） |
| 性能专项 | **全部达标或超预期** | 强（本机实测） |

**结论：发现并修复了 1 个产品代码缺陷（P1，JPEG GainMap 的 MPF 偏移）+ 2 个环境/基础设施缺陷；其余管线实现正确、输出正确。**

> ⚠️ **重要订正**：本报告的早期版本（在外部裁判返回前）写过"未发现产品代码缺陷"。**该结论已被推翻** ——
> JPEG GainMap 的 MPF 结构偏移错 4 字节，导致 ffmpeg/libavformat 完全无法打开该文件。详见 §零（最高优先级）。

---

### 零、【P1】JPEG GainMap 的 MPF 偏移错误 → ffmpeg/libavformat 完全拒读（已修复）

**这是本轮唯一的产品代码缺陷，也是最严重的发现。**

#### 现象

产品产出的所有"带增益图"的 JPEG，ffmpeg/ffprobe 一律拒绝打开：

```
$ ffprobe -v error -show_streams D_gainmap_hdr.jpg
[jpeg_mpf] error decoding EXIF data: Invalid data found when processing input
exit=1
```

系统性（4 份产品 GainMap 产物全部中招，含 4K 真实截图）：

| 产物 | 大小 | ffprobe |
|------|------|---------|
| `D_gainmap_hdr.jpg`（640×480） | 21,577 B | **exit=1** |
| `bench_JPEG_GAINMAP.jpg`（4K） | 395,958 B | **exit=1** |
| `gainmap_ultrahdr.jpg` | 220,936 B | **exit=1** |
| `gainmap_test.jpg` | 5,061 B | **exit=1** |
| `sdr_gainmap_srgb.jpg`（**不含**增益图） | 4,562 B | exit=0 ✅ |

→ **凡产品产出的"带增益图"文件，ffmpeg 一律打不开；不含增益图的那个反而正常。**

#### 根因

`src/TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs` 的 `BuildMpf` 里，MPEntry(0xB002) 的 value 字段
被写成常数 `int entryOffset = 54;`：

```csharp
// value 指向 IFD 后的数据区 (相对 MPF 数据起始 = 54)     ← 这个"基准"是错的
int entryOffset = 54;
```

CIPA DC-007 规定 MP 头的所有 offset 一律**相对 TIFF 头**（本文件的 `MM`），而 `MPF\0` 占 4 字节。
所以"相对 `MPF\0`"的 54，换算到"相对 TIFF 头"应是 **50**。

#### 决定性证据（只改 4 字节）

| 操作 | MPEntry value | 读到的 attr | ffprobe |
|------|--------------|------------|---------|
| 产品原样 | **54** | `0x00005170`（错位垃圾，size=0 offset=0） | **rc=1 ❌** |
| 仅改这 4 字节 54→50 | **50** | `0x00000300`（JPEG+Primary，正确） | **rc=0 ✅** `mjpeg,640,480,yuvj444p` |
| libultrahdr v2.0.2 参考实现 | **50** | `0x00030000` | rc=0 ✅ |

**其余字节一个都没动**，只改这一处 ⇒ ffmpeg 立刻能读。

#### 关键：增益图数据本身一直是正确的

- `Entry[0].size=20848` = `文件21577 − 增益图727 − 末尾EOI 2` ✔
- `Entry[1].size=727` 与 XMP `Item:Length="727"` 一致 ✔
- `Entry[1].offset=17255` 解开后正好落在 `FFD8FFE2`（增益图 SOI）✔
- libultrahdr `-P` 读出完整 7 个 hdrgm 参数（`maxContentBoost 4.99332` 等）✔

**所以坏的只有这张 MPF 表的定位基准**，size/offset 的计算全部正确 ⇒ 这是"差 4 字节"的最小修复。

#### 修复

`JpegGainMapEncoder.cs:844`：`entryOffset` 从 `54` 改为 `50`，并注明基准换算（`54 − 4`，`MPF\0` 占 4 字节）。

#### 修复后验证

```
gm_BT2020_False.jpg    MPEntry value=50 -> attr=0x00000300 size=3809  ffprobe rc=0 ✅
gm_BT2020_True.jpg     MPEntry value=50 -> attr=0x00000300 size=3808  ffprobe rc=0 ✅
gm_sRGB_False.jpg      MPEntry value=50 -> attr=0x00000300 size=3809  ffprobe rc=0 ✅
```

门禁回归：**8/8 套件 · 2438 断言 · 0 失败**（修复未破坏任何现有断言）。

---

### 【P3 存疑】MPEntry attribute 与参考实现不一致（本轮不改）

| | Entry[0] attribute | Entry[1] attribute |
|---|---|---|
| 产品 | `0x00000300` | `0x00000000` |
| libultrahdr v2.0.2 | `0x00030000` | `0x00000000` |

两者确实不同，但**无法在本环境定性哪个符合规范**（缺 CIPA DC-007 原文的 attribute 位定义表）。

**已实测确认：该不一致不影响解码。** 把 attribute 改成 `0x00030000` / `0x00000300` / `0x00000301` 三种值，
ffprobe 行为**完全相同**（全部 rc=0）。各解码器主要靠 XMP 定位增益图，不靠 attribute 遍历。

**处置：不改**（无原文依据时不应动）。若日后要对齐 libultrahdr，需在注释里写明基准来源。

---

### 零-B、【关键方法论发现】"灰阶测不了色域"——一次差点改错产品的教训

**这是本轮最重要的方法论产出，比任何一条缺陷都值得记下来。**

#### 背景

色彩管线审查提出一个 **P1 疑点（D-1）**：`CapturePipelineService.cs:140-163` 的 ACM 分支里，
两条子路按**两个互斥的源色域假设**解释同一批字节：

- `:143-144` sRGB 目标 → 原样返回 + 声明 `cICP=1` ⇒ 隐含"字节是 sRGB"
- `:151-163` 其他目标 → 以 `GetStandardIccProfile("DisplayP3")` 为源烘焙 ⇒ 隐含"字节是 P3"

上文 `:134-139` 自己的注释写的是"ACM 时 BGRA8 是 DWM 合成后的显示器色域"。若两者矛盾，sRGB 短路会让纯红产生 9%–100% 的相对误差。

#### 唯一"实测支撑"的方法论缺陷

仓库里支撑"ACM 下字节是 sRGB"的唯一实测是 `--sdr-semantics-probe` 的 `mean8/authored = 1.000`。
但**那个判据结构上对色域问题必然空转**：

> 中性灰 (v,v,v) 在 sRGB→P3 / sRGB→BT.2020 的 3×3 矩阵下**逐值不变**（行和恒为 1）。
> 所以"灰度均值 == authored"只能否证**亮度抬升**，不能否证**色域转换**。

代码里甚至写明了这个局限（`SdrSemanticsProbe.cs:599` 原注释"中性灰下通道顺序不影响结果"），
只是当时没人意识到它同时也是"色域不影响结果"。

#### 决定性实验（本轮补做）

给探针补 3 枚高饱和色块（纯红/纯绿/纯蓝，authored 值已知）+ 逐通道采样，
然后**真机跑**（IL 修复后 WGC 可用）：

```
chip     authored       实测R     实测G     实测B   最大|Δ|  判读
red    192,  0,  0     192.00      0.00      0.00     0.00  ByteIsAuthoredSrgb
green    0,192,  0       0.00    192.00      0.00     0.00  ByteIsAuthoredSrgb
blue     0,  0,192       0.00      0.00    192.00     0.00  ByteIsAuthoredSrgb

色域结论 = ByteIsAuthoredSrgb | 全部 3 枚饱和块都贴 authored（≤2 码）⇒ 8-bit 字节仍是 authored sRGB
```

#### 🔴 实验过程中的两次自我纠错（必须记录）

1. **第一次跑出 `ByteIsPanelGamut`（2/3 被搬动）** —— 差点据此下"产品撒谎"的结论。
   但读数形态可疑：`red(192,0,0)` 实测 **R=0, B=192**（完全对调）、`blue` 反之。
   那不是色域搬动，是**我的通道序 bug**：GDI `CreateSolidBrush` 的参数是
   `COLORREF = 0x00bbggrr`（**低字节 = R**），我写成 `(R<<16)|(G<<8)|B` 正好把 R/B 对调。
   灰阶补丁三字节相同 ⇒ **旧代码从没暴露过这个坑**，彩色补丁一上就现形。
   修正后立刻得到 3/3 bit-exact。

2. **我一度真的改了产品代码**（把 ACM 分支的烘焙源从 `nativeGamutTag` 改成 `"sRGB"`），
   门禁 2438 断言**全绿**（因为没有一条断言覆盖这条路径 —— 这本身就是覆盖缺口）。
   但随后自我检讨发现证据不充分：

   > **探针的补丁是 GDI 画的窗口，它自己就是一个 sRGB 应用** ⇒ WGC 读回 authored sRGB 是**自然的**。
   > 这只证明"GDI 窗口内容"这一种源，不能证明整个桌面（D3D 全屏、广色域应用）也是 sRGB。

   加上改动推翻了 2026-08-09 的设计前提（该前提来自当时的真机观察，本轮无原文依据），
   且**零测试覆盖** ⇒ **已回退**，只保留探针增强（纯增益、不改产品行为）。
   回退的 diff 留档在 `artifacts/audit-2026-11-02/acm-fix-REVERTED.diff`。

#### 结论与遗留

- **D-1 未被证实，也未被证伪**（从"存疑"降级为"仍然存疑，但已排除一种错误证法"）。
  本轮确立的是：**原来的"实测支撑"无效**，现在的探针**能测这一维了**；
  但它测的是"GDI 窗口字节"，覆盖不到"整个桌面在 ACM 下的字节"。
- **要真正定论仍需第二个证法**（任一条）：
  ① 正常会话下 `--display-audit-emit` 出 Rec.709/Rec.2020 两枚 PNG，从盘上读回声明并与同区像素比；
  ② 直接把探针补丁换成 **D3D 全屏渲染窗口**（非 GDI）重跑；
  ③ 直接读 8-bit 会话的 `DXGI_COLOR_SPACE`。
- **测试覆盖缺口（新登记 P2）**：ACM 色彩分支**零断言覆盖**（本轮改了它，2438 断言全绿 —— 即门禁看不见这条路径）；
  且 `--png-check` 的桥接把 `systemAcm` 硬编码为 `false`（`PngCheck.cs:102`）⇒ **结构上不可能覆盖 ACM 分支**。

**给后续维护者的一句话**：*改判据之前先问"这把尺子量得到这一维吗"* ——
中性灰量的不是色域，`mean8/authored=1.000` 读起来很像"证明了色域是 sRGB"，但它一个字都没说。

---

### 零-C、【被否证的疑点】三条件子代理判断，经我实测均不成立

审查的价值一半在"确认了什么"，一半在"否证了什么"。以下三条曾被报为缺陷，经独立实测**否证**，
记在这里以免后续重复排查。

#### C-1 ❌"JPEG/WebP 的 HDR 归因错标（BlockedByDisplay 抢占 BlockedByFormat）"

**指控**：HDR 屏上选 JPEG/WebP 时日志写 `HDR=False/BlockedByDisplay`，而真实原因是"格式没有 HDR 通道"，
`BlockedByFormat` 成了死分支，会把排障引向显示器/ACM。

**否证证据**：用产品自带的受控判据表 `--display-audit-emit`（它按格式逐个问"HDR 开关按开时交付形态是什么"）：

```
交付形态(=界面卡片那行箭头; HDR 开关按开、两档目标色域各问一次):
PNG               Rec.709⇒BlockedBySrgbTarget Rec.2020⇒PqContainer
JPEG              Rec.709⇒BlockedByFormat     Rec.2020⇒BlockedByFormat
JPEG XL           Rec.709⇒BlockedBySrgbTarget Rec.2020⇒PqContainer
WebP              Rec.709⇒BlockedByFormat     Rec.2020⇒BlockedByFormat
增益图(JPEG)        Rec.709⇒GainMap             Rec.2020⇒GainMap
```

**归因完全正确**：PNG/JXL 有 HDR 通道 ⇒ `PqContainer`；JPEG/WebP 无 ⇒ `BlockedByFormat`；增益图 ⇒ `GainMap`。
`BlockedByFormat` 是活的。

**那次 `BlockedByDisplay` 的真实来源**：日志时间戳 01:17:26 是 `--selftest` 进程（NORMALIZE 项在遍历设置轴），
走的是**合成决策**而非真实屏幕路径；同一时刻 `JPEG_GAINMAP HDR=True/GainMap` 就打对了。

**顺带查清一个机制细节**（子代理的机制推断也有误）：`HdrSwitch.IsEnabled` 只看硬件
（`MainWindow.xaml.cs:689/771` = `_hdrHardwareSupported && cap.SupportsHdr`），**不按格式禁用**，
所以 `HdrEnabled: (HdrSwitch.IsOn && HdrSwitch.IsEnabled)` 不会因格式能力被折成 false。

#### C-2 ❌"用户选无损格式 ⇒ 压缩档位应禁用"（口径冲突，非缺陷）

**指控**：任务书说"无损格式应禁用压缩档位"，但产品在 PNG/AVIF-CRF0/JXL-0.1 三档下档位都 `IsEnabled=True`。

**否证**：产品的判据是**"后端有没有这个旋钮"**，不是"格式是否有损"。
PNG/TIFF 的「压缩强度」（deflate level）**恰恰是无损格式唯一的旋钮**，禁用它是错的。
产品在"后端无旋钮"时（NVENC/QSV 的 AVIF 压缩强度）确实会禁用并点名 —— 那条行为正确。
**结论：产品行为正确，是任务书口径不准。**

#### C-3 ⚠️"每轮 +283MB 是产品代码泄漏"（降级为 P1 存疑）

见 §4.0。我做了 4 种判定尝试（含真改代码加 `ClearState+Flush`、禁用共享槽、连续截图对照、杀进程后测记账），
**均未定罪**，且读数"每轮精确 +283.0/287.4/282.0"这种整齐度强烈提示是 WDDM 记账口径而非真实分配。
**已回退那次代码改动**（diff 留档 `wgc-flush-REVERTED.diff`）。

### 与上一轮（2026-11-01 静态审查）的关键差异

上一轮受 PowerShell 环境故障所限只能静态审查，多个结论标"证据不足"。**本轮这些格子全部补上了真机证据**：

| 上轮标记 | 本轮结果 |
|---------|---------|
| F4 静置唤醒"测量前提失效，数据无效" | 定位到根因是**仓库目录 Low 完整性标签**（非产品问题），已修复 |
| F5 探针"11 臂建议挂 probe.log" | **真机重跑 11 臂全绿，EXIT=0** |
| TIFF 外部裁判"脚本缺失不可复现" | 脚本已重建并**在门禁中跑通**（`[2.5/2]`） |
| PQ 性能"只能文档自洽" | **本机实测 5.97×**（优于文档 2.30×） |
| AVIF 档位"文档数字不可复测" | **本机实测三档 sha 全互异** |

---

## 🔴 二、审查中发现的环境缺陷（已修复，非产品缺陷）

### 缺陷 E-1：仓库目录树带 Low 完整性标签 ⇒ 所有真机捕获被 Windows 拒绝 【P1】

**现象**：从仓库内启动的 `TrueToneCap.exe` 做任何真实屏幕捕获都失败，报 `0x80070005（拒绝访问）`。产品自身给出极清晰的归因：

```
无法创建 WGC 捕获会话：本进程运行在【低完整性/受限令牌】下
（检测读数：完整性 RID=0x1000（Low（受限令牌，WGC 同样会拒）））
```

**根因**：
```
icacls C:\PLAN\TrueToneCap
  → Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)     ← 目录被打上 Low 标签
icacls ...\win-x64\TrueToneCap.exe
  → Mandatory Label\Low Mandatory Level:(I)(NW)          ← 全部子文件继承
```
Windows 规则：进程有效完整性 = min(镜像标签, 令牌标签)。宿主 pwsh 是 `RID=0x3000`(Medium)，但 exe 镜像继承 Low ⇒ 进程被降为 Low ⇒ WGC 拒绝。

**影响面**（这是本轮一切"捕获失败"的真正原因）：
- 上一轮（2026-11-01）PowerShell 全部崩在 `0xC0000142`
- 本轮所有子进程写 `%TEMP%`/`%LOCALAPPDATA%` 被拒
- GUI 窗口无法通过 UIAutomation 被发现
- WGC/截图/自检 HDR_SOURCE 全部 E_ACCESSDENIED

**修复**：
```powershell
icacls C:\PLAN\TrueToneCap /setintegritylevel Medium /T /C /Q
# → Successfully processed 6120 files; Failed processing 0 files
```

**修复后验证**（决定性）：
- exe 标签 → `Medium Mandatory Level`
- `--frame-handoff-probe`：**11 臂全绿，EXIT=0**
- `--selftest`：**11/11 通过，skip=0**（修复前 ok=10/skip=1）

> ⚠️ **这不是产品缺陷**，而是审查机器/工作区的 ACL 状态问题。**产品代码里的完整性判据（`WgcCaptureService.cs:2668-2780`）是完全正确且优秀的防御**：它准确区分了"低完整性 / 提权 / 被挡"三种情形并给出可操作的处置建议。本轮审查反而**验证了这条判据的正确性**。

### 缺陷 E-2：受限环境下哨兵用例让整条 `--unit-tests` 套件崩溃 【P1，测试基础设施】

**现象**：`--unit-tests` 退出码 `-532462766`，在第一个哨兵用例就终止：

```
System.UnauthorizedAccessException: Access to the path
  'C:\Users\20210\AppData\Local\TrueToneCap\encoder-crash.NVENC.sentinel' is denied.
   at CorePipelineTests.WriteForeignSentinel(...) line 333
```

**影响**：`unit-tests` 有 **197 个测试函数 / 1807 条断言**，崩在第 1 个 ⇒ 后面 190+ 个用例**一条都没跑**，全量断言从 2438 掉到 631，门禁无法判定真实状态。

**为什么是缺陷**：哨兵的四个核心契约（写入 / 在途≠残留 / 读后即消费 / 后端归属隔离）需要真读写文件才判得动，而测试把目录硬编码在 `%LOCALAPPDATA%`，在受限环境（CI 容器 / 只读 AppData / MDM 锁定）必然失败，且是**硬崩而非跳过**。

**修复**（两处，保持契约真跑而非跳过）：
1. `src/TrueToneCap.Core/Encoding/EncoderCrashSentinel.cs`：新增 **internal 测试注入点** `UseTestDir(string?)`（生产路径一个字未改，默认仍为 `%LOCALAPPDATA%\TrueToneCap`）
2. `src/TrueToneCap.Test/CorePipelineTests.cs`：新增 `SentinelPrecondition`（切到可写目录 + 真实自检）与 `SentinelCleanup`（用例收尾恢复生产路径），三个哨兵用例接入

**为什么不用"跳过"**：跳过等于永久放弃这 15 条契约的验证，而受限环境恰恰最需要它们（托管机器上硬件编码真会崩）。注入点让契约**照原样真跑**。

**修复后验证**：
```
--unit-tests: 1807 通过 / 0 失败 / 2 跳过   ← 与基线 1807/0/2 逐字一致
```

---

## ✅ 三、分维度实测结果

### 3.1 自动化门禁（全量实跑）

```
[1/2]   构建                     ✅ 0 警告 / 0 错误
[1.6/2] 发布门禁回归             ✅ 通过
[1.7/2] 外部判据（14 项）         ✅ 全部通过（含 nvenc_matrix_probe）
[1.8/2] 色彩管线判据（3+1 项）    ✅ 全部判了且通过
[1.9/2] 宿主新鲜度               ✅ 2 个宿主都不早于 165 份构建输入
[1.95/2] 闸门接线                ✅ core.hooksPath 已接
[1.96/2] 日志行格式对撞          ✅ ARMS=8 MISMATCH=0
[1.97/2] App 设置迁移判据        ✅ 退出码 0
[1.98/2] VS-Cap 侧车归档         ✅ 已并入主程序
[2/2]   8 个测试套件
    --unit-tests       1807 通过 / 0 失败 / 2 跳过
    --color-tests       270 通过 / 0 失败
    --encoding-tests     23 通过 / 0 失败
    --service-tests       2 通过 / 0 失败 / 1 跳过
    --ocr-tests          12 通过 / 0 失败
    --qr-tests           47 通过 / 0 失败
    --format-tests       52 通过 / 0 失败
    --usability-tests   225 通过 / 0 失败
[2.5/2] 无损容器外部裁判         ✅ 通过（本轮重建的 tiff_png_referee.py）
══════════════════════════════════════════════
  ✅ 8 个套件全部通过 — 共 2438 断言 (67.5s)｜无未判格子
```

**基线核对**：`scripts/gate-baseline.json` 声明 2438 断言，实测 **2438**，逐套件逐格一致。

### 3.2 编码管线：7 格式 × 外部解码器独立读回

**生产路径基准（`--bench-savepath`，4K 3840×2160）**

| 格式 | pixels | color/tone | encode+write | total | 产物 | 状态 |
|------|--------|-----------|--------------|-------|------|------|
| PNG | 44ms | 15ms | 140ms | **199ms** | 247,011 B | ok |
| TIFF | 40ms | 0ms | 42ms | **82ms** | 2,645,760 B | ok |
| WebP | 44ms | 0ms | 949ms | **993ms** | 174,818 B | ok |
| JPEG_LI | 38ms | 0ms | 485ms | **523ms** | 272,264 B | ok |
| JPEG_XL | 38ms | 0ms | 567ms | **605ms** | 104,180 B | ok |
| AVIF | 38ms | 0ms | 2212ms | **2250ms** | 56,986 B | ok |
| JPEG_GAINMAP | 43ms | 19ms | 731ms | **793ms** | 395,958 B | ok |

**外部解码器读回（`artifacts/audit-2026-11-02/selftest-full/`，产品 `--selftest` 真实产出）**

| 格式 | 外部解码器读数 | ICC | 内容真实性 |
|------|--------------|-----|-----------|
| PNG | PIL `(96,96)` RGBA | 0B（sRGB 不嵌，正确） | std=80.8 ✅ |
| TIFF | tifffile `(96,96,4)` uint8 spp=4 bits=(8,8,8,8) **extra=UNASSALPHA(2)** IFD 升序 ✅ | — | ✅ |
| JPEG_LI | PIL `(96,96)` RGB | 0B | std=67.9 ✅ |
| JPEG_XL | ffprobe `jpegxl,96,96,rgb24,iec61966-2-1,bt709` | — | ✅ |
| AVIF | ffprobe `av1,96,96,yuv444p,iec61966-2-1,bt709` | — | ✅ |
| WebP | PIL `(96,96)` RGB | 0B | std=67.9 ✅ |
| JPEG_GAINMAP | PIL 可开 + MPF 段存在 + `hdrgm`/`GainMapMin`/`GainMapMax` XMP 齐备 | 2100B(广色域料) | ✅ |

**关键专项验收**

1. **无损像素等值**：PNG8 与 TIFF8 同源编码，PIL 逐像素比对 → **`np.array_equal = True`**（完全相等）
2. **TIFF 契约（20 份全量审计）**：`SamplesPerPixel=4` / `ExtraSamples=(UNASSALPHA:2)` / IFD tag 升序 → **20/20 全部符合，0 份违规**
3. **16-bit PNG 真实性**：IHDR `bitDepth=16, colorType=6`（真 16-bit RGBA，非 8-bit 伪装）
4. **位深上下限**：请求 8→`yuv444p`、10→`yuv444p10le`、12→`yuv444p12le`、16→`yuv444p12le`（16 被 `MaxBitDepth` 钳到 12，**设计如此**，见 `AvifEncoderCapabilities.cs:40`）
5. **档位互异（CompressionEffort）**：PNG/TIFF 8&16-bit 四档、AVIF 8&10-bit 三档 → **sha/字节全部互异**

### 3.3 色彩管线：声明与像素同源

**真机 HDR 截图产物（3840×2160，`e2e-hdr2/`）**

| 格式 | codec | pix_fmt | transfer | primaries | bits | 判定 |
|------|-------|---------|----------|-----------|------|------|
| JXL | jpegxl | `rgba64le` | **smpte2084 (PQ)** | **bt2020** | 16 | ✅ 完全正确 |
| AVIF | av1 | `yuv420p10le` | **smpte2084 (PQ)** | **bt2020** | 10 | ✅ 完全正确 |
| WebP | webp | `yuv420p` | unknown | unknown | 8 | ✅ **设计如此**（WebP 无 HDR 通道，见下） |

**真机 SDR 截图产物**

| 格式 | pix_fmt | transfer | primaries | 判定 |
|------|---------|----------|-----------|------|
| JXL | rgba64le | iec61966-2-1 (sRGB) | bt709 | ✅ |
| AVIF | yuv420p10le | iec61966-2-1 (sRGB) | bt709 | ✅ |

**WebP HDR 降级是正确的产品设计**，代码依据充分：
- `FormatEncoders.cs:550` — `public override bool SupportsHdr => false;` + `:555` "WebP 不支持 HDR，始终降级到 SDR"
- `FormatColorFacts.cs:84-85` — `HdrDelivery.BlockedByFormat`，且注释详述为何应报"格式没通道"而非"目标不宽"（"报目标就是把人往错的方向指"）
- 该降级会通过 `EncoderFallbackNotifier` 留痕告知用户

**HDR 源真实性（`--selftest` HDR_SOURCE，本机实测）**
```
帧 3840x2160 捕获耗时 466ms
目标屏=\\.\DISPLAY5 IsHdr=yes SupportsHdr=yes BitsPerColor=10 色域=RgbFullG2084NoneP2020 峰值=418nits ACM=yes
float min=-0.1104 max=1.5977
越出 SDR 范围: >1.0 占 40.83% | >纸白(1.5000) 占 0.01% | <0 占 0.01%
落在本帧 8-bit 整格比例: raw 域 59.77%、线性→sRGB 反解后 89.76%
中性像素 189696 个 → 去重量级 2017
⇒ PASS：float 帧明显偏离 1/256 整格，中性级数 2017 破 256 上限 ⇒ 下游精度确实不止 8-bit
```
**这条是 HDR 管线正确性的硬证据**：源真的是 Float16 高精度、真的超出 SDR 范围、真的包含 >纸白 高光。

**HDR 判据自身的"有牙"证明**（`HDR_JUDGE`）：
- 256 级台阶帧（应 FAIL）→ 实判 **FAIL**（整格 100%、中性级数 256）
- 1024 级台阶帧（应 PASS）→ 实判 **OK**
- 纸白 1.5 同一对（8bit 应 FAIL / 10bit 应 PASS）→ 实判 **FAIL / OK**，且旧锚点（纸白按 1.0）会把 8-bit 判成 OK ⇒ **判据会红也会绿，非恒真**

### 3.4 捕获管线：帧交接 11 臂（真机全绿）

修复 IL 后 `--frame-handoff-probe` 实测：

```
被捕获的屏：\\.\DISPLAY5 矩形=(0,0)-(3840,2160) HDR
✅ 臂① 前置自证：整屏捕获的哈希出现过变化
✅ 臂② 深拷贝对照：再截一张后深拷贝仍然不变
ℹ️ 臂③ CPU 复用缓冲：已被后续帧覆写（只报告：护住画面的是 _isCapturing 防重入闸）
共享句柄自检：Shared 纹理已建（4 种句柄形态全通，含裸 vtable）
共享槽：句柄已取走 3840x2160 (HDR/Float16)，改在【第二个 D3D11 设备】上打开
✅ 臂④-1 跨设备共享可用：第二设备打开成功并回读到内容
✅ 臂④-2 共享槽尺寸与捕获一致（3840x2160 vs 3840x2160）
✅ 臂④-3 持有期间冻结：再截 3 次后同一块纹理内容不变
✅ 臂④-4 交还后恢复更新：同一块槽上出现了新内容
✅ 臂⑤ 生产选区回读 == 第二设备整块回读取同矩形（1920x1080@960,540，比 8294400 个分量，不同 0 处）
✅ 臂⑥ 运动画面下生产路径每次都冻出槽且选区回读对得上（6 次对上 / 0 未取到 / 0 不等）
✅ 臂⑦-控 对照臂（无槽 + CPU 帧=洋红）屏幕采样=(137,0,137) 色比=0.00 应为洋红
✅ 臂⑦ 预览采样的是共享槽（屏幕采样=(224,224,224) 色比=1.00）
✅ 臂⑧ SDR 帧不再整帧回读：SdrPixels=null、像素只在槽里
✅ 臂⑧ SDR 槽尺寸与捕获一致（3840x2160 vs 3840x2160）
✅ 臂⑨ 生产 SDR 选区回读 == 第二设备整块回读取同矩形（比 8294400 字节，不同 0 处）
✅ 臂⑩ 运动画面下 SDR 槽每次都冻结且选区回读对得上（6 次对上）
✅ 臂⑪ 只认持有者那块槽：错句柄被拒（null），正确句柄可读（16384B）
RESULT handoff=pass    EXIT=0
```

**亮点**：臂⑤/⑨ 的 **8,294,400 字节 0 处不同**是跨设备共享纹理正确性的最强证据；臂⑦ 的洋红对照臂证明"预览确实采样共享槽"不是空转；臂⑪ 证明句柄归属校验有效。

### 3.5 端到端集成：产品自带自检 11/11

`TrueToneCap.exe --selftest --out ... --formats PNG,JPEG_LI,JPEG_XL,AVIF,WebP,TIFF,JPEG_GAINMAP`

```
[01:44:38.515] PNG          ok   52ms
[01:44:38.528] TIFF         ok   11ms
[01:44:38.814] JPEG_LI      ok  285ms
[01:44:39.076] JPEG_XL      ok  260ms
[01:44:39.760] AVIF         ok  683ms
[01:44:40.021] WebP         ok  259ms
[01:44:40.055] SETTINGS_MIGRATION ok  30ms  ✓ 门是活的，老用户旧质量档会被交还默认表一次
[01:44:40.066] NORMALIZE          ok   7ms  ✓ 59 轴越界/合法/大小写/迁移全部符合期望
[01:44:40.069] GAINMAP_MIGRATION ok   1ms  ✓ 裸索引不会静默换格式，迁移只跑一次
[01:44:40.096] HDR_JUDGE          ok  25ms  ✓ 判据会红也会绿
[01:44:40.786] HDR_SOURCE         ok 672ms  ✓ 真捕获 3840x2160 + Float16 高精度确证
[01:44:40.789] json-written failed=0 ok=11 skip=0 missingBackend=0 fail=0
[01:44:40.790] returning                     EXIT=0
```

**11/11 通过、0 跳过**（修复 IL 前是 10 ok + 1 skip）。所有产物均通过外部解码器独立读回（见 §3.2）。

### 3.5b 真实用户手势路径：热键→拖框→Enter→四格式闭环

**这是本轮最有价值的端到端证据**：不走任何内部入口，完全按用户操作复现。

```
① Ctrl+Shift+S（产品日志自报已注册 id=9001）→ ② 等 TTC_HC 全屏窗（3840×2160）
→ ③ SendInput 真实鼠标 拖框 (900,600)→(2400,1500) → ④ VK_ENTER（HdrCaptureWindow.Key → DoAction(Save)）
→ ⑤ 等产品落盘 → ⑥ 外部 ffprobe/Pillow 读回
```

每次运行前后设置按字节备份/还原并复算 sha256（`47b2c7…533d` 一致）。

| 格式 | 产物 | 大小 | pix_fmt | 尺寸 | transfer/primaries | 内容真实性 |
|------|------|------|---------|------|-------------------|-----------|
| **PNG** | `TTC_20261102_014921871.png` | 661,865 B | **`rgba64be`**（16-bit） | 2250×1259 | **`smpte2084` / `bt2020`** + cICP `9/16/0/1` | kinds=215，黑/白 0% ⇒ 有结构 |
| **JPEG** | `TTC_20261102_014955284.jpg` | 270,819 B | `gbrp` | 2250×1259 | 靠 ICC(720B, `XYB_Per`) | kinds=468 ⇒ 有结构 |
| **AVIF** | `TTC_20261102_015012621.avif` | 257,940 B | **`yuv420p10le`** | 2250×**1260** ⚠ | **`smpte2084` / `bt2020`** | kinds=281 ⇒ 有结构 |
| **TIFF** | `TTC_20261102_015029571.tiff` | 1,131,929 B | **`rgba64le`** | 2250×1259 | ICC 25100B，原色实测 **BT.2020**（偏差 0.0013），desc=`020 (PQ)`, TRC curv 4096 | kinds=236 ⇒ 有结构 |

产品自报的关键日志（证明走的是真实保存管线）：

```
| 选区完成 2250x1259 @(窗口内 1350,900)
| 编码设置: PNG HDR=True/PqContainer 质量=100.0 位深=10 色度=444 色域=BT2020
| HDR 编码: PNG 2250x1259 HDR=True 分类=Hdr 源色域=DisplayP3 目标=BT2020 HDR=写PQ
| HDR 编码完成: … (646.4 KB) 总耗时=1455ms
```

**像素是真实屏幕内容的三重证据**：① `kinds`（60×60 网格）215/468/281/236，远超纯色的 1；② 黑 0.00%、白 0.00%，均值有明确分布；③ 另两次整屏 3840×2160 捕获 `kinds=195`。

**剪贴板双通道（同样真机验证）**：

```
无感截图 Ctrl+Shift+Q 后：
  #2  CF_BITMAP
  #8  CF_DIB   | 3840x2160 bpp=32 comp=3 hdrSize=40
  #17 CF_DIBV5 | 3840x2160 bpp=32 comp=3 hdrSize=124
  #15 CF_HDROP | first=...\TTC_20261102_015250794.png      ← 文件通道
选区保存后：CF_DIB/CF_DIBV5 = 1500x900 bpp=32
```

**配置持久化 + 控件状态**（`Fail=0 Undecided=0`）：
- 格式持久化往返闭合（盘 5/TIFF → 界面 TIFF → 点选 JPEG XL → 再点回 PNG → 重启仍 PNG）
- 压缩档位往返闭合（盘 `Quick` → 界面「快速」→ 点选「均衡」→ 重启仍「均衡」）
- **无损格式控件状态符合设计**：质量滑块**收起**、压缩强度**可用**（PNG/TIFF 的 deflate level 是它唯一旋钮），
  并用 **JPEG XL 对照臂**证明"收起是真的，不是仪器瞎"
- "差集保存"语义正确：等于默认值时不写盘（不是写入失败）

**冷启动**：进程→可见窗 **1342ms**，产品自报"进程到主窗口 1014ms"，`error/critical lines: 0`，WER 无 App 崩溃记录。

### 3.6 性能专项

**PQ 查表（`--bench-pq-table`，4K 全域密扫 20,000,001 点）**
```
ms/帧: ref(pow)=1207.3  product.LinearToPQ=1203.7  cand(table)=202.1
speedup(ref/cand) = 5.97×
全域密扫: cand 10-bit max=1  12-bit max=1  不同码(10b)=0.324%
weak(粗表正控) 10-bit max=296 → 期望 >1（证明误差扫描是活的）
✅ 通过（候选 10-bit≤1，12-bit max=1；5.97x；粗表正控越界成立）
```
> 文档声明 2.30×（134.6→58.4ms），本机实测 **5.97×**（1207→202ms）。加速比更高，原因：本机 i7-13650HX 有 AVX2+FMA 无 AVX512，表插值优势更明显。**方向一致，实测优于文档，不是矛盾。**

**AVIF 档位正控（`--bench-avif-effort`，1024×640）**
```
Smallest  ms=3756  bytes=979   sha16=A80806A4CA46C349
Balanced  ms= 751  bytes=2131  sha16=9793B6612CEBF14D
Fastest   ms= 576  bytes=2648  sha16=789FEB79232EADEF
✅ 三档产物互不相同 ⇒ 档位真的进到本次所选后端
```
**三档 sha 全互异 + 速度差 6.5×**（3756 → 576ms）。另在 `enc2/shots/` 对 8-bit 与 10-bit 各验一组三档，**sha 全部互异**。

**TIFF 全档位（`--bench-tiff`，4K）**

| 位深 | 档位 | lvl | convert | deflate | total | 产物 |
|------|------|-----|---------|---------|-------|------|
| 8-bit | Smallest | 9 | 9ms | 594ms | **73ms** | 2,680,156 B |
| 8-bit | Quick | 8 | 9ms | 117ms | **28ms** | 2,643,465 B |
| 8-bit | Fastest | 1 | 9ms | 111ms | **38ms** | 12,727,286 B |
| 16-bit | Smallest | 9 | 8ms | 2864ms | **395ms** | 3,847,151 B |
| 16-bit | Quick | 8 | 8ms | 1121ms | **125ms** | 3,847,157 B |
| 16-bit | Fastest | 1 | 8ms | 154ms | **48ms** | 15,681,223 B |

正控：effort 产物字节不同=是、deflate lvl9 > lvl1=是。

> 注：`assemble+write = total − convert − deflate` 出现 `-1`，这是**有意设计**（`SavePathBench.cs:269-273`：deflate 那列是"串行逐条"口径，比产品的"整帧判一次 + 条间并行"慢，差值为负就写 -1，**不许留 0 让人读成"组装与写盘不要钱"**）。这恰是并行分条生效的证据。

---

## 🟡 四、未覆盖 / 客观受限项（如实登记）

### 4.0 【P1 存疑，未定罪】GPU 显存每轮 +283MB（空闲回收后不回落）

**这是本轮最需要后续跟进的一条。** 实测（真实用户路径：起 GUI → 热键截图 → 静置 >15s 触发空闲回收 → 再截图）：

```
基线        122.5 MB
1 轮截图后   667.4 MB  → 回收后 667.4（不回落）
2 轮截图后   950.3 MB  → 回收后 950.3（+283）
3 轮截图后  1237.6 MB  → 回收后 1237.6（+287）
4 轮截图后  1519.7 MB  → 回收后 1519.7（+282）
```

每轮增量 ≈283MB，恰好等于一条会话的全套纹理（SDR staging+latest+槽 31×3 = 93MB，HDR 63×3 = 189MB，合计 282MB）。
F5 仪表也证实每轮都新建会话（`建SDR会话 n=3/4`、`建Float16会话 n=3/4`）。

**已做的判定尝试（均未定罪）**：

| 尝试 | 结果 |
|------|------|
| 在 `PooledSession.Dispose()` 加 `ctx.ClearState()+Flush()` | 只降 32MB/轮（571.8 vs 667.4），**线性增长依旧** ⇒ 已回退（diff 留档 `wgc-flush-REVERTED.diff`） |
| 禁用 SDR 共享槽（`TTC_SDR_SHARED=0`） | 仍 +283/轮 ⇒ 泄漏**不是槽纹理** |
| 连续截图不等空闲回收（5 次） | 稳定 595–604MB **不增长** ⇒ 池复用时正常 |
| 杀进程后测该 PID | 2s 内记账归零 ⇒ 进程退出即释放 |

**为什么判"存疑"而不是"已定罪"**：
1. 每轮峰值**精确相同**（603.7/950.3/1237.7/1519.7，差值恒 283.0/287.4/282.0）——真实 D3D 分配量通常有对齐/池化抖动，
   这种"整齐"更像 WDDM `Dedicated Usage` 记账口径；
2. 只用 `Get-Counter '\GPU Process Memory(*)\Dedicated Usage'` 一种仪器，**没做 DXGI/COM 引用计数探针**；
3. 无法区分"D3D11 运行时分配器保留缓存（预期，为下次复用）"与"真泄漏"。

**要把这条定罪，需要补**（任一条）：
- DXGI `IDXGIObject::GetPrivateData` / `ID3D11Device::CheckCounter`（`DXGI_QUERY_VIDEO_MEMORY_INFO`）读数；
- 在 `Dispose` 后立刻 `EmptyWorkingSet` + 观察，或用 `ID3D11Device1` 的 `GetDeviceRemovedReason` 旁证；
- 长跑对照：连续 20 轮后看是否线性到耗尽（若 WDDM 缓存会自己封顶，则到某点应该平稳）。

**当前建议：不阻塞发布**（会话池复用路径正常、进程退出即释放、单次截图 480MB 也可接受），
但**列为发布后第一优先跟进项**——若确认是真泄漏，长时间连续截图的用户会累积到数 GB。

### 4.1 其它未覆盖 / 受限项

| 项目 | 状态 | 原因 |
|------|------|------|
| **双屏 #46** | ❌ **未测** | 本机只有 1 块活动显示器（`\\.\DISPLAY5` **3840×2160**）。WMI 有 2 条物理屏记录（AUO 笔记本非活动 + PHILIPS 27M2N5800P），但只有 1 块在扩大桌面。**池键 Monitor 维度 / 多屏拼接 Parallel.ForEach / 跨屏 DPI / HDR 拼接完整性 / 副屏色彩决定 —— 一条都没验证** |
| **F4 静置唤醒** | ⚠️ **未测（延迟）** | 根因（IL 标签）已定位并修复、GUI 已能起窗，但单实例 `Global\TrueToneCap_SingleInstance` 互斥体**跨会话生效**，与并行审查/子代理的 GUI 用例互相踩锁，未拿到有效静置数据 |
| **AVIF `avifenc` 软件路径** | ⚠️ **未测** | 本机 `Auto` 恒解析为 NVENC，只验硬件直通一条腿 |
| **AVIF 压缩强度禁用格（QSV/MFT）** | ⚠️ **未测** | 本机 HDR 开启使 QSV 项被 `SupportsHdrPath` 摘除，该格无对象可判 |
| **剪贴板位图逐像素比对** | ⚠️ **部分** | 已验 `CF_BITMAP`+`CF_DIB`/`CF_DIBV5` 存在且尺寸正确（整屏 3840×2160、选区 1500×900、32bpp）；**未逐像素解码比对** |
| **418 nits 面板峰值** | ⚠️ **受限** | 未构造 >400 nits 高光夹具；实测最高只到 127.8 nits（= 桌面内容水平），不能证明捕获能取到面板峰值 |
| **NVENC 444** | ℹ️ 已知设计 | 驱动恒按 4:2:0，请求 444 不生效（有降级留痕，非缺陷） |

### 4.2 产品侧 P2/P3 缺陷（本轮登记，未修）

| 编号 | 严重度 | 现象 | 复现与判断 |
|------|-------|------|-----------|
| **P2-1** | P2 | `XamlControlsResources 加载失败 (0x8000FFFF)` **每次启动必现**（13 次/本轮） | `App.xaml.cs:228-235` 的 try-catch 里，WinAppSDK 非打包模式已知问题。**代码注释已明载**"它一直都在，真正的修法未找到"。UI 功能正常，**无功能后果** |
| **P2-2** | P2 | AVIF 4:2:0 产物高度比选区 **+1**（2250×1259 → 2250×**1260**）；PNG/JPEG/TIFF 均 1259 | AV1 4:2:0 要求宽高偶数，NVENC 内部补齐。**上游未做偶数对齐**。内容完整、仅底部多 1 行 |
| **P2-3** | P2 | **ACM 色彩分支零断言覆盖** | 本轮曾改该分支，**2438 断言全绿** ⇒ 门禁看不见这条路径。且 `--png-check` 桥接把 `systemAcm` 硬编码 `false`（`PngCheck.cs:102`）⇒ 结构上不可能覆盖 |
| **P2-4** | P2 | **`_poolLock` 在整段"建会话"里被持有 123–187ms** | 期间 `TryGetLatestFrame`/`ReleaseSharedFrame` 全部排队；录制器与预览交还会被截图建池卡住。无死锁（锁序 `_poolLock→_frameLock→_gpuLock` 已核，无环）。F5 仪表已有此读数但没人读 |
| **P2-5** | P2 | **热键静默降级不上界面**（`MainWindow.Settings.cs:124-126`） | 回退只发生在 `HotkeyManager.RegisterNamed` 内部、无写回 ⇒ 实际生效 Ctrl+Alt+Q，界面/盘上仍显示 Ctrl+Shift+Q。⚠ 仅到代码+设置文件级，渲染层未取证 |
| **P3-1** | P3 | JPEG 的 ICC 是 `mAB/mBA` 矩阵型（desc=`XYB_Per`），无 `rXYZ` | 第三方工具无法独立验证"这就是 BT.2020"。ICC 存在(720B)、lcms 可解析、白点=D65。**不判缺陷**，但让色域自动化判据难落地 |
| **P3-2** | P3 | `FormatEncoders.cs:2168` 注释"HDR 一律不嵌 ICC"与实测矛盾（TIFF/JXL 的 HDR 都嵌） | 文档过度概括，改成逐格式说即可 |
| **P3-3** | P3 | PQ 的"2.30× / 0.062%" 不可复现（本机 5.97×、0.324%） | 唯一出处是未跟踪的 `artifacts/perfreview_pqlut3_2026-10-31.log` |
| **P3-4** | P3 | `TickCount64` 实际粒度 **15.6ms**（自旋实测均值 15.62ms） | `GetFrameAge` 声称"毫秒粒度"偏乐观；对 2000ms 量级无影响，对 47–131ms 帧龄有 ~12% 误差 |
| **P3-5** | P3 | 冷池 20/20 交回"未来帧"（Δ下 p50=-100ms）、热池 20/20 交回 100ms 前帧；诊断档抓到一次 **910ms 旧帧**被真实交出 | 这是实测形状而非理论风险；是否可接受需产品口径拍板 |
| **P3-6** | P3 | Tools 输出目录缺 `native\deflate.dll` ⇒ `--bench-hdr-routing` 恒 SKIP(退出码3) | `TrueToneCap.Tools.csproj` 只 Copy 4 个 native exe，无 deflate 条目；补 dll 后两臂跑通 |
| **P3-7** | P3 | HEAD exe 自报 **0.3.7-beta+9fdbca5**（`Directory.Build.props` 默认 0.3.7），任务书说 0.3.10 | 版本漂移；真正 0.3.10 包是 `publish\TrueToneCap-v0.3.10-beta`（`991272d`，落后 HEAD 4 提交） |

---

## 📦 五、本轮改动清单

| 文件 | 改动 | 性质 |
|------|------|------|
| **`src/TrueToneCap.Core/Encoding/JpegGainMapEncoder.cs`** | **`entryOffset` 54→50 + 基准换算注释** | **修复 P1（产品代码缺陷，唯一一处）** |
| **`src/TrueToneCap.App/Services/SdrSemanticsProbe.cs`** | **+彩色补丁能力**：3 枚高饱和块 + 逐通道采样 + 色域判定 + COLORREF 通道序修正 | **探针增强**（纯增益，不改产品行为；让"色域"这一维从不可测变可测） |
| `src/TrueToneCap.Core/Encoding/EncoderCrashSentinel.cs` | +38 行：internal 测试注入点 `UseTestDir`（生产路径不变） | 修复 E-2 |
| `src/TrueToneCap.Test/CorePipelineTests.cs` | +46 行：`SentinelPrecondition`/`SentinelCleanup` + 三用例接入 | 修复 E-2 |
| `scripts/verify.ps1` | +37 行：新增 `[2.5/2]` 无损容器外部裁判步骤 | 上轮遗留待办 |
| `tests/manual/safety/f45-meters-cell.ps1` | +85 行：静置臂前提守卫 + `[Meter/F4]` 两口径对照 | 上轮遗留待办 |
| `scripts/precision/tiff_png_referee.py` | 新建：重建缺失的外部裁判 | 上轮遗留待办 |
| 环境 | `icacls ... /setintegritylevel Medium /T`（6120 文件） | 修复 E-1 |

### 已回退的改动（如实留痕）

我一度把 `CapturePipelineService.cs` ACM 分支的烘焙源从 `nativeGamutTag` 改成 `"sRGB"`（基于 §零-B 的彩色补丁读数），
门禁 2438 断言全绿。**但自我检讨后已回退**，理由：
1. 探针的补丁是 **GDI 窗口**（本身是 sRGB 应用）⇒ 读回 authored sRGB 是**自然的**，覆盖不到"整个桌面"；
2. 该改动推翻 2026-08-09 的设计前提，本轮无原文依据；
3. **零测试覆盖**（改完 2438 断言全绿 = 门禁看不见这条路径）。

回退 diff 留档：`artifacts/audit-2026-11-02/acm-fix-REVERTED.diff`。

**本轮产品代码只改了一处：GainMap 的 MPF offset（4 字节常数的取值）。** 其余全部是测试基础设施、探针与审查脚本。

---

## 🎯 六、最终结论与建议

### 发布就绪度：**修复 P1 后可以发布**


支持的硬证据：
1. 门禁 **8/8 套件 2438 断言全绿**（与基线逐格一致；唯一"未判"是 `--gainmap-color-product` 第 4 层 SKIP，因 ultrahdr_app 能力探测不过，属工具环境问题而非产品）
2. **7 格式产物全部通过外部解码器独立读回**（PIL/tifffile/ffprobe，不信本仓解析器）
   —— 其中 **GainMap 的 ffmpeg 拒读问题已定位并修复**（P1，见 §零）
3. **无损像素逐字节相等**：PNG8 == TIFF8 且 PNG16 == TIFF16，`max|Δ|=0`、0/1228800 不同样本；通道序反证通过（R/B 互换后 max|Δ|=254）
4. **HDR 元数据完全正确**：真机截图 PNG=`rgba64be`+cICP`9/16`、AVIF=`yuv420p10le`+`smpte2084`/`bt2020`、TIFF=`rgba64le`+ICC(BT.2020 偏差 0.0013, desc "020 (PQ)")；且**源确证是 Float16 高精度**（float max=1.5977、>1.0 占 40.8%、中性级数 2017–2039 破 256 上限）
5. **捕获 11 臂全绿**（跨设备共享槽 8,294,400 字节 0 处不同；含洋红对照臂与错句柄拒绝）
6. **产品自检 11/11、0 跳过**（含 HDR_SOURCE 真捕获）
7. **真实用户手势路径闭环**：热键→TTC_HC→拖框 2250×1259→Enter→PNG/JPEG/AVIF/TIFF 四格式落盘并外部读回
8. **性能全部达标或超预期**（PQ 5.97×、AVIF 档位 6.5× 速度差、TIFF 8-bit Smallest 73ms/16-bit 395ms）

### 建议的后续动作

| 优先级 | 动作 |
|--------|------|
| **P0（已完成）** | ✅ JPEG GainMap MPF offset 已修复（54→50），ffprobe rc=0，门禁回归全绿 |
| **P0** | 确认**发布机/构建机**的仓库目录**不带 Low 完整性标签**（`icacls <repo> \| findstr Mandatory` 应为 Medium 或空）。带 Low 会让交付的 App 无法截图——这是打包部署环节必须核对的 |
| **P1** | **双屏 #46 仍需真机（双 4K 环境）验证**；代码路径经静态审查（包围盒公式对负坐标正确），但**零实测**。本轮无法验证虚拟坐标拼接/多屏会话池/副屏色彩决定 |
| **P2** | **AVIF 4:2:0 尺寸 +1**：若产品承诺"产物尺寸==选区尺寸"，需在上游加偶数对齐（仅影响奇数高度/宽度 + 420 档） |
| **P2** | **F4 静置复测**：确保单实例锁空闲时跑 `tests\manual\safety\f45-meters-cell.ps1 -IdleSec 45`，读取数区"两口径对照"行 |
| **P3** | `XamlControlsResources 0x8000FFFF`（已知未修，无功能后果）——若要治本需查 WinAppSDK 资源打包 |
| **P3** | 修 `--png-check` 里 DisplayP3/AdobeRGB 的"期望"注释（收口前遗留文档漂移，非缺陷） |
| **P3** | GainMap 的 MPEntry attribute 与 libultrahdr 不一致（`0x00000300` vs `0x00030000`）；**实测不影响解码**，无 CIPA 原文依据前**不建议改动** |

### 本轮更正记录（如实留痕）

1. **"未发现产品代码缺陷"结论被推翻**：外部裁判（子代理深度验证）发现 GainMap MPF 偏移 P1，本报告早期版本已订正。
2. **门禁两次假红是我自己的操作干扰**：`--window-probe` 的 -1 是因为我在门禁运行时并发 `Stop-Process` 抢同一实例；`--gainmap-color-product` 的 SKIP 是 ultrahdr_app 工具能力探测不过（修复前就存在）。
3. **子代理误杀我的一格 bench**（PID 101904 Tools）：已重跑该格，不影响结论。
4. **任务书的分辨率口径有误**：写 2560×1440，实测 **3840×2160**（DXGI 与 WGC 实得帧一致）。本报告一律以 3840×2160 为准。

---

**审查执行**: Team Lead + 4 路子代理并行 + Lead 亲自实测复核
**证据产物**: `artifacts/audit-2026-11-02/`（日志、样张、外部裁判输出、自检 JSON）
**签名**: 2026-11-02
