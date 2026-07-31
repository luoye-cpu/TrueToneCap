// TrueToneCap.App/Services/WgcCaptureService.cs
// Windows.Graphics.Capture (WGC) 统一捕获服务 — 会话池化高性能版
// 目标：Windows 11 24H2+，以 WGC 为唯一捕获后端
// 优化：持久会话 + 最新帧缓存 + Staging 纹理复用 + HDR 能力缓存

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.App.Models;

namespace TrueToneCap.App.Services;

/// <summary>WGC 捕获配置。</summary>
public sealed record WgcCaptureConfig
{
    /// <summary>目标显示器 HMONITOR。0 = 鼠标所在显示器。</summary>
    public nint TargetMonitor { get; init; }

    /// <summary>是否优先 HDR 捕获 (R16G16B16A16Float)。</summary>
    public bool PreferHdr { get; init; } = true;

    /// <summary>SDR 帧超时（毫秒）。</summary>
    public int FrameTimeoutMs { get; init; } = 3000;

    /// <summary>是否捕获光标。</summary>
    public bool CaptureCursor { get; init; } = false;

    /// <summary>捕获所有显示器（拼接）。</summary>
    public bool CaptureAllMonitors { get; init; }
}

/// <summary>
/// WGC 统一捕获服务 — 会话池化高性能版。
/// 核心优化：后台持久 Session 持续接收帧，截图时零延迟取最新帧。
/// </summary>
public sealed class WgcCaptureService : IDisposable
{
    // ── 并发保护 ──
    private static readonly SemaphoreSlim s_captureLock = new(1, 1);

