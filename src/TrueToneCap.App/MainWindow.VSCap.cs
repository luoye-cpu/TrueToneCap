using Microsoft.UI.Xaml;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Metadata;

namespace TrueToneCap.App;

public sealed partial class MainWindow
{
    private async Task StartVSCapCaptureAsync()
    {
        var monitor = DisplayEnumerator.GetMonitorUnderCursor();
        using var capture = await _wgcService!.CaptureMonitorAsync(new WgcCaptureConfig
        {
            TargetMonitor = monitor, FrameTimeoutMs = 3000,
            PreferHdr = (HdrSwitch.IsOn && HdrSwitch.IsEnabled) || ShouldUseFloat16ForWideGamut()
        });
        if (!string.IsNullOrEmpty(capture.DegradationWarning)) ToastService.ShowCaptureDegraded(capture.DegradationWarning);
        var display = capture.SourceDisplay ?? DisplayEnumerator.FindDisplayByMonitor(monitor);
        var frame = new VSCapFrame(capture.Width, capture.Height, capture.SdrPixels,
            Environment.GetEnvironmentVariable("TTC_FORCE_SDR") == "1" ? null : capture.HdrPixels);
        await VSCapEditor.OpenAsync(frame, display?.X ?? 0, display?.Y ?? 0,
            AppServices.Pipeline.GetEffectiveOutputDir(), ExportVSCapAsync,
            error => { SetStatus($"❌ VS-Cap: {error}"); ToastService.ShowCaptureFailed(error); });
        SetStatus("就绪");
    }

    private async void OnVSCapPasteClick(object sender, RoutedEventArgs e) => await PasteVSCapAsync();

    private async Task PasteVSCapAsync()
    {
        try
        {
            await VSCapEditor.OpenAsync(null, 0, 0, AppServices.Pipeline.GetEffectiveOutputDir(),
                ExportVSCapAsync, error => { SetStatus($"❌ VS-Cap: {error}"); ToastService.ShowCaptureFailed(error); });
        }
        catch (Exception ex) { SetStatus($"❌ VS-Cap: {ex.Message}"); ToastService.ShowCaptureFailed(ex.Message); }
    }

    private async Task ExportVSCapAsync(VSCapFrame frame, bool avif, string destination)
    {
        var format = avif ? OutputFormat.AVIF : OutputFormat.PNG;
        var ui = CaptureEncodingUiState();
        var monitor = DisplayEnumerator.GetMonitorUnderCursor();
        var metadata = MetadataCollector.Collect(DisplayEnumerator.FindDisplayByMonitor(monitor));
        var settings = AppServices.Pipeline.BuildEncodingSettings(format,
            frame.HdrPixels is not null && ui.HdrEnabled, metadata, ui.ColorSpaceTag, _settings.AcmeDetected);
        // The toolbar explicitly requests PNG/AVIF, using TTC's per-format settings.
        settings.AvifPngSuffix = false;
        string path;
        if (frame.HdrPixels is not null)
            path = await AppServices.Pipeline.EncodeHdrFrameAsync(new HdrFrameData
            {
                Pixels = frame.HdrPixels, Width = frame.Width, Height = frame.Height, Metadata = metadata
            }, settings);
        else
            path = await AppServices.Pipeline.EncodeAndSaveAsync(frame.SdrPixels!, frame.Width, frame.Height,
                settings, ui.IccBakeEnabled, ui.ColorSpaceTag);
        if (!string.IsNullOrEmpty(destination) && !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(path, destination, true);
            path = destination;
        }
        SetStatus($"✅ VS-Cap 已保存: {Path.GetFileName(path)}");
        // VS-Cap itself supplies the image/PNG/scRGB clipboard formats unchanged.
    }
}
