# 构建与全量测试验证报告

> **日期**：2026-09-14
> **性质**：**真实执行**（非静态验证）。此前多份报告因环境问题判定「无法真实构建」，本轮已定位并解决。
> **结论**：恢复真实构建能力 → 修复 2 处测试断言缺陷 + 1 处解决方案配置缺陷 → **8/8 套件、373 项断言全部通过**。

---

## 一、环境根因定位（推翻旧结论）

### 旧结论（错误）

> 沙箱中 `APPDATA` 未定义 → NuGet `XPlatMachineWideSetting` 抛 `ArgumentNullException: path1`
> → `dotnet restore/build` 全部失败；且导出 `APPDATA` 也**无法**修复。

据此，此前多份文档都写了「本轮改动仅为静态验证」「锁定文件只能手工推导」。

### 真实根因

缺失的是 **`ProgramFiles` / `ProgramData` / `ALLUSERSPROFILE`**，不是 `APPDATA`。
NuGet 构造 `XPlatMachineWideSetting` 时要拼接 Program Files 路径，
`ProgramFiles` 为空 → `Path.Combine(null, …)` → `path1` 空引用。

### 修复

```bash
export APPDATA='C:\Users\<你>\AppData\Roaming'
export LOCALAPPDATA='C:\Users\<你>\AppData\Local'
export USERPROFILE='C:\Users\<你>'
export ProgramData='C:\ProgramData'          # ← 关键
export ALLUSERSPROFILE='C:\ProgramData'      # ← 关键
export ProgramFiles='C:\Program Files'       # ← 最关键
export PUBLIC='C:\Users\Public'
export SystemRoot='C:\Windows'
export SystemDrive='C:'
```

> 带括号的 `ProgramFiles(x86)` 在 bash 中不是合法标识符、无法 `export`，实测**不需要**。
> 正常 PowerShell / 开发机 / CI 中这些变量由系统预置，不会遇到此问题。

判据：`dotnet restore TrueToneCap.slnx` 输出 5 个「已还原」。

---

## 二、锁定文件：改为 NuGet 原生生成

此前 5 个 `packages.lock.json` 是从 `project.assets.json` 反推的。**实测反推版确实有偏差**：

| 项目 | 反推生成 | NuGet 原生 | 说明 |
|---|---|---|---|
| `LogViewer` | 11729 B | **7013 B** | 反推版混入多余内容 |
| `Core` | 3282 B | **3367 B** | 条目有增减 |
| TFM 键 | `net11.0-windows10.0.26100.0` | **`net11.0-windows10.0.26100`** | NuGet 使用**无末尾 `.0`** 的规范化名 |

已删除全部反推版并重新 `dotnet restore` 生成。当前状态：

| 项目 | TFM 键 | 包数 |
|---|---|---|
| App | `net11.0-windows10.0.26100` + `…/win-x64` | 29 + 5 |
| Core | `net11.0-windows10.0.26100` | 11 |
| LogViewer | `net11.0-windows10.0.26100` + `…/win-x64` | 16 + 3 |
| Test | `net11.0-windows10.0.26100` | 14 |
| Tools | `net11.0-windows10.0.26100` | 14 |

**校验**：`dotnet restore TrueToneCap.slnx --locked-mode` ✅ 通过。

> **教训**：`packages.lock.json` 必须且只能由 `dotnet restore` 生成。
> 任何手工/反推产物都不能作为最终交付 —— CI 用 `--locked-mode` 会因内容不符而失败。

---

## 三、构建结果

```
dotnet build TrueToneCap.slnx -c Release
```

| 项目 | 输出路径 | 结果 |
|---|---|---|
| Core | `bin/Release/net11.0-windows10.0.26100.0/` | ✅ |
| Test | `bin/Release/net11.0-windows10.0.26100.0/` | ✅ |
| Tools | `bin/Release/net11.0-windows10.0.26100.0/` | ✅ |
| LogViewer | `bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/` | ✅ |
| App | `bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/` | ✅ |

**0 个警告，0 个错误。**

