# 性能实现结果（2026-10-31，承接 10-30 全管线审查）

> **判词先行**
> ① 审查候选 **A（AVIF 真 10-bit 压缩强度）**、**E（OCR 解码 Span + rec 合批）**、**D（CPU OCR 懒建）** 已落地，
> 各有源码锁、变异与真机读数；产物/文本对等均已验证。
> ② 候选 **B（常驻池按需刷新）** 维持审查判词"不做"，本批未动一行。
> ③ 候选 **C（libdeflate）** 已完整落地：启动期探测 `deflate.dll`，缺失/坏掉自动回退内置 zlib-ng；
> 按素材自适应（非极致可压缩才外包给 libdeflate 10，UI 型保持 zlib-ng 9）。DLL 已入库并接通
> 发布 / MSIX / 校验 / 许可链路；真实样张实测 Smallest 档 11.78s→6.01s 且小 6.04%（§4）。
> ④ 跨屏（#46）仍缺硬件，本批读数全部来自单块 SAC2765。
> 所有数字都能落到 `artifacts/` 下的同名日志；未量的格子写"未量"，不写估计值。

---

## 1. 候选 A：AVIF 真 10-bit 通路的压缩强度（已落地）

### 改了什么

| 位置 | 改动 |
|---|---|
| `src/TrueToneCap.Core/Encoding/ImageEncoder.cs:241` | `EncodeArgb10Async` 增 `CompressionEffort effort = Balanced` 形参（默认实现透传） |
| `src/TrueToneCap.Core/Encoding/FormatEncoders.cs:386-387` | HDR 硬解入口把 `s.Effort` 传进 `TryEncodeNvencHdrAvif` |
| `src/TrueToneCap.Core/Encoding/FormatEncoders.cs:457-458` | SDR 真 10-bit 分支把 `s.Effort` 传进后端 |
| `src/TrueToneCap.Core/Encoding/FormatEncoders.cs:1494/1566/1582` | 三处 `EncodeAv1Argb10` 全部改 5 参重载（4 参 = 该路恒 Balanced） |
| `src/TrueToneCap.Core/Encoding/NvEncoderNative.cs:1219` | `PickPresetForEffort` 早已存在，此前 10-bit 两条路没把 effort 传到它 |

### 运行时证据（不是只读源码）

`artifacts/perf_avif10_effort_4k_2026-10-31.log`：4K（3840×2160）A2R10G10B10 合成梯度，
反射调 `TryEncodeNvencHdrAvif`，三档各跑 2 次。两次独立运行（07:02 与 07:05）结果一致：

| 档位 | 选中 preset GUID | 对应档 | 耗时 samples（ms） | 产物字节 |
|---|---|---|---|---|
| 极高 Smallest | `84848c12-6f71-4c13-931b-53e283f57974` | P7 | 433 / 76 | **968,182** |
| 均衡 Balanced | `90a7b826-df06-4862-b9d2-cd6d73a08681` | P6 | 93 / 87 | **965,850** |
| 快速 Fastest | `fc0a8d3e-45f8-4cf8-80c7-298871590ebf` | P1 | 81 / 73 | **1,009,741** |

- **判据意义**：三档各自选中不同的 preset GUID、产物字节三段互不相同 ⇒ effort 真的改掉了编码器档位，
  不是"形参收下、内部仍恒 P1"。改前三条路的 effort 恒为默认 Balanced（P6），三档产物会完全一样。
- **不承诺时间/体积方向**：每档计时都包含一次编码会话创建（~15–20ms 底噪）；P7 首轮 416–442ms
  在本机三次运行都复现，属该 preset 的首次引擎侧开销，**不是稳态**（第二发 75–76ms）。
  合成梯度上 P7 反而比 P6 大 0.24% —— **真实样张的体积/耗时差未量**，本批不写"极高更小"这种话。
- **回退**：没有开关；还原三处传参即回到旧行为（设置里的档位对 10-bit 路不再生效）。

