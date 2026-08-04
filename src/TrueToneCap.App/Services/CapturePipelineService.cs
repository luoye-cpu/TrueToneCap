// TrueToneCap.App/Services/CapturePipelineService.cs
// 截图编码管线服务 — 从 MainWindow 提取
// 负责: ICC 烘焙 + 编码调度 + 输出路径构建

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Metadata;
using TrueToneCap.Core.Processing;
using Vortice.Direct3D11;

namespace TrueToneCap.App.Services;

/// <summary>截图编码管线服务：ICC 色彩管理 + 格式编码 + 文件输出。</summary>
public sealed class CapturePipelineService
{
    private readonly SettingsService _settings;

    public CapturePipelineService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>ICC 色彩管理准备（后台线程安全，不访问 UI 控件）。</summary>
    public static (byte[] pixels, byte[]? iccProfile) PreparePixelsWithIcc(
        byte[] bgra, int w, int h, bool iccBakeEnabled, string colorSpaceTag,
        bool acmEnabled = false, nint? monitorHandle = null)
    {
        if (!iccBakeEnabled)
            return (bgra, null);

        // 将 "System" 解析为实际色域（ACM 感知）
        var resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag, false, acmEnabled, monitorHandle);
        var targetCs = ColorProfileProvider.MapColorSpaceTag(resolvedTag);
        bool isSRgbTarget = resolvedTag is "sRGB";

        byte[]? displayIcc = null;
        try
        {
            var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
            displayIcc = ColorProfileProvider.GetDisplayIccProfile(cursorMonitor);
        }
        catch { }

        // 有显示器 ICC → ACES Perceptual 烘焙到目标色域
        if (displayIcc is not null && displayIcc.Length > 500)
        {
            var (baked, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, displayIcc, targetCs);
            if (baked is not null)
            {
                if (isSRgbTarget)
                    return (baked, null);
                return (baked, targetIcc);
            }
        }

        // 无显示器 ICC / 烘焙失败
        if (!isSRgbTarget)
        {
            var targetIcc = ColorProfileProvider.GetStandardIccProfile(targetCs);
            return (bgra, targetIcc);
        }

