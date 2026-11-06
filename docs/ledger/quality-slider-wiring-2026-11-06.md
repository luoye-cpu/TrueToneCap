# 滑块接线落点专项判定 · 2026-11-06

> 调查人：滑块接线调查员（**只读代码分析**；未修改 src\ 下任何文件，未 commit/push；唯一写入 = 本报告）
> 上游材料：`docs\ledger\handoff-gui-quality-2026-11-06.md`（§2.1/§2.2/§3）、`docs\ledger\quality-chain-evidence-2026-11-06.md`（裁决①②③、§1.3、§3.3、§4）
> 任务：逐环核查"拖动 → OnQualityChanged → SetQuality(正确格式)"落点，产出失效机理判定
> 所有行号基于本次审计时的工作区状态（二源覆盖 `xaml.cs:3404` 已删除，见 xaml.cs:3417-3419 注释与 CorePipelineTests.cs:10390-10439 三层哨兵）

---

## 0. 一句话判定（先给结论）

**静态分析在产品代码中找不到"拖动 → 字段"链路的任何断点：7 环全部闭合。** "设置文件每格式字段全缺键（=全默认）"与"用户在当前版本成功拖动过滑块"在静态证据下**不相容** —— 失效最可能发生在链路上游之外（旧版拖动痕迹只留死键 `"Quality":1`、拖动对象是已收起的 PNG/TIFF 行、或 <500ms 内拖回默认被防抖合并）。**最终定案必须运行时实证（§6）**：静态分析能排除的是"产品接线断了"，不能证明的是"用户到底有没有在新版拖过"。

---

## 1. 逐环证据表

### 环 1 · 事件绑定 —— ✅ 闭合（绑定唯一、不可解除、拖动必然触发）

| 证据 | 位置 | 结论 |
|---|---|---|
| `ValueChanged="OnQualityChanged"` 为 XAML 属性绑定 | `MainWindow.xaml:56` | 编译期生成器接线，非弱引用事件；全代码后置 grep 无任何 `-=`（解除）或二次 `+=`（重复绑定）⇒ **绑定不可能在运行中被解除或重复** |
| 全 App 仅 2 个 Slider | `MainWindow.xaml:56`（QualitySld）、`MainWindow.xaml:517`（RecordQualitySld） | 贴图窗（PinWindow）、预览窗**均无质量滑块** —— "多实例不同格"假设排除。RecordQualitySld 走独立链路 `OnRecordQualityChanged`（xaml.cs:1304-1309）→ `RecordQuality` 字段 → 仅动图录制（AnimationRecorder.cs:353-354），与静态图质量零交集（quality-chain-evidence §3.2 已证） |
| 拖动时事件必然触发 | WinUI RangeBase 语义 + `xaml.cs:681` | 拖动（PointerMove 持续吸附）、键盘、程序赋值均抛 ValueChanged；松手前的最后位置必产生一次。唯一不触发的形态：`IsEnabled=false` —— 那正是 PNG/TIFF（`QualityIsFixed` ⇒ 681 行禁用 + 680 行 `QualityRow.Visibility = Collapsed` **整行收起，用户看不到也拖不动**） |

### 环 2 · 守卫 —— ✅ 闭合（finally 必复位 + 同步窗口对用户拖动不可达）

| 证据 | 位置 | 结论 |
|---|---|---|
| 置位/复位形态 | `MainWindow.xaml.cs:653-658`：`_qualityPanelUpdating = true; try { UpdateQualityPanelCore(); } finally { _qualityPanelUpdating = false; }` | **复位走 finally ⇒ "守卫置位后异常路径未复位 → 用户后续拖动永久被吞"的窗口不存在**。哨兵测试钉死此形态：CorePipelineTests.cs:8289-8291（"复位必须走 finally"）+ 8285-8288（"OnQualityChanged 的入口守卫必须认这个标志"）+ 8292-8294（"面板主体在 Core 里（包装才有作用域可言）"） |
| 守卫确实也挡 OnQualityChanged | `MainWindow.xaml.cs:1345`：`if (!_uiReady || _qualityPanelUpdating || FormatCbo.SelectedIndex < 0) return;` | "拖动发生在程序性更新窗口内会被吞"**理论上成立、静态上不可达**：该守卫窗口是 UI 线程**同步执行段**（try 块内无 await），WinUI 事件单线程队列分发，用户拖动的 ValueChanged 不可能插入同步代码中途 |
| 守卫窗口内的异步逃逸核查 | `RefreshAvifMeasuredCaps` xaml.cs:1140-1150：`Task.Run` 回调经 `DispatcherQueue.TryEnqueue` 只调 `UpdateAvifEncoderLabels()` 与 `UpdateAvifOptionAvailability()` | 异步回调**不触碰滑块的任何属性**（全部 8 处 QualitySld 赋值点已穷举：Settings.cs:105、xaml.cs:612/668/669/670/671/674/681/878、手输 1501/1512 —— 无一位于异步回调路径）⇒ 守卫窗口没有"半截留在外面"的尾巴 |
| `_uiReady` 窗口 | xaml.cs:352-356（构造期 `_uiReady=false` → `ApplySettingsToUI()` → `true`） | 仅启动后约 435ms（Brief 档实测），窗口尚未可交互，用户不可能在此拖动 |

