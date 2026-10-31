# 全管线性能审查（2026-10-31）

> 带日期 ⇒ 取证快照，不回写。本文不改任何代码。
> 口径：① 8 路并行源码审查（捕获/编码-原生/编码-托管/色彩/UI/OCR/启动/跨切面反模式 + 一路仪表盘点）；
> ② 本轮从出货目录日志**现取**的真机分布（`app_2026-10-30.log`，n=47 次启动）；
> ③ 本轮新跑的三台离线仪表（`--perf-png` 4K 两臂、Tools 无参 4K 全格式、仓库外 PQ 内核对照）。
> 判词只认②③这类能指到文件的读数；单路代理报出而我没有复现的，逐条标 **单路未复现**。

## 判词

1. **一条功能性缺陷兼最大单点杠杆：AVIF 的"压缩强度"在 8-bit 产物腿与硬件失败回退链上根本不生效。**
   `FormatEncoders.cs:518` 调 `be.EncodeAsync(...)` 只传 11 个位置参，接口第 12 参
   `CompressionEffort effort = Balanced`（`ImageEncoder.cs:229`）于是恒取默认；同族漏传还有 6 处
   `AvifFallbackHelper.FallbackToLibAom(...)`（`:908/1177/1191/1205/1214/1401`，签名 `:1667-1669` 明确带
   `effort = Balanced`），以及 `MftAvifEncode` —— 它**收了** `effort`（`:853`）却整段不用，
   硬件 leg `new MftEncoderNative(w, h, useAv1: true)`（`:878`）与 libaom 回退（`:908`）两条都不接。
   后果有现成读数：`CompressionEffort.cs:124-128` 的 avifenc `-s` 三档 = **s0 7.2s/110.2KB、s6 0.94s/144.3KB、
   s10 0.59s/180.1KB**。用户在界面上选"极高"，拿到的是 s6；选"快速"，拿到的也是 s6。
   ⚠ 本轮订正一处我自己写错的标签：NVENC 侧 `Balanced` 经 `CompressionEffortMap.NvencPresetNumber`
   得的是 **P4**（`CompressionEffort.cs:184-190`，`_ => 4`），不是 10-31 交付文档 §1 表里写的 "P6"
   —— 那张表是拿驱动枚举出的 GUID 反推档名，与本仓自己那份映射不一致；
   驱动枚举失败时还会再退到 P1（`NvEncoderNative.cs:1226`）。GUID↔档名哪一份是对的**未量**，
   但"界面档位到不了这条腿"这一条与标签无关，成立。
   2026-10-31 那批只修了**真 10-bit** 三条腿并补了五条源码锁，8-bit 家族与回退链不在锁面内
   （`CorePipelineTests.cs:3310-3349` 那把行切锁只数 `ManagedPngEncoder.Encode*/ManagedTiffEncoder.Encode`，
   明确排除的是"中转 `fastest: true`"与"保底 PNG"两类，没有一条覆盖 `be.EncodeAsync`）。
2. **保存/编码仍是唯一秒级段，但 deflate 已经不是它的名字。** 本轮 4K 10-bit 噪声 PNG 两臂实测
   内置 zlib-ng 9 = **14,889ms**（deflate 14,671，占 99%）→ libdeflate 自适应臂 = **3,937ms**（deflate 3,126），
   产物 43,532,976 → 42,549,000 B（−2.26%）。⇒ 10-31 落地的 libdeflate 分流在 4K 上同样成立（3.8×）。
   剩下的非 deflate 段是 `转换+滤波 127–498ms`（两臂间抖动大，见 §6）加上一笔**当时从没量过**的
   CRC/搬运/写盘（`DeflateZlib.cs:120-127` 无初值 `MemoryStream` + `ToArray()`、`AtomicFileWriter.cs:36` 默认 4KB 缓冲）。
   ⚠ **口径标注（裁定 1 要求的"**claim 与判据混在一句里**"就地处理：声明不删，改标）**实测/推算"**：
   ① 转换+滤波那半是**实测**；② 搬运腿同日第四轮补上了**分段实测** = IDAT 走流式后，内置路由每次保存
   **少分配 8.0 MB**（`--bench-png-idat` 的分配列，两臂码流逐字节相同；见 §8 批2 那张表下面的「分配腿读数」），
   缓冲初值那半见 §8「deflate 初值 A/B」；③ 但"**整条保存链省了多少内存**"至今**没有同尺仪表** ⇒
   那一格只能读成**推算（estimate）**，谁引用它都得带着这两个字；④ 写盘缓冲（`AtomicFileWriter` 1 MiB）
   只改了形状，**未单独计时**，不许被前两个读数顺带覆盖。
   ~~**TIFF 全尺寸耗时至今零读数**~~ ⇒ 同日第二轮第一次有数（§8 批2 那张 4K TIFF 表：16-bit Smallest
   端到端 393ms），且分条并行已落地 ⇒ 上面"一处 `Parallel.` 都没有 / 单 strip"这两句描述的是**改前**形状。
3. **逐像素 pow 不是秒级凶手，量级被我自己的仪表打回原形。** 色彩那一路报"每帧约 5,000 万次 pow、估 150–400ms"，
   仓库外科目实测：4K 一帧 2,488 万次 PQ（每次两个 `MathF.Pow`）在 20 核上 = **134.6ms**，
   同形状内存下限 40.2ms；换成 `L^(1/4)` 双 sqrt + 4096 格插值 = **58.4ms（2.30×）**，
   且 10-bit 码字最大误差 **1**、全域 2,000 万密扫只有 **0.062%** 的取值与逐像素 pow 不同码。
   ⇒ 值得做（HDR 每条 PQ 路都吃这一趟），但不该被写成"秒级收益"。
   同台仪器顺手判死一条：`判界表 + 二分`（按构造应当逐码相同）**0.88×，比 pow 还慢**，
   且 20M 密扫仍有 8,865 处不同码（float 边界），**不要走这条路**。
4. **热键路径上没有秒级项，但"每次一帧都不白等"这件事没人证过。** 真机 n=47 分布：
   窗口构造 p50 **51ms**、窗口初始化 p50 **31ms**、首帧 UploadFrame p50 **8ms**；
   WGC 建会话 SDR p50 **65ms**（p90 118、max 198）而 HDR p50 **13ms** —— SDR 那条腿贵 5 倍，值得单独问一句为什么。
   启动侧：**进程到主窗口 p50 945ms / p90 1,656ms / max 2,146ms**，其中 XAML 初始化 p50 381ms。
5. **日志链路在门控之前就把活干完了**（`LogService.cs:339-340` 的两次正则 Scrub 在 `:371` 的
   `IsEnabled` 判断**之前**），且 `:271` 常驻 writer `AutoFlush = true` + 全局 `s_lock` ⇒ 每条一次 WriteFile。
   默认 Brief 档下 App 侧 41 处 Debug/Trace 行"被丢弃却照样付全额"；这条同时是启动、捕获热路径与
   录制期共同的固定成本，**目前没有任何一臂量过它**。
6. **UI 逐帧与静置这一族是本轮最可疑、证据最薄的一块**（详见 §3-F4，全部单路未复现）：
   悬停换窗触发整张 2048×1408 图集重排、Flip 模型每帧重建 RTV、预览窗静置每 3s 全量 `EnumWindows` + 重绘、
   渲染循环用 `Thread.Sleep(16−耗时)` 定步长。⇒ 这一族**先补仪表再动代码**，顺序与 10-30 那条
   "去克隆要先补『已交出的帧有没有被后续帧改写』仪表"同一族纪律。

---

## 1. 本轮口径

- 二进制：`dotnet build TrueToneCap.slnx -c Release --no-incremental` → **0 警告 / 0 错误 / 48.23s**
  （`artifacts/perfreview_build_2026-10-31.log`），Tools/Test/App 宿主 09:08–09:09 新编。
- 真机分布：`src/TrueToneCap.App/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/log/app_2026-10-30.log`
  （出货目录 exe 同级 `log/`；n=117 次启动、其中 47 次记到"进程到主窗口"），
  `awk` 抽 ms 后排序取 p50/p90/max，命令见 §6。
- 离线仪表：`src/TrueToneCap.Tools/bin/Release/net11.0-windows10.0.26100.0/TrueToneCap.Tools.exe`
  - `--perf-png --w 3840 --h 2160` 两臂：`artifacts/perfreview_png4k_builtin_2026-10-31.log`、
    `..._libdeflate_...`（后者 `TTC_DEFLATE_DLL` 指 `src/TrueToneCap.Core/Resources/deflate.dll`）
  - 无参 4K 全格式：`artifacts/perfreview_formatbench4k_2026-10-31.log`
  - PQ 内核对照（仓库外，`%TEMP%\pqlut`）：`artifacts/perfreview_pqlut3_2026-10-31.log`
- 硬件：RTX 5080、20 逻辑核、单块 SAC2765（HDR）。**跨屏（#46）本轮依旧缺硬件，全部读数为单屏。**

## 2. 分段读数

### 2.1 真机启动与开窗（n 见每行）

