# PowerShell 双宿主兼容（5.1 保留 + 7.x 最新）—— 审计与修复记录

> 日期：2026-10-26
> 需求（维护者）：在继续支持 Windows PowerShell 5.1 的前提下，支持最新的 PowerShell 7.x
> （本机实装 pwsh 7.6.8；7.1 是 2020 年的 EOL 版本，对 7.x 的兼容工作同样覆盖它）。
> 方法：全仓 .ps1 审计（BOM / 7-only 语法 / 7.x 移除的 cmdlet / 钩子选主）+ 双宿主实测。

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| 脚本里有 7.x-only 语法吗？ | **零**。`-AsByteStream`/`&&`/`\|\|`/`??`/三元/`-Parallel` 全仓 0 命中（命中的 `&&` 全在 C# here-string 里）；7.x 已移除的 `Get-WmiObject` 也是 0 |
| 5.1 为什么会红？ | **不是缺命令**。是继承的 `PSModulePath` 把 pwsh 目录排前面时，5.1 模块解析抢先命中 pwsh 的 Utility 清单（.NET Framework 装不下）⇒ `Get-FileHash` "命令不存在" ⇒ 哈希门禁整层误红（复现 [1.6/2] "4/8 个场景不符"） |
| 修了什么？ | ① 脚本层：`scripts/ps-compat.ps1` 的 `Get-Sha256Hex`（.NET 直调）替换三门禁全部 `Get-FileHash`；② 环境层：钩子 5.1 回退分支 `env -u PSModulePath`；③ 编码层：83 个非 ASCII .ps1 补 UTF-8 BOM；④ 产物层：build-info.json 写入改无 BOM，双宿主同产物 |
| 顺手抓到什么？ | **与本主题无关但被这轮实测暴露的漂移**：`Publish.ps1` 的 `$gateSuites` 漏了昨天新增的 `--qr-tests` ⇒ 断言下限(1961)按 8 套件计、桩只发 7 个套件(1914) ⇒ B/D 发布场景在【两个宿主】都误红（"1/7 个套件未通过"）。已补齐（pwsh 同样红，与 5.1 无关——早上绿只是因为当时基线尚未更新） |
| 验证 | 发布门禁回归 5.1 与 pwsh 均 **8/8 场景**；5.1 全量门禁从 4/8 → 全绿；pwsh 7.6.8 全量门禁无回归（8 套件 1961 断言）；助手函数两宿主输出与 `Get-FileHash` 逐字符一致（-ceq True） |

## 1. 根因取证（不是"5.1 缺 Get-FileHash"）

2026-10-26 在 ZCode/Git Bash 会话里实测：

```
$env:PSModulePath（继承给 5.1）:
  C:\Users\20210\Documents\PowerShell\Modules      ← pwsh 用户
  C:\Program Files\PowerShell\Modules              ← pwsh 全局
  c:\program files\powershell\7\Modules            ← pwsh $PSHOME（排最前的冲突源）
  C:\Program Files\WindowsPowerShell\Modules
  C:\Windows\system32\WindowsPowerShell\v1.0\Modules  ← 5.1 自己的（排最后）

5.1 实测:
  Get-Module -ListAvailable Microsoft.PowerShell.Utility 首命中 = pwsh 7 的 psd1
  Get-FileHash      → "命令不存在"
  ConvertFrom-Json  → 可用（部分命令侥幸走通，哈希层必死）
  env -u PSModulePath 后 → Get-FileHash 恢复正常
```

**机制**：5.1 的模块自动解析按 PSModulePath 顺序扫描；pwsh 的 Utility 清单（版本
7.0.0.0、导出 99 命令）排在其自身清单之前被命中，但那是 .NET (Core) 程序集，
5.1 的 CLR 加载不了 ⇒ 命令解析失败。此前的钩子注释把它归因为"5.1 缺 Get-FileHash"，
本轮已更正。

**为什么之前只红过 pre-push、平时全绿**：钩子 [1.6/2] 用"当前宿主自身"启动发布门禁子进程
—— 外层 pwsh 时子进程也是 pwsh（绿）；外层 5.1 时子进程是被污染的 5.1（红）。

