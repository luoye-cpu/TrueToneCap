// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/LogService.cs
// 统一日志服务 — 全局日志记录，支持文件输出 + UI 实时显示
// 日志文件: {AppDir}/log/ 目录，按日轮转
// UI 显示: 内存环形缓冲区，通过 LogEntry 事件实时推送
// 分类: System / Capture / Encoding / UI / OCR / Network
//
// ⚠ 等级与分类的定义在 TrueToneCap.Core.Logging（Core 也要能写日志，而 Core 不引用 App）。
//   本服务同时充当 CoreLog 的落点，见 InitializeFileLog 里的 Attach 调用。

using System.Collections.Concurrent;
using System.Threading.Tasks;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.App.Services;

/// <summary>日志条目（供 UI 绑定）。</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public LogCategory Category { get; init; }
    public string Tag { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }

    /// <summary>一次操作的关联 ID。多拍并发时这是唯一能把链路拆开的东西。</summary>
    public string? OpId { get; init; }

    // ── 2026-08-11: 调用者信息 (文件:行号) — 大幅提升日志定位能力 ──
    public string CallerFile { get; init; } = "";
    public int CallerLine { get; init; }
    public string CallerDisplay => CallerFile.Length == 0 ? "" : $" {CallerFile}:{CallerLine}";
    public string OpDisplay => string.IsNullOrEmpty(OpId) ? "" : $"#{OpId} ";
    public string FormattedLine => $"{Timestamp:HH:mm:ss.fff} [{Level,-7}] {OpDisplay}[{CategoryDisplay}/{Tag}] {Message}{CallerDisplay}";
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
    public string CategoryDisplay => Category switch
    {
        LogCategory.System => "系统",
        LogCategory.Capture => "捕获",
        LogCategory.Encoding => "编码",
        LogCategory.UI => "界面",
        LogCategory.OCR => "OCR",
        LogCategory.Network => "网络",
        _ => "其他",
    };
    public string ColorCode => Level switch
    {
        LogLevel.Error => "#FF4444",
        LogLevel.Warning => "#FFAA00",
        LogLevel.Info => "#CCCCCC",
        LogLevel.Debug => "#888888",
        _ => "#666666",
    };
    public string Icon => Level switch
    {
        LogLevel.Error => "❌",
        LogLevel.Warning => "⚠️",
        LogLevel.Info => "ℹ️",
        LogLevel.Debug => "🔍",
        LogLevel.Trace => "·",
        _ => "ℹ️",
    };
    public string CategoryIcon => Category switch
    {
        LogCategory.System => "⚙️",
        LogCategory.Capture => "📷",
        LogCategory.Encoding => "💾",
        LogCategory.UI => "🖥️",
        LogCategory.OCR => "🔤",
        LogCategory.Network => "🌐",
        _ => "📋",
    };
}


/// <summary>统一日志服务（静态，全局可用）。</summary>
public static class LogService
{
    private static readonly object s_lock = new();
    private static string? s_logDir;
    private static string? s_currentLogPath;
    private static string? s_currentDate;
    private static long s_maxFileSize = 4_194_304; // 4MB 轮转

    // ── 日志保留策略 ──
    private const int MaxLogFiles = 30;          // 最多保留 30 个日志文件
    private const int MaxLogDays = 30;           // 最多保留 30 天
    private static readonly TimeSpan s_cleanupInterval = TimeSpan.FromHours(1);
    private static DateTime s_lastCleanup = DateTime.MinValue;

    /// <summary>后台清理任务的取消源。由 <see cref="Shutdown"/> 取消，保证进程可干净退出。</summary>
    private static CancellationTokenSource? s_cleanupCts;

    // ── 2026-09-20：删除 FileDebugEnabled ──
    // 原声明注释写着"排障时可开"，但全仓没有任何一处给它赋值 ⇒ 恒 false，
    // 于是 App 侧所有 LogService.Debug(...)（含 WgcCaptureService 的每一条降级留痕）
    // 永远进不了日志文件。它的替代者是 CoreLog 的档位模型（Brief / Diagnose / Full）：
    // 2026-09-20 已接到 设置 → 行为 → "日志详细度"（MainWindow.Settings.ApplyLogVerbosity）。
    // ⚠ 设置读进来之前的启动早期日志仍固定按 Brief 落盘 —— 那几行里没有 Debug 级的东西。

