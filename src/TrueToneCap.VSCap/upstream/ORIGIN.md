# VS-Cap 源码来源

来源：[user-Wing/VS-Renderer-GUI](https://github.com/user-Wing/VS-Renderer-GUI)，提交 `7042772f009e4fcfbbf2e570919ce9224b066e41`，版本 1.0.10。

`src/cap/main.cpp`、`DesktopCapture.cpp`、`DesktopCapture.h`、`tests/TestCapWorkflow.cpp` 逐字复制，保留 MIT 许可。接入代码放在上级目录，不修改上游绘图、文字、历史、贴图、缩放或像素处理。

TTC 自己执行 WGC 捕获和最终编码；VS-Cap 原独立托盘入口编译时重命名，不运行，不注册第二套截图热键。
