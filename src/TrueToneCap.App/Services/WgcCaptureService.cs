// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
using TrueToneCap.Core.Encoding;
using TrueToneCap.App.Models;
using TrueToneCap.Core.Logging;   // LogLevel / LogCategory（定义已移到 Core）

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
public sealed partial class WgcCaptureService : IDisposable
{
    // ── 并发保护 ──
    private static readonly SemaphoreSlim s_captureLock = new(1, 1);

    // ── 内部诊断日志（使用统一 LogService）──
    private static void Log(string msg) => LogService.Debug("WgcCapture", msg, LogCategory.Capture);
    private static void LogInfo(string msg) => LogService.Info("WgcCapture", msg, LogCategory.Capture); // 关键事件落盘

    private ID3D11Device? _d3dDevice;
    private nint _deviceMonitor;
    private bool _disposed;

    // ═══ D3D11 线程安全：ImmediateContext 非线程安全，所有 GPU 操作必须加锁 ═══
    // 实例级锁：每个 WgcCaptureService 实例的 D3D11 设备各自加锁，多显示器场景不互相阻塞
    private readonly object _d3dContextLock = new();

    // 2026-10-01：这里原有 `public object D3dContextLock` 与 `public ID3D11Device? GetSharedDevice()`
    // 两个"把设备/锁借给外部组件"的出口，唯一消费者是覆盖层那扇下层供帧窗（报告 §39 已删）。
    // 内部仍然处处用 _d3dContextLock，所以字段留着，公共出口撤掉——
    // 留一个没人调用的"共享设备入口"，就等于留一份"外部可以并进这条 GPU 上下文"的错误承诺。

    // ═══ P0: 懒启动会话池 + 延迟自动停止 ═══
    // P3 修复: 使用 (HMONITOR, IsHdr) 元组 key，避免位或标记碰撞
    private readonly Dictionary<(nint Monitor, bool IsHdr), PooledSession> _sessionPool = [];
    private readonly object _poolLock = new();
    private CancellationTokenSource? _idleStopCts;
    private const int IdleStopDelayMs = 15000; // 15 秒无截图则自动停止会话

    // ═══ P1: HDR 能力缓存 ═══
    private static readonly Dictionary<nint, bool> s_hdrCapabilityCache = [];

    // ═══ 2026-09-19: Float16「连续空帧」软冷却 —— 与上面的能力位是两回事，务必别混 ═══
    // 能力判定（CanDeliverExtendedRange）只回答「这块屏给不给得出 >8-bit」，答不了「DWM 这次到底成不成帧」。
    // 会话建得起来却从不成帧的屏（远程/虚拟显示器、刚切分辨率、驱动抽风）以前每次截图都要付满
    // 一次 FrameTimeoutMs(3000ms) 才退回 8-bit —— 而且这类屏恰恰多是被判「宽松放行」的（枚举不到/查不到就放行）。
    //
    // 为什么不干脆写进 s_hdrCapabilityCache：那是把「这次没成帧」这种**瞬时状态**固化成「能力不支持」，
    // 而能力缓存进程内不会自动复位 ⇒ 一次休眠恢复就让该屏**永久**失去高精度源（静默降级、用户零感知）。
    // 2026-09-15 那条修复正是为此才把「会话启动失败」从能力缓存里摘掉的，不能又从这里漏回去。
    //
    // 所以这里只是计数器，语义是「先歇一会儿，待会儿再试」：
    //   同一块屏连续 HdrEmptyStreakToCoolDown 次未成帧 ⇒ 之后 HdrSkipAttempts 次不再【新建】Float16 会话；
    //   一旦成帧立刻全部清零；显示器配置变更时随 InvalidateSessions 一起作废。
    // 冷却只拦「新建再试」，绝不拦「复用已经出帧的 Float16 会话」（见 GetOrCreateHdrSession 里的判定顺序），
    // 所以不会把一个正常工作的浮点源挡掉；代价是冷却期内那几次截图会退成 8-bit（有日志、不静默）。
    private static readonly Dictionary<nint, int> s_hdrEmptyStreak = [];
    private static readonly Dictionary<nint, int> s_hdrSkipRemaining = [];
    private static readonly object s_hdrStallLock = new();
    private const int HdrEmptyStreakToCoolDown = 3;
    private const int HdrSkipAttempts = 10;

    // ═══ P0: 捕获降级警告传播 ═══
    // 当 CreateItemForMonitor 回退到方法3 (桌面窗口=仅壁纸) 时，记录警告文本供给 CaptureResult 传播
    private static string? s_lastItemDegradation;

    // P2: Staging 纹理复用已移入 PooledSession 内部

    // ── WGC interop 的两个 GUID：各有其主，别再混用 ──
    // 权威源：Windows Kits\10\Include\{10.0.26100.0,10.0.28000.0}\um\Windows.Graphics.Capture.Interop.h
    //         两版一致；本机实测评探针见 publish/probe-wgc-iid.ps1
    // ① interop 接口 IID —— 用于 QI **激活工厂**（方法0 的 As<T>() 与方法2 的 QueryInterface 都取它）。
    //    该接口派生自 IUnknown（不是 IInspectable），故 vtable：CreateForWindow=槽3、CreateForMonitor=槽4。
    // ② 类 IID（== 默认接口 IGraphicsCaptureItem 的 IID）—— 作为 CreateForMonitor 的 **riid** 传出去。
    //    ⚠ 不能用 typeof(GraphicsCaptureItem).GUID 顶替：投影类挂的不是类 IID（实测值见下面 itemGuid
    //    那段的注释），拿它当 riid 会得到 E_NOINTERFACE，症状与①用错完全同款。
    // 此前 ① 的位置上填的是 ②：症状不是崩溃而是**恒 E_NOINTERFACE**，两条 CreateForMonitor 路径
    // 在所有机器上静默失效，捕获一直由 TryCreateFromDisplayId 兜着 —— 只盯"能不能截图"看不出来。
    internal const string WgcInteropIid = "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356";
    internal const string WgcItemClassIid = "79C3F95B-31F7-4EC2-A464-632EF5D30760";

    [ComImport, Guid(WgcInteropIid)]
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
        // ═══ D3D11 设备锁：ImmediateContext 非线程安全，所有 GPU 操作必须加锁 ═══
        // 同一 WgcCaptureService 实例的所有 PooledSession 共享此锁（同一 D3D11 设备）
        private readonly object _gpuLock;

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
        private long _frameTimestamp; // 始终在 _frameLock 内访问，锁提供内存屏障
        private readonly object _frameLock = new();
        private readonly ManualResetEventSlim _firstFrameEvent = new(false);
        private volatile bool _hasFrame;
        private volatile bool _disposed; // ═══ 防止 Dispose 后 FrameArrived 回调访问已释放资源 ═══

        // ═══ 在途回调跟踪：Dispose 时等待正在执行的 FrameArrived 回调完成，避免释放竞态 ═══
        private int _inFlightCallbacks;
        private readonly ManualResetEventSlim _callbacksDrained = new(true); // 无在途回调时为 set

        // Staging 纹理复用
        private ID3D11Texture2D? _stagingTex;
        private int _stagingW, _stagingH;

        // ═══ GPU 纹理缓存：最新帧的 GPU 拷贝，用于直通编码路径（跳过 CPU 回读）═══
        private ID3D11Texture2D? _latestTexture;
        private int _latestTexW, _latestTexH;
        private bool _latestTexValid;

        /// <summary>本会话的捕获降级警告 (null = 正常)。设置于 Start() 时读取静态标记。</summary>
        private string? _degradationWarning;
        public string? DegradationWarning => _degradationWarning;

        public int Width => _width;
        public int Height => _height;
        public bool HasFrame => _hasFrame;
        public bool IsHdr => _format == DirectXPixelFormat.R16G16B16A16Float;

