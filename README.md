# 真色截图（TrueToneCap）— HDR现代化截图工具 / HDR Modern Screenshot Tool

> **v0.1 Beta** · Windows 11 24H2+ · C# 13 · WinUI 3 · .NET 10 · DXGI/DirectX

<br/>

**English** — TrueToneCap is a scalpel made for pixels. In an era where HDR displays are everywhere but screenshot tools remain stuck in SDR, TrueToneCap takes a different path: it captures your display's Float16 framebuffer directly from DXGI, preserving every nit of light, then gracefully tone-maps it into what your eyes can see with GPU shaders. Selection, annotation, OCR, and translation all happen on a full-screen overlay in real time — no popups, no context switches. Export to PNG (mathematically lossless), JPEG Gain Map (Ultra HDR compatible), AVIF, JPEG XL, and more. Translation engine features Youdao + Google multi-endpoint fallback with optional LLM backend.

**中文** — TrueToneCap 是一把为像素而生的手术刀。HDR 显示器早已普及，截图工具却仍停留在 SDR 时代——TrueToneCap 走了另一条路：从 DXGI 底层直接捕获 Float16 浮点帧缓冲，完整保留每一尼特的光照信息，再通过 GPU 着色器将 HDR 优雅地映射到人眼可视范围。框选、标注、OCR、翻译——全部在按下快捷键后的全屏覆盖层上实时完成，无需弹窗，无需跳转。导出格式涵盖 PNG（数学无损）、JPEG Gain Map（Ultra HDR 兼容）、AVIF、JPEG XL 等。翻译引擎内置有道 + Google 多端点自动降级，LLM 后端可按需接入。

这是 0.1 测试版，功能在快速迭代中。欢迎反馈。 / This is v0.1 beta — rapidly iterating. Feedback welcome.

---

## Features / 功能亮点

| Module / 模块 | Capability / 能力 |
|---------------|-------------------|
| 📷 **Capture / 屏幕捕获** | DXGI Desktop Duplication, Float16 HDR / BGRA8 SDR, GDI fallback / DXGI 桌面复制, Float16 HDR / BGRA8 SDR, GDI 回退 |
| 🎨 **Color / 色彩管理** | Display ICC detection & baking, ACM adaptation, BT.2020 / sRGB gamut / 显示器 ICC 检测与烘焙, ACM 适配, BT.2020 / sRGB 色域 |
| ✏️ **Annotation / 矢量标注** | Rect, Ellipse, Arrow, Pen, Text, Mosaic — full-screen overlay real-time editing / 矩形/椭圆/箭头/画笔/文字/马赛克, 全屏覆盖层实时编辑 |
| 🖼️ **Export / 多格式导出** | AVIF (HDR) · JPEG XL (HDR) · JPEG Gain Map (Ultra HDR) · PNG · WebP · JPEG LI · BMP |
| 🔤 **OCR / 文字识别** | Windows built-in OCR engine, multi-language / Windows 内置 OCR 引擎, 多语言支持 |
| 🌐 **Translate / 翻译** | Youdao + Google multi-endpoint fallback + configurable LLM (OpenAI/DeepSeek compatible) / 有道 + Google 多端点降级 + 可配置 LLM |
| 🎬 **Recording / 动图录制** | WebP / APNG / AVIF / GIF, ring buffer + frame diff detection / 环形缓冲 + 帧差检测 |
| ⌨️ **Hotkeys / 全局热键** | Customizable screenshot/record hotkeys, tray background running / 可自定义截图/录制快捷键, 托盘后台运行 |

## Quick Start / 快速开始

### Installation / 安装运行

1. Download `TrueToneCap-v0.1-beta-win-x64.zip`, extract to any folder / 下载 zip 并解压到任意目录
2. Double-click `TrueToneCap.exe` / 双击 `TrueToneCap.exe`

> **🎉 Zero dependencies / 零依赖开箱即用**：.NET 10 runtime + Windows App Runtime 1.6 are embedded in the app directory and auto-deployed on first launch. No manual installation needed. / .NET 10 运行时 + Windows App Runtime 1.6 均已嵌入程序目录, 首次启动时自动部署, 无需手动安装。

### System Requirements / 系统要求

