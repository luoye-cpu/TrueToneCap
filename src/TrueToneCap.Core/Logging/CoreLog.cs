// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Logging/CoreLog.cs
// Core 侧日志出口 —— 单一事实源。
//
// 为什么存在：Core 对 App 零项目引用，原先 142 处 System.Diagnostics.Debug.WriteLine
// 在 Release 下**架构上就到不了日志文件**（不是等级不够，是没有管道）。用户报障时
// 这些事实全部不存在。本文件给 Core 一个不依赖 App 的静态出口，由 App 在启动最早期
// 通过 Attach() 接上真正的落盘实现。
//
// ⚠ 现状（2026-09-21 复核，交接 §35.1 / §36.5）：
//     管道 ✅ 建好并挂上（App.xaml.cs:68 / AppServices.cs:47 → InitializeFileLog → AttachCoreSink）
//     档位 ✅ 有三级，且已接到设置页（设置 → 行为 → 日志详细度）
//     调用点 ✅ 迁移完成：Core 侧 CoreLog.\* 143 处，白名单外的裸 Debug.WriteLine 计数 0
//     ⇒ 白名单只有本文件与 App 侧 LogService.cs 两处（出口自身的控制台镜像 / sink 未挂载兜底），
//       由 unit 套件的 Test_NoRawDebugWritesOutsideTheWhitelistedSinks 钉住不回潮：白名单外长回来要红，
//       白名单内被删光（等于砍掉出口的控制台镜像）也要红。
//     关联 ID ✅ 2026-09-21 起截图链路真的在开作用域：两条截图根（选区 / 无感）各 BeginOp，
//       跨 UI 事件根的下游（选区动作回调、OCR 预览窗口的复制/翻译/保存）用 ContinueOp 续同一个 ID。
//       接线判据见 CorePipelineTests 的 OpId ⑥ 组 —— 这条之所以要有判据：机制 09-20 就建好了，
//       但当时全仓无人调用，"基础设施齐备"读起来就像"已经生效"，结果 #opId 段恒空了一整天。
//
// 档位与分类的定义也搬到这里，避免 App/Core 各维护一份枚举造成第二事实源。

namespace TrueToneCap.Core.Logging;

/// <summary>日志级别（严重程度）。新增 <see cref="Trace"/> 位于最低端，故其余成员顺延。</summary>
public enum LogLevel { Trace, Debug, Info, Warning, Error }

/// <summary>日志分类。</summary>
public enum LogCategory
{
    /// <summary>系统: 启动、服务初始化、设置、托盘。</summary>
    System,
    /// <summary>捕获: WGC、截图、HDR、选区。</summary>
    Capture,
    /// <summary>编码: 格式编码、色彩管线、文件输出。</summary>
    Encoding,
    /// <summary>界面: 窗口、主题、渲染、交互。</summary>
    UI,
    /// <summary>OCR: 文字识别、翻译、LLM。</summary>
    OCR,
    /// <summary>网络: API 调用、下载、连接。</summary>
    Network,
}

/// <summary>
/// 日志详细度档位。控制"写不写进文件"，与 <see cref="LogLevel"/>（单条严重程度）是两个轴。
/// </summary>
public enum LogDetail
{
    /// <summary>简（默认）: 用户操作、启动时的各种能力识别、一切降级事实。Info 及以上。</summary>
    Brief,
    /// <summary>诊断: 再加每拍的请求/实测快照、回退原因、容器声明、耗时。Debug 及以上。</summary>
    Diagnose,
    /// <summary>全: 再加逐帧、逐参数、逐子进程的细节。Trace 及以上。</summary>
    Trace,
}

/// <summary>
/// Core 的日志出口。
/// <para>⚠ 隐私地板：任何档位都不得写入图像字节内容、OCR 识别出的文本、API 密钥。
/// 本工具会截图并识字，这些正是用户最不该外泄的东西；全档只增加"做了什么"，不增加"看到了什么"。</para>
/// </summary>
public static class CoreLog
{
    /// <summary>日志落点委托。由 App 的 LogService 在启动最早期挂载。</summary>
    public delegate void LogSink(
        LogLevel level, LogCategory category, string tag, string message,
        string callerFile, int callerLine, string? opId);

    private static volatile LogSink? s_sink;
    private static readonly object s_earlyLock = new();