| 段 | p50 | p90 | max | n |
|---|---|---|---|---|
| 进程 → 主窗口 | **945ms** | 1,656 | 2,146 | 47 |
| └ XAML 初始化 | 381ms | 500 | 610 | 47 |
| └ 设置加载与回显 | 28ms | **509** | 722 | 47 |
| └ 构造尾巴（托盘/热键/OCR/本地化） | 55ms | 93 | 108 | 38 |
| WGC 建会话 0x10001 3840x2160 SDR | 65ms | 118 | 198 | 127 |
| WGC 建会话 0x10001 3840x2160 HDR | 13ms | 21 | 28 | 126 |
| 选区窗口构造 | 51ms | 69 | 123 | 144 |
| 选区窗口初始化 | 31ms | 52 | 96 | 144 |
| 首帧 UploadFrame | 8ms | 28 | 59 | 155 |

**两条从日志结构直接读到的固定行为**（117/117 次启动全部成立，不是抽样）：
- 主窗口出现**之前**已经写了 **3 次** `设置已保存到 …`（`saves_before=3` 逐次相同）；
- 每次启动都有一条 `XamlControlsResources 加载失败 (非致命): 灾难性故障 (0x8000FFFF)`；
- 每次启动主窗口前都跑过 DirectML 探测 + **检测模型加载**（`dml_before=1 det_before=1`）；
  识别模型在主窗口之后才加载（后台腿）。
⇒ 三次写盘的**来源没有归因**（可能是迁移/归一化落盘，属正确性而非浪费），本轮只定罪"它在首帧前"，
不定罪"它可以删"。

一次启动的完整时序（21:37:43.882 → 44.615，732ms，逐行时间戳见日志）：

```
43.882 应用启动开始 → 43.897 主题 → 43.905 着色器预热启动 → 43.907 OCR 初始化引擎
43.908-43.908 四枚 native exe 就绪 → 43.909 XamlControlsResources 0x8000FFFF
43.933 cjpegli 就绪 → 43.969 进程优先级 High / DI 开始
44.003 两枚 .cso 已存在 → 44.092 D3D11 设备建成 → 44.101 DI 完成
44.265 DirectML 检测成功          ← 164ms 无任何日志的空档
44.338 检测模型 PP-OCRv6_medium_det.onnx
44.424 设置已加载 → 44.545 设置已保存(1) → 44.573 回显分段 → 44.588 托盘 → 44.597 保存(2) → 44.600 保存(3)
44.615 XAML 初始化 312 / 设置 25 / 尾巴 42 → 进程到主窗口 732
```

### 2.2 编码（本轮实测，4K 8-bit 合成渐变，`Tools` 无参）

| 格式 | 耗时 | 产物 |
|---|---|---|
| PNG 无损 | 126ms | 241.2 KB |
| JPEG LI | 939ms | 265.9 KB |
| WebP | 1,273ms | 170.7 KB |
| AVIF | 3,076ms | 55.7 KB（期间 `CreateInstance OK (SDK 13.1)` ×4 ⇒ 硬件腿反复试过） |
| JPEG XL | 730ms | 101.7 KB |
| JPEG GainMap | 579ms | 386.7 KB |
| 色调映射（LUT 路，4K） | 64.8ms | — |

ISA 实测：`AVX2=True AVX512=False FMA=True AVXVNNI=True VecWidth=32`。

### 2.3 PNG 16-bit 分段（本轮 `--perf-png`，raw 63.3MB）

| 内容 / 档 | 内置 zlib-ng：转换+滤波 / deflate / 总 | libdeflate 自适应臂：/ deflate / 总 | 产物 |
|---|---|---|---|
| ui10 Smallest | 127 / 403 / **473ms** | 240 / 405 / 602ms（探针判 0.012 ⇒ 不外包，正确） | 241,153 B 两臂相同 |
| noise10 Smallest | 498 / 14,671 / **14,889ms** | 329 / 3,126 / **3,937ms** | 43,532,976 → **42,549,000（−2.26%）** |
| noise10 Quick | 498 / 1,560 / 2,281ms | 329 / 1,545 / 2,027ms（不外包） | 42,512,842 B 相同 |
| noise10 Fastest | 498 / 629 / 1,237ms | 329 / 630 / 1,099ms | 55,619,427 B 相同 |

⚠ `转换+滤波` 那一列在两臂之间从 127 跳到 240、从 498 掉到 329 —— **同一份内容同一趟代码**，
所以它测的是"这段的一次性内存/JIT 形状"，不是稳态；只有 deflate 与总耗时两列可按比值读（deflate 与总耗时同向）。
⇒ 要拿"非 deflate 段"记账，必须先把它改成同进程内重复取中位（§6）。
**补测后更正（同日 `artifacts/perfreview_png4k_reps_2026-10-31.log`）**：同参数各跑两轮后
该列稳定在 ±10%（ui10 113/124/118ms、noise10 399/432/404ms）——上面那对 127↔240 是首发射 JIT，
不是仪表坏；非 deflate 段的账可以记，量级 0.1–0.45s/次（4K 10-bit）。

### 2.4 PQ 内核对照（仓库外科目，4K 帧，20 核 Parallel.For）

| 内核 | ms/帧 | 相对 | 10-bit 码字误差（真实帧 / 20M 密扫） |
|---|---|---|---|
| 逐像素精确（2×`MathF.Pow`） | 134.6 | 1.00× | — |
| `L^(1/8)` 三 sqrt + 4096 格插值 | 89.2 | 1.51× | max=1 / max=1，不同码 0.057% |
| `L^(1/4)` 双 sqrt + 4096 格插值 | **58.4** | **2.30×** | max=1 / max=1，不同码 0.062% |
| 判界表 + 二分（按构造应逐码相同） | 153.1 | **0.88×** | 真实帧不同码 24,091、密扫 8,865 ⇒ **判死** |
| 同形状内存下限（PQ 换成一次乘法） | 40.2 | 3.35× | — |

| Y4M 4K 4:4:4 10-bit 落盘 | ms/帧（单线程，最好值） |
|---|---|
| 每样本 2×`WriteByte`（产品现在这样，`NativeAvifEncoder.cs:524-531`） | **289.7** |
| 每行一次 `Write` | **113.4** |

## 3. 发现清单

标注：`【已验】`= 我本轮实读源码或实测；`【单路未复现】`= 只有一路代理报出，未独立命中，**不进结论，只进队列**。

### F1 AVIF 档位漏传 【已验】
见判词 1。四处独立命中同一族：产物腿 `:518`、回退链 6 处、MFT 收了不用、现有源码锁不覆盖。

### F2 无损/写盘的固定搬运 【已验 + 本批已改，读数见 §8】
- ~~`DeflateZlib.cs:120-127`（【单路未复现】）无初值 `MemoryStream` + `ToArray()` ⇒ 大帧多次翻倍重分配 + 第三趟全量拷贝。~~
  ⇒ **形状指控成立、量级由读数改了口径**（`--bench-deflate-cap`，1 MiB/次、level 9、按形状分列）：
  倍增链确实存在（改动前那臂在 1 MiB 全熵料上中位分配 **5.24 MB**，出货那式 **2.89 MB**），
  但"初值估得准就一律更好"**被同一张表否证** —— 低料（实测比例 0.012）上出货式反而比改动前
  **多**分配 238 KB/次；耗时列**判不出**（同路两条臂之差 = 1.4 ms 的噪声下限）。⇒ 这是交换，不是纯收益。
- `AtomicFileWriter.cs:36/43`（【已验：grep 命中，未读上下文】）`File.Create` 未给 bufferSize、无 `SequentialScan`
  ⇒ 本批只给了 1 MiB 写缓冲，**未单独计时**，`SequentialScan` 未做。
- ~~`ManagedPngEncoder.cs:131-133`（【单路未复现】）IDAT 单块 ⇒ 必须物化全部压缩字节再逐字节表法 CRC。~~
  ⇒ **本批已改**（IDAT 按 64 KiB 分块流式 + CRC 随块增量），分配腿读数 **8.0 MB/次保存**（内置路由）；
  改的过程中被新写的裁判抓出一份**产品级回归**（chunk CRC 双重翻转 ⇒ IHDR/IEND 坏，PIL 拒收），见 §8 第四轮。
- `NativeAvifEncoder.cs:515-533` 【已验】单线程全帧循环 + 每样本两次 `WriteByte`；实测每行 Write 省 **176ms/4K 帧**。
- `ManagedPngEncoder.cs:525 ComputeAdler32`（【单路未复现】）全仓零调用（Adler 由 native 算）⇒ 死码。

