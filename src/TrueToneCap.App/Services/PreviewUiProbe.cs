// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/PreviewUiProbe.cs
//
// 真机探针：TrueToneCap.exe --preview-ui-probe
//
// 判什么：选区预览窗（HdrCaptureWindow）里四件"看不见就说不清"的事 ——
//   ① 静置操作提示真的画上去了（未框选时居中一行白字）；
//   ② 尺寸徽标真的画上去了、且静置提示已让位（选区上方蓝底白字的 W×H；读数取"松手落定"那一帧，
//      原因见 ② 那段注释：拖拽中的矩形不由探针决定，见 instrument 段）；
//   ③ 选区内是【真桌面帧】而不是一片灰（帧上传/颜色空间整条链有画面）；
//   ④ 拖四角手柄只动被抓住的那个角（实机报"点角调大小、范围却乱跳"的回归判据）；
//   ⑤–⑧ 选区交互契约：框内平移、框内单击不乱跳、框外按下重画、角拖过对角翻面。
//   ⑨ 富文本文字标注：单击→输入框→图层（字体/字号/加粗现在住在 D3D 第二行里，探针拿窗自己算出的
//      格子矩形、用真鼠标消息点「字号格→列表第 N 项」「B 格」；2026-10-28 起 Win32 控件条已删除，
//      旧判据靠 GetDlgItem 找控件，控件没了就整相"不判"——那正是这条改道的原因）→撤销/重做→
//      拖动移动（一条可撤销的 UpdateLayer）→再编辑；读数是窗自己交出的图层状态。
//   ⑩ 置顶贴图窗：能开、带 WS_EX_TOPMOST、滚轮 +120 宽按 1.2 倍、工具栏"复制"调到回调、能关。
//   ⑪ 标注几何（像素级）：箭头落在拖拽的那条线上、马赛克只糊自己那块（读屏幕，不看图层状态）。
//   ⑫ 画笔粗细滑条：点「画笔涂抹」换形态 → 拖轨两端与中段 → 涂一笔 → 图层的 BrushWidth 必须等于
//      探针【自己】从轨道几何算出的期望值（期望值不复用窗的换算，否则三处一起漂就照不出）。
//   ⑬ 工具条/选项行的点击不许改写选区（按下被按钮吃掉时，松手会落到"重画选区"的收尾——
//      真机读到过 1280x820 → 1133x861，保存出去的图比用户框的窄 147 px）。
//   ⑭ 托盘注册/删除（真机）：用一扇真窗的 HWND 真发 Shell_NotifyIcon，量注册结果、NIM_DELETE 的
//      真实返回、连删两次只发一次删（幂等闸）、Dispose 之后不再加删。
// ④ 的读数是窗自己报出来的选中矩形（ActionCompleted 的 Save 参数）——那也正是最终裁切的矩形；
// 旧实现把松手点无条件写进 (_sx2,_sy2)，抓 TL/TR/BL 一松手选区就塌成一条线，本条必红。
// ⑤–⑧ 同一条读数路线，每相一扇新窗：先拖出基准选区 A（按窗口尺寸换算，不写死分辨率），再施加被测手势。
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
// ⚠ 第 2 版（2026-10-27）实测到两件事，都已按它们改了判据：
//   ① 窗 SetCapture 之后系统会按【物理光标】补发移动，而本机 SetCursorPos 被拒（实读数 False）
//      ⇒ 拖拽中的矩形不由探针决定。所以像素读数取"松手落定"那一帧；④–⑧ 的读数本来就只依赖
//      我们自己发的松手点/手势终点。
//   ② 显示器切换的瞬间，DisplayEnumerator 的读数可能与真实客户区不一致（实测枚举 2560x1440、
//      窗是 2560x1600）⇒ 建窗后用 GetClientRect 复核，不一致就明说"不判"，不拿错尺子量。
// 悬停归一化：先发一条不带按键的 WM_MOUSEMOVE 到屏幕左下角（任务栏一带），把"悬停高亮"
// 固定在边缘，中央的提示/徽标读数才不会被某个亮窗口污染。
//
// 退出码表达【判没判】，不表达"截图好不好看"——与 --window-probe / --sdr-semantics-probe 同一口径。

using System.Runtime.InteropServices;
using System.Text;
using TrueToneCap.App.Models;
using TrueToneCap.Core;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Capture;

namespace TrueToneCap.App.Services;

public static class PreviewUiProbe
{
    public const string Flag = "--preview-ui-probe";

    private const string ClassName = "TTC_HC";   // HdrCaptureWindow.CN（探针只按类名找窗，不要求它改成 public）

    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
    private const uint WM_KEYDOWN = 0x0100;
    private const int VK_S = 0x53, VK_ENT = 0x0D;
    private const int MK_LBUTTON = 0x0001;

