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
                    var info = d.VendorId switch
                    {
                        0x10DE => new GpuEncoderInfo { Type = GpuEncoderType.NVENC, AdapterName = d.Description.Trim(), SupportsAv1 = IsAdaOrNewer((int)d.DeviceId), SupportsHevc = true },
                        0x8086 => new GpuEncoderInfo { Type = GpuEncoderType.QSV, AdapterName = d.Description.Trim(), SupportsAv1 = d.Description.Contains("Arc") || d.Description.Contains("Xe"), SupportsHevc = true },
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

    private static bool IsAdaOrNewer(int devId) => (devId & 0xFFFF) is >= 0x2600 and <= 0x27FF or >= 0x2F00 and <= 0x2FFF;
}
