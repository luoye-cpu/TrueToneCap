# 管线审查与实机可用性 · 报告（2026-10-01）

计划见 `docs/reviews/usability/pipeline-and-usability-review-plan-2026-10-01.md`。本文是本轮唯一完整交付；台账只在 §4 新开一节指过来。

## 0. 判词（先看这几句）

1. **主链路可用**：六个格式（PNG / JPEG LI / JPEG XL / AVIF / WebP / TIFF）在真机上从"一次热键"走到"落盘 + 剪贴板"，**6/6 交付的确实是要的那个格式**，外部裁判都能解码，内容不是平白一片，色彩声明逐枚用字节扫过（§1）。选区那条腿的两发键（Enter 保存 / Ctrl+C 复制）**本轮第一次都判成**（§2）。
2. **本轮修掉一个 P1**：用户在界面选的**输出格式每次开机被抹成默认，而且下一次保存会把盘上那个键删掉**。根因是一行回落表达式；改前改后都有真机读数，新锁 7 条并做了变异（§3）。
3. **两个"每次都发生"的界面缺陷，都只进日志、界面不解释**：`XamlControlsResources` 加载失败在**发布包里 23/23 次启动、开发包里 31/31 次启动**全部发生（§6）；两个下拉的值对不上选项 ⇒ 显示空白且保存时被兜底覆盖（§4 表末两行）。
4. **一次真实崩溃**（NVIDIA 用户态驱动里的栈溢出），本轮 ~17 次启动里 1 次；随后 8 次连拍全活 ⇒ 记"偶发、非因果"，不记"已修"（§5）。
5. **未评**：英文界面目视、OCR/翻译真实产出、多屏与纯 SDR 屏、标注入口的观感、以及"降级浮层到底弹没弹到用户眼前"（§8）。

## 1. W1 实机矩阵：一次热键 = 五环全走

**为什么用无感截图（Ctrl+Shift+Q）当矩阵的行**：它一次跑完"捕获→色彩→编码→落盘→剪贴板"，且不需要拖拽、不需要工具栏坐标、不依赖前台焦点 ⇒ 把 §2 那类"键到底进没进去"的第三方变量排除在外。
**每格三件证据**：① 外部裁判（`C:\PLAN\FFmpeg` 的 ffprobe/ffmpeg，不是产品自带解码器）；② 内容结构（与 preview-content 同一把尺）；③ 产品自己那一行日志。
**档**：本机 HDR 屏在位（分类 Hdr），`HdrSwitch=开` ⇒ 这一趟全部走 HDR 交付。`TTC_FORCE_SDR` 对这条腿**无效**（它只被预览/选区那一支读，见 §7-③）。
出处：`tests/output/realmachine-2026-10-01/w1a-matrix-hdr.log`，格子 6 枚、非 OK 0 枚，设置按字节还原并复算 sha 相同。

| 请求 | 产品自报 | 落盘 | 外部裁判（ffprobe） | 声明层（**逐字节扫原件**） | 内容 | 剪贴板 |
|---|---|---|---|---|---|---|
| PNG | PNG | .png | png 3840×2160 rgba64be / PQ / bt2020 | cICP + sBIT | 128 色，非平白 | 位图 3840×2160 + 文件 |
| JPEG LI | JPEG_GAINMAP（增益图开关是开的） | .jpg | mjpeg yuvj420p | **ICC + XMP + ISO 21496 + MPF** | 295 色 | 位图 + 文件 |
| JPEG XL | JPEG_XL | .jxl | jpegxl rgba64le / PQ / bt2020 | colour 盒（`,,`） | 164 色 | **只有文件，无位图** |
| AVIF | AVIF | .avif | av1 yuv444p10le / bt2020nc / PQ | colr + nclx + av1C | 137 色 | 位图 + 文件 |
| WebP | WebP | .webp | webp yuv420p | ICCP | 235 色 | 位图 + 文件 |
| TIFF | TIFF | .tiff | tiff rgba64le | ICC 标签在 | 128 色 | 位图 + 文件 |

**这一表里唯一对用户有后果的缺口是 JPEG XL 那一行**：剪贴板只有文件、没有位图 ⇒ 粘进聊天框/画图不会出图。这与代码里那句"AVIF / JPEG XL 等格式系统剪贴板未必能解码 ⇒ `SetBitmap` 会抛"的注释方向一致，但**本轮只量到 JXL 这一格真的没有位图通道，AVIF 有**（旧注释把两者混在一起说）。

⚠ 声明列不拿 ffprobe 的 `icc_profile` 当尺：矩阵第一版打的"ICC 侧数据=False"六格全 False，那是 **ffprobe 对 cICP/nclx/ICCP 不报 side-data** 造成的假读数；上表的声明结论全部来自对原件字节的独立扫描。

## 2. 选区两条腿：09-30 那句判词是错的，本轮更正

`tests/output/realmachine-2026-10-01/w1b-region-copy.log`（`-WaitSec 30`）：

- 轮 1（Enter = 保存）：目标窗 `TTC_HC` 3840×2160 **dpi=144**，按键前/选区后前台窗都就是它（`本进程是否前台 = True`）⇒ 产品自报 `HDR 选区保存` ⇒ `HDR 编码完成 … .png` ⇒ 新产物落盘。**通过**。
- 轮 2（Ctrl+C = 只复制）：同一会话第二发，`HDR 选区复制到剪贴板` → `编码设置: PNG …` → `开始编码` → `窗口关闭` → `编码完成 (213.9 KB)` ⇒ 剪贴板**位图 1400×900 + 文件 1 个**。**通过**。

**更正**：09-30 23:17 那一趟留下的判词是"拖拽没选中 ⇒ 本格未判"。它自己那行日志里已经印着 `选区完成 1445x959 @(窗口内 1243,541)` ⇒ 前提成立，那条分支逻辑上就死了。真正的两票是：① 当时把 `HDR 选区复制到剪贴板`（**开始**行）当命中就停止等待，于是编码还在跑就去读剪贴板 ⇒ 读出"空"是尺子坏了；② 补的那一发 Enter 排在一次白等 30 s 之后，被预览窗的 **60 s 空闲自关**吃掉。两条都在本轮的夹具里改掉了（等待门槛排到完成行；两轮之间先等收尾）。
**顺带量到的一条产品事实**：预览窗 60 s 空闲自关是真的，且它会把"用户想清楚了再按"的键吞掉——界面上有没有倒计时/提示，本轮未看（§8）。

## 3. P1：盘上存的输出格式在启动时被抹掉（已修）

**现象（两趟独立实机读数，都是产品自己写的键）**
- 06:50：盘上 `FormatIndex=2`（JPEG XL）⇒ 产品自报 `捕获配置: 格式=PNG`，产物 `.png`。
- 06:54：界面选 AVIF ⇒ 产品把 `FormatIndex=3` 写进文件；06:56 只读探针（不 Select）跑完，**那个键整个从文件里消失**。
- 07:00 改前复现：写 5（TIFF）⇒ 界面显示 PNG，会话结束键没了。

**根因（一条回落表达式）**：构造期先跑 `RebuildFormatCombo()`，那时控件尚未实化 ⇒ `SelectedIndex` 读回 -1 ⇒ 旧写法按 `FormatOrder.DefaultIndex` 回落成 PNG。而这一次**程序性赋值**会触发 `OnFormatChanged`（此刻 `_uiReady` 已经是 true）⇒ `_settings.FormatIndex = 0` **并且 `SaveQuiet()` 落盘**。差量写盘 ⇒ 0 就是默认 ⇒ 键被删。三段少一段都不成立，所以"界面显示错"、"编码用错"、"文件里键没了"三件事同源。