    // ── UI 环形缓冲区 ──
    private const int MaxUiEntries = 2000;
    private static readonly ConcurrentQueue<LogEntry> s_uiEntries = new();

    /// <summary>日志目录（应用主目录下的 log/ 文件夹）。</summary>
    public static string LogDirectory => s_logDir ?? Path.Combine(AppContext.BaseDirectory, "log");

    /// <summary>新日志条目事件（UI 订阅以实时刷新）。</summary>
    public static event Action<LogEntry>? OnLogEntry;

    /// <summary>获取当前 UI 日志条目快照。</summary>
    /// <remarks>
    /// ⚠ 不要写成集合表达式 <c>[.. s_uiEntries]</c>：目标类型 <c>IReadOnlyList&lt;T&gt;</c>
    /// 是不可变接口，CsWinRT 源码生成器会报 CsWinRT1032（WinRT 场景下无法确定具体类型，
    /// 不兼容 trimming / AOT），且是 error 而非 warning。显式具体类型即可。
    /// </remarks>
    public static IReadOnlyList<LogEntry> GetUiEntries() => s_uiEntries.ToArray();

    /// <summary>
    /// 把本服务挂成 Core 的日志落点，并一次性写出挂载前的早期缓冲。
    /// Core 不引用 App，迁移前那 142 处 <c>Debug.WriteLine</c> 在 Release 下无处可去；
    /// 迁移完成后 Core 的 143 处 <c>CoreLog.*</c> 仍只有这一条落盘线路（交接 §35.1）。
    /// </summary>
    private static void AttachCoreSink()
    {
        if (CoreLog.HasSink) return;
        CoreLog.Attach((lvl, cat, tag, msg, file, line, op) =>
            Write(lvl, cat, tag, msg, null, file, line, op));
    }

    /// <summary>
    /// 应用日志详细度档位，并在日志里写一条抬头声明当时生效的档位——
    /// 否则一份日志文件无法自证它是在哪一档下产生的，"没记录"就分不清是"没发生"还是"没开"。
    /// </summary>
    public static void ApplyDetailLevel(LogDetail detail, IReadOnlyDictionary<LogCategory, LogDetail>? floors = null)
    {
        CoreLog.SetDetail(detail);
        if (floors is not null)
            foreach (var (cat, floor) in floors)
                CoreLog.SetCategoryFloor(cat, floor);

        var floorText = floors is { Count: > 0 }
            ? "，分类细调: " + string.Join("、", floors.Select(kv => $"{NameOf(kv.Key)}={NameOf(kv.Value)}"))
            : "";
        Info("LogService", $"日志详细度 = {NameOf(detail)}{floorText}");
    }

    private static string NameOf(LogDetail d) => d switch
    {
        LogDetail.Brief => "简",
        LogDetail.Diagnose => "诊断",
        _ => "全",
    };

    private static string NameOf(LogCategory c) => c switch
    {
        LogCategory.System => "系统",
        LogCategory.Capture => "捕获",
        LogCategory.Encoding => "编码",
        LogCategory.UI => "界面",
        LogCategory.OCR => "OCR",
        LogCategory.Network => "网络",
        _ => c.ToString(),
    };

