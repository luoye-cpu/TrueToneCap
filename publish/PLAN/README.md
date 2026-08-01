# TrueToneCap 非核心组件仓库 / Non-Core Components

此目录为 **依赖仓库**，存放所有非代码运行时组件。打包时由 `Publish.ps1` 自动收集到发布包中。

## 目录结构

```
PLAN/
├── tools/       # 原生工具链 → 预提取到发布包 native/ 目录
├── models/      # OCR ONNX 模型 → 复制到发布包 data/Models/ 目录
├── fonts/       # [可选] 用户可放置 .ttf 字体到此
└── README.md
```

## 各组件说明

### tools/ — 原生工具链

| 文件 | 来源 | 大小 | 用途 | 内嵌方式 |
|------|------|------|------|---------|
| `avifenc.exe` | libavif (aom) | ~12 MB | AVIF 编码 | `Core/Resources/` 嵌入 DLL → 运行时提取到 `native/` |
| `cjpegli.exe` | Google jpegli | ~5 MB | JPEG LI 编码 | `Core/Resources/` 嵌入 DLL → 运行时提取到 `native/` |
| `cwebp.exe` | Google libwebp | ~0.7 MB | WebP 编码 | `Core/Resources/` 嵌入 DLL → 运行时提取到 `native/` |

> **保持同步**：`PLAN/tools/` 与 `src/TrueToneCap.Core/Resources/` 必须保持文件一致。
> 更新工具时，两个目录都需要替换。

### models/ — OCR ONNX 模型

| 文件 | 大小 | 用途 |
|------|------|------|
| `PP-OCRv6_medium_det.onnx` | ~59 MB | 文字检测 (FP16 中型) |
| `PP-OCRv6_medium_rec.onnx` | ~73 MB | 文字识别 (FP16 中型) |
| `ppocrv6_dict.txt` | ~26 KB | 中英文字典 (6625 字符) |

> 模型文件太大（共 ~132 MB），不适合嵌入 DLL，通过 `Publish.ps1` 复制到 `data/Models/`。

### fonts/ — 用户字体目录

> 此目录已清空，不再内置打包字体。用户可通过系统设置 → 界面字体选择已安装的系统字体。

---

## 打包流程

```
Publish.ps1
  ├─ dotnet publish → 发布包基础输出
  ├─ PLAN/tools/*.exe → native/  (预提取，避免首次运行等待)
  ├─ PLAN/models/*.onnx → data/Models/  (OCR 模型)
  └─ 清理 .pdb 等调试符号
```

## 版本对应

| 软件版本 | PLAN 组件版本 | 备注 |
|---------|-------------|------|
| v0.3.0-beta | PP-OCRv6_medium, avifenc 1.x, cjpegli 1.x, cwebp 1.5 | — |