### F3 全帧缓冲每趟新分配 【部分已验】
- `ColorProfileProvider.cs:1684-1696` 【已验结构】`ConvertFloat16ToSdrBgra` 先 `ConvertScrgbToTarget(...)` 产出整帧 `float[]` 再量化 —— 对照 `FormatEncoders.cs:2223` 已有"融合、无中间 float[] 分配"的写法，同一件事两种形状并存。
- `WgcCaptureService.cs:2371/2375`（【单路未复现】）每个副屏整帧新建 132MB `float[]`，随后**再单独一趟**全帧循环乘 `sdrScale`。
- `JpegGainMapEncoder.cs:138/314/380/481/436`（【单路未复现】）一趟保存最多 5–6 个全帧遍历各物化一次。
- `ToneMapper.cs:350/387/445/491`、`PixelOps.cs:492/556`（【单路未复现】）全帧目标缓冲一律 `new`，无池化。
- `JpegGainMapEncoder.cs:582`（【单路未复现】）`new float[channels]` 在**每个输出像素**内层 ⇒ 4K 约 13 万次小分配。
- `unsafe` 覆盖面（【已验：计数】）全产品 9 个文件用了 `unsafe`，而托管像素量最大的 `ToneMapper`/`JpegGainMapEncoder`/`ManagedPngEncoder`/`ColorProfileProvider` 仍是托管索引器（每次访问一趟边界检查）。

### F4 预览窗逐帧与静置 【全部单路未复现，先补仪表】
`HdrCaptureWindow.cs:4275-4282`（悬停换窗 ⇒ `_atlasScale=-1` + `_annoTextSlotsDirty` ⇒ 整张 2048×1408 图集重排，同文件 `:761/:1786` 注释自记排版 86ms、读回 34–84ms）· `:1274-1287`（Flip 下 `GetBuffer` 指针逐帧交替 ⇒ 每帧 `Dispose`+`CreateRenderTargetView` + 新 RCW）· `:4258-4270`（`_regionRefreshTimer` 每 3s 全量 `DetectWindows` 并在未框选时 `RequestRedraw`）· `:954`（`Thread.Sleep(16−耗时)` 定步长，非 vsync）· `:2364/2372`（每帧每层重跑 `SimplifyPolyline`，无缓存）· `:2037-2039`（每帧 `new float[224]×2 + float[1024]`）· `:619-641`（Initialize 里同步查 `PresentAllowTearing`/`CheckHardwareCompositionSupport`，结果只用于 Info 日志）。
`MainWindow.xaml.cs:2376-2377`（捕获返回与建窗之间 UI 线程同步跑 `DetectDisplayState`（注册表 ACM）+ `GetSdrWhiteLevel`）· `:67/127-133`（编码队列清空 8s 后 `GC.Collect` 阻塞式回收，下一拍要重 commit 132MB LOH）· `PinWindow.cs:207/230-245`（底栏 hover ⇒ `InvalidateRect` 整窗 + `HALFTONE StretchBlt` 整图重采样）。

### F5 捕获与会话 【部分已验 + 一条已有读数】
- SDR 建会话 p50 65ms vs HDR 13ms（§2.1，本轮现取）——**为什么差 5 倍没有解释**，是本轮最容易拿到新读数的一条。
- `WgcCaptureService.cs:778`（【单路未复现】）`EnsureStaging` 只比 w/h ⇒ 选区矩形回读把整屏 staging 拆掉重建，下一帧逐帧回读再重建一次。
- `WgcCaptureService.cs:881/918/1147`（【单路未复现】）每帧 2–3 次整屏 GPU 拷贝；**10-30 审查已把这条记为"未量 ⇒ 不建议直接改"（§5-B）**，本轮维持该判词。
- `WgcCaptureService.cs:529/593/607`（【单路未复现】）冷回读新建 px 后又 `BlockCopy` 进 reuse ⇒ 白付一次整帧搬运。
- `WgcCaptureService.cs:1818-1831`（【单路未复现】）`TryGetLatestFrame` 持 `_poolLock` 整段做回读（含 500ms GPU 锁 + 33MB 拷贝）。
- `WgcCaptureService.cs:514/589`（【单路未复现】）新鲜度判据用 `TickCount64`（毫秒粒度）⇒ 同毫秒两帧被判成同一帧。

### F6 OCR 【已验结构 + 前批读数】
- `OnnxOcrEngine.cs:52` `RecImgMaxW: 1280` 固定，`:770` 单框张量 `[1,3,recH,1280]`、`:818` 合批 `[4,3,recH,1280]` ⇒ **短框也跑满宽度**（结构已验）。前批读数：rec 段 615ms / 端到端 708ms ⇒ rec 占 87%。
- `OnnxOcrEngine.cs:666/939`（【单路未复现】）**写**输入仍走 `input[0,c,y,x]` 多维索引器；10-30 那批的 Span 改动只覆盖了**读**。
- `OnnxOcrEngine.cs:1074/1087/704`（【单路未复现】）检测后处理每次 `new` `probMap`+`visited`，BFS 每前景像素 `new` 一个四元组数组。
- `MultiOcrService.cs:189/202/241`（【单路未复现】）一次最坏 45+20=65s；`s_ocrGate` 排队不设上限 ⇒ 连点线性叠加。
- `QrDecodeService.cs:115-126/162`（【单路未复现】）无码（常态）也付满三趟整图（原/反相/转 90°），外加一次 33MB 转置分配。全仓无 QR 计时臂。

### F7 翻译 【单路未复现 + 前批读数】
`TranslationService.cs:209-223`：每行都从 LLM 起重走整条降级链（`LastBackendServed` 不用于排序），而 Edge 口的 body 本就是数组却逐行发。前批单行实测 1,087ms ⇒ 14 行按并发 4 约 4.4s；若改成"上次命中档置前 + 批量"，纸面 4.4s→1.1s（**未量**）。

### F8 编码固定成本 【结构已验，量级用前批读数】
- 每次保存新建 NVENC 会话（`FormatEncoders.cs:1278/1312/1344/1493/1564/1581/1632/1645` 的 `using var nv = new NvEncoderNative(...)`；D3D 设备有 `s_cachedD3DDevice`，会话没有）⇒ 前批读数 **15–20ms/次**。
- `NvEncoderNative.cs:906-907`（【单路未复现】）上传是 `LockInputBuffer` 后**逐行** `Marshal.Copy`，4K = 2,160 次 P/Invoke；`pitch == w*4` 时可整块一次。
- `NvEncoderNative.cs:1391` `AppendDiag` **确实带 `[Conditional("DEBUG")]`** ⇒ Release 不付磁盘 IO。**跨切面那一路把它报成"每次编码 open+write+close"是假指控，已否证。**
- `NvEncoderNative.cs:802/846/885/894`（【已验：四条 `Console.WriteLine` 不在 `#if DEBUG` 内】）GUI 无控制台时近乎零成本，但**产物被重定向时是同步落盘**。
- `EncoderCrashSentinel.cs:89-193`（【单路未复现】）每次原生编码在进程级 `s_lock` 内做 3 次磁盘往返 ⇒ 加固定成本且让并发保存排队。
- 原生 CLI 每张新起进程（`NativeAvifEncoder.cs:300`/`NativeJxlEncoder.cs:431`/`NativeWebPEncoder.cs:90`/`JpegLiNative.cs:167`）——**"预热子进程"已实测判无效**，这条只剩"能不能并行提交"与"能不能长驻"两种形状，均属 10-30 已判不做的"固定成本"族，**不重开**。
- `JpegGainMapEncoder.cs:258→282`（【单路未复现】）base 与增益图两次 `cjpegli` **串行**且互不依赖。

### F9 TIFF 【已验：零并行】
`ManagedTiffEncoder.cs` 全文 `grep "Parallel."` **0 命中**（本轮实测）⇒ `ConvertToRgba`（`:180/:225`）逐像素串行；单 strip ⇒ deflate 也没有并行入口。**全尺寸 TIFF 耗时零读数**，所以这条只有"形状"没有"量级"。

### F10 启动 【部分已验】
- `App.xaml.cs:189` `MultiOcrService.Initialize(...)` 排在 `InitializeComponent`（`:218`）之前；日志侧 44.101→44.265 有 164ms 空档后才是 DirectML 结论（§2.1）。**是否阻塞首帧需要读那条腿的 Task 形状，本轮只证"它在主窗口之前发生"。**
- `ShaderCompiler.cs:61-66` 【已验】`EnsureCompiled` **先** `FindFxc()`（`:126-156`：起 `where fxc.exe` 子进程 `WaitForExit(2000)`，失败再 LINQ 枚举 `Windows Kits\10\bin` 各版本目录），**后**才 `File.Exists(cso)`；而启动日志证明 .cso 在绝大多数机器上"已存在" ⇒ 每次启动白花一次进程创建或一轮目录枚举（本机事实：冷启动一次进程 ~200ms，紧接第二次 13–20ms）。
- `LogService.cs:339-340 → :371` 【已验】Scrub 在门控前；`:271` `AutoFlush=true` + `s_lock`。
- `SettingsService.cs:163-172`（【单路未复现】）同一份 settings.json 启动被读/解析 4 次（主题一次、反序列化一次、两趟迁移各一次 `JsonDocument.Parse`），全在 UI 线程。
- `App.xaml.cs:79` 与 `AppServices.cs:47`（【单路未复现】）同一进程 `InitializeFileLog()` 跑两次，第二次 `Shutdown()` 后重开。
- 包体：模型 68MB + native 23MB + `Microsoft.Windows.SDK.NET.dll` 24.9 + onnxruntime 22.0 + DirectML 17.8 ⇒ **OCR 全家桶约 109MB 对不用识字的用户是纯负担**；`-Trim` 实测 151.4→114.4MB 但强制关 R2R；`Publish.ps1:439` R2R 默认开（`TrueToneCap.r2r.dll` 124.1MB 是绿色包最大单件），AOT 从未真开（`:455` 的 `CorePublishAot` 零消费者）。

