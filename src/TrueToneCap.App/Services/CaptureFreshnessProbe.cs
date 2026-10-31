// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/CaptureFreshnessProbe.cs
//
// 真机测量仪表：TrueToneCap.exe --capture-freshness-probe
//
// ── 它回答哪一个具体问题 ──
// 现在每次按热键都新建两条帧池（B8G8R8A8 + R16G16B16A16F）并各自阻塞等首帧，本机今天的中位是 479ms。
// "让两条池常驻、按下热键直接取已投递的最新一帧"能把这 479ms 省掉，但它撞上一条语义要求：
// 截图必须拍到用户按下热键【那一刻】的桌面。
//   怀疑方：常驻池里缓存的那一帧是几百毫秒前的画面 ⇒ 截图拍到过期内容。
//   反驳方：冷启动那 479ms 等的也正是"DWM 愿意把这一帧合成出来交给你"，两边受同一次合成延迟限制，
//           冷池并不保证更新。
// 两边都是推理，没有读数。本仪表只做一件事：把"截图的时间戳到底落后现实多少"量出来，
// 冷池臂与热池臂各测一遍，交回【分布】。哪个时限能接受、因此动不动那条常驻优化，是维护者拿分布做的决定，
// 本仪表不代判 —— 它连"好不好"都不说，只说"测到了没有"。
//
// ── 被测物：一扇每 100ms 自己变一次的刻度窗 ──
// 20 个大方格（5×4）做二进制编码：亮=1，格号 = 行优先，权重 = 2^格号 ⇒ 一个"刻度值"，每 100ms +1。
// 刻意不用文字/OCR：文字链路上任何一环（字体回退、DPI、 ClearType 合成、识别模型）出问题都会把判据
// 变成"探针自己坏了"，而这条判据要的是【机器可读的形状】。每格再画一个红色十字作定位标记：
// 解码器先拿十字定标（同一帧内的"亮参考"与"暗参考"）再判明暗，十字不在期望位置上就直接作废整帧 ——
// 这样"格子几何算错了/被别的窗挡住了/屏幕上根本没画出来"都会显形，而不是读出一个看似合理的错数。
// ⚠ 纪律：期望值取【窗口自己声称画了哪个值】（WM_PAINT 落地时写进 s_paintedValue），
//   不是程序推算"现在应该是第几格"。因为 WM_TIMER 落到 WM_PAINT 落到 DWM 合成，每一拍都可能被
//   UI 线程的别的事推迟；程序推算的"应该"比屏幕真正刷出来的可能晚一拍也可能早一拍，
//   两个方向的误差都会伪造出本仪表要测的那个量。
//
// ── 两台仪器（必须是两条真正分叉的实现） ──
// 参照仪器 = GDI：CreateDC("DISPLAY") + BitBlt(SRCCOPY) + GetDIBits 抓同一块矩形，解出刻度值。
//   选它是因为它是同步的"现在"，且与 WGC 没有任何共享代码路径 —— 拿 WGC 的帧去证明 WGC 的帧不叫参照。
//   ⚠ 只用 SRCCOPY，不加 CAPTUREBLT：刻度窗是不透明的普通 WS_POPUP 窗（不是分层窗），SRCCOPY 已经包含它；
//     而 CAPTUREBLT 在部分驱动上会整块返回黑。"到底包不包含这扇窗"由正控当场证明，不靠这里的断言。
// 被试仪器 = 产品自己的捕获路径：AppServices.Wgc 的 CaptureMonitorAsync（同一台仪器、同一条池、同一次
//   冷启动逻辑），取同一条矩形，解出刻度值。误差 = 参照值 − 被试值（正数 ⇒ WGC 拍到了过去），
//   换算毫秒按 ×100ms/格。
//   读数取 8-bit 那一池的帧：SdrSessionSemantics 那轮已经实测 8-bit 会话交回的是 authored sRGB、
//   逐枚 bit-exact ⇒ 解码它不需要再猜任何色彩变换。Float16 那一池的到货时刻由同一趟的捕获耗时记进读数。
// 一次试验里参照【抓两次】：捕获调用之前（≈用户按下热键的那一刻）与捕获返回之后。
//   err下界 = 之前那次 − 被试值（对"截图该对上按下那一刻"这条语义直接的读数，也是本仪表的主读数）；
//   err上界 = 之后那次 − 被试值（若你认为截图该对上 API 返回的时刻，冷臂要额外背整段建池耗时）。
//   符号两个方向都有意义，都不是"好"：正数=被试拍到【过去】的画面（热池最坏的那种形状）；
//   负数=被试交回【按下之后才合成】的画面（冷池的那一种）——"截图必须拍到按下那一刻"这两个方向都不满足，
//   差别只在超差是多少、以及这个超差是不是可以被工程手段压掉。
//   两条都报，是因为"哪个时刻才算数"本身就是待决的语义，仪表不该替维护者先把它钉成一个数。
//
// ── 两臂 ──
// 冷臂：每次试验前调 InvalidateSessions() 逼产品重建两条池并阻塞等首帧 ⇒ 就是今天热键走的那条路。
// 热臂：让池保持常驻，隔 HotGapMs 再取同一块屏的同一块矩形 ⇒ 拿到的是池里已经躺着的那一帧，
//       并把"这一帧已经存了多少毫秒"（PooledSession.GetFrameAge）一并记下来。
//       HotGapMs 必须远小于 IdleStopDelayMs(15000ms)：越过那条线，池会被产品自己的闲置回收撤掉，
//       热臂就悄悄变成第二条冷臂 —— 交错跑（冷、热、冷、热…）正是为了让这种退化当场可见：
//       热臂若出现冷臂量级的捕获耗时，表里那一行自己会说话。
// ⚠ 一条会被读反的地方，写在读表处也写在头里：刻度窗每 100ms 自己变一次 ⇒ 它本身就在持续制造 DWM 合成，
//   于是热臂量到的是"【画面在动的时候】常驻帧落后多少"。完全静止的桌面是另一种形状——产品自己的注释
//   实测静态桌面投递只有 ~1.2fps，那一形状下缓存帧的【年龄】能一路涨到闲置回收，但画面本来就一模一样，
//   语义并没有被破。所以"静止时年龄大"不等于"截图错"，"在动时年龄小"也不等于"截图对"：
//   能定罪的是本仪表这两条 Δ 分布，不是年龄那一列。
//
// ── 两条控制臂（缺一不许报结论） ──
// 正控【解码器读得对】：冻结成"全 1"必须解出 2^20−1；再只翻转第 7 格，必须解出正好差 128 的数。
//   两条都覆盖 GDI 与 WGC 两台仪器 —— 只测一台，另一台的读数就没人担保。
// 参照臂自证【参照仪器不漂】：冻结刻度，背靠背用 GDI 抓两次（中间不泵消息 ⇒ WM_TIMER 不可能推进刻度，
//   这就是"同一时刻"的构造性保证），两次解出的数必须相同、且必须等于窗口自报的那个值。
//   它不为 0 ⇒ 参照仪器自己在动 ⇒ 整张表作废。
// 这两条任一不过 ⇒ 当场返回非 0 并【不跑两臂】（表里的数全是垃圾，跑一遍只是让人误以为有分布可看）。
// 另有一条前置自证（刻度窗真的在自转），跑两遍：进场时一遍、两条控制臂收场并把窗放开之后一遍。
// 没有它：①"整轮里刻度值一直是同一个数"会被读成"WGC 很新鲜"；②控制臂是靠【冻结】刻度窗才判得动的，
// 忘了解开就两把仪器一起读同一个常数、Δ 恒为 0 —— 那张表看起来像"截图新鲜得无懈可击"，
// 是这台仪器最坏的一种假绿。两遍都判"自报值在不在动"，不判"动了几格"（动几格由 UI 线程忙闲决定，
// 不是被测对象的性质）。
//
// ── 退出码 ──
// 0 = 两条控制臂都过且两臂样本量撑得起分位数（【测出来了】，不代表"截图不旧"）；
// 1 = 任一控制臂没过 / 捕获拿不到帧 / 判定样本不足。
// 与 --window-probe / --sdr-semantics-probe / --overlay-whitelevel-ab 同一口径：退出码只说判没判。
//
// ── 接线（由维护者做，本文件不动 App.xaml.cs） ──
// 照 --overlay-whitelevel-ab 那段写法挂：RunAsync 必须跑在 UI 线程并【全程 await、绝不阻塞消息循环】——
// 刻度窗的 WM_TIMER/WM_PAINT 由这条线程泵出来，阻塞它等于让被测物停止变化。
// 请照抄那段用 DispatcherQueue.TryEnqueue + await，不要 ConfigureAwait(false)。
//
// ── 已知的不成立风险（写在这里，因为控制臂会把它们当场照出来） ──
// ① Win11 HDR 输出上 GDI 的屏直流可能交出被色调映射过（甚至全黑）的画面 ⇒ 十字定标的对比门限会红；
// ② 置顶窗是否被 WGC 正常投递：本机 --overlay-whitelevel-ab 已投过一次同款窗，但那是"两臂都在同一帧里"
//   的形状，本仪表要求的是"解出一个具体的数"，格子几何错一点点就作废 ⇒ 由正控的 WGC 那半条钉住。

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TrueToneCap.App.Models;      // CaptureResult
using TrueToneCap.Core.Capture;    // DisplayEnumerator / DisplayInfo
using TrueToneCap.Core.Logging;    // LogCategory