> 路径差异印证了既有判断：`Platform` 只在 App / LogViewer 声明，
> 全局化会改变 Core / Test / Tools 的输出路径（`PlatformTarget=x64` 才是不影响路径的写法）。

### 构建前置条件

**必须先退出正在运行的 TrueToneCap**，否则其占用的输出 DLL 会导致
`MSB3027` / `MSB3021`（重试 10 次后失败）。本轮即遇到（PID 12312），退出后构建通过。

---

## 四、测试发现与修复

### 4.1 测试缺陷：ICC 烘焙断言与产品契约不符（2 处，同一根因）

**首次运行**：8 个套件中 2 个失败，失败项同名 —— `BakeIccToTarget: sRGB ICC 完整 (0B)`。

**根因是测试断言错，不是产品缺陷。** `ColorProfileProvider.BakeIccToTarget`
（`ColorProfileProvider.cs:570-573`）在**目标为 sRGB 时刻意不嵌入 ICC**：

```csharp
// 如果源和目标相同（都是 sRGB），无需转换
if (targetColorSpace.Equals("sRGB", ...) && IsSrgbProfile(sourceIcc))
{
    return (bgra, null); // sRGB 目标不嵌入 ICC
}
```

sRGB 是隐含默认色域，嵌入属冗余。而原测试对 4 个色彩空间（`sRGB`/`DisplayP3`/`BT2020`/`AdobeRGB`）
**一律**断言 `icc.Length > 128`，sRGB 那一项必然误报。

**修复**（保持可证伪，未写成恒真式）：

| 文件 | 函数 |
|---|---|
| `src/TrueToneCap.Test/ColorPipelineTests.cs` | `IccProfile_BakeToTarget` |
| `src/TrueToneCap.Test/UsabilityTests.cs` | `ColorProfileProvider_BakeIccToTarget_AllSpaces` |

均改为按契约分支：

```csharp
if (target.Equals("sRGB", StringComparison.OrdinalIgnoreCase))
    Assert($"... 不嵌入 ICC(契约) ({icc?.Length ?? 0}B)", icc is null || icc.Length == 0);
else
    Assert($"... 嵌入完整 ICC ({icc?.Length ?? 0}B)", icc is not null && icc.Length >= 128);
```

### 4.2 解决方案配置缺陷：App 在 slnx 中缺少 x64 映射

`App.csproj` 声明了 `<Platform>x64</Platform>`（影响输出路径），
但 `TrueToneCap.slnx` 只给 **LogViewer** 配了 `<Platform Project="x64" />`，**App 没有**。

解决方案构建会以**全局属性覆盖**项目内的 `Platform`，于是：

| 构建入口 | App 输出路径 |
|---|---|
| 单独构建 `App.csproj` | `bin/x64/Release/…/win-x64/` |
| 构建 `slnx`（修复前） | `bin/Release/…/win-x64/` ← 少了 `x64` 段 |

**影响评估**：`Publish.ps1` 用 `dotnet publish -o $OutputDir` 直接输出到指定目录，
不读 bin 路径；`PublishLogViewer.ps1` 已同时兼容两种路径。**故对发布无实际影响**，
属潜在一致性缺陷。已补上映射，修复后两种入口输出一致。

---

## 五、最终测试结果

命令：

```
src/TrueToneCap.Test/bin/Release/net11.0-windows10.0.26100.0/TrueToneCap.Test.exe --all
```

| # | 套件 | 断言数 | 结果 |
|:-:|---|:-:|:-:|
| 1 | 核心单元测试 | 47 | ✅ |
| 2 | 色彩管线精度 | 93 | ✅ |
| 3 | 编码管线集成 | 14 | ✅ |
| 4 | 基础设施服务 | 5 | ✅ |
| 5 | OCR 服务 | 10 | ✅ |
| 6 | 输出结构合法性 | 30 | ✅ |
| 7 | 综合可用性 | 153 | ✅ |
| 8 | WGC 捕获 + AVIF 后端验证（子进程隔离） | 21 | ✅ |
| | **合计** | **373** | **✅ 8/8 套件通过，0 失败** |

