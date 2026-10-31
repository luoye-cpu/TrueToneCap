// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/PinWindow.cs
// 置顶贴图窗 — 把一帧截图像便签一样钉在屏幕上（对齐上游 PR #1 的 PinWindow 语义）

using System.Runtime.InteropServices;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.App.Services;

/// <summary>
/// 置顶贴图窗：左键拖动、滚轮缩放、右键 复制 / 保存为 PNG / 保存为 AVIF / 隐藏工具栏 / 销毁。
/// <para>为什么是原生 Win32 而不是 WinUI 窗口：贴图必须"不抢焦点、始终置顶、按物理像素 1:1 显示"，
/// 且它要在主窗口的截图流程结束之后继续独立存活；GDI StretchBlt + WS_EX_TOPMOST 是这台机器上最短的一条路。</para>
/// <para>像素是调用方给的 BGRA8 副本（与保存/复制走同一份合成结果，标注不会被绕过）。</para>
/// </summary>
internal sealed class PinWindow
{
    private const string CN = "TTC_PinWnd";
    private const int BorderW = 2;
    private const int BarHDip = 34;
    private const int BtnCount = 4;

    // 颜色（COLORREF = 0x00BBGGRR）
    private const uint BorderColor = 0x00C88746; // RGB(70,135,200) — 与上游贴图窗边框同色
    private const uint BarBg = 0x00312825;       // RGB(37,40,49)
    private const uint BarEdge = 0x005F463C;     // RGB(60,70,95) — 底栏上沿分隔线
    private const uint BarHover = 0x00503A30;    // RGB(48,58,80)
    private const uint BarText = 0x00FFFFFF;
    private const uint BarDanger = 0x00AAAAFF;   // RGB(255,170,170)

    private readonly byte[] _pixels;
    private readonly int _srcW, _srcH;
    private readonly Func<byte[], int, int, Task> _savePng;
    private readonly Func<byte[], int, int, Task> _saveAvif;
    private readonly Func<byte[], int, int, Task> _copy;

    private nint _hwnd, _memDc, _hbm, _bits, _uiFont;
    private double _zoom = 1.0;
    private double _scale = 1.0;
    private int _barH = BarHDip;
    private bool _barVisible = true;
    private int _hoverBtn = -1;
    private bool _dragging;
    private int _dragCursorX, _dragCursorY, _dragWinX, _dragWinY;

    private static bool s_classReg;
    private static readonly object s_classLock = new();
    private static WndProcDelegate? s_wndProc;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, PinWindow> s_windows = new();

    private PinWindow(byte[] pixels, int w, int h,
        Func<byte[], int, int, Task> savePng, Func<byte[], int, int, Task> saveAvif, Func<byte[], int, int, Task> copy)
    {
        _pixels = (byte[])pixels.Clone(); // 调用方之后还会用原缓冲，不共享
        _srcW = w; _srcH = h;
        _savePng = savePng; _saveAvif = saveAvif; _copy = copy;
    }

    /// <summary>钉住一帧 BGRA8 图像。保存/复制回调跑在 UI 线程（与主窗口同一条编码管线）。</summary>
    public static void Show(byte[] bgra, int w, int h,
        Func<byte[], int, int, Task> savePng, Func<byte[], int, int, Task> saveAvif, Func<byte[], int, int, Task> copy)
    {
        if (bgra is null || w <= 0 || h <= 0 || bgra.Length < w * h * 4) return;
        try
        {
            EnsureClass();
            new PinWindow(bgra, w, h, savePng, saveAvif, copy).Create();
        }
        catch (Exception ex)
        {
            LogService.Error("Pin", $"贴图窗创建失败: {ex.Message}", ex);
        }
    }

