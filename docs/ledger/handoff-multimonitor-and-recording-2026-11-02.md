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

> **⚠️ 本节已被 §13 收口（2026-11-03）**：下面六条的处置结果全部记在 §13.1 的逐条表里。
> 本表**保留原样不回写**（append-only 纪律）：它记的是"当时的未知"，§13 记的是"后来的结论"。

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

> **2026-11-03 更新**：上面最后两条已在 §13.4 / §13.5 修掉（三态文案 + PNG 四条负路径，
> 各带变异验证）。前三条维持记档：AVIF prof box 是 ffmpeg muxer 限制不动；
> `Cancel()` 是预留 API；峰值内存量级可接受。

### 12.3 终态

unit **1868 通过 / 0 失败 / 2 跳过**（1820 基线 + 48 条）；format 52 / ocr 12；构建 0/0。
审查修复后实机复跑一轮 WebP 端到端 ✅（毫秒时间戳文件名、TargetMonitor、声明手术照常）。
本节成文即提交基线。

---

## §13 遗留清单收口（2026-11-03，本轮全部处置完毕）

> 承接 §9 的六条未决 + §12 的三条未修记档。本节逐条给"做了什么 / 判据在哪 / 怎么证明它真咬得住"。
> 纪律：凡是"改了产品码"的条目，都必须有一条**变异验证**（把修复摘掉 ⇒ 判据红 ⇒ 装回去 ⇒ 绿），
> 否则那条判据只是装饰。

### 13.1 逐条处置表

| # | 遗留 | 处置 | 落点 |
|---|---|---|---|
| 1 | gate-baseline 仍写 1807/2438（§9-1） | **✅ 用 `-WriteGateBaseline` 正规重生成**：八格全部来自同一次现跑，总断言 **2515**（2438 + 77） | `scripts/gate-baseline.json` |
| 2 | A 流表头溢出（§9-5，`MappingArrow` 布局契约） | **✅ 本轮修**：`TextWrapping="Wrap"` + tooltip 运行期同源，4 条布局契约判据 | `MainWindow.xaml`、`MainWindow.ColorManagement.cs` |
| 3 | `FfmpegPath` 无 UI（settings.json 手编） | **✅ 补设置页输入框** + 回显 + 保存回写 + 保存期校验 + 中英文本地化，5 条判据 | `MainWindow.xaml`/`.Settings.cs`/`.Localization.cs`、`LocaleManager.cs`、`FfmpegLocator.cs` |
| 4 | B 流无 CHANGELOG 条目（多屏修复是用户可感知的） | **✅ 补**：新条目「双屏/多屏：选区色彩与气泡都跟着那块屏走」六条 bullet | `CHANGELOG.md` |
| 5 | Win32 错误码≠2 文案失真、PNG 手术无负路径测试（§12 记档） | **✅ 顺手修**：三态文案 + PNG 手术四条负路径（7 条判据） | `AnimationRecorder.cs`、`CorePipelineTests.cs` |
| 6 | §9-6 冲突提示 | **✅ 结案**：`fallback` 列表 + Error 日志已覆盖（§6 记档原判成立，不另加提示） | 无改动（判定即处置） |
| 7 | §9-3 播放器肉眼验证 | **✅ 自动化部分已尽，留清单给用户**：`tests/manual/media-player-visual-checklist-2026-11-03.md` | 新增手动清单 |

§9 里其余两条（§9-2 动图色彩声明 / §9-4 回退位深）已在 §11 由用户拍板并落地，不属本轮。

### 13.2 遗留 2：`MappingArrow` 布局契约（§9-5）

**病灶不是文案，是列宽装不下**：固定 120px 首列 + 最长两支文案
（`→ 色调映射为 SDR（屏幕证据不授权 HDR 声称）`）⇒ 不换行就溢出自己的格子、视觉上撞进第二列
的 `TargetGamutTxt`。2026-11-02 在**未修改的发布版**上用 UIA 取证过是既有缺陷，不是 A 流引入。

**契约（写进 XAML 注释，判据锁住）**：
1. 列宽**不动**（`120,*`）—— 与「显示器色域」那行保持同一列基线；
2. 标签在自己的格子里 `TextWrapping="Wrap"`，绝不越界；
3. 换行后行变高是可接受的代价，**不**偷改列宽去挤第二列。

**tooltip 的处理（这里改了两轮）**：第一版把说明写在 XAML 的 `ToolTipService.ToolTip` 里，
内容是"2026-11-02 A 流遗留…"这种开发注释——两处都不对：① 用户不该看到开发备忘；
② 本地化判据要求 XAML 每条中文都进 `ZhToEn`，写死就等于维护第二份会分叉的文案。
最终形态：**tooltip 由 `RefreshColorMappingUi` 在运行期设成与标签同一个 `arrow` 局部量**
（不是在 tooltip 里再调一遍 `Tr(...)` —— 那会重新变成两份真相）。

**变异验证**：摘掉 `TextWrapping="Wrap"` ⇒
`❌ MappingArrow 允许换行（TextWrapping=Wrap）—— 长文案在自己的 120px 列里折行，不越界撞第二列`
（1 失败）。装回 ⇒ 1884/0。

### 13.3 遗留 3：`FfmpegPath` 设置页输入框

原来这一格只存在于 `settings.json`，用户要关掉 app 手编文件。补完后四层都在：

| 层 | 落点 | 关键约定 |
|---|---|---|
| 控件 | `MainWindow.xaml` 动图录制卡片 | 与相邻设置行**同一套列定义**（`120,*`），不自成排版 |
| 回显 | `ApplySettingsToUI` | 空值回显成空 —— 不补兜底文字，否则"没配过"变"配了个假路径" |
| 保存 | `SaveSettings` | 清洗链与 `SettingsService.Normalize` 同形；**空串是合法值**（= 自动解析），不写 `?? 兜底` |
| 校验 | `FfmpegLocator.DescribePathIssue` | 目录 / 文件不存在 / 非 `.exe` 三类各有专句；**永不抛**，且**不**实跑 `-version`（保存是同步 UI 路径，不能起子进程） |

**为什么校验放在保存那一刻而不是等录制失败**：路径填错这件事在按下保存时就已经确定，
让用户等一次录制失败才知道，等于把一次可立即纠正的笔误拖成一次产品故障。
但**只警告、不拒绝保存** —— 文件可能稍后才到位，而 locator 本来就按"配置无效 ⇒ 回退自动解析"工作。
有效路径会 `Invalidate()` 掉缓存，否则改完路径还继续用上次解析的那一枚。

**变异验证**：摘掉 `_settings.FfmpegPath = (FfmpegPathTxt.Text ?? "")` 回写 ⇒
`❌ SaveSettings 从输入框回写 FfmpegPath`（1 失败）。装回 ⇒ 1884/0。

### 13.4 遗留 5：`Win32` 错误码 ≠ 2 的文案失真

原实现只有 `ex.NativeErrorCode == 2`（`ERROR_FILE_NOT_FOUND`）才算"缺席"，其余一律落到
`"ffmpeg 编码失败 —— 已退化为单帧 PNG（SDR）"`。问题是：**进程压根没起来**（拒绝访问 5 /
找不到路径 3 / 不是有效的 Win32 程序 193 / 参数错误 87）时，一个字节都没编过，说"编码失败"
会把用户引去查片源和格式，而真正要改的是路径或权限。

改成**三态**：

| 态 | 判据 | 文案 |
|---|---|---|
| 缺席 | `NativeErrorCode is 2 or 3` | `未找到 ffmpeg：请在设置里指定路径，或把 ffmpeg 加入 PATH` |
| 起不来 | 其他 `Win32Exception` | `ffmpeg 无法启动（0x…(N) 原因）——请检查该文件是否可执行、以及安全软件是否拦下` |
| 编码失败 | 进程跑过但退出码非 0 / 无产物 | `ffmpeg 编码失败` |

日志里另外保留完整 `0x{code}({code}) {Message}`，便于贴给上游查。

### 13.5 遗留 5 后半：PNG 手术的负路径（§12 记档原话"仅 WebP junk 一条"）

`TryEmbedPngDeclaration` 有四个 `return false`，每个都是一句"畸形输入不会产出坏文件"的承诺——
没有判据的承诺等于注释。补了四条，**每条都带"原文件字节未变"的强判据**（拒绝 = 不写盘，
不是先写了再报错）：

| # | 输入 | 守的是哪个分支 |
|---|---|---|
| ① | 非 PNG（任意 12 字节） | 签名守卫 —— 不拒就会在第 33 字节插 chunk，造出杂种文件 |
| ② | 签名对、`IHDR` 类型对但**长度=15** | 审查轮 R2-P2-2 那条：不校验长度就切进载荷中间静默产出坏文件 |
| ③ | `cICP` 3 字节 / 5 字节 | 载荷恒 4 字节契约 —— 别的长度会让读回方跨到隔壁 chunk 头 |
| ④ | 两个声明都为 `null` | 唯一"成功却不写盘"的返回 ⇒ 调用方要能区分"没东西可嵌"与"嵌失败" |

**变异验证（这条最值钱）**：把 `BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)) != 13`
改成 `false`（等于摘掉 IHDR 长度守卫）⇒ 负路②两条同时红，且**实际产出了坏文件**：

```
❌ 负路②：IHDR 长度≠13 的畸形 PNG 被拒绝（不再切进载荷中间产出坏文件） —— cICP+iCCP 已插入 IHDR 之后
❌ 负路②的强判据：畸形文件保持原样（拒绝不许留下半截写入）
```

装回 ⇒ 1884/0。**这就是 §12 记档时那条"没有负路径测试"背后真存在的风险**：
守卫一旦被误删，没有任何东西会喊。

### 13.6 遗留 1：gate-baseline 正规重生成（附带修了 writer 的两个缺陷）

`-WriteGateBaseline` 跑完（`GATE_EXIT=0`、8/8 套件、**2515 断言**、无 BOM 已复核），
但生成结果暴露了脚本自身两个缺陷，一并修掉：

