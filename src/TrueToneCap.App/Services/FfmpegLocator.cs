// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/FfmpegLocator.cs
// ffmpeg 可执行文件解析器（2026-11-02 D 流第 3 步）—— 录制动图的唯一外部依赖收口在此。
//
// 为什么必须有这一层：AnimationRecorder 起子进程时原来写死 FileName="ffmpeg"，
// 全靠 PATH 命中。ffmpeg 是全应用唯一的 PATH 强依赖，便携版 / Scoop / 多版本共存者
// 未必把它放进 PATH ⇒ 设置里给了 FfmpegPath（AppSettingsData，空 = 自动解析）之后，
// 由本类按统一顺序解析并缓存，避免每次录制都 fork 一遍探测进程。
//
// ⚠ 铁律：解析【绝不能抛异常打断调用方】。录制是可选功能，截图是主线；
//    探测进程超时 / 权限拒绝 / 环境变量损坏都必须落到"返回找不到 + 原因"，不是往上炸。

namespace TrueToneCap.App.Services;

internal static class FfmpegLocator
{
    /// <summary>探测子进程的超时（毫秒）。ffmpeg -version 冷启动实测 <1s，3s 已是 3 倍余量。</summary>
    private const int ProbeTimeoutMs = 3000;

    private static readonly object _lock = new();
    /// <summary>(配置键, 解析结果, 来源) 三元组缓存。只缓存成功；失败不缓存 ——
    /// 用户装完 ffmpeg 不该被一次"当时没找到"钉死到重启。</summary>
    private static (string ConfiguredKey, string Path, string Source)? _cache;

    /// <summary>解析 ffmpeg 可执行文件。顺序：设置指定 → PATH 逐目录 → 常见安装位置。
    /// 返回 (null, 原因) = 找不到；永不抛异常。</summary>
    public static (string? Path, string Source) Resolve(string configuredPath)
    {
        try
        {
            lock (_lock)
            {
                var key = configuredPath?.Trim() ?? "";
                if (_cache is { } hit && hit.ConfiguredKey == key)
                    return (hit.Path, hit.Source);

                var result = ResolveCore(key);
                if (result.Path is not null)
                    _cache = (key, result.Path, result.Source);
                return result;
            }
        }
        catch (Exception ex)
        {
            // 这条 catch 是本类的存在理由：任何一层的环境异常（PATH 含非法字符、
            // 目录不可枚举…）都不许打断录制入口，更不许打断截图链路。
            LogService.Warn("FfmpegLocator", $"探测异常（按找不到处理）: {ex.Message}");
            return (null, $"探测异常: {ex.Message}");
        }
    }

    /// <summary>丢弃缓存。留作显式出口：用户改设置 / 改 PATH 后由调用方触发；
    /// 即使无人调，缓存也按"配置键不匹配即失效"工作（配置变更自动重解析）。</summary>
    public static void Invalidate()
    {
        lock (_lock) _cache = null;
    }

