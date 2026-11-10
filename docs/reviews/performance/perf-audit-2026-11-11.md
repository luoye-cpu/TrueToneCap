# TrueToneCap 全量性能审计（2026-11-11）

> 只读审计，未改任何代码。目的：在 7079c67 之后盘点四个领域的剩余性能空间，
> 区分"实测/实验验证"与"代码推断"，并对已处于良好状态的区域明示"不要再动"。

## 方法与来源

| 领域 | 审计方式 | 主要覆盖文件 |
|---|---|---|
| 捕获链路 | 子 agent 全文审读 + 关键行号抽验 | WgcCaptureService.cs、HdrCaptureWindow.cs、MainWindow.xaml.cs 捕获段 |
| OCR 推理与预览渲染 | 子 agent 全文审读 + 关键行号抽验 | OnnxOcrEngine.cs、OcrTextOverlay.cs、OcrPreviewWindow.xaml(.cs)、BitmapPreprocessor.cs |
| 编码管线 | 主 agent 自查 | FormatEncoders.cs、NativeAvifEncoder.cs、ManagedPngEncoder.cs、DeflateZlib.cs、NvEncoderNative.cs、GpuCapability.cs、NativeJxlEncoder.cs、JpegLiNative.cs |
| 启动与常驻 | 主 agent 自查 | App.xaml.cs、AppServices.cs、LogService.cs、SettingsService.cs、HotkeyManager.cs |

反幻觉措施：两个 agent 报告中的全部高危指控（回读线程归属、`_poolLock` 临界区、复用缓冲竞态、
翻译双渲染、rec 定宽）均按 file:line 逐一回读原文核实，全部属实。

---

## 优先级总表

### P1 —— 会产出错误结果（不只是慢）

| # | 发现 | 位置 |
|---|---|---|
| 1 | **复用缓冲静默覆写竞态**：`_reuseSdr/_reuseHdr` 在长度匹配时原缓冲返回，而 2026-08-25 的"后台持续编码"模型让编码跑在 `_isCapturing` 之外 —— 慢格式（JXL 实测可达 24s）编码中再截一拍，`GetLatestSdr/GetLatestHdr` 会 `Buffer.BlockCopy` 进上一个编码任务**正在读**的数组 → 撕裂图被编码保存。文件内 2026-10-30 的更正注释只覆盖了选区腿（有 `_isCapturing` 全程守着），漏了 SilentCapture 这条没守的腿 | WgcCaptureService.cs:166-169、2107-2117、2186；MainWindow.xaml.cs:1790-1807 |
| | **修法**：按消费者区分 —— 选区路径（同步消费）继续复用；`WantSharedFrame=false` 的静默截图路径改双缓冲轮换或直接新分配（该路径本就低频）。**验证**：JXL 无感截图，编码期间立刻再截一拍，比对第一张产物与安静环境产物的哈希 | |

### P2 —— 每次使用的可感知延迟（结构性）

| # | 发现 | 位置 |
|---|---|---|
| 2 | **选区回读整段跑在 UI 线程**：`await tcs.Task` 续体回 UI 线程后同步调 `ReadSharedRegion/ReadSharedRegionSdr`（内含最长 500ms GPU 锁等待 + 整选区转换；4K 全屏 = 132MB float 分配），期间状态栏/托盘/热键全部冻结 | MainWindow.xaml.cs:2890、2910 |
| 3 | **`_poolLock` 临界区贯穿 GPU 回读与会话 Dispose**：回读（10-500ms）、整帧拷贝（TryGetLatestFrame，录制器每 tick 过锁）、Dispose（内含 `_callbacksDrained.Wait(2000)`）全在锁内 → 录制掉帧、并发截图的会话查找被卡。F5 仪表注释自己已点名 | WgcCaptureService.cs:1962-2027、1858-1859、1730 |
| 4 | **OCR 行几何每次重建全量重扫（三案同根）**：① resize ~10Hz 触发 RenderOverlay，每行每次重跑全分辨率像素扫描（MeasureInkRect 窗口可达 det 框高 3.8 倍）+ 两次 MedianBlockColor；② FitFontSize 的收缩循环被 `result = Math.Max(result, referenceSize)` 无条件抬回 → **返回值恒等于 max(12, startPx)，循环是可证明的死代码**，cache miss 时白付 ~14 次 Win2D 文本布局（floor 是"覆盖字 ≥ 原字"的既定口径，循环可直接删）；③ 翻译完成时 `ModeToggle.IsChecked = true`（触发事件内 RenderOverlay）+ 851 行显式 RenderOverlay，**背靠背两次全量重建**。一个"每 OCR 结果一次"的行几何缓存可同时收掉三案 | OcrPreviewWindow.xaml.cs:274-282、849-851、660-676；OcrTextOverlay.cs:49-66、23 |
| 5 | **rec 推理恒按 RecImgMaxW=1280 定宽**：模型签名 `-1×3×48×-1` 本可动态；单框/合批张量固定 1280 宽，窄框（典型 300px）多付 ~4 倍 rec 算力，解码同样对补零区全宽做 18710 类取 max。CPU EP 上收益直接；解码侧与宽度成正比，任何 EP 都省 | OnnxOcrEngine.cs:816、864、936-937、1001 |
| 6 | **16-bit AVIF 中转仍是临时文件（已实测可 stdin 化）**：8-bit AVIF 已走 `RunPngViaStdin`（零磁盘 I/O），但 HDR PQ 软件路、SDR ≥10-bit 路、GainMap 容器路（2 PNG 进 + 2 AVIF 出）、JXL（PPM/PNG + ICC 文件）、JpegLi（PPM）全部写临时文件再让外部工具读回。4K 口径每次 40-130MB 磁盘 I/O 在编码关键路径上。**附录 A 已实测**：16-bit PNG 经 `--stdin --input-format png` 编码成功且 `--depth 10` 真实生效（ffprobe: yuv444p10le） | FormatEncoders.cs:428、494、605-608；NativeJxlEncoder.cs:173、298、392；JpegLiNative.cs:275、315 |

