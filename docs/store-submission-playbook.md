# 微软商店提交标准流程（本机 CDP 复用登录态）

> 状态：活文档，2026-11-17 首次验证。
> 适用对象：TrueToneCap 已上架后的 **MSIX 更新提交**（不是首次注册开发者账号，也不是首次预留应用名）。
> 首次账号/隐私政策/上架准备见 `docs/reviews/msix-store/store-submission-guide-2026-10-07.md`；本文件只记录“怎么把新包安全地送进认证”。
>
> **已验证案例**：TrueToneCap 0.3.18，产品 `luoye-cpu.TrueToneCap` /「真色截图」，Product ID `9N42LP5NVFWR`，`Submission 5` ID `1152921505702422497`，2026-11-17 提交后状态为 **“正在认证更新”**。

---

## 0. 一句话结论

当运行时禁止桌面控制、内置 Browser Use 又没有本机登录态时，**不要继续在真实 GUI 上循环，也不要用内置浏览器冒充登录**。正确路径是：

1. 用**本机已登录的 360 浏览器**，复制一份最小登录态到独立配置目录；
2. 以 `--remote-debugging-port` 启动**独立 360 调试实例**；
3. 用 Playwright `connect_over_cdp` 接入这个实例；
4. 在 Partner Center 完成包上传、listing 保存、提交选项保存；
5. 等 `提交进行认证` 解除 disabled 后提交，核验 `Submission` 状态与提交 ID。

核心原则：**复用本机会话，但不读取、不输出、不提交任何 Cookie 内容；只复制登录态文件，主 360 不关闭、不修改。**

---

## 1. 前置条件（缺一不可）

- [ ] 本机 360 已登录 Partner Center，且该账号对目标产品有提交权限。
- [ ] 产品已在 Partner Center 存在，已知 Product ID（TrueToneCap 为 `9N42LP5NVFWR`）。
- [ ] MSIX 已构建完成，版本严格高于商店线上版本。
- [ ] WACK 已跑过；`OVERALL_RESULT=PASS`；可选失败项已记录且不阻断。
- [ ] 包身份、哈希、大小已固定；后续不再改包，否则哈希失效。
- [ ] 用户已明确要求“提交/发布”；`提交进行认证` 是真实外部写操作，不能把“测试一下”当成发布授权。
- [ ] 不碰主 360：调试实例必须使用独立 `--user-data-dir`。

---

## 2. 总流程

```text
构建与包校验
   ↓
复制 360 最小登录态（卷影复制 Cookies）
   ↓
启动独立 360 CDP 实例（127.0.0.1:9222）
   ↓
Playwright connect_over_cdp → 验证 Partner Center 已登录
   ↓
产品概览 → 开始更新 → Submission 草稿
   ↓
上传 MSIX（>50MB 用 CDP DOM.setFileInputFiles）→ Validated
   ↓
保存包 → 更新 Store 一览（release notes）→ 保存提交选项（runFullTrust 说明）
   ↓
等待“提交进行认证”解除 disabled → 提交
   ↓
核验“正在认证更新” + Submission ID → 记录
   ↓
认证通过后核验商店版本；失败则按反馈修复重提
```

---

## 3. 构建与包校验（提交前）

### 3.1 版本坐标

至少核对以下位置一致：

- `Directory.Build.props`：`Version` / `VersionPrefix` / `VersionSuffix` 等实际版本字段；
- `Package.appxmanifest`：`Identity/@Version`（商店包必须是四段版本，第四段通常为 `0`）；
- `README.md` 版本徽章；
- `CHANGELOG.md` 版本段；
- 商店线上版本：新包必须严格大于线上包版本。

TrueToneCap 0.3.18 的实际坐标：`Directory.Build.props` 为 `0.3.18 / 0.3.18.0 / 0.3.18.0`，`Package.appxmanifest` 为 `0.3.18.0`。

### 3.2 构建与测试

- Release 构建：**0 警告 / 0 错误**。
- 全量测试：套件数与断言数记录；0.3.18 为 8 套件 / 2905 断言通过。
- 不要用 `--no-verify` 跳过钩子；不要为了过钩子改包内容。
- 绿色包与 MSIX 的 JIT/裁剪口径与上一版保持一致；0.3.18 为自包含、未裁剪、纯 JIT。

### 3.3 WACK 与包内核对

