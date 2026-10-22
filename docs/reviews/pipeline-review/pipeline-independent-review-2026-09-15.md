# TrueToneCap 独立审查报告（第二轮，不沿用历史结论）

- 审查日期：2026-09-15
- **独立性声明**：本报告不参考、不沿用此前任何审查文档的结论。以当前工作区代码为唯一事实来源，从零读码自行判断。子代理的每一条结论我都**逐条读码复核**过；本文标注【已核验】的均已亲自打开源码确认，标注【待核验】的来自子代理报告但我未能逐行确认。
- 与同日另一份报告的关系：那份聚焦「捕获/色彩/编码/UI 交互」，本份聚焦**上次未深入的子系统**——启动与设置、标注与覆盖层渲染、OCR 与翻译。两者互补，不重叠。
- 副产物：本报告同时审查了上一轮我自己提交的 6 项修复，**未发现其引入回归**。

---

## 0. 执行摘要

**一句话结论：在"捕获—编码"主链路之外，标注、OCR/翻译、设置三个子系统存在若干"功能按钮存在但完全无效"的缺陷，且多为必然触发——这些不是边界情况，是用户点下去就会发现的。**

实测基线仍然健康（构建 0 警告 0 错误、395 断言全绿），但**这批缺陷全部逃过了门禁**，原因见第 5 章。

### 最值得注意的三条

1. **标注图层的"上移/下移"按钮完全无效** —— 交换了 `ZOrder`，但渲染从不按 `ZOrder` 排序。
2. **有道翻译后端永久失效** —— `LlmConfig` 构造时从未传入有道凭据，用户填了 AppKey/Secret 也没用，且**静默降级 Google**，无任何提示。
3. **OCR 引擎里的"Windows OCR"选项无法保存** —— 设置白名单写的是 `"Windows"`，而 UI 实际 Tag 是 `"WindowsOcr"`，每次启动都被重置回 DirectML。

---

## 1. 实测基线

```
构建：0 个警告 / 0 个错误
门禁：7 套件 395 断言全部通过
```

---

## 2. 确认缺陷（已逐条读码核验）

### 2.1 【高】标注图层上移/下移完全无效

【位置】`src/TrueToneCap.App/AnnotationWindow.xaml.cs:854-871`

```csharp
private void MoveLayer(int direction)
{
    var layers = _annotationManager.Layers;
    ...
    // 交换 ZOrder
    var tmp = layers[idx].ZOrder;
    layers[idx].ZOrder = layers[newIdx].ZOrder;
    layers[newIdx].ZOrder = tmp;
    RenderAllLayers();
```

【证据】交换的只是 `ZOrder` 属性，而渲染两处都直接遍历列表顺序：

- 预览：`AnnotationWindow.xaml.cs:482` `foreach (var layer in _annotationManager.Layers.Where(l => l.IsVisible))`
- 输出：`:746` 同上

`AnnotationManager.Layers` 的 getter 是 `_layers.ToArray()`（`AnnotationManager.cs:76`）——**无排序**。全仓 grep `OrderBy` + `ZOrder`：**零命中**。

【后果】点击"上移/下移"后重绘，但绘制顺序未变 ⇒ **视觉与输出均无变化**。按钮是死的。
【触发】必然。

---

### 2.2 【高】有道翻译后端永久失效

【位置】`src/TrueToneCap.App/MainWindow.xaml.cs:1683-1691`

```csharp
var cfg = new LlmConfig
{
    UseCustomLlm = _settings.UseCustomLlm,
    ApiEndpoint = _settings.LlmEndpoint,
    ApiKey      = _settings.LlmApiKey,
    ModelName   = _settings.LlmModel,
    SystemPrompt= _settings.LlmSystemPrompt,
    TargetLanguage = _settings.TargetLanguage
};
```

`LlmConfig` 确实有 `YoudaoAppKey` / `YoudaoAppSecret` 两个属性（`TranslationService.cs:413,416`），但**构造时一个都没赋值**。

于是 `TranslationService.cs:88-89` 的判空必然成立 → 有道分支永不执行 → 静默降级 Google。

【加重】`YoudaoAppKey` 甚至没有进入加密凭据库：`SettingsService.cs:109/140` 只存取了 `YoudaoAppSecret`，AppKey 走的是 JSON 明文 —— 与"密钥用 DPAPI 加密"的设计不一致。

【后果】国内用户配置有道凭据后**完全无效**，且界面无任何失败提示（看到的是 Google 的翻译结果）。
【触发】必然。

---

### 2.3 【高】OCR 引擎"Windows OCR"选项无法持久化

【位置】`src/TrueToneCap.App/Services/SettingsService.cs:212`

```csharp
if (!IsOneOf(s.OcrEngineMode, "OnnxCpu", "OnnxGpu", "Windows", "Acme")) s.OcrEngineMode = "OnnxGpu";
```

而 XAML 实际 Tag（`MainWindow.xaml:325-327`）：