1. **`总断言` 变成浮点**：`Measure-Object -Sum` 的返回类型随输入浮动，把 2515 写成了 `2515.0`。
   旧版是整数 `2438`。`ConvertFrom-Json` 读得回、`-lt` 比较也不受影响 ⇒ **这个瑕疵不会自己变红**，
   只会让每份历史 diff 都带一条噪声。修法：`$totalPassed = [int]$totalPassed`。
2. **`_说明` 里的历史沿革被整段冲掉**：旧版是个固定字面量，每次重生成就把 10-31 三段手改记录
   （每次为什么加/减、删的是哪一条判据、为什么删）全冲了。那是"地板为什么长成这个数"的唯一解释。
   修法：**沿革只追加不重写** —— 读回旧 `_说明` 保留，追加本轮一行；并跟着记"上一版总断言与取数时间"，
   这样即便有人手工删了沿革，相邻两次读数之间的跳跃仍留痕。

**同轮验证（第二轮门禁）**：`-SkipBuild` 再跑一次吃新基线 ⇒
`✅ 8 个套件全部通过 — 共 2515 断言`，无"断言数从基线掉落"告警。基线比对真的生效。

### 13.7 本轮读数与"没判"清单（诚实口径）

- **构建**：`dotnet build src\TrueToneCap.App -c Debug` ⇒ **0 警告 0 错误**（42.4s）。
- **门禁**：`scripts\verify.ps1 -Configuration Debug -WriteGateBaseline` ⇒ `GATE_EXIT=0`，
  8/8 套件，**2515 断言 / 0 失败**（unit 1884 跳 2；service 2 跳 1）。
- **变异验证 3 条全部咬住**（§13.2 / §13.3 / §13.5 各一次），每次恢复后都回到 1884/0。
- **没判的**（不是通过）：
  - `--gainmap-color-product` 仍 ⏭ SKIP（两个候选 `ultrahdr_app.exe` 都过不了能力探测）。
    补上认新 CLI 的外部件后要重跑。
  - §9-3 播放器肉眼兼容（本轮给了清单 `tests/manual/media-player-visual-checklist-2026-11-03.md`，
    仍需人执行）。
  - 本轮**没有**做 §9-2 的 AVIF prof(ICC) box（ffmpeg muxer 限制，nclx 已够用，维持 §12 记档）。

### 13.8 本轮环境坑（下次别再踩）

1. **`pwsh` 工具在 `workspace-write` 下完全不可用**：每次调用返回空输出 + 退出码 `3221225794`
   （`0xC0000142` / `STATUS_DLL_INIT_FAILED`），连 `echo hello` 都不例外。`danger-full-access` 下正常。
   这不是本仓的 bug，是这台机器的沙箱形态（历史台账里多次出现）。
2. **`edit` 工具改完要立刻回读**：本轮我在 `ApplySettingsToUI` 里一度写出重复块、在 `SaveSettings`
   里把方向写反（设置→UI），都是靠回读 + 构建 + 判据三重才发现。判据那条
   `SaveSettings 从输入框回写 FfmpegPath` 正是在这一步抓到的。
3. **C# 字符串里别用中文引号**：`"…"` 当定界符是语法错误，但视觉上和 `"…"` 极像。
   编译期会拦，别指望肉眼。
4. **XAML 里写中文就得进 `ZhToEn`**：`界面中文本地化覆盖` 那条判据要求 XAML 每条中文静态文字
   都在对照表里。往 XAML 加 tooltip/placeholder 前先想清楚要不要走 `LocaleManager` 属性。

### 13.9 遗留清单终态

**§9 六条 + §12 三条记档，全部处置完毕，无一条悬空**：

- 4 条已修并上判据（§9-5 表头溢出、`FfmpegPath` UI、`Win32` 文案、PNG 负路径）；
- 1 条已重生成（§9-1 baseline，2515）；
- 1 条已补文档（CHANGELOG B 流条目）；
- 1 条判定即结案（§9-6 冲突提示，现有兜底已覆盖）；
- 1 条转手动清单（§9-3 播放器肉眼，自动化部分已尽）。

下一轮接手时：`scripts/gate-baseline.json` 是 **2515**（unit 1884），
`--unit-tests` 少一条就会红并点名。

> **⚠️ 2026-11-03 §14 更正**：本节数字已成过去时——全量审查（§14）又修了 7 处判据/工具/文档缺陷
> 与 1 处产品数据丢失窗口，终态为 **2518（unit 1887）**。§13 正文按 append-only 纪律不回写，
> 读数一律以 §14.5 为准；§13.6 那句 "+77 = 本轮收口三条遗留" 是账目错误（61 条是 B/D 流未入册
> 的累积），更正后的构成在 `scripts/gate-baseline.json` 的 `_说明` 与 §14.2。

---

## §14 提交前全量审查（2026-11-03，对 §13 整轮的自审）

### 14.1 审查方式（含一次失败的方法学记录）

原计划四路并行只读审查（Lane A App 接线 / Lane B 引擎与 Core / Lane C 判据与门禁 / Lane D 文档诚实性），
**四路子 agent 全部中途崩溃、零产出**（加上更早的 changelog-scribe / player-checklist 两路，
本会话子 agent 通道六路全灭——环境问题，与仓库无关）。改由我自己按同样四条 lane 逐条过，
外加两类审查 agent 给不出的东西：**判别性变异**（见 14.4）与**探针实测**（见 14.2-⑦）。

### 14.2 发现并已修复的缺陷（7 处；每处都留了证据）

按发现顺序，标的是"缺陷当时处在哪一层"：

**① 判据假绿：新判据比它替换掉的旧判据更弱（测试侧）**
旧判据 `!ms.Contains("_settings.FfmpegPath =")` 是"任何回写都禁止"——加了 UI 后必须放宽，但我放宽时
把保护也丢了：兜底检查写成"同一句的 200 字符内找 `?? "`"，实测两种写法都能绕过（回写之后另起一句
`if (string.IsNullOrEmpty(...)) _settings.FfmpegPath = "ffmpeg";`、或紧跟着 `.Trim() ?? "ffmpeg"`）。
拆成三条（回写在不在 / 那条语句取得到 / 语句窗口内不许带兜底）并变异验证（见 14.4-1）。

**② 判据假绿：正向判据读的是未剥注释的原始文本（测试侧）**
本方法里 `ms/ml/f/lm` 全是 `File.ReadAllText` 原文——注释里写同一句代码也会命中，等于"把代码删掉、
只留注释"判据照样绿。仓内早有惯例（本文件 `a` / `mCode` 两个变量就是为这存在的，R2-P3），我抄漏了。
修复：补 `msRaw/fRaw/mlRaw/lmRaw`，并顺手删掉我重复造的局部同名 `StripLineComments`（类级 L8753 已有，
两份真相）。判别性变异：把回写整段改成注释 ⇒ 两条红（14.4-2）。

**③ 判据假绿的假修复：对 XAML 用 `//` 行剥法 = 一条注释都没剥（测试侧）**
第一刀修复里 `xRaw = StripLineComments(x)`——XAML 的注释是 `<!-- -->`，行剥法对它**一条都剥不掉**，
所以那刀"修复"本身是假的，判据读的仍是原始文本。改用本仓既有惯例
`Regex.Replace(@"<!--.*?-->", "", Singleline)`（`Test_FormatLabels_AreUserFacingNames` 里同款）。
判别性变异：真控件删掉、注释里留一份带 `TextWrapping="Wrap"` 的假声明 ⇒ 三条 MappingArrow 判据全红
（14.4-3）；FfmpegPathTxt 同型变异也红，且顺带牵出两条既有本地化死键判据正确工作（注释里的中文
不再算活引用 ⇒ 死键计数 1）。

**④ 产品缺陷：`FfmpegPath` 的静默数据丢失窗口（App 侧，本轮审查抓出的最重要一条）**
空串在这格是**合法值**（= 自动解析），于是"控件还没回填就保存"在别的格只是显示问题，在这里是
**无痕的数据丢失**：用户手改在 settings.json 里的 `FfmpegPath`，若 `ApplySettingsToUI` 有任何一句在
回显（回显位于方法中部）之前抛异常，退出路径的 `SaveSettings()` 是被 `try { } catch { }` 吞掉的
（MainWindow.xaml.cs 关闭回调两处），那一格会以空串写回盘上。已核对：`SaveSettings` 三个调用点都在
关闭/退出/显式保存，构造期顺序安全（回显先于任何保存）；但异常路径的"不可达"不成立。
修复：`_ffmpegPathEchoed` 闩——回显成功才置真，保存只闩真时回写，与仓内既定惯例
（OverlayColorCbo"没有选中项就保留盘上原值"，2026-10-01）同向。两种变异都验证会红（14.4-4）。

**⑤ 工具缺陷：baseline `_说明` 每轮净增 ~250 字符且无上限（脚本侧）**
我修"沿革被重生成抹掉"时只做了追加——追加的东西下一轮被当成旧沿革读回来再追加 ⇒ 每跑一次
`-WriteGateBaseline` 净增约 250 字符。而且 header 每轮重新前置、读回时不认 ⇒ 实测连跑两轮后
header 出现 3 次。修复：沿革封顶最近 8 轮（更早的压成一行省略摘要，保留 10-31 手改史原文）+
读回先按行剥 header。验证：种入 10 条假轮次跑一轮，省略行出现、长度回落；再跑一轮 header 恒为 1。

**⑥ 账目不实（文档/基线侧，两条）**
- CHANGELOG B 流条目写"新增 13 条判据…unit 1820 → 1868"——13 条对应 1807→1820，1820→1868 是
  D 流 +48。一句里两个增量混装。已改成 1807 → 1820（与 §9-1 的"实际 1820（B 流 +13 条）"对齐）。
- baseline `_说明` 把 +77 全记成"本轮收口三条遗留"——实际 61 条是 B 流（+13）与 D 流（+48）
  **从未入册的累积**（基线 10-31 之后就没人重生成过），本轮真正新增只有 +16。已重写构成
  （+13 / +48 / +16 三段分记）。这条能被抓出来靠的是把 4+5+7=16 与 77 对不上当成线索往下挖。

