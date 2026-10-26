// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/OverlayWhiteLevelAb.cs
//
// 额外测试：兜底那一路的【呈现面】到底有没有被多抬一次亮度。
//
// ── 为什么还要这一步 ──
// --sdr-semantics-probe 已经把字节侧钉死：8-bit 会话交回的是 authored sRGB，逐枚 bit-exact
// （读数见 Core/Capture/SdrSessionSemantics.cs 的文件头）。所以"兜底覆盖层背景比实时桌面亮"
// 如果真存在，只剩呈现这一侧能解释：SelectionOverlay.RenderDesktopBackground 是把字节【原样】
// 写进 8-bit WriteableBitmap 的（零色彩转换）——万一 XAML/WinUI 在 HDR 屏上给这块表面的待遇
// 和普通 SDR 窗口不同（例如已经当成 EDR 内容，DWM 再抬一次），用户就会看到截图比桌面亮。
//
// ── 怎么把观感变成读数 ──
// 同屏并排放两块【同一个 authored 字节值 V】：
//   · 参照臂 = GDI 画的普通置顶窗（复用探针那扇补丁窗，不另写一份 WndProc —— 两台仪器必须
//     是同一种东西，否则"参照"两个字没有内容）；
//   · 被试臂 = 一扇无边框置顶 XAML 窗，内容用兜底层一模一样的配方（WriteableBitmap + 字节直写
//     PixelBuffer + Image 拉伸填满）。刻意不复用 SelectionOverlay 本尊：它一进来就带全屏矩形、
//     暗化层和工具条，那些都会污染要被测量的那一块。
// 然后用 Float16 会话读两块区域求比值。依据是探针那轮的实测：Float16 恒等于
// srgb_decode(字节)×(纸白/80) ⇒ 它是这台机器上唯一现成的"屏幕上到底多少尼特"的仪器；
// 参照臂与被试臂落在【同一帧】里，比值把纸白与面板峰值一并约掉。
//
//   ratio ≈ 1 ⇒ 同等待遇：兜底面的供帧语义正确，"XAML 面被多抬一次"这一支也被否证
//   ratio ≈ k ⇒ 第二抬坐实且被量化 ⇒ 修在兜底渲染器内部（÷k），捕获层与 D3D 那条已正确的路不许跟改
//   其余      ⇒ 灰带，不判
//
// 退出码表达【判没判】，不表达"截图对不对"——与 --window-probe / --sdr-semantics-probe 同一口径。

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TrueToneCap.Core.Capture;
using Windows.Graphics;
using System.Runtime.InteropServices.WindowsRuntime;   // PixelBuffer.AsStream() 这个扩展在这里，不在 Windows.Storage.Streams

namespace TrueToneCap.App.Services;

public static class OverlayWhiteLevelAb
{
    public const string Flag = "--overlay-whitelevel-ab";

    private static readonly byte[] TestGrays = [32, 96, 128, 192];