```xml
<ComboBoxItem Tag="OnnxGpu" IsSelected="True">DirectML GPU (推荐)</ComboBoxItem>
<ComboBoxItem Tag="OnnxCpu">ONNX CPU (兼容)</ComboBoxItem>
<ComboBoxItem Tag="WindowsOcr">Windows OCR (系统)</ComboBoxItem>
```

`"WindowsOcr"` 不在白名单 → 每次 `Normalize()` 都被改回 `"OnnxGpu"`。白名单里的 `"Windows"` / `"Acme"` 全仓零引用（`"Acme"` 只在此处出现一次，疑为占位残留）。

【后果】用户选"Windows OCR"→ 重启后回到 DirectML GPU，**且下拉框显示为第一项，用户不会察觉被改过**。
【触发】必然（只要选第三项）。

---

### 2.4 【高】无 D3D 设备丢失处理 —— 驱动重置后永久黑屏

【证据】全仓 grep `DeviceRemovedReason` / `DXGI_ERROR_DEVICE`：**零命中**。

【位置】`HdrCaptureWindow.cs:599,608-612`、`HDRPreviewWindow.cs:278-281,355` —— `Present` / `Map` 失败仅记日志后吞掉，无 `GetDeviceRemovedReason` 检测与设备重建。

【后果】驱动重置（TDR）、显卡切换、远程桌面会话变更、笔记本独显/核显切换后，HDR 预览**永久黑屏**，必须重启应用。对一个 HDR 截图工具，这是核心场景。
【触发】特定条件，但一旦发生即不可自愈。

---

### 2.5 【中高】硬看门狗 3 分钟无条件中断截图

【位置】`SelectionOverlay.xaml.cs:297,301-321`（HdrCaptureWindow.cs:449-463 同款）

```csharp
// ── 硬看门狗：3 分钟后无论什么情况强制退出 ──
_hardWatchdog = new Timer(HardWatchdogTick, null, HardExitMs, Timeout.Infinite);
```

`TouchIdle()` 只重置 1 分钟空闲计时器，**硬看门狗不看用户是否活跃**。到点后 `Finish(Cancel)`，若 5 秒内 UI 未响应则 `Environment.Exit(0)`。

【后果】用户花 3 分钟以上做精细标注 → **全部标注丢失**。更糟的是 `Environment.Exit(0)` 会杀死整个进程，在途编码被截断。
【触发】必然（覆盖层存活满 3 分钟）。

---

### 2.6 【中高】HDR 路径文字标注第二次起失效

【位置】`HdrCaptureWindow.cs:2017,2046-2049`

```csharp
private static nint s_editOrigProc;      // ← static
...
if (s_editOrigProc == 0)                 // ← 只子类化一次
{
    s_editOrigProc = GetWindowLongPtrW(_editHwnd, GWLP_WNDPROC);
    ...
}
```

且 `DestroyEdit:2077` 不还原。

【后果】第一次文字标注正常；第二次起新建的 EDIT 控件没有自定义 `WndProc` → **Enter/Esc 无响应**，文字无法提交、输入框不销毁并挡住截图。`static` 意味着跨 HDR 会话同样只在首次生效。
【触发】必然（同一进程内第二次使用）。

---

### 2.7 【中】马赛克并行数据竞争

【位置】`AnnotationRasterizer.cs:135-158`

```csharp
// 块间互不重叠，按行并行安全          ← 注释是错的
Parallel.For(ly, ry + 1, y =>
{
    int blockY = y / block * block;   // 连续 block 个 y 映射到同一 blockY
    ...
    for (int yy = blockY; yy <= by2; yy++) ...   // 读整个块行
    for (int yy = blockY; yy <= by2; yy++) ...   // 写整个块行
});
```

`y ∈ [ly, ry]` 并行，而 `blockY = y / block * block` 使 `block` 个不同 `y` 得到**同一个块行** ⇒ 多个线程对同一批像素并发"读平均→写回"。读循环与写循环分离，一个线程可能读到另一线程**部分写入**的块。

【后果】马赛克块颜色偶发错误、不可复现。非崩溃，但输出不确定。
【触发】必然（区域高度 > blockSize 时即可交错，是否显现取决于时序）。

---

### 2.8 【中】OCR CPU 路径缺少大图上限保护

【位置】`OnnxOcrEngine.cs:466-487`

`MaxDmlTextureDim` 收紧**只在 `_provider == DirectML` 分支内**（`:480`），CPU 分支直接沿用 `:466` 的结果。

【推算】细长选区 6000×150：短边 150 < 960 → 放大 ratio 取上限 6.0 → `resizeW≈36000, resizeH≈896` → 检测张量 `1×3×896×36000×4B ≈ 387 MB`（`:498`），随后 `:547-548` 每行再分配 2 个 `float[resizeW*3]`（>85KB → LOH）。

【后果】CPU 模式下框选一条细长区域 → 内存暴涨/长时间卡死。
【触发】特定条件（宽高比极端），但选"一行文字"是常见操作，4K 屏上更容易逼近。

---

### 2.9 【中】OCR 引擎 Dispose 释放了自己刚获取的信号量

