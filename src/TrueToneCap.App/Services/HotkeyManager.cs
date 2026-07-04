// TrueToneCap.App/Services/HotkeyManager.cs
// 全局热键管理（基于 Win32 RegisterHotKey + WinUI 3 HWND 子类化）

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TrueToneCap.App.Services;

public static class HotkeyManager
{
    private static nint _hwnd;
    private static int _hotkeyId = -1;
    private static Action? _callback;
    private static WndProcDelegate? _wp;
    private static nint _origWp;

    private delegate nint WndProcDelegate(nint h, uint m, nint w, nint l);

    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowLongPtrW(nint h, int i, nint v);
    [DllImport("user32.dll", SetLastError = true)] static extern nint CallWindowProcW(nint p, nint h, uint m, nint w, nint l);

    private static nint Hook(nint h, uint m, nint w, nint l)
    {
        if (m == WM_HOTKEY && (int)w == _hotkeyId)
            _callback?.Invoke();
        return CallWindowProcW(_origWp, h, m, w, l);
    }

    public static bool Register(Window window, string hotkey, Action callback)
    {
        Unregister();

        if (!ParseHotkey(hotkey, out uint mods, out uint vk))
            return false;

        _hwnd = WindowNative.GetWindowHandle(window);
        _callback = callback;
        _hotkeyId = 9001;
        _wp = Hook;
        _origWp = SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wp));

        return RegisterHotKey(_hwnd, _hotkeyId, mods | MOD_NOREPEAT, vk);
    }

    public static void Unregister()
    {
        if (_hotkeyId >= 0 && _hwnd != 0)
        {
            UnregisterHotKey(_hwnd, _hotkeyId);
            _hotkeyId = -1;
        }
        _callback = null;
    }

    private static bool ParseHotkey(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var p in s.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "ALT": mods |= MOD_ALT; break;
                default:
                    vk = p.Length == 1 && char.IsLetterOrDigit(p[0]) ? (uint)char.ToUpperInvariant(p[0])
                        : p.Equals("F1", StringComparison.OrdinalIgnoreCase) ? 0x70u
                        : p.Equals("F2", StringComparison.OrdinalIgnoreCase) ? 0x71u
                        : p.Equals("F3", StringComparison.OrdinalIgnoreCase) ? 0x72u
                        : p.Equals("F4", StringComparison.OrdinalIgnoreCase) ? 0x73u
                        : p.Equals("F5", StringComparison.OrdinalIgnoreCase) ? 0x74u
                        : p.Equals("F6", StringComparison.OrdinalIgnoreCase) ? 0x75u
                        : p.Equals("F7", StringComparison.OrdinalIgnoreCase) ? 0x76u
                        : p.Equals("F8", StringComparison.OrdinalIgnoreCase) ? 0x77u
                        : p.Equals("F9", StringComparison.OrdinalIgnoreCase) ? 0x78u
                        : p.Equals("F10", StringComparison.OrdinalIgnoreCase) ? 0x79u
                        : p.Equals("F11", StringComparison.OrdinalIgnoreCase) ? 0x7Au
                        : p.Equals("F12", StringComparison.OrdinalIgnoreCase) ? 0x7Bu
                        : p.Equals("PRINT", StringComparison.OrdinalIgnoreCase) || p.Equals("PRTSC", StringComparison.OrdinalIgnoreCase) ? 0x2Cu
                        : p.Equals("SPACE", StringComparison.OrdinalIgnoreCase) ? 0x20u : 0;
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0;
    }
}
