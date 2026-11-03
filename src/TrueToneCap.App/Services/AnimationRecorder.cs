// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/AnimationRecorder.cs
// 动图录制引擎 — 基于 WGC + ffmpeg (动图编码)
// P0 修复: 有界帧缓冲防 OOM + 共享 WgcCaptureService 单例 + PeriodicTimer 精确帧定时

using System.Collections.Concurrent;
using System.Threading.Channels;
using TrueToneCap.Core.Capture;

namespace TrueToneCap.App.Services;

public enum AnimationFormat { AnimatedWebP, AnimatedPNG, AnimatedAVIF, GIF }

public sealed class RecordingConfig
{
    public int FrameRate { get; set; } = 15;
    public int MaxDurationSeconds { get; set; } = 60;
    public float ChangeThreshold { get; set; } = 0.01f;
    public AnimationFormat OutputFormat { get; set; } = AnimationFormat.AnimatedWebP;
    public int Quality { get; set; } = 80;
    public string OutputPath { get; set; } = "";
    public int DisplayIndex { get; set; }

    // ═══ 2026-11-03（审查轮 R1-P2-4）：显式目标屏（HMONITOR）。
    // 录制入口已按"当时"光标屏预建了会话；RecordLoop 若再自行查一次光标，
    // 两读之间光标跨屏 ⇒ 预建白做、前若干 tick 全空。非 0 = 用它，不再自选。
    public nint TargetMonitor { get; set; }

    // ═══ 2026-11-02（D 流第 5 步）：ffmpeg 可执行文件（FfmpegLocator 解析后的绝对路径）。
    // 空 = 按 "ffmpeg" 裸名交给 PATH（保留旧语义）；locator 已缺席时录制入口根本不会走到这里。
    public string FfmpegPath { get; set; } = "";

    // ═══ ICC 色彩管理：自 2026-09-22 起不可关（默认映射进标准色域），所以这里不再有开关字段 ═══
    public string ColorSpaceTag { get; set; } = "System";
}

public enum RecordingState { Idle, Recording, Encoding, Completed, Cancelled, Error }