    // ── 异步诊断日志（带大小轮转）──
    private static readonly string? s_logPath = Path.Combine(Path.GetTempPath(), "TrueToneCap_WGC.log");
    private static readonly Channel<string>? s_logChannel;
    private static readonly CancellationTokenSource s_logCts = new();
    private const long MaxLogSizeBytes = 1_048_576; // 1MB 轮转上限

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine(msg);
        s_logChannel?.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
    }

    static WgcCaptureService()
    {
        try
        {
            System.IO.File.WriteAllText(s_logPath!, $"=== WGC Pooled {DateTime.Now}\n");
            s_logChannel = System.Threading.Channels.Channel.CreateUnbounded<string>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var entry in s_logChannel.Reader.ReadAllAsync(s_logCts.Token))
                    {
                        // P2: 日志文件轮转 — 超过 1MB 时截断重写
                        var fi = new FileInfo(s_logPath!);
                        if (fi.Exists && fi.Length > MaxLogSizeBytes)
                            await System.IO.File.WriteAllTextAsync(s_logPath!, $"=== Log rotated {DateTime.Now}\n", s_logCts.Token);
                        await System.IO.File.AppendAllTextAsync(s_logPath!, entry, s_logCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        catch { }
    }

    private ID3D11Device? _d3dDevice;
    private nint _deviceMonitor;
    private bool _disposed;

    // ═══ D3D11 线程安全：ImmediateContext 非线程安全，所有 GPU 操作必须加锁 ═══
    private static readonly object s_d3dContextLock = new();

    // ═══ P0: 懒启动会话池 + 延迟自动停止 ═══
    // P3 修复: 使用 (HMONITOR, IsHdr) 元组 key，避免位或标记碰撞
    private readonly Dictionary<(nint Monitor, bool IsHdr), PooledSession> _sessionPool = [];
    private readonly object _poolLock = new();
    private CancellationTokenSource? _idleStopCts;
    private const int IdleStopDelayMs = 15000; // 15 秒无截图则自动停止会话

    // ═══ P1: HDR 能力缓存 ═══
    private static readonly Dictionary<nint, bool> s_hdrCapabilityCache = [];

    // P2: Staging 纹理复用已移入 PooledSession 内部

    // IGraphicsCaptureItemInterop COM GUID
    [ComImport, Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint result);
        int CreateForMonitor(nint hmonitor, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint result);
    }

    // ═══════════════════════════════════════
    //  PooledSession: 持久后台捕获会话
    // ═══════════════════════════════════════

    private sealed class PooledSession : IDisposable
    {
        private readonly nint _hmonitor;
        private readonly DirectXPixelFormat _format;
        private readonly ID3D11Device _d3dDevice;

        private IDirect3DDevice? _winrtDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _item;

        // 最新帧缓存（SDR）— 双缓冲：写入缓冲区与发布缓冲区分离，消除竞态
        private byte[]? _latestSdr;
        private float[]? _latestHdr;
        private byte[]? _writeBufferSdr;   // 生产者写入缓冲区（不与消费者共享）
        private float[]? _writeBufferHdr;
        private int _width, _height;
        private long _frameTimestamp;
        private readonly object _frameLock = new();
        private readonly ManualResetEventSlim _firstFrameEvent = new(false);
        private volatile bool _hasFrame;
        private volatile bool _disposed; // ═══ 防止 Dispose 后 FrameArrived 回调访问已释放资源 ═══

        // Staging 纹理复用
        private ID3D11Texture2D? _stagingTex;
        private int _stagingW, _stagingH;

        // ═══ GPU 纹理缓存：最新帧的 GPU 拷贝，用于直通编码路径（跳过 CPU 回读）═══
        private ID3D11Texture2D? _latestTexture;
        private int _latestTexW, _latestTexH;
        private bool _latestTexValid;

        public int Width => _width;
        public int Height => _height;
        public bool HasFrame => _hasFrame;
        public bool IsHdr => _format == DirectXPixelFormat.R16G16B16A16Float;

        public PooledSession(nint hmonitor, DirectXPixelFormat format, ID3D11Device d3dDevice)
        {
            _hmonitor = hmonitor;
            _format = format;
            _d3dDevice = d3dDevice;
        }

        /// <summary>启动持久捕获会话。</summary>
        public bool Start()
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // WinRT 设备
                _winrtDevice = CreateDirect3DDevice(_d3dDevice);

                // GraphicsCaptureItem
                _item = CreateItemForMonitor(_hmonitor);
                if (_item is null) { Log($"[Pool] {_hmonitor:X}: CreateItem 失败"); return false; }
                _width = _item.Size.Width;
                _height = _item.Size.Height;

                // 帧池（2 帧缓冲，FreeThreaded 避免 UI 线程依赖）
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice, _format, 2, _item.Size);
                _framePool.FrameArrived += OnFrameArrived;

                // 会话
                _session = _framePool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = false;
                // 消除 WGC 默认黄色录制边框
                try { _session.IsBorderRequired = false; } catch { }
                _session.StartCapture();

                sw.Stop();
                Log($"[Pool] {_hmonitor:X}: 会话启动 {sw.ElapsedMilliseconds}ms ({_width}x{_height}) {(IsHdr ? "HDR" : "SDR")}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 启动失败: {ex.Message}");
                return false;
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
        {
            if (_disposed) return; // ═══ 防止 Dispose 后回调访问已释放资源 ═══
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null || _disposed)
                {
                    return;
                }

                using var surface = GetDxgiSurface(frame.Surface);
                if (surface is null)
                {
                    Log($"[Pool] {_hmonitor:X}: GetDxgiSurface 失败");
                    return;
                }

                using var texture = surface.QueryInterface<ID3D11Texture2D>();
                if (texture is null)
                {
                    Log($"[Pool] {_hmonitor:X}: QI ID3D11Texture2D 失败");
                    return;
                }

                int w = _width, h = _height;

                if (IsHdr)
                {
                    var pixels = ReadFloatPixelsPooled(texture, w, h);
                    lock (_frameLock)
                    {
                        // 双缓冲交换：旧发布缓冲变为下次写入缓冲，消除竞态
                        _writeBufferHdr = _latestHdr;
                        _latestHdr = pixels;
                        _latestSdr = null;
                        CacheTextureGpuCopy(texture, w, h);
                        _frameTimestamp = Environment.TickCount64;
                    }
                }
                else
                {
                    var pixels = ReadBytePixelsPooled(texture, w, h);
                    lock (_frameLock)
                    {
                        // 双缓冲交换：旧发布缓冲变为下次写入缓冲，消除竞态
                        _writeBufferSdr = _latestSdr;
                        _latestSdr = pixels;
                        _latestHdr = null;
                        CacheTextureGpuCopy(texture, w, h);
                        _frameTimestamp = Environment.TickCount64;
                    }
                }

                if (!_hasFrame)
                {
                    _hasFrame = true;
                    _firstFrameEvent.Set();
                    Log($"[Pool] {_hmonitor:X}: 首帧就绪 {w}x{h}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Pool] FrameArrived 异常: {ex.Message}");
            }
        }

        /// <summary>等待首帧到达。</summary>
        public bool WaitForFirstFrame(int timeoutMs) => _firstFrameEvent.Wait(timeoutMs);

        /// <summary>获取最新 SDR 帧（安全拷贝，调用方可跨线程使用）。</summary>
        public byte[]? GetLatestSdr()
        {
            lock (_frameLock)
            {
                if (_latestSdr is null) return null;
                var copy = new byte[_latestSdr.Length];
                Buffer.BlockCopy(_latestSdr, 0, copy, 0, copy.Length);
                return copy;
            }
        }

        /// <summary>获取最新 HDR 帧（安全拷贝，调用方可跨线程使用）。</summary>
        public float[]? GetLatestHdr()
        {
            lock (_frameLock)
            {
                if (_latestHdr is null) return null;
                var copy = new float[_latestHdr.Length];
                Buffer.BlockCopy(_latestHdr, 0, copy, 0, copy.Length * sizeof(float));
                return copy;
            }
        }

        /// <summary>获取帧年龄（ms）。</summary>
        public long GetFrameAge()
        {
            lock (_frameLock) { return Environment.TickCount64 - _frameTimestamp; }
        }

        /// <summary>获取最新帧的 GPU 纹理拷贝（用于 GPU 直通编码路径）。
        /// 返回的纹理是 Default 资源，不可 CPU 读，不可跨 D3D11 设备使用。
        /// 调用方必须自行 AddRef 或 CopyResource 后再跨线程使用。
        /// 返回 null 表示无帧或 GPU 拷贝失败。</summary>
        public ID3D11Texture2D? GetLatestTexture()
        {
            lock (_frameLock)
            {
                if (!_latestTexValid || _latestTexture is null) return null;
                // 返回 AddRef 后的拷贝，调用方负责 Release
                // 通过 QueryInterface 增加引用计数
                var ptr = Marshal.GetIUnknownForObject(_latestTexture);
                if (ptr == 0) return null;
                try
                {
                    var copy = _latestTexture.QueryInterface<ID3D11Texture2D>();
                    return copy;
                }
                catch { return null; }
            }
        }

        /// <summary>在锁内缓存 GPU 纹理拷贝（从 WGC 帧纹理拷贝到本地的 Default 纹理）。</summary>
        private void CacheTextureGpuCopy(ID3D11Texture2D wgcTexture, int w, int h)
        {
            try
            {
                // 尺寸变化时重新创建
                if (_latestTexture is null || _latestTexW != w || _latestTexH != h)
                {
                    _latestTexture?.Dispose();
                    _latestTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = wgcTexture.Description.Format,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None
                    });
                    _latestTexW = w;
                    _latestTexH = h;
                }
                // D3D11 线程安全：此方法在 _frameLock 内调用，且与 _d3dDevice 同一线程
                var ctx = _d3dDevice.ImmediateContext;
                ctx.CopyResource(_latestTexture, wgcTexture);
                _latestTexValid = true;
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: CacheTextureGpuCopy 失败: {ex.Message}");
                _latestTexValid = false;
            }
        }

        // ── Staging 纹理复用 + 像素缓冲区复用的像素读取 ──

        private byte[] ReadBytePixelsPooled(ID3D11Texture2D texture, int w, int h)
        {
            // P2: 复用像素缓冲区（避免 60fps 下每帧分配 33MB）
            // 修复竞态：使用独立写入缓冲区，不与消费者共享的 _latestSdr 冲突
            int len = w * h * 4;
            if (_writeBufferSdr is null || _writeBufferSdr.Length != len)
                _writeBufferSdr = new byte[len];
            var pixels = _writeBufferSdr;

            // ═══ D3D11 线程安全：所有 GPU 操作必须在锁内执行 ═══
            lock (s_d3dContextLock)
            {
                var ctx = _d3dDevice.ImmediateContext;

                // P2: 复用 staging 纹理
                if (_stagingTex is null || _stagingW != w || _stagingH != h)
                {
                    _stagingTex?.Dispose();
                    _stagingTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    });
                    _stagingW = w; _stagingH = h;
                }

                ctx.CopyResource(_stagingTex, texture);
                var mapped = ctx.Map(_stagingTex, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int dstStride = w * 4;

                    fixed (byte* dst = pixels)
                    {
                        for (int row = 0; row < h; row++)
                        {
                            byte* srcRow = srcBase + row * srcRowPitch;
                            byte* dstRow = dst + row * dstStride;
                            Buffer.MemoryCopy(srcRow, dstRow, dstStride, dstStride);
                        }
                    }
                }
                ctx.Unmap(_stagingTex, 0);
            }

            // Alpha 修复（WGC 输出 alpha 可能为 0）— 纯 CPU 操作，无需锁
            TrueToneCap.Core.PixelOps.FixAlphaChannel(pixels);
            return pixels;
        }

        private float[] ReadFloatPixelsPooled(ID3D11Texture2D texture, int w, int h)
        {
            int pixelCount = w * h * 4;

            // P2: 复用像素缓冲区（避免 60fps 下每帧分配 132MB）
            // 修复竞态：使用独立写入缓冲区，不与消费者共享的 _latestHdr 冲突
            if (_writeBufferHdr is null || _writeBufferHdr.Length != pixelCount)
                _writeBufferHdr = new float[pixelCount];
            var pixels = _writeBufferHdr;

            // ═══ D3D11 线程安全：所有 GPU 操作必须在锁内执行 ═══
            lock (s_d3dContextLock)
            {
                var ctx = _d3dDevice.ImmediateContext;

                if (_stagingTex is null || _stagingW != w || _stagingH != h)
                {
                    _stagingTex?.Dispose();
                    _stagingTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = Vortice.DXGI.Format.R16G16B16A16_Float,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    });
                    _stagingW = w; _stagingH = h;
                }

                ctx.CopyResource(_stagingTex, texture);
                var mapped = ctx.Map(_stagingTex, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int halfsPerRow = w * 4;

                    fixed (float* dst = pixels)
                    {
                        for (int row = 0; row < h; row++)
                        {
                            byte* srcRow = srcBase + row * srcRowPitch;
                            float* dstRow = dst + row * w * 4;
                            TrueToneCap.Core.PixelOps.ConvertHalfToFloatRow(srcRow, dstRow, halfsPerRow);
                        }
                    }
                }
                ctx.Unmap(_stagingTex, 0);
            }

            return pixels;
        }

        public void Dispose()
        {
            _disposed = true; // ═══ 先标记，阻止后续 FrameArrived 回调 ═══
            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }
            try { _winrtDevice?.Dispose(); } catch { }
            lock (s_d3dContextLock) { try { _stagingTex?.Dispose(); } catch { } }
            try { _latestTexture?.Dispose(); } catch { }
            _firstFrameEvent.Dispose();
        }
    }

    // ═══════════════════════════════════════
    //  设备管理
    // ═══════════════════════════════════════

    /// <summary>获取或创建 D3D11 设备。线程安全（内部加锁）。</summary>
    public ID3D11Device GetOrCreateDevice(nint hmonitor)
    {
        if (_d3dDevice is not null && _deviceMonitor == hmonitor)
        {
            try
            {
                _ = _d3dDevice.ImmediateContext;
                return _d3dDevice;
            }
            catch
            {
                lock (s_d3dContextLock) { _d3dDevice?.Dispose(); }
                _d3dDevice = null;
            }
        }

        lock (s_d3dContextLock) { _d3dDevice?.Dispose(); }
        _d3dDevice = null;
        _deviceMonitor = hmonitor;

        // 尝试通过 DXGI 找到匹配的适配器
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter is null) break;
                try
                {
                    for (uint j = 0; ; j++)
                    {
                        if (adapter.EnumOutputs(j, out var output).Failure || output is null) break;
                        try
                        {
                            if (output.Description.Monitor == hmonitor)
                            {
                                _d3dDevice = D3D11.D3D11CreateDevice(
                                    Vortice.Direct3D.DriverType.Hardware,
                                    DeviceCreationFlags.BgraSupport);
                                Log($"[WGC] D3D11 设备创建, 适配器: {adapter.Description.Description.Trim()}");
                                return _d3dDevice;
                            }
                        }
                        finally { output.Dispose(); }
                    }
                }
                finally { adapter.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Log($"[WGC] 适配器匹配跳过: {ex.Message}");
        }

        _d3dDevice = D3D11.D3D11CreateDevice(
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        Log("[WGC] D3D11 设备创建于默认适配器 (回退)");
        return _d3dDevice;
    }

    // ═══════════════════════════════════════
    //  会话池管理
    // ═══════════════════════════════════════

    /// <summary>获取或创建指定显示器的持久 SDR 会话。</summary>
    private PooledSession GetOrCreateSdrSession(nint hmonitor)
    {
        var key = (hmonitor, false);
        lock (_poolLock)
        {
            if (_sessionPool.TryGetValue(key, out var existing))
            {
                // 会话已存在（无论是否有帧），直接复用
                return existing;
            }

            var device = GetOrCreateDevice(hmonitor);
            // WGC 仅支持 B8G8R8A8UIntNormalized 和 R16G16B16A16Float 两种帧池格式。
            // HDR 显示器上 DWM 自动将 HDR 内容色调映射为 SDR 后交付，输出已是 sRGB gamma 编码。
            var session = new PooledSession(hmonitor, DirectXPixelFormat.B8G8R8A8UIntNormalized, device);
            if (!session.Start())
                throw new InvalidOperationException($"无法为显示器 0x{hmonitor:X} 创建 WGC 会话");

            _sessionPool[key] = session;
            return session;
        }
    }

    /// <summary>获取或创建指定显示器的持久 HDR 会话。</summary>
    private PooledSession? GetOrCreateHdrSession(nint hmonitor)
    {
        // P1: HDR 能力缓存 — 已知不支持则跳过
        lock (s_hdrCapabilityCache)
        {
            if (s_hdrCapabilityCache.TryGetValue(hmonitor, out var supported) && !supported)
                return null;
        }

        var key = (hmonitor, true);
        lock (_poolLock)
        {
            if (_sessionPool.TryGetValue(key, out var existing) && existing.HasFrame)
                return existing;

            if (existing is not null)
            {
                existing.Dispose();
                _sessionPool.Remove(key);
            }

            var device = GetOrCreateDevice(hmonitor);
            var session = new PooledSession(hmonitor, DirectXPixelFormat.R16G16B16A16Float, device);
            if (!session.Start())
            {
                lock (s_hdrCapabilityCache) { s_hdrCapabilityCache[hmonitor] = false; }
                return null;
            }

            _sessionPool[key] = session;
            return session;
        }
    }

    /// <summary>预热（懒启动模式下不再预热，仅记录日志）。</summary>
    public void WarmupSessions()
    {
        Log("[Warmup] 懒启动模式：会话将在首次截图时按需创建");
    }

    /// <summary>截图完成后调用，启动延迟自动停止计时器。</summary>
    private void ScheduleIdleStop()
    {
        _idleStopCts?.Cancel();
        _idleStopCts?.Dispose();
        var cts = new CancellationTokenSource();
        _idleStopCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleStopDelayMs, cts.Token);
                // 超时 → 停止所有会话释放资源
                lock (_poolLock)
                {
                    if (_sessionPool.Count > 0)
                    {
                        foreach (var s in _sessionPool.Values) s.Dispose();
                        _sessionPool.Clear();
                        Log("[WGC] 空闲超时，所有会话已自动停止");
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>使所有会话失效（显示器配置变更时调用）。</summary>
    public void InvalidateSessions()
    {
        lock (_poolLock)
        {
            foreach (var s in _sessionPool.Values) s.Dispose();
            _sessionPool.Clear();
        }
        lock (s_hdrCapabilityCache) { s_hdrCapabilityCache.Clear(); }
        Log("[WGC] 所有会话已失效");
    }

    // ═══════════════════════════════════════
    //  单显示器捕获（池化快速路径）
    // ═══════════════════════════════════════

    /// <summary>捕获单个显示器。从持久会话取最新帧，无需等待 VSync。</summary>
    public async Task<CaptureResult> CaptureMonitorAsync(WgcCaptureConfig? config = null)
    {
        if (!await s_captureLock.WaitAsync(0))
            throw new InvalidOperationException("已有另一个捕获正在进行，请稍后重试。");
        try { return CaptureMonitorInternal(config); }
        finally { s_captureLock.Release(); }
    }

    /// <summary>无锁读取最新帧（供录制器使用）。不获取 s_captureLock，不创建会话，
    /// 仅从已有池化会话中读取最新帧。如果会话不存在或无帧则返回 null。</summary>
    public CaptureResult? TryGetLatestFrame(nint targetMonitor)
    {
        lock (_poolLock)
        {
            var key = (targetMonitor, false);
            if (!_sessionPool.TryGetValue(key, out var session) || !session.HasFrame)
                return null;
            var pixels = session.GetLatestSdr();
            if (pixels is null) return null;
            return new CaptureResult
            {
                SdrPixels = pixels,
                Width = session.Width,
                Height = session.Height
            };
        }
    }

    /// <summary>捕获主体（同步，在调用线程执行）。
    /// 统一捕获 SDR（用于预览）+ HDR（用于编码），一次调用返回两者。</summary>
    private CaptureResult CaptureMonitorInternal(WgcCaptureConfig? config = null)
    {
        config ??= new WgcCaptureConfig();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        nint targetMonitor = config.TargetMonitor != 0
            ? config.TargetMonitor
            : DisplayEnumerator.GetMonitorUnderCursor();

        var displayInfo = DisplayEnumerator.FindDisplayByMonitor(targetMonitor);
        if (displayInfo is null)
            throw new InvalidOperationException("找不到目标显示器。");

        // ═══ 1. 始终捕获 SDR（用于预览）═══
        var sdrSession = GetOrCreateSdrSession(targetMonitor);
        Log($"[WGC] SDR 池状态: HasFrame={sdrSession.HasFrame} Age={sdrSession.GetFrameAge()}ms");

        byte[]? sdrPixels = null;
        if (sdrSession.HasFrame)
        {
            sdrPixels = sdrSession.GetLatestSdr();
        }
        else
        {
            Log($"[WGC] SDR 等待首帧 (timeout={config.FrameTimeoutMs}ms)...");
            if (sdrSession.WaitForFirstFrame(config.FrameTimeoutMs))
                sdrPixels = sdrSession.GetLatestSdr();
        }

        if (sdrPixels is null)
            throw new TimeoutException($"SDR 捕获超时（{config.FrameTimeoutMs}ms）。");

        int w = sdrSession.Width, h = sdrSession.Height;

        // ═══ 2. 尝试 Float16 扩展范围捕获（HDR + 广色域 SDR 均适用）═══
        // 不限制 IsHdr：SDR 广色域显示器 (P3/AdobeRGB + ACM) 的 DWM 内部合成缓冲区
        // 也是扩展范围的，Float16 会话可以获取完整广色域数据。
        // 如果显示器不支持 Float16 帧池，GetOrCreateHdrSession 会返回 null（已缓存）。
        float[]? hdrPixels = null;
        if (config.PreferHdr)
        {
            try
            {
                var hdrSession = GetOrCreateHdrSession(targetMonitor);
                if (hdrSession is not null)
                {
                    if (!hdrSession.HasFrame)
                    {
                        Log($"[WGC] Float16 等待首帧 (timeout={config.FrameTimeoutMs}ms)...");
                        hdrSession.WaitForFirstFrame(config.FrameTimeoutMs);
                    }
                    if (hdrSession.HasFrame)
                        hdrPixels = hdrSession.GetLatestHdr();
                }
            }
            catch (Exception ex)
            {
                Log($"[WGC] Float16 捕获失败: {ex.Message}（不影响 SDR 结果）");
            }
        }

        // ═══ 3. 获取 GPU 纹理（用于 GPU 直通编码路径）═══
        // 优先使用 SDR 会话的纹理，因为 NVENC 输入格式为 BGRA8
        ID3D11Texture2D? gpuTexture = null;
        try
        {
            gpuTexture = sdrSession.GetLatestTexture();
            Log($"[WGC] GPU 纹理获取: {(gpuTexture is not null ? "✓" : "✗")}");
        }
        catch (Exception ex)
        {
            Log($"[WGC] GPU 纹理获取失败: {ex.Message}（不影响编码结果）");
        }

        var result = new CaptureResult
        {
            SdrPixels = sdrPixels,
            HdrPixels = hdrPixels,
            Width = w,
            Height = h,
            GpuTexture = gpuTexture
        };

        // 附加 ICC + 显示器信息
        try
        {
            result.IccProfile = ColorProfileProvider.GetDisplayIccProfile(displayInfo.MonitorHandle);
            result.SourceDisplay = displayInfo;
        }
        catch { }

        sw.Stop();
        result.CaptureTimeMs = sw.ElapsedMilliseconds;
        Log($"[WGC] CaptureMonitor 完成: {w}x{h} {sw.ElapsedMilliseconds}ms (HDR={hdrPixels is not null}, GPU纹理={gpuTexture is not null})");
        ScheduleIdleStop();
        return result;
    }

    /// <summary>一次性捕获回退（池化会话不可用时使用）。在专用 STA 线程执行以确保 WGC 帧投递。</summary>
    private CaptureResult? OneShotCapture(nint hmonitor, DisplayInfo displayInfo, int timeoutMs)
    {
        CaptureResult? result = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var tcs = new TaskCompletionSource<CaptureResult?>();
            IDirect3DDevice? winrtDevice = null;
            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;
            bool captured = false;

            try
            {
                var device = GetOrCreateDevice(hmonitor);
                winrtDevice = CreateDirect3DDevice(device);
                var item = CreateItemForMonitor(hmonitor);
                if (item is null) { done.Set(); return; }
                int w = item.Size.Width, h = item.Size.Height;

                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

                framePool.FrameArrived += (sender, _) =>
                {
                    if (captured) return;
                    try
                    {
                        using var frame = sender.TryGetNextFrame();
                        if (frame is null) return;
                        using var surface = GetDxgiSurface(frame.Surface);
                        if (surface is null) return;
                        using var texture = surface.QueryInterface<ID3D11Texture2D>();
                        if (texture is null) return;

                        var ctx = device.ImmediateContext;
                        using var staging = device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                            SampleDescription = new(1, 0),
                            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read
                        });
                        ctx.CopyResource(staging, texture);
                        var mapped = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        var pixels = new byte[w * h * 4];
                        unsafe
                        {
                            byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                            int srcRowPitch = (int)mapped.RowPitch;
                            int dstStride = w * 4;
                            fixed (byte* dst = pixels)
                            {
                                for (int row = 0; row < h; row++)
                                    Buffer.MemoryCopy(srcBase + row * srcRowPitch, dst + row * dstStride, dstStride, dstStride);
                            }
                        }
                        ctx.Unmap(staging, 0);
                        TrueToneCap.Core.PixelOps.FixAlphaChannel(pixels);

                        // 创建 GPU 纹理拷贝（用于 GPU 直通编码路径）
                        ID3D11Texture2D? gpuTex = null;
                        try
                        {
                            gpuTex = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                                SampleDescription = new(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None
                            });
                            ctx.CopyResource(gpuTex, texture);
                        }
                        catch { gpuTex?.Dispose(); gpuTex = null; }

                        captured = true;
                        tcs.TrySetResult(new CaptureResult { SdrPixels = pixels, Width = w, Height = h, GpuTexture = gpuTex });
                    }
                    catch { }
                };

                session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                session.StartCapture();
                Log("[WGC] 一次性捕获: 会话已启动，等待帧...");

                // 在 STA 线程上泵消息等待帧到达
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!captured && sw.ElapsedMilliseconds < timeoutMs)
                {
                    PumpMessages();
                    Thread.Sleep(1);
                }

                if (captured)
                {
                    result = tcs.Task.IsCompleted ? tcs.Task.Result : null;
                    Log($"[WGC] 一次性捕获完成: {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Log($"[WGC] 一次性捕获超时 ({timeoutMs}ms)");
                }
            }
            catch (Exception ex)
            {
                Log($"[WGC] 一次性捕获异常: {ex.Message}");
            }
            finally
            {
                try { session?.Dispose(); } catch { }
                try { framePool?.Dispose(); } catch { }
                try { winrtDevice?.Dispose(); } catch { }
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(timeoutMs + 1000);
        return result;
    }

    // ═══════════════════════════════════════
    //  全桌面捕获（多显示器拼接）
    // ═══════════════════════════════════════

    /// <summary>捕获所有显示器并拼接为完整桌面图像。</summary>
    public async Task<CaptureResult> CaptureAllMonitorsAsync(WgcCaptureConfig? config = null)
    {
        if (!await s_captureLock.WaitAsync(0))
            throw new InvalidOperationException("已有另一个捕获正在进行，请稍后重试。");
        try { return await CaptureAllMonitorsInternalAsync(config); }
        finally { s_captureLock.Release(); }
    }

    private async Task<CaptureResult> CaptureAllMonitorsInternalAsync(WgcCaptureConfig? config = null)
    {
        config ??= new WgcCaptureConfig();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var displays = DisplayEnumerator.EnumerateDisplays();
        Log($"[WGC] CaptureAllMonitors: {displays.Count} 显示器");
        if (displays.Count == 0)
            throw new InvalidOperationException("未检测到任何显示器。");

        if (displays.Count == 1)
        {
            // 同步调用，已在 Task.Run 线程池中执行，不会阻塞 UI 线程
            // 单显示器：同时捕获 SDR（用于预览）和 HDR（用于编码/预览）
            var single = CaptureMonitorInternal(new WgcCaptureConfig
            {
                TargetMonitor = displays[0].MonitorHandle,
                PreferHdr = true, // 同时获取 HDR 和 SDR 数据
                FrameTimeoutMs = config.FrameTimeoutMs
            });
            sw.Stop();
            single.CaptureTimeMs = sw.ElapsedMilliseconds;
            return single;
        }

        // 计算虚拟桌面边界
        int vx = displays.Min(d => d.X);
        int vy = displays.Min(d => d.Y);
        int vw = displays.Max(d => d.X + d.Width) - vx;
        int vh = displays.Max(d => d.Y + d.Height) - vy;

        var fullPixels = new byte[vw * vh * 4];
        var fullStride = vw * 4;

        // 从各显示器的池化会话取帧并拼接
        foreach (var display in displays)
        {
            var session = GetOrCreateSdrSession(display.MonitorHandle);
            if (!session.HasFrame && !session.WaitForFirstFrame(config.FrameTimeoutMs))
            {
                Log($"[WGC] 显示器 {display.Name} 首帧超时，跳过");
                continue;
            }

            var pixels = session.GetLatestSdr();
            if (pixels is null) continue;

            int dx = display.X - vx;
            int dy = display.Y - vy;
            int srcStride = session.Width * 4;
            int copyH = Math.Min(session.Height, display.Height);

            for (int row = 0; row < copyH; row++)
            {
                int srcOff = row * srcStride;
                int dstOff = ((dy + row) * fullStride) + (dx * 4);
                Buffer.BlockCopy(pixels, srcOff, fullPixels, dstOff, srcStride);
            }
        }

        sw.Stop();
        Log($"[WGC] CaptureAllMonitors 完成: {vw}x{vh} {sw.ElapsedMilliseconds}ms");
        return new CaptureResult
        {
            SdrPixels = fullPixels,
            Width = vw,
            Height = vh,
            CaptureTimeMs = sw.ElapsedMilliseconds
        };
    }

    // ═══════════════════════════════════════
    //  静态辅助方法（COM 互操作）
    // ═══════════════════════════════════════

    private static GraphicsCaptureItem? CreateItemForMonitor(nint hmonitor)
    {
        // 方法0: TryCreateFromDisplayId
        try
        {
            var item = CreateItemFromDisplayId(hmonitor);
            if (item is not null)
            {
                Log($"[WGC] 方法0(DisplayId): OK {item.Size.Width}x{item.Size.Height}");
                return item;
            }
        }
        catch (Exception ex) { Log($"[WGC] 方法0 异常: {ex.Message}"); }

        var itemGuid = typeof(GraphicsCaptureItem).GUID;

        // 方法1: CsWinRT As<> 扩展
        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            int hr = interop.CreateForMonitor(hmonitor, itemGuid, out var ptr);
            Log($"[WGC] 方法1(CsWinRT): hr=0x{hr:X8}");
            if (hr >= 0 && ptr != 0) return MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        }
        catch (Exception ex) { Log($"[WGC] 方法1: {ex.Message}"); }

        // 方法2: RoGetActivationFactory + vtable
        try
        {
            var interopGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var factoryGuid = new Guid("00000035-0000-0000-C000-000000000046");
            var className = "Windows.Graphics.Capture.GraphicsCaptureItem";

            int hr = RoGetActivationFactory(IntPtrHelper.CreateString(className), ref factoryGuid, out var facPtr);
            if (hr >= 0 && facPtr != 0)
            {
                try
                {
                    hr = Marshal.QueryInterface(facPtr, in interopGuid, out var interopPtr);
                    if (hr >= 0 && interopPtr != 0)
                    {
                        try
                        {
                            hr = VtblCall4(interopPtr, hmonitor, itemGuid, out var itemPtr);
                            if (hr >= 0 && itemPtr != 0) return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                        }
                        finally { Marshal.Release(interopPtr); }
                    }
                }
                finally { Marshal.Release(facPtr); }
            }
        }
        catch (Exception ex) { Log($"[WGC] 方法2 异常: {ex.Message}"); }

        // 方法3: TryCreateFromWindowId（桌面窗口回退）
        try
        {
            var item = CreateItemFromDesktopWindow(hmonitor);
            if (item is not null) return item;
        }
        catch (Exception ex) { Log($"[WGC] 方法3 异常: {ex.Message}"); }

        Log("[WGC] ❌ 所有方法均无法创建 GraphicsCaptureItem");
        return null;
    }

    private static Windows.Graphics.DisplayId? s_cachedDisplayId;
    private static GraphicsCaptureItem? CreateItemFromDisplayId(nint hmonitor)
    {
        try
        {
            if (s_cachedDisplayId is null)
            {
                var primaryArea = Microsoft.UI.Windowing.DisplayArea.Primary;
                if (primaryArea is null) return null;
                s_cachedDisplayId = new Windows.Graphics.DisplayId(primaryArea.DisplayId.Value);
            }
            return GraphicsCaptureItem.TryCreateFromDisplayId(s_cachedDisplayId.Value);
        }
        catch { return null; }
    }

    private static GraphicsCaptureItem? CreateItemFromDesktopWindow(nint targetMonitor)
    {
        nint[] candidateHwnds = [GetShellWindow(), GetDesktopWindow()];
        foreach (var hwnd in candidateHwnds)
        {
            if (hwnd == 0) continue;
            try
            {
                var msWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var wsWindowId = new Windows.UI.WindowId(msWindowId.Value);
                var item = GraphicsCaptureItem.TryCreateFromWindowId(wsWindowId);
                if (item is not null && item.Size.Width > 0 && item.Size.Height > 0)
                    return item;
            }
            catch { }
        }
        return null;
    }

    // ── Win32 消息泵（STA 线程 WGC 帧投递需要） ──
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int pt_x; public int pt_y; }

    [DllImport("user32.dll")]
    private static extern int PeekMessageW(out MSG msg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);

    private static void PumpMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, 1 /*PM_REMOVE*/) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    private static int VtblCall4(nint pThis, nint hmonitor, Guid riid, out nint result)
    {
        nint vtable = Marshal.ReadIntPtr(pThis);
        nint methodPtr = Marshal.ReadIntPtr(vtable + 4 * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateForMonitorDelegate>(methodPtr);
        return fn(pThis, hmonitor, ref riid, out result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitorDelegate(nint pThis, nint hmonitor, ref Guid riid, out nint result);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint hstring, ref Guid iid, out nint factory);

    private static class IntPtrHelper
    {
        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out nint hstr);
        public static nint CreateString(string s) { WindowsCreateString(s, s.Length, out var h); return h; }
    }

    /// <summary>从 Vortice D3D11 设备创建 WinRT IDirect3DDevice。</summary>
    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        if (dxgiDevice is null)
            throw new InvalidOperationException("[WGC] QI for IDXGIDevice 失败");

        var winrtGuid = typeof(IDirect3DDevice).GUID;
        int hr = Marshal.QueryInterface(dxgiDevice.NativePointer, in winrtGuid, out var winrtPtr);

        if (hr >= 0 && winrtPtr != 0)
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);

        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var d3d11Ptr);
        if (hr < 0 || d3d11Ptr == 0)
            throw new InvalidOperationException($"[WGC] CreateDirect3D11DeviceFromDXGIDevice 失败 hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(d3d11Ptr, in winrtGuid, out winrtPtr);
        Marshal.Release(d3d11Ptr);
        if (hr < 0 || winrtPtr == 0)
            throw new InvalidOperationException($"[WGC] 回退 QI 失败 hr=0x{hr:X8}");

        return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint outD3D11Device);

    /// <summary>从 WinRT IDirect3DSurface 获取 DXGI 表面。</summary>
    private static IDXGISurface? GetDxgiSurface(IDirect3DSurface surface)
    {
        // 方法1: 通过 IWinRTObject 获取原生 COM 指针 → IDirect3DDxgiInterfaceAccess → GetSurface
        try
        {
            if (surface is IWinRTObject winrtObj)
            {
                nint thisPtr = winrtObj.NativeObject.GetRef();
                var accessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
                int hr = Marshal.QueryInterface(thisPtr, in accessGuid, out var accessPtr);
                if (hr >= 0 && accessPtr != 0)
                {
                    try
                    {
                        var dxgiGuid = typeof(IDXGISurface).GUID;
                        hr = VtblCall3(accessPtr, ref dxgiGuid, out var dxgiPtr);
                        if (hr >= 0 && dxgiPtr != 0)
                        {
                            return new IDXGISurface(dxgiPtr);
                        }
                    }
                    finally { Marshal.Release(accessPtr); }
                }
            }
        }
        catch { }

        // 方法2: Marshal.GetIUnknownForObject 回退
        try
        {
            nint nativePtr = Marshal.GetIUnknownForObject(surface);
            try
            {
                var accessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
                int hr = Marshal.QueryInterface(nativePtr, in accessGuid, out var accessPtr);
                if (hr >= 0 && accessPtr != 0)
                {
                    try
                    {
                        var dxgiGuid = typeof(IDXGISurface).GUID;
                        hr = VtblCall3(accessPtr, ref dxgiGuid, out var dxgiPtr);
                        if (hr >= 0 && dxgiPtr != 0)
                            return new IDXGISurface(dxgiPtr);
                    }
                    finally { Marshal.Release(accessPtr); }
                }
            }
            finally { Marshal.Release(nativePtr); }
        }
        catch { }

        // 方法3: 直接 QI IDXGISurface
        try
        {
            nint nativePtr = Marshal.GetIUnknownForObject(surface);
            try
            {
                var dxgiGuid = typeof(IDXGISurface).GUID;
                int hr = Marshal.QueryInterface(nativePtr, in dxgiGuid, out var dxgiPtr);
                if (hr >= 0 && dxgiPtr != 0)
                {
                    // 修复: 不再多余 Release — IDXGISurface 构造函数接管指针所有权
                    return new IDXGISurface(dxgiPtr);
                }
            }
            finally { Marshal.Release(nativePtr); }
        }
        catch { }

        return null;
    }

    private static int VtblCall3(nint pThis, ref Guid iid, out nint result)
    {
        nint vtable = Marshal.ReadIntPtr(pThis);
        nint methodPtr = Marshal.ReadIntPtr(vtable + 3 * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<VtblCall3Delegate>(methodPtr);
        return fn(pThis, ref iid, out result);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtblCall3Delegate(nint pThis, ref Guid iid, out nint result);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InvalidateSessions();
        try { _d3dDevice?.Dispose(); } catch { }
        s_logCts.Cancel();
    }
}
