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

    /// <summary>顺便把这一帧冻结进共享槽、交给预览窗直接采样（路线 B，仅单屏 HDR 有意义）。
    /// <para>必须在【捕获方法内部】取槽：取完像素到返回之间还有 ICC 读取、日志、线程池续体
    /// （本机实测这段期间 WGC 仍在出新帧），在调用方那里取到的必然是更新的一帧 ——
    /// 预览会和最终保存的像素差一帧。这个开关就是让取槽紧跟在取像素之后、同一段里完成。</para></summary>
    public bool WantSharedFrame { get; init; }

    /// <summary>是否顺带把最新帧复制成一块独立纹理交给 <c>CaptureResult.GpuTexture</c>（GPU 直通编码用）。
    /// 选区预览路径不消费这张快照（拿到结果就 Dispose），开着它等于每张截图白付一次整屏 BGRA8
    /// 分配 + CopyResource（4K 约 33MB 显存）。
    /// 默认 true = 现有调用方行为不变：静默截图/立即捕获仍走 GPU 直通编码。</summary>
    public bool WantGpuTexture { get; init; } = true;
}

/// <summary>
/// WGC 统一捕获服务 — 会话池化高性能版。
/// 核心优化：后台持久 Session 持续接收帧，截图时零延迟取最新帧。
/// </summary>
public sealed partial class WgcCaptureService : IDisposable
{
    // ── 并发保护 ──
    private static readonly SemaphoreSlim s_captureLock = new(1, 1);

    // 诊断开关：TTC_SDR_SHARED=0 关掉"SDR 也建共享槽"（真机 A/B 用；默认开）。
    // 关掉即回到 2026-10-30 之前的行为：每次截图整屏回读 SDR（本机实测 21.6–37.1ms）。
    private static readonly bool sSdrSlotEnabled =
        Environment.GetEnvironmentVariable("TTC_SDR_SHARED") != "0";

    // 诊断开关：TTC_NO_FLOAT16=1 把本机当"纯 SDR 屏"用（CanDeliverExtendedRange 恒 false）。
    // 它验的是阶段 3 的"纯 SDR 分支一字不改"：那种屏上 SDR 槽从来不冻，选区像素照旧整帧裁。
    private static readonly bool sNoFloat16 =
        Environment.GetEnvironmentVariable("TTC_NO_FLOAT16") == "1";

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

    // ═══ F5 仪表：_poolLock 每一处持锁的【次数 / 合计 / 峰值】（2026-10-31 批，只测不改）═══
    // 要回答的问题（审查 §3-F5「TryGetLatestFrame 持 _poolLock 整段做回读」）：这把锁到底被持有多久、
    // 是哪一处持有它。原指控给不出读数的原因很具体：持锁段里那次回读本身要等 GPU 锁（最多 500ms）
    // 加一次 33MB 拷贝，而这三件事今天没有任何一处计时。
    // 口径（今天点名的 `_engines` 那类"静态可变跨调用状态"，这里刻意避开）：
    //   · 数组是本**实例**的字段 —— WgcCaptureService 由 DI 建一份，多进程/多实例互不串账；
    //   · 并发截图（s_captureLock 只拦 CaptureMonitorAsync，TryGetLatestFrame/ReleaseSharedFrame 不排队）
    //     会同刻进入不同站点，所以一律 Interlocked：计数/合计用 Add，峰值用 CAS 环，
    //     两线程同时到峰最多一个赢，不会写坏也不会丢总数；
    //   · 单位存【微秒】：4K 一次区域回读常在 1ms 以下，按 ms 记账会整列读成 0。
    // 逐帧/逐次一律零日志，只在诊断档按结算点打一行（见 DumpPoolLockMeters）。
    private static readonly string[] s_poolLockNames =
    [
        "建SDR会话", "建Float16会话", "空闲回收", "会话失效", "探针取帧龄",
        "交还共享槽", "选区回读(Float16)", "选区回读(SDR)", "录制取最新帧",
    ];
    private const int PoolLockSites = 9;
    private const int LockGetSdr = 0, LockGetHdr = 1, LockIdleStop = 2, LockInvalidate = 3,
        LockProbeAge = 4, LockReleaseShared = 5, LockReadHdr = 6, LockReadSdr = 7, LockTryLatest = 8;
    private readonly long[] _mPoolLockN = new long[PoolLockSites];
    private readonly long[] _mPoolLockUs = new long[PoolLockSites];
    private readonly long[] _mPoolLockMaxUs = new long[PoolLockSites];
    private CancellationTokenSource? _idleStopCts;
    private const int IdleStopDelayMs = 15000; // 15 秒无截图则自动停止会话

    /// <summary>结算一次 _poolLock 持有（在锁**外面**调用：先出锁再记账，绝不让仪表把临界区延长）。</summary>
    private void RecordPoolLock(int site, long t0)
        => PerfMeter.Record(_mPoolLockN, _mPoolLockUs, _mPoolLockMaxUs, site, PerfMeter.SinceMicro(t0));

    /// <summary>把 _poolLock 的持锁账目打成一行（诊断档）。空站点不写，读数短。</summary>
    private void DumpPoolLockMeters(string reason)
    {
        if (!PerfMeter.DumpEnabled) return;
        var sb = new System.Text.StringBuilder(160);
        sb.Append($"[Meter/F5] _poolLock 持锁账目（{reason}）:");
        bool any = false;
        for (int i = 0; i < PoolLockSites; i++)
        {
            long n = Interlocked.Read(ref _mPoolLockN[i]);
            if (n == 0) continue;
            any = true;
            sb.Append($" {s_poolLockNames[i]} n={n} 合计{PerfMeter.Ms(Interlocked.Read(ref _mPoolLockUs[i]))}"
                + $" 峰值{PerfMeter.Ms(Interlocked.Read(ref _mPoolLockMaxUs[i]))}");
        }
        Log(any ? sb.ToString() : sb.Append(" 一次都没进过锁").ToString());
    }

    /// <summary>对外结算入口（探针/CLI 需要"现在就报"时调；本批不接 Program.cs 的参数）。</summary>
    internal void DumpPerfMetersOnDemand(string reason)
    {
        DumpPoolLockMeters(reason);
        lock (_poolLock)
        {
            if (_sessionPool.Count == 0) { Log($"[Meter/F5] 当前在场会话: 0 条（{reason}）"); return; }
            var sb = new System.Text.StringBuilder(200);
            sb.Append($"[Meter/F5] 当前在场会话: {_sessionPool.Count} 条（{reason}，未结算，结算行在会话 Dispose 时打）");
            foreach (var (key, s) in _sessionPool)
                sb.Append($" | 0x{key.Monitor:X}{(key.IsHdr ? "/HDR" : "/SDR")} {s.Width}x{s.Height}");
            Log(sb.ToString());
        }
    }

    // ═══ 2026-10-07 性能轮：单帧取帧缓冲复用 ═══
    // 单屏捕获路径（CaptureMonitorInternal）每帧各取一次 SDR/HDR，复用这两块把
    // 「分配+拷贝」变成「只拷贝」：4K 下实测暖机 30 次 **35.05 → 14.53 ms/帧（省 20.5 ms，降 59%）**，
    // 并消掉 158 MB/帧的 LOH 分配。
    // 为什么是实例字段而不是局部变量：CaptureMonitorInternal 每帧返回后调用方就把它拷进
    // 拼接画布/交给编码器，下一帧再进来时上一帧早已消费完 ⇒ 复用安全（契约见 GetLatestSdr/Hdr 备注）。
    // ⚠ 只有单屏路径用它们。多屏拼接路径按显示器并行取帧，共用一块必被互相覆盖，
    //   所以那条路继续走"每次分配"的旧重载 —— 那是正确性，不是遗漏。
    private byte[]? _reuseSdr;
    private float[]? _reuseHdr;

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

    /// <summary>四条创建路径全部失败时，留给上层的那句话（含"是提权还是被挡"的判别）。
    /// <para>与 <see cref="s_lastItemDegradation"/> 分开：那个是"成功了但降级"，
    /// 这个是"根本没成功"，两种事实不该共用一枚字段 —— 共用就会让"降级提示"在
    /// 失败路径上被覆写成空，用户两头都收不到话。</para></summary>
    private static string? s_lastItemFailureMessage;

    /// <summary>取走最近一次 item 创建失败的说明（读后即清，避免泄漏到下一张图）。</summary>
    internal static string? TakeItemFailureMessage()
    {
        var m = s_lastItemFailureMessage;
        s_lastItemFailureMessage = null;
        return m;
    }

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

        // ═══ 2026-10-29（P0-5）：逐帧 CPU 回读改为"消费者活跃时才做" ═══
        // 实测（本机 4K HDR 屏，静态桌面）：保活窗口内不截图也持续出帧（≈1.2 fps），
        // 每帧整屏回读 54.5ms ⇒ 会话保活 15s 期间占 **43.4% 单核**（会话停掉后基线 0%）。
        // 帧率高的时候（视频/游戏）代价按比例放大。
        // 现在：帧回调只更新 GPU 快照（本来每帧也要拷给直通编码），CPU 回读仅在
        //   ① 最近 ConsumerWindowMs 内有消费者（连续截图走快路径，零额外延迟），或
        //   ② 消费者来取帧时按需从快照补读一次（冷启动晚 ~55ms，但只发生一次）。
        // ⚠ 刻意不采用"请求位 + 等下一帧"：静态桌面实测只有 ~1 fps，等帧会退化成等超时；
        //   按需回读读的是已缓存的快照，桌面不变时它就是当前画面。
        private long _cpuSdrTick, _cpuHdrTick;   // 对应 CPU 帧来自哪一帧（_frameTimestamp），_frameLock 内访问
        private long _consumerUntil;             // 消费者窗口截止时刻（TickCount64）
        private const long ConsumerWindowMs = 2000;

        // ═══ 2026-10-30 SDR(8-bit) 腿仪表 —— 只读数，不参与任何判定 ═══
        // 为什么要它：下一步要决定「要不要给 SDR 也建共享槽 / 常驻一块 CPU 帧」，而这个决定取决于
        // 消费者窗口内**逐帧回读到底发生了几次、一共吃掉多少 CPU 时间**。逐帧打日志不行（静态桌面
        // 也有 1~2 fps，窗口内几十行会把判据淹掉），所以先原子累加、窗口到期后结算一行。
        // 为什么是 Interlocked：WGC FreeThreaded 帧池的回调可能并发进入，普通 += 会丢计数。
        // 两枚字段各自原子，交界处最多漏算一帧的毫秒 —— 对"要不要建槽"这个量级无影响。
        private long _sdrWindowFrames;   // 本窗口内成功回读的帧数
        private long _sdrWindowTicks;    // 对应回读工作（不含 GPU 锁等待）的 Stopwatch tick 累计
        private readonly ManualResetEventSlim _firstFrameEvent = new(false);
        private volatile bool _hasFrame;
        private volatile bool _disposed; // ═══ 防止 Dispose 后 FrameArrived 回调访问已释放资源 ═══

        // ═══ 在途回调跟踪：Dispose 时等待正在执行的 FrameArrived 回调完成，避免释放竞态 ═══
        private int _inFlightCallbacks;
        private readonly ManualResetEventSlim _callbacksDrained = new(true); // 无在途回调时为 set