### P3 —— 中等收益（交互底噪 / 冷启动 / 分配抖动）

| # | 发现 | 位置 |
|---|---|---|
| 7 | 帧回调每帧两次整屏 GPU 拷贝（`_latestTexture` 合理 + `_sharedTex` 仅冻槽时才需要）：动态内容 60fps 下 = 每帧 66-264MB 纯多余显存带宽，且拉长每帧持锁时间（加剧 #3） | WgcCaptureService.cs:963-1001 |
| 8 | 悬停窗口标题一变 → 整张 2048×1408 文字图集渲染线程全量重排（实测这条腿 34-84ms，DirectWrite 排版占 86ms@4K+150%）：QQ 式浏览期每划过一个窗口卡一拍。修法：标题走小动态纹理（`_badgeTex` 模式现成），主图集缓存不被标题失效 | HdrCaptureWindow.cs:4428-4436、1574-1657 |
| 9 | `IdleStopDelayMs=15000` 的原始依据（保活逐帧整屏回读占 43.4% 单核）已被 P0-5 拆掉，常数没跟着调：现在每付一次会话冷启动（p50 51ms + SDR 等首帧 83ms + Float16 等帧 160ms）只换回"省一次显存内 CopyResource"。提到 60-120s 或对光标屏常保暖会话；`SharedHeld` 顺延保护已就位，调常数无正确性风险（显存代价要与 #7 的削减一起算账） | WgcCaptureService.cs:121、2045-2052 |
| 10 | 多屏选区在框选前付全量拼接 + HDR 整帧回读 + 逐屏白点查询（3 次 QueryDisplayConfig 实测 ~150ms）：先做无风险的一半 —— 白点/能力查询按显示器配置版本缓存（`InvalidateSessions` 是现成失效钩子） | WgcCaptureService.cs:2487-2542 |
| 11 | det 路径大额分配抖动：Parallel.For 每行 new 两个行缓冲（4K ≈ 200MB Gen0 churn/次）、probMap 33MB + bitmap 8MB + visited 8MB 每次新分、BFS 每像素 new 4 元组数组。张量本体已池化，这几处漏了 | OnnxOcrEngine.cs:679-680、750-751、1121、1150 |
| 12 | `AppServices.Initialize()` 在 UI 线程同步建 WGC 会话 + D3D 设备（设备创建实测量级 33-57ms），位于"进程 → 主窗口"关键路径。托盘常驻场景只发生一次，属启动指标优化而非交互优化；可后台化但首个热键会 inheriting 该延迟，需权衡 | AppServices.cs:70-74 |
| 13 | HDR PNG 直存的 `Rgba16ToBgra16Bytes` 整帧字节序拷贝（4K ≈ 66MB 一次性 LOH 分配 + 逐字节换序）：可让 ToneMapper 直接输出 BGRA16 顺序或 Encode16 内联换序 | FormatEncoders.cs:47 |

### P4 —— 顺手级

- 渲染线程逐帧小分配：`new UiCbData`（~6KB/帧）、`ParseColorToLinear` 每帧解析同一串颜色 ×3、`GetLayersSnapshot` 每帧多次锁内 ToArray —— <10KB/帧，稳态 Gen0 压力，可零化且改动面窄（HdrCaptureWindow.cs:2142-2154、2381、2387/2545）。
- Compose 与预览对同样行做了一遍一模一样的像素分析（OcrTextOverlay.cs:262-294）—— 接 #4 的行几何缓存后整段可删；33MB clone 经评估**不值得动**（后台线程 5-15ms memcpy）。
- Windows OCR 兜底路径全图预处理（积分图 67MB + 整图副本）—— 仅 ONNX 不可信时发生，罕见路径；若动则限制预处理最大边 + 积分图改 int 行前缀和（OcrService.cs:24-47、BitmapPreprocessor.cs:133）。
- 设置文件启动时被读两次（主题一次 + 完整 Load 一次）：小 JSON 亚毫秒级，仅记录备查。
- `OcrTextOverlay.s_fitCache` 键漏了 startPx（现因 #4 的死循环而潜伏；真把循环修成"取放得下的最大字号"时会显形，修 #4 时必须一并处理）。
- NVENC 会话逐编码建/销（`using var nv = new NvEncoderNative(...)`）：单张触发场景毫秒级无感；将来做连拍/批量才值得缓存（FormatEncoders.cs:1514）。

