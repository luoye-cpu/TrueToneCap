# 交接台账：多屏鼠标跟随 + 录制/动图激活（2026-11-02）

> **文件性质**：按 `docs/README.md` §一.2 的例外条款，本文件是**跨周追加的运行台账**（不是取证快照），
> 后续轮次在同一文件里追加编号小节，**不回写历史小节**。写到哪一节、被推翻的旧结论、变异实验，
> 一律追加在后。
>
> **本轮状态**：两条改动流已实现并实机验证（未 commit）；第三条流（录制激活）只落地了第 1/9 步。
> **工作树脏**：11 个文件待提交，`git status` 明细见 §1。
>
> 行号口径：所有 `文件:行` 均为 **2026-11-02 工作树版本**，commit 之后会漂，按符号名搜。

---

## §0 一句话状态

| 流 | 内容 | 状态 | 验证 |
|---|---|---|---|
| A | 输出设置页重排（色域映射并入色彩与 HDR、格式卡紧跟基础输出） | ✅ 完成 | 构建 0/0；`--window-probe` exit 0；161 个 `x:Name` 与 HEAD 逐字相同 |
| B | 多屏鼠标跟随改进（P0 色彩同源 / P1 跟随 UX / P2 逐屏白点 / P3 可见性 / P4 卫生） | ✅ 完成 | unit **1820 通过 / 0 失败 / 2 跳过**；ocr 12；format 52；双屏实机前后对照成立 |
| C | 录制/动图模式分析 | ✅ 完成（只读） | 引擎逐行读完 + 断点三处 + 缺陷五条 + ffmpeg 动图 AVIF 可行性实测 |
| D | 录制激活 + ffmpeg 手动路径 | ⚠ **进行中：1/9** | 只落 `AppSettingsData.cs` 四个字段；构建 0/0；**无任何消费方**，行为零变化 |

**下一步第一件事**：读 §5，从"第 2 步"接着做（`SettingsService.ClampTo` + 三个假控件接线），**不要重做第 1 步**。

---

## §1 工作树状态（未 commit）

`git status --short`（HEAD = `9b8a930 release: v0.3.10-beta`）：

```
 M CHANGELOG.md                                     +11          A 流
 M src/TrueToneCap.App/MainWindow.xaml              +127/-…      A 流
 M src/TrueToneCap.App/MainWindow.xaml.cs           +320/-…      B 流（含删 OnCaptureNow -6804B）+ D 流待接线点
 M src/TrueToneCap.App/Models/AppSettingsData.cs    +16          D 流（唯一一处 D 流改动）
 M src/TrueToneCap.App/Services/HdrCaptureWindow.cs +18          B 流 P1-7
 M src/TrueToneCap.App/Services/ToastService.cs     +11/-…       B 流 P1-6
 M src/TrueToneCap.App/Services/WgcCaptureService.cs +66/-…      B 流 P0-1/P2-9/P3-10
 M src/TrueToneCap.App/SilentCaptureToast.xaml.cs   +45/-…       B 流 P1-6
 M src/TrueToneCap.Core/Capture/DisplayInfo.cs      +298/-…      B 流 P0-2/P2-9/P4-11
 M src/TrueToneCap.Core/Encoding/ImageEncoder.cs    +9           B 流 P0-4（纯注释）
 M src/TrueToneCap.Test/CorePipelineTests.cs        +109/-…      B 流 P0-5 判据 + 2 条旧计数放宽
```

构建：`dotnet build src/TrueToneCap.App -c Debug` ⇒ **0 警告 0 错误**（36.7s，2026-11-02 23:40 实测）。

---

## §2 A 流：输出设置页重排（完成）

**用户原话**：「色域映射合并进色彩与HDR下放，输出设置最下面的每种格式的相关选项移动到基础输出页下方」。

改动全在 `MainWindow.xaml`（+127/-…）：
- 7 张格式卡（PNG/JPEG/JPEG XL/AVIF/WebP/TIFF/…）从页面末尾移到「基础输出」卡正下方；
- 「色彩与 HDR」卡整体下放到页面末尾，原「色域映射」卡并入其下半部分，用 1px `Border`
  （`CardStrokeColorDefaultBrush`）分隔，`GamutMapTitle` 降级为 FontSize 12 次级半粗。

**纪律核对**（这次改动的验收口径）：
- 161 个 `x:Name` 值与 HEAD 逐字相同 ⇒ 代码后置文件零改动需求（`MainWindow.Settings.cs` 的回显/保存全部照旧）；
- XML 合法、构建 0/0；
- `--window-probe` exit 0、日志 `全部窗口构造通过`、`MainWindow` XAML init 684 ms。

`CHANGELOG.md` +11 行由 teammate `changelog-scribe` 写：
`## [未发布] — 2026-11-02：输出设置页重排：格式选项紧跟格式，色域映射并入色彩与 HDR`。

**已知遗留（有意不修）**：`→ HDR 直通（容器写 PQ）` 在固定 120px 首列表头里溢出、视觉上撞到 `Rec.2020`。
已在**未修改的发布版**上用 UIA 取证证明是既有缺陷，且标记逐字节等于 HEAD ⇒ 不是本次引入。
修它会动 `MappingArrow` 的布局契约（有测试锁），留待单独一轮。

---

## §3 B 流：多屏鼠标跟随（完成并实机验证）

### 3.1 根因（实机复现，不是推测）

双屏下三处入口→屏的映射本来就不一致：
- **无感截图 / 录制**：单屏，跟随光标屏（本身自洽）；
- **区域截图**：恒拼接整个虚拟桌面，`CaptureAllMonitorsInternalAsync` 返回的 `CaptureResult`
  原先**不填 `SourceDisplay`、不填 `IccProfile`** ⇒ 下游只能拿光标屏猜。

实机复现（21:12，修正前）：选区拉在 **SDR 副屏**、保存前光标回 **HDR 主屏** ⇒ 决定按主屏算
`分类=Hdr 源=DisplayP3 目标=BT2020 HDR=写PQ` ⇒ 产物 **16-bit PNG + cICP 9/16/0（Rec.2020+PQ）**，
内容是副屏的 SDR 桌面。**像素与声明分属两屏。**

### 3.2 改法（按优先级）

