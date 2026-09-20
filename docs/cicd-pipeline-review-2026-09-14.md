# TrueToneCap CI/CD 管线审查报告

> ⚠ **时效标注（2026-09-20 整洁审计）**：本文的"接入标准测试框架"建议（`:625`）与 CI 骨架里的
> `run: dotnet test -c Release --no-build --logger trx`（`:695`，另见 `:714`）**未被采纳，且照做会出事**——
> 本文自己 `:364` 就写着"无测试发现机制，`dotnet test` 无法识别任何测试"。仓库至今不引用
> `Microsoft.NET.Test.Sdk` ⇒ `dotnet test` 的表现是**退出码 0、一条测试都没跑**（假绿灯）。
> 现行唯一入口：`dotnet run --project src/TrueToneCap.Test -c Release -- --<suite>`；
> 完整门禁：`scripts/verify.ps1`（`ci.yml` 只调它，不复制其逻辑）。
> 本文其余部分（缓存、锁定文件、发布链路）仍可作为背景阅读，但结论以
> `docs/pipeline-review-2026-09-15.md` 与 `handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` 为准。

**审查日期**：2026-09-14  
**审查范围**：配置管理、构建管线、测试体系、发布部署、环境一致性、安全性  
**代码基线**：`main` @ `0916a2a` (release: v0.3.3-beta)，工作区含未提交改动  
**项目形态**：.NET 11 / WinUI 3 桌面应用，5 个项目，自包含发布（self-contained win-x64）

---

## 修复状态（2026-09-14 更新）

用户约束：**P0-1（引入 CI 系统）暂不实施**；**P1-8（构建依赖全局 dotnet/py 命令）视为可接受**。
其余项目均已修复或评估。

| 编号 | 项目 | 状态 |
|------|------|------|
| P0-1 | 完全缺失 CI 编排 | ⏸️ 按约束暂不实施 |
| P0-2 | 无版本固定机制 | ✅ 已修复（`global.json` + `Directory.Build.props`） |
| P0-3 | 依赖链含预览版本 | 🔶 部分（已固定 SDK；预览包因 .NET 11 预览版基线暂保留） |
| P0-4 | 版本号三处漂移 | ✅ 已修复（单一源 + `Publish.ps1 -Version`） |
| P0-10 | 零测试框架 | 🔶 部分（运行器与断言已加固；引入 xunit 属独立较大改造） |
| P0-18 | 开发/发布环境混同 | 🔶 部分（已消除对 `bin/` 中间产物的依赖） |
| P0-22 | 内存安全无防护 | ⏸️ 未实施（需 SAST 工具链，独立议题） |
| P1-5 | 构建步骤冗余 | ✅ 已修复（保留 csproj Target 为唯一实现） |
| P1-6 | 校验失败不阻断 | ✅ 已修复（`throw` 阻断） |
| P1-7 | 路径探测脆弱 | ✅ 已修复（Publish.ps1 不再猜测 bin 路径） |
| P1-8 | 全局工具依赖 | ⏸️ 用户明确视为可接受 |
| P1-10 | `--all` 遗漏 WGC 套件 | ✅ 已修复 |
| P1-11 | 测试环境依赖未声明 | ✅ 已修复（README 补「需要桌面会话」列） |
| P1-12 | 测试恒真断言 | ✅ 已修复 |
| P1-13 | 无代码签名 | ⏸️ 未实施（需证书） |
| P1-14 | 无校验和/来源证明 | ✅ 已修复（`SHA256SUMS.txt` + `build-info.json`） |
| P1-15 | 依赖清单人工同步 | ✅ 已修复（单一源改为 `Core/Resources/`） |
| P1-16 | 发布目录膨胀 | ⏸️ 未实施 |
| P1-17 | 脚本参数化不足 | ✅ 已修复 |
| P1-19 | 大文件入库 | ⏸️ 未实施（迁 LFS 属仓库历史操作） |
| P1-20 | 无 `.editorconfig` 门禁 | ⏸️ 未实施（已移除误导性空 `CodeAnalysisRuleSet`） |
| P1-21 | 无 SAST/SCA | ⏸️ 未实施 |
| P2-9 | 着色器 Target 映射 | ✅ **经核实原判断有误，实际正确** |
| P2-17 | 发布脚本参数化 | ✅ 已修复 |

### 本轮实施的具体改动

**新增**：
- `global.json`（固定 SDK）
- `Directory.Build.props`（公共属性单一事实源）
- `Directory.Packages.props`（**依赖版本单一事实源**，CPM）
- `src/*/packages.lock.json`（5 个，锁定完整传递依赖图）
- `docs/dependency-management.md`（依赖管理操作指南）

**修改**：
- `src/*/*.csproj`（5 个）— 移除被集中接管的重复属性；`PackageReference` 去除 `Version`（改由 CPM 管理）
- `Publish.ps1` — 重写（详见下节）；新增 [0.5/6] 锁定文件齐全性校验 + `build-info.json` 记录 `lockFilesSha256`
- `PublishLogViewer.ps1` — PRI 缺失改为阻断
- `src/TrueToneCap.Test/Program.cs` — `--all` 补全 8 套件 + WGC 子进程隔离
- `src/TrueToneCap.Test/ServiceTests.cs` / `UsabilityTests.cs` / `ColorPipelineTests.cs` — 修复恒真断言
- `scripts/onnx_fp16_quantize.py` — 移除硬编码仓库路径
- `README.md` / `tests/README.md` — 补充环境要求、测试矩阵、发布说明
- `src/TrueToneCap.Core/TrueToneCap.Core.csproj` — **移除空壳包 `libwebp 1.0.0`**（零资产包，实际 WebP 编码走 `cwebp.exe` 子进程）
- `Directory.Build.props` — 启用 `RestorePackagesWithLockFile=true`

