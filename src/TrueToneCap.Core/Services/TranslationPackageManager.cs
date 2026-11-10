// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationPackageManager.cs
// 离线翻译语言包管理器 - 管理下载、安装和卸载
//
// 2026-11-11 重写要点（相对上一版孤儿代码）：
//   · 去掉 static 可变状态（s_installedPackages / AllPackages / SetModelDirectory）：
//     上一版实例构造函数里有静态副作用、且 InitializeInstalledPackages 会把 null 加进列表
//     （`s_installedPackages.Add(pkg!)`——磁盘上有不在预设清单里的 .pkg 文件时 pkg 为 null）。
//     现在改为纯实例类，构造时给定模型目录，扫描只认装得下的文件。
//   · 文件格式改认 .argosmodel（Argos 官方包的真实扩展名），不再用自造的 .pkg。
//   · 下载地址按官方索引解析（多镜像），全挂才落到 FallbackDownloadUrl；
//     本机网络连不上 GitHub raw 时索引走 jsDelivr 镜像（2026-11-11 本机实测 raw 超时）。
//   · 新增 InstallFromFileAsync：用户手动下载 .argosmodel 后可从本地文件导入，
//     这是大陆网络环境下的可靠兜底。

using System.IO;
using System.Net.Http;
using System.Text.Json;
using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Models;

namespace TrueToneCap.Core.Services;

/// <summary>
/// 离线翻译语言包管理器。
/// 负责下载、安装（含本地导入）、验证和卸载 Argos Translate 语言包。
/// <para>生命周期：一个进程一个实例即可（MainWindow 持有）。线程安全：
/// 下载/卸载并发由调用方 UI 串行化（每个包一行按钮），类内只对列表变更加锁。</para>
/// </summary>
public sealed class TranslationPackageManager
{
    /// <summary>Argos 官方包索引的镜像列表。顺序即尝试顺序：先 GitHub raw，
    /// 再 jsDelivr（国内可达性实测更好）。索引结构见 argospm-index 仓库，
    /// 每个条目含 from_code/to_code/links[]。</summary>
    private static readonly string[] s_indexUrls =
    {
        "https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json",
        "https://cdn.jsdelivr.net/gh/argosopentech/argospm-index@main/index.json",
    };

    /// <summary>索引里每个条目最长等待时间。两个镜像都试，最坏 ~16s 后落兜底直链。</summary>
    private static readonly TimeSpan s_indexTimeout = TimeSpan.FromSeconds(8);

    /// <summary>解析过的下载地址缓存（identifier → url）。索引内容基本不变，
    /// 一次会话内没必要为每个包都抓两遍。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_resolvedUrls = new();

    private readonly HttpClient _http;
    private readonly object _listLock = new();

    /// <summary>模型存储目录。</summary>
    public string ModelsDir { get; }

    /// <summary>已知包（预设清单 + 磁盘上多出来的本地导入包）。IsInstalled/LocalPath 按扫描结果就地更新。</summary>
    private readonly List<TranslationPackageInfo> _packages = new();

    /// <summary>已安装的语言包快照。</summary>
    public IReadOnlyList<TranslationPackageInfo> InstalledPackages
    {
        get { lock (_listLock) return _packages.Where(p => p.IsInstalled).ToList(); }
    }

    /// <summary>所有可用语言包（含未安装的预设与本地导入的额外包）。</summary>
    public IReadOnlyList<TranslationPackageInfo> AvailablePackages
    {
        get { lock (_listLock) return _packages.ToList(); }
    }

    /// <summary>
    /// 构造并扫描模型目录。<paramref name="modelsDir"/> 为空时用
    /// <see cref="OfflineArgosEngine.EffectiveModelsDir"/>（默认 %LOCALAPPDATA%\TrueToneCap\translation-models）。
    /// </summary>
    public TranslationPackageManager(string? modelsDir = null)
    {
        ModelsDir = Path.GetFullPath(modelsDir ?? OfflineArgosEngine.EffectiveModelsDir);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        Rescan();
    }

