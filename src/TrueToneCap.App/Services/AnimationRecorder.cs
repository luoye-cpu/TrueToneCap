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

    // ═══ ICC 色彩管理（动图录制时每个帧先经过 ICC 烘焙再编码）═══
    public bool IccBakeEnabled { get; set; }
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

    private readonly CancellationTokenSource _cts = new();
    private volatile RecordingState _state = RecordingState.Idle;
    private int _framesCaptured;
    private int _framesDropped;
    private DateTime _startTime;
    private bool _disposed;

    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public RecordingState State => _state;

    /// <summary>创建录制器。必须传入共享的 WgcCaptureService 实例（避免重复创建 D3D11 设备）。</summary>
    public AnimationRecorder(RecordingConfig config, WgcCaptureService wgcService)
    {
        _config = config;
        _wgcService = wgcService;
        _frameChannel = Channel.CreateBounded<(byte[], int, int)>(
            new BoundedChannelOptions(MaxBufferedFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // 缓冲满时丢弃最旧帧
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

        if (displayInfo is null)
        {
            LogService.Error("AnimationRecorder", "找不到目标显示器");
            Report("找不到目标显示器");
            _state = RecordingState.Error;
            return;
        }

        // ═══ P1: PeriodicTimer 精确帧定时（替代 Thread.Sleep）═══
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _config.FrameRate));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if ((DateTime.UtcNow - _startTime).TotalSeconds >= _config.MaxDurationSeconds)
                    break;

                try
                {
                    // 无锁读取：直接从池化会话取最新帧，不与主截图竞争 s_captureLock
                    var result = _wgcService.TryGetLatestFrame(displayInfo.MonitorHandle);

                    if (result?.SdrPixels is null) continue;

                    var pixels = result.SdrPixels;
                    if (HasChange(pixels, last, _config.ChangeThreshold))
                    {
                        // 有界写入：缓冲满时 DropOldest 自动丢弃最旧帧
                        if (!_frameChannel.Writer.TryWrite((pixels, result.Width, result.Height)))
                        {
                            Interlocked.Increment(ref _framesDropped);
                        }
                        Interlocked.Increment(ref _framesCaptured);
                        last = pixels;
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
        LogService.Info("AnimationRecorder", $"录制循环结束: 捕获 {_framesCaptured} 帧");
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
                var (bakedPixels, _) = CapturePipelineService.PreparePixelsWithIcc(
                    px, w, h, _config.IccBakeEnabled, _config.ColorSpaceTag);
                TrueToneCap.Core.Encoding.ManagedPngEncoder.Encode(bakedPixels, w, h, framePath, 8);
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
                    var (bakedPixels, _) = CapturePipelineService.PreparePixelsWithIcc(
                        px0, w0, h0, _config.IccBakeEnabled, _config.ColorSpaceTag);
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
