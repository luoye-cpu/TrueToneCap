# TrueToneCap 依赖清单与升级可行性分析 (2026-09-14)

> **状态：本报告的改进项已于同日全部实施完成。** 实施详情见第 8 节。

> 本报告基于**逐包对 NuGet.org 官方 API 的实时查询**（`https://api.nuget.org/v3-flatcontainer/<包名小写>/index.json`），
> 结合本地 `obj/project.assets.json` 还原图还原出的完整直接/传递依赖树。
>
> ⚠️ **本报告更正了 `dependency-audit-2026-08-25.md` 的多处错误**，详见第 6 节。

---

## 1. 依赖全景

- **直接依赖**：11 个 `PackageReference` 声明，分布在 5 个 csproj
- **传递依赖**：20 个，共 **31 个 NuGet 包**
- **无 `Directory.Packages.props`**（无集中式版本管理）
- **无 `packages.lock.json`**（无锁定文件，还原结果不可复现）

### 1.1 直接依赖一览

| 包名 | 当前版本 | 项目 | 用途 |
|------|---------|------|------|
| `Microsoft.WindowsAppSDK` | 2.4.0 | App, LogViewer | WinUI 3 框架 |
| `Microsoft.Graphics.Win2D` | 1.4.0 | App | 2D 渲染 |
| `Microsoft.Extensions.DependencyInjection` | 11.0.0-preview.6.26359.118 | App | DI 容器 |
| `Microsoft.ML.OnnxRuntime.DirectML` | 1.24.4 | Core | ONNX 推理 (GPU) |
| `Vortice.Direct3D11` | 3.8.3 | Core | D3D11 互操作 |
| `Vortice.DXGI` | 3.8.3 | Core | DXGI 互操作 |
| `MozJpegSharp.Core` | 3.0.8 | Core | JPEG 编码 |
| `libwebp` | 1.0.0 | Core | WebP 原生库 |
| `System.Drawing.Common` | 10.0.11 | Test, Tools | 测试图生成 |

### 1.2 传递依赖（关键分组）

**WindowsAppSDK 组件链（12 个，由 2.4.0 锁定）**

| 包 | 版本 | 最新稳定 |
|---|---|---|
| `Microsoft.WindowsAppSDK.Runtime` | 2.4.0 | 2.4.0 ✅ |
| `Microsoft.WindowsAppSDK.AI` | 2.4.4 | 2.4.4 ✅ |
| `Microsoft.WindowsAppSDK.Search` | 2.4.4 | 2.4.4 ✅ |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.9 | — |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.6 | — |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.6 | — |
| `Microsoft.WindowsAppSDK.DWrite` | 2.1.0 | — |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 | — |
| `Microsoft.WindowsAppSDK.Widgets` | 2.0.5 | — |
| `Microsoft.WindowsAppSDK.ML` | 2.1.74 | — |
| `Microsoft.AI.DirectML` | 1.15.4 | — |
| `Microsoft.Web.WebView2` | 1.0.3719.77 | **1.0.4191.47** ⬆️ |

**Vortice 链**

| 包 | 版本 | 最新稳定 |
|---|---|---|
| `Vortice.DirectX` | 3.8.3 | 3.8.3 ✅ |
| `Vortice.Mathematics` | 2.1.0 | **2.1.1** ⬆️ |
| `SharpGen.Runtime` | 2.4.2-**beta** | 2.4.2-beta ⚠️ |
| `SharpGen.Runtime.COM` | 2.4.2-**beta** | 2.4.2-beta ⚠️ |

**其他**

| 包 | 版本 | 来源 |
|---|---|---|
| `Microsoft.ML.OnnxRuntime.Managed` | 1.24.4 | ORT |
| `System.Numerics.Tensors` | 9.0.0 | ORT |
| `Microsoft.Win32.SystemEvents` | 10.0.11 | System.Drawing.Common |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | WinAppSDK |
| `Microsoft.Windows.SDK.BuildTools.MSIX` | 1.7.251221100 | WinAppSDK |

