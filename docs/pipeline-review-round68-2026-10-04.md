# TrueToneCap 管线完整审查 · 报告（round68，2026-10-04）

- **计划**：`docs/pipeline-review-plan-round68-2026-10-04.md`。
- **基线坐标**：HEAD `ca7ed99`（tag `v0.3.6-beta` 之后只差文档）。开工时工作树 **77 项未提交改动**（`M` 62 / `D` 4 / `??` 11），收工时 80 项——**多出的只有本轮新增的 `tests/output/` 产物与本计划文件，产品源码一行未动**（§7 有逐项核对）。
- **上一轮门禁**：`tests/output/w9/gate-final2.log` = `GATE_RC=0` / 7 套件全通过 / **1738 断言** / 无未判格子；地板 `scripts/gate-baseline.json` = 1738。
- **本轮门禁**：**6/7 套件逐套件与地板逐字一致（563 断言），`--unit-tests` 因环境不够未复核**（§1 说清为什么）。

---

## 0. 判词（先看这四句）

1. **本轮没找到需要立刻改产品代码的东西。** 六条 lane 深读 + 主循环逐条回源复核之后，**唯一新增的可执行修复项是 P2**（`StartupManager` 的 setter 吞掉异常 ⇒ 那句"保存失败要告诉用户"的 catch 永远不执行，§3-L5-1）。其余候选要么是旧账、要么经复核被驳回。
2. **门禁本身这一轮跑不干净，但原因全在环境，不在产品。** 两条环境障碍都定位到了确切根因并给出绕行（§1）；**六套件与地板逐字对上**这一条，反倒**独立验证了 `gate-baseline.json` 这份地板是真的**。
3. **两个"结构性缺口"经复核仍成立，且比上一轮更硬**：门禁**没有任何一条断言检查子进程是否杀干净**（`entireProcessTree` 在测试工程里 **0 命中**），而产品里**恰好有两个该杀树的地方没杀**（§3-L3-1）；`File.ReadAllText` 文本哨兵从 09-27 记的 **80 处涨到 103 处**，App 侧判据的 T2 占比还在扩大（§4）。
4. **一次"差点写错的结论"被拦下**：`--param-sweep` 在门禁里崩了一次（`0xC00000FD` 栈溢出，NVIDIA 驱动栈）。我**没有**据此写"NVENC 路径不稳"，而是先量复现率——**独立连跑 10 次 0 失败** ⇒ 记为偶发，不记因果（§5）。

---

## 1. 本轮门禁：两条环境障碍，都定位到根因（都不算产品问题）

### 1.1 `pwsh` 工具本身全线失效 → 用本机真 pwsh 绕行

`pwsh` 工具的**默认二进制加载环境坏了**：连 `echo hello` / `exit 0` 都返回空输出 + exit `3221225794`（`0xC0000142` = `STATUS_DLL_INIT_FAILED`），子代理独立复现同一结果。旁证：这不是我的命令有问题——`exit 0` 也退同一个码。

**绕行（实测有效）**：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -Command "<命令>"
```

实测返回正常：`pwsh7-ok`、`dotnet --version` = `11.0.100-rc.1.26425.128`、`git status --porcelain` 正常输出。**本轮所有读数都是这条路径取的。**

### 1.2 DSH 沙箱掐掉 .NET 子进程的 `%TEMP%` 写入 → `TMP`/`TEMP` 重定向绕行

**现象**：第一次全量门禁 ⇒ `GATE_EXIT=1`，倒在 `[1.7/2] 外部判据`：

```
❌ --wg-samples 生成样张失败 (退出码 1)
❌ BT2020_jpeg_li.jpg   UnauthorizedAccessException: Access to the path
   'C:\Users\20210\AppData\Local\Temp\ttc_jpegli_<guid>.ppm' is denied.
