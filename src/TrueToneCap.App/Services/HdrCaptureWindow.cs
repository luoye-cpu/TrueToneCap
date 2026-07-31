// TrueToneCap.App/Services/HdrCaptureWindow.cs
// 全 D3D11 HDR 截图预览窗口
// 桌面帧: D3D11 全屏纹理渲染 (scRGB Float16)
// UI 覆盖层: CPU 软件渲染 BGRA8 → D3D11 Alpha 混合合成
// 交互: 原生 Win32 鼠标/键盘消息

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using TrueToneCap.Core.Detection;

namespace TrueToneCap.App.Services;

public enum HdrCaptureAction { Cancel, Save, Copy, Annotate, Ocr, Translate }

public sealed class HdrCaptureWindow : IDisposable
{
    private nint _hwnd;
    private int _winX, _winY, _winW, _winH;
    private bool _disposed;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _samp;
    private ID3D11Texture2D? _desktopTex;
    private ID3D11ShaderResourceView? _desktopSrv;
    private bool _hasFrame;
    private float[]? _origPixels;  // 原始帧（不可修改）
    private float[]? _compPixels; // 合成缓冲（每帧从原始数据重建）
    private int _dragHandle = -1; // 当前拖拽的手柄 (0=TL, 1=TR, 2=BL, 3=BR, -1=无)
    private const int HANDLE_SIZE = 8; // 手柄尺寸（像素）
    private const int HANDLE_HIT = 12; // 手柄命中区域（像素）
    private bool _selComplete;
    private int _sx1, _sy1, _sx2, _sy2, _mx, _my;
    private bool _down, _moved;

    // 窗口识别（QQ截图式快速选择）
    private List<DetectedRegion> _regions = [];
    private int _hoverRegion = -1; // 当前悬停的窗口索引

    // 自动超时关闭
    private System.Threading.Timer? _autoTimer;
    private const int AutoTimeoutMs = 300_000; // 5 分钟无操作自动关闭
    private System.Diagnostics.Stopwatch _idleWatch = System.Diagnostics.Stopwatch.StartNew();

    public event Action<HdrCaptureAction, int, int, int, int>? ActionCompleted;
    public bool IsInitialized { get; private set; }
    public string? LastError { get; private set; }

