// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/NativeLibraryResolver.cs
// 原生工具链（avifenc.exe / cwebp.exe）嵌入资源提取器
// 内嵌在 Resources/ 下的 exe 在首次运行时提取到 native/ 目录

using System.Reflection;
using System.IO.Compression;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Services;

/// <summary>
/// 原生可执行文件解析器 — 从嵌入资源提取到 native/ 目录。
/// 支持: avifenc.exe (AVIF), cwebp.exe (WebP)
/// </summary>
public static class NativeLibraryResolver
{
    private static readonly string NativeDir;
    private static volatile bool _initialized;

    /// <summary>
    /// 最后一次初始化失败的原因（成功则为 null）。
    /// <para>⚠️ 提取失败原本只写 <c>Debug.WriteLine</c> —— Release 下完全不可见。
    /// 而 native 工具（avifenc / cjxl / cwebp / cjpegli）提取失败会让
    /// AVIF / JPEG XL / WebP / JPEG LI 四种格式**全部静默失效**。
    /// 上层应查询本属性并给出用户可见的提示与修复指引。</para>
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 重置初始化状态，下次调用将重新尝试提取。
    /// <para>原实现一旦失败就 `_initialized = true` 且永不复位 ——
    /// 若失败是瞬时的（杀软正在扫描、文件被占用、权限尚未生效），
    /// 用户即使解决了问题也必须重启程序。提供本方法作为恢复入口。</para>
    /// </summary>
    public static void Reset()
    {
        lock (typeof(NativeLibraryResolver))
        {
            _initialized = false;
            LastError = null;
        }
    }

    /// <summary>需要提取的原生可执行文件清单。</summary>
    private static readonly string[] EmbeddedExeNames = ["avifenc.exe", "cwebp.exe", "cjpegli.exe", "cjxl.exe"];

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
                // ═══ 使用 typeof 所在程序集（Core），而非调用方程序集（Test）═══
                // Assembly.GetExecutingAssembly() 在测试项目中返回 Test 程序集，
                // 而嵌入资源在 Core 程序集中，导致 cjpegli.exe 等无法提取。
                var asm = Assembly.GetAssembly(typeof(NativeLibraryResolver))!;
                var allResNames = asm.GetManifestResourceNames();

                foreach (var exeName in EmbeddedExeNames)
                {
                    string targetPath = Path.Combine(NativeDir, exeName);
                    if (File.Exists(targetPath))
                    {
                        // 已存在则跳过
                        CoreLog.Info("NativeResolver", $"✅ {exeName} 已就绪: {targetPath}", LogCategory.System);
                        continue;
                    }

                    // 查找嵌入资源 (Resources/exeName 或 Resources.{exeName})
                    string? resName = allResNames.FirstOrDefault(n =>
                        n.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith("Resources." + exeName, StringComparison.OrdinalIgnoreCase));

                    if (resName is null)
                    {
                        // 回退：从文件系统拷贝（使用 BaseDirectory，而非 Assembly.Location）
                        // 注意: Assembly.Location 在 AOT 单文件发布中返回空字符串
                        string coreDir = AppContext.BaseDirectory;
                        string srcPath = Path.Combine(coreDir, "Resources", exeName);
                        if (!File.Exists(srcPath))
                            srcPath = Path.Combine(AppContext.BaseDirectory, "Resources", exeName);
                        if (!File.Exists(srcPath))
                            srcPath = Path.Combine(AppContext.BaseDirectory, "data", "Tools", exeName);
                        if (!File.Exists(srcPath))
                            srcPath = Path.Combine(AppContext.BaseDirectory, "PLAN", "tools", exeName);

                        if (File.Exists(srcPath))
                        {
                            File.Copy(srcPath, targetPath, overwrite: true);
                            CoreLog.Warn("NativeResolver", $"✅ {exeName} 从文件系统拷贝（嵌入资源缺失，回退到文件系统）: {srcPath} → {targetPath}", LogCategory.System);
                            continue;
                        }

                        CoreLog.Warn("NativeResolver", $"⚠️ {exeName} 未找到 (嵌入资源或文件系统均无)", LogCategory.System);
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

                    CoreLog.Info("NativeResolver", $"✅ {exeName} 已提取: {targetPath}", LogCategory.System);
                }
            }
            catch (Exception ex)
            {
                // 记录到 LastError：Release 下 Debug.WriteLine 不可见，
                // 而本机工具提取失败会让 AVIF/JPEG XL/WebP/JPEG LI 全部静默失效。
                LastError = ex.Message;
                CoreLog.Warn("NativeResolver", "❌ 初始化失败", ex, LogCategory.System);
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
                $"[NativeResolver] {exeName} 未找到 (期望路径: {path})。请确保文件已嵌入 Resources/ 目录或位于 PLAN/tools/ 目录。");
        return path;
    }

    /// <summary>获取原生工具目录。</summary>
    public static string GetNativeDirectory() => NativeDir;
}