    /// <summary>重扫模型目录，刷新每个包的 IsInstalled/LocalPath；目录里多出的
    /// .argosmodel（用户手动导入或改名）也登记进来，不再像上一版那样丢给 null。</summary>
    public void Rescan()
    {
        lock (_listLock)
        {
            _packages.Clear();
            _packages.AddRange(TranslationPackageInfo.DefaultPackages.Select(p => new TranslationPackageInfo
            {
                Identifier = p.Identifier,
                NameEnglish = p.NameEnglish,
                NameChinese = p.NameChinese,
                SizeBytes = p.SizeBytes,
                SourceLanguageCode = p.SourceLanguageCode,
                TargetLanguageCode = p.TargetLanguageCode,
            }));

            if (!Directory.Exists(ModelsDir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(ModelsDir, "*.argosmodel", SearchOption.TopDirectoryOnly))
                {
                    var identifier = Path.GetFileNameWithoutExtension(file);
                    var pkg = _packages.FirstOrDefault(p => p.Identifier == identifier);
                    if (pkg is null)
                    {
                        // 不在预设清单里的包（如罕见语言对）：仍可被 Argos 运行时用，
                        // 登记为"本地导入"条目，名称先用标识符顶着。
                        var codes = TranslationPackageInfo.ParseIdentifier(identifier);
                        pkg = new TranslationPackageInfo
                        {
                            Identifier = identifier,
                            NameEnglish = identifier,
                            NameChinese = identifier,
                            SizeBytes = 0,
                            SourceLanguageCode = codes?.From ?? "",
                            TargetLanguageCode = codes?.To ?? "",
                        };
                        _packages.Add(pkg);
                    }
                    pkg.IsInstalled = true;
                    pkg.LocalPath = file;
                    try { pkg.SizeBytes = new FileInfo(file).Length; } catch { /* 读不到长度就用预估值 */ }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Error("TranslationPackage", $"扫描语言包目录失败: {ex.Message}", ex);
            }
        }
    }

    /// <summary>按标识符取包信息（含未安装的）。</summary>
    public TranslationPackageInfo? GetPackage(string identifier)
    {
        lock (_listLock) return _packages.FirstOrDefault(p => p.Identifier == identifier);
    }

    /// <summary>检查指定语言包是否已安装（磁盘有文件即算，列表状态只是缓存）。</summary>
    public bool IsPackageInstalled(string identifier)
    {
        if (File.Exists(Path.Combine(ModelsDir, identifier + ".argosmodel")))
            return true;
        lock (_listLock) return _packages.Any(p => p.Identifier == identifier && p.IsInstalled);
    }

    /// <summary>异步下载并安装语言包。进度回调在后台线程触发，UI 侧自行调度。</summary>
    public async Task<DownloadResult> DownloadPackageAsync(
        string identifier,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        var package = GetPackage(identifier);
        if (package is null)
            return new DownloadResult(false, $"语言包不存在：{identifier}");

        var finalFile = Path.Combine(ModelsDir, identifier + ".argosmodel");
        if (IsPackageInstalled(identifier))
            return new DownloadResult(false, $"语言包 '{package.NameChinese}' 已安装");

        var tempFile = Path.Combine(Path.GetTempPath(), $"ttc_{identifier}_{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(ModelsDir);
            var url = await ResolveDownloadUrlAsync(identifier, ct);
            CoreLog.Info("TranslationPackage", $"开始下载 {identifier}（源：{url}）");

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new DownloadResult(false, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");

            // 索引/直链都可能不给 ContentLength ⇒ 拿不到就用预估值，拿不到预估值就 -1（未知），
            // 进度回调里 total<=0 表示"只有字节增量没有百分比"。
            var totalBytes = response.Content.Headers.ContentLength
                ?? (package.SizeBytes > 0 ? package.SizeBytes : -1);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            long downloaded = 0;
            {
                // ⚠ fileStream 的释放必须先于下面的 File.Move：await using 挂在方法作用域时
                // 释放点在方法末尾，Move 会在写句柄仍打开时执行 ⇒ 必然报
                // "文件正由另一进程使用"。2026-11-11 端到端实测抓到（67MB 全部下完，
                // 最后一刻失败），用限定块把释放点提到 Move 之前。
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read == 0) break;
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    onProgress?.Invoke(downloaded, totalBytes);
                }
            }

            // 校验：只有响应方明确给出 ContentLength 才做长度对账（预估值不作数）。
            // 失败也要把临时文件清掉 —— 这条早退路径原先不清理，白留几十 MB 垃圾。
            if (response.Content.Headers.ContentLength is long expected && downloaded != expected)
            {
                CleanupTempFile(tempFile);
                return new DownloadResult(false, $"文件校验失败：预期 {expected} 字节，实际 {downloaded} 字节");
            }

            File.Move(tempFile, finalFile, overwrite: true);

            lock (_listLock)
            {
                package.IsInstalled = true;
                package.LocalPath = finalFile;
                package.SizeBytes = downloaded;
            }
            CoreLog.Info("TranslationPackage", $"下载完成 {identifier}（{TranslationPackageInfo.FormatSize(downloaded)}）");
            return new DownloadResult(true, $"成功安装 '{package.NameChinese}'（{TranslationPackageInfo.FormatSize(downloaded)}）");
        }
        catch (OperationCanceledException)
        {
            CleanupTempFile(tempFile);
            return new DownloadResult(false, "下载已取消");
        }
        catch (HttpRequestException ex)
        {
            CleanupTempFile(tempFile);
            CoreLog.Warn("TranslationPackage", $"下载 {identifier} 网络错误: {ex.Message}");
            return new DownloadResult(false, $"网络错误：{ex.Message}");
        }
        catch (IOException ex)
        {
            CleanupTempFile(tempFile);
            return new DownloadResult(false, $"文件操作错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            CleanupTempFile(tempFile);
            CoreLog.Error("TranslationPackage", $"下载 {identifier} 失败: {ex.Message}", ex);
            return new DownloadResult(false, $"未知错误：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析某语言包的真实下载地址：官方索引（多镜像）优先，索引失败落兜底直链。
    /// <para>解析结果进程级缓存 —— 索引不会在会话内变。</para>
    /// </summary>
    private async Task<string> ResolveDownloadUrlAsync(string identifier, CancellationToken ct)
    {
        if (s_resolvedUrls.TryGetValue(identifier, out var cached))
            return cached;

        foreach (var indexUrl in s_indexUrls)
        {
            // ⚠ 每个镜像一个 CTS：CancelAfter 在源已取消后不可复位，复用一个 CTS 会让
            // 第一个镜像超时后第二个镜像直接跳过（token 永久取消态）。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                timeoutCts.CancelAfter(s_indexTimeout);
                using var resp = await _http.GetAsync(indexUrl, timeoutCts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
                var codes = TranslationPackageInfo.ParseIdentifier(identifier);
                if (codes is null) break;

                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (!TryGetProp(entry, "from_code", out var from) || from != codes.Value.From ||
                        !TryGetProp(entry, "to_code", out var to) || to != codes.Value.To)
                        continue;
                    if (entry.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var link in links.EnumerateArray())
                        {
                            if (link.ValueKind == JsonValueKind.String && link.GetString() is { Length: > 0 } url
                                // 索引里有 ipfs:// 等非 http 链接：HttpClient 走不了这些协议，
                                // 只认 http(s)，让后面的链接（通常 argos-net.com 在前）接住。
                                && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                    || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                            {
                                s_resolvedUrls[identifier] = url;
                                return url;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单个镜像超时，换下一个
            }
            catch (Exception ex)
            {
                CoreLog.Debug("TranslationPackage", $"索引 {indexUrl} 解析失败: {ex.Message}");
            }
        }

        var fallback = GetPackage(identifier)?.FallbackDownloadUrl
            ?? $"https://argos-translate.acclivate.ninja/packages/{identifier}.argosmodel";
        CoreLog.Warn("TranslationPackage", $"索引不可用，{identifier} 落兜底直链");
        return fallback;
    }

    private static bool TryGetProp(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) ||
            el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return value.Length > 0;
    }

    /// <summary>
    /// 测试专用：强制某标识符的"已解析下载地址"（单元测试用本地 TcpListener 伪造源站，
    /// 真实走完 下载→校验→落盘 链路，不碰外网）。仅 Test 工程可见（InternalsVisibleTo）。
    /// </summary>
    internal static void ForceResolvedUrlForTests(string identifier, string url) =>
        s_resolvedUrls[identifier] = url;

    /// <summary>从本地 .argosmodel 文件导入（用户自行下载后手动安装的可靠通道）。</summary>
    public async Task<DownloadResult> InstallFromFileAsync(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return new DownloadResult(false, $"文件不存在：{sourcePath}");
            var identifier = Path.GetFileNameWithoutExtension(sourcePath);
            if (TranslationPackageInfo.ParseIdentifier(identifier) is null)
                return new DownloadResult(false, $"文件名不是语言包格式（应为 en_zh.argosmodel 这类）：{identifier}");
            if (IsPackageInstalled(identifier))
                return new DownloadResult(false, $"语言包 '{identifier}' 已安装");

            Directory.CreateDirectory(ModelsDir);
            var dest = Path.Combine(ModelsDir, identifier + ".argosmodel");
            await Task.Run(() => File.Copy(sourcePath, dest, overwrite: false));
            Rescan();
            CoreLog.Info("TranslationPackage", $"本地导入 {identifier} ← {sourcePath}");
            return new DownloadResult(true, $"成功导入 '{identifier}'");
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, $"导入失败：{ex.Message}");
        }
    }

    /// <summary>卸载指定的语言包（删除本地文件）。</summary>
    public bool UninstallPackage(string identifier)
    {
        try
        {
            var file = Path.Combine(ModelsDir, identifier + ".argosmodel");
            if (!File.Exists(file))
                return false;
            File.Delete(file);
            lock (_listLock)
            {
                var pkg = _packages.FirstOrDefault(p => p.Identifier == identifier);
                if (pkg is not null)
                {
                    pkg.IsInstalled = false;
                    pkg.LocalPath = null;
                }
            }
            CoreLog.Info("TranslationPackage", $"卸载语言包 {identifier}");
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Error("TranslationPackage", $"卸载语言包 '{identifier}' 失败: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>获取已安装包总数及总大小（按实际文件长度计）。</summary>
    public (int count, long bytes) GetInstalledSummary()
    {
        long bytes = 0;
        int count = 0;
        foreach (var pkg in InstalledPackages)
        {
            count++;
            try { bytes += pkg.LocalPath is not null ? new FileInfo(pkg.LocalPath).Length : pkg.SizeBytes; }
            catch { bytes += pkg.SizeBytes; }
        }
        return (count, bytes);
    }

    private static void CleanupTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略清理失败 */ }
    }

    /// <summary>下载结果封装。</summary>
    public sealed record DownloadResult(bool Success, string Message);
}
