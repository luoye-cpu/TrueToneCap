# 软件管线实机测试计划（2026-09-20）

目标：在真机上把 **截图 → 色彩 → 编码 → 落盘/剪贴板** 全链路跑一遍，回答四个问题——
功能是否健康、输出图像是否正确、UI 组合之间是否互相打架、**有多少降级是用户完全看不见的**。

基线坐标：`v0.3.4-beta`（工作树干净，`git status --porcelain` 空）。**本计划只观测、只记账，不修色彩管线输出形态**（0.3.4-beta 仍在等用户目视确认）。

---

## 1. 判据总纲：三方对齐

现有资产（`verify.ps1` + 8 条外部判据 + `--selftest` + 373 条 C# 断言）判的是**合成样张的容器声明**。它有两处结构性失明：不经 App 的捕获入口（`--wgc-avif-tests` 自建会话、帧池硬编码 `B8G8R8A8`），以及完全不覆盖热键 / overlay / 标注 / 剪贴板 / UI 组合。

所以本计划的每一条结论都由三方读数对齐产生：

| 方 | 内容 | 采集方式 |
|---|---|---|
| **请求** | 用户当时在 UI 上选了什么 | 截图前把 `FileNamePrefix` 写成 `E2E<caseId>_`，产物文件名即关联键；同时快照一份 `TrueToneCap.settings.json` |
| **实测** | 产物到底是什么 | 外部解码器 + 像素比对 + 规范算术（见 §3），**不经产品代码** |
| **自述** | 应用有没有说话 | `log/app_*.log` 全文 + toast 是否弹出 + 状态栏文本 + sentinel 文件 |

四类结论，只有第四类是本次要抓的东西：

1. 请求 == 实测 → 正常。
2. 请求 != 实测，**自述说清了** → 诚实降级（记为设计行为，附证据）。
3. 请求 != 实测，**自述沉默** → 🔴 **静默降级**，本次的核心交付物。
4. 请求 == 实测，自述却报错 → 假警报，同样要记账（它会让用户不信任提示）。

> 为什么必须这样设计：`CapturePipelineService.cs:646,674-676` 的 `s_hwFallbackNotified` 让**每条降级 toast 每进程只弹一次**，`EncoderFallbackNotifier` 是单个 `volatile string?` 槽、会被并发编码互相覆盖且读后即清（`CapturePipelineService.cs:665-667`）。⇒ **"盯着 UI 看有没有提示"这个观测手段本身就是坏的**，第 2 拍之后必然看着很干净。三方对齐不依赖应用的自述完整性，所以不受这个坑影响。

---

## 2. 前置条件