## 4. 本轮否证 / 撤销的指控

| 指控 | 结论 | 依据 |
|---|---|---|
| "每次 NVENC 编码付 `AppendDiag` 三次磁盘往返" | **假**（Release 不编译进） | `NvEncoderNative.cs:1391` `[Conditional("DEBUG")]` |
| "PQ 逐像素 pow 估 150–400ms/保存" | **打回为 134.6ms/帧（20 核实测）**，且判界表方案不可用 | §2.4 |
| "常驻 WGC 池能省截图延迟" | 维持 10-30 判死 | `project-capture-latency-budget` 读数：常驻帧旧 250–500ms 只换约 100ms |
| "GPU 色调映射激活是远期最大收益" | 维持作废（资产 10-01 已删） | `docs/performance-analysis.md:102-104` |

## 5. 不重开清单（前批已判，本轮维持）

常驻池 / 池内按需刷新（B） / 每帧新分配去克隆 / 启动预热子进程 / AVIF 文件中转→管道、JPEG LI 读回、
"打每次保存的固定成本"整批 / 给 OCR 合批补仪表追收益 / 换无损压缩库（libdeflate 已落地）/
"源精度按显示器能力"三档策略 / 8-bit 从 HDR 帧下变换（改像素语义，须维护者拍）。

## 6. 仪表缺口（建议落地前必须先补的）

> 本节写的是**开工前**的缺口；同日批4 已把 1/2/3/5 四格补成可复跑仪表（见 §8 批4），
> 4（UI 开窗 p95）与 6（录制）仍缺。

1. **端到端"热键 → 落盘"没有一台把 捕获/预览/回读/编码/写盘 相加的仪表**；今天靠散落日志行拼。
2. **色彩/ICC 转换零计时**：`--icc-check/--png-check/--fmt-check/--cicp-sweep/--ramp-sweep/--param-sweep` 全是结构判据不计时。
3. **标注栅格化零计时**（`AnnotationRasterizer.cs`），"保存链叠加标注后多慢"未量。
4. **UI 开窗无离线 bench、无 p95**（只有 app 日志的逐次行）。
5. **TIFF 全尺寸零读数**；`--selftest` 只有 96×96 合成。
6. **录制/GIF 零计时**；OCR 只量过 1200×800 合成，无 4K 真材。
7. `转换+滤波` 段在两臂间 127↔240、498↔329 跳（§2.3）⇒ 该段要么重复取中位、要么换进程内 A/B。

## 8. 本批落地（同日第二轮，维护者点头批1–批4）

构建：`--no-incremental` **0 警告 / 0 错误**（`artifacts/perfreview_build_final_2026-10-31.log`）。
套件（本轮改动涉及的每一张都现跑）：unit **1805/0/2**、format 43/0、color 270/0、ocr 12/0、
usability 225/0、encoding 14/0、qr 47/0、service 2/0/1。基线 `gate-baseline.json` 手改
2390 → **2418**（unit 1777→1805，`_说明` 里留了理由；其余七格与旧基线逐字相同）。

### 批1 AVIF 档位收口
接线：`FormatEncoders.cs:525` 成品腿、6 处 `FallbackToLibAom`、`:1526` 真 10-bit→8-bit 降级转交
（第 7 处同族漏传，是本轮新发现的：同一次编码里"10-bit 按档位、退 8-bit 就换档"）。
MFT/QSV 腿**判"无落点"而不是造一档**：`MftEncoderNative` 全文没有 effort/preset/quality 形参，
也没有档位类 CODECAPI ⇒ 新增 `MftEffortSupport`（`HardwareLegAcceptsEffort` 恒 false + 每进程一条 Warn），
界面按这份判据禁用 `EffortAvifCbo` 并点名，
`ZhToEn` 同批补了那句（键含前导空格，逐字对齐）。
⚠ 提交前清账（2026-10-31 本批）：同批登记的逐张状态位 `HardwareLegIgnoredEffort` **已删** ——
它只有生产者、没有读者（全仓唯二读者是数它那两行赋值的源码断言），而它的文档承诺"界面每次编码后读一次"
并没有兑现：界面走的是能力轴 `AvifEncoderCapabilities.EffortApplies`。留着就是"一句没人兑的承诺"，
且与 `EffortApplies` 构成同一事实写两处。事后通道收敛为**唯一一条**：`NoteHardwareLegIgnored` 那条 Warn。
配套：`CorePipelineTests.cs:4797-4800` 那条源码断言的第三个合取项
（数 `MftEffortSupport.HardwareLegIgnoredEffort = false;` == 1）随字段一起删，否则 ④ 必红。
**行为读数**（`artifacts/perfreview_avif_effort_2026-10-31.log`，1024×640，Auto 解析）：
Smallest 2354ms/979 B、Balanced 80ms/2,131 B、Fastest 61ms/2,648 B —— 三档产物互不相同，
时间跨度 39×。⚠ "改前三档全同"这一格是**源码论证**（接口默认参 `Balanced`），本轮没有拿旧 exe 复现，
所以它不是读数；新锁 ② 是行为级正控，抽掉实参会当场红（见下方变异 M1）。

### 批2 内核与搬运形状
**PQ 查表**（新增 `Core/Processing/PqTransfer.cs`，4096 格、按 s=L^(1/4) 分段 + 线性插值，
`maxValue>4095` 自动回原式；开关 `TTC_PQ_TABLE=0`）：
- 并行路径（我本轮仓库外科目，`artifacts/perfreview_pqlut3_2026-10-31.log`）：134.6 → **58.4ms/帧**（2.30×），内存下限 40.2ms；
- 单线程路径（仓内 `--bench-pq-table`）：1387 → **141ms/帧**（**9.82×**）；
- 精度：确定性逐格内点扫描 2,052,096 点，10-bit 与 12-bit **最大 1 码字**，不同码占比 0.0014% / 0.0063%；
  弱表正控（64 格）越界到 296 码字 ⇒ 判据有牙。`FormatHelper.LinearToPQ` 降级为别名，PQ 算式收成一份。

**TIFF 分条**（`ManagedTiffEncoder`：~1 MiB/条、行并行转 RGBA、条间并行 deflate、整帧判一次后端）
—— 全尺寸 TIFF **第一次**有读数（`artifacts/perfreview_bench_tiff_pq_2026-10-31.log`）：

| 4K 3840×2160 | convert（仪表） | deflate（**串行逐条**口径） | 端到端 Encode（产品路径） | 产物 |
|---|---|---|---|---|
| 8-bit Smallest | 27ms | 465ms | **63ms** | 2,680,144 B |
| 8-bit Fastest | 27ms | 106ms | 32ms | 12,727,274 B |
| 16-bit Smallest | 13ms | 2,241ms | **393ms** | 3,847,139 B |
| 16-bit 其它 | — | 1,001 / 166ms | 208 / 69ms | 3,847,145 / 15,681,211 B |

⚠ 两条诚实限定：① 我没有"改前"的同机读数（旧二进制被覆盖），所以只能给"现在端到端多快 +
串行口径多慢"，**不写"省了多少"**；② `deflate` 那列是**串行且逐条判后端**的口径，与产品的
"整帧判一次 + 条间并行"不同形，差值列（assemble+write）为负时改成写 `-1`，不许留 0 被读成"写盘不要钱"。

**外部裁判（多 strip 改了容器，判据必须是别人的解码器）**：
- Pillow/libtiff 解 4K 8-bit：`max|Δ|=0`（33,177,600 样本），正控"错通道序"=255、"错一行"=254 ⇒ 判别成立；
- ffmpeg 解 4K 16-bit：`pix_fmt=rgba64le`、66,355,200 B，`max|Δ|=0`，正控"不换序"/"错一行"=65,278；
- ~~⚠ 但我的端序对照不判别~~ ⇒ **同日已补成会判的臂**（`tests/manual/safety/tiff16-endianness-referee.ps1`
  + 同名 `.py`，18 条臂，主循环亲手跑过：rc=0）。**旧臂为什么不判别，根因两条都不是产品的事**：
  ① `--bench-tiff` 的 16-bit 源是 `ExpandTo16BE = v*257` 造的（`SavePathBench.cs:116-128`）⇒ 每个样本
  都是 `0xNNNN` **回文**（实测回文占比 = 1.0000），LE↔BE 是恒等映射，这块料上任何裁判都红不了；
  ② `np.asarray(PIL.Image.open(tif))` 对 RGBA;16 **下采样成 uint8** ⇒ 那条"外部解码"腿本身也在量别的东西。
  新格换了料（渐变 16-bit，非回文）并配两端对打印：字节交换后"渐变单调"违反数 **0 → 215,040**（必须红 ✓）、
  对 PNG-16 跨容器锚 max|Δ| **63（1 个码步）→ 60,685**（必须红 ✓）；`v<<8` 冒充 `v*257`、错通道序、
  错一行三条正控各 255 / 65,278 / 65,278；阴性对照 = 同一脚本跑 8-bit 产品件全 0。
  ⚠ **Y5「两个外部解码器一致」不许当端序证据**：tifffile 与 ffmpeg 都读同一个 `II` 头，
  这对臂只能证"两家一致"；真假阳的形态（头写 `II`、样本按 BE 存）由 Y1–Y4/Y6 咬住
  （造了那个 mutant，格退 1）。RowsPerStrip 被外部读成 68（8-bit）/34（16-bit）与规划一致。
