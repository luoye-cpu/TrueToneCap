# tests/ 目录说明

本目录放两类东西：**取证 harness**（`manual/`，一次性验证轮留下的脚本）与**真夹具**（`fixtures/`）。
`output/` 是本机落点，整个被忽略，不在讨论范围。

> 2026-10-20 更正：本文件原写「本目录统一管理**不上传 GitHub** 的测试与调试脚本」，
> 这句已被 `.gitignore:114-141` 那套逐条点名反选推翻 —— `manual/` 下**有大量文件是跟踪入库的**
> （本轮清掉 29 个零活调用方的取证件后，工作树里仍剩 82 个在册脚本），"不上传"只对新写的脚本成立。

## 结构

| 目录 | 内容 | 是否上传 |
|------|------|----------|
| `fixtures/` | 门禁要读的真夹具（ICC 结构判据的两个 `.icc`） | ✅ 上传，且被 `scripts/verify.ps1` 的新鲜度枚举面扫到 |
| `manual/` | 一次性取证 harness 与分析脚本 | ⚠️ **默认不上传，入库需显式点名**：`.gitignore` 用 `tests/manual/*` 打底，再逐条 `!` 反选；`safety/`、`publish-probes/` 两个子目录整体反选 |
| `output/` | harness 的运行产物（日志、样张、备份补丁） | ❌ `tests/output/` 整目录忽略 |
| `src/TrueToneCap.Test/`（项目内，**不在本目录**） | 正式自动化测试（随 slnx 构建，被门禁实跑） | ✅ 上传 |

## 自动化测试

运行方式：
```powershell
dotnet run --project src/TrueToneCap.Test -- --all      # 全部 8 个套件
dotnet run --project src/TrueToneCap.Test -- --unit-tests   # 单套件
```

> **断言数不在本文件维护。** 下面这列抄自单一真源 [scripts/gate-baseline.json](../scripts/gate-baseline.json)
> 的 `各套件.*.断言`，取数时间 **2026-10-06 16:25**（本机 7 套件全绿 / 1,787 断言 / 0 失败）。
> 本表原有的一列数字（合计 373、unit 47）是 2026-09-14 期的读数，与真源差了 4.8 倍却仍标着"当前"，
> 属典型的手抄漂移 —— 现在的规矩是：改断言就更新 `gate-baseline.json`，本表要么不动、要么照抄并注明取数时间。

| 套件 | 参数 | 断言数（10-06 抄录） | 跳过 | 需桌面会话 |
|------|------|:---:|:---:|:---:|
| 核心管线（PixelOps / ToneMapper / ICC / 标注 / 编码器） | `--unit-tests` | 1,224 | 1 | |
| 色彩管线精度 | `--color-tests` | 267 | 0 | |
| 编码管线集成 | `--encoding-tests` | 14 | 0 | |
| 输出结构合法性（TIFF IFD / AVIF box） | `--format-tests` | 43 | 0 | |
| 基础设施服务 | `--service-tests` | 2 | 1 | |
| OCR 服务 | `--ocr-tests` | 12 | 0 | |
| 综合可用性 | `--usability-tests` | 225 | 0 | 部分¹ |
| WGC 捕获 + AVIF 后端验证 | `--wgc-avif-tests` | —（不在 baseline，默认门禁不跑） | — | ✓ |

> ¹ `--usability-tests` 自 2026-09-14 起**可在 headless 环境运行**：18 个小节中只有
> `DisplayInfo` / `RegionDetector` 两处真正依赖显示器，已改为「无可用显示器则**显式跳过**」
> （计入 `skipped`，既不算通过也不算失败）。因此它已被纳入 `Publish.ps1` 与
> `scripts/verify.ps1` 的默认门禁 —— **当时的**门禁覆盖率由 199/373 提升到 352/373（分母是 09-14 期口径，
> 现已是 1,787，不能横比），仅剩 WGC 套件（需真实捕获 + GPU）需 `-IncludeDesktop` 手工执行。

### 执行模型

- `--all` 覆盖**全部 8 个套件**（此前遗漏 WGC 套件的问题已修复）。
- 大部分套件在同一进程内顺序执行，各自以静态 `_passed/_failed` 独立计数。
- `--wgc-avif-tests` 因需独占 **STA 线程**（WGC 帧投递依赖消息泵）并持有
  D3D11 设备 / 捕获会话等 GPU 资源，在 `--all` 下改以**独立子进程**运行，
  避免与同进程内的其它测试相互干扰。
- 退出码汇总：任一子进程或套件失败，`--all` 即返回非 0。

> 提示：带 ✓ 的套件需要交互式桌面会话，在无 GUI 的 headless 环境中会被跳过或失败。

## 约定

- 新增临时调试/验证脚本一律放入 `tests/manual/`，**不要**放回 `scripts/` 或仓库根目录。
- `scripts/` 仅保留正式工具脚本（如 `migrate-net11.ps1`、`onnx_fp16_quantize.py`、
  `export_onnx_fp32.py`）。
- `tests/manual/` 由 `.gitignore` 的 `tests/manual/*` 打底，**反选是逐条点名的**
  （`:119-135`，另有 `safety/`、`publish-probes/` 两个子目录整体反选）。
  写"无需手动维护忽略清单"是错的：往这里新增文件不会被自动纳入，要入库必须显式加一行，
  那一行就是审查点。反过来，已跟踪文件不受忽略规则影响，忽略只对未跟踪文件生效。
- `glm_*` 三个脚本已于 2026-09-26 删除（各硬编码一把活的 GLM API Key），本目录现已无此文件；
  约束照旧：**带密钥的探针一律不反选入库**，要入库先把密钥改成读环境变量。
  撤销动作在智谱侧，删文件不等于密钥失效（见 `.gitignore:136-141`）。
- 断言必须**可证伪**：避免 `x is null || ...` 这类恒真写法（无论成败都通过），
  此类断言会掩盖真实回归。
