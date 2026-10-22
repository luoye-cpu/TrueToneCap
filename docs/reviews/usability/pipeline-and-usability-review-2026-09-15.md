# TrueToneCap 管线完整审查与可用性综合评估

- 审查日期：2026-09-15
- 审查范围：**运行时管线全链路**（捕获 → 色彩 → 色调映射 → 编码 → 输出）＋ **端到端可用性**
- 与 `pipeline-review-2026-09-15.md` 的分工：该报告覆盖构建/依赖/CI/发布/测试治理，本报告聚焦**用户实际拿到的功能是否成立**
- 方法：四路并行深读 + 对每条高严重度发现**逐条读码复核**（子代理结论未经复核不写入本报告）
- 代码基线：本地工作区（v0.3.4-beta 开发态）

---

## 0. 执行摘要

**一句话结论：软件的"技术底座"是扎实的（构建 0 警告 0 错误、368 断言全绿、历史崩溃坑全部修对），但"用户价值链路"上存在 4 个会导致核心功能不成立或静默出错的缺陷 —— 其中最严重的一条会让多显示器用户截到错误的屏幕。**

本次最重大的发现是一个**此前所有审查均未触及的功能性缺陷**：

> **多显示器环境下，无论截取哪个显示器，实际捕获到的永远是主显示器内容。**

该问题不报错、不提示、测试套件无覆盖（单显示器环境跑不出来），属"用户一用副屏就会发现"的级别。

### 可用性总体评级：**B−（可用，但存在关键缺口）**

软件不是"能不能跑"的问题，而是"在不同用户场景下是否给出正确结果"的问题。分场景看差异极大：

| 用户场景 | 评级 | 判断 |
|---|:---:|---|
| 单显示器 + SDR 截图 + 存文件 | **A−** | 主链路完整，体验良好 |
| 单显示器 + HDR 截图 | **B** | 可用，但预览存在已确认的暗环伪影且与输出不一致 |
| **多显示器用户** | **D** | 核心功能失效（P0-A） |
| **依赖 Ctrl+V 粘贴的用户** | **D** | 最高频诉求未满足（P0-B） |
| 广色域 / 色彩准确场景 | **C** | P3 / DCI-P3 静默偏色（P0-D） |
| OCR 使用 | **B−** | 中文可用、韩文不可用；存在模型目录竞态 |

**发布建议：建议在 v0.3.4 正式发布前修复 4 个 P0（A/B/C/D），尤其是 P0-A 与 P0-B。**

---

## 1. 实测基线（本次实际执行）

```
=========== BUILD (Release, solution) ===========
    0 个警告 / 0 个错误          已用时间 00:00:20.95
[1.6/2] 发布门禁回归            ✅ 通过（子进程隔离）
[2/2] 测试门禁（7 套件）        61s

  ✅ --unit-tests       65 通过 / 0 失败
  ✅ --color-tests      93 通过 / 0 失败
  ✅ --encoding-tests   14 通过 / 0 失败
  ✅ --service-tests     4 通过 / 0 失败 / 1 跳过
  ✅ --ocr-tests        10 通过 / 0 失败
  ✅ --format-tests     30 通过 / 0 失败
  ✅ --usability-tests 152 通过 / 0 失败 / 1 跳过
  ────────────────────────────────────────────
     共 368 断言，全部通过，exit=0
```

判读：

- 工程健康度良好，且 unit 套件已从 47 增长到 65（含新增的崩溃哨兵回归断言）。
- **但"门禁全绿"与"功能正确"之间没有蕴含关系**。本报告所有 P0 均在门禁全绿的情况下成立 —— 这恰恰说明当前测试覆盖存在结构性盲区（见第 6 章）。

---

## 2. 管线全景