    /// <summary>
    /// 给"设置里手填的那个路径"一个当场可读的判断（2026-11-03 遗留 3 的配套）。
    /// <para>返回 <c>null</c> = 没问题（含"留空 = 自动解析"，那是合法值，不是缺失）；
    /// 返回非空串 = 一句能直接显示给用户的中文原因。<b>永不抛异常</b> —— 它长在保存路径上，
    /// 保存设置不该因为一次文件探测而失败。</para>
    /// <para>⚠ 与 <see cref="Resolve"/> 的分工：Resolve 决定"最终用哪个 ffmpeg"（会回退、会探测），
    /// 这里只回答"用户填的这一格本身像不像话"。两者刻意不共用代码：把探测塞进保存路径会让
    /// 每次点保存都 fork 一个 ffmpeg -version（内层 3s 超时），而这一步只需要文件系统事实。</para>
    /// <para>⚠ 只做存在性/形状判断，<b>不</b>实跑 <c>-version</c>：保存是同步 UI 路径，
    /// 不能为一句提示起子进程。真实可执行性留给 Resolve 的探测，它在真录制时才发生。</para>
    /// </summary>
    public static string? DescribePathIssue(string? configuredPath)
    {
        try
        {
            var p = configuredPath?.Trim() ?? "";
            if (p.Length == 0) return null;                 // 留空 = 自动解析，合法
            // 只有目录没有文件名：用户常从"打开所在文件夹"复制路径 ⇒ 给它一句能照做的提示
            if (Directory.Exists(p)) return "这是一个目录，请填到 ffmpeg.exe（例如 " + Path.Combine(p, "ffmpeg.exe") + "）";
            if (!File.Exists(p)) return $"文件不存在：{p}";
            // 扩展名不是 .exe：Windows 下 Process.Start 未必起得来，属于"填了但不是它"的常见笔误
            if (!string.Equals(Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase))
                return $"不是 .exe 文件：{p}";
            return null;
        }
        catch (Exception ex)
        {
            // ⚠ 实测：在当前 .NET 11 上这条 catch 进不去 —— File.Exists / Directory.Exists 对
            //   "非法字符 / 超长路径 / 无权限 / UNC / 末尾反斜杠"一律返回 false 而不抛，
            //   Path.GetExtension 只在入参为 null 时抛而上面已经用 `?? ""` 挡掉（2026-11-03 探针实测 11 类边界输入，零抛出）。
            //   留着它是【防御】：这段承诺是"对 UI 永不抛"，而承诺靠的不该是"今天的 BCL 恰好不抛"——
            //   BCL 哪天改了这些方法的异常行为，或者将来有人往这个同步 UI 路径里塞一个真会抛的调用，
            //   这里就是最后一道闸。判据钉的是"函数有 catch 兜底"这个形状，不是"哪条输入会触发它"。
            return $"路径无法识别：{ex.Message}";
        }
    }

    private static (string? Path, string Source) ResolveCore(string configured)
    {
        // ① 设置指定（非空 = 优先）。指向无效时记日志后【继续】自动解析 —— 用户填错路径
        //    不该让录制彻底哑掉，但必须留下可追查的痕迹。
        if (configured.Length > 0)
        {
            var candidate = configured.Trim().Trim('"').Trim();
            if (candidate.Length > 0 && File.Exists(candidate) && Probe(candidate))
                return (candidate, "设置指定");
            LogService.Info("FfmpegLocator",
                $"设置指定的 ffmpeg 路径不可用（{(candidate.Length == 0 ? "空白" : File.Exists(candidate) ? "探测失败" : "文件不存在")}），回退自动解析");
        }

        // ② PATH 逐目录探。不用 where.exe / 裸名起进程：那要 fork 才知道结果，
        //    目录扫描 + 一次探测就够，且拿到的是完整路径（日志可读）。
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(d => d.Trim('"')))   // Windows 允许 PATH 条目带引号；不剥则 File.Exists 恒败（审查轮 R1-P2-6）
        {
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate) && Probe(candidate))
                    return (candidate, "PATH");
            }
            catch { /* 单个目录坏了不拖累整体 */ }
        }

        // ③ 常见安装位置（2026-11-02 台账 §5.2 第 3 步点名的三处）。
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),   // 便携版旁挂
        })
        {
            try
            {
                if (File.Exists(candidate) && Probe(candidate))
                    return (candidate, "常见位置");
            }
            catch { /* 同上 */ }
        }

        return (null, "未找到（试过：设置指定 → PATH → 常见安装位置）");
    }

    /// <summary>实跑 <c>ffmpeg -version</c> 验证：文件存在不等于能跑（损坏 / 版本不兼容 / 权限）。
    /// 成功时把版本串打进日志（排查录制问题时的第一锚点）。任何异常 = 探测失败。
    /// ⚠ 两个流都要先起异步读再等退出（审查轮 R1-P1-1）：先 Result 后 WaitForExit 的写法
    /// 会被"不关 stdout 的候选进程"无限卡住（还在锁内、还在 UI 线程），stderr 不排空
    /// 还可能把健康的 ffmpeg 误判为失败 —— 与 AnimationRecorder 的 ffmpeg 收尾同一形状。</summary>
    private static bool Probe(string exePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-hide_banner -version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(ProbeTimeoutMs))
            {
                try { proc.Kill(); } catch { }
                try { Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult(); } catch { }   // 排空管道，别留未观察任务
                return false;
            }
            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            var stdout = stdoutTask.Result;
            if (proc.ExitCode != 0 || !stdout.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                return false;
            var versionLine = stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            LogService.Info("FfmpegLocator", $"ffmpeg 就绪: {exePath}（{versionLine}）");
            return true;
        }
        catch { return false; }
    }
}
