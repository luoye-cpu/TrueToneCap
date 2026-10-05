# TrueToneCap 管线完整审查 · 执行计划（round68，2026-10-04）

- **基线坐标**：HEAD `ca7ed99`（tag `v0.3.6-beta` 之后只差文档）。工作树 **77 项未提交改动**（`M` 62 / `D` 4 / `??` 11），含 A/B 档已落地的删除（`GpuToneMapper.cs`、`ShaderLoader.cs`、`ToneMapping.hlsl`、`HDRPreviewWindow.cs`）。
- **上一轮门禁**：`tests/output/w9/gate-final2.log` ⇒ `GATE_RC=0` / 7 套件全通过 / **1738 断言** / 无未判格子；地板 `scripts/gate-baseline.json` = 1738。**本轮 W0 当场重取，不拿它当预期值。**
- **本文性质**：计划。批次 W0 起才动手；**不 commit / 不 push / 不打 tag**。
- **本轮相对 10-01/10-02 两轮的骨架变化**：那两轮的骨架是"用户动作 → 外部消费者拿到的东西"，本轮不再重跑那张 24 格矩阵（已交付），改为**补它没有覆盖的判据面**——见 §2。

---

## 0. 一条执行前提：shell 工具坏了，绕行方式已找到

`pwsh` 工具在本会话**全线失效**：连 `echo hello` / `exit 0` 都返回空输出 + exit `3221225794`（`0xC0000142` = `STATUS_DLL_INIT_FAILED`），子代理独立复现同一结果。

**可用绕行**：工具默认二进制的加载环境坏了，但本机真 `pwsh.exe` 本身是好的——

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -Command "<命令>"
```

实测返回正常（`pwsh7-ok`、`dotnet --version` = `11.0.100-rc.1.26425.128`、`git status --porcelain` 正常输出）。**本轮所有命令一律走这条路径**，并在每条读数旁注明它是这条路径取的。这一点写进计划首页，是因为它决定了"这轮能不能取到 T1/T3 读数"——能，但每条命令都要多套一层。

其余工具（read / grep / glob / edit / write）正常。

### 0.2 第二条环境障碍：DSH 沙箱掐掉了子进程的 `%TEMP%` 写入（已找到绕行）

**现象**：W0 第一次跑全量门禁 ⇒ **`GATE_EXIT=1`**，倒在 `[1.7/2] 外部判据`：

```
❌ --wg-samples 生成样张失败 (退出码 1)
❌ BT2020_jpeg_li.jpg   UnauthorizedAccessException: Access to the path
   'C:\Users\20210\AppData\Local\Temp\ttc_jpegli_<guid>.ppm' is denied.
❌ 有 21 个格子没产出样张 ⇒ 跨容器像素层的覆盖不完整，本轮不算判过
```

**根因不是产品缺陷**，是沙箱按进程收口：

| 谁写 `%TEMP%` | 结果 |
|---|---|
| 我的 pwsh 外壳 | ✅ 成功 |
| 从外壳起的 **Python** 子进程 | ✅ 成功 |
| 从外壳起的 **TrueToneCap.Tools.exe**（.NET） | ❌ **全是 `Access to the path … is denied`** |

`%TEMP%` 的 ACL 是全权（`BUILTIN\Administrators` + 本用户 FullControl），目录也确实可写——所以这**不是权限配置问题，是 DSH 文件沙箱对那个 .NET 子进程的收口**。旁证：该宿主跑 `--bench <目录>` 时**仍然**去写 `%TEMP%`（它按 `Path.GetTempPath()` 走），而 `Path.GetTempPath()` 读 `TMP`/`TEMP`。

**绕行（实测有效，改动为零）**：把 `TMP`/`TEMP` 指到**工作区内**再起进程。同一台宿主、同一条命令：

```
重定向前：PNG ❌ / JPEG LI ❌ / WebP ❌ / AVIF ❌ / JPEG XL ❌ / GainMap ❌   （全部 Access denied）
重定向后：PNG 91.4 KB ✅(444ms) / JPEG LI 265.9 KB ✅ / WebP 170.7 KB ✅ /
          AVIF 55.7 KB ✅ / JPEG XL 97.4 KB ✅(21.3s) / JPEG GainMap 376.3 KB ✅
