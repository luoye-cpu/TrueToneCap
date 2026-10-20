// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/StartupManager.cs
// 管理 Windows 开机自启动。
//
// ═══ 两种运行形态用两套机制（2026-10-07 补齐打包形态）═══
//   · 未打包（绿色包）：写 HKCU\...\Run 注册表项 + 启动参数 --autostart（本文件原有实现）。
//   · 已打包（MSIX）：走清单里声明的 <uap5:Extension Category="windows.startupTask"> +
//     Windows.ApplicationModel.StartupTask API。
// ⚠️ 为什么打包形态【不能】继续用注册表：
//     MSIX 下对 HKCU 的写入会被**重定向到包专属 hive**（官方："All writes under HKCU are
//     copied on write to a private per-user, per-app location."）⇒ 那条 Run 项**对系统启动无效**。
//     症状最具欺骗性：界面开关看起来能勾、注册表里也能读到值，但**重启后不会自动启动** ——
//     用户与开发者都会以为功能是好的。
//   清单里早就声明了 startupTask（见 Package.appxmanifest），但代码一直没接这个 API，
//   于是打包形态的自启是"声明了但没实现"。
//
// ═══ 为什么用【直接调用】而不是反射（2026-10-07 修正，我先前写错过一次）═══
// 我最初用反射调 StartupTask（理由：不想让未打包形态沾 WinRT）。实测证明那个理由不成立，
// 而且那版反射有**三个真 bug**，全部指向同一个后果 —— 打包形态自启永远不生效且只留一行 Warn：
//   ① AcceptTask 的 MakeGenericMethod(typeof(int)) 对两个不同的返回类型是错的
//      （GetAsync 返回 IAsyncOperation<StartupTask>，RequestEnableAsync 返回
//       IAsyncOperation<StartupTaskState>），传错类型 ⇒ Invoke 抛 ArgumentException；
//   ② 对**开放泛型**参数类型调 IsAssignableFrom 恒为 false ⇒ 循环里每个 AsTask 重载都被跳过；
//   ③ 于是 AsTask 永远返回 null，所有调用提前 return。
// 正确做法：本工程是 net11.0-windows10.0.26100.0，**WinRT 投影本来就在编译期可用**
// （实测：`using System;` 之后 `await StartupTask.GetAsync(...)` 直接编译通过，无需额外包引用）。
// ⇒ 直接用强类型 API：编译期就能查出签名错误，且未打包形态根本不会执行这些语句
//   （两条分支由 PackageRuntime.IsPackaged 隔开），所以"不想沾 WinRT"这个顾虑本来就不存在。
// 教训记在这里：**能用编译期类型就别用反射** —— 反射把这些错误从编译期推迟到了运行时，
// 而运行时那条路径只有装机后才会走到。

using System;                      // AsTask 扩展方法（WindowsRuntimeSystemExtensions）在此命名空间
using Microsoft.Win32;
using Windows.ApplicationModel;    // StartupTask / StartupTaskState

namespace TrueToneCap.App.Services;

public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TrueToneCap";

    /// <summary>清单中声明的 StartupTask Id（必须与 Package.appxmanifest 逐字一致）。</summary>
    public const string StartupTaskId = "TrueToneCapStartup";

    /// <summary>打包形态（MSIX）判定 —— 决定用 StartupTask 还是注册表。</summary>
    private static bool Packaged => TrueToneCap.Core.Settings.PackageRuntime.IsPackaged;

    /// <summary>
    /// 是否已启用开机自启。
    /// <para>打包形态读 <c>StartupTask.State</c>；未打包形态读注册表 Run 项。</para>
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            if (Packaged) return IsEnabledPackaged();
            return IsEnabledRegistry();
        }
        set
        {
            if (Packaged) SetEnabledPackaged(value);
            else SetEnabledRegistry(value);
        }
    }

    // ═══════════════════════════════════════
    //  未打包形态：注册表 Run（原实现，行为不变）
    // ═══════════════════════════════════════

    private static bool IsEnabledRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(AppName) is not string val) return false;

            // ═══ 2026-09-15 修复：读写不对称 ═══
            // 写入（见 SetEnabledRegistry）是 "\"<path>\" --autostart" —— 带引号且带参数；
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

    private static void SetEnabledRegistry(bool value)
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

    private static string GetAppPath()
    {
        return Environment.ProcessPath ?? Path.Combine(
            AppContext.BaseDirectory, "TrueToneCap.exe");
    }

    // ═══════════════════════════════════════
    //  打包形态：StartupTask（MSIX）
    // ═══════════════════════════════════════
    // 全部用强类型 API —— 见文件头"为什么用直接调用而不是反射"。

    /// <summary>
    /// 读取打包形态的自启状态。
    /// <para>StartupTaskState: Disabled=0, DisabledByUser=1, Enabled=2, DisabledByPolicy=3。</para>
    /// </summary>
    private static bool IsEnabledPackaged()
    {
        try
        {
            var startupTask = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (startupTask is null) return false;

            int s = (int)startupTask.State;
            LogService.Info("StartupManager",
                $"StartupTask 状态={s}（0=Disabled 1=DisabledByUser 2=Enabled 3=DisabledByPolicy）");
            return s == 2;
        }
        catch (Exception ex)
        {
            LogService.Warn("StartupManager", $"读取 StartupTask 状态失败: {ex.Message}");
            return false;
        }
    }

    private static void SetEnabledPackaged(bool value)
    {
        try
        {
            var startupTask = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (startupTask is null)
            {
                LogService.Warn("StartupManager", "StartupTask 实例为空 ⇒ 开机自启不可用");
                return;
            }

            if (value)
            {
                // ⚠️ 与注册表形态的关键差异：这里可能【被系统拒绝】。
                //    用户若在「设置 → 应用 → 启动」里关掉过，会返回 DisabledByUser，
                //    且 API 不会替你改回来（系统有意的保护）—— 必须把结果如实报出来，
                //    否则界面显示"已启用"而实际没启用，正是本文件最该避免的那种谎报。
                var finalState = startupTask.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                int s = (int)finalState;
                if (s == 2)
                {
                    LogService.Info("StartupManager", "开机自启已启用（StartupTask）");
                }
                else
                {
                    LogService.Warn("StartupManager",
                        $"开机自启未能启用（StartupTaskState={s}）；"
                        + "若为 1(DisabledByUser) 需用户到「设置 → 应用 → 启动」手动打开");
                    // 如实告知界面：IsEnabled 下一次读取会返回 false，不谎报成功
                }
            }
            else
            {
                startupTask.Disable();
                LogService.Info("StartupManager", "开机自启已禁用（StartupTask）");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("StartupManager", $"设置 StartupTask 失败: {ex.Message}");
        }
    }
}

