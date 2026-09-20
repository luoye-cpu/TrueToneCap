# TrueToneCap 分发打包说明 / Distribution Packaging Guide

> v0.3.1 Beta · 2026-08-10
>
> ⚠ **本文整体已过时（2026-09-20 整洁审计，`docs-staleness-audit-2026-09-14.md:20` 早判 🔴 未修）**：
> 版本停在 v0.3.1-beta，而现行版本是 0.3.4-beta（也未打 tag）；下面引用的四个分发件名
> （`TrueToneCap-v0.3.1-beta/`、`-lite/`、`-win-x64.zip`、`-win-x64-lite.zip`）**都已不存在**——
> 现行打包链路是 `Publish.ps1`（本地测试包另有 `scripts/publish-with-prebuilt.ps1`），产物为目录 + `.7z`
> + `SHA256SUMS.txt` + `build-info.json`，`publish/` 整体被 .gitignore 覆盖（仓库里只剩本文与该目录）。
> 要发布/复现请读 `Publish.ps1` 的参数与本仓 `CHANGELOG.md` 的发布小节，别照本文的手工步骤做。


---

## 一、发布包结构 / Package Structure

```
TrueToneCap-v0.3.1-beta-win-x64/
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
├── native/                     # 原生工具链（Publish.ps1 [3/6] 从 Core/Resources/ 复制，详见 §六）
│   ├── avifenc.exe             # AVIF 编码器 (~16 MB；自编译的 libavif 1.4.2，含 aom + SVT-AV1，配方见 scripts/build-avifenc.py)
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
| **标准版** | `TrueToneCap-v0.3.1-beta-win-x64.zip` | ~550 MB | 包含 OCR 模型，开箱即用识字/翻译 |
| **精简版** | `TrueToneCap-v0.3.1-beta-win-x64-lite.zip` | ~400 MB | 不含 OCR 模型，需联网下载或仅用 Windows OCR |

### 制作方法

```powershell
# 1. 运行发布脚本（自动编译 + 从单一事实源复制工具链与模型）
.\Publish.ps1 -Configuration Release -Runtime win-x64

# 2. 制作精简版（不含 data/Models/）
$publishDir = "publish\TrueToneCap-v0.3.1-beta"
$liteDir = "publish\TrueToneCap-v0.3.1-beta-lite"
Copy-Item $publishDir $liteDir -Recurse
Remove-Item "$liteDir\data\Models" -Recurse -Force -ErrorAction SilentlyContinue

# 3. 打包标准版（含 OCR 模型）
Compress-Archive -Path "$publishDir\*" -DestinationPath "publish\TrueToneCap-v0.3.1-beta-win-x64.zip"

# 4. 打包精简版（不含 OCR 模型）
Compress-Archive -Path "$liteDir\*" -DestinationPath "publish\TrueToneCap-v0.3.1-beta-win-x64-lite.zip"
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
| `PP-OCRv6_medium_det.onnx` | **29.7 MB** | 文字检测（FP16 中型） |
| `PP-OCRv6_medium_rec.onnx` | **36.6 MB** | 文字识别（FP16 中型） |
| `ppocrv6_dict.txt` | **73 KB** (74947 B) | 中英文字典 |

> **2026-09-14 更正**：此前标注的 ~59 MB / ~73 MB 是 **FP32 原始模型**的体积
> （现存放于 `official_models/onnx_fp32/`，已被 `.gitignore` 排除、不入库）。
> 仓库中实际打包的是经 `scripts/onnx_fp16_quantize.py` 量化后的 **FP16** 版本。

### 模型来源

基于 PaddleOCR PP-OCRv6 导出为 ONNX 格式（FP16 中型），支持中文 + 英文混合识别。
FP16 模型由 `scripts/onnx_fp16_quantize.py` 从官方 FP32 模型转换而来；
如需重新获取，可用 `download_models.ps1`（输出到 `src/TrueToneCap.App/Models/`）。