### 判据与变异

- `CorePipelineTests.cs:4702` 五条源码锁：接口形参、SDR 10-bit 调用点、HDR 入口接线、三处 5 参调用。
- 变异 `artifacts/mutation_avif_effort_2026-10-30.log`：去掉一处 effort 传递 ⇒ 恰好 1 红（1744/1/1）。

---

## 2. 候选 E：OCR 解码 Span + rec 合批（已落地）

### 改了什么

| 位置 | 改动 |
|---|---|
| `OnnxOcrEngine.cs:128-136` | `s_recBatchEnabled`（`TTC_OCR_BATCH != "0"`）、`RecBatchSize = 4`、`s_spanDecodeEnabled`（`TTC_OCR_SPAN != "0"`） |
| `OnnxOcrEngine.cs:454-461` | 识别主循环 >1 框时按 `RecBatchSize` 合批，单框仍走原路 |
| `OnnxOcrEngine.cs:805-833` | `RunRecognitionBatch`：`[4,3,recH,recMaxW]` 池化张量、一次 Run、逐行解码 |
| `OnnxOcrEngine.cs:854` | `FillRecognitionInput`：串行与合批共用一份裁剪/padding/双线性/归一化（不许两份算术） |
| `OnnxOcrEngine.cs:707-709/965-967` | 检测概率图与 rec logits 在 `DenseTensor<float>` 时直接读 `Buffer.Span`，非 Dense 仍走原索引器兜底 |
| 两处 `catch` | 合批异常 ⇒ 该批整批退回 `RunRecognition` 串行（宁慢不丢行） |

### 读数（1200×800 UI 文本 / 14 框 / DirectML 与 CPU 各 5 次 × 两次独立轮）

手臂口径：`nospan = 旧多维索引器`；`span = 连续内存读`；`batch = Span + 合批`。
**判据只认 total** —— `det/rec` 分段是事后对每个框单独调 `RunRecognition` 的串行探针，
不经过合批路径，在合批臂上与 total 不同源，不能拿来比较（见 `PerfMeasureBench.cs:93-110`）。

| 臂 | DirectML total（两次独立轮，ms） | CPU total（ms） | 文本 |
|---|---|---|---|
| 多维索引 + 串行 | 752 / 730 | 6,645 | 440 字，SHA `5C4B82FD` |
| Span + 串行 | 586 / 619 | 5,289 | 同 |
| Span + 合批 | 503 / 538 | 7,143* | 同 |

\* CPU 该轮 `rest` 中位 2,141ms（对照 194–510ms），是本机挖矿/浏览器背景负载；扣掉 `rest` 为 4,949ms
（对照 5,095ms），**合批在 CPU 上中性**。

- **Span 是明确的赢面**：DML −20%（752→586、730→619），CPU −20%（6,645→5,289）；rec 段 DML 625→485ms。
- **合批在两条仪器上结论不一致**：引擎直调（上表）−13~15%；`MultiOcrService` 路由 3 轮
  `artifacts/perf_ocr_route_dml_nobatch_2026-10-30.log` 538ms vs `..._batch...` 555ms（+3%），落在噪声内。
  ⇒ **不把合批当性能承诺**；默认保留只因为产物逐字相同、退回路径完备，开关 `TTC_OCR_BATCH=0` 随时可关。
- **正确性**：三臂 × 两 provider 的 OCR 文本 SHA 全部 `5C4B82FD`（440 字）；
  路由级 CPU 臂（懒建后）同样 `5C4B82FD`（`artifacts/perf_ocr_route_cpu_lazy_2026-10-31.log`）。

### 判据与变异

- `CorePipelineTests.cs:4734` 七条源码锁：共用预处理、合批张量形状、逐框解码、两处退回、两个开关、Span 读与唯一兜底。
- 变异 `artifacts/mutation_ocr_batch_fallback_2026-10-30.log`：删掉一处退回 ⇒ 恰好 1 红。

