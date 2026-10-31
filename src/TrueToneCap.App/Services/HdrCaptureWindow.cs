// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/HdrCaptureWindow.cs
// 全 D3D11 HDR 截图预览窗口
// 桌面帧: D3D11 全屏纹理渲染 (scRGB Float16)
// UI 覆盖层: CPU 软件渲染 BGRA8 → D3D11 Alpha 混合合成
// 交互: 原生 Win32 鼠标/键盘消息

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vortice.Direct3D11;
using Vortice.DXGI;
using TrueToneCap.Core.Detection;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Processing;   // D3DDeviceLost（设备丢失判定）
using TrueToneCap.Core.Logging;   // LogLevel / LogCategory（定义已移到 Core，Core 也要能写日志）
using TrueToneCap.App.Models;        // ToolbarContract / MainToolbarAction（2026-10-26 契约单源化）

namespace TrueToneCap.App.Services;

public sealed partial class HdrCaptureWindow : IDisposable
{
    /// <summary>烘字图集的工具条字号（物理像素 = 此值 × _uiScale）。图集 / CompositeUI 提示框宽 / GDI 回退 / 预热四处必须同源。</summary>
    private const float ToolbarFontSize = 18f;

    private nint _hwnd;
    private int _winX, _winY, _winW, _winH;
    private volatile bool _disposed;
    private volatile bool _closing; // ═══ 2026-08-16 P1-2: Close 后渲染循环感知退出 ═══
    // ═══ 2026-09-15: 设备丢失后可重建，故不再 readonly ═══
    // 原为 readonly ⇒ 设备丢失后无法替换，只能整个窗口重建（用户可见闪烁 + 丢失选区）。
    private ID3D11Device _device;
    private ID3D11DeviceContext _ctx;

    // ── 阶段 4（2026-10-30）：进程级设备复用 ──
    // 每张截图一扇窗，原先每扇窗都 D3D11CreateDevice（本机实测 33–57ms）。设备与窗口无关
    // （交换链/纹理才是每窗的），收成进程级惰性单例后第 2 扇起不再付这笔钱。
    // 三条纪律（与 Win2D 的 SharedCanvasDevice 同口径）：
    //   ① 共享设备与它的 ImmediateContext【永不】由某扇窗 Dispose —— 缓存在进程上，谁建谁收；
    //   ② 设备丢失时由第一个发现的窗口把缓存整体换新，其余窗口在重建自己资源时拿到同一块新设备；
    //   ③ ImmediateContext 非线程安全 ⇒ RenderCore 入口先拿 s_ctxLock（这是复用唯一新增的并发面；
    //      产品路径同一时刻只有一扇窗在渲染，探针的多窗是串行的）。
    private static readonly object s_ctxLock = new();
    private static readonly object s_deviceCacheLock = new();
    private static ID3D11Device? s_cachedDevice;
    private static ID3D11DeviceContext? s_cachedCtx;
    private static readonly bool s_sharedDeviceEnabled =
        Environment.GetEnvironmentVariable("TTC_PREVIEW_SHARED_DEVICE") != "0";
    /// <summary>这块设备是不是本窗自己建的（自建 ⇒ Dispose 时要收；共享 ⇒ 放手不收）。</summary>
    private bool _ownsDevice;

    // ── D3D 设备丢失恢复 (2026-09-15) ──
    private volatile bool _deviceLost;            // 已检测到丢失（防止每帧重复处理/刷屏）
    private int _deviceLostRecoveryAttempts;      // 已尝试重建次数
    private const int MaxDeviceLostRecovery = 2;  // 上限：连续失败说明驱动层面有问题，不再自愈
    private int _renderFrameSeq;                  // 渲染帧序号（用于周期性设备探测）
    private volatile bool _renderThreadStuck;     // 渲染线程未能在超时内退出（卡在 D3D 调用中）
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    // ═══ 2026-09-16：_ps 字段已删除 ═══
    // 原 _ps（ToneMapping PS）是死代码：创建后从未 PSSetShader ——
    // 本文件唯一的 PSSetShader（RenderGpuComposite）绑的是 _overlayPs。
    // 保留它只会误导后来者以为存在一条"GPU 直通拷贝/色调映射"路径。
    // （真正承担色调映射的是 Core 的 ToneMapper，走 CPU。）

    // ═══ 2026-08-16 方案A: GSYNC/FreeSync (VRR) 支持 ═══
    // 检测到允许撕裂时交换链加 AllowTearing + Present(Tear), 让 DWM 在 VRR 面板
    // 刷新窗口内任意相位呈现 → 选区拖拽更平滑、输入延迟更低。不支持时回退 Present(0)。
    /// <summary>中心行亮度打点的一次性开关（0=未打点）。</summary>
    private int _diagSampleDone;
    /// <summary>首帧合成打点的一次性开关（0=未打点）。渲染线程独占访问。</summary>
    private int _diagFirstCompositeDone;
    /// <summary>文字图集首建里"CPU 排版 + 读回"那一段的耗时（ms）；余下的是上传。由首帧合成那条打点一并交出。</summary>
    private long _rasterMsAtlas;
    /// <summary>文字图集首建的总耗时（ms），0 = 这一帧没重建图集。</summary>
    private long _atlasTotalMsAtlas;
    /// <summary>预热那一趟排版自己花掉的毫秒数（<b>不</b>在首帧关键路径上），0 = 这次没吃到预热。</summary>
    private long _atlasPrewarmMsAtlas;
    /// <summary>图集预热【投递】的时刻（TickCount64）。与 <see cref="_atlasPrewarmStartedAtMs"/> 相减=任务排队。</summary>
    private long _atlasPrewarmQueuedAtMs;
    /// <summary>图集预热【真正开始跑】的时刻（TickCount64，由后台线程在 CPU 腿第一句盖）。渲染线程 Wait() 之后读到的是 happens-before 保证的值。</summary>
    private long _atlasPrewarmStartedAtMs;
    /// <summary>首帧图集被拆成的三段（ms）：任务排队（投递→开跑）、渲染线程等它跑完、纯上传。
    /// 修前真机上传单独是 12ms；改后图集整腿读到 43ms 却分不出是"排队排晚了"还是"上传变慢"，这三段就是那把尺。</summary>
    private long _atlasQueueMsAtlas, _atlasPrewarmWaitMs, _atlasUploadMs;
    /// <summary>预热那一趟【排版】自己再拆成的三段（ms）：画（建目标 + 几十条 DrawText）、读回（11.5MB GPU→CPU）、
    /// 并入（逐行 BlockCopy 文字图层预览）。三段之和 ≈ <see cref="_atlasPrewarmMsAtlas"/>。
    /// 为什么要拆：整腿 34–84ms 里"读回"若是大头，出路是让图集生在 GPU 里（换拷贝形状）；
    /// "画"若是大头，出路是整张缓存跨窗复用 —— 两条修法互斥，先要有尺。</summary>
    private long _atlasPrewarmDrawMs, _atlasPrewarmReadMs, _atlasPrewarmMergeMs;
    /// <summary>开窗时投递出去的图集 CPU 腿（见 <see cref="BuildAtlasLayout"/>）。渲染线程取走并置空。</summary>
    private System.Threading.Tasks.Task<AtlasLayout>? _pendingAtlasLayout;
    /// <summary>那次预热所依据的 DPI 缩放 —— 与当前值不一致就作废。</summary>
    private double _pendingAtlasScale;

    private bool _vrrSupported; // 仅用于启动时诊断输出（Present 路径已固定 Present(0)）
    // ═══ 2026-08-16 增强: 多平面覆盖层 (MPO) / 硬件缩放合成支持 (独立翻转前提) ═══
    // 窗口化 DirectFlip 由 DWM 自动协商, 但可查询确认 — 诊断日志供排障。
    private bool _mpoWindowed;
    private bool _mpoFullscreen;

    // ── GPU 合成 (Phase 2, 2026-08-11): OverlayComposite 着色器替代 CPU CompositeUI ──
    private ID3D11PixelShader? _overlayPs;
    private ID3D11Buffer? _uiCb;                 // 常量缓冲 (UI 状态)
    private ID3D11Texture2D? _textAtlas;         // 工具栏文字图集
    private ID3D11ShaderResourceView? _textAtlasSrv;
    private double _atlasScale;                  // 图集对应的 DPI 缩放 (失效重建)
    private bool _useGpuCompositor;              // GPU 合成可用标志 (失败回退 CPU)
    // ── 选区尺寸徽标：独立小动态纹理（拖拽时每帧可能改字，放图集里就要 5MB 级全量重建）──
    private const int BadgeTexW = 512, BadgeTexH = 128;
    private ID3D11Texture2D? _badgeTex;
    private ID3D11ShaderResourceView? _badgeSrv;
    private string _badgeText = "";
    private double _badgeScale;
    private int _badgeW, _badgeH;
    // ── 输入中的文字预览：EDIT 子窗口在这块覆盖层上不可见，只能把它的内容烘成小纹理画进帧 ──
    private const int EditTexW = 1024, EditTexH = 1024;
    private ID3D11Texture2D? _editTex;
    private ID3D11ShaderResourceView? _editSrv;
    private readonly object _editPreviewLock = new();
    private string _editPrevKey = "";                 // 上次烘的 (文字+字形+宽) 指纹；只在 _editPreviewLock 里读写
    private byte[]? _editPendingBgra;                 // UI → 渲染线程 待上传位图
    private int _editPendingW, _editPendingH;
    private bool _editPendingUpload;
    private int _editPrevW, _editPrevH;               // 渲染线程独占：当前纹理里那张位图的尺寸（UI 线程不读）
    private int _editFrameX, _editFrameY;         // 输入框在帧内的左上角（预览贴同一位置）
    private int _idleHintW = 1, _idleHintH = 1;   // 静置提示在图集里的像素尺寸（随 DPI 重建）
    private ID3D11SamplerState? _samp;
    private ID3D11Texture2D? _desktopTex;
    private ID3D11ShaderResourceView? _desktopSrv;
    // ═══ 2026-10-30 路线 B：桌面帧可能来自 WGC 会话的跨设备共享纹理（整帧不进 CPU）═══
    // _sharedHandle/_sharedRelease 由 UI 线程在 Initialize 之前挂上；其余只在渲染线程用。
    // _desktopTex 在共享模式下【就是】打开的那块共享纹理（我们持一份引用，Dispose 只减引用）。
    private nint _sharedHandle;
    private int _sharedW, _sharedH;
    private bool _sharedAttached;
    private Action? _sharedRelease;      // 交还共享槽（窗口 Dispose 时调一次）
    private Func<float[]?>? _sharedFallback;   // 共享纹理打不开时现取一份整帧（路线 B-3，只在该失败路径用）
    private bool _sharedFaultLogged;
    // ⚠ 不能拿 _desktopTex 非空当"已经打开了共享纹理"：Initialize 会预建一块同尺寸的黑纹理
    // （2026-08-25 的预热优化），那样判会让共享路径直接把它当桌面画出来 —— 整屏纯黑，且两头都没有日志。
    private ID3D11Texture2D? _sharedTexOpen;
    // ── 池化 Staging 纹理 (2026-08-10: 消除每帧 CreateTexture2D ~32MB GPU 分配) ──
    private ID3D11Texture2D? _pooledStaging;
    private int _pooledStW, _pooledStH;
    private bool _hasFrame;

    /// <summary>原始捕获帧（像素 + 其真实宽高）。不可变，供 UI 线程与渲染线程间原子传递。</summary>
    private sealed class FrameData
    {
        public readonly float[] Pixels;
        public readonly int W, H;
        public FrameData(float[] pixels, int w, int h) { Pixels = pixels; W = w; H = h; }
    }

    // ═══ 帧状态原子发布 ═══
    // 渲染线程会并发读取"像素数组 + 其宽高"。三者若为独立字段，渲染线程可能观察到
    // "新数组 + 旧尺寸"的撕裂组合 → 按错误的 w*h*4 索引数组 → 托管堆越界读写。
    // 打包为单个不可变对象后通过 Volatile.Write/Read 发布/读取，杜绝撕裂。
    private FrameData? _frame;     // 原始帧（不可修改）+ 其真实尺寸
    private float[]? _compPixels; // 合成缓冲（按窗口尺寸分配，见 CompositeUI）
    // ── 跨线程可见性：UI 线程写、渲染线程读，加 volatile 保证内存屏障 ──
    private volatile int _dragHandle = -1; // 当前拖拽的手柄 (0=TL, 1=TR, 2=BL, 3=BR, -1=无)
    private volatile int _dragOffX, _dragOffY; // 手柄抓取偏移：按下点相对该角的位移（不存就一直"角跳到手心"）
    private volatile bool _movingSel;          // 在已完成选区内部拖拽 = 平移整框
    private volatile int _moveOffX, _moveOffY; // 平移抓取偏移：按下点相对选区左上角的位移
    private volatile int _handleSize = 8; // 手柄尺寸（物理像素，随 DPI 缩放）
    private volatile int _handleHit = 12; // 手柄命中区域（物理像素，随 DPI 缩放）
    private double _uiScale = 1.0; // DPI 缩放系数（GetDpiForWindow / 96）——仅 Initialize 写，渲染前已发布，无需 volatile
    private volatile bool _selComplete;
    private volatile int _sx1, _sy1, _sx2, _sy2, _mx, _my;
    private volatile bool _down, _moved;
    private bool _toolClickLatch;   // 这次按下被标注工具条/选项条/下拉吃掉了 ⇒ Up 只许收尾，不许重画选区（只走 UI 线程）

    // ── 标注模式 (2026-08-11 A方案: GPU 内联标注) ──
    private volatile bool _annoMode;                      // 是否处于标注模式
    private volatile string _annoTool = "Rect";           // 当前标注工具
    private volatile bool _annoDown;                      // 标注拖拽中
    private System.Numerics.Vector2 _annoDragStart; // 标注拖拽起点 (选区图像坐标)
    private readonly List<System.Numerics.Vector2> _penPoints = []; // 画笔轨迹
    private readonly object _penPointsLock = new(); // 保护 _penPoints 跨线程访问 (UI 写/渲染线程读)
    private readonly AnnotationManager _annoManager = new();       // 标注图层管理 (撤销/重做)
    private nint _editHwnd;                      // 文字输入 EDIT 子窗口
    private System.Numerics.Vector2 _textInsertPos; // 文字插入位置 (选区图像坐标)
    private bool _annoTextSlotsDirty;            // 文字槽图集需要重建

    // ── 文字标注：字体状态 / 就地再编辑 / 拖动 (2026-10-27 对齐上游 PR 的富文本能力) ──
    private string _textFontFamily = "Microsoft YaHei UI";
    private float _textFontSizeDip = 18f;        // 界面单位（dip）；图层里存物理像素 = dip × _uiScale
    private bool _textFontBold, _textFontItalic;
    private TextLayer? _editingLayer;            // 正在再编辑的图层（null = 新建）
    private nint _editFont;                      // EDIT 当前 HFONT（销毁时 DeleteObject）
    private bool _textClickArmed;                // 按下在选区内，抬起仍没拖动 ⇒ 打开输入框
    private TextLayer? _textHitLayer;            // 按下命中的文字层（拖动 = 移动它）
    private int _textDownX, _textDownY;          // 按下点（帧坐标）
    private float _textLayerStartX, _textLayerStartY; // 命中图层的原始位置（拖动预览用）
    private bool _textDragging;                  // 已越过拖动阈值
    private bool _textDragMoved;                 // 位置真的变了 ⇒ 抬手上报一条 UpdateLayer（撤销点）
    // 字体/字号下拉（也画进 D3D 帧：Win32 组合框在这块覆盖层上一像素都不显示，见下方说明）
    private int _ddOpen;                         // 0=关 1=字体家族 2=字号
    private int _ddTop;                          // 列表首个可见项的绝对下标
    private int _ddHover = -1;                   // 悬停项的绝对下标（-1 = 无）
    private bool _uiTextDirty;                   // 动态文字（字体名/字号/列表项）需要重烘图集
    private string[]? _fontFamilies;             // 系统字体名缓存（首次展开时取一次）
    private const int DdMaxVisible = 12;
    // 文字图集尺寸与最下方那条动态文字带（40-43 固定格 + 下拉项）。带宽固定，格高随 DPI 长，
    // 所以"能装几项"不是常量 —— 必须按 PackTextBand 实测容量夹可见行数（见 DdBandCapacity）。
    private const int AtlasW = 2048, AtlasH = 1408, AtlasBandH = 256;
    private volatile int _ddBakedRows;           // 本轮图集真烘出来的下拉行数（探针读它对比 DdGeometry.Visible）
    private int _ddCapLogged = -1;               // 上次记录过的文字带容量（只在容量变化时写一条日志）

    // ── 第二行工具选项条（线色 / 马赛克方式）──
    // ⚠ 2026-10-28 实测：这块全屏覆盖层的交换链位于合成平面之上，同进程的 Win32 子控件
    // （按钮/组合框/编辑框）和目标窗口都一个像素都不显示 —— 连独立顶层弹窗也被盖住。
    // 因此第二行改为画进 D3D 帧（几何/状态经 TextPos[38]/[39] 传给 OverlayComposite.hlsl），
    // 命中检测在 Down() 里做。不要再退回 Win32 子控件。
    private Color4 _annoColor = new(1f, 0.16f, 0.16f, 1f);   // 线色（默认红）
    private float _annoStrokeW = 2f;                        // 线宽（图层坐标 = 像素）
    private string _mosaicMode = "Rect";                    // Rect | Brush
    private bool _optRowVisible;
    private int _optHover = -1;                             // 第二行悬停格（-1 = 无）
    private const int IdOptColorCount = 6, IdOptMosaicCount = 2;
    // 马赛克画笔粗细（图像像素）：第二行里是一条可左右拉的滑条，1..100 px 直接写进图层 BrushWidth
    private const float MosaicBrushMinPx = 1f, MosaicBrushMaxPx = 100f;
    private float _mosaicBrushPx = 28f;
    private bool _sliderDrag;                              // 正拖着粗细滑条
    private const string MosaicModeRectText = "区域框选", MosaicModeBrushText = "画笔涂抹";
    private static readonly Color4[] s_swatchColor =
    [
        new(1f, 40 / 255f, 40 / 255f, 1f),
        new(1f, 215 / 255f, 0f, 1f),
        new(40 / 255f, 200 / 255f, 90 / 255f, 1f),
        new(60 / 255f, 140 / 255f, 1f, 1f),
        new(1f, 1f, 1f, 1f),
        new(0f, 0f, 0f, 1f),
    ];
    /// <summary>标注管理器（MainWindow 保存/复制时读取层做 CPU 合成输出）。</summary>
    public AnnotationManager AnnoManager => _annoManager;

    // ── 专用渲染线程：UI 线程(WndProc)只更新状态+发信号，渲染在后台线程执行 ──
    // 根因: 4K 全屏 CPU 合成 100ms+/帧，若在 UI 线程执行会占满消息泵导致整个应用无响应
    private Thread? _renderThread;
    private readonly AutoResetEvent _renderSignal = new(false);

    // 窗口识别（QQ截图式快速选择）
    private List<DetectedRegion> _regions = [];
    private int _hoverRegion = -1; // 当前悬停的窗口索引
    private string? _hoverTitle;   // 已渲染到图集槽 26 的标题（变化时局部更新）
    private int _hoverTipW = 200, _hoverTipH = 28; // 悬停标题 tooltip 屏幕尺寸 (物理像素, 随 DPI)
    private System.Threading.Timer? _regionRefreshTimer; // 窗口检测定时刷新 (QQ截图式实时)

    // ═══ F4 仪表（2026-10-31 批：预览窗逐帧与 3s 静置唤醒，只测不改）═══
    // 要回答的问题（审查 §3-F4 / §9「F4 整族仍未量」）：
    //   ① 一帧 RenderCore 到底多少钱（今天只有首帧那一行有数）；
    //   ② FlipSequential 下每帧 GetBuffer→RTV 重建到底触发了几次（审查说"每帧"，无人数过）；
    //   ③ 静置时每 3s 那次唤醒干了什么：EnumWindows 的耗时，以及它顺手 RequestRedraw 引出的那一帧；
    //   ④ 悬停换标题逼出来的整张图集重排（2048×1408）发生了几次、每次多少毫秒（今天只给首帧计时）。
    // 口径与并发（今天点名过 `_engines` 那类"静态可变跨调用状态"，这里刻意不用静态桶）：
    //   计数器全是**每扇窗自己的实例字段** ⇒ 两扇窗重叠（探针"旧窗未 Dispose、新窗已渲染"那种）各记各的账，
    //   结算行随窗走、点名自己的客户区尺寸。写入者有两个线程（渲染线程与定时器线程池回调），
    //   所以一律 Interlocked（峰值 CAS 环）；单位微秒 —— 一帧常在 1ms 以下，按毫秒会整列读成 0。
    // 逐帧一律零日志：只在 Close 结算一行，且只有诊断档落盘（默认简档连这一行都不写）。
    private long _mFrames;             // RenderCore 帧数
    private long _mFrameUs;            // 帧耗时合计
    private long _mFrameMaxUs;         // 帧耗时峰值
    private long _mIdleWakeFrames;     // 其中由 3s 静置定时器唤醒的那几帧
    private long _mIdleWakeUs;
    private long _mIdleWakeMaxUs;
    private long _mRtvRebuilds;        // EnsureBackBuffer 真的换缓冲+建 RTV 的次数（早退不算）
    private long _mRegionTicks;        // 3s 定时器进回调的次数
    private long _mRegionDetectUs;     // 回调里 EnumWindows 一趟的耗时合计
    private long _mRegionDetectMaxUs;
    private long _mRegionRedraws;      // 回调里真的发了 RequestRedraw 的次数（未框选时才发）
    private long _mAtlasRebuilds;      // 图集重建次数（含首建与 hover/脏标记逼出来的重排）
    private long _mAtlasUs;
    private long _mAtlasMaxUs;
    private long _mHoverTitleSwitches; // 标题变化逼整张图集作废的次数
    private int _mIdleWakePending;     // 1 = 下一次 RenderCore 是静置唤醒引出的那一帧
    private long _mRegionRedrawsAfterClose;  // 关闭后再被唤醒（应当恒 0；非 0 说明定时器没停干净）
    private int _mDumped;              // 结算行每扇窗只打一次（Close 与 Cleanup 都会走到 StopRenderThread）
    private readonly long _mOpenedAtQpc = System.Diagnostics.Stopwatch.GetTimestamp();  // 本窗存活起点（算帧率的分母）

    // 自动超时关闭
    private System.Threading.Timer? _autoTimer;
    private const int AutoTimeoutMs = 60_000; // 1 分钟无操作自动关闭
    // ── 硬看门狗：3 分钟无论什么情况强制退出（UI 卡死兜底）──
    private System.Threading.Timer? _hardTimer;
    private const int HardExitMs = 3 * 60 * 1000;
    private System.Diagnostics.Stopwatch _idleWatch = System.Diagnostics.Stopwatch.StartNew();