### 环 3 · 格式一致 —— ✅ 闭合（双取法同落点 + 切格式窗口同步不可插入）

| 证据 | 位置 | 结论 |
|---|---|---|
| `CurrentFormat()` 定义 | `MainWindow.xaml.cs:3262-3267`：`_formats[FormatCbo.SelectedIndex]`，且 JPEG LI + GainMapEnabled ⇒ 翻转成 `JPEG_GAINMAP` | 取的是**全局格式选择器**（格式下拉）的值，不是滑块面板的独立状态 —— 滑块面板本身没有"自己的格式"概念 |
| OnQualityChanged 内两处取法不同 | 1346 行 `var format = _formats[FormatCbo.SelectedIndex]`（**不翻转**，仅供 `EncoderFactory.Create` 出描述文案）；1353 行 `SetQuality(CurrentFormat(), val)`（**翻转**，写入落点） | 两法不同**但落点同格**：AppSettingsData.cs:507/522 中 `JPEG_LI` 与 `JPEG_GAINMAP` **共用 `QualityJpegLi` 字段**（"增益图态不单独占一格"注释，:497-498）⇒ 写入无分叉。文案用的不翻转 format 仅影响 `QualityDescTxt`，且两者 GetQualityRange 同为 butteraugli 0.5-3.0（xaml.cs:672 注释自证）⇒ 无害 |
| 滑块与格式选择器何时可能不同格 | Core 663 行（`_formats[FormatCbo.SelectedIndex]`）与 876 行（`CurrentFormat()`）同栈刷新 | **静态不存在"面板尚未随格式切换刷新"的状态**：`OnFormatChanged`（xaml.cs:634-648）→ `UpdateQualityPanel()`（637 行）在**同一同步调用栈**内完成 SelectedIndex 赋值 → SelectionChanged → 面板重画；且 ComboBox 下拉展开时指针被 Popup 捕获，用户无法在间隙拖滑块 |
| 预览窗/贴图窗与主窗口滑块是否同一实例 | grep 全部 XAML 仅 2 个 Slider（环 1） | 是同一实例（唯一）；贴图窗"保存为 AVIF"（xaml.cs:2989 → 1626）用**主窗口编码快照**，不读贴图窗控件。修复前该路径的量纲错位已随二源覆盖删除而消灭（xaml.cs:3412-3419 注释自述） |

### 环 4 · 弹回 —— ✅ 闭合（弹回读回的是用户已写的值；无"写回默认"路径）