**修法**（`MainWindow.xaml.cs` RebuildFormatCombo，一行 + 说明）：`keep < 0` 时回落取 `_settings.FormatIndex`（持久化真值），不取表里的默认项。第一次构造调用时 `_settings` 已是 `SettingsService.Load()` 的结果（`LoadSettings()` 是空壳），所以这一处不会引入新时序。

**改后同一夹具复跑**：写 5 ⇒ 界面显示 **TIFF** ⇒ 会话结束 `FormatIndex:5` 还在。
**判据**：新卡 `Test_FormatCombo_RebuildKeepsThePersistedFormat`（7 条）+ 那条 09-26 的镜像断言原本钉的就是 `FormatOrder.DefaultIndex`，一并改判到 `_settings.FormatIndex`。
**变异**：把回落改回 `DefaultIndex` ⇒ `--unit-tests` 当场红 3 条（本卡 2 条 + 镜像那条），还原后 1089 全绿。地板 1645→1652。
**这条锁的天花板**：它是 T2 文本锁，钉得住"回落取哪个值"，钉不住"这次赋值会不会又触发写盘"——后者住在 §2 那类实机格里。**改这段代码必须重跑 `w1-format-persist-cell.ps1`，别只跑门禁。**

## 4. 降级与失败可见性（本轮实测的行）

| 位置 | 触发构造 | 实际行为 | 进日志 | 界面 | 结论 |
|---|---|---|---|---|---|
| 用户真实 `OutputPath="D:\TTC"`（本机无 D 盘） | 不改动任何设置，按一次热键 | `Error 无感截图编码失败: Could not find a part of the path 'D:\TTC'` + `ToastService 已提交系统通知: ❌ 截图失败` | ✅ | ✅ 通知 | **可见**（不是静默）；但"每次保存必失败"这件事本身仍是这台机器的现实 |
| `cjxl.exe` 不在位 | 把 `native/cjxl.exe` 改名再跑 JXL 格 | 三条留痕：编码器警告 ⇒ `PNG 保底（HDR 帧）成功，实际落盘 …` ⇒ Pipeline 说明"本次已改存为 PNG（不是 JPEG XL）" | ✅ 三条 | **未判**（见下行） | 换格式这一步**日志诚实**；我的夹具在这一格从 OK 翻成 `format-changed-to.png` ⇒ 新加的④号判据有牙 |
| 降级提示到底弹没弹 | 同上 | `NotifyEncoderFallbackIfAny` 走完 `LogService.Warn` 后调 `ToastService.ShowCaptureDegraded(reason)`，但**该调用不留回执行**（对比：失败路径会打"已提交系统通知"） | 半个 | ? | **未判**：那一趟进程是提权起的 ⇒ 通道被改派"应用内浮层"，而浮层没有日志回执。要判得再跑一趟非提权 + 数浮层窗 |
| `OverlayColorCbo` | 每次启动 | `设置值「#99001833」在下拉 OverlayColorCbo 的 7 个选项里没有对应项 ⇒ 该下拉将显示为空白，且保存时会被兜底值覆盖` | ✅ | 只有日志 | **与我刚修的 P1 同一形状**（持久值 ↔ 下拉项对不上 ⇒ 显示空白 + 保存覆盖）。本轮只登记，未动 |
| `OcrLangCbo` | 每次启动 | 同上，值为空串、选项数 0 | ✅ | 只有日志 | 同上 |
| QSV/系统 MFT AV1 | 每次启动 | `Qsv: IsAvailable=false` / `QSV: 文件有效但并非硬件产出 ⇒ 判为不可用` | ✅ | — | 本机量不到真 MFT（旧口径不变） |

## 5. 崩溃与启动稳定性

- 07:18 那一趟 W1b 里产品起来约 15 s 后消失，`Stop-Process` 报"进程不存在"。**WER 归档为证**（不是 harness 假信号）：`AppCrash_TrueToneCap.exe_…`，`异常代码 c00000fd`（栈溢出），`故障模块 nvwgf2umx.dll` 32.0.16.1692（NVIDIA 用户态驱动），`IsFatal=1`。
- 该趟最后一条产品日志停在 `所有显示器均使用系统默认 ICC`，**下一行本该是 `AvifProbe Nvenc: OK`** ⇒ 崩在 AV 探针/设备创建这一段附近（相关，不是结论）。
- 随后 `w3-startup-stability-cell.ps1` 连起 **8 次：8 活 / 0 崩 / 0 未定**（每 18 s 查存活 + WER 归档计数当第二把尺，归档数全程 3 未增）。
- **判词**：本轮总启动约 17 次里 1 次崩溃 ⇒ 偶发。按本仓规矩（n<5 不写成因果、崩溃与判红分开计数）只登记现象与落点，不据此改任何代码。

## 6. 界面目视（唯一一张图）

`tests/output/realmachine-2026-10-01/mainwindow.png`（PrintWindow flags=2 抓被遮挡窗，不动用户前台；`ExStyle=0x100` ⇒ 无 `NOREDIRECTIONBITMAP`，全黑采样 1.14% ⇒ 这张图能当读数）。

- 整体**是有样式的**：卡片、ToggleSwitch（"开"）、强调色按钮、状态栏"就绪"都在 ⇒ 我一度怀疑"XamlControlsResources 失败 = 界面裸奔"是**错的**，据图更正。
- **左侧导航栏 5 个图标全部渲染成空方框（豆腐块）** ⇒ 图标字形没取到。这正是用户 09-30 报的"复古/缺东西"里能被客观指认的那一半。
- 页面上"保存路径 D:\TTC"、"格式 PNG (无损)"、"HDR 已启用 (10-bit) | ACM 已启用 | SDR 白点 120 nits | 峰值 417 nits" 与本机登记一致。
- **每次都失败的那条**：`XamlControlsResources 加载失败 (非致命): 灾难性故障 (0x8000FFFF)` —— 发布包 v0.3.6-beta 的日志 **23/23 次启动**都有，今天开发包 **31/31 次**都有 ⇒ **不是开发布局的水土不服，用户装的包里同样发生**。代码把它归为"非致命"并只留一条 Warn。
- 未评：豆腐块是否就是用户说的那个问题、以及它和 XamlControlsResources 的因果（本轮只证到"两件事每次都同时发生"）。

## 7. 本轮我自己造成的事（全部已改或已登记）

1. **矩阵第一版是假绿**：只查"文件在/能解码/不平白"，没查"是不是要的那个格式" ⇒ 06:50 那次"要 JXL 拿到 PNG"被打成 OK。补 ④号判据（请求↔自报↔落盘后缀三方对撞），并在 `cjxl` 移开那一格验证它会翻红。
2. **它的退出码也不 fail-closed**：非 OK 6 格里有 1 格时仍打 `RC=0`。已改成按点名条数定退出码。
3. **`-Arm Sdr` 这个名字撒了谎**：`TTC_FORCE_SDR` 只有预览/选区那一支读，无感截图不吃它 ⇒ 那一档标签去掉，矩阵改标 `Hdr`（真实状态）。
4. **ffprobe 的 `icc_profile` side-data 不能当"有没有色彩声明"的尺**（六格全 False，实际六格都有声明）。改用字节扫描，两把尺对齐后才写结论。
5. **把"开始"行当"完成"行等** ⇒ 读早了剪贴板，造出一次假的"复制腿坏了"。这正是 §22 记过的同一课，我又犯了一次。
6. **一次等待放错位置** ⇒ 白等 30 s 后一发键被 60 s 自关吃掉，差点把产品记成"Enter 不通"。
7. **`open(p,'w').write(未定义变量)` 把 `scripts/gate-baseline.json` 截成 0 字节**（`open` 的截断发生在参数求值之前）。靠一步之前顺手 `cp` 的临时件恢复（4336 B，复算 1645/1082 逐字回来）。生成脚本一律改成"临时件 + `os.replace`"。
8. 夹具的 P/Invoke 类型两次以"没有数据"的形式伪装编译失败（`Add-Type` 缺 `-ReferencedAssemblies System.Drawing`；`[GW]::M $a` 在表达式模式里必须写成 `[GW]::M($a)`）——两处都被"类型没编出来 ⇒ 未判"那道守卫拦住，没有伪装成读数。

