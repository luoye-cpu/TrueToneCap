// TrueToneCap.Core/Services/NativeLibraryResolver.cs
// 原生工具链（avifenc.exe / cwebp.exe）嵌入资源提取器
// 内嵌在 Resources/ 下的 exe 在首次运行时提取到 native/ 目录

using System.Reflection;
using System.IO.Compression;

namespace TrueToneCap.Core.Services;

/// <summary>
/// 原生可执行文件解析器 — 从嵌入资源提取到 native/ 目录。
/// 支持: avifenc.exe (AVIF), cwebp.exe (WebP)
/// </summary>
public static class NativeLibraryResolver
{
    private static readonly string NativeDir;
    private static volatile bool _initialized;

    /// <summary>需要提取的原生可执行文件清单。</summary>
    private static readonly string[] EmbeddedExeNames = ["avifenc.exe", "cwebp.exe"];

    static NativeLibraryResolver()
    {
        NativeDir = Path.Combine(AppContext.BaseDirectory, "native");
    }

    /// <summary>初始化：确保所有内嵌的本机工具已提取到 native/ 目录。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        lock (typeof(NativeLibraryResolver))
        {
            if (_initialized) return;

            try
            {
                Directory.CreateDirectory(NativeDir);
                var asm = Assembly.GetExecutingAssembly();
                var allResNames = asm.GetManifestResourceNames();

                foreach (var exeName in EmbeddedExeNames)
                {
                    string targetPath = Path.Combine(NativeDir, exeName);
                    if (File.Exists(targetPath))
                    {
                        // 已存在则跳过
                        System.Diagnostics.Debug.WriteLine($"[NativeResolver] ✅ {exeName} 已就绪: {targetPath}");
                        continue;
                    }

                    // 查找嵌入资源 (Resources/exeName 或 Resources.{exeName})
                    string? resName = allResNames.FirstOrDefault(n =>
                        n.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith("Resources." + exeName, StringComparison.OrdinalIgnoreCase));

                    if (resName is null)
                    {
                        // 回退：从文件系统拷贝
                        string srcPath = Path.Combine(AppContext.BaseDirectory, "data", "Tools", exeName);
                        if (!File.Exists(srcPath))
                            srcPath = Path.Combine(AppContext.BaseDirectory, "Resources", exeName);
                        if (!File.Exists(srcPath))
                            srcPath = Path.Combine(Path.GetDirectoryName(asm.Location)!, "Resources", exeName);

                        if (File.Exists(srcPath))
                        {
                            File.Copy(srcPath, targetPath, overwrite: true);
                            System.Diagnostics.Debug.WriteLine($"[NativeResolver] ✅ {exeName} 从文件系统拷贝: {srcPath} → {targetPath}");
                            continue;
                        }

                        System.Diagnostics.Debug.WriteLine($"[NativeResolver] ⚠️ {exeName} 未找到 (嵌入资源或文件系统均无)");
                        continue;
                    }

                    // 从嵌入资源提取
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream is null) continue;

                    // 尝试压缩解压 (若资源是 .gz 后缀)
                    if (exeName.Contains('.'))
                    {
                        var plainName = exeName;
                        // 直接写入
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                    }
                    else
                    {
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                    }

                    System.Diagnostics.Debug.WriteLine($"[NativeResolver] ✅ {exeName} 已提取: {targetPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeResolver] ❌ 初始化失败: {ex.Message}");
            }

            _initialized = true;
        }
    }

    /// <summary>获取已提取的可执行文件完整路径。</summary>
    public static string GetExePath(string exeName)
    {
        Initialize();
        string path = Path.Combine(NativeDir, exeName);
        if (!File.Exists(path))
            throw new DllNotFoundException(
                $"[NativeResolver] {exeName} 未找到 (期望路径: {path})。请确保文件已正确嵌入 Resources/ 目录。");
        return path;
    }

    /// <summary>获取原生工具目录。</summary>
    public static string GetNativeDirectory() => NativeDir;
}