**⑦ 不实注释（App 侧，防御性代码的诚实口径）**
`DescribePathIssue` 的 catch 注释声称"非法字符 / 超长路径 / 无权限枚举会走到这里"。探针实测
（临时 console 工程，.NET 11 同运行时）11 类边界输入——null / 空白 / 含 NUL / 400 字符超长 / UNC /
末尾反斜杠 / 目录 / 非 .exe / 不存在——**零抛出**：`File.Exists`/`Directory.Exists` 对畸形输入返回
false 而不抛，`Path.GetExtension` 只在 null 时抛而上面已用 `?? ""` 挡掉。catch 当前是死代码，
留作防御（承诺"永不抛"不该押在"今天的 BCL 恰好不抛"上），注释改成如实说"实测进不去、为什么还留着"。

### 14.3 判据的两处顺带加固（同一批）

- "MappingArrow 所在行仍是 120px 固定首列"原本是紧邻正则（Grid 与元素之间只许有注释）——
  它把"注释在不在"变成了通过条件，**逼着我把布局契约注释挪出 Grid**（判据扭曲代码，方向反了），
  且别人日后在 Grid 里加一句注释它会假红。改成位置判据：从元素往前 `LastIndexOf` 最近的
  `<Grid ColumnDefinitions=`。变异：列宽 120→200 ⇒ 红并印出 `<Grid ColumnDefinitions="200,*">`。
  注释随之挪回 Grid 内原位。
- tooltip 判据原先只看元素自身标签——tooltip 挂在外层 Grid 上它会漏。扩成"Grid 开标签 + 元素标签"合并判。

### 14.4 变异验证台账（审查轮 6 次，全部咬住）

| # | 变异 | 预期 | 实测 |
|---|---|---|---|
| 1 | `SaveSettings` 里插入 `if (IsNullOrEmpty(…)) … = "ffmpeg"` 兜底行 | 旧判据放行、新判据红 | ✅ 旧判据（拆分前）对此全绿；新"不许带兜底"红 |
| 2 | 回写代码整段注释掉（代码没了、注释还在） | 正向判据必须红 | ✅ "回写"与"不许带兜底"两条红 |
| 3 | 删真 MappingArrow、注释里留带 Wrap 的假声明 | 读原始文本会假绿 | ✅ 三条 MappingArrow 判据红 |
| 4 | 删真 FfmpegPathTxt、原样放进 `<!-- -->` | 同上 | ✅ 控件判据红；本地化死键 +1（既有判据正确联动） |
| 5 | MappingArrow 列宽 120→200 | 列宽判据红 | ✅ 红且印出 `<Grid ColumnDefinitions="200,*">` |
| 6 | 回显闩：摘掉 `&& _ffmpegPathEchoed` / 置真挪到回显之前 | 闩判据红 | ✅ 两种变异各红一次 |

§13 收口轮的 3 次变异（摘 Wrap / 摘回写 / 摘 IHDR 长度守卫）见 §13.2/§13.3/§13.5，不重复记。

### 14.5 终态（取代 §13.7 的读数）

- **构建**：`dotnet build`（App 与全解）⇒ **0 警告 0 错误**。
- **门禁**：`scripts\verify.ps1 -Configuration Debug -WriteGateBaseline` ⇒ `GATE_EXIT=0`，
  8/8 套件，**2518 断言 / 0 失败**（unit 1887 跳 2；service 2 跳 1），宿主新鲜度 ✅，基线无 BOM 已复核。
- **账目自洽**：八格之和 = 总断言 = 2518；1868（§12.3）+16（§13 收口）+3（§14 审查）= 1887；
  1807 + 13（B）+ 48（D）+ 16（收口）+ 3（审查）= 1887。
- **没判的**（不是通过）：`--gainmap-color-product` 仍 ⏭ SKIP（两个候选 ultrahdr_app 都过不了能力探测，
  补认新 CLI 的外部件后重跑）；§9-3 播放器肉眼仍待用户按
  `tests/manual/media-player-visual-checklist-2026-11-03.md` 执行；AVIF prof(ICC) box 维持 §12 记档不动。
- **已知代价**（有意接受，记档）：回显闩防的是"回显前抛异常"，代价是"回显完成前用户就改了这格"
  会丢一次编辑——那条路径要求用户在构造期一瞬之内改到一个还没显示的输入框，不可达；
  而它防的"静默清掉手改路径"是可达且无痕的。

### 14.6 本轮踩坑（下次别再踩）

1. **`pwsh` 工具在 `workspace-write` 下整轮不可用**（`0xC0000142`，连 `echo` 都失败），全程
   `danger-full-access`。会话中段用户把文件策略切成 `danger-full-access` 后才恢复。
2. **PowerShell 内嵌 here-string 的引号坑**：`-replace` 的替换串里带引号 + here-string 结尾标记同行
   ⇒ ParserError；C# 代码片段里的 `\"` 转义同样翻车。第二次起一律把 python 脚本写到 `%TEMP%\probe\`
   再执行（§8-4 的惯例），一次过。
3. **变异验证必须做判别型**：把"真元素删掉、注释里留假元素"才验得出剥注释是否生效——只摘属性
   （如只摘 Wrap）验不出"读的是注释还是代码"，因为真元素还在原位、匹配到的仍是它。
4. **修 A 引入 B 要再审一轮**：本轮 7 处缺陷里有 3 处（⑤ 的两个、⑥ 的 CHANGELOG 一处）是
   §13 收口轮自己修东西时带进来的。审查对象必须包含"为修它而写的每一行"，不只是产品码。
5. **`git show HEAD:file` 取原文**是清污染的锚：沿革被我种过假轮次、改过账目，最后靠逐字取回
   HEAD 的 10-31 三段史重建，避免"重写历史 = 第二份真相"。（顺带：HEAD 原文里
   `scripts\u000berify.ps1` 那个 `\v` 竖直制表符是 10-31 就存在的字节，本轮逐字保真未"修"。）

### 14.7 审查后的遗留清单终态（取代 §13.9 的读数行）

§13.9 的五条处置**全部维持成立**，只有读数行作废：`scripts/gate-baseline.json` 终态
**2518（unit 1887）**，`--unit-tests` 少一条就会红并点名。本轮全部改动未提交，工作树
14 个跟踪文件 + 1 个新增（播放器清单，`.gitignore` 已点名入库）。


## §15 增益图元数据三档 + AVIF 双 HDR 承载（2026-11-03，用户口径两拍板）

### 15.1 需求与结论

用户原话两个诉求：①"分析 GainMap 增益模式下的 JPEG 是怎么写入元数据的，改为只写最新标准
ISO 的元数据"；②"PNG 默认不开启 HDR、WebP 没办法开启；AVIF 允许传统 HDR 也允许 GainMap HDR，
加开关，参考隔壁 ffmpegPictureUI"。

结论：
- **JPEG**：旧实现恒双写（底图 APP1 `hdrgm` XMP + 次图 APP2 ISO 21496-1）。现改为三档
  `GainMapMetadataMode { Iso, Xmp, Both }`，**默认 Iso**（对齐 libultrahdr 上游默认
  `UHDR_WRITE_XMP=FALSE / UHDR_WRITE_ISO=TRUE`）。MPF 偏移改按档位动态算（旧代码假设 APP1
  恒在，iso 档会多算 29B 导致次图定位失败）。顺带修真 bug：Xmp/Both 档现在**次图也写属性形式
  XMP**（libultrahdr 只从次图读元数据，旧版只写底图会被参考读手判"无有效增益图元数据"）。
- **PNG 默认 HDR / WebP 无 HDR**：均为预期行为非 bug。HDR 是全局开关（`AppSettingsData.HdrEnabled`
  默认 true + "HDR 屏自动开"规则 + `HdrUserToggled` 一旦置真永久尊重）；WebP 是
  `SupportsHdr: false`（有损 WebP 容器无 HDR 传输函数通道），开关置灰正确。
- **AVIF**：新增 `AvifHdrMode { Pq, GainMap }`，**默认 Pq**（=本轮之前唯一 HDR 路，老用户产物
  逐字节不变；也是 NVENC/QSV 硬件直通唯一支持的形状）。GainMap 档像素阶段与 JPEG 出口
  **逐字同源**（`PrepareGainMapColorSpace / ComputeWhitePoint / ReinhardToSdr / ComputeGainMap /
  RescaleGainMap` 全部 internal 化复用），只换容器；恒交付 3 平面（Gray 复制 ch0 成套，
  与 libavif 对单通道"参数复制成套"处理等价）；元数据恒 ISO 21496-1（tmap 只承载这一种）。

### 15.2 AVIF 容器写出器（移植 + 三处必须改）

从隔壁移植 `IsoBmffGainMapWriter`（纯托管 ISO-BMFF tmap 容器组装，两遍法算偏移），
但照搬就是错的，三处适配：
1. **不 patch 底图 colr**：隔壁底图来自 ffmpeg `avif` muxer（实测不采纳 -color_primaries，
   写出 unspecified），必须重写 nclx；我们的底图来自 avifenc `--cicp`，实测 colr 恒正确
   ⇒ patch 反而改坏。只给 tmap 项追加替代图（HDR 侧）colr（默认 16/6/1，与 libavif 官方
   seine_sdr_gainmap_srgb 样本逐字段一致，avifdec 实读核对）。
2. **Temporal Delimiter 保留为防御**：实测 avifenc 每个 item 首字节已是 0x12(TD) ⇒ no-op，
   留着挡"哪天换无 TD 编码器"。
3. **auxC（alpha 副 item）拒绝而非静默丢**：实测恒不透明 RGBA ⇒ avifenc 丢 alpha 出单 item，
   真半透明才出 auxC；写出器只搬主 item ⇒ 检测到副 item 就拒写，绝不出"丢 alpha 谎件"。
   （另实测：写出器 pixi 通道对撞门、"增益图 > 底图尺寸"拒绝门都在。）

**外部裁判**：`avifdec --info`（隔壁 libavif build，560KB）完整读出我们的手拼容器
（`Gain map: … Alternate Headroom 2.32 (HDR)` + `Alternate image: Transfer Char. 16`）。
`ultrahdr_app v2.0.2` 读 libavif 自家增益图 AVIF 样本报 "does not contain gainmap image"
（exit -1）——这就是门禁 `--gainmap-color-product` 长期 SKIP 的根因，仍未解决。

