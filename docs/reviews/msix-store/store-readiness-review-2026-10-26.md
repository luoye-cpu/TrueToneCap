# 商店就绪复审 —— v0.3.10-beta（2026-10-26）

> 日期：2026-10-26
> 方法：对 2026-10-07 那轮分析（`store-readiness-gap-analysis-2026-10-07.md`、
> `store-submission-guide-2026-10-07.md`）之后的 23 个提交**从零复核**，全部结论来自本轮实跑：
> 重建 MSIX、跑内容门禁、跑 WACK、跑全部 8 个测试套件、核对隐私政策与代码的一致性。

---

## 0. 结论摘要

| 级别 | 数量 | 内容 |
|---|---|---|
| 🔴 阻断提交 | 1 | ① `Publisher` 仍是占位符（需注册账号后填入，**与 10-07 相同，未变**） |
| 🟡 本轮新发现，已修复 | 2 | ② 清单版本漂移；③ `PRIVACY.md` 口径过期（v0.3.10 免费翻译默认启用未反映） |
| 🟡 提交表单必填 | 3 | ④ 商店截图；⑤ 商店描述/年龄分级；⑥ 实机安装验证 |
| 🟢 已就绪（本轮实测） | — | 出包、内容门禁、WACK PASS、测试 8 套件全绿、隐私政策 URL 匿名可达 |

**一句话**：10-07 时说的"代码侧已清空"在 v0.3.10 之后出现了两处**新债**（版本漂移、政策口径过期），
本轮已清掉并固化了判据；剩下的仍是只有账号持有人能做的那几件事。

---

## 1. 🟡 本轮发现并已修复

### ② 清单版本漂移：`Identity/@Version` 不随 `-p:Version` 覆盖

**实测**：`dotnet build -p:TtcMsixPackage=true -p:Version=0.3.10-beta -p:FileVersion=0.3.10.0`
→ 包目录与包内清单仍是 **0.3.7.0**（`Directory.Build.props` 把 `AssemblyVersion` 等钉在
0.3.7.0 的事实无关紧要，MSIX 的 `Identity/@Version` 只读清单文件本身）。

**危害**：商店要求每次提交包版本严格递增；清单不跟版本走，第二次提交会因
"版本不高于已上架版本"被拒，且报错不指向清单。

**修复**：`Package.appxmanifest` 的 `Identity/@Version` 已改 `0.3.10.0`，并在清单头注释里写明
"该值须在每次发版时手动同步"。**每次发版多一步手工动作，属已知取舍**（自动化注入需动 MSIX
targets 的属性管道，收益不值）。

**判据固化**：`scripts/verify-msix.ps1` 新增一条——包身份版本必须与随包 `TrueToneCap.exe`
的 FileVersion 一致（`Publish.ps1` 会把 FileVersion 跟着 `-Version` 走，二者一比对，
漂移即判红）。本轮实测：一致时 ✅。

### ③ `PRIVACY.md` 口径过期：v0.3.10 免费翻译"默认启用"未反映

v0.3.10（提交 b51d598）把 `AllowUnofficialTranslateEndpoints` 默认值翻成 **true**
（`AppSettingsData.cs:317`），并接入 Edge 与 MyMemory——免费公共端点变成
**Edge / 必应 / MyMemory / Google 四条、默认开启、无需任何配置**。
而 `PRIVACY.md` 还写着旧口径（"有道翻译 / Google 翻译公开接口"，暗示需要选择）。

**危害**：政策 10.1「误导」风险——商店页面链接的隐私政策与软件实际行为不符；
审核员实测一点翻译就发现数据外发而政策说"仅你自己配置的端点"。

**修复**（已改，双语同步）：
- §三 表格：免费翻译标注**默认启用**，发送对象改为四家接口（含域名）；新增"如何关闭"
  （设置 → 翻译 取消勾选，取消后连请求都不发、选择永久保留）
