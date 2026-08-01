# TrueToneCap 分发打包说明 / Distribution Packaging Guide

> v0.3.0 Beta · 2026-08-01

---

## 一、发布包结构 / Package Structure

```
TrueToneCap-v0.3.0-beta-win-x64/
├── TrueToneCap.exe              # 主程序入口
├── TrueToneCap.dll              # WinUI 3 应用层
├── TrueToneCap.Core.dll         # 核心引擎（捕获/编码/色彩/OCR）
├── TrueToneCap.pri              # 编译后的 XAML 资源索引
├── TrueToneCap.deps.json        # .NET 依赖清单
├── TrueToneCap.runtimeconfig.json
│
├── *.xbf                        # 编译后的 XAML 二进制文件 (7 个)
│   ├── App.xbf
│   ├── MainWindow.xbf
│   ├── AnnotationWindow.xbf
│   ├── SelectionOverlay.xbf
│   ├── OcrPreviewWindow.xbf
│   ├── SilentCaptureToast.xbf
│   └── WindowPreviewTooltip.xbf
│
├── data/                       # 运行时数据目录
│   ├── Models/                  # OCR ONNX 模型（详见 §五）
│   │   ├── PP-OCRv6_medium_det.onnx   (~59 MB)
│   │   ├── PP-OCRv6_medium_rec.onnx   (~73 MB)
│   │   └── ppocrv6_dict.txt            (~26 KB)
│   ├── Shaders/                 # HLSL 着色器 (.cso)
│   └── TrueToneCap.Core.pdb     # 调试符号
│
├── native/                     # 原生工具链（从 PLAN/tools/ 预提取，详见 §六）
│   ├── avifenc.exe             # AVIF 编码器 (~12 MB)
│   ├── cjpegli.exe             # JPEG LI 编码器 (~5 MB)
│   └── cwebp.exe               # WebP 编码器 (~0.7 MB)
│
├── README.md                    # 用户文档
├── LICENSE                      # Apache 2.0
│
├── Microsoft.WindowsAppRuntime.Bootstrap.dll  # Windows App Runtime 引导
├── Microsoft.WindowsAppRuntime.dll
│
├── Microsoft.UI.Xaml.dll        # WinUI 3 框架
├── Microsoft.UI.Xaml/           # WinUI 3 XAML 资源
│
├── onnxruntime.dll              # ONNX Runtime (OCR 推理, DirectML)
├── Microsoft.ML.OnnxRuntime.dll # ONNX Runtime .NET 绑定
├── Microsoft.ML.OnnxRuntime.DirectML.dll # DirectML 执行提供程序
│
├── Magick.NET-Q16-HDRI-AnyCPU.dll  # ImageMagick 绑定 (图像编码)
├── Magick.NET.Core.dll
├── Magick.Native-Q16-HDRI-x64.dll   # ImageMagick 原生库
│
├── Vortice.Direct3D11.dll       # D3D11 绑定
├── Vortice.Direct3D12.dll       # D3D12 绑定
├── Vortice.DXGI.dll             # DXGI 绑定
├── Vortice.DirectX.dll          # DirectX 通用
├── Vortice.Mathematics.dll      # 数学库
│
├── Microsoft.Graphics.Canvas.dll    # Win2D GPU 加速 2D 渲染
├── Microsoft.Graphics.Canvas.Interop.dll
│
├── Microsoft.Web.WebView2.Core.dll  # WebView2（可选，LLM 翻译 UI 使用）
├── WebView2Loader.dll
│
├── WinRT.Runtime.dll            # WinRT 互操作
├── SharpGen.Runtime.dll         # COM 互操作（Vortice 依赖）
│
├── System.Drawing.Common.dll    # GDI+ 互操作
├── System.Windows.Forms.dll     # WinForms 托盘图标/剪贴板
├── System.Text.Json.dll         # JSON 序列化
│
├── runtimes/                    # .NET 运行时原生库
├── zh-CN/ en-us/ ja/ ...        # 多语言资源 (68 种语言)
│
└── *.dll (250+ .NET 框架程序集)  # Self-contained 发布包含完整 .NET 10 运行时
```

---

## 二、版本变体 / Release Variants

| 变体 | 文件名 | 大小 | 说明 |
|------|--------|------|------|
| **标准版** | `TrueToneCap-v0.3.0-beta-win-x64.zip` | ~550 MB | 包含 OCR 模型，开箱即用识字/翻译 |
| **精简版** | `TrueToneCap-v0.3.0-beta-win-x64-lite.zip` | ~400 MB | 不含 OCR 模型，需联网下载或仅用 Windows OCR |

### 制作方法

```powershell
# 1. 运行发布脚本（自动编译 + 从 PLAN/ 复制依赖）
.\Publish.ps1 -Configuration Release -Runtime win-x64

# 2. 制作精简版（不含 data/Models/）
$publishDir = "publish\TrueToneCap-v0.3.0-beta"
$liteDir = "publish\TrueToneCap-v0.3.0-beta-lite"
Copy-Item $publishDir $liteDir -Recurse
Remove-Item "$liteDir\data\Models" -Recurse -Force -ErrorAction SilentlyContinue

# 3. 打包标准版（含 OCR 模型）
Compress-Archive -Path "$publishDir\*" -DestinationPath "publish\TrueToneCap-v0.3.0-beta-win-x64.zip"

# 4. 打包精简版（不含 OCR 模型）
Compress-Archive -Path "$liteDir\*" -DestinationPath "publish\TrueToneCap-v0.3.0-beta-win-x64-lite.zip"
```

