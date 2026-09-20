# TrueToneCap 完整管线审查规划

- 制定日期：2026-09-14
- 适用版本：`v0.3.4-beta`（当前待提交批次，88 项改动）
- 前置文档：`cicd-pipeline-review-`、`pipeline-review-round2-`、`pipeline-review-e2e-`、
  `docs-staleness-audit-`、`git-tracking-audit-`、`dependency-inventory-`、`re-audit-`、
  `pending-changes-triage-`、`test-verification-`（均 `-2026-09-14`）

---

## 执行进度（2026-09-14 实跑结果）

| 项 | 结果 | 证据 |
|---|---|---|
| 0.1 构建 | ✅ | `dotnet build -c Release` RC=0，5/5，**0 警告 0 错误**（22.2s） |
| 0.2 测试门禁 | ✅ | 8 套件 **373 断言**（372 通过 + 1 跳过）/ 0 失败，RC=0 |
| 0.3 锁文件 | ✅ | `--locked-mode` RC=0；Core md5 `88353b61…` 与基准一致，`win-x64` 段 0 处 |
| 0.4 版本自洽 | ✅ | props `0.3.4-beta` / FileVersion `0.3.4.0` / ProductVersion `0.3.4-beta+0916a2a…` == HEAD |
| 0.5 脚本语法 | ✅ | 17 个 `.ps1` 全部解析通过，0 失败 |
| 0.6 暂存区审计 | ✅ | 76 文件，**0 必须修**（51 条均为注释/文档里的版本号误报） |
| 0.7 死链 | ✅ | 0 死链 |
| 1.x 测试语义（N2） | ✅ **已修** | 见下节 |
| 3.5 钩子模式 | ✅ | 两个钩子 `100755`；blob **CR=0 / CRLF=0**，首行 `#!/bin/sh` |
| 4.5 CI 不重复实现 | ✅ | `ci.yml` 仅调 `scripts/verify.ps1`，未自写 `dotnet run … -- <suite>` |
| 5.6 大文件登记 | ✅ | 3 个 >10 MB：`rec.onnx` 36 MB / `det.onnx` 29 MB / `avifenc.exe` 11 MB；`.git` 170 MB |
| 3.4 门禁回归接入（N1） | ⚠️ **已接入，需真终端验证** | `verify.ps1` 新增 `[1.6/2]` 步骤，默认执行；`pre-push` 与 CI 自动覆盖（见下节） |
| D4/D5 发布链路静态审查 | ✅ | 归一化步骤位于 `Publish.ps1:463-479`，早于 `[6/6]` 计算 `lockFilesSha256`（L638）✅；`$lockProjects` 5 个与 CI preflight 一致；`$toolsInfo` 深度 2，`ConvertTo-Json -Depth 4` 足够 |
| 5.5 文档命令对账 | ✅ | README 全部脚本调用 ↔ 脚本 `param` 块逐一对账，**0 漂移**（见下） |

#### 5.5 文档 ↔ 脚本参数对账（2026-09-14）

| README 写法 | 脚本实际 | 结论 |
|---|---|---|
| `install-git-hooks.ps1 -Status` / `-Uninstall` | 两个 `[switch]` 均存在 | ✅ |
| `Publish.ps1 -Version v0.3.4-beta` | 有 `$Version`，且 **L97 会剥离 `v` 前缀** | ✅ |
| `Publish.ps1 -NoReadyToRun` | 存在 | ✅ |
| `pack.ps1 -PackageDir …` | 存在（Mandatory） | ✅ |
| `verify-package.ps1 -PackageDir … -ExpectedVersion …` | 两者均存在 | ✅ |

> 注：`-ExpectedCommit` 虽存在但 README 未列 —— 不传时脚本默认比对 HEAD，行为等价，不补。

### N1 修复：给 `verify-publish-gates.ps1` 接上自动调用方

`verify.ps1` 新增 `[1.6/2]` 步骤（位于格式检查之后、定位测试 exe 之前），默认执行，
可用 `-SkipPublishGates` 关闭。三个关键设计决策：

