// TrueToneCap.App/Services/StartupManager.cs
// 管理 Windows 开机自启动（注册表 Run 键）

using Microsoft.Win32;

namespace TrueToneCap.App.Services;

public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TrueToneCap";

    /// <summary>是否已启用开机自启。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(AppName) is string val && val == GetAppPath();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartupManager] 读取注册表失败: {ex.Message}");
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKey);

                if (value)
                    key.SetValue(AppName, $"\"{GetAppPath()}\" --autostart");
                else
                    key.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartupManager] 写入注册表失败: {ex.Message}");
            }
        }
    }

    private static string GetAppPath()
    {
        return Environment.ProcessPath ?? Path.Combine(
            AppContext.BaseDirectory, "TrueToneCap.exe");
    }
}