    public event Action<MainToolbarAction, int, int, int, int>? ActionCompleted;
    public bool IsInitialized { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>本实例自己的窗口句柄（0 = 未建或已关闭）。探针按实例取窗，不按类名找 —— 全屏覆盖层
    /// 可能同时存在多扇（上一相没来得及销毁 / 用户自己开着的预览窗），按类名 FindWindow 会打错窗。</summary>
    internal nint Hwnd => _hwnd;

    private static bool _classReg;
    private const string CN = "TTC_HC";
    private static WndProcDelegate? _staticWndProc;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, HdrCaptureWindow> s_windows = new();
    private const int GWLP_USERDATA = -21;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc cb, nint l);
    private delegate bool EnumWindowsProc(nint h, nint l);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint ex, string cls, string t, uint s, int x, int y, int w, int h, nint p, nint m, nint i, nint lp);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint h, nint a, int x, int y, int cx, int cy, uint f);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCapture(nint h);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int k);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? n);
    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint h, nint c);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint h, ref PAINTSTRUCT ps);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
    // 选区起点预热时窗口还没建、拿不到 HWND，只能按显示器查 DPI（Win8.1+ / shcore.dll）。
    // 0 = MDT_EFFECTIVE_DPI，与 GetDpiForWindow 返回的是同一把尺（不相同时键会对不上，预热就白烘）。
    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint hmon, int dpiType, out uint dpiX, out uint dpiY);
    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint h);
    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint p, nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);
    // ⚠ 必须用 StringBuilder：`char[]` 在 [DllImport] 下默认按 In 数组封送，原生写进缓冲的内容
    // 不会回到托管侧 —— 2026-10-27 实机探针测到"文字图层内容全是 NUL、字号控件读回恒为空串"
    //（GetWindowText 返回了正确长度 9，但数组里 9 个 '\0'）。这个坑同时打坏了输入框提交与字体控件。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextSb(nint h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint h, string text);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageInt(nint h, uint msg, nint w, nint l);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW { public uint style; public nint proc; public int cbCls; public int cbWnd; public nint inst; public nint icon; public nint cur; public nint bg; public string? menu; public string? cls; }
    // ⚠ 必须与 Win32 PAINTSTRUCT 完全同构（x64 下 72 字节）。
    // 之前只声明了前 3 个字段（32 字节），BeginPaint 会写满整个结构
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }

    // （含 fRestore/fIncUpdate/rgbReserved[32]）→ 每次 WM_PAINT 溢出约 40 字节破坏栈帧。
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rc;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    private delegate nint WndProcDelegate(nint h, uint m, nint w, nint l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);

    private const uint WS_POPUP = 0x80000000, WS_VIS = 0x10000000;
    // WS_EX_TOPMOST | WS_EX_TOOLWINDOW
    // 注意: 不能用 WS_EX_NOACTIVATE（键盘失效）和 WS_EX_TRANSPARENT（鼠标穿透）
    // ═══ 2026-08-26 关键修复: 移除 WS_EX_NOREDIRECTIONBITMAP ═══
    // NOREDIRECTIONBITMAP 窗口没有 redirection surface, DWM 不能从 CreateSwapChainForHwnd
    // 获取 swap chain 内容 → DWM 呈现透明 → 用户看到实时桌面而非截图内容。
    // 移除后窗口有正常 redirection surface, DWM 合成 swap chain 内容到屏幕。
    private const uint WS_EX = 0x00000008 | 0x00000080;
    private const uint SWP_F = 0x0040 | 0x0010;
    private static readonly nint TOP = new(-1), CROSS = new(32515);
    private const uint WM_PAINT = 0x000F, WM_ERASE = 0x0014, WM_KEY = 0x0100, WM_LD = 0x0201, WM_LU = 0x0202, WM_MM = 0x0200;
    private const uint WM_CHAR = 0x0102, WM_KEYUP = 0x0101, WM_PASTE = 0x0302, WM_CUT = 0x0300, WM_UNDO = 0x0304;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_CLOSE = 0x0010; // Alt+F4 / 系统关闭
    private const uint WM_APP_TIMEOUT = 0x8000 + 1; // 超时消息（线程池 Timer → 窗口消息，由 UI 线程处理）
    private const uint WM_SETFONT = 0x0030;
    private const int VK_ESC = 0x1B, VK_ENT = 0x0D, VK_S = 0x53, VK_C = 0x43;
    private const int VK_Z = 0x5A, VK_Y = 0x59, VK_B = 0x42, VK_I = 0x49, VK_F = 0x46;
    private const int VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD, VK_SHIFT = 0x10;
    // 编辑框消息
    private const uint EM_SETSEL = 0x00B1;
    // EDIT 子窗口 / 子控件常量
    private const uint WS_CHILD = 0x40000000, WS_BORDER = 0x00800000;
    private const uint ES_AUTOHSCROLL = 0x0080, ES_MULTILINE = 0x0004, ES_WANTRETURN = 0x1000;
    private const uint ES_AUTOVSCROLL = 0x0040, ES_NOHIDESEL = 0x0100;
    private const int GWLP_WNDPROC = -4;
    private bool _actionFired; // ActionCompleted 是否已触发（防止 WM_CLOSE 等兜底路径重复触发）
    private static int GX(nint l) => (short)(l.ToInt32() & 0xFFFF);
    private static int GY(nint l) => (short)((l.ToInt32() >> 16) & 0xFFFF);

    // ── GPU 合成常量缓冲 (与 OverlayComposite.hlsl cbuffer UIState 严格对应, 16B 对齐) ──
    // ── 光栅化状态: CullMode.None 双保险 (VS 绕序已改 CW, 此防御任何绕序问题) ──
    private ID3D11RasterizerState? _rsNoCull;
    [StructLayout(LayoutKind.Sequential)]
    private struct UiCbData
    {
        public System.Numerics.Vector4 OverlayColor;  // 遮罩色 (线性 RGB + A)
        public System.Numerics.Vector4 BorderColor;   // 边框色
        public System.Numerics.Vector4 SelRect;       // 选区 (x,y,w,h)
        public System.Numerics.Vector4 ToolbarRect;   // 工具栏背景
        public System.Numerics.Vector4 ButtonLayout;  // (btnW,btnH,gap,btnCount)
        public System.Numerics.Vector4 PadHandle;     // (padX,padY,handleSize,0)
        public System.Numerics.Vector4 MousePos;      // (mx,my,0,0)
        public System.Numerics.Vector4 Flags;         // (selComplete,down,0,dark)
        public System.Numerics.Vector4 FrameSize;     // (w,h,0,0)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 224)]
        public float[] TextSubs;  // 56 槽 × 4 (0-6 主按钮, 8-17 标注, 18 标注提示, 19-26 文字, 27 悬停标题, 28-34 主逐钮提示,
                                  //            36 静置, 37 主提示框, 38/39 第二行, 40-43 文字行格子, 44-55 下拉列表项)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 224)]
        public float[] TextPos;   // 56 槽 × 4
        public System.Numerics.Vector4 AnnoState;     // (mode, tool, down, 0)
        public System.Numerics.Vector4 AnnoColor;     // 标注颜色 (线性 RGBA)
        public System.Numerics.Vector4 AnnoStroke;    // (strokeW, opacity, 0, 0)
        public System.Numerics.Vector4 HoverRect;     // 悬停窗口矩形 (x,y,w,h); w<=0 无悬停
        public System.Numerics.Vector4 SizeBadgeSub;  // 尺寸徽标纹理子矩形 (u0,v0,du,dv); w<=0 不画
        public System.Numerics.Vector4 SizeBadgePos;  // 尺寸徽标帧内位置 (x,y,w,h)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
        public float[] AnnoLayers; // 256 槽 × 4 (16 层 × 16 vec)
        public System.Numerics.Vector4 EditSub;       // 输入中文字纹理子矩形 (u0,v0,du,dv); w<=0 不画
        public System.Numerics.Vector4 EditPos;       // 输入中文字帧内位置 (x,y,w,h)
    }

    private static void SetV4(float[] arr, int idx, System.Numerics.Vector4 v)
    {
        int o = idx * 4;
        arr[o] = v.X; arr[o + 1] = v.Y; arr[o + 2] = v.Z; arr[o + 3] = v.W;
    }

    // ═══ 背景面契约（唯一出处：建窗与设备丢失重建都走这里）═══
    // Format = R16G16B16A16_Float + ColorSpaceType.RgbFullG10NoneP709 == 16-bit scRGB：
    // 每通道线性 float，1.0 = 80 nits 名义白，HDR 高光用 >1 的扩展范围表示。
    // 纸白换算（×k，k = 纸白/80）只在 CPU 侧 BgraToScrgbForDisplay 乘一次，这里不乘第二次。
    // AlphaMode.Ignore：整屏不透明覆盖层，合成 PS 恒输出 alpha=1，不参与 DWM 的 alpha 混合。
    // ⚠ 本文件曾有一段「AlphaMode.Ignore → Premultiplied 关键修复」的注释，与代码从未相符
    //   （git 历史里没有那次改动），其前提 WS_EX_NOREDIRECTIONBITMAP 也已从 WS_EX 常量处移除——
    //   不要照着它把这里改成 Premultiplied。
    private static SwapChainDescription1 CreateOverlayDesc(int w, int h) => new()
    {
        Width = (uint)w,
        Height = (uint)h,
        Format = Format.R16G16B16A16_Float,
        SampleDescription = new SampleDescription(1, 0),
        BufferUsage = Usage.RenderTargetOutput,
        BufferCount = 2,
        Scaling = Scaling.Stretch,
        SwapEffect = SwapEffect.FlipSequential,
        AlphaMode = AlphaMode.Ignore,
        Flags = SwapChainFlags.None,
    };

    /// <summary>
    /// 把背景面颜色空间钉成 linear scRGB；不支持时把实际协商结果写进日志，
    /// 而不是静默留在上一个颜色空间（那会让线性帧被按 sRGB 解释，画面整体偏亮/偏暗）。
    /// </summary>
    private static void ApplyOverlayColorSpace(IDXGISwapChain3 sc3)
    {
        const ColorSpaceType want = ColorSpaceType.RgbFullG10NoneP709;
        var support = sc3.CheckColorSpaceSupport(want);
        if ((support & SwapChainColorSpaceSupportFlags.Present) != 0)
        {
            sc3.SetColorSpace1(want);
            LogService.Info("HdrCapture", "背景面: R16G16B16A16_Float + RgbFullG10NoneP709 (16-bit scRGB)");
        }
        else
        {
            LogService.Warn("HdrCapture",
                $"背景面不支持 RgbFullG10NoneP709（CheckColorSpaceSupport={support}），"
                + "帧仍按 16-bit 提交但颜色空间未生效 —— 画面可能整体偏亮/偏暗",
                LogCategory.Capture);
        }
    }

    /// <summary>取 D3D11 设备：默认复用进程级共享设备（阶段 4），<c>TTC_PREVIEW_SHARED_DEVICE=0</c> 时本窗独建。</summary>
    public HdrCaptureWindow()
    {
        // 每张截图都要新建一扇：这一段就是"按完键到窗口能画"里除捕获外的固定成本，
        // 单独立一条读数是为了决定要不要把设备改成进程级复用（没有这条数就只是猜）。
        // 2026-10-30：设备已改成默认复用 ⇒ 这一行的毫秒数从此回答"复用有没有生效"。
        var swCtor = System.Diagnostics.Stopwatch.StartNew();
        var (dev, ctx) = AcquireDevice();
        _device = dev;
        _ctx = ctx;
        LogService.Info("HdrCapture",
            $"窗口构造 {swCtor.ElapsedMilliseconds}ms（{(_ownsDevice ? "D3D11 设备" : "复用进程级共享设备")}）",
            LogCategory.Capture);
    }

    /// <summary>取设备：共享开启且缓存可建 ⇒ 用缓存（<see cref="_ownsDevice"/>=false）；否则本窗独建。</summary>
    private (ID3D11Device Device, ID3D11DeviceContext Ctx) AcquireDevice()
    {
        if (!s_sharedDeviceEnabled)
        {
            _ownsDevice = true;
            return NewDevice();
        }
        var pair = GetOrCreateCachedDevice();
        if (pair.Device is null)
        {
            // 建共享设备失败（驱动/显存）：退回本窗独建 —— 宁可多付 33–57ms，也不能没有设备。
            _ownsDevice = true;
            return NewDevice();
        }
        _ownsDevice = false;
        return (pair.Device, pair.Ctx!);
    }

    private static (ID3D11Device? Device, ID3D11DeviceContext? Ctx) GetOrCreateCachedDevice()
    {
        lock (s_deviceCacheLock)
        {
            if (s_cachedDevice is not null) return (s_cachedDevice, s_cachedCtx);
            try
            {
                var dev = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                s_cachedDevice = dev;
                s_cachedCtx = dev.ImmediateContext;
                LogService.Info("HdrCapture", "预览设备: 建立进程级共享 D3D11 设备（后续窗口复用）", LogCategory.Capture);
                return (dev, s_cachedCtx);
            }
            catch (Exception ex)
            {
                LogService.Warn("HdrCapture", $"共享 D3D11 设备建立失败，本窗退回独建设备: {ex.Message}", LogCategory.Capture);
                return (null, null);
            }
        }
    }

    /// <summary>把进程级共享 D3D11 设备提前建好（选区起点投递，与捕获那 30–150ms 重叠）。
    /// 失败只留 Warn：设备是建窗时才用的东西，这里只负责把首扇窗的那 33–65ms 挪进捕获窗口。</summary>
    internal static void WarmSharedDevice()
    {
        if (!s_sharedDeviceEnabled) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pair = GetOrCreateCachedDevice();
            if (pair.Device is not null)
                LogService.Debug("HdrCapture",
                    $"预览设备预热: 共享设备就绪 {sw.ElapsedMilliseconds}ms", LogCategory.Capture);
        });
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Ctx) NewDevice()
    {
        var dev = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        return (dev, dev.ImmediateContext);
    }

    /// <summary>设备丢失后换新共享缓存：只换一次（后到的窗口看到的是已换好的那块），旧设备先收。</summary>
    /// <remarks>必须在 <see cref="s_deviceCacheLock"/> 内判断"缓存那块是不是真的没了"——否则两个窗口
    /// 前后脚报丢失时，第二个会把第一个刚建好的设备再收掉一次。</remarks>
    private static (ID3D11Device Device, ID3D11DeviceContext Ctx) ReplaceCachedDevice()
    {
        lock (s_deviceCacheLock)
        {
            bool dead = s_cachedDevice is null || D3DDeviceLost.IsDeviceRemoved(s_cachedDevice);
            if (!dead) return (s_cachedDevice!, s_cachedCtx!);

            var oldDev = s_cachedDevice;
            var oldCtx = s_cachedCtx;
            s_cachedDevice = null;
            s_cachedCtx = null;
            try { oldCtx?.Dispose(); } catch { }
            try { oldDev?.Dispose(); } catch { }

            var dev = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            s_cachedDevice = dev;
            s_cachedCtx = dev.ImmediateContext;
            LogService.Warn("HdrCapture", "共享 D3D11 设备已丢失 ⇒ 换新（后续窗口自动跟进）", LogCategory.Capture);
            return (dev, s_cachedCtx);
        }
    }

#pragma warning disable CS8618 // 无设备壳：_device/_ctx 刻意不建（见下），壳只跑 BuildAtlasLayout 这一条腿
    /// <summary>
    /// 无设备预热壳：只带一个 DPI 缩放，<b>不建 D3D11 设备、不建窗口</b>。
    /// <see cref="BuildAtlasLayout"/> 这条 CPU 腿只碰 Win2D 的进程级共享设备（<c>AnnotationRasterizer.SharedCanvasDevice</c>），
    /// 与窗口自己的 <c>_device</c>/<c>_ctx</c> 无关（这条纪律已有判据钉着，见 BuildAtlasLayout 上方注释），
    /// 所以可以用一个没有设备的壳，在选区起点就把它丢到后台提前烘，真窗口建好后直接吃跨窗缓存。
    /// </summary>
    private HdrCaptureWindow(float uiScale)
    {
        _uiScale = uiScale;
    }