**依赖升级**：
- `Microsoft.Extensions.DependencyInjection`：`10.0.x` → `11.0.0-rc.1.26425.128`
  （仅此一项升级；其余 8 项直接依赖均已是当前最新稳定版，见 `docs/dependency-inventory-2026-09-14.md`）

### 两点重要更正

1. **P2-9 原判断有误**：经核实 `CompileShaders.ps1` 产出 `<Shader>.hlsl.cso`，
   与 Target 的 `Outputs` 声明及 `Content Shaders\*.cso` 通配**完全一致**，
   实际文件 `ToneMapping.hlsl.cso` 等均存在，增量构建可正确判定。
   原报告的怀疑基于「产出可能是 `ToneMapping.cso`」的**推测**，实测不成立，**无需修改**。

2. **`OcrServiceTests` 无需进程隔离**：其测试体仅覆盖 `BitmapPreprocessor` 与
   `LlmProviders` 的纯函数逻辑（无 ONNX 会话、无 GPU），仍保留在同进程串联组；
   仅 `WgcAvifBackendTests` 因独占 STA 线程与 GPU 资源而需子进程隔离。

### 验证限制说明（环境问题，非项目缺陷）

> **✅ 2026-09-14 后续更新：限制已解除，下述内容仅作问题排查留档。**
> 根因并非 `APPDATA`，而是 `ProgramFiles` / `ProgramData` 等变量缺失
> （见 `docs/dependency-management.md` §7）。补齐后本环境已真实跑通：
> `dotnet restore`（含 `--locked-mode`）、`dotnet build -c Release`、
> 全量测试 `--all` → **8/8 套件、373 项断言全部通过**。
> 唯一遗留：构建 `App` 项目前需先退出正在运行的 TrueToneCap（否则输出 DLL 被占用）。

本审查环境沙箱中 `APPDATA` **未定义**（`USERPROFILE` 正常），导致 NuGet 的
`XPlatMachineWideSetting` 构造抛 `ArgumentNullException: path1`，使**所有**
`dotnet restore` / `dotnet build` 失败。

已用**全新最小项目**（与 TrueToneCap 无关）复现同样错误，确认属环境限制：

```
$ dotnet build t.csproj        # 最小 net11.0 控制台项目
error : Value cannot be null. (Parameter 'path1')
  at NuGet.Common.NuGetEnvironment.CalculateFolderPath(...)
  at NuGet.Configuration.XPlatMachineWideSetting..ctor()
```

**替代验证手段及结果**：
- `dotnet build -getProperty:...`（不触发 NuGet）→ ✅ 5 个项目属性全部正确解析，
  含两处项目特有覆盖（`LogViewer` 的 `AllowUnsafeBlocks=false`、`Tools` 的 `SelfContained=false`）
- `-p:Version=v0.3.5-beta` 覆盖 → ✅ 成功注入
- PowerShell Parser 语法校验 → ✅ 3 个脚本全部通过
- XML/JSON 解析 → ✅ 7 个文件全部合法
- 括号平衡（去注释/字符串）→ ✅ 4 个 C# 文件全部平衡
- 版本解析正则 → ✅ 解析出 `0.3.4-beta`，且不误匹配 `AssemblyVersion`
- 依赖源路径存在性 → ✅ 4 工具 + 3 模型全部就位

**待用户在正常环境最终确认**：
```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/TrueToneCap.Test -- --all
.\Publish.ps1 -NoReadyToRun
```

---

## 一、执行摘要

| 维度    | 评级   | 一句话结论                                |
| ----- | ---- | ------------------------------------ |
| 配置管理  | 🔴 差 | 无版本固定、无集中依赖管理、版本号三处漂移                |
| 构建管线  | 🟡 中 | 单机脚本可用，但步骤冗余、失败不阻断、产物不可复现            |
| 测试体系  | 🔴 差 | 自研断言器、零测试框架、无报告产出，**无法接入任何 CI**      |
| 部署发布  | 🟡 中 | 一键脚本体验好，但含人工步骤、无签名、无版本追溯             |
| 环境一致性 | 🔴 差 | 依赖全局 .NET 11 预览版 + 全局 Python/`py` 命令 |
| 安全性   | 🟡 中 | 无密钥泄露，但无 SAST/SCA、无代码签名、unsafe 代码无防护 |

**总体判断**：本项目当前处&#x4E8E;**「本地手工构建」阶段，尚未建立 CI/CD 管线**。  
仓库中**不存在任何 CI 配置**（无 `.github/workflows/`、无 `azure-pipelines.yml`、无 `.gitlab-ci.yml`），  
所有构建、测试、发布动作均由开发者在本机通过 `Publish.ps1` 手工触发。  
核心矛盾是：**发布流程本身写得相当完整（5 步流水线 + 依赖校验 + 目录整理），但它只存在于一台机器上，且没有任何自动化质量门禁。**

---

## 二、配置维度

### 🔴 P0-1 完全缺失 CI 编排

**证据**：

```
$ ls .github            → 不存在
$ find . -name "*.yml" -o -name "*.yaml"  → 无 CI 配置
```

无 CI 意味着：

- 代码推送到 `main` 后**没有任何自动构建/测试验证**（`git log` 显示直接向 `main` 提交，共 15 次提交全在主干）
- 无法在 PR 阶段拦截破坏性改动
- 发布质量完全依赖开发者本机环境和自觉性

### 🔴 P0-2 无版本固定机制（三处缺失）