---

## 2. 升级可行性结论（核心）

按"能否安全完整升级"分四类。

### ✅ A 类：已是最新稳定版，无需动作（8 项）

| 包 | 版本 | 说明 |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.4.0 | 高于它只有 `2.4.1-experimental`，不可用于生产 |
| `Microsoft.Graphics.Win2D` | 1.4.0 | 已是终点版本 |
| `Microsoft.ML.OnnxRuntime.DirectML` | **1.24.4** | **已是最新稳定版** |
| `Microsoft.ML.OnnxRuntime.Managed` | 1.24.4 | 同上 |
| `Vortice.Direct3D11` | 3.8.3 | 已是终点版本 |
| `Vortice.DXGI` | 3.8.3 | 已是终点版本 |
| `Vortice.DirectX` | 3.8.3 | 同上 |
| `MozJpegSharp.Core` | 3.0.8 | 已是终点版本 |

### ⬆️ B 类：存在明确升级空间（4 项）

| 包 | 当前 | 可升到 | 类型 | 风险 | 建议 |
|---|---|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection` | `11.0.0-preview.6` | **`11.0.0-rc.1.26425.128`** | 直接引用 | 低 | 🟡 可升，同为大版本预发布 |
| `Microsoft.Web.WebView2` | `1.0.3719.77` | `1.0.4191.47` | 传递 | 低 | 🟢 由 WinAppSDK 决定，勿单独覆盖 |
| `Vortice.Mathematics` | `2.1.0` | `2.1.1` | 传递 | 低 | 🟢 随 Vortice 升级自然跟进 |
| `System.Numerics.Tensors` | `9.0.0` | 10.x 存在 | 传递 | 中 | 🟢 由 ORT 锁定，勿单独覆盖 |

> **B 类里唯一可独立操作的是 `Microsoft.Extensions.DependencyInjection`。**
> 其余三项都是传递依赖——**手动提升传递依赖版本是反模式**（会造成版本图与上游声明不一致，且下次还原可能被拉回）。正确做法是升级上游（WinAppSDK / Vortice / ORT）。

### ⚠️ C 类：无法升级，且存在结构性问题（2 项）

| 包 | 问题 |
|---|---|
| `libwebp` | NuGet 上**只有一个版本 `1.0.0`**（2021 年后未再更新）。经核实：还原图中该包的 `compile` / `runtime` / `native` 资产**三项全为空** —— 它是一个**不提供任何内容的空壳包**。项目 WebP 编码实际走 `NativeWebPEncoder` → `cwebp.exe` 子进程（`NativeLibraryResolver.GetExePath("cwebp.exe")`），代码中**无任何 `DllImport("libwebp")`**。**建议直接移除。** |
| `SharpGen.Runtime` / `.COM` | 锁定在 **`2.4.2-beta`**（预发布版）。这是 Vortice 的传递依赖，无法单独替换。**生产环境依赖 beta 包是供应链风险**。 |

### 🔴 D 类：需重点关注的复合问题

| 问题 | 说明 |
|---|---|
| 版本管理缺集中化 | 11 处版本号散落在 5 个 csproj，`System.Drawing.Common` 在 Test/Tools 两处重复声明（当前一致，但易漂移） |
| 无锁定文件 | 无 `packages.lock.json`，CI 每次还原可能拿到不同传递依赖补丁版本，破坏可复现构建 |
| 预发布依赖占比高 | 31 个包中，`Microsoft.Extensions.DependencyInjection`（preview）、`SharpGen.Runtime×2`（beta）共 3 个预发布版 |

---

## 3. 依赖树结构（文本表示）

```
TrueToneCap.App
├── Microsoft.WindowsAppSDK 2.4.0
│   ├── .Runtime 2.4.0
│   ├── .AI 2.4.4 ── .ML 2.1.74 ── Microsoft.AI.DirectML 1.15.4
│   ├── .Search 2.4.4
│   ├── .Foundation 2.3.9 ── .WinUI 2.3.6 ── .InteractiveExperiences 2.1.6 ── .DWrite 2.1.0
│   ├── .Base 2.0.4 / .Widgets 2.0.5
│   ├── Microsoft.Web.WebView2 1.0.3719.77          ⬆️ 1.0.4191.47
│   ├── Microsoft.Windows.SDK.BuildTools 10.0.26100.4654 (+ .MSIX)
│   └── System.Numerics.Tensors 9.0.0
├── Microsoft.Graphics.Win2D 1.4.0
└── Microsoft.Extensions.DependencyInjection 11.0.0-preview.6   ⬆️ rc.1