【位置】`OnnxOcrEngine.cs:1027-1038`

```csharp
try { _inferGate.Wait(TimeSpan.FromSeconds(5)); } catch { }
... _detSession?.Dispose(); _detSession = null; ...
try { _inferGate.Release(); } catch { }   // ← 把门重新打开
```

三个问题：① `Wait` 成功后又 `Release`，等于放行在途调用者，而 session 已为 null → NRE（被 `:424` 吞成"ONNX 异常"）；② 注释写"若获取超时则不应释放"，但 Release **无条件执行**，超时时会抛 `SemaphoreFullException` 再被吞；③ 无 `_disposed` 标志，重复 Dispose 无保护。

---

### 2.10 【中】自启动状态读取恒为 false

【位置】`StartupManager.cs:21,37`

```csharp
// 写：key.SetValue(AppName, $"\"{GetAppPath()}\" --autostart");
// 读：return key?.GetValue(AppName) is string val && val == GetAppPath();
```

写入带引号和 `--autostart` 参数，读取却与**裸路径严格相等** ⇒ 永远不相等。

【后果】启用自启后 `IsEnabled` 恒为 false，日志与 UI 显示"已禁用"，与实际状态相反，误导排查。
【触发】必然。

---

### 2.11 【中】翻译服务 HttpClient 泄漏

【位置】`OcrPreviewWindow.xaml.cs:402` —— 每次翻译 `new TranslationService(...)`，而其内部 `new HttpClient(handler)`（`TranslationService.cs:29`），且该类未实现 `IDisposable`。

【后果】反复翻译会累积 HttpClient 与 TCP 连接（socket 耗尽）。这是 .NET 经典陷阱，官方建议单例复用。

---

### 2.12 【中】设置文件非原子写，单点故障即全量重置

【位置】`SettingsService.cs:102,145`

`File.WriteAllText` 无 tmp+Move（对比 `SecretStore.cs:125-131` **用了**原子写，两处不一致）；加载时整文件一次 `Deserialize`，任一字段类型不匹配即抛 `JsonException` → `:123-127` 整份 `Current = new()`。

【后果】写一半崩溃/断电 → 文件截断 → 下次启动**全部设置**（输出路径、热键、质量、前缀）静默回默认，无备份。

---

## 3. 待核验项（子代理报告，我未逐行确认）

| # | 位置 | 现象 | 影响 |
|---|---|---|---|
| 1 | `SelectionOverlay.xaml.cs:159-225` | GPU 背景创建在 `Activated` 事件内，`_hdrBgWnd` 无条件覆盖旧引用且不释放 | 覆盖层失焦再激活即泄漏一个 D3D 交换链 + 原生窗口 |
| 2 | `HdrCaptureWindow.cs:2149-2187` | 渲染线程 `Join(2000)` 超时后 `Cleanup()` 照常 Dispose | 渲染线程访问已释放 COM → 进程崩溃 |
| 3 | `OnnxOcrEngine.cs:1018` | NMS IoU 阈值 0.15，而注释 `:1001` 声称 0.3 | 相邻长行子框外扩后被整块丢弃 → 漏字 |
| 4 | `OcrService.cs:192` | 行重建与"上一个词"比 Y，而非行首 Y | 斜行/基线抖动把一行切碎 → 错序 |
| 5 | `App.xaml.cs:66` + `AppServices.cs:45` | `InitializeFileLog()` 调用两次，`s_cleanupCts` 直接覆盖未 Cancel 旧 CTS | 第一个后台清理循环永久泄漏 |
| 6 | `SecretStore.cs:77,105` | 解密失败时仍缓存空 dict，后续 Save 会覆盖 `secrets.bin` | 已存 API Key 被永久抹掉（罕见但不可恢复） |
| 7 | `AnnotationLayer.cs:275-281` | `TextLayer.Clone` 未复制 `CachedWidth/CachedHeight` | 撤销/重做后命中区域与背景框尺寸跳变 |
| 8 | `AnnotationRasterizer.cs:40-44` | `HighlightLayer`/`SequenceNumberLayer` 无渲染分支，落入 default 画成红色描边矩形 | 与预览（黄色半透明/圆形序号）不一致 |
| 9 | `AnnotationLayer.cs:96-106` | `CreateMosaic`/`CreateBlur` 返回 `RectangleLayer`，而光栅化按 CLR 类型 switch 且 `IsStrokeEnabled=false` | 产出物在最终图里完全不可见（当前仅测试调用，属埋雷） |
| 10 | `AnnotationRasterizer.cs:267` | `using var device = CanvasDevice.GetSharedDevice()` 释放**进程级共享设备** | 后续文字光栅化可能命中已释放对象，被 catch 吞掉 |
| 11 | `App.xaml.cs:164` + `MainWindow` 构造 | 两处各起 `Task.Run(Initialize)`，`_initialized` 先置位无锁 | 竞态：App 侧不传模型目录，若抢先则正确目录被丢弃 |
| 12 | `Localization.cs:147-150` | OCR 引擎下拉用 `"Auto"/"Gpu"/"Windows"/"Cpu"`，XAML 中均不存在 | 三条本地化静默 no-op，英文界面下恒为中文 |

