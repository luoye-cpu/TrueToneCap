# 完整验证报告（独立复跑）

> 日期：2026-10-07
> 方法：**删除全部 obj/bin 后从零重建**，不依赖任何既有产物；逐项复跑并复核我自己写下的每条声明。
> 结论：**验证通过**，但在验证过程中**发现并修正了 2 处"判据没有牙"与 1 处文档数字过期**。

---

## 0. 结论摘要

| # | 验证项 | 结果 |
|---|---|---|
| 1 | 从零干净构建（5 项目） | ✅ **0 警告 0 错误** |
| 2 | CI 关键路径：`--locked-mode` 还原 | ✅ 通过 |
| 3 | 8 套件测试 | ✅ **1872 通过 / 0 失败**（2 跳过） |
| 4 | `--selftest`（真实图像管线） | ✅ 通过，产出真实文件 |
| 5 | MSIX 构建 + 内容门禁 | ✅ 150.91 MB，门禁全绿 |
| 6 | **我写的代码确实进了交付物** | ✅ 反编译核实（见 §4） |
| 7 | WACK（商店认证同一套） | ✅ `OVERALL_RESULT=PASS`，**0 项阻断性失败** |
| 8 | `Publish.ps1` 两种模式 | ✅ 均已复跑（数字需更正，见 §6） |
| 9 | 仓库卫生 | ✅ 构建产物已不被跟踪，规则生效 |
| 10 | **变异测试（判据是否真有牙）** | ⚠️→✅ 查出 **2 条无牙判据并修好** |
| 11 | 文档声明与事实一致性 | ⚠️→✅ 更正 **31 处过期体积数字** |

> **本次验证最大的价值不是"全绿"，而是抓出了 3 个问题**：
> 2 条判据看似有保护、实则变异后仍全绿；1 组体积数字在后续改动后已过期。

---

## 1. 从零干净构建

先删除**全部** `obj/`、`bin/`、`AppPackages/`，排除陈旧产物干扰。

| 项目 | 错误 | 警告 | 结果 |
|---|---|---|---|
| `TrueToneCap.Core` | 0 | 0 | ✅ |
| `TrueToneCap.LogViewer` | 0 | 0 | ✅ |
| `TrueToneCap.App` | 0 | 0 | ✅ |
| `TrueToneCap.Tools` | 0 | 0 | ✅ |
| `TrueToneCap.Test` | 0 | 0 | ✅ |

> 仓内 `TreatWarningsAsErrors=true` ⇒ **"0 警告"是硬性要求**，不是好看。

### 1.1 CI 关键路径

```powershell
dotnet restore TrueToneCap.slnx --locked-mode   # => exit 0
```

`RestorePackagesWithLockFile=true` + CI 用 `--locked-mode` ⇒ 这条不过，CI 直接红。
另外核实 **`packages.lock.json` 与 HEAD 一致**（我的改动没有引起依赖漂移）。

---

## 2. 测试与自检

| 套件 | 通过 | 跳过 |
|---|---|---|
| `--unit-tests` | 1276 | 1 |
| `--color-tests` | 267 | 0 |
| `--encoding-tests` | 14 | 0 |
| `--service-tests` | 2 | 1 |
| `--ocr-tests` | 12 | 0 |
| `--format-tests` | 43 | 0 |
| `--usability-tests` | 225 | 0 |
| `--wgc-avif-tests` | 33 | 0 |
| **合计** | **1872** | **2** |

**失败：0。**

### 2.1 `--selftest`（真实管线，不是模拟）

```
[selftest] 分类: ok=10 skip=1 missingBackend=0 fail=0
```

产出**真实文件**（证明编码链路真的在工作，而非只报状态）：

| 文件 | 大小 |
|---|---|
| `selftest_TIFF.tiff` | 30,223 B |
| `selftest_JPEG_LI.jpg` | 1,233 B |
| `selftest_AVIF.avif` | 536 B |
| `selftest_PNG.png` | 483 B |
| `selftest.json` | 6,240 B |

> `skip=1` 是 **HDR 源**：当前显示器 `SupportsHdr=no`（非 HDR 屏）⇒ 设计内的跳过，非失败。

---

## 3. MSIX 构建与内容门禁

```
MSIX: 150.91 MB      包内文件数: 566
```

门禁 `scripts/verify-msix.ps1` **全部通过**：