| 证据 | 位置 | 结论 |
|---|---|---|
| `UpdateQualityPanel` 全部调用点穷举 | xaml.cs:365（构造尾，启动后首次刷新）、**593**（`DetectAndApplySystemCapabilitiesAsync` 尾部，xaml.cs:514 起的一次性启动期能力检测，await 后回 UI 线程）、637（OnFormatChanged）、901（OnAvifEncoderChanged）、1231（OnGainMapToggled）、1544（OnHdrToggled）、1578（色域变化）、3617（语言切换） | 触发源全部是**用户主动动作或启动期一次性事件**，不存在"每次截图后"或周期性刷新 ⇒ 用户拖完后没有常见动作会隐蔽地重置滑块 |
| 弹回用字段值重置滑块 | Core 876-880：`savedQ = _settings.GetQuality(CurrentFormat())` → 越界回落 `def`（877）→ `QualitySld.Value = useQ`（878） | **OnQualityChanged 在拖动的每一次 ValueChanged 都同步 SetQuality（1353 行，内存即时写入，无延迟）** ⇒ 任何弹回读到的都是用户刚写入的值 ⇒ 弹回值=用户值，观感稳定。"拖完滑块 → 再截一张/切格式/关面板触发弹回默认"**静态不可达** |
| `SetQuality` 全部调用点闭环 | xaml.cs:1353（拖动）、xaml.cs:1514（手输失焦）、MainWindow.Settings.cs:368（保存设置时回写滑块值） | 三处写的都是**滑块当前值**，而滑块当前值只有三种来源：用户拖动、用户手输、面板刷新（=字段值自身）⇒ **不存在任何"字段被程序改回默认"的路径**，闭环自洽 |
| 877 行越界回落 `def` 的可达性 | 加载路径 xaml.cs:877 + SettingsService.cs:448-465（`Normalize` 加载时对 7 个每格式字段做区间钳制） | 超界值只能来自旧设置文件，但 Normalize 已在加载时钳掉 ⇒ `def` 分支对**新写入的值**不可达。其代价（quality-chain-evidence §2.3 时刻 2：管线日志与 MainWindow 日志可能不一致）随二源覆盖删除已一并消失（xaml.cs:3419 "两行质量日志从此恒一致"） |

### 环 5 · 区间钳制 —— ✅ 闭合（区间恒同步；"Maximum 残留 63"前提不成立）

| 证据 | 位置 | 结论 |
|---|---|---|
| 区间随格式无条件重设 | Core 668/669 行：`QualitySld.Minimum = min; QualitySld.Maximum = max;`（`encoder.GetQualityRange()`，Core 能力表单一事实源） | **每刷必重设（无条件赋值，非增量）⇒ 运行中区间恒等于当前格式区间**。`UpdateQualityPanel` 的 8 个调用点保证了任何格式变化后必有一次刷新 |
| 唯一的增量式修改 | xaml.cs:612：`QualitySld.Minimum = Math.Max(QualitySld.Minimum, AvifEncoderCapabilities.MinCrf(resolved))`，宿主 `UpdateQualityPanelForAvifHardware`（597），仅在 Core 的 isAvif 分支（867）调用 | 此时 668 行刚设过 AVIF 的格式 min ⇒ 只会**抬高 AVIF CRF 下限**（NvEnc/QSV 无损约束，610-611 注释）；非 AVIF 格式不调用 ⇒ **无跨格式残留**。"JPEG LI 面板 Maximum 残留 63"的假设被否定 |
| 拖动值是否可能超格式区间 | 滑块 Value 恒被 RangeBase 的 Min/Max 物理约束；OnQualityChanged 1348 行仅 `Math.Round(v, 1)`（无区间钳制） | **不需要** OnQualityChanged 钳制：拖动值物理上出不了滑块区间，而滑块区间恒=格式区间（上两行）⇒ 写入值恒合法。`SetQuality` 本体无钳制（AppSettingsData.cs:516-529 纯赋值）在此前提下无害 |
| "值超 ButteraugliRange 被 SetQuality 钳制/忽略" | SetQuality 无任何忽略逻辑（直接写字段）；下游 JpegLiNative.cs:117 `Math.Clamp(distance, 0.0f, 25.0f)` 的静默钳制确实存在（quality-chain-evidence §1.3 实证：灌 100 → 钳 25 → 产物 1/4.25 = 量纲灾难） | 该灾难路径的**唯一入口是量纲错位**（另一格式区间的值灌进来）——二源覆盖删除后，量纲错位的所有静态可达形态（forceFormat、越界回落）已消灭（xaml.cs:3417-3419） |
| 手输路径 | xaml.cs:1500/1510：`Math.Clamp(v, QualitySld.Minimum, QualitySld.Maximum)` | 手输同样恒在区间内 |

### 环 6 · Snap/SmallChange —— ✅ 闭合（次要瑕疵一处，不构成失效）

| 证据 | 位置 | 结论 |
|---|---|---|
| 步进设置 | xaml.cs:670-674：`SmallChange = 0.1; LargeChange = 0.5; StepFrequency = precise ? 0.1 : 1.0`（precise = JPEG_LI/JPEG_XL/JPEG_GAINMAP，673 行） | JPEG LI 区间 0.5-3.0 × 步进 0.1 = **26 档分辨率，不粗**；AVIF/WebP 整数量纲配步进 1.0 恰当。未用 IsSnapToTickEnabled/TickFrequency（WinUI Slider 用 StepFrequency，同义） |
| 唯一小瑕 | XAML 未声明 StepFrequency（MainWindow.xaml:56）⇒ WinUI 默认 1.0 生效于启动到首次刷新（xaml.cs:365）之间的数百 ms | 该窗口内用户无法交互（窗口刚构造完）。即便拖动，StepFrequency=1 对 JPEG LI 只损失小数位（1.0/2.0/3.0），不会产生超界值。**记录在案，不构成"极其糟糕"** |