---

## 3. 候选 D：CPU OCR 会话按需构建（已落地）

### 改了什么

| 位置 | 改动 |
|---|---|
| `MultiOcrService.cs:93-102` | 启动期仅 `!gpuReady || eagerCpu` 时建 CPU（`TTC_OCR_EAGER_CPU == "1"` 回到旧行为） |
| `MultiOcrService.cs:36` / `:149-173` | `SelectedEngine` 取不到 OnnxCpu 时调 `EnsureCpuEngine` 补建，插在 Windows OCR 之前（GPU→CPU→Windows 顺序不变） |
| 建失败 | 不写负缓存（下次仍可重试）；CPU 会话只允许两处 `new`（启动兜底 + 按需） |

### 读数

启动日志（同一 Release 二进制，`TTC_OCR_EAGER_CPU` 两臂）：

| 臂 | 引擎表 | 首次切 CPU | 备注 |
|---|---|---|---|
| 默认（懒建） | 2 个：DirectML + Windows OCR | **175ms** | `artifacts/perf_ocr_route_cpu_lazy_2026-10-31.log`；日志 `CPU 引擎就绪（按需，175ms）` |
| `TTC_OCR_EAGER_CPU=1` | 3 个：DirectML + CPU + Windows OCR | 0ms（已常驻） | `artifacts/perf_ocr_route_eager_init_2026-10-31.log`，Initialize 1,045ms vs 懒建 915ms（单发，只报方向不报差值） |

真机启动内存（同一 exe，`Start-Process` 后按窗柄出现计时，`Get-Process` 采样）：

| 臂 | win | t0 WS / Private | t+8s | t+60s |
|---|---|---|---|---|
| 默认（懒建） | 1,198ms | 369 / 460MB | 373 / 486MB | 364 / 478MB |
| eager | 1,435ms | 441 / 531MB | 467 / 574MB | 465 / 573MB |

⇒ 常驻少 **约 95–100MB**（t+60s WS −101MB / Private −95MB；t+8s −94 / −88MB）。
启动到主窗口的 0.24s 差是单发读数，**不作结论**（两臂各自只有一次）。
工件：`artifacts/startup_mem_lazycpu_2026-10-30.log`、`artifacts/startup_mem_eagercpu_2026-10-31.log`。

### 判据与变异

- `CorePipelineTests.cs:4778` 六条源码锁：条件懒建、eager 开关、SelectedEngine 补建、插入位置、无负缓存、仅两处 new。
- 变异 `artifacts/mutation_lazy_cpu_2026-10-30.log`：把条件改成恒真 ⇒ 恰好 1 红。

---

## 4. 候选 C：无损 deflate 后端（可选 libdeflate + 自适应分流；DLL 已随包分发）

### 4.1 全量替换被判死，固定用 level 9 也不成立

真实形状 raw（2250×1350 BGRA16 的 PNG 滤波后扫描线，24,301,350 B），三种内容型；
libdeflate 各 level 都是同一份 `libdeflate_zlib_compress`，解压全部逐字节相等
（`artifacts/perf_png_levels_2026-10-31.log`，仓库外 harness）：

| 内容 | 后端 / level | 耗时 | 产物 | 相对 zlib-ng 9 |
|---|---|---|---|---|
| UI | zlib-ng 9 | **198ms** | **186,716 B** | 1.000 |
| UI | libdeflate 9 | 76ms | 201,046 B | **+7.7%** |
| UI | libdeflate 10 / 11 / 12 | 477 / 1,566 / 3,986ms | 197,736 / 189,003 / 176,774 | +5.9% / +1.2% / −5.3% |
| 噪声 | zlib-ng 9 | 5,924ms | 15,988,334 B | 1.000 |
| 噪声 | libdeflate 9 | **534ms** | 16,041,613 B | **+0.33%** |
| 噪声 | libdeflate 10 / 11 / 12 | 1,293 / 1,666 / 1,903ms | 15,630,332 / 15,462,575 / 15,395,131 | −2.2% / −3.3% / −3.7% |