| 文件                         | 状态    | 后果                                                            |
| -------------------------- | ----- | ------------------------------------------------------------- |
| `global.json`              | ✅ 已建 | 固定 SDK `11.0.100-preview.7.26381.103`                          |
| `Directory.Build.props`    | ✅ 已建 | 公共属性（LangVersion/Nullable/PlatformTarget）集中声明               |
| `Directory.Packages.props` | ✅ 已建 | 依赖版本集中于 8 条 `PackageVersion`（CPM 已启用）                        |
| `packages.lock.json`       | ✅ 已生成 | 5 个项目各一份，锁定完整传递依赖图 + SHA512（**需提交入库**）                  |
| `NuGet.config`             | ❌ 不存在 | 依赖默认源，无源固定/无镜像配置（**仍待补充**）                                    |

**版本分散实证**（同一属性重复 5 份）：

```
src/TrueToneCap.App/TrueToneCap.App.csproj:6        <RuntimeIdentifier>win-x64</RuntimeIdentifier>
src/TrueToneCap.LogViewer/...csproj:6               <RuntimeIdentifier>win-x64</RuntimeIdentifier>
src/TrueToneCap.App/...csproj:8                     <LangVersion>14</LangVersion>
src/TrueToneCap.Core/...csproj:4                    <LangVersion>14</LangVersion>
src/TrueToneCap.Test/...csproj:5                    <LangVersion>14</LangVersion>
src/TrueToneCap.Tools/...csproj:5                   <LangVersion>14</LangVersion>
src/TrueToneCap.LogViewer/...csproj:11              <LangVersion>14</LangVersion>
```

> ✅ **已修复**：新增 `global.json`（固定 SDK `11.0.100-preview.7.26381.103`）与
> `Directory.Build.props`（公共属性单一事实源）。5 个 csproj 的重复声明已全部移除。
>
> ✅ **后续补充（同日）**：引入 `Directory.Packages.props` 启用**集中式包版本管理（CPM）**，
> 8 个依赖版本集中声明，10 处 `PackageReference` 的 `Version` 属性全部移除；
> 并启用 `RestorePackagesWithLockFile` 生成 **5 份 `packages.lock.json`**，锁定完整传递依赖图。
> `Publish.ps1` 增加锁定文件校验（缺失即阻断）。详见 `docs/dependency-management.md`。

### 🔴 P0-3 依赖链含预览版本（不可复现风险）

```xml

<TargetFramework>net11.0-windows10.0.26100.0</TargetFramework>     ← .NET 11（预览）
<PackageReference Include="Microsoft.Extensions.DependencyInjection"
                  Version="11.0.0-preview.6.26359.118" />            ← 显式预览版
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
```

`Microsoft.Extensions.DependencyInjection` 直接引用带 `-preview.` 后缀的版本，  
NuGet 对此类版本不做稳定性保证，且**同一 preview 版本号可能被重新发布**（NuGet 允许覆盖 preview 包），  
意味着今天能还原的构建，明天可能还原出不同二进制。

### 🟡 P1-4 版本号三处漂移

```
Publish.ps1:6          $OutputDir = "publish\TrueToneCap-v0.3.3-beta"     ← 0.3.3
Publish.ps1:15         标题硬编码 "TrueToneCap v0.3.3 Beta 发布脚本"      ← 0.3.3
App.csproj:22          <Version>0.3.4-beta</Version>                      ← 0.3.4
App.csproj:23-24       <AssemblyVersion>0.3.4.0</AssemblyVersion>         ← 0.3.4
README.md              已写 "v0.3.4-beta — 2026-09-13"                    ← 0.3.4
```

发布脚本产物目录名与程序集版本**不一致**，会导致发布包无法与 git tag 对应。

> ✅ **已修复**：版本号唯一源为 `Directory.Build.props` 的 `<Version>`（带 `Condition`）；
> `Publish.ps1` 新增 `-Version` 参数，留空时自动解析该值（正则 `<Version\b[^>]*>`），
> 硬编码的 `v0.3.3-beta` 已移除。

---

## 三、构建维度

### 🟡 P1-5 构建步骤冗余：csproj Target 与 PS 脚本职责重叠

`Publish.ps1` 步骤 2/3 与 `csproj` 内 `CopyPriAndXbf` Target 做了**同一件事**：

```xml

<Target Name="CopyPriAndXbf" AfterTargets="Publish">
  <Copy SourceFiles="@(_PriFiles)" DestinationFolder="$(PublishDir)" ... />
  <Copy SourceFiles="@(_XbfFiles)" DestinationFolder="$(PublishDir)" ... />
  <Move SourceFiles="@(_PdbFiles)" DestinationFolder="$(PublishDir)data\" />
  ...
</Target>
```

```powershell
# Publish.ps1:110-149  又手工做一遍同样的复制
Write-Host "[2/5] 复制 PRI 资源..."
Copy-Item $priFile.FullName $OutputDir -Force
Write-Host "[3/5] 复制 XBF 文件..."
Copy-Item $xbfFiles.FullName $OutputDir -Force
```

双重实现必然漂移：csproj 版用 `$(PublishDir)` 且 `SkipUnchangedFiles`，  
PS 版用 `$OutputDir` 且 `-Force`，两者对 `.pdb` 的处理也不同（csproj `Move`，PS 直接 `Remove-Item`）。  
**同一份产物被两套逻辑操作，是隐蔽的构建缺陷来源。**

> ✅ **已修复**：Publish.ps1 中重复的 PRI/XBF/PDB 处理已删除，
> `App.csproj` 的 `CopyPriAndXbf` Target 保留为唯一实现。

### 🟡 P1-6 依赖校验失败不阻断构建