1. **必须用子进程隔离**。该脚本用 `function global:git` / `global:dotnet` 给外部命令打桩，
   若在同一会话内点源执行，桩会一直生效，之后 `verify.ps1` 的 `dotnet build` 会调到桩上，
   构建被静默跳过 —— 这是最难排查的假绿灯。
2. **不写死 `pwsh` / `powershell`**。改用 `(Get-Process -Id $PID).Path` 取当前宿主自身启动子进程，
   既不依赖具体宿主名（自托管机器可能只有 PS 5.1），又能保证桩只活在子进程里。
   本机实测该探测返回 `C:\Program Files\PowerShell\7\pwsh.EXE`，路径有效。
3. **观察期：只告警不阻断**。这一步本环境**无法实跑验证**（PS 工具会话无法启动原生进程，
   实测报"无法在管道中间运行文档"）。为避免一个未验证的新步骤阻塞发布，首次接入设为告警；
   确认稳定后把代码里的 `exit 1` 打开即升级为阻断门禁。

**覆盖情况**：`pre-push`（不带参数）✅ 与 CI（`pwsh ./scripts/verify.ps1`）✅ 均会自动执行；
`pre-commit` 用 `-BuildOnly`，在该步骤之前就返回，保持快速反馈 —— 符合原设计取舍。

**待验证**：在真终端跑一次 `.\scripts\verify.ps1`，确认 `[1.6/2]` 输出
`✅ 发布门禁回归通过`。若报环境错误，观察期设计保证不会阻塞发布，但需据此调整实现方式。

### N2 修复：8 处「伪装跳过」的恒真断言

`TestSummary.cs` 的注释已明令禁止「伪装通过 = 恒真断言」，但代码里仍有 8 处在环境不满足时
执行 `Assert(..., true, "跳过…")`。本次全部改为显式 `Skip()`：

| 文件 | 原写法 | 现写法 |
|---|---|---|
| `UsabilityTests.cs` ×4 | `Assert(..., true, "跳过(无 D3D)")` 等 | `Skip(name, reason)`（该套件本就有 `Skip()`，只是自己没用一致） |
| `FormatStructureTests.cs` ×2 | `Assert("AVIF/box 结构", true, "跳过 (不可用…)")` | 新增 `_skipped` + `Skip()`，Report 传第 6 参数 |
| `EncodingIntegrationTests.cs` ×1 | `Assert(name, true, "跳过 (不支持 HDR)")` | 同上 |
| `WgcAvifBackendTests.cs` ×1 | `Assert($"{backend}/跳过(不可用)", true, "")` | 同上 |

**验证结果**：usability 从 `passed=153` 变为 `passed=152 / skipped=1`，
总数 372 + 1 = **373 不变**，退出码仍 0 —— 说明只是把"伪装成通过"如实改判为"跳过"，
没有掩盖任何真实失败。

保留未改：`UsabilityTests.cs:1349` 的 `Assert("GetMonitorUnderCursor: 不崩溃", true)`
—— 它有 `HasDisplays()` 前置守卫且真实执行调用，属合法的"不崩溃"断言。

**待你决策**：`WgcAvifBackendTests.cs:79-83` 在无法捕获桌面帧时 `Assert(..., false)` 判失败。
按 `TestSummary.cs` 注释这同属反模式（"当成失败 = 永远进不了门禁"），但该套件本就设计成
"需桌面+GPU"、不在 7 套件门禁内。改成跳过会掩盖真实的捕获故障，故**暂不改**。

### 一个自我更正

审查中途我据 `grep -c $'\r$'` 报出「钩子 blob 有 52 行 CRLF」，并判定为 P0。
**该结论是错误的**——Git Bash 的 grep 对 `$'\r$'` 解释有误，产生全行误报。
改用 Python 逐字节核验：blob `CR=0 / CRLF=0`、首行 `#!/bin/sh`，**钩子换行符本来就是正确的**。
教训：涉及字节级断言时不要用 shell grep，用能数字节的手段复核。

---

## 0. 这份规划要解决什么

此前 9 份审查文档是**发现驱动**的：查到一处改一处，改完再查。问题是留下了两类空洞：

1. **没有"我还没验证过什么"的权威清单** —— 于是出现了"CI 工作流、git 钩子端到端、
   `install-git-hooks.ps1`、`verify.ps1 -BuildOnly` 在本环境根本没跑过就提交"的事故。