| 编号 | 改法 | 落点 |
|---|---|---|
| P0-1 | 拼接结果补齐元数据：`SourceDisplay = hdrSource ?? 主屏 ?? 首屏`、`IccProfile = GetDisplayIccProfile(基准屏)`，与单屏路径同形状；Info 日志 `拼接元数据: 基准屏=… ICC=…` | `WgcCaptureService.cs` 多屏 return |
| P0-2 | 新增 Core 纯函数 `ColorDecision.BuildForRegion(displays, selX, selY, selW, selH, requestedTag, hdrOutput, systemAcm, format)`：按选区∩屏幕矩形覆盖判定；单屏覆盖 ⇒ 原样转 `Build`；跨屏 ⇒ **最弱屏钳制**（任一确证窄屏 ⇒ 目标收口 sRGB + `GamutClamped`；覆盖屏未全 Hdr ⇒ 压 PQ + `HdrClamped`）；交集全 0 ⇒ `Unknown` 不猜屏；**交集 1 像素也算覆盖** | `DisplayInfo.cs` `ColorDecision` 内 |
| P0-3 | 选区全链接线：`regionDecision` 在选区动作分派处算一次，穿到保存/复制/二维码/贴图/标注窗/OCR 预览窗六个出口。`EncodeAndSaveHdrAsync`/`EncodeAndSaveAsync`/`EncodeAndCopyAsync`/`CaptureAndOcrFromPixelsAsync`/`CaptureAndTranslateFromPixelsAsync`/`OpenOcrPreviewWindow`/`OpenAnnotationWindow` 全部加可选尾参；光标屏兜底**只剩真正无源信息入口** | `MainWindow.xaml.cs` |
| P0-4 | `HdrFrameData.IccProfile` 定性为**死通道**（Core 编码器零读取，实测确认），只加注释不改行为 | `ImageEncoder.cs:131` 附近 |
| P1-6 | Toast 跟随截图屏：`SilentCaptureToast(position, RECT?)`，`PlaceAt` 优先用传入工作区；`ToastService.ShowToast(..., captureScreenWorkArea)`；`ShowSaveToast(..., DisplayInfo? captureMonitor)` → `GetMonitorWorkArea` 走 `GetMonitorInfoW`（就地 `MonitorInfoLite`/`RectLite`，跟随 PinWindow 惯例） | `SilentCaptureToast.xaml.cs`、`ToastService.cs`、`MainWindow.xaml.cs` |
| P1-7 | 覆盖层 chrome 逐屏 DPI：`_uiScale` 改为 `GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI)`，失败退 `GetDpiForWindow`。与图集预热同一把尺（`GetDpiForMonitor` MDT_EFFECTIVE_DPI） | `HdrCaptureWindow.cs:785` 附近 |
| P2-9 | 逐屏 SDR 白点：新增 `DisplayInfo.GetSdrWhiteLevelForMonitor(gdiDeviceName)`（按活跃路径取源侧 GDI 名匹配 ⇒ 对 target 查 `DISPLAYCONFIG_SDR_WHITE_LEVEL`）；拼接上转换按各屏自己的白点缩放，查不到回退系统级单值；白点不同打 Info | `DisplayInfo.cs`、`WgcCaptureService.cs` 拼接循环 |
| P3-10 | 黑块/降级可见性：HDR 源建立失败、单屏首帧超时（该屏区域为黑）、SDR→scRGB 上转换失败三条 `Log`(Debug)→`LogInfo`(Brief 可见) | `WgcCaptureService.cs` |
| P3-11 | 壁纸回退竞态：`s_lastItemDegradation` 改为 `CreateItemForMonitor` **入口清一次**，删掉三处"成功即清零"⇒ A 屏的回退警告不再被 B 屏成功清掉 | `WgcCaptureService.cs` |
| P4-11 | 删死代码：`OnCaptureNow`（零 XAML 绑定/零热键，6804 字符）；5 个自相矛盾的死结构体（`DISPLAYCONFIG_VIDEO_SIGNAL_INFO`/`PATH_TARGET_INFO`(76)/`PATH_SOURCE_INFO`(20)/`PATH_INFO`(100)/`MODE_INFO`）——它们与活代码的裸偏移常量（`DC_PATH_INFO_SIZE=72`、target@20、id@28…）互相矛盾，是地雷 | `MainWindow.xaml.cs`、`DisplayInfo.cs` |

### 3.3 判据（`CorePipelineTests.cs` +109 行）

新增 **13 条**：
1. 选区只在窄色域副屏 ⇒ 收口 sRGB + HDR 压掉 + `HdrClamped`
2. 选区只在 HDR 主屏 ⇒ Hdr/BT2020 放行（与单屏 `Build` 同形）
3. 跨屏（HDR 主 + 确证窄副）⇒ 声明收口 sRGB + SDR，`Source` 仍是主源屏
4. 跨屏（HDR 主 + 确证广色域副）⇒ HDR 压掉但目标**不收口**（没有窄屏）
5. 跨屏 + 显式要 BT2020 + 含窄屏 ⇒ 收口 + `GamutClamped` + 证据含"跨屏选区覆盖"
6. **跨屏压 HDR 必须亮 `HdrClamped`**（审查轮补的；见 §3.5-①）
7. 悬空选区 ⇒ `Unknown`
8. 空显示器列表 ⇒ `Unknown`（不崩）
9. 跨屏边界 1 像素重叠 ⇒ 仍按最弱屏钳制
10. 拼接结果必须填 `SourceDisplay`（源码形态锁）
11. 拼接结果必须填 `IccProfile`
12. 选区动作用 `BuildForRegion`、`regionDecision ?? ColorDecision.Build(` 兜底形状
13. `EncodeAndSaveHdrAsync` 签名带 `regionDecision`

同时**放宽 2 条旧计数判据**（不是因为测试红而放宽，而是因为死代码被删）：
- `PreferHdr = true` 计数 `>=3` ⇒ `>=2`（无感 + 选区；第 3 处原本在 `OnCaptureNow`）
- `BuildEncodingSettings(format, hdrScenario` 计数 `>=3` ⇒ `>=2`（同上）

### 3.4 实机验收（双屏真机，前后对照）

| 实验 | 修正前（21:12） | 修正后（22:02） |
|---|---|---|
| 光标=主屏、选区=**副屏** | `分类=Hdr 目标=BT2020 HDR=写PQ`；产物 **16-bit + cICP 9/16/0**（假 HDR） | `分类=NarrowSdr [HDR已收口]`；产物 **8-bit + cICP 1/13/0**（诚实 SDR） |
| 光标=主屏、选区=主屏（对照） | — | `分类=Hdr 目标=BT2020 HDR=写PQ`；16-bit + cICP 9/16/0 **无回归** |
| 拼接 | 无元数据 | `拼接元数据: 基准屏=\.\DISPLAY5 ICC=无`；画布 6400x2160 HDR=True |
| `--display-audit` | — | exit 0，两屏分类正确 |

**`ICC=无` 不是缺陷**：本机两块屏都"使用系统默认 ICC"（`--display-audit` 当日读数），
WCS 对无用户配置文件的屏返回 null 是正确答复；决定仍走屏幕分类 + 标准 ICC 兜底。

### 3.5 审查轮发现并已修的三个问题（**别再引入**）

1. **跨屏压 HDR 时 `HdrClamped` 标记丢失**：`hdr=false` 由 `BuildForRegion` 自己传下去，
   而 `Build` 的 HdrClamped 只在"开关开着 && primary 屏不授权"那格置位 —— primary 恰好是 Hdr 档时它不知道。
   修法：`crossHdrClamped = hdrOutput && !allHdr`，返回处 `HdrClamped = decision.HdrClamped || crossHdrClamped`；
   证据行拆成两句（`最弱屏钳制目标色域；` / `覆盖屏未全部处于 HDR 档 ⇒ 不写 PQ；`）。