    private const int Side = 280;                 // 每块补丁的边长（物理像素）
    private const int Gap = 90;                   // 两臂之间的间隙
    private const int OriginX = 900, OriginY = 700;   // 参照臂左上角（物理像素）
    private const int SettleMs = 250;
    private const int MaxStableTries = 8;
    private const double StableTol = 0.002;       // float 线性均值的复读容差
    // 判定阈值不在这里：见 Core/Capture/SdrSessionSemantics.JudgePresentationAb

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out SdrSemanticsProbe.RECT rc);

    /// <summary>跑在 UI 线程上（全程 await，绝不阻塞消息循环 —— 参照臂的窗就建在这条线程上，
    /// 阻塞它等于让补丁画不上去、被试臂也停渲染）。</summary>
    public static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        string artifact = Path.Combine(AppContext.BaseDirectory, "overlay-whitelevel-ab.txt");
        nint ruler = 0;
        Window? xaml = null;
        void Say(string line)
        {
            sb.AppendLine(line);
            Console.WriteLine($"[overlay-ab] {line}");
            try { File.AppendAllText(artifact, line + "\n", new UTF8Encoding(false)); } catch { }
        }

        try
        {
            File.WriteAllText(artifact, "", new UTF8Encoding(false));
            var displays = DisplayEnumerator.EnumerateDisplays();
            var (sysHdr, acm, _) = CapabilityService.DetectDisplayState();
            int whiteNits = DisplayEnumerator.GetSdrWhiteLevel();
            double k = SdrSessionSemantics.SdrWhiteScale(whiteNits);
            var target = displays.FirstOrDefault(d => d.IsHdr)
                         ?? displays.FirstOrDefault(d => d.IsPrimary)
                         ?? displays.FirstOrDefault();
            if (target is null) { Say("★ 无显示器 ⇒ 不判"); return 1; }
            if (k <= 0) { Say($"★ 纸白读不到（{whiteNits}nits）⇒ 没有可比的标尺，不判"); return 1; }

            Say($"{Flag}　参照臂=GDI 普通置顶窗　被试臂=XAML WriteableBitmap（兜底层同款供帧配方）");
            Say($"  目标屏={target.Name} {target.Width}x{target.Height} HDR={sysHdr} ACM={acm} " +
                $"纸白={whiteNits}nits ⇒ k={k:0.000}");
            Say("  判读：ratio = 被试float均值 / 参照float均值；≈1 ⇒ 同等待遇；≈k ⇒ 被试面被多抬一次；其余 ⇒ 灰带不判");

            // ── 参照臂：探针那扇补丁窗，同一段实现 ──
            ruler = SdrSemanticsProbe.CreatePatchWindow(OriginX, OriginY, Side, Side,
                out var rRect, out string? rulerErr);
            if (ruler == 0) { Say($"★ 参照臂建不起来：{rulerErr} ⇒ 不判"); return 1; }
            // 建窗时那一次 WM_PAINT 早于任何换色，刷柄还是空 ⇒ 不先涂一帧就会留下一次假"绘制失败"，
            // 而这条告警以后会被当成真事故来读（探针那边同一件事写在 SetPatchColor(0) 的注释里）。
            SdrSemanticsProbe.PaintPatch(ruler, 0);
            Say($"  参照臂实得矩形=({rRect.Left},{rRect.Top})-({rRect.Right},{rRect.Bottom})");

            // ── 被试臂：XAML 窗 ──
            (xaml, var xRect) = await CreateXamlPatchAsync(OriginX + Side + Gap, OriginY, Side, Side);
            Say($"  被试臂实得矩形=({xRect.Left},{xRect.Top})-({xRect.Right},{xRect.Bottom})");
            if (xRect.Right - xRect.Left < 40 || xRect.Bottom - xRect.Top < 40)
            { Say("★ 被试臂矩形拿不到（或太小）⇒ 不判"); return 1; }

            var wgc = AppServices.Wgc;
            if (wgc is null) { Say("★ WGC 服务未初始化 ⇒ 不判"); return 1; }
            var cfg = new WgcCaptureConfig { PreferHdr = true, FrameTimeoutMs = 4000 };

            var ratios = new List<double>();
            Say("");
            Say("     V     参照float   被试float    ratio    参照8bit  被试8bit   稳定");
            foreach (byte v in TestGrays)
            {
                SdrSemanticsProbe.PaintPatch(ruler, v);
                SetXamlGray(xaml, v);
                await Task.Delay(SettleMs);

                var m = await MeasurePairAsync(wgc, cfg, rRect, xRect, target.X, target.Y);
                if (!m.Stable || m.RulerF <= 0)
                {
                    Say($"  {v,5}    —— 复读 {m.Tries} 次仍不稳定或参照臂无读数 ⇒ 这一档不进判定");
                    continue;
                }
                double ratio = m.XamlF / m.RulerF;
                ratios.Add(ratio);
                Say($"  {v,5}   {m.RulerF,9:0.0000} {m.XamlF,9:0.0000} {ratio,8:0.000}" +
                    $"   {m.Ruler8,7:0.00}  {m.Xaml8,7:0.00}   是({m.Tries})");
            }

            if (SdrSemanticsProbe.PatchPaintErrors > 0)
                Say($"  ⚠ 参照臂绘制失败 {SdrSemanticsProbe.PatchPaintErrors} 次（不影响已稳定的读数，但会污染未稳定档）");

            // 判定交回 Core 那把尺子（与探针同一个文件、同一套纪律）：这里一个字结论都不下 ——
            // 内联判读没法配合成对照臂，"这台仪器会红"就无从证明。
            var ab = SdrSessionSemantics.JudgePresentationAb(ratios, whiteNits);
            Say($"\n  读数 = {ab.Why}");
            Say($"  结论 = {ab.Verdict}");
            Say($"  含义 = {SdrSessionSemantics.Implication(ab)}");
            Console.WriteLine($"[overlay-ab] verdict={ab.Verdict} medianRatio={ab.MedianRatio:0.000} k={ab.SdrWhiteScale:0.000}");
            return ab.SelfCheckPassed && ab.Verdict is not SdrSessionSemantics.PresentationVerdict.Unknown ? 0 : 1;
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
            try { xaml?.Close(); } catch { }
            if (ruler != 0) SdrSemanticsProbe.ClosePatchWindow(ruler);
            try
            {
                File.WriteAllText(artifact, sb.ToString(), new UTF8Encoding(false));
                Console.WriteLine($"[overlay-ab] 读数已落盘: {artifact}");
            }
            catch { }
        }
    }

    // ── 被试臂的窗：配方逐条对齐 SelectionOverlay 的建窗与供帧（无边框 / 置顶 / 不抢焦点 /
    //    物理像素矩形 / WriteableBitmap 字节直写），差别只在"这一扇只装一块纯色"。
    private static async Task<(Window wnd, SdrSemanticsProbe.RECT rect)> CreateXamlPatchAsync(
        int x, int y, int w, int h)
    {
        var wnd = new Window { Title = "TTC A/B 被试臂" };
        var img = new Image { Stretch = Stretch.Fill };
        wnd.Content = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 0, 0)), Children = { img } };

        // ⚠ 必须是 GetWindowHandle → GetWindowIdFromWindow → GetFromWindowId 这一条顺序，
        // 而【不是】wnd.AppWindow：窗口还没激活时 AppWindow 属性拿到的是"未就绪"的状态，
        // 实测在其上设 PreferredHeightOption 直接抛 COMException「组或资源的状态不是执行请求操作的正确状态」。
        // 另外 ExtendsContentIntoTitleBar 必须排在 PreferredHeightOption 前面 —— 照抄 SelectionOverlay
        // 那扇真跑通的窗（:125-131）的顺序，被试臂才谈得上"同款配方"。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(wnd);
        var appw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appw.TitleBar.ExtendsContentIntoTitleBar = true;
        appw.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        appw.IsShownInSwitchers = false;
        if (appw.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        appw.MoveAndResize(new RectInt32(x, y, w, h));   // 物理像素

        wnd.Activate();
        await Task.Delay(400);                            // 走完首帧；这条线程继续泵消息
        GetWindowRect(hwnd, out var rc);
        return (wnd, rc);
    }

    /// <summary>与 RenderDesktopBackground 同配方：WriteableBitmap + PixelBuffer 字节直写 +
    /// Image 拉伸填满。64×64 足够 —— 被测量的是【一个灰值】，不是分辨率。</summary>
    private static void SetXamlGray(Window wnd, byte v)
    {
        const int N = 64;
        var bmp = new WriteableBitmap(N, N);
        var bytes = new byte[N * N * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = v; bytes[i + 1] = v; bytes[i + 2] = v; bytes[i + 3] = 255;
        }
        using (var stream = bmp.PixelBuffer.AsStream()) stream.Write(bytes, 0, bytes.Length);
        bmp.Invalidate();
        if (wnd.Content is Grid g && g.Children.Count > 0 && g.Children[0] is Image im) im.Source = bmp;
    }

    private readonly record struct Pair(double RulerF, double XamlF, double Ruler8, double Xaml8, int Tries, bool Stable);

    /// <summary>复读直到【两臂在同一次捕获里都与上一次一致】—— 与探针同一条取样纪律：
    /// 8-bit 会话与 Float16 会话不同步（实测能差一整档），不一致时读到的根本不是同一张画面。</summary>
    private static async Task<Pair> MeasurePairAsync(WgcCaptureService wgc, WgcCaptureConfig cfg,
        SdrSemanticsProbe.RECT rRect, SdrSemanticsProbe.RECT xRect, int monX, int monY)
    {
        var rs = ToSample(rRect, monX, monY);
        var xs = ToSample(xRect, monX, monY);
        double pR = double.NaN, pX = double.NaN;
        for (int i = 1; i <= MaxStableTries; i++)
        {
            var cap = await wgc.CaptureAllMonitorsAsync(cfg);
            try
            {
                if (cap.SdrPixels is null || cap.HdrPixels is null)
                    return new Pair(0, 0, 0, 0, i, false);
                var a = SdrSemanticsProbe.SampleRect(cap.SdrPixels, cap.HdrPixels, cap.Width, cap.Height, rs);
                var b = SdrSemanticsProbe.SampleRect(cap.SdrPixels, cap.HdrPixels, cap.Width, cap.Height, xs);
                if (!double.IsNaN(pR) && Math.Abs(a.FloatMean - pR) < StableTol && Math.Abs(b.FloatMean - pX) < StableTol)
                    return new Pair(a.FloatMean, b.FloatMean, a.Mean, b.Mean, i, true);
                pR = a.FloatMean; pX = b.FloatMean;
            }
            finally { cap.Dispose(); }
            await Task.Delay(SettleMs);
        }
        return new Pair(pR, double.NaN, 0, 0, MaxStableTries, false);
    }

    private static SdrSemanticsProbe.RectI ToSample(SdrSemanticsProbe.RECT rc, int monX, int monY)
    {
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        int ix = w / 4, iy = h / 4;   // 每边缩 25%：避开边缘、圆角、参照臂的投影
        return new SdrSemanticsProbe.RectI(rc.Left - monX + ix, rc.Top - monY + iy, w - 2 * ix, h - 2 * iy);
    }
}
