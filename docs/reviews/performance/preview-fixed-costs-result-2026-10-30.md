# 预览固定成本与启动：阶段 0–4 执行结果（2026-10-30）

> 带日期 ⇒ 取证快照，不回写；计划本身在 `preview-fixed-costs-plan-2026-10-30.md`（同目录）。
> 本文只记**已落地的东西 + 读数出处 + 未做的部分**，每个数字都能指到 `artifacts/` 里的文件。

## 判词

1. 阶段 0（仪表）、1（起点预烘图集）、2（`avifenc` 探测推后 + 尾巴三件推迟）、3（SDR 共享槽）
   与 4（预览窗复用进程级设备）**均已落地**；只有启动链最前段与最后两段按末节理由不做。
2. 阶段 3 的收益是**每次选区捕获少一次整屏 SDR 回读**（本机 4K：21.6–37.1ms），换来的代价是两块槽的
   配对交还纪律；探针 ⑨ 已逐字节证明"按矩形回读 == 整块回读取同一矩形"（8,294,400 字节，0 处不同）。
3. 冷启动第一拍（会话刚建）SDR 槽**不可用**，照常整屏回读 —— 这是设计，不是漏：那一刻没有已冻结的帧。

## 读数（改前 → 改后）

| 项 | 改前 | 改后 | 出处 |
|---|---|---|---|
| 首扇预览窗"等预热图集" | 等 27–35ms（第 2 扇起 3ms） | **0ms**，首帧合成 35→12ms | `artifacts/preview_fixed_costs_phase1_2026-10-30.log`（改前那行 18:42 与改后 19:43:07.698 并排在同一文件里） |
| 启动：设置加载与回显 | 435ms（反序列化 0 + 回显 435） | **23ms**（基础 2 + 编码选项 1 + 翻译/语言 1 + 其余 18） | `artifacts/` 下 E2E 各 log；改前见 19:45 那批 `启动: 设置两段` 行 |
| 启动：进程到主窗口 | 1301ms（同批） | **632–716ms**（阶段 4 同日第二轮三次冷启动） | 同上 |
| 启动：构造尾巴 | 52–69ms | **31–36ms**（三次：31/31/36） | 同上 |
| 每次选区捕获的 SDR 整屏回读 | 21.6 / 22.6 / 23.9 / 28.1 / 33.5 / 37.1ms | **11–25ms**，且只读选区矩形 | 改前 `artifacts/preview_fixed_costs_phase1_2026-10-30.log`；改后 `artifacts/e2e_sdr_default_*.log` 的 `选区 SDR 回读` 行 |
| SDR 回读窗口汇总 | 8 帧 197.7ms / 53 帧 1161.7ms（2s 窗口） | 同一条仪表保留，用于后续对账 | 同上 |
| 交接探针 | 臂①–⑦ | 臂①–⑪ 全绿，`RESULT handoff=pass` | `artifacts/frame_handoff_probe_sdr_2026-10-30.log` |
| `TTC_SDR_SHARED=0` A/B | — | 臂④–⑦ 不变，臂⑧ 报"不判"（SDR 槽一次都没冻） | `artifacts/frame_handoff_probe_sdr_off_2026-10-30.log` |

E2E 细节（默认档）：冷启动第一拍 `SDR 池状态 HasFrame=False` ⇒ `SDR 槽不可用` ⇒ 整屏回读 40.4ms；
预热后连续两拍都 `SDR 共享槽已冻结`，选区回读 11/12ms；取消（Esc）之后下一拍**照样冻得出槽**，
日志里没有"上一任未交还"的轮换告警 ⇒ 交还纪律成立（`artifacts/e2e_sdr_default_mix.log`）。

## 阶段 3 的形状（读代码时的三条主线）

1. 捕获侧 `WgcCaptureService.CaptureMonitorInternal`：冻结 SDR 槽的条件 = `WantSharedFrame && 开关 &&
   hdrSession is not null`。最后一个条件是硬要求 —— 没有 Float16 会话时预览只吃 SDR 整帧，
   跳过回读就是整屏遮罩。