namespace TrueToneCap.App.Services;

// ⚠ partial 是必需的，不是风格：LibraryImport 要把实现生成到本类的另一个部分里，
//   类型不写 partial 直接是 SYSLIB1053 错误（SdrSemanticsProbe 那边同理）。
public static partial class CaptureFreshnessProbe
{
    public const string Flag = "--capture-freshness-probe";

    // ── 刻度窗 ──
    private const int Bits = 20;                              // 20 格二进制 ⇒ 一圈 2^20 格 ≈ 29 小时（一次仪表跑不完一圈，但判据仍按回绕算，见 SignedDelta）
    private const int Cols = 5, Rows = 4;
    private const int ValueMask = (1 << Bits) - 1;
    private const int TickMs = 100;                           // 每 100ms 一格：这就是"一格 ⇒ 多少毫秒"的那把尺
    private const int CellMax = 150;                          // 方格边长上限（物理像素）
    private const int CellMin = 44;                           // 再小就没法在合成取整之后还分得清明暗
    private const int EdgeReserve = 240;                      // 屏内留白：格子尺寸由"屏减去留白"推，判据不写死分辨率

    // ── 解码门限 ──
    private const double MinContrast = 60;                    // 十字定标的亮参考−暗参考 至少要这么大，否则整幅按"没画面"作废
    private const int RedMin = 25;                            // R − max(G,B) 超过它算"红"（十字）：白底 R−G≈0、黑底 R≈0，稳
    private const double FiducialMinFrac = 0.5;               // 十字带里至少一半像素认得出红 ⇒ 承认这一格的几何落在期望位置
    private const double AmbiguityFrac = 0.15;                 // 明暗判定离门限太近就不许猜：整帧作废

    // ── 取样与节奏 ──
    private const int TrialsPerArm = 20;
    private const int MinJudgedPerArm = 12;                   // 低于这个数，p95 没有支点 ⇒ 按"没判"处理
    private const int HotGapMs = 800;                         // 热臂取帧之前的空档；必须 ≪ IdleStopDelayMs(15000)，理由见文件头
    private const int SettleMs = 350;                         // 冻结换值后等 DWM 把这一格合成上屏（≈3 个刻度）
    private const int PollMs = 200;                           // 控制臂"等到证据出现为止"的轮询步长（与预览探针同一手法）
    private const int MaxPatternTries = 10;                   // 控制臂最多轮询这么多次；到点仍不等 ⇒ 判红（不是"跳过"）
    private const int FrameTimeoutMs = 4000;                  // 与现有三个探针同一档
    private const int CaptureWaitMs = 20_000;                 // 单次捕获的外层上限：会话起不来时不拖死仪表

    // ═══ Win32：建窗/绘制这一段照抄 SdrSemanticsProbe 的写法（同一套 WS_POPUP|WS_VISIBLE +
    // TOPMOST|TOOLWINDOW|NOACTIVATE、同一个 FillRect 配方、同一条 ValidateRect 纪律）。
    // 为什么不复用它的 CreatePatchWindow：它的 WndProc 只会 FillRect 一块纯色刷，而本仪表要被测物
    // 【自己按 100ms 变一个机器可读的图案】—— 图案必须长在 WndProc 里，所以只能同方子另开一扇。
    // 两处共用同一套 flag 与同一套绘制原语，"参照与产品看到的是同一张画面"这件事才有内容。
    private const string ClassName = "TtcCaptureFreshnessRuler";
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_ERASEBKGND = 0x0014;
    // 定时器 id：const nint 在 C# 里根本不合法（const 只能是基元/字符串/枚举那一族），
    // 所以这里存 int，在需要指针的两个调用点上用 new IntPtr(...) 明确造一枚句柄值，比较走 wParam.ToInt32()。
    private const int TimerId = 0xF2E5;   // 本进程内只此一扇用此 id