2. **我自己引入的未用结构体** `DISPLAYCONFIG_GET_SOURCE_NAME`：落地实现走的是既有裸偏移
   `QuerySourceGdiDeviceName`，那个结构体等于刚在 P4-11 删掉的"第二份布局真相"。**已删**，
   并在注释里写明这条路径不许再造结构体 marshaling。
3. **`SilentCaptureToast.xaml.cs` 的 UTF-8 BOM 被 PowerShell 写回剥掉**：该文件在 HEAD 带 BOM
   （仓里其余 `.cs` 无 BOM）。**已恢复**，并逐文件核对了 11 个改动文件 HEAD↔现在的 BOM 状态。
   ⇒ 纪律：**用 `read`/`edit`/`write` 工具改文件，别用 `[System.IO.File]::WriteAllText` 直写**；
   非直写不可时必须显式传 `new UTF8Encoding($true/$false)` 对齐原文件。

---

## §4 C 流：录制/动图模式全盘现状（只读分析，未改动）

### 4.1 一句话

**引擎完整、整车没接电**：`AnimationRecorder`（`src/TrueToneCap.App/Services/AnimationRecorder.cs`，
396 行）是一条可用的 WGC → PNG 帧序列 → ffmpeg 动图管线，但全仓 **`new AnimationRecorder(` 零处**，
git 全历史也只在 docs 里出现过（v0.3.6/v0.3.7 快照同样零处）。这是台账 §54.4 记录在案的
**"用户令先待命"**（2026-10-02 拍板），不是回归。

### 4.2 引擎设计（已实现的部分）

采集侧（`RecordLoop`）：
- 屏选择：`DisplayIndex != 0 ? 指定屏 : GetMonitorUnderCursor()`（单屏，光标跟随语义）；
- 取帧：`WgcCaptureService.TryGetLatestFrame(hmonitor)` —— 在 `_poolLock` 下读 `(hmonitor, false)`
  的 **SDR 8-bit 池**，**不抢 `s_captureLock`**（与截图互不阻塞）；池空/无帧返回 null，跳过该 tick；
- 定时：`PeriodicTimer(1000/fps)`；
- 差异检测 `HasChange`：5000 点采样、字节差 >8 计变化、占比 > `ChangeThreshold`(0.01) 才存帧
  ⇒ 静态画面零帧产出；
- 内存纪律：有界 `Channel` **30 帧** + `FullMode.Wait` 背压（1s 超时丢帧并计数）；未变化帧复用
  `scratch` 缓冲（静态画面不每 tick 分配 33MB）；所有权注释写清 scratch 只能持"无人引用"的缓冲。

编码侧（`EncodeFrames`）：
1. Channel 读出全部帧 → 2. 逐帧 `CapturePipelineService.PreparePixelsWithIcc`（色彩决定**整次录制算一次**）
→ 3. 写临时 PNG（**`ManagedPngEncoder.EncodeFast`**：Filter=None+Fastest，4K 单帧 448→55ms，中转文件不值得压缩）
→ 4. **ffmpeg 子进程**：WebP（`-c:v libwebp -quality N -loop 0`）/ APNG（`-plays 0 -f apng`）/
GIF（`palettegen`+`paletteuse` 两 pass）→ 5. 超时 120s kill；stderr 异步读防管道死锁
→ 6. 失败/ffmpeg 缺席 ⇒ 第一帧走**全量** `ManagedPngEncoder.Encode` 存单帧 PNG。

性能判据已在位：`CorePipelineTests.cs:3273`（`Test_PerfWiring_A1A2A4A5`）钉住"中间帧必须 `EncodeFast`、
回退必须全量 `Encode`"。

### 4.3 断点清单（引擎 → 用户之间）

| # | 断点 | 证据 |
|---|---|---|
| 1 | **无实例化** | `new AnimationRecorder(` 全仓 0 处；`--record` 在 `src/` 0 次 |
| 2 | **录制热键不注册** | `MainWindow.xaml.cs` 的 `recordedBox == RecordHotkeyTxt` 分支**只**写 `_settings.RecordHotkey = result`，没有 `RegisterNamed("record", …)`；对照 `HotkeyTxt`（`RegisterCaptureHotkey`）与 `SilentHotkeyTxt`（`HotkeyManager.RegisterNamed(this,"silent",…)`）两条活分支 |
| 3 | **三个假控件** | `AnimFormatCbo`(GIF/APNG/AVIF)、`RecordFpsCbo`(10/15/20/30)、`MaxDurationCbo`(15/30/60/120) **连设置字段都没有**，只被 `MainWindow.Localization.cs:151-163` 改显示文字；选什么都不被读 |
| 4 | 半断点：`RecordQuality` | 滑条回显 + 保存都有，但**没人填进 `RecordingConfig.Quality`** |
| 5 | 半断点：`ToastOnRecording` | 开关有 UI 有保存，`ShowSaveToast` 的 `"recording"` 分支存在，但**全部调用点只传 capture/silent** ⇒ 不可达分支 |

`RecordingConfig.DisplayIndex` / `ChangeThreshold` / `ColorSpaceTag` / `OutputPath`：UI 无暴露，恒默认值。

### 4.4 引擎自身缺陷（激活时必须一起处理）

1. **"动画 AVIF" 是假选项**：UI 有 `Tag="AVIF"`，但引擎的格式 switch 里 `AnimatedAVIF` 落到
   `_ => ("webp", ".webp")` ⇒ **选 AVIF 会静默编出 WebP**。Core 侧也没有 AVIF 序列编码器
   （`NativeAvifEncoder` 只有单帧）。
   ⚠ **但 ffmpeg 这条路是通的** —— 本轮实测（见 §4.5）`avif` muxer + `libaom-av1` 多帧写入成功。
2. **产物零声明**（交接 §41.4 记录在案）：中间帧 `EncodeFast` 不写 cICP 也不带 iCCP ⇒
   `PreparePixelsWithIcc` 的**像素搬运**生效、"声明"通道全丢；动图容器由 ffmpeg 另编，
   色彩声明无处安放 ⇒ 像素正确、声明空白。
3. **文档漂移**：`docs/architecture-design.md:525` 与 `:780` 写"DropOldest 防 OOM"，
   代码 P3 之后是 `FullMode.Wait` + 1s 超时丢帧计数 ⇒ 活文档需更正（日期命名的快照不许回写）。
4. **ffmpeg 是全应用唯一的 PATH 强依赖**：自动检测，不可用回退单帧 PNG（动图直接没了）。
   本机：`C:\PLAN\FFmpeg\ffmpeg.exe` 在 PATH 上，`ffmpeg version git-2026-10-27`。
5. **单帧回退恒 8-bit**：`Encode(bakedPixels, w0, h0, fallbackPath, 8)` —— HDR/广色域会话录出来的
   东西退化成 SDR PNG（与"动图无 HDR 容器"一致，但激活时该在 UI 明说）。

