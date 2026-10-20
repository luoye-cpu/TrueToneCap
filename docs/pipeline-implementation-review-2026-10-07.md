# 管线与实现审查报告

> 日期：2026-10-07
> 范围：全仓管线与实现（App 19.7k 行 + Core 18.7k 行 + Tools 6.1k 行）
> 方法：**从源码实证**，不采信注释与既有文档的自述；可疑处逐一跑代码验证。
> 结论按严重度分级，**含对我自己本次会话改动的自查**。

---

## 0. 结论摘要

| 级别 | 数量 | 内容 |
|---|---|---|
| 🔴 **严重（已修）** | 1 | 我自己写的 `StartupManager` 反射有 3 个 bug ⇒ 打包形态自启**永远不生效且静默** |
| 🟠 **中等（已修）** | 2 | 翻译扇出缺 `CancellationToken`；译文发布非原子 ⇒ 均已修复并有变异测试 |
| 🟡 **低** | 3 | 若干可改进项（见 §4） |
| 🟢 **复核通过** | 6 类 | D3D 设备锁、捕获回调生命周期、临时文件清理、静默失败分级、编码器回退、OCR 解码 |

> ✅ **本次审查发现的问题已全部修完**（1 严重 + 2 中等）。剩余 🟡 三项见 §4。

**最重要的一条**：审查前半段我判断"代码质量高、没找到问题"，直到**转向审查我自己本次会话的改动**，
才暴露了一个**真缺陷**。教训写在 §2 末尾。

---

## 1. 🟢 复核通过的部分（先说没问题的，避免只报坏消息）

### 1.1 D3D11 共享设备的加锁纪律 —— 正确

`FormatEncoders.cs` 有 `s_cachedD3DDevice` + `s_deviceLock`。我逐一核对**所有 14 处访问**：

- 10 处读在 `lock (s_deviceLock)` 内（L1176 / 1430 / 1500 / 1581 / 1594 等）
- L1477、L1569 的读看似裸露，实际分别位于 L1475、L1567 的 `lock` 块内 —— **是我一开始看错了**
- `NotifySharedDeviceDisposed()`（L1022）只清引用不 `Dispose`，注释写明"所有权在调用方"，**正确**

> 这段还带着一次真实修复的记录（P1-8 悬垂设备）：`WgcCaptureService` 换显示器时 Dispose 旧设备，
> 但此前不通知编码层 ⇒ 下次 AVIF 走 NVENC 纹理路径时访问已释放 COM 对象 ⇒
> **P/Invoke 层崩溃不可捕获 ⇒ 整进程被杀**。现在通知链已补上。

### 1.2 捕获回调的在途计数 —— 正确（我先误判了一次）

`WgcCaptureService.OnFrameArrived` 有 5 个提前 `return`（L281/288/295/312/331）。
我一度怀疑它们会**跳过 `Interlocked.Decrement`** ⇒ 计数永不归零 ⇒
`Dispose` 里 `_callbacksDrained.Wait(2000)` 必然超时。

**实测读完后确认：不会。** 递减在 L356 的 `finally` 里，覆盖所有提前返回路径。
配套的 `TryResetEvent`/`TrySetEvent` 还专门吞掉 `ObjectDisposedException`，
避免 Dispose 后事件已释放时二次抛异常。

### 1.3 捕获的 Dispose 顺序 —— 正确且是最佳实践

```
1. 先取消订阅 FrameArrived      ← 防止新回调进入
2. _disposed = true             ← 阻止已在途回调继续处理
3. 等待在途回调排空（2s 超时）  ← 不让资源在回调使用中被释放
4. 释放资源（D3D 资源在 _gpuLock 内）
```

### 1.4 临时文件清理 —— 创建/删除严格配对，且都在 `finally`

| 文件 | tempPaths | File.Delete | 在 finally 内 |
|---|---|---|---|
| `JpegLiNative.cs` | 3 | 5 | ✅ 全部 |
| `NativeAvifEncoder.cs` | 3 | 3 | ✅ 全部 |
| `NativeJxlEncoder.cs` | 6 | 6 | ✅ 全部 |
| `NativeWebPEncoder.cs` | 1 | 1 | ✅ 全部 |

逐个核对 `File.Delete` 上下文，确认**没有一条在非 finally 路径上**。

### 1.5 设备丢失检测已接入（不只是有个帮助类）

`D3DDeviceLost.cs` 是纯函数（可单测、零副作用）。关键是它**真的被调用了**：

```
HdrCaptureWindow.cs:564  每 30 帧主动查 IsDeviceRemoved
HdrCaptureWindow.cs:628  异常路径查 ShouldRebuild
HdrCaptureWindow.cs:654  Describe() 写日志
```