- §四 表格：设置/日志落点按**商店版 vs 便携版**双形态写清（`SettingsService.cs:33-39` 的
  打包分支，`%LOCALAPPDATA%\TrueToneCap\`）
- 头部维护注释的代码行号全部重对（854→1180、404→445/447/950、263→304）
- "最后更新"改 2026-10-26

**⚠️ 须随本次发版一起 push**——包内副本与仓库副本已保证逐字一致（sha256 实测相同），
但 Partner Center 填的 URL 指向 GitHub `main`，不 push 政策就不更新。

---

## 2. 🟢 本轮实测证据（v0.3.10.0，框架依赖包 84.6 MB）

| 检查 | 结果 |
|---|---|
| `dotnet build -p:TtcMsixPackage=true -p:TtcFrameworkDependent=true` | ✅ 0 警告 0 错误 |
| `scripts/verify-msix.ps1`（含新判据） | ✅ 全绿：4 原生工具 + 3 OCR 资产 + PRIVACY/NOTICES + 身份一致，124 文件 |
| 包内 `PRIVACY.md` vs 仓库副本 | ✅ sha256 逐字相同（`c98f74ad…`） |
| **WACK** `appcert.exe test -appxpackagepath` | ✅ `OVERALL_RESULT=PASS`，24 项，仅 2 个 OPTIONAL FAIL（与 10-07 相同：Win2D 上游元数据 / 子进程引用，不阻断） |
| 测试 `dotnet run --project src/TrueToneCap.Test -- --all` | ✅ **8 套件全部通过**（核心单元/色彩精度/编码集成/基础设施/OCR/输出结构/综合可用性/WGC+AVIF） |
| 隐私政策 URL `github.com/luoye-cpu/TrueToneCap/blob/main/PRIVACY.md` | ✅ 匿名可访问（HTTP 200）——**注意线上还是 10-07 旧版，push 后才更新** |
| 商店磁贴资产 | ✅ 48 个（10-16 提交 cddc4a9 补齐 scale + altform-unplated 两族） |
| 符号包 | ✅ `.appxsym` 随包产出（Partner Center 崩溃分析用） |

包位置：`src/TrueToneCap.App/AppPackages/TrueToneCap.App_0.3.10.0_x64_Test/`
（`TrueToneCap.App_0.3.10.0_x64.msix` + `.appxsym`）。

---

## 3. 剩余事项（全部是只有你能做的）

| # | 事项 | 说明 |
|---|---|---|
| 1 | **注册开发者账号 → 预留名称 → 拿到 `Publisher`** | 从 <https://storedeveloper.microsoft.com> 进（别处会走旧收费流程）。拿到形如 `CN=xxxx` 的 Publisher 后逐字填回 `Package.appxmanifest`（区分大小写） |
| 2 | **commit + push 本轮改动** | 包含：清单版本修复、`PRIVACY.md` 更新、`verify-msix.ps1` 新判据、本报告。push 后政策 URL 才指向新口径 |
| 3 | **实机安装验证一次**（干净机器最优） | 用包内 `Add-AppDevPackage.ps1` 装一遍，验证：设置持久化（`%LOCALAPPDATA%\TrueToneCap\`）、日志落点、四种编码格式（子进程 + %TEMP%）、WGC 捕获、StartupTask 自启。10-07 起这一环从未做过 |
| 4 | **商店截图** ≥1 张（建议 1366×768+） | 仓库现无任何可用素材（tests/output 只有测试输入图）。建议：全屏覆盖层框选界面 + OCR/翻译结果 + 设置页 |
| 5 | **商店描述 + IARC 年龄分级问卷** | 描述须写明（政策 10.1.1）：翻译需联网、免费翻译默认开启且会把识别文字发给四家公共翻译服务、视觉翻译会把整张截图发给用户配置的 LLM |

> 版本提醒：若下一次发版不是 0.3.10，出包前记得同步 `Package.appxmanifest` 的
> `Identity/@Version`（新门禁会拦，但先改对省一次重建）。

---

## 4. 提交流程（浓缩版；账号注册细节见 `store-submission-guide-2026-10-07.md`）

```
1. 注册账号（storedeveloper.microsoft.com，免费，Individual 不可逆）
2. Partner Center → New product → 预留名称 → 取 Publisher
3. Publisher 填回 Package.appxmanifest → 重建 → verify-msix.ps1 全绿
4. push（含本轮改动）→ 政策 URL 生效
5. 实机装包验证一遍（§3 第 3 条的清单）
6. Start submission：
   Packages        → 传 .msix（.appxsym 可一起上传，换崩溃分析）
   Pricing         → 免费 / 市场选择
   Properties      → 类别 + 隐私政策 URL（github blob 页，勿用 raw）
   Age ratings     → IARC 问卷（工具类、无暴力/赌博内容）
   Store listing   → 描述 + 截图 + 支持邮箱（Individual 账号可免客服信息）
   Notes for certification → 见 §5
