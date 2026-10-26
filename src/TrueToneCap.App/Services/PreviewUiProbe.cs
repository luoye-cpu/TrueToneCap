// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/PreviewUiProbe.cs
//
// 真机探针：TrueToneCap.exe --preview-ui-probe
//
// 判什么：选区预览窗（HdrCaptureWindow）里三件"看不见就说不清"的事 ——
//   ① 静置操作提示真的画上去了（未框选时居中一行白字）；
//   ② 拖拽中的尺寸徽标真的画上去了（选区上方蓝底白字的 W×H）；
//   ③ 选区内是【真桌面帧】而不是一片灰（帧上传/颜色空间整条链有画面）。
// 前两件是"烘进位图的字"，UIA 与本地化扫描都看不见；第三件是实机报"覆盖面全灰"时
// 唯一能当场分辨"真 bug / 探针自己喂的灰帧"的读数 —— 所以本探针【必须】喂真帧，不许再喂纯色。
// ③ 的判据是【与建窗前的 8-bit 基线帧做 Pearson 相关】，不是"色散够不够大"：一整块单调的浅灰
// 壁纸同样色散很小，首版按色散判红是判据错（把好画面判成灰帧），换成"两张图是不是同一幅画面"。
//
// 窗口几何与产品一致：虚拟桌面原点 + 全尺寸（MainWindow 就是拿这组数调 Initialize）。
// 首版给小窗时，实机看到的是"变成一张小图、没有全屏覆盖"——探针的窗口形状也是判据的一部分。
// 坐标系只有一套：窗的客户区原点就是虚拟桌面原点 ⇒ WM_* 的消息坐标与 WGC 帧的像素坐标同值。
// 首版把屏幕坐标用进了消息与取样带，多屏负原点时会整体偏移 (vx,vy)。
//
// 仪器：WGC 拿 SDR 会话整屏（与 --overlay-whitelevel-ab 同一台仪器、同一条稳定复读纪律）。
// 触发拖拽不注入真实键鼠：PostMessage 到探针自己建的那扇窗（FindWindowW 按类名 TTC_HC 找到），
// 且结束前一定补一发 WM_LBUTTONUP 释放鼠标捕获 —— 首版漏了这步，实机上把用户自己的拖拽吸进了探针窗。
// 悬停归一化：先发一条不带按键的 WM_MOUSEMOVE 到屏幕左下角（任务栏一带），把"悬停高亮"
// 固定在边缘，中央的提示/徽标读数才不会被某个亮窗口污染。
//
// 退出码表达【判没判】，不表达"截图好不好看"——与 --window-probe / --sdr-semantics-probe 同一口径。

using System.Runtime.InteropServices;
using System.Text;
using TrueToneCap.Core;
using TrueToneCap.Core.Capture;

namespace TrueToneCap.App.Services;

public static class PreviewUiProbe
{
    public const string Flag = "--preview-ui-probe";

    private const string ClassName = "TTC_HC";   // HdrCaptureWindow.CN（探针只按类名找窗，不要求它改成 public）

    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
    private const int MK_LBUTTON = 0x0001;

