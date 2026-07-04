// TrueToneCap.App/Services/HotkeyManager.cs
// 全局热键管理（基于 Win32 RegisterHotKey + 冲突检测 + 回退）
// 窗口子类化仅执行一次，委托用 GCHandle 防止 GC 回收 → 消除闪退

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
    private static GCHandle _wpHandle;      // 防止委托被 GC 回收
    private static string _lastRegisteredHotkey = "";
    private static bool _subclassed;         // 窗口是否已子类化

    private delegate nint WndProcDelegate(nint h, uint m, nint w, nint l);

    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

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

    /// <summary>已注册的热键字符串（空表示注册失败）。</summary>
    public static string RegisteredHotkey => _lastRegisteredHotkey;

    /// <summary>注册全局热键。窗口子类化仅首次执行。</summary>
    public static bool Register(Window window, string hotkey, Action callback)
    {
        // 先注销旧热键（不注销窗口子类化）
        UnregisterHotkeyOnly();

        _hwnd = WindowNative.GetWindowHandle(window);
        _callback = callback;
        _hotkeyId = 9001;

        // ── 窗口子类化：仅首次执行（委托用 GCHandle 钉住，防止 GC 回收 → 闪退）──
        if (!_subclassed)
        {
            _wp = Hook;
            _wpHandle = GCHandle.Alloc(_wp);          // 钉住委托
            _origWp = SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wp));
            if (_origWp == nint.Zero)
            {
                // 子类化失败：释放 GCHandle 并标记为未子类化
                System.Diagnostics.Debug.WriteLine($"[HotkeyManager] 窗口子类化失败 (err={Marshal.GetLastWin32Error()})");
                if (_wpHandle.IsAllocated) _wpHandle.Free();
                _wp = null;
                _lastRegisteredHotkey = "";
                return false;
            }
            _subclassed = true;
        }

        // 首先尝试用户首选热键
        if (TryRegister(hotkey))
            return true;

        // 回退方案
        string[] fallbacks = ["Ctrl+Shift+X", "Ctrl+Alt+S", "Alt+Print", "Print"];
        foreach (var fb in fallbacks)
        {
            if (TryRegister(fb))
                return true;
        }

        _lastRegisteredHotkey = "";
        return false;
    }

    private static bool TryRegister(string hotkey)
    {
        if (!ParseHotkey(hotkey, out uint mods, out uint vk))
            return false;

        // 临时注册检测冲突
        if (RegisterHotKey(_hwnd, _hotkeyId + 1, mods | MOD_NOREPEAT, vk))
            UnregisterHotKey(_hwnd, _hotkeyId + 1);
        else
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyManager] 冲突: {hotkey} (err={Marshal.GetLastWin32Error()})");
            return false;
        }

        if (!RegisterHotKey(_hwnd, _hotkeyId, mods | MOD_NOREPEAT, vk))
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyManager] 注册失败: {hotkey}");
            return false;
        }

        _lastRegisteredHotkey = hotkey;
        System.Diagnostics.Debug.WriteLine($"[HotkeyManager] 已注册: {hotkey}");
        return true;
    }

    /// <summary>仅注销 Win32 热键，保留窗口子类化。</summary>
    private static void UnregisterHotkeyOnly()
    {
        if (_hotkeyId >= 0 && _hwnd != 0)
        {
            UnregisterHotKey(_hwnd, _hotkeyId);
            _hotkeyId = -1;
        }
        _callback = null;
        _lastRegisteredHotkey = "";
    }

    /// <summary>完全注销（热键 + 释放 GCHandle）。应在窗口销毁时调用。</summary>
    public static void Unregister()
    {
        UnregisterHotkeyOnly();
        if (_wpHandle.IsAllocated)
            _wpHandle.Free();
        _subclassed = false;
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
                case "WIN": case "WINDOWS": mods |= MOD_WIN; break;
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
                        : p.Equals("SPACE", StringComparison.OrdinalIgnoreCase) ? 0x20u
                        : p.Equals("OEM_3", StringComparison.OrdinalIgnoreCase) || p.Equals("`", StringComparison.OrdinalIgnoreCase) ? 0xC0u
                        : p.Equals("OEM_MINUS", StringComparison.OrdinalIgnoreCase) || p.Equals("-", StringComparison.OrdinalIgnoreCase) ? 0xBDu
                        : p.Equals("OEM_PLUS", StringComparison.OrdinalIgnoreCase) || p.Equals("=", StringComparison.OrdinalIgnoreCase) ? 0xBBu
                        : 0;
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0;
    }
}