        return (bgra, null);
    }

    /// <summary>
    /// 从 Float16 广色域像素转换为 SDR BGRA8 + 嵌入 ICC 元数据。
    /// 用于 HDR 关闭 + 广色域目标场景：
    /// WGC Float16 包含完整广色域数据 → 色域矩阵转换 → 色调映射 → sRGB gamma → BGRA8
    /// </summary>
    public static (byte[] pixels, byte[]? iccProfile) PrepareFloat16WithIcc(
        float[] hdrPixels, int w, int h,
        bool iccBakeEnabled, string colorSpaceTag,
        TrueToneCap.Core.Processing.ToneMappingParams toneParams,
        bool acmEnabled = false, nint? monitorHandle = null)
    {
        // 将 "System" 解析为实际色域（ACM 感知）
        var resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag, false, acmEnabled, monitorHandle);

        // 1. 色域转换 + 色调映射到 BGRA8
        var bgra = ColorSpaceConverter.ConvertFloat16ToSdrBgra(
            hdrPixels, w, h, resolvedTag, toneParams);

        // 2. ICC 色彩管理（嵌入元数据）
        bool isSRgbTarget = resolvedTag is "sRGB";
        if (!iccBakeEnabled || isSRgbTarget)
            return (bgra, null);

        var targetCs = ColorProfileProvider.MapColorSpaceTag(resolvedTag);
        var targetIcc = ColorProfileProvider.GetStandardIccProfile(targetCs);
        return (bgra, targetIcc);
    }

    /// <summary>构建编码设置。</summary>
    public EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta,
        string? colorSpaceTag = null, bool? acmEnabled = null)
    {
        var s = _settings.Current;
        bool acm = acmEnabled ?? s.AcmeDetected;
        var avifBackend = s.AvifBackendIndex switch
        {
            1 => AvifEncoderBackend.LibAom,
            2 => AvifEncoderBackend.Qsv,
            3 => AvifEncoderBackend.Nvenc,
            _ => AvifEncoderBackend.Auto
        };

        // 每格式参数映射（与 MainWindow.BuildEncodingSettings 保持一致）
        var (bitDepth, chroma) = format switch
        {
            OutputFormat.PNG => (s.BitDepthPng, "444"),
            OutputFormat.JPEG_LI => (s.BitDepthJpegLi, s.ChromaJpegLi),
            OutputFormat.JPEG_XL => (s.BitDepthJpegXl, s.ChromaJpegXl),
            OutputFormat.AVIF => (s.BitDepthAvif, s.ChromaAvif),
            OutputFormat.WebP => (s.BitDepthWebP, s.ChromaWebP),
            OutputFormat.TIFF => (s.BitDepthBmp, s.ChromaBmp),
            OutputFormat.JPEG_GAINMAP => (s.BitDepthGainMap, s.ChromaGainMap),
            _ => (s.OutputBitDepth, s.AvifChroma),
        };

        var settings = new EncodingSettings
        {
            Format = format,
            Quality = (float)s.Quality,
            HdrOutput = hdrOutput,
            AvifBackend = avifBackend,
            AvifPngSuffix = s.AvifPngSuffix,
            AvifChroma = chroma,
            ChromaSubsampling = chroma,
            OutputBitDepth = bitDepth,
            DisplayBitDepth = s.DisplayBitDepth,
            GainMapMode = s.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
            Metadata = meta,
            PreferGpuEncode = true,
            ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Aces },
        };

        // 解析 "System" 为实际色域（ACM 感知），确保编码器能正确判断 ICC/CICP 策略
        var resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag ?? "System", hdrOutput, acm, GetMonitorHandle());
        settings.ColorSpaceTag = resolvedTag;

        // 关键修复: HDR 开启 + sRGB 目标 → 色调映射到 SDR 输出
        // 用户显式选择 sRGB 意味着需要 SDR 兼容输出，而非 HDR PQ 编码
        // 此时 HdrOutput 设为 false，触发色调映射降级路径
        if (hdrOutput && resolvedTag == "sRGB")
        {
            settings.HdrOutput = false;
            LogService.Info("Pipeline", $"HDR 开启 + sRGB 目标 → 自动降级为 SDR 色调映射输出");
        }
        // HDR 开启 + 广色域目标 (P3/AdobeRGB/BT.2020) → 保留 HDR 直通编码
        // 用户选择广色域目标意味着要保留 HDR 动态范围

        LogService.Info("Pipeline", $"编码设置: {format} HDR={settings.HdrOutput} 质量={s.Quality:F1} 位深={bitDepth} 色度={chroma} AVIF后端={avifBackend} 色域={resolvedTag}");
        return settings;
    }

    /// <summary>获取当前鼠标所在显示器句柄。</summary>
    private static nint? GetMonitorHandle()
    {
        try { return DisplayEnumerator.GetMonitorUnderCursor(); }
        catch { return null; }
    }

    /// <summary>构建输出文件路径。</summary>
    public string BuildOutputPath(OutputFormat format, string outDir)
    {
        var s = _settings.Current;
        var ext = format switch
        {
            OutputFormat.JPEG_LI => ".jpg",
            OutputFormat.JPEG_GAINMAP => ".jpg",
            OutputFormat.JPEG_XL => ".jxl",
            OutputFormat.AVIF => ".avif",
            OutputFormat.WebP => ".webp",
            OutputFormat.TIFF => ".tiff",
            _ => ".png"
        };
        if (format == OutputFormat.AVIF && s.AvifPngSuffix) ext += ".png";
        return Path.Combine(outDir, $"{s.FileNamePrefix}{DateTime.Now:yyyyMMdd_HHmmssfff}{ext}");
    }

    /// <summary>获取有效的输出目录（含归档子目录）。</summary>
    public string GetEffectiveOutputDir()
    {
        var s = _settings.Current;
        var outDir = s.OutputPath;
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        if (s.ArchiveEnabled) outDir = GetArchivePath(outDir, s.ArchiveMode);
        return outDir;
    }

    /// <summary>根据归档模式生成子目录路径。</summary>
    public static string GetArchivePath(string baseDir, string mode)
    {
        var now = DateTime.Now;
        string sub = mode switch
        {
            "Year" => now.ToString("yyyy"),
            "Day" => now.ToString("yyyy-MM-dd"),
            _ => now.ToString("yyyy-MM"),
        };
        string full = Path.Combine(baseDir, sub);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>编码并保存 SDR 像素到文件（同步编码，在后台线程执行）。</summary>
    /// <remarks>
    /// 注意: 输入为 byte[] BGRA8 像素，始终走 SDR 编码路径。
    /// 即使 hdrOutput=true，byte[] 输入也不应转为 HDR PQ 路径（因为没有实际的 HDR float 数据）。
    /// 真正的 HDR 编码请使用 EncodeHdrFrameAsync（接收 HdrFrameData float 像素）。
    /// </remarks>
    public async Task<string> EncodeAndSaveAsync(
        byte[] bgra, int w, int h, OutputFormat format,
        bool hdrOutput, bool iccBakeEnabled, string colorSpaceTag,
        CancellationToken ct = default, ID3D11Texture2D? gpuTexture = null)
    {
        var encoder = EncoderFactory.Create(format);
        var settings = BuildEncodingSettings(format, hdrOutput, null, colorSpaceTag);
        if (gpuTexture is not null)
            settings.GpuTexture = gpuTexture;
        var outDir = GetEffectiveOutputDir();
        var path = BuildOutputPath(format, outDir);

        LogService.Info("Pipeline", $"SDR 编码启动: {format} {w}x{h} HDR={hdrOutput} → {Path.GetFileName(path)}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile) = PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;

            // 始终走 SDR 路径：byte[] 输入是 SDR BGRA8 像素，不应转为 HDR float16 编码
            // 如需 HDR 编码，使用 EncodeHdrFrameAsync 传入 HdrFrameData
            EncodeSyncSdr(encoder, pixels, w, h, settings, path, ct);
        }, ct);
        }
        finally { PowerManager.AllowSleep(); }

        var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        LogService.Info("Pipeline", $"SDR 编码完成: {Path.GetFileName(path)} ({fileSize / 1024.0:F1} KB)");
        return path;
    }

    /// <summary>后台线程中同步执行 HDR 编码（避免 GetAwaiter().GetResult() 阻塞线程池）。</summary>
    private static void EncodeSync(ImageEncoder encoder, HdrFrameData frame, EncodingSettings settings, string path, CancellationToken ct)
    {
        encoder.EncodeAsync(frame, settings, path, ct).GetAwaiter().GetResult();
    }

    /// <summary>后台线程中同步执行 SDR 编码。</summary>
    private static void EncodeSyncSdr(ImageEncoder encoder, byte[] pixels, int w, int h, EncodingSettings settings, string path, CancellationToken ct)
    {
        encoder.EncodeSdrAsync(pixels, w, h, settings, path, ct).GetAwaiter().GetResult();
    }

    /// <summary>编码并保存（使用调用方提供的显式设置，供 MainWindow UI 路径使用）。</summary>
    /// <remarks>
    /// 注意: 输入为 byte[] BGRA8 像素，始终走 SDR 编码路径，忽略 settings.HdrOutput。
    /// 真正的 HDR 编码请使用 EncodeHdrFrameAsync。
    /// </remarks>
    public async Task<string> EncodeAndSaveAsync(
        byte[] bgra, int w, int h, EncodingSettings settings,
        bool iccBakeEnabled, string colorSpaceTag,
        CancellationToken ct = default, ID3D11Texture2D? gpuTexture = null)
    {
        var encoder = EncoderFactory.Create(settings.Format);
        if (gpuTexture is not null)
            settings.GpuTexture = gpuTexture;
        var outDir = GetEffectiveOutputDir();
        var path = BuildOutputPath(settings.Format, outDir);

        LogService.Info("Pipeline", $"开始编码: {settings.Format} {w}x{h} → {Path.GetFileName(path)}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile) = PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;

            ct.ThrowIfCancellationRequested();
            // 始终走 SDR 路径
            EncodeSyncSdr(encoder, pixels, w, h, settings, path, ct);
        }, ct);
        }
        finally { PowerManager.AllowSleep(); }

        var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        LogService.Info("Pipeline", $"编码完成: {Path.GetFileName(path)} ({fileSize / 1024.0:F1} KB)");
        return path;
    }

    /// <summary>编码并保存 HDR 帧数据（WGC 直接捕获的 scRGB float 像素）。</summary>
    public async Task<string> EncodeHdrFrameAsync(
        HdrFrameData hdrFrame, EncodingSettings settings,
        CancellationToken ct = default)
    {
        var encoder = EncoderFactory.Create(settings.Format);
        var outDir = GetEffectiveOutputDir();
        var path = BuildOutputPath(settings.Format, outDir);

        LogService.Info("Pipeline", $"HDR 编码启动: {settings.Format} {hdrFrame.Width}x{hdrFrame.Height} HDR={settings.HdrOutput} 色域={settings.ColorSpaceTag} → {Path.GetFileName(path)}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // HDR 关闭 + scRGB 数据 → 色调映射到 SDR
            // 场景: HDR ON + sRGB 目标 (已在 BuildEncodingSettings 中设为 HdrOutput=false)
            //       或编码器不支持 HDR 的降级
            if (!settings.HdrOutput)
            {
                LogService.Info("Pipeline", $"HDR 帧降级到 SDR 色调映射: {settings.Format}");
                var sdrPixels = FormatHelper.ToSdr(hdrFrame, settings);
                if (hdrFrame.GpuTexture is not null)
                    settings.GpuTexture = hdrFrame.GpuTexture;
                // 色调映射后像素为 sRGB 色域
                settings.ColorSpaceTag = "sRGB";
                EncodeSyncSdr(encoder, sdrPixels, hdrFrame.Width, hdrFrame.Height, settings, path, ct);
                return;
            }

            if (encoder.SupportsHdr)
            {
                LogService.Debug("Pipeline", $"HDR 直通编码: {settings.Format}");
                EncodeSync(encoder, hdrFrame, settings, path, ct);
            }
            else
            {
                // 编码器不支持 HDR → 色调映射到 SDR
                LogService.Info("Pipeline", $"编码器不支持 HDR，色调映射到 SDR: {settings.Format}");
                var sdrPixels = FormatHelper.ToSdr(hdrFrame, settings);
                if (hdrFrame.GpuTexture is not null)
                    settings.GpuTexture = hdrFrame.GpuTexture;
                settings.ColorSpaceTag = "sRGB";
                settings.HdrOutput = false;
                EncodeSyncSdr(encoder, sdrPixels, hdrFrame.Width, hdrFrame.Height, settings, path, ct);
            }
        }, ct);
        }
        finally { PowerManager.AllowSleep(); }

        var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        LogService.Info("Pipeline", $"HDR 编码完成: {Path.GetFileName(path)} ({fileSize / 1024.0:F1} KB)");
        return path;
    }
}
