// TrueToneCap.App/Services/FontLoader.cs
// 字体工具 — 提供字体选择 + 系统字体枚举辅助

namespace TrueToneCap.App.Services;

/// <summary>字体工具：提供默认字体回退链和用户字体选择支持。</summary>
public static class FontLoader
{
    /// <summary>默认字体回退链：微软雅黑 → Segoe UI → 系统后备。</summary>
    public const string DefaultFontFamily = "Microsoft YaHei, Segoe UI, sans-serif";

    /// <summary>获取当前有效的字体族字符串。空值或空字符串时返回默认回退链。</summary>
    public static string GetEffectiveFontFamily(string? userFontFamily)
    {
        if (string.IsNullOrWhiteSpace(userFontFamily))
            return DefaultFontFamily;
        return userFontFamily;
    }
}