### 15.3 判据与变异（本轮 4 套件全绿后的读数）

- unit **1907**（+17）、color **300**（+13）、encoding 23、usability 225，全 0 失败。
- 新判据：`Test_AvifHdrMode_DefaultIsPq_AllLayers`（类型默认真跑 + App 四处源码形态同向）、
  白名单 `AvifHdrModeCbo` 交叉、`EncodedAvif_GainMapContainer`（ftyp tmap 品牌 / iinf 3 item /
  dimg 引用体逐字节 / grpl.altr / tmap 142B flags bit7 / **默认 PQ 档不含 tmap**）。
- **变异一**（分派 `if (false && …)`）：7 条容器结构判据全红 ⇒ 开关真换容器。
- **变异二**（dimg 砍掉增益图引用）：**首轮判据全绿、avifdec 当场报
  "Invalid tone mapped image item"** —— 自解析抓不住引用体内容，是"自解析≠合法"的活教材。
  补 `TryReadDimgBody` 逐字节验 `from=2/count=2/[1,3]` 后重放变异 ⇒ 红；恢复 ⇒ 绿。

### 15.4 外部裁判推翻过一次界面文案

AVIF 增益图档的卡片文案初版写"底图是 SDR 并嵌 ICC"，avifdec 实读 sRGB 目标产物
`ICC Profile: Absent`（只有显式 Rec.2020 才嵌）⇒ 界面在撒谎。改为"底图是 SDR（声明同普通
SDR 产物）"。教训：容器文案必须由读手实读校准，不能照实现意图写。

### 15.5 措辞/接线判据的连锁（判据教我补的）

改 `Declaration` 加第 5 参 `avifHdrMode` 后：调用形态判据（钉死带 `LocaleManager.Tr` 的整句）
红 ⇒ 同步改判据并要求 `avifMode` 一路；`BuildGamutHint` 形参名从 `avifHdrMode` 统一成
`avifMode`（判据钉整句）。AVIF Hint 改口径 ⇒ ZhToEn 死键判据红（旧中文串还在表里）⇒ 换键。
AVIF 增益图档声明新片段 ⇒ "App 清单点名" + "ZhToEn 有译文"两条判据红 ⇒ 三处逐字同步
（Core 片段 / App 登记 / 对照表）。两个中转 PNG 站点初写用 `ManagedPngEncoder.Encode` ⇒
"PNG/TIFF 档位接线"判据红（它是产物站点计数器，中转必须 `EncodeFast`）⇒ 改用 EncodeFast，
与 HDR-PQ 路 `Encode16(fastest: true)` 同纪律。

### 15.6 基线更新与包

`scripts/verify-publish-gates.ps1` 首跑 4/8 红——**不是代码问题**：15:44 实机验证 HDR 能力时
启动的旧 beta 包（`publish\TrueToneCap-v0.3.10-beta\TrueToneCap.exe`，PID 132500）没退出，
Publish.ps1 的运行中检测正确地拒绝了 stub 场景。结束进程后 8/8。教训记下：跑门禁前先查
`Get-Process TrueToneCap`。

**全门禁重跑通过（exit 0），`-WriteGateBaseline` 已写入**：总断言 **2518 → 2568（+50）**，
取数 2026-11-03 21:16:08。分套件：unit 1887→**1907**（+20：默认链 Iso 六处 8 + AVIF 默认链
Pq 四层 5 + 白名单交叉 2 + 调用形态/死键/清单/档位连锁修判据后净 +5）、color 270→**300**
（+30：JPEG 三档全覆盖改写 + AVIF 增益图容器结构含 dimg 引用体 + ftyp/盒族）、encoding 23、
usability 225、service 2 跳 1、ocr 12、qr 47、format 52（后五格不变）。同轮
`--gainmap-color-product` 仍 ⏭ SKIP（ultrahdr_app 读 AVIF 增益图样本报
"does not contain gainmap image"，能力探测不过），不是通过。

CHANGELOG「未发布」节已加两条用户口径（JPEG 元数据三档 / AVIF 双承载）。

**包已发**：`.\Publish.ps1 -Version 0.3.11-beta -AllowDirty`（沿用上一包 dirtyWorktree=True
的惯例，27 项未提交改动与 HEAD 6e3895f 一并记入 build-info）。发布脚本内含测试门禁与
SHA256SUMS，exit 0；包 613 文件 / 417.7 MB，位于 `publish\TrueToneCap-v0.3.11-beta`。
包内 exe 冒烟（--display-audit）正常：HDR 硬件支持=yes。

## §16 元数据不做用户抉择：JPEG 恒写 ISO 21496-1（2026-11-03 晚，二次收口）

### 16.1 用户口径与处置

用户看完 v0.3.11-beta 后追加："元数据方式 JPEG 应该只产出 ISO 标准的这一种，不应该让用户去
进行抉择。"——上午 §15 的三档下拉（Iso/Xmp/Both）整体退役：

- **Core**：`WriteJpegGainMapFile` 删 `metaMode` 参数，`writeIso=true/writeXmp=false` 字面固化；
  `GainMapMetadataMode` 枚举缩成单档并标 `[Obsolete]`（新代码不许引用）；`EncodingSettings.GainMapMetadata`
  字段删除；`BuildXmpMetadata` 等 hdrgm XMP 生成器**保留为 public**（文档级参照 + 测试对照），
  不再写进产物。
- **App**：XAML 下拉/提示行、`OnGainMapMetadataChanged`、`ApplySettingsToUI` 回显、`SaveSettings`
  回写、`AppSettingsData` 字段、`SettingsService.FitTag` 白名单、管线三态映射、`LocaleManager`
  七个运行时属性 + 两个 ZhToEn 键、能力卡文案（"写哪份元数据见设置页"改为"恒写 ISO 21496-1
  最新标准"）——全部拆除。settings.json 遗留键被 JSON 反序列化静默忽略，无需迁移。
- **Test**：`EncodedJpeg_GainMapMarker` 从三档循环改为单档正反双钉（ISO 恒在 + MPF 恒在 +
  Base/次图两侧 hdrgm XMP 出现即回归）；`Test_GainMap_MetadataDefaultIsIso_AllLayers` 改为
  **退役形态反钉**（EncodingSettings 反射无成员 + 五份源文件负向 Contains + 写出器签名无
  metaMode 且含 `bool writeIso = true;`）；白名单交叉行删除。

### 16.2 判据自己抓的回归

负向 Contains 连注释也算：AppSettingsData 与 SettingsService 的"已删"注释里含
`GainMapMetadata` 字样 ⇒ 判据红。改写注释措辞（不含该词）后绿——注释不是豁免区，
这条对今后删字段类重构同样有效。

### 16.3 验证读数

- 四套件全绿：unit **1904** / color **282** / encoding 23 / usability 225。
- **变异**（`writeIso=true` → `false`）：ISO 恒在判据红 ⇒ 恢复 ⇒ 绿。源确认
  `bool writeIso = true;`。
- **外部裁判**：ultrahdr_app v2.0.2 解码 ISO-only 产物 exit 0（32×32 RGBA8888 = 4096B，
  非零字节 4047 = 真实图像数据）。⚠ 该工具写不了 `%TEMP%` 与 artifacts（后者 ACL 有 DENY DC），
  只能写在自己 exe 同目录——探针类工具的落盘约束各异，失败时先换路径别急着判产物坏。
- 全门禁重跑 exit 0，`-WriteGateBaseline` 写入：总断言 **2568 → 2547**（−21：三档循环判据
  收敛成单档 + 白名单交叉行退役），unit 1907→1904、color 300→282。
- 重新出包 `v0.3.11-beta`（同版本号覆盖式重发：上一包发出后口径即变，不发 0.3.12 避免把
  三档下拉当"已存在过的功能"留在版本史里）。

## §17 完整审查轮：四处实现问题 + 两处判据自身缺陷（2026-11-03 深夜）

用户要求"进行一轮完整审查，确保相关实现正确"。审的是 §15/§16 两轮的全部产物码。
结论：**发现 4 处实现问题（其中 2 处是真行为错误）与 2 处判据自身缺陷，全部修复并验证**。

### 17.1 实现侧四处

**① AVIF 替代图 matrix 与底图不同源（真错误，字节级证据）**
`IsoBmffGainMapWriter.Build(...)` 的调用把 `alternateMatrix` 写死 `6`，而底图走
`FormatHelper.AvifMatrixCoefficients(altPrim)`（709⇒1 / 2020⇒9）。于是 Rec.2020 目标下
同一个文件里出现"底图 matrix=9、替代图 matrix=6"的自相矛盾组合。
实测对照：libavif 全部官方增益图样本里两者都同源（1/13/6 配 1/16/6）。
改为 `FormatHelper.AvifMatrixCoefficients(altPrim)`。
⚠ 我最初的担心（"1/16/6 不自洽"）被实测推翻——sRGB 样本的替代图就是 1/16/6，
libavif 自己的惯例。**不查证就把直觉当结论会改错方向。**

**② AVIF 底图 ICC 来源不一致（真错误）**
`PrepareGainMapColorSpace` 返回的 `baseIcc`（像素搬到 2020 时配套返回的 2020 ICC）被我丢弃，
改从 `s.IccProfile` 重算 ⇒ 拿到的是用户 profile（可能是显示器 ICC 或 null），与已搬过的
像素不是一套。改用返回值。
但改完立刻被外部裁判抓到**第二层错误**：无条件嵌入后，sRGB 目标底图多了 2100B ICC，
违背既有"SDR 路 sRGB 不嵌 ICC（cicp 已足够）"契约，也让能力卡文案对不上产物。
终态：`embedIcc = (ColorSpaceTag == "BT2020") ? baseIcc : null` —— 只在广色域嵌，
且嵌的是与像素同源的那份。avifdec 复核：sRGB 产物 `ICC Profile: Absent`、
BT2020 产物 `Present (2100 bytes)`，两条都符合契约。