## 8. 仍需眼睛 / 未判（下一轮的输入）

1. 降级浮层是否真上屏（非提权重跑 + 数浮层窗，§4 第 3 行）。
2. 导航栏豆腐块与 `XamlControlsResources 0x8000FFFF` 的因果，以及用户观感（§6）。
3. 预览窗 60 s 自关在界面上有没有倒计时/提示（§2）。
4. 英文界面计数那次读数（`en-ui-count.ps1` 至今没在本轮跑）。
5. OCR 三引擎与翻译三档的真实产出；真联网那条要用户点头（会向第三方发一条文本）。
6. 多屏 / 混合 DPI / 纯 SDR 屏：本轮全部在单屏 HDR + 144 dpi 上量，结论绑定这台机。
7. JPEG LI 增益图那枚 `.jpg` 的观感（声明层齐全，像素对不对没看）。
8. `--service-tests` 只剩 2 条（合并与否是结构决定，不是缺陷）。

## 9. 本轮账目

- 门禁：改前基线 **round48 = 1645 断言 / 全绿 / 无未判格子**（`tests/output/round48-gate.log`，`GATE_EXIT=0`）；改后 unit 单套件 **1089**（+7），地板 1645→1652；全量复跑见 `tests/output/round49-gate.log`。
- 源码改动：`MainWindow.xaml.cs`（1 处回落 + 注释）、`CorePipelineTests.cs`（新卡 7 条 + 镜像断言改判）。**未提交**，HEAD 仍 `ca7ed99`。
- 新夹具：`w1a-silent-matrix-cell.ps1`、`w1b-region-copy-cell.ps1`、`w1-probe-uia.ps1`、`w1-format-persist-cell.ps1`、`w3-startup-stability-cell.ps1`、`w5-mainwindow-shot-cell.ps1`。
- 本机状态改动（都可逆，逐条有复算）：`native/` 里补进 4 枚随包 exe（对齐 `Publish.ps1` [3/6] 的单一事实源 `Core/Resources/`，sha 记在正文）；`cjxl.exe` 改名一轮后按原名放回并复算 sha 相同；设置文件每轮按字节还原并复算 sha256 相同；剪贴板轮前拍快照、轮末还原。

---

## §10 追加（同日 10:30）：用户回话「2.是的，3.修，4.按照你的想法改」之后的处置

### 10-② 导航栏豆腐块：先找到那条"每次都响的假警告"

用户确认豆腐块就是他说的那个"复古"。查下来两件事是同一处的两面：

- `App.xaml:24-26` 已经用 `<XamlControlsResources/>` 合过一份主题资源；
- `App.xaml.cs` 又 `Resources.MergedDictionaries.Add(new XamlControlsResources())` 加了**第二份** ⇒ 第二次构造必抛 `0x8000FFFF`，被 catch 成一条 `非致命` Warn。
  这解释了那个矛盾现象：**样式其实是好的**（截图里卡片/ToggleSwitch/强调色按钮都在，卡片描边用的 `{ThemeResource CardStrokeColorDefaultBrush}` 能解析），
  而日志里那条"加载失败"每次都响——它既不是真失败，又把真问题（图标字形）埋在同一条噪声下面。

处置：删掉代码侧第二次 Add，并留一条 `MergedDictionaries.Count == 0 ⇒ LogService.Error` 的守卫
（否则将来谁把 XAML 那份删了，就真的是"样式全没"而日志安静如常）。新卡 `Test_XamlControlsResources_MergedExactlyOnce`（4 条），
变异 M-A（把第二份 Add 塞回去）只红 1 条。
**豆腐块是否随之消失，要真机取图才算**——见 10-⑤，被在跑的实例挡住了。

### 10-③ 两条空白下拉：与 §3 同一条处方

`SetComboByTag` 的旧行为是"匹配不到就什么都不做"⇒ 下拉空白，而空白那一瞬正是保存侧 `?? 字面量` 改掉用户值的时机。
本机 `OverlayColor="#99001833"` 不在那 7 个选项里，而**那个字面量自己就是 `SaveSettings` 的兜底**——
它既不在选项里、也与模型默认 `#80000000` 不同向，等于每次空白保存都把脏值再生产一遍。
`OcrLanguage` 同形：ONNX 引擎下那个下拉是空的（`PopulateOcrLanguages` 会 Clear），旧写法每次保存把存过的语言抹成空串。

改了三处（都在一个点上，18 处 `?? 字面量` 那条路径整体不再被触发）：
1. `Items.Count == 0` ⇒ 早退，不报 Warn（"控件本来没项"不是"值域漂移"，混在一起真漂移就被固定噪声埋掉）；
2. 比较改成与校验侧同一把尺（`OrdinalIgnoreCase`）——`CorePipelineTests` 那条"白名单 vs Tag"的注释早就点过这个形状，今天才修；
3. 未匹配 ⇒ **把盘上原值补成一项并选中**（`Content` 只用原值本身，不新增中文串 ⇒ 不碰翻译表那条生路）；
   外加两处保存侧改成"没有选中项就不动原值"。
新卡 `Test_ComboByTag_NeverLeavesBlank`（9 条）。**这一卡第一版漏了一发**：M-C（`Tag = tag` → `Tag = null`）照样全绿，
因为断言只看了"有没有 Add 一项"，没看补的那一项挂不挂原值——而 null Tag 会让保存侧读回空，比留空白更坏（它看起来是好的）。补一条断言后 M-C 才红。

### 10-④ 开发构建缺原生编码器：改注释 + 补布局（我的取舍）