```
[用户按 Ctrl+Shift+S]
        │
        ▼
┌─ ① 捕获 ────────────────────────────────────────────┐
│ HotkeyManager → MainWindow.SilentCapture()          │
│   → DisplayInfo.GetMonitorUnderCursor()  ← 得到 hmonitor
│   → WgcCaptureService.CaptureMonitorAsync(hmonitor)  │
│       └─ CreateItemForMonitor(hmonitor)  ⚠P0-A       │
│            └─ OnFrameArrived → GPU→CPU 回读          │
│ 产物：SDR byte[] BGRA8  或  HDR float[] scRGB        │
└──────────────────────────────────────────────────────┘
        │
        ▼
┌─ ② 色彩 / 色调映射 ─────────────────────────────────┐
│ ColorProfileProvider：ICC 检测、色域矩阵             │
│   ⚠P0-D  P3/DCI-P3 矩阵匹配失败                      │
│   ⚠P0-E  ICC 生成失败静默回退 sRGB                   │
│   ⚠P0-F  递归路径可能栈溢出                          │
│ ToneMapper（CPU）  y>1 → 1.0   ✅ 已修暗环           │
│ ToneMapping.hlsl（GPU 预览） 旧曲线 ⚠P0-C           │
└──────────────────────────────────────────────────────┘
        │
        ▼
┌─ ③ 编码 ────────────────────────────────────────────┐
│ EncoderFactory.Create(format)                       │
│   PNG/TIFF  → 托管编码器                             │
│   JPEG LI/XL/AVIF/WebP → 子进程 native exe           │
│   AVIF → NVENC(进程内) → libaom → ⚠PNG(P1-1 路径错) │
└──────────────────────────────────────────────────────┘
        │
        ▼
┌─ ④ 输出 ────────────────────────────────────────────┐
│ BuildOutputPath → MyPictures\TrueToneCap             │
│   ⚠P1-4 无重名检测（毫秒戳，并发覆盖）              │
│ 复制到剪贴板 → ⚠P0-B 仅 SetStorageItems，无位图     │
│ Toast 通知                                           │
└──────────────────────────────────────────────────────┘
        │
        ▼
┌─ ⑤ 后处理 ──────────────────────────────────────────┐
│ OCR：MultiOcrService → OnnxOcrEngine / Windows OCR   │
│   ⚠P1-3 模型目录竞态                                 │
│ 翻译：Youdao → Google → LLM                          │
└──────────────────────────────────────────────────────┘
```

---

## 3. P0 — 核心功能不成立 / 用户可见严重缺陷

### P0-A 多显示器捕获恒取主显示器内容（本次最重大发现）

【位置】`src/TrueToneCap.App/Services/WgcCaptureService.cs:1172-1186`、调用点 `:1100-1113`

【现象】`CreateItemFromDisplayId(nint hmonitor)` **完全忽略入参 `hmonitor`**：

```csharp
private static Windows.Graphics.DisplayId? s_cachedDisplayId;
private static GraphicsCaptureItem? CreateItemFromDisplayId(nint hmonitor)
{
    if (s_cachedDisplayId is null)
    {
        var primaryArea = Microsoft.UI.Windowing.DisplayArea.Primary;   // ← 恒取主显示器
        if (primaryArea is null) return null;
        s_cachedDisplayId = new Windows.Graphics.DisplayId(primaryArea.DisplayId.Value);
    }
    return GraphicsCaptureItem.TryCreateFromDisplayId(s_cachedDisplayId.Value);
}
```

而它被列在 `CreateItemForMonitor` 的**方法0（最高优先级）**：

```csharp
// 方法0: TryCreateFromDisplayId
var item = CreateItemFromDisplayId(hmonitor);
if (item is not null) { ...; return item; }     // ← 成功即返回
// 方法1: CsWinRT CreateForMonitor(hmonitor)     ← 真正按 hmonitor，永不执行
// 方法2: RoGetActivationFactory + vtable        ← 同上
```

【触发链】`PooledSession` 构造器保存 `_hmonitor`（`:104,152`）→ `Start()` 调 `CreateItemForMonitor(_hmonitor)`（`:169`）→ 方法0 命中 → 返回主显示器 item。

【后果】

1. **多显示器用户截副屏，得到的是主屏画面**；
2. "捕获全部显示器拼接"模式 → 拼接出 N 份相同的主屏内容；
3. `s_cachedDisplayId` 是 static 且 `InvalidateSessions`（`:732-746`）**未清空它** → 热插拔/换主显示器后继续错；
4. **全程无日志、无提示、无异常**，用户只能自己发现"图不对"。

【为何测试没抓到】门禁跑在单显示器环境，`hmonitor` 恰好就是主显示器 → 行为正确。**这是典型的"环境盲区"，不是代码覆盖率问题。**

【修复建议】把真正按 `hmonitor` 的 `CreateForMonitor` 提为方法0，`DisplayId` 路径降为回退且必须**由 hmonitor 反查对应 DisplayId**（不能取 Primary）；`s_cachedDisplayId` 改为按 `hmonitor` 字典缓存并在 `InvalidateSessions` 中清空。若本机为单显示器无法验证，此项**必须交实机多显示器验证**。

---

### P0-B 剪贴板只放文件对象，不放位图 —— Ctrl+V 无法粘贴