### 4.5 ffmpeg 动图 AVIF 可行性（本轮实测，**这是 D 流的技术前提**）

本机 ffmpeg 带编码器：`libaom-av1`、`libwebp`、`libwebp_anim`、`gif`。

实测命令（64×48 三帧 PNG 序列）：
```
ffmpeg -y -framerate 5 -i frame_%05d.png -c:v libaom-av1 -still-picture 0 out.avif
```
结果：`exit=0`、`out.avif` 1126 B、`ffprobe` 报 `stream,av1,1` 与 **`stream,av1,3`（3 帧真实写入）**
⇒ **动图 AVIF 走 ffmpeg 可行**，`AnimatedAVIF` 不需要删 UI 项，补参数分支即可
（激活时还要实测定 `-still-picture 0` + `-loop` 语义与播放器兼容性）。

---

## §5 D 流（进行中）：录制激活 + ffmpeg 手动路径

### 5.1 第 1 步（✅ 已落地，唯一改动）

`src/TrueToneCap.App/Models/AppSettingsData.cs` +16 行，在 `RecordQuality` 之后新增：

```csharp
public string AnimFormat { get; set; } = "WebP";          // Tag 值即持久化契约：GIF/APNG/AVIF/WebP
public int RecordFps { get; set; } = 15;
public int MaxDurationSeconds { get; set; } = 30;
public string FfmpegPath { get; set; } = "";             // 空 = 自动解析；非空 = 优先
```

语义已写在注释里：
- `AnimFormat` 的字符串值与 UI `ComboBoxItem` 的 `Tag` **逐字一致**（GIF/APNG/AVIF/WebP），缺键落 `"WebP"`；
- `FfmpegPath` 空/空白 = 自动解析（缓存 → PATH → 常见安装位置）；非空 = 优先，指向无效时**回退自动解析并记日志**。

⚠ 本步**没有任何消费方**，所以行为零变化；构建 0/0。**没有改 `SettingsService.ClampTo`**（下一步就要加）。

### 5.2 剩余 8 步（按依赖顺序，落点与坑都写清）

**第 2 步 · 设置规范化**（`src/TrueToneCap.App/Services/SettingsService.cs`）
- 在 `ClampTo(s.RecordQuality, …)` 之后加：`RecordFps` 收敛到 `{10,15,20,30}` 之一（不合法落 15）；
  `MaxDurationSeconds` 收敛到 `{15,30,60,120}`（落 30）；`AnimFormat` 白名单
  `{GIF,APNG,AVIF,WebP}`（落 `WebP`，大小写归一）；`FfmpegPath` 去首尾空白、Trim 引号、
  **不做存在性判据**（路径可能暂时不可达，存在性由 locator 每次解析时判）。
- 坑：`MainWindow.Settings.cs` 的旧 `legacyTags→currentTags` 式迁移会读老文件 ⇒ 新增字段
  必须是"缺键即默认"，别写会在反序列化后抛异常的校验。

**第 3 步 · ffmpeg 解析器（新文件 `src/TrueToneCap.App/Services/FfmpegLocator.cs`）**
- 静态类，`Resolve()` 返回 `(string? Path, string Source)`，顺序：
  ① `_settings.FfmpegPath`（非空且 `File.Exists`，且**实跑 `-version` 成功**）；
  ② PATH（`ProcessStartInfo` 用 `ffmpeg` 裸名，或 `where.exe`/`GetEnvironmentVariable("PATH")` 逐目录探）；
  ③ 常见位置：`%ProgramFiles%\ffmpeg\bin`、`%LOCALAPPDATA%\Microsoft\WinGet\Links`、应用目录旁 `ffmpeg.exe`。
- **探测结果缓存**（本机路径 + 版本串），带显式 `Invalidate()`（用户改设置/改 PATH 后调用）；
  避免每帧/每次录制都 fork 一次进程。
- 判据要点：**绝不能让探测抛异常打断截图链路**（录制是可选功能，截图是主线）。
- 坑：`ffmpeg -version` 的输出要从 stdout 读，`-hide_banner` 少一行；超时（比如 3s）必须 kill。

**第 4 步 · 三个假控件接线**（`MainWindow.Settings.cs` 回显 + 保存；`MainWindow.xaml.cs` 消费）
- 回显：`AnimFormatCbo` 按 `Tag == _settings.AnimFormat` 选中；`RecordFpsCbo` 按 `Tag == RecordFps`；
  `MaxDurationCbo` 按 `Tag == MaxDurationSeconds`。
- 保存：三个 `SelectionChanged` 落回设置（照 `OnRecordQualityChanged` 的形状写）。
- 消费：构造 `RecordingConfig` 时读设置（别在 UI 事件里直接读控件，走设置字段）。
- 坑：`Localization.SetComboItemText` 是按 Tag 改文字的，**别把 Tag 改成本地化后的字符串**。

**第 5 步 · `AnimationRecorder` 改造**
- 构造函数或 `RecordingConfig` 增加 `FfmpegPath`（由 locator 给出）；`Encoding` 阶段用它起进程，
  找不到时的错误信息要**可执行**（"未找到 ffmpeg：请在设置里指定路径，或把 ffmpeg 加入 PATH"），
  而不是现在的静默回退单帧 PNG（回退保留，但必须 Toast 明说"已退化为单帧 PNG"）。
- **补 `AnimatedAVIF` 分支**：`("avif", ".avif")` + `-c:v libaom-av1 -still-picture 0 -loop 0`
  （本轮已实测 `-still-picture 0` + `-c:v libaom-av1` 出多帧 avif；`-loop` 语义需实测定）；
  同时把 `_ => ("webp", ".webp")` 兜底改成**显式抛错/断言**，防止将来再加枚举值时静默错配。
- 声明通道（§4.4-②）：ffmpeg 侧无 ICC 注入位（`-write_icc` 类参数不存在）⇒ 至少在产物旁
  **写一份 sidecar 或在日志里声明"动图容器无色彩声明"**；要不要做是产品决定（见 §9）。

**第 6 步 · 热键注册 + 启停循环**
- `MainWindow` 启动处照 `silent` 的写法注册：`HotkeyManager.RegisterNamed(this, "record",
  _settings.RecordHotkey, () => DispatcherQueue.TryEnqueue(() => ToggleRecording()), fallback: [...])`；
- `recordedBox == RecordHotkeyTxt` 分支补一次重注册（与 `SilentHotkeyTxt` 对齐）；
- `ToggleRecording()`：`Idle` ⇒ 建 `AnimationRecorder`、预建会话（第 7 步）、开始、状态栏/托盘提示
  "录制中（再按 Ctrl+Shift+G 停止）"；`Recording` ⇒ `StopAndEncodeAsync()`、进度提示、完成后
  `ShowSaveToast(…, "recording")`（**这一步才让 §4.3-⑤ 的不可达分支变可达**）。
- 坑：`AnimationRecorder` 是 `IDisposable`；异常路径必须 `Dispose`；重复按键要防重入
  （参照 `_isCapturing` 的 `Interlocked.CompareExchange` 纪律）。