WGC 套件在真实 GPU 上执行，识别到 `NVIDIA GeForce RTX 4060 Laptop GPU`，
NVENC SDK 13.1 会话与 AV1 编码均成功；LibAom 后端编码 163.7 KB / 2.71 s 亦通过。

---

## 六、本轮改动文件

| 文件 | 改动 |
|---|---|
| `src/TrueToneCap.Test/ColorPipelineTests.cs` | ICC 契约断言修复 + PQ 两处恒真断言修复 |
| `src/TrueToneCap.Test/UsabilityTests.cs` | ICC 契约断言修复 |
| `src/TrueToneCap.App/TrueToneCap.App.csproj` | `.onnx` 改用显式 `Link`（修复模型/字典分家，见 §9.3） |
| `TrueToneCap.slnx` | 补 App 的 `<Platform Project="x64" />` 映射 |
| `src/*/packages.lock.json` ×5 | 删除反推版，改由 NuGet 原生生成 |
| `docs/dependency-management.md` | §7 更正根因（APPDATA → ProgramFiles） |
| `docs/dependency-inventory-2026-09-14.md` | §8.3 遗留事项标记已解决，附反推版差异实测 |
| `docs/cicd-pipeline-review-2026-09-14.md` | 验证限制说明加「已解除」更正 |
| `docs/re-audit-2026-09-14.md` | 加真实构建补验结论 |

---

## 七、发布链路验证（部分完成）

`Publish.ps1` 的前置校验与我本轮新增的锁定文件校验**已真实执行并通过**：

```
[0/6]   验证依赖完整性 → ✅ 工具 4 项 / 模型 3 项
        （模型尺寸显示 29.7 / 36.6 MB，与 PACKAGE.md 本轮修正值一致）
[0.5/6] 校验依赖锁定文件 → ✅ 依赖锁定文件齐全 (5 个项目)   ← 本轮新增逻辑
[1/6]   准备输出目录 → ✅
```

`[2/6]` 的实质内容（`dotnet publish`）在 bash 中单独验证通过：

```
dotnet publish src/TrueToneCap.App/TrueToneCap.App.csproj -c Release -r win-x64 \
    --self-contained true -p:WindowsAppSDKSelfContained=true -o <输出目录>
→ TrueToneCap.App -> ...\TrueToneCap-0.3.4-beta\
→ [TrueToneCap] ✅ 目录整理完成        ← csproj 的 CopyPriAndXbf Target 已执行
```

产物 557 个文件 / 318 MB，`TrueToneCap.pri`、`data/Models/`（3 个模型）、
`data/Shaders/` 均已就位。

### 未能完成的部分及原因

