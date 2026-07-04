# TrueToneCap / 真色截图

> **v0.1.5 Beta** · Windows 11 24H2+ · WinUI 3 · .NET 10 · DXGI

TrueToneCap 是一把为像素而生的手术刀。

在这个 HDR 显示器逐渐普及、但截图工具仍停留在 SDR 时代的间隙里，TrueToneCap 选择了不同的路——从 DXGI 底层直接捕获显示器的 Float16 浮点帧缓冲，保留每一尼特的光照信息，再用 GPU 色调映射将其优雅地落入人眼可见的范围。

框选、标注、OCR、翻译——所有操作都在按下快捷键后的全屏覆盖层上实时完成，无需弹窗、无需跳转。截图可以导出为 PNG（数学无损）、JPEG Gain Map（兼容 Ultra HDR）、AVIF、JPEG XL 等格式。对于国内用户，翻译引擎内置了有道 + Google 多端点自动降级，LLM 也可按需接入。

---

TrueToneCap is a scalpel built for pixels.

In the gap where HDR displays are becoming ubiquitous yet screenshot tools remain stuck in the SDR era, TrueToneCap takes a different path — capturing the display's Float16 framebuffer directly from the DXGI back-end, preserving every nit of luminance, then gracefully tone-mapping it into the visible range via GPU shaders.

Selection, annotation, OCR, translation — everything happens in real-time on a full-screen overlay after pressing a hotkey, with no pop-ups and no context switches. Screenshots export to PNG (mathematically lossless), JPEG Gain Map (Ultra HDR compatible), AVIF, JPEG XL, and more. For users in China, the translation engine includes Youdao + Google multi-endpoint automatic fallback, with custom LLM (OpenAI/DeepSeek) available on demand.

---
---

## Quick Start / 快速开始

1. Download `TrueToneCap-v0.1.5-beta-win-x64.zip`, extract / 下载解压
2. Run `TrueToneCap.exe` / 双击运行
3. Press `Ctrl+Shift+S` to capture / 按快捷键截图

> **System / 系统**: Windows 11 24H2+ · HDR display optional / HDR 显示器可选

---

## Features / 功能

| Feature | Detail |
|---------|--------|
| 📷 Capture / 捕获 | DXGI Desktop Duplication, Float16 HDR / BGRA8 SDR, GDI fallback |
| 🎨 Color / 色彩 | ICC detection & baking, BT.2020 / sRGB / Display P3, ACM |
| ✏️ Annotate / 标注 | Rect, Ellipse, Arrow, Pen, Text, Mosaic — full-screen overlay |
| 🖼️ Export / 导出 | PNG (lossless HDR) · JPEG Gain Map (Ultra HDR) · JPEG XL · AVIF (QSV/NVENC) · WebP · JPEG LI · BMP |
| 🔤 OCR / 识字 | Windows OCR + preprocess (contrast / scale-up / threshold) / 预处理管线 |
| 🌐 Translate / 翻译 | Youdao → Google multi-endpoint → custom LLM (OpenAI/DeepSeek) |
| ⌨️ Hotkeys / 热键 | Recordable / 可录制 · tray minimize / 托盘 · autostart / 开机启动 |

---

## Output Formats / 输出格式

| Format | HDR | Bit Depth | Encoder |
|--------|-----|-----------|---------|
| PNG | ✅ cICP | 8/16-bit | Magick.NET |
| JPEG Gain Map | ✅ Ultra HDR | 8-bit + gain map | Magick.NET + MPF |
| JPEG XL | ✅ | Float/16-bit | libjxl |
| AVIF | ✅ | 10/12-bit | libaom / QSV / NVENC |
| WebP | ❌ | 8-bit | Magick.NET |
| JPEG LI | ❌ | 8-bit | jpegli |
| BMP | ❌ | 8-bit | Magick.NET |

---

## Build / 构建

```powershell
dotnet restore
dotnet run --project src\TrueToneCap.App -c Release
.\Publish.ps1   # one-click publish / 一键发布
```

---

## Changelog / 更新日志

### v0.1.5 — 2026-07-05

- 🎨 Theme: 完整重写主题系统 — 浅色/深色/OLED/跟随系统四种模式全面可用 / Complete theme system rewrite — Light/Dark/OLED/Follow-system all fully functional
- 🖼️ Window: 默认窗口尺寸增大至 1260×840 / Default window enlarged to 1260×840
- 🏷️ TitleBar: 深色模式标题栏扩展消除顶部白条 / Title bar extended into content to eliminate white bar in dark mode
- 🛠️ Toolbar: 截图预览工具栏深色主题适配（硬编码色 → 主题感知） / Screenshot toolbar dark theme adaptation (hardcoded → theme-aware)
- 🧹 Deps: Magick.NET 14.5.0 → 14.14.0 (消除 500+ 安全漏洞警告) / Magick.NET 14.5.0 → 14.14.0 (eliminated 500+ security vulnerability warnings)
- 🔧 Fix: WinUI 3 `RequestedTheme` 启动崩溃修复 / WinUI 3 `RequestedTheme` startup crash fix
- 🔧 Fix: HotkeyManager 窗口子类化失败保护 / HotkeyManager window subclassing failure protection
- 🔧 Fix: TrayIcon/StartupManager 初始化异常防御 / TrayIcon/StartupManager init exception guarding
- 📐 Layout: OCR 引擎选项移至 AI 翻译面板 / OCR engine option moved to AI translation panel

### v0.1.4 — 2026-07-05

- 🖥️ Window: `ExtendsContentIntoTitleBar` 消除深色模式顶部白条 / Eliminate white bar in dark mode title bar
- 🎨 Theme: App.xaml ThemeDictionaries 标准化 (Default/Light/Dark) / Standardized ThemeDictionaries
- 🖊️ Overlay: SelectionOverlay 工具栏主题感知化 / SelectionOverlay toolbar theme-aware
- 📦 OCR: ONNX DirectML/CPU 引擎诊断与验证 / ONNX DirectML/CPU engine diagnostics & validation

### v0.1.3 — 2026-07-05

- 🔧 DPI: `SetProcessDpiAwarenessContext` 强制 PerMonitorV2 修复窗口模糊
- 🔧 Font: `ThemeDictionaries` 覆盖 WinUI 控件模板字体 / 微软雅黑优先
- 🔧 Close: `AppWindow.Closing` 替代不可取消的 `Window.Closed`
- 🔧 Hotkey: `GCHandle` 防 GC 闪退 + 单次窗口子类化
- 🔧 OCR: 双 Pass 预处理管线（对比度增强 / 放大 / 自适应二值化）
- ✨ Hotkey record button / 快捷键录制按钮 — 按键即录即时生效

### v0.1.2 — 2026-07-04

- 🔤 HarmonyOS Sans SC 内嵌 / 回落微软雅黑
- 🚀 开机静默托盘 (`--autostart`)
- 🎨 Gain Map 灰度/RGB 双模 + ICC 检测修正
- 🖥️ AVIF 10-bit / JXL 16-bit / PNG 16-bit 高色深保护

### v0.1.1 — 2026-07-04

- 🖼️ JPEG Gain Map (Ultra HDR) + 文件归档
- 🌐 有道 + Google 翻译多端点降级
- 🖊️ 全屏覆盖层标注 + 剪贴板文件粘贴

---

## License / 许可证

Apache 2.0 · [LICENSE](./LICENSE)

**TrueToneCap** — Every frame deserves to be remembered. / 让每一帧都值得被记住。
