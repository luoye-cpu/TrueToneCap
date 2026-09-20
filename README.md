# TrueToneCap / 真色截图

> **v0.3.4-beta** · Windows 11 24H2+ · WinUI 3 · .NET 11 · WGC

**TrueToneCap** is a scalpel built for pixels. / **TrueToneCap** 是一把为像素而生的手术刀。

In the gap where HDR displays are becoming ubiquitous yet screenshot tools remain stuck in the SDR era, TrueToneCap takes a different path — capturing the display's **Float16 framebuffer** directly from the DXGI back-end, preserving every nit of luminance, then gracefully tone-mapping it into the visible range via GPU shaders.

在这个 HDR 显示器逐渐普及、但截图工具仍停留在 SDR 时代的间隙里，TrueToneCap 选择了不同的路——从 DXGI 底层直接捕获显示器的 **Float16 浮点帧缓冲**，保留每一尼特的光照信息，再用 GPU 色调映射将其优雅地落入人眼可见的范围。

<details open>
<summary><b>🌐 English</b></summary>

Selection, annotation, OCR, translation — everything happens in real-time on a full-screen overlay after pressing a hotkey, with no pop-ups and no context switches. Screenshots export to PNG (mathematically lossless), JPEG Gain Map (Ultra HDR compatible), AVIF, JPEG XL, and more. Translation goes through Youdao's official open-platform API (bring your own credentials) or a custom LLM (OpenAI/DeepSeek); the credential-free unofficial web endpoints (Bing / Google) stay off until you explicitly opt in.

</details>

<details>
<summary><b>🌐 简体中文</b></summary>

框选、标注、OCR、翻译——所有操作都在按下快捷键后的全屏覆盖层上实时完成，无需弹窗、无需跳转。截图可以导出为 PNG（数学无损）、JPEG Gain Map（兼容 Ultra HDR）、AVIF、JPEG XL 等格式。对于国内用户，翻译走有道官方开放平台（自备凭据，注册赠体验金）或自定义 LLM；免密的非官方网页端点（必应 / Google）默认不启用，需在设置里显式勾选。

</details>

---

## Quick Start / 快速开始

| Step / 步骤 | English | 中文 |
|-------------|---------|------|
| 1 | 获取发布包 `TrueToneCap-v0.3.4-beta-win-x64.7z`（由维护者出包分发） | 获取发布包 `TrueToneCap-v0.3.4-beta-win-x64.7z`（由维护者出包分发） |
| 2 | Extract the archive | 解压压缩包 |
| 3 | Run `TrueToneCap.exe` | 双击运行 `TrueToneCap.exe` |
| 4 | Press `Ctrl+Shift+S` to capture | 按 `Ctrl+Shift+S` 截图 |

> **自行出包**（维护者）：`.\Publish.ps1 -Tag` 出包并打 `v<版本>` 标签，再
> `.\scripts\pack.ps1 -PackageDir publish\TrueToneCap-v<版本>` 压缩。
> **文件名规则**：`TrueToneCap-v<版本>-win-x64.7z`（未安装 7-Zip 时自动退化为同名 `.zip`）。
> 压缩包带同名 `.sha256`：PowerShell 用 `Get-FileHash <文件>`、cmd 用 `certutil -hashfile <文件> SHA256`、
> Git-Bash / Linux 用 `sha256sum -c <文件>.sha256`。
> 解压后包内另有一份逐文件哈希 `SHA256SUMS.txt`（UTF-8 无 BOM / LF / 正斜杠，
> 与 coreutils 惯例一致），在包目录下 `sha256sum -c SHA256SUMS.txt` 即可整包校验；
> 只有末行（指向压缩包本身）要在压缩包的上一级目录才验得到 —— 日常用上面那份 `.sha256` 更省事。

> **System / 系统**: Windows 11 24H2+ · HDR display optional / HDR 显示器可选

---

## Features / 功能