【位置】`src/TrueToneCap.App/MainWindow.xaml.cs:1024-1038`、`:1068-1071`

【现象】两个复制路径都只写 `SetStorageItems`：

```csharp
var file = await StorageFile.GetFileFromPathAsync(path);
var dp = new DataPackage();
dp.SetStorageItems(new[] { file });          // ← 只有"文件"
Clipboard.SetContent(dp);
```

【后果】用户截完图按 Ctrl+V：

| 目标 | 结果 |
|---|---|
| 微信 / QQ / 钉钉 聊天框 | ❌ 无反应（不接受文件对象） |
| Word / 画图 / Photoshop | ❌ 无反应 |
| 网页富文本输入框 | ❌ 无反应 |
| 资源管理器 / 桌面 | ✅ 粘贴为文件 |
| 支持"粘贴为文件"的应用 | ✅ |

对截图工具而言这是**最高频诉求**。用户默认心智是"截图 → 粘贴"，当前实现只在少数场景成立。

【加重情节】`catch {}`（`:1034-1037`）吞掉一切异常，复制失败时用户完全无感知。

【修复建议】补位图通道：

```csharp
dp.SetStorageItems(new[] { file });
dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));  // 追加
```

并给 catch 分支加 `LogService.Warn` + 状态栏区分文案（"✅ 已保存（复制失败）"）。
注意：HDR/AVIF/JXL 等格式系统剪贴板不一定识别，建议为剪贴板路径单独产出一份 PNG 副本以保证兼容（现默认输出是 JPEG LI，兼容性虽好但仍建议显式 PNG）。

---

### P0-C HLSL 与 CPU 色调映射曲线漂移 —— 预览带已确认的暗环伪影

【位置】`src/TrueToneCap.App/Shaders/ToneMapping.hlsl:49-62` vs `src/TrueToneCap.Core/Processing/ToneMapper.cs:236-246`

【现象】CPU 侧已于 2026-08-30 修复暗环，改为三重约束下的唯一解：

```csharp
// ToneMapper.cs:236-246
if (y <= 1.0f) return Math.Clamp(y, 0f, 1f);
_ = headroom; _ = eps;
return 1.0f;                    // ← 高光单调饱和
```

而 HLSL 仍是**旧算法**：

```hlsl
// ToneMapping.hlsl:49-62
float rY = (1.0f + y / h2) / (1.0f + y) * y;   // ← 旧 Reinhard
if (y < 1.0f + eps) {
    float t = (y - 1.0f) / eps;
    float s = t * t * (3.0f - 2.0f * t);
    return (1.0f - s) + s * rY;                 // ← 非单调，暗环来源
}
return rY;
```

【这不是死代码】`HdrCaptureWindow.cs:346` 真实加载 `ToneMapping.hlsl.cso` 用于 HDR 选区窗口渲染。（注：`GpuToneMapper` 类本身确实零调用，但 HdrCaptureWindow 绕过它直连 cso，故问题真实生效。）

【后果】

1. **HDR 预览窗口仍在渲染已确认有缺陷的曲线** —— 高光边缘暗环伪影（降幅可达 42%）；
2. **预览与实际保存不一致**：预览走 GPU 旧曲线，保存走 CPU 新曲线 → 同一张图两副模样；
3. `ToneMapping.hlsl:47` 注释自称"与 CPU 融合内核一致"，**与实际相反**，会误导后来者。

【修复建议】HLSL 对齐 CPU：

```hlsl
float SegmentedReinhardMap(float y, float headroom)
{
    if (y <= 1.0f) return saturate(y);
    return 1.0f;    // 与 ToneMapper.cs 一致：三重约束下的唯一解
}
```

并加**一致性回归断言**：对同一组采样点（尤其 y=1.0 附近 ±0.05），比对 CPU 与 HLSL 输出（可用 CPU 侧模拟 HLSL 公式做数值比对），防止再次漂移。

---

### P0-D Display P3 / DCI-P3 色域矩阵匹配失败 —— 静默偏色

【位置】`src/TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs:691-721`

【现象】匹配用 `ToUpperInvariant()`，但 case 常量带空格/连字符：

```csharp
return colorSpace.ToUpperInvariant() switch
{
    "P3" or "DISPLAY P3" or "DCI-P3" => new float[] { /* P3 矩阵 */ },
    ...
    _ => null
};
```

而系统实际流通的标签是 **`"DisplayP3"` / `"DCI_P3"`**（证据：`MainWindow.ColorManagement.cs:88,101-102,114,121`、`PngCheck.cs:21`、`ColorProfileProvider.cs:771,845,852,1116-1117`）：