2. **审查覆盖不均** —— 构建/发布链路被反复过筛，而测试报告语义、CI 可运行性、
   门禁回归的自动调用这些角落没人碰。

本规划把管线切成 **8 个域**，对每个域给出「已验证 / 未验证 / 无法在本环境验证」三态，
并给出**验收标准必须是可观测的退出码或文件产物**，不接受"看了一遍觉得没问题"。

### 三条硬约束（承接项目铁律）

| 约束 | 含义 |
|---|---|
| **未验证不提交** | 「提交信息里标注未验证」不算合规。标注只是诚实，不等于验证过。 |
| **一个版本一笔提交** | 不再有 `feat:` / `chore:` 与 `release:` 混排。 |
| **验收须可复现** | 每条检查项要么给出命令 + 退出码，要么给出产物文件路径 + 校验方法。 |

---

## 1. 现状快照（2026-09-14 实扫，非记忆推断）

### 1.1 规模

| 项目 | .cs 文件 | 说明 |
|---|---|---|
| `TrueToneCap.Core` | 38 | 色彩、编码、OCR、原生工具封装 |
| `TrueToneCap.App` | 33 | WinUI 3 主程序 |
| `TrueToneCap.Tools` | 13 | 命令行工具 |
| `TrueToneCap.Test` | 10 | 8 个测试套件 |
| `TrueToneCap.LogViewer` | 3 | 日志查看器 |
| **合计** | **97** | |

### 1.2 管线资产清单

| 类别 | 资产 | 入库 | 状态 |
|---|---|---|---|
| 构建入口 | `TrueToneCap.slnx` | ✅ | 已实跑 |
| 配置单一源 | `Directory.Build.props` / `Directory.Packages.props` / `global.json` / `NuGet.config` | ✅（本次新增后两位） | 已实跑 |
| 锁文件 | 5 × `src/*/packages.lock.json` | ✅ | 已实跑（`--locked-mode` RC=0） |
| 测试入口 | `src/TrueToneCap.Test`（**非** `dotnet test`） | ✅ | 已实跑 373 断言 |
| 发布 | `Publish.ps1` | ✅ | **部分实跑**（[2] publish 跑过，全链路未跑） |
| 打包 | `scripts/pack.ps1` | ✅ | ❌ 未实跑 |
| 出包校验 | `scripts/verify-package.ps1` | ✅ | ❌ 未实跑 |
| 开发期门禁 | `scripts/verify.ps1` | ✅ | ❌ 未实跑（`-BuildOnly` 尤其） |
| 门禁回归 | `scripts/verify-publish-gates.ps1` | ✅ | ⚠️ 曾跑通 5/5，**但无自动调用方** |
| 钩子安装 | `scripts/install-git-hooks.ps1` | ✅ | ❌ 未实跑 |
| git 钩子 | `pre-commit` / `pre-push` | ✅ 均 `100755` | ❌ 未端到端实跑 |
| CI | `.github/workflows/ci.yml` | ✅ | ❌ 未端到端实跑 |
| 发布工作流 | — | — | **不存在**（只有 CI，无 release workflow） |
| 代码签名 | — | — | **不存在**（全文无 `signtool`/`.pfx`/Authenticode） |

### 1.3 本轮实扫新发现（此前 9 份文档均未记录）

| # | 发现 | 影响 |
|---|---|---|
| N1 | `scripts/verify-publish-gates.ps1` **没有任何自动调用方**（不被 CI、不被钩子、不被 `verify.ps1` 引用） | 门禁回归只在人工想起时跑，回归保护形同虚设 |
| N2 | 8 个测试套件中**只有 `UsabilityTests` 传了 `skipped` 参数**给 `TestSummary.Report`，其余 7 个不传 | 若其余套件存在环境依赖用例，会被计为"通过"或"失败"而非"跳过"，静默污染门禁 |
| N3 | CI 用 `shell: pwsh` + `pwsh ./scripts/verify.ps1` | self-hosted Windows 机器默认只有 Windows PowerShell 5.1，未装 PowerShell 7 时**步骤直接失败** |
| N4 | CI `runs-on: [self-hosted, Windows, X64]` | 若无自建运行器，CI **永远不会执行**，绿灯假象 |
| N5 | 无 release workflow，发版全靠本地手工 `Publish.ps1` + `pack.ps1` | 产物可复现性依赖个人机器状态 |