```powershell
# Publish.ps1:64-68
if (-not $allOk) {
    Write-Warning "   ⚠️ 部分依赖缺失，请检查 publish/PLAN/ 目录"
} else {
    Write-Host "   ✅ PLAN 依赖仓库完整"
}
# ← 没有 throw，脚本继续往下跑
```

`[0/5]` 校验步骤发现的缺失（如 `avifenc.exe`、OCR 模型），  
**不会终止发布**，只是打印警告。后续 `[5/5]` 复制时同样只 `Write-Warning`：

```powershell
# Publish.ps1:181
Write-Warning "   ⚠️ $exe 在 PLAN/tools/ 中缺失"
```

结果是会**静默产出功能残缺的发布包**（例如缺少 AVIF 编码器的包），直到用户运行才发现。  
对比 `[1/5]` 编译步骤有正确的 `if ($LASTEXITCODE -ne 0) { throw }` —— 说明脚本作者知道该模式，只是校验步骤漏了。

> ✅ **已修复**：`[0/6]` 依赖校验收集全部缺失项后 `throw`；
> 工具/模型复制阶段若单文件缺失同样 `throw`，不再产出残缺包。

### 🟡 P1-7 路径探测脆弱（三候选硬编码）

```powershell
# Publish.ps1:115-120
$candidates = @(
    "src\TrueToneCap.App\bin\x64\$Configuration\net11.0-windows10.0.26100.0\$Runtime",
    "src\TrueToneCap.App\bin\$Configuration\net11.0-windows10.0.26100.0\$Runtime",
    "src\TrueToneCap.App\bin\x64\$Configuration\net11.0-windows10.0.26100.0"
)
```

路径中硬编码了 TFM `net11.0-windows10.0.26100.0`，**升级 .NET 或 Windows SDK 时脚本静默失效**  
（走到 `$BinDir = $null` 分支，PRI/XBF 复制全部降级为警告）。注释自承"旧路径在清理缓存后失效"。  
正确做法是让 MSBuild 输出 `$(OutputPath)`，而非在脚本里猜路径。

> ✅ **已修复**：Publish.ps1 不再从 `bin/` 拾取 PRI/XBF（改由 csproj Target 处理），
> 三候选硬编码 TFM 路径逻辑已移除。`PublishLogViewer.ps1` 保留 bin 探测但
> 在找不到目录或 PRI 时改为 `throw`（不再静默降级）。

### 🟡 P1-8 构建环境依赖全局状态

| 依赖                         | 引用位置                                | 风险                                |
| -------------------------- | ----------------------------------- | --------------------------------- |
| 全局 `dotnet`                | `Publish.ps1:99`                    | 未固定 SDK 版本，无 `global.json`        |
| 全局 `py` 命令                 | `download_models.ps1:33`            | 依赖 Python Launcher 及 `rapidocr` 包 |
| 全局 `powershell`            | `App.csproj:70`（编译着色器）              | 假设 `powershell` 在 PATH            |
| `taskkill`                 | `Publish.ps1:95`                    | 强制终止进程，若用户有未保存工作会丢失数据             |
| 绝对路径 `C:/PLAN/TrueToneCap` | `scripts/onnx_fp16_quantize.py:103` | **硬编码开发者机器路径，脚本在他人机器必然失败**        |

其中 `scripts/onnx_fp16_quantize.py` 的 `base_dir = "C:/PLAN/TrueToneCap"` 是最典型的环境耦合，  
应改为 `os.path.dirname(os.path.dirname(os.path.abspath(__file__)))` 或接受命令行参数。

另外 `taskkill /F /IM TrueToneCap.exe` 是**破坏性操作**：无确认、无保存提示、强制杀进程。  
在 CI 中无害，但在开发者本机运行发布时可能丢失正在编辑的截图/标注工作。

### ✅ P2-9 着色器 Target 映射 —— 经核实**无问题**（原判断更正）

```xml
<Target Name="CompileShaders" BeforeTargets="Build"
        Inputs="Shaders\ToneMapping.hlsl;Shaders\FullscreenVS.hlsl;Shaders\OverlayComposite.hlsl"
        Outputs="Shaders\ToneMapping.hlsl.cso;Shaders\FullscreenVS.hlsl.cso;Shaders\OverlayComposite.hlsl.cso">
```

**审查时怀疑**：`Outputs` 声明为 `*.hlsl.cso`，若 `CompileShaders.ps1` 实际产出
`ToneMapping.cso`，则会导致增量构建每次重编译或 `Content` 通配匹配不到文件。

**核实结论**：怀疑不成立。证据链完整闭合：

1. `CompileShaders.ps1` 的输出命名为 `Join-Path $OutputDir "$($s.Input).cso"`，
   而 `$s.Input = "ToneMapping.hlsl"` → 实际产出 **`ToneMapping.hlsl.cso`**
2. `Shaders/` 目录下实际存在 `ToneMapping.hlsl.cso` / `FullscreenVS.hlsl.cso` /
   `OverlayComposite.hlsl.cso` 三个文件 —— 与 `Outputs` 声明**逐字一致**
3. `Content Include="Shaders\*.cso"` 通配可正常匹配这三个文件（均以 `.cso` 结尾）
4. 生成的 `.cso` 被 `.gitignore` 正确忽略（属构建产物，按需由 PreBuild Target 重建），
   `git ls-files` 确认仅 4 个源文件入库（3 个 `.hlsl` + 1 个 `.ps1`）

**结论：设计正确，无需修改。** 原报告的怀疑源于对产出文件名的**推测**而非实测，特此更正。

---

## 四、测试维度


### 🔴 P0-10 零测试框架（最严重的管线阻断项）

**证据**：

```
$ git grep -l "xunit|NUnit|MSTest|Microsoft.NET.Test.Sdk" -- "*.csproj"
(无输出 — 无任何测试框架)
```