7. Submit → 认证 ≤3 个工作日 → 通过后微软自动重签上架
```

---

## 5. `Notes for certification` 建议文案（v0.3.10 更新版）

> 变化点：10-07 版说"翻译需要 API Key，建议讲清不配置仍可测主功能"；
> v0.3.10 之后**免费翻译开箱即用**，认证人员反而**可以直接实测翻译**——这段要改写：

- 截图、标注、OCR 全程本地，无需账号即可测试。
- 翻译功能开箱即用：默认通过 Edge / Bing / MyMemory / Google 四家公共翻译接口翻译
  **识别出的文字**（需联网）。可在 设置 → 翻译 关闭，关闭后不发任何请求。
- 应用也可选配用户自有的 LLM API（视觉翻译会把整张截图发往用户自己填写的端点），
  默认未配置、不影响其他功能。
- 应用会调用随包分发的开源编码器子进程（avifenc / cjxl / cwebp / cjpegli）实现
  AVIF / JPEG XL / WebP / JPEG 导出——WACK 的"已阻止的可执行文件"OPTIONAL 项即源于此，
  属核心设计而非缺陷。
- 需要 Windows 11 24H2（build 26100）+；WACK"一般元数据"OPTIONAL FAIL 源于上游
  Win2D winmd 元数据，与本应用代码无关。
- 无语言包时韩文 OCR 自动走 Windows OCR 兜底（设计行为）。

---

## 6. 商店包是否内置 .NET 11？—— 结论：框架依赖形态不解决"目标设备"问题（2026-10-26 补充）

**问题**：框架依赖商店包（84.6 MB）依赖目标机器上存在 .NET 运行时；商店会不会自动替用户装？

### 7.1 实测证据链（全部本地解包验证）

| 检查 | 结果 |
|---|---|
| 框架依赖包内 .NET 运行时特征文件（hostfxr / coreclr / System.Private.CoreLib） | ❌ **全部不存在** —— 包里没有 .NET 11 |
| 包内 AppxManifest 的 `<PackageDependency>` | 只声明 `Microsoft.WindowsAppRuntime.2` MinVersion 2.5.1.0 —— **没有任何 .NET 运行时依赖** |
| `Microsoft.WindowsAppRuntime.2` 框架包（商店侧自动装的那只，48.7 MB / 解包 117 MB） | ❌ 无 hostfxr，其自身清单也**无**对 .NET 框架包的链式依赖 —— 依赖解析链在此终止 |
| 侧载目录 `Dependencies/`（4 架构） | 只有 `Microsoft.WindowsAppRuntime.2.msix`，无 .NET 运行时包 |
| .NET 运行时是否为 Windows 内置 | ❌ Windows 内置的是 .NET Framework 4.8.1，不含 .NET 11 |
| .NET 运行时是否可作商店框架包被声明 | ❌ **微软从未发布** —— [dotnet/runtime#51574](https://github.com/dotnet/runtime/issues/51574) 开放 5 年+（milestone: Future），官方确认无框架依赖模型 |

**结论**：商店只自动安装**清单里声明了的**框架依赖（本包 = WindowsAppRuntime.2 ✅ 会自动装）；
.NET 11 既不在包里、也不在依赖链上、也不是系统内置、商店也无处可装。
⇒ **干净 Windows 11 24H2 机器上装本包，应用启动即弹 apphost 的"必须安装 .NET"错误框**。
10-07 分析 §8.2 那条"商店会自动装框架包，但本机未验证"的待确认项，就此关闭：**对 .NET 一半不成立**。

### 7.2 修复：提交形态改为自包含（已执行并验证）

| 检查 | 结果 |
|---|---|
| 自包含 MSIX 重建（`TtcMsixPackage=true`，**不传** `-FrameworkDependent`） | ✅ 152 MB（非 R2R 直 build；走 `Publish.ps1 -Msix` 带 R2R 约 176 MB） |
| 包内 .NET 运行时 | ✅ hostfxr / coreclr / System.Private.CoreLib 全部在包内 |
| 包内 WinAppSDK 运行时 | ✅ Microsoft.WindowsAppRuntime.dll / Microsoft.ui.xaml.dll 全部在包内（`WindowsAppSDKSelfContained=true`） |
| 清单框架依赖 | ✅ **零 PackageDependency** —— 包身份完全自洽，商店/用户机器无需任何外部运行时 |
| 内容门禁 `verify-msix.ps1` | ✅ 全绿（601 文件，含全部必需内容） |
| WACK | ✅ 见本报告 §2（对该形态复跑） |

**代价**：包体 84.6 → 152 MB（+67 MB，仍远低于商店 25 GB 上限）。
**收益**：目标设备的确定性 —— 商店认证机与任何用户机器都不再依赖运行时预装。
（AOT 可再省体积，但被上游 WinUI 阻塞，见 `aot-and-store-publishing-analysis-2026-10-07.md`。）

### 7.3 对既有文档的两处更正

1. `store-submission-guide-2026-10-07.md` §4.1"推荐 -FrameworkDependent 减体积"**作废**：
   该建议只对了 WinAppRuntime 一半（声明了依赖、商店确实自动装），.NET 一半当时未验证。
2. 绿色包（7z）不受影响：它本来就是自包含 R2R，无此问题。

**发版执行口径**：商店包用 `.\Publish.ps1 -Msix`（**不带** `-FrameworkDependent`）。

## 7. AOT/裁剪能省多少体积？—— 实测：裁剪 152→114 MB；AOT 不会更小（2026-10-26 补充）

> 方法：解包自包含 MSIX 按类别计重 → 实跑裁剪发布（`-p:PublishTrimmed=true`，压制上游注解
> 警告 IL2026/IL2075/IL2104，仅作**体积测量**，未做运行时验证）→ 由构成推算 AOT 区间。

### 7.1 体积构成（自包含非 R2R，解包 333 MB → MSIX 151 MB）

| 类别 | 解包体积 | AOT/裁剪能否触及 |
|---|---|---|
| OCR 模型（data/Models） | 66.4 MB | ❌ 数据 |
| 原生编码工具（native/ 4 exe） | 22.1 MB | ❌ 原生 |
| onnxruntime + DirectML | 39.9 MB | ❌ 原生 |
| WinAppSDK 原生运行时（CoreMessagingXP/DWriteCore/MRM…） | ~25 MB | ❌ 原生 |
| 资源（pri/xbf/png 等） | ~11 MB | ❌ |
| **BCL（System.\*）** | **64.2 MB** | ✅ 裁剪 64.2→8.1 |
| **.NET 运行时本体（hostfxr/coreclr/clrjit）** | **7.8 MB** | ✅ 仅 AOT 消灭 |
| WinRT 投影（Windows.SDK.NET） | 26.0 MB | ✅ 裁剪 26.0→0.3 |
| 应用+第三方 IL（Vortice/Win2D/MEDI/自有） | ~66 MB | ✅ 部分裁剪 |

**不可省底座 ≈ 165 MB 解包（≈75 MB 压缩后）——数据与原生部分占一半，AOT 一个字节也省不到。**

### 7.2 实测与推算

| 形态 | MSIX 体积 | 说明 |
|---|---|---|
| 自包含（提交口径） | **151 MB** | 门禁/WACK 全绿 |
| 自包含 + R2R（Publish.ps1 默认绿色包） | ~176 MB | 启动换体积 |
| **自包含 + 裁剪（✅ 已实现并回归）** | **114.4 MB（−24%）** | `.\Publish.ps1 -Msix -Trim`，见 §7.3 |
| NativeAOT（推算区间） | ~110–130 MB | ≈ 裁剪持平或略大，**上游阻塞不可得** |

**AOT 的体积收益 ≈ 等效裁剪 + 运行时本体 7.8 MB − native 代码展开膨胀（IL→机器码典型 1.0–1.3×）。**
对本应用两头相抵：AOT 大概率**不比裁剪更小** —— 而裁剪已经落地（见 §7.3），AOT 无剩余收益可拿。

### 7.3 裁剪路径已完整实现（2026-10-26 同日，§7.2 的 114.4 MB 即实现后产物）

上轮测量时暴露的三个问题已全部修掉，裁剪从"测量"升级为"可分发能力"：

| # | 原问题 | 修复 |
|---|---|---|
| 1 | `TrimmerRoots.xml` 的 `Vortice.Direct2D1` 不在 App 依赖闭包 → IL2007 拒绝 | 已摘除并在文件头写明闭包约束；将来闭包新增 Vortice 系包再加回 |
| 2 | 4 处自有代码反射（IL2026/IL2075） | **反射 → 直访**：MftAvifEncode 是 file 作用域类（探测器够不到，这才是当初用反射的原因），在 FormatEncoders.cs 同文件开 `MftHardwareFlag` internal 出口，file 封装保持不变 |
| 3 | BCL rc.2 / WinRT.Runtime / Vortice 上游注解缺口（IL2104） | App.csproj 裁剪条件块带理由放行（非裁剪构建不受影响）；自有代码警告【不压】—— 新增反射会被 TreatWarningsAsErrors 直接判红 |

**配套落地**：
- `Publish.ps1 -Trim` 开关（强制纯 JIT —— R2R 会把裁掉的体积以原生码加回，抵消收益）
- `scripts/probes/trim_launch_probe.ps1`：裁剪/AOT 产物实机启动回归探针（单实例守卫 + 存活/窗口/日志三查）
- `ToastService.EnsureAppShortcut` 按打包形态分支（MSIX 由系统提供快捷方式；10-07 P0② 一并落地）

**回归证据（全部实跑）**：
- 8 个测试套件全部通过（Core 反射重构无回归）
- 启动探针 ×2 全绿（临时裁剪产物 + 官方管线产物）：进程存活、主窗口句柄有效（XAML 在裁剪下正常构建）、日志无 Unhandled/FATAL/XamlParseException
- 官方管线端到端：`Publish.ps1 -Msix -Trim -NoVSCap` 全绿（内容门禁 438 文件、build-info、SHA256SUMS）
- WACK 对裁剪版 MSIX：**OVERALL_RESULT=PASS**（24 项，同样仅 2 个 OPTIONAL FAIL）

**口径**：`AppPackages` 里当前是**裁剪版**（114.4 MB）。提交前如需保守回退，去掉 `-Trim`
重建即回 151 MB 自包含版。裁剪产物上架前的最后一道门仍是**干净实机的完整功能回归**
（本探针只覆盖启动层；OCR/编码/WGC 全链路建议在实机安装后过一遍）。

## 8. 复现命令

```powershell
# 出包（版本由清单 Identity/@Version 决定，发布前先手动同步它）
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 `
  -p:TtcMsixPackage=true -p:TtcFrameworkDependent=true `
  -p:Version=0.3.10-beta -p:AssemblyVersion=0.3.10.0 -p:FileVersion=0.3.10.0

# 内容门禁（含新的"包身份版本 vs exe FileVersion"判据）
.\scripts\verify-msix.ps1

# WACK
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test `
  -appxpackagepath (Get-ChildItem -Recurse src\TrueToneCap.App\AppPackages -Filter *.msix).FullName `
  -reportoutputpath "$env:TEMP\wack.xml"
[xml]$x = Get-Content "$env:TEMP\wack.xml" -Raw
$x.REPORT.GetAttribute('OVERALL_RESULT')    # => PASS（注意是根节点属性）

# 测试
dotnet run --project src\TrueToneCap.Test -c Release -- --all
```