---

## 4. 对上一轮修复的复审（自我审视）

上一轮我提交了 6 项修复（多显示器捕获、剪贴板位图、HLSL 曲线、色域矩阵、ICC 递归、AVIF 回退路径）。本轮独立重审结论：

- **未发现这 6 项引入新的回归**：构建 0 警告 0 错误，395 断言全绿，相关代码路径逻辑自洽。
- **但有一项仍无法自证**：P0-A（多显示器捕获）—— 本机单显示器，修复后正确性无法验证。**这仍是全项目最高优先级的待验证项。**
- 另需说明：上一轮把 `GetTargetXyzToRgbMatrix` 从 private 改为 internal 以便测试。本轮复查确认该改动未扩大程序集公开面（internal 不对外暴露），可接受。

---

## 5. 为什么这批缺陷全部逃过了 395 条断言

与上一轮的三类盲区并列，本轮暴露的是**第四类、也是更普遍的一类**：

**"状态正确性"盲区** —— 现有断言几乎都是"函数调用后不抛异常 / 返回值非空"，而本轮缺陷全部是"**调用成功了，但状态没变对**"：

| 缺陷 | 为什么断言抓不到 |
|---|---|
| 图层上移无效 | 若有测试，大概会断言"调用 MoveLayer 不抛异常"→ 通过。需要断言的是"渲染顺序确实变了" |
| 有道凭据未注入 | 断言"翻译服务构造成功"→ 通过。需要断言"cfg.YoudaoAppKey 非空" |
| OCR 引擎 Tag | 断言"设置能存能读"→ 通过（存的是被 Normalize 改过的值）。需要断言"读出来的值 == 用户选的值" |
| 自启动读写不对称 | 断言"Set 后注册表有值"→ 通过。需要断言"Set 后 IsEnabled == true"（**写读往返**） |
| 马赛克竞态 | 单线程跑不出，且输出是"看起来像马赛克"的图 |

**可复用的结论**：对任何「设置 / 序列化 / 顺序 / 往返」类功能，应当优先写**往返断言**（round-trip）与**状态变更断言**，而不是"不抛异常"断言。具体建议：

1. **枚举往返**：UI 下拉框的每个 Tag → 存 → 读 → 必须等于原值（能一次性抓住 2.3 类问题）。
2. **读写往返**：`StartupManager.IsEnabled = true` → 读回必须为 true（抓 2.10）。
3. **配置注入完整性**：断言构造 `LlmConfig` 后所有凭据字段非空（抓 2.2）。
4. **顺序敏感断言**：改变 ZOrder 后，断言渲染遍历顺序同步改变（抓 2.1）。

这四条都是低成本、高命中率的断言形态。

---

## 6. 修复建议（按性价比排序）

| 优先级 | 项 | 改动量 | 说明 |
|---|---|---|---|
| 1 | 2.3 OCR 引擎 Tag 白名单 | 1 行 | 改为 `"OnnxCpu","OnnxGpu","WindowsOcr"`，删除 `"Windows"`/`"Acme"` 死值 |
| 2 | 2.1 图层顺序 | ~3 行 | 渲染前 `OrderBy(l => l.ZOrder)`（预览与输出两处），或让 `MoveLayer` 直接交换列表元素 |
| 3 | 2.2 有道凭据注入 | ~4 行 | `LlmConfig` 补 `YoudaoAppKey`/`YoudaoAppSecret`；AppKey 一并纳入 SecretStore |
| 4 | 2.10 自启动读写 | ~2 行 | 读取时兼容引号与参数（或写入时保持一致） |
| 5 | 2.5 看门狗 | ~5 行 | 改为"空闲 3 分钟"而非"存活 3 分钟"；`Environment.Exit` 前先中止在途编码 |
| 6 | 2.6 文字子类化 | ~3 行 | `s_editOrigProc` 改实例字段，DestroyEdit 中还原并置零 |
| 7 | 2.7 马赛克 | ~2 行 | 并行粒度改为按块行（`for (int by = ly; by <= ry; by += block)`），而非按 y |
| 8 | 2.9 Dispose | ~5 行 | 删除 Release，加 `_disposed` 标志 |
| 9 | 2.11 HttpClient | ~10 行 | `TranslationService` 静态单例 HttpClient，或实现 IDisposable 并在窗口关闭时释放 |
| 10 | 2.8 CPU 大图上限 | ~3 行 | 把 `MaxDmlTextureDim` 收紧逻辑提到分支外，CPU 同样受总像素数上限约束 |
| 11 | 2.12 设置原子写 | ~5 行 | 对齐 `SecretStore` 的 tmp+Move 写法 |
| 12 | 2.4 D3D 设备丢失 | 较大 | 需设计重建流程，建议单独立项 |

前 4 项合计约 10 行代码，却能让 4 个"点了没反应/配了没用"的功能真正生效。

---