        // Staging 纹理复用
        private ID3D11Texture2D? _stagingTex;
        private int _stagingW, _stagingH;

        // ═══ F5 仪表（2026-10-31 批：先补仪表，不改任何行为）═══
        // 要回答的问题（审查 §3-F5 / §9 那两行"仍未量"）：一条会话活多久，其间 staging 纹理被
        // 【重建】了几次、其中几次是"尺寸切换"造成的（整屏 ↔ 选区来回切就是那条指控的形状），
        // 以及这一趟 CreateTexture2D 到底贵不贵（4K 8-bit 单块实测 42.2MB，尺寸即体积）。
        // 口径：这是**每条会话自己的实例字段**，不是进程级静态 —— 会话由
        // (HMONITOR, IsHdr) 唯一确定，多屏并发时各数各的，不会互相认领计数；
        // 帧回调（FreeThreaded 池）与消费者线程可能并发进入同一条会话 ⇒ 一律 Interlocked。
        // 落盘点 = Dispose 一行（会话一条），逐帧零日志。
        private long _mStagingAsk;          // 问过几次要 staging（含命中复用）
        private long _mStagingCreate;       // 真的新建了几块
        private long _mStagingResize;       // 其中"尺寸与上一块不同"的几次（拆掉重建）
        private long _mStagingCreateUs;     // CreateTexture2D 本体耗时合计（微秒）
        private long _mStagingCreateMaxUs;  // 峰值
        private long _mStagingBytes;        // 累计向驱动申请的 staging 字节数

        // ═══ GPU 纹理缓存：最新帧的 GPU 拷贝，用于直通编码路径（跳过 CPU 回读）═══
        private ID3D11Texture2D? _latestTexture;
        private int _latestTexW, _latestTexH;
        private bool _latestTexValid;

        // ═══ 2026-10-30 路线 B：跨设备共享槽（给【另一个 D3D 设备】上的预览窗直接采样）═══
        // 与 _latestTexture 分开的一块：_latestTexture 每帧被覆写（直通编码要最新帧），
        // 而预览窗一拿就是几十秒（用户框选期间）⇒ 必须有一块"被取走就不再动"的。
        // 冻结不是优化，是截图语义本身：用户按下热键要的是【那一刻】的画面，不是活的视频。
        // 句柄在纹理创建时一次生成；预览侧 OpenSharedResource 之后自己持引用，
        // 所以即使本会话被 Dispose，预览手里那块仍然有效（DXGI 共享资源的引用计数语义）。
        private ID3D11Texture2D? _sharedTex;
        private nint _sharedHandle;
        private int _sharedW, _sharedH;
        private bool _sharedValid;   // 槽里有与 _latestTexture 同帧的内容
        private bool _sharedHeld;    // 已被取走：期间不覆写（否则预览画面会在用户眼前被换掉）
        /// <summary>共享槽的失败只报一次，且报在 Info 档 —— 它是路线 B 的结构性故障，
        /// 落在 Debug 档等于"探针只看到取不到槽、日志里什么都没有"（第一次跑就是这么哑的）。</summary>
        private bool _sharedFaultLogged;
        internal bool SharedHeld => _sharedHeld;
        internal bool SharedValid => _sharedValid;

        private void LogSharedFault(string msg)
        {
            if (_sharedFaultLogged) return;
            _sharedFaultLogged = true;
            LogInfo($"[Pool] {_hmonitor:X}: 共享槽故障（只报一次）: {msg}");
        }

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
                long now = Environment.TickCount64;

                // GPU 快照每帧都更新（直通编码与"按需回读"都靠它，成本是一次显卡内拷贝）
                CacheTextureGpuCopy(texture, w, h);
                lock (_frameLock) { _frameTimestamp = now; }

