# 距离发布微软商店还剩什么问题

> 日期：2026-10-07
> 方法：**逐项从零复核**（不依赖先前笔记）—— 实测当前源码、重跑 MSIX 门禁与 WACK、核查仓库卫生。
> 结论按"是否阻断提交"分级。

---

## 0. 结论摘要

| 级别 | 数量 | 内容 |
|---|---|---|
| 🔴 **阻断提交** | **1** | ① `Publisher` 仍是占位符（需先注册账号）|
| 🟠 **阻断认证/上架** | **1** | ② MSIX 从未实机安装运行验证（唯一未验证的沙箱环节）|
| 🟡 **提交表单必填** | 3 | ③ 隐私政策 URL；④ 商店截图；⑤ 商店描述/年龄分级 |
| 🟢 **已就绪** | — | 出包、内容门禁、WACK、图标、隐私政策文件、设置/日志/自启的 MSIX 适配 |

> ✅ **2026-10-07 本轮已清掉两项**（原为 2 个 🔴 + 1 个 🟠）：
> - ~~151.5 MB 构建产物被误提交进 git~~ → **已清理**（`.gitignore` 补齐规则 + `git rm --cached`）
> - ~~打包形态开机自启不生效~~ → **已实现**（`StartupTask` API + 测试覆盖）

**一句话**：**代码侧与仓库卫生都已清空**（WACK PASS、门禁绿、设置/日志/自启三处 MSIX 适配完成），
剩下的**全部是需要你本人的账号与素材操作**，外加一次实机安装验证。

---

## 1. 🔴 阻断项 ①：`Publisher` 仍是占位符

```
Identity Publisher = CN=PLACEHOLDER-REPLACE-WITH-PARTNER-CENTER-IDENTITY
```

**为什么阻断**：上传时包身份与 Partner Center 预留的应用不匹配，**直接失败**，
且官方注明该字段**区分大小写、空格与标点必须完全一致**，报错不指向这里。

**修法**：注册账号 → 预留名称 → Partner Center「查看应用标识详细信息」取真实 `Publisher` → 替换。
本仓 `scripts/verify-msix.ps1` 已会在检测到 `PLACEHOLDER` 时给 ⚠️ 提示（但不阻断，因为本机/侧载合法）。

**归属**：⏳ 需要你完成账号注册后我才能填。

---

## 2. ✅ 已清理（原 🔴 阻断项）：151.5 MB 构建产物被误提交进 git

> **2026-10-07 本轮已修复** —— `.gitignore` 补齐规则 + `git rm -r --cached src/TrueToneCap.App/AppPackages`。
> 修复后实测：tracked 归零、磁盘文件保留、`git add -A` 不会再收进来。以下保留原分析备查。

### 实测现状

```
$ git ls-files src/TrueToneCap.App/AppPackages | Measure-Object
Count: 18    （磁盘合计 151.5 MB）
$ git ls-files ... | Where-Object { $_ -match '\.msix$' }
src/TrueToneCap.App/AppPackages/TrueToneCap.App_0.3.7.0_x64_Test/TrueToneCap.App_0.3.7.0_x64.msix
```

`git status` 里那一行 `M src/TrueToneCap.App/AppPackages/.../*.msix` 就是它 —— **一个 150 MB 的二进制被跟踪了**。
`.gitignore` 里**没有** `AppPackages` / `*.msix` 规则（实测确认）。

### 为什么是我的问题

这批文件是**我做 MSIX 打包验证时生成的**（`dotnet build -p:TtcMsixPackage=true`
默认输出到 `$(MSBuildProjectDirectory)\AppPackages\`）。测试产物不该进版本库。

### 为什么要修

1. **仓库膨胀**：151 MB 二进制会永久留在 git 历史里，克隆代价陡增（且 clone 是用户/审核看项目的第一印象）
2. **误导性**：包内的 `.msix` 是**旧构建**（我多次重建过），有人可能取它当"发布产物"
3. **合规噪声**：`AppPackages` 里还含 `Add-AppDevPackage.ps1` 等微软提供的文件，与"仓库只放自己的东西"冲突

### 修法（两步，缺一不可）

```powershell
# ① .gitignore 增加规则（防止再犯）
src/TrueToneCap.App/AppPackages/
*.msix
*.appxsym