| # | 事项 | 命令 / 判据 | 状态 |
|---|---|---|---|
| P1 | 基线绿 | `powershell -File scripts/verify.ps1 -IncludeDesktop`，记录断言数与 8 条判据退出码 | 交接 §32.14 记的是 979/0，**本轮复跑取新数** |
| P2 | 真身二进制自检 | `publish/TrueToneCap-v0.3.4-beta/TrueToneCap.exe --selftest --out artifacts/e2e/<runid>/selftest` | 读 `selftest.json` + `progress.txt`，不信退出码单独说话 |
| P3 | 门禁钩子是否真挂载 | `git config --get core.hooksPath` | **已实测为 `scripts/git-hooks`**（记忆里的"未挂载"结论已过期）。⚠ 副作用：整体替换 `.git/hooks`，Qoder tracker 的 `post-commit`/`post-checkout` 停摆 |
| P4 | 外部裁判可达 | `py -3 -c` 探测 ffprobe / avifdec / Pillow / imagecodecs + `precision_env.py` 的发现逻辑 | 用 `py -3`，本机 `python` 是商店桩（退出码 49、无输出） |
| P5 | 待授权的远端件 | JXL 的 ICC **目前无任何外部裁判**（`djxl` 不在、Pillow 12.3 无插件、imagecodecs 无 JXL） | 需下载 libjxl `djxl.exe` 才能补这一格，**等点头** |
| P6 | 显示器态登记 | `--hdr-probe`：本机记录为 HDR10 / 10-bit / P3 / 418 nits / ACM on | 矩阵结论必须绑定"这台机器的显示器态"，换机不可迁移 |
| P7 | 长跑环境 | 关闭自动睡眠与锁屏；WGC 在锁屏/会话离开时拿不到帧，会让超时误判成产品缺陷 | 每拍前确认会话 active |
| P8 | 残留清场 | `%TEMP%\ttc_avif_*.png`、`%TEMP%\TrueToneCap_Clip\`、`.%name%.%guid%.tmp`、`%LOCALAPPDATA%\TrueToneCap\encoder-crash.*.sentinel`、`%TEMP%\nvenc_diag.txt` | 开跑前拍快照，收尾比对差集（孤儿文件本身就是结论） |

**执行纪律（都是踩过的坑）**：`dotnet test` 禁用；构建失败即作废随后所有 `RESULT` 读数；管道末端退出码不作数（`set -o pipefail` + `${PIPESTATUS[0]}`）；相对路径门禁前先 `pwd`；PowerShell 经 MSYS bash 调用会被静默破坏，判据脚本一律从 PowerShell 侧起。

---

## 3. 图像正确性：六层独立裁判

**每一层都要自带自检**（已知为真的样本必须通过、已知残缺的必须被拒；自检不过 ⇒ 这一层明说"不给结论"并判红，不许凑绿）。这是 ICC 标定那轮 LittleCMS 放过 700 B 截断件换来的规矩。

| 层 | 判什么 | 手段 | 参照物独立性 |
|---|---|---|---|
| **L1 声明层** | 容器把头写成了什么 | 复用 8 条 `scripts/precision/*.py`（`param_sweep_check` 的 H.273 手编码点表、`cicp_consistency`、`icc_readback_matrix` 的 ICC.1 结构自洽、`ramp_levels` 的 64 级台阶计数、`chroma_knob_check` 的**字节恒等**判据、`gamut_depth_consistency` 带负对照、`jpeg_icc_readback`、`av1c_vs_bitstream`） | 已确证独立，脚本头自己声明"never via TrueToneCap code" |
| **L2 码流 vs 头** | 声明与码流真身是否互相说谎 | `av1c_vs_bitstream.py` + `scripts/probes/nvenc_matrix_probe.py`（问驱动实际用了哪个 RGB→YUV 矩阵） | 两条不同实现交叉，曾据此推翻过一轮全绿 |
| **L3 屏幕锚定像素**（**新建，本次最缺的一块**） | 产物像素 vs 屏幕上真实显示过的值 | 屏幕渲染一张数值已知的测试卡 → 用 `Graphics.CopyFromScreen`（GDI 路径，独立于 WGC 与整个编码栈）取参考像素 → 与解码后的产物比对；配**负对照**（同图故意按 sRGB↔P3 两套走，必须可见差，否则证明矩阵从没跑） | 参照物来自 Windows 合成器，不经被测代码 |
| **L4 外部消费者** | 别人打不打得开、打开后是不是同一张图 | 五家独立解码器读同一产物：ffmpeg/ffprobe、libavif `avifdec`、Pillow+lcms2、**Edge headless 浏览器 canvas**（`toDataURL` 取回像素，这是唯一能证明"网页端看到什么"的裁判）、`publish/PLAN/tools/ultrahdr_app.exe` + `libuhdr.dll`（GainMap 的**真**外部解码，比现在的 `deep_color_analysis.py` 拿自家 Base 当参照强得多）。另加 Windows Shell WIC（`System.Windows.Media.Imaging`）代表资源管理器/画图 | 全部外部实现 |
| **L5 规范算术** | 数值对不对 | 手写 PQ EOTF（SMPTE 2084 常数）、`rXYZ+gXYZ+bXYZ` 列和（只作诊断，**不断言等于 `wtpt`** —— 微软 sRGB 的 wtpt 记原始 D65，差 0.264 会误杀真件）、位深/台阶统计 | 规范文本 |
| **L6 变异实验** | 旋钮有没有真的进码流 | 只改一个参数、其余逐字节相同地重跑：产物字节**必须**变；声称无关的参数产物**必须**逐字节相同。反向的用 `--resampling` 那次教训：先确认工具真的回答了，`grep` 无匹配不等于不支持 | 自证，最硬的因果证据 |

**已知盲区，报告里必须原文列出**：JXL 的 ICC（等 P5）；QSV/MFT 路（本机无 AV1 MFT，`QsvEncoderNative.IsAvailable` 恒 false，**不许在无 Intel 核显的机器上推测其行为**）；HLG 只被检测成"HDR"而全链路按 PQ 处理（`DisplayInfo.cs:160`）；`MetadataCollector` 采集的窗口标题/进程名/显示器名**没有任何编码器读取**（死数据，别为它写断言）。

---

## 4. UI 组合冲突矩阵

全叉约 10⁵ 量级，实机跑不动也没必要。**用 pairwise 打底 + 20 个热格子穷举**。分两个可达域，因为它们的失效模式完全不同：

- **A 域：UI 允许你选出来的组合**（下拉会隐藏不可行项，`MainWindow.xaml.cs:748-812`、`SetItemsVisible:963-999`）
- **B 域：只能从 `settings.json` 手写 / 旧配置迁移 / 索引错位进来的组合**（`Normalize()` 只在启动时跑一次，`SettingsService.cs:118`；且 `ColorSpaceIndex` 白名单收 0–5 而下拉只有 3 项，`:277`）

⚠ 热格子 1、3、5、6 的结论**必须由实验产生，不由读码产生**——代码注释与实现已经分叉过一次（`MainWindow.Settings.cs:62` 声称 `DisplayP3→sRGB`，`CollapseSelectableTag` 实际把三种全收进 BT.2020）。

| # | 格子 | 域 | 决定性实验 | 判据 |
|---|---|---|---|---|
| 1 | GainMap 锁定的持久化污染 | A | HDR 关 + 目标色域 sRGB → 选 JPEG Gain Map → **点"保存设置"** → 重读 JSON → 换回 PNG → 重启进程看 UI | 读码已确证：锁定期 `HdrSwitch.IsOn=true`/`ColorCbo.SelectedIndex=BT2020`（`MainWindow.xaml.cs:566-597`），而 `SaveSettings()` 无条件读控件值（`MainWindow.Settings.cs:170-172`）。要实验判的是**污染的是 JSON 还是只有内存** |
| 2 | HDR on × 目标色域 sRGB | A | 三种色域 × HDR on 各一拍，同格式 | 读码 `CapturePipelineService.cs:266-270` 会把 `HdrOutput` 改回 false。自述只有一条 Info 日志、**无 toast** ⇒ 若产物确为 SDR 而用户没被告知，记入 §5 的 3 类 |
| 3 | AVIF Auto × 色度 422/444 | A | 计时跑，别只看产物 | 444 会**静默 forfeit 硬件加速**（约 0.12 s → 约 22 s，`CapturePipelineService.cs:651`）。协商上" satisfiable by libaom"所以不弹 toast ⇒ 用户视角是"卡死了"。这一格要单独定性：**行为正确、体验是缺陷** |
| 4 | 位深 >8 × 四个目的地 | A | PNG16/JXL16/TIFF16/AVIF12 分别走 保存 / 复制 / 标注后保存 / 无感截图 | `byte[]` 入口一律钳到 8-bit（`CapturePipelineService.cs:487-492`，`FormatEncoders.cs:90,241,537`）。**复制和标注后保存恒 8-bit**，UI 下拉却仍然显示 16 |
| 5 | 首运行色域索引越界 | B | 删 settings.json → 在 HDR 机上起进程 → 看下拉显示什么 vs JSON 里 `ColorSpaceIndex` → 点保存设置 → 再读 JSON | `DetectBestColorSpace` 对 HDR 面板返回 5 而 combo 只有 3 项（`MainWindow.xaml.cs:398-407` 刷了 HDR/ICC 两个开关，**没刷 ColorCbo**） |
| 6 | `.avif.png` / `.jxl.png` 后缀 × 兜底 | B | 开后缀 + 把 `native\avifenc.exe` 移走 → 编码 → 看文件名与实际字节 | 兜底 PNG 用 `Path.ChangeExtension` 算路径（`FormatEncoders.cs:447,1484`），对 `x.avif.png` **返回同名** ⇒ `ResolveActualPath` 无从纠正，得到一张顶着 `.avif.png` 名的 PNG |
| 7 | `QualityAvif=0` 从 JSON vs 从 UI | B | 手改 0 → 起进程 → 看滑块位置 → 编码 → 读回码流是否 lossless | 硬件态只抬滑块下限到 1（`MainWindow.xaml.cs:476`），`Normalize` 仍收 0（`SettingsService.cs:270`）⇒ 两条路对同一个值给出不同答案 |
| 8 | `HardwareEncodingEnabled=false` | B | JSON 写 false → 拍一张 → 点保存设置 → 重读 JSON | 每次保存恒写回 true（`MainWindow.Settings.cs:193-194`），而 Core 真的在读它 ⇒ **"关不掉硬件"**，且 crash sentinel 已无手动复位入口 |
| 9 | 标注 → 识字/翻译 | A | 给敏感区域打马赛克 → 按识字 | 标注层只对 Confirm/Copy 合成（`SelectionOverlay.xaml.cs:715-720`），OCR 读的是**未合成的桌面像素** ⇒ 马赛克挡不住 OCR。隐私向，优先级按产品定 |
| 10 | 标注 → 保存（HDR 屏） | A | 加一层标注后保存，读回位深 | 一层就够把路改到 `EncodeAndSaveAsync` ⇒ 8-bit SDR（`MainWindow.xaml.cs:1933-1937`）。自述：无 |
| 11 | 双屏 + 光标在副屏 + 跨屏选区 | A | 两台屏（一 HDR 一 SDR），光标放 SDR 屏，拉一个跨屏选区 | 三个来源三套规则：像素=全桌面拼接、ICC=光标屏（`CapturePipelineService.cs:402-406` 编码时才取 handle）、HDR 参考=primary 优先（`WgcCaptureService.cs:1511`）。**光标在取 handle 前移动 ⇒ 像素与声明分属两屏** |
| 12 | HDR 会话尺寸不匹配 | A | 多屏不同分辨率下反复拍 | `WgcCaptureService.cs:1071-1077` HDR 像素整块丢弃退 SDR，只有 Info 日志 |
| 13 | Float16 冷却期 | A | 连续拍 15 张，中途令 HDR 会话空帧 3 次 | 之后**10 张**直接跳过 Float16（`WgcCaptureService.cs:837-856`）⇒ 同样的设置先出 HDR 后出 SDR。要能区分"退化"与"冷却"，只能靠 Debug 级日志时间戳 |
| 14 | QSV 隐藏/恢复态 | A | 选 QSV → 开 HDR → 关 HDR → 看下拉是否还是 QSV | `_avifEncoderHiddenByHdr`（`MainWindow.xaml.cs:764-787`）是有状态交互 |
| 15 | `FormatIndex` 越界 | B | JSON 写 99 / 写 -1 → 起进程 | `Math.Clamp` 到列表边缘 ⇒ 存的 AVIF 偏好静默变 PNG（`MainWindow.xaml.cs:2405`），无提示 |
| 16 | 保存/取消快捷键匹配 | A | 在 overlay 里按 Ctrl+S、Alt+S；按设定的取消键 | 修饰键被忽略（`SelectionOverlay.xaml.cs:405` 直接比 `e.Key`）；`CancelShortcut` **从不参与匹配**，Esc 硬编码 |
| 17 | 慢编码期间改设置 | A | 用 4K libaom AVIF（约 22 s）作窗口，编码中改前缀/输出目录/归档 | 落点走 `_settings.Current` 的**实时**读法（`CapturePipelineService.cs:416,433`）而非快照 ⇒ 产物落点与用户当时所见不符 |
| 18 | 归档 `Week` | B | JSON 写 `Week` | 白名单收 4 值、下拉只 3 项（`AppSettingsData.cs:122`） |
| 19 | 主题切换后截图 | A | 切 Dark/OLED → 不重启 → 打开 overlay 与标注窗 | `RequestedTheme` 启动后不可变；已建窗口不跟随 |
| 20 | 录像 / `OnCaptureNow` | A | 按录制热键；找全屏截图按钮 | `RecordHotkey` 只有文本管道，**无任何 `RegisterHotKey` 调用**（已实测确认）；`AnimationRecorder` 未被实例化；`OnCaptureNow`（`MainWindow.xaml.cs:2221`）在 XAML 里零命中 ⇒ 三条死路。`architecture-design.md` 还把后者列为"5 号入口"，属文档与实现分叉 |

---

## 5. 静默降级台账（本次核心交付物）

读码得到的降级点约 **68 处**。台账每行八列：**位置 / 触发构造 / 实际行为 / 有 toast? / 进 `app_*.log`? / 只有 `Debug.WriteLine`? / 外部能否推断 / 结论分类**。

按"能不能被观测到"分四档，档位决定用什么手段抓：

| 档 | 含义 | 抓法 | 代表位置 |
|---|---|---|---|
| **D0 通道自身损耗** | 事件发生了、也生成了，但通知机制把它吞了 | **每拍单起一个进程**，或把"toast 每进程只一次"本身列为被检对象 | `s_hwFallbackNotified`（`CapturePipelineService.cs:646,674`）、`s_lastDegradedToast` 按字符串去重（`:320-342`）、`EncoderFallbackNotifier` 单槽读后即清（`:665-667`） |
| **D1 只进日志** | 有 Info/Warn，用户不会看 | 三方对齐的"自述"列 | HDR+ sRGB 退 SDR（`:269`）、AVIF 位深钉 10（`:352`）、8-bit 源钳位（`:489`）、系统 nits 覆盖用户值（`:254`）、**无感截图的编码失败既不弹 toast 也不写状态**（`MainWindow.xaml.cs:1418-1421`，而按钮路 `:2366` 会 toast）、SDR 像素为空时静默 return（`:1392-1397`） |
| **D2 只有 `Debug.WriteLine`** | **既不落日志文件也不落 UI**，产品构建下等于黑洞 | **只能靠外部推断**（期望 vs 实测的差），零 App 改动 | Float16 会话构建异常退 8-bit（`WgcCaptureService.cs:1086-1089`）、多屏任一屏没填满整块退 SDR（`:1446-1450`）、HDR overlay 初始化失败退回 SDR overlay（`MainWindow.xaml.cs:1969`）、**标注合成超 10 s 就保存未标注的裁剪区**（`SelectionOverlay.xaml.cs:728-734`）、`avifenc` 写 ICC 临时文件失败 ⇒ 广色域 AVIF 无 ICC 且无警告（`NativeAvifEncoder.cs:186-188,413-423`）、HDR+AdobeRGB 被就地改写成 BT.2020 声明（`FormatEncoders.cs:1836`）、Ultra HDR 在 HDR 关时退成普通 JPEG（`JpegGainMapEncoder.cs:55-64`） |
| **D3 完全静默且返回值看似成功** | 兜底逻辑返回"宽松默认值"，连日志线索都没有 | 只能靠**构造环境**逼出来再比对 | 能力查询异常 ⇒ `CanDeliverExtendedRange` 返回 **true**（`WgcCaptureService.cs:764-767`）、ACM 读不到 ⇒ 返回 true（`:780`），而**同族**的 `ReadSystemAcm` 失败时返回 **false**（`CapturePipelineService.cs:165-169`）—— 两个"宽容默认"方向相反；GPU 适配器枚举异常 ⇒ 静默丢 AV1 能力、Auto 退软件（`GpuCapability.cs:94`）；`GetSelectedColorSpaceTag` 跨线程异常 ⇒ 返回 "System"（`MainWindow.ColorManagement.cs:165-171`，历史上就是"用户选 P3 结果按 System 走"的根因，陷阱仍在）；`AtomicFileWriter` 建 temp 失败 ⇒ 直写最终路径、丢掉崩溃安全（`AtomicFileWriter.cs:36-42`）；`ResolveActualPath` 在目标不存在时返回**未写出的请求路径**（`FormatEncoders.cs:1522-1533`）；`EncoderFactory` 未知格式 ⇒ **PNG**（`:2132`） |

**进程级例外，单独一档**：`NativeEncoderGuard.cs:104-117` 的注释自己承认那两条 catch 在 .NET 11 永不触发，原生 CSE 直接吃掉进程、`LogCrash` 只写 `Debug.WriteLine` 与 stderr、**不进 `app_*.log`**。⇒ "进程消失"必须是一等测试结果而不是超时；取证走 `scripts/probes/crash_probe.ps1`（系统日志 41/1001/4101/6008、`nvlddmkm`/`dxgkrnl` TDR、`C:\Windows\Minidump`、WER）+ `py -3 scripts/probes/minidump_blame.py <dmp>`（无需 WinDbg 就能点名肇事模块），外加 `encoder-crash.<backend>.sentinel`（它会让**下一次启动**避开该后端，界面上完全看不出来）。

**触发构造清单**（D2/D3 大部分只能逼出来，不能靠常规跑遇到）：移走 `native\{avifenc,cwebp,cjxl,cjpegli}` 逐个制造编码器缺失；预置 sentinel 文件；`TTC_FORCE_SDR=1`（`MainWindow.xaml.cs:1874`）作所有 HDR 格的对照组；拔掉显示器 ICC 关联 / 断网制造 WCS 取档失败（`ColorProfileProvider.cs:359-384` 无日志退 null）；两台同型号屏制造 EDID 指纹碰撞（`GetMonitorFingerprint` 整体 `catch {}` 退 `""`，`:510`）；制造连续空帧进冷却；把 `ChromaAvif=444` 直调后端以绕开预钳、触发 NVENC 的 4:2:0 偷换（`FormatEncoders.cs:1071-1075`，容器照声明 444 写）。

---

## 6. 功能健康清单（"所有功能可用"逐项要有证据）

每项产出 = 一个产物文件 + 一条日志行 + 一张证据截图，缺一项不算"健康"。

热键（区域 / 无感 / 冲突回退链 `Ctrl+Shift+X`→`Ctrl+Alt+S`→`Alt+Print`→`Print`）· 托盘左键与三项右键菜单 · **两套 overlay 实现**（HDR 原生 D3D11 窗 `HdrCaptureWindow` vs SDR WinUI `SelectionOverlay`，两套的键鼠处理与配色各自测）· 标注 6 工具 × 3 处实现（overlay 内联 / 独立 `AnnotationWindow` / 同一 `AnnotationRasterizer`）+ 撤销重做 · 60 s 空闲超时与 3 min 硬看门狗 · OCR 三引擎（ONNX-GPU / ONNX-CPU / Windows OCR，含**引擎静默降级**：所选未注册则 `_engines[0]`、空结果与异常都自动重试 Windows OCR，`FallbackFrom` 字段只进 Debug，`MultiOcrService.cs:144-231`）+ 45 s 看门狗（超时任务是被**丢弃不是取消**，`:236-245`，反复超时会堆 DirectML 占用）· 翻译三模式（**`Vision` 无代码路径**，与 `LLM` 同行为；有道凭据**全仓无 UI 控件**且 `YoudaoAppSecret` 是 `[JsonIgnore]`，等于不可设置）· 剪贴板双通道（`SetStorageItems` + `SetBitmap`；后者对 AVIF/JXL 常失败并只写状态栏，`:1489-1503` ⇒ "粘进聊天框没反应"）· 文件命名与归档 · 日志页与 `LogViewer` · 开机自启 · 语言/字体（界面文案"需重启生效"已过期，实况是新建窗口即时生效、但启动后新装的字体仍不可见）· 动图录制（预期不可达）。

---

## 7. 新建的唯一一块基础设施

**现状：没有可复用的 GUI 驱动 harness。** `tests/manual/` 49 个脚本全 untracked、25 个指向已清空的 `Pictures\TrueToneCap`；`smoke-anno-test.ps1` 是唯一较完整的驱动，但它轮询 `bin\x64\Debug\...\log`——该目录不存在（日志实际在 Release 树），**触发检测永远观察不到变化**，已判红。`test_hang.ps1` 同病。

⇒ 新建 `scripts/e2e/ttc-e2e.ps1`，把这些已验证可用的技术收拢成一个带参数、带退出码的驱动器，而不是去救旧脚本：

- 起 `publish\TrueToneCap-v0.3.4-beta\TrueToneCap.exe`（**测发布包，不测 Debug bin**）
- 触发：`PostMessage` 级鼠标键盘（`tests/manual/diag_ttc_window.ps1` 的路子，**不要求前台焦点**，这是它比 `SendKeys` 强的地方）+ 按类名 `TTC_HC` 找窗（`enum_windows.ps1` 的按 PID 枚举）
- 假死判定：`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`
- 证据：`PrintWindow(hwnd, hdc, 2)` 抓 overlay 本体 + `Graphics.CopyFromScreen` 抓桌面（后者兼作 §3-L3 的参照像素源）
- 每拍输出一个 case 目录：请求快照 / 产物 / 日志切片 / 截图 / 三方对齐结论（JSON），退出码 **0 通过 / 1 回归 / 3 SKIP**，**绝不"什么都没判还退 0"**

---

## 8. 批次

| 批 | 内容 | 产出 |
|---|---|---|
| A | 前置 P1–P8 全跑，登记基线数字与环境事实 | 基线记录（含真实断言数） |
| B | 建 `scripts/e2e/ttc-e2e.ps1` + 测试卡，先用它复现已知为真的两条（HDR 源可达、AVIF 声明正确），**证明驱动器本身不是假绿源** | 可用的驱动层 + 驱动器自检 |
| C | §3 六层图像正确性 | 每张产物的多层读数表 |
| D | §4 二十个热格子 + pairwise 覆盖 | 矩阵结果表，含每格决定性实验的实际读数 |
| E | §5 约 68 点降级台账，逐点给触发构造与四列观测结论 | `静默降级台账` 表 |
| F | 汇总 | 本报告续写 + 交接文档新开一轮（未修项按 P0/P1/P2 入账，附证据数字） |

---

## 9. 需要你拍板三件事

1. **要不要给 App 加一层"每拍降级流水账"**（env 门控的 JSONL，每拍一条、不去重、不清空）。§1 的三方对齐已经能不依赖它把 D1/D2/D3 全部抓出来；加了它只能把"为什么退的"从推断变成直述。**代价**：动了已发布版本的代码，且按"一条提交=一个版本"要并到 0.3.5。我倾向**先不加**，Phase E 结束时按实际抓不到的比例再定。
2. **P5 的 `djxl.exe` 下载**：不点头就 JXL 的 ICC 这一格保持"无裁判"，报告会明写少了哪一面。
3. **UI 驱动方式**：PowerShell `PostMessage`（零新依赖，够覆盖本计划）vs 引入 UIA/WinAppDriver（能直接读控件状态，少猜很多 A 域组合，但要在本机装东西）。我倾向前者。
