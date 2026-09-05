# TrueToneCap / 真色截图

> **v0.3.3-beta** · Windows 11 24H2+ · WinUI 3 · .NET 11 · WGC

TrueToneCap 是一把为像素而生的手术刀。

在这个 HDR 显示器逐渐普及、但截图工具仍停留在 SDR 时代的间隙里，TrueToneCap 选择了不同的路——从 DXGI 底层直接捕获显示器的 Float16 浮点帧缓冲，保留每一尼特的光照信息，再用 GPU 色调映射将其优雅地落入人眼可见的范围。

框选、标注、OCR、翻译——所有操作都在按下快捷键后的全屏覆盖层上实时完成，无需弹窗、无需跳转。截图可以导出为 PNG（数学无损）、JPEG Gain Map（兼容 Ultra HDR）、AVIF、JPEG XL 等格式。对于国内用户，翻译引擎内置了有道 + Google 多端点自动降级，LLM 也可按需接入。

---

TrueToneCap is a scalpel built for pixels.

In the gap where HDR displays are becoming ubiquitous yet screenshot tools remain stuck in the SDR era, TrueToneCap takes a different path — capturing the display's Float16 framebuffer directly from the DXGI back-end, preserving every nit of luminance, then gracefully tone-mapping it into the visible range via GPU shaders.

Selection, annotation, OCR, translation — everything happens in real-time on a full-screen overlay after pressing a hotkey, with no pop-ups and no context switches. Screenshots export to PNG (mathematically lossless), JPEG Gain Map (Ultra HDR compatible), AVIF, JPEG XL, and more. For users in China, the translation engine includes Youdao + Google multi-endpoint automatic fallback, with custom LLM (OpenAI/DeepSeek) available on demand.

---
---

## Quick Start / 快速开始

1. Download `TrueToneCap-v0.3.3-beta-win-x64.zip`, extract / 下载解压
2. Run `TrueToneCap.exe` / 双击运行
3. Press `Ctrl+Shift+S` to capture / 按快捷键截图

> **System / 系统**: Windows 11 24H2+ · HDR display optional / HDR 显示器可选

---

## Features / 功能

| Feature | Detail |
|---------|--------|
| 📷 Capture / 捕获 | WGC (Windows.Graphics.Capture), Float16 HDR / BGRA8 SDR, 池化零延迟 |
| 🎨 Color / 色彩 | ICC detection & baking, BT.2020 / sRGB / Display P3, ACM |
| ✏️ Annotate / 标注 | Rect, Ellipse, Arrow, Pen, Text, Mosaic — full-screen overlay |
| 🖼️ Export / 导出 | PNG (lossless HDR) · JPEG Gain Map (Ultra HDR) · JPEG XL · AVIF (QSV/NVENC) · WebP · JPEG LI · BMP |
| 🔤 OCR / 识字 | Windows OCR + preprocess (contrast / scale-up / threshold) / 预处理管线 |
| 🌐 Translate / 翻译 | Youdao → Google multi-endpoint → custom LLM (OpenAI/DeepSeek) |
| ⌨️ Hotkeys / 热键 | Recordable / 可录制 · tray minimize / 托盘 · autostart / 开机启动 |

---

## Output Formats / 输出格式

| Format | HDR | Bit Depth | Encoder |
|--------|-----|-----------|---------|
| PNG | ✅ cICP | 8/10/12/16-bit | 托管编码器 (ManagedPngEncoder) |
| JPEG Gain Map | ✅ Ultra HDR | 8-bit + gain map | jpegli + 增益图 |
| JPEG XL | ✅ | 8/10/12-bit | JxlNet (NativeJxlEncoder) |
| AVIF | ✅ | 8/10/12-bit | libaom / QSV / NVENC / MFT |
| WebP | ❌ | 8-bit | libwebp / cwebp 回退 PNG |
| JPEG LI | ❌ | 8-bit | jpegli |
| TIFF | ✅ | 8/16-bit | 托管编码器 |
| BMP | ❌ | 8-bit | 托管编码器 |

---

## Build / 构建

```powershell
dotnet restore
dotnet run --project src\TrueToneCap.App -c Release
.\Publish.ps1   # one-click publish / 一键发布
```

---

## Changelog / 更新日志