## 7. 可用性评估更新

结合两轮审查（上一轮主链路 + 本轮子系统的独立性发现）：

| 环节 | 上一轮评级 | 本轮修正 | 依据 |
|---|:---:|:---:|---|
| 主链路（捕获→保存） | A− | A−（P0 已修） | 多显示器/剪贴板待实机确认 |
| **标注** | 未评 | **C** | 上移下移无效、马赛克竞态、3 分钟丢失、HDR 文字二次失效 |
| **OCR** | B− | **C+** | Windows OCR 选不中、CPU 大图风险、NMS/行序问题 |
| **翻译** | 未评 | **C** | 有道永久失效、HttpClient 泄漏 |
| **设置/自启动** | 未评 | **C+** | 三个选项存不住/读不出 |

**综合：C+ ~ B−**。核心截图能力依然扎实，但**"功能存在却不生效"的密度偏高**——这类缺陷比崩溃更消耗信任，因为用户无法判断是自己操作有误还是软件坏了。

---

## 8. 本轮已实施的修复与验证（2026-09-15 同日）

对第 2 章的确认缺陷做了修复。全部经构建 + 门禁实测。

### 修复清单（11 项）

| # | 对应 | 文件 | 改动 |
|---|---|---|---|
| 1 | 2.3 | `SettingsService.cs:212` | OCR 引擎白名单改为 `"OnnxCpu","OnnxGpu","WindowsOcr"`，删除死值 `"Windows"`/`"Acme"` |
| 2 | 2.10 | `StartupManager.cs:21` | 读取时从命令行还原可执行文件路径（兼容引号与 `--autostart` 参数） |
| 3 | 2.2 | `MainWindow.xaml.cs:1683` | `LlmConfig` 补 `YoudaoAppKey` / `YoudaoAppSecret` |
| 4 | 2.12 | `SettingsService.cs:145` | 设置写入改为 tmp + `File.Move` 原子写（对齐 `SecretStore`） |
| 5 | 2.1 | `AnnotationWindow.xaml.cs:482,746` | 渲染（预览与输出）均改为 `.OrderBy(l => l.ZOrder)` |
| 6 | 2.7 | `AnnotationRasterizer.cs:135` | 马赛克并行粒度由"每个 y"改为"每个块行"，消除数据竞争 |
| 7 | 2.5 | `SelectionOverlay.xaml.cs:324`、`HdrCaptureWindow.cs:2082` | `TouchIdle()` 中一并重置硬看门狗 ⇒ 语义变为"最后一次交互后 3 分钟" |
| 8 | 2.6 | `HdrCaptureWindow.cs:2046,2077` | 文字框每次创建都子类化，`DestroyEdit` 还原并清零 |
| 9 | 2.9 | `OnnxOcrEngine.cs:1027,342` | `Dispose` **不再** `Release`（保持门关闭）；新增 `_disposed` 拦截新请求 |
| 10 | 2.8 | `OnnxOcrEngine.cs:485` | 新增 CPU 路径总像素上限（12M），细长选区不再申请数百 MB 张量 |
| 11 | 2.11 | `TranslationService.cs:16` | `HttpClient` 改为进程级静态共享 |

### 新增断言（unit 92 → 99，共 +7）

| 组 | 断言 |
|---|---|
| 设置白名单 vs XAML Tag | 解析 XAML `OcrEngineCbo` 的 Tag 与 `IsOneOf` 白名单，断言两者**集合相等**（3 条） |
| LlmConfig 凭据 | 构造块内必须出现 `YoudaoAppKey` / `YoudaoAppSecret`（2 条） |
| StartupManager | 写入含 `--autostart`；读取**不得**再与裸路径严格相等（2 条） |

> 测试工程只引用 Core、不引用 App，故这三条采用**源码级契约断言**（与 HLSL 一致性同思路）。

### 实测结果

```
构建：0 个警告 / 0 个错误
门禁：7 套件 402 断言全部通过（unit 99）
```

### 反向验证

把第 1 项的白名单**改回错误写法**（`"Windows"/"Acme"`）后重跑：

```
RESULT suite=--unit-tests passed=98 failed=1
  ❌ 设置白名单[OnnxCpu,OnnxGpu,Windows,Acme] == XAML Tag[OnnxGpu,OnnxCpu,WindowsOcr]
```

**1 条断言精确命中**，零误伤 → 断言确实能捕获该缺陷。还原后全绿。

### 未修（需决策或改动较大）

- 第 3 章「待核验」的 12 项：尚未逐行确认，未改动。其中 `#1 覆盖层 Activated 泄漏 HDR 窗口`、`#2 渲染线程 Join 超时后仍释放` 两条若属实均为高危，建议优先核实。
- 2.2 的附带问题：`YoudaoAppKey` 仍走 JSON 明文（未进 SecretStore）。AppKey 是应用标识而非密钥，风险低于 Secret，但与设计不一致；迁移需处理旧配置，故留待决定。

---

## 9. D3D 设备丢失处理（2.4，2026-09-15 实现）

