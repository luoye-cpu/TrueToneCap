// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
    /// <summary>调用者文件名（不含行号），窗口归属由它推出来。</summary>
    public string CallerFile { get; set; } = "";
    /// <summary>这行出自哪个 UI 窗口；空串 = 不属于任何窗口（服务类/框架行）。</summary>
    public string WindowName { get; set; } = "";
    /// <summary>覆盖层实例号（进程内单调递增）；0 = 这一行没带实例打点。</summary>
    public int InstanceSeq { get; set; }
    /// <summary>该实例进入激活回调的第几次；0 = 这一行没带次数。</summary>
    public int ActivationSeq { get; set; }
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
    public Visibility WindowVisibility => string.IsNullOrEmpty(WindowName) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>窗口徽标文本。带图标前缀，免得裸窗口名被读成又一个 Tag。</summary>
    public string WindowDisplay => WindowName.Length == 0 ? "" : $"🖥 {WindowName}";

    /// <summary>实例身份徽标文本：把两个计数器拼成一句，读的人不必再回原文找。</summary>
    public string InstanceDisplay => InstanceSeq > 0
        ? ActivationSeq > 0 ? $"实例#{InstanceSeq} 第{ActivationSeq}次" : $"实例#{InstanceSeq}"
        : ActivationSeq > 0 ? $"Activated #{ActivationSeq}" : "";
    public Visibility InstanceVisibility => string.IsNullOrEmpty(InstanceDisplay) ? Visibility.Collapsed : Visibility.Visible;
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
    // ⚠ 级别方括号与 [分类/Tag] 之间还可能有一段 #opId（LogService.cs:36 的 OpDisplay，
    //   2026-09-21 起一次截图操作期间写的行都带它）。这一段必须是【可选且被吃掉】：
    //   原先要求 `]` 之后紧跟 `[` ⇒ 带 opId 的行整条判 null，日志查看器里【一条都不显示】，
    //   而那恰恰是排障时最需要看的那批行。非捕获组 ⇒ 后面的分组编号不变。
    private static readonly Regex s_headerRegex = new(
        @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w+)\s*\]\s+(?:#\S+\s+)?\[([^/\]]+)/([^\]]+)\]\s+(.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 旧格式(无调用者): 直接以消息结尾
    private static readonly Regex s_headerNoCallerRegex = new(
        @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w+)\s*\]\s+(?:#\S+\s+)?\[([^/\]]+)/([^\]]+)\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 调用者: 行尾 " 文件.cs:行号"
    // ⚠ 文件名必须允许【多个点】。本仓窗口的源文件是 XAML 代码后置形状 (MainWindow.xaml.cs /
    //   SelectionOverlay.xaml.cs) 与分部类切片 (MainWindow.Settings.cs)，
    //   旧判据 [A-Za-z0-9_]+\.cs 只吃"一个点的名字" ⇒ 这两类行的 Caller 恒空、
    //   "文件.cs:行号"整段粘在 Message 尾巴上当文案（2026-09-30 真实日志实测: 739 行受影响，
    //   其中覆盖层 366 行 —— 恰好是排障最需要按窗口归因的那一批）。
    private static readonly Regex s_callerRegex = new(
        @"\s+([A-Za-z0-9_.]+\.cs):(\d+)\s*$",
        RegexOptions.Compiled);

    // ── 窗口归属：这行出自哪个 UI 窗口 ──
    // 三条形状（2026-09-30 逐个核实过本仓的窗口类）：
    //   ① X.xaml.cs —— XAML 代码后置。除 App.xaml.cs（那是 Application）外全是 : Window。
    //   ② XWindow.cs —— 自绘窗口，HdrCaptureWindow 自己 CreateWindowExW 一个 HWND
    //      （曾经还多一个 HDRPreviewWindow，2026-10-01 报告 §39 删除；形状与本条无关，正则按 *Window.cs 认）。
    //   ③ MainWindow.<切片>.cs —— 同一个窗口类的分部文件，归给切片所属的窗口。
    // ⚠ 新增 UserControl/Page 的 .xaml.cs 时形状①会把它误认成窗口，判据要跟着收紧。
    // ⚠ 这条正则与 TrueToneCap.Test 的镜像常量【逐字】比对（Test 不引用 LogViewer，只能抄一份锁漂移），
    //   改这里必须同步改那条用例，否则用例会红 —— 那是设计，不是缺陷。
    private static readonly Regex s_windowRegex = new(
        @"^(?!App\.xaml\.cs$)(?<name>[A-Za-z0-9_]*Window|[A-Za-z0-9_]+(?=\.xaml\.cs$))(?:\.[A-Za-z0-9_]+)?\.cs$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── 实例身份：这行属于哪个窗口实例、第几次进入激活回调 ──
    // 现场：一次捕获里"覆盖层就绪"打了 30 次。到底是"一个实例反复进 Activated"
    // 还是"同时存在多个实例"，只有这两个数能分清（SelectionOverlay 2026-09-30 起的打点）。
    //   "Activated #2（实例#1）进入背景创建" / "覆盖层就绪 实例#1 第1次进入 DPI=144 …"
    private static readonly Regex s_instanceRegex = new(
        @"实例#(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 两个来源写成一个判据：Activated 那条自带序号，"覆盖层就绪"那条用"第N次"表达同一个数。
    private static readonly Regex s_activationRegex = new(
        @"(?:Activated\s*#(\d+)|第(\d+)次)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            var file = cm.Groups[1].Value;
            entry.Caller = $"{file}:{cm.Groups[2].Value}";
            entry.CallerFile = file;
            entry.WindowName = WindowOf(file);
            entry.Message = entry.Message[..cm.Index].TrimEnd();
        }

        // 实例身份只在带打点的行上有值；没有就留 0（显示层据此整块收起）
        var im = s_instanceRegex.Match(entry.Message);
        if (im.Success && int.TryParse(im.Groups[1].Value, out var seq)) entry.InstanceSeq = seq;

        var am = s_activationRegex.Match(entry.Message);
        if (am.Success)
        {
            var digits = am.Groups[1].Success ? am.Groups[1].Value : am.Groups[2].Value;
            if (int.TryParse(digits, out var act)) entry.ActivationSeq = act;
        }

        return entry;
    }

    /// <summary>调用者文件 → 窗口名；无法归属到窗口时返回空串。</summary>
    public static string WindowOf(string callerFile) =>
        s_windowRegex.Match(callerFile) is { Success: true } wm ? wm.Groups["name"].Value : "";

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