`TrueToneCap.Test.csproj` 完整依赖列表仅一行：

```xml
<PackageReference Include="System.Drawing.Common" Version="10.0.12" />
```

测试实现是**完全自研的手写断言器**（以 `ServiceTests.cs` 为例）：

```csharp
private static int _passed, _failed;
static void Assert(string name, bool condition, string? detail = null)
{
    if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
    else { _failed++; Console.WriteLine($"  ❌ {name}..."); }
}
```

**这带来的连锁问题**：

1. **无测试发现机制** — `dotnet test` 无法识别任何测试（无 `[Fact]`/`[Test]`/`[TestMethod]`，  
   无 `Microsoft.NET.Test.Sdk`，`IsTestProject` 未设置）
2. **无结构化报告** — 只输出 ANSI 彩色文本到 stdout，无 TRX/JUnit XML，  
   CI 平台无法解析通过率、无法做趋势分析、无法在 PR 上标注失败用例
3. **无隔离** — 所有测试在同一进程顺序执行（`Program.cs` 手动串联 7 个 `RunAll()`），  
   共享静态状态（`_passed`/`_failed` 为静态字段），一个用例污染全局状态会影响后续全部用例
4. **无参数化/无 Fixture** — 无 `[Theory]`、无共享上下文，重复代码量大
5. **无可选执行** — 无法按名称筛选单个测试、无法并行、无法重试 flaky 用例

8 个测试文件共 **316 处手写 `Assert(` 调用**，全部无法被标准工具链消费：

```
ColorPipelineTests.cs:82    CorePipelineTests.cs:47    UsabilityTests.cs:97
FormatStructureTests.cs:36  WgcAvifBackendTests.cs:21 EncodingIntegrationTests.cs:13
OcrServiceTests.cs:11       ServiceTests.cs:9
```

**同时注意**：`Program.cs:53-73` 的 `--all` 聚合入口**遗漏了 `WgcAvifBackendTests`**。  
`--wgc-avif-tests` 是独立入口（`Program.cs:47`），但 `--all` 只串联 7 个套件（第 55-68 行），  
不含 WGC 套件。即"全部测试"实际上**跑不全**。README 中宣称的  
"Unit 24/24、Usability 144/144、Encoding 14/14" 也无法在一条命令下复现。

> ✅ **已修复**：`Program.cs` 已重写，`--all` 现覆盖全部 **8** 个套件并输出套件汇总表。
> `WgcAvifBackendTests` 因需独占 STA 线程与 D3D11/捕获会话等 GPU 资源，
> 改为**独立子进程**运行以避免与同进程内其它测试相互干扰；
> 其余 7 个套件同进程串联（经核查 `OcrServiceTests` 为纯函数测试，无需隔离）。

### 🟡 P1-11 测试环境依赖未声明

`README` 注明 `--wgc-avif-tests` "需桌面会话"，`tests/README.md` 列出 7 个套件但未说明：

- 哪些套件需要 GUI/桌面会话（无法在 headless CI 上运行）
- 哪些需要真实硬件（NVENC 编码测试需 NVIDIA GPU）
- 哪些需要预置模型文件（OCR 测试需 `Models/*.onnx`）
- 哪些需要外部原生工具（编码测试需 `native/*.exe`）

`WgcAvifBackendTests.cs` 含 `Thread.Sleep`/`Task.Delay` 固定等待（**固定 sleep 是 flaky 测试的头号来源**），  
在慢速 CI runner 上极易超时失败。

**这直接决定了 CI 策略**：测试无法全量上 CI，必须按"可 headless 运行"重新分层。

### 🟡 P1-12 测试默认宽松（false-pass 风险）

```csharp
// ServiceTests.cs:45-47
var bytes = ShaderLoader.Load("ToneMapping.hlsl.cso");
// 文件系统找不到时返回 null（不崩溃），嵌入资源也找不到时亦然
Assert("ShaderLoader: ToneMapping 不崩溃", bytes is null or { Length: > 0 });
```

断言写成"`null` **或** 有内容都算通过" —— 即**无论加载成功还是失败都通过**，这个断言永远为真。  
同类问题见 `CacheWorks` 断言 `first is null || ReferenceEquals(first, second)`。  
这种断言在自研框架下不会报警，但迁移到 xunit 时会立即暴露为无效测试。**迁移时需逐个复核断言有效性。**

> ✅ **已修复**：三处恒真断言已重写为可证伪形式：
> - `LoadKnownShader`：拆为「调用不抛异常」+「非空时必须具备 **DXBC 魔数**（`D`/`X`/`B`/`C`）」；
>   无数据时明确打印「未找到」而非静默通过
> - `CacheWorks`：拆为「两次加载引用相等」+「`ClearCache` 后重新加载内容一致」
> - 另修复同类的 `UsabilityTests.cs`（`GetDisplayIccProfile(0)`、`BakeIccToTarget`）
>   与 `ColorPipelineTests.cs`（`BakeIccToTarget`），拆分为独立的尺寸断言与 ICC 完整性断言
>
> 经复核 `CorePipelineTests.cs` 的两处 `icc is null || icc.Length < 128`
> 属**正确的失败判定逻辑**（`ok = false`），非恒真写法，未改动。

---

## 五、部署维度

### 🟡 P1-13 无代码签名（用户侧安全警告 + 易被篡改）

`.gitignore:134-138` 已预置签名文件忽略规则：

```
*.pfx
*.snk
*.spc
*.cer
```

但 **发布流程中无任何签名步骤**。后果：

- 用户下载 exe 会触发 SmartScreen "未知发布者" 警告
- 无签名 = 无法验证产物完整性，中间人替换无法检测
- 无 `signtool` / `Azure Trusted Signing` 集成