**第 7 步 · 会话预建**
- `TryGetLatestFrame` 对空池返回 null ⇒ 录制开始前必须有人把 `(hmonitor, false)` 会话建起来，
  否则前若干 tick 全空、`_framesCaptured` 一直是 0。
- 最小改法：`ToggleRecording` 开始前调 `AppServices.Wgc.CaptureMonitorAsync`（或暴露一个
  `EnsureSdrSession(hmonitor)`），成功后再 `StartRecording()`。

**第 8 步 · 判据（`CorePipelineTests.cs`）**
- 接线锁：`new AnimationRecorder(` 存在且只出现在录制入口；`RegisterNamed(this, "record"` 存在；
- 假选项锁：`AnimationFormat` 每个枚举值在 ffmpeg 参数 switch 里**都有显式分支**（不许 `_ =>` 兜底能命中任何枚举值）；
- 设置锁：三个新字段在 `AppSettingsData` 有初值、在 `MainWindow.Settings.cs` 有回显与保存（形态锁）；
- locator 锁：解析顺序（设置 > PATH > 常见位置）与"探测异常不打断调用方"的形态；
- 文档锁：`architecture-design.md` 不再出现"DropOldest"（或改成 Wait+丢帧计数）。

**第 9 步 · 文档与实机**
- 更正 `docs/architecture-design.md:525`、`:780` 的 DropOldest 漂移；§8.1 表格补 `FfmpegLocator`；
- `CHANGELOG.md` 加一条（录制激活 + 手动 ffmpeg 路径）；
- 实机跑一轮：14 fps / 30 秒 / 四个格式各录一次，确认产物能被系统播放器打开
  （GIF/APNG/WebP/AVIF 各验一次；AVIF 要重点验帧数与循环）。

---

## §6 验证口径（怎么复跑，别跑全量）

```powershell
# 构建
dotnet build src\TrueToneCap.App\TrueToneCap.App.csproj -c Debug -v:q

# 本流相关套件（B 流改动的回归口径）
dotnet run --project src\TrueToneCap.Test -- --unit-tests     # 基线 1820 通过 / 0 失败 / 2 跳过
dotnet run --project src\TrueToneCap.Test -- --ocr-tests      # 12 通过 / 0 失败
dotnet run --project src\TrueToneCap.Test -- --format-tests   # 52 通过 / 0 失败

# 双屏实机（B 流）
#   apps 需先关掉任何持有单实例互斥体的实例，然后：
#   TrueToneCap.exe --display-audit        ⇒ exit 0，读 display-audit.txt
#   TrueToneCap.exe --window-probe         ⇒ exit 0，日志"全部窗口构造通过"

# 录制实机（D 流接线后）
#   起 app → Ctrl+Shift+G 开始 → 等 3s → 再按停止 → 检查输出目录的动图 + 日志"编码完成"
```

**不要跑 `scripts/verify.ps1` 全门禁**（只在发版时跑）。`scripts/gate-baseline.json` 里
`--unit-tests` 的期望计数是 **1807**，本轮之后实际 **1820** ⇒ 若跑全门禁会因计数漂移报错，
发版前需要更新 baseline（这是**已知待办**，见 §9）。

---

## §7 环境事实（本轮实测，后续复现要一致）

双屏拓扑（`--display-audit` 2026-11-02 读数）：

| 屏 | 分辨率/位置 | 分类 | 证据 |
|---|---|---|---|
| `\\.\DISPLAY5` `0x405221B` **主屏** | 3840×2160 @(0,0) | **Hdr** | DXGI RgbFullG2084NoneP2020、10 bpc、峰值 418 nits；DISPLAYCONFIG DWM mode=2；源 DisplayP3 → 目标 BT2020；EDID 原生宽 ΔG=0.0989 |
| `\\.\DISPLAY1` `0x1031132B` 副屏 | 2560×1600 @(3840,0) | **NarrowSdr** | AUO75AC ΔG=0.0234（确证不宽）；8 bpc；supportsHdr=false；DWM mode=1(WCG)+广色域已启用=是 **但 EDID 不宽** ⇒ 正确压回 NarrowSdr；所有投递 `BlockedByDisplay` |

- 拼接 HDR 源 = `\\.\DISPLAY5 0x405221B`；拼接画布 6400×2160，HDR=True，3 会话，520~920 ms；
- 系统 SDR 白点 = **120 nits**（两块屏都吃同一系统值；`GetSdrWhiteLevelForMonitor` 逐屏查询在本机返回同值）；
- 系统级 HDR 开、ACM 注册表=yes；
- 本机两块屏都是"系统默认 ICC" ⇒ `GetDisplayIccProfile` 返回 null（正确行为，见 §3.4）；
- ffmpeg：`C:\PLAN\FFmpeg\ffmpeg.exe`（PATH 可见），`git-2026-10-27`，含 `libaom-av1`/`libwebp_anim`/`gif`。

**日志档位**：默认 Brief ⇒ 只有 Info 及以上落盘。`Log`(=Debug) 的行在常规日志里看不见 ——
本轮 P3-10 升的三条正是这个原因，`拼接元数据`/`逐屏 SDR 白点` 也按可见性分档处理过
（`拼接元数据` 已升 Info，`逐屏 SDR 白点` 只在"白点确实不同"时打）。

---

## §8 本轮踩过的坑（下次别再踩）

1. **单实例互斥体挡住探针**：`--window-probe` / `--display-audit` 若被已运行的发布版
   （如 `publish\TrueToneCap-v0.3.10-beta\TrueToneCap.exe`）持有 `Global\TrueToneCap_SingleInstance`，
   会 exit 0 但什么都不做。跑探针前先确认没有实例在跑。
2. **PowerShell 直写文件会改编码**：`[System.IO.File]::WriteAllText` 不带 `UTF8Encoding` 会剥 BOM；
   本仓 `.cs` 大部分无 BOM、个别有（`SilentCaptureToast.xaml.cs`）⇒ 用 `read/edit/write` 工具。
3. **构建撞 `MSB3027`= 目标被占用**：测试 app 还在跑时改 Core 会失败。先 `Stop-Process` 再 build。
4. **PowerShell 里 `Add-Type` 定义的类跨调用丢失**：每个 `pwsh` 调用是新进程，驱动脚本要一次写完；
   `public static void X(){ return h; }` 这种签名错误会整块 `Add-Type` 失败并连带 `找不到类型`。
5. **UIA 触发需要前台**：主窗在后台时 `InvokePattern.Invoke()` 会静默不触发（日志里没有
   `选区截图启动` 就是没进去）。先 `ShowWindow`+`SetForegroundWindow` 再 Invoke。
6. **覆盖层窗口类名是 `TTC_HC`、标题 `HDR`**；`FindWindowW` 的 CharSet 不匹配时恒返回 0，
   用 `EnumWindows`+`GetClassNameW` 更稳。保存动作 = 向该窗口 `PostMessageW(WM_KEYDOWN, 'S')`。