真实样张（本机产品自己保存的一份 2250×1350 10-bit PQ PNG，IDAT 解压后就是同一形状的
24,301,350 B 扫描线；`artifacts/perf_png_realhdr_2026-10-31.log`）：

| 后端 / level | 耗时 | 产物 | 相对 zlib-ng 9 |
|---|---|---|---|
| zlib-ng 9 | 10,551ms | 5,499,379 B | 1.000 |
| libdeflate 9 | 2,816ms | 5,564,098 B | +1.18% |
| libdeflate 10 | **6,050ms** | **5,167,195 B** | **−6.04%** |
| libdeflate 11 / 12 | 8,153 / 10,829ms | 5,148,970 / 5,144,061 | −6.37% / −6.46% |
| zlib-ng 8 | 6,015ms | 5,517,263 B | +0.32% |

- UI 型（纯色/文字/渲染内容）上 libdeflate 在三档取舍里都输：9 快但大 7.7%，10/11 又慢又大，
  12 只小 5.3% 却要 3,986ms（20×）；而 zlib-ng 9 本来只要 198ms ⇒ **这一支必须留给内置**。
- 噪声型与真实截图型才是痛点（zlib-ng 9 要 5.9–10.6s）。噪声上 libdeflate 9 最快但大 0.33%；
  真实样张上 **level 10 同时赢速度（−43%）和体积（−6.04%）**，9/11/12 各输一边。
- ⇒ **按素材自适应**，且外包档不是 level 9 而是 **level 10**。探针与产品同一口径
  （256×4 KiB 均匀抽样共 1 MiB，再跑内置 level 1）：UI 0.012、真实样张 0.3208、
  噪声 0.837 ⇒ 阈值取 **0.05**，只把"极致可压缩"的 UI 型留在内置，其余（含最常见的真实截图）
  交给 libdeflate 10。（早期草稿用过 0.5，会把 0.3208 的真实样张也挡在内置上，
  白丢约 4.5–5.8s 与 6% 体积。探针是均匀抽样，与"连续首块 1 MiB"不是同一口径：
  后者在本样张上是 0.3064。）
- 边界：这份真实样张的高光上限只到纸白附近（≈120 nits），不含真正的过曝高光；
  它是真实产物的像素统计，但"强高光 HDR 样张"仍待量（§7）。

### 4.2 落地的形状

| 位置 | 内容 |
|---|---|
| `src/TrueToneCap.Core/Encoding/DeflateZlib.cs` | 新增统一入口：可选 libdeflate + 内置 zlib-ng。`NativeLibrary.Load` 显式路径（不依赖 DllImport 搜索），校验 4 个导出，压 64KB 样本再用 `ZLibStream` 解回全等才判可用 |
| 探测失败 | 缺失 / 架构不对 / 导出缺失 / 自检不等 / 运行中抛错 ⇒ **永久回退内置**，只记一次日志；运行中失败的那次保存立即用内置重压 |
| 自适应分流 | 只有 Smallest(9) 且 1 MiB 均匀采样的 level-1 压缩比 ≥ **0.05** 才外包，外包档 = **libdeflate level 10**（UI 0.012 留内置、真实样张 0.3208 与噪声 0.837 外包）；Quick(8) 永远走内置 |
| 启动探测 | `App.xaml.cs` 与 jpegli 同一后台任务，一行结论：`无损压缩后端: libdeflate（…\deflate.dll）` 或 `… 内置 zlib-ng（未找到 deflate.dll；放到 exe 旁或 native/ 即启用）` |
| 接线 | `ManagedPngEncoder.CompressDeflate`、`ManagedTiffEncoder` 的压缩段都只经 `DeflateZlib.Compress`；两者仍输出 zlib 封装（PNG IDAT / TIFF tag 8） |