| 检查 | 结果 |
|---|---|
| 4 枚原生工具（`avifenc`/`cjxl`/`cjpegli`/`cwebp`） | ✅ |
| 3 项 OCR 资产（det / rec / dict） | ✅ |
| `TrueToneCap.exe` | ✅ |
| **`PRIVACY.md`**（商店政策 10.5.1） | ✅ 8.0 KB |
| **`THIRD-PARTY-NOTICES.md`** | ✅ 389.4 KB |
| 版本第四段为 0（商店要求） | ✅ |
| `runFullTrust` 能力 | ✅ |
| `startupTask` 已声明 | ✅ `TaskId=TrueToneCapStartup` |
| 无 `.pdb`/`.ppm`/`.tmp` 残留 | ✅ |

---

## 4. 关键验证：**我写的代码确实进了交付物**

这一项是本次验证里我认为最有价值的 —— **"源码改了"不等于"包里是新的"**。
我把 MSIX 解包，用 **`System.Reflection.Metadata` 真读程序集**（不是字符串 grep）：

### 4.1 应当存在的（我的实现）

| 项目 | 位置 | 结果 |
|---|---|---|
| `TrueToneCapStartup`（const 字面量） | `TrueToneCap.dll` #US 堆 | ✅ FOUND |
| `Windows.ApplicationModel.StartupTask` | TypeRef | ✅ |
| `Windows.ApplicationModel.StartupTaskState` | TypeRef | ✅ |
| `RequestEnableAsync` / `Disable` / `AsTask` | MemberRef | ✅ |
| `PackageRuntime` / `IsPackaged` / `GetCurrentPackageFullName` | `TrueToneCap.Core.dll` | ✅ |
| `TranslationSnapshot` / `_workCts` | `TrueToneCap.dll` | ✅ |

### 4.2 应当**不存在**的（我删掉的反射）

| 项目 | 结果 |
|---|---|
| `MakeGenericMethod`（ldstr 字面量） | ✅ **absent** |
| `ResolveStartupTaskType` | ✅ **absent** |

⇒ **反射版的 3 个 bug 所依赖的代码路径，在交付物里已经不存在。**

> 📌 顺带纠正一个**我先前的错误结论**：我第一次用"字节扫描 + 找 UTF-16"的方式查
> `TrueToneCapStartup`，得到"未找到"，一度怀疑语法糖内联。改用
> `MetadataReader` 枚举 `ldstr` 操作数后确认**它就在 #US 堆里**。
> **教训：字节级 grep 查托管程序集不可靠，要用元数据读取器。**

---

## 5. WACK（商店认证用的同一套）

```
OVERALL_RESULT = PASS
APP_TYPE       = Centennial        APP_NAME/VER = TrueToneCap 0.3.7.0
tests=24   non-PASS=2   blocking failures (OPTIONAL != TRUE) = 0
```

两项 `FAIL` 均为 **`OPTIONAL="TRUE"`**（不阻断，`OVERALL_RESULT` 仍为 PASS）：

| 测试 | 原因 | 归属 |
|---|---|---|
| 一般元数据正确性 | Win2D 的 `Microsoft.Graphics.Canvas.winmd` 引用了 WinAppSDK 类型，WACK 独立解析时找不到 | **上游 Win2D 的元数据问题**，非本仓引入 |
| 已阻止的可执行文件 | ① 本应用**确实会启动编码器子进程**（核心功能）；② `DirectML.dll`/`onnxruntime.dll` 里的 `reg`/`cmd` 属**字节串误报** | 设计使然 + 误报 |

---

## 6. ⚠️ 验证发现的问题 ①：**体积数字已过期**

复跑 `Publish.ps1` 两种模式，发现与我在文档里写的数字**不符**：

| 模式 | 我文档里写的 | **本次实测** | 差异 |
|---|---|---|---|
| `dotnet build`（自包含） | 150.90 MB | **150.91 MB** | +0.01 |
| `dotnet publish`（自包含，默认） | 175.82 MB | **175.85 MB** | +0.03 |
| `dotnet publish`（**框架依赖**） | 84.57 MB | **85.12 MB** | **+0.55** |

**原因**（不是回归，是真实的代码增长）：这些数字测于实现当日，**之后同日又加入了**
隐私政策窗口、`MarkdownDocumentWindow`（360 行）、`TranslationSnapshot`、`PackageRuntime`，
并把 `StartupManager` 从反射改为**强类型引用 `StartupTask`/`StartupTaskState`** ——
后者引入 WinRT 投影的 TypeRef/MemberRef 元数据，反射版没有这些引用。

⇒ **真实收益：175.85 → 85.12 MB = −90.73 MB（−51.6%）**（原记 −52%，差异可忽略）