`NativeLibraryResolver` 的文件头写着"内嵌在 Resources/ 下的 exe 在首次运行时提取"，而 **`TrueToneCap.Core.csproj` 里没有任何 `EmbeddedResource`** ⇒
那条"从嵌入资源提取"的分支从未命中，真正在跑的只有后面的文件系统回退链，而喂它的只有 `Publish.ps1 [3/6]`。
后果本轮实机量到：裸 `dotnet build` 的 `native/` 是空的 ⇒ AVIF/JXL/WebP/JPEG LI 四格必走保底（我一度把"选 JXL 拿到 PNG"读成产品行为）。
处置：① 文件头与类注释改成实测口径；② App 工程加 `CopyNativeToolsToDevOutput`（AfterBuild，从 `Core/Resources/*.exe` 拷进 `$(OutDir)native\`，
单一事实源不变，计数硬断言 4 枚，少一枚就 Error）。这样下一轮任何人从开发构建起产品，量到的都是与发布包同一份布局。

顺带登记一条**没动**的隐患：`TrueToneCap.App.csproj:166` 的 `RemoveDir "$(PublishDir)Native"`（注释写"死目录清理"）在大小写不敏感的 NTFS 上
就是删 `native/` 本身。今天安全，只因为 `Publish.ps1` 是先 publish 后拷 native；**谁把顺序倒过来，四枚 exe 就会被这条清理静默删掉**。

### 10-⑤ 账目与还差的那一步

- unit：1089 → **1102**（+13）；地板 1652 → **1665**。四发变异 M-A/M-B/M-C/M-D 各自只红 1 条，还原后 md5 与备份逐字相同。
- **还差的**：App 重新构建 + 真机复验（那条 Warn 是否消失、豆腐块是否随之不见、OverlayColor 是否显示原值、保存后盘上值是否还在）。
  被挡住了：用户自己 10:27 起的实例 `PID 27560` 占着 `TrueToneCap.exe`（构建报 MSB3027 文件被锁），
  且产品有单实例 Mutex ⇒ 我起的第二个实例会立刻退出、量不到任何窗口。**不擅自杀用户的进程**，等一句"可以关"或他自己关掉。

---

## §11 复验轮（同日 11:00–11:35，用户："关掉那个进程，你继续验证"）

### 11-① 我先把自己的改动打死了一次产品（最重要的自我更正）

关掉用户的实例后重编，`w6-verify` 量到：**应用起不来**——日志停在 `NativeResolver` 那四行之后，
`主窗口已创建` 不再出现，托管 `UnhandledException` 没跑（`crash.log` 时间戳没动），
WER 归档给出 **`combase.dll` / `8000ffff`**。同一个桶在 11:05、11:09、11:12 各死一次。

隔离过程（每一步都只动一处、各自重编重跑）：
| 试的假设 | 结果 |
|---|---|
| 是我删掉第二次 `Add` 造成的 | 把 Add 恢复后**仍崩** ⇒ 排除 |
| 是 ③ 的"补一项"造成的 | 禁用补项后**仍崩** ⇒ 排除 |
| 是 ③ 的其他改动 | 崩点未变 ⇒ 排除（③ 无罪，且后来被证明是有效修复）|
| 是我新加的那条 `if (MergedDictionaries.Count == 0) LogService.Error(...)` 守卫 | **撤掉后立刻恢复**（64 行日志、主窗在位、四项判据全部可测）⇒ 就是它 |

机制：这一段排在 `this.UnhandledException += ...` **之前**，而它读的正是那个每次都要失败一次的 WinRT 集合 ⇒
同一个 `0x8000FFFF` 换个地方抛出、且那里没人接。**"多加一条守卫"在这里不是更稳，是更脆。**
判据随之反向改判：`MergedDictionaries` 只许出现在那对 try/catch 里（全文件 1 处引用，越界引用即红）。

### 11-② 豆腐块：四条假设全部证伪，仍是开放缺陷

| 试过的修法 | 读数 |
|---|---|
| 删掉代码侧第二次 `Add`（以为样式来自 XAML 那份） | 崩，见 11-①；已撤 |
| 8 枚 `FontIcon` 都写死 `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"` | 截图零变化（`mainwindow-after-fontfix2.png`）|
| 注释掉 `App.xaml` 那份**没有 `BasedOn`** 的隐式 `FontIcon` 样式（怀疑它连模板一起换掉） | 零变化（`mainwindow-styleoff2.png`）|
| 自己在三份 ThemeDictionaries 里补 `SymbolThemeFontFamily`（应用已用同机制覆盖另两个字体键） | 零变化（`mainwindow-symbolfont.png`）|

另外两条**前提**也核了：`Segoe Fluent Icons` 与 `Segoe MDL2 Assets` 两个字体本机都装
（`InstalledFontCollection` 报 True/True，`C:\Windows\Fonts` 里 `SegoeIcons.ttf`、`segmdl2.ttf` 都在），
且 `MainWindow.xbf` 比 `.xaml` 新 ⇒ 我改的东西确实进了运行时。
最后为排除"夹具拍不到"，同一扇窗用**第二种取图法**（直接抓屏幕那块矩形）再拍一次：
`mainwindow-screen.png` 与 PrintWindow 那张**同样**是豆腐块 ⇒ 用户屏幕上确实缺字形，不是假象。

**结论**：② 未修。已排除的是"字体没装""XAML 没重编""取图假象""隐式样式吃掉模板""缺 SymbolThemeFontFamily"这五条；
剩下的最强线索仍是那条每次都抛的 `XamlControlsResources`——但 11-① 证明它**不能删、也不能改 catch 位置**，
要修得先回答"为什么在这个环境里构造它会失败"（怀疑方向：WinAppSDK 运行时版本与 `Microsoft.winappsdk-*` 元包不匹配）。

### 11-③ 两条空白下拉：真机四项里过了三项

`w6-verify-c3.log`（同一格一次跑完 A/B/C/D）：
- **C ✓** `OverlayColorCbo 项数=8 选中=[#99001833] 含盘上原值=True`——补的那一项在，且被选中。
  中途一个真缺陷被这格抓出来：先写成 `item.IsSelected = true` 时读数是"项数=8、**选中=[]**"，
  因为那一页在启动时还没实化 ⇒ 改判为控件级 `cbo.SelectedIndex` 才落地（与 §3 那条 P1 同族）。
- **D ✓** UIA 真按了一次「保存设置」之后，盘上 `OverlayColor` 逐字还是 `#99001833`（旧写法这里会被抹成兜底字面量）。
- **无「0 个选项」噪声 ✓**：`OcrLangCbo` 在 ONNX 下为空，旧代码每次启动报一条假漂移，现在不报了。
- **A ★**：那条 `XamlControlsResources` 警告仍在（11-② 未修的直接后果，不是回归——它一直在那儿）。
- 未判：`OcrLangCbo` 本身翻到 OCR 页也没进可视树（子卡收起时整块不在树里，本仓记过同型），所以"语言下拉显示什么"这一格本轮量不到。

### 11-④ 开发构建的后端布局：闭环

`rm native/*.exe` → `dotnet build` → `native/` 回到 4 枚，逐枚 md5 与 `Core/Resources/` 相同；
运行时日志从四条 `⚠️ X.exe 未找到` 变成四条 `✅ X.exe 已就绪`。④ 达成：开发构建与发布包同一份布局。

### 11-⑤ 账目

unit **1104**（+2 条：反向禁止 `item.IsSelected`、`MergedDictionaries` 引用点唯一），地板 1665→**1667**；
全量门禁 `round50/51/52` 三轮 `GATE_EXIT=0`、**1667 断言 / 无未判格子 / 轮前后设置逐字不变**（round50、51 跑在地板改判之前，round52 是"实得==地板"那一轮）。
三次 XAML 试验已全部撤回（备份留在 `tests/output/realmachine-2026-10-01/App.xaml.before-fonttest`、`MainWindow.xaml.before-fontfix`）。

### 11-② 补记（同日 20:07–20:33）：又证伪四条，线索收窄到"这个进程用不了符号字体"

用户 20:07 又起了一个实例（PID 17536）。这次我的夹具按新加的起手自检**直接拒跑**并写明原因
（"起手就有 1 个实例在跑 ⇒ 单实例 Mutex 会让本次起的进程立刻退出"），没有再把"0 个窗口"当成读数。
经授权关掉后继续做区分实验，全部用同一扇主窗取图对比：