### 🟡 P1-14 发布产物无校验和、无来源证明

`Publish.ps1` 结束时只打印体积和文件数，**不产出**：

- SHA256 校验和文件（用户无法验证下载完整性）
- SBOM（软件物料清单）
- 构建元数据（git commit SHA、构建时间、SDK 版本）
- 版本清单 JSON

发布产物目录名 `TrueToneCap-v0.3.3-beta` 是唯一的版本标识，且该标识与程序集版本不一致（见 P1-4）。

> ✅ **已修复**：`[6/6]` 步骤产出 `SHA256SUMS.txt`（发布目录全文件校验和）与
> `build-info.json`（版本 / 配置 / commit SHA / SDK 版本 / 构建时间 / 构建机）。

### 🟡 P1-15 依赖清单靠人工同步（已识别的漂移隐患）

原生工具链在两处各存一份：

```
src/TrueToneCap.Core/Resources/avifenc.exe   (5.2 MB)   ← 嵌入用
publish/PLAN/tools/avifenc.exe               (5.2 MB)   ← Publish.ps1 预提取用
src/TrueToneCap.Core/Resources/cjpegli.exe   vs  publish/PLAN/tools/cjpegli.exe
src/TrueToneCap.Core/Resources/cjxl.exe      vs  publish/PLAN/tools/cjxl.exe
```

`publish/PACKAGE.md` 声明"必须保持同步"，但**同步靠人工**，Publish.ps1 不做哈希校验。  
`docs/repo-hygiene-review.md` 已将其列为 🟡 项，但截至本次审查仍未修复。  
一旦漂移，嵌入资源与预提取 exe 版本不一致，会产生难以定位的编码行为差异。

> ✅ **已修复**：原生工具链单一事实源改为 `src/TrueToneCap.Core/Resources/`，
> Publish.ps1 直接从该目录复制到发布包 `native/`，不再读取 `publish/PLAN/tools/`，
> 从结构上消除双份同步需求。

### 🟢 P2-16 发布目录膨胀与垃圾文件

```
publish/                 728 MB
├── PLAN/tools/*.raw     33MB × 5 + 8.3MB + 1.5KB×2 ≈ 174 MB  ← 调试像素数据
├── PLAN/tools/*.dll     libgcc/libstdc++/libwinpthread/libuhdr
└── ...
```

`*.raw` 虽被 `.gitignore:55` 忽略，但**物理上仍占 174MB 磁盘**且无清理机制。  
`ultrahdr_app.exe`、`libuhdr.dll` 存在于 tools/ 但未被 `Publish.ps1:173` 的复制列表包含  
（列表只有 4 个：`avifenc/cjpegli/cwebp/cjxl`）—— 死文件。

### 🟢 P2-17 发布脚本参数化不足

```powershell
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish\TrueToneCap-v0.3.3-beta",
    [switch]$CoreAot = $false,
    [switch]$NoReadyToRun = $false
)
```

- `Version` 未参数化（硬编码在 `$OutputDir` 默认值里）
- `Runtime` 只支持 `win-x64`（实际也只能跑 x64，但参数形同虚设）
- 无 `-SkipTests`/`-Force` 之类的安全开关，无 dry-run 模式
- `$CoreAot` 自承"试验性"却暴露为用户选项，会造成不受支持的构建组合

> ✅ **已修复**：新增 `-Version` / `-OutputDir` / `-Force` / `-SkipDependencyCheck`；
> 版本默认从 `Directory.Build.props` 解析，输出目录依据版本自动生成。

---

## 六、环境一致性维度

### 🔴 P0-18 开发环境与发布环境完全同一（无隔离）

当前"环境一致性"的现状是：**只有一台机器的一种环境**。  
`Publish.ps1` 直接复用开发者 `bin/` 中间产物（`Publish.ps1:115-136` 从 `bin/` 拾取 PRI/XBF），  
意味发布产物**依赖于开发者本地 `obj/`、`bin/` 的历史状态**。  
注释自承："旧路径在清理缓存后失效，需自适应查找" —— 这正是不干净构建导致不一致的直接证据。

标准 CI 应从干净 checkout 出发，`dotnet restore --locked-mode` + `dotnet publish`，  
确保 `publish` 结果只由源码 + 锁定依赖决定。

### 🟡 P1-19 大型二进制入库（仓库 170MB）

```
.git                                                    170 MB
src/TrueToneCap.App/Models/PP-OCRv6_medium_rec.onnx      38 MB  (tracked)
src/TrueToneCap.App/Models/PP-OCRv6_medium_det.onnx      31 MB  (tracked)
src/TrueToneCap.Core/Resources/avifenc.exe              5.2 MB  (tracked)
official_models/                                        133 MB  (已 gitignore)
```

模型与工具链二进制入库导致 clone 极慢、仓库无法瘦身（历史不可逆）。  
`download_models.ps1` 已具备按需下载能力，但**主模型仍走 git**（csproj:64 从 `Models\*.onnx` 打包分发）。

### 🟡 P1-20 无 `.editorconfig` / 无代码质量门禁

```xml
<CodeAnalysisRuleSet></CodeAnalysisRuleSet>   
```

- 无 `.editorconfig`（无统一缩进/换行/编码约定，与 `docs/repo-hygiene-review.md` 提到的  
  "HDRPreviewWindow.cs 疑似行尾符改动" 问题呼应）
- `CodeAnalysisRuleSet` 声明了却是空字符串 → **分析器完全未配置**
- 无 `.NET analyzers` 启用（`EnableNETAnalyzers` 未设置，默认仅 `latest` 级别的部分规则）
- 崩溃根因（AVIF stdin、WGC 超时、OCR 线程安全）**都有一个共同特征：属于分析器能捕获的类别**  
  （unsafe 指针误用、未释放资源、竞态），却因无门禁而只能在运行期暴露