**③ 管线对「增益图档」的两条形状约束缺失（契约诚实）**
旧写法把硬件→软件的回落藏在 Core 的 `ResolveAvifencCodec` 里：用户亲手选了 NVENC，
拿到的是软件编码，界面与日志零交代（§27 那类"换了参数不说"）。另外 HDR 位深协商仍按
"下限 10"抬，而该档产物恒 8-bit ⇒ 日志说 10、产物是 8。
补管线分支：`GainMap + 非软件编码器 ⇒ 改写 Avifenc 并留痕`（Auto 只记 Info，显式选择才
Toast，沿用 SupportsHdrPath 那条的口径）；`GainMap ⇒ bitDepth = 8` 并记 Info（形状约束，
不是能力降级，故不弹警告）。

**④ 退役后的死代码（清理，无行为影响）**
XMP 双写通路退役后留下：三个私有 helper（`InsertXmpIntoSegment` / `BuildSecondaryXmpPayload`
/ `PrefixXmpNamespace`）只剩不可达分支引用、`GainMapMetadataMode` 枚举全仓零使用、
`WriteJpegGainMapFile` 的 `baseW/baseH/gmW/gmH` 四形参只为已死的 BuildXmpMetadata 调用存在。
全部删除；`app1Total` 改为 `const int = 0` 但**保留 MPF 算式的完整形态**（恢复某条 APP1 段时
不必重推公式，注释写明这个意图）。恒真的 `bool writeIso` 一并删除——留一个恒真开关只会让
下一个人以为它还有另一态。`BuildXmpMetadata` 保留（public，有测试对照）。

### 17.2 判据自身两处缺陷（同样是 bug，且更隐蔽）

**A. `AlternateNclxOf` 按"第二个 nclx"取 → 取到的是增益图自己的 colr。**
ipco 顺序是 底图属性→增益图属性→tmap 追加属性，替代图那张**恒为最后一个**。
首版判据报"实得 1/13/1"（增益图的 nclx），差点被当成实现错误。改为取最后一个。
**B. 用 `"prof"||"iCCP"` 判"嵌了 ICC"** —— 那是 PNG 块名；AVIF 的 ICC 在 colr 盒里，
任何真 profile 必含 `acsp` magic。首版侥幸通过（撞运气）。改用 `acsp`。

**C. 负向 Contains 判据连注释都算**：AppSettingsData / SettingsService 的"已删"注释里
含 `GainMapMetadata` 字样 ⇒ 判据红（§16 已记，本轮再次印证这条规矩有效）。

### 17.3 验证

- 四套件全绿：unit **1908** / color **286** / encoding 23 / usability 225。
- 新判据（BT2020 三条 + sRGB 反向 acsp 缺席 + 管线两条源码形态 + helper 删除 + 无条件 ISO 插入）。
- **变异三轮**：① alternateMatrix 改回 6 ⇒ "替代图 nclx 9/16/9"红并报出实得 9/16/6；
  ② 删除管线 `bitDepth = 8;` ⇒ 该判据红；③ §16 的 writeIso 变异已验。均恢复后复绿。
- **外部裁判**：`avifgainmaputil printmetadata` 独立解析我们的 tmap/ISO 21496-1 载荷，
  逐项吻合写入意图（Alternate headroom 232/100=2.32、三通道 max 一致、offset 1/64、
  gamma 1/1、Use Base Color Space True）。`tonemap` 因本机 avifdec/util 未链 AV1 解码器
  而无法跑像素级（No codec available）——像素级施加增益的验证仍是缺口（见 17.4）。
- 全门禁 exit 0，基线 **2547 → 2555**（+8：unit +4 / color +4）。

### 17.4 审查确认的遗留（不在本轮处置）

1. **像素级增益施加无验证**：本机没有带 AV1 解码器的 avifdec/avifgainmaputil，
   `tonemap` 跑不动。声明层已双裁判覆盖（avifdec + printmetadata），但"叠加后亮度对不对"
   只能靠实机看图。要补需要链了 dav1d/aom 的解码器。
2. `--gainmap-color-product` 仍 ⏭ SKIP（ultrahdr_app 不解 AVIF 增益图），非通过。
3. 增益图档的**无损底图**（Quality=0）未单测；该档 CRF 直通 avifenc，形状与有损同路。
4. AVIF 增益图档不做硬件加速是【当前事实】而非物理必然；若将来 NVENC 支持增益图容器，
   删掉管线那段改写即可，判据会红并点名。

## §18 AVIF 增益图底图的 Rec.2020 矛盾（2026-11-04，用户提问引出）

### 18.1 用户的问题与实测答案

问："AVIF 的 HDR 下 GainMap 的底图是否是正常的 Rec.2020？"

**答：修复前不是——那是一个三处互相矛盾的错件。** 用探针实跑三场景（32×32 scRGB 浮点帧，
`AvifHdrMode.GainMap`），再用 `avifdec --info` + ICC desc 解码：

| 设置 | 底图 nclx | 内嵌 ICC desc | 文件大小 | 判定 |
|---|---|---|---|---|
| 跟随显示器（HDR 屏，**产品默认**） | 9/13/9 | **"sRGB IEC61966-2.1"** | 2871 | ❌ 三处矛盾 |
| 显式 Rec.2020 | 9/13/9 | BT.2020 | 2863 | ✅ 一致 |
| sRGB 目标 | 1/13/1 | 无 ICC | 757 | ✅ 一致 |

矛盾在于：容器声明 Rec.2020 原色、ICC 说是 sRGB、像素实际也是 sRGB/709。

### 18.2 根因

`ColorDecision.Build` 在 HDR 下把目标解析成 BT2020（`target = hdr ? "BT2020" : …`），
但 `ExplicitWideTarget` 只在用户**亲手**选广色域时才为真（跟随显示器不算"用户的手"）。
而 AVIF 增益图分支此前调用 `PrepareGainMapColorSpace(..., requireExplicit: true)`（默认门）
⇒ **像素不搬**，仍留在 sRGB/709；可容器声明却无条件跟 `ColorSpaceTag` 写 9/13/9，
ICC 又是该函数返回的 sRGB profile。三者各自"看起来对"，合起来是错件。

为什么 JPEG 增益图没这问题：JPEG 家族的默认档被 `ColorDecision` 的格式口径钳制收成 sRGB
（`rec709Defaulted`），`wideTarget` 直接为 false，声明跟着 ICC 走，自洽。

### 18.3 修法

AVIF 增益图分支改传 `requireExplicit: false` —— **HDR 输出的 BT.2020 是标准色域，
不是"用户偏好广色域"**，决策层本来就这么解析；增益图档必须与 PQ 档交付同一套原色，
否则"底图是 SDR"会退化成"底图还更窄"。JPEG 主路径仍用默认门（L229 无实参，未动）。

### 18.4 判据（用逐字节相同，比查标签有牙）

`AVIF 增益图档[HDR+跟随显示器]: 与显式 Rec.2020 逐字节相同` —— 两个设置只差一个布尔，
若代码真的走同一条路，产物字节就该相同。任何"声明照写、像素不搬"的退化都会让两边分叉。
- 修复后实测：两者 SHA256 相同（`38bb2213…`，2863B）。
- **变异**：`requireExplicit: false` → 默认 `true` ⇒ 判据红且诊断直接点出
  "底图 nclx=9/13/9，但与显式档不同 ⇒ 像素没搬/Metadata 分叉"；恢复 ⇒ 绿。

### 18.5 读数与遗留

四套件：color **287**（+1）/ encoding 23 / unit **1908** / usability 225，全绿。
**遗留**：① ICC desc 的解码只能在探针里做（测试项目不引 Native 解码器），判据用字节相同间接覆盖；
② JPEG 增益图在 HDR + 跟随显示器时底图仍是 sRGB/709（自洽，但比 PQ 档窄一档）——
这是"JPEG 家族默认 Rec.709"的产品口径，不是缺陷，未改。

## §19 第二轮审查：§18 修复留下的同类洞 + 声明通道口径收口（2026-11-04）

用户要求"跑一轮审查"。这轮审的是 §18 那次修复本身 —— 结果是**它只补了主路径，回退路径仍写着同一个错件**。

### 19.1 审查发现（1 处真错件 + 1 处静默参数）

**① `PrepareGainMapColorSpace` 的回退分支仍会产出"声明 2020、像素 709"。**
该函数在"取不到可嵌 BT.2020 ICC"时会 `CoreLog.Warn` 后**静默退回 sRGB 像素**（这是为 JPEG 设计的：
JPEG 只有 ICC 一条声明通道，退回 sRGB 像素 + sRGB ICC 仍然自洽）。但 AVIF 有**两条**声明通道
（nclx + ICC），而 §18 的修复只把 `requireExplicit` 放开了，声明仍按 `s.ColorSpaceTag` 写 ⇒
一旦 2020 ICC 不可用，产物就是 `nclx 9/13/9` + 内嵌 sRGB ICC + sRGB 像素的三处矛盾错件，
与 §18 修掉的那个是**同一个病、不同的触发条件**。

修法：让函数返回第三元 `WideApplied`（像素**是否真的**搬走了），AVIF 分支据此决定
CICP / ICC / 替代图 primaries 三件声明；JPEG 两个调用点丢弃它（它们只有 ICC 一条通道，本就自洽）。
回退时同时 `CoreLog.Warn` 说明"目标要求 2020 但 ICC 不可用 ⇒ 按 sRGB 交付，宁可窄不错"。

**② 增益图档恒 8-bit，但直调方传 10-bit 时没有任何痕迹。**
管线侧已经有 Info（§17 加的），但探针/判据宿主直接构造 `EncodingSettings` 绕过管线时，
`OutputBitDepth=10` 会被无声忽略。补一条 Core 层 Warn。

### 19.2 顺带确认（不是缺陷，不改）

- **JPEG 增益图无同类问题**：`DefaultsToRec709` 把 JPEG_GAINMAP 纳入，HDR 下决策层已把
  target 收成 sRGB ⇒ `wideTarget=false`，声明跟 ICC 走，自洽。
- **AVIF PQ 档无同类问题**：`HdrToPq16(f, csTag)` 的转换与 `BuildHdrCicp(csTag)` 的声明
  共用同一个 tag，不存在"转换一套、声明另一套"。