### 环 7 · 修复后链路完整性 —— ✅ 闭合（拖动→本次编码全程无断点）

逐点确认（修复后唯一链路 = 拖动 → OnQualityChanged → SetQuality(字段) → 管线 GetPerFormatQuality）：

1. **拖动**：`MainWindow.xaml:56` ValueChanged ⇒ `OnQualityChanged`（xaml.cs:1341）。
2. **守卫通过**：1345 行三条件对用户拖动全为 false（环 2）。
3. **写字段（内存即时）**：1353 行 `_settings.SetQuality(CurrentFormat(), val)` —— 同步内存写入，**不等 500ms 防抖**。
4. **落盘**：1354 行 `ScheduleSettingsSave`（xaml.cs:180-188：500ms 防抖 → `DispatcherQueue.TryEnqueue` 回 UI 线程 → `SaveQuiet` → 差集序列化）—— 序列化在 UI 线程执行，与字段读写同线程，无线程安全问题；防抖只延迟**文件**，不延迟**内存**。
5. **编码消费**：截图动作 → `CaptureEncodingUiState`（xaml.cs:3244/3254：`Settings` = 按下那一刻 `_settings.Snapshot()` 整份快照）→ 管线 `CapturePipelineService.cs:291-294` `Quality = (float)GetPerFormatQuality(s, format)`（= `s.GetQuality(format)`，:594-595）→ `EncodingSettings.Quality` → 编码器（quality-chain-evidence 裁决①：三编码器在差异质量值下产物字节比 1.54x~2.12x，坐实生效）。
6. **"拖完立即截图"也生效**：SetQuality 内存写入先于快照 ⇒ 编码必拿新值；即便 500ms 内进程崩溃，丢的只是文件持久化，不是本次编码。
7. **二源已死且钉死**：覆盖点删除（xaml.cs:3417-3419 注释）+ 三层哨兵测试（CorePipelineTests.cs:10390-10439：record 无 Quality 成员 / 快照采集不含滑块 / BuildEncodingSettings 方法体无 `settings.Quality =`、剥注释后 `ui.Quality` 零出现）。

**修复前后失效观感差异**：用户"拖动 → 主管线编码"这条路径**修复前后逐值一致**（覆盖在滑块与字段同步的时刻=冗余等值，交接 §2.1 日志三行数值一致即同步时刻的实证）。修复真正改变的是：(a) 贴图窗 forceFormat 量纲错位（修复前 `xaml.cs:2989` → `3404` 把原格式量纲灌进目标格式，§1.3 可达形态；修复后统一走目标格式字段）；(b) 越界回落时刻的两行日志分叉。**即：修复与本案例"拖动滑块疑似失效"无关——若用户在修复前就遇到失效，机理也不在主管线拖动路径上。**

---

## 2. 静态可达的三个低危窗口（记录在案，均不构成"极其糟糕"主因）

| # | 窗口 | 位置 | 危害评估 |
|---|---|---|---|
| a | 拖动进行中恰逢面板重刷（弹回观感） | xaml.cs:593（启动期能力检测一次性回调）、1231/1544/1578（用户主动动作，同步执行不与拖动并发） | 弹回读的是用户已写值（环 4）⇒ 值不丢，仅视觉跳动；启动期窗口用户尚未交互 |
| b | 手输中间态每字符写盘 | xaml.cs:1496-1504：TextChanged → `QualitySld.Value = v`（1501）→ 触发 OnQualityChanged → SetQuality | 敲 "2.5" 的中间态 "2" 会被写入字段并启动防抖，但继续输入 500ms 内最终值覆盖 ⇒ 最终值正确；且与 1514 行失焦写入同格 |
| c | <500ms 内拖出又拖回默认值 | ScheduleSettingsSave xaml.cs:180-188（tick 合并） | 只留最终值（=默认=缺键）⇒ **拖动痕迹完全消失**。概率低，但与用户证据相容（见 §3） |

---

## 3. 最终判定：失效机理

### 3.1 判定

