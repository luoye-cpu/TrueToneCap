# 通知实现调研：为何放弃 Windows 系统通知，转向自绘浮层

- 日期：2026-09-16
- 结论：**主流截图工具不使用 Windows 系统通知**，改用自绘浮层

---

## 一、Windows 系统通知（非打包应用）的完整条件

依据 Microsoft Learn《App notifications quickstart》与 WindowsAppSDK 官方 spec，
非打包（unpackaged / 非 MSIX）应用要显示系统通知，需**同时**满足：

| # | 条件 | 依据 | 本项目 |
|---|---|---|---|
| 1 | **不得以管理员权限运行** | 文档原文："Show will fail silently and no notification will be displayed" | ✅ 清单为 asInvoker |
| 2 | 进程级 **AppUserModelID** | 非打包应用必须自行声明 | ✅ 已实现 |
| 3 | **带 AUMID 的开始菜单快捷方式** | 系统据此把应用登记进通知列表 | ✅ 已实现（COM: IShellLinkW + IPropertyStore） |
| 4 | **Register 时提供 DisplayName + 本地 Icon** | spec：非打包应用专属重载；Icon 限 png/bmp/jpg/ico 且必须在本地 | ✅ 已实现（程序化生成 PNG） |

### 为什么仍会失败

即便四条都满足，仍有**不可控的外部变量**：

- Windows 需要时间登记应用（重启资源管理器/系统才生效）
- 用户可能开启专注助手 / 通知被系统设置关闭
- Windows 版本差异（24H2 行为可能变化）
- **失败是静默的**：`Show()` 不抛异常、不返回错误码，日志完全正常

⇒ 这意味着**无法通过代码保证它一定工作**，也无法在失败时给用户有意义的提示。

---

## 二、其他软件如何实现

| 软件 | 通知方式 | 说明 |
|---|---|---|
| **ShareX** | **自绘窗体**（NotificationForm） | C# 开源截图工具，用自定义通知窗体，可堆叠、带进度与操作按钮 |
| **Snipaste** | 自绘 | 截图后显示轻量浮层 |
| **Flameshot** | 自绘 | Qt 实现的自定义提示 |
| Windows 系统应用 | 系统通知 | 它们是打包的、有完整应用身份 |

**关键结论**：与我们同类的桌面截图工具，**没有一家依赖 Windows 系统通知**。原因就是上面那些不可控因素。

替代方案（CommunityToolkit.WinUI.Notifications 的 `ToastNotificationManagerCompat`）本质上是把上述 1-4 条封装起来，仍需快捷方式与图标，**同样受这些外部条件约束**，并不能绕过根本问题。

---

## 三、本项目的实现（参考 ShareX 的完成度）

`SilentCaptureToast`（自绘 WinUI 窗口）+ `ToastService`（通道分发）

### 已实现

| 能力 | 说明 |
|---|---|
| 四角定位 | BottomRight / TopRight / TopLeft / BottomLeft，尺寸固定 480×144 |
| 无边框无标题栏 | `OverlappedPresenter.SetBorderAndTitleBar(false, false)` |
| Windows 11 卡片布局 | 语义图标 + 应用名 + 时间戳 + 标题 + 正文 |
| **多通知堆叠** | 按槽位纵向错开，最多 3 条，超出关闭最旧的；关闭后剩余自动回位 |
| **滑入 + 淡入动画** | 220ms CubicEase，从右侧 32px 滑入 |
| 操作按钮 | 打开文件夹 / 打开文件 / 复制路径（带"已复制"就地反馈） |
| 悬停暂停 | 鼠标移入停止自动关闭，移出恢复 |
| 系统通知可选 | 保留 `WindowsNotify` 选项；**不可用时自动回退浮层** |

### 关键实现细节（踩过的坑）

1. **WinUI 的 `Window` 不是 FrameworkElement**
   - 没有 `Loaded` 事件 → 用内容根元素 `RootGrid.Loaded`
   - 没有 `Resources` 属性（WMC0011）→ 资源字典挂 `<Grid.Resources>`

2. **不要靠 Measure 做窗口自适应**：`RootGrid.Loaded` 时 `Measure()` 返回 0，
   窗口被压到最小高度后标题栏占满、内容看不见（已回滚为固定尺寸）

3. **无边框必须用 presenter 的 API**，不能用 `ExtendsContentIntoTitleBar`（那只是把内容扩展到标题栏，标题栏仍在并压住内容），手动清 `WS_CAPTION` 也不彻底

4. **WinUI 的 `RoutedEventArgs` 没有 `Handled`**（WPF 才有）→ 阻止冒泡用 `PointerRoutedEventArgs.Handled`

---

## 四、给用户的建议

设置里的"Windows 自带通知"保留为可选（尽力而为），**默认推荐四角位置的应用内浮层**：

- 可控：不依赖快捷方式、AUMID、图标、权限、系统设置
- 可诊断：出问题能在日志里定位
- 功能更强：带操作按钮（打开文件夹/文件/复制路径），系统通知做不到

若确实需要系统通知，请确认：
1. 未以管理员身份运行 TTC
2. 启动过一次（快捷方式会自动创建）后**重启 TTC**
3. 设置 → 系统 → 通知里能看到 TrueToneCap 且开关已打开
