# 真机验证报告：安装真实 MSIX 后的实测结果

> 日期：2026-10-07
> 方法：**创建自签名证书 → 构建 MSIX → 真机安装 → 以真实包身份运行 → 逐项验证**
> 结论：**原先 6 项"无法本机验证"的项目，5 项已实测通过，1 项确认不可在本机测。**

---

## 0. 突破点：把"装不了"变成"能装"

先前多处文档写"需真装 MSIX 才能验证"，原因是**包身份无法伪造**。
本次找到了完整路径，因此这批验证从"待做"变成"已做"：

| 步骤 | 命令/结果 |
|---|---|
| 1. 本机已是提权会话 | `IsInRole(Administrator) = True` |
| 2. 启用开发者模式（侧载前提） | `HKLM\...\AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1` |
| 3. 建自签名证书，**Subject 与清单 Publisher 逐字一致** | `CN=PLACEHOLDER-REPLACE-WITH-PARTNER-CENTER-IDENTITY` |
| 4. 装入信任存储 | `LocalMachine\TrustedPeople` + `LocalMachine\Root` |
| 5. 构建 MSIX 并签名 | `signtool sign /fd SHA256 /sha1 <thumb>` → 0 errors |
| 6. `Add-AppxPackage` | ✅ **安装成功** |
| 7. 确认包身份 | `TrueToneCap_0.3.7.0_x64__4vm2c64tkcgbr`，`Status=Ok` |

> 📌 **这条路径本身就有价值**：它证明"本仓产出的 MSIX 是**可安装**的" ——
> 不只是"能被 WACK 解析"，而是真的能装进 Windows 并跑起来。

---

## 1. ✅ 核心假设验证：安装目录确实**只读**

这一条是整个设置修复的**前提**。修补前我先验证前提是否成立：

```
InstallLocation: C:\Program Files\WindowsApps\TrueToneCap_0.3.7.0_x64__4vm2c64tkcgbr
写入尝试 => ✅ WRITE DENIED (UnauthorizedAccessException)
```

⇒ **官方文档"包目录只读"在本机得到实证**，因此"设置必须改写到别处"不是臆测。

---

## 2. ✅ 设置与日志：真的落到了新位置

以**真实包身份**运行应用后，读它**自己写的日志**（这是沙箱内部的 ground truth）：

```
[Info] [系统/LogService]     日志系统初始化，目录: C:\Users\20210\AppData\Local\TrueToneCap\log
[Info] [系统/SettingsService] 设置已保存到 C:\Users\20210\AppData\Local\TrueToneCap\TrueToneCap.settings.json
[Info] [系统/SettingsService] 设置已保存到 C:\Users\20210\AppData\Local\TrueToneCap\TrueToneCap.settings.json  (多次)
```

| 检查 | 结果 |
|---|---|
| 设置写入 `%LOCALAPPDATA%\TrueToneCap\` | ✅ |
| 日志写入 `%LOCALAPPDATA%\TrueToneCap\log` | ✅ |
| 设置文件**出现在只读安装目录里**（应为 False） | ✅ **False** |
| `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json` 实际存在 | ✅ |

⇒ **修复前的行为会是**：`CanPersist=false` ⇒ 全程默认值 ⇒ 用户改的设置重启即丢。
**现在真的存下来了。**

---

## 3. ✅ 沙箱内子进程 + `%TEMP%`：四种原生编码器全部工作

这是我最担心的一项（4 种格式依赖把临时文件交给随包 exe 处理）。
用 `IApplicationActivationManager` COM 以**包身份**带参数启动应用的 `--selftest`：

```
hr=0x00000000 pid=19136
```

**产出文件（全部由沙箱内进程生成）：**

| 文件 | 大小 | 依赖 |
|---|---|---|
| `selftest_JPEG_XL.jxl` | 457 B | **`cjxl.exe` 子进程** |
| `selftest_WebP.webp` | 446 B | **`cwebp.exe` 子进程** |
| `selftest_AVIF.avif` | 536 B | **`avifenc.exe` 子进程** |
| `selftest_JPEG_LI.jpg` | 1,233 B | **`cjpegli.exe` 子进程** |
| `selftest_PNG.png` / `.tiff` | 483 / 30,223 B | 托管编码器 |

**selftest 自评：`ok=10  skip=1  fail=0`**

⇒ 结论：
- ✅ **子进程能在 MSIX 沙箱内正常创建**
- ✅ **`%TEMP%` 在沙箱内可写**（中间 PPM/PNG 正常落地）
- ✅ **只读安装目录不影响临时文件使用**
- ✅ 4 种原生编码格式**全部成功**，无降级

> 日志同时显示四枚工具都从包内被正确解析：
> `native\avifenc.exe` / `cwebp.exe` / `cjpegli.exe` / `cjxl.exe` 均已就绪。

---

## 4. ✅ `StartupTask`：读、写、回读**全部实测通过**

这是本会话最严重的缺陷（反射版恒失败且静默）所在处，因此验证得最彻底。

### 4.1 用包身份运行探针

用 `Invoke-CommandInDesktopPackage`（以指定包的**身份**运行任意程序）：

**READ（读状态）**
```
BEFORE: State=Disabled (0)
AFTER:  State=Disabled (0)
```

**ENABLE（启用 —— 反射版会在这里静默失败）**
```
BEFORE: State=Disabled (0)
REQUEST-ENABLE-RESULT: Enabled (2)      <-- 成功翻转
AFTER:  State=Enabled (2)
```

**独立佐证**（Windows 自己的注册表，非本程序自述）：
```
HKCU\...\SystemAppData\TrueToneCap_4vm2c64tkcgbr\TrueToneCapStartup
  State = 2              (2 = Enabled)
  UserEnabledStartupOnce = 1