- ⚠ **顺手量到一个既有的容器缺陷（不是本批引入，HEAD 就在那里）**：16-bit 那条路写
  `SamplesPerPixel=4`（RGBA）但**不发 ExtraSamples(338)** —— `ManagedTiffEncoder.cs:83` 的
  `baseTagCount = 12` 在 `git show HEAD:` 里就是 12，本批没动过标签数。第 4 个样本的语义因此是
  让读方猜（libtiff/tifffile/Pillow 都默认当 alpha 收了，所以今天的裁判读着对）；
  按 TIFF 口径 Photometric=2 + SamplesPerPixel=4 应当带 338=1(assumed alpha) 或 =2(unspecified) 之一。
  **本批没修**：改标签数会动 FormatIndex 之外的容器契约，属另一批的事。

**其它**：`DeflateZlib.CompressBuiltin` 初值容量 + 容量恰等时 `GetBuffer()` 零拷贝；`AtomicFileWriter` 1 MiB 写缓冲。
⚠ **这一格原先写的"按上次输出比 EMA"是 Lane 的中间形状，不是出货形状**（裁定 1 的"改文字不删声明"就地生效）：
出货的是**只看这一次输入长度**的静态外推（`input/4 + 1024`，钳 [64 KiB, 硬膨胀上界]），
静态 EMA 已在提交前清账时删除，理由与读数见下面「deflate 初值 A/B」那一节。
⚠ **Y4M 每行写**（实测 289.7→113.4ms）落在 `NativeAvifEncoder.EncodeHdrFromScrgb` 上，
而它**零调用方** ⇒ 这项 176ms **不构成收益**。
**处置（提交前清账，2026-10-31 本批）：已删**。调用方复核覆盖 `src` / `tests` / `scripts` / `docs` / `artifacts`
全部非 `.git` 文件（含 `--binary-files=without-match`，防二进制里藏字面量），`EncodeHdrFromScrgb` 命中 8 处：
1 处是定义本身（`NativeAvifEncoder.cs:505`），其余 7 处全是文档/注释（`.workbuddy-ai` 1、`docs` 4、
`ManagedPngEncoder.cs:649` 注释 1、本审查 1 的引用形态）⇒ **真实调用 0**。反射通路也查过：
`FormatBench.cs` / `SavePathBench.cs` 与全部 `*Bench.cs` 的 `GetMethod`/`NativeLibrary.GetExport` 目标只有
`OnnxOcrEngine.Run{Detection,Recognition}`、`NvencAvifBackend.TryEncodeNvencHdrAvif`、
`ManagedPngEncoder.BuildRawScanlines16`/`CompressDeflate`、`ManagedTiffEncoder.ConvertToRgba` ——
没有一个指向 `NativeAvifEncoder`；`TrimmerRoots.xml` 只保 Vortice/SharpGen，也不涉及本类。
随入口一并删掉的是它两个只被它用的私有助手 `FloatToPq10`、`LinearToPq`（删后本类内引用归零），
于是那 176ms 的"外科目"连同它服务的死路一起退场 —— 这正是"零调用方的赢不算赢"的收尾。
⚠ 同批必须一起处理的锁：`CorePipelineTests.cs:5010-5013` 的 ⑧ 条数 `fs.Write(lines[y]` == 1，
入口删掉后该计数为 0 ⇒ ⑧ 必红；⑧ 的 ②/③/⑦ 与 TIFF/deflate 各条不受影响。
`ManagedPngEncoder.cs:649` 那句"与 backlog 第 5 条同一类问题"的指涉也已失效，一并改掉。

### 批3 启动关键路径
- `ShaderCompiler.EnsureCompiled`：`.cso` 存在性检查前置到 `FindFxc()`（起 `where fxc.exe` 子进程 /
  枚举 Windows Kits）**之前**。Lane 从 10-30 真机日志量到 `预热启动→cso 已存在` 这段是
  **141–351ms（n=10）**，且日志证明 .cso 在每次启动都"已存在" ⇒ 这段在 .cso 齐全的机器上归零。
  **改后实测（2026-10-31 11:0x，12 次真机会话全取到）**：`着色器预热启动→着色器预热完成`
  = **1–2ms**。口径边界：端点不完全同一条（旧数取"到第一枚 cso 已存在"，新数取"到全部完成"，
  后者是前者的上界包络），且"旧"量在 10-30 的 exe 上、不是同一件二进制的 A/B；
  `.cso` 今天仍然齐全这一条有日志行本身作证据，所以这段的差不是"没走到"造成的。
- **主窗口前 3 次设置写盘已逐次归因**：全部出自 `MainWindow.RebuildFormatCombo()` → `OnFormatChanged`
  （`_uiReady` 那时已是 true）。其中**第 2 次把 `FormatIndex=-1` 写进盘**——那是换 `ItemsSource`
  把 SelectedIndex 打回 -1 触发的，属正确性缺陷而不是性能问题；根因在 `RebuildFormatCombo`，
  本批没越界去修它，只在 `SettingsService` 出口加了"逐字节相同 ⇒ 跳过文件 IO"
  （`File.Exists` 前置 + 只跳 IO **不跳 SaveSecret**，凭据是 `[JsonIgnore]` 不在这份 JSON 里）。
- `LogService`：只把 **detail** 的 Scrub 移进门内；**msg 的 Scrub 留在门外**——同一条 msg 还进
  UI 环形缓冲与 `OnLogEntry`，移到门后等于把未脱敏原文送进日志面板。
  **批量化 flush 判不做**：`AutoFlush` 是崩溃前把行交给 OS 的唯一保证，而 `--window-probe`/`selftest`
  直接 `Environment.Exit` 不走 `Shutdown` ⇒ 缓冲会整段丢掉 `[1.96/2]` 与 `logline_format_check.ps1`
  赖以判定的行。
- `MultiOcrService`：DirectML 会话改**默认按需**（`TTC_OCR_EAGER_GPU=1` 回旧行为），补建点
  `EnsureGpuEngine` 插在 `SelectedEngine`，降级顺序 GPU→CPU→Windows 不变；顺带修了
  `_modelDir` 取空串会让懒建**永久失败**的形状（同 09-15 那族）；"取引擎"那步挪进 `Task.Run`
  （否则 0.8–1.2s 的会话加载会内联跑在 UI 线程 = 首次识字冻窗）。
  **A/B 已做（见下面「批3 的实机 A/B」）**：`进程到主窗口` 两臂中位差 **−91.5ms**（n=6/6，交替跑），
  但这笔钱不是每次都省到——会话构建本来就在后台，会跟关键路径重叠。代价照实写：
  首次识字多付一次会话加载（同一段 673–1020ms）。

### 批3 的实机 A/B（2026-10-31 11:0x，最终构建件，同一台机同一份设置）
仪表：`tests/manual/safety/startup-ab-ocr-eager-cell.ps1`（新）。两臂 = 默认（DirectML 会话按需建）
vs `TTC_OCR_EAGER_GPU=1`（启动即建）。被测量取产品自述的「启动: 进程到主窗口 Nms」，**A/B 交替跑**
（A B A B…），不是先跑完一臂再跑另一臂。臂身份每次从日志里读出来（A 必须有「按需构建」行、
B 必须有「引擎就绪」行），6+6 次全部 ✓ ⇒ 环境变量真的生效、两臂不是同一个世界。

| 批次 | n | A 懒建 各次(ms) | A 中位 | B 启动即建 各次(ms) | B 中位 | 两臂区间 |
|---|---|---|---|---|---|---|
| 第1批 | 3/3 | 844, 927, 943 | 927 | 1070, 930, 1074 | 1070 | **重叠**（B2=930 < A3=943） |
| 第2批 | 6/6 | 823, 829, 840, 825, 802, 883 | **827** | 1102, 828, 887, 886, 964, 950 | **918.5** | 基本分开（仅 B2=828 落进 A 区） |

- **判词：省 ~90ms，不是省 ~700–1000ms。** 第2批 Mann-Whitney U=33/36（单侧 p≈0.01），均值差 102.5ms；
  第1批 n=3 单独不成立（区间重叠），只作为同向的第二批证据。**两批不许合并**：A 臂自己的中位从
  927 漂到 827（100ms），比要量的效应还大 ⇒ 合并只会把漂移当成收益。
- **为什么 700ms 的构建只换成 90ms**：那枚会话建在后台，与建窗口的活重叠。有一轮直接看到边界——
  11:06:13.586「初始化引擎」→ 11:06:14.154 主窗口（964ms）→ 11:06:14.**269**「DirectML GPU 引擎就绪」，
  就绪比窗口晚 115ms ⇒ 那一轮的构建几乎全在关键路径之外。
