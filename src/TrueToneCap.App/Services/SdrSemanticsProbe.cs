// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/SdrSemanticsProbe.cs
//
// 真机探针：TrueToneCap.exe --sdr-semantics-probe
//
// ── 它回答哪一个具体问题 ──
// 2026-10-26 用 TTC_FORCE_LEGACY_OVERLAY=1 冒烟时，兜底覆盖层（2026-10-27 已整份删除）的背景被肉眼看到"比实时桌面亮"。
// 兜底那条路的供帧是【纯字节拷贝】（SelectionOverlay.RenderDesktopBackground 把
// CaptureResult.SdrPixels 直接写进 8-bit WriteableBitmap，没有任何色彩转换），
// 所以"更亮"只有两种可能：字节本身已被系统纸白抬升过一次（8-bit 窗显示时 DWM 再抬一次 ⇒ 双重提亮），
// 或者字节是 authored、亮源自别处。两条修法互斥，必须先分清。
// 判据本体在 Core（SdrSessionSemantics.Judge，配合成对照臂的单测），这里只负责【交回读数】。
//
// ── 绝对锚点从哪来 ──
// 自己用 GDI 画一块【已知字节值】的纯色补丁（WS_POPUP 无边框、置顶、WS_EX_NOACTIVATE 不抢焦点），
// 再经生产捕获入口把同一时刻的 8-bit 帧与 Float16 帧一起拿回来，采样补丁区：
//   · mean8 / authored               ⇒ 8-bit 会话是否原样交回画上去的字节
//   · meanFloat / linear(authored)   ⇒ Float16 的亮度基准在 1.0（80nits 参考白）还是 k=纸白/80
// 补丁刻意取【中性灰】：R=G=B 时通道顺序、原色、ACM 的色域重映射都影响不到灰度均值，
// 读数只可能来自亮度基准这一维 —— 正是本探针要问的那一维。
// 另取一枚"补丁隐藏时"的同区对照读数：证明采到的那块确实是补丁本身，而不是恰好同色的桌面。
//
// ── 退出码 ──
// 0 = 自检通过且 8-bit 语义判到了（authored / boosted 二选一）；1 = 没判到。
// 退出码表达【判没判】，不表达"截图对不对"——与 --window-probe / --hdr-probe 同一口径。

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TrueToneCap.Core;
using TrueToneCap.Core.Capture;

namespace TrueToneCap.App.Services;

public static partial class SdrSemanticsProbe
{
    public const string Flag = "--sdr-semantics-probe";

    // 灰阶 5 档。中段（24~220）进判定；255 只看钳位。
    // 刻意不给太多档：一次"稳定"要连读两帧，每帧 4K 回读 ~0.7s，档数是运行时间的乘数。
    private static readonly int[] Grays = [32, 64, 128, 192, 255];

    private const int PatchW = 520, PatchH = 360;   // 物理像素
    private const int MonitorInset = 200;           // 补丁离目标屏左上角的距离
    private const int SettleMs = 200;               // 每次复读之间隔这么久
    private const int MaxStableTries = 8;           // 一个补丁最多复读这么多次
    private const double StableTol8 = 0.5;          // 8-bit 均值两次之差小于此 ⇒ 认为画面已稳定
    private const double StableTolFloat = 0.002;    // float 线性均值的同档容差
    private const int CaptureWaitMs = 25_000;       // 单次捕获整体上限（会话起不来时不拖死探针）
    private const double SampleInset = 0.20;        // 采样区每边再缩 20%（避开边缘、阴影、取整）