| 实际值 | ToUpperInvariant | case 常量 | 匹配 |
|---|---|---|---|
| `DisplayP3` | `DISPLAYP3` | `DISPLAY P3` | ❌（空格） |
| `DCI_P3` | `DCI_P3` | `DCI-P3` | ❌（连字符） |

⇒ 返回 `null`。

【后果】拿不到 XYZ→RGB 矩阵 → 像素**直通不转换**，但 ICC 仍按 P3 嵌入 → **像素内容与色彩声明矛盾** → 在色彩管理应用里显示偏色。用户选了 P3、界面也提示"scRGB→P3 矩阵"，实际什么都没做，**无任何提示**。

【修复建议】

```csharp
static string Norm(string s) => s.ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
// "P3" or "DISPLAYP3" or "DCIP3" => ...
```

并加**穷尽性断言**：遍历 UI 色域下拉框所有 Tag，断言每个都能解析出非 null 矩阵。这正是能防住本类缺陷的测试形态。

---

### P0-E ICC 生成失败静默回退 sRGB

【位置】`src/TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs` `PatchSrgbPrimaries` / `IccStore.cs:44,60,76`

【现象】为 P3 / AdobeRGB 打补丁生成 ICC 失败时，静默回退 sRGB ICC。

【后果】用户明确选择了 AdobeRGB / Display P3，最终文件里嵌的却是 sRGB 描述文件 —— 色彩管理应用按 sRGB 解释广色域像素，**输出与用户意图完全相反**，且无任何提示。对一个以"色彩准确"为核心卖点的工具，这是信任层面的问题。

【修复建议】失败时向上层返回明确状态，由 UI 层给出"⚠️ 目标色彩空间不可用，已回退 sRGB"提示；或在设置面板提前探测并置灰不可用选项。

---

### P0-F ICC 兜底路径存在无限递归

【位置】`ColorProfileProvider.cs:164`（`GetDefaultSRgbIcc` → `BuildValidSRgbIcc` → `IccStore.SRGB` → `GetDefaultSRgbIcc`）+ `IccStore.cs:26`

【后果】系统 sRGB ICC 缺失（少数精简系统/损坏安装）时栈溢出，进程直接崩溃。

【修复建议】打破循环：`BuildValidSRgbIcc` 使用**硬编码的基础 sRGB 字节数组**而非再次调用 `GetDefaultSRgbIcc`；或加 `ThreadStatic` 递归哨兵。

---

## 4. P1 — 应修（用户可见，影响面较窄或有条件触发）