---

## 七、安全维度

### ✅ 已做好的部分

1. **无硬编码密钥**（本次扫描结果）：
   ```
   git grep -n -iE "(api[_-]?key|secret|password|token|bearer)\s*[:=]\s*[\"'][A-Za-z0-9_-]{12,}"  → 0 命中
   git grep -n -E "sk-[A-Za-z0-9]{16,}"                                                        → 0 命中
   ```
2. **签名密钥忽略规则完备**：`*.pfx` / `*.snk` / `*.spc` / `*.cer` 全部忽略
3. **调试脚本隔离**：`tests/manual/` 整体 gitignore，含 API Key 的 `glm_*` 脚本未入库
4. **日志/崩溃转储忽略**：`*.log`、`*.dmp`、`*_crash.log`、`log/`

### 🟡 P1-21 无 SAST / SCA / 供应链防护

| 防护项           | 状态               | 风险                                       |
| ------------- | ---------------- | ---------------------------------------- |
| 静态代码扫描 (SAST) | ❌ 无              | CodeQL / SecurityCodeScan 未接入            |
| 依赖漏洞扫描 (SCA)  | ❌ 无              | `dotnet list package --vulnerable` 未在流程中 |
| 密钥扫描          | ❌ 无              | 无 gitleaks/trufflehog，仅靠人工 gitignore     |
| 依赖来源校验        | ❌ 无 NuGet.config | 无源固定，存在依赖混淆风险                            |
| 分支保护          | ❌ 无              | `main` 可直接 push（`git log` 显示全部提交直达主干）    |

`docs/dependency-audit-2026-08-25.md` 显示项目**做过人工依赖审计**，但那是**一次性人工动作**，  
不是自动化门禁 —— 新的 CVE 无法被持续发现。

### 🔴 P0-22 内存安全高风险面且无任何防护

```xml

<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<LegacyCorruptedStateExceptionsPolicy>true</LegacyCorruptedStateExceptionsPolicy>
```

