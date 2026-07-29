// TrueToneCap.App/Services/CapturePipelineService.cs
// 截图编码管线服务 — 从 MainWindow 提取
// 负责: ICC 烘焙 + 编码调度 + 输出路径构建

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Metadata;
using TrueToneCap.Core.Processing;

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
        byte[] bgra, int w, int h, bool iccBakeEnabled, string colorSpaceTag)
    {
        if (!iccBakeEnabled)
            return (bgra, null);

        var targetCs = ColorProfileProvider.MapColorSpaceTag(colorSpaceTag);
        bool isSRgbTarget = colorSpaceTag is "System" or "sRGB";

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

    /// <summary>构建编码设置。</summary>
    public EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta)
    {
        var s = _settings.Current;
        var avifBackend = s.AvifBackendIndex switch
        {
            1 => AvifEncoderBackend.LibAom,
            2 => AvifEncoderBackend.Qsv,
            3 => AvifEncoderBackend.Nvenc,
            _ => AvifEncoderBackend.Auto
        };
        var settings = new EncodingSettings
        {
            Format = format,
            Quality = (float)s.Quality,
            HdrOutput = hdrOutput,
            AvifBackend = avifBackend,
            AvifPngSuffix = s.AvifPngSuffix,
            AvifChroma = s.AvifChroma,
            DisplayBitDepth = s.DisplayBitDepth,
            GainMapMode = s.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
            Metadata = meta,
            PreferGpuEncode = true,
            ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable }
        };

        LogService.Info("Pipeline", $"编码设置: {format} HDR={hdrOutput} 质量={s.Quality:F1} AVIF后端={avifBackend} 色度={s.AvifChroma}");
        return settings;
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
            OutputFormat.BMP => ".bmp",
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

    /// <summary>编码并保存 SDR 像素到文件。</summary>
    public async Task<string> EncodeAndSaveAsync(
        byte[] bgra, int w, int h, OutputFormat format,
        bool hdrOutput, bool iccBakeEnabled, string colorSpaceTag,
        CancellationToken ct = default)
    {
        var encoder = EncoderFactory.Create(format);
        var settings = BuildEncodingSettings(format, hdrOutput, null);
        var outDir = GetEffectiveOutputDir();
        var path = BuildOutputPath(format, outDir);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile) = PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;

            if (hdrOutput && encoder.SupportsHdr)
            {
                var hdrFrame = new HdrFrameData
                {
                    Pixels = TrueToneCap.Core.PixelOps.BgraToScrgbLinearFast(pixels, w, h),
                    Width = w, Height = h,
                    IccProfile = settings.IccProfile
                };
                encoder.EncodeAsync(hdrFrame, settings, path, ct).GetAwaiter().GetResult();
            }
            else
            {
                encoder.EncodeSdrAsync(pixels, w, h, settings, path, ct).GetAwaiter().GetResult();
            }
        }, ct);

        return path;
    }
}