2. 配对纪律：SDR 冻上而 HDR 没冻成 ⇒ 交还 SDR 槽 + 整帧回读 + 取不到就抛（不回退成静默黑屏）。
3. `MainWindow.StartSelectionCapture`：`forceSdr` 在**捕获之前**求值（那档不冻任何槽）；认领 SDR 槽在
   `captureResult.Dispose()` 之前；`sdrRegion` 在动作分派之前回读；四条出口（保存/取消/失败/异常）都在
   `finally` 交还。SDR 槽从不交给预览窗，所以"取消时没人交还"这类问题在这里不存在。

## 门禁（本批实跑）

| 门 | 结果 |
|---|---|
| 编译 | `dotnet build src/TrueToneCap.App -c Release`：0 警告 0 错误；Test 项目同 |
| 单测 | `RESULT suite=--unit-tests passed=1700 failed=0 skipped=1`（skip 为本机既有的环境跳过） |
| 变异 | 三条各让**恰好一条**新判据红：去掉 `!forceSdr` → ⑥ forceSdr 判据红；回读用错句柄 → ⑥ 回读位置红；`Dispose` 不交还 SDR 槽 → ④ 红 |
| 探针·交接 | 默认 `rc=0`；`TTC_SDR_SHARED=0` 时臂⑧"不判"（预期） |
| E2E | 默认保存 / 取消后保存 / `TTC_PREVIEW_SHARED=0` / `TTC_FORCE_SDR=1` / `TTC_NO_FLOAT16=1` 各存出一张可打开 PNG |

## 阶段 4：预览窗复用进程级 D3D11 设备（同日第二轮）

**形状**：`HdrCaptureWindow` 的公开构造不再 `D3D11CreateDevice`，改为取进程级惰性单例
（`TTC_PREVIEW_SHARED_DEVICE=0` 回到自建）。三条纪律：

1. 共享设备与其 `ImmediateContext` **永不**由某扇窗 Dispose（`_ownsDevice` 两条分支：`Dispose` 与
   `ReleaseDeviceResources`），否则下一扇窗复用到的是一块已释放的 COM 对象；
2. 设备丢失时由第一个发现的窗口在缓存锁内**再判一次"真丢了"**再换新（两个窗口前后脚报丢失时，
   后到的不能把前一个刚建好的设备再收一次）；
3. `ImmediateContext` 非线程安全 ⇒ `RenderCore` 入口拿全局 `s_ctxLock`（产品路径同一时刻只有一扇
   预览窗，真机上恒无竞争；它挡的是探针那种"旧窗未 Dispose、新窗已在渲染"的叠加）。

首扇窗那笔 33–65ms 也不再计入用户等待：选区起点 `PrewarmAtlasForMonitor` 顺手 `WarmSharedDevice()`，
与捕获那 30–150ms 重叠。

### 读数

| 项 | 改前 | 改后 | 出处 |
|---|---|---|---|
| 每扇预览窗的构造（D3D 设备） | 39 / 43 / 44 / 49 / 51 / 52 / 53 / 54 / 55 / 57 / 59 / 65 / 71 / 78 / 98ms（同一二进制连续 15 扇） | **0ms**（第 1 扇由起点预热建成，日志 `窗口构造 0ms（复用进程级共享设备）`） | app log 20:20–20:26 那 15 行 vs 20:48:53.949 `窗口构造 0ms` |
| 探针连开窗（`--preview-ui-probe`） | 每扇 39–57ms | 首扇 53ms（探针不预热）+ 其后 16 扇全部 0ms | app log 20:44:41–20:44:56；探针 `rc=0` |
| 关掉开关（`TTC_PREVIEW_SHARED_DEVICE=0`） | — | 39 / 51 / 54ms（回到改前形状，探针仍 `rc=0`） | app log 20:44:16–20:44:20 |

### 判据与变异

- 新增 `Test_PerfWiring_SharedPreviewDevice` 七组源码锁：构造只取不算 / 两处所有权分支 / 换新只在锁内判
  一次"真丢了" / 渲染段全局锁且入口唯一 / 开关与建不出来时的自建兜底 / 起点预热排在缓存开关早退之前 /
  产品建窗只有一处。
- 变异三条，各让**恰好一条**红：删 `if (!_ownsDevice)` 闸（②）、把 `lock (s_ctxLock)` 去掉（④）、
  删兜底分支里的 `_ownsDevice = true`（⑤）。
