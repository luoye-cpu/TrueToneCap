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
            // 2026-10-30：CPU 会话改为按需构建（那时启动只剩 DirectML + Windows OCR 两档）。
            // 用户显式选了 CPU、或 DirectML 不可用而默认落到 CPU 时，这里补建一次。
            // 2026-10-31：DirectML 会话改成同一形状（默认按需，TTC_OCR_EAGER_GPU=1 回旧行为），
            // 所以 GPU 这一档也要能在【引擎选择的同一处】补建 —— 降级顺序仍由登记顺序决定，
            // 这里只补建、不改 SelectedEngineType（"只回退不改选择"是 10-30 那批定下的口径）。
            if (type == OcrEngineType.OnnxGpu) return EnsureGpuEngine();
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
            // ⚠ 空 modelDir 不能原样留着：懒建（GPU / CPU）都是在【首次识字】时才拿 _modelDir
            // 去 new 会话，而 OnnxOcrEngine 的构造只做 `modelDir ?? ResolveModelDir()` ——
            // 空串不是 null，绕不过那层兜底 ⇒ 一旦"没传目录的那次 Initialize 先到"，
            // 整个 ONNX 路径就永久建不起来（正是 2026-09-15 那条"谁先到谁定"的缺陷形状）。
            _modelDir = string.IsNullOrWhiteSpace(modelDir)
                ? OnnxOcrEngine.ResolveModelDir()
                : modelDir;

            CoreLog.Info("OCR", $"初始化引擎，模型目录: {_modelDir}", LogCategory.OCR);

            // 1️⃣ ONNX PP-OCRv6 DirectML (GPU, FP16)
            // 2026-10-31：默认【不】在启动期建 —— 与 CPU 那一档同族、同一形状（见 2️⃣）。
            //   真机读数：一次启动里 DirectML 这条腿 = 探测(~164ms 空档) + 检测模型 + 识别模型
            //   + 18708 字符字典 ≈ 1.1s 后台占用，且这份会话整场可能在第一次识字前根本没人用。
            //   需要回到旧行为（启动即建）时设 TTC_OCR_EAGER_GPU=1。
            bool eagerGpu = Environment.GetEnvironmentVariable("TTC_OCR_EAGER_GPU") == "1";
            if (eagerGpu) BuildGpuEngineLocked();
            else CoreLog.Info("OCR", "DirectML GPU 会话按需构建（首次识字才建）", LogCategory.OCR);

            // 2️⃣ ONNX PP-OCRv6 CPU (FP16 模型, FP32 计算)
            // 2026-10-30：默认【不】在启动期建。CPU 会话只有在两种场景下才会被用：
            //   ① 用户显式选 OnnxCpu；② DirectML 不可用，默认落到 CPU。
            //   GPU 可用时它整场无人调用，却要常驻一份 ~180ms 加载的会话与内存。
            // 需要回到旧行为（启动即建）时设 TTC_OCR_EAGER_CPU=1。
            // ⚠ 2026-10-31 这条判据的【含义】随 GPU 懒建变了：GPU 还没建时"表里没有 OnnxGpu"
            //   不再等于"DirectML 不可用"（只说明还没建），所以把 !eagerGpu 也算进 gpuReady ——
            //   否则每次启动都会因为"表里暂时没 GPU"而把 CPU 会话又预建回去，白拿的省没了。
            //   真正确定不可用时（eager 建过、自降成 OnnxCpu 之外没有 OnnxGpu）仍走启动期建 CPU 那条路。
            bool gpuReady = _engines.Any(e => e.Info.EngineType == OcrEngineType.OnnxGpu) || !eagerGpu;
            bool eagerCpu = Environment.GetEnvironmentVariable("TTC_OCR_EAGER_CPU") == "1";
            if (!gpuReady || eagerCpu)
            {
                if (!eagerCpu)
                    CoreLog.Info("OCR", "DirectML 不可用 ⇒ 启动期建 CPU 引擎", LogCategory.OCR);
                BuildCpuEngineLocked();
            }
            else
            {
                CoreLog.Info("OCR", eagerGpu
                    ? "CPU 引擎按需构建（DirectML 可用）"
                    : "CPU 引擎按需构建（GPU 会话也按需，DirectML 可用性尚未定）", LogCategory.OCR);
            }

            // 3️⃣ Windows OCR (系统 OCR)
            _engines.Add(new WindowsOcrEngine());
            CoreLog.Info("OCR", "Windows OCR 引擎已注册", LogCategory.OCR);

            CoreLog.Info("OCR", $"共 {_engines.Count} 个引擎: {string.Join(", ", _engines.Select(e => e.Info.Name))}", LogCategory.OCR);

            // 注册顺序即降级优先级：ONNX GPU → ONNX CPU → Windows OCR。
            // ⚠ 若 DirectML 不可用，OnnxOcrEngine 会把 _provider 降级成 Cpu 并以此身份注册，
            // 于是 OnnxGpu 这一档【根本不在表里】——此时 _engines[0] 是 OnnxCpu。
            // ⚠ 2026-10-31：GPU 懒建时表里同样没有 OnnxGpu，但那【不是】"该档不可用"的证据。
            //   这里不能再拿 _engines[0] 当默认（此刻它是 Windows OCR ⇒ 等于把默认识别引擎
            //   从 ONNX 换成系统 OCR，是行为改变而不是省时间）。默认仍给 OnnxGpu：
            //   首次识字时 EnsureGpuEngine 真去建，DirectML 不可用则由引擎自降成 OnnxCpu 并登记，
            //   RecognizeCoreAsync 那句"选中引擎未注册（该档不可用）→ 改用 …"的既有回退会接住
            //   （2026-09-15 那条修复本来就是为这一格写的）。
            if (SelectedEngineType is null && _engines.Count > 0)
            {
                SelectedEngineType = !eagerGpu
                    ? OcrEngineType.OnnxGpu
                    : _engines[0].Info.EngineType;
                string defaultNote = !eagerGpu
                    ? "（会话按需构建，DirectML 不可用时首次识字自降 CPU）"
                    : $" = {_engines[0].Info.Name}";
                CoreLog.Info("OCR", $"默认选中引擎: {SelectedEngineType}{defaultNote}", LogCategory.OCR);
            }

            // 必须等注册全部完成再置位，否则并发调用会看到"已初始化但引擎表为空"。
            // ⚠ 懒建不改变这句的语义：置位时表里已经登记了"启动期该登记的"全部内容，
            //   GPU/CPU 是【刻意】留给引擎选择处的补建，不是"没建起来"。
            _initialized = true;
        }
    }

    /// <summary>启动期建 DirectML(GPU) 引擎（调用方须持有 <see cref="s_initLock"/>）。仅 TTC_OCR_EAGER_GPU=1 时调用。</summary>
    private static void BuildGpuEngineLocked()
    {
        try
        {
            var gpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, _modelDir);
            if (gpuEngine.Info.IsAvailable)
            {
                RegisterOnnxEngineLocked(gpuEngine);
                // 记住"这次 DirectML 请求已经有了落点"：引擎在 DML 不可用时会自降成 OnnxCpu 身份注册，
                // 此时表里【没有】OnnxGpu 那一档 —— 没有这条痕迹，GPU 侧的按需补建会把它当成
                // "还没建过"再 new 一份会话（同一进程内重复付一次模型加载）。
                // ⚠ 这不是能力缓存，也【不是负缓存】：它只说"建过的这一枚落在哪一档"，
                //   真失败（IsAvailable=false / 抛异常）时这里一行都不留，下次照样重试。
                _gpuBuiltDegraded = gpuEngine.Info.EngineType != OcrEngineType.OnnxGpu;
                CoreLog.Info("OCR", _gpuBuiltDegraded
                    ? "DirectML GPU 引擎就绪（已自降为 CPU 会话）"
                    : "DirectML GPU 引擎就绪", LogCategory.OCR);
            }
            else
            {
                CoreLog.Warn("OCR", "DirectML GPU 引擎不可用", LogCategory.OCR);
                gpuEngine.Dispose();
            }
        }
        catch (Exception ex) { CoreLog.Warn("OCR", "DirectML 初始化异常", ex, LogCategory.OCR); }
    }

    /// <summary>
    /// DirectML 请求是否已经建过一次并【自降成 CPU 身份】登记在表里（调用方须持 <see cref="s_initLock"/> 读写）。
    /// <para>用途只有一个：避免同一进程内为同一个 GPU 请求重复 new 会话。它不等于"GPU 永久不可用"的
    /// 结论缓存 —— 真失败时这个标记不会被置上（见 <see cref="BuildGpuEngineLocked"/> 的 ⚠）。</para>
    /// </summary>
    private static bool _gpuBuiltDegraded;

    /// <summary>
    /// 把一枚 ONNX 会话登记进表，且排在所有非 ONNX 引擎之前（调用方须持 <see cref="s_initLock"/>）。
    /// <para>为什么不能直接 <c>Add</c>：GPU 懒建后补建的时机不固定，用户可能先点 CPU
    /// （EnsureCpuEngine 把 CPU 插在了 Windows OCR 之前），此时补建的 GPU 必须落在 CPU 之前，
    /// 否则"注册顺序即降级优先级"这句就不成立了（RecognizeCoreAsync 的兜底取的是 _engines[0]）。</para>
    /// </summary>
    private static void RegisterOnnxEngineLocked(IOcrEngine engine)
    {
        int insertAt = _engines.FindIndex(e =>
            e.Info.EngineType is not (OcrEngineType.OnnxGpu or OcrEngineType.OnnxCpu));
        _engines.Insert(insertAt < 0 ? _engines.Count : insertAt, engine);
    }

    /// <summary>
    /// 按需构建 DirectML(GPU) 会话（首次识字、或用户显式选了 GPU 时才走到这里）。
    /// <para>失败不缓存：下一次选择/调用会再试一次 —— 与 CPU 那一档、以及"瞬时失败不写能力缓存"同一口径。</para>
    /// <para>⚠ 唯一【不算失败】的留痕是 <see cref="_gpuBuiltDegraded"/>：DirectML 不可用时引擎会自降成
    /// OnnxCpu 身份并【成功】登记在表里，那是一次建起来了、只是不在 GPU 档 —— 若不认它，
    /// 每次识字都要重 new 一份会话（重付一次模型加载）。判据用这个显式标记，
    /// 不用"表里是否有任一 ONNX 会话"（用户可能先点了 CPU，那时 GPU 还一次都没试过）。</para>
    /// </summary>
    private static IOcrEngine? EnsureGpuEngine()
    {
        lock (s_initLock)
        {
            var existing = _engines.FirstOrDefault(e => e.Info.EngineType == OcrEngineType.OnnxGpu);
            if (existing is not null) return existing;
            // Initialize 持锁时不会被走到（RecognizeCoreAsync 先调 Initialize 再取引擎）；
            // 这里兜的是"初始化尚未完成就有人来取"的并发形态。
            if (!_initialized) return null;
            // 已经建过、且那枚会话以 OnnxCpu 身份登记在表里 ⇒ 不要再 new 一份。
            // 这里【不能】用"表里有 OnnxCpu"当判据：用户可能先点了 CPU（EnsureCpuEngine 建的），
            // 那时 GPU 还一次都没试过，拿它当"GPU 已落定"会把 GPU 永久挡在门外。
            if (_gpuBuiltDegraded) return null;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var gpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML, _modelDir);
                if (!gpuEngine.Info.IsAvailable)
                {
                    CoreLog.Warn("OCR", "DirectML GPU 引擎不可用", LogCategory.OCR);
                    gpuEngine.Dispose();
                    return null;
                }
                // 保持声明顺序：GPU → CPU → Windows OCR（降级优先级与日志口径不变）。
                // DirectML 不可用时引擎会以 OnnxCpu 身份自降，同样落在 Windows OCR 之前。
                RegisterOnnxEngineLocked(gpuEngine);
                bool degraded = gpuEngine.Info.EngineType != OcrEngineType.OnnxGpu;
                if (degraded) _gpuBuiltDegraded = true;
                CoreLog.Info("OCR",
                    $"DirectML GPU 引擎就绪（按需{(degraded ? "，已自降为 CPU 会话" : "")}，{sw.ElapsedMilliseconds}ms）",
                    LogCategory.OCR);
                // 自降那一枚返回 null：让 RecognizeCoreAsync 走既有的
                // "选中引擎未注册（该档不可用）→ 改用 _engines[0]" 那条回退（会留一句 Warn，
                // 而不是静默地把 CPU 会话当成 GPU 报给用户）。
                return degraded ? null : gpuEngine;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("OCR", "DirectML 初始化异常", ex, LogCategory.OCR);
                return null;
            }
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

        // ⚠ 取引擎这一步必须在【线程池】上做，不能顺着调用线程跑：
        // GPU/CPU 会话懒建后就可能发生在这里（EnsureGpuEngine 一次 ≈ 0.7–1.1s 的模型加载），
        // 而本方法常常是从 UI 线程的 async void 事件里进来的 —— 上游那句
        // `await s_ocrGate.WaitAsync(ct).ConfigureAwait(false)` 在门是空闲的时候【同步完成】，
        // 于是整段 RecognizeCoreAsync 一路内联跑在 UI 线程上，首次识字会把整个窗口冻住。
        // （2026-10-30 那批做 CPU 懒建时同样有这一格，只是 CPU 只要 175ms，没被拿出来当问题。）
        // 这里只把"取实例"这一步搬走，不改它的判据，也不动降级顺序。
        Func<IOcrEngine?> pickSelected = () => SelectedEngine;
        var engine = await Task.Run(pickSelected, ct).ConfigureAwait(false)
            ?? (_engines.Count > 0 ? _engines[0] : null);
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