    private static bool _classReg;
    private const string CN = "TTC_HC";
    private static WndProcDelegate? _staticWndProc;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, HdrCaptureWindow> s_windows = new();
    private const int GWLP_USERDATA = -21;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string t, uint s, int x, int y, int w, int h, nint p, nint m, nint i, nint lp);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool SetCapture(nint h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int k);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? n);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint h, nint c);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW { public uint style; public nint proc; public int cbCls; public int cbWnd; public nint inst; public nint icon; public nint cur; public nint bg; public string? menu; public string? cls; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public nint hdc; public bool fErase; public RECT rc; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }
    private delegate nint WndProcDelegate(nint h, uint m, nint w, nint l);

    private const uint WS_POPUP = 0x80000000, WS_VIS = 0x10000000;
    // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP
    // 注意: 不能用 WS_EX_NOACTIVATE（键盘失效）和 WS_EX_TRANSPARENT（鼠标穿透）
    private const uint WS_EX = 0x00000008 | 0x00000080 | 0x00200000;
    private const uint SWP_F = 0x0040 | 0x0010;
    private static readonly nint TOP = new(-1), CROSS = new(32515);
    private const uint WM_PAINT = 0x000F, WM_ERASE = 0x0014, WM_KEY = 0x0100, WM_LD = 0x0201, WM_LU = 0x0202, WM_MM = 0x0200;
    private const int VK_ESC = 0x1B, VK_ENT = 0x0D, VK_S = 0x53;
    private static int GX(nint l) => (short)(l.ToInt32() & 0xFFFF);
    private static int GY(nint l) => (short)((l.ToInt32() >> 16) & 0xFFFF);

    public HdrCaptureWindow(ID3D11Device? shared = null)
    {
        if (shared is not null) { _device = shared; _ctx = _device.ImmediateContext; }
        else { _device = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport); _ctx = _device.ImmediateContext; }
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
            SetWindowLongPtr(_hwnd, GWLP_USERDATA, _hwnd); // 用窗口句柄自身作为 key
            SetForegroundWindow(_hwnd); // 获取键盘焦点

            using var dxgiD = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiD.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1 { Width = (uint)w, Height = (uint)h, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore };
            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(_backBuffer);

            string sd = Path.Combine(AppContext.BaseDirectory, "data", "Shaders");
            string vsp = Path.Combine(sd, "FullscreenVS.hlsl.cso");
            if (File.Exists(vsp)) _vs = _device.CreateVertexShader(File.ReadAllBytes(vsp), null);
            // 使用 CopyTexture.hlsl（直通拷贝，不做色调映射）
            string psp = Path.Combine(sd, "CopyTexture.hlsl.cso");
            if (!File.Exists(psp)) psp = Path.Combine(sd, "ToneMapping.hlsl.cso"); // 回退
            if (File.Exists(psp)) _ps = _device.CreatePixelShader(File.ReadAllBytes(psp));
            _samp = _device.CreateSamplerState(new SamplerDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue });

            SetWindowPos(_hwnd, TOP, x, y, w, h, SWP_F);
            IsInitialized = true;
            System.Diagnostics.Debug.WriteLine($"[HC] OK {w}x{h} @({x},{y})");

            // 启动自动超时定时器
            _autoTimer = new System.Threading.Timer(_ =>
            {
                if (_idleWatch.ElapsedMilliseconds >= AutoTimeoutMs)
                {
                    System.Diagnostics.Debug.WriteLine("[HC] 超时自动关闭");
                    ActionCompleted?.Invoke(HdrCaptureAction.Cancel, 0, 0, 0, 0);
                    Close();
                }
            }, null, AutoTimeoutMs, 5000);

            // 后台检测窗口区域（QQ截图式快速选择）
            _ = Task.Run(() =>
            {
                try
                {
                    _regions = RegionDetector.DetectWindows(x, y, w, h, new HashSet<nint> { _hwnd });
                    System.Diagnostics.Debug.WriteLine($"[HC] 窗口检测: {_regions.Count} 个");
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HC] 窗口检测失败: {ex.Message}"); }
            });

            return true;
        }
        catch (Exception ex) { LastError = ex.ToString(); Cleanup(); return false; }
    }

    public unsafe void LoadFrame(float[] pixels, int w, int h)
    {
        if (_disposed) return;
        // 保留原始帧（不可修改）+ 创建合成缓冲
        _origPixels = pixels;
        _compPixels = new float[pixels.Length];
        UploadFrame(pixels, w, h);
    }

    private unsafe void UploadFrame(float[] pixels, int w, int h)
    {
        if (_desktopTex is null || _desktopTex.Description.Width != w || _desktopTex.Description.Height != h)
        {
            _desktopSrv?.Dispose(); _desktopTex?.Dispose();
            _desktopTex = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource });
            _desktopSrv = _device.CreateShaderResourceView(_desktopTex);
        }
        using var st = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Write });
        var m = _ctx.Map(st, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        if (m.DataPointer == 0) return;
        byte* db = (byte*)m.DataPointer; int dp = (int)m.RowPitch;
        fixed (float* s = pixels) { for (int r = 0; r < h; r++) TrueToneCap.Core.PixelOps.ConvertFloatToHalfRow(s + r * w * 4, (ushort*)(db + r * dp), w * 4); }
        _ctx.Unmap(st, 0); _ctx.CopyResource(_desktopTex, st);
        _hasFrame = true;
    }

    public void Render()
    {
        if (!IsInitialized || _swapChain is null || _backBuffer is null) return;
        try
        {
            // 从原始帧重建合成缓冲（不破坏原始数据）
            if (_origPixels is not null && _compPixels is not null)
            {
                CompositeUI();
                UploadFrame(_compPixels, _winW, _winH);
            }

            // 直接 CopyResource：桌面纹理和后台缓冲区都是 R16G16B16A16_Float 同尺寸
            if (_hasFrame && _desktopTex is not null)
            {
                _ctx.CopyResource(_backBuffer, _desktopTex);
            }

            _swapChain.Present(1, PresentFlags.None);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HC] Render: {ex.Message}"); }
    }

    /// <summary>从原始帧重建合成缓冲，叠加 UI 元素（每帧从 _origPixels 拷贝，不累积）。</summary>
    private void CompositeUI()
    {
        if (_origPixels is null || _compPixels is null) return;
        int w = _winW, h = _winH;

        // 1. 从原始帧拷贝到合成缓冲
        Array.Copy(_origPixels, _compPixels, _origPixels.Length);
        var px = _compPixels;

        // 2. 半透明遮罩（选区外）— 使用用户配置的覆盖颜色
        if (!_selComplete || _down)
        {
            // 解析用户配置的覆盖颜色 (#AARRGGBB → scRGB 线性)
            var (ovR, ovG, ovB, ovA) = ParseColorToLinear(AppServices.Settings.Current.OverlayColor);

            int x1 = _selComplete ? Math.Min(_sx1, _sx2) : -1;
            int y1 = _selComplete ? Math.Min(_sy1, _sy2) : -1;
            int x2 = _selComplete ? Math.Max(_sx1, _sx2) : -1;
            int y2 = _selComplete ? Math.Max(_sy1, _sy2) : -1;

            for (int row = 0; row < h; row++)
            {
                int rowOff = row * w * 4;
                for (int col = 0; col < w; col++)
                {
                    if (_selComplete && col >= x1 && col <= x2 && row >= y1 && row <= y2)
                        continue;
                    int i = rowOff + col * 4;
                    px[i] = px[i] * (1 - ovA) + ovR * ovA;
                    px[i + 1] = px[i + 1] * (1 - ovA) + ovG * ovA;
                    px[i + 2] = px[i + 2] * (1 - ovA) + ovB * ovA;
                }
            }
        }

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
            // 四角手柄（白色方块，8x8）
            if (_selComplete)
            {
                int hs = HANDLE_SIZE;
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

            int btnW = 90, btnH = 32, gap = 4;
            int padX = 6, padY = 5;
            int n = items.Length;
            int totalW = n * btnW + (n - 1) * gap + padX * 2;
            int totalH = btnH + padY * 2;

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
                int hintW = 160, hintH = 22;
                int hx = Math.Clamp(tx + totalW / 2 - hintW / 2, 4, w - hintW - 4);
                int hy = ty + totalH + 4;
                if (hy + hintH + 4 > h) hy = ty - hintH - 4;
                DrawRoundedRect(px, w, h, hx, hy, hx + hintW, hy + hintH, 4, hintR, hintG, hintB, hintA);
                DrawTextGdi(px, w, h, hintText, hx, hy, hintW, hintH, txtR, txtG, txtB);
            }
        }
    }

    /// <summary>使用 System.Drawing (GDI+) 渲染文字到 Bitmap，用 alpha 通道合成到 float 缓冲区。</summary>
    private unsafe void DrawTextGdi(float[] px, int bufW, int bufH, string text, int x, int y, int tw, int th, float r, float g, float b)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var gfx = System.Drawing.Graphics.FromImage(bmp))
            {
                // 透明背景 → alpha=0，白色文字 → alpha=255，抗锯齿边缘 → alpha=1..254
                gfx.Clear(System.Drawing.Color.Transparent);
                gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                using var font = new System.Drawing.Font("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Bold);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                var sf = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center
                };
                gfx.DrawString(text, font, brush, new System.Drawing.RectangleF(0, 0, tw, th), sf);
            }

            // 读取像素：Format32bppArgb 在 little-endian 内存中为 BGRA
            // alpha 通道 (byte[3]) 直接作为文字覆盖率
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, tw, th),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte* src = (byte*)data.Scan0;
            for (int row = 0; row < th; row++)
            {
                int dstY = y + row;
                if (dstY < 0 || dstY >= bufH) continue;
                byte* rowPtr = src + row * stride;
                for (int col = 0; col < tw; col++)
                {
                    int dstX = x + col;
                    if (dstX < 0 || dstX >= bufW) continue;
                    int si = col * 4;
                    byte alpha = rowPtr[si + 3]; // A 通道 = 文字覆盖率
                    if (alpha < 15) continue;
                    float a = alpha / 255f;
                    int di = (dstY * bufW + dstX) * 4;
                    if (di + 3 >= px.Length) continue;
                    px[di] = px[di] * (1 - a) + r * a;
                    px[di + 1] = px[di + 1] * (1 - a) + g * a;
                    px[di + 2] = px[di + 2] * (1 - a) + b * a;
                }
            }
            bmp.UnlockBits(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HC] DrawTextGdi 失败: {ex.Message}");
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
        int rad = Math.Min(radius, Math.Min((x2 - x1) / 2, (y2 - y1) / 2));
        if (rad < 2)
        {
            FillRectLinear(px, w, h, x1, y1, x2, y2, r, g, b);
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
        switch (m)
        {
            case WM_PAINT: { Render(); var ps = new PAINTSTRUCT(); BeginPaint(h, ref ps); EndPaint(h, ref ps); return 0; }
            case WM_ERASE: return 1;
            case WM_LD: Down(GX(l), GY(l)); SetCapture(h); return 0;
            case WM_MM: Move(GX(l), GY(l)); return 0;
            case WM_LU: Up(GX(l), GY(l)); ReleaseCapture(); return 0;
            case WM_KEY: Key((int)w); return 0;
        }
        return DefWindowProcW(h, m, w, l);
    }

    private void Down(int x, int y)
    {
        TouchIdle();
        _down = true; _moved = false; _dragHandle = -1;

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
            int btnW = 90, btnH = 32, gap = 4, padX = 6, padY = 5, n = items.Length;
            int totalW = n * btnW + (n - 1) * gap + padX * 2;
            int totalH = btnH + padY * 2;
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
                    DoAction(actions[i]);
                    return;
                }
            }

            // 2. 四角手柄拖拽检测
            int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
            int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
            int hit = HANDLE_HIT;
            if (Math.Abs(x - x1) <= hit && Math.Abs(y - y1) <= hit) { _dragHandle = 0; return; } // TL
            if (Math.Abs(x - x2) <= hit && Math.Abs(y - y1) <= hit) { _dragHandle = 1; return; } // TR
            if (Math.Abs(x - x1) <= hit && Math.Abs(y - y2) <= hit) { _dragHandle = 2; return; } // BL
            if (Math.Abs(x - x2) <= hit && Math.Abs(y - y2) <= hit) { _dragHandle = 3; return; } // BR

        }

        _sx1 = _sx2 = x; _sy1 = _sy2 = y;
    }

    private void Move(int x, int y)
    {
        TouchIdle();
        _mx = x; _my = y;

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
            Render();
            return;
        }

        if (_down && (Math.Abs(x - _sx1) > 3 || Math.Abs(y - _sy1) > 3)) { _moved = true; _sx2 = x; _sy2 = y; Render(); }
        else if (!_down && !_selComplete)
        {
            // 窗口悬停检测（QQ截图式）
            int screenX = _winX + x, screenY = _winY + y;
            int newHover = RegionDetector.FindSmallestRegionAt(_regions, screenX, screenY);
            if (newHover != _hoverRegion) { _hoverRegion = newHover; Render(); }
        }
        else if (!_down && _selComplete) Render(); // hover 时刷新工具栏高亮
    }

    private void Up(int x, int y)
    {
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
        _selComplete = true; Render();
    }

    private void Key(int vk)
    {
        TouchIdle();
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        if (vk == VK_ESC) { ActionCompleted?.Invoke(HdrCaptureAction.Cancel, 0, 0, 0, 0); Close(); }
        else if ((vk == VK_ENT || (vk == VK_S && !ctrl)) && _selComplete) DoAction(HdrCaptureAction.Save);
    }

    private void DoAction(HdrCaptureAction a)
    {
        if (!_selComplete) return;
        int x1 = Math.Min(_sx1, _sx2), y1 = Math.Min(_sy1, _sy2);
        int x2 = Math.Max(_sx1, _sx2), y2 = Math.Max(_sy1, _sy2);
        ActionCompleted?.Invoke(a, _winX + x1, _winY + y1, x2 - x1, y2 - y1);
        Close();
    }

    private void TouchIdle() { _idleWatch.Restart(); }

    public void Close() { _autoTimer?.Dispose(); _autoTimer = null; if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; } IsInitialized = false; }

    private void Cleanup()
    {
        _autoTimer?.Dispose(); _autoTimer = null;
        _desktopSrv?.Dispose(); _desktopTex?.Dispose();
        _rtv?.Dispose(); _backBuffer?.Dispose(); _swapChain?.Dispose();
        _vs?.Dispose(); _ps?.Dispose(); _samp?.Dispose();
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Cleanup(); }
}