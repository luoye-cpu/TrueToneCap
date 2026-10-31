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
    /// ⚠ 已知未收口的缺口（交接 §41.4）：动图中间帧走 <c>ManagedPngEncoder.EncodeFast</c>，
    /// 那条签名既不写 cICP 也不带 iCCP ⇒ 这里的决定只作用于像素，产物本身零声明。</summary>
    private ColorDecision? _colorDecision;

    /// <summary>取本次录制的色彩决定；理论上到不了 null（存帧前 RecordLoop 必然已赋值），
    /// 兜一份"无源屏信息"的决定，比在帧循环里再解析一次下拉好。
    /// <para>⚠ 有意不传第 5 个 format 参数（= null），两处调用点同理：录音里真正被本仓库编码的
    /// 只有<b>中间 PNG 帧</b>（<c>ManagedPngEncoder.EncodeFast</c>）与 ffmpeg 缺席时的
    /// <b>单帧回退 PNG</b>（<c>ManagedPngEncoder.Encode</c>），而 PNG 不在
    /// <c>ColorDecision.DefaultsToRec709</c> 的白名单里；动图容器（WebP/APNG/GIF）由 ffmpeg
    /// 另编，本决定根本不参与它的色彩声明。传 OutputFormat.PNG 结果与 null 逐位相同，
    /// 但会把"这次编的是 PNG"写进签名，而这里并没有这种格式上下文
    /// （<see cref="RecordingConfig.OutputFormat"/> 是 <c>AnimationFormat</c>，与
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

    public async Task StopAndEncodeAsync()
    {
        if (_state != RecordingState.Recording) return;
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
            (nint)_config.DisplayIndex != 0
                ? (nint)_config.DisplayIndex
                : DisplayEnumerator.GetMonitorUnderCursor());

        // 色彩决定整次录制算一次（源屏 = 被录那块屏），逐帧复用；帧循环里不再各自解析下拉。
        // ⚠ 格式有意传 null（不传第 5 个参数），理由见 Decision()：本决定服务的容器是 PNG 中间帧，
        //    而 PNG 不属"默认档 = Rec.709"那一族 ⇒ 与 2026-10-06 之前的行为逐位相同。
        _colorDecision = ColorDecision.Build(displayInfo, _config.ColorSpaceTag, false,
            AppServices.Settings.Current.AcmeDetected);

        if (displayInfo is null)
        {
            LogService.Error("AnimationRecorder", "找不到目标显示器");
            Report("找不到目标显示器");
            _state = RecordingState.Error;
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
            var tmpDir = Path.Combine(Path.GetTempPath(), $"ttc_anim_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);

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
                TrueToneCap.Core.Encoding.ManagedPngEncoder.EncodeFast(bakedPixels, w, h, framePath);
                    encoded++;
                    Report();
                }

                // ffmpeg 编码动图
                var (fmt, ext) = _config.OutputFormat switch
                {
                    AnimationFormat.AnimatedWebP => ("webp", ".webp"),
                    AnimationFormat.AnimatedPNG => ("apng", ".png"),
                    AnimationFormat.GIF => ("gif", ".gif"),
                    _ => ("webp", ".webp")
                };

                if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ext);

                var fps = _config.FrameRate;
                var quality = _config.Quality;
                string args = fmt switch
                {
                    "webp" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -c:v libwebp -quality {quality} -loop 0 \"{path}\"",
                    "apng" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -plays 0 -f apng \"{path}\"",
                    "gif" => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -vf \"split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" \"{path}\"",
                    _ => $"-framerate {fps} -i \"{tmpDir}/frame_%05d.png\" -c:v libwebp -quality {quality} -loop 0 \"{path}\""
                };

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    // 修复死锁: 异步读取 stdout 和 stderr，防止管道缓冲区满导致死锁
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120_000))
                    {
                        proc.Kill();
                        LogService.Warn("AnimationRecorder", "ffmpeg 超时 (120s)，已终止");
                    }
                    // 确保两个流都读完，释放管道缓冲区
                    Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
                    var stderr = stderrTask.Result;
                    if (!string.IsNullOrEmpty(stderr) && proc.ExitCode != 0)
                        LogService.Warn("AnimationRecorder", $"ffmpeg stderr: {stderr[..Math.Min(stderr.Length, 500)]}");
                }

                if (proc?.ExitCode != 0 || !File.Exists(path))
                {
                    // ffmpeg 不可用时回退：保存第一帧为 PNG（带 ICC 色彩管理）
                    var fallbackPath = Path.ChangeExtension(path, ".png");
                    var (px0, w0, h0) = _encodedFrames[0];
                    var (bakedPixels, _, _) = CapturePipelineService.PreparePixelsWithIcc(
                        px0, w0, h0, Decision());
                    TrueToneCap.Core.Encoding.ManagedPngEncoder.Encode(bakedPixels, w0, h0, fallbackPath, 8);
                    path = fallbackPath;
                    LogService.Warn("AnimationRecorder", "ffmpeg 不可用，回退为单帧 PNG");
                }
                else
                {
                    LogService.Info("AnimationRecorder", $"ffmpeg 编码成功: {path}");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }

            _config.OutputPath = path;
            _state = RecordingState.Completed;
            LogService.Info("AnimationRecorder", $"编码完成: {encoded} 帧 → {path} (丢弃 {_framesDropped} 帧)");
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
        _encodedFrames.Clear();
    }
}
