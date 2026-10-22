# AOT 发布可行性 + 微软商店上架分析

> 日期：2026-10-07
> 方法：**实测**（真实执行 AOT / Trim 发布，收集编译器诊断）+ 源码审计 + 包内容清点。
> 本文所有"阻塞项"都有具体的错误码与文件行号，不是推测。

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| **能否 AOT 发布？** | ⚠️ **技术上不是不可能，但被 WinUI 卡死** —— 见 §1.4。修复自有代码只需 ~1h，但**上游 WinUI/WinAppSDK 不声明 AOT 支持**，这是硬边界 |
| **建议怎么做？** | **不要追 NativeAOT**。改用 **ReadyToRun（现状，已启用）+ 修掉那些 IL 警告**。理由见 §2.3 |
| **能否上架微软商店？** | ✅ **可以**。包体 412 MB 远低于 25 GB 上限；无管理员权限要求；**但需重做 3 处架构假设** —— 见 §3 |
| **商店上架的工作量** | 中等：新增 MSIX 打包配置 + 改造 3 处（启动项/快捷方式/临时文件） + Partner Center 流程 |

**一句话**：AOT 的瓶颈不在你的代码，在 WinUI；而商店上架真正的难点不是"能不能打包"，是**当前代码里有 3 处把"未打包应用"当成前提的假设**。

---

## 1. NativeAOT 可行性：实测结果

### 1.1 现状：仓库里已有一个**不起作用的** AOT 开关

`Publish.ps1:380-392` 有一个 `-CoreAot` 开关，源码注释已如实说明它**从未生效**：

```
⚠️ 2026-09-15 更正：这个开关此前【完全没有生效】。
   全仓 grep `CorePublishAot` 只有本文件这一处产出，没有任何 csproj 消费它；
   `PublishAot` / `PublishTrimmed` / `TrimMode` / `IsAotCompatible` 在各 csproj 中零命中。
   即：下面这行传下去后无人读取，实际产出的仍是 JIT + ReadyToRun，属于典型的"假生效"。
```

**实测确认**：全仓 `PublishAot` / `PublishTrimmed` / `PublishReadyToRun` **零命中**（除 Publish.ps1 传入的那个无人消费的属性）。当前发布 = **JIT + ReadyToRun 复合映像**。

### 1.2 仓库已经做对的部分（AOT 就绪度比想象中高）

| 项 | 状态 |
|---|---|
| `TrimmerRoots.xml` | ✅ 已存在，为 SharpGen/Vortice 的 COM vtable 反射保留类型 |
| JSON 源生成 | ✅ `AppJsonContext` / `LlmJsonContext` 已是源生成（AOT 必需） |
| `AllowUnsafeBlocks` | ✅ 已开 |
| `Deterministic` | ✅ 已开 |
| `TreatWarningsAsErrors` | ✅ 已开 —— **这正是 AOT 试发布时能立刻列出全部阻塞项的原因** |

### 1.3 实测：AOT 发布的两层阻塞

直接跑 `-p:PublishAot=true`，编译器给出了具体清单（**已实测，非推断**）：

**第 1 层：自有代码的反射（4 处诊断 / 2 个文件）**

| 错误 | 位置 | 原因 |
|---|---|---|
| `IL2026` ×2 | `AvifHardwareProbe.cs:182`、`AvifEncoderProbe.cs:280` | `Assembly.GetTypes()` 标记为 `RequiresUnreferencedCode` |
| `IL2075` ×2 | `AvifHardwareProbe.cs:183`、`AvifEncoderProbe.cs:281` | `Type.GetField(...)` 的返回值的注解不满足要求 |

**⚠️ 实测发现：这 4 处反射其实是【不必要的】**：

```csharp
var asm = typeof(AvifHardwareProbe).Assembly;
var t = asm.GetType("TrueToneCap.Core.Encoding.MftAvifEncode", throwOnError: false)
         ?? asm.GetTypes().FirstOrDefault(x => x.Name == "MftAvifEncode");   // ← IL2026
var f = t?.GetField("LastEncodeUsedHardware", ...);                          // ← IL2075
return f?.GetValue(null) is bool b && b;
```

而目标字段在**同一个程序集**里、且是 `internal`：

```csharp
// FormatEncoders.cs
internal static class MftAvifEncode
{
    internal static volatile bool LastEncodeUsedHardware;   // ← internal，同程序集可直接访问
}
```

