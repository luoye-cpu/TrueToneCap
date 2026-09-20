# 依赖管理指南 / Dependency Management

> 本仓库采用 **NuGet 集中式包版本管理（CPM）** + **依赖锁定文件** 双重机制，
> 以保证依赖版本唯一、可复现、可审计。

---

## 1. 文件职责分工

| 文件 | 职责 | 是否提交 |
|------|------|:---:|
| `Directory.Packages.props` | **依赖版本的单一事实源**。所有 NuGet 包的版本在此声明。 | ✅ |
| `Directory.Build.props` | 公共 MSBuild 属性 + 启用锁定文件（`RestorePackagesWithLockFile`）。 | ✅ |
| `src/*/packages.lock.json` | **锁定的完整依赖图**（含传递依赖的精确版本 + SHA512）。 | ✅ |
| `src/*/*.csproj` | 只声明**用了哪些包**（包名），不写版本。 | ✅ |
| `src/*/obj/project.assets.json` | 还原产物，由 NuGet 生成。 | ❌ (gitignore) |

---

## 2. 日常操作

### 2.1 新增一个依赖

1. 在 `Directory.Packages.props` 加一行（**先查真实版本号**，见第 4 节）：
   ```xml
   <PackageVersion Include="Foo.Bar" Version="1.2.3" />
   ```
2. 在需要的 `.csproj` 引用它（**不写 Version**）：
   ```xml
   <PackageReference Include="Foo.Bar" />
   ```
3. 重新还原并刷新锁定文件：
   ```powershell
   dotnet restore
   ```
4. 提交 `Directory.Packages.props`、对应 `.csproj`、以及更新后的 `packages.lock.json`。

### 2.2 升级一个依赖

1. 改 `Directory.Packages.props` 里那一行版本号。
2. 重新还原（刷新锁定文件）：
   ```powershell
   dotnet restore
   ```
3. 构建 + 全量测试：
   ```powershell
   dotnet build TrueToneCap.slnx -c Release
   dotnet run --project src\TrueToneCap.Test -- --all
   ```
4. 提交。

> ⚠️ **不要手动编辑 `packages.lock.json`** —— 它由还原过程生成。
> 手工改动会导致下次还原时校验失败（`NU1004`）。

### 2.3 临时绕过集中版本（个别项目需要不同版本）

在单个 `PackageReference` 上用 `VersionOverride`：

```xml
<PackageReference Include="Foo.Bar" VersionOverride="2.0.0" />
```

仅在确有需要时使用；滥用会破坏"版本唯一事实源"的价值。

---

## 3. 锁定文件机制

### 为什么需要

没有锁定文件时，`dotnet restore` 会**解析传递依赖的最新匹配版本**。
上游发布一个补丁版本，你的构建产物就可能悄悄变化 —— 同样的源码，不同的二进制。

锁定文件把**完整的传递依赖图**（含版本号与内容哈希）固定下来，使：
- 本地、CI、发布三处还原结果完全一致
- 依赖图变更在 code review 中**可见**（`packages.lock.json` 的 diff）

### 关键约束

| 场景 | 行为 |
|------|------|
| 锁定文件存在且与 csproj 一致 | 正常还原，使用锁定版本 |
| 锁定文件存在但**不一致** | **还原失败**（NU1004）—— 这是刻意的保护 |
| 锁定文件缺失 | 生成新的锁定文件（首次或删除后） |
| 切换 CI 为 `RestoreLockedMode` | 不一致时直接失败而非自动更新（推荐 CI 使用） |

### CI 中的推荐用法

在 CI 中启用"锁定模式"，确保构建使用**已提交**的依赖图，而非现场解析：

```powershell
dotnet restore --locked-mode
```

这样若有人改了版本却没提交锁定文件，CI 会立即报错。

---

## 4. 核实版本号的方法

**绝不凭记忆写"最新版"。** 必须查 NuGet 官方 API：

```
https://api.nuget.org/v3-flatcontainer/<包名小写>/index.json
```

例：查 `Microsoft.ML.OnnxRuntime.DirectML`

```
https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/index.json
```

返回 JSON 的 `versions` 数组，**最后一个非预发布项**即最新稳定版。

### 为什么强调这一点

本仓库的历史文档 `docs/dependency-audit-2026-08-25.md` 曾声称
`Microsoft.ML.OnnxRuntime.DirectML` 最新版为 `1.29.0`，并据此推导出一系列
"升级收益 20-30%"的结论。**该版本在 NuGet.org 上并不存在**
（最新稳定版为 `1.24.4`，即项目当时已在使用的版本）。

这类错误无法通过代码审查发现，只能通过查证真实数据源杜绝。

---

## 5. 判断一个包是否"空壳"

某些 NuGet 包声明了依赖但不提供任何编译/运行时内容。判断方法：

查看 `src/<项目>/obj/project.assets.json` → `targets.<TFM>.<包名>`，
若 `compile` / `runtime` / `native` 三项**全为空**，则该包不产出任何内容。

实例：本仓库曾引用 `libwebp 1.0.0`，其三项资产全空；
项目 WebP 编码实际走 `NativeWebPEncoder` → `cwebp.exe` 子进程，与该包无关。
该包已移除。

