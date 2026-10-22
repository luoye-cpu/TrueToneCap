# TrueToneCap 完整软件管线审查报告

> ⚠ **关于本文里的提交 SHA（2026-09-20 版本收口时加注）**：`:456` 与 `:504` 两处写的"历史回到 / HEAD 恢复
> `85d876a`"是**当时那次备份还原事件的记录**，属实、不改。但要注意 0.3.4-beta 已按"一条提交 = 一个版本"
> 收口：`85d876a` 及同批 4 条提交合并后**不再出现在 `main` 上**（合并前的完整历史在
> `C:\PLAN\pre-squash-v0.3.4.bundle` 与 reflog 里可查）。⇒ 按 SHA 去 `git log` 找不到 ≠ 这条记录失效。


> ⚠ **时效标注（2026-09-20 整洁审计）**：本文两处结论已被代码取代——
> ① 崩溃哨兵一节（`:336-352`）写的是"**接入 `NvencAvifBackend.EncodeAsync`**、只判 NVENC、
> 读到残留就**自动禁用**该后端"。2026-09-20 查实的后果是这条设计有缺陷：残留只读不删，
> 而被禁用的后端再也不会去 Arm/Disarm ⇒ 哨兵永远没人清理，**一次偶发崩溃＝永久关闭硬件编码**
> （界面上的"硬件编码"复选框 09-18 已按用户指令移除，`ClearAll()` 无生产调用方 ⇒ 提示是空头承诺）。
> 现改为**读后即消费**（残留只禁用下一次启动的那一个会话，一次成功即解除），并把 **MFT/QSV 纳入**
> （key 统一 `"MFT"`）。详见交接 §32.9。
> ② `:176` 的"本机当前状态良好：`core.hooksPath` 正确"—— 09-20 实测**已丢失**（推断为 09-15
> `filter-repo` 重建 `.git/config` 所致），已重新挂载。该条本来就是"本地配置不入仓 ⇒ 新克隆静默失效"，
> 需定期复验而非当既成事实。

- 审查日期：2026-09-15
- 审查范围：构建 / 依赖 / CI / 发布 / 测试 / 运行时核心 / OCR 全链路
- 代码基线：`HEAD = 0916a2a (v0.3.3-beta)` + 工作区 90 个暂存的改动
- 代码规模：5 个工程、约 37,190 行 C#（不含 obj/bin）

---

## 0. 执行摘要

**一句话结论：代码质量本身相当扎实（构建 0 警告 0 错误、三个历史 OCR 崩溃坑全部修对），但整条管线的"事实基础"没有落地 —— 构建治理层与 CI 从未进入任何 commit，导致本地与 CI 是两套环境。**

本次审查实测了构建与门禁（非纸面推断），并对四路并行深度审查的每一条 P0 发现做了逐条代码复核，全部属实。

| 维度 | 评级 | 关键判断 |
|---|---|---|
| 构建 / 依赖 | ⚠️ 高危 | 治理文件未入库，CI 必然分叉；AOT 开关空转 |
| CI / 发布 | ⚠️ 高危 | 测试门禁防假绿灯设计正确，但**发布路径无此保护**，产物零校验 |
| 测试 | ⚠️ 中危 | 断言总数真实，但含约 15 条恒真断言，绿不可尽信 |
| 运行时核心 | ✅ 良好 | 三个历史崩溃坑全部修对；2 个资源管理高危项待修 |
| OCR | ✅ 良好 | 生命周期正确；韩文缺陷根因已定位到具体映射缺失 |

发现统计：**P0 七项 / P1 十二项 / P2 九项**。

---

## 1. 实测结果（本次审查实际执行，非推断）

```
=========== BUILD (solution, Release) ===========
已成功生成。
    0 个警告
    0 个错误
BUILD_EXIT=0

=========== TEST GATE (--all) ===========
RESULT suite=--unit-tests        passed=47  failed=0 skipped=0
RESULT suite=--color-tests       passed=93  failed=0 skipped=0
RESULT suite=--encoding-tests    passed=14  failed=0 skipped=0
RESULT suite=--service-tests     passed=5   failed=0 skipped=0
RESULT suite=--ocr-tests         passed=10  failed=0 skipped=0
RESULT suite=--format-tests      passed=30  failed=0 skipped=0
RESULT suite=--usability-tests   passed=152 failed=0 skipped=1  (35059ms)
RESULT suite=--wgc-avif-tests    passed=21  failed=0 skipped=0
TEST_EXIT=0
```

判读：

- 构建达标（`TreatWarningsAsErrors=true` 下仍 0 警告，说明该开关真实生效，非摆设）。
- 门禁全绿，断言数与历史三份日志（`all-run.txt` / `final5.log` / `gate-final.log`）**逐位一致**，不存在断言虚标。
- 一个与既有认知的偏差：**实际门禁是 8 套件而非前 7。** `Program.cs:89-95` 中 `isolatedSuites`（WGC）同样执行 `if(exit!=0) failedSuites++`，故 `--all` 返回 1。WGC 的子进程隔离本身是有效的（`RunInChildProcess` 真起子进程，日志可见其 RESULT 行），只是它并非"不计入门禁"。这点此前文档描述不准，需修正。

---

## 2. P0 — 阻断 / 高危

### P0-1 构建治理层从未进入任何 commit —— 本地与 CI 是两套环境

【位置】`git ls-tree HEAD` 对比暂存区
【现象】以下文件在 HEAD 中**完全不存在**，仅存在于暂存区（staged，从未 commit）：

| 文件 | HEAD 状态 |
|---|---|
| `Directory.Build.props` | ★ 未入库 |
| `Directory.Packages.props` | ★ 未入库 |
| `NuGet.config` | ★ 未入库 |
| `global.json` | ★ 未入库 |
| `.editorconfig` | ★ 未入库 |
| `.github/workflows/ci.yml` | ★ 未入库 |
| 5 个 `packages.lock.json` | ★ 未入库（+767 行全新增） |

未提交改动总量：**90 个文件，+9,835 / −19,640 行**。
【风险】这是本次审查最严重的问题，且是"管线级"而非"代码级"：
1. CI 从远端检出后拿不到锁文件，`ci.yml:105` 的 `dotnet restore --locked-mode` **必然失败**；
2. 拿不到 props/CPM/global.json → 依赖版本、SDK 版本、编译器开关全部失控，"可复现构建"承诺落空；
3. `ci.yml` 自己也没提交 → CI 实际执行的很可能是旧流程或根本不触发；
4. 任何 clone / fork / 协作 / 回滚都拿不到这套治理，团队其他人和未来的自己都是裸奔。
【建议】**最高优先级**：将这一组作为**原子提交**单独落地（props + CPM + NuGet.config + global.json + 5 锁文件 + ci.yml + .editorconfig），随后立即跑一次 `dotnet restore TrueToneCap.slnx --locked-mode` 验证，再拆后续业务提交。注意提交前核对 `git status --short` 中 `D` 计数应为 **12**（9 tools + 3 models），`git add -A` 会吃掉"解除跟踪"改动。