**已处理**：在 4 份文档里更正/加注（31 处命中），并在
`docs/reviews/msix-store/msix-size-and-publish-pipeline-analysis-2026-10-07.md` 新增 §9 记录**测量时点与漂移原因**，
而不是把旧数字悄悄改掉 —— 体积数字必须能追溯到"什么时候量的"。

---

## 7. ⚠️ 验证发现的问题 ②③：**两条判据没有牙**（最重要）

我写了一个变异测试脚本 `scripts/mutation-verify.ps1`：把关键实现**故意改坏**，
期望单元测试报红；**若仍全绿，说明这条判据是假保护**。

### 第一轮结果（发现问题）

| 判据 | 变异 | 结果 |
|---|---|---|
| StartupManager: 禁反射 | 塞回 `MakeGenericMethod` | ✅ RED |
| StartupManager: TaskId | 改成 `"Wrong"` | ✅ RED |
| Preview: cancel rethrow | 去掉 `{ throw; }` | ✅ RED |
| **Preview: snapshot** | 改类型名 | ❌ **GREEN —— 没有牙** |
| Settings: packaged branch | `IsPackaged` → `false` | ✅ RED |
| **LogService: packaged branch** | `IsPackaged` → `false` | ❌ **GREEN —— 没有牙** |

### ② 快照判据被"前缀关系"击穿

我原先写的是：

```csharp
code.Contains("record TranslationSnapshot") && code.Contains("_translation = new TranslationSnapshot(")
```

把类型名改成 `TranslationSnapshotRenamed` 时，**第一子句失败，但第二子句仍为真** ——
因为 `"new TranslationSnapshot("` 是 `"new TranslationSnapshotRenamed("` 的**前缀**。
⇒ **两个子句不是独立证据**，`&&` 被前缀关系绕过。

**修法**：改用**词边界正则**，让"改名字"必红：

```csharp
Regex.IsMatch(code, @"\brecord\s+TranslationSnapshot\b")
    && Regex.IsMatch(code, @"new\s+TranslationSnapshot\s*\(")
```

### ③ LogService：**我改了代码却忘了加判据**

这是更严重的一类问题。我在本轮审查中修改了 `LogService`，让它按 `PackageRuntime.IsPackaged`
选日志目录（打包形态写 `%LOCALAPPDATA%\TrueToneCap\log`），**但从头到尾没有为它加任何测试**。

⇒ 把 `IsPackaged` 替换成常量 `false`（**等于废掉整个修复**）后，单元测试**仍然全绿**。
一条没有判据保护的修复，**下次重构就会被无声删掉**，而且没人会发现。

**修法**：新增 `Test_LogDirectory_PackagedBranch`，3 条断言：

- 必须按 `PackageRuntime.IsPackaged` 分支
- 打包形态落在 `%LOCALAPPDATA%\TrueToneCap\log`
- 未打包形态仍写程序目录下的 `log`（**保住绿色包语义**）

### 第二轮结果（全部修好）

| 判据 | 结果 |
|---|---|
| StartupManager: 禁反射 | ✅ RED |
| StartupManager: TaskId | ✅ RED |
| Preview: cancel rethrow | ✅ RED |
| **Preview: snapshot** | ✅ **RED（已修）** |
| Settings: packaged branch | ✅ RED |
| **LogService: packaged branch** | ✅ **RED（已修）** |

**无牙判据：0 条。**

> 📌 **这是本次完整验证最重要的产出。** 前 5 项全绿时很容易收工，
> 但"判据全绿"与"判据有效"是两件事 —— 前者只能靠变异测试区分。
> 这也再次印证了本轮审查的核心教训：**通过了测试，不等于测试测到了东西。**

---

## 8. 仓库卫生

| 检查 | 结果 |
|---|---|
| `AppPackages` 被跟踪文件数 | ✅ **0**（原 18 个 / 151.5 MB） |
| `.gitignore` 规则命中 | ✅ 第 58 行 `src/TrueToneCap.App/AppPackages/` |
| `git add -A` 会否重新收入 | ✅ **不会** |
| 已暂存的删除条数 | 18（待下次提交生效） |
| `origin/main` 中的构建产物 | ✅ **0** ⇒ 从未推送，无需改写历史 |

---

## 9. 事实性声明复核

我把散落在各文档里的关键声明逐条核实（避免"文档说了但事实不是"）：