主动轮询这条尤其对 —— 注释指出"某些驱动下 Present **静默失败**（不抛异常、画面不更新），
只有 `DeviceRemovedReason` 能暴露问题"。

### 1.6 静默失败分级 —— 165 处空 `catch` 逐一分类后，绝大多数正当

我担心 165 是个坏数字，于是按**上下文**分类：

- **正当**：文件追加失败（日志绝不能崩应用）、UI 事件派发、日志轮转删除、
  `Dispose` 清理、"级联回退"（后端 A→B→C，每级失败落下一级）
- 典型的级联回退在 `TranslationService.cs:438-440`（Google 多端点依次尝试），
  且**出口是 `return null` 让调用方继续回退**，最终 `TranslateAsync` L170
  抛 `TranslationException` 并带上最后一次失败原因 —— **没有把失败伪装成成功**

**结论：这里没有需要修的缺陷。** 逐条看比看数字重要。

---

## 2. 🔴 严重（已修）：我自己写的反射自启 —— 3 个 bug，且全部静默

### 2.1 事实

这是**我在本次会话里新写的代码**（上一轮"实现 StartupTask"）。
我当时的理由是"不想让未打包形态沾 WinRT 依赖"，于是用反射调 `StartupTask` 三个成员。

**那版有三个真 bug，且全部指向同一后果 —— 打包形态开机自启永远不生效、只在日志留一行 Warn：**

| # | Bug | 后果 |
|---|---|---|
| ① | `MakeGenericMethod(typeof(int))` 写死 | `GetAsync` 返回 `IAsyncOperation<StartupTask>`、`RequestEnableAsync` 返回 `IAsyncOperation<StartupTaskState>`，**都不是 `int`** ⇒ `Invoke` 抛 `ArgumentException` |
| ② | 对**开放泛型**参数类型调 `IsAssignableFrom` | 恒为 `false` ⇒ 循环里**每个** `AsTask` 重载都被 `continue` 跳过 |
| ③ | 于是 `AsTask` 永远返回 `null` | 所有调用方提前 `return`，**只留一行 Warn** |

### 2.2 我为什么能确认（而不是猜）

我第一次写这段时就**无法在本机验证**（打包形态需要真装 MSIX）。这正是危险所在：
**运行时才走到的路径 + 静默失败 + 无法本地验证 = 三重叠加**。

转机是我去验证"反射到底有没有必要"，写了两个 10 行的探针工程：

```csharp
// 探针 1：System.WindowsRuntimeSystemExtensions.AsTask 能否直接调用？
using System;
var t = System.WindowsRuntimeSystemExtensions.AsTask(op);   // ✅ 直接编译通过

// 探针 2：StartupTask 能否直接用？
using System;                                   // AsTask 扩展在此命名空间
using Windows.ApplicationModel;
var st = await StartupTask.GetAsync("TrueToneCapStartup");
StartupTaskState state = st.State;
var after = await st.RequestEnableAsync();
st.Disable();                                   // ✅ 直接编译通过
```

**两次都编译通过** ⇒ 本工程 TFM 是 `net11.0-windows10.0.26100.0`，WinRT 投影本来就在编译期可用。
**"不想沾 WinRT"这个前提根本不成立**，反射是纯粹的额外风险。

### 2.3 修法

全部改为强类型直接调用：

```csharp
var startupTask = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
int s = (int)startupTask.State;
var finalState = startupTask.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
startupTask.Disable();
```

**编译期就能查出签名错误** —— 那 3 个 bug 一个都活不下来。

### 2.4 加了防复发断言（已做变异测试）

```csharp
Assert("StartupManager: 打包分支不得再用反射（那版反射恒失败 ⇒ 自启静默不生效）",
    !text.Contains("MakeGenericMethod") && !text.Contains("GetAssemblies")
    && !text.Contains("ResolveStartupTaskType"));
```

**变异验证**：把 `MakeGenericMethod` 字样塞回源码 ⇒ 该断言立即报红（exit 1）。**判据有牙。**

### 2.5 教训（写给下一次）

> **能用编译期类型，就别用反射。**
> 反射把这些错误从编译期推迟到了运行时，而运行时那条路径**只有装机后才会走到**。
>
> 更具体地说，我犯的错是：**为一个"假想约束"（不想沾 WinRT）引入了真风险（反射）**，
> 而且**没有先花 2 分钟验证那个约束是否成立**。探针工程只用了 10 行代码就推翻了它。
>
> ⚠️ 还有一层：这个缺陷**在我上一轮"自测通过"时是看不出来的** ——
> 单元测试只断言了源码里有 `RequestEnableAsync` 字样（正向锚点），
> 而反射版**确实含有那个字样**。**文本级断言验证不了运行期行为。**