---

### P0-2 发布门禁只判退出码，无 RESULT 校验 —— 假绿灯在发布路径敞开

【位置】`Publish.ps1:427-433`
【现象】
```powershell
& dotnet run --project $testProject -c $Configuration -- $suite
$code = $LASTEXITCODE
if ($code -ne 0) { ... }
```
只判退出码。而 `scripts/verify.ps1:246-261` 已经实现了正确的防假绿灯逻辑（解析 `RESULT suite=… passed=… failed=…`，`:266-269` 把"退出码 0 但未解析到 RESULT 行"判为失败，注释明写"必须视为失败，否则门禁就是假绿灯"）。
【风险】套件名一旦拼错、或运行器吞参数 → 零断言执行、退出码 0 → 照常出包并盖章 SHA256SUMS。**同一份保护在 verify.ps1 有、在 Publish.ps1 没有**，这是不对称的直接后果。
【建议】抽出公共 `Invoke-GateSuite`（含 RESULT 正则 + 断言数下限），Publish.ps1 与 verify.ps1 共用，杜绝两份实现漂移。

---

### P0-3 约 15 条恒真断言 —— 门禁"能红，但绿不可信"

【位置与证据（均已逐条读码复核）】
| 位置 | 代码 | 影响 |
|---|---|---|
| `ColorPipelineTests.cs:465` | `Assert($"AVIF: …", ok \|\| true); // 允许失败` | AVIF 编码彻底失败也判通过 |
| `ColorPipelineTests.cs:469` | `catch(Exception){ Assert(…, true); }` | native 库缺失 → 绿灯 |
| `UsabilityTests.cs:1316` | 在 `Enum.GetValues<OutputFormat>()` 循环内 `Assert(…, true)` | **一次产出 7 条恒真**；且该方法只构造 `EncodingSettings`，从未调用编码器 |
| `ServiceTests.cs:64 / :127 / :145` | 三条硬编码 `true` | 实测日志显示 `⚠ ShaderLoader: ToneMapping.hlsl.cso 未找到` → DXBC 魔数校验与缓存引用相等断言**从未执行**，service5 中仅 2 条有效 |
| `UsabilityTests.cs:1329` | `displays.Count >= 0` | `int.Count>=0` 恒真 |
| `UsabilityTests.cs:1275/1292/1349` | `Assert(…, true)` | 仅表达"没抛异常" |

【风险】合计约 15 条，占 351 断言的 **4.3%**。因为它们恒真，即使相关功能完全坏掉门禁仍绿。
【加重情节】`ColorPipelineTests.cs` **根本没有跳过机制**（`:94` 的 `Report(...,0,sw.ElapsedMilliseconds)` 未传第 6 参 `_skipped`），直接违反项目"环境不满足必须显式 Skip"的铁律。本机 D3D/native 可用故未触发，属**潜伏**假绿灯，换机即现。
【建议】① 删除 `|| true` 与循环内恒真，改为真实调用后断言；② ServiceTests 的"未找到"三条改为 `Skip()`；③ 给 `ColorPipelineTests` / `ServiceTests` / `CorePipelineTests` / `OcrServiceTests` 补 `_skipped` 并接入 `TestSummary.Report` 第 6 参。

---

### P0-4 Native 崩溃隔离失效 —— 硬件编码回退链是死代码

【位置】`src/TrueToneCap.Core/TrueToneCap.Core.csproj:9` vs 发布产物
【现象】`<LegacyCorruptedStateExceptionsPolicy>true</>` **只写在 Core 库** csproj，入口项目 `TrueToneCap.App.csproj` 未声明。三个发布产物的 `runtimeconfig.json` 实测 `configProperties` 仅 3 项（`MetadataUpdater.IsSupported` / `EnableUnsafeBinaryFormatterSerialization` / `CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS`），**无 CSE 开关**。
【风险】`NativeEncoderGuard.cs:41,46,78,83` 与 `OnnxOcrEngine.cs:622,790` 的 `catch (AccessViolationException)` / `catch (SEHException)` 是**死代码** —— 与本项目历史上实测到的"DML 下 100% AV、try/catch 无效、进程直接死（退出码 −1073741819）"完全吻合。后果：NVENC / MFT / QSV 的 P/Invoke 一旦崩溃，**回退链（NVENC → libaom → PNG）根本不会被执行**，进程直接终止。
#### 补充实测（2026-09-15 完成，结论：进程内方案彻底不可行）

审查时的建议是"先实测再决定"。实测已完成（`tests/manual/cse-probe/`，.NET 11.0.0-preview.7），**三重证据一致指向「不可行」**：

| 证据 | 方法 | 结果 |
|---|---|---|
| ① 运行时 | 经 P/Invoke（`RtlMoveMemory` 向无效地址写）触发真实 0xC0000005 | 进程直接终止，**退出码 −1073741819**；`catch(AccessViolationException)` 与 `finally` **均未执行** |
| ② 配置开关 | 加 `-p:LegacyCorruptedStateExceptionsPolicy=true` 后重跑 | 行为**完全相同**（同为 −1073741819）；两份 `runtimeconfig.json` **逐字节一致**，均不含该开关 —— 即该属性在当前 SDK 上**根本不再写入 runtimeconfig** |
| ③ 编译器 | 标注 `[HandleProcessCorruptedStateExceptions]` | `error SYSLIB0032`："Recovery from corrupted process state exceptions is not supported; **HandleProcessCorruptedStateExceptionsAttribute is ignored**." |

**结论修正**：原建议的方案 2（在 App.csproj 补属性）**已被证伪，不要采用**。该属性在当前 SDK 上是死配置，补到哪里都不生效。

**唯一可行方案 = 子进程隔离**。`avifenc` / `cjxl` / `cwebp` 已是子进程，崩溃只影响子进程（天然安全）；**MFT / NVENC / QSV 是进程内 P/Invoke，其崩溃目前无法拦截** —— 这是需要在架构层面单独排期的事项，不是改几行 catch 能解决的。

**已做的诚化处理**（2026-09-15）：移除 Core.csproj 中失效的 `LegacyCorruptedStateExceptionsPolicy`，并在 `NativeEncoderGuard` / `OnnxOcrEngine` 的 CSE 分支上加醒目注释，说明其不可达及实测依据 —— 避免未来有人继续依赖这层"虚假安全感"。

---

### P0-5 韩文 OCR 恒 0% —— 根因已定位到具体映射缺失