        public PooledSession(nint hmonitor, DirectXPixelFormat format, ID3D11Device d3dDevice, object gpuLock)
        {
            _hmonitor = hmonitor;
            _format = format;
            _d3dDevice = d3dDevice;
            _gpuLock = gpuLock;
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
                // 提级到 Info：这是"会话建不起来"唯一能区分【item 建不出】与【后续步骤抛异常】的读数，
                // 留在 Debug 档等于把根因藏进用户默认不落盘的那一层。
                if (_item is null) { LogInfo($"[Pool] {_hmonitor:X}: CreateItem 失败（四条创建路径全部未产出 item）"); return false; }
                _width = _item.Size.Width;
                _height = _item.Size.Height;

                // ═══ 降级检测: 如果 CreateItem 用了方法3 回退 (桌面窗口=仅壁纸)，记录警告 ═══
                // 供调用方 (CaptureMonitorInternal/CaptureAllMonitorsInternal) 传播到 CaptureResult，
                // 让 UI 向用户提示"截图仅包含桌面背景，不含应用窗口"。
                _degradationWarning = s_lastItemDegradation;

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
                LogInfo($"会话启动: 0x{_hmonitor:X} {_width}x{_height} {(IsHdr ? "HDR" : "SDR")} {sw.ElapsedMilliseconds}ms");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 启动失败: {ex.Message}");
                LogInfo($"会话启动失败: 0x{_hmonitor:X} {ex.Message}");
                return false;
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
        {
            // 原子增：进入回调前增加计数
            // 如果在 Disposed 后仍收到回调，计数器会暂时增加但随后检查 _disposed 退出
            int count = Interlocked.Increment(ref _inFlightCallbacks);
            if (count == 1) TryResetEvent(_callbacksDrained); // 从 0->1，重置事件

            if (_disposed)
            {
                // 已在 Dispose 中，减计数并返回
                if (Interlocked.Decrement(ref _inFlightCallbacks) == 0)
                    TrySetEvent(_callbacksDrained);
                return;
            }

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
                    // ═══ 2026-09-16 根因修复：取帧失败时**保留上一帧**，不要发布 null ═══
                    // ReadFloatPixelsPooled 在 GPU 锁争用超时（500ms）时返回 null。
                    // 原实现无条件 `_latestHdr = pixels` —— 把上一帧也清成 null，
                    // 于是"一次瞬时争用"被放大成"这个会话没有帧"，
                    // 上层（首帧等待 / GetLatestHdr）随即把它当成结构性失败。
                    // 保留上一帧：宁可用稍旧的画面，也好过整块缺失。
                    if (pixels is null)
                    {
                        Log($"[Pool] {_hmonitor:X}: 本帧读取失败(GPU 锁争用)，保留上一帧");
                        return;
                    }
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
                    // 同理（见上方 HDR 分支）：取帧失败时保留上一帧，不把 null 发布出去
                    if (pixels is null)
                    {
                        Log($"[Pool] {_hmonitor:X}: 本帧读取失败(GPU 锁争用)，保留上一帧");
                        return;
                    }
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
                    LogInfo($"首帧就绪: 0x{_hmonitor:X} {w}x{h} {(IsHdr ? "HDR" : "SDR")}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Pool] FrameArrived 异常: {ex.Message}");
            }
            finally
            {
                // 回调结束：减少计数；若归零则唤醒等待 Drain 的 Dispose
                if (Interlocked.Decrement(ref _inFlightCallbacks) == 0)
                    TrySetEvent(_callbacksDrained);
            }
        }

        /// <summary>安全重置事件（Dispose 后事件已释放时静默忽略，防止 ObjectDisposedException）。</summary>
        private static void TryResetEvent(ManualResetEventSlim ev)
        {
            try { ev.Reset(); } catch (ObjectDisposedException) { }
        }