| 声明 | 核实方式 | 结果 |
|---|---|---|
| `Publisher` 仍是占位符 | 读清单 | ✅ `CN=PLACEHOLDER-REPLACE-WITH-PARTNER-CENTER-IDENTITY` |
| `PRIVACY.md` 已被跟踪 | `git ls-files` | ✅ |
| `PRIVACY.md` 随包分发 | 解包 MSIX | ✅ |
| D3D 设备访问都在锁内 | 逐处扫描 + 上下文判定 | ✅ 唯一的"游离"命中是字段声明本身（L978），非访问 |
| 捕获回调在 `finally` 里递减 | 定位 L356 | ✅ |
| `OcrPreviewWindow` 无 `_translatedLines` 残留 | grep | ✅ 0 |
| `StartupManager` 无反射残留 | grep（滤注释） | ✅ 0 |

---

## 10. 仍未验证的（诚实边界）

以下**无法在本机验证**，且**全部属于"打包/真机才暴露"这一类** —— 而本会话的严重缺陷正是产生于这个盲区：

| # | 项 | 为什么本机测不了 |
|---|---|---|
| 1 | **打包形态 `StartupTask` 的真实行为** | 需真装 MSIX 才有包身份；本机造不出来 |
| 2 | **`RequestEnableAsync` 被 `DisabledByUser` 拒绝的实际路径** | 需真机 + 先在系统设置里关过 |
| 3 | **MSIX 沙箱内子进程 + `%TEMP%`** | 需真装实测（这是四格式编码的关键依赖） |
| 4 | **取消后请求是否真的立即中止** | 需真实网络长时间观察（文本断言只能钉接线） |
| 5 | **框架依赖包在无运行时机器上的安装流程** | 商店自动装框架包，本机无该场景 |
| 6 | **设置持久化在打包形态下真的生效** | 需真装后改设置再重启 |

> ⚠️ **建议**：装一次 MSIX 可**一次性覆盖 1/2/3/6**。这是当前唯一的高价值剩余验证动作。

---

## 11. 复现命令

```powershell
# ── 1. 干净构建（含 CI 关键路径）──
Remove-Item -Recurse -Force src/*/obj, src/*/bin, src/TrueToneCap.App/AppPackages -ErrorAction SilentlyContinue
dotnet restore TrueToneCap.slnx --locked-mode
foreach ($p in @("Core","LogViewer","App","Tools","Test")) {
  dotnet build "src/TrueToneCap.$p" -c Release -p:Platform=x64
}

# ── 2. 全部测试 + 自检 ──
dotnet run --project src/TrueToneCap.Test -c Release -- --all
src/TrueToneCap.App/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/TrueToneCap.exe --selftest

# ── 3. MSIX + 门禁 ──
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true
.\scripts\verify-msix.ps1

# ── 4. WACK ──
$wack = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe"
$msix = (Get-ChildItem -Recurse src/TrueToneCap.App/AppPackages -Filter *.msix).FullName
& $wack reset; & $wack test -appxpackagepath $msix -reportoutputpath "$env:TEMP\w.xml"
([xml](Get-Content "$env:TEMP\w.xml" -Raw)).REPORT.GetAttribute('OVERALL_RESULT')   # => PASS

# ── 5. 判据是否真有牙（本次新增，建议纳入 CI）──
.\scripts\mutation-verify.ps1        # 期望：guards without teeth: 0

# ── 6. 两种发布模式 ──
.\Publish.ps1 -Msix -SkipTests -AllowDirty                  # => 175.85 MB
.\Publish.ps1 -Msix -FrameworkDependent -SkipTests -AllowDirty  # => 85.12 MB
```

---

## 12. 最终验收读数

| 项 | 结果 |
|---|---|
| 5 项目 Release 构建 | ✅ **0 警告 0 错误** |
| `--locked-mode` 还原 | ✅ 通过 |
| 8 套件测试 | ✅ **1872 通过 / 0 失败**（2 跳过） |
| `--selftest` | ✅ 通过（产出真实编码文件） |
| MSIX 构建 + 内容门禁 | ✅ 通过（150.91 MB / 566 文件） |
| 交付物内含我的实现 | ✅ 元数据级核实 |
| 交付物内**不含**被删的反射 | ✅ 元数据级核实 |
| WACK | ✅ `OVERALL_RESULT=PASS`，0 阻断 |
| **变异测试** | ✅ **6/6 判据有牙**（修好 2 条） |
| 仓库卫生 | ✅ 构建产物已隔离 |
| 文档与事实一致 | ✅ 更正 31 处过期数字 |