- 用 Windows App Certification Kit 对 **最终 MSIX** 跑 WACK，记录 `OVERALL_RESULT`。
- 0.3.18 实测 `PASS`，24 项全部运行；3 个 FAIL 均为 `OPTIONAL=TRUE`，不阻断：
  - 两张 `scale-400` logo 超过 200KB；
  - WinUI 上游 winmd GUID 重复；
  - 子进程编码器引用进程启动 API。
- 用 `verify-msix.ps1` 或等价脚本核对包内原生编码器、OCR 模型、清单身份。
- 记录 MSIX 的字节数与 SHA256；上传后 Partner Center 显示的体积可能按 MB 四舍五入。

---

## 4. 准备独立 360 登录态（关键步骤）

### 4.1 为什么不能直接复制

主 360 正在运行时，`Default\Network\Cookies` 被独占锁定：

- `Copy-Item` 会被文件锁拦截；
- `esentutl /y` 可能返回 `JET_errFileAccessDenied`；
- SQLite `backup()` 可能返回 `unable to open database file`；
- `vssadmin create shadow` 在当前系统版本可能报 `Invalid command`。

**有效替代**：管理员权限下用 WMI `Win32_ShadowCopy.Create` 创建卷影快照，从快照只读复制 Cookies 与最小登录态文件。

### 4.2 路径变量

```powershell
$srcProfile = Join-Path $env:APPDATA 'secoresdk\360se6\User Data'
$dstProfile = Join-Path $env:LOCALAPPDATA 'Temp\ttc-360-profile'
$browserExe = Join-Path $env:APPDATA 'secoresdk\360se6\Application\360se.exe'
$cdpUrl     = 'http://127.0.0.1:9222'
```

> 360 版本与路径会变；先确认 `$browserExe` 存在。TrueToneCap 本次实测为 360 `16.3.1131.64`。

### 4.3 创建卷影并复制最小登录态

在**管理员 PowerShell**中执行：

