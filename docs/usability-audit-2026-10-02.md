# TrueToneCap 完整可用性审计（2026-10-02）

审计对象：`main` 工作树，HEAD `ca7ed99` + 72 个未提交条目。
门禁基线：`round64` = `GATE_EXIT=0` / **1690 断言** / 无未判格子。

**证据分级（先读这段再看结论）**：本审计由三条只读子代理线索 + 我自己的实测/回读合成。
按本仓规矩，子代理的总结不算读数，所以：

- **我亲自复核过的**（逐条 grep / 读源码 / 实机截图）：测试工程引用面（csproj:8）、`AnimationRecorder` 零调用点、
  `HotkeyManager.RegisterNamed` 全部调用点、`ShowPreview` 全部出现点、`RecordBtn` 的 `Click` 目标、
  本地化扫描面清单、`OverlayColorCbo` 选项数、LogViewer 零机制、`LanguageChanged` 零订阅、
  `OcrPreviewWindow.xaml.cs:277`、默认语言与系统语言探测缺失、`WgcCaptureService.cs:55/1111/1123/1485/1503`、
  `CoreLog` 的 Brief 契约、`AppSettingsData` 默认详细度、`SelfTest.cs:148-150` 与 `--selftest` 入口、
  `NativeAvifEncoder.cs:296-297` 与输出路径无清理、发布包体积构成、冷启动耗时、第二实例行为（实机两次）。
- **采信了子代理、我未逐行回读的**：`FormatEncoders.cs` / `CapturePipelineService.cs` 的具体行号清单、
  `HdrCaptureWindow.cs` 的 D3D 文字烘制行号（该结论 10-01 轮已独立确证过，只是行号未重读）、
  `MainWindow.xaml:533` 与 `MainWindow.xaml.cs:2843/2852` 的字体提示—行为不一致、
  `MainWindow.Localization.cs:331-337` 的副窗口 `Loaded` 挂载点。
  ⇒ 这些支撑的是**次要论断**，行号可能有偏差；要动它们之前先重读。
- **已被我驳掉的子代理结论**：见 §2.2 的 `RecordBtn`、§3.2 末尾的反向本地化条、§2.1 的 `:1111`。

---

## 0. 判词

**核心截图—编码—保存/复制这条主链是可用的，且被真实证据支撑**（六格式矩阵、区域截图两腿、
降级可见性、启动稳定性都在 10-01 轮实机量过）。

**但这个产品的"外围"没有主链那样的证据密度，而且有三处用户能直接撞见的空洞：**

| 级别 | 判词 |
|---|---|
| **P1** | **HDR 可以悄悄降成 SDR，而且这违反本仓自己写下的日志契约。** `CoreLog` 的 `Brief` 档注释明写"Info 及以上 ⇒ **一切降级事实**"，默认详细度就是 `Brief`；而多显示器拼接时"放弃整图 HDR，输出 SDR"这一句走的是 `Log()` = `Debug` ⇒ 默认配置下用户看不到。一个主打"真色/HDR 截图"的工具，最要紧的那条降级被自己过滤掉了。 |
| **P1** | **动图录制是卖出去但没接线的功能。** 界面上有格式/帧率/时长/质量/快捷键一整套控件，`AnimationRecorder` 全仓**零实例化**，`RecordHotkey` **从不注册**（`RegisterNamed` 只有 `"capture"` 与 `"silent"` 两个调用点）⇒ 用户全部设置都不产生任何结果。 |
| **P2** | **双击第二个实例没有任何可见反应（两次实测一致）。** 第二个进程正常退出（`RC=0`，≈1 s / ≈4.5 s），但已有窗口**没有被带到前台**，用户只会认为"程序卡了/没反应"。 |
| **P2** | **英文模式是半成品**：`LanguageChanged` 零订阅、已开窗口不重渲染、`ComboBoxItem` 不在扫描面、LogViewer 完全没有机制。 |
| **P2** | **`预览` 复选框勾了没反应**（`ShowPreview` 只有自身读写，无任何行为消费者）。 |

**一句话结论**：主链可以发布；但"录制"这一整块要么接线要么下架，
HDR→SDR 的降级必须提到 Info，这两条不解决就不能说"功能完整"。

---

## 0.5 新旧归属（对本审计的一次自我纠正）

写完初稿后我逐条回查了 `docs/` 与交接台账，结果是：**上面六项里有四项不是新发现，而是旧账**，
其中两项还是用户明确拍板"先待命"的。把它们当新缺陷报出来是错的，会引导错的动作。逐条归属：

| 项 | 归属 | 先前出处 |
|---|---|---|
| F1 HDR→SDR 只落 Debug | **新** | 无。且新加的契约哨兵当场又逮到 2 处（`:717`/`:723`）手工审计漏掉的同类 |
| F2 动图录制无入口 | **旧 · 已拍板待命** | 交接 **§54.4**"动图录制整条腿（用户令『先待命』）：`AnimationRecorder.cs` 370 行、`new AnimationRecorder(` 零处" |
| F3 第二实例无前台反馈 | **新** | 无（`FlashWindow` 在全部文档里只出现在本文件） |
| F4 `ShowPreview` 假配置 | **旧 · 已登记** | 交接 **§54.4**"另外两处假配置：`PreviewChk`/`ShowPreview`（5 个引用全是写，读者 0）"；更早见 09-15 §341 |
| F5 发布包夹带自测样张 | **旧 · 已记录** | 09-27 §212 已逐字点名 `color-samples/____DISPLAY5-{System,BT2020}.png`（各 7 024 730 B） |
| F6 `ComboBoxItem` 不在扫描面 | **旧 · 已量化并有尺子** | 09-27 **N1**，且已写好可复算脚本 `tests/manual/combo_locale_coverage_measure.py`，读数 `named=19 zh_items=73 unhooked=7 unhooked_items=35 file_total_items=131` |
| F7 字体提示与行为相反 | **新** | 无（`FontDesc`/`要重启` 只出现在本文件） |