## 2. 支持矩阵与防线分层

| 宿主 | 门禁脚本 | 钩子 | 手动运行 |
|---|---|---|---|
| PowerShell 7.x（最新 7.6.x，含 7.1） | ✅ 原生 | ✅ 首选 | ✅ |
| Windows PowerShell 5.1（干净控制台） | ✅ | ✅（回退分支） | ✅ |
| Windows PowerShell 5.1（被 pwsh 污染的控制台） | ✅（脚本层防线） | ✅（env -u 防线） | ✅（脚本层防线） |

- **脚本层**：`scripts/ps-compat.ps1` 的 `Get-Sha256Hex` 用 `[System.Security.Cryptography.SHA256]`
  .NET 直调，不经任何 cmdlet 解析，两宿主输出逐字符一致（返回与 `Get-FileHash.Hash` 同形的
  大写无连字符 hex，调用方 `.ToLowerInvariant()` 链零改动）。消费方：`Publish.ps1`（模型/工具/
  锁指纹/SHA256SUMS 四处）、`scripts/verify.ps1`（[1.97/2] 设置指纹）、`scripts/pack.ps1`（包哈希）。
- **环境层**：`scripts/git-hooks/{pre-commit,pre-push}` 在回退 `powershell.exe` 时
  `env -u PSModulePath` 启动，5.1 重建自身默认模块路径，其余 cmdlet 一并脱险。
- 两层独立成立：钩子层防"钩子路径"，脚本层防"用户从任意控制台手动跑"。

## 3. 编码与产物一致性（同一轮顺手收口）

1. **BOM**：全仓 83 个含非 ASCII 的 .ps1 补 UTF-8 BOM（纯 ASCII 的 1 个不动）。
   本机 ACP=65001（开了系统级 UTF-8）掩盖了问题；标准中文机器 ACP=936 下，
   无 BOM 的 UTF-8 脚本在 5.1 里中文注释/字符串必乱码。BOM 对 7.x 无影响。
   全部文件过严格 UTF-8 解码校验后才加，非 UTF-8 文件跳过（实测 0 个）。
2. **build-info.json**：`Publish.ps1` 原用 `Set-Content -Encoding UTF8` —— 5.1 必带 BOM、
   7 不带 ⇒ 同一版本不同宿主产物漂移。改为 `.NET WriteAllText + UTF8Encoding($false)`
   （与下方 SHA256SUMS.txt 既有纪律同一条），双宿主产物逐字节一致。
3. **无 #Requires 语句**：刻意不加 —— 上限/下限约束对本仓无意义，加了反而让"哪边能跑"
   变成清单问题而不是实测问题。

## 4. 已知边界（诚实记录）

1. `tests/manual/**` 的 20 余个一次性 cell 仍用 `Get-FileHash` —— 它们是开发者手动跑的
   旁路探针，在干净 5.1 或 pwsh 下正常；被污染的 5.1 控制台里会误红（不产生假绿）。
   门禁链路已全部免疫，旁路探针留待下次触碰时顺手换。
2. `pack.ps1:199` 给用户展示的"自行校验"提示文本仍写 `Get-FileHash` —— 那是对用户的
   操作指引（7.x / 干净 5.1 都可用），不是脚本自身的依赖。
3. 7.1 已于 2022-09 EOL：本仓对 7.x 的承诺按"最新稳定版（7.6.x）实测"兑现，7.1 的
   语法面是 7.x 的子集，兼容结论向上向下成立，但 7.1 本身不受上游安全支持。

## 5. 修复推进的两层剥笋（实测时序）

1. **哈希层（5.1 独有）**：修复前 5.1 全量 = "4/8 场景不符"（B/D/E 死在"依赖校验失败"，
   即 Get-FileHash 不存在）。换 `Get-Sha256Hex` 后 → 2/8（B/D 仍红但换了原因）。
2. **套件漂移（与宿主无关）**：剩下的 B/D 红是 `Publish.ps1 $gateSuites` 漏 `--qr-tests`
   —— 断言下限检查 1914 < 1961。pwsh 此刻跑同样红（与 5.1 无关；当天早上绿是因为
   `-WriteGateBaseline` 尚未把 QR 套件写进基线，老基线 1914 = 桩的 7 套件合计）。
   补齐 `$gateSuites` + 桩手抄值 + `ExpectDotnetRuns 7→8` 后 → **两宿主 8/8**。