#pragma warning restore CS8618

    public bool Initialize(int x, int y, int w, int h)
    {
        var swInit = System.Diagnostics.Stopwatch.StartNew();
        _winX = x; _winY = y; _winW = w; _winH = h;
        try
        {
            if (!_classReg)
            {
                _staticWndProc = StaticWndProc;
                var wc = new WNDCLASSW { style = 0, proc = Marshal.GetFunctionPointerForDelegate(_staticWndProc), inst = GetModuleHandleW(null), cur = LoadCursorW(0, CROSS), cls = CN };
                RegisterClassW(ref wc);
                _classReg = true;
            }
            _hwnd = CreateWindowExW(WS_EX, CN, "HDR", WS_POPUP | WS_VIS, x, y, w, h, 0, 0, GetModuleHandleW(null), 0);
            if (_hwnd == 0) { LastError = $"CreateWindowExW 失败: {Marshal.GetLastWin32Error()}"; return false; }
            // 将实例指针存入窗口 GWLP_USERDATA
            s_windows[_hwnd] = this;
            SetWindowLongPtrW(_hwnd, GWLP_USERDATA, _hwnd); // 用窗口句柄自身作为 key
            SetForegroundWindow(_hwnd); // 获取键盘焦点

            using var dxgiD = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiD.GetAdapter();
            // ═══ 2026-08-16 方案A: 升级到 IDXGIFactory7 以访问 PresentAllowTearing ═══
            using var factory = adapter.GetParent<IDXGIFactory7>();
            // 检测 VRR/撕裂支持 (Win10 1607+ 驱动 + 显示器): present_allow_tearing 非零即支持
            try { _vrrSupported = factory.PresentAllowTearing; }
            catch { _vrrSupported = false; }
            LogService.Info("HdrCapture", $"VRR (GSYNC/FreeSync) 支持: {(_vrrSupported ? "✓ 启用" : "✗ 不可用, 回退常规呈现")}");

            // ═══ 2026-08-16 增强: 检测 MPO/硬件合成 (独立翻转/DirectFlip 前提) ═══
            // 窗口化 DirectFlip + MPO 时 DWM 可绕过合成直接扫描出画面, 延迟进一步降低。
            // 仅诊断输出, 实际协商由 DWM 自动完成 (全屏 3840x2160 窗口满足前置条件)。
            try
            {
                using var output = adapter.EnumOutputs(0, out var o0).Success ? o0 : null;
                if (output is not null)
                {
                    using var output6 = output.QueryInterface<IDXGIOutput6>();
                    var mpo = output6.CheckHardwareCompositionSupport();
                    _mpoWindowed = mpo.HasFlag(HardwareCompositionSupportFlags.Windowed);
                    _mpoFullscreen = mpo.HasFlag(HardwareCompositionSupportFlags.Fullscreen);
                    LogService.Info("HdrCapture", $"MPO 硬件合成: 窗口化={(_mpoWindowed ? "✓" : "✗")} 全屏={(_mpoFullscreen ? "✓" : "✗")} (独立翻转/DirectFlip 自动协商)");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("HdrCapture", $"MPO 检测失败: {ex.Message}", LogCategory.Capture);
            }

            // ═══ 2026-08-25 修复: 创建交换链时强制不带 AllowTearing ═══
        // 用户环境 Present(1, AllowTearing) 静默失败 (不抛异常, 画面不更新)。
        // 即使 VRR 支持, 也先创建不带 AllowTearing 的交换链, 确保 Present(0) 正常。
        // 若后续需要在 PresentFrame 中尝试 VRR, 可在此处加 AllowTearing 并在失败时重建。
            // 背景面格式/颜色空间契约见 CreateOverlayDesc 上方注释（唯一出处）
            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, CreateOverlayDesc(w, h));
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            ApplyOverlayColorSpace(sc3);
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(_backBuffer);

            string sd = Path.Combine(AppContext.BaseDirectory, "data", "Shaders");
            // Debug 下 bin 直出目录是 Shaders/ (csproj Content LinkBase 未生效), 双路径回退
            string sdAlt = Path.Combine(AppContext.BaseDirectory, "Shaders");
            static string? FindCso(string dir, string name) =>
                File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;
            string? vsp = FindCso(sd, "FullscreenVS.hlsl.cso") ?? FindCso(sdAlt, "FullscreenVS.hlsl.cso");
            if (vsp is not null) _vs = _device.CreateVertexShader(File.ReadAllBytes(vsp), null);
            // ═══ 2026-09-16：删除 ToneMapping PS（即原 _ps）的创建 ═══
            // 原代码在这里创建 _ps，但它从未被 PSSetShader 绑定过：
            // 本文件唯一的 PSSetShader（RenderGpuComposite）绑的是 _overlayPs。
            // 也就是说这个着色器白白创建、又白白释放，还让后来者误以为存在
            // 一条"GPU 直通拷贝 / GPU 色调映射"的路径。
            // 真正承担色调映射的是 Core 的 ToneMapper（CPU 路径）。
            // 同步删除：字段声明、本处创建、重建路径中的创建、以及各 Dispose 处的释放。

            // ── Phase 2 (2026-08-11): GPU 合成着色器 + 常量缓冲（失败自动回退 CPU CompositeUI）──
            try
            {
                string? osp = FindCso(sd, "OverlayComposite.hlsl.cso") ?? FindCso(sdAlt, "OverlayComposite.hlsl.cso");
                if (osp is not null && _vs is not null)
                {
                    _overlayPs = _device.CreatePixelShader(File.ReadAllBytes(osp));
                    // ═══ 2026-08-26 根因修复: 常量缓冲大小必须 ≥ HLSL cbuffer 声明大小 ═══
                    // HLSL 需要: 9×vec4(144) + TextSubs[56]×16(896) + TextPos[56]×16(896)
                    //           + 6×vec4(96) + AnnoLayers[256]×16(4096) + 2×vec4(32) = 6160
                    // D3D11 规则: ByteWidth < 着色器声明 → Draw 静默失败/无输出
                    //   (桌面帧仍显示因为走 CopyResource 不依赖 cbuffer; UI 层全灭)
                    // 硬编码而非依赖 Marshal.SizeOf (ByValArray float[] 封送大小不可靠)
                    const uint UiCbByteWidth = 6160;
                    _uiCb = _device.CreateBuffer(new BufferDescription
                    {
                        ByteWidth = UiCbByteWidth,
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ConstantBuffer,
                        CPUAccessFlags = CpuAccessFlags.Write
                    });
                    _useGpuCompositor = true;
                    // ═══ 2026-08-26 对照测试开关: TTC_GPU_COMPOSITE=0 强制 CPU 合成路径 ═══
                    if (Environment.GetEnvironmentVariable("TTC_GPU_COMPOSITE") == "0")
                    {
                        _useGpuCompositor = false;
                        LogService.Warn("HdrCapture", "诊断开关 TTC_GPU_COMPOSITE=0: 强制 CPU 合成路径", LogCategory.Capture);
                    }
                    else
                        LogService.Info("HdrCapture", "GPU 合成已启用 (OverlayComposite)");
                    _rsNoCull = _device.CreateRasterizerState(new RasterizerDescription
                    {
                        FillMode = FillMode.Solid,
                        CullMode = CullMode.None // 双保险: 不剔除任何面
                    });
                    LogService.Info("HdrCapture", "GPU 合成已启用 (OverlayComposite)");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("HdrCapture", $"GPU 合成初始化失败，回退 CPU: {ex.Message}", LogCategory.Capture);
                _overlayPs?.Dispose(); _overlayPs = null;
                _uiCb?.Dispose(); _uiCb = null;
                _useGpuCompositor = false;
            }
            _samp = _device.CreateSamplerState(new SamplerDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue });

            SetWindowPos(_hwnd, TOP, x, y, w, h, SWP_F);
            IsInitialized = true;
            LogService.Info("HdrCapture", $"窗口初始化成功 {w}x{h} @({x},{y})", LogCategory.Capture);

            // ═══ 2026-08-25 P1 优化: 预创建首帧 GPU 资源 ═══
            // UploadFrame 首次调用会创建 _desktopTex + _pooledStaging (~5-10ms),
            // 提前到 Initialize 阶段 (窗口显示前) 完成, 首帧只需 Map+转换+Copy。
            // 尺寸为窗口分辨率 (全屏时=显示器桌面分辨率)。_ctx 仅创建纹理, 无渲染操作, UI 线程安全。
            // 路线 B-3（2026-10-30）：共享帧已挂上时桌面就是那块共享纹理，这两块（4K Float16 = 132MB）
            // 建了也会被 EnsureSharedDesktop 立刻换掉 —— 共享路直接跳过；整帧兜底路由 UploadFrame 按需建。
            if (!_sharedAttached)
            {
                try
                {
                    if (_desktopTex is null || _desktopTex.Description.Width != w || _desktopTex.Description.Height != h)
                    {
                        _desktopSrv?.Dispose(); _desktopTex?.Dispose();
                        _desktopTex = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource });
                        _desktopSrv = _device.CreateShaderResourceView(_desktopTex);
                    }
                    if (_pooledStaging is null || _pooledStW != w || _pooledStH != h)
                    {
                        _pooledStaging?.Dispose();
                        _pooledStaging = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Write });
                        _pooledStW = w; _pooledStH = h;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn("HdrCapture", $"预创建纹理失败 (首帧将惰性创建): {ex.Message}", LogCategory.Capture);
                }
            }

            // ── DPI 缩放：全屏 CPU 渲染的 UI（工具栏/手柄/文字）按物理像素绘制，
            // 4K 200% 缩放下必须放大，否则 UI 过小无法操作 ──
            uint dpi = GetDpiForWindow(_hwnd);
            _uiScale = dpi > 0 ? dpi / 96.0 : 1.0;
            _handleSize = Math.Max(8, (int)Math.Round(8 * _uiScale));
            _handleHit = Math.Max(12, (int)Math.Round(12 * _uiScale));
            _hoverTipW = Math.Max(160, (int)Math.Round(200 * _uiScale));
            _hoverTipH = Math.Max(22, (int)Math.Round(28 * _uiScale));
            LogService.Info("HdrCapture", $"DPI={dpi} Scale={_uiScale:F2} 手柄={_handleSize}/{_handleHit}", LogCategory.UI);

            // ═══ 2026-10-30：图集的 CPU 腿提前到开窗时开跑 ═══
            // 真机读数（4K + 150% 缩放）：首帧合成 111ms 里 98ms 是图集，而 98ms 中 86ms 是 DirectWrite
            // 排版 —— 它只用 Win2D 的进程级共享设备，不需要本窗口的 D3D 设备/上下文，所以能搬走。
            // 启动预热那 96 个逐格位图喂的是 CPU 兜底合成（DrawTextGdi），GPU 合成开着时一次也不被读，
            // 因此这 86ms 一直留在"按下热键之后"的关键路径上。现在让它与窗口初始化 + 区域检测 + 首帧上传
            // （本机这三段约 350ms）重叠，渲染线程在 EnsureTextAtlas 取用；期间脏标记被置起或缩放变了就作废重排。
            _pendingAtlasScale = _uiScale;
            _atlasPrewarmQueuedAtMs = Environment.TickCount64;   // 投递时刻；与后台线程开跑时刻相减=任务排队
            // 跨窗缓存命中就不再排这一趟：渲染线程在 EnsureTextAtlas 里直接取缓存（省一条后台任务与争核）
            if (!AtlasCacheHot())
                _pendingAtlasLayout = System.Threading.Tasks.Task.Run(BuildAtlasLayout);

            // 启动专用渲染线程（UI 线程不再执行任何渲染）
            StartRenderThread();

            // 启动自动超时定时器（回调仅投递窗口消息，由 UI 线程处理，线程安全）
            _autoTimer = new System.Threading.Timer(_ =>
            {
                if (_idleWatch.ElapsedMilliseconds >= AutoTimeoutMs && _hwnd != 0)
                {
                    LogService.Info("HdrCapture", "1 分钟无操作，自动关闭", LogCategory.Capture);
                    PostMessageW(_hwnd, WM_APP_TIMEOUT, 0, 0);
                }
            }, null, AutoTimeoutMs, 5000);

            // ── 硬看门狗：3 分钟后无论什么情况强制退出（线程池线程，UI 卡死也能触发）──
            _hardTimer = new System.Threading.Timer(_ =>
            {
                if (_actionFired || _hwnd == 0 || _disposed) return; // 已正常处理
                LogService.Warn("HdrCapture", "硬看门狗触发: 3 分钟强制退出", LogCategory.Capture);
                // 先投递超时消息尝试优雅关闭（UI 活着时正常走 FireAction(Cancel)+Close）
                try { if (_hwnd != 0) PostMessageW(_hwnd, WM_APP_TIMEOUT, 0, 0); } catch { }
                // 给 UI 线程 5 秒处理时间；仍未响应说明 UI 卡死 → 强制退出进程
                Thread.Sleep(5000);
                if (!_actionFired && _hwnd != 0)
                {
                    try { LogService.Error("HdrCapture", "硬看门狗: UI 无响应，强制退出"); } catch { }
                    Environment.Exit(0);
                }
            }, null, HardExitMs, Timeout.Infinite);

            // 后台检测窗口区域（QQ截图式快速选择）
            StartRegionDetection();

            LogService.Info("HdrCapture", $"窗口初始化 {swInit.ElapsedMilliseconds}ms（含纹理预建/图集投递/渲染线程）", LogCategory.Capture);
            return true;
        }
        catch (Exception ex) { LastError = ex.ToString(); Cleanup(); return false; }
    }

    public void LoadFrame(float[] pixels, int w, int h)
    {
        if (_disposed) return;
        if (pixels is null || (long)w * h * 4 > pixels.Length)
        {
            LogService.Error("HdrCapture", $"LoadFrame 参数无效: len={pixels?.Length ?? -1} w={w} h={h}", LogCategory.Capture);
            return;
        }
        // 2026-08-10: 132MB 合成缓冲分配移入渲染线程（CompositeUI 懒创建）——
        // UI 线程不再做 LOH 大分配（4K 分配曾引起长 GC 停顿，实机表现为弹出预览瞬间卡顿）
        // 一次性原子发布（数组 + 尺寸打包），渲染线程 Volatile.Read 读取
        Volatile.Write(ref _frame, new FrameData(pixels, w, h));
        _compPixels = null;
        _hasFrame = false;
        LogService.Info("HdrCapture", $"LoadFrame: {w}x{h} (win={_winW}x{_winH})", LogCategory.Capture);
        RequestRedraw();
    }

    /// <summary>路线 B：把桌面帧改成直接采样 WGC 会话那块跨设备共享纹理（整帧不再过 CPU）。
    /// 必须在 <see cref="Initialize"/> 之前调用；<paramref name="release"/> 在本窗口 Dispose 时调一次。
    /// 打不开、格式或尺寸对不上时自动退回 <see cref="LoadFrame"/> 的整帧上传，画面不依赖它成功。
    /// <paramref name="fallback"/> 是路线 B-3 之后新增的：捕获侧不再预读整帧，共享纹理打不开时
    /// 没有 CPU 帧可退 ⇒ 渲染线程现问它要一份（只在失败时付这笔钱，正常路径一次都不调）。</summary>
    public void AttachSharedFrame(nint sharedHandle, int w, int h, Action? release,
        Func<float[]?>? fallback = null)
    {
        if (_disposed || sharedHandle == 0) return;
        _sharedHandle = sharedHandle;
        _sharedW = w; _sharedH = h;
        _sharedRelease = release;
        _sharedFallback = fallback;
        _sharedAttached = true;
    }

    /// <summary>打开共享纹理并建成桌面 SRV（渲染线程独占 <c>_device</c>，只能在渲染线程调）。</summary>
    /// <remarks>两条硬约束：
    /// ① NTHANDLE 只能用 <c>OpenSharedResource1</c> 打开（<c>OpenSharedResource</c> 走旧式共享句柄，拿到的是空/异常）；
    /// ② 着色器 <c>OverlayComposite</c> 把 t0 当 scRGB 线性 Float16 采样、且 UV 按窗口尺寸展开 ⇒
    ///   格式不是 R16G16B16A16_Float、尺寸不等于窗口时一律不接（否则整屏错位/颜色全错）。
    /// 设备丢失重建后 <c>_desktopTex</c> 为空、句柄仍在 ⇒ 在这里按新设备重开一次。</remarks>
    private bool EnsureSharedDesktop()
    {
        if (!_sharedAttached) return false;
        if (_sharedTexOpen is not null) return true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var dev1 = _device.QueryInterface<ID3D11Device1>();
            var tex = dev1.OpenSharedResource1<ID3D11Texture2D>(_sharedHandle);
            var desc = tex.Description;
            if (desc.Format != Format.R16G16B16A16_Float || desc.Width != (uint)_winW || desc.Height != (uint)_winH)
            {
                if (!_sharedFaultLogged)
                {
                    _sharedFaultLogged = true;
                    LogService.Warn("HdrCapture",
                        $"共享桌面帧不合用（{desc.Width}x{desc.Height} {desc.Format}，窗口 {_winW}x{_winH}）⇒ 退回整帧上传",
                        LogCategory.Capture);
                }
                tex.Dispose();
                return false;
            }
            _sharedTexOpen = tex;
            // 换掉 Initialize 预建的那块占位纹理（共享路径下桌面帧就是这一块）
            _desktopSrv?.Dispose();
            _desktopTex?.Dispose();
            _desktopTex = tex;
            _desktopSrv = _device.CreateShaderResourceView(tex);
            LogService.Info("HdrCapture",
                $"桌面帧走共享纹理: {_winW}x{_winH} 打开+建SRV {sw.ElapsedMilliseconds}ms（整帧不过 CPU）", LogCategory.Capture);
            return true;
        }
        catch (Exception ex)
        {
            if (!_sharedFaultLogged)
            {
                _sharedFaultLogged = true;
                LogService.Warn("HdrCapture", $"打开共享桌面帧失败 ⇒ 退回整帧上传: {ex.Message}", LogCategory.Capture);
            }
            return false;
        }
    }

    /// <summary>交还共享槽（只发一次）。预览关闭后下一帧才能继续覆写那块纹理。</summary>
    private void ReleaseSharedSource()
    {
        var release = Interlocked.Exchange(ref _sharedRelease, null);
        _sharedAttached = false;
        _sharedHandle = 0;
        _sharedFallback = null;
        try { release?.Invoke(); }
        catch (Exception ex) { LogService.Warn("HdrCapture", $"交还共享槽失败: {ex.Message}", LogCategory.Capture); }
    }

    /// <summary>请求渲染线程重绘（UI 线程仅发信号，微秒级返回）。</summary>
    public void RequestRedraw()
    {
        if (_disposed || _closing || _hwnd == 0) return;
        // ═══ 2026-08-25 诊断: 确认渲染信号发出 ═══
        bool wasSignaled = _renderSignal.Set();
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "HdrCaptureRender",
            Priority = ThreadPriority.BelowNormal // 不抢 UI 线程
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        long lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
        bool isFirstFrame = true;
        int frameCount = 0;
        while (!_disposed && !_closing)
        {
            _renderSignal.WaitOne();
            if (_disposed || _closing) return;
            try
            {
                // ═══ 阶段 4：ImmediateContext 可能被多扇窗共用（进程级设备）⇒ 渲染段串行 ═══
                // 产品路径同一时刻只有一扇窗在渲染，这把锁在真机上恒无竞争；它挡的是探针那种
                // "旧窗还没 Dispose、新窗已在渲染"的叠加场景（Map/Unmap 交错 = 偶发花屏/设备丢失）。
                long tFrame = PerfMeter.Now();   // F4 仪表：只圈住渲染段，不含下面那次定步长 Sleep
                lock (s_ctxLock) { RenderCore(); }
                long frameUs = PerfMeter.SinceMicro(tFrame);
                PerfMeter.Record(ref _mFrames, ref _mFrameUs, ref _mFrameMaxUs, frameUs);
                // 静置唤醒的那一帧单独归一桶：判据是"这一帧是不是由 3s 定时器发信号引出来的"，
                // 不是"用户没动"—— 用户动的时候 RequestRedraw 也走同一个信号，混在一起就成了"静置税"虚高。
                if (Interlocked.Exchange(ref _mIdleWakePending, 0) == 1)
                    PerfMeter.Record(ref _mIdleWakeFrames, ref _mIdleWakeUs, ref _mIdleWakeMaxUs, frameUs);
                frameCount++;
                // 每 10 帧打点属调试信息，降级为 Debug（默认不落盘），
                // 避免正常使用时日志文件被高频渲染打点持续撑大。
                if (frameCount % 10 == 1)
                    LogService.Debug("HdrCapture", $"渲染帧 #{frameCount}", LogCategory.Capture);
                if (isFirstFrame)
                    LogService.Info("HdrCapture", "首帧 RenderCore 完成", LogCategory.Capture);
            }
            catch (Exception ex)
            {
                LogService.Error("HdrCapture", $"渲染线程异常: {ex.Message}\n{ex}", LogCategory.Capture);
            }

            if (isFirstFrame) isFirstFrame = false;
            long elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lastRender) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            int wait = 16 - (int)elapsedMs;
            if (wait > 0) Thread.Sleep(wait);
            lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        LogService.Debug("HdrCapture", $"渲染循环退出, 总帧数={frameCount}", LogCategory.Capture);
    }

    private void RenderCore()
    {
        if (_disposed || !IsInitialized || _swapChain is null) return;

        // ═══ 2026-09-15: 周期性主动探测设备状态 ═══
        // 某些驱动下 Present 会【静默失败】（不抛异常、画面不更新），
        // 只有 DeviceRemovedReason 能暴露问题。每 30 帧（约 0.5s）查一次，开销可忽略。
        if (_deviceLost) return;   // 已判定丢失，等恢复流程结束
        if ((++_renderFrameSeq % 30) == 0 && D3DDeviceLost.IsDeviceRemoved(_device))
        {
            HandleDeviceLost(null);
            return;
        }

        try
        {
            // 2026-08-11 修复: FlipSequential 交换链 Present 后后台缓冲翻转,
            // 必须每次渲染前重新 GetBuffer(0) + 重建 RTV — 固定引用导致绘制到过期缓冲 → 黑屏
            EnsureBackBuffer();
            if (_backBuffer is null || _rtv is null) return;

            // ═══ Phase 2 (2026-08-11): GPU 合成路径 — 桌面帧纹理 + OverlayComposite PS 直接画 UI ═══
            // CPU 路径（CompositeUI 全屏混合）仅作回退。收益: 拖拽合成 ~20-30ms → GPU <2ms
            var frame = Volatile.Read(ref _frame);
            // 路线 B（2026-10-30）：桌面帧优先来自 WGC 会话那块共享纹理 —— 整帧不再 float[]→half→
            // 暂存纹理→CopyResource。打不开就退回下面的整帧上传（frame 仍在，画面不受影响）。
            bool sharedReady = _useGpuCompositor && EnsureSharedDesktop();
            // 路线 B-3（2026-10-30）：捕获侧不再预读整帧 ⇒ 共享纹理打不开时没有 frame 可退。
            // 现问捕获侧要一份整帧（它仍冻着），只在失败路径付这笔钱；正常路径一次都不调。
            if (!sharedReady && frame is null && _sharedFallback is not null)
            {
                var fb = _sharedFallback;
                _sharedFallback = null;
                var swf = System.Diagnostics.Stopwatch.StartNew();
                var px = fb();
                if (px is not null && (long)_winW * _winH * 4 <= px.Length)
                {
                    Volatile.Write(ref _frame, new FrameData(px, _winW, _winH));
                    _compPixels = null;
                    _hasFrame = false;
                    frame = Volatile.Read(ref _frame);
                    LogService.Warn("HdrCapture",
                        $"共享帧不可用 ⇒ 现取整帧兜底 {swf.ElapsedMilliseconds}ms（{_winW}x{_winH}）", LogCategory.Capture);
                }
                else
                    LogService.Error("HdrCapture", "共享帧打不开、整帧兜底也取不到 ⇒ 预览只有遮罩没有桌面", LogCategory.Capture);
            }
            if (_useGpuCompositor && _overlayPs is not null && _uiCb is not null
                && (sharedReady || frame is not null))
            {
                // 首次渲染时上传桌面帧到 _desktopTex（帧内容不变，只传一次）
                if (!sharedReady && !_hasFrame)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    UploadFrame(frame!.Pixels, frame.W, frame.H);
                    LogService.Info("HdrCapture", $"首帧 UploadFrame: {sw.ElapsedMilliseconds}ms", LogCategory.Capture);
                }
                if (_desktopTex is not null)
                {
                    // ═══ 2026-08-26 关键修复: GPU 合成分支禁止 CopyResource ═══
                    // RenderGpuComposite 已用 OverlayComposite PS 把 UI 绘到后台缓冲。
                    // 之前的 CopyResource(_backBuffer, _desktopTex) 用桌面纹理覆盖了后台缓冲
                    // → UI 被桌面擦掉 → 用户看到"直接显示桌面"无任何覆盖层。
                    //
                    // 首帧一次性分解打点：实机日志里"首帧 UploadFrame"之后到"首帧 RenderCore 完成"
                    // 恒定还有 172–567ms（44 次真跑，p50=244ms），而这段时间里只有下面两次调用，
                    // 分不清是合成（文字图集首建在其中）还是第一次 Present 被 DWM 拖住。
                    bool diag = Volatile.Read(ref _diagFirstCompositeDone) == 0;
                    long compositeMs = 0, presentMs = 0;
                    var swc = System.Diagnostics.Stopwatch.StartNew();
                    RenderGpuComposite();
                    if (diag) { compositeMs = swc.ElapsedMilliseconds; swc.Restart(); }
                    PresentFrame();
                    if (diag)
                    {
                        presentMs = swc.ElapsedMilliseconds;
                        Volatile.Write(ref _diagFirstCompositeDone, 1);
                        // 图集这一腿拆成三段交出来（2026-10-30）：修前真机"上传"单独 12ms，改后整腿读到 43ms
                        // 却分不出是【预热任务排队排晚了】还是【上传本身变慢】——这两件事的修法相反，只能用尺子分。
                        // 排队记在预热那一侧：它发生在渲染线程"问它"之前，不在图集整腿的总时间里。
                        LogService.Info("HdrCapture",
                            $"首帧合成 {compositeMs}ms（文字图集 {_atlasTotalMsAtlas}ms = 排版 {_rasterMsAtlas} + 等 {_atlasPrewarmWaitMs} + 上传 {_atlasUploadMs}，预热排版 {_atlasPrewarmMsAtlas}ms〔画 {_atlasPrewarmDrawMs} + 读回 {_atlasPrewarmReadMs} + 并入 {_atlasPrewarmMergeMs}〕排队 {_atlasQueueMsAtlas}）+ 呈现 {presentMs}ms",
                            LogCategory.Capture);
                    }
                    return;
                }
            }

            // ═══ CPU 回退路径（原实现）═══
            // 从原始帧重建合成缓冲（不破坏原始数据；_compPixels 由 CompositeUI 懒创建）
            if (frame is not null)
            {
                CompositeUI(frame);
                // ⚠ 必须用"窗口"尺寸上传：下方 CopyResource(_backBuffer, _desktopTex) 要求
                // 二者同尺寸，而 _backBuffer 恒为窗口尺寸。之前传的是帧尺寸，帧尺寸≠窗口尺寸
                // 时 _desktopTex 尺寸与后台缓冲不符 → CopyResource 失败 → CPU 回退路径黑屏。
                if (_compPixels is not null)
                    UploadFrame(_compPixels, _winW, _winH);
            }

            // 直接 CopyResource：桌面纹理和后台缓冲区都是 R16G16B16A16_Float 同尺寸
            if (_hasFrame && _desktopTex is not null)
            {
                _ctx.CopyResource(_backBuffer, _desktopTex);
            }

            // Present(0)：不等待 vsync，避免渲染线程被垂直同步阻塞
            PresentFrame();
        }
        catch (Exception ex)
        {
            // ═══ 2026-09-15: D3D 设备丢失判定 ═══
            // 此前这里只记一行日志。设备丢失（驱动 TDR / 显卡切换 / 远程桌面变更）后，
            // 每一帧都会在同一个已死的设备上重复失败：日志刷屏、画面永久黑屏，
            // 而用户拿不到任何提示，只能杀进程重开。
            if (D3DDeviceLost.ShouldRebuild(_device, ex))
            {
                HandleDeviceLost(ex);
                return;
            }
            LogService.Error("HdrCapture", $"RenderCore 异常: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  D3D 设备丢失恢复 (2026-09-15)
    // ═══════════════════════════════════════

    /// <summary>
    /// 处理设备丢失：去重 → 记录诊断 → 尝试原地重建 → 超限时放弃自愈。
    /// </summary>
    /// <remarks>
    /// 判据来自 <see cref="D3DDeviceLost"/>（异常 HRESULT + 设备 DeviceRemovedReason 双通道）。
    /// 原地重建而非重建窗口：窗口句柄、帧数据、选区状态全部保留，用户不会看到窗口闪烁。
    /// </remarks>
    private void HandleDeviceLost(Exception? ex)
    {
        if (_deviceLost) return;   // 已在恢复流程中，避免每帧重复触发
        _deviceLost = true;

        LogService.Error("HdrCapture",
            $"检测到 D3D 设备丢失: {D3DDeviceLost.Describe(_device, ex)}", LogCategory.Capture);

        if (_deviceLostRecoveryAttempts >= MaxDeviceLostRecovery)
        {
            LogService.Error("HdrCapture",
                $"已重建 {MaxDeviceLostRecovery} 次仍再次丢失，停止自动恢复（通常是显卡驱动问题，建议更新驱动或重启程序）",
                LogCategory.Capture);
            return;
        }

        _deviceLostRecoveryAttempts++;
        if (!TryRebuildDeviceResources())
        {
            LogService.Error("HdrCapture",
                "设备丢失后重建失败 —— HDR 覆盖层不可用；建议改用选区模式或重启程序",
                LogCategory.Capture);
        }
    }

    /// <summary>
    /// 释放所有 D3D 资源（重建前调用）。逐项 try：设备已失效时 Dispose 本身可能抛异常。
    /// </summary>
    private void ReleaseDeviceResources()
    {
        void Safe(System.IDisposable? d) { try { d?.Dispose(); } catch { } }
        Safe(_desktopSrv); Safe(_desktopTex); Safe(_pooledStaging);
        Safe(_textAtlasSrv); Safe(_textAtlas);
        Safe(_badgeSrv); Safe(_badgeTex);
        Safe(_editSrv); Safe(_editTex);
        Safe(_samp); Safe(_rsNoCull); Safe(_uiCb);
        Safe(_overlayPs); Safe(_vs);   // _ps 已删除（死代码，见文件头说明）
        Safe(_rtv); Safe(_backBuffer); Safe(_swapChain);
        // 注意：先释放子资源再释放设备/上下文。
        // 阶段 4：共享设备归进程缓存所有 ⇒ 这里只放手、不 Dispose（否则下一扇窗拿到的是已死的 COM 对象）。
        if (_ownsDevice) { Safe(_ctx); Safe(_device); }

        _desktopSrv = null; _desktopTex = null; _pooledStaging = null;
        _sharedTexOpen = null;   // 设备已换：共享纹理要按新设备重开（句柄留着，见下）
        // _sharedHandle 刻意不在这里清：设备丢失重建后要按新设备把同一块共享纹理重开一次
        // （句柄由 WGC 会话持有，与我们的设备无关）。只有 Dispose 交还共享槽时才置 0。
        _textAtlasSrv = null; _textAtlas = null;
        _badgeSrv = null; _badgeTex = null; _badgeText = "";
        // 输入预览纹理跟着设备一起没了：指纹清空，下一次按键/改字形会重新烘一张
        _editSrv = null; _editTex = null;
        _editPrevW = _editPrevH = 0;
        lock (_editPreviewLock) { _editPrevKey = ""; _editPendingUpload = false; _editPendingBgra = null; }
        _samp = null; _rsNoCull = null; _uiCb = null;
        _overlayPs = null; _vs = null;
        _rtv = null; _backBuffer = null; _swapChain = null;
        _ctx = null!;
    }

    /// <summary>
    /// 设备丢失后原地重建 D3D 资源。成功后帧会自动重传（<c>_hasFrame=false</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 本方法的创建参数必须与 <see cref="Initialize"/> 中的创建逻辑保持一致
    /// （交换链格式/缓冲数/常量缓冲大小等）。修改任一处时请同步另一处 ——
    /// 设备丢失是罕见路径，自动化测试难以覆盖，不同步只会在真机 TDR 时才暴露。
    /// </remarks>
    private bool TryRebuildDeviceResources()
    {
        try
        {
            LogService.Info("HdrCapture",
                $"尝试重建 D3D 资源（第 {_deviceLostRecoveryAttempts} 次）", LogCategory.Capture);

            // ① 释放旧资源
            ReleaseDeviceResources();

            // ② 重建/换新设备
            // 共享模式：缓存那块若真的丢了就整体换新（见 ReplaceCachedDevice 的锁内判断），
            // 没丢就跟着换新的窗口一起用同一块；自建模式：照旧本窗独建。
            var (dev, ctx) = _ownsDevice ? NewDevice() : ReplaceCachedDevice();
            _device = dev;
            _ctx = ctx;

            // ③ 重建交换链 + 后台缓冲 + RTV（参数与 Initialize 一致）
            using (var dxgiD = _device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiD.GetAdapter())
            using (var factory = adapter.GetParent<IDXGIFactory7>())
            {
                var desc = CreateOverlayDesc(_winW, _winH);
                _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
                factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
                using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
                ApplyOverlayColorSpace(sc3);
            }
            _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(_backBuffer);

            // ④ 重建着色器 / 常量缓冲 / 采样器（与 Initialize 同样的双路径 cso 查找）
            string sd = Path.Combine(AppContext.BaseDirectory, "data", "Shaders");
            string sdAlt = Path.Combine(AppContext.BaseDirectory, "Shaders");
            static string? FindCso(string dir, string name) =>
                File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;

            string? vsp = FindCso(sd, "FullscreenVS.hlsl.cso") ?? FindCso(sdAlt, "FullscreenVS.hlsl.cso");
            if (vsp is not null) _vs = _device.CreateVertexShader(File.ReadAllBytes(vsp), null);
            // 注：原实现在此重建 _ps（ToneMapping PS），但它是死代码（从未绑定），已一并删除。

            try
            {
                string? osp = FindCso(sd, "OverlayComposite.hlsl.cso") ?? FindCso(sdAlt, "OverlayComposite.hlsl.cso");
                if (osp is not null && _vs is not null)
                {
                    _overlayPs = _device.CreatePixelShader(File.ReadAllBytes(osp));
                    // 必须与 Initialize 中的 UiCbByteWidth 一致（HLSL cbuffer 声明大小）
                    const uint UiCbByteWidth = 6160;
                    _uiCb = _device.CreateBuffer(new BufferDescription
                    {
                        ByteWidth = UiCbByteWidth,
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ConstantBuffer,
                        CPUAccessFlags = CpuAccessFlags.Write
                    });
                    _useGpuCompositor = Environment.GetEnvironmentVariable("TTC_GPU_COMPOSITE") != "0";
                    _rsNoCull = _device.CreateRasterizerState(new RasterizerDescription
                    {
                        FillMode = FillMode.Solid,
                        CullMode = CullMode.None
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("HdrCapture", $"重建 GPU 合成资源失败，回退 CPU: {ex.Message}", LogCategory.Capture);
                _overlayPs?.Dispose(); _overlayPs = null;
                _uiCb?.Dispose(); _uiCb = null;
                _useGpuCompositor = false;
            }

            _samp = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            });

            // ⑤ 纹理交给惰性重建：_frame 仍保留，RenderCore 会因 _hasFrame=false
            //    自动重新 UploadFrame（UploadFrame 内部按需创建 _desktopTex / _pooledStaging）。
            _hasFrame = false;
            _atlasScale = 0;   // 文字图集也需按新设备重建

            _deviceLost = false;
            LogService.Info("HdrCapture", "D3D 资源重建成功，帧将自动重传", LogCategory.Capture);
            RequestRedraw();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"D3D 资源重建失败: {ex.Message}", LogCategory.Capture);
            return false;
        }
    }

    /// <summary>统一帧呈接入口 (2026-08-26 修复: VRR 路径彻底禁用)。
    /// ═══ 根因确认: 交换链已改为不带 ALLOW_TEARING (04:0x 修复), 但 PresentFrame
    /// 仍走 _vrrSupported=true 的 Present(1, AllowTearing) 分支 → DXGI 规范不允许
    /// 无标志交换链传 ALLOW_TEARING → Present 静默失败 (不抛异常) → 画面永不更新。
    /// 最终方案: 无论 VRR 检测结果如何, 一律 Present(0, None) — 截图工具不需要 VRR
    /// 的毫秒级延迟优化, 稳定可靠优先。</summary>
    private void PresentFrame()
    {
        if (_swapChain is null) return;
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>每次渲染前重新获取后台缓冲 + RTV（FlipSequential Present 后缓冲翻转）。</summary>
    private void EnsureBackBuffer()
    {
        if (_swapChain is null) return;
        var bb = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        if (bb is null) return; // 获取失败
        // ═══ 2026-08-25 修复: NativePointer 比较替代 COM 引用比较 ═══
        // bb == _backBuffer 在 Vortice 中比较的是托管包装引用, 不是底层 COM 指针。
        // FlipSequential 交换链 Present 后 GetBuffer(0) 返回的是不同的内部缓冲,
        // 但 Vortice 可能返回相同包装对象 → 错误跳过重建 → RTV 指向过期缓冲。
        if (bb.NativePointer == _backBuffer?.NativePointer) { bb.Dispose(); return; }
        _backBuffer?.Dispose();
        _rtv?.Dispose();
        _backBuffer = bb;
        _rtv = _device.CreateRenderTargetView(bb);
        Interlocked.Increment(ref _mRtvRebuilds);   // F4 仪表：指针真的换了才计（早退那一趟不算"每帧重建"）
    }

    /// <summary>GPU 合成：桌面帧 + UI 覆盖层一次绘制到后台缓冲（Phase 2）。</summary>
    private void RenderGpuComposite()
    {
        // 1. 工具栏文字图集（按当前 DPI 构建，失效重建）
        EnsureTextAtlas();

        // 2. 更新常量缓冲（UI 状态）
        UpdateUiConstantBuffer();

        // 3. 绘制：全屏三角形 + OverlayComposite PS
        _ctx.RSSetViewport(0, 0, _winW, _winH);
        _ctx.RSSetState(_rsNoCull); // 不剔除 (双保险, VS 绕序已改 CW)
        _ctx.OMSetRenderTargets(_rtv!);
        // 2026-08-11: IASetInputLayout(null) —— 残留 InputLayout 会让 Draw 无输出（当时的对照对象是
        // Core 的 GPU 色调映射路径；该类已于 2026-10-01 作为死资产删除，这条结论与本处实现无关）
        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_overlayPs!);
        _ctx.VSSetConstantBuffer(0, _uiCb!);
        _ctx.PSSetConstantBuffer(0, _uiCb!);
        _ctx.PSSetShaderResource(0, _desktopSrv!);
        _ctx.PSSetShaderResource(1, _textAtlasSrv ?? _desktopSrv!); // 图集缺失时兜底绑定桌面（无文字）
        // 徽标纹理缺失时同样兜底绑定桌面：SizeBadgePos.w<=0 本来就不进采样分支
        _ctx.PSSetShaderResource(2, _badgeSrv ?? _desktopSrv!);
        _ctx.PSSetShaderResource(3, _editSrv ?? _desktopSrv!); // 输入中文字预览（EditPos.z<=0 不采样）
        _ctx.PSSetSampler(0, _samp!);
        _ctx.Draw(3, 0);
    }

    private System.Numerics.Vector4[] _atlasSubs = new System.Numerics.Vector4[56];

    /// <summary>
    /// 确保工具栏文字图集存在（按 _uiScale 构建）。
    /// 布局 2048×1152: 行0 = 主工具栏 8 按钮 (槽 0-7), 行1 = 标注工具栏 10 项 + 提示 (槽 8-18),
    /// 文字层 = 2048×512 装货区（槽 19-26, 按实测位图尺寸 shelf 打包, 不缩放不拉伸）,
    /// 悬停标题 (槽 27), 主工具条逐钮悬停提示 8 个 (槽 28-35, shader 按 hover 取 TextSubs[28+hover]),
    /// 静置操作提示 (槽 36, 未框选时居中)。
    /// 按钮文字/提示一律取自 ToolbarContract（契约单源）——本文件不许再出现按钮文案字面量。
    /// </summary>
    /// <summary>图集的 CPU 腿（整张排版 + 一次读回）的产物，连同它自己花掉的毫秒数。
    /// IdleW/IdleH/DdBakedRows 是排版时写进实例字段的三个读数（静置提示尺寸、下拉已烘行数）——
    /// 从缓存取布局时必须连同它们一起恢复，否则静置提示会按 1×1 贴、滚轮也会按 0 行夹。</summary>
    private readonly record struct AtlasLayout(byte[] Bytes, System.Numerics.Vector4[] Subs, long Ms, long ReadMs, long MergeMs,
        int IdleW, int IdleH, int DdBakedRows);

    // ═══ 图集跨窗缓存（2026-10-30）═══
    // 每扇预览窗都是新对象，但同一份 (DPI/字体/文案/下拉状态/悬停标题/文字层) 烘出来的是同一张图集。
    // 没有缓存时第二扇窗起仍要重排 + 读回 11.5MB（本机实测首帧里"等预热 16–33ms + 上传 2–5ms"）。
    // 键必须并进 BuildAtlasLayout 实际读到的【全部】输入 —— 漏一项就是"显示的是旧字，而界面看着完全正常"。
    private static readonly object s_atlasCacheLock = new();
    private static AtlasLayout? s_atlasCache;
    private static string? s_atlasCacheKey;
    // 选区起点预热是否已在途（0=空闲 1=已投递）：连按截图键可能连着来两次起点，同一张图集不值得烘两遍
    //（两趟同键排版还会并发写壳上的 _ddBakedRows/_idleHintW，读数就不可信了）。
    private static int s_atlasPrewarmInFlight;
    // 诊断开关：TTC_ATLAS_CACHE=0 关掉跨窗缓存（真机 A/B 用；默认开）
    private static readonly bool s_atlasCacheEnabled = Environment.GetEnvironmentVariable("TTC_ATLAS_CACHE") != "0";

    /// <summary>图集缓存的键 = <see cref="BuildAtlasLayout"/> 实际读到的每一个输入。
    /// 改烘焙逻辑时先问"它多读了什么"，多读一项就要多并一项 —— 否则界面会安静地显示旧字。</summary>
    private string BuildAtlasCacheKey()
    {
        var sb = new System.Text.StringBuilder(512);
        sb.Append(_uiScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(_textFontFamily).Append('|')
          .Append(_textFontSizeDip.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(_ddOpen).Append('|').Append(_ddTop).Append('|').Append(_hoverTitle ?? "").Append('|');
        foreach (var b in ToolbarContract.Main) sb.Append(b.Glyph).Append(b.Text).Append(b.Hint).Append('\u0001');
        foreach (var t in ToolbarContract.Anno) sb.Append(t.Glyph).Append(t.Text).Append('\u0001');
        sb.Append(ToolbarContract.AnnoHint).Append('\u0001').Append(ToolbarContract.IdleHint).Append('\u0001');
        sb.Append(MosaicModeRectText).Append('\u0001').Append(MosaicModeBrushText).Append('|');
        int n = Math.Min(DdItemCount(), 64);
        for (int i = 0; i < n; i++) sb.Append(DdItemText(i)).Append('\u0001');
        // 文字层预览也烘进图集（槽 19-26）：新窗口本来没有图层，键里带上签名，
        // 才不会把上一扇窗里用户刚打的字当成这扇窗自己的预览。
        foreach (var layer in _annoManager.GetLayersSnapshot())
        {
            if (layer is not TextLayer t || !t.IsVisible) continue;
            sb.Append(t.Id).Append('\u0001').Append(t.Text).Append('\u0001')
              .Append(t.FontFamily).Append('\u0001')
              .Append(t.FontSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\u0001')
              .Append(t.Bold).Append(t.Italic).Append('\u0001')
              .Append(t.MaxWidth.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\u0001');
        }
        return sb.ToString();
    }

    /// <summary>开窗时先问一句：这一套输入的图集已经在缓存里了吗（在就不用再排那一趟后台排版）。</summary>
    private bool AtlasCacheHot()
    {
        if (!s_atlasCacheEnabled) return false;
        var key = BuildAtlasCacheKey();
        lock (s_atlasCacheLock) return s_atlasCacheKey == key && s_atlasCache is not null;
    }

    private bool TryTakeCachedAtlas(out AtlasLayout layout)
    {
        layout = default;
        if (!s_atlasCacheEnabled) return false;
        var key = BuildAtlasCacheKey();
        lock (s_atlasCacheLock)
        {
            if (s_atlasCacheKey == key && s_atlasCache is { } hit) { layout = hit; return true; }
        }
        layout = default;
        return false;
    }

    private static void StoreAtlasCache(string key, AtlasLayout layout)
    {
        lock (s_atlasCacheLock) { s_atlasCacheKey = key; s_atlasCache = layout; }
    }

    /// <summary>
    /// 选区起点就把图集 CPU 腿丢到后台。起点时窗口还没 CreateWindow（拿不到 DPI），所以只拿 HMONITOR
    /// 查一次缩放，用一个无设备壳提前烘；产物由 <see cref="BuildAtlasLayout"/> 自己写进跨窗缓存，
    /// 真窗口建好后 <see cref="TryTakeCachedAtlas"/> 直接命中，首帧只剩上传。
    /// </summary>
    internal static void PrewarmAtlasForMonitor(nint hmonitor)
    {
        // 设备预热与图集缓存是两个开关：图集缓存关了，设备该建还得建（首扇窗要用）。
        WarmSharedDevice();

        if (!s_atlasCacheEnabled) return;   // 尊重 TTC_ATLAS_CACHE=0：缓存关了，烘出来也没人取

        var (scale, key, shell) = AtlasKeyForMonitor(hmonitor);
        lock (s_atlasCacheLock)
        {
            // 已经有同键的干净图集 ⇒ 真窗口也会直接命中，这一趟不必烘（热路径不打日志）
            if (s_atlasCacheKey == key && s_atlasCache is not null) return;
        }
        if (Interlocked.CompareExchange(ref s_atlasPrewarmInFlight, 1, 0) != 0) return;   // 已有一趟在跑

        LogService.Info("HdrCapture", $"图集预热(选区起点): 投递 scale={scale:0.##}", LogCategory.UI);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                shell.BuildAtlasLayout();
                LogService.Info("HdrCapture", $"图集预热(选区起点): 完成 {sw.ElapsedMilliseconds}ms", LogCategory.UI);
            }
            catch (Exception ex)
            {
                LogService.Warn("HdrCapture", $"图集预热(选区起点) 失败: {ex.Message}", LogCategory.UI);
            }
            finally
            {
                Interlocked.Exchange(ref s_atlasPrewarmInFlight, 0);
            }
        });
    }

    /// <summary>探针口：这块屏的"默认态图集"是否已经躺在缓存里（预热落地与否的唯一判据）。
    /// 缓存被关时恒 false —— 调用方要自己按 <c>TTC_ATLAS_CACHE</c> 决定这条算不算失败。</summary>
    internal static bool AtlasCacheWarmForMonitor(nint hmonitor)
    {
        if (!s_atlasCacheEnabled) return false;
        var (_, key, _) = AtlasKeyForMonitor(hmonitor);
        lock (s_atlasCacheLock) return s_atlasCacheKey == key && s_atlasCache is not null;
    }

    /// <summary>按显示器查缩放并算默认态缓存键（顺带把算键用的壳交回去，投递那趟要拿它跑排版）。
    /// 查询失败（HMONITOR 无效 / 老系统）一律按 100%：预热只是加速，不许让截图流程抛异常。</summary>
    private static (double Scale, string Key, HdrCaptureWindow Shell) AtlasKeyForMonitor(nint hmonitor)
    {
        double scale;
        try
        {
            int hr = GetDpiForMonitor(hmonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            scale = hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        }
        catch { scale = 1.0; }
        var shell = new HdrCaptureWindow((float)scale);
        return (scale, shell.BuildAtlasCacheKey(), shell);
    }

    /// <summary>
    /// 图集 = 两段，分开写是因为它们能待的地方不一样：
    /// <para>① <see cref="BuildAtlasLayout"/> 只碰 Win2D 的【进程级共享设备】⇒ 可以搬到后台线程，
    /// 于是在开窗时就开跑，把那条 4K 实测 86ms 的 DirectWrite 排版盖进"窗口初始化 + 区域检测 + 首帧上传"
    /// 那段时间里（那三段本机实测约 350ms）。② 上传碰本窗口自己的 <c>_device</c>/<c>_ctx</c>（ImmediateContext
    /// 非线程安全）⇒ 必须留在渲染线程。</para>
    /// <para>预热产物只在【投递之后没有脏标记被置起、且缩放没变】时才用；否则作废、现地重排一遍。
    /// 用"等它跑完"而不是"另起一遍"：这一条腿无论如何都要花那么多 CPU，重跑等于付两遍，
    /// 还会让两个排版并发写 <c>_ddBakedRows</c>/<c>_idleHintW</c> 这类共享读数。</para>
    /// </summary>
    private void EnsureTextAtlas()
    {
        if (_textAtlas is not null && Math.Abs(_atlasScale - _uiScale) < 0.01
            && !_annoTextSlotsDirty && !_uiTextDirty)
            return;

        // 先清脏标记再重建：重建途中 UI 线程再置脏，不会被这轮末尾的清零吞掉（旧写法会丢一次更新）
        // 取走预热产物也必须排在读脏标记之后、清之前 —— 脏了就说明这段文字已经不是开窗时那一段了。
        var pending = _pendingAtlasLayout;
        _pendingAtlasLayout = null;
        bool prewarmed = pending is not null
            && !_annoTextSlotsDirty && !_uiTextDirty
            && Math.Abs(_pendingAtlasScale - _uiScale) < 0.01;
        _uiTextDirty = false;
        _annoTextSlotsDirty = false;
        // ── F4 仪表：每一次重建都计时（含 hover/脏标记逼出来的整张 2048×1408 重排）──
        // 刻意只用自己的一对累加字段，不去碰下面那组 swAtlas/_atlasTotalMsAtlas：
        // 那组是【首帧】分解打点的输入（只在 _textAtlas 为 null 时起表），改了它的存活条件
        // 就等于改首帧那行日志的读数 —— 仪表不许动被测量的口径。
        long tAtlas = PerfMeter.Now();
        // 首建打点：图集 = 几十条 DirectWrite 排版 + 2048×1408 全量上传，而它现在落在首帧合成的
        // 关键路径上（RenderGpuComposite 的第一句）。命中上面的早退时不计时，逐帧零开销。
        var swAtlas = _textAtlas is null ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            AtlasLayout layout;
            bool fromCache = false;
            if (prewarmed)
            {
                pending!.Wait();          // 异常在这里原样抛出 ⇒ 交给下面同一个 catch，与现地排版同一条失败路
                layout = pending.Result;
                // 投递→取用隔了多久：把"投递太晚"直接量出来（比首帧那条"等它跑完"多含一段排队）
                if (_atlasPrewarmQueuedAtMs > 0)
                    LogService.Debug("HdrCapture", $"图集预热: 投递→取用 {Environment.TickCount64 - _atlasPrewarmQueuedAtMs}ms");
            }
            else if (TryTakeCachedAtlas(out var hit))
            {
                // 第二扇窗起：排版与 11.5MB 读回都不再做，只剩上传（本机 2–5ms）
                layout = hit;
                fromCache = true;
            }
            else
            {
                layout = BuildAtlasLayout();
            }
            // 「问它 → bytes 到手」= 渲染线程在这条腿上等预热的净时长（未命中预热时≈本地排版那一趟）
            long askToHaveMs = swAtlas?.ElapsedMilliseconds ?? 0;
            if (swAtlas is not null)
            {
                _rasterMsAtlas = prewarmed || fromCache ? 0 : layout.Ms;   // 关键路径上的排版；预热/缓存命中就是 0
                _atlasPrewarmMsAtlas = prewarmed ? layout.Ms : 0;    // 预热那一趟自己花了多少（不在关键路径上）
                _atlasPrewarmDrawMs = prewarmed ? layout.Ms - layout.ReadMs - layout.MergeMs : 0;
                _atlasPrewarmReadMs = prewarmed ? layout.ReadMs : 0;
                _atlasPrewarmMergeMs = prewarmed ? layout.MergeMs : 0;
                _atlasQueueMsAtlas = prewarmed
                    ? Math.Max(0L, _atlasPrewarmStartedAtMs - _atlasPrewarmQueuedAtMs) : 0;
            }
            UploadTextAtlas(layout.Bytes);
            _atlasSubs = layout.Subs;
            // 从缓存取的布局要把它烘出来的三个读数一起搬回来（见 AtlasLayout 注释）：
            // 少搬这两个尺寸 ⇒ 静置提示按 1×1 贴到中间，探针"静置提示"那一相会红。
            _idleHintW = layout.IdleW; _idleHintH = layout.IdleH;
            _ddBakedRows = layout.DdBakedRows;
            _atlasScale = _uiScale;
            if (swAtlas is not null)
            {
                _atlasPrewarmWaitMs = prewarmed ? askToHaveMs : 0;                     // 命中预热才有"等它跑完"
                _atlasTotalMsAtlas = swAtlas.ElapsedMilliseconds;                      // 问它 → 上传完 = 图集整腿
                _atlasUploadMs = Math.Max(0L, _atlasTotalMsAtlas - askToHaveMs);       // 剩下的才是纯上传
            }
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"文字图集构建失败（GPU 合成降级）: {ex.Message}");
            _textAtlasSrv?.Dispose(); _textAtlasSrv = null;
            _textAtlas?.Dispose(); _textAtlas = null;
        }
        finally
        {
            // F4 仪表：失败的那一趟同样要计（"重建了几次"包含没做成的那次，否则读数会把故障藏起来）
            PerfMeter.Record(ref _mAtlasRebuilds, ref _mAtlasUs, ref _mAtlasMaxUs,
                PerfMeter.SinceMicro(tAtlas));
        }
    }

    /// <summary>Stopwatch 时间戳差换算成毫秒（图集那三条子腿各自只有几毫秒，用 TickCount64 会读到一堆 0）。</summary>
    private static long ElapsedMsSince(long startTimestamp)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000
           / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>图集的 CPU 腿：整张排版 + 一次读回。只用 Win2D 共享设备，不许出现 <c>_device</c>/<c>_ctx</c>
    /// （那正是它能被搬到后台线程、从而盖进开窗那段时间的前提；判据钉的就是这一条）。</summary>
    private AtlasLayout BuildAtlasLayout()    {
        {
            _atlasPrewarmStartedAtMs = Environment.TickCount64;   // 开跑时刻（这必须是本方法第一句，否则量不到排队）
            var swLayout = System.Diagnostics.Stopwatch.StartNew();
            string cacheKey = BuildAtlasCacheKey();               // 入缓存用；也代表这一趟读到的输入
            // 布局: 2048×1408
            const int atlasW = AtlasW, atlasH = AtlasH;
            const int row1Y = 128;
            const int textShelfY = 256, textShelfH = 512;
            var mainItems = ToolbarContract.Main
                .Select(b => (text: $"{b.Glyph} {b.Text}", w: 0, h: 0))
                .ToArray();
            var annoItems = ToolbarContract.Anno
                .Select(t => (text: $"{t.Glyph} {t.Text}", w: 0, h: 0))
                .Append((text: ToolbarContract.AnnoHint, w: 0, h: 0))
                .ToArray();
            var lay = GetToolbarLayout(ToolbarContract.Main.Length);
            int btnW = lay.BtnW, btnH = lay.BtnH;
            int hintH = Math.Max(24, (int)Math.Round(30 * _uiScale));

            // 2026-10-26 用户反馈"框选字体太小"：11f 在 90×32 按钮里字高占比仅 ~1/3，
            // 提到 18f（字高/按钮 ≈ 0.56，与 SDR SelectionOverlay 的 28/48 观感一致）。
            float fontSize = ToolbarFontSize * (float)_uiScale;
            int hintW = ToolbarContract.MainHintWidthPx(fontSize);
            // 标注行 = 10 按钮 + 末尾提示，必须整行装进 2048：200% DPI 下不夹一下就会越界写进下一行
            int annoRowUsed = 10 * (btnW + 8);
            if (annoRowUsed + hintW > atlasW) hintW = Math.Max(120, atlasW - annoRowUsed);
            for (int i = 0; i < mainItems.Length; i++) { mainItems[i].w = btnW; mainItems[i].h = btnH; }
            for (int i = 0; i < 10; i++) { annoItems[i].w = btnW; annoItems[i].h = btnH; }
            annoItems[10].w = hintW; annoItems[10].h = hintH;
            var subs = new System.Numerics.Vector4[56];
            // 共享设备：不 Dispose（见 AnnotationRasterizer.SharedCanvasDevice 的说明）
            var device = AnnotationRasterizer.SharedCanvasDevice;
            // ═══ 2026-10-29 整张图集一次排版：一格一个 CanvasRenderTarget 改为全图一个 ═══
            // 原先每个格子新建一张渲染目标 + 一次 GetPixelBytes() 再逐行拷进 atlasBytes。
            // 实测首帧那 ~250ms 里，DrawText 本身只占 50–100ms（把 DrawText 整段跳过的变异跑
            // 仍读到 176–225ms），剩下的都是这几十次"建目标/会话/读回/销毁"。
            // 现在一次画进同一张 2048×1408 的目标、只在末尾读回一次；格子坐标与 subs 完全不变，
            // 未画到的区域仍是 Clear(Transparent) 的全零，与旧的 zero-init 缓冲同值。
            using var atlasTarget = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, atlasW, atlasH, 96);
            byte[] atlasBytes;
            // 文字图层预览是"别处烘好的位图"（AnnotationRasterizer.RasterizeText），只能事后并入 CPU 缓冲；
            // 先记下位置，等整张目标读回来再逐行拷（图集槽位互不重叠，先后顺序不影响结果）。
            var pendingPreviews = new List<(int X, int Y, int W, int H, byte[] Pixels)>();
            using (var ds = atlasTarget.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                void DrawSlot(int x, int y, int w, int h, string text, float px)
                {
                    if (w <= 0 || h <= 0 || string.IsNullOrEmpty(text)) return; // 与旧的"这一格不入库"同语义
                    try
                    {
                        ds.DrawText(text, x, y, w, h, Microsoft.UI.Colors.White,
                            new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                            {
                                FontFamily = "Microsoft YaHei UI",
                                FontSize = px,
                                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                            });
                    }
                    catch
                    {
                        // 单格失败只丢这一格（旧实现每格自带 try/catch，不能让一格拖垮整张图集）
                    }
                }
                // 行 0: 主工具栏 7 按钮 (槽 0-6)
                int cursorX = 0;
                for (int i = 0; i < mainItems.Length; i++)
                {
                    int dstX = cursorX;
                    subs[i] = new System.Numerics.Vector4(dstX / (float)atlasW, 0, mainItems[i].w / (float)atlasW, mainItems[i].h / (float)atlasH);
                    DrawSlot(dstX, 0, mainItems[i].w, mainItems[i].h, mainItems[i].text, fontSize);
                    cursorX += mainItems[i].w + 8;
                }
                // 行 1: 标注工具栏 11 项 (槽 8-18)
                cursorX = 0;
                for (int i = 0; i < annoItems.Length; i++)
                {
                    int dstX = cursorX;
                    subs[8 + i] = new System.Numerics.Vector4(dstX / (float)atlasW, row1Y / (float)atlasH, annoItems[i].w / (float)atlasW, annoItems[i].h / (float)atlasH);
                    DrawSlot(dstX, row1Y, annoItems[i].w, annoItems[i].h, annoItems[i].text, fontSize);
                    cursorX += annoItems[i].w + 8;
                }
                // 文字层装货区 (槽 19-26, y=256..768): 每层按【实测位图】原尺寸入库。
                // 旧实现固定 256×64 槽 + 字距拉伸，字号/字宽/折行全被改写；现在子矩形就是位图本身，
                // shader 把它贴到图层内框上，预览与输出用的是同一张位图（见 TextLayer 注释）。
                int slot = 0;
                int shelfX = 0, shelfTop = textShelfY, shelfRowH = 0;
                // ═══ 2026-08-16 P1-3: 快照遍历 (渲染线程与 UI 线程修改互斥) ═══
                foreach (var layer in _annoManager.GetLayersSnapshot().Where(l => l is TextLayer && l.IsVisible))
                {
                    if (slot >= 8) break;
                    var tl = (TextLayer)layer;
                    if (AnnotationRasterizer.RasterizeText(tl.Text, tl.FontSize, tl.FontFamily,
                            tl.Bold, tl.Italic, tl.MaxWidth) is not var (bmp, bw, bh))
                    {
                        subs[19 + slot] = System.Numerics.Vector4.Zero; // 没有位图 ⇒ 不采样（输出仍会画背景框）
                        slot++;
                        continue;
                    }
                    // shelf 打包：一行放不下就换行；整块装货区放不下就放弃预览（输出合成本来就不走图集）
                    if (shelfX + bw > atlasW) { shelfTop += shelfRowH; shelfX = 0; shelfRowH = 0; }
                    if (shelfTop + bh > textShelfY + textShelfH || bw > atlasW)
                    {
                        subs[19 + slot] = System.Numerics.Vector4.Zero;
                        LogService.Warn("HdrCapture",
                            $"文字预览槽不足（{bw}x{bh}），图层不参与预览；输出合成不受影响", LogCategory.UI);
                        slot++;
                        continue;
                    }
                    int dstX = shelfX, dstY = shelfTop;
                    subs[19 + slot] = new System.Numerics.Vector4(
                        dstX / (float)atlasW, dstY / (float)atlasH, bw / (float)atlasW, bh / (float)atlasH);
                    pendingPreviews.Add((dstX, dstY, bw, bh, bmp));
                    shelfX += bw + 2;
                    shelfRowH = Math.Max(shelfRowH, bh);
                    slot++;
                }
                // 悬停窗口标题槽 (槽 27, 512×64) — QQ截图式窗口标题提示
                {
                    const int tipW = 512, tipH = 64, row3Y = textShelfY + textShelfH;
                    subs[27] = new System.Numerics.Vector4(0, row3Y / (float)atlasH, tipW / (float)atlasW, tipH / (float)atlasH);
                    string hoverTitle = _hoverTitle ?? "";
                    if (hoverTitle.Length > 0)
                        DrawSlot(0, row3Y, tipW, tipH, hoverTitle, 12f * (float)_uiScale);
                }
                // 主工具条逐钮悬停提示 (槽 28-35, 1024×64 槽格, 1024 宽是 300% DPI 下最长句也不裁的余量)。
                // 修复 2026-10-26 复核发现的老 divergence：GPU 路此前槽 7 只有"保存截图到文件"一句静态提示，
                // 悬停哪枚按钮都显示它；GDI 回退路却是逐钮的。shader 现按 hover 取 TextSubs[28+hover]。
                {
                    const int hintSlotW = 1024, hintSlotH = 64, row4Y = textShelfY + textShelfH + 64;
                    for (int i = 0; i < ToolbarContract.Main.Length; i++)
                    {
                        int dstX = (i % 2) * hintSlotW;
                        int dstY = row4Y + (i / 2) * hintSlotH;
                        subs[28 + i] = new System.Numerics.Vector4(dstX / (float)atlasW, dstY / (float)atlasH, hintW / (float)atlasW, hintH / (float)atlasH);
                        DrawSlot(dstX, dstY, hintW, hintH, ToolbarContract.Main[i].Hint, fontSize);
                    }
                }
                // 静置操作提示 (槽 36, 整行 2048×64) —— 未框选/未拖拽时居中显示一句操作说明。
                {
                    const int idleRowY = 1088, idleH = 64;
                    int idleW = Math.Min(atlasW, ToolbarContract.EstimateTextWidthPx(ToolbarContract.IdleHint, fontSize));
                    subs[36] = new System.Numerics.Vector4(0, idleRowY / (float)atlasH, idleW / (float)atlasW, idleH / (float)atlasH);
                    DrawSlot(0, idleRowY, idleW, idleH, ToolbarContract.IdleHint, fontSize);
                    _idleHintW = idleW; _idleHintH = idleH;
                }
                // 第二行「马赛克方式」两个标签 (槽 38-39) —— 复用行 0 右侧空闲区
                // （行 0 只用到 mainItems.Length×(btnW+8)，2048 宽还剩近千像素）。
                {
                    int optX = mainItems.Length * (btnW + 8) + 16;
                    int optW = Math.Max(70, (int)Math.Round(96 * _uiScale));
                    int optH = btnH;
                    string[] labels = [MosaicModeRectText, MosaicModeBrushText];
                    for (int i = 0; i < labels.Length; i++)
                    {
                        if (optX + optW > atlasW) break;
                        subs[38 + i] = new System.Numerics.Vector4(optX / (float)atlasW, 0, optW / (float)atlasW, optH / (float)atlasH);
                        DrawSlot(optX, 0, optW, optH, labels[i], fontSize);
                        optX += optW + 8;
                    }
                }
                // 文字行的格子文字与下拉列表（槽 40-55）—— 内容随用户选择变化，
                // 与按钮文案不同，只能在打开/翻动列表时整张重烘（_uiTextDirty）。
                // 带宽固定 256px，格高随 DPI 长：装不下的格子一个都不许往列表里画（旧写法整格置零 ⇒
                // 250% DPI 下列表后几行是空白条）。可见行数与烘焙行数都取 PackTextBand 的实测容量。
                {
                    int cellH = OptionsRowHeight();
                    var (famW, sizeW, biW, _) = TextRowWidths((float)_uiScale);
                    // 烘的格宽必须等于画的面板宽：shader 把整个子矩形铺进面板，烘宽了字就被横向压扁
                    int panelW = DdPanelWidth();
                    int itemCap = DdBandCapacityAt((float)_uiScale, panelW);
                    float uiFpx = Math.Max(11f, 15f * (float)_uiScale);
                    const int fixedCells = 4;
                    string[] fixedTexts = [_textFontFamily,
                        _textFontSizeDip.ToString("0", System.Globalization.CultureInfo.InvariantCulture), "B", "I"];
                    int[] fixedW = [famW, sizeW, biW, biW];
                    int[] cw = new int[fixedCells + itemCap], ch = new int[fixedCells + itemCap];
                    for (int i = 0; i < fixedCells; i++) { cw[i] = fixedW[i]; ch[i] = cellH; }
                    for (int i = 0; i < itemCap; i++) { cw[fixedCells + i] = panelW; ch[fixedCells + i] = cellH; }
                    var rects = PackTextBand(atlasW, AtlasH - AtlasBandH, AtlasBandH, cw, ch);
                    void Bake(int slot, string text, (int X, int Y, int W, int H) r)
                    {
                        subs[slot] = new System.Numerics.Vector4(
                            r.X / (float)atlasW, r.Y / (float)atlasH, r.W / (float)atlasW, r.H / (float)atlasH);
                        DrawSlot(r.X, r.Y, r.W, r.H, text, uiFpx);
                    }
                    bool dropped = false;
                    for (int i = 0; i < fixedCells; i++)
                    {
                        if (rects[i].H > 0) { Bake(40 + i, fixedTexts[i], rects[i]); continue; }
                        subs[40 + i] = System.Numerics.Vector4.Zero;
                        dropped = true;
                    }
                    int baked = 0;
                    for (int i = 0; i < itemCap; i++)
                    {
                        int idx = _ddTop + i;
                        var r = rects[fixedCells + i];
                        if (_ddOpen == 0 || idx >= DdItemCount() || r.H <= 0)
                        {
                            subs[44 + i] = System.Numerics.Vector4.Zero;
                            if (r.H <= 0) dropped = true;
                            continue;
                        }
                        Bake(44 + i, DdItemText(idx), r);
                        baked++;
                    }
                    for (int i = itemCap; i < DdMaxVisible; i++) subs[44 + i] = System.Numerics.Vector4.Zero;
                    _ddBakedRows = baked;
                    if (dropped)
                        LogService.Warn("HdrCapture",
                            $"文字带装不下：缩放 {_uiScale:0.##}、格高 {cellH}、带高 {AtlasBandH}，"
                            + $"容量 {itemCap}/{DdMaxVisible} 格 —— 有格子没烘出来（列表会缺文字）", LogCategory.UI);
                    if (itemCap != _ddCapLogged)
                    {
                        _ddCapLogged = itemCap;
                        if (itemCap < DdMaxVisible)
                            LogService.Info("HdrCapture",
                                $"缩放 {_uiScale:0.##} 下文字带只装得下 {itemCap}/{DdMaxVisible} 行下拉列表（超出部分靠滚轮翻）",
                                LogCategory.UI);
                    }
                }
            }
            // 整张一次读回（ds 已在上面 using 结束时 Flush），再把"别处烘好的"文字图层预览并进去。
            // 三条各自计时（2026-10-30）：这一腿 34–84ms 里【DirectWrite 排版】【11.5MB GPU→CPU 读回】
            // 【逐行并入预览】哪条贵，决定下一步往哪打 —— 读回贵就换 GPU 内拷贝，排版贵就缓存整张。
            long tAtlasRead = System.Diagnostics.Stopwatch.GetTimestamp();
            atlasBytes = atlasTarget.GetPixelBytes();
            long tAtlasMerge = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var p in pendingPreviews)
                for (int row = 0; row < p.H; row++)
                    Buffer.BlockCopy(p.Pixels, row * p.W * 4, atlasBytes, ((p.Y + row) * atlasW + p.X) * 4, p.W * 4);
            long atlasMergeMs = ElapsedMsSince(tAtlasMerge);
            var built = new AtlasLayout(atlasBytes, subs, swLayout.ElapsedMilliseconds,
                ElapsedMsSince(tAtlasRead), atlasMergeMs, _idleHintW, _idleHintH, _ddBakedRows);
            // 只缓存「干净」的那张工具条图集：下拉展开/悬停标题/带文字层都是窗口内的瞬态，
            // 存进去只会把常驻那张挤掉（缓存只有一个槽），下一扇窗反而要重烘 —— 真机跑
            // --preview-ui-probe 时就撞到过：第二扇窗仍读到"预热排版 21ms"，正是被下拉那张挤掉了。
            bool pristine = _ddOpen == 0 && string.IsNullOrEmpty(_hoverTitle)
                && !_annoManager.GetLayersSnapshot().Any(l => l is TextLayer t && t.IsVisible);
            // 脏标记意味着用户刚改过字/图层：这一张可能是两段状态拼出来的，不能入缓存。
            if (s_atlasCacheEnabled && pristine && !_uiTextDirty && !_annoTextSlotsDirty)
                StoreAtlasCache(cacheKey, built);
            return built;
        }
    }

    /// <summary>图集的 GPU 腿：整张上传。<b>必须留在渲染线程</b> —— 用的是本窗口自己的
    /// <c>_device</c>/<c>_ctx</c>，而 ImmediateContext 非线程安全（与 <c>_gpuLock</c> 那条纪律同源）。</summary>
    private void UploadTextAtlas(byte[] atlasBytes)
    {
        const int atlasW = AtlasW, atlasH = AtlasH;
        // 纹理复用：尺寸固定，重烘只换内容（每敲一个字/翻一屏列表都重建 9 MB 纹理太贵）
        if (_textAtlas is null)
        {
            _textAtlas = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = atlasW, Height = atlasH, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
            });
            _textAtlasSrv?.Dispose();
            _textAtlasSrv = _device.CreateShaderResourceView(_textAtlas);
        }
        // staging 上传（与项目既有风格一致）
        using (var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = atlasW, Height = atlasH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write
        }))
        {
            var m = _ctx.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            if (m.DataPointer != 0)
            {
                try
                {
                    unsafe
                    {
                        fixed (byte* src = atlasBytes)
                        {
                            int rowPitch = atlasW * 4;
                            for (int row = 0; row < atlasH; row++)
                                Buffer.MemoryCopy(src + row * rowPitch, (byte*)m.DataPointer.ToPointer() + row * m.RowPitch, rowPitch, rowPitch);
                        }
                    }
                }
                finally { _ctx.Unmap(staging, 0); } // 异常路径也必须 Unmap，否则该资源永久无法再写入
            }
            _ctx.CopyResource(_textAtlas, staging);
        }
    }

    /// <summary>
    /// 尺寸徽标纹理：文字变化时才重烘（拖拽中每个像素都可能改数，但一次只有十几字节的排版）。
    /// 独立于图集 —— 图集是 2048×704 全量上传，不能每帧重建。
    /// </summary>
    private void EnsureSizeBadge(string text)
    {
        if (_badgeTex is not null && _badgeText == text && Math.Abs(_badgeScale - _uiScale) < 0.01)
            return;

        try
        {
            float fontPx = 16f * (float)_uiScale;
            int bw = Math.Min(BadgeTexW, ToolbarContract.EstimateTextWidthPx(text, fontPx) + (int)Math.Round(8 * _uiScale));
            int bh = Math.Min(BadgeTexH, Math.Max(24, (int)Math.Round(28 * _uiScale)));
            var bmp = RenderTextBitmap(AnnotationRasterizer.SharedCanvasDevice, text, bw, bh, fontPx);
            if (bmp is null) return;

            if (_badgeTex is null)
            {
                _badgeTex = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = BadgeTexW, Height = BadgeTexH, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write
                });
                _badgeSrv = _device.CreateShaderResourceView(_badgeTex);
            }

            var m = _ctx.Map(_badgeTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            if (m.DataPointer != 0)
            {
                try
                {
                    unsafe
                    {
                        fixed (byte* src = bmp)
                        {
                            int rowBytes = bw * 4;
                            for (int row = 0; row < bh; row++)
                                Buffer.MemoryCopy(src + row * rowBytes, (byte*)m.DataPointer.ToPointer() + row * m.RowPitch, rowBytes, rowBytes);
                        }
                    }
                }
                finally { _ctx.Unmap(_badgeTex, 0); }
            }

            _badgeText = text; _badgeScale = _uiScale;
            _badgeW = bw; _badgeH = bh;
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"尺寸徽标纹理构建失败（徽标隐藏）: {ex.Message}");
            _badgeSrv?.Dispose(); _badgeSrv = null;
            _badgeTex?.Dispose(); _badgeTex = null;
            _badgeText = "";
        }
    }

    /// <summary>
    /// 把输入框里的文字烘成一张小纹理 —— EDIT 子窗口在这块全屏交换链上一个像素都不显示，
    /// 不这么做用户就是在"闭眼打字"。内容/字形/折行宽度任一变化才重烘。
    /// <para>⚠ 只在 UI 线程算位图（与 CommitTextEdit 的 ApplyMetrics 同一把尺），
    /// 上传纹理留给渲染线程 <see cref="UploadEditPreview"/> —— D3D 立即上下文只归渲染线程。</para>
    /// </summary>
    private void SyncEditPreview()
    {
        if (_editHwnd == 0)
        {
            bool queued = false;
            lock (_editPreviewLock)
            {
                // 判据只认这把锁里的状态：_editPrevW/_editPrevH 归渲染线程、它的写端不在这把锁里，
                // 拿它当"已经清过"的凭据等于读别人的裸字段（旧写法就是这样）。
                if (_editPrevKey.Length == 0 && !_editPendingUpload) return;
                _editPendingBgra = null;
                _editPendingW = _editPendingH = 0;
                _editPendingUpload = true;
                _editPrevKey = "";
                queued = true;
            }
            if (queued) RequestRedraw();
            return;
        }

        var sb = new System.Text.StringBuilder(8192);
        int len = GetWindowTextSb(_editHwnd, sb, sb.Capacity);
        string text = len > 0 ? sb.ToString(0, len) : "";
        float fontPx = Math.Max(8f, _textFontSizeDip * (float)_uiScale);
        float maxW = Math.Clamp(Math.Abs(_sx2 - _sx1) - 12f, 120f, 1600f);
        string key = $"{text}\u0001{fontPx:F1}\u0001{_textFontFamily}\u0001{_textFontBold}\u0001{_textFontItalic}\u0001{maxW:F0}";
        bool changed;
        lock (_editPreviewLock)
        {
            // 比较与写入同一把锁：ReleaseDeviceResources（渲染线程）会把 key 清空，裸比较会漏掉那次清空
            changed = key != _editPrevKey;
            if (changed) _editPrevKey = key;
        }
        if (!changed) return;

        var bmp = text.Length == 0
            ? ((byte[] Bgra, int W, int H)?)null
            : (AnnotationRasterizer.RasterizeText(text, fontPx, _textFontFamily,
                   _textFontBold, _textFontItalic, maxW) is { } r
               && r.W <= EditTexW && r.H <= EditTexH ? r : null);
        lock (_editPreviewLock)
        {
            _editPendingBgra = bmp?.Bgra;
            _editPendingW = bmp?.W ?? 0;
            _editPendingH = bmp?.H ?? 0;
            _editPendingUpload = true;
        }
        RequestRedraw();
    }

    /// <summary>渲染线程：把 UI 线程烘好的输入预览位图传进小纹理（D3D 上下文只归渲染线程）。</summary>
    private void UploadEditPreview()
    {
        byte[]? bgra;
        int bw, bh;
        lock (_editPreviewLock)
        {
            if (!_editPendingUpload) return;
            bgra = _editPendingBgra;
            bw = _editPendingW;
            bh = _editPendingH;
            _editPendingUpload = false;
            _editPendingBgra = null;
        }
        if (bgra is null || bw <= 0 || bh <= 0)
        {
            _editPrevW = _editPrevH = 0;
            return;
        }
        try
        {
            if (_editTex is null)
            {
                _editTex = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = EditTexW, Height = EditTexH, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write
                });
                _editSrv?.Dispose();
                _editSrv = _device.CreateShaderResourceView(_editTex);
            }
            var m = _ctx.Map(_editTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            if (m.DataPointer != 0)
            {
                try
                {
                    unsafe
                    {
                        fixed (byte* src = bgra)
                        {
                            int rowBytes = bw * 4;
                            for (int row = 0; row < bh; row++)
                                Buffer.MemoryCopy(src + row * rowBytes,
                                    (byte*)m.DataPointer.ToPointer() + row * m.RowPitch, rowBytes, rowBytes);
                        }
                    }
                }
                finally { _ctx.Unmap(_editTex, 0); }
            }
            _editPrevW = bw; _editPrevH = bh;
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"输入文字预览纹理构建失败（预览隐藏）: {ex.Message}");
            _editSrv?.Dispose(); _editSrv = null;
            _editTex?.Dispose(); _editTex = null;
            _editPrevW = _editPrevH = 0;
        }
    }

    /// <summary>更新 GPU 合成常量缓冲（与 CompositeUI 的绘制逻辑一致 + 标注层数据）。</summary>
    private void UpdateUiConstantBuffer()
    {
        var cb = new UiCbData
        {
            TextSubs = new float[224],
            TextPos = new float[224],
            AnnoLayers = new float[1024]
        };
        int w = _winW, h = _winH;

        // 遮罩/边框色（sRGB → 线性, 与 CPU 路径一致）
        var (ovR, ovG, ovB, ovA) = ParseColorToLinear(AppServices.Settings.Current.OverlayColor);
        cb.OverlayColor = new System.Numerics.Vector4(ovR, ovG, ovB, ovA);
        var (bdR, bdG, bdB, _) = ParseColorToLinear(AppServices.Settings.Current.BorderColor);
        cb.BorderColor = new System.Numerics.Vector4(bdR, bdG, bdB, 1);

        // ═══ 2026-08-26 诊断日志已移除 (确认根因: cbuffer 大小不匹配) ═══

        // 选区
        if (_sx1 != _sx2 || _sy1 != _sy2)
        {
            cb.SelRect = new System.Numerics.Vector4(
                Math.Min(_sx1, _sx2), Math.Min(_sy1, _sy2),
                Math.Abs(_sx2 - _sx1), Math.Abs(_sy2 - _sy1));
        }
        else cb.SelRect = new System.Numerics.Vector4(0, 0, 0, 0);

        // 工具栏布局（与 GetToolbarLayout / Down() 命中一致; 标注模式 10 按钮 槽 8-17）
        bool isDark = App.ResolveEffectiveTheme(App.CurrentTheme) is AppThemeMode.Dark or AppThemeMode.OLED;
        if (_selComplete)
        {
            int n = _annoMode ? 10 : ToolbarContract.Main.Length;
            int subBase = _annoMode ? 8 : 0;
            var lay = ToolbarFrame(n, w, h);
            int totalW = lay.TotalW, totalH = lay.TotalH;
            int tx = lay.Tx, ty = lay.Ty;
            cb.ToolbarRect = new System.Numerics.Vector4(tx, ty, totalW, totalH);
            cb.ButtonLayout = new System.Numerics.Vector4(lay.BtnW, lay.BtnH, lay.Gap, n);
            cb.PadHandle = new System.Numerics.Vector4(lay.PadX, lay.PadY, _handleSize, 0);

            // 文字位置（帧内, 与图集子矩形对应）
            for (int i = 0; i < n; i++)
            {
                int bx = tx + lay.PadX + i * (lay.BtnW + lay.Gap);
                int by = ty + lay.PadY;
                SetV4(cb.TextPos, subBase + i, new System.Numerics.Vector4(bx, by, lay.BtnW, lay.BtnH));
            }
            // 悬停提示位置（工具栏下方/上方）。宽度按契约最长句估算（与图集提示槽同源），
            // 高度随字号 18f 加高 —— 旧写死 160 逻辑像素会让长提示在 100% DPI 裁边。
            int hintW = ToolbarContract.MainHintWidthPx(ToolbarFontSize * (float)_uiScale);
            int hintH = Math.Max(24, (int)Math.Round(30 * _uiScale));
            int hx = Math.Clamp(tx + totalW / 2 - hintW / 2, 4, w - hintW - 4);
            int hy = ty + totalH + 4;
            // 第二行（字体条 / 颜色 / 马赛克方式）占住了工具条下方：提示改到上方，别压住选项行
            if (_annoMode && _optRowVisible) hy = ty - hintH - 4;
            if (hy + hintH + 4 > h) hy = ty - hintH - 4;
            if (hy < 4) hy = ty + totalH + 4;
            // 主工具条 8 枚按钮占满 TextPos[0..7] ⇒ 提示框位置改住槽 37（标注模式仍是槽 18，与图集同源）
            SetV4(cb.TextPos, _annoMode ? 18 : 37, new System.Numerics.Vector4(hx, hy, hintW, hintH));

            // ── 第二行选项条（线色 / 马赛克方式 / 画笔粗细 / 文字字体）：画在帧里 ──
            // 槽 38 = (行左, 行上, 等宽模式的格子宽, 格高)；槽 39 = (gap, 格数, 形态, 状态)；
            // 槽 40 = 形态 3 的四格宽度 / 形态 2 的 (数值格宽, 徽标像素宽, 徽标像素高, 0)；
            // 槽 41/42/43 = 下拉面板矩形 / (可见行, 首行, 悬停, 形态) / 当前项（形态 2 的槽 43 = 粗细刻度）。
            var optG = OptionsRowForTool() ? CurrentOptionsRow() : default;
            if (OptionsRowForTool())
            {
                int optMode = OptionsRowMode();
                SetV4(cb.TextPos, 38, new System.Numerics.Vector4(optG.X, optG.Y, optG.W[0], optG.CellH));
                // 槽 43 = (当前字体/字号项, 加粗, 斜体, 0)
                SetV4(cb.TextPos, 43, optMode == 3
                    ? new System.Numerics.Vector4(DdSelIndex(), _textFontBold ? 1 : 0, _textFontItalic ? 1 : 0, 0)
                    : System.Numerics.Vector4.Zero);
                if (optMode == 3)
                {
                    SetV4(cb.TextPos, 39, new System.Numerics.Vector4(optG.Gap, optG.W.Length, optMode, _optHover + 1));
                    SetV4(cb.TextPos, 40, new System.Numerics.Vector4(
                        optG.W[0], optG.W[1], optG.W[2], optG.W[3]));
                }
                else if (optMode == 2)
                {
                    // 形态 2 没有"选中第几格"：槽 39.w 只放 悬停格+1（shader 按 optMode>=2 解）
                    SetV4(cb.TextPos, 39, new System.Numerics.Vector4(optG.Gap, optG.W.Length, optMode, _optHover + 1));
                    // 槽 43 = (当前粗细, 最小, 最大, 轨道内缩)
                    SetV4(cb.TextPos, 43, new System.Numerics.Vector4(
                        _mosaicBrushPx, MosaicBrushMinPx, MosaicBrushMaxPx, SliderInset()));
                    // 槽 40 = (数值格宽, 徽标像素宽, 徽标像素高, 0)：数值按原始大小居中贴，不铺满整格
                    EnsureSizeBadge($"{(int)MathF.Round(_mosaicBrushPx)}px");
                    SetV4(cb.TextPos, 40, new System.Numerics.Vector4(optG.W[1], _badgeW, _badgeH, 0));
                    if (_badgeSrv is not null)
                        cb.SizeBadgeSub = new System.Numerics.Vector4(
                            0, 0, _badgeW / (float)BadgeTexW, _badgeH / (float)BadgeTexH);
                }
                else
                {
                    int sel = optMode switch
                    {
                        0 => SwatchIndex(),
                        2 => 0, // 粗细用滑条，没有"选中第几格"这回事
                        _ => _mosaicMode == "Rect" ? 0 : 1,
                    };
                    // (gap, count, mode, sel + 1 + (hover + 1) * 16)
                    SetV4(cb.TextPos, 39, new System.Numerics.Vector4(optG.Gap, optG.W.Length, optMode,
                        sel + 1 + (_optHover + 1) * 16));
                    SetV4(cb.TextPos, 40, System.Numerics.Vector4.Zero);
                }

                if (_ddOpen != 0)
                {
                    var d = DdGeometry();
                    SetV4(cb.TextPos, 41, new System.Numerics.Vector4(d.X, d.Y, d.W, d.ItemH));
                    SetV4(cb.TextPos, 42, new System.Numerics.Vector4(d.Visible, _ddTop, _ddHover + 1, _ddOpen));
                }
                else
                {
                    SetV4(cb.TextPos, 41, System.Numerics.Vector4.Zero);
                    SetV4(cb.TextPos, 42, System.Numerics.Vector4.Zero);
                }
            }
            else
            {
                for (int i = 38; i <= 43; i++) SetV4(cb.TextPos, i, System.Numerics.Vector4.Zero);
            }
        }
        else
        {
            cb.ToolbarRect = System.Numerics.Vector4.Zero;
            cb.ButtonLayout = new System.Numerics.Vector4(90, 32, 4, 6);
            cb.PadHandle = new System.Numerics.Vector4(6, 5, _handleSize, 0);
        }

        cb.MousePos = new System.Numerics.Vector4(_mx, _my, 0, 0);
        cb.Flags = new System.Numerics.Vector4(_selComplete ? 1 : 0, _down ? 1 : 0, 0, isDark ? 1 : 0);
        cb.FrameSize = new System.Numerics.Vector4(w, h, 0, 0);

        // ── 窗口悬停高亮 (QQ截图式) ──
        // 仅在未选中且未拖拽时显示; 悬停窗口坐标 → HoverRect; 标题 → 图集槽 26
        if (!_selComplete && !_down && _hoverRegion >= 0 && _hoverRegion < _regions.Count)
        {
            var hr = _regions[_hoverRegion];
            cb.HoverRect = new System.Numerics.Vector4(
                hr.X - _winX, hr.Y - _winY, hr.Width, hr.Height);
            EnsureHoverTitle(hr.Title);
            // 标题 tooltip 位置: 窗口左上角上方
            int tipW = _hoverTipW, tipH = _hoverTipH;
            int tix = Math.Clamp(hr.X - _winX, 4, w - tipW - 4);
            int tiy = hr.Y - _winY - tipH - 4;
            if (tiy < 0) tiy = hr.Y - _winY + 4;
            SetV4(cb.TextPos, 27, new System.Numerics.Vector4(tix, tiy, tipW, tipH));
        }
        else
        {
            cb.HoverRect = System.Numerics.Vector4.Zero;
        }

        // ── 选区尺寸徽标（拖拽中/完成后, 选区上方居中; SDR 兜底层 SizeLabel 同形）──
        int selL = Math.Min(_sx1, _sx2), selR = Math.Max(_sx1, _sx2);
        int selT = Math.Min(_sy1, _sy2), selB = Math.Max(_sy1, _sy2);
        if (!_annoMode && (_down || _selComplete) && selR - selL >= 1 && selB - selT >= 1)
        {
            EnsureSizeBadge(ToolbarContract.SizeBadgeText(selR - selL, selB - selT));
            if (_badgeSrv is not null)
            {
                int bx = Math.Clamp(selL + (selR - selL) / 2 - _badgeW / 2, 4, Math.Max(4, w - _badgeW - 4));
                int by = selT - _badgeH - 6;
                if (by < 4) by = Math.Min(selT + 6, h - _badgeH - 4);
                cb.SizeBadgeSub = new System.Numerics.Vector4(0, 0, _badgeW / (float)BadgeTexW, _badgeH / (float)BadgeTexH);
                cb.SizeBadgePos = new System.Numerics.Vector4(bx, by, _badgeW, _badgeH);
            }
        }

        // ── 静置操作提示（未框选、未拖拽时居中; 图集槽 35）──
        if (!_selComplete && !_down)
        {
            int ix = Math.Clamp((w - _idleHintW) / 2, 4, Math.Max(4, w - _idleHintW - 4));
            int iy = Math.Max(4, (h - _idleHintH) / 2);
            SetV4(cb.TextPos, 36, new System.Numerics.Vector4(ix, iy, _idleHintW, _idleHintH));
        }

        // 图集子矩形（56 槽）
        for (int i = 0; i < 56 && i < _atlasSubs.Length; i++)
            SetV4(cb.TextSubs, i, _atlasSubs[i]);

        // ── 输入中的文字预览（EDIT 子窗口不可见 ⇒ 用纹理画在同一位置）──
        UploadEditPreview();
        if (_editHwnd != 0 && _editSrv is not null && _editPrevW > 0 && _editPrevH > 0)
        {
            cb.EditSub = new System.Numerics.Vector4(0, 0, _editPrevW / (float)EditTexW, _editPrevH / (float)EditTexH);
            cb.EditPos = new System.Numerics.Vector4(_editFrameX, _editFrameY, _editPrevW, _editPrevH);
        }

        // ── 标注层数据 ──
        BuildAnnoCb(ref cb);

        // 上传常量缓冲 (2026-08-11: 改用指针直接写入, 完全掌控布局 — StructureToPtr 的 ByValArray 内联不可靠)
        var mapped = _ctx.Map(_uiCb!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        if (mapped.DataPointer != 0)
        {
            try
            {
            unsafe
            {
                var p = (byte*)mapped.DataPointer;
                *(System.Numerics.Vector4*)(p + 0) = cb.OverlayColor;
                *(System.Numerics.Vector4*)(p + 16) = cb.BorderColor;
                *(System.Numerics.Vector4*)(p + 32) = cb.SelRect;
                *(System.Numerics.Vector4*)(p + 48) = cb.ToolbarRect;
                *(System.Numerics.Vector4*)(p + 64) = cb.ButtonLayout;
                *(System.Numerics.Vector4*)(p + 80) = cb.PadHandle;
                *(System.Numerics.Vector4*)(p + 96) = cb.MousePos;
                *(System.Numerics.Vector4*)(p + 112) = cb.Flags;
                *(System.Numerics.Vector4*)(p + 128) = cb.FrameSize;
                fixed (float* ts = cb.TextSubs)
                    Buffer.MemoryCopy(ts, p + 144, 896, 896);
                fixed (float* tp = cb.TextPos)
                    Buffer.MemoryCopy(tp, p + 1040, 896, 896);
                *(System.Numerics.Vector4*)(p + 1936) = cb.AnnoState;
                *(System.Numerics.Vector4*)(p + 1952) = cb.AnnoColor;
                *(System.Numerics.Vector4*)(p + 1968) = cb.AnnoStroke;
                *(System.Numerics.Vector4*)(p + 1984) = cb.HoverRect;
                *(System.Numerics.Vector4*)(p + 2000) = cb.SizeBadgeSub;
                *(System.Numerics.Vector4*)(p + 2016) = cb.SizeBadgePos;
                fixed (float* al = cb.AnnoLayers)
                    Buffer.MemoryCopy(al, p + 2032, 4096, 4096);
                *(System.Numerics.Vector4*)(p + 6128) = cb.EditSub;
                *(System.Numerics.Vector4*)(p + 6144) = cb.EditPos;
            }
            }
            finally { _ctx.Unmap(_uiCb!, 0); } // 异常路径也必须 Unmap，否则常量缓冲永久无法再写入
        }
    }

    // ═══════════════════════════════════════
    //  标注层 → 常量缓冲打包
    // ═══════════════════════════════════════

    private static readonly System.Numerics.Vector4 s_invalidSeg = new(-1e6f, -1e6f, -1e6f, -1e6f);

    private void BuildAnnoCb(ref UiCbData cb)
    {
        cb.AnnoState = new System.Numerics.Vector4(_annoMode ? 1 : 0, AnnoToolIndex(), _annoDown ? 1 : 0, 0);
        var (cr, cg, cbb, ca) = ParseColorToLinear("#FF0000"); // 标注统一红色 (与 SelectionOverlay 一致)
        cb.AnnoColor = new System.Numerics.Vector4(cr, cg, cbb, ca);
        cb.AnnoStroke = new System.Numerics.Vector4(2f, 1f, 0, 0);

        int slot = 0;
        // ═══ 2026-08-16 P1-3: 快照遍历 ═══
        foreach (var layer in _annoManager.GetLayersSnapshot().Where(l => l.IsVisible))
        {
            if (slot >= 15) break; // 层 0-14 提交层, 15 保留预览
            // 正在再编辑的文字层由输入预览接管：两处都画会看到重影
            if (_editHwnd != 0 && ReferenceEquals(layer, _editingLayer)) continue;
            WriteAnnoLayer(ref cb, slot, layer);
            slot++;
        }
        for (int i = slot; i < 15; i++) ClearAnnoLayer(ref cb, i);

        // 预览层（拖拽中实时形状）
        if (_annoDown && _annoMode)
            WriteAnnoPreview(ref cb);
        else
            ClearAnnoLayer(ref cb, 15);
    }

    /// <summary>当前绘图工具的槽位（0..5 = 矩形/椭圆/箭头/画笔/文字/马赛克），其余 -1。
    /// shader 用它把"选中的那枚"高亮出来 —— 没有这层反馈，用户点完工具看不出自己选了谁。</summary>
    private int AnnoToolIndex() => _annoTool switch
    {
        "Rect" => 0, "Ellipse" => 1, "Arrow" => 2, "Pen" => 3, "Text" => 4, "Mosaic" => 5,
        _ => -1
    };

    private void WriteAnnoLayer(ref UiCbData cb, int idx, AnnotationLayer layer)
    {
        int o = idx * 16;
        float type = layer.Type switch
        {
            ShapeType.Rectangle => 1, ShapeType.Ellipse => 2, ShapeType.Arrow => 3,
            ShapeType.Freehand => 4, ShapeType.Text => 5, ShapeType.Mosaic => 6,
            ShapeType.MosaicBrush => 7,
            _ => 1
        };
        // 线色/线宽按图层自己的样式（第二行工具选项条选的颜色），不再是写死的红
        BrushStyle? style = layer switch
        {
            RectangleLayer rl2 => rl2.Style,
            EllipseLayer el2 => el2.Style,
            ArrowLayer al2 => al2.Style,
            FreehandLayer fl2 => fl2.Style,
            _ => null,
        };
        var b = layer.GetBounds();
        SetV4(cb.AnnoLayers, o + 0, new System.Numerics.Vector4(
            type, layer.Opacity, Math.Max(1f, style?.StrokeWidth ?? 2f), 0));
        // ⚠ 箭头的 V1 必须写【起止点】，不能写外接框：shader 的箭头分支把 a1.xy→a1.zw 当主线，
        // 喂外接框（左上→右下）会让所有箭头在预览里恒指向右下 —— 2026-10-28 用户实测"箭头无法选择方向"。
        // 提交层与拖拽预览（WriteAnnoPreview）是同一个坑，两处一起改。
        SetV4(cb.AnnoLayers, o + 1, layer is ArrowLayer a0
            ? new System.Numerics.Vector4(a0.StartX, a0.StartY, a0.EndX, a0.EndY)
            : new System.Numerics.Vector4(b.Left, b.Top, b.Right, b.Bottom));
        SetV4(cb.AnnoLayers, o + 2, style is { } st
            ? new System.Numerics.Vector4(
                SrgbToLinear(st.StrokeColor.R), SrgbToLinear(st.StrokeColor.G),
                SrgbToLinear(st.StrokeColor.B), Math.Clamp(st.StrokeColor.A, 0f, 1f))
            : new System.Numerics.Vector4(1f, 0f, 0f, 1f));

        float param = 0, slot = 0;
        float extraZ = 0;
        switch (layer)
        {
            case ArrowLayer al: param = al.ArrowHeadSize; break;
            case MosaicLayer m: param = m.BlockSize; break;
            case MosaicBrushLayer mb: param = mb.BlockSize; extraZ = mb.BrushWidth; break;
            case TextLayer tl:
                param = tl.FontSize; slot = TextSlotOf(tl);
                // 文字颜色按图层（CPU 合成用的也是它）；形状仍是统一红色。
                // sRGB → 线性：着色器在 scRGB 线性基上做 lerp，CPU 侧是 BGRA8 字节。
                SetV4(cb.AnnoLayers, o + 2, new System.Numerics.Vector4(
                    SrgbToLinear(tl.TextColor.R), SrgbToLinear(tl.TextColor.G),
                    SrgbToLinear(tl.TextColor.B), Math.Clamp(tl.TextColor.A, 0f, 1f)));
                // V4 = 文字背景（含 Padding 的外框）；a3.z = Padding（内框 = 外框减它）
                SetV4(cb.AnnoLayers, o + 4, new System.Numerics.Vector4(
                    SrgbToLinear(tl.BackgroundColor.R), SrgbToLinear(tl.BackgroundColor.G),
                    SrgbToLinear(tl.BackgroundColor.B), Math.Clamp(tl.BackgroundColor.A, 0f, 1f)));
                break;
        }
        SetV4(cb.AnnoLayers, o + 3, new System.Numerics.Vector4(param, slot,
            layer is TextLayer t2 ? Math.Max(2f, t2.Padding) : extraZ, 0));

        if (layer is FreehandLayer fl)
        {
            var pts = SimplifyPolyline(fl.Points, 13);
            for (int k = 0; k < 12; k++)
                SetV4(cb.AnnoLayers, o + 4 + k, k + 1 < pts.Count
                    ? new System.Numerics.Vector4(pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y)
                    : s_invalidSeg);
        }
        else if (layer is MosaicBrushLayer mbl)
        {
            var pts = SimplifyPolyline(mbl.Points, 13);
            for (int k = 0; k < 12; k++)
                SetV4(cb.AnnoLayers, o + 4 + k, k + 1 < pts.Count
                    ? new System.Numerics.Vector4(pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y)
                    : s_invalidSeg);
        }
    }

    private void ClearAnnoLayer(ref UiCbData cb, int idx)
    {
        int o = idx * 16;
        SetV4(cb.AnnoLayers, o + 0, System.Numerics.Vector4.Zero); // type=0 → 空槽
    }

    private void WriteAnnoPreview(ref UiCbData cb)
    {
        int o = 15 * 16;
        var start = _annoDragStart;
        var end = ScreenToAnno(_mx, _my);
        bool brushMosaic = _annoTool == "Mosaic" && _mosaicMode == "Brush";
        float type = brushMosaic ? 7 : _annoTool switch
        {
            "Rect" => 1, "Ellipse" => 2, "Arrow" => 3, "Pen" => 4, "Text" => 5, "Mosaic" => 6,
            _ => 1
        };
        float x1 = Math.Min(start.X, end.X), y1 = Math.Min(start.Y, end.Y);
        float x2 = Math.Max(start.X, end.X), y2 = Math.Max(start.Y, end.Y);
        SetV4(cb.AnnoLayers, o + 0, new System.Numerics.Vector4(type, 1f, Math.Max(1f, _annoStrokeW), 0));
        // 箭头（type 3）的主线是 a1.xy→a1.zw：这里必须给拖拽真实起止点，给外接框就永远指向右下
        SetV4(cb.AnnoLayers, o + 1, type == 3
            ? new System.Numerics.Vector4(start.X, start.Y, end.X, end.Y)
            : new System.Numerics.Vector4(x1, y1, x2, y2));
        // 预览也用当前选的颜色（以前写死红 ⇒ 换色只在提交后才生效，画的时候看不出来）
        SetV4(cb.AnnoLayers, o + 2, new System.Numerics.Vector4(
            SrgbToLinear(_annoColor.R), SrgbToLinear(_annoColor.G), SrgbToLinear(_annoColor.B),
            Math.Clamp(_annoColor.A, 0f, 1f)));
        // 涂抹预览的笔宽与提交时同一个来源（MosaicBrushPx），不再各算一份
        float brushW = MosaicBrushPx();
        SetV4(cb.AnnoLayers, o + 3, new System.Numerics.Vector4(
            type == 3 ? 12f : (type == 6 || type == 7 ? Math.Max(4f, 10f * (float)_uiScale) : 0f),
            0, type == 7 ? brushW : 0, 0));
        if (type == 4 || type == 7)
        {
            List<System.Numerics.Vector2> pts;
            lock (_penPointsLock) { pts = SimplifyPolyline(_penPoints, 13); }
            for (int k = 0; k < 12; k++)
                SetV4(cb.AnnoLayers, o + 4 + k, k + 1 < pts.Count
                    ? new System.Numerics.Vector4(pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y)
                    : s_invalidSeg);
        }
    }

    /// <summary>帧坐标 → 选区图像坐标（标注层坐标系）。</summary>
    private System.Numerics.Vector2 ScreenToAnno(int fx, int fy)
    {
        int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
        return new System.Numerics.Vector2(fx - x1, fy - y1);
    }

    /// <summary>文字层在图集文字槽中的序号（与 EnsureTextAtlas 的筛法/顺序严格一致）。
    /// <para>两处都用 <c>l is TextLayer && l.IsVisible</c>：此前图集按"可见"筛、这里按"全部"数，
    /// 只要有一层被隐藏，其后的文字就会各自错位采样到别人的位图。</para></summary>
    private float TextSlotOf(TextLayer tl)
    {
        int slot = 0;
        // ═══ 2026-08-16 P1-3: 快照遍历 ═══
        foreach (var layer in _annoManager.GetLayersSnapshot())
        {
            if (layer is not TextLayer t || !t.IsVisible) continue;
            if (ReferenceEquals(t, tl)) return slot;
            slot++;
        }
        return 0;
    }

    /// <summary>折线简化（贪心保留最远点, 最多 maxPts 点; 预览用, 最终输出保留完整轨迹）。</summary>
    private static List<System.Numerics.Vector2> SimplifyPolyline(List<System.Numerics.Vector2> pts, int maxPts)
    {
        if (pts.Count <= maxPts) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        int kept = 2;
        while (kept < maxPts)
        {
            float bestD = 1e-4f; int bestI = -1;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                if (keep[i]) continue;
                int p = i - 1; while (p >= 0 && !keep[p]) p--;
                int n = i + 1; while (n < pts.Count && !keep[n]) n++;
                if (p < 0 || n >= pts.Count) continue;
                float d = DistToSegment(pts[i], pts[p], pts[n]);
                if (d > bestD) { bestD = d; bestI = i; }
            }
            if (bestI < 0) break;
            keep[bestI] = true; kept++;
        }
        var res = new List<System.Numerics.Vector2>(kept);
        for (int i = 0; i < pts.Count; i++) if (keep[i]) res.Add(pts[i]);
        return res;
    }

    private static float DistToSegment(System.Numerics.Vector2 p, System.Numerics.Vector2 a, System.Numerics.Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-6f ? Math.Clamp(System.Numerics.Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return (p - (a + ab * t)).Length();
    }

    private unsafe void UploadFrame(float[] pixels, int w, int h)
    {
        // ⚠ 长度校验：下方按 w*h*4 索引 pixels。尺寸与数组不匹配时 fixed 会固定住
        // 长度不符的数组并越界读写托管堆。用 long 计算避免 int 溢出导致校验失效。
        if (pixels is null || w <= 0 || h <= 0 || (long)w * h * 4 > pixels.Length)
        {
            LogService.Error("HdrCapture",
                $"UploadFrame 参数无效: len={pixels?.Length ?? -1} 需要={w}x{h}x4", LogCategory.Capture);
            return;
        }

        if (_desktopTex is null || _desktopTex.Description.Width != w || _desktopTex.Description.Height != h)
        {
            _desktopSrv?.Dispose(); _desktopTex?.Dispose();
            _desktopTex = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource });
            _desktopSrv = _device.CreateShaderResourceView(_desktopTex);
        }
        // 池化 Staging 纹理：尺寸匹配时复用，避免每帧 32MB 分配（渲染线程独占访问，无锁安全）
        if (_pooledStaging is null || _pooledStW != w || _pooledStH != h)
        {
            _pooledStaging?.Dispose();
            _pooledStaging = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Write });
            _pooledStW = w; _pooledStH = h;
        }
        var st = _pooledStaging;
        var m = _ctx.Map(st, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        if (m.DataPointer == 0)
        {
            LogService.Error("HdrCapture", $"UploadFrame Map 失败 (w={w} h={h})");
            return;
        }
        long dbBase = (long)m.DataPointer; int dp = (int)m.RowPitch;

        // 中心行亮度打点（排查"源像素全黑"用）。
        // ⚠ 原实现注释写"一次性"但没有实际开关：CPU 回退路径下 UploadFrame 每帧执行，
        // 于是每帧多一次 O(w) 扫描 + 一次同步磁盘写（LogService.Info 内部 lock + AppendAllText）。
        // 改为真正的一次性。
        if (Volatile.Read(ref _diagSampleDone) == 0)
        {
            Volatile.Write(ref _diagSampleDone, 1);
            int midR = h / 2, midOff = midR * w * 4;
            float mn = float.MaxValue, mx = float.MinValue, sum = 0;
            for (int i = midOff; i < midOff + w * 4; i += 4)
            { float v = pixels[i]; if (v < mn) mn = v; if (v > mx) mx = v; sum += v; }
            int n = w;
            LogService.Info("HdrCapture", $"UploadFrame 源中心行 R: min={mn:F4} max={mx:F4} avg={sum / n:F4} (行{midR})");
        }

        // 按行并行转换（行间无依赖；long 基址避免在 lambda 中捕获 fixed/指针变量）
        // ⚠ Unmap 必须在 finally 中：Map 与 Unmap 之间若抛异常（越界/DeviceRemoved/GC），
        // 不 Unmap 会导致该 subresource 后续 Map 永久失败 → 渲染线程静默停止出图。
        try
        {
            Parallel.For(0, h, r =>
            {
                fixed (float* sp = pixels)
                    TrueToneCap.Core.PixelOps.ConvertFloatToHalfRow(
                        sp + r * w * 4, (ushort*)(dbBase + r * dp), w * 4);
            });
        }
        finally
        {
            _ctx.Unmap(st, 0);
        }
        _ctx.CopyResource(_desktopTex, st);
        _hasFrame = true;
    }

    public void Render() => RequestRedraw(); // 兼容旧调用（MainWindow 启动首帧）

    /// <summary>从原始帧重建合成缓冲，叠加 UI 元素（每帧从原始帧拷贝，不累积）。
    /// 合成缓冲懒创建：分配发生在渲染线程，UI 线程不承担 132MB LOH 大分配。
    /// ⚠ 合成缓冲按"窗口"尺寸分配：本函数内所有 UI 元素（遮罩/选区/工具栏/悬停高亮）
    /// 均在窗口坐标系中定位，且 RenderCore 以 (_winW,_winH) 上传该缓冲。
    /// 之前按 src.Length（帧尺寸）分配，而帧尺寸可能≠窗口尺寸（多显示器拼接/DPI）
    /// → Array.Copy 源越界（被上层空 catch 吞掉，表现为 CPU 回退路径黑屏）或行错位。</summary>
    private void CompositeUI(FrameData frame)
    {
        int w = _winW, h = _winH;
        if (w <= 0 || h <= 0) return;

        var src = frame.Pixels;
        // 懒分配合成缓冲（按窗口尺寸，非帧尺寸）
        int need = w * h * 4;
        if (_compPixels is null || _compPixels.Length != need)
            _compPixels = new float[need];
        var px = _compPixels;

        // 1. 帧 → 合成缓冲 基线拷贝
        // 帧尺寸可能与窗口尺寸不等，故按各自行宽逐行拷贝重叠区域。
        // ⚠ 未覆盖区域必须显式清零：_compPixels 在同一帧序列内复用，
        // 若帧小于窗口（多显示器 + 单屏 HDR 时会出现 frame.W < _winW），
        // 右侧/下方残留区会保留上一帧绘制的 UI（选区边框/手柄/工具栏），并逐帧累积。
        int copyW = Math.Min(frame.W, w), copyH = Math.Min(frame.H, h);
        if (copyW > 0 && copyH > 0)
        {
            Parallel.For(0, copyH, row =>
            {
                Array.Copy(src, row * frame.W * 4, px, row * w * 4, copyW * 4);
                // 行内右侧未覆盖部分清零
                if (copyW < w) Array.Clear(px, row * w * 4 + copyW * 4, (w - copyW) * 4);
            });
            // 下方未覆盖的整行清零
            if (copyH < h)
                Parallel.For(copyH, h, row => Array.Clear(px, row * w * 4, w * 4));
        }
        else
        {
            // 帧完全不可用（尺寸为 0 或帧尚未就绪）：整块清零，避免显示陈旧内容
            Array.Clear(px, 0, px.Length);
        }

        // 2. 遮罩（2026-08-11 区域化: 拆成 上/下/左/右 4 个矩形直接遍历 — 无逐列 skip 分支判断）
        // 2026-08-11 区域化: 拆成 上/下/左/右 4 个矩形直接遍历 — 无逐列 skip 分支判断
        if (!_selComplete || _down)
        {
            // 解析用户配置的覆盖颜色 (#AARRGGBB → scRGB 线性)
            var (ovR, ovG, ovB, ovA) = ParseColorToLinear(AppServices.Settings.Current.OverlayColor);
            float invA = 1 - ovA;
            // 预乘混合系数（避免循环内重复乘）
            float mr = ovR * ovA, mg = ovG * ovA, mb = ovB * ovA;

            // 遮罩区（屏幕坐标，未选择时全屏）
            int x1 = _selComplete ? Math.Max(0, Math.Min(_sx1, _sx2)) : -1;
            int y1 = _selComplete ? Math.Max(0, Math.Min(_sy1, _sy2)) : -1;
            int x2 = _selComplete ? Math.Min(w - 1, Math.Max(_sx1, _sx2)) : -1;
            int y2 = _selComplete ? Math.Min(h - 1, Math.Max(_sy1, _sy2)) : -1;
            int X1 = Math.Max(0, x1), Y1 = Math.Max(0, y1);
            int X2 = Math.Min(w - 1, x2), Y2 = Math.Min(h - 1, y2);

            // 行处理：拷贝整行 + 对指定列区间混合（无分支）
            // 2026-08-11: AVX2 FMA 加速 — 8 float/轮 = 2 像素 (RGBA×2), A 通道系数 1.0 保持不动
            // ⚠️ 步长必须 c += 2 (2像素/向量)! 写成 c += 8 会漏掉 6 像素 → 细格!
            void BlendRow(int row, int c0, int c1)
            {
                // 基线拷贝已在上方整体完成，此处只做遮罩混合
                int rowOff = row * w * 4;
                if (TrueToneCap.Core.PixelOps.HasFma && c1 - c0 >= 4) // ≥2 像素才用 SIMD
                {
                    var invA8 = Vector256.Create(invA, invA, invA, 1f, invA, invA, invA, 1f);
                    var mix8 = Vector256.Create(mr, mg, mb, 0f, mr, mg, mb, 0f);

                    // 对齐起点: c0 奇数时先标量处理 1 像素, 使向量从偶数像素开始
                    int c = c0;
                    if ((c & 1) != 0)
                    {
                        int i = rowOff + c * 4;
                        px[i] = px[i] * invA + mr;
                        px[i + 1] = px[i + 1] * invA + mg;
                        px[i + 2] = px[i + 2] * invA + mb;
                        c++;
                    }
                    int last = c1 - 1; // 向量覆盖 c 和 c+1, 保证 c+1 <= c1
                    for (; c <= last; c += 2)
                    {
                        int i = rowOff + c * 4;
                        var p = Vector256.LoadUnsafe(ref px[i]);
                        p = Fma.MultiplyAdd(p, invA8, mix8); // vfmaddps ymm
                        p.StoreUnsafe(ref px[i]);
                    }
                    for (; c <= c1; c++)
                    {
                        int i = rowOff + c * 4;
                        px[i] = px[i] * invA + mr;
                        px[i + 1] = px[i + 1] * invA + mg;
                        px[i + 2] = px[i + 2] * invA + mb;
                    }
                }
                else
                {
                    for (int col = c0; col <= c1; col++)
                    {
                        int i = rowOff + col * 4;
                        px[i] = px[i] * invA + mr;
                        px[i + 1] = px[i + 1] * invA + mg;
                        px[i + 2] = px[i + 2] * invA + mb;
                    }
                }
            }

            // 上带: [0, Y1) 整行混合
            Parallel.For(0, Math.Max(0, Y1), row => BlendRow(row, 0, w - 1));
            // 下带: (Y2, h) 整行混合
            Parallel.For(Y2 + 1, h, row => BlendRow(row, 0, w - 1));
            // 左带: [Y1, Y2] 行内 [0, X1)
            Parallel.For(Math.Max(0, Y1), Math.Min(Y2 + 1, h), row => BlendRow(row, 0, X1 - 1));
            // 右带: [Y1, Y2] 行内 (X2, w)
            Parallel.For(Math.Max(0, Y1), Math.Min(Y2 + 1, h), row => BlendRow(row, X2 + 1, w - 1));
        }
        // else：无遮罩时无需额外处理，基线拷贝已包含完整帧内容

        // 2.5 窗口悬停高亮（QQ截图式：未拖拽时高亮悬停窗口）
        if (!_selComplete && !_down && _hoverRegion >= 0 && _hoverRegion < _regions.Count)
        {
            var r = _regions[_hoverRegion];
            int rx = r.X - _winX, ry = r.Y - _winY;
            // 高亮边框（亮蓝色 3px）
            for (int t = 0; t < 3; t++)
            {
                for (int c = rx; c < rx + r.Width && c < w; c++)
                {
                    if (c >= 0) { SetPixelLinear(px, w, c, ry + t, 0.3f, 0.6f, 1.0f); SetPixelLinear(px, w, c, ry + r.Height - 1 - t, 0.3f, 0.6f, 1.0f); }
                }
                for (int row = ry; row < ry + r.Height && row < h; row++)
                {
                    if (row >= 0) { SetPixelLinear(px, w, rx + t, row, 0.3f, 0.6f, 1.0f); SetPixelLinear(px, w, rx + r.Width - 1 - t, row, 0.3f, 0.6f, 1.0f); }
                }
            }
        }

        // 3. 选区边框 + 四角手柄
        if ((_sx1 != _sx2 || _sy1 != _sy2) && (_down || _selComplete))
        {
            var (bdR, bdG, bdB, _) = ParseColorToLinear(AppServices.Settings.Current.BorderColor);
            int x1 = Math.Max(0, Math.Min(_sx1, _sx2)), y1 = Math.Max(0, Math.Min(_sy1, _sy2));
            int x2 = Math.Min(w - 1, Math.Max(_sx1, _sx2)), y2 = Math.Min(h - 1, Math.Max(_sy1, _sy2));
            for (int t = 0; t < 2; t++)
            {
                for (int c = x1; c <= x2; c++) { SetPixelLinear(px, w, c, y1 + t, bdR, bdG, bdB); SetPixelLinear(px, w, c, y2 - t, bdR, bdG, bdB); }
                for (int r = y1; r <= y2; r++) { SetPixelLinear(px, w, x1 + t, r, bdR, bdG, bdB); SetPixelLinear(px, w, x2 - t, r, bdR, bdG, bdB); }
            }
            // 四角手柄（白色方块，随 DPI 缩放）
            if (_selComplete)
            {
                int hs = _handleSize;
                FillRectLinear(px, w, h, x1 - hs / 2, y1 - hs / 2, x1 + hs / 2, y1 + hs / 2, 1.0f, 1.0f, 1.0f); // TL
                FillRectLinear(px, w, h, x2 - hs / 2, y1 - hs / 2, x2 + hs / 2, y1 + hs / 2, 1.0f, 1.0f, 1.0f); // TR
                FillRectLinear(px, w, h, x1 - hs / 2, y2 - hs / 2, x1 + hs / 2, y2 + hs / 2, 1.0f, 1.0f, 1.0f); // BL
                FillRectLinear(px, w, h, x2 - hs / 2, y2 - hs / 2, x2 + hs / 2, y2 + hs / 2, 1.0f, 1.0f, 1.0f); // BR
            }
        }

        // 4. 工具栏 — QQ 风格工具栏（每个按钮独立显示，图标+文字并排，悬停提示功能）
        if (_selComplete)
        {
            bool isDark = App.ResolveEffectiveTheme(App.CurrentTheme) is AppThemeMode.Dark or AppThemeMode.OLED;
            // 文案一律取自契约（门禁禁止本文件再出现按钮文案字面量）
            var items = ToolbarContract.Main
                .Select(b => (icon: b.Glyph, text: b.Text, hint: b.Hint))
                .ToArray();

            // 工具栏 — QQ 风格工具栏（每个按钮独立显示，图标+文字并排，悬停提示功能）
            // 落位与尺寸一律取 ToolbarFrame（与 GPU 绘制、Down()/HitAnnoToolbar 命中、第二行几何同源）
            int n = items.Length;
            var lay = ToolbarFrame(n, w, h);
            int btnW = lay.BtnW, btnH = lay.BtnH, gap = lay.Gap;
            int padX = lay.PadX, padY = lay.PadY;
            int totalW = lay.TotalW, totalH = lay.TotalH;
            int tx = lay.Tx, ty = lay.Ty;

            // 绘制毛玻璃背景（圆角）
            float bgA = 0.88f;
            float bgR = isDark ? 0.07f : 0.93f;
            float bgG = isDark ? 0.07f : 0.93f;
            float bgB = isDark ? 0.07f : 0.93f;
            float txtR = isDark ? 1.0f : 0.05f;
            float txtG = isDark ? 1.0f : 0.05f;
            float txtB = isDark ? 1.0f : 0.05f;
            DrawRoundedRect(px, w, h, tx, ty, tx + totalW, ty + totalH, 6, bgR, bgG, bgB, bgA);

            int hoverIdx = -1;
            for (int i = 0; i < n; i++)
            {
                int bx = tx + padX + i * (btnW + gap);
                int by = ty + padY;
                bool hv = _mx >= bx && _mx <= bx + btnW && _my >= by && _my <= by + btnH;
                if (hv) hoverIdx = i;

                // 按钮背景（悬停时浅蓝高亮）
                if (hv)
                {
                    float hlR = isDark ? 0.15f : 0.85f;
                    float hlG = isDark ? 0.15f : 0.85f;
                    float hlB = isDark ? 0.15f : 0.85f;
                    DrawRoundedRect(px, w, h, bx, by, bx + btnW, by + btnH, 4, hlR, hlG, hlB, 1.0f);
                }

                // 图标 + 文字居中
                DrawTextGdi(px, w, h, items[i].icon + " " + items[i].text, bx, by, btnW, btnH, txtR, txtG, txtB);
            }

            // 悬停提示（在工具栏下方或上方显示）
            if (hoverIdx >= 0)
            {
                string hintText = items[hoverIdx].hint;
                float hintA = 0.92f;
                float hintR = isDark ? 0.02f : 0.15f;
                float hintG = isDark ? 0.02f : 0.15f;
                float hintB = isDark ? 0.02f : 0.15f;
                int hintW = ToolbarContract.MainHintWidthPx(ToolbarFontSize * (float)_uiScale);
                int hintH = Math.Max(24, (int)Math.Round(30 * _uiScale));
                int hx = Math.Clamp(tx + totalW / 2 - hintW / 2, 4, w - hintW - 4);
                int hy = ty + totalH + 4;
                if (hy + hintH + 4 > h) hy = ty - hintH - 4;
                DrawRoundedRect(px, w, h, hx, hy, hx + hintW, hy + hintH, 4, hintR, hintG, hintB, hintA);
                DrawTextGdi(px, w, h, hintText, hx, hy, hintW, hintH, txtR, txtG, txtB);
            }
        }
    }

    /// <summary>工具栏布局（物理像素，随 DPI 缩放）。绘制与命中检测共用，保证一致。</summary>
    private (int BtnW, int BtnH, int Gap, int PadX, int PadY, int TotalW, int TotalH) GetToolbarLayout(int n)
    {
        // 整数百分比运算（缩放后取整），绘制/命中两处结果严格一致
        int s = Math.Max(100, (int)Math.Round(_uiScale * 100));
        int btnW = Math.Max(60, 90 * s / 100);
        int btnH = Math.Max(24, 32 * s / 100);
        int gap = Math.Max(2, 4 * s / 100);
        int padX = Math.Max(3, 6 * s / 100);
        int padY = Math.Max(3, 5 * s / 100);
        int totalW = n * btnW + (n - 1) * gap + padX * 2;
        int totalH = btnH + padY * 2;
        return (btnW, btnH, gap, padX, padY, totalW, totalH);
    }

    /// <summary>
    /// 工具条在帧内的整块落位（左上角 + 布局）—— 绘制、两处命中、第二行几何【四处同源】。
    /// <para>以前每处各抄一遍夹取算术：改一处就分叉，而分叉的表现是"画在那儿的按钮点不到 /
    /// 点到的按钮画在别处"。2026-10-28 真机还暴露出另一层代价：命中侧的算术被人当依据去改状态时，
    /// 谁也算不清是哪一处在错。</para>
    /// </summary>
    private (int Tx, int Ty, int BtnW, int BtnH, int Gap, int PadX, int PadY, int TotalW, int TotalH)
        ToolbarFrame(int n, int frameW, int frameH)
    {
        var lay = GetToolbarLayout(n);
        int selX1 = Math.Min(_sx1, _sx2), selX2 = Math.Max(_sx1, _sx2);
        int selY1 = Math.Min(_sy1, _sy2), selY2 = Math.Max(_sy1, _sy2);
        int tx = Math.Clamp(selX1 + (selX2 - selX1) / 2 - lay.TotalW / 2, 4, Math.Max(4, frameW - lay.TotalW - 4));
        int ty = selY2 + 10;
        if (ty + lay.TotalH + 10 > frameH) ty = selY1 - lay.TotalH - 12;
        if (ty < 0) ty = 4;
        return (tx, ty, lay.BtnW, lay.BtnH, lay.Gap, lay.PadX, lay.PadY, lay.TotalW, lay.TotalH);
    }

    /// <summary>探针读数口：工具条落位（用帧尺寸，与绘制/命中同一处算术）。</summary>
    internal (int Tx, int Ty, int BtnW, int BtnH, int Gap, int PadX, int PadY, int TotalW, int TotalH)
        ProbeToolbarFrame(int n) => ToolbarFrame(n, _winW, _winH);

    /// <summary>探针读数口：当前选区（帧内、已归一化）。工具条点击一类"不该动选区"的判据要读它。</summary>
    internal (int X, int Y, int W, int H) ProbeSelection()
    {
        int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
        return (x1, y1, Math.Abs(_sx2 - _sx1), Math.Abs(_sy2 - _sy1));
    }

    /// <summary>工具栏文字位图缓存（文字内容固定，避免每次 Win2D 渲染 + GPU 回读）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string text, int w, int h), byte[]> s_textCache = new();

    /// <summary>
    /// 启动预热 (2026-08-11 Phase 1): 后台线程预热 Win2D 设备 + 常见 DPI 的工具栏文字位图。
    /// 消除首次 hover 工具栏时 CanvasDevice 创建 + 位图渲染的延迟。
    /// 只预热常见 DPI (100%/125%/150%/200%) 组合，限制缓存量防内存膨胀。
    /// </summary>
    public static void PreloadUiCache()
    {
        try
        {
            // 1. Win2D 共享设备预热（首次调用慢，后续零成本）
            var device = AnnotationRasterizer.SharedCanvasDevice;   // 共享设备：不 Dispose

            // 2. 工具栏按钮文字（内容取自 ToolbarContract —— 与图集/命中检测同一份声明）
            var items = ToolbarContract.Main
                .Select(b => (icon: b.Glyph, text: b.Text, hint: b.Hint))
                .Concat(ToolbarContract.Anno.Select(t => (icon: t.Glyph, text: t.Text, hint: "")))
                .ToArray();
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                int s = (int)Math.Round(scale * 100);
                int btnW = Math.Max(60, 90 * s / 100);
                int btnH = Math.Max(24, 32 * s / 100);
                float fontSize = ToolbarFontSize * (float)scale;
                int hintW = ToolbarContract.MainHintWidthPx(fontSize);
                int hintH = Math.Max(24, (int)Math.Round(30 * scale));

                foreach (var it in items)
                {
                    // 按钮文字
                    var btnKey = (it.icon + " " + it.text, btnW, btnH);
                    if (!s_textCache.ContainsKey(btnKey))
                    {
                        var bmp = RenderTextBitmap(device, btnKey.Item1, btnW, btnH, fontSize);
                        if (bmp is not null) s_textCache[btnKey] = bmp;
                    }
                    // 悬停提示（标注工具没有逐钮提示，hint 为空串跳过）
                    if (it.hint.Length == 0) continue;
                    var hintKey = (it.hint, hintW, hintH);
                    if (!s_textCache.ContainsKey(hintKey))
                    {
                        var bmp = RenderTextBitmap(device, hintKey.Item1, hintW, hintH, fontSize);
                        if (bmp is not null) s_textCache[hintKey] = bmp;
                    }
                }
                // 标注工具条的静态提示（图集槽 18）
                var annoHintKey = (ToolbarContract.AnnoHint, hintW, hintH);
                if (!s_textCache.ContainsKey(annoHintKey))
                {
                    var bmp = RenderTextBitmap(device, ToolbarContract.AnnoHint, hintW, hintH, fontSize);
                    if (bmp is not null) s_textCache[annoHintKey] = bmp;
                }
            }
            LogService.Info("HdrCapture", $"启动预热完成: {s_textCache.Count} 个文字位图", LogCategory.UI);
        }
        catch (Exception ex)
        {
            LogService.Warn("HdrCapture", $"启动预热失败（非致命）: {ex.Message}", LogCategory.UI);
        }
    }

    /// <summary>用 Win2D 渲染文字位图（供预热与运行时缓存共用）。</summary>
    private static byte[]? RenderTextBitmap(Microsoft.Graphics.Canvas.CanvasDevice device, string text, int tw, int th, float fontSize)
    {
        try
        {
            using var renderTarget = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, tw, th, 96);
            using var ds = renderTarget.CreateDrawingSession();
            ds.Clear(Microsoft.UI.Colors.Transparent);
            var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontFamily = "Microsoft YaHei UI",
                FontSize = fontSize,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            };
            ds.DrawText(text, 0, 0, tw, th, Microsoft.UI.Colors.White, format);
            ds.Flush();
            return renderTarget.GetPixelBytes();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>使用 Win2D 渲染文字到临时缓冲区，用 alpha 通道合成到 float 缓冲区（替代 System.Drawing GDI+）。</summary>
    private unsafe void DrawTextGdi(float[] px, int bufW, int bufH, string text, int x, int y, int tw, int th, float r, float g, float b)
    {
        try
        {
            // 缓存命中直接复用位图（文字内容/尺寸相同即可，颜色在合成时着色）
            if (!s_textCache.TryGetValue((text, tw, th), out var pixels))
            {
                var device = AnnotationRasterizer.SharedCanvasDevice;   // 共享设备：不 Dispose
                pixels = RenderTextBitmap(device, text, tw, th, 18f * (float)_uiScale);
                if (pixels is null) return;
                s_textCache[(text, tw, th)] = pixels;
            }

            int stride = tw * 4;
            for (int row = 0; row < th; row++)
            {
                int dstY = y + row;
                if (dstY < 0 || dstY >= bufH) continue;
                for (int col = 0; col < tw; col++)
                {
                    int dstX = x + col;
                    if (dstX < 0 || dstX >= bufW) continue;
                    int si = row * stride + col * 4;
                    byte alpha = pixels[si + 3]; // A 通道 = 文字覆盖率
                    if (alpha < 15) continue;
                    float a = alpha / 255f;
                    int di = (dstY * bufW + dstX) * 4;
                    if (di + 3 >= px.Length) continue;
                    px[di] = px[di] * (1 - a) + r * a;
                    px[di + 1] = px[di + 1] * (1 - a) + g * a;
                    px[di + 2] = px[di + 2] * (1 - a) + b * a;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static void SetPixelLinear(float[] px, int w, int x, int y, float r, float g, float b)
    {
        int i = (y * w + x) * 4;
        if (i + 3 < px.Length) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 1.0f; }
    }

    /// <summary>解析 #AARRGGBB 颜色为 scRGB 线性值 (sRGB gamma → linear)。</summary>
    private static (float r, float g, float b, float a) ParseColorToLinear(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            byte a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            byte r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
            // sRGB → 线性
            float lr = SrgbToLinear(r / 255f);
            float lg = SrgbToLinear(g / 255f);
            float lb = SrgbToLinear(b / 255f);
            return (lr, lg, lb, a / 255f);
        }
        catch { return (0f, 0.1f, 0.4f, 0.6f); } // 回退默认
    }

    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static void FillRectLinear(float[] px, int w, int h, int x1, int y1, int x2, int y2, float r, float g, float b)
    {
        for (int row = Math.Max(0, y1); row < Math.Min(y2, h); row++)
            for (int col = Math.Max(0, x1); col < Math.Min(x2, w); col++)
            {
                int i = (row * w + col) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 1.0f;
            }
    }

    /// <summary>绘制圆角矩形（8 邻域近似的圆角，scRGB 线性）。</summary>
    private static void DrawRoundedRect(float[] px, int w, int h, int x1, int y1, int x2, int y2, int radius, float r, float g, float b, float alpha)
    {
        // 大区域用并行填充加速（4K 全屏合成时开销显著）
        int rad = Math.Min(radius, Math.Min((x2 - x1) / 2, (y2 - y1) / 2));
        if (rad < 2)
        {
            Parallel.For(Math.Max(0, y1), Math.Min(y2, h), row =>
            {
                int rowOff = row * w * 4;
                for (int col = Math.Max(0, x1); col < Math.Min(x2, w); col++)
                {
                    int i = rowOff + col * 4;
                    if (i + 3 >= px.Length) continue;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 1.0f;
                }
            });
            return;
        }
        int radSq = rad * rad;
        for (int row = Math.Max(0, y1); row < Math.Min(y2, h); row++)
        {
            for (int col = Math.Max(0, x1); col < Math.Min(x2, w); col++)
            {
                // 判断是否在四个圆角区域
                bool inCorner = false;
                // TL
                if (col < x1 + rad && row < y1 + rad)
                    inCorner = (col - (x1 + rad)) * (col - (x1 + rad)) + (row - (y1 + rad)) * (row - (y1 + rad)) > radSq;
                // TR
                else if (col >= x2 - rad && row < y1 + rad)
                    inCorner = (col - (x2 - rad)) * (col - (x2 - rad)) + (row - (y1 + rad)) * (row - (y1 + rad)) > radSq;
                // BL
                else if (col < x1 + rad && row >= y2 - rad)
                    inCorner = (col - (x1 + rad)) * (col - (x1 + rad)) + (row - (y2 - rad)) * (row - (y2 - rad)) > radSq;
                // BR
                else if (col >= x2 - rad && row >= y2 - rad)
                    inCorner = (col - (x2 - rad)) * (col - (x2 - rad)) + (row - (y2 - rad)) * (row - (y2 - rad)) > radSq;

                if (!inCorner)
                {
                    int i = (row * w + col) * 4;
                    if (i + 3 >= px.Length) continue;
                    float a = alpha;
                    px[i] = px[i] * (1 - a) + r * a;
                    px[i + 1] = px[i + 1] * (1 - a) + g * a;
                    px[i + 2] = px[i + 2] * (1 - a) + b * a;
                }
            }
        }
    }

    private static nint StaticWndProc(nint h, uint m, nint w, nint l)
    {
        if (s_windows.TryGetValue(h, out var self))
            return self.WndProc(h, m, w, l);
        return DefWindowProcW(h, m, w, l);
    }

    private nint WndProc(nint h, uint m, nint w, nint l)
    {
        // ⚠ WndProc 由 native 代码回调：托管异常跨越 native 栈帧无法被 CLR 正常展开，
        // 会直接终止进程。此处兜底，保证任何未预料异常都降级为"该消息不处理"。
        try
        {
            return WndProcCore(h, m, w, l);
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"WndProc 处理消息 0x{m:X} 时异常", ex);
            return 0;
        }
    }

    private nint WndProcCore(nint h, uint m, nint w, nint l)
    {
        switch (m)
        {
            // UI 线程绝不渲染：WM_PAINT 仅验证区域 + 通知渲染线程
            case WM_PAINT:
                {
                    // ⚠ rgbReserved 必须预先分配：ByValArray 字段为 null 时，
                    // 封送 ref 结构体存在不确定性（可能抛异常或只写入部分字段）。
                    var ps = new PAINTSTRUCT { rgbReserved = new byte[32] };
                    BeginPaint(h, ref ps);
                    EndPaint(h, ref ps);
                    RequestRedraw();
                    return 0;
                }
            case WM_ERASE: return 1;
            case WM_LD:
                // 2026-08-25 诊断日志已降级：鼠标按下/抬起是高频事件，
                // 原本每次都写 Info 级日志会淹没有效信息并持续增大日志文件。
                LogService.Debug("HdrCapture", $"WM_LBUTTONDOWN ({GX(l)},{GY(l)}) annoMode={_annoMode} selComplete={_selComplete}", LogCategory.Capture);
                Down(GX(l), GY(l)); SetCapture(h); return 0;
            case WM_MM: Move(GX(l), GY(l)); return 0;
            case WM_LU:
                LogService.Debug("HdrCapture", $"WM_LBUTTONUP ({GX(l)},{GY(l)}) moved={_moved} down={_down}", LogCategory.Capture);
                Up(GX(l), GY(l)); ReleaseCapture(); return 0;
            case WM_KEY: Key((int)w); return 0;
            case WM_MOUSEWHEEL: // 下拉列表翻页（高字 = 带符号的滚轮增量）
                DdScroll((short)((w.ToInt64() >> 16) & 0xFFFF));
                return 0;
            case WM_CLOSE: // Alt+F4 / 系统关闭 → 兜底触发 Cancel，保证 MainWindow 防重入锁必然释放
                FireAction(MainToolbarAction.Cancel, 0, 0, 0, 0);
                Close();
                return 0;
            case WM_APP_TIMEOUT: // 3 分钟无操作超时（UI 线程安全关闭）
                FireAction(MainToolbarAction.Cancel, 0, 0, 0, 0);
                Close();
                return 0;
        }
        return DefWindowProcW(h, m, w, l);
    }

    /// <summary>触发 ActionCompleted（幂等：只触发一次）。</summary>
    private void FireAction(MainToolbarAction a, int x, int y, int w, int h)
    {
        if (_actionFired) return;
        _actionFired = true;
        ActionCompleted?.Invoke(a, x, y, w, h);
    }

    private void Down(int x, int y)
    {
        TouchIdle();
        // 上一次滑条拖拽没收到抬起（丢焦点/捕获被抢）→ 新一次按下先收尾，否则 Move 永远走拖拽分支
        if (_sliderDrag) { _sliderDrag = false; ReleaseCapture(); }
        _down = true; _moved = false; _dragHandle = -1; _movingSel = false;
        // 闩描述的是【这一次按下】归谁，所以每次按下都重算：不在这儿清零，拖完滑条留下的闩
        // 会把下一笔"涂一笔"的松手也吃掉（2026-10-28 探针 ⑫ 当场抓到：涂抹图层没了）。
        _toolClickLatch = false;

        if (_annoMode)
        {
            // ── 标注模式：工具栏 / 选区内绘制 / 文字输入 ──
            // ⚠ 吃掉按下时必须立 _toolClickLatch：_down=false 之后 Up() 就不再有"这次按下属于谁"的信息，
            // 会一路落到"重画选区"的收尾，把 (_sx2,_sy2) 写成那次点击的位置 —— 2026-10-28 真机日志：
            // 点标注工具条「完成」后 选区完成 1280x820 → 1133x861，保存出去的图比用户框的少 147 px 宽。
            // 旧写法 `_moved = true` 挡不住：Up() 尾部的"单击"分支判的是 !_moved，动了就直接重画选区。
            var (hit, idx) = HitAnnoToolbar(x, y);
            if (hit) { _down = false; _moved = true; _toolClickLatch = true; OnAnnoToolbarClick(idx); return; }

            // 下拉列表优先于第二行：面板盖在行上方/下方，点它不能当成选格子或开始画图
            if (HitDropdown(x, y)) { _down = false; _moved = true; _toolClickLatch = true; return; }
            // 第二行选项条（线色 / 马赛克方式 / 画笔粗细 / 文字字体）：命中就吃掉这次按下
            if (HitOptionsRow(x, y)) { _down = false; _moved = true; _toolClickLatch = true; return; }

            int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
            int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
            if (x >= x1 && x <= x2 && y >= y1 && y <= y2)
            {
                if (_annoTool == "Text")
                {
                    // 文字不是"拖出来"的：按下只记账 —— 抬起仍没拖动才开输入框；
                    // 落在已有文字上拖动 = 移动它（位置变化在抬手上报一条 UpdateLayer ⇒ Ctrl+Z 可回）。
                    var ap = ScreenToAnno(x, y);
                    _textClickArmed = true;
                    _textHitLayer = TextLayerAt(ap.X, ap.Y);
                    _textDownX = x; _textDownY = y;
                    _textDragging = false; _textDragMoved = false;
                    if (_textHitLayer is not null)
                    {
                        _textLayerStartX = _textHitLayer.X;
                        _textLayerStartY = _textHitLayer.Y;
                    }
                    SetCapture(_hwnd);
                    return; // _down 保持 true，由 Up/Move 收尾
                }
                _down = false; // 标注绘制不使用选区拖拽标志（shader 遮罩由选区外条件控制）
                _annoDown = true;
                _annoDragStart = ScreenToAnno(x, y);
                lock (_penPointsLock)
                {
                    _penPoints.Clear();
                    _penPoints.Add(_annoDragStart);
                }
                SetCapture(_hwnd);
                RequestRedraw();
                return;
            }
            _down = false;
            return;
        }

        if (_selComplete)
        {
            // 1. 工具栏按钮点击检测（QQ风格）。槽位序 = ToolbarContract.Main 声明序，动作直接取声明值。
            var items = ToolbarContract.Main;
            int n = items.Length;
            var lay = ToolbarFrame(n, _winW, _winH);
            int btnW = lay.BtnW, btnH = lay.BtnH, gap = lay.Gap, padX = lay.PadX, padY = lay.PadY;
            int tx = lay.Tx, ty = lay.Ty;
            for (int i = 0; i < n; i++)
            {
                int ix = tx + padX + i * (btnW + gap);
                int iy = ty + padY;
                if (x >= ix && x <= ix + btnW && y >= iy && y <= iy + btnH)
                {
                    _down = false;
                    _moved = true; // 防 Up 单击逻辑重置选区 (2026-08-11 bugfix)
                    _toolClickLatch = true; // 同一闩：这一下属于按钮，不属于"重画选区"（DoAction 若没关窗，Up 会把右下角写到按钮上）
                    DoAction(items[i].Action);
                    return;
                }
            }

            // 2. 四角手柄拖拽检测（欧氏距离，避免对角区域误判）
            int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
            int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
            int hit = _handleHit;
            bool Near(int px, int py, int tx, int ty) => (px - tx) * (px - tx) + (py - ty) * (py - ty) <= hit * hit;
            // 记下"手心到角的偏移"：手柄命中区有 12×DPI 那么宽，不记偏移的话，
            // 就算按在离角 10px 的地方，角也会在第一次 Move 时立刻跳到手心（看起来像"点一下就变形"）。
            if (Near(x, y, x1, y1)) { _dragHandle = 0; _dragOffX = x - x1; _dragOffY = y - y1; return; } // TL
            if (Near(x, y, x2, y1)) { _dragHandle = 1; _dragOffX = x - x2; _dragOffY = y - y1; return; } // TR
            if (Near(x, y, x1, y2)) { _dragHandle = 2; _dragOffX = x - x1; _dragOffY = y - y2; return; } // BL
            if (Near(x, y, x2, y2)) { _dragHandle = 3; _dragOffX = x - x2; _dragOffY = y - y2; return; } // BR

            // 3. 选区内部：拖拽 = 平移整框；单击 = 什么都不做（保持选区）。
            //    旧写法落进下面那行"从按下点重开一个选区"，于是一次误触就把整框重置，
            //    松开时又按"单击=选中悬停窗口/全屏"处理 ⇒ 选区一下跳成别的范围。
            //    ⚠ 必须先判"点确实在框内"：框外按下仍是【重开新选区】，否则用户想重画却平移了旧框。
            if (x >= x1 && x <= x2 && y >= y1 && y <= y2)
            {
                _movingSel = true;
                _moveOffX = x - x1;
                _moveOffY = y - y1;
                return;
            }
            // 4. 选区之外：重开一个新选区。
            //    这里必须把 _selComplete 放下 —— 否则整个重画过程里工具条/手柄仍按"已选完"画着，
            //    用户看到的是"框没断、按钮还在"，实际在画的是一个新框（同一个"看起来不像鼠标做的"症状）。
        }

        _selComplete = false;
        _sx1 = _sx2 = x; _sy1 = _sy2 = y;
    }

    private void Move(int x, int y)
    {
        TouchIdle();
        if (_sliderDrag)
        {
            float v = SliderValueAt(x);
            if (Math.Abs(v - _mosaicBrushPx) > 0.01f) { _mosaicBrushPx = v; RequestRedraw(); }
            _mx = x; _my = y;
            return; // 拖滑条时不画形状、也不更新格子悬停
        }
        if (_annoMode) UpdateOptionsHover(x, y);
        _mx = x; _my = y;

        if (_annoDown)
        {
            // 标注拖拽：画笔累积轨迹, 其他工具实时更新预览
            _moved = true;
            if (_annoTool == "Pen" || (_annoTool == "Mosaic" && _mosaicMode == "Brush"))
            {
                var ap = ScreenToAnno(x, y);
                lock (_penPointsLock)
                {
                    var last = _penPoints[^1];
                    if ((ap - last).Length() > 0.5f) _penPoints.Add(ap);
                }
            }
            RequestRedraw();
            return;
        }

        if (_annoMode)
        {
            // 文字工具：拖动已有文字 = 移动（预览直接改图层位置，抬手时再补一条可撤销的 UpdateLayer）
            if (_down && _textClickArmed)
            {
                if (!_textDragging && (Math.Abs(x - _textDownX) > 3 || Math.Abs(y - _textDownY) > 3))
                    _textDragging = true;
                if (_textDragging && _textHitLayer is not null)
                {
                    int selW = Math.Abs(_sx2 - _sx1), selH = Math.Abs(_sy2 - _sy1);
                    float bw = _textHitLayer.GetBounds().Width, bh = _textHitLayer.GetBounds().Height;
                    float nx = Math.Clamp(_textLayerStartX + (x - _textDownX), 0, Math.Max(0, selW - bw));
                    float ny = Math.Clamp(_textLayerStartY + (y - _textDownY), 0, Math.Max(0, selH - bh));
                    if (Math.Abs(nx - _textLayerStartX) >= 1 || Math.Abs(ny - _textLayerStartY) >= 1)
                        _textDragMoved = true;
                    _textHitLayer.X = nx; _textHitLayer.Y = ny;
                    RequestRedraw();
                }
                return;
            }
            RequestRedraw(); return; // 标注模式 hover 刷新工具栏高亮
        }

        if (_down && _dragHandle >= 0)
        {
            // 拖拽手柄调整选区：只动被抓住的那个角，另一角保持原位。
            // 坐标按抓取偏移换算并钳进窗口，避免"角跑出屏幕后回来时对不上"。
            int nx = Math.Clamp(x - _dragOffX, 0, Math.Max(0, _winW - 1));
            int ny = Math.Clamp(y - _dragOffY, 0, Math.Max(0, _winH - 1));
            _moved = true;
            switch (_dragHandle)
            {
                case 0: _sx1 = nx; _sy1 = ny; break; // TL
                case 1: _sx2 = nx; _sy1 = ny; break; // TR
                case 2: _sx1 = nx; _sy2 = ny; break; // BL
                case 3: _sx2 = nx; _sy2 = ny; break; // BR
            }
            RequestRedraw();
            return;
        }

        if (_down && _movingSel)
        {
            // 平移已完成选区：整框跟着鼠标走，尺寸不变，边界钳在窗口内。
            int sw = Math.Abs(_sx2 - _sx1), sh = Math.Abs(_sy2 - _sy1);
            int left = Math.Clamp(x - _moveOffX, 0, Math.Max(0, _winW - 1 - sw));
            int top = Math.Clamp(y - _moveOffY, 0, Math.Max(0, _winH - 1 - sh));
            if (left != Math.Min(_sx1, _sx2) || top != Math.Min(_sy1, _sy2)) _moved = true;
            _sx1 = left; _sy1 = top;
            _sx2 = left + sw; _sy2 = top + sh;
            RequestRedraw();
            return;
        }

        if (_down && (Math.Abs(x - _sx1) > 3 || Math.Abs(y - _sy1) > 3)) { _moved = true; _sx2 = x; _sy2 = y; RequestRedraw(); }
        else if (!_down && !_selComplete)
        {
            // 窗口悬停检测（QQ截图式）
            int screenX = _winX + x, screenY = _winY + y;
            int newHover = RegionDetector.FindSmallestRegionAt(_regions, screenX, screenY);
            if (newHover != _hoverRegion)
            {
                _hoverRegion = newHover;
                _hoverTitle = null; // 标题槽需重建（渲染线程 EnsureHoverTitle 会重新渲染）
                RequestRedraw();
            }
        }
        else if (!_down && _selComplete) RequestRedraw(); // hover 时刷新工具栏高亮
    }

    private void Up(int x, int y)
    {
        if (_sliderDrag)
        {
            _sliderDrag = false;
            _mosaicBrushPx = SliderValueAt(x);
            ReleaseCapture();
            LogService.Info("HdrCapture", $"画笔粗细 → {_mosaicBrushPx:F0}px", LogCategory.UI);
            RequestRedraw();
            return;
        }
        if (_toolClickLatch)
        {
            // 按下已经被标注工具条/选项条/下拉吃掉：这一次松手只是收尾。
            // 少了这道闩，退出标注模式后的那次 Up 会落到下面"重画选区"的尾巴，把右下角写成点击位置。
            _toolClickLatch = false;
            _down = false; _moved = false;
            RequestRedraw();
            return;
        }
        if (_annoDown)
        {
            // ── 标注绘制完成：提交图层 ──
            _annoDown = false;
            _down = false;
            ReleaseCapture();
            var end = ScreenToAnno(x, y);
            bool brushMosaic = _annoTool == "Mosaic" && _mosaicMode == "Brush";
            if (_annoTool == "Pen" || brushMosaic)
            {
                lock (_penPointsLock)
                {
                    if (_penPoints.Count >= 2)
                    {
                        _penPoints.Add(end);
                        if (brushMosaic)
                        {
                            var stroke = new MosaicBrushLayer
                            {
                                Points = [.. _penPoints],
                                BlockSize = Math.Max(4f, 10f * (float)_uiScale),
                                BrushWidth = MosaicBrushPx(),
                            };
                            _annoManager.AddLayer(stroke);
                            LogService.Info("HdrCapture",
                                $"马赛克涂抹提交 笔宽 {stroke.BrushWidth:F0}px 点数 {stroke.Points.Count}", LogCategory.UI);
                        }
                        else
                            _annoManager.AddLayer(new FreehandLayer
                            {
                                Points = [.. _penPoints],
                                Style = new BrushStyle
                                {
                                    StrokeColor = _annoColor,
                                    StrokeWidth = Math.Max(1f, _annoStrokeW),
                                    IsFillEnabled = false,
                                },
                            });
                    }
                    _penPoints.Clear();
                }
            }
            else
            {
                var start = _annoDragStart;
                float x1 = Math.Min(start.X, end.X), y1 = Math.Min(start.Y, end.Y);
                float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
                if (w >= 3 && h >= 3)
                {
                    var style = new BrushStyle
                    {
                        StrokeColor = _annoColor,
                        StrokeWidth = Math.Max(1f, _annoStrokeW),
                        IsFillEnabled = false,
                    };
                    AnnotationLayer layer = _annoTool switch
                    {
                        "Rect" => new RectangleLayer { X = x1, Y = y1, Width = w, Height = h, Style = style },
                        "Ellipse" => new EllipseLayer { CenterX = x1 + w / 2, CenterY = y1 + h / 2, RadiusX = w / 2, RadiusY = h / 2, Style = style },
                        "Arrow" => new ArrowLayer { StartX = start.X, StartY = start.Y, EndX = end.X, EndY = end.Y, Style = style },
                        "Mosaic" => new MosaicLayer { X = x1, Y = y1, Width = w, Height = h, BlockSize = Math.Max(4f, 10f * (float)_uiScale) },
                        _ => new RectangleLayer { X = x1, Y = y1, Width = w, Height = h, Style = style }
                    };
                    _annoManager.AddLayer(layer);
                }
            }
            RequestRedraw();
            return;
        }

        if (_annoMode)
        {
            if (_textClickArmed)
            {
                var hit = _textHitLayer;
                bool dragged = _textDragging;
                bool moved = _textDragging && _textDragMoved;
                _textClickArmed = false;
                _textHitLayer = null;
                _textDragging = false;
                _textDragMoved = false;
                _down = false;
                if (moved && hit is not null)
                {
                    var after = (TextLayer)hit.Clone();   // Clone 保留 Id（见 AnnotationLayer.Id 注释）
                    // 先把 live 层还原到拖动前：UpdateLayer 抓的 before 必须是"拖动前"，
                    // 否则撤销只能回到拖动后的位置（等于没有撤销）。
                    hit.X = _textLayerStartX; hit.Y = _textLayerStartY;
                    _annoManager.UpdateLayer(hit.Id, after);
                    LogService.Info("HdrCapture", $"文字移动 → ({after.X:0},{after.Y:0})", LogCategory.UI);
                    RequestRedraw();
                    return;
                }
                if (dragged) { RequestRedraw(); return; } // 空处拖了一段：不当单击，不开输入框
                // 单击：命中文字 = 再编辑它；空处 = 在这一点新建
                ShowTextEdit(new System.Numerics.Vector2(x, y), hit);
                RequestRedraw();
                return;
            }
            _down = false; return; // 标注模式下 Up 不处理选区 (2026-08-11 bugfix)
        }

        // ═══ 手柄拖拽 / 平移选区：端点已由 Move 维护，这里【不许】再把 (_sx2,_sy2) 写成松手点 ═══
        // 旧写法无条件执行下面那两行 ⇒ 抓 TL/TR/BL 时"被固定的那个角"会被改写成鼠标位置：
        // 抓左上角松手 = 右上角也被拖到左上角 ⇒ 选区当场塌成一条线（只有 BR 恰好正确）。
        // 这也是"点四个角调大小，范围却乱跳"的主因；顺带把"点一下角就变全屏/别的窗口"一起止住。
        if (_dragHandle >= 0 || _movingSel)
        {
            bool resized = _dragHandle >= 0;
            _down = false;
            _dragHandle = -1;
            _movingSel = false;
            RequestRedraw();
            // 与下面"选区完成"同一用途的前提见证：调整/平移之后交给裁切链的到底是多大一块，
            // 只有窗自己写得出来。单击框内不动不记（没调整，别污染日志）。
            if (_moved)
                LogService.Info("HdrCapture",
                    $"{(resized ? "选区调整" : "选区平移")} {Math.Abs(_sx2 - _sx1)}x{Math.Abs(_sy2 - _sy1)} "
                    + $"@(窗口内 {Math.Min(_sx1, _sx2)},{Math.Min(_sy1, _sy2)})",
                    LogCategory.Capture);
            return;
        }

        _down = false; _sx2 = x; _sy2 = y;
        if (!_moved)
        {
            // 单击：优先选中悬停的窗口（QQ截图式）
            if (_hoverRegion >= 0 && _hoverRegion < _regions.Count)
            {
                var r = _regions[_hoverRegion];
                _sx1 = r.X - _winX; _sy1 = r.Y - _winY;
                _sx2 = _sx1 + r.Width; _sy2 = _sy1 + r.Height;
            }
            else
            {
                // 无悬停窗口 → 全屏
                _sx1 = 0; _sy1 = 0; _sx2 = _winW; _sy2 = _winH;
            }
        }
        _selComplete = true; RequestRedraw();
        // 这条 Info 是给"选区之后按 Ctrl+C / Enter 有没有落地"这类判据当【前提见证】用的：
        // 2026-09-30 端到端取证里，注入式拖拽三次里两次根本没进入选完状态，而产品在这里一个字都不写，
        // 于是"Ctrl+C 没反应"分不清是【键没进来】还是【压根没选中】（只能再追一发 Enter 反推）。
        // 判据读不到自己的前提，就只能在错误上盖结论 —— 这行把它变成可核对的。
        LogService.Info("HdrCapture",
            $"选区完成 {Math.Abs(_sx2 - _sx1)}x{Math.Abs(_sy2 - _sy1)} @(窗口内 {Math.Min(_sx1, _sx2)},{Math.Min(_sy1, _sy2)})",
            LogCategory.Capture);
    }

    private void Key(int vk)
    {
        TouchIdle();
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        if (_editHwnd != 0) return; // 文字输入中：EDIT 子窗口自己处理 Enter/Esc
        if (_annoMode)
        {
            if (vk == VK_ESC && _ddOpen != 0) { CloseDropdown(); return; }
            if (vk == VK_ESC) { ExitAnnotationMode(); return; }
            if (ctrl && vk == VK_Z) { _annoManager.Undo(); _annoTextSlotsDirty = true; RequestRedraw(); return; }
            if (ctrl && vk == VK_Y) { _annoManager.Redo(); _annoTextSlotsDirty = true; RequestRedraw(); return; }
            // 文字字体的键盘等价物（控件条之外的快路；写进同一份状态）
            if (ctrl && _annoTool == "Text" && vk == VK_B) { _textFontBold = !_textFontBold; OnFontStateChanged(); return; }
            if (ctrl && _annoTool == "Text" && vk == VK_I) { _textFontItalic = !_textFontItalic; OnFontStateChanged(); return; }
            if (ctrl && _annoTool == "Text" && (vk == VK_OEM_4 || vk == VK_OEM_6))
            {
                StepFontSize(vk == VK_OEM_6 ? +1 : -1);
                return;
            }
            // 标注模式下 Enter = 保存（保持现状行为）
        }
        if (vk == VK_ESC) { FireAction(MainToolbarAction.Cancel, 0, 0, 0, 0); Close(); }
        else if ((vk == VK_ENT || (vk == VK_S && !ctrl)) && _selComplete) DoAction(MainToolbarAction.Save);
        else if (vk == VK_C && ctrl && _selComplete) DoAction(MainToolbarAction.Copy); // Ctrl+C 复制
    }

    private void DoAction(MainToolbarAction a)
    {
        if (!_selComplete) return;
        if (a == MainToolbarAction.Annotate)
        {
            // 2026-08-11 A方案: 标注改为窗口内内联编辑, 不再关闭窗口
            EnterAnnotationMode();
            return;
        }
        int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
        int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
        FireAction(a, _winX + x1, _winY + y1, x2 - x1, y2 - y1);
        Close();
    }

    // ═══════════════════════════════════════
    //  标注模式
    // ═══════════════════════════════════════

    private void EnterAnnotationMode()
    {
        if (_annoMode) return;
        _annoMode = true;
        _annoTool = "Rect";
        _annoDown = false;
        ShowSecondRowForTool(); // 矩形工具 → 第二行出颜色格
        LogService.Info("HdrCapture", "进入标注模式", LogCategory.UI);
        RequestRedraw();
    }

    /// <summary>探针/内嵌入口：不经工具条点击直接进入标注模式并选中工具（工具条点击仍走 OnAnnoToolbarClick）。</summary>
    internal void EnterAnnotationModeForProbe(string tool)
    {
        EnterAnnotationMode();
        _annoTool = tool;
        ShowSecondRowForTool();
        RequestRedraw();
    }

    // ── 探针读数口（--preview-ui-probe）──
    // 第二行的字体/字号/滑条都画在 D3D 帧里，没有 Win32 句柄可拿 ⇒ 探针只能拿"窗自己算出来的
    // 格子矩形"，再用真鼠标消息点那些坐标（走 Down→HitOptionsRow→HitDropdown 这条产品路径）。
    // 这些成员只读、不改状态，产品侧不消费。
    internal int ProbeOptionsRowMode() => OptionsRowMode();
    internal (int X, int Y, int CellH, int Gap, int[] W, int[] CellX) ProbeOptionsRow() => CurrentOptionsRow();
    internal int ProbeSliderInset() => SliderInset();
    internal int ProbeDropdownOpen() => _ddOpen;
    internal int ProbeDropdownTop() => _ddTop;
    internal (int X, int Y, int W, int ItemH, int Visible) ProbeDropdownGeometry() => DdGeometry();
    internal int ProbeDdBandCapacity() => DdBandCapacity();
    internal int ProbeDdBandCapacityAt(float scale) => DdBandCapacityAt(scale, TextRowWidths(scale).PanelW);
    internal int ProbeBakedDdRows() => _ddBakedRows;
    internal int ProbeDdMaxVisible() => DdMaxVisible;
    internal int[] ProbeTextFontSizes() => TextFontSizes;
    internal float ProbeFontSizeDip() => _textFontSizeDip;
    internal bool ProbeTextBold() => _textFontBold;
    internal bool ProbeAnnoMode() => _annoMode;
    internal double ProbeUiScale() => _uiScale;
    internal float ProbeBrushPx() => MosaicBrushPx();

    private void ExitAnnotationMode()
    {
        if (!_annoMode) return;
        _annoMode = false;
        _annoDown = false;
        // 拖滑条时按 Esc：Move() 会先看 _sliderDrag 再看出标注模式，不收尾就把整块界面卡住
        if (_sliderDrag) { _sliderDrag = false; ReleaseCapture(); }
        lock (_penPointsLock) { _penPoints.Clear(); }
        DestroyEdit();
        CloseDropdown();
        HideOptionsRow();
        LogService.Info("HdrCapture", "退出标注模式", LogCategory.UI);
        RequestRedraw();
    }

    /// <summary>标注工具栏命中检测（10 按钮, 与 UpdateUiConstantBuffer 布局一致）。</summary>
    private (bool hit, int idx) HitAnnoToolbar(int x, int y)
    {
        if (!_selComplete) return (false, -1);
        int n = 10;
        var lay = ToolbarFrame(n, _winW, _winH);
        int tx = lay.Tx, ty = lay.Ty;
        for (int i = 0; i < n; i++)
        {
            int ix = tx + lay.PadX + i * (lay.BtnW + lay.Gap);
            int iy = ty + lay.PadY;
            if (x >= ix && x <= ix + lay.BtnW && y >= iy && y <= iy + lay.BtnH)
                return (true, i);
        }
        return (false, -1);
    }

    private void OnAnnoToolbarClick(int idx)
    {
        switch (idx)
        {
            case 0: _annoTool = "Rect"; break;
            case 1: _annoTool = "Ellipse"; break;
            case 2: _annoTool = "Arrow"; break;
            case 3: _annoTool = "Pen"; break;
            case 4: _annoTool = "Text"; break;
            case 5: _annoTool = "Mosaic"; break;
            case 6: _annoManager.Undo(); _annoTextSlotsDirty = true; break; // 撤销
            case 7: _annoManager.Redo(); _annoTextSlotsDirty = true; break; // 重做
            case 8: ExitAnnotationMode(); break; // 完成
            case 9: ExitAnnotationMode(); break; // 取消
        }
        ShowSecondRowForTool(); // 选工具 → 弹出它自己的第二行（文字=字体/字号，形状=颜色，马赛克=方式）
        RequestRedraw();
    }

    // ── 文字标注：字体工具条 / 就地输入 / 再编辑 / 拖动（2026-10-27 富文本） ──
    // 字形度量与位图只走 AnnotationRasterizer.RasterizeText —— 预览图集与最终输出是同一张位图。

    private static WndProcDelegate? s_editProc;
    private static nint s_editOrigProc;

    private static nint EditWndProc(nint h, uint m, nint w, nint l)
    {
        if (m == WM_KEY)
        {
            int vk = (int)w;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            // Enter 提交；Shift+Enter 交给 EDIT 自己插换行（ES_WANTRETURN）；Esc 放弃
            if (vk == VK_ESC || (vk == VK_ENT && !shift))
            {
                if (s_windows.TryGetValue(GetParent(h), out var self))
                {
                    self.OnEditKey(vk);
                    return 0;
                }
            }
        }
        // 每次内容变化都重烘预览（输入框本身看不见，画在帧里的才是用户看到的那份）
        if (m == WM_CHAR || m == WM_KEYUP || m == WM_PASTE || m == WM_CUT || m == WM_UNDO)
        {
            nint r = CallWindowProcW(s_editOrigProc, h, m, w, l);
            if (s_windows.TryGetValue(GetParent(h), out var owner)) owner.SyncEditPreview();
            return r;
        }
        return CallWindowProcW(s_editOrigProc, h, m, w, l);
    }

    /// <summary>命中测试：点（选区图像坐标）所在的最上层可见文字图层。</summary>
    private TextLayer? TextLayerAt(float ax, float ay)
    {
        var layers = _annoManager.GetLayersSnapshot();
        for (int i = layers.Length - 1; i >= 0; i--)
        {
            if (layers[i] is not TextLayer t || !t.IsVisible) continue;
            var b = t.GetBounds();
            if (ax >= b.Left && ax <= b.Right && ay >= b.Top && ay <= b.Bottom) return t;
        }
        return null;
    }

    private static readonly int[] TextFontSizes =
        [12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 56, 64, 72];

    // ── 第二行工具选项条 ──
    // 明确要求（2026-10-28 用户）：选中某个工具后，工具条【下方】再出一行它自己的选项。
    // 形状/画笔/箭头 → 线色；马赛克 → 区域框选/画笔涂抹 → 画笔涂抹时再出粗细；
    // 文字 → 字体家族 / 字号（下拉）+ B / I。
    // 整条都画在 D3D 帧里（Win32 子控件在这块全屏交换链上一个像素都不显示，见 _annoColor 上方说明）。

    private int OptionsRowHeight() => Math.Max(26, (int)Math.Round(30 * _uiScale));

    /// <summary>当前工具是否要出第二行。</summary>
    private bool OptionsRowForTool()
        => _annoMode && _selComplete
           && _annoTool is "Rect" or "Ellipse" or "Arrow" or "Pen" or "Mosaic" or "Text";

    /// <summary>第二行形态：0=线色色板 1=马赛克方式 2=画笔粗细 3=文字字体。</summary>
    private int OptionsRowMode() => _annoTool switch
    {
        "Text" => 3,
        "Mosaic" => _mosaicMode == "Brush" ? 2 : 1,
        _ => 0,
    };

    /// <summary>每个格子的宽度（文字行的家族/字号/B/I 四种宽度不同，其余等宽）。</summary>
    private int[] OptionsRowWidths()
    {
        int hs = OptionsRowHeight();
        switch (OptionsRowMode())
        {
            case 0: return [hs, hs, hs, hs, hs, hs];
            case 1: return [.. Enumerable.Repeat(Math.Max(70, (int)Math.Round(96 * _uiScale)), IdOptMosaicCount)];
            // 画笔粗细：左格是滑条（宽），右格是当前值（窄）
            case 2: return [Math.Max(200, (int)Math.Round(260 * _uiScale)), Math.Max(70, (int)Math.Round(96 * _uiScale))];
            default:
            {
                var (famW, sizeW, biW, _) = TextRowWidths((float)_uiScale);
                return [famW, sizeW, biW, biW];
            }
        }
    }

    /// <summary>
    /// 文字行（形态 3）四格宽度与下拉面板宽 —— 只依赖缩放，所以图集烘焙、命中检测、容量计算
    /// 都可以用同一份表达式（容量还要算"别的缩放下装得下几行"，不能读当前状态）。
    /// PanelW 取家族/字号两列里较宽的那个：容量按最坏的列算，任何一列打开都不会超。
    /// </summary>
    private static (int FamW, int SizeW, int BiW, int PanelW) TextRowWidths(float scale)
    {
        int famW = Math.Max(150, (int)Math.Round(220 * scale));
        int sizeW = Math.Max(64, (int)Math.Round(84 * scale));
        int biW = Math.Max(30, (int)Math.Round(40 * scale));
        int panelW = Math.Max(Math.Max(famW, sizeW), Math.Max(160, (int)Math.Round(200 * scale)));
        return (famW, sizeW, biW, panelW);
    }

    /// <summary>往文字带里逐格排版（纯算术）。装不下的格子返回 H=0，后续格子也不会再装回去。</summary>
    private static (int X, int Y, int W, int H)[] PackTextBand(
        int atlasW, int bandTop, int bandH, int[] cw, int[] ch)
    {
        var rects = new (int X, int Y, int W, int H)[cw.Length];
        int sx = 0, sy = bandTop, rowH = 0;
        for (int i = 0; i < cw.Length; i++)
        {
            if (sx + cw[i] > atlasW) { sy += rowH; sx = 0; rowH = 0; }
            if (sy + ch[i] > bandTop + bandH) continue;
            rects[i] = (sx, sy, cw[i], ch[i]);
            sx += cw[i] + 2;
            rowH = Math.Max(rowH, ch[i]);
        }
        return rects;
    }

    /// <summary>文字带在给定缩放下真能烘下几项下拉列表（与 EnsureTextAtlas 同一份排版算术）。</summary>
    private int DdBandCapacityAt(float scale, int panelW)
    {
        int cellH = Math.Max(26, (int)Math.Round(30 * scale));
        var (famW, sizeW, biW, _) = TextRowWidths(scale);
        int[] cw = [famW, sizeW, biW, biW];
        int[] ch = [cellH, cellH, cellH, cellH];
        Array.Resize(ref cw, 4 + DdMaxVisible);
        Array.Resize(ref ch, 4 + DdMaxVisible);
        for (int i = 0; i < DdMaxVisible; i++) { cw[4 + i] = panelW; ch[4 + i] = cellH; }
        var rects = PackTextBand(AtlasW, AtlasH - AtlasBandH, AtlasBandH, cw, ch);
        int n = 0;
        while (n < DdMaxVisible && rects[4 + n].H > 0) n++;
        return n;
    }

    private int DdBandCapacity() => DdBandCapacityAt((float)_uiScale, DdPanelWidth());

    private int OptionsRowGap() => Math.Max(4, (int)Math.Round(6 * _uiScale));

    /// <summary>第二行的几何（绘制与命中检测共用一处，避免两边算法漂移）。</summary>
    private (int X, int Y, int CellH, int Gap, int[] W, int[] CellX) OptionsRowGeometry(
        (int Tx, int Ty, int BtnW, int BtnH, int Gap, int PadX, int PadY, int TotalW, int TotalH) tb)
    {
        int cellH = OptionsRowHeight();
        int gap = OptionsRowGap();
        var w = OptionsRowWidths();
        int totalW = w.Sum() + (w.Length - 1) * gap;
        int bx = Math.Clamp(tb.Tx + tb.TotalW / 2 - totalW / 2, 4, Math.Max(4, _winW - totalW - 4));
        int by = tb.Ty + tb.TotalH + 6;                                  // 工具条正下方
        if (by + cellH + 4 > _winH) by = Math.Max(4, tb.Ty - cellH - 6);  // 下方放不下 → 上方
        var xs = new int[w.Length];
        int cx = bx;
        for (int i = 0; i < w.Length; i++) { xs[i] = cx; cx += w[i] + gap; }
        return (bx, by, cellH, gap, w, xs);
    }

    /// <summary>第二行几何（命中/悬停/上传三处共用；工具条落位取自 ToolbarFrame 这一处）。</summary>
    private (int X, int Y, int CellH, int Gap, int[] W, int[] CellX) CurrentOptionsRow()
        => OptionsRowGeometry(ToolbarFrame(10, _winW, _winH));

    /// <summary>按当前工具切换第二行（文字行 = 字体/字号/B/I）。</summary>
    private void ShowSecondRowForTool()
    {
        CloseDropdown();
        _optRowVisible = OptionsRowForTool();
        _optHover = -1;
        _uiTextDirty = true;
        if (_optRowVisible) RequestRedraw();
    }

    // ── 字体/字号下拉列表 ──

    private string[] FontFamilies()
    {
        if (_fontFamilies is not null) return _fontFamilies;
        var list = FontLoader.GetSystemFonts()
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        if (!list.Any(n => string.Equals(n, _textFontFamily, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, _textFontFamily);
        return _fontFamilies = [.. list];
    }

    private int DdItemCount() => _ddOpen == 1 ? FontFamilies().Length : TextFontSizes.Length;

    private string DdItemText(int i) => _ddOpen == 1
        ? FontFamilies()[i]
        : TextFontSizes[i].ToString(System.Globalization.CultureInfo.InvariantCulture);

    private int FamilyIndex()
    {
        int i = Array.FindIndex(FontFamilies(), n => string.Equals(n, _textFontFamily, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    private int SizeIndex()
    {
        int i = Array.IndexOf(TextFontSizes, (int)Math.Round(_textFontSizeDip));
        return i < 0 ? 0 : i;
    }

    private int DdSelIndex() => _ddOpen == 2 ? SizeIndex() : FamilyIndex();

    private int DdPanelWidth() => Math.Max(
        OptionsRowWidths()[_ddOpen == 2 ? 1 : 0],
        Math.Max(160, (int)Math.Round(200 * _uiScale)));

    /// <summary>下拉面板几何（打开状态才有效）。放不下就翻到行的上方，再不行就贴底。</summary>
    private (int X, int Y, int W, int ItemH, int Visible) DdGeometry()
    {
        var g = CurrentOptionsRow();
        int w = DdPanelWidth();
        int itemH = OptionsRowHeight();
        int count = DdItemCount();
        int maxRows = Math.Max(3, (_winH / 2) / Math.Max(1, itemH));
        // 可见行数还必须 ≤ 文字带真装得下的行数：多出来的行图集里没有字，只会画成空白条
        int vis = Math.Min(Math.Min(count, DdMaxVisible), Math.Min(maxRows, Math.Max(1, DdBandCapacity())));
        int x = Math.Clamp(g.CellX[_ddOpen == 2 ? 1 : 0], 4, Math.Max(4, _winW - w - 4));
        int y = g.Y + g.CellH + 2;
        if (y + vis * itemH + 4 > _winH)
        {
            int above = g.Y - 2 - vis * itemH;
            y = above >= 4 ? above : Math.Max(4, _winH - vis * itemH - 4);
        }
        return (x, y, w, itemH, vis);
    }

    private void CloseDropdown()
    {
        if (_ddOpen == 0) return;
        _ddOpen = 0;
        _ddTop = 0;
        _ddHover = -1;
        _uiTextDirty = true;
        RequestRedraw();
    }

    private void ToggleFontDropdown(int which)
    {
        if (_ddOpen == which) { CloseDropdown(); return; }
        _ddOpen = which;
        _ddHover = -1;
        var d = DdGeometry();
        _ddTop = Math.Clamp(DdSelIndex() - d.Visible / 2, 0, Math.Max(0, DdItemCount() - d.Visible));
        _uiTextDirty = true;
        RequestRedraw();
        LogService.Info("HdrCapture", which == 1 ? "字体列表展开" : "字号列表展开", LogCategory.UI);
    }

    /// <summary>滚轮翻列表（只在展开时吃）。delta > 0 = 上滚。</summary>
    private void DdScroll(int delta)
    {
        if (_ddOpen == 0) return;
        var d = DdGeometry();
        int maxTop = Math.Max(0, DdItemCount() - d.Visible);
        int step = Math.Max(1, d.Visible / 2);
        int nt = Math.Clamp(_ddTop + (delta > 0 ? -step : step), 0, maxTop);
        if (nt == _ddTop) return;
        _ddTop = nt;
        _uiTextDirty = true;
        RequestRedraw();
    }

    private bool DdContains(int x, int y)
    {
        if (_ddOpen == 0) return false;
        var d = DdGeometry();
        return x >= d.X && x <= d.X + d.W && y >= d.Y && y <= d.Y + d.Visible * d.ItemH;
    }

    /// <summary>下拉命中：点面板内 = 选中该项；点面板外 = 只收起列表（这一下不再落到画布上）。</summary>
    private bool HitDropdown(int x, int y)
    {
        if (_ddOpen == 0) return false;
        if (!DdContains(x, y)) { CloseDropdown(); return true; }
        var d = DdGeometry();
        int idx = _ddTop + (y - d.Y) / d.ItemH;
        if (idx >= 0 && idx < DdItemCount())
        {
            if (_ddOpen == 1) _textFontFamily = FontFamilies()[idx];
            else _textFontSizeDip = TextFontSizes[idx];
            ApplyFontToEditor();
            SyncEditPreview();
            LogService.Info("HdrCapture",
                _ddOpen == 1 ? $"文字字体 → {_textFontFamily}" : $"文字字号 → {_textFontSizeDip:F0} dip", LogCategory.UI);
        }
        CloseDropdown();
        return true;
    }

    private void HideOptionsRow()
    {
        if (!_optRowVisible) return;
        _optRowVisible = false;
        _optHover = -1;
    }

    /// <summary>当前线色在色板里的下标（不在色板里返回 -1）。</summary>
    private int SwatchIndex()
    {
        for (int i = 0; i < s_swatchColor.Length; i++)
            if (Math.Abs(_annoColor.R - s_swatchColor[i].R) < 0.01f
                && Math.Abs(_annoColor.G - s_swatchColor[i].G) < 0.01f
                && Math.Abs(_annoColor.B - s_swatchColor[i].B) < 0.01f)
                return i;
        return -1;
    }

    /// <summary>粗细滑条的轨道内缩（物理像素）。绘制与拖拽换算共用这一处，避免两边刻度漂移。</summary>
    private int SliderInset() => Math.Max(8, (int)Math.Round(12 * _uiScale));

    /// <summary>滑条轨道左右端（物理像素 x）。</summary>
    private (int X0, int X1) SliderTrack()
    {
        var g = CurrentOptionsRow();
        int inset = SliderInset();
        return (g.CellX[0] + inset, g.CellX[0] + g.W[0] - inset);
    }

    /// <summary>把鼠标 x 换算成 1..100 px 的粗细（越界自动夹住）。</summary>
    private float SliderValueAt(int x)
    {
        var (x0, x1) = SliderTrack();
        float t = x1 > x0 ? Math.Clamp((x - x0) / (float)(x1 - x0), 0f, 1f) : 0f;
        return MathF.Round(MosaicBrushMinPx + t * (MosaicBrushMaxPx - MosaicBrushMinPx));
    }

    /// <summary>
    /// 画笔粗细的唯一取值点：拖拽中的涂抹预览与提交进图层的 BrushWidth 必须读它。
    /// <para>两边各算一份就会漂 —— 2026-10-28 实测预览写死 `28 × UI 缩放`，于是 150%/200% 下
    /// 预览比输出粗一倍、滑条拖到 100 松手反而变细。</para>
    /// </summary>
    private float MosaicBrushPx() => Math.Clamp(_mosaicBrushPx, MosaicBrushMinPx, MosaicBrushMaxPx);

    /// <summary>第二行命中检测：命中就应用选择并返回 true（调用方应吃掉这次点击）。</summary>
    private bool HitOptionsRow(int x, int y)
    {
        if (!OptionsRowForTool()) return false;
        var g = CurrentOptionsRow();
        if (y < g.Y || y > g.Y + g.CellH) return false;
        int mode = OptionsRowMode();
        for (int i = 0; i < g.W.Length; i++)
        {
            if (x < g.CellX[i] || x > g.CellX[i] + g.W[i]) continue;
            switch (mode)
            {
                case 0:
                    _annoColor = s_swatchColor[i];
                    LogService.Info("HdrCapture",
                        $"标注线色 → #{_annoColor.R * 255:F0},{_annoColor.G * 255:F0},{_annoColor.B * 255:F0}", LogCategory.UI);
                    break;
                case 2:
                    // 粗细是一条滑条：按下即跳到该处并进入拖拽（随后由 Move/Up 收尾）
                    if (i == 0)
                    {
                        _sliderDrag = true;
                        _mosaicBrushPx = SliderValueAt(x);
                        RequestRedraw();
                        return true;
                    }
                    return true; // 右格只是数值显示

                case 3:
                    if (i == 0) { ToggleFontDropdown(1); return true; }
                    if (i == 1) { ToggleFontDropdown(2); return true; }
                    if (i == 2) _textFontBold = !_textFontBold;
                    else _textFontItalic = !_textFontItalic;
                    LogService.Info("HdrCapture",
                        $"文字样式 → B={(_textFontBold ? 1 : 0)} I={(_textFontItalic ? 1 : 0)}", LogCategory.UI);
                    OnFontStateChanged();
                    return true;
                default:
                    _mosaicMode = i == 0 ? "Rect" : "Brush";
                    LogService.Info("HdrCapture", $"马赛克方式 → {_mosaicMode}", LogCategory.UI);
                    CloseDropdown();
                    break;
            }
            RequestRedraw();
            return true;
        }
        return false;
    }

    /// <summary>悬停高亮：第二行格子 + 下拉列表项（变化才重绘）。</summary>
    private void UpdateOptionsHover(int x, int y)
    {
        int hov = -1;
        if (OptionsRowForTool())
        {
            var g = CurrentOptionsRow();
            if (y >= g.Y && y <= g.Y + g.CellH)
                for (int i = 0; i < g.W.Length; i++)
                    if (x >= g.CellX[i] && x <= g.CellX[i] + g.W[i]) { hov = i; break; }
        }
        if (hov != _optHover) { _optHover = hov; RequestRedraw(); }

        int dhov = -1;
        if (_ddOpen != 0 && DdContains(x, y))
        {
            var d = DdGeometry();
            int row = (y - d.Y) / d.ItemH;
            if (row >= 0 && row < d.Visible) dhov = _ddTop + row;
        }
        if (dhov != _ddHover) { _ddHover = dhov; RequestRedraw(); }
    }

    /// <summary>字体状态变了：格子文字要重烘、EDIT 与输入预览跟着换字形。</summary>
    private void OnFontStateChanged()
    {
        _uiTextDirty = true;
        ApplyFontToEditor();
        SyncEditPreview();
        RequestRedraw();
    }

    /// <summary>Ctrl+[ / Ctrl+]：在预设字号表里走一格（界面上与下拉框保持同步）。</summary>
    private void StepFontSize(int dir)
    {
        int cur = (int)Math.Round(_textFontSizeDip);
        int idx = Array.IndexOf(TextFontSizes, cur);
        if (idx < 0)
        {
            idx = dir > 0
                ? Array.FindIndex(TextFontSizes, s => s > cur)
                : Array.FindLastIndex(TextFontSizes, s => s < cur);
            if (idx < 0) idx = dir > 0 ? TextFontSizes.Length - 1 : 0;
        }
        else idx = Math.Clamp(idx + dir, 0, TextFontSizes.Length - 1);
        _textFontSizeDip = TextFontSizes[idx];
        OnFontStateChanged();
    }

    /// <summary>按当前字体状态重建 EDIT 的 HFONT（输入框里看到的字号/字形 = 将写进图层的）。</summary>
    private void ApplyFontToEditor()
    {
        if (_editHwnd == 0) return;
        if (_editFont != 0) { DeleteObject(_editFont); _editFont = 0; }
        int px = Math.Max(8, (int)Math.Round(_textFontSizeDip * _uiScale));
        _editFont = CreateFontW(-px, 0, 0, 0, _textFontBold ? 700 : 400,
            _textFontItalic ? 1u : 0u, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 0, 0, _textFontFamily);
        if (_editFont != 0) SendMessageInt(_editHwnd, WM_SETFONT, _editFont, 1);
    }

    private void ShowTextEdit(System.Numerics.Vector2 framePos, TextLayer? existing)
    {
        if (_editHwnd != 0) return;
        if (existing is not null)
        {
            // 再编辑：把该层的字体状态带回控件条（与上游"选中文字后工具条跟随"同一语义）
            _textFontFamily = existing.FontFamily;
            _textFontSizeDip = Math.Max(1f, existing.FontSize / (float)_uiScale);
            _textFontBold = existing.Bold;
            _textFontItalic = existing.Italic;
            _uiTextDirty = true;
            // existing.X/Y 是【选区图像坐标】，输入框要的是帧坐标 ⇒ 加上选区原点
            framePos = new System.Numerics.Vector2(
                Math.Min(_sx1, _sx2) + existing.X, Math.Min(_sy1, _sy2) + existing.Y);
        }
        _editingLayer = existing;
        _textInsertPos = ScreenToAnno((int)framePos.X, (int)framePos.Y);

        int selW = Math.Abs(_sx2 - _sx1);
        int ew = Math.Max(180, (int)Math.Round(Math.Min(360 * _uiScale, Math.Max(180, selW - 20))));
        int eh = existing is { CachedHeight: > 0 }
            ? (int)Math.Clamp(existing.CachedHeight + 16, (int)Math.Round(40 * _uiScale), (int)Math.Round(320 * _uiScale))
            : Math.Max(40, (int)Math.Round(40 * _uiScale));
        if (eh > _winH) eh = Math.Max(30, _winH - 8);
        int ex = Math.Clamp((int)framePos.X, 0, Math.Max(0, _winW - ew));
        int ey = Math.Clamp((int)framePos.Y, 0, Math.Max(0, _winH - eh));
        _editFrameX = ex; _editFrameY = ey;
        _editHwnd = CreateWindowExW(0, "EDIT", "",
            WS_CHILD | WS_VIS | WS_BORDER | ES_MULTILINE | ES_WANTRETURN | ES_AUTOVSCROLL | ES_NOHIDESEL,
            ex, ey, ew, eh, _hwnd, (nint)1001, GetModuleHandleW(null), 0);
        if (_editHwnd == 0) return;

        if (existing is not null)
        {
            SetWindowTextW(_editHwnd, existing.Text);
            SendMessageInt(_editHwnd, EM_SETSEL, 0, -1); // 全选旧文字，输入即替换
        }
        ApplyFontToEditor();

        // ═══ 2026-09-15 修复：文字标注第二次起失效 ═══
        // 原实现 `if (s_editOrigProc == 0)` 只在【首次】做子类化，且 s_editOrigProc 是 static、
        // DestroyEdit 又不还原 ⇒ 第二次及以后新建的 EDIT 完全没有自定义 WndProc：
        // Enter/Esc 无响应 → 文字无法提交，输入框也不销毁、一直挡在截图上。
        // 现改为每次创建都记录并子类化；DestroyEdit 中还原并清零。
        // （同一时刻最多存在一个文字输入框，static 保存"当前"值是安全的。
        //   s_editProc 必须用 static 字段持有委托引用，否则委托被 GC 后原生回调会崩。）
        s_editProc = EditWndProc;
        s_editOrigProc = GetWindowLongPtrW(_editHwnd, GWLP_WNDPROC);
        SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(s_editProc));
        SetFocus(_editHwnd);
        SyncEditPreview();
        LogService.Info("HdrCapture", existing is null ? "文字输入框打开（新建）" : "文字输入框打开（再编辑）", LogCategory.UI);
    }

    private void OnEditKey(int vk)
    {
        if (vk == VK_ENT) CommitTextEdit();
        else if (vk == VK_ESC) CancelTextEdit();
    }

    /// <summary>提交输入框 → AddLayer（新建）或 UpdateLayer（再编辑），两者都进同一撤销栈。</summary>
    private void CommitTextEdit()
    {
        if (_editHwnd == 0) return;
        var sb = new System.Text.StringBuilder(4096);
        int len = GetWindowTextSb(_editHwnd, sb, sb.Capacity);
        string text = len > 0 ? sb.ToString(0, len).Trim() : "";
        var editing = _editingLayer;
        var insert = _textInsertPos;
        DestroyEdit();

        if (text.Length == 0)
        {
            // 清空已有文字 = 删除该层（可撤销）；新建空文字 = 什么都不做
            if (editing is not null)
            {
                _annoManager.RemoveLayer(editing.Id);
                _annoTextSlotsDirty = true;
                RequestRedraw();
            }
            return;
        }

        float pad = 6f;
        float fontPx = Math.Max(8f, _textFontSizeDip * (float)_uiScale);
        int selW = Math.Abs(_sx2 - _sx1);
        // 折行宽度 = 选区宽度（两端量法相同）；上限 1600 防止一张图被一行字撑爆
        float maxW = Math.Clamp(selW - pad * 2, 120f, 1600f);

        TextLayer layer;
        if (editing is null)
        {
            layer = new TextLayer { X = insert.X, Y = insert.Y };
        }
        else
        {
            // 必须新建实例再 UpdateLayer：直接改 live 对象会让管理器抓到的 before 也是新状态（撤销/重做全失效）
            layer = new TextLayer { X = editing.X, Y = editing.Y };
            layer.TakeIdentityFrom(editing);
        }
        layer.Text = text;
        layer.FontFamily = _textFontFamily;
        layer.FontSize = fontPx;
        layer.Bold = _textFontBold;
        layer.Italic = _textFontItalic;
        layer.MaxWidth = maxW;
        layer.Padding = pad;
        layer.TextColor = editing?.TextColor ?? layer.TextColor;
        layer.BackgroundColor = editing?.BackgroundColor ?? layer.BackgroundColor;
        AnnotationRasterizer.ApplyMetrics(layer);

        if (editing is null) _annoManager.AddLayer(layer);
        else _annoManager.UpdateLayer(editing.Id, layer);
        _annoTextSlotsDirty = true;
        LogService.Info("HdrCapture",
            $"文字{(editing is null ? "新建" : "再编辑")} {layer.CachedWidth:0}×{layer.CachedHeight:0} " +
            $"@{layer.X:0},{layer.Y:0} [{layer.FontFamily} {layer.FontSize:0}px{(layer.Bold ? " B" : "")}{(layer.Italic ? " I" : "")}]",
            LogCategory.UI);
        RequestRedraw();
    }

    private void CancelTextEdit()
    {
        DestroyEdit();
        RequestRedraw();
    }

    private void DestroyEdit()
    {
        if (_editHwnd != 0)
        {
            // 销毁前还原原始 WndProc 并清零（配合 CreateEdit 中"每次都子类化"的修复）
            if (s_editOrigProc != 0)
            {
                SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, s_editOrigProc);
                s_editOrigProc = 0;
            }
            DestroyWindow(_editHwnd);
            _editHwnd = 0;
        }
        if (_editFont != 0) { DeleteObject(_editFont); _editFont = 0; }
        _editingLayer = null;
        SyncEditPreview(); // _editHwnd 已清零 ⇒ 走"清预览"分支（排队给渲染线程）
    }

    private void TouchIdle()
    {
        _idleWatch.Restart();
        // ═══ 2026-09-15 修复：硬看门狗必须随用户活跃一起重置 ═══
        // 同 SelectionOverlay.TouchIdle：原实现只在启动时挂一次 3 分钟定时器，
        // 之后无论用户是否在标注，到点一律强制退出（甚至 Environment.Exit(0) 杀进程）。
        // 用户精心标注到第 3 分钟会全部丢失。正确语义是"最后一次交互后 3 分钟"。
        _hardTimer?.Change(HardExitMs, Timeout.Infinite);
    }

    // ═══════════════════════════════════════
    //  窗口检测（QQ截图式, 定时刷新）
    // ═══════════════════════════════════════

    /// <summary>后台检测窗口区域 + 每 3 秒定时刷新（响应新开/关闭/移动窗口）。</summary>
    private void StartRegionDetection()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _regions = RegionDetector.DetectWindows(_winX, _winY, _winW, _winH, new HashSet<nint> { _hwnd });
                LogService.Info("HdrCapture", $"窗口检测完成: {_regions.Count} 个", LogCategory.Capture);
                if (_hoverRegion >= _regions.Count) _hoverRegion = -1;
            }
            catch (Exception ex)
            {
                LogService.Error("HdrCapture", $"窗口检测失败: {ex.Message}", LogCategory.Capture);
            }
        });

        // 定时刷新（每 3 秒重新检测, 窗口变化实时感知）
        _regionRefreshTimer?.Dispose();
        _regionRefreshTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Interlocked.Increment(ref _mRegionTicks);   // F4 仪表：静置时每 3s 到底醒了几次
                long tDetect = PerfMeter.Now();
                var regions = RegionDetector.DetectWindows(_winX, _winY, _winW, _winH, new HashSet<nint> { _hwnd });
                long detectUs = PerfMeter.SinceMicro(tDetect);
                PerfMeter.Record(ref _mRegionDetectUs, ref _mRegionDetectMaxUs, detectUs);
                _regions = regions;
                if (_hoverRegion >= regions.Count) _hoverRegion = -1;
                if (!_selComplete && !_down)
                {
                    // 静置唤醒的账：先立记号再发信号 ⇒ 渲染线程下一次 RenderCore 归到"静置那一帧"的桶里。
                    // 顺序不能反：反过来会有极小概率把用户拖动引出的一帧记成静置帧（判据是"谁发的信号"，不是时间差）。
                    Volatile.Write(ref _mIdleWakePending, 1);
                    RequestRedraw(); // 悬停高亮可能变化
                    Interlocked.Increment(ref _mRegionRedraws);
                    if (_closing || _disposed) Interlocked.Increment(ref _mRegionRedrawsAfterClose);
                }
            }
            catch { }
        }, null, 3000, 3000);
    }

    /// <summary>悬停窗口标题变化 → 强制重建图集（复用 EnsureTextAtlas 构建, 标题渲染进槽 26）。</summary>
    /// <remarks>2026-08-11: 原 CopySubresourceRegion 局部更新导致 GPU DeviceRemoved 崩溃, 改整图集重建 (hover 切换 ~5ms 可接受)。</remarks>
    private void EnsureHoverTitle(string? title)
    {
        title ??= "";
        if (_textAtlas is null || title == _hoverTitle) return;
        _hoverTitle = title;
        _atlasScale = -1;      // 强制重建
        _annoTextSlotsDirty = true;
        Interlocked.Increment(ref _mHoverTitleSwitches);   // F4 仪表：每一次都会逼整张图集在下一帧重排
    }

    /// <summary>F4 仪表结算：把这扇窗的逐帧/静置账打成一行（只诊断档、每扇窗一次）。
    /// 落点选在渲染线程已经停下之后（StopRenderThread 末尾），否则读到的是一半的账。</summary>
    private void DumpPreviewMeters(string reason)
    {
        if (!PerfMeter.DumpEnabled) return;
        if (Interlocked.CompareExchange(ref _mDumped, 1, 0) != 0) return;
        long frames = Interlocked.Read(ref _mFrames);
        long lifeUs = PerfMeter.SinceMicro(_mOpenedAtQpc);
        LogService.Debug("HdrCapture",
            $"[Meter/F4] 预览窗 {_winW}x{_winH}（{reason}）: 存活 {lifeUs / 1_000_000.0:F1}s"
            + $"，渲染 {frames} 帧（合计 {PerfMeter.Ms(Interlocked.Read(ref _mFrameUs))}"
            + $"，均 {(frames == 0 ? 0 : Interlocked.Read(ref _mFrameUs) / frames / 1000.0):F2}ms/帧"
            + $"，峰 {PerfMeter.Ms(Interlocked.Read(ref _mFrameMaxUs))}）"
            + $"≈{(lifeUs == 0 ? 0 : frames * 1_000_000.0 / lifeUs):F2} 帧/s"
            + $"｜RTV 真重建 {Interlocked.Read(ref _mRtvRebuilds)} 次（帧数={frames}：两者相等就坐实「每帧重建」，差一大截就是只在换缓冲时才建）"
            + $"｜3s 静置定时器醒 {Interlocked.Read(ref _mRegionTicks)} 次"
            + $"（EnumWindows 合计 {PerfMeter.Ms(Interlocked.Read(ref _mRegionDetectUs))}"
            + $" 峰 {PerfMeter.Ms(Interlocked.Read(ref _mRegionDetectMaxUs))}），"
            + $"其中发过 redraw {Interlocked.Read(ref _mRegionRedraws)} 次"
            + $"⇒ 静置帧 {Interlocked.Read(ref _mIdleWakeFrames)} 帧"
            + $"（合计 {PerfMeter.Ms(Interlocked.Read(ref _mIdleWakeUs))}"
            + $" 峰 {PerfMeter.Ms(Interlocked.Read(ref _mIdleWakeMaxUs))}）"
            + $"｜图集重建 {Interlocked.Read(ref _mAtlasRebuilds)} 次"
            + $"（合计 {PerfMeter.Ms(Interlocked.Read(ref _mAtlasUs))} 峰 {PerfMeter.Ms(Interlocked.Read(ref _mAtlasMaxUs))}），"
            + $"由悬停标题逼出来的 {Interlocked.Read(ref _mHoverTitleSwitches)} 次"
            + $"｜关闭后仍被定时器唤醒 {Interlocked.Read(ref _mRegionRedrawsAfterClose)} 次（应为 0）",
            LogCategory.Capture);
    }

    public void Close()
    {
        LogService.Info("HdrCapture", $"窗口关闭 (win={_winW}x{_winH})", LogCategory.Capture);
        DestroyEdit();
        _autoTimer?.Dispose(); _autoTimer = null;
        _hardTimer?.Dispose(); _hardTimer = null;
        _regionRefreshTimer?.Dispose(); _regionRefreshTimer = null;
        // ═══ 2026-08-16(复审 P1-2): Close 置 _closing, 渲染循环立刻感知 ═══
        // 旧实现 Close 不置任何标志 → 渲染线程在 _swapChain 已 Dispose 后仍空转
        // 直到 MainWindow 的 using 作用域结束 (Dispose)。置位后渲染循环退出,
        // 不再对已释放资源操作。
        _closing = true;
        _renderSignal.Set(); // 唤醒渲染线程检查退出
        StopRenderThread(); // 先停渲染线程，再销毁窗口/释放资源
        DumpPreviewMeters("Close");   // F4 仪表：渲染线程已停 ⇒ 账是齐的（定时器在上面已 Dispose）
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
        IsInitialized = false;
    }

    private void StopRenderThread()
    {
        _renderSignal.Set(); // 唤醒渲染线程退出
        if (_renderThread is { IsAlive: true } && _renderThread != Thread.CurrentThread)
        {
            // ═══ 2026-09-15 修复：Join 超时后【绝不能】继续释放 D3D 资源 ═══
            // Join 失败 = 渲染线程仍卡在某个 D3D 调用里。典型场景正是**设备丢失**：
            // Present / Map 在已移除的设备上挂起。原实现对此毫无处理 ——
            // Cleanup() 随后照常 Dispose 交换链、纹理、设备，于是该线程的下一步操作
            // 就是 use-after-free ⇒ AccessViolationException。
            // 而 .NET 上 AV 属 Corrupted State Exception，**try/catch 拦不住，进程直接死**
            // （本项目在 OCR 的 AsTensor 生命周期上已经踩过完全相同的坑）。
            // 取舍：宁可泄漏这几个 D3D 对象（随进程退出回收），也不能让整个进程崩溃。
            if (!_renderThread.Join(2000))
            {
                _renderThreadStuck = true;
                LogService.Error("HdrCapture",
                    "渲染线程 2s 内未退出（可能卡在 D3D 调用，典型如设备丢失）— 跳过资源释放以避免进程崩溃",
                    LogCategory.Capture);
            }
        }
        _renderThread = null;
    }

    /// <summary>外部强制取消（应用退出时由 MainWindow 调用，触发 ActionCompleted + 关闭窗口）。</summary>
    public void RequestCancel()
    {
        if (_hwnd == 0 || _disposed) return; // 已关闭/已销毁，防止重复触发
        FireAction(MainToolbarAction.Cancel, 0, 0, 0, 0);
        Close();
    }

    private void Cleanup()
    {
        _autoTimer?.Dispose(); _autoTimer = null;
        _hardTimer?.Dispose(); _hardTimer = null;
        _regionRefreshTimer?.Dispose(); _regionRefreshTimer = null;
        _closing = true; // ═══ 2026-08-16 P1-2: 清理前让渲染循环退出 ═══
        StopRenderThread();

        // 渲染线程卡住（详见 StopRenderThread）：只清理与 D3D 无关的资源，
        // 避免该线程访问已被释放的 COM 对象导致进程崩溃。
        if (_renderThreadStuck)
        {
            LogService.Error("HdrCapture", "渲染线程仍在运行，跳过 D3D 资源释放（将随进程退出回收）", LogCategory.Capture);
            if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
            return;
        }

        _overlayPs?.Dispose(); _overlayPs = null;
        _uiCb?.Dispose(); _uiCb = null;
        _rsNoCull?.Dispose(); _rsNoCull = null;
        _textAtlasSrv?.Dispose(); _textAtlasSrv = null;
        _textAtlas?.Dispose(); _textAtlas = null;
        _badgeSrv?.Dispose(); _badgeSrv = null;
        _badgeTex?.Dispose(); _badgeTex = null;
        _desktopSrv?.Dispose(); _desktopSrv = null;
        _desktopTex?.Dispose(); _desktopTex = null;
        _sharedTexOpen = null;
        _pooledStaging?.Dispose(); _pooledStaging = null;
        _rtv?.Dispose(); _rtv = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        _swapChain?.Dispose(); _swapChain = null;
        _vs?.Dispose(); _vs = null;
        // _ps 已删除（死代码）
        _samp?.Dispose(); _samp = null;
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        // 路线 B：交还共享槽排在渲染线程停下之后（Cleanup 里已 Join），且只发一次。
        // 早交还 ⇒ 下一帧就会覆写用户正看着的画面；漏交还 ⇒ 槽一直冻着（靠取槽轮换兜底，但那是在浪费一张图）。
        ReleaseSharedSource();
        // ═══ 2026-08-16(复审 P1-1): 释放本窗自己建的 D3D11 设备+上下文 ═══
        // 旧实现 Dispose 从不释放 → 每次 HDR 截图泄漏一整套 D3D11 设备/上下文/显存。
        // Cleanup() 已先停渲染线程, 此处安全释放。
        //
        // ═══ 2026-09-15: 但若渲染线程卡住（设备丢失时 Present/Map 挂起），
        //     释放设备会让该线程立刻 use-after-free ⇒ 不可捕获的 AV ⇒ 进程终止。
        //     这里同样跳过 —— 泄漏优于崩溃。
        if (_renderThreadStuck) return;
        // ═══ 阶段 4：共享设备归进程缓存所有 ═══
        // 这里只放手不 Dispose —— 否则下一扇窗复用到的是一块已释放的 COM 对象（立刻 use-after-free）。
        // 自建模式（TTC_PREVIEW_SHARED_DEVICE=0）保持 2026-08-16 的收法不变。
        if (!_ownsDevice) { _ctx = null!; _device = null!; return; }
        _ctx?.Dispose();
        _device?.Dispose();
    }
}