⇒ **反射是多余的间接层**。`AvifHardwareProbe`、`AvifEncoderProbe`、`MftAvifEncode` **三者都在 `TrueToneCap.Core/Encoding/`**，
直接 `MftAvifEncode.LastEncodeUsedHardware` 即可，**零反射、零 IL 警告、行为完全等价**。
（原注释写"经反射读取"是为了容忍"旧构建里类型不存在"，但同程序集内类型必然存在 —— 该防御是无效的。）

**第 2 层：WinUI 的 COM 互操作（3 处，且修完第 1 层才暴露）**

把 IL 警告临时压制后，继续出现：

| 错误 | 位置 | 原因 |
|---|---|---|
| `CsWinRT1033` ×3 | `ToastService.cs:378 / 384 / 395` | `[ComImport]` 接口在 AOT 下与 CsWinRT 对象不兼容 |

```
error CsWinRT1033: The type '...IShellLinkW' used in a cast operation is a [ComImport] interface,
which is not compatible with CsWinRT objects in AOT scenarios.
Consider using the COM generators to define the interface, or manually handling the interface
query on the underlying native object.
```

这段代码是 `EnsureAppShortcut()` —— 手动用 `IShellLinkW`/`IPropertyStore`/`IPersistFile`
创建带 AUMID 的开始菜单快捷方式。**它在 AOT 下需要改写为 COM 生成器或手工 vtable 调用**。

> 📌 注意：这段代码在**下面商店章节里还要改**（MSIX 自动提供 AUMID 与快捷方式，
> 手工建快捷方式既不允许也没必要）。两件事可以一并处理。

### 1.4 真正的硬边界：WinUI 不声明 AOT 支持

这是**决定性**的一条，也是为什么我建议不要追 AOT：

| 证据 | 结果 |
|---|---|
| `Microsoft.WinUI.dll` 内 `IsAotCompatible` 属性 | **0 处** |
| 同上 `RequiresDynamicCode` / `RequiresUnreferencedCode` | **0 处** |
| `Microsoft.WindowsAppSDK` 的 `.props`/`.targets` 中 `PublishAot`/`IsAotCompatible`/`ILLink` 处理 | **0 处** |
| 包内 `lib/` 目标框架 | `net6.0-windows10.0.17763.0`（managed projection，非 AOT 专用构建） |
| `Microsoft.WindowsAppSDK` nuspec 中的 AOT 声明 | **无** |

**含义**：WinUI 是一个**基于反射/COM projection 的 XAML 框架**，它既没有声明 AOT 兼容，
也没有提供 AOT 专用的构建路径。即使你把自有代码的 7 个诊断全修掉，**仍要承担**：
XAML 运行时绑定、`Microsoft.UI.Xaml` 的反射式类型解析、CsWinRT 投影在 AOT 下的行为
**全部属于"未声明支持"的状态** —— 能编译通过 ≠ 能正确运行。

⇒ **判定：NativeAOT 对 WinUI 3 应用目前不是一条可靠路径。** 这不是本项目的代码质量问题，是上游边界。

---

## 2. 那应该怎么做？

### 2.1 立即价值：把 IL 警告修掉（无论是否上 AOT）

那 4 处反射**本来就该修** —— 它是一层无用的间接，且在**任何**裁剪/AOT 场景下都会爆。
修完的收益：
- 为将来任何裁剪场景扫清自有代码障碍
- 移除一处"反射查同程序集类型"的反模式
- 让 `PublishAot` 的错误清单只剩 WinUI 的部分，边界更清晰

### 2.2 可选：开启 `IsAotCompatible` 作为**持续门禁**（不真的 AOT 发布）

在各 csproj 设 `<IsAotCompatible>true</IsAotCompatible>` —— 它**不改变产物**，
只让编译器**持续**对新增代码报 IL 警告。配合已有的 `TreatWarningsAsErrors=true`，
等于把"AOT 就绪度"变成一条会一直守着的门禁。

> ⚠️ 但要注意：WinUI 自身未声明兼容，开这个开关会**在 WinUI 相关调用点**持续报警。
> 建议**先只对 `TrueToneCap.Core` 开**（Core 不依赖 WinUI），App 暂不开。

### 2.3 建议的发布形态：维持 ReadyToRun（现状）