⇒ **真正新增的只有 F1、F3、F7 三条**，本轮也只动这三条 + 一条通用防线（§7 的兜底卡）。
F2/F4 属"已拍板待命"，**不在本轮擅自动手范围**；F5 是旧账复现（仍未修）；F6 该先重跑 09-27 那把尺子，
而不是另起一套。

**为什么我会重报**：派子代理做只读普查时，我没有把"已拍板/待命清单"贴进任务说明——
这正是本仓记忆里已经写过一次的错法（09-29 同一条 dead code 第二次被当成新 P0 报回）。
根因是**那份待命账本只活在散文里，门禁不查它**，所以每次重新普查都会把它当新料刨出来。
这条本身就是 §7 兜底卡存在的理由。

---

## 1. 承重前提：为什么"1690 断言全绿"证明力有限

`src/TrueToneCap.Test/TrueToneCap.Test.csproj:8` 里**唯一的 `ProjectReference` 是 `TrueToneCap.Core`**。

⇒ 测试宿主**无法实例化任何 App 类型**（`MainWindow` / `AppSettingsData` / `SettingsService` / `HotkeyManager` 都不行）。
⇒ 因此**每一条点名的"界面控件判据"，按构造都只能是源码文本哨兵**（"这行字还在文件里"），不是执行判据。
⇒ 门禁里唯一真正跑起来的 App 代码是 `TrueToneCap.exe --settings-migration`（`verify.ps1:940–1000`），
  且只覆盖取值归一化。

这条决定了本审计的读法：**"有判据" ≠ "会被测到坏"**。下面凡是标 SOURCE-TEXT 的，
坏掉时门禁不会红，只有眼睛会看见——本轮 LogViewer 工具条叠字（§14.2）就是活例。

---

## 2. P1

### 2.1 HDR→SDR 静默降级，且与自家日志契约冲突

| 证据 | 内容 |
|---|---|
| `CoreLog.cs`（`LogDetail.Brief` 注释） | "简（默认）: 用户操作、启动时的各种能力识别、**一切降级事实**。Info 及以上。" |
| `AppSettingsData.cs:291` | `public string LogVerbosity { get; set; } = "Brief";` ⇒ 默认档就是 Brief |
| `WgcCaptureService.cs:55` | `private static void Log(string msg) => LogService.Debug(...)` |
| `WgcCaptureService.cs:1485` | `Log($"[WGC] {hdrFillFailures} 个显示器未参与 HDR 拼接 → 放弃整图 HDR，输出 SDR");` |

同一条链上还有两处同类：`:1503`（"HDR 源区域填充失败，放弃 HDR 输出（保留 SDR）"，只有 `Log()`）、
`:1123`（"Float16 捕获失败…（不影响 SDR 结果）"，只有 `Log()`）。
⇒ **降级事实写在 Debug 档，而契约要求它在 Brief 档出现。** 修法很小（`Log(` → `LogInfo(`）但语义要定：
是否要在截图当时就弹提示（用户已经拿到一张"不是他要的"图）。**这条我不擅自定。**

> ⚠ 本条第一版把 `:1111`（HDR/SDR 会话尺寸不一致）也列成静默降级，**那是错的**：
> 它下一行 `:1112` 就有一条配对的 `LogInfo("HDR 会话尺寸不匹配…已回退 SDR")`，属 HONOURED。
> 错因是直接采信了子代理的行号清单没有逐行回读——回读后从"四处"改成"三处"。
> 同文件里 `Log($…)` 38 处、`LogInfo(…)` 24 处，两种用法并存，所以"有没有配对 Info"必须逐条看，不能按模式推。

同类但**已处理正确**的对照：编码器缺失 ⇒ 换 PNG + toast（`FormatEncoders.cs:141,183,307,348,522,571`
→ `CapturePipelineService.cs:764-790`）；AVIF 位深/色度/编码器协商 ⇒ toast；崩溃哨兵禁用后端 ⇒ toast。
⇒ 说明"降级要让用户看见"在这个仓里是**执行得到的**，2.1 是漏网而不是做不到。

### 2.2 动图录制：可见、可设置、无实现

| 环节 | 实况 | 证据 |
|---|---|---|
| UI | 格式 / 帧率 / 时长 / 质量 / 快捷键一整套控件存在，且**没有 `Visibility="Collapsed"`** | `MainWindow.xaml:348-362`（`AnimFormatCbo`/`RecordFpsCbo`/`MaxDurationCbo`/`RecordQualitySld`/`RecordBtn`/`RecordHotkeyTxt`） |
| 录制器 | `AnimationRecorder` 全仓**零实例化** | `grep -rn 'AnimationRecorder' src/ --include=*.cs` 去掉自身文件后 **0 命中** |
| 快捷键 | `RecordHotkey` 从不注册 | `HotkeyManager.RegisterNamed` 调用点只有 `"silent"`（`MainWindow.xaml.cs:298,3000`）与 `"capture"`（`HotkeyManager.cs:84`） |
| 设置项 | `RecordQuality` 写盘、无人消费 | 唯一消费者 `AnimationRecorder` 不存在 |

**要写准的一点（我纠正了子代理的说法）**：`RecordBtn` **不是死按钮** ——
它是 `<Button x:Name="RecordBtn" Content="录制" Click="OnRecordHotkeyClick"/>`，
即"点这里、再按键来绑定录制快捷键"。按钮工作正常，**被绑定的那个快捷键没有注册**。
这个区别重要：把它说成"按钮没接线"会引导错修法。

⇒ 这是本轮唯一一处"用户会认为自己发现了 bug 但其实是功能没做"的地方。**要么接线要么下架**，
保持现状是最坏选项（它同时污染设置页和英文本地化面）。

---

## 3. P2

### 3.1 双击第二个实例：无可见反应（自测，n=2）

`App.xaml.cs:124` 抢 `Global\TrueToneCap_SingleInstance` 失败 → `:134` `BroadcastActivateExistingInstance()`
（`:312` `SendMessageTimeout(HWND_BROADCAST, …, 1500ms)`）→ 接收侧 `MainWindow.xaml.cs:3131` 命中
→ `:3166` 最小化则 `Restore()` → `:3171` `Activate()`。**机制是接好的。**

实测（`w9-second-instance-cell.ps1`，先把前台切到别的进程再判定）：

