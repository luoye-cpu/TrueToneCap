# TTC 的 VS-Cap 模式

2026-10-11，本地移植。TTC 基线为 `luoye-cpu/TrueToneCap@v0.3.3-beta`（v0.3.3-beta），代码、模型和原生工具已拉齐。

## 使用

“截图与录制”中的“VS-Cap 模式”默认关闭，开启后区域截图捕获鼠标所在屏幕，再使用 VS-Cap 选区、绘图和文字工具。TTC 原模式保留多显示器、OCR、翻译等入口。无感截图与录制仍使用原入口。

剪贴板贴图按钮与 `Ctrl+Alt+T` 可直接建立置顶贴图。快捷键注册冲突时状态栏提示，仍可使用按钮。贴图隐藏工具栏时可拖动，滚轮等比最近邻缩放；右键支持显示 / 隐藏工具栏、复制、保存 PNG / AVIF 与销毁。文字与绘图共用撤销历史，导出保持原尺寸。

截图“✓”沿用 VS-Cap 的图像 / PNG / scRGB 剪贴板处理，同时交给 TTC 保存 PNG。贴图“✓”只隐藏工具栏，贴图右键复制保留 VS-Cap 原行为。保存按钮仍弹出文件选择框；实际 PNG / AVIF 编码调用 TTC 的 `CapturePipelineService`，质量、位深、色域、ICC、HDR 及 AVIF 后端取对应 TTC 设置。显式保存为 AVIF 使用 `.avif`，不附加 TTC 的兼容 `.png` 后缀。

## 最小移植范围

- `src/TrueToneCap.VSCap/upstream/` 保存 VS-Cap `7042772f009e4fcfbbf2e570919ce9224b066e41` 的 `src/cap/main.cpp`、`DesktopCapture.cpp`、`DesktopCapture.h` 与 `TestCapWorkflow.cpp`，SHA-256 均与来源逐字一致，保留 MIT 许可。
- `src/TrueToneCap.VSCap/main.cpp` 只接收 TTC 像素、建立原编辑 / 贴图窗口、回传导出请求和管理进程生命周期。原独立托盘入口重命名后不运行。窗口全部关闭后保留 600 ms，允许原剪贴板重试完成；TTC 退出后接入进程随之退出。
- `VSCapFrame` 使用小端 TTC1 文件交换 BGRA8 / RGBA float32，不先编码 PNG，不量化 HDR 浮点。物理屏幕原点用于匹配 QScreen，缩放、裁剪与文字坐标仍由原编辑器处理。
- `VSCapEditor` 观察接入程序输出；选区窗口结束就释放截图锁，现存贴图不阻止新截图。临时交换文件在导出读取和进程结束后清理。
- `MainWindow.VSCap.cs` 调用原 WGC 单显示器捕获与编码服务。主窗口仅加模式开关、贴图入口、热键和路由。`TrueToneCap.Core`、`WgcCaptureService`、`CapturePipelineService`、原标注光栅化及色彩处理没有改动。
- `CompileShaders.ps1` 只补 UTF-8 BOM；原脚本在 PowerShell 5.1 中把中文字符串按 ANSI 解码，基线构建失败，BOM 修复后 HLSL 编译成功。

## 构建

先使用 Qt 6.8+ / MinGW 编译并收集接入程序，再使用 .NET 11 SDK 构建 TTC。不要把 FFmpeg 的 MinGW DLL 混进 Qt 接入程序目录。

```powershell
.\scripts\BuildVSCap.ps1 -QtRoot 'C:\Private\Soft\QT\6.10.2\mingw_64' -ToolchainBin 'C:\Private\Soft\QT\Tools\mingw1310_64\bin' -CMake 'C:\Private\Soft\QT\Tools\CMake_64\bin\cmake.exe' -Ninja 'C:\Private\Soft\QT\Tools\Ninja\ninja.exe'
dotnet publish src\TrueToneCap.App\TrueToneCap.App.csproj -c Release -p:Platform=x64 -o publish\local-test
```

`build/vscap-runtime/` 自动通过 csproj 收集到 TTC 的 `data/vscap/`，Qt DLL、平台插件、MinGW DLL 和许可随程序一起部署。正式 TTC 发布仍使用原 `Publish.ps1` 收集 `publish/PLAN` 的编码器与模型；需先执行 `BuildVSCap.ps1`。本轮 `publish/local-test/` 已补齐相同的原生工具与模型，可直接运行。

## 验证

本机 Windows 11、Qt 6.10.2 / MinGW 13.1、.NET 11.0.100-rc.1.26425.128、150% DPI，实际桌面为 SDR。

- TTC 构建 / 发布成功；保留上游 `LogService.cs` 的一个 `CsWinRT1032` 警告，不改无关代码。
- TTC 核心测试：47 passed，0 failed，日志 `build/ttc-core-tests.txt`。
- 原编辑工作流：11 passed，0 failed，日志 `build/vscap-editor-workflow.txt`；覆盖真实桌面像素、选区、文字、绘图、撤销、工具参数、贴图缩放 / 编辑 / 菜单 / 边框和 HDR 剪贴板。
- 新接入测试：5 passed，0 failed，日志 `build/vscap-bridge-tests.txt`；覆盖无效输入、C#→Qt 精确 BGRA / alpha / HDR 数据、HDR 编辑导出与撤销。C# 再读取 Qt 输出，确认负值、高光与透明度逐样本一致。
- TTC 设置页的模式开关和保存，以及 WGC 2560×1600 捕获进入接入程序已观察；用户随后明确确认“手动测试已经没问题了”，据此结束界面操作。
- 本机没有实际 HDR 屏幕；浮点数据测试不等于实际 HDR 显示器验收。混合 DPI 多显示器尚未实测。
- 带入的原 `realHdrAvif` 测试依赖 VS-Cap 的 FFmpeg 导出。本接入使用 TTC 编码器，该测试不列入上述 11 项；本机单独尝试因 FFmpeg 与 Qt 的 MinGW DLL 冲突失败，未修改原测试或编码算法掩盖失败。

C# / Qt 交接测试命令：先运行 `dotnet run --project tests/VSCapInterop -- --write-fixtures build/vscap-fixtures`，设置 `TTC_VSCAP_FIXTURES` 为该目录绝对路径，运行 `build/vscap/vscap-bridge-tests.exe`，最后运行 `dotnet run --project tests/VSCapInterop -- --verify-qt build/vscap-fixtures`。