```

⇒ **本轮全部门禁与测试一律带 `TMP`/`TEMP` 重定向跑**，命令写成：

```powershell
$ws='C:\PLAN\TrueToneCap\tests\output\round68-2026-10-04\scratch\tmp'
New-Item -ItemType Directory -Force -Path $ws | Out-Null
$env:TMP=$ws; $env:TEMP=$ws
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
```

**这条要记进报告首页**，因为它同时是一条**方法学发现**：在带文件沙箱的环境里，"门禁红"可能是**量具被环境掐住**，不是产品坏了。判据是那把三行对照表（外壳能写 / Python 能写 / .NET 宿主不能写）——**不先量这一条就会把环境问题写成产品 P0**。

### 0.3 沙箱收口的精确边界与剩余不可绕行项（已测）

**边界**：收口**不是按路径 ACL，而是按可执行文件**——同一个 .NET 运行时、同一个用户、同一条路径，只有 exe 不同：

| 写入者 | 目标 | 结果 |
|---|---|---|
| `pwsh`（.NET） | `%LOCALAPPDATA%\TrueToneCap` / `%TEMP%` / `C:\PLAN` | ✅ 全通过 |
| **`TrueToneCap.Tools.exe`** | `%TEMP%\…` / `C:\PLAN\probe_out\…` | ❌ 拒绝 |
| **`TrueToneCap.Test.exe`** | `%LOCALAPPDATA%\TrueToneCap\…sentinel` | ❌ 拒绝 |

允许面 = **工作区 `C:\PLAN\TrueToneCap` 之内**（`tests\`、`artifacts\`、`src\…\bin\Release` 全可写），工作区之外一律拒绝 ⇒ 与 DSH 工作区沙箱的足迹逐字吻合。`%TEMP%` 的 ACL 本就是本用户 FullControl ⇒ **不是权限配置问题**。

**受影响的套件（实测）**：`--unit-tests`（fatal）、`--color-tests`（fatal）、`--encoding-tests`、`--format-tests`、`--usability-tests`、`[1.7/2]` 外部判据（21 个空格子）、`[1.8/2]`、`[1.97/2]`；**不受影响**：`--service-tests`、`--ocr-tests`（不写 temp）。

**`TMP`/`TEMP` 重定向修好 6/7 套件**（实测读数，全部与 `scripts/gate-baseline.json` 逐字一致）：

```
--color-tests      267 通过 / 0 失败      （基线 267 ✓）
--encoding-tests    14 通过 / 0 失败      （基线  14 ✓）
--format-tests      43 通过 / 0 失败      （基线  43 ✓）
--usability-tests  225 通过 / 0 失败      （基线 225 ✓）
--ocr-tests         12 通过 / 0 失败      （基线  12 ✓）
--service-tests      2 通过 / 0 失败 / 1 跳过（基线 2/1 ✓）
```

⇒ 六套件**逐套件与地板逐字对上**。这既证明产品侧没问题，也**独立验证了 `gate-baseline.json` 这份地板是真的**。

**剩余不可绕行项：`--unit-tests`。** 它死在 `CorePipelineTests.cs:282` → `:284 Directory.CreateDirectory(dir)`（`WriteForeignSentinel` 伪造"别的进程留下的哨兵"），而 `EncoderCrashSentinel.SentinelDir`（`EncoderCrashSentinel.cs:55-57`）走的是 `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`——**壳 API，不吃 `LOCALAPPDATA`/`USERPROFILE` 环境变量**（两条都试过，仍拒）。另：把 `LOCALAPPDATA` 重定向过去反而**弄坏了一条本来通过的断言** ⇒ 此路不通。

实测崩溃点与所得读数：

```
passed_before_crash=183   failed_before_crash=1
❌ EncoderCrashSentinel: 在途期间文件仍在 ⇒ 真崩了仍有据可读   ← 唯一失败（哨兵文件建不出来）
Unhandled exception ... Access to the path
  '...\TrueToneCap\encoder-crash.NVENC.sentinel' is denied.
  at CorePipelineTests.WriteForeignSentinel(...) line 282
  at CorePipelineTests.Test_EncoderCrashSentinel_ArmDisarm() line 269
  at CorePipelineTests.RunAll() line 91
