// TrueToneCap.Core/Encoding/GpuCapability.cs
// GPU 硬件编码器检测 — 原生 SDK (非 FFmpeg)

using Vortice.DXGI;

namespace TrueToneCap.Core.Encoding;

public enum GpuEncoderType { None = 0, NVENC = 1, QSV = 2 }

public sealed class GpuEncoderInfo
{
    public GpuEncoderType Type { get; init; }
    public string AdapterName { get; init; } = "";
    public bool SupportsAv1 { get; init; }
    public bool SupportsHevc { get; init; }
    public bool Available { get; set; }
    public string DisplayName => Type switch
    {
        GpuEncoderType.NVENC => $"NVIDIA NVENC ({AdapterName}){(SupportsAv1 ? " [AV1]" : " [HEVC]")}",
        GpuEncoderType.QSV => $"MFT ({AdapterName}){(SupportsAv1 ? " [AV1]" : " [HEVC]")}",
        _ => "无硬件编码器"
    };
}

public static class GpuCapability
{
    private static List<GpuEncoderInfo>? _cached;

    public static IReadOnlyList<GpuEncoderInfo> DetectEncoders()
    {
        if (_cached != null) return _cached;
        _cached = [];

        var dxgi = DetectViaDxgi();
        foreach (var info in dxgi)
        {
            info.Available = info.Type switch
            {
                GpuEncoderType.NVENC => NvEncoderNative.IsAvailable,
                GpuEncoderType.QSV => MftEncoderNative.IsIntelGpuAvailable,
                _ => false
            };
            _cached.Add(info);
        }
        return _cached;
    }

    public static bool HasGpuEncoder => DetectEncoders().Any(e => e.Available);

    private static List<GpuEncoderInfo> DetectViaDxgi()
    {
        var r = new List<GpuEncoderInfo>();
        try
        {
            using var f = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
            for (uint i = 0; ; i++)
            {
                if (f.EnumAdapters1(i, out var a).Failure || a is null) break;
                try
                {
                    var d = a.Description;
                    var name = d.Description.Trim();
                    var info = d.VendorId switch
                    {
                        0x10DE => new GpuEncoderInfo
                        {
                            Type = GpuEncoderType.NVENC,
                            AdapterName = name,
                            // P3: 多策略 AV1 检测 — DeviceId 范围 + 适配器名称关键词
                            SupportsAv1 = IsNvidiaAv1Capable((int)d.DeviceId, name),
                            SupportsHevc = true
                        },
                        0x8086 => new GpuEncoderInfo
                        {
                            Type = GpuEncoderType.QSV,
                            AdapterName = name,
                            // P3: 扩展 Intel AV1 检测 — Arc/Xe + 第 12 代+ 核显名称
                            SupportsAv1 = IsIntelAv1Capable(name),
                            SupportsHevc = true
                        },
                        _ => null
                    };
                    if (info != null) r.Add(info);
                }
                finally { a.Dispose(); }
            }
        }
        catch { }
        return r;
    }

    /// <summary>NVIDIA AV1 能力检测：DeviceId 范围 + 适配器名称双重判断。</summary>
    private static bool IsNvidiaAv1Capable(int devId, string adapterName)
    {
        // 策略1: DeviceId 范围（Ada Lovelace 0x26xx/0x27xx, Blackwell 0x2Fxx）
        if ((devId & 0xFFFF) is >= 0x2600 and <= 0x27FF or >= 0x2F00 and <= 0x2FFF)
            return true;

        // 策略2: 适配器名称关键词（覆盖新卡/未知 DeviceId）
        // RTX 40 系列 (Ada), RTX 50 系列 (Blackwell), 及未来支持 AV1 的型号
        var nameUpper = adapterName.ToUpperInvariant();
        return nameUpper.Contains("RTX 4") || nameUpper.Contains("RTX 5")
            || nameUpper.Contains("RTX A4") || nameUpper.Contains("RTX A5")  // RTX A4000+ (Ampere GA102 部分支持)
            || nameUpper.Contains("RTX 6000") || nameUpper.Contains("RTX 5000"); // 工作站卡
    }

    /// <summary>Intel AV1 能力检测：Arc 独显 + Xe-HPG/HPC 核显。</summary>
    private static bool IsIntelAv1Capable(string adapterName)
    {
        var nameUpper = adapterName.ToUpperInvariant();
        // Arc 独显 (Alchemist/Battlemage/Celestial)
        if (nameUpper.Contains("ARC")) return true;
        // Xe / Xe2 核显（第 12 代 Alder Lake+ 集显）
        if (nameUpper.Contains("XE")) return true;
        // Intel UHD/Iris 第 12 代+ (Alder Lake/Raptor Lake/Meteor Lake/Lunar Lake)
        if (nameUpper.Contains("UHD GRAPHICS 7") || nameUpper.Contains("IRIS")) return true;
        // 适配器名称含 "A7" (Arc A7xx) 或 "B" (Battlemage)
        if (nameUpper.Contains("A7") || nameUpper.Contains("B5")) return true;
        return false;
    }
}
