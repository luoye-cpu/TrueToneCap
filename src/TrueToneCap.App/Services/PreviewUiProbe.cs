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
//
// 窗口几何与产品一致：虚拟桌面原点 + 全尺寸（MainWindow 就是拿这组数调 Initialize）。
// 首版给小窗时，实机看到的是"变成一张小图、没有全屏覆盖"——探针的窗口形状也是判据的一部分。
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

            var wgc = AppServices.Wgc;
            if (wgc is null) { Say("★ WGC 服务未初始化 ⇒ 不判"); return 1; }

            // ── 真帧：与 MainWindow 同一条供帧链（HDR 优先，SDR 走 BgraToScrgbForDisplay）──
            var (sysHdr, _, _) = CapabilityService.DetectDisplayState();
            int whiteNits = DisplayEnumerator.GetSdrWhiteLevel();
            float[]? present;
            int frameW, frameH;
            using (var cap0 = await wgc.CaptureAllMonitorsAsync(new WgcCaptureConfig { PreferHdr = true, FrameTimeoutMs = 4000 }))
            {
                present = cap0.HdrPixels
                          ?? (cap0.SdrPixels is not null
                              ? PixelOps.BgraToScrgbForDisplay(cap0.SdrPixels, cap0.Width, cap0.Height, sysHdr, whiteNits)
                              : null);
                frameW = cap0.Width; frameH = cap0.Height;
            }
            if (present is null) { Say("★ 桌面帧抓不到 ⇒ 不判"); return 1; }
            Say($"{Flag}　虚拟桌面={vw}x{vh}@({vx},{vy})（全屏，与产品同几何）" +
                $"｜帧={frameW}x{frameH} 来源={(sysHdr ? "HDR/Float16 优先" : "SDR→scRGB")} 纸白={whiteNits}nits");

            win = new HdrCaptureWindow();
            if (!win.Initialize(vx, vy, vw, vh)) { Say($"★ 预览窗建不起来：{win.LastError ?? "无 LastError"} ⇒ 不判"); return 1; }
            win.LoadFrame(present, frameW, frameH);
            win.Render();
            await Task.Delay(SettleMs);

            hwnd = FindWindowW(ClassName, null);
            if (hwnd == 0) { Say("★ FindWindowW(TTC_HC) 找不到预览窗 ⇒ 不判"); return 1; }

            var cfg = new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 };

            // 悬停归一化：把高亮钉在屏幕左下角（任务栏一带），别落进中央测量区
            PostMessage(hwnd, WM_MOUSEMOVE, 0, LP(vx + 8, vy + vh - 8));
            await Task.Delay(SettleMs);

            // ── ① 静置提示：窗口中央应有一行白字（用"形状"判，不用绝对亮度）──
            var capA = await CaptureStableAsync(wgc, cfg);
            if (capA is null) { Say("★ 静置态捕获失败 ⇒ 不判"); return 1; }
            int cx = vx + vw / 2, cy = vy + vh / 2;
            var hintBand = new SdrSemanticsProbe.RectI(cx - 420, cy - 34, 840, 68);
            var (hintCount, hintCols, hintRows) = BrightProfile(capA, vw, vh, hintBand, 200);
            Say($"  ① 静置提示带：亮像素 {hintCount}｜亮列占比 {hintCols:P0}｜亮行占比 {hintRows:P0}");
            // 一行字：横跨大部分宽度、但只占少数行；整块亮区（悬停窗口/白底应用）行占比会接近 100%
            bool hintOk = hintCount >= 40 && hintCols >= 0.35 && hintRows <= 0.60;
            Say(hintOk ? "  ✓ 静置提示已画上去（细长一行白字，非整块亮区）"
                       : "  ★ 静置提示没画上，或中央被整块亮区覆盖 ⇒ 这次不判");

            // ── ② 拖拽中的尺寸徽标（结束前补 UP，释放鼠标捕获）──
            int dx1 = vx + vw * 15 / 100, dy1 = vy + vh * 15 / 100;
            int dx2 = vx + vw * 55 / 100, dy2 = vy + vh * 60 / 100;
            PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, LP(dx1, dy1));
            PostMessage(hwnd, WM_MOUSEMOVE, MK_LBUTTON, LP(dx2, dy2));
            dragging = true;
            await Task.Delay(SettleMs);

            var capB = await CaptureStableAsync(wgc, cfg);
            if (capB is null) { Say("★ 拖拽态捕获失败 ⇒ 不判"); return 1; }
            int selW = dx2 - dx1;
            int badgeW = 320, badgeH = 54;   // 测量带略高于实际徽标（40），留边
            int bx = Math.Clamp(dx1 + selW / 2 - badgeW / 2, vx + 4, vx + vw - badgeW - 4);
            int by = dy1 - badgeH - 4;
            if (by < vy + 4) by = dy1 + 6;
            var badgeBand = new SdrSemanticsProbe.RectI(bx, by, badgeW, badgeH);
            var (badgeCount, badgeCols, badgeRows) = BlueProfile(capB, vw, vh, badgeBand);
            var (hintCountB, _, _) = BrightProfile(capB, vw, vh, hintBand, 200);
            Say($"  ② 尺寸徽标带：蓝像素 {badgeCount}｜蓝列占比 {badgeCols:P0}｜蓝行占比 {badgeRows:P0}｜拖拽时中央提示亮像素 {hintCountB}");
            bool badgeOk = badgeCount >= 150 && badgeCols >= 0.50 && badgeRows <= 0.90 && hintCountB <= 20;
            Say(badgeOk ? "  ✓ 拖拽中尺寸徽标已画上去，且静置提示已让位"
                        : "  ★ 徽标没画上 / 形状不对 / 提示没隐藏 ⇒ 这次不判");

            // ── ③ 选区内是真桌面帧（不是一片灰）──
            // 判据：选区内一块取样按 5 bit/通道量化后数不同颜色 + 亮度标准差。
            // 断掉"帧没上传/上传成常数"的那一类：此时不同颜色 ≈ 1、标准差 ≈ 0。
            var inner = new SdrSemanticsProbe.RectI(dx1 + 60, dy1 + 60,
                Math.Max(64, (dx2 - dx1) / 2), Math.Max(64, (dy2 - dy1) / 2));
            var (distinct, std, mean) = Dispersion(capB, vw, vh, inner);
            Say($"  ③ 选区内部：不同颜色 {distinct} 种｜亮度标准差 {std:0.0}｜均值 {mean:0}");
            bool contentOk = distinct >= 100 && std >= 6.0;
            Say(contentOk ? "  ✓ 选区内是真实桌面内容（色散/方差都远高于纯色）"
                          : "  ★ 选区内像纯色 ⇒ 帧没到后台缓冲或颜色空间链断了（这才是「全灰」的真形状）");

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
                PostMessage(hwnd, WM_LBUTTONUP, 0, LP(0, 0));
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

    /// <summary>量化后的不同颜色数 + 亮度标准差 + 亮度均值：纯色帧三项分别是 1 / 0 / 常数。</summary>
    private static (int Distinct, double Std, double Mean) Dispersion(
        byte[] sdr, int w, int h, SdrSemanticsProbe.RectI r)
    {
        var set = new HashSet<int>();
        double sum = 0, sum2 = 0;
        int n = 0;
        for (int y = Math.Max(0, r.Y); y < Math.Min(h, r.Y + r.H); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(w, r.X + r.W); x++)
            {
                int i = (y * w + x) * 4;
                int b = sdr[i], g = sdr[i + 1], rr = sdr[i + 2];
                set.Add(((rr >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                double lum = 0.299 * rr + 0.587 * g + 0.114 * b;
                sum += lum; sum2 += lum * lum; n++;
            }
        if (n == 0) return (0, 0, 0);
        double mean = sum / n;
        double var = Math.Max(0, sum2 / n - mean * mean);
        return (set.Count, Math.Sqrt(var), mean);
    }
}
