// TrueToneCap.Core/Encoding/IccStore.cs
// 内嵌标准 ICC Profile 生成器 — 替代 ImageMagick.ColorProfiles
// 使用 IccProfileBuilder 生成所有标准色彩空间 ICC

namespace TrueToneCap.Core.Encoding;

/// <summary>标准 ICC Profile 存储 — 运行时生成，线程安全缓存。</summary>
public static class IccStore
{
    private static byte[]? _srgb;
    private static byte[]? _adobeRgb;
    private static byte[]? _displayP3;
    private static byte[]? _bt2020;
    private static readonly object _lock = new();

    /// <summary>sRGB IEC 61966-2.1 ICC Profile。</summary>
    public static byte[] SRGB
    {
        get
        {
            if (_srgb is not null) return _srgb;
            lock (_lock)
            {
                if (_srgb is not null) return _srgb;
                // 直接调用 GetDefaultSRgbIcc 避免与 GetStandardIccProfile 循环
                _srgb = ColorManagement.ColorProfileProvider.GetDefaultSRgbIcc();
                return _srgb ?? [];
            }
        }
    }

    /// <summary>Adobe RGB (1998) ICC Profile。</summary>
    public static byte[] AdobeRGB1998
    {
        get
        {
            if (_adobeRgb is not null) return _adobeRgb;
            lock (_lock)
            {
                if (_adobeRgb is not null) return _adobeRgb;
                // 直接使用 PatchSrgbPrimaries 避免循环
                _adobeRgb = ColorManagement.ColorProfileProvider.PatchSrgbPrimaries(
                    ColorManagement.IccPrimaries.AdobeRGB, "Adobe RGB (1998)");
                return _adobeRgb ?? SRGB;
            }
        }
    }

    /// <summary>Display P3 ICC Profile。</summary>
    public static byte[] DisplayP3
    {
        get
        {
            if (_displayP3 is not null) return _displayP3;
            lock (_lock)
            {
                if (_displayP3 is not null) return _displayP3;
                _displayP3 = ColorManagement.ColorProfileProvider.PatchSrgbPrimaries(
                    ColorManagement.IccPrimaries.DisplayP3, "Display P3");
                return _displayP3 ?? SRGB;
            }
        }
    }

    /// <summary>BT.2020 ICC Profile。</summary>
    public static byte[] BT2020
    {
        get
        {
            if (_bt2020 is not null) return _bt2020;
            lock (_lock)
            {
                if (_bt2020 is not null) return _bt2020;
                _bt2020 = ColorManagement.ColorProfileProvider.PatchSrgbPrimaries(
                    ColorManagement.IccPrimaries.BT2020, "BT.2020");
                return _bt2020 ?? SRGB;
            }
        }
    }

    /// <summary>根据色彩空间名称获取 ICC Profile。</summary>
    public static byte[]? GetByName(string colorSpace) => colorSpace switch
    {
        "sRGB" or "System" => SRGB,
        "AdobeRGB" => AdobeRGB1998,
        "DisplayP3" or "DCI_P3" => DisplayP3,
        "BT2020" => BT2020,
        _ => SRGB
    };
}





