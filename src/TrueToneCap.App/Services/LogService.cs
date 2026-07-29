// TrueToneCap.App/Services/LogService.cs
// 统一日志服务 — 全局日志记录，支持文件输出 + UI 实时显示
// 日志文件: {AppDir}/log/ 目录，按日轮转
// UI 显示: 内存环形缓冲区，通过 LogEntry 事件实时推送

using System.Collections.Concurrent;

namespace TrueToneCap.App.Services;

/// <summary>日志级别。</summary>
public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>日志条目（供 UI 绑定）。</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Tag { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }

    public string FormattedLine => $"{Timestamp:HH:mm:ss.fff} [{Level,-7}] [{Tag}] {Message}";
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
    public string ColorCode => Level switch
    {
        LogLevel.Error => "#FF4444",
        LogLevel.Warning => "#FFAA00",
        LogLevel.Info => "#CCCCCC",
        LogLevel.Debug => "#888888",
        _ => "#CCCCCC",
    };
    public string Icon => Level switch
    {
        LogLevel.Error => "❌",
        LogLevel.Warning => "⚠️",
        LogLevel.Info => "ℹ️",
        LogLevel.Debug => "🔍",
        _ => "ℹ️",
    };
}

/// <summary>统一日志服务（静态，全局可用）。</summary>
public static class LogService
{
    private static readonly object s_lock = new();
    private static string? s_logDir;
    private static string? s_currentLogPath;
    private static string? s_currentDate;
    private static long s_maxFileSize = 2_097_152; // 2MB 轮转

    // ── UI 环形缓冲区 ──
    private const int MaxUiEntries = 1000;
    private static readonly ConcurrentQueue<LogEntry> s_uiEntries = new();

    /// <summary>日志目录（应用主目录下的 log/ 文件夹）。</summary>
    public static string LogDirectory => s_logDir ?? Path.Combine(AppContext.BaseDirectory, "log");

    /// <summary>新日志条目事件（UI 订阅以实时刷新）。</summary>
    public static event Action<LogEntry>? OnLogEntry;

    /// <summary>获取当前 UI 日志条目快照。</summary>
    public static IReadOnlyList<LogEntry> GetUiEntries() => [.. s_uiEntries];

    /// <summary>初始化日志服务。</summary>
    public static void InitializeFileLog(string? logDir = null)
    {
        s_logDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "log");
        try
        {
            Directory.CreateDirectory(s_logDir);
            RotateLogFile();
            Info("LogService", $"日志系统初始化，目录: {s_logDir}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogService] 初始化失败: {ex.Message}");
            s_logDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_logs");
            Directory.CreateDirectory(s_logDir);
            RotateLogFile();
        }
    }

    /// <summary>按日期轮转日志文件。</summary>
    private static void RotateLogFile()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (s_currentDate == today && s_currentLogPath is not null)
        {
            try
            {
                var fi = new FileInfo(s_currentLogPath);
                if (fi.Exists && fi.Length < s_maxFileSize) return;
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

    // ── 公开日志方法 ──

    public static void Debug(string tag, string msg) => Write(LogLevel.Debug, tag, msg);
    public static void Info(string tag, string msg) => Write(LogLevel.Info, tag, msg);
    public static void Warn(string tag, string msg) => Write(LogLevel.Warning, tag, msg);
    public static void Error(string tag, string msg) => Write(LogLevel.Error, tag, msg);
    public static void Error(string tag, string msg, Exception ex) => Write(LogLevel.Error, tag, $"{msg}: {ex.Message}", ex.ToString());

    // ── 核心写入 ──

    private static void Write(LogLevel level, string tag, string msg, string? detail = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Tag = tag,
            Message = msg,
            Detail = detail,
        };

        // 控制台输出
        System.Diagnostics.Debug.WriteLine(entry.FormattedLine);

        // UI 环形缓冲区
        s_uiEntries.Enqueue(entry);
        while (s_uiEntries.Count > MaxUiEntries)
            s_uiEntries.TryDequeue(out _);

        // 触发 UI 事件
        try { OnLogEntry?.Invoke(entry); } catch { }

        // 文件日志（仅 Info+ 级别写入文件，避免 Debug 刷屏）
        if (level < LogLevel.Info) return;

        lock (s_lock)
        {
            try
            {
                RotateLogFile();
                if (s_currentLogPath is not null)
                    File.AppendAllText(s_currentLogPath, entry.FormattedLine + "\n");
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
}
