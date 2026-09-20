# tests/ 目录说明

本目录统一管理**不上传 GitHub** 的测试与调试脚本。

## 结构

| 目录 | 内容 | 是否上传 |
|------|------|----------|
| `manual/` | 手动调试/验证脚本（`analyze_*`、`verify_*`、`check_*`、`test_*`、`glm_*` 等一次性分析脚本） | ❌ 被 `.gitignore` 忽略 |
| `src/TrueToneCap.Test/`（项目内） | 正式自动化测试（随 slnx 构建） | ✅ 上传 |

## 自动化测试

运行方式：
```powershell
dotnet run --project src/TrueToneCap.Test -- --all      # 全部 8 个套件
dotnet run --project src/TrueToneCap.Test -- --unit-tests   # 单套件
```

| 套件 | 参数 | 断言数 | 需桌面会话 |
|------|------|:---:|:---:|
| 核心管线（PixelOps / ToneMapper / ICC / 标注 / 编码器） | `--unit-tests` | 47 | |
| 色彩管线精度 | `--color-tests` | 93 | |
| 编码管线集成 | `--encoding-tests` | 14 | |
| 输出结构合法性（TIFF IFD / AVIF box） | `--format-tests` | 30 | |
| 基础设施服务 | `--service-tests` | 5 | |
| OCR 服务 | `--ocr-tests` | 10 | |
| 综合可用性 | `--usability-tests` | 153 | 部分¹ |
| WGC 捕获 + AVIF 后端验证 | `--wgc-avif-tests` | 21 | ✓ |

> ¹ `--usability-tests` 自 2026-09-14 起**可在 headless 环境运行**：18 个小节中只有
> `DisplayInfo` / `RegionDetector` 两处真正依赖显示器，已改为「无可用显示器则**显式跳过**」
> （计入 `skipped`，既不算通过也不算失败）。因此它已被纳入 `Publish.ps1` 与
> `scripts/verify.ps1` 的默认门禁 —— 门禁覆盖率由 199/373 提升到 **352/373**，
> 仅剩 WGC 套件（需真实捕获 + GPU）需 `-IncludeDesktop` 手工执行。

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
- `tests/manual/` 由 `.gitignore` 的 `tests/manual/` 规则忽略，无需手动维护忽略清单。
- 注意：`glm_*` 脚本内含 API Key，禁止上传到任何公开仓库。
- 断言必须**可证伪**：避免 `x is null || ...` 这类恒真写法（无论成败都通过），
  此类断言会掩盖真实回归。