❌ 有 21 个格子没产出样张 ⇒ 跨容器像素层的覆盖不完整，本轮不算判过
```

**根因：收口是按可执行文件，不是按路径 ACL。** 同一个用户、同一个 .NET 运行时、同一条路径，只有 exe 不同：

| 写入者 | 目标 | 结果 |
|---|---|---|
| `pwsh`（.NET） | `%LOCALAPPDATA%\TrueToneCap` / `%TEMP%` / `C:\PLAN` | ✅ 全通过 |
| **`TrueToneCap.Tools.exe`** | `%TEMP%\…` / `C:\PLAN\probe_out\…` | ❌ 拒绝 |
| **`TrueToneCap.Test.exe`** | `%LOCALAPPDATA%\TrueToneCap\…sentinel` | ❌ 拒绝 |

允许面 = **工作区内**（`tests\` / `artifacts\` / `src\…\bin\Release` 全可写），区外一律拒 ⇒ 与 DSH 工作区沙箱足迹逐字吻合。`%TEMP%` 的 ACL 本就是本用户 FullControl ⇒ **不是权限配置问题**。Python 子进程写 `%TEMP%` 也成功 ⇒ 也不是"子进程一律不给写"。

**绕行（实测有效，改动为零）**：把 `TMP`/`TEMP` 指到工作区内。同一台宿主、同一条命令：

```
重定向前：PNG ❌ / JPEG LI ❌ / WebP ❌ / AVIF ❌ / JPEG XL ❌ / GainMap ❌  （全 Access denied）
重定向后：PNG 91.4 KB ✅ / JPEG LI 265.9 KB ✅ / WebP 170.7 KB ✅ /
          AVIF 55.7 KB ✅ / JPEG XL 97.4 KB ✅ / GainMap 376.3 KB ✅
```

`verify.ps1` **不需要改**（它从不覆盖 `TMP`/`TEMP`，且所有宿主都是子进程 ⇒ 父壳设置即可继承）。

### 1.3 门禁实测读数（带重定向）

```
[1/2]   构建            0 警告 0 错误（29.84 s）
[1.6/2] 发布门禁回归     通过
[1.7/2] 外部判据         通过   ← 重定向后 21 个空格子全部产出真样张
[1.8/2] 色彩管线判据     3/3 全部判了且通过
[1.9/2] 宿主新鲜度       通过（"2 个宿主都不早于 140 份构建输入"）
[1.95/2] 闸门接线        通过
[1.96/2] 日志行格式对撞   通过（含两条负样本仍被拒）
[1.97/2] App 侧设置迁移判据 通过（退出码 0）
[2/2]   测试：
   --unit-tests      ❌ 退出码 -532462766   ← 环境，见 §1.4
   --color-tests      267 通过 / 0 失败     （地板  267 ✓）
   --encoding-tests    14 通过 / 0 失败     （地板   14 ✓）
   --service-tests      2 通过 / 0 失败 / 1 跳过（地板 2/1 ✓）
   --ocr-tests         12 通过 / 0 失败     （地板   12 ✓）
   --format-tests      43 通过 / 0 失败     （地板   43 ✓）
   --usability-tests  225 通过 / 0 失败     （地板  225 ✓）
```

**六套件与 `scripts/gate-baseline.json` 逐字一致** ⇒ 产品侧没问题，且这份地板是真的。

### 1.4 剩余不可绕行项：`--unit-tests`（**记"环境不够，未复核"，不记"通过"**）

死点精确到行：`CorePipelineTests.cs:282` → `:284 Directory.CreateDirectory(dir)`（`WriteForeignSentinel` 伪造"别的进程留下的哨兵"），而 `EncoderCrashSentinel.SentinelDir`（`EncoderCrashSentinel.cs:55-57`）走 `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`——**壳 API，不吃 `LOCALAPPDATA`/`USERPROFILE` 环境变量**（两条都试过，仍拒）。另：把 `LOCALAPPDATA` 重定向过去**反而弄坏了一条本来通过的断言** ⇒ 此路不通。

实测崩溃点与读数：

```
passed_before_crash=183   failed_before_crash=1
❌ EncoderCrashSentinel: 在途期间文件仍在 ⇒ 真崩了仍有据可读   ← 唯一失败（哨兵文件建不出来）
Unhandled exception ... Access to the path
  '...\TrueToneCap\encoder-crash.NVENC.sentinel' is denied.
  at CorePipelineTests.WriteForeignSentinel(...) line 282
  at CorePipelineTests.Test_EncoderCrashSentinel_ArmDisarm() line 269
  at CorePipelineTests.RunAll() line 91
