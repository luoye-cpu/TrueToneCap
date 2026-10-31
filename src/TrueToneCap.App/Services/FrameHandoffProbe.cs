// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/FrameHandoffProbe.cs
//
// 真机测量仪表：TrueToneCap.exe --frame-handoff-probe
//
// ── 它回答哪一个具体问题 ──
// 路线 B（帧不进 CPU，预览直接采样 GPU 快照）的验收前提是同一句话：
//   **"已经交给消费者（预览窗）的那一块像素，事后会不会被后续帧改写"**。
// 今天没有任何一条判据能回答它 —— 现有四台仪器各判各的（--capture-freshness-probe 判"截图落后现实多少"、
// --sdr-semantics-probe 判"两次读数是同一张画面"、--preview-ui-probe 判"屏幕上呈现成什么样"、
// --selftest 判"源精度"），没有一条盯着"交出去之后那块内存/纹理有没有被动过"。
// 本仪表量三件事：交出去的 CPU 复用缓冲/GPU 共享槽各自会不会被后续帧改写、生产侧的"选区回读"
// 取的是不是就是槽里那一块、以及预览窗到底采样的是哪一块。
//
// ── 七条臂，每条都在防一种"看起来是绿其实是空转" ──
// ① 被测物必须真的在变（前置自证）：一扇每 60ms 自翻色的顶端小窗 + 两次整屏捕获的哈希必须不同。
//    它不为真 ⇒ 后面所有"没变"都可能只是画面本来就一样，整张表作废（返回非 0，不跑后面的臂）。
// ② 深拷贝对照臂（仪器能看见"不变"）：把第一次捕获的 SDR 数组深拷一份，再截一张 ⇒ 深拷贝必须【不变】。
//    它红了就意味着比较器/哈希自己写错了，"变了"也就不再是证据。
// ③ CPU 复用缓冲臂（今天的产品行为）：从服务拿到的数组就是服务内部 _reuseSdr 本身 ——
//    下一次 CaptureMonitorAsync 会把它整块覆写。今天护住用户画面的不是这块缓冲的纪律，
//    而是 MainWindow 那道 _isCapturing 防重入闸（预览期间不许第二次截图）。
//    本臂【只报告不判死】：它记录的是"复用缓冲会被覆写"这个事实，将来若改成私有拷贝，这一行会自己变。
// ④ 共享槽臂（路线 B 的交接契约，这条要判）：
//    从生产路径拿槽（WantSharedFrame=true，取槽就冻在捕获方法里）→ 在【第二个 D3D11 设备】上
//    OpenSharedResource1 打开 → 回读哈希；持有期间连截 3 次 → 哈希必须【不变】（用户框选时画面要冻住）；
//    交还后再截一次、直接读原句柄 → 哈希必须【变】（否则这块槽从此不再更新，第二张截图会是老画面）。
// ⑤ 生产侧的"选区回读" == 第二设备整块回读后取同一矩形，逐元素比（路线 B-3）。
//    保存链的 HDR 像素现在就来自这句回读；坐标/行距/半精度转换任一处错，保存的都不是用户框住的那块。
// ⑥ 同一条生产路径连跑 6 次（画面在动）：每次都必须冻出槽、每次都回读得出选区且逐元素对得上。
//    取槽点一旦被挪回调用方（晚几十毫秒），这条臂会时对时错地红。
// ⑦ 预览窗真的在采样那块槽：喂给窗口的 CPU 兜底帧故意涂成洋红 ——
//    对照臂（不给槽）屏幕上必须看到洋红（证明仪器看得见这扇覆盖窗），
//    被测臂（给槽）屏幕上必须不是洋红（证明采样源是槽，不是 CPU 帧）。
// ⑧–⑪ SDR 槽（2026-10-30 阶段 3）：SDR 帧也冻结进它自己的槽、不再整帧回读；
//    生产回读 == 第二设备整块回读取同矩形（逐字节）；运动下连 6 次都对得上；错句柄被拒。
//    ⑧–⑩ 只在 Float16 会话在场时才有对象（捕获侧的条件）—— 没有会话时"不判"，不算失败。
//
// ── 两台"仪器"的分工 ──
// 哈希 = FNV-1a 64 位，逐字节；对 CPU 数组直接算，对纹理先 CopyResource 进 staging 再按行算
// （**按 RowPitch 逐行**，不能整块拷 —— 4K 下 RowPitch 常大于 w*bpp，整块算会把行尾填充也算进哈希，
//  于是同一帧两次读出的填充字节不同就会伪造出"变了"）。
//
// ── 退出码 ──
// 0 = 前置 + 深拷贝对照 + 共享槽四条判据 + 选区回读对等 + 生产路径连跑 + 预览真的在采样，全过；
// 1 = 任一失败 / 拿不到帧 / 没跑完。
//
// ── 接线 ──
// 与 --capture-freshness-probe 同形状：必须等 XAML 应用起来、跑在 UI 线程、全程 await 不阻塞消息循环
// （自翻色小窗的 WM_TIMER 靠这条线程泵出来；阻塞它 = 被测物停止变化 = 所有"没变"归零）。

using System.Diagnostics;
using System.Runtime.InteropServices;
using TrueToneCap.App.Models;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace TrueToneCap.App.Services;

public static partial class FrameHandoffProbe
{
    public const string Flag = "--frame-handoff-probe";

