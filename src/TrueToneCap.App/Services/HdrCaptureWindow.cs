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

namespace TrueToneCap.App.Services;

public enum HdrCaptureAction { Cancel, Save, Copy, Annotate, Ocr, Translate }

public sealed partial class HdrCaptureWindow : IDisposable
{
    private nint _hwnd;
    private int _winX, _winY, _winW, _winH;
    private volatile bool _disposed;
    private volatile bool _closing; // ═══ 2026-08-16 P1-2: Close 后渲染循环感知退出 ═══
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;

    // ═══ 2026-08-16 方案A: GSYNC/FreeSync (VRR) 支持 ═══
    // 检测到允许撕裂时交换链加 AllowTearing + Present(Tear), 让 DWM 在 VRR 面板
    // 刷新窗口内任意相位呈现 → 选区拖拽更平滑、输入延迟更低。不支持时回退 Present(0)。
    /// <summary>中心行亮度打点的一次性开关（0=未打点）。</summary>
    private int _diagSampleDone;

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
    private ID3D11SamplerState? _samp;
    private ID3D11Texture2D? _desktopTex;
    private ID3D11ShaderResourceView? _desktopSrv;
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
    private int _dragHandle = -1; // 当前拖拽的手柄 (0=TL, 1=TR, 2=BL, 3=BR, -1=无)
    private int _handleSize = 8; // 手柄尺寸（物理像素，随 DPI 缩放）
    private int _handleHit = 12; // 手柄命中区域（物理像素，随 DPI 缩放）
    private double _uiScale = 1.0; // DPI 缩放系数（GetDpiForWindow / 96）
    private bool _selComplete;
    private int _sx1, _sy1, _sx2, _sy2, _mx, _my;
    private bool _down, _moved;

    // ── 标注模式 (2026-08-11 A方案: GPU 内联标注) ──
    private bool _annoMode;                      // 是否处于标注模式
    private string _annoTool = "Rect";           // 当前标注工具
    private bool _annoDown;                      // 标注拖拽中
    private System.Numerics.Vector2 _annoDragStart; // 标注拖拽起点 (选区图像坐标)
    private readonly List<System.Numerics.Vector2> _penPoints = []; // 画笔轨迹
    private readonly AnnotationManager _annoManager = new();       // 标注图层管理 (撤销/重做)
    private nint _editHwnd;                      // 文字输入 EDIT 子窗口
    private System.Numerics.Vector2 _textInsertPos; // 文字插入位置 (选区图像坐标)
    private bool _annoTextSlotsDirty;            // 文字槽图集需要重建
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

    // 自动超时关闭
    private System.Threading.Timer? _autoTimer;
    private const int AutoTimeoutMs = 60_000; // 1 分钟无操作自动关闭
    // ── 硬看门狗：3 分钟无论什么情况强制退出（UI 卡死兜底）──
    private System.Threading.Timer? _hardTimer;
    private const int HardExitMs = 3 * 60 * 1000;
    private System.Diagnostics.Stopwatch _idleWatch = System.Diagnostics.Stopwatch.StartNew();