| 方案 | 启动 | 体积 | 依赖 | 本仓适用 |
|---|---|---|---|---|
| **JIT** | 慢 | 小 | 需 .NET 运行时 | — |
| **R2R（现状）** | 快 | +122 MB（复合） | 自包含 | ✅ **保持** |
| **R2R + 裁剪** | 快 | 更小 | 自包含 | 🟡 可评估，需先修 4 处反射 |
| **NativeAOT** | 最快 | 最小 | 无 | ❌ WinUI 不支持 |

**R2R 已经拿到了"启动快"这个主要收益**，而 AOT 的额外收益（内存、无运行时依赖）
在 WinUI 应用上会被上游不支持的风险吃掉。

> 💡 若想减小体积，**裁剪（trimming）比 AOT 现实得多**：实测裁剪**只**卡在那 4 处反射
> （比 AOT 少了 CsWinRT 那 3 处），修完即可试。但需完整回归 —— 裁剪会静默删掉反射可达的代码。

---

## 3. 微软商店上架

### 3.1 现状：完全未做 MSIX 打包

| 检查项 | 现状 |
|---|---|
| `WindowsPackageType` | **`None`**（未打包应用） |
| `Package.appxmanifest` | **不存在**（MSIX 必需的清单文件） |
| `app.manifest` | 存在，但那是 **Win32 兼容性清单**（DPI/OS 版本），与 MSIX 无关 |
| 发布形态 | 自包含 unpackaged，靠 `Publish.ps1` 拷目录 + zip |

⇒ 上架商店需要**新建 MSIX 打包配置**，这是从零开始的一块。

### 3.2 好消息：几个常见的商店障碍本仓都没有

| 潜在障碍 | 本仓情况 |
|---|---|
| 包体超限 | ✅ **412.7 MB**，远低于商店 25 GB 上限（但见 §3.4 的体积优化建议） |
| 需要管理员权限 | ✅ `app.manifest` **没有** `requireAdministrator` |
| 需要提权才能跑的功能 | ⚠️ NVENC/AVIF 编码器是进程内调用，不需提权 |
| 不支持的语言/框架 | ✅ .NET 11 + WinAppSDK 2.5.1，商店支持 |
| 网络 API 依赖 | ✅ 翻译功能走 LLM API（HTTP，属正常出站，不需特殊 capability） |

### 3.3 🔴 必须改造的 3 处架构假设（真正的难点）

当前代码**把"未打包应用"当成前提**，上 MSIX 后会静默失效或报错：

**① 开机自启：注册表 `HKCU\...\Run` → 必须改用 StartupTask**

```csharp
// StartupManager.cs:12,53-54
private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? ...
```

MSIX 下 HKCU 写入被**重定向到包专属 hive**，写进去的 `Run` 项**对系统启动无效**。
正确做法是在 `Package.appxmanifest` 里声明：

```xml
<Extensions>
  <uap5:Extension Category="windows.startupTask" Executable="TrueToneCap.exe"
                  EntryPoint="Windows.FullTrustApplication">
    <uap5:StartupTask TaskId="TrueToneCapStartup" Enabled="false"
                      DisplayName="TrueToneCap" />
  </uap5:Extension>
</Extensions>
```
并在代码里改用 `StartupTask.GetAsync("TrueToneCapStartup")` 启停。

**② 开始菜单快捷方式：手工 `.lnk` → 应删除（MSIX 自动提供）**

```csharp
// ToastService.cs:361-402 EnsureAppShortcut()
// 手工建 .lnk 并把 AUMID 写进 PKEY_AppUserModel_ID
```

MSIX 包由系统**自动**创建开始菜单入口并分配 AUMID ⇒ 这段代码：
- 在打包环境下**多余**（MSIX 已提供）
- 且**是 §1.3 那 3 个 `CsWinRT1033` 错误的来源**
⇒ **上商店时直接删掉这段**，一举解决两个问题（AOT 阻塞 + MSIX 冲突）。

**③ 临时文件子进程架构：需实测确认**

应用有 **45 处 `GetTempPath()`**，把 PNG/PPM 写到 `%TEMP%` 再交给
`avifenc`/`cjxl`/`cwebp`/`cjpegli` 子进程。

- MSIX 的**文件系统重定向**主要影响 `AppData` 的写入；`%TEMP%` 通常**仍可正常读写**
- 但**原生 exe 必须随包分发且位于包内可执行路径**，子进程继承包身份，**需要实测**
- ⚠️ 另需确认：包内文件不可写 → 若代码往**安装目录**写临时文件会失败