    /// <summary>初始化日志服务。</summary>
    public static void InitializeFileLog(string? logDir = null)
    {
        // ═══ 2026-10-07：按运行形态选日志目录 ═══
        // MSIX 包的安装目录是【只读】的（官方："Write inside the package — Not allowed.
        // The package is read-only."）。未打包时日志写在 <程序目录>\log 属于"便携"语义，
        // 但打包后那里写不进去 —— 原实现会走下面的 catch 落到 %TEMP%\TrueToneCap_logs：
        // 不至于丢日志，但位置反直觉（用户在程序目录找不到 log/，也不知道去了哪儿）。
        // ⇒ 打包形态直接写 %LOCALAPPDATA%\TrueToneCap\log，与设置/密钥同一处，用户找得到。
        // ⚠️ 刻意不动未打包形态：绿色包用户仍在程序目录看到 log/。
        s_logDir = logDir ?? (TrueToneCap.Core.Settings.PackageRuntime.IsPackaged
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueToneCap", "log")
            : Path.Combine(AppContext.BaseDirectory, "log"));
        try
        {
            Directory.CreateDirectory(s_logDir);
            RotateLogFile();
            CleanupOldLogs();
            AttachCoreSink();          // 必须在 RotateLogFile 之后：早期缓冲要有地方落
            Info("LogService", $"日志系统初始化，目录: {s_logDir}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogService] 初始化失败: {ex.Message}");
            s_logDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_logs");
            Directory.CreateDirectory(s_logDir);
            RotateLogFile();
            AttachCoreSink();
        }

        // 启动后台定时清理任务（旧实现无取消机制，进程退出时任务仍在等待）
        //
        // ═══ 2026-09-15 修复：重复初始化会泄漏后台清理循环 ═══
        // 本方法在 App.xaml.cs:66 与 AppServices.cs:45 各被调用一次。
        // 原实现直接 `s_cleanupCts = new CancellationTokenSource()` 覆盖：
        // 既未 Cancel 也未 Dispose 旧的 ⇒ 第一个后台循环永远挂在 1 小时的 Task.Delay 上
        // （进程退出前不会醒来，也不会被取消），且此后每小时会有两个循环重复清理。
        // 这里先停掉上一次的，保证任意时刻最多一个清理循环。
        Shutdown();

        var cts = new CancellationTokenSource();
        s_cleanupCts = cts;
        // 捕获【局部】token：若仍读 s_cleanupCts.Token，一旦字段被后续初始化替换，
        // 本循环就会与新 CTS 纠缠（取消新循环时旧循环不停，反之亦然）。
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 用 s_cleanupInterval 常量（旧实现把 1 小时硬编码为 3600000ms）
                    await Task.Delay(s_cleanupInterval, token);
                    CleanupOldLogs();
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // 清理失败不致命；继续等待下一个周期（但避免异常时忙循环）
                    try { await Task.Delay(s_cleanupInterval, token); } catch (OperationCanceledException) { break; }
                }
            }
        });
    }

    /// <summary>停止后台清理任务。应用退出时调用，避免进程无法干净退出。</summary>
    public static void Shutdown()
    {
        s_cleanupCts?.Cancel();
        s_cleanupCts?.Dispose();
        s_cleanupCts = null;
    }

    /// <summary>按日期轮转日志文件。同一天超过大小上限时创建分片 app_日期_N.log。</summary>
    private static void RotateLogFile()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (s_currentDate == today && s_currentLogPath is not null)
        {
            try
            {
                var fi = new FileInfo(s_currentLogPath);
                if (fi.Exists && fi.Length < s_maxFileSize) return;
                // 超 4MB → 分片轮转 (不覆盖)
                if (fi.Exists)
                {
                    s_currentLogPath = NextSlicePath(today);
                    File.AppendAllText(s_currentLogPath, $"=== TrueToneCap 日志分片 [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===\n");
                    return;
                }
            }
            catch { }
        }
        s_currentDate = today;
        s_currentLogPath = Path.Combine(s_logDir!, $"app_{today}.log");
        try
        {
            File.AppendAllText(s_currentLogPath, $"=== TrueToneCap 日志 [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===\n");
        }
        catch { }
    }

    /// <summary>生成下一个分片路径 app_日期_N.log。</summary>
    private static string NextSlicePath(string today)
    {
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(s_logDir!, $"app_{today}_{i}.log");
            if (!File.Exists(p)) return p;
        }
    }

    // ── 公开日志方法（带分类 + 调用者信息） ──

    public static void Debug(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Write(LogLevel.Debug, cat, tag, msg, null, file, line);

    public static void Info(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Write(LogLevel.Info, cat, tag, msg, null, file, line);

    public static void Warn(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Write(LogLevel.Warning, cat, tag, msg, null, file, line);

    public static void Error(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Write(LogLevel.Error, cat, tag, msg, null, file, line);

    public static void Error(string tag, string msg, Exception ex, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Write(LogLevel.Error, cat, tag, $"{msg}: {ex.Message}", ex.ToString(), file, line);

    // ── 核心写入 ──

    private static void Write(LogLevel level, LogCategory category, string tag, string msg, string? detail,
        string? callerFile, int callerLine, string? opId = null)
    {
        // 隐私地板：判据只有一份，在 CoreLog.Scrub（App 与 Core 共用）。
        // 这条路径今天就已经用得上 —— Error(tag, msg, ex) 把 ex.ToString() 当 detail 传进来，
        // 而 HttpRequestException 的文本可能带请求 URI，翻译请求的 URI 里挂着 q=<OCR 原文>。
        msg = CoreLog.Scrub(msg);
        if (detail is not null) detail = CoreLog.Scrub(detail);

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Tag = tag,
            Message = msg,
            Detail = detail,
            OpId = opId ?? CoreLog.CurrentOpId,
            CallerFile = Path.GetFileName(callerFile ?? ""),
            CallerLine = callerLine,
        };

        // 控制台输出
        System.Diagnostics.Debug.WriteLine(entry.FormattedLine);
        if (detail is not null)
            System.Diagnostics.Debug.WriteLine(detail);

        // UI 环形缓冲区
        s_uiEntries.Enqueue(entry);
        while (s_uiEntries.Count > MaxUiEntries)
            s_uiEntries.TryDequeue(out _);

        // 触发 UI 事件
        try { OnLogEntry?.Invoke(entry); } catch { }

        // 文件日志的门控与 Core 共用一个判据（档位 + 分类地板），Warn/Error 恒写。
        // UI 环形缓冲区与上面的控制台镜像不受此门控 —— 档位管的是"文件里留不留"，
        // 不是"屏幕上看不看"。
        if (!CoreLog.IsEnabled(category, level)) return;

        lock (s_lock)
        {
            try
            {
                RotateLogFile();
                if (s_currentLogPath is not null)
                {
                    File.AppendAllText(s_currentLogPath, entry.FormattedLine + "\n");
                    if (detail is not null)
                        File.AppendAllText(s_currentLogPath, "    └─ " + detail.Replace("\n", "\n       ") + "\n");
                }
            }
            catch { }
        }
    }

    /// <summary>获取日志文件列表。</summary>
    public static string[] GetLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return [];
            return Directory.GetFiles(LogDirectory, "app_*.log")
                .OrderByDescending(f => f)
                .ToArray();
        }
        catch { return []; }
    }

    /// <summary>打开日志文件所在目录。</summary>
    public static void OpenLogDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", LogDirectory);
        }
        catch { }
    }

    /// <summary>清理过期日志文件。按数量和天数双重限制。</summary>
    public static void CleanupOldLogs()
    {
        try
        {
            // 限频：每小时最多执行一次
            if (DateTime.Now - s_lastCleanup < s_cleanupInterval) return;
            s_lastCleanup = DateTime.Now;

            var logDir = LogDirectory;
            if (!Directory.Exists(logDir)) return;

            var files = Directory.GetFiles(logDir, "app_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            // 删除过期文件（超过 30 天）
            var cutoff = DateTime.Now.AddDays(-MaxLogDays);
            foreach (var file in files)
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); System.Diagnostics.Debug.WriteLine($"[LogService] 删除过期日志: {file.Name}"); } catch { }
                }
            }

            // 如果数量仍超过上限，删除最旧的
            var remaining = Directory.GetFiles(logDir, "app_*.log")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();
            while (remaining.Count > MaxLogFiles)
            {
                var oldest = remaining[0];
                try { oldest.Delete(); System.Diagnostics.Debug.WriteLine($"[LogService] 删除超量日志: {oldest.Name}"); } catch { }
                remaining.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogService] 清理日志失败: {ex.Message}");
        }
    }
}