| 次 | 第二进程 | 之后实例数 | 前台窗是否=旧主窗 |
|---|---|---|---|
| 1 | 退出，RC=0，≈1 s | 1 | **否**（停在 WPS 文档窗） |
| 2 | 退出，RC=0，≈4.5 s | 1 | **否**（停在别进程的"另存为"） |

⇒ 单实例本身工作正常；**"把已有窗口叫到面前"这一步没成功**，与 Windows 前台锁
（后台进程 `SetForegroundWindow` 默认被拒）一致。
**未量的部分**：任务栏是否闪了提示——本判据只看前台窗，没看任务栏状态。
修法方向（不擅自动手）：`FlashWindowEx`，或在第二进程退出前 `AllowSetForegroundWindow`。

### 3.2 英文模式是半成品

| 事实 | 证据 | 我是否亲自复核 |
|---|---|---|
| `LocaleManager.LanguageChanged` 事件**零订阅**（声明 + `?.Invoke` 各一处，无 `+=`） | `LocaleManager.cs:18,24` | ✅ 亲自 grep |
| 已打开的副窗口只挂 `Loaded` ⇒ 切语言不重渲染 | `MainWindow.Localization.cs:331-337`，5 个副窗口调用 | 部分（读了调用点清单） |
| 扫描面**不含 `ComboBoxItem`** | 我逐行读了 `MainWindow.Localization.cs` 的 surface 声明：`TextBlock.Text / CheckBox / RadioButton / FrameworkElement.ToolTip / Button.Content / NavigationViewItem.Content / ToggleSwitch.Header / TextBox.Header / PlaceholderText / NumberBox.Header / AppBarButton.Label` | ✅ 亲自读 |
| 靠 48 处手写 `SetComboItemText` 补，但**没覆盖全部下拉** | `OverlayColorCbo` 的 7 个中文选项区间内 `SetComboItemText` = **0** | ✅ 亲自数 |
| LogViewer **完全没有本地化机制**，却带 19 条中文 UI 串 | `grep 'LocaleManager\|ZhToEn\|ApplyLocale' src/TrueToneCap.LogViewer` = **0 命中** | ✅ 亲自 grep |
| 默认语言恒 `zh`，不探测系统语言 | `AppSettingsData.cs:328`；`grep 'CurrentUICulture\|PrimaryLCID\|GetUserDefault'` = **0 命中** | ✅ 亲自 grep |
| `HdrCaptureWindow` 的工具条文字**烘进 D3D 位图** ⇒ 门禁与 UIA 都看不见 | `HdrCaptureWindow.cs:1598,1615,1696-1716` | 沿用 10-01 轮已确证结论 |

**我驳掉的一条子代理结论**：它报"存在英文模式下显示中文的反向缺陷 `OcrPreviewWindow.xaml.cs:277`"。
实读该行及其上方注释：`OverlayToggleTxt.Text = show ? LocaleManager.Tr("隐藏覆盖") : "Show overlay";`
——注释明写这一处**已经修过**（"原先右侧写的是中文…复核轮抓到，交接 §55.6"）。反向三元方向正确，**不成立**。

**我改口的另一条**：子代理报 `OverlayColorCbo` 有 13 个选项，实测 **7 个**。

### 3.3 `预览` 复选框无消费者

`ShowPreview` 全部出现点：`MainWindow.Settings.cs:127`（回填勾选态）、`:281`/`:331`、
`MainWindow.xaml.cs:2772`（写）、`AppSettingsData.cs:143`（定义）。
**没有任何一处把它读进条件判断。** ⇒ 勾与不勾行为一致。

### 3.4 设置页里"改了要重启/要下一张才生效"的说法与实现不一致

`MainWindow.xaml:533` 的 `FontDesc` 告诉用户改字体需重启，而 `OnFontChanged`
（`MainWindow.xaml.cs:2843,2852`）**当场就 `ApplyFontToUI()`**。⇒ 提示与行为相反。
反过来，编码侧的位深/色度/质量/AVIF 编码器/PNG 兜底是**下一张截图才生效且无任何提示**
（`AppSettingsData.Snapshot()` → `CapturePipelineService.BuildEncodingSettings`）。

---

## 4. P3 / 清洁度

- **发布包夹带维护者自测产物**：`publish/TrueToneCap-v0.3.6-beta/color-samples/____DISPLAY5-{System,BT2020}.png`
  共 **13.4 MB**，时间戳 2026-09-28（发布目录是 10-01 生成的）。它们是 `SelfTest.cs:148-150`
  按 `AppContext.BaseDirectory` 写出的自测样张，文件名里还带着**维护者显示器的 sanitized 名字**。
  `grep -c 'color-samples' Publish.ps1` = **0** ⇒ 没有排除规则。
  （我一开始怀疑它是"没人用的测试垃圾"，查到 `SelfTest.cs:148` 有写者、且 `--selftest` 是命令行专用入口
  （`App.xaml.cs:77` 跑完 `Environment.Exit`），所以**不影响终端用户路径**，只影响发布包整洁与体积。）
- **原生编码器失败可能留下半截文件**：`NativeAvifEncoder.cs:296-297` 在 `ExitCode != 0` 时直接抛，
  不删 `path`；`grep -rn 'File.Delete' src/TrueToneCap.Core/Encoding/*` 的命中全在
  `AtomicFileWriter`（自己的临时件）、探针与哨兵里，**没有一处删输出路径**。
  ⇒ 兜底 PNG 与半截 `.avif` 并排留在用户目录。**我未实跑复现**，只确证了"无清理代码"。
- **包体**：425.9 MB / 613 文件，其中 `TrueToneCap.r2r.dll` 一个就 122.2 MB（29%），OCR 模型 66.3 MB。
  第三方 `.pdb` 由 `Publish.ps1` 有意剔除、本项目 `.pdb` 有意保留用于报障——**这不是缺陷**，
  我一开始误判为"符号文件外泄"，查了 `Publish.ps1:627-645` 才纠正。

---

## 5. 已经做对的（审计只列缺陷不可信）

