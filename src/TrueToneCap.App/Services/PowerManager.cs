// TrueToneCap.App/Services/PowerManager.cs
// 电源管理 — 在捕获/编码期间阻止系统睡眠/屏幕关闭

using System.Runtime.InteropServices;

namespace TrueToneCap.App.Services;

/// <summary>电源管理辅助：在捕获/编码期间阻止系统进入睡眠状态。</summary>
public static partial class PowerManager
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private static readonly object s_lock = new();
    private static int s_refCount;

    /// <summary>阻止系统睡眠（可嵌套调用，内部引用计数）。</summary>
    public static void PreventSleep(bool preventDisplay = false)
    {
        lock (s_lock)
        {
            if (s_refCount == 0)
            {
                var flags = ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED;
                if (preventDisplay)
                    flags |= ExecutionState.ES_DISPLAY_REQUIRED;
                SetThreadExecutionState(flags);
            }
            s_refCount++;
        }
    }

    /// <summary>恢复系统睡眠许可（引用计数归零时恢复）。</summary>
    public static void AllowSleep()
    {
        lock (s_lock)
        {
            if (s_refCount > 0)
                s_refCount--;
            if (s_refCount == 0)
                SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
        }
    }
}