| Feature / 功能 | English | 中文 |
|----------------|---------|------|
| 📷 **Capture** | WGC (Windows.Graphics.Capture), Float16 HDR / BGRA8 SDR, zero-latency pooling | WGC 捕获，Float16 HDR / BGRA8 SDR，池化零延迟 |
| 🎨 **Color** | Per-monitor advanced-color detection, ICC detect & bake, target gamut: follow-display / sRGB / BT.2020, ACM | 逐屏高级色能力探测，ICC 检测与烘焙，可选目标色域：跟随显示器 / sRGB / BT.2020，ACM |
| ✏️ **Annotate** | Rect, Ellipse, Arrow, Pen, Text, Mosaic — full-screen overlay | 矩形、椭圆、箭头、画笔、文字、马赛克 — 全屏覆盖层 |
| 🖼️ **Export** | PNG (lossless HDR) · JPEG Gain Map (Ultra HDR) · JPEG XL · AVIF (NVENC / MFT-QSV / libaom / SVT-AV1) · WebP · JPEG LI · BMP | PNG（无损 HDR）· JPEG Gain Map（Ultra HDR）· JPEG XL · AVIF（NVENC / 系统 MFT(QSV) / libaom / SVT-AV1）· WebP · JPEG LI · BMP |
| 🔤 **OCR** | PP-OCRv6 (ONNX, DirectML/CPU) + Windows OCR fallback · zh/en strongest, ja needs ≥24px, ko unsupported | PP-OCRv6（ONNX，DirectML/CPU）+ Windows OCR 兜底 · 中/英文最准，日文需字高 ≥24px，韩文不支持（转系统 OCR） |
| 🌐 **Translate** | Custom LLM → Youdao official API → (opt-in) Bing / Google web endpoints | 自定义 LLM → 有道官方开放平台 →（需显式勾选）必应 / Google 网页端点 |
| ⌨️ **Hotkeys** | Recordable · tray minimize · autostart | 可录制 · 托盘最小化 · 开机启动 |

---

## Output Formats / 输出格式

| Format | HDR | Bit Depth | Encoder / 编码器 |
|--------|-----|-----------|------------------|
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

### 环境要求 / Prerequisites

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET SDK | `11.0.100-preview.7.26381.103` | 由 `global.json` 固定，含 Windows 桌面负载 |
| Windows SDK | ≥ 10.0.26100.0 | 需要 `fxc.exe` 编译 HLSL 着色器；构建脚本会**枚举已安装的最高版本**，不要写死具体版本号 |
| GPU | 支持 D3D11 的显卡 | 色彩管线与硬件编码必需 |

> ⚠️ **Windows SDK 是硬依赖，不是可选**：编译后的着色器 `*.cso` 被 `.gitignore` 排除、
> **不入库**，全新克隆必须由 `CompileShaders` Target 用 `fxc.exe` 现场编译。
> 没装 Windows SDK 时构建会直接失败（若本机已有 `.cso`，Target 会跳过，构建仍可通过）。
> 另注：编译器是 **fxc（SM5.0）** 而非 dxc —— dxc 产出的 SM6/DXIL 会被 D3D11
> `CreateShader` 以 `E_INVALIDARG` 拒绝，详见 `Shaders/CompileShaders.ps1` 头部。

> 公共 MSBuild 属性（`TargetFramework` / `LangVersion` / `PlatformTarget` 等）统一声明于
> 仓库根目录的 `Directory.Build.props`，各 `.csproj` 不再重复声明。
>
> **依赖版本**统一声明于 `Directory.Packages.props`（NuGet 集中式包管理 / CPM），
> 各 `.csproj` 的 `<PackageReference>` 只写包名、不写 `Version`。升级依赖只需改一处。
>
> **依赖锁定**：已启用 `packages.lock.json`（`RestorePackagesWithLockFile=true`），
> 锁死完整传递依赖图以保证可复现还原。**锁定文件必须提交到版本控制。**
> 升级依赖后重新还原以刷新锁定文件。

### 构建与运行 / Build & Run

```powershell
dotnet restore
dotnet run --project src\TrueToneCap.App -c Release
```

### 测试 / Tests

```powershell
dotnet run --project src\TrueToneCap.Test -- --all   # 全部 8 个套件
dotnet run --project src\TrueToneCap.Test -- --unit-tests   # 单套件示例
```

> ⚠️ **不要用 `dotnet test`。** 测试项目未引用 `Microsoft.NET.Test.Sdk`，
> `dotnet test` 会以**退出码 0 静默执行零个测试**（"假绿灯"）——看似通过，实则什么都没跑。
> 唯一有效入口就是上面的 `dotnet run --project src\TrueToneCap.Test -- <套件参数>`。
> CI 脚本若照常规写 `dotnet test`，会全程绿灯放行而毫无质量保障。
>
> 另注：测试运行器**一次只接受一个套件参数**（命中首个即返回），
> 需要跑多个套件时必须逐个调用，或直接用 `--all`。

