# TTC 的 VS-Cap 模式（已于 2026-10-27 归档）

> 本文件保留路径只为让旧链接不悬空。**侧车源码、Qt 依赖与「VS-Cap 模式」开关已从产品移除**；
> 它带进来的三项能力已并入主程序。旧构建步骤、`src/TrueToneCap.VSCap/` 与
> `scripts/BuildVSCap.ps1` 只存在于 git 历史（最后一个包含侧车的提交），不在当前树里。

## 为什么归档

上游 PR #1 的思路是"把区域截图交给一个 Qt 侧车去编辑"：像素出进程 → Qt 编辑 → 回写 → TTC 编码。
在这台机器上这条路从来没跑通过，而且它把真正缺的**能力**（富文本、统一撤销、置顶贴图）
绑在了一条**不可交付的依赖**上：

- 本机没有 Qt 工具链（只有 cmake），`BuildVSCap.ps1` 无法执行；CI 的自托管 runner 同样没有。
- 侧车靠 csproj 一条 glob 进包：目录不存在时展开成 0 个条目 ⇒ 构建、打包、测试全绿，
  而功能根本不在包里。为此长期挂着一个"点了只会报错"的开关与一套"声明带不带侧车"的门禁。
- 侧车里的选区是**不可调整**的（没有手柄、不能平移），所以它不是选区交互问题的来源，
  也不是答案；产品真实的选区路径始终是 `HdrCaptureWindow`。

## 并入主程序的三项能力（2026-10-27）

| 能力 | 落点 | 真机读数 |
| --- | --- | --- |
| 富文本文字：字体 / 字号 / 加粗 / 斜体、就地再编辑、拖动移动 | `HdrCaptureWindow`（Win32 字体控件条 + 多行 EDIT）与 `AnnotationRasterizer`（Win2D 实测位图） | `--preview-ui-probe` ⑨ |
| 创建 / 移动 / 编辑进同一条撤销栈 | `AnnotationManager.UpdateLayer` + `TextLayer.Clone` 保留图层身份 | `--preview-ui-probe` ⑨（撤销/重做逐项对撞） |
| 置顶贴图窗：拖动、滚轮 1.2 倍缩放、右键 复制 / 保存 PNG / 保存 AVIF / 隐藏工具栏 / 销毁 | `Services/PinWindow.cs`，主工具条「贴图」按钮 | `--preview-ui-probe` ⑩ |

文字预览与导出现在用**同一张**实测位图：旧实现预览把文字塞进固定 256×64 槽位再拉伸、
导出按"字数 × 字号 × 1.05"估宽，两边从来不是同一幅画面；同时文字提交以前实际上不落地
（`GetWindowText` 用 `char[]` 在 `DllImport` 下是 In 数组，读回一屏 NUL）——两处都已修。

## 归档后的一致性判据

- `scripts/verify.ps1` 的 `[1.98/2]`：侧车源码 / 对撞工程 / 构建脚本**都不在**树里；
  它们一旦回来，就必须连同跨语言像素判据一起恢复，不许半留。
- `scripts/verify-package.ps1` 的 `[5.5/6]`：包内**不许**出现 `data\vscap`。
- `Publish.ps1` 不再有 `-NoVSCap` 参数；`release.yml` 也不再传它（两侧由
  `scripts/verify-publish-gates.ps1` 的场景 G 静态对撞）。
- 许可清单里与 Qt / VS-Cap 有关的两条随侧车一起移除（`licenses/INDEX.json`）。

## 历史

移植记录与当时的验证边界（TTC1 交接头、`VSCapFrame` 的 BGRA8 / RGBA float32 交换、
`Ctrl+Alt+T` 贴图热键、Qt 侧 11 项工作流读数）见 git 历史中的本文件，以及
[`docs/reviews/upstream/upstream-pr1-review-2026-10-16.md`](reviews/upstream/upstream-pr1-review-2026-10-16.md)。
那份审阅里记的"合到了错误基座/构建不过/侧车不入包"等结论，正是这次归档的依据。