    // ═══ Win32 interop（LibraryImport + 函数指针 WNDPROC，无需 GC 根）═══
    private const string ClassName = "TtcSdrSemanticsPatch";
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SW_HIDE = 0, SW_SHOW = 5;
    private const uint PM_REMOVE = 0x0001;   // ⚠ 0 是 PM_NOREMOVE —— 写 0 时 PeekMessage 恒真且不摘取，
                                             // 内层 while 会拿同一条消息转圈，探针表现为"卡在 Pump 里"
    private const uint WM_PAINT = 0x000F, WM_DESTROY = 0x0002, WM_ERASEBKGND = 0x0014;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, uint nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG msg, nint hWnd, uint min, uint max, uint flags);

    [LibraryImport("user32.dll")]
    private static partial void TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(nint hWnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ValidateRect(nint hWnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out RECT rc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT rc);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    // FillRect 是 **user32** 的导出（gdi32 里只有 Rectangle 那一族）。
    // 实测代价：写成 gdi32 时探针不是报异常，而是 0xC0000409 fail-fast ——
    // EntryPointNotFoundException 从 WM_PAINT 回调里抛出去，穿不过原生边界，托管日志一个字都不留。
    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint hdc, ref RECT rc, nint brush);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    // WndProc 只碰 nint/struct，不分配托管对象 ⇒ 可以是 [UnmanagedCallersOnly]。
    // 补丁刷柄由 pump 线程写入，WM_PAINT 也在同一线程派发 ⇒ 无跨线程竞争。
    private static nint s_brush;
    private static int s_paintErrors;   // 画不成的次数：异常穿不出原生边界（会变成 0xC0000409），只能自己记账

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 这里抛出去的任何托管异常都穿不过原生边界 ⇒ 进程 fail-fast，且不留一行日志
        // （上一版就是这么死的：EntryPointNotFound 从 WM_PAINT 里出来 = 0xC0000409）。
        // 所以整段包起来，失败只加计数，由探针主体把原因写进读数文件。
        try
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return 1;   // 背景由 WM_PAINT 亲手填满；让它擦只会制造闪烁
                case WM_PAINT:
                {
                    if (s_brush == 0) { s_paintErrors++; break; }
                    var hdc = GetDC(hWnd);
                    if (hdc == 0 || !GetClientRect(hWnd, out var rc)) { s_paintErrors++; break; }
                    FillRect(hdc, ref rc, s_brush);
                    ReleaseDC(hWnd, hdc);
                    // 没走 BeginPaint 就没人清 update region ⇒ 必须亲手 ValidateRect，
                    // 否则 WM_PAINT 永远再来一次，消息循环空转、探针看起来像"卡在某档"。
                    ValidateRect(hWnd, 0);
                    return 0;
                }
                case WM_DESTROY:
                    return 0;
            }
        }
        catch
        {
            s_paintErrors++;
            return 0;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ═══ 补丁窗的唯一实现：探针（自己那条线程泵消息）与 --overlay-whitelevel-ab（UI 线程泵）共用
    // 同一扇窗、同一个 WndProc、同一份 FillRect 配方。两处各写一份的话，"参照臂和被试臂到底是不是
    // 同一种东西"这个问题就没有答案了 —— 而那正是 A/B 唯一要问的事。
    // ⚠ 静态刷柄只支持进程内一扇在世；两个诊断档互斥（各自独立进程），不做多实例支持。

    /// <summary>建一扇置顶无边框补丁窗，并读回【实测】矩形（调用方要按这个矩形采样，别按请求值）。
    /// 失败返回 0 并在 error 里带上 Win32 错误码。</summary>
    internal static unsafe nint CreatePatchWindow(int x, int y, int w, int h,
        out RECT actual, out string? error)
    {
        actual = default;
        error = null;
        nint hInst = GetModuleHandleW(0);
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            // Win32 回调是 stdcall；`delegate* unmanaged` 默认 cdecl，显式写 [Stdcall] 才与
            // WndProc 上的 CallConvs 匹配（少写会报 CS8757「没有匹配的重载」）。
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = hInst,
            lpszClassName = classNamePtr,
        };
        ushort atom = RegisterClassExW(ref wc);
        Marshal.FreeHGlobal(classNamePtr);
        if (atom == 0)
        {
            error = $"RegisterClassExW 失败 err={Marshal.GetLastPInvokeError()}";
            return 0;
        }

        nint hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, ClassName, WS_POPUP | WS_VISIBLE, x, y, w, h, 0, 0, hInst, 0);
        if (hwnd == 0)
        {
            error = $"CreateWindowExW 失败 err={Marshal.GetLastPInvokeError()}";
            return 0;
        }
        if (!GetWindowRect(hwnd, out actual))
        {
            error = $"GetWindowRect 失败 err={Marshal.GetLastPInvokeError()} ⇒ 采样几何无从校验";
            DestroyWindow(hwnd);
            return 0;
        }
        return hwnd;
    }

    /// <summary>换色 + 立刻同步重绘一帧（UpdateWindow 直接派发 WM_PAINT，不等消息循环）。</summary>
    internal static void PaintPatch(nint hwnd, byte gray)
    {
        SetPatchColor((uint)((gray << 16) | (gray << 8) | gray));
        InvalidateRect(hwnd, 0, false);
        UpdateWindow(hwnd);
    }

    internal static void ShowPatch(nint hwnd, bool on) => ShowWindow(hwnd, on ? SW_SHOW : SW_HIDE);

    internal static void ClosePatchWindow(nint hwnd)
    {
        if (s_brush != 0) { DeleteObject(s_brush); s_brush = 0; }
        if (hwnd != 0) DestroyWindow(hwnd);
    }

    internal static int PatchPaintErrors => s_paintErrors;

    /// <summary>探针主体。返回退出码（0=判到了，1=没判到）；全文同时落盘到 <paramref name="artifactPath"/>。</summary>
    public static int Run(string artifactPath)
    {
        var report = new StringBuilder();
        int exitCode = 1;
        // 读数【逐行】落盘，不是攒到最后一次写：本轮实测过一次"卡在第 1 次捕获里"，
        // 那种情况下最后汇总的那一次写永远不会发生，探针就成了一个没有任何现场的超时。
        try
        {
            var dir = Path.GetDirectoryName(artifactPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(artifactPath, "", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sdr-semantics-probe] ★ 读数文件建不出来（{ex.GetType().Name}: {ex.Message}）");
            return 1;
        }

        // 窗口必须建在"自己会 pump 消息"的线程上；捕获从这条线程交给线程池（生产入口内部也是 Task.Run）。
        var worker = new Thread(() => exitCode = Worker(report, artifactPath)) { IsBackground = true, Name = "SdrSemanticsProbe" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromMinutes(2)))
        {
            exitCode = 1;
            Say(report, artifactPath, "★ 探针线程 2 分钟未返回 ⇒ 判为【没判】（上面最后一行就是卡住的那一步）");
        }

        try
        {
            File.WriteAllText(artifactPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"[sdr-semantics-probe] 退出码={exitCode} 读数已落盘: {artifactPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sdr-semantics-probe] ★ 落盘失败（{ex.GetType().Name}: {ex.Message}），下面仍是全文：");
            Console.WriteLine(report.ToString());
        }
        return exitCode;
    }

    /// <summary>追加一行：既进汇总，也立刻落盘 —— 挂住/崩掉时"最后一行"就是现场。</summary>
    private static void Say(StringBuilder report, string artifactPath, string line)
    {
        report.AppendLine(line);
        try { File.AppendAllText(artifactPath, line + "\n", new UTF8Encoding(false)); } catch { }
    }

    private static unsafe int Worker(StringBuilder report, string artifactPath)
    {
        void Say2(string line) => Say(report, artifactPath, line);

        var swAll = Stopwatch.StartNew();
        try
        {
            // ── 0. 先印现场事实，再谈读数 ──
            var displays = DisplayEnumerator.EnumerateDisplaysUncached();
            var (sysHdr, acm, supportsHdr) = CapabilityService.DetectDisplayState();
            int sdrWhiteNits = DisplayEnumerator.GetSdrWhiteLevel();
            double k = SdrSessionSemantics.SdrWhiteScale(sdrWhiteNits);
            var target = displays.FirstOrDefault(d => d.IsHdr)
                         ?? displays.FirstOrDefault(d => d.IsPrimary)
                         ?? displays.FirstOrDefault();
            if (target is null)
            {
                Say2("★ 无显示器 ⇒ 判为【没判】");
                return 1;
            }

            Say2("TrueToneCap SDR 会话语义探针 (--sdr-semantics-probe)");
            Say2($"  目标屏={target.Name} 原点=({target.X},{target.Y}) 尺寸={target.Width}x{target.Height} " +
                              $"IsHdr={target.IsHdr} 色域={target.ColorSpace} 峰值={target.MaxLuminance:F0}nits " +
                              $"（屏枚举 {displays.Count} 个）");
            Say2($"  系统 HDR={sysHdr} ACM={acm} 硬件支持={supportsHdr} " +
                              $"SDR 白点={sdrWhiteNits}nits ⇒ k=白点/80={(k > 0 ? k.ToString("0.000") : "读不到")}");
            Say2($"  补丁={PatchW}x{PatchH} 物理像素 @({target.X + MonitorInset},{target.Y + MonitorInset}) " +
                              $"灰阶=[{string.Join(",", Grays)}] 采样区每边缩 {(int)(SampleInset * 100)}%");
            Say2("  读法：mean8/authored≈1 ⇒ 8-bit 是 authored；≥1.08 ⇒ 字节里已含一次纸白抬升。");
            Say2("        float/linear(authored)≈1 ⇒ Float16 在 80nits 参考白；≈k ⇒ Float16 已抬到系统纸白。");

            // ── 1. 建补丁窗（WS_POPUP 无边框 ⇒ 客户区≈窗口矩形；NOACTIVATE ⇒ 不动用户焦点）──
            nint hwnd = CreatePatchWindow(target.X + MonitorInset, target.Y + MonitorInset,
                                           PatchW, PatchH, out var wr, out string? winErr);
            if (hwnd == 0)
            {
                Say2($"★ {winErr} ⇒ 判为【没判】");
                return 1;
            }

            try
            {
                // 先把刷柄备好：WM_PAINT 在 s_brush==0 时会记一次绘制失败，而开窗后那次重绘
                // 根本还没到"该出血色"的时候 —— 不预置就等于给自己造一条假告警。
                SetPatchColor(0);

                // ── 2. 采样矩形以 GetWindowRect 的【实测】为准，不用我请求的那个 ──
                // 「CreateWindowEx 的坐标在 PMv2 进程里是否等于物理像素」在这一侧没有本机之外的证据；
                // 读回矩形并把"整块必须落在目标屏内"做成硬自检 ⇒ 几何错了会显形，不会悄悄采到桌面。
                bool inside = wr.Left >= target.X && wr.Top >= target.Y &&
                              wr.Right <= target.X + target.Width && wr.Bottom <= target.Y + target.Height;
                Say2($"  补丁实得矩形=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) " +
                                  $"{wr.Right - wr.Left}x{wr.Bottom - wr.Top} 落在目标屏内={inside}");
                if (!inside)
                {
                    Say2("★ 补丁矩形越出目标屏 ⇒ 采样几何不可信，判为【没判】");
                    return 1;
                }

                // 帧坐标 = 屏幕坐标 − 目标屏原点（CaptureMonitor 交回的是这一块屏自己的 surface）
                int insetX = (int)((wr.Right - wr.Left) * SampleInset);
                int insetY = (int)((wr.Bottom - wr.Top) * SampleInset);
                var sample = new RectI(wr.Left - target.X + insetX, wr.Top - target.Y + insetY,
                                       wr.Right - wr.Left - 2 * insetX, wr.Bottom - wr.Top - 2 * insetY);
                Say2($"  采样区(帧坐标)=({sample.X},{sample.Y}) {sample.W}x{sample.H}");
                if (sample.W < 40 || sample.H < 40)
                {
                    Say2("★ 采样区过小 ⇒ 判为【没判】");
                    return 1;
                }

                Pump(hwnd, 400);   // 首帧合成 + 会话启动的余量

                // ── 3. 捕获服务：与 --hdr-probe 同一条路（本模式跑在 AppServices.Initialize 之前）──
                Say2("  · 第 1 步：构造 WgcCaptureService（DXGI 适配器枚举 + D3D11 设备）");
                var cfg = new WgcCaptureConfig
                {
                    TargetMonitor = target.MonitorHandle,
                    PreferHdr = true,
                    FrameTimeoutMs = 4000,
                };
                var wgc = new WgcCaptureService();
                Say2("  · ✓ 服务已构造，开始对照测量（补丁隐藏）");

                // ── 4. 对照：补丁隐藏时同一块区域是什么样（同样要读到稳定，否则和后面的档不可比）──
                ShowWindow(hwnd, SW_HIDE);
                Pump(hwnd, SettleMs);
                double controlMean = double.NaN;
                var ctlOpt = MeasureStable(wgc, cfg, report, hwnd, sample);
                if (ctlOpt is not null)
                {
                    var ctl = ctlOpt.Value;
                    controlMean = ctl.Mean8;
                }
                Say2($"  对照（补丁隐藏）mean8 = " +
                                  (double.IsNaN(controlMean) ? "拿不到帧 ⇒ 落地性自检无从执行"
                                   : $"{controlMean:0.00}（复读 {ctlOpt!.Value.Tries} 次，稳定={ctlOpt.Value.Stable}）"));

                // ── 5. 逐档灰阶：画 → 读到【连续两次一致】为止 → 同一帧里同时取 8-bit 与 Float16 ──
                // 为什么不能"画完等一会儿读一次"：上一版实测 8-bit 会话比 Float16 会话【慢一整档】
                // （mean8 读到的全是上一档的颜色，而 float 已经是当前档）⇒ 两帧不是同一时刻的画面，
                // 任何按档对齐的比较都会得出假结论。稳定判据把这种错帧直接拦成"未稳定 ⇒ 不进判定"。
                ShowWindow(hwnd, SW_SHOW);
                var readings = new List<PatchReading>();
                bool sawFloat = false;
                Say2("");
                Say2("  authored   mean8    std8   float均值  float/linear  稳定  进判定");
                for (int gi = 0; gi < Grays.Length; gi++)
                {
                    int v = Grays[gi];
                    SetPatchColor((uint)((v << 16) | (v << 8) | v));
                    InvalidateRect(hwnd, 0, false);
                    UpdateWindow(hwnd);              // 同步派发 WM_PAINT：这一步颜色已画进窗口
                    Pump(hwnd, SettleMs);

                    var mOpt = MeasureStable(wgc, cfg, report, hwnd, sample);
                    if (mOpt is null)
                    {
                        Say2($"  {v,6}    —— 捕获未返回（第 {gi + 1}/{Grays.Length} 档）");
                        continue;
                    }
                    var m = mOpt.Value;
                    if (m.SdrMissing)
                    {
                        Say2($"  {v,6}    —— 8-bit 帧缺失（第 {gi + 1}/{Grays.Length} 档）");
                        continue;
                    }
                    sawFloat |= m.HasFloat;
                    double authoredLinear = PixelOps.SrgbToLinearLut[v];
                    double fRatio = m.HasFloat && authoredLinear > 0 ? m.FloatMean / authoredLinear : double.NaN;
                    bool used = m.Stable && v is >= SdrSessionSemantics.MidToneLow and <= SdrSessionSemantics.MidToneHigh;
                    Say2($"  {v,6}   {m.Mean8,7:0.00} {m.Std8,6:0.00}  " +
                        $"{(m.HasFloat ? m.FloatMean.ToString("0.0000") : "  n/a    ")}  " +
                        $"{(double.IsNaN(fRatio) ? "  n/a  " : fRatio.ToString("0.000"))}   " +
                        $"{(m.Stable ? $"是({m.Tries})" : $"★否({m.Tries})")}   {(used ? "是" : "否")}");
                    if (!m.Stable)
                        Say2($"        ★ 第 {v} 档复读 {m.Tries} 次仍不稳定 ⇒ 不进判定（两会话没落到同一张画面）");
                    if (m.MissPixels > 0)
                        Say2($"        ⚠ 采样区有 {m.MissPixels} 个像素越出帧外（已跳过）");
                    if (!used) continue;

                    readings.Add(new PatchReading(v, m.Mean8, m.Std8,
                        m.HasFloat ? m.FloatMean : 0.0,
                        double.IsNaN(controlMean) ? -999 : controlMean));
                }

                try { wgc.Dispose(); } catch { }
                if (s_paintErrors > 0)
                    Say2($"  ⚠ 补丁窗口绘制失败 {s_paintErrors} 次 ⇒ 屏幕上可能根本没有画出血色，" +
                                      "下面的读数若与对照同值属预期（自检会把它拦成「没判」）");
                if (!sawFloat)
                    Say2("  ⚠ Float16 全程未成帧 ⇒ float 那一侧本轮无读数（8-bit 的结论不受影响）");

                // ── 6. 判定：交回 Core 那把尺子，这里一个字结论都不下 ──
                var verdict = SdrSessionSemantics.Judge(readings, sdrWhiteNits);
                Say2("");
                Say2($"  自检通过 = {verdict.SelfCheckPassed}");
                Say2($"  读数 = {verdict.Why}");
                Say2($"  结论 8-bit = {verdict.EightBit}   Float16 = {verdict.Float16}");
                Say2($"  含义 = {SdrSessionSemantics.Implication(verdict)}");
                Console.WriteLine($"[sdr-semantics-probe] 8-bit={verdict.EightBit} float={verdict.Float16} " +
                                  $"mean8/authored={verdict.EightBitRatio:0.000} " +
                                  $"float/linear={verdict.FloatRatio:0.000} k={verdict.SdrWhiteScale:0.000}");

                swAll.Stop();
                Say2($"  耗时 = {swAll.ElapsedMilliseconds}ms（{Grays.Length} 次换色与捕获）");
                bool decisive = verdict.SelfCheckPassed && verdict.EightBit is not EightBitSemantics.Unknown;
                if (!decisive)
                    Say2("  ★ 本轮【没判】到 8-bit 语义 ⇒ 退出码 1。这不等于「兜底层没有过曝」，也不等于「字节有问题」。");
                return decisive ? 0 : 1;
            }
            finally
            {
                if (s_brush != 0) { DeleteObject(s_brush); s_brush = 0; }
                DestroyWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Say2($"★ 探针自身异常 {root.GetType().Name}: {root.Message}");
            Say2(root.StackTrace ?? "");
            return 1;
        }
    }

    private readonly record struct PatchMeasure(double Mean8, double Std8, double FloatMean,
        bool HasFloat, bool SdrMissing, int MissPixels, bool Stable, int Tries);

    /// <summary>
    /// 复读直到【连续两帧同区读数一致】—— 这是"两帧落在同一张画面上"的唯一可机器检验的替身。
    /// <para>不这么做的代价上一版已经付过一次：8-bit 会话比 Float16 慢一整档，按档对齐的比较
    /// 会把 authored 档读成上一档的字节，ratio 全是噪声。</para>
    /// <para>读不满时返回 Stable=false，调用方必须把这一档踢出判定（不许拿"最后一次读数"充数）。</para>
    /// </summary>
    private static PatchMeasure? MeasureStable(WgcCaptureService wgc, WgcCaptureConfig cfg,
        StringBuilder report, nint hwnd, RectI sample)
    {
        PatchMeasure prev = default;
        bool havePrev = false;
        for (int i = 1; i <= MaxStableTries; i++)
        {
            var cap = Capture(wgc, cfg, report);
            if (cap is null)
                return havePrev ? prev with { Stable = false, Tries = i } : null;
            PatchMeasure cur;
            try
            {
                if (cap.SdrPixels is null)
                    return new PatchMeasure(double.NaN, double.MaxValue, 0.0,
                        cap.HdrPixels is not null, SdrMissing: true, 0, false, i);
                var s = SampleRect(cap.SdrPixels, cap.HdrPixels, cap.Width, cap.Height, sample);
                cur = new PatchMeasure(s.Mean, s.Std, s.FloatMean,
                    cap.HdrPixels is not null, SdrMissing: false, s.MissPixels, false, i);
            }
            finally { cap.Dispose(); }

            if (havePrev &&
                Math.Abs(cur.Mean8 - prev.Mean8) < StableTol8 &&
                (!cur.HasFloat || !prev.HasFloat || Math.Abs(cur.FloatMean - prev.FloatMean) < StableTolFloat))
                return cur with { Stable = true };

            prev = cur;
            havePrev = true;
            if (i < MaxStableTries) Pump(hwnd, SettleMs);
        }
        return prev with { Stable = false, Tries = MaxStableTries };
    }

    private static void SetPatchColor(uint rgb)
    {
        var nb = CreateSolidBrush(rgb);
        if (nb == 0) return;
        if (s_brush != 0) DeleteObject(s_brush);
        s_brush = nb;
    }

    /// <summary>跑消息循环 <paramref name="ms"/> 毫秒 —— 换色后必须让线程回到消息循环，
    /// 否则补丁内容还没进 DWM 合成，WGC 读到的就是旧画面。</summary>
    private static void Pump(nint hwnd, int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(5);
        }
    }

    private static TrueToneCap.App.Models.CaptureResult? Capture(
        WgcCaptureService wgc, WgcCaptureConfig cfg, StringBuilder report)
    {
        try
        {
            var t = Task.Run(() => wgc.CaptureMonitorAsync(cfg));
            if (!t.Wait(TimeSpan.FromMilliseconds(CaptureWaitMs)))
            {
                report.AppendLine($"        ⚠ 捕获 {CaptureWaitMs / 1000}s 未返回");
                return null;
            }
            return t.Result;
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            report.AppendLine($"        ⚠ 捕获失败 {root.GetType().Name}: {root.Message}");
            return null;
        }
    }

    internal readonly record struct RectI(int X, int Y, int W, int H);

    internal readonly record struct SampleStats(double Mean, double Std, double FloatMean, int Pixels, int MissPixels);

    /// <summary>区域内灰度（三通道平均）统计；float 帧同样取三通道平均 —— 中性灰下通道顺序不影响结果。
    /// 对 A/B 也开放：两台仪器必须用同一把尺子量同一块像素。</summary>
    internal static SampleStats SampleRect(byte[] sdr, float[]? hdr, int w, int h, RectI r)
    {
        double sum = 0, sumSq = 0, fSum = 0;
        int n = 0, fN = 0, miss = 0;
        int x1 = r.X + r.W, y1 = r.Y + r.H;
        for (int y = r.Y; y < y1; y++)
        {
            if (y < 0 || y >= h) { miss += r.W; continue; }
            for (int x = r.X; x < x1; x++)
            {
                if (x < 0 || x >= w) { miss++; continue; }
                int i = (y * w + x) * 4;
                if (i + 3 >= sdr.Length) { miss++; continue; }
                double gray = (sdr[i] + sdr[i + 1] + sdr[i + 2]) / 3.0;
                sum += gray; sumSq += gray * gray; n++;
                if (hdr is not null && i + 3 < hdr.Length)
                {
                    fSum += (hdr[i] + hdr[i + 1] + hdr[i + 2]) / 3.0;
                    fN++;
                }
            }
        }
        double mean = n > 0 ? sum / n : double.NaN;
        double std = n > 1 ? Math.Sqrt(Math.Max(0.0, sumSq / n - mean * mean)) : double.MaxValue;
        return new SampleStats(mean, std, fN > 0 ? fSum / fN : 0.0, n, miss);
    }
}