    /// <summary>
    /// sink 挂载前的早期缓冲。Core 里有在 <c>AppServices.Initialize()</c> 之前就会跑的成员
    /// （原生库解压器、NVENC 静态构造），不缓冲就会把"启动时的各种识别"整段丢掉——
    /// 而那正是简档默认要交付的内容。
    /// </summary>
    private static readonly Queue<(LogLevel Lvl, LogCategory Cat, string Tag, string Msg, string File, int Line, string? Op)> s_early = new();
    private const int MaxEarlyEntries = 4000;

    private static LogDetail s_detail = LogDetail.Brief;
    private static readonly Dictionary<LogCategory, LogDetail> s_floors = new();

    /// <summary>
    /// 每拍一个关联 ID。<see cref="AsyncLocal{T}"/> 会随 await 与 Task.Run 的 ExecutionContext
    /// 自动传播 ⇒ 一旦某处开了作用域，链路里所有 CoreLog 调用都自动带上它，不必改任何方法签名。
    /// <para>前提当然是有人开作用域。2026-09-21 起截图链路在开了：两条根（选区热键 / 无感截图）各
    /// <see cref="BeginOp"/>，而<b>一次"拍"横跨好几个彼此独立的 UI 事件根</b>（按下热键 → 选区回调 →
    /// 预览窗口的复制/翻译/保存），<c>AsyncLocal</c> 不会自动跨过它们 ⇒ 那些下游根用
    /// <see cref="ContinueOp"/> 显式续同一个 ID。接线判据在 <c>CorePipelineTests</c> 的 OpId ⑥ 组。
    /// 之前（2026-09-20）的状态是"机制齐备、全仓零调用方"，日志行前那段 <c>#opId </c> 恒空 ——
    /// 这段历史留着，是因为它就是本仓反复踩的那类错：把"基础设施建好了"读成"已经生效了"。</para>
    /// </summary>
    private static readonly AsyncLocal<string?> s_opId = new();

    /// <summary>当前关联 ID；未处于任何一次操作内时为 null。</summary>
    public static string? CurrentOpId => s_opId.Value;

    /// <summary>当前生效的全局档位（已挂载的 sink 与设置界面用它回显）。</summary>
    public static LogDetail Detail => s_detail;

    /// <summary>sink 是否已挂载（未挂载时消息进早期缓冲）。</summary>
    public static bool HasSink => s_sink is not null;

    /// <summary>挂载落点，并把早期缓冲一次性写出。重复调用以最后一次为准。</summary>
    public static void Attach(LogSink sink)
    {
        s_sink = sink ?? throw new ArgumentNullException(nameof(sink));
        (LogLevel, LogCategory, string, string, string, int, string?)[] pending;
        lock (s_earlyLock)
        {
            pending = s_early.ToArray();
            s_early.Clear();
        }
        foreach (var e in pending)
            Write(e.Item1, e.Item5, e.Item3, e.Item6, e.Item7, e.Item2, e.Item4);
    }

    /// <summary>设置全局档位。</summary>
    public static void SetDetail(LogDetail detail) => s_detail = detail;

    /// <summary>为单个分类设置档位地板；传 null 表示跟随全局。</summary>
    public static void SetCategoryFloor(LogCategory category, LogDetail? floor)
    {
        if (floor is null) s_floors.Remove(category);
        else s_floors[category] = floor.Value;
    }

    /// <summary>某分类实际生效的档位（分类地板可高于也可低于全局）。</summary>
    public static LogDetail EffectiveDetail(LogCategory category) =>
        s_floors.TryGetValue(category, out var f) ? f : s_detail;

    /// <summary>
    /// 该条要不要写。<see cref="LogLevel.Warning"/> 及以上**任何档位都写**——
    /// 否则用户可以把自己的故障日志关成空文件。
    /// </summary>
    public static bool IsEnabled(LogCategory category, LogLevel level) =>
        level >= LogLevel.Warning
        || RequiredSeverity(EffectiveDetail(category)) <= level;

    private static LogLevel RequiredSeverity(LogDetail detail) => detail switch
    {
        LogDetail.Brief => LogLevel.Info,
        LogDetail.Diagnose => LogLevel.Debug,
        _ => LogLevel.Trace,
    };