### 4.3 产品路径 A/B（`--perf-png`，2250×1350 10-bit）

合成内容（自适应策略，`artifacts/perf_png_adaptive_{nodll,withdll}_2026-10-31.log`）：

| 内容 / 档 | 内置 zlib-ng（无 DLL） | 带 DLL（自适应） | 产物 |
|---|---|---|---|
| UI Smallest | deflate 208ms / 总 233ms | deflate 213ms / 总 234ms（探针 ~5ms，仍走内置） | 186,789 B，两者相同 |
| 噪声 Smallest | 5,854 / 6,041ms | 1,369 / 1,523ms（deflate 4.3×） | 15,988,407 → 15,630,405（−2.24%） |
| 噪声 Quick | 629 / 835ms | 621 / 828ms（不外包） | 15,615,385 B，相同 |

真实样张（`--perf-png --raw <该样张的滤波扫描线>`，
`artifacts/perf_png_realhdr_{nodll,withdll}_2026-10-31.log`）：

| 档 | 内置 zlib-ng（无 DLL） | 带 DLL（自适应） | 产物 |
|---|---|---|---|
| Smallest | 11,784ms | **6,013ms（−49%）** | 5,499,379 → **5,167,195 B（−6.04%）** |
| Quick | 5,950ms | 5,979ms（不外包） | 5,517,263 B，相同 |
| Fastest | 108ms | 109ms | 10,919,224 B，相同 |

带 DLL 的 Smallest 端到端 6,013ms 与 harness 直调 level 10 的 6,050ms 同量级 ⇒ 产品入口确实
按 0.3208 的采样比把真实样张交给了 level 10，不是"探针判错、仍在内置上慢跑"。
探针在 UI 型保存上多付约 5ms；在真实/噪声型上相对要省的 4–10s 可忽略。

### 4.4 判据与变异

- `CorePipelineTests.cs:4812` 新增 `Test_DeflateZlib_BackendProbeAndFallback`：**22 条源码与分发锁**
  （DLL 路径/开关/入口条件/level 10/阈值分流/运行中一次停用/自检/启动接线 + 12 条分发契约）
  + **9–10 条行为臂**（内置四档解压等价、档位正控、探针两侧正控、纯色内容即使 DLL 在位也走内置、
  Quick 永不外包、无 DLL 时产品入口 == 内置 level 9、有 DLL 时外包臂解压等价且产物不同）。
- 双环境实测：**无 DLL 1777 通过 / 0 失败 / 2 跳过**；**有 DLL 1778 / 0 / 1**
  （`artifacts/unit_deflate_backend_2026-10-31.log`、`..._withdll_...`）。两者的差正是
  "libdeflate 正臂"从 1 条跳过变成 2 条断言。
- 变异：阈值 `0.05 → 0.0` ⇒ **1776/1/2，恰好 1 红**（探针两侧正控）。
  工件 `artifacts/mutation_deflate_threshold_2026-10-31.log`。

### 4.5 分发链路（已接通）

- 二进制入库：`src/TrueToneCap.Core/Resources/deflate.dll`（183,808 B，SHA256
  `e859291f…0f38dff`，libdeflate 1.26 / 上游 commit `2b7cdc5`）。
- **开发输出与 MSIX**：App.csproj 新增 `Resources\*.dll` 的 Content（`Link=native\…`）与
  `TtcNativeLibs` 拷贝；4 枚 exe 的硬断言旁新增"deflate.dll 必须恰好 1 个"的断言。
- **发布**：`Publish.ps1` 的 `$toolFiles` 加 `deflate.dll`（缺件即 throw），汇总按 `native/` 全部文件计数。
- **完整性**：`tools-manifest.json` 记录 sha256/sizeBytes/licenseFiles；
  `verify-package.ps1` 加 128KB 尺寸下限，并明确跳过非 exe 的启动探针。
