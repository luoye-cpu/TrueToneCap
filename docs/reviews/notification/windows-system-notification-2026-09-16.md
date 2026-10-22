# Windows 系统通知（非打包应用）实现分析

- 日期：2026-09-16
- 现象：设置里选"Windows 自带通知"后，截图完成**收不到通知**
- 依据：Microsoft Learn《App notifications quickstart》+ WindowsAppSDK 官方 spec

---

## 结论先行

非打包（unpackaged，非 MSIX）应用要显示 Windows 系统通知，需同时满足**四个条件**，缺一即**静默失败**（Show 不报错、日志正常，但通知不出现）：

| # | 条件 | 本项目状态 |
|---|---|---|
| 1 | 应用**不能以管理员权限运行** | ✅ 清单未声明 requireAdministrator，但需用户不以"管理员身份运行"启动 |
| 2 | 设置 **AppUserModelID**（进程级） | ✅ 已加 `SetCurrentProcessExplicitAppUserModelID` |
| 3 | **带 AUMID 的开始菜单快捷方式** | ✅ 已加（COM: IShellLinkW + IPropertyStore 写 PKEY_AppUserModel_ID） |
| 4 | **Register 时提供 DisplayName + 本地 Icon** | ✅ 已加（项目无内置图标，程序化生成 256×256 PNG） |

---

## 官方文档要点（与直觉相反的部分）

### 1. 提升权限 = 通知静默失败

> "App notifications are not supported when your app is running with administrator privileges (elevated). **Show will fail silently** and no notification will be displayed."

这是最容易踩的一条：日志里 Show 调用成功、无任何异常，但通知就是不出现。

### 2. WindowsAppSDK 移除了 AUMID 重载

spec 明确说明 Windows SDK 旧的 `AppId` 重载带来过"字符串长度限制、PII 泄漏、无效桌面注册"等问题，因此 **WindowsAppSDK 的公开 API 不再接受 AppId/AUMID 参数**。

⇒ AUMID 只能靠**进程声明**与**快捷方式属性**让系统识别，不能通过 `Register()` 传入。

### 3. 非打包应用专属：带资产的 Register 重载

```csharp
// 仅 Unpackaged 可用；Packaged 调用会抛异常
AppNotificationManager.Default.Register(string displayName, Uri iconUri);
```

限制：
- DisplayName 与 Icon 要么**都提供**，要么**都不提供**
- Icon 格式限 `png / bmp / jpg / ico`
- Icon **必须位于本地机器**，否则**直接抛异常**（不是静默失败）
- 无参 `Register()` 时，系统"从 Shell 获取"显示名与图标 —— **本项目没有内置图标资源，这一步会失败**

### 4. 其它规范约束

- Unpackaged 应用调用 `Register()` 后，**当前 .exe 会被注册为 COM Server**（无需 manifest，manifest 仅 Packaged 需要）
- 应在 `Register()` **之前**订阅 `NotificationInvoked`，否则每次点击会启动新进程
- 退出前应调用 `Unregister()` 释放 COM
- 桌面应用会**忽略**通知 XML 中的 `activationType="background"`

---

## 本项目的实现

`src/TrueToneCap.App/Services/ToastService.cs`

1. **管理员检测**：`IsElevated()`（WindowsPrincipal.IsInRole），命中则记 Warn 并关闭系统通知
2. **AUMID**：`SetCurrentProcessExplicitAppUserModelID("TrueToneCap.Desktop")`，在 `Register()` **之前**调用
3. **开始菜单快捷方式**：
   `%APPDATA%\Microsoft\Windows\Start Menu\Programs\TrueToneCap.lnk`
   通过 `IShellLinkW` 创建，再用 `IPropertyStore` 写入
   `PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5`（PROPVARIANT vt=VT_LPWSTR=31）

4. **本地图标**：项目无内置图标，程序化生成 256×256 纯色 PNG（#0078D4）到
   `%LOCALAPPDATA%\TrueToneCap\app.png`（手写 PNG 块 + CRC32），再以
   `Register("TrueToneCap", new Uri(icon))` 注册

5. **保底回退**：`s_systemNotifyAvailable` 为 false 时（注册失败或以管理员运行），`ShowToast` **不返回**，改为走应用内浮层 —— 保证用户总能收到通知

---

## 排查记录（可供同类问题复用）

| 阶段 | 日志表现 | 判断 |
|---|---|---|
| 初始 | `通知注册成功`，无其它 | 代码路径疑似未走 |
| 加日志后 | `已提交系统通知` 出现，仍不显示 | 代码全通，卡在 Windows 侧 |
| 查通知设置 | 应用**不在**列表里 | 系统未登记该应用 |
| 加 AUMID + 快捷方式 | 快捷方式创建成功，仍不显示 | 还缺显示名/图标资产 |
| 查官方文档 | 发现"提升权限静默失败"与"带资产 Register" | 补齐第 4 个条件 |

**关键经验**：系统通知的失败是**静默**的。必须自己加"已提交"日志，才能区分"没走到 Show"和"提交了但系统没显示"。

---

## 仍未验证 / 风险

- 本机无法确认通知是否真的弹出（需用户肉眼确认）
- 图标是程序化生成的纯色图，若 Windows 对尺寸/格式有更严要求，Register 可能抛异常（已 catch，会回退浮层）
- 若 Windows 侧仍不显示，最后的退路是**放弃系统通知、统一使用应用内浮层**（已实现回退，用户始终能看到通知）