7. **光标移动要 DPI 感知**：`pwsh` 默认 DPI-unaware，坐标会被虚拟化；先 `SetProcessDPIAware()`，
   并用 `GetPhysicalCursorPos` 复核（本机实测虚拟=物理，但别假设）。
8. **`edit` 工具要求先 `read`**：改了外部进程（脚本）写过的文件后，工具会报
   `file changed since it was read`/`has not been read`，重读再改。

---

## §9 待拍板 / 已知未决

| # | 事项 | 现状 | 需要决定 |
|---|---|---|---|
| 1 | `scripts/gate-baseline.json` 的 `--unit-tests` 期望计数 | 仍写 **1807**，实际 **1820**（B 流 +13 条） | 发版前要不要更新 baseline（更新即承认新增判据） |
| 2 | 动图产物的色彩声明（§4.4-②） | 中间帧零声明；ffmpeg 无 ICC 注入位 | 要不要做 sidecar / 日志声明 / 干脆接受"动图无声明"并写进文档 |
| 3 | AVIF 动图的播放器兼容与 `-loop` 语义 | 已实测能出多帧 avif，未验播放器 | 激活时要不要实测 Edge/Chrome/系统照片各播一次 |
| 4 | 单帧回退恒 8-bit（§4.4-⑤） | HDR 会话录出来退化为 SDR PNG | 是否允许回退走 16-bit，或在 UI 明说 |
| 5 | A 流遗留的表头溢出（§2） | 既有缺陷，与本次无关 | 是否单开一轮修 `MappingArrow` 布局契约（会动测试锁） |
| 6 | 录制热键默认值 `Ctrl+Shift+G` 的冲突面 | 未验证与系统/其他软件冲突 | 激活时用 `RegisterNamed` 的 fallback 列表兜底即可，是否还要加冲突提示 |

---

## §10 交接清单（接手第一件事）

1. 读 §5.2 第 2 步开始做；**第 1 步已落地，别重做**。
2. 动手前先 `git status` 确认工作树仍是 11 个文件未提交（若已 commit 则看 `git log` 找本文件名）。
3. 任何改 `Core` 的动作前先关掉在跑的 app（否则 `MSB3027`）。
4. 每改完一段跑一次 `--unit-tests`（基线见 §6），别攒到最后。
5. 录制接线完成后**必须实机录一轮**并验产物（§5.2 第 9 步）—— 单测钉不住"ffmpeg 真能编出来"。

---

## §11 D 流落地实录（2026-11-03 追加；第 2~9 步全部完成）

> 承接 §5.2 的 8 步计划。本节记录实际落法、实测推翻/确认的假设、踩坑。
> 用户拍板两项（§9-2 / §9-4 的答案）：**动图声明"能声明就声明"**（用户反问 PNG/WebP/AVIF 本来就有
> 声明通道——正确）；**回退走 16-bit**。

### 11.1 交付内容（对照 §5.2 步号）

| 步 | 落点 | 备注 |
|---|---|---|
| 2 | `SettingsService.Normalize`：`RecordFps is 10 or 15 or 20 or 30`、`MaxDurationSeconds is 15/30/60/120`、`FitTag(s.AnimFormat,…,"GIF","APNG","AVIF","WebP")`、`FfmpegPath` 只 Trim+去引号不判存在 | 照 §5.2 第 2 步 |
| 3 | 新文件 `FfmpegLocator.cs`：设置指定 → PATH 逐目录 → 常见位置（ProgramFiles/ffmpeg/bin、WinGet Links、exe 旁挂）；实跑 `-hide_banner -version`（3s 超时 kill）；只缓存成功、配置键不匹配自动重解析；`Resolve` 整体 catch 永不抛 | 照 §5.2 第 3 步 |
| 4 | XAML 三下拉挂 SelectionChanged + **AnimFormatCbo 补 `Tag="WebP"` 项**（设置默认档此前在下拉里根本不存在，SetComboByTag 会走补项分支）；回显走 `SetComboByTag`；保存读 Tag；`MainWindow.Localization.cs` + `LocaleManager.AnimWebp` | `FfmpegPath` 无 UI（编辑 settings.json），SaveSettings 刻意不写它 |
| 5 | 引擎改造见 11.2 | |
| 6 | `MainWindow.xaml.cs`：启动处 + RecordHotkeyTxt 分支各一次 `RegisterNamed(this,"record",…,["Ctrl+Alt+G","Alt+Shift+G"])`；`ToggleRecording`（`_recordingBusy` Interlocked，独立于 `_isCapturing`——录制读 SDR 池不排 s_captureLock）；开始 Toast / 停止编码 / `ShowSaveToast(…,"recording")`（§4.3-⑤ 不可达分支变可达，标题按模式说真话："🎬 录制已保存"） | |
| 7 | 会话预建：`CaptureMonitorAsync(new WgcCaptureConfig{ TargetMonitor=hmon, PreferHdr=false })`，忙时捕获其 InvalidOperationException 并 Toast；成功即弃（Result 是副本，池会话已在） | 未加 EnsureSdrSession 新口——CaptureMonitorAsync 现成且形状对 |
| 8 | `CorePipelineTests`：`Test_RecordingActivation_Wiring`（41 断言：实例化唯一、热键两处、会话预建、设置四层、XAML、locator 顺序锁、假选项锁、声明分派、16-bit 回退、自动收尾）+ `Test_AnimatedContainerColor_Surgery`（14 断言真跑：WebP RIFF 结构/画布/奇长补位/负路、PNG 插位/幂等）；PerfWiring A1 断言 8→16 随用户拍板更新 | unit **1865 通过 / 0 失败 / 2 跳过**（1820+45）；ocr 12；format 52 |
| 9 | architecture-design.md 两处 DropOldest 清掉 + §7 重写（激活/FfmpegLocator/声明矩阵）；**:783 的 Magick.NET 也是漂移，一并改**；CHANGELOG 新条目；实机验证见 11.4 | |

### 11.2 引擎（第 5 步）实际改了什么

- `RecordingConfig` + `FfmpegPath`（locator 解析结果）；`FileName` 不再写死 `"ffmpeg"`；
  `Process.Start` 包 Win32Exception（err=2 判"缺席"），错误信息可执行。
- **AnimatedAVIF 显式分支**：`("avif",".avif")` + `-c:v libaom-av1 -still-picture 0 -crf {N} -b:v 0` +
  colorFlags；两处 switch 的 `_ =>` 都改成 `throw InvalidOperationException`（§4.4-① 收口）。