- **Windows 11** 24H2 (build 26100) or later. Windows 10 and older Windows 11 builds may also work but are untested. / Windows 11 24H2 (build 26100) 或更高。Windows 10 及旧版 Windows 11 理论上也可运行, 但未经充分测试。
- HDR-capable display is optional (SDR mode works perfectly) / 支持 HDR 的显示器可选（SDR 模式正常使用）

### Developer Build / 开发者构建

```powershell
git clone https://github.com/your-org/TrueToneCap.git
cd TrueToneCap

# Restore dependencies / 还原依赖
dotnet restore

# Compile shaders (requires dxc.exe, included in Windows SDK) / 编译着色器
.\shaders\CompileShaders.ps1

# Build & run / 构建 & 运行
dotnet run --project src\TrueToneCap.App -c Release
```

### Usage / 使用

1. App lives in system tray after launch / 启动后软件驻留系统托盘
2. Press `Ctrl+Shift+S` (default hotkey) to capture / 按默认热键触发截图
3. Drag to select region → toolbar appears / 拖拽框选区域 → 工具栏出现:
   - **Save / 保存** → Encode, save & copy to clipboard / 编码保存 + 复制到剪贴板
   - **Annotate / 标注** → Draw directly on selection (rect, arrow, mosaic, etc.) / 选区上直接绘图
   - **Copy / 复制** → Copy image to clipboard / 直接复制图像
   - **OCR / 识字** → Extract text from selection / 提取选区文字
   - **Translate / 翻译** → Recognize & translate text / 识别并翻译
4. `Enter` to save, `Esc` to cancel / `Enter` 保存, `Esc` 取消

## Project Structure / 项目结构

```
TrueToneCap/
├── docs/
│   └── architecture-design.md    # Architecture documentation / 架构设计文档
├── shaders/
│   ├── ToneMapping.hlsl          # HDR→SDR tone mapping / 色调映射着色器
│   └── MosaicEffect.hlsl         # Mosaic effect / 马赛克效果着色器
├── src/
│   ├── TrueToneCap.Core/         # Core library / 核心类库
│   │   ├── Capture/              # Screen capture (DXGI/D3D11) / 屏幕捕获
│   │   ├── Annotation/           # Vector annotation engine / 矢量标注引擎
│   │   ├── Encoding/             # Multi-format encoders (Magick.NET) / 多格式编码器
│   │   ├── ColorManagement/      # Color management (ICC/ACM) / 色彩管理
│   │   ├── Processing/           # GPU tone mapping / GPU 色调映射
│   │   └── Services/             # OCR / Translation services / OCR 翻译服务
│   ├── TrueToneCap.App/          # WinUI 3 desktop app / WinUI 3 桌面应用
│   └── TrueToneCap.Test/         # Test project / 测试项目
├── tools/
│   └── DiagCapture/              # Diagnostic tool / 诊断工具
└── Publish.ps1                   # One-click publish script / 一键发布脚本
```

## Output Formats / 输出格式

| Format / 格式 | HDR | Bit Depth / 位深 | Default Quality / 默认质量 | Encoder / 编码引擎 |
|---------------|-----|-------------------|---------------------------|---------------------|
| PNG | ✅ cICP | 8/16-bit | Lossless / 无损 | Magick.NET |
| JPEG Gain Map | ✅ Ultra HDR | 8-bit + gain map | Dist 1.0 / Gain 85% / 距离 1.0 / 增益 85% | Magick.NET + MPF |
| JPEG XL | ✅ Native / 原生 | Float/16-bit | Visually lossless 0.8 / 视觉无损 | Magick.NET (libjxl) |
| AVIF | ✅ Native / 原生 | 10/12-bit | CRF 18 | libaom / QSV / NVENC |
| WebP | ❌ | 8-bit | 92% | Magick.NET |
| JPEG LI | ❌ | 8-bit | Dist 1.0 / 距离 1.0 | Magick.NET (jpegli) |
| BMP | ❌ | 8-bit | Lossless / 无损 | Magick.NET |

## Dependencies / 主要依赖