### 问题回顾

全仓 `DeviceRemovedReason` / `DXGI_ERROR_DEVICE` **零命中**。GPU 驱动重置（TDR）、显卡切换、远程桌面会话变更之后，设备被标记为已移除，此后 `Present` / `Map` 全部失败 —— 但窗口不报错，只是**永久黑屏**，`RenderCore` 的 catch 每帧刷一行日志，用户只能杀进程重开。

### 实现：三层结构

| 层 | 位置 | 职责 |
|---|---|---|
| **判定层** | 新增 `src/TrueToneCap.Core/Processing/D3DDeviceLost.cs` | 纯函数，无 COM 副作用，**可单测** |
| **检测层** | `HdrCaptureWindow.RenderCore` | 异常通道 + 主动通道双检测 |
| **恢复层** | `HdrCaptureWindow.TryRebuildDeviceResources` | 原地重建，保留窗口与帧数据 |

**判定层**（放在 Core 是为了可测——测试工程只引用 Core）：

- `IsDeviceLostHResult(int)`：`DEVICE_REMOVED` / `DEVICE_RESET` / `DRIVER_INTERNAL_ERROR` / `D3DDDIERR_DEVICEREMOVED` 判为丢失。
  刻意**不**纳入 `E_OUTOFMEMORY`——显存不足应通过缩小资源恢复，与"设备物理消失"性质不同，混判会导致过度重建。
- `IsDeviceLostException(Exception)`：遍历内部异常链（Vortice 等封装常见）。
- `IsDeviceRemoved(ID3D11Device)`：主动查询 `DeviceRemovedReason`。某些驱动下 `Present` 会**静默失败**（不抛异常、画面不更新），只有主动查询能暴露。

**检测层**（双通道，任一命中即触发）：

```
RenderCore 入口：每 30 帧（约 0.5s）主动探测 IsDeviceRemoved
RenderCore catch：异常 HRESULT 判定 + 设备状态查询
```

**恢复层**：`_device` / `_ctx` 去掉 `readonly` → 释放全部 D3D 资源 → 重建设备/交换链/RTV/着色器/采样器 → `_hasFrame = false`。

> 关键简化：`_frame`（原始帧像素）在窗口内保留，而 `RenderCore` 在 `_hasFrame == false` 时会自动 `UploadFrame`。因此重建后**无需手动重传帧**，渲染循环下一帧自动恢复画面。

**防护**：`_deviceLost` 标志去重（避免每帧重复触发）、`MaxDeviceLostRecovery = 2` 上限（连续失败说明驱动层面有问题，停止自愈并给出明确日志）。

### 验证

```
构建：0 个警告 / 0 个错误
测试：8 套件 440 断言全绿（unit 99 → 116，新增 17 条）
```

新增断言覆盖：HRESULT 常量值本身（写错一位即失效且极难发现）、四类丢失码判定、`E_OUTOFMEMORY`/`INVALID_CALL`/普通异常/`null` **不误判**、异常链穿透、名称映射。

### ⚠️ 必须说明的局限

- **重建路径本身无法在本环境实机验证** —— 触发真实的设备丢失需要 TDR 或驱动重置。已验证的是：判定语义正确、编译通过、现有行为无回归。
- `TryRebuildDeviceResources` 中的创建参数必须与 `Initialize` 保持一致（交换链格式、缓冲数、常量缓冲 5584 字节等）。这两处是**人工同步**关系，已在方法注释中警示——设备丢失是罕见路径，自动化测试覆盖不到，不同步只会在真机 TDR 时才暴露。
- 本次只接入了 `HdrCaptureWindow`。`HDRPreviewWindow` 有同类问题（同样零设备丢失处理），但它生命周期短，优先级较低，未改动。

---

## 10. 待核验项的核实与修复（2026-09-15 追加）

对第 3 章「待核验」清单中标注为高危的两条（#1、#2）及三条用户可见项做了核实，结论如下。

### 已修复（4 项）

| # | 核实结论 | 文件 | 改动 |
|---|---|---|---|
| 1 | **属实且高危**。`_bgRendered` 只保护 CPU 分支（`:250`），GPU 分支（`:159-225`）无任何保护；`Activated` 在窗口失焦再回来时重复触发，无条件 `new HdrPreviewWindow` 覆盖 `_hdrBgWnd`，旧窗口与交换链从不释放 | `SelectionOverlay.xaml.cs:159` | 回调开头加 `_bgRendered` 拦截（仍执行置顶），GPU 呈现成功后置位 |
| 2 | **属实且高危，且与设备丢失直接耦合**。`Join(2000)` 超时后 `Cleanup()` 照常 Dispose 全部 D3D 资源；若线程卡在 `Present`/`Map`（正是设备丢失的典型表现）→ use-after-free → **不可捕获的 AV，进程直接死** | `HdrCaptureWindow.cs:2380,2396,2420` | 新增 `_renderThreadStuck`：Join 失败则跳过全部 D3D 释放（泄漏优于崩溃） |
| 11 | **属实**。`App.xaml.cs:164` 调 `Initialize()` 不传目录，而 `MainWindow:245` 传 `ResolveModelDir()`；`_initialized` 先置位短路 ⇒ 若前者抢先，模型目录永久为空，ONNX 引擎加载失败、只剩 Windows OCR 兜底 | `App.xaml.cs:164` | 两处统一走 `ResolveModelDir()` |
| 12 | **属实**。本地化用 `"Auto"/"Gpu"/"Windows"/"Cpu"`，XAML 实际 Tag 是 `"OnnxGpu"/"OnnxCpu"/"WindowsOcr"` ⇒ 三条 `SetComboItemText` 全部静默 no-op | `MainWindow.Localization.cs:147` | 改为正确 Tag，移除 XAML 中不存在的 `"Auto"` |