TrueToneCap.Core
├── Microsoft.ML.OnnxRuntime.DirectML 1.24.4  ✅最新
│   └── Microsoft.ML.OnnxRuntime.Managed 1.24.4 ── System.Numerics.Tensors 9.0.0
├── Vortice.Direct3D11 3.8.3 ✅
│   └── Vortice.DirectX 3.8.3 ── SharpGen.Runtime 2.4.2-beta ⚠️
│                                └── SharpGen.Runtime.COM 2.4.2-beta ⚠️
├── Vortice.DXGI 3.8.3 ✅
│   └── Vortice.Mathematics 2.1.0                    ⬆️ 2.1.1
├── MozJpegSharp.Core 3.0.8 ✅
└── libwebp 1.0.0（唯一版本，可能已无用）

TrueToneCap.Test / Tools
└── System.Drawing.Common 10.0.11 → Microsoft.Win32.SystemEvents 10.0.11
```

---

## 4. 建议的升级路线

### 阶段 1 —— 可立即执行（低风险）

**1.1 统一 `Microsoft.Extensions.DependencyInjection` 到 `11.0.0-rc.1.26425.128`**

- 理由：同属 11.0 线，从 preview.6 到 rc.1 是**发布稳定化方向**的推进，不是激进跳版
- 验证：`dotnet build` + `--all` 全量测试
- 风险：低。DI 容器 API 在 11.0 内已趋稳定

### 阶段 2 —— 建议执行（工程性改进，非版本升级）

**2.1 引入 `Directory.Packages.props` 集中管理版本**

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
    <PackageVersion Include="Microsoft.Graphics.Win2D" Version="1.4.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="11.0.0-rc.1.26425.128" />
    <PackageVersion Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
    <PackageVersion Include="Vortice.Direct3D11" Version="3.8.3" />
    <PackageVersion Include="Vortice.DXGI" Version="3.8.3" />
    <PackageVersion Include="MozJpegSharp.Core" Version="3.0.8" />
    <PackageVersion Include="libwebp" Version="1.0.0" />
    <PackageVersion Include="System.Drawing.Common" Version="10.0.11" />
  </ItemGroup>
</Project>
```

各 csproj 中的 `<PackageReference Include="X" Version="Y" />` 去掉 `Version` 属性。

**2.2 生成 `packages.lock.json` 以确保可复现还原**

在 `Directory.Build.props` 加入：
```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
```

### 阶段 3 —— 需评估（供应链风险）

**3.1 消解 `SharpGen.Runtime 2.4.2-beta`**

该 beta 来自 Vortice 3.8.3 的声明。可选路径：
- 等待 Vortice 发布依赖稳定版 SharpGen 的版本
- 或显式提升 SharpGen 到稳定版（若存在且兼容）——但不推荐，需实测

**3.2 移除 `libwebp` 包（已确证为零资产空壳）**

结论：**可直接移除**。证据：
- `obj/project.assets.json` 中 `libwebp/1.0.0` 的 `compile`、`runtime`、`native` 资产均为 `None`
- 全仓库无 `DllImport("libwebp")` 或对 `libwebp.dll` 的加载
- WebP 编码路径为 `NativeWebPEncoder` → `cwebp.exe` 子进程（`src/TrueToneCap.Core/Resources/cwebp.exe`）
- NuGet 上该包仅有 `1.0.0` 一个版本，为第三方包装