---

## 三、依赖说明 / Dependencies

### 运行时依赖（已内嵌，无需安装）

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET 10 Runtime | 10.0.x | Self-contained 内嵌 (~80 MB) |
| Windows App SDK | 2.3.1 | Bootstrap 内嵌，运行时自动加载框架 |
| WinUI 3 | 3.1.6 | 内嵌在 Microsoft.UI.Xaml.dll |
| ONNX Runtime | 1.24.4 | DirectML 后端，OCR 推理 (PP-OCRv6) |
| ImageMagick (Magick.NET) | 14.15.0 Q16-HDRI | ICC 烘焙 + 色域映射（编码器已全部替换为托管/原生实现） |
| Win2D | 1.4.0 | GPU 加速 2D 渲染 |
| Vortice (DirectX) | 3.8.3 | D3D11/D3D12/DXGI 底层绑定 |

### 系统要求

| 要求 | 最低 | 推荐 |
|------|------|------|
| **操作系统** | Windows 11 24H2 (Build 26100) | 最新 Windows 11 |
| **架构** | x64 | x64 |
| **GPU** | 任何支持 D3D11 的 GPU | D3D12 + DirectML 兼容 GPU |
| **显示器** | SDR | HDR (BT.2020 / Display P3) |
| **内存** | 8 GB | 16 GB+ |
| **磁盘** | 1 GB (精简) / 1.5 GB (标准) | SSD |

---

## 四、字体说明 / Fonts

TrueToneCap 默认使用 **微软雅黑** 作为 UI 字体。可在系统设置 → 界面字体中自定义为任意已安装的系统字体。

---

## 五、OCR 模型说明 / OCR Models

### 模型列表

| 文件 | 大小 | 用途 |
|------|------|------|
| `PP-OCRv6_medium_det.onnx` | ~59 MB | 文字检测（FP16 中型） |
| `PP-OCRv6_medium_rec.onnx` | ~73 MB | 文字识别（FP16 中型） |
| `ppocrv6_dict.txt` | ~26 KB | 中英文字典 (6625 字符) |

### 模型来源

基于 PaddleOCR PP-OCRv6 导出为 ONNX 格式（FP16 中型），支持中文 + 英文混合识别。

### 模型加载优先级

1. 首先查找 `<应用目录>\data\Models\` （发布包自带，由 `Publish.ps1` 从 `PLAN/models/` 复制）
2. 回退到 `%LOCALAPPDATA%\TrueToneCap\onnx_models\` （用户自行下载）
3. 如果 ONNX 模型不可用，降级使用 Windows 内置 OCR

---

## 六、依赖仓库 / Dependency Repository

所有非核心运行时组件统一存放在 `publish/PLAN/` 目录下，打包时由 `Publish.ps1` 自动收集。

### 目录结构

```
publish/PLAN/
├── tools/       # 原生工具链 → 预提取到发布包 native/
├── models/      # OCR ONNX 模型 → 复制到发布包 data/Models/
├── fonts/       # [可选] 用户字体放置目录
└── README.md    # 依赖仓库说明
```

### 保持同步

| 仓库位置 | 发布包目标 | 同步方式 |
|---------|----------|---------|
| `PLAN/tools/` | `native/` | `Publish.ps1` 步骤 5.1 复制 |
| `PLAN/models/` | `data/Models/` | `Publish.ps1` 步骤 5.2 复制 |
| `src/Core/Resources/` | 嵌入 DLL 运行时提取到 `native/` | 手动同步（与 `PLAN/tools/` 一致） |

> **注意**：`src/TrueToneCap.Core/Resources/` 中的 exe 嵌入 DLL 供运行时自动提取，
> `PLAN/tools/` 中的 exe 供打包时预提取。两个目录必须保持文件一致。

---

## 七、更新检查清单 / Release Checklist

- [ ] `csproj` 版本号更新（Version / AssemblyVersion / FileVersion）
- [ ] `Publish.ps1` 输出目录更新
- [ ] `README.md` 版本号和更新日志更新
- [ ] `PLAN/tools/` 与 `Core/Resources/` 工具版本一致
- [ ] 编译通过 `dotnet build -c Release -r win-x64`
- [ ] 运行 `.\Publish.ps1` 生成发布包（自动验证 PLAN 完整性）
- [ ] 检查 `native/` 包含 3 个工具（avifenc.exe, cjpegli.exe, cwebp.exe）
- [ ] 检查 `data/Models/` 包含 3 个 OCR 文件（标准版）
- [ ] 生成精简版（不含 `data/Models/`）
- [ ] 打包 ZIP
- [ ] 验证 ZIP 解压后可直接运行 `TrueToneCap.exe`
