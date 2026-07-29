// TrueToneCap.App/Services/RunReportService.cs
// 运行报告服务 — 每次截图/编码操作自动生成结构化报告存入 logs 文件夹
// 用户可在日志面板查看，也可在 logs/reports/ 目录浏览历史报告

using System.Text;

namespace TrueToneCap.App.Services;

/// <summary>运行报告服务：自动记录每次捕获/编码操作的结构化报告。</summary>
public static class RunReportService
{
    private static readonly string ReportDir = Path.Combine(LogService.LogDirectory, "reports");
    private static readonly object s_lock = new();
    private static int _sessionCount;

    /// <summary>初始化报告目录。</summary>
    public static void Initialize()
    {
        try
        {
            if (!Directory.Exists(ReportDir))
                Directory.CreateDirectory(ReportDir);
        }
        catch { }
    }

    /// <summary>报告目录路径（供 UI 显示）。</summary>
    public static string ReportsDirectory => ReportDir;

    /// <summary>
    /// 生成截图/编码操作报告并保存到 logs/reports/ 目录。
    /// 同时写入 LogService 以便日志面板实时显示。
    /// </summary>
    public static void ReportCapture(
        string format, int width, int height, bool isHdr,
        long captureTimeMs, long encodeTimeMs, long totalTimeMs,
        string outputPath, string? colorSpace, bool iccBaked,
        string? avifBackend = null, string? error = null)
    {
        int seq = Interlocked.Increment(ref _sessionCount);
        var now = DateTime.Now;

        var sb = new StringBuilder(512);
        sb.AppendLine($"╔══════════════════════════════════════════════");
        sb.AppendLine($"║ TrueToneCap 运行报告 #{seq:D4}");
        sb.AppendLine($"║ {now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"╠══════════════════════════════════════════════");
        sb.AppendLine($"║ 状态: {(error is null ? "✅ 成功" : "❌ 失败")}");
        sb.AppendLine($"║ 格式: {format}{(isHdr ? " (HDR)" : " (SDR)")}");
        sb.AppendLine($"║ 分辨率: {width}×{height}");
        sb.AppendLine($"║ 色彩空间: {colorSpace ?? "System"}");
        sb.AppendLine($"║ ICC 烘焙: {(iccBaked ? "是" : "否")}");
        if (avifBackend is not null)
            sb.AppendLine($"║ AVIF 后端: {avifBackend}");
        sb.AppendLine($"╠──────────────────────────────────────────────");
        sb.AppendLine($"║ ⏱ 捕获耗时: {captureTimeMs}ms");
        sb.AppendLine($"║ ⏱ 编码耗时: {encodeTimeMs}ms");
        sb.AppendLine($"║ ⏱ 总计耗时: {totalTimeMs}ms");
        sb.AppendLine($"╠──────────────────────────────────────────────");
        sb.AppendLine($"║ 📁 输出: {Path.GetFileName(outputPath)}");
        sb.AppendLine($"║    目录: {Path.GetDirectoryName(outputPath)}");
        if (error is not null)
        {
            sb.AppendLine($"╠──────────────────────────────────────────────");
            sb.AppendLine($"║ ❌ 错误: {error}");
        }
        sb.AppendLine($"╚══════════════════════════════════════════════");

        var report = sb.ToString();

        // 写入 LogService（日志面板实时可见）
        LogService.Info("Report", $"#{seq:D4} {format} {width}×{height} {(isHdr ? "HDR" : "SDR")} → {Path.GetFileName(outputPath)} ({totalTimeMs}ms)");

        // 保存报告文件
        try
        {
            lock (s_lock)
            {
                var fileName = $"report_{now:yyyyMMdd_HHmmss}_{seq:D4}.txt";
                File.WriteAllText(Path.Combine(ReportDir, fileName), report, Encoding.UTF8);
            }
        }
        catch { /* 报告写入失败不影响主流程 */ }
    }

    /// <summary>生成应用启动报告。</summary>
    public static void ReportStartup(
        bool gpuToneMapperAvailable, bool wgcAvailable,
        string gpuName, int displayCount, bool hdrEnabled)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder(384);
        sb.AppendLine($"╔══════════════════════════════════════════════");
        sb.AppendLine($"║ TrueToneCap 启动报告");
        sb.AppendLine($"║ {now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"╠══════════════════════════════════════════════");
        sb.AppendLine($"║ GPU: {gpuName}");
        sb.AppendLine($"║ GPU 色调映射: {(gpuToneMapperAvailable ? "✅ 可用" : "⚠ 不可用 (CPU 回退)")}");
        sb.AppendLine($"║ WGC 捕获: {(wgcAvailable ? "✅ 可用" : "❌ 不可用")}");
        sb.AppendLine($"║ 显示器数量: {displayCount}");
        sb.AppendLine($"║ HDR 启用: {(hdrEnabled ? "是" : "否")}");
        sb.AppendLine($"║ 日志目录: {LogService.LogDirectory}");
        sb.AppendLine($"║ 报告目录: {ReportDir}");
        sb.AppendLine($"╚══════════════════════════════════════════════");

        try
        {
            lock (s_lock)
            {
                var fileName = $"startup_{now:yyyyMMdd_HHmmss}.txt";
                File.WriteAllText(Path.Combine(ReportDir, fileName), sb.ToString(), Encoding.UTF8);
            }
        }
        catch { }

        LogService.Info("Report", $"启动: GPU={gpuName} HDR={hdrEnabled} 显示器={displayCount}");
    }

    /// <summary>获取最近的报告文件列表（供 UI 显示）。</summary>
    public static string[] GetRecentReports(int count = 10)
    {
        try
        {
            if (!Directory.Exists(ReportDir)) return [];
            return Directory.GetFiles(ReportDir, "*.txt")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(count)
                .ToArray();
        }
        catch { return []; }
    }
}