### v0.3.3-beta — 2026-08-29 ~ 08-31

- ⚡ **NVENC 硬件编码修复（重大）** — 此前 NVENC 从未成功初始化过，实测定位并修复 6 处缺陷：
  - `NV_ENCODE_API_FUNCTION_LIST.version` 缺 `(1<<16)|(0x7<<28)` → `NvEncodeAPICreateInstance` 恒返回 `NV_ENC_ERR_INVALID_VERSION(15)`，**任何系统都无法初始化**
  - `NV_ENC_DEVICE_TYPE` 枚举语义混淆：现代定义 DX11=2，但实测驱动（616.56 / RTX 5080）按旧版语义（DIRECTX=0）解释 → 现运行时依次尝试
  - 各结构体 subversion 不同（`INITIALIZE_PARAMS`=5、`CONFIG`=6、`PIC_PARAMS`=4、`REGISTER_RESOURCE`=3、`MAP_INPUT_RESOURCE`=4），原实现统一用 1
  - `NV_ENC_INITIALIZE_PARAMS` 字段偏移全部错位 +4（GUID 对齐为 4 而非 8）→ `encodeGUID`/`encodeConfig` 等读到垃圾
  - `NvEncDestroyEncoder` 函数索引误用 24（实为 `NvEncUnregisterAsyncEvent`）→ 会话从未销毁、累积达上限
  - 纹理直通路径 3 个函数索引同样错位 → 该路径完全不可用
- ⚠️ 已知限制: `NV_ENC_CONFIG` 内部偏移（qp 等）需按现代 SDK 头文件重排，故硬件编码仍会回退 libaom。功能与画质不受影响，修复已使 NVENC 从"完全不可用"推进到"会话可建立"
- 🐛 NVENC 补充修复: codec GUID 与 nvEncodeAPI.h 标准值不符（HEVC 应为 `790CDC88-4522-4D7B-9425-BDA9975F7603`，AV1 为 `0A78D0B8-1D63-4B45-8E6B-C5F41C7C8C2C`）
- 🐛 NVENC 补充修复: `NV_ENC_CONFIG` 缓冲区仅 512 字节（实际需 3400+）→ 驱动读写越界；改用 **NvEncGetEncodePresetConfig 由驱动填充**，并加入三级渐进回退（完整/仅GOP/纯预设）
- 🐛 NVENC 补充修复: preset GUID 改为运行时枚举（NvEncGetEncodePresetGUIDs），不再硬编码，避免驱动不接受时 profileGUID 全零
- 🧪 新增 `NvEncoderNative.EnumerateCodecGuids()`，测试工具可列出驱动报告的 codec GUID 与标准值比对
- 🐛 **NVENC: 修正 AV1 codec GUID** — 实测驱动（RTX 5080/4060）报告的真实值为 `0a352289-0aa7-4759-862d-5d15cd16d254`，原代码用 `0a78d0b8-...` 导致 `NO_ENCODE_CAPABILITY(0x8)`
- 🐛 NVENC: `profileGUID` 改为运行时枚举（`NvEncGetEncodeProfileGUIDs`），此前全零使驱动拒绝初始化
- 🐛 NVENC: 修正 `NV_ENC_PRESET_P1_GUID` 为标准值 `49DF21C5-6DFA-4FEB-9781-51BEE52B390C`
- ⚠️ **NVENC 仍回退 libaom**: 驱动对 AV1 **不提供任何 preset**（实测 `GetEncodePresetGUIDs` 返回空），故必须手工构造 `NV_ENC_CONFIG.encodeCodecConfig.av1Config`；该联合体成员的偏移需现代 SDK 头文件（本地仅有旧版 8.1）才能确定。当前配置层级已改为驱动填充+渐进回退，profileGUID 有效，仅缺 av1Config 字段。功能与画质无影响。
- ⚡ Perf: Auto 模式下硬件 AVIF 后端**从未被选中** — 原优先级把内嵌的 libaom 置于最高（avifenc.exe 随程序发布，恒可用），NVENC/QSV 分支成死代码。现改为有明确 AV1 能力的硬件优先
- 🐛 Fix: 后端选择仅校验 `Available` 未校验 `SupportsAv1`，导致不支持 AV1 编码的显卡（如 RTX 30）被误选后失败回退
- 🐛 Fix: NVENC 可用性探测创建的 D3D11 设备未释放，每次探测泄漏一个设备
- 🧪 Test: 新增 WGC 真实捕获 + AVIF 后端验证工具（`--wgc-avif-tests`），含 GPU 能力探测、逐后端编码计时、容器结构独立解析