    private void Create()
    {
        // 源位图：DIBSection 顶朝下，行序与 BGRA8 缓冲一致
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _srcW,
                biHeight = -_srcH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            }
        };
        _hbm = CreateDIBSection(0, ref bmi, 0, out _bits, 0, 0);
        if (_hbm == 0 || _bits == 0) throw new InvalidOperationException($"CreateDIBSection 失败: {Marshal.GetLastWin32Error()}");
        Marshal.Copy(_pixels, 0, _bits, _srcW * _srcH * 4);
        _memDc = CreateCompatibleDC(0);
        if (_memDc == 0) throw new InvalidOperationException($"CreateCompatibleDC 失败: {Marshal.GetLastWin32Error()}");
        SelectObject(_memDc, _hbm);

        // 初始缩放：能整张贴进光标所在屏的工作区（不超过 1:1 —— 放大交给滚轮）
        GetCursorPos(out var cursor);
        var area = WorkAreaAt(cursor);
        double fit = Math.Min(1.0, Math.Min(
            (area.Right - area.Left - 40.0) / _srcW,
            (area.Bottom - area.Top - 40.0) / _srcH));
        if (fit <= 0 || double.IsNaN(fit)) fit = 1.0;
        _zoom = Math.Clamp(fit, 0.1, 8.0);

        int cw = Math.Max(1, (int)Math.Round(_srcW * _zoom)) + BorderW * 2;
        int ch = Math.Max(1, (int)Math.Round(_srcH * _zoom)) + BorderW * 2;
        int x = Math.Clamp(cursor.X - cw / 2, area.Left, Math.Max(area.Left, area.Right - cw));
        int y = Math.Clamp(cursor.Y - ch / 2, area.Top, Math.Max(area.Top, area.Bottom - ch));

        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, CN, LocaleManager.Tr("贴图"),
            WS_POPUP, x, y, cw, ch, 0, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowExW 失败: {Marshal.GetLastWin32Error()}");
        s_windows[_hwnd] = this;

        // 底栏按 DPI 放大：34px 写死 · 12px 系统字体，在 4K@150% 上只有 23 DIP ——
        // 2026-10-28 实机反馈"点了贴图只看到一块能拖的选区、不知道还能操作"，就是这条太细。
        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi > 0 ? dpi / 96.0 : 1.0;
        _barH = Math.Max(28, (int)Math.Round(BarHDip * _scale));
        _uiFont = CreateFontW(-Math.Max(14, (int)Math.Round(15 * _scale)), 0, 0, 0, 400, 0, 0, 0,
            1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE_QUALITY*/, 0, "Microsoft YaHei UI");

        ShowWindow(_hwnd, SW_SHOW);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        LogService.Info("Pin", $"贴图窗打开 {_srcW}x{_srcH} @{_zoom:0.###} → {cw}x{ch} @({x},{y})", LogCategory.UI);
    }

    private void Destroy()
    {
        if (_hwnd != 0) { s_windows.TryRemove(_hwnd, out _); DestroyWindow(_hwnd); _hwnd = 0; }
        if (_uiFont != 0) { DeleteObject(_uiFont); _uiFont = 0; }
        if (_memDc != 0) { DeleteDC(_memDc); _memDc = 0; }
        if (_hbm != 0) { DeleteObject(_hbm); _hbm = 0; _bits = 0; }
        LogService.Info("Pin", "贴图窗关闭", LogCategory.UI);
    }

    private static void EnsureClass()
    {
        lock (s_classLock)
        {
            if (s_classReg) return;
            s_wndProc = StaticWndProc;
            var wc = new WNDCLASSW
            {
                style = 0x0001 | 0x0002, // CS_VREDRAW | CS_HREDRAW
                proc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                cur = LoadCursorW(0, 32512), // IDC_ARROW
                cls = CN,
            };
            if (RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = 类已存在
                throw new InvalidOperationException($"RegisterClassW 失败: {Marshal.GetLastWin32Error()}");
            s_classReg = true;
        }
    }

    private static nint StaticWndProc(nint h, uint m, nint w, nint l)
    {
        if (s_windows.TryGetValue(h, out var self))
        {
            try { return self.WndProc(h, m, w, l); }
            catch (Exception ex)
            {
                LogService.Error("Pin", $"WndProc 处理 0x{m:X} 时异常", ex);
                return 0;
            }
        }
        return DefWindowProcW(h, m, w, l);
    }

    private nint WndProc(nint h, uint m, nint w, nint l)
    {
        switch (m)
        {
            case WM_PAINT: Paint(h); return 0;
            case WM_ERASEBKGND: return 1;
            case WM_MOUSEWHEEL: // delta 在高 16 位（有符号）
                {
                    int delta = (short)((w.ToInt64() >> 16) & 0xFFFF);
                    if (delta != 0) SetZoom(h, _zoom * Math.Pow(1.2, delta / 120.0));
                    return 0;
                }
            case WM_LBUTTONDOWN:
                {
                    int mx = GX(l), my = GY(l);
                    int btn = HitToolbar(h, mx, my);
                    if (btn >= 0) { InvokeAction(btn); return 0; }
                    _dragging = true;
                    GetCursorPos(out var pt);
                    GetWindowRect(h, out var rc);
                    _dragCursorX = pt.X; _dragCursorY = pt.Y;
                    _dragWinX = rc.Left; _dragWinY = rc.Top;
                    SetCapture(h);
                    return 0;
                }
            case WM_MOUSEMOVE:
                {
                    if (_dragging)
                    {
                        GetCursorPos(out var pt);
                        SetWindowPos(h, 0, _dragWinX + pt.X - _dragCursorX, _dragWinY + pt.Y - _dragCursorY,
                            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                        return 0;
                    }
                    int btn = HitToolbar(h, GX(l), GY(l));
                    if (btn != _hoverBtn) { _hoverBtn = btn; InvalidateRect(h, 0, false); }
                    return 0;
                }
            case WM_LBUTTONUP:
                if (_dragging) { _dragging = false; ReleaseCapture(); }
                return 0;
            case WM_RBUTTONUP:
                ShowMenu(h);
                return 0;
            case WM_KEYDOWN:
                if ((int)w == 0x1B) { /* Esc 不销毁：贴图常驻，销毁走右键菜单 */ }
                if ((int)w == 0x2E) { DestroyWindow(h); return 0; } // Delete = 销毁
                return 0;
            case WM_CLOSE:
                DestroyWindow(h);
                return 0;
            case WM_DESTROY:
                Destroy();
                return 0;
        }
        return DefWindowProcW(h, m, w, l);
    }

    private void Paint(nint h)
    {
        var ps = new PAINTSTRUCT { rgbReserved = new byte[32] };
        var hdc = BeginPaint(h, ref ps);
        try
        {
            GetClientRect(h, out var rc);
            var brush = CreateSolidBrush(BorderColor);
            FillRect(hdc, ref rc, brush);
            DeleteObject(brush);

            int iw = Math.Max(1, (int)Math.Round(_srcW * _zoom));
            int ih = Math.Max(1, (int)Math.Round(_srcH * _zoom));
            SetStretchBltMode(hdc, 4 /* HALFTONE */);
            SetBrushOrgEx(hdc, 0, 0, 0);
            StretchBlt(hdc, BorderW, BorderW, iw, ih, _memDc, 0, 0, _srcW, _srcH, 0x00CC0020 /* SRCCOPY */);

            if (_barVisible)
            {
                int by = BorderW + ih - _barH;
                if (by < BorderW) by = BorderW;
                // 上沿一条亮线：底栏是深灰、图像可能是任意内容，没有这条线就分不清"这是底栏"还是"图片的一部分"
                var edge = new RECT { l = BorderW, t = by - 1, r = BorderW + iw, b = by };
                brush = CreateSolidBrush(BarEdge);
                FillRect(hdc, ref edge, brush);
                DeleteObject(brush);

                var bar = new RECT { l = BorderW, t = by, r = BorderW + iw, b = by + _barH };
                brush = CreateSolidBrush(BarBg);
                FillRect(hdc, ref bar, brush);
                DeleteObject(brush);

                var old = SelectObject(hdc, _uiFont != 0 ? _uiFont : GetStockObject(17 /* DEFAULT_GUI_FONT */));
                SetBkMode(hdc, 1 /* TRANSPARENT */);
                for (int i = 0; i < BtnCount; i++)
                {
                    var r = ButtonRect(i, iw, by);
                    if (i == _hoverBtn)
                    {
                        brush = CreateSolidBrush(BarHover);
                        FillRect(hdc, ref r, brush);
                        DeleteObject(brush);
                    }
                    SetTextColor(hdc, i == BtnCount - 1 ? BarDanger : BarText);
                    // DT_END_ELLIPSIS：图很小时按钮比字窄，宁可出现"复制…"，也不许跨格叠字
                    DrawTextW(hdc, ButtonLabel(i), -1, ref r,
                        0x0001 /* DT_CENTER */ | 0x0004 /* DT_VCENTER */ | 0x0020 /* DT_SINGLELINE */
                        | 0x8000 /* DT_END_ELLIPSIS */);
                }
                SelectObject(hdc, old);
            }
        }
        finally
        {
            EndPaint(h, ref ps);
        }
    }

    /// <summary>底栏按钮文字（短标签）。完整说法在右键菜单里 —— 每个按钮只有图宽的四分之一，
    /// 小图上"保存为 PNG"这类长句会跨过按钮边界、和后一格叠字（2026-10-28 实机截图可见）。</summary>
    private static string ButtonLabel(int i) => i switch
    {
        0 => LocaleManager.Tr("复制"),
        1 => "PNG",
        2 => "AVIF",
        _ => LocaleManager.Tr("销毁"),
    };

    private RECT ButtonRect(int i, int imgW, int barTop)
    {
        int w = Math.Max(1, imgW / BtnCount);
        return new RECT
        {
            l = BorderW + i * w,
            t = barTop,
            r = i == BtnCount - 1 ? BorderW + imgW : BorderW + (i + 1) * w,
            b = barTop + _barH,
        };
    }

    /// <summary>命中工具栏按钮；-1 = 不在工具栏上。</summary>
    private int HitToolbar(nint h, int mx, int my)
    {
        if (!_barVisible) return -1;
        int iw = Math.Max(1, (int)Math.Round(_srcW * _zoom));
        int ih = Math.Max(1, (int)Math.Round(_srcH * _zoom));
        int by = Math.Max(BorderW, BorderW + ih - _barH);
        if (my < by || my >= by + _barH) return -1;
        for (int i = 0; i < BtnCount; i++)
        {
            var r = ButtonRect(i, iw, by);
            if (mx >= r.l && mx < r.r) return i;
        }
        return -1;
    }

    private void InvokeAction(int btn)
    {
        switch (btn)
        {
            case 0: Fire(_copy); break;
            case 1: Fire(_savePng); break;
            case 2: Fire(_saveAvif); break;
            case 3: DestroyWindow(_hwnd); break;
        }
    }

    /// <summary>交给主窗口的编码管线（它自己会在 UI 线程起步、失败留日志）。</summary>
    private void Fire(Func<byte[], int, int, Task> action)
    {
        try
        {
            var t = action((byte[])_pixels.Clone(), _srcW, _srcH);
            _ = t.ContinueWith(x =>
            {
                var ex = x.Exception?.GetBaseException();
                if (ex is not null) LogService.Error("Pin", $"贴图动作失败: {ex.Message}", ex);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            LogService.Error("Pin", $"贴图动作启动失败: {ex.Message}", ex);
        }
    }

    private void ShowMenu(nint h)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, 0, 1, LocaleManager.Tr(_barVisible ? "隐藏工具栏" : "显示工具栏"));
            AppendMenuW(menu, 0, 2, LocaleManager.Tr("复制"));
            AppendMenuW(menu, 0, 3, LocaleManager.Tr("保存为 PNG"));
            AppendMenuW(menu, 0, 4, LocaleManager.Tr("保存为 AVIF"));
            AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
            AppendMenuW(menu, 0, 5, LocaleManager.Tr("销毁"));
            GetCursorPos(out var pt);
            int cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */,
                pt.X, pt.Y, h, 0);
            switch (cmd)
            {
                case 1: _barVisible = !_barVisible; InvalidateRect(h, 0, false); break;
                case 2: Fire(_copy); break;
                case 3: Fire(_savePng); break;
                case 4: Fire(_saveAvif); break;
                case 5: DestroyWindow(h); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private void SetZoom(nint h, double z)
    {
        z = Math.Clamp(z, 0.1, 8.0);
        if (Math.Abs(z - _zoom) < 1e-6) return;
        GetWindowRect(h, out var rc);
        int cx = rc.Left + (rc.Right - rc.Left) / 2;
        int cy = rc.Top + (rc.Bottom - rc.Top) / 2;
        int nw = Math.Max(1, (int)Math.Round(_srcW * z)) + BorderW * 2;
        int nh = Math.Max(1, (int)Math.Round(_srcH * z)) + BorderW * 2;
        _zoom = z;
        SetWindowPos(h, 0, cx - nw / 2, cy - nh / 2, nw, nh, SWP_NOZORDER | SWP_NOACTIVATE);
        InvalidateRect(h, 0, false);
    }

    private static RECT WorkAreaAt(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != 0 && GetMonitorInfoW(monitor, ref info)) return info.rcWork;
        return new RECT { l = 0, t = 0, r = 1920, b = 1080 };
    }

    // ═══════════════════════════════════════
    //  Win32
    // ═══════════════════════════════════════

    private static int GX(nint l) => (short)(l.ToInt64() & 0xFFFF);
    private static int GY(nint l) => (short)((l.ToInt64() >> 16) & 0xFFFF);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_SHOW = 5;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_CLOSE = 0x0010;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100, WM_DESTROY = 0x0002;

    private delegate nint WndProcDelegate(nint h, uint m, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style; public nint proc; public int cbClsExtra; public int cbWndExtra;
        public nint inst; public nint icon; public nint cur; public nint bg; public string? menu; public string? cls;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int l, t, r, b;
        public readonly int Left => l;
        public readonly int Top => t;
        public readonly int Right => r;
        public readonly int Bottom => b;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc; public int fErase; public RECT rc; public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint h, out RECT rc);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint h, out RECT rc);
    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint h, nint rect, bool erase);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint h);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint inst, int name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint h, nint param);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint h);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);
    // ⚠ FillRect 在 user32.dll，不在 gdi32.dll（实机首次 WM_PAINT 抛 EntryPointNotFoundException，
    // 整条绘制路径连边框都没画出来；编译期与"窗口能开"都发现不了）。
    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rc, nint brush);
    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(nint hdc, int x, int y, nint prev);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint dest, int x, int y, int w, int h,
        nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO bmi, uint usage,
        out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint hdc, string text, int count, ref RECT rc, uint flags);
}