【位置】`MultiOcrService.cs:249-275`（`MapLangToBcp47`）vs `OcrLanguage.cs:23`
【现象】ONNX 语言 Id 是 `"ko"`，而映射表只认 `"korean"` / `"kor"` → `"ko"` 落到 `_ => null` 分支返回 `null`。同理漏映射的还有 `"ja"`（表内是 `"jp"`/`"japanese"`）、`"nl"`、`"pl"`、`"th"`、`"vi"`、`"ar"` —— **16 个语言中有 7 个漏映射**。
【风险】这直接解释了长期悬而未决的"韩文恒 0%"：ONNX 侧拿到空结果 → fallback 到 Windows OCR 时传入 `null` → `OcrService.cs:76` 默认回退 `zh-Hans-CN` → **兜底形同虚设**，用中文引擎去识别韩文。
【建议】按 `OcrLanguages.OnnxLanguages` 的 Id **全集**补齐映射，并加一条单测断言"每个 OnnxLanguage.Id 都有非 null 的 BCP-47 映射"——这类"枚举全集覆盖"的断言正好能防住本次这类漏项。

---

### P0-6 CI 完全不产出、不校验发布产物

【位置】`.github/workflows/ci.yml:113-118`（明文注释"这里不跑发布打包"）
【现象】无 `Publish.ps1` / `verify-package.ps1` / `pack.ps1` / `upload-artifact`。
【风险】AOT 裁剪、R2R 复合映像、`.cso` 着色器、native dll、模型缺件等**真实发布期风险，CI 覆盖为 0**，唯一防线是人工发版时的手工步骤。而 `verify-package.ps1` 自身也不启动主程序（`vpkg.log:14` 明示），只做清单 + 尺寸下限 + PE 头 + 版本资源 + native `--version` → WinAppSDK/PRI/XAML 缺失、启动即崩的情况仍判绿。
【建议】新增 release workflow：publish → verify-package → pack → upload-artifact（含 SHA256SUMS.txt）；并给主程序加 `--selftest --out <dir>` 无头模式，产出 PNG/AVIF/JXL 各一，复用 format 套件的 box/ICC 断言做真校验。

---

### P0-7 预发布 RC 依赖进入生产产物

【位置】`Directory.Packages.props:27`、`global.json:3`
【现象】`Microsoft.Extensions.DependencyInjection 11.0.0-rc.1.26425.128`（App 直接依赖，进发布产物）；SDK 钉 `11.0.100-preview.7.26381.103`。
【风险】RC 包可能被下架/取消列出 → 全新机器（无本地 NuGet 缓存）还原 **NU1101**；GA 后仍钉 RC 会与 11.0.0 GA 包产生版本冲突。锁文件虽已 resolved 到该版本，但锁文件目前也未入库（见 P0-1），双重裸奔。
【建议】若 App 未使用 .NET 11 专属 API，评估改用 10.0.x LTS 线的 DI；否则在 release checklist 中显式记录"必须随 .NET 11 GA 同步升级"。

---

## 3. P1 — 应修

| # | 位置 | 现象 | 风险 | 建议 |
|---|---|---|---|---|
| 1 | `verify.ps1:76-84` | 套件列表硬编码但**无断言总数下限** | 删一项套件 CI 仍绿 | 断言"8 条 RESULT 行 + 总 passed ≥ 351" |
| 2 | `Publish.ps1:478-481` | 锁文件 RID 归一化失败仅 Warning | 残留 win-x64 段 → 下次 CI `--locked-mode` NU1004 | 改 `throw`；归一化移入 `finally` |
| 3 | `Publish.ps1:200-222` | 模型仅 `Test-Path`（工具侧却有 SHA256 强校验 `:255-266`） | 可产出模型缺件/损坏的包 | 建 `models-manifest.json`，与工具同等强校验 |
| 4 | `Publish.ps1:379-381` | `Remove-Item -Recurse -Force $OutputDir` 无白名单，且**早于测试门禁**(`:388`) | `-OutputDir` 传错毁目录；门禁红时上一版已被删 | 限定 `publish\TrueToneCap-v*`，门禁移到清理前 |
| 5 | `Directory.Packages.props:17` | `CentralPackageTransitivePinningEnabled=false` + 无 `NuGetAuditMode=all` | 传递依赖（含 `SharpGen.Runtime 2.4.2-beta`）静默漂移；有 CVE 不报；且 `TreatWarningsAsErrors` 会把 NU19xx 升级为错误，新公告无预警阻断构建 | 加 `NuGetAudit=true` / `NuGetAuditMode=all` / `WarningsNotAsErrors=NU1901..1904` |
| 6 | `git-hooks/*` | 四处失败路径 `exit 0`；`core.hooksPath` 是**本地配置不入仓** | 新克隆静默失效（本机当前状态良好：hooksPath 正确、索引 100755、LF 正确） | 失败路径改 `exit 1`；CI 加一步校验 hooksPath 与可执行位 |
| 7 | `GpuToneMapper.cs:213-277` | `ctx.Map` 与 `ctx.Unmap` 之间**无 try/finally**，且整段在 `lock(_poolLock)` 内 | 设备移除/驱动重置 → Unmap 未执行 → 后续截图全阻塞在锁上，**界面永久假死** | Map/Unmap 包 `try/finally` |
| 8 | `WgcCaptureService.cs:574` + `FormatEncoders.cs:427-435` | WGC Dispose 旧 D3D11 设备后未通知 NVENC（`InvalidateSessions` `:717` 不清 `_d3dDevice`） | 换显示器后 NVENC 用悬垂设备 → 原生崩溃，并被 P0-4 放大为进程终止 | `InvalidateSessions` 内调 `NvencAvifBackend.SetSharedD3DDevice(new)` 或改 `WeakReference` |
| 9 | `MultiOcrService.cs:104`(45s) vs `OnnxOcrEngine.cs:366`(60s) | 外层超时 < 内层超时；`Task.WhenAny` 超时后被放弃的推理仍持有 `_inferGate` | 下一次 OCR 请求排队最长 15s，表现为"卡住" | 内层阈值小于外层（如 40s），放弃时共用 CTS |
| 10 | ~~`ToneMapper.cs:236-246` 高光硬削平~~ | **经复核为正确设计，撤销本条**（详见第 7 章「一处自我修正」） | 无 —— 原判断有误 | 无需改动 |
| 11 | `ColorPipelineTests.cs:96` | `return _failed;` 用失败数当退出码 | 恰好 256 个失败时 exit 0 | 改 `return _failed > 0 ? 1 : 0` |
| 12 | `WgcAvifBackendTests.cs:125-129` | 只跑 `{Nvenc, LibAom}`，`ImageEncoder.cs:47` 枚举含 `Qsv` | QSV 后端零覆盖 | 补 Qsv 用例 |

---

## 4. P2 — 建议