`[3/6]` 原生工具链复制、`[4/6]` 模型复制、`[5/6]`、`[6/6]` 校验和与 `build-info.json`
**未执行** —— 沙箱的 PowerShell 会话**完全无法启动 dotnet**
（即便 PATH 含 `C:\Program Files\dotnet\`、exe 存在、且已补齐
`ProgramFiles`/`ProgramData`/`APPDATA`，`dotnet --version` 仍返回空）。
故 `Publish.ps1` 无法在该会话中跑完 `[2/6]` 之后的步骤。

> 该目录下的半成品产物（缺 `native/` 工具链）已清理，未留存，
> 以免被误当成可用的发布包。

### 附带发现：模型复制存在冗余（无害）

`App.csproj` 已通过 `Content ... Link="data\Models\..."` 把模型与着色器拷进 `data/`，
`Publish.ps1` 的 `[4/6]` 又从同一源复制一次。属**冗余的防御性拷贝**，
结果一致、无副作用，暂不改动。

---

## 八、测试断言可证伪性审计

对 `src/TrueToneCap.Test` 下全部 `Assert` 调用做了扫描，结论：

- **绝大部分 `Assert(name, true)` 是合格写法**，属于两类：
  1. **try/catch 冒烟型** —— 异常路径会 `Assert(..., false)`，正常路径 `true` 语义正确
     （`ServiceTests.NativeLibraryResolver`、`UsabilityTests` 的 0x0 / 空数据 / 崩溃边界）；
  2. **循环前置型** —— 循环内任一点超差即 `Assert(false)` 并 return，
     能走到末尾的 `true` 说明全部通过（`ColorPipelineTests.PqCurve_Symmetric`）。
- **发现 2 处真正的缺陷，已修复**（`ColorPipelineTests.cs`）：

| 位置 | 问题 | 修复 |
|---|---|---|
| `PqCurve_KnownValues` | `if (i == 0) { Assert("PQ 精度: 0 nits → 0.0", true); continue; }`<br>名字声称校验 0 nits→0.0，实际断言字面量 `true`，什么都没查 | 改为真断言 `pq0 >= 0 && pq0 < 1e-4` |
| `PqCurve_ExtremeValues` | `Assert("PQ 0→0", v0 == 0f \|\| float.IsNaN(v0) == false)`<br>等价于"不是 NaN"，与名字不符，NaN 外任何值都通过 | 改为 `v0 >= 0f && v0 < 1e-4f` |

> **为什么容差是 1e-4 而不是 0**：PQ 公式在 0 处有固有偏置，实测
> `LinearToPq(0f) = 7.30956E-07`（与理论值 ~7.3e-7 吻合）。
> 原作者写弱断言正是因为这里不精确为 0 —— 但正确做法是给出显式容差，而非退化成恒真式。

## 九、Debug 配置与实机验证

### 9.1 Debug 构建与测试

```
dotnet build TrueToneCap.slnx -c Debug   → 5/5 项目，0 警告 0 错误
测试 --all（Debug）                       → 8/8 套件，373 项断言，0 失败
```

与 Release 结果完全一致。

### 9.2 实机冒烟测试

从 Release bin 输出启动 `TrueToneCap.exe`，进程稳定存活（PID 67692，占用 427 MB），
日志显示：

- **ONNX Runtime 会话创建成功** → 检测/识别模型与字典均已加载
- **NVENC SDK 13.1 会话就绪**，AV1 编码可用
- 仅有 libpng 的 `iCCP: known incorrect sRGB profile` 警告（PNG 元数据层面，非致命）

### 9.3 冒烟测试暴露的真实缺陷：模型与字典在 bin 输出中被拆到两个目录

**现象**：build 输出（Debug 与 Release 均如此）中：

```
Models/PP-OCRv6_medium_det.onnx      ← 根目录
Models/PP-OCRv6_medium_rec.onnx
data/Models/ppocrv6_dict.txt         ← data 目录
```

而 `OnnxOcrEngine.FindFirstExisting` **只在单一目录**内查找全部候选文件，
`LoadModels()` 要求模型与字典同时命中。于是从 bin 输出运行（IDE 调试 / `dotnet run`）
时字典必然找不到 → `return false` → OCR 完全不可用。
**仅 publish 产物正常**，因为 `CopyPriAndXbf` Target 会把根 `Models/` 兜底移动到 `data/Models/`。

**根因**：`App.csproj` 对 `.txt` 用了显式 `Link="data\Models\..."`，
对 `.onnx` 却用了 `LinkBase="data\Models\"` —— 而该文件第 45 行的注释自己就写明
「WinUI 构建目标下 LinkBase 对 Content 未生效」。

**修复**：`.onnx` 改用与 `.txt` 相同的显式 `Link`。

**验证**：修复后 Debug / Release 的 bin 输出与 publish 输出中，
3 个文件全部位于 `data/Models/`，根 `Models/` 不再产生；重新 publish 布局仍正确。

---

## 十、遗留事项

- `publish/PLAN/tools/` 解除跟踪（27 MB）与历史重写（回收 ~132 MB 旧代模型）**未执行** ——
  两者都会改动 git SHA，需单独安排。
- CI 系统（P0-1）按此前约定本轮不做。
- 你 2026-09-13 那批未提交的功能改动（11 个文件，含 `NvEncoderNative.cs` 重写）
  **已通过本次真实构建与全量测试**，无回归。
