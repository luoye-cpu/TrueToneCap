// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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
                if (key?.GetValue(AppName) is not string val) return false;

                // ═══ 2026-09-15 修复：读写不对称 ═══
                // 写入（见下面的 set）是 "\"<path>\" --autostart" —— 带引号且带参数；
                // 而这里原先与【裸路径】严格相等 ⇒ 恒为 false。
                // 后果：用户启用自启后，UI 与日志仍显示"已禁用"，与实际状态相反，误导排查。
                // 现从命令行中还原出可执行文件路径再比较，两种写法（有无引号）都能处理。
                var exe = val;
                if (exe.StartsWith('"'))
                {
                    int end = exe.IndexOf('"', 1);
                    exe = end > 0 ? exe[1..end] : exe.Trim('"');
                }
                else
                {
                    int sp = exe.IndexOf(" --", StringComparison.Ordinal);
                    if (sp > 0) exe = exe[..sp];
                }
                return exe.Trim().Equals(GetAppPath(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LogService.Warn("StartupManager", $"读取注册表失败: {ex.Message}");
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
                LogService.Warn("StartupManager", $"写入注册表失败: {ex.Message}");
            }
        }
    }

    private static string GetAppPath()
    {
        return Environment.ProcessPath ?? Path.Combine(
            AppContext.BaseDirectory, "TrueToneCap.exe");
    }
}