| # | 试的东西 | 读数 |
|---|---|---|
| 1 | 8 枚 `FontIcon` 写死 `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"` | 零变化（`mainwindow-after-fontfix2.png`）|
| 2 | 注释掉 `App.xaml` 那份**无 `BasedOn`** 的隐式 `FontIcon` 样式（怀疑它吃掉模板） | 零变化（`mainwindow-styleoff2.png`）|
| 3 | 自己在三份 ThemeDictionaries 里补 `SymbolThemeFontFamily` | 零变化（`mainwindow-symbolfont.png`）|
| 4 | **区分实验**：把第 1 枚的 `Glyph` 换成中文「设」 | **它显示成了「设」**（`mainwindow-glyphprobe.png`）|
| 5 | 撤掉 `App.xaml` 里那条全局隐式 `Style TargetType="TextBlock"` 的 FontFamily | 零变化（`mainwindow-rootcause2.png`）|
| 6 | 第 4 枚换成 `<SymbolIcon Symbol="Setting"/>`（另一个元素、另一条渲染路径） | **同样是豆腐块**（`mainwindow-symbolicon.png`）|

读数 4 是这一串里唯一有信息量的：**图标元素确实在渲染它的文字，但用的是一族中文字体**——
所以"字体没装"（两个字体本机都在）、"XAML 没重编"（`.xbf` 比 `.xaml` 新）、"取图假象"
（PrintWindow 与直接抓屏两张一致）这三条早就排除了，现在**连"XAML 样式层写错了字体"也排除**：
`FontIcon` 与 `SymbolIcon` 两条独立路径都拿不到 PUA 字形 ⇒ 问题在**这个进程解析符号字体的能力**上，不在标记里。

因此 ② 的下一步不该再动 XAML，而要问：为什么这个进程用不了 `Segoe MDL2 Assets` / `Segoe Fluent Icons`
（怀疑方向：非打包 WinUI 的运行时解析路径 / WinAppSDK 元包与机器上运行时版本不匹配——
这与那条每次都抛的 `XamlControlsResources 0x8000FFFF` 可能是同一个根，因为 11-① 已证明
"删掉/挪动那处构造"会直接把应用打死，说明主题资源与字体解析都建立在同一个半坏的运行时之上）。

**六次试验的 XAML 改动已全部还原**（`MainWindow.xaml`、`App.xaml` 回到在库状态，
残留的 8 行 diff 是 09-29 的 `PngFallbackChk`，不是本轮试验痕迹）；还原后重编复跑门禁
`round53-gate.log` = `GATE_EXIT=0` / **1667 断言** / 无未判格子 / 轮前后设置逐字不变；
用户那个实例已按原样重新起回（PID 11860，`主窗口已创建`）。

### 11-② 再补记（20:40–20:55）：定位到"这个进程按名字解析不出任何符号字体"，XAML 层彻底出局

先更正我自己早一句的过头结论。我说"进程用不了符号字体"时漏看了一条反证——截图里 CheckBox 那个勾。
核对后那条**不构成反证**：WinUI 的 CheckBox 勾是 `Path` 几何，不是字形。真正的判据实验是这两步：

1. **把图标挪到 NavigationView 外面**：一枚 `FontIcon Glyph="&#xE713;"` + 一枚裸 `TextBlock Text="&#xE713;"`，
   都写死 `Segoe MDL2 Assets` ⇒ **两枚都是豆腐块**（`mainwindow-outside-nav.png`）⇒ 与 `NavigationViewItem` 的图标宿主无关。
2. **三种符号字体并排**：`Segoe MDL2 Assets` / `Segoe Fluent Icons` / `Segoe UI Symbol` 同画 E713 ⇒
   **三枚全是豆腐块**，而同一段里的 `MDL2:` `Fluent:` `UISymbol:` 这些拉丁字母与中文正文照常清晰
   （`mainwindow-3fonts2.png`）。

⇒ 定位收敛为一句：**这个进程按【字体友好名】解析符号字体失败，而普通文字走的是另一条回落路径所以看不出来。**
配合构建配置：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`（`TrueToneCap.App.csproj:14-15`），
且 bin 里带着自己的 `DWriteCore.dll` —— 自包含非打包模式下 WinUI 的文字服务走 DWriteCore，
这是目前唯一能同时解释"三族全挂 / 正文正常 / 与控件无关"的一层。

**下一步该做的（我没做，因为它要动运行时配置）**：把 DWriteCore 关掉再拍一次同一张图——
若三枚齿轮立刻出来，根因与修法就都在这一层；若仍是豆腐块，再往字体注册/回落数据那侧查。
到这一步之前，**不要再改 XAML**：本轮已在 XAML 层白烧了六次试验（见上表 1–6）。

试验痕迹已全部还原（`MainWindow.xaml` 残留 8 行 diff 是 09-29 的 `PngFallbackChk`，与本轮无关；`App.xaml` 零 diff）；
还原后重编 + 全量门禁 `round54-gate.log` = `GATE_EXIT=0` / **1667 断言** / 无未判格子 / 轮前后设置逐字不变；
用户那个实例按原样起回（PID 17580，活着）。

### 11-② 第三段（21:06–21:10）：DWriteCore 假设出局，机器侧字体也清白

按方案执行了两条**不碰产品源码**的判据臂：

1. **把 `DWriteCore.dll` 从开发 bin 里挪走**（`w7-dwritecore-arm.ps1`，按 sha256 复算还原，逐字相同）。
   读数：应用**照样起来**（主窗在位、子树 55、取图成功），而图标**仍是豆腐块**
   （`mainwindow-nodwritecore.png`）⇒ "自包含模式走 DWriteCore 所以解析不了符号字体"这条假设**证伪**。
   ⚠ 这一臂第一次跑是**废的**：内层夹具发现刚被关掉的实例还没退干净，按起手自检拒跑，
   而我的判词逻辑把这次"前提不成立"读成了"应用起不来 ⇒ 引擎依赖 DWriteCore"——一个反向的假结论。
   已改成"等实例真退出才继续 + 把『夹具拒跑』与『产品起不来』分成两种判词"后重跑，才是上面这条读数。
2. **用 GDI+（另一条完全独立的文字栈）画同一族同一码点**：`Segoe MDL2 Assets` + `U+E713`
   ⇒ 画出来是**一枚完好的齿轮**（`gdiplus-glyph.png`；墨迹采样 50 点，而 `.notdef` 那侧 28–30 点）。
   ⇒ 机器上的字体、码点、注册都没问题，**坏的是这个进程里的 XAML 文字栈**。

至此 ② 的排除清单是 10 条：XAML 标记与样式（6 次试验）、`NavigationViewItem` 图标宿主、
特定字体缺失、`.xbf` 过期、取图假象、DWriteCore、机器侧字体本身。
剩下的最强相关仍是那条**每次启动必抛的 `XamlControlsResources 0x8000FFFF`**——
主题资源与符号字体解析同属 WinUI 运行时这一层，而 11-① 已证明这层动不得（删/挪即崩）。

**因此给出两条可执行出路（不再盲试）**：
- **A｜绕开**：导航图标改用 `Path` 几何而不是符号字体。依据是同一张截图里 CheckBox 的勾（WinUI 用 `Path` 画）**一直正常**
  ⇒ 这条路已被这个进程自己证明可用。代价：5 枚（全应用 8 枚）图标要给出几何数据，观感需你眼睛过一遍。
- **B｜治根**：查 WinUI 运行时为什么在本机拒绝按名解析字体——方向是 WinAppSDK 2.4.0 自包含非打包这套布局
  （`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`），需要动运行时/打包配置，属你点头才做的范围。

---

## §12 走 A：图标改成 Path 几何（同日 21:30–22:00，用户："开始推进"）

根因（②）没修，改的是**渲染路径**：同一张截图里 CheckBox 的勾一直是好的，而 WinUI 的勾是 `Path` 几何 ⇒
把符号字形取成轮廓、图标换成 `PathIcon`，就不经过那套解析不出来的字体名。

**取数据**：`tests/manual/safety/w7-glyph-to-pathdata.ps1` 用 GDI+ 的 `GraphicsPath.AddString` + `Flatten()`
把 24 枚字形抽成 0..20 坐标系的 Path Data（`glyph-paths*.txt`）。
**先校尺再动手**：`w7-pathdata-roundtrip-check.ps1` 把取出来的数据回放成图，与字体直画并排比——
第一版右边是实心饼，我差点据此判定"数据丢了孔洞"；实际是**回放器的 bug**（对每个子图元各调一次 `FillPolygon`，
孔洞那圈被单独填满）。改成"所有图元拼成一条 `GraphicsPath`、只填一次"后与左边一致
（`roundtrip-E713-v2.png`）⇒ 数据忠实，才允许进产品。
**探针先行**：先只换导航栏第 4 枚，取图确认齿轮出现、其余四枚仍是豆腐块（`probe-pathicon.png`），
再批量换其余——避免一次改 33 枚之后才发现路线不通。

**落地范围**：`MainWindow.xaml` 8、`SelectionOverlay.xaml` 15、`OcrPreviewWindow.xaml` 6、
`AnnotationWindow.xaml` 3、`ThirdPartyNoticesWindow.xaml` 1，共 **33 枚**；`<FontIcon` 在这五个文件里归零。
**未换**：`SilentCaptureToast.xaml` 5 枚——它们带 `Style="{StaticResource ToastActionIcon}"` 与
`x:Name="IconGlyph"`（代码按通知类型回填 Glyph），要连样式和代码一起改，不在这次范围内；
新卡把这个例外**数量钉死为 5**，多一个就红。

**门禁**：新卡 `Test_XamlIcons_DoNotDependOnSymbolFonts`（14 条），unit 1104→**1118**，地板 1667→**1681**；
`round57-gate.log` = `GATE_EXIT=0` / **1681 断言** / 无未判格子 / 轮前后设置逐字不变。

**这一路门禁替我兜住的一次自伤**：批量替换第三处脚本时我漏写了 `Data` 属性的收尾引号
（`Data="F0 …/>`），而当时我只重编了 Test 工程没重编 App ⇒ 单跑套件全绿；
是门禁 `[1/2]` 的整解决方案构建报出 `WMC9997 “<”是无效的特性字符` 才发现（`round55` 那轮红）。
**改 XAML 必须编 App 工程，不能只编测试宿主。**