### 刻意未改（1 项）

**#3 NMS 阈值**：注释写 `IoU > 0.3`，代码是 `iou > 0.15f`（`OnnxOcrEngine.cs:1043`），确实不一致。

但**只修正注释，不改动数值**。理由：该阈值是启发式参数，调大会漏字、调小会重复识别，改动必须以 OCR 准确率基准实测为依据（当前 CPU 约 91.5%），而本轮没有跑基准。凭直觉调参违反项目"无实测依据不落地改动"的约定，风险高于收益。已在注释中写明这一点与基准入口。

### 验证

```
构建：0 个警告 / 0 个错误
测试：8 套件 440 断言全绿
```

### 追加修复：#10 CanvasDevice 共享设备被释放（4 处）

**属实**。`CanvasDevice.GetSharedDevice()` 返回的是**进程级共享**设备，所有权属于 Win2D，调用方不应 Dispose。而全仓 4 处写成 `using var device = GetSharedDevice()`，在方法结束时释放了不属于自己的对象；后续调用便可能命中已释放设备，异常被上层 `catch` 吞掉 ⇒ 表现为"文字图层只剩背景框、没有字"。

修复：`AnnotationRasterizer` 新增懒加载的 `SharedCanvasDevice` 属性（**永不释放**），4 处调用改为引用它：

| 位置 | 原写法 |
|---|---|
| `AnnotationRasterizer.cs:276` | `using var device = GetSharedDevice()` |
| `HdrCaptureWindow.cs:910` | `using (var device = ...) { ... }`（保留块，去掉 using） |
| `HdrCaptureWindow.cs:1645` | `using var device = GetSharedDevice()` |
| `HdrCaptureWindow.cs:1725` | `using var device = GetSharedDevice()` |

采用懒加载而非静态字段直接初始化，是为了保持"首次用到才创建"的时机，且创建失败时由调用方 `try/catch` 处理，不会升级成 `TypeInitializationException`。

### 剩余待核验（6 项，未改动）

`#4 Windows OCR 行重建基准漂移`、`#5 LogService 双初始化 CTS 泄漏`、`#6 SecretStore 解密失败污染缓存`、`#7 TextLayer.Clone 丢缓存尺寸`、`#8 Highlight/SequenceNumber 无渲染分支`、`#9 CreateMosaic 产出物不可见`。

其中 `#9`（`CreateMosaic`/`CreateBlur` 返回 `RectangleLayer`，而光栅化按 CLR 类型 switch）当前仅测试调用、生产路径未触达，属埋雷性质；`#5` LogService 双初始化若属实会泄漏一个后台清理循环，建议下一轮优先核实。

---

## 11. 追加优化：NVENC 小图失败导致大图永久退软件（本地发布暴露）

### 现象

本地发布后的产物自测（`--selftest`，使用 96×96 测试图）暴露：

```
[NVENC] InitEncoder hr=0x00000008
        lastErr=Frame dimensions are less than the minimum supported value.
[NativeEncoderGuard] ⚠️ NVENC 崩溃隔离 [InvalidOperationException] ... 96x96 ...
```

表面看只是"多了一次注定失败的尝试"。但追下去发现真正的问题严重得多。

### 真正的问题：失败缓存被污染

`FormatEncoders.cs:598` 原本**无条件**执行 `s_nvencFailed = true`：

```
一张小图失败 → s_nvencFailed = true → 后续所有 AVIF 编码永久退回 libaom
```

而 libaom 与 NVENC 的差距是量级性的 —— **4K AVIF：硬件约 0.12s vs 软件约 22s（约 180 倍）**。用户此后每次保存 AVIF 都会慢两个数量级，却只会觉得"软件变慢了"，**完全无从察觉原因，只能靠重启恢复**。

失败原因其实分两类，被混为一谈了：

| 类别 | 例子 | 正确处理 |
|---|---|---|
| ① 硬件/驱动不可用 | 初始化失败、`NO_ENCODE_CAPABILITY`、设备丢失 | 缓存并永久禁用（避免每张图白试） |
| ② **本次输入不适用** | 尺寸低于最小支持、该分辨率不支持 | **只针对这张图**降级，不污染全局状态 |

### 两项优化