### 3.4 商店上架的完整步骤

**阶段 A — 打包能力（先决条件）**

1. 新增 `Package.appxmanifest`：身份（Publisher=`CN=<Partner Center 名称>`）、
   显示名、能力、`windows.startupTask` 扩展
2. csproj 增加 `<EnableMsixTooling>true</EnableMsixTooling>`、
   `<AppxPackageSigningEnabled>`、`<GenerateAppxPackageOnBuild>`
3. **为商店正确设置部署模式**：商店包应为
   **framework-dependent**（`WindowsAppSDKSelfContained=false`）以减小体积 ——
   但本仓现在是 `true`，且 `Publish.ps1` 也是。**需评估**：改 false 要求用户机器有
   WinAppSDK runtime（商店可自动满足）。
4. 处理 `Publish.ps1` 的目录布局假设（MSIX 不用裸目录，用包）

**阶段 B — 代码改造（§3.3 的三处）**

5. `StartupManager` 改用 StartupTask API（保留非打包回退路径，便于继续发绿色包）
6. 删除 `EnsureAppShortcut()`（或仅在未打包时执行）
7. 实测子进程 + `%TEMP%` 在 MSIX 沙箱内的行为

**阶段 C — Partner Center**

8. 注册开发者账号（个人 $19 / 公司 $99，一次性）
9. 预留应用名称 → 拿到**保留的产品标识**（Publisher DN 必须与清单一致）
10. 商店图标/截图/描述/隐私政策 URL（**必须**提供隐私政策 —— 因为会发 HTTP 请求）
11. 提交 `-Store` 签名的 `.msixupload`（可由 VS 或 `msbuild -t:Publish` 生成）

**阶段 D — 商店特有约束**

12. ✅ **`SecretStore` 的写入位置已核查，MSIX 安全**：它写
    `%LOCALAPPDATA%\TrueToneCap\secrets.bin`（`SecretStore.cs:38-40`），
    MSIX 会把 `LocalApplicationData` 重定向到包专属容器 ⇒ **合规、可写**。
    用的是 P/Invoke `CryptProtectData`（DPAPI），MSIX 下可用。
13. ✅ **无自更新逻辑，不冲突**：已逐仓检索"下载并替换 exe / releases/latest / 检查更新"
    等模式，**零命中** ⇒ 不存在"MSIX 禁止自更新"的冲突。（该功能本来也没有。）
14. ⚠️ **原生 exe 随包分发需符合商店政策**（本仓 4 个 exe 均为开源许可，
    已在 `licenses/` 台账 + `THIRD-PARTY-NOTICES.md` 留痕 —— **合规方面已就绪**）。

### 3.5 ⚠️ 体积优化：现有发布目录还留着升级前的旧二进制

清点 `publish/TrueToneCap-v0.3.7-beta/` 时发现 `native/cjpegli.exe` 是 **5.13 MB** ——
那是 **cjpegli 自编译改造之前**的旧件（新件 0.72 MB）。说明该发布目录**早于本次依赖升级**，
不是当前源码的产物。上架前必须先重新发布，否则会把旧二进制（含已弃用的依赖版本）打进商店包。

体积构成（412.7 MB）：

| 组成 | 体积 | 占比 | 说明 |
|---|---|---|---|
| `TrueToneCap.r2r.dll` | 122.4 MB | 29.7% | 复合 R2R 映像（把全部依赖的原生码合并成一个） |
| OCR 模型（det+rec） | 66.3 MB | 16.1% | 必需 |
| `Microsoft.Windows.SDK.NET.dll` | 24.9 MB | 6.0% | WinRT 投影 |
| `onnxruntime.dll` + `DirectML.dll` | 38.3 MB | 9.3% | 推理运行时 |
| 原生工具 4 个 exe | ~26 MB | 6.3% | 升级后应略降（cjpegli -4.4 MB） |

**若想显著瘦身**，最有效的是**去掉复合 R2R**（`PublishReadyToRunComposite=false`）——
复合映像把所有依赖合成一个 122 MB 的巨大文件，改用非复合 R2R 可让它回到单个小文件之和，
但**启动时间会略变差**。这属于**取舍**，需实测后决定，不建议为上架而仓促改。

---

## 4. 建议的执行顺序