**产品代码"拖动 → OnQualityChanged → SetQuality(正确格式) → 编码"链路静态核查 7 环全部闭合，无断点。** 守卫异常不复位（finally 钉死）、异步回调裸改滑块（不存在）、多实例不同步（唯一实例）、面板与格式不同格（同步栈刷新）、字段被弹回默认（闭环自洽）、区间残留（每刷重设）、步进过粗（0.1 步进）——七个候选机理逐一被证据排除。

### 3.2 与用户证据的不相容性（本轮最硬的一条推理）

- 设置文件实证（quality-chain-evidence §3.3）：**所有每格式字段全部缺键** = 反序列化全走默认（JPEG_LI=1.0 / AVIF=20 / PNG=100）⇒ 与三次编码日志全默认完全吻合。
- 推理：用户若在**当前版本**对任何可见可拖的滑块（AVIF/WebP/JPEG LI/JXL）成功拖动过一次，`OnQualityChanged` 必触发 → `SetQuality` 写入非默认值（内存即时）→ 500ms 后差集序列化**必写键**（值≠默认）。
- **"文件全缺键"与"当前版本成功拖动过"二者不可同真**，除非落入 §2-c（<500ms 拖回默认）或拖动对象根本不可拖（PNG/TIFF 整行收起，环 1）。
- 设置文件里唯一的拖动痕迹是死键 `"Quality":1`（§3.3）——它说明**滑块曾到过 1**，但写入它的代码路径（`MainWindow.Settings.cs:363` 镜像到遗留 `Quality` 字段）属于**每格式字段引入前的旧版行为**；且不存在"遗留 Quality → 每格式字段"的迁移（§3.4）⇒ **旧版拖出的质量偏好在新版里无声丢失**。这是与全部证据最自洽的解释：**用户"调过质量"是真的，但那次的痕迹留在了死键上；新版里他面对的是全默认的每格式字段**。

### 3.3 结论分级

1. **排除**："滑块接线在当前版本断了"——7 环证据不支持。
2. **最可能**：用户的质量偏好停留在旧版（死键 `Quality:1`），新版从未成功输入过非默认质量；"极其糟糕"的观感需要另行归因——JPEG LI 420 色度对文字/UI 截图发糊（handoff 可疑点 D，已拍板的产品口径）是首要候选，其次是 AVIF CRF=20（`-q 68`）对某些内容本就不够好的观感。
3. **不能由静态分析排除的残余**：用户在当前版本的某次拖动落入 §2-c 极窄窗口、或存在本审计未覆盖的运行时形态（WinUI 事件时序的实测偏差）。**这就是必须运行时实证的原因——静态分析能证明"接线是通的"，不能证明"用户按的是这根线"。**

---

## 4. 若 7 环中任一环未来发生缺陷的观感对照（交接第 7 环要求）

| 若缺陷在 | 修复前（二源覆盖存在时） | 修复后（现状） |
|---|---|---|
| 守卫环（异常不复位） | 拖动被吞 + 贴图窗 forceFormat 路径仍以滑块值编码（错量纲活跃） | 拖动被吞，但 forceFormat 已走字段（错量纲路径死） |
| 格式环（写错格） | 错格值**立即生效**（3404 覆盖让滑块值直通编码） | 错格值落进错误字段，编码仍取正确格字段 ⇒ **编码不受影响**，仅字段脏 |
| 区间环（Maximum 残留 63） | 滑块超界值经覆盖**直通编码** → JpegLiNative 钳 25 → 极其糟糕（§1.3 实证） | 超界值只会写脏字段，编码取值仍被 877 行回落兜住 ⇒ 观感差异巨大 |
| 弹回环（字段被重置默认） | 滑块弹回 + 编码经覆盖用滑块默认值 ⇒ 本次编码即变默认 | 滑块弹回 + 编码用被重置的字段默认值 ⇒ 两者观感相同 |

即：**修复把"滑块错误直通编码"的三条通路全部收窄成"只脏字段"** —— 这也是为什么修复后若再报"拖动失效"，排查重点应放在字段与落盘（§6 步骤 2），而不是编码值。

---

## 5. 关键坐标索引