    private const int SettleMs = 600;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? cls, string? title);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint h, uint msg, nint w, nint l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint h, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint h, out RECT r);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint h, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint h, StringBuilder sb, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint h, out RECT r);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static string CursorNote()
    {
        var s = CursorState();
        return s.Ok ? $"({s.X},{s.Y}) 左键={(s.Down ? "按下" : "抬起")}" : "读不到";
    }

    private static (bool Ok, int X, int Y, bool Down) CursorState()
    {
        if (!GetCursorPos(out var p)) return (false, 0, 0, false);
        return (true, p.X, p.Y, (GetAsyncKeyState(0x01) & 0x8000) != 0);
    }

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
        (bool Ok, int X, int Y, bool Down) cursor0 = default;   // 进场时的真实光标，散场要还回去
        try
        {
            File.WriteAllText(artifact, "", new UTF8Encoding(false));
            var displays = DisplayEnumerator.EnumerateDisplaysUncached();
            if (displays.Count == 0) { Say("★ 无显示器 ⇒ 不判"); return 1; }
            // 与 MainWindow.StartSelectionCapture 完全同一组几何
            int vx = displays.Min(d => d.X);
            int vy = displays.Min(d => d.Y);
            int vw = displays.Max(d => d.X + d.Width) - vx;
            int vh = displays.Max(d => d.Y + d.Height) - vy;

            // 客户区坐标 → 把真实光标也挪过去（窗 SetCapture 后系统会按物理光标补发移动，
            // 只发消息不挪光标，最后落地的就是那颗没动的光标）。退出时还原到原处。
            cursor0 = CursorState();
            void CursorAt(int x, int y) => SetCursorPos(vx + x, vy + y);

            // 拖拽点（客户区 = 帧像素坐标）
            int cdx1 = vw * 15 / 100, cdy1 = vh * 15 / 100;
            int cdx2 = vw * 55 / 100, cdy2 = vh * 60 / 100;

            // 测量带：提示居中 840x68；徽标带按 DPI 算 —— 产品画的是 28×scale 高的圆角色块，
            // 带子必须用同一把尺子留余量（旧版固定 320x54 在 150% 缩放下只剩一像素余量，
            // 判据"蓝列占比≥50%"恰好在边界上，好画面会被判红）。宽度 480 是给"拖拽状态被
            // 真实光标挪了几十像素"留的位置余量。
            var hintBand = new SdrSemanticsProbe.RectI(vw / 2 - 420, vh / 2 - 34, 840, 68);
            int selW = cdx2 - cdx1;
            double uiScale = GetDpiForSystem() / 96.0;
            int badgeH = Math.Max(20, (int)Math.Round(28 * uiScale));
            int badgeW = 480, badgeBandH = badgeH + 14;
            int bx = Math.Clamp(cdx1 + selW / 2 - badgeW / 2, 4, Math.Max(4, vw - badgeW - 4));
            // 带底停在选区上沿之上 2px：再往下就会把【选区描边】也算进蓝色外接框
            // （实测 480x5 的一条横线，宽高比与徽标完全不符）。
            var badgeBand = new SdrSemanticsProbe.RectI(bx, Math.Max(4, cdy1 - badgeH - 16), badgeW, badgeBandH);
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

            // ── 与产品同一条投递点：先在"选区起点"把图集预热一次，再等它落地（有界 2s）──
            // 之后每一扇窗（含下面 14 相读屏幕像素的那些）用的都是【预热壳】烘出来的那张图集。
            // 壳与真窗口只要有一处输入（DPI/字体/字号/下拉/悬停/文字层）或一处几何漂了，
            // ① 静置提示带、⑨ 文字带、⑫ 滑条那几相会当场红 —— 这是壳这条新路唯一的像素级验收。
            // TTC_ATLAS_CACHE=0 时预热按设计不投递，这一相直接"不判"，但会打出来，免得被读成通过。
            bool prewarmJudged = Environment.GetEnvironmentVariable("TTC_ATLAS_CACHE") != "0";
            bool prewarmOk = true;
            if (prewarmJudged)
            {
                nint prewarmMon = DisplayEnumerator.GetMonitorUnderCursor();
                HdrCaptureWindow.PrewarmAtlasForMonitor(prewarmMon);
                bool warm = false;
                for (int i = 0; i < 40 && !warm; i++)
                {
                    warm = HdrCaptureWindow.AtlasCacheWarmForMonitor(prewarmMon);
                    if (!warm) await Task.Delay(50);
                }
                prewarmOk = warm;
                Say($"  · 图集预热(探针): {(warm ? "已落地，后面每一扇窗都吃缓存 ✓" : "2s 未落地 ★ 预热没落地")}");
            }
            else
            {
                Say("  · 图集预热(探针): TTC_ATLAS_CACHE=0 ⇒ 本臂不投递、不判");
            }

            win = new HdrCaptureWindow();
            if (!win.Initialize(vx, vy, vw, vh)) { Say($"★ 预览窗建不起来：{win.LastError ?? "无 LastError"} ⇒ 不判"); return 1; }
            win.LoadFrame(present, frameW, frameH);
            win.Render();
            await Task.Delay(SettleMs);

            hwnd = win.Hwnd;
            if (hwnd == 0) { Say("★ 预览窗建不起来（实例句柄为 0）⇒ 不判"); return 1; }
            // 枚举出来的虚拟桌面必须是窗的客户区，否则下面每条坐标（拖拽点、被测带）都按错的尺子量。
            // 实机踩过：显示器切换的瞬间枚举到 2560x1440 而窗真的建成了 2560x1600，
            // 于是硬编码的拖拽点撞上产品的窗口钳位，读出来像"产品算错了"。
            if (!GetClientRect(hwnd, out var cr0))
            { Say("★ GetClientRect 失败 ⇒ 量不了窗的客户区，不判"); return 1; }
            if (cr0.Right - cr0.Left != vw || cr0.Bottom - cr0.Top != vh)
            {
                Say($"★ 预览窗客户区 {cr0.Right - cr0.Left}x{cr0.Bottom - cr0.Top} 与枚举的虚拟桌面 {vw}x{vh} 不一致" +
                    " ⇒ 坐标换算不成立（多半是显示器刚切换），不判");
                return 1;
            }

            var cfg = new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 };

            // 悬停归一化：把高亮钉在屏幕左下角（任务栏一带），别落进中央测量区
            PostMessage(hwnd, WM_MOUSEMOVE, 0, LP(8, vh - 8));
            await Task.Delay(SettleMs);

            // ── ① 静置提示：窗口中央应有一行白字（用"形状"判，不用绝对亮度）──
            // ⚠ 第一帧可能还没上屏：渲染线程要建 Win2D 共享设备 + 烘 2048×1152 图集，
            // 冷启动时比固定等待更慢（2026-10-27 实测到过一次"亮像素 0"，同一份代码下两轮后即恢复）。
            // 所以这里复读到达：同一带连读两次一致【且已经看到字】才交读数；始终没字就按最后一次判红。
            int hintCount = 0, hintTries = 0;
            double hintCols = 0, hintRows = 0;
            for (; hintTries < 8; hintTries++)
            {
                var capTry = await CaptureStableAsync(wgc, cfg);
                if (capTry is null) { Say("★ 静置态捕获失败 ⇒ 不判"); return 1; }
                (hintCount, hintCols, hintRows) = BrightProfile(capTry, vw, vh, hintBand, 200);
                if (hintCount >= 40) break;
                await Task.Delay(200);
            }
            var capA = await CaptureStableAsync(wgc, cfg);
            if (capA is null) { Say("★ 静置态捕获失败 ⇒ 不判"); return 1; }
            (hintCount, hintCols, hintRows) = BrightProfile(capA, vw, vh, hintBand, 200);
            Say($"  ① 静置提示带：亮像素 {hintCount}｜亮列占比 {hintCols:P0}｜亮行占比 {hintRows:P0}（等待 {hintTries} 轮）");
            // 一行字：横跨大部分宽度、但只占少数行；整块亮区（悬停窗口/白底应用）行占比会接近 100%
            bool hintOk = hintCount >= 40 && hintCols >= 0.35 && hintRows <= 0.60;
            Say(hintOk ? "  ✓ 静置提示已画上去（细长一行白字，非整块亮区）"
                       : "  ★ 静置提示没画上，或中央被整块亮区覆盖 ⇒ 这次不判");

            // ── ② 尺寸徽标：拖出一条选区并松手，徽标应落在选区上方居中、静置提示让位 ──
            upX = cdx1; upY = cdy1;
            bool lastCursorOk = false;
            PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, LP(cdx1, cdy1));
            // 按真实拖拽分段下发移动（一次跳变不是用户的手），然后【松手再读像素】。
            // 为什么不在拖拽中读：窗 SetCapture 之后系统会按【物理光标】补发一条移动，而本机
            // SetCursorPos 被系统拒绝（实机读数 False）⇒ 拖拽中的矩形不由探针决定（实机读到过
            // BR 落在物理光标的 2510x906，徽标画得没错、是量错了地方）。松手点是我们发的，
            // 落定后的矩形 100% 确定，徽标位置才是可判的。
            for (int i = 1; i <= 4; i++)
            {
                int mx = cdx1 + (cdx2 - cdx1) * i / 4, my = cdy1 + (cdy2 - cdy1) * i / 4;
                lastCursorOk = SetCursorPos(vx + mx, vy + my);
                PostMessage(hwnd, WM_MOUSEMOVE, MK_LBUTTON, LP(mx, my));
                await Task.Delay(40);
            }
            upX = cdx2; upY = cdy2;
            dragging = true;
            PostMessage(hwnd, WM_LBUTTONUP, 0, LP(cdx2, cdy2));
            dragging = false;   // 鼠标捕获已在上面的 UP 里释放，finally 不用再补
            await Task.Delay(SettleMs);

            var capB = await CaptureStableAsync(wgc, cfg);
            if (capB is null) { Say("★ 拖拽态捕获失败 ⇒ 不判"); return 1; }
            var (badgeCount, _, _) = BlueProfile(capB, vw, vh, badgeBand);
            var (bl, bt, br, bb, bn, rows) = BadgeBox(capB, vw, vh, badgeBand);
            var (hintCountB, _, _) = BrightProfile(capB, vw, vh, hintBand, 200);
            // 画面跟上消息有延迟：实机读到过"MOVE 已生效（随后 ④ 的 BR 分毫不差）但这一次捕获里
            // 徽标还没出现"，固定 settle 会把好画面判红。轮询到证据出现为止，超时仍红。
            for (int i = 0; i < 6 && (rows == 0 || hintCountB > 20); i++)
            {
                await Task.Delay(250);
                var again = await CaptureStableAsync(wgc, cfg);
                if (again is null) break;
                capB = again;
                (badgeCount, _, _) = BlueProfile(capB, vw, vh, badgeBand);
                (bl, bt, br, bb, bn, rows) = BadgeBox(capB, vw, vh, badgeBand);
                (hintCountB, _, _) = BrightProfile(capB, vw, vh, hintBand, 200);
            }
            Say($"  ② 尺寸徽标带：蓝像素(全带) {badgeCount}｜徽标行 {rows}" +
                $"｜徽标框 {(rows == 0 ? "无" : $"{br - bl + 1}x{bb - bt + 1}@({bl},{bt})（蓝像素 {bn}）")}" +
                $"｜拖拽时中央提示亮像素 {hintCountB}");
            // 探针窗是全屏最前的真实窗口：人一动鼠标，那些事件就串进这次"拖拽"（换的只是
            // 徽标里的数字，不影响"画没画上去"这条结论）。这条读数留作现场解释用。
            var curAfter = CursorState();
            Say($"  · 真实鼠标 {CursorNote()}｜SetCursorPos→{lastCursorOk}｜预期拖到 ({vx + cdx2},{vy + cdy2})" +
                $"｜DPI={GetDpiForSystem()} scale={uiScale:0.00} 徽标带 {badgeW}x{badgeBandH}");
            // 判据看徽标框的形状，不看整带占比：徽标是"宽 90–400、高 = 28×scale ±6"的圆角块，
            // 里面显示的数字随拖拽状态变化（宽度也会变），拿列占比判等于把数字长度当判据。
            int bboxW = br - bl + 1, bboxH = bb - bt + 1;
            bool badgeOk = rows >= 20 && bn >= 1500 && bboxW is >= 90 and <= 400
                           && Math.Abs(bboxH - badgeH) <= 6 && hintCountB <= 20;
            Say(badgeOk ? "  ✓ 尺寸徽标已画在选区上方居中（外接框与 DPI 推出的形状一致），且静置提示已让位"
                        : "  ★ 徽标没画上 / 形状不对 / 提示没隐藏 ⇒ 这次不判");
            if (!badgeOk)
            {
                // 判红时把被测带附近原样落盘：像素说话，省得下一次还要靠"猜那 480 个蓝像素是谁"。
                string dump = Path.Combine(AppContext.BaseDirectory, "preview-ui-probe-badge.bmp");
                try
                {
                    DumpBmp(capB, vw, vh, new SdrSemanticsProbe.RectI(0, 0, vw, vh), dump);
                    Say($"  · ② 现场已落盘: {dump}");
                }
                catch { }
            }

            // ── ③ 选区内还是【同一张桌面】，不是一片灰 ──
            // 把窗内选区（剔掉两条铺灰带）与建窗前的 8-bit 基线帧做 Pearson 相关：
            // 帧真的进了窗、窗真的画在屏幕上 ⇒ 两张图是同一幅画面的两个副本，r 应接近 1；
            // "帧没上传/上传成常数/颜色空间链断"（实机报的"全灰"）会掉到 0 附近。
            // 不用色散：一整块单调的浅灰壁纸同样色散很小，上一轮 21 色/σ31 判红就是判据错。
            var (corr, baseStd, samples) = Correlate(capB, baseline, vw, vh, contentBand, hintBand, badgeBand);
            Say($"  ③ 选区内部 vs 建窗前基线：Pearson r={corr:0.000}｜基线标准差 {baseStd:0.0}｜样本 {samples} 像素");
            // 不判也要继续跑 ④–⑧：一条判不了不该把后面的契约读数一起遮掉（每相自己写结论）。
            bool contentJudged = baseStd >= 6.0;
            bool contentOk = contentJudged && corr >= 0.90;
            if (!contentJudged)
                Say("  ★ 基线取样区本来就近乎纯色（σ<6）⇒ 相关判不动；让出一块有内容的桌面再跑");
            else
                Say(contentOk ? "  ✓ 选区内呈现的是那张真桌面帧（与基线高度相关）"
                              : "  ★ 选区内与基线对不上 ⇒ 帧没到后台缓冲 / 颜色空间链断了（「全灰」的真形状）");

            // ── ⑭ 托盘注册/删除（真机）：用这扇真窗的 HWND 真发 Shell_NotifyIcon ──
            // 托盘图标挂在 (hWnd,uID) 上，与 XAML 无关 ⇒ 探针不需要再造一扇 WinUI 窗。
            var (trayOk, trayLine) = RunTrayPhase(hwnd);
            Say("  " + trayLine);

            // ── ④ 四角手柄：抓 TL 角拖内缩，BR 角必须原地不动 ──
            // 选区已在 ② 松手落定（TL/BR 就是 ② 的两点），这里直接抓手柄。
            await Task.Delay(200);

            // 故意按在离 TL 角 (5,5) 的位置：产品必须记住抓取偏移，否则角会在第一帧跳到手心，
            // 那正是"点上去范围就变"的另一种形状。目标角 = 松手点 - 偏移。
            int gdx = selW / 4, gdy = (cdy2 - cdy1) / 4;   // 内缩量（把 TL 往里拖）
            int tlx = cdx1 + gdx, tly = cdy1 + gdy;        // 期望的新 TL
            int grabX = cdx1 + 5, grabY = cdy1 + 5;
            int relX = tlx + 5, relY = tly + 5;

            (MainToolbarAction A, int X, int Y, int W, int H)? got = null;
            win.ActionCompleted += (a, ax, ay, aw, ah) => got = (a, ax, ay, aw, ah);

            CursorAt(grabX, grabY);
            PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, LP(grabX, grabY));
            CursorAt((grabX + relX) / 2, (grabY + relY) / 2);
            PostMessage(hwnd, WM_MOUSEMOVE, MK_LBUTTON, LP((grabX + relX) / 2, (grabY + relY) / 2));
            CursorAt(relX, relY);
            PostMessage(hwnd, WM_MOUSEMOVE, MK_LBUTTON, LP(relX, relY));
            PostMessage(hwnd, WM_LBUTTONUP, 0, LP(relX, relY));
            await Task.Delay(SettleMs);

            // 窗自己报的选中矩形就是以屏幕坐标交给裁切链的那一份
            PostMessage(hwnd, WM_KEYDOWN, VK_S, 0);
            await Task.Delay(400);
            hwnd = 0; // Save 会 Close 这扇窗，finally 不能再往上发消息

            bool resizeOk = false;
            if (got is { } g && g.A == MainToolbarAction.Save)
            {
                int wantX = vx + tlx, wantY = vy + tly;
                int wantW = cdx2 - tlx, wantH = cdy2 - tly;
                int ex = g.X - wantX, ey = g.Y - wantY, ew = g.W - wantW, eh = g.H - wantH;
                resizeOk = Math.Abs(ex) <= 2 && Math.Abs(ey) <= 2 && Math.Abs(ew) <= 2 && Math.Abs(eh) <= 2;
                Say($"  ④ 拖 TL 角：窗报选中 {g.W}x{g.H}@({g.X - vx},{g.Y - vy})（窗口内）" +
                    $"｜期望 {wantW}x{wantH}@({tlx},{tly})｜偏差 dx={ex} dy={ey} dw={ew} dh={eh}");
                Say(resizeOk ? "  ✓ 只动了被抓住的 TL 角，BR 角保持原位"
                             : "  ★ 松手后选区被改写（另一角被拖走 / 尺寸不符）⇒ 角拖拽仍在乱跳");
            }
            else
            {
                Say($"  ④ 拖 TL 角：窗没有回传 Save 的选中矩形（got={(got is null ? "null" : got.Value.A.ToString())}）⇒ 不判");
            }
            // ④ 红了也继续跑 ⑤–⑧：一次角拖拽失败不该把其余四条契约的读数一起遮掉。

            // ── ⑤–⑧ 选区交互契约 ──
            // 每相一扇新窗：先拖出基准选区 A，再施加被测手势，最后按 S 取窗自己报的矩形。
            // 期望值全部手算（不用产品代码算），四条分别钉住：平移不改尺寸 / 框内单击不乱跳 /
            // 框外按下是重画而不是搬旧框 / 角拖过对角时应翻面成新矩形而不是塌掉。
            // 基准选区与全部手势点都按窗口尺寸换算：写死 4K 坐标时换到 2560x1600 上会撞到
            // 产品的"角钳进窗口"边界（那是对的），读出来却像产品算错 —— 判据不许依赖屏幕分辨率。
            int ax1 = vw * 26 / 100, ay1 = vh * 29 / 100;
            int ax2 = vw * 57 / 100, ay2 = vh * 62 / 100;
            int aw = ax2 - ax1, ah = ay2 - ay1;
            int acx = ax1 + aw / 2, acy = ay1 + ah / 2;
            int moveX = -vw * 8 / 100, moveY = vh * 14 / 100;        // ⑤ 平移量
            int redrawX = vw * 8 / 100, redrawY = vh * 14 / 100;     // ⑦ 框外起点
            int redrawW = vw * 15 / 100, redrawH = vh * 21 / 100;    // ⑦ 新框大小
            int flipX = vw * 9 / 100, flipY = vh * 7 / 100;          // ⑧ 越过对角的距离

            async Task BaseSelect(nint h)
            {
                CursorAt(ax1, ay1);
                PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ax1, ay1));
                for (int i = 1; i <= 4; i++)
                {
                    CursorAt(ax1 + aw * i / 4, ay1 + ah * i / 4);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON,
                        LP(ax1 + aw * i / 4, ay1 + ah * i / 4));
                    await Task.Delay(30);
                }
                PostMessage(h, WM_LBUTTONUP, 0, LP(ax2, ay2));
                await Task.Delay(120);
            }

            var phases = new (string Name, int X, int Y, int W, int H, Func<nint, Task> Run)[]
            {
                // ⑤ 框内按住拖：整框平移 moveX/moveY，尺寸不变
                ($"⑤ 框内平移", ax1 + moveX, ay1 + moveY, aw, ah, async h =>
                {
                    await BaseSelect(h);
                    CursorAt(acx, acy);
                    PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(acx, acy));
                    CursorAt(acx + moveX / 2, acy + moveY / 2);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(acx + moveX / 2, acy + moveY / 2));
                    CursorAt(acx + moveX, acy + moveY);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(acx + moveX, acy + moveY));
                    PostMessage(h, WM_LBUTTONUP, 0, LP(acx + moveX, acy + moveY));
                }),
                // ⑥ 框内单击不动：矩形必须原地不动（旧写法会跳成悬停窗口/全屏）
                ("⑥ 框内单击", ax1, ay1, aw, ah, async h =>
                {
                    await BaseSelect(h);
                    CursorAt(acx, acy);
                    PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(acx, acy));
                    PostMessage(h, WM_LBUTTONUP, 0, LP(acx, acy));
                }),
                // ⑦ 框外按下拖：应重画出一个新框，而不是把旧框搬走
                ("⑦ 框外重画", redrawX, redrawY, redrawW, redrawH, async h =>
                {
                    await BaseSelect(h);
                    CursorAt(redrawX, redrawY);
                    PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(redrawX, redrawY));
                    CursorAt(redrawX + redrawW / 2, redrawY + redrawH / 2);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(redrawX + redrawW / 2, redrawY + redrawH / 2));
                    CursorAt(redrawX + redrawW, redrawY + redrawH);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(redrawX + redrawW, redrawY + redrawH));
                    PostMessage(h, WM_LBUTTONUP, 0, LP(redrawX + redrawW, redrawY + redrawH));
                }),
                // ⑧ 抓 TL 拖过 BR：应翻面成以旧 BR 为左上角的新矩形
                ("⑧ 角拖过对角", ax2, ay2, flipX, flipY, async h =>
                {
                    await BaseSelect(h);
                    CursorAt(ax1, ay1);
                    PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ax1, ay1));
                    CursorAt(ax2 + flipX / 2, ay2 + flipY / 2);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(ax2 + flipX / 2, ay2 + flipY / 2));
                    CursorAt(ax2 + flipX, ay2 + flipY);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(ax2 + flipX, ay2 + flipY));
                    PostMessage(h, WM_LBUTTONUP, 0, LP(ax2 + flipX, ay2 + flipY));
                }),
            };

            bool interactOk = true;
            foreach (var p in phases)
            {
                var (ok, line) = await RunPhaseAsync(present, frameW, frameH, vx, vy, vw, vh,
                    p.Name, p.X, p.Y, p.W, p.H, p.Run);
                Say("  " + line);
                interactOk &= ok;
            }

            // ── ⑨ 文字标注（富文本 + 撤销/重做 + 拖动 + 再编辑）──
            // 读数是窗自己交出来的图层状态（探针与窗同进程），不是从像素反推文字内容：
            // 这里要判的是"输入框→图层→撤销栈→拖动→再编辑"这条链，像素只能证明"有白点"。
            var (textOk, textLine) = await RunTextPhaseAsync(present, frameW, frameH, vx, vy, vw, vh);
            Say("  " + textLine);

            // ── ⑫ 画笔粗细滑条：点"画笔涂抹"换形态 → 拖轨到两端与中段 → 涂一笔 → 图层笔宽必须等于滑条值 ──
            var (brushOk, brushLine) = await RunBrushSliderPhaseAsync(present, frameW, frameH, vx, vy, vw, vh);
            Say("  " + brushLine);

            // ── ⑬ 工具条/选项行的点击不许改写选区（2026-10-28 真机：点「完成」后选区 1280x820→1133x861）──
            var (latchOk, latchLine) = await RunToolClickKeepsSelectionAsync(present, frameW, frameH, vx, vy, vw, vh);
            Say("  " + latchLine);

            // ── ⑪ 标注几何（像素级）：箭头方向 + 马赛克边界 ──
            // 这两处曾经只在【预览】里错（CPU 存档路径一直是对的），⑨ 读图层状态看不见，
            // 必须从画面上读：箭头必须落在拖拽的那条线上、马赛克不许溢出它自己的矩形。
            var (geomOk, geomLine) = await RunAnnoGeometryPhaseAsync(present, frameW, frameH, baseline, vx, vy, vw, vh);
            Say("  " + geomLine);

            // ── ⑩ 置顶贴图窗：能开、是置顶、滚轮按 1.2 倍缩放、工具栏"复制"真的调到回调、能关 ──
            var (pinOk, pinLine) = await RunPinPhaseAsync();
            Say("  " + pinLine);

            // 退出码 = 每一相都【判了】且都通过；"不判"的相一律不算通过。
            return hintOk && badgeOk && contentJudged && contentOk
                   && resizeOk && interactOk && textOk && brushOk && latchOk && geomOk && pinOk && trayOk
                   && prewarmOk ? 0 : 1;
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
            if (cursor0.Ok) SetCursorPos(cursor0.X, cursor0.Y);   // 归还用户的光标
            try { win?.Close(); } catch { }
            try { win?.Dispose(); } catch { }
            try { File.WriteAllText(artifact, sb.ToString(), new UTF8Encoding(false)); } catch { }
            Console.WriteLine($"[preview-ui] 读数已落盘: {artifact}");
        }
    }

    /// <summary>一相交互契约：新开一扇窗 → 跑手势 → 按 S 取窗自己报的选中矩形（换回窗口内坐标）→ 对期望值。</summary>
    private static async Task<(bool Ok, string Line)> RunPhaseAsync(
        float[] present, int frameW, int frameH, int vx, int vy, int vw, int vh,
        string name, int ex, int ey, int ew, int eh, Func<nint, Task> run)
    {
        var w = new HdrCaptureWindow();
        nint h = 0;
        try
        {
            if (!w.Initialize(vx, vy, vw, vh))
                return (false, $"{name}: 预览窗建不起来（{w.LastError ?? "无 LastError"}）⇒ 不判");
            w.LoadFrame(present, frameW, frameH);
            w.Render();
            await Task.Delay(400);
            h = w.Hwnd;
            if (h == 0) return (false, $"{name}: 预览窗建不起来（实例句柄为 0）⇒ 不判");

            (MainToolbarAction A, int X, int Y, int W, int H)? got = null;
            w.ActionCompleted += (a, x, y, ww, hh) => got = (a, x, y, ww, hh);
            await run(h);
            await Task.Delay(250);
            PostMessage(h, WM_KEYDOWN, VK_S, 0);
            await Task.Delay(300);

            if (got is not { } g || g.A != MainToolbarAction.Save)
                return (false, $"{name}: 窗没有回传 Save 的选中矩形 ⇒ 不判");
            int gx = g.X - vx, gy = g.Y - vy;
            int dx = gx - ex, dy = gy - ey, dw = g.W - ew, dh = g.H - eh;
            bool ok = Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2 && Math.Abs(dw) <= 2 && Math.Abs(dh) <= 2;
            return (ok, $"{name}: 窗报 {g.W}x{g.H}@({gx},{gy})｜期望 {ew}x{eh}@({ex},{ey})"
                      + $"｜偏差 dx={dx} dy={dy} dw={dw} dh={dh}" + (ok ? " ✓" : " ★ 与鼠标动作不符"));
        }
        finally
        {
            // 手势中途异常也不能把鼠标捕获留在探针窗上（那一段里用户的真实拖拽会被吸进来）
            if (h != 0) PostMessage(h, WM_LBUTTONUP, 0, LP(0, 0));
            try { w.Close(); } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// ⑨ 文字标注：单击 → 输入框 → 图层（字体状态来自真实的 Win32 控件条）→ 撤销/重做 →
    /// 拖动移动（一条可撤销的 UpdateLayer）→ 单击再编辑（输入框带出旧文字）→ 撤销编辑。
    /// <para>每一步都读 <c>AnnoManager</c> 的图层状态：这是窗自己的事实，不是从像素猜的。
    /// "不判"（输入框/控件条没出现）一律算失败 —— 与其余各相同一口径。</para>
    /// </summary>
    private static async Task<(bool Ok, string Line)> RunTextPhaseAsync(
        float[] present, int frameW, int frameH, int vx, int vy, int vw, int vh)
    {
        const string Name = "⑨ 文字标注";
        var w = new HdrCaptureWindow();
        nint h = 0;
        try
        {
            if (!w.Initialize(vx, vy, vw, vh))
                return (false, $"{Name}: 预览窗建不起来（{w.LastError ?? "无 LastError"}）⇒ 不判");
            w.LoadFrame(present, frameW, frameH);
            w.Render();
            await Task.Delay(400);
            h = w.Hwnd;
            if (h == 0) return (false, $"{Name}: 预览窗建不起来（实例句柄为 0）⇒ 不判");

            int ax1 = vw * 26 / 100, ay1 = vh * 29 / 100;
            int ax2 = vw * 57 / 100, ay2 = vh * 62 / 100;
            int aw = ax2 - ax1, ah = ay2 - ay1;
            void CursorAt(int x, int y) => SetCursorPos(vx + x, vy + y);

            // 基准选区（与其它相同一手势）
            CursorAt(ax1, ay1);
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ax1, ay1));
            for (int i = 1; i <= 4; i++)
            {
                CursorAt(ax1 + aw * i / 4, ay1 + ah * i / 4);
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(ax1 + aw * i / 4, ay1 + ah * i / 4));
                await Task.Delay(30);
            }
            PostMessage(h, WM_LBUTTONUP, 0, LP(ax2, ay2));
            await Task.Delay(300);

            // 文字工具（工具条点击之外唯一入口，见 HdrCaptureWindow.EnterAnnotationModeForProbe）
            w.EnterAnnotationModeForProbe("Text");
            await Task.Delay(300);

            // 字体/字号/B/I 现在住在 D3D 帧里（Win32 控件条已于 2026-10-28 删除，那块交换链在
            // 合成平面之上，子控件一个像素都不显示）⇒ 探针没有句柄可拿，只能拿窗自己算出的格子
            // 矩形，用真鼠标消息点那些坐标：走的仍是 Down → HitOptionsRow → HitDropdown 这条产品路径。
            double scale = GetDpiForSystem() / 96.0;
            int rowMode = w.ProbeOptionsRowMode();
            if (rowMode != 3)
                return (false, $"{Name}: 文字工具的第二行形态 ≠ 3（实得 {rowMode}）⇒ 不判");
            var frow = w.ProbeOptionsRow();
            void RowClick(int cell)
            {
                int cx = frow.CellX[cell] + frow.W[cell] / 2, cy = frow.Y + frow.CellH / 2;
                CursorAt(cx, cy);
                PostMessage(h, WM_LBUTTONDOWN, 0, LP(cx, cy));
                PostMessage(h, WM_LBUTTONUP, 0, LP(cx, cy));
            }

            RowClick(1);                       // 字号格 → 应展开字号列表
            await Task.Delay(200);
            if (w.ProbeDropdownOpen() != 2)
                return (false, $"{Name}: 点字号格没有展开字号列表（形态={w.ProbeDropdownOpen()}）⇒ 不判");
            var sizes = w.ProbeTextFontSizes();
            int pick = Math.Min(3, sizes.Length - 1), dip = sizes[pick];
            var dd = w.ProbeDropdownGeometry();
            int visRow = pick - w.ProbeDropdownTop();
            if (visRow < 0 || visRow >= dd.Visible)
                return (false, $"{Name}: 想点的第 {pick} 项不在列表可见窗口里（首行={w.ProbeDropdownTop()} 可见={dd.Visible}）⇒ 不判");
            int ix = dd.X + dd.W / 2, iy = dd.Y + visRow * dd.ItemH + dd.ItemH / 2;

            // 文字带容量：列表报的可见行必须都是图集里真烘出来的行。带高固定 256px、格高随 DPI 长，
            // 250% 下只装得下 7 行 —— 旧实现照报 12 行、只烘 7 行，列表后几行是空白条（静默少画）。
            // 容量算术另在 250%/300% 缩放下扫一遍：必须随缩放变小、且至少留 1 行。
            int capNow = w.ProbeDdBandCapacity();
            int bakedRows = w.ProbeBakedDdRows();
            for (int i = 0; i < 20 && bakedRows == 0; i++)
            {
                await Task.Delay(50);
                bakedRows = w.ProbeBakedDdRows();
            }
            int maxVis = w.ProbeDdMaxVisible();
            int cap25 = w.ProbeDdBandCapacityAt(2.5f), cap30 = w.ProbeDdBandCapacityAt(3f);
            bool bandOk = dd.Visible <= capNow && bakedRows == dd.Visible;
            bool bandSweepOk = capNow > 0 && cap25 >= 1 && cap25 < maxVis && cap30 >= 1 && cap30 <= cap25;
            string bandNote = $"文字带 可见 {dd.Visible}/容量 {capNow}/烘出 {bakedRows}"
                + $"（250%→{cap25} 行、300%→{cap30} 行，上限 {maxVis}）";
            CursorAt(ix, iy);
            PostMessage(h, WM_LBUTTONDOWN, 0, LP(ix, iy));
            PostMessage(h, WM_LBUTTONUP, 0, LP(ix, iy));
            await Task.Delay(200);
            bool sizeOk = Math.Abs(w.ProbeFontSizeDip() - dip) < 0.6 && w.ProbeDropdownOpen() == 0;

            RowClick(2);                       // B 格 → 加粗
            await Task.Delay(150);
            bool boldOk = w.ProbeTextBold();

            // "选项行在界内"的尺子也跟着换了：以前查控件句柄 + IsWindowVisible，现在只能查几何 ——
            // 四格都得有面积、整行落在覆盖层窗口之内（画到窗外等于用户看不见）。
            // ⚠ 这条【不证明像素上真画出来了】：那部分证据在鼠标点击本身（字号格→列表→某一项
            // 真的改了状态）与临时 harness 的取样读数，不在这里。
            GetWindowRect(h, out var wrOverlay);
            int overlayW = wrOverlay.Right - wrOverlay.Left, overlayH = wrOverlay.Bottom - wrOverlay.Top;
            int rowSpan = frow.W[0] + frow.W[1] + frow.W[2] + frow.W[3] + 3 * frow.Gap;
            bool fontBarShown = frow.CellH > 0 && rowSpan > 0
                && frow.X >= 0 && frow.Y >= 0 && frow.X + rowSpan <= overlayW && frow.Y + frow.CellH <= overlayH;
            string fontBarNote = fontBarShown
                ? $"选项行在界内（4 格 {frow.W[0]}/{frow.W[1]}/{frow.W[2]}/{frow.W[3]}，行 y={frow.Y} 高 {frow.CellH}，列表 {dd.Visible} 行）"
                : $"选项行越界（x={frow.X} y={frow.Y} 跨度={rowSpan}，窗 {overlayW}x{overlayH}）";

            // 单击选区左上内侧 → 输入框（走真实的 Down/Up 路径）
            int tx = ax1 + 12, ty = ay1 + 12;
            CursorAt(tx, ty);
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(tx, ty));
            PostMessage(h, WM_LBUTTONUP, 0, LP(tx, ty));
            nint edit = await WaitChildAsync(h, "EDIT");
            if (edit == 0) return (false, $"{Name}: 单击后没有出现输入框 ⇒ 不判");

            const string first = "TTC 文字 ①②";
            SetWindowTextW(edit, first);
            PostMessage(edit, WM_KEYDOWN, VK_ENT, 0);
            for (int i = 0; i < 40 && w.AnnoManager.LayerCount == 0; i++) await Task.Delay(50);

            var t1 = w.AnnoManager.LayerCount == 1 ? w.AnnoManager.GetLayersSnapshot()[0] as TextLayer : null;
            bool createOk = t1 is not null && t1.Text == first && t1.CachedWidth > 0 && t1.CachedHeight > 0
                && Math.Abs(t1.FontSize - dip * scale) <= 1.5 && t1.Bold
                && t1.GetBounds().Width <= aw; // 折行宽度按选区封顶
            string createNote = t1 is null
                ? $"图层数={w.AnnoManager.LayerCount}（期望 1 个文字层）"
                : $"文字='{t1.Text}' 位图 {t1.CachedWidth:0}×{t1.CachedHeight:0} 字号 {t1.FontSize:0}px（控件 {dip}dip×{scale:0.##}）加粗={t1.Bold}";

            // 撤销/重做：创建也要进同一条栈
            w.AnnoManager.Undo();
            bool undoOk = w.AnnoManager.LayerCount == 0;
            w.AnnoManager.Redo();
            var t2 = w.AnnoManager.LayerCount == 1 ? w.AnnoManager.GetLayersSnapshot()[0] as TextLayer : null;
            bool redoOk = t2 is not null && t2.Text == first;

            // 拖动已有文字：位置变、大小不变，且只是一条可撤销的更新
            bool moveOk = false, moveUndoOk = false, moveRedoOk = false;
            string moveNote = "重做后图层不见了";
            if (t2 is not null)
            {
                float x0 = t2.X, y0 = t2.Y, w0 = t2.GetBounds().Width;
                int sx = ax1 + (int)x0 + 20, sy = ay1 + (int)y0 + 20, dx = 48, dy = 36;
                CursorAt(sx, sy);
                PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(sx, sy));
                for (int i = 1; i <= 3; i++)
                {
                    CursorAt(sx + dx * i / 3, sy + dy * i / 3);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(sx + dx * i / 3, sy + dy * i / 3));
                    await Task.Delay(30);
                }
                PostMessage(h, WM_LBUTTONUP, 0, LP(sx + dx, sy + dy));
                // ⚠ 等"提交"而不是等"位置变了"：拖拽过程中窗是【原地改】图层对象（X/Y 直接写），
                // 快照里能看到新位置时 UP 可能还没进消息循环；那时 Undo 弹出来的是"新建"那条命令，
                // 撤销的是图层本身（实机 2026-10-28 读到 moveUndo=False、位置对不上）。
                // UpdateLayer 会把列表里那一格换成新克隆 ⇒ 对象引用变了才是"移动已入栈"的证据。
                TextLayer? t3 = null;
                for (int i = 0; i < 40; i++)
                {
                    t3 = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                    if (t3 is not null && !ReferenceEquals(t3, t2)) break;
                    await Task.Delay(50);
                }
                moveNote = t3 is null ? "拖动后图层不见了"
                    : $"({x0:0},{y0:0})→({t3.X:0},{t3.Y:0})，宽 {w0:0}→{t3.GetBounds().Width:0}";
                if (t3 is not null)
                {
                    // ⚠ 不判"终点 == 我发的最后一个坐标"：SetCapture 之后系统会按【物理光标】
                    // 补发移动（本机 SetCursorPos 时灵时不灵，实机读到过位移 987,652 —— 图层跟着
                    // 那颗没被挪走的光标跳了），拖拽终点不由探针决定 —— 与 ② 徽标同一条纪律。
                    // 判据只留"确实发生了一次移动"，精确性交给下一条：撤销精确回到起点、
                    // 重做精确回到观测终点（这两条才是"一条可撤销的 UpdateLayer"的真契约）。
                    float mx = t3.X, my = t3.Y;
                    float dxObs = mx - x0, dyObs = my - y0;
                    moveOk = (Math.Abs(dxObs) > 0.5f || Math.Abs(dyObs) > 0.5f)
                             && Math.Abs(t3.GetBounds().Width - w0) < 0.5 && w.AnnoManager.LayerCount == 1;
                    moveNote += $"（位移 {dxObs:0},{dyObs:0}）";
                    w.AnnoManager.Undo();
                    var tu = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                    moveUndoOk = tu is not null && Math.Abs(tu.X - x0) <= 0.5 && Math.Abs(tu.Y - y0) <= 0.5;
                    w.AnnoManager.Redo();
                    var tr = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                    moveRedoOk = tr is not null && Math.Abs(tr.X - mx) <= 0.5 && Math.Abs(tr.Y - my) <= 0.5;
                }
            }

            // 再编辑：点中已有文字 → 输入框带出旧文字；改成第二版提交 ⇒ UpdateLayer（可撤销）
            bool prefillOk = false, editOk = false, editUndoOk = false;
            string editNote = "拖动阶段没有留下文字层";
            var t4 = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
            if (t4 is not null)
            {
                int ex = ax1 + (int)t4.X + 10, ey = ay1 + (int)t4.Y + 10;
                CursorAt(ex, ey);
                PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ex, ey));
                PostMessage(h, WM_LBUTTONUP, 0, LP(ex, ey));
                nint edit2 = await WaitChildAsync(h, "EDIT");
                editNote = edit2 == 0 ? "点已有文字没有打开输入框" : $"带出 '{WindowText(edit2)}'";
                if (edit2 != 0)
                {
                    prefillOk = WindowText(edit2) == first;
                    const string second = "第二版";
                    SetWindowTextW(edit2, second);
                    PostMessage(edit2, WM_KEYDOWN, VK_ENT, 0);
                    for (int i = 0; i < 40; i++)
                    {
                        var now = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                        if (now is not null && now.Text == second) break;
                        await Task.Delay(50);
                    }
                    var te = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                    editOk = te is not null && te.Text == second && w.AnnoManager.LayerCount == 1;
                    w.AnnoManager.Undo();
                    var tb = w.AnnoManager.GetLayersSnapshot().FirstOrDefault() as TextLayer;
                    editUndoOk = tb is not null && tb.Text == first;
                }
            }

            bool ok = createOk && undoOk && redoOk && moveOk && moveUndoOk && moveRedoOk
                      && prefillOk && editOk && editUndoOk && fontBarShown && sizeOk && boldOk
                      && bandOk && bandSweepOk;
            string line = $"{Name}: 输入框→图层 {createNote}｜{fontBarNote}"
                + $"｜字号格→列表→{dip}dip={sizeOk} 加粗格={boldOk}"
                + $"｜{bandNote}（可见行=烘出行={bandOk}，随缩放收缩={bandSweepOk}）"
                + $"｜撤销={undoOk} 重做={redoOk}"
                + $"｜拖动 {moveNote}（撤销/重做 {moveUndoOk}/{moveRedoOk}）"
                + $"｜再编辑 {editNote}（提交/撤销 {editOk}/{editUndoOk}）"
                + (ok ? " ✓" : " ★ 文字标注链路上有一环没落地");
            return (ok, line);
        }
        catch (Exception ex)
        {
            return (false, $"{Name}: 自身异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != 0) PostMessage(h, WM_LBUTTONUP, 0, LP(0, 0));
            try { w.Close(); } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// ⑬ 工具条与选项行的点击【不许】改写选区。
    /// <para>2026-10-28 真机日志抓到的形状：按下被标注工具条吃掉时把 <c>_down</c> 置了 false，于是松手
    /// 那条路径再也认不出"这一下属于按钮"，一路落到"重画选区"的收尾，把右下角写成按钮的坐标 ——
    /// 框选 1280x820，点「完成」后变成 1133x861，保存出去的图比用户框的少 147 px 宽，而界面上看不出来。
    /// 判据读窗自己的选区状态（前后一比），不看像素。</para>
    /// </summary>
    private static async Task<(bool Ok, string Line)> RunToolClickKeepsSelectionAsync(
        float[] present, int frameW, int frameH, int vx, int vy, int vw, int vh)
    {
        const string Name = "⑬ 工具条点击不改选区";
        var w = new HdrCaptureWindow();
        nint h = 0;
        try
        {
            if (!w.Initialize(vx, vy, vw, vh))
                return (false, $"{Name}: 预览窗建不起来（{w.LastError ?? "无 LastError"}）⇒ 不判");
            w.LoadFrame(present, frameW, frameH);
            w.Render();
            await Task.Delay(400);
            h = w.Hwnd;
            if (h == 0) return (false, $"{Name}: 实例句柄为 0 ⇒ 不判");

            int ax1 = vw * 12 / 100, ay1 = vh * 12 / 100;
            int ax2 = vw * 55 / 100, ay2 = vh * 55 / 100;
            void CursorAt(int x, int y) => SetCursorPos(vx + x, vy + y);
            void Press(int x, int y)
            {
                CursorAt(x, y);
                PostMessage(h, WM_LBUTTONDOWN, 0, LP(x, y));
                PostMessage(h, WM_LBUTTONUP, 0, LP(x, y));
            }

            CursorAt(ax1, ay1);
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ax1, ay1));
            for (int i = 1; i <= 4; i++)
            {
                int mx = ax1 + (ax2 - ax1) * i / 4, my = ay1 + (ay2 - ay1) * i / 4;
                CursorAt(mx, my);
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(mx, my));
                await Task.Delay(25);
            }
            PostMessage(h, WM_LBUTTONUP, 0, LP(ax2, ay2));
            await Task.Delay(250);
            var sel0 = w.ProbeSelection();
            if (sel0.W <= 0 || sel0.H <= 0) return (false, $"{Name}: 没框出选区（{sel0}）⇒ 不判");

            // 臂一：标注工具条「完成」——它会退出标注模式，正是那次把选区改掉的点击
            w.EnterAnnotationModeForProbe("Rect");
            await Task.Delay(200);
            int finish = Array.FindIndex(ToolbarContract.Anno, t => t.Text == "完成");
            if (finish < 0) return (false, $"{Name}: 契约里没有「完成」按钮 ⇒ 不判");
            bool armedIn = w.ProbeAnnoMode();                       // 前提：确实进了标注模式，否则"没改"是真空成立
            var tb = w.ProbeToolbarFrame(10);
            Press(tb.Tx + tb.PadX + finish * (tb.BtnW + tb.Gap) + tb.BtnW / 2,
                  tb.Ty + tb.PadY + tb.BtnH / 2);
            await Task.Delay(250);
            var sel1 = w.ProbeSelection();
            // 两个读数必须【当场取】：臂二会把标注模式重新打开，等拼报告时再读就成了另一个时刻的状态
            // （首版就是这么写的：判据用当场值、报告行用事后值，于是同一行里"未变=True"配"点后退出=False"）
            bool exitedAfterFinish = !w.ProbeAnnoMode();
            // 这一臂必须有"点到了"的见证：选区没变 + 标注模式真的退了，才算判上
            // （只比选区的话，那一下压根没命中按钮也会绿 —— 同一把尺量不到自己要防的那种空过）
            bool keptFinish = armedIn && exitedAfterFinish && sel1 == sel0;

            // 臂二：第二行选项条（同一道闩的另一个入口，且它不关窗）
            w.EnterAnnotationModeForProbe("Mosaic");
            await Task.Delay(200);
            var row = w.ProbeOptionsRow();
            Press(row.CellX[1] + row.W[1] / 2, row.Y + row.CellH / 2);   // 点「画笔涂抹」
            await Task.Delay(250);
            var sel2 = w.ProbeSelection();
            bool rowSwitched = w.ProbeOptionsRowMode() == 2;
            bool keptRow = sel2 == sel0 && rowSwitched;                   // 还要确认这一下真的生效在选项行上

            bool ok = keptFinish && keptRow;
            string line = $"{Name}: 框选 {sel0.W}x{sel0.H}@({sel0.X},{sel0.Y})"
                + $"｜点「完成」后 {sel1.W}x{sel1.H}@({sel1.X},{sel1.Y}) 未变={keptFinish}"
                + $"（进得了标注模式={armedIn}，点后退出={exitedAfterFinish}）"
                + $"｜点「画笔涂抹」后 {sel2.W}x{sel2.H} 未变={keptRow}（形态已切到滑条={rowSwitched}）"
                + (ok ? " ✓" : " ★ 选区被一次按钮点击改写了");
            return (ok, line);
        }
        catch (Exception ex)
        {
            return (false, $"{Name}: 自身异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != 0) PostMessage(h, WM_LBUTTONUP, 0, LP(0, 0));
            try { w.Close(); } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// ⑫ 画笔粗细滑条（1..100 px）：形态切换、拖轨、涂一笔 → 图层的 BrushWidth 必须等于拖出来的那个数。
    /// <para>期望值由探针【自己】从选项行矩形与轨道内缩算出来（不复用窗的换算函数）——
    /// 否则"绘制 / 命中 / 落值"三处一起漂就照不出。这条相就是那三处换算的对账点。</para>
    /// </summary>
    private static async Task<(bool Ok, string Line)> RunBrushSliderPhaseAsync(
        float[] present, int frameW, int frameH, int vx, int vy, int vw, int vh)
    {
        const string Name = "⑫ 画笔粗细滑条";
        var w = new HdrCaptureWindow();
        nint h = 0;
        try
        {
            if (!w.Initialize(vx, vy, vw, vh))
                return (false, $"{Name}: 预览窗建不起来（{w.LastError ?? "无 LastError"}）⇒ 不判");
            w.LoadFrame(present, frameW, frameH);
            w.Render();
            await Task.Delay(400);
            h = w.Hwnd;
            if (h == 0) return (false, $"{Name}: 预览窗实例句柄为 0 ⇒ 不判");

            int ax1 = vw * 10 / 100, ay1 = vh * 10 / 100;
            int ax2 = vw * 60 / 100, ay2 = vh * 55 / 100;
            int aw = ax2 - ax1, ah = ay2 - ay1;
            void CursorAt(int x, int y) => SetCursorPos(vx + x, vy + y);
            void ClickAt(int x, int y)
            {
                CursorAt(x, y);
                PostMessage(h, WM_LBUTTONDOWN, 0, LP(x, y));
                PostMessage(h, WM_LBUTTONUP, 0, LP(x, y));
            }

            ClickAt(ax1, ay1);
            for (int i = 1; i <= 4; i++)
            {
                CursorAt(ax1 + aw * i / 4, ay1 + ah * i / 4);
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(ax1 + aw * i / 4, ay1 + ah * i / 4));
            }
            ClickAt(ax2, ay2);
            await Task.Delay(250);

            w.EnterAnnotationModeForProbe("Mosaic");
            await Task.Delay(200);
            if (w.ProbeOptionsRowMode() != 1)
                return (false, $"{Name}: 马赛克工具的第二行形态 ≠ 1（区域框选/画笔涂抹，实得 {w.ProbeOptionsRowMode()}）⇒ 不判");
            var mrow = w.ProbeOptionsRow();
            ClickAt(mrow.CellX[1] + mrow.W[1] / 2, mrow.Y + mrow.CellH / 2);   // 画笔涂抹
            await Task.Delay(200);
            if (w.ProbeOptionsRowMode() != 2)
                return (false, $"{Name}: 点了「画笔涂抹」之后形态 ≠ 2（滑条，实得 {w.ProbeOptionsRowMode()}）⇒ 不判");

            var srow = w.ProbeOptionsRow();
            int inset = w.ProbeSliderInset();
            int railX0 = srow.CellX[0] + inset, railX1 = srow.CellX[0] + srow.W[0] - inset;
            if (railX1 <= railX0)
                return (false, $"{Name}: 轨道退化（{railX0}..{railX1}）⇒ 不判");
            int railY = srow.Y + srow.CellH / 2;

            static float ExpectAt(int x, int x0, int x1) =>
                MathF.Round(1f + Math.Clamp((x - x0) / (float)(x1 - x0), 0f, 1f) * 99f);

            async Task<float> DragTo(int targetX)
            {
                CursorAt(railX0, railY);
                PostMessage(h, WM_LBUTTONDOWN, 0, LP(railX0, railY));
                for (int i = 1; i <= 4; i++)
                {
                    int mx = railX0 + (targetX - railX0) * i / 4;
                    CursorAt(mx, railY);
                    PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(mx, railY));
                    await Task.Delay(20);
                }
                CursorAt(targetX, railY);
                PostMessage(h, WM_LBUTTONUP, 0, LP(targetX, railY));
                await Task.Delay(120);
                return w.ProbeBrushPx();
            }

            float atMin = await DragTo(railX0);
            float atMax = await DragTo(railX1);
            int midX = (railX0 + railX1) / 2;
            float atMid = await DragTo(midX);
            float wantMid = ExpectAt(midX, railX0, railX1);
            bool endsOk = atMin == 1f && atMax == 100f;
            bool midOk = Math.Abs(atMid - wantMid) <= 1f;

            // 用中段这个粗细涂一笔：图层的 BrushWidth 必须就是它（块大小跟着 UI 缩放，两边同源）。
            // 笔画坐标必须取【窗自己报的选区】，不能拿上面那组意图坐标算：这一相开头那次 ClickAt 若被
            // 当成"点选悬停窗口"，落成的选区会比意图的小一截（实测 1242x831@(85,76) vs 意图 1920x972@(384,216)），
            // 于是 0.75 高度处的笔画正好落到底边之外 ⇒ 图层没建，判据报"涂抹没落成"。2026-10-30 实测：
            // 同一份代码开/关图集缓存只挪了开窗时序，选区就挪了 38px，这一笔在界内界外来回翻。
            var sel = w.ProbeSelection();
            if (sel.W < 40 || sel.H < 40)
                return (false, $"{Name}: 选区退化（{sel.W}x{sel.H}）⇒ 不判");
            int strokeY = sel.Y + sel.H * 3 / 4;
            int sx0 = sel.X + sel.W / 6, sx1 = sel.X + sel.W * 5 / 6;
            CursorAt(sx0, strokeY);
            PostMessage(h, WM_LBUTTONDOWN, 0, LP(sx0, strokeY));
            for (int i = 1; i <= 6; i++)
            {
                int mx = sx0 + (sx1 - sx0) * i / 6;
                CursorAt(mx, strokeY);
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(mx, strokeY));
                await Task.Delay(20);
            }
            CursorAt(sx1, strokeY);
            PostMessage(h, WM_LBUTTONUP, 0, LP(sx1, strokeY));
            await Task.Delay(250);

            var mb = w.AnnoManager.GetLayersSnapshot().OfType<MosaicBrushLayer>().FirstOrDefault();
            bool layerOk = mb is not null
                && Math.Abs(mb.BrushWidth - atMid) < 0.01f
                && mb.Points.Count >= 2
                && Math.Abs(mb.BlockSize - Math.Max(4f, 10f * (float)w.ProbeUiScale())) < 0.01f;
            string layerNote = mb is null ? "涂抹没落成 MosaicBrush 图层"
                : $"图层笔宽 {mb.BrushWidth:0}px 点数 {mb.Points.Count} 块 {mb.BlockSize:0}px";

            bool ok = endsOk && midOk && layerOk;
            string line = $"{Name}: 两端 {atMin:0}/{atMax:0}（期望 1/100）｜中段 {atMid:0}（几何算出的期望 {wantMid:0}）"
                + $"｜轨道 {railX0}..{railX1} 内缩 {inset}｜{layerNote} {(ok ? "✓" : "★ 滑条与图层对不上")}";
            return (ok, line);
        }
        catch (Exception ex)
        {
            return (false, $"{Name}: 自身异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != 0) PostMessage(h, WM_LBUTTONUP, 0, LP(0, 0));
            try { w.Close(); } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// ⑪ 标注几何（像素级）：箭头方向 + 马赛克边界。
    /// <para>这两处曾经只在【GPU 预览】里错（CPU 存档路径一直按图层矩形画），⑨ 读的是图层状态，
    /// 看不见这类"图层对、画面错"的问题：箭头喂的是外接框 ⇒ 恒指右下；马赛克分支漏了边界判断 ⇒
    /// 整块选区都糊掉（2026-10-28 用户实测）。判据直接读屏幕像素：箭头必须落在拖拽的那条线上，
    /// 马赛克矩形之外必须与建窗前的基线一致。</para>
    /// </summary>
    private static async Task<(bool Ok, string Line)> RunAnnoGeometryPhaseAsync(
        float[] present, int frameW, int frameH, byte[] baseline, int vx, int vy, int vw, int vh)
    {
        const string Name = "⑪ 标注几何";
        var w = new HdrCaptureWindow();
        nint h = 0;
        try
        {
            if (!w.Initialize(vx, vy, vw, vh))
                return (false, $"{Name}: 预览窗建不起来（{w.LastError ?? "无 LastError"}）⇒ 不判");
            w.LoadFrame(present, frameW, frameH);
            w.Render();
            await Task.Delay(400);
            h = w.Hwnd;
            if (h == 0) return (false, $"{Name}: 实例句柄为 0 ⇒ 不判");

            int ax1 = vw * 26 / 100, ay1 = vh * 29 / 100;
            int ax2 = vw * 57 / 100, ay2 = vh * 62 / 100;
            int aw = ax2 - ax1, ah = ay2 - ay1;
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(ax1, ay1));
            for (int i = 1; i <= 4; i++)
            {
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(ax1 + aw * i / 4, ay1 + ah * i / 4));
                await Task.Delay(30);
            }
            PostMessage(h, WM_LBUTTONUP, 0, LP(ax2, ay2));
            await Task.Delay(300);

            var cfg = new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 };

            // ── 箭头：右上 → 左下。旧写法把外接框当主线，画出来是"左上 → 右下"的另一条对角线 ──
            w.EnterAnnotationModeForProbe("Arrow");
            await Task.Delay(250);
            int sx = ax1 + aw * 3 / 4, sy = ay1 + 120;
            int ex = ax1 + aw / 10, ey = ay1 + 500;
            int lx = sx + (ex - sx) * 3 / 10, ly = sy + (ey - sy) * 3 / 10;   // 正确线上的点
            int wx = ex + (sx - ex) * 3 / 10, wy = sy + (ey - sy) * 3 / 10;   // 旧错误对角线上的同 y 点
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(sx, sy));
            for (int i = 1; i <= 3; i++)
            {
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON, LP(sx + (ex - sx) * i / 3, sy + (ey - sy) * i / 3));
                await Task.Delay(30);
            }
            PostMessage(h, WM_LBUTTONUP, 0, LP(ex, ey));
            await Task.Delay(350);
            byte[]? capA = null;
            int headDelta = 0, lineDelta = 0, wrongDelta = 0;
            for (int i = 0; i < 5; i++)
            {
                capA = await CaptureStableAsync(AppServices.Wgc!, cfg);
                headDelta = RedDelta(capA, baseline, vw, vh, vx, vy, ex, ey, 10);
                lineDelta = RedDelta(capA, baseline, vw, vh, vx, vy, lx, ly, 5);
                wrongDelta = RedDelta(capA, baseline, vw, vh, vx, vy, wx, wy, 5);
                if (headDelta >= 3 && lineDelta >= 2) break;
                await Task.Delay(200);
            }
            bool arrowOk = headDelta >= 3 && lineDelta >= 2 && wrongDelta == 0;

            // ── 马赛克：只糊拖出来的那一块；矩形外必须与建窗前基线一致 ──
            w.EnterAnnotationModeForProbe("Mosaic");
            await Task.Delay(250);
            int mx1 = ax1 + 200, my1 = ay1 + 150, mx2 = ax1 + 500, my2 = ay1 + 350;
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(mx1, my1));
            for (int i = 1; i <= 3; i++)
            {
                PostMessage(h, WM_MOUSEMOVE, MK_LBUTTON,
                    LP(mx1 + (mx2 - mx1) * i / 3, my1 + (my2 - my1) * i / 3));
                await Task.Delay(30);
            }
            PostMessage(h, WM_LBUTTONUP, 0, LP(mx2, my2));
            await Task.Delay(400);

            byte[]? capB = null;
            int outsideDiff = -1;
            double gradIn = -1, gradBase = -1;
            int ox = ax1 + aw - 100, oy = ay1 + ah - 100;   // 选区内、马赛克矩形之外
            for (int i = 0; i < 5; i++)
            {
                capB = await CaptureStableAsync(AppServices.Wgc!, cfg);
                if (capB is null) { await Task.Delay(200); continue; }
                outsideDiff = MaxChannelDiff(capB, baseline, vw, vh, vx, vy, ox, oy);
                gradIn = MeanGrad(capB, vw, vh, vx + (mx1 + mx2) / 2, vy + (my1 + my2) / 2, 30);
                gradBase = MeanGrad(baseline, vw, vh, vx + (mx1 + mx2) / 2, vy + (my1 + my2) / 2, 30);
                if (outsideDiff >= 0 && outsideDiff <= 42) break;   // 采样容差：邻近块的块边界会挪几个像素
                await Task.Delay(200);
            }
            bool mosaicOutsideOk = outsideDiff >= 0 && outsideDiff <= 42;
            // 块内梯度必须显著低于基线（块大小 10 ⇒ 块内平坦）；基线本来就平的取样区跳过这条
            bool mosaicInsideJudged = gradBase >= 1.0;
            bool mosaicInsideOk = !mosaicInsideJudged || gradIn <= Math.Max(2.0, gradBase * 0.6);

            bool ok = arrowOk && mosaicOutsideOk && mosaicInsideOk;
            string line = $"{Name}: 箭头 头={headDelta} 线={lineDelta} 旧对角线={wrongDelta}（期望 头≥3 线≥2 旧=0）"
                + $"｜马赛克 矩形外与基线差 {outsideDiff}（期望 ≤42）"
                + $"｜块内梯度 {gradIn:0.0} vs 基线 {gradBase:0.0}"
                + (mosaicInsideJudged ? "" : "（基线太平，块内不判）")
                + (ok ? " ✓" : " ★ 预览里的标注几何与图层不一致");
            return (ok, line);
        }
        catch (Exception ex)
        {
            return (false, $"{Name}: 自身异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != 0) PostMessage(h, WM_LBUTTONUP, 0, LP(0, 0));
            try { w.Close(); } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    // ── ③/⑪ 共用的小工具 ──

    private static bool IsRed(byte[] px, int i) => px[i + 2] > 150 && px[i + 1] < 100 && px[i] < 100;

    /// <summary>窗内点周围新增的"红像素"个数（与基线相比）。用来判箭头画在哪条线上。</summary>
    private static int RedDelta(byte[]? cap, byte[] basePx, int vw, int vh, int vx, int vy, int x, int y, int r)
    {
        if (cap is null || cap.Length != (long)vw * vh * 4 || basePx.Length != (long)vw * vh * 4) return -1;
        int add = 0;
        for (int yy = Math.Max(0, vy + y - r); yy <= Math.Min(vh - 1, vy + y + r); yy++)
            for (int xx = Math.Max(0, vx + x - r); xx <= Math.Min(vw - 1, vx + x + r); xx++)
            {
                int i = (yy * vw + xx) * 4;
                if (IsRed(cap, i) && !IsRed(basePx, i)) add++;
            }
        return add;
    }

    private static int MaxChannelDiff(byte[] a, byte[] b, int vw, int vh, int vx, int vy, int x, int y)
    {
        if (a.Length != (long)vw * vh * 4 || b.Length != (long)vw * vh * 4) return -1;
        int sx = Math.Clamp(vx + x, 0, vw - 1), sy = Math.Clamp(vy + y, 0, vh - 1);
        int i = (sy * vw + sx) * 4;
        return Math.Max(Math.Abs(a[i] - b[i]),
            Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
    }

    /// <summary>邻域内相邻像素的通道差均值（粗糙度）：马赛克块内应显著下降。</summary>
    private static double MeanGrad(byte[] px, int vw, int vh, int cx, int cy, int r)
    {
        double sum = 0;
        int n = 0;
        for (int y = Math.Max(0, cy - r); y < Math.Min(vh - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x < Math.Min(vw - 1, cx + r); x++)
            {
                int i = (y * vw + x) * 4, j = i + 8;
                sum += Math.Abs(px[i] - px[j]) + Math.Abs(px[i + 1] - px[j + 1]) + Math.Abs(px[i + 2] - px[j + 2]);
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>
    /// ⑭ 托盘注册/删除（真机）：用一个真 HWND 真发 Shell_NotifyIcon —— 判"注册真的挂上了"、
    /// "删除真的返回成功"、"连删两次只发一次 NIM_DELETE（幂等闸）"、以及 Dispose 之后不再加删。
    /// <para>为什么塞进探针：托盘图标是纯 P/Invoke，编译通过 0 证据；而它失败时用户看到的是
    /// "关窗后进程还在、托盘里没有图标"这种找不回的孤儿状态。注册失败时这里直接判红并点名，
    /// 不打"跳过"—— 判成"不判"就等于替一个可能真坏了的链路开脱。</para>
    /// </summary>
    private static (bool Ok, string Line) RunTrayPhase(nint hwnd)
    {
        const string Name = "⑭ 托盘注册/删除";
        if (hwnd == 0) return (false, $"{Name}: 没有可用的 HWND ⇒ 不判");
        var tray = new TrayIconManager(hwnd);
        try
        {
            bool added = tray.IconRegistered;
            tray.RemoveIcon();
            bool removed = tray.ProbeRemoveResult == true;
            int callsAfterFirst = tray.ProbeDeleteCalls;
            tray.RemoveIcon();                       // 幂等闸：第二次不许再发 NIM_DELETE
            bool idempotent = tray.ProbeDeleteCalls == callsAfterFirst;
            tray.Dispose();                          // Dispose 里还会 RemoveIcon 一次，同样要被闸住
            bool disposeQuiet = tray.ProbeDeleteCalls == callsAfterFirst;
            bool ok = added && removed && callsAfterFirst == 1 && idempotent && disposeQuiet;
            string line = $"{Name}: 注册={added}｜NIM_DELETE 返回={tray.ProbeRemoveResult}｜"
                + $"两次 RemoveIcon 共发 {callsAfterFirst} 次删={idempotent}｜Dispose 不再加删={disposeQuiet}"
                + (ok ? " ✓" : added
                    ? " ★ 删除没落地或幂等闸失效（看 TrayIcon 告警行里的 Win32 错误）"
                    : " ★ 注册失败：进程完整性低于 Explorer 时 UIPI 会拦（见 AddTrayIcon 注释）");
            return (ok, line);
        }
        finally
        {
            tray.Dispose();
        }
    }

    /// <summary>
    /// ⑩ 置顶贴图窗：用一块合成像素真的开一扇窗，量四件事 —— 窗口存在且带 WS_EX_TOPMOST、
    /// 滚轮 +120 后客户区宽按 1.2 倍变、工具栏"复制"真的调到回调、WM_CLOSE 之后窗口消失。
    /// <para>为什么塞进这条探针：贴图窗全程 P/Invoke（DIB/StretchBlt/菜单），编译通过完全不代表它能开；
    /// 而它开不出来时用户看到的是"点了贴图没有任何反应"，日志里也只会有一行。</para>
    /// </summary>
    private static async Task<(bool Ok, string Line)> RunPinPhaseAsync()
    {
        const string Name = "⑩ 置顶贴图";
        const string PinClass = "TTC_PinWnd";
        const int pw = 320, ph = 200;
        var px = new byte[pw * ph * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0xC0; px[i + 1] = 0x60; px[i + 2] = 0x20; px[i + 3] = 0xFF; // 不透明蓝绿
        }
        int copies = 0;
        Task Stub(byte[] p, int w, int h) { copies++; return Task.CompletedTask; }

        try
        {
            PinWindow.Show(px, pw, ph, Stub, Stub, Stub);
            await Task.Delay(300);
            nint h = FindWindowW(PinClass, null);
            if (h == 0) return (false, $"{Name}: Show 之后找不到 {PinClass} ⇒ 不判");

            long exStyle = (long)GetWindowLongPtrW(h, -20 /* GWL_EXSTYLE */);
            bool topmost = (exStyle & 0x8 /* WS_EX_TOPMOST */) != 0;
            GetWindowRect(h, out var r0);
            int w0 = r0.Right - r0.Left;

            // ── 真的画出来了吗（像素级）──
            // "窗口能开"不等于"画面画出来了"：2026-10-28 实机抓到的正是后者 —— FillRect 的
            // DllImport 写成了 gdi32.dll（实际在 user32.dll），首次 WM_PAINT 直接抛
            // EntryPointNotFoundException，边框/图像/工具栏一条都没画；而置顶位、缩放尺寸、
            // 回调次数、能关掉这些读数全绿。所以这里抓一帧真桌面，在窗内取两点：
            // 图像区必须是我们喂进去的蓝绿色，工具栏区必须是暗色。
            var displays = DisplayEnumerator.EnumerateDisplaysUncached();
            int vx = displays.Count > 0 ? displays.Min(d => d.X) : 0;
            int vy = displays.Count > 0 ? displays.Min(d => d.Y) : 0;
            int vw = displays.Count > 0 ? displays.Max(d => d.X + d.Width) - vx : 0;
            int vh = displays.Count > 0 ? displays.Max(d => d.Y + d.Height) - vy : 0;
            byte[]? cap = null;
            for (int i = 0; i < 3; i++)
            {
                cap = await CaptureStableAsync(AppServices.Wgc!, new WgcCaptureConfig { PreferHdr = false, FrameTimeoutMs = 4000 });
                var probeImg = cap is null ? null : PixelAt(cap, vw, vh, r0.Left - vx + 10, r0.Top - vy + 10);
                if (probeImg is { } p0 && Math.Abs(p0.B - 0xC0) <= 10 && Math.Abs(p0.G - 0x60) <= 10 && Math.Abs(p0.R - 0x20) <= 10) break;
                await Task.Delay(200);
            }
            var imgPx = cap is null || cap.Length != vw * vh * 4 ? null : PixelAt(cap, vw, vh, r0.Left - vx + 10, r0.Top - vy + 10);
            var barPx = cap is null || cap.Length != vw * vh * 4 ? null : PixelAt(cap, vw, vh, r0.Left - vx + 12, r0.Top - vy + 178);
            bool imgPainted = imgPx is { } ip && Math.Abs(ip.B - 0xC0) <= 10 && Math.Abs(ip.G - 0x60) <= 10 && Math.Abs(ip.R - 0x20) <= 10;
            // 工具栏底色 BarBg(37,40,49) / 悬停 BarHover(48,58,80)：都必须是"暗"的，而不是图像那张蓝绿色
            bool barPainted = barPx is { } bp && bp.R < 90 && bp.G < 90 && bp.B < 110;
            bool paintOk = imgPainted && barPainted;
            string paintNote = imgPx is null || barPx is null
                ? "抓不到屏幕帧/取样点在窗外"
                : $"图像区 {(imgPx is { } i2 ? $"(B{i2.B},G{i2.G},R{i2.R})" : "无")}" +
                  $"｜工具栏 {(barPx is { } b2 ? $"(B{b2.B},G{b2.G},R{b2.R})" : "无")}";

            // 滚轮 +120 ⇒ 1.2 倍（缩放围绕中心，尺寸是最容易读的那半边）
            PostMessage(h, 0x020A /* WM_MOUSEWHEEL */, (nint)(120 << 16), LP(10, 10));
            await Task.Delay(250);
            GetWindowRect(h, out var r1);
            int w1 = r1.Right - r1.Left;
            // 期望值手算：窗口宽 = round(源宽 × 1.2) + 边框 2×2（不从 w0 再乘，避免把上一层的取整误差算进来）
            int expect = (int)Math.Round(pw * 1.2) + 4;
            bool zoomOk = Math.Abs(w1 - expect) <= 1;

            // 工具栏第一格 = 复制（底边上方 20px 处）
            int cw = r1.Right - r1.Left, ch = r1.Bottom - r1.Top;
            int bx = 2 + Math.Max(1, (cw - 4) / 8), by = ch - 20;
            PostMessage(h, WM_LBUTTONDOWN, MK_LBUTTON, LP(bx, by));
            PostMessage(h, WM_LBUTTONUP, 0, LP(bx, by));
            await Task.Delay(250);
            bool copyOk = copies == 1;

            PostMessage(h, 0x0010 /* WM_CLOSE */, 0, 0);
            await Task.Delay(250);
            bool closed = FindWindowW(PinClass, null) == 0;

            bool ok = topmost && zoomOk && copyOk && closed && paintOk;
            return (ok, $"{Name}: 置顶={topmost}｜宽 {w0}→{w1}（期望 {expect}）滚轮缩放={zoomOk}"
                       + $"｜工具栏复制回调 {copies} 次={copyOk}｜关闭后窗口消失={closed}"
                       + $"｜画面已绘制={paintOk}（{paintNote}）"
                       + (ok ? " ✓" : " ★ 贴图窗行为与代码不符"));
        }
        catch (Exception ex)
        {
            return (false, $"{Name}: 自身异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string WindowText(nint h)
    {
        var sb = new StringBuilder(512);
        int n = GetWindowTextW(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary>整屏 BGRA 帧上取一点（物理像素，原点 = 虚拟桌面左上角）；越界返回 null。</summary>
    private static (int B, int G, int R)? PixelAt(byte[] bgra, int vw, int vh, int x, int y)
    {
        if (x < 0 || y < 0 || x >= vw || y >= vh || bgra.Length < (long)vw * vh * 4) return null;
        int i = (y * vw + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    /// <summary>等一个子控件出现。PostMessage 只是把 DOWN/UP 排进窗的消息队列，落地的时刻不由探针决定；
    /// 固定 sleep 读一次会偶发"单击后没有输入框"（实机 2026-10-28 两轮一红一绿）。等到证据出现为止。</summary>
    private static async Task<nint> WaitChildAsync(nint parent, string cls, int timeoutMs = 2000)
    {
        for (int waited = 0; ; waited += 50)
        {
            nint c = FindWindowExW(parent, 0, cls, null);
            if (c != 0) return c;
            if (waited >= timeoutMs) return 0;
            await Task.Delay(50);
        }
    }

    /// <summary>把 8-bit BGRA 帧的一块裁出来存成 BMP（判红现场，便于事后看像素而不是猜读数）。</summary>
    private static void DumpBmp(byte[] bgra, int w, int h, SdrSemanticsProbe.RectI r, string path)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int cw = Math.Min(w - x0, r.W), ch = Math.Min(h - y0, r.H);
        if (cw <= 0 || ch <= 0) return;
        int stride = cw * 4;
        var buf = new byte[54 + stride * ch];
        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        BitConverter.GetBytes(buf.Length).CopyTo(buf, 2);
        BitConverter.GetBytes(54).CopyTo(buf, 10);          // 像素数据偏移
        BitConverter.GetBytes(40).CopyTo(buf, 14);          // BITMAPINFOHEADER
        BitConverter.GetBytes(cw).CopyTo(buf, 18);
        BitConverter.GetBytes(-ch).CopyTo(buf, 22);         // 负高度 = 自上而下
        BitConverter.GetBytes((short)1).CopyTo(buf, 26);
        BitConverter.GetBytes((short)32).CopyTo(buf, 28);   // 8:8:8:8 BGRA
        BitConverter.GetBytes(stride * ch).CopyTo(buf, 34);
        BitConverter.GetBytes(2835).CopyTo(buf, 38);        // 72 DPI
        BitConverter.GetBytes(2835).CopyTo(buf, 42);
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(bgra, ((y0 + y) * w + x0) * 4, buf, 54 + y * stride, stride);
        File.WriteAllBytes(path, buf);
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

    /// <summary>徽标外接框：逐行取"长度 90–400 的最大蓝段"，把这些行并起来。
    /// <para>为什么不能直接取整条带子的蓝外接框：选区描边是一条贯穿整条带子的横线（段长 = 带宽），
    /// 一并进去就得到"480×46"这种既不像徽标、又随带子宽度变化的读数（实测踩过）。
    /// 徽标本体每行的蓝段是 90–400：圆角块 + 里面十几个字符，正好落在这一区间。</para></summary>
    private static (int L, int T, int R, int B, int N, int Rows) BadgeBox(
        byte[] sdr, int w, int h, SdrSemanticsProbe.RectI r)
    {
        int l = int.MaxValue, t = int.MaxValue, rr = -1, bb = -1, n = 0, rows = 0;
        int x0 = Math.Max(0, r.X), x1 = Math.Min(w, r.X + r.W);
        for (int y = Math.Max(0, r.Y); y < Math.Min(h, r.Y + r.H); y++)
        {
            int run = 0, best = 0, bestL = -1, bestR = -1, curL = -1;
            for (int x = x0; x < x1; x++)
            {
                if (IsBlue(sdr, (y * w + x) * 4))
                {
                    if (run == 0) curL = x;
                    if (++run > best) { best = run; bestL = curL; bestR = x; }
                }
                else run = 0;
            }
            if (best is < 90 or > 400) continue;   // 满宽的描边 / 零碎杂点都不算徽标行
            rows++; n += best;
            if (bestL < l) l = bestL;
            if (bestR > rr) rr = bestR;
            if (y < t) t = y;
            if (y > bb) bb = y;
        }
        return rows == 0 ? (0, 0, -1, -1, 0, 0) : (l, t, rr, bb, n, rows);
    }

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