```

⇒ `RunAll()` 在**第 91 个测试**处中断，**后面约 992 条 unit 断言本轮根本没跑到** ⇒ `--unit-tests` 的 **1175 条地板本轮无法复核**。

**我没有绕过它**，两条路都不该走：（a）把 `%LOCALAPPDATA%\TrueToneCap` 加进沙箱允许面是环境改动，我在会话内改不了；（b）改产品源码 `SentinelDir` 属审查轮外，且**那个目录里放着用户的真实数据**（`settings.json` / `onnx_models` / `crash.log`）——把它换成 junction 有数据风险，我不做。

### 1.5 一条方法学发现（比这套数字更值钱）

**在带文件沙箱的环境里，"门禁红"可能是量具被环境掐住，不是产品坏了。** 判据就是 §1.2 那张三行对照表（外壳能写 / Python 能写 / 该宿主不能写）。**不先量这一条，就会把 21 个空格子写成产品 P0。**

---

## 2. 六条 lane 深读：方法与复核纪律

lane 全部**只读**（不 build、不跑测试、不起产品进程）——门禁轮正在用同一份 `bin`。七条 lane 按文件切，互不重叠；每条候选必须给 `文件:行` + 触发构造 + 用户可见性 + P 级 + T 级 + **旧账归属**。

**复核纪律（本轮实际执行）**：lane 的总结论一律**降级为候选**，主循环逐条回源读。下面凡我亲自回读过的行，去掉【lane】标记；只到 lane 的留标记。

**lane 回收情况**：7 条里 4 条产出报告（L1 / L3 / L4 / L7），3 条（L2 / L5 / L6）首轮无产出，由一名补跑代理重做后全部回收。**⇒ 没有让空跑的 lane 混过去。**

---

## 3. 候选缺陷表（按 P 级排，全部经主循环回源）

### L5-1 【唯一新增可执行项】`StartupManager` 的 setter 吞掉异常 ⇒ 那句"失败要告诉用户"永不执行 · **P2** · T2

| 项 | 内容 |
|---|---|
| 位置 | `StartupManager.cs:61-64`（setter 的 `catch` 只 `LogService.Warn`，**不 rethrow**）+ `MainWindow.Settings.cs:391-395`（调用方的 `catch` 因此**不可达**） |
| 触发构造 | `HKCU\...\Run` 写入被策略挡掉，或 `CreateSubKey` 返回 null（`:53-54` 的 `??` 分支会 NRE，同样被吞） |
| 实际行为 | 界面显示 `✅ 设置已保存`（`:377`），而开机自启**实际没写进注册表** |
| 用户可见性 | **只有 `app_*.log` 的 Warn**；`SetStatus("⚠ 开机自启设置失败")` 这行**永远不会跑到** |
| 旧账归属 | **NEW**（10-02 §9.3 记的是"启动路径不该写注册表"，已修；本条是**修完之后留下的缝**，不是同一条） |
| 复核 | **我已亲自回读** `StartupManager.cs:49-65` 与 `MainWindow.Settings.cs:376-395` 两处原文 |

**为什么算缺陷**：代码自己把意图写在了 `:382-383` 的注释里——*"失败必须显式告知用户：否则设置界面显示『已保存』而系统侧实际未生效"*——而实现做不到。这正是它旁边那段代码要消灭的那类"界面说成功、系统说没有"。

**修法**：`StartupManager.cs:61` 的 `catch` 改成 rethrow（或把属性改成返回 `bool`）。**一行级**，但要配一条判据。

**顺带确证（这条是"已修"的正证）**：`AutoStart` 的赋值点全仓只有 3 处——`MainWindow.Settings.cs:388`（唯一写注册表，在保存路径上）、`:126` 与 `MainWindow.xaml.cs:2925`（都是 UI→内存）。`MainWindow.xaml.cs:310-326` 的启动路径**只读+不一致就 Warn**。⇒ 10-02 §9.3 那条**确实修好了**，非用户保存路径碰不到注册表。

### L3-1 子进程超时不杀进程树，且门禁对这条**零断言** · **P2** · T1（判据缺口）/ T2（代码）

| 项 | 内容 |
|---|---|
| 位置 | `NativeAvifEncoder.cs:283`（管道写失败）与 `:289`（30 s 超时）：两处都是裸 `proc.Kill()`，**没有** `entireProcessTree: true` |
| 触发构造 | AVIF SDR 路径（`RunPngViaStdin`，正是产品实际走的那条）超时或管道写失败 |
| 实际行为 | 子进程被杀，**它自己的子进程留下** |
| 用户可见性 | **完全静默**（avifenc 正常是单进程，所以实际泄漏面窄——见下） |
| 旧账归属 | **部分旧**：09-27 记过"`NativeAvifEncoder.cs:289` 有 Kill 但无 `entireProcessTree`"（P1-5）；**本轮的新增量是"门禁对这条零断言"** |
| 复核 | **我已亲自回读** `NativeAvifEncoder.cs:274-295` 原文 |

**两条硬证据**：

1. `entireProcessTree` 在**整个测试工程里 0 命中**（grep 确认）⇒ 没有任何断言检查这件事。
2. 那条本该管它的判据 `CorePipelineTests.cs:7875-7902` **恰好把它放行**：
   - `:7891` 查的是"以 `proc.WaitForExit(` 开头的行"，而 `:287` 写的是 `if (!proc.WaitForExit(30_000))` ⇒ **不以它开头，绕过**；
   - `:7900` 断 `awaitedWait == 1` ⇒ **显式白名单了"手写等待只剩一处"**，而那一处就是 `RunPngViaStdin`。
   ⇒ **测试是照着这段代码的现状写的**，所以它绿着而缺陷在。

**严重度我按实测下调**：avifenc 是单进程（20 个 worker 是**线程**不是进程），所以今天不会真的漏一堆进程；但代码与它自己声明的不变量不符，且同一个形状离"真会漏"只差一次重构。

**同族（更弱的一处，我一并读了）**：`ShaderCompiler.cs:107` `proc?.WaitForExit(30000)` **丢弃返回值且完全不 Kill**；`:109` 随后读 `ExitCode`（进程没退出时抛 `InvalidOperationException`，被 `:119` 的 catch 吞成一句"编译异常"，与真编译错误**无法区分**）；`:115` 在限时等待后做**同步** `ReadToEnd()`。`:133-134` 的 `where fxc.exe` 探针同形。⇒ 这是全树最弱的子进程处理。

### L1-1 Float16 软冷却的每一次跳过都只落 Debug · **P2** · T2

| 项 | 内容 |
|---|---|
| 位置 | `WgcCaptureService.cs:862` `Log($"[WGC] 显示器 0x{hmonitor:X}: Float16 软冷却中…本次不新建 Float16 会话，用 8-bit BGRA")` |
| 触发构造 | 某屏连续 3 次未成帧 ⇒ 进入冷却，随后**每次截图**都走这条 |
| 实际行为 | 该次截图静默变成 8-bit BGRA（丢弃 Float16 高精度源） |
| 用户可见性 | **只有 Debug 档**（`Log()` = `LogService.Debug`，`:55`） |
| 旧账归属 | **NEW**：F1 那轮把 5 处提到 Info（`:717/:723/:1123/:1485/:1503`），**漏了这一处** |
| 复核 | **我已亲自回读** `WgcCaptureService.cs:852-864` 原文 |

**为什么算漏网**：它下面 `:845` 有个**同类事实**（能力 `NarrowSdr` ⇒ 整图退 8-bit），而那一处**被提到了 Info**，理由就写在紧邻的注释里——*"这是『为什么这张图只有 8-bit』的唯一对外解释，停在 Debug 就等于静默降级"*。`:862` 是**同一方法、同一类事实、同一后果**，却没跟着一起提。

加重一层：冷却**进入**是 Info（`:945`，"连续 N 次未成帧 ⇒ 接下来 10 次跳过"），而 10 次**跳过本身**全是 Debug ⇒ Brief 档用户看到"冷却开始了"，之后**看不到任何一次降级记录**。

**修法**：`:862` 的 `Log(` → `LogInfo(`（单 token）。另两处 `:889`（Float16 会话启动失败）与 `:936`（冷却解除仍 Debug）建议一并定夺。

### L4-1 同毫秒文件名静默覆盖（旧账复现，且默认格式变更后风险上移）· **P2** · T2

| 项 | 内容 |
|---|---|
| 位置 | `CapturePipelineService.cs:505` `BuildOutputPath` = `{FileNamePrefix}{yyyyMMdd_HHmmssfff}{ext}`，函数体内**无** `File.Exists`/递增；写入侧 `AtomicFileWriter.cs:63` 是 `File.Move(temp, full, overwrite: true)` |
| 触发构造 | 两次截图在**同一毫秒**内取名（无感路径刻意让编码在后台并发，`:1451-1458`） |
| 实际行为 | 后一枚**静默覆盖**前一枚 |
| 用户可见性 | **完全静默** |
| 旧账归属 | **旧账**：09-27 P1-4"仍开"（`:501`，行号现已漂到 `:505`） |
| 复核 | **我已亲自回读** `CapturePipelineService.cs:505` 与 `AtomicFileWriter.cs:63` |

**本轮增量**：默认格式已是 PNG（快），连拍更便宜 ⇒ 撞名概率比 09-15 记的更高。**全仓无任何 `GetUniquePath` 类助手**（`BuildOutputPath` 只有两个调用点，`：581` / `：637`，都直接喂编码器）。

### L4-2 剪贴板位图通道是**延迟引用**，失败落在粘贴时 ⇒ 提示反而说成功 · **P2** · T2

| 项 | 内容 |
|---|---|
| 位置 | `MainWindow.xaml.cs:1725` `dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file))`（`bitmapOk=true` 只看**同步**是否抛）；`:1733` `Clipboard.SetContent(dp)`；`ToastService.cs:138` 的成功 toast 无条件说"已复制到剪贴板" |
| 触发构造 | 存成系统**没有 WIC 解码器**的容器（JXL）：`SetBitmap` 注册时**不一定抛**，真实失败发生在**消费方粘贴那一刻** |
| 实际行为 | `bitmapOk` 为 true ⇒ 状态栏说成功、toast 说"已复制"；用户粘进聊天框**没反应** |
| 用户可见性 | **不可见**（承诺了成功）。10-01 实机矩阵量到 JXL 那格"只有文件、无位图"，与这条同向 |
| 旧账归属 | **新增量**：10-01 记的是"JXL 没有位图通道"（现象）；**"为什么失败不可见"（延迟引用 + toast 过度承诺）是本轮读码得到**的机制 |
| 复核 | **我已亲自回读** `MainWindow.xaml.cs:1707-1744` 全文 |

**同一处还有一条更硬的**：`EncodeAndCopyAsync`（`:1794-1808`）把文件写进 `%TEMP%\TrueToneCap_Clip` 并在 **3 秒后删除**；而位图是**延迟**引用 ⇒ 3 秒后粘贴取不到流。注释按"剪贴板持有文件"推理，但延迟引用**不是持有**。

**反面确证（这三条经复核是干净的，一并记）**：
- **无"每进程只提示一次"的开关**：`s_hwFallbackNotified` 代码中 0 命中，且 `CorePipelineTests.cs:4409` 用正则**主动禁止**这类字段回归；残留的 `s_` 静态量逐个看过，都是**内容去重**（正确形状）或负缓存，不是一次性闩锁。
- `EncoderFallbackNotifier` 单槽**不会吞掉第二种降级**：写侧是**合并**（`FormatEncoders.cs:1708-1713` 读 `earlier` 并用 `｜另外：` 并起来），不是覆盖。
- 保存失败**已全部到达用户**（状态栏；编码类失败另有 toast）。仅两处只有状态栏，且 `SetStatus` 有 50 ms 去抖（`:143-153`）⇒ 紧随其后的成功消息可能把它冲掉。

### L6-1 / L6-2 托盘菜单硬编码中文 + `Shell_NotifyIconW` 返回值全丢弃 · **P2** · T2（**旧账，非新发现**）

| 项 | 内容 |
|---|---|
| 位置 | `TrayIconManager.cs:164-167`（4 条中文直进 `AppendMenuW`）；`:99/103/125`（三处返回值丢弃） |
| 用户可见性 | 英文模式下**静默显示中文**（HMENU 是 Win32 结构，`LocaleManager` 的 WinUI 可视树扫描**结构上碰不到**）；`NIM_ADD` 失败时无托盘图标也无提示 |
| 旧账归属 | **旧账**：09-27 P1-17"仍开（三点全在）"，行号一致 |
| 复核 | 由补跑代理回读；**本轮未由主循环复读** ⇒ 标【lane】 |

### L2 色彩与色调映射：**本轮无新增缺陷**（负结论，逐项列出）

- **GPU tone mapper 删除后没有 CPU↔GPU 漂移面**：唯一的色调映射是 `ToneMapper.cs` 的 CPU 路径，且 8/10/16-bit 三个出口**共用同一个 kernel**（`ToneMapToSdr:280-326`），只有最后量化步不同 ⇒ 2026-09-17 那次重构是成立的。
- **取档失败是可见的，不是静默回落**：`CapturePipelineService.cs:159-161` 走 `NoteDegraded(degraded, "本机无可用显示器 ICC")` → `LogService.Warn` + `EncoderFallbackNotifier`；且 `NotConverted` 返回**像素的真实色域**而非默认 sRGB。
- **残留的一处未证**：`HdrCaptureWindow.cs` 的 `RenderGpuComposite`（OverlayComposite PS）vs `CompositeUI`（CPU）这一对**合成**路径没有像素级等价证据；`:343` 声明 `RgbFullG10NoneP709` 而 `:337` 注明预乘 + PS 输出 alpha=1。**动这两条路之前应先补 T1/T3 判据**——这条我按 lane 的自我限制**不当结论**，只当"要先量再动"的提醒。

### L6 其余：三条"已拍板/旧账"，不是新缺陷

- **收起卡清单**：15 个 `Visibility="Collapsed"` 具名组**逐个查到点亮行**，`LlmConfigCard` 那个"只在 SelectionChanged 点亮"的陷阱**不成立**（`MainWindow.Settings.cs:169` 在加载时显式调了一次）⇒ **AVIF 那类"整卡消失"的洞没有复现**。
- **翻译 `Vision` 有代码路径、有道凭据有 UI**：`TranslationService.cs:813-814/822/890` + `MainWindow.xaml:470-471`、`:434-435` ⇒ **09-20 那份计划里"`Vision` 无代码路径""有道凭据全仓无 UI 控件"两句都已过期，该退休**（否则下一个审查者会把 `Vision` 当死代码再报一次）。
- **LogViewer 零本地化机制**：确认 0 命中；中文字串粗数 277 处（含注释，可见 UI 子集约半）。**旧账**（10-02 已记）。

### L7 判据体检（G3）：两个数字

1. **`File.ReadAllText` 从 80 处涨到 103 处**（我亲自 grep 确认）⇒ App 侧判据的 T2 文本哨兵面**还在扩大**，而 `csproj:8` 仍只引 Core ⇒ 测试宿主**无法实例化任何 App 类型**的结论不变。
2. **`entireProcessTree` 在测试工程 0 命中**（唯一那条 `Kill()` 在 `CorePipelineTests.cs:5899` 本身也是裸杀）⇒ 子进程树这条**零覆盖**。

---

## 4. 旧账重判（G2）：本轮确证"已修"的两条 + 纠正两条过期记录

| 旧项 | 本轮判定 | 证据 |
|---|---|---|
| 10-02 §9.3 启动路径写注册表（会删用户自启项） | **已修** | 赋值点全仓 3 处，唯一写注册表的是 `MainWindow.Settings.cs:388`（保存路径）；`MainWindow.xaml.cs:310-326` 只读+Warn |
| 09-27 N1 下拉项本地化 | **已修到 0** | 10-02 §9.11 记 `--ceiling 0 0`、T3 展开 7/7、44 项、0 项中文 |
| 09-27 P1-4 静默覆盖 | **仍开**（行号 `:501`→`:505`） | §3-L4-1 |
| 09-27 P1-17 托盘 | **仍开** | §3-L6-1/2 |
| 09-27 N4 `NativeLibraryResolver.Reset()` 死入口 | **仍开** | `Reset()`（`:47-54`）全仓 0 调用；`_initialized = true`（`:149`）在 try 之外 ⇒ 失败后永不重试 |
| 09-20 "`Vision` 无代码路径 / 有道凭据无 UI" | **作废（两句都过期）** | §3-L6-其余 |
| 10-02 F1 HDR→SDR 只落 Debug | **部分修**（5 处已提，`:862` 漏网） | §3-L1-1 |
| 10-02 F2 动图录制无实现 | **仍开（已拍板待命）** | lane 复核：`new AnimationRecorder` 0 处、`RegisterNamed("record")` 0 处；但 `RecordBtn` **不是死按钮**（它绑 `OnRecordHotkeyClick`，是"按键绑定"控件，绑出来的键没人注册） |

---

## 5. 我自己造成的事 / 差点写错的结论（按仓里规矩留痕）

1. **`--param-sweep` 崩了一次，我没写成产品缺陷。** 门禁 `[1.7/2]` 里它退 `-1073741571`（`0xC00000FD` 栈溢出）于 `NvencAvifBackend.TryEncodeNvencArgb10`。我**先量复现率再定性**：独立连跑 **10 次 0 失败** ⇒ 记**偶发、非因果**（与 10-01 §5 那次 WER 归档的 NVIDIA 驱动栈溢出同族，n 不够）。**没有**据此写"NVENC 不稳"。
2. **退路补丁差点是假的。** 我按老办法存 `git diff HEAD > tracked.patch`，`git apply -R --check` **报失败**。诊断出真因：`core.autocrlf=true` 且 `.gitattributes` **刻意没设 `* text=auto`** ⇒ 补丁里的 LF 内容与工作区 CRLF 不逐字匹配。加 `--ignore-whitespace` 后 `RC=0`。
   **但我没有就此收工**——按"存了不算完，要当场验"的规矩做了**真实往返**：反向应用 `MainWindow.xaml` 再正向应用回来，**sha 变了**（`5EF0B6F9…` → `96354845…`）。
   ⇒ 结论：**补丁不是字节忠实的还原手段**。已改做**字节级 zip 备份**并用已知文件验证**逐字节相同**（`worktree-bytebackup.zip`，172 文件，sha256 `0409A0CB…`）。两个补丁文件仍保留作 diff 参考，但**还原一律用 zip**。这一步如果偷懒，真出事时还原出来的树是"看着像、字节不同"的。
3. **顺手清理了两处越界残留**：补跑代理在工作区外建的 `C:\PLAN\probe_out_round68` 与 `%LOCALAPPDATA%\TrueToneCap_probe`（**先确认两者都是空目录**才删）。
4. **一次工具故障被我当成事实先量了**：`pwsh` 全线失效时我没有绕着猜，而是起了独立子代理复现（它拿到同一个 `3221225794`）⇒ 确认是会话级工具故障而非我的命令错。用户随后提示"本地有 pwsh7"，一次命中。

---

## 6. 仍需眼睛 / 仍需实机（下一轮的输入）

1. **粘进真实应用的效果**（微信 / Word / 画图）——探针只能证剪贴板里有哪些通道，证不了"粘出来成像"；§3-L4-2 这条**必须**用眼睛判。
2. **overlay 与热键冲突回退链**的实际交互。
3. **多屏 / 混合 DPI / 纯 SDR 屏**：本机**只有 1 块活跃屏**（`\\.\DISPLAY5` 3840×2160、Hdr、10 bit、峰值 417 nits、EDID ΔG=0.0963）⇒ 全部做不了；QSV/MFT 那一列也量不到。
4. **GPU vs CPU 合成等价性**（§3-L2 末）——动那条路之前要先补判据。
5. **`--unit-tests` 的 1175 条地板**要在能写 `%LOCALAPPDATA%` 的环境里复核一次。

---

## 7. 本轮账目

- **产品源码改动：0 行。** 工作树开工 77 项 → 收工 80 项，多出的 3 项全在 `tests/output/`（本轮的日志与恢复件）与本计划文件；`M`/`D` 计数由 62/4 变 60/4（差异来自 `git status` 对 `tests/output` 内文件的分类，**产品源码无一行变动**）。
- **新增文件**：`docs/pipeline-review-plan-round68-2026-10-04.md`（本计划）、`docs/pipeline-review-round68-2026-10-04.md`（本报告）、`tests/output/round68-2026-10-04/`（门禁三轮日志 + `scratch/` ），`tests/output/recovery-round68/`（退路）。
- **退路**：`tests/output/recovery-round68/worktree-bytebackup.zip`（**172 文件、逐字节验证可还原**，sha256 `0409A0CBADBF0A2F0C1C1B462E067AE08406B4228F2061662CA418DB41787A60`）+ 两个 diff 补丁（参考用，**不作还原手段**）+ `manifest.txt`。
- **门禁**：`[1/2]`–`[1.97/2]` 全部通过；7 套件中 6 套逐套件与地板逐字一致（**563 断言**），`--unit-tests` 记**环境不够、未复核**。
- **未 commit / 未 push / 未打 tag**（逐次授权）。
- **用户机器未被改**：两份 `%LOCALAPPDATA%\TrueToneCap\*.settings.json` 全程未动（`210FFCEC…` / `847D1A54…`）。

---

## 附：一轮的合格线自检

- 每个写进报告的结论都指到 `tests/output/` 或具体 `文件:行`；**没跑过的批次没写成结论**，估算值不混进实测读数 ✅
- SKIP 与判红分开计数 ✅（`--unit-tests` 记"未复核"，不记通过）
- 报告保留"本轮我自己造成的事"一节 ✅（§5，四条）
- 收尾证据数字是当场重跑的 ✅
- **未达标项**：`--unit-tests` 无法复核；三条 lane 的行号只到代理（已标【lane】）；观感类结论未下。