1. **最小尺寸前置判断**（`EncodeAsync` 入口）：`w < 128 || h < 128` → 直接走软件路径。
   省掉一次注定失败的会话创建（创建+初始化并不便宜），也从源头杜绝污染。
   阈值取 128 而非实测的 96 临界值，留有余量（精确下限随驱动/显卡代次而异）。
2. **失败分类**：新增 `IsInputSpecificNvencFailure`，遍历异常链匹配驱动的尺寸/分辨率类错误特征；
   只有非"输入不适用"的失败才设 `s_nvencFailed`。
   未匹配到已知特征时**按"硬件不可用"处理** —— 保守方向是禁用，避免每张图都白试一次。

### 验证

新增 6 条断言（unit 116 → 122），覆盖：尺寸类错误识别、嵌套异常穿透、创建实例失败/无编码能力（应判为硬件不可用）、`null` 与空消息边界。

```
构建：0 个警告 / 0 个错误
测试：8 套件 446 断言全绿
```

> 这个缺陷只有**真正把发布产物跑起来**才会暴露 —— 门禁全绿时它一直存在。这也是"本地出包 + 产物自测"不可省略的直接证据。

---

## 12. 同类问题专项复查（"一次失败 → 全局永久退化"模式）

修完 NVENC 小图问题后我意识到，它的本质是**进程级失败缓存被"本次输入不适用"污染**。于是以此为题对全仓做了一次专项排查。

### 已修（4 项）

| 位置 | 问题 | 处理 |
|---|---|---|
| `AvifHardwareProbe` 64×64 样张 | **由本次修复引入的交互问题**。样张 64 < `MinNvencDimension`(128)，NVENC 被前置判断跳过、实际走软件回退，但输出文件依然有效 ⇒ 探针得出"NVENC 可用"的**假绿**：对外报告硬件可用，实际从不使用 | 样张改 256×256；加 `Debug.Assert` 把样张尺寸与下限绑定；`MinNvencDimension` 改 internal 供引用 |
| `NativeLibraryResolver` | 提取失败**也**置 `_initialized=true`（永不重试），且只写 `Debug.WriteLine`（Release 下完全无声）⇒ avifenc/cjxl/cwebp/cjpegli 提取失败会让 AVIF/JPEG XL/WebP/JPEG LI **四种格式静默失效** | 新增 `LastError` 与 `Reset()`；App 层把失败提升到 `LogService.Warn`（UI 日志可见） |
| `WgcCaptureService.cs:688` | Float16 会话启动失败即写 `s_hdrCapabilityCache=false`，而该缓存**进程内无重置路径** ⇒ 一次瞬时失败（D3D 设备忙、分辨率切换中、显存紧张）就让该显示器**永久走 SDR**，HDR 与广色域静默降级 | 会话启动失败不再写能力缓存（能力缓存只应记录"能力"层面判定），并补上 `session.Dispose()`（原实现泄漏） |
| `FormatEncoders` `s_texturePathFailed` | 无条件缓存 ⇒ 一次失败后**所有**图都不再尝试 GPU 纹理直通（可省一次 CPU 回读） | 复用 `IsInputSpecificNvencFailure` 做与 `s_nvencFailed` 一致的分类 |

### 未修（记录待办）

- **各 native 编码器的 `static bool? _available` 粘滞负缓存**：探测时 `WaitForExit(5000)` 超时会抛 `InvalidOperationException`，被 `catch { _available = false; }` 吞掉 —— 一次冷启动或杀软扫描导致的 5 秒超时，即**永久判死该格式**。建议区分"超时"与"确实不可用"，超时允许重试。
- **`MultiOcrService.Initialize` 先置位后干活且无锁**：并发调用时第二个线程可能拿到"已初始化但引擎尚未注册"的中间态。

### 处理得当（勿在重构中破坏）

- `EncoderCrashSentinel`：跨进程降级**有** UI 提示（`MainWindow.Settings.cs:103`）与 `ClearAll()` 恢复入口 —— 这是"降级标志"应有的样子。
- `ColorProfileProvider:76`：失败时不缓存 null，保留重试机会，另有 `InvalidateCache()`。
- `HdrCaptureWindow._deviceLost`：重建成功即复位，且有 `MaxDeviceLostRecovery` 上限与 `LogService.Error`。

### 验证

```
构建：0 个警告 / 0 个错误
测试：7 套件 425 断言全绿
```

### 提炼为一条通用判据

> 凡要写入全局降级标志前，先问：**这次失败是"环境/能力不可用"，还是"本次输入不适用"？**
> 前者可以缓存（避免反复白试），后者绝不能缓存 —— 否则一次偶发就会退化整个进程，
> 而且用户通常只能靠重启恢复，期间无任何提示。

---

## 13. 需实机验证

- **多显示器捕获**（上一轮 P0-A 修复后）—— 本机单显示器，最高优先级
- 剪贴板位图能否粘贴到微信/Word
- HDR 预览暗环是否消失
- 设备丢失后是否真的黑屏（可用远程桌面或驱动重置复现）
- 标注 3 分钟被中断的实际观感
