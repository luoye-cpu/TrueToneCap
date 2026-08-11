// TrueToneCap.LogViewer/Services/LogParser.cs
// 日志行解析器 — 完整解析 TrueToneCap 日志格式:
//   HH:mm:ss.fff [Level   ] [分类/Tag] Message 文件.cs:行号
//   HH:mm:ss.fff [Level   ] [CategoryDisplay/Tag] Message 文件.cs:行号
//   异常堆栈: "    └─ ..." 缩进行 (属于上一条)

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TrueToneCap.LogViewer.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Unknown,
}

/// <summary>解析后的日志条目。</summary>
public sealed class ParsedLogEntry
{
    // 注意: 属性必须可 set — XAML 类型信息生成器 (XamlTypeInfo.g.cs) 用对象初始化器创建
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; } = LogLevel.Unknown;
    public string Category { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Message { get; set; } = "";
    public string Caller { get; set; } = "";
    public string Raw { get; set; } = "";
    /// <summary>后续的堆栈/详情行 (异常详情)。</summary>
    public List<string> Details { get; } = [];

    public bool IsHeader => Level != LogLevel.Unknown;

    // UI 展示
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelDisplay => Level switch
    {
        LogLevel.Error => "错误",
        LogLevel.Warning => "警告",
        LogLevel.Info => "信息",
        LogLevel.Debug => "调试",
        _ => "未知",
    };
    public string LevelIcon => Level switch
    {
        LogLevel.Error => "❌",
        LogLevel.Warning => "⚠️",
        LogLevel.Info => "ℹ️",
        LogLevel.Debug => "🔍",
        _ => "❔",
    };
    public string CategoryIcon => Category switch
    {
        "系统" => "⚙️",
        "捕获" => "📷",
        "编码" => "💾",
        "界面" => "🖥️",
        "OCR" => "🔤",
        "网络" => "🌐",
        _ => "📋",
    };
    public string ColorHex => Level switch
    {
        LogLevel.Error => "#FF6B6B",
        LogLevel.Warning => "#FFB454",
        LogLevel.Info => "#D8D8D8",
        LogLevel.Debug => "#8A8A8A",
        _ => "#AAAAAA",
    };

    // x:Bind 辅助
    public Visibility CallerVisibility => string.IsNullOrEmpty(Caller) ? Visibility.Collapsed : Visibility.Visible;
    private Brush? _colorBrush;
    public Brush ColorBrush => _colorBrush ??= new SolidColorBrush(ColorFromHex(ColorHex));

    private static Windows.UI.Color ColorFromHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(
                255,
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
        }
        catch { return Windows.UI.Color.FromArgb(255, 200, 200, 200); }
    }
}

/// <summary>日志解析器。</summary>
public static class LogParser
{
    // 主格式: 03:17:36.052 [Info   ] [捕获/WgcCapture] 会话启动: 3840x2160 SDR 126ms WgcCaptureService.cs:52
    // 兼容英文: [System/HdrCapture] / [Capture/WgcCapture]
    private static readonly Regex s_headerRegex = new(
        @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w+)\s*\]\s+\[([^/\]]+)/([^\]]+)\]\s+(.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 旧格式(无调用者): 直接以消息结尾
    private static readonly Regex s_headerNoCallerRegex = new(
        @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w+)\s*\]\s+\[([^/\]]+)/([^\]]+)\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 调用者: 行尾 " 文件.cs:行号"
    private static readonly Regex s_callerRegex = new(
        @"\s+([A-Za-z0-9_]+\.cs):(\d+)\s*$",
        RegexOptions.Compiled);

    /// <summary>解析一行。返回 null 表示非日志头 (可能是堆栈/空行/分隔线)。</summary>
    public static ParsedLogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith("──") || line.StartsWith("===")) return null; // 文件头/分隔线

        var m = s_headerRegex.Match(line);
        if (!m.Success) m = s_headerNoCallerRegex.Match(line);
        if (!m.Success) return null;

        var entry = new ParsedLogEntry
        {
            Timestamp = ParseTime(m.Groups[1].Value),
            Level = ParseLevel(m.Groups[2].Value),
            Category = m.Groups[3].Value.Trim(),
            Tag = m.Groups[4].Value.Trim(),
            Message = m.Groups[5].Value.Trim(),
            Raw = line,
        };

        // 提取调用者 (文件.cs:行号)
        var cm = s_callerRegex.Match(entry.Message);
        if (cm.Success)
        {
            entry.Caller = $"{cm.Groups[1].Value}:{cm.Groups[2].Value}";
            entry.Message = entry.Message[..cm.Index].TrimEnd();
        }

        return entry;
    }

    /// <summary>判断是否为详情/堆栈行 (属于上一条日志)。</summary>
    public static bool IsDetailLine(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        (line.TrimStart().StartsWith("└─") || line.TrimStart().StartsWith("at "));

    private static DateTime ParseTime(string hms)
    {
        if (DateTime.TryParseExact(hms, "HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
        {
            return DateTime.Today.Add(t.TimeOfDay);
        }
        return DateTime.Today;
    }

    private static LogLevel ParseLevel(string s) => s.Trim().ToLowerInvariant() switch
    {
        "error" => LogLevel.Error,
        "warning" => LogLevel.Warning,
        "info" => LogLevel.Info,
        "debug" => LogLevel.Debug,
        _ => LogLevel.Unknown,
    };
}