- **挪走的成本没有消失**：同一台机上 B 臂 `初始化引擎→引擎就绪` = **673 / 675 / 687 / 765 / 855ms**
  （第2批 5 次可取，第1批 751 / 890 / 1020ms），A 臂这段在启动里为 0；这同一段就是**首次点「识字」
  要多等的量级**（会话建好后常驻，一次性的）。取舍归产品，不由这段读数替你决定。

### 格式往返闭环 + 仪器修正（同日第三轮，`FormatIndex=-1` 修复之后）
- **读侧 + 写侧各一个独立读数**（`w1-format-persist-cell.ps1`，真机真 exe）：盘上按字节写
  `FormatIndex=5(TIFF)` → 起进程 → UIA 只读界面 → 停进程 → 再看盘。实测：
  **界面 ListItem `[5] 「TIFF」 选中=True`（共 1 项被选中）**，且**盘上仍是 5**，
  本会话「设置已保存」**0 条**（修复前同一格是 3 条 + 键被覆写成 PNG）。设置按原字节还原，sha 逐字相同。
- ⚠ 「键还在」这一条**单独看不算过**：闩锁忘了放会造成一模一样的读数（写侧整个断）。
  所以补了正控 `w1-format-persist-positive-control.ps1`：真实点选第 3 项（UIA `Select`）⇒
  盘上键变 `"FormatIndex":3` 且「设置已保存」**恰好 1 条** ⇒ 写侧活着。两条合起来才叫往返闭合。
- 判据从两格改成三格（同一处改动）：旧版只有"键在不在"一个读数，"键还在"同时兼容
  "界面显示对了"与"界面根本没反映"两种世界，而后者正是它要抓的缺陷形态。解析逻辑单独喂了
  四种输入验它会分：实档→`5` / 只有 `选中=False`→`$null` / 明写没选中→`-1` / 合成第 2 项选中→`2`。
- **仪器自己坏过一次（已修，不是产品的事）**：`w1-probe-uia.ps1` 在 `ShowWindow(SW_RESTORE)` 之后
  拿 `[int]` 转 UIA 的 `BoundingRectangle`，而托盘隐藏态那个窗给的是 **±∞** ⇒ 探针抛
  `Cannot convert value "∞" to Int32` 直接死掉，第一次那一格**整格没判**（日志里连选中项行都没有）。
  修法 = `Fmt-Rect`：非有限值原样印成「不可得(∞/NaN)」，读数留在日志里而不是把仪器炸掉；
  不用 `[double]::IsFinite`（PS 5.1 宿主没有这个方法）。

### 批4 仪表（`src/TrueToneCap.Tools/SavePathBench.cs`，815 行 + 5 行分派）
`--bench-tiff` / `--bench-color` / `--bench-savepath` / `--bench-pq-table` / `--bench-avif-effort`，
结论走 **ExitCode + CSV** 落 `artifacts/bench/`；`cells==0` 一律退 1；缺外部编码器退 3=SKIP；
每台自带正控（粗表必须越界、effort 两档产物必须不同、托管档失败算红、全同判 `non-discriminating`）。

### 变异正控（本批）
| 变异 | 结果 | 工件 |
|---|---|---|
| M1 抽掉 `:525` 的 `effort: s.Effort` | **1804/1**，红的正是新锁② | `artifacts/mutation_avif8bit_effort_2026-10-31.log` |
| M4 表段数 4096→64 | **1803/2**，红在新锁①②（10 与 12-bit 两格，`max10=2 / max12=6`） | `artifacts/mutation_pq_segments64_2026-10-31.log` |
| M5 注释掉 `RebuildFormatCombo` 末尾的 `CheckFormatComboContract();`（本批判据重构那一轮） | **1807/1**，红的只有被重构的那一条 | 现场跑；还原后同一命令 **1808/0/2** |

还原后 `--no-incremental` 重编再取绿（1805/0/2）；`grep -r "MUT-" src` = 0 命中。
本批最终件现取：`--unit-tests` **1808/0/2**、`--ocr-tests` **12/0**；基线手抬 2418→**2421**
（`scripts/gate-baseline.json` 的 `_说明` 记了逐格出处：+3 全在 unit = 重建期中间态不落盘、闩锁必须在 finally、「方法体解析得到」那条失明守卫）。


### 裁定 1–4 的落字与补测（同日第四轮，维护者四条裁定之后）

四条裁定的落点：**①** claim 不删，改标"实测/推算" ⇒ 已就地标在判词 2 的"口径标注"、§3-F2 前三条、
上面「批2 其它」那一格；**②** 静态 EMA 不恢复，由读数判死 ⇒ 下面「deflate 初值 A/B」；
**③** 缺的测试必须补，正控口径 = "字节改动必须让解压或像素校验变红" ⇒ 下面「新判据与它红的样子」；
**④** 四批全保留、不回退，提交前跑门禁并把结果给维护者 ⇒ 本节末「门禁」+ 单独一份实机 UIA 复跑。

**deflate 初值 A/B（裁定 2 要的那条臂；新台 `--bench-deflate-cap`，六臂只差 `new MemoryStream(cap)` 的 cap）**
夹具先自检实测压缩比（摊不开三档就直接红）：**ui 0.0120 / photo 0.3619 / noise 1.0003**
⇒ "任何单一常数都会在一端错"这句话第一次有支撑料，不是修辞。
- **容量不进码流（正控）**：E0 = 两形状 × 5 个显式初值（含故意给的 **8 B** 与硬膨胀上界 147,968），
  E1 = 三形状 × 六臂 ⇒ **产物逐字节相同**（这条不成立整台退 1，因为那时比的已不是成本差）。
  同时 E1 里 `product` 臂（真调 `DeflateZlib.CompressBuiltin`）与 `est` 臂（仪表复刻）逐字节相同
  ⇒ 仪表量的就是产品那条路，不是另一份实现。
- **分配按形状分列**（`head` = 改动前：无初值 + 末尾 `ToArray()`；`est` = 出货：只看本次输入长度的 1/4；
  `ema` = 仪表里模拟的历史型（α=1/4，种子 0.25）；`oracle` = 把第一帧的产物长度当永久初值 = 历史型能做到的最好）：

  | 1 MiB/次、level 9、中位字节 | ui(0.012) | photo(0.362) | noise(1.000) | 整段 12 次总分配 |
  |---|---|---|---|---|
  | head（改动前） | **37,824** | 1,420,400 | 5,235,584 | 20,081,424 |
  | est（**出货**） | 276,096 | **1,169,400** | **2,891,504** | **13,010,904** |
  | ema（历史型） | 534,040 | 797,176 | 3,772,456 | 15,656,008 |
  | oracle（记住第一帧） | 12,872 | 1,170,432 | 4,248,912 | 16,296,648 |

  ⚠ 表里 `ema` 那一行的 ui 格比 E1 单形状（164,976）差得多，正是它自己的病：交替序列里它的中位初值被
  后一帧的 noise 拖到 **521,142 B** ⇒ 删除理由里那句"串值"现在有数了。
- **EMA 由读数判死**：交替整段它比出货式**多**分配 **2,645,104 B**，而这台自己的噪声下限
  （`est` vs `product` 两条同路臂之差）只有 **8,064 B** ⇒ 差 **328 倍**，不是打平。
  它只在**连续同内容**时赢（E1 单形状 ui：164,976 < 276,096）⇒ 结论：**不恢复历史型初值**；
  要恢复得先有新的读数形状，不是新的理由。（工件：`artifacts/bench/bench_deflate_cap_2026-10-31_150444.csv`）
- **反过来，静态 1/4 也不是纯收益**：低熵内容上它比改动前**多**分配 238,272 B/次，高熵上**少** 2,344,080 B/次。
  **耗时列判不出** —— 同一条代码路径的两臂最大形状差就有 **1,442 µs**，表里所有耗时差都在它之下 ⇒
  这台只判分配，时间一律读成打平。⚠ 遗留候选（**未量、本批不动**）：把分流探针已经算出的 level-1 比值
  （同一帧、无历史，不违背确定性）喂给初值，低熵那端就不必付那 238 KB；值多少毫秒没量。

**其余三台的读数（本批新加的仪表，全部主循环亲手跑）**
- `--bench-png-idat`（4K）：**流式 vs 旧"整帧物化再写一条 IDAT"** 两臂码流逐字节相同，内置路由
  **每次保存少分配 8.0 MB**（Quick 档 43,015,656 → 34,639,600；Smallest 档同量级）⇒ 判词 2 里那句
  "搬运腿从没量过"就地改成实测；⚠ 只量到**这一条腿**，整条保存链的内存账仍是**推算**。