- **冷启动 1.26 s**：进程启动 06:09:00.888 → 主窗口已创建 06:09:02.150；全局热键在 **1.23 s** 就已注册可用。
- **输出目录不可写**：不静默改道，直接 `❌ 保存失败: …` + 失败 toast（`CapturePipelineService.cs:509-518`
  无 try/catch 无重定向，且被 `CorePipelineTests.cs:1331-1345` 钉住）⇒ **HONOURED**。
- **无 HDR 硬件**：卡片显示 `⚠ 此显示器不支持 HDR` 且禁用开关并给理由。
- **GPU 编码器不可用**：硬件→libaom、HDR→avifenc 的重写都记日志并弹 toast；
  `Auto` 档故意只记日志（那是用户没指定具体后端的情形）。
- **托管 PNG/TIFF 走 `AtomicFileWrite`** ⇒ 编码中途崩溃不会留下损坏的成品文件。
- **崩溃自愈**：`EncoderCrashSentinel` 只禁用一个会话并 toast 说明。
- **隐私地板**：`CoreLog` 明写任何档位不得写入图像字节、OCR 文本、API 密钥。
- **门禁自身**：1690 断言、无未判格子、地板逐字比对、源码哨兵带反向孤儿闸门。

---

## 6. 本审计**没有评**的（别当成已验）

1. **性能与长时稳定性**：连续截图 500 张、内存增长、句柄泄漏、4K 多屏拼接耗时——一律未测。
2. **触控 / 无鼠标操作**：Tab 序列、焦点顺序、屏幕阅读器、高对比度模式——未测。
3. **安装与卸载**：这是 unpackaged 自包含目录，"卸载"就是删文件夹；注册表残留、
   开机自启项在删除后是否遗留——未测。
4. **任务栏交互**：3F 跳转列表、托盘菜单项、通知点击回跳——未测（3.1 只判了前台窗）。
5. **多显示器不同 DPI 混合缩放下的覆盖层几何**：09-22 曾验过 WGC interop，但工具条定位/命中未重验。
6. **OCR 精度、翻译质量、LLM 端到端**：`--ocr-tests` 与 `ServiceTests` 的活网络那一条默认跳过
  （`TTC_LIVE_TRANSLATE` 门控）。
7. **第二实例的"任务栏是否闪烁"**：见 3.1 末尾。
8. **半截 `.avif` 的实际复现**：见 §4，只证"无清理代码"，未证"确实留下过"。

---

## 7. 建议动手顺序（等你一句，我按这个做）

| # | 动作 | 代价 | 判据 |
|---|---|---|---|
| 1 | HDR→SDR 那 3 处只有 Debug 的 `Log(` → `LogInfo(`（`:1123/:1485/:1503`；`:1111` 已有配对 Info，不动），并加一条源码哨兵禁止"放弃 HDR"走 Debug | 小 | 变异：改回 Debug ⇒ 卡红 |
| 2 | 录制功能：**下架**（删卡 + 删 5 个设置键 + 记入决策）或**接线**（`AnimationRecorder` 接进 `RecordBtn` 链路 + 注册 `RecordHotkey`） | 下架小 / 接线大 | 无论哪条，都要有一条"设置键不许无人消费"的卡兜住 |
| 3 | 双击第二实例 ⇒ 加 `FlashWindowEx` 或 `AllowSetForegroundWindow`，实机判"前台或任务栏至少有一个动" | 小 | 复用 `w9-second-instance-cell.ps1`，判据扩成"前台=旧窗 或 任务栏在闪" |
| 4 | `ShowPreview` 接进预览分支或删除复选框 | 小 | 同 2 的兜底卡 |
| 5 | `Publish.ps1` 排除 `color-samples/`，并在发布后断言包内无 `____DISPLAY*` | 小 | 出包后一条 glob 断言 |
| 6 | 英文面：把 `ComboBoxItem` 接进扫描面（或把 48 处手写补全成"每个下拉都要有"）；LogViewer 要么接机制要么明写"仅中文" | 中 | 现有中文卡已能兜，缺的是覆盖面 |
| 7 | `FontDesc` 文案与 `OnFontChanged` 行为对齐（二选一） | 小 | 源码哨兵 |

（下表是**当时的计划**；实际落地结果与计划的偏差全部记在 §8，其中 F6 的方案被量否、兜底卡被主动撤除。）

---

## 8. 本轮实际落地结果（06:20–08:45）

| 项 | 结果 | 证据 |
|---|---|---|
| **F1** HDR 降级只落 Debug | **已修** | 5 处提到 Info：`:1123/:1485/:1503`（审计原列）+ `:717/:723`（**新卡当场又逮到的两处**，手工审计漏了）。`:1111` 下一行本就有配对 `LogInfo`，属重复 ⇒ 删 Debug 留 Info。新卡 `Test_WgcCapture_DegradationIsVisibleAtDefaultVerbosity` 三层：词表扫描 + 已知降级逐条点名 + 反向锚（≥3 条降级以 Info 落盘）。unit 1127→**1131** |
| **F7** 字体提示与行为相反 | **已修** | 改成实话："选择后立即生效；该字体未安装时界面会回落到默认字体"。同一个错误声明在源码里有**两份**（`LocaleManager.cs:145` 属性 + `:311` 表项），四处（中/英 × 两处）一起改 |
| **F5** 发布包夹带自测样张 | **已修（脚本侧）** | `Publish.ps1` 加剔除块 + 删后断言无残留。谓词干跑实测：`sampleDirs=1 files=2 MB=13.4`，`leftover=3`。**未跑真实发布**（发布是远端动作，等授权） |
| **F3** 第二实例无前台反馈 | **已改，未判** | 先定位再动手：把接收侧日志从 Debug 提到 Info 后实测——广播 **7 ms 内就收到**、无"单实例激活失败"，但前台仍不是旧窗 ⇒ 坏的确实是 Windows 前台锁，不是消息没通。据此加了 `AllowSetForegroundWindow(ASFW_ANY)`（发送侧）+ `FlashWindowEx(FLASHW_ALL\|FLASHW_TIMERNOFG)`（接收侧兜底）。**但本机判据被第三方全屏窗污染**：整轮前台窗从头到尾没变过（连夹具自己的 `SetForegroundWindow` 都没生效）⇒ 只能记"已改、未判"，不能记成已修 |
| **兜底卡**（写了没人读的设置键） | **主动撤除** | 做不到可接受精度：`\bKEY\b` 被**同名异物**污染——`Quality` 命中 83 处无关标识符、`HardwareEncodingEnabled` 命中的是 `FormatEncoders.cs` 里另一个同名成员、`BitDepthGainMap/ChromaGainMap` 命中的是 SelfTest 迁移夹具。泛化检测器在这种命名下必然假红 ⇒ 卡片删除，不留半成品 |
| **F6** 下拉项本地化 | **未动，且原方案被否** | 先量再改：`ZhToEn` 表 191 个键里**没有任何一个是下拉项文字** ⇒ "把 `ComboBoxItem` 接进扫描面"这条**几乎买不到东西**（扫描器对 44+5+5 个缺口项一个键都找不到）。真正的活是往表里播种或按 combo 做 `foreach`（`LogVerbosityCbo` 已是这个形状）。**顺带量到两处新的**：`Localization.cs:192` 传 Tag `""`、`:194` 传 `"zh-Hans"`（真实 ID 是 `zh-Hans-CN`）⇒ 两条静默失效的翻译调用 |
| **F2 / F4** | **未动** | §0.5 已归为"已拍板待命"，不在擅自动手范围 |