---

## 6. 当前依赖快照（2026-09-14 核实）

### 直接依赖（9 项，集中于 `Directory.Packages.props`）

| 包 | 版本 | 项目 |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.4.0 | App, LogViewer |
| `Microsoft.Graphics.Win2D` | 1.4.0 | App |
| `Microsoft.Extensions.DependencyInjection` | 11.0.0-rc.1.26425.128 | App |
| `Microsoft.ML.OnnxRuntime.DirectML` | 1.24.4 | Core |
| `Vortice.Direct3D11` | 3.8.3 | Core |
| `Vortice.DXGI` | 3.8.3 | Core |
| `MozJpegSharp.Core` | 3.0.8 | Core |
| `System.Drawing.Common` | 10.0.11 | Test, Tools |

### 状态说明

- **已是最新稳定版**：WindowsAppSDK、Win2D、OnnxRuntime.DirectML、Vortice.*、MozJpegSharp.Core
- **已有更新**：`Microsoft.Extensions.DependencyInjection`（已升级至 `11.0.0-rc.1`）
- **传递依赖中的预发布版**：`SharpGen.Runtime` / `SharpGen.Runtime.COM` 为 `2.4.2-beta`
  （由 Vortice 3.8.3 锁定，无法单独替换）

> 详见 `docs/dependency-inventory-2026-09-14.md`。

---

## 7. 故障排查

### `NU1004` — 锁定文件与项目不一致

说明 csproj 或 `Directory.Packages.props` 的依赖声明与 `packages.lock.json` 不匹配。

解决：重新还原以刷新锁定文件
```powershell
dotnet restore
```
然后**提交更新后的 `packages.lock.json`**。

### `NU1010` — 缺少 `PackageVersion`

某个 `<PackageReference>` 在 `Directory.Packages.props` 里没有对应的 `<PackageVersion>`。
补上即可。

### `NU1008` — 意外指定了版本

CPM 已启用，但某个 `PackageReference` 仍写了 `Version` 属性。
删除该属性（如需覆盖请改用 `VersionOverride`）。

### 还原报 `Value cannot be null. (Parameter 'path1')`

这是**环境问题**，不是项目问题：NuGet 读取 machine-wide 配置时需拼接 Windows
特殊目录路径，若相关环境变量缺失会抛空引用。错误位置形如
`NuGet.targets(796,5)` → `_GetRestoreSettings` → `GetRestoreSettingsTask`。

**⚠️ 根因是 `ProgramFiles` / `ProgramData`，不是 `APPDATA`。**
（2026-09-14 曾误判为 `APPDATA`，只补 `APPDATA` 实测无效。
`ProgramFiles` 缺失 → `XPlatMachineWideSetting` 构造时 `Path.Combine(null, …)` 抛 `path1`。）

检查并修复：
```bash
# bash / Git Bash 会话
env | grep -iE '^(programfiles|programdata|allusersprofile|appdata)='
# 缺哪个补哪个；ProgramFiles 是最常见且最关键的一个
export ProgramFiles='C:\Program Files'
export ProgramData='C:\ProgramData'
export ALLUSERSPROFILE='C:\ProgramData'
export APPDATA='C:\Users\<你>\AppData\Roaming'
export LOCALAPPDATA='C:\Users\<你>\AppData\Local'
export USERPROFILE='C:\Users\<你>'
```
带括号的 `ProgramFiles(x86)` 在 bash 中不是合法标识符、无法 `export`，但实测**不需要**。
正常 PowerShell / 开发机 / CI 里这些变量由系统预置，不会遇到此问题。

修复成功的判据：`dotnet restore TrueToneCap.slnx` 输出 5 个「已还原」。

> 2026-09-14 更新：本仓库审查环境补齐上述变量后，`restore` / `build` / 全量测试
> **均已真实跑通**，锁定文件也改为由 `dotnet restore` 原生生成（不再需要手工推导）。

---

## 8. 待办与已知缺口

| 项 | 状态 | 说明 |
|---|:---:|---|
| `NuGet.config` | ❌ 未建立 | 未固定 `nuget.org` 源、未做包源审计（对应审查报告 P1-17 的一部分） |
| `SharpGen.Runtime 2.4.2-beta` | ⚠️ 无法消除 | 由 `Vortice 3.8.3` 传递锁定，除非 Vortice 发布移除该依赖的新版 |
| 传递依赖滞后项 | ⚠️ 已记录 | `WebView2` / `Vortice.Mathematics` / `System.Numerics.Tensors` 低于最新，但受上游约束，见依赖清单报告 |
| CI 集成 `--locked-mode` | 📋 待接入 | `ci.yml` 尚未建立（P0-1，本轮明确不做） |

## 9. 变更历史

| 日期 | 变更 |
|------|------|
| 2026-09-14 | 引入 CPM（`Directory.Packages.props`）+ 锁定文件（`RestorePackagesWithLockFile`）；`PackageReference` 去除版本；移除空壳包 `libwebp`；`Microsoft.Extensions.DependencyInjection` 升级至 `11.0.0-rc.1.26425.128` |