```powershell
# 1) 创建客户端可访问的卷影
$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create `
  -Arguments @{ Volume = 'C:\'; Context = 'ClientAccessible' }
$shadow = Get-CimInstance Win32_ShadowCopy |
  Where-Object { $_.ShadowID -eq $r.ShadowID }
$dev = $shadow.DeviceObject
if (-not $dev) { throw 'ShadowCopy 创建失败' }

# 2) 独立配置目录
New-Item -ItemType Directory -Force -Path $dstProfile | Out-Null

# 3) 从快照复制最小登录态；不复制 Cache / GPUCache / Code Cache
$shadowProfile = Join-Path $dev "Users\$env:USERNAME\AppData\Roaming\secoresdk\360se6\User Data"
Copy-Item -LiteralPath (Join-Path $shadowProfile 'Local State') -Destination $dstProfile -Force
Copy-Item -LiteralPath (Join-Path $shadowProfile 'Preferences') -Destination $dstProfile -Force
robocopy (Join-Path $shadowProfile 'Default\Network') (Join-Path $dstProfile 'Default\Network') /E /COPY:DAT /R:0 /W:0
robocopy (Join-Path $shadowProfile 'Default\Local Storage') (Join-Path $dstProfile 'Default\Local Storage') /E /COPY:DAT /R:0 /W:0
robocopy (Join-Path $shadowProfile 'Default\Session Storage') (Join-Path $dstProfile 'Default\Session Storage') /E /COPY:DAT /R:0 /W:0
robocopy (Join-Path $shadowProfile 'Default\IndexedDB') (Join-Path $dstProfile 'Default\IndexedDB') /E /COPY:DAT /R:0 /W:0
robocopy (Join-Path $shadowProfile 'Default\Web Applications') (Join-Path $dstProfile 'Default\Web Applications') /E /COPY:DAT /R:0 /W:0
```

要点：

- `Local State` 必须复制，否则 Cookies 的解密上下文可能不完整。
- `Default\Network` 里包含 `Cookies`；只复制文件，**不要读取内容，不要打印，不要提交到任何仓库**。
- `Local Storage` / `Session Storage` / `IndexedDB` / `Web Applications` 保存登录相关状态；缓存不必复制。
- 配置目录放在 `%LOCALAPPDATA%\Temp`，属于临时材料；不要放到仓库或用户文档目录。
- 不要复制 `Login Data` 等密码库；本流程不需要，也不应接触。

### 4.4 启动独立调试实例

```powershell
Start-Process -FilePath $browserExe -ArgumentList @(
  '--remote-debugging-port=9222',
  "--user-data-dir=$dstProfile",
  '--no-first-run',
  '--no-default-browser-check'
)
Start-Sleep -Seconds 5
Invoke-RestMethod "$cdpUrl/json/version"
```

验证：

- `json/version` 应返回 `Browser` 与 `webSocketDebuggerUrl`；
- 出现登录页并不等于失败；登录态复制不全时，**只在这个独立实例里让用户手动登录**，不要改用内置 Browser Use；
- 如果 9222 被占用，改用 9223 并同步修改 `$cdpUrl`；
- 调试实例的窗口是独立 360 实例，**不要关闭主 360**。

### 4.5 Playwright 环境

```powershell
python -m pip install --target "$env:TEMP\ttc-cdp-libs" playwright==1.65.2 greenlet==3.5.6
```

接入：

```python
import sys
sys.path.insert(0, r"<临时目录>\ttc-cdp-libs")
from playwright.sync_api import sync_playwright

with sync_playwright() as p:
    browser = p.chromium.connect_over_cdp("http://127.0.0.1:9222")
    ctx = browser.contexts[0]
    page = ctx.pages[0]
```

验证页面：

```python
page.goto(
    "https://partner.microsoft.com/zh-cn/dashboard/products/9N42LP5NVFWR/overview",
    wait_until="domcontentloaded", timeout=60000
)
page.wait_for_timeout(10000)
print(page.title())
print(page.locator("body").inner_text()[:500])
```

如果落在 `login.microsoftonline.com`，说明没有复用登录态。**停止自动化**，让用户在这个独立 360 窗口手动登录；不要让代理输入密码或验证码。

---

## 5. Partner Center 提交流程

以下以“更新已有产品”为例；产品 ID / 提交 ID 从页面实际读取，不要套用旧值。

### 5.1 进入产品概览并确认线上版本

```text
https://partner.microsoft.com/zh-cn/dashboard/products/<ProductId>/overview
```

确认：

- 线上提交（如 `Submission 4`）的包版本；
- 当前是否有进行中的草稿；
- 新版本号严格大于线上版本。

TrueToneCap 0.3.18 的基线：

- 线上 `Submission 4`，ID `1152921505702407086`，包 `TrueToneCap.App_0.3.16.0_x64.msix`；
- 从概览点“开始更新”创建 `Submission 5` 草稿，ID `1152921505702422497`。

### 5.2 上传 MSIX

包页：

```text
https://partner.microsoft.com/zh-cn/dashboard/products/<ProductId>/submissions/<SubmissionId>/packages
```

**不要用 Playwright `set_input_files` 传大包**：对超过 50MB 的文件会报：

```text
Cannot transfer files larger than 50Mb...
```

改用 CDP 让本机 360 自己读取文件：

```python
msix = r"C:\PLAN\TrueToneCap\publish\store-0.3.18\TrueToneCap.App_0.3.18.0_x64.msix"

client = page.context.new_cdp_session(page)
root = client.send("DOM.getDocument")["root"]["nodeId"]
node = client.send("DOM.querySelector", {
    "nodeId": root,
    "selector": 'input[type="file"]'
})["nodeId"]
if not node:
    raise RuntimeError("未找到文件输入")
client.send("DOM.setFileInputFiles", {
    "files": [msix],
    "nodeId": node
})
```

要点：

- 上传期间**不要**调用 `browser.close()`，否则 CDP 断开，页面会变成 `null Bytes` / `已暂停`；
- 如果出现 `null Bytes` / `已暂停`：移除失败包，重新上传，不要点 `Retry` 硬顶；
- 上传进度到 99% / `Analyzing package` 后可能短暂出现 `This package didn't finish processing`；重新读取后常见状态会变为 `Validated`；
- 旧版本包会被高版本包自动替换；0.3.18 上传后 0.3.16 从草稿包列表中消失，这是正常行为；
- 包页 `Save` 是 `input[type=button][value="Save"]`；保存后回概览，包应显示 `Validated` / `已更新`。

### 5.3 更新 Store 一览（release notes）

中文商店一览：

```text
https://partner.microsoft.com/zh-cn/dashboard/products/<ProductId>/submissions/<SubmissionId>/listings?languageid=5&languagecode=zh-cn
```

- 找到 `#releaseNotes`；
- **在原有内容后追加**，不要覆盖旧版本说明；
- 每条以版本号开头，一行一条；
- 保存后回概览，`Store 一览` 应为 `已更新`。

0.3.18 实际追加文案：

```text
0.3.18：修复运行时主题：深色/浅色模式现在正确跟随系统与窗口主题切换，界面不再固定为单一主题。
0.3.18：修复高对比度主题：移除对高对比度调色板的全局硬编码合并，此前该覆盖会使系统/自定义主题失效。
```

注意：提交后不要为了补其他语言 release notes 再改 listing，否则可能让已提交的认证失效；需要补语言时，在**提交前**完成。

### 5.4 提交选项与 `runFullTrust`

提交选项：

```text
https://partner.microsoft.com/zh-cn/dashboard/products/<ProductId>/submissions/<SubmissionId>/options
```

如果页面检测到受限能力 `runFullTrust`：

- 在“为何需要使用 runFullTrust 功能，如何在产品中使用？”填写用途；
- 保存；
- 页面没有独立的“请求批准”按钮时，说明会随提交一起进入认证；如微软在认证阶段要求补充材料，按通知补充；
- 保存后概览的 `提交选项` 可能显示 `未更改`，含义是“已保存且没有新的未保存改动”，不是“未完成”。

0.3.18 实际使用说明：

```text
TrueToneCap 是 WinUI 3 桌面截图与色彩管理应用。runFullTrust 用于以完整 Win32 进程运行应用本体，访问 Windows 图形/显示 API、文件系统、系统主题与高对比度设置，并调用随包分发的本地图像编码器；不用于远程代码执行或权限提升。
```

### 5.5 提交认证

回到产品概览：

```text
https://partner.microsoft.com/zh-cn/dashboard/products/<ProductId>/overview
```

提交前检查：

- 包：`Validated` / `已更新`；
- Store 一览：`已更新`；
- 提交选项：已保存（常见显示 `未更改`）；
- 年龄分级等必需模块：`完成`；
- 线上旧版仍可正常存在。

点击 `提交进行认证` 时注意：

- 按钮宿主可能带 `disabled` / `disable-submit` 属性，内部 shadow button 也可能 disabled；
- 保存后状态有异步延迟，**先等待并刷新**，直到宿主 `disabled` 属性消失；
- 点击后页面可能没有立即跳转、没有明显弹窗，这是正常的异步提交；
- 成功后概览会变为 **“在 Microsoft Store 中 / 正在认证更新”**，并出现“认证状态”时间线（提交 → 预处理 → 认证 → 发布），`提交进行认证` 按钮消失；
- 页面会显示 **“正在认证(Submission N: 上次修改时间 …)”**；记录这个 N 与修改时间。

0.3.18 实际结果：

- `Submission 5` / ID `1152921505702422497`；
- 状态：**正在认证更新**；
- 最后修改：2026-11-17；
- 线上仍是 `Submission 4` / 0.3.16；0.3.18 尚未上线。

---

## 6. 提交后记录与上线核验

提交成功后立即更新版本目录里的 `STORE-SUBMISSION.md`：

- 状态从“待提交”改为“已提交认证”；
- 记录 Product ID、Submission ID、包名、体积、哈希；
- 记录 listing 更新范围（例如仅中文 release notes）；
- 记录 `runFullTrust` 说明已保存；
- 记录“线上旧版仍是哪个版本”；
- 记录提交时间与当前状态。

认证周期通常数小时，最多约 3 个工作日。通过后：

- 商店产品页应显示 0.3.18；
- 线上包版本应从 0.3.16 更新到 0.3.18；
- 若认证失败，按 Partner Center 的失败项修复；**不要直接复用已失败的版本号**，除非确认商店允许且没有版本冲突。

**在商店产品页真实显示新版本前，不要对外说“已发布”，只能说“已提交认证 / 正在认证”。**

---

## 7. 常见故障与处置

| 现象 | 根因 | 处置 |
|---|---|---|
| `computer_use_tool plane="cu"` 授权超时 / `NO_SESSION` | 运行时无本机桌面权限 | 停止 GUI 循环，改用本机 360 CDP；不要在真实桌面反复点击 |
| 内置 Browser Use 打开 Partner Center 落在微软登录页 | 内置浏览器没有本机登录态 | 不要用它提交；改用本机 360 独立实例 |
| `Copy-Item` 复制 Cookies 失败 | 360 占用文件锁 | 用 WMI `Win32_ShadowCopy` 卷影快照只读复制 |
| `esentutl /y` 返回 `JET_errFileAccessDenied` | 同上 | 同上；不要反复试 `esentutl` |
| SQLite `backup()` 返回 `unable to open database file` | 同上 | 同上 |
| `vssadmin create shadow` 报 `Invalid command` | 当前系统版本不支持该子命令 | 用 WMI `Win32_ShadowCopy.Create` |
| Playwright `set_input_files` 报 >50MB | Playwright 传输限制 | 用 CDP `DOM.setFileInputFiles`，让本机浏览器读取 |
| 上传后显示 `null Bytes` / `已暂停` | 上传期间关闭浏览器/断开 CDP | 移除失败包，重新上传；上传期间不要 `browser.close()` |
| 包处理出现 `This package didn't finish processing` | 处理中的暂态 | 等待并重新读取；常见会变为 `Validated` |
| `提交进行认证` 点击无反应 | 按钮仍是 disabled；保存后状态延迟 | 等待并刷新，检查宿主 `disabled` 属性；不要连续点击 |
| `提交选项` 显示 `未更改` | 已保存、无新改动 | 不是未完成；以页面无红色错误/可提交按钮为准 |
| `runFullTrust` 提示“必须先请求批准” | 受限能力需要说明/审批 | 填写并保存用途说明；提交后按认证通知补充审批材料 |
| 提交后仍是草稿 | 提交点击未生效或按钮未启用 | 回到概览确认 `提交进行认证` 是否消失；不要伪造“已提交” |
| `.appxsym` 没有上传入口 | 商店包页只接受 MSIX，符号包可选 | 不要把 `.appxsym` 未上传说成提交失败；保留在版本目录即可 |

---

## 8. 安全与权限边界

1. **不读取、不输出、不落盘 Cookie 内容**。只复制文件；日志里不打印 Cookie、Authorization、账号邮箱等敏感值。
2. **不关闭、不修改用户主 360**。调试实例必须使用独立 `--user-data-dir`；只用命令行匹配该 profile 来结束调试进程。
3. **调试端口只绑本机**；不要将 9222 暴露到局域网或公网。
4. **不代替用户登录**。登录、验证码、二次验证由用户在独立 360 窗口完成；代理不输入密码。
5. **提交前必须获得明确的发布授权**。用户说“测试”“看看”不等于同意提交；`提交进行认证` 前要确认目标产品、版本和提交范围。
6. **不改包、不改哈希**。包上传后不要再构建或覆盖同目录的 MSIX。
7. **不移动旧标签**。GitHub 发布与商店提交是两条独立路径；商店流程不修改 `v0.3.17` 等旧标签。
8. **清理临时材料**。完成后关闭调试实例，删除临时 profile 与卷影副本；删除前确认只指向调试对象，不碰主 360 的真实 profile。

---

## 9. 标准验收清单

提交前：

- [ ] 版本坐标一致，新版本严格大于线上版本；
- [ ] Release 构建 0 警告 / 0 错误；
- [ ] 全量测试通过；
- [ ] WACK `PASS`，可选失败项已记录；
- [ ] MSIX 哈希、体积、清单身份已记录；
- [ ] 独立 360 CDP 实例已启动，Partner Center 登录态已验证；
- [ ] 主 360 未被关闭或修改。

提交后：

- [ ] 包页显示 `Validated` / `已更新`；
- [ ] Store 一览显示 `已更新`，release notes 为追加而非覆盖；
- [ ] 提交选项已保存，`runFullTrust` 说明已填写；
- [ ] `提交进行认证` 已消失，页面显示“正在认证更新”；
- [ ] 已记录 Submission ID、状态、最后修改时间；
- [ ] `STORE-SUBMISSION.md` 已回写；
- [ ] 线上旧版本仍可正常访问。

上线后：

- [ ] 商店产品页显示新版本；
- [ ] 线上包版本已替换；
- [ ] 若认证失败，已记录失败项并规划修复版本；
- [ ] 临时调试实例与 profile 已清理。

---

## 10. 已验证案例（0.3.18）

| 项目 | 值 |
|---|---|
| 产品 | `luoye-cpu.TrueToneCap` /「真色截图」 |
| Product ID | `9N42LP5NVFWR` |
| 线上提交 | `Submission 4`，ID `1152921505702407086`，包 `TrueToneCap.App_0.3.16.0_x64.msix` |
| 当前提交 | `Submission 5`，ID `1152921505702422497` |
| 新包 | `TrueToneCap.App_0.3.18.0_x64.msix`，163,066,748 B，SHA256 `6b2da64e02281ec7540f556a9508637b2429b8c6ae57e139164818039dabc7a2` |
| 提交状态 | 正在认证更新；最后修改 2026-11-17 |
| 线上状态 | 仍为 0.3.16；0.3.18 通过认证后自动发布 |

这次验证证明：**本机 360 CDP 复用登录态 + CDP 大文件上传 + 提交按钮异步解除 disabled** 的组合可以完成商店更新提交；后续版本沿用本文件，不需要重新摸索。