**过程中的两次自我否决**（都是"仪器坏了"而不是"产品坏了"，区别很重要）：
1. 探针第一版目录过滤写成 `if os.sep + 'obj' + dp or ...` ⇒ 第一个操作数是非空字符串、**恒真** ⇒
   一个 `.cs` 都没读到 ⇒ `OutputPath`/`FormatIndex` 这些活着也被报成"0 消费者"。靠**正例必须命中**这条控制才发现。
2. `MainWindow.xaml.cs` 是**混合换行**（17 个 CRLF + 3428 个 LF），其余文件都是纯 CRLF ⇒
   按 CRLF 换算的锚点匹配不上。这条本身也记作一条仓库卫生问题。

**门禁**：`round65` 绿 1694 → 抬地板 → `round66-floorcheck` 绿 **1694 == 地板** / 无未判格子 / `GATE_EXIT=0`。

---

## 9. 10-03 续：一次我自己造成的文件损坏、一次开机自启动副作用，和 F3 的对照臂

### 9.1 两个源文件被我的操作损坏，已完整恢复（过程值得留痕）

给 F3 做"修复前对照臂"时，我把两个文件备份到 `C:\tmp`，然后：

- **备份被本机清理器吃掉了**（记忆里就有这条："C 盘占用是移动靶，本机清理器秒删 %TEMP% 新建文件"）；
- 恢复时 `cp /c/tmp/app-f3.cs src/...` **找不到源文件**——但 MSYS 的 `cp` 在报错前已经把**目标截断**了；
- 结果 `App.xaml.cs` 与 `MainWindow.xaml.cs` 变成 **NUL 填充的废文件**（`git` 直接认成二进制）。

恢复路径：`tests/output/uncommitted-2026-10-01b.patch`（08:43 那份）里含这两个文件的完整 diff
⇒ `git checkout HEAD -- <两文件>` + 逐文件 `git apply`。核对：无 NUL、行数正常、
F3 锚点齐全、**本轮之前那批未提交工作（FormatCbo 回落、SetComboByTag 改写）也都在**。
`git diff --stat HEAD` = 141 增 / 26 删，`--ignore-cr-at-eol` 给**同样**的数 ⇒ 换行符没有污染 diff。

**三条要改流程的结论**：① 备份一律放**仓库内**（`tests/output/recovery-*/`），不放 `C:\tmp`；
② 做对照臂之前先确认备份可读，再动源文件；③ 退路补丁不是"存了就完"，要像这次一样
**当场验它在不在、内容对不对**——它这次真的救了回来。

### 9.2 顺带纠正我上一轮的换行符判断（方向是反的）

上一轮我写"HEAD 是纯 LF、那 17 个 CRLF 是我带进来的"，并把 `MainWindow.xaml.cs` 归一回 LF。
实际：本机 `core.autocrlf = true`，且 `.gitattributes` **刻意没设** `* text=auto`
（注释里写明是为了避免全仓重归一化的噪声）⇒ `git show HEAD:` 给的是**索引里的 LF**，
而工作区的约定本来就是 **CRLF**。所以那个文件"大部分是 LF"才是异常状态，
我那次"归一"是**朝错误方向改的**。恢复后两文件回到纯 CRLF，是对的。

本轮改过的文件里只剩 1 处混合换行（`CorePipelineTests.cs` 里我插入的一行 CRLF，
该文件本身以 LF 为主 7883:1）⇒ 按**文件自身**的主导约定归一，不按仓库约定。

### 9.3 我把用户的开机自启动改掉了，已撤除——但这同时是一条产品缺陷

`app_2026-10-03.log` 08:53:23：`主窗口已创建, 自动启动=True` → `开机自启动模式，不显示窗口`。
`HKCU\...\Run\TrueToneCap` 指向 **dev 构建** 的 exe 带 `--autostart`。链路是审计 §3.4 那条：
`StartupManager` 只在 `SaveSettings()` 里被同步 ⇒ 本轮某格验证**真按了「保存设置」**，
就把一个测试构建注册进了用户的开机启动。重启后它以无窗口模式活着，双击 exe 又被单实例吞掉
⇒ **用户看到的正是"程序崩溃/没反应"**。

已撤除并读回确认（`w9-revert-dev-autostart.ps1`，只删指向 dev 路径的值，指向别处一律不动）：
`删除后读回 =（已不存在）✓`，`AutoStart` 由 `true` 改回 `false`。

**产品面这条要记下**：`StartupManager` 把"**哪个构建最后按过保存**"写进开机启动，**没有任何提示**。
任何用户（或任何一次测试）跑一下开发版并保存设置，自启动就被指向测试构建。属 P2。

### 9.4 F3 的对照臂：我的新判据修复前也成立 ⇒ 那两处改动本机仍未被验证