- **合规**：`licenses/libdeflate-MIT.txt` + `licenses/INDEX.json`（shippedIn=`deflate.dll`）+
  notices 生成器新增产物桶；`THIRD-PARTY-NOTICES.md` 已重生成并通过 `--check`（46 条许可）。
- **新鲜度**：`scripts/verify.ps1` 的构建输入面从 `*.exe` 扩到 `*.exe,*.dll`。
- **实测（绿色包）**：`Publish.ps1 -AllowDirty -SkipTests -OutputDir publish\TrueToneCap-v0.0.0-deflatecheck-2026-10-31`
  成功（5 个原生产物、deflate.dll SHA256 校验通过）；`verify-package.ps1` 全绿；
  发布包内 `TrueToneCap.exe` 启动日志 `无损压缩后端: libdeflate（…\native\deflate.dll）`。
  工件：`artifacts/publish_deflate_check_2026-10-31.log`、`artifacts/verify_package_deflate_check_2026-10-31.log`。
- **MSIX**：`dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true`
  成功（0 警告 / 0 错误，`TrueToneCap.App_0.3.10.0_x64.msix` 151.4 MB）；
  `makeappx unpack` 后 `native/deflate.dll` 183,808 B 在位；`verify-msix.ps1` 新增点名检查并通过
  （包内 603 个文件）。工件：`artifacts/msix_build_deflate_check_2026-10-31.log`、
  `artifacts/msix_native_listing_2026-10-31.log`、`artifacts/verify_msix_deflate_check_2026-10-31.log`。

### 4.6 本轮补齐的三项与仍未覆盖的边界

- **真实 HDR 样张已量**（§4.1 / §4.3）：Smallest 档 11.78s→6.01s，产物小 6.04%，
  且这是把策略从"一律 level 9"改成"level 10 + 0.05 阈值"的直接依据。
  边界：该样张高光只到纸白附近（≈120 nits），强高光样张仍待量（§7）。
- **MSIX 已打包并核对内容**；**未做侧载安装**（需开发者模式或受信任证书），包本身仍是 NotSigned。
- **签名 / 杀软**：`deflate.dll` 与 `TrueToneCap.exe` 都是 **NotSigned**（本机构建无证书）；
  MP 扫描**没能执行**——Windows Defender 服务是 Stopped，活动杀软是本机 360 安全卫士
  （`MpCmdRun` 退出码 2 / `0x80004005`）。这不是"扫描干净"，是"没扫"。
  工件：`artifacts/signature_status_2026-10-31.log`。

---

## 5. 判据、变异与门禁（本批）