---

## 3. ✅ 已修：两项中等问题（2026-10-07 当日修复）

### 3.1 翻译扇出缺 `CancellationToken` —— **已修**

**原位置**：`OcrPreviewWindow.xaml.cs:482`

```csharp
try { return await translator.TranslateAsync(lineText, _translationConfig.TargetLanguage); }
//                                                                    ^^^^^^^^^^^^^^^^ 未传 ct
```

`TranslateAsync` 的签名**接受** `CancellationToken ct = default`，但调用点没传；
且 `OcrPreviewWindow` 里**没有任何 `CancellationTokenSource`**，`OnClosed` 是空的（L558）。

**实测确认的后果**（不是猜的）：

- 关闭预览窗后，HTTP 请求继续跑到各自的内部超时
  （Google 每端点 5s、Bing 5s、LLM 视配置）
- `SemaphoreSlim(4)` 的槽位被占着直到请求结束
- **不会崩**：最终 UI 更新走 `DispatcherQueue.TryEnqueue`，窗口已关时安全 no-op
- `_translatedLines` 是**实例字段**（非 static）⇒ **不会跨窗口串状态**

**修法**：
- 窗口持有 `_workCts`，每次翻译新建（不复用已完成的 CTS —— 复用会让新工作一开始就处于已取消态）
- token 传进 `TranslateAsync(line, lang, null, ct)` / `TranslateImageWithVisionAsync(..., ct)` / `sem.WaitAsync(ct)`
- `OnClosed` 里 `Cancel()`（**只 Cancel 不 Dispose** —— 链仍在 await，此刻 Dispose 会让它们抛
  `ObjectDisposedException` 而不是 `OperationCanceledException`，那就把"用户主动取消"变成了"异常"）
- 新增 `catch (OperationCanceledException)` 分支：取消**不是错误**，不报成"翻译失败"
- 取消后 `ThrowIfCancellationRequested()`，不再发布结果

> ⚠️ **修第一版时我自己漏了一格，被新加的判据当场抓住**：
> 行级 `catch (Exception ex)` 会**先吞掉** `OperationCanceledException`，把它当成"这一行翻译失败"，
> 于是 `firstError` 被写成"操作已取消"，关窗在状态栏里显示成"某行失败"。
> 已改为 `catch (OperationCanceledException) { throw; }` **置于通用 catch 之前**。
> 这条教训值得记住：**取消语义穿越多层 catch 时，顺序就是正确性**。

### 3.2 译文发布改为不可变快照 —— **已修**

原先 `_translatedLines`（数据）与 `_showTranslation`（显示标志）**分两处写**，
中间隔着一次 `DispatcherQueue.TryEnqueue`。当前调用顺序恰好正确，但渲染端只要有人在
`Task.WhenAll` 之前读一次，就会拿到"有译文但标志未置位"或反之的**半成品**。

**修法**：合成不可变记录，**单次引用写入**：

```csharp
private sealed record TranslationSnapshot(
    IReadOnlyList<string> Lines, int? DefiniteFailed, string? FirstError);
private TranslationSnapshot? _translation;   // 一次赋值 = 一次发布
```

同时把原先混用的两个概念**显式拆开**（这是 20 处调用点里最容易再次混淆的地方）：

| 概念 | 判据 | 语义 |
|---|---|---|
| **有没有译文** | `_translation is not null` | 由快照存在性决定 |
| **显示哪一边** | `_showTranslation` | 用户的视图选择，可在原文/译文间来回切 |

### 3.3 三项修复的验收与防复发

新增 `Test_OcrPreviewTranslation_CancellationAndSnapshot`（10 条断言），
并对**两条最关键**的做了变异测试：

| 变异 | 结果 |
|---|---|
| 去掉 `catch (OperationCanceledException) { throw; }` | ✅ 判据报红（正是我第一版犯的错） |
| 把 `OnClosed` 改回空 | ✅ 判据报红 |

> 诚实边界：这些是**文本级接线断言**。它们能证明"接线存在"，不能证明"取消真的生效"——
> 后者要在真机上长时间点翻译再关窗观察。但 §2 的教训恰恰是"文本断言验证不了运行期行为"，
> 所以这里明确区分：**接线形态用文本断言钉住，运行期行为列入待实测清单。**


---

## 4. 🟡 低：可改进项