- **AOT 链路实际未接通**：全仓 grep `CorePublishAot` 仅 `Publish.ps1:335` 一处消费，**无任何 csproj 读取**；`PublishAot|PublishTrimmed|TrimMode|IsAotCompatible` 在 csproj 中零命中 → 控制台打印"🔥 Core AOT 已启用"但实际是 JIT+R2R。`TrimmerRoots.xml` 在 ILLink 未启用时不生效，问题只会在有人真打开 `PublishAot` 时首次暴露。
- **fxc 版本不钉**：构建期 `CompileShaders.ps1:28-33` 与运行时 `ShaderCompiler.cs:139-144` 都取"SDK 最高版本"（双端策略一致这点是对的），但"最高"≠"固定" → 不同机器产出不同 DXBC。建议支持 `-p:WinSdkVersion` 覆盖。
- **着色器三清单无校验**：`CompileShaders.ps1:43-47` / `ShaderCompiler.cs:40-42` / `App.csproj:63` 当前一致（均为 ToneMapping ps_5_0 / FullscreenVS vs_5_0 / OverlayComposite ps_5_0），但靠人工同步。建议加 Target 或单测断言一致性。
- `HdrCaptureWindow.cs:343` 仍查 `CopyTexture.hlsl.cso`（源文件不存在，必然回退）→ 死引用。
- `dotnet-tools.json` 位于仓库根（正确位置是 `.config/`）且 `tools:{}` 为空 → 死配置。
- `publish/PLAN/models/` 下 2 个 ONNX 模型**已入库**，是 `.git` 达 170 MB 的成因之一；建议 `git rm --cached` 并改用 `download_models.ps1` 获取。
- 空 `catch {}` 共约 72 处；其中 `CapturePipelineService.cs:50,96`（取显示器 ICC 失败）会**静默降级为不嵌 ICC**，色彩偏差无任何提示。
- 测试临时目录硬编码 `Path.GetTempPath()/TrueToneCap_*`，`UsabilityTests.cs:1431` 还用 `Parallel.For` 并发写同目录 → 并发跑必然文件锁。建议加 `Guid` 子目录。
- `App.csproj` 缺 `<Platforms>x64</Platforms>`（`LogViewer.csproj:10` 有），与 slnx 的 `<Platform Project="x64" />` 映射不对称。

---

## 5. 已验证为良好实践（无需改动，避免重复劳动）

这些是本次**逐条读码确认**过的，请勿在后续重构中破坏：

**运行时核心与 OCR（重点）**
- ✅ **历史坑① `AsTensor<float>()` 零拷贝 use-after-free —— 修对了。** `OnnxOcrEngine.cs:596-621`（det）概率图/位图拷贝全部在 `using var results` 存活期内完成，output 从未带出作用域；`:756-789`（rec）CTC 解码在作用域内完成，只把 `List<int>` 索引带出，`:766-767` 注释明确"不额外拷 logits（每框 ~24MB）"—— 正确的性能取舍。
- ✅ **历史坑② DirectML 纹理 16384 上限 —— 修对了。** `:822` `const int MaxDmlTextureDim = 16384`；`:474-481` 按最终尺寸收紧；`:483-484` `ratioW/H` 由最终尺寸反算；判定用 `>` 允许恰好 16384，符合 D3D12 上限含端点。
- ✅ **历史坑③ DML 必须 `EnableMemoryPattern=false` —— 修对了。** `:294` `opts.EnableMemoryPattern = _provider != OnnxExecutionProvider.DirectML;` 配合 `:299` `ExecutionMode = ORT_SEQUENTIAL`，符合 ORT 官方要求。
- ✅ 全仓库 **0 处 `async void`**；`.Result/.Wait()` 均在 `Task.Run` 内或已完成态，无 UI 线程死锁。
- ✅ 多显示器变更有处理（`MainWindow.xaml.cs:2381-2393` → `InvalidateSessions`）；`PooledSession` 的在途回调计数 + Drain 等待设计扎实。

**构建与依赖**
- ✅ CPM 未被绕过：5 个 csproj 所有 `PackageReference` 均无 `Version`，全仓无 `VersionOverride`。
- ✅ 无属性重复声明：`LangVersion|TargetFramework|Nullable|TreatWarningsAsErrors|PlatformTarget` 在 5 个 csproj 中零命中。
- ✅ `Platform`/`RuntimeIdentifier`/`SelfContained` 刻意未进 props（props:29-41 注释完整说明理由），与历史约定一致。
- ✅ `NuGet.config` `<clear/>` + 独立 `auditSources`；`.gitignore` 已无 `NuGet.Config` / `packages.lock.json` 屏蔽规则。
- ✅ 着色器管线历史坑已修：fxc + SM5 构建期与运行时对齐；fxc 发现逻辑双端同策略且有 `ProgramFilesX86` 兜底；`App.csproj:91-104` 动态收集 cso 并用限定元数据 `%(_TtcCsoFile.Filename)` 避免批处理污染。
- ✅ `tests/manual/` 未入库（含 `glm_*.py` 明文 API Key）—— 隔离正确，不要放开目录级忽略。

**CI 与测试**
- ✅ `verify.ps1:246-269` 的防假绿灯设计（RESULT 正则 + `NoResult` 判失败）是本仓库最正确的一处门禁实现，应作为标准推广到 Publish.ps1。
- ✅ CI 全程不用 `dotnet test`（`ci.yml:107-111`），规避了"退出码 0 零执行"陷阱。
- ✅ 无 `actions/cache` → 不存在缓存假绿。
- ✅ WGC 子进程隔离真实有效（`Program.cs:149-216`，`RedirectStandardOutput=false`，日志可见其 RESULT 行）。
- ✅ `TestSummary` 的 `skipped` 仅打印、**不计入 passed**、不影响退出码 —— 语义正确，不会被误算成通过。

---

## 6. 修复路线图

**第一阶段 — 让管线成立（最高优先，先于任何功能改动）**
1. **提交构建治理层原子提交**（P0-1）：props + CPM + NuGet.config + global.json + 5 锁文件 + ci.yml + .editorconfig；提交后立即 `restore --locked-mode` 验证；提交前核对 `D` 计数 = 12。
2. **清理恒真断言**（P0-3）：`ColorPipelineTests.cs:465`、循环内 `UsabilityTests.cs:1316`、`ServiceTests.cs:64/127/145`；补 4 个套件的 `_skipped` 上报。
3. **发布门禁对齐**（P0-2）：抽 `Invoke-GateSuite`，Publish.ps1 与 verify.ps1 共用；加断言数下限。
4. **韩文映射补齐**（P0-5）：小改动、直接收益，并加"枚举全集覆盖"单测。

**第二阶段 — 让崩溃可控**
5. 子进程隔离 MFT/NVENC（P0-4），先实测 CSE 开关在 .NET 11 下是否可用再决定是否走进程内方案。
6. `GpuToneMapper` Map/Unmap 加 `try/finally`（P1-7）——改动极小，防的是界面永久假死。
7. NVENC 悬垂设备（P1-8）；OCR 超时阈值统一（P1-9）。

