// TrueToneCap.App/Services/CapturePipelineService.cs
// 截图编码管线服务 — 从 MainWindow 提取
// 负责: ICC 烘焙 + 编码调度 + 输出路径构建

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Metadata;
using TrueToneCap.Core.Processing;
using TrueToneCap.App.Models;
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
        bool? acmEnabled = null, nint? monitorHandle = null)
    {
        if (!iccBakeEnabled)
            return (bgra, null);

        // acmEnabled 默认为系统当前 ACM 状态 (从 AppServices 读取)
        bool acm = acmEnabled ?? ReadSystemAcm();

        // 将 "System" 解析为实际色域（ACM 感知）
        var resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag, false, acm, monitorHandle);
        var targetCs = ColorProfileProvider.MapColorSpaceTag(resolvedTag);
        bool isSRgbTarget = resolvedTag is "sRGB";

        // ACM 下检测显示器原生色域 (SDR 捕获像素所在色域)
        string? nativeGamutTag = null;
        if (acm)
        {
            try
            {
                var hmon = monitorHandle ?? DisplayEnumerator.GetMonitorUnderCursor();
                nativeGamutTag = ColorProfileProvider.GetDisplayNativeGamutTag(hmon);
            }
            catch { }
            nativeGamutTag ??= (isSRgbTarget ? "sRGB" : null);
        }

        // ═══ ACM 模式 (2026-08-09 方案A): 系统已接管显示器色彩管理 ═══
        // ACM 启用时, WGC SDR 捕获的 BGRA8 像素是 DWM 合成后的显示器色域 (ACM 已把
        // 所有应用内容映射到显示器色域)。因此:
        // - 目标 = 显示器原生色域 (或 sRGB): 像素已在该色域, 跳过烘焙, 嵌显示器原生
        //   色域的标准 ICC (或 sRGB 不嵌) 即可 → 无双重转换, 色彩准确
        // - 目标 ≠ 显示器原生色域: 像素在显示器色域但用户要输出目标色域, 必须烘焙
        //   (从显示器色域转换到目标), 跳过烘焙会产生像素/ICC 矛盾
        if (acm)
        {
            // 目标色域 = 显示器原生色域(或 sRGB) → 跳过烘焙, 嵌对应标准 ICC
            if (isSRgbTarget || targetCs == nativeGamutTag)
                return (bgra, isSRgbTarget ? null : ColorProfileProvider.GetStandardIccProfile(targetCs));

            // ═══ 2026-08-09 修复: 目标 ≠ 显示器色域 → 用标准原生色域 ICC 作源烘焙 ═══
            // ACM 下 GetDisplayIccProfile 可能返回 null 或已被 ACM 修改的 ICC (非原生色域),
            // 用它作烘焙源 → 矩阵错误/烘焙失败 → 像素未转换却嵌目标 ICC → 色彩错误。
            // 改用标准原生色域 ICC (可靠矩阵): 像素 = 显示器色域 + 标准原生 ICC 解析
            // → BakeIccToTarget 正确转换像素到目标色域 + 嵌目标标准 ICC。
            if (nativeGamutTag is not null)
            {
                var nativeIcc = ColorProfileProvider.GetStandardIccProfile(nativeGamutTag);
                if (nativeIcc is { Length: > 128 })
                {
                    var (baked, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, nativeIcc, targetCs);
                    if (baked is not null)
                    {
                        if (isSRgbTarget)
                            return (baked, null);
                        return (baked, targetIcc);
                    }
                }
            }
            // 原生色域未知 → fallthrough 到下方传统烘焙 (可能失败, 兜底)
        }

        // ── 非 ACM 模式: 传统烘焙 (显示器 ICC → 目标色域) ──
        byte[]? displayIcc = null;
        try
        {
            var cursorMonitor = DisplayEnumerator.GetMonitorUnderCursor();
            displayIcc = ColorProfileProvider.GetDisplayIccProfile(cursorMonitor);
        }
        catch { }

        // 有显示器 ICC → 烘焙到目标色域 (广色域 SDR)
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

    /// <summary>读取系统当前 ACM 状态 (供 static 方法默认值)。</summary>
    private static bool ReadSystemAcm()
    {
        try { return AppServices.Settings.Current.AcmeDetected; }
        catch { return false; }
    }

    /// <summary>
    /// 从 Float16 广色域像素转换为 SDR BGRA8 + 嵌入 ICC 元数据。
    /// 用于 HDR 关闭 + 广色域目标场景：
    /// WGC Float16 包含完整广色域数据 → 色调映射 → sRGB gamma → BGRA8
    /// </summary>
    public static (byte[] pixels, byte[]? iccProfile) PrepareFloat16WithIcc(
        float[] hdrPixels, int w, int h,
        bool iccBakeEnabled, string colorSpaceTag,
        TrueToneCap.Core.Processing.ToneMappingParams toneParams,
        bool? acmEnabled = null, nint? monitorHandle = null)
    {
        bool acm = acmEnabled ?? ReadSystemAcm();

        // 将 "System" 解析为实际色域（ACM 感知）
        var resolvedTag = ColorProfileProvider.ResolveColorSpaceTag(colorSpaceTag, false, acm, monitorHandle);
        bool isSRgbTarget = resolvedTag is "sRGB";

        // ═══ 2026-08-10 修复(2): SDR 输出恒为 sRGB 色域 ═══
        // 教训: 2026-08-09 误改为"转换到目标色域 (BT2020)"→ 影响 GainMap SDR 回退
        // 和 JPEG LI 的 HDR→SDR 路径:
        //   - 像素被矩阵转换到 BT2020, 但 gamma 编码是 sRGB 的
        //   - 再嵌 BT2020 ICC → 查看器按 BT2020 TRC 解码 → gamma 不匹配 → 颜色错误
        // GainMap 的正确映射 (ReinhardToSdr): scRGB 直接色调映射, 不做色域矩阵,
        // Base 恒 sRGB + sRGB ICC (2026-08-06 设计决策: Base 与增益图同处 scRGB 空间)。
        // SDR 8-bit 容器语义 = sRGB; BT2020 色域只应通过 HDR 直通 (PQ) 实现。
        // 因此: 恒输出 sRGB 色域像素 (scRGB BT.709 原色 + sRGB gamma), 与 GainMap 一致。

        // 1. 色调映射到 BGRA8 (scRGB → sRGB gamma, 不转换色域矩阵)
        var bgra = ColorSpaceConverter.ConvertFloat16ToSdrBgra(
            hdrPixels, w, h, "sRGB", toneParams);

        // 2. ICC 色彩管理: SDR 输出恒为 sRGB → 不嵌 ICC (查看器默认 sRGB)
        return (bgra, null);
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
            OutputFormat.TIFF => (s.BitDepthTiff, s.ChromaTiff),
            OutputFormat.JPEG_GAINMAP => (s.BitDepthGainMap, s.ChromaGainMap),
            _ => (s.OutputBitDepth, s.AvifChroma),
        };

        var settings = new EncodingSettings
        {
            Format = format,
            Quality = (float)GetPerFormatQuality(s, format),
            HdrOutput = hdrOutput,
            AvifBackend = avifBackend,
            AvifPngSuffix = s.AvifPngSuffix,
            JxlPngSuffix = s.JxlPngSuffix,
            AvifChroma = chroma,
            ChromaSubsampling = chroma,
            OutputBitDepth = bitDepth,
            DisplayBitDepth = s.DisplayBitDepth,
            GainMapMode = s.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
            Metadata = meta,
            PreferGpuEncode = true,
            // ── SDR 白点: 系统检测值优先 (DISPLAYCONFIG_SDR_WHITE_LEVEL), 用户手动设置回退 ──
            // 关键: GainMap 的 SDR 直通阈值必须与系统实际 SdrWhiteLevel 一致,
            // 否则 Base 亮度错误 (SDR 发暗/过曝)。系统值 > 0 时优先。
            // ── HDR 峰值: 系统检测值优先 (DXGI MaxLuminance), 用户设置回退 ──
            // 关键: GainMap headroom (XMP/ISO GainMapMax) 依赖此值,
            // 错误值会导致解码端 weight_factor 偏差 → HDR 还原过亮/过暗。
            // ── 色调映射模式: 分段 Reinhard (GainMap 同款) ──
            // 2026-08-08: 所有格式 HDR→SDR 统一使用 GainMap 的分段 Reinhard 曲线
            // (y≤SDR白点直通 + smoothstep 过渡 + 高光 Reinhard 压缩), 保证各格式输出一致。
            ToneMappingParams = new ToneMappingParams
            {
                Mode = ToneMapMode.SegmentedReinhard,
                PaperWhiteNits = (s.SystemSdrWhiteLevel > 0 ? s.SystemSdrWhiteLevel : s.PaperWhiteNits),
                DisplayMaxNits = (s.SystemMaxNits > 0 ? s.SystemMaxNits : s.DisplayMaxNits)
            },
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

        LogService.Info("Pipeline", $"编码设置: {format} HDR={settings.HdrOutput} 质量={settings.Quality:F1} 位深={bitDepth} 色度={chroma} AVIF后端={avifBackend} 色域={resolvedTag}");
        return settings;
    }

    /// <summary>按格式获取每格式独立质量（避免切换格式互相覆盖）。</summary>
    private static double GetPerFormatQuality(AppSettingsData s, OutputFormat format) => format switch
    {
        OutputFormat.PNG => s.QualityPng,
        OutputFormat.JPEG_GAINMAP => s.QualityGainMap,
        OutputFormat.JPEG_LI => s.QualityJpegLi,
        OutputFormat.JPEG_XL => s.QualityJpegXl,
        OutputFormat.AVIF => s.QualityAvif,
        OutputFormat.WebP => s.QualityWebp,
        OutputFormat.TIFF => s.QualityTiff,
        _ => s.Quality,
    };

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
        else if (format == OutputFormat.JPEG_XL && s.JxlPngSuffix) ext += ".png";
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
    /// ═══ 重要: 此方法始终走 SDR 编码路径 ═══
    /// 输入为 byte[] BGRA8 像素，即使 hdrOutput=true，也绝不转为 HDR PQ 路径。
    /// 因为 byte[] 输入不包含 HDR 浮点数据，强行转为 HDR 会导致色域错误。
    /// 
    /// 如需 HDR 编码，请使用 EncodeHdrFrameAsync()（接收 HdrFrameData float 像素）。
    /// 
    /// hdrOutput 参数仅用于 BuildEncodingSettings 的色域解析逻辑
    /// （例如 HDR 开启时 "System" 解析为 BT.2020），不改变编码路径。
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
        await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile) = PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;

            // 始终走 SDR 路径：byte[] 输入是 SDR BGRA8 像素，不应转为 HDR float16 编码
            // 如需 HDR 编码，使用 EncodeHdrFrameAsync 传入 HdrFrameData
            await encoder.EncodeSdrAsync(pixels, w, h, settings, path, ct);
        }, ct);
        }
        finally { PowerManager.AllowSleep(); }

        var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        LogService.Info("Pipeline", $"SDR 编码完成: {Path.GetFileName(path)} ({fileSize / 1024.0:F1} KB)");
        return path;
    }

    // ═══ 2026-08-30 修复: 移除 EncodeSync / EncodeSyncSdr 两个同步包装 ═══
    // 原实现用 encoder.EncodeXxxAsync(...).GetAwaiter().GetResult() 在 Task.Run 内阻塞等待，
    // 而编码器内部又各自包了一层 Task.Run —— 一次编码同时占用 2 个线程池线程
    // （1 个阻塞在 GetResult + 1 个真正执行）。连续/并发截图时有线程池饥饿风险。
    // 现改为在 Task.Run 内直接 await，外层线程在等待期间归还线程池。

    /// <summary>编码并保存（使用调用方提供的显式设置，供 MainWindow UI 路径使用）。</summary>
    /// <remarks>
    /// 注意: 输入为 byte[] BGRA8 像素，始终走 SDR 编码路径，忽略 settings.HdrOutput。
    /// 真正的 HDR 编码请使用 EncodeHdrFrameAsync。
    /// </remarks>
    public async Task<string> EncodeAndSaveAsync(
        byte[] bgra, int w, int h, EncodingSettings settings,
        bool iccBakeEnabled, string colorSpaceTag,
        CancellationToken ct = default, ID3D11Texture2D? gpuTexture = null,
        string? outputDir = null)
    {
        var encoder = EncoderFactory.Create(settings.Format);
        if (gpuTexture is not null)
            settings.GpuTexture = gpuTexture;
        var outDir = outputDir ?? GetEffectiveOutputDir();
        var path = BuildOutputPath(settings.Format, outDir);

        LogService.Info("Pipeline", $"开始编码: {settings.Format} {w}x{h} → {Path.GetFileName(path)}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile) = PreparePixelsWithIcc(bgra, w, h, iccBakeEnabled, colorSpaceTag);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;

            ct.ThrowIfCancellationRequested();
            // 始终走 SDR 路径
            await encoder.EncodeSdrAsync(pixels, w, h, settings, path, ct);
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
        await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // HDR 关闭 + scRGB 数据 → 色调映射到 SDR
            // 场景: HDR ON + sRGB 目标 (已在 BuildEncodingSettings 中设为 HdrOutput=false)
            //       或编码器不支持 HDR 的降级
            if (!settings.HdrOutput)
            {
                LogService.Info("Pipeline", $"HDR 帧降级到 SDR 色调映射: {settings.Format}");
                var sdrPixels = FormatHelper.ToSdr(hdrFrame, settings);
                // ═══ 2026-08-16 P1-2 修复: 派生像素 (色调映射后) 禁止带 GPU 纹理 ═══
                // 纹理是原始捕获帧的内容, 与色调映射后的像素不一致 → NVENC 会编码
                // 原始桌面帧而非映射结果 → 色彩/亮度错误。统一规则: 派生像素不带纹理。
                settings.GpuTexture = null;
                // 色调映射后像素为 sRGB 色域
                settings.ColorSpaceTag = "sRGB";
                await encoder.EncodeSdrAsync(sdrPixels, hdrFrame.Width, hdrFrame.Height, settings, path, ct);
                return;
            }

            if (encoder.SupportsHdr)
            {
                // ═══ 2026-08-09 修复: HDR 直通 ICC 策略 ═══
                // 关键教训: 标准 ICC (GetStandardIccProfile) 是 sRGB TRC,
                // 而 HDR 像素是 PQ 编码 → 嵌入会覆盖/冲突 → 解码器按 sRGB gamma
                // 解码 PQ 值 → 图像错误 (JXL 实测: cjxl 的 icc_pathname 覆盖 color_space,
                // 最终 codestream 声明 sRGB transfer 而像素是 PQ!)。
                // - JXL: 用 `-x color_space=Rec2100PQ` color fields 声明 BT.2020+PQ (最优)
                // - PNG/AVIF: 用 CICP (BT.2020 + PQ) 声明 (PNG 3.0/AV1 规范优先)
                // - TIFF: 无 CICP 机制 → 必须嵌 PQ TRC ICC
                if (settings.Format == OutputFormat.TIFF)
                {
                    var hdrCsTag = settings.ColorSpaceTag;
                    settings.IccProfile = (hdrCsTag is not (null or "System" or "sRGB"))
                        ? ColorProfileProvider.GetHdrStandardIccProfile(hdrCsTag)
                        : null;
                }
                else
                {
                    settings.IccProfile = null;
                }
                LogService.Debug("Pipeline", $"HDR 直通编码: {settings.Format}");
                await encoder.EncodeAsync(hdrFrame, settings, path, ct);
            }
            else
            {
                // 编码器不支持 HDR → 色调映射到 SDR
                LogService.Info("Pipeline", $"编码器不支持 HDR，色调映射到 SDR: {settings.Format}");
                var sdrPixels = FormatHelper.ToSdr(hdrFrame, settings);
                // ═══ 2026-08-16 P1-2 修复: 派生像素禁止带 GPU 纹理 (同上) ═══
                settings.GpuTexture = null;
                settings.ColorSpaceTag = "sRGB";
                settings.HdrOutput = false;
                await encoder.EncodeSdrAsync(sdrPixels, hdrFrame.Width, hdrFrame.Height, settings, path, ct);
            }
        }, ct);
        }
        finally { PowerManager.AllowSleep(); }

        var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        LogService.Info("Pipeline", $"HDR 编码完成: {Path.GetFileName(path)} ({fileSize / 1024.0:F1} KB)");
        return path;
    }
}