教训：`verify.ps1` 与 `Publish.ps1` 各自持有一份套件清单，加套件时只改一处就是漂移 ——
已在 `$gateSuites` 条目上写明"必须与 verify.ps1 的 `$suites` 同集合"及漏改的实测后果。

## 6. 复现

```sh
# 根因复现（在被 pwsh 污染的会话里）:
powershell.exe -NoProfile -Command "Get-FileHash LICENSE"          # → 命令不存在
env -u PSModulePath powershell.exe -NoProfile -Command "Get-FileHash LICENSE"  # → 正常

# 修复验证:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1   # 5.1 全量 → 8/8 场景
pwsh -NoProfile -File scripts/verify.ps1                                     # 7.x 全量 → 8 套件全绿
```

## 7. 宿主策略定版（2026-10-26 第二轮）：默认 7.x，5.1 基线回退

维护者拍板，把口径从"5.1 保留为主、7.x 跟进"翻转为：**默认宿主 = PowerShell 7.x
（本机 7.6.8 实测基线），Windows PowerShell 5.1 降为基线回退**。钩子与 csproj 在
上一轮已是 pwsh 优先，本轮清掉剩余的写死点，让该口径在所有入口一致：

| 位置 | 原状 | 处置 |
|---|---|---|
| `verify.ps1` [1.96/2] 日志行格式对撞 | 写死 `& powershell`：pwsh 会话里也偷偷换 5.1；pwsh-only 机器（Win11 可卸载 5.1）上这条阻塞步恒红；与 [1.6/2] "不写死宿主"的注释自相矛盾 | 改 `(Get-Process -Id $PID).Path` 跟随调用者（与 [1.6/2] 同一纪律） |
| `tests/manual/safety/` 的 gate-runner / freshness-two-leg / two-leg2 / no-incremental + `publish-probes/run-color-decision-check.ps1` | 五个手动探针全部写死 `powershell`（§4.1 只豁免了 Get-FileHash，宿主写死不在豁免内） | 同上，跟随调用者；默认跑出来就是 7.x，要验 5.1 就从 5.1 会话再跑一轮 |
| `ps-compat.ps1` | 只有哈希助手 | 增 `Get-TtcPsHostLabel`（**纯展示，不参与判定**）：verify.ps1 头部打印宿主标签，门禁日志从此自证"这轮是谁跑的" |
| `verify-package-content.ps1:6`、`gui-locale-clipboard-probe.ps1:2` | 用法注释教 `powershell -File` | 改 pwsh 优先，注明 5.1 亦可 |
| `ci.yml:117` 在 `shell: pwsh` 步骤里再显式写 `pwsh ./scripts/verify.ps1` | 看似冗余 | **保留，有意为之**：.ps1 的 `exit N` 不写 `$LASTEXITCODE`（它只归原生 exe），退出码靠外层 pwsh 进程传递；改成直调 .ps1 会让 `$LASTEXITCODE` 停在脚本内最后一个原生命令上 ⇒ 假绿 |

顺带修正昨日 QR 提交残留的过时套件计数（"7 个"→"8 个"）：verify.ps1 头注两处、
README `[2/2]` 行与 `-IncludeDesktop` 说明、pre-commit/pre-push 注释各一处。

**验收（本轮实测）**：

```text
pwsh 7.6.8                    全量 verify.ps1 → EXIT=0（8 套件 1961 断言，横幅"宿主: pwsh 7.6.8"）
Windows PowerShell 5.1.26100.9711（env -u PSModulePath）→ EXIT=0（同 1961）
日志行探针 pwsh 直跑 → RC=0，8/8 臂（[1.96/2] 换宿主后的直接证据）
钩子 dry-run → 两钩子均选 pwsh.exe（默认路径）
```

两轮全量里的 [1.6/2] 发布门禁回归（8/8 场景）与 [1.9/2] 宿主新鲜度均随宿主通过。