        /// <summary>安全设置事件（Dispose 后事件已释放时静默忽略，防止 ObjectDisposedException）。</summary>
        private static void TrySetEvent(ManualResetEventSlim ev)
        {
            try { ev.Set(); } catch (ObjectDisposedException) { }
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
        /// 返回的纹理已增加引用计数（对应一次 Release），调用方用完后需 Dispose。
        /// 返回 null 表示无帧或 GPU 拷贝失败。</summary>
        public ID3D11Texture2D? GetLatestTexture()
        {
            lock (_frameLock)
            {
                if (!_latestTexValid || _latestTexture is null) return null;
                // ═══ 2026-09-15 P0-C 修复：不得返回 _latestTexture 的共享引用 ═══
                // 之前的 QueryInterface 返回的是缓存纹理本身，下一帧 CacheTextureGpuCopy 的
                // CopyResource 会在调用方仍持有引用时原地覆盖其内容，导致编码期间画面被撕裂。
                // 改为在 GPU 锁内 CopyResource 到一块独立快照纹理后返回该拷贝，
                // 调用方 Dispose 时释放快照（与原引用计数语义一致：返回即转移所有权）。
                try
                {
                    lock (_gpuLock)
                    {
                        var desc = _latestTexture.Description;
                        var snapshot = _d3dDevice.CreateTexture2D(new Texture2DDescription
                        {
                            Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                            Format = desc.Format,
                            SampleDescription = new(1, 0),
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.None
                        });
                        try
                        {
                            _d3dDevice.ImmediateContext.CopyResource(snapshot, _latestTexture);
                            return snapshot;
                        }
                        catch
                        {
                            snapshot.Dispose(); // 拷贝失败时释放快照，避免泄漏
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Pool] {_hmonitor:X}: GetLatestTexture 快照失败: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>在锁内缓存 GPU 纹理拷贝（从 WGC 帧纹理拷贝到本地的 Default 纹理）。</summary>
        private void CacheTextureGpuCopy(ID3D11Texture2D wgcTexture, int w, int h)
        {
            try
            {
                // ═══ D3D11 操作必须在 GPU 锁内 ═══
                lock (_gpuLock)
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
                var ctx = _d3dDevice.ImmediateContext;
                ctx.CopyResource(_latestTexture, wgcTexture);
                _latestTexValid = true;
                }
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: CacheTextureGpuCopy 失败: {ex.Message}");
                _latestTexValid = false;
            }
        }

        // ── Staging 纹理复用 + 像素缓冲区复用的像素读取 ──

        private byte[]? ReadBytePixelsPooled(ID3D11Texture2D texture, int w, int h)
        {
            int len = w * h * 4;
            if (_writeBufferSdr is null || _writeBufferSdr.Length != len)
                _writeBufferSdr = new byte[len];
            var pixels = _writeBufferSdr;

            // ═══ D3D11 线程安全：ImmediateContext 非线程安全，所有 GPU 操作必须加锁 ═══
            // TryEnter 超时保护：设备异常/被外部 Dispose 时 GPU 操作挂起，绝不无限等待
            // （否则 WGC 回调线程卡死 → 后续截图拿不到帧 → 用户感知=卡死）
            if (!Monitor.TryEnter(_gpuLock, 500))
            {
                Log($"[Pool] {_hmonitor:X}: GPU 锁超时 (500ms)，跳过本帧 SDR 读取");
                LogInfo($"GPU 锁超时 (500ms)，跳过 SDR 帧");
                return null;
            }
            try
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
            finally
            {
                Monitor.Exit(_gpuLock);
            }

            // Alpha 修复（WGC 输出 alpha 可能为 0）— 纯 CPU 操作，无需锁
            TrueToneCap.Core.PixelOps.FixAlphaChannel(pixels);
            return pixels;
        }

        private float[]? ReadFloatPixelsPooled(ID3D11Texture2D texture, int w, int h)
        {
            int pixelCount = w * h * 4;

            // P2: 复用像素缓冲区（避免 60fps 下每帧分配 132MB）
            // 修复竞态：使用独立写入缓冲区，不与消费者共享的 _latestHdr 冲突
            if (_writeBufferHdr is null || _writeBufferHdr.Length != pixelCount)
                _writeBufferHdr = new float[pixelCount];
            var pixels = _writeBufferHdr;

            // ═══ D3D11 线程安全：ImmediateContext 非线程安全，所有 GPU 操作必须加锁 ═══
            // TryEnter 超时保护（同 ReadBytePixelsPooled）
            if (!Monitor.TryEnter(_gpuLock, 500))
            {
                Log($"[Pool] {_hmonitor:X}: GPU 锁超时 (500ms)，跳过本帧 HDR 读取");
                LogInfo($"GPU 锁超时 (500ms)，跳过 HDR 帧");
                return null;
            }
            try
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
            finally
            {
                Monitor.Exit(_gpuLock);
            }

            return pixels;
        }

        public void Dispose()
        {
            // ═══ 1. 先取消订阅，防止新回调进入 ═══
            if (_framePool is not null)
                try { _framePool.FrameArrived -= OnFrameArrived; } catch { }

            // ═══ 2. 标记已释放，阻止后续回调处理 ═══
            _disposed = true;

            // ═══ 3. 等待正在执行的回调完成（最多 2 秒）═══
            if (!_callbacksDrained.Wait(2000))
            {
                Log($"[Pool] {_hmonitor:X}: Dispose 等待回调超时，强制继续");
            }

            // ═══ 4. 释放资源 ═══
            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }
            try { _winrtDevice?.Dispose(); } catch { }
            // ═══ D3D11 资源释放也必须在 GPU 锁内 ═══
            lock (_gpuLock)
            {
                try { _stagingTex?.Dispose(); } catch { }
                try { _latestTexture?.Dispose(); } catch { }
            }
            _firstFrameEvent.Dispose();
            _callbacksDrained.Dispose();
        }
    }

    // ═══════════════════════════════════════
    //  设备管理
    // ═══════════════════════════════════════

    /// <summary>
    /// 释放当前 D3D11 设备，并通知编码层丢弃其缓存引用。
    ///
    /// 2026-09-15 修复（P1-8 悬垂设备）：此前只 Dispose 而不通知，
    /// 导致 NvencAvifBackend.s_cachedD3DDevice 仍指向已释放的 COM 对象；
    /// 下次 AVIF 编码会拿它走 NVENC 纹理路径 → 原生访问违规。
    /// 而 P/Invoke 层的崩溃在 .NET 上【不可捕获】（见 NativeEncoderGuard 说明），
    /// 后果是整个进程被杀死，而不是回退到 libaom。
    /// </summary>
    private void DisposeDeviceAndNotify()
    {
        lock (_d3dContextLock) { _d3dDevice?.Dispose(); }
        _d3dDevice = null;
        NvencAvifBackend.NotifySharedDeviceDisposed();
    }

    /// <summary>获取或创建 D3D11 设备。线程安全（内部加锁）。
    /// ⚠️ 不要拿它给"另一扇窗口"取设备：传进来的 hmonitor 与截图所用显示器不一致时，
    /// 它会 Dispose 正在被 WGC 会话使用的设备 ⇒ GPU 操作挂起、UI 线程等锁卡死。
    /// （覆盖层那扇下层窗就是这么死的，2026-10-01 报告 §39 已整条删除。）</summary>
    public ID3D11Device GetOrCreateDevice(nint hmonitor)
    {
        // ═══ 2026-09-16 根因修复：设备不应因显示器切换而销毁 ═══
        // 原实现是 `_d3dDevice is not null && _deviceMonitor == hmonitor` ——
        // 只要换了显示器就 DisposeDeviceAndNotify() 重建。
        // 而多显示器捕获会为每个显示器各建一个会话（Parallel.ForEach 遍历所有显示器），
        // 于是 N 个显示器里必然有 N-1 个会话拿到的是**已被释放**的设备，
        // 这些会话永远出不了帧 —— 表现为首帧超时 / GetLatestSdr() 恒为 null。
        // 更关键的是：下面创建设备用的是 D3D11CreateDevice(DriverType.Hardware)，
        // **并没有使用按 hmonitor 找到的那个 adapter** —— 所谓"按显示器匹配适配器"是空转，
        // 每次重建拿到的其实还是同一个默认适配器设备，属于纯破坏性重建。
        // 现改为：只要设备健康就复用（与 hmonitor 无关），仅在设备确实失效时才重建。
        if (_d3dDevice is not null)
        {
            try
            {
                _ = _d3dDevice.ImmediateContext;
                _deviceMonitor = hmonitor;   // 仅作诊断记录，不再作为销毁依据
                return _d3dDevice;
            }
            catch
            {
                DisposeDeviceAndNotify();
            }
        }

        DisposeDeviceAndNotify();
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
            LogInfo($"[WGC] 适配器匹配异常，改走默认适配器: {ex.Message}");
        }

        _d3dDevice = D3D11.D3D11CreateDevice(
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        LogInfo("[WGC] D3D11 设备创建于默认适配器（未按显示器匹配到适配器）");
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
            var session = new PooledSession(hmonitor, DirectXPixelFormat.B8G8R8A8UIntNormalized, device, _d3dContextLock);
            if (!session.Start())
                throw new InvalidOperationException($"无法为显示器 0x{hmonitor:X} 创建 WGC 会话");

            _sessionPool[key] = session;
            return session;
        }
    }

    /// <summary>
    /// 该显示器在**能力上**能否给出 &gt;8-bit 的扩展范围内容（源精度的唯一判据）。
    /// <para>2026-09-19（用户修正 §4.3）：源精度不再由"用户这次要不要 HDR/广色域输出"决定 ——
    /// 只要 Windows 给得出高精度内容就用，给不出才退回 8-bit BGRA；即使最终输出 8-bit，
    /// 内部走浮点链也有意义（色域矩阵/色调映射/gamma 都在浮点里做完，最后量化一次，
    /// 而不是先量化到 8-bit 再在这些步骤里来回舍入）。</para>
    /// <para>2026-09-19（收尾）：判据本体搬到 Core ——
    /// <see cref="DisplayEnumerator.CanDeliverExtendedRange(DisplayInfo, bool)"/>，
    /// 因为多屏拼接选 HDR 源那条路径也必须用同一份判据（原来它自己写了一份 IsHdr‖SupportsHdr，
    /// 两处不同源 ⇒ 同一块屏单屏能拿到 float、多屏拿不到）。
    /// 同一时间把"ACM 是系统级单值"这个洞补上：ACM 这一条改为**逐显示器**的
    /// DISPLAYCONFIG 高级颜色能力查询，全局 AcmeDetected 只在系统对这块屏没有答复时才兜底。
    /// 不这么做的后果：双屏（一块 10-bit/ACM、一块 8-bit）时 8-bit 副屏也被放行 ⇒ 白建一个 Float16 会话、
    /// 拿到的是被钳到 SDR 范围的内容，下游还会按"有 float 像素"声明高位深 ⇒ 假高精度文件。</para>
    /// <para>枚举不到该显示器、或能力查询本身抛异常时**返回 true（宽松）**：宁可多试一次 Float16 会话
    /// （失败也只是回退 8-bit，且不会被写入能力缓存），也不要因为一次枚举意外而永久放弃高精度源。</para>
    /// </summary>
    /// <para>分类为 <c>Unknown</c>（枚举不到该显示器、或能力查询本身抛异常）时仍算"给得出"：宁可多试一次
    /// Float16 会话（失败也只是回退 8-bit，且不会被写入能力缓存），也不要因为一次枚举意外而永久放弃高精度源。
    /// 注意这只放宽【源精度】；【输出声明】侧走 <c>CanClaimWideGamut/CanClaimHdr</c>，Unknown 在那边不授权声称。</para>
    /// </summary>
    private static DisplayEnumerator.DisplayColorClass ClassifyMonitorColor(nint hmonitor)
    {
        try
        {
            var d = DisplayEnumerator.FindDisplayByMonitor(hmonitor);
            return DisplayEnumerator.ClassifyDisplayColor(d, SystemWideAcmEnabled);
        }
        catch
        {
            // 查询本身出错 ⇒ 按"未知"处理：与 Core 的 Unknown 同义（放行 Float16、不授权高位深声明）。
            // 绝不能在这里返回 NarrowSdr —— 一次枚举意外会把该屏的浮点链永久关掉（静默降级）。
            return DisplayEnumerator.DisplayColorClass.Unknown;
        }
    }

    private static bool CanDeliverExtendedRange(nint hmonitor) =>
        ClassifyMonitorColor(hmonitor) != DisplayEnumerator.DisplayColorClass.NarrowSdr;

    /// <summary>该屏的完整判据输入（与 <see cref="ClassifyMonitorColor"/> 同源，只用于日志/探针）。</summary>
    private static string DescribeMonitorColor(nint hmonitor)
    {
        try
        {
            return DisplayEnumerator.DescribeColorEvidence(
                DisplayEnumerator.FindDisplayByMonitor(hmonitor), SystemWideAcmEnabled);
        }
        catch { return $"屏 0x{hmonitor:X} 证据渲染失败"; }
    }

    /// <summary>
    /// 系统级 ACM（高级颜色管理）开关 —— 注册表算出来的**全局单值**，不区分是哪块屏。
    /// 2026-09-19：它已从"逐屏判据"降级为"逐屏查询无答复时的兜底"，只允许在 Core 判据的第三档出现。
    /// 读不到设置时返回 true（宽松）：SelfTest 这类入口跑在 AppServices.Initialize 之前，
    /// 拿设置会抛 ⇒ 与 CanDeliverExtendedRange 的 catch 同向，绝不因为"读不到"而误判成不支持。
    /// </summary>
    private static bool SystemWideAcmEnabled
    {
        get
        {
            try { return AppServices.Settings.Current.AcmeDetected; }
            catch { return true; }
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

        // ═══ 2026-09-19（用户修正 §4.3 双路径策略）：能力层面的负缓存由这里写入 ═══
        // 源精度必须由"Windows 能不能给出 >8-bit 的内容"决定，而不是由用户这次想不想输出 HDR/广色域决定。
        // 于是调用方现在恒请求 Float16 ⇒ 必须有一个"给不出"的判据来避免每次截图都付一次首帧超时
        // （Float16 会话建得起来但 DWM 从不成帧的显示器会走满 FrameTimeoutMs）。
        // 这正是 2026-09-15 那条修复留的口子：s_hdrCapabilityCache 只该由【能力判定】写入，
        // 不能由"本次会话启动失败"这种瞬时状态写入。判据（见 CanDeliverExtendedRange，本体在 Core）：
        //   IsHdr（当前处于 PQ/HLG）或 SupportsHdr（BitsPerColor≥10 且峰值亮度>200，或本身是 HDR 色彩空间）
        //   ⇒ 这两条同时覆盖了 HDR 显示器与"10-bit SDR 面板"；剩下的广色域 SDR 屏由**逐屏**的
        //   DISPLAYCONFIG 高级颜色能力回答（2026-09-19 收尾：不再用系统级 ACM 单值，否则 8-bit 副屏会被误放行）。
        // 注意这条负缓存是"能力"级的：只有判据明确说"给不出"才写，判据查不到时一律放行且不写。
        if (!CanDeliverExtendedRange(hmonitor))
        {
            lock (s_hdrCapabilityCache) s_hdrCapabilityCache[hmonitor] = false;
            // Info 级：这是"为什么这张图只有 8-bit"的唯一对外解释，停在 Debug 就等于静默降级。
            // 带上完整证据行，是因为只写"分类=NarrowSdr"仍要回去猜是驱动没答复、还是 DWM 在 SDR 合成。
            LogInfo($"[WGC] 不建 Float16 会话，源按 8-bit BGRA 取 ⇒ {DescribeMonitorColor(hmonitor)}");
            return null;
        }

        var key = (hmonitor, true);
        lock (_poolLock)
        {
            if (_sessionPool.TryGetValue(key, out var existing) && existing.HasFrame)
                return existing;

            // ═══ 2026-09-19 软冷却判定：位置很讲究，必须在「复用已出帧会话」之后、「新建」之前 ═══
            // 在复用之后 ⇒ 冷却绝不会挡掉一个正在正常出帧的 Float16 会话（那才是我们要的高精度源）；
            // 在新建之前 ⇒ 才有意义：不再为"建得起来却从不成帧"的屏每次截图都付满一次 FrameTimeoutMs。
            // 这里不 Dispose 那条"没帧的旧会话"：留着它，DWM 万一随后补上首帧，下一次就走上面的复用路径；
            // 真一直不出帧则由 ScheduleIdleStop 的闲置回收统一收走。
            if (HdrStalledAndShouldSkip(hmonitor))
            {
                Log($"[WGC] 显示器 0x{hmonitor:X}: Float16 软冷却中（连续未成帧），本次不新建 Float16 会话，用 8-bit BGRA");
                return null;
            }

            if (existing is not null)
            {
                existing.Dispose();
                _sessionPool.Remove(key);
            }

            var device = GetOrCreateDevice(hmonitor);
            var session = new PooledSession(hmonitor, DirectXPixelFormat.R16G16B16A16Float, device, _d3dContextLock);
            if (!session.Start())
            {
                // ═══ 2026-09-15 修复：不要把"本次启动失败"当成"该显示器不支持 HDR" ═══
                // 原实现在此写入 s_hdrCapabilityCache[hmonitor] = false，而该缓存
                // 【进程内没有任何重置路径】⇒ 一次**瞬时**失败（D3D 设备忙、分辨率正在切换、
                // 系统显存紧张、刚从休眠恢复）就会让该显示器永久走 SDR 路径：
                // HDR 与广色域全部静默降级，用户零感知，只能重启程序才能恢复。
                //
                // s_hdrCapabilityCache 的语义应是"能力"（capability），
                // 只该由能力层面的判定写入（如 IsDisplayHdr 为假）；
                // 会话启动失败属**瞬时状态**，不应污染能力缓存 —— 下次截图仍应重试。
                //
                // 2026-09-19：但完全不管它也不行 —— 一直启动失败的屏同样在反复浪费会话创建开销。
                // 于是记进上面的【软冷却】计数器（有额度、会自己恢复、配置一变就作废），
                // 而不是记进能力缓存：这就是"瞬时状态"与"能力"分家的落点。
                Log($"[WGC] Float16 会话启动失败（多为瞬时原因，不记为能力不支持）: 0x{hmonitor:X}");
                session.Dispose();   // 原实现未释放：Start 失败的会话会泄漏
                NoteHdrAttemptOutcome(hmonitor, false);
                return null;
            }

            _sessionPool[key] = session;
            return session;
        }
    }

    /// <summary>
    /// 2026-09-19：本次是否应跳过「新建 Float16 会话」（消耗一次软冷却额度）。
    /// 与能力位无关，见 s_hdrEmptyStreak 字段处的说明。
    /// </summary>
    private static bool HdrStalledAndShouldSkip(nint hmonitor)
    {
        lock (s_hdrStallLock)
        {
            if (!s_hdrSkipRemaining.TryGetValue(hmonitor, out int remaining) || remaining <= 0)
                return false;
            s_hdrSkipRemaining[hmonitor] = remaining - 1;
            return true;
        }
    }

    /// <summary>
    /// 2026-09-19：登记一次 Float16 尝试的结局，驱动上面的软冷却。
    /// <para>口径是「这次 DWM 到底成没成帧」（会话的 HasFrame），不是「最后那批 float 像素留没留下」：
    /// 只有没成帧才会付满一次 FrameTimeoutMs，那正是冷却要治的东西；"出帧了但尺寸不合被丢弃"的那种
    /// 下一次走的是复用快路径（零等待），记成空帧会让计数器永远清不掉。</para>
    /// <para>成帧 ⇒ 连击与冷却额度一并清零（这块屏没事，立刻回到"每次都试"的正常行为）。</para>
    /// <para>未成帧 ⇒ 连击 +1，攒够 <see cref="HdrEmptyStreakToCoolDown"/> 次就上
    /// <see cref="HdrSkipAttempts"/> 次冷却额度。**故意不清连击**：冷却期满后只需再付一次探测
    /// （而不是重攒 3 次），稳态就是"每 11 次截图付 1 次首帧超时"，而不是一直接近每次都付。</para>
    /// <para>异常路径不记账（异常多为瞬时且另有日志），以免把一次偶发异常攒成"看起来像结构性问题"的连击。</para>
    /// </summary>
    private static void NoteHdrAttemptOutcome(nint hmonitor, bool gotFrame)
    {
        lock (s_hdrStallLock)
        {
            if (gotFrame)
            {
                // 两次 Remove 都要执行 ⇒ 先各自存进局部变量再判（写成 a || b 会因为短路只清掉第一个）
                bool hadStreak = s_hdrEmptyStreak.Remove(hmonitor);
                bool hadSkipBudget = s_hdrSkipRemaining.Remove(hmonitor);
                if (hadStreak || hadSkipBudget)
                    Log($"[WGC] 显示器 0x{hmonitor:X}: Float16 重新出帧，软冷却清零");
                return;
            }

            int streak = (s_hdrEmptyStreak.TryGetValue(hmonitor, out int v) ? v : 0) + 1;
            s_hdrEmptyStreak[hmonitor] = streak;
            if (streak >= HdrEmptyStreakToCoolDown)
            {
                s_hdrSkipRemaining[hmonitor] = HdrSkipAttempts;
                LogInfo($"显示器 0x{hmonitor:X}: Float16 连续 {streak} 次未成帧 ⇒ 接下来 {HdrSkipAttempts} 次截图跳过新建 Float16 会话" +
                        "（软冷却，非能力判定；期间只要有一次成帧就立即解除）");
            }
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
        // 2026-09-19：软冷却的账也一起作废。显示器配置变了（插拔/切分辨率/休眠恢复）之后，
        // "这块屏之前一直没成帧"这件事已经没有参考价值 —— 让它重新试，而不是带着旧账继续跳过 Float16。
        lock (s_hdrStallLock) { s_hdrEmptyStreak.Clear(); s_hdrSkipRemaining.Clear(); }
        // 显示器配置变更后设备通常会被 GetOrCreateDevice 重建（那里也会通知）。
        // 此处再通知一次属【保守冗余】：万一调用方只失效会话而不重建设备，
        // 也不会残留一个已失效的缓存引用。代价至多是多创建一次 D3D11 设备，
        // 换来的是不会拿着失效设备去跑 NVENC（该崩溃在 .NET 上不可捕获）。
        NvencAvifBackend.NotifySharedDeviceDisposed();
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
        try
        {
            // ═══ 2026-09-19：捕获体搬到线程池，不再阻塞 UI 线程 ═══
            // CaptureMonitorInternal 是【同步】实现，里面的 WaitForFirstFrame(FrameTimeoutMs=3000)
            // 是硬阻塞的 _firstFrameEvent.Wait() ⇒ 原先调用方（async void 的 UI 事件处理器）
            // 会在 UI 线程上等最多 3 秒：SDR 首帧等待本来就存在这个问题，而"源精度改由显示器能力
            // 决定"之后又多加了一次 Float16 首帧等待 ⇒ 首帧不成形的显示器上界面会明显卡住。
            // 帧池是 CreateFreeThreaded（:180/:1030），帧回调自带线程池线程，不需要创建线程的消息泵。
            return await Task.Run(() => CaptureMonitorInternal(config));
        }
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
        // 该不该建这个会话由 CanDeliverExtendedRange（逐屏 DXGI + 逐屏 DISPLAYCONFIG 高级颜色）决定；
        // 能力不够、或该屏正处于「连续空帧」软冷却中时，GetOrCreateHdrSession 返回 null。
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

                    // ═══ 2026-09-15 P0-A 修复：HDR 会话与 SDR 会话尺寸校验 ═══
                    // 两个会话各自独立创建（CreateItemForMonitor 时机不同，可能取到
                    // 不同 DPI/分辨率快照），若尺寸不一致，下游用 SDR 宽高解释 HDR
                    // 像素会得到错位/越界的画面。不一致时丢弃 HDR 像素，让下游自然走 SDR。
                    if (hdrPixels is not null &&
                        (hdrSession.Width != w || hdrSession.Height != h))
                    {
                        LogInfo($"HDR 会话尺寸不匹配 ({hdrSession.Width}x{hdrSession.Height} != {w}x{h})，已回退 SDR");
                        hdrPixels = null;
                    }

                    // ═══ 2026-09-19：软冷却记账。口径是「这次到底成没成帧」，不是「最后像素留没留下」═══
                    // 只有"没成帧"才会付满一次 FrameTimeoutMs，那正是冷却要治的东西；
                    // 上面那种"出帧了但尺寸不合而被丢弃"的会话下一次走复用快路径（零等待），
                    // 把它记成空帧就会让计数器永远清不掉 —— 判据选 HasFrame 而非 hdrPixels。
                    NoteHdrAttemptOutcome(targetMonitor, hdrSession.HasFrame);
                }
            }
            catch (Exception ex)
            {
                LogInfo($"[WGC] Float16 捕获失败: {ex.Message}（不影响 SDR 结果）");
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
            GpuTexture = gpuTexture,
            DegradationWarning = sdrSession.DegradationWarning // 传播降级警告 (来自会话)
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
                        // ═══ 2026-09-15 P0-B 修复：OneShotCapture 与池化会话共享同一 D3D11 设备 ═══
                        // ImmediateContext 非线程安全，CopyResource/Map/Unmap 必须与池化会话
                        // 共用同一把 _d3dContextLock，否则与 FrameArrived 回调并发时会产生竞态。
                        // Map 失败会导致锁不释放，故整段包在 lock 内（与 PooledSession 读像素写法一致）。
                        byte[] pixels = new byte[w * h * 4];
                        lock (_d3dContextLock)
                        {
                        ctx.CopyResource(staging, texture);
                        var mapped = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
                        } // end lock (_d3dContextLock)
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
        // 同 CaptureMonitorAsync：内部有硬阻塞的首帧等待（SDR 逐屏 + HDR 会话），
        // 调用方是 UI 线程的 async void 处理器 ⇒ 搬到线程池，别把等待压在 UI 线程上。
        try { return await Task.Run(() => CaptureAllMonitorsInternalAsync(config)); }
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

        // 从各显示器的池化会话取帧并拼接（2026-08-10: 按显示器并行，显示器矩形不重叠，
        // 各行写入互不竞争；双 4K 拼接 30-60ms → ~15-25ms）
        var displaysArr = displays.ToArray();

        // ═══ 2026-09-16 新增：多显示器也要能出 HDR ═══
        // 原实现只在 displays.Count == 1 时 PreferHdr，多显示器分支**完全不尝试** HDR 会话，
        // 于是 CaptureResult.HdrPixels 恒为 null ⇒ 多屏用户永远拿不到 HDR 数据，
        // HDR 编码与 HDR 预览全线静默降级为 SDR，且没有任何提示。
        //
        // 策略：以【第一个活跃显示器】为准作为 HDR 源 —— 优先主显示器；
        // 主显示器不具备 HDR 能力时，退选第一个支持 HDR 的显示器。
        // 其余显示器仍走 SDR，但其内容会**上转换**到 scRGB 线性空间后参与 HDR 拼接：
        // 一张图只能有一种色彩空间，不存在"局部 HDR"，因此必须整图统一，
        // 否则同一张图里 HDR 区域与 SDR 区域的亮度基准会不一致。
        //
        // 注意：HDR 会话在并行循环**之前**建立 —— GetOrCreateDevice 无锁，
        // 若放进 Parallel.ForEach 内会与其它显示器的设备创建并发，存在重建/悬垂设备竞态。
        DisplayInfo? hdrSource = SelectHdrSourceDisplay(displaysArr);
        PooledSession? hdrSrcSession = null;
        float[]? hdrSrcPixels = null;
        if (hdrSource is not null)
        {
            try
            {
                var s = GetOrCreateHdrSession(hdrSource.MonitorHandle);
                if (s is not null && (s.HasFrame || s.WaitForFirstFrame(config.FrameTimeoutMs)))
                {
                    hdrSrcPixels = s.GetLatestHdr();
                    if (hdrSrcPixels is not null) hdrSrcSession = s;
                }

                // ═══ 2026-09-19：多屏也要吃「连续空帧」软冷却，口径与单屏一致（看 HasFrame）═══
                // 不记的话：选中的 HDR 源若是那块"建得起来却从不成帧"的屏，每次全桌面截图都要
                // 在这里付满一次 FrameTimeoutMs，而单屏路径早就不会了 —— 两条路径必须同进同退。
                // 放在 try 内且只看 HasFrame：中途抛异常时不记账（异常多为瞬时，见 NoteHdrAttemptOutcome）。
                if (s is not null)
                    NoteHdrAttemptOutcome(hdrSource.MonitorHandle, s.HasFrame);
            }
            catch (Exception ex)
            {
                // 任一步失败都只影响 HDR，SDR 结果不受影响（下面仍会正常拼接）
                // 异常不记进软冷却账（多为瞬时，且已有日志），见 NoteHdrAttemptOutcome 的说明
                Log($"[WGC] HDR 源({hdrSource.Name})建立失败，本次按全 SDR 输出: {ex.Message}");
                hdrSrcPixels = null;
            }
        }
        var fullHdr = hdrSrcPixels is not null ? new float[vw * vh * 4] : null;
        // HDR 拼接完整性计数：任一显示器未能填充，整图就放弃 HDR（理由见下方汇总处）
        int hdrFillFailures = 0;

        // ═══ SDR 内容上转换后的亮度基准对齐 ═══
        // scRGB 约定：1.0 = 80 nits。但系统在 HDR 模式下会把 SDR 内容显示到
        // GetSdrWhiteLevel() nits（常见 160~300，本项目默认按 200）。
        // 若不乘这个系数，SDR 显示器那部分会比 HDR 源区域**明显偏暗** ——
        // 这才是"亮度基准不一致"的真正来源，不能靠"放弃整图 HDR"来回避。
        int sdrWhiteNits = DisplayEnumerator.GetSdrWhiteLevel();
        float sdrScale = sdrWhiteNits > 0 ? sdrWhiteNits / TrueToneCap.Core.PixelOps.NominalSdrReferenceWhiteNits : 1f;
        if (sdrScale != 1f)
            Log($"[WGC] SDR 内容亮度对齐: {sdrWhiteNits} nits → 缩放 ×{sdrScale:F2}");

        Parallel.ForEach(displaysArr, display =>
        {
            var session = GetOrCreateSdrSession(display.MonitorHandle);
            if (!session.HasFrame && !session.WaitForFirstFrame(config.FrameTimeoutMs))
            {
                Log($"[WGC] 显示器 {display.Name} 首帧超时，跳过");
                if (fullHdr is not null) Interlocked.Increment(ref hdrFillFailures);
                return;
            }

            var pixels = session.GetLatestSdr();
            if (pixels is null)
            {
                if (fullHdr is not null) Interlocked.Increment(ref hdrFillFailures);
                return;
            }

            int dx = display.X - vx;
            int dy = display.Y - vy;
            int srcStride = session.Width * 4;
            int copyH = Math.Min(session.Height, display.Height);

            // ═══ 会话捕获的未必是"单个显示器" ═══
            // 实测（双屏 + WGC 降级到桌面窗口模式）：每个会话的 Size 都等于**整个虚拟桌面**，
            // 而不是各自显示器的分辨率。此时若仍从源 (0,0) 整行拷贝，
            // 副显示器那块区域会被"虚拟桌面左边缘"的内容错误覆盖（表现为右屏内容重复左屏）。
            // 判据：会话尺寸 == 虚拟桌面尺寸 ⇒ 源图是全桌面，需从该显示器对应的源区域取。
            bool sessionIsFullDesktop = session.Width == vw && session.Height == vh;
            int srcX = sessionIsFullDesktop ? dx : 0;
            int srcY = sessionIsFullDesktop ? dy : 0;
            int copyW = Math.Min(display.Width, session.Width - srcX);
            if (copyW <= 0) { if (fullHdr is not null) Interlocked.Increment(ref hdrFillFailures); return; }

            for (int row = 0; row < copyH; row++)
            {
                int srcOff = ((srcY + row) * srcStride) + (srcX * 4);
                int dstOff = ((dy + row) * fullStride) + (dx * 4);
                if (srcOff + copyW * 4 > pixels.Length) break;
                Buffer.BlockCopy(pixels, srcOff, fullPixels, dstOff, copyW * 4);
            }

            // ── HDR 拼接（仅在 HDR 源建立成功时进行）──
            // HDR 源自身区域稍后由循环外统一覆盖（避免并发访问同一个 HDR 会话），
            // 这里只把**非源**显示器的 SDR 内容上转换到 scRGB 线性空间后填入。
            if (fullHdr is not null && display.MonitorHandle != hdrSource?.MonitorHandle)
            {
                try
                {
                    var scrgb = TrueToneCap.Core.PixelOps.BgraToScrgbLinearFast(pixels, session.Width, session.Height);
                    // 对齐亮度基准（见 sdrScale 计算处）：只缩放 RGB，alpha 不参与
                    if (sdrScale != 1f)
                    {
                        for (int i = 0; i < scrgb.Length; i++)
                        {
                            if ((i & 3) == 3) continue;   // alpha 通道
                            scrgb[i] *= sdrScale;
                        }
                    }
                    // 与 SDR 同理：会话可能是全桌面，此时源需带显示器偏移
                    CopyFloatRegion(scrgb, session.Width, fullHdr, vw, dx, dy, copyH, srcX, srcY);
                }
                catch (Exception ex)
                {
                    Log($"[WGC] 显示器 {display.Name} SDR→scRGB 上转换失败: {ex.Message}");
                    Interlocked.Increment(ref hdrFillFailures);
                }
            }
        });

        // ═══ HDR 拼接完整性：宁可全 SDR，也不要半张 HDR ═══
        // 若有任一显示器未参与填充（首帧超时 / 取帧失败 / 上转换异常），
        // 它在 fullHdr 中的区域会是 scRGB 零值 = 纯黑，且亮度基准与其它区域不一致。
        // 输出这种图比直接给整张 SDR 更糟，故整体放弃 HDR（SDR 结果始终不受影响）。
        if (fullHdr is not null && hdrFillFailures > 0)
        {
            LogInfo($"[WGC] {hdrFillFailures} 个显示器未参与 HDR 拼接 → 放弃整图 HDR，输出 SDR");
            fullHdr = null;
        }

        // ── HDR 源区域：用真实 HDR 数据覆盖（循环外串行执行）──
        if (fullHdr is not null && hdrSrcPixels is not null && hdrSrcSession is not null && hdrSource is not null)
        {
            try
            {
                int hdx = hdrSource.X - vx;
                int hdy = hdrSource.Y - vy;
                // HDR 源会话同样可能捕获整个虚拟桌面（判据与其它会话一致）
                bool hdrSessionIsFullDesktop = hdrSrcSession.Width == vw && hdrSrcSession.Height == vh;
                CopyFloatRegion(hdrSrcPixels, hdrSrcSession.Width, fullHdr, vw, hdx, hdy,
                    Math.Min(hdrSrcSession.Height, hdrSource.Height),
                    hdrSessionIsFullDesktop ? hdx : 0, hdrSessionIsFullDesktop ? hdy : 0);
                Log($"[WGC] HDR 拼接: 源={hdrSource.Name} {hdrSrcSession.Width}x{hdrSrcSession.Height}");
            }
            catch (Exception ex)
            {
                // 半张 HDR 比全 SDR 更糟（亮度基准不一致），直接放弃整图的 HDR 输出
                LogInfo($"[WGC] HDR 源区域填充失败，放弃 HDR 输出（保留 SDR）: {ex.Message}");
                fullHdr = null;
            }
        }

        sw.Stop();
        Log($"[WGC] CaptureAllMonitors 完成: {vw}x{vh} {sw.ElapsedMilliseconds}ms (HDR={fullHdr is not null})");

        // ═══ 2026-09-19：多屏分支同样要登记闲置回收（原来只有单屏路径有，这里直接 return）═══
        // 不这么做的后果：每屏一条 SDR 会话 + 一条 Float16 会话，连同各自的 staging 纹理、
        // GPU 纹理缓存与 float 写入缓冲会一直常驻（双 4K 约 132MB×2 起步），截完图也永远不释放。
        //
        // 时序约定与单屏【完全一致】，且这里比单屏更安全，不存在"把下游正在读的缓冲提前释放"：
        //   ① ScheduleIdleStop 只是挂一个 15 秒的后台计时器，下一次截图会把它取消重挂；
        //   ② 本次返回的像素全是独立副本 —— fullPixels/fullHdr 是这里新建的拼接画布，
        //      各屏像素经 GetLatestSdr()/GetLatestHdr()（内部 Buffer.BlockCopy 复制）再拷进画布，
        //      会话里的 _latestSdr/_writeBufferHdr/_stagingTex/_latestTexture 没有一张被交给下游；
        //   ③ 本路径不返回 GpuTexture（单屏才有那条直通编码的快照纹理，且它也是 CopyResource 出来的快照），
        //      所以 15 秒后 Dispose 会话释放的显存与下游无关。
        ScheduleIdleStop();

        return new CaptureResult
        {
            SdrPixels = fullPixels,
            HdrPixels = fullHdr,
            Width = vw,
            Height = vh,
            CaptureTimeMs = sw.ElapsedMilliseconds,
            DegradationWarning = s_lastItemDegradation // 传播降级警告 (任一会话回退到壁纸)
        };
    }

    /// <summary>
    /// 为多显示器拼接选择 HDR 源 —— 判据本体在 Core（<see
    /// cref="DisplayEnumerator.SelectHdrSourceDisplay(System.Collections.Generic.IReadOnlyList{DisplayInfo}, bool)"/>），
    /// 这里只注入"全局 ACM 单值"这一个输入。
    /// <para>保留 2026-09-19 那条教训：这里**不许**再自己写一份选择规则。当年两处各写一份的后果是
    /// 同一块屏"单屏能拿 float、多屏拿不到"，用户只是换了个"截取全部显示器"的按钮，精度就悄悄掉了。
    /// 现在两条路径都经 <c>ClassifyDisplayColor</c> 这一个函数，规则要改只改那一处（真值表在 Core 侧）。</para>
    /// <para>2026-09-22 收紧两点（都在 Core）：<c>Unknown</c> 不再够格当源；HDR 档优先于广色域档，
    /// 不再是"主屏给得出就用主屏"。</para>
    /// </summary>
    private static DisplayInfo? SelectHdrSourceDisplay(DisplayInfo[] displays) =>
        DisplayEnumerator.SelectHdrSourceDisplay(displays, SystemWideAcmEnabled);

    /// <summary>
    /// 把一个 float(BGRA) 像素块按行拷贝到目标画布的指定位置。
    /// </summary>
    /// <remarks>越界一律安全截断：多显示器分辨率/DPI 不一致时，某显示器的实际帧尺寸
    /// 未必等于其逻辑尺寸，逐行拷贝必须各自夹紧，否则会写越界（甚至写到画布之外）。</remarks>
    /// <param name="srcX">源图起始 X（会话捕获到整个虚拟桌面时，需传该显示器在桌面中的偏移）。</param>
    /// <param name="srcY">源图起始 Y，同上。</param>
    private static void CopyFloatRegion(float[] src, int srcW, float[] dst, int dstW, int dx, int dy, int copyH,
        int srcX = 0, int srcY = 0)
    {
        int srcStride = srcW * 4;
        int dstStride = dstW * 4;
        if (srcStride <= 0 || dstStride <= 0 || dx < 0 || dy < 0 || srcX < 0 || srcY < 0) return;

        // ═══ 夹紧单行宽度 ═══
        // 只防总长越界是不够的：混合 DPI 多屏下 session.Width（WGC 物理像素）可能大于
        // 该屏在虚拟桌面逻辑坐标中的剩余宽度，行尾会绕写到下一行开头 ⇒ 错行/撕裂。
        // 故每行实际拷贝量取 min(源剩余宽, 目标画布剩余宽)。
        int copyW = Math.Min(srcStride - srcX * 4, dstStride - dx * 4);
        if (copyW <= 0) return;

        int rows = Math.Min(Math.Min(copyH, (dst.Length / dstStride) - dy), (src.Length / srcStride) - srcY);

        for (int row = 0; row < rows; row++)
        {
            int srcOff = ((srcY + row) * srcStride) + (srcX * 4);
            int dstOff = ((dy + row) * dstStride) + (dx * 4);
            if (dstOff + copyW > dst.Length) break;   // 目标行越过画布右边界
            Buffer.BlockCopy(src, srcOff * sizeof(float), dst, dstOff * sizeof(float), copyW * sizeof(float));
        }
    }

    // ═══════════════════════════════════════
    //  静态辅助方法（COM 互操作）
    // ═══════════════════════════════════════

    private static GraphicsCaptureItem? CreateItemForMonitor(nint hmonitor)
    {
        // ═══ 2026-09-15 修复：多显示器捕获恒取主显示器 ═══
        // 原实现把 TryCreateFromDisplayId 放在【方法0】，而它**完全忽略入参 hmonitor**：
        // 恒取 DisplayArea.Primary 的 DisplayId，并用 static 字段永久缓存。
        // 只要它成功（Win11 上几乎必然成功），下面真正按 hmonitor 定位的
        // CreateForMonitor 就永不执行 —— 多显示器用户截副屏得到主屏，
        // "全部显示器拼接"变成 N 份主屏，且全程无日志、无提示、无异常。
        // 现改为：以 hmonitor 为准的 CreateForMonitor 作为首选。
        // DisplayId 反查**没有被删除**，它现在是下面的「回退 1」——这点必须写清：在 interop IID
        // 修好之前，它是唯一真的能产出 item 的路径（方法0/方法2 恒 E_NOINTERFACE），
        // 所以那句"整体移除"既与代码不符，也掩盖了"主路径其实一直是空的"这件事。
        // riid 必须传 **GraphicsCaptureItem 的类 IID**（= 默认接口 IGraphicsCaptureItem 的 IID）。
        // 原先这里写的是 `typeof(GraphicsCaptureItem).GUID` —— 投影类挂的是另一个 GUID
        // （本机实测 e430267b-debd-3a92-b2c7-287da8bef945），不是类 IID，
        // 于是 CreateForMonitor 内部按 riid QI 新对象时返回 E_NOINTERFACE：
        // 症状与"interop 接口 IID 用错"完全一样，只是换了个位置，所以第一次修 IID 后本机仍然是 0x80004002。
        // 实测值打印在单测 --unit-tests 的 ℹ️ 行里；探针取证见 publish/probe-wgc-iid.ps1。
        var itemGuid = new Guid(WgcItemClassIid);

        // 方法0: CsWinRT As<> 扩展（按 hmonitor 精确定位目标显示器）
        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            int hr = interop.CreateForMonitor(hmonitor, itemGuid, out var ptr);
            LogInfo($"[WGC] 方法0(CsWinRT CreateForMonitor) 0x{hmonitor:X}: hr=0x{hr:X8}");
            if (hr >= 0 && ptr != 0) { s_lastItemDegradation = null; return MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); }
        }
        catch (Exception ex) { LogInfo($"[WGC] 方法0: {ex.Message}"); }

        // 方法2: RoGetActivationFactory + vtable
        try
        {
            var interopGuid = new Guid(WgcInteropIid);
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
                            if (hr >= 0 && itemPtr != 0) { s_lastItemDegradation = null; return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr); }
                        }
                        finally { Marshal.Release(interopPtr); }
                    }
                }
                finally { Marshal.Release(facPtr); }
            }
            // 原实现在这里彻底静默：hr<0 或 QI 拿不到 interop 时一行都不留，
            // 于是"方法0 与方法2 双双失败"在日志里看起来像只失败了一步。
            LogInfo($"[WGC] 方法2(vtable) 未产出 item，最后 hr=0x{hr:X8}");
        }
        catch (Exception ex) { LogInfo($"[WGC] 方法2 异常: {ex.Message}"); }

        // ═══ 回退 1（优先）：TryCreateFromDisplayId ═══
        // 放在"桌面窗口"之前是关键：桌面窗口路径**只能拿到壁纸、不含应用窗口**，
        // 但凡有别的办法都应避免。DisplayId 路径由 hmonitor 正确反查（见方法定义处），
        // 既能拿到窗口内容，又不会像 0.3.3 那样串到主显示器。
        // 放在 CreateForMonitor 之后而非之前，是为了不改变原本已经成功的路径。
        try
        {
            var item = CreateItemFromDisplayId(hmonitor);
            if (item is not null)
            {
                s_lastItemDegradation = null;
                return item;
            }
        }
        catch (Exception ex) { LogInfo($"[WGC] DisplayId 回退外层异常: {ex.Message}"); }

        // 方法3: TryCreateFromWindowId（桌面窗口回退）— ⚠️ 仅捕获桌面壁纸/桌面窗口，不含应用窗口
        try
        {
            var item = CreateItemFromDesktopWindow(hmonitor);
            if (item is not null)
            {
                s_lastItemDegradation = "WGC 捕获回退到桌面窗口模式，截图可能仅包含桌面壁纸，不含应用窗口。请检查系统/驱动兼容性。";
                LogInfo("[WGC] ⚠️ 回退到桌面窗口捕获 (方法3) — 仅包含壁纸/桌面背景，不含应用窗口！");
                return item;
            }
        }
        catch (Exception ex) { LogInfo($"[WGC] 方法3 异常: {ex.Message}"); }

        LogInfo("[WGC] ❌ 所有方法均无法创建 GraphicsCaptureItem");
        return null;
    }

    // ═══ 2026-09-16 恢复并修正：CreateItemFromDisplayId ═══
    //
    // 历史与回归：
    //   0.3.3 的该方法**恒用 DisplayArea.Primary 的 DisplayId** 且静态缓存，
    //   导致多显示器场景恒捕获主显示器 —— 因此上一轮把它整个删除。
    //   但删除后出现**严重回归**：某些环境下 CreateForMonitor（CsWinRT / vtable）会失败，
    //   于是直接回退到"桌面窗口"路径 —— **只捕获壁纸，完全不含应用窗口**。
    //
    // 正确做法不是删除，而是**修正反查**：
    //   DisplayId 其实可以可靠反查 —— 取该 hmonitor 的矩形中心点，
    //   问系统 DisplayArea.GetFromPoint "这个点属于哪个显示器"，即可拿到它自己的 DisplayId。
    //   这样多显示器各自拿到自己的 ID，既不串到主屏，又能正常捕获窗口内容。

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    /// <summary>按 hmonitor 反查**它自己**的 DisplayId（不假设是主显示器）。
    /// <para>每条失败都必须留痕：这条链是"回退 1"，它静默失败时用户看到的就是"完全无法捕获"，
    /// 而日志里一个字都没有。三种失效各自的原因不同，必须分开报。</para></summary>
    private static Windows.Graphics.DisplayId? GetDisplayIdForMonitor(nint hmonitor)
    {
        try
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hmonitor, ref mi))
            {
                LogInfo($"[WGC] DisplayId 反查失败: GetMonitorInfo(0x{hmonitor:X}) 返回 false（该 HMONITOR 已失效/显示器刚变更）");
                return null;
            }

            // 用该显示器的中心点询问系统"这个点属于哪个 DisplayArea"
            var center = new Windows.Graphics.PointInt32(
                (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2,
                (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2);

            var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                center, Microsoft.UI.Windowing.DisplayAreaFallback.None);
            if (area is null)
            {
                // Fallback.None ⇒ 点不在任何 DisplayArea 内就是返回 null，不是错误码 ⇒ 原先零痕迹。
                // 混合 DPI / 负坐标 / 复制模式下这一步是会发生的，此处就是"兜底也失败"的第一现场。
                LogInfo($"[WGC] DisplayId 反查失败: 中心点 ({center.X},{center.Y}) 不属于任何 DisplayArea " +
                        $"(rect={mi.rcMonitor.Left},{mi.rcMonitor.Top},{mi.rcMonitor.Right},{mi.rcMonitor.Bottom})");
                return null;
            }
            return new Windows.Graphics.DisplayId(area.DisplayId.Value);
        }
        catch (Exception ex)
        {
            // DisplayArea 属 WinUI3 类型，而捕获体现在跑在线程池线程上 ⇒ 这一步是否有线程前提，
            // 记下来比猜重要：出问题时这是唯一的线索。
            LogInfo($"[WGC] DisplayId 反查抛异常: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static GraphicsCaptureItem? CreateItemFromDisplayId(nint hmonitor)
    {
        try
        {
            var displayId = GetDisplayIdForMonitor(hmonitor);
            if (displayId is null) return null;   // 失败原因已在 GetDisplayIdForMonitor 里留痕
            var item = GraphicsCaptureItem.TryCreateFromDisplayId(displayId.Value);
            if (item is null)
            {
                LogInfo($"[WGC] DisplayId 路径: TryCreateFromDisplayId 返回 null（hmonitor=0x{hmonitor:X} 该目标不可捕获）");
                return null;
            }
            // 提级到 Info 并在同一行带上 hmonitor 与尺寸：在方法0/方法2 长期失效期间，
            // 这条是判断"捕获到底由哪条腿供的源、指向的是不是请求的那块屏"的唯一读数。
            LogInfo($"[WGC] DisplayId 路径: OK hmonitor=0x{hmonitor:X} {item.Size.Width}x{item.Size.Height}");
            return item;
        }
        catch (Exception ex)
        {
            LogInfo($"[WGC] DisplayId 路径异常: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(out MSG msg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG msg);

    private static void PumpMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, 1 /*PM_REMOVE*/) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    private static int VtblCall4(nint pThis, nint hmonitor, Guid riid, out nint result)
    {
        nint vtable = Marshal.ReadIntPtr(pThis);
        nint methodPtr = Marshal.ReadIntPtr(vtable + 4 * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateForMonitorDelegate>(methodPtr);
        return fn(pThis, hmonitor, ref riid, out result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitorDelegate(nint pThis, nint hmonitor, ref Guid riid, out nint result);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint hstring, ref Guid iid, out nint factory);

    private static partial class IntPtrHelper
    {
        [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int WindowsCreateString(string s, int len, out nint hstr);
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

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint outD3D11Device);

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
    }
}