---

## 已确认良好、无需再动的区域

**捕获链路**：路线 B-3 本体（整帧不过 CPU、NTHANDLE、`expectedHandle` 防槽轮换、`SharedHeld` 冻结语义）；
P0-5 消费者窗口（修复 43.4% 单核）；staging 复用 + F5 账目；图集三件套预热；进程级共享预览设备 +
WarmSharedDevice；渲染线程纪律（UI 线程零渲染、BelowNormal↔Normal、Present(1)、悬停去重、Unmap-finally）；
`WantGpuTexture=false`；EnsureBackBuffer 的"每帧重建 RTV"疑点已有 F4 计数，**读数没坐实之前不要动**。

**OCR**：det/rec/批张量池化 + 锁 + 只清本批行；Span 解码带 A/B 开关；byte→float LUT；RecBatchSize=4
合批 + 失败整批退串行；两层推理串行化与超时分层；Compose 在后台线程；s_textCache/s_fitCache 封顶；
MedianBlockColor 已 /2 子采样；DebouncedSyncOverlay 的"节流 + 尾随补刷"模式本身正确；
MergeDetLines 与 CTC 尾部字符串拼接在现有量级下可忽略（避免误报，明确不修）。

**编码管线**：8-bit AVIF stdin 管道（零临时 I/O）+ EncodeFast（Filter=None + 最快压缩，中转文件的最优档）；
avifenc/JpegLi 可用性探测均缓存；stderr/stdout 异步排水防死锁；DeflateZlib libdeflate 后端 + 可压缩性探针
静态缓存（level 10）；GpuCapability 检测缓存一次（NVENC `IsAvailable` 每次建 D3D 设备的疑点**不成立**——
只被它调用一次）；PNG 直存按用户档位 `s.Effort`；GainMap 中转全部 fastest 档；NVENC 纹理直通避免 CPU 拷贝。

**启动与常驻**：重活全部后台化（着色器预热 / OCR 引擎 / jpegli+libdeflate 探测 / PreloadUiCache）且启动耗时
有仪表（s_startTick → "进程到主窗口 Xms"）；LogService 常驻 StreamWriter + 内存计数轮转（每条日志一次
syscall，UI 队列有 MaxUiEntries 封顶）；设置保存差量序列化 + 原子写（tmp+rename）；热键响应路径是字典查表
+ 回调，无可削；单实例走全局 Mutex + RegisterWindowMessage 广播（SendMessageTimeout 防拖死）；崩溃哨兵
与反复崩溃保护是稳定性机制，不属于可优化项。

---

## 附录 A：实测 —— avifenc `--stdin` 接受 16-bit PNG（2026-11-11 本机）

```
$ ffmpeg -f lavfi -i testsrc=... -pix_fmt rgb48be in16.png        # 2967 B，16-bit 源
$ cat in16.png | avifenc --stdin --input-format png --depth 10 -s 9 -o out_stdin.avif
Encoded successfully.  Color total size: 6276 bytes
$ ffprobe -show_entries stream=pix_fmt out_stdin.avif
256,256,yuv444p10le                                                # 10-bit 真实生效
```

结论：P2-#6 的修法可行 —— 把 `ManagedPngEncoder.Encode16` 增加 Stream 重载、`RunPngViaStdin` 扩展为
接受预编码 PNG 字节流，即可让 HDR PQ 软件路 / SDR ≥10-bit 路 / GainMap 底图全部零磁盘 I/O。
GainMap 的两份 AVIF 输出文件（容器拼装需读回字节）属于两遍装配的固有代价，保留文件即可。

## 附录 B：仪表缺口

现有 F4/F5 仪表是本仓审计得以进行的原因（多个发现直接引用了仪表注释里的自证）。
本次发现的两个盲区，动手前先补仪表：① "复用缓冲交出时上一份是否仍被引用"（P1-#1 无任何覆盖）；
② 帧回调 GPU 拷贝段时长（P3-#7 的 `CacheTextureGpuCopy` 无计时）。

## 建议动手顺序

1. **P1-#1 复用缓冲竞态**（错误文件级，改动小：静默路径改双缓冲）
2. **P2-#4 行几何缓存**（一个缓存收三案：resize 卡顿、翻译顿挫、Compose 重复扫描）
3. **P2-#2 + #3 连做**（回读外移与锁瘦身耦合在同一临界区，分开做会返工）
4. **P3-#9 空闲回收常数**（一行改动 + 仪表对读，收益/风险比最高）
5. P2-#5、P2-#6、P3-#7/#8 按需排期；P4 批量顺手处理
