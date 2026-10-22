# v0.3.4-beta 发布前审查与发布记录

- 日期：2026-09-16
- 版本：`0.3.4-beta`（AssemblyVersion `0.3.4.0`）—— 来自 `Directory.Build.props:49`，发布时**未覆盖**
- 改动规模：46 文件、+2880 / -290 行（未提交）

---

## 1. 审查方式

两路独立审查（Core 层 / App 层），各自通读 `git diff`，互不共享结论。

## 2. 审查结论

| 层 | 结论 |
|---|---|
| Core | 有小问题，**不阻塞** |
| App | 有小问题，**不阻塞** |

两路均未发现阻塞问题。

## 3. 审查发现后**已修复**的问题

这些都是本次新增代码引入的缺陷，发布前必须修掉。

| # | 问题 | 修复 |
|---|---|---|
| 1 | **多屏 HDR 拼接可能输出半张黑图**：某显示器首帧超时 / 取帧失败 / 上转换异常时，它在 `fullHdr` 中的区域留 scRGB 零值（纯黑），且亮度基准与其它区域不一致 —— 比原来的整图 SDR 更差 | 新增 `hdrFillFailures` 计数，任一显示器未参与填充即整图放弃 HDR（SDR 结果不受影响） |
| 2 | **`CopyFloatRegion` 未夹紧单行宽度**：只防总长越界，混合 DPI 多屏下 `session.Width`（物理像素）可能大于该屏在逻辑坐标中的剩余宽度，行尾会绕写到下一行开头（错行/撕裂） | `copyW = min(源行宽, 目标剩余宽度)`，按 `copyW` 拷贝 |
| 3 | `LastFallbackReason` 非 volatile，跨线程（编码线程写 / UI 读）存在可见性风险 | 加 `volatile` |

## 4. 已知但**未修**（不阻塞，已记录）

| # | 问题 | 性质 |
|---|---|---|
| 1 | `OnnxOcrEngine.Dispose` 不再 `Release()` `_inferGate`，已越过检查的请求可能永久挂起 | 罕见，建议加超时 |
| 2 | 纹理路径的异常串不含驱动英文描述 ⇒ `IsInputSpecificNvencFailure` 对它恒不命中（等同旧行为，无回归，但该分支是死逻辑） | 待改进 |
| 3 | `SettingsService` 未做写探测：装在只读目录时 `SaveQuiet`（硬件编码开关走此路）会静默失败 | 功能性回退风险，建议后续加写探测 |
| 4 | `QsvAvifBackend` 与 `MftAvifBackend` 现已完全同实现（都走 MFT），QSV 只剩标签；`QsvEncoderNative.cs` 成死代码 | 建议合并或说明 |
| 5 | `EncoderCrashSentinel` 每次编码写+删一次哨兵文件；且"进程被强杀"与"NVENC 真崩溃"无法区分，会误降级 | 建议连续 2 次残留才禁用 |
| 6 | Toast 浮层无堆叠（连发会重叠）；`OnPointerExited` 在窗口关闭后仍触发 | 建议加偏移堆叠 + 置空计时器 |
| 7 | `ToneMapping.hlsl:57`、`ShaderCompiler.cs:19` 注释指向已删除的 `_ps` 加载 | 仅文档 |
| 8 | HDR 设备重建失败后用户只见黑屏，提示仅进日志 | 建议补可见提示 |

## 5. 验证

```
构建：0 个警告 / 0 个错误
测试：8 套件 446 断言全绿
产物自测：6/6 格式通过
```

## 6. 发布结果

| 项 | 结果 |
|---|---|
| 发布 | ✅ 566 文件 / 415.6 MB，`publish\TrueToneCap-v0.3.4-beta` |
| 校验 | `SHA256SUMS.txt` + `build-info.json` |
| 打包 | ✅ `TrueToneCap-v0.3.4-beta-win-x64.7z`，120.3 MB |
| 锁文件 | Core 无 `win-x64` 段（无 RID 漂移）；App 锁文件改动为上一轮把 `DependencyInjection` 从 RC（11.0.0-rc.1）改为稳定版 10.0.12，属有意调整 |

⚠️ `build-info.json` 标记 `dirtyWorktree=true`（脏工作区发布），**不能作为正式发行版**，仅用于验证。正式发布需先提交改动。