| 优先级 | 动作 | 理由 | 工作量 |
|---|---|---|---|
| **P0** | 修掉 4 处反射（改用同程序集直访） | 无论 AOT/裁剪/上架都受益；是纯改进 | ~1h |
| **P0** | 删除/条件化 `EnsureAppShortcut()` | 同时解掉 AOT 阻塞与 MSIX 冲突 | ~1h |
| **P1** | 对 `TrueToneCap.Core` 开 `IsAotCompatible` 门禁 | 让 AOT 就绪度被持续守住（不改变产物） | ~0.5h |
| **P2** | 评估 `PublishTrimmed` + 完整回归 | 比 AOT 现实的体积优化路径 | ~1 天 |
| **P2** | 新建 `Package.appxmanifest` + MSIX 打包 | 上架的先决条件 | ~1 天 |
| **P3** | `StartupManager` 改 StartupTask + 沙箱实测 | 上架的真正难点 | ~1 天 |
| **P3** | Partner Center 注册与提交 | 流程性工作 | 视审核而定 |

**关键提醒**：P0 两项**独立于是否上商店、是否做 AOT**，都值得做 ——
它们移除的是反模式（无谓反射）和打包环境下的冲突代码。

---

## 5. 复现方式

```powershell
# 1) AOT 试发布（复现本文的两层阻塞）
dotnet publish src/TrueToneCap.App/TrueToneCap.App.csproj `
  -c Release -r win-x64 -p:Platform=x64 `
  -p:PublishAot=true -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true -o $env:TEMP/aot-probe
# => IL2026 x2 + IL2075 x2（2 个文件）

# 2) 压制 IL 警告后看第二层
#    （注意 PowerShell 里分号要转义为 %3B）
dotnet publish ... "-p:NoWarn=IL2026%3BIL2075"
# => CsWinRT1033 x3（ToastService.cs）

# 3) 裁剪试发布（只卡在第一层）
dotnet publish ... -p:PublishTrimmed=true

# 4) 确认 WinUI 不声明 AOT 支持
#    在 Microsoft.WinUI.dll 中检索 IsAotCompatible / RequiresDynamicCode → 均 0 处

# 5) 现状确认：零 AOT 配置
Select-String -Path (Get-ChildItem -Recurse -Filter *.csproj src).FullName -Pattern "PublishAot|PublishTrimmed|IsAotCompatible"
```

**关键文件定位**

| 关注点 | 位置 |
|---|---|
| 形同虚设的 AOT 开关 | `Publish.ps1:380-392` |
| 裁剪根配置（已存在） | `TrimmerRoots.xml` |
| **IL2026/IL2075 来源** | `AvifHardwareProbe.cs:180-185`、`AvifEncoderProbe.cs:278-283` |
| 被反射的目标（可直接访问） | `FormatEncoders.cs` 的 `MftAvifEncode.LastEncodeUsedHardware` |
| **CsWinRT1033 来源** | `ToastService.cs:361-402`（`EnsureAppShortcut`） |
| 开机自启（MSIX 需改造） | `StartupManager.cs:12,53-54` |
| 打包类型 | `TrueToneCap.App.csproj:14`（`WindowsPackageType=None`） |
| 自包含开关 | `TrueToneCap.App.csproj:8,15` |
| 发布产物 | `publish/TrueToneCap-v0.3.7-beta/`（412.7 MB） |

---

## 6. 待确认（本文未验证的部分）

诚实标注，避免把推断当结论。**已在本次核查中解决的，移到 §3.3/§3.5 并标 ✅**：

1. **MSIX 沙箱内子进程 + `%TEMP%` 的实际行为** —— 需要真实打包后实测
   （结论依赖 Windows 版本与包能力声明）。这是**上架前唯一必须实测**的技术点。
2. **商店对"截图/录屏类"应用的具体政策要求** —— 可能涉及隐私政策中对屏幕内容的说明
3. **`Microsoft.WindowsAppSDK` 后续版本是否会正式支持 AOT** —— 本文结论基于 2.5.1；
   上游若宣布支持需重新评估（这是唯一会让 §1.4 结论翻转的因素）

**本次已核查并关闭的开放项**：
- ✅ `SecretStore` 写 `%LOCALAPPDATA%`，MSIX 重定向后安全可写
- ✅ 无自更新逻辑，不存在 MSIX 冲突
- ✅ 包体 412.7 MB 远低于商店上限；无 `requireAdministrator`