| 套件 | 参数 | 需要桌面会话 |
|------|------|:---:|
| 核心单元（PixelOps / ToneMapper / ICC / 标注 / 编码器） | `--unit-tests` | |
| 色彩管线精度 | `--color-tests` | |
| 编码管线集成 | `--encoding-tests` | |
| 基础设施服务（ShaderLoader / NativeLibraryResolver） | `--service-tests` | |
| OCR 服务（BitmapPreprocessor / LLM 提供商与模型清单闭合） | `--ocr-tests` | |
| 输出结构合法性（TIFF IFD / AVIF box） | `--format-tests` | |
| 综合可用性 | `--usability-tests` | ✓ |
| WGC 捕获 + AVIF 后端验证 | `--wgc-avif-tests` | ✓ |

`--all` 会运行全部 8 个套件；其中 WGC 套件因需独占 STA 线程与 GPU 资源，
以独立子进程方式执行，避免与其它套件相互干扰。

### 自动拦截（git 钩子 / CI）

校验脚本本身不会自己跑起来 —— 需要挂到 git 上：

```powershell
.\scripts\install-git-hooks.ps1            # 安装（写入 core.hooksPath）
.\scripts\install-git-hooks.ps1 -Status    # 只看当前状态
.\scripts\install-git-hooks.ps1 -Uninstall # 摘掉
```

| 钩子 | 做什么 | 耗时 |
|------|--------|------|
| `pre-commit` | 只构建，挡"编译不过" | 约 27s |
| `pre-push` | 构建 + 发布门禁回归 + 7 个门禁套件，挡"测不过" | 约 110s |

分开放是有意的：把 110s 压在每次 commit 上，实际结果是人人都加 `--no-verify`，
钩子整体失效反而更糟。临时跳过用 `git commit --no-verify` / `git push --no-verify`。

钩子本体在 `scripts/git-hooks/`（入库、可 review），靠 `core.hooksPath` 挂载，
因此**新克隆后需要跑一次安装脚本**；不往 `.git/hooks` 里复制文件。

CI 见 `.github/workflows/ci.yml`，默认面向 self-hosted Windows 运行器
（原因写在文件头：pinned .NET 11 preview SDK、Windows SDK 26100、fxc 缺一不可）。

### 发布 / Publish

```powershell
.\scripts\verify.ps1                   # ① 一键校验（日常提交前用）
.\scripts\verify.ps1 -IncludeDesktop   #    追加需桌面会话的 --wgc-avif-tests
.\scripts\verify.ps1 -BuildOnly        #    只构建不跑测试（约 27s）
.\scripts\verify.ps1 -SkipPublishGates #    跳过 [1.6/2] 发布门禁回归
```

`verify.ps1` 的步骤依次为：

| 步骤 | 内容 | 备注 |
|------|------|------|
| `[1/2]` | 构建解决方案 | 失败即退出 |
| `[1.5/2]` | 代码格式检查 | 需 `-Format`；仅报告不阻断 |
| `[1.6/2]` | 发布门禁回归 | 调 `verify-publish-gates.ps1`；**观察期只告警不阻断** |
| `[2/2]` | 7 个门禁套件 | 失败即退出 |

`[1.6/2]` 会以**子进程**隔离执行（`verify-publish-gates.ps1` 给 `git`/`dotnet` 打桩，
同会话执行会污染后续构建）；宿主路径取当前 PowerShell 自身，不写死 `pwsh`。

.\Publish.ps1                          # ② 版本号自动取自 Directory.Build.props
.\Publish.ps1 -Version v0.3.4-beta     # 显式指定版本
.\Publish.ps1 -NoReadyToRun            # 禁用 ReadyToRun（体积更小）
.\PublishLogViewer.ps1                 # 单独发布日志查看器（可选工具）