| # | 位置 | 现象 | 后果 | 建议 |
|---|---|---|---|---|
| 1 | `FormatEncoders.cs:698-699` vs `CapturePipelineService.cs:350/397` | AVIF 末端回退写 `.png`，但函数仍返回原 `.avif` 路径 | Toast/剪贴板指向**不存在的文件**，用户以为保存成功 | 回退后返回**实际写入的路径** |
| 2 | `SettingsService.cs:152` + 9 处调用点 | `SaveQuiet` 空 catch | 装到 Program Files（只读）时**所有设置重启即回默认**，用户无感知 | 保存失败须显式提示，并引导改存 `%LOCALAPPDATA%` |
| 3 | `MainWindow.xaml.cs:1585-1588` vs `OnnxOcrEngine.cs:218-233` | OCR 路径硬编码 `%LOCALAPPDATA%\...\onnx_models`，绕过 `ResolveModelDir()` 优先级；`MultiOcrService.Initialize` 有 `_initialized` 短路（`:50-51`） | 若 OCR 抢在启动后台初始化之前 → **永久使用错误目录** → ONNX 引擎不可用 → 静默降级为 Windows OCR | 统一走 `ResolveModelDir()`；或让 `Initialize` 记录并校验已用目录 |
| 4 | `CapturePipelineService.cs:276` | 文件名毫秒时间戳，**无重名检测** | 连拍/多显示器并发保存时**直接覆盖** | 加 `(1)/(2)` 递增 |
| 5 | `NativeJxlEncoder.cs:142/215`、`JpegLiNative.cs:156`、`NativeAvifEncoder.cs:228` | 只 `WaitForExit(30s/60s)` **不 Kill** | 编码卡死时子进程泄漏 | 超时后 `Kill(entireProcessTree: true)` |
| 6 | `MainWindow.xaml.cs:830,879,890` | 无感截图只锁捕获阶段，编码转后台，**并发无上限**；CT 不传子进程 | 连按热键 → N 个 cjxl/avifenc 并行，4K 下资源争抢；取消无效 | 加并发信号量；超时 Kill（同 #5） |
| 7 | `SelectionOverlay.xaml.cs:301-321` | 3 分钟硬看门狗 `Environment.Exit(0)` | 强杀在途编码 → **输出文件截断** | 退出前等待/中止在途任务 |
| 8 | `MainWindow.xaml.cs:160` | 未勾选"最小化到托盘"时直接 `Environment.Exit(0)`，绕过 `OnWindowClosed` | 不等 `_pendingEncodeTasks` → 末张图可能截断 | 统一走 `OnWindowClosed` |
| 9 | `NativeAvifEncoder.cs:101` | SDR 路径未传 `--depth` | 设置里 `BitDepthAvif=10` **对 SDR 完全无效**，恒 8-bit | 补 `--depth`，或在 UI 上说明该设置仅 HDR 生效 |
| 10 | `ManagedPngEncoder.cs:108` | 请求 10/12-bit 时 `sBIT` 恒写 8 | 容器升到 16-bit 但精度未提升 → **体积翻倍、零收益** | 要么真实提升精度，要么限制选项 |
| 11 | `MainWindow.xaml` 114 处硬编码中文 vs `Localization.cs:25-190` 仅覆盖 41 处；Tag 不匹配（`OcrEngineCbo` 用 "Auto/Gpu" 实际是 "OnnxGpu"；`GainMapModeCbo` 用 "Rgb" 实际是 "RGB"） | 中英切换后大量界面仍为中文；部分下拉框本地化不生效 | 英文用户体验割裂 | 补齐映射 + 修正 Tag 大小写 |
| 12 | `SelectionOverlay.xaml.cs:236-240,458-462` | 用**单一** `_dpiScale` 换算全部坐标 | 多显示器不同缩放比时**选区偏移** | 按所在显示器取 DPI（需实机验证） |
| 13 | `NativeLibraryResolver.cs:104-107` | native 释放失败仅 `Debug.WriteLine`，`_initialized=true` 仍置位 | 装到 Program Files 时 AccessDenied → AVIF/JXL/WebP/JpegLi **全部失效且永不重试** | 失败须提示；释放目录优先 `%LOCALAPPDATA%` |
| 14 | `WgcCaptureService.cs:590` vs `:106` | 换显示器即 Dispose D3D 设备，但 `PooledSession._d3dDevice` 仍持指针 | 多显示器场景**必现**悬垂设备 | Dispose 时同步清空所有 session 的设备引用 |
| 15 | `WgcCaptureService.cs:753/1014` + `MainWindow.xaml.cs:857` | 捕获接口内部全同步、无 `Task.Run`，在 UI 线程 await | 最长阻塞约 6s | 捕获段包 `Task.Run` |
| 16 | `WgcCaptureService.cs:427-446/495-513` | 15s 空闲窗口内持久会话**每帧** GPU→CPU 回读 | 4K 每帧约 132 MB 回读（代码注释自述） | 空闲期停止回读，按需取帧 |
| 17 | `TrayIconManager.cs:97,162-165` | `Shell_NotifyIconW` 返回值未检查；菜单仅 3 项且硬编码中文 | 托盘创建失败静默；功能入口缺失 | 检查返回值 + 失败降级提示 |
| 18 | `CapturePipelineService.cs:280-302` | `Directory.CreateDirectory` 与归档路径构造无异常处理 | 路径非法/无权限/磁盘满时异常直抛到通用 catch，仅显示原始异常文本 | 预检 + 友好提示 |

---

## 5. P2 — 建议改进

- **无首次运行引导**：无欢迎页、无权限说明、无依赖自检。缺 OCR 模型时仅 `Debug.WriteLine`；程序在托盘时用户**零感知**。建议加一次性的"环境自检"面板（模型/native/HDR/编码器可用性一目了然）。
- **Toast 注册失败无提示**（`ToastService.cs:36-40`）→ 通知永久消失且用户不知情。
- **日志写入失败静默**（`LogService.cs:282`）→ UI 显示有日志、磁盘上没有。
- **主窗口无 `MinWidth`/`MinHeight`** → 可缩到内容不可读。
- **WebP 回退是死码**：`FormatEncoders.cs:233` 只 catch `DllNotFoundException`，而 `NativeWebPEncoder.cs:99` 实际抛 `InvalidOperationException` → 真实失败不触发回退。
- **TIFF 未写 `ExtraSamples`(338)**（`ManagedTiffEncoder.cs:98`）→ 部分解码器误判第 4 通道。
- **死代码/无效开关**：`ShowPreview` 无消费方（实际由 `ToastOnCapture` 控制）；`OnCaptureNow` 无 XAML 绑定；录制功能（`AnimationRecorder`）无 UI 入口；`TryEncodeAsync`（`NativeEncoderGuard.cs:117`）零调用 → 编码超时保护形同虚设；BMP 枚举不存在但 `ManagedBmpEncoder` 与本地化文案仍在。
- **空 `catch {}` 约 70+ 处**（MainWindow 22、LogService 7、App 9 等）。建议建立"禁止空 catch"的代码分析规则，至少要求 `LogService.Warn`。
- **设置文件无原子写**（`SettingsService.cs:145`）→ 写一半崩溃会损坏配置。

