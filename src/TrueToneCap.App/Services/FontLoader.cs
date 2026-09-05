// TrueToneCap.App/Services/FontLoader.cs
// 字体工具 — 系统字体枚举 + 默认回退链 + 用户字体选择支持

using System.Runtime.InteropServices;

namespace TrueToneCap.App.Services;

/// <summary>字体信息。</summary>
public sealed record FontInfo(string Name, string DisplayName);

/// <summary>字体工具：提供系统字体枚举、默认字体回退链和用户字体选择支持。</summary>
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

    // ═══ Win32 字体枚举 (EnumFontFamiliesEx) ═══

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    // ═══ 2026-08-25 审查修复: 原回调签名用 ref class 封送为"指针的指针",
    // 与原生 FONTENUMPROCW (结构体指针) 布局错位 → 崩溃风险。
    // 改用 IntPtr + 手动读取 LOGFONT.lfFaceName (偏移 28, 32×wchar UTF-16)。
    private const byte DEFAULT_CHARSET = 1;
    private delegate bool EnumFontFamExProc(IntPtr lpelfe, IntPtr lpntme, uint FontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT lpLogfont,
        EnumFontFamExProc lpEnumFontFamExProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>从 ENUMLOGFONTEXW 指针读取字体名 (lfFaceName 偏移 30, LF_FACESIZE=32 wchar)。</summary>
    /// <remarks>LOGFONTW 布局: 5×int(20B) + 9×byte(9B) + 1B 对齐 = 偏移 30 处是 lfFaceName。实测确认。</remarks>
    private static string ReadFaceName(IntPtr pEnumLogFontEx)
    {
        var p = pEnumLogFontEx + 30;
        int len = 0;
        const int maxChars = 32;
        while (len < maxChars && System.Runtime.InteropServices.Marshal.ReadInt16(p, len * 2) != 0)
            len++;
        if (len == 0) return "";
        return System.Runtime.InteropServices.Marshal.PtrToStringUni(p, len) ?? "";
    }

    /// <summary>枚举系统已安装的所有字体（去重 + 常用优先排序）。</summary>
    public static IReadOnlyList<FontInfo> GetSystemFonts()
    {
        var fontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lf = new LOGFONT
        {
            lfCharSet = DEFAULT_CHARSET,
            lfFaceName = ""
        };

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            EnumFontFamiliesEx(hdc, ref lf, (IntPtr lpelfe, IntPtr lpntme, uint fontType, IntPtr lParam) =>
            {
                try
                {
                    var name = ReadFaceName(lpelfe);
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith('@'))
                        fontNames.Add(name);
                }
                catch { /* 单个字体读取失败不中断枚举 */ }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* 枚举失败时返回空列表 */ }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }

        // 排: 常用字体优先 + 按字母排
        var common = new[] { "Microsoft YaHei", "Segoe UI", "Microsoft YaHei UI",
            "Microsoft Sans Serif", "SimSun", "KaiTi", "SimHei", "FangSong",
            "Microsoft JhengHei", "Noto Sans SC", "Source Han Sans SC" };

        var ordered = fontNames
            .OrderByDescending(f => Array.IndexOf(common, f) >= 0 ? -Array.IndexOf(common, f) : int.MaxValue)

            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FontInfo(f, f))
            .ToList();
        return ordered;
    }
}
