# 微软商店打包：实现方式与可用安装方式分析

> 日期：2026-10-07
> 状态：**✅ 已实现并跑通**（同日）—— 实现清单与验收读数见文末 §8「实现记录」
> 方法：**实测**（真实翻转工程配置、真实构建出 MSIX、真实解包核对内容）+ 官方文档核实。
> 本文的配置结论都有"跑过才知道"的实证支撑，不是照抄文档。

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| **商店接受哪些格式？** | **两种**：MSIX 系（`.msix`/`.msixbundle`/`.msixupload`）**或** 传统 EXE/MSI 安装器 |
| **MSIX 打包能实现吗？** | ✅ **能，已实测跑通** —— 构建出真实 `141.4 MB` 的 `.msix` 包 |
| **需要买代码签名证书吗？** | ❌ **不需要**（走 MSIX 时商店自动用微软证书重签）；✅ **需要**（走 EXE/MSI 时必须 CA 证书） |
| **本仓现成可用吗？** | ❌ 需要新增 **2 处配置**（`AppxManifest` 项 + 打包开关），代码零改动即可出包 |
| **最大的坑？** | 🔴 **4 个原生编码器不会被打进包** —— 实测已复现，必须补一条 `<Content>` 规则 |

**一句话**：MSIX 打包**技术上已经跑通、工具链齐全、免签名**；但当前工程的目录布局会让
**4 个原生 exe 静默漏出包外**（OCR 模型能进、工具进不去），这是实测发现的、必须在正式打包前修掉的问题。

---

## 1. 商店支持的两条路（官方原文核实）