---

## 6. 为什么这些问题没被测试抓到（结构性盲区）

这是本次审查最值得反思的一点：**368 断言全绿，但 4 个 P0 全部成立**。根因有三类：

1. **环境盲区**：P0-A 多显示器在单显示器 CI/本机上行为完全正确。**这类缺陷无法靠增加断言解决，只能靠"多环境实机矩阵"覆盖。**
2. **契约盲区**：P0-D 的矩阵匹配失败是"字符串常量与另一处枚举不同步"。缺的是**穷尽性断言**（"UI 每个色域 Tag 都能解析出矩阵"），而非更多用例。
3. **跨层一致性盲区**：P0-C 是 CPU 与 HLSL 两份实现漂移。缺的是**跨实现比对断言**。

建议补充三类"防漂移"测试（成本低、防的是本报告这类缺陷）：

| 测试 | 断言内容 |
|---|---|
| 色域 Tag 穷尽性 | 遍历 UI 色域下拉框全部 Tag → 每个都能解析出非 null 矩阵 |
| CPU/GPU 曲线一致性 | 对 y ∈ [0, 4] 采样（y=1 附近加密）→ CPU 与 HLSL 公式输出逐点一致 |
| 编码器路径契约 | 每种格式编码后 → 返回值 `File.Exists` 为真且后缀与声明格式一致（直接防 P1-1） |

---

## 7. 修复路线图

### 第一阶段 —— 发布阻塞（建议 v0.3.4 正式版前完成）

1. **P0-A 多显示器捕获**：把 `CreateForMonitor(hmonitor)` 提为首选；DisplayId 按 hmonitor 反查；缓存按显示器分区并在 `InvalidateSessions` 清空。**需实机多显示器验证。**
2. **P0-B 剪贴板位图**：`SetBitmap` + `SetStorageItems` 双写；失败显式提示。
3. **P0-C HLSL 曲线对齐**：HLSL 改为 `y>1 → 1.0`，加 CPU/GPU 一致性断言。
4. **P0-D 色域矩阵匹配**：归一化比较，加 Tag 穷尽性断言。

> 这四项改动量都不大（每项约 5–30 行），但对可用性的影响远大于任何新功能。

### 第二阶段 —— 让失败可见（1 周内）

5. **P0-E ICC 静默降级** + **P0-F 递归破裂**。
6. **P1-1 回退路径返回真实文件** + **P1-2 设置保存失败提示** + **P1-3 OCR 模型目录统一**。
7. **P1-5/6 超时 Kill + 并发上限**（防进程泄漏与资源争抢）。
8. 建立"禁止空 catch"约定，把 `LogService.cs:282`、`ToastService.cs:36`、`NativeLibraryResolver.cs:104` 等高危静默点优先处理。

### 第三阶段 —— 体验与一致性

9. P1-11 国际化补齐（含 Tag 大小写修正）。
10. P1-12 多 DPI 选区校正（需实机）。
11. P1-9/10 位深设置与实际行为对齐（要么生效，要么从 UI 移除）。
12. 新增**首次运行环境自检面板**（模型 / native 工具 / HDR / 编码器可用性）。

### 第四阶段 —— 承接上一份报告的未决项

13. 构建治理层原子提交（props / CPM / NuGet.config / global.json / 锁文件 / ci.yml）。
14. CI release workflow + `--selftest`（已在 `pipeline-review-2026-09-15.md` 第五轮实现，待实跑验证）。
15. 预发布 RC 依赖（DI `11.0.0-rc.1`）是否回退 LTS 的产品决策。

---

## 8. 需要实机验证的事项（本环境无法确认）