- **声明通道（用户拍板"能声明就声明"后的实测结论）**：
  - AVIF：`-color_primaries bt709/bt2020/smpte432 -color_trc iec61966-2-1 -colorspace rgb -color_range pc`
    ——本机 ffmpeg 实测 colr(nclx) 全部持久化（ffprobe 读回 primaries/trc/space=gbr(=matrix 0)/range=pc），
    **但 muxer 不写 prof(ICC) box、也不搬 PNG iCCP** ⇒ AVIF 声明只有 nclx 一条路；
  - WebP：**muxer 不搬输入 iCCP（实测）** ⇒ 编码后 RIFF 手术嵌 ICCP（新 Core 类
    `AnimatedContainerColor.TryEmbedWebpIcc`：补 VP8X(ICC 位 0x20)+ICCP，从 VP8/VP8L/VP8X 解析画布，奇长补位，RIFF 尺寸回填）；
  - APNG：apng muxer 零声明（实测）⇒ `TryEmbedPngDeclaration` 在 IHDR 后插 cICP+iCCP（顺序与
    ManagedPngEncoder.Encode 一致）；幂等（已存在跳过）；
  - GIF：格式无通道 ⇒ Info 日志声明"像素已烘焙到目标色域"；
  - 中间 PNG 帧：`EncodeFast` 新增可选 `cicp` 参数，中转帧带 cICP（§4.4-② 收口一半——
    ffmpeg 反正不读，但中间文件不再裸奔）；
  - 码点/ICC 全部从 `Decision().TargetTag` 导出（`BuildSdrCicp` + `IccStore` 标准 profile），
    AdobeRGB 无 H.273 码点 ⇒ nclx/cICP 不冒充、ICC 照常（IccStore.AdobeRGB1998）。
- **回退 16-bit**（用户拍板）：`Encode(bakedPixels, w0, h0, fallbackPath, 16, iccProfile: icc, cicp: cicp)`
  ——源是 SDR 8-bit 池，16-bit 是升位深不是增精度（sBIT=8 照实声明），文件翻倍是知情选择；
  降级消息（缺席/失败两版）经 Completed 事件的 ErrorMessage 带给 UI → ShowCaptureDegraded。
- **到点自动收尾**：RecordLoop break 后若非取消，`await StopAndEncodeAsync()` ——
  旧代码到 MaxDuration 后 State 卡在 Recording、无人编码，"录了但永远不出片"。

### 11.3 实现期踩坑（别再踩）

1. **RIFF 与 PNG 的 chunk 布局是两套**：PNG = length(BE32)+type+payload+CRC32；RIFF/WebP =
   type+size(LE32)+payload+奇补位，**无 CRC**。第一版共用一个写入器把 PNG 式 chunk 写进了 RIFF，
   测试里 VP8X 断言"错错相抵"侥幸绿、画布断言红、再往下直接越界崩 —— 后拆 `WriteRiffChunk`/`WriteChunk`，
   测试按各自布局读。
2. **反向源码判据必须去注释**：引擎注释里保留着"_ => (\"webp\" …"的历史引文，`File.ReadAllText`
   直判会永红。PerfWiring 的 CodeOf（剥 `//` 行）是现成惯例，照用。
3. **CRLF**：`Contains("…\n…")` 对 CRLF 文件恒假，跨行形态判据用 `\s+` regex。
4. **测试 fixture 自带声明会让"手术插入"判空转**：real.png 先用 EncodeFast(cicp:) 编了 cICP，
   手术走"已存在跳过"，cICP 内容断言红。要测插入就用裸产物。
5. XAML 编译器 WMC9999（"未将对象引用设置到对象的实例"）是 C# 编译错误的连锁症状，
   先修 C# 再看 XAML。

### 11.4 实机验证（第 9 步，2026-11-03）

（本节为占位——实机四格式录制验证结果由本轮后续小节补记。）


### 11.4 实机验证实录（2026-11-03 00:27~00:52，双屏真机，Debug 构建）

驱动方式：pwsh 脚本起 app（单实例清场）→ `keybd_event` 注入 Ctrl+Shift+G（RegisterHotKey 对
注入输入照常触发；SendKeys/PostMessage 那类线程级手段不行）→ 间隔 N 秒再注入停止 → 读日志 +
ffprobe 验产物。格式切换靠文本预置 `bin\x64\...\win-x64\TrueToneCap.settings.json` 的
`AnimFormat`（app 关着时改，Load→Normalize 会保留）。

| 格式 | 结果 | 产物证据 |
|---|---|---|
| WebP（默认档） | ✅ | 2 帧 4K，`webp_anim` 可解码；**ICCP 2100B 已嵌入（画布 3840x2160 来自 VP8X）**——RIFF 手术真跑通 |
| AVIF | ✅（二次） | 5 帧，nclx **bt2020 / iec61966-2-1 / gbr(matrix 0) / pc** 全部持久化，解码零警告；primaries=9 印证"光标屏=HDR 主屏 → 目标 BT.2020"，声明与像素同源 |
| GIF | ✅ | 2 帧可解码；日志如实声明"GIF 容器无色彩声明通道（格式限制）" |
| APNG | ✅（二次） | 2 帧 `apng` 解码**零警告**；cICP + iCCP + acTL 三 chunk 齐（cICP 是 ffmpeg 从带 cICP 的中间帧自行搬运的——EncodeFast 补 cICP 的意外红利） |
| 回退链路 | ✅（实测触发） | AVIF 超时 Kill → 首帧 16-bit PNG（sBIT=8 + cICP/iCCP）→ "ffmpeg 编码失败——已退化为单帧 PNG（SDR）" → 降级 Toast |

**热键/locator/会话预建链路**：`已注册: Ctrl+Shift+G (id=9003)`、
`FfmpegLocator: ffmpeg 就绪: C:\PLAN\FFmpeg\ffmpeg.exe（git-2026-10-27）`、
预建会话后首 tick 即有帧（`捕获 N 帧` 与录制时长相符）——§5.2 第 6/7 步的行为全部在场。

### 11.5 实机揭出的缺陷（已修，别再犯）

1. **Process.Dispose 后读 ExitCode**：`using (proc)` 块结束 → `proc?.ExitCode` 抛
   "No process is associated with this object"，ffmpeg 明明编成功却报 Error。改为显式先取
   `exitCode` 再 Dispose，读码本身也套 try/catch（超时 Kill 后未等退出同理可能炸）。
2. **PNG chunk 没有奇长补位**：RIFF 的 pad 约定被我带进 PNG 写入器，奇数长度 iCCP 多 1 字节 →
   整条 chunk 链错位，ffmpeg "chunk too big" 拒收。PNG 与 RIFF 是两套布局，写入器必须分开
   （`WriteChunk` 无 pad / `WriteRiffChunk` 有 pad）。
3. **iCCP 载荷缺压缩方法字节**：PNG 规范 11.3.2.5 = 名字\0 + **0x00** + zlib 流；漏了
   ffmpeg 判 "iCCP with invalid compression"。单测已补字节级判据。
4. **`-tiles` 不是 libaom-av1 的选项**（正确名 `-tile-columns`）：写错 ffmpeg 直接退出。
5. **libaom 4K 速度档**：默认 cpu-used=1 → 2 帧编 95s；cpu-used 4 → 24 帧仍超 120s；
   **cpu-used 6 + row-mt + tile-columns 1 → 3 帧 4K 4.3s（~1.4s/帧）**。动图档定这个。