    // SetLastError = true：不写上它，下面这些失败行打印的 err= 可能是【上一条 API 留下的】陈旧值 ——
    // 探针读的就是这一列，陈旧错误码比没有错误码更坏。（照抄 SdrSemanticsProbe 的那批签名时补齐了这一维。）
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

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
    private static partial bool GetClientRect(nint hWnd, out SdrSemanticsProbe.RECT rc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out SdrSemanticsProbe.RECT rc);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    // FillRect 在 **user32** 不在 gdi32：写成 gdi32 时不是抛托管异常，而是 WM_PAINT 里
    // EntryPointNotFoundException 穿过原生边界 ⇒ 0xC0000409 fail-fast、日志一个字都不留（探针那边实测过）。
    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint hdc, ref SdrSemanticsProbe.RECT rc, nint brush);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("user32.dll")]
    private static partial nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hWnd, nint nIDEvent);

    // ── 参照仪器：GDI 屏直流 ──
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateDCW(string? driver, string? device, string? output, nint init);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy,
        nint srcDc, int srcX, int srcY, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint hbmp, uint startLine, uint lineCount,
        byte[] pixels, ref BITMAPINFO bmi, uint usage);

    private const uint SRCCOPY = 0x00CC0020, DIB_RGB_COLORS = 0;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;       // biHeight 取负 ⇒ 自上而下（与帧缓冲同一朝向）
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;              // BI_RGB 不读颜色表，这一枚只为把结构尺寸摆对
    }

    // ═══ 被测物的状态（进程内一扇在世；与 SdrSemanticsProbe 的 s_brush 同一取向，不做多实例）═══
    private static int s_forceValue = -1;               // ≥0 ⇒ 冻结画这个值（控制臂用）；-1 ⇒ 跟着刻度自转
    private static volatile int s_counter;              // 自由跑的刻度
    private static volatile int s_paintedValue;         // 窗口自报：这一帧我画的是哪个值
    private static long s_paintedTickMs;                // 以及什么时刻画的（C# 不许 long 用 volatile ⇒ 读写各走 Volatile.*）
    private static int s_cell;                          // 方格边长：WndProc 与解码器必须用同一个数
    private static int s_paintErrors;                   // 画不成的次数：异常穿不出原生边界，只能自己记账

    // WndProc 只碰 nint/struct、不分配托管对象 ⇒ 可以是 [UnmanagedCallersOnly]。
    // 刻度自转与绘制都发生在这扇窗的 owner 线程（= UI 线程），故 s_counter 的读写无跨线程竞争；
    // volatile 是给"探针在别的线程上读自报值"那条退路留的（见文件头接线段）。
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 这里抛出去的任何托管异常都穿不过原生边界 ⇒ 进程 fail-fast 且不留一行日志（上一版探针就是这么死的）。
        // 整段包起来，失败只加计数，由仪表主体把原因写进读数文件。
        try
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return 1;   // 背景由 WM_PAINT 亲手填满；让它擦只会制造闪烁，闪烁会被读成"画面变了"
                case WM_TIMER:
                    if (wParam.ToInt32() == TimerId)
                    {
                        // 冻结态也必须重画一遍：正控靠"值不动"成立，而"不动"要由这一拍真的画出来才算。
                        if (s_forceValue < 0) s_counter = (s_counter + 1) & ValueMask;
                        InvalidateRect(hWnd, 0, false);
                        UpdateWindow(hWnd);   // 同步派发 WM_PAINT：刻度值与"屏幕上真有这一格"之间不留异步缝
                        return 0;
                    }
                    break;
                case WM_PAINT:
                    // 无论画没画成都要清 update region：没走 BeginPaint 就没人清，WM_PAINT 会立刻再来一次，
                    // 消息循环空转、UI 线程被吃满 —— 上一轮探针"卡在某一档"就是这个形状。
                    // 所以 ValidateRect 放在 finally，而不是放进 PaintScale 的正常出口。
                    try { PaintScale(hWnd); }
                    finally { ValidateRect(hWnd, 0); }
                    return 0;
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

    /// <summary>画当前声称的刻度值：20 格本体（亮=1/暗=0）+ 每格一个红色十字定位标记，
    /// 并把"这一帧画的是哪个值、什么时刻"写进共享字段——判据只认这里，理由见文件头那条纪律。</summary>
    private static void PaintScale(nint hWnd)
    {
        if (s_cell <= 0) { s_paintErrors++; return; }
        if (!GetClientRect(hWnd, out var cli)) { s_paintErrors++; return; }
        var hdc = GetDC(hWnd);
        if (hdc == 0) { s_paintErrors++; return; }
        try
        {
            int cell = s_cell;
            int inset = cell / 5;                       // 本体取样与十字都在这块内缩区内：与 SdrSemanticsProbe.SampleRect 的 20% 同一口径
            int crossHalf = Math.Max(2, cell / 10);
            int v = s_forceValue >= 0 ? s_forceValue : s_counter;
            for (int i = 0; i < Bits; i++)
            {
                int x0 = (i % Cols) * cell, y0 = (i / Cols) * cell;
                var bodyRc = new SdrSemanticsProbe.RECT { Left = x0, Top = y0, Right = x0 + cell, Bottom = y0 + cell };
                nint body = CreateSolidBrush(((v >> i) & 1) == 1 ? 0x00FFFFFFu : 0x00000000u);
                // 刷柄拿不到就记失败并收手：FillRect 收到 NULL 刷是"什么都不画"，
                // 那一格会留着上一帧的颜色 —— 一种能同时骗过两台仪器的假一致，绝不能默默过去。
                if (body == 0) { s_paintErrors++; return; }
                FillRect(hdc, ref bodyRc, body);
                DeleteObject(body);

                nint cross = CreateSolidBrush(0x000000FFu);   // COLORREF = 0x00BBGGRR ⇒ 纯红；与明暗两个读数正交，专门用来定标与定位
                if (cross == 0) { s_paintErrors++; return; }
                int ccx = x0 + cell / 2, ccy = y0 + cell / 2;
                var vert = new SdrSemanticsProbe.RECT { Left = ccx - crossHalf, Top = y0 + inset, Right = ccx + crossHalf, Bottom = y0 + cell - inset };
                var horz = new SdrSemanticsProbe.RECT { Left = x0 + inset, Top = ccy - crossHalf, Right = x0 + cell - inset, Bottom = ccy + crossHalf };
                FillRect(hdc, ref vert, cross);
                FillRect(hdc, ref horz, cross);
                DeleteObject(cross);
            }
            // 自报写在最后一格画完之后：宁可让这一列偶尔偏后一帧，也不要出现"声称画完了、屏上只画了一半"
            s_paintedValue = v;
            Volatile.Write(ref s_paintedTickMs, Environment.TickCount64);
        }
        finally { ReleaseDC(hWnd, hdc); }   // update region 由调用方（WM_PAINT 分支）统一清
    }

    /// <summary>建刻度窗（固定位置、置顶、不抢焦点），并读回【实测】矩形——采样一律按实得的来，不按请求的来。
    /// unsafe 是 (delegate* unmanaged[Stdcall])&WndProc 那枚函数指针要的，不是可有可无的修饰。</summary>
    private static unsafe nint CreateRulerWindow(int x, int y, int w, int h,
        out SdrSemanticsProbe.RECT actual, out string? error)
    {
        actual = default;
        error = null;
        nint hInst = GetModuleHandleW(0);
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            // Win32 回调是 stdcall；delegate* unmanaged 默认 cdecl，显式写 [Stdcall] 才与 WndProc 的 CallConvs 匹配。
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = hInst,
            lpszClassName = classNamePtr,
        };
        ushort atom = RegisterClassExW(ref wc);
        Marshal.FreeHGlobal(classNamePtr);
        if (atom == 0) { error = $"RegisterClassExW 失败 err={Marshal.GetLastPInvokeError()}"; return 0; }

        nint hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, ClassName, WS_POPUP | WS_VISIBLE, x, y, w, h, 0, 0, hInst, 0);
        if (hwnd == 0) { error = $"CreateWindowExW 失败 err={Marshal.GetLastPInvokeError()}"; return 0; }
        if (!GetWindowRect(hwnd, out actual))
        {
            error = $"GetWindowRect 失败 err={Marshal.GetLastPInvokeError()} ⇒ 采样几何无从校验";
            DestroyWindow(hwnd);
            return 0;
        }
        SetTimer(hwnd, new IntPtr(TimerId), TickMs, 0);   // 回调为 0 ⇒ 走 WM_TIMER，由 owner 线程的消息泵派发
        return hwnd;
    }

    /// <summary>冻结或放开刻度值。放开前把 s_counter 对齐到刚画过的那个值 ⇒ 自转序列不断档
    /// （否则"从冻结值继续"会跳一格，把一格的假误差算进分布）。</summary>
    private static void SetFrozen(nint hwnd, int value)
    {
        s_forceValue = value;
        s_counter = value;
        InvalidateRect(hwnd, 0, false);
        UpdateWindow(hwnd);
    }

    private static void ReleaseRuler(nint hwnd)
    {
        if (hwnd == 0) return;
        KillTimer(hwnd, new IntPtr(TimerId));
        DestroyWindow(hwnd);
    }

    /// <summary>整轮试验的几何与仪器把手（一次算定，此后每个读数都用它，杜绝中途漂移）。
    /// <para>两套原点分开存，因为两台仪器的坐标空间本来就不同：SrcX/SrcY 是 GDI 屏直流的原点
    /// （= 主屏左上角）起的取源坐标，FrameX/FrameY 是 WGC 交回的那一块屏自己的 surface 坐标
    /// （= 屏幕坐标 − 目标屏原点，与 SdrSemanticsProbe 同一条换算）。本仪表把目标锁在主屏，两者恰好同值 ——
    /// 同值是【选屏的结果】，不是"它们是同一个东西"，所以不留成一个字段。</para></summary>
    private readonly record struct Ctx(nint ScreenDc, int SrcX, int SrcY, int W, int H, int FrameX, int FrameY);

    // ════════════════════════════════════════════════════════════════
    //  主体
    // ════════════════════════════════════════════════════════════════
    public static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        string artifact = ResolveArtifactPath($"capture_freshness_{DateTime.Now:yyyy-MM-dd}.log");
        void Say(string line)
        {
            sb.AppendLine(line);
            Console.WriteLine($"[freshness] {line}");
            // 逐行落盘，不攒到最后一次写：挂住/崩掉时"最后一行"就是现场（SdrSemanticsProbe 那条教训）。
            try { File.AppendAllText(artifact, line + "\n", new UTF8Encoding(false)); } catch { }
        }

        nint hwnd = 0;
        nint screenDc = 0;
        try
        {
            TryCreateDirectoryFor(artifact);
            File.WriteAllText(artifact, "", new UTF8Encoding(false));

            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            {
                Say("★ 调用线程没有 DispatcherQueue ⇒ 本仪表必须跑在 UI 线程（刻度窗的 WM_TIMER/WM_PAINT 靠这条线程泵出来）⇒ 不判");
                return 1;
            }
            var wgc = AppServices.Wgc;
            if (wgc is null) { Say("★ WGC 服务未初始化 ⇒ 不判"); return 1; }

            // ── 目标屏 = 主屏。为什么锁死主屏：参照仪器用的是 GDI 的 "DISPLAY" 直流，它覆盖的就是主屏那块
            //    表面（虚拟桌面的原点按定义在主屏左上角）；拿它去抓副屏会读到错位的东西，而错位读数和
            //    "截图过期"在表里长得一模一样，分不开。副屏要测就得换参照实现 —— 那是另一台仪器。
            var displays = DisplayEnumerator.EnumerateDisplaysUncached();
            var target = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (target is null) { Say("★ 无显示器 ⇒ 不判"); return 1; }
            if (!target.IsPrimary)
            { Say($"★ 只枚举到非主屏（{target.Name}）⇒ GDI 屏直流覆盖不到它，参照仪器无从成立，不判"); return 1; }

            // 格子尺寸按屏推、不写死：判据不许依赖某一台机器的分辨率。
            int cell = Math.Min(CellMax, Math.Max(CellMin,
                Math.Min((target.Width - 2 * EdgeReserve) / Cols, (target.Height - 2 * EdgeReserve) / Rows)));
            int winW = cell * Cols, winH = cell * Rows;
            if (winW > target.Width - 2 * EdgeReserve || winH > target.Height - 2 * EdgeReserve)
            {
                Say($"★ 屏太小（{target.Width}x{target.Height}）放不下 {winW}x{winH} 的刻度窗（每边留 {EdgeReserve}px，" +
                    $"格边下限 {CellMin}px）⇒ 被测物建不起来，不判");
                return 1;
            }
            // 位置一次算定并整轮不动（刻意居中：任何分辨率下都留得出边距，"固定"才是可验的）
            int px0 = target.X + (target.Width - winW) / 2, py0 = target.Y + (target.Height - winH) / 2;
            s_cell = cell;
            s_forceValue = 0;   // 先冻结成 0：刷柄/图案在第一次 WM_PAINT 就得是完整的，不留"空帧"假告警
            hwnd = CreateRulerWindow(px0, py0, winW, winH, out var wr, out string? winErr);
            if (hwnd == 0) { Say($"★ 刻度窗建不起来：{winErr} ⇒ 不判"); return 1; }

            GetClientRect(hwnd, out var cli);
            int cliW = cli.Right - cli.Left, cliH = cli.Bottom - cli.Top;
            int actualCell = Math.Min(cliW / Cols, cliH / Rows);
            Say($"{Flag}　参照仪器=GDI BitBlt 被试仪器=产品 CaptureMonitorAsync(8-bit 池)　两臂各 {TrialsPerArm} 次交错");
            Say($"  目标屏={target.Name} 原点=({target.X},{target.Y}) 尺寸={target.Width}x{target.Height} 主屏={target.IsPrimary}");
            Say($"  刻度窗实得矩形=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) 客户区={cliW}x{cliH} " +
                $"格边请求={cell} 客户区推出的格边={actualCell}");
            if (actualCell != cell)
            {
                Say($"★ 实得客户区推出的格边 ≠ 请求值（{actualCell} vs {cell}）⇒ 请求像素在 PMv2 进程里不等于物理像素，" +
                    "格子几何全是错的，不判");
                return 1;
            }
            if (cliW != wr.Right - wr.Left || cliH != wr.Bottom - wr.Top)
            { Say("★ 客户区 ≠ 窗口矩形（WS_POPUP 无边框的几何假设不成立）⇒ 两台仪器的取样原点会错位，不判"); return 1; }
            bool inside = wr.Left >= target.X && wr.Top >= target.Y &&
                          wr.Right <= target.X + target.Width && wr.Bottom <= target.Y + target.Height;
            if (!inside)
            { Say("★ 刻度窗矩形越出目标屏 ⇒ 采样几何不可信，不判"); return 1; }

            screenDc = CreateDCW("DISPLAY", null, null, 0);
            if (screenDc == 0)
            { Say($"★ CreateDCW(\"DISPLAY\") 失败 err={Marshal.GetLastPInvokeError()} ⇒ 参照仪器不存在，不判"); return 1; }

            var ctx = new Ctx(screenDc, wr.Left - target.X, wr.Top - target.Y, wr.Right - wr.Left, wr.Bottom - wr.Top,
                              wr.Left - target.X, wr.Top - target.Y);
            var cfg = new WgcCaptureConfig { TargetMonitor = target.MonitorHandle, PreferHdr = true, FrameTimeoutMs = FrameTimeoutMs };
            Say($"  取样矩形 屏内坐标=({ctx.FrameX},{ctx.FrameY}) {ctx.W}x{ctx.H}｜刻度一圈={ValueMask + 1} 格 = {(ValueMask + 1) * TickMs / 1000.0 / 3600.0:0.00} 小时");
            Say($"  误差口径：Δ=参照值−被试值，1 格 = {TickMs}ms；参照每轮抓两次：捕获之前(下界)/返回之后(上界)");
            Say("  符号读法：Δ>0 = 被试拍到【过去】的画面（热池要防的那一种）；Δ<0 = 被试交回【按下之后才合成】的画面" +
                "（冷池的那一种）——两个方向都不满足「拍到按下那一刻」，只是差多少、能不能压掉不一样");
            Say($"  热臂空档={HotGapMs}ms（必须远小于产品的 IdleStopDelayMs=15000ms，越过那条线池被闲置回收、热臂退化成冷臂）");

            // ── 前置自证：被测物真的在自己变 ──
            // 没有这一条，"整轮里刻度值一直是同一个数"会被读成"WGC 一拍就新鲜"——那是最像绿的假绿。
            // 先 SetFrozen(0) 再放开：这一步把内部刻度对回 0（SetFrozen 会同时写 s_counter），
            // 于是自转序列从刚画过的那个值继续，不会在放开的一瞬跳一格。
            SetFrozen(hwnd, 0);
            s_forceValue = -1;
            if (!await ConfirmRotatingAsync(Say, "前置自证·进场")) return 1;

            // ── 控制臂一：正控（解码器读得对） ──
            bool positiveOk = await RunPositiveControlAsync(wgc, cfg, ctx, hwnd, Say);
            // ── 控制臂二：参照臂自证（同一时刻 GDI 抓两次误差恒为 0） ──
            bool rulerOk = await RunRulerSelfCheckAsync(ctx, hwnd, Say);

            Say("");
            if (!positiveOk || !rulerOk)
            {
                Say($"★ 控制臂没过（正控={positiveOk} 参照自证={rulerOk}）⇒ 本次【不判】，退出码非 0，" +
                    "且【不跑两臂】：判据本身不可信时跑出来的分布只会让人误以为有证据。");
                return 1;
            }
            Say("  ✓ 两条控制臂都过 ⇒ 下面的分布有判据可依赖；这两条任一不过即本次不判（见各自读数行）。");

            // ⚠ 两条控制臂都是把窗【冻住】跑的，收场时窗还停在最后那个校验值上。不放开就整轮 Δ 恒为 0 ——
            //   屏幕上根本没在动，两台仪器一起读同一个常数，那张表看起来像"截图新鲜得无懈可击"，
            //   是这台仪器最坏的一种假绿。放开之后再自证一次"它又动了"，不靠记忆也不靠注释。
            s_forceValue = -1;
            if (!await ConfirmRotatingAsync(Say, "前置自证·控制臂之后")) return 1;

            // ── 两臂交错跑 ──
            var cold = new List<Trial>();
            var hot = new List<Trial>();
            Say("");
            Say($"  每行读数的成立条件：参照之前/参照之后/被试 三处【都】解出刻度才记 Δ；任一处没解出 ⇒ 该行标 ★ 并写明卡在哪，不进分布。");
            Say($"  列义：claim=窗口自报值  参照前/后=GDI 抓两次解出的值  被试=WGC 帧解出的值  Δ下=参照前−被试  Δ上=参照后−被试");
            Say("        「画于 N ms 前」= 自报那一格【最后一次被画出来】距今多久：无关的重绘（例如被别的窗揭开）" +
                "也会刷新它，所以它只是现场参考，不参与任何判据 —— 参与判据的只有三个解出的刻度值。");
            for (int k = 0; k < TrialsPerArm; k++)
            {
                var tc = await RunTrialAsync(wgc, cfg, ctx, cold: true);
                cold.Add(tc);
                PrintTrial(Say, "冷", k, tc);
                var th = await RunTrialAsync(wgc, cfg, ctx, cold: false);
                hot.Add(th);
                PrintTrial(Say, "热", k, th);
            }

            if (s_paintErrors > 0)
                Say($"  ⚠ 刻度窗绘制失败 {s_paintErrors} 次（异常穿不出原生边界，只能这样记账；已稳定的读数不受影响，但说明这一轮有帧没画出来）");

            // ── 分布 ──
            Say("");
            int coldJudged = ReportArm(Say, "冷臂（每轮先 InvalidateSessions 再取帧 = 今天热键走的那条路）", cold, withAge: false);
            int hotJudged = ReportArm(Say, "热臂（池常驻，隔 " + HotGapMs + "ms 取同一块屏 = 想优化的那条路）", hot, withAge: true);

            Say("");
            bool enough = coldJudged >= MinJudgedPerArm && hotJudged >= MinJudgedPerArm;
            Say($"  样本量：冷={coldJudged}/{TrialsPerArm} 热={hotJudged}/{TrialsPerArm} ⇒ 每臂至少 {MinJudgedPerArm} 条才判" +
                $"（p95 需要支点；成立条件=两臂判定数都到线，否则退出码非 0）");
            if (!enough)
                Say("  ★ 样本不足 ⇒ 本次【不判】。这不等于「截图旧」，也不等于「截图不旧」，" +
                    "只等于这台仪器这次没量出可比的分布。");
            else
                Say("  ✓ 本次【判到了】= 两臂的误差分布都量出来了。退出码 0 担保的只有这一句，" +
                    "它不担保截图不旧，也不担保常驻池可以拿：那要拿上面的分布去和维护者心里的时限对账。");

            var coldP = Percentile(JudgedErrs(cold, true), 0.5);
            var hotP = Percentile(JudgedErrs(hot, true), 0.5);
            LogService.Info("CaptureFreshness",
                $"两臂各 {TrialsPerArm} 次（判定 冷{coldJudged}/热{hotJudged}）；Δ下界 p50：冷={(double.IsNaN(coldP) ? "n/a" : $"{coldP:0.0} 格")} " +
                $"热={(double.IsNaN(hotP) ? "n/a" : $"{hotP:0.0} 格")}；{(TickMs)}ms/格；判={enough}；读数={artifact}",
                LogCategory.Capture);
            return enough ? 0 : 1;
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Say($"★ 自身异常 {root.GetType().Name}: {root.Message}");
            Say(root.StackTrace ?? "");
            return 1;
        }
        finally
        {
            if (hwnd != 0) { try { ReleaseRuler(hwnd); } catch { } }
            if (screenDc != 0) { try { DeleteDC(screenDc); } catch { } }
            s_cell = 0;
            s_forceValue = -1;
            try
            {
                File.WriteAllText(artifact, sb.ToString(), new UTF8Encoding(false));
                Console.WriteLine($"[freshness] 读数已落盘: {artifact}");
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  控制臂
    // ════════════════════════════════════════════════════════════════

    /// <summary>确认被测物正在自己变（跑一遍 3 格 + 余量的观察窗）。本文件里跑两遍：进场时一遍，
    /// 控制臂收场、放开冻结之后一遍 —— 判据是"窗口自报的值在动"，不是"我调用过 SetTimer"。
    /// <para>为什么值不判"动了多少格"：这条自证只管"在不在动"。动了 2 格还是 4 格由消息泵的忙闲决定，
    /// 拿它当判据等于把 UI 线程的抖动算进被测对象的性质里。</para></summary>
    private static async Task<bool> ConfirmRotatingAsync(Action<string> Say, string when)
    {
        const int ObserveMs = 3 * TickMs + 120;
        int a0 = s_paintedValue;
        await Task.Delay(ObserveMs);
        int a1 = s_paintedValue;
        if (a0 == a1)
        {
            Say($"★ {when}：{ObserveMs}ms 内自报值停在 {a0} ⇒ 被测物没有在动（冻结没放开，或这条线程的消息泵" +
                $"没派发 WM_TIMER），绘制失败计数={s_paintErrors} ⇒ 不判");
            return false;
        }
        Say($"  ✓ {when}：{ObserveMs}ms 内自报 {a0}→{a1}（每 {TickMs}ms 一格）｜成立条件=窗口自己画的值真的在推进");
        return true;
    }

    /// <summary>正控：证明解码器读得对。先画"全 1"断言解出该值，再只翻转第 7 格断言差值正好是那一格的权重。
    /// <para>GDI 与 WGC 两台仪器各判一遍 —— 只担保一台，另一台的读数就是没根的。</para>
    /// <para>这一相把刻度窗【冻结】：冻结之后"画面内容"不再是变量，判据才只剩解码器与几何一条。</para>
    /// <para>为什么是"轮询到两处都到位、超时仍红"而不是"抓一次比对一次"：常驻池里那一帧【可能还是翻转之前】
    /// 的那一张（静态桌面实测投递只有 ~1.2fps），抓一次就判会把好机器读成假红。轮询只加"时间"这一维，
    /// 判据本体没动 —— 值不等就是一直不等，到点判红，与预览探针"等证据出现为止"同一条纪律。</para></summary>
    private static async Task<bool> RunPositiveControlAsync(WgcCaptureService wgc, WgcCaptureConfig cfg,
        Ctx ctx, nint hwnd, Action<string> Say)
    {
        const int Flip = 7;                       // 翻转哪一格是任取的；判据钉的是"差值 = 2^7"，不是这一格特别重要
        int all = ValueMask, flipped = ValueMask ^ (1 << Flip);
        Say("");
        Say($"  控制臂【正控】：全 1 必须解出 {all}，只翻第 {Flip} 格必须正好差 {1 << Flip}；成立条件=两台仪器各自都满足这两条，否则本次不判");

        var (g1, w1, claim1, ms1, note1) = await WaitForPatternAsync(wgc, cfg, ctx, hwnd, all);
        var (g2, w2, claim2, ms2, note2) = await WaitForPatternAsync(wgc, cfg, ctx, hwnd, flipped);

        bool gdiOk = g1.Ok && g2.Ok && g1.Value == all && g2.Value == flipped && g1.Value - g2.Value == (1 << Flip);
        bool wgcOk = w1.Ok && w2.Ok && w1.Value == all && w2.Value == flipped && w1.Value - w2.Value == (1 << Flip);
        // 冻结值必须就是窗口自报值：否则正控测的根本不是"画上去的那个图案"，两台仪器一起读到一个没画过的数也能绿
        bool claimOk = claim1 == all && claim2 == flipped;

        Say($"    GDI  全1→{Show(g1)}｜翻第{Flip}格→{Show(g2)}｜差值={(g1.Ok && g2.Ok ? (g1.Value - g2.Value).ToString() : "n/a")}" +
            $"（期望 {1 << Flip}）⇒ {(gdiOk ? "✓" : "★")}");
        Say($"    WGC  全1→{Show(w1)}｜翻第{Flip}格→{Show(w2)}｜差值={(w1.Ok && w2.Ok ? (w1.Value - w2.Value).ToString() : "n/a")}" +
            $"（期望 {1 << Flip}）⇒ {(wgcOk ? "✓" : "★")}");
        Say($"    窗口自报 全1={claim1} 翻转={claim2}（期望 {all}/{flipped}）⇒ {(claimOk ? "✓" : "★ 自报值与冻结值不一致：WM_PAINT 没按请求落地")}");
        Say($"    到位过程：全1 {note1}（那次捕获 {ms1}ms）｜翻转 {note2}（那次捕获 {ms2}ms）" +
            $"｜成立条件=两台仪器在 {MaxPatternTries} 次 × {PollMs}ms 内【同时】解出冻结值；到不了就判红");
        bool ok = gdiOk && wgcOk && claimOk;
        Say(ok ? "    ✓ 正控通过：格子几何、通道顺序、明暗定标与权重映射这一条链是通的"
               : "    ★ 正控未过 ⇒ 解码器或取样几何不可信，整张表作废");
        return ok;
    }

    /// <summary>把刻度窗冻到 expected 上，轮询到 GDI 与 WGC 两台仪器【都】解出该值为止（有界）。
    /// 交回最后一次的读数与窗口自报值，供调用方做等值与差值判定。</summary>
    private static async Task<(Dec Gdi, Dec Wgc, int Claim, long CaptureMs, string Note)> WaitForPatternAsync(
        WgcCaptureService wgc, WgcCaptureConfig cfg, Ctx ctx, nint hwnd, int expected)
    {
        SetFrozen(hwnd, expected);
        await Task.Delay(SettleMs);              // 先给 DWM 一拍把这一格合成上屏，省掉一次注定空的轮询
        Dec g = default, w = default;
        int claim = s_paintedValue, tries = 0;
        long captureMs = 0;
        string lastWhy = "";
        for (; tries < MaxPatternTries; tries++)
        {
            claim = s_paintedValue;
            g = GdiDecode(ctx);
            var (cap, ms, err) = await CaptureTimedAsync(wgc, cfg);
            captureMs = ms;
            try { w = WgcDecode(cap, ctx); } finally { try { cap?.Dispose(); } catch { } }
            bool hit = g.Ok && g.Value == expected && w.Ok && w.Value == expected;
            if (hit) return (g, w, claim, ms, $"第 {tries + 1} 次到位");
            lastWhy = !g.Ok ? $"GDI：{g.Why}" : !w.Ok ? $"WGC：{w.Why}{(err is null ? "" : "；" + err)}"
                : $"GDI={g.Value} WGC={w.Value}（都不等于 {expected}）";
            await Task.Delay(PollMs);
        }
        return (g, w, claim, captureMs, $"轮询 {MaxPatternTries} 次仍未到位：{lastWhy}");
    }

    /// <summary>参照臂自证：同一时刻 GDI 抓两次，解出的数必须恒等，且必须等于窗口自报（也就是冻结）的那个值。
    /// <para>为什么"背靠背不泵消息"就能算同一时刻：刻度只在 WM_TIMER→WM_PAINT 这条路上推进，
    /// 两次抓取之间不回到消息循环，那条路就没有机会走一格 ⇒ 两次解出的数若不相等，动的不是画面，
    /// 是参照仪器自己（BitBlt/GetDIBits 与显示表面之间还隔着什么、或者驱动给了不一致的答复）。</para>
    /// <para>前面那段轮询只是等这一格【上屏】；被判定的是最后那一对背靠背抓取，它们之间不夹任何等待。</para></summary>
    private static async Task<bool> RunRulerSelfCheckAsync(Ctx ctx, nint hwnd, Action<string> Say)
    {
        const int Freeze = 0b00000_10101_10101_01010;   // 明暗交替：让"读错行/读错列"也能在数值上显形，而不是恰好对上
        Say("");
        Say($"  控制臂【参照自证】：冻结在 {Freeze} 后背靠背 GDI 抓两次 ⇒ 两次必须相同【且】等于窗口自报值；" +
            "不为 0 说明参照仪器自己在漂，整张表作废");
        SetFrozen(hwnd, Freeze);
        int settledAfter = 0;
        Dec probe = default;
        for (; settledAfter < MaxPatternTries; settledAfter++)
        {
            probe = GdiDecode(ctx);
            if (probe.Ok && probe.Value == Freeze) break;
            await Task.Delay(PollMs);   // 等的是"这一格合成上屏了"，不是"仪器不漂"——后者只由下面那一对抓取判
        }
        int claim = s_paintedValue;
        var a = GdiDecode(ctx);
        var b = GdiDecode(ctx);           // 上面两行之间没有任何 await/消息泵：见注释
        int delta = a.Ok && b.Ok ? SignedDelta(a.Value, b.Value) : int.MinValue;
        string deltaTxt = a.Ok && b.Ok ? delta.ToString() : "n/a";
        bool equal = a.Ok && b.Ok && delta == 0;
        bool matchesClaim = a.Ok && b.Ok && a.Value == claim && b.Value == claim && claim == Freeze;
        Say($"    上屏轮询 {settledAfter + 1} 次{(probe.Ok && probe.Value == Freeze ? "已到位" : $"仍未到位：{(probe.Ok ? $"解出 {probe.Value}≠{Freeze}" : probe.Why)}")}" +
            "｜成立条件=到位轮询的等待不算进「同一时刻」，判据只看下面那一对背靠背抓取");
        Say($"    第一次={Show(a)} 第二次={Show(b)} 差={deltaTxt} 格 ⇒ {(equal ? "✓ 恒为 0" : "★ 参照仪器在漂")}");
        Say($"    窗口自报={claim}（期望 {Freeze}）｜两次抓取都解出该值={matchesClaim} ⇒ {(matchesClaim ? "✓ 参照读到的就是屏幕上那一格画面" : "★ 参照与自报不符：可能抓黑/抓旧/几何错位")}");
        bool ok = equal && matchesClaim;
        Say(ok ? "    ✓ 参照臂自证通过" : "    ★ 参照臂自证未过 ⇒ 本次不判");
        return ok;
    }

    // ════════════════════════════════════════════════════════════════
    //  单次试验
    // ════════════════════════════════════════════════════════════════

    /// <summary>一次试验的全部读数。字段顺序就按读表时眼睛走的顺序排（判没判 → 两个 Δ → 三个原始值 →
    /// 时间列 → 说明），构造处用注释逐一对位，免得日后加字段时把数字接错串。</summary>
    private readonly record struct Trial(
        bool Judged,            // 三处（参照前/参照后/被试）都解出刻度
        int DeltaLoTicks,       // Δ下界 = 参照前 − 被试（格）
        int DeltaHiTicks,       // Δ上界 = 参照后 − 被试（格）
        int RefBefore,          // GDI 在捕获调用之前解出的值
        int RefAfter,           // GDI 在捕获返回之后解出的值
        int WgcValue,           // 产品 WGC 帧解出的值
        long CaptureMs,         // CaptureMonitorAsync 从调用到交回 8-bit 帧
        long? AgeMs,            // 常驻池里那一帧的年龄（冷臂为 null）
        string AgeNote,         // 年龄读不到时的说明（冷臂留空，不占版面）
        int Claim,              // 窗口自报的刻度值
        long ClaimAgeMs,        // 自报那一格画出来距今多少毫秒
        string Why);            // 未判定的原因 / 判定行的附加条件

    /// <summary>冷臂=先 InvalidateSessions 逼重建，再走产品入口；热臂=保持常驻，隔空档直接取。
    /// 除这一条之外，两臂的取样动作【逐字相同】——否则差别就说不清是谁带来的。</summary>
    private static async Task<Trial> RunTrialAsync(WgcCaptureService wgc, WgcCaptureConfig cfg, Ctx ctx, bool cold)
    {
        long? age = null;
        string ageNote = "";
        if (cold)
        {
            wgc.InvalidateSessions();
            // 诊断进程里顺带把 Float16 软冷却额度与 HDR 能力缓存清了，还会 NotifySharedDeviceDisposed：
            // 本进程跑完就退，这些副作用没有下游消费者；产品里这条只在显示器配置变更时调。
        }
        else
        {
            await Task.Delay(HotGapMs);
            var (a, note) = ReadResidentFrameAgeMs(wgc, cfg.TargetMonitor);
            age = a;
            ageNote = note;
        }

        // 自报值与"自报那一格画出来多久了"都在抓取之前先取：事后取到的是被这一轮自己拖过去的时间。
        int claim = s_paintedValue;
        long claimAge = Environment.TickCount64 - Volatile.Read(ref s_paintedTickMs);
        var refBefore = GdiDecode(ctx);                    // ≈按下热键的那一刻
        var (cap, captureMs, capErr) = await CaptureTimedAsync(wgc, cfg);
        Dec wgcVal = default;
        try { wgcVal = WgcDecode(cap, ctx); } finally { try { cap?.Dispose(); } catch { } }
        var refAfter = GdiDecode(ctx);                     // ≈API 交回画面的那一刻

        if (!refBefore.Ok || !refAfter.Ok || !wgcVal.Ok)
        {
            string why = !refBefore.Ok ? $"参照前未解出（{refBefore.Why}）"
                       : !refAfter.Ok ? $"参照后未解出（{refAfter.Why}）"
                       : $"被试未解出（{wgcVal.Why}{(capErr is null ? "" : "；" + capErr)}）";
            return new Trial(false, 0, 0, refBefore.Ok ? refBefore.Value : -1, refAfter.Ok ? refAfter.Value : -1,
                wgcVal.Ok ? wgcVal.Value : -1, captureMs, age, ageNote, claim, claimAge, why);
        }
        return new Trial(true,
            SignedDelta(refBefore.Value, wgcVal.Value), SignedDelta(refAfter.Value, wgcVal.Value),
            refBefore.Value, refAfter.Value, wgcVal.Value, captureMs, age, ageNote, claim, claimAge,
            capErr ?? "");
    }

    private static void PrintTrial(Action<string> Say, string arm, int index, Trial t)
    {
        string ageTxt = t.AgeMs is not null ? $"年龄={t.AgeMs}ms"
            : t.AgeNote.Length == 0 ? "年龄=冷臂不测"
            : $"年龄=无（{t.AgeNote}）";
        if (!t.Judged)
        {
            Say($"    {arm} #{index + 1:D2} ★ 未判定：{t.Why}（claim={t.Claim} 捕获={t.CaptureMs}ms {ageTxt}）" +
                "｜成立条件=参照前/参照后/被试三处都解出刻度");
            return;
        }
        Say($"    {arm} #{index + 1:D2} Δ下={t.DeltaLoTicks:+0;-0;0}格({t.DeltaLoTicks * TickMs}ms) " +
            $"Δ上={t.DeltaHiTicks:+0;-0;0}格({t.DeltaHiTicks * TickMs}ms) " +
            $"参照前={t.RefBefore} 参照后={t.RefAfter} 被试={t.WgcValue} claim={t.Claim}（画于 {t.ClaimAgeMs}ms 前）" +
            $" 捕获={t.CaptureMs}ms {ageTxt}｜成立条件=三处同一把尺各解出一次{(t.Why.Length == 0 ? "" : "；捕获有话：" + t.Why)}");
    }

    /// <summary>捕获 + 外层超时。为什么不当场阻塞等：阻塞会把这条线程的消息泵停住，
    /// 被测物就停止变化，测出来的时效全是探针自己造的（SdrSemanticsProbe 在自己的线程里泵，
    /// 本仪表跑在 UI 线程、靠 await 交还控制权，同一件事只能这么做）。</summary>
    private static async Task<(CaptureResult? Cap, long Ms, string? Why)> CaptureTimedAsync(
        WgcCaptureService wgc, WgcCaptureConfig cfg)
    {
        var sw = Stopwatch.StartNew();
        var t = wgc.CaptureMonitorAsync(cfg);
        var done = await Task.WhenAny(t, Task.Delay(CaptureWaitMs));
        if (done != t)
        {
            // 迟到的那一帧仍要归还（里面有 staging 纹理与复用缓冲），但不能让仪表等它。
            // 显式给 TaskScheduler.Default：不写就是 TaskScheduler.Current，而 Current 由调用处决定，
            // 在 UI 线程上可能把这段清理塞回这条线程的队列里（正是本文件一直躲开的那种"探针自己造延迟"）。
            _ = t.ContinueWith(static x =>
            {
                try { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); } catch { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return (null, sw.ElapsedMilliseconds, $"捕获 {CaptureWaitMs}ms 未返回");
        }
        try
        {
            var cap = await t;
            return (cap, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            return (null, sw.ElapsedMilliseconds, $"捕获异常 {root.GetType().Name}: {root.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  两台仪器的取帧与解码
    // ════════════════════════════════════════════════════════════════

    private readonly record struct Dec(bool Ok, int Value, string Why);

    private static string Show(Dec d) => d.Ok ? $"{d.Value}" : $"★{d.Why}";

    /// <summary>参照仪器：GDI 抓刻度窗那块矩形并解出刻度值。</summary>
    private static Dec GdiDecode(Ctx ctx)
    {
        if (ctx.ScreenDc == 0) return new Dec(false, -1, "GDI 屏直流未开");
        int w = ctx.W, h = ctx.H;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return new Dec(false, -1, $"抓取矩形尺寸不对（{w}x{h}）");
        nint mem = CreateCompatibleDC(ctx.ScreenDc);
        if (mem == 0) return new Dec(false, -1, $"CreateCompatibleDC 失败 err={Marshal.GetLastPInvokeError()}");
        nint bmp = CreateCompatibleBitmap(ctx.ScreenDc, w, h);
        if (bmp == 0)
        {
            DeleteDC(mem);
            return new Dec(false, -1, $"CreateCompatibleBitmap 失败 err={Marshal.GetLastPInvokeError()}");
        }
        nint oldBmp = 0;
        try
        {
            oldBmp = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, ctx.ScreenDc, ctx.SrcX, ctx.SrcY, SRCCOPY))
                return new Dec(false, -1, $"BitBlt 失败 err={Marshal.GetLastPInvokeError()}");
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,           // 负高度 = 自上而下，与 WGC 帧缓冲同一朝向 ⇒ 解码器只写一份
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,       // BI_RGB
                }
            };
            var px = new byte[w * h * 4];
            int got = GetDIBits(mem, bmp, 0, (uint)h, px, ref bmi, DIB_RGB_COLORS);
            if (got != h) return new Dec(false, -1, $"GetDIBits 只交出 {got}/{h} 行");
            return DecodeScale(px, w, h, 0, 0);
        }
        finally
        {
            // 复位 → 删位图 → 删 DC：这一整轮要抓 80+ 次，漏一次就是几百枚 GDI 句柄，
            // 而 GDI 句柄耗尽在这台机器上的表现是"后面的 BitBlt 全部静默失败"，很容易被读成"屏幕没画面"。
            if (oldBmp != 0) SelectObject(mem, oldBmp);
            DeleteObject(bmp);
            DeleteDC(mem);
        }
    }

    /// <summary>被试仪器：产品交回的整屏 8-bit 帧里解出刻度值。
    /// 帧坐标 = 屏幕坐标 − 目标屏原点（CaptureMonitor 交回的是这一块屏自己的 surface，与探针同一换算）。</summary>
    private static Dec WgcDecode(CaptureResult? cap, Ctx ctx)
    {
        if (cap is null) return new Dec(false, -1, "捕获没交回结果");
        if (cap.SdrPixels is null) return new Dec(false, -1, "8-bit 帧缺失");
        return DecodeScale(cap.SdrPixels, cap.Width, cap.Height, ctx.FrameX, ctx.FrameY);
    }

    /// <summary>把一块 BGRA 缓冲里的 20 格图案解成刻度值。两台仪器共用这一个函数 ——
    /// "两台仪器用同一把尺子"不是姿态：判据要能红，就得保证差异只可能来自画面，不来自解码。</summary>
    private static Dec DecodeScale(byte[] px, int frameW, int frameH, int ox, int oy)
    {
        int cell = s_cell;
        if (cell <= 0) return new Dec(false, -1, "格边长未定");
        if (px.Length < (long)frameW * frameH * 4)
            return new Dec(false, -1, $"缓冲只有 {px.Length}B，帧是 {frameW}x{frameH}");
        if (ox < 0 || oy < 0 || ox + Cols * cell > frameW || oy + Rows * cell > frameH)
            return new Dec(false, -1, $"取样矩形越出帧（原点({ox},{oy}) 格{cell}px，帧 {frameW}x{frameH}）");

        int inset = cell / 5;
        int crossHalf = Math.Max(2, cell / 10);
        var bodyMean = new double[Bits];
        var redFrac = new double[Bits];
        double hiRef = 0, loRef = 0;

        for (int i = 0; i < Bits; i++)
        {
            int x0 = ox + (i % Cols) * cell + inset, x1 = ox + (i % Cols + 1) * cell - inset;
            int y0 = oy + (i / Cols) * cell + inset, y1 = oy + (i / Cols + 1) * cell - inset;
            int ccx = ox + (i % Cols) * cell + cell / 2, ccy = oy + (i / Cols) * cell + cell / 2;
            double bodySum = 0, crossR = 0, crossGB = 0;
            int bodyN = 0, crossN = 0, redN = 0;
            for (int y = y0; y < y1; y += 2)
            {
                bool inH = Math.Abs(y - ccy) < crossHalf;
                int rowBase = y * frameW;
                for (int x = x0; x < x1; x += 2)
                {
                    int p = (rowBase + x) * 4;
                    int b = px[p], g = px[p + 1], r = px[p + 2];   // BGRA，与产品 8-bit 帧同一种布局（预览探针 IsBlue 那条也是这个顺序）
                    if (Math.Abs(x - ccx) < crossHalf || inH)
                    {
                        crossR += r; crossGB += (g + b) / 2.0; crossN++;
                        if (r - Math.Max(g, b) > RedMin) redN++;
                    }
                    else { bodySum += (r + g + b) / 3.0; bodyN++; }
                }
            }
            if (crossN == 0 || bodyN == 0)
                return new Dec(false, -1, $"第 {i} 格取样退化（十字 {crossN} 点/本体 {bodyN} 点，格边 {cell}px）");
            bodyMean[i] = bodySum / bodyN;
            redFrac[i] = (double)redN / crossN;
            hiRef += crossR / crossN;
            loRef += crossGB / crossN;
        }
        hiRef /= Bits; loRef /= Bits;
        double contrast = hiRef - loRef;
        // 定标用【同一帧里的十字】而不是写死的 128：GDI 在 HDR 输出上可能交出被抬或被压的一版，
        // 写死门限会把"整体偏暗"读成"所有格子都是 0"——那是最坏的假绿（两臂同错，误差恰好为 0）。
        if (contrast < MinContrast)
            return new Dec(false, -1, $"十字定标无对比（亮参考={hiRef:0} 暗参考={loRef:0} 差={contrast:0} < {MinContrast}）" +
                                      "⇒ 这块画面在屏幕上根本没被抓到（HDR 输出下 GDI 交黑就是这个形状）");

        double thr = (hiRef + loRef) / 2.0, amb = AmbiguityFrac * contrast;
        int value = 0, fidFail = 0, ambiguous = 0;
        for (int i = 0; i < Bits; i++)
        {
            if (redFrac[i] < FiducialMinFrac) { fidFail++; continue; }
            double d = bodyMean[i] - thr;
            if (Math.Abs(d) < amb) { ambiguous++; continue; }
            if (d > 0) value |= 1 << i;
        }
        if (fidFail > 0)
            return new Dec(false, -1, $"{fidFail} 个格的十字不在期望位置（红色占比 < {FiducialMinFrac:P0}）⇒ 格子几何与实得矩形对不上，或被别的窗盖住");
        if (ambiguous > 0)
            return new Dec(false, -1, $"{ambiguous} 个格落在明暗歧义带（离门限 < {amb:0.0}）⇒ 不猜，整帧作废");
        return new Dec(true, value, $"定标 亮={hiRef:0} 暗={loRef:0} 门限={thr:0}");
    }

    // ════════════════════════════════════════════════════════════════
    //  常驻池的帧年龄（热臂附带读数）
    // ════════════════════════════════════════════════════════════════

    /// <summary>读 PooledSession.GetFrameAge()。
    /// <para>为什么要反射：帧年龄长在 private 嵌套类 PooledSession 上，而本仪表不许动 WgcCaptureService.cs
    /// （接线与出口都由维护者定）。反射不中就退化成"这一列没有读数"—— 它只是辅助读数，
    /// 主判据（参照值−被试值）不依赖它；哪天维护者加了 internal 出口，这里就该换成直访。</para></summary>
    private static (long? AgeMs, string Note) ReadResidentFrameAgeMs(WgcCaptureService wgc, nint hmonitor)
    {
        try
        {
            // 走 WgcCaptureService 的 internal 探针出口，不走反射：反射在打包形态下会静默拿到 null，
            // 而这一列一旦"读到 null"就被当成 0 的话，热臂会假装自己很新鲜。
            var age = wgc.ProbeFrameAgeMs(hmonitor, hdr: false);   // 8-bit 那一池（参照臂读的是它）
            return age is null
                ? (null, "常驻池里没有该屏的 8-bit 会话或它还没出过帧 ⇒ 这一臂本轮其实是冷的")
                : (age, "常驻 8-bit 池的帧年龄");
        }
        catch (Exception ex)
        {
            return (null, $"读帧年龄失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  分布
    // ════════════════════════════════════════════════════════════════

    private static List<int> JudgedErrs(List<Trial> ts, bool lower)
        => ts.Where(t => t.Judged).Select(t => lower ? t.DeltaLoTicks : t.DeltaHiTicks).ToList();

    /// <summary>报一臂的误差分布（p50/p95/max、Δ>0 的次数、捕获耗时），热臂另报帧年龄分布。
    /// 每条都带成立条件；这一相不写"结论"，只写"这次量到了什么"。</summary>
    private static int ReportArm(Action<string> Say, string name, List<Trial> ts, bool withAge)
    {
        var judged = ts.Where(t => t.Judged).ToList();
        Say($"  {name}");
        Say($"    判定 {judged.Count}/{ts.Count} 次；成立条件=该次参照前/参照后/被试三处都解出刻度（未判定次数的原因写在各自那行）");
        if (judged.Count == 0)
        {
            Say("    —— 一次都没判上 ⇒ 这一臂本轮没有分布");
            return 0;
        }
        foreach (var (label, lower) in new (string, bool)[] { ("Δ下界(参照取按下之前)", true), ("Δ上界(参照取返回之后)", false) })
        {
            var errs = judged.Select(t => lower ? t.DeltaLoTicks : t.DeltaHiTicks).ToList();
            double p50 = Percentile(errs, 0.5), p95 = Percentile(errs, 0.95), mx = errs.Max(), mn = errs.Min();
            int pos = errs.Count(e => e > 0), neg = errs.Count(e => e < 0);
            Say($"    {label}：p50={p50:0.0}格({p50 * TickMs:0}ms) p95={p95:0.0}格({p95 * TickMs:0}ms) " +
                $"max={mx}格({mx * TickMs}ms) min={mn}格({mn * TickMs}ms) " +
                $"Δ>0={pos}/{errs.Count} 次（被试拍到过去）Δ<0={neg}/{errs.Count} 次（被试拍到之后）Δ=0={errs.Count - pos - neg} 次" +
                "｜成立条件=分布只由判定次数构成，未判定的轮次不进分布（原因在各自那行）");
        }
        var ms = judged.Select(t => (double)t.CaptureMs).ToList();
        Say($"    捕获耗时：p50={Percentile(ms, 0.5):0}ms p95={Percentile(ms, 0.95):0}ms max={ms.Max():0}ms" +
            "｜成立条件=从调用 CaptureMonitorAsync 到交回 8-bit 帧，含两端 GDI 抓取之外的一切");
        if (withAge)
        {
            var ages = judged.Where(t => t.AgeMs.HasValue).Select(t => (double)t.AgeMs.GetValueOrDefault()).ToList();
            if (ages.Count == 0)
                Say("    帧年龄：本轮一次都没读到（见上面反射那条说明）⇒ 这一列没有读数，不影响误差分布");
            else
                Say($"    常驻池帧年龄：p50={Percentile(ages, 0.5):0}ms p95={Percentile(ages, 0.95):0}ms max={ages.Max():0}ms" +
                    $" 读到 {ages.Count}/{judged.Count} 次｜成立条件=取帧前该屏 8-bit 会话在世且有帧（GetFrameAge 口径=帧时间戳距今）");
            Say("    ⚠ 这一列的读法：刻度窗每 100ms 自变 ⇒ 本轮全程【画面在动】，量到的是'在动时常驻帧落后多少'。" +
                "完全静止的桌面另一形状（产品注释实测静态投递约 1.2fps，年龄能涨到闲置回收），但那种情况下缓存帧与当前画面本来就同值；" +
                "定罪用的是上面的 Δ 分布，不是这一列年龄。");
        }
        return judged.Count;
    }

    /// <summary>有符号回绕差：刻度一圈只有 2^20 格（≈29 小时），跨圈时裸相减会得到一个大得离谱的数，
    /// 把一次正常回绕读成"截图旧了几小时"。折进 (-2^19, 2^19]（±14.6 小时）之后，跨圈前后同一句话。</summary>
    private static int SignedDelta(int a, int b)
    {
        int d = (a - b) & ValueMask;
        return d >= (1 << (Bits - 1)) ? d - (1 << Bits) : d;
    }

    private static double Percentile(List<int> v, double q) => Percentile(v.Select(x => (double)x).ToList(), q);

    /// <summary>线性插值分位数（n=1 时 p50/p95 都退化成那一个数——样本少到分不出位数时，
    /// 上面的样本量那行会把"撑不起 p95"单独判红，而不是让这里假装有一个分布）。</summary>
    private static double Percentile(List<double> v, double q)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToArray();
        double pos = q * (s.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return lo == hi ? s[lo] : s[lo] + (pos - lo) * (s[hi] - s[lo]);
    }

    /// <summary>读数按【仓根相对】落盘：artifacts/capture_freshness_&lt;日期&gt;.log。
    /// <para>先例是 Tools 那边（FormatBench.cs / CorePipelineTests.cs 里那一串 ".."），但个数不能照抄：
    /// 本项目同时声明了 Platform=x64 与 RuntimeIdentifier=win-x64，输出比 Tools 多两层
    /// （bin/x64/&lt;Cfg&gt;/&lt;TFM&gt;/win-x64/），而 Directory.Build.props 明确写了"层数因项目而异"。
    /// ⇒ 改成向上找到仓库标记（.git）为止；找不到就退回 exe 自己旁边——路径写错只是读数换了个地方，
    /// 不该把判据拖下水，所以这里不判红。</para></summary>
    private static string ResolveArtifactPath(string fileName)
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return Path.Combine(dir.FullName, "artifacts", fileName);
                dir = dir.Parent;
            }
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static void TryCreateDirectoryFor(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[freshness] ★ 读数目录建不出来（{ex.GetType().Name}: {ex.Message}）——判据照跑，只是不落盘");
        }
    }
}