---

## 2. 审查域矩阵

| 域 | 覆盖内容 | 本环境可验 | 当前三态 |
|---|---|---|---|
| **D1 配置与依赖** | MSBuild 单一源、CPM、锁文件、RID 漂移、SDK 钉版 | ✅ 完全 | 🟢 已验证 |
| **D2 构建** | 解决方案构建、着色器双路径、警告即错误 | ✅ 完全 | 🟢 已验证 |
| **D3 测试** | 8 套件、跳过语义、断言计数、假绿灯防护 | ✅ 完全 | 🟡 部分（N2 待查） |
| **D4 发布** | `Publish.ps1` 全链路、锁文件归一化、工具清单校验 | ⚠️ 部分 | 🟡 部分 |
| **D5 打包与出包校验** | `pack.ps1`、`verify-package.ps1`、内嵌 commit | ❌ 不可 | 🔴 未验证 |
| **D6 本地门禁** | `verify.ps1`、git 钩子、`install-git-hooks.ps1` | ❌ 不可 | 🔴 未验证 |
| **D7 CI** | `ci.yml`、运行器、pwsh 可用性 | ❌ 不可 | 🔴 未验证 |
| **D8 仓库卫生** | 大文件、历史 blob、文档一致性、死链 | ✅ 完全 | 🟡 部分（瘦身未做） |

图例：🟢 已验证（有退出码/产物佐证）｜🟡 部分验证｜🔴 未验证

---

## 3. 分阶段执行计划

### 阶段 0：发布阻塞项（必须与 v0.3.4-beta 同批完成）

> 目标：让 `v0.3.4-beta` 这一笔提交**每一条都被实跑验证过**。

| # | 检查项 | 方法 | 验收标准 | 执行环境 |
|---|---|---|---|---|
| 0.1 | 构建零告警 | `dotnet build TrueToneCap.slnx -c Release` | RC=0，5/5 项目，0 警告 0 错误 | 沙箱 ✅ |
| 0.2 | 门禁 7 套件全绿 | `dotnet run --project src/TrueToneCap.Test -c Release -- --all` | 352 断言 / 0 失败 / 0 跳过 | 沙箱 ✅ |
| 0.3 | 锁文件无 RID 漂移 | `dotnet restore TrueToneCap.slnx --locked-mode` | RC=0，Core 锁文件 0 个 `win-x64` 段 | 沙箱 ✅ |
| 0.4 | 版本自洽 | 解析 `Directory.Build.props` `<Version>` + 产物 `FileVersion` | 二者一致；`ProductVersion` 的 commit 段 == HEAD | 沙箱 ✅（PS .NET API） |
| 0.5 | 脚本语法 | `Parser::ParseFile` 逐个解析 `.ps1` | 全部无 ParseError | 沙箱 ✅ |
| 0.6 | 暂存区审计 | `.workbuddy-ai/pre-commit-audit.py` | 0 个 must-fix | 沙箱 ✅ |
| 0.7 | 死链 | `.workbuddy-ai/link-check.py` | 0 死链 | 沙箱 ✅ |
| 0.8 | **完整发布实跑** | `Publish.ps1 -Tag` → `pack.ps1` → `verify-package.ps1` | 三步均 RC=0，包内 `SHA256SUMS.txt` 校验通过 | **真终端** ⚠️ |
| 0.9 | 钩子端到端 | `install-git-hooks.ps1` 后做一次真实 `git commit` | 钩子确实执行（日志可见），非静默跳过 | **真终端** ⚠️ |

**顺序约束（易错）**：0.8 必须在 `git commit` **之后**执行。
`Publish.ps1 -Tag` 是 `git tag -a v$Version` 打在**当前 HEAD** 上；当前 HEAD 仍是
`0916a2a release: v0.3.3-beta`，先发布会把它错误地标成 `v0.3.4-beta`。
同时 `verify-package.ps1` 校验「产物内嵌 commit == HEAD」，脏工作区出包后提交会让该校验必然失败。