换成读窗口状态（最小化 → 启第二实例 → 看 `IsIconic` 是否变回 False）之后，测试通过：
`第 1 次轮询：IsIconic 变 False ⇒ 窗口被恢复`，实例数回到 1，第二实例 `RC=0`。

**但我拿修复前的代码跑了同一格：也通过。** 这符合预期——我加的是
`AllowSetForegroundWindow` 与 `FlashWindowEx`，两者都不影响 `IsIconic`，
而 `p.Restore()` 本来就在。所以：

- 这格证明的是【广播 → Restore 这条链本来就通】；
- **它不证明我加的两处有任何效果**；
- "窗口是否跳到最前"归 Windows 前台锁管，本机有第三方全屏置顶窗、连夹具自己的
  `SetForegroundWindow` 都不生效 ⇒ **F3 的两处新增至今未被验证**，只能记"已实现、未证"。

### 9.5 F6 的两条死调用已修，并把缺口变成门禁里的天花板

从 `src/TrueToneCap.Core/Services/OcrLanguage.cs` 读出真实 ID 后逐条定性：

| 调用 | 判定 | 处置 |
|---|---|---|
| `SetComboItemText(OcrLangCbo, "", OlSystem)` | 两份语言表里都**没有空 Tag**，且"跟随系统"这一项**根本不存在** | **删调用**（不是硬凑一个目标），并留注释说明要支持它得先加语言项 |
| `SetComboItemText(OcrLangCbo, "zh-Hans", TlChinese)` | 真实 ID 是 `zh-Hans-CN` ⇒ 静默死调用 | **改对** |
| `:193 "zh-en"` / `:195 "en-US"` | 分别只在 ONNX / Windows OCR 下命中 | **条件有效，不动**（上一轮"可能死"的怀疑被否） |

并把 `combo_locale_coverage_measure.py` 从"只报数"升级成带 `--ceiling 7 35` 的判据，
接进 `verify.ps1` 外部判据列表。路径改为**按脚本自身位置**解析（不靠调用方 cwd）。
判据有牙实测：天花板=现值 `RC=0`、收紧到 6/34 `RC=1`、再跑仍 `RC=0`。
⇒ F6 从散文里的待办变成**只许变小、不许变大**的账。

**我上一轮的一条测量错误也要留**：验退出码时写成 `... | tail -2; echo RC=$?`，
拿到的是 `tail` 的码不是 python 的 ⇒ 第一次"收紧也 RC=0"是假读数。去掉管道后才是真的红。

### 9.6 现在的账

- 门禁 `round67` = `GATE_EXIT=0` / **1694 断言 == 地板** / 无未判格子 / 新天花板判据已在线。
- unit **1131** 全绿（0 失败 1 跳过）。
- 退路补丁已刷新：md5 `38b22da5…`，跟踪段 62 文件 / 未跟踪 84 文件，反打 rc=0，
  并**当场验过**两个受损文件与 F3/降级卡的关键字确实在补丁里。
- 开机自启动已撤除；`AutoStart` 已改回 false。


---

## 9.7 先审最优解：英文模式到底能不能用（11:10）

用户这一轮的指令是「按照你的想法审查一下最优解，然后完整推进完成」。审出来的第一条不是
"哪串文案怎么翻"，而是 **我此前所有界面截图都是中文模式** —— 也就是说 F6 整套修法
（补 35 个下拉项、补格式 Hint、补质量描述）全都建立在"英文模式本身是好的"这个**从没量过**的
假设上。这一票必须最先投，否则后面全是白做。

`w9-english-mode-cell.ps1`：往 dev 那份设置里按字节注入 `"Language":"en"`，截图，再按字节还原并
复算 sha256（`93B942EC30AEE7F2…` 与轮前逐字相同）。读数 = **机制通、覆盖不全**：导航、卡片标题、
`PNG (Lossless)` 都翻成了英文，落地下面留着两条中文提示行。假设成立，才继续铺字符串。

## 9.8 三批补完，与它们各自量到的数

| 批次 | 内容 | 落地读数 |
|---|---|---|
| 一 | 7 条格式 `Hint` + GainMap 两段（`s_formatCaps` 是 `static readonly` + 编译期常量 ⇒ 只能在**使用点**翻，不能在声明处） | unit 1131 全绿；`ui-en2.png` 里提示行已英文化 |
| 二 | Core 质量文案 10 串（6 条 `GetQualityRange().Label` + 2 条常量描述 + 2 条内插串片段） | unit 1138（+7 新判据）；`ui-en3.png` 出现 `Lossless (fixed 100%)` |
| 三 | 7 个下拉共 35 个中文项（34 个不同键，`全部` 两个下拉共用） | 静态 18/18 已接线、0 漏；T3 展开 7/7、44 项、**0 项中文** |

第二批差点漏掉一半：**区间标签 `QualityLabel.Text`（`MainWindow.xaml.cs:540`）和描述行
`SetQualityDescription`（:1108）是两个使用点**，上一版脚本只包了后者。英文模式下前者会整行留中文。

### 为什么"内插串按片段换"不是糊

Core 里有两条 `$"距离: {q:F1}…"`，App 拿到的是带数值的成品串，整串精确匹配永不命中。
按已知片段换看着像糊，但它被 `Test_QualityTexts_CoreStringsAreAllCovered` 钉住：
该判据枚举**全部格式 × {区间下界, 默认, 上界}** 实跑 Core，把 App 清单扣掉后**还剩中文就红并点名**。
Core 一改措辞 ⇒ 片段替换落空 ⇒ 当场红。三发变异各自归属：

| 变异 | 期望被哪一半抓到 | 实得 |
|---|---|---|
| M1 抽掉清单里一条 | "全被 App 清单覆盖" | 红，点名 `JPEG_LI.Label` 与 `JPEG_GAINMAP.Label` |
| M2 Core 改写措辞 | 同上 | 红，点名 `WebP.Label=质量 (50-100; 85+ 视觉近乎完美)` |
| M3 删掉一条译文 | "清单里每一条都在 ZhToEn 有译文" | 红，点名 `butteraugli 距离 (0.5-3.0)` |