**只有"能看见"才暴露出来的产品事实**：导航栏 5 枚图标现形后，其中两枚的语义是错的——
`OCR / 翻译` 用的 `E945` 是一枚**闪电**、`运行日志` 用的 `E8D4` 是一枚**图钉**。
以前全是豆腐块，没人看得出码位选错了。要不要换码位属产品决定（见文末待你一句）。

---

## §13 收尾轮（跨到 2026-10-02 03:30–04:15，用户："一起做，子agent协助推进" → "你重新分析一下实现方式" → "做好完整规划推进任务"）

### 13.1 §12 那两枚错码位已换，且上面那句"图钉"是我写错了

`OCR / 翻译` 从 `E945`（闪电）换成 `E164`（"A字"），`运行日志` 从 `E8D4` 换成 `E756`（列表）。
**并且 §12 那句"`E8D4` 是一枚图钉"是错的**：复核 lane 把 `E8D4` 在 MDL2 与 Fluent 两种字体里都渲出来，
是一枚**头像/人像框**，不是图钉。当时我把它认成图钉，和把 `E7DE` 认成图钉一样，都是"没见过 ⇒ 凭码位段猜语义"。
这条留在文档里不删，是为了让"目视之前不要给码位起名字"这个错法留下证据。

`SilentCaptureToast` 的 5 枚例外也清零了：4 枚按 tone 切换的图标 + 关闭 + 3 枚动作图标全部改成 `PathIcon`，
卡里那条"例外数量钉死为 5"随之改成 0（现跑读数：该文件 `<PathIcon` 8 枚、内联 `Data="F0 ` 也是 8 枚）。
共享的 `ToastActionIcon` 样式**保留**、只是从 `FontIcon` 改成 `PathIcon`（它管 Width/Height 12 + Foreground，
与内联 `Data` 并存不冲突）。
> ⚠ 本段第一版把这里写成"一个元素只能挂一个 `Style` ⇒ 那三枚的样式被摊成内联属性、样式本体已删"——**那是错的**：
> 那种摊法只发生在已经回退的 B0 实验里，在库形态从来不是这样。写错的原因是拿实验中间态当成了落地态。

### 13.2 B0（把 46 枚轮廓收进 Icons.xaml）：做成了，然后**否决并回退**

用户让我重分析实现方式，量出来的三笔代价（不是感觉，是实测）：

| 代价 | 读数 |
|---|---|
| 体量 | 46 条 `Data`、共 **34,705 字符**、最长一条 **3,847 字符**，散在 7 个 XAML；去重后只有 **32** 条不同串（14 处是重复引用） |
| 保真 | GDI+ `Flatten()` 在固定 em 上离散化 ⇒ 无 hinting，14px 下警告三角里的感叹号几乎消失 |
| 再分发 | 把微软字体抠出的轮廓烘进随包 DLL，与"引用系统字体"是两种合规姿态；`ThirdPartyNoticesWindow` 没覆盖这一项 |

先查权威源再动手：`PathIcon.Data` 声明类型是 **Geometry**，mini-language 只在**属性字符串**上被转换
（这也解释了为什么 `PathGeometry.Figures` 收同一个串会抛 WMC0055）。但 `Setter.Value` 是 `object`，
文档没说它行不行 ⇒ 搭了一枚**同图自带对照**的臂（导航项走 Style、页眉走内联、两枚 Data 逐字相同）：
两枚一模一样 ⇒ **机制成立**，于是照做。

生成器 `tests/manual/safety/w8-gen-icon-table.py` 落地 46→32 条表 + 两张 `Icons.xaml`（LogViewer 是独立
进程、看不见主程序的 `Application.Resources`，所以必须两张），全部结构断言在脚本内，
并且**可复现性是按"还原→重跑→10 个文件 sha256 逐字节相同"量的**，不是按行数或"看起来一样"。

然后屏幕给了个门禁看不见的红：

- 默认页（导航栏 5 枚）改前改后**逐像素零差异**（1,026,302 采样点，`b0-after-default.png` vs `style-arm-default.png`）；
- 但 AI 翻译页两枚卡头图标**恒定少一枚**——暗列首段从 375/376 跳到 388，即那枚元素测出 0 宽、文字整个左移；
- 少哪一枚**随文件里无关行的增删换位**：把两枚的 key 互换 ⇒ 跟着 key 走；在 PageOutput 顶部插 7 行诊断臂 ⇒ 换到了另一枚；
- 同一次构建连拍 4 次读数完全一致 ⇒ 不是运行时抖动，是**构建期就定了**；
- 给那枚显式 `Width="20" Height="20"` **救不回来**。