```

**DISABLE（禁用）**
```
State after disable = 0
```

### 4.2 应用自身的端到端回读

重启打包应用后，读它自己的日志：

```
22:05:49  [系统/StartupManager] StartupTask 状态=0（0=Disabled 1=DisabledByUser 2=Enabled 3=DisabledByPolicy）
22:17:49  [系统/StartupManager] StartupTask 状态=2（0=Disabled 1=DisabledByUser 2=Enabled 3=DisabledByPolicy）
```

⇒ **应用能正确读出状态从 Disabled 变为 Enabled** —— 读写两条路径都通。

### 4.3 这直接证伪了反射版

反射版会在 `AsTask` 处返回 `null`，只留下一行 `Warn`，**永远拿不到 `State`**。
现在实测拿到 `State=0`、成功 `RequestEnableAsync` → `Enabled(2)`、再读出 `2`。
**强类型改法是对的。**

### 4.4 一个附带确认

`HKCU\...\SystemAppData\<PFN>\TrueToneCapStartup` 这个键**存在本身**就说明：
**清单里的 `TaskId` 与代码请求的 ID 一致** —— 若不一致，`GetAsync` 会返回 `null`，
该键也不会被 Windows 建立。

---

## 5. ✅ 取消：**实测立即中止**（带时延测量）

不再依赖"文本断言说接线存在"，而是真的发起请求再取消并计时：

| 场景 | 结果 |
|---|---|
| 发起后 **300 ms** 取消 | `OperationCanceledException`，**330 ms** 返回（时延 30 ms） |
| 发起后 **800 ms** 取消 | `OperationCanceledException`，**816 ms** 返回（时延 16 ms） |
| **预先取消**的 token | `OperationCanceledException`，**0 ms** |
| 基线（不取消） | **成功**，1289 ms → 译文 `你好，世界。` |

⇒ 取消**立即生效**（时延 16–30 ms）。修复前，关窗后请求会跑满各自超时（实测基线 ~1.3 s，
Google 单端点超时 5 s）—— **这就是修复实际消除的浪费**。

> 该测试同时验证了翻译链路本身可正常工作（基线返回了真实译文）。

---

## 6. ✅ 框架依赖包：**能装、能跑**

我特意装的是 **框架依赖版（85.12 MB）**，因为它把 .NET 与 WinAppSDK 交给系统解析 ——
这正是先前"未验证"的安装流程：

- ✅ `Add-AppxPackage` 成功
- ✅ 应用正常启动并完成完整 selftest
- ✅ 本机存在 22 个 `Microsoft.WindowsAppRuntime.*` 框架包（1.4 / 1.5 等）

⇒ **框架依赖形态在真实安装下可用**，且体积只有自包含版的一半。

---

## 7. ⚠️ 未能验证的一项（诚实说明）

| 项 | 状态 | 原因 |
|---|---|---|
| `RequestEnableAsync` 被 **`DisabledByUser`** 拒绝的实际路径 | ❌ **未验证** | 需要：① 用户到「设置 → 应用 → 启动」手动关掉，② 再调 API 观察是否返回 `1`。**这需要 GUI 交互，且要故意制造拒绝态**。代码已把该状态如实记日志（`StartupTaskState=1` 分支），但**该分支未在真机触发过**。 |

> 我不会为了测这一格而往**出货源码**里加诊断代码。
> 该分支的代码路径已由单元测试钉住（如实上报、不谎报成功），属于"逻辑正确但未实机触发"。

---

## 8. 清理（把机器还原）

| 动作 | 结果 |
|---|---|
| 禁用 StartupTask | ✅ `State=0` |
| 卸载包 | ✅ 已安装包数 = 0，`WindowsApps` 无残留目录 |
| 移除测试证书（3 个存储） | ✅ 全部移除 |
| 应用进程 | ✅ 无残留 |
| **开发者模式** | ✅ **已还原为原状**（原先即"未设置"，故**删除**该两个值而非置 0） |

```powershell
# 还原开发者模式（还原到"未设置"，而不是置 0 —— 与发现时的状态一致）
$k = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
Remove-ItemProperty $k AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
Remove-ItemProperty $k AllowAllTrustedApps -ErrorAction SilentlyContinue
```

> **唯一保留**：`%LOCALAPPDATA%\TrueToneCap\` —— 应用在真机运行时正常写下的用户数据
> （settings.json 与日志）。这是**应用数据而非测试残留**，删除与否由你决定。
>
> ⚠️ 开发者模式关闭后**无法再侧载未签名 MSIX**。日后若还要本机验证打包形态，
> 需重新执行 §10 第 0 步。

---

## 9. 结论：原先的"验证盲区"已基本关闭

| 先前状态 | 现在 |
|---|---|
| ❌ 打包形态 `StartupTask` 真实行为 | ✅ **读/写/回读全通过** |
| ❌ `DisabledByUser` 拒绝路径 | ⚠️ 仍未验证（需 GUI 制造拒绝态） |
| ❌ MSIX 沙箱内子进程 + `%TEMP%` | ✅ **四种原生编码器全部成功，fail=0** |
| ❌ 取消是否真的立即中止 | ✅ **实测 16–30 ms 内中止** |
| ❌ 框架依赖包安装流程 | ✅ **能装、能跑、完成完整 selftest** |
| ❌ 打包形态设置持久化 | ✅ **确实写入 %LOCALAPPDATA% 并读回** |

**6 项中 5 项实测通过，1 项明确未验证且说明了原因。**

> 📌 这也回答了一个更根本的问题：**本仓产出的 MSIX 是真能用的** ——
> 不只是"能过 WACK"，而是能装进 Windows、以包身份运行、完成全部编码与识别能力。

---

## 10. 复现步骤

```powershell
# ── 前提：提权会话 ──
$k = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
New-Item -Path $k -Force | Out-Null
Set-ItemProperty $k AllowDevelopmentWithoutDevLicense 1 -Type DWord
Set-ItemProperty $k AllowAllTrustedApps 1 -Type DWord