还原一律按 md5 复算逐字一致，还原后复跑 1138 全绿。

**中文模式界面文字不变的依据是结构，不是读数**：`CoreText` 在非英文模式第一行就原样返回，
`SetComboItemText(…, Tr(串))` 在中文模式写回的就是同一个串。这一条我没有拿截图量，别当量过。

## 9.9 这一轮顺带查出的三条产品事实

**① `AvifCodecCbo` 的项文字不是 XAML 字面量。** 11:57 的 UIA 展开读数里它只有 4 项、且没有
`Intel QSV / 系统 MFT`。读代码定位到 `UpdateAvifEncoderLabels()`：它按能力表把**每一个非 Auto 项**的
`Content` 重写成 `AvifEncoderCapabilities.Caps(kind).DisplayName`，还会往后拼 `（本机不可用）`。
⇒ 第三批写在该下拉上的 `SetComboItemText(…, "Qsv", Tr(…))` 是**死写**，已撤；改在重写那一处过
`CoreText`，并给 Core 能力表加第二段覆盖判据（+7，变异实测：改 Qsv 的 DisplayName 当场红并点名）。
`Auto` 那一项由该函数**显式跳过**（其文档注释写明"Auto 的显示名就是 XAML 里的「自动」"），
所以那一条 `SetComboItemText` 是唯一写者，保留。

**② `_settings.OverlayColor` 有两个写者，10-01 只修了一个。** 展开读数里 `OverlayColorCbo`
数到 8 项而 XAML 只 7 项，多出的是 `SetComboByTag` 把盘上原值补成的一项，标签是一串裸
`#99001833`。顺着查：`MainWindow.Settings.cs:291` 已在 10-01 改成"没有选中项就保留原值"，
而 `MainWindow.xaml.cs:1231` 的 `OnOverlayColorChanged` 仍留着 `?? "#99001833"` ——
盘上那个脏值就是它再生产的。已按同一处方改齐，并把那条判据从"只看一份文件"改成
"跟着这个字段有几个写者走"（枚举 App 全部 `.cs` 里 `_settings.OverlayColor =` 的赋值点，
少于 2 个就红）。变异实测：把第二个写者改回旧写法 ⇒ 红并点名 `MainWindow.xaml.cs`（旧判据看不见这个文件）。
盘上现值仍是 `#99001833`，**我没动它**（10-01 的处方就是"不许静默改写用户数据"）；用户在界面里
任选一个真实选项一次，它自愈。

**③ `AvifCodecCbo` 少的那一项是产品行为，不是缺陷也不是采样没到位。**
`UpdateAvifOptionAvailability` 里的 `SetItemsVisible` 在 HDR 开着时收起不支持 HDR 通路的后端
（本机 HDR 屏在线 ⇒ Qsv 被收）。判据因此改成把"哪些项允许消失"写进预期表，而不是放宽判定；
若项数既不等于账面、也不能由这几项解释，仍然红。

## 9.10 我自己两台仪器错在哪里（这两条比补的字符串更值钱）

**`combo_locale_coverage_measure.py` 把 18 个下拉 / 70 个中文项报成了 19 / 73。**
块尾只找 `</ComboBox>`，而 `FormatCbo` 是自闭合的（项由 `ItemsSource` 生成）⇒ `find` 越过它
捞到**下一个**下拉的闭合标签，把 `ArchiveModeCbo` 的 3 个中文项算给了 `FormatCbo`。
原有的"选中数 > 文件级总数"哨兵抓不到它，因为 73 < 131 —— 只防"多到离谱"，不防"多算一份"。
补了三样：块区间不许重叠（变异 B 实测红并点名 `FormatCbo/ArchiveModeCbo`）、
一个都没认领就判"未判"（变异 A 实测 RC=1）、以及**换 ElementTree 当独立裁判**
（30 个具名下拉 / 131 个直接子项，逐下拉一致）。开闭标签的配平口径也改成
`开 == 闭 + 自闭合`，不再让一份合法的自闭合每次都把"分块口径不可信"打在结论上。

**第一次修完还引入了一个恒零 bug。** 把块尾改成"卡在下一个开标签"时用了裸 `<ComboBox`，
而 `<ComboBoxItem` 也以它打头 ⇒ 每个块都切成空、`named=0`，而 `--ceiling 0 0` **照样绿**。
"0 个漏"和"尺子根本没看见东西"在这一层是同一个读数。上面那条反向哨兵就是为它加的。

**`w9-i18n-quality.py` 抄表式正则时漏了收尾的 `]`**，把"对照表解析成功"判成 0 条。
原来的消息只有 `只解析出 0 条`，分不清"没有 ZhToEn ="和"正则没匹配上"——补上守卫自报输入
（命中位置 / 块长 / 实读文件名 / 字符数）才一眼定位。顺带把这条守卫留在判据里。

**两条老坑再中一次**：`... | tail; echo RC=$?` 量的是 `tail` 的码（变异 A 第一次"RC=0"是假的）；
`MainWindow.xaml.cs` 是全 CRLF 而另三份 .cs 是全 LF，锚点里写死 ` chr(10)` 会让 CRLF 那份恒命中 0 次
——这不是"没有这一行"，是锚点没对齐。

## 9.11 现在的账（12:07）

- 门禁 `tests/output/w9/gate-final.log` = **GATE_RC=0** / 7 套件全通过 / **1709 断言** / 无未判格子。
- 地板 `scripts/gate-baseline.json` 1694 → **1709**（unit 1131 → 1146，+15），各套件求和 == 总断言已自检。
- 覆盖面天花板 `--ceiling 7 35` → **0 0**，实测 `named=18 zh_items=70 unhooked=0 unhooked_items=0`。
- 第三批 T3：`combo-items-en.log` `RC=0`，7/7 可达、44 项、0 项中文；两个前置下拉已选回原值；
  设置按字节还原并复算 sha256 逐字相同；用户实例起回。
- 英文模式残留（两把独立尺子）：产品自报 **11 → 8**，UIA 全窗走树 **6** 条
  （其中 1 条是 ScrollBar 的框架自报名「垂直」，其余 5 条是 HDR/ACM 状态行那类 `.cs` 运行时拼串）。