---

- 🔧 Refactor: MainWindow.xaml.cs 按职责拆分为 5 个 partial 文件（主文件 3233→2653 行）/ MainWindow split into partial classes
- 🖼️ Encoding: TIFF 输入位深改为显式参数，消除按数组长度推断的歧义 / TIFF explicit input bit depth
- 🌐 Translation: 有道翻译增加文本长度上限校验，超限显式降级而非静默失败 / Youdao length guard
- 🐛 Critical: **修复启动即崩溃** — P/Invoke 未指定 W 入口点（user32.dll 只导出 RegisterWindowMessageW/A），解析失败抛异常于 App 构造函数早期 → WinUI FailFast (0xC000027B)。同时修复 SetProcessDpiAwarenessContext 参数应为指针尺寸类型 / startup crash fix
- ⚡ Perf: **修复 Auto 模式下硬件 AVIF 编码从未生效** — 原优先级把 libaom 置于最高，而 avifenc.exe 随程序内嵌发布（恒可用），导致 NVENC/QSV 分支成为永不执行的死代码。4K AVIF 无论显卡如何都跑约 22 秒软件编码。现改为「有明确 AV1 能力(SupportsAv1)的硬件优先」，后端内部有完善的失败回退 / hardware AVIF never selected in Auto mode
- 🐛 Fix: 后端选择仅校验 Available（NVENC 会话可创建）而未校验 SupportsAv1，导致 RTX 30 等不支持 AV1 编码的显卡被选中后失败回退 / AV1 capability not checked
- 🐛 Fix: NVENC 可用性探测创建的 D3D11 设备未释放，每次探测泄漏一个设备 / D3D device leak in NVENC probe
- 🖼️ Encoding: 修复 AVIF iloc box 的 sizes 字段 — 原写 0x44 (offset_size=4) 为 ISOBMFF 无效值，与 4 字节写入自相矛盾 / AVIF iloc sizes fix
- 🎨 Color: **修复 SDR 白点处的亮度断崖** — SegmentedReinhardMap 在 y=1 处非单调，导致刚超过 SDR 白点的高光反而更暗（暗环伪影）。重建为单调曲线 / tone mapping dark-ring fix
- ⚡ Async: 移除编码路径的双层 Task.Run + GetAwaiter().GetResult()，一次编码不再占用 2 个线程池线程 / async encoding fix
- 🔒 Capture: 捕获闸门由"立即失败"改为带超时排队，连续按截图键不再直接报错 / capture gate queuing
- 🧪 Test: 修复色调映射单调性测试的尺寸盲区（64 像素按 16 调用，从未采样到 y=1），新增 5 项断崖回归用例 / tone map test blind spot fixed

- 🛡️ Stability: 修复 `PAINTSTRUCT` 结构体尺寸不足导致的栈溢出（每次 WM_PAINT 破坏栈帧）/ stack buffer overflow fix
- 🛡️ Stability: 帧状态改为原子发布，修复渲染线程"新数组+旧尺寸"撕裂导致的越界读写 / frame state torn-read fix
- 🛡️ Stability: D3D `Map`/`Unmap` 全部改为 finally 保护，异常后资源不再永久无法写入 / map/unmap exception safety
- 🖼️ Encoding: TIFF IFD 条目补齐 12 字节、条目计数修正、值区与像素数据不再重叠 / TIFF IFD layout fixes
- 🖼️ Encoding: TIFF 16-bit 路径步长与 Alpha 扩展修复（此前通道错位且近乎全透明）/ TIFF 16-bit stride & alpha
- 🖼️ Encoding: AVIF `mdat` box 长度改大端；`iloc` 回填偏移修正（原少 2 字节）/ AVIF box endianness & iloc fix
- 🖼️ Encoding: MFT drain 命令常量修正（原误用 TICK，导致 MFT 后端 100% 回退 libaom）/ MFT drain command fix
- 🖼️ Encoding: `av1C` 配置记录按 AV1 ISOBMFF 规范重写 / av1C config record rewrite
- 🔒 Security: LLM API Key 与有道 AppSecret 改用 DPAPI 加密存储，不再明文落盘 / credential encryption
- 🔒 Security: WebP 临时文件改用随机名，修复可预测路径的 TOCTOU 风险 / temp file TOCTOU fix
- ⚖️ Compliance: 移除硬编码的有道网页端私有密钥，改走官方开放平台 API（用户自备凭据）/ Youdao open API
- 🎨 Color: 修复后台编码线程读取 WinUI 控件被静默吞异常导致的色域/ICC 设置静默失效 / silent colorspace fallback fix
- 🧪 Test: 新增输出结构合法性套件（30 项），覆盖 TIFF IFD 与 AVIF box 容器结构 / format structure tests
- 🔧 Build: 修复 slnx 编译错误，新增 GitHub Actions CI / build fix & CI
- ⚠️ QSV: AVIF QSV 后端暂停启用（P/Invoke 结构体与 oneVPL 官方定义不符，待重写后验证）/ QSV backend disabled