| 事项 | 原因 |
|---|---|
| **多显示器捕获正确性**（P0-A 修复后） | 本机单显示器，修复前无法复现、修复后无法验证 |
| 剪贴板位图能否粘贴到微信/Word | 需真实应用交互 |
| HDR 预览暗环是否肉眼可见 | 需 HDR 显示器 + 高光场景 |
| P3/AdobeRGB 输出在色彩管理软件中的实际观感 | 需校色环境 |
| 多显示器不同 DPI 选区偏移（P1-12） | 需混合 DPI 环境 |
| 热键与第三方软件冲突的实际表现 | 需真实软件环境 |
| native 工具被杀软拦截后的表现（P1-13） | 需带杀软环境 |

---

## 9. 已验证为良好（勿在后续重构中破坏）

这些是本次**逐条读码确认**的正确实现：

**数值与算法**
- ✅ 色域矩阵数值正确：抽查 `SrgbToBt2020`（0.627403/0.329283/0.043313）与 ITU-R BT.2087 一致；`SrgbToDisplayP3`（0.822462/0.177194/0.000344）与 Apple 官方矩阵偏差 < 4e-4；XYZ→sRGB（3.2404542/-1.5371385/-0.4985314）与 AdobeRGB（2.0413690/-0.5649464/-0.3446944）均正确。
- ✅ PQ 编码正确：`LinearToPQ` 中 `nits = lin × 80`、`/10000` 符合 SMPTE ST 2084。
- ✅ 位深量化与左对齐正确，clamp 齐备，字节序（大端）正确 —— **未发现溢出/截断**。
- ✅ `ToneMapper` 的 `y>1 → 1.0` **不是缺陷**（三重约束下唯一解，`DisplayMaxNits` 在 HDR/GainMap 路径正确使用）—— 复核后维持原结论。

**工程实践**
- ✅ 三个历史崩溃坑全部修对：`AsTensor` use-after-free、DML 16384 纹理上限、DML `EnableMemoryPattern=false`。
- ✅ 全仓 0 处 `async void`；色调映射用 LUT 替代 `MathF.Pow`。
- ✅ 临时文件全部 `Guid` 随机名 + `finally` 清理（安全与清理都到位）。
- ✅ 崩溃哨兵（`EncoderCrashSentinel`）设计务实：用"崩溃残留 → 下次禁用"把不可恢复的硬崩溃降级为可自愈路径，这是对"进程内 CSE 无法拦截"约束下的正确取舍。
- ✅ WGC 帧状态原子发布 + 双缓冲；`FrameArrived` 不直接触碰 UI。
- ✅ `PooledSession.Dispose` 等在途回调 2s + Drain 等待设计扎实。

**历史容器中已修的正确实现**（勿回退）：AVIF box 大端、`colr` 入 `ipco`+`ipma`、`av1C` 布局、cICP matrix 0→9/1、TIFF IFD 12 条目布局、HDR 不嵌 sRGB-TRC ICC、AdobeRGB 不写 cICP。

---

## 10. 后续：本轮已实施的修复与验证（2026-09-15 同日）

审查完成后，对**判据明确、且能在本环境验证**的项立即做了修复。以下全部经构建 + 门禁实测。

### 修复清单

| # | 问题 | 文件 | 改动 |
|---|---|---|---|
| 1 | **P0-A** 多显示器恒取主屏 | `WgcCaptureService.cs` | 把按 `hmonitor` 定位的 `CreateForMonitor` 提为方法0；**删除**恒取 `DisplayArea.Primary` 且静态缓存的 `CreateItemFromDisplayId`（DisplayId 无法由 hmonitor 可靠反查，保留只会被再次选中） |
| 2 | **P0-B** 剪贴板无位图 | `MainWindow.xaml.cs` | 两处复制路径补 `SetBitmap` 双写；空 `catch` 改为写日志 + 状态栏区分文案（"已保存（复制到剪贴板失败）"） |
| 3 | **P0-C** HLSL 曲线漂移 | `Shaders/ToneMapping.hlsl` | `SegmentedReinhardMap` 对齐 CPU（`y>1 → 1.0`）；顺带修正"dxc ps_6_0"过时注释（实际是 fxc ps_5_0） |
| 4 | **P0-D** P3 矩阵失配 | `ColorProfileProvider.cs` | 新增 `NormalizeColorSpaceKey`（忽略大小写/空格/连字符/下划线）；矩阵匹配改走归一化；方法改 internal 以便穷尽性测试 |
| 5 | **P0-F** ICC 无限递归 | `ColorProfileProvider.cs` | `BuildValidSRgbIcc` 加线程静态重入哨兵，重入时直接落到手写最小 ICC |
| 6 | **P1-1** 回退路径不一致 | `FormatEncoders.cs` + `CapturePipelineService.cs` | `FormatHelper` 新增 `RecordFallbackPath` / `ResolveActualPath`；AVIF 保底写 PNG 后登记，管线 3 处返回**真实落盘**路径 |