| 项 | 结果 | 工件 |
|---|---|---|
| 单元测试（无 deflate.dll，默认门禁口径） | **1777 通过 / 0 失败 / 2 跳过** | `artifacts/unit_deflate_backend_2026-10-31.log` |
| 单元测试（有 deflate.dll，正臂） | **1778 通过 / 0 失败 / 1 跳过** | `artifacts/unit_deflate_backend_withdll_2026-10-31.log` |
| 发布链路（绿色包） | Publish 退出码 0，5 个原生产物，deflate.dll SHA256 校验通过 | `artifacts/publish_deflate_check_2026-10-31.log` |
| 包校验（verify-package） | 全绿；`native\deflate.dll (179.5 KB)`；非 exe 跳过启动探针 | `artifacts/verify_package_deflate_check_2026-10-31.log` |
| MSIX 构建 | 0 警告 / 0 错误；包 151.4 MB | `artifacts/msix_build_deflate_check_2026-10-31.log` |
| MSIX 包内核对（verify-msix） | `native/deflate.dll` 183,808 B；包内 603 个文件；全绿 | `artifacts/verify_msix_deflate_check_2026-10-31.log`、`artifacts/msix_native_listing_2026-10-31.log` |
| 发布包启动 | 日志 `无损压缩后端: libdeflate（…\native\deflate.dll）` | 发布包内 `log/app_2026-10-31.log:08:09:21` |
| 门禁基线重生成 | 2026-10-31 08:34:56；总断言 2390（unit 1777 / 跳过 2） | `artifacts/gate_baseline_regen_2026-10-31.log` |
| 变异 A（effort 断一处） | 1744/1/1，红的是本批判据 | `artifacts/mutation_avif_effort_2026-10-30.log` |
| 变异 E（删一处退回） | 1744/1/1 | `artifacts/mutation_ocr_batch_fallback_2026-10-30.log` |
| 变异 D（条件恒真） | 1744/1/1 | `artifacts/mutation_lazy_cpu_2026-10-30.log` |
| 变异 C（探针阈值 0.05→0.0） | 1776/1/2，恰好 1 红 | `artifacts/mutation_deflate_threshold_2026-10-31.log` |
| App Release 构建 | 0 警告 / 0 错误 | `artifacts/msix_build_deflate_check_2026-10-31.log` |
| 签名 / 杀软 | 两个产物 NotSigned；Defender 停用、360 在跑 ⇒ MP 扫描未执行（不是"干净"） | `artifacts/signature_status_2026-10-31.log` |
| 文本对等 | 所有 OCR 臂 `5C4B82FD`（440 字） | 上表各日志 |
| 启动探测真机（有/无 DLL） | 有：`libdeflate（…\deflate.dll）`；无：`内置 zlib-ng（未找到 deflate.dll…）` | `bin\x64\Release\…\win-x64\log\app_2026-10-31.log:07:44:50 / 07:45:01` |

⚠ 单测的最终数字以"最后一次改源之后现取"为准。本批最后一次**行为**改源是 `DeflateZlib.cs` 的
档位/阈值回填（level 10 + 0.05），随后两臂各现取一次；门禁基线 08:34:56 写盘（unit 1777 / 跳过 2）。
之后只给探针正控补了失败详情（`flat=True/random=True`，断言数不变）并重跑了变异与两臂，
所以基线文件未再重生成——断言规模 2390 这一格本次是自比，下一轮才由它拦。

## 6. 开关一览（本批新增）

| 开关 | 作用 | 默认 |
|---|---|---|
| `TTC_OCR_SPAN=0` | 回到 `Tensor` 多维索引器读取 | 开（Span） |
| `TTC_OCR_BATCH=0` | 回到逐框串行识别 | 开（合批，`RecBatchSize=4`） |
| `TTC_OCR_EAGER_CPU=1` | 启动即建 CPU 会话（旧行为） | 关（按需） |
| `TTC_DEFLATE=0` | 强制内置 zlib-ng（诊断/A-B） | 开（有 DLL 且素材非极致可压缩时用 libdeflate 10） |
| `TTC_DEFLATE_DLL` | 指定 `deflate.dll` 路径（测试/排查） | 未设：exe 旁 → `native\` |

## 7. 未做 / 未量

- **B（池内按需刷新）**：维持审查判词不做——静态桌面收益≈0，动态场景需先补"每帧拷贝次数/有无消费者"仪表。
- **A 的真实样张体积/耗时差**：未量（本批只有 4K 合成梯度）。
- **强高光 HDR 样张**：本批真实样张高光只到纸白附近（≈120 nits）；更亮素材对选路的影响未量。
  阈值 0.05 与样张实测 0.3208 之间余量很大，预期不翻转，但没有读数就不写结论。
- **MSIX 侧载安装**：包已构建并核对内容，未安装（需开发者模式或受信任证书）；Publisher 仍是占位符。
- **杀软扫描**：本机 Defender 服务停用、360 为活动 AV，`MpCmdRun` 无法执行 ⇒ 未扫（不是扫干净）。
- **签名**：两个产物 NotSigned（本机构建无证书）；正式分发仍需代码签名。
- **跨屏（#46）**：缺第二块屏，未做。
- **合批在 DML 上的真收益**：两条仪器互相矛盾（−13~15% vs +3%），判为"不承诺"，不再加仪表追。