| Package / 包 | Purpose / 用途 |
|-------------|----------------|
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | DirectX managed bindings (DXGI/D3D11/D3D12) / DirectX 托管绑定 |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | Image encoding/decoding (PNG/JXL/AVIF/WebP) / 图像编解码 |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 framework / WinUI 3 框架 |
| [Microsoft.Graphics.Win2D](https://github.com/microsoft/Win2D) | Direct2D Canvas wrapper / Direct2D Canvas 封装 |

---

## Changelog / 版本日志

### v0.1 Beta — 2026-07-04

**First public beta release. / 首个公开测试版。**

#### 🎯 Core / 核心功能
- DXGI Desktop Duplication capture with Float16 HDR support / DXGI 桌面复制, Float16 HDR 支持
- GPU tone mapping via HLSL pixel shaders (Hable Filmic) / GPU 色调映射 (HLSL 像素着色器)
- GDI fallback for locked screens / UAC / GDI 回退 (锁屏/UAC 场景)
- Per-monitor DPI awareness (PerMonitorV2) / 逐显示器 DPI 感知
- Screen-coordinate-accurate selection with DPI-scale correction / DPI 缩放校正的屏幕坐标精确选区

#### ✏️ Annotation / 标注
- Full-screen overlay annotation: Rect, Ellipse, Arrow, Pen, Text, Mosaic / 全屏覆盖层标注
- Infinite undo/redo via command pattern / 命令模式无限撤销/重做
- Real-time preview during drawing / 绘制时实时预览
- Physical-pixel-accurate coordinate mapping / 物理像素精确坐标映射

#### 🖼️ Export / 导出
- **PNG**: Mathematically lossless, 8/16-bit, cICP HDR metadata / 数学无损 HDR
- **JPEG Gain Map**: ISO 21496-1 Ultra HDR, MPF + XMP container, Gray/RGB dual-mode gain map / Ultra HDR 双模增益图
- **JPEG XL**: Visually lossless (butteraugli dist 0.8), float/16-bit HDR, effort=7 / 视觉无损
- **AVIF**: CRF 18 default, libaom / Intel QSV / NVIDIA NVENC auto-select, chroma 444 / 三后端
- **WebP**: 92% quality, method=6, alpha-quality=100 / 视觉近无损
- **JPEG LI**: butteraugli distance 1.0, DCT float precision / DCT 浮点精度
- Clipboard: `SetStorageItems` (file paste) for WeChat/QQ/Explorer compatibility / 文件粘贴兼容

#### 🔤 OCR & Translation / 识别与翻译
- Windows built-in OCR engine, multi-language / Windows 内置 OCR
- Youdao Translate (primary, China-friendly, free, no API key) / 有道翻译 (国内首选)
- Google Translate multi-endpoint auto-fallback (gtx, chrome-ex) / Google 多端点降级
- Configurable LLM backend (OpenAI/DeepSeek compatible API) / 可配置 LLM

#### 🎬 Recording / 动图录制
- WebP / APNG / AVIF / GIF animation / 动画录制
- Ring buffer + frame diff detection / 环形缓冲 + 帧差检测

#### 🎨 Color / 色彩管理
- Display ICC profile detection & sRGB baking / 显示器 ICC 检测与烘焙
- ACM (Auto Color Management) detection / ACM 检测
- BT.2020 / sRGB / Display P3 / Adobe RGB color space / 多色彩空间

#### 🛠️ Engineering / 工程
- .NET 10 self-contained publish (zero dependencies) / 自包含发布 (零依赖)
- Windows App Runtime 1.6 MSIX auto-deployment / 框架自动部署
- `Publish.ps1` one-click release script / 一键发布脚本
- Apache 2.0 License / Apache 2.0 开源许可证

#### ⚠️ Known Issues / 已知问题
- JPEG Gain Map compatibility depends on viewer (Chrome 124+, Google Photos) / JPEG Gain Map 需新版查看器
- Animation recording UI not yet integrated into selection overlay / 动图录制界面尚未集成
- HDR capture may fail on some dual-GPU laptops / 部分双显卡笔记本 HDR 捕获可能失败
- Youdao Translate signature key may expire and require update / 有道签名 Key 可能过期需更新

---

## License / 许可证

Licensed under **Apache License 2.0**. See [LICENSE](./LICENSE) for full text. / 基于 **Apache License 2.0** 开源。详见 [LICENSE](./LICENSE)。

---

**TrueToneCap** — Every frame deserves to be remembered. / 让每一帧都值得被记住。
