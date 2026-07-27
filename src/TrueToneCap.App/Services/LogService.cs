// TrueToneCap.App/Services/LogService.cs
// 统一日志服务 — 替代散落的 Debug.WriteLine / Trace.WriteLine / Console.WriteLine
// 轻量实现：Debug 输出 + 可选文件日志（带轮转），无外部依赖

namespace TrueToneCap.App.Services;

/// <summary>日志级别。</summary>
public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>统一日志服务（静态，全局可用）。</summary>
public static class LogService
{
    private static readonly object s_lock = new();
    private static string? s_logPath;
    private static long s_maxFileSize = 1_048_576; // 1MB 轮转

    /// <summary>初始化文件日志（可选）。不调用则仅输出到 Debug。</summary>
    public static void InitializeFileLog(string? logPath = null, long maxFileSize = 1_048_576)
    {
        s_logPath = logPath ?? Path.Combine(Path.GetTempPath(), "TrueToneCap.log");
        s_maxFileSize = maxFileSize;
        try { File.WriteAllText(s_logPath, $"=== TrueToneCap {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
        catch { s_logPath = null; }
    }

    public static void Debug(string tag, string msg) => Write(LogLevel.Debug, tag, msg);
    public static void Info(string tag, string msg) => Write(LogLevel.Info, tag, msg);
    public static void Warn(string tag, string msg) => Write(LogLevel.Warning, tag, msg);
    public static void Error(string tag, string msg) => Write(LogLevel.Error, tag, msg);
    public static void Error(string tag, string msg, Exception ex) => Write(LogLevel.Error, tag, $"{msg}: {ex.Message}");

    private static void Write(LogLevel level, string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-7}] [{tag}] {msg}";

        // 始终输出到 Debug（开发调试）
        System.Diagnostics.Debug.WriteLine(line);

        // 文件日志（仅 Warning+ 或显式启用时）
        if (s_logPath is not null && level >= LogLevel.Info)
        {
            lock (s_lock)
            {
                try
                {
                    var fi = new FileInfo(s_logPath);
                    if (fi.Exists && fi.Length > s_maxFileSize)
                        File.WriteAllText(s_logPath, $"=== Log rotated {DateTime.Now} ===\n");
                    File.AppendAllText(s_logPath, line + "\n");
                }
                catch { /* 日志写入失败不影响主流程 */ }
            }
        }
    }
}
