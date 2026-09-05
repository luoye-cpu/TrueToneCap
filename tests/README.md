# tests/ 目录说明

本目录统一管理**不上传 GitHub** 的测试与调试脚本。

## 结构

| 目录 | 内容 | 是否上传 |
|------|------|----------|
| `manual/` | 手动调试/验证脚本（`analyze_*`、`verify_*`、`check_*`、`test_*`、`glm_*` 等一次性分析脚本） | ❌ 被 `.gitignore` 忽略 |
| `src/TrueToneCap.Test/`（项目内） | 正式自动化测试（随 slnx 构建） | ✅ 上传 |

## 自动化测试

运行方式：`dotnet run --project src/TrueToneCap.Test -- --all`

| 套件 | 参数 |
|------|------|
| 核心管线（PixelOps / ToneMapper / ICC / 标注 / 编码器） | `--unit-tests` |
| 色彩管线精度 | `--color-tests` |
| 编码管线集成 | `--encoding-tests` |
| 输出结构合法性（TIFF IFD / AVIF box） | `--format-tests` |
| 基础设施服务 | `--service-tests` |
| OCR 服务 | `--ocr-tests` |
| 综合可用性 | `--usability-tests` |

## 约定

- 新增临时调试/验证脚本一律放入 `tests/manual/`，**不要**放回 `scripts/` 或仓库根目录。
- `scripts/` 仅保留正式工具脚本（如 `migrate-net11.ps1`、`onnx_fp16_quantize.py`）。
- `tests/manual/` 由 `.gitignore` 的 `tests/manual/` 规则忽略，无需手动维护忽略清单。
- 注意：`glm_*` 脚本内含 API Key，禁止上传到任何公开仓库。