public sealed class RecordingProgressEventArgs : EventArgs
{
    public RecordingState State { get; init; }
    public int FramesCaptured { get; init; }
    public int FramesEncoded { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? OutputFile { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class AnimationRecorder : IDisposable
{
    private readonly RecordingConfig _config;
    private readonly WgcCaptureService _wgcService;

    // ═══ P0: 有界帧缓冲 — 限制最大帧数防止 OOM ═══
    // 4K@15fps 缓冲 2 秒 ≈ 30 帧 × 33MB ≈ 1GB 上限
    private const int MaxBufferedFrames = 30;
    private readonly Channel<(byte[] Pixels, int W, int H)> _frameChannel;
    private readonly List<(byte[] Pixels, int W, int H)> _encodedFrames = [];

    /// <summary>本次录制的<b>色彩决定</b>，在 RecordLoop 拿到目标显示器时构造一次，逐帧复用。
    /// 与静默截图同一份形状（Core 的 <c>ColorDecision</c>）⇒ 不许在帧循环里再各自解析色域。
    /// null 只可能出现在 RecordLoop 之前（那种情况下也不会有帧可存）。
    /// 2026-11-02（D 流第 5 步）：交接 §41.4 记录的"产物零声明"缺口已收口 ——
    /// 中间 PNG 帧带 cICP、回退 PNG 带 cICP+iCCP（16-bit）、WebP 产物嵌 ICCP、AVIF 下发 nclx
    /// color flags、APNG 补 cICP/iCCP（均从本决定导出，声明与像素同源）；GIF 无通道（日志声明）。</summary>
    private ColorDecision? _colorDecision;

    /// <summary>取本次录制的色彩决定；理论上到不了 null（存帧前 RecordLoop 必然已赋值），
    /// 兜一份"无源屏信息"的决定，比在帧循环里再解析一次下拉好。
    /// <para>⚠ 有意不传第 5 个 format 参数（= null），两处调用点同理：录音里真正被本仓库编码的
    /// 只有<b>中间 PNG 帧</b>（<c>ManagedPngEncoder.EncodeFast</c>）与 ffmpeg 缺席时的
    /// <b>单帧回退 PNG</b>（<c>ManagedPngEncoder.Encode</c>），而 PNG 不在
    /// <c>ColorDecision.DefaultsToRec709</c> 的白名单里；动图容器的声明走
    /// <see cref="TrueToneCap.Core.Encoding.AnimatedContainerColor"/> 与 ffmpeg color flags
    /// （码点/ICC 都从本决定的 TargetTag 导出），不借这第 5 个参数。
    /// 传 OutputFormat.PNG 结果与 null 逐位相同，但会把"这次编的是 PNG"写进签名，而这里并没有
    /// 这种格式上下文（<see cref="RecordingConfig.OutputFormat"/> 是 <c>AnimationFormat</c>，与
    /// <c>Core.Encoding.OutputFormat</c> 不是同一个枚举，不能硬套）。
    /// 不传 = 维持 2026-10-06 之前的行为，正是这条兜底该有的语义。</para></summary>
    private ColorDecision Decision() => _colorDecision
        ?? ColorDecision.Build(null, _config.ColorSpaceTag, false, AppServices.Settings.Current.AcmeDetected);

    private readonly CancellationTokenSource _cts = new();
    private volatile RecordingState _state = RecordingState.Idle;
    private int _framesCaptured;
    private int _framesDropped;
    private DateTime _startTime;
    private bool _disposed;

    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public RecordingState State => _state;

    /// <summary>获取丢帧计数（供 UI 显示录制质量）。</summary>
    public int FramesDropped => _framesDropped;

    /// <summary>创建录制器。必须传入共享的 WgcCaptureService 实例（避免重复创建 D3D11 设备）。</summary>
    public AnimationRecorder(RecordingConfig config, WgcCaptureService wgcService)
    {
        _config = config;
        _wgcService = wgcService;
        // ═══ P3 改进: DropOldest → Wait(1s) + 丢帧统计 ═══
        // DropOldest 会静默丢弃旧帧导致时间戳不连续、录制时长变短。
        // 改为 Wait(1s)：编码端稍慢时短暂背压而非丢帧；若 1s 仍写不进去（编码端严重慢），
        // 才放弃该帧并计数，让 UI 可见丢帧率。
        _frameChannel = Channel.CreateBounded<(byte[] Pixels, int W, int H)>(
            new BoundedChannelOptions(MaxBufferedFrames)
            {
                FullMode = BoundedChannelFullMode.Wait, // 背压而非静默丢帧
                SingleReader = true,
                SingleWriter = true
            });
    }

    public void StartRecording()
    {
        if (_state != RecordingState.Idle) return;
        _state = RecordingState.Recording;
        _startTime = DateTime.UtcNow;
        _framesCaptured = 0;
        LogService.Info("AnimationRecorder", $"录制启动: 帧率={_config.FrameRate} 最大时长={_config.MaxDurationSeconds}s 格式={_config.OutputFormat}");
        _framesDropped = 0;
        Task.Run(() => RecordLoop(_cts.Token));
    }

    private int _encodeStarted;   // 0=未开始 1=已开始（审查轮 R1-P1-2：到点自动收尾 × 手动停止可同时过 _state 检查 ⇒ 双跑 EncodeFrames）

    public async Task StopAndEncodeAsync()
    {
        if (_state != RecordingState.Recording) return;
        // 状态检查与置位非原子（volatile enum 挡不住 check-then-act），这里用 CAS 把
        // "只允许一个 EncodeFrames"做成全程序不变量：输家直接退出，绝不二次编码。
        if (Interlocked.CompareExchange(ref _encodeStarted, 1, 0) != 0) return;
        LogService.Info("AnimationRecorder", "录制停止，开始编码...");
        _state = RecordingState.Encoding;
        _cts.Cancel();
        _frameChannel.Writer.TryComplete();
        await Task.Run(() => EncodeFrames());
    }

    public void Cancel()
    {
        LogService.Info("AnimationRecorder", "录制已取消");
        _cts.Cancel();
        _frameChannel.Writer.TryComplete();
        _state = RecordingState.Cancelled;
    }

    private async Task RecordLoop(CancellationToken ct)
    {
        byte[]? last = null;

        var displayInfo = DisplayEnumerator.FindDisplayByMonitor(
            _config.TargetMonitor != 0 ? _config.TargetMonitor
            : (nint)_config.DisplayIndex != 0
                ? (nint)_config.DisplayIndex
                : DisplayEnumerator.GetMonitorUnderCursor());

        // 色彩决定整次录制算一次（源屏 = 被录那块屏），逐帧复用；帧循环里不再各自解析下拉。
        // ⚠ 格式有意传 null（不传第 5 个参数），理由见 Decision()：本决定服务的容器是 PNG 中间帧，
        //    而 PNG 不属"默认档 = Rec.709"那一族 ⇒ 与 2026-10-06 之前的行为逐位相同。
        _colorDecision = ColorDecision.Build(displayInfo, _config.ColorSpaceTag, false,
            AppServices.Settings.Current.AcmeDetected);

        if (displayInfo is null)
        {
            // ⚠ 先置 Error 再 Report（审查轮 R1-P2-7）：反过来事件带着 Recording 状态出门，
            // UI 的 switch 没有这个分支 ⇒ 永远停在"🔴 录制中"、无错误提示。
            _state = RecordingState.Error;
            LogService.Error("AnimationRecorder", "找不到目标显示器");
            Report("找不到目标显示器");
            return;
        }

        // ═══ P1: PeriodicTimer 精确帧定时（替代 Thread.Sleep）═══
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _config.FrameRate));

        // 未变化、被丢弃的帧复用这块缓冲（所有权只在"入队"时移交），
        // 静态画面下不再每 tick 新分配一整帧（4K 33MB）。
        byte[]? scratch = null;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if ((DateTime.UtcNow - _startTime).TotalSeconds >= _config.MaxDurationSeconds)
                    break;

                try
                {
                    // 无锁读取：直接从池化会话取最新帧，不与主截图竞争 s_captureLock
                    var result = _wgcService.TryGetLatestFrame(displayInfo.MonitorHandle, scratch);

                    if (result?.SdrPixels is null) continue;

                    var pixels = result.SdrPixels;
                    if (HasChange(pixels, last, _config.ChangeThreshold))
                    {
                        // ═══ P3: Wait(1s) 背压策略 — 编码端稍慢时短暂等待而非丢帧 ═══
                        // 若 1s 内缓冲仍满（编码端严重慢于捕获率），放弃该帧并计数
                        bool written = false;
                        try
                        {
                            written = await _frameChannel.Writer.WaitToWriteAsync(ct).AsTask()
                                .WaitAsync(TimeSpan.FromSeconds(1));
                        }
                        catch (TimeoutException)
                        {
                            // 缓冲持续满 1s → 主动丢帧
                            written = false;
                        }
                        if (written)
                        {
                            _frameChannel.Writer.TryWrite((pixels, result.Width, result.Height));
                        }
                        else
                        {
                            // 缓冲持续满 → 主动丢帧并计数（UI 可见）
                            Interlocked.Increment(ref _framesDropped);
                        }
                        Interlocked.Increment(ref _framesCaptured);
                        last = pixels;
                        // 纪律：scratch 只能持有"没有任何人还引用"的那块。
                        // 走到这一支说明 pixels 已经被 last（变化帧还可能已被编码端）引用，
                        // 再当 scratch 用就等于让 last 与自己比 ⇒ HasChange 恒假、后续全丢。
                        scratch = null;
                    }
                    else
                    {
                        scratch = pixels;     // 未变化 ⇒ 没人引用它，下个 tick 直接拷进来
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn("AnimationRecorder", $"WGC 捕获异常: {ex.Message}");
                    continue;
                }

                Report();
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }

        _frameChannel.Writer.TryComplete();
        LogService.Info("AnimationRecorder", $"录制循环结束: 捕获 {_framesCaptured} 帧, 丢帧 {_framesDropped}");
        Report();

        // ═══ 2026-11-02（D 流第 6 步）：到点自动收尾 ═══
        // 旧代码 break 后无人调 EncodeFrames：State 卡在 Recording、Channel 已 Complete，
        // 用户看到"录了但永远不出片"，必须再按一次停止键。最大时长的语义是"到点自动出片"。
        // 用户手动停止走 ct.IsCancellationRequested = true 这条路，不会双跑。
        if (!ct.IsCancellationRequested)
            await StopAndEncodeAsync();
    }

    private static bool HasChange(byte[]? cur, byte[]? prev, float threshold)
    {
        if (prev == null || cur == null || cur.Length != prev.Length) return true;
        int diff = 0, step = Math.Max(1, cur.Length / 5000);
        for (int i = 0; i < cur.Length; i += step)
            if (Math.Abs(cur[i] - prev[i]) > 8) diff++;
        return (float)diff / (cur.Length / step) > threshold;
    }

    private void EncodeFrames()
    {
        try
        {
            var path = string.IsNullOrEmpty(_config.OutputPath)
                ? Path.Combine(Path.GetTempPath(),
                    $"TrueToneCap_{DateTime.Now:yyyyMMdd_HHmmss}.webp")
                : _config.OutputPath;

            int delay = Math.Max(1, 100 / _config.FrameRate);

            // 从 Channel 中读取所有缓冲帧
            while (_frameChannel.Reader.TryRead(out var frame))
                _encodedFrames.Add(frame);

            if (_encodedFrames.Count == 0)
            {
                _state = RecordingState.Error;
                LogService.Warn("AnimationRecorder", "没有捕获到任何帧");
                Report("没有捕获到任何帧");
                return;
            }

            // ═══ 动图编码：通过 ffmpeg 进程调用 ═══
            int encoded = 0;
            string? degradation = null;   // 非空 = 产物已偏离"正常动图"，完成事件要带给 UI
            var tmpDir = Path.Combine(Path.GetTempPath(), $"ttc_anim_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);

            // ═══ 色彩声明源（2026-11-02 D 流第 5 步）═══
            // 像素已由 PreparePixelsWithIcc 烘焙到决定的目标色域 ⇒ 声明也必须从同一次决定导出。
            // BuildSdrCicp 对 AdobeRGB 返回 null（H.273 无该 primaries 码点）—— 码点不下发、不冒充；
            // ICC 通道照常（IccStore 有 Adobe 的标准 profile）。
            var targetTag = Decision().TargetTag;
            byte[]? cicp = TrueToneCap.Core.Encoding.FormatHelper.BuildSdrCicp(targetTag);
            byte[]? icc = StandardIccFor(targetTag);
            // AVIF 的 nclx primaries 名（2026-11-03 本机 ffmpeg git-2026-10-27 实测：avif muxer 会把
            // stream 色彩属性持久化进 colr(nclx)；但它不写 prof(ICC) box、也不搬 PNG iCCP
            // ⇒ AVIF 的声明通道只有 nclx 这一条）。
            string? avifPrimaries = cicp is null ? null : cicp[0] switch
            {
                1 => "bt709",
                9 => "bt2020",
                12 => "smpte432",    // Display P3 (D65)
                _ => null,           // 认不出的码点不冒充：flags 缺席 ⇒ nclx 缺席，日志留痕
            };
            // transfer 恒 sRGB：录制源是 SDR 8-bit 池，decision 恒 HdrOutput=false。
            // WebP 容器没有 CICP 通道（传了也只是编解码器自嗨）⇒ flags 只给 AVIF；
            // GIF/APNG 的声明走编码后的 chunk 手术（AnimatedContainerColor）。
            string colorFlags = avifPrimaries is null ? "" :
                $" -color_primaries {avifPrimaries} -color_trc iec61966-2-1 -colorspace rgb -color_range pc";

            try
            {
                // 写入临时 PNG 帧序列（每个帧先经过 ICC 色彩管理）
                for (int i = 0; i < _encodedFrames.Count; i++)
                {
                    var (px, w, h) = _encodedFrames[i];
                    var framePath = Path.Combine(tmpDir, $"frame_{i:D5}.png");

                    // ═══ ICC 色彩管理：烘焙到目标色域 ═══
                    var (bakedPixels, _, _) = CapturePipelineService.PreparePixelsWithIcc(
                        px, w, h, Decision());
                    // 中间帧只喂给 ffmpeg、随后整目录删除 ⇒ 用 EncodeFast（Filter=None + Fastest）。
                    // 4K 单帧实测 448~536ms → 55~63ms（2026-10-29 临时探针），30 帧从 ~15s 降到 ~2s。
                    // 与 JXL/AVIF/WebP 的中转文件同一处置办法；ffmpeg 读到的是像素，与压缩率无关。
                    // 2026-11-02：带上 cICP —— 中间文件不再"裸奔"（交接 §41.4 缺口的一半）。
                    TrueToneCap.Core.Encoding.ManagedPngEncoder.EncodeFast(bakedPixels, w, h, framePath, cicp: cicp);
                    encoded++;
                    Report();
                }

                // ffmpeg 编码动图。⚠ 每个枚举值都有显式分支：2026-11-02 之前 AnimatedAVIF 掉进
                // `_ => ("webp", ".webp")` 静默编出 WebP（台账 §4.4-①）。兜底现在是显式抛错 ——
                // 将来加枚举值漏了分支，会得到一条看得懂的失败，而不是一个错的文件。
                var (fmt, ext) = _config.OutputFormat switch
                {
                    AnimationFormat.AnimatedWebP => ("webp", ".webp"),
                    AnimationFormat.AnimatedPNG => ("apng", ".png"),
                    AnimationFormat.AnimatedAVIF => ("avif", ".avif"),
                    AnimationFormat.GIF => ("gif", ".gif"),
                    _ => throw new InvalidOperationException($"未知的动图格式: {_config.OutputFormat}")
                };

                if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ext);

                var fps = _config.FrameRate;
                var quality = _config.Quality;
                int avifCrf = Math.Clamp((int)Math.Round((100 - quality) * 63.0 / 100), 0, 63);
                string args = fmt switch
                {
                    "webp" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -c:v libwebp -quality {quality} -loop 0 \"{path}\"",
                    "apng" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -plays 0 -f apng \"{path}\"",
                    // 2026-11-03 实机：libaom 默认 cpu-used=1 对 4K 动图完全不可用（2 帧编了 95s），
                    // cpu-used 4 也仍超 120s（24 帧 4K）⇒ 6 是动图档的实际速度位（实测 3 帧 4K 4.3s，
                    // ~1.4s/帧；屏幕内容对 cpu-used 不如实拍敏感）。⚠ tiles 参数名是 -tile-columns，
                    // 不是 -tiles（后者会让 ffmpeg 直接退出）。速度/超时实测记录见台账 §11.4。
                    "avif" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -c:v libaom-av1 -still-picture 0 -cpu-used 6 -row-mt 1 -tile-columns 1 -crf {avifCrf} -b:v 0{colorFlags} \"{path}\"",
                    "gif" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -vf \"split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" \"{path}\"",
                    _ => throw new InvalidOperationException($"未知的动图 ffmpeg 参数分支: {fmt}")
                };

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    // 2026-11-02（D 流第 3/5 步）：FileName 来自 FfmpegLocator 的解析结果；空则退回裸名。
                    FileName = string.IsNullOrWhiteSpace(_config.FfmpegPath) ? "ffmpeg" : _config.FfmpegPath,
                    Arguments = $"-y {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                System.Diagnostics.Process? proc = null;
                bool ffmpegMissing = false;
                try { proc = System.Diagnostics.Process.Start(psi); }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // 起不来（多数=locator 解析结果已失效/裸名不在 PATH）：错误必须可执行，回退要 Toast 明说
                    ffmpegMissing = ex.NativeErrorCode == 2;   // ERROR_FILE_NOT_FOUND
                    LogService.Error("AnimationRecorder",
                        $"ffmpeg 无法启动 ({psi.FileName}): {ex.Message}（请在设置里指定路径，或把 ffmpeg 加入 PATH）");
                }
                int exitCode = -1;
                if (proc is not null)
                {
                    try
                    {
                        // 修复死锁: 异步读取 stdout 和 stderr，防止管道缓冲区满导致死锁。
                        // 超时按帧数给：2026-11-03 实机 4K AVIF 软编 ~秒级/帧，固定 120s 挡不住
                        // 长录制（30s@15fps=450 帧），只能靠"每帧 ~1.5s"的线性预算，下限仍 120s。
                        int timeoutMs = Math.Max(120_000, 1_500 * _encodedFrames.Count);
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(timeoutMs))
                        {
                            proc.Kill();
                            LogService.Warn("AnimationRecorder", $"ffmpeg 超时 ({timeoutMs / 1000}s)，已终止");
                        }
                        // 确保两个流都读完，释放管道缓冲区
                        Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
                        // ⚠ 先取退出码再 Dispose：2026-11-03 实机抓到 Dispose 后读 ExitCode 抛
                        // "No process is associated with this object"（超时 Kill 后未等退出同理可能炸）。
                        exitCode = proc.ExitCode;
                        var stderr = stderrTask.Result;
                        if (!string.IsNullOrEmpty(stderr) && exitCode != 0)
                            LogService.Warn("AnimationRecorder", $"ffmpeg stderr: {stderr[..Math.Min(stderr.Length, 500)]}");
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn("AnimationRecorder", $"ffmpeg 进程收尾失败: {ex.Message}");
                    }
                    finally { proc.Dispose(); }
                }

                if (exitCode != 0 || !File.Exists(path))
                {
                    // ffmpeg 失败/缺席时回退：首帧存 PNG（带 ICC 色彩管理）。
                    // 2026-11-02 用户拍板：回退走 16-bit。事实说清：录制源是 SDR 8-bit 池，
                    // 16-bit 只是升位深不是增精度（Encode 写 sBIT=8 照实声明实际精度），文件翻倍是知情选择。
                    var fallbackPath = Path.ChangeExtension(path, ".png");
                    var (px0, w0, h0) = _encodedFrames[0];
                    var (bakedPixels, _, _) = CapturePipelineService.PreparePixelsWithIcc(
                        px0, w0, h0, Decision());
                    TrueToneCap.Core.Encoding.ManagedPngEncoder.Encode(bakedPixels, w0, h0, fallbackPath, 16,
                        iccProfile: icc, cicp: cicp);
                    path = fallbackPath;
                    degradation = ffmpegMissing
                        ? "未找到 ffmpeg：请在设置里指定路径，或把 ffmpeg 加入 PATH —— 已退化为单帧 PNG（SDR）"
                        : "ffmpeg 编码失败 —— 已退化为单帧 PNG（SDR）";
                    LogService.Warn("AnimationRecorder", degradation);
                }
                else
                {
                    LogService.Info("AnimationRecorder", $"ffmpeg 编码成功: {path}");
                }

                // ═══ 声明后置（2026-11-02 D 流第 5 步）：ffmpeg 不搬声明（2026-11-03 实测），
                //     WebP/APNG 的声明由我们自己往编好的容器里插；插不动就留日志、产物保持无声明。
                //     按【最终产物】的容器类型分派 —— 回退成单帧 PNG 后再印 AVIF nclx 就是撒谎
                //     （2026-11-03 实机：AVIF 超时回退，声明日志仍写 nclx）。═══
                (bool declOk, string declReason) = (fmt, Path.GetExtension(path).ToLowerInvariant()) switch
                {
                    ("apng", _) => TrueToneCap.Core.Encoding.AnimatedContainerColor.TryEmbedPngDeclaration(path, cicp, icc),
                    // ⚠ (_, ".png") 必须排在 gif/webp/avif 之前：任何格式回退成单帧 PNG 后，
                    // 再印"GIF 无声明通道"/"AVIF nclx"都是撒谎（审查轮 R2-P2-1 补的 GIF 这一漏）。
                    // APNG 走不到这里（上一臂先命中；回退时产物已带声明，TryEmbed 幂等跳过）。
                    (_, ".png") => (true, "回退单帧 PNG：16-bit 容器 + sBIT(实际 8-bit) + cICP/iCCP 声明"),
                    ("gif", _) => (true, "GIF 容器无色彩声明通道（格式限制）；像素已烘焙到目标色域"),
                    ("webp", _) when icc is { Length: > 0 } =>
                        TrueToneCap.Core.Encoding.AnimatedContainerColor.TryEmbedWebpIcc(path, icc),
                    ("webp", _) => (true, "WebP 无可用标准 ICC，保持无声明"),
                    ("avif", _) => (true, cicp is null
                        ? "AVIF：目标 AdobeRGB 无 primaries 码点，nclx 不下发（与单帧 AVIF 同口径）"
                        : $"AVIF：colr(nclx) primaries={cicp[0]} transfer=13(sRGB) matrix=0 full-range（ffmpeg color flags 写入）"),
                    _ => (true, "未知容器，未嵌声明"),
                };
                LogService.Info("AnimationRecorder",
                    (declOk ? "动图色彩声明: " : "动图色彩声明【失败】(产物保持无声明): ") + declReason);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }

            _config.OutputPath = path;
            _state = RecordingState.Completed;
            LogService.Info("AnimationRecorder", $"编码完成: {encoded} 帧 → {path} (丢弃 {_framesDropped} 帧)");
            // State 已是 Completed 再报，UI 才能把 degradation 当"完成但降级"处理而不是当错误
            if (degradation is not null)
                Report(degradation);
        }
        catch (Exception ex)
        {
            _state = RecordingState.Error;
            LogService.Error("AnimationRecorder", $"编码异常: {ex.Message}", ex);
            Report(ex.Message);
        }
        finally
        {
            // 释放帧缓冲内存
            _encodedFrames.Clear();
            LogService.Info("AnimationRecorder", "编码资源已释放");
            Report();
        }
    }

    /// <summary>目标色域 → 标准 ICC profile（2026-11-02 D 流第 5 步）。
    /// <para>录制没有显示侧 ICC 可用（TryGetLatestFrame 的 SDR 池结果不填 IccProfile），
    /// 声明走 IccStore 运行时生成的标准 profile —— 与"系统默认 ICC 的屏拿标准 ICC 兜底"
    /// 是同一条口径（台账 §3.4）。AdobeRGB 也有：ICC 通道认得它，不像 cICP 那样受 H.273 限制。</para></summary>
    private static byte[] StandardIccFor(string tag) => tag switch
    {
        "DisplayP3" => TrueToneCap.Core.Encoding.IccStore.DisplayP3,
        "BT2020" => TrueToneCap.Core.Encoding.IccStore.BT2020,
        "AdobeRGB" => TrueToneCap.Core.Encoding.IccStore.AdobeRGB1998,
        _ => TrueToneCap.Core.Encoding.IccStore.SRGB,
    };

    private void Report(string? err = null) =>
        ProgressChanged?.Invoke(this, new RecordingProgressEventArgs
        {
            State = _state,
            FramesCaptured = _framesCaptured,
            FramesEncoded = _encodedFrames.Count,
            ElapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds,
            OutputFile = _config.OutputPath,
            ErrorMessage = err
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _frameChannel.Writer.TryComplete();
        _cts.Dispose();
        // ⚠ 不碰 _encodedFrames（审查轮 R1-P2-8）：UI 线程的 CleanupRecorder 可能在引擎线程
        // 的 EncodeFrames finally 还没跑完时调进来，List 双线程 Clear 是数据竞争。
        // 该列表只在 EncodeFrames 内被填充，其 finally 负责清空；Cancel 路径下未编码的帧
        // 随本对象一起进 GC，上限即 Channel 容量（30 帧），不值得为它冒险。
    }
}