    /// <summary>
    /// 开启一次操作的关联范围。用 <c>using</c> 包住整条链路。
    /// <para>⚠ 一次"拍"往往不是一条连续的 await 链：热键按下 → 选区 → 用户点保存，中间隔着
    /// 好几次独立的 UI 事件回调，而 <see cref="AsyncLocal{T}"/> 只沿同一条执行上下文流动。
    /// 所以根上 <c>BeginOp()</c> 拿到的 <see cref="OpScope.Id"/> 要**存下来**，后续那些根用
    /// <see cref="ContinueOp"/> 续到同一个 ID 上，否则一次截图会散成四五个互不相干的 ID。</para>
    /// </summary>
    public static OpScope BeginOp() => new(null);

    /// <summary>
    /// 把当前这次操作**续到已有 ID** 上（跨 UI 事件根做关联用）。
    /// <para><paramref name="opId"/> 为 null / 空时退化成 <see cref="BeginOp"/>：
    /// 关联断了也比错连到上一拍强 —— 宁可开一个新 ID，也不把两次截图的日志混成一条时间线。</para>
    /// </summary>
    public static OpScope ContinueOp(string? opId) => new(string.IsNullOrWhiteSpace(opId) ? null : opId);

    /// <summary>显式指定关联 ID（用于把外部已有的一次操作并进同一条时间线）。</summary>
    public static void SetOpId(string? opId) => s_opId.Value = opId;

    /// <summary>关联范围：进入时定 ID（新建或续用），离开时还原外层 ID（嵌套调用不会串线）。</summary>
    public sealed class OpScope : IDisposable
    {
        private readonly string? _previous;
        /// <summary>本范围的 ID。</summary>
        public string Id { get; }

        internal OpScope(string? explicitId)
        {
            _previous = s_opId.Value;
            Id = explicitId ?? NewId();
            s_opId.Value = Id;
        }

        /// <summary>离开作用域时还原外层 ID。重复调用无副作用。</summary>
        public void Dispose() => s_opId.Value = _previous;

        private static string NewId()
        {
            // 6 位 base36，够一次会话内不撞；比 GUID 短得多，日志行不至于被它撑爆。
            Span<char> buf = stackalloc char[6];
            var v = (uint)(Environment.TickCount64 * 2654435761L ^ Interlocked.Increment(ref s_opCounter));
            for (int i = 5; i >= 0; i--)
            {
                buf[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(v % 36)];
                v /= 36;
            }
            return new string(buf);
        }

        private static int s_opCounter;
    }

    // ── 公开写入方法（带分类与调用者信息）──

