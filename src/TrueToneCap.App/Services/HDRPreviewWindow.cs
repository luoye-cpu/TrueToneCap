// TrueToneCap.App/Services/HdrPreviewWindow.cs
// HDR 预览窗口 — 完整 D3D11 渲染管线实现
// 使用 Win32 原生窗口 + DXGI 交换链，在 HDR 显示器上直接呈现 scRGB 线性帧
// 替代旧版 HDRPreviewWindow + HdrSwapChainRenderer（两者合并重写）

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TrueToneCap.App.Services;

public sealed class HdrPreviewWindow : IDisposable
{
    // ── 窗口句柄与尺寸 ──
    private nint _hwnd;
    private int _winX, _winY, _winW, _winH;
    private bool _disposed;

    // ── D3D11 资源 ──
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;

    // ── 状态 ──
    public bool IsInitialized { get; private set; }
    public string? LastError { get; private set; }
    public nint Hwnd => _hwnd;

    /// <summary>用户按下 Esc 取消时触发。</summary>
    public event Action? OnCancel;

    // ═══════════════════════════════════════════════════
    //  Win32 窗口注册
    // ═══════════════════════════════════════════════════

    private static bool _classRegistered;
    private const string ClassName = "TrueToneCap_HdrV2";
    private static WndProcDelegate? _s_wndProc;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? lpName);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_DESTROY = 0x0002;
    private const int VK_ESCAPE = 0x1B;

    // ═══════════════════════════════════════════════════
    //  构造函数
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 创建 HDR 预览窗口。
    /// </summary>
    /// <param name="sharedDevice">可选的共享 D3D11 设备（来自 WgcCaptureService）。
    /// 为 null 时内部自动创建。</param>
    public HdrPreviewWindow(ID3D11Device? sharedDevice = null)
    {
        if (sharedDevice is not null)
        {
            _device = sharedDevice;
            _context = _device.ImmediateContext;
        }
        else
        {
            _device = D3D11.D3D11CreateDevice(
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport);
            _context = _device.ImmediateContext;
        }
    }

    // ═══════════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════════

    /// <summary>创建窗口和 DXGI 交换链。</summary>
    public bool Initialize(int x, int y, int width, int height)
    {
        _winX = x; _winY = y; _winW = width; _winH = height;

        try
        {
            // ── 注册窗口类 ──
            if (!_classRegistered)
            {
                _s_wndProc = WndProc;
                var wc = new WNDCLASSW
                {
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_s_wndProc),
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = ClassName
                };
                RegisterClassW(ref wc);
                _classRegistered = true;
            }

            // ── 创建窗口 ──
            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
                ClassName, "HDR Preview",
                WS_POPUP | WS_VISIBLE,
                x, y, width, height,
                nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);

            if (_hwnd == nint.Zero)
            {
                LastError = $"CreateWindowExW 失败: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // ── 创建 DXGI 交换链 ──
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore
            };

            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);

            // ═══ 关键: 设置 DXGI 色彩空间为 scRGB (RgbFullG10NoneP709) ═══
            // 默认是 RgbFullG22NoneP709 (sRGB gamma 2.2)，但 WGC HDR 捕获的数据
            // 是 scRGB 线性 (gamma 1.0)，不设置会导致 DWM 以 sRGB 非线性解释
            // 线性数据 → 严重过曝 + 色彩错误
            using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);

            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            SetFocus(_hwnd);

            IsInitialized = true;
            System.Diagnostics.Debug.WriteLine(
                $"[HdrPreview] 初始化完成: {width}x{height} @({x},{y}) HWND=0x{_hwnd:X}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            System.Diagnostics.Debug.WriteLine($"[HdrPreview] 初始化失败: {ex}");
            Cleanup();
            return false;
        }
    }

    // ═══════════════════════════════════════════════════
    //  帧呈现
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 呈现 HDR 帧到窗口。
    /// 输入的 float[] 应为 scRGB 线性 RGBA 数据（与 WGC HDR 捕获输出一致）。
    /// </summary>
    /// <param name="pixels">scRGB linear float[] RGBA 像素。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    public unsafe void PresentFrame(float[] pixels, int width, int height)
    {
        if (_disposed || _swapChain is null || _context is null || _device is null)
            return;

        try
        {
            // ── 尺寸变化时重建交换链 ──
            if (width != _winW || height != _winH)
            {
                _backBuffer?.Dispose();
                _backBuffer = null;
                _swapChain.ResizeBuffers(2, (uint)width, (uint)height,
                    Format.R16G16B16A16_Float, SwapChainFlags.None);
                _winW = width;
                _winH = height;
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

                // ResizeBuffers 后色彩空间重置为默认，需重新设置
                using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
                sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            }

            // ── 创建 Staging 纹理并上传像素 ──
            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            var mapped = _context.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            if (mapped.DataPointer == IntPtr.Zero)
                return;

            // 使用 PixelOps SIMD 加速的 float→half 批量转换
            byte* dstBase = (byte*)mapped.DataPointer.ToPointer();
            int dstRowPitch = (int)mapped.RowPitch;
            int halfsPerRow = width * 4;

            fixed (float* src = pixels)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* dstRow = dstBase + row * dstRowPitch;
                    float* srcRow = src + row * width * 4;
                    // .NET 10 JIT 自动将 Half 转换编译为 F16C VCVTPS2PH (x86)
                    TrueToneCap.Core.PixelOps.ConvertFloatToHalfRow(
                        srcRow, (ushort*)dstRow, halfsPerRow);
                }
            }

            _context.Unmap(staging, 0);

            // ── 复制到后台缓冲区并呈现 ──
            _context.CopyResource(_backBuffer!, staging);
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HdrPreview] PresentFrame 异常: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  测试图案（无需真实捕获数据即可验证 HDR 显示）
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 呈现 HDR 测试图案（用于验证 HDR 显示是否正确）。
    /// 图案为红-绿-蓝渐变条，上半部分高亮度，下半部分 20% 亮度。
    /// </summary>
    public unsafe void PresentTestPattern(int width, int height)
    {
        if (_disposed || _swapChain is null || _context is null || _device is null || _backBuffer is null)
            return;

        try
        {
            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            var mapped = _context.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            if (mapped.DataPointer == IntPtr.Zero)
                return;

            byte* db = (byte*)mapped.DataPointer.ToPointer();
            int dp = (int)mapped.RowPitch;

            for (int row = 0; row < height; row++)
            {
                ushort* d = (ushort*)(db + row * dp);
                bool bottomHalf = row > height / 2;

                for (int x = 0; x < width; x++)
                {
                    float t = (float)x / width;
                    float r, g, b;

                    // 从左到右渐变色：红→绿→蓝
                    if (t < 0.33f)
                    {
                        r = 1.0f;
                        g = t * 3.0f;
                        b = 0.0f;
                    }
                    else if (t < 0.66f)
                    {
                        r = (0.66f - t) * 3.0f;
                        g = 1.0f;
                        b = (t - 0.33f) * 3.0f;
                    }
                    else
                    {
                        r = 0.0f;
                        g = (1.0f - t) * 3.0f;
                        b = 1.0f;
                    }

                    // 下半部分 20% 亮度，验证 HDR 动态范围
                    if (bottomHalf)
                    {
                        r *= 0.2f;
                        g *= 0.2f;
                        b *= 0.2f;
                    }

                    int i = x * 4;
                    d[i]     = FloatToHalf(r);
                    d[i + 1] = FloatToHalf(g);
                    d[i + 2] = FloatToHalf(b);
                    d[i + 3] = 0x3C00;
                }
            }

            _context.Unmap(staging, 0);
            _context.CopyResource(_backBuffer, staging);
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HdrPreview] 测试图案失败: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Float32 → Float16 转换
    // ═══════════════════════════════════════════════════

    /// <summary>IEEE 754 float32 → float16 转换。</summary>
    private static ushort FloatToHalf(float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        int s = (bits >> 16) & 0x8000;
        int e = ((bits >> 23) & 0xFF) - 127 + 15;
        int m = bits & 0x7FFFFF;

        if (e <= 0)
        {
            if (e < -10) return (ushort)s;
            m |= 0x800000;
            return (ushort)(s | (m >> (14 - e)));
        }
        if (e >= 31)
            return (ushort)(s | 0x7BFF);

        return (ushort)(s | (e << 10) | (m >> 13));
    }

    // ═══════════════════════════════════════════════════
    //  窗口过程
    // ═══════════════════════════════════════════════════

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE)
                {
                    OnCancel?.Invoke();
                    Close();
                }
                return 0;

            case WM_DESTROY:
                return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ═══════════════════════════════════════════════════
    //  关闭与清理
    // ═══════════════════════════════════════════════════

    /// <summary>关闭窗口（不销毁 D3D 设备，适合共享设备场景）。</summary>
    public void Close()
    {
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
        IsInitialized = false;
    }

    private void Cleanup()
    {
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain?.Dispose();
        _swapChain = null;
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
        IsInitialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain?.Dispose();
        _swapChain = null;
        // 共享设备场景下不释放 _device，由调用方管理
        _context?.Dispose();
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }
}