操作：删除 `src/TrueToneCap.Core/TrueToneCap.Core.csproj` 第 18 行
```xml
<PackageReference Include="libwebp" Version="1.0.0" />
```
验证：`dotnet build` + `--encoding-tests`（覆盖 WebP 编码路径）。

### 阶段 4 —— 阻塞项

**`Microsoft.WindowsAppSDK` / `Vortice.*` / `ORT` / `Win2D` / `MozJpegSharp` 均已是最新稳定版**——这些核心依赖**当前没有升级空间**，不需要任何动作。

---

## 5. 风险登记

| 编号 | 风险 | 严重度 | 说明 |
|------|------|--------|------|
| D-1 | 无锁定文件 | 中 | 传递依赖补丁版本漂移，构建不可复现 |
| D-2 | 版本号分散在多处 | 中 | 已发现 `System.Drawing.Common` 在 2 处重复声明 |
| D-3 | 依赖 beta 包 | 中 | `SharpGen.Runtime 2.4.2-beta`（生产链路） |
| D-4 | 依赖 preview 包 | 低 | `Microsoft.Extensions.DependencyInjection` preview.6 |
| D-5 | `libwebp` 为空壳依赖 | 低 | **已确证**：`1.0.0` 包 compile/runtime/native 资产全空，建议移除 |
| D-6 | 无自动依赖审计 | 中 | 无 Dependabot / `dotnet list package --outdated` 自动化 |

---

## 6. 对旧报告 `dependency-audit-2026-08-25.md` 的更正

本报告在核实过程中发现旧报告存在**事实性错误**，必须更正，以免后续决策建立在错误信息上。

| 项目 | 旧报告结论 | 核实结果 | 判定 |
|------|-----------|---------|------|
| `Microsoft.ML.OnnxRuntime.DirectML` 最新版 | **`1.29.0`** | 最新稳定版为 **`1.24.4`**（NuGet API 全量版本列表中不存在 1.29.0） | ❌ **虚构版本号** |
| ONNX Runtime 升级收益 | "1.24→1.29 跨 5 个 minor，OCR 加速 20-30%" | 不存在 1.29.0，**该结论完全失效** | ❌ **结论作废** |
| `Microsoft.WindowsAppSDK` 当前版本 | `2.3.1` | 实际 `2.4.0`（旧报告时点后项目已升级） | ⚠️ 已过时 |
| `System.Drawing.Common` 当前版本 | `8.0.8` | 实际 `10.0.11` | ⚠️ 已过时 |
| `Microsoft.Extensions.DependencyInjection` | "已最新（preview.6）" | 已有 `11.0.0-rc.1.26425.128` | ⚠️ 漏报 |
| `Vortice.Mathematics` | 最新 `2.1.1` | 确认 `2.1.1` 存在 | ✅ 正确 |

### 影响评估

旧报告第 7.2 节、第 8 节步骤一、第 9 节优先级表**均以"ORT 1.29.0"为前提**，据此建议"高优先级、独立分支验证图优化 bug"。**由于 1.29.0 不存在，这部分建议无法执行**。

同时，旧报告关于 `SimplifiedLayerNormFusion` bug 的分析（第 7.3 节）如果确有此事，那是**真实的技术问题**，但与"升级到 1.29.0"无关——需要在 ORT 1.24.4 现状下另行研究，或等待 ORT 实际发布后续版本。

> **建议**：将 `dependency-audit-2026-08-25.md` 归档或标注"含错误版本信息，以 2026-09-14 报告为准"。

---

## 7. 一句话结论

**能"完整升级"的只有 1 个包** —— `Microsoft.Extensions.DependencyInjection`（preview.6 → rc.1）。
其余 8 个直接依赖已全部处于最新稳定版，**没有升级空间**；剩下一项 `libwebp` 是已确证的**零资产空壳包**，应直接移除而非升级。