- `--bench-hdr-routing`（4K 10-bit 合成高高光 PQ 夹具）：内置 3,452ms/1,371,480 B vs libdeflate
  5,396ms/1,389,757 B，自动腿走了 libdeflate（5,464ms）⇒ **在这类料上分流选了一条更慢且更大的臂**，
  与 2250×1350 真 HDR 样张上量到的 11.78→6.01s 相反。⚠ 夹具是合成的（CSV 的 `fixture` 列钉着
  "不是真屏幕截图"），所以这条**不判死分流**，判的是"阈值 0.05 的适用范围没覆盖 4K 尺寸这一格"——
  要下结论得拿真 4K HDR 截图重跑。
- `--bench-ocr-rec`：rec 张量宽度按批内真实最大宽收窄，10 批两臂**识别文本逐批相同**，
  合计 749ms → 501ms ⇒ **1.495×**（判别批 ≥1.32×，两条同形批收敛 ≤3.8%）；
  ⚠ "文本相同"这一条在产品里靠 `DecodeRecognitionRow` 那句 `.Where(d => d-1 < _dict.Length)` 兜住
  （模型第 18710 类 = 尾随特殊类），那道界一旦动，收窄会把尾类暴露成文本差。
- `--bench-translate`：逐行 ×14 = 959ms/14 次往返 vs 批量 1×14 = 304ms/1 次 ⇒ **3.15×**，
  是真实墙钟读数，不是"请求数换算"；两条正控（N=1 收敛、标尺会动 3.72×）都过。

**F4 预览窗静置（批4 的仪表第一次落到真窗，`tests/output/f45-meters-cell.log`）**
静置 **47.02s** 内渲染帧数 **21** ⇒ 平摊 **0.447 帧/s**（若真是"每 3s 醒一次"应 ≈0.33 帧/s：
**量级对得上，但 21 帧不是 15.7 帧**，多出的部分没有解释）；同一区间进程 CPU 增量 1,328.1ms ÷ 16 次节拍
⇒ **≈83 ms/拍**，⚠ 这一列**口径 = 整进程**（含 WGC 回调线程与 UI 线程），标的是**估算**，
不等于 RenderCore 单帧。⇒ F4 从"全部单路未复现"降到"唤醒确实存在，但 3s 那一次值多少 RenderCore 毫秒
仍没分出来"；`_poolLock` 持锁账目同轮现取（15s 空闲回收窗口内）：建 SDR 会话 n=1 合计 97.72ms、
建 Float16 会话 15.24ms、空闲回收 9.02ms、交还共享槽 ≈0 ⇒ **持锁时长不是这一轮的主要成本**（旧指控
"持锁整段做 33MB 回读"在这段窗口里没出现）。

**新判据与它红的样子（裁定 3：不许只靠自洽）**
断言数变化全部现取（门禁同一轮）：**encoding 14→23（+9）**、**format 43→52（+9）**、**unit 1808→1807（−1）**，
总基线 **2421→2438**。逐条指名，不写"若干条"：
| # | 新判据 | 字节改动必须让它红的方式 | 本批实证的变异/正控 |
|---|---|---|---|
| 1 | 多 IDAT 切分边界 = ⌈码流/64 KiB⌉ | 改任一块边界 ⇒ 条数与载荷界不符 | 仪表 ②b 已含（`--bench-png-idat` 退 1） |
| 2 | 逐块 CRC 用**独立算式**重算（不用产品的表法） | 改任一字节 ⇒ 该块 CRC 不等 | **翻字节正控**跑在套件里（encoding 第 9 条那格） |
| 3 | 拼接后 inflate 长度 == 输入长度 | 破坏码流 ⇒ 长度不等/解压红 | 同上 |
| 4 | chunk CRC（IHDR/IDAT/IEND）外部 zlib 复核 | 双重翻转 CRC ⇒ PIL 拒收 | **本批判出的真回归**：新写的这条抓到 `WriteChunk` 把 CRC 翻转两次 ⇒ IHDR/IEND 坏、**PIL 拒绝产品的 PNG**；修后外部裁判 11/11 臂绿、PIL 打开像素 Δ=0/307,200 |
| 5–7 | ExtraSamples(338) 契约：`type=SHORT && count=1 && value=2`（值 2=Unassociated，因为本编码器交出的 alpha 未预乘，写 1=assumed 是在撒谎） | 见 M6/M7 两趟变异（下面两行），**都实测过** | `FormatStructureTests.AssertExtraSamplesContract`（TIFF8/TIFF16/TIFF-ICC 各一次调用） |
| 8–9 | 同一次调用里另两条：标签**总数**钉死（13/13/14）+ IFD 条目按 tag **升序** | 插标签改序 ⇒ 升序那条红；漏改 `baseTagCount` ⇒ 总数那条红 | 同上 ⇒ 一次调用 3 条 × 3 张料 = **format 的 +9 全部来源**，不是另加九件事 |
| M6 变异 | **只**注释掉 `WriteShortEntry(writer, 338, 2)` 一行 | ⇒ **6 格红**，但红的是**产品自己的落盘守卫**：`TIFF 值区布局与写入顺序不一致: 实际=182 预期=194（拒绝写出坏件）` ⇒ 顺带证了一件事：**标签数与实际写入不一致时产品不静默产坏件**（`artifacts/mutation_338_missing.log`） |
| M7 变异 | 把 338 **整套**摘掉（同一行注释 + `baseTagCount 13→12`，这才是"以后有人回退这件事"的真实形状） | ⇒ **46 过 / 6 败**，红的正好是新契约那 6 条（338 在场 ×3 + 标签数 ×3），**升序那条不红**（没动序 ⇒ 判据没有过度咬合）（`artifacts/mutation_338_both.log`） |
| 还原 | 两行都改回去、重建、同一条命令 | ⇒ **52 过 / 0 败**（`artifacts/restore_338_format.log`）——变异闭环按"红→还原→绿"走完，不是只跑红的一半 |
| 10 | 真 16-bit **非回文**大端料（先证前提） | 端序换向 ⇒ 渐变单调违反数 0→215,040 | 前提断言 = 回文占比 1.0000 的旧料被**判成不判别**后换料；`tiff16-endianness-referee` 18 臂 rc=0 |
| 11 | 18710 尾类越界界（`_dict.Length` 过滤） | 动那道界 ⇒ 收窄臂的文本相同列变红 | 上面 `--bench-ocr-rec` 的 ⚠ 那句 |
| 12 | UIA `IsEnabled=false`（进程外读数） | 只禁用不渲染 ⇒ C2 红 | C1 正控先证仪器看得见 true；两条仪器假红见下 |
⇒ 表里 12 条是"会分的判据"全集，**不再有"9 条会分"这种没有名字的数**。
unit 那 **−1** 是被判对象同退场：原 ⑧ 数的是 `NativeAvifEncoder.EncodeHdrFromScrgb` 里的每行写次数，
而该入口全仓零调用方、本批连同两个私有助手删除 ⇒ 留着就是"给死路上锁"当覆盖率。

**界面禁用那一格：进程外 UIA 复跑（与提交版脚本严格同一份）**
`tests/manual/safety/effort-combo-disabled-uia-cell.ps1` 对 15:10 那件构建复跑 ⇒ **RC=0 / Fail=0 / Undecided=0**：
C1 正控（Auto 档 `IsEnabled=True`，先证仪器看得见 true）、C2 判据（选到「Intel QSV / 系统 MFT（本机不可用）」后
`IsEnabled=False`）、C4（提示里那句"⚠ 该后端没有压缩强度入口（系统 MFT 无此旋钮），上面的下拉已禁用"渲染出来）
三格全部成立；C3 明确标了**不作判据**（禁用态下 `Expand` 抛 "Unrecognized error." 与 ScrollIntoView 后仍 false
只记账）。收尾 HDR 摆回开、两个下拉还原、**盘上设置按字节还原 = 起手值**（`sha256=74bed98d…357d`）。
⚠ 两条仪器账：① 本会话第一次复跑（15:14，`-SettleSec` 用默认 12）报的是 **未判**——"本 PID 下枚举到 0 个顶层窗"，
把它叫停成 RC=2 而不是读成通过；等 20s 重跑才拿到绿。⇒ **未判与不合格是两种状态**，这一格的区别在于
仪器没看见窗，不是界面没渲染。② 之前 14:26 那份绿日志出自**改动前 21 秒的脚本**（那句还原文案在成功分支里
也印了"仍不同，人工核对"）⇒ 证据与提交版脚本不匹配，所以才有上面这次复跑；现在这份日志与脚本一致。

**门禁（裁定 4：提交前跑，结果交维护者）**
`scripts/verify.ps1 -Configuration Release` 连跑两轮，八格读数全部取到最后一次改源之后：
- 第一轮 15:09:35（`artifacts/verify_gate_r1.log`）：八格全绿，唯一红 = **断言数对不上基线**
  （unit 1808→1807 等），⇒ "这一轮不作数"。按那一轮的现跑读数手改 `scripts/gate-baseline.json`：
  **总 2421→2438**（encoding 14→**23**、format 43→**52**、unit 1808→**1807**，其余五格逐字相同），
  `_说明` 里逐格记了出处，含"删的那 1 条钉的是零调用方死路"这一句。