**第三阶段 — 让产物可信**
8. CI 增加 release workflow 与 `--selftest`（P0-6）。
9. 模型 SHA256 清单（P1-3）、`Remove-Item` 白名单 + 门禁前置（P1-4）、锁文件归一化改 `throw`（P1-2）。
10. 开启 `NuGetAuditMode=all` 并显式 `WarningsNotAsErrors`（P1-5）。

**第四阶段 — 卫生与可复现**
11. 模型出库 + `.git` 瘦身（P2）；AOT 开关要么接通要么删除（P2）；fxc 版本可覆盖（P2）；着色器三清单自动校验（P2）。

---

## 7. 本轮已实施的修复（含实测验证）

审查完成后立即修复了第一阶段中**判据明确、风险可控**的部分。全部改动均已实测。

### 实测结果（修复后重跑）

```
构建：0 个警告 / 0 个错误 / BUILD_EXIT=0

RESULT suite=--unit-tests        passed=47  failed=0 skipped=0
RESULT suite=--color-tests       passed=93  failed=0 skipped=0   ← AVIF 真实断言通过
RESULT suite=--encoding-tests    passed=14  failed=0 skipped=0
RESULT suite=--service-tests     passed=4   failed=0 skipped=1   ← 原 1 条恒真改为显式 Skip
RESULT suite=--ocr-tests         passed=10  failed=0 skipped=0
RESULT suite=--format-tests      passed=30  failed=0 skipped=0
RESULT suite=--usability-tests   passed=152 failed=0 skipped=1   ← 7 条恒真改为真实断言，全部通过
RESULT suite=--wgc-avif-tests    passed=21  failed=0 skipped=0
```

门禁 7 套件断言总数 **351 → 350**（+1 显式跳过），WGC 21 不变。

### 修复清单

| # | 对应问题 | 文件 | 改动 |
|---|---|---|---|
| 1 | P0-5 韩文 0% | `MultiOcrService.cs` `MapLangToBcp47` | 补齐 `"ko"/"ja"/"nl"/"pl"/"th"/"vi"/"ar"` 映射；并修复 `"zh-en"`（中英混合 Id）被"含 `-` 则原样返回"分支误当成合法 BCP-47 的问题 |
| 2 | P0-3 恒真断言 | `ServiceTests.cs:64` | 恒真改为 `Skip()`；补 `_skipped` 并接入 `TestSummary.Report` 第 6 参 |
| 3 | P0-3 恒真断言 | `ColorPipelineTests.cs:465` | `ok \|\| true` 改为真实断言 `Assert("AVIF: 文件已创建且非空", ok)` |
| 4 | P0-3 伪装通过 | `ColorPipelineTests.cs` 280/443/469/671 | 4 处"环境不满足就判 true"改为显式 `Skip()`（附原因）；补 `_skipped` 与 `Skip()` |
| 5 | P0-3 恒真断言 | `UsabilityTests.cs:1316` | 循环内 7 条恒真改为真实契约断言"默认质量 ∈ [min, max]"（无需实际编码即可验证） |
| 6 | P0-3 弱断言 | `UsabilityTests.cs:1329` | `Count >= 0`（恒真）改为 `Count >= 1`，无显示器时 `Skip()` |
| 7 | P1-11 退出码 | `ColorPipelineTests.cs:96` | `return _failed` → `return _failed > 0 ? 1 : 0`（避免 256 个失败溢出为 0） |
| 8 | P1-7 假死风险 | `GpuToneMapper.cs` 三处 | `Map`/`Unmap` 全部包 `try/finally`（异常时不再漏 Unmap，杜绝 `_poolLock` 永久阻塞） |
| 9 | P1-9 超时矛盾 | `OnnxOcrEngine.cs:366` | 内层看门狗 60s → **40s**，严格小于外层 45s，保证先释放 `_inferGate` |
| 10 | P0-2 发布假绿灯 | `Publish.ps1:427-465` | 门禁改为解析 `RESULT` 行：退出码 0 但无 RESULT 行 = 失败；`failed>0` = 失败；新增 `-MinGateAssertions`（默认 340）防套件静默删减 |

**关于第 10 项的实测验证**（不满足于"看起来对"）：
```
===== 门禁判据单元测试 =====
  [正常]      => 通过 (47 断言)
  [零断言]    => 失败 (无 RESULT 行 / 零断言执行)
  [有失败]    => 失败 (2 条断言)
  [退出码非0] => 失败 (退出码 1)
===== 端到端: 传一个不存在的套件名 =====
真实退出码 = 1 ；输出含 RESULT 行? False ；=> 失败
```
同时 `Publish.ps1`、`verify.ps1`、`verify-publish-gates.ps1`、`pack.ps1` 四个脚本均通过 PowerShell 语法解析（`SYNTAX_OK`）。

### 一处刻意的判断收窄

初版审查报告曾把 `ServiceTests.cs:127/:145` 与 `ColorPipelineTests.cs:186` 也列为恒真断言。复核后判定**不属于**恒真，故未改动：

- `ServiceTests.cs:127/:145` 是 try 成功到达该行才执行的 no-throw 断言（catch 分支判 `false`），到达即已证明"未抛异常"；
- `ColorPipelineTests.cs:186` 是 100 个采样点循环全部通过后才执行的终态断言（任一点失败会提前 `Assert(false); return`）。

两者都是合法的测试写法。**判定恒真断言要看控制流，不能只 grep `, true)`。**

### 第二轮修复（同日追加，均已实测）

