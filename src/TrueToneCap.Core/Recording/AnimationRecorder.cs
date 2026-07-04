// TrueToneCap.Core/Recording/AnimationRecorder.cs
// 动图录制引擎 — 简化版，使用 Magick.NET MagickImageCollection

using System.Collections.Concurrent;
using ImageMagick;
using TrueToneCap.Core.Capture;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.Core.Recording;

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
    private readonly ConcurrentQueue<(byte[] Pixels, int W, int H)> _frameBuffer = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile RecordingState _state = RecordingState.Idle;
    private int _framesCaptured;
    private DateTime _startTime;
    private bool _disposed;

    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public RecordingState State => _state;

    public AnimationRecorder(RecordingConfig config) => _config = config;

    public void StartRecording()
    {
        if (_state != RecordingState.Idle) return;
        _state = RecordingState.Recording;
        _startTime = DateTime.UtcNow;
        _framesCaptured = 0;
        Task.Run(() => RecordLoop(_cts.Token));
    }

    public async Task StopAndEncodeAsync()
    {
        if (_state != RecordingState.Recording) return;
        _state = RecordingState.Encoding;
        _cts.Cancel();
        await Task.Run(() => EncodeFrames());
    }

    public void Cancel()
    {
        _cts.Cancel();
        _state = RecordingState.Cancelled;
    }

    private void RecordLoop(CancellationToken ct)
    {
        var interval = 1000.0 / _config.FrameRate;
        byte[]? last = null;

        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        using var capture = new ScreenCapture(device, _config.DisplayIndex);

        while (!ct.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - _startTime).TotalSeconds >= _config.MaxDurationSeconds)
                break;

            using var frame = capture.TryAcquireNextFrame(16);
            if (frame == null) { Thread.Sleep(16); continue; }

            var pixels = frame.GetBytePixelsAsync(ct).Result;
            if (HasChange(pixels, last, _config.ChangeThreshold))
            {
                _frameBuffer.Enqueue((pixels, frame.Width, frame.Height));
                Interlocked.Increment(ref _framesCaptured);
                last = pixels;
            }

            Report();
            var wait = interval - (DateTime.UtcNow - _startTime).TotalMilliseconds % interval;
            if (wait > 0) Thread.Sleep((int)Math.Min(wait, 16));
        }
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

            int delay = 100 / _config.FrameRate;
            var frames = _frameBuffer.ToArray();
            using var col = new MagickImageCollection();

            for (int i = 0; i < frames.Length; i++)
            {
                var (px, w, h) = frames[i];
                var rs = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Bgra };
                var img = new MagickImage(px, rs);
                img.AnimationDelay = (uint)delay;
                img.Quality = (uint)_config.Quality;
                col.Add(img);
                Interlocked.Increment(ref _framesCaptured); // reuse counter for encoded
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
        }
        catch (Exception ex)
        {
            _state = RecordingState.Error;
            Report(ex.Message);
        }
        finally { Report(); }
    }

    private void Report(string? err = null) =>
        ProgressChanged?.Invoke(this, new RecordingProgressEventArgs
        {
            State = _state,
            FramesCaptured = _framesCaptured,
            FramesEncoded = _framesCaptured,
            ElapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds,
            OutputFile = _config.OutputPath,
            ErrorMessage = err
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel(); _cts.Dispose();
    }
}
