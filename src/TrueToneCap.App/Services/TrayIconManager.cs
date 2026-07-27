// TrueToneCap.App/Services/TrayIconManager.cs
// WinUI3 原生托盘图标 — P/Invoke Shell_NotifyIcon，零外部依赖

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TrueToneCap.App.Services;

public sealed class TrayIconManager : IDisposable
{
    private readonly Window _window;
    private readonly nint _hwnd;
    private nint _hIcon;
    private bool _disposed;
    private uint _taskbarRestartMsg;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1;
    private const uint NIF_ICON = 2;
    private const uint NIF_TIP = 4;
    private const uint NIF_GUID = 0x20;
    private const uint NIF_SHOWTIP = 0x80;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 1;

    // NOTIFYICONDATAW — 完整结构体 (cbSize=956, 支持 guidItem)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public System.Action? OnCaptureHotkey { get; set; }
    public System.Action? OnExitApp { get; set; }

    public TrayIconManager(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
        _taskbarRestartMsg = RegisterWindowMessageW("TaskbarCreated");
        _hIcon = GenerateAppIcon(32);

        AddTrayIcon();
    }

    // ═══════════════════════════════════════
    //  托盘图标操作
    // ═══════════════════════════════════════

    private void AddTrayIcon()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "TrueToneCap",
            guidItem = new Guid("8F7C5B3A-1D2E-4A6F-9C8B-7E3D5F1A2B4C"),
        };
        Shell_NotifyIconW(NIM_ADD, ref nid);

        // 通知 Explorer 使用现代通知区域 (NOTIFYICON_VERSION_4 = Win7+)
        nid.uVersionOrTimeout = 4;
        Shell_NotifyIconW(NIM_SETVERSION, ref nid);
    }

    public void MinimizeToTray()
    {
        _window.AppWindow.Hide();
    }

    public void Restore()
    {
        _window.AppWindow.Show(true);
        _window.AppWindow.MoveInZOrderAtTop();
    }

    public void RemoveIcon()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
    }

    public bool RegisterCaptureHotkey(string hotkey)
    {
        return HotkeyManager.Register(_window, hotkey, () =>
            _window.DispatcherQueue.TryEnqueue(() => OnCaptureHotkey?.Invoke()));
    }

    private void ExitApp()
    {
        RemoveIcon();
        HotkeyManager.Unregister();
        OnExitApp?.Invoke();
        _window.Close();
    }

    /// <summary>处理托盘消息（由 MainWindow 的 WndProc 调用）。</summary>
    public void HandleTrayMessage(uint msg, nint lParam)
    {
        if (msg == WM_TRAYICON)
        {
            uint low = (uint)(lParam.ToInt64() & 0xFFFF);
            if (low == WM_LBUTTONDOWN)
                Restore();
            else if (low == WM_RBUTTONUP)
                ShowContextMenu();
        }
        else if (msg == _taskbarRestartMsg)
        {
            // Explorer 重启后重建图标
            if (_hIcon == 0) _hIcon = GenerateAppIcon(32);
            AddTrayIcon();
        }
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, 1, "📷 区域截图");
        AppendMenuW(menu, 0, 2, "🖥 打开主窗口");
        AppendMenuW(menu, 0x800, 0, "");
        AppendMenuW(menu, 0, 3, "❌ 退出 TrueToneCap");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenu(menu, 0x0100, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case 1: OnCaptureHotkey?.Invoke(); break;
            case 2: Restore(); break;
            case 3: ExitApp(); break;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // ═══════════════════════════════════════
    //  程序化图标生成（32x32 ARGB）
    // ═══════════════════════════════════════

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern uint SetPixelV(nint hdc, int x, int y, uint color);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    /// <summary>自生成 TrueToneCap 托盘图标（蓝青渐变圆形 + "T" 字母）。</summary>
    private static nint GenerateAppIcon(int size)
    {
        try
        {
            nint hdcScreen = GetDC(0);
            nint hdcMem = CreateCompatibleDC(hdcScreen);
            nint hBitmap = CreateCompatibleBitmap(hdcScreen, size, size);
            nint hOldBitmap = SelectObject(hdcMem, hBitmap);

            int cx = size / 2, cy = size / 2;
            int r2 = (size / 2 - 2) * (size / 2 - 2);

            // 圆形渐变背景（蓝青彩虹）
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int dx = x - cx, dy = y - cy;
                    int dist2 = dx * dx + dy * dy;
                    if (dist2 > r2 + 4) continue;

                    float alpha = dist2 <= r2 ? 1f
                        : Math.Clamp(1f - (dist2 - r2) / 4f, 0f, 1f);

                    float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
                    if (angle < 0) angle += 360;
                    float hue = 200 + angle * 0.15f;
                    var (r, g, b) = HsvToRgb(hue % 360, 0.85f, 0.92f);
                    byte br = (byte)(r * alpha * 255), bg = (byte)(g * alpha * 255), bb = (byte)(b * alpha * 255);
                    SetPixelV(hdcMem, x, y, (uint)((br << 16) | (bg << 8) | bb));
                }
            }

            // 中心白色 "T" 字母
            int tw = size / 3, th = Math.Max(2, size / 8);
            int topY = cy - size / 5;
            for (int y = topY; y < topY + th; y++)
            for (int x = cx - tw / 2; x < cx + tw / 2; x++)
                SetPixelV(hdcMem, x, y, 0x00FFFFFF);
            for (int y = topY + th; y < topY + th + size / 3; y++)
            for (int x = cx - th / 2; x < cx + th / 2; x++)
                SetPixelV(hdcMem, x, y, 0x00FFFFFF);

            SelectObject(hdcMem, hOldBitmap);

            var iconInfo = new ICONINFO { fIcon = true, hbmColor = hBitmap, hbmMask = hBitmap };
            nint hIcon = CreateIconIndirect(ref iconInfo);

            DeleteDC(hdcMem);
            ReleaseDC(0, hdcScreen);
            DeleteObject(hBitmap);

            LogService.Debug("TrayIcon", $"自生成 {size}x{size} 图标成功");
            return hIcon;
        }
        catch (Exception ex)
        {
            LogService.Warn("TrayIcon", $"图标生成失败: {ex.Message}");
            return 0;
        }
    }

    private static (float r, float g, float b) HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - MathF.Abs((h / 60) % 2 - 1));
        float m = v - c;
        return (h % 360) switch
        {
            < 60 => (c + m, x + m, m),
            < 120 => (x + m, c + m, m),
            < 180 => (m, c + m, x + m),
            < 240 => (m, x + m, c + m),
            < 300 => (x + m, m, c + m),
            _ => (c + m, m, x + m),
        };
    }

    // ═══════════════════════════════════════
    //  IDisposable
    // ═══════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        if (_hIcon != 0) { DestroyIcon(_hIcon); _hIcon = 0; }
    }
}