```
verify.ps1 → [你本地审查] → git commit -m "release: v0.3.4-beta"
           → Publish.ps1 -Tag → pack.ps1 → verify-package.ps1
           → git push origin main --follow-tags
```

> 纠正：`-Force` 在 `Publish.ps1` 里**只用于强杀运行中的 `TrueToneCap.exe`**（L365），
> 不是"允许清空输出目录"；L376 的 `Remove-Item -Recurse -Force $OutputDir` 本来就是无条件的。
> 目标目录已清理 ⇒ 现在跑**不需要 `-Force`**。

---

### 阶段 1：测试语义（解决 N2）

| # | 检查项 | 验收标准 | 环境 |
|---|---|---|---|
| 1.1 | 逐个检查 8 个套件的 `TestSummary.Report` 调用 | 列出每个套件是否有环境依赖用例 | 沙箱 ✅ |
| 1.2 | 对存在环境依赖但**未传** `skipped` 的套件补齐 | 该套件在环境不满足时输出 `skipped=N` 而非 0/失败 | 沙箱 ✅ |
| 1.3 | 复跑 8 套件，确认断言总数不降级 | 仍 373 断言（或更高），0 失败 | 沙箱 ✅ |
| 1.4 | 确认 `dotnet test` 的假绿灯有显式防护 | 文档/README 明确写"不要用 dotnet test"；或加保护 | 沙箱 ✅ |

---

### 阶段 2：发布与打包（解决 D5）

| # | 检查项 | 验收标准 | 环境 |
|---|---|---|---|
| 2.1 | `pack.ps1` 实跑 | 生成 `TrueToneCap-v0.3.4-beta-win-x64.7z`，无 7-Zip 时退化 .zip 并告警 | 真终端 ⚠️ |
| 2.2 | `verify-package.ps1` 实跑（含 native 启动探针） | RC=0；故意破坏一个文件后 RC≠0（**反向测试**） | 真终端 ⚠️ |
| 2.3 | 锁文件归一化生效 | publish 后 5 个锁文件 md5 与提交版本逐字节一致 | 真终端 ⚠️（沙箱可复算 md5） |
| 2.4 | `Publish.ps1` 失败路径 | 故意删掉一个工具 exe，确认 `[0/6]` 抛错而非继续 | 真终端 ⚠️ |

---

### 阶段 3：本地门禁（解决 D6 + N1）

| # | 检查项 | 验收标准 | 环境 |
|---|---|---|---|
| 3.1 | `install-git-hooks.ps1` 实跑 | `core.hooksPath` 被设置，指向 `scripts/git-hooks` | 真终端 ⚠️ |
| 3.2 | `pre-commit` 端到端 | 真实 commit 时钩子执行；故意引入编译错误应被拦下 | 真终端 ⚠️ |
| 3.3 | `pre-push` 端到端 | 真实 push 时执行全量门禁 | 真终端 ⚠️ |
| 3.4 | **把 `verify-publish-gates.ps1` 接入 `verify.ps1`**（N1） | `verify.ps1` 新增开关或默认步骤调用它；CI 与 pre-push 均自动跑到 | 沙箱可写，真终端验 |
| 3.5 | 钩子模式复核 | `git ls-files --stage` 两个钩子均为 `100755`；blob 内无 `\r\n`；有 shebang | 沙箱 ✅ |
| 3.6 | 新克隆自检 | 干净克隆 → 跑安装脚本 → 构建，确认无"只在我机器上能编"的依赖 | 真终端 ⚠️ |

---

### 阶段 4：CI 可运行性（解决 D7 + N3 + N4）