- 第二轮 15:11:56（`artifacts/verify_gate_r2.log`）⇒ ✅ **8 套件全部通过 — 共 2438 断言**：
  unit **1807/0/2** · color **270/0** · encoding **23/0** · service **2/0/1** · ocr **12/0** ·
  qr **47/0** · format **52/0** · usability **225/0**；构建 **0 警告 / 0 错误**；
  `[1.7/2]` 外部判据（外部解码器读回产物）**15 格全 ✅**；`[2/2]` 宿主新鲜度与侧车离树检查过。
- ⏭ **1 处未判，不许读成通过**：`[1.8/2] --gainmap-color-product` —— 两个 `ultrahdr_app` 候选都过不了
  能力探测（旧版不认 `-j/-z` 这套 CLI，或它依赖的 DLL 不在旁边）。与本轮改动无关，属环境缺件；
  补上 `TTC_ULTRAHDR_APP` 后必须重跑那一格。
- **变异闭环之后（15:18:05）对同一棵树重跑被牵连那三格** ⇒ unit **1807/0/2** · encoding **23/0** ·
  format **52/0**，与上面第二轮逐字相同（`artifacts/post_restore_--unit-tests.log` 等三份）
  ⇒ 那串数是**最后一次改源之后**的读数，不是改源前的旧绿（M6/M7 那两趟变异动过 Core，必须这样收一次口）。

### 本批之后仍未量 / 未做
1. ~~启动改后 A/B（需起产品进程）~~ ⇒ **已做**（见上面「批3 的实机 A/B」：省 ~90ms，量级远小于
   被挪走的那 673–1020ms 构建，因为构建本来就在后台重叠；首次识字多付同一段）。
   `XamlControlsResources` 那条 0x8000FFFF ⇒ **提交前把因果查清了（本批，只查不删）**：
   - **读数**：`artifacts/e2e_sdr_*.log` 8 份里 **8/8** 都有
     `XamlControlsResources 加载失败 (非致命): 灾难性故障 (0x8000FFFF) App.xaml.cs:229`
     ⇒ 这条不是偶发，是每次启动必发生；时序上占 `43.908→43.909`（≈1ms，报告早先写的"3ms"是同一格的粗读）。
     ⚠ 日志里的 `:229` 是**当时那件构建**里 `LogService.Warn` 的行号；现行源码里那句 Warn 在
     `App.xaml.cs:234`，它所属的 try/catch 是 **228-235**，Add 本身在 **230**。
   - **能确定的一半**：抛点在 `App.xaml.cs:230` 那句 `Resources.MergedDictionaries.Add(new XamlControlsResources())`
     的**参数求值**上 ⇒ `Add` 这一步从没执行完。所以"第二份实例"**没有往资源树里合进任何东西**，
     全进程实际生效的 `XamlControlsResources` 只有 `App.xaml:25` 那一份。
     ⇒ "选区/标注那些独立 XAML 根的窗是靠第二份实例拿到隐式样式"这一条**证伪了**：
     第二份的内容一份也没落地，删掉它的**内容**不可能改变任何窗的解析结果。
   - **不能确定的一半（这就是它还该留着的原因）**：那次**失败的构造**是否顺带把 WinUI 控件框架初始化推到
     了后面要用到的程度（`CorePipelineTests.cs:1093-1099` 的原话是"这次构造顺带完成的框架初始化后面要用"）。
     这件事在进程内**读不到**：异常被 catch 吞成一条 Warn，没有任何仪表能区分"初始化做到了"与"没做到"。
     ⚠ 而且那条锁的失败文案（"删掉它产品会起不来，WER: combase.dll / 8000ffff，实测于 2026-10-01"）
     对应的实测**不是同一个改动**：2026-10-01 那次是"在 try/catch 之外读 `MergedDictionaries.Count`"，
     不是"删掉这次构造" ⇒ 它证明的是"这一段排在 `UnhandledException` 注册之前、在这里抛出没人接"，
     **没有**证明"删掉 Add 产品仍起不来"。这一格现在是**空白**，不是已定罪。
   - **归维护者肉眼的那一步（唯一可接受的下一步）**：把 `App.xaml.cs:228-235` 那对 try/catch 整体注释掉
     （不删），起新构建的 exe，然后**不看主面板**、直接开**标注窗**（`AnnotationWindow`，
     用 `{ThemeResource CardBackgroundFillColorDefault}` + `Button`/`ComboBox` 隐式样式）与
     `OcrPreviewWindow`/`QrResultWindow`/`SilentCaptureToast` 各自截一张图：
     判据是"控件带主题外观、无裸控件（豆腐块）"，而**不是**"主窗正常"。
     主窗正常而覆盖窗裸控件是自洽的可能结局 —— 每个 `Window` 有自己的 `XamlRoot` 与资源作用域，
     App 启动路径的绿证不到它们。App 侧共 7 个 `: Window` 类（Annotation / OcrPreview / QrResult /
     SilentCaptureToast / PrivacyPolicy / ThirdPartyNotices / MarkdownDocument）都在这条风险面上。
   - **要补的源锁（位置越界，本批没写，请主循环落）**：`CorePipelineTests.cs` 的
     `Test_XamlControlsResources_MergedExactlyOnce` 现在只钉"代码侧那一次 Add 还在"（`addSites == 1`），
     它挡得住"悄悄删"，挡不住"连锁一起删"。再加一条同文件锁：
     要求 `App.xaml.cs` 里那句 Add 之前必须存在一条含标记串 `豆腐块手验未做` 的注释，
     且 `App.xaml` 里 `<XamlControlsResources` 恰好 1 处；判据写成
     `code.Contains("豆腐块手验未做") && addSites == 1` —— 想删 Add 就得先删这个标记，
     而删标记的唯一正当条件是上面那一步的**手验产物**已入账。手验做完后把标记改成
     `豆腐块手验已做 <日期> <产物路径>`，锁继续钉住"不许无证据翻转"。
   在这一格拿到肉眼证据之前，**别把它当 3ms 的收益删**。
2. F3（132MB 级全帧缓冲复用）与 F4（预览窗逐帧/静置）——**判据前置是批4**，
   而这些仪表目前只在合成数据上跑过，真素材与真窗口没读。
3. ~~16-bit TIFF 的端序判据不判别~~ ⇒ **已补成会判的臂并主循环亲手跑过**（上面「外部裁判」那条）。
   ~~同一处顺带记下一条既有容器缺陷：**缺 ExtraSamples(338)**~~ ⇒ **同日第四轮已修**（裁定"四批保留、
   用测试验证"之后单独点头的一小批）：`baseTagCount 12→13` + 在 tag 296 之后写 `338=2`（unspecified，
   因为本编码器交出的 alpha 是**未预乘**的，写 1=assumed 是在撒谎）；判据 = 外部读端（tifffile/Pillow
   现在报 `extra=2`）+ 新加的 `AssertExtraSamplesContract`（TIFF8/TIFF16/TIFF-ICC 各一次调用，每次 3 条
   = 338 在场 / 标签总数 / 标签升序，共 **9 条**），变异两趟都实测过：只删写入行 ⇒ 6 格红在产品自己的
   "值区布局与写入顺序不一致"守卫上；连 `baseTagCount` 一起回退 ⇒ 46/6，红的正好是那 6 条新契约；
   还原后 52/0（详见下面「新判据与它红的样子」的 M6/M7）。
4. ~~IDAT 分块、流式 CRC、`ToArray()` 彻底消掉~~ ⇒ **本批已做前两件**（64 KiB/块 + CRC 随块增量；
   两条字面锁先改后改形，锁现在钉的是新的流式形状）。`ToArray()` 只在**仍要 byte[] 的消费者**上保留
   （TIFF 分条必须拿确切长度回填 273/279，探针样本亦然）⇒ 那一半不是漏改，是形状还需要它。
5. ~~`EncodeHdrFromScrgb` 死代码的处置~~ ⇒ **已删**（连同只被它用的 `FloatToPq10` / `LinearToPq`；
   复核与牵连的锁见上面「批2 其它」那条）。
6. ~~界面禁用那格的**渲染证据**：只有代码 + 断言；脚本只做过语法校验~~ ⇒ **已跑，rc=0**（第四轮，
   见下面「裁定 1–4 的落字与补测」的 UIA 那一格）：C2 `IsEnabled=false` 与 C4 点名提示两格判上。


## 9. 本轮开工前挂在"未量"的那一列，现在的状态

- ~~三次启动前设置写盘各自的来源与可挪性~~ ⇒ **已归因并处理**（§8 批3：全部来自 `RebuildFormatCombo`，
  出口去重已落地，根因那句 `FormatIndex=-1` 的写法留作缺陷队列）。
- F4 整族（悬停重排、每帧 RTV、3s 静置唤醒）——**仍未量**，前置是批4 的窗口臂跑真素材。
- F5 的 staging 重建次数、`_poolLock` 持锁时长——**仍未量**。
- OCR rec 按批内真实宽度收窄后的端到端差；翻译批量化的 4.4s→1.1s——**仍未量**。
- 强高光 HDR 素材（>纸白 120 nits）对 deflate 分流选路的影响；跨屏 #46；MSIX 侧载；签名与杀软——**仍未做**（与 10-31 交付文档 §7 同一列）。