那一轮 `--unit-tests` 1121 条全绿、门禁整解决方案构建 0 错误。**这个红在屏幕外没有任何东西看得见。**
所以 B0 回退：9 个文件还原（`App.xaml`/`MainWindow.xaml` 与探针前 sha256 逐字相同、`Icons.xaml` 删除、
生成器移出 `scripts/` 不再冒充构建工具），并留下一条**否决锁**（§13.5）。

### 13.3 ② 根因：八种寻址形态全部失败，且这次有阳性对照

`#28` 原本只要求"量出 `SymbolThemeFontFamily` 的运行时实际值"。做完之后这条问题被换掉了——
一次性诊断臂在默认可见页并排 8 行，同一张图里读：

| # | 形态 | 结果 |
|---|---|---|
| 1 | `FontFamily="Segoe Fluent Icons"` | 豆腐块 |
| 2 | `FontFamily="Segoe MDL2 Assets"` | 豆腐块 |
| 3 | `{ThemeResource SymbolThemeFontFamily}` | 豆腐块 |
| 4 | `C:/Windows/Fonts/SegoeIcons.ttf#Segoe Fluent Icons` | 豆腐块 |
| 5 | `<Setter Property="FontFamily">` 下发 | 豆腐块 |
| 6 | **`FontFamily="Microsoft YaHei"` + 中文** | **正常渲染 ⇒ 阳性对照成立** |
| 7 | `ms-appx:///Assets/SegoeIcons.ttf#Segoe Fluent Icons` | 豆腐块 |
| 8 | `Assets/SegoeIcons.ttf#Segoe Fluent Icons` | 豆腐块 |

第 6 行是关键：它证明"给 TextBlock 设 `FontFamily`"这条机制在本进程**是通的**，
所以 1–5/7/8 的失败不是"属性没生效"，而是**这个进程用不了符号字体本身**。
由此 ② 的旧口径要收窄：**不是"按友好名解析失败"，而是"按任何 XAML 侧寻址都拿不到该字体"**
（3 排除了主题资源没解析、5 排除了样式没下发、1/2 排除了名字写错）。
诚实的边界：4/7/8 用的是文件路径形态，而 WinUI 可能根本不支持这种 URI ⇒ 这三臂不能算"路径寻址被否证"，
只有 1/2/3/5 四臂是硬结论。

### 13.4 本轮我自己造成的两处测量错误（都被自己的锚点逮住）

1. **`ls C:\Windows\Fonts | grep -E 'segoe|icon'` 造出"这台机器没装图标字体"的假象。**
   注册表打脸：`Segoe MDL2 Assets → segmdl2.ttf`、`Segoe UI Symbol → seguisym.ttf` 都在，
   而这两个文件名既不含 `segoe` 也不含 `icon`——是我的过滤器把缺席证据滤掉了。
   这条与既有记忆里"缺席断言用的 grep 必须先对已知存在的串命中一次"是同一个坑，我又踩了一次。
2. **手写 TTF `name` 表解析器，把表头的 `version` 当成 `count` 读**（表头是 version/count/stringOffset 三个 USHORT），
   结果对 `SegoeIcons.ttf`、`segmdl2.ttf`、`segoeui.ttf` **三个文件全部返回空**。
   是"连已知一定有名字的 segoeui 也空"这个不自洽让我回去修解析器的，不是运气。
   修好后读到 `SegoeIcons.ttf` 的 Family 名**就是** `Segoe Fluent Icons` ⇒ 臂 4/7/8 的 `#名字` 没写错，
   那三臂的失败不能靠"我名字打错了"来解释（也正因为如此，它们仍然只能算 URI 形态存疑，见 §13.3 的边界）。

另外排除掉一条我自己刚起的新假设：会不会是提权差异（产品自报"以管理员权限运行"）？
量了——harness 的 `IsInRole(Administrator) = True`，两边同级；HKCU 那 3 条 per-user 字体是
`yyb Sans` / `FFont-Hu`，与图标字体无关。**假设在自己手里被量死，就没写进结论。**

### 13.5 判据与账目

- 新增 **2 条**否决锁（挂在 `Test_XamlIcons_DoNotDependOnSymbolFonts` 里）：
  ①不许有图标把轮廓交给 `Style` 下发且每枚必须自带内联 `Data`（≥46）；②两个工程都不许出现 `Icons.xaml`。
  变异 M-E1 实测：把 `ThirdPartyNoticesWindow` 那枚换成 Style 下发 ⇒ **正好红那一条**（1122 通过 / 1 失败），
  还原后 1123 通过 / 0 失败。
- `--unit-tests` **1121 → 1123**；地板 `1681 → 1686`（按门禁现跑读数抬，不手填）。
  这 5 条的构成要分两段记，不能都算在今天头上：**+3 是昨晚 toast 换形时加的**（1118→1121，当时没抬地板），
  **+2 是今天的两条否决锁**。抬完之后重跑一轮门禁（`round60-floorcheck.log`）要求"实得 == 地板"逐字相等，
  不是"≥ 地板"。
- 中途误跑了一次**旧产物**：`bin/x64/Release/net11.0-…/TrueToneCap.Test.exe` 是 **09-01** 的，
  报出"47 通过"。按门禁同款挑法（含 `Release` 段 + 按 `LastWriteTime` 取最新）改跑
  `bin/Release/net11.0-windows10.0.26100.0/`，才拿到 1121/1123 这份真读数。**"产物是旧的"这条我自己又踩了一次。**

### 13.6 未做与未判（下一轮的输入）

- **B1（重抽轮廓到 24em + 按字体真实 fill-rule）没做**。B0 被否后，"集中管理轮廓"这个动机已经消失，
  剩下的只有保真度问题（14px 警告三角那处）。要不要为它重做 46 枚并逐张目视复核，是产品决定，不是我能替他定的。
- **② 的最后一层没揭开**：这个进程的字体集里为什么没有符号字体。往下要量的是**进程内** DirectWrite
  的 `ISystemFontCollection` 到底列不列得出这两个家族——那需要产品自带一条诊断出口，属新增面，未做。
- **旧版兜底覆盖层那 15 枚图标没有截图读数**。它是正常构建到不了的路径（要靠诊断开关逼），
  而且它的窗在 `EnumWindows` 里时有时无（两次实测一次给到 2560px、一次只剩 840px 主窗），
  判据建在窗口枚举上不稳定。那 15 枚由"同一渲染路径已在主窗证明 + 源码哨兵"覆盖，**记为未目视**。
- `LogViewer` 那 5 枚换完了但**从没截过图**（它是独立 exe，起它要另走一条），同样记为未目视。
- 弱匹配不一致仍在：`保存` 在两处用 `E8FB`、在 `OcrPreviewWindow` 用 `E74E`（另存为）。现在两枚都看得见形状了，
  要不要统一属产品决定。

---

## §14 目视补盲轮（同日 05:20–06:10，用户："没做的部分按照你的想法推进，不需要反复征求我的意见，保证软件功能完美实现"）

### 14.1 B1 的前提被量死：14px 的细不是抽取失真

`w7-glyph-to-pathdata.ps1` 第 8 行写明抽取**已经在 em=100 上取点**、之后归一到 0..20 保留两位小数
⇒ 量化步长 0.05% em，根本不构成"细节丢失"。用独立渲染器（GDI+ `DrawString` 按像素字号直画，
完全不碰我抄的那串 Data）在 21/24/27/32 设备像素下逐档比最细笔画：

```
IconWarning  21px  字体直画 最细笔画 1px   我的 Data 最细笔画 2px   墨水比 1.04
IconError    21px  字体直画 最细笔画 1px   我的 Data 最细笔画 2px   墨水比 1.17
```

