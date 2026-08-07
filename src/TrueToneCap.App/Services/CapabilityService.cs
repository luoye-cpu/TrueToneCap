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
    /// <summary>当前 HDR 是否启用（DWM 色彩空间为 PQ/HLG）。</summary>
    public bool SystemHdr { get; init; }

    /// <summary>显示器硬件是否支持 HDR（即使当前 HDR 未开启）。</summary>
    public bool SupportsHdr { get; init; }

    public bool SystemAcm { get; init; }
    public bool CustomIcc { get; init; }
    public bool NvencAvailable { get; init; }
    public bool QsvAvailable { get; init; }
    public int DisplayBitDepth { get; init; } = 8;

    /// <summary>显示器实际 SDR 白点亮度 (nits)。从 EDID 或 API 读取，默认 200。
    /// 用于色调映射的 PaperWhite 归一化。值越高，HDR 高光保留越好，但 SDR 内容越暗。</summary>
    public int DisplayPaperWhiteNits { get; init; } = 200;

    /// <summary>ACM 或自定义 ICC 时 ICC 烘焙可用（用户可能需要输出到显示器色域以外的目标）。</summary>
    public bool IccBakeAvailable => SupportsHdr || SystemAcm || CustomIcc;
}

/// <summary>系统能力检测服务（HDR / ACM / ICC / 硬件编码器）。</summary>
public sealed class CapabilityService
{
    /// <summary>一次性检测所有系统能力。</summary>
    public async Task<CapabilityResult> DetectAllAsync(CancellationToken ct = default)
    {
        var (sysHdr, sysAcm, supportsHdr) = DetectDisplayState();

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
            ?? displays.FirstOrDefault(d => d.SupportsHdr)?.BitsPerColor
            ?? displays.FirstOrDefault()?.BitsPerColor ?? 8;

        return new CapabilityResult
        {
            SystemHdr = sysHdr,
            SupportsHdr = supportsHdr,
            SystemAcm = sysAcm,
            CustomIcc = customIcc,
            NvencAvailable = nvenc,
            QsvAvailable = qsv,
            DisplayBitDepth = bitDepth,
            // 读取系统实际 SDR 白点 (nits), 失败回退默认 200
            // 用于 GainMap/色调映射的 PaperWhite 归一化, 必须与系统一致
            DisplayPaperWhiteNits = DisplayEnumerator.GetSdrWhiteLevel() is var pw && pw > 0 ? pw : 200
        };
    }

    /// <summary>检测系统显示状态：HDR 是否启用、ACM 是否启用、硬件是否支持 HDR。</summary>
    public static (bool hdr, bool acm, bool supportsHdr) DetectDisplayState()
    {
        bool hdr = false, acm = false, supportsHdr = false;
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            hdr = displays.Any(d => d.IsHdr);
            supportsHdr = displays.Any(d => d.SupportsHdr);
            LogService.Info("Capability", $"显示器枚举: {displays.Count} 个, HDR当前启用={hdr}, HDR硬件支持={supportsHdr}");

            // ACM 检测策略 (Windows 11 22H2+):
            // 1. 检查 HKLM ICM 全局开关
            // 2. 检查 HKCU ProfileAssociations 中是否存在 ICMProfileAC 值（ACM 自动分配的配置文件）
            try
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
                if (hklmKey is not null)
                {
                    var val = hklmKey.GetValue("AcmEnabled");
                    if (val is int i && i != 0) { acm = true; }
                }
            }
            catch { }

            // 回退: 检查用户级 ProfileAssociations 中是否有 ICMProfileAC（ACM 自动分配标志）
            if (!acm)
            {
                try
                {
                    using var icmKey = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display");
                    if (icmKey is not null)
                    {
                        foreach (var clsid in icmKey.GetSubKeyNames())
                        {
                            using var clsidKey = icmKey.OpenSubKey(clsid);
                            if (clsidKey is null) continue;
                            foreach (var monitorId in clsidKey.GetSubKeyNames())
                            {
                                using var monKey = clsidKey.OpenSubKey(monitorId);
                                if (monKey is null) continue;
                                var acProfile = monKey.GetValue("ICMProfileAC");
                                // ICMProfileAC 非空且非空字符串 → ACM 已为该显示器分配配置文件
                                if (acProfile is string[] arr && arr.Any(s => !string.IsNullOrWhiteSpace(s)))
                                {
                                    acm = true;
                                    break;
                                }
                                if (acProfile is string s2 && !string.IsNullOrWhiteSpace(s2))
                                {
                                    acm = true;
                                    break;
                                }
                            }
                            if (acm) break;
                        }
                    }
                }
                catch { }
            }

            LogService.Info("Capability", $"ACM 检测: {(acm ? "启用" : "未启用")}");
        }
        catch (Exception ex)
        {
            LogService.Warn("Capability", $"显示状态检测异常: {ex.Message}");
        }
        return (hdr, acm, supportsHdr);
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
    public static int DetectBestColorSpace(bool hdr, bool supportsHdr, bool acm, bool customIcc)
    {
        if (hdr) return 5;       // HDR 当前启用 → BT.2020
        if (supportsHdr) return 5; // 硬件支持 HDR（即使未启用）→ 也默认 BT.2020
        if (acm) return 0;       // ACM → 跟随系统
        if (customIcc) return 0; // 自定义 ICC → 跟随系统
        return 0;
    }
}
