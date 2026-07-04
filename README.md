# TrueToneCap — HDR 截图与标注工具

> **v0.1 Beta** · Windows 11 24H2+ · C# 13 · WinUI 3 · .NET 10 · DXGI/DirectX

<br/>

**TrueToneCap** 是一把为像素而生的手术刀。

在这个 HDR 显示器逐渐普及、但截图工具仍停留在 SDR 时代的间隙里，TrueToneCap 选择了不同的路——从 DXGI 底层直接捕获显示器的 Float16 浮点帧缓冲，保留每一尼特的光照信息，再用 GPU 色调映射将其优雅地落入人眼可见的范围。

框选、标注、OCR、翻译——所有操作都在按下快捷键后的全屏覆盖层上实时完成，无需弹窗、无需跳转。截图可以导出为 PNG（数学无损）、JPEG Gain Map（兼容 Ultra HDR）、AVIF、JPEG XL 等格式。对于国内用户，翻译引擎内置了有道 + Google 多端点自动降级，LLM 也可按需接入。

这是 0.1 测试版，功能在快速迭代中。欢迎反馈。

---

## 功能亮点

| 模块 | 能力 |
|------|------|
| 📷 **屏幕捕获** | DXGI Desktop Duplication，Float16 HDR / BGRA8 SDR，GDI 回退 |
| 🎨 **色彩管理** | 显示器 ICC 检测与烘焙、ACM 适配、BT.2020 / sRGB 色域 |
| ✏️ **矢量标注** | 矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克，全屏覆盖层实时编辑 |
| 🖼️ **多格式导出** | AVIF (HDR) · JPEG XL (HDR) · JPEG Gain Map (Ultra HDR) · PNG · WebP · JPEG LI |
| 🔤 **OCR 识别** | Windows 内置 OCR 引擎，多语言支持 |
| 🌐 **翻译** | 有道 + Google 多端点降级 + 可配置 LLM（OpenAI/DeepSeek 兼容） |
| 🎬 **动图录制** | WebP / APNG / AVIF / GIF，环形缓冲 + 帧差检测 |
| ⌨️ **全局热键** | 可自定义截图/录制快捷键，托盘后台运行 |

## 快速开始

### 系统要求

- **Windows 11** 24H2 (build 26100) 或更高
- **.NET 10** Runtime / SDK
- 支持 HDR 的显示器（可选，SDR 模式正常使用）

### 构建

```powershell
git clone https://github.com/your-org/TrueToneCap.git
cd TrueToneCap

# 还原依赖
dotnet restore

# 编译着色器（需 dxc.exe，Windows SDK 自带）
.\shaders\CompileShaders.ps1

# 构建 & 运行
dotnet run --project src\TrueToneCap.App -c Release
```

### 使用

1. 启动后软件驻留系统托盘
2. 按 `Ctrl+Shift+S`（默认热键）触发截图
3. 拖拽框选区域 → 工具栏出现：
   - **保存** → 编码保存 + 复制到剪贴板
   - **标注** → 在选区上直接绘图（矩形/箭头/马赛克等）
   - **复制** → 直接复制图像到剪贴板
   - **识字** → OCR 提取选区文字
   - **翻译** → 识别并翻译文字
4. `Enter` 保存，`Esc` 取消

## 项目结构

```
TrueToneCap/
├── docs/
│   └── architecture-design.md    # 完整架构文档
├── shaders/
│   ├── ToneMapping.hlsl          # HDR→SDR 色调映射
│   └── MosaicEffect.hlsl         # 马赛克效果
├── src/
│   ├── TrueToneCap.Core/         # 核心类库
│   │   ├── Capture/              # 屏幕捕获 (DXGI/D3D11)
│   │   ├── Annotation/           # 矢量标注引擎
│   │   ├── Encoding/             # 多格式编码器 (Magick.NET)
│   │   ├── ColorManagement/      # 色彩管理 (ICC/ACM)
│   │   ├── Processing/           # GPU 色调映射
│   │   └── Services/             # OCR / 翻译服务
│   ├── TrueToneCap.App/          # WinUI 3 桌面应用
│   └── TrueToneCap.Test/         # 测试项目
└── tools/
    └── DiagCapture/              # 诊断工具
```

## 输出格式

| 格式 | HDR | 位深 | 默认质量 | 编码引擎 |
|------|-----|------|----------|----------|
| PNG | ✅ cICP | 8/16-bit | 无损 | Magick.NET |
| JPEG Gain Map | ✅ Ultra HDR | 8-bit + 增益图 | 距离 1.0 / 增益图 85% | Magick.NET + MPF |
| JPEG XL | ✅ 原生 | 浮点/16-bit | 视觉无损 (0.8) | Magick.NET (libjxl) |
| AVIF | ✅ 原生 | 10/12-bit | CRF 18 | libaom / QSV / NVENC |
| WebP | ❌ | 8-bit | 92% | Magick.NET |
| JPEG LI | ❌ | 8-bit | 距离 1.0 | Magick.NET (jpegli) |
| BMP | ❌ | 8-bit | 无损 | Magick.NET |

## 主要依赖

| 包 | 用途 |
|----|------|
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | DirectX 托管绑定 (DXGI/D3D11/D3D12) |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | 图像编解码 (PNG/JXL/AVIF/WebP) |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 框架 |
| [Microsoft.Graphics.Win2D](https://github.com/microsoft/Win2D) | Direct2D Canvas 封装 |

## 许可证

本项目基于 **Apache License 2.0** 开源。详见 [LICENSE](./LICENSE)。

---

**TrueToneCap** — 让每一帧都值得被记住。