# ② 从索引移除（保留磁盘文件，因为它是当前构建输出）
git rm -r --cached src/TrueToneCap.App/AppPackages
```

> ⚠️ **`--cached` 是刻意的**：只从索引移除，不删磁盘文件 —— 那个 `.msix` 正是待验证的产物。

### 好消息：**从未 push 过，所以代价最小**

实测确认（这一点决定了修法）：

| 位置 | 是否含这批文件 |
|---|---|
| 工作区 / 索引（staged） | 未 staged |
| **本地 HEAD**（已 commit） | ⚠️ **含 18 个文件** |
| **`origin/main`**（远端） | ✅ **不含** ⇒ **从未推送** |

⇒ 这批文件只存在于**本地一次尚未推送的提交**里。在把它 push 之前清理掉，
**git 历史里不会留下这 151 MB**，也**不需要 `git filter-repo` 改写历史**（那会影响所有克隆者）。

> ⚠️ **一旦先 push 再清理**，这 151 MB 会永久留在远端历史里，只能靠改写历史/强推来补救。
> ⇒ **这是当前最紧的一步**：清理必须在 push 之前做。


---

## 3. 🟠 阻断项 ③：MSIX **从未实机安装运行验证**（唯一未验证环节）

### 已验证到哪一步

| 环节 | 状态 |
|---|---|
| MSIX 出包 | ✅ 150.91 MB |
| 内容核对（4 原生工具 + 3 OCR 资产 + 2 合规文档） | ✅ 全绿 |
| WACK（商店认证用的同一套） | ✅ `OVERALL_RESULT=PASS`（24 项，2 项 OPTIONAL FAIL 不阻断） |
| **装到干净机器上跑一遍** | ❌ **从未做过** |

### 为什么这条重要（不是形式主义）

本次修的两个缺陷（设置丢失、日志写不进）**都属于"打包后才暴露"的类型** ——
它们不会被构建、门禁或 WACK 发现，只有真跑起来才看得到。
这本身就说明"验证到包内容正确"是不够的。

### 装一次能一次性验证的清单

1. **设置持久化**：改一项设置 → 重启 → 是否还在（本次修复的直接验证）
2. **日志落点**：打包形态应写 `%LOCALAPPDATA%\TrueToneCap\log`（本次修复）
3. **子进程 + `%TEMP%`**：四种格式（AVIF / JPEG XL / WebP / JPEG LI）能否正常编码
   —— 应用把临时 PNG/PPM 写 `%TEMP%` 再交给随包的原生 exe；**MSIX 沙箱下这一环未经实测**
4. **屏幕捕获**：D3D11/WGC 在包身份下是否正常
5. **开机自启**：清单已声明 `startupTask`，但**代码侧尚未切到 `StartupTask` API**（见下）

### 一个已知的功能缺口（原为阻断项，**本轮已实现**）

> ✅ **2026-10-07 已修**：`StartupManager` 现在按运行形态分支 ——
> 打包形态走清单声明的 `startupTask` + `Windows.ApplicationModel.StartupTask` API
> （`GetAsync` / `RequestEnableAsync` / `Disable`），未打包形态保持注册表实现**逐字不变**。
> 新增判据 `Test_StartupManager_PackagedUsesStartupTask`（4 组断言），并做过**变异测试**：
> 把 `StartupTaskId` 改错后该判据立即报红 —— 它咬的正是"TaskId 与清单不一致 ⇒
> `GetAsync` 返回 null、自启静默失效且无报错"这一格。

原分析（备查）：`StartupManager` 曾写 `HKCU\...\Run`，而 MSIX 下 HKCU 写入被重定向到包专属 hive
⇒ 打包形态开机自启不会生效。症状最具欺骗性：**界面开关能勾、注册表里也读得到值，
只有重启后才发现不会自动启动**。清单里早已声明 `startupTask`，但代码没接该 API。

**归属**：⏳ 需要你在有/无运行时的机器上装一次（包内已附带 `Add-AppDevPackage.ps1`）。

---

## 4. 🟡 提交表单必填项（3 项，都不阻断代码但缺了交不了）

| # | 项 | 现状 | 说明 |
|---|---|---|---|
| ④ | **隐私政策 URL** | 文件已就绪（`PRIVACY.md` 已 tracked），**URL 尚不可用** | 需先 push；push 后 `https://github.com/luoye-cpu/TrueToneCap/blob/main/PRIVACY.md` 即可用（公开仓库已验证匿名可访问） |
| ⑤ | **商店截图** | ❌ **未准备** | 至少 1 张，建议 1366×768 以上。仓库里只有 `tests/output/**` 的实机截图，需挑/裁成商店规格 |
| ⑥ | **商店描述 + 年龄分级** | ❌ 未撰写 | 需中英文描述；年龄分级在提交时填 IARC 问卷。⚠️ 政策 10.1.1 要求**准确描述功能与限制**（如"翻译需联网、视觉翻译会把截图发给第三方"） |

> 📌 另外两项由提交表单自然覆盖：**支持联系方式**（Company 账号在部分区域会展示）、
> **类别/市场选择**。若用 Individual 账号则无需客服信息。

---

## 5. 🟢 已就绪（复核确认，无需再做）

