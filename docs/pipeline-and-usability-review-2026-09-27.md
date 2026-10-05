# TrueToneCap 管线完整审查与可用性分析 · 报告（2026-09-27）

- **基线**：`v0.3.6-beta`（标签钉在发布提交 `4368eed`，HEAD `ca7ed99` 只差文档；工作树除本文件与计划文件外干净）。
- **计划**：`docs/pipeline-and-usability-review-plan-2026-09-27.md`（目标 G1–G4、六条 lane、三级判据 T1/T2/T3、交付物 D1–D8、六个决策）。
- **本文当前状态**：批次 A 的基线取证已完成，批次 G（旧 P1×18 台账重判）首遍完成。批次 B/C/D/E/F 未开始 ⇒ G1（产出正确性全矩阵）与 G2（静默降级台账）**尚未回答**，本文不假装答过。

---

## 1. 基线实测（当场跑，出处 `tests/output/plan-baseline-gate.log`）

```
[1/2] 构建 TrueToneCap.slnx        0 个警告      已用时间 00:00:31.54
[1.6/2] 发布门禁回归               通过
[1.7/2] 外部判据                   通过
[1.8/2] 色彩管线判据               3/3 全部判了且通过
[1.9/2] 宿主新鲜度                 通过
[2/2]   7 套件 1365 断言 (48.7 s)   exit=0       全程 wall = 191.6 s
   --unit-tests       869 / 0 / 跳过 1     --color-tests      213 / 0     --encoding-tests    14 / 0
   --service-tests      4 / 0 / 跳过 2    --ocr-tests         12 / 0     --format-tests       43 / 0
   --usability-tests  210 / 0 / 跳过 1
```

- 求和自检：869+213+14+4+12+43+210 = **1365**，与汇总行一致。
- **4 处跳过**＝本轮有 4 件事没判；C 批起第一条要办的就是逐条问"这 4 个 skip 是环境够不到，还是判据恒跳过"（结论在 §5，其中一条当场被接上）。
- ⚠ **这一节的 1365 已被本轮后面的改动作废**：§5.1 接上一把从没判过的锁、§5.2 把一条恒跳过改成真断言之后，基线变成 **1369 / 2 处跳过**。引用基线请取 §5.2 末尾那条。
- `[1.6]–[1.9]` 约 111 s 是两个端点相减**推算**的，不是逐步实测。
- 脚本注释里"全量约 110 s"与实测 191.6 s 差一截 ⇒ 注释过期，本轮不改（属 `pipeline-review-2026-09-15.md` 的地盘），只记这条差异。
- **没有改用户机器**：两份 `%LOCALAPPDATA%\TrueToneCap\*.settings.json` 跑前跑后 sha1 逐字不变（`5c9fef8a…` / `40589a55…`，记录 `tests/output/preflight-settings.sha1`）。

---

## 2. 判据强度的结构性事实（G3 的第一条答案）

门禁的 1369 条断言（§5.2 之后的现值；取数那一轮是 1365）**跑不到 App 侧代码**，这不是覆盖率问题而是接线问题：

- `src/TrueToneCap.Test/*.csproj` 与 `src/TrueToneCap.Tools/*.csproj` 只 `ProjectReference` 到 **Core**，没有任何一条引到 `TrueToneCap.App`；
- App 侧规则靠**读源码文本**判：`CorePipelineTests.cs` 里 **80 处 `File.ReadAllText`**；
- App 侧真正的无头执行早已存在但住在产品进程里：`App/Services/SelfTest.cs` 直接 `new WgcCaptureService()`（`:218`）、`new CapturePipelineService(new SettingsService())`（`:301`）、调 `PreparePixelsWithIcc`（`:134`），CI 用 `& $exe --selftest --out …`（`.github/workflows/release.yml:68-71`）；
- 而 `SelfTest.cs:302` 自己注明用 `new AppSettingsData()` 默认快照、"只看 HDR 落点，不看质量档" ⇒ **真身进程判据的覆盖面 ≠ 用户所选面**。

⇒ 于是"1365 全绿"与"09-15 四个 P0 成立""§57 整张卡片消失而全绿"并不矛盾：**T2（源码里出现过这行字）不能推 T3（这行代码被跑到过）**。本报告所有结论都按 T1/T2/T3 定级，见 §3 表最后一列。

---

## 3. 2026-09-15 报告 P1×18 重判（G6 交付物 D6 的首遍）

「复核」列标【我】的是我逐行回读过原文的；【lane】表示只有子代理取证、主循环未复读——按项目规矩**这类行不得当已确证**。

| 旧编号 | 旧症状 | 现状 | 决定性证据 | 复核 |
|---|---|---|---|:--:|
| P1-1 | AVIF 保底写 `.png` 却返回 `.avif` 路径 ⇒ 指向不存在的文件 | **已修**（两条换后缀保底都登记） | `FormatEncoders.cs:481`(WebP→PNG)、`:1545`(AVIF→PNG) 各调 `RecordFallbackPath`；管线 `CapturePipelineService.cs:609`、`:719` 用 `ResolveActualPath` 后返回真实路径 | lane |
| P1-2 | `SaveQuiet` 空 catch ⇒ 只读目录时设置静默回默认 | **已修**（失败进日志页，无弹窗） | `SettingsService.cs:214-218` catch 改 `LogOnce(...)`；新增前置门 `:41-42` `SettingsWriteProbe.CanPersistSettings`；调用点从 9 处涨到 **28 处**；`AppServices.cs:124` 外层仍是空 catch | lane |
| P1-3 | OCR 模型目录硬编码绕过 `ResolveModelDir()` | **已修** | 目录只算一处在 `OnnxOcrEngine.cs:221-238`，四个调用点全走它；`_initialized` 短路被 `MultiOcrService.cs:59` 的锁包住且置位移到注册后 | lane |
| P1-4 | 毫秒时间戳命名、无重名检测 | **仍开** | `CapturePipelineService.cs:501` = `{FileNamePrefix}{yyyyMMdd_HHmmssfff}{ext}`，函数体内无 `File.Exists`/递增；覆盖是**静默**的，因为 `AtomicFileWriter.cs:63` 是 `File.Move(temp, full, overwrite: true)` | **我** |
| P1-5 | 子进程超时不 Kill ⇒ 泄漏 | **大部分已修，三处残留** | Kill 收口在 `NativeEncoderGuard.cs:56` `Kill(entireProcessTree: true)`，cjxl/cjpegli/cwebp 全部经 `NativeProcess.DrainAndFinish`（`NativeJxlEncoder.cs:247/336/426`、`JpegLiNative.cs:152/298`、`NativeWebPEncoder.cs:85`）。残留：`NativeAvifEncoder.cs:289` 有 Kill 但**无** `entireProcessTree`；`ShaderCompiler.cs:99/126` 超时后**完全不 Kill** | **我** |
| P1-6 | 无感截图编码并发无上限、取消令牌不传子进程 | **仍开（两点都在）** | `MainWindow.xaml.cs:1367-1376` 编码转 `Task.Run` 后 `:1387` 只释放捕获锁；`TrackPendingTask` 不排队；产品码里 `SemaphoreSlim` 只有翻译/WGC/OCR 三处门，编码链路上没有。无感路径 `:1470` 显式传 `CancellationToken.None`，且 `NativeEncoderGuard` 的等待签名里根本没有 ct 形参 | **我** |
| P1-7 | 3 分钟硬看门狗 `Environment.Exit(0)` 截断在途编码 | **部分修** | `SelectionOverlay.xaml.cs:314-338`：先 `Finish(Cancel)` 给 5 秒再 `Environment.Exit(0)`，判据也改成"最后一次交互后 3 分钟"（`:341-351`）；但**不等 `_pendingEncodeTasks`**（该字段是 MainWindow 私有，此文件零引用）；HDR 覆盖窗同形一份：`HdrCaptureWindow.cs:462-475` | lane |
| P1-8 | 未勾选托盘时直接 `Environment.Exit(0)` 绕过等待 | **仍开** | `MainWindow.xaml.cs:228-250`：`:233 e.Cancel = true` ⇒ `Closed`/`OnWindowClosed` 不触发，而"等 5 秒在途编码"的逻辑恰在 `OnWindowClosed`（`:2968-2979`）里；该分支只有 `Task.Delay(200)`。对比：托盘退出分支经 `TrayIconManager.cs:138-139` → `:230 return` ⇒ 等待逻辑会生效 | **我** |
| P1-9 | AVIF SDR 不传 `--depth` ⇒ 位深设置对 SDR 无效 | **已修** | `NativeAvifEncoder.cs:181` `string depthArg = isHdr ? "" : GetDepthArg(bitDepth);`，`:198-203` 拼进命令行；钳制 `FormatEncoders.cs:1808-1809` `AvifContainerDepth` | lane |
| P1-10 | PNG 请求 10/12-bit 时 `sBIT` 恒写 8 | **已修**（且 PNG 现在是默认格式，权重上调） | 高位深走 `Encode16` 且按实际位写 sBIT（`ManagedPngEncoder.cs:167-173`）；byte[] 路诚实标 8（`:107-114`）而产品码已不会带 >8 进这一支（`FormatEncoders.cs:95-107` 显式把 `OutputBitDepth>8` 降回 8）⇒ 容器翻倍不可达。残留：`:22` 的 XML 文档还写着旧口径 | lane |
| P1-11 | XAML 114 处中文 vs 表内 41 处；两个 Tag 对不上 | **部分修，且新抓到一类漏**（见 §4-N1） | 机制换成显式声明表 `MainWindow.Localization.cs:290-322`（11 行槽位）；`OcrEngineCbo` 与 `GainMapModeCbo` 两个 Tag 已逐字对上（`:179-181` vs `MainWindow.xaml:381-383`；`:141-142` vs `:204-205`） | **我** |
| P1-12 | 单一 `_dpiScale` 换算全部坐标 ⇒ 混合 DPI 选区偏移 | **仍开** | `SelectionOverlay.xaml.cs:255-261` 用 `GetDpiForWindow(hwnd)` 取**一个**标量，而窗口几何就是整块虚拟桌面（`:248-249`、`:281`）；`:485-490` 及另 10 处都乘这同一个数。逐屏取 DPI 的代码不存在（`DisplayInfo.cs` 有 `MonitorFromPoint` 但属性里没有 DPI 字段） | lane |
| P1-13 | native 释放失败仍置 `_initialized=true` 且永不重试 | **部分修** | 失败已写 `LastError` + `CoreLog.Warn` 并由 `App.xaml.cs:191-200` 提到 Warn 日志；但 `NativeLibraryResolver.cs:140` **照样置 `_initialized = true`** ⇒ `:57/:60` 双检短路使本进程永不重试；释放目录仍是 `AppContext.BaseDirectory\native`（`:51`）无退路；恢复入口 `Reset()`（`:37-44`）**全仓零调用** | **我** |
| P1-14 | 换显示器 Dispose D3D 设备，session 仍持旧指针 | **已修（根因拆除），留一个窄口子** | 销毁触发点删掉：`WgcCaptureService.cs:671-683` 改成"试 `ImmediateContext` 探活，`_deviceMonitor` 仅作诊断记录"；`PooledSession._d3dDevice` 是 `readonly` 托管引用（`:141`）。残留：`GetOrCreateSdrSession` 复用分支 `:739-743` 不校验所持设备是否已 Dispose | lane |
| P1-15 | 捕获接口全同步却在 UI 线程 await，最长阻塞约 6 s | **已修** | `WgcCaptureService.cs:1012-1027` 与 `:1308-1316` 都 `await Task.Run(…Internal)`，且 `:1014` 用 `s_captureLock.WaitAsync(0)` 抢不到就失败不排队；上限常量可量（`FrameTimeoutMs` 3000 `:36`、GPU 锁 500ms `:485/:554`） | lane |
| P1-16 | 15 秒空闲窗口内每帧 GPU→CPU 回读（4K 约 132 MB/帧） | **仍开** | `IdleStopDelayMs = 15000`（`:81`）由每次截图末尾武装（`:1165` 单屏、`:1527` 多屏）；`OnFrameArrived` 的 HDR 与 SDR 两分支 `:280-322` **无条件**回读，没有"有人要帧才读"的门；`:546-550` 的注释自述只做了"少分配"（复用 `_writeBufferHdr`），没减回读次数 | **我**（常量与注释行；`:280-322` 的分支为 lane 取证） |
| P1-17 | 托盘 `Shell_NotifyIconW` 返回值不查、菜单 3 项硬编码中文 | **仍开（三点全在）** | `TrayIconManager.cs:99/103/125` 三处返回值全丢弃；菜单仍 `:163-167` 三项 + 一分隔线，字面中文直进 `AppendMenuW`；本地化扫描面是 WinUI 可视树（`MainWindow.Localization.cs:210`），**HMENU 结构上碰不到**，本文件对 `LocaleManager` 零引用 | lane |
| P1-18 | 输出目录/归档路径构造无异常处理，用户只看到原始异常文本 | **仍开** | `CapturePipelineService.cs:511` 与 `:527` 两次 `Directory.CreateDirectory` 裸调，且调用点 `:576`/`:622` 在 try 之外；全文件仅两个 catch（`:136` 取 ICC、`:168` 读 ACM），与路径无关。用户侧：`MainWindow.xaml.cs:1295` 状态栏 `$"❌ 保存失败: {ex.Message}"`、`:1296` → `ToastService.cs:137-143` 原样透传 | lane |

小结：**已修 8 条**（P1-1/2/3/9/10/14/15 + P1-5 的主干）、**仍开 6 条**（P1-4/6/12/16/17/18）、**部分修 3 条**（P1-7/11/13）、**1 条**（P1-8）确证仍开且已知正确分支存在。旧报告里"P1-10 体积翻倍"与"P1-9 位深无效"两条已被实现提到水线；`n<5` 的偶发一条也没写成因果。

**优先级要重排**：P1-4（静默覆盖）与 P1-6/P1-8（在途编码被强杀）现在是同一族——默认格式已是 PNG（快），连拍更便宜，撞名概率反而比 09-15 更高。

---

## 4. 本轮新发现（都是"判据看不见"那一类）

### N1 `ComboBoxItem.Content` 不在本地化扫描面内 ⇒ 7 个下拉的中文项预计原样留着（T2 静态量，缺 T3 目视）

`SweepSurface` 声明的 11 个槽位里没有 ComboBoxItem（`MainWindow.Localization.cs:290-322`，我逐行读过），下拉项的文字只能靠**逐个显式点名** `SetComboItemText(cbo, tag, text)`（`:397`）。可复算的尺已落成脚本：`tests/manual/combo_locale_coverage_measure.py`，读数 `RESULT named=19 zh_items=73 unhooked=7 unhooked_items=35 file_total_items=131`。

- 主窗口 19 个具名下拉含中文项，共 **73** 项；其中 **7 个下拉一次都没被点名**，合计 **35** 项：`BorderColorCbo`(8)、`OverlayColorCbo`(7)、`LogCategoryCbo`(6)、`LogFilterCbo`(5)、`LlmProviderCbo`(4)、`TranslationModeCbo`(3)、`AvifCodecCbo`(2)。
- `AvifCodecCbo` 还多一道硬挡：`MainWindow.xaml.cs:742` `if (item.Tag as string is not { } tag || tag == "Auto") continue;` ⇒ 动态标签刻意跳过 Auto，英文界面里那项仍是"自动"。
- **这组数的可信边界，脚本自己报了**：`<ComboBox>` 开标签 30、闭标签 29 不等 ⇒ 至少有一处自闭合或嵌套，脚本按"首个 `</ComboBox>` 切块"，所以**分块口径本身有待核**（脚本对这种情况会打印警告而不是静默给数）。绝对数 73 ≤ 全文 `ComboBoxItem` 131 这条自检通过——上一轮我曾因把 `<ComboBox` 前缀匹配到 `<ComboBoxItem` 而算出"1318 项 > 全文 116"的假数，那个哨兵现在写在脚本里。
- 这与 CHANGELOG `[0.3.6-beta]`"英文界面补完"的口径**不完全一致**——那句补完说的是对照表覆盖率与副窗口整窗两类，下拉项这一类不在它的判据面上。定性：T2（结构量）成立，**T3（真机英文界面目视）缺**。

### N2 那条"英文模式下仍有 N 处显示中文"的运行时告警，**结构上看不见 N1**（一把量程不足的尺）

`ApplyLocaleCollect(root, onlyResidue: true)` 的计数只走同一张 `SweepSurface`（`:248-264`）⇒ ComboBoxItem 永不计入；再加上折叠的下拉在 VisualTree 里未实化，即使纳入槽位也扫不到。⇒ **不能拿"N=0"当英文界面干净**的证据。注释自己承认过这个形状犯过一次（`:252-256`：原先用精确类型相等，导致 48 处 ToolTip 整类不计入、"报出来的 N 是假的低"）——N1 是同一把尺上的第二处漏。

### N3 `ShaderCompiler` 的两个子进程点：超时不 Kill，且进程未退出就读管道（新 P1 级）

`ShaderCompiler.cs:99` `proc?.WaitForExit(30000);` 之后 `:106` 直接 `StandardError.ReadToEnd()`；`:126` `WaitForExit(2000)` 同样无 Kill。fxc 挂住时既不杀也不报错，且管道读会二次挂死。同族两条：`NativeAvifEncoder.cs:289` 与 `AnimationRecorder.cs:300` 有 Kill 但没带 `entireProcessTree`（avifenc/ffmpeg 起子进程时杀不干净）。

### N4 `NativeLibraryResolver.Reset()` 是死入口

`:37-44` 提供了复位口，全仓 `NativeLibraryResolver.Reset` **零命中**（我自己复跑过这条 grep，并用同一命令对另一个函数名取到正样本 ⇒ 这个"零"是真的）⇒ P1-13 那条"失败后永不重试"其实已经有药，只是没人喂。补一条"提取失败 ⇒ 下一次取用前重跑"的接线即可，属小改。

### N5 色域显示名对未知 tag **静默报 Rec.709**（候选，待 D 批定性）

单一来源函数是 `Core/ColorManagement/ColorProfileProvider.cs:1524` `GetColorSpaceDisplayName`，六档映射清楚，但 `_ => "Rec.709"`（`:1532`）把**任何认不出来的 tag 都说成 Rec.709**；同族默认值另有一处 `Core/Encoding/FormatColorFacts.cs:93` `_ => "1(Rec.709)"`。§57 那一轮消掉的正是"三份互相矛盾的映射"，而"缺键回落成最窄的那一档"是同一形状的另一面：一旦盘上出现迁移表没覆盖的值（B 域手写 JSON、`ColorSpaceIndex` 白名单收 0–5 而下拉只有 3 项那条旧账），界面就会指着 Rec.2020 的产物说"Rec.709"。要判它成不成立，得先构造一枚真实的不认识 tag（D/E 批的 B 域格子里补）。

**D 批首件·定性完成（2026-09-28，T2 级：把全部生产者列出来，不构造输入）**——四个调用点的 tag 来源逐个查到了取值集合：

| 调用点 | tag 从哪来 | 实际可能的值 |
|---|---|---|
| `MainWindow.ColorManagement.cs:65` | `src.NativeGamutTag` ← `GetDisplayNativeGamutTag`（EDID 闸门 + ICC 矩阵兜底） | `sRGB` / `DisplayP3` / `AdobeRGB` / `BT2020`（`ClassifyGamutByXyzMatrix:1431-1434` 只有这四条 return；读不到一律 `"sRGB"`） |
| `MainWindow.ColorManagement.cs:83` | `d.TargetTag` ← `ColorDecision` | 同一套白名单（经 `CollapseSelectableTag` 收口） |
| `MainWindow.xaml.cs:467` | `GetSelectedColorSpaceTag()` ← 下拉索引 | 下拉 3 项 |
| `MainWindow.xaml.cs:1168/1174` | 同上 `tag` | 同上 |

而落盘形态是**整数索引不是字符串**：`SettingsService.cs:337` `if (s.ColorSpaceIndex is < 0 or > 5) = 0`，索引→tag 的表 `MainWindow.Settings.cs:98` `legacyTags = { System, sRGB, DisplayP3, DCI_P3, AdobeRGB, BT2020 }` —— **正好就是显示名那六条 key**。

⇒ 定性结论：**不是当前可达的缺陷**，手写 JSON 也注入不了不认识的字面串（只能注入 0–5 的整数，而那六个全有名字）。它是一处**维护性地雷**：将来加第七个 tag 而忘了加显示名时，表现是"界面把新色域说成 Rec.709"（最窄那一档），而不是"说不知道"。`FormatColorFacts.cs:93` 的 `_ => "1(Rec.709)"` 同形。
⇒ 所以原来那句"要构造一枚真实的不认识 tag 才能判"被这条读码路径替代了：**判可达性看的是生产者集合，不是构造输入**（构造出来的那枚根本进不了产品）。修法仍是一行（`_ => $"未知({tag})"`），要不要动由用户定；不动的话这条留在账上当地雷清单。

> 编号说明：**N6 记在 §5**——它是在做 SKIP 审计时才露出来的，不是读码阶段想到的。

---

## 5. 批次 C 首件：把基线里那 4 处 SKIP 定性（附一把锁当场接上）

计划里写的"C 批起第一条要办的"就是把 §1 那 4 处跳过逐条问清。跑对宿主后四条全部点名（明细只存在于测试宿主自己的 stdout，见 N6）：

| 套件 | 跳过的判据 | 它给的理由 | 定性 |
|---|---|---|---|
| unit | `MFT AV1 编码` | 本机无 AV1 MFT 编码器（QSV 回退软件编码） | **环境够不到**——与 §62.8"真 MFT 那一列本机量不到"同一条，换机才判得到 |
| service | `翻译链路端到端（必应）` | 需显式 `TTC_LIVE_TRANSLATE=1`（会向第三方发一条合成文本） | **设计上 opt-in**，诚实；不出本机流量是既定口径 |
| service | `ShaderLoader 字节码校验` | 测试环境无着色器产物 `data/Shaders/ToneMapping.hlsl.cso` | **接线断了** ⇒ 本轮已接上，见 §6.1 |
| usability | `ColorProfile: GetDisplayIccProfile(0)` | 无显示器（返回 null 属预期） | **形状错了**：传的是字面空句柄 `0`（旧 `UsabilityTests.cs:566-578`），本机接着真 HDR 屏也永远返回 null ⇒ 这条分支恒跳过。null 本来就是这条同步口的预期值（`ColorProfileProvider.cs:31-34`），预期值该断言不该算"没判" ⇒ **本轮已改成真断言并在门禁里判到**，见 §5.2(b) |

### 5.1 ShaderLoader 那条锁：从"永远不判"到"当场咬住"（T1 级，带牙）

- 现状取证：`.cso` 盘上有 5 份（`src/TrueToneCap.App/Shaders/`、App 的 Debug/Release bin、`publish/TrueToneCap-v0.3.6-beta/data/Shaders/`、`publish/_scratch/...`），头四字节实测就是 `DXBC`；而 `Core/Processing/ShaderLoader.cs:32` 只按 `AppContext.BaseDirectory/data/Shaders/` 找 ⇒ 测试宿主自己没有这份产物 ⇒ **09-15 那轮把它从"恒真断言"改成"诚实跳过"是对的（`ServiceTests.cs:117-119` 注释自述），但没人把它接上，于是 DXBC 魔数校验与缓存两条判据至今一次没判过。**
- 改动（一处，`src/TrueToneCap.Test/TrueToneCap.Test.csproj`）：新增 `AfterTargets="Build"` 的 `CopyCompiledShadersForTests`，把 App 的 `Shaders\*.hlsl.cso` 拷进宿主输出 `data\Shaders\`；**动态**添加而非静态 glob（`.cso` 是构建产物，静态 glob 在求值阶段展开会漏，这条教训写在 `TrueToneCap.App.csproj:47-48,106-107`）；拿不到时保留诚实跳过，并打一条 MSBuild warning 说明"本轮这锁没判上"。
- 效果：`--service-tests` 从 **4 通过 / 2 跳过** 变 **7 通过 / 1 跳过**（`tests/output/lock-fix-service.log`，RESULT 行 `passed=7 failed=0 skipped=1`），新增执行的三条是"返回非空字节码 / 具备 DXBC 魔数 / ClearCache 后重新加载内容一致"，其中**缓存那条以前只能断"两次都是 null"**。
- **全量门禁已复跑**（`tests/output/lock-fix-gate.log`，exit 0）：总数 **1365 → 1368**，与单套件推算的 +3 逐字对上（`--service-tests` 4→7，其余六套一条没动：unit 869 / color 213 / encoding 14 / ocr 12 / format 43 / usability 210）；跳过 **4 → 3**；构建 0 警告 0 错误；`[1.9/2]` 宿主新鲜度过（"2 个宿主都不早于 126 份源码"）。⇒ **此后引用基线用 1368，不是 1365。**（随后 §5.2 又落地两处修法，现值是 **1369**，以 §5.2 末尾为准。）
- **牙是量过的**（`tests/output/lock-teeth-service.log`）：把宿主输出里那份 `.cso` 的头四字节改成 `XXXX` ⇒ `RESULT ... passed=6 failed=1`、**RC=1** 并点名 `❌ ShaderLoader: ToneMapping 具备 DXBC 魔数`；随后用另存的副本还原并核对 sha1（`6cdd0fd66c8496d286a3d20f6ececd8e23fd1333`，与 `/tmp/good.cso` 逐字相同），重跑回到 `7 通过 / 0 失败 / 1 跳过`。

### N6 门禁的"跳过 N"不可追溯（新 P2 级，一条就够修）

`verify.ps1` 在**通过路径**上只印 `✅ 通过 (N 断言, 跳过 M)`，套件自己的 stdout 全丢；只有失败时才回显最后 8 行（`scripts/verify.ps1:800-823`）。⇒ 基线日志里那 4 处跳过**查不到是哪四条**，本轮是我逐个套件手跑才拿到名字（`tests/output/skip-audit-*.log`）。这与 §62.7 那条"新接一条依赖外部工具的判据，验收必须包含【判了】那一行"是同一族：**跳过必须点名，否则"N 处没判"这句话说不出内容**。修法是一行级的（`skipped>0` 时把 `⏭`/`⚠️ … 跳过:` 那些行一并回显）⇒ **本轮已修并在真实门禁路径上验到**，见 §5.2(a)。

### 我先前一条猜测被读码推翻（按规矩记下来）

看到"缓存有效"也进了跳过同族，我初判它是"null==null 的恒真断言"。回读 `ServiceTests.cs:147-175` 后发现 09-15 已经修过（注释自述"原断言 `first is null || ReferenceEquals(...)` 在 first 为 null 时恒真"），现在只在确有内容时才校验引用相等。⇒ **恒真的是"这条判据从没跑到"，不是"断言写错了"**——两者修法完全不同，先把代码读一遍再下结论省掉了一次误改。

### 5.2 两处一行级修法（②授权，实测读数）

**(a) N6 跳过点名**：`verify.ps1` 的通过路径原本把套件 stdout 整段丢弃，现在 `skipped>0` 时回显 `… — 跳过: 原因` 那些行，并带一条守卫（点名条数 ≠ `RESULT skipped=N` 就出声）。

- 生效证据：本轮门禁日志里第一次出现了点名行——
  `⚠️ MFT AV1 编码 — 跳过: 本机无 AV1 MFT 编码器（QSV 选项将回退软件编码）` 与
  `⏭ 翻译链路端到端（必应） — 跳过: 需显式 TTC_LIVE_TRANSLATE=1（会向第三方发一条合成文本），门禁默认不跑`
  （`tests/output/n6fix-gate2.log`）；守卫一条没喊（grep `有跳过项没写原因` = 0 命中）⇒ 2 处跳过、2 条名字，对得上。
- 改完先跑过解析自检（`[ScriptBlock]::Create` → PARSE OK）；文件仍是 UTF-8 无 BOM + LF。
- 一个纯观感的小瑕疵：CorePipelineTests 自己打 `⚠️`、另几套打 `⏭`，我的回显原本又叠了一层前缀（门禁日志里是 `⏭ ⚠️ MFT AV1 编码 — 跳过:`）。已改成只留缩进。⚠ **这条去叠只做到"读码 + `[ScriptBlock]::Create` 解析自检通过"，没有为它单独再跑一轮门禁** ⇒ 上面引的两条点名行读数（1369/2 跳过）来自去叠**之前**那一轮（`tests/output/n6fix-gate2.log`），数字不受影响，只有显示格式待下一轮顺带确认。

**(b) `GetDisplayIccProfile(0)` 从 Skip 改成真断言**（`UsabilityTests.cs:566-583`）：null 是这条同步口的**预期值**（`ColorProfileProvider.cs:31-34` 写明"缓存未命中就立刻返回 null、永不阻塞"），预期值就该断言而不是记成"没判"。现在断的是"空句柄返回 null（不回落成主屏 profile）"——回落成主屏 profile 是真实风险（会把 A 屏的 ICC 盖到 B 屏像素上），所以这条有内容。

- 读数：`--usability-tests` **210 通过/1 跳过 → 211 通过/0 跳过**（单套件跑 `tests/output/n6fix-usability.log`，门禁跑同样 211/0）。
- ⚠ **这条新断言的牙没量成**：想注入"空句柄吐 64 字节残缺数据"的变异夹具时，**权限分类器拦下了往产品源码里造故障这一步**，我没有绕过。所以它的判别力目前只是"条件直接来自被测返回值"这一层构造性论证，**没有实测记录**。下一轮要补的话，得先拿到"允许临时改产品源码做变异"的明确授权。

**基线因此变成 1369**（`tests/output/n6fix-gate2.log`：构建 0 警告 0 错误、7 套件全部通过、`GATE_EXIT=0`）：
869(unit，1 跳) + 213 + 14 + 7(service，1 跳) + 12 + 43 + 211 = **1369**，与汇总行逐字一致；跳过 **3 → 2**（剩下的两条都是"设计如此/本机没硬件"，不是接线断）。
⇒ **此后引用基线用 1369。**（⚠ 已被 §6.10⑦ 的 **1379** 取代；本节数字保留作当轮证据。）

### 顺带一次读数作废（真实事故，别学）

我第一次手跑 `--service-tests` 用的是 `bin/x64/Release/...` 那份**宿主，时间戳 2026-09-01**，得出"5 通过 / 0 跳过"，与门禁的"4 通过 / 2 跳过"矛盾。真因不是判据不一致，是我拿了旧二进制：盘上同时存在 `bin/Release`（23:44，门禁用的）与 `bin/x64/Release`（09-01）与 `bin/Debug`（16:34）三份宿主。`verify.ps1:704-709` 的选择逻辑本身是对的（先按 `$Configuration` 过滤路径段，再按 `LastWriteTime` 取最新）。⇒ 手跑任何套件前先 `find bin -name '*.exe' -printf` 看一眼时间戳，别默认"第一个找到的就是它"。



## 6. T3 读数（①授权：起发布包真身，本机首次）

跑的是 `publish/TrueToneCap-v0.3.6-beta/TrueToneCap.exe`（`build-info.json`：`commitSha=4368eed`、`dirtyWorktree=false`、`smokeTested=true`），三条无头入口都在建窗之前分派（`App.xaml.cs:73-81`）⇒ 不在桌面弹窗，也不抢单实例锁。

| 命令 | 退出码 | 结果 | 出处 |
|---|:--:|---|---|
| `--selftest` | 0 | **10/10 通过**，`failed=0` | `tests/output/t3-selftest.log` + `artifacts/t3-2026-09-28/selftest/selftest.json` |
| `--hdr-probe` | 0 | 4/4 通过 | `tests/output/t3-hdrprobe.log` |
| `--display-audit-emit` | 0 | 审计卡 + 逐屏两枚真 PNG | `tests/output/t3-audit.log` + `artifacts/t3-2026-09-28/audit/display-audit.txt` |

六格式在真身进程里各编了一遍（合成小图，非实拍）：PNG 19 ms/483 B、TIFF 4 ms/30 223 B、JPEG LI 559 ms/1 233 B、JPEG XL 527 ms/472 B、AVIF 922 ms/536 B、WebP 233 ms/446 B；另三条是迁移门与两条高精度源探针。

本机显示器态（结论绑定这一台，换机不可迁移）：**活跃显示器只有 1 块** `\\.\DISPLAY5` 3840x2160，`分类=Hdr`、10 bit/通道、色域 `RgbFullG2084NoneP2020`、峰值 418 nits、ACM=yes、EDID 原生 ΔG=0.0963。⇒ 多屏/混合 DPI 那几格本轮仍做不了。

### 6.1 一条方法学发现：T3 的**数值不可复现**，只有结论可复现

`HDR_SOURCE` 同一条判据连跑两次（间隔约 4 分钟），数字明显不同：

| 读数 | 第一次（`--hdr-probe`） | 第二次（`--selftest`） |
|---|---|---|
| 捕获耗时 | 856 ms | 570 ms |
| `>1.0` 通道占比 | 1.73 % | 2.91 % |
| raw 域落在 1/256 整格比例 | 49.92 % | 53.99 % |
| 中性像素去重量级 | **197** | **659** |
| 判定 | PASS | PASS（且第二次是靠"659 破 256 上限"这一支过的） |

根因不是抖动：它抓的是**当时那块活桌面**（`--hdr-probe` 与 `--selftest` 之间我这边窗口内容变了）。⇒ 规矩要写死：**引用 T3 数值必须带"采样时刻的桌面内容"限定，能引用的只有 PASS/FAIL 与阈值边距**；而"中性级数 197 一次、659 一次"恰好证明**这条判据的 PASS 分支不能靠单一维度**（它自己有两支：整格比例 与 中性级数/头部余量，两次靠的不是同一支——这是运气好，不是设计稳，值得在 D 批复核它的阈值）。

对照证据：`HDR_JUDGE`（合成台阶帧、无环境依赖）两次读数**逐字相同**（256 级→实判 FAIL，1024 级→OK，整格 100.00 %/50.98 %）⇒ 判据实现是确定的，飘的是输入源。这条对照把"抖动来自桌面内容"和"判据自己不稳"区分开了。

### 6.2 一次误判的撤回（我怀疑产物与声明打架，读码后收回）

审计卡正文写 `PNG：cICP 9/16/0…HDR 一律不嵌 ICC`，而它自己的"盘上读回"那一行给的是 `____DISPLAY5-System.png cICP=9/13/0/1 iCCP=834 B` —— 看着像一条 P0 级"声明与产物分叉"。**没有据此下结论**，先读 emit 代码：`SelfTest.cs:130` 取的是 `cap.SdrPixels`（8-bit 缓冲）、`:140` 写死 `OutputBitDepth = 8` ⇒ emit 面按构造就产不出 PQ 文件，`transfer=13(sRGB)+带 ICC` 对那张 8-bit 样本是**正确**的。而"HDR PNG 真产物"的正牌判据在别处且是真执行级的：`ColorPipelineTests.cs:598` 断 `cicp[1] == 16`、`:649` 断整串 `{9,16,0,1}`。⇒ 不是缺陷，撤回。

留下一条文档级 nit（P3）：审计卡把"分类=Hdr／HDR=写PQ"的决定行与一枚 8-bit SDR 样本并排印在同一节，读起来就像自相矛盾——要么在样本行注明"本枚走 8-bit 缓冲，不证 PQ"，要么让它与决定行分开。本轮未动。

### 6.3 T3 起进程带来的盘上副作用（如实记）

包目录里多出：`color-samples/____DISPLAY5-{System,BT2020}.png`（各 7 024 730 B）与 `log/app_2026-09-28.log` —— 无头那两轮时它是 52 行、grep `WARN|ERROR|⚠|❌` 零命中；**GUI 轮之后它长到 835 行，并且确实有 Warning 了**（`检测到以管理员权限运行 —— 系统通知不受支持`、`XamlControlsResources 加载失败 (非致命)`，见 §6.4 末），所以"零命中"那句只对无头轮成立，不能当本轮结论读。

设置面：起真身会**重写**同目录那份 `TrueToneCap.settings.json`（一轮 6 次 `设置已保存`），探针收尾时把它删掉了（原本不存在）；用户 `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json` 的 sha1 全程与 preflight 逐字一致（`40589a55…`）⇒ 起真身进程没碰到你的配置。
新增脚本 `tests/manual/gui-locale-clipboard-probe.ps1` 与 N1 的尺一样被 `.gitignore:99`（`tests/manual/*`）挡着，**未入 index**（本轮只 force-add 了那一个 py 文件，是③授权范围内的）。

### 6.4 英文界面目视（真起带窗口的会话，UIA 从进程外驱动）

探针：`tests/manual/gui-locale-clipboard-probe.ps1`（跑发布包 exe，UIA 枚举 + `PrintWindow(flag=2)` 截图，不依赖前台焦点、不动用户光标）。日志 `tests/output/gui-en3.log`、`gui-en4.log`、`gui-clip*.log`；证据件 `artifacts/t3-2026-09-28/gui/`（逐页 UIA dump、展开的下拉项、截图 `window-en.png` 1260x840）。

**先记两条我自己的错**（都差点变成假结论）：
1. 第一版探针用 `InvokePattern` 切导航页，五个页全部 `Unsupported Pattern`，而脚本照样报 `fail=0` ⇒ **一次彻底的假绿**。NavigationViewItem 要走 `SelectionItemPattern.Select()`；并补了"成功切页 <3 就判红"的下限守卫。
2. 剪贴板第一轮三个格式都报"空"，我几乎写成 P0。读日志才发现：4K HDR PNG 从 `01:44:09` 起跑编码，我**固定睡 14 s 就去读剪贴板**——那时还没走到复制那一步。改成轮询到产品自己写出"无感截图完成/已保存并复制/复制失败"（上限 120 s）。
3. 顺带抓到夹具自身的坑：我往同目录设置里手写 `FormatIndex=3/2`，产品启动后**又重写**了那份设置（日志里 `设置已保存` 一轮出现 6 次），实际自报 `捕获配置: 格式=PNG` ⇒ **B 域（手写 JSON）这条路进不去**，必须从界面选（A 域）。探针因此加了 `Pick-Format`：展开 `FormatCbo` 再用 UIA 选中目标格式，并把"产品自报的格式"写进读数。

**N1 的终审（英文模式，逐页展开下拉实测）**：

| 下拉 | 展开后可见项 | 其中含中文 | 静态预判（§4-N1） |
|---|---:|---:|---|
| `LogCategoryCbo` | 7 | **6** | 未点名 ⇒ 预计漏，实测确证 |
| `LogFilterCbo` | 5 | **5** | 同上，确证 |
| `BorderColorCbo` | 8 | **8** | 同上，确证 |
| `OverlayColorCbo` | 7 | **7** | 同上，确证 |
| `TranslationModeCbo` | 3 | **3** | 同上，确证 |
| `TargetLangCbo` | 14 | **2** | 点名过但按 Tag 匹配，仍有漏 ⇒ 确证 |
| `FormatCbo / ColorCbo / BdPngCbo / LanguageCbo / ThemeCbo / FontCbo / LogVerbosityCbo / MaxDurationCbo / AnimFormatCbo / OcrEngineCbo` | 4/3/4/2/4/50/3/4/3/3 | **0** | 点名过，实测确实翻了 |
| `LlmProviderCbo / AvifCodecCbo / ToastPositionCbo` | — | 未实化 | 所在子卡收起，UIA 拿不到 ⇒ **本轮判不了**，不是"没问题" |

⇒ 静态预测的 7 个未hook 下拉里，**5 个被运行时确证**（共 31 个中文项），1 个（`TargetLangCbo`）是另一种漏法（点名了但按 Tag 匹配不到），2 个本轮没实化。**N1 成立，且量级比静态数更准。**

**N2 的终审（那条告警第一次有了读数）**：日志里确实写出来了 ——
`01:40:37.819 [Warning] [系统/Locale] 英文模式下仍有 11 处扫得到的控件显示中文 ⇒ 对照表缺条目或扫描面缺控件  MainWindow.Localization.cs:241`

我这边跨四页去重量到的是**也是 11 条**界面中文串，但**两个 11 不是同一把尺**：产品的计数只覆盖"告警那一刻已实化的树"（启动页），我的是逐页扫完去重。数字相同是巧合，别当互相印证。这 11 条按来源分三类：

- `开` / `关` —— WinUI `ToggleSwitch` 的框架默认 On/Off 文案，跟随系统 zh-CN，**不是对照表缺条目**；要改得显式赋 `OnContent/OffContent`，而这两槽**不在扫描面表里**（`MainWindow.Localization.cs` 里 `OnContent` 命中 0）。我一开始把它写成"漏翻条目"，读了 XAML 才发现源 XAML 的 `OnContent` 全是空串 ⇒ 撤回。
- `就绪` —— 状态栏由代码赋值（`cs=8 / xaml=3` 处命中），扫描面只认 XAML 原文，管不到。
- 其余 8 条（审计卡、逐格式声明、灰字提示、降级说明，如 `WGC Float16 → 色域矩阵 → PQ ST.2084…`、`ACM 已启用 — 像素会映射到所选目标色域（Rec.709 / Rec.2020）。`、`→ HDR 直通（容器写 PQ）`、`无损 (固定 100%)`）—— **全部只存在于 `.cs`，`.xaml` 里 0 命中**（逐条 grep 核过）。

⇒ **英文界面漏翻的真实形状不是"§55 的对照表缺条目"，而是"运行时拼接的字符串整体在那套机制之外"**。这条比 N1 更值钱：它指出了修法的面（要么把这些串收进一张可翻的表，要么给它们单独一条机制），而不是补几条条目就完事。

**顺带一条运行期发现**（Logs 页里读到的，本轮没深查）：
`XamlControlsResources 加载失败 (非致命): 灾难性故障 (0x8000FFFF)` 与 `检测到以管理员权限运行 —— Windows 系统通知不受支持（Show 会静默失败），将回退应用内浮层`。
后者是环境（我的 shell 提权 ⇒ 起的实例也提权），不算产品问题；**前者**在发布包上每次都出现、被标"非致命"，属 E/F 批该抓的形状（资源库加载失败会不会让某些控件样式/本地化走空）。登记，待查。

### 6.5 剪贴板三面读数（同一探针，纠正后重跑，`tests/output/gui-clip2.log`）

上一版"三个格式剪贴板全空"是**我探针等得太短**造成的假信号。改成轮询到产品写出"无感截图完成"之后再读，三面都有数：

| 目标格式（界面选中 + 产品自报双证） | 编码路径与档位（**三行不同件，不可横比**） | 编码耗时（产品日志） | 产物 | 剪贴板通道 |
|---|---|---:|---|---|
| PNG | 托管 PNG 编码器 · 质量 **100.0（无损）** · 位深 10 · 色度 **444** | **22.9 s** | `E2E_fmt0_…​.png` 15 620.2 KB | FileDrop / FileContents / GroupDescriptor / **Bitmap + CF_DIB** |
| AVIF | **NVENC 硬件直通**（`AvifProbe: Nvenc: OK`、`实测能力 Auto→Nvenc`）· 质量 **15.0** · 位深 10 · 色度 **420** | 0.53 s | `E2E_fmt3_…​.avif` 760.9 KB | 同上，含 **Bitmap + CF_DIB** |
| JPEG XL | **cjxl 子进程** · 自报"质量 1.0"实为 **butteraugli distance**（命令行 `-d 1.0000 -e 9`，见 `NativeJxlEncoder.cs:232` ⇒ 近无损 + effort 9 最慢档）· 位深 10 · 色度 **444** | **46.5 s** | `E2E_fmt2_…​.jxl` 364.2 KB | 同上，含 **Bitmap + CF_DIB** |

⚠ **AVIF 那 0.53 s 一度被我写成"比 PNG 快 40 倍"式的对比，那是错的**——它快的原因是**换了后端**（NVENC 硬件直通）且色度是 420、质量 15，与另两行的托管无损 / cjxl 444 不是同一件。三件独立核过：

- 计时自洽：日志 `01:50:15.758 HDR 编码启动` → `01:50:16.280 HDR 编码完成` = 522 ms，与它自报 `总耗时=531ms` 同段（`CapturePipelineService.cs:646/721` 之间）。
- 产物不是回退件：外部 ffprobe 读那枚 avif = `codec=av1 3840x2160 pix_fmt=yuv420p10le color=bt2020nc/smpte2084/bt2020 range=pc` —— 真 10-bit HDR AVIF；且日志里没有 `回退`/`保底`/`位图通道不可用` 任何一条。
- 与仓里既有阶梯读数同向：交接 §62.9 量过 NVENC ≈ 0.12 s、libaom ≈ 22 s，而旧报告 P1-3/热格子 3 记的是"选 **444** 会静默 forfeit 硬件加速 ⇒ 从约 0.12 s 跳到约 22 s"。本轮 AVIF 色度是 420，硬件保住了，所以 0.5 s 量级符合预期。
⇒ **要横比 AVIF 快慢，必须锁后端与色度**（同一 420、同一 Nvenc，或同一 libaom 444），否则这张表会教出错误结论。真正的可用性结论只在"同件内"取：4K HDR 下**托管无损 PNG 要 22.9 s、cjxl 要 46.5 s**，这两条叠在 P1-6（并发无上限）与 P1-8（关窗口不等在途任务）上才是用户会撞到的代价。

⚠ 顺带登记一条**待查（本轮不下结论）**：AVIF 的质量轴在不同后端上方向可能不一致。产品自报 `质量=15.0`，而编码器入口形参叫 `crf`（`FormatEncoders.cs:721`、`:746`、`:827`、`:1020` 的 `EncodeAsync(…, int crf, …)`），NVENC 侧则收 `qp`（`NvEncoderNative.cs:635 EncodeAv1(bgra, w, h, int qp, int bitDepth)`、`:717 EncodeAv1Argb10(pq10, w, h, qp)`），avifenc 侧另有 `NativeAvifEncoder.cs:303 CrfToQuality(int crf)`。QP 越小质量越高、而界面上"质量"越大越好的直觉 ⇒ 这两根轴是不是真反了、`15` 在硬件档上实际给到哪一档，本轮**没有量**（记忆里也记过"avifenc -q 与 QualityAvif 是两根反向轴"）。E 批热格子该补一格：同一 `质量=15` 分别在 Nvenc / libaom 下，产物字节、读回 QP/码率与外部观感指标都要给读数，不许用推理交差。

- 关键一条（重新独立数过，不靠 `tail`）：本轮日志 `app_2026-09-28.log`（86 667 字符）里 `剪贴板位图通道不可用` **0 命中**、`复制到剪贴板失败` **0 命中** ⇒ `SetBitmap` 对 AVIF / JXL **也没抛**（`MainWindow.xaml.cs:1548-1561` 的注释预期"系统剪贴板未必能解码这些格式"在本机不成立）。⇒ 09-15 P0-B 的位图通道在真身进程里三面都接通了。
  ⚠ 同一文件里 `回退` 有 20 处，逐条看过**全部不是 AVIF 编码保底**：10 处是 `QSV: 文件有效但并非硬件产出（已回退软件或标志缺失）→ 判为不可用`（能力探针判定，`AvifHardwareProbe.cs:154`），10 处是 `检测到以管理员权限运行 …将回退应用内浮层`（通知通道，`ToastService.cs:93`）。本轮共起 **13 次进程、6 次无感截图**——**为什么这两条各只出现 10 次而不是 13 次，本轮没查**（大概是某些入口没走到那两处），不解释就记成未决。
- ⚠ **但这只证明"通道在"，不证明"粘进去成像"**：目标应用拿到 AVIF/JXL 的位图 rendition 是不是正确像素，要真粘一次才知道（资源管理器也可能自己解码生成缩略 rendition，这一层我无法从进程外区分）。所以可用性表里"剪贴板"这一格仍是**待眼睛**，不给字母评级。
- **顺带一条可用性实测**（之前只有估的）：无感截图在 4K HDR 屏上，**托管无损 PNG 要 22.9 s、cjxl 要 46.5 s** 才完成——而 P1-6 说编码并发无上限、P1-8 说关窗口不等在途任务 ⇒ 用户连按热键的真实代价比旧报告里写的更具体。D4 场景表"无感连拍"那一格现在有数了。
- 顺带一条默认值事实：探针没写任何质量键，产品自报的是 `AVIF 质量=15.0 / JPEG XL 质量=1.0 / PNG 质量=100.0`，与 `--selftest` 里那条迁移门读数一致（"把 63.00/50.00/3.00 **交还为 15.00/90.00/1.00**"）⇒ 这三行的档位是**当前默认表给的**，不是我挑的；AVIF 默认 15 + 420 也解释了它为什么走得到硬件档。
- 两处探针自身的坑也记一下：① `Read-LogSilent` 返回 `,@(...)` 套了一层，`$done.Count` 对包装数组求值可能非零而 `foreach` 打不出东西（"等待 N s；收尾行："后面是空的），读数恰好与完成时刻对得上才算没骗人，但**形状是错的**，下轮要改成扁平返回；② 产物计数被上一轮（错误版）留下的 `shots-fmt*` 目录污染（报"2 枚"而日志只有 1 枚完成行）⇒ 每轮该用一次性目录名，且**以日志完成行为准，不以目录文件数为准**。

### 6.6 JPEG 的几种表现：增益图 关 / 开RGB / 开灰度（本轮新测）

同一台 HDR 屏、同一入口（无感截图 Ctrl+Shift+Q），三档各跑两遍；控件是真点出来的（`GainMapEnableChk` 用 UIA `TogglePattern`、`GainMapModeCbo` 用 `SelectionItemPattern`），产品自报的格式与档位都进了读数。**外部裁判**：`tests/manual/jpeg_anatomy.py`（逐段扫 marker，带 cjpegli 负样本自检）与 `tests/manual/jpeg_pixel_probe.py`（ffmpeg 解码后读像素电平）。

| 档 | 产品自报 | 容器结构（外部读） | 文件大小（两遍） | 耗时 |
|---|---|---|---|---|
| 增益图**关** | `JPEG_LI HDR=False/BlockedByDisplay 质量=1.0 位深=8 色度=420 色域=BT2020` | **SOF2 progressive**、ICC_PROFILE 2,114 B、**无 XMP/MPF、SOI=1** | 53,279 / 169,500 B | 518 ms / 1,030 ms |
| 增益图**开·RGB** | `JPEG_GAINMAP HDR=True/GainMap 质量=1.0 位深=8 色度=420 色域=BT2020` | **SOF1 extended**、ICC 2,114 B、**XMP 1,106 B（`http://ns.adobe.com/hdr-gain-map/1.0/`，标签 GainMapMin/Max·Gamma·HDRCapacityMin/Max）+ MPF 88 B 在 APP2**、**SOI=2** | 1,291,285 / 1,253,072 B | 1,056 ms / 1,024 ms |
| 增益图**开·灰度** | 同上（模式=灰度） | 同上，SOI=2 | 1,144,469 / 86,535 B | 1,057 ms / 967 ms |

按第二个 SOI 的位置拆开：RGB 档 = 底图 1,284,711 B + 增益图 6,574 B；灰度档 = 底图 1,142,195 B + 增益图 2,274 B ⇒ **灰度增益确实只有单通道体积**（2.3 KB vs 6.6 KB，约 1/3），这一档开关真进了码流（T1 级因果，`JpegLiNative.cs:138/287` 与 `JpegGainMapEncoder.cs:361/380` 的 RGB/Gray 两条实现都被产物字节印证）。

三件与"表现"直接相关的确证：

1. **关增益图 ⇒ HDR 落空成 8-bit SDR**（`HDR=False/BlockedByDisplay`），且容器走 **progressive**；开增益图为了 Ultra HDR 兼容性强制 **baseline/extended**（`JpegLiNative.cs:131-134` 注释就这么写）。⇒ 同一种"JPEG"在开关两侧**不是同一种容器**，将来若有人拿 progressive 的读回去验证增益图，会在错误的容器上验。
2. **剪贴板三档都通**（`CF_DIB`/`Bitmap`/`FileDrop` 齐，且 `剪贴板位图通道不可用` 与 `复制到剪贴板失败` 在两遍日志里 **0 命中**）⇒ 增益图开不开都不影响剪贴板可用性。
3. **两遍之间文件大小差很多**（off 53 KB→169 KB；灰度 1.14 MB→86 KB），而产品档位完全相同 ⇒ 差异全部来自**当时桌面内容**，与 §6.1 同源：活桌面上的读数不可复现，能复现的只有结构（marker/命名空间/SOI 数）。

### 6.7 一条差点写错的结论：那枚黑帧是 1/9 的偶发，而且我的对照夹具自己先崩了

- 现象：第一遍 off 那枚 53 KB，外部解码后 **99.78 % 的采样通道值 ≤ 2、均值 0.14、max 236** ⇒ 近乎全黑。我一开始据此写"关增益图 ⇒ 黑帧"。
- 反证 1：同配置再跑 1 次 + 灰卡下连拍 8 次 ⇒ **9 次里只有那 1 次是黑的**，8 次连拍均值 36–65、全部有内容。
- 反证 2（更关键）：我做了"已知亮度全屏卡"的端点实验，结果**夹具不自洽** —— 白卡(255) 截出来主带落在 96–127（均值 99.85），灰卡(128) 截出来主带在 128–159（均值 145）⇒ **亮度非单调**，卡没起作用或帧里根本不是卡。按项目规矩（夹具不过自检就不给结论），**这组亮度数全部作废**，"截图偏暗/色调映射档位"这个问题**仍是未决**，不是已排除。
- 因此本轮只成立三条：**① 黑帧是偶发（1/9），不能写成代码路径的因果；② 现有活桌面夹具不足以判亮度类问题，得先给夹具加一条"请求色值 vs 帧内主带"的成立自检；③ 无感截图路途中一旦成黑帧，产品侧没有任何"整帧近乎全黑"的自查**（`--selftest` 的 `HDR_JUDGE` 有合成台阶帧自检，但真实截图路没有电平合理性判据）⇒ 属该补的一块，登记进 D/E 批，本轮不动产品代码。

### 6.8 专项：增益图有全分辨率 / 1/2 / 1/4 吗？能不能拿它省体积？

**① 实现只有一个档。** `JpegGainMapEncoder.RescaleGainMap`（`:508-514`）硬编码 `(w+3)/4` 的 4×4 箱式平均 ⇒ 增益图恒为底图的 **1/4 边长 = 1/16 面积**；没有 1/2、没有全分辨率，也没有设置项或 UI 旋钮。实测印证：4K 截图里 ffprobe 读到增益图 `960x540`，RGB 档 `yuvj444p`、灰度档 `gray`（YCbCr400 单通道）。

**② 格式与参考实现都允许别的档，且改因子不用动元数据。** libultrahdr 的 `uhdr_enc_set_gainmap_scale_factor` 合法范围 `(0,128]`（`ultrahdr_api.cpp:643`）；通用默认是 `kMapDimensionScaleFactorDefault = 1`，但 **JPEG 这条路 `jpegr.h:55` 的构造默认是 `kMapDimensionScaleFactorAndroidDefault = 4`** ⇒ 我们的硬编码 4 就是参考实现对 JPEG 的默认档，不是随手取的。分辨率差是自描述的：解码端 `jpegr.cpp:1659` 直接 `resize_image(gainmap, sdr.w, sdr.h)` 吸收，MPF 只记偏移与字节数、尺寸由增益图自己的 SOF 给（本轮外部读数即证）⇒ 真要加档不必改 XMP/MPF 布局。

**③ 但它省不了体积。** 同一 cjpegli、同 `-d 1.0 --progressive_level 0`，同内容改分辨率实测：

| 增益图内容 | 1/4（现状） | 1/2 | 全分辨率 |
|---|---:|---:|---:|
| 全零（本轮桌面无超纸白内容 ⇒ 正是这种） | 6,399 B | 24,579 B | 97,479 B |
| 平滑渐变（真实照片增益的量级） | 14,638 | 44,475 | 154,503 |
| 噪声（上界情形） | 365,025 | 1,448,702 | 5,793,570 |

每降一档因子字节约 ×4（面积律）。实测产物里增益图占比：**RGB 档 6,574 / 1,291,285 = 0.51 %**，灰度档 2,274 / 1,144,469 = 0.20 %（底图很小的一遍里是 2.6 %）。⇒ 从 1/4 再降到 1/8 只省 1.6–3.7 KB = **文件总量的 0.1–0.3 %**；反过来做全分辨率要给 **+8 %**（底图小的时候是 +115 %）**⚠ 这一行的两个数是当时用合成件推的下界，⑧ 已用真跑的实测把它替换成 rgb +10.1 % / gray +3.6 %，以⑧为准。）⇒ **方向是反的：1/4 已经是省体积那一档，往下再省不出东西。**

**④ 想省体积要动的是底图距离。** 把这枚 4K 底图解回 ppm 再用同一个 cjpegli 重编（`--progressive_level 0`）：`-d 1.0` 1,316,981 B（与原文件 1,246,498 B 同量级，重编略涨是正常的——二次编码）→ `-d 2.0` 774,500 B（**−41 %**）→ `-d 3.0` 623,744 B（−53 %）→ `-d 4.0` 514,399 B。而 `QualityJpegLi` 与 `QualityGainMap` 默认都是 **1.0**（butteraugli 距离，0.5–3.0，越小越清晰）。⇒ "JPEG 太占体积"这句话的正确落点是底图距离，不是增益图分辨率。

**⑤ 一处可议的绑定（不是缺陷）。** `JpegGainMapEncoder.cs:234` 让增益图距离 = `settings.Quality`，即**沿用底图那个数**；libultrahdr 的 API 是按 intent 分开给质量（`m_quality[UHDR_GAIN_MAP_IMG]` vs `[UHDR_BASE_IMG]`，`ultrahdr_api.cpp:1112/1338`）。增益图是低频信号，给到 d=3–5 视觉代价很小 —— 但按上面的占比，最多也就省零点几个百分点 ⇒ 进 E 批"值不值得动"清单，不单独开工。

**⑥ 方法论一条（我自己差点用错数）。** 两枚不同时刻的增益图解码后 **sha1 相同、整帧为 0**，我第一反应是"增益图没算出来"。核过才对上：`HDR_SOURCE` 两次都读到 `max=1.5000`，而系统纸白 120 nits 配 `nits = lin × 80` ⇒ 1.5 恰好就是纸白，**当时桌面没有任何超纸白内容 ⇒ 增益图恒 0 是正确结果**（0 值图在 `-d 1.0` 下仍占 6.4 KB / 2.3 KB）。⇒ 用真实桌面截图量的增益图字节是**下界**，所以③的表把三种内容都列出来，不拿最好看的数当结论。

**⑦ 用参考实现当裁判，顺手否掉我自己正要写的一条"互操作缺陷"。** 读 MPF 布局时发现两处偏移用了不同基址（IFD 偏移写 8 = TIFF 相对，MPEntry 写 54 = 从含 4 字节 MPF 签名起算；而 libultrahdr `multipictureformat.cpp:38` 写的是 `kMpEndianSize + sizeof(kMpfSig)` = 12），我正要把它写成"严格按 TIFF 基址的读者会找错位置"。没写，先去问权威实现本身：`ultrahdr_app -m 1 -j <件> -P`（探针模式，专报增益图元数据）。

```
开增益图·RGB   ⇒ Ultra HDR Image: Yes，maxContentBoost 3.4822 / minContentBoost 1 / gamma 1 / offsetSdr=offsetHdr=0.015625
开增益图·灰度  ⇒ 同上（Yes + 完整元数据）
关增益图       ⇒ input uhdr image does not contain gainmap image（RC 非 0）⇒ 负对照成立
```

⇒ **我们的 Ultra HDR 封装被 libultrahdr 的解码器读通了**，那条基址疑虑不成立为缺陷（它至多是"与参考实现不同的写法恰好被同一实现接受"，不是 bug）。同时这里也解释了 ⑥ 的那个"看着矛盾"的组合：全零增益图 + `maxContentBoost 3.4822` 并不冲突 —— 后者是**声明的容量上限**，每像素的实际提升由 map 值给出（全零 ⇒ 无提升）。
⇒ 可复用的一条：**`-P` 探针是 Ultra HDR 最便宜也最硬的常驻外部裁判**（记忆里"别拿自家 Base 当参照"那条的正解），E/F 批的增益图判据该接它。

**⑧ 全分辨率增益图的实际体积（产品真跑出来的，不是推算）。** 做法：在 `artifacts/wt-gm` 里开一个 detached worktree（基线 `ca7ed99`，与工作树同一提交），只在**那一份**里把 `RescaleGainMap` 的因子改成可读 `TTC_GM_FACTOR`（默认仍 4），`dotnet build` 出 App（50.7 s，0 警告 0 错误），再用同一探针按因子 4 / 2 / 1 各拍 rgb 与 gray 两档，外部拆件量字节。**主工作树的产品源码一行未动**（`src/.../JpegGainMapEncoder.cs:512` 仍是 `(w + 3) / 4`，`git status` 只有本轮那三处验证侧改动）。

| 因子 | 增益图尺寸 | rgb 档增益图 | 占全文件 | gray 档增益图 | 占全文件 |
|---:|---|---:|---:|---:|---:|
| 4（shipped 现状） | 960x540 | 6,574 B | 0.76 % | 2,274 B | 0.27 % |
| 2 | 1920x1080 | 24,754 B | 2.74 % | 8,334 B | 0.96 % |
| 1（全分辨率） | 3840x2160 | **97,654 B** | **10.14 %** | **32,634 B** | **3.61 %** |

- **对照组先过**：因子 4 跑出来的增益图是 `6,574 / 2,274 B`、`960x540`、`yuvj444p / gray` —— 与发布包那两枚**逐字节相同**，说明 worktree 构建、环境变量入口与拆件量法三者都没引入偏差，另外两档才敢用。
- **③里那条推算被实测校验**：我上一轮用"把 960×540 放大再编"估出全分辨率 ≈ 97,479 B，实测 97,654 B ⇒ 差 0.18 %。当时它只是下界估计，现在是可以直接引用的数。
- 每降一档因子的增益图体积按 ×4 走（面积律），与合成曲线一致；但**占比涨得比字节更凶**：全分辨率时 rgb 档整文件从 857 KB 变 963 KB（+12 %），因为底图本身也在 850–880 KB 之间飘（活桌面，见 §6.1）。
- **这仍是最好情况。** 本轮桌面无超纸白内容 ⇒ 增益图整帧为 0（⑥），全分辨率也只是"把 830 万个 0 编成 JPEG"就要 97 KB。真拿到 HDR 场景（增益值逐像素变化）只会更贵 —— SDR 窗口封顶在纸白，本机没法凭空造出超纸白内容，所以"真实 HDR 场景下全分辨率增益图多少字节"这一格**本轮没量到**，要量得先接一路真 HDR 源（HDR 视频或游戏）。
- 结论没变但有了硬数：**把增益图改成全分辨率，代价是 rgb 档 +10 %、gray 档 +3.6 % 的文件体积，收益在截图这条路上是零**（截图工具绝大多数帧根本没有超纸白内容）。反过来 1/8 也省不出东西（0.1–0.3 %）。⇒ 因子 4 是正确的落点，这条可以结掉。
- 复现路径：worktree 在 `artifacts/wt-gm`（被 `.gitignore` 挡着，502 MB），要清就 `git worktree remove --force artifacts/wt-gm`；本轮先留着，因为它同时是"黑帧偶发率"和"因子/距离"两组实验的载体。

### 6.9 仍然缺的 T3 面

英文界面目视与剪贴板"有没有进去"两面本轮已跑到（§6.5）。剩下的：
① **粘进真实应用的效果**——探针只能证明剪贴板里有哪些格式通道，证不了"粘到微信/Word/画图里成不成像"，那要人眼；
② **overlay 与热键冲突回退链**的实际交互（选区框、两套 overlay 的键鼠处理）——同样要在你桌面上真操作；
③ 多屏/混合 DPI 那几格——本机现在只有 1 块活跃屏，做不了。

### 6.10 专项：截图尺寸除不尽 4 时，增益图怎么处理（本轮新测，并补了一条门禁断言）

**① 这条路真的会走到，不是假想。** 选区是鼠标拖出来的整数矩形，`MainWindow.xaml.cs:1991-1998` 直接把 `new RectInt32(rx, ry, rw, rh)` 交给裁剪，而裁剪函数 `ExtractHdrRegionFromDesktop`（`:1720-1741`）**按 `rw`/`rh` 逐字搬**（`:1731` `new float[rw * rh * 4]`，`:1738` 每行拷贝长度 `rw * 4`），**全程没有偶数化/对齐**；只有"整屏"那一档才是规则尺寸。⇒ 任意 `w×h`（含两边都奇数）会直接进增益图编码器。

**② 实现的答复：向上取整 + 边缘半块照平均。** `JpegGainMapEncoder.RescaleGainMap`（`:508-542`）：`:512-513` `(w+3)/4`、`:524-525` `ex=Math.Min(sx+4,w)`（最后那块只有 1~3 个源像素也收）、`:540` `sum/count`（除以**实际**像素数，不是固定 4）。链路上别处都不假设除得尽：增益在满幅 `w×h` 上算（`:226`），底图尺寸原样交 cjpegli（`:207`），封装只写偏移与字节数（`:605-626`）。

**③ 尺寸根本不进元数据 ⇒ "奇数"靠自描述而合法。** `BuildXmpMetadata(baseW, baseH, gmW, gmH, …)` 收了四个尺寸参数**一个都没用**（`:830-876` 通读：XMP 里只有 `Container:Item` 的 Mime/Length 加 `hdrgm` 那组数）；MPF 的 ImageData 元素是 16 B 的 attribute/size/offset，**没有宽高**。⇒ 读者只能各自解出增益图自己的 SOF。这既说明 ceil/floor 都不构成"声明与实现矛盾"，也说明另一件事：**尺寸写错了不会有任何元数据跟它打架**，外部只能从 SOF 看出来（所以⑦那条断言只能自己钉）。
- 一条现成的旁证：既有测试 `ColorPipelineTests.cs:1605/1630/1633/1705` 给的是 `BuildXmpMetadata(1920, 1080, 481, 271, …)` —— 1920×1080 的 base 按 `(w+3)/4` 只会得到 **480×270**，那对 481×271 是**凭空写的**。它一直绿，恰好证明这四个参数没有落到任何字节上（不是我把判据放宽了；本轮没动这几条）。
- 缺席结论的正对照（按项目规矩，负断言的 grep 要先量到一份真的有）：`grep -rn RescaleGainMap` 在 `JpegGainMapEncoder.cs` 命中 2 处、在 `src/TrueToneCap.Test/` 只命中我本轮新写的那两行注释 ⇒ "Test 里没人钉过几何"是量出来的，不是猜的。

**④ 与参考实现的分歧只在取整方向。** libultrahdr 用 `map_width = image_width / factor`（**向下**，`jpegr.cpp:694-695`），并带一条兜底：任一维取整后为 0 就自动减小因子（`:696-705`）⇒ 我们的 `3×2→1×1`、`1×1→1×1` 在它那里表现为"因子降到 1"，不是产出 0×0。解码端按**实际比例**重采样：`jpegr.cpp:1673` `map_scale_factor = sdr.w / gm.w`，`gainmapmath.cpp:871-883` 双线性并对 `map->w-1` **钳位** ⇒ 越界被钳住，读不到脏数据。

**⑤ 实测（产品编码器真跑出来的，夹具=最右一列 + 最下一行放超纸白 255×3 scRGB，其余 100）。** 外部读数脚本 `artifacts/t3-2026-09-28/gm_edge_read.py`（Pillow 独立解码那枚增益图，不走仓库任何解析器），件在 `artifacts/t3-2026-09-28/geom/`：

| 源尺寸 | 增益图 | 末列均值/最大 | 末行均值/最大 | 内区最大 | 全图最大 |
|---|---|---|---|---:|---:|
| 64×64（除得尽，对照） | 16×16 | 7 / 13 | 7 / 13 | 2 | 13 |
| 129×77 | 33×20 | 28 / 28 | 27 / 28 | 0 | 28 |
| 1921×1081 | 481×271 | 28 / 28 | 27 / 28 | 1 | 28 |

- **除得尽那档**说明的是另一件事：1 px 宽的边缘高光被摊进一整块 4 px ⇒ 末列 = 28/4 = **7**，角块 7 亮/16 ⇒ 12.25→**13**。这是**任何 1/4 增益图共有的性质，与奇偶无关**（`sum/count` 的算术在这里逐位对上，说明降采样本身没有偏差）。
- **除不尽那两档**：末列拿到的是**满值 28** —— 它那一块本来就只有 1 个源像素 ⇒ 向上取整保住了"最右那一列有自己的增益样本"。
- **反证（同一夹具换成向下取整）**：129 宽只留 32 列 ⇒ 最右那 1 列没有样本；解码端 `x_map = 128/(129/32) = 31.75`，钳到 31 ⇒ 读到的是全暗的内块 ⇒ **这一列的 HDR 提升整个丢掉**，而底图又在纸白封顶 ⇒ 观感就是被削平。⇒ ceil 不是随手写的，它买的是**边缘覆盖**。（这条与走哪条重采样分支无关：129×77 的 floor 件 AR 差 0.53 % 走比例采样、ceil 件 1.51 % 走 resize，但"有没有那一个样本"在采样之前就已经由尺寸定了。）
- **位置漂移这一维是平的**：`x_map = x·mapW/W` 对理想 `x/4` 的偏差 = `x·(mapW − W/4)/W`，上界 **0.75 个增益样本 = 3 个源像素**（ceil 取正、floor 取负）。差别不在准不准，在上面那条有没有样本。
- **小选区会换一条解码分支**：libultrahdr 先比长宽比（`jpegr.cpp:1653-1659`，容差 1%），超过就 `resize_image()` 双三次重采样到满幅（`editorhelper.cpp:100-142`，越界同样钳位 ⇒ **是支持的路径，不是错误**）。穷举 1..2000×1..2000：**两边都 ≥300 的组合里 0 个触发**（2 893 401 个全过），能触发的最长短边是 297（如 297×300）⇒ 只有很小的选区走 resize，走哪条都能还原，只是插值方式不同（resize 那条会把边缘样本摊到邻近像素）。

**⑥ 奇数尺寸对底图那一半也安全（三个独立解码器读回）。** cjpegli 编 1921×1081 @420/422/444 的 SOF 都写 `1921x1081`；ffprobe 读回 `yuvj420p bt470bg`；Pillow 解出 1921×1081；**WIC**（Windows 自带解码栈，资源管理器/画图同一条路）本轮补跑成功：三档都 `1921x1081`、右下角那颗像素 = `128,128,128`（等于源值，`artifacts/t3-2026-09-28/probe-wic-odd.ps1`）。⇒ 4:2:0 的"除不尽"只决定色度怎么取边，不影响尺寸可逆，也没有哪家解码器要求偶数。（⚠ 这个探针第一版传了个不存在的 `BitmapCacheOption` 值 `'CacheOnLoad'`，三条全走 catch，报的是"失败"不是"读回"——修成 `OnLoad` 才有上面的读数；又一次"看到红要先确认夹具自己站得住"。）

**⑦ 门禁此前对这条几何零断言 ⇒ 本轮补了 10 条。** 此前 `EncodingIntegrationTests` 只判"双 SOI + XMP + ISO 存在"，`UsabilityTests` 只判"文件 > 0 B"，**没有任何一处钉过尺寸**。新增 `UsabilityTests.GainMap_OddDimensionGeometry`（5 档尺寸 × 2 条）：底图 SOF 必须等于源尺寸（防"被偶数化"）、增益图 SOF 必须等于向上取整（防改成 floor 或改因子）。
- 读数：`--usability-tests` **211 → 221 通过 / 0 失败 / 0 跳过**（`tests/output/geom-usability2.log`：`RESULT suite=--usability-tests passed=221 failed=0 warnings=0 elapsedMs=23164 skipped=0`，构建 0 警告 0 错误）。
- **整条门禁复跑过并以此为新基线**（`tests/output/geom-gate2.log`，`GATE_EXIT=0`）：
  869(unit，1 跳) + 213 + 14 + 7(service，1 跳) + 12 + 43 + **221** = **1379 断言 / 2 跳过**，与汇总行"共 1379 断言 (43.3s)"逐字一致；跳过仍是那两条名字（MFT AV1 / 必应翻译），点名数与 `skipped` 对得上。
  ⚠ 第一次跑（`geom-gate.log`）是 `[1.9/2]` 宿主新鲜度判红 ⇒ 整轮作废：我只改了 Test，MSBuild 合理跳过 Tools 宿主，它的时间戳落后（记忆⑱③那条已知假红）。按它给的修法 `--no-incremental` 全量重编（0 警告 0 错误）再跑才是上面这轮。**上一轮记的 1369 因此过期，此后引用基线用 1379。**
- **牙（构造性论证，仍未做变异实测）**：这 5 档里 `129×77`、`1921×1081`、`3×2` 三档的 ceil 与 floor 结果本就不同（33×20 vs 32×19、481×271 vs 480×270、1×1 vs 0×0）⇒ 把 `(w+3)/4` 改成 `w/4` 会让至少三档当场判红，不需要额外注入故障。这是本轮第一条**不用变异就有牙**的几何断言。
- 没进门禁的那半：边缘那一列"是不是满值"只有⑤这份外部读数（要断它得在 Test 里解 JPEG 像素，代价不划算）。
- **参考实现对这五档奇数/退化尺寸全部认可**（外部裁判 `ultrahdr_app -m 1 -j <件> -P`，退出码不接管道直接取）：`64×64 / 129×77 / 1921×1081 / 3×2 / 1×1` 五枚产品真件全部 `Ultra HDR Image: Yes` + 完整增益图元数据、`RC=0`；同一命令对一张**没有增益图**的普通 1921×1081 JPEG 报 `input uhdr image does not contain gainmap image`、`RC=127` ⇒ 对照成立，"Yes"不是恒绿。⇒ 连 1×1 这种"整帧不足一个 4×4 块"的截图都能被 libultrahdr 读通（我们产出 1×1 的图，它自己的兜底是"取整为 0 就减小因子"，两边走法不同但都合法）。

**⑧ 一句结论**：除不尽**不需要对齐、不需要裁边**，现在的向上取整是三种写法里唯一保住边缘覆盖的；两个代价都不构成缺陷 —— 单像素边缘高光被 4× 平均削到 1/4（与奇偶无关，是 1/4 档的固有性质），短边 <300 的小选区会让参考实现换走 bicubic resize 分支（同样被支持）。

## 7. 下一步（按计划的批次序）

1. **批次 B 首件已落地并跑到 T3**（§8 + §8 的 T3 读数：差分随输入翻转、`GainMapMode` 四格跟动、`EXIT=0`、收尾夹具还原且设置未被改动）。**B 批剩下的**：`landingFailures` 那条红路径仍未反向验过（要"故意喂错"的一次起进程授权或变异授权）；`--selftest` 的 case 表仍只喂合成 96×96 台阶帧，"用户所选"目前只进了审计卡这一条路。
2. **批次 C/D/E/F**：六条 lane 深读 + 全矩阵三层读数 + 20 热格子 + 降级台账。G1/G2 要到 F/H 才谈得上回答。
3. ~~两处一行级的修~~ ⇒ **本轮已做完并在真实门禁路径上验到**（§5.2：N6 跳过点名 + `GetDisplayIccProfile(0)` 改真断言；基线因此 1369 / 2 跳过）。
4. **T3 无头面 + 带窗口面都已跑到**（§6：`--selftest` 10/10、`--hdr-probe` 4/4、`--display-audit-emit` 审计卡；§6.4 英文界面目视已终审、§6.5 剪贴板三格式已读）。**仍缺的 T3** 见 §6.9：粘进真实应用的效果、overlay/热键冲突回退链的实际交互、多屏/混合 DPI（本机只剩 1 块活跃屏，做不了）。
5. **一条要带回 D 批复核的判据设计问题**（§6.1）：`HDR_SOURCE` 两次 PASS 靠的是**不同的支**（一次靠整格比例、一次靠中性级数 659 破 260 上限），而它的 SKIP 带很宽（整格 95 %–99 % 之间一律 SKIP）⇒ D 批要复核阈值与 SKIP 带。**已复核（§9）**：真正的毛病不是带宽而是**锚点**——三条臂里有两条锚在绝对 scRGB 常数上，纸白 120 nits 的本机上一支恒假、另一支把假高精度读成 PASS（夹具实测）；锚已改到帧自己的纸白，阈值宽度本轮未动（§9⑤）。
6. **JPEG 这轮带出的四条待办**（§6.6/§6.7）：
   ① **夹具先修**：亮度卡实验非单调（白卡比灰卡暗）⇒ 给卡片探针加一条成立自检（请求色值 vs 帧内主带必须对得上，不过就明说"这面不给结论"）；修好后才能谈"截图偏暗/色调映射档位"。**自检已落地**：`tests/manual/jpeg_pixel_probe.py --ladder`（逐级均值差 ≥1 LSB，否则退 1；三枚合成件把 0/1/2 三种退出码都验过，见附 17）——但**还没在真机卡片上跑过一次**，所以"亮度/色调映射档位"这一面仍未开。
   ② **黑帧偶发率**：现有 1/9，样本不够 ⇒ 要一次 20–30 张的连拍统计（顺带就把 P1-6 的并发无上限压出来），按"黑帧/成图"分开计数，别把两者混成一个数。
   ③ **真实截图路缺一条电平合理性判据**：`--selftest` 的 `HDR_JUDGE` 只对合成台阶帧自检，活帧没有任何"整帧近乎全黑/全白"的检查 ⇒ 一旦成黑帧，用户只看到"保存成功"。这是可修的一条，等授权。
   ④ **同一"JPEG"在增益图开关两侧容器不同**（progressive SOF2 vs baseline SOF1）⇒ 任何针对 JPEG 的读回判据都要写明是哪一侧，别拿 progressive 件去验增益图契约。
7. **一条已经量清的产品结论**（§6.8⑧ 是实测，不是推算）：增益图分辨率**不是**省体积的杠杆 —— 再降到 1/8 只省 0.1–0.3 %，改成全分辨率要给 **rgb 档 +10.1 %、gray 档 +3.6 %**；而 1/4 正是参考实现对 JPEG 的默认档。底图距离才是（`-d 1.0→2.0` 即 −41 %）。将来做"JPEG 体积"相关决定时用这组数，不要重新推理。
8. **第二条已经量清的产品结论**（§6.10）：截图尺寸**除不尽 4 时不需要对齐也不需要裁边** —— 现在的 `(w+3)/4` 向上取整是三种写法里唯一保住"最右列/最下行有增益样本"的一个（向下取整会把那 1 px 超纸白高光的提升整个丢掉，实测满值 28 vs 丢失）。⇒ 将来有人想"把选区尺寸规整到 4 的倍数再编"，用这条否掉它。同时这条几何此前**在门禁里零断言**，本轮补了 10 条（`GainMap_OddDimensionGeometry`，§6.10⑦）。

**本轮改动面（§6.10 之后已更新）**：产品源码 `src/TrueToneCap.App` / `src/TrueToneCap.Core` **一行未动**。落盘改动共四处（其中一处本轮加了 10 条断言），全在验证侧：
① `src/TrueToneCap.Test/TrueToneCap.Test.csproj`（把 App 编好的 `.cso` 拷进测试宿主，§5.1）；
② `src/TrueToneCap.Test/UsabilityTests.cs`（两处：一条恒跳过改真断言 §5.2b；本轮新增 `GainMap_OddDimensionGeometry` + 两条 SOF 解析小函数，§6.10⑦）；
③ `scripts/verify.ps1`（通过路径回显跳过点名 + 点名数守卫，§5.2a）；
④ `tests/manual/combo_locale_coverage_measure.py`（N1 的尺，按仓里先例 `git add -f` 暂存，blob `9d02291c`）。
本轮另外起的三个小工具都在 `artifacts/t3-2026-09-28/`（被 `.gitignore` 挡着，不进仓）：`probe-wic-odd.ps1`（WIC 读回奇数尺寸 JPEG）、`gm_edge_read.py`（拆出增益图并用 Pillow 量边缘样本）、`geom/`（五档尺寸的 UltraHDR 真件）。
外加两份文档（本文件与计划文件，未跟踪）。**全部未提交**；基线 **1379 断言 / 2 跳过**（`tests/output/geom-gate2.log`，GATE_EXIT=0）。

---

## 8. 批次 B 首件：把「用户所选」喂进 SelfTest 的管线落点（2026-09-28 续）

**改的是什么**（`src/TrueToneCap.App/Services/SelfTest.cs`，`RunDisplayAudit` 的"管线落点"段，净 +72 行）。这一段是审查轮 §2 那个结构性缺口的正面补法：Test 工程只引用 Core ⇒ App 侧的规则过去只有源码文本哨兵在盯，而 SelfTest 是**唯一**能在真身进程里执行 App 逻辑的入口。原来它这里喂的是 `new AppSettingsData()` 默认快照，于是：

- **"用户亲手选的增益档/质量档有没有落进 `EncodingSettings`"这一格从来没被判过** —— 卡片上只有 `HdrOutput` 一个布尔；
- 更要紧的是 **`--display-audit` 只在"写文件失败"时退非零**：卡片能印出 `✗ 判定与落点分叉`，进程照样退 0 ⇒ 那条最要紧的分叉是一行只能给人眼读的装饰（正是记忆里"exit 0 不区分【判了】和【没判】"那一条的又一种形态）。

**四条改动，全部是"补既有件、不新建 harness"**：
1. `snap` 改成 `new SettingsService().Load()` —— 读真正被产品读的那份（`SettingsService.SettingsPath`，同目录唯一答案），并把来源路径 / `文件本来就在` / `CanPersist` 原样印到卡片上。⚠ 顺手纠正了这段自己的旧注释（它写着"这条探针碰不到设置文件"）：`Load()` 会读盘，且同目录无文件而旧位置有文件时会**搬一份过来**（`MigrateFromLegacyLocation`）⇒ 注释与代码分叉本身就是本轮要修的东西之一。
2. 每个探针多算一份 `es0`（喂默认快照的那份），卡片逐格打 **差分（默认⇒你的）**：质量/色度/位深/增益档四轴。
3. 判两条**不需要再抄一张"格式↔字段"键表**的直通规则：① `GainMapMode` 字符串→枚举那一步；② 质量差分——两份快照的该格式质量本来就不同时，落点必须不同（用产品自己的 `snap.GetQuality(f)` 取值 ⇒ 改键表不会让这里过期）。色度/位深只印不判，理由就一条：判它们要在 SelfTest 里复制一份 `format switch`，那正是 §41/§48 反复踩的"同一事实两处写"。
4. 退出码改成 fail-closed：`landingFailures` 非空 ⇒ 卡片末尾逐条点名 + 进程退 1（`<returns>` 注释同步改写，写清"批次 B 之前只在看盘失败时退非零"）。

**已经量到的**：`dotnet build TrueToneCap.slnx -c Release` ⇒ 0 警告 0 错误（T0/编译级）。整条门禁复跑 `tests/output/batchB-gate.log` ⇒ `GATE_EXIT=0`、**1379 断言 / 2 处点名跳过**（869+213+14+7+12+43+221，与汇总行逐字一致，`[2/2]` 34.4 s）——与 §6.10⑦ 的基线同数，因为 App 侧这段代码不在这 7 套件里执行（正是 §2 那个结构性事实）。⚠ 这一轮之前还得先 `--no-incremental` 全量重编：只改 App 源码时 Tools/Test 宿主被 MSBuild 合理跳过，`[1.9/2]` 会按目录时间戳判红并作废整轮（`tests/output/geom-gate.log` 就是那样作废的第一轮，不是代码问题）。
**还没量到的（要说清）**：这一批改动的**内容读数需要起一次产品进程**（`--display-audit` 住在 WinExe 里，计划 D3 已定"默认不起、要起先报备"）。且要读出"差分真的有差"，得先让那份设置与默认值不同——本机 dev 目录里那份 settings 已有 `QualityJpegLi=0.5 / QualityJpegXl=0.5`，我据此预判差分会真的动。**（⚠ 这条预判被下面 T3 的第二次读数纠正了一半：那两格被一次性的修订号迁移吃掉了，见 ①。）**

### 批次 B 的 T3 读数（用户"起"授权后真跑，`--display-audit` × 2 次）

命令（同一脚本，只读不点屏幕）：`artifacts/t3-2026-09-28/run-audit-b.ps1` ⇒ `TrueToneCap.exe --display-audit --out <dir>`，`Start-Process -Wait` 取**真退出码**（记忆②那条"`& $exe` 不等 GUI 进程"用 `-PassThru + WaitForExit(90s)` 绕开）。

**第一次** `artifacts/t3-2026-09-28/audit-b/`：四格全 ✓、卡片末尾 `管线落点：HdrOutput / 增益档直通 / 质量差分 三类一致，0 条分叉`，但差分行是 `质量 1→1 … 增益档 Gray→Gray` 并打出我那条自报 **`⚠ 你这份设置与默认值在这几根轴上完全相同 ⇒ 上面的差分读数不判别`**。
⇒ 同时量到一件我没预料到的事：**起这一次进程改了设置文件**（sha256 `3C77…→A945…`，键 14→10）。归因查清了，不是新代码写盘：`SettingsService.Load()` 在 `:120` 就有 `if (migrated) Save()`，而这次触发的是 `:113` 的 `MigrateStaleDefaults` —— 那份文件的 `SettingsRevision < 2`，于是五档质量 + `ColorSpaceIndex` 被整体交还默认表（0.5/0.5/63 → 默认）。
⇒ 顺带把"升级会不会清掉用户质量档"这件事钉死：按标签逐个查 `SettingsService.cs` 源码，**差集序列化与 Save 里钉修订号是同一版一起进仓的**（`v0.3.4-beta` 两者皆 0，`v0.3.5-beta`/`v0.3.6-beta` 两者皆 1）⇒ 发布线上不存在"文件已是差集形态、修订号却还是旧的"那个窗口；我撞上的这份是 09-24→09-25 之间**本地构建**写的（那个窗口只存在于 dev 机器）。⇒ **不是产品缺陷，是门的既定行为**，我踩到的是一次性迁移。

**第二次**（先手工把 `QualityJpegLi=0.5`、`GainMapMode=Rgb` 写回那份文件，让它真的与默认不同）`artifacts/t3-2026-09-28/audit-b2/`：
```
EXIT=0    设置 sha256 起进程前后一致 = True（修订号已是 2 ⇒ 这一次审计确实只读）
增益图 + Rec.709 底图   … 增益档=Rgb 质量=0.5 ⇒ 期望 True ✓   └ 差分(默认⇒你的): 质量 1→0.5 增益档 Gray→Rgb
增益图 + Rec.2020 底图  … 增益档=Rgb 质量=0.5 ⇒ 期望 True ✓   └ 差分(默认⇒你的): 质量 1→0.5 增益档 Gray→Rgb
JPEG XL + Rec.709 目标  … 增益档=Rgb 质量=1  ⇒ 期望 False ✓  └ 差分(默认⇒你的): 质量 1→1 增益档 Gray→Rgb
JPEG XL + Rec.2020 目标 … 增益档=Rgb 质量=1  ⇒ 期望 True ✓   └ 差分(默认⇒你的): 质量 1→1 增益档 Gray→Rgb
卡片末尾：管线落点：HdrOutput / 增益档直通 / 质量差分 三类一致，0 条分叉（且不再打"不判别"）
```
⇒ **批次 B 的判据第一次拿到判别性读数**：只改 `QualityJpegLi`，动的正是两格增益图探针而**不动**两格 JPEG XL ⇒ 这同时证明了"用户所选落到 `EncodingSettings`"和"每格式各取自己那一格"（增益图共用 JPEG LI 那格，`AppSettingsQuality.GetQuality` 的口径在真身进程里对上了）；`GainMapMode` 四格全跟动。那条"⚠ 不判别"的自报随输入翻转 ⇒ 它不是装饰。
⇒ 收尾：夹具已撤（`cp -p` 还原快照 A，sha256 逐字节一致 `a945f48f…`），`tasklist` 无残留进程。⚠ 还原用的正是记忆里点过名的 `cp -p`——这次它只是**落盘一份 JSON 夹具**、不参与任何构建，所以不构成那条"保 mtime ⇒ MSBuild 不重编 ⇒ 二进制过期"的坑；但下一次还原的对象若是源码，就必须按那条规矩 `--no-incremental`。

**这一格仍然欠着的**：`landingFailures` 非空 ⇒ 退 1 这条**反向路径没测过**（今天没有任何一格分叉，红长什么样只有代码保证）。要量它得起一次"故意喂错"的进程——分类器会拦改产品源码的变异注入，所以这条路留给"允许变异"的那次授权，或者接受它作为未验项。

## 9. 批次 D 首件：HDR 精度判据的"白点锚"复核（§7-5 那条待办，测出一次假绿并已修）

**① 待办原文**（§7-5）：`HDR_SOURCE` 两次 PASS 靠的是**不同的支**，且它的 SKIP 带很宽 ⇒ D 批要复核阈值与 SKIP 带。

**② 复核结果：问题比"带宽"大，锚点本身就错了。** 三条读数把它钉出来（都是 T3/T1，不是推的）：
- **真机 >1.0 占比在同一台机器上四次读到 1.49% / 60.84% / 86.53% / 92.12%**，而纸白恒定在 1.5（`GetSdrWhiteLevel()` 本机实测 120 nits）⇒ 这个量读的是"桌面有多亮"，与帧的精度无关。
- 于是三条臂里：`headroom`（>1.0 落在 0.05%–50%）今天恒假；`fitsSdrBox`（>1.0 占比 <0.01%）也恒假 ⇒ **"升位深式假高精度"那一支 FAIL 在纸白≠80 nits 的屏上永远打不出来**；而整格臂的尺子写死 `1/255`，DWM 把 8-bit 内容抬到 1.5 之后台阶间距是 `1.5/255`。
- **合成夹具把后果直接量出来了**：一份"纸白 1.5 的 256 级台阶帧"（就是假高精度的样子）在**旧锚点下被判成 OK（假绿）**，新锚点下判成 **FAIL**（整格 100.00%、>纸白 0.00%）；同法抬上去的 1024 级帧仍判 OK。⇒ 不是保守，是**方向反了**：§28 要抓的东西会被它读成通过。

**③ 改了什么**（`src/TrueToneCap.App/Services/SelfTest.cs`，只动 App 侧判据，产品编码路径一行未动）：
1. `AnalyzeHdrFloat(px, w, h, float paperWhite = 1f)`：整格尺子 `Lsb = 纸白/255`、SDR 统计窗口 `[0, 纸白]`、新增 `AboveWhite` 计数，`NeutralTol` 跟着 Lsb 缩放（8-bit 量化噪声也按同一比例放大）。
2. `JudgeHdrFloat` 的 `headroom` 与 `fitsSdrBox` 改按 `AboveWhitePct`；`>1.0` 仍打印但标成"旧锚点，只留作对照"，JSON 另存 `aboveWhitePct` + `paperWhite`。
3. 纸白取产品同一个出口 `DisplayEnumerator.GetSdrWhiteLevel()`；读不到 ⇒ 1.0 ⇒ **逐位退化回改前写法**，老机器/老读数不受影响。地板 `max(nits,80)` 与 `JpegGainMapEncoder.ComputeWhitePoint` 同一条并在注释里点名，不再长第三份。
4. 判据自检从两帧变**四帧**（纸白 1.0 与 1.5 各一对），并把"旧锚点把这份 8-bit 判成什么"一并印进日志 ⇒ **夹具本身就是这次修复的证据**，不用变异、不用怀疑自己的代码。

**④ 读数**：构建 0 警告 0 错误；`--hdr-probe` 真跑 `EXIT=0`：
```
HDR_JUDGE ok  256级→FAIL(整格100.00%) | 1024级→OK(整格50.98%,级数1024) |
              纸白1.5 同一对 → FAIL / OK，>纸白 0.00%，而【旧锚点(纸白按1.0)】把这份 8-bit 判成 OK
HDR_SOURCE ok 帧3840x2160 min=-0.0277 max=1.6855 >1.0 占92.12% >纸白(1.5) 占0.01% <0 占0.00%
              整格 raw 72.05% / sRGB反解 91.25% → 采用91.25% | 中性像素148752 → 去重量级1010 ⇒ PASS(级数那一支)
```
**门禁复跑**（含本轮全部改动）：`tests/output/batchD-gate3.log` ⇒ `GATE_EXIT=0`、**1379 断言 / 2 处点名跳过**（`[2/2]` 38.5 s，全日志 `❌`/`作废` 命中 0）——与 §6.10⑦ 同数：§9 改的是 App 侧探针，7 套件不执行它，所以基线不会动，能动的只有构建（0 警告 0 错误）。⚠ 前一轮 `batchD-gate2.log` 是 `GATE_EXIT=1`：`[1.9/2]` 抓到我在它构建**之后**又改了一次注释（源码 16:52:08 > 宿主 16:51:27）⇒ 那轮不作数，这一轮才是终态。那条判据第三次救我（附 16）。
⇒ **同一条判据隔 6 分钟复跑**（`hdrprobe-d2`，把 `pw` 归一那步改完之后）：`>1.0 占 74.46%`、`>纸白(1.5) 占 0.00%`、`max 1.5332`、`级数 1140`、`EXIT=0` ⇒ 结论没变，而 `>1.0` 那根轴两次差 17 个百分点 —— **它已经完全不参与判决**（只有 `>纸白` 参与），这正是本轮要的效果。②里那串 1.49 %/60.84 %/74.46 %/86.53 %/92.12 %/92.36 % 就是同一台机器一周内 `>1.0` 的全部读数。
**门禁复跑**（含本轮全部改动）：`tests/output/batchD-gate3.log` ⇒ `GATE_EXIT=0`、**1379 断言 / 2 处点名跳过**（`[2/2]` 38.5 s，全日志 `❌`/`作废` 命中 0）——与 §6.10⑦ 同数：§9 改的是 App 侧探针，7 套件不执行它，所以基线不会动，能动的只有构建（0 警告 0 错误）。⚠ 中间那一轮 `batchD-gate2.log` 是 `GATE_EXIT=1`：`[1.9/2]` 抓到我在它构建**之后**又改了一次注释（源码 16:52:08 > 宿主 16:51:27）⇒ 那轮不作数，这一轮才是终态（同族事故见附 16）。

**⑤ 仍然开着的两格**：① 阈值**宽度**本轮没动（只换锚），`GridFreePct=95`/`MinSampledPixels` 那套带宽是否合适要等多屏/多内容形态；不过现在 SKIP 会同时打 `>1.0` 与 `>纸白` 两个占比，旧读数与新读数可比了。② 真 HDR 源下的 headroom 分支仍未亮过（本机造不出）。

## 附：本轮我自己造成的事（照项目规矩记账）

1. **一次假阴性**：`grep -n "BuildOutputPath|FileNamePrefix|..." CapturePipelineService.cs` 第一次返回空，我差点据此写"命名函数不在这个文件里"；同一条命令重跑命中 `:486/:501`。⇒ 负断言前必须用第二条独立命令复跑同一模式，这条规矩（记忆里已有）本轮救了一次。
2. **一次假数**：量下拉中文项的第一版解析器把 `<ComboBox` 前缀也匹配到 `<ComboBoxItem`，块尾一路吞到文件末尾，算出"28 个下拉 / 1318 个中文项"——而全文件只有 116 个 `ComboBoxItem`。⇒ **布局/计数型读数必须带一条文件级独立计数当对照**（这次对照数就是 116），否则会把解析 bug 报成产品缺陷。
3. **两次 bash 引号事故**：`\"$env:LOCALAPPDATA\...\"` 与 `$_` 被 MSYS/bash 吞掉，PowerShell 命令当场报错（一次还差点被当成"文件不存在"的负结论）；改用显式绝对路径 + 与 preflight **同一 recipe** 复量才可比。
4. **一处 lane 结论我没有复读**：§3 里标【lane】的 9 行只有子代理取证，按项目规矩不得当已确证；后续批次要补读。
5. **最严重的一次：我把"我的命令退出码"当成"门禁退出码"，并且没读日志就报了数**。后台命令写成 `powershell … > log; echo "GATE_EXIT=$?"`，notification 里那个 `exit code 0` 是这句 echo/包装的，不是 `verify.ps1` 的；而那一轮实际在 `[1.9/2]` 宿主新鲜度处**判红作废**（日志末行就是"❌ 宿主比源码旧 ⇒ 随后所有 RESULT 读数都属于旧构建，本轮结论作废"）。我同时还写出"基线 1368 → 1371、跳过只剩 2 处"——**那个数我根本没量**。⇒ 两条规矩同时被自己违反：① 后台命令要让被验对象本身成为最后一句（`powershell …; exit $LASTEXITCODE` 之类），否则包装层的退出码会顶掉它；② 报数前 `tail` 那一轮日志。真数是 §5.2 的 **1369 / 2 处跳过**，出自 `tests/output/n6fix-gate2.log` 汇总行与 `GATE_EXIT=0`（那次才是门禁自己的退出码）。
6. **一次 Edit 把下一节标题吃掉**：插入 §5.2 时 old_string 用了紧邻的 `### 顺带一次读数作废…` 标题行，结果标题被替换掉、正文成了孤儿。发现方式是改完后 `grep -n "^#"` 看标题序列（这条自检以后每次改完长文都跑一遍）。
7. **一次误判并当场撤回**：见 §6.2——以为"HDR 产物 cICP 写 sRGB + 嵌 ICC"是 P0 级分叉，读 emit 代码后确认那条路径按构造只产 8-bit SDR 样本，正牌判据在 `ColorPipelineTests.cs:598/649`。**没写进结论，也没去"修"**。
8. **一处呈现错误（被用户抓到）**：§6.5 那张三面表原本只写"目标格式 / 编码耗时 / 产物 / 剪贴板"，把 **NVENC·质量15·420** 的 AVIF、**托管无损·质量100·444** 的 PNG、**cjxl·质量1.0·444** 的 JXL 三行并排 ⇒ 读出来就是"AVIF 比 PNG 快 40 倍"这种根本不成立的结论。读数本身没错（531 ms 与日志时间戳自洽、产物经外部 ffprobe 证是真 HDR AVIF），**错在我没标条件**。⇒ 规矩：**任何耗时/尺寸类表格，每一行必须带它自己的路径与档位**，跨行不可比时要明写"不可横比"。
9. **把"没看到输出"当成"自检通过"**：`py -m py_compile` 那一批命令我只收到了 Edit 的回执、**根本没看到 bash 的输出**，就写了"语法自检过了"；下一次真跑立刻炸在 155 行（嵌套双引号）。⇒ 自检的判据必须是**我亲眼看到的退出码或输出行**，不是"我记得跑了"。
10. **探针自己的两处假读数，都被盘上证据推翻**：① 连拍 8 次、日志里明明有 8 条 `无感截图完成`，探针却报"本轮成图 0 张"——根因是我按"日志行数偏移"做增量读取，而产品同一天复用同一个日志文件；改成按本轮 `FileNamePrefix` 唯一键全文件计数才对得上。② `Pick-Format` 一次展开拿到 0 项，差点被我写成"格式下拉空白"的产品结论——它是时序脆弱，加了收起再展开的重试；**在下结论前，一次空读数必须先区分"没东西"与"我没等到东西"**。
11. **亮度夹具不自洽，我据此作废了自己的数**（§6.7）：白卡截出来比灰卡暗 ⇒ 非单调。我没有去挑一个能用的端点讲故事，而是把整组亮度读数作废并把问题留作未决——这比"找到一个能写的结论"更值钱。夹具的修法已登记：加一条"请求色值 vs 帧内主带"的成立自检，不过就明说不给结论。
12. **一次"想写成交互缺陷"的冲动，被权威实现否掉了**（§6.8⑦）：读 MPF 时发现两处偏移基址不一致（IFD 相对 TIFF 写 8、MPEntry 含签名写 54），我正要写"严格的 TIFF 读者会找错位置"。改成先问 libultrahdr 自己的解码器（`ultrahdr_app -m 1 -j 件 -P`）⇒ 它明确认我们的件（`Ultra HDR Image: Yes` + 完整增益图元数据），负对照（关增益图那枚）也如实报"不含 gainmap"。**疑虑降级为"写法与参考实现不同但被同一实现接受"，没进结论。**
13. **一次工具性事故**：往文档里写 `MPF` 加一个 NUL 字面量时，字符串被解释成**真 NUL 字节**，文档一度被 grep 当成二进制文件（`Binary file matches`）。已剔除并把措辞改成"含 4 字节 MPF 签名"；收尾自检加一条：按字节扫一遍确认无 NUL。
14. **同族第四次（这次在 C# 里）**：写 §8 那段时，一行 `"…要判"用户所选有没有落地"得先改一格再跑"` 把内层引号写成未转义的 `"`，串会被提前截断；同时三条 failure 文案引用了一个我凭空发明的变量 `displayTag`（循环里只有 `d.Name`）。两处都是**编译前读自己刚写的 diff** 抓到的，不是我"记得检查了一遍"——那次构建因此直接 0 警告 0 错误，而不是炸在两个错上。⇒ 多行插值串与"我以为存在的标识符"是同一类成本最低的自检对象。
15. **报备时把"预计"写成了"事实"**：向用户报备那次起进程时我写了"设置只读不写"。这是我从"我的代码只调 `Load()`"推出来的，**没去读 `Load()` 自己**——而它在 `:120` 就写着 `if (migrated) Save()`。第一次真跑当场把设置文件从 14 个键改写成 10 个（质量档被修订号门整体交还）。所幸后果只是我早先自己在这台 dev 目录里造的夹具，且迁移是一次性的，但**这句话本身是错的，而且是那种"用户会照着它决定要不要放行"的错**。⇒ 规矩：报备副作用要沿调用链读到**落盘出口**，不能停在"我这一层没写"。同轮另一条同源：`git log -S SerializeDiffs` 只回一个提交，我据此差点写成"差集序列化是 v0.3.6 才进的"；改成按标签逐个 `git show <tag>:文件 | grep -c` 才拿到真序（v0.3.4=0、v0.3.5=1、v0.3.6=1）。**`-S` 找的是"计数变化"，一次把两处都改动的提交只会给一条**。
16. **在自己起的构建跑着的时候改源码**（2026-09-28，第三次被同一条判据抓）：`batchD-gate2.log` 那轮 `GATE_EXIT=1`，因为我在它 `--no-incremental` 构建**之后**又落了一笔注释修改（源码 16:52:08 > 宿主 16:51:27），`[1.9/2]` 直接把整轮作废。前两次记的是"构建失败/产物过旧都作废 RESULT"，这次的新意只有一条：**判据不看改的是不是注释** —— 我当时想的是"注释不进二进制，不必重跑"，而规矩是"终态必须有一轮匹配的门禁"。⇒ 要么把编辑做完再起门禁，要么起完之前不碰 `src/`；"后台期只读"这条我这次没守全（改 `tests/manual/` 安全，改 `src/` 不是）。
17. **一条本该在第一次就存在的自检**：亮度卡片夹具非单调（白卡比灰卡暗）那组读数整批作废（§6.7），但当时只登记成待办、没当场给探针加自检。本轮补了 `--ladder`（`tests/manual/jpeg_pixel_probe.py`）：按参数顺序当黑→灰→白阶梯，逐级均值差必须 ≥1 LSB，否则退 1。三枚读数都是当场造的合成件：递增件 `RC=0`（逐级差 97/112）、故意把白放中间 `RC=1`、只给一枚 `RC=2` 拒判。⇒ 这条**还没在真机卡片上跑过**（那要再起一次产品进程），所以现在只能算"尺子造好并自校过"，不能写成"亮度那一面已经量过"。

---

## 10. 批次 C 全量：九条 lane 的候选台账 + 我这一轮的复核（2026-09-29）

第十条 lane（L7 判据体检）在本节落稿时仍在跑，它的产出进 §11，本节不含它的数。

### 10.1 当场读数（基线与前置，出处都指到文件）

- **本轮基线**：`tests/output/round3-baseline-gate.log` ⇒ `GATE_EXIT=0`、**1379 断言 / 2 处点名跳过**，逐套件 869+213+14+7+12+43+221=1379 与汇总行逐字一致（`[2/2]` 43.6 s）。两处跳过的名字当场打出来了：① MFT AV1 编码——本机无 AV1 MFT 编码器；② 翻译链路端到端（必应）——需 `TTC_LIVE_TRANSLATE=1`。§5.2a 那把"跳过必须点名"的锁在真实门禁路径上第二次生效。
- **用户设置指纹**：轮前轮后逐字不变（`SETTINGS_DRIFT=0`，同一份日志末两行）⇒ 本轮门禁没改用户机器上的设置。
- **外部裁判可达性（D 批的前提，本轮实测）**：`ffprobe = C:\PLAN\FFmpeg\ffprobe.exe`（**这份带非上游 HDR10+ 补丁**，声明层读数要用另一份干净件复算）；`imagecodecs 2026.6.26` **有 AVIF 解码**；`Pillow 12.3.0` + `lcms2` 可导入；`pillow_heif` **缺失**（HEIF 容器只能靠 imagecodecs/ffprobe）；`jxlinfo/djxl = C:\PLAN\ffmpegPictureUI\tools\src\libjxl\build\tools\Release\`；`ultrahdr_app` 在位。
- **盘上残留基线**：`tests/manual/safety/preflight-orphans-2026-09-29.txt`（8 个 `%TEMP%\TrueToneCap_*` 目录、`nvenc_diag.txt`、`%LOCALAPPDATA%\TrueToneCap\` 下两份遗留 settings）。⇒ 批次 D/F 收尾比差集用。
- **未提交的上一轮改动**已整份存成 `tests/manual/safety/uncommitted-2026-09-29.patch`（43 KB）。

### 10.2 候选总量与强度分布（这是 G3 要的数，不是我的判断）

| lane | 面 | 候选条数 | 其中自报 P0 |
|---|---|---|---|
| L1 | 捕获 / 热键 / 区域检测 | 18 | 3 |
| L2 | 色彩与色调映射 | 14 | 1 |
| L3a | 编码声明与容器 | 14 | 0 |
| L3b | 编码后端与硬件 | 18 | 0 |
| L4 | 输出与目的地 | 22 | 5 |
| L5 | 设置与能力协商 | 14 | 1 |
| L6a | 交互界面与标注 | 30 | 5 |
| L6b | 预览与本地化 | 17 | 3 |
| L6c | OCR 与翻译 | 18 | 2 |
| **相加** | | **165** | **20** |

三条横向结论（每条都能在 lane 表里指到行）：

1. **P0 全部住在 App 侧，而 App 侧结构性测不到**。20 条 P0 里只有 2 条落在 Test 工程可达的 Core 代码上（L2-1 的浮点 alpha 透传、L6c-#11 的灰度系数）；其余 18 条在 `MainWindow.xaml.cs` / 两个覆盖层 / `HdrCaptureWindow` / 目的地链上。根因还是 §2 那条：`TrueToneCap.Test.csproj` 只引用 Core ⇒ 这一层永远只能是 T2/T3。
2. **"降级/失败对用户不讲话"是一条贯穿九条 lane 的同一种病**：L4 的 45 行可见性台账里 D2/D3 档占多数；L1 十条退档分支里 6 条只到 `Debug`（默认简档根本不落盘）；L3b 的假原因（崩溃哨兵、能力探测残留）；L6c 的"静默换引擎零痕迹"；L6a 的动作→反馈表里"标注被丢 0 提示"。**结论：G2 的答案不是"有几处漏"，而是"这套系统的留痕默认级别是 Debug/Info，而用户能看到的只有 Warn 以上 + toast"**。
3. **两处"门禁替 bug 背书"**（本轮最贵的形状，两条我都回源读了）：`CorePipelineTests.cs:269-273` 把崩溃哨兵的并发误读断言成正确契约；`CHANGELOG.md:1372` 声称 opId 前缀"不影响日志查看器的行格式"，而下一条就证伪。⇒ 这类"绿灯 + 文档双保险"的条目，补牙时要连断言文案一起改，不能只加新锁。

### 10.3 我回源确证的（附取证方式；未列的一律仍是候选）

| 编号 | lane | 结论 | 我怎么确证的 | 级别 |
|---|---|---|---|---|
| V1 | L4-D1/D2/D3 | 三根截图入口的失败可见性不一致，无感那根最静默：`:1330-1334` WGC 未初始化只 Error 就 return、`:1458-1462` 像素为空只 Warn、`:1483-1486` 后台编码异常只 Error；对照选区那根 `:1292-1297` 有 ❌+toast | 读 `MainWindow.xaml.cs` 四段原文逐行比对 | 确证（后果面 T3） |
| V2 | L6c-#2 | `TranslationService.cs:781` `return content?.Trim() ?? text;` ⇒ LLM 回 200 而 `content:null` 时**把源文本当译文返回**且零痕迹 | 读该行 + 上下文 `:772-781` | 确证（触发要一个回 null 的端点，本机 loopback 可造，T1 可达） |
| V3 | L6c-#11 | `BitmapPreprocessor.cs:92` 灰度权重 `bgra[off]*77 + [off+1]*150 + [off+2]*29`：BGRA 布局下 77 给了 B、29 给了 R ⇒ BT.601 的 R/B **装反**（纯红算成 28、纯蓝算成 76，G 与总权重 256 都对） | 读该行 + 手算三原色 | 确证（`IsDarkBackground` 阈值 110 在饱和色上判反，T1 可判且现无判据） |
| V4 | L3b-#5/#6 | `MftEncoderNative.cs:239-243` 把 `IMFTransform_SetInputType/SetOutputType/ProcessInput/ProcessOutput/ProcessMessage` 声明成 `[LibraryImport("mfplat.dll")]` 扁平导出——这些是 COM vtable 槽不是导出符号 ⇒ 首次调用即 `EntryPointNotFoundException`；`:245-246` 那个 `[Guid] interface IMFTransform {}` 是空的；文件头 `:13-14` 自称"只走微软自己导出的扁平函数（… IMFTransform_*）"**这句为假**。CLSID（`:26-27`）另有一问，要回权威头核对，我**没有**据此下结论 | Grep 出全部 `LibraryImport` 声明 + 读文件头 | 确证"调用约定错"；CLSID 待权威源；可达性 T3（本机无 AV1 MFT ⇒ 永远撞不到第二层） |
| V5 | L3b-#7 | 崩溃哨兵的在途 `Arm` 与崩溃残留同形：`EncoderCrashSentinel.cs:78-104` 只比首行后端名、时间戳写了没人读、不看 PID ⇒ 第二个并发编码会把**前一个仍在原生调用里**的保护文件删掉（`:96`）、置 `s_nvencFailed`、并把假的"上次 NVENC 导致进程崩溃"写进留痕（`FormatEncoders.cs:1058-1065`）。而 `CorePipelineTests.cs:260-273` 把这个误读**断言成正确行为**（`stillArmed` 期望 true） | 读哨兵 70 行 + 消费点 + 那条断言原文 | 确证（并发可达性属 L4 的账：编码段无并发上限，`_isCapturing` 只挡捕获段） |
| V6 | L5-#1 | `SettingsService.cs:120` 的 `if (migrated) Save()` 跑在 `:123` 的 `Normalize(Current)` **之前** ⇒ 迁移期把未归一的越界值原样钉回盘上（并盖 revision/merged 章，此后永不收敛） | 读 `:104-137` 原文定次序 | 确证（盘上形态要 T3） |
| V7 | L5-#2 | 文件缺失或反序列化抛 ⇒ `:127/:134` `Current = new()` ⇒ 凭据字段空 ⇒ 任一次 `Save()` 经 `:286-293` `SaveSecret` 的 `else SecretStore.Delete(name)` **把已存 API Key/Secret 永久删掉** | 读 `Load`/`Save`/`SaveSecret` 三段 | 确证（删的是加密库里的值，无备份） |
| V8 | L5-#5（前半） | `MainWindow.xaml.cs:697-698` 的回落条件是 `savedQ ∈ [QualitySld.Minimum, Maximum]`，而滑块下限被编码器能力抬高：`AvifEncoderCapabilities.cs:359-360` `MinCrf(Nvenc/Qsv)=1` ⇒ 盘上合法的 `QualityAvif=0` 在切到 Auto→NVENC 时**被判越界并回落默认 15**。写回盘那一半我**没有**读完 | 读 `:690-707` + `MinCrf` 定义 | 部分确证：回落成立；"下次保存把 0 静默写成 15"待读 `MainWindow.Settings.cs:242`/`:2567` 的写链 |
| V9 | L6a-D4 | 选区覆盖层的实际自动取消是 `SelectionTimeoutMs = 60*1000`（`:31/:34` 注释自陈"1 分钟"），`HardExitMs = 3*60*1000`（`:38`）是另一条无条件看门狗；`commit 3f8b3be` 的标题写的是"3分钟超时自动取消"，全仓无倒数/剩余时间提示 | 读常量两段 + `git log -1 3f8b3be` + grep 倒数 | 确证（"用户丢工作"这一后果要 T3 体感） |
| V10 | L6a-D3 | `SelectionOverlay.xaml.cs:716-721`：只有 `Confirm`/`Copy` 两个目的地去做标注合成（`AnnotatedRegionPixels`），**识字/翻译两条不在其中** ⇒ 它们在 `MainWindow.xaml.cs:2080/2099` 回落原始桌面像素 ⇒ 用户用马赛克盖住的文字会被 OCR 原样读出 | 读 `:705-738` 原文 | 确证（端到端后果 T3） |
| V11 | L3a-D1/D2 | AVIF 全后端失败的 PNG 保底 `FormatEncoders.cs:1538` `ManagedPngEncoder.Encode(bgra,w,h,pngPath,8)` **不带 icc/cicp**（同函数 `:1528-1529` 手里就有 `icc/colorSpaceTag`），而对照的 WebP 保底 `:477-478` 老实取 `GetColorMetadata(s)` 并传进去、还写了"不是 WebP"的留痕 ⇒ 保底之间不一致，AVIF 那格产物是"广色域像素 + 零声明"的 PNG | 读两段保底对照 | 确证（触发要 avifenc 与硬件同时不可用，窄） |
| V12 | L6b-L7 | LogViewer 的行解析正则（`LogParser.cs:104`）要求级别方括号后**紧跟** `[`，而产品的行格式（`LogService.cs:36`）在有 OpId 时是 `时间 [级别] #opId [分类/Tag] …` ⇒ 带 opId 的行 `ParseLine` 判 null、在查看器里静默消失。我用 `tests/manual/safety/logparser-opid-check.ps1` 当场量了三条臂：真机 09-28 日志的三条普通行 **HIT**，同一条行只加 `#a1b2c3 ` 前缀 ⇒ **MISS** | 自建夹具实跑（对照臂 + 被试臂 + 同一条行加前缀） | 确证"格式不匹配"；**流行度未量**——那份日志里带 opId 的行是 0 条（无截图动作），占比要 T3 跑一次真截图才拿得到 |

### 10.4 我驳回或降级的（含一条方法学）

- **降级：`AnimationRecorder` 整卡（L1-#3、L6a-D6）**。"零构造点、`record` 热键从不注册、三个下拉没有 `SelectionChanged`"我复核成立，但它**不是新发现**——2026-09-26 维护者已当场拍板"两处 dead code 都暂不删除、原地保留"。⇒ 不进缺陷台账，只在可用性 D4 表里记一行事实：`Ctrl+Shift+G` 与那张卡的三个下拉当前**无任何动作可拒**（功能未接线），而状态栏照报"✅ 设置已设置"。
- **驳回其判据形式：`grep -c LocaleManager == 0` 不能证明漏翻**。我用它量 `HdrCaptureWindow.cs` 得 0（另有 480 行含汉字），但同一命令量 `SelectionOverlay.xaml.cs` **也是 0**——后者是走 XAML 可视树、被每窗口扫描钩子接管的那一支。⇒ "零引用"这一维不判别；真正判别的是**文字有没有进可视树**：`HdrCaptureWindow` 的字烘在 D3D 图集里（槽位 `:875/:886-906/:1080`），所以门禁、UIA、运行时告警三条路**都看不见它**。这条要按 V13 的形状重述，不能按"该文件没引 LocaleManager"来报。
- **保留但改级**：L3b-#6 的两枚 MFT CLSID，lane 说"占位值"，我没有权威源可核（本机注册表里也没有 AV1 MFT 可比）⇒ 按 [[feedback-authoritative-api-layouts]] 的规矩，这条只能写成"待核对"，不写成结论。

### 10.5 待判的三块，各自已经有判据形状（不是"以后再说"）

1. **D 批全矩阵**：主输入是 L3a 的**声明写入点表**（格式 × 字段 × 行号，含两条"钳像素没改声明"的具体格子：AVIF 中转 PNG 的 sBIT 恒 16、PNG `EncodeSdrAsync` 的三个不可达 case）+ L2 的 **CPU↔HLSL 常数对照表**（唯一实测不一致项：Reinhard 亮度权重 CPU 按 tag 取 BT2020/P3/709 三组、HLSL 恒 709 一组，纯红处输出亮度差至多约 23 %，但生产恒发 SegmentedReinhard ⇒ 当前不可达）。⇒ 曲线逐点判据可做成 T1（WARP + `.cso`，缺失必须判红而不是 Skip——今天 `ServiceTests.cs:106-120` 是 Skip）。
2. **E 批热格子**：主输入是 L5 的**B 域越界值行为表**（约 26 行，每行给"归一前读到什么 / 界面显示什么 / 编码实际用什么 / 行号"）。其中三格是"界面与编码各说各话"的形状：`ColorSpaceIndex:6..99`（归一判 0，但上界 5 是旧 6 项表长、未随 `FormatOrder` 取表）、`QualityAvif:0`（V8）、`OverlayColor` 的兜底串 `#99001833` 不在任何 Tag 里（自增型隐形值）。
3. **F 批降级台账**：L4 已经交了 45 行草稿（位置/构造/行为/toast/日志/档），L3b 交了"后端 × 超时 Kill × 降级留痕"表。⇒ F 批的活是**把 D2/D3 档逐点逼出实测读数**，不是重新找点。

### 10.6 修复档位（本轮按 B 档推，理由写在里面）

| 档 | 内容 | 代价与风险 |
|---|---|---|
| **A 只补判据，不动产品码** | V2/V3/V5/V6/V7/V12 各配一条 T1 锁 + 变异实测；`LogParser`/`FontLoader` 两条纯函数锁；曲线逐点判据；"漏翻"三面差集判据（L6b §⑤ 的形状，含 4 条负样本） | 最低风险，不动已发布行为；**但 20 条 P0 里 16 条修不掉** |
| **B = A + 判据明确的局部产品码修法** | ①无感截图三处静默 → 与选区那根对齐（V1）；②`content:null` 不许当译文（V2）；③R/B 系数摆正（V3）；④哨兵加 PID+时间戳归属，并把 `:273` 那条断言改成正确的期望（V5）；⑤`Save` 挪到 `Normalize` 之后（V6）；⑥凭据：读盘失败不许删（V7）；⑦`LogParser` 两条正则兼容 `#opId`（V12）+ 把 `CHANGELOG.md:1372` 那句改口；⑧AVIF→PNG 保底带上 icc/cicp 与留痕（V11）；⑨识字/翻译走合成后的像素（V10） | 每项都要自带"接上时绿 / 改回旧写法红且点名 / 还原后绿"三段；①②③⑦⑧⑨改动都在个位数行；④⑤⑥动的是并发/时序/持久化次序，**必须先在 T3 上复现一次再改** |
| **C = B + 需要真机量的两项** | L6a-D1/D2（工具栏定位常量 460×50 vs XAML 实际约 892×96，按钮可能被裁到屏外）——按 [[verification-measure-the-shipped-composition]]，这条**必须先量出货构型再改**，不许照 lane 的算式直接动码 | 要起带窗口的真身会话（T3） |

**MFT/QSV 那条（V4）我按"诚实化"处理，不当"修好"处理**：把 vtable 方法改成正确的 COM 调用是可行的，但本机没有 AV1 MFT ⇒ 改完也无法验，等于拿一条不可判的改动进仓。所以本轮做的是：①删掉/改正那句为假的文件头承诺；②让 `[1.8/2]` 或 Test 侧把"这条路径结构上不可达"判成**可读的结论**而不是 Skip（现在"本机没这个编码器"与"这条路是死的"读出来一模一样，正是 §5 那把"恒 SKIP 的锁"的又一种形态）；③把"能不能修 + 换机验"留给有 AV1 MFT 的那台机器。若维护者更希望直接把 QSV 选项从界面撤下（它是 `AvifEncoderKind` 下拉里的一档），那是一句产品决定，等一句口径。

---

## 11. 批次 C 第十条 lane（L7 判据体检）的交付与我的复核 ⇒ G3 的答案（2026-09-29）

L7 的数全部当场量得（方法写在下面每张表的表头），基线读数以 §10.1 本轮那份为准，没有引用上一节的数当现状。

### 11.1 判据体系的规模（量法：`grep -c` / awk 按 `Test_` 方法切段 / 逐行数）

- `File.ReadAllText|ReadAllLines` **88 处**（`CorePipelineTests.cs` 87 + `OcrServiceTests.cs` 1；其余五套 **0**）。
- 读源码的 `Test_` 方法 **53 / 128 个（41 %）**；这些方法里的 `Assert(` **467 条**。
- 语句级分类：**文本匹配型断言 236 / 全工程 1200 条**。
- 被读的 App 侧源文件种类 20 种；`src/TrueToneCap.App/**` 共 **21685 行**。

### 11.2 (b) 类名录：断言的是运行时行为、却只有文本哨兵在盯

**48 条断言 / 33 个方法**（完整逐条表在 lane 交付里，本节只记分布与最贵的五处）：`CorePipelineTests.cs` 的 `292/331/404/1320/1468/1551/1863/2046/2129/2183/2343/2362/2520/2560/2591/2691/2723/2793/3326/3359/3396/3428/3507/3542/3570/3626/4026/4062/4118/4259/4323/4371/4424/4596/4664/4883/5256/5488/5569/5603/5718/5748/5994` + `OcrServiceTests.cs:170-268`。

其中最贵的五处（"绿灯替行为背书"的程度最高）：

| 行 | 它以为在判 | 实际只在判 |
|---|---|---|
| `2129/2160` | `Save()` 真走差集 | 用 `IndexOf` 的**文本先后**冒充执行次序 |
| `2183/2231` | `FormatIndex` 迁移真按标签 | 同文件 `2221-2223` 注释自陈"靠顺序才对"，`2223` 还记着"挪序后 851 全过" |
| `4424/4575,4578` | 真身 T3 判据（`--display-audit`）活着 | **用 T2 证明 T3 判据本身活着** |
| `5569/5576-5589` | `--hdr-probe` 的判据没死 | 同上，用文本证明一条探针判据 |
| `3326/3335-3350` | Auto 解析真早于位深协商 | 全文件 `IndexOf` 先后 |

⇒ **G3 的第一半答案**：1200 条断言里约 236 条是文本匹配，其中 **48 条断言的是运行时行为**。这 48 条不会因为"代码被改坏"而红，只会因为"字面被改掉"而红。

### 11.3 恒真 / 恒跳过 / 无牙（逐条可复跑）

- **`Assert(…, true)` 9 处**：`ColorPipelineTests.cs:504,593`、`CorePipelineTests.cs:1800`、`ServiceTests.cs:183,201`、`UsabilityTests.cs:1415,2000`、`WgcAvifBackendTests.cs:95` ⇒ 1379 里有这几张空票。
- **`FindRepoFile` 找不到就 Skip**（`CorePipelineTests.cs:1982-1991`，我读了原文）：向上找 8 层，返回 null ⇒ 调用方 `Skip`。**53 个哨兵方法全依赖它**；一旦从 `publish/` 或搬过的目录跑宿主，这 53 条集体不判而套件照样 0 失败退 0。全仓唯一把"文件缺失"判红的先例是 `5858-5866`，其余 52 处没跟上。
- **两条 `||` 逃生臂（我逐条复跑过判据两边）**：
  - `4803-4804` `!cps.Contains("ResolveColorSpaceTag(") || cps.Contains("// 不许再调 ResolveColorSpaceTag")`：`CapturePipelineService.cs:266` 的真注释是 `// ⚠ 本函数不许再调…` ⇒ 右臂**当前为假**，所以今天靠左臂有牙；但谁把那句注释顺一下措辞，这条锁就**永久解除**（把"改注释"变成"关掉判据"）。
  - `305-307` 的右臂是左臂的**子集** ⇒ 整条退化成"全文件任何一处有该形参即可"，`EncodeFile` 摘掉形参照样绿。
- **名不符实**：`351-352` 断言名写"Disarm 且在 finally 里"，条件只数了出现次数 ⇒ 把 `Disarm` 移出 `finally` 不红；`4175-4178` 原子写判的是"文本里没有 `= File.Create(path)`、有 `AtomicFileWriter.Write(`" ⇒ 换成 `File.WriteAllBytes`/`fs.Write` 都不红（正对上 §10.3 V11 那条保底的写入路径）。
- **只判数量 >0**：`2626 sites>=100`、`5171 >=5`、`5193 >0`、`1830 两桶非空`。

### 11.4 SKIP 与门禁盲区（含我这一轮确证的一条）

- **§5.2a 的"跳过点名"复核为已修**：`verify.ps1:811-823`，通过路径按 `'跳过:'` 逐行回显，且 `820-822` 比对"点名数 ≠ RESULT 的 skipped 就告警"。⇒ 我本轮基线日志里那 2 条都点到名字（§10.1）。
- **仍有两处跳过不进结论**：`verify.ps1:691-696` 的 `[1.8/2]` 第 ④ 条（`--avif-hdr-depth`）只处理 `fail`，`skip` 既不入 `$colorSkipped` 也不进"本轮【没判】"名单（分母写死 `/3`）；最终汇总 `838-874` 不回声 `[1.7/2]`/`[1.8/2]` 整步跳过 ⇒ **一台没有 ffprobe 的机器能打印"✅ 7 个套件全部通过"而 12 条外部判据一条没判**。
- **`[1.9/2]` 的输入面**（`verify.ps1:725-765`，量法：读实现 + `find` 数文件）：只收 `*.cs,*.xaml`、目录级 `Get-ChildItem -File` **非递归**、且只跟"最新一份源码"比 ⇒ 9 份 `*.hlsl`（改色调映射曲线不碰任何 C#）、4 枚随包 `Resources/*.exe`（avifenc/cjxl/cjpegli/cwebp，正是 `[1.7/2]` 判据的真身）、`Directory.Build.props`、各 `*.csproj`、`tests/fixtures/*.icc`、`scripts/precision/*.py` **全在新鲜度面外**。untracked 不失明（判据走文件系统枚举）。
- **一条读旧件的判据**：`[1.7/2]` 调 `scripts/probes/icc_selfbuilt_referee.py`（`verify.ps1:490`），它读 `%TEMP%\TrueToneCap_ColorTest\*.png`，而那两枚是 `[2/2]` 的 `--color-tests` 产的 ⇒ **顺序在产出之前**，脚本只判 `isfile` 不判时间 ⇒ 有旧文件时拿昨天的 PNG 报"✅ 通过"。
- **断言总数没有基线**：`verify.ps1:859,861` 只打印不比；`Publish.ps1:53` 的 `MinGateAssertions=340` 对真实 1379 ⇒ **删掉约 1000 条断言两处都不红**；而 `verify-publish-gates.ps1:65-74` 桩里的模拟数合计正好 **368**、注释自称"取当前真实门禁规模" ⇒ 那条回归在拿三年前的尺子量今天的闸门。
- **`--selftest` 只被 `workflow_dispatch` 的 `release.yml:71` 调用**，`verify.ps1`/`Publish.ps1`/git 钩子里零调用 ⇒ **本轮这套本地门禁的 App 侧 T1 执行数 = 0**（与我 §2 的结构性结论同向，但这里给的是可复跑的证据）。

### 11.5 App 侧执行覆盖率（报告首页该引的那张表，行数量级当场 `wc -l`）

| 档 | 模块 |
|---|---|
| **有执行**（SelfTest 真调到）≈ 5900 行 / 27 % | `SettingsService`(607，**只两个静态迁移函数**，Load/Save/CanPersist/Normalize 从未被执行) · `AppSettingsData`(372) · `CapturePipelineService`(766，只 `PreparePixelsWithIcc` 且只在 `--display-audit-emit`) · `WgcCaptureService`(1962，只 `--hdr-probe` 那段) · `CapabilityService`(195) · `AppServices`(135) · `CaptureResult`(65) · `ToastService`/`LogService` 各 1 处引用 |
| **只有 T2** ≈ 7100 行 / 33 % | `MainWindow.xaml.cs`(3341) · 四个 `MainWindow.*` partial · `App.xaml.cs`(426) · `LocaleManager`(531) · `StartupManager`(73) · `AppJsonContext`(15) · **`SelfTest.cs`(1276) 由 T2 判它自己活着** · `MainWindow.xaml`(691) · `ToneMapping.hlsl` |
| **零判据** ≈ 8800 行 / **41 %** | `HdrCaptureWindow`(2465) · `SelectionOverlay.xaml(.cs)`(1539) · `AnnotationWindow.xaml(.cs)`(996) · `ThirdPartyNoticesWindow`(533) · `HDRPreviewWindow`(598) · `SilentCaptureToast`(511) · `AnimationRecorder`(370) · `TrayIconManager`(333) · `AnnotationRasterizer`(338) · `SecretStore`(218) · `HotkeyManager`(170) · `ShaderCompiler`(154) · `FontLoader`(123) · `AnimationHelper`(78) · `PowerManager`(54) · `OcrPreviewWindow.xaml` · 另两支 hlsl |

⇒ **G3 的第二半答案**：这一轮九条 lane 报的 20 条 P0 里 18 条落在这张表的"只有 T2 + 零判据"两档（合计 74 %）。**不是这些锁写得不好，是它们够不到那一层**。

### 11.6 一条我当场确证、且影响你现在安全网的事

**git 钩子当前是断线的**（三条只读命令可复跑）：

```
git config --local --get core.hooksPath  →  C:\PLAN\TrueToneCap\.git\hooks
ls .git/hooks（去掉 .sample）            →  post-checkout, post-commit     ← 只有 Qoder 的跟踪钩子
ls scripts/git-hooks                     →  pre-commit, pre-push           ← 仓库自带的两个在这里
```

⇒ `README.md:153-154` 与 `:335` 所承诺的"`pre-commit` 挡编译不过、`pre-push` 挡测试不过"**当前不成立**；今天 `git commit` 不会构建、`git push` 不会跑任何套件，门禁只在有人手工敲 `verify.ps1` 时才跑。仓库自己还留了安装器（`scripts/install-git-hooks.ps1`，`-Status` 可只看状态），而 `install-git-hooks.ps1:88-98` 恰好预警过"hooksPath 一改另一批静默失效"这类事——**这次是反方向发生的**（钩子还在仓里，指针被别的东西挪走了）。

接法是 `.\scripts\install-git-hooks.ps1`（它写 `core.hooksPath`，属改本机 git 配置）⇒ **这条我不自己执行，等维护者一句话或自己动手**。同时该给它补一条判据（L7 建议 #1）：`verify.ps1` 顶部自检"`git config core.hooksPath` 是否指向 `scripts/git-hooks`，不是就红一行"——把 README 的承诺变成机器判得到的东西，否则同一次事故会再来一遍且仍然无人知晓。

### 11.7 最便宜的补牙点（孤儿裁判 + 建议清单里的前段）

- **19 条写了但没有任何门禁调用的裁判**（`precision/` 9 条 + `probes/` 10 条），其中 5 条现在就能接：`wg_container_roundtrip.py`（跨容器同像素一致，正是 09-19 TIFF 事故的同类判据）、`jxl_hdr_p3_transfer_readback.py`（JXL HDR 传递函数的独立实现）、`avif_colr_dump.py`（直读 ISOBMFF 的 `colr(nclx)` 四字段——文档明说 ffprobe 对某些值直接不打印 ⇒ 现在"写了但被省略"与"没写"不可区分）、`svt_caps_matrix.py`（色度×位深**跨轴**，仓内现有判据都只按轴）、`mem_growth_probe.ps1`/`crash_probe.ps1`（长跑与崩溃面，今天整个体系回答不了"跑一天会不会涨死"）。
- **补牙优先序**（本轮按这个做）：①钩子自检 + 断言总数入库比基线；②`FindRepoFile` 缺文件判红（照 `5858-5866` 先例）+ 哨兵覆盖面计数；③两条 `||` 逃生臂删右臂或改就近切片；④`351` 的 finally 半句、`4175` 的原子写改成真跑"中途抛异常 ⇒ 最终路径不得存在文件"；⑤`icc_selfbuilt_referee` 挪到 `[2/2]` 之后或比 mtime；⑥`[1.9/2]` 输入面扩到 `.hlsl/Resources *.exe/csproj/fixtures` 且逐文件对撞；⑦把 §11.2 名录里**读 Core 的那批**（`292/331/404/1320/4165/4259/4596/5488/5603/5748`）提级成 T1——它们读的是 Test 已引用的程序集源码，改成真调函数取命令行/真走留痕路径，零接线成本。
- **我自己这一轮造成的一件事**：本轮基线日志最初是"`Set-Content -Encoding UTF8` 写前段 + `Tee-Object -Append` 写后段（PS5.1 的 Tee 是 Unicode）"⇒ 一份文件两种编码，`grep` 判成二进制。凡是读不出字的尺子都可能造假负结论，已把包装器改成全程单一 UTF-8 并加了 `-LogName` 参数（默认名不再指向本轮那份证据件）：`tests/manual/safety/gate-runner.ps1`。

---

## 12. 补牙与修复轮：11 处产品码改动、6 把新锁，以及我自己的 6 处尺子错（2026-09-29）

### 12.1 当场读数

- **修复后的全量门禁**：`tests/output/round3-fixes-gate2.log` ⇒ **7 个套件全绿、1407 断言 / 0 失败 / 2 跳过**，逐套件 897+213+14+7+12+43+221=1407 与汇总行逐字一致（`[2/2]` 44.5 s）。
- 该轮 `GATE_EXIT=1` **不是套件判红**，是本轮新加的两条 fail-closed 生效：① `[1.95/2] 本机钩子【没接】`；② `没有 scripts\gate-baseline.json ⇒ 断言总数这一格【没判】`。两条都在末屏的"本轮判据未覆盖"清单里点名（以前这类只打一行黄字，跑完看不到）。
- **构建**：两次 `--no-incremental` 全解，均 **0 警告 0 错误**（22.9 s / 17.5 s）。
- 基线轮（带 `-WriteGateBaseline`）在写这一节时仍在跑，落 `tests/output/round3-baseline-gate.log`；`scripts/gate-baseline.json` 的逐套件数字以那一轮为准，本文不预告。
- 断言数从 1379 → **1407（+28）**，且**全部落在 `--unit-tests`（869 → 897），其余六套逐套件不变** —— 这两行数字是两次汇总行逐行对出来的。我原本手算出"新增 25 条"想直接写成分解式，与实测的 28 对不上 ⇒ 那个分解就不写，只留能量到的部分。新增/重写的锁名：`Test_ReviewRoundFixes_HaveTeeth`、`Test_CaptureRoots_FailureNeverSilent`、`Test_AnnotationComposedForAllDestinations` 与三条 `Test_EncoderCrashSentinel_*`。

### 12.2 产品码改动（11 处，全部留在工作树，未提交）

| # | 文件:行 | 改了什么 | 判据落点 | 强度 |
|---|---|---|---|---|
| 1 | `Core/Services/TranslationService.cs:781` + 新增 `RequireTranslatedContent` | LLM 回 200 而 `content` 为 null/空白 ⇒ 抛，交给既有阶梯换后端并写 `_lastBackendNote`；**不再把用户自己的源文本当译文返回** | `Test_ReviewRoundFixes_HaveTeeth` 两臂（空要抛、有内容要原样去空白） | **T1** |
| 2 | `Core/Services/BitmapPreprocessor.cs:94/124/183/258` | BT.601 权重落回正确通道（BGRA 下 `R*77 + G*150 + B*29`），四处一次改齐 | 一对**镜像夹具**：`(R=255,G=120,B=0)` 不算深底 + `(R=0,G=120,B=255)` 算深底 —— 装反时两臂同时反 | **T1** |
| 3 | `Core/Encoding/FormatEncoders.cs:1540` 保底 + `FormatHelper.GetColorMetadata` 拆出裸值入口 | AVIF 全后端失败的 PNG 保底**带上与主路同源的 icc/cicp**，并补一条"不是 AVIF 而是 PNG"的留痕 | 同源三臂（同输入必须同输出）+ 就近源码锁（保底那段必须有 6 参调用与 notifier） | T1 + T2 |
| 4 | `Core/Encoding/EncoderCrashSentinel.cs`（`Arm` 写 PID、`IsBlocked` 问在途、`SentinelPathFor` 转 internal） | 同进程在途的 Arm 不再被读成"上次进程崩了"（⇒ 不再删别人的保护、不再整进程禁硬件、不再弹假原因） | 重写三条哨兵锁 + **伪造别的进程 PID 的残留**当对照臂 | **T1** |
| 5 | 同上 `IsBlocked` 的 reader 作用域 | **本轮新抓的第 11 个 bug**：`using var reader` 还开着句柄就 `File.Delete` ⇒ Windows 上恒定抛、被 `catch{}` 吞 ⇒ "读后即消费"一次也没删掉文件 ⇒ 崩溃残留永不清除，正是 2026-09-20 注释声称修掉的"硬件被永久关掉" | 两条哨兵锁都断言"消费后 `File.Exists` 必须为 false"（**只断言返回值时它是绿的**） | **T1**，由新断言抓到 |
| 6 | `App/MainWindow.xaml.cs:1334/1466/1494` | 无感截图三处静默失败接上状态栏 + 失败提示（文案全部复用同文件另两根入口的既有中文，不新开串） | `Test_CaptureRoots_FailureNeverSilent`（三个锚点各两臂） | T2（行为面要 T3） |
| 7 | `App/SelectionOverlay.xaml.cs:716` + `MainWindow.xaml.cs:2034/2041` | **识字/翻译改吃合成后的像素**（SDR 路扩守卫；HDR 路复用同文件既有的 `ComposeAnnotatedSdr`）⇒ 马赛克盖住的原文不再被 OCR 读出来 | `Test_AnnotationComposedForAllDestinations`（守卫四名字 + HDR 两个 case 走合成、且不许直接喂 `sdrRegion`） | T2（端到端要 T3） |
| 8 | `App/Services/SettingsService.cs:131→139` | `if (migrated) Save()` 挪到 `Normalize` **之后** ⇒ 不再把未归一的越界值钉回盘上并盖章固化；顺带确认明文 Key 清除不受影响（`SecretStore.Set` 在迁移函数里当场已做） | 新增**次序锁**（旧锁只钉"存不存在"，对这种错位是盲的） | T2 |
| 9 | 同上 `_loadedFromDisk` + `SaveSecret(…, allowDelete)` | 设置文件缺失/反序列化抛时**不再删加密库里的凭据**；用户在设置页主动清空那条路仍然真删 | 形态锁（`else if (allowDelete)` 且不许再出现无条件 `else { SecretStore.Delete(`） | T2 + T3 两臂 |
| 10 | `Core/Encoding/MftEncoderNative.cs`（只改自述） | 把"只走微软自己导出的扁平函数（含 `IMFTransform_*`）"这句**假陈述**改成逐条实述：哪三类是真导出、哪五个是 COM vtable 方法却按扁平导出声明 ⇒ 该路径当前不可达；CLSID 与 `MFSet*`/`MFSampleAddBuffer` 一律标【待核】而不是替它下结论 | 无（文案） | — |
| 11 | `LogViewer/Services/LogParser.cs:104/109` | 两条正则各加可选的 `#opId ` 段 ⇒ 一次截图期间写的日志行不再在查看器里整批消失 | `[1.96/2]` 对撞（`scripts/probes/logline_format_check.ps1`，四臂：对照/被试/两条负样本） | **T1（尺子从源码现取正则）** |

### 12.3 门禁自己的五处补强

1. `[1.95/2]` 闸门接线自检（非阻断，但进"未覆盖"清单）——把 README 的承诺变成机器判得到的一行。
2. `[1.96/2]` 写方 ↔ 读方行格式对撞（跨进程契约，此前无编译期耦合）。
3. `[1.9/2]` 输入面从"只 `*.cs/*.xaml`"扩到 `*.hlsl` / 随包四枚 `Resources\*.exe` / `*.csproj` / `*.props` / `tests\fixtures` 的 ICC 与 PNG。
4. `$script:GateGaps` 台账 + 末屏"本轮判据未覆盖"回显（`-SkipPrecision`、python/ffprobe 缺失、`[1.8/2]` 第 ④ 条 skip 全进清单）。
5. `-WriteGateBaseline` + 逐套件断言数比对（变少即红）——替掉 `Publish.ps1:53` 那个 340 对 1379 的假阈值。

### 12.4 本轮我自己造成的事（接上一节的编号）

18. **一份日志两种编码**：`Set-Content -Encoding UTF8` + `Tee-Object -Append`（PS5.1 是 Unicode）混写 ⇒ `grep` 判成二进制。已改单一 UTF-8。
19. **我新写的判据第一次跑就打了假 ✅**：`(($r -or $hooksNow) -ieq $hooksWant)` —— PowerShell 里 `-ieq` 优先级高于 `-or`，括号把 `-or` 折成 `[bool]`，随后 `-ieq` 按**左操作数类型**把右侧字符串转成 `$true` ⇒ 非空路径一律"已接线"。用独立探针复现（`bool-vs-str : True`、`type-of-paren : Boolean`）后改成"先算实际路径、再比较"。**教训**：新锁上线前必须做一次"它能不能判红"的负样本，我给了 `[1.96/2]` 四条臂，却没给这把 —— 结果它当场骗了我一轮。
20. **`==` 比 `byte[]`**：`BuildSdrCicp` 每次新分配 ⇒ 三条"两入口同源"恒假红。改 `SequenceEqual`（并把这个坑写进 helper 的注释）。
21. **能传但不生效的假旋钮**：包装器声明了 `-VerifyArgs` 却没接进调用 ⇒ `-WriteGateBaseline` 那轮什么都没写，只能再跑一轮。
22. **误报 lane 的落地状态**：F1 因上游限流回传"error"，我据此写下"Core 一个文件都没动"——而 `git status` 里五个文件全是 M。判据应该是盘上实况，不是子代理的回传措辞（同族教训：lane 末句会误报盘上实况）。
23. **我自己的一条空转臂**：写"归属校验"那臂时，把不匹配的残留放到**别的后端**的路径上、却断言"真正的主人来读仍是残留"——那是"文件不存在"判绿的空转臂。写完立刻自读时发现，改成诚实形态（NVENC 自己没残留 ⇒ false，且不许替别人删）。

（上一轮"把包装器的 exit 0 当门禁 exit"那条，本轮靠"`-File` 起子进程 + 末尾 `exit $gate`"规避，且我读的始终是日志里 verify.ps1 自己写的 `GATE_EXIT=` 那一行。）

### 12.5 变异实测读数（`tests/manual/safety/mutation-plan-2026-09-29.md` 的 13 条，逐条跑）

流程：`cp -p` 留 8 份还原副本 + md5 清单 → 破 → 构建 → 跑套件按**断言名归属** → 还原 → md5 逐个对回 → 复跑。还原核对：8/8 逐字一致、`grep -rn MUT-M src` 命中 0。

| 变异 | 破的写法 | 结果 |
|---|---|---|
| M1 | 灰度权重退回 `B*77 … R*29` | 两条镜像臂**同时反向**（暖色/冷色都红）⇒ 夹具不是恒真 |
| M2 | LLM 调用点退回 `content?.Trim() ?? text` | **第一版没抓住**（下面①）；补锁后复跑：恰好那 2 条红，其余绿 |
| M3 | 去掉"在途 ≠ 残留"那道判断 | 4 条红（`ArmDisarm` 2 + `ConcurrentArm` 2），对照臂"别的进程留下的仍判崩溃"仍绿 |
| M4 | reader 未关就 `File.Delete` | 已由 §12.2-5 的两轮门禁实况作证（红过 → 改完转绿），未另跑 |
| M6 | PNG 保底退回 5 参 / 删掉留痕 | 拆成两把锁后**各咬各的**：只破声明 ⇒ 声明红、留痕绿 |
| M7 | `if (migrated) Save()` 挪回 `Normalize` 之前 | 次序锁红；旁边那条"迁移结果必须驱动写盘"**仍绿** ⇒ 作证旧锁为何不够 |
| M8 | `else if (allowDelete)` → `else if (true)` | 凭据形态锁红 |
| M9 | SDR 守卫去掉 `Ocr/Translate`；HDR `case Ocr` 退回喂 `sdrRegion` | 2 条红，**HDR `case Translate` 仍绿**（故意留的控制臂） |
| M10 | 三处 `SetStatus`/`ShowCaptureFailed` 改名 | 3 条红、三条"锚点仍在源码里"全绿 ⇒ 两把锁分工不重叠 |
| M11 | 读方正则去掉 `#opId` 段 | **第一版没抓住**（下面②）；改尺子后：只红 `s_headerRegex` 的被试臂、`RC=1`、退出码 1 |
| M12 | 手改基线数（把 `--ocr-tests` 记成 20） | 新机制开火：`❌ --ocr-tests 断言数从基线 20 掉到 12（少 8 条）` 并进 `GATE_EXIT=1` ⇒ "删判据会红"从此是判出来的，不是承诺 |
| M13 | 把钩子自检改回 `-or` 折 bool 的写法 | 已在 §12.4-19 用独立 PS 探针复现（`type-of-paren : Boolean`、`bool-vs-str : True`），不另占构建 |

**两处"没抓住"是本节唯一值钱的部分**，因为它们都是读代码看不出来的：

1. **只锁 helper、没锁调用点**。`RequireTranslatedContent` 的两条 T1 臂直接调那个 internal 函数，把调用点改回旧写法时它们照样全绿 —— 与本轮 §2 那个结构性缺口（"这行字在源码里出现过"≠"这行代码被跑到过"）是同一件事，只是这次发生在我自己刚写的锁上。补的是两道就近锁：切片取 LLM 解析那一段（切到方法结束、并先 `StripLineComments` —— helper 的 XML 文档里就抄着旧写法当反例，不剥注释会永久假红），断言"调用点必须出现 `RequireTranslatedContent(content)`"且"这一段不许再出现 `?? text`"。
2. **尺子能静默跳过自己的输入**。对撞脚本原本按"`@"…"` 后面紧跟 `RegexOptions`"抽正则，我把一条正则破掉并在行尾留注释，它就跳过那条、拿另一条去判 ⇒ 报假绿。改成**按变量名锚定 + 两条正则各跑四臂 + 抽不到就退非零**：看不见输入时必须报错，而不是给一个结论。

**一条新的方法学读数**（M12 那轮顺带量到的）：同一套件里 **T2 文本锁跟着源码走、T1 执行锁跟着二进制走**。那次 `-SkipBuild` 跑的是 M3 还在的旧二进制，于是 M3 的 4 条执行锁红、而 M2 的 2 条文本锁已经绿（源文件当时已还原）—— 一轮里同时出现两种真值。⇒ 混跑时可以按 T 级把红绿分开归因，但**不能把这一轮的总读数当成"当前树"的结论**（本仓规矩：产物是旧的 ⇒ RESULT 作废，这是它的一个新表现形式）。

**仍然没被锁住的**（诚实记账）：M1 只咬到 `SampleRegion`（`:94`）那一处，`AdaptiveThresholdV2:124` / `EnhanceContrast:183` / `AdaptiveThreshold:258` 三处同族算式**没有各自的判据**（本轮只是把它们一次改对）；M3 未覆盖"两个 TrueToneCap 实例并发硬件编码"（彼此看到的都是别人的 PID，仍会被当残留 —— 需要实例标识或文件锁）；M6 的"保底真出带声明的 PNG"仍只有就近锁，要行为级判据得把 `AvifFallbackHelper` 从 `file static` 改成 `internal`（一词），那件事留给下一批和 D 批一起做。

### 12.6 终验读数与本轮收口

- **终验门禁**：`tests/output/round3-final-gate.log` ⇒ `GATE_EXIT=0`、**7 个套件全部通过、1410 断言 / 0 失败 / 2 跳过**（`[2/2]` 47.8 s），逐套件 900+213+14+7+12+43+221=1410。轮前轮后设置指纹**逐字不变**；末屏"本轮判据未覆盖"只剩一条：`[1.95/2] 提交闸门未接线`。
- **断言基线已入库**：`scripts/gate-baseline.json`（总断言 1410，取数 2026-09-29 07:22:12，逐套件带跳过数）。它由 `-WriteGateBaseline` 从那一轮真读数生成，不是手抄。
- **M14（下限的牙）**：把基线里的 `总断言` 临时改成 99999 后跑发布门禁回归 ⇒ `❌ 断言总数 1410 低于下限 99999 —— 门禁疑似被静默删减` 出现两次、场景 B/D 从"放行"翻成拒绝、harness 退 1；还原后 5/5 场景重新符合预期，基线文件 md5 与副本逐字一致。这一条读数同时作证两件事：**新下限真的从基线文件读**，且 **桩发的数就是基线里的数**（那句"1410"来自桩按基线逐套件模拟的合计）—— 原先那个合计 368 的手抄桩不会再悄悄漂。
- **本轮改动面（全部未提交，按规矩等授权）**：产品码 11 处（Core 5 / App 4 / LogViewer 1 / 内部分解 1）；判据与脚本 5 个文件（`CorePipelineTests.cs`、`scripts/verify.ps1`、`Publish.ps1`、`scripts/verify-publish-gates.ps1`，加上一份新尺子 `scripts/probes/logline_format_check.ps1`）；新基线件 1 份。整份工作树另存 `tests/manual/safety/wip-2026-09-29-final.patch`（129 968 字节，17 个跟踪文件）。

### 12.7 仍开的账，按"卡在谁那里"分四堆

| 堆 | 条目 |
|---|---|
| **等产品一句话** | ①QSV/MFT：撤下拉档，还是留着等装了 AV1 MFT 的机器再修 COM 互操作（本轮只把自述改诚实）；②修订号"交还门"的范围要不要收窄（现在一动 `CurrentSettingsRevision` 就把五档质量 + `ColorSpaceIndex` 整体拉回默认，见 §11 之 L5-#4）；③`install-git-hooks.ps1` 装不装（写本机 git 配置，我没动） |
| **等 T3 授权（要起带窗口的真身会话）** | 英文界面"N 处显示中文"计数读全（要先展开 30 个下拉）；overlay/热键冲突回退链实机；剪贴板粘进真实应用；`HdrCaptureWindow` 那一路的整窗中文目视；L6a-D1/D2 的工具栏越界（必须先量出货构型再改）；`--display-audit` 的 `landingFailures` 反向路径（要一次"故意喂错"） |
| **下一批实测（不需授权，需要跑）** | 批次 D 全矩阵三层读数（`--color-matrix` 挂在既有 `FormatBench.cs` 分派器上，不新建 harness）；批次 E 的 20 热格子（输入 = L5 的 B 域表）；批次 F 的 D2/D3 档逐点逼出（输入 = L4 的 45 行台账 + L3b 的后端留痕表）；`jpeg_pixel_probe.py --ladder` 在真机卡片上第一次跑；黑帧 20–30 张连拍统计 |
| **已知零判据（本轮只登记）** | App 侧 41% 代码零判据（`HdrCaptureWindow` 2465 行、`SelectionOverlay` 1539、`AnnotationWindow` 996 等）；`verify.ps1` 的 53 处 `FindRepoFile` 缺文件即 Skip（集体失明）；恒真 `Assert(…, true)` 9 处；孤儿裁判 19 条里剩下的 14 条；`[1.7/2]` 的 `icc_selfbuilt_referee` 读 `[2/2]` 的产出（旧件可能报绿）；L6a 那 30 条界面缺陷里的绝大多数（本片零判据，只能 T3） |

---

## 13. 批次 D 第一层：把"第三方解码像素"接进门禁（2026-09-29）

### 13.1 为什么不新建 harness

原计划（§4-L3 / §6-D 批）写着"全矩阵三层读数"。量完才发现前两层早有常驻尺子：`param_sweep_check`（参数进没进码流）、`cicp_consistency`（跨行声明一致）、`icc_readback_matrix`（嵌进去的那份 profile 字节）、`gamut_depth_consistency`（矩阵与位深解耦）、`ramp_levels`（实际交付几级）。**缺的是第三层——别人写的解码器看到什么像素**，而这一层的两条尺子 `wg_container_roundtrip.py` / `audit_roundtrip.py` 已经写好，只是从没被任何门禁调用过（§11.6 的孤儿裁判名单里）。⇒ D 批的第一件事不是造东西，是接线。

### 13.2 接线与实测读数

| 判据 | 单跑读数（接线的同时先手跑一次） | 门禁里的状态 |
|---|---|---|
| `wg_container_roundtrip.py`（同图跨容器像素一致，基准=该色域 PNG） | 9 组比对：BT2020/DisplayP3/sRGB × AVIF/JXL/TIFF，最大差 **1–2 LSB**（容差 8/1），平均 0.04–0.46；`不一致容器数 = 0，解码失败/全黑 = 0` | `[1.7/2]` 第 56 行 ▶，本轮通过 |
| `audit_roundtrip.py`（ffmpeg/ffprobe 读回容器自述与精度） | `[RESULT] 文件 20，问题 0`；`hdr_png.png rgba64be smpte2084 通道单调✅`；`hdr_tiff.tiff … 声明n/a(容器无 CICP)` | `[1.7/2]` 第 54 行 ▶，本轮通过 |
| `jxl_hdr_p3_transfer_readback.py` | **本机跑不起来**：`jxlinfo` 退 `3221225781` = `0xC0000135`。把 `build\lib\Release`（`jxl.dll`/`jxl_cms.dll`/`jxl_dec.dll`/`jxl_threads.dll` 都在）追加进子进程 PATH **不解决**；并排量过"是不是 `precision_env` 改了环境"这个假设——PATH 长度 1878 / 47 项在 import 前后完全一致、退出码不变 ⇒ 假设不成立，是缺别的依赖 | 未接线（诚实 SKIP，不是假绿）；同题在 `[1.8/2]` 由 C# 侧 `--jxl-color-product` 仍判 |

- 接线的 fail-closed 形态照抄既有两组样张的做法并加严：`--container-audit` / `--wg-samples` 退出码非 0 ⇒ `exit 1`；**退出码 0 但样张目录是空的** 也 ⇒ `exit 1`（"生成了个寂寞"不能算通过）。样张不拷第二份（`precision_env` 自己找 `artifacts\precision\probe\<kind>`，多拷一份就多一处"哪份是旧的"）。
- `[1.7/2]` 的 SKIP 从此也进末屏"本轮判据未覆盖"清单（以前只在中间刷一行黄字，跑完没人看到）。
- 终验：`tests/output/round3-referees-gate.log` ⇒ `GATE_EXIT=0`、**1410 断言 / 0 失败 / 2 跳过**、`✅ 外部判据通过`；两条新判据**没有**出现在"未覆盖"清单里（清单只剩钩子那条）⇒ 这一格是"判了"，不是"跳了"。设置指纹逐字不变。

### 13.3 `audit_roundtrip` 顺带点出的一个待拍板项（不是缺陷，我没动实现）

它的"精度声明审计"段如实写着：**16-bit 容器不写 `sBIT` = 宣称 16 位全有效，而源只有 ~11 位有效精度**（Float16 源：1 隐含 + 10 尾数）。实测行：`sdr_jxl16_srgb.jxl raw_bits=16`、`sdr_png16_srgb.png … 无(视同全有效)`。
今天的产品在 10/12-bit 是老实写 sBIT 的（`ManagedPngEncoder.cs:168-172`，且 `ColorPipelineTests.cs:639-650` 钉着），到 16-bit 反而不写——而 `:596` 那条断言把"16-bit 不写 sBIT"钉成了**期望行为**。⇒ 于是这一格有两种读法：①"sBIT 只在少于容器位深时才有意义，16=全有效是自明的，不写才对"；②"源只有 ~11 位，16-bit 容器不写 sBIT 就是宣称比交付强"。这是产品口径问题，**等一句拍板**；要改就得同时动 `:596` 那条断言，我不能单方面放宽。

### 13.4 矩阵覆盖现状（下一格要补什么，按量到的文件数说）

- `wg`（跨容器像素层）：12 件 = 3 色域 × 4 容器（PNG/TIFF/AVIF/JXL）⇒ **缺 WebP、JPEG、JPEG-LI（含增益图底图）、以及位深这一轴**。09-19 的 TIFF 事故正好是这一族，所以补 WebP/JPEG-LI 是有对象的、不是凑格子。
- `audit`（自述层）：20 件，其中 avif 6 / tiff 4 / png 4 / jxl 3 / jpg 2 / **webp 1** ⇒ WebP 那一列薄。
- 批次 E 的 20 热格子仍待跑，输入用 §11 里 L5 那张 B 域越界值行为表（约 26 行，每行给"归一前读到什么 / 界面显示什么 / 编码实际用什么 / 行号"）；其中三格是"界面与编码各说各话"的形状：`ColorSpaceIndex:6..99`（上界 5 是旧 6 项表长、未随 `FormatOrder` 取表）、`QualityAvif:0`（§10.3 V8 只确证了一半）、`OverlayColor` 兜底串 `#99001833` 不在任何 Tag 里。

### 13.5 一条没做成的事，记下来省下一轮的时间：AVIF→PNG 保底的**行为级**判据

§10.3 V11 那条保底修复今天只有就近文本锁（M6），行为级判据想做但**卡在"怎么让编码确定地失败"**。三条路都量过一遍：

1. **删/改名 `native\avifenc.exe` 再跑** —— `NativeLibraryResolver.cs:51` 把 native 目录定死成 `AppContext.BaseDirectory\native`，**没有任何环境变量能改指**；而且它是按需从内嵌资源提取的，删掉后下一次调用很可能又给提回来。更要紧的是：测试宿主与产品**共用同一份 bin**（本机还有并发会话），拿改名去造故障等于临时毁别人的构建产物。⇒ 否。
2. **把 `AvifFallbackHelper` 从 `file static` 改 `internal` 后直接喂 0×0/1×1 像素** —— 一词之改能让 Test 够到它，但"0×0 会不会稳定让 avifenc 失败"我没法在不跑的前提下断言；如果它在更早的入口就被参数校验挡住，这条判据就只是"我调到了一个抛异常的地方"，不证明保底路径。⇒ 待定，需要一次带故障注入的实验。
3. **喂一个后端确定不支持的跨轴组合** —— 例如 12-bit + 4:4:4（L7 §11.6 正好指出 `svt_caps_matrix.py` 是孤儿、跨轴组合今天无人判）。这条路最像"真实世界的失败"，但要先把能力表与 avifenc 实际行为对齐一次才能确定哪个组合必败。⇒ 下一轮从这里切入最省。

顺带一条有用的负数：`[1.7/2]` 那 14 条判据里**没有任何一条会走保底路径** ⇒ 三条"换格式"保底（WebP→PNG、AVIF→libaom→PNG、其余抛）今天全靠读码 + 文本锁，用户真拿到保底件时那条路是否带声明，门禁不知道。这正是 F 批"D2/D3 档逐点逼出"要还的债，不是 D 批能顺手做完的。

### 13.6 把像素层从 12 件补到 36 件（WebP / JPEG LI / 位深轴），并先证明这根轴不是假的

`--wg-samples` 原来只产 3 色域 × 4 容器 = 12 件。本轮补两列容器（WebP、JPEG LI）与一档位深（8-bit），成 **3 × 2 × 6 = 36 件**，全数产出、无失败（`tests/output/wg-extend-1.log`）。

动手前先量了"加了等于没加"这个风险（记忆里那条"判开关有无效果要三向对照"）——**位深轴确实改变了产物**：

| 对照 | d10 | d08 |
|---|---|---|
| `sRGB_png.png` | md5 `3b49b3a4…` / 1798 B | `2aa2051f…` / 1078 B |
| `DisplayP3_png.png` | `7204f371…` / 2663 B | `86c9bfc2…` / 1942 B |
| `BT2020_png.png` | `39669e74…` / 2657 B | `a0e94113…` / 1935 B |
| AVIF 容器（ffprobe） | `yuv444p10le` | `yuv444p` |

⇒ 请求的位深真的抵达容器，且 4:4:4 在两档都保住；PNG 两档逐字节不同，说明"10-bit 走 16 位容器 + sBIT / 8-bit 走 8 位"两条路各走各的。**如果这张表里出现"逐字节相同"，这一格就该判成缺陷而不是判成绿**——所以先量它。

裁判侧读数（`tests/output/wg-extend-2.log`）：**30 组**比对（36 件 − 6 个 PNG 基准）、`不一致容器数 = 0，解码失败/全黑 = 0`、`EXIT=0`。新加的两列都在容差内（WebP/JPEG-LI 属有损，容差 8 LSB；`LOSSY` 集合本来就含 `.jpg/.webp`，所以不用改裁判）：例如 `BT2020_jpeg_li.jpg` 的 `red-m=(63,17,4)` vs `BT2020_avif.avif` 的 `(63,17,5)`。

两处实现细节记下来，免得下一个人重踩：

1. 8-bit 那组用**前缀自成一组**（`sRGBd08_png.png`）而不是加后缀 —— 裁判按 `f.split("_")[0]` 分组，后缀会被并到 10-bit 那组里、拿 8-bit 去比 10-bit 的基准，造出假红或掩盖真红。
2. 生成器现在**任一格子没产出就 `Environment.ExitCode = 1`**（原来只打一行 ❌ 就退 0）。理由：少一格 = 这层覆盖范围悄悄缩水，而裁判只会"看到更少文件"、永远不会红。`[1.7/2]` 的 fail-closed（退出码非 0 / 退出码 0 但目录空 ⇒ `exit 1`）现在才接得住这种情况。

### 13.7 保底路径的行为级判据：两条注入方案已量过，一条不成立、一条待写

要把 §10.3 V11（AVIF→PNG 保底带声明）从文本锁升级成"真跑一次保底"，卡点从来不是"够不够得到那个函数"，而是**怎么让编码确定地失败**。本轮量完两条：

1. **喂 avifenc 必败的档位 —— 不成立。** 事实一：随包的 `avifenc 1.4.2` 对 `-d 16` 是**参数期就拒**（`ERROR: unsupported depth or depth extension: 16`，退出码 1，连输入文件都不打开）。事实二：但产品的 AVIF 路**发不出这个参数** —— `NativeAvifEncoder.GetDepthArg`（`:245-249`）自己先过一道 `FormatHelper.AvifContainerDepth` 钳制，`OutputBitDepth=16` 早被 `SettingsService.Normalize` 收成 12。⇒ 除非我为了测试去绕过生产钳制（那测的就不是产品路了），此路不通。
2. **让 avifenc 写不出目标文件 —— 可行，下一轮照这个写。** 测试里对目标 `x.avif` 持一个 `FileShare.None` 的句柄 ⇒ avifenc 撞共享冲突而失败 ⇒ 落进 `catch` 的 PNG 保底；而保底写的是 `Path.ChangeExtension(path, ".png")` = `x.png`，**那条路径是空的** ⇒ 保底能真正走完。然后断言三件：`x.png` 存在、它的容器里有 iCCP/cICP（用外部实现读，不用本仓解析器）、`EncoderFallbackNotifier.LastReason` 说了"不是 AVIF 而是 PNG"。
   这条还是 F 批的第一格真数：它属 D1 档（有日志、有留痕），正好给 §12.7 里"保底层没有任何判据走得到"那条债起头。

**别再去动的东西**：测试宿主 `src/TrueToneCap.Test/bin/…\native\` 里四件工具（avifenc/cjxl/cwebp/cjpegli）**都在位**，所以"改名让它失败"在 Test 里既不可靠也会污染与产品共用的 bin（本机还有并发会话）；`NativeLibraryResolver` 的 native 目录定死在 `AppContext.BaseDirectory\native`，**没有任何环境变量可以改指**（`:51`）。

### 13.8 像素层接线的终读数，以及我又犯了一次上一轮记过的错

- `tests/output/round3-wgextend-gate2.log` ⇒ `GATE_EXIT=0`、**1410 断言 / 0 失败 / 2 跳过**、`[1.7/2] ✅ 外部判据通过`；`audit_roundtrip` 与 `wg_container_roundtrip` 两条都在里面**判了**（既没进 ❌，也没进末屏"本轮判据未覆盖"清单——那条清单的写法是"没判才进"，所以"没进"就是有结论）；轮前轮后设置指纹逐字不变。
- **这轮第一次跑是我自己作死的**：只 `dotnet build src/TrueToneCap.Tools` 就起了门禁，于是 `[1.9/2]` 抓到 Test 宿主（07:18:35）比源码（`FormatBench.cs` 07:2x 改过）旧 ⇒ 整轮作废（`round3-wgextend-gate.log` 的 `GATE_EXIT=1`）。这是上一轮 §附 16 记过的同一件事——"要么把编辑做完再起门禁，要么起完之前不碰 `src/`"。这条判据不看改动进不进得了那个二进制，它就是要作废整轮；本轮是它第四次抓我，前三次都是别人的改动撞上来，这次是我自愿。

### 13.9 新发现（P1）：AVIF 的"确保截图不丢失"只覆盖硬件分支，软件后端那条路一失败图就没

写 §13.7 那条行为判据时当场撞出来的，**不在任何 lane 的清单里**（它们都只看到 `:1531` 那段 catch 存在；没有一条路把"选中软件后端时 avifenc 失败"跑到产物上）。

- **形状**：`AvifEncoder.EncodeSdrAsync:427-435` 只做 `AvifEncoderSelector.SelectByKind(...)` 然后 `be.EncodeAsync(...)`；而"libaom 也失败 ⇒ 回退 PNG"那段 catch 挂在 **`AvifFallbackHelper.FallbackToLibAom` 里**，`FallbackToLibAom` 只在**硬件后端失败后**才被调用（`:802/1035/1049/1063/1072` 五个调用点全在硬件分支）。⇒ 选中的后端本来就是 avifenc 时（`Auto` 在没有 AV1 编码硬件的机器上，或用户显式 `LibAom`），avifenc 一失败异常直接冒到调用方：**没有 PNG、`EncoderFallbackNotifier.LastReason` 也还是 null**。
- **实测读数**（注入 = 本进程以 `FileShare.None` 独占 `shot.avif`；avifenc 是独立进程 ⇒ 撞共享冲突，出处 `tests/output/avif-fallback-behavior.log`）：
  ```
  ❌ AVIF 保底：真的落了一枚非空 PNG …  抛的是 InvalidOperationException:
      [AVIF] avifenc 失败 (exit=1) ERROR: Failed to open file for write: …\avif-fallback\shot.avif
  ❌ AVIF 保底的 PNG 必须带 iCCP 或 cICP …        ❌ AVIF 保底必须写降级留痕 … 实得 <null>
  RESULT suite=--color-tests passed=213 failed=3
  ```
- **用户侧后果（与 §10.3 V1 叠加）**：选区那根入口至少能看到 `❌ 保存失败: [AVIF] avifenc 失败 (exit=1)…`；**无感那根**入口本轮修之前是"只写日志、按了热键什么都没发生"，修之后能看到提示，但**图仍然丢**，而这条函数自己的注释写着"确保截图不丢失"。
- **修法（不抄第二份保底）**：把那段 PNG 保底抽成 `AvifFallbackHelper.PngLastResort(...)`（同一个 `file static class` 内、`internal` 可见），原 catch 与 `EncodeSdrAsync` 都调它；`OperationCanceledException` 原样上抛，`AggregateException`（= 硬件链里的保底已经试过 PNG、连 PNG 都写不出去）也原样上抛，不重复同一个失败。
- **修完读数**：`tests/output/avif-fallback-behavior2.log` ⇒ 三条臂全 ✅、`RESULT suite=--color-tests passed=216 failed=0`。三段读数齐不缺：**旧写法红 = 修前那次运行本身就是**（异常原文被断言点名，不用另做变异），改完绿 = 216/0，接线绿 = §13.10 那轮门禁。
- **仍未覆盖的同类**：`WebP→PNG`（`:455-470`，只 catch `DllNotFoundException` ⇒ cwebp 在位但编码崩了同样不保底）与 `JPEG LI / JXL` 的"后端不可用"路径。这次只补了 AVIF 一条，**不能记成三条保底都补了**。

### 13.10 收口读数，以及我这两个小时里连犯两次的同一个操作错误

- **终验门禁**：`tests/output/round3-fallback-fix-gate3.log` ⇒ `GATE_EXIT=0`、**1414 断言 / 0 失败 / 2 跳过**（901+216+14+7+12+43+221=1414，与汇总行逐字一致，`[2/2]` 45 s），基线刷新为 1414，设置指纹逐字不变；末屏"未覆盖"仍只剩钩子那一条。
- **两次白跑的门禁，原因同一**：都红在 `[1.9/2]` 宿主新鲜度，因为我**为了省时间做局部构建**——第一次只建 Tools（Test 宿主滞后），第二次只建 Test（Tools 宿主滞后）。这条判据是"任一源码比任一宿主目录新 ⇒ 整轮作废"，它宁可假红也不放过旧二进制，处方就是它自己写的那句 `--no-incremental`。同一类错本轮犯两次（上一轮 §附 16 已记过三次），说明"改完 `src/` 必全解重编"对我得是默认动作而不是事后提醒。
  ⚠ **这句判词本轮被自己推翻一半**（见 §14.5）：整解构建 + `0 个错误` 之后照样红，因为 `[1.9/2]` 拿全局最新源码比每个宿主目录——只改 App 时 Test/Tools【正确地】不重编，这条口径本来就不可满足。局部构建确实是我的错，但那两轮不是。已把 `[1/2]` 改成 `--no-incremental`（+13.2 s）并两腿实测。
- **`-WriteGateBaseline` 的门**：上一轮（`round3-fallback-fix-gate.log`，`--unit-tests` 红 1 条）照样写了基线，于是地板被记成 **899**（比真实规模低）——一个"断言变少要红"的机制，自己的地板可以被一轮失败的读数写坏。已加 `if ($WriteGateBaseline -and $failedSuites.Count -eq 0)`，失败轮拒绝写并红一行。
  ⚠ **诚实记账**：这个"拒绝写"分支**本轮没做实测**——要走它必须有一轮套件真失败。可用的造法是设 `TTC_LIVE_TRANSLATE=1` 让必应活体用例去撞（那是一次出网请求，按规矩要先点头），或赌 `-IncludeDesktop` 的 WGC 套件状态（不可靠）。所以这条现在是"读码正确、未判"，别当已验。
- **锚点式文本锁自己错位红过一次**：我为 §13.9 加调用点之后，那条"就近钉"的窗口被 WebP 保底里的 `RecordFallbackPath` 提前截断 ⇒ 假红（`--unit-tests 899/1`）。改成钉在 `PngLastResort` 函数本体上，并补一条"保底必须有 ≥2 个调用点"。这次错位本身就是 §11.2 那句"文本哨兵证不了运行时"的日常代价：它不但证不了行为，还会因为**别处**的改动而错位——错位时它红，但红的原因与被钉的对象无关。

### 13.11 本轮到此的总量（每个数都可核）

165 条候选（九条 lane 各自表格相加）→ 我逐行回源确证 12 条 → **产品码改 12 处**（Core 6 / App 4 / LogViewer 1 / Tools 1），其中 **1 条是写行为判据时新撞出来的 P1**（§13.9 AVIF 保底覆盖面），**1 条是修上一条时被新断言抓出的第二层缺陷**（§12.2-5 "读后即消费"从未删掉文件）；判据与门禁侧改 6 个文件、新增 1 把跨进程行格式对撞尺子、断言地板 1414 入库且验过它会咬人（M14：把地板改成 99999 ⇒ 两条红并点名）。13 条变异实测里 11 条当场咬住、2 条先漏后补（都是我自己的锁）、1 条用门禁实况作证、1 条补丁不解决问题后撤掉。工作树 20 个跟踪文件**全部未提交**，快照 `tests/manual/safety/wip-2026-09-29-d2.patch`（151 KB）。

## 14. 批次 E：B 域热格子的真身读数（2026-09-29）

E 批要还的债是 §13.4 那句"20 热格子仍待跑"——批次 C 的 B 域台账（L5 那张 26 行表）全是**读码**得出的，
每行只写了"归一前读到什么 / 界面显示什么 / 编码实际用什么 / 行号"里的前一半。本轮把它拆成两种读数分别取证：
**盘上后果**用产品真身跑（`--display-audit`，§14.1），**归一本身**在 App 进程内直调生产分支跑（§14.3）。
"界面显示什么"那一列仍欠着，原因写在 §14.5，不是忘了。

### 14.1 四个格子跑在真身上（harness `tests/manual/safety/audit-case.ps1`，日志 `tests/output/audit-*.log`）

每格：把夹具写进 bin 目录那份 settings.json → `Start-Process -Wait` 起 `TrueToneCap.exe --display-audit`（120 s 上限）
→ 截卡片的「管线落点」段 → 在**还原之前**拍下盘上内容与 sha1 → 按备份还原并核指纹。四格读数：

| 格子 | 写进盘上的设置 | 进程有没有改设置 | 卡片读数 | 这一格判了什么 |
|---|---|---|---|---|
| control | 不动（用现有那份） | 无（sha1 前后 `C76ABAC1…` 相同） | 四行差分全是 `质量 1→1 / 增益档 Gray→Gray`，卡片自己补一句"⚠ …这几根轴上完全相同 ⇒ 差分读数不判别" | 对照臂：审计跑起来**不往盘上写**，且"不判别"这句话是产品自己说的，不是我推断的 |
| userchoice | `GainMapMode":"Rgb"`、`QualityJpegLi:0.5`、`BitDepthJpegXl:16`、`ChromaJpegXl:422` | 无（`0D870D3F…` 前后一致） | 差分变判别：`质量 1→0.5`、`位深 10→16`、`增益档 Gray→Rgb`；**`色度 444→444`** | §8 批次 B 欠的那笔：用户所选确实抵达编码设置（真调 `BuildEncodingSettings`） |
| oobnomig | `FormatIndex:99`、`QualityAvif:999`、`ColorSpaceIndex:6`、`ArchiveMode:"Week"`，修订号=当前 | 无（`9E23F1BC…` 前后一致，轮后盘上内容与写进去的**逐字相同**） | 卡片对这四格**完全沉默**（它的探针只有 JPEG_GAINMAP / JPEG_XL 两根轴） | 迁移门不触发时，越界值不会被"顺手钉回盘上"；同时暴露卡片覆盖面缺口（§14.5） |
| v6proof | `{"SettingsRevision":1,"GainMapMerged":true,"FormatIndex":99,"QualityAvif":999,"GainMapMode":"Rgb","QualityJpegLi":0.5}`——与 oobnomig 只差修订号（1 而不是当前的 2），另少 ColorSpaceIndex/ArchiveMode、多两格取值；改修订号只为逼迁移门触发 | **有**（`9F448B73… → 76E086FF…`） | 轮后盘上只剩 `{"SettingsRevision":2,"GainMapMerged":true,"GainMapMode":"Rgb"}` | §12 那条"Normalize 之后再 Save"的次序修复在真进程里成立：钉回盘的是**归一后的值**（99/999 都没留下），不是原始越界值 |

- v6proof 那一格顺带把 L5-#4 记成实数：交还门（`MigrateStaleDefaults`，门是修订号）在同一次启动里
  把 `QualityJpegLi=0.5` **清掉了**（键消失 = 与默认 1.0 相同 ⇒ 差集序列化不写它）。这是产品口径
  （"旧文件里的值被钉死、代码默认改不动，本次交还默认表"），日志里有一条 Info，界面上没有任何东西；
  用户视角是"我没碰设置，我挑的质量档没了"。**不是缺陷，但它发生在用户没操作的一次启动上**， worth 一句拍板。
- `SECRETS=0`：本机 `%LOCALAPPDATA%\TrueToneCap` 下没有独立凭据件 ⇒ V7 里"坏文件不许删凭据"那一条
  在本机**不可观测**，判不了（不是通过）。

### 14.2 把一条老尺子从 2 个下拉扩到 7 个 ⇒ 当场抓出两处白名单与界面分叉

`Test_SettingsWhitelist_MatchesXamlTags` 本来只钉 `OcrEngineMode` / `AvifEncoder` 两项（这两项都各踩过一次事故）。
本轮把同一把尺子接到剩下 5 个下拉上，**第一件事就是红两条**（`tests/output/wl-extend-before.log`，`--unit-tests 914/2`）：

```
❌ 设置白名单[Gray,RGB] == GainMapModeCbo Tag[Rgb,Gray]
❌ 设置白名单[Day,Week,Month,Year] == ArchiveModeCbo Tag[Year,Month,Day]
```

- **`GainMapMode`：白名单写 `"RGB"`，XAML 挂的是 `Tag="Rgb"`。** 机制是两件工具的口径不一致：
  校验用 `IsOneOf(..., StringComparison.OrdinalIgnoreCase)`，回填下拉用 `SetComboByTag` 里的
  `(string)item.Tag == tag`（区分大小写，`MainWindow.Settings.cs:205`）。于是一个大小写变体
  （`"rgb"`、`"RGB"`）会**穿过校验**、编码路径按 Rgb 走，而界面那侧匹配不到任何项 ⇒
  下拉空白 + 一条 Warn，且用户下一次保存**任何**设置时该字段被
  `(SelectedItem as ComboBoxItem)?.Tag as string ?? "Gray"`（`MainWindow.Settings.cs:269`）静默翻成灰度增益。
  可达性量过：`git log -S 'Tag="RGB"'` 对 MainWindow.xaml **零命中** ⇒ 界面从来没有产出过这串，
  所以只有手改 settings.json（或未来的第二个写者）能进——但 `IgnoreCase` 让"随手写小写"也算数，这一条并不苛刻。
- **`ArchiveMode`：白名单放行 `"Week"`，界面只有 Year/Month/Day，且 `GetArchivePath`
  （`CapturePipelineService.cs:521-529`）没有按周的分支**——`_ => yyyy-MM` 与 Month 同一条路。
  于是 `"Week"` 是一个"选不到、也没有行为"的值：手改的人看到空白下拉，产物照旧按月归档。
  `git log -S 'Tag="Week"'` 同样零命中 ⇒ 它不是"界面还没接上"，是白名单里的空转残留。
  本轮 oobnomig 那格恰好写进了 `"Week"`，于是这条从读码怀疑变成真进程读到的盘上事实。

**修法（`SettingsService.Normalize`）**：新增 `FitTag(current, set, def, tags)`，把七条字符串轴统一改成
"只放行界面真实存在的 Tag，并把大小写变体**改写成 Tag 的拼法**，匹配不到才回落默认"。
两个方向都要钉死：原样留着 = 今天修掉的缺陷；一律回落默认 = 另一种错法（把"用户想要 RGB 增益"读成"没设过"）。
顺带把默认值从字面量换成 `AppSettingsData.CreateDefaults()` 取值。`SettingsService.cs` 里那句
"不得再写字面量"的注释早就写过这条纪律（本轮实测在 :382；写 §14 时它在 :346 —— 行号随编辑漂移，
引用请以锚文本为准），但字符串轴一直违着它（质量轴 2026-09-17 那次漂移的同族），`IsOneOf` 由此零调用方，删掉。
`"Week"` 从白名单摘掉；`ColorSpaceIndex is < 0 or > 5` 那个 5 **不动**（它是旧 6 项表的最后一项，见 §14.4）。

四段读数（新锁必须咬，出处都落盘）：

| 臂 | 做什么 | 读数 |
|---|---|---|
| 修前 | 只把尺子从 2 个下拉扩到 7 个，产品码未动 | `--unit-tests 914/2`，两条点名见上（`wl-extend-before.log`） |
| 修后 | FitTag + 摘掉 Week + 默认值取 CreateDefaults() | `--unit-tests 926/0`（`csindex-lock.log`）；门禁 `GATE_EXIT=0` |
| 变异①：tags 改回 `"Gray","RGB"` | 恢复旧白名单 | T2 红 `设置白名单[Gray,RGB] == GainMapModeCbo Tag[Rgb,Gray]`（`wl-mutation-A.log`）；T1 `NORMALIZE fail 57 轴读毕，3 条不符`，其中一条是 **`GainMapMode Rgb→RGB`** ⇒ 改回旧写法连**拼法本来正确**的用户值都会被改写坏（`settings-migration-mA2.log`，`EXIT=1`） |
| 变异②：FitTag 改成"只放行不改写"（= 旧 `IsOneOf` 语义） | 证明大小写那两臂有牙 | `NORMALIZE fail 57 轴读毕，10 条不符`，10 条**全部**是大小写臂（`小写·ThemeMode dark→dark`…`大写·ToastPosition TOPRIGHT→TOPRIGHT`），越界臂与合法臂零误报 ⇒ 这一臂只抓它该抓的那一类（`settings-migration-mA.log`，`EXIT=1`） |

### 14.3 新增一条 App 侧 T1：`NORMALIZE`（58 臂直调生产 `Normalize`）

盘上后果（§14.1）只回答"会不会被钉回去"，回答不了"归一之后到底是几"。那一半在 App 进程里，
而 `Test` 工程只引用 Core ⇒ 够不到 `AppSettingsData`；把夹具写进 bin 目录那份 settings.json 借 `Load()`
触发，等于为了测试改用户机器上的设置（本仓"设置只认同目录一个答案"是拍过板的）。
所以照 `MigrateStaleDefaults` 的先例把 `Normalize` 改成 `internal`，在 `SelfTest` 里加一条
**直接调用那条分支**的自检（`RunNormalizeGateCheck`）：六枚夹具、58 条臂，逐轴打"输入 → 实得（期望）"。
（枚数我当时写成了"五枚"，§17 复核时数了夹具变量：`oob`/`keep`/`cased`/`upper`/`legacy`/`csIdx` = 6 枚；
臂数 58 与日志 `58 轴读毕` 相符。审查轮又加了第 7 枚 `legacyLower`，那一轮的读数见 §17。）

读数里最容易读反、也因此最值得钉的几条（57 臂那份来自 `tests/output/settings-migration-after.log`；
`ColorSpaceIndex … 5→5` 那一臂是随后加的，终读数在 `settings-migration-final.log`，58 臂）：

| 轴族 | 实得 | 这一格的意思 |
|---|---|---|
| `QualityAvif 999` | → **15.00** | 质量越界是**交还默认**，不是钳到区间端点（端点是 63）。L5 表若写成"钳到 63"方向就是反的 |
| `QualityWebp 3` → 90 / `50` → 50 | 见左 | 下界 50 属于"用户所选"——合法臂作证"回落不是无条件的" |
| `BitDepthAvif 16` | → **12** | 位深越界是**就近收敛**，不回落 8 —— 与质量那族方向相反，两条并存于同一个函数 |
| `BitDepthJpegXl 12`→10 / `16`→16 | 见左 | JXL 只认 8/10/16；§12.2 加的 16 档没被吃掉 |
| `BitDepthTiff 10` | → **16** | 方向又一次相反：>8 一律升 16（保精度优先于省体积） |
| `BitDepthJpegLi 10` / `BitDepthWebP 10` / `BitDepthGainMap 12` | → 8 / 8 / 8 | 这三家编码器根本没有位深这一维，恒 8——只有越界臂能量出来，合法臂钉不住它 |
| `ChromaJpegXl 422` | → **444** | JXL 用自己的值域表；同一串在 AVIF 那边 `ChromaAvif 422→422` 原样活着（界面确有 4:2:2）。**同名不同值域**，最容易"顺手合并成一条"的地方 |
| `ColorSpaceIndex 6` / `3` / `5` | → 0 / 3 / 5 | 只有纯越界才回落；旧 6 项表里的 3/4/5 必须活着交给界面按标签收口（本机老文件实测就是 5） |
| `ArchiveMode Week` | → Month | §14.2 摘掉空转值之后的实况 |
| `AvifEncoder Avifenc` | → LibAom（不是 Auto） | 09-24 那条迁移的语义：落 Auto 会优先走硬件，等于翻转用户"选了软件编码器" |

三臂分工不是凑格子：`越界` 臂钉回落目标；`合法非默认` 臂钉"回落不是无条件的"（有人把这段写成"一律回默认"
只有这一臂会红）；`大小写` 臂钉"改写成 Tag 拼法而不是回落默认"。期望值一律写字面量，不从被测常数推导——
唯一例外是 `FormatIndex` 的回落落点，那张顺序表本身就是契约（Core 的 `FormatOrder` 另有逐格对撞）。

**这条判据跑在哪**：`--settings-migration`（手跑，harness `tests/manual/safety/run-settings-migration.ps1`，
退出码即判据，轮前轮后盘上设置 sha1 逐字不变 = 它不改用户机器）与 `--selftest`
（`.github/workflows/release.yml:63-76`，退出码非 0 直接 throw）⇒ **已经在发布链里**，不需要动本地门禁。
`verify.ps1` 到今天仍不起产品进程，要接进去得先点头（§14.5）。

**变异③**：`FixBitDepthAvif` 的 `>= 12 => 12` 改成 `=> 8` ⇒ `EXIT=1`，点名
`越界·BitDepthAvif 16→8（期望 12）❌`，其余 56 臂一条不误报（当时 57 臂；`settings-migration-mB.log`）。
加"旧表 5 必须活着"那一臂之后的变异④读数见 §14.4（58 臂）。

我自己在这条上犯的两次 harness 错，都属于"判据消失了但看着像有"：

1. `Start-Process -PassThru` 带 `-RedirectStandardOutput` 时 `$p.ExitCode` 读出来是**空**——
   第一轮 `settings-migration-e1.log` 里 `EXIT=` 是空的，而 stdout 那 57 臂全 ok，页面好看。
   判据是退出码，日志只是证据，所以那一轮不算数。改成 `-Wait`，并加一条
   "读不到退出码就拒绝给结论、不许拿 stdout 里的 ok 代替"。
2. 接线哨兵（防"自检函数写了没人调"）第一版只认 `RunNormalizeGateCheck(outDir).Status == "fail"` 一种写法，
   而 `--selftest` 那侧是 `var ng = RunNormalizeGateCheck(outDir);` 先接住再判 ⇒ 它把"我没匹配到"
   读成"没接线"，红了一条（`wl-extend-after.log`，`RESULT … passed=920 failed=1`）。方向对、原因错；改成数**调用点**
   —— 修好尺子那一轮是 `wl-extend-after2.log` 的 `921/0`，两个数我都抄错过，以日志落盘那行为准。
   这是 §11.2"文本哨兵证不了运行时"的日常代价，只是这次被钉的对象是哨兵自己。

### 14.4 色域那一轴：白名单尺子对它无效，改钉两张表的长度等式

`ColorSpaceIndex` 存的是**裸索引**，字符串对撞管不到它。本轮补两条等式：

- `Normalize` 里写死的上界 `5` 必须等于 `MainWindow.Settings.cs:98` 那张 `legacyTags` 的表长 − 1。
  今天两者一致（6 项 → 5），于是 L5 那行"上界 5 是旧 6 项表长、未随表取"的悬案有了答案：
  参照表是 `legacyTags` 而不是 `FormatOrder`，`> 5` 与它一字不差 ⇒ **不是缺陷，但当时没人钉这条等式**，
  现在钉了。差一格的后果与 2026-09-26 摘掉 GainMap 那一项同型（合法区间放过 / 吞掉一个旧索引）。
- `currentTags` 必须**逐项有序**等于 XAML `ColorCbo` 的 Tag。落空时 `Array.IndexOf` 返回 −1，
  代码走 `newIdx < 0 → 0` ⇒ 用户想要 Rec.2020 却拿到"跟随显示器"。
- 顺带把 `ExtractComboItemTags` 的锚点从裸名字改成 `x:Name="…"`：`ColorCbo` 是
  `OverlayColorCbo` / `BorderColorCbo` 的子串，"取第一次命中"今天侥幸落在正确那块，一旦谁把两张卡片
  挪到前面就会静默换块 ⇒ 解析到别人的 Tag 还自认为对了。8 个调用点同时受益。

**变异④**：把上界改成 `> 4` ⇒ T2 点名 `色域索引上界 4 == legacyTags 表长-1 5`（`csindex-mutation-C.log`），
T1 同时点名 `ColorSpaceIndex(旧表 5=BT2020) 5→0（期望 5）❌`（`settings-migration-mC.log`，`EXIT=1`）
⇒ 同一处改动能被"文本等式"与"真跑一遍"分别抓住，两把尺子不是冗余。

### 14.5 一条门禁尺子的口径错，以及我为了修它又亲手造了三个新错

§13.8 / §13.10 我写过"两次白跑都红在 `[1.9/2]` 宿主新鲜度，因为我为了省时间做局部构建"。
**那句话只对一半**，本轮量清了：

- 复现：`dotnet build TrueToneCap.slnx -c Release` 整解构建、打印"0 个警告 0 个错误"，紧接着起门禁，
  照样 `GATE_EXIT=1`，`滞后的宿主: …Test.exe（输出目录最晚写入 18:03:25）` vs `最新的源码: …SettingsService.cs (18:05:21)`
  （`round4-e-batch-gate2.log` / `gate4.log` 两轮都是这个形状）。
- 根因：`[1.9/2]` 拿的是【所有项目共享的一把尺】——"全局最新输入"对"每个宿主目录的时间戳"。
  只改 App 的源码时，MSBuild **正确地**跳过重编 Test/Tools（`Test` 与 `Tools` 的 csproj 各只引用 `Core`，
  实测三条 `ProjectReference` 就是全部边），于是那两个目录的时间戳永远追不上这次编辑 ⇒ **必然假红**。
  今天它已经抓了我五次，其中至少这两次不是我构建偷懒，是尺子不可满足。
- 我的第一版修法（按宿主算输入闭包：ProjectReference 传递闭包）**撤回**了。腿 B 实测：
  只碰 `Core\Encoding\FormatEncoders.cs`，两腿都判绿——`Test`/`Tools` 的输入数分别是 13 / 25，
  恰好等于各自项目自己的 `.cs`+csproj 数，**Core 那 47 份一份都没进去**（`freshness-two-leg2.log`）。
  两个坑叠在一起：① PowerShell 函数 `return $set` 时 HashSet 会被解流；② 我复用宿主的 csproj 叶子名去
  拼被引用目录的路径（`Core\TrueToneCap.Test.csproj` 当然不存在）。
  ⇒ 这比假红危险得多：它会在"真宿主旧"的时候安静放行。**尺子宁可假红，不可假绿**，所以撤回。
- ⚠ **而且这一格项目记忆里早就写着**（`project-verification-gates` 那条 ③：
  "freshness 判据 `[1.9/2]` 会因'只改了 Test/App'假红 ⇒ 按它给的修法 `--no-incremental` 全量重编即可"）。
  我起门禁之前没去查它，于是把尺子的错连记了两轮成自己的错（§13.8/§13.10），还顺手重新发明了一遍
  更差的解法。**记忆条目在动手前读一遍，比写完再读便宜**——已把那条更新成"结构性修好了，再见到这个形状
  先怀疑 flag 被改回增量"。
- 实际修法：让 `[1/2]` 用 `--no-incremental`，把"这条口径可满足"变成构建自己的事
  （当场 `Measure-Command` 量到整解重编 **13.2 秒**；⚠ 这是一次性读数，**没有落盘日志**，
  后轮若要引用这个数字请重测一遍再写）。两腿实测（`freshness-noincremental2.log`）：
  腿 A 碰 App 源码后起【完整门禁】⇒ `✅ 宿主新鲜度: 2 个宿主都不早于 142 份构建输入`、`退出码 0`；
  腿 B 碰 Core 源码后带 `-SkipBuild` 起门禁（产物不许动）⇒ 两个宿主都被**点名**滞后（各一行，:10/:11）。
  ⚠ 那一跑的**退出码仍是 0**：`-SkipBuild` 按 `verify.ps1` 自己的参数约定把判红降级成警告（:12）。
  ⇒ 所以腿 B 证的是「能点名」而不是「会拦下」；「会拦下」的证据是本轮另一次**不带** `-SkipBuild`
  的真滞后（§13.8 那两轮就是这么废掉的，`GATE_EXIT=1`）。

我在这半轮里自己造的三个错，逐个记：

1. **`Start-Process -PassThru` + `-RedirectStandardOutput` ⇒ `$p.ExitCode` 是空的**。
   第一轮 `settings-migration-e1.log` 的 `EXIT=` 空，而 stdout 里 57 臂全 ok——看着像通过，其实判据不存在。
   改成 `-Wait`，并加一条"读不到退出码就拒绝给结论，不许拿 stdout 的 ok 代替"。
2. **接线哨兵第一版只认一种写法**：`RunNormalizeGateCheck(outDir).Status == "fail"`，而 `--selftest`
   那侧是 `var ng = RunNormalizeGateCheck(outDir);` ⇒ 把"我没匹配到"读成"没接线"，红一条
   （`wl-extend-after.log` 的 `passed=920 failed=1`）。方向对、原因错；改成数**调用点**。
3. **harness 里留了第二份同名函数**：`freshness-noincremental.ps1` 先后有两个 `function Touch`，
   PowerShell 后定义覆盖前定义 ⇒ 前两腿跑的全是"源码时间戳 = 现在 + 2 分钟"这种任何真实构建都追不上的
   未来时间，`LEG_A=FAIL(仍假红)` 是**夹具造假**，不是产品/门禁的问题。删掉重复定义后两腿才给出上面的定案。
   （"同一个参数有两个写者时，只测自己那一半不算验过"——这条记忆点在案，本轮又亲手犯了一次。）

### 14.6 E 批仍欠的列（写清楚，不假装做完）

| 欠的是什么 | 为什么本轮没做到 |
|---|---|
| "界面显示什么"那一整列 | 要带窗口的 UIA 会话。本轮所有关于"下拉空白 / 保存时被兜底翻转"的陈述都是**代码路径**读出来的（`MainWindow.Settings.cs:202-212` 的 `==` 比较与 `:269` 的 `?? "Gray"`），不是界面实测 |
| AVIF / WebP / PNG 三格的**管线落点** | `SelfTest.cs:301-302` 明写"刻意不取 PNG / AVIF 两格：它们各自的位深协商与编码器回落在 ToastService 上有副作用"。回代码数了那五处 Toast 出口（`CapturePipelineService.cs:332/387/405/430/455`）确认这句话是真的，且 `ToastService.ShowToast` 在没有 UI 队列时**直接走系统通知**（`ToastService.cs:181-186`）⇒ 加一格 = 可能往用户通知中心写一条。要给这两格开判据，得先有一条"审计态禁弹"的通道，那是产品码改动 + 一次带窗口验证 |
| `QualityAvif=999` 在卡片上是**沉默**的 | 卡片探针只有 `JPEG_GAINMAP` / `JPEG_XL` 两根轴。本轮由 §14.3 的 `NORMALIZE` 臂接住"进编码设置之前那个值是多少"，**没有**接住"卡片会不会把 63 写成 15" |
| V7 的"坏文件不许删凭据" | 本机 `SECRETS=0`（`%LOCALAPPDATA%\TrueToneCap` 下没有独立凭据件）⇒ 不可观测，判不了 |
| `-WriteGateBaseline` 的拒绝分支 | 仍要靠一轮真失败或一次出网授权才能走，读码正确、未判 |
| 本地门禁不起产品进程 | `verify.ps1` 里 `TrueToneCap.exe` 零命中（实测）。App 侧 T1 目前只有 `--settings-migration`（手跑）与 `release.yml` 的 `--selftest` 两条路；要接进 `verify.ps1` 需要点头，因为它会让每次门禁都起一次产品进程 |

### 14.7 E 批收口读数

- **终验门禁**：`tests/output/round4-e-final-gate.log` ⇒ `GATE_EXIT=0`、**1439 断言 / 0 失败 / 2 跳过**
  （926+216+14+7+12+43+221=1439，与末屏"共 1439 断言 (36.2s)"逐字一致），地板刷新为 1439，
  轮前轮后设置指纹逐字不变；末屏"本轮判据未覆盖"仍只有钩子那一条（`合计 1 处`）。
  **同一条读数又独立复跑一次**：`round4-e-final-gate2.log` ⇒ 同样 `1439 断言 (39.5s)`、`GATE_EXIT=0`、
  设置指纹逐字不变 ⇒ 这一格不是"碰巧绿一次"。
- `[1.9/2]` 那行改成新口径的实况：`✅ 宿主新鲜度: 2 个宿主都不早于 142 份构建输入`。
  这 142 份 = `src` 下的 `.cs/.xaml/.hlsl/.csproj/.props/.manifest`（各项目 .cs 合计 117 份：Core 47 / App 35 /
  Tools 22 / Test 10 / LogViewer 3）+ 4 枚随包原生编码器 + `tests\fixtures` 的 2 份样张。
  **我没有逐项核对这份加法**（门禁只报总数），但它和"输入面没被改小"这件事一致：
  撤回闭包版之后，判据仍是拿全部 142 份比每个宿主。
- **App 侧新 T1**：`tests/output/settings-migration-final.log` ⇒ `EXIT=0`、`NORMALIZE ok 58 轴读毕，全部符合期望`，
  盘上设置 sha1 逐字不变（这条自检不改用户机器上的任何东西）。
  ⚠ 别拿 `settings-migration-after.log` 当终读数——那份是 57 臂，加"旧表 5 必须活着"那一臂之前的。
- 断言净增 **+25**（1414 → 1439）：7 个下拉的白名单对撞（+15 条：5 轴 × 3）、色域两张表的长度等式（+5）、
  自检接线哨兵（+5）。E 批产品码只改 1 处（`SettingsService.Normalize` 的 7 条字符串轴 + `FitTag`，
  删掉 `IsOneOf`），判据侧改 3 个文件（`SelfTest.cs` 新自检、`CorePipelineTests.cs` 三组锁、
  `verify.ps1` 的 `[1/2]`/`[1.9/2]`）。
- 本轮 E 批的变异实测 4 条（§14.2 两条、§14.3 一条、§14.4 一条），**4/4 当场咬住并点名**；
  撤回的那版"按宿主算输入闭包"用腿 B 证明了它会安静漏判，属于"变异没被抓到"的反面教材——
  只不过这次被抓的是我自己新写的尺子，且是在它进门禁之前。

## 15. 批次 F：把"换格式保底"与"不许静默成功"逼出读数（2026-09-29）

§13.11 记的债是"三条保底里只补了 AVIF 一条"。本轮把保底这一族整张铺开，逐格实测。

### 15.1 换格式 / 降级这条链的现状（读代码定位，实测取证）

| 路 | promise（代码里怎么写的） | 实测行为 | 判据 |
|---|---|---|---|
| AVIF 硬件后端失败 | `FallbackToLibAom` → "确保截图不丢失" | 退 libaom，再失败退 PNG，带声明 + 留痕 + 登记路径 | `AvifPngFallback_…`（§13.9，行为级） |
| AVIF 选中的本来就是软件后端 | 同一句 promise | §13.9 之前**根本不进保底**；现已接同一个实现 | 同上 |
| WebP：cwebp 缺 | "libwebp 不可用，回退 PNG" | 退 PNG，带声明 + 留痕 + 登记路径 | 本轮前只有读码，本轮实测（对照臂 + 注入臂） |
| **WebP：cwebp 在位但这次编码失败** | 同一句 promise 覆盖的是"这次拿不到 WebP" | **promise 不兑现**：旧写法只 `catch (DllNotFoundException)`，而编码失败抛 `InvalidOperationException`（`NativeWebPEncoder.cs:88-92` 的非零退出 / `NativeEncoderGuard` 的超时崩溃隔离）⇒ 图丢、`LastReason` 仍是 null、上层拿原路径报"已保存" | 本轮新增 `WebPFallback_WhenBackendFails_…`（6 条，行为级） |
| JPEG LI / JPEG XL / 增益图的 SDR 支 | **没有**换格式保底；`JpegGainMapEncoder.cs` 原 :147 明写"无回退，必须可用"（§16 已改掉这句，旧口径现在只存在于 :63/:174 的注释里） | 抛异常（失败可见，但**捕获点在窗口层不在管线**：`MainWindow.xaml.cs` 里以 `ex.Message` 走 `ShowCaptureFailed` 的共 8 处，如 :1296/:1496/:1699/:1854/:1925/:2131/:2147/:2424；另有 6 处走写死的文案），**图仍丢** | 本轮新增 `JpegLiAndJxl_BackendFailure_NeverClaimsSuccess`（9 条）只钉"不许静默成功"这一条下限 |

⇒ 最后那一行是**口径问题不是缺陷**：给 LI/JXL 加 PNG 保底 = 换掉用户选的格式，那要拍板；
本轮只把"要么抛、要么真有产物"钉成判据（§15.3），把"要不要退 PNG"留成一句话（§15.6）。

### 15.2 WebP：把实现提到它自己声明的水位

**复现（修前）**：`tests/output/webp-fallback-before.log` ⇒ `--color-tests 217/4`，四条点名：

```
✅ WebP 注入成立：独占让这次 cwebp 确实失败了
❌ WebP 保底：cwebp 编码失败时也要落一枚非空 PNG … 抛的是 InvalidOperationException: [WebP] cwebp 编码失败: … Cannot open output file
❌ WebP 保底的 PNG 必须带 iCCP 或 cICP …   ❌ WebP 保底必须写降级留痕 … 实得 <null>   ❌ WebP 保底要登记「实际写入路径」…
```

**改法（不抄第二份保底）**：把 `PngLastResort` 从 `AvifFallbackHelper` 挪进 `FormatHelper`，
参数化 `who`（日志类目）/ `why`（日志原因）/ `notice`（用户可见文案）——
**机制只有一份，各格式的"事实"由调用方给**。WebP 因此删掉了自己内联的那一整套
（取声明 → `ManagedPngEncoder.Encode` → 写留痕 → 登记路径），改成同一个调用。
`catch` 从 `DllNotFoundException` 放宽成 `when (ex is not (OperationCanceledException or ArgumentException))`：
取消是用户主动的、`ArgumentException` 是"这个文件名本身非法"（换后缀照样写不出去，
兜下去只会把诊断从"路径非法"糊成"保底也失败"），其余都接。

**三段读数**：修前红 = 上面 4 条点名；修后绿 = `--color-tests 222/0`（`webp-fallback-after2.log`）；
变异红 = 把 `catch` 改回只接 `DllNotFoundException` ⇒ **恰好同样 4 条红**、其余 218 条不动
（`webp-mutation-W.log`）⇒ 这一族的新锁抓的正是它该抓的那一类。

### 15.3 新加的下限判据："不许静默成功"，以及它咬住的第一例

注入同一个 `FileShare.None`，对 JPEG LI / JPEG XL / 增益图 SDR 支各断三件：
要么抛、要么真有产物；抛的话异常必须带类型与信息（能归属到是哪个后端）；并且**独占确实挡住了写入**
（那个目标文件仍是 0 字节）。绿读数 `--color-tests 231/0`（`loud-lock-after.log`，9 条全 ✅）。

它的牙是当场验的：**变异 X** 把 JPEG LI 的 `File.WriteAllBytes` 包一层 `catch { }`（"吞掉写入失败"）
⇒ `--color-tests 225/1`，红的那一条正是
`❌ JPEG_LI：后端失败必须【要么抛异常、要么真落产物】… 既没抛、盘上又什么都没有 ⇒ 上层会拿这个路径报「已保存」并进剪贴板`
（`loud-lock-mutation-X.log`）。⇒ 这正是 §12 那轮修掉的形状的构造器，现在有一条执行级判据看着它。

结构侧同时钉住"保底只能有一份"：全 `FormatEncoders.cs` 里 `Path.ChangeExtension(path, ".png")`
**实测只许 1 处**（就在保底内部），调用点从 2 个变 3 个（AVIF 硬件链 / AVIF 软件后端 / WebP）。
这条是防"再长第二份"的——上一轮 §13.9 那个洞就是两份并行的保底里只补了一份造成的。

### 15.4 我在这半轮里自己的两处尺子错

1. **注入判据写成"抛没抛异常"** —— 修好保底之后异常被接住，`thrown` 变 null，那条前置判据**自己红了一格**
   （`webp-fallback-after.log` 的 `❌ WebP 注入成立…cwebp 竟然写成功了`）。红得没错（它确实在谎报注入），
   但"抛没抛"从来不是注入的证据。改成两条独立证据：**对照臂**（不加独占必须真落出 `RIFF….WEBP` 码流）
   + **注入证据**（那个 `.webp` 仍是 0 字节）。前者不成立就单独红，后者与"保底生效"不是同一件事 ⇒ 不会恒真。
2. **在源码里往字符串里塞 ASCII 引号** —— `走不到"编码失败"那一支` 直接把 `ColorPipelineTests.cs` 弄成
   CS1003 语法错（4 处）。同一条坑记忆点在案（消息串里的引号一律用「」），这轮又犯；
   而且我第一次想用 `python` 脚本批量修，**`python` 在本机被拦成 exit 49**、脚本根本没跑，
   我却以为改动生效了（下一次 `grep` 才看见文件原样没动）⇒ 用 `py` 或直接 Edit，且**批量改写后必须回读**。

### 15.5 F 批仍欠的（不假装做完）

- **要拍板的一句**：JPEG LI / JPEG XL / 增益图要不要也接这条 PNG 保底？接 = 用户选的格式被换掉
  （与 AVIF/WebP 现在的行为一致），不接 = 后端不可用时图丢但提示清楚。当时代码里增益图那处原写
  "无回退，必须可用" ⇒ 这是**产品口径**，我不单方面改。（那一拍板已经在 §16 落到实现里，那句注释也随之改掉，
  现在这句在 `JpegGainMapEncoder.cs` 里只作为历史口径出现在 :63/:174 的注释里，不再是代码里的承诺。）
- 还没逼出的构造：`TTC_FORCE_SDR`、只读目录、断网制造 WCS 失败、native 目录被占用（§13.7 已否掉"改名造故障"这条路）。
  （**后续**：占用后端与"设置文件只读的整进程"两格已在 §18 结清，并顺手改掉那里一句写死成因的假日志；
  "只读目录"这一格当时就列粗了——探针的 ACL 拒写臂早有 T1（`Test_SettingsWriteProbe_RealProbe`，见 §18.2）；
  剩下两格要带窗口会话或要动本机网络状态，见 §18.3。）
- MFT / QSV 那一族按用户口径** parked**（"QSV 不可达是正常情况"），本轮没动它的行为。

### 15.6 本轮 F 批的收口读数

- 门禁：`tests/output/round5-f-batch-gate.log` ⇒ `GATE_EXIT=0`、**1455 断言 / 0 失败 / 2 跳过**
  （927+231+14+7+12+43+221=1455，与末屏"共 1455 断言 (41.5s)"逐字一致），地板刷新 1455，
  设置指纹逐字不变，末屏"未覆盖"仍只有钩子那一条。
- 产品码改动 **1 处**（`WebPEncoder.EncodeSdrAsync` + 保底实现收拢进 `FormatHelper.PngLastResort`），
  判据侧新增 **15 条**行为级断言（WebP 6 + 三格式下限 9），变异实测 **2 条**（W、X）都当场咬住并点名。
- 与上一轮同一件事的差别值得写一句：§13.9 那条 AVIF 保底是靠"写判据时撞出来"的，
  这轮的 WebP 那条是靠**把 promise 与 catch 逐字对照**找出来的 ⇒ 台账里"注释/文案说了什么"这一列本身就是缺陷源。

## 16. 用户拍板：接上保底 + 触发必须告知 + 设置里给开关（2026-09-29）

用户对着 §15.5 那句"要不要给 LI / JXL / 增益图也接 PNG 保底"给的口径是三件事：
**接上；触发的时候一定要告诉用户；并且在用户设置里做一个开关**。下面按这三件落地，
另外三条是我在这次改动里自己造出来的危险——都被判据当场抓住，不是事后补的说明。

### 16.1 落点

| 那一句话 | 落在哪里 |
|---|---|
| 接上保底 | JPEG LI / JPEG XL（SDR + HDR 两腿）/ 增益图（SDR + HDR 两腿）各接同一个 `FormatHelper.PngLastResort`；HDR 那两腿走新增的 `PngLastResortHdrFrameAsync`——**它把帧交给产品自己的 PNG 编码器**，不在保底里重算一遍声明/位深（否则 HDR 会被顺手压成 8-bit SDR，那是保底里夹带的第二次降级）。`JpegGainMapEncoder` 里那句"无回退，必须可用"的旧口径一并改掉，不留会说谎的注释 |
| 一定要告诉用户 | 同一条 `EncoderFallbackNotifier.LastReason` 单槽 → 管线 `NotifyEncoderFallbackIfAny` → 日志 + Toast；HDR 帧那条腿还会把 PNG 编码器自己的留痕（请求 8-bit ⇒ HDR 退回 SDR）**并进同一句**，不是覆盖掉。`ResolveActualPath` 早就接好了，报的路径是真落盘那一枚 |
| 设置里的开关 | 设置页「行为」卡片里 `编码失败时改存为 PNG`（灰字提示行讲清两种选择的代价），中英两份串；`AppSettingsData.PngFallbackEnabled = true`（默认开 = 与这次改动之前一致，也不需要迁移代码：`Save()` 只写与默认不同的键） |
| 开关的覆盖面 | 只管【换格式】。容器内的后端阶梯（AVIF 硬件→libaom、HDR→同格式 SDR、位深/色度协商）不归它管。**为此把 AVIF 硬件链里那次 PNG 保底上移**到带 `EncodingSettings` 的那一层：`FallbackToLibAom` 现在只把 libaom 的失败抛出去（后端层拿不到用户设置，那里换格式 = 开关管不住它） |

### 16.2 行为矩阵：5 格式 × 开关两态 = 10 格 / 45 条断言（`--color-tests 267/0`）

同一枚夹具（对目标文件持 `FileShare.None`）跑两态，**两态互为对照**，所以开关的牙不需要另做变异：

| 每一格都先判 | 开关开 | 开关关 |
|---|---|---|
| 注入成立：原路径仍是 0 字节（失败确实是独占造成的，不是编码器坏） | ✅ ×5 | ✅ ×5 |
| 结果 | 必须落一枚非空 PNG ＋ 必须写留痕（含"PNG"字样）＋ 必须登记实际路径 | 必须抛 ＋ **盘上一律不许出现那枚 PNG** ＋ 不许留"换过格式"的登记 ＋ 异常类型/信息可归属 |

`JPEG_LI / JPEG_XL / JPEG_GAINMAP / WebP / AVIF(软件后端) × 开/关` 十格全 ✅ —— 出处**只有**
`fallback-matrix-2.log`：每格命中 4～5 行（合计 45 条），`❌` 0 行，
`RESULT suite=--color-tests passed=267 failed=0`。
（先前这里还挂了 `loud-lock-after.log` 当第二处出处，那是被这十格**替换掉**的旧"下限锁"轮次，
`passed=231`、里面一条 `保底=开/关` 都没有 ⇒ 拿它当这十格的证据是错的，已撤。）
"关掉以后不许留 PNG"这一臂是关键：只测"抛了"不够——偷偷写一枚 PNG 再抛，
上层照样能报"已保存"。

### 16.3 开关到编码设置之间那一段：源码锁 + 真身读数 + 一次变异

Core 的矩阵判的是"编码器认不认这个布尔"，而"设置页那个勾 → 布尔"整段在 App 工程里（Test 只引用 Core，
运行时够不到），所以分两层钉：

- **源码锁**（`--unit-tests`，那一轮净增 **+14**：`round5-f-batch-gate.log` 的 927 → `round6-fallback-gate3.log`
  的 941；这 14 条里含开关接线本身与两条结构锁的改写，"接线锁几条"这种拆分我当时没量，不再凭印象写数）：
  设置项存在且默认开；只有管线那一个写者（用正则把
  `_settings.PngFallbackEnabled =` 与 `settings.PngFallbackEnabled =` 分开——第一版我拿子串判，
  被自己的 `_settings.` 命中，假红了一条根本不存在的写者）；Core 侧的 catch 都读这个闸（打印值是
  正则命中数，锁本体判 `>= 7`；我先前写的"9 处"里有一处其实是 `ImageEncoder.cs` 的**文档注释**被正则吃了进去，
  已把模式收紧成必须带 `when (` 前缀）；
  换后缀的实现只许两份；XAML 控件 / ApplySettingsToUI / SaveSettings / 事件分支四段齐；
  两串中文按**字面量**去 `ZhToEn` 表里查（改了文案忘了改表就红）；Tools 与审计卡**不许提**这个字段。
- **真身读数**（审计卡新增一行）：`tests/manual/safety/audit-fb-on` = `True ⇒ True（两处一致 ✓）`，
  `audit-fb3-off` = `False ⇒ False（两处一致 ✓）`；不一致就进 `landingFailures` ⇒ 卡片退出码非 0。
- **变异 Y**：把管线那句改成 `PngFallbackEnabled = true` ⇒ `audit-fb4-off` 的 `EXIT=1`，卡片点名
  `✗ 保底开关: 设置=False 抵达编码设置=True`，且那一行的文字变成 `False ⇒ True （★ 不一致：开关在管线上丢了）`
  ⇒ 这条真身判据有牙，不是打印给人看的装饰。

### 16.4 我自己造出来、被门禁抓住的三条危险（这三条是本轮最值钱的部分）

1. **保底会把"编码器坏了"洗成"产物存在"**。我最初把 `EncodingSettings.PngFallbackEnabled` 的初始值定成
   `true`（想着"和产品默认一致"），于是整轮门禁 `[1.7/2]` 立刻红：`param_sweep_check.py` 的
   `c_avifsvt_444` 从 `✅ 按预期失败（SVT 仅 4:2:0，未静默降级）` 翻成
   `竟然成功了 —— SVT 能力表需要重测`（`round6-fallback-gate.log`）。真因不是 SVT 的能力变了，
   是保底在扫参目录里多写了一枚 `c_avifsvt_444.png`，而裁判按**文件名前缀**找文件。
   ⇒ 初始值翻成 `false`：产品的"开"只由管线从设置快照带进来；任何直接 `new EncodingSettings` 的
   测量宿主（`--param-sweep` / `--selftest` / 外部裁判 / 单测夹具）**默认不许换格式**，失败就得是失败。
   再加一条源码锁钉住"Tools 与审计卡不许提这个字段"。**默认值该按"谁来构造"分，而不是按"产品对用户是什么态度"分**——
   这一条我一开始想反了。
2. **样张目录里的尸体替上一轮作答**。翻完默认值之后那一格**还是红**：`--param-sweep` 只覆盖同名文件、
   从不删旧的，上一轮那枚 `.png` 还躺在 `artifacts/precision/probe/sweep`，被门禁复制进被judged的目录。
   ⇒ 生成器现在先清空自己的输出目录、清不掉就 `ExitCode = 1` 直接拒产出（`FormatBench.cs`），
   并把这次经过写成注释留在原地。这条属于记忆里"共享产物目录里枚举第一个当夹具"的同族，
   只是这次更隐蔽：**判据读的仍是真的文件，只是那些文件不是本轮生成的**。
3. **一次坏 JSON 让审计卡说反话**。我给 `-SettingsJson` 传参时 shell 多吃一层转义，落盘的 settings.json
   是坏 JSON；产品的 `Load()` 走 catch 用默认值（日志里有 Warn），而卡片当时只按"文件在不在"出话，
   于是写着"喂给管线的是你磁盘上那份"——症状是"我明明设了 false，卡片说 True 且两处一致 ✓"，
   看起来像开关坏了。⇒ 卡片多一列 `实际读进来=是/否`（`SettingsService.LastLoadCameFromDisk`），
   坏文件时说"★ 文件在但没读进来"；harness 侧加 JSON 自检与"夹具文件不存在就拒绝出结论"
   （`audit-fb2-off` 那一格就是被这个静默降级骗过一次：文件名少打一个连字符，它安静地走了对照臂分支）。

### 16.5 用户视角的最终口径（写下来免得下次又被"改回默认"绕进去）

- 设置页那个勾**默认是开的**，行为与这次改动之前一致：编不出来就改存 PNG 并弹提示。
- 关掉它的人多半是"按后缀/目录接下游"的人：他们宁可要一次明确的失败，也不要 `.avif` 记录里躺着一张 PNG。
- 开关不管容器内的后端选择，也不管 HDR→SDR；那两件事各有自己的留痕，混进这一个开关只会让它变成"什么都能关掉"。

### 16.6 本轮收口读数

- 门禁：`round6-fallback-gate3.log` ⇒ `GATE_EXIT=0`、**1505 断言 / 0 失败 / 2 跳过**
  （941+267+14+7+12+43+221=1505，与末屏"共 1505 断言 (40.2s)"逐字一致），地板从 1455 刷新为 1505，
  设置指纹逐字不变，"未覆盖"仍只有钩子那一条。
- 断言净增 **+50**（`round5-f-batch-gate.log` 的 1455 → `round6-fallback-gate3.log` 的 1505），拆开只有两个实测差值：
  `--color-tests` **+36**（231 → 267 ＝ 矩阵 45 条替换掉 §15.3 那 9 条下限锁）＋
  `--unit-tests` **+14**（927 → 941 ＝ 开关接线锁与两条结构锁的改写）。
  ⚠ 这一句原先写作"开关接线锁 11 条 ＋ 结构锁改写"，那个 11 是我没量过的拆分（§17 复核时按两份日志的
  `RESULT` 行重算）；只报差值，不再凭印象分配。
- 产品码：Core 3 个文件（`ImageEncoder.cs` 开关字段、`FormatEncoders.cs` 五条腿 + 两份保底实现 + 硬件链上移、
  `JpegGainMapEncoder.cs` 两腿 + 旧口径改写）、App 4 个文件（设置项、管线写者、界面与中英串、审计卡新行）、
  Tools 1 个文件（样张目录先清）。
- 判据被自己写错的地方共 3 处（`_settings.` 子串误判、断言详情里 `FileInfo.Length` 把整套件打崩、
  默认值定反），全部由门禁或套件当场报出，没有一处是我自己回读发现的——这正是把锁写完就当场用一次的价值。
- §15.5 那句"要不要接保底"的待拍板已经**结清**：接，且带开关与告知；F 批剩下的只有 §15.5 列的那批强制环境构造
  （`TTC_FORCE_SDR`、只读目录、断网 WCS、native 目录被占用）。
- ⚠ 上面那句"判据被自己写错的地方共 3 处"是**§16 那一轮之内**的计数；审查轮（§17）又量出 6 处我此前写下的
  判据缺陷（负锁空转、单向探针、地板标签与实况差两枚、把文档注释数成读闸、锚点撞到第二处同名赋值、
  尺子钉死日期）。以 §17 的账为准。

---

## §17 审查轮：把这一轮所有改动再过一遍（2026-09-29，用户："帮我把所有改动的文件都审查一次"）

**判词先说**：这轮审查没有推翻前面任何一条**产品行为**结论，但推翻/改正了我自己写下的 **8 条报告陈述**
（§17.4 逐条列表）和**判据与门禁侧 6 个文件里的一批 fail-open**（§17.2 第 4-9 条），其中两处是真的会让用户
丢东西或丢事实的产品码问题（新装机删不掉凭据、保底把更早那条降级留痕吃掉）。收口读数：`round9-final-cert.log` ⇒ `GATE_EXIT=0`、
**1515 断言 / 0 失败 / 2 跳过**（`--unit-tests` 941 → **951**，判据净增 +10；`--color-tests` 267 不变），
`--settings-migration` ⇒ `NORMALIZE ok 59 轴读毕，全部符合期望`（+1 臂），三次变异（Z1/Z2/Z3）全部当场咬住，
轮前轮后 3 份设置指纹逐字不变。**未判**仍只有 `[1.95/2]` 提交闸门接线那一处（要用户装钩子）。
（`round7-review-gate.log` 是这轮改动刚落地的第一圈，`--unit-tests 950/1` 红在我新写的 Avifenc 锁上——
锚点撞了同名赋值，见 §17.2 第 5 条；`round8`/`round9` 才是改完之后的两圈绿。）

### 17.1 做法

三条 lane 并行按文件切分（Core 编码器 / App 设置与界面 / 门禁与判据），测试与文档留主循环写。
lane 回传后**逐条回代码复核**，只采纳我自己证实的那部分——三条 lane 共回传 13 条"确认缺陷"，
其中 **1 条被证实是错的**（上一轮那条 FitTag P1，引的四处行号现在指向别的代码、结论也反了），
**2 条半对**（§17.3）。最重的三条（凭据门、双向探针、留痕覆盖）我自己复跑了变异，没有只信 lane 的总结。

### 17.2 采纳并修掉的

**产品码 3 处**

1. **新装机那次会话里"清空 API Key"删不掉凭据**（`SettingsService.cs`）。`_loadedFromDisk` 只在
   `Load()` 读到文件那一支置真 ⇒ 首启根本没有 settings.json 的会话里它整会话为假，用户**先填再清**时
   `SaveSecret` 走"不许删"分支只留一条 Warn；下次启动 `Load()` 又从 SecretStore 把那条"我已经删掉了"的
   凭据回填出来，界面上一次会话显示的是"已清空"。改法不是把那个标志放宽（那会把 V7 的保护弄丢），
   而是**再记一个来源**：`_savedToDisk` 在写盘成功后置真（排在 `File.Move` 之后，写失败不算），
   两道门合成 `SecretClearanceIsUserIntent = _loadedFromDisk || _savedToDisk`；`Load()` 开头两个一起复位
   （读失败会把 `Current` 换成默认值表，之前那次写的可信度随之作废）。`LastLoadCameFromDisk` 仍然只说"读"，
   审计卡那句"实际读进来"没被稀释。
   顺手把 `SaveSecret` 的 Warn 与它的文档注释也改到说实话：原先那句"因设置未加载成功，本次未清除…"
   在**迁移触发**的那次 `Load()` 内部 `Save()` 里是假的（那次读取确实成功，只是标志要到 `Load()` 末尾才抬），
   现在说的是"这份空值还不能当成用户清空"，并把两个可信来源点名。日志没人拿它当判据（全仓 grep 只有源文件
   与 `tests/manual/safety/restore/` 那份快照命中），所以这一处**只经构建、无判据覆盖**。
2. **`Avifenc` 迁移门与 `FitTag` 口径不同向**（同文件 :452）。FitTag 认大小写变体，迁移门原先是精确 `==`
   ⇒ 手写成 `"avifenc"` 的那份**逃过迁移**，再被白名单回落成 `Auto`，而 `Auto` 优先走硬件——正是上面
   那段 ⚠ 注释禁止的翻转。改成 `string.Equals(..., OrdinalIgnoreCase)`。
3. **PNG 保底不许吃掉更早的那条降级留痕**（`FormatEncoders.cs:1699` 那一族）。`EncoderFallbackNotifier` 只有
   一个易失槽，原先是裸赋值 ⇒ 同一次编码里更早写的**格式内**降级（例如"MFT 失败已回退 libaom，会明显变慢"）
   被"改存 PNG"覆盖 ⇒ 用户只看见换了格式，永远不知道自己还绕开过硬件后端。现在与 HDR 那份同形：
   先读 `earlier`、非空就并到文案后面（`…｜另外：…`）并另写一行 Warn，日志里两条都在。
   槽位由上层读完即清（`CapturePipelineService.cs:756-757`），所以这里读到的是本次编码写的。
   顺带把那段**说谎的文档注释**改了：它还写着"全仓【唯一】一份实现"、"调用方①AVIF 硬件链 → libaom → 这里"、
   `who` 只举 "AVIF" / "WebP" 两族——硬件链在 §16 已改成只抛出、不换格式，调用点也早已是 5 条 SDR 腿 + 3 条 HDR 腿。

**判据与门禁侧 6 个文件**（每一条都是"我这轮之前写下的、今天被证伪"的形状）

4. `SelfTest.cs` 审计卡：保底开关与增益档改成**双向探针**（正向用 `snap`，反向用一份取反的 `Snapshot()`，
   只在内存里、不写盘）。理由见 §17.3 的 lane P2；同时把 `Load()` 会**重写盘**这件事写进注释
   （原先只承认"会搬一份过来"），并说明取证脚本必须前后拍 sha1。
5. `CorePipelineTests.cs`：`Read()` 找不到文件返回 `""` ⇒ 那条锁里的**负向**判据（`!bench.Contains(...)`、
   `badWriters.Count == 0`）在空串上恒成立 ⇒ 前置一把"这条链的 10 个源文件都要定位到，缺一个先红"的锁；
   `ColorCbo` 与 `currentTags` 两边都解析成 0 时"数量相等"也恒成立 ⇒ 各加一条"解析器必须真解析到东西"；
   `loc.Contains(["中文"])` 原先在整份 `LocaleManager.cs` 里搜 ⇒ 改成先切出 `ZhToEn` 表窗口并**自证切到了**；
   `自检真调 Normalize` 那条地板写着 `>= 4`、标签还写"四枚夹具"，而实况当时已是 6 枚（现在 7 枚）⇒ 抬到 `>= 7`；
   数"读这个闸"的正则原先是 `(s|settings)\.PngFallbackEnabled`，**我自己这轮新写的一段文档注释**
   （`<see cref="EncodingSettings.PngFallbackEnabled"/>`）当场被它数成一处读闸（8 吹成 10）⇒ 收紧成必须带
   `when (` 前缀，实测回到 **8 处**。
6. `ColorPipelineTests.cs`：十格矩阵缺"后端在位"这道前提 ⇒ 注入用的 0 字节壳是夹具自己造的，
   后端根本不在位时它也照样"注入成立"，于是那一格会**绿着通过却一次没判到**"后端在位而这次编不出来"。
   现在每格先探 `IsAvailable`（cjpegli / cjxl / cwebp / avifenc），不在位逐格记 `Skip` 进台账，不记 ✅。
7. `verify.ps1` 五处 fail-open：① `$newest -and …` 在输入枚举为空时让**每个宿主都算新鲜**，还会打印
   "✅ …不早于 0 份构建输入" ⇒ 空集当场 ❌（带 `-SkipBuild` 时按既有约定降级成警告），且那句 ✅ 只在
   `inputs.Count > 0` 时才许打印；② `$hostsToCheck` 为空时整块**一个字都不印**就往下走 ⇒ 补 else 点名；
   ③ 地板只遍历"本轮跑了的"套件 ⇒ 把一整条套件从 `$suites` 摘掉（或改名）时断言数根本不变、门禁全绿，
   现在反向比一遍"基线里有、本轮没跑"并额外比总数；④ `-WriteGateBaseline` 那一轮拿本轮读数和**本轮读数**
   比是自我比对 ⇒ 明说一句"下一轮起才由它拦"，并把它记进未判台账；⑤ `GateGaps` 从不进结论行 ⇒ 现在
   结论行带尾巴"｜另有 N 处未判"。
8. `scripts/probes/logline_format_check.ps1`：硬编码 `C:\PLAN\TrueToneCap` 与**那一天的**那份
   `log\app_2026-09-28.log`——它是 `[1.96/2]` 这道**阻塞**步调的尺子，日志按天轮转，次日起它必红，
   红的还不是产品。现在仓库根由 `$PSScriptRoot` 推（留 `TTC_REPO_ROOT` 口子），日志取 `app_*.log` 里
   最新的并打印"候选 10 份，选中 …（时间戳）"；一份都取不到时**仍然 RC=1**（对照臂不许我手搓）。
9. `Publish.ps1`：缺 `gate-baseline.json` 时静默退回手抄的 340，而"下限来源"只在**失败**那一支才印 ⇒
   现在无条件印一行"下限 N（来源 X）· 本轮实测 M"，缺文件当场点名。

### 17.3 lane 回传里被驳回或说得不准的

- **驳回**：上一轮那条"FitTag 会打断按 `==` 读的字段"（引 `SelfTest.cs:573`、`MainWindow.Settings.cs:322/127`、
  `LocaleManager.cs:94`）。四处行号现在指向别的代码，而真正的读方（`MainWindow.xaml.cs:347` 的 `== "en"`、
  `:2665-2668` 按 Tag 拼法 switch、`CapturePipelineService.cs:234` 的 `== "Gray"`）比的全是 **Tag 的拼法** ⇒
  把大小写变体改写成 Tag 拼法只可能让它们更成立。它附带那句"`AppSettingsData.cs:202` 列了 Week"也不实，那文件里没有 Week。
- **半对（P4）**：说"增益档那条镜像期望式在 Gray 时恒绿、只有质量差分配了'不判别'告警"。前半句对——
  默认就是 Gray，正向那趟确实恒绿；后半句不对，`SelfTest.cs:415` 那句 ⚠ 的条件里就含
  `snap.GainMapMode == dflt.GainMapMode`。处理方式：不靠告警兜，直接把反向探针加进去（P2 一起修）。
- **半对（P3）**：说审计探针"可以在用户没操作时重写 settings.json"，这是**事实**（v6proof 那格量到的 sha1 变化就是它），
  但它建议的"给探针一个只读入口（抑制迁移落盘）"我没做——迁移落盘是产品本意（清了明文 Key 就得重写文件，
  见 `Load()` 里那段 ⚠ 次序注释），为取证把它按掉等于制造第二个行为分叉。做的是把注释改到说实话，
  风险由 harness 的前后指纹兜住：本轮 6 次 `--display-audit`（含两格变异）**全部 `PROC_WRITTEN=False`**，
  门禁三轮也都是"轮后指纹逐字不变"。
- **确认但不修（记进 §17.5）**：P5"与屏无关的判红塞在逐屏循环里 ⇒ 按屏数吹大"。结构确凿——那块在
  `SelfTest.cs:228 foreach (var d in displays)` 之内；本机实测只有 1 块活跃屏（卡片 `\\.\DISPLAY5`，
  每格 1 行读数），所以今天的影响是零。多屏那天会吹大，修法是把与屏无关的探针整段挪到循环外，
  那是结构性改动、且需要一次带多屏的验证，不在这轮顺手做。

### 17.4 报告自己的 8 条不可证陈述（已就地改正）

| # | 原来写的 | 实况（有处的都指到日志） |
|---|---|---|
| 1 | `wl-extend-after.log` 的 `921/1`（两处） | 那份日志是 `passed=920 failed=1`；`921/0` 在 `wl-extend-after2.log` |
| 2 | `NORMALIZE` 自检"五枚夹具" | 六枚（`oob`/`keep`/`cased`/`upper`/`legacy`/`csIdx`），本轮起七枚 |
| 3 | v6proof 夹具"同上一格，但 `SettingsRevision:1`" | 与 oobnomig 差的不止修订号：少 `ColorSpaceIndex`/`ArchiveMode`、多 `GainMapMode`/`QualityJpegLi`（逐字取自 `audit-v6proof.log`） |
| 4 | "实测整解重编 13.2 秒" | 当场 `Measure-Command` 的数，**没有落盘日志** ⇒ 标注成一次性读数 |
| 5 | 腿 B "两个宿主都被点名滞后 ⇒ 牙没被磨平" | 那一跑 `GATE_EXIT` 仍是 0：`-SkipBuild` 把判红**降级成警告**（`freshness-noincremental2.log:12`，本轮 `round7-mutation-Z2` 又复现一次）。这一腿证的是"能点名"，"会拦下"的证据是另一次不带 `-SkipBuild` 的真滞后 |
| 6 | 十格出处挂着 `loud-lock-after.log` | 那份是**被这十格替换掉**的旧下限锁轮（`passed=231`，里面一条 `保底=开/关` 都没有）；出处只有 `fallback-matrix-2.log` |
| 7 | "4 处捕获点都走 `ShowCaptureFailed(ex.Message)`" | `MainWindow.xaml.cs` 里以 `ex.Message` 走的共 **8 处**（:1296/:1496/:1699/:1854/:1925/:2131/:2147/:2424），另有 6 处走写死文案；且捕获点在**窗口层**不在管线 |
| 8 | "开关接线锁 11 条"、"Core 侧 9 处 catch" | 11 是没量过的拆分（实测只有 `--unit-tests` +14、`--color-tests` +36 两个差值）；9 里有一处是 `ImageEncoder.cs` 的**文档注释**被正则吃进去，收紧后实测 **8** |

另有 8 处**纯行号漂移**（`:203→:205`、`:266→:269`、`:520-525→:521-529`、`:328/383/401/426/451→:332/387/405/430/455`、
`SelfTest.cs:299-300→:301-302`、`SettingsService.cs:346→:382` 等），已按本轮实测重标，并在 §14.2 那里加了一句
"行号随编辑漂移，引用请以锚文本为准"——这条规矩本轮自己被验证了两次：我给 `PngLastResort` 补的那段说实话的注释，
恰好把另一把尺子的计数吹大（§17.2 第 5 条）。

### 17.5 变异实测（新写的锁必须当场咬一次）

| 变异 | 改了什么 | 读数 |
|---|---|---|
| Z1 | `Avifenc` 迁移门退回精确 `==` | T2：`--unit-tests 950/1`，点名 `❌ Avifenc 那道迁移门按 IgnoreCase 比（与 FitTag 同口径）`（`mutation-Z1-exact-compare.log`）。T1：`settings-migration-mutation-Z1.log` ⇒ `EXIT=1`、`NORMALIZE fail 59 轴读毕，1 条不符`，不符那条正是新增的 `迁移·AvifEncoder(小写逃过迁移门) avifenc→Auto（期望 LibAom）❌`，其余 58 臂零误报；`SET_DRIFT=False`。恢复后 `settings-migration-after-review.log` ⇒ `EXIT=0`、`ok 59 轴读毕` |
| Z2 | 往 `gate-baseline.json` 塞一枚不存在的 `--ghost-tests` | `round7-mutation-Z2-ghost-suite.log` ⇒ 七个套件**全绿**（951/267/…）却 `GATE_EXIT=1`，红的是新加的反向比对：`❌ 基线里的 --ghost-tests 本轮没有跑`，结论行"套件全绿，但断言数量对不上基线 ⇒ 这一轮不作数"。撤掉后 `round8` 全绿 |
| Z3 | 删掉 `CapturePipelineService.cs:231` 那行接线 | 两格都红：控制格 `设置 True/False 抵达编码设置 False/False`；**关态格** `设置 False/True 抵达编码设置 False/False`——后者正是旧的单向比较永远看不见的那一格（`audit-z3-control.log` / `audit-z3-off.log`，各 `管线落点判红 1 条`、`EXIT=1`）。恢复后两格 `EXIT=0` 且打印 `两个方向都跟得上 ✓`（`audit-restore-control.log` / `audit-restore-off.log`） |
| 没做变异 | ① 矩阵的 `Skip` 分支；② `Publish.ps1` 的来源印行 | 诚实标注：① 要它响得让某个后端真的不在位——**这一条已在 §18.1 结清**（占用 `avifenc.exe` 后 `--color-tests` 的 AVIF 两格转 Skip 并点名原因，对照读数同表）；② 只是多印一行，无判据语义，**至今没有实测读数** |

### 17.6 本轮之后的未修台账（新增 5 条，接 §16.6）

1. 审计卡与屏无关的判红在逐屏循环里（`SelfTest.cs:228`）⇒ 多屏那天"判红 N 条"会按屏数吹大；本机 1 屏，实测无影响。
2. `[1.9/2]` 之外，取证 harness 群（`tests/manual/safety/*.ps1`）仍各自硬编码 `C:\PLAN\TrueToneCap`。
   本轮只修了被**阻塞**步 `[1.96/2]` 调用的那把尺子；harness 是我手工敲的，换机器时红得看得见，暂不动。
3. "测量宿主不许打开保底"那条负锁的边界要在文档里说清：审计卡现在会构造一份 `PngFallbackEnabled=true` 的
   **反向探针对象**（当盘上值为 false 时）。它只喂给 `BuildEncodingSettings`，不喂编码器 ⇒ 不落任何产物，
   负锁也仍然只禁 `PngFallbackEnabled = true` 那种字面赋值。将来若有人把 `flipped` 那份快照拿去真编码，
   §17.2 第 1 条那个"洗白"风险会原样回来。
4. §14.6 那两列欠的（"界面显示什么"要带窗口 UIA；AVIF/WebP/PNG 三格的管线落点被 5 处 Toast 出口挡住）
   本轮**没有**推进，判据面一点没变。
5. **降级留痕这一根槽没有"每次编码开始即清"的锚点 ⇒ 失败路径上的残留会报到下一次截图。**
   全仓 `EncoderFallbackNotifier.LastReason = null` 在产品码里只有两处：HDR 保底的入口
   （`FormatEncoders.cs:1744`）和管线的**消费点**（`CapturePipelineService.cs:757`），而消费点排在
   `NotifyEncoderFallbackIfAny()` 那一句只在**成功路径**上执行（:616 / :727，都不在 `finally` 里）。
   ⇒ 编码整条失败（开关关着、或保底也失败）时那次残留没人清，下一次成功的截图会把它当自己的降级弹出来——
   `:752-753` 那段注释自己就写着"不清空会把上一次的原因报到下一次截图上"，但清的动作只覆盖了一半路径。
   本轮我把 byte[] 那份保底改成"并入更早那条"，让这个残留**更容易被看见**（会挂在 `｜另外：` 后面），
   但没动它的生命周期：真正的修法是"每次编码开始即清"或"消费放进 `finally`"，而那要先定并发语义
   （两根截图同时在飞时这唯一槽归谁——同一族的 `s_lastDegradedToast` 去重也是静态的），属产品口径，等一句话。
   测试侧不受影响：矩阵与 WebP 那几格每格自己先清（`ColorPipelineTests.cs:550/642/736/…`），所以这条一直判不到。

---

## §18 F 批剩下的强制环境构造：占用后端 / 只读设置（2026-09-30，"继续"）

§15.5 那行"还没逼出的构造"列了四格：`TTC_FORCE_SDR`、只读目录、断网制造 WCS 失败、native 目录被占用。
本轮结清其中两格，另两格说清为什么不做。

### 18.1 占用随包后端（`native-busy-cell.ps1`）

为什么用"占用"：§13.7 已否掉"改名造故障"——随包 exe 在 `[1.9/2]` 的构建输入面里，改名既是故障又是输入变更，
还原失败还把下一轮跑在坏环境上。占用是同一格故障的**无损**构造：句柄一放环境就复原。
三条自检（少一条就拒判，脚本里都有）：① 锁到几份、路径逐条打印（本次 2 份：`native/` 与 `Resources/`）；
② **锁完之后亲口试跑一次 `avifenc.exe --version`，必须失败**——挡住的是进程创建（`blocked`），
不是我臆测的"编码会失败"；③ `finally` 放锁后再试跑一次，必须 `ok`，否则当场报"这台机器的后端是我弄坏的"。

| 套件 | 未占用（对照） | 占用 avifenc | 点名 |
|---|---|---|---|
| `--color-tests` | `267 通过 / 0 失败 / skipped 0`（`native-idle-color.log`；与 `round9` 那份逐字相同） | `252 通过 / 0 失败 / skipped 3`（`native-busy-avifenc-color.log`） | `⏭ AVIF/保底=开 — 跳过: 本机取不到 avifenc（NativeAvifEncoder.IsAvailable）`，关态同一句 |
| `--unit-tests` | `951 通过 / 0 失败 / skipped 1`（`round9`） | `945 通过 / 0 失败 / skipped 3`（`native-busy-avifenc-unit.log`） | `AVIF 实测能力探测`、`SVT-AV1 编译判定` 两格转 Skip |

**这一格真正判到的东西**：它第一次行使了审查轮刚给矩阵加的那道"后端在位"前提 ⇒
§17.5 里我诚实标注的"这条分支没有实测读数"现在结清。含义要说白：**没有这道前提时，这两格会在后端根本
不在位的情况下继续报绿**——注入用的那枚 0 字节壳是夹具自己创建的，后端缺席时也没有进程去写它，
于是"注入成立（独占挡住了写入）"这句话照样打印。缺席被读成"在位且失败"，正是本仓反复那一族。
另外两格（cjxl / cjpegli / cwebp）在只锁 avifenc 时保持原读数 ⇒ 归属清楚，不是一锁全变。

未判：门禁层的跳过台账（`verify.ps1:1049-1052`，`Skipped > 基线` 就 ⚠️ 并进未判清单）
**没有占着锁跑一整轮**，这句只是读码所得，不算实测。

### 18.2 设置文件只读 ⇒ 整进程怎么办（`readonly-settings-cell.ps1`）

先纠一处台账过期：探针本身早有 T1（`Test_SettingsWriteProbe_RealProbe`，`--unit-tests` 每轮都跑）——
只读文件那一臂与 **ACL 拒写目录**那一臂（夹具取 `C:\Program Files\WindowsApps`，因为本机 shell 是提权的，
System32/Program Files 统统可写、拿它们当反例会让判据恒真）都是真写一次量的。
§15.5 把"只读目录"整个列为未做，说粗了；**没做的是端到端那一层**，本轮补上。

读数（`readonly-settings-cell.log` / `readonly-settings-cell2.log`）：只读位写入后读回确认
（`ReadOnly, Archive`）⇒ 起 `--display-audit` ⇒ 卡片那句是
`文件本来就在=是  可持久化=False  实际读进来=否 ⇒ 喂给管线的是默认值表（★ 文件在但没读进来 —— 语法/权限/反序列化失败，看日志那条 Warn）`；
退出码 0（这一格的产品口径就是"按默认值表跑 + 记日志"，不判红）；**盘上 sha1 前后 `C76ABAC1…` 逐字不变**
（不可写时它本就不该写）；收尾只读位摘回 `Archive`。
卡片指向的那条日志确实存在（`app_2026-09-30.log` 01:01:34 那条 Error）⇒ 指针不是空的。

**顺带抓到一句假话并改掉**：那条 Error 原先写死"**设置目录不可写** ⇒ 不读取也不保存…"，而这一格里
目录其实是可写的，是那份文件带了 `ReadOnly` 位——`CanPersistSettings` 有两种 false，日志只报其中一种，
排查的人会照着"去改目录权限"忙一场。已改成点名两种成因 + 给分辨办法（同目录随手新建一个文件试试），
`SettingsService.cs:282`；全仓 grep 确认没有判据钉这段文案（只有源文件与 `tests/manual/safety/restore/`
那份快照命中）。改后复跑同一格，01:01:34 那条就是新文案。

### 18.3 剩下两格为什么不做（不是忘了）

- **断网制造 WCS 失败**：要动本机网络状态（影响别的进程与该用户会话），不属我可以自行执行的构造。
  可行的替代是"给一条查不到匹配的 profile"，但那是另一条码路，冒充不了 WCS 超时——宁可留着不判。
- **`TTC_FORCE_SDR=1`**：读它的地方是 `MainWindow.xaml.cs:1957-1961`，作用在**选区截图的预览路径**上
  ⇒ 要一次带窗口的会话才看得见后果，归批次 #7（起真身那批要逐次报备）。同族还有个
  `TTC_GPU_COMPOSITE=0`（`HdrCaptureWindow.cs:386/767`），同样要带窗口。

### 18.4 同目录那份设置不见了 ⇒ 旧位置搬移路（`legacy-migrate-cell.ps1`，两跑可重复）

D4 场景集里"首运行盘上无配置文件"这一格，此前只有**源码锁**（`MigrateFromLegacyLocation` 存在且只搬一次），
没有整进程读数。本轮做**可做的那半臂**：把 bin 目录那份 `TrueToneCap.settings.json` 移到一边
（同一目录内改名 `.ttc-aside`），旧位置 `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json` 全程**只读不动**，
然后起一次 `--display-audit`。

读数（`legacy-migrate-cell.log`，第二跑 `…cell2.log` 搬出来的内容**逐字相同**）：

- 卡片：`文件本来就在=否  可持久化=True  实际读进来=是 ⇒ 喂给管线的是你磁盘上那份`
  —— 这三态第一次在"文件本来没有"的情况下互不矛盾：搬进来之后确实算读到了。
- 同目录**重新长出**一份，内容是老文件搬来后被归一 + 剪枝 + 盖改的结果：
  `{"SettingsRevision":2,"FormatIndex":3,"GainMapMerged":true,"Quality":100,"OutputPath":"D:\\TTC","AutoStart":true,"ChromaAvif":"444","FirstRun":false,"OverlayColor":"#99001833"}`
  而旧位置那份是 v0.3.x 的全量形态（`SettingsVersion:1` + 七档 `QualityXxx` 各自带值）。
- ⇒ **老用户被交还默认质量档，这条路径第二次被独立量到**：`FormatIndex 4→3` 是增益图并入的重映射，
  而 `QualityAvif:63`/`QualityJpegLi:0.5`/`QualityWebp:50`… 这些**没有出现在新文件里** ——
  老文件没有 `SettingsRevision` 键 ⇒ 读作 0 ⇒ 交还门开 ⇒ 这些档被清成默认（差集序列化不再写它们）。
  这与 §14.1 v6proof 那格是同一件事，只是**入口不同**（那次是手改修订号，这次是老文件自然缺键）。
  界面上依旧没有任何东西，只有日志一条 Info——所以 §14.1 那句"worth 一句拍板"现在有两份独立读数背书。
- 还原自检：原件移回后 sha1 与轮前 `C76ABAC1…` 逐字相同；旧位置 `40589A55…` 全程未变。

⚠ 这一格里我自己的 harness 先犯了个错值得记：状态行写的是 `REAPPEARED=$(Test-Path $set)`，
而那句排在**删掉产出文件之后** ⇒ 恒为 False，屏幕上打了"没长出文件"，日志里却是"长出来了"。
已改成在删除之前先落变量（`legacy-migrate-cell.ps1` 里带了一条注释），第二跑打的是 `REAPPEARED=True`。
读数以日志为准，不以那行状态为准。

**未做的另一半臂**：真正的"两处都没有"（连旧位置一起移开）要动 `%LOCALAPPDATA%` 里那份用户数据，
不在我可以自行执行的范围内 ⇒ 那一格在 D4 表里记**未评**。

---

## §19 D4 可用性分场景评级（计划 §8 那张表；口径：每格三件证据齐才给字母）

计划给 D4 定的判据是"**一个产物 + 一条日志行 + 一张证据截图**，缺一项不写健康"。
把 11 个场景按这三腿过一遍，结论是：**本轮没有任何一格能给字母评级**——不是判据失败，
是每格都缺同一条腿（目视/截图），而那一腿要一次带窗口的会话（批次 #7，起真身需逐次报备）。
所以这张表的价值不在评级，在于**把"缺哪条腿"逐格钉清楚**，让 #7 那批有明确的待办清单。

| 场景 | 产物腿 | 日志腿 | 目视腿 | 本行结论 |
|---|---|---|---|---|
| 单屏 SDR 存文件（默认 PNG） | ✅ 色彩管线判据读回 PNG 的 iCCP/cICP；D 批 pairwise | ✅ `编码完成: …(KB)`（`CapturePipelineService.cs:615`） | ❌ 未拍 | **未评**（只缺目视） |
| 单屏 HDR（本机有 HDR 面板） | ✅ 外部 ffprobe / libultrahdr 读回（§13、G 批） | ✅ 审计卡"管线落点"段，本轮 6 跑全落盘 | ❌ HDR 观感按计划由你一句定，我不写"好不好" | **未评**（观感待一句） |
| 广色域 + 自动档解析 | ✅ 审计卡三档读数；EDID ΔG=0.0963 本机自量 | ✅ 同上 | ❌ 界面那句箭头未拍 | **未评** |
| 多屏 / 混合 DPI 选区 | ❌ | ❌ | ❌ 本机现在只有 1 块活跃屏（卡片 `\\.\DISPLAY5`，§17.5） | **不可做**：要接第二块屏 |
| 英文界面 | ✅ **本轮不再为零**：进程外 UIA 走真实树，41 个带名控件里英文模式残留 8 条（修前 9） | ✅ 产品自己那句 `英文模式下仍有 11 处`（4 次启动稳定），但它只量 Loaded 时刻 | ⚠ 有 UIA 实测，仍**没截图**；且设置页之外（覆盖层/HDR 窗）没扫 | **半格**（§20：9→8 已修一条，两把尺子的口径差异已判定；截图与其余窗口仍缺） |
| 无感连拍（并发/取消/编码失败可见性） | ✅ 崩溃哨兵那一族 **15 条 T1**（`--unit-tests` 里 `EncoderCrashSentinel:` 6 条 + `哨兵:` 9 条，本轮按日志逐条数） | ✅ 三条失败点被源码锁钉住；§22 另有一次完整 `#opId` 链（启动→捕获→协商→编码→完成 5256 ms） | ⚠ 单张端到端本轮**真跑通**（§22）；"连拍/并发时序"仍没实测，Toast 也没拍 | **半格**（单张已判；并发那一腿未判） |
| OCR 三引擎 + 翻译三档 | ✅ `--ocr-tests` 12 条（含语言映射与兜底） | ✅ | ❌ 结果框里的字没看过；**live 翻译未跑**（要出网，等点头） | **未评** |
| 首运行盘上无配置文件 | ✅ **同目录缺失那一臂本轮实测**（§18.4：旧位置搬移路两跑可重复） | ✅ 卡片三态 + 那条 Info | ❌ | **半格**：两处都无那一臂要动 `%LOCALAPPDATA%`，未做 |
| 只读安装目录 | ✅ 探针两臂 T1（只读文件 + ACL 拒写的 `WindowsApps`）；整进程只读文件臂本轮实测（§18.2） | ✅ 那条 Error 文案本轮还改掉一句假话 | ❌ 真把程序装进只读位置没做过 | **未评**（ACL 臂缺整进程读数） |
| 剪贴板到各目的地 | ✅ §22：无感截图完成后剪贴板里是**位图 3840x2160**；产物 4766.9 KB 被 `ffprobe` 独立读回 `rgba64be/smpte2084/bt2020`；另有 `ResolveActualPath` 4 条 T1 | ✅ `#ohzq94` 那条链一路到"无感截图完成 (5256ms)" | ⚠ 只验了"粘进系统剪贴板有位图"这一格；**粘到具体目的地**（微信/Word/画图）没验 | **大半格**：位图通道已判，目的地那半未判 |
| 托盘与热键冲突回退链 | ✅ §22：抢下两个默认键 ⇒ 产品连吃两档，实际注册 `Ctrl+Alt+S` / `Ctrl+Alt+Q`，并用"再注册一次必须失败 + 对照臂必须成功"独立反证 | ✅ `冲突: … (err=1409)`（本轮还把恒 0 的 `err` 修对了） | ❌ 回退后的键位**在界面上看不出**（UIA 那一腿未判，且代码里根本没把它写回设置或提示） | **半格 + 一个待拍板**：链本身工作正常；换掉的键位要不要让用户看见，等你定（§22.3 第 2 条） |

一句判词：**已判的腿没有一条报出"用户拿到的是错东西"**，而三条"不可做/半格"当时全部卡在
①一次带窗口的会话、②出网一次、③动一下工作区外那份用户数据。
（**后续更正**：这张表写完后用户授权了①，于是 §22 把"热键回退链"与"剪贴板位图通道"两格从**未评**推进到
**半格/大半格**，并顺手修掉一个恒打 0 的错误码；仍然缺的是出网那一次与选区覆盖层的几何。）

---

## §20 批次 T3 第一格：英文界面的第一次真读数（两把尺子，2026-09-30）

D4 场景 5 在此之前是**零次读数**。harness `tests/manual/safety/en-ui-count.ps1`：把 `Language` 写进盘上那份设置
（跑前备份、跑完移回并核 sha1）⇒ 起真身 ⇒ 等 12 s ⇒ ①读产品自己那条 Locale 日志 ②从进程外用 UIA 走真实控件树
⇒ 杀掉本次起的 PID ⇒ 还原设置。

| 臂 | 带 Name 的控件 | 含 CJK | 含拉丁词（对照） |
|---|---|---|---|
| 英文模式（修前） | 41 | **9** | 34 |
| 中文模式（对照臂） | 41 | **34** | 9 |
| 英文模式（修后） | 41 | **8** | 33 |
| 产品自测（英文模式，4 次启动） | — | **11 处** | — |

两臂互为对照（34↔9 镜像）⇒ 这条尺子扫到的是真内容，不是空树。

**两把尺子为什么给的数不一样**（这是本格最值钱的一条方法学）：产品那次扫描发生在 `Loaded`，
而运行时被 `.cs` 盖回去的中文它**看不见**；UIA 是 12 s 之后走树，看得见"盖回"，却看不见没悬停的 ToolTip
（`MainWindow.Localization.cs:254` 那段注释自己说过残留计数会漏掉 ToolTip 那一整类）。
⇒ **绝对数不可横比，各自的 delta 才算数**：UIA 那一趟 `9 → 8` 且「就绪」从清单里消失 = 修好了；
产品那句始终报 11 不动 = 它对这一类天生盲。这本身就是一条判据缺口（见 §20.2 末）。

**这一格抓出并修掉的缺陷**：`MainWindow.xaml.cs` 三处 `StatusTxt.Text = "就绪"` 直写中文字面量
——XAML 初值与 `ZhToEn` 表里都有 `就绪 → Ready`（`LocaleManager.cs:148/316`），扫描也确实翻了它，
但每次操作后 .cs 又把中文写回去 ⇒ **英文界面下状态栏每按一次就变回中文**。
修法：三处改走 `LocaleManager.Ready`；新锁 `Test_StatusBarLocale_CsMustNotReWriteChinese`
钉"直写 = 0 处"与"三处复位点都走 Ready"（`--unit-tests` 953 → 956），复跑 UIA 实测掉那一条（读数在上表第三行）。

**同类缺陷仍在（记入账，未修）**：失败态状态栏那三条（`【WGC 捕获服务未初始化】`等）同样是 .cs 直写中文，
但它们被 6 把镜像锁钉成"这个字面量必须仍在源码里"⇒ 要修得连那 6 把的口径一起改，属独立一批，不在这轮顺手做。

### 20.2 剩下三格为什么不做（不是没做）

- **区域截图 → 选区覆盖层 → 工具条几何 / 托盘与热键回退链 / 剪贴板真粘贴**：入口依次是
  一次真实鼠标拖拽与 `RegisterHotKey`（只能被真实键输入触发）。剪贴板三个写入点
  （`MainWindow.xaml.cs:1555/1619`、`OcrPreviewWindow.xaml.cs:574`、`SilentCaptureToast.xaml.cs:314`）
  全都排在那两个手势之后 ⇒ 要量就得往你的交互会话里注键鼠。
  （**后续更正**：用户随后授权注键鼠 ⇒ **热键回退链**与**无感截图到剪贴板**两格已拿到四腿读数，见 §22；
  仍只剩"选区覆盖层的工具条几何"未做——那要一次真实鼠标拖拽，且覆盖层全窗置顶会占用你的光标会话。）
- **`HdrCaptureWindow` 里那批中文字**：烘进 D3D 位图 ⇒ 门禁、UIA、告警三路机制性全盲（§55 已记），本轮维持未判。
- **产品自检的那条盲区**（上面方法学的直接推论）：`英文模式下仍有 N 处` 只在 `Loaded` 时量一次，
  运行时被盖回的中文永远不进它的数 ⇒ 它报的 11 是"扫描时刻的残留"，不是"用户看到的残留"。
  要把这一格修对，得让那次扫描在页面稳定后**再量一次**（或在 `.cs` 赋值路径上复查），属产品码一批改动。

---

## §21 总评：这份审计最后给的一句话

**分层判词**（每层都指到实测出处，没量的地方直说没量）

1. **色彩与编码正确性 —— 可信**。D/E 两批（§13-§14）把"产物说谎"这一类逐格逼出：声明与像素同向、
   保底带声明、位深/色度按格式钳制、`NORMALIZE` 59 臂直调生产 `Normalize`。
   已知残留只有一条方向性的：CPU 与 HLSL 的 Reinhard 亮度权重不同组（当前不可达，§57 那批）。
2. **失败可见性 —— 从"会静默"改到"必须出声"**。§15 数出的静默点到本轮为止：换格式保底覆盖五格式并带用户开关、
   更早那条格式内降级不再被覆盖、消费点收进 `finally`（变异 W2 验过牙）。
   仍开着：**单槽通道在并发截图下互相顶掉**（§17.6 第 5 条），取消/失败态的提示去重口径还没人拍。
3. **设置与持久化 —— 判据面最厚的一层，但两处口径还欠**。`CanPersist` 两臂 T1（含 ACL 拒写的真目录）、
   差集序列化 + 修订号迁移、旧位置搬移路本轮有端到端读数（§18.4，两跑逐字相同）。
   欠的是**产品口径**：老文件缺修订号 ⇒ 质量档被静默交还默认（两条独立路径实测背书，界面上零痕迹）。
4. **界面与本地化 —— 全项目最弱的一层，而且是机制性的**。41 个带名控件实测残留 8 条，
   其中状态栏那条本轮修掉并加了锁；但同族里 .cs 直写中文 63 处、正文形态 70 项、LogViewer 整工程无机制（§55），
   而**产品自己那条自检只在 `Loaded` 量一次，看不见运行时被盖回的中文**（§20 的方法学）。
   ⇒ 这一层的"已判"只覆盖判据扫得到的部分；扫不到的不是安全，是不可见。
5. **判据本身 —— 规模与牙齿都在涨，天花板没拆**。`verify-round22.log`（06:41:25，本轮最终验收）⇒ 7 套件 /
   **1555 断言** / 0 失败 / 39 s / `GATE_EXIT=0`，地板已重生成到 1555
   （`--unit-tests` 958 → 989 → **991**，后两档分别是 §25.5 的两条锁与 ⑤ 补的两条）；
   中途读数（1522 / `round12`、1553 / `round19`）留在原处不回改，以这一行为准。
   **两轮红是我自己撞出来的，不是产品**：`round20` 在 `[1.7/2]` 跑 `TrueToneCap.Tools.exe --nvenc-color-probe`
   时报 `hostpolicy.dll 找不到`（PowerShell 在 `$ErrorActionPreference=Stop` 下把它当终止错误整脚本中止，
   退出码 1），并留下一个孤儿 Tools 宿主把 `round21` 的构建锁死（MSB3027 "文件被 TrueToneCap.Tools 锁定"）。
   根因是我让**门禁轮与 T3 harness 并发**（06:32 那一跑覆盖层 cell 还在收尾时 round20 正在 `--no-incremental` 构建）
   ——本仓早有"并发抢同一份 bin"的账，我又犯一次。清法：等孤儿自然退出 + 全解重建一次通过 ⇒
   `round22` 单独串行跑，才是这批代码的验收轮。**教训：门禁轮跑着的时候，别的什么带 bin 的都不要碰。**
   本审计周期共**起产品真身 23 次**（无窗口审计卡 11 次、`--settings-migration` 2 次、带窗口 10 次）。
   **本审查周期新增 4 枚变异（Z1 / Z2 / Z3 / W2）全部当场咬住**，
   本轮（§25）另加 4 枚（搬闩锁 / 改回 void / 删留痕日志 / 把结果丢回 Wait 边界）各命中自己的规则，
   此前 E·F 两批另有 7 枚（见 §14-§16 的变异表）。
   天花板还是那一条：**Test 只引用 Core ⇒ App 侧运行时行为只能靠审计卡与 UIA 进**，
   而审计卡有 5 处 Toast 出口不在审计态出声（§14.6），UIA 进不去选区覆盖层（要真实拖拽）。

**剩余风险清单（按"会不会咬到用户"排）**

| # | 风险 | 现状 |
|---|---|---|
| 1 | ~~提交闸门没接线~~ **已解决（2026-09-30）**：`core.hooksPath` 已指向 `scripts/git-hooks`，pre-commit 真跑通一次（构建校验 0 错误，28.5 s），`round13` 的 `[1.95/2]` 翻绿、结论行首次报"无未判格子" | ⚠ 副作用要记着：`.git/hooks` 里 Qoder 自己的 `post-checkout`/`post-commit` 从此不再执行（一个 hooksPath 只能指一处）；退路是 `install-git-hooks.ps1 -Uninstall` |
| 2 | 界面本地化机制覆盖不全（.cs 拼串 / 正文 / LogViewer / 位图字）＋ 产品自检量的是 Loaded 时刻 | 已量化（8/41 实测 + §55 静态账）；修到铺满是一批产品码 |
| 3 | 老文件缺修订号 ⇒ 质量档静默交还默认 | 两条独立路径实测背书；属口径问题，不是实现问题 |
| 4 | 降级留痕单槽在并发下互相顶掉 | 生命周期本轮收口（finally 消费），归属语义未定 |
| 5 | MFT/QSV"自述"与"调用"不同向 | 本机不可验，你已定 parked |
| 6 | 热键被回退之后**用户看不出来**：既不写回设置、也不上界面，只在日志里（§22.3 第 2 条） | 回退链本身实测工作正常（连吃两档仍拿到可用键位）；缺的是那一句提示，属产品口径 |

**一句话总评**：编码器与设置那两半已经做到"错了会被抓住"；界面与本地化那半还停在"抓住了才知道错"，
它缺的不是更多判据，而是**把扫描面铺满**的那批活儿 —— 外加你手上那一条 `install-git-hooks.ps1`。

---

## §23 现场事故：强制 SDR 时看到的"很古老的截图界面"（2026-09-30，用户目视抓到）

我在跑 §22 之后剩的那一格（选区工具条几何）时用了 `TTC_FORCE_SDR=1`，你在屏幕上看到一个像旧版的截图界面。
**结论：那不是一个更老的版本，是同一条链的另一条分支 + 它自己的 GPU 背景在崩。** 两件事都有读数。

### 23.1 为什么会长那样

- 这个诊断开关只做一件事：`hdrDesktopPixels = null`（`MainWindow.xaml.cs:1957-1963`）⇒ `hasHdr=false` ⇒
  阶段 2 不进 `HdrCaptureWindow`（类名 `TTC_HC`，全 D3D11 原生窗、scRGB 直显），改走 **`SelectionOverlay`**
  （XAML 覆盖层，背景是 `WriteableBitmap` CPU 合成）。上一格还顺手量到这件事本身：HDR 屏上按截图热键
  进的是 `TTC_HC`，而它的 UIA 子树里**一个控件都没有**（全屏 3840x2160、0 控件）⇒ §55 那句
  "字烘进 D3D 位图、门禁/UIA/告警三路全盲"现在有现场证据。
- 两条路给用户的动作是**对等的**（`HdrCaptureAction{Cancel,Save,Copy,Annotate,Ocr,Translate}`
  vs `BtnConfirm/Annotate/Copy/Ocr/Translate/Cancel`）⇒ 你看到的"古老"不是功能退化，是**另一套皮肤**。
- 让这套皮肤显形的是下一件事：这一次运行里 SelectionOverlay 的 GPU 背景**每次都抛 NRE**，于是落到
  CPU 兜底（`GPU背景=False`）——背景不再是 GPU 帧而是位图，观感整个退一个年代。
- 用例 A 里 UIA 在那个全屏窗只看到 `Pane/Pane/Image 3840x2160` 加两条提示文字
  （「QQ (1442×959)」「🪟 QQ … 单击选中 | 拖拽自…」）⇒ 覆盖层当时停在"选区还没成形"的状态。

### 23.2 已经修掉的三处（编译过；日志那两处实测生效）

1. **日志把异常说成超时**（最要命）：`Task.Run(…).Wait(3000)` 抛异常时 `presentOk` 同样是 false，
   紧跟的 `if (!presentOk)` 无条件打印 `GPU 呈现超时 (3s)，回退 CPU 渲染`。时间戳显示两次日志只差 30-70 ms
   ⇒ 那 3 秒根本没走过。后来人按"GPU 超时"去查锁竞争/驱动，方向整个错。现在分三条：
   `GPU 呈现抛出异常（不是超时）` / `GPU 呈现超时（等了 3s）` / `GPU 呈现失败 ⇒ 回退 CPU 渲染`。
2. **`AggregateException` 把真凶藏起来**：`Wait()` 包装后只剩
   `One or more errors occurred. (Object reference not set to an instance of an object.)`。
   现在拆到 inner、打类型 + 首个栈帧 ⇒ 实测打出
   `NullReferenceException … <>c__DisplayClass77_2.<.ctor>b__4()`（= `Task.Run` 那个后台 lambda 内部）。
3. **失败路径不 Dispose**：`Initialize` 成功、`Present` 抛出这一支既不 Dispose 也不置 `_bgRendered`
   ⇒ 每失败一次留下一个已建的全屏窗口 + 一条 D3D 交换链。这恰是 2026-09-15 那段注释声称已防住的泄漏
   （它只防了"失焦再激活时重复 new"，没防"present 抛异常"）。现在失败即 Dispose + 置 null。
   > → 这一笔当时修得**不完整**：超时腿当场 Dispose 会撞上后台线程还在呈现 ⇒ use-after-dispose。
   > 分腿写法见 **§25.4**，别照抄上面那句"失败即 Dispose"。

### 23.3 没定位到的那一步——数字自相矛盾，所以我不猜

一次捕获流程里（`阶段1 WGC捕获完成` ×1、`诊断开关` ×1，同一个 `#qqvjc7`）：

| 日志行 | 次数 | 位置 |
|---|---|---|
| `覆盖层就绪` | **30** | Activated 回调内，GPU 块之后（`SelectionOverlay.xaml.cs:288`） |
| `GPU 呈现抛出异常` / `失败 ⇒ 回退` | **29 / 29** | 同一回调的 GPU 块 |
| `背景渲染 …ms` | **1** | 同回调、紧接就绪之后，包在 `if (!_bgRendered)` 里 |
| `窗口检测完成` | **1** | 再往后几行 |

矛盾：`_bgRendered` 全文件只在 `if (!_bgRendered){ _bgRendered = true; … }` 里被置位、无任何复位，
回调开头还有 `if (_bgRendered) return;`。⇒"就绪 30 次"要求守卫当时为假，"背景渲染 1 次"要求守卫为真，
**同一份字段在同一次执行里不可能两个都成立**。要么这个回调被多个实例各自订阅（那"背景渲染"也该是 30 次），
要么守卫读的不是我以为的那个东西。
下一步很便宜且已明确：在守卫两个分支各打一条带实例 `GetHashCode()` 的日志，重跑同一 harness（约 40 s）
就能分清"多实例"还是"守卫失效"。**没有这条读数之前，我不把任何一种解释写进结论。**

> → 这笔账已在 **§25** 结掉：打点给出"实例号只有一个"，窗清单给出 36 个可见背景窗 ⇒ 两者只在
> **递归**下自洽，根因是闩锁写在回调底部。原始判断不回改，结论以 §25 为准。

### 23.4 为什么这不只是"我的诊断开关"的事

`SelectionOverlay` 不是只有开关能进：**任何拿不到 HDR 帧的场景都走它**——SDR 面板、ACM 关着、
WGC 给不出 extended range。"GPU 背景抛 NRE ⇒ 掉 CPU 渲染 + 29 次再入"在那些机器上是不是天天发生，
**本机没有读数**（这块屏是 HDR 面板，只能用开关复现）⇒ 要判得把屏切成非 HDR 或在一台 SDR 屏上跑同一格。
连同 23.3 的再入谜团一起进未修台账。

### 23.5 我这一轮自己的账（一起记，别只记产品的）

- 为量"工具条几何"我用了真鼠标注入，结果只拿到半个读数：用例 B 量到 6 个按钮、包围盒 1320x108
  完整落在屏幕内（PASS）；用例 A 两次都 0 按钮（拖拽没进入"选完"状态，原因未定）⇒
  不变量②"两次位置必须不同"**没判成**。
- 本脚本进程是 DPI-unaware 的，`SetCursorPos` 坐标在 1.5x 缩放的 4K 屏上会被虚拟化
  ⇒ 这一格只报不变量（是否越出屏幕），不报"工具条离选区多少像素"这种要看真数的数。
- 第一版 harness 的 Add-Type 里我把 `SendInput` 声明成 `public` 而参数类型是私有嵌套结构 ⇒ CS0051
  编译失败、后续每一步只报 `TypeNotFound`，看起来像"产品没反应"。现在脚本开头有"类型编不出来就不给结论"的自检。

### 23.6 追问："GPU 背景失败"凭什么改变整体观感——把机制拆开算

先纠我自己上一轮的措辞：**背景失败本身不改样式**。改样式的是它连带产生的三件事，
而且这三件的证据强度不一样，逐条标出来。

结构前提（代码依据，`HDRPreviewWindow.cs:160-170` 与 `SelectionOverlay.xaml:8-27`）：

- 背景窗的样式是 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP` + `WS_POPUP | WS_VISIBLE`，
  父窗为 0、**不置顶**（注释写明"必须位于选区覆盖层之下，否则会吃掉鼠标事件导致无法框选"）。
- 覆盖层自己是 `RootGrid Background="Transparent"`，里面一层 `DesktopImage`（正常路径下是空的）、
  一层 `DimOverlay #80000000`（50% 黑）、四块 mask、工具条。**覆盖层不提供背景，背景来自它下面那扇窗。**

| # | 机制 | 为什么会改观感 | 证据强度 |
|---|---|---|---|
| A | **孤儿背景窗叠在透明覆盖层之下/之上** | `NOREDIRECTIONBITMAP` 的窗口没有 DWM 重定向位图，DWM 只能合成它交换链里的内容；而 `Present` 抛异常 ⇒ 那条链从没画过 ⇒ 客户区是**未定义/黑**。旧代码失败不 Dispose ⇒ 一次捕获里堆 29 个全屏黑窗。覆盖层是透明的，**谁在上面谁就决定你看到什么**：z 序一 churn（两边都在调 `SetWindowPos(HWND_TOPMOST)` 抢顺序）你就可能看到黑底 + 零星控件，而不是桌面截图 | 窗口属性=代码依据；29 次=读数；"确实翻到上面过"=**没证**（没截图） |
| B | **色管线路径不同** | GPU 路是 `BgraToScrgbLinearFast` → scRGB Float16 交换链 → **DWM 色域感知合成**；CPU 路是 `WriteableBitmap` 装 BGRA8、按 sRGB 解，再叠 50% 黑遮罩。同一帧桌面在 HDR 面板（本机纸白 120 nits）上走这两条，明度与饱和会整体位移 ⇒ 看着像换了个年代的皮肤，而不只是内容变了 | 两条管线=代码依据；本机 `GPU背景=False`+`背景渲染 (GPU=False)`=读数；**差多少没有像素级对照** |
| C | `DesktopImage Stretch="Fill"` | 位图尺寸 ≠ 布局尺寸（多屏/负坐标）时帧被硬拉 ⇒ 文字发糊 | 代码依据；**本机单屏不成立**，只算通用风险 |

顺带把我上一轮的一个猜测**否掉**：背景窗带 `WS_EX_NOACTIVATE` ⇒ 它**不会抢激活**。
所以 §23.3 里"Activated 进了 30 次"不是这 29 个孤儿窗造成的——那个矛盾仍然是开放的，只有打点能定。

要坐实 A 与 B，各差一条便宜读数：① `EnumWindows` 按 PID 列出顶层窗 + z 序（能直接看到叠了几个背景窗）；
② 同一帧桌面分别走两条管线各存一张图，逐维比亮度/饱和（工具是现成的 `--container-audit`/`ffprobe` 那一套）。
这两条都记进任务 #12，别把"看起来像"当结论。

---

## §24 两条预览路径的色彩差——把 §23.6 的假设 B 量死（2026-09-30，用户："跑一次对比测试"）

### 24.1 怎么量的（两边独立实现，不用同一份代码自证）

- 被测量取**产品自己算的数**：`TrueToneCap.Test.exe --scrgb-compare`（新增的诊断模式，不是套件 ⇒
  不产 RESULT、不进断言地板）把 256 个中性灰码点逐个喂进
  `PixelOps.BgraToScrgbLinearFast`，并同时打出过一遍 `Half` 之后的值（交换链真实存储精度）。
  读数：`tests/output/scrgb-compare.csv`。
- 参照曲线由 `scripts/precision/scrgb_path_compare.py` 按 **IEC 61966-2-1 分段函数**独立实现，
  再各自折回 8-bit 码点比较。换算 nits 时用到本机 SDR 纸白 = 120 nits（Δ码点那一列不依赖这个假设）。
- 自检跑在计算之前（参照点 128、分段曲线互逆、half 的 1 ULP 步长行为）；
  而且它**真的拦下了一次**：脚本原先拿 CPython 的 `float→half` 当被测量，与 .NET 的 `Half`
  在 **240/256 个码点上不一致**（最大 2.5e-8，约 1 ULP）⇒ 主口径改取产品那一列，
  工具的 `to_half()` 只留作自检。**这就是"两边的 half 不是同一个东西"的现场证据，注脚里写着。**

### 24.2 读数

| 误差来源 | 峰值（线性） | @码点 | 折成 Δ8bit 码 | Δnits @纸白 120 | 均值 |
|---|---|---|---|---|---|
| ① `BgraToScrgbLinearFast` 的近似 | 2.1e-07 | 252 | **0.021** | 0.0000 | 4e-08 |
| ② Half16 存储量化 | 2.35e-04 | 197 | **0.037** | 0.0282 | 5.1e-05 |
| ＝ 两条路的总差 | 2.35e-04 | 197 | **0.037** | 0.0282 | 5.1e-05 |

分段看总差（Δ8bit 码）：暗部 0-10 → max 0.002；低中 11-64 → 0.010；中 65-200 → 0.037（@192）；高 201-255 → 0.031。
折算到本机 10-bit 网格，最大约 **0.15 级**。

结构上还有一条：交换链声明的是 `ColorSpaceType.RgbFullG10NoneP709`（`HDRPreviewWindow.cs:226/378`）——
**G10 = gamma 1.0 = 线性**，正好对上 scRGB Float16 缓冲；而 WinUI 的 `WriteableBitmap` 按 sRGB 解。
⇒ 两条路各自的空间声明都是对的，差的就只是上面那点数值。

### 24.3 判词：**假设 B 被这批数字否掉了**

两条路径的数据差最大只有 **0.037 个 8-bit 码点 / 0.028 nits**，比一个可分辨的码位小一个数量级以上
——**"色彩管线不同所以看着像旧版"这个解释站不住**，我自己上一轮那条推测要撤回。
`BgraToScrgbLinearFast` 这个名字里的 "Fast" 也测了：它对 IEC 参照的最大偏差是 2.1e-07（0.021 码点），
不是一个隐藏的降级。

于是 §23 那台机器上"整体风格和 UI 都变了"只能由**内容/叠放**解释，即 §23.6 的机制 A：
覆盖层本身是 `Background="Transparent"` + 一层 50% 黑遮罩，**画面全靠它下面那扇背景窗供帧**；
而旧代码在 `Present` 抛异常时留下一串 `WS_EX_NOREDIRECTIONBITMAP`、从没画过任何东西的全屏窗
（未定义/黑）。**这是"没有画面/黑底"，不是"颜色变了"。**

还欠的最后一条读数也明确了：在覆盖层开着的时候截屏（`PrintWindow(2)` 按 HWND 抓，不抢焦点），
GPU 成功态与强制 CPU 态各一张，逐像素比 ⇒ 直接把"看起来变了"折成一个数。连同 30 次再入的打点
一起记在任务 #12。

**认证**：`round15-scrgb-compare.log` ⇒ `GATE_EXIT=0`、7 套件 **1522 断言 / 0 失败**、
`[1.95/2]` ✅、轮前轮后设置指纹逐字不变。这一轮的要点是**总数一点没动**（1522 与 `round13/14` 相同）——
新加的 `--scrgb-compare` 是诊断模式而非套件，不产 RESULT、不进断言地板；
若当初顺手把它挂进 `$suites`，地板就会在同一轮里漂移，那正是本仓防的那类事。

---

## §22 批次 T3 后两格：热键回退链 与 无感截图到剪贴板（2026-09-30，用户授权注入键鼠）

harness `tests/manual/safety/hotkey-clipboard-cell.ps1`，读数 `hotkey-clipboard-cell2/3/4.log`。
做法：本进程先 `RegisterHotKey` 抢下 `Ctrl+Shift+S` / `Ctrl+Shift+Q` ⇒ 产品启动必然走回退；
再从它自己的日志里取出"实际注册的是哪条"，按那条键送一次 `SendInput`。

### 22.1 三条自检（少一条就不给结论）

| 自检 | 读数 |
|---|---|
| ① 占用抢得到 | `Ctrl+Shift+S ⇒ True`、`Ctrl+Shift+Q ⇒ True`（抢不到就说明冲突来自别的程序，对照不成立） |
| ② 反证带对照臂 | 本进程再注册「Ctrl+Alt+S」「Ctrl+Alt+Q」⇒ **都失败**（= 确实被产品持有）；对照臂注册没人用的 `Ctrl+Shift+Y` ⇒ **True**。没有这第三条，上面那些"失败"可能只是恒真 |
| ③ 收尾干净 | 只杀本次起的 PID、释放占用、原件移回并核 sha1（`C76ABAC1…` 逐字相同）；产物只落仓库内目录 |

### 22.2 四腿读数

| 腿 | 读数 |
|---|---|
| **回退链** | 日志打了三条冲突：`Ctrl+Shift+S`、**`Ctrl+Shift+X`**、`Ctrl+Shift+Q` ⇒ 实际注册 **`Ctrl+Alt+S`**（捕获，第 3 档）与 **`Ctrl+Alt+Q`**（无感，第 1 档回退）。`Ctrl+Shift+X` **我没有抢** ⇒ 本机另有程序占着它 ⇒ 真实机器上这条链会连吃两档 |
| **按键送达** | `SendInput 6/6 个事件`；产品按 `#ohzq94` 一条链走完：`无感截图启动 → 会话启动 3840x2160 HDR → HDR 帧捕获成功 → PNG HDR 位深协商 8→10 → 编码设置 HDR=True/PqContainer 位深=10 色度=444 色域=BT2020 → 无感截图完成 (5256ms)` |
| **产物** | `tests/output/t3-cells/TrueToneCap_20260930_025449195.png` **4766.9 KB**、sha1 `85CFD72F…`；外部裁判 `ffprobe` 独立读回：`3840x2160 / pix_fmt=rgba64be / transfer=smpte2084 / primaries=bt2020` ⇒ 真 10-bit HDR PNG，声明与像素同向 |
| **剪贴板** | 完成行之后读到 **位图 3840x2160 `Format32bppRgb`** ✓（这就是截图工具最高频的那一步：按一个键 → 直接能粘进聊天框/画图） |
| **界面（回退后的键位可见性）** | **未判**：主窗里 2 个编辑框的 UIA `Value` 都不含 `+` —— 那一格在设置页里、且 WinUI 的 `PlaceholderText` 不进 Value ⇒ 这条腿要量得先切页或读控件文本，本轮没做 |

### 22.3 这一格抓到 / 落出的两件事

1. **产品码缺陷（已修 + 已锁）**：`冲突: … (err=0)` 这个 `err` **恒为 0**。`RegisterHotKey` 的
   `[LibraryImport("user32.dll")]` 没开 `SetLastError = true`，于是 `Marshal.GetLastWin32Error()`
   取不到本次调用的错误码。热键问题只有两个成因——被别的程序占用与参数不合法——而这两者的处置完全相反，
   日志把关键分叉印成 0 等于让它退化成靠猜。加上标志后本轮实测打出 **`err=1409`**（真码）。
   锁：`Test_HotkeyConflictLog_CarriesRealWin32Error`（两条，`--unit-tests` 956 → **958**）。
2. **口径缺口（未修，等一句）**：回退本身是善意，但**换掉的键位既不写回设置、也不上界面，只在日志里**。
   后果：用户按自己挑的那个键没反应，界面上看不出键位已经变了；而且下次启动仍然先试那个被占的键。
   三种处置都能自圆其说（把实际生效的键位写回设置 / 提示"`Ctrl+Shift+S` 被占用，现在用 `Ctrl+Alt+Q`" /
   保持现状但把这条日志升成 Toast）⇒ 属产品口径，等你定。

### 22.4 harness 自己犯的两条错（都记下来，因为它们都会造出假结论）

- `INPUT` 结构在 x64 上被我声明成 28 字节（Win32 要 40：type 4 + 填充 4 + 联合 32）⇒ `SendInput`
  返回 **0 个事件**，差点被读成"产品不响应热键"。修成 `Explicit + Size=40`，并让它打印
  "送出 N / 应送 M"——**送没送出去必须是读数，不能是假设**。
- 判"截图完成"我原本用的是"输出目录里出现文件"，而产品的原子写是 `.名字.<guid>.tmp` 先出现 ⇒
  我按那个 0 字节 `.tmp` 就杀了进程，剪贴板那条腿变成"我读早了"（第三跑的 `NEW=1` 其实是半截产物）。
  修：等它自报 `无感截图完成 / 失败 / 取消` 那一条日志再读副作用，并单独点名未改名的 `.tmp`。
  这正是本仓早就写着的那条规矩——**读副作用要等被测自己报完成，固定睡/固定条件=赌**——我自己又犯了一遍。

---

## §25 覆盖层再入风暴：根因、连带挖出的两处、以及"新锁必须咬"的实测（2026-09-30，用户："现在跑吧"）

### 25.1 根因只有一句：闩锁写在了底部

`_bgRendered = true` 原先在 `Activated` 回调的**最底下**，而回调中间那一段
（`CreateWindowExW` + GPU 呈现）会跑**嵌套消息泵** ⇒ `Activated` 在同一次调用里被再次派发。
每一层进来时闩锁仍是 false ⇒ **每一层都建一个全屏背景窗**；最深那层置位后往外退，
其余各层在底部看见 true，于是又把 CPU 兜底与窗口检测一起跳过。
⇒ §23.3 那三个互相矛盾的数字（就绪 30 / 背景渲染 1 / EnumWindows 36 个可见窗）
在"递归"这一个解释下同时成立，不需要第二个假设。

修法两笔（都在 `SelectionOverlay.xaml.cs`）：

1. 闩锁提到**入口**（紧接「进入背景创建」那条日志之后）；
2. 底部那一整块从 `if (!_bgRendered) { _bgRendered = true; … }` 改成**无条件块**。
   第 2 笔不是可选的：原判断既是防再入的唯一保险，同时也是"CPU 兜底 + 窗口检测"的开关，
   只做第 1 笔就等于把兜底静默关死——比风暴更糟。

顺带定一条口径：**GPU 失败后不在下次 `Activated` 重试**（同一次调用末尾的 CPU 兜底一定把背景画好）。
"每次激活都重试"正是这次风暴的形状，不是保守。

### 25.2 复跑读数（每行自带条件；来源都指得到日志）

| 会话号（时间区间） | 当时的二进制 | 进入背景创建 | 覆盖层就绪 | 背景渲染 | GPU 呈现 NRE | EnumWindows 数到的可见 `HDR Preview` |
|---|---|---|---|---|---|---|
| `#qqvjc7` 04:03:35-38 | 打点之前 | 无此行 | 无此行 | 1（GPU=False） | 29 | 未数（该轮没有窗清单） |
| `#qqwrtg` 04:42:46-50 | 打点后、修闩锁前 | **30** | **30** | 1（GPU=False） | **29** | **36** |
| `#liv5ua` 04:48:36-38 | 闩锁修复后 | 1 | 1 | 1（GPU=True） | 0 | 1 |
| `#dpjh11` 04:56:12-13 | 再叠加"呈现结果被消费"后 | 1 | 1 | 1（GPU=True） | 0 | 1 |

- 计数来源：`log/app_2026-09-30.log` 按 `#会话号` 归因（`LogService` 的 opId，一轮捕获一个号），
  窗口清单来源 `tests/output/overlay-visual-cell.log`（`#liv5ua`）与 `overlay-visual-cell-2.log`（`#dpjh11`）。
- 两轮 harness 收尾都自证 `还原后 sha1=C76ABAC1… 与轮前逐字相同`；`RC=0`。
- **没对上的一笔**：风暴那轮 30 条「进入背景创建」对 36 个可见窗，差的 6 个我**没能归因**
  （同 PID、日志窗口覆盖整个进程生命期）。不猜：要么另有建窗点，要么计数被消息泵吃掉过一段。
- 两条腿各有真读数了：GPU 腿（`#liv5ua`/`#dpjh11`，`GPU背景=True`）、CPU 兜底腿
  （03:56 / 04:03 / 04:42 三次 `背景渲染 19-30ms (GPU=False)`）。

### 25.3 连带挖出的第二条：GPU 背景可以"报成功却一帧没出"

回读 `HDRPreviewWindow.PresentFrame` 时发现它是 `void`，内部四处 bail 全静默：

- `PresentFrame:261` `_disposed || _swapChain/_context/_device/_backBuffer 为空` ⇒ 裸 `return;`
- `PresentFrameCore:306` `Map` 取到空指针 ⇒ 裸 `return;`
- 覆盖层那侧 `Task.Run` 的两个分支都不进时（HDR 像素与桌面像素都为空）照样 `return true;`

⇒ `presentOk` 为真 ⇒ `GPU背景=True` ⇒ **`if (!gpuBgOk && …)` 的 CPU 兜底被关掉**，
而透明覆盖层下面那扇窗从没被呈现过。这正是 §23.6 表里 A 那一格的成立条件，
当时我只写成"孤儿窗叠上去"，漏了更直接的一条：**根本没有第二个窗，只有一扇什么都没画的窗**。

改法：`PresentFrame` / `PresentFrameCore` / 私有 `PresentFrame()` 全部改成返回"这一帧到底出没出去"，
`Dispose` 后的 bail 补一条留痕日志（`跳过呈现，一帧未出: disposed=… swapChain=… …`），
调用方两条腿都判返回值、无像素那一路改成 `return false`。全仓只有这两处调用点（已 grep 确认）。

**但我第一版这一改是空转的，这里把假陈述写下来。** 调用方那一行我写成了

```csharp
presentOk = presentTask.Wait(3000);      // ← Task.Wait(int) 返回的是"等没等完"，不是任务结果
```

`Wait(int)` 的布尔值是"这次等待有没有等到"，任务的 `Result` 从没被读过 ⇒ 闭包里两处 `return false`
全被丢在 Wait 边界上，bail 仍然表现为"任务正常完成" ⇒ `GPU背景=True` 照样关掉 CPU 兜底。
也就是说 §25.5 那条"两条呈现腿都要判返回值（实得 2/2）"绿着，而行为一个字都没变——
**形状对了、值没传上来，正是源码哨兵最容易供出假绿的那一格。**
（本仓早就有同族的锁：`NativeJxlEncoder` / `JpegLiNative` / `NativeWebPEncoder` 都有
"没有丢弃返回值的 `WaitForExit`"这条断言——我把同一个错在 `Task` 上又犯了一遍。）

现在改成 `presentOk = presentTask.Wait(3000) && presentTask.Result;`（短路顺序是有意的：
超时那一支绝不读 `Result`，否则 UI 线程会被挂回无限等），并补两条锁钉住这个形状：
"presentOk 不许直接等于 Wait 的返回值" + "结果必须被真读一次"。变异 M3 实测：把那一行退回旧写法
⇒ **正好这两条红**（`passed=989 failed=2`），其余断言不受影响；还原后 sha1 逐字相同。
修完仍在真机复跑一次确认健康路没被改坏（`#y8hg53` 06:32：`GPU背景=True`、`背景渲染 0ms`、
`GPU 背景窗口 (SDR→scRGB) 已创建` ⇒ 结果读上来了、成功路仍然是成功）。

### 25.4 第三条是我自己这一轮引入的（超时腿 use-after-dispose）

§23.2 第 3 笔（"失败即 Dispose"）修得太快：`Wait(3000)` 返回 false 的**超时腿**里，
后台线程**可能还在** `PresentFrame` 中间，UI 线程当场 `bgWnd.Dispose()` 就是
对一条正在呈现的交换链做 use-after-dispose（`Dispose` 会 `DestroyWindow` + 释放交换链）。
后果不是黑屏，是把进程带走——比留一个黑窗严重一个量级。

现在按腿分开：抛出腿 = 任务已落地（`Wait` 是把异常取回来才抛的）⇒ 当场 Dispose；
超时腿 = 交给 `ContinueWith`，等它真落地再 Dispose。

**未验**：本机这次跑不出超时腿（GPU 呈现稳定 <250 ms），所以这一支只有代码依据与
判据锁，没有现场读数。要量得在呈现里注入等待（会改产品码），不划算，留作台账。

### 25.5 判据与变异实测（两条锁 / 11 条断言）

- `Test_OverlayReentryLatch_SitsBeforeWindowCreation`：钉的是**顺序**不变量
  ——`_bgRendered = true` 的下标必须小于 `new HdrPreviewWindow(` 的下标（实得 6469 < 6769）；
  外加"全文件只许写一次 true""不许出现 `_bgRendered = false`""兜底块不许再被 `if (!_bgRendered)` 包着"
  "入口早退守卫只许一处"。锚点前一律先剥整行注释（本仓老规矩，否则注释里的字面量会把断言喂饱）。
- `Test_GpuPresentOutcome_IsConsumedByCaller`：钉 `public unsafe bool PresentFrame(` 的形状、
  两条呈现腿都判返回值（实得 2/2）、留痕日志字符串在位、抛/超时两腿的 Dispose 时机分开写
  （`if (presentThrew)` 后面 120 字符内必须有 `bgWnd.Dispose();`，且出现 `ContinueWith`）。

变异实测（都是**只改文本、不重编译**——源码哨兵读的是盘上文本，所以每次约 40 s）：

| 变异 | 期望 | 实得 |
|---|---|---|
| M1：把 `_bgRendered = true;` 搬到 `new HdrPreviewWindow(…)` 之后 | 只有"顺序"那条红 | `❌ 闩锁必须【先于】背景窗分配（实得 latch=6818 < alloc=6737）`，`passed=967 failed=1` ⇒ **命中正确规则** |
| M2：`public unsafe bool PresentFrame` 改回 `void` | 只有"结果被消费"那条红 | `❌ PresentFrame 必须把『这一帧出没出去』交回去`，`failed=1` ⇒ 命中 |
| M2 第一发（无效变异，教训）：在留痕日志前加一个 `Z` | —— | 断言照绿。**不是锁没牙，是变异无效**：`Contains` 里 `"Z跳过…"` 仍然含原串。改成整条删除才有牙 |
| M2b：把 `LogService.Warn(… 跳过呈现，一帧未出 …)` 整条删掉 | "bail 要留痕"那条红 | `❌ Dispose 了的窗口不能静默返回：bail 要留痕`，`failed=1` ⇒ 命中 |
| M3（⑤ 之后补）：把 `presentOk = Wait(3000) && Result` 退回 `presentOk = Wait(3000)` | 只有钉消费点的两条红 | `❌ presentOk 不许直接等于 Wait 的返回值` + `❌ 结果必须被真读一次`，`passed=989 failed=2` ⇒ 命中且不串味 |

三次红各归各的规则，没有串味；两文件还原后 sha1 与轮前逐字相同（`442371c75292` / `433fd63a3a72`），
变异夹具已从 `qoder-scratch` 清掉。

### 25.6 有一把我自己读过的数是废的，得撤

`overlay-visual-cell.log` 里那行 `z=1 [HDR Preview] 2560x1440 近黑 100% 均值亮度 0`
——我一度把它当成"背景窗是黑的"的证据。**它不能这么读**：那扇窗建的时候带
`WS_EX_NOREDIRECTIONBITMAP`（`HDRPreviewWindow.cs:165`），压根没有 DWM 重定向位图可抄，
`PrintWindow` 对直接合成的交换链内容本来就拿不到 ⇒ 全黑是这个抓取方式的必然结果，与画面无关。
第二跑更把这条钉死：同一个覆盖层，第一跑抓到 `近黑 0% / 均值 228.2`，第二跑直接"抓不到客户区"
⇒ 这条像素判据在两次同码运行之间就不稳定，**不能进结论**。
本轮的结论只建立在两样东西上：`#会话号` 归因后的日志计数 + EnumWindows 的窗口清单。

### 25.7 §23.6 那张表的终判

| 机制 | 判定 | 依据 |
|---|---|---|
| A 背景窗/透明覆盖层的供帧不确定 | **成立，且比原先写的更直接**：不必叠 36 个窗，一扇"报成功却没画"的窗就够 | §25.1 读数 + §25.3 代码依据 |
| B GPU 路与 CPU 路色管线不同 ⇒ 观感整体位移 | **否**（量死了）：满量程最大差 0.037 个 8-bit 码、0.028 nits | §24 的 `--scrgb-compare` + 独立 IEC 参照实现 |
| C `DesktopImage Stretch="Fill"` 拉伸 | 本机单屏不成立，只算通用风险 | 不动，留在原格 |

也就是说：用户看到的"古老界面"= A（供帧者不确定 / 没画），不是 B（颜色变了）。
上一轮我先给的是 B、被一句"GPU 超时为什么会让整体风格变"问回去之后才量死——
**方向是用户纠正的，不是我自己走到的**，这句要留在账上。

### 25.8 这一轮的取证脚本本身不在版本控制里（要一句口径）

`.gitignore:99` 那条规则是 `tests/manual/*`，反选只放开了 `publish-probes/`、`cse-probe/` 和几个
具名脚本（`git ls-files tests/manual` = 24 个）。**`tests/manual/safety/` 不在反选里** ⇒
那一整个目录（现存 17 个 `.ps1`）全是 **ignored、未跟踪**，其中含本轮的 7 个 T3 判据驱动
（`overlay-visual-cell.ps1` / `overlay-geometry-cell.ps1` / `hotkey-clipboard-cell.ps1` /
`native-busy-cell.ps1` / `readonly-settings-cell.ps1` / `legacy-migrate-cell.ps1` / `en-ui-count.ps1`）。
后果不是"少个文件"：报告里每一条 T3 读数都要靠这些脚本才能复现，新克隆下来的机器既跑不了也验不了。
`.gitignore` 自己在 2026-09-26 的注释里已经认过这条理——"忽略掉的取证脚本对新克隆既跑不了也验不了，等于没有"。

我先把它们另存了一份（155 个文件 + sha256 清单）在 `C:\PLAN\qoder-scratch\uncommitted-20260930\ignored-harness\`，
连同本轮全部未提交改动（`git diff HEAD` 反向 `--check` 已通过 ⇒ 可正向还原）与 5 个未跟踪件。
**要不要加 `!tests/manual/safety/` 是仓库口径，等一句**，我不动 `.gitignore`。

---

### 25.9 "是否彻底解决"——分开答：证到的、构造保证的、没归因的

**① 已证：闩锁是承重的，不是"这次恰好没坏"。**
修复后的两跑里，嵌套派发**仍然在发生**，只是被入口闩锁挡成一次无害早退——
时间戳顺序自己说话（同一 UI 线程，行有序）：

| 跑 | 外层进入 | 嵌套派发早退 | 外层下一行日志（在 `Initialize` 内） |
|---|---|---|---|
| `#liv5ua` 04:48 | 38.400（`进入背景创建`） | **38.416（`Activated #2 … 背景已就绪 ⇒ 早退`）** | 38.433 `VRR 支持`（`HDRPreviewWindow.cs:184`） |
| `#dpjh11` 04:56 | 13.759 | **13.770** | 13.794 同上 |

⇒ 嵌套点被夹在 `进入背景创建` 与 `HDRPreviewWindow.cs:184` 之间，也就是**原生窗口创建那一段**
（`CreateWindowExW` 在 `:164`）在隐式泵消息；不是 GPU 呈现、也不是那些 NRE 造成的。
所以风暴的前提条件（呈现失败）本来就不是"会不会递归"的决定因素，递归一直在，只是修完以后每次都被挡掉。

**② 由构造保证；故障注入这一路已试，结论是"此路不通"（有读数，不是没做）。**
递归的**唯一入口条件**是"进来时闩锁为假"；现在任何一次真进入都在第一行之后立刻置位，
所以失败与否改变不了这个条件 ⇒ 结构上不可能再叠第二层。
**实测尝试**：给 `overlay-visual-cell.ps1` 加了 `-ContendGpu`——先按无感截图热键（本机实测 WGC 回读
522 ms > `PresentFrame` 里 `TryEnter` 的 500 ms），120 ms 后再按选区热键，想让覆盖层那一次呈现撞在锁上。
读数（`tests/output/overlay-failleg-cell2.log`）：**覆盖层压根没起来**——`截图已在进行中，忽略重复触发` 1 条、
`进入背景创建` 0 条、`GPU 锁超时` 0 条 ⇒ 该腿判【未判】(RC=2)，不记绿也不记红。
原因不是运气，是**结构**：产品把两条截图路径串行了（防重入闸在开覆盖层之前就把第二次按键整发丢掉），
所以"覆盖层呈现期间另有操作占着共享 D3D 锁"这个状态**无法从用户接口侧造出来**。
剩下唯一可行的办法是给产品加故障注入开关（往生产面加东西，不做）。
⇒ 这一腿维持"构造保证 + 顺序锁"，**不写成已验**；这一发误报另见 §28。


**③ 没归因的：36 个窗对 30 条进入，差的 6 个。**
排除掉了"还有第二个创建者"这一种可能：全仓 `new HdrPreviewWindow(` 只有 `SelectionOverlay.xaml.cs:214` 一处，
`"HDR Preview"` 这个标题也只出现在 `HDRPreviewWindow.cs:166`。⇒ 多出来的 6 个不是我漏数了创建路径，
而是当时那 30 层之外还有东西建了窗，**这一笔我认不下**，也不拿它当已解。

**④ 同源风险扫描：干净。**
全仓只有两处 `Activated +=`：修掉的这一处，和 `MainWindow.xaml.cs:2075` 的 `overlayReady.TrySetResult(true)`
——后者不建窗、不改状态，无同类风险。顺带量到一件小事：**`overlayReady` 从来没被 `await`**
（全文件只有声明、订阅、一条 1 s 兜底 `TrySetResult(false)` 三处），是个死握手；
清理不属本任务，我没动它，记在这里。

**⑤ 本小节写完又翻出来一条，它推翻了我上面的一处结论。**
§25.3 那笔"呈现结果被消费"的第一版是**空转**的：`presentOk = presentTask.Wait(3000)` 读的是
"等没等完"而不是任务结果 ⇒ bail 仍报成功，而 §25.5 那条"两条腿都判返回值 实得 2/2"照样绿。
已改 + 补两条锁钉住消费点 + 变异 M3 咬住（细节在 §25.3 末）。⇒ "没画却说画了"这半条通路，
到本轮末了才算真断。这笔是**内账**：源码哨兵钉的是形状，形状对而值不流动它不会替你说话；
以后"把返回值传上去"这一类改动，锁要钉到**谁读了它**，不是只钉"调用点判了"。

**判词（写下 ①-④ 之后 revised）：叠窗那一半已证不再复发（①②），"报成功却没画"那一半到 ⑤ 才修通；
现在两半都有能红的锁。** 还差的两笔没变：GPU 失败腿的故障注入实测（②）、那 6 个窗的归因（③）
——都不是再改一行代码能换来的，都在台账上。

补一笔时刻差，免得数字被横着比：第 3 跑（`#y8hg53`，06:32，带 ⑤ 的修复）EnumWindows 数到
**0 个**可见 `HDR Preview`——不是"降到了 0"，而是那一跑整条流程**已经收尾**（选区完成 → 覆盖层关闭
→ `_hdrBgWnd.Close()/Dispose()`），它证的是"没留窗"（清理路径顺带验到），
而 run1/run2 那个"1 个"是**飞行中**的计数。两个时刻，不可互换。
同一跑的健康路也没被 ⑤ 改坏：`GPU 背景窗口 (SDR→scRGB) 已创建` → `GPU背景=True` → `背景渲染 0ms (GPU=True)`。

---

## §26 日志查看器：我给的前提是错的，真的洞在另一处（2026-09-30，派 lane 挖"窗口归因"）

派活时我下的判断是"`LogParser` 按 `…Window.cs` 后缀认窗口，所以 `SelectionOverlay.xaml.cs` 归不进去"。
lane 通读 `LogParser.cs`/`MainWindow.xaml.cs`/`MainWindow.xaml` 后**否掉了这个前提**：全项目没有
任何按窗口后缀分类的代码。真正的机制是另外两条，都在解析器最上游，所以后果比"少一个徽标"大得多：

1. **调用者文件名只吃"一个点"**（**本轮 lane 的真贡献**）。旧判据 `[A-Za-z0-9_]+\.cs` ⇒
   XAML 代码后置（`SelectionOverlay.xaml.cs` / `MainWindow.xaml.cs`）与分部切片（`MainWindow.Settings.cs`）
   的 `Caller` 恒空，`文件.cs:行号` 整段粘在消息尾巴上。恰好覆盖层与主窗的行全在这一类里。
   放宽成 `[A-Za-z0-9_.]+\.cs`，并新增窗口归属与实例身份两组判据（`实例#N` / `Activated #N` / `第N次`），
   查看器加两枚徽标列、搜索面同时匹配窗口名与实例号。窗口形状的三条来源逐个核实过：`X.xaml.cs`、
   `XWindow.cs`（`HdrCaptureWindow` / `HDRPreviewWindow` 各自 `CreateWindowExW` 一个 HWND）、
   `MainWindow.<切片>.cs`；`App.xaml.cs` 是 Application 不是窗口 ⇒ 显式排除。
2. **`#opId` 让整行被判 null**——lane 把它当成自己的新发现报回来，**这是我 09-29 就修过的一条**
   （§10.3 V12：产品 2026-09-21 起给一次操作期间的行加 `#opId `，读方要求级别方括号后紧跟 `[`
   ⇒ 那批行整条判 null、静默消失）。修法（opId 段做成可选且被吃掉的非捕获组，分组编号不变）
   与锁（`scripts/probes/logline_format_check.ps1` = 门禁格子 `[1.96/2]`）都在昨天那一批里，一直没提交。
   **证据不是推理，是门禁日志自己印的**：`tests/output/round11-t3-gate.log`（01:31 那一轮）、
   `round14`、`round15` 三条里，`[1.96/2]` 现取的读方 pattern 已经带 `(?:#\S+\s+)?`，
   而被试臂"同一条行加 `#a1b2c3 ` 前缀（命中=True 期望=True）"✅——这些都早于 lane 写文件的 05:07:17。

> 这一条是本轮最该记的**流程账**：`git diff` 只比 HEAD 与现树，未提交的历史全被压成"lane 干的事"。
> 以后派活在产品码上的验收，判据要指到**门禁日志里印出的那份 pattern/读数**，不能只比 diff 的 `-` 侧。

独立复量（我不吃 lane 的总结，另拿 .NET `Regex` 抄判据在真日志上重跑，脚本
`C:\PLAN\qoder-scratch\winattrib-recheck.ps1`，只读、不落仓库）。
**"改动前"一栏一律取 HEAD 已提交版**，所以两行分属两批修复，不能横着加：

| 读数（`app_2026-09-30.log`，共 2120 行） | HEAD 版 | 现树 | 归哪一批 |
|---|---|---|---|
| 能被解析成日志头的行（头正则：旧 vs 新） | 1351 | **2097** | 09-29 的 opId 修复（+746 行此前在查看器里根本不存在） |
| 带 Caller 的行（**两侧都用新头正则**，隔离出调用者这一维） | 1358 | **2097** | 本轮 lane 的多点文件名修复（+739） |
| 能归到某个 UI 窗口的行 | 0 | **791** | 本轮新增的窗口归属判据 |
| 按窗口拆 | —— | SelectionOverlay 373 / HDRPreviewWindow 267 / MainWindow 109 / HdrCaptureWindow 42 | 同上 |
| 带实例身份（`实例#N` 或 `Activated #N`/`第N次`）的行 | 0 | **69** | 本轮新增的实例身份判据 |

lane 的临时探针（链真实 `LogParser.cs`，跑冻结副本）给出的数字与我这遍**逐格一致**
（1358→2097、791、373/267/109/42、69），两个独立实现对齐后才算量过。探针已删，仓库内没留副产物。

锁：`Test_LogViewer_WindowAndInstanceAttribution`（8 条窗口形状用例 + 真行行为 + 四条正则与生产
**逐字**比对）。Test 工程不引用 LogViewer ⇒ 这是**受控复制**而不是同源自证；漂移检查我做实了第二发：
把生产侧 `s_windowRegex` 的 `$` 去掉 ⇒ `❌ 窗口归属正则与镜像逐字一致`（`passed=988 failed=1`），
还原后回绿。**注意这条锁的覆盖面**：它钉的是 caller/window/instance/activation 四条，
**头正则（opId 那一维）不在里面**——那一维由门禁格子 `[1.96/2]` 钉着，两把锁各管一段，别以为一条就够。

边界要说清，免得这一节被读成"LogViewer 的问题解决了"：本轮补的是**日志行的窗口/实例归因**（看得见哪一行的来源），
§55 那条"**LogViewer 整工程没有本地化机制**"（界面串还是中文硬编码）**原样还开着**，两者不是同一件事。

### 26.1 lane 报回来的那条"门禁缺口"：数字我复量到了，机制两句是假的——撤回

lane 的总结里有一条"顺带发现"：说 `TrueToneCap.Test.exe --verify-ui-strings --check` 是现成的
App 侧界面串门禁、只是没接进 `verify.ps1`，并且引 `SelfTest.cs:270-271` 说 `OutputPath` 为空会整臂 Skip。
我按规矩回代码复查，**两处都不成立**：

| 它的陈述 | 我复查到的 | 判法 |
|---|---|---|
| 存在 `--verify-ui-strings --check` 这个模式 | Test 宿主的模式表是死列表（`Program.cs:43-51`：7 个串联套件 + `--all`/`--wgc-avif-tests`/`--scrgb-compare`/`--help`）；全仓搜 `verify-ui-strings` **只命中我自己刚写的这两份文档** | **不存在** ⇒ 撤回 |
| `SelfTest.cs:270-271` 读 `settings.OutputPath`，空则 Skip | `SelfTest.cs` 里 `OutputPath` 大小写不敏感 **0 命中**；270 行实际是格式交付形态表 `fmts`。它用的是命令行 `-OutputDir`→`outDir`，为空**回落到 `%TEMP%\TrueToneCap_SelfTest`**（`SelfTest.cs:72-83`） | 机制说反了 ⇒ 撤回 |
| 8 臂 RC=0 是"零判据的绿" | `ARMS=8` 这个数是真的，但它属于**我自己的探针** `scripts/probes/logline_format_check.ps1`，与被撤掉的那两条无干 | 结论不成立，读数本身留用 |

所以"App 侧现成门禁没接线"这条**不成立**，任务 #13 已按真实缺口改写。
真正还开着的仍是 §18 那条老的：**Test 工程不引用 App/LogViewer ⇒ App 侧运行时行为 T1=0**，
补它的现成通路只有产品进程内的 `TrueToneCap.exe --display-audit` / `--selftest` 那一类
（而且本仓已经知道 `Load()` 会重写 settings ⇒ 接之前必须先量 sha1）。

一句自己的账：这条我差点直接写进"待办"。lane 的**数字**我逐格复量过、确实对得上，
但**机制引证**两句全假 ⇒ 对 lane 的返回，数字要复量、机制要自己回代码读一遍，
尤其是它长得像"顺手就能加一条门禁"的那种好建议。








### 26.2 那句"没验：LogViewer 界面本身"——本轮补上了，而且第一版红是我自己的判法错

lane 交付时明说：绑定只被源码断言钉住，**界面本身没验**（WinUI 无头起不来）。
本轮用现成的进程外办法补掉：起 `TTCLogViewer.exe`、把真日志放进它自动扫描的 `<exeDir>\log`、
用 UIA 走可视树读数。harness `tests/manual/safety/logviewer-ui-cell.ps1`，
读数 `tests/output/logviewer-ui-cell.log`，截图 `tests/output/logviewer-ui-cell.png`。

**第一版三条判据全红（徽标 0 个 / 实例号 0 个 / 窗口种类 0 种），但那三红是 harness 的错，不是产品的：**
`ListView` 是虚拟化的——只有滚到眼前的行才有可视树元素。未过滤视图里我数到 630 个带 Name 的元素，
可它们对应的行是**文件开头那批（00:59 起）本来就不属于任何窗口的行**。
诊断块把这件事钉死了：`含 .cs: 的 Name = 88`（调用者列在渲染）而 `含 SelectionOverlay 的 Name = 0`
⇒ 不是"徽标没绑上"，是"眼前没有窗口行可渲染徽标"。
（上一拍还叠了第二个错：截图在过滤之后才拍，图里"0 条"被我当成"列表是空的"，白看一眼。）

改判法为**先用搜索面把视图收敛到目标行、再看徽标在不在**，读数（`RC=0`）：

| 判据 | 读数 | 判 |
|---|---|---|
| ① 渲染出来了 | 带 Name 元素 630 个（列表窗 `<WinUIDesktopWin32WindowClass>` 子树 640） | PASS |
| ⑤ 搜索面接的是这个视图 | 输入不存在的关键字 ⇒ 元素 630 → 14、条目数 0 条 | PASS |
| ⑥ 过滤 `SelectionOverlay` | 徽标行 121，其中 `🖥 SelectionOverlay` **91**，含 `实例#` **60** | PASS（= 旧判据②③） |
| ⑦ 对照臂：过滤 `MainWindow.xaml.cs` | 徽标行 74，**全部非覆盖层**，样本 `🖥 MainWindow` | PASS（徽标随数据而动，不是模板写死） |
| 收尾 | 停本次 PID、删自己放进去的那一份夹具、`<exeDir>\log` 复原到 0 件 | ✓ |

⇒ lane 那句"未验"现在翻成**已验且为正**；同时留一条方法论：**在虚拟化列表上"数不到"不能读成"没渲染"**，
判据要先把视图收敛到目标行，否则红的是夹具。中文/emoji 匹配串一律由码点拼（`[char]0xD83D + [char]0xDDA5`），
因为这些 `.ps1` 是无 BOM 的 UTF-8 而 PowerShell 5.1 按 ANSI 读文件——写字面量会假红。

---

## §28 顺手抓到的一条真缺陷：被防重入闸拦下的那次按键，被报成"截图失败"（2026-09-30，已修 + 已锁 + A/B）

发现方式很偶然——我在造 §25.9② 那个失败腿时，同一毫秒里看到两行：

```
06:57:17.104 [Warning] 截图已在进行中，忽略重复触发      MainWindow.xaml.cs:1864
06:57:17.109 [Info   ] 已提交系统通知: ❌ 截图失败       ToastService.cs:269
```

读代码确认（`MainWindow.xaml.cs:1861-1866`）：防重入闸拦下第二次按键时走的是
`ToastService.ShowCaptureFailed("上一次截图仍在处理中，请稍候")`，而该方法标题写死 `"❌ 截图失败"`。
⇒ 标题与正文互相打脸；用户视角更糟：他按了键、屏幕上什么都没发生，却收到一条**红色"截图失败"**。
这不是 §15 那一族"静默降级"，是它的反面——**误报失败**。

第二层顺带看清：`ShowCaptureFailed` 走的是老的 `Show(dict)`，**直发 Windows 系统通知**，
忽略用户设置的"通知位置"；同一个毛病 `ShowCaptureDegraded` 在 2026-09-16 就修过
（注释原话："原实现只发 Windows 系统通知，用户若在设置里选了四角位置，降级提示仍会忽略它"），
但成功/失败那两条没跟着改。**那两条我没动**（改它们会影响真失败时的通知去向，属产品口径），
只把"被拦下"这一支挪到统一入口：`ShowToast("⏳ 截图进行中", …, ToastTone.Info)`。

**A/B 是同一条构造跑出来的，不是推理**：修复前那一跑有 1 条 `❌ 截图失败`；
修复后重跑 `-ContendGpu` ⇒ `通知语义：防重入拦下 1 次，其间「❌ 截图失败」通知 0 条 ⇒ PASS（拦下≠失败）`。
锁：`Test_BusyRejection_DoesNotUseFailureToast`（3 条，钉**通道形状**不钉文案：
拦下后 400 字符内不许出现 `ShowCaptureFailed`，且必须走 `ShowToast(... ToastTone.Info)`）。
变异 M4 把那一行改回 `ShowCaptureFailed` ⇒ 正好这两条红（`passed=992 failed=2`），还原后 sha1 逐字相同。
`--unit-tests` 991 → **994**。

**还欠一笔（等口径）**：`ShowCaptureSuccess` / `ShowCaptureFailed` 仍走老的 `Show(dict)`，
即"成功/失败通知不受用户『通知位置』约束"。要么也并进 `ShowToast`，要么明确记一句
"这两类故意进通知中心"——现在是不一致且没留理由。

### 28.1 试过给"烘字盲区"补一条计数锁，量出来的单位没意义 ⇒ 撤了，不拿假数当判据

§23.1 / 交接 §55 那条最硬的本地化盲区是：独立捕获窗的文字是 `DrawTextGdi` / `ds.DrawText` 画进像素的
（`HdrCaptureWindow.cs:1597/1614/1709`），XAML 扫描、UIA、告警三条路全看不见它。
真修要动渲染层（产品口径），所以我先试的是**把盲区变成一个带基线的数**：数该文件里"画进位图的中文字面量"，
多一条就红、少一条也要显式改基线，让这笔债从此在 diff 里可见。

**试完撤了。** 用正则切字符串字面量，`--unit-tests` 实得 **98**，而我手算的基线是 **41**——
差的不是代码变了，是**我的数法在插值字符串上会碎**：预览里能看到 `)} 全屏={(_mpoFullsc…` 这种碎片，
说明 `$"…{expr}…"` 的洞被当成了串边界。⇒ 98 这个数**单位没有意义**，钉住它等于把"判据"变成"噪声计数器"，
以后谁看见这条红都无法判断是真多烘了一条中文还是我数错了。

处置：整条锁（注册 + 方法体）已删干净（`--unit-tests` 回到 **994 通过 / 0 失败**，文件里 0 处残留引用）。
这条盲区**维持未闭合**，并且把"要什么样的判据才够格"写清楚：需要一个**真正的字符串字面量切分器**
（能处理 `@""`、`$""`、插值洞、转义引号），或者让产品侧把画进位图的串集中到一个可枚举的表里再钉表
——后者同时就是真修的路。**没有能红的判据，就不假装有。**

---

## §27 App 侧进程内判据：成本量出来了，退出码却还不能用（2026-09-30，任务 #13 的前置量）

§18 记的那条老天花板是"Test 只引用 Core ⇒ App 侧运行时行为只能靠审计卡与 UIA 进"。
要判断"能不能把产品自带的进程内自检接进门禁"，缺的就是两笔数：贵不贵、会不会写盘。
新 harness：`tests/manual/safety/app-selftest-cost-cell.ps1`（读数落 `tests/output/app-selftest-cost-cell.log`），
三档各跑一次，退出码即该档判据，设置文件跑前取字节快照、跑后按字节还原并复算 sha1：

| 模式 | 退出码 | 墙钟 | 设置文件 | `--out` 目录新增 |
|---|---|---|---|---|
| `--settings-migration` | **0** | **672 ms** | sha1 `C76ABAC1…` 未动 | 1 件（progress.txt） |
| `--display-audit` | **0** | **1390 ms** | 未动 | 1 件（display-audit.txt） |
| `--selftest` | **1 ⇒ failed=2** | 1849 ms | 未动 | 4 件（PNG / TIFF / json / progress） |

两笔前置量的结论：**便宜**（最贵一档 1.8 s，而门禁 `[2/2]` 七套总共 42 s），且**这一轮三档都没重写设置**
——但"没重写"是这一轮的读数，不是性质：本仓已知 `SettingsService.Load()` 会回写
（App 侧 `--display-audit` 历史上就为这个先量哈希），所以接线时"快照 → 还原 → 复算"那一步必须留着。

**但 `--selftest` 的退出码现在不能当判据用**，原因不在产品：

- 四个外部编码器全都找不到（`avifenc` / `cwebp` / `cjxl` / `cjpegli`），因为**发布布局才带它们**：
  `Publish.ps1:661-682` 把 `Core/Resources` 里那四个 exe 复制进 `native/`；dev 构建输出目录下的
  `native/` 是**空目录**（实测 0 件），`TrueToneCap.App.csproj:162` 还会主动 `RemoveDir $(PublishDir)Native`。
- 同一根因（缺随包 exe）在这份自检里被记成**两种结论**：`JPEG_LI` / `JPEG_XL` 记 **skip**，
  `AVIF` / `WebP` 记 **fail** ⇒ "这个布局本来就没带工具"被算成两格产品失败，于是退出码 1。
  这正是本仓反复在防的那类：**缺席与失败必须分开计数**，否则读退出码的人分不出"产品坏了"与"这台机器没带件"。
- 接线前先定口径，两条都能自圆其说：① 后端缺失一律记 `missingBackend`（不绿不红，红绿只留给真判据），
  门禁要跑这一档就必须在**发布布局**里跑；② 门禁干脆只接 `--settings-migration`
  （672 ms、三臂纯逻辑、不依赖任何外部件）。**这是产品口径，等一句**——把 fail 改成 skip 属于放宽判据，
  没授权我不改分类语义。
- 值得接的理由也在这份读数里：`--selftest` 一跑就打出了 `NORMALIZE` 59 轴全符合、
  `SETTINGS_MIGRATION`（"门是活的，老用户的旧质量档会被交还默认一次"）、`HDR_JUDGE` 双向（该 FAIL 的 FAIL、
  该 PASS 的 PASS，且纸白≠80nits 时仍然有牙）、`HDR_SOURCE` 真机 4K/10bit/418nits 判 PASS——
  **这些正是门禁七套表现在完全看不到的那一半 App 码**，也就是 §18 那句"41% 零判据"的肉。

---

## §29 那五条"等一句口径"的处置（2026-09-30，用户："按你的想法完整推进"）

| # | 项 | 我的处置 | 理由（以及代价） |
|---|---|---|---|
| ① | `tests/manual/safety/` 被 `.gitignore:99` 挡在版本控制外 | **已改**：加 `!tests/manual/safety/` 反选 | 那 155 个脚本是本轮每条 T3 读数的唯一复现路径（30/1/36 三个数、LogViewer 徽标读数都靠它们）。`.gitignore` 自己 09-26 的注释已经认过这条理："忽略掉的取证脚本对新克隆既跑不了也验不了，等于没有"。代价：仓里多一批 `.ps1`，其中含两个 128 KB 的 `wip-*.patch`（下一步单独清出去）。 |
| ② | `--selftest` 把"缺随包件"记成产品失败 | **派 lane 修**：新增 `missingBackend` 第三类，不计入 `failed`；并把 `--settings-migration`（实测 672 ms、退出码即判据、不依赖外部件）接进门禁一格 | 缺席与失败混在一个计数里，退出码就不能用（今天 `failed=2` 全是布局没带 avifenc/cwebp/cjxl/cjpegli）。接 `--selftest` 本体要发布布局，不接。 |
| ③ | `ShowCaptureSuccess` / `ShowCaptureFailed` 的通知去向 | **一半判错，已被 lane 推翻**（见 §30.3）：失败那条确实与位置设置无关，成功那条其实上游就分叉 | **判为故意，不改行为**，但要在调用点把理由写死并加锁 | `ShowToast` 自己的文档写着："可追溯类别（无感截图完成 / 录制完成 / 失败）——即便用户设了四角位置也应进通知中心，便于事后回看"。所以这两类走系统通知是设计，不是漏改；**问题只在于这个设计没写在调用点**，读代码的人会当成 §28 那类遗漏。改成 Overlay 反而会让"截图失败"这种要事后查的提示消失。 |
| ④ | 防重入闸把第二次按键整发丢掉（也是 §25.9② 造不出锁竞争的原因） | **判为保持丢弃，不排队** | 排队意味着"用户几秒前按的那一下"会在稍后的屏幕上落一张他以为没发生的图——截图工具里这是更坏的行为。真正该修的是**它当时的反馈**，那已经是 §28（红色"截图失败"→ 蓝色"截图进行中"）。 |
| ⑤ | 烘字盲区（`HdrCaptureWindow` 画进像素的中文） | **派 lane 做真正的字面量切分器**，用它把盲区钉成有单位的数 | 上一版正则切插值串会碎（98 vs 41，单位无意义，已撤）。切分器本身要带对抗性夹具（注释里的串不能算、`'` 里含引号不能带偏、插值洞里的串只能算一次），否则又是"形状对、值不流动"。真修（把串换成走 `LocaleManager`）仍属产品口径，不在本轮。 |

**判据的通用一条**（今天第三次踩同一类）：能钉住的东西必须是**有单位的数**——
"98 个字符串片段"不判任何东西，"98 个字面量、其中含中文 41"才判；
同理 `if (!bgWnd.PresentFrame(...))` 是形状，`presentOk = Wait() && Result` 才是值。

---

## §30 三条 lane 并行落地 + 集成读数（2026-09-30，用户："子 agent 并行推进剩下的全部内容"）

三条线按文件切 lane、互不重叠（A=Test/CorePipelineTests + 新 Scanner；B=SelfTest.cs + verify.ps1；
C 在 `C:/PLAN/ttc-toast-lane` 隔离 worktree 里做，只交回 patch）。集成由主循环串行做：应用 patch →
全门禁 → 重生成地板 → 刷新未提交备份。

### 30.1 最终门禁

`tests/output/verify-round24.log` ⇒ 7 套件 **1600 断言 / 0 失败 / `GATE_EXIT=0`**，地板重生成到 1600
（上一版 1558 + A 的 38 + C 的 4，数目逐项对得上）。新格 **`[1.97/2]`** 已在门禁内：App 侧进程内跑
`--settings-migration`，判据=退出码，带设置 sha256 快照-还原、60 s 超时，**超时/exe 找不到一律记【没判】并判红**。

### 30.2 ② 缺席与失败分账（lane B）

`--selftest` 退出码 **1 → 0**，末行现在是
`[selftest] 分类: ok=7 skip=0 missingBackend=4 fail=0`——四个随包 exe 缺失被单列成 `missingBackend`，
不再冒充产品失败。判据没被放宽：临时让 WebP 抛 `IOException` ⇒ `fail=1`、退出码 **1**。
顺带修了一处旧账：汇总的 `total` 原先写死 `cases.Length + 4`（会少数），改为 `entries.Count`，
并加"分账不自洽就大声报"的守卫（`failCount != failed` 直接 ⚠）。
B 自证的三发门禁变异：失败臂 ⇒ `verify exit 1`；exe 路径过滤成空 ⇒ `❌ 未找到 TrueToneCap.exe … 这一格【没判】` + `exit 1`；
让 App 重写设置并睡过临时压到 2 s 的上限 ⇒ 先"已按原字节还原、与轮前逐字相同 ✓"，再 `❌ 2s 未判` + `exit 1`。

### 30.3 ⑤ 烘字盲区从此有了带单位的数（lane A），以及我自己那两个数哪个是真的

`src/TrueToneCap.Test/StringLiteralScanner.cs` 是手写的字面量切分器（普通串转义、`@""` 双引号、
`$""` 插值洞、`$@""`、字符字面量、`//` 与 `/* */` 与 `///`）；未闭合引号**不猜**：不出字面量、
位置记进 `UnterminatedAt`，哨兵断言它必须小于 0 ⇒ 文件被写坏时报红而不是悄悄少数。
基线：**`HdrCaptureWindow.cs` 含中文的字面量 98 条 / 全文件字面量 243 条**。

**这里必须更正我自己：先前那个"手算 41"才是假的。** 我上一轮是在 bash heredoc 里写那条正则的，
MSYS 会把成对的反斜杠折成单个，于是正则里那个"排除引号 / 反斜杠 / 换行"的字符类少了一项转义，
切出来的东西根本不是完整字面量——41 是这么来的。
lane 的 98 有两个独立实现背书（它的 C# 与它另写的 Python 同数），且我核过它**真的被调用**：
`CorePipelineTests.cs:144` 有 `StringLiteralScannerTests.RunAll(Assert);`，注释写着"先于用它的锁"——
不是"写好没接线、稳定产出全绿"那种死自检。它的三发变异也各有归属：插一条中文探针 ⇒ `98 → 99` 单条红；
再加一处 `ds.DrawText(LocaleManager.T(…))` ⇒ 2 红（计数 + 绘制腿锚点）；把切分器的注释跳过改坏 ⇒
**7 红**，其中复合夹具 13→15、注释夹具 1→4（证明负样本真的在跑）。被改的 App 文件 sha256
`9F7377360A2A…` 前后一致、无探针残留。

### 30.4 ③ 通知去向：lane 推翻了我的一半判断

我 §29③ 写"成功/失败两类都故意进通知中心"。**失败那条成立**：`ShowCaptureFailed` 的 11 个调用点
没有一个按 `ToastPosition` 分叉，确实与位置设置无关，符合 `ShowToast` 文档里那条"可追溯类即便用户选了
四角也应进通知中心"。**成功那条我说错了**：`ShowCaptureSuccess` 只有两个调用点，都在 `ShowSaveToast` 里，
而上游 `:1513/:1521/:1535` 已经按位置分叉——选了四角就走应用内浮层，`ShowCaptureSuccess` 只是那条
"浮层创建失败才回退系统通知"的兜底。所以它**不是**绕过设置，注释按实情写。
锁：`Toast_TraceabilityIntent()`（4 条，缺文件 `Assert(false)` 不 Skip；判路由形状用剥注释后的方法体，
判意图注释只看紧邻的 `///` 块），`--usability-tests` 210 → **214**。三发变异各有归属：删注释 ⇒ 2 红；
把降级那条的 `Overlay` 改成 `System` ⇒ 1 红并打印参数表；降级退回老的 `Show(dict)` ⇒ 2 红。

### 30.5 未验与后续

- **发布布局没跑**：`missingBackend` 归零这件事要在真带了 avifenc/cwebp/cjxl/cjpegli 的包里验，本轮没有。
- **AVIF 的口径**：缺 avifenc 时若硬件链可用会回退，B 选择读成 `missingBackend` 并在代码里注明——
  这是口径不是缺陷，但"硬件编出来了却以为软件件缺失"这种读法要有人知道。
- 三条 lane 的产物都在主树（C 是 `git apply` 进去的，worktree 已 `worktree remove` 干净）；
  未提交，HEAD 仍 `ca7ed99`；`tracked.patch` 已刷新且反向 `--check` 通过。

---

## §31 接手轮：§30.5 那条"未验"结清 + safety/ 入库边界补齐（2026-09-30，用户："其他 agent 有因为上下文未完成的部分，你接手任务继续推进"）

### 31.1 safety/ 的边界补齐（把 §29① 那句"下一步单独清出去"做完）

上一轮只把目录放开（`!tests/manual/safety/`），可放开的东西里混着两类**不是判据**的：harness 自己的
`--out` 落点，和一份 `restore/` 源文件快照。补一条 `tests/manual/safety/*/`（`.gitignore:215`）后边界是：

| 侧 | 内容 | 件数 |
|---|---|---|
| 纳入 | 20 `.ps1` + 3 `.py` + 2 `.json` 夹具（`fb-off`/`fb-corrupt`）+ 1 `.md` 变异计划 + 1 `.txt` 盘上残留基线 + 本轮新增那份 cell | **28** |
| 排除 | 25 个 `--out` 子目录（内含 44 件）/ 6 个 `wip-*.patch` | 50 |
| 合计 | 盘上 `tests/manual/safety/` 全部文件 | 78（28+44+6，逐类对得上） |

判据两向都量了（`git check-ignore -v`）：4 个负样本 `restore/FormatEncoders.cs`、
`audit-control/display-audit.txt`、`settings-backup/TrueToneCap.settings.json`、`wip-2026-09-29-e1.patch`
全部命中新规则；3 个正样本 `audit-case.ps1`、`fb-corrupt.json`、`preflight-orphans-2026-09-29.txt`
仍可纳入。`preflight-orphans-*.txt` 是 §523 那条"盘上残留基线"，批次 D/F 收尾要比差集 ⇒ 属证据件，留。

**为什么 `restore/` 尤其不能进**：不是因为它会撑爆新鲜度面（`[1.9/2]` 只枚举 `src/` 与 `tests/fixtures`，
摸不到 `tests/`），而是这份 09-29 的源文件快照**已经两次污染过检索结论的写法** —— 本报告 `:1302`
与 `:1458` 那句"全仓 grep 只有两处命中，另一处是 `safety/restore/`"就是它造成的。副本一旦进仓，
每一条"全仓只有 X 处"的陈述都要多带一句限定。

**没做的事**：`restore/`、6 个 `wip-*.patch`、25 个输出目录都还躺在盘上，只是不再会被纳入。
删它们是不可逆动作且不属于本轮判据，等一句话再动。

### 31.2 §30.5 第一条"未验"结清：三臂，同一份二进制，缺席→在位→只缺一枚

新 harness `tests/manual/safety/publishlayout-selftest-cell.ps1`，读数 `tests/output/publishlayout-selftest-cell.log`
（同一构造跑了 **3 次**，逐臂数字完全相同）。

| 臂 | `native/` 前置 | 六格里编码那四格 | 汇总（产品自报） | 逐格式行独立数 | 退出码 | 墙钟 |
|---|---|---|---|---|---|---|
| A | 空（dev 布局原状） | JPEG LI / JPEG XL / AVIF / WebP 全 `missingBackend` | `ok=7 skip=0 missingBackend=4 fail=0` | 4 | **0** | 1549 ms |
| B | 四枚齐全（模拟发布布局） | 四格全 `ok`（AVIF 323 ms / JXL 100 ms / LI 79 ms / WebP 79 ms） | `ok=11 skip=0 missingBackend=0 fail=0` | 0 | **0** | 1496 ms |
| C | **只有 `cwebp.exe`** | WebP `ok`，其余三格仍 `missingBackend` | `ok=8 skip=0 missingBackend=3 fail=0` | 3 | **0** | 1519 ms |

三件事各自被哪一票钉住：
* **归零成立**由 A→B 那一维结清（§30.5 第一条）：`missingBackend 4 → 0`、`ok 7 → 11`、`fail` 恒 0。
* **"按格式独立判定"而不是"目录非空即全绿"**由臂 C 结清 —— 这一臂是我自己加的对抗夹具：
  若分类走的是"native/ 有没有东西"这种粗判，C 会读成 `missingBackend=0`；实读 3，且 `WebP ok 122 ms`
  单独翻绿 ⇒ `BackendToolMissing` 确实逐格查 `JpegLiNative`/`NativeJxlEncoder`/`NativeAvifEncoder`/
  `NativeWebPEncoder` 四个探针（`SelfTest.cs:787-790`）。
* **缺席不许冒充失败**由臂 A 的退出码结清：dev 布局缺四枚，退出码仍是 0（§27 那格"退 1 = failed=2"
  的旧读数就此被同二进制的新读数取代）。

**这格证到哪为止，要说白**：所谓"发布布局"这里只复现了对判据起作用的那一条性质 ——
`<BaseDirectory>\native\` 里躺着那四枚 exe（`Publish.ps1` [3/6] 干的就是这件事）。
锚点是逐枚核过的：四枚在 `src/TrueToneCap.Core/Resources/` 与
`publish/TrueToneCap-v0.3.6-beta/native/` 里 **sha256 前 16 位逐字相同**
（`F8142CC0…`/`F89AF038…`/`B6AE9F8A…`/`6FCB8098…`，日志前 4 行）。
真·出包形态（单文件裁剪、依赖闭包、`data/Models`）本轮没跑 —— 出一次包要 3–5 分钟且会覆盖现有归档，
属远端/不可逆那一档，没授权不动。四枚探针各自只看自己的 exe（`JpegLiNative` 跑 `cjpegli.exe -h`，
不查 `jxl.dll`）这句话是回代码读的，`SelfTest.cs:547` 那行 `(jxl.dll)` 只是描述文案。

还原与副作用：`native/` 收尾件数回到轮前的 **0**（在 `finally` 里做，中途抛出也还原）；
设置三份（App 同目录 + `%LOCALAPPDATA%\TrueToneCap` 两份）当前与轮前不一致的 = **0 ⇒ 逐字相同**。

### 31.3 我这个 harness 自己有三处坏，都在日志里露了馅（先记账再交读数）

1. **判别句写了 `$(if (A) { '好' } { '坏' })`，少一个 `else`。** 第一版日志里四行锚点全都印成
   `逐字相同  '★ 不同：本脚本模拟的布局不等于发布布局…'` —— 也就是说那 four 句话**同时**说着两种结论。
   语义我没猜，落了一份探针（`C:\PLAN\qoder-scratch\psprobe\ifelse-probe.ps1`）实测：
   相等 ⇒ `NOELSE[SAME' }{ 'DIFF']`（两支文本都进串，还带引号碎渣），不等 ⇒ `NOELSE[ 'DIFF' ]`，
   带 `else` 的写法两支各自干净（`ELSE[SAME]` / `ELSE[DIFF]`）。**方向是假红不是假绿**，
   但一条恒带警报的判据一样是废读数 —— 已改成先算 `$mark` 变量再插值，并复跑（§31.2 那份日志是复跑后的）。
2. **按第 90 个字符切详情**，把异常文案切成 `JPEG_LI missingBackend 15ms.exe (Google jpegli)，请将…` 这种
   碎渣 —— 看着像被测量在胡说，其实是我在胡说。改成整段照录。
3. **留了一行空转的 `foreach ($f in $before) { }`**（我原本想显式过一遍，实际什么也没做），删掉。
   
⇒ 补一条自我约束：**凡"写进日志的判别句"，交读数之前要先拿一对反例跑一次**（这次那对反例就是
`ifelse-probe.ps1`，而它是在日志已经印出怪话之后才写的，属事后补救，不算事前防线）。

### 31.4 门禁

`tests/output/verify-round25.log` ⇒ 7 套件 **1600 断言 / 0 失败 / 1 跳过**、`GATE_EXIT=0`、
**无未判格子**（§30.1 那轮是基线来源 ⇒ 断言规模那格是自比；本轮用同一地板重跑，才有对照价值），
轮前/轮后设置指纹**逐字不变**。这轮跑在 `.gitignore` 两处改动之后 ⇒ 27→28 件从 ignored 变 trackable
没有动过任何一格读数（总数仍是 1600，与地板逐项相等）。

### 31.5 本轮没推进的（不是忘了）

* §17.6 台账 1–4 条里本轮结清第 3 条（见 §31.6），其余原样在：审计卡屏无关判红仍在逐屏循环里
  （本机 1 屏，影响为零）；harness 群仍各自硬编码 `C:\PLAN\TrueToneCap`（**我新写那份也照抄了这个惯例**，
  没顺手改成 `--repo` 参数）；§14.6 欠的两列一字未动。
  （**同日由 §32 结清第 1 条**；第 2、4 条仍原样。）
* §19 那张 D4 表**每格仍缺同一条腿**（目视/截图）：本轮三臂是日志与文件计数，不是画面。
* 未提交，HEAD 仍 `ca7ed99`。入库前扫了一遍凭据：28 件里 `apikey|appsecret|token|password|Bearer|sk-`
  的命中只有三处，且都是文本不是值 —— `audit-case.ps1:105` 的 `SECRETS={3}` 是**计数位**、
  `logviewer-ui-cell.ps1:27` 是故意造的假 token、`mutation-plan-*.md:15` 是变异条目描述。
  两份 `.json` 夹具内容就是 `{"SettingsRevision":2,"GainMapMerged":true,…}`，无键值。

### 31.6 结掉台账 §17.6 第 3 条：那条负锁**到底锁住了什么**，边界写在这里

锁在 `CorePipelineTests.cs:2177-2179`，两半：`FormatBench.cs` 里**不许出现** `PngFallbackEnabled` 这个字段名；
`SelfTest.cs` 里**不许出现** `PngFallbackEnabled = true` 这句字面赋值。它防的是 `:2171-2172` 记的那次实测事故
——把 `EncodingSettings` 初值定成 `true` 以后，外部裁判把"按预期失败"的 `c_avifsvt_444` 读成"竟然成功了"，
真因只是保底多写了一枚 `.png`。

**边界（回代码逐行读过，不是推测）**：审计卡在 `SelfTest.cs:344-350` 会造一份 `flipped = snap.Snapshot()`
并把 `flipped.PngFallbackEnabled = !snap.PngFallbackEnabled`（`:345`）取反。当盘上值是 `false` 时，
这个内存对象的布尔就是 **`true`** —— 而锁只禁 `= true` 那种**字面赋值**，`!snap.…` 的写法不撞它 ⇒ 锁照样绿。
所以这条负锁的真实保证是：**宿主不把 `true` 硬写进 `EncodingSettings`，也不读产物文件**；
它**不**保证"审计卡里不存在一个 `PngFallbackEnabled=true` 的设置对象"。
`flipped` 那份目前的去向只有 `probePipeline.BuildEncodingSettings(… flipped …)`（`:349-350`），
拿来比布尔落点、不进任何编码器 ⇒ 不落产物，风险为零。
**将来若有人把这份 `flipped` 快照拿去真编码**（或拿 `probeEsFlipped` 去写文件），`:2171` 那次"洗白"会原样回来，
而这条锁**不会红** —— 那句话就是本条要留在账上的东西：**判"这条锁还活着"不能靠它绿着，要看有没有人把
`flipped` 接进编码腿**。为什么这一轮不加一把锁钉住它：能钉的形状只有"`flipped` 出现在哪个调用里"，
而那种按出现次数/位置切的判据正是 §28.1 撤掉那把"计数锁"的同族（单位没意义，只会把判据变成噪声计数器）。

---

## §32 台账 §17.6 第 1 条结清：审计卡"与屏无关"那一段挪出逐屏循环（2026-09-30，用户："继续"）

### 32.1 病灶与分界

`SelfTest.RunDisplayAudit` 里那段落点探针（读设置 / 保底开关两个方向 / 增益档反向 / 同轴警告）与"哪块屏"无关，
却长在 `foreach (var d in displays)` 里面。两笔代价都只在多屏那天显现，所以本机（1 块活跃屏）读不出来：

1. 同一句分叉按屏数吹成 N 条 ⇒ 卡片末尾那句 `管线落点判红 N 条` 的 N **不是缺陷数**；
2. 段里的 `settingsSource.Load()` **不是纯读** —— 迁移门触发时它末尾 `Save()`、`MigratePlaintextApiKey` 还写
   SecretStore，而审计跑在单实例锁之前 ⇒ 每块屏重复一遍"可能改用户盘"的动作。

留下的只有确实屏相关的：那 4 条格式探针的期望值要按 `d.IsHdr` 降档（`hdrScreen ? p.WantHdr : false`）。
判红标签相应从 `{d.Name}·保底开关` 改成 `全局·保底开关`，卡片多出一个
`管线落点·全局(…整轮只跑一次):` 段，排在所有屏名之前。

### 32.2 新增 5 条锁（`Test_PngFallbackSwitch_Wired` 内，全走 `StripLineComments` 后取下标）

| 锁 | 钉什么 | 单位 |
|---|---|---|
| 两处锚点都还在 | `settingsSource.Load()` 与 `foreach (var d in displays)` 各自命中 | 下标 ≥0 |
| Load 先于逐屏循环 | 顺序不变量（同 §25.5 闩锁那把的写法） | `idxLoad < idxLoop` |
| 全文件只许一处 `.Load()` | 不许有人按屏再读一份 | 正则计数 == 1 |
| 全局那两条判红不许带屏名 | `全局·保底开关`/`全局·增益档反向` 必须在，`{d.Name}·…` 必须不在 | 4 个 Contains |
| 同轴警告恰好一处 | 见 32.5(b)：第一版漏挪的那句 | 正则计数 == 1 |

`--unit-tests` 1032 → **1037**（+5）。

### 32.3 三发变异，各按自己的规则归属

* **V1｜"两段并存"的中间态**（我先插全局段、还没删循环内那份）⇒ `只许一处 .Load()` 与
  `不许带屏名` **两条红**（`passed=1034 failed=2`），另 4 个**编译错误**是同一形状的另一半（重复声明）
  ⇒ 这一发只证"那两条有牙"，顺序锁在此态是绿的（第一处 Load 确实在循环之前）。
* **V2｜纯把 Load 搬回循环内**（单自变量，文本变异、不重建）⇒ **只有顺序锁红**，
  且把两个下标都印出来（`实得 13745 < 10317`）⇒ 这就是"顺序锁与计数锁各管一头"的实测版。
* **V3｜打断管线接线**（`CapturePipelineService.cs:231` 改成硬编码 `false`）⇒
  `AUDIT_EXIT=1`、卡片 `（★ 至少一个方向丢了：开关在管线上不生效）`、末尾 `管线落点判红 1 条`
  且点名 `全局·保底开关` ⇒ **"把段挪出去"没有把判断挪没：值仍然一路流到退出码**。
  这一发同时也让 `pipe.Contains("PngFallbackEnabled = s.PngFallbackEnabled")` 那条既有锁红 ⇒ 两条各归各。
* 还原都是逐字节的：`SelfTest.cs` 回到 `116a841598cbc9d424d0` / `9fc96d206793356adfd7`，
  `CapturePipelineService.cs` 回到 `8a8ec73bb39d035782b1`（脚本内 `assert h==期望` 才继续）。

### 32.4 审计卡改前/改后（同一台机器、同一份设置、`--out` 都指进仓内）

| 读数 | 改前 `audit-layout-before` | 改后 `audit-layout-final` |
|---|---|---|
| `AUDIT_EXIT` | **0** | **0** |
| `换格式保底（设置项 PngFallbackEnabled）` 出现次数 | 1（在屏名之下） | 1（在 `管线落点·全局` 段里，排在屏名之前） |
| 该行内容 | `= True ⇒ …= True；反向 False ⇒ False （两个方向都跟得上 ✓）` | 逐字相同 |
| 逐屏 4 条格式探针（`└ 差分(默认⇒你的)`） | 4 行 | **4 行**（没被挪走、没少） |
| 同轴警告那句 | 1 次（本机 1 屏） | 1 次（结构上不可能再按屏重复） |
| `RESTORED` / `PROC_WRITTEN` / `SECRETS` | True / False / 0 | True / False / 0 |

⇒ 这一格给的是"搬家没丢东西"的对照，不是"多屏下少 N-1 条"的实测——本机只有 1 块活跃屏，那一句只能靠结构 + 锁。

### 32.5 顺带抓到两处，都是"改了结构没重跑消费者"那一类

* **(a) `audit-case.ps1` 的固定 22 行窗口把逐屏落点段整块截掉了。** 它原来按"第一个 `管线落点` 命中 + 22 行"截卡片，
  全局段一挪到循环之前，第一个命中就变成全局那 4 行，窗口随后全落在显示器枚举区里 ⇒
  **逐屏那 4 条格式探针从证据日志里消失**，而我已经在拿这份日志当读数用了才发现。
  改成两段各截（全局段截到第一个空行；逐屏段按 `管线落点(真调` 的**每一处**命中截到本屏 `实得源/捕获`），
  并且任一段找不到就点名"这段没判"，不再静默给一段空白。复跑 `audit-layout-after2` 两段齐全。
* **(b) 第一版漏挪了"同轴警告"。** 循环里那份 `⚠ 你这份设置与默认值…` 我按锚点切区间时没切到（它在
  `var probes` 之后），于是卡片上同一句出现两次（两屏就是三次）。**是 (a) 修好之后的切片读出来的**，
  不是推出来的 ⇒ 补挪 + 钉成"恰好一处"（32.2 第 5 条），并用 V-extra（在循环里再插一份）实测该锁独红一条。
* ⇒ 记一条方法：**搬动产品输出结构之后，要重跑的是"读这份输出的下游"，不是只重跑门禁。**

### 32.6 门禁四轮 + 地板

`verify-round26.log` 1604（+4 锁，用旧地板 1600 跑 ⇒ 有对照价值）→ `round27-baseline` 重生成 1604 →
`round27` 用新地板复跑 1604 → 补刀 (b) 后 `round28` **1605** → `round29-baseline` 重生成
**1605 / unit 1037** → **`round30-final.log`：1605 断言 / 0 失败 / `GATE_EXIT=0` / 无未判格子 /
轮后设置指纹逐字不变**（`SETTINGS_DRIFT=0`）。最终验收轮是 round30。

### 32.7 没证到的

* **多屏实况**：本机 1 块活跃屏 ⇒ "N 屏吹成 N 条"与"Load 写盘重复 N 次"这两句靠代码结构 + 5 条锁立着，
  没有第二块屏的读数。
* 循环里其余按屏打印的段落没动（它们确实在用 `d`：色彩决定、交付形态、声明通道、实得源/内容实测）。
* `emitSamples` 那档（逐屏各落两枚真 PNG）仍在循环里 —— 那是屏相关的产物，本来就该按屏跑。
* 未提交，HEAD 仍 `ca7ed99`。一次性补丁脚本与两份还原副本落在 `tests/output/`（被忽略的目录）：
  `_patch_selftest_hoist.py`、`SelfTest.cs.bak-before-hoist`、`SelfTest.cs.NEW-hoisted{,2}`、
  `CapturePipelineService.cs.bak`。

---

## §33 批次 T3 最后一格结了：工具条几何；以及"废弃 UI 又出来了"的归因（2026-09-30，用户："2.可以开始"→中途"分析原因"）

### 33.1 这一格的读数（`tests/output/overlay-geometry-postfix.log`，`-ForceSdr`）

驱动是既有那份 `overlay-geometry-cell.ps1`（280 行，安全闸：没确认到全屏窗就绝不下左键 ⇒ 不会点到用户桌面）。
**跑在 §25 闩锁修复之后的当前二进制上**，两例这回都进了"选完"状态（上一版 03:53 两例都是 0 按钮）：

| 用例 | 拖拽 | 按钮 | 按钮组包围盒 | 不变量①完整在屏内 |
|---|---|---|---|---|
| A 左上→右下大框 | `(307,173)→(2112,1188)` | 6 | `1290,1421` 尺寸 `1320x108`（6 枚有尺寸） | **PASS** |
| B 贴着右下 | `(2688,1512)→(3725,2095)` | 6 | `3061,1457` 尺寸 `752x108`（**只有 4 枚有尺寸**） | **PASS** |

* **不变量②"两次位置必须不同"= PASS**（位移和 1807 px）⇒ "工具条跟随选区"不再是注释。
  这一条就是 §23.5 记的那半格欠账，本轮结清。
* 闩锁在飞行中确证：同一实例 `Activated #1 进入背景创建` → `#2…#5 背景已就绪 ⇒ 只重新置顶后返回`、
  `GPU背景=True`、`覆盖层就绪 实例#1 第1次进入`、背景窗 1 个（§25 那轮是 30/1/36）。
* 尺子的先天限制照旧写在读数里：本进程 DPI-unaware，UIA 给物理像素、`SetCursorPos` 按虚拟桌面归一，
  1.5x 缩放的 4K 屏上换算必有误差 ⇒ 只报不变量，不报"工具条离选区右下角几像素"。
* **没归因的一条，不猜**：B 那支 6 枚按钮里只有 4 枚量到尺寸 ⇒ 是"翻到左边后两枚被挤成零尺寸"
  还是 UIA 在我那个时刻没读到布局，要再看一次（或直接读产品自己的工具条构型）。
  ⇒ 这一格给的是"两条不变量 + 包围盒"，**完整构型仍欠**，别当量全了。
* 收尾：`已停止本次起的 PID`、设置 sha1 前后逐字相同、跑完盒里没有 `TrueToneCap*` 进程（`Get-Process` 实测为空）。

### 33.2 "被废弃的 UI 又出来了" —— 归因：是我这个驱动的开关造成的，不是产品退化

用户中途报告屏幕上出现旧界面。三条独立证据：

1. **产品自己认了**：`18:41:29.760 [Warning] #lasysq 诊断开关 TTC_FORCE_SDR=1: 强制走 SDR 预览路径
   MainWindow.xaml.cs:1967` —— opId `#lasysq` 正是我这轮日志里那一拍的 ID。
2. **代码里这开关只做一件事**：`:1964-1969` 读环境变量，唯一动作是 `hdrDesktopPixels = null` ⇒
   `hasHdr=false` ⇒ 落到 `:2069` 的 SDR 路径 ⇒ `new SelectionOverlay(...)`（`:2076`）。
3. **同一二进制的 A/B**：带开关 → 窗类 `WinUIDesktopWin32WindowClass`（SelectionOverlay）；
   **不带开关 → 窗类 `TTC_HC`（HdrCaptureWindow）**，驱动当场点名"这条路工具条几何不可判"
   （`overlay-geometry-noswitch.log`）。⇒ 默认路没变，不存在"旧界面顶掉新界面"。
   ⚠ 03:53 那份"不带开关也进 TTC_HC"是**旧二进制**的读数，本轮特意重跑一份同构建对照，没拿旧那份充数。

**为什么非开这个开关不可**：`HdrCaptureWindow` 那批字烘进 D3D 位图 ⇒ UIA 里 0 控件（§55 的烘字盲区），
工具条几何在那条路上**机制性不可判**；能量到按钮包围盒的只有 SelectionOverlay 这一条。

**顺带纠正一处定性**：`SelectionOverlay` **不是废弃代码**，它是 SDR 屏上的主路径 UI
（`:2069` 的注释就写着"SDR 路径：SelectionOverlay（WinUI 3）"）。本机分类=Hdr ⇒ 平时看不见它，所以"像旧版"。
真入口只有三个：①诊断开关；②HDR 窗初始化失败回退；③取不到 HDR 像素（Windows 里 HDR 关掉／屏不授权）。

### 33.3 由此挖出的一条口径缺口（本轮未动）

入口②那段 `:2059-2067`：HDR 窗 `Initialize` 失败 ⇒ 记一条 `LogService.Warn("… 回退 SDR：…")`
然后**直接落到旧界面**，界面上没有任何"你现在看的是 SDR 降级预览"的话。
按本仓既定口径（JPEG/PNG 保底那族"触发一定要告诉用户"，§52 就是照这条做的开关+告知），这里缺一句话。
**没修**：它要动 Toast 通道与文案，且与那 6 把"字面量必须在源码里"的镜像锁同族 ⇒ 属产品口径 + 一批改动，
等一句要不要做、按哪一档做（只提示 / 提示+可关 / 与保底同形）。
现状读数：**这条回退本机从未响过** —— 26/28/29/30 四份 app 日志里 `回退 SDR` 命中 **0 次**（逐份 `grep -ac`，不是推断）
⇒ 它是"没观测到的风险"，不是"正在发生的缺陷"。

---

## §34 选区预览统一到同一张窗：SDR 不再另开一套界面，且 SDR 内容要看得对（2026-09-30，用户："做一个完整规划，开始实现，确保SDR下仍然能查看正确的SDR内容"）

### 34.1 病根（§33.2 归因之后的结构结论）

`SelectionOverlay` 是 WinUI 窗，而它**没有任何让自己变透明的机制**（全 App 工程里 `SystemBackdrop`
只在 `SilentCaptureToast.xaml.cs:121` 出现过一次；XAML 里那张本该承载画面的 `SwapChainPanel HdrPanel`
**在代码里零引用**）。GPU 供帧用的是另一扇 `HDRPreviewWindow`，而被覆盖层自己 `HWND_TOPMOST` 压在下面
（`:184`、`:335` 两句注释自己写着"确保覆盖层始终在背景窗口之上"）。于是
`if (!gpuBgOk && …) RenderDesktopBackground(...)` 这一句把**唯一会画进可见表面的那段**跳过了
⇒ "GPU 成功"这一档真正画出来的是不透明页底色（维护者眼里的"全白"），**失败时才轮到那条能看的兜底**。

> **当日自纠（写 §35 时量的，本段原话说过头了）**：上面"**没有任何**让自己变透明的机制"不成立——
> 机制在代码里是**有**的：`SelectionOverlay.xaml.cs:341-342` 用 `MARGINS` 四边全 `-1` 调
> `DwmExtendFrameIntoClientArea`，注释就写着"让下层 HDR 独立窗口的 scRGB 内容可见"，
> 且 `SelectionOverlay.xaml:8` 的 `RootGrid Background="Transparent"`。我当时只扫了 `SystemBackdrop`
> 和 `AllowsTransparency` 两个词就下了"没有"。**准确的口径是：机制存在，但在 WinUI 3 岛窗上是否生效没有证据**，
> 而"全白"正是它没按预期生效的现场（生效应见桌面或黑，不该见白）。
> 由此 §35 里"B 档删了下层供帧窗零代价"的推论要降级为**待实验**（见 §35.2 第 3 条）。

### 34.2 改动（每处都给行位）

| 文件 | 改了什么 |
|---|---|
| `Core/PixelOps.cs` | 新增 `BgraToScrgbForDisplay(bgra,w,h,hdrOutputActive,sdrWhiteNits)` + 公开常数 `NominalSdrReferenceWhiteNits = 80`：HDR 输出档 RGB ×= 白点/80，**非 HDR 档不乘**；A 一律写 1；长度不足直接抛 |
| `MainWindow.xaml.cs:1971-2010` | 撤掉 `if (hasHdr)` 那道**换界面**的分叉：两档帧合成一份 `presentFrame`，预览窗只有一张；老的 `if (hdrDesktopPixels is null) return;`（会把 SDR 档吞成无声取消）一并删；两档帧都没有时留一条 Warn |
| `MainWindow.xaml.cs:2077-2091` | 预览窗建不起来才回落旧覆盖层，且**发一条可见 Toast**（`PreviewFallbackTitle/Hint`），结掉 §33.3 那条静默 |
| `SelectionOverlay.xaml.cs:358` | 兜底那侧改成**无条件**把像素画进自己的表面（注释写清"这张窗不透明，下面那扇窗本来就看不见"） |
| `WgcCaptureService.cs:1408` | 拼接侧那句 `sdrWhiteNits / 80f` 改成引用同一个常数 ⇒ "一条数学规则只有一处实现" |
| `SelfTest.cs`（全局落点段之后） | 审计卡新增一行 `呈现帧契约:` —— 用**现读的** HDR 输出状态与系统白点跑 256 级灰阶，打印缩放/码点 255/整条 max/A≠1 格数 |
| `LocaleManager.cs:151-155` | 三条新串（`SelectionPreview`/`PreviewFallbackTitle`/`PreviewFallbackHint`），状态提示不再由 .cs 直写中文 |

### 34.3 判据：T1 十八 + 形状锁七

* **T1（18 条，`Test_BgraToScrgbForDisplay_PresentationContract`）**：期望值取**独立实现**（IEC 61966-2-1
  的 sRGB EOTF 现算），不抄本文件 LUT；两档白点（HDR 档 255→1.5 / 非 HDR 档恰好 1.0）、非 HDR 档整帧 max≤1、
  HDR 档白必须 >1（等于旧函数即"这刀没生效"）、A 恒 1（夹具里 128 格 A=0）、`nits<=0` 回落不猜、
  源帧短一寸必抛 `ArgumentException`、单调性、以及与旧函数逐位不同。
* **形状锁（7 条，`Test_SdrPreview_RoutesThroughTheHdrWindow`）**：`if (hasHdr)` 不许回来；MainWindow 里
  不许再直接吃 `BgraToScrgbLinearFast`；`LoadFrame` 必须吃 `presentFrame`；旧覆盖层全文件只许 1 处创建点
  且必须排在 `if (initOk)` **之后**；回退必须发可见提示；参考白常数只许一处定义。
* `--unit-tests` 1037 → **1062**；门禁 1605 → **1630**（其余六档逐项未变 ⇒ 改道没有附带损伤那 13 行
  点名 `SelectionOverlay` 的既有判据）。**最终验收轮 `verify-round33-final.log`：1630 / 0 失败 /
  `GATE_EXIT=0` / 无未判格子 / 轮后设置指纹逐字不变**；地板重生成 1630（unit 1062）。

### 34.4 实机接线读数（无头，不占光标）

`--display-audit` 的 `RESULT=ok EXIT=0 RESTORED=True PROC_WRITTEN=False`，卡片那一行：

```
呈现帧契约: HDR输出=True 系统SDR白点=120nits 缩放=×1.5 码点255→1.5 整条max=1.5 A≠1的格数=0 （HDR 输出：白已抬到系统白点之上 ✓）
```

⇒ 这行证明的是**接线**（App 侧真把 `state.hdr` 与系统白点喂进了那个函数、alpha 不信源），
Test 工程那 18 条证明的是**算法**；两边合起来才叫这一格判过。

### 34.5 权威口径（为什么"ACM 开不开"不是这件事的门槛）

[DirectX Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)：
FP16 + `RGB_FULL_G10_NONE_P709` "is the same pixel format and color space used by the DWM"，且是
"the only option that works for **all types** of Advanced Color displays"；门槛是 **flip model**
（我们本来就是 `FlipSequential`）。而 ACM 管的是"要不要由 OS 把各 app 的内容转换到显示器色域"——
HDR 屏上那两级转换是 HDR 通路自带的，**只有 SDR 屏**才需要厂商 provisioning + 那个开关。
但"被接受"≠"语义一样"，两条硬约束直接落进上面的实现：非 Advanced Color 屏上
"everything outside of the [0, 1] numeric range **is clipped**" ⇒ 所以乘白点只许在 HDR 那一档做；
以及 "if your app performs its own composition of SDR and HDR content into a single surface,
then **you're responsible** for performing the SDR reference white level adjustment yourself"。

### 34.6 这一格证到哪为止（必须说白的四处）

1. **没有视觉确认**。屏幕上到底对不对，仪器只有你那台眼睛；T1 与契约行都是数值契约与接线，
   不等于"看着对"。改道后请按这条看一次：普通截图热键 → 应是平时那张 HDR 界面、桌面内容清楚、
   亮度与周围一致；`TTC_FORCE_SDR=1` 再走一次 → 同一张界面，只是源换成 SDR 帧。
2. **兜底那侧仍带旧缺陷**：`SelectionOverlay` 的 460 钳制（≈932 DIP 实测宽 ⇒ 贴右边缘必然溢出屏幕）
   只在"预览窗建不起来"时才碰得到；主路用的是按实测宽度钳的那份（`HdrCaptureWindow.cs:1059`）。
   这条兜底本机历史上 **0 次**触发，现在触发还会出 Toast，但缺陷没清。
3. **统一的代价**：UIA / 本地化闸门 / 降级告警三条检测路径原先只对 HDR 用户失明，现在对**全部**用户失明。
   §28.1 写过的真修（把画进位图的串集中成一张可枚举的表）还没做 ⇒ 建议作为下一批，别拖到删除覆盖层之后。
4. **一处推断未证实**：覆盖层"全白"里的"白"具体是不是不透明页底色，是 grep 缺失 + z-order 两句注释
   推出来的；这一批证据里唯一没被像素级证实的一环（改道之后它已不影响主路）。

### 34.7 待你定

① 要不要把 `SelectionOverlay` + `HDRPreviewWindow` 这条兜底整条删掉（删完上面第 2 条自然消失，但会撤掉
13 行既有判据里的一部分，且不可逆度最高）；② 第 3 条那张可枚举表什么时候做；③ 34.6-1 的那一眼。

### 34.8 不用眼睛的那一眼：改道与画面都量到了（新 harness `preview-content-cell.ps1` + `preview-content-teeth.ps1`）

维护者不想只靠肉眼，于是把这一格换成**两臂抓屏 + 三条阈值的正反控制**，全程只用键盘（开预览靠产品自己
注册的全局热键，键位从它日志那句 `已注册: …` 里读，不猜）。读数 `tests/output/preview-content-cell.log`：

| 臂 | 全屏窗窗类 | 不同颜色 | 纯白占比 | 全黑占比 | 判读 |
|---|---|---|---|---|---|
| A `TTC_FORCE_SDR=1`（新的 SDR 档） | **`TTC_HC`** | 434 | 14.11% | 0.14% | OK：有结构、非平白 |
| B 不带开关（HDR 档，正对照） | **`TTC_HC`** | 386 | 15.14% | 0% | OK：有结构、非平白 |

* **改道成立**：A 档今天改**前**两次实测都是 `WinUIDesktopWin32WindowClass`（§33.1 的 18:41 与 19:09 两份日志），
  现在与 B 档同为 `TTC_HC` ⇒ "SDR 另开一套界面"确实没了，而且这是同一份二进制的两臂对照。
* **"全白"这一族被否**：两臂都不是平白（颜色 434/386 种、白占 14–15%）。
  抓取之所以合法：`HdrCaptureWindow.cs:224` 的 `WS_EX = TOPMOST|TOOLWINDOW`，
  而 `:220` 那条 08-26 的记录写明**已移除 `NOREDIRECTIONBITMAP`**（当年正因为它有 DWM 才抓不到内容）
  ⇒ 这扇窗有 redirection surface，屏幕抓取对它有效（§25.6 那次失效的是另一扇窗，不是这张）。
* **阈值有牙，三张合成图各挡一头**（`preview-content-teeth.log`）：纯白图 ⇒ 判"复现平白一片"；
  纯黑图 ⇒ 判"仪器未判"；中灰 128 图 ⇒ 判"无结构"。三条都在期望处红 ⇒
  上面那两个"OK"不是恒绿。并且尺子抽成 `preview-stats-lib.ps1` 后**与单元内联那份逐值一致（2/2）**，
  对照不是自源自证。
* **本格答不了的问题，写在读数旁边**：亮度的**绝对值**。GDI 抓取在 HDR 输出上会做 HDR→SDR 转换并钳位，
  所以"白点是否真抬到 120 nits"只能由 §34.3 那 18 条（值域/两档白点 1.5 与 1.0）与 §34.4 的
  `呈现帧契约` 那行（现读 HDR 输出=True、白点=120、缩放 ×1.5、码点 255→1.5、A≠1=0）承担，不能由这张图说话。

**我自己这一节又犯的错（两条，都当场改了）**：
① 判决行 `⇒ … $(if ($okBoth) {'✓'} {'★ …'})` 又漏了 `else` ⇒ 好的一面也带着警报字样
（同一条坑今天第三次，说明"写日志前先算变量"还没变成肌肉记忆）；已改成先算 `$markBoth` 再插值，
并用重算脚本给出干净的句子。② 那三条 parse 自检**是假绿**：我把文件名写成 `'…safety$f.ps1'`（bash 里 `$f`
没展开），`Get-Content` 报错、`Tokenize` 收到 null，而我的打印仍然报 `PARSE_ERRORS=0`。
重做时改成**同时打印输入长度**（`inputLen=…`），这样"空输入"再也不能冒充"零错误"
——正是 `[守卫自己就是最大假绿源]` 那条：判据缺失只能记未判。

### 34.9 端到端：SDR 档选区→编码→落盘→读回声明，链路可用（新 harness `preview-save-e2e-cell.ps1`）

改道只证到"打开的是同一张窗、屏幕不是平的"不够——保存那条腿以前在 SDR 档走覆盖层的 `ActionCompleted`，
现在走 HDR 窗那一支（`hdrRegion=null ⇒ ComposeAnnotatedSdr ⇒ EncodeAndSaveAsync`），**换了喂法就得自己跑一遍**。
读数 `tests/output/preview-save-e2e-cell.log`（一次真实拖拽 + 一次 Enter；驱动有闸：没确认到本 PID 的
全屏窗就不下键；键位从产品日志 `已注册: Ctrl+Shift+S` 现读）：

| 问 | 读数 | 判据 |
|---|---|---|
| ① 文件落了吗、能解码吗 | `png 1500x900 rgba 243,254 B sha256=0CAE05DC…`，**ffprobe exit 0** | 外部裁判（`C:\PLAN\FFmpeg`），不用产品自带解码器自证 |
| ② 里面是画面吗 | 经 ffmpeg 换 8-bit 后：`1500x900`、颜色 **269 种**、纯白 **5%**、全黑 0.67% | 与 §34.8 同一把尺（含正反控制） |
| ③ 声明诚实吗 | 传输 `iec61966-2-1`、原色 `bt2020`、无 ICC 侧数据；产品自己那行是 `编码设置: 格式=PNG HDR=False/BlockedByDisplay 质量=100.0 位深=8 色度=444 色域=BT2020` | **与拍板的第二档"广色域→BT2020+SDR"逐字一致** ⇒ 不是"声明比交付强" |

* 链路上产品自己的三步都在日志里：`HDR 选区保存` → `开始截图流程: 1500x900 格式=PNG` →
  `编码完成: …png (237.6 KB)`，收尾 `窗口关闭 (win=3840x2160)`，产物删除、设置 sha256 与轮前逐字相同。
* **我自己的 ③ 判据第一版写宽了**：它把"原色=bt2020"也算作"声明成 HDR"，于是把合法的第二档报成 ★。
  改成 **HDR 档只取传输特性（PQ）**，原色作为事实单独印。这是 `[判据的取材范围必须等于它声称的那件事]`
  在本轮的第四次命中——第一次跑出的那个 ★ 是我的尺子宽，不是产品的错。
* 顺手把兜底那侧的钳制常数也修了（`SelectionOverlay.xaml.cs`）：写死的 **460×50** 换成
  `ToolbarWidthDip()/ToolbarHeightDip()`（强制 `Measure` 后取 `DesiredSize`，量不到时退让到实测地板
  932×72 ⇒ 方向是"宁可往左推，不许把按钮推出屏"）。新增 1 条锁（变异把 460 塞回一处 ⇒ 该锁独红，
  实得 `裸 460 = 1 处`），`--unit-tests` 1062 → **1063**，门禁 1630 → **1631**，
  **最终 `verify-round36-final.log`：1631 / 0 失败 / `GATE_EXIT=0` / 无未判格子 / 设置逐字不变**，地板 1631（unit 1063）。
* **一个漏项要记**：这次文本变异的还原是对称往返（两处 `assert count==1`）+ 锁再转绿 + 那行逐字回读，
  但我**没在变异前拍 sha256**，所以"与轮前逐字相同"这句在本格只能写成推断。
* **仍未覆盖的**：标注层非空时的那条合成腿（要再拖一次并画一笔）、OCR/翻译两腿的实际出字、
  以及"超纸白样本占比"这级内容范围核对（要 float 解码那条读法）。声明一致性本格的结论是
  **"传输=SDR + 原色=BT2020 + 位深 8"，与第二档策略一致**，不含对样本范围的独立复核。

### 34.11 复制这条腿：三次注入都判【未判】，但换来一条持久见证与一次基线抬升（并更正 §34.9 两处）

`preview-copy-leg-cell.ps1` 跑了三遍（23:10 / 23:13 / 23:16），**全部记未判，不记产品缺陷**：

* 前两遍连"选区成没成"都无从知道——`_selComplete = true`（`HdrCaptureWindow.cs:2123`）**一个字都不写**，
  于是"Ctrl+C 没反应"既可解释成"键没进来"也可解释成"压根没选中"。第二遍我加了判别臂
  （Ctrl+C 无反应就再按一发 Enter），两次都"两个键都没反应"。
* 补上见证后再跑一遍，模糊性当场消掉：`选区完成 1445x959 @(窗口内 1243,541)` ——
  **这不是我框的矩形**（按的是 (1000,700)→(2400,1500)，1.5x 物理后应约 2100x1200），
  说明起手那下被当成"单击选中悬停窗口"（`:2110` 那条 QQ 式吸附），随后两个键都没进那扇 tool window。
  ⇒ 两件事都出在我的驱动（手势保真度 + 焦点），**不是这条腿的结论**。
* 为什么不能宣布"复制不可用"：同形状的拖拽在 22:43 那一格成功过且三步日志齐全
  （`HDR 选区保存 → 开始截图流程: 1500x900 → 编码完成 (237.6 KB)`）⇒ 键盘入口在这条路上能落地，
  至少 Enter 实测可用。注入式拖拽本身不可靠 ⇒ 这一格改列进手测清单。

**这轮的持久产出是那行 Info**：`HdrCaptureWindow.cs:2128` 现在写 `选区完成 {W}x{H} @(窗口内 x,y)`，
注释写明它为什么存在——判据读不到自己的前提，就只能把结论盖在猜上（"再按一发 Enter 反推"是事后补救）。

**烘字基线 98 → 99**（`CorePipelineTests.cs:3816`）：这行 Info 是一个新的中文字面量，ratchet 当场咬住
（`verify-round37.log` 因此 `GATE_EXIT=1`）。归因先做再动基线：这把尺的单位本来就含日志串——
报错自己印的前六条里就有 `"CreateWindowExW 失败: {…}"`、`"MPO 检测失败"`、`"VRR 支持: ✓ 启用"`
⇒ 属基线注释写的解释①（数量 +1、单位没变），按"同一次改动里改基线并说明"办，**没有**给扫描器开例外。
这条红是好消息：它证明那把尺有牙。收口 `verify-round38-final.log`：**1631 / 0 失败 / `GATE_EXIT=0` / 无未判格子 / 设置逐字不变**。

**更正 §34.8/§34.9 两处说法**：
① "亮度绝对值本格量不了"这句**说过头了**——产品自己就有一条上传诊断：
   `UploadFrame 源中心行 R: min=0.0046 max=1.5000 avg=0.2504 (行1080)`（`HdrCaptureWindow.cs` 的既有 Info 行）。
   在改道后的 SDR 档里它读到的 **max 正好 1.5000 = 系统 SDR 白点(120nits)/80**、avg 0.25 而非 1.0
   ⇒ **×1.5 确实落在了真实桌面像素上**，这一半不再只靠合成夹具的 T1。
   再加上 `MainWindow` 自己那行 `预览帧来源=SDR(BGRA→scRGB) HDR输出=True SDR白点=120nits 呈现缩放=1.5`，
   呈现侧的接线与数值都有了产品自报的读数。
② "选区链路已可用"这句**要收窄**：能证的是 **保存(Save)这一支端到端可用**；
   复制(Copy)因驱动的注入不可靠而【未判】，标注/OCR/翻译三支同样在清单上。

**交给你手测的四条**（每步都有见证行可读，不必靠肉眼猜）：① 选区后 `Ctrl+C`
（应见 `选区完成 …` → `HDR 选区复制到剪贴板` → `编码完成`）；② 画一笔再保存（`ComposeAnnotatedSdr` 那条分支）；
③ OCR / 翻译实际出字；④ 多屏、纯 SDR 屏、ACM=off 三档（本机不可造）。

---

## §35 老管线能不能清理：边界、冗余度与三档代价（2026-09-30，用户："现在分析老旧管线是否可以清理之类的？"）

### 35.1 边界与体量（只数这次"选区预览"涉及的东西；动图录制那一族是 §54 拍板过的，不在议题里）

| 件 | 体量 | 谁还在用 | 状态 |
|---|---|---|---|
| `SelectionOverlay.xaml` + `.xaml.cs` | 177 + 1485 行 | **只有** `MainWindow.xaml.cs:2119` 一处构造，且排在 `if (initOk)` 之后（今天钉的那条锁正要求这点） | 兜底 |
| `Services/HDRPreviewWindow.cs`（类名 `HdrPreviewWindow`） | 613 行 | 只被覆盖层用：字段声明 `SelectionOverlay.xaml.cs:115`，创建/初始化 `:214/:217`，取句柄 `:222`，失败回收 `:315/:324-325/:331-332`，关闭 `:877-879`，抓区域排除 `:1310/:1344` | **已于 §39 删除**（删除前读的行号，保留作 diff 索引） |
| `AnnotationWindow.xaml.cs` | 882 行 | 只被"标注兜底"用（`MainWindow.xaml.cs:1676/2070`，注释自陈"窗口内联，兜底打开独立窗口"） | 兜底的兜底 |
| `Shaders/ToneMapping.hlsl` | cso 2492 B | 见下方**更正**：运行时消费链 09-16 起断了 | 死资产（链条见 §36.1） |

⇒ 三条老管线合计约 **3.2 k 行**，另有一条 shader 资产确定是死的。

### 35.2 冗余度：三条独立证据

1. **HDR 窗自己就有 CPU 合成回退**：`HdrCaptureWindow.cs:62/:362` 写着"GPU 合成着色器 + 常量缓冲
   （**失败自动回退 CPU CompositeUI**）"，还留了 `TTC_GPU_COMPOSITE=0` 强制走 CPU（`:385-389`）
   ⇒ "GPU 不通就换人画"在那张窗内部已经做过一次，外层再兜一张 WinUI 覆盖层是同一件事做第二遍。
2. **"兜底从未被走到"这句我写宽了——当日自纠**（补测于同一条命令树）：三条串在 **15 份** app 日志
   （09-15…09-30，逐份 `grep -ac` 相加 = **314 个会话**）里，`预览窗失败，回退` 命中 **0** 次；
   可分母一样薄：走到那一步之前的 `开始截图流程` 总共只命中 **1** 次（今天 19:19 那遍端到端），
   其余 14 份全为 0 ⇒ 这份语料里根本没有"轮到兜底出场"的会话。**缺席读数没有正例兜着，只能记"未测"**。
   另一条 `回退 SDR` 更不该列在这里：它出自 `WgcCaptureService.cs:1114` 的"HDR 会话尺寸不匹配"，
   量的是采集侧回落，与这条 UI 兜底无关——**已从本条证据里撤掉**。
   *（2026-10-01 §37 追记：入口补上之后，`预览窗失败，回退` 这条见证**第一次也是唯一一次**命中 1 次——
   但那一次是诊断开关主动造的，不是真实初始化失败 ⇒ "真实失败样本 n=0"这句仍然成立。）*
   *覆盖层内部*那层 GPU→CPU 回退还有一组读数，方向与上面相反：今天 `app_2026-09-30.log` 里
   `覆盖层就绪` 97 次 = `GPU背景=False` 90 + `True` 7，`回退 CPU 渲染` 87 次、`GPU 呈现抛出异常` 58 次。
   归因后是两件事：**90/87/58 全部来自 3 个会话**（每会话 30 次进入 = §23.3 那个重入风暴，今天已修），
   修完之后的 7 个会话是 **就绪 1 / True 1 / 兜底 0**。**但这 10 个会话跑的都不是今天改道后的二进制**：
   逐会话查 `预览帧来源=`（改道那笔新加的见证行）命中 **0**，而查 `覆盖层就绪 … 实例#` 能命中
   ⇒ 它们是"有闩锁修复、尚无改道"那一档二进制；改道之后覆盖层只在 `initOk==false` 的回落里才建，
   而全语料 `预览窗失败，回退` 0 命中 ⇒ **当前树里这扇覆盖层一次都没被激活过**。
   这条读数的用处因此收窄为：**`HdrPreviewWindow` 这个类本身（改道没动它）呈现是成功的**，不能读成"当前配置下它在上屏"。
3. **真正"死"的是显示机会，不是代码**（第二处措辞自纠）：`HdrPreviewWindow` 那条分支**画得出东西**——
   `PresentFrame` 返回 true，上条那 7 个会话如此。它上不了屏的原因是**今天自己造成的**：
   覆盖层那侧现在**无条件**把像素画进自己的表面（`SelectionOverlay.xaml.cs:369`，那块注释 `:356-362`），
   而那层 Image 铺满客户区、覆盖层又被 `SetWindowPos(HWND_TOPMOST)` 压在上头（`:336`）
   ⇒ **不管 DWM 那套透明机制生效与否，下层窗的成果现在都被自己人盖住了**。
   这里要撤回我此前说过头的一句："覆盖层没有任何透明机制"——机制是**有**的
   （`:341-342` 四边 `-1` 的 `DwmExtendFrameIntoClientArea`，注释写着"让下层 HDR 独立窗口的 scRGB 内容可见"；
   `SelectionOverlay.xaml:8` 的 `Background="Transparent"`），只是**它在 WinUI 3 岛窗上到底生效没有，本仓没有证据**
   （§25.6 那一格当时就写着"确实翻到上面过 = 没证"）。
   ⇒ B 档的代价口径应是：**删它 = 删一条一直在跑、今天起被自己盖住、且从没被证明上过屏的路径**；
   它把 BGRA 经 `PixelOps.BgraToScrgbLinearFast` 转线性 scRGB 走 FP16 交换链（DWM 色域感知那一层），
   而 CPU 画进页面这一侧没有这条链路——**只有"以后给覆盖层做透明、让下层窗供帧"这个选项会随它一起没了**。

### 35.3 删之前必须处理的账（逐条计数，别用"看起来干净"下结论）

* 点名 `SelectionOverlay` 的判据/测试行 **18**（其中 5 条是今天新写的，钉的正是"只剩一处创建点、
  且排在 initOk 之后"——真删兜底就得连它们一起撤，不能留着空转）；点名 `HDRPreviewWindow` **2** 行
  （`Test_GpuPresentOutcome_IsConsumedByCaller` 的取文件 + Skip 分支）；点名 `HdrCaptureWindow` 7 行（**不动**）。
* `[1.9/2]` 的输入面按目录递归收 `*.cs`/`*.hlsl` ⇒ 删文件不会造出假绿；但 csproj `CompileShaders`
  （`TrueToneCap.App.csproj:88`）里的 `ToneMapping.hlsl` 要一起摘，否则它继续产 cso。
* `ShaderCompiler.cs:22` 那张表写着 `ToneMapping.hlsl ✅ 存在，运行时消费`——**这句现在是假的**，
  启动日志还在打 `ToneMapping.hlsl.cso 已存在 (2492 bytes)`。要么删资产、要么改口径，都要留痕。
* `LocaleManager.ZhToEn`（表内 191 条）里有 **13 条**只属于覆盖层界面（`保存截图到文件并复制 (Enter)`、
  `取消选区 (Esc)`、`复制图像到剪贴板`、`完成标注 (Esc)`、`识别图中文字 (OCR)`、工具条的
  `文字 (T)/矩形 (R)/椭圆 (E)/箭头 (A)/画笔 (P)/马赛克 (M)`、`标注选区（矩形/箭头/马赛克等）`、提示行
  `单击 = 全屏`/`拖拽 = 框选`/`…`）。
  **⚠ 这一格第一版是我手点的 10 条，已由判据实测改成 13**（漏了 `…` 与两条标注态标签，见 §38.3）。
  现有闸门只查"界面有、表里缺"，**不查反向的孤儿** ⇒ 删界面不清表就留 13 条死键且不会红。
  **这个洞已在 §38 补上**（反向孤儿判据 + 夹具④ 就是这个数字的出处）。
* LogViewer：`LogParser.cs:136/158` 的注释与窗口归属含 `SelectionOverlay`（§26 量的"能归到窗口 791 行"
  里覆盖层占 373）⇒ 删它要一起改归属映射与那条锁的样本。
* 点名 `ToneMapping` 的判据要一起处理（这条我第一版漏了）：`ServiceTests.cs:106/124/125/153/154/167`
  六处拿 `ToneMapping.hlsl.cso` 当 `ShaderLoader` 的样本文件（**读的是 cso，不是那条死掉的 PS**），
  `CorePipelineTests.cs:2192 Test_Hlsl_ToneMapping_MatchesCpu` 又是**源码级**比对该 .hlsl 曲线与 CPU 实现。
  ⇒ A 档"判据代价 0"是错的：直接删源文件会 Skip 那一族（53 条源码哨兵本来就"缺文件即 Skip = 集体失明"），
  必须先把样本换成仍在使用的 `OverlayComposite.hlsl` / `FullscreenVS.hlsl`，或明确撤掉这几条。
* 失去对象的取证驱动 **2** 支（第一版写 4 支是数的）：`overlay-geometry-cell.ps1`（19 处点名）、
  `overlay-visual-cell.ps1`（16 处）。`preview-content-cell.ps1` 与 `audit-case.ps1` 里
  `SelectionOverlay|覆盖层|Overlay` 命中 **0** ⇒ 它们不是这一档的对象，从清单里撤掉。
  工具条几何那一格已由"按实测宽度钳 + 锁"接住，且活路径本来就是算出来的
  （`HdrCaptureWindow.cs:1620 GetToolbarLayout(n)`，绘制与命中共用同一份，`:1555` 注释自陈），
  **写死 460 的只有覆盖层那侧**。

### 35.4 三档（建议 A、B 分两步走，C 单独议）

| 档 | 动什么 | 净删 | 判据代价 | 风险 |
|---|---|---|---|---|
| **A 清死物** | 摘 `ToneMapping.hlsl` + csproj 条目 + 改掉 `ShaderCompiler.cs:22` 那句假口径 | 1 项资产 + 2 处文案 | **不是 0**：`ServiceTests` 6 处 + `Test_Hlsl_ToneMapping_MatchesCpu` 要换样本或撤（35.3 第一条） | 换样本后几乎无；不换样本直接删＝造出一族静默 Skip |
| **B 覆盖层 CPU-only（✅ 已执行，见 §39）** | 删 `HDRPreviewWindow.cs`，覆盖层的 GPU 背景分支整块撤（今天已让它无条件 CPU 画） | **613 行**（另加覆盖层内那段 `:210-332` ≈ 123 行） | 撤那 2 行点名锁（GPU 呈现消费那对） | **代价已由 §37.7 实测：零观感代价**。标记帧实验里下层窗涂品红 + 跳过覆盖层 CPU 画，屏幕上读到 0/6400 品红、通道均值中性灰 ⇒ 那次 `-1` margins 的 DWM 透明尝试在这张岛窗上没兑现，那扇窗的帧从来没上屏。（覆盖面：本机 HDR 面板 + HDR 输出激活；纯 SDR 屏 / ACM=off 两档本机不可造。反向注意：无条件 CPU 合成是兜底画面唯一供帧者，别把它当冗余优化掉） |
| **C 整条删兜底** | 再删 `SelectionOverlay.xaml(.cs)`，清 **13** 条死键（§38 实测，原手点数是 10）、改 LogParser 归属、撤 2 支驱动 | **1662 行** | 撤 18 行点名判据（含今天新写的 5 条）；**表那一面现在已有闸门看着**：不清表就直接红并点名（§38） | **判词已更新（§37 量过）**：这条兜底**画得出画面**（C 臂 660 种/纯白 0%/有结构），所以删它=删一条能用的回退路，不是删死物。另一半仍是取舍：`Initialize` 真失败时用户拿"截图失败"还是"丑但能用"——真实失败样本 n=0 |

顺带：`AnnotationWindow`（882 行）算 C 的姊妹档——前提是先逐工具比对内联标注（HDR 窗 10 按钮 +
`AnnoManager` 的撤销/重做）与它的功能面**是否真对等**；这个比对我没做，所以不放进任何一档的承诺里。

### 35.5 补测（2026-10-01）：那笔"没做的比对"做了，结论是**不能删**；顺带查出覆盖层已经**没有入口**

**① `AnnotationWindow` 与内联标注的功能面（按 API 调用点量，不看文案）**

| | 内联（`HdrCaptureWindow.cs`，字段 `_annoManager`）| `AnnotationWindow.xaml.cs`（字段 `_annotationManager`）|
|---|---|---|
| 用到的 `AnnotationManager` 方法 | `GetLayersSnapshot` ×3、`AddLayer` ×3、`Undo` ×2、`Redo` ×2 | 同四项 + **`ClearAll`（`:707`）、`RemoveLayer`（`:847`）** |
| 层面板操作 | 无（`ClearAll\|RemoveLayer` 在该文件命中 **0**） | `OnToggleLayers/OnLayerSelected/OnLayerPrev/OnLayerNext/OnLayerDelete/OnLayerMoveUp/OnLayerMoveDown`（`:763-859`，7 个方法） |
| 全 App 侧 `RemoveLayer`/`ClearAll` 的**唯一**调用者 | — | `AnnotationWindow.xaml.cs:707/847` |

⇒ **不对等：删层与清层只有这一处入口**。所以这一档从"姊妹档"改判为 **不删**——
要删得先在内联那侧补出"删层/清层"的两个动作，那是**补功能**，不是清理。

> **这条判词已被 §40.2 推翻一半（2026-10-01 当日第二次自纠）**：那"唯一入口"本身**不可达**
> （`DoAction` 把 `Annotate` 截走做内联，`FireAction(Annotate)` 永不发生 ⇒ `MainWindow:2083→2087` 是死路）。
> 结论方向不变（不删），但理由要从"删它掉功能"换成"它守着的是一套本来就按不到的能力"——
> 真正的缺口是**内联标注没有编辑纵深**，那是一条新功能，不是清理账（§40.3）。

**② 覆盖层现在根本没有到达它的路径**（这条改变了 B/C 的问法）
把全仓的 `GetEnvironmentVariable("TTC_` 逐个列出来（去掉 obj/bin）：**App 侧只有两个**——
`TTC_FORCE_SDR`（`MainWindow.xaml.cs:1968`）与 `TTC_GPU_COMPOSITE`（`HdrCaptureWindow.cs:386/767`）；
其余三个不在产品里：`TTC_LIVE_TRANSLATE` 在测试工程（`ServiceTests.cs:66`）、
`TTC_FFPROBE` / `TTC_JXL_HDR_OUT` 是 Tools 探针的取数变量。**没有一个能把执行送进 `initOk == false` 那一支**，
而 `SelectionOverlay` 的唯一构造点 `MainWindow.xaml.cs:2119` 正排在那一支里 ⇒
**09-30 改道之后，这 1662 行 + 613 行在正常构建里到不了**（35.2 第 2 条那个"回退 0 命中"因此更读不出东西：
不是没触发，是**没有入口可触发**）。
后果三条，都要在动手前解决：
* `overlay-geometry-cell.ps1`（19 处点名）与 `overlay-visual-cell.ps1`（16 处）**已失去被测物**，
  它们原来靠 `-ForceSdr` 进覆盖层；`TTC_FORCE_SDR` 的语义 09-30 已收窄成"只换像素源"（`:1963-1967` 注释自陈）。
* 09-30 修在覆盖层那侧的工具条钳制（`SelectionOverlay.xaml.cs:569/:603-622`，写死 460 → 实测 `DesiredSize`）
  **落在那条到不了的路径上**；活路径 `HdrCaptureWindow.cs:1620 GetToolbarLayout(n)` 本来就是算出来的，
  两者不是同一处代码，别再拿覆盖层那格当"工具条溢出已修"的证据。
* B/C 的代价口径要改：不再是"删一条走不到的兜底"，而是"**这扇窗现在只是备用件，
  而它唯一的实测价值（那 7 次 GPU 呈现成功）来自改道前的二进制**"。
  要判"到底该留兜底还是让 `Initialize` 失败直接报错"，先决条件是**给它一个可达入口**
  （一个 `TTC_FORCE_LEGACY_OVERLAY=1` 之类的开关，或让 `initOk` 可被诊断路径置假）——
  没有入口，任何"兜底可用/不可用"的读数都只能是历史二进制上的。

## §36 A 档执行（2026-10-01，用户："1."= 先换样本再摘死 shader）

### 36.1 动手前重新量了一遍：这条死链有三节，不是一节

§35.1 那行写的是"`ToneMapping.hlsl` 的 cso **无人加载**"——那句在结论上对、在链路上不完整。
真实链条是三段，一节一节量：

| 段 | 证据（当前树） | 判词 |
|---|---|---|
| `Shaders/ToneMapping.hlsl` → 字节码 | 构建期仍被 `CompileShaders.ps1:69` + `App.csproj:88` 编译 | 资产活着，产物照进 `data/Shaders` |
| 字节码 → 谁读它 | 唯一读点是 `Core/Processing/GpuToneMapper.cs:105` 的 `ShaderLoader.Load("ToneMapping.hlsl.cso")`（**不是** `HdrCaptureWindow`——那里 09-16 已删掉 `_ps` 的创建，见其 `:354/:750` 注释） | §35.1 漏了这一节 |
| `GpuToneMapper` → 谁构造它 | `AppServices.cs:76-82` 自己写着"【死代码】…属性全仓没有任何调用者"，2026-09-16 起停止构造；`new GpuToneMapper` 生产构造点 **0**；`AppServices.GpuToneMapper` 属性读取点 **0** | 整条链在第二级就断了 |

⇒ 只摘资产会留下"代码指向一个不再构建的 cso"这种更坏的形状，所以 A 档按**整簇**执行：
`GpuToneMapper.cs`（333 行）+ `ShaderLoader.cs`（74 行）+ `ToneMapping.hlsl`（113 行）+ 四处清单，合计 **520 行**。

**一条必须点名的反面旧论断**：`.workbuddy-ai/memory/2026-09-15.md:684` 曾写
"`ToneMapping.hlsl.cso` **不是死代码**：`HdrCaptureWindow.cs:346` 真实加载它"，并在 :705 警告
"'GpuToneMapper 是死代码所以 HLSL 漂移无害' → 错"。那句**在当时属实**（`_ps` 那时还在被创建并回退到这份 cso）；
09-16 那笔创建被删之后不再成立。本次删除的依据不是对旧论断的否定，而是**当前树**的读数：
剥掉整行注释后，全 App/Core 没有任何一处按名字找 `"ToneMapping.hlsl.cso"`（下表 M3 变异证明这条判据真的有牙）。

### 36.2 逐处改动（四处清单必须同一批，`CompileShaders.ps1:111-128` 是四方对账、不一致直接 `exit 1`）

| 文件 | 改动 |
|---|---|
| `Core/Processing/GpuToneMapper.cs`、`ShaderLoader.cs`、`App/Shaders/ToneMapping.hlsl`(+`.cso`) | 删除（三源文件 + 源目录里的陈旧 cso 各 1） |
| `App/Shaders/CompileShaders.ps1:69` | 摘掉 `$shaders` 里的那一条 |
| `App/TrueToneCap.App.csproj:88` | `Inputs`/`Outputs` 各摘一条，并把 `:87` 那句拿 `ToneMapping.hlsl.cso` 当"实际存在文件名"例子的说明改掉 |
| `App/Services/ShaderCompiler.cs:22` | **那句假口径改掉**：`ToneMapping.hlsl ✅ 存在，运行时消费` ⇒ 移入"已移除的条目"，并写明它唯一的加载方 09-16 起不再构造；`s_shaders` 摘掉该条 |
| `App/Services/AppServices.cs:34/:76/:100` | 删属性（无人读）、把 09-16 那段"死代码"说明改写成"当时只停构造、留下类和假话"的记录、`finally` 那句注释归位 |
| `Core/Processing/ToneMapper.cs:15`、`App/Services/HdrCaptureWindow.cs:853` | 两处指向已删类的注释改掉（不留悬空指针） |
| `Test/ServiceTests.cs` | 三条 `ShaderLoader` 测试连同节标题、`using`、文件头口径一起撤（218 → 129 行） |
| `Test/TrueToneCap.Test.csproj` | **撤掉 `CopyCompiledShadersForTests` 整个目标**：它的存在理由就是"喂 ServiceTests 的 DXBC 校验"，那条测试撤了之后没有任何测试再读 `data/Shaders`（留着一行"我们在测着色器加载"的假声明，正是本档要清的东西） |
| `Test/Program.cs:166` | `--help` 里 `--service-tests` 的说明去掉 `ShaderLoader` |

删除前逐文件拍过 sha256（`tests/output/tierA-2026-10-01/before.sha256`，11 件），
`sha256sum -c` 显示**要删的三件与我改过的那八件区分清楚**（三件 OK）才动的手。

### 36.3 新判据：`Test_ShaderList_HasNoOrphanEntry`（8 条，替代被撤的 4 条 HLSL 曲线锁）

三张名单各自独立取（`ShaderCompiler.cs` 的 `new("X.hlsl",…)` / `Shaders/*.hlsl` 目录列举 /
`App.csproj` 的 `CompileShaders Inputs`），两两比对是单向锁所以三张都对；
消费点判据只认**剥掉整行注释之后**的代码。为什么必须留在 C# 门禁里：`CompileShaders.ps1` 那份四方自检
挂在带 `Inputs/Outputs` 的目标上，**cs 改了而 .cso 没过期时它整段不跑**。

三次变异，每次只动一处、各自归属（门禁日志在 `tests/output/tierA-2026-10-01/`）：

| 变异 | 红了哪几条 | 套件读数 | 证明了什么 |
|---|---|---|---|
| **M1** 把 `new("ToneMapping.hlsl","ps_5_0")` 塞回清单 | 清单↔磁盘、清单↔csproj、`ToneMapping` 无消费点、"不在清单里"、"不许被代码引用" **共 5 条** | `1063 通过 / 5 失败 / exit=1` | 半套删除（只回清单不回文件）抓得住 |
| **M2** 换成陌生名字 `Ghost.hlsl` | 清单↔磁盘、清单↔csproj、`Ghost` 无消费点 **共 3 条**（`ToneMapping` 那三条全绿） | `1065 通过 / 3 失败 / exit=1` | 规则不是照 `ToneMapping` 这个词写的 |
| **M3** 只把 `HdrCaptureWindow.cs` 的两处 `"FullscreenVS.hlsl.cso"` 改名 | **恰好 1 条**：该条目"有运行时消费点"，其余 7 条全绿 | `1066 通过 / 1 失败 / exit=1` | 消费点这一维**单独**有牙（前三张名单完全一致时它仍会红） |

每次变异后都做了还原证明：`sha256sum -c mutation-baseline.sha256` 逐字 OK、变异标记 `FullscreenVSX` 残留 0、
重跑 `--unit-tests` 与变异前日志逐条比对**只差两处计时数字**（`1667ms`→`1660ms`、`67ms`→`64ms`），
断言名与结果序列一致。

### 36.4 断言账：1631 → 1630，按地板文件自己的规矩手改

`--unit-tests 1063 → 1067`（撤 4 条、补 8 条），`--service-tests 7 → 2`（撤 ShaderLoader 的 5 条），
其余套件不动 ⇒ 总 1631 → **1630**。第一次跑门禁（`tierA-round39-gate.log`）因此红了三行：
七套全绿 + "总断言从基线 1631 掉到 1630" + "--service-tests 断言数从基线 7 掉到 2" ——
**这正是地板该红的方式**，不是缺陷。地板文件 `_说明` 自己要求
"刻意删断言时要显式改这份文件，让删除行为留在 diff 里"，所以是手改三个数 + 追加理由，
**没有**用 `-WriteGateBaseline` 重新生成（那会把这次删除洗成无痕）。

### 36.5 活文档同步（带日期的快照一份没回写）

`docs/architecture-design.md`（活文档，多处把 GPU 路径当现状）：顶部那张"文中引用 vs 现状"的审计表
**新增三行**并改掉 `GpuEffectProcessor` 那行指向的承担者；§3 标题去掉 `GpuToneMapper`；
§3.1 着色器表把 `ps_6_0/vs_6_0` 改成实际的 **ps_5_0/vs_5_0**、`dxc.exe` 改成实际的 **fxc**、
`ToneMapping.hlsl` 标删并补一行"`CopyTexture.hlsl` 从未存在"；§3.2 整节换成删除说明 + 指向活路；
结构树 3 行、生命周期图 2 行、服务单例表 1 行逐条标注。
`docs/performance-analysis.md:102` 那行"GPU 色调映射激活＝远期收益最大"作废（它写的是"生产零调用"，
零调用是删除理由而不是待激活开关），下面那句**结论**一并改口。`README.md:132` 套件表去掉 ShaderLoader。
`CHANGELOG.md` **不动**：那两处命中是历史条目（v0.3.x 发布记录），且本仓约定"一条提交＝一版本"，
未提交的改动不该占一个版本节——等发布那笔再记。

### 36.6 这一档留下的两个尾巴

* `--service-tests` 只剩 2 条断言（`NativeLibraryResolver`）+ 1 条默认跳过的翻译端到端。这个套件已经薄到
  几乎不成立，地板也跟着掉到 2 ⇒ 下次动它之前值得先决定"要不要干脆并入别的套件"，而不是继续往里塞。
* `FullscreenVS.hlsl` 与 `OverlayComposite.hlsl` 现在都被 `HdrCaptureWindow` 的 `FindCso` 消费，
  但**着色器内容本身对不对**（合成公式、文字图集）仍只有 M3 那种"有没有人读"的锁，没有像素级判据；
  这一格与 §34/§35 交到手测的那四条一样，本仓造不出读数。

**A 档收口读数**：`tests/output/tierA-round41-gate.log` ⇒
**`GATE_EXIT=0`、7 个套件全绿、共 1630 断言 / 0 失败 / 2 跳过、无未判格子、轮前后设置指纹逐字不变**
（`--unit-tests 1067`、`--color 267`、`--encoding 14`、`--service 2`、`--ocr 12`、`--format 43`、`--usability 225`）。
另跑了一次 `dotnet build` 单验：**App 与 Test 各自 0 警告 0 错误**；单独执行 `CompileShaders.ps1`
打出 `清单自检通过: FullscreenVS.hlsl=vs_5_0, OverlayComposite.hlsl=ps_5_0` 与 `✓ 所有着色器编译完成 (2 个)`，
两个 `.cso` 字节数与删除前一致（728 / 190128）。
**两次门禁的必要性**：round40 与 round41 逐套件读数逐字相同，但 round40 那一轮里我在门禁窗口内
单独跑了一次 `CompileShaders.ps1`（动了两个 `.cso` 的 mtime）⇒ 那次读数带着我自己造成的干扰，
不能当收口证据；清跑一轮（round41）并把两轮 diff 摆出来，才是"并发写入没有影响结论"的证明而不是假设。

**与本轮无关但顺手量到的红线**：门禁第一次跑（round39）红的三行是地板机制正常工作
（"总断言 1631→1630" + "--service-tests 7→2" + "套件全绿但断言数对不上基线"），
`GATE_EXIT=1` 出现在七套全绿的同一轮里 ⇒ 这正是本仓"绿不等于对"的那道闸。

## §37 B/C 的前置工作：给兜底那条路一个可达入口，于是第一次量到它（2026-10-01，用户："继续推进"）

### 37.1 为什么先补入口，而不是先删

§35.5 的结论是"改道之后覆盖层没有入口"。当时 B/C 两档的代价全悬在那儿：
C 档的风险句写的是"从未触发的兜底消失"，而"从未触发"这个读数我在 §35.2 已经自纠成**未测**
（15 份日志 / 314 会话里，连需要它兜的那一步都只走过 1 次）。
**没有入口 ⇒ 任何"兜底能不能用"的判断只能引改道前的历史二进制** ⇒ 所以这一步不是新功能，
是把两档决策从推断里捞出来的前置。

### 37.2 改动：一个诊断开关 + 三条锁

| 处 | 内容 |
|---|---|
| `MainWindow.xaml.cs:2027-2038` | 新开关 `TTC_FORCE_LEGACY_OVERLAY=1`：`if (initOk && 开关)` ⇒ 打一条 Warning、`hdrWnd.Close()`、`_activeHdrWindow = null`、`initOk = false`。刻意**与 `initOk` 取与**：不置开关时默认行为逐字不变，失败路本来就走兜底 |
| 同上 `:2036` 注释 | 写明 `Close()` 是必要的：不先把那扇 topmost 自绘窗收掉，harness 拍到的是它而不是覆盖层，**读数会假** |
| `CorePipelineTests.Test_SdrPreview_RoutesThroughTheHdrWindow` | +3 条：唯一读取点、必须是 `initOk && GetEnvironmentVariable(...)` 那个形状、进兜底前必须 `Close()` + 清注册 |

`--unit-tests` **1067 → 1070 / 0 失败**；烘字那条 ratchet 的扫描面只有 `HdrCaptureWindow.cs`
（`:3906` 取的就是那一个文件），所以加在 `MainWindow.xaml.cs` 的新日志串没惊动 `BakedCnBaseline 99`
——这一条我是先量再下的结论：本轮日志 `✅ …== 基线 99 条（实得 99 条）`。

**判据自己被红过一次**：第一版"唯一读取点"数的是名字出现次数 ⇒ 实得 2、直接红。
第二个"读取点"是那条 Warning 文案里的开关名（**其它诊断开关的文案也这样写**，是约定不是错）。
改成数 `GetEnvironmentVariable("…")` 的调用形状才对。这条坑与本仓反复出现的那一族同源：
**缺席/计数型判据要先问"我的单位是什么"**。

### 37.3 三臂读数（`tests/output/tierA-2026-10-01/preview-content-3arm.log`，只用键盘、设置逐字还原）

| 臂 | 环境 | 全屏窗类 | 颜色种数 | 纯白 | 全黑 | 均值 | 产品自报的分支行 |
|---|---|---|---|---|---|---|---|
| A 强制 SDR 像素源 | `TTC_FORCE_SDR=1` | `TTC_HC` | 1282 | 0% | 0% | R127 G133 B140 | `预览帧来源=SDR(BGRA→scRGB) … 缩放=1.5` |
| B 正路 | 无 | `TTC_HC` | 1070 | 45.08% | 6.11% | R155 G159 B160 | `预览帧来源=HDR(Float16) … 缩放=n/a(OS 抬)` |
| **C 强制兜底覆盖层** | `TTC_FORCE_LEGACY_OVERLAY=1` | **`WinUIDesktopWin32WindowClass`** | 660 | **0%** | 7.08% | R91 G93 B92 | 见下四行 |

C 臂那四行见证是这一格真正的产出：
```
诊断开关 TTC_FORCE_LEGACY_OVERLAY=1: 主动关闭已建好的预览窗 ⇒ 改走兜底覆盖层   MainWindow.xaml.cs:2035
预览窗失败，回退旧版覆盖层：未知原因                                          MainWindow.xaml.cs:2117
GPU 背景窗口 (SDR→scRGB) 已创建 3840x2160                                   SelectionOverlay.xaml.cs:254
覆盖层就绪 实例#1 第1次进入 DPI=144 Scale=1.50 GPU背景=True                  SelectionOverlay.xaml.cs:349
背景渲染 24ms (GPU=True)                                                    SelectionOverlay.xaml.cs:370
```

### 37.4 这批读数改了什么判断

1. **"全白"这一族在兜底那侧确认消失**：C 臂纯白 0%、660 种、判词 `OK：有结构、非平白`。
   比"两臂同一张界面"更进一步——**旧界面现在也画得出画面**（09-30 那次改成无条件 CPU 合成的效果）。
2. **重入风暴确认已被修好**：`第1次进入`、`实例#1`，且没有再出现 09-30 凌晨那种 30 次叠窗
   （那 30 次的读数是本轮之前唯一能证明"路径被走到"的东西）。
3. **C 档（整条删兜底）的代价要按"删一条画得出画面的回退路"写**，不能再写"从未触发"。
   它仍是产品取舍，但取舍的两边现在都有读数：留着 = 1662 + 613 行 + 18 行判据 + 2 支驱动；
   删掉 = `Initialize` 真失败时用户拿"截图失败"而不是"丑但能用"。
4. **B 档（覆盖层 CPU-only）的"下层窗看不见"仍然是未证**。C 臂同时打出
   `GPU 背景窗口…已创建` **和** `背景渲染 24ms (GPU=True)` ⇒ 两条都在跑，
   而屏幕统计分不出可见像素是谁给的（`DimOverlay` 那层 `#80000000` 半黑遮罩还压在更上面，
   这也解释了 C 臂均值明显比 A/B 暗）。⇒ 见 §37.5。
   **（该格已结，2026-10-01 §37.7：标记帧实验读出 E 臂品红 0/6400、通道均值中性灰 ⇒ 下层窗的帧从未上屏，
   B 档观感代价实测为零；同时反证那次无条件 CPU 合成是兜底画面的唯一供帧者。）**
5. **一个新发现的保真度缺陷（只在诊断路）**：forced 兜底时 `hdrWnd.LastError` 是 null，
   于是那句回退日志与 toast 都写成**"未知原因"**——真实失败时它有值。
   默认路不受影响；但既然这个开关是**留给以后取证用的**，这点必须写下来，
   否则下次有人拿 C 臂的 toast 文案当"失败原因没上报"的证据。

### 37.5 还缺的那一格（设计好了，本轮没做）

要判"下层 GPU 窗到底有没有上过屏"（也就是 B 档的真代价），唯一干净的实验是**让两条路给出不同画面**：
给 `HdrPreviewWindow.PresentFrame` 加一个临时 env 门（`TTC_BG_MARKER=1` ⇒ 整帧涂品红），
再加一个"跳过覆盖层 CPU 画"的 env 门，三臂跑：
① 只开标记 → 拍到桌面 ⇒ 覆盖层自己的 Image 盖住了下层窗；
② 标记 + 跳过 CPU 画 → 拍到品红 ⇒ 透明机制生效、下层窗才是供帧者；拍到黑/未定义 ⇒ 透明机制不生效。
两臂都必须以"标记那一路产品自己打的日志行"为正对照，否则"没看到品红"既可能是被盖住也可能是标记没生效。
这条实验要改产品代码 + 起进程，**做完必须撤钩子**（本仓旧账：调试钩子活不过收尾）。

### 37.6 收口读数

`tests/output/tierA-round42-gate.log` ⇒ **`GATE_EXIT=0`、7 套全绿、共 1633 断言 / 无未判格子 / 设置逐字不变**
（`--unit-tests 1070`）。地板同步抬到 1633（`scripts/gate-baseline.json`，理由写在该文件 `_说明` 里）。

### 37.7 实验做了（§37.5 那格）：品红没上屏 ⇒ 下层 GPU 窗从来没贡献过一个像素

三臂都先经 `TTC_FORCE_LEGACY_OVERLAY=1` 进兜底覆盖层（不进就没有"下层窗"可问），
临时钩子只有两处：`HDRPreviewWindow.PresentFrameCore` 里 `TTC_BG_MARKER=1` 把整帧涂成 scRGB 品红，
`SelectionOverlay` 里 `TTC_OVERLAY_CPU_PAINT=0` 跳过那次无条件 CPU 合成。日志证明钩子真的生效了
（`取证钩子 TTC_BG_MARKER=1…` / `…跳过覆盖层自己的 CPU 背景合成`），不是"没跑到所以没看到"。

| 臂 | 环境 | 全屏窗类 | 品红占比（阈值判据）| 品红占比（**与亮度无关**判据）| 颜色种数 | 通道均值 |
|---|---|---|---|---|---|---|
| C 对照 | 只有 FORCE_LEGACY | `WinUIDesktopWin32WindowClass` | 0% | 13/6400 = 0.2% | 692 | R108.6 G108.4 B108.2 |
| D 仅标记 | + `BG_MARKER=1` | 同上 | 0% | 13/6400 = 0.2% | 816 | R111.2 G112.1 B113.7 |
| **E 标记且跳过 CPU 画** | + `CPU_PAINT=0` | 同上 | **0%** | **0/6400 = 0%** | **93** | R129.9 **G130.4** B129.9 |

**计数器先自证有牙**：三张合成件读出 全品红 100% / 全白 0% / 半品红 50%（3/3 通过）才去量产品。
D 与 E 各有一处需要排除的假阴性，两条都堵上了：
① "品红被 DWM 的 HDR→SDR 压暗所以阈值抓不到" ⇒ 换用**与亮度无关**的判据（真品红在任何亮度下都必须
`R≥1.4·G` 且 `B≥1.4·G`），E 臂 0/6400，且 E 的 **G 均值反而略高于 R/B** ⇒ 那是一面中性灰，不是暗品红；
② "覆盖层根本没画满、可能透出下层" ⇒ E 臂只剩 93 种颜色的页面底，是"没有供帧者"的样子，不是"透出了桌面"。

**判词（把 B 档从推断变成实测）**：
`:341-342` 那次四边 `-1` 的 `DwmExtendFrameIntoClientArea` **在这张 WinUI 3 岛窗上没有兑现**，
`HdrPreviewWindow` 呈现的每一帧从来没上过屏 ⇒
**B 档（覆盖层 CPU-only、删那 613 + 123 行）观感代价为零**，§35.4 里"真代价是关掉以后做透明的选项"
这一句要改口径：那个选项**已经实测不成立**（至少在这块面板 + HDR 输出激活这一档下不成立）。
**反向结论更要紧**：那次无条件 CPU 合成是兜底画面**唯一**的供帧者（E 臂一跳就退化成 93 种颜色的页面底），
所以谁以后想把 `if (!gpuBgOk …)` 那种"GPU 成功就不画"的逻辑请回来，这一格就是它的反例。

**钩子已撤，且撤的证据不是"我删了"**：`HDRPreviewWindow.cs` 对 `sha256sum -c` **逐字 OK**；
`SelectionOverlay.xaml.cs` 与实验前检查点比对，剥注释后的代码行 **77 vs 77、双向差集都是 0**（只剩注释不同，
内容已换成上面这条实测结论）；重编后的 `TrueToneCap.dll` 里 `TTC_BG_MARKER` / `TTC_OVERLAY_CPU_PAINT`
**UTF-16 命中 0**，而保留下来的 `TTC_FORCE_LEGACY_OVERLAY` 命中 2 ⇒ 探针本身对这个容器有效，那两个 0 才是真缺席。
**仍有一格没测**：纯 SDR 屏 / ACM=off 两档本机造不出来，"透明机制在别的环境会不会兑现"不在这次结论的覆盖面里。
取证夹具（`bg-marker-cell.ps1` / `bg-marker-stats.ps1` / `bg-marker-chroma.ps1`，在忽略区）
只对"带钩子的那个构建"有效，实验完即失效——留着是为了复现判据，不是为了再跑一次。

**实验后收口**：`tests/output/tierA-round43-gate.log` ⇒ **`GATE_EXIT=0`、7 套全绿、共 1633 断言 /
无未判格子 / 设置逐字不变**（unit 1070）——与 round42 同数，说明抬上去的地板就是当前真实规模，
也说明撤钩子没有改变任何判据结果。

## §38 补上反向闸门：表里不许有无人使用的键（并顺手把我自己手点过的 10 条改成实测 13 条，2026-10-01）

### 38.1 缺的是哪一面

`Test_Xaml_HardcodedChineseIsLocalized` 那几条管的都是正方向：**界面有中文 ⇒ 表里必须有英文**。
反方向（**表里有英文 ⇒ 界面上得有人用这个键**）过去没有任何判据覆盖 ⇒
删一处界面而忘了清 `ZhToEn`，表里留下一条死键，**门禁不响**。§35.3 点名的就是这个洞，
而它正是 B/C 档删除的前置：不补上，删完就永久留一批"英文模式再也不会用到"的条目。

新增在第 7 节，落在同一个方法里（**不另开一份解析器**：表、XAML 面、扫描面声明都复用上面已经建好的东西，
再抄一份就是第二份真相——本仓反复栽在这上面）。

### 38.2 使用面的三块，以及第一版栽的那一块

`使用面 = XAML 静态中文(attrs) + App 全部 .cs 剥注释后的文本 + <Window Title="…">`

**第一版只有前两块 ⇒ 误报 2 条真在用的键**：`TrueToneCap 标注`、`TrueToneCap 文字识别`。
根因是 `attrs` 那一循环**刻意**不收窗口标题（`if (tag2 == "Window" && prop == "Title") continue;`，
注释写着"窗口不在可视树里，只能由构造器 `Title = Tr(Title)` 兜"）——
借用别人的扫描面，就得连它故意留的洞一起补，否则"使用面"就少一类使用者。
补了标题提取，并把它写成一条**独立的 fail-closed 守卫**：那两条已知标题读不到就红（提取式失效）——
守卫不写"至少 N 条"这种我猜的数，第一版我写了 `>= 5` 而实际只有 3 条，当场把自己判红。

匹配用**整串包含**而不是再解析一遍字面量，误差方向是刻意的：宁可漏报不误报；
而"新孤儿"只在引用它的文件被删掉时出现，那时包含匹配必然失败 ⇒ 该响的一定响。

### 38.3 四个夹具 + 一条基线（`--unit-tests` 1070 → **1077**）

| 判据 | 读数 |
|---|---|
| 使用面非空守卫 | `attrs>150 且 .cs 面>100_000 字符` ⇒ 使用面读空了"零孤儿"就是假绿 |
| 窗口标题提取守卫 | 两条已知标题都必须在提取结果里（实得 3 条） |
| 夹具①（有牙） | 凭空造的键必须被抓 ⇒ 抓到 1/1 |
| 夹具②（不误报） | XAML 真用到的中文 一条都不报 ⇒ 误报 0 |
| 夹具③（不误报） | 只做窗口标题的两条键必须被认成在用 ⇒ 0（这条就是第一版栽的地方） |
| 夹具④（删除代价） | 把 `SelectionOverlay.xaml` + `.xaml.cs` 整个从使用面里摘掉 ⇒ **表里立刻死 13 条** |
| 死键基线 | `== 0（实得 0）` ⇒ 以后删界面忘了清表就红并点名 |

夹具④ 打出的 13 条（日志原样）：`保存截图到文件并复制 (Enter)`、`单击 = 全屏`、`拖拽 = 框选`、`…`、
`取消选区 (Esc)`、`复制图像到剪贴板`、`完成标注 (Esc)`、`文字 (T)`、`标注选区（矩形/箭头/马赛克等）`、
`椭圆 (E)`、`画笔 (P)`、`矩形 (R)`、`箭头 (A)`、`识别图中文字 (OCR)`、`马赛克 (M)`
（前 15 条打印，其中 `…` 本身是一条键）。

**⇒ §35.3 那句"10 条只属于覆盖层界面"是手点的，实测是 13 条**，差在我把工具条标签与提示行分开数了一遍、
漏了 `…` 与两条标注态标签。已按实测改口（改在 §35.3 原位，不另开一段遮掩）。

### 38.4 地板与收口

`scripts/gate-baseline.json`：1633 → **1640**（`--unit-tests` 1070 → 1077）。
抬地板的理由写在同文件的 `_说明` 里；这已是本日的第二次抬——
**留在旧数等于给未来留一段"删了也不红"的余量**，所以每次净增都要跟上，而不是等哪天对不上再说。
门禁复跑 `tests/output/tierA-round44-gate.log`（读数落在本节末尾）。
**round44 实测**：`GATE_EXIT=0`、7 套全绿、**共 1640 断言 / 无未判格子 / 轮前后设置逐字不变**（unit 1077）
——与抬上去的地板逐字相等，所以这一次"抬地板"同时验了两件事：新判据全绿，且基线不是抄来的。

一次自曝：本节改坏过一次文件顺序——用 Edit 往已有列表里插条目时**锚点行被吃掉**，
结果 夹具③④ 换了位置、`const OrphanBaseline` 出现两次，编译报 `CS0841`（在声明之前使用）。
修法不是继续叠补丁，是写一份 `rewrite-section7.py` 整段重写，并且**先断言旧块形状**（`OrphanBaseline` 出现 2 次）
再落盘。这也是本轮第三次遇到"判据先于我发现问题"——`CS0841` 那一次是编译器抓的，
`>= 5` 那一次是判据抓的，标题误报那一次是夹具③抓的（补完之后）。

## §39 B 档执行（2026-10-01，用户："按照你的想法找出最优解执行"）

### 39.1 我的选择：做 B、不做 C、`--service-tests` 先不动

* **B 执行**：它的代价在 §37.7 已被量成零（下层窗涂品红 + 跳过覆盖层 CPU 合成 ⇒ 屏幕读不到品红、三通道中性灰）
  ⇒ 这是一次**有读数的删除**，不是凭体感的清理。
* **C 不执行**：C 删的是**被证明能用**的那条回退路（§37.3 的 C 臂：`WinUIDesktopWin32WindowClass`、660 种、纯白 0%、有结构）。
  而真实 `Initialize` 失败样本 **n=0**，多屏 / 纯 SDR 屏 / ACM=off 三档本机造不出来
  ⇒ 证据只撑到"能删"，撑不到"该删"。**这是这一档的边界，不是我保守。**
* **`--service-tests` 不并**：并套件要动门禁的套件清单与地板键，收益只是少一行 ⇒ 记着，不值这一轮。

### 39.2 删了什么（逐处，含"为什么这处必须跟着走"）

| 处 | 内容 | 为什么跟着走 |
|---|---|---|
| `Services/HDRPreviewWindow.cs` | 整个文件删除（删除时 613 行；对 HEAD 的 diff 记 599 行，因为今天它有未提交增补） | 它就是那条不上屏的供帧路 |
| `SelectionOverlay.xaml.cs` 1486→**1333**（中途量到 1325，之后加了 10 行失败留痕、并掉 2 行空行，见 §40.1） | GPU 背景块 **130 行**（`GetSharedDevice` → `new HdrPreviewWindow` → `Task.Run` + 3 s 超时 → 抛出腿/超时腿两条 Dispose 时机）；另有第二次 `SetWindowPos` 置顶、`_hdrBgWnd` 全部散点（字段、`OnClosed` 收尾、两处区域检测的排除分支） | 那些散点全部为"下面那扇窗"服务 |
| 同上 `:341-342` | `DwmExtendFrameIntoClientArea` 四边 `-1` + `MARGINS` + PInvoke 声明 | 注释自陈目的就是"让下层 HDR 窗的 scRGB 内容可见"，实测没兑现；留着就是给"再试试透明"留门 |
| 同上 构造口 | 8 参重载（`hdrPixels/hdrW/hdrH`）合回 5 参 | **它从来没被调用过**（唯一构造点走 5 参）⇒ `_hdrPixels` 恒 null，GPU 块里那条 HDR 分支本来就进不去 |
| 同上 两条日志 | `覆盖层就绪` 去掉 `GPU背景={gpuBgOk}`；`背景渲染 …ms` 去掉 `(GPU=…)` | 读数不许承诺一个已经不存在的字段 |
| `WgcCaptureService.cs:67/:74` | 撤 `public object D3dContextLock` 与 `public ID3D11Device? GetSharedDevice()` | 唯一消费者就是那扇窗；留着等于留一份"外部可以并进这条 GPU 上下文"的错误承诺。**`_d3dContextLock` 字段内部 8 处仍在用 ⇒ 保留**；"别拿 `GetOrCreateDevice` 给另一扇窗取设备"的警告搬到它自己头上 |
| `LogParser.cs:147` | 形状②注释里的 `HDRPreviewWindow` 半句改掉 | 正则按 `*Window.cs` 认窗口，与类名无关 ⇒ 只改注释，镜像判据不受影响 |

### 39.3 判据同步（撤一条、补一条、重钉一个锚点）

* **撤** `Test_GpuPresentOutcome_IsConsumedByCaller`（文本 8 处 Assert/Skip，实跑 7 条）：它锁的是
  "GPU 呈现结果必须被消费、两条腿的 Dispose 时机要分开"，通路没了再留着就是**一条永远不会红的形状锁**
  ——与本仓"53 个源码哨兵缺文件即 Skip = 集体失明"同一族。
* **补** `Test_Overlay_HasNoGpuBackgroundPath`（5 条反向哨兵）：覆盖层里不许再出现那扇窗的类名、
  按名字找字节码的 `PresentFrame(`、`GPU背景=` 字段、`DwmExtendFrameIntoClientArea`；
  且 `RenderDesktopBackground(desktopPixels, vw, vh)` 必须仍在**无条件**分支上。
* **重钉** `Test_OverlayReentryLatch_SitsBeforeWindowCreation` 的右锚点：原比"闩锁 vs `new HdrPreviewWindow(`"，
  分配点删了 ⇒ 换成"闩锁 vs 背景渲染段"，**顺序不变量语义不变**（闩锁挪回回调底部就恢复叠窗风暴）。
  实得 `latch=5827 < alloc=6659` ✅。没有因为锚点消失就把这条撤掉。
* **夹具同步**：LogParser 镜像那条 `readyLine` 跟着新日志格式改（正则不依赖 `GPU背景=`，
  但夹具必须与真实行同形，否则测的是一个不存在的形状）。
* `--unit-tests` 1077 → **1075**；地板按 `_说明` 的规矩手改成 **1638**（−7 +5，逐处替换 + 计数守卫，
  没用 `-WriteGateBaseline` 把删除洗成无痕）。

### 39.4 中途那次"全白"：是量具的坑，顺带挖出一个真缺陷

B 改完第一遍三臂，C 臂读到 **92.97% 纯白 / 205 种 / 均值 R244**。看着像"B 把兜底画坏了"，三条都对不上：
日志 `背景渲染 33ms` 正常、整天 0 条 `[Error]`、A/B 两臂同一时刻都有结构。
**决定性的算术**：覆盖层那层 `DimOverlay` 是 `#80000000`（50% 黑），真把画上去 ⇒ 白只会到 **~R127**；
读到 R244 说明那一帧里**根本没有桌面像素**——是 HWND 已能被 UIA 枚举、而 WinUI 首帧还没合成的空档。
其后两遍同一格读到 R74.6（04:43）与 R98.4（04:47）⇒ 支持"一次时序"而不是"必然坏"。

* **量具改造（已做）**：`preview-content-cell.ps1` 加 `-SecondShotSec`，**默认 2 秒**，判决以二拍为准、
  首拍留作"首帧状态"的见证；两拍都平白才算这一臂没有画面。改的是我这把尺子，不是产品。
* **挖出的真缺陷（已修）**：`RenderDesktopBackground` 的 `catch` 过去只写 HintText、日志一个字不留
  ⇒ "背景渲染 33ms"照打、判据读到"有结构"，而屏幕上其实没画上任何东西。现在补 `LogService.Error`
  并带 `len` 与 `需要=w×h×4`。**没有这条留痕，我差点把量具的坑记成产品的账。**
* **留给后续的观察**：首帧那一瞬可能露页面底色——改道后的正常路径不经过这里，只在诊断开关下可见；
  若 C 档最终保留兜底，值得让 CPU 合成完成后强制出一帧再显示。这条只有 04:37 一次现场，没有读数。

### 39.5 读数（B 档收口）

* 三臂复核（硬化后的量具、默认二拍判决）`tests/output/tierA-2026-10-01/preview-content-hardened.log`：
  **A**（强制 SDR 像素源）`TTC_HC` 1936 种 / 白 6.33%；**B**（正路）`TTC_HC` 2136 种 / 白 6.11%；
  **C**（强制兜底覆盖层）`WinUIDesktopWin32WindowClass` **3118 种 / 白 1.06% / 黑 0% / 均值 R139.2 G136.7 B127.7**
  ⇒ 三臂全是"有结构、非平白"，**C 臂删完 GPU 分支后画面统计不降反升**（首拍 2052 种也是 OK）
  ⇒ **B 档观感零代价，这次是在"删除之后"独立量过的，不是只靠删除前那一次实验。**
  RC=0，设置 sha256 `A945F48F…` 轮前后逐字相同。
* App 与 Test 分别 `dotnet build`：**0 警告 0 错误**（删掉一个类 + 两个公共出口后无人引用）。
* 门禁 `tests/output/tierA-round45-gate.log`：**`GATE_EXIT=0`、7 套全绿、共 1638 断言 / 0 失败 / 无未判格子 /
  轮前后设置逐字不变**（`--unit-tests 1075`）——实得与手改后的地板逐字相等。
  顺带确认一件事：新加的那行 `LogService.Error` 没惊动烘字 ratchet（它仍报 `基线 99 条（实得 99 条）`，
  因为扫描面只取 `HdrCaptureWindow.cs`）。
* 独立复核：本轮另派只读子代理逐行看 B 档 diff（残留死代码 / 误删 / 日志格式影响面 / 新哨兵是否有牙），
  结论单列在 §40，不与本节混算。

## §40 两个只读子代理的回话：采纳 4、驳回 5，外加一条推翻我自己判词的产品缺陷（2026-10-01）

### 40.1 B 档 diff 的独立复核

先说我自己复验出来的两处**它读数对、我的文档要改**：`SelectionOverlay.xaml.cs` 现在是 **1333 行**
（§39.2 里那个 1325 是手术中途值——之后加了 10 行失败留痕、并掉 2 行空行）；改完 App **0 错误**、
`--unit-tests` 仍 **1075 / 0 红**，全量门禁 `tierA-round46-gate.log` ⇒ **`GATE_EXIT=0`、共 1638 断言、
无未判格子、设置逐字不变**（死赋值与空行并拢都是行为中性的，这一轮就是证明）。

**采纳（已修）**

| # | 事项 | 处置 |
|---|---|---|
| 1 | `MainWindow.xaml.cs:2126` 的 `hdrDesktopPixels = null;` 是**死赋值**：最近的相关读点在 :2063（在它上面），此后全文再无读取；它上面那句"以免下面误判成还有 HDR 路可走"描述的防护并不存在（早于本次删除，HEAD 即如此） | 删赋值，注释改成"复核结论"留痕 |
| 2 | `overlay-visual-cell.ps1:319-320/338-340` 依赖已无生产者的字面量（`GPU 呈现抛出异常`、`跳过呈现，一帧未出`、`背景渲染 … GPU=False`、窗标题 `HDR Preview`）⇒ 那两条"失败腿"**永远读到 0**，会被误读 | 文件头加退役说明；读数行下补一条 `ⓘ 读到 0 属预期`；现役口径指向 `preview-content-cell` 的 C 臂与闩锁判据 |
| 3 | 删除留下的三连空行（`:186-188`） | 收成一行 |
| 4 | 闩锁移到入口后，"一次实例只试一次背景渲染"成为**新语义**（渲染抛异常不再重试；好在失败现在有留痕） | 记进 §39.2 与本节，不必后人重推 |

**驳回（附证据，我核过）**

- "少了一次 `SetWindowPos` 会不会影响 z 序"——剩下的两处（`:169`/`:185`）与被删那次参数**逐字相同**，
  那次唯一作用是"压回背景窗之上"，背景窗已不存在 ⇒ 可证等价于 no-op。
- "撤了 DWM 会露黑底"——`DesktopImage Stretch="Fill"` 铺满整块虚拟桌面，且 WGC 两腿都过
  `FixAlphaChannel`（`WgcCaptureService.cs:533/1235`）⇒ alpha 恒 255，没有半透依赖。
  （它自己标了一项**未量过**：非扩展帧下窗口四角圆角/阴影的差异。这条我原样留着，不写成已验。）
- "`GetSharedDevice`/`D3dContextLock` 还有消费者"——大小写不敏感扫全仓 `hdrpreview` 只剩 5 处，全是注释/哨兵/诊断计数。
- "LogViewer 窗口归属会读错"——`WindowOf` 按文件名形状（`*Window.cs`/`*.xaml.cs`）归类，与这行文案无关；镜像夹具已同步。
- "新哨兵有恒真式 / 1077→1075 算错 / 8 参构造口有人调"——三项均被实读否证；
  其中"实跑 7 条"也对上了：我 b2 脚本数的 8 是 `Assert(` 出现次数，含 1 条不可达分支。

### 40.2 C 档前置账目：最重要的发现是**我上一轮的判词错了**

子代理报"`AnnotationWindow` 当前不可达"。我把这条链自己读完，**成立**：

1. `HdrCaptureWindow.DoAction` 的全部调用点只有两处：工具条 `DoAction(actions[i])`（`:1988`，数组里确实含
   `Annotate`）与键盘 Save/Copy（`:2147/:2148`）。
2. 而 `DoAction` 开头就把 `Annotate` 截走：`EnterAnnotationMode(); return;`（`:2154-2159`，注释自陈
   "2026-08-11 A 方案：标注改为窗口内内联编辑，不再关闭窗口"）⇒ **`FireAction(Annotate)` 永远不会发生**。
3. 于是 `MainWindow.xaml.cs:2083 case HdrCaptureAction.Annotate` → `:2087 OpenAnnotationWindow(...)` 这条路是死的，
   而 `OpenAnnotationWindow` 唯一调用点就是 :2087、`new AnnotationWindow` 唯一构造点就是 :1680。

  ⇒ 我 §35.5 的判词"**改判为不删——要删得先在内联侧补出删层/清层**"必须改成：**它承载的删层 / 清层 / 7 色 × 4 档配色 / 层排序，
本来就是一套够不着的能力**。⇒ 这不再是清理账，而是一条**产品能力缺口**：
内联那侧颜色与粗细是写死的（`HdrCaptureWindow.cs:1164-1166` 红 2px），没有删层/清层/层列表。

⇒ **C 档判断不变（暂缓）**，但理由换掉一半：删覆盖层**不会**掉"活着的"标注功能（独窗同样不可达）；
它掉的仍然是"**D3D 设备/交换链起不来时，唯一还能出画面并完成保存/复制/OCR/翻译的 CPU 路径**"。

### 40.3 新开一条，不在 A/B/C 三档里

**内联标注的编辑纵深**：要么给 `AnnotationWindow` 接回一个可达入口（哪怕一枚显式"高级标注"按钮），
要么把配色 / 粗细 / 删层 / 清层搬进内联工具条。现状是"代码里有一整套层编辑能力，用户按不到"。
这条是**加功能**不是清理 ⇒ 等你点头，本轮不动。

## §41 输出目录这条链第一次有了判据——以及我自己写的一条假绿被变异当场抓住（2026-10-01）

### 41.1 起因：一处真实设置 + 零判据覆盖

`exe 旁`与 `AppData\Local\TrueToneCap\`两份设置都写着 `OutputPath":"D:\\TTC"`，而**本机没有 D 盘**
（`Test-Path D:\` = False，盘符 C,E,G,Q,W）。全仓 `grep -rn 'OutputPath' src/TrueToneCap.Test/*.cs` **0 命中**
⇒ 这条链从来没进过任何判据的视野。上一轮我把它写成"每次保存必失败"是过头话，已改口：
15 份日志里 `保存失败` 0 命中，而那几次"保存成功"至少有一次是我的 harness 先把设置改到仓内目录再还原的。
所以本轮做的是**不引入任何行为变更**的那一半：把现状钉住。

### 41.2 新卡 `Test_OutputDir_OnlyFallsBackWhenEmpty_FailureIsVisible`（7 条，unit 1075 → **1082**）

| 条 | 钉住什么 |
|---|---|
| 空/空白才回落 | `string.IsNullOrWhiteSpace(outDir)` + `SpecialFolder.MyPictures` 两句都必须在（回落只服务于"没填"） |
| **非空路径不可创建 ⇒ 当场抛，没有第二道回落** | 结构判据：`GetEffectiveOutputDir` 方法体内 **`try`/`catch` 必须为 0** |
| 方法体锚点读得到 | `IndexOf("public string GetEffectiveOutputDir()")` 读不到就红（防锚点漂了蒙绿） |
| 失败留日志 | `LogService.Error("MainWindow", $"截图保存失败…`（含异常对象）必须在 |
| 失败弹提示 | `ToastService.ShowCaptureFailed(ex.Message)` 必须在 |

意图是留一道闸：**将来谁想加"建不出来就悄悄换个目录"这种降级，必须先过这张卡并写明理由**——
静默改掉用户的落点在本仓的定义里是"降级不是修复"。

### 41.3 一次假绿的现场：我自己的第一版判据是空的

第一版把第二条写成 `code.Contains("if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);")`。
然后做变异（把整句包进 `try { … } catch { outDir = 图片\TrueToneCap; }`，也就是那条我想拦的降级本身）：
**判据照绿，`--unit-tests` 1081 通过 0 失败**（`mutation-outputdir.log`）。
原因很直白：包进 try 之后，那句子串**仍然完整存在**。我在注释里写着"要加降级必须先过这条"，而它根本拦不住。

改成结构判据（截出方法体，断言其中无 `try`、无 `catch`）后，同一个变异当场红，且**只红这一条**、
详情直接印出 `try=True catch=True`（`mutation-outputdir-2.log`：`1081 通过 / 1 失败`）⇒ 归属干净。
撤变异后：文件对 `sha256sum -c` 逐字还原（`8a8ec73b…`），App 重编 **0 错误**，`--unit-tests` **1082 / 0 失败**。

> 这一条值得单独留在文档里：它跟 §17.2「哨兵读到注释」、§34.11「变异被抓要按规则名归属」是同一族，
> 但这次是**判据形状对了、单位却锁在子串上**——一句"存在"的判据，对"被包住"这种破坏是无感的。
> 判据要钉的是**结构**（谁包着谁），不是**文本存在**。

### 41.4 顺手量清的一件事：旧快照原来**不在 git 里**，直接删不可逆

`tests/manual/safety/restore/`（09-29 那份源文件快照，11 件）逐件 `git hash-object` + `git cat-file -e`：
**11/11 都不在 git 对象库** ⇒ 那是那次未提交状态的唯一副本（6 个 `wip-*.patch` 同理）。
之前"3. 可以清理"我只做到 gitignore 排除、没删，是对的。本轮给它们建了一份**已验副本**：
`tests/output/legacy-backups-20260929.tgz`（17 件，`cf164fa3…`），并做了解包往返——
两边 `sha256sum` 清单 **17 行逐字相同** ⇒ 从"删了就没了"变成"删了还能取回"。要不要真删仍等你一句。

### 41.5 地板与读数

地板 1638 → **1645**（unit 1075 → 1082），理由写在该文件 `_说明`。
全量门禁复跑：`tests/output/tierA-round47-gate.log` ⇒ **`GATE_EXIT=0`、7 套全绿、共 1645 断言 /
0 失败 / 无未判格子 / 轮前后设置逐字不变**（`--unit-tests 1082`），与手改后的地板逐字相等。