来源：[MSIX 应用包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)、
[MSI/EXE 应用包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

### 1.1 路线 A：MSIX（推荐）

支持的提交格式：`.msix` / `.msixbundle` / `.msixupload` / `.appx` / `.appxbundle` / `.appxupload`

**关键优势（原文）**：

> Your MSIX and AppX packages **don't have to be signed with a certificate rooted in a trusted
> certificate authority** when submitting to the Microsoft Store. The Microsoft Store will
> **automatically re-sign** your MSIX/AppX packages with a Microsoft certificate during the
> publishing process after your app passes certification.

⇒ **不用买代码签名证书**。这解决了一个常识性误解。
（但**侧载/企业分发**仍需自签 —— 那是另一回事。）

其他硬性要求：
- 包体上限：**25 GB**（bundle 与单包各自计算）⇒ 本仓 412 MB 毫无压力
- 块映射哈希：**SHA2-256**
- 文件名必须 **ANSI**
- 版本号第 4 位必须为 **0**（留给商店）

### 1.2 路线 B：EXE / MSI（2021 年 6 月起允许）

⚠️ **要求明显更苛刻**（原文）：

> The binary and **all of its Portable Executable (PE) files** must be digitally signed with a
> code signing certificate that **chains up to a certificate issued by a CA** that is part of the
> Microsoft Trusted Root Program.

对本仓的直接影响：

| 要求 | 本仓情况 |
|---|---|
| **所有 PE 文件**都要 CA 签名 | 🔴 需签 **主 exe + 4 个原生工具 exe**（`avifenc`/`cjxl`/`cjpegli`/`cwebp`）+ 各 DLL —— **这些第三方 exe 目前没有签名** |
| 静默安装（不得显示安装 UI） | 需自建安装器 |
| 独立安装器（非 web 下载器） | 需自建 |
| 自行托管下载 URL 并维护版本 | 需自建 CDN 流程 |
| 商店**不会**替你重签 | 必须自己持有 CA 证书（年费） |

⇒ **结论：对本仓强烈建议走 MSIX。** EXE/MSI 路线意味着要给 4 个第三方原生 exe 都做 CA 签名，
成本与维护量都高得多；而 MSIX 免签名、商店还提供 CDN 托管与自动更新。

---

## 2. MSIX 打包的实现方式（实测结论）

### 2.1 工具链：本机已齐全

| 工具 | 来源 | 实测 |
|---|---|---|
| `makeappx.exe` | Windows SDK `10.0.28000.0\x64` | ✅ 存在 |
| `signtool.exe` | 同上 | ✅ 存在 |
| `makepri.exe` | 同上 | ✅ 存在 |
| MSIX 打包 targets | `Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.251221100（NuGet，已随 WinAppSDK 传递引入） | ✅ 存在 |
| `makeappx`/`signtool` 副本 | 该 NuGet 包内 `bin\10.0.26100.0\x64\` | ✅ 存在 |

⇒ **无需额外安装任何东西**即可打包。

### 2.2 实测：MSIX 配置的**精确**要求

我通过**逐次翻转工程配置 + 真实构建**，把 SDK 的要求逼了出来。

**❌ 尝试 1：只把 `WindowsPackageType` 改成 `MSIX`**

结果：构建**成功（exit 0）但【没有产出任何包】** —— 这是个典型的"假成功"：
配置写了却无人消费，与仓库里那个形同虚设的 `-CoreAot` 开关是同一类陷阱。

**❌ 尝试 2：再加 `EnableMsixTooling=true`**

结果：**失败**，并给出了精确的规则（来自
`Microsoft.Windows.SDK.BuildTools.MSIX.Packaging.targets:412`）：

```xml
<Error Condition="'$(WindowsPackageType)' != 'None' and '@(AppxManifest)'==''"
       Text="Improper project configuration: no AppxManifest is specified,
             but WindowsPackageType is not set to $(WindowsPackageType)."/>
```

同文件里还有三条**反向**约束，合起来构成一个"二选一"的硬规则：

| 条件 | 结论 |
|---|---|
| `WindowsPackageType=None` 且 `PublishAppxPackage=true` | ❌ 报错 |
| `WindowsPackageType=None` 且 `AppxPackage=true` | ❌ 报错 |
| `WindowsPackageType=None` 且有 `AppxManifest` 项 | ❌ 报错 |
| **`WindowsPackageType != None` 且无 `AppxManifest` 项** | ❌ 报错 |

⇒ **必须是"打包配置完整的一套"，不能只改一半。**

**✅ 尝试 3：`WindowsPackageType=MSIX` + `EnableMsixTooling=true` + `<AppxManifest>` 项**

结果：**构建成功**。但 `dotnet build` **仍不产包** —— 还需显式开关。

**✅ 尝试 4：再加 `GenerateAppxPackageOnBuild=true`**

结果：**成功产出真实包**：

```
TrueToneCap.App -> ...\AppPackages\TrueToneCap.App_0.3.7.0_x64_Test\TrueToneCap.App_0.3.7.0_x64.msix
```

**实测包体：141.40 MB**（未启用 R2R/未压缩优化，仅验证流程）

### 2.3 最小可用配置（实测验证过）

```xml
<!-- TrueToneCap.App.csproj -->
<PropertyGroup>
  <WindowsPackageType>MSIX</WindowsPackageType>      <!-- 关键：不是 None -->
  <EnableMsixTooling>true</EnableMsixTooling>        <!-- 关键：否则 targets 不介入 -->
  <AppxPackageSigningEnabled>false</AppxPackageSigningEnabled>  <!-- 商店会重签；本地测试可关 -->
  <GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>  <!-- 关键：否则 build 不产包 -->
</PropertyGroup>

<ItemGroup>
  <AppxManifest Include="Package.appxmanifest" />    <!-- 关键：缺它直接报错 -->
</ItemGroup>
```

外加一个 `Package.appxmanifest`（实测用的最小可用版本，含 `runFullTrust` 受限能力）：

```xml
<Package xmlns=".../foundation/windows10" xmlns:uap=".../uap/windows10"
         xmlns:rescap=".../foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="TrueToneCap" Publisher="CN=<Partner Center 里的名称>"
            Version="0.3.7.0" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>TrueToneCap</DisplayName>
    <PublisherDisplayName>TrueToneCap contributors</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources><Resource Language="zh-CN" /></Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop"
                        MinVersion="10.0.26100.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="TrueToneCap.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="TrueToneCap" Description="..."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
```

> ⚠️ `Identity/@Publisher` 必须与 Partner Center 里"查看应用标识详细信息"给出的值**逐字一致**
> （含大小写、空格），否则上传失败。这是官方文档明确点名的常见失败原因。

### 2.4 自包含 WinAppSDK 与 MSIX 可以共存（官方文档核实）

这一点很关键 —— 本仓现在是 `WindowsAppSDKSelfContained=true`，
直觉上会担心"打包后还能不能自包含"。官方文档明确：

> If your app is **packaged**, then the Windows App SDK dependencies will be
> **included as content inside the MSIX package**.

⇒ **自包含 + MSIX 是受支持的组合**，依赖作为包内容一起分发。

另外，`WindowsAppSdkUndockedRegFreeWinRTInitialize` **只在未打包时默认开启**
（条件：`WindowsAppSDKSelfContained=true` **且** `WindowsPackageType=None`）
⇒ 打成 MSIX 后该机制自动不再需要，**无需手工处理**。

**但这里有一个必须决策的取舍**：

| 模式 | 包体 | 用户机器要求 | 商店适配 |
|---|---|---|---|
| `WindowsAppSDKSelfContained=true`（现状） | 大（含 WinAppSDK 运行时） | 无 | ✅ 可用，但包大 |
| `WindowsAppSDKSelfContained=false`（框架依赖） | 小 | 需 WinAppSDK 运行时 | ✅ **商店会自动按依赖装框架包** |

⇒ **上商店时建议改 `false`**：商店能自动处理框架包依赖，能显著减小下载体积。
但这会**改变分发形态**（绿色包需带运行时），所以应作为**独立决策**，且需要
`Publish.ps1` 支持两种模式。

---

## 3. 🔴 实测发现的坑：4 个原生工具不会进包

这是本文**最有价值的发现**，因为它的表现是"**构建成功、包也能出、但功能是坏的**"。

### 3.1 现象（实测）

我把构建出的 MSIX **解包**后逐个核对内容：

| 资产 | 是否在包里 |
|---|---|
| `PP-OCRv6_medium_det.onnx` | ✅ 在（`data\Models\`）|
| `PP-OCRv6_medium_rec.onnx` | ✅ 在 |
| `ppocrv6_dict.txt` | ✅ 在 |
| **`avifenc.exe`** | ❌ **不在** |
| **`cjxl.exe`** | ❌ **不在** |
| **`cjpegli.exe`** | ❌ **不在** |
| **`cwebp.exe`** | ❌ **不在** |

### 3.2 根因

三条事实叠在一起：

1. **OCR 模型**走 `TrueToneCap.App.csproj:50,56` 的 **`<Content Include="Models\*.onnx">` 规则**
   ⇒ 这是 **MSBuild 声明的项**，MSIX 打包会枚举到并带上。
2. **4 个原生 exe** 的搬运有**两条路径，但都不是 MSBuild 项**：
   - `Publish.ps1:703-716`：脚本手工 `Copy-Item` 到发布目录 `native/`
   - `TrueToneCap.App.csproj:179`：`<Target Name="CopyNativeToolsToDevOutput" AfterTargets="Build">`
     —— 用 `<Copy DestinationFolder="$(OutDir)native">` **拷文件**，
     而不是声明 `<Content>` 项
   ⇒ 两条都是"**文件系统动作**"，**MSIX 打包枚举的是声明的项（items），不是输出目录里的文件**
   ⇒ 打包时完全不知道它们的存在。
3. 运行时要靠 `AppContext.BaseDirectory/native/` 找到它们
   （`NativeLibraryResolver.cs:61`）⇒ 包里没有 = **AVIF / JPEG XL / WebP / JPEG LI 四个格式全部失效**，
   与 `App.csproj:171-174` 注释里记过的那次"裸 `dotnet build` 导致 `native/` 为空、
   四种格式静默走保底"是**同一个故障模式**。

> 💡 值得注意的讽刺之处：`App.csproj:183` 那个 `AfterBuild` 目标**已经有硬断言**
> （`@(TtcNativeTools->Count()) != '4'` 就报错），说明维护者已意识到"native/ 不许静默少拷"。
> 但该断言只守在 **Build** 阶段，**MSIX 打包阶段不在它的覆盖范围内** —— 这正是漏洞所在。

### 3.3 修法

把原生工具变成 **MSBuild 认识的 Content 项**（而不是只靠脚本拷贝），例如在 App 项目里：

```xml
<ItemGroup>
  <Content Include="..\TrueToneCap.Core\Resources\*.exe"
           Link="native\%(Filename)%(Extension)"
           CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

> ⚠️ 注意 `Directory.Build.props:110-118` 有一条**刻意排除 App Release** 的同类规则
> （注释明写："不排除等于在发布包里多存一份 16MB avifenc"）。
> 引入 MSIX 后这个取舍要**重新评估**：MSIX 打包需要 MSBuild 看得见这些文件，
> 而"避免重复"的目标可以靠 `Publish.ps1` 侧的调整来达成，**不能**再用"让 MSBuild 看不见"来实现。

### 3.4 为什么这条必须实测才能发现

- `dotnet build` → ✅ 成功
- `dotnet publish` → ✅ 成功
- MSIX 打包 → ✅ 成功出包
- **只有把包解开逐个核对，才发现少了 4 个文件**

⇒ 这正是"构建绿灯 ≠ 功能可用"的典型场景。**上架前必须做一次解包核对**，不能只看构建退出码。

---

## 4. 可用安装方式对比

| 方式 | 免签名 | 自动更新 | 体积 | 需改造 | 适用 |
|---|---|---|---|---|---|
| **MSIX → 商店** | ✅ 商店重签 | ✅ 商店托管 | 大（自包含） | 中 | 🎯 **推荐** |
| **MSIX → 侧载/企业** | ❌ 需自签证书 | 手动 | 大 | 中 | 内部分发 |
| **EXE/MSI → 商店** | ❌ 必须 CA 签**所有** PE | 自建 | 中 | 大 | 不推荐 |
| **绿色包（现状，zip）** | ✅ 不需 | 自建 | 大 | 无 | ✅ 保持并行分发 |

**建议：MSIX（商店）+ 绿色包（GitHub）双轨并行。**
两者不冲突，但代码里那 3 处"未打包假设"（开机自启的注册表、手工快捷方式、
临时文件）需要**同时兼容两种形态** —— 即运行时判断是否在包内身份下运行。

---

## 5. 落地步骤（按依赖顺序）

| # | 动作 | 依据 | 风险 |
|---|---|---|---|
| 1 | 在 `App.csproj` 加 `native\*.exe` 的 `<Content>` 项 | §3 | 低，但需重新评估 `Directory.Build.props:114` 的排除规则 |
| 2 | 新增 `Package.appxmanifest` + 4 张图标（`Assets/`） | §2.3 | 低 |
| 3 | 加 4 个打包属性（§2.3） | §2.2 实测 | 低 |
| 4 | 决定 `WindowsAppSDKSelfContained` 取值 | §2.4 | 中（影响分发形态） |
| 5 | `Publish.ps1` 增加 MSIX 产出路径（或独立的 `Pack-Msix.ps1`） | — | 中 |
| 6 | 改造 3 处未打包假设（启动项/快捷方式/临时文件） | 前文 §3.3 | 中 |
| 7 | **解包核对**：脚本化"包内必须含 4 exe + 3 模型" | §3.4 | — 应作为常驻门禁 |
| 8 | Partner Center 注册 → 预留名称 → 拿 Publisher DN | — | 流程 |

> 第 7 步**强烈建议做成自动门禁**。本仓已有 `scripts/verify-package.ps1` 那套
> "发布后断言清单与尺寸下限"的成熟做法（`Publish.ps1:780`），
> 把 MSIX 的内容核对接进同一条链即可 —— 否则就是重演 §3 那次"出包成功但缺件"。

---

## 6. 复现方式

```powershell
# 1) 工具链确认（应全部存在，无需安装）
Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.28000.0\x64\makeappx.exe"
Test-Path "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools.msix"

# 2) 复现"只改 WindowsPackageType 不生效"的假成功
#    把 <WindowsPackageType>None</WindowsPackageType> 改成 MSIX，然后 dotnet build
#    => exit 0，但 bin 下没有任何 .msix

# 3) 复现精确报错（要求 AppxManifest 项）
#    在上一步基础上加 <EnableMsixTooling>true</EnableMsixTooling>
#    => error : Improper project configuration: no AppxManifest is specified...

# 4) 跑通并出包（PowerShell 里分号要转义为 %3B）
#    WindowsPackageType=MSIX + EnableMsixTooling=true
#    + <AppxManifest Include="Package.appxmanifest" /> + GenerateAppxPackageOnBuild=true
dotnet build src/TrueToneCap.App/TrueToneCap.App.csproj -c Release -p:Platform=x64
#    => src/TrueToneCap.App/AppPackages/.../*.msix

# 5) 解包核对（发现缺 4 个原生工具的关键步骤）
makeappx unpack /p <产出>.msix /d $env:TEMP\unpacked /o
Get-ChildItem -Recurse $env:TEMP\unpacked -Filter "*.exe"   # => 只有 TrueToneCap.exe
```

**关键文件定位**

| 关注点 | 位置 |
|---|---|
| 打包类型（当前为未打包） | `TrueToneCap.App.csproj:14` |
| 自包含开关 | `TrueToneCap.App.csproj:8,15` |
| **OCR 模型 Content 规则（MSBuild 项，MSIX 能带上）** | `TrueToneCap.App.csproj:50,56` |
| **原生 exe 的 Build-only 拷贝目标（MSIX 看不见）** | `TrueToneCap.App.csproj:179-188` |
| 原生 exe 的脚本拷贝 | `Publish.ps1:703-716` |
| 已有的 4-exe 硬断言（仅覆盖 Build 阶段） | `TrueToneCap.App.csproj:183-184` |
| App Release 刻意排除原生 exe 的规则 | `Directory.Build.props:110-118` |
| 运行时查找路径 `BaseDirectory/native` | `NativeLibraryResolver.cs:61` |
| 发布内容断言（可扩展为 MSIX 核对） | `Publish.ps1:780`、`scripts/verify-package.ps1` |

---

## 7. 待确认

1. **MSIX 沙箱内子进程 + `%TEMP%` 的实际行为** —— 需真实安装后实测
   （这是上架前唯一必须实机验证的技术点，本次只验证到"能出包"）
2. **`WindowsAppSDKSelfContained` 取值决策** —— 影响包体与分发形态，需产品判断
3. **Partner Center 开发者账号类型与费用** —— 个人/公司不同，具体金额以注册页为准
4. **商店对截图/录屏类应用的隐私政策要求** —— 可能需说明屏幕内容处理方式

---

## 8. 实现记录（2026-10-07 同日完成）

### 8.1 改动清单

| 文件 | 改动 | 性质 |
|---|---|---|
| `TrueToneCap.App.csproj` | **🔴 关键修复**：原生 4 exe 从"只有 Copy 动作"改为**声明 `<Content>` 项**（+ `Link="native\..."`）；保留原 Copy 作兜底 | 修复出包缺件 |
| `TrueToneCap.App.csproj` | 新增 `TtcMsixPackage` 开关（默认 `false`），带动 `WindowsPackageType=MSIX` + `EnableMsixTooling` + `AppxManifest` 项 + `GenerateAppxPackageOnBuild` + `AppxPackageSigningEnabled=false` | 打包配置 |
| `TrueToneCap.App.csproj` | 新增 `MsPdbCmfExeFullpath` 探测，让 `.appxsym` 符号包能生成 | 崩溃分析 |
| `Package.appxmanifest` | **新增**：Identity / VisualElements / `runFullTrust` / `startupTask` | 打包必需 |
| `Assets/*.png`（13 个） | **新增**：各尺寸磁贴与启动画面 | 打包必需 |
| `scripts/gen-msix-assets.py` | **新增**：可复现地生成上述资产 | 可复现 |
| `scripts/verify-msix.ps1` | **新增**：解包核对门禁（4 项检查） | 防回归 |
| `Publish.ps1` | 新增 `-Msix` 开关 + `[5.6/6]` MSIX 核对步骤（失败即阻断盖章） | 流程集成 |
| `scripts/verify-package.ps1` | `cjpegli.exe` 尺寸下限 1MB → **256KB** | 修早前升级引入的误拦 |

### 8.2 关键修复：原生工具进包（本任务的核心）

**改动前**（实测）：MSIX 解包后 `native\` 下 **4 枚 exe 全部缺失**，OCR 模型却在。
根因是 MSIX 打包**枚举 MSBuild 声明的项**，而原生 exe 当时只靠 `<Copy>` 动作搬运。

**改动后**（实测）：4 枚全部进包。

```
✅ native\avifenc.exe   15.68 MB
✅ native\cjxl.exe       5.01 MB
✅ native\cjpegli.exe    0.72 MB
✅ native\cwebp.exe      0.71 MB
```

### 8.3 门禁做了变异测试（证明它有牙）

一个"永远通过"的门禁比没有门禁更危险，所以我对 `verify-msix.ps1` 做了**变异测试**：
把修好的 `<Content>` 项临时删掉、重新构建、重跑门禁。

| 阶段 | 结果 |
|---|---|
| 删掉 `<Content>` 项后构建 | **exit 0**（依然"成功"—— 这就是原始的静默故障） |
| 门禁 | **exit 1**，逐条点出 4 枚缺失，并说明后果："对应编码格式在包内会静默走保底" |

⇒ **门禁确实能拦住它要拦的那类缺陷**，不是摆设。

### 8.4 另一个实测教训：属性名写错等于没写

首次尝试让 `.appxsym` 生成时，我按直觉写了 `MspdbcmfPath` —— **打包仍报警告**。
读 targets 原文后才确认正确属性是 **`MsPdbCmfExeFullpath`**（注意 `Fullpath` 不是 `FullPath`），
因为该 target 的进入条件是 `'$(MsPdbCmfExeFullpath)' == ''`：名字不对 = 条件仍成立 = 整段解析照跑。

> 这与仓库里那个形同虚设的 `-CoreAot` 是**同一类错误**：属性名对不上就静默无效。
> 已在 csproj 注释里写死这个教训。

### 8.5 顺带修正：一次合法瘦身把发布拦死了

跑 `Publish.ps1 -Msix` 时冒烟门禁报 **`native\cjpegli.exe — 759808 B，低于下限 1048576 B`**。
这不是新 bug，而是**早前 cjpegli 自编译改造**（5.13 MB → 0.72 MB）之后，
`verify-package.ps1` 里那个 1 MB 下限变成了错的口径。

⇒ 尺寸下限的本意是"挡住截断/空文件"，应当**贴着当前真实体积**取；已改为 256KB 并注明依据。
（这次拦截本身是**门禁正确工作**的表现 —— 它拦住了一次它无法判断对错的体积突变。）

### 8.6 验收读数（本机实测）

| 项 | 结果 |
|---|---|
| 5 项目 Release 构建 | ✅ **0 警告 0 错误** |
| 默认路径（不加 `-Msix`） | ✅ **不产出任何 .msix** —— 未打包流程完全不受影响 |
| 测试套件 | ✅ 8 套件 **1820 通过 / 0 失败** |
| `--selftest` | ✅ 11 项（10 ok + 1 环境性 skip，非本次改动所致） |
| MSIX 构建 | ✅ **150.90 MB** + `.appxsym` 0.24 MB |
| MSIX 内容核对 | ✅ 4 原生工具 + 3 OCR 资产 + 清单身份全通过 |
| **`Publish.ps1 -Msix` 端到端** | ✅ **exit 0**（冒烟 → MSIX 核对 → 盖章全通） |
| 包内文件数 | 565 |

### 8.7 用法

```powershell
# 只出绿色包（默认，行为与改动前一致）
.\Publish.ps1

# 绿色包 + MSIX（商店/侧载）
.\Publish.ps1 -Msix

# 单独出 MSIX（开发期快速迭代）
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true

# 单独核对已有 MSIX
.\scripts\verify-msix.ps1
```

### 8.8 仍未做（明确记录）

1. **`Identity/@Publisher` 仍是占位符** —— 必须等 Partner Center 预留应用名后，
   用其"查看应用标识详细信息"里的值替换（门禁会以 ⚠️ 提示，不阻断本机/侧载）。
2. **`WindowsAppSDKSelfContained` 仍是 `true`** —— 上商店时改成 `false` 可显著减小下载体积
   （商店自动处理框架依赖），但会改变分发形态，**需产品决策**。
3. **3 处"未打包假设"未改造** —— 开机自启（`StartupManager` 写 HKCU\Run）、
   手工快捷方式（`ToastService.EnsureAppShortcut`）、临时文件子进程。
   清单里 `startupTask` **已声明**，但代码侧尚未切到 `StartupTask.GetAsync`；
   打包后开机自启**当前不会生效**。
4. **MSIX 沙箱内运行未实测** —— 本次验证到"包内容正确"，尚未"装上去跑一遍"。
   建议下一步用 `Add-AppDevPackage.ps1`（包内已附带）装一次，确认
   子进程 + `%TEMP%` + 四种编码格式在沙箱内可用。