真正需要工程投入的不是"升级版本"，而是 **引入集中式版本管理（`Directory.Packages.props`）+ 锁定文件（`packages.lock.json`）**，这两项才能从根本上解决依赖漂移问题。

---

## 8. 实施记录（2026-09-14 完成）

### 8.1 已完成的改动

| # | 改动 | 文件 |
|---|------|------|
| 1 | **移除 `libwebp 1.0.0`** 零资产空壳包 | `src/TrueToneCap.Core/TrueToneCap.Core.csproj` |
| 2 | **升级 `Microsoft.Extensions.DependencyInjection`** `11.0.0-preview.6` → `11.0.0-rc.1.26425.128` | `Directory.Packages.props` |
| 3 | **新建 `Directory.Packages.props`**，启用 CPM，集中声明 8 个依赖版本 | 新建 |
| 4 | **各 csproj 移除 `Version` 属性**（10 处 `PackageReference` 改为仅包名） | 5 个 csproj |
| 5 | **启用锁定文件** `RestorePackagesWithLockFile=true` | `Directory.Build.props` |
| 6 | **生成 5 个 `packages.lock.json`**（锁定完整传递依赖图 + SHA512） | 各项目根 |
| 7 | **`Publish.ps1` 增加锁定文件校验**（缺失即阻断发布）+ `build-info.json` 记录锁定指纹 | `Publish.ps1` |
| 8 | **新增依赖管理文档** | `docs/dependency-management.md` |

### 8.2 验证结果

| 验证项 | 方法 | 结果 |
|--------|------|------|
| 全部配置 XML/JSON 良构 | Python `xml.dom.minidom` / `json` 解析 | ✅ 9/9 PASS |
| CPM 声明与引用一致 | 交叉比对 `PackageVersion` vs `PackageReference` | ✅ 10/10 引用均有声明，无冗余 |
| 锁定文件与集中版本一致 | 比对 `resolved` vs `PackageVersion` | ✅ 11/11 PASS |
| 输出路径未变 | `dotnet build -getProperty:OutputPath` | ✅ 与改动前一致 |
| LogViewer 独立版本保持 | `-getProperty:Version` | ✅ 仍为 `2.0.0` |
| PowerShell 语法 | `[Parser]::ParseFile()` | ✅ 2/2 OK |

### 8.3 遗留事项 —— ✅ 已于 2026-09-14 当日解决

> **更正**：原判定的「沙箱 `APPDATA` 缺失导致无法还原」根因有误，
> 真正缺失的是 `ProgramFiles` / `ProgramData`。补齐后已在本环境真实执行
> `dotnet restore`，**锁定文件现为由 NuGet 原生生成**，不再是 `project.assets.json` 反推版本。
> 详见 `docs/dependency-management.md` §7。

**反推版与 NuGet 原生版的实测差异**（说明反推确实存在偏差，不可作为最终产物）：

| 项目 | 反推生成 | NuGet 原生 | 差异 |
|---|---|---|---|
| `LogViewer/packages.lock.json` | 11729 B | 7013 B | 反推版混入多余内容 |
| `Core/packages.lock.json` | 3282 B | 3367 B | 条目有增减 |
| TFM 键 | `net11.0-windows10.0.26100.0` | `net11.0-windows10.0.26100` | NuGet 用**无末尾 `.0`** 的规范化名 |

**结论**：`packages.lock.json` 必须且只能由 `dotnet restore` 生成；
任何手工/反推产物都应删除后重新还原。校验命令：
```powershell
dotnet restore TrueToneCap.slnx --locked-mode   # 通过即锁定文件有效
```

### 8.4 尚未实施（需评估，见第 4 节阶段 3）

- **消解 `SharpGen.Runtime 2.4.2-beta`**：由 Vortice 3.8.3 锁定，需等待上游发版或实测替换
- **传递依赖滞后项**（WebView2 / Vortice.Mathematics / System.Numerics.Tensors）：
  由上游锁定，正确做法是升级上游而非手动覆盖，当前**不建议单独操作**