| 环 | 坐标 |
|---|---|
| 绑定 | MainWindow.xaml:56（ValueChanged）· xaml.cs:1304-1309（RecordQualitySld 独立链路）· xaml.cs:681（IsEnabled）· 680（QualityRow 收起） |
| 守卫 | xaml.cs:653-658（try/finally）· 1345（入口）· 3431-3442（设计注释）· 1140-1150（异步回调不触滑块）· CorePipelineTests.cs:8285-8294（哨兵） |
| 格式 | xaml.cs:3262-3267（CurrentFormat）· 1346/1353（双取法）· AppSettingsData.cs:507/522（GainMap 共格）· 497-498（设计注释） |
| 弹回 | xaml.cs:365/593/637/901/1231/1544/1578/3617（8 调用点）· 876-880（重置逻辑）· 1353/1514/MainWindow.Settings.cs:368（SetQuality 三写入点） |
| 区间 | xaml.cs:668/669（每刷重设）· 612（仅抬 AVIF 下限）· 877（加载回落）· AppSettingsData.cs:516-529（SetQuality 无钳制）· SettingsService.cs:448-465（加载钳制） |
| Snap | xaml.cs:670-674 |
| 链路 | xaml.cs:1341-1355 → 180-188 → CapturePipelineService.cs:291-294/594-595 → xaml.cs:3244-3254（快照）· 3417-3419（删除注释）· CorePipelineTests.cs:10390-10439（三层哨兵） |

---

## 6. 运行时实证（handoff §3 第 1 步的落地细化，本报告判据分支）

> 目的：静态已排除产品接线缺陷，实证目标是**钉死"用户拖动是否在当前版本到达字段"**。全程只读日志 + 设置文件，不改代码。

**最小操作步骤**（约 2 分钟）：

1. 启动应用 → 格式选 **AVIF** → 记下滑块显示值（应为 20.0，区间 0-63）。
2. 把滑块**拖到 8**（肉眼明显不同；或手输 8 后按 Tab 让失焦路径 1514 行也走一遍）→ 等 1 秒。
3. 打开 `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json`，查 **`QualityAvif`** 键。
4. 随便截一张保存，然后看日志 `%LOCALAPPDATA%\TrueToneCap\log\app_<日期>.log` 两行：
   - `[系统/MainWindow] BuildEncodingSettings: 质量=?`
   - `[系统/Pipeline] 编码设置: AVIF ... 质量=?`

**判据分支**：

| 读数 | 判定 | 下一步 |
|---|---|---|
| `QualityAvif: 8` 且两行日志都是 8.0 | **接线全通，收案**。用户案例归因 = 旧版拖动留死键（§3.2）+ 观感另行归因（420 色度 / CRF=20 本身的档位观感） | 转交 handoff 可疑点 D 的用户确认；可选：给遗留 `Quality` 键补 §3.5-3 的一次性迁移或日志提示 |
| `QualityAvif` 缺键/仍 20，但滑块显示 8 | 拖动→字段断了（与静态结论矛盾，说明存在运行时形态） | 看滑块是否"自己弹回 20"：弹回 ⇒ 弹回环（在 xaml.cs:878 加临时日志定位调用源）；不弹回 ⇒ 在 OnQualityChanged 1345 行前加 `LogService.Info($"QC: guard={_qualityPanelUpdating} uiReady={_uiReady}")` 复跑 |
| `QualityAvif: 8` 但日志仍 20.0 | 字段通、编码源断 —— 但管线快照注释（xaml.cs:3244）与哨兵测试都排除此形态 | 检查是否跑了旧 exe（便携版 publish\ 与商店版目录混用，handoff §5 日志路径两条） |
| 两行都是 8.0 但产物依旧差 | 编码器消费段——已被 quality-chain-evidence 裁决①实证排除（1.54x~2.12x） | 归因观感：420 色度 / 用户预期管理 |

**顺手可做的零成本观察**：让用户在 JPEG LI 页拖一次滑块并注意 `QualityLbl` 文本是否实时跟随（1349 行）——文本跟随但文件不写 ⇒ OnQualityChanged 已触发且 SetQuality 已执行 ⇒ 矛盾只可能出在序列化（quality-chain-evidence §3.3 已证差集序列化正常）⇒ 该分支实际不可达，仅作旁证。

---

## 7. 本轮未做 / 移交

1. 运行时实证（§6）需用户配合，是收案前置条件。
2. AVIF/WebP 包装类仍无命令行日志（quality-chain-evidence §1.4 遗留）——若 §6 走到第三行分支才需要补。
3. `MainWindow.Settings.cs:363` 镜像写死键（`_settings.Quality = QualitySld.Value`）每保存一次就制造一次"这键有用"的假象，处置建议见 quality-chain-evidence §3.5（本轮只读，未动）。