| # | 检查项 | 验收标准 | 环境 |
|---|---|---|---|
| 4.1 | 确认是否真有 self-hosted 运行器 | 有 → 继续；无 → 改造为 `windows-latest` 或关闭 CI 并如实标注 | **需你确认** |
| 4.2 | 运行器上 pwsh 可用性 | `pwsh -v` 有输出；否则改 `shell: powershell` 并验证 5.1 兼容 | 真终端 ⚠️ |
| 4.3 | `workflow_dispatch` 手动跑一次 | 全流程 RC=0，前置检查四步全绿 | 真终端 ⚠️ |
| 4.4 | 反向测试：故意破坏锁文件 | CI 还原步骤应失败并给出可读报错 | 真终端 ⚠️ |
| 4.5 | CI 与本地脚本无重复实现 | CI 只调 `scripts/verify.ps1`，不自己写 `dotnet run --project ... -- <suite>` | 沙箱 ✅（已符合） |

---

### 阶段 5：仓库卫生与可选增强

| # | 检查项 | 验收标准 | 环境 | 优先级 |
|---|---|---|---|---|
| 5.1 | 历史 blob 瘦身（P2-14） | `git filter-repo` + `gc --prune=now`，`.git` 从 170 MB 降到 < 40 MB | 真终端 ⚠️ | P2 |
| 5.2 | 瘦身影响面评估 | 确认 tag / 备份分支 / origin 的处理方式，**先建镜像备份** | 真终端 ⚠️ | P2（5.1 前置） |
| 5.3 | 代码签名（P1-8） | 有证书后加签名步骤；无证书则在 README 明示"未签名，SmartScreen 会告警" | 阻塞于证书 | P1 |
| 5.4 | release workflow（N5） | 新增 `.github/workflows/release.yml`，`workflow_dispatch` 触发发布打包 | 真终端 ⚠️ | P2 |
| 5.5 | 文档一致性复检 | 文档步骤名 ↔ 脚本实际步骤名逐一对账（此前发现 `install-hooks.ps1` 名称漂移） | 沙箱 ✅ | P2 |
| 5.6 | 大文件登记 | 列出现跟踪 > 10 MB 文件并说明必要性（模型 38/31 MB、avifenc 12 MB） | 沙箱 ✅ | P3 |

#### 5.1 / 5.2 实测数据（2026-09-14，`git rev-list --objects --all`）

| 解压后大小 | 路径 | 是否可瘦身 |
|---|---|---|
| 73.1 MB | `publish/PLAN/models/PP-OCRv6_medium_rec.onnx`（历史） | ✅ **可删**（已解除跟踪，只活在历史里） |
| 59.2 MB | `publish/PLAN/models/PP-OCRv6_medium_det.onnx`（历史） | ✅ **可删**（同上） |
| 36.6 MB | `src/TrueToneCap.App/Models/PP-OCRv6_medium_rec.onnx` | ❌ 当前在用，保留 |
| 29.7 MB | `src/TrueToneCap.App/Models/PP-OCRv6_medium_det.onnx` | ❌ 当前在用，保留 |
| 11.8 MB | `src/TrueToneCap.Core/Resources/avifenc.exe` | ❌ 当前在用，保留 |
| 5.1 MB | `src/TrueToneCap.Core/Resources/cjpegli.exe` | ❌ 当前在用，保留 |

**结论**：`.git` 170 MB 的主体是**已解除跟踪却仍留在历史里**的两个旧模型（132 MB 解压后，
旧版 `rec.onnx` 73 MB 比现版 37 MB 大一倍，应是 fp32 → 量化的更替）。
瘦身后预计降到 40 MB 量级。

**风险（必须先于 5.1 完成 5.2）**：`git filter-repo` 会**重写全部 commit SHA**，
波及 5 个 tag、`backup-before-reset-20260914` / `backup-unverified-20260914` 两个备份分支、
以及 `origin/main`。执行前必须：
1. `git clone --mirror` 建独立镜像备份，并验证备份可检出；
2. 确认无他人基于旧 SHA 工作（本仓只有 `main` 一个远程分支，风险可控）；
3. 瘦身后**所有克隆必须重新拉取**，不能靠 `git pull` 收敛。

---

## 4. 未验证项追踪表（提交前的准入门槛）

> **凡在此表为 🔴 的项，不得进入 v0.3.4-beta 提交**，除非在"处置"列写明降级理由并经你确认。