- **目标标签取值域**：`CollapseSelectableTag` 只产出 System / sRGB / BT2020 ⇒
  `widePixels` 判断覆盖全，不会漏掉 DisplayP3/AdobeRGB 这类中间态。
- **增益图 item 自身的 nclx 是 1/13/1**（底图 9/13/9 时）：增益图是中性比值数据，
  ISO 21496-1 的 `use_base_cg=true` 表示"按底图色彩空间施加"，libavif 不解它的 colr ⇒ 无影响，不改。

### 19.3 判据与变异

- 新增 3 条源码形态判据（钉 `widePixels` 决定 ICC / CICP / 替代图 primaries 三件），
  外加 1 条定位守卫：unit 1908 → **1912**。
- **变异**：把 `embedIcc = widePixels ? baseIcc : null` 改回按 tag 判 ⇒ 判据红并点名"回退路径会产错件"；
  恢复 ⇒ 绿。
- 探针复跑三场景：跟随显示器/显式 2020 仍逐字节相同（2863B），sRGB 档 1/13/1 无 ICC —— 无回归。
- 四套件：unit 1912 / color 287 / encoding 23 / usability 225，全绿。

## §20 AVIF 增益图改为「10-bit 底图 + 8-bit 增益层」（2026-11-04，用户口径）

### 20.1 用户口径与改动

用户问"为什么 AVIF 增益图恒定 8bit"后拍板：**"AVIF 改为默认 10bit 底图、8bit 增益图，JPEG 不变"**。

- 底图：`Math.Clamp(s.OutputBitDepth, 8, 12)`，走正常 HDR 位深协商（下限 10），经
  **16-bit PNG 中转 + avifenc `--depth N`**，只量化一次；`>8` 时用新增的
  `JpegGainMapEncoder.LinearToBgra16`（大端 BGRA16，与 `ToneMapper.FloatToBgra16Bytes` 同约定）。
- 增益层：**恒 8-bit 不变**（ISO 21496-1 的比值图惯例，增益是平滑乘数）。
- JPEG 侧**一字未动**（JPEG 格式本身没有 10-bit 有损通道，8-bit 是硬约束）。
- 管线：删掉上一轮"增益图档 ⇒ bitDepth = 8"的形状钉死，改为与 PQ 档相同的协商
  （增益层的 8-bit 是 Core 内部常量，管线不再重复表达同一事实）。

### 20.2 两个实测踩出来的坑

**① avifenc 对 16-bit PNG 输入一律按"带 alpha"处理。**
即便 alpha 全为满值，也照样产出 alpha 副 item（`infe=2` + `auxC`）；8-bit RGB 输入则只有
一个 item。增益图容器只搬主图 ⇒ 底图带副 item 就被写出器的"底图含副 item"门拒写
（测试直接表现为该条 Skip，报 `底图含 1 个副 item（alpha）⇒ 拒写`）。
> 期间我先误判为"alpha 值不是满值导致"，把 `a16` 改成 65535（从 `qMax<<qShift`）——
> 实测三个不同 alpha 值（65535 / 65472 / 0）产出**完全相同**（都 infe=2），证明与值无关。
> **是先做三组对照实验才找到真因，不是靠推理猜中的。**

修法：给 `ManagedPngEncoder.Encode16` 加 `omitAlpha` 开关，写 **color type 2（RGB）**
而非 6（RGBA），IHDR/sBIT/扫描线/滤波 bpp 全部随之自适应（bpp 6 而非 8）。
截图恒不透明 ⇒ 零信息损失。

**② 判据"PNG/TIFF 产物站点必须带 effort"把中转站当成了产物。**
它按行匹配 `ManagedPngEncoder.Encode16(` 并排除含 `fastest: true` 的行；我最初把调用拆成
两行，`fastest: true` 落在第二行 ⇒ 被判为"产物站点没带档位"而红。改成单行调用后绿。
**判据又一次走在实现前面**（与 §15.5 的 EncodeFast 同一条纪律）。

### 20.3 判据与验证

- color 287 → **290**（+3）：`PixiChannelDepths` 读 pixi 盒的 bits_per_channel，
  钉"底图 10 / 增益层 8"（ipco 顺序：底图属性在前、增益层在后）。
- unit 1912（+0，但把上一轮"钉 8"的判据**反转为**"不许再钉 8"——否则 10-bit 会被静默压回）。
- **变异**：`baseDepth = 8` ⇒ 判据红并报 `实得 [8,8,8]`；恢复 ⇒ 绿。
- **外部裁判**：`avifdec --info` 读出 `Bit Depth: 10`（底图/替代图）+ `Gain map: … 8 bit`，
  `avifgainmaputil printmetadata` 元数据完好（Alternate headroom 232/100）。
- 四套件全绿：unit 1912 / color 290 / encoding 23 / usability 225。

## §21 交付事故：演示污染了出包设置文件（2026-11-04）

### 21.1 用户报的现象

"AVIF 的默认方式是传统方式，处理一下"——用户打开包后看到 AVIF 「HDR 承载方式 = 增益图」，
而产品默认应该是「传统 HDR（底图写 PQ）」。

### 21.2 根因：不是代码，是我污染了出包设置

代码三处默认值都正确（`EncodingSettings.AvifHdrMode = Pq`、`AppSettingsData.AvifHdrMode = "Pq"`、
XAML `Tag="Pq" IsSelected="True"`）。真因是：**我为了截图演示新开关，直接改了
`publish\TrueToneCap-v0.3.11-beta\TrueToneCap.settings.json`，把 `AvifHdrMode` 写成 `"GainMap"`，
之后没有恢复就重发了包。**

未打包形态的设置文件就落在 `AppContext.BaseDirectory`（exe 同目录）——见
`SettingsService.SettingsPath`——所以用户装上这个包，第一次打开看到的不是产品默认，
而是**出包者调试时的界面状态**。

实测确认：干净 settings 下 AVIF 默认就是「传统 HDR（底图写 PQ）」，代码无需改动。

### 21.3 处置

1. **清理全部污染**：`publish\*` 与 `src\...\bin\*` 下 6 份被污染的 settings 全部重置为
   首启模板 `{"SettingsRevision":2,"GainMapMerged":true,"FreeEndpointsMigrated":true,"FirstRun":false}`。
2. **加门禁**（`scripts\verify-package.ps1` 新增 [5.7/6]）：包内 settings 若存在，其键必须
   全部属于 `{SettingsRevision, GainMapMerged, FreeEndpointsMigrated, FirstRun}` 这一组
   "迁移/首启标记"；出现任何**用户偏好键**（FormatIndex / AvifHdrMode / Quality / OutputPath …）
   即判失败并点名，因为那意味着"装包用户会继承出包者的界面状态"。
3. **变异验证**：故意写入 `FormatIndex=3, AvifHdrMode=GainMap` ⇒ 门禁失败并点名这两项；
   恢复 ⇒ 通过。

### 21.4 教训（写进纪律）

