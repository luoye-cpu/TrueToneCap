// TrueToneCap.App/Services/AnimationRecorder.cs
// 动图录制引擎 — 基于 WGC + Magick.NET
// P0 修复: 有界帧缓冲防 OOM + 共享 WgcCaptureService 单例 + PeriodicTimer 精确帧定时

using System.Collections.Concurrent;
using System.Threading.Channels;
using ImageMagick;
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
        _framesDropped = 0;
        Task.Run(() => RecordLoop(_cts.Token));
    }

    public async Task StopAndEncodeAsync()
    {
        if (_state != RecordingState.Recording) return;
        _state = RecordingState.Encoding;
        _cts.Cancel();
        _frameChannel.Writer.TryComplete();
        await Task.Run(() => EncodeFrames());
    }

    public void Cancel()
    {
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
                    // 复用共享 WGC 服务（不再创建独立实例）
                    var result = await _wgcService.CaptureMonitorAsync(new WgcCaptureConfig
                    {
                        TargetMonitor = displayInfo.MonitorHandle,
                        PreferHdr = false,
                        FrameTimeoutMs = 100
                    });

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
                catch (InvalidOperationException)
                {
                    // 捕获锁被占用（主截图正在进行），跳过本帧
                    continue;
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
                Report("没有捕获到任何帧");
                return;
            }

            using var col = new MagickImageCollection();
            int encoded = 0;

            for (int i = 0; i < _encodedFrames.Count; i++)
            {
                var (px, w, h) = _encodedFrames[i];
                var rs = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Bgra };
                var img = new MagickImage(px, rs);
                img.AnimationDelay = (uint)delay;
                img.Quality = (uint)_config.Quality;
                col.Add(img);
                encoded++;
                Report();
            }

            var fmt = _config.OutputFormat switch
            {
                AnimationFormat.AnimatedWebP => MagickFormat.WebP,
                AnimationFormat.AnimatedPNG => MagickFormat.APng,
                AnimationFormat.AnimatedAVIF => MagickFormat.Avif,
                AnimationFormat.GIF => MagickFormat.Gif,
                _ => MagickFormat.WebP
            };
            col.Write(path, fmt);
            _config.OutputPath = path;
            _state = RecordingState.Completed;
            LogService.Info("AnimationRecorder", $"编码完成: {encoded} 帧 → {path} (丢弃 {_framesDropped} 帧)");
        }
        catch (Exception ex)
        {
            _state = RecordingState.Error;
            Report(ex.Message);
        }
        finally
        {
            // 释放帧缓冲内存
            _encodedFrames.Clear();
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
