// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/MultiOcrService.cs
// 多引擎 OCR 路由器 — 用户手动选择单一引擎；选中的是 ONNX 时，拿不到可信结果会用 Windows OCR 兜底
// 切换引擎自动切换对应的可选语言列表

using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Services;

/// <summary>OCR 引擎管理器 — 手动选择主引擎，ONNX 不可信结果时 Windows OCR 兜底。</summary>
public static class MultiOcrService
{
    private static readonly List<IOcrEngine> _engines = [];
    private static readonly object s_initLock = new();
    private static bool _initialized;
    private static string _modelDir = "";

    /// <summary>当前选中的引擎类型（null = 未选择）。</summary>
    public static OcrEngineType? SelectedEngineType { get; set; }

    /// <summary>所有已注册的引擎。</summary>
    public static IReadOnlyList<IOcrEngine> Engines => _engines;

    /// <summary>获取当前选中的引擎实例（CPU 引擎按需构建）。</summary>
    public static IOcrEngine? SelectedEngine
    {
        get
        {
            if (SelectedEngineType is null) return null;
            var type = SelectedEngineType.Value;
            var existing = _engines.FirstOrDefault(e => e.Info.EngineType == type);
            if (existing is not null) return existing;
            // 2026-10-30：CPU 会话改为按需构建（启动时只建 DirectML + 注册 Windows OCR）。
            // 用户显式选了 CPU、或 DirectML 不可用而默认落到 CPU 时，这里补建一次。
            return type == OcrEngineType.OnnxCpu ? EnsureCpuEngine() : null;
        }
    }

    /// <summary>获取当前选中引擎支持的语言列表。</summary>
    public static OcrLanguage[] GetSupportedLanguages()
    {
        if (SelectedEngineType is null) return [];
        return OcrLanguages.GetLanguagesForEngine(SelectedEngineType.Value);
    }

    /// <summary>获取当前选中引擎的默认语言 ID。</summary>
    public static string GetDefaultLanguage()
    {
        if (SelectedEngineType is null) return "ch";
        return OcrLanguages.GetDefaultLanguage(SelectedEngineType.Value);
    }