### 模型加载优先级

1. 首先查找 `<应用目录>\data\Models\` （发布包自带，由 `Publish.ps1` **[4/6] 步骤**从
   **`src/TrueToneCap.App/Models/`** 复制 —— 该目录是模型单一事实源）
2. 回退到 `%LOCALAPPDATA%\TrueToneCap\onnx_models\` （用户自行下载）
3. 如果 ONNX 模型不可用，降级使用 Windows 内置 OCR

### 设置文件位置

- **软件同目录**的 `TrueToneCap.settings.json`（默认）—— 设置随程序走，
  便于整目录拷贝的绿色 / 便携用法
- 若同目录**不可写**（例如安装到 `Program Files`、放在只读介质、以无权限账户运行），
  自动改用 `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json`
- 从旧版本升级时：若同目录尚无配置文件，会自动把旧位置的配置搬过来，**旧文件保留**以便回退
- **恢复默认设置**：删除上述 `TrueToneCap.settings.json` 后重启即可

> 崩溃哨兵文件（`encoder-crash.sentinel`）始终位于
> `%LOCALAPPDATA%\TrueToneCap\`，不随程序目录 —— 程序目录可能只读，
> 而哨兵必须"进程被杀死后仍然留下"，放在只读位置会让它彻底失效。

---

## 六、依赖来源 / Dependency Sources

> ⚠️ **2026-09-14 重要变更：本节描述的旧流程已废弃。**
>
> 早期版本把非核心运行时组件统一放在 `publish/PLAN/` 下，并要求
> `PLAN/tools/` 与 `src/TrueToneCap.Core/Resources/` **人工保持同步** —— 这带来了
> 双份人工维护的漂移隐患（见 `docs/repo-hygiene-review.md`）。
>
> **现已改为单一事实源**，`publish/PLAN/` 下的工具与模型副本**均已从版本控制移除**，
> `Publish.ps1` 不再读取该目录。

### 单一事实源（当前）

| 内容 | 单一事实源 | 发布包目标 | Publish.ps1 步骤 |
|------|-----------|-----------|:---:|
| 原生工具链 | `src/TrueToneCap.Core/Resources/` | `native/` | **[3/6]** |
| OCR ONNX 模型 | `src/TrueToneCap.App/Models/` | `data/Models/` | **[4/6]** |
| 用户字体 | `publish/PLAN/fonts/`（可选） | — | — |

> `src/TrueToneCap.Core/Resources/` 中的 exe 既嵌入 DLL 供运行时自动提取，
> **也**由 `Publish.ps1` [3/6] 直接复制进发布包 `native/` —— 两者同源，无需同步。

---

## 七、更新检查清单 / Release Checklist

- [ ] `Directory.Build.props` 版本号更新（Version / AssemblyVersion / FileVersion）
      —— 全仓库版本唯一事实源，各 csproj 不再单独声明
- [ ] `Publish.ps1` 输出目录更新
- [ ] `README.md` 版本号和更新日志更新
- [ ] `src/TrueToneCap.Core/Resources/` 与 `src/TrueToneCap.App/Models/` 内容齐全
      （`Publish.ps1` [0/6] 会校验并阻断缺失）
- [ ] 依赖变更后的 `packages.lock.json` 已重新生成并提交
      （`Publish.ps1` [0/6] 会校验锁定文件齐全性）
- [ ] 编译通过 `dotnet build -c Release -r win-x64`
- [ ] 运行 `.\Publish.ps1` 生成发布包（自动验证 PLAN 完整性）
- [ ] 检查 `native/` 包含 3 个工具（avifenc.exe, cjpegli.exe, cwebp.exe）
- [ ] 检查 `data/Models/` 包含 3 个 OCR 文件（标准版）
- [ ] 生成精简版（不含 `data/Models/`）
- [ ] 打包 ZIP
- [ ] 验证 ZIP 解压后可直接运行 `TrueToneCap.exe`