    // ── 自翻色小窗（被测物）──
    private const int TickerW = 420, TickerH = 300;
    private const int TickerTickMs = 60;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_ERASEBKGND = 0x0014;
    private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x00000008,
                       WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SW_SHOWNOACTIVATE = 0x0004;

    private static nint s_tickerHwnd;
    private static int s_tickerPaint;
    private static readonly WndProcDelegate s_wndProc = TickerWndProc;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>IDXGIResource1::CreateSharedHandle 的原生签名（vtable 第 13 槽）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSharedHandleFn(nint self, nint attrs, uint access, nint name, out nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // 这三个走 DllImport 而不是 LibraryImport：源生成的封送不支持带 ByValArray / 字符串字段的结构
    // （SYSLIB1051）。与本仓其它探针同一取舍 —— 探针里混用两种声明不算风格分裂，是封送能力的边界。
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    private static partial nint SetTimer(nint hwnd, nint id, uint ms, nint proc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hwnd, nint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);

    // 三个 GDI 入口分居两个 DLL，别按"GDI 就该在 gdi32"一把梭：
    //   CreateSolidBrush / DeleteObject 在 gdi32.dll；FillRect 实际导出在 user32.dll。
    // 两次编译都过、错在运行时第一次 WM_PAINT —— 这条注释就是为了别再猜第三遍。
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    private static partial int FillRect(nint dc, ref RECT rect, nint brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[]? rgbReserved;
    }

    private static nint TickerWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                InvalidateRect(hwnd, 0, false);   // 只失效不擦背景 ⇒ 下一拍走 WM_PAINT
                return 0;
            case WM_ERASEBKGND:
                return 1;                          // 背景在 WM_PAINT 里一次画完
            case WM_PAINT:
            {
                var ps = new PAINTSTRUCT();
                nint dc = BeginPaint(hwnd, ref ps);
                if (dc != 0)
                {
                    if (GetClientRect(hwnd, out var rc))
                    {
                        // 两帧一循环：纯黑 ↔ 纯白。整窗翻转 ⇒ 整屏哈希必然变，不依赖任何字体/合成细节。
                        uint color = (Interlocked.Increment(ref s_tickerPaint) & 1) == 0 ? 0x00000000u : 0x00FFFFFFu;
                        nint brush = CreateSolidBrush(color);
                        if (brush != 0) { FillRect(dc, ref rc, brush); DeleteObject(brush); }
                    }
                    EndPaint(hwnd, ref ps);
                }
                return 0;
            }
            case WM_DESTROY:
                s_tickerHwnd = 0;
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static nint CreateTickerWindow(int x, int y)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = GetModuleHandleW(0),
            lpszClassName = "TTC_FrameHandoffProbe_Ticker",
        };
        if (RegisterClassExW(ref wc) == 0) return 0;
        // 坐标由调用方按【被测那块屏】给：放在 (40,40) 只是主屏上好看，光标在副屏时
        // 整屏捕获里根本没有这扇窗 ⇒ 臂①永远读到"画面没变"（第一次跑就是这么瞎的）。
        nint hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, "handoff-ticker", WS_POPUP,
            x, y, TickerW, TickerH, 0, 0, wc.hInstance, 0);
        if (hwnd == 0) return 0;
        ShowWindow(hwnd, (int)SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);
        SetTimer(hwnd, 1, TickerTickMs, 0);
        s_tickerHwnd = hwnd;
        return hwnd;
    }

    private static void Say(string s)
    {
        LogService.Info("FrameHandoff", s, LogCategory.Capture);
        Console.WriteLine(s);
    }

    private static ulong Fnv1a(byte[] data, int len)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < len; i++) { h ^= data[i]; h *= 1099511628211UL; }
        return h;
    }

    private static int _fail;

    private static void Need(bool ok, string what, string why)
    {
        Say($"{(ok ? "✅" : "❌")} {what}");
        if (!ok) { Say($"     ↳ {why}"); _fail++; }
    }

    public static async Task<int> RunAsync()
    {
        Say($"== {Flag}：帧交接（CPU 复用缓冲 / GPU 共享槽）==");

        nint monitor = DisplayEnumerator.GetMonitorUnderCursor();
        if (monitor == 0) { Say("★ 取不到光标所在显示器 ⇒ 不判"); return 1; }

        var disp = DisplayEnumerator.FindDisplayByMonitor(monitor);
        if (disp is null) { Say("★ 枚举不到该显示器的矩形 ⇒ 被测物放不进画面，不判"); return 1; }
        Say($"被捕获的屏：{disp.Name} 矩形=({disp.X},{disp.Y})-({disp.X + disp.Width},{disp.Y + disp.Height}) " +
            $"{(disp.IsHdr ? "HDR" : "SDR")}");

        nint hwnd = CreateTickerWindow(disp.X + 40, disp.Y + 40);
        if (hwnd == 0) { Say("★ 自翻色小窗没建起来 ⇒ 被测物不存在，不判"); return 1; }
        if (!GetWindowRect(hwnd, out var tickRect))
        { Say("★ 读不到自翻色小窗的矩形 ⇒ 无法证明它在画面里，不判"); return 1; }
        bool inFrame = tickRect.Left >= disp.X && tickRect.Top >= disp.Y
                       && tickRect.Right <= disp.X + disp.Width && tickRect.Bottom <= disp.Y + disp.Height;
        Need(inFrame, $"被测物落在被捕获的那块屏内（窗=({tickRect.Left},{tickRect.Top})-({tickRect.Right},{tickRect.Bottom})）",
            "窗不在被捕获的屏内 ⇒ 臂①永远读到\"画面没变\"，整张表作废");

        var wgc = AppServices.Wgc;
        if (wgc is null) { Say("★ WGC 服务未初始化"); return 1; }

        try
        {
            var cfg = new WgcCaptureConfig { TargetMonitor = monitor, PreferHdr = true };

            // ── 臂①：被测物真的在变（WGC 的投递节奏不由我们定，所以最多等 2.4s 看它变一次）──
            var r1 = await wgc.CaptureMonitorAsync(cfg);
            if (r1.SdrPixels is null) { Say("★ 拿不到 SDR 帧 ⇒ 不判"); return 1; }
            ulong hFirst = Fnv1a(r1.SdrPixels, r1.SdrPixels.Length);
            ulong hPrev = hFirst;
            ulong hNow = hFirst;
            bool alive = false;
            for (int i = 0; i < 12 && !alive; i++)
            {
                await Task.Delay(200);
                var rn = await wgc.CaptureMonitorAsync(cfg);
                if (rn.SdrPixels is null) continue;
                hNow = Fnv1a(rn.SdrPixels, rn.SdrPixels.Length);
                alive = hNow != hPrev;
                hPrev = hNow;
            }
            Need(alive, $"臂① 前置自证：整屏捕获的哈希出现过变化（{hFirst:X16} → {hNow:X16}）",
                "画面没在变 ⇒ 后面所有\"没变\"都可能只是画面本来就一样，整张表作废");
            if (!alive) return 1;
            var a2 = await wgc.CaptureMonitorAsync(cfg);
            if (a2.SdrPixels is null) { Say("★ 臂②那一次拿不到 SDR 帧 ⇒ 不判"); return 1; }

            // ── 臂②：深拷贝对照（仪器能看见"不变"）──
            var deep = new byte[a2.SdrPixels.Length];
            Buffer.BlockCopy(a2.SdrPixels, 0, deep, 0, deep.Length);
            ulong deepHash = Fnv1a(deep, deep.Length);
            await Task.Delay(240);
            await wgc.CaptureMonitorAsync(cfg);
            ulong deepAfter = Fnv1a(deep, deep.Length);
            Need(deepHash == deepAfter, $"臂② 深拷贝对照：再截一张后深拷贝仍然不变（{deepHash:X16}）",
                "深拷贝都变了 ⇒ 比较器/哈希自己写错，\"变了\"不再构成证据");

            // ── 臂③：CPU 复用缓冲（今天的产品行为，只报告）──
            var b1 = await wgc.CaptureMonitorAsync(cfg);
            if (b1.SdrPixels is null) { Say("★ 臂③拿不到 SDR 帧"); return 1; }
            ulong held = Fnv1a(b1.SdrPixels, b1.SdrPixels.Length);
            await Task.Delay(240);
            await wgc.CaptureMonitorAsync(cfg);
            ulong heldAfter = Fnv1a(b1.SdrPixels, b1.SdrPixels.Length);
            Say($"ℹ️ 臂③ CPU 复用缓冲：{(held == heldAfter ? "未被覆写（私有一份）" : "已被后续帧覆写")}" +
                $"（{held:X16} → {heldAfter:X16}）—— 只报告：今天护住用户画面的是 _isCapturing 防重入闸，不是这块缓冲");

            using var probeDev = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);

            // ── 共享句柄自检（在探针自己的设备上做，与被测会话无关）──
            // 为什么要它：产品侧 CreateSharedHandle 在"desc 回读确认 Misc=Shared"之后仍回 E_INVALIDARG。
            // 把同一段调用搬到一台全新的设备 + 一块全新的纹理上，就能分开"Vortice 调用形式有问题"
            // 与"只有捕获设备/那条纹理有问题"——否则只会在产品日志里反复猜。
            // ⚠ 它必须排在取槽之前：产品侧取不到槽就会 return，自检放在后面永远跑不到。
            try
            {
                using var t = probeDev.CreateTexture2D(new Texture2DDescription
                {
                    Width = 256, Height = 256, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.None,
                    // 与产品同一条约束：MISC_SHARED_NTHANDLE（普通 Shared 只能走 DXGI1.0 的 GetSharedHandle，
                    // 拿它去 CreateSharedHandle 恒 E_INVALIDARG —— 见该文件头的实测记录）
                    MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
                });
                Say($"共享句柄自检：本机新建设备上 Shared 纹理已建（desc.Misc={t.Description.MiscFlags}）");
                using var selfRes1 = t.QueryInterface<Vortice.DXGI.IDXGIResource1>();
                var combos = new (string Label, Vortice.DXGI.SharedResourceFlags Flags, string? Name)[]
                {
                    ("None / 无名", Vortice.DXGI.SharedResourceFlags.None, null),
                    ("Read|Write / 无名", Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null),
                    ("Read|Write / 有名", Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, "TTC_HandoffSelfTest"),
                };
                foreach (var c in combos)
                {
                    try
                    {
                        nint h = selfRes1.CreateSharedHandle(null, c.Flags, c.Name);
                        Say($"  · {c.Label} ⇒ 句柄=0x{h:X}");
                    }
                    catch (Exception ex)
                    {
                        Say($"  · {c.Label} ⇒ 失败：{ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}");
                    }
                }

                // ④ 绕过 SharpGen 包装，直接打 vtable：IDXGIResource1::CreateSharedHandle 是
                // IUnknown(3) + IDXGIObject(4) + IDXGIResource(5) 之后的第 13 槽（0 基）。
                // 上面的三个组合全红、而 desc 回读确认 Misc=Shared ⇒ 怀疑是包装层把
                // SecurityAttributes? 的 null 封送成了一个"非空但内容为零"的指针（nLength=0 ⇒ E_INVALIDARG）。
                // 这一格把 attrs 真正传 0，用读数来判这个怀疑。
                try
                {
                    // ⚠ 必须用 QI 出来的那条接口指针（selfRes1），不是纹理自己的 NativePointer：
                    // 后者是 ID3D11Texture2D 的 vtable，按 IDXGIResource1 的槽位去取会取到别的函数 ⇒ 当场 AV。
                    nint self = selfRes1.NativePointer;
                    nint vtbl = Marshal.ReadIntPtr(self);
                    nint fn = Marshal.ReadIntPtr(vtbl, 13 * IntPtr.Size);
                    var call = Marshal.GetDelegateForFunctionPointer<CreateSharedHandleFn>(fn);
                    int hr = call(self, 0, 0x80000001, 0, out nint rawHandle);
                    Say($"  · 裸 vtable(attrs=null, R|W) ⇒ hr=0x{hr:X8} 句柄=0x{rawHandle:X}");
                }
                catch (Exception ex)
                {
                    Say($"  · 裸 vtable ⇒ 抛异常：{ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}");
                }
            }
            catch (Exception ex)
            {
                Say($"共享句柄自检：连 Shared 纹理都建不出来 —— {ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}");
            }

            // ── 臂④：共享槽（路线 B 的契约，要判）──
            // 走【生产路径】：WantSharedFrame=true 让捕获方法在取像素的同一段里冻结这一帧。
            // 不在这里"捕获完再取槽"：捕获返回前还有 ICC 读取/日志/线程池续体，本机实测那段里
            // WGC 仍在出帧 ⇒ 晚取到的必然是新一帧。
            // 路线 B-3 之后这一步【不再预读整帧像素】（HdrPixels=null 是正常的），只看取槽成不成。
            async Task<(bool Ok, nint H, int W, int H2)> TakeHdrSlotAsync(int tries)
            {
                for (int i = 0; i < tries; i++)
                {
                    var c = await wgc.CaptureMonitorAsync(cfg with { WantSharedFrame = true });
                    nint th = c.TakeShared(out int tw, out int thh);
                    if (th != 0) return (true, th, tw, thh);
                    await Task.Delay(120);
                }
                return (false, 0, 0, 0);
            }

            var tk = await TakeHdrSlotAsync(8);
            if (!tk.Ok) { Say("★ 捕获没冻出共享槽（HDR 会话 / 生产路径上不该发生）⇒ 不判"); return 1; }
            nint handle = tk.H; int sw = tk.W, sh = tk.H2;
            bool hdr = true;   // 只取 HDR：预览窗要的正是 Float16 这一块（SDR 位在着色器里是另一种解释）
            Say($"共享槽：句柄已取走 {sw}x{sh} (HDR/Float16)，改在【第二个 D3D11 设备】上打开");
            ulong? ReadOpenedHash(string tag, nint h)
            {
                // NTHANDLE 必须用 OpenSharedResource1 打开（OpenSharedResource 走的是旧式共享句柄）
                using var dev1 = probeDev.QueryInterface<ID3D11Device1>();
                using var tex = dev1.OpenSharedResource1<ID3D11Texture2D>(h);
                var d = tex.Description;
                using var staging = probeDev.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1,
                    Format = d.Format, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
                });
                var ctx = probeDev.ImmediateContext;
                ctx.CopyResource(staging, tex);
                var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (mapped.DataPointer == 0) return null;
                try
                {
                    int bpp = hdr ? 8 : 4;                       // R16G16B16A16_Float=8B/px, B8G8R8A8=4B/px
                    int rowBytes = sw * bpp;
                    var row = new byte[rowBytes];
                    ulong hh = 14695981039346656037UL;
                    long min = long.MaxValue, max = long.MinValue, sum = 0;
                    for (int y = 0; y < sh; y++)
                    {
                        // 逐行按 RowPitch 取：4K 下 RowPitch 常大于 w*bpp，整块拷会把行尾填充也算进哈希，
                        // 于是同一帧两次读出就可能伪造出"变了"。
                        Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, rowBytes);
                        for (int i = 0; i < rowBytes; i++) { hh ^= row[i]; hh *= 1099511628211UL; }
                        if (y == sh / 2)                            // 中心行：顺带交个亮度范围，证明不是全零
                        {
                            for (int i = 0; i < rowBytes; i += bpp)
                            {
                                long v = hdr ? BitConverter.ToUInt16(row, i) : row[i];
                                if (v < min) min = v;
                                if (v > max) max = v;
                                sum += v;
                            }
                        }
                    }
                    Say($"     ↳ 回读{tag}：中心行 min={min} max={max} avg={(double)sum / Math.Max(1, rowBytes / bpp):F1}");
                    return hh;
                }
                finally { ctx.Unmap(staging, 0); }
            }

            ulong? firstHash = ReadOpenedHash("(1)", handle);
            if (firstHash is null) { Say("★ 跨设备打开后回读失败（OpenSharedResource 成功但 Map 不到）⇒ 不判"); return 1; }
            Need(firstHash.Value != 0, $"臂④-1 跨设备共享可用：第二设备上打开成功并回读到内容（{firstHash.Value:X16}）",
                "打开或回读失败 ⇒ 路线 B 的技术前提不成立");
            Need(sw == a2.Width && sh == a2.Height, $"臂④-2 共享槽尺寸与捕获一致（{sw}x{sh} vs {a2.Width}x{a2.Height}）",
                "尺寸不一致 ⇒ 预览窗会按错的坐标采样");

            // 让画面重新动起来，再在持有期间连截 3 次：槽必须冻住（画面在变、槽不变，才叫冻结）
            SetTimer(hwnd, 1, TickerTickMs, 0);
            for (int i = 0; i < 3; i++) { await Task.Delay(150); await wgc.CaptureMonitorAsync(cfg); }
            ulong? heldHash2 = ReadOpenedHash("(2)", handle);
            if (heldHash2 is null) { Say("★ 持有期间回读失败"); return 1; }
            Need(firstHash.Value == heldHash2.Value, $"臂④-3 持有期间冻结：再截 3 次后同一块纹理内容不变（{heldHash2.Value:X16}）",
                "槽被后续帧覆写了 ⇒ 用户框选时画面会在眼前被换掉（P0-C 那次撕裂的同型事故）");

            // 交还后再截一次：同一块句柄上必须重新出现新内容。
            // 这里刻意【不再取一次槽】：取槽要过帧号闸，而画面在动时闸本来就会拦下（那是它的职责）；
            // 本臂要证的是"交还之后生产侧恢复了覆写"，直接读原句柄即可（DXGI 引用计数保证它仍有效）。
            wgc.ReleaseSharedFrame(monitor, true);
            await Task.Delay(400);
            await wgc.CaptureMonitorAsync(cfg);
            ulong? afterHash = ReadOpenedHash("(3)", handle);
            if (afterHash is null) { Say("★ 交还后回读失败"); return 1; }
            Need(afterHash.Value != heldHash2.Value, $"臂④-4 交还后恢复更新：同一块槽上出现了新内容（{heldHash2.Value:X16} → {afterHash.Value:X16}）",
                "交还后槽没再更新 ⇒ 第二张截图会拿到老画面（冻结没解开）");
            wgc.ReleaseSharedFrame(monitor, hdr);

            // ── 臂⑤：生产用的"只回读选区" == 第二设备整块回读再取同一矩形（逐元素）──
            // 路线 B-3 之后，保存链的 HDR 像素就是生产函数给的（冻结槽 + CopySubresourceRegion 只搬选框那块）。
            // 坐标错一列、行距算错、半精度转换不同源，界面上都可能"看着正常"，所以用另一台设备
            // 整块回读、按同一矩形取行，与生产函数逐元素对账。
            float[]? ReadRegionOnProbe(nint h, int x, int y, int w, int hgt)
            {
                using var dev1 = probeDev.QueryInterface<ID3D11Device1>();
                using var tex = dev1.OpenSharedResource1<ID3D11Texture2D>(h);
                var d = tex.Description;
                using var st = probeDev.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1,
                    Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
                });
                var ctx = probeDev.ImmediateContext;
                ctx.CopyResource(st, tex);
                var m = ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (m.DataPointer == 0) return null;
                try
                {
                    var px = new float[w * hgt * 4];
                    unsafe
                    {
                        fixed (float* dst = px)
                        {
                            for (int row = 0; row < hgt; row++)
                            {
                                // 与产品同一条半精度转换：两侧同源同转换，才谈得上"逐元素相等"
                                TrueToneCap.Core.PixelOps.ConvertHalfToFloatRow(
                                    (byte*)m.DataPointer + (y + row) * (int)m.RowPitch + x * 8,
                                    (float*)((byte*)dst + row * (long)w * 16), w * 4);
                            }
                        }
                    }
                    return px;
                }
                finally { ctx.Unmap(st, 0); }
            }

            // 返回不同的元素个数；-1 = 生产函数没给；-2 = 第二设备读不到
            int CompareRegion(nint h, int x, int y, int w, int hgt, out int len)
            {
                len = 0;
                var prod = wgc.ReadSharedRegion(monitor, true, h, x, y, w, hgt);
                if (prod is null) return -1;
                len = prod.Length;
                var refr = ReadRegionOnProbe(h, x, y, w, hgt);
                if (refr is null) return -2;
                int bad = 0;
                for (int i = 0; i < prod.Length; i++)
                {
                    float a = prod[i], b = refr[i];
                    if (a != b && !(float.IsNaN(a) && float.IsNaN(b))) bad++;
                }
                return bad;
            }

            int bad5 = int.MinValue, len5 = 0, w5 = 0, h5h = 0, rw5 = 0, rh5 = 0;
            for (int i = 0; i < 4 && bad5 == int.MinValue; i++)
            {
                var c = await wgc.CaptureMonitorAsync(cfg with { WantSharedFrame = true });
                nint h5 = c.TakeShared(out int tw5, out int th5);
                if (h5 == 0 || c.Width != tw5 || c.Height != th5)
                {
                    if (h5 != 0) wgc.ReleaseSharedFrame(monitor, true);
                    await Task.Delay(150);
                    continue;
                }
                w5 = tw5; h5h = th5;
                rw5 = tw5 / 2; rh5 = th5 / 2;
                bad5 = CompareRegion(h5, tw5 / 4, th5 / 4, rw5, rh5, out len5);
                wgc.ReleaseSharedFrame(monitor, true);
            }
            if (bad5 == int.MinValue) { Say("★ 臂⑤连 4 次都没从捕获里拿到共享槽 ⇒ 不判"); return 1; }
            if (bad5 == -1) { Say("★ 臂⑤生产选区回读返回 null（槽冻着、尺寸也对，不该发生）⇒ 判死"); return 1; }
            if (bad5 == -2) Say("★ 臂⑤第二设备读不到槽 ⇒ 不判（生产侧已返回像素）");
            else
            {
                Need(bad5 == 0 && len5 == rw5 * rh5 * 4,
                    $"臂⑤ 生产选区回读 == 第二设备整块回读取同矩形（{rw5}x{rh5}@{w5 / 4},{h5h / 4}，比 {len5} 个分量，不同 {bad5} 处）",
                    "坐标/行距/半精度转换任一处错 ⇒ 保存的不是用户框住的那块");
            }

            // ── 臂⑥：画面在动时，生产路径每次都冻得出槽、每次都回读得出选区且逐元素对得上 ──
            // 臂⑤ 是一件样本；这一臂是重复采样：自翻色小窗每 60ms 翻一次，六次捕获各自都得对上。
            // 它同时守着"取槽点不许挪回调用方"：挪回去以后画面一动就会时对时错，这一臂必然红。
            SetTimer(hwnd, 1, TickerTickMs, 0);
            int acc6 = 0, miss6 = 0, accBad6 = 0;
            for (int i = 0; i < 6; i++)
            {
                var c = await wgc.CaptureMonitorAsync(cfg with { WantSharedFrame = true });
                nint h = c.TakeShared(out int w, out int hh);
                if (h == 0) { miss6++; continue; }
                acc6++;
                if (CompareRegion(h, w / 3, hh / 3, Math.Max(1, w / 3), Math.Max(1, hh / 3), out _) != 0) accBad6++;
                wgc.ReleaseSharedFrame(monitor, true);
            }
            Need(miss6 == 0 && accBad6 == 0,
                $"臂⑥ 运动画面下生产路径每次都冻出槽且选区回读对得上（{acc6} 次对上 / 没取到 {miss6} 次 / 不等 {accBad6} 次）",
                "有取不到或对不上的 ⇒ 取槽点被挪离了捕获段，或选区回读本身错了");

            // ── 臂⑦：预览窗真的在采样那块共享纹理（接线的像素级证明 + 正控）──
            // 仪器：喂给预览的 CPU 兜底帧故意涂成洋红（真桌面里不会有这种颜色）。于是
            //   对照臂（不给槽）⇒ 屏幕上必须看到洋红：证明这台仪器看得见这扇覆盖窗；
            //   被测臂（给槽）  ⇒ 屏幕上是真桌面（白块），不是洋红：证明采样源是槽而不是 CPU 帧。
            // 判别用色比 g/min(r,b) 而不是绝对阈值：遮罩色是用户设置里读的，绝对亮度会随它漂。
            int vw2 = disp.Width, vh2 = disp.Height;
            var magenta = new float[vw2 * vh2 * 4];
            for (int i = 0; i < magenta.Length; i += 4)
            { magenta[i] = 0.5f; magenta[i + 1] = 0f; magenta[i + 2] = 0.5f; magenta[i + 3] = 1f; }

            int sx = (tickRect.Left + tickRect.Right) / 2 - disp.X;
            int sy = (tickRect.Top + tickRect.Bottom) / 2 - disp.Y;

            async Task<(int R, int G, int B)> SampleScreenAsync()
            {
                // PreferHdr=false：屏幕采样只走 8-bit 会话，别去碰 Float16 会话（会改写它"最近交出的帧号"）
                var sc = await wgc.CaptureMonitorAsync(new WgcCaptureConfig { TargetMonitor = monitor, PreferHdr = false, FrameTimeoutMs = 3000 });
                if (sc.SdrPixels is null || sc.Width != vw2 || sc.Height != vh2) return (-1, -1, -1);
                long r = 0, g = 0, b = 0; int n = 0;
                for (int y = sy - 20; y < sy + 20; y++)
                    for (int x = sx - 20; x < sx + 20; x++)
                    {
                        int o = (y * sc.Width + x) * 4;   // BGRA
                        b += sc.SdrPixels[o]; g += sc.SdrPixels[o + 1]; r += sc.SdrPixels[o + 2]; n++;
                    }
                return ((int)(r / n), (int)(g / n), (int)(b / n));
            }

            static double Neutrality((int R, int G, int B) c)
            {
                int lo = Math.Min(c.R, c.B);
                return lo <= 0 ? (c.G > 0 ? 9 : 1) : (double)c.G / lo;
            }

            if (sx < 40 || sy < 40 || sx >= vw2 - 40 || sy >= vh2 - 40)
            { Say($"★ 采样点 ({sx},{sy}) 不在屏内安全区 ⇒ 不判"); return 1; }

            using (var w0 = new HdrCaptureWindow())
            {
                if (!w0.Initialize(disp.X, disp.Y, vw2, vh2))
                { Say("★ 臂⑦对照臂窗口建不起来 ⇒ 不判"); return 1; }
                w0.LoadFrame(magenta, vw2, vh2);
                w0.Render();
                await Task.Delay(900);
                var cm = await SampleScreenAsync();
                Need(Neutrality(cm) < 0.6, $"臂⑦-控 对照臂（无槽 + CPU 帧=洋红）屏幕采样=({cm.R},{cm.G},{cm.B}) 色比={Neutrality(cm):F2} 应为洋红",
                    "屏幕上看不到洋红 ⇒ 这台仪器看不见预览窗，被测臂的\"不是洋红\"不构成证据");
                w0.Dispose();
                await Task.Delay(400);
            }

            // 取一块被测物是【白】的槽：白在遮罩下仍是中性灰，与洋红在色比上拉得最开
            nint h7 = 0; int w7 = 0, h7h = 0;
            (float R, float G, float B) SlotPixel(nint h, int x, int y)
            {
                using var dev1 = probeDev.QueryInterface<ID3D11Device1>();
                using var tex = dev1.OpenSharedResource1<ID3D11Texture2D>(h);
                var d = tex.Description;
                using var st = probeDev.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1,
                    Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
                });
                var ctx = probeDev.ImmediateContext;
                ctx.CopyResource(st, tex);
                var m = ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (m.DataPointer == 0) return (-1, -1, -1);
                try
                {
                    unsafe
                    {
                        var p = (ushort*)((byte*)m.DataPointer + y * (int)m.RowPitch + x * 8);
                        return ((float)BitConverter.UInt16BitsToHalf(p[0]),
                                (float)BitConverter.UInt16BitsToHalf(p[1]),
                                (float)BitConverter.UInt16BitsToHalf(p[2]));
                    }
                }
                finally { ctx.Unmap(st, 0); }
            }

            for (int i = 0; i < 20 && h7 == 0; i++)
            {
                var c = await wgc.CaptureMonitorAsync(cfg with { WantSharedFrame = true });
                nint h = c.TakeShared(out int w, out int hh);
                if (h == 0) { await Task.Delay(80); continue; }
                var px = SlotPixel(h, sx, sy);
                if (px.R > 0.5f) { h7 = h; w7 = w; h7h = hh; }
                else { wgc.ReleaseSharedFrame(monitor, true); await Task.Delay(40); }
            }
            if (h7 == 0) { Say("★ 臂⑦取不到\"白\"相位的槽 ⇒ 不判"); return 1; }

            using (var w1 = new HdrCaptureWindow())
            {
                // 交还挂在窗口生命周期上：Dispose 时发一次（与产品 MainWindow 的接法一致）
                w1.AttachSharedFrame(h7, w7, h7h, () => wgc.ReleaseSharedFrame(monitor, true));
                if (!w1.Initialize(disp.X, disp.Y, vw2, vh2))
                { Say("★ 臂⑦被测窗建不起来 ⇒ 不判（窗口 Dispose 会交还槽）"); return 1; }
                // 故意喂一份与槽不同的 CPU 帧：屏幕上若出现洋红，就说明走的是整帧上传
                w1.LoadFrame(magenta, vw2, vh2);
                w1.Render();
                await Task.Delay(1000);
                var ts = await SampleScreenAsync();
                Need(Neutrality(ts) > 0.75 && ts.G > 90,
                    $"臂⑦ 预览采样的是共享槽（屏幕采样=({ts.R},{ts.G},{ts.B}) 色比={Neutrality(ts):F2}；槽里那块是白、CPU 兜底帧是洋红）",
                    "屏幕上仍是洋红（或仍是暗的）⇒ 共享槽没接上，或打开失败后静默退回了整帧上传");
                w1.Dispose();
                await Task.Delay(400);
            }

            // ── 臂⑧–⑪：SDR 共享槽（2026-10-30 阶段 3）──
            // 与臂④⑤⑥同一条生产路径，只是取的是 SDR 槽（池键 false、槽格式 B8G8R8A8_UNorm）。
            // ⑧ 判"这一拍真的没整帧回读"（SdrPixels=null 才叫省下来了）；⑨ 判生产回读 == 第二设备
            // 整块回读取同一矩形（逐字节）；⑩ 画面在动时连 6 次都得冻出槽且对得上；
            // ⑪ 判错句柄被拒（槽被轮换后不许把用户没框住的那一帧交出去）。
            async Task<(bool Ok, nint H, int W, int H2, bool SdrCpuNull)> TakeSdrSlotAsync(int tries)
            {
                for (int i = 0; i < tries; i++)
                {
                    // using：HDR 槽这一臂不消费，交给 CaptureResult.Dispose 当场交还；
                    // SDR 槽被 TakeSharedSdr 认领后已脱离结果的所有权，由调用方交还。
                    using var c = await wgc.CaptureMonitorAsync(cfg with { WantSharedFrame = true });
                    nint th = c.TakeSharedSdr(out int tw, out int thh);
                    bool cpuNull = c.SdrPixels is null;
                    if (th != 0) return (true, th, tw, thh, cpuNull);
                    await Task.Delay(120);
                }
                return (false, 0, 0, 0, false);
            }

            byte[]? ReadSdrRegionOnProbe(nint h, int x, int y, int w, int hgt)
            {
                using var dev1 = probeDev.QueryInterface<ID3D11Device1>();
                using var tex = dev1.OpenSharedResource1<ID3D11Texture2D>(h);
                var d = tex.Description;
                if (d.Format != Format.B8G8R8A8_UNorm)
                { Say($"     ↳ SDR 槽格式={d.Format}（应为 B8G8R8A8_UNorm）"); return null; }
                using var st = probeDev.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1,
                    Format = d.Format, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
                });
                var ctx = probeDev.ImmediateContext;
                ctx.CopyResource(st, tex);
                var m = ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (m.DataPointer == 0) return null;
                try
                {
                    var px = new byte[w * hgt * 4];
                    for (int row = 0; row < hgt; row++)
                        Marshal.Copy(m.DataPointer + (y + row) * (int)m.RowPitch + x * 4, px, row * w * 4, w * 4);
                    return px;
                }
                finally { ctx.Unmap(st, 0); }
            }

            // 返回不同的字节个数；-1 = 生产函数没给；-2 = 第二设备读不到
            int CompareSdrRegion(nint h, int x, int y, int w, int hgt, out int len)
            {
                len = 0;
                var prod = wgc.ReadSharedRegionSdr(monitor, h, x, y, w, hgt);
                if (prod is null) return -1;
                len = prod.Length;
                var refr = ReadSdrRegionOnProbe(h, x, y, w, hgt);
                if (refr is null) return -2;
                int bad = 0;
                for (int i = 0; i < prod.Length; i++) if (prod[i] != refr[i]) bad++;
                return bad;
            }

            int bad8 = int.MinValue, len8 = 0, rw8 = 0, rh8 = 0, w8 = 0, h8 = 0;
            bool cpuNull8 = false;
            for (int i = 0; i < 4 && bad8 == int.MinValue; i++)
            {
                var t8 = await TakeSdrSlotAsync(1);
                if (!t8.Ok) { await Task.Delay(150); continue; }
                w8 = t8.W; h8 = t8.H2; cpuNull8 = t8.SdrCpuNull;
                rw8 = w8 / 2; rh8 = h8 / 2;
                bad8 = CompareSdrRegion(t8.H, w8 / 4, h8 / 4, rw8, rh8, out len8);
                wgc.ReleaseSharedFrame(monitor, false);
            }
            if (bad8 == int.MinValue)
            { Say("★ 臂⑧ 连 4 次都没冻出 SDR 槽（本机 Float16 会话没建起来？SDR 槽只在那条线在场时冻）⇒ 不判"); return 1; }
            Need(cpuNull8, "臂⑧ SDR 帧不再整帧回读：这一拍 SdrPixels=null、像素只在槽里",
                "仍在整帧回读 ⇒ 阶段 3 的收益一分没省（行为退回改前）");
            Need(w8 == a2.Width && h8 == a2.Height, $"臂⑧ SDR 槽尺寸与捕获一致（{w8}x{h8} vs {a2.Width}x{a2.Height}）",
                "尺寸不符 ⇒ 选区坐标（虚拟桌面减原点）会解释错位");
            if (bad8 == -1) Say("★ 臂⑨ 生产 SDR 选区回读返回 null（槽冻着、尺寸也对，不该发生）⇒ 判死");
            else if (bad8 == -2) Say("★ 臂⑨ 第二设备读不到 SDR 槽 ⇒ 不判（生产侧已返回像素）");
            else
                Need(bad8 == 0 && len8 == rw8 * rh8 * 4,
                    $"臂⑨ 生产 SDR 选区回读 == 第二设备整块回读取同矩形（{rw8}x{rh8}@{w8 / 4},{h8 / 4}，比 {len8} 字节，不同 {bad8} 处）",
                    "行距/坐标/通道任一处错 ⇒ 标注/复制/识字拿到的不是用户框住的那块");

            SetTimer(hwnd, 1, TickerTickMs, 0);
            int acc9 = 0, miss9 = 0, bad9 = 0;
            for (int i = 0; i < 6; i++)
            {
                var t9 = await TakeSdrSlotAsync(1);
                if (!t9.Ok) { miss9++; continue; }
                acc9++;
                if (CompareSdrRegion(t9.H, t9.W / 3, t9.H2 / 3, Math.Max(1, t9.W / 3), Math.Max(1, t9.H2 / 3), out _) != 0) bad9++;
                wgc.ReleaseSharedFrame(monitor, false);
            }
            Need(miss9 == 0 && bad9 == 0,
                $"臂⑩ 运动画面下 SDR 槽每次都冻结且选区回读对得上（{acc9} 次对上 / 没取到 {miss9} / 不等 {bad9}）",
                "有取不到或对不上的 ⇒ 取槽点被挪离了捕获段，或 SDR 回读本身错了");

            var t11 = await TakeSdrSlotAsync(4);
            if (!t11.Ok) Say("★ 臂⑪ 取不到 SDR 槽 ⇒ 不判");
            else
            {
                var rejected = wgc.ReadSharedRegionSdr(monitor, t11.H + 1, 0, 0, 64, 64);
                var accepted = wgc.ReadSharedRegionSdr(monitor, t11.H, 0, 0, 64, 64);
                Need(rejected is null && accepted is not null,
                    $"臂⑪ 只认持有者那块槽：错句柄被拒（{(rejected is null ? "null" : "非 null")}），正确句柄可读（{(accepted is null ? "null" : $"{accepted.Length}B")}）",
                    "错句柄也读得出来 ⇒ 槽轮换后会把用户没框住的那一帧交出去");
                wgc.ReleaseSharedFrame(monitor, false);
            }

            Say(_fail == 0 ? "RESULT handoff=pass" : $"RESULT handoff=fail ({_fail})");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Say($"★ 异常：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (s_tickerHwnd != 0)
            {
                KillTimer(s_tickerHwnd, 1);
                DestroyWindow(s_tickerHwnd);
                s_tickerHwnd = 0;
            }
        }
    }
}