    /// <summary>
    /// 初始化所有可用引擎（不自动选择，需用户手动设置 SelectedEngineType）。
    /// <para>⚠ 2026-09-21 加锁：本方法原先【无锁】且把 <c>_initialized = true</c> 写在注册之前，
    /// 而启动期有两个并发调用点（App.xaml.cs 的 Task.Run 与 MainWindow 的 Task.Run），
    /// 用户点 OCR 时还有第三个（CaptureAndOcrFromPixelsAsync）。两个线程能同时通过
    /// <c>if (_initialized) return;</c> ⇒ 同一批引擎被注册两遍（<c>_engines</c> 是普通 List，
    /// 且并发 Add 本身不安全）。整个方法体现在在锁内跑，第二个及以后的调用直接返回。</para>
    /// </summary>
    public static void Initialize(string? modelDir = null)
    {
        lock (s_initLock)
        {
            if (_initialized) return;
            _modelDir = modelDir ?? "";

            CoreLog.Info("OCR", $"初始化引擎，模型目录: {_modelDir}", LogCategory.OCR);

            // 1️⃣ ONNX PP-OCRv6 DirectML (GPU, FP16)
            try
            {
                var gpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, modelDir);
                if (gpuEngine.Info.IsAvailable)
                {
                    _engines.Add(gpuEngine);
                    CoreLog.Info("OCR", "DirectML GPU 引擎就绪", LogCategory.OCR);
                }
                else
                {
                    CoreLog.Warn("OCR", "DirectML GPU 引擎不可用", LogCategory.OCR);
                }
            }
            catch (Exception ex) { CoreLog.Warn("OCR", "DirectML 初始化异常", ex, LogCategory.OCR); }

            // 2️⃣ ONNX PP-OCRv6 CPU (FP16 模型, FP32 计算)
            // 2026-10-30：默认【不】在启动期建。CPU 会话只有在两种场景下才会被用：
            //   ① 用户显式选 OnnxCpu；② DirectML 不可用，默认落到 CPU。
            //   GPU 可用时它整场无人调用，却要常驻一份 ~180ms 加载的会话与内存。
            // 需要回到旧行为（启动即建）时设 TTC_OCR_EAGER_CPU=1。
            bool gpuReady = _engines.Any(e => e.Info.EngineType == OcrEngineType.OnnxGpu);
            bool eagerCpu = Environment.GetEnvironmentVariable("TTC_OCR_EAGER_CPU") == "1";
            if (!gpuReady || eagerCpu)
            {
                if (!eagerCpu)
                    CoreLog.Info("OCR", "DirectML 不可用 ⇒ 启动期建 CPU 引擎", LogCategory.OCR);
                BuildCpuEngineLocked();
            }
            else
            {
                CoreLog.Info("OCR", "CPU 引擎按需构建（DirectML 可用）", LogCategory.OCR);
            }

            // 3️⃣ Windows OCR (系统 OCR)
            _engines.Add(new WindowsOcrEngine());
            CoreLog.Info("OCR", "Windows OCR 引擎已注册", LogCategory.OCR);

            CoreLog.Info("OCR", $"共 {_engines.Count} 个引擎: {string.Join(", ", _engines.Select(e => e.Info.Name))}", LogCategory.OCR);

            // 注册顺序即降级优先级：ONNX GPU → ONNX CPU → Windows OCR。
            // ⚠ 若 DirectML 不可用，OnnxOcrEngine 会把 _provider 降级成 Cpu 并以此身份注册，
            // 于是 OnnxGpu 这一档【根本不在表里】——此时 _engines[0] 是 OnnxCpu。
            if (SelectedEngineType is null && _engines.Count > 0)
            {
                SelectedEngineType = _engines[0].Info.EngineType;
                CoreLog.Info("OCR", $"默认选中引擎: {_engines[0].Info.Name}", LogCategory.OCR);
            }

            // 必须等注册全部完成再置位，否则并发调用会看到"已初始化但引擎表为空"。
            _initialized = true;
        }
    }

    /// <summary>启动期建 CPU 引擎（调用方须持有 <see cref="s_initLock"/>）。仅 DirectML 不可用时调用。</summary>
    private static void BuildCpuEngineLocked()
    {
        try
        {
            var cpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, _modelDir);
            if (cpuEngine.Info.IsAvailable)
            {
                _engines.Add(cpuEngine);
                CoreLog.Info("OCR", "CPU 引擎就绪", LogCategory.OCR);
            }
            else
            {
                CoreLog.Warn("OCR", "CPU 引擎不可用", LogCategory.OCR);
                cpuEngine.Dispose();
            }
        }
        catch (Exception ex) { CoreLog.Warn("OCR", "CPU 初始化异常", ex, LogCategory.OCR); }
    }

    /// <summary>
    /// 按需构建 CPU 引擎（用户选中 OnnxCpu，或 DirectML 不可用导致默认落到 CPU）。
    /// <para>失败不缓存：下一次选择/调用会再试一次 —— 与"瞬时失败不写能力缓存"同一口径。</para>
    /// </summary>
    private static IOcrEngine? EnsureCpuEngine()
    {
        lock (s_initLock)
        {
            var existing = _engines.FirstOrDefault(e => e.Info.EngineType == OcrEngineType.OnnxCpu);
            if (existing is not null) return existing;
            // Initialize 持锁时不会被走到（RecognizeCoreAsync 先调 Initialize 再取引擎）；
            // 这里兜的是"初始化尚未完成就有人来取"的并发形态。
            if (!_initialized) return null;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu, _modelDir);
                if (!cpuEngine.Info.IsAvailable)
                {
                    CoreLog.Warn("OCR", "CPU 引擎不可用", LogCategory.OCR);
                    cpuEngine.Dispose();
                    return null;
                }
                // 保持声明顺序：GPU → CPU → Windows OCR（降级优先级与日志口径不变）
                int winIdx = _engines.FindIndex(e => e.Info.EngineType == OcrEngineType.WindowsOcr);
                if (winIdx >= 0) _engines.Insert(winIdx, cpuEngine);
                else _engines.Add(cpuEngine);
                CoreLog.Info("OCR", $"CPU 引擎就绪（按需，{sw.ElapsedMilliseconds}ms）", LogCategory.OCR);
                return cpuEngine;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("OCR", "CPU 初始化异常", ex, LogCategory.OCR);
                return null;
            }
        }
    }

    /// <summary>
    /// 主引擎总超时（秒）。防止 DirectML/ONNX 卡死时 UI 永久等待。
    /// <para>内层阈值（OnnxOcrEngine 的 40 秒看门狗）必须【严格小于】本值，
    /// 否则被放弃的任务会一直压着推理门，下一次请求只能在门上排队。改这里要同步核对那里。</para>
    /// </summary>
    internal static TimeSpan OcrTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 兜底引擎的【独立】时间预算（秒）。
    /// <para>⚠ 2026-09-21 修复：旧实现里主引擎与兜底共用同一个 <c>timeoutCts</c> —— 主引擎到点后
    /// 那份 token 已经取消，兜底一进 <c>RunWithTimeout</c> 就看见 <c>Task.Delay</c> 先完成，
    /// 当场返回"OCR 引擎超时"，Windows OCR 一毫秒都没跑到。也就是说"超时之后由 Windows OCR
    /// 兜底"这句一直存在的注释，从来没有兑现过。</para>
    /// <para>证据分两半，别说满：机理（共享一份已过期的 token）是读旧代码 + 常驻断言
    /// <c>Test_OcrTimeoutStillReachesFallbackEngine</c> 确证的；而"同一张图 ONNX 753 ms、
    /// Windows OCR 308 ms 就能出全文"这组数字来自一次性探针，探针是把外部 <c>ct</c> 当截止用的，
    /// 走的是同一条短路、但不是内部 45 s 预算那条。两者不是同一次测量，别混着引用。</para>
    /// </summary>
    internal static TimeSpan FallbackTimeout { get; set; } = TimeSpan.FromSeconds(20);

    // ═══ 2026-09-13 P0 修复: 全局串行化信号量 ═══
    // MainWindow 的选区 OCR/翻译可从不同 UI 动作并发触发 (连点/不同热键)，
    // 即使单个 OnnxOcrEngine 内部已串行，多个引擎 (ONNX GPU + Windows OCR fallback)
    // 仍可能并发。用全局信号量统一串行化所有引擎调用，杜绝原生层并发崩溃闪退。
    private static readonly SemaphoreSlim s_ocrGate = new(1, 1);

    /// <summary>引擎在分给它的预算内没有返回（含"卡死不抛异常"与"引擎自己响应取消"两种形态）。</summary>
    internal sealed class OcrEngineTimeoutException : Exception
    {
        public OcrEngineTimeoutException(string engineName, TimeSpan budget)
            : base($"{engineName} 在 {budget.TotalSeconds:F0} 秒内未返回") { }
    }

    /// <summary>
    /// 使用当前选中的引擎执行 OCR 识别。
    /// ═══ 2026-08-25 优化: ONNX 结果不可信时自动 fallback 到 Windows OCR ═══
    /// 背景: PP-OCRv6 统一字典不含韩文学符，韩文整页只能吐出汉字/拉丁区的乱码（实测 0/15 字符）。
    /// Windows OCR (系统托管) 原生支持韩文等 Windows 语言包。
    /// 触发条件: 选中 ONNX 引擎 + 结果为空/全空白/【字符域与请求语言不符】+ Windows OCR 已注册。
    /// ═══ 2026-09-04 加固: 总超时看门狗 — ONNX 推理若死锁 (不抛异常)，
    ///    await 会永久挂起导致 UI 卡死；包一层 Task.WhenAny 强制超时返回。
    /// ═══ 2026-09-13 加固: 全局串行化 — 快速连点选区 OCR/翻译只排队执行，
    ///    不再并发 → 杜绝 OnnxRuntime session 并发 Run 的原生崩溃闪退。
    /// ═══ 2026-09-21: 超时改为"先兜底再报障"；兜底判据加字符域；取消不再抛给调用方。
    /// <para>本方法不抛 <see cref="OperationCanceledException"/>：用户取消也返回
    /// <c>Error="OCR 识别已取消"</c>。原先 <c>s_ocrGate.WaitAsync(ct)</c> 写在 try 之外，
    /// 取消时异常直接逃逸给调用方，App 侧只能走兜底的 <c>catch (Exception)</c>，
    /// 状态栏于是显示一句英文原文 "❌ A task was canceled."。</para>
    /// </summary>
    public static async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_initialized) Initialize();

        var acquired = false;
        try
        {
            await s_ocrGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            return await RecognizeCoreAsync(bgra, w, h, lang, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new OcrResult { Error = "OCR 识别已取消" };
        }
        finally
        {
            if (acquired) s_ocrGate.Release();
        }
    }

    private static async Task<OcrResult> RecognizeCoreAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
    {
        if (!_initialized) Initialize();

        var engine = SelectedEngine ?? (_engines.Count > 0 ? _engines[0] : null);
        if (engine is null)
            return new OcrResult { Error = "未选择 OCR 引擎" };

        // ═══ 2026-09-15 修复：选中的引擎不可用时应回退，而不是整体失败 ═══
        // 典型场景：默认设置是 OnnxGpu（DirectML），但若 DML 不可用，OnnxOcrEngine 会以 Cpu
        // 身份注册（见 Initialize），OnnxGpu 这一档不在表里 ⇒ SelectedEngine 为 null。
        // 原实现直接返回"未选择 OCR 引擎"：OCR 完全不能用，状态栏卡在"OCR 引擎探测中..."，
        // 用户既不知道原因，也不知道切到 Windows OCR 就能恢复正常。
        if (SelectedEngine is null)
            CoreLog.Warn("OCR",
                $"选中引擎 {SelectedEngineType} 未注册（该档不可用）→ 改用 {engine.Info.Name}", LogCategory.OCR);

        var isOnnx = engine.Info.EngineType is OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu;

        OcrResult result;
        string? primaryFailure = null;   // 主引擎为什么没给出可用结果（超时/异常），null = 结果本身的问题
        try
        {
            // 主选 Windows OCR 时也要过一遍语言映射：设置里存着 ONNX 短码（"ch"/"ko"）时，
            // 原样传给 OcrService 会撞上 TryCreateFromLanguage 失败，
            // 报出一句"请安装语言包"——而真实原因只是标签形态不对。实测 lang="ch" 即命中。
            var engineLang = isOnnx ? lang : LangForWindows(lang);
            CoreLog.Debug("OCR", $"使用引擎: {engine.Info.Name}, 语言: {engineLang ?? "default"}", LogCategory.OCR);
            result = await RunWithTimeout(engine, bgra, w, h, engineLang, OcrTimeout, ct);
        }
        catch (OcrEngineTimeoutException) when (!ct.IsCancellationRequested)
        {
            primaryFailure = $"OCR 识别超时（>{OcrTimeout.TotalSeconds:F0} 秒），可能为 DirectML 卡死";
            result = new OcrResult { Error = primaryFailure };
            CoreLog.Warn("OCR", $"主引擎 {engine.Info.Name} 超时 → 尝试 Windows OCR 兜底", LogCategory.OCR);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            primaryFailure = $"OCR 识别失败: {ex.Message}";
            result = new OcrResult { Error = primaryFailure };
            CoreLog.Warn("OCR", $"引擎 {engine.Info.Name} 异常", ex, LogCategory.OCR);
        }

        if (primaryFailure is null && IsResultTrustworthy(result, lang))
        {
            CoreLog.Info("OCR", $"成功! 引擎={engine.Info.Name}, 文本长度={result.Text.Length}", LogCategory.OCR);
            return result;
        }

        // ═══ fallback: ONNX 结果不可信（空/超时/异常）→ Windows OCR ═══
        if (isOnnx)
        {
            var (fallback, note) = await TryWindowsFallbackAsync(bgra, w, h, lang, ct);
            if (fallback is not null)
            {
                fallback.FallbackFrom = engine.Info.EngineType.ToString();
                CoreLog.Info("OCR",
                    $"Windows OCR 兜底成功: {fallback.Text.Length} 字（原引擎 {engine.Info.Name}" +
                    (primaryFailure is null ? "" : " 已超时/异常") + "）", LogCategory.OCR);
                return fallback;
            }

            // 兜底也没救回来 → 把两件事一起说清：主引擎怎么了、兜底为什么也不行
            result.Error = primaryFailure is null
                ? $"OCR 未获得可信结果，已尝试 Windows OCR 兜底但未出结果（{note}）"
                : $"{primaryFailure}；已尝试 Windows OCR 兜底但未出结果（{note}）";
        }

        CoreLog.Warn("OCR", $"引擎 {engine.Info.Name}: 无可用结果 (error={result.Error ?? "null"})", LogCategory.OCR);
        return result;
    }

    /// <summary>
    /// 把图片再交给 Windows OCR 一次。<paramref name="lang"/> 用 ONNX 的短标签形态传入，
    /// 这里负责转成 Windows 认的形态。返回 (结果或 null, 失败原因说明)。
    /// <para>⚠ 关键：这里用 <see cref="FallbackTimeout"/> 那份【新】预算，绝不复用主引擎的
    /// 过期 token —— 那正是"超时之后兜底必然落空"的成因（见 FallbackTimeout 注释）。</para>
    /// </summary>
    private static async Task<(OcrResult? result, string note)> TryWindowsFallbackAsync(
        byte[] bgra, int w, int h, string? lang, CancellationToken ct)
    {
        var winOcr = FindEngine(OcrEngineType.WindowsOcr);
        if (winOcr is null)
            return (null, "系统未注册 Windows OCR 引擎");

        var winLang = LangForWindows(lang);
        try
        {
            var fallback = await RunWithTimeout(winOcr, bgra, w, h, winLang, FallbackTimeout, ct);
            if (!string.IsNullOrWhiteSpace(fallback.Text))
                return (fallback, "");

            // Windows OCR 的"没结果"多半是语言包缺失（TryCreateFromLanguage 返回 null），
            // 那句错文本身就带语言标签，直接当兜底失败原因回传，不另编一套话。
            return (null, string.IsNullOrWhiteSpace(fallback.Error) ? "未识别出文字" : fallback.Error);
        }
        catch (OcrEngineTimeoutException ex)
        {
            // 用异常自带的预算，别在这里重抄 FallbackTimeout —— 一旦传进来的预算与它不同，
            // 这句话就报出一个从未存在过的时限（变异实验里实测到：预算 0 秒，说明写成"超过 5 秒"）。
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 用户取消不是"兜底失败"，交回调用方按取消处理
        }
        catch (Exception ex)
        {
            return (null, $"Windows OCR 异常: {ex.Message}");
        }
    }

    /// <summary>带预算的引擎调用：预算用尽时抛 <see cref="OcrEngineTimeoutException"/>。</summary>
    private static async Task<OcrResult> RunWithTimeout(IOcrEngine engine, byte[] bgra, int w, int h,
        string? lang, TimeSpan budget, CancellationToken ct)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);
        var budgetCt = budgetCts.Token;

        var task = engine.RecognizeAsync(bgra, w, h, lang, budgetCt);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, budgetCt)).ConfigureAwait(false);
        if (completed != task)
        {
            // 被放弃的 task 仍持有引擎内部的推理串行门。引擎侧的内层看门狗必须严格短于
            // 本方法的 budget，否则它会压着门直到自己走完，期间后续请求只能在门外排队。
            // ⚠ 先分清"是谁到点的"：外部 ct 取消时把这报成"引擎超时 N 秒"会把用户指向
            // 一个根本不存在的卡死（实测踩过：400ms 取消 ⇒ 报"45 秒内未返回"）。
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new OcrEngineTimeoutException(engine.Info.Name, budget);
        }
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budgetCt.IsCancellationRequested)
        {
            // 引擎自己响应了预算取消 —— 与"卡死到点"是同一种用户可见事实
            throw new OcrEngineTimeoutException(engine.Info.Name, budget);
        }
    }

    /// <summary>根据引擎类型查找引擎实例。</summary>
    public static IOcrEngine? FindEngine(OcrEngineType type)
    {
        return _engines.FirstOrDefault(e => e.Info.EngineType == type);
    }

    /// <summary>
    /// 仅供测试：换上给定的一组引擎，并把两份超时预算缩短到毫秒级。
    /// <para>为什么要有这个口子：「超时之后 Windows OCR 兜底到底有没有真的跑起来」这条判据，
    /// 在只有真模型 + 真语言包的环境下无法确定性复现 —— 要等 45 秒、还要一台 DirectML 会卡死的
    /// 机器。这个缺陷活了整整一个月，靠一次性探针才抓出来（见 <see cref="FallbackTimeout"/>
    /// 注释里的实测数字）。有了可注入的引擎与预算，它就能变成常驻断言。</para>
    /// </summary>
    internal static void UseEnginesForTest(IOcrEngine primary, IOcrEngine? windowsFallback,
        TimeSpan primaryBudget, TimeSpan fallbackBudget)
    {
        lock (s_initLock)
        {
            _engines.Clear();
            _engines.Add(primary);
            if (windowsFallback is not null) _engines.Add(windowsFallback);
            OcrTimeout = primaryBudget;
            FallbackTimeout = fallbackBudget;
            SelectedEngineType = primary.Info.EngineType;
            _initialized = true;
        }
    }

    /// <summary>
    /// 结果是否可信（决定要不要走兜底）。
    /// <para>① 原有判据：无 error 且文本非空白。<br/>
    /// ② 新增字符域判据：请求的是韩文，而结果里一个韩文字符都没有 —— 那一定是把韩文
    /// 错认成了汉字/拉丁（PP-OCRv6 统一字典不含韩文）。实测韩文整页返回 "E三\n是"，
    /// 在旧判据下算"成功"，用户看到的就是没有报错的乱码，且兜底永远不触发。
    /// 只对韩文开这一道：韩文音节/字母区与中日汉字完全不重叠，误判风险接近零；
    /// 日文/中文不能这么判（"文字認識"这类正常日文结果里就没有假名，会误杀）。</para>
    /// </summary>
    internal static bool IsResultTrustworthy(OcrResult result, string? lang)
    {
        if (!string.IsNullOrEmpty(result.Error)) return false;
        if (string.IsNullOrWhiteSpace(result.Text)) return false;
        if (MapLangToBcp47(lang) == "ko-KR" && !ContainsHangul(result.Text)) return false;
        return true;
    }

    /// <summary>
    /// 文本里是否出现谚文。用码点区间而不是字面字符，便于核对：
    /// U+1100–U+11FF（Jamo 初/中/终声）、U+3130–U+318F（兼容字母）、
    /// U+A960–U+A97F（Jamo Ext-A）、U+AC00–U+D7FF（音节 + Jamo Ext-B；
    /// U+D7A4–U+D7AF 未分配，收进来不影响判定）。
    /// </summary>
    internal static bool ContainsHangul(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u1100' and <= '\u11FF'
                     or >= '\u3130' and <= '\u318F'
                     or >= '\uA960' and <= '\uA97F'
                     or >= '\uAC00' and <= '\uD7FF')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Windows OCR 能直接接受的语言形态：BCP-47（含 '-'），以及 <c>OcrService</c> 自己拦截的
    /// "zh-en"（中英双引擎合并，<see cref="OcrService"/> 在建引擎之前就分派掉，不会走
    /// <c>TryCreateFromLanguage</c>）。其余 ONNX 短标签先过 <see cref="MapLangToBcp47"/>。
    /// <para>主选 Windows 与兜底两条路共用本函数，避免"一处映射一处不映射"的两套规则。</para>
    /// </summary>
    internal static string? LangForWindows(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        var s = lang.Trim();
        if (s.Equals("zh-en", StringComparison.OrdinalIgnoreCase) || s.Contains('-')) return s;
        return MapLangToBcp47(s);
    }

    /// <summary>
    /// ═══ R-2 修复: ONNX 短语言标签 → Windows OCR BCP-47 标签映射 ═══
    /// ONNX (PP-OCR) 用 "ch"/"en"/"korean" 等短标签; Windows OCR 的
    /// OcrEngine.TryCreateFromLanguage 需要 BCP-47 ("zh-Hans-CN" 等)。
    /// 无法映射时返回 null → OcrService 走默认语言逻辑 (zh-Hans-CN → 用户配置)。
    /// <para>注意 "zh-en"：它不是合法 BCP-47，但 <see cref="OcrService.RunOcrAsync"/> 在建引擎
    /// 之前就把这个值分派给中英双引擎合并分支，因此<b>原样返回是安全的</b>；
    /// 需要保留这个形态的调用方请走 <see cref="LangForWindows"/>。</para>
    /// </summary>
    internal static string? MapLangToBcp47(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return lang.Trim().ToLowerInvariant() switch
        {
            // 中文
            "zh-en" or "ch" or "zh" or "chinese" or "chs" => "zh-Hans-CN",
            "cht" or "zh-tw" or "zh-hant" => "zh-Hant-TW",
            // 英文
            "en" or "english" => "en-US",
            // 日文（ONNX Id 为 "ja"，PP-OCR 传统标签为 "jp" —— 两者都要认）
            "ja" or "jpn" or "jp" or "japan" or "japanese" => "ja-JP",
            // 韩文
            // 🔴 修复 2026-09-15：原表只认 "korean"/"kor"，而 OcrLanguages.OnnxLanguages 里
            // 韩文的 Id 是 "ko" —— 于是 "ko" 落到 _ => null，fallback 到 Windows OCR 时传入 null
            // 被 OcrService 默认成 zh-Hans-CN（拿中文引擎识别韩文），这是「韩文恒 0%」的直接成因。
            "ko" or "kor" or "korean" => "ko-KR",
            // 法/德/西/俄/葡/意
            "french" or "fr" or "fra" => "fr-FR",
            "german" or "de" or "deu" => "de-DE",
            "spanish" or "es" or "spa" => "es-ES",
            "russian" or "ru" or "rus" => "ru-RU",
            "portuguese" or "pt" => "pt-BR",
            "italian" or "it" or "ita" => "it-IT",
            // 其余 PP-OCRv6 支持语言 —— 与 OcrLanguages.OnnxLanguages 的 Id 一一对齐，
            // 此前 nl/pl/th/vi/ar 全部漏映射（同样会静默 fallback 成中文）。
            "nl" or "dut" or "nld" => "nl-NL",
            "pl" or "pol" => "pl-PL",
            "th" or "tha" => "th-TH",
            "vi" or "vie" => "vi-VN",
            "ar" or "ara" => "ar-SA",
            // 已是 BCP-47 格式 (含 '-') → 原样返回
            var s when s.Contains('-') => lang,
            // 未知短标签 → null 走默认
            _ => null,
        };
    }

    /// <summary>检查指定引擎类型是否可用。</summary>
    public static bool IsEngineAvailable(OcrEngineType type)
    {
        return _engines.Any(e => e.Info.EngineType == type && e.Info.IsAvailable);
    }
}

/// <summary>Windows OCR 引擎。</summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    public OcrEngineInfo Info => new(
        "Windows OCR (系统)",
        OcrEngineMode.Cpu,
        true,
        OcrEngineType.WindowsOcr,
        Version: "System");

    public async Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
        string? lang = null, CancellationToken ct = default)
        => await OcrService.ExtractTextAsync(bgra, w, h, lang, ct);
}