### 新增防漂移断言（unit 65 → 92，共 +27）

| 组 | 断言 |
|---|---|
| 色域 Tag 穷尽性 | 5 个真实 Tag 均可解析矩阵 + 3 种等价写法 + 未知色域须返回 null（**9 条**） |
| 归一化函数 | 去空格 / 去连字符 / 去下划线 / 大小写无关 / 空串安全（**5 条**） |
| 色调映射三重约束 | 单调不减、输出 ∈[0,1]、y≤1 恒等、f(1)=1、高光饱和（**5 条**） |
| HLSL/CPU 一致性 | HLSL 函数体内已无 `y / h2`、无 smoothstep、含 `return 1.0f`、保留直通分支（**4 条**） |
| 回退路径校正 | 未登记原样返回 / 登记后校正 / 一次性消费 / 文件不存在时返回原路径（**4 条**） |

### 实测结果

```
构建：0 个警告 / 0 个错误
  ✅ --unit-tests       92 通过 / 0 失败
  ✅ --color-tests      93 通过 / 0 失败
  ✅ --encoding-tests   14 通过 / 0 失败
  ✅ --service-tests     4 通过 / 0 失败 / 1 跳过
  ✅ --ocr-tests        10 通过 / 0 失败
  ✅ --format-tests     30 通过 / 0 失败
  ✅ --usability-tests 152 通过 / 0 失败 / 1 跳过
  共 395 断言，全部通过
```

### 反向验证（证明断言不是恒真）

把 P0-D 的 case **改回错误写法**（`"DISPLAY P3"` / `"DCI-P3"`）后重跑：

```
RESULT suite=--unit-tests passed=87 failed=5
  ❌ 色域矩阵覆盖: DisplayP3 可解析
  ❌ 色域矩阵覆盖: DCI_P3 可解析
  ❌ 色域矩阵覆盖: 'Display P3'(空格) 等价
  ❌ 色域矩阵覆盖: 'DCI-P3'(连字符) 等价
  ❌ 色域矩阵覆盖: 小写 'displayp3' 等价
```

5 条**精确命中**色域矩阵断言，无一条误伤 → 断言确实能捕获该缺陷。随后还原，重跑全绿。

> 附带发现：归一化后，旧 case 常量（含空格/连字符）是**任何形式都匹配不上**的死分支——因为它们自身带分隔符，而归一化后的输入必然不含分隔符。

### 断言自身的一次修正

初版把"SDR 白点保真"写成对 `y ∈ [0.999, 1.001]` 断言 `f(y) == y`。但 `y>1` 时按设计 `f(y)=1.0 ≠ y` → **误报一次**。修正为两条独立断言：`y≤1` 恒等映射、`f(1)=1`。

教训：**断言的采样区间必须与被验证性质的定义域一致**——把"白点保真"这个点性质误写成区间性质，就会越过定义域边界而误报。

### ⚠️ 需实机验证（本环境无法确认）

| 项 | 原因 |
|---|---|
| **P0-A 多显示器捕获** | 本机单显示器：修复前无法复现、修复后无法验证。这是唯一无法自证的一项，**优先级最高** |
| P0-B 剪贴板位图 | 需真实验证能否粘贴到微信 / QQ / Word / 画图 |
| P0-C HDR 预览 | 需 HDR 显示器确认暗环消失、预览与保存结果一致 |

> 按项目规则：**所有改动需等本地实机验证完成后再提交 git。** 本轮改动尚未 commit。

---

## 附：本次审查方法

- 四路并行深读（捕获 / 色彩 / 编码输出 / UI 交互），随后对**每一条 P0 逐一读码复核**：直接读源码、grep 验证调用点与实际取值、对比 CPU 与 HLSL 实现。
- 实测执行 Release 全量构建与 7 套件门禁（结果见第 1 章）。
- 未修改任何源码；未执行 commit / push（遵守项目铁律）。
- 子代理报告中若干结论经复核后**未采纳**（如"GPU 色调映射是死代码"—— 实为 `HdrCaptureWindow` 绕开 `GpuToneMapper` 直连 cso，问题真实生效；如"OCR 恒用错模型目录"—— 实为竞态而非必然）。