### v0.3.2-beta — 2026-08-10

- ✏️ Annotate: 预览/输出一致性修复 — Arrow/Pen/Text 预览可见，输出不再画成矩形框 / annotation preview-output consistency
- 🖥️ Overlay: HDR 路径"标注"按钮接通独立标注窗口（原为死按钮丢失截图）/ HDR annotate wired up
- 🛡️ Stability: Alt+F4/系统关闭兜底触发 Cancel，防重入锁不再泄漏 / close fallback fixes
- ✏️ Annotate: 画笔轨迹累积 + 文字输入浮层（原画笔只画直线、文字固定"标注"）/ pen stroke + text input
- ⚡ Performance: 合成移出 UI 线程 + 马赛克并行 / compose off UI thread
- ⚡ Performance: staging 纹理池化、拷贝+遮罩合并遍历、渲染线程 16ms 门控、Present 去 vsync、多屏拼接并行 / preview pipeline optimizations
- 🖼️ Encoding: Gain Map R/B 通道交换修复 — 灰度测试不可见，纯色测试验证 / Gain Map R/B channel swap fix
- 🖼️ Encoding: AVIF CICP matrix 修复 (BT.2020 NCL, matrix=9)，Windows 解码器兼容 / AVIF CICP matrix fix
- 🎨 Color: HDR 编码不再嵌入 ICC 覆盖 JXL color fields，intensity_target 遵循显示器峰值 / ICC override fix
- 🖥️ Overlay: 选区覆盖层 3 分钟超时自动取消 + 应用退出强制关闭覆盖层 / selection overlay timeout
- 🗑️ Repo: 清理调试脚本/测试数据，git 历史瘦身，测试文件统一管理不上传 / repo cleanup

### v0.3.0-beta — 2026-08-01

- 🏗️ Pipeline: 全面管线大修 — 捕获/色调映射/色彩管理/编码 全部重写 / Major pipeline overhaul
- 🎨 Color: 色域转换链路修复 — scRGB→BT.2020/P3/AdobeRGB 矩阵 + 动态 CICP / Color gamut conversion fix
- 🖼️ Encoding: 全线迁移到托管/原生编码器，零 Magick.NET 依赖 / Migrated to managed/native encoders
- ⚡ GPU: NVENC GPU 纹理直通路径 (D3D11→NVENC，跳过 CPU 回读) / GPU texture direct path
- 🔧 JPEG: 全面迁移到 jpegli，Gain Map 加入崩溃隔离+色域转换 / JPEG migration to jpegli
- 🖥️ ACM: ACM 开启时 ICC 烘焙不再禁用，Float16 广色域路径 / ACM+ICC coexistence fix
- 🔤 Font: 界面字体自定义功能，支持任意系统已安装字体 / Custom UI font selection
- 🧪 Test: 全部测试通过（含新增的输出结构合法性套件）/ all tests pass
- 📦 Package: 原生工具链嵌入资源，运行时自动提取 / Native tools embedded as resources

### v0.2.0 — 2026-07-28

