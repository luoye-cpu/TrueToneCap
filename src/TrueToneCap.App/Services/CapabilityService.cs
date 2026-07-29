// TrueToneCap.App/Services/CapabilityService.cs
// 系统能力检测服务 — 从 MainWindow 提取
// 负责: HDR/ACM 检测、ICC 校色检测、AVIF 硬件编码器探测

using Microsoft.Win32;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.App.Services;

/// <summary>系统能力检测结果。</summary>
public sealed record CapabilityResult
{
    public bool SystemHdr { get; init; }
    public bool SystemAcm { get; init; }
    public bool CustomIcc { get; init; }
    public bool NvencAvailable { get; init; }
    public bool QsvAvailable { get; init; }
    public int DisplayBitDepth { get; init; } = 8;

    /// <summary>ACM 开启时 ICC 烘焙应禁用（避免双重色彩管理）。</summary>
    public bool IccBakeAvailable => CustomIcc && !SystemAcm;
}

/// <summary>系统能力检测服务（HDR / ACM / ICC / 硬件编码器）。</summary>
public sealed class CapabilityService
{
    /// <summary>一次性检测所有系统能力。</summary>
    public async Task<CapabilityResult> DetectAllAsync(CancellationToken ct = default)
    {
        var (sysHdr, sysAcm) = DetectDisplayState();

        bool customIcc = false;
        try
        {
            customIcc = await Task.Run(DetectCustomIccProfile, ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch { }

        bool nvenc = false, qsv = false;
        try
        {
            var probe = await Task.Run(() => AvifHardwareProbe.Result, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            nvenc = probe.NvencAvailable;
            qsv = probe.QsvAvailable;
        }
        catch { }

        var displays = DisplayEnumerator.EnumerateDisplays();
        int bitDepth = displays.FirstOrDefault(d => d.IsHdr)?.BitsPerColor
            ?? displays.FirstOrDefault()?.BitsPerColor ?? 8;

        return new CapabilityResult
        {
            SystemHdr = sysHdr,
            SystemAcm = sysAcm,
            CustomIcc = customIcc,
            NvencAvailable = nvenc,
            QsvAvailable = qsv,
            DisplayBitDepth = bitDepth
        };
    }

    /// <summary>检测系统显示状态：HDR 是否启用、ACM 是否启用。</summary>
    public static (bool hdr, bool acm) DetectDisplayState()
    {
        bool hdr = false, acm = false;
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            hdr = displays.Any(d => d.IsHdr);
            LogService.Info("Capability", $"显示器枚举: {displays.Count} 个, HDR={hdr}");

            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\ICM");
            if (key is not null)
            {
                var val = key.GetValue("AcmeEnabled");
                acm = val is int i && i != 0;
            }
            LogService.Info("Capability", $"ACM 检测: {(acm ? "启用" : "未启用")}");
        }
        catch (Exception ex)
        {
            LogService.Warn("Capability", $"显示状态检测异常: {ex.Message}");
        }
        return (hdr, acm);
    }

    /// <summary>检测当前显示器是否使用自定义 ICC 配置文件。</summary>
    public static bool DetectCustomIccProfile()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            foreach (var d in displays)
            {
                var icc = ColorProfileProvider.GetDisplayIccProfile(d.MonitorHandle);
                if (ColorProfileProvider.IsNonStandardIcc(icc))
                {
                    LogService.Info("Capability", $"显示器 {d.Name} 使用自定义 ICC ({icc?.Length ?? 0} bytes)");
                    return true;
                }
            }
            LogService.Info("Capability", "所有显示器均使用系统默认 ICC");
        }
        catch (Exception ex)
        {
            LogService.Warn("Capability", $"ICC 检测异常: {ex.Message}");
        }
        return false;
    }

    /// <summary>根据 HDR/ACM/ICC 状态决定最佳色彩空间默认值。</summary>
    public static int DetectBestColorSpace(bool hdr, bool acm, bool customIcc)
    {
        if (hdr) return 5;       // HDR → BT.2020
        if (acm) return 0;       // ACM → 跟随系统
        if (customIcc) return 0; // 自定义 ICC → 跟随系统
        return 0;
    }
}