                // CPU 回读只在消费者活跃窗口内做；否则这一帧只留在 GPU 快照里
                bool wantCpu = now < Volatile.Read(ref _consumerUntil);
                if (wantCpu)
                {
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
                            Log($"[Pool] {_hmonitor:X}: 本帧读取失败(GPU 锁争用)，保留上一帧");
                        else
                        {
                            lock (_frameLock)
                            {
                                // 双缓冲交换：旧发布缓冲变为下次写入缓冲，消除竞态
                                _writeBufferHdr = _latestHdr;
                                _latestHdr = pixels;
                                _latestSdr = null;
                                _cpuHdrTick = now;
                            }
                        }
                    }
                    else
                    {
                        var pixels = ReadBytePixelsPooled(texture, w, h, out long sdrWorkTicks);
                        // 同理（见上方 HDR 分支）：取帧失败时保留上一帧，不把 null 发布出去
                        if (pixels is null)
                            Log($"[Pool] {_hmonitor:X}: 本帧读取失败(GPU 锁争用)，保留上一帧");
                        else
                        {
                            // 只累计成功的那次：失败那帧的耗时主要是等锁（另有日志），
                            // 混进来会把"每帧回读成本"抬成锁争用的数字，判据就失真了。
                            Interlocked.Increment(ref _sdrWindowFrames);
                            Interlocked.Add(ref _sdrWindowTicks, sdrWorkTicks);

                            lock (_frameLock)
                            {
                                // 双缓冲交换：旧发布缓冲变为下次写入缓冲，消除竞态
                                _writeBufferSdr = _latestSdr;
                                _latestSdr = pixels;
                                _latestHdr = null;
                                _cpuSdrTick = now;
                            }
                        }
                    }
                }

                if (!_hasFrame)
                {
                    // 首帧判据从"CPU 回读成功"提前到"GPU 快照就绪"：回读已不再每帧发生，
                    // 消费方拿到首帧事件后由 GetLatest* 按需补读。
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
        public byte[]? GetLatestSdr() => GetLatestSdr(null);

        /// <summary>获取最新 HDR 帧（安全拷贝，调用方可跨线程使用）。</summary>
        public float[]? GetLatestHdr() => GetLatestHdr(null);

        // ═══════════════════════════════════════════════════════════════════════════════
        //  帧缓冲复用（2026-10-07 性能轮）
        //  ═══════════════════════════════════════════════════════════════════════════════
        //  为什么要有 reuse 形参：这两个方法每调用一次就 new 一个整帧数组再 BlockCopy。
        //  4K 下实测（暖机 30 次取均值）：SDR+HDR 合计 **35.05 → 14.53 ms/帧，省 20.5 ms（降 59%）**。
        //  （早期单点估计写的是"34.8 → 11.9、省 22.8"，那是未暖机的读数；以暖机值为准。）
        //  更要紧的是**分配落在 LOH**（byte[4K]=33 MB、float[4K]=133 MB，都远超 85 KB 门槛），
        //  而 LOH 默认不压缩 ⇒ 碎片随时间累积。C 盘当时只剩 21.6 GB，这条不是纸面问题。
        //
        //  ⚠ 传进来的 reuse 只用于 **SDR 与 HDR 各自的固定尺寸**（会话尺寸在一个会话内不变）。
        //    尺寸不符就**忽略它另分配**，绝不截断 —— 把一帧截短比多分配一次糟得多。
        //  ⚠ 调用方必须保证：拿到之后到下次调用本方法之前，不再读那份缓冲。
        //    ═══ 2026-10-30 更正：这句话当时就是错的，别照它做设计 ═══
        //    选区预览那条腿（MainWindow.StartSelectionCapture 把 HdrPixels 原样交给
        //    HdrCaptureWindow.LoadFrame，后者把它存进 _frame，渲染线程每拍重读，一直读到用户
        //    框选完成）就是【跨帧长期持有】这一份，远不是"取到即消费"。
        //    它今天没出事，靠的不是这里的缓冲纪律，而是 MainWindow 那道防重入闸：
        //    _isCapturing 在 StartSelectionCapture 开头的 Interlocked.CompareExchange 抢下，
        //    在同一个方法末尾的 finally 里放（那里的注释自己写着"预览窗无论成功/取消/失败/提前返回，
        //    都在这里释放"）⇒ 预览期间不会有第二次捕获，于是没有第二次的
        //    GetLatestHdr(_reuseHdr) 来覆写用户正看着的那张。
        //    ⚠ 这条跨模块不变量现在是【没人写下来、也没仪表守着】的：谁把那道闸改成"预览期间也允许
        //    再按一次"，用户屏幕上的画面就会被下一帧静默换掉，而所有现有判据全绿。
        //    （现有仪表各判各的：--capture-freshness-probe 判"截图落后现实多少"、--sdr-semantics-probe
        //    判"两次读数是同一张画面"、--preview-ui-probe 判"像素呈现结果"、--selftest 判"源精度"
        //    —— 没有一条能判"已经交出去的那一块事后有没有被后续帧改写"。）
        //  ⚠ 仍持锁拷贝（_frameLock）：复用只去掉了分配，**没有**去掉生产者/消费者之间的拷贝，
        //    帧回调线程随时会改写 _latestSdr/_latestHdr（见 OnFrameArrived 里 _writeBuffer* = _latest* 那一换）。
        //    省的是分配与 LOH 压力，不是同步。

        /// <summary>获取最新 SDR 帧。<paramref name="reuse"/> 尺寸匹配时复用它（省一次分配）。</summary>
        /// <remarks>空闲期没有逐帧回读时，这里从最近一帧的 GPU 快照补读一次（见 _consumerUntil 注释）。</remarks>
        public byte[]? GetLatestSdr(byte[]? reuse)
        {
            // ═══ 汇总必须结算在刷新 _consumerUntil 之前 ═══
            // 下一行会把这个窗口的截止时刻改成 now+2000；之后再判"窗口到期"永远为假，
            // 累计只增不清、那行汇总一次也打不出来。
            FlushSdrWindowIfExpired();

            Volatile.Write(ref _consumerUntil, Environment.TickCount64 + ConsumerWindowMs);   // 后续帧恢复逐帧回读

            // 计时只包住 CopySdr 那一句；Log 挪到锁外，落盘/推 UI 的时间不该算进 _frameLock 的持锁时间。
            byte[]? reusedCopy = null;
            double reusedMs = 0;
            lock (_frameLock)
            {
                if (_latestSdr is not null && _cpuSdrTick == _frameTimestamp)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    reusedCopy = CopySdr(_latestSdr, reuse);
                    reusedMs = MsSince(t0);
                }
            }
            if (reusedCopy is not null)
            {
                Log($"[Pool] SDR 帧: 复用拷贝 {reusedMs:0.0}ms");
                return reusedCopy;
            }

            // 回读耗时由 ReadbackLatestSdrFresh 在**拿到 GPU 锁之后**起计：500ms 的锁等待
            // 是争用、不是回读成本，混进来会让"要不要建槽"的判断建立在一个假的单价上。
            var fresh = ReadbackLatestSdrFresh(out double readbackMs);
            if (fresh is not null)
            {
                byte[] copied;
                lock (_frameLock)
                {
                    _latestSdr = fresh;
                    _cpuSdrTick = _frameTimestamp;
                    copied = CopySdr(fresh, reuse);
                }
                // 只在这条路**拿到像素**时打：回读失败（锁超时/无快照）会落到下面的兜底，
                // 那里已经有自己的一行；给失败也打一条 0.0ms 的"按需回读"是假读数。
                Log($"[Pool] SDR 帧: 按需回读 {readbackMs:0.0}ms");
                return copied;
            }

            byte[]? fallback = null;
            double fallbackMs = 0;
            lock (_frameLock)
            {
                if (_latestSdr is not null)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    fallback = CopySdr(_latestSdr, reuse);
                    fallbackMs = MsSince(t0);
                }
            }
            // 兜底单独一行：它说明"新鲜回读没拿到、交出去的是旧 CPU 帧"，
            // 混进"复用拷贝"会被读成快路径命中。
            if (fallback is not null) Log($"[Pool] SDR 帧: 兜底拷贝 {fallbackMs:0.0}ms");
            return fallback;
        }

        /// <summary>消费者窗口到期后的第一次取帧：结算该窗口内 SDR 逐帧回读的次数与总耗时。</summary>
        /// <remarks>调用点必须在刷新 <c>_consumerUntil</c> 之前，否则旧截止时刻已被覆盖、永不触发。
        /// 空闲窗口（这 2s 内没人取帧，回调也没回读）直接跳过，不打印 —— 否则每次冷取帧都多一行噪声。</remarks>
        private void FlushSdrWindowIfExpired()
        {
            if (Environment.TickCount64 < Volatile.Read(ref _consumerUntil)) return;

            // 两个计数器分别 exchange：并发回调在交界处最多漏算一帧的毫秒，对判据无影响。
            long frames = Interlocked.Exchange(ref _sdrWindowFrames, 0);
            long ticks = Interlocked.Exchange(ref _sdrWindowTicks, 0);
            if (frames == 0) return;

            double totalMs = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log($"[Pool] SDR 回读窗口: {frames} 帧 共 {totalMs:0.0}ms（窗口 {ConsumerWindowMs}ms）");
        }

        /// <summary>Stopwatch 时间戳差 → 毫秒（不分配）。</summary>
        private static double MsSince(long startTimestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>获取最新 HDR 帧。<paramref name="reuse"/> 尺寸匹配时复用它（省一次分配）。</summary>
        /// <remarks>同 <see cref="GetLatestSdr"/>：空闲期按需从 GPU 快照补读。</remarks>
        public float[]? GetLatestHdr(float[]? reuse)
        {
            Volatile.Write(ref _consumerUntil, Environment.TickCount64 + ConsumerWindowMs);
            lock (_frameLock)
            {
                if (_latestHdr is not null && _cpuHdrTick == _frameTimestamp)
                    return CopyHdr(_latestHdr, reuse);
            }

            var fresh = ReadbackLatestHdrFresh();
            if (fresh is not null)
            {
                lock (_frameLock)
                {
                    _latestHdr = fresh;
                    _cpuHdrTick = _frameTimestamp;
                    return CopyHdr(fresh, reuse);
                }
            }

            lock (_frameLock) return _latestHdr is null ? null : CopyHdr(_latestHdr, reuse);
        }

        private static byte[] CopySdr(byte[] src, byte[]? reuse)
        {
            var copy = (reuse is not null && reuse.Length == src.Length) ? reuse : new byte[src.Length];
            Buffer.BlockCopy(src, 0, copy, 0, copy.Length);
            return copy;
        }

        private static float[] CopyHdr(float[] src, float[]? reuse)
        {
            var copy = (reuse is not null && reuse.Length == src.Length) ? reuse : new float[src.Length];
            Buffer.BlockCopy(src, 0, copy, 0, copy.Length * sizeof(float));
            return copy;
        }

        /// <summary>按需从最近一帧的 GPU 快照回读 SDR 像素。空闲期未逐帧回读时由 GetLatestSdr 调用。</summary>
        /// <param name="workMs">回读本身耗时（毫秒）：从拿到 GPU 锁起算，**不含**锁等待；失败为 0。</param>
        private byte[]? ReadbackLatestSdrFresh(out double workMs)
        {
            workMs = 0;
            byte[] px;
            if (!Monitor.TryEnter(_gpuLock, 500))
            {
                Log($"[Pool] {_hmonitor:X}: 按需回读 GPU 锁超时 (500ms)");
                return null;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (!_latestTexValid || _latestTexture is null || _latestTexW <= 0 || _latestTexH <= 0) return null;
                int w = _latestTexW, h = _latestTexH;
                px = new byte[w * h * 4];
                EnsureStaging(w, h, Vortice.DXGI.Format.B8G8R8A8_UNorm);
                var staging = _stagingTex;
                if (staging is null) return null;
                var ctx = _d3dDevice.ImmediateContext;
                ctx.CopyResource(staging, _latestTexture);
                var mapped = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int dstStride = w * 4;
                    fixed (byte* dst = px)
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy(srcBase + row * srcRowPitch, dst + row * dstStride, dstStride, dstStride);
                }
                ctx.Unmap(staging, 0);
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 按需回读 SDR 失败: {ex.Message}");
                return null;
            }
            finally { Monitor.Exit(_gpuLock); }

            TrueToneCap.Core.PixelOps.FixAlphaChannel(px);
            workMs = MsSince(t0);
            return px;
        }

        /// <summary>按需从最近一帧的 GPU 快照回读 HDR 像素（RGBA16F → float）。</summary>
        private float[]? ReadbackLatestHdrFresh()
        {
            if (!Monitor.TryEnter(_gpuLock, 500))
            {
                Log($"[Pool] {_hmonitor:X}: 按需回读 GPU 锁超时 (500ms)");
                return null;
            }
            try
            {
                if (!_latestTexValid || _latestTexture is null || _latestTexW <= 0 || _latestTexH <= 0) return null;
                return ReadFloatPixelsLocked(_latestTexture, 0, 0, _latestTexW, _latestTexH);
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 按需回读 HDR 失败: {ex.Message}");
                return null;
            }
            finally { Monitor.Exit(_gpuLock); }
        }

        /// <summary>把一块 Float16 纹理上的一个矩形读成 float[]（调用方须持 GPU 锁）。失败返回 null。</summary>
        private float[]? ReadFloatPixelsLocked(ID3D11Texture2D src, int srcX, int srcY, int w, int h)
        {
            var px = new float[w * h * 4];
            EnsureStaging(w, h, Vortice.DXGI.Format.R16G16B16A16_Float);
            var staging = _stagingTex;
            if (staging is null) return null;
            var ctx = _d3dDevice.ImmediateContext;
            var sd = src.Description;
            // 整块就读整块（驱动可走整块搬），否则只拷选区那一块、也只转换那一块 ——
            // 路线 B-3：选区截图不必把整屏 132MB 搬进 CPU。
            if (srcX == 0 && srcY == 0 && w == (int)sd.Width && h == (int)sd.Height)
                ctx.CopyResource(staging, src);
            else
                ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, src, 0,
                    new Vortice.Mathematics.Box(srcX, srcY, 0, srcX + w, srcY + h, 1));
            var mapped = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            // 行间无依赖 ⇒ 整帧转换按行并行。本机 4K 孤立实测：串行逐行 31.4ms → 按行并行 6.0ms
            // （读数 artifacts/readback_bench_2026-10-30.log；孤立单项，不许相加去解释端到端）。
            // ⚠ Unmap 收进 finally：并行体里一旦抛异常（越界/取消），不 Unmap 的这个 subresource
            //   之后再 Map 会永久失败 ⇒ 该会话从此静默交不出像素（HdrCaptureWindow.UploadFrame 里同款修复）。
            try
            {
                unsafe
                {
                    long sBase = (long)mapped.DataPointer.ToPointer();   // fixed 指针不许被 lambda 捕获 ⇒ 走 long 基址
                    int srcRowPitch = (int)mapped.RowPitch;
                    fixed (float* dst = px)
                    {
                        long dBase = (long)dst;
                        Parallel.For(0, h, row =>
                            TrueToneCap.Core.PixelOps.ConvertHalfToFloatRow(
                                (byte*)(sBase + row * (long)srcRowPitch),
                                (float*)(dBase + row * (long)w * 16),
                                w * 4));
                    }
                }
            }
            finally { ctx.Unmap(staging, 0); }
            return px;
        }

        /// <summary>把一块 8-bit（B8G8R8A8_UNorm）纹理上的一个矩形读成 byte[]（调用方须持 GPU 锁）。失败返回 null。</summary>
        /// <remarks>
        /// 为什么每次新分配 <c>byte[w*h*4]</c>、刻意不复用 <c>_writeBufferSdr</c>：那块是整帧的
        /// **发布缓冲** —— 帧回调把它当"上一帧"交给 GetLatestSdr / 录制器（见 OnFrameArrived 的
        /// <c>_writeBufferSdr = _latestSdr</c> 交接）。把矩形像素写进去会按行覆盖掉整帧缓冲的前
        /// <c>h</c> 行前 <c>w</c> 列，下一个消费者取"最新帧"拿到的就是被挖了一块碎图的帧。
        /// 选区回读是一次性交付（选区定了才调一次），这里不值得为省一次分配去冒污染发布帧的风险。
        /// </remarks>
        private byte[]? ReadBytePixelsLocked(ID3D11Texture2D src, int srcX, int srcY, int w, int h)
        {
            var px = new byte[w * h * 4];
            EnsureStaging(w, h, Vortice.DXGI.Format.B8G8R8A8_UNorm);
            var staging = _stagingTex;
            if (staging is null) return null;
            var ctx = _d3dDevice.ImmediateContext;
            var sd = src.Description;
            // 与 ReadFloatPixelsLocked 同一形状：整块就读整块（驱动可走整块搬），
            // 否则只拷选区那一块 —— 选区截图不必把整屏搬进 CPU。
            if (srcX == 0 && srcY == 0 && w == (int)sd.Width && h == (int)sd.Height)
                ctx.CopyResource(staging, src);
            else
                ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, src, 0,
                    new Vortice.Mathematics.Box(srcX, srcY, 0, srcX + w, srcY + h, 1));
            var mapped = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            // Unmap 收进 finally 的理由同 ReadFloatPixelsLocked：漏掉一次 Unmap，该 subresource
            // 之后 Map 恒失败 ⇒ 这个会话从此静默交不出像素（并行/异常退出都不许绕过这一步）。
            try
            {
                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer.ToPointer();
                    int srcRowPitch = (int)mapped.RowPitch;
                    int dstStride = w * 4;
                    fixed (byte* dst = px)
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy(srcBase + row * srcRowPitch, dst + row * dstStride, dstStride, dstStride);
                }
            }
            finally { ctx.Unmap(staging, 0); }

            // 与整屏 SDR 回读同一条 alpha 纪律（ReadBytePixelsPooled 末尾那一句）：
            // WGC 交出来的 alpha 可能是 0，不修的选区像素与今天整屏出来的不是同一套颜色，
            // 下游一旦把 alpha 当不透明/预乘来解释，画面就会偏。纯 CPU 遍历，不碰 GPU。
            TrueToneCap.Core.PixelOps.FixAlphaChannel(px);
            return px;
        }

        /// <summary>确保 staging 纹理与给定尺寸/格式匹配（调用方须持 GPU 锁）。</summary>
        private void EnsureStaging(int w, int h, Vortice.DXGI.Format format)
        {
            Interlocked.Increment(ref _mStagingAsk);   // F5 仪表：命中复用也算"问过"，否则分母只剩新建
            if (_stagingTex is not null && _stagingW == w && _stagingH == h) return;
            _stagingTex?.Dispose();
            // ── F5 仪表：上一块存在且尺寸不同 ⇒ 这就是"选区回读把整屏 staging 拆掉重建"的直接读数 ──
            // （_stagingW/_stagingH 只在成功新建之后被赋值，所以">0"就是"这条会话之前有过一块"。）
            if (_stagingW > 0 && _stagingH > 0) Interlocked.Increment(ref _mStagingResize);
            Interlocked.Increment(ref _mStagingCreate);
            long tStaging = PerfMeter.Now();
            _stagingTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = format,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            PerfMeter.AddStagingCreate(ref _mStagingCreateUs, ref _mStagingCreateMaxUs,
                ref _mStagingBytes, PerfMeter.SinceMicro(tStaging), w, h, format);
            _stagingW = w;
            _stagingH = h;
        }

        /// <summary>获取帧年龄（ms）；会话还没拿到过任何帧时返回 -1。</summary>
        /// <remarks>
        /// ═══ 2026-10-30：没帧时不许回吐"开机以来的毫秒数" ═══
        /// <c>_frameTimestamp</c> 只在帧回调里被赋值，初始值是 0 ⇒ 旧写法对没帧的会话算出
        /// <c>TickCount64 - 0</c>，日志里长成 <c>HasFrame=False Age=67813432ms</c>。
        /// 排障时"没有帧"和"帧很旧"是两条相反的处置路径，一个读数把两者混成了一个数量级的怪数。
        /// </remarks>
        public long GetFrameAge()
        {
            if (!_hasFrame) return -1;
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
                UpdateSharedSlotLocked();
                }
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: CacheTextureGpuCopy 失败: {ex.Message}");
                _latestTexValid = false;
            }
        }

        // ═══ 2026-10-30 路线 B：跨设备共享槽的维护与交接 ═══
        // 三个动作各有一条纪律，写在这里是因为它们在时序上互相牵制：
        //   ① Update（每帧，持 _gpuLock）：被取走期间【必须什么都不做】—— 用户框选时画面要冻住；
        //   ② Take（一次截图一次）：取走时若上一任还没交还，就【换一块新纹理】把它那帧留给它，
        //      绝不去覆写一块别人正拿着的（这正是 2026-09-15 P0-C 那次画面撕裂的同型事故）；
        //      换新的代价只有一块纹理，漏交还本身是调用方的 bug，不该让用户的画面来赔；
        //   ③ Release（预览窗关闭时）：把冻结解开，下一帧继续更新这一块。
        // 单消费者是前提：今天由 MainWindow 的 _isCapturing 防重入闸保证（预览期间不会有第二次截图），
        // 与本文件 GetLatestHdr 那段"契约注释是错的"记的是同一条不变量。

        /// <summary>建/更新共享槽（调用方须持 GPU 锁）。被取走时直接返回 —— 宁可少更新一帧，也不动别人拿着的那块。</summary>
        private void UpdateSharedSlotLocked()
        {
            if (_sharedHeld || _latestTexture is null || !_latestTexValid) return;
            string step = "?"; // 失败时点名到步：建纹理 / 建句柄 / 拷贝 三件都可能抛 E_INVALIDARG
            try
            {
                var desc = _latestTexture.Description;
                if (_sharedTex is null || _sharedW != (int)desc.Width || _sharedH != (int)desc.Height
                    || _sharedTex.Description.Format != desc.Format)
                {
                    step = $"建纹理 {desc.Width}x{desc.Height} {desc.Format}";
                    CreateSharedTextureLocked(desc.Width, desc.Height, desc.Format);
                }
                step = "拷贝";
                _d3dDevice.ImmediateContext.CopyResource(_sharedTex!, _latestTexture);
                _sharedValid = true;
            }
            catch (Exception ex)
            {
                LogSharedFault($"{step} ⇒ {ex.Message}");
                _sharedValid = false;
            }
        }

        /// <summary>建一块可跨设备打开的纹理并生成句柄（调用方须持 GPU 锁）。
        /// 两步各自带名抛异常：E_INVALIDARG 既可能来自建纹理（desc 不合规），也可能来自建句柄
        /// （访问权/属性），合在一起报就分不出是哪一步 —— 第一版就吃了这个亏。</summary>
        private void CreateSharedTextureLocked(uint w, uint h, Vortice.DXGI.Format format)
        {
            ResetSharedSlotLocked();
            ID3D11Texture2D tex;
            try
            {
                tex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                {
                    Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                    Format = format,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,     // 预览侧要 CreateShaderResourceView
                    CPUAccessFlags = CpuAccessFlags.None,
                    // ═══ 2026-10-30 实测：必须是 SharedNTHandle，不能是 Shared ═══
                    // 普通 MISC_SHARED 走的是 DXGI 1.0 的 IDXGIResource::GetSharedHandle，
                    // 而 IDXGIResource1::CreateSharedHandle 只认 MISC_SHARED_KEYEDMUTEX / MISC_SHARED_NTHANDLE
                    // ⇒ 拿普通 Shared 去建句柄恒 E_INVALIDARG（本机把 desc 回读成 Misc=Shared 也照样红，
                    //   连裸 vtable + attrs=null 都是同一个错，见 artifacts/frame_handoff_2026-10-30.log）。
                    // 消费侧对应地必须用 ID3D11Device1::OpenSharedResource1，不能用 OpenSharedResource。
                    MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"CreateTexture2D(Shared {w}x{h} {format}) 失败: {ex.Message}", ex);
            }
            try
            {
                using var res = tex.QueryInterface<Vortice.DXGI.IDXGIResource1>();
                _sharedHandle = res.CreateSharedHandle(null,
                    Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
            }
            catch (Exception ex)
            {
                // 把建出来的纹理自己回读的 desc 一起交出去：CreateSharedHandle 的 E_INVALIDARG
                // 只有两种来路 —— 资源没带 MISC_SHARED，或属性/访问权不合规。
                // 这一行让下一次读数直接分得开（Misc=Shared 还在 ⇒ 不是标志没落上）。
                try
                {
                    var back = tex.Description;
                    LogSharedFault($"CreateSharedHandle({ex.Message}) 回读 desc: Misc={back.MiscFlags} " +
                                   $"Bind={back.BindFlags} Usage={back.Usage} Format={back.Format} {back.Width}x{back.Height}");
                }
                catch { }
                tex.Dispose();
                throw new InvalidOperationException($"CreateSharedHandle 失败: {ex.Message}", ex);
            }
            _sharedTex = tex;
            _sharedW = (int)w; _sharedH = (int)h;
            _sharedValid = false;
        }

        /// <summary>释放本侧对共享槽的引用（关句柄 + 弃纹理）。预览侧已打开的资源不受影响（DXGI 引用计数）。</summary>
        private void ResetSharedSlotLocked()
        {
            if (_sharedHandle != 0)
            {
                CloseHandle(_sharedHandle);   // CreateSharedHandle 交出来的是调用方负责关的 HANDLE
                _sharedHandle = 0;
            }
            _sharedTex?.Dispose();
            _sharedTex = null;
            _sharedW = _sharedH = 0;
            _sharedValid = false;
        }

        /// <summary>交还共享槽（预览窗关闭时调）。之后下一帧继续覆写这一块。</summary>
        internal void ReleaseSharedFrame()
        {
            lock (_gpuLock) { _sharedHeld = false; }
        }

        /// <summary>路线 B 的生产入口：把当前帧冻结进共享槽并交出句柄（不在这里读像素）。</summary>
        /// <remarks>为什么不"先 GetLatestHdr 再取槽"：本机实测两者之间恒定隔着一次 WGC 交付
        /// （捕获尾部还有 ICC 读取/日志；即使挪进捕获方法，帧回调也仍在跑），槽必然比像素新一帧。
        /// 取槽紧跟取像素、且两者来自同一块冻结纹理；像素等到选区定了才按矩形回读
        /// （<see cref="ReadSharedRegion"/>）—— 整屏 132MB 的 CPU 回读不在开窗前付。
        /// 失败（槽没建好 / 格式不对）时返回 false，调用方退回常规取像素。</remarks>
        internal bool TryFreezeShared(out nint handle, out int width, out int height)
        {
            handle = 0; width = 0; height = 0;
            if (!Monitor.TryEnter(_gpuLock, 500)) { Log($"[Pool] {_hmonitor:X}: 取共享槽 GPU 锁超时 (500ms)"); return false; }
            try
            {
                if (_sharedTex is null || _sharedHandle == 0 || !_sharedValid || _latestTexture is null) return false;
                // 格式闸认"会话自己的格式"，不再硬编码 Float16：槽本来就是按 desc.Format 建的
                // （UpdateSharedSlotLocked → CreateSharedTextureLocked(desc.Format)），这里只要求
                // 槽与当前帧同格式 —— 槽是上一帧建的、帧是这一帧，格式不一致说明槽还没跟上。
                // HDR 会话的纹理就是 R16G16B16A16_Float ⇒ 对既有行为等价；
                // SDR 会话（B8G8R8A8_UNorm）从此也能冻结交槽。
                if (_sharedTex.Description.Format != _latestTexture.Description.Format) return false;
                if (_sharedHeld)
                {
                    // 上一任没交还：换一块新的给它留住，这一块照常交出（纪律②）
                    Log($"[Pool] {_hmonitor:X}: 共享槽上一任未交还，换一块新纹理（旧那块留给持有者）");
                    CreateSharedTextureLocked((uint)_latestTexW, (uint)_latestTexH, _latestTexture.Description.Format);
                    _d3dDevice.ImmediateContext.CopyResource(_sharedTex!, _latestTexture);
                    _sharedValid = true;
                }
                _sharedHeld = true;
                handle = _sharedHandle; width = _sharedW; height = _sharedH;
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 取共享槽失败: {ex.Message}");
                _sharedHeld = false;
                return false;
            }
            finally { Monitor.Exit(_gpuLock); }
        }

        /// <summary>路线 B-3：从已被取走的那块冻结纹理上只回读一个矩形（选区定了才调）。
        /// 坐标是纹理坐标（单屏 = 窗口坐标减显示器原点）。</summary>
        /// <remarks>只认 <paramref name="expectedHandle"/> 那一块：若槽在此期间被轮换过，
        /// 读新块等于把用户没框住的那一帧交出去 —— 宁可失败让上层留痕。</remarks>
        internal float[]? ReadSharedRegion(nint expectedHandle, int x, int y, int w, int h)
        {
            if (!Monitor.TryEnter(_gpuLock, 500)) { Log($"[Pool] {_hmonitor:X}: 选区回读 GPU 锁超时 (500ms)"); return null; }
            try
            {
                if (!_sharedHeld || !_sharedValid || _sharedTex is null) return null;
                if (expectedHandle == 0 || _sharedHandle != expectedHandle)
                {
                    Log($"[Pool] {_hmonitor:X}: 选区回读拒绝：槽已轮换（持有 {expectedHandle:X} 当前 {_sharedHandle:X}）");
                    return null;
                }
                if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > _sharedW || y + h > _sharedH)
                {
                    Log($"[Pool] {_hmonitor:X}: 选区回读越界 ({x},{y} {w}x{h} / 槽 {_sharedW}x{_sharedH})");
                    return null;
                }
                return ReadFloatPixelsLocked(_sharedTex, x, y, w, h);
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 选区回读失败: {ex.Message}");
                return null;
            }
            finally { Monitor.Exit(_gpuLock); }
        }

        /// <summary>路线 B-3（SDR 腿）：从已被取走的那块冻结纹理上只回读一个矩形的 8-bit 像素。
        /// 守卫与 <see cref="ReadSharedRegion"/> 逐条同构，只是像素类型是 byte[]（槽格式 B8G8R8A8_UNorm）。</summary>
        /// <remarks>只认 <paramref name="expectedHandle"/> 那一块：槽若在此期间被轮换过，
        /// 读新块等于把用户没框住的那一帧交出去 —— 宁可失败让上层留痕。
        /// 日志刻意与 float 那条不同句（点名 SDR），否则排障时两条路径的留痕混在一起分不开。</remarks>
        internal byte[]? ReadSharedRegionBytes(nint expectedHandle, int x, int y, int w, int h)
        {
            if (!Monitor.TryEnter(_gpuLock, 500)) { Log($"[Pool] {_hmonitor:X}: 选区 SDR 回读 GPU 锁超时 (500ms)"); return null; }
            try
            {
                if (!_sharedHeld || !_sharedValid || _sharedTex is null) return null;
                if (expectedHandle == 0 || _sharedHandle != expectedHandle)
                {
                    Log($"[Pool] {_hmonitor:X}: 选区 SDR 回读拒绝：槽已轮换（持有 {expectedHandle:X} 当前 {_sharedHandle:X}）");
                    return null;
                }
                if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > _sharedW || y + h > _sharedH)
                {
                    Log($"[Pool] {_hmonitor:X}: 选区 SDR 回读越界 ({x},{y} {w}x{h} / 槽 {_sharedW}x{_sharedH})");
                    return null;
                }
                return ReadBytePixelsLocked(_sharedTex, x, y, w, h);
            }
            catch (Exception ex)
            {
                Log($"[Pool] {_hmonitor:X}: 选区 SDR 回读失败: {ex.Message}");
                return null;
            }
            finally { Monitor.Exit(_gpuLock); }
        }

        // ── Staging 纹理复用 + 像素缓冲区复用的像素读取 ──

        private byte[]? ReadBytePixelsPooled(ID3D11Texture2D texture, int w, int h, out long workTicks)
        {
            workTicks = 0;
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
            // 锁之后起计：这里要的是"整屏回读的单价"（好拿去和建共享槽的收益比），
            // 不含锁等待；FixAlphaChannel 是回读链路自己的一趟 CPU 遍历，算进去。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var ctx = _d3dDevice.ImmediateContext;

                // P2: 复用 staging 纹理
                Interlocked.Increment(ref _mStagingAsk);   // F5 仪表（与 EnsureStaging 同口径：命中复用也计"问过"）
                if (_stagingTex is null || _stagingW != w || _stagingH != h)
                {
                    _stagingTex?.Dispose();
                    if (_stagingW > 0 && _stagingH > 0) Interlocked.Increment(ref _mStagingResize);
                    Interlocked.Increment(ref _mStagingCreate);
                    long tStaging = PerfMeter.Now();
                    _stagingTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    });
                    PerfMeter.AddStagingCreate(ref _mStagingCreateUs, ref _mStagingCreateMaxUs,
                        ref _mStagingBytes, PerfMeter.SinceMicro(tStaging), w, h,
                        Vortice.DXGI.Format.B8G8R8A8_UNorm);
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
            workTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
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

                Interlocked.Increment(ref _mStagingAsk);   // F5 仪表（HDR 逐帧整屏回读这一腿）
                if (_stagingTex is null || _stagingW != w || _stagingH != h)
                {
                    _stagingTex?.Dispose();
                    if (_stagingW > 0 && _stagingH > 0) Interlocked.Increment(ref _mStagingResize);
                    Interlocked.Increment(ref _mStagingCreate);
                    long tStaging = PerfMeter.Now();
                    _stagingTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                        Format = Vortice.DXGI.Format.R16G16B16A16_Float,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    });
                    PerfMeter.AddStagingCreate(ref _mStagingCreateUs, ref _mStagingCreateMaxUs,
                        ref _mStagingBytes, PerfMeter.SinceMicro(tStaging), w, h,
                        Vortice.DXGI.Format.R16G16B16A16_Float);
                    _stagingW = w; _stagingH = h;
                }

                ctx.CopyResource(_stagingTex, texture);
                var mapped = ctx.Map(_stagingTex, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                // 与 ReadbackLatestHdrFresh 同一条纪律：按行并行 + Unmap 进 finally（两处必须同形，
                // 否则"消费者窗口内"这条更常走的路径反而是串行那份）。
                try
                {
                    unsafe
                    {
                        long sBase = (long)mapped.DataPointer.ToPointer();
                        int srcRowPitch = (int)mapped.RowPitch;
                        int halfsPerRow = w * 4;
                        fixed (float* dst = pixels)
                        {
                            long dBase = (long)dst;
                            Parallel.For(0, h, row =>
                                TrueToneCap.Core.PixelOps.ConvertHalfToFloatRow(
                                    (byte*)(sBase + row * (long)srcRowPitch),
                                    (float*)(dBase + row * (long)halfsPerRow * 4),
                                    halfsPerRow));
                        }
                    }
                }
                finally { ctx.Unmap(_stagingTex, 0); }
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
                // 共享槽：先关句柄再弃纹理。已在预览侧打开的那份由 DXGI 引用计数保命，
                // 所以这里释放不影响"用户还在框选"这种情形（预览窗自己持着引用）。
                try { ResetSharedSlotLocked(); } catch { }
            }
            _firstFrameEvent.Dispose();
            _callbacksDrained.Dispose();

            // ═══ F5 仪表结算：一条会话一行（诊断档才落盘；逐帧只累加内存，不碰磁盘）═══
            // 落点选在这里是因为"每条会话各几次"就是这条路的原问题：会话生命周期由
            // (HMONITOR,IsHdr) + 15s 空闲回收决定，一条一结既是"per session"的字面口径，
            // 也不会像逐帧打点那样把判据淹掉。Environment.Exit 的探针不走这里 ⇒ 那种跑法
            // 请改看 WgcCaptureService 侧的空闲回收/失效结算（同一批字段，跨会话合计）。
            if (PerfMeter.DumpEnabled)
                Log($"[Meter/F5] 会话 0x{_hmonitor:X} {(IsHdr ? "Float16" : "BGRA8")} {_width}x{_height}："
                    + $" staging 问 {Interlocked.Read(ref _mStagingAsk)} 次 ⇒ 新建 {Interlocked.Read(ref _mStagingCreate)} 块"
                    + $"（其中尺寸切换 {Interlocked.Read(ref _mStagingResize)} 次）"
                    + $"，CreateTexture2D 合计 {PerfMeter.Ms(Interlocked.Read(ref _mStagingCreateUs))}"
                    + $" 峰值 {PerfMeter.Ms(Interlocked.Read(ref _mStagingCreateMaxUs))}"
                    + $"，累计申请 {Interlocked.Read(ref _mStagingBytes) / (1024 * 1024)}MB"
                    + $"（逐帧回读的次数/总耗时由 FlushSdrWindowIfExpired 每 2s 一结，不在这里重复）");
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
        // ⚠ 2026-10-07：上面那条"匹配是空转"已修 —— 现在真的用匹配到的适配器建（见
        //   CreateDeviceOnAdapter）。但"设备健康就复用"仍然成立：一个进程内只建一次设备，
        //   后续显示器切换不再重建（那正是 2026-09-16 这条修复要治的病）。
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
            int adapterCount = 0, outputCount = 0;
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter is null) break;
                adapterCount++;
                try
                {
                    for (uint j = 0; ; j++)
                    {
                        if (adapter.EnumOutputs(j, out var output).Failure || output is null) break;
                        outputCount++;
                        try
                        {
                            if (output.Description.Monitor == hmonitor)
                            {
                                // ═══ 2026-10-07：这里原来【找到了适配器却不传给创建函数】═══
                                // 旧代码是 `D3D11CreateDevice(DriverType.Hardware, BgraSupport)` ——
                                // 那个简化重载没有适配器形参，拿到的是**默认适配器**；
                                // 上面这层 for/EnumOutputs 匹配只影响那行日志，对"用哪块卡"零作用。
                                // 后果（混合显卡机）：默认适配器未必驱动本屏 ⇒ WGC 帧池建在 A 卡上、
                                // 而画面在 B 卡上，DWM 只能跨适配器拷贝。本机（RTX 5080 是 adapter[0]）
                                // 恰好命中所以没暴露，但那是运气，不是设计。
                                // ⚠ Vortice 3.8.3 的简化重载不接受 IDXGIAdapter（见 NvEncoderNative.cs:427
                                //   的同款结论），故走原生 P/Invoke，driverType 必须 = 0(UNKNOWN)。
                                _d3dDevice = CreateDeviceOnAdapter(adapter);
                                if (_d3dDevice is not null)
                                {
                                    // ⚠ 必须用 LogInfo（不是 Log）：这行是"到底用了哪块卡"的唯一读数，
                                    //   而默认日志档位会把 Debug 整层滤掉 ⇒ 旧实现里它从来没落过盘，
                                    //   于是"匹配失败退到默认适配器"与"匹配成功"在日志上完全一样。
                                    LogInfo($"[WGC] D3D11 设备创建于匹配到的适配器: {adapter.Description.Description.Trim()}");
                                    return _d3dDevice;
                                }
                                // 指定适配器失败 ⇒ 不 return，继续试下一个（可能还有别的适配器也带这个 output）
                                LogInfo($"[WGC] 在适配器 {adapter.Description.Description.Trim()} 上创建设备失败，继续枚举");
                            }
                        }
                        finally { output.Dispose(); }
                    }
                }
                finally { adapter.Dispose(); }
            }
            // ⚠ 必须说出"枚举了几块卡、几个 output"：匹配不到时旧实现只静默落到默认适配器，
            //   看起来和"匹配成功"一模一样（都是建出一块设备），于是这条腿坏掉也无人察觉。
            LogInfo($"[WGC] 适配器匹配: 枚举 {adapterCount} 块适配器 / {outputCount} 个 output，未命中 hmonitor=0x{hmonitor:X}");
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

    /// <summary>在**指定适配器**上创建 D3D11 设备；失败返回 null（调用方继续枚举下一个）。
    /// <para>为什么不用 Vortice 的重载：3.8.3 的 <c>D3D11.D3D11CreateDevice</c> 只有
    /// <c>(DriverType, Flags, …)</c> 简化重载与不可访问的 10 参数原生重载，
    /// <b>没有</b>接受 <c>IDXGIAdapter</c> 的简化重载 —— 同款结论见
    /// <c>NvEncoderNative.cs:427</c>（那里为 NVENC 走了同一条 P/Invoke）。</para>
    /// <para>⚠ 传了适配器时 <c>driverType</c> 必须是 <c>0 (D3D_DRIVER_TYPE_UNKNOWN)</c>，
    /// 传 Hardware 会被 D3D11 拒绝（E_INVALIDARG）。</para></summary>
    private static ID3D11Device? CreateDeviceOnAdapter(IDXGIAdapter1 adapter)
    {
        try
        {
            int hr = D3D11CreateDeviceNative(
                adapter.NativePointer,
                0,                                          // D3D_DRIVER_TYPE_UNKNOWN（指定适配器时必须）
                nint.Zero,                                  // Software (HMODULE) 不用
                (uint)DeviceCreationFlags.BgraSupport,
                nint.Zero,                                  // pFeatureLevels = null ⇒ 默认列表
                0,                                          // FeatureLevels = 0 ⇒ 用默认
                D3D11SdkVersion,
                out nint devPtr, out _, out nint ctxPtr);

            if (hr < 0 || devPtr == 0) return null;

            // 立即上下文稍后由 ID3D11Device.ImmediateContext 再取，这里释放原始引用
            if (ctxPtr != 0) Marshal.Release(ctxPtr);
            return new ID3D11Device(devPtr);
        }
        catch (Exception ex)
        {
            LogInfo($"[WGC] CreateDeviceOnAdapter 异常: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private const uint D3D11SdkVersion = 7;

    /// <summary>原生 D3D11CreateDevice。
    /// <para>⚠ 必须显式 <c>EntryPoint="D3D11CreateDevice"</c>：d3d11.dll 导出的就是该名字，
    /// 按 C# 方法名查找会抛 <c>EntryPointNotFoundException</c>（同款教训见 NvEncoderNative.cs:405）。</para>
    /// <para>形参顺序即 D3D11CreateDevice 的 ABI 顺序；<c>pAdapter</c> 非 0 时
    /// <c>driverType</c> 必须为 0（见 <see cref="CreateDeviceOnAdapter"/>）。</para></summary>
    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3D11CreateDeviceNative(
        nint pAdapter,
        int driverType,
        nint software,
        uint flags,
        nint pFeatureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint ppDevice,
        out int pFeatureLevel,
        out nint ppImmediateContext);

    // ═══════════════════════════════════════
    //  会话池管理
    // ═══════════════════════════════════════

    /// <summary>获取或创建指定显示器的持久 SDR 会话。</summary>
    private PooledSession GetOrCreateSdrSession(nint hmonitor)
    {
        var key = (hmonitor, false);
        long tLock = PerfMeter.Now();   // F5 仪表：包住原有临界区，不改其中任何一步
        try
        {
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
                    // ⚠ 用 CreateItemForMonitor 留下的那句归因（提权 vs 被挡），而不是复述显示器号：
                    //   旧文案「无法为显示器 0x… 创建 WGC 会话」对用户是零信息 ——
                    //   它既没说为什么，也指不出任何可做的动作（本机 EnableLUA=0 那次就是这么被卡住的）。
                    throw new InvalidOperationException(
                        TakeItemFailureMessage() ?? $"无法为显示器 0x{hmonitor:X} 创建 WGC 会话");

                _sessionPool[key] = session;
                return session;
            }
        }
        finally { RecordPoolLock(LockGetSdr, tLock); }
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
        // 诊断开关（2026-10-30 阶段 3）：TTC_NO_FLOAT16=1 模拟"纯 SDR 屏"——扩展范围会话一律不给，
        // 捕获栈自然退回只有 SDR 帧那条路（本机是 HDR 屏，没有这块硬件可测）。只此一处短路，
        // 下游 GetOrCreateHdrSession/捕获分支靠既有判据自己走 SDR，不需要第二处改动。
        !sNoFloat16 && ClassifyMonitorColor(hmonitor) != DisplayEnumerator.DisplayColorClass.NarrowSdr;

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
        long tLock = PerfMeter.Now();   // F5 仪表：包住原有临界区（注意锁内还有 Dispose 等待回调，最长可到 2s）
        try
        {
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
        finally { RecordPoolLock(LockGetHdr, tLock); }
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
                long tLock = PerfMeter.Now();   // F5 仪表
                try
                {
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
                finally { RecordPoolLock(LockIdleStop, tLock); }
                // 结算点：一批截图结束时把 _poolLock 的持锁账打成一行（诊断档）。
                // 选这里而不是"每 N 次"：空闲回收本来就是"这一批结束了"的天然边界，
                // 一次回收一行，量级与截图批数同阶。
                DumpPoolLockMeters($"空闲回收 {IdleStopDelayMs / 1000}s");
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>使所有会话失效（显示器配置变更时调用）。</summary>
    public void InvalidateSessions()
    {
        long tLock = PerfMeter.Now();   // F5 仪表
        try
        {
            lock (_poolLock)
            {
                foreach (var s in _sessionPool.Values) s.Dispose();
                _sessionPool.Clear();
            }
        }
        finally { RecordPoolLock(LockInvalidate, tLock); }
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
        DumpPoolLockMeters("会话失效（显示器变更/服务退出）");
    }

    /// <summary>探针出口（--capture-freshness-probe）：读某块屏某条池"最新一帧已经存了多少毫秒"。
    /// 没有会话、或有会话但从来没出过帧 ⇒ 返回 null，不许用 0 冒充"这帧很新"。
    /// 走 internal 直访而不是反射：与 <see cref="HdrCaptureWindow.ProbeBakedDdRows"/> 同一口径，
    /// 反射在打包形态下会静默失败（那类事故在本仓留过记录）。</summary>
    internal long? ProbeFrameAgeMs(nint hmonitor, bool hdr)
    {
        long tLock = PerfMeter.Now();   // F5 仪表
        try
        {
            lock (_poolLock)
            {
                if (!_sessionPool.TryGetValue((hmonitor, hdr), out var s) || !s.HasFrame)
                    return null;
                return s.GetFrameAge();
            }
        }
        finally { RecordPoolLock(LockProbeAge, tLock); }
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

    /// <summary>交还共享槽（预览窗关闭时调）。会话已被回收时无事可做。</summary>
    internal void ReleaseSharedFrame(nint targetMonitor, bool hdr)
    {
        long tLock = PerfMeter.Now();   // F5 仪表
        try
        {
            lock (_poolLock)
            {
                if (_sessionPool.TryGetValue((targetMonitor, hdr), out var session))
                    session.ReleaseSharedFrame();
            }
        }
        finally { RecordPoolLock(LockReleaseShared, tLock); }
    }

    /// <summary>从冻结的共享槽里只回读一个矩形（路线 B-3：选区定了才调）。会话已被回收时返回 null。</summary>
    internal float[]? ReadSharedRegion(nint targetMonitor, bool hdr, nint expectedHandle,
        int x, int y, int w, int h)
    {
        long tLock = PerfMeter.Now();   // F5 仪表：这一段今天【整段在锁内做 GPU 回读】，持锁时长就是那条指控要的读数
        try
        {
            lock (_poolLock)
            {
                if (_sessionPool.TryGetValue((targetMonitor, hdr), out var session))
                    return session.ReadSharedRegion(expectedHandle, x, y, w, h);
            }
        }
        finally { RecordPoolLock(LockReadHdr, tLock); }
        return null;
    }

    /// <summary>从 SDR 会话冻结的共享槽里只回读一个矩形的 8-bit 像素（路线 B-3 的 SDR 腿）。
    /// 会话已被回收时返回 null。</summary>
    /// <remarks>SDR 会话的槽格式是 <c>B8G8R8A8_UNorm</c> ⇒ 出 <c>byte[]</c>；HDR 那条
    /// （<see cref="ReadSharedRegion"/>，槽是 <c>R16G16B16A16_Float</c>）出 <c>float[]</c>。
    /// 两条分开不是因为守卫不同，而是下游消费的像素类型不同 —— 合成一个入口就得让调用方
    /// 按会话的 HDR 标志去猜返回类型。</remarks>
    internal byte[]? ReadSharedRegionSdr(nint targetMonitor, nint expectedHandle, int x, int y, int w, int h)
    {
        long tLock = PerfMeter.Now();   // F5 仪表（SDR 腿，同上：回读整段在锁内）
        try
        {
            lock (_poolLock)
            {
                if (_sessionPool.TryGetValue((targetMonitor, false), out var session))
                    return session.ReadSharedRegionBytes(expectedHandle, x, y, w, h);
            }
        }
        finally { RecordPoolLock(LockReadSdr, tLock); }
        return null;
    }

    /// <summary>无锁读取最新帧（供录制器使用）。不获取 s_captureLock，不创建会话，
    /// 仅从已有池化会话中读取最新帧。如果会话不存在或无帧则返回 null。</summary>
    /// <param name="reuse">调用方持有的空闲缓冲：尺寸匹配时拷进它而不新分配。
    /// 录制器用它接住"被判为未变化、随即丢弃"的帧 —— 静态画面下原先每 tick 都白分配一整帧。</param>
    public CaptureResult? TryGetLatestFrame(nint targetMonitor, byte[]? reuse = null)
    {
        long tLock = PerfMeter.Now();   // F5 仪表：GetLatestSdr 可能在这里等 GPU 锁最多 500ms + 拷一整帧
        try
        {
            lock (_poolLock)
            {
                var key = (targetMonitor, false);
                if (!_sessionPool.TryGetValue(key, out var session) || !session.HasFrame)
                    return null;
                var pixels = session.GetLatestSdr(reuse);
                if (pixels is null) return null;
                return new CaptureResult
                {
                    SdrPixels = pixels,
                    Width = session.Width,
                    Height = session.Height
                };
            }
        }
        finally { RecordPoolLock(LockTryLatest, tLock); }
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

        // ═══ 1. 两条线的池【先都建好】，再依次取帧（2026-10-30 重叠）═══
        // 原顺序是「建 SDR 池 → 阻塞等 SDR 首帧 → 回读 → 才建 Float16 池 → 再阻塞等 Float16 首帧」。
        // 于是第二条线在等第一条首帧的那段里【还不存在】：本机实测 SDR 等帧 p50=83ms、
        // Float16 建池 p50=51ms + 等帧 p50=160ms，串行合计 479ms；两个池都是 CreateFreeThreaded、
        // 投递各走自己的回调线程，所以只要先把池都建起来，那 83ms 就能被第二条线的建池+到货盖掉
        // （合计下界 396ms）。只改顺序，一条判据都没动：
        //   软冷却额度仍在【新建之前】被消费（GetOrCreateHdrSession 内部），
        //   记账口径仍看 HasFrame，尺寸校验仍在取像素之后，复用槽更新仍排在尺寸校验之后。
        var sdrSession = GetOrCreateSdrSession(targetMonitor);
        PooledSession? hdrSession = null;
        if (config.PreferHdr)
        {
            try
            {
                hdrSession = GetOrCreateHdrSession(targetMonitor);
            }
            catch (Exception ex)
            {
                LogInfo($"[WGC] Float16 会话建立失败: {ex.Message}（不影响 SDR 结果）");
            }
        }
        // 没帧时 Age 段的读法见 GetFrameAge()：这一行不许把"没帧"印成"帧龄很大"。
        long sdrAgeMs = sdrSession.GetFrameAge();
        Log($"[WGC] SDR 池状态: HasFrame={sdrSession.HasFrame} " +
            (sdrAgeMs < 0 ? "Age=无帧" : $"Age={sdrAgeMs}ms"));

        byte[]? sdrPixels = null;
        // ═══ 路线 B（SDR 半，2026-10-30）：扩展范围屏上 SDR 像素也等到选区定了才按矩形回读 ═══
        // 条件里的 `hdrSession is not null` 是硬要求：没有 Float16 会话时，预览窗**只能**吃这张 SDR 整帧
        // （预览的 GPU 采样源是 HDR 槽），这里跳过回读就等于整屏只剩遮罩。
        // 两块槽成对取出、成对交还，但**不是同一帧**（两个会话各自到货、各自冻结，今天的 CPU 路径同此）。
        (nint Handle, int W, int H) sharedPreviewSdr = default;
        Action? sharedReleaseSdr = null;
        if (config.WantSharedFrame && sSdrSlotEnabled && hdrSession is not null)
        {
            try
            {
                if (sdrSession.TryFreezeShared(out nint sdrHandle, out int slotW, out int slotH)
                    && slotW == sdrSession.Width && slotH == sdrSession.Height)
                {
                    sharedPreviewSdr = (sdrHandle, slotW, slotH);
                    sharedReleaseSdr = sdrSession.ReleaseSharedFrame;
                    Log($"[WGC] SDR 共享槽已冻结 {slotW}x{slotH}（本次不整帧回读）");
                }
                else
                {
                    // 槽没建好/尺寸不符：交还（TryFreezeShared 可能已置 held）并走今天的整帧回读
                    sdrSession.ReleaseSharedFrame();
                    Log($"[WGC] SDR 槽不可用（未建好/尺寸不符）⇒ 本次照常整帧回读");
                }
            }
            catch (Exception ex)
            {
                sdrSession.ReleaseSharedFrame();
                LogInfo($"[WGC] SDR 取槽失败: {ex.Message}（本次照常整帧回读）");
            }
        }

        if (sharedPreviewSdr.Handle == 0)
        {
            if (sdrSession.HasFrame)
            {
                sdrPixels = sdrSession.GetLatestSdr(_reuseSdr);
            }
            else
            {
                Log($"[WGC] SDR 等待首帧 (timeout={config.FrameTimeoutMs}ms)...");
                if (sdrSession.WaitForFirstFrame(config.FrameTimeoutMs))
                    sdrPixels = sdrSession.GetLatestSdr(_reuseSdr);
            }
            // 记下这次实际用的缓冲：尺寸变了（切分辨率）时 GetLatestSdr 会另分配，
            // 这里跟着更新，下一帧就能继续复用，不会卡在旧尺寸上反复重分配。
            _reuseSdr = sdrPixels;

            if (sdrPixels is null)
                throw new TimeoutException($"SDR 捕获超时（{config.FrameTimeoutMs}ms）。");
        }

        int w = sdrSession.Width, h = sdrSession.Height;

        // ═══ 2. 尝试 Float16 扩展范围捕获（HDR + 广色域 SDR 均适用）═══
        // 不限制 IsHdr：SDR 广色域显示器 (P3/AdobeRGB + ACM) 的 DWM 内部合成缓冲区
        // 也是扩展范围的，Float16 会话可以获取完整广色域数据。
        // 该不该建这个会话由 CanDeliverExtendedRange（逐屏 DXGI + 逐屏 DISPLAYCONFIG 高级颜色）决定；
        // 能力不够、或该屏正处于「连续空帧」软冷却中时，GetOrCreateHdrSession 返回 null。
        // 会话本身已在上面（与 SDR 建池同一批）建好，这里只负责到货与取像素。
        float[]? hdrPixels = null;
        // 路线 B：本帧冻结进共享槽的那块（句柄/尺寸 + 交还动作）
        (nint Handle, int W, int H) sharedPreview = default;
        Action? sharedRelease = null;
        if (hdrSession is not null)
        {
            try
            {
                if (!hdrSession.HasFrame)
                {
                    Log($"[WGC] Float16 等待首帧 (timeout={config.FrameTimeoutMs}ms)...");
                    hdrSession.WaitForFirstFrame(config.FrameTimeoutMs);
                }
                if (hdrSession.HasFrame)
                {
                    // 路线 B：取槽紧跟取像素（同一段、同一块冻结纹理）—— 预览画的与保存的是同一帧。
                    // 像素不在这里读：选区定下来才按矩形回读（ReadSharedRegion），整屏 CPU 回读不提前付。
                    bool frozen = false;
                    if (config.WantSharedFrame)
                    {
                        if (hdrSession.TryFreezeShared(out nint shHandle, out int shW, out int shH))
                        {
                            if (shW == w && shH == h)
                            {
                                sharedPreview = (shHandle, shW, shH);
                                sharedRelease = hdrSession.ReleaseSharedFrame;
                                frozen = true;
                            }
                            else
                            {
                                // 尺寸不符：下游只有 SDR 宽高的解释，交还槽、走常规取像素
                                hdrSession.ReleaseSharedFrame();
                                Log($"[WGC] 共享槽尺寸 {shW}x{shH} 与 {w}x{h} 不符 ⇒ 本次不走共享预览");
                            }
                        }
                    }
                    if (!frozen)
                        hdrPixels = hdrSession.GetLatestHdr(_reuseHdr);
                }

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

                // ⚠ 缓存更新必须排在尺寸校验【之后】：被丢弃的那份不该进复用槽。
                //   放前面也不会出错（GetLatestHdr 的 reuse 长度校验会挡住尺寸不符的复用），
                //   但那等于让复用槽长期停在一个"每次都要重新分配"的尺寸上，白付分配。
                //   只在"这份缓冲这次真被采用"时更新，槽才能一直跟住会话的真实尺寸。
                if (hdrPixels is not null) _reuseHdr = hdrPixels;

                // ═══ 2026-09-19：软冷却记账。口径是「这次到底成没成帧」，不是「最后像素留没留下」═══
                // 只有"没成帧"才会付满一次 FrameTimeoutMs，那正是冷却要治的东西；
                // 上面那种"出帧了但尺寸不合而被丢弃"的会话下一次走复用快路径（零等待），
                // 把它记成空帧就会让计数器永远清不掉 —— 判据选 HasFrame 而非 hdrPixels。
                NoteHdrAttemptOutcome(targetMonitor, hdrSession.HasFrame);

                // 共享槽与"这一帧"已在上面一步对齐（TryFreezeShared 紧跟取像素），没有第二处取槽 ——
                // "先取像素、再晚几十毫秒取槽"就是差一帧的那条路，别再把它加回来。
            }
            catch (Exception ex)
            {
                LogInfo($"[WGC] Float16 捕获失败: {ex.Message}（不影响 SDR 结果）");
            }

            // ═══ 配对纪律（2026-10-30）═══
            // SDR 槽已经冻上、HDR 却没冻成（没帧/尺寸不符/异常）⇒ 预览窗没有可采样的帧了
            //（它只认 HDR 槽）。这时必须交还 SDR 槽并补一次整帧回读，宁可多付 20–30ms，也不能让预览无帧。
            if (sharedPreviewSdr.Handle != 0 && sharedPreview.Handle == 0)
            {
                sdrSession.ReleaseSharedFrame();
                sharedPreviewSdr = default;
                sharedReleaseSdr = null;
                sdrPixels = sdrSession.GetLatestSdr(_reuseSdr);
                _reuseSdr = sdrPixels;
                if (sdrPixels is null)
                    throw new TimeoutException($"SDR 捕获超时（{config.FrameTimeoutMs}ms）。");
                LogInfo("[WGC] HDR 槽没冻成 ⇒ 交还 SDR 槽并整帧回读（预览只能吃 SDR 帧）");
            }
        }

        // ═══ 3. 获取 GPU 纹理（用于 GPU 直通编码路径）═══
        // 优先使用 SDR 会话的纹理，因为 NVENC 输入格式为 BGRA8。
        // 只在调用方真要时才取：选区预览拿到结果就 Dispose，这张快照没人读 ——
        // 白取一次就是每张截图付满一次整屏分配 + CopyResource（4K BGRA8 约 33MB）。
        ID3D11Texture2D? gpuTexture = null;
        if (config.WantGpuTexture)
        {
            try
            {
                gpuTexture = sdrSession.GetLatestTexture();
                Log($"[WGC] GPU 纹理获取: {(gpuTexture is not null ? "✓" : "✗")}");
            }
            catch (Exception ex)
            {
                Log($"[WGC] GPU 纹理获取失败: {ex.Message}（不影响编码结果）");
            }
        }
        else
        {
            // 保持 null 而不是"空纹理"：下游判据是 GpuTexture is not null，null 才是"没有"。
            Log("[WGC] GPU 纹理按需跳过: WantGpuTexture=false（调用方不消费，省略整屏快照拷贝）");
        }

        var result = new CaptureResult
        {
            SdrPixels = sdrPixels,
            HdrPixels = hdrPixels,
            Width = w,
            Height = h,
            GpuTexture = gpuTexture,
            SharedHandle = sharedPreview.Handle,
            SharedWidth = sharedPreview.W,
            SharedHeight = sharedPreview.H,
            SdrSharedHandle = sharedPreviewSdr.Handle,
            SdrSharedWidth = sharedPreviewSdr.W,
            SdrSharedHeight = sharedPreviewSdr.H,
            DegradationWarning = sdrSession.DegradationWarning // 传播降级警告 (来自会话)
        };
        result.SetSharedRelease(sharedRelease);   // 没被 TakeShared 认领时由 Dispose 交还
        result.SetSdrSharedRelease(sharedReleaseSdr);   // 同上；两块槽各自登记，漏一块就冻死一块

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
                FrameTimeoutMs = config.FrameTimeoutMs,
                WantSharedFrame = config.WantSharedFrame,  // 路线 B：单屏才有意义（多屏拼接的帧是虚拟桌面尺寸）
                WantGpuTexture = config.WantGpuTexture     // 原样下传：关掉后这里再取一份，前面省的就白省了
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
    //  完整性级别检测（E_ACCESSDENIED 的归因依据）
    // ═══════════════════════════════════════

    /// <summary>本进程是否以<b>提权</b>（High 完整性 / 管理员）令牌运行。
    /// <para>═══ 2026-10-06：为什么这件事必须单独判、单独说 ═══</para>
    /// <para>四条创建路径同灭时全是 <c>E_ACCESSDENIED (0x80070005)</c>，而这个 HRESULT
    /// 至少有三种互不相干的成因，处置完全不同（降权运行 / 换回正常桌面会话 / 退第三方软件）。
    /// 不把完整性读出来，日志里这三种长得一模一样，用户只会看到一句
    /// 「无法为显示器 0x… 创建 WGC 会话」——既说不出原因，也指不出任何可做的动作。</para>
    /// <para><b>本机实测（2026-10-06，两次独立验证）：</b>一个普通控制台进程（HIGH 完整性）
    /// 调同一个 <c>CreateForMonitor</c> 得到 <c>S_OK</c>，WGC 完全可用；而本程序被外部
    /// 以 <b>Low</b> 令牌启动时四路径全灭。⇒ <b>Low 完整性会挡住 WGC 是实测事实；
    /// 而"HIGH 会被挡"在本机没有成立过</b>（控制台臂就是 HIGH，照样成功）。
    /// 所以下面只把读数记下来交给上层措辞，不在代码里对成因下定论。</para>
    /// <para>判据来源是令牌的完整性级别（TokenIntegrityLevel），不是 <c>IsInRole(Administrator)</c>：
    /// 后者在 UAC 开启时对"未提权的管理员"也会返回 true，会把正常环境误报成故障。</para>
    /// </summary>
    private static bool? s_runningElevated;

    /// <summary>本进程是否提权运行（结果缓存：进程生命周期内不变）。</summary>
    internal static bool IsRunningElevated => s_runningElevated ??= DetectElevation();

    /// <summary>本进程令牌的完整性级别 RID（0x1000=Low / 0x2000=Medium / 0x3000=High / 0x4000=System）。
    /// <para>0 = 读不到（判据不可用，此时不得据此下任何结论）。</para></summary>
    internal static int IntegrityRid { get; private set; }

    /// <summary>读当前进程令牌的完整性级别是否为 High 以上。
    /// <para>═══ 2026-10-06：为什么"提权"与"低完整性"必须分开说 ═══</para>
    /// <para>受限令牌（<b>Low</b>，沙箱 / 受控宿主 / 某些串流与防护软件会强加）已被实测
    /// 证明会让 WGC 四路径全灭；<b>High</b> 则在本机实测中完全可用（控制台臂 HIGH ⇒ S_OK）。
    /// 两者在日志里都是同一个 <c>E_ACCESSDENIED</c>，所以读数必须打出来，
    /// 由上层按实际读数措辞，而不是在这里预设"提权就一定是凶手"。</para>
    /// <para>任何一步读失败都返回 false 并把原因写进 <see cref="ElevationDiagnostic"/>：
    /// "读不到"与"没提权"在布尔上是一样的，不把读数打出来就无法区分。</para>
    /// </summary>
    private static bool DetectElevation()
    {
        IntegrityRid = 0;
        nint token = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            { ElevationDiagnostic = $"OpenProcessToken 失败 err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"; return false; }
            // TokenIntegrityLevel = 25；先问长度再取，避免结构大小写错（写错会拿到 ERROR_BAD_LENGTH）
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out int need);
            if (need <= 0)
            { ElevationDiagnostic = $"GetTokenInformation(问长度) need={need} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"; return false; }
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(need);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, need, out int got))
                { ElevationDiagnostic = $"GetTokenInformation(取值) need={need} got={got} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"; return false; }
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                nint sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                if (sid == 0) { ElevationDiagnostic = "SID 指针为 0"; return false; }
                // SID 布局：rev(1) + subAuthCount(1) + identifierAuthority(6) + subAuthority[](每个 4B)
                byte count = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);
                if (count == 0) { ElevationDiagnostic = "SID 子权威数为 0"; return false; }
                // 直读最后一个子权威（完整性级别就在那一格）。不走 GetSidSubAuthority：
                // 多一个 P/Invoke 就多一个"返回指针被误用"的机会，而布局本身是固定的。
                int rid = System.Runtime.InteropServices.Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                IntegrityRid = rid;
                ElevationDiagnostic = $"完整性 RID=0x{rid:X}（{IntegrityName(rid)}）";
                return rid >= 0x3000;   // High / System ⇒ WGC 会拒
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { ElevationDiagnostic = $"异常 {ex.GetType().Name}: {ex.Message}"; return false; }
        finally { if (token != 0) CloseHandle(token); }
    }

    private static string IntegrityName(int rid) => rid switch
    {
        0x0000 => "Untrusted",
        0x1000 => "Low（受限令牌，WGC 同样会拒）",
        0x2000 => "Medium（正常桌面进程）",
        0x3000 => "High（提权，WGC 会拒）",
        0x4000 => "System",
        _ => "未知",
    };

    /// <summary>提权/完整性检测的内部读数（写进日志与用户提示）。
    /// <para>判据本身失败时必须能说出卡在哪一步，否则"检测不出来"会被当成"没提权" ——
    /// 那正好把用户引到错的方向。</para></summary>
    internal static string ElevationDiagnostic { get; private set; } = "(未运行)";

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int cls, nint info, int len, out int retLen);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    /// <summary>把"四条路径全灭"翻译成一句用户能照做的话。
    /// <para>三种情形分开说，因为处置完全不同：<b>低完整性/受限令牌</b>（要求换回正常
    /// 桌面会话——本机实测确认它会挡住 WGC）、<b>提权</b>（常见于 UAC 关闭的机器，
    /// 此时建议降权运行）、<b>都不是</b>（才轮到"被别的程序/驱动挡住捕获"那条通用解释）。</para>
    /// <para>⚠ 优先判 Low：本机实测里 Low 是<b>已证实的</b>成因，而 High 那一条是保守建议
    /// （本机控制台臂在 HIGH 下 WGC 照样成功，所以不能对用户断言"提权必然失败"）。</para>
    /// </summary>
    private static string DescribeItemFailure()
    {
        if (IntegrityRid is > 0 and < 0x2000)
            return "无法创建 WGC 捕获会话：本进程运行在【低完整性/受限令牌】下"
                 + $"（检测读数：{ElevationDiagnostic}），"
                 + "这种令牌不被允许读取桌面。请从正常桌面会话启动本程序"
                 + "（不要用沙箱、受限用户或受控宿主启动它）。";
        if (IsRunningElevated)
            return "无法创建 WGC 捕获会话：本程序正以【管理员/提权】身份运行"
                 + $"（检测读数：{ElevationDiagnostic}）。"
                 + "请以普通身份重新启动本程序（不要右键『以管理员身份运行』）；"
                 + "若系统关闭了 UAC（EnableLUA=0），所有进程都会是提权令牌，需开启 UAC 后重新登录。";
        return "无法创建 WGC 捕获会话：系统拒绝了屏幕捕获请求。"
             + $"（本进程完整性读数：{ElevationDiagnostic}。）"
             + "常见原因是另一个程序或显示驱动正在阻止捕获（远程串流 / 录屏 / 显卡叠加类软件、虚拟显示器驱动）。"
             + "可先退出这类程序再试；若仍然失败，请把本次日志一并反馈。";
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
        // ═══ 2026-10-06：四条路径同灭时，先分辨"低完整性 / 提权 / 被挡" ═══
        // 三者处置完全不同（换回正常会话 / 降权运行 / 退第三方软件），而在日志里
        // 长得一模一样（都是 E_ACCESSDENIED）。不做这项分辨就会把用户引向错误方向。
        // ⚠ 先判 Low：本机实测里 Low 是【已证实】的成因；High 那条是保守建议
        //   （控制台臂在 HIGH 下 WGC 照样 S_OK，故不能断言"提权必然失败"）。
        _ = IsRunningElevated;   // 触发检测，填好 IntegrityRid 与 ElevationDiagnostic
        if (IntegrityRid is > 0 and < 0x2000)
            LogInfo("[WGC] ⛔ 归因：本进程运行在【低完整性/受限令牌】下 ⇒ 不被允许读取桌面，"
                  + "四条路径必然全部 E_ACCESSDENIED。请从正常桌面会话启动本程序。"
                  + $"读数：{ElevationDiagnostic}");
        else if (IsRunningElevated)
            LogInfo("[WGC] 归因：本进程【提权运行】⇒ 建议以普通身份重试（本机控制台臂证明 HIGH 下"
                  + "WGC 也可用，故此处只作建议、不断言必然失败）。"
                  + $"读数：{ElevationDiagnostic}");
        else
            LogInfo($"[WGC] 归因：完整性正常（{ElevationDiagnostic}）⇒ 拒绝来自系统/第三方捕获防护"
                  + "（串流、录屏、显卡叠加或虚拟显示器驱动）。");
        s_lastItemFailureMessage = DescribeItemFailure();
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

// ═══════════════════════════════════════════════════════════════════════════
//  性能仪表的公共算法（2026-10-31 批 4 追加：F5「staging 重建 / _poolLock 持锁」、
//  F4「预览窗逐帧与 3s 静置唤醒」）
// ═══════════════════════════════════════════════════════════════════════════
// 只做一件事：把 Stopwatch 读数原子累加进【调用方自己的】字段。本类没有任何可变静态状态
// （今天审查点名的 `_engines` 那类形状 ⇒ 刻意避开）：计数器的宿主依次是
//   PooledSession（每屏每精度档一条）/ WgcCaptureService（DI 单例）/ HdrCaptureWindow（每扇窗一份）/
//   CapturePipelineService（DI 单例，一次保存累加一次），
// 所以"并发截图会怎样"这件事的答案是按宿主分的：
//   · 同一宿主被多线程进入（帧回调 FreeThreaded、预览渲染线程、后台保存腿）⇒ 全部走 Interlocked，
//     峰值用 CAS 环；交界处最多丢一次峰值，不会丢总数、不会把两次的账并成一条；
//   · 不同宿主（两块屏 / 两扇预览窗 / 两次保存）⇒ 各记各的账，结算行点名自己的宿主身份
//     （会话点名 HMONITOR+精度档，窗口点名客户区尺寸，保存点名格式），不共享桶。
// 单位一律【微秒】存 long：4K 一次区域回读常在 1ms 以下，按毫秒记账会整列读成 0。
internal static class PerfMeter
{
    /// <summary>QPC 时间戳（不分配）。</summary>
    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>距 <paramref name="t0"/> 的微秒数（QPC 差值，与挂钟/调整无关）。</summary>
    public static long SinceMicro(long t0)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>仪表落盘闸：默认【简】档一行不写 —— 计数照常累加（几纳秒），但绝不产生磁盘 IO。
    /// 这一条是硬要求：LogService 的 writer 是 AutoFlush=true 的 StreamWriter，落盘一次就是一次 WriteFile，
    /// 它不能被放在捕获/渲染的逐帧路上，也不能在默认档把日志撑开。</summary>
    public static bool DumpEnabled => CoreLog.Detail >= LogDetail.Diagnose;

    public static string Ms(long microSeconds) => $"{microSeconds / 1000.0:F2}ms";

    /// <summary>一次事件记账：次数 +1、合计 += deltaUs、峰值按 CAS 更新。数组版（多站点共用）。</summary>
    public static void Record(long[] n, long[] totalUs, long[] maxUs, int site, long deltaUs)
    {
        Interlocked.Increment(ref n[site]);
        Interlocked.Add(ref totalUs[site], deltaUs);
        UpdateMax(ref maxUs[site], deltaUs);
    }

    /// <summary>一次事件记账（单字段版，含次数）。</summary>
    public static void Record(ref long n, ref long totalUs, ref long maxUs, long deltaUs)
    {
        Interlocked.Increment(ref n);
        Interlocked.Add(ref totalUs, deltaUs);
        UpdateMax(ref maxUs, deltaUs);
    }

    /// <summary>一次事件记账（只记毫秒账，次数由调用方自己 +1）。</summary>
    public static void Record(ref long totalUs, ref long maxUs, long deltaUs)
    {
        Interlocked.Add(ref totalUs, deltaUs);
        UpdateMax(ref maxUs, deltaUs);
    }

    private static void UpdateMax(ref long maxUs, long deltaUs)
    {
        long cur;
        while (deltaUs > (cur = Interlocked.Read(ref maxUs)))
            if (Interlocked.CompareExchange(ref maxUs, deltaUs, cur) == cur) break;
    }

    /// <summary>staging 纹理新建的账（耗时 + 向驱动申请的字节数）。尺寸即体积：4K 8-bit 单块实测 42.2MB。</summary>
    public static void AddStagingCreate(ref long createUs, ref long createMaxUs, ref long bytes,
        long deltaUs, int w, int h, Vortice.DXGI.Format format)
    {
        Interlocked.Add(ref createUs, deltaUs);
        UpdateMax(ref createMaxUs, deltaUs);
        int bpp = format == Vortice.DXGI.Format.R16G16B16A16_Float ? 8 : 4;
        Interlocked.Add(ref bytes, (long)w * h * bpp);
    }
}