- 🏗️ DI: AppServices 重构为 Microsoft.Extensions.DependencyInjection 容器 / Refactored to DI container
- 🧹 Cleanup: ToneMapper.cs 移除死代码 D3D11 占位符，转为纯静态 CPU 算法库 / Removed dead D3D11 placeholder code
- 🐧 Platform: 新增 `Platform/` 抽象层 (ICaptureBackend / IGpuRenderer / IPlatformServices) 为 Linux 迁移预留 / Added platform abstraction for Linux migration
- 📄 Docs: 新增架构设计文档和色彩管线分析
- 🔢 Version: 统一全项目版本引用至 v0.2.0 / Unified version references to v0.2.0
- 📷 Capture: 捕获后端统一为 WGC (Windows.Graphics.Capture)，README 更新 / Unified capture backend to WGC

### v0.1.5 — 2026-07-05

- 🎨 Theme: 完整重写主题系统 — 浅色/深色/OLED/跟随系统四种模式全面可用 / Complete theme system rewrite — Light/Dark/OLED/Follow-system all fully functional
- 🖼️ Window: 默认窗口尺寸增大至 1260×840 / Default window enlarged to 1260×840
- 🏷️ TitleBar: 深色模式标题栏扩展消除顶部白条 / Title bar extended into content to eliminate white bar in dark mode
- 🛠️ Toolbar: 截图预览工具栏深色主题适配（硬编码色 → 主题感知） / Screenshot toolbar dark theme adaptation (hardcoded → theme-aware)
- 🧹 Deps: Magick.NET 14.5.0 → 14.14.0 (消除 500+ 安全漏洞警告) / Magick.NET 14.5.0 → 14.14.0 (eliminated 500+ security vulnerability warnings)
- 🔧 Fix: WinUI 3 `RequestedTheme` 启动崩溃修复 / WinUI 3 `RequestedTheme` startup crash fix
- 🔧 Fix: HotkeyManager 窗口子类化失败保护 / HotkeyManager window subclassing failure protection
- 🔧 Fix: TrayIcon/StartupManager 初始化异常防御 / TrayIcon/StartupManager init exception guarding
- 📐 Layout: OCR 引擎选项移至 AI 翻译面板 / OCR engine option moved to AI translation panel

### v0.1.4 — 2026-07-05

- 🖥️ Window: `ExtendsContentIntoTitleBar` 消除深色模式顶部白条 / Eliminate white bar in dark mode title bar
- 🎨 Theme: App.xaml ThemeDictionaries 标准化 (Default/Light/Dark) / Standardized ThemeDictionaries
- 🖊️ Overlay: SelectionOverlay 工具栏主题感知化 / SelectionOverlay toolbar theme-aware
- 📦 OCR: ONNX DirectML/CPU 引擎诊断与验证 / ONNX DirectML/CPU engine diagnostics & validation

### v0.1.3 — 2026-07-05

- 🔧 DPI: `SetProcessDpiAwarenessContext` 强制 PerMonitorV2 修复窗口模糊
- 🔧 Font: `ThemeDictionaries` 覆盖 WinUI 控件模板字体 / 微软雅黑优先
- 🔧 Close: `AppWindow.Closing` 替代不可取消的 `Window.Closed`
- 🔧 Hotkey: `GCHandle` 防 GC 闪退 + 单次窗口子类化
- 🔧 OCR: 双 Pass 预处理管线（对比度增强 / 放大 / 自适应二值化）
- ✨ Hotkey record button / 快捷键录制按钮 — 按键即录即时生效

### v0.1.2 — 2026-07-04

- 🔤 界面字体可自定义（微软雅黑默认，支持任意系统字体）
- 🚀 开机静默托盘 (`--autostart`)
- 🎨 Gain Map 灰度/RGB 双模 + ICC 检测修正
- 🖥️ AVIF 10-bit / JXL 16-bit / PNG 16-bit 高色深保护

### v0.1.1 — 2026-07-04

- 🖼️ JPEG Gain Map (Ultra HDR) + 文件归档
- 🌐 有道 + Google 翻译多端点降级
- 🖊️ 全屏覆盖层标注 + 剪贴板文件粘贴

---

## License / 许可证

Apache 2.0 · [LICENSE](./LICENSE)

**TrueToneCap** — Every frame deserves to be remembered. / 让每一帧都值得被记住。