**演示/截图不得改动产品目录里的持久化文件。** 需要展示某个非默认界面状态时，正确做法是
临时复制一份 exe 到独立目录去改那份设置，或直接在界面上点（本轮的截图本来也是这么做的）——
一旦写进 `publish\` 里的 settings，它就会随包发给用户，而**既有 6 步冒烟一条都不会红**
（exe 能跑、版本对、许可齐）。这条门禁就是为这个盲区加的。

## §22 AVIF 色度默认 420 → 444，编码器默认 Auto → LibAom（2026-11-04，用户口径）

### 22.1 用户报告的问题

"目前 420 的截图文字边界有了很明显的劣化，思考一下原因。"

### 22.2 实测定位（不是推测）

用 avifenc 编码含文字/UI 的合成画面，ffmpeg 解码回 rgb24，逐像素与原始 RGBA 比对：

| 内容类型 | 444 边缘误差 | 420 边缘误差 | 判定 |
|---|---|---|---|
| 中性黑白文字 | 0.00 | **0.00** | 完全不受影响（信息全在亮度 Y 平面） |
| **彩色 UI 文字** | 0.14 | **7.82** | **劣化 56 倍 —— 真正的病灶** |
| 自然照片平滑渐变 | 0.53 | 0.79 | 温和 |

**结论：4:2:0 只砍色度分辨率，所以纯黑白文字毫发无损；被破坏的是彩色文字、图标描边、
ClearType 彩边。** 提高质量救不回来（q68→q90 只从 13.05→12.82），因为这是采样阶段的结构性损失。

### 22.3 为什么不是 422（专门测过）

用户要求"测试一下 422 的表现"，实测三档（640×240 彩色文字，CRF 20）：

| 色度 | 体积 | 平均\|Δ\| | 边缘\|Δ\| | C 边缘误差 |
|---|---|---|---|---|
| 420 | 28186B | 4.60 | 5.75 | 4.15 |
| **422** | **32142B** | 4.00 | **5.46**（仅改善 5%） | 3.90 |
| **444** | 37734B | **2.54** | **3.22**（改善 44%） | **2.18** |

**422 是"两头不靠"**：只改善 5% 却比 420 大 14%。原因：422 只补了水平方向的色度分辨率，
垂直仍减半，而文字/UI 在两个方向都有锐利边缘。Y 边缘误差 420/422 几乎相同（1.95/1.96），
证明 422 改善的纯粹是色度那一半。
另：中性文档上 422 甚至比 444 还大 6%（1499 vs 1410B）—— 每种内容上都最差或并列最差。

### 22.4 体积代价要分开看

上面 444 的 +34% 是"大面积彩色细节"的最坏情形。真实场景：
- 桌面截图混合：420 2110B → 444 2124B（**+0.7%**）
- 中性文档：420 1412B → 444 1410B（**−0.1%**）

那类内容色度本来就平滑，4:2:0 省不下空间 ⇒ 默认 444 的实际代价接近零。

### 22.5 为什么编码器默认也必须改

`AvifEncoderCapabilities.PickAuto` 只在 **420** 才选硬件（NVENC/QSV 只支持 4:2:0、≤10-bit）。
保持 `Auto` 为默认时，"默认 444"会在硬件机上被能力协商**静默降回 420** —— 用户看到 444、
产物却是 420。所以两个默认值必须成对改：色度 `444` + 编码器 `LibAom`（软件 avifenc 是唯一
支持全色度 + ≤12-bit 的路径）。想用硬件加速仍可显式选，届时色度按其能力协商并给出提示。

### 22.6 判据与变异

- `Test_AvifChroma_DefaultIs420` → 重写为 `Test_AvifChroma_DefaultIs444`（含方法名、
  三处事实源：Core 类型默认 / App 设置项 / XAML IsSelected）。
- 新增 `Test_AvifEncoder_DefaultIsLibAom`：Core 行为判据（真跑）+ App 设置项 + XAML IsSelected
  + "不许有两个 IsSelected"。
- `Test_EncodingSettings_Defaults` 同步（原钉 `Auto`），失败消息改成打印实得全字段。
- **变异两轮**：ChromaAvif 改回 420 ⇒ 红并点名；AvifEncoder 改回 Auto ⇒ 红并点名
  "默认色度 444 会在硬件机上被静默降回 420"；恢复 ⇒ 全绿。
- 四套件：unit **1919**（+7）/ color 290 / encoding 23 / usability 225。

## §23 试过 sharpyuv，实测否决（2026-11-04）

### 23.1 动机与构建

`--sharpyuv` 是 libavif 里唯一专门缓解 4:2:0 色度下采样损伤的开关，看起来正对 §22 的病。
按用户要求"在 avifenc 侧重构建"执行，结果：**构建成功，但实测是负收益，已回退。**

### 23.2 关键认知错误（值得记住）

**`--sharpyuv` 的编译开关是 `AVIF_LIBSHARPYUV`（libwebp 的 libsharpyuv），
不是 `AVIF_LIBYUV`（Google libyuv 的缩放/快速路径）—— 两个独立选项。**

我一开始只改 `AVIF_LIBYUV=LOCAL`，`avifenc --version` 于是印出 `libyuv : available`，
看起来成功了；但 `--sharpyuv` 一用就报 `Conversion to YUV failed`、零产出。
隔壁 ffmpegPictureUI 自建的那枚二进制正是这个状态（version 报 available、一用就失败）。
**判断 sharpyuv 能不能用，只能靠真实编码对比，不能看 version 串。**

### 23.3 实测数据（彩色文字/UI，CRF 20）

| 配置 | 体积 | 平均\|Δ\| | 边缘\|Δ\| | Y误差 | Cb误差 | Cr误差 |
|---|---|---|---|---|---|---|
| 420 普通 | 28186B | 4.60 | 5.75 | 1.56 | 3.46 | 3.20 |
| **420 +sharpyuv** | **47823B** | **7.33** | **9.25** | 2.02 | 5.23 | 5.58 |
| 444 | 37734B | 2.54 | 3.22 | 1.16 | 1.81 | 1.56 |

**sharpyuv 让亮度和色度误差同时变大（边缘劣化 61%），体积还涨 70%，且比 444 更差又更大。**
它优化的是"主观边缘锐度"，代价是色度精度；在本仓的逐像素度量下是净损失。

### 23.4 回退与保留

配方 `scripts/build-avifenc.py` 已回退：`AVIF_LIBSHARPYUV=OFF` + `AVIF_LIBYUV=OFF`，
并把上述数据与"两个开关不是一回事"的坑写进注释（防止下次再走一遍）。

**保留的真实修复（与 sharpyuv 无关，是配方原有 bug）：**
1. `SRC = args[i+1]` 的变量遮蔽：让它赋值后 `SRC` 成为整个 `main()` 的局部变量 ⇒
   不传 `--workdir` 时抛 `UnboundLocalError`。改为局部别名 `src`。
2. 工具链探测：ninja（VS 自带，不在 PATH）、perl（隔壁 exiftool 带的是**精简版，
   缺 Getopt::Long**，会让 aom 的 rtcd.pl 在编译中途炸；必须用 Strawberry Perl）、
   nasm（系统已装但不在 PATH）。现在脚本自己找，找不到就在 configure 之前明确报错。

## §24 全格式色度体检：增益图底图与 JPEG LI 分家（2026-11-04，用户口径）

### 24.1 用户报的问题与本轮范围

"分析一下其他格式是否也有类似的问题"（§22 修完 AVIF 之后）。本轮把**所有带色度概念的格式**
都做了实测体检，方法同 §22：项目自己的编码器 API 出图 → ffmpeg 解回 rgb24 → 逐像素比对。

**先做了测量有效性诊断**（避免拿有偏的尺子下结论）：纯色块近无损往返，白/中灰/黑 Δ 全 0，
证明 ffmpeg 的 JPEG 解码没有 limited-range 偏移 ⇒ 下面的数字可信。

### 24.2 体检结果

| 格式 | 默认色度 | 420→444 边缘误差 | 结论 |
|---|---|---|---|
| PNG / TIFF | 管线硬编码 444 | — | ✅ 无损 RGB，无色度概念 |
| WebP | 硬编码 420 | **无法改** | ⚠️ VP8 有损格式定义只有 4:2:0 |
| JPEG XL | 444 | 30.67 → 5.48（**420 劣化 5.6 倍**） | ✅ 默认本来就对 |
| AVIF | 444（§22 已修） | 5.75 → 3.22 | ✅ |
| **JPEG LI** | 420 | 7.42 → 6.71（仅 −10%） | ⚠️ 同类问题，幅度小 |
| **增益图底图** | 共用 ChromaJpegLi=420 | 同上 | ⚠️ **本轮处理对象** |

（绝对值不能跨格式比，只能看同格式内的相对差。）

### 24.3 用户拍板与分家理由

用户口径：**"JPEG LI 还是默认 420，其他默认 444"**。

分家的技术理由：增益图的底图会被查看器**乘增益放大还原**，底图的色度误差会一并放大 ⇒
这一档色度精度优先于体积；普通 JPEG LI 照片档按体积优先。

### 24.4 实现（三处，都不在 Core）

1. **管线 switch**：`JPEG_GAINMAP => (s.BitDepthJpegLi, "444")` —— 与 PNG/TIFF 同一手法
   （硬编码在调用方，Core 不感知），位深仍同源。
2. **UI 联动**：`gainMapOn` 时 `ChromaJpegLiCbo.IsEnabled = false` + 说明文字；
   else 分支成对还原（与 HDR 锁定同一手法，不写 _settings，避免状态粘住）。
3. **ZhToEn 新键**：禁用时的说明文案进表。

### 24.5 门禁当场抓到的一次自我纠错（记进纪律）

新文案我最初写成两段拼接：

```csharp
LocaleManager.Tr("增益图态底图恒为 4:4:4（…）；"
    + "关闭「附带增益图」后可再选。")