- 仍未闭环，别当已验：**F3** 两处前台改动本机不可验（判据依赖焦点，正是本仓已判过不可信的那类）；
  **F4** `ShowPreview` 按 §54.4 暂缓；**②** 符号字体根因开放；**F5** 只做了脚本侧，真实发布未跑；
  **Core 里 `$"距离: {q:F1}"` 那两条内插串的根治**（让 Core 别产出用户可见文案）是签名级改动，未做。


---

## 9.12 把上轮两项没做完的叫回来做完（14:50–15:40）

用户：「填完整这一部分，完成实现」。指的是我上轮末尾自己列的两项：**R2 的 Core 剩余文案**与 **F5 的真实发布**。

### R2：Core 不再产出文案这件事，做到底了

第二批我给 `FormatColorFacts` 留的权宜是"App 按片段回翻"。这次去看了它到底有多少条：
**31 条，其中 7 条带 `{expr}`**。带值的那 7 条整串永远匹配不上，只能拆片段 —— 而拆完之后
每条英文是半句话，拼起来读不通（`PNG：cICP ` + 值 + `（原色/传输/矩阵）…` 这种）。
所以真正的修法只有一个方向：**Core 交模板，App 代值**。

- Core 侧：`Declaration(..., Func<string,string>? tr = null)`，带值的字面量改写成 `{0}` 模板，
  内部统一走 `L(template, args...)` = 先 `tr(模板)` 再代值。
- **默认原样返回**是这条改动的关键：`Declaration` 同时喂界面与日志（文件注释自己写着
  "用于界面与日志"），日志与 `SelfTest` 那张表不传钩子 ⇒ 中文逐字不变；只有色域映射卡传 `LocaleManager.Tr`。
- 判据三段：3c 反向锁（Core 每条中文声明必须被 App 清单点名、表里有译文、**模板与译文占位符数量一致**
  —— 少一个 `{1}` 漏代值、多一个 `{2}` 当场抛 `FormatException`）、夹具④、
  以及把 `Declaration` 调用点那条镜像判据改成【必须带 Tr 实参】（旧的那条钉的是三参形式，
  加参数就会红 —— 这正是它该有的行为，不是它错了）。

### 一个"结论对、尺子错"的样本

修完之后英文残留还剩 3 处：CommandBar 溢出按钮「更多」、ToggleSwitch 默认「开 / 关」。
我判定它们是框架自带（随 OS 显示语言取词），并加了一条白名单判据去钉"不许把自家漏翻塞进白名单"。

**第一版那条判据用 `Contains` 子串匹配 ⇒ 恒红**，我一度据此怀疑"这三串不在我们代码里"的结论错了。
错的是尺子：白名单里有 `开`/`关` 这种单字，而全仓"未开启""开关"遍地都是，子串必然命中。
改成【完整字面量】匹配（`"X"` / `>X<` / `="X"`）并把白名单自身那一段从使用面里切掉之后，
逐字确认：三串在 App 的 .xaml/.cs 里**只出现在白名单那一行**。结论站住了。

同时补了一条**正向夹具**：我们真写死的 `"能力检测中..."` 与 `"截图后右下角缩略图预览"`
必须被同一把尺子认出来 —— 认不出来就说明"不在我们代码里"是恒真的装饰。这条判据现在两头都有牙。

### 顺带查出一个真缺陷：语言初始化排在一句界面文字之后

残留从 5 降到 1，最后那 1 处是启动时状态栏的「能力检测中...」。
根因不在文案表：`MainWindow` 构造里 `LocaleManager.SetLanguage` 排在 :359，
而 `SetStatus(LocaleManager.CapabilityDetect)` 在 :288 —— **取词发生在切语言之前**，
于是英文模式启动那几秒状态栏恒为中文，之后再没人重刷。
把语言初始化提到第一句界面文字之前（`LanguageChanged` 全仓零订阅，早发一次没有副作用）。

修完：**产品自报残留 8 → 0**（15:25 那一跑没有任何 Locale 告警），
**UIA 全窗 6 → 1**，剩那 1 条是 `ScrollBar` 的框架自报名「垂直」，不是我们的串。

### F5：真实发布跑通了，而且逮到发布脚本自己的 bug

`Publish.ps1` 在 `$ErrorActionPreference='Stop'` 下用 `& dotnet run ... 2>&1 | Out-String`
跑测试套件 —— 子进程往 stderr 写**降级留痕**（`[NativeEncoderGuard] ⚠️ WebP 崩溃隔离 …`）
会被 PowerShell 变成 ErrorRecord 并当场抛出 ⇒ 一次 **1154 通过 / 0 失败**的绿轮被判定成
"发布失败"，包根本没出。改成只在**退出码 / 有 RESULT 行 / failed==0** 三条上判决，stderr 只作输出。

真实发布：`publish/TrueToneCap-v0.0.0-r5verify`，**606 文件 / 414 MB**，
`build-info.json` 记 `commitSha=ca7ed99` + `dirtyWorktree=true` + `smokeTested=true`。
样张排除在**包内**实测为空，正对照是源树里那份 `src/…/win-x64/color-samples` ——
这条判据从此不再是"写了没跑过"。

**我自己造的一次假失败要留**：第一次重跑发布时报 4/7 套件失败，我差点去查产品。
实际是我在发布还在跑的时候改源码，`dotnet run` 每轮重新构建，撞上我半路的编译错误。
不碰工作树重跑即全绿 ⇒ **发布期间不许动工作树**。

### 账

门禁 `gate-final2.log` = **GATE_RC=0 / 1738 断言 / 7 套件全通过 / 无未判格子**；
地板 1726 → **1738**（unit 1163 → 1175，+12），各套件求和 == 总断言已自检。

**仍未闭环**：② 的根因已找到并修掉（见 §9.13 前一轮的记录），但产品里 46 枚图标仍是 PathIcon 轮廓，
没有回退到 FontIcon —— 那是既定形态，不再改；F3 的"用户双击时能否抢到前台"本机仍无读数；
`FormatColorFacts` 的模板化只做了 `Declaration`，Core 里其它喂界面的中文（若有）未穷举。
