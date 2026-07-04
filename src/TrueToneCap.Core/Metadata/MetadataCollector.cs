// TrueToneCap.Core/Metadata/MetadataCollector.cs
// 自动收集截图时的系统/窗口/显示器元数据

using System.Diagnostics;
using System.Runtime.InteropServices;
using TrueToneCap.Core.Capture;

namespace TrueToneCap.Core.Metadata;

/// <summary>元数据收集器 — 获取前台窗口、光标、显示器等信息。</summary>
public static class MetadataCollector
{
    /// <summary>收集当前环境元数据。</summary>
    public static Encoding.ImageMetadata Collect(DisplayInfo? display = null, CapturedFrame? frame = null)
    {
        var meta = new Encoding.ImageMetadata
        {
            TimestampUtc = DateTime.UtcNow
        };

        // 前台窗口信息
        var fgHwnd = GetForegroundWindow();
        if (fgHwnd != nint.Zero)
        {
            meta.ForegroundWindowTitle = GetWindowTitle(fgHwnd);
            GetWindowThreadProcessId(fgHwnd, out uint pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                meta.ForegroundProcessName = proc.ProcessName;
            }
            catch { }
        }

        // 显示器信息
        if (display != null)
        {
            meta.DisplayName = display.Name;
            meta.ScreenX = display.X;
            meta.ScreenY = display.Y;
            meta.ScreenWidth = display.Width;
            meta.ScreenHeight = display.Height;
            meta.IsHdr = display.IsHdr;
            meta.ColorSpace = display.ColorSpace.ToString();
        }

        // 光标位置
        if (GetCursorPos(out var pt))
        {
            meta.CursorX = pt.X;
            meta.CursorY = pt.Y;
            meta.CursorType = GetCursorTypeName();
        }

        return meta;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        GetWindowTextW(hwnd, buffer, length + 1);
        return new string(buffer, 0, length);
    }

    private static string GetCursorTypeName()
    {
        var cursorInfo = new CURSORINFO();
        cursorInfo.cbSize = (uint)Marshal.SizeOf<CURSORINFO>();
        if (GetCursorInfo(ref cursorInfo))
        {
            return cursorInfo.hCursor != 0 ? "Custom/App" : "Default";
        }
        return "Unknown";
    }

    // ────────────── P/Invoke ──────────────

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, char[] text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }
}
