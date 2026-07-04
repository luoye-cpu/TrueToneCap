// TrueToneCap.App/Services/TrayIconManager.cs
// 使用 WinForms NotifyIcon 管理托盘图标和右键菜单

using Microsoft.UI.Xaml;

namespace TrueToneCap.App.Services;

public sealed class TrayIconManager : IDisposable
{
    private readonly Window _window;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu;
    private bool _disposed;

    public System.Action? OnCaptureHotkey { get; set; }

    public TrayIconManager(Window window)
    {
        _window = window;

        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _contextMenu.Items.Add("打开主窗口", null, (_, _) => Restore());
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add("退出 TrueToneCap", null, (_, _) => ExitApp());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TrueToneCap",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Restore();
        };
    }

    public void MinimizeToTray()
    {
        _notifyIcon.Visible = true;
        _window.AppWindow.Hide();
    }

    public void Restore()
    {
        _window.AppWindow.Show(true);
        _window.AppWindow.MoveInZOrderAtTop();
    }

    public void AddIcon() => _notifyIcon.Visible = true;
    public void RemoveIcon() => _notifyIcon.Visible = false;

    public bool RegisterCaptureHotkey(string hotkey)
    {
        return HotkeyManager.Register(_window, hotkey, () =>
            _window.DispatcherQueue.TryEnqueue(() => OnCaptureHotkey?.Invoke()));
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        HotkeyManager.Unregister();
        System.Environment.Exit(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HotkeyManager.Unregister();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
