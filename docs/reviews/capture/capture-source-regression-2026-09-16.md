# 回归修复：捕获只有桌面、没有窗口（参考 0.3.3）

- 日期：2026-09-16
- 现象：截图**只有桌面壁纸，不含任何应用窗口**
- 用户提示：该问题在 0.3.4 早期出现过并被修复过，要求参考 0.3.3 的实现修正

---

## 结论先行

不是简单恢复 0.3.3 的代码。0.3.3 的那段实现**本身有 bug**（恒取主显示器），正确做法是**修正反查**后恢复它，并放在合适的位置。

---

## 根因链

### 1. 0.3.3 的实现：能捕获窗口，但多显示器会串到主屏

```csharp
// 0.3.3 CreateItemFromDisplayId
if (s_cachedDisplayId is null)
{
    var primaryArea = Microsoft.UI.Windowing.DisplayArea.Primary;   // ← 恒取主显示器
    s_cachedDisplayId = new DisplayId(primaryArea.DisplayId.Value);
}
return GraphicsCaptureItem.TryCreateFromDisplayId(s_cachedDisplayId.Value);
```

它是**优先路径（方法0）**，所以 0.3.3 能正常捕获窗口内容。代价是多显示器场景恒捕获主屏。

### 2. 上一轮：因多显示器 bug 把它整个删除

删除后只剩：

1. CsWinRT `CreateForMonitor`
2. RoGetActivationFactory + vtable
3. **桌面窗口回退** ← 只捕获壁纸

### 3. 回归：CreateForMonitor 失败 → 落到了方法3

一旦 1、2 都失败，就回退到"桌面窗口"——**只有壁纸，完全不含应用窗口**，这正是用户报告的现象。

日志证据（修复前每次截图都有）：

```
[Warning] WGC 捕获回退到桌面窗口模式，截图可能仅包含桌面壁纸，不含应用窗口。
```

---

## 修法：恢复，但修正反查

**关键点：DisplayId 其实可以可靠反查，0.3.3 只是没做对。**

```csharp
// 取该 hmonitor 的矩形，用中心点问系统"这个点属于哪个显示器"
var mi = new MONITORINFO { cbSize = ... };
GetMonitorInfo(hmonitor, ref mi);
var center = new PointInt32((mi.rcMonitor.Left + mi.rcMonitor.Right) / 2,
                            (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2);
var area = DisplayArea.GetFromPoint(center, DisplayAreaFallback.None);
return new DisplayId(area.DisplayId.Value);
```

这样每个显示器拿到**它自己**的 DisplayId：既不串主屏，又能正常捕获窗口内容。

### 位置设计

放在 **`CreateForMonitor` 之后、"桌面窗口"之前**：

| 顺序 | 路径 | 说明 |
|---|---|---|
| 1 | CsWinRT `CreateForMonitor` | 首选，最精确 |
| 2 | vtable `CreateForMonitor` | 备选 |
| 3 | **DisplayId（本次新增回退）** | 按 hmonitor 正确反查，能捕获窗口 |
| 4 | 桌面窗口 | 最后手段，**只有壁纸** |

刻意**不**放在最前面——避免改变原本已经成功的路径。

---

## 附带发现：两个修复互为印证

修好**设备生命周期**（另一个根因：`GetOrCreateDevice` 因 hmonitor 变化销毁设备）之后，`CreateForMonitor` **直接就成功了**，不再需要任何回退。

由此推断：之前观察到的"回退到桌面窗口"，很可能正是**设备被 Dispose 导致 item 创建失败**引起的——两个问题在根子上是同一个。

---

## 验证（双屏实机）

| 项 | 修复前 | 修复后 |
|---|---|---|
| 降级警告 | 每次截图都出现"回退到桌面窗口" | **无** |
| item 创建路径 | 落到方法3（桌面窗口） | 正常 `CreateForMonitor` |
| 截图耗时 | — | 2359ms / 2966ms（两次复测） |
| 多显示器拼接 | 第二个会话首帧超时 | 两个会话均启动并首帧就绪 |

两次复测日志中**无任何回退记录**（无 DisplayId、无桌面窗口），说明走的是正常路径。

⚠️ **最终确认需要你肉眼看一下**：截图里是否真的包含了应用窗口。我无法在沙箱环境中查看图像内容。

---

## 教训

> 一个方法"有 bug"时，**先判断是实现有 bug 还是思路有 bug**。
> 0.3.3 的 DisplayId 路径思路是对的（能捕获窗口），错的是实现（恒取主屏）。
> 上一轮按"实现有 bug"处理成了"整体删除"，结果丢失了它真正的能力，引发回归。
> 正确做法：修实现，而不是删能力。
