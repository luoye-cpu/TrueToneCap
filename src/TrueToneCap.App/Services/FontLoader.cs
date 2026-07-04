// TrueToneCap.App/Services/FontLoader.cs
// 内嵌字体加载器 — 从程序目录加载 .ttf 字体并注册到系统
// HarmonyOS Sans SC 内嵌：优先加载本地捆绑字体，回退到系统已安装字体

using System.Runtime.InteropServices;

namespace TrueToneCap.App.Services;

public static class FontLoader
{
    [DllImport("gdi32.dll")]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);
    [DllImport("gdi32.dll")]
    private static extern int RemoveFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    private const uint FR_PRIVATE = 0x10; // 仅当前进程可用，不污染系统
    private static readonly List<string> _loadedFonts = [];

    /// <summary>默认字体回退链：微软雅黑(系统预装) → 鸿蒙黑体(内嵌/已安装) → Segoe UI → 系统后备</summary>
    public const string DefaultFontFamily = "Microsoft YaHei, HarmonyOS Sans SC, Segoe UI, sans-serif";

    /// <summary>
    /// 从应用程序目录的 Fonts 子文件夹加载所有 .ttf/.otf 字体。
    /// 使用 FR_PRIVATE 标志：仅对当前进程可见，退出后自动释放。
    /// </summary>
    public static void LoadBundledFonts()
    {
        string fontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (!Directory.Exists(fontsDir))
        {
            System.Diagnostics.Debug.WriteLine($"[FontLoader] 字体目录不存在: {fontsDir}");
            return;
        }

        foreach (var file in Directory.GetFiles(fontsDir, "*.ttf"))
            LoadFontFile(file);
        foreach (var file in Directory.GetFiles(fontsDir, "*.otf"))
            LoadFontFile(file);

        if (_loadedFonts.Count > 0)
            System.Diagnostics.Debug.WriteLine($"[FontLoader] 已加载 {_loadedFonts.Count} 个内嵌字体");
    }

    private static void LoadFontFile(string path)
    {
        int result = AddFontResourceExW(path, FR_PRIVATE, IntPtr.Zero);
        if (result > 0)
        {
            _loadedFonts.Add(path);
            System.Diagnostics.Debug.WriteLine($"[FontLoader] ✓ {Path.GetFileName(path)}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[FontLoader] ✗ 加载失败: {Path.GetFileName(path)} (err={Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>卸载所有已加载的内嵌字体（应用退出时调用，FR_PRIVATE 会自动清理但显式释放更规范）。</summary>
    public static void UnloadBundledFonts()
    {
        foreach (var path in _loadedFonts)
            RemoveFontResourceExW(path, FR_PRIVATE, IntPtr.Zero);
        _loadedFonts.Clear();
    }
}