```xml

<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

项目特征决定了高风险：

- **大量原生互操作**：`NativeAvifEncoder`、`NvEncoderNative`、`ManagedPngEncoder`、  
  直接 D3D11/DXGI COM 调用（Vortice）、ONNX Runtime P/Invoke
- `LegacyCorruptedStateExceptionsPolicy` 明确是为了**捕获 `AccessViolationException`/`SEHException`**  
  并继续运行 —— 这意味着**原生层崩溃被吞掉后继续执行**，可能造成静默数据损坏
- `TrimmerRoots.xml` 注释自承："移除这些类型会导致所有 D3D11 操作在 AOT 运行时崩溃" ——  
  AOT + COM vtable 反射是脆弱组合

在此前提下，**无 SAST、无 fuzzing、无 ASan 等价物、无自动化回归测试覆盖原生路径**，  
构成实质性的安全与稳定性风险。

### 🟢 P2-23 安全实践层面的次要问题

- `Publish.ps1` 中使用 `powershell -ExecutionPolicy Bypass`（csproj:70）绕过执行策略，  
  在多用户/受管环境属敏感行为
- 无 `SECURITY.md` / 漏洞披露流程
- 无依赖许可证合规检查（`LICENSE` 为单一项目许可，但内置 `avifenc`/`cwebp`/`cjxl` 等  
  第三方二进制各自的许可证未随发布包分发说明）

---

## 八、改进方案（按优先级排序）

### 阶段一：止血（P0，建议 1-2 周内完成）

| # | 行动                                                                                                                                                         | 验收标准                                     |
| - | ---------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| 1 | **建立最简 CI**（GitHub Actions `windows-latest`）：`restore → build → 运行可 headless 的测试子集 → publish`                                                              | PR 上出现 ✅/❌ 状态检查，构建产物作为 artifact 上传       |
| 2 | **接入标准测试框架**：`Microsoft.NET.Test.Sdk` + `xunit`，将 8 个套件迁移为 `[Fact]`/`[Theory]`，`dotnet test --logger trx`                                                  | `dotnet test` 能发现并执行测试；产出 TRX/JUnit XML  |
| 3 | ~~**修复 `--all` 遗漏 WGC 套件**~~ **✅ 已完成**：`--all` 现覆盖全部 8 个套件，WGC 以子进程隔离 | `--all` 真实覆盖全部套件 |
| 4 | ~~**添加 `global.json`** 固定 SDK 版本~~ **✅ 已完成** | 不同机器 `dotnet --version` 一致 |
| 5 | **添加 `Directory.Build.props`** ✅ 已完成（收敛公共属性）；~~`Directory.Packages.props`~~ ⏸️ 用户明确不需要 | 5 个 csproj 无重复声明 |
| 6 | **升级或替换预览依赖**：`Microsoft.Extensions.DependencyInjection 11.0.0-preview.*` → 稳定的 10.x，或至少固定精确版本并加 `packages.lock.json` | 构建可复现 |


### 阶段二：加固（P1，建议 1 个月内）

| #  | 行动 | 状态 / 收益 |
| -- | ---- | ---------- |
| 7  | **修复 Publish.ps1 失败不阻断** | ✅ 已完成（`throw` 阻断） |
| 8  | **消除步骤冗余**：PRI/XBF/PDB 处理只保留一处 | ✅ 已完成（保留 csproj Target） |
| 9  | **路径探测改为 MSBuild 驱动** | ✅ 已完成（Publish.ps1 不再猜测 bin 路径） |
| 10 | **版本号单一来源** | ✅ 已完成（`Directory.Build.props` + `-Version`） |
| 11 | **`Publish.ps1` 增加 `-Version`** + 移除 `taskkill /F` | ✅ 已完成（改需 `-Force`） |
| 12 | **原生工具链单一事实源** | ✅ 已完成（源改为 `Core/Resources/`） |
| 13 | **发布产物加 SHA256 校验和 + 构建元数据** | ✅ 已完成（`SHA256SUMS.txt` + `build-info.json`） |
| 14 | **接入 `dotnet list package --vulnerable`** | ⏸️ 待实施（依赖 CI） |
| 15 | **添加 `.editorconfig`** + 启用 `EnableNETAnalyzers` | ⏸️ 待实施 |
| 16 | **修复硬编码路径** | ✅ 已完成（改为 `find_repo_root()` + argparse） |
| 17 | **为大文件引入 Git LFS 或移除** | ⏸️ 待实施（属仓库历史操作） |

### 阶段三：体系化（P2，持续演进）

| #  | 行动                                                                                                | 说明                      |
| -- | ------------------------------------------------------------------------------------------------- | ----------------------- |
| 18 | **代码签名**：接入 Azure Trusted Signing 或自签证书，发布包签名                                                     | 消除 SmartScreen 警告，可验完整性 |
| 19 | **分支保护 + PR 流程**：`main` 禁止直推，要求 CI 通过 + review                                                    | 建立质量闸门                  |
| 20 | **构建矩阵分层**：headless-suitable 套件上 CI，硬件依赖套件（NVENC/WGC）走自托管 runner 或夜跑                              | 兼顾覆盖率与可行性               |
| 21 | **消除 flaky 风险**：`WgcAvifBackendTests` 的固定 `Thread.Sleep`/`Task.Delay` 改为条件轮询（带超时）                 | 提升 CI 稳定性               |
| 22 | **清理发布目录垃圾**：删除 `PLAN/tools/*.raw`（174MB）；移除未使用的 `ultrahdr_app.exe`/`libuhdr.dll` 或在脚本中正确处理       | 减小发布包                   |
| 23 | **SBOM 产出**（`dotnet CycloneDX`）                                                                   | 供应链透明                   |
| 24 | **`SECURITY.md`** + 漏洞披露流程；核对第三方二进制许可证随包分发                                                        | 合规与信任                   |
| 25 | ~~修复 P2-9 着色器 Target 输入输出映射~~ **已核实无需修改**：实际产出的 `*.hlsl.cso` 与 `Inputs`/`Outputs`/`Content` 通配完全一致 | 已确认正确 |

---


## 九、建议的 CI 骨架（可直接落地）

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push: { branches: [main] }
  pull_request: { branches: [main] }

jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }          # 供 git describe 取版本

      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }   # 依赖 P0-4

      - name: Restore (locked)
        run: dotnet restore --locked-mode          # 依赖 packages.lock.json

      - name: Vulnerability scan
        run: dotnet list package --vulnerable --include-transitive
        continue-on-error: true                    # 先告警，后收紧

      - name: Build
        run: dotnet build -c Release --no-restore

      - name: Test (headless subset)
        run: dotnet test -c Release --no-build --logger "trx;LogFileName=results.trx"

      - name: Upload test report
        if: always()
        uses: actions/upload-artifact@v4
        with: { name: test-results, path: '**/*.trx' }

      - name: Publish (portable package)
        run: pwsh -File Publish.ps1 -Version ${{ github.ref_name }}
        # 注意：Publish.ps1 需先完成 P1-7/8/10/11 改造

      - name: Checksum
        run: Get-FileHash publish/**.zip -Algorithm SHA256 | Out-File SHA256SUMS.txt
        shell: pwsh

      - uses: actions/upload-artifact@v4
        with: { name: TrueToneCap-win-x64, path: publish/ }
```

**前置依赖**：该骨架中 `global-json-file`、`--locked-mode`、`dotnet test`、`Publish.ps1 -Version`  
四项均依赖阶段一的 P0-4、P0-2、P0-10、P1-10 完成。**建议严格按阶段一 → 二的顺序推进**，  
否则 CI 会因缺少测试框架和锁定文件而无法真正生效。

---

## 十、附：本次审查的原始证据

| 检查项                 | 命令                                                  | 结果                                |
| ------------------- | --------------------------------------------------- | --------------------------------- |
| CI 配置存在性            | `ls .github/workflows`                              | 不存在                               |
| CI YAML 存在性         | `find . -name "*.yml" -o -name "*.yaml"`            | 无                                 |
| 测试框架                | `git grep -l "xunit\|NUnit\|MSTest" -- "*.csproj"`  | 无命中                               |
| 锁定文件                | `ls packages.lock.json`                             | 不存在                               |
| SDK 固定              | `ls global.json`                                    | 不存在                               |
| 中央依赖管理              | `ls Directory.Packages.props Directory.Build.props` | 均不存在                              |
| 硬编码密钥               | `git grep -iE "(api_key\|secret\|password\|token)"` | 0 命中                              |
| 硬编码本机路径             | `git grep -E "C:/" -- src scripts`                  | 1 命中（`onnx_fp16_quantize.py:103`） |
| 分支情况                | `git branch -a`                                     | 仅 `main`（无保护流程）                   |
| 仓库体积                | `du -sh .git`                                       | 170 MB                            |
| 发布目录                | `du -sh publish`                                    | 728 MB                            |
| 测试断言数               | `git grep -c "Assert("`                             | 316 处（8 文件）                       |
| CodeAnalysisRuleSet | `git grep CodeAnalysisRuleSet`                      | 5 个项目均为空值                         |