# ── 1. 证书 Subject 必须与清单 Publisher 逐字一致 ──
$pub = (Select-String src/TrueToneCap.App/Package.appxmanifest -Pattern 'Publisher="([^"]+)"').Matches.Groups[1].Value
$cert = New-SelfSignedCertificate -Type Custom -Subject $pub -KeyUsage DigitalSignature `
  -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
Export-Certificate -Cert $cert -FilePath "$env:TEMP/ttc.cer" -Force
Import-Certificate -FilePath "$env:TEMP/ttc.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Import-Certificate -FilePath "$env:TEMP/ttc.cer" -CertStoreLocation Cert:\LocalMachine\Root

# ── 2. 构建 + 签名 + 安装 ──
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true `
  -p:TtcFrameworkDependent=true -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishReadyToRun=false
$msix = (Get-ChildItem -Recurse src/TrueToneCap.App/AppPackages -Filter *.msix).FullName
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint $msix
Add-AppxPackage -Path $msix

# ── 3. 以包身份运行 selftest（验证沙箱内子进程）──
#     IApplicationActivationManager.ActivateApplication(AUMID, "--selftest --out <dir>", 0, out pid)
$pfn = (Get-AppxPackage -Name TrueToneCap).PackageFamilyName
#     或用 Invoke-CommandInDesktopPackage 以包身份运行任意探针

# ── 4. 读应用自己的日志（沙箱内 ground truth）──
Get-Content "$env:LOCALAPPDATA/TrueToneCap/log/app_*.log" | Select-String "StartupTask|日志系统初始化|设置已保存"

# ── 5. 清理 ──
Remove-AppxPackage (Get-AppxPackage -Name TrueToneCap).PackageFullName
foreach ($s in "Cert:\LocalMachine\TrustedPeople","Cert:\LocalMachine\Root","Cert:\CurrentUser\My") {
  Get-ChildItem $s | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item -Force
}
```
