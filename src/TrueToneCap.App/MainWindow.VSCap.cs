// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
using Microsoft.UI.Xaml;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Metadata;

namespace TrueToneCap.App;

public sealed partial class MainWindow
{
    private bool _vsCapAbsenceLogged;

    /// <summary>
    /// 「VS-Cap 模式」开关的唯一落点：入口可见性 + 全局热键登记 + 设置残留一起走。
    /// <para>两条各自记过的账：① 上游 PR 在构造函数里无条件 RegisterNamed("vscap-pin")，
    /// 功能默认关着也抢占一个系统级 Ctrl+Alt+T，与它自述的 "off by default" 相反；
    /// ② 侧车由一条 glob 进包，构建机上没有 Qt 时展开成 0 个条目 ⇒ 构建打包全绿而功能不存在，
    /// 那时还把开关摆给用户，就是给他一个点了只会报错的按钮。</para></summary>
    private void ApplyVSCapGate()
    {
        bool available = VSCapEditor.IsAvailable;
        if (!available)
        {
            // 带侧车的那枚包上开过的设置，落到没侧车的这枚包里就是残留 ⇒ 交还关闭
            _settings.UseVSCap = false;
            if (VSCapModeChk is not null) VSCapModeChk.IsChecked = false;
            if (!_vsCapAbsenceLogged)
            {
                _vsCapAbsenceLogged = true;
                LogService.Info("MainWindow",
                    "VS-Cap 侧车未随包分发（data/vscap/ttc-vscap.exe 不存在）⇒ 隐藏入口、不注册 Ctrl+Alt+T；"
                    + "要出带该功能的包先跑 scripts/BuildVSCap.ps1", LogCategory.Capture);
            }
        }
        bool on = available && _settings.UseVSCap;
        if (VSCapModeChk is not null)
            VSCapModeChk.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (VSCapPasteBtn is not null)
            VSCapPasteBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            if (!HotkeyManager.RegisterNamed(this, "vscap-pin", "Ctrl+Alt+T",
                    () => DispatcherQueue.TryEnqueue(async () => await PasteVSCapAsync())))
                SetStatus("⚠ 贴图快捷键 Ctrl+Alt+T 已被占用，请使用截图设置中的贴图按钮");
        }
        else
        {
            HotkeyManager.UnregisterNamed("vscap-pin");
        }
    }

    private async Task StartVSCapCaptureAsync()
    {
        var monitor = DisplayEnumerator.GetMonitorUnderCursor();
        using var capture = await _wgcService!.CaptureMonitorAsync(new WgcCaptureConfig
        {
            TargetMonitor = monitor, FrameTimeoutMs = 3000,
            // 源精度不在此决定：PreferHdr 恒 true，由服务侧的逐屏能力判定给不给得出 >8-bit（§4.3）。
            // PR 原文写的是 (HdrSwitch.IsOn && IsEnabled) || ShouldUseFloat16ForWideGamut()，
            // 后者在 v0.3.4 已随该不变量一起删除 —— 用输出意图决定源精度会把 10-bit 屏降成 8-bit 源。
            PreferHdr = true,
        });
        if (!string.IsNullOrEmpty(capture.DegradationWarning)) ToastService.ShowCaptureDegraded(capture.DegradationWarning);
        var display = capture.SourceDisplay ?? DisplayEnumerator.FindDisplayByMonitor(monitor);
        var frame = new VSCapFrame(capture.Width, capture.Height, capture.SdrPixels,
            Environment.GetEnvironmentVariable("TTC_FORCE_SDR") == "1" ? null : capture.HdrPixels);
        // 被截那块屏跟着闭包走：TTC1 头里只有 x/y（且侧车回写时恒填 0），
        // 让导出时再按光标重猜一次屏，就是交接 §41.4 记过的那个缺口。
        await VSCapEditor.OpenAsync(frame, display?.X ?? 0, display?.Y ?? 0,
            AppServices.Pipeline.GetEffectiveOutputDir(),
            (f, avif, dest) => ExportVSCapAsync(f, avif, dest, display),
            error => { SetStatus($"❌ VS-Cap: {error}"); ToastService.ShowCaptureFailed(error); });
        SetStatus(LocaleManager.Ready);
    }

    private async void OnVSCapPasteClick(object sender, RoutedEventArgs e) => await PasteVSCapAsync();

    private async Task PasteVSCapAsync()
    {
        try
        {
            await VSCapEditor.OpenAsync(null, 0, 0, AppServices.Pipeline.GetEffectiveOutputDir(),
                (f, avif, dest) => ExportVSCapAsync(f, avif, dest, null),
                error => { SetStatus($"❌ VS-Cap: {error}"); ToastService.ShowCaptureFailed(error); });
        }
        catch (Exception ex) { SetStatus($"❌ VS-Cap: {ex.Message}"); ToastService.ShowCaptureFailed(ex.Message); }
    }

    /// <param name="sourceDisplay">截这张图的那块屏；剪贴板入口没有源屏概念，传 null 才退回光标屏。</param>
    private async Task ExportVSCapAsync(VSCapFrame frame, bool avif, string destination, DisplayInfo? sourceDisplay)
    {
        var format = avif ? OutputFormat.AVIF : OutputFormat.PNG;
        var ui = CaptureEncodingUiState();
        var display = sourceDisplay ?? DisplayEnumerator.FindDisplayByMonitor(DisplayEnumerator.GetMonitorUnderCursor());
        var metadata = MetadataCollector.Collect(display);
        // §4.3 / D-2 口径：源是不是 Float16 与"这次是否 HDR 直通"是两件事，分开传。
        // HDR 能力屏上 VSCapFrame 恒为浮点帧，若按源精度分派，关掉 HDR 开关也会出 HDR 容器。
        bool hdrSource = frame.HdrPixels is not null;
        bool hdrScenario = hdrSource && ui.HdrEnabled;
        // 决定与容器同源：format 用侧车点名的那个（PNG/AVIF），不是 ui.Format
        var decision = DecisionForDisplay(display, ui, hdrScenario, format);
        var settings = BuildEncodingSettings(format, hdrScenario, metadata, ui, decision);
        // The toolbar explicitly requests PNG/AVIF, using TTC's per-format settings.
        settings.AvifPngSuffix = false;
        string path;
        if (hdrSource)
            path = await AppServices.Pipeline.EncodeHdrFrameAsync(new HdrFrameData
            {
                Pixels = frame.HdrPixels!, Width = frame.Width, Height = frame.Height, Metadata = metadata
            }, settings);
        else
            path = await AppServices.Pipeline.EncodeAndSaveAsync(frame.SdrPixels!, frame.Width, frame.Height,
                settings, decision);
        if (!string.IsNullOrEmpty(destination) && !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(path, destination, true);
            path = destination;
        }
        SetStatus($"✅ VS-Cap 已保存: {Path.GetFileName(path)}");
        // VS-Cap itself supplies the image/PNG/scRGB clipboard formats unchanged.
    }
}