6. **固定 120s 超时挡不住长 4K AVIF**：改 `max(120s, 1.5s × 帧数)` 的线性预算。
7. **声明日志按最终产物说话**：AVIF 超时回退后声明日志仍印 nclx 是撒谎——已按
   (请求格式, 实际扩展名) 联合分派。
8. **`C:\PLAN\tmp-dstream-test` 会被外部周期性清空**（两回整个目录消失，原因未查）：
   驱动脚本别放 C:\PLAN 下，放 `%TEMP%\ttc-dstream\`。
9. 构建撞 MSB3027 = 上一轮 app 还活着（§8-3 的坑再次验证），脚本里 Stop-Process 是必要步骤。

**终态**：unit **1867 通过 / 0 失败 / 2 跳过**（1820 基线 + 47 条新判据）；format 52；ocr 12。
`scripts/gate-baseline.json` 仍是 1807/2438（§9-1 发版前待办，未动）。
遗留：动图 AVIF 的 prof(ICC) box 仍无（ffmpeg muxer 限制，nclx 已够用）；`FfmpegPath` 无 UI
（settings.json 手编）；§9-3 的"播放器肉眼兼容"留给用户抽验（ffprobe/解码全绿）。

---

## §12 交接指针更新 + 审查轮实录（2026-11-03，提交前审查）

### 12.1 旧小节指针作废声明（append-only 纪律下的更正，不回写 §0/§1/§10）

- **§0 状态表 / §5.2 剩余步骤 / §10 交接清单已全部被 §11 取代**：D 流第 2~9 步全部落地并实机验证，
  "从第 2 步接着做""确认工作树仍是 11 个文件"这类指令已失效——当前文件清单以 `git status` 为准。
- §1 的量化读数有两处与 numstat 不符：`MainWindow.xaml` 实为 **+72/-61**（非 "+127"）；
  文件清单 12 个（漏计 `docs/README.md`）。§1 表也未标注 `MainWindow.xaml` 里混有 D 流三处
  SelectionChanged + WebP 项（§11.1 第 4 步有披露）。
- 11.4 有一节**废弃占位**（"本节为占位——实机验证结果由后续小节补记"），真正的实录在其后的
  "11.4 实机验证实录"；读的时候跳过占位节。

### 12.2 提交前三路审查（三个并行只读 agent：D 流 App 接线 / D 流引擎与 Core / A·B 流与文档诚实性）

**P0 = 0**。P1 共 9 条，全部当轮修复；P2 修复了 12 条，其余记档。要点：

引擎与并发：
1. `StopAndEncodeAsync` 的 check-then-act 非原子：到点自动收尾 × 手动停止可双跑 EncodeFrames
   （双 ffmpeg 写同一路径 + List 数据竞争）⇒ 引擎加 `_encodeStarted` CAS。
2. UI 停止把分钟级编码 await 在 `_recordingBusy` 里 ⇒ 编码期热键死键 ⇒ 改为不 await
   （引擎同步翻 Encoding），异常落 `StopRecordingAsync`。
3. `ToggleRecordingCoreAsync` 无 catch：StartRecordingAsync 的异常逃成未观察任务（热键无反应）
   ⇒ 补 catch + toast；`OnWindowClosed` 补 `_recorder?.Cancel()/Dispose()`（孤儿 ffmpeg/临时目录）。
4. warmup 与 RecordLoop 各查一次光标屏，两读间跨屏则预建白做 ⇒ `RecordingConfig.TargetMonitor`
   显式传屏；`ColorSpaceTag` 恒 System 写明是决定不是漏接。

Core 结构手术：
5. **VP8L 画布高解析丢 bit12–13**（掩码 0x03 应为 0x0F）：高 >4096 的无 VP8X WebP 被截断
   （7680×4320 → 7680×224）；fixture 同坑导致测试恒绿 ⇒ 两处同修 + 新增 3840×4320 判据。
   当前调用方（ffmpeg 动画 WebP 恒带 VP8X）不触发，属兜底路径地雷。
6. `TryEmbedPngDeclaration` 补 IHDR 长度==13 校验（类型对长度错的畸形文件不再静默产出坏文件）。

App 接线：
7. **FfmpegLocator.Probe 先 `ReadToEndAsync().Result` 后 WaitForExit**：不关 stdout 的候选进程
   会在锁内+UI 线程无限卡死、stderr 不排空 ⇒ 改与引擎同形状（双读任务 → 超时 Kill → 排空）；
   Resolve 挪进 `Task.Run`（探测进程最坏 3s/候选，别冻 UI）；PATH 条目剥引号。
8. ShowSaveToast 的三条系统通知路径对录制弹"✅ 截图已保存…已复制到剪贴板"双重谎言
   ⇒ `ShowHonestSuccess` 按 mode 分流；输出文件名时间戳加毫秒（同秒重录覆盖）；
   `RecLoop displayInfo==null` 先置 Error 再 Report（否则 UI 永远"录制中"）；
   Dispose 不再清 `_encodedFrames`（与引擎 finally 的并发 Clear 竞争）；
   声明分派补 (_, ".png") 臂序（GIF 回退不再谎称 GIF 无声明通道）；
   `FfmpegPath` 清洗扩到单引号/尾随分号；AnimFormat 空串先落默认（SetComboByTag 补项死循环洞）。

A/B 流（上轮遗留）：
9. **P3-11 实质未修**（审查证实）：静态单槽警告在"入口清一次"下仍会被后建屏抹掉，
   "A 屏警告不再被 B 屏清掉"的保证为假 ⇒ 改为**逐屏会话聚合**
   （PooledSession.DegradationWarning 在各自 Start() 捕获，时机在自身 item 创建之后），
   静态值只作兜底。`拼接元数据`/白点日志的档位同步核对（白点升 Info，兑现 §3.2 承诺）。
10. `HdrCaptureWindow` 注释措辞更正：MonitorFromWindow 取的是相交面积最大屏，非"左上角所在屏"。

测试判据自检（审查轮对判据的审查）：
11. 三条"锁不住声称锁的东西"的判据补实：解析结果必须真流入 `FfmpegPath = ffmpeg,`；
    locator 顺序断言先验三段存在（IndexOf=-1 空洞）；`new AnimationRecorder(` 计数去注释；
    iCCP 长度读改大端（小端读低字节符号随机红/空）；ShowSaveToast 退回判据改两级锁
    （分支调 ShowHonestSuccess + 函数体内有 ToastService 出口）。

**未修记档**：AVIF 无 prof(ICC) box（muxer 限制）；`Cancel()` 全仓零调用（预留 API）；
`AnimatedContainerColor` 峰值内存三份并存（量级可接受）；Win32 错误码非 2 的文案轻度失真；
PNG 手术负路径测试仅 WebP junk 一条。

### 12.3 终态

unit **1868 通过 / 0 失败 / 2 跳过**（1820 基线 + 48 条）；format 52 / ocr 12；构建 0/0。
审查修复后实机复跑一轮 WebP 端到端 ✅（毫秒时间戳文件名、TargetMonitor、声明手术照常）。
本节成文即提交基线。
