# 未提交改动分类报告（2026-09-14）

**目的**：为 P1-7「清理 62 项悬挂改动」提供分批提交依据。
**方法**：按 **diff 内容** 判断归属 —— 不用 mtime（后编辑会覆盖时间戳，历史上据此误判过）。
**基线**：`main` @ `49d6c94`（该提交已包含用户 9-13 的功能批次）。

---

## 1. 归属结论

| 归属 | 数量 | 说明 |
|------|:---:|------|
| 2026-09-14 工程化改动（本轮/前几轮审查的落地） | **61 项** | 配置集中化、测试体系、发布与打包、着色器对齐、文档过时标注、仓库卫生 |
| **混合文件** | **1 项** | `README.md` —— 含用户 9-13 的双语重写 + 本轮的发布流程/文件名修正 |
| 用户 9-13 遗留功能代码 | **0 项** | 已由 `49d6c94` 提交完毕，工作区不再有遗留功能改动 |

> 逐文件核对过的 diff 摘要见 §4。

---

## 2. 建议的分批提交顺序

先底层配置 → 再代码 → 再脚本 → 最后文档，**保证每一批单独可构建**。

| 批次 | 主题 | 建议 commit message | 文件 |
|:---:|------|------|------|
| **1** | 配置集中化与依赖治理 | `build: 配置集中化 — Directory.Build.props / CPM / 锁定文件 / SDK 固定` | `Directory.Build.props`、`Directory.Packages.props`、`global.json`、`NuGet.config`、`.editorconfig`、`.gitattributes`、`TrueToneCap.slnx`、5 个 `*.csproj`、5 个 `packages.lock.json` |
| **2** | 着色器双路径对齐 | `fix(shader): 运行时与构建期统一 fxc + SM5，枚举 SDK 版本不再硬编码` | `src/TrueToneCap.App/Services/ShaderCompiler.cs`、`Shaders/CompileShaders.ps1`、`Services/HdrCaptureWindow.cs`（注释） |
| **3** | 测试体系加固 | `test: 统一总结输出 TestSummary、修复恒真断言、--all 覆盖 8 套件` | `src/TrueToneCap.Test/*`（10 个）、`tests/README.md` |
| **4** | 发布与打包 | `build(release): 依赖校验阻断 + 工具链哈希清单 + 出包冒烟 + 压缩打包` | `Publish.ps1`、`PublishLogViewer.ps1`、`download_models.ps1`、`scripts/*`（5 个）、`src/TrueToneCap.Core/Resources/tools-manifest.json`、`publish/PACKAGE.md` |
| **5** | 仓库卫生 | `chore: 解除 publish/PLAN/tools 重复二进制的跟踪，收敛 .gitignore` | `.gitignore`、`publish/PLAN/tools/*`（9 个删除，已 `git rm --cached`） |
| **6** | 文档 | `docs: 过时标注 + 流水线审查报告 + README 发布流程与文件名修正` | `README.md`（**混合**）、`docs/*` |

> 第 5 批中 `publish/PLAN/tools/*` 的删除**已处于暂存区**（`D ` 状态），
> 提交后 `.gitignore` 的 `publish/PLAN/tools/` 规则才开始对新文件生效 —— 这一步不能跳。

---

## 3. 需要人工确认的两点

### 3.1 `README.md` 是混合文件

- **用户内容**（9-13）：双语简介、特性表、v0.3.4-beta 更新日志等大段重写；
- **本轮内容**：快速开始的文件名规则说明、发布章节新增 ①②③ 三步、冒烟说明。

两份内容在同一文件里交错，**按 hunk 拆分成本高且易出错**。
建议：整文件提交，commit message 明确写「含 9-13 双语重写 + 本轮发布流程修正」，不做归属切割。

### 3.2 `.workbuddy-ai/` 建议忽略

该目录是工作台数据（项目记忆），不应进版本库。已加入 `.gitignore`（归入第 5 批）。

---

## 4. 逐文件核对摘要（diff 内容，非 mtime）

```
配置类（全部为"把公共属性上提到 Directory.Build.props / 版本上提到 Directory.Packages.props"）
  TrueToneCap.slnx                        +7-1   App/LogViewer 补 <Platform Project="x64" />
  5 × *.csproj                                   移除重复属性；PackageReference 去 Version
  5 × packages.lock.json                  新增   NuGet 原生生成，未手工编辑

着色器（双路径对齐）
  ShaderCompiler.cs                       +61-23 ps_6_0/dxc → ps_5_0/fxc；补 OverlayComposite；清单状态注释
  CompileShaders.ps1                      +17-5  枚举 Windows Kits 取最高版本，不再硬编码 26100/22621
  HdrCaptureWindow.cs                     +6-1   纯注释：说明 _ps 是死代码、回退无行为影响

测试（统一输出 + 修复不可证伪断言）
  5 × *Tests.cs                           +1-1   手写"结果:"行 → TestSummary.Report(...)
  Program.cs                              +138-59 --all 覆盖 8 套件 + WGC 子进程隔离 + 用法说明
  TestSummary.cs                          新增   统一 RESULT 行
  ColorPipelineTests.cs                   +33-5  PQ(0) 用 1e-4 容差（不再恒真）
  UsabilityTests.cs                       +42-8  ICC 契约分情况断言（sRGB 不嵌 ICC）
  ServiceTests.cs                         +62-10 ShaderLoader 不可达时的断言不再恒真

发布与打包
  Publish.ps1                             +571-151 依赖校验 / 锁定文件 / 测试门禁 / [5.5/6] 冒烟 / 工具哈希 / build-info
  download_models.ps1                     +48-6  模型 SHA256 固化 + 强制校验
  scripts/verify.ps1 等 5 个               新增   一键校验 / 冒烟 / 打包 / 门禁桩验证 / 预编译发布

文档
  docs/architecture-design.md             顶部加"已过时"对照表
  docs/ocr-parallel-analysis.md           顶部标注并行化方案已被 2026-09-13 修复否决
  docs/reviews/dependency/dependency-audit-2026-08-25.md     标注含事实性错误、已被新清单取代
  docs/repo-hygiene-review.md / roadmap-v0.4.md  状态更新横幅
  8 篇 2026-09-14 审查报告                 新增
```

---

## 5. 提交前的门禁

每批提交前后都建议跑一次（配置类改动尤其需要）：

```powershell
.\scripts\verify.ps1          # 构建 + headless 6 套件
```

全部提交完成后，`Publish.ps1` 的「工作区必须干净」门禁才真正可用 ——
否则 `-AllowDirty` 会被常态化，产物不可复现却无人察觉（这正是 P1-7 指出的问题）。