四枚语义图标每一档都是**两侧同量级（1–2px）**。⇒ 14px 下感叹号偏细是这套字形设计本身，
不是轮廓抄坏了；**重抽到 24em 修的是一个不存在的缺陷**，B1 撤案。
（§13.2 代价表里"保真"那一行因此只算观感事实，不算待修项。）

### 14.2 目视补上两处盲区，其中一处揪出真的功能缺陷

**LogViewer**：第一次截它的图（`logviewer-icons.png`）就看到工具条文字**重叠成一团**。回源：
表头那个 Grid 声明了 9 个 `ColumnDefinition`，而 `刷新 / 选择目录 / 级别筛选 / 搜索 / 清理过期 / 清空`
六个直接子元素**一个都没写 `Grid.Column`** ⇒ 全部默认落在第 0 列叠在同一位置。
**这不是本轮换图标引入的**（图标都带 `Width/Height=14`），是 LogViewer 一直就有的布局缺陷，
只是过去没人截过它的图。

判重叠没敢用截图：取图进程（PowerShell）未声明 DPI 感知，`GetClientRect` 读到的是被虚拟化过的坐标
（`appWindow.Resize(1280,800)` 读成 853×533，正好差 1.5 倍），"顶到右缘"这类读数分不清是产品还是夹具。
改用 UIA 的 `BoundingRectangle`（`w9-logviewer-uia-geometry.ps1`）：

| | 六枚 X | 重叠对 | X 递增 |
|---|---|---|---|
| 修复前 | 全等于 105 | **15** | 否 |
| 修复后 | 447/568/725/839/1011/1174 | **0** | 是 |

中途走过一条弯路：先把搜索框改成 `*` 列想让行可压缩，结果**两枚清理按钮被挤成 0 宽、直接消失**——
比原来"少一点内边距"更糟，已回退。真正的红是那个 1.5 倍换算的猜测本身（该图与 UIA 同为 1:1）。

新卡 `Test_LogViewer_Toolbar_HasDistinctColumns`（4 条）把该形状钉成源码判据，
并且**拿真实的修复前文件回归**（不是合成变异）：红 6 条、还原后 1127 全绿。

**覆盖层 15 枚**：主工具条 6 枚在 `overlay-toolbar2.png` 里目视确认（保存=软盘、标注=笔、复制=两页、
**识字=A字**、**翻译=地球**、取消=X）。剩下 9 枚在点「标注」之后才出现的子工具条里 ——
先按 `ControlType=Button` 找按钮找不到（覆盖层工具条是 Border + Tapped，不是 Button 控件），
改成在 `SelectionOverlay` 那扇窗的后代里**按名字**找、取它的 `BoundingRectangle` 中心再点
（不猜坐标，免得误触发保存或复制剪贴板）。结果 9 枚全部现形且语义正确：
矩形/椭圆/箭头/画笔/**AA**/**四方格**/撤销/重做/完成。

### 14.3 一次自我否决：没验证过的仪器不产出结论

本来想做"46 枚轮廓离线普查"（不依赖界面，逐条比 `Data` 与字体直画的形状），
跑出来 32 条里 24 条 `IoU<0.80` —— 与我亲眼看到的正确渲染直接矛盾。
先怀疑仪器：拿一枚我确定渲染正常的（`E8B7` 输出设置页）做裁决，
结果 `adjudicate-E8B7.png` **整张空白** ⇒ 仪器本身坏了。
于是那 24 条**两个方向都不采信**（既不说图标坏、也不说图标好），普查作废，
改回上面那条可靠办法（UIA 取矩形 + 目视）。这条留在这里是因为：
"跑出来一个数字很多、看起来很严密的表"本身不构成证据。

### 14.4 又踩了一条自己记过的坑：手改了生成物

给 `ThirdPartyNoticesWindow` 补"随包轮廓派生自微软字体"这条声明缺口时，我直接编辑了
`THIRD-PARTY-NOTICES.md`。该文件是 `scripts/gen-third-party-notices.py` 的**生成物**，
门禁 `[1.7/2]` 当场红：

```
[FAIL] THIRD-PARTY-NOTICES.md 与 licenses/ + INDEX.json 不同步：现有 398382 B / 应为 397147 B
       修复：py scripts/gen-third-party-notices.py
```

处置：内容搬进生成器的 `HEADER` 常量再重跑（`--check` 现 PASS，398386 B）。
过程中为消 `\W`/`\S` 的 `SyntaxWarning` 先试逐个双写反斜杠 —— 打地鼠（路径里还有两个文件名分隔符），
最后把 `HEADER` 整块改成原始字符串一次解决。
声明本身的口径：**只陈述事实与"未闭环"**——轮廓派生自 `SegoeIcons.ttf` / `segmdl2.ttf`、
这两个字体受微软 Windows 桌面许可约束、上表 BSD/Apache/MIT 义务都不覆盖这一项、
本环境没有可依据的条文；并写明替代路径是改用 MIT 许可的 SVG 几何。不假装它已被覆盖，也不替它下法律结论。

### 14.5 一次未复现的红（只记录，不判因）

`gate-round63-floorcheck.log`：`TrueToneCap.Tools.exe` 在 `[1.7/2]` 之后那一步报
`Stack overflow.`，`GATE_EXIT=1`。同一段代码 round62 与 round64 都是 `GATE_EXIT=0`，
把 `--container-audit` / `--wg-samples` 单独各跑 3 次共 6/6 全 `RC=0`。
⇒ **n=1、未复现**，按"偶发的偶数样本不足以下因果结论"只登记现象与日志路径，不写成缺陷、也不写成误报。
下一轮若再见到，先固定"整轮 vs 单跑"这一对读数再定性。

### 14.6 本轮账目

- unit 1123 → **1127**（新卡 4 条）；地板 1686 → **1690**（求和自校验 == 总断言）。
- 门禁：round61 红（我自己手改生成物，已修）、round62 绿 1690、round63 未复现的红、
  round64 绿 1690 / 无未判格子 / `GATE_EXIT=0`。
- 判据回归：新卡用**真实修复前文件**跑红 6 条；还原后全绿。
- 产品改动：`src/TrueToneCap.LogViewer/MainWindow.xaml`（6 个 `Grid.Column`）、
  `scripts/gen-third-party-notices.py`（HEADER 两条声明 + 改原始串）→ 重新生成 `THIRD-PARTY-NOTICES.md`。
- 新增 harness：`w9-fidelity-check.ps1`、`w9-logviewer-shot-cell.ps1`、`w9-logviewer-uia-geometry.ps1`、
  `w9-overlay-annotate-cell.ps1`、`w9-icon-census.ps1`（作废留痕）、`w9-fix-notice-escapes.py`、
  `w8-relaunch-instance.ps1`、`w8-font-name-probe.ps1`、`w8-pathicon-type-probe.ps1`、`w8-setter-style-probe.py`、
  `w8-font-arm.py`、`w8-style-arm-cell.ps1`、`w8-gen-icon-table.py`（B0 否决后移出 `scripts/`）。
- 诊断臂全部撤除：`MainWindow.xaml` 与探针前 sha256 逐字相同、`Assets/SegoeIcons.ttf` 已从 bin 删除、
  两个 `.probe-bak` 已清理。
- 覆盖层与 LogViewer 的"未目视"声明已解除；`#25` ② 根因仍开放（需产品自带诊断出口才能量到进程内字体集）。