- 真机：`--preview-ui-probe rc=0`（`artifacts/preview_ui_probe_shareddev2_2026-10-30.log`；
  第一次那轮 rc=1 是鼠标被占用导致选区几何全错，同刻关掉开关的对照臂也复现同样 ★，与设备无关）、
  `--frame-handoff-probe RESULT handoff=pass`（`artifacts/frame_handoff_probe_shareddev_2026-10-30.log`）、
  E2E 保存一张（`artifacts/e2e_sdr_shareddev_save.log`，日志里 `窗口构造 0ms` + `选区 SDR 回读 23ms`）。

**回退**：`TTC_PREVIEW_SHARED_DEVICE=0`。

## 阶段 2 第二步：启动尾巴（同日第二轮）

- 删掉 `MainWindow` 构造里重复的一次 `Task.Run(MultiOcrService.Initialize)` —— `App.xaml.cs` 启动段
  已经启动同一次（内部 `_initialized` 短路），第二次纯属重复投递。
- 「开机自启动一致性检查（只读+报警）」「Toast 注册」「日志页订阅」三件推到
  `DispatcherQueuePriority.Low`（首帧之后）；托盘/热键/本地化/首帧文字**不动**。
- 读数：构造尾巴 **52–69ms → 31–36ms**（三次冷启动 31/31/36）；进程到主窗口 **773–979ms → 632–716ms**
  （同机当次三次；XAML 段本身有冷热波动，这里只报合并值，不拆卖）。
- 判据：`Test_PerfWiring_StartupBreakdown` 增三组源码锁（Low 队列唯一且排在本地化之前、三件都在同一回调、
  `SubscribeLogEvents` 只一处、重复 OCR 投递已删）。

## 未做（每条都能指到原因）

- **复制 / 识字 / 翻译 / 贴图、带标注保存的真机 E2E**：这几条与保存走同一份 `sdrRegion`，而它的像素内容
  已由探针 ⑨ 逐字节对等证明（生产回读 == 第二设备整块回读取同一矩形）；再驱动的意义只剩接线，
  而驱动覆盖全屏预览窗会与维护者抢鼠标（本轮实测就撞上了：`SetCursorPos` 被拒，光标不动）。
  接线本身有源码锁（⑥ 回读排在动作分派之前），留待维护者手验。
- **阶段 2 其余回显分段**：回显已从 435ms 降到 23ms（`UpdateAvifEncoderLabels` 那 478ms 是唯一大头）。
  剩下的 `基础/编码选项/翻译语言/其余` 四格都在 2ms 级；要再压就得动"格式/质量"这类**被 `CurrentFormat()`
  读取**的控件，与本轮的"只推不参与读取的刷新"原则相冲，列为不做。
- **启动链最后两段**：`AppServices.Initialize` 里 WGC/GPU 设备同步创建（本机 133ms，在窗口之前）与
  `InitializeComponent` 本身（265–449ms，36 个 ComboBox + 145 个内联项）。前者要拆成"懒建 + 首个捕获等它"，
  会让首个热键与静默截图共享一条初始化竞态；后者要拆 XAML 页（`x:Load`）。两笔都动面大、收益未量，
  本轮不做 —— 要做得先补"窗口已激活 → 首帧可见"的仪表，现有打点只到 `Activate` 之前。
- **纯 SDR 屏（无 Float16 硬件）**：本机没有这块屏，用 `TTC_NO_FLOAT16=1` 已模拟跑通（预览吃 SDR 帧、
  框选、保存 SDR PNG），但"真机 + 真 SDR 屏"仍缺一次。

## 回退

| 开关 | 作用 |
|---|---|
| `TTC_PREVIEW_SHARED=0` | 两块槽都不冻，回到整帧路径（首帧耗时 A/B 用） |
| `TTC_SDR_SHARED=0` | 只关 SDR 槽；HDR 槽与今天一致 |
| `TTC_FORCE_SDR=1` | 预览只吃 SDR 帧，且不冻任何槽 |
| `TTC_PREVIEW_SHARED_DEVICE=0` | 预览窗回到每窗自建 D3D11 设备（阶段 4 回退） |
| `TTC_NO_FLOAT16=1` | 把本机当纯 SDR 屏（`CanDeliverExtendedRange` 恒 false） |