    /// <summary>写一条 Trace。仅在"全"档下出现，用于逐帧/逐参数/逐子进程细节。</summary>
    public static void Trace(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!IsEnabled(cat, LogLevel.Trace)) return;
        Emit(LogLevel.Trace, cat, tag, msg, file, line);
    }

    /// <summary>懒构造的 Trace 重载：热路径（逐帧、逐像素）用它，避免为被丢弃的消息拼字符串。</summary>
    public static void Trace(string tag, Func<string> msgFactory, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!IsEnabled(cat, LogLevel.Trace)) return;
        Emit(LogLevel.Trace, cat, tag, msgFactory(), file, line);
    }

    /// <summary>写一条 Debug（诊断档）。</summary>
    public static void Debug(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!IsEnabled(cat, LogLevel.Debug)) return;
        Emit(LogLevel.Debug, cat, tag, msg, file, line);
    }

    /// <summary>写一条 Info（简档即可见）。</summary>
    public static void Info(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!IsEnabled(cat, LogLevel.Info)) return;
        Emit(LogLevel.Info, cat, tag, msg, file, line);
    }

    /// <summary>写一条 Warning。任何档位都写。</summary>
    public static void Warn(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Emit(LogLevel.Warning, cat, tag, msg, file, line);

    /// <summary>写一条 Error。任何档位都写。</summary>
    public static void Error(string tag, string msg, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Emit(LogLevel.Error, cat, tag, msg, file, line);

    /// <summary>写一条带异常的 Warning。任何档位都写。</summary>
    public static void Warn(string tag, string msg, Exception ex, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Emit(LogLevel.Warning, cat, tag, $"{msg}: {ex.Message}", file, line, ex.ToString());

    /// <summary>写一条带异常的 Error。任何档位都写。</summary>
    public static void Error(string tag, string msg, Exception ex, LogCategory cat = LogCategory.System,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => Emit(LogLevel.Error, cat, tag, $"{msg}: {ex.Message}", file, line, ex.ToString());

    private static void Emit(LogLevel level, LogCategory cat, string tag, string msg, string? file, int line, string? detail = null)
    {
        // 隐私地板在唯一的收口处生效（含早期缓冲）：不能让"挂载早晚"决定敏感到没刮
        msg = Scrub(msg);
        if (detail is not null) detail = Scrub(detail);
        var sink = s_sink;
        if (sink is null)
        {
            lock (s_earlyLock)
            {
                if (s_early.Count >= MaxEarlyEntries) s_early.Dequeue();
                s_early.Enqueue((level, cat, tag, detail is null ? msg : msg + "\n       └─ " + detail,
                    file ?? "", line, s_opId.Value));
            }
            return;
        }
        Write(level, file ?? "", tag, line, s_opId.Value, cat, detail is null ? msg : msg + "\n       └─ " + detail);
    }

    private static void Write(LogLevel level, string file, string tag, int line, string? opId, LogCategory cat, string msg)
    {
        try { s_sink?.Invoke(level, cat, tag, Scrub(msg), System.IO.Path.GetFileName(file), line, opId); }
        catch { /* 日志绝不能把宿主带崩 */ }
    }

    /// <summary>
    /// 隐私地板：把"值"从字符串里刮掉，只留下参数名。任何档位都生效。
    /// <para>为什么必须有机制而不是一句注释：Core 那 142 处 <c>System.Diagnostics.Debug.WriteLine</c>
    /// 往本出口迁的时候（2026-09-21 完成，交接 §35.1），那些行里就在写
    /// <c>ex.Message</c> —— <see cref="System.Net.Http.HttpRequestException"/> 的消息可能带上
    /// 请求 URI，而翻译请求的 URI 里就挂着 <c>q=&lt;OCR 出来的原文&gt;</c>。
    /// 截图文字可能含 token、身份证号、聊天记录；API 密钥同理。</para>
    /// <para>刻意保守：只刮"看起来是 query 参数值/凭据赋值"的片段，正文一律原样保留 ——
    /// 日志的价值就在于忠实复现当时发生了什么，宁可少刮也不要把诊断信息改坏。
    /// 因此这条地板**不承诺**"任何敏感内容都进不了日志"：调用方自己拼进 msg 的裸文本它认不出来。</para>
    /// </summary>
    public static string Scrub(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg ?? "";
        // key= / token= / secret= / appSecret= / q= / text= 之类的"值"整段换掉，只留参数名。
        // ⚠ 这里是"替换成 pre + 标记"，不是"在值后面追加标记" —— 首版写成了追加，
        //   于是密钥原样留在日志里，而判据当时没抓到（见 Test_LogPrivacyFloorScrubbing 的 ①）。
        var scrubbed = s_scrubRe.Replace(msg!, m => m.Groups[1].Value + REDACTED);
        // Base64 长串（图像字节 / 密钥的常见形态）：连续 24 位以上，且大小写与数字三类齐全才遮。
        // 三类都要是刻意收紧的：日志里合法存在 64 位小写的 SHA256（发布校验、工具链哈希），
        // 只按"长度 + 字符集"会把它一起刮掉 —— 那是把诊断信息改坏，比漏刮更糟。
        scrubbed = s_b64Re.Replace(scrubbed, m =>
            HasLower(m.Value) && HasUpper(m.Value) && HasDigit(m.Value) ? REDACTED : m.Value);
        return scrubbed;
    }

    static bool HasLower(string s) { foreach (char c in s) if (char.IsAsciiLetterLower(c)) return true; return false; }
    static bool HasUpper(string s) { foreach (char c in s) if (char.IsAsciiLetterUpper(c)) return true; return false; }
    static bool HasDigit(string s) { foreach (char c in s) if (char.IsAsciiDigit(c)) return true; return false; }

    private const string REDACTED = "‹redacted›";

    // (?<![A-Za-z0-9_]) 防止把 "...q=" 之类嵌在单词里的等号误当参数；值到 & 空白 " ' ) , 或行尾为止
    static readonly System.Text.RegularExpressions.Regex s_scrubRe = new(
        @"(?<pre>[?&]?(?:q|text|texts|prompt|query|input|key|apikey|api_key|token|secret|appsecret|app_secret|password|sig|signature)=)(?<val>[^&\s""'`),;\]}]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex s_b64Re = new(
        @"[A-Za-z0-9+/]{24,}={0,2}",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}