.\scripts\pack.ps1 -PackageDir publish\TrueToneCap-v0.3.4-beta   # ③ 压缩 + 压缩包哈希入库
```

发布流程包含依赖完整性校验（缺失即中止，不会产出残缺包）、
**原生工具链 SHA256 校验**（`src/TrueToneCap.Core/Resources/tools-manifest.json`，
版本写入 `build-info.json`）、以及 **[5.5/6] 出包冒烟验证**（主程序 / 模型 / 着色器 /
native 工具清单与可启动性，外加"二进制内嵌 commit 是否等于源码 HEAD"），
并在输出目录生成 `SHA256SUMS.txt`（完整性与来源可校验）与
`build-info.json`（含 commit SHA、SDK 版本、Windows SDK 版本、构建时间，便于追溯）。

冒烟不通过则不生成校验和与元数据 —— 不合格的产物不会被盖章。
冒烟也可对任意已有产物单独执行：

```powershell
.\scripts\verify-package.ps1 -PackageDir publish\TrueToneCap-v0.3.4-beta -ExpectedVersion 0.3.4-beta
```

> ⚠️ 发布前请退出正在运行的 TrueToneCap。如确需强制终止进程请加 `-Force`
> （未保存的截图/标注工作将丢失）。

---

## Release Notes / 版本说明

> 这一节只写**新增能力**与**工程规范**。缺陷的根因、判据设计、被实测推翻的旧结论都不在这里 ——
> 面向用户的变更流水见 [CHANGELOG.md](./CHANGELOG.md)；09-15 之后的逐条判据与实证过程见
> `docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md`（维护者文档）；更早版本（v0.3.3 及以前）的
> NVENC 结构体偏移、GUID 实测值、容器布局等细节，**权威出处是代码自身的注释**
> （`NvEncoderNative.cs` 的偏移表与 GUID 段、`ManagedTiffEncoder.cs`、`MftEncoderNative.cs` 等），
> 完整叙述另见 `docs/pipeline-review-*.md` 与 git 历史。

### v0.3.4-beta

**新增能力**

- **源精度由显示器能力决定**：HDR 模式 / ≥10-bit 面板 / ACM 开启时直接取显示器的 Float16 scRGB 帧；
  即便导出 8-bit 目标，内部也走完整浮点链、只在末端量化一次。
- **逐显示器高级色能力**：按屏探测 `SupportsAdvancedColor`（FP16 / 色域 / 峰值与最小亮度），
  HDR 与 SDR 共用同一条色彩处理链路。
- **可选目标色域**：跟随显示器 / sRGB / BT.2020；广色域目标在编码前完成色域矩阵搬运，
  并按目标色域给出配套声明（不是只改标签）。
- **八种输出格式**：PNG（数学无损，可带 cICP/iCCP）· JPEG Gain Map（Ultra HDR 兼容）· JPEG XL ·
  AVIF · WebP · JPEG LI · TIFF · BMP；HDR 通路 10-bit/16-bit 容器与位深下拉一致生效。
- **硬件与软件后端同一套色彩声明**：AVIF 支持 NVENC 与系统 MFT（QSV 候选）硬件通路，
  软件侧 libaom / SVT-AV1；同一次截图在两条路上给出相同的色域矩阵、摆幅与元数据。
- **OCR 与翻译**：PP-OCRv6（ONNX）+ Windows OCR（DirectML 不可用时降 CPU），
  选中 ONNX 时以下四种情况都会改用 Windows OCR 兜底：结果为空、结果的字符域与请求语言不符
  （韩文整页被误认成汉字即属此类）、主引擎超时、主引擎抛异常 —— 兜底自带一份独立的时间预算，
  不与主引擎共用那份已经过期的；兜底成功会在结果上标 `FallbackFrom` 并落一条 Warn 级日志。
  单模型统一字典不随语言切换：中文/英文最准，日文需字高 ≥24px，韩文实测 0%（字典不含谚文）。
  长行拆分与置信度回退；翻译默认只走 **有道官方开放平台 API**（用户自备凭据；注册赠 50 元体验金，
  常见语种 48 元/百万字符 ≈ 104 万字符）与自定义 LLM（OpenAI / DeepSeek 兼容端点）。
  2026-09-21 实测（取证脚本 `scripts/probes/youdao_free_endpoint_probe.py`、`baidu_free_endpoint_probe.py`）：
  有道与百度都有免密的**查询**口，但只到词与短语级 —— 查到的还是有网页例句节选（带 `<b>` 标签与省略号），
  整句／中文句／韩文句／菜单串一律零载荷；官方 API 缺 AppID 即拒（101），网页版翻译要么要求前端私有签名
  （有道 50）要么给一个需要执行 JS 的应用壳（百度）⇒ **不存在免密的整句翻译通道**，本程序不移植那套签名逻辑。
  真正的免密整句只有非官方一路，且国内实测**只有必应网页版可用**（抓 `cn.bing.com` 的页面凭据 →
  `ttranslatev3`，1.1–2.4 秒/句；凭据约 1 小时过期、页面上线改版即断，所以它没有资格当默认路径）
  ⇒ 这类端点默认不启用，需在设置里显式勾选后才发送原文；勾选框现在叫
  「允许回退到非官方翻译端点（必应网页版 / Google）」。
  翻译失败时状态栏会给出「哪条链、什么原因」（如 `有道 errorCode 108：AppID 不存在或输错`），不再只说"失败 N 行"。


**工程规范**

- **可复现构建**：`global.json` 钉住 .NET SDK；依赖版本集中在 `Directory.Packages.props`（CPM）
  并强制提交 `packages.lock.json`；公共属性集中在 `Directory.Build.props`，版本号只写一处。
- **质量门禁**：`scripts/verify.ps1` 一次跑完 8 个套件 + 12 条外部判据（`scripts/precision/` 与
  `scripts/probes/`）；判据一律用**外部实现读回**
  （ffprobe / ffmpeg / Pillow / avifdec / 浏览器），不用仓库自己的第二份解析器自证。
- **交付物可校验**：包内 `SHA256SUMS.txt`（含原生工具链 SHA256 清单）与 `build-info.json`
  （commit SHA、SDK 与 Windows SDK 版本、构建时间、工作树是否干净）；出包前强制冒烟，
  不合格不盖章。
- **版本锚点**：**一条提交 = 一个版本**，发布时打附注标签（`v0.3.4-beta`），
  使"用户装的版本"能对应到仓库中的不可变坐标。
- **自动拦截**：`scripts/git-hooks/`（入库、可 review）经 `core.hooksPath` 挂载 ——
  `pre-commit` 挡编译不过、`pre-push` 挡测试不过；CI 只调用同一个 `verify.ps1`，不复制门禁逻辑。
- **凭据与外发**：LLM Key / 有道 AppSecret 用 DPAPI 加密存储，明文不落盘；非官方翻译端点默认关闭，
  截图原文只在用户明确同意后才发给第三方。日志设三档（设置 → 行为 → 日志详细度），
  **任何一档都会先刮掉 URL 参数值与凭据赋值**（`q=` / `key=` / `token=` 之类）再落盘；
  它刮的是"参数位上的值"，调用方把敏感内容拼成正文它认不出来 —— 2026-09-21 迁移那 142 处遗留调试输出时逐条把关（交接 §35.1）。
- **第三方许可随包分发**：`licenses/` 收录每个再分发组件的许可全文，`THIRD-PARTY-NOTICES.md`
  给出「组件 → 版本 → 许可 → 义务」映射；出包冒烟缺文本即变红，应用内亦可直接查看该清单。

### 更早版本（新增能力摘要）

| 版本 | 新增能力 |
|------|----------|
| v0.3.3-beta | AVIF 硬件编码首次接通（4K 约 0.12s，对比软件编码约 22s）；托管编码器接管 PNG / TIFF / AVIF 容器；独立标注窗口上线；覆盖层超时与剪贴板文件粘贴 |
| v0.3.2-beta | 画笔轨迹与文字输入浮层；合成移出 UI 线程 + 纹理池化（预览管线提速）；Gain Map 双模（灰度 / RGB） |
| v0.3.0-beta | 捕获 / 色调映射 / 色彩管理 / 编码全链路重写；零 Magick.NET 依赖；NVENC GPU 纹理直通（跳过 CPU 回读）；原生工具链作为资源内嵌、运行时自动提取 |
| v0.2.0 | `AppServices` 迁移到 Microsoft.Extensions.DependencyInjection；新增 `Platform/` 抽象层（为跨平台预留）；架构与色彩管线文档 |
| v0.1.x | 四模式主题系统（浅色 / 深色 / OLED / 跟随系统）；Per-Monitor V2 DPI；界面字体可自定义；JPEG Gain Map（Ultra HDR）与高色深容器保护；多端点翻译降级；快捷键录制 |

---

## License / 许可证

Apache 2.0 · [LICENSE](./LICENSE) · Copyright 2026 TrueToneCap contributors

再分发的第三方组件（原生编码器、.NET 运行时、WindowsAppSDK、DirectML、ONNX Runtime、OCR 模型权重）
各自的许可全文见 `licenses/`，汇总与义务说明见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。

> **AV1 提示**：AVIF 编码用到的 libaom / SVT-AV1 均为 BSD 系许可，其原文**明确不授予任何专利权利**
> （SVT-AV1 许可第 2 条）。AV1 的专利授权需另行向 Alliance for Open Media 专利池确认，
> 本项目的 Apache 2.0 许可证不包含该授权。

**TrueToneCap** — Every frame deserves to be remembered. / 让每一帧都值得被记住。