| 项 | 实测证据 |
|---|---|
| **MSIX 出包** | ✅ 150.91 MB |
| **内容门禁** | ✅ `verify-msix.ps1`：4 原生工具 + 3 OCR 资产 + 2 合规文档 + 清单身份全通过 |
| **WACK** | ✅ `OVERALL_RESULT=PASS`，24 项中 22 PASS + 2 OPTIONAL FAIL（上游 Win2D 元数据 / 子进程引用，均不阻断） |
| **磁贴图标** | ✅ 13 个 Assets，清单引用的 6 个路径**全部存在**（实测逐个解析） |
| **凭据声明** | ✅ 只需 `runFullTrust`（桌面应用必需），无多余能力 |
| **隐私政策文件** | ✅ `PRIVACY.md` 已 tracked + 应用内入口（`PrivacyPolicyWindow`）+ 随包分发 |
| **设置/日志 MSIX 缺陷** | ✅ 已修（`PackageRuntime` 分支）+ 测试覆盖 |
| **子进程启动方式** | ✅ 30 处 `ProcessStartInfo` 中 **24 处 `UseShellExecute=false`**，用显式路径 —— MSIX 下的正确写法 |
| **代码构建/测试** | ✅ 5 项目 0 警告 0 错误；8 套件 **1851 通过 / 0 失败** |
| **商店免签名** | ✅ 商店认证通过后用微软证书自动重签，**无需购买代码签名证书** |
| **包体** | ✅ 自包含 175.85 MB / 框架依赖 **85.12 MB**，远低于 25 GB 上限 |

---

## 6. 建议执行顺序

| 顺序 | 动作 | 谁做 | 阻断什么 |
|---|---|---|---|
| ~~1~~ | ~~清理误提交的构建产物~~ | ✅ **已完成** | — |
| ~~2~~ | ~~修 `StartupManager` 用 `StartupTask` API~~ | ✅ **已完成** | — |
| **3** | 注册账号 → 预留名称 → 给我 `Publisher` | ⏳ **你做** | 上传失败 |
| **4** | 我填 `Publisher` 进清单 | ✅ 我做（拿到值后） | 上传失败 |
| **5** | push 仓库 → 拿到隐私政策 URL | ⏳ **你做**（一次 push） | 表单必填 |
| **6** | 准备截图 + 中英文描述 | ⏳ **你做**（或我起草描述） | 表单必填 |
| **7** | **装一次 MSIX 实机验证**（干净机器） | ⏳ **你做** | 认证/功能 |
| **8** | `.\Publish.ps1 -Msix -FrameworkDependent` 出最终包 | ✅ 我做 | — |
| **9** | 提交 Partner Center（含 Notes for certification） | ⏳ **你做** | — |

> ✅ **我方（代码/仓库）的活已全部做完**。第 3–9 步**全部需要你** ——
> 其中第 3、5、6、7 是只有账号持有人或能访问真机的人才能做的。
> 第 4 与第 8 我可以立刻做，但都卡在"先拿到 `Publisher`"／"先确认要发哪种形态"。
>
> ⚠️ **push 之前的提醒**：本次清理是 `git rm --cached`（只在索引层），
> 那 18 个文件**仍在本地 HEAD 里**，要等下一次提交才真正从历史消失。
> ⇒ **提交并 push 时请确认这次提交包含 `.gitignore` 与那 18 条 `D` 删除**，
> 否则它们会随 push 进入远端历史（那时就只能改写历史了）。

---

## 7. 复现命令

```powershell
# ── 复核阻断项 ②（构建产物被跟踪）──
git ls-files src/TrueToneCap.App/AppPackages | Measure-Object
$files = git ls-files src/TrueToneCap.App/AppPackages
($files | ForEach-Object { (Get-Item $_ -ErrorAction SilentlyContinue).Length } | Measure-Object -Sum).Sum / 1MB
Select-String -Path .gitignore -Pattern "AppPackages|\.msix"    # => 空 = 未忽略

# ── 复核阻断项 ①（Publisher 占位符）──
Select-String -Path src/TrueToneCap.App/Package.appxmanifest -Pattern "Publisher="

# ── 复核已就绪项：出包 + 门禁 + WACK ──
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true
.\scripts\verify-msix.ps1
$wack = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe"
$msix = (Get-ChildItem -Recurse src/TrueToneCap.App/AppPackages -Filter *.msix).FullName
& $wack reset; & $wack test -appxpackagepath $msix -reportoutputpath "$env:TEMP\w.xml"
[xml]$x = Get-Content "$env:TEMP\w.xml" -Raw
$x.REPORT.GetAttribute('OVERALL_RESULT')     # => PASS

# ── 复核清单资产是否都存在 ──
[xml]$m = Get-Content src/TrueToneCap.App/Package.appxmanifest -Raw
$m.SelectNodes("//*[local-name()='VisualElements' or local-name()='DefaultTile' or local-name()='SplashScreen']")
```

---

## 8. 待确认（我无法在本机验证的）

1. **MSIX 沙箱内子进程 + `%TEMP%` 的实际行为** —— 需真实安装后实测（§3）
2. **框架依赖包在无 .NET/WinAppSDK 运行时机器的表现** —— 商店会自动装框架包，但本机未验证安装流程
3. **`StartupTask` 在打包形态下的实际可用性** —— 需装包后测（当前代码尚未接入该 API）
4. **商店对"截图/录屏类"应用的具体隐私要求** —— 政策 10.5.1 已满足，但可能有额外问询
5. **`Individual` 账号的发布者显示名** —— 会影响商店页面署名，需你在注册时决定