```

⇒ `RunAll()` 在**第 91 个测试**处中断，**后面约 992 条 unit 断言本轮根本没跑到**。`--unit-tests` 的 1175 条地板由此**本轮无法复核**。

**我没有绕过它**，理由写清：唯一的两条路分别是（a）把 `%LOCALAPPDATA%\TrueToneCap` 加进沙箱允许面（环境改动，我在会话内改不了），或（b）改产品源码 `EncoderCrashSentinel.SentinelDir`（本轮是审查轮，且那个目录里放着**用户的真实数据**——`settings.json`、`onnx_models`、`crash.log`，把它换成 junction 会有数据风险，我不做）。

⇒ **本轮的门禁读数按这个口径记账**：`6/7 套件逐套件与地板逐字一致（563 断言）+ [1.6]–[1.9] 全通过`，而 `--unit-tests` 记**"环境不够，未复核"**——不是"通过"，也不是"产品坏了"。这条按项目规矩明写在报告首页。

---

## 1. 目标（每条给合格线）

| # | 目标 | 什么算答完 |
|---|---|---|
| **G1** | 主链路（捕获→色彩→编码→落盘/剪贴板）**维持** 10-01 轮已证的可用性，且本轮的树仍然过门禁 | W0 当场门禁 `GATE_RC=0`，断言数与地板逐字一致，套件求和自检通过 |
| **G2** | **旧账逐条重判**：交接 §64.22/§65/§66/§67 记的"未闭环"逐条给「已修 / 仍开 / 作废 / 本机不可验」 | 一份重判表，每条带当前 `file:line` 与 T 级；**不许把旧账当新缺陷重报**（10-02 §0.5 已付过这次学费） |
| **G3** | **判据体检**：1738 条里有多少"没在判" | T2 冒充 T1 的完整名录 + 恒跳过 / 恒真 / 无牙三类点名；已知结构事实（Test 工程只引 Core）仍在，需量化 App 侧执行覆盖率 |
| **G4** | **六条 lane 静态深读**：找形状（静默宽松默认值 / 双实现漂移 / 声明与像素不同向 / 收起卡缺点亮行 / T2 冒充 T1） | 每 lane 一份候选缺陷表，每条 = `文件:行` + 触发构造 + 用户可见性 + P 级 + T 级。**没有行号的推测不收** |
| **G5** | 候选经**回源复核**才升级为结论 | 主循环逐条回读最重的若干条；负断言用第二条独立命令复跑；报告里只到 lane 的行带【lane】标记 |

不回答：性能优化、新功能设计、构建/依赖/CI 治理（`docs/pipeline-review-2026-09-15.md` 的地盘）。

---

## 2. 本轮**不重做**的部分（明确划出，避免重复劳动）

10-01 轮已交付、本轮只引用不重跑：

- **24 格实机矩阵**（保存 × 复制 × 6 格式 × HDR 档）⇒ `tests/output/realmachine-2026-10-01/w1a-matrix-hdr.log`，6/6 格式正确、外部裁判可解、内容非平白。**唯一用户级缺口**：JPEG XL 剪贴板无位图通道（§1 已记）。
- **选区两条腿**（Enter 保存 / Ctrl+C 复制）⇒ 已判成。
- **降级可见性**部分行：`OutputPath=D:\TTC` 不可达 ⇒ 失败但可见；`cjxl` 移开 ⇒ 日志三条诚实。
- **六格式容器声明逐字节扫描**、增益图几何（§6.10）、JPEG 三档容器结构（§6.6）、libultrahdr `-P` 外部裁判。
- **英文界面三批补完**：残留产品自报 8→0、UIA 全窗 6→1（剩 1 条框架 ScrollBar 自报名）。

⇒ 本轮**把力气全花在 §1 的 G2/G3/G4 上**，不再重跑已交付的实机矩阵。

---

## 3. 方法

**分级沿用 T1/T2/T3**（09-27 §3.2）。本轮的硬规矩继承前几轮，并加一条：

1. **T2 不能推 T3**：`src/TrueToneCap.Test/TrueToneCap.Test.csproj:8` 唯一的 `ProjectReference` 是 `TrueToneCap.Core` ⇒ 测试宿主**无法实例化任何 App 类型**，所以每条点名"界面控件"的判据按构造只能是源码文本哨兵。
2. **"有判据" ≠ "会被测到坏"**：坏掉时门禁不红、只有眼睛会看见（LogViewer 工具条叠字 §14.2 就是活例）。
3. **新规矩——每条候选缺陷必须带"排除旧账"一行**：写明它是否已在 `docs/` 或交接台账里出现过。10-02 §0.5 记过一次"6 项里 4 项是旧账、2 项已拍板待命"的重报事故，根因是"待命账本只活在散文里"。本轮要求每条候选先查账再报。
4. **每条"已修"必须有当前行号可复读**：旧报告里的行号会漂（A/B 档删了 520+ 行），引用旧行号一律先回读。
5. **负断言要两条独立命令**：grep 到 0 命中时，用另一条命令对已知正样本复跑，证明这把尺子看得见东西。

**判据形状**沿用三方对齐（请求 / 实测 / 自述），六层外部裁判（L1 声明层 / L2 码流 vs 头 / L3 屏幕锚定像素 / L4 外部消费者 / L5 规范算术 / L6 变异实验）不重设计。

**对外部工具的依赖**（本轮复核可达性）：`C:\PLAN\FFmpeg` 的 ffprobe/ffmpeg、`avifdec`、`djxl`/`jxlinfo`、Pillow+lcms2、`py -3`（本机 `python` 是商店桩 ⇒ 用 `py -3`）、`ultrahdr_app -P`。

---

## 4. lane 切分：按文件切，互不重叠

lane 全部**只读**（不许 build / 不许跑测试 / 不许起产品进程）——W0 门禁正在用同一份 `bin`，动一次就把宿主时间戳改了，`[1.9/2]` 会作废整轮。

| lane | 文件面 | 要回答 |
|---|---|---|
| **L1 捕获** | `WgcCaptureService.cs`、`Core/Capture/DisplayInfo.cs`、`HotkeyManager.cs`、`Core/Detection/*`、`AnimationRecorder.cs` | 帧来源怎么选、什么时候整块退 SDR（**F1 已把 5 处降级提到 Info**，复核是否还有漏网的 Debug 档降级）、`AnimationRecorder` 零实例化的现状 |
| **L2 色彩与色调映射** | `ColorProfileProvider.cs`、`IccStore.cs`、`ToneMapper.cs`、`PixelOps.cs`、`MainWindow.ColorManagement.cs`、`App/Shaders/*.hlsl` | A 档删掉 `GpuToneMapper`/`ShaderLoader`/`ToneMapping.hlsl` 之后，**CPU 与 GPU 曲线是否还有漂移面**、三源取档失败给用户什么 |
| **L3 编码与容器** | `Core/Encoding/` 全部 + `Core/Settings/FormatOrder.cs` | 声明 vs 码流 vs 外部解码三方；后端阶梯；保底分支返回值；超时 Kill 覆盖（09-27 记 `NativeJxlEncoder`/`JpegLiNative`/`NativeWebPEncoder` 三处没有 `entireProcessTree`，**复核现状**） |
| **L4 输出与目的地** | `CapturePipelineService.cs`、`MainWindow.xaml.cs`（落盘/剪贴板/toast/状态栏段）、`ToastService.cs`、`SilentCaptureToast.xaml.cs`、`AppServices.cs`、`LogService.cs` | 命名与覆盖（P1-4 仍开？）、剪贴板各目的地、通知通道自身吞事件 |
| **L5 设置与能力协商** | `SettingsService.cs`、`Models/AppSettingsData.cs`、`SettingsDiffPruner.cs`、`SettingsWriteProbe.cs`、`CapabilityService.cs`、`SecretStore.cs`、`StartupManager.cs`、`NativeLibraryResolver.cs` | 差量写盘与修订门、只读安装目录、**`StartupManager` 把"哪个构建最后按过保存"写进开机启动（§9.3，P2）**、越界值行为 |
| **L6 UI 与后处理** | `MainWindow.xaml` + `MainWindow.{Settings,OcrSettings,Localization}.cs`、`SelectionOverlay.*`、`AnnotationWindow`/`Annotation*`、`OcrPreviewWindow`、`HdrCaptureWindow.cs`、`TrayIconManager.cs`、`Core/Services/{MultiOcrService,OnnxOcrEngine,OcrService,TranslationService,BitmapPreprocessor}.cs`、`TrueToneCap.LogViewer/*` | 收起卡点亮行、两套 overlay 键鼠差异、OCR 引擎静默换、翻译三档 `Vision` 有无代码路径、**LogViewer 零本地化机制** |
| **L7 判据体检** | `src/TrueToneCap.Test/*.cs`、`scripts/verify.ps1`、`scripts/precision/*.py`、`scripts/probes/*`、`.github/workflows/release.yml` | G3 名录：T2 冒充 T1 完整清单 / 恒真·恒跳·无牙 / App 侧执行覆盖率 / 孤儿裁判 |

**交界约定**：`DisplayInfo.cs` 归 L2；`MainWindow.xaml.cs` 归 L4，界面控件与本地化归 L6；native 后端与声明层都在 L3，在"保底分支返回值后缀"这条契约上自查。

**每条候选缺陷的格式（硬的）**：`文件:行` + 触发构造 + 用户可见性（toast / 进 `app_*.log` / 只有 `Debug.WriteLine` / 完全静默）+ P 级 + T 级 + **旧账归属一行**。

---

## 5. 交付物

| # | 交付物 | 落点 | 合格线 |
|---|---|---|---|
| **D1** | 审查报告（本轮唯一完整交付） | `docs/pipeline-review-round68-2026-10-04.md` | 每个结论指到 `tests/output/` 或具体判据；每格标 T 级；含"我自己造成的事"与"仍需眼睛/仍需实机"两节 |
| **D2** | 旧账重判表（G2） | 报告内表 | §64.22/§65/§66/§67 每条给四分类之一 + 当前证据行 |
| **D3** | 判据体检名录（G3） | 报告内表 + 计数 | T2 冒充 T1 的门控清单；恒跳过逐条点名；App 侧执行覆盖率量化 |
| **D4** | 六 lane 候选缺陷表（G4/G5） | 报告内表 | 每条带 `file:line`；【lane】标记区分主循环复核过的 |
| **D5** | 交接入账（§68，只追加不回写） | `docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` | 未修项按 P0/P1/P2 入账，每条附证据与"下一步能直接执行的那一句" |
| **D6** | 退路刷新 | `tests/output/recovery-round68/` | 未提交改动整份存补丁 + **当场验它在不在、内容对不对**（09-03 §9.1 付过学费：备份被清理器吃掉、恢复时 `cp` 把目标截断） |

---

## 6. 批次与顺序

| 批 | 内容 | 产出 | 成本 |
|---|---|---|---|
| **W0** | 基线门禁 + 显示器态登记 + 树账核对 + 退路刷新 | `tests/output/round68-2026-10-04/w0-*.log` | 门禁实测 ~3.5 min（10-01 曾量 52.8 s 测试段 / 全程 ~3.5 min） |
| **C** | 七条 lane 静态深读（只读，可并行） | 每 lane 候选表 | 与 W0 并行 |
| **G** | 旧账重判（纯读码 + 回读当前行号） | D2 表 | 低 |
| **V** | 候选回源复核（主循环自己复跑最重的 2–3 条/lane） | 升级 / 驳回记录 | 中 |
| **T** | 判据体检：T2/T1 名录、恒跳过定性、新锁量牙 | D3 表 + 变异 log | 中 |
| **H** | 汇总成文 + 交接入账 | D1 + D5 | 主循环写 |

顺序：**W0 ∥ C → G → V → T → H**。W6/T 批若需起产品进程，**先报备命令、判据与超时再跑**。

---

## 7. 执行纪律

1. **独占**：门禁轮与实机轮永不重叠；跑格子期间不并行动 `bin/`、设置文件、剪贴板（测试与产品共享这三样，动一次就改了用户机器）。
2. **设置**：轮前按字节存，收尾还原并**复算 sha256 与轮前相同**（现值那把尺见 §9.3）。测试与产品共用配置 ⇒ **跑测试能改用户机器**。
3. **不 commit / 不 push / 不打 tag**：逐次授权，且授权前本机验证要跑完。
4. **发布期间不许动工作树**（§9.12 付过学费：`dotnet run` 会重新构建，撞上我半路的编译错误）。
5. **`dotnet test` 禁用**；管道末端退出码不作数（`... | tail -2; echo RC=$?` 量的是 `tail` 的码，§9.9 付过两次学费）。
6. **判据不依赖焦点**：GUI 取证用 `PrintWindow(hwnd,…,2)` 抓被遮挡窗、`PostMessage` 不动用户光标；第一帧可能是未合成的平帧 ⇒ 取内容读数用第二帧。
7. **等自述行，不固定睡**：副作用类读数（剪贴板、落盘）必须等产品自己写出完成行再取。
8. **备份一律放仓库内**（`tests/output/recovery-*/`），不放 `C:\tmp`（本机清理器会秒删）。
9. **`MainWindow.xaml.cs` 是全 CRLF**（其余三份 `.cs` 是纯 LF）⇒ 锚点里写死 `chr(10)` 会让 CRLF 那份恒命中 0 次，这不是"没有这一行"。
10. **会话不锁屏、不休眠**（WGC 在会话离开时拿不到帧，会把超时误判成产品缺陷）。

---

## 8. 风险与不可覆盖项（报告首页要先讲）

1. **T3 缺口**：不在真机上操作就不可能证到 overlay/热键/粘贴成像；本计划把这类格子明写"缺证据"，不拿 T2 的绿当 T3 的绿。
2. **换机不可迁移**：本机 **1 块活跃显示器** `\\.\DISPLAY5` 3840×2160、分类 Hdr、10 bit、峰值 417–418 nits、EDID ΔG=0.0963 ⇒ 多屏/混合 DPI/纯 SDR 屏全部做不了；QSV/MFT 那一列本机量不到。
3. **共享机器归因**：并发会话会抢同一份 `bin` 与同一台显示器；全屏置顶窗会让光标型判据不可做。
4. **观感类结论不由我下**：HDR 观感、图标形状、界面"好不好"要眼睛。
5. **n<5 的偶发不写成因果**：崩溃与判红分开计数。

---

## 9. 前置（W0 清单）

| # | 事项 | 命令 / 判据 |
|---|---|---|
| P1 | 基线绿 | `verify.ps1` 全量：`[1/2]` 构建 → `[1.5/2]` 格式 → `[1.6/2]` 发布门禁回归 → `[1.7/2]` 外部判据 → `[1.8/2]` 色彩管线判据 → `[1.9/2]` 宿主新鲜度 → `[2/2]` 7 套件 |
| P2 | 宿主新鲜度会误伤 | `[1.9/2]` 是全局判据：只改一句注释也会因 Tools 宿主时间戳滞后整轮作废 ⇒ 处方是 `dotnet build --no-incremental` 全解重编 |
| P3 | 外部裁判可达 | `py -3` 探 ffprobe / `avifdec` / Pillow+lcms2 / `djxl`；jxl 的运行时 DLL 在 `build/lib/<Config>/` 而非 exe 同目录，直接跑退 `0xC0000135` |
| P4 | 显示器态登记 | `--hdr-probe` 取本机读数写进报告首页（结论绑定"这台机"） |
| P5 | 树账核对 | `git status --porcelain` 与 10-01 记的 58 项 / 10-03 记的 77 项对差集 |
| P6 | 退路刷新 | 未提交改动存补丁 + `git apply -R --check` 自证 + **当场读回验证内容在** |
| P7 | 残留清场 | `%TEMP%\ttc_avif_*.png`、`%TEMP%\TrueToneCap_Clip\`、`encoder-crash.*.sentinel`、`%TEMP%\nvenc_diag.txt` 拍快照，收尾比差集——孤儿文件本身就是结论 |

---

## 10. 需要用户一句话的挂账（不擅自做）

| # | 事项 | 我不做的理由 |
|---|---|---|
| 1 | 77 项树账的提交授权 | 逐次授权规矩 |
| 2 | 动图录制：接线还是下架（§54.4 已拍"先待命"） | 产品决定；保持现状是最坏选项，但决定权在你 |
| 3 | `Publish.ps1` 剔除 `color-samples/` 已做，真实发布未跑 | 发布是远端动作 |
| 4 | `OutputPath` 指向不存在的盘：改设置 / 加可见校验 / 加回落 | 回落 = 降级，要授权（10-01 已判"失败但可见"） |
| 5 | 真联网翻译（必应）要不要跑 | 会向第三方发一条文本 |
| 6 | Core 里其它喂界面的中文是否继续穷举模板化 | 签名级改动，`Declaration` 之外的未穷举 |

---

## 状态更新（执行中追加，不回写正文）

### 完成情况（2026-10-04）

| 批 | 状态 | 读数 |
|---|---|---|
| **W0** 基线门禁 | **完成（6/7 套件）** | `tests/output/round68-2026-10-04/w0-gate3.log`：`[1/2]`–`[1.97/2]` 全通过；6 套件逐套件与地板逐字一致（**563 断言**）；`--unit-tests` 环境不够未复核 |
| **P5** 树账核对 | 完成 | 开工 77 项 / 收工 80 项，**产品源码 0 行变动** |
| **P6** 退路刷新 | 完成 | `recovery-round68/worktree-bytebackup.zip`（172 文件，逐字节验证可还原） |
| **C** 七条 lane 静态深读 | **完成** | 4 条直接产出 + 3 条补跑回收，**无空跑 lane** |
| **V** 候选回源复核 | 完成 | 最重的 4 条（L5-1 / L3-1 / L1-1 / L4-2）主循环亲自回读原文 |
| **G** 旧账重判 | 完成 | 报告 §4：2 条确证已修、2 条过期记录提请退休、4 条仍开 |
| **T** 判据体检 | 完成 | 报告 §3-L7：`entireProcessTree` 0 命中；`File.ReadAllText` 80→103 |
| **H** 成文 + 入账 | 完成 | `docs/pipeline-review-round68-2026-10-04.md` + 交接 **§68**（只追加，677 210 → 682 127 B） |

### 结论首行

**本轮没有找到需要立刻改产品代码的东西**，唯一新增可执行项是 **P2**（`StartupManager` setter 吞异常）。
两条环境障碍都定位到根因并绕行；**六套件与地板逐字对上**反倒独立验证了那份地板是真的。
`--unit-tests` 记**"环境不够、未复核"**——不记通过，也不记产品坏了。

### 未做（留给下一轮）

报告的 §6 五条 + `--unit-tests` 的 1175 条地板复核。