| 项 | 域 | 状态 | 处置 |
|---|---|---|---|
| `Publish.ps1` 全链路（[1] 清目录 → [6] build-info） | D4 | 🔴 | 真终端实跑 |
| `Publish.ps1 -Tag` 打标签 | D4 | 🔴 | 真终端实跑 |
| `pack.ps1` | D5 | 🔴 | 真终端实跑 |
| `verify-package.ps1`（含 native 探针） | D5 | 🔴 | 真终端实跑 + 反向测试 |
| `verify.ps1`（含 `-BuildOnly`） | D6 | 🔴 | 真终端实跑 |
| `install-git-hooks.ps1` | D6 | 🔴 | 真终端实跑 |
| `pre-commit` / `pre-push` 端到端 | D6 | 🔴 | 真终端实跑 |
| `ci.yml` 首次运行 | D7 | 🔴 | 真终端 `workflow_dispatch` |
| CI 运行器存在性 | D7 | 🔴 | **需你确认**（无运行器则 CI 是假绿灯） |
| `verify-publish-gates.ps1` 自动调用 | D6 | 🔴 | 阶段 3.4 接入后验 |
| 7 个测试套件的 `skipped` 语义 | D3 | 🟡 | 阶段 1 排查 |
| 历史 blob 瘦身 | D8 | 🔴 | P2，非本版阻塞 |
| 代码签名 | — | 🔴 | 阻塞于证书，非本版阻塞 |

---

## 5. 执行顺序建议

```
阶段 0（阻塞发布）
  ├─ 沙箱可做的 0.1–0.7   ← 现在就能推进
  └─ 你的本地审查
       └─ git commit "release: v0.3.4-beta"
            └─ 0.8 完整发布实跑（真终端）
            └─ 0.9 钩子端到端（真终端）
                 └─ git push origin main --follow-tags

阶段 1（测试语义）      ← 可与阶段 0 并行，沙箱内完成
阶段 2（发布与打包）    ← 依赖阶段 0 的真终端执行
阶段 3（本地门禁）      ← 依赖真终端
阶段 4（CI）            ← 依赖你的运行器确认
阶段 5（卫生与增强）    ← 独立于发版，可长期推进
```

**我在此刻能独立完成的**：0.1–0.7、阶段 1 全部、3.5、4.5、5.5、5.6。
**必须你在真终端完成的**：0.8、0.9、阶段 2、阶段 3（除 3.5）、阶段 4。
**需要你决策的**：是否有 self-hosted 运行器（4.1）、是否有代码签名证书（5.3）。

---

## 6. 风险登记册

| 风险 | 触发条件 | 后果 | 缓解 |
|---|---|---|---|
| CI 是假绿灯 | 无 self-hosted 运行器 | 仓库显示 CI 通过，实际从未跑过 | 4.1 先确认；无则改造或显式关闭 |
| 钩子静默失效 | `.gitattributes` 未锁 `eol=lf`、或模式为 `100644` | 提交无门禁保护 | 3.5 每次提交前复核 |
| 门禁回归失守 | `verify-publish-gates.ps1` 无调用方 | 门禁逻辑腐化到发版当天才发现 | 3.4 接入 `verify.ps1` |
| 跳过语义缺失 | 套件环境依赖未报 skipped | 门禁被恒真断言或假失败污染 | 阶段 1 |
| 锁文件 RID 漂移 | `publish -r win-x64` 后未归一化 | CI `--locked-mode` 报 NU1004 | `Publish.ps1` 已有归一化步骤（在算 `lockFilesSha256` 之前） |
| 标签打错提交 | 提交前跑 `Publish.ps1 -Tag` | v0.3.3 提交被标成 v0.3.4-beta | 严格遵守阶段 0 顺序约束 |
| 瘦身丢历史 | `filter-repo` 前未备份 | 不可逆数据丢失 | 5.2 先建镜像备份 |

---

## 7. 与既有文档的关系

本规划**不替代**既有 9 份审查文档，而是给它们补一张"完成度地图"：

- 既有文档回答「**发现了什么问题、怎么改的**」
- 本规划回答「**还有哪些没验证、按什么顺序验证、谁来验证**」

每完成一个阶段，在本文档对应表格把三态从 🔴/🟡 改为 🟢，并在"验收证据"列记入命令与退出码。
改状态的前提是**有可复现的观测证据**，不接受"看了一遍觉得没问题"。