| # | 对应问题 | 文件 | 改动 |
|---|---|---|---|
| 11 | P0-4 | `TrueToneCap.Core.csproj` | 移除失效的 `LegacyCorruptedStateExceptionsPolicy`（实测不再写入 runtimeconfig），改以注释固化三重证据 |
| 12 | P0-4 | `NativeEncoderGuard.cs`、`OnnxOcrEngine.cs` | 4 处 CSE 捕获分支加醒目注释：在 .NET 上不可达，不得当作"崩溃已兜底" |
| 13 | P1-8 悬垂设备 | `NvencAvifBackend`（FormatEncoders.cs） | 新增 `NotifySharedDeviceDisposed()`；`WgcCaptureService` 在 Dispose 设备与 `InvalidateSessions` 时调用，杜绝 NVENC 使用已释放的 D3D11 设备 |
| 14 | P1-6 钩子静默失效 | `pre-commit`、`pre-push` | 6 处"环境异常 → exit 0"改为 **exit 1** 阻断（静默跳过 = 门禁无声失效） |
| 15 | P1-4 清理风险 | `Publish.ps1` | 输出目录加**白名单**（必须在 `publish\` 内且以 `TrueToneCap-v` 开头）；清理动作从"门禁前"移到**门禁后**（门禁红时不破坏上一版产物） |
| 16 | P1-2 锁文件漂移 | `Publish.ps1` | 归一化还原失败由 Warning 改为 **throw**（否则污染会跨一个提交才在 CI 暴露 NU1004） |
| 17 | P1-5 审计盲区 | `Directory.Build.props` | 加 `NuGetAudit=true` / `NuGetAuditMode=all`（覆盖传递依赖）+ `WarningsNotAsErrors=NU1901..1904`（不让上游 CVE 公告突然阻断构建） |
| 18 | P1-3 模型完整性 | 新增 `Models/models-manifest.json` + `Publish.ps1` | 模型由"只查存在"升级为 **SHA256 强校验**，与工具侧 `tools-manifest.json` 对称 |

第 17 项实测：`dotnet list package --vulnerable --include-transitive` 显示**当前无易受攻击包**，锁文件未受影响。
第 18 项实测：3 个模型哈希全部通过，且与 `download_models.ps1` 登记值逐位一致（det/rec）；反向验证（篡改期望值）能被正确检出。

### 第三轮修复（P2 清理，均已实测）

| # | 对应问题 | 文件 | 改动 |
|---|---|---|---|
| 19 | 着色器清单漂移 | `Shaders/CompileShaders.ps1` | 新增**四处清单一致性自检**：构建脚本 `$shaders` / `Shaders\*.hlsl` / `ShaderCompiler.cs` 的 `s_shaders` / `App.csproj` 的 `Inputs`。比较的是 **(文件, Profile) 成对值**，因为历史坑正是 Profile 不同步（ps_6_0 vs ps_5_0）。不一致直接 `exit 1` 终止构建 |
| 20 | fxc 版本未钉 | `Shaders/CompileShaders.ps1` | 新增 `-WinSdkVersion` 参数。指定时**命中即用、未命中直接失败**（不做静默回退，避免出现"我以为钉住了"的假象）；不指定仍走枚举取最高 |
| 21 | 死引用 | `HdrCaptureWindow.cs` | 去掉对 `CopyTexture.hlsl.cso` 的查找 —— 其源文件本就不存在，查找必然落空后回退，保留只会误导后来者以为存在 GPU 直通路径。行为完全等价 |
| 22 | AOT 开关假生效 | `Publish.ps1` | `-CoreAot` 此前无任何 csproj 消费，却打印"🔥 Core AOT 已启用"。改为明确告警：库项目不支持 AOT，实际产物仍为 JIT + ReadyToRun |

第 19 项实测（含反向验证）：正常情况打印"清单自检通过"；把 `ShaderCompiler.cs` 中某 Profile 临时改为 `ps_6_0` 后，脚本立刻报出**脚本 vs 运行时的具体差异**并 `exit 1`；随后自动还原源文件，再次执行恢复通过。
第 20 项实测：不指定 → 通过；指定本机已有的 `10.0.28000.0` → 通过并打印钉定版本；指定不存在的 `10.0.99999.0` → `exit 1` 且不静默回退。

### 第四轮：P0-4 的务实解法 —— 崩溃哨兵（新增，已实测）

重新评估后认为：**把 NVENC / MFT 改成子进程并不现实**。它们吃的是 GPU 纹理，跨进程需要传递共享纹理句柄，复杂度与性能代价都很大；而 AVIF 本就已有 `avifenc` 这条安全的子进程软件路径，NVENC 只是**加速路径**。

因此改用「崩溃哨兵」把不可恢复的硬崩溃降级为可自愈的软件路径：

| 阶段 | 行为 |
|---|---|
| 进入 NVENC 原生调用前 | 写哨兵（`%LOCALAPPDATA%\TrueToneCap\encoder-crash.sentinel`，记录后端名 + 时间） |
| 正常返回 | `finally` 中清除哨兵 |
| 进程被 0xC0000005 杀死 | `finally` 不执行 → **哨兵残留** |
| 下次启动 | 读到残留 → 判定"上次是在 NVENC 崩的" → 自动禁用 NVENC，回退 libaom |

这不是"修复崩溃"，而是让**第二次启动不再重复崩溃**。新增 `src/TrueToneCap.Core/Encoding/EncoderCrashSentinel.cs`，并接入 `NvencAvifBackend.EncodeAsync`。

新增 5 条回归断言（unit 47 → 52，门禁 350 → **355**）：
```
✅ EncoderCrashSentinel: 初始未阻塞
✅ EncoderCrashSentinel: Arm 后处于阻塞态（模拟进程崩溃残留）
✅ EncoderCrashSentinel: Disarm 后解除（模拟正常返回）
✅ EncoderCrashSentinel: 同名后端被正确识别
✅ EncoderCrashSentinel: 其它后端不受牵连（按后端隔离降级）
```

#### 配套的设置入口（同日补齐）

哨兵若只能自动降级、无法恢复，就成了单向开关。因此补上用户侧入口：

| 层 | 改动 |
|---|---|
| Core | `NvencAvifBackend.HardwareEncodingEnabled` 静态开关（Core 不反向依赖 App 设置，由 App 注入） |
| 设置模型 | `AppSettingsData.HardwareEncodingEnabled`（默认 `true`） |
| UI | AVIF 选项卡片内新增"硬件编码"复选框；哨兵残留时在其下方显示红色说明 |

两个出口：**取消勾选** = 主动回避硬件路径；**重新勾选** = 清除哨兵 + 重置失败缓存，恢复硬件编码。

⚠️ 一个关键实现细节：复选框绑定的是 `Click` 而**不是** `Checked`/`Unchecked`。后者在 `LoadSettings()` 给 `IsChecked` 赋值时同样会触发，于是每次启动都会误执行一次"清除哨兵"，令自动降级完全失效。

> **需实机验证**：本次 UI 改动仅通过了 XAML 编译与门禁，**控件外观与交互未经实机确认**（本环境无法渲染界面），请在实机上确认布局与文案。

### 第五轮：P0-6 —— 无头自测 + release workflow

**新增 `TrueToneCap.exe --selftest [--out <dir>] [--formats PNG,AVIF]`**（`src/TrueToneCap.App/Services/SelfTest.cs`）：在 App 构造函数**最早期**拦截（早于单实例 Mutex、主题初始化、OCR 模型加载、窗口创建），生成渐变测试图 → 逐个格式编码 → 产出 `selftest.json` → 以退出码表达成败。CI 与发布校验由此能真正"跑一遍编码链路"。

实测（开发态，native 目录下为空属已知状态）：
```
PNG ok / TIFF ok                       ← 托管编码器
JPEG_LI skip / JPEG_XL skip            ← 本机无 cjpegli.exe / cjxl.exe（环境限制）
AVIF fallback / WebP fallback          ← native 缺失时【静默回退为 PNG】，计为失败
failed=2 → 退出码 1（耗时 2s）
```

> **副产品发现**：AVIF / WebP 在 native 工具缺失时会**回退成 PNG 却仍返回成功**。这在发布校验里必须被看见，否则会误判"AVIF 编码通过"而包里其实没有 AVIF 能力。故自测把这种情况显式标为 `fallback` 并计入失败。

#### 排查过程中挖出的一个真问题：WinUI 主线程 async 死锁

自测第一版表现为"进程 45 秒不退出、无任何异常"，但 PNG 文件**已经写好**。加进度落盘后定位到：卡在 `EncodeSdrAsync(...).GetAwaiter().GetResult()` 之后、结果写回之前。

根因是经典的同步上下文死锁：App 构造函数运行在 **WinUI 主线程 (STA)**，`GetAwaiter().GetResult()` 阻塞了该线程，而编码器内部的 `await` 完成后要回到该线程上下文恢复 —— 互相等待。

修复：把整个自测放进线程池执行。
```csharp
ExitCode = Task.Run(() => Run(outDir, only)).GetAwaiter().GetResult();
```
修复后 0.8 秒跑完。**这个坑对本项目有普遍意义：任何在 UI 线程上同步阻塞等待 async 编码的写法，都有同样风险。**

**新增 `.github/workflows/release.yml`**（workflow_dispatch 手动触发）：发布打包 → 定位产物目录 → `verify-package.ps1` → **`--selftest` 无头自测** → `pack.ps1` → 上传产物。YAML 已通过解析校验（7 步）。

> ⚠️ 该 workflow **尚未在 GitHub Actions 上实跑过**（本环境无法触发），首次运行请人工确认。

### 一处自我修正：撤销「ToneMapper 高光削平」

初版报告把 `ToneMapper.SegmentedReinhardMap` 的高光硬削平列为 P1，理由是"高光细节全失、`DisplayMaxNits` 完全失效、代价被低估"。**复核后确认这个判断是错的**，予以撤销：

1. **数学上它是唯一解。** 三条约束——SDR 白点保真 `f(1)=1`、输出不超上界 `f(y) ≤ 1`（8-bit SDR）、单调不减——同时对 `y > 1` 成立时：`f(y) ≥ f(1) = 1`（单调）且 `f(y) ≤ 1`（上界）⇒ `f(y) ≡ 1`。想保留高光层次，只能放弃"SDR 白点保真"（即把 SDR 内容整体压暗），那会让所有普通 SDR 截图变暗，代价更大。
2. **`DisplayMaxNits` 并没有失效。** 它在该函数里不参与运算是对的，但在 HDR 路径上大量且正确地使用：`FormatEncoders.cs:134`（AVIF `intensityTarget`）、`:191`（`peakNits`）、`JpegGainMapEncoder.cs:235`（GainMap 的 `hdrPeakNits`）、`GpuToneMapper.cs:259`（GPU 常量缓冲）。
3. **高光信息由 GainMap 承载**，HDR 查看器据 `gain = log2(y / f(y)) = log2(y)` 完整还原——这正是当前架构的正确分工。

**教训**：初版判断只看了"参数没被使用"这一现象，没有追到它在别处的正确使用，也没有验证注释里的数学推导。**"参数未参与运算"不等于"功能失效"**，必须先在调用链上确认该参数的其它消费点。

### `.git` 170 MB 的完整分析（已查清，建议不要单独处理）

实测对象构成（>1MB 的 blob）：

| 大小 | 路径 | 状态 |
|---|---|---|
| 73.1 MB | `publish/PLAN/models/PP-OCRv6_medium_rec.onnx` | 已从 HEAD 删除，**历史 blob 仍在** |
| 59.2 MB | `publish/PLAN/models/PP-OCRv6_medium_det.onnx` | 同上 |
| 36.6 MB | `src/TrueToneCap.App/Models/PP-OCRv6_medium_rec.onnx` | **当前仍在版本库中**（正在使用的模型） |
| 29.7 MB | `src/TrueToneCap.App/Models/PP-OCRv6_medium_det.onnx` | 同上 |
| 11.8 / 5.1 / 4.5 MB | `Core/Resources/` 下 avifenc / cjpegli / cjxl | 当前在用，属必要 |

**结论：不要单独做"移除 OCR 模型"这一项**，理由：

1. **收益有限**。当前在库的模型只有 66 MB；而历史里已删除的 132 MB 必须靠 `filter-repo` 重写历史才能清除。只做前者，`.git` 约从 170 MB 降到 ~104 MB，大头还在。
2. **代价是给 CI 引入外网依赖**。`download_models.ps1` 是**无条件下载**（不检查文件是否已存在、每次 69 MB、源站在 HuggingFace），既拖慢 CI，也带来网络不稳导致的随机构建失败。
3. **配套改动牵一发动全身**：移除模型 → 必须加 `.gitignore` 规则 → 必须给 CI 加下载步骤 → 要真正瘦身还得 `filter-repo` + `gc --prune=now` + **强推**（重写历史）。

**建议**：要么维持现状（170 MB 对一个含 native 工具链的仓库尚可接受），要么**一次性**规划为独立任务：移除模型 + `.gitignore` + CI 下载步骤 + 清理历史 + 协调强推。

#### 实测结果：filter-repo 在本环境不可用，已回滚（2026-09-15）

经授权后实际执行了一次，**失败并造成仓库损坏**，已完整恢复。过程与教训记录在此，避免重复踩坑：

| 阶段 | 结果 |
|---|---|
| `git add -A` + `git reset --hard HEAD` | 报 "Updating files 100%"，但**只写回部分文件**，61 个文件落到 ` D`（索引删除 + 磁盘也没了）→ 用 `git checkout -f HEAD -- .` 救回 |
| `git filter-repo --invert-paths --path … --force` | 历史重写成功（新 HEAD `13f5282`），**但随后 `git gc --prune=now` 失败**：`fatal: not a git repository (or any of the parent directories): .git` |
| 事后状态 | `.git` 只剩 `info/` 与 `objects/` 两个空目录（refs/HEAD/config/pack 全无），`git rev-parse` 失败；工作区 cs 文件从 151 掉到 81 |
| 恢复 | 由事前备份完整还原：历史回到 `85d876a`，24 项改动逐条核对一致，构建 0 警告 0 错误、门禁 355 全绿 |

**备份先行是这次能全身而退的唯一原因**：`git bundle create --all`（157 MB，含 main/origin/各 tag）+ `git diff --cached --binary` 补丁（86 KB）+ 新增文件副本 + 状态清单。恢复步骤见项目记忆。

#### 后续：改用「副本验证 → 替换 .git」，瘦身成功

上述失败**不是 filter-repo 本身不可用**，而是真实仓库在做过滤前已因 checkout 写入不完整而处于不一致状态。改为在副本上操作后**一次成功**：

| 步骤 | 结果 |
|---|---|
| 从 bundle 克隆副本到 `.workbuddy-ai/filter-test` | `.git` 157 MB |
| 副本内执行 `filter-repo --invert-paths --path … --force` | ✅ 成功，`.git` **157 MB → 57 MB**，新 HEAD `13f5282` |
| 副本出现大量 ` D`（沙箱下 checkout 只写出部分文件） | `git checkout -f HEAD -- .` 兜底恢复 |
| 真实仓库 `mv .git <备份>` + `cp -r 副本/.git .git` | 历史替换为已过滤版本 |
| 重新添加 origin（filter-repo 会移除 remote） | 已添加 |
| 构建 + 门禁 | **0 警告 0 错误，355 断言全绿**，25 项改动完好 |

同时给 `.gitignore` 增加 `src/TrueToneCap.App/Models/*.onnx`，防止模型再次被误纳入（同目录的 `ppocrv6_dict.txt` 是 74 KB 文本，仍入库）。

#### 发布产物端到端实测：`--selftest` 全绿

用 `-AllowDirty -SkipTests` 跑通完整发布流程（178.8 s，566 个文件），验证本轮对 `Publish.ps1` 的改动全部生效：

- 🔒 模型 SHA256 校验通过（`PP-OCRv6_medium_det/rec.onnx`）—— 本轮新增，此前模型侧没有任何完整性校验
- ✅ 输出目录白名单校验通过
- ✅ 锁文件归一化执行、AOT 假生效改为明确告警

随后在**发布产物**上运行 `--selftest`：

| 格式 | 状态 | 耗时 |
|---|---|---|
| PNG（托管） | ok | 60 ms |
| TIFF（托管） | ok | 22 ms |
| JPEG LI（native jpegli） | ok | 457 ms |
| JPEG XL（native cjxl） | ok | 446 ms |
| AVIF（native avifenc） | ok | 960 ms |
| WebP（native cwebp） | ok | 418 ms |

**6/6 全部通过，`failed=0`，退出码 0，总耗时 3.4 s。**

这同时证明了 `fallback` 检测逻辑是有效的：开发态 `native/` 为空时它正确报 `fallback`（不静默当成功），而发布产物上 native 齐全故不触发。P0-6 想要的效果——"CI 能真正跑一遍编码链路"——已经具备。

> 该产物 `build-info.json` 标记为 `dirtyWorktree=true`（脏工作区发布，仅用于验证），不可作为正式发布版本。

**一个反复踩到的统计陷阱**：用 `find src -name "*.cs"` 判断"文件是否丢失"会把 `bin/obj` 下的生成文件算进去（源码实际只有 99 个 .cs）。判断完整性必须用 `git ls-tree -r HEAD --name-only | grep -c "\.cs$"`。我因此误判过两次。

#### ⛔ 但最终回退了：模型出库这条路走不通

过滤做完后去验证"CI 之后怎么拿到模型"，结论是**不能可靠获取**，因此整体回退（`.git` 换回过滤前版本，HEAD 恢复 `85d876a`，模型重新入库，`.gitignore` 里的忽略规则改为注释说明）。

`download_models.ps1` 拿不到项目实际使用的 medium 模型：

| 场景 | 实际行为 |
|---|---|
| 本机装了 `rapidocr` | 把 **small** 版 det（9.47 MB）复制成 `PP-OCRv6_det.onnx` 顶替；而代码加载的是 `PP-OCRv6_medium_det.onnx`（29.7 MB）—— 该副本既不会被加载，又留下一个易混淆文件 |
| CI 等干净环境 | 需从 HuggingFace 拉取 69 MB，源站可达性与网络都不可控，**也没有转换 medium 所需的 paddle2onnx 链路** |

⇒ 模型一旦出库，CI 与新克隆都无法稳定获得它，OCR 功能与发布校验会直接失败。**省下 100 MB 换这个风险不值得。**

**若将来仍要瘦身**，前提是先把模型放到可靠分发点（如 GitHub Release 资产），然后**一次性**配套改动：忽略规则 + CI 下载步骤 + 清理历史 + 协调强推。不要单独做其中任何一项。

#### 附带修掉的两个问题

1. **`download_models.ps1` 不再复制 small 模型**——改为仅提示"本项目需要 medium"。实测不再产生 `PP-OCRv6_det.onnx`，目录保持干净。
2. **`models-manifest.json` 不收录 `ppocrv6_dict.txt`**——它是文本文件，受 git eol 规范化影响：HEAD blob 存 LF（`b5f2bfe…`），工作区按 `core.autocrlf` 检出可能是 CRLF（`769e7fa…`），**同一个文件两种哈希**。写进清单会让发布校验随开发者本地 eol 设置随机失败。清单现只收录 2 个 onnx（二进制，eol 安全），字典的完整性由 git 自身保证。

> 这个 eol 陷阱一度让我误判为"下载脚本污染了字典"，`grep` 显示脚本根本不碰字典后才定位到真实原因。判断"文件是否被改动"时，务必先排除 eol 因素。

### 本轮未修（需决策或改动较大）

- **P0-4 的架构部分**：MFT / NVENC / QSV 的**子进程隔离改造**（进程内 catch 已被证实无效，只能改架构）
- **P0-6 CI 发布产物校验** —— 需新增 release workflow + 给主程序加 `--selftest` 无头模式
- **P0-7 预发布 RC 依赖**（DI `11.0.0-rc.1` + preview SDK）—— 涉及是否回退 10.0.x LTS 线，属产品决策
- ~~**P1-10 `ToneMapper` 高光硬削平**~~ —— **已复核撤销**：该设计在数学上是三重约束下的唯一解，且 `DisplayMaxNits` 在 HDR/GainMap 路径正确使用，无需改动（详见下）
- **P2**：fxc 版本未钉、着色器三清单无自动校验、`publish/PLAN/models` 已入库（`.git` 170 MB 成因之一）、AOT 开关空转、`dotnet-tools.json` 位置错误

> ⚠️ 按项目规则（2026-09-15）：**所有改动需等本地实机验证完成后再提交 git。**
> 上述第二轮修复已通过构建（0 警告 0 错误）与门禁（350 断言全绿 + WGC 21），但**尚未 commit**，等待实机验证。

---

## 附：本次审查方法说明

- 实测执行了 Release 全量构建与 `--all` 门禁，结果见第 1 节。
- 四路并行深度审查（构建依赖 / CI 发布 / 测试 / 运行时核心与 OCR）的**每一条 P0 均经逐条读码复核**，复核方式：直接 `sed` 读关键行、`grep` 验证属性声明、`git ls-tree HEAD` 验证入库状态、`cat` 发布产物 `runtimeconfig.json`。
- 未修改任何源码；未执行 commit / push（遵守项目铁律，未经明确授权不提交）。