| # | 项 | 说明 |
|---|---|---|
| 1 | `Publisher` 仍是占位符 | 已在就绪度报告中登记，需账号 |
| 2 | 165 处空 `catch` 无统一约定 | 多数正当，但**缺少"为什么可以吞"的一句话注释**。建议只对"非清理类"的空 catch 补注释 |
| 3 | `MainWindow.xaml.cs` 26 处空 catch | 全仓最多，值得单独过一遍（我抽查的几处都是 UI 安全网，正当） |

---

## 5. 本次审查的可复现命令

```powershell
# ── 复核 D3D 设备锁纪律（应全部落在 lock 内）──
Select-String -Path src/TrueToneCap.Core/Encoding/FormatEncoders.cs -Pattern "s_cachedD3DDevice|s_deviceLock"

# ── 复核捕获回调 finally 覆盖（递减必须在 finally 里）──
Select-String -Path src/TrueToneCap.App/Services/WgcCaptureService.cs -Pattern "_inFlightCallbacks" -Context 3,3

# ── 复核临时文件清理配对 ──
foreach ($f in "JpegLiNative","NativeAvifEncoder","NativeJxlEncoder","NativeWebPEncoder") {
  $t = Get-Content "src/TrueToneCap.Core/Encoding/$f.cs" -Raw
  "$f  temp=$(([regex]::Matches($t,'Path\.GetTempPath\(\)')).Count)  del=$(([regex]::Matches($t,'File\.Delete\(')).Count)"
}

# ── 复现"反射不必要"的结论（10 行探针即可）──
#   建一个 net11.0-windows10.0.26100.0 工程，写：
#     using System; using Windows.ApplicationModel;
#     var st = await StartupTask.GetAsync("TrueToneCapStartup");
#   ⇒ 直接编译通过，无需任何额外包引用

# ── 复核翻译扇出是否传了 ct ──
Select-String -Path src/TrueToneCap.App/OcrPreviewWindow.xaml.cs -Pattern "TranslateAsync\(|CancellationTokenSource|OnClosed"

# ── 复核防复发断言有效（变异测试）──
#   在 StartupManager.cs 里塞回 "MakeGenericMethod" 字样 ⇒ 单元测试应报红
dotnet run --project src/TrueToneCap.Test -c Release -- --unit-tests
```

---

## 6. 我没能验证的（诚实边界）

| 项 | 原因 |
|---|---|
| **打包形态下 `StartupTask` 的真实行为** | 需真装 MSIX 获得包身份；本机造不出来。**这正是 §2 缺陷藏身之处** |
| **`RequestEnableAsync` 被 `DisabledByUser` 拒绝的实际路径** | 同上，需真机 + 在系统设置里关过 |
| **MSIX 沙箱内子进程 + `%TEMP%` 的实际行为** | 需真装实测（已多次登记） |
| **翻译扇出取消后的实际收益** | 需真实网络环境量化 |

> ⚠️ 这四项**全部集中在"打包后才暴露"这一类**。
> 本报告 §2 的缺陷就是在这类盲区里产生的 —— 这条边界值得当成一条正式风险来管理，
> 而不是当成"以后再测"。建议：**装一次 MSIX 并把这几项一起验证**（见就绪度报告第 7 步）。

---

## 7. 验收读数

| 项 | 结果 |
|---|---|
| 5 项目 Release 构建 | ✅ **0 警告 0 错误** |
| 8 套件测试 | ✅ **1869 通过 / 0 失败**（含本轮新增 10 条断言） |
| 防复发断言 | ✅ **两处变异测试均通过**（见 §3.3） |
| MSIX 构建 + 内容门禁 | ✅ 通过 |
| `--selftest` | ✅ 通过 |

### 7.1 本轮新增/修改的防复发判据

| 判据 | 防的是什么 | 变异验证 |
|---|---|---|
| `StartupManager: 打包分支不得再用反射` | 反射版的 3 个 bug（静默失效） | ✅ 报红 |
| `StartupManager: 禁用走强类型 Disable()` | 退回反射字符串调用 | ✅ |
| `行级 catch 必须让 OperationCanceledException 先抛` | 取消被当成"某行翻译失败" | ✅ 报红 |
| `关窗（OnClosed）必须调用 Cancel` | 退回空 OnClosed | ✅ 报红 |
| `译文与显示标志必须合成不可变快照` | 退回两个独立字段（半成品） | ✅ |
| `不得再直接读写 _translatedLines` | 两条数据源并存 | ✅ |

### 7.2 仍未验证（需真机）

- **打包形态 `StartupTask` 的真实行为**（装 MSIX 后测）
- **取消后请求是否真的立即中止**（需真实网络环境长时间观察）
- MSIX 沙箱内子进程 + `%TEMP%`、设置持久化、日志落点

