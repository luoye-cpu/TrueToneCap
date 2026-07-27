# TrueToneCap 分发打包说明 / Distribution Packaging Guide

> v0.1.5 Beta · 2026-07-05

---

## 一、发布包结构 / Package Structure

```
TrueToneCap-v0.1.5-beta-win-x64/
├── TrueToneCap.exe              # 主程序入口
├── TrueToneCap.dll              # WinUI 3 应用层
├── TrueToneCap.Core.dll         # 核心引擎（捕获/编码/色彩/OCR）
├── TrueToneCap.pri              # 编译后的 XAML 资源索引
├── TrueToneCap.deps.json        # .NET 依赖清单
├── TrueToneCap.runtimeconfig.json
│
├── *.xbf                        # 编译后的 XAML 二进制文件
│   ├── App.xbf
│   ├── MainWindow.xbf
│   ├── AnnotationWindow.xbf
│   └── SelectionOverlay.xbf
│
├── Fonts/                       # 字体目录（详见 §四）
│   ├── README_FONTS.txt
│   └── *.ttf / *.otf            # 用户自行放置鸿蒙黑体
│
├── Models/                      # OCR ONNX 模型（详见 §五）
│   ├── ch_PP-OCRv4_det_server_infer.onnx   (~108 MB)
│   ├── ch_PP-OCRv4_rec_server_infer.onnx   (~86 MB)
│   └── ppocr_keys_v1.txt                   (~26 KB)
│
├── README.md                    # 用户文档
├── LICENSE                      # Apache 2.0
│
├── Microsoft.WindowsAppRuntime.*.msix  # Windows App Runtime 框架 (×4)
├── MSIX.inventory               # MSIX 清单
├── WindowsAppRuntime.png
│
├── Microsoft.UI.Xaml.dll        # WinUI 3 框架
├── Microsoft.UI.Xaml/           # WinUI 3 XAML 资源
│
├── onnxruntime.dll              # ONNX Runtime (OCR 推理)
├── Microsoft.ML.OnnxRuntime.dll # ONNX Runtime .NET 绑定
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
| **标准版** | `TrueToneCap-v0.1.5-beta-win-x64.zip` | ~470 MB | 包含 OCR 模型，开箱即用识字/翻译 |
| **精简版** | `TrueToneCap-v0.1.5-beta-win-x64-lite.zip` | ~270 MB | 不含 OCR 模型，需联网下载或仅用 Windows OCR |

### 制作方法

```powershell
# 1. 先运行标准发布
.\Publish.ps1 -Configuration Release -Runtime win-x64

# 2. 制作精简版（不含 Models/）
$publishDir = "publish\TrueToneCap-v0.1.5-beta"
Copy-Item $publishDir "publish\TrueToneCap-v0.1.5-beta-lite" -Recurse
Remove-Item "publish\TrueToneCap-v0.1.5-beta-lite\Models" -Recurse -Force

# 3. 制作标准版（含 Models/）
Copy-Item "$env:LOCALAPPDATA\TrueToneCap\onnx_models" "$publishDir\Models" -Recurse

# 4. 打包
Compress-Archive -Path "$publishDir\*" -DestinationPath "publish\TrueToneCap-v0.1.5-beta-win-x64.zip"
Compress-Archive -Path "publish\TrueToneCap-v0.1.5-beta-lite\*" -DestinationPath "publish\TrueToneCap-v0.1.5-beta-win-x64-lite.zip"
```

---

## 三、依赖说明 / Dependencies

### 运行时依赖（已内嵌，无需安装）

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET 10 Runtime | 10.0.x | Self-contained 内嵌 (~80 MB) |
| Windows App SDK | 1.6.250205002 | 4 个 MSIX 框架包内嵌 (~40 MB) |
| WinUI 3 | 3.1.6 | 内嵌在 Microsoft.UI.Xaml.dll |
| ONNX Runtime | 1.20.0 | DirectML 后端，OCR 推理 |
| ImageMagick (Magick.NET) | 14.14.0 Q16-HDRI | 多格式图像编码 |
| Win2D | 1.3.1 | GPU 加速 2D 渲染 |
| Vortice (DirectX) | 3.8.3 | D3D11/D3D12/DXGI 底层绑定 |

### 系统要求

| 要求 | 最低 | 推荐 |
|------|------|------|
| **操作系统** | Windows 11 24H2 (Build 26100) | 最新 Windows 11 |
| **架构** | x64 | x64 |
| **GPU** | 任何支持 D3D11 的 GPU | D3D12 + DirectML 兼容 GPU |
| **显示器** | SDR | HDR (BT.2020 / Display P3) |
| **内存** | 4 GB | 8 GB+ |
| **磁盘** | 500 MB (精简) / 700 MB (标准) | SSD |

---

## 四、字体说明 / Fonts

TrueToneCap 默认使用 **微软雅黑** 作为 UI 字体，并支持加载自定义字体。

### HarmonyOS Sans SC（鸿蒙黑体）

由于版权限制，鸿蒙黑体不能直接包含在发布包中。

**安装方法**：
1. 访问 [HarmonyOS Design](https://developer.harmonyos.com/cn/design/resource/)
2. 下载 HarmonyOS Sans 字体包
3. 将 `HarmonyOS_Sans_SC_Regular.ttf` 和 `HarmonyOS_Sans_SC_Bold.ttf` 复制到 `Fonts/` 目录
4. 重启 TrueToneCap，自动加载并在 UI 中使用

字体在 TrueToneCap 进程内加载，不会安装到系统字体目录。

---

## 五、OCR 模型说明 / OCR Models

### 模型列表

| 文件 | 大小 | 用途 |
|------|------|------|
| `ch_PP-OCRv4_det_server_infer.onnx` | 108 MB | 文字检测（高精度 FP32） |
| `ch_PP-OCRv4_rec_server_infer.onnx` | 86 MB | 文字识别（高精度 FP32） |
| `ppocr_keys_v1.txt` | 26 KB | 中英文字典 (6625 字符) |

### 模型来源

基于 PaddleOCR PP-OCRv4 导出为 ONNX 格式，支持中文 + 英文混合识别。

### 模型加载优先级

1. 首先查找 `<应用目录>\Models\` （发布包自带）
2. 回退到 `%LOCALAPPDATA%\TrueToneCap\onnx_models\` （用户自行下载）
3. 如果 ONNX 模型不可用，降级使用 Windows 内置 OCR

### 运行时自动下载

精简版用户首次使用 OCR 时，应用会提示模型缺失。可将标准版中的 `Models/` 文件夹复制到 `%LOCALAPPDATA%\TrueToneCap\onnx_models\`。

---

## 六、更新检查清单 / Release Checklist

- [ ] `csproj` 版本号更新（Version / AssemblyVersion / FileVersion）
- [ ] `Publish.ps1` 输出目录更新
- [ ] `README.md` 版本号和更新日志更新
- [ ] 编译通过 `dotnet build -c Release -r win-x64`
- [ ] 运行 `.\Publish.ps1` 生成发布包
- [ ] 复制 OCR 模型到 `Models/`
- [ ] 生成精简版（不含 `Models/`）
- [ ] 打包 ZIP
- [ ] 验证 ZIP 解压后可直接运行 `TrueToneCap.exe`
- [ ] 检查 `Fonts/README_FONTS.txt` 包含字体引导
- [ ] 检查 `Models/` 包含 3 个 OCR 文件（标准版）