```

死键判据立刻报红（当前树死键 0 → 1）：**表里的完整键在源码中不连续出现，就等于没人用**。
改成单一字面量后绿。**教训：给 `LocaleManager.Tr` 传中文串时必须写成一整条字面量，
不许 `+` 拼接**，否则要么判据红，要么真的漏译。

### 24.6 判据与变异

- 原"同源"判据（`JPEG_GAINMAP => (…, s.ChromaJpegLi)`）改写为钉"分家的形状"，
  并新增反向判据：JPEG LI 普通档**仍必须**读字段（不许把 420 也硬编码掉，那是用户选择权）。
- 新增两条假配置锁：开启时禁用 / 关闭时还原。
- **变异两轮**：① 增益图色度改回 `s.ChromaJpegLi` ⇒ 红并点名"会让它跟着默认掉到 420"；
  ② 删掉 UI 禁用行 ⇒ 红并点名"留着就是假配置"；恢复 ⇒ 全绿。
- 本轮没动 Core ⇒ 只跑 unit **1923**（+7）/ color 290 / usability 225，encoding 未跑
  （按用户偏好：非出包不跑全量）。

### 24.7 补：色度落点断言该放哪 —— 撞出一条既有契约（2026-11-04）

给"分家"加活体判据（真调 `BuildEncodingSettings`，比源码串强）时，我最初把 JPEG LI 探针
加进了【逐屏循环】的 `probes`，`--display-audit` 当场报红：

```
✗ DISPLAY5·JPEG LI 色度跟随设置: 交付形态说 BlockedByFormat 而落点 HdrOutput=True，期望 False
```

**这不是产品缺陷，是我这条新探针的期望设错了。** 查编码器入口注释确认：JPEG LI 的
`EncodeAsync` 开头就写着"不支持 HDR，始终降级到 SDR"，`HdrOutput=True` 是既有契约
（交付形态 `BlockedByFormat` 说的正是这件事）。原四条探针没有 JPEG LI，正是因为
它没有"HDR 该不该开"的可钉期望。

两个更正：
1. **撤掉逐屏的 JPEG LI 探针**，色度断言挪到【全局落点段】—— 实测色度落点与屏无关
   （窄色域屏与 HDR 屏同为 420/444），逐屏跑只是把同一句印四遍。
2. 断言写成【成对的两条】：增益图必须恒 444、JPEG LI 必须跟随字段。只留一条的话，
   "两边都硬编码同一个值"也能过 —— 两条同时判才是"分家"的证据。

**行为级变异两轮**（`--display-audit` 退出码为判据，非源码串）：
- 增益图色度改回 `s.ChromaJpegLi` ⇒ `✗ 全局·增益图底图色度: …实得 420`，退出码 1
- JPEG LI 改回硬编码 `"444"` ⇒ `✗ 全局·JPEG LI 色度: …实得 444（用户的选择权没了）`，退出码 1
- 恢复 ⇒ `色度落点: JPEG LI=420 增益图底图=444 ⇒ 分家成立 ✓`，退出码 0

教训一句话：**新加探针时，先确认这条格式在该轴上"有没有可钉的期望"** —— JPEG LI 的
HDR 落点是"恒开、由编码器降级"，拿别的格式的 WantHdr 模板套它就会自己造出一条假分叉。

### 24.8 补：接线审查第二轮 —— 子 agent 交回的 7 项发现与处置（2026-11-04）

静态审查（只读子 agent）+ 我独立复核，处置如下：

**已修（4 项，均带判据或变异验证）：**
- **E-1（高危）**：`AvifHdrModeRow` 可见性在 `UpdateQualityPanelCore` 前半段读 `HdrSwitch.IsOn`，
  早于同方法 else 分支的开关回填 ⇒ 从"JPEG LI+增益图（锁开）"切到 AVIF 会点亮一格
  "动了没反应"的假配置控件。修法：判断下移到两个分支定稿之后 + 口径统一为 `IsOn && IsEnabled`
  （SDR-only 屏上只看 IsOn 会点亮被管线夹掉 HDR 的无效格子）。新判据
  `Test_AvifHdrModeRow_VisibilityReadsFinalHdrState` 按出现次序断言；变异（谓词丢 IsEnabled）三断言齐红。
- **C-1（高危）**：`FreeEndpointsChk` 零事件、`FfmpegPathTxt`（11-03 新增）无 LostFocus，
  而"关窗口 = 最小化到托盘"根本不跑全量 SaveSettings ⇒ 改动静默丢失。修法照 `ArchiveChk`/
  `OnOcrLangChanged` 先例补实时落盘；FfmpegPath 复用 `_ffmpegPathEchoed` 闩（空串是合法值），
  规整链抽成 `NormalizeFfmpegPathInput` 两个写点共用。新判据
  `Test_SettingsControls_RealtimeWriteback`（点名层 + 遍历层）；变异（删 XAML 事件）红。
- **A-1（低）**：UI 内四处 `?? "Auto"` 与写侧 LibAom 不同源（同族 bug 只修了一半）。已改 LibAom；
  L990 保留 —— 它是遍历 item.Tag 的缺失兜底，走 `Parse` 既有空值契约（有判据钉住回落 Auto）。
- **G-1**：四处过期注释（ChromaGainMap"唯一事实源"口径、`Test_AvifChroma_DefaultIs420` 旧名、
  `RefreshFormatPanel` 幽灵方法名、XAML/回显区"管线对 JPEG_GAINMAP 也读 ChromaJpegLi"）全部修正。

**复核后否决（1 项，教训比修复值钱）：**
- **B-1**：子 agent 声称"改默认必须 +1 SettingsRevision 并点名，否则老文件用户永远停在 420/Auto"。
  **推演否决**：2 号盖章发生在 2026-09-25，当时默认就是 420/Auto ⇒ 盖章后第一次 Save 的
  差集剪枝就把这两个"等于当时默认"的老键剪掉了，此后缺键回落**当前**默认 ⇒ 老用户自动拿到
  444/LibAom。反而：①+1 会让所有 revision=2 文件重跑 2 号迁移（质量档/色域无差别重置）；
  ②任何"见 420 就改 444"的迁移都会覆盖**显式选过 420** 的用户 —— 盘上 420 无法区分
  "整份写回替用户记的"与"用户真选的"。整条链由
  `Test_AvifDefaults_RecastIsHandledByDifferentialSave` 真跑 SettingsDiffPruner 钉住（含反面）。

**记档待办（3 条判据盲区，本轮不动手）：**
- **C-2/C-3**：SaveSettings 不写 6 个 Effort*（全靠实时处理器，"撤销/重置"类功能会漏）；
  遍历判据的单行正则抓不到 `if (…) _settings.X = … ?? "Y";` 跨行形态，且
  `defaults.TryGetValue` 交集比对对"兜底字段名拼错"静默放行。
- **F-2**：代码里拼接的动态中文（`$"…"`/`+`，如 :785 增益图锁开提示、fmtHdrWarn 两条）
  全仓 0 条英译对照，XAML+Tr 双锁结构上看不见；本轮的禁用说明做对了，紧邻的锁开提示没做。
- **F-3**：AVIF HDR 承载文案"XAML 表 + LocaleManager 运行时属性"两份真相，改一侧即分叉。

unit 1925 → 1949（+24，其中本轮新增 5 条判据、多条带变异）。

### §25 补：抓屏覆盖层"拖拽不顺手"重修（2026-11-05，提交 9539e37）

**用户原话**："截图后的界面目前拖拽不是很顺手"。根因两个，都在渲染循环（HdrCaptureWindow.RenderLoop）:

1. **Present(0) + Thread.Sleep(16-elapsed) 定步**。两层伤害：
   - Sleep 在无 timeBeginPeriod 的机器上受 15.6ms 计时器粒度影响，`Sleep(14)` 实际睡满下一拍
     ⇒ 拖拽有效帧率 30-45fps 且逐帧抖动；
   - 无 vsync ⇒ 呈现相位不受控，选区边缘追赶光标、肉眼可见的撕裂/掉帧。
   **历史包袱**：2026-08-26 曾用 Present(1, AllowTearing) 在无 ALLOW_TEARING 标志的交换链上
   静默失败（该组合本身非法——AllowTearing 必须配 SyncInterval=0），当时的修法把 vsync
   整个扔了。Present(1, None) 才是标准路径，本次恢复；注释里已把这段误诊写清。
2. **渲染线程 Priority.BelowNormal**：拖拽中鼠标消息洪峰 + 其他进程 Normal 线程会把它饿出
   几十毫秒的调度延迟。现交互期（_down/_annoDown/_sliderDrag）提到 Normal，结束还回。

附带修复：**悬停重绘去重**。旧 Move 里 `else if (!_down && _selComplete) RequestRedraw();`
让选完框后在工具栏上滑动时每条 WM_MOUSEMOVE 都全量合成一帧。现在 ToolbarHoverIndex
（与 OverlayComposite.hlsl §4 同一套命中算式）现算悬停索引，变了才画；窗口尺寸变化用
-2 哨兵强制重绘一帧。

**验证**：Test_CaptureOverlay_RenderPacing 四条结构契约（RenderLoop 无 Sleep 定步〔唯一白名单
是 Sleep(4) 防 hot-spin 护栏〕/ PresentFrame 恒 Present(1, None) 且全文件无 Present(0) /
交互优先级提升 / 悬停索引去重）。变异验证：Present(0) 红、Sleep 定步红、恢复绿。
unit 1949 → 1959。

**⚠ 流程事故自查（写在这里防止再犯）**：变异验证恢复时我顺手敲了 `git checkout -- <file>`，
把**未提交的全部修复**回滚掉了（diff 清零才发现）。重做时逐条补齐后立即提交。教训：
**验证用的文件改动恢复必须走备份文件（.bak copy），永远不许用 git checkout 恢复未提交工作**；
产品代码改完第一件事是提交，不让带牙的判据守着一份随时会被自己回滚的工作区。

**F4 仪表语义变化**：frameUs 从"纯渲染耗时"变为"帧周期"（含 Present(1) 的 vblank 等待），
峰值≈刷新周期。判读历史对比数据时注意口径切换点 = 本提交。

### §26 补：WGC 启动延迟优化 —— 用户反馈"WGC 捕获开启时有一点卡顿"（2026-11-05）

**定位**（publish 0d9d127 真机日志，双屏 3840×2160 HDR@120nits + 2560×1600 SDR@80nits）：
热键→覆盖层首帧 727ms。分解：WGC 冷启动会话 265ms（15s 闲置回收 ⇒ 典型节奏每截必冷）+
**白点查询 ~150ms**（每次截图 3 次完整 QueryDisplayConfig，无缓存）+ **拼接 ~236ms**
（两块画布 55MB+221MB 零初始化 + 每块 SDR 屏 132MB 临时 float 数组 + 整块拷贝）+ 覆盖层 94ms。

**三项修复**（提交本条）：
- **A 白点缓存**：GetSdrWhiteLevel/ForMonitor 加 120s TTL 缓存（只缓存成功值>0），
  挂进 DisplayEnumerator.InvalidateCache（显示器变更信号已覆盖主要失效场景；
  系统 HDR 亮度滑块无通知 ⇒ TTL 兜底）。拼接侧懒查询：无 HDR 源时这两个量没有消费者，纯 SDR 截图跳过全部 3 次查询。
- **B 画布免零分配**：fullPixels/fullHdr 改 GC.AllocateUninitializedArray；免零后"未填到=黑"
  的旧语义由 failedRects（三条失败路径登记：首帧超时/取帧失败/copyW 非法）在循环后 Array.Clear 显式兜底。
- **C SDR→scRGB 直写**：PixelOps.BgraToScrgbLinearInto（逐行转换直写目标区域，scale 只乘 RGB、
  alpha 不缩放，越界抛 ArgumentException）替换"132MB 临时数组 + scale 循环 + CopyFloatRegion"三段串联。

**不做的**：池常驻（15s→更长）。10-30 探针实测常驻帧在画面移动时 p50 已落后 250ms，
而截图语义是"拍到按下那一刻"——拿 250ms 陈旧帧换 127ms 冷启动，语义上不成立。

**实机验证**（新构建 Debug，同一台双屏机）：666ms（-61ms），白点段并入并行等首帧（缓存命中），
拼接段 ~120ms。预期典型节奏（冷启动）总收益 ~60-100ms；高频连拍（120s 内）因白点缓存全额生效更多。
**探针验证**：--capture-freshness-probe 被单实例机制挡住（用户实例占位时激活已有窗口），
跑探针前必须关掉常驻实例 —— 写在这里防止下次误判"探针坏了"。

**判据**（unit 1959→1974，+15；基线 2600→2625）：
- Test_PixelOps_BgraToScrgbLinearInto：全画布逐位等价 Fast / 区域写+scale 只乘 RGB / 区域外不动 / 越界抛
- Test_DisplayEnumerator_WhitePointCache：命中不增长真实查询计数 / 失效后重查且值不变 / 逐屏同款
- Test_WgcStitch_CanvasAllocation：形态锁（免零分配/failedRects 三路径/显式清黑/直写/白点懒查询）
- 变异验证：R/B 互换 → 等价性 2 条红；画布退回零初始化 → 形态锁 2 条红（恢复均绿）。
  ⚠ 教训：改 Core 后 Test 项目不 --no-incremental 会吃到旧 DLL 假红 —— 恢复后必须强制重建再判。