    public event Action<HdrCaptureAction, int, int, int, int>? ActionCompleted;
    public bool IsInitialized { get; private set; }
    public string? LastError { get; private set; }

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
    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint h);
    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint p, nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint h, char[] sb, int max);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW { public uint style; public nint proc; public int cbCls; public int cbWnd; public nint inst; public nint icon; public nint cur; public nint bg; public string? menu; public string? cls; }
    // ⚠ 必须与 Win32 PAINTSTRUCT 完全同构（x64 下 72 字节）。
    // 之前只声明了前 3 个字段（32 字节），BeginPaint 会写满整个结构
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
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }
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
    private const uint WM_CLOSE = 0x0010; // Alt+F4 / 系统关闭
    private const uint WM_APP_TIMEOUT = 0x8000 + 1; // 超时消息（线程池 Timer → 窗口消息，由 UI 线程处理）
    private const int VK_ESC = 0x1B, VK_ENT = 0x0D, VK_S = 0x53, VK_C = 0x43;
    private const int VK_Z = 0x5A, VK_Y = 0x59;
    // EDIT 子窗口常量
    private const uint WS_CHILD = 0x40000000, WS_BORDER = 0x00800000;
    private const uint ES_AUTOHSCROLL = 0x0080;
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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
        public float[] TextSubs;  // 40 槽 × 4 (0-6 主, 7-17 标注, 18-33 文字)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
        public float[] TextPos;   // 40 槽 × 4
        public System.Numerics.Vector4 AnnoState;     // (mode, tool, down, 0)
        public System.Numerics.Vector4 AnnoColor;     // 标注颜色 (线性 RGBA)
        public System.Numerics.Vector4 AnnoStroke;    // (strokeW, opacity, 0, 0)
        public System.Numerics.Vector4 HoverRect;     // 悬停窗口矩形 (x,y,w,h); w<=0 无悬停
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
        public float[] AnnoLayers; // 256 槽 × 4 (16 层 × 16 vec)
    }

    private static void SetV4(float[] arr, int idx, System.Numerics.Vector4 v)
    {
        int o = idx * 4;
        arr[o] = v.X; arr[o + 1] = v.Y; arr[o + 2] = v.Z; arr[o + 3] = v.W;
    }

    /// <summary>独立 D3D11 设备：渲染线程独占 context，不与 WGC 共享设备竞争。</summary>
    public HdrCaptureWindow()
    {
        _device = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _ctx = _device.ImmediateContext;
    }

    public bool Initialize(int x, int y, int w, int h)
    {
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
        // ═══ 2026-08-26 关键修复: AlphaMode.Ignore → Premultiplied ═══
        // 窗口为 WS_EX_NOREDIRECTIONBITMAP (DWM 直接合成交换链)。此模式下 DWM 按
        // 后台缓冲 alpha 与桌面混合 — Ignore 使 DWM 将窗口视为不透明, 但 Flip 模型
        // 合成路径实际按预乘 alpha 处理; PS 输出 alpha=1 的区域正常, 而未写入区域
        // (alpha=0) 透出实时桌面 → 表现为"覆盖层透明/只有桌面"。
        // Premultiplied + PS 输出 alpha=1 (OverlayComposite 已 return float4(rgb,1))
        // → DWM 完全不透明合成, 修复透明问题。
        var desc = new SwapChainDescription1 { Width = (uint)w, Height = (uint)h, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore, Flags = SwapChainFlags.None };
            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(_backBuffer);

            string sd = Path.Combine(AppContext.BaseDirectory, "data", "Shaders");
            // Debug 下 bin 直出目录是 Shaders/ (csproj Content LinkBase 未生效), 双路径回退
            string sdAlt = Path.Combine(AppContext.BaseDirectory, "Shaders");
            static string? FindCso(string dir, string name) =>
                File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;
            string? vsp = FindCso(sd, "FullscreenVS.hlsl.cso") ?? FindCso(sdAlt, "FullscreenVS.hlsl.cso");
            if (vsp is not null) _vs = _device.CreateVertexShader(File.ReadAllBytes(vsp), null);
            // 使用 CopyTexture.hlsl（直通拷贝，不做色调映射）
            string? psp = FindCso(sd, "CopyTexture.hlsl.cso") ?? FindCso(sdAlt, "CopyTexture.hlsl.cso")
                ?? FindCso(sd, "ToneMapping.hlsl.cso") ?? FindCso(sdAlt, "ToneMapping.hlsl.cso"); // 回退
            if (psp is not null) _ps = _device.CreatePixelShader(File.ReadAllBytes(psp));

            // ── Phase 2 (2026-08-11): GPU 合成着色器 + 常量缓冲（失败自动回退 CPU CompositeUI）──
            try
            {
                string? osp = FindCso(sd, "OverlayComposite.hlsl.cso") ?? FindCso(sdAlt, "OverlayComposite.hlsl.cso");
                if (osp is not null && _vs is not null)
                {
                    _overlayPs = _device.CreatePixelShader(File.ReadAllBytes(osp));
                    // ═══ 2026-08-26 根因修复: 常量缓冲大小必须 ≥ HLSL cbuffer 声明大小 ═══
                    // 旧: ByteWidth = Marshal.SizeOf<UiCbData>() = 4624
                    // HLSL 需要: 9×vec4(144) + TextSubs[40]×16(640) + TextPos[40]×16(640)
                    //           + 4×vec4(64) + AnnoLayers[256]×16(4096) = 5584
                    // D3D11 规则: ByteWidth < 着色器声明 → Draw 静默失败/无输出
                    //   (桌面帧仍显示因为走 CopyResource 不依赖 cbuffer; UI 层全灭)
                    // 硬编码 5584 而非依赖 Marshal.SizeOf (ByValArray float[] 封送大小不可靠)
                    const uint UiCbByteWidth = 5584;
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
                System.Diagnostics.Debug.WriteLine($"[HC] GPU 合成初始化失败，回退 CPU: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[HC] 预创建纹理失败 (首帧将惰性创建): {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("[HC] 硬看门狗触发: 3 分钟强制退出");
                // 先投递超时消息尝试优雅关闭（UI 活着时正常走 FireAction(Cancel)+Close）
                try { if (_hwnd != 0) PostMessageW(_hwnd, WM_APP_TIMEOUT, 0, 0); } catch { }
                // 给 UI 线程 5 秒处理时间；仍未响应说明 UI 卡死 → 强制退出进程
                Thread.Sleep(5000);
                if (!_actionFired && _hwnd != 0)
                {
                    System.Diagnostics.Debug.WriteLine("[HC] UI 无响应，强制退出进程");
                    try { LogService.Error("HdrCapture", "硬看门狗: UI 无响应，强制退出"); } catch { }
                    Environment.Exit(0);
                }
            }, null, HardExitMs, Timeout.Infinite);

            // 后台检测窗口区域（QQ截图式快速选择）
            StartRegionDetection();

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
                RenderCore();
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
        try
        {
            // 2026-08-11 修复: FlipSequential 交换链 Present 后后台缓冲翻转,
            // 必须每次渲染前重新 GetBuffer(0) + 重建 RTV — 固定引用导致绘制到过期缓冲 → 黑屏
            EnsureBackBuffer();
            if (_backBuffer is null || _rtv is null) return;

            // ═══ Phase 2 (2026-08-11): GPU 合成路径 — 桌面帧纹理 + OverlayComposite PS 直接画 UI ═══
            // CPU 路径（CompositeUI 全屏混合）仅作回退。收益: 拖拽合成 ~20-30ms → GPU <2ms
            var frame = Volatile.Read(ref _frame);
            if (_useGpuCompositor && frame is not null && _overlayPs is not null && _uiCb is not null)
            {
                // 首次渲染时上传桌面帧到 _desktopTex（帧内容不变，只传一次）
                if (!_hasFrame)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    UploadFrame(frame.Pixels, frame.W, frame.H);
                    LogService.Info("HdrCapture", $"首帧 UploadFrame: {sw.ElapsedMilliseconds}ms", LogCategory.Capture);
                }
                if (_desktopTex is not null)
                {
                    // ═══ 2026-08-26 关键修复: GPU 合成分支禁止 CopyResource ═══
                    // RenderGpuComposite 已用 OverlayComposite PS 把 UI 绘到后台缓冲。
                    // 之前的 CopyResource(_backBuffer, _desktopTex) 用桌面纹理覆盖了后台缓冲
                    // → UI 被桌面擦掉 → 用户看到"直接显示桌面"无任何覆盖层。
                    RenderGpuComposite();
                    PresentFrame();
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
        catch (Exception ex) { LogService.Error("HdrCapture", $"RenderCore 异常: {ex.Message}"); }
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
        // 2026-08-11: IASetInputLayout(null) 与 GpuToneMapper 对齐 (残留 InputLayout 导致 Draw 无输出)
        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_overlayPs!);
        _ctx.VSSetConstantBuffer(0, _uiCb!);
        _ctx.PSSetConstantBuffer(0, _uiCb!);
        _ctx.PSSetShaderResource(0, _desktopSrv!);
        _ctx.PSSetShaderResource(1, _textAtlasSrv ?? _desktopSrv!); // 图集缺失时兜底绑定桌面（无文字）
        _ctx.PSSetSampler(0, _samp!);
        _ctx.Draw(3, 0);
    }

    private System.Numerics.Vector4[] _atlasSubs = new System.Numerics.Vector4[40];

    /// <summary>
    /// 确保工具栏文字图集存在（按 _uiScale 构建）。
    /// 布局 2048×384: 行0 = 主工具栏 7 项 (槽 0-6), 行1 = 标注工具栏 11 项 (槽 7-17),
    /// 行2 = 文字槽 16 个 × 256×64 (槽 18-33)。
    /// </summary>
    private void EnsureTextAtlas()
    {
        if (_textAtlas is not null && Math.Abs(_atlasScale - _uiScale) < 0.01 && !_annoTextSlotsDirty)
            return;

        try
        {
            _textAtlasSrv?.Dispose(); _textAtlasSrv = null;
            _textAtlas?.Dispose(); _textAtlas = null;

            // 布局: 2048×384 图集
            const int atlasW = 2048, atlasH = 384;
            const int row1Y = 128, row2Y = 256;
            var mainItems = new (string text, int w, int h)[]
            {
                ("\u2611 保存", 0, 0), ("\u270E 标注", 0, 0), ("\u2610 复制", 0, 0),
                ("\u25C9 识字", 0, 0), ("\u2605 翻译", 0, 0), ("\u2715 取消", 0, 0),
                ("保存截图到文件", 0, 0),
            };
            var annoItems = new (string text, int w, int h)[]
            {
                ("\u25AD 矩形", 0, 0), ("\u25EF 椭圆", 0, 0), ("\u2197 箭头", 0, 0),
                ("\u270F 画笔", 0, 0), ("A 文字", 0, 0), ("\u25A6 马赛克", 0, 0),
                ("\u21B6 撤销", 0, 0), ("\u21B7 重做", 0, 0), ("\u2713 完成", 0, 0),
                ("\u2715 取消", 0, 0), ("拖拽绘制标注", 0, 0),
            };
            var lay = GetToolbarLayout(6);
            int btnW = lay.BtnW, btnH = lay.BtnH;
            int hintW = Math.Max(120, (int)Math.Round(160 * _uiScale));
            int hintH = Math.Max(18, (int)Math.Round(22 * _uiScale));
            for (int i = 0; i < 6; i++) { mainItems[i].w = btnW; mainItems[i].h = btnH; }
            mainItems[6].w = hintW; mainItems[6].h = hintH;
            for (int i = 0; i < 10; i++) { annoItems[i].w = btnW; annoItems[i].h = btnH; }
            annoItems[10].w = hintW; annoItems[10].h = hintH;

            float fontSize = 11f * (float)_uiScale;
            var atlasBytes = new byte[atlasW * atlasH * 4];
            var subs = new System.Numerics.Vector4[40];
            using (var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice())
            {
                // 行 0: 主工具栏 7 项 (槽 0-6)
                int cursorX = 0;
                for (int i = 0; i < mainItems.Length; i++)
                {
                    var bmp = RenderTextBitmap(device, mainItems[i].text, mainItems[i].w, mainItems[i].h, fontSize);
                    if (bmp is null) continue;
                    int dstX = cursorX;
                    subs[i] = new System.Numerics.Vector4(dstX / (float)atlasW, 0, mainItems[i].w / (float)atlasW, mainItems[i].h / (float)atlasH);
                    for (int row = 0; row < mainItems[i].h; row++)
                        Buffer.BlockCopy(bmp, row * mainItems[i].w * 4, atlasBytes, (row * atlasW + dstX) * 4, mainItems[i].w * 4);
                    cursorX += mainItems[i].w + 8;
                }
                // 行 1: 标注工具栏 11 项 (槽 7-17)
                cursorX = 0;
                for (int i = 0; i < annoItems.Length; i++)
                {
                    var bmp = RenderTextBitmap(device, annoItems[i].text, annoItems[i].w, annoItems[i].h, fontSize);
                    if (bmp is null) continue;
                    int dstX = cursorX;
                    subs[7 + i] = new System.Numerics.Vector4(dstX / (float)atlasW, row1Y / (float)atlasH, annoItems[i].w / (float)atlasW, annoItems[i].h / (float)atlasH);
                    for (int row = 0; row < annoItems[i].h; row++)
                        Buffer.BlockCopy(bmp, row * annoItems[i].w * 4, atlasBytes, ((row1Y + row) * atlasW + dstX) * 4, annoItems[i].w * 4);
                    cursorX += annoItems[i].w + 8;
                }
                // 行 2: 文字槽 8 个 × 256×64 (槽 18-25) — 每个 256×64 固定 (2048/256=8 个, 超限不显示但输出合成仍正确)
                const int slotW = 256, slotH = 64;
                int slot = 0;
                // ═══ 2026-08-16 P1-3: 快照遍历 (渲染线程与 UI 线程修改互斥) ═══
                foreach (var layer in _annoManager.GetLayersSnapshot().Where(l => l is TextLayer && l.IsVisible))
                {
                    if (slot >= 8) break;
                    var tl = (TextLayer)layer;
                    int dstX = slot * slotW;
                    subs[18 + slot] = new System.Numerics.Vector4(dstX / (float)atlasW, row2Y / (float)atlasH, slotW / (float)atlasW, slotH / (float)atlasH);
                    float tfs = Math.Clamp(tl.FontSize, 10f, 48f) * (float)_uiScale;
                    var bmp = RenderTextBitmap(device, tl.Text, slotW, slotH, tfs);
                    if (bmp is not null)
                    {
                        for (int row = 0; row < slotH; row++)
                            Buffer.BlockCopy(bmp, row * slotW * 4, atlasBytes, ((row2Y + row) * atlasW + dstX) * 4, slotW * 4);
                    }
                    slot++;
                }
                // 行 3: 悬停窗口标题槽 (槽 26, 512×64) — QQ截图式窗口标题提示
                {
                    const int tipW = 512, tipH = 64, row3Y = 320;
                    subs[26] = new System.Numerics.Vector4(0, row3Y / (float)atlasH, tipW / (float)atlasW, tipH / (float)atlasH);
                    string hoverTitle = _hoverTitle ?? "";
                    if (hoverTitle.Length > 0)
                    {
                        float tfs2 = 12f * (float)_uiScale;
                        var bmp = RenderTextBitmap(device, hoverTitle, tipW, tipH, tfs2);
                        if (bmp is not null)
                        {
                            for (int row = 0; row < tipH; row++)
                                Buffer.BlockCopy(bmp, row * tipW * 4, atlasBytes, ((row3Y + row) * atlasW + 0) * 4, tipW * 4);
                        }
                    }
                }
            }
            _atlasScale = _uiScale;
            _annoTextSlotsDirty = false;
            _textAtlas = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = atlasW, Height = atlasH, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
            });
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
            _textAtlasSrv = _device.CreateShaderResourceView(_textAtlas);
            _atlasSubs = subs;
        }
        catch (Exception ex)
        {
            LogService.Error("HdrCapture", $"文字图集构建失败（GPU 合成降级）: {ex.Message}");
            _textAtlasSrv?.Dispose(); _textAtlasSrv = null;
            _textAtlas?.Dispose(); _textAtlas = null;
        }
    }

    /// <summary>更新 GPU 合成常量缓冲（与 CompositeUI 的绘制逻辑一致 + 标注层数据）。</summary>
    private void UpdateUiConstantBuffer()
    {
        var cb = new UiCbData
        {
            TextSubs = new float[160],
            TextPos = new float[160],
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

        // 工具栏布局（与 GetToolbarLayout / Down() 命中一致; 标注模式 10 按钮 槽 7-17）
        bool isDark = App.ResolveEffectiveTheme(App.CurrentTheme) is AppThemeMode.Dark or AppThemeMode.OLED;
        if (_selComplete)
        {
            int n = _annoMode ? 10 : 6;
            int subBase = _annoMode ? 7 : 0;
            var lay = GetToolbarLayout(n);
            int selX1 = Math.Min(_sx1, _sx2), selX2 = Math.Max(_sx1, _sx2);
            int selY1 = Math.Min(_sy1, _sy2), selY2 = Math.Max(_sy1, _sy2);
            int totalW = lay.TotalW, totalH = lay.TotalH;
            int tx = Math.Clamp(selX1 + (selX2 - selX1) / 2 - totalW / 2, 4, w - totalW - 4);
            int ty = selY2 + 10;
            if (ty + totalH + 10 > h) ty = selY1 - totalH - 12;
            if (ty < 0) ty = 4;
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
            // 悬停提示位置（工具栏下方/上方）
            int hintW = Math.Max(120, (int)Math.Round(160 * _uiScale));
            int hintH = Math.Max(18, (int)Math.Round(22 * _uiScale));
            int hx = Math.Clamp(tx + totalW / 2 - hintW / 2, 4, w - hintW - 4);
            int hy = ty + totalH + 4;
            if (hy + hintH + 4 > h) hy = ty - hintH - 4;
            SetV4(cb.TextPos, subBase + n, new System.Numerics.Vector4(hx, hy, hintW, hintH));
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
            SetV4(cb.TextPos, 26, new System.Numerics.Vector4(tix, tiy, tipW, tipH));
        }
        else
        {
            cb.HoverRect = System.Numerics.Vector4.Zero;
        }

        // 图集子矩形（40 槽）
        for (int i = 0; i < 40 && i < _atlasSubs.Length; i++)
            SetV4(cb.TextSubs, i, _atlasSubs[i]);

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
                    Buffer.MemoryCopy(ts, p + 144, 640, 640);
                fixed (float* tp = cb.TextPos)
                    Buffer.MemoryCopy(tp, p + 784, 640, 640);
                *(System.Numerics.Vector4*)(p + 1424) = cb.AnnoState;
                *(System.Numerics.Vector4*)(p + 1440) = cb.AnnoColor;
                *(System.Numerics.Vector4*)(p + 1456) = cb.AnnoStroke;
                *(System.Numerics.Vector4*)(p + 1472) = cb.HoverRect;
                fixed (float* al = cb.AnnoLayers)
                    Buffer.MemoryCopy(al, p + 1488, 4096, 4096);
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

    private static int AnnoToolIndex() => 0; // 工具索引未用 (shader 仅按层类型绘制)

    private void WriteAnnoLayer(ref UiCbData cb, int idx, AnnotationLayer layer)
    {
        int o = idx * 16;
        float type = layer.Type switch
        {
            ShapeType.Rectangle => 1, ShapeType.Ellipse => 2, ShapeType.Arrow => 3,
            ShapeType.Freehand => 4, ShapeType.Text => 5, ShapeType.Mosaic => 6,
            _ => 1
        };
        var b = layer.GetBounds();
        SetV4(cb.AnnoLayers, o + 0, new System.Numerics.Vector4(type, layer.Opacity, 2f, 0));
        SetV4(cb.AnnoLayers, o + 1, new System.Numerics.Vector4(b.Left, b.Top, b.Right, b.Bottom));
        SetV4(cb.AnnoLayers, o + 2, new System.Numerics.Vector4(1f, 0f, 0f, 1f)); // 红色

        float param = 0, slot = 0;
        switch (layer)
        {
            case ArrowLayer al: param = al.ArrowHeadSize; break;
            case MosaicLayer m: param = m.BlockSize; break;
            case TextLayer tl: param = tl.FontSize; slot = TextSlotOf(tl); break;
        }
        SetV4(cb.AnnoLayers, o + 3, new System.Numerics.Vector4(param, slot, 0, 0));

        if (layer is FreehandLayer fl)
        {
            var pts = SimplifyPolyline(fl.Points, 13);
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
        float type = _annoTool switch
        {
            "Rect" => 1, "Ellipse" => 2, "Arrow" => 3, "Pen" => 4, "Text" => 5, "Mosaic" => 6,
            _ => 1
        };
        float x1 = Math.Min(start.X, end.X), y1 = Math.Min(start.Y, end.Y);
        float x2 = Math.Max(start.X, end.X), y2 = Math.Max(start.Y, end.Y);
        SetV4(cb.AnnoLayers, o + 0, new System.Numerics.Vector4(type, 1f, 2f, 0));
        SetV4(cb.AnnoLayers, o + 1, new System.Numerics.Vector4(x1, y1, x2, y2));
        SetV4(cb.AnnoLayers, o + 2, new System.Numerics.Vector4(1f, 0f, 0f, 1f));
        SetV4(cb.AnnoLayers, o + 3, new System.Numerics.Vector4(type == 3 ? 12f : (type == 6 ? 10f : 0f), 0, 0, 0));
        if (type == 4)
        {
            var pts = SimplifyPolyline(_penPoints, 13);
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

    /// <summary>文字层在图集文字槽中的序号（与 EnsureTextAtlas 渲染顺序一致）。</summary>
    private float TextSlotOf(TextLayer tl)
    {
        int slot = 0;
        // ═══ 2026-08-16 P1-3: 快照遍历 ═══
        foreach (var layer in _annoManager.GetLayersSnapshot())
        {
            if (layer is not TextLayer t) continue;
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
            var items = new (string icon, string text, string hint)[]
            {
                ("\u2611", "保存", "保存截图到文件"),
                ("\u270E", "标注", "在截图上标注"),
                ("\u2610", "复制", "复制到剪贴板"),
                ("\u25C9", "识字", "识别图中文字"),
                ("\u2605", "翻译", "翻译图中文字"),
                ("\u2715", "取消", "取消截图"),
            };

            // 工具栏 — QQ 风格工具栏（每个按钮独立显示，图标+文字并排，悬停提示功能）
            // 尺寸随 DPI 缩放（绘制与 Down() 命中检测共用 GetToolbarLayout 保证一致）
            int n = items.Length;
            var lay = GetToolbarLayout(n);
            int btnW = lay.BtnW, btnH = lay.BtnH, gap = lay.Gap;
            int padX = lay.PadX, padY = lay.PadY;
            int totalW = lay.TotalW, totalH = lay.TotalH;

            int selX1 = Math.Min(_sx1, _sx2), selX2 = Math.Max(_sx1, _sx2);
            int selY1 = Math.Min(_sy1, _sy2), selY2 = Math.Max(_sy1, _sy2);
            int tx = Math.Clamp(selX1 + (selX2 - selX1) / 2 - totalW / 2, 4, w - totalW - 4);
            int ty = selY2 + 10;
            if (ty + totalH + 10 > h) ty = selY1 - totalH - 12;
            if (ty < 0) ty = 4;

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
                int hintW = Math.Max(120, (int)Math.Round(160 * _uiScale));
                int hintH = Math.Max(18, (int)Math.Round(22 * _uiScale));
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
            // 1. Win2D 共享设备预热（首次调用慢，后续 GetSharedDevice 零成本）
            using var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();

            // 2. 工具栏按钮文字（内容固定，与 CompositeUI 中的 items 一致）
            var items = new (string icon, string text, string hint)[]
            {
                ("\u2611", "保存", "保存截图到文件"),
                ("\u270E", "标注", "在截图上标注"),
                ("\u2610", "复制", "复制到剪贴板"),
                ("\u25C9", "识字", "识别图中文字"),
                ("\u2605", "翻译", "翻译图中文字"),
                ("\u2715", "取消", "取消截图"),
            };
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                int s = (int)Math.Round(scale * 100);
                int btnW = Math.Max(60, 90 * s / 100);
                int btnH = Math.Max(24, 32 * s / 100);
                int hintW = Math.Max(120, (int)Math.Round(160 * scale));
                int hintH = Math.Max(18, (int)Math.Round(22 * scale));
                float fontSize = 11f * (float)scale;

                foreach (var it in items)
                {
                    // 按钮文字
                    var btnKey = (it.icon + " " + it.text, btnW, btnH);
                    if (!s_textCache.ContainsKey(btnKey))
                    {
                        var bmp = RenderTextBitmap(device, btnKey.Item1, btnW, btnH, fontSize);
                        if (bmp is not null) s_textCache[btnKey] = bmp;
                    }
                    // 悬停提示
                    var hintKey = (it.hint, hintW, hintH);
                    if (!s_textCache.ContainsKey(hintKey))
                    {
                        var bmp = RenderTextBitmap(device, hintKey.Item1, hintW, hintH, fontSize);
                        if (bmp is not null) s_textCache[hintKey] = bmp;
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine($"[HC] 启动预热完成: {s_textCache.Count} 个文字位图");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HC] 启动预热失败（非致命）: {ex.Message}");
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
                using var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                pixels = RenderTextBitmap(device, text, tw, th, 11f * (float)_uiScale);
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HC] DrawTextWin2D 失败: {ex.Message}");
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
            case WM_CLOSE: // Alt+F4 / 系统关闭 → 兜底触发 Cancel，保证 MainWindow 防重入锁必然释放
                FireAction(HdrCaptureAction.Cancel, 0, 0, 0, 0);
                Close();
                return 0;
            case WM_APP_TIMEOUT: // 3 分钟无操作超时（UI 线程安全关闭）
                FireAction(HdrCaptureAction.Cancel, 0, 0, 0, 0);
                Close();
                return 0;
        }
        return DefWindowProcW(h, m, w, l);
    }

    /// <summary>触发 ActionCompleted（幂等：只触发一次）。</summary>
    private void FireAction(HdrCaptureAction a, int x, int y, int w, int h)
    {
        if (_actionFired) return;
        _actionFired = true;
        ActionCompleted?.Invoke(a, x, y, w, h);
    }

    private void Down(int x, int y)
    {
        TouchIdle();
        _down = true; _moved = false; _dragHandle = -1;

        if (_annoMode)
        {
            // ── 标注模式：工具栏 / 选区内绘制 / 文字输入 ──
            var (hit, idx) = HitAnnoToolbar(x, y);
            if (hit) { _down = false; _moved = true; OnAnnoToolbarClick(idx); return; } // _moved=true 防 Up 重置选区

            int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
            int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
            if (x >= x1 && x <= x2 && y >= y1 && y <= y2)
            {
                if (_annoTool == "Text")
                {
                    _textInsertPos = ScreenToAnno(x, y);
                    _down = false;
                    ShowTextEdit(new System.Numerics.Vector2(x, y)); // 帧坐标
                    return;
                }
                _down = false; // 标注绘制不使用选区拖拽标志（shader 遮罩由选区外条件控制）
                _annoDown = true;
                _annoDragStart = ScreenToAnno(x, y);
                _penPoints.Clear();
                _penPoints.Add(_annoDragStart);
                SetCapture(_hwnd);
                RequestRedraw();
                return;
            }
            _down = false;
            return;
        }

        if (_selComplete)
        {
            // 1. 工具栏按钮点击检测（QQ风格）
            var items = new (string icon, string text, string hint)[]
            {
                ("\u2611", "保存", "保存截图到文件"),
                ("\u270E", "标注", "在截图上标注"),
                ("\u2610", "复制", "复制到剪贴板"),
                ("\u25C9", "识字", "识别图中文字"),
                ("\u2605", "翻译", "翻译图中文字"),
                ("\u2715", "取消", "取消截图"),
            };
            int n = items.Length;
            var lay = GetToolbarLayout(n);
            int btnW = lay.BtnW, btnH = lay.BtnH, gap = lay.Gap, padX = lay.PadX, padY = lay.PadY;
            int totalW = lay.TotalW, totalH = lay.TotalH;
            int selX1 = Math.Min(_sx1, _sx2), selX2 = Math.Max(_sx1, _sx2);
            int selY1 = Math.Min(_sy1, _sy2), selY2 = Math.Max(_sy1, _sy2);
            int tx = Math.Clamp(selX1 + (selX2 - selX1) / 2 - totalW / 2, 4, _winW - totalW - 4);
            int ty = selY2 + 10;
            if (ty + totalH + 10 > _winH) ty = selY1 - totalH - 12;
            if (ty < 0) ty = 4;
            for (int i = 0; i < n; i++)
            {
                int ix = tx + padX + i * (btnW + gap);
                int iy = ty + padY;
                if (x >= ix && x <= ix + btnW && y >= iy && y <= iy + btnH)
                {
                    var actions = new[] { HdrCaptureAction.Save, HdrCaptureAction.Annotate, HdrCaptureAction.Copy,
                        HdrCaptureAction.Ocr, HdrCaptureAction.Translate, HdrCaptureAction.Cancel };
                    _down = false;
                    _moved = true; // 防 Up 单击逻辑重置选区 (2026-08-11 bugfix)
                    DoAction(actions[i]);
                    return;
                }
            }

            // 2. 四角手柄拖拽检测（欧氏距离，避免对角区域误判）
            int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
            int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
            int hit = _handleHit;
            bool Near(int px, int py, int tx, int ty) => (px - tx) * (px - tx) + (py - ty) * (py - ty) <= hit * hit;
            if (Near(x, y, x1, y1)) { _dragHandle = 0; return; } // TL
            if (Near(x, y, x2, y1)) { _dragHandle = 1; return; } // TR
            if (Near(x, y, x1, y2)) { _dragHandle = 2; return; } // BL
            if (Near(x, y, x2, y2)) { _dragHandle = 3; return; } // BR

        }

        _sx1 = _sx2 = x; _sy1 = _sy2 = y;
    }

    private void Move(int x, int y)
    {
        TouchIdle();
        _mx = x; _my = y;

        if (_annoDown)
        {
            // 标注拖拽：画笔累积轨迹, 其他工具实时更新预览
            _moved = true;
            if (_annoTool == "Pen")
            {
                var ap = ScreenToAnno(x, y);
                var last = _penPoints[^1];
                if ((ap - last).Length() > 0.5f) _penPoints.Add(ap);
            }
            RequestRedraw();
            return;
        }

        if (_annoMode) { RequestRedraw(); return; } // 标注模式 hover 刷新工具栏高亮

        if (_down && _dragHandle >= 0)
        {
            // 拖拽手柄调整选区
            _moved = true;
            switch (_dragHandle)
            {
                case 0: _sx1 = x; _sy1 = y; break; // TL
                case 1: _sx2 = x; _sy1 = y; break; // TR
                case 2: _sx1 = x; _sy2 = y; break; // BL
                case 3: _sx2 = x; _sy2 = y; break; // BR
            }
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
        if (_annoDown)
        {
            // ── 标注绘制完成：提交图层 ──
            _annoDown = false;
            _down = false;
            ReleaseCapture();
            var end = ScreenToAnno(x, y);
            if (_annoTool == "Pen")
            {
                if (_penPoints.Count >= 2)
                {
                    _penPoints.Add(end);
                    _annoManager.AddLayer(new FreehandLayer { Points = [.. _penPoints] });
                }
                _penPoints.Clear();
            }
            else
            {
                var start = _annoDragStart;
                float x1 = Math.Min(start.X, end.X), y1 = Math.Min(start.Y, end.Y);
                float w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
                if (w >= 3 && h >= 3)
                {
                    AnnotationLayer layer = _annoTool switch
                    {
                        "Rect" => new RectangleLayer { X = x1, Y = y1, Width = w, Height = h },
                        "Ellipse" => new EllipseLayer { CenterX = x1 + w / 2, CenterY = y1 + h / 2, RadiusX = w / 2, RadiusY = h / 2 },
                        "Arrow" => new ArrowLayer { StartX = start.X, StartY = start.Y, EndX = end.X, EndY = end.Y },
                        "Mosaic" => new MosaicLayer { X = x1, Y = y1, Width = w, Height = h },
                        _ => new RectangleLayer { X = x1, Y = y1, Width = w, Height = h }
                    };
                    _annoManager.AddLayer(layer);
                }
            }
            RequestRedraw();
            return;
        }

        if (_annoMode) { _down = false; return; } // 标注模式下 Up 不处理选区 (2026-08-11 bugfix)

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
    }

    private void Key(int vk)
    {
        TouchIdle();
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        if (_editHwnd != 0) return; // 文字输入中：EDIT 子窗口自己处理 Enter/Esc
        if (_annoMode)
        {
            if (vk == VK_ESC) { ExitAnnotationMode(); return; }
            if (ctrl && vk == VK_Z) { _annoManager.Undo(); _annoTextSlotsDirty = true; RequestRedraw(); return; }
            if (ctrl && vk == VK_Y) { _annoManager.Redo(); _annoTextSlotsDirty = true; RequestRedraw(); return; }
            // 标注模式下 Enter = 保存（保持现状行为）
        }
        if (vk == VK_ESC) { FireAction(HdrCaptureAction.Cancel, 0, 0, 0, 0); Close(); }
        else if ((vk == VK_ENT || (vk == VK_S && !ctrl)) && _selComplete) DoAction(HdrCaptureAction.Save);
        else if (vk == VK_C && ctrl && _selComplete) DoAction(HdrCaptureAction.Copy); // Ctrl+C 复制
    }

    private void DoAction(HdrCaptureAction a)
    {
        if (!_selComplete) return;
        if (a == HdrCaptureAction.Annotate)
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
        LogService.Info("HdrCapture", "进入标注模式", LogCategory.UI);
        RequestRedraw();
    }

    private void ExitAnnotationMode()
    {
        if (!_annoMode) return;
        _annoMode = false;
        _annoDown = false;
        _penPoints.Clear();
        DestroyEdit();
        LogService.Info("HdrCapture", "退出标注模式", LogCategory.UI);
        RequestRedraw();
    }

    /// <summary>标注工具栏命中检测（10 按钮, 与 UpdateUiConstantBuffer 布局一致）。</summary>
    private (bool hit, int idx) HitAnnoToolbar(int x, int y)
    {
        if (!_selComplete) return (false, -1);
        int n = 10;
        var lay = GetToolbarLayout(n);
        int selX1 = Math.Min(_sx1, _sx2), selX2 = Math.Max(_sx1, _sx2);
        int selY1 = Math.Min(_sy1, _sy2), selY2 = Math.Max(_sy1, _sy2);
        int tx = Math.Clamp(selX1 + (selX2 - selX1) / 2 - lay.TotalW / 2, 4, _winW - lay.TotalW - 4);
        int ty = selY2 + 10;
        if (ty + lay.TotalH + 10 > _winH) ty = selY1 - lay.TotalH - 12;
        if (ty < 0) ty = 4;
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
        RequestRedraw();
    }

    // ── 文字输入 (Win32 EDIT 子窗口, 原生 IME 支持) ──

    private static WndProcDelegate? s_editProc;
    private static nint s_editOrigProc;

    private static nint EditWndProc(nint h, uint m, nint w, nint l)
    {
        if (m == WM_KEY)
        {
            int vk = (int)w;
            if (vk == VK_ENT || vk == VK_ESC)
            {
                if (s_windows.TryGetValue(GetParent(h), out var self))
                {
                    self.OnEditKey(vk);
                    return 0;
                }
            }
        }
        return CallWindowProcW(s_editOrigProc, h, m, w, l);
    }

    private void ShowTextEdit(System.Numerics.Vector2 framePos)
    {
        if (_editHwnd != 0) return;
        int ew = Math.Max(160, (int)Math.Round(240 * _uiScale));
        int eh = Math.Max(24, (int)Math.Round(30 * _uiScale));
        int ex = Math.Clamp((int)framePos.X, 0, _winW - ew);
        int ey = Math.Clamp((int)framePos.Y, 0, _winH - eh);
        _editHwnd = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_VIS | WS_BORDER | ES_AUTOHSCROLL,
            ex, ey, ew, eh, _hwnd, (nint)1001, GetModuleHandleW(null), 0);
        if (_editHwnd == 0) return;
        if (s_editOrigProc == 0)
        {
            s_editProc = EditWndProc;
            s_editOrigProc = GetWindowLongPtrW(_editHwnd, GWLP_WNDPROC);
            SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(s_editProc));
        }
        SetFocus(_editHwnd);
    }

    private void OnEditKey(int vk)
    {
        if (vk == VK_ENT)
        {
            var chars = new char[512];
            int len = GetWindowTextW(_editHwnd, chars, 512);
            string text = len > 0 ? new string(chars, 0, len).Trim() : "";
            DestroyEdit();
            if (text.Length > 0)
            {
                _annoManager.AddLayer(new TextLayer
                {
                    X = _textInsertPos.X, Y = _textInsertPos.Y,
                    Text = text, FontSize = 16
                });
                _annoTextSlotsDirty = true; // 文字槽图集重建
                RequestRedraw();
            }
        }
        else if (vk == VK_ESC) DestroyEdit();
    }

    private void DestroyEdit()
    {
        if (_editHwnd != 0) { DestroyWindow(_editHwnd); _editHwnd = 0; }
    }

    private void TouchIdle() { _idleWatch.Restart(); }

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
                var regions = RegionDetector.DetectWindows(_winX, _winY, _winW, _winH, new HashSet<nint> { _hwnd });
                _regions = regions;
                if (_hoverRegion >= regions.Count) _hoverRegion = -1;
                if (!_selComplete && !_down) RequestRedraw(); // 悬停高亮可能变化
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
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
        IsInitialized = false;
    }

    private void StopRenderThread()
    {
        _renderSignal.Set(); // 唤醒渲染线程退出
        if (_renderThread is { IsAlive: true } && _renderThread != Thread.CurrentThread)
            _renderThread.Join(2000);
        _renderThread = null;
    }

    /// <summary>外部强制取消（应用退出时由 MainWindow 调用，触发 ActionCompleted + 关闭窗口）。</summary>
    public void RequestCancel()
    {
        if (_hwnd == 0 || _disposed) return; // 已关闭/已销毁，防止重复触发
        FireAction(HdrCaptureAction.Cancel, 0, 0, 0, 0);
        Close();
    }

    private void Cleanup()
    {
        _autoTimer?.Dispose(); _autoTimer = null;
        _hardTimer?.Dispose(); _hardTimer = null;
        _regionRefreshTimer?.Dispose(); _regionRefreshTimer = null;
        _closing = true; // ═══ 2026-08-16 P1-2: 清理前让渲染循环退出 ═══
        StopRenderThread();
        _overlayPs?.Dispose(); _overlayPs = null;
        _uiCb?.Dispose(); _uiCb = null;
        _rsNoCull?.Dispose(); _rsNoCull = null;
        _textAtlasSrv?.Dispose(); _textAtlasSrv = null;
        _textAtlas?.Dispose(); _textAtlas = null;
        _desktopSrv?.Dispose(); _desktopSrv = null;
        _desktopTex?.Dispose(); _desktopTex = null;
        _pooledStaging?.Dispose(); _pooledStaging = null;
        _rtv?.Dispose(); _rtv = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        _swapChain?.Dispose(); _swapChain = null;
        _vs?.Dispose(); _vs = null;
        _ps?.Dispose(); _ps = null;
        _samp?.Dispose(); _samp = null;
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        // ═══ 2026-08-16(复审 P1-1): 释放独立 D3D11 设备+上下文 ═══
        // HdrCaptureWindow 构造时创建独立设备 (渲染线程独占), 旧实现 Dispose
        // 从不释放 → 每次 HDR 截图泄漏一整套 D3D11 设备/上下文/显存。
        // Cleanup() 已先停渲染线程, 此处安全释放。
        _ctx?.Dispose();
        _device.Dispose();
    }
}
