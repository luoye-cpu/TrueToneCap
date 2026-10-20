// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/NativeLibraryResolver.cs
// 原生工具链（avifenc / cwebp / cjpegli / cjxl）就位解析器
//
// ⚠ 2026-10-01 更正本文件头与类注释的口径：这些 exe **不是嵌入资源**。
//   TrueToneCap.Core.csproj 里【没有任何 EmbeddedResource 条目】，Core.dll 内也没有这四枚文件
//   （实测：从 dotnet build 的产物起产品，四条 `⚠️ X.exe 未找到 (嵌入资源或文件系统均无)` 每次都打）。
//   所以下面 GetManifestResourceNames() 那条"从嵌入资源提取"的分支【从未命中】，
//   真正在跑的只有它后面的【文件系统回退链】：BaseDirectory 下的 Resources/ → data/Tools/ → PLAN/tools/。
//   而把 exe 放进那些位置的，是 Publish.ps1 的 [3/6]（单一事实源 = Core/Resources/）。
//   后果：裸 `dotnet build` 的 bin 里 native/ 是空的 ⇒ AVIF / JPEG XL / WebP / JPEG LI
//   四种格式在开发构建上一律走保底（本轮实机就量到"选 JPEG XL 拿到 PNG"）。
//   开发构建侧现在由 TrueToneCap.App.csproj 的 CopyNativeToolsToDevOutput 目标补齐同一份布局，
//   与发布包逐字同源；两处口径若再分叉，那条目标会先红。

using System.Reflection;
using System.IO.Compression;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Services;

/// <summary>
/// <summary>
/// 原生可执行文件解析器 — 从嵌入资源提取到 native/ 目录。
/// 支持: avifenc.exe (AVIF), cjxl.exe (JPEG XL), cjpegli.exe (JPEG LI), cwebp.exe (WebP)
/// <para>⚠️ 2026-10-07 商店上架核查时确认：本类会<b>尝试写入</b> <c>BaseDirectory\native</c>，
/// 而 MSIX 包目录是<b>只读</b>的（官方："Write inside the package — Not allowed."）。
/// 在打包形态下这不会出问题，理由是<b>两个条件同时成立</b>：
/// ① 4 枚 exe 已作为 <c>Content</c> 随包分发，<c>File.Exists(targetPath)</c> 先命中并 <c>continue</c>，
///    根本走不到写分支（实测解包确认 native\ 下 4 枚俱在）；
/// ② 即便真的去写，整个方法体在 <c>try/catch</c> 内，失败只记录 <see cref="LastError"/> 并告警，
///    不会让启动崩溃。
/// ⇒ <b>不需要为 MSIX 改动本类</b>；但若将来把 native 工具改成"不随包分发、运行时提取"，
///    这两个条件会同时失效，那时必须改为提取到可写目录（如 %LOCALAPPDATA%）。</para>
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