    private const int SettleMs = 600;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? cls, string? title);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint h, uint msg, nint w, nint l);

    private static nint LP(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    public static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        string artifact = Path.Combine(AppContext.BaseDirectory, "preview-ui-probe.txt");
        void Say(string line)
        {
            sb.AppendLine(line);
            Console.WriteLine($"[preview-ui] {line}");
            try { File.AppendAllText(artifact, line + "\n", new UTF8Encoding(false)); } catch { }
        }

        HdrCaptureWindow? win = null;
        nint hwnd = 0;
        bool dragging = false;
        int upX = 0, upY = 0;   // 松开点（客户区坐标），finally 里一定要补发
        try
        {
            File.WriteAllText(artifact, "", new UTF8Encoding(false));
            var displays = DisplayEnumerator.EnumerateDisplays();
            if (displays.Count == 0) { Say("★ 无显示器 ⇒ 不判"); return 1; }
            // 与 MainWindow.StartSelectionCapture 完全同一组几何
            int vx = displays.Min(d => d.X);
            int vy = displays.Min(d => d.Y);
            int vw = displays.Max(d => d.X + d.Width) - vx;
            int vh = displays.Max(d => d.Y + d.Height) - vy;

            // 拖拽点（客户区 = 帧像素坐标）
            int cdx1 = vw * 15 / 100, cdy1 = vh * 15 / 100;
            int cdx2 = vw * 55 / 100, cdy2 = vh * 60 / 100;

            // 测量带：提示居中 840x68；徽标在选区上方居中 320x54（产品实测宽约 160、高约 42，带留余量）
            var hintBand = new SdrSemanticsProbe.RectI(vw / 2 - 420, vh / 2 - 34, 840, 68);
            int selW = cdx2 - cdx1;
            int badgeW = 320, badgeH = 54;
            int bx = Math.Clamp(cdx1 + selW / 2 - badgeW / 2, 4, Math.Max(4, vw - badgeW - 4));
            int by = cdy1 - badgeH - 4;
            if (by < 4) by = cdy1 + 6;
            var badgeBand = new SdrSemanticsProbe.RectI(bx, by, badgeW, badgeH);
            // ③ 的取样区 = 选区内部（内缩 10px 避开选区描边），两条被测带在取样时单独剔除
            var contentBand = new SdrSemanticsProbe.RectI(cdx1 + 10, cdy1 + 10,
                Math.Max(64, selW - 20), Math.Max(64, cdy2 - cdy1 - 20));

            var wgc = AppServices.Wgc;
            if (wgc is null) { Say("★ WGC 服务未初始化 ⇒ 不判"); return 1; }

            // ── 真帧：与 MainWindow 同一条供帧链（HDR 优先，SDR 走 BgraToScrgbForDisplay）──
            var (sysHdr, _, _) = CapabilityService.DetectDisplayState();
            int whiteNits = DisplayEnumerator.GetSdrWhiteLevel();
            float[]? present;
            byte[]? baseline;
            int frameW, frameH;
            using (var cap0 = await wgc.CaptureAllMonitorsAsync(new WgcCaptureConfig { PreferHdr = true, FrameTimeoutMs = 4000 }))
            {
                present = cap0.HdrPixels
                          ?? (cap0.SdrPixels is not null
                              ? PixelOps.BgraToScrgbForDisplay(cap0.SdrPixels, cap0.Width, cap0.Height, sysHdr, whiteNits)
                              : null);
                // 基线：窗还没建，这一帧就是"真桌面"本身。8-bit 会话交回 authored sRGB，
                // 窗把它呈现出来再被抓一次仍是同一编码 ⇒ 两端才能直接算相关。
                baseline = cap0.SdrPixels is not null ? (byte[])cap0.SdrPixels.Clone() : null;
                frameW = cap0.Width; frameH = cap0.Height;
            }
            if (present is null) { Say("★ 桌面帧抓不到 ⇒ 不判"); return 1; }
            if (frameW != vw || frameH != vh)
            { Say($"★ 帧尺寸 {frameW}x{frameH} 与虚拟桌面 {vw}x{vh} 不一致（多屏拼接有偏移）⇒ 本探针不做坐标换算，不判"); return 1; }
            if (baseline is null || baseline.Length != vw * vh * 4)
            {
                baseline = await CaptureStableAsync(wgc, new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 });
                if (baseline is null || baseline.Length != vw * vh * 4) { Say("★ 8-bit 基线帧抓不到 ⇒ 不判"); return 1; }
            }
            // 被测带铺成固定暗灰：提示/徽标是白字，只有背景可控时才判得动形状；其余像素保持真实桌面
            // （③ 的相关取样在选区内部，两条带会被单独剔除）。这样悬停高亮/白底应用不会再让读数变成"整块亮区"。
            PaintRect(present, frameW, frameH, hintBand.X, hintBand.Y, hintBand.W, hintBand.H, 0.04f);
            PaintRect(present, frameW, frameH, badgeBand.X, badgeBand.Y, badgeBand.W, badgeBand.H, 0.04f);
            Say($"{Flag}　虚拟桌面={vw}x{vh}@({vx},{vy})（全屏，与产品同几何）" +
                $"｜帧={frameW}x{frameH} 来源={(sysHdr ? "HDR/Float16 优先" : "SDR→scRGB")} 纸白={whiteNits}nits" +
                "｜提示/徽标两条被测带在帧里铺成固定暗灰，其余像素是真桌面");

            win = new HdrCaptureWindow();
            if (!win.Initialize(vx, vy, vw, vh)) { Say($"★ 预览窗建不起来：{win.LastError ?? "无 LastError"} ⇒ 不判"); return 1; }
            win.LoadFrame(present, frameW, frameH);
            win.Render();
            await Task.Delay(SettleMs);

            hwnd = FindWindowW(ClassName, null);
            if (hwnd == 0) { Say("★ FindWindowW(TTC_HC) 找不到预览窗 ⇒ 不判"); return 1; }

            var cfg = new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 };

            // 悬停归一化：把高亮钉在屏幕左下角（任务栏一带），别落进中央测量区
            PostMessage(hwnd, WM_MOUSEMOVE, 0, LP(8, vh - 8));
            await Task.Delay(SettleMs);

            // ── ① 静置提示：窗口中央应有一行白字（用"形状"判，不用绝对亮度）──
            var capA = await CaptureStableAsync(wgc, cfg);
            if (capA is null) { Say("★ 静置态捕获失败 ⇒ 不判"); return 1; }
            var (hintCount, hintCols, hintRows) = BrightProfile(capA, vw, vh, hintBand, 200);
            Say($"  ① 静置提示带：亮像素 {hintCount}｜亮列占比 {hintCols:P0}｜亮行占比 {hintRows:P0}");
            // 一行字：横跨大部分宽度、但只占少数行；整块亮区（悬停窗口/白底应用）行占比会接近 100%
            bool hintOk = hintCount >= 40 && hintCols >= 0.35 && hintRows <= 0.60;
            Say(hintOk ? "  ✓ 静置提示已画上去（细长一行白字，非整块亮区）"
                       : "  ★ 静置提示没画上，或中央被整块亮区覆盖 ⇒ 这次不判");

            // ── ② 拖拽中的尺寸徽标（结束前补 UP，释放鼠标捕获；坐标已在上面算好）──
            upX = cdx1; upY = cdy1;
            PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, LP(cdx1, cdy1));
            PostMessage(hwnd, WM_MOUSEMOVE, MK_LBUTTON, LP(cdx2, cdy2));
            upX = cdx2; upY = cdy2;
            dragging = true;
            await Task.Delay(SettleMs);

            var capB = await CaptureStableAsync(wgc, cfg);
            if (capB is null) { Say("★ 拖拽态捕获失败 ⇒ 不判"); return 1; }
            var (badgeCount, badgeCols, badgeRows) = BlueProfile(capB, vw, vh, badgeBand);
            var (hintCountB, _, _) = BrightProfile(capB, vw, vh, hintBand, 200);
            Say($"  ② 尺寸徽标带：蓝像素 {badgeCount}｜蓝列占比 {badgeCols:P0}｜蓝行占比 {badgeRows:P0}｜拖拽时中央提示亮像素 {hintCountB}");
            bool badgeOk = badgeCount >= 150 && badgeCols >= 0.50 && badgeRows <= 0.90 && hintCountB <= 20;
            Say(badgeOk ? "  ✓ 拖拽中尺寸徽标已画上去，且静置提示已让位"
                        : "  ★ 徽标没画上 / 形状不对 / 提示没隐藏 ⇒ 这次不判");

            // ── ③ 选区内还是【同一张桌面】，不是一片灰 ──
            // 把窗内选区（剔掉两条铺灰带）与建窗前的 8-bit 基线帧做 Pearson 相关：
            // 帧真的进了窗、窗真的画在屏幕上 ⇒ 两张图是同一幅画面的两个副本，r 应接近 1；
            // "帧没上传/上传成常数/颜色空间链断"（实机报的"全灰"）会掉到 0 附近。
            // 不用色散：一整块单调的浅灰壁纸同样色散很小，上一轮 21 色/σ31 判红就是判据错。
            var (corr, baseStd, samples) = Correlate(capB, baseline, vw, vh, contentBand, hintBand, badgeBand);
            Say($"  ③ 选区内部 vs 建窗前基线：Pearson r={corr:0.000}｜基线标准差 {baseStd:0.0}｜样本 {samples} 像素");
            if (baseStd < 6.0)
            { Say("  ★ 基线取样区本来就近乎纯色（σ<6）⇒ 相关判不动；让出一块有内容的桌面再跑"); return 1; }
            bool contentOk = corr >= 0.90;
            Say(contentOk ? "  ✓ 选区内呈现的是那张真桌面帧（与基线高度相关）"
                          : "  ★ 选区内与基线对不上 ⇒ 帧没到后台缓冲 / 颜色空间链断了（「全灰」的真形状）");

            return hintOk && badgeOk && contentOk ? 0 : 1;
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
            // 先释放鼠标捕获，再关窗：否则那一段里用户的真实鼠标事件会被吸进探针窗
            if (dragging && hwnd != 0)
                PostMessage(hwnd, WM_LBUTTONUP, 0, LP(upX, upY));
            try { win?.Close(); } catch { }
            try { win?.Dispose(); } catch { }
            try { File.WriteAllText(artifact, sb.ToString(), new UTF8Encoding(false)); } catch { }
            Console.WriteLine($"[preview-ui] 读数已落盘: {artifact}");
        }
    }

    /// <summary>复读两次一致才交读数（8-bit 会话可能落后一帧，探针那轮踩过整整一档）。</summary>
    private static async Task<byte[]?> CaptureStableAsync(WgcCaptureService wgc, WgcCaptureConfig cfg)
    {
        byte[]? prev = null;
        for (int i = 0; i < 6; i++)
        {
            using var cap = await wgc.CaptureAllMonitorsAsync(cfg);
            var px = cap.SdrPixels;
            if (px is null) { await Task.Delay(150); continue; }
            if (prev is not null && SameBytes(prev, px)) return px;
            prev = (byte[])px.Clone();
            await Task.Delay(150);
        }
        return prev;
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int step = Math.Max(4, a.Length / 4096 / 4 * 4);   // 抽样足够代表一帧，省掉一次全量比较
        for (int i = 0; i + 3 < a.Length; i += step)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false;
        return true;
    }

    /// <summary>把帧里的一块铺成固定线性灰（只用于两条被测带，其余像素保持真桌面）。</summary>
    private static void PaintRect(float[] px, int w, int h, int x, int y, int rw, int rh, float v)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(w, x + rw), y1 = Math.Min(h, y + rh);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int i = (yy * w + xx) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;
            }
    }

    private static bool IsBright(byte[] sdr, int i, int threshold)
        => sdr[i] + sdr[i + 1] + sdr[i + 2] >= threshold * 3;

    private static bool IsBlue(byte[] sdr, int i)
    {
        int b = sdr[i], g = sdr[i + 1], rr = sdr[i + 2];
        return b > 120 && b - rr >= 40 && b - g >= 20;
    }

    /// <summary>亮像素计数 + 覆盖到的列/行占比：一行字是"宽而薄"，整块亮区是"又宽又厚"。</summary>
    private static (int Count, double Cols, double Rows) BrightProfile(
        byte[] sdr, int w, int h, SdrSemanticsProbe.RectI r, int threshold)
        => Profile(sdr, w, h, r, i => IsBright(sdr, i, threshold));

    private static (int Count, double Cols, double Rows) BlueProfile(
        byte[] sdr, int w, int h, SdrSemanticsProbe.RectI r)
        => Profile(sdr, w, h, r, i => IsBlue(sdr, i));

    private static (int Count, double Cols, double Rows) Profile(
        byte[] sdr, int w, int h, SdrSemanticsProbe.RectI r, Func<int, bool> hit)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(w, r.X + r.W), y1 = Math.Min(h, r.Y + r.H);
        if (x1 <= x0 || y1 <= y0) return (0, 0, 0);
        var cols = new bool[x1 - x0];
        var rows = new bool[y1 - y0];
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                if (!hit(i)) continue;
                n++; cols[x - x0] = true; rows[y - y0] = true;
            }
        return (n, cols.Count(b => b) / (double)cols.Length, rows.Count(b => b) / (double)rows.Length);
    }

    /// <summary>Pearson 相关：窗内选区呈现的到底是不是建窗前那张桌面。剔除两条铺灰带。</summary>
    private static (double R, double BaseStd, int N) Correlate(
        byte[] shot, byte[] baseline, int w, int h,
        SdrSemanticsProbe.RectI r, SdrSemanticsProbe.RectI skip1, SdrSemanticsProbe.RectI skip2)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int n = 0;
        for (int y = Math.Max(0, r.Y); y < Math.Min(h, r.Y + r.H); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(w, r.X + r.W); x++)
            {
                if (Inside(skip1, x, y) || Inside(skip2, x, y)) continue;
                int i = (y * w + x) * 4;
                double a = Lum(shot, i), b = Lum(baseline, i);
                sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
                n++;
            }
        if (n < 2) return (0, 0, n);
        double ma = sa / n, mb = sb / n;
        double va = Math.Max(0, saa / n - ma * ma), vb = Math.Max(0, sbb / n - mb * mb);
        double cov = sab / n - ma * mb;
        double rv = va > 1e-9 && vb > 1e-9 ? cov / Math.Sqrt(va * vb) : 0;
        return (rv, Math.Sqrt(vb), n);
    }

    private static bool Inside(SdrSemanticsProbe.RectI r, int x, int y)
        => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    private static double Lum(byte[] sdr, int i)
        => 0.299 * sdr[i + 2] + 0.587 * sdr[i + 1] + 0.114 * sdr[i];
}
