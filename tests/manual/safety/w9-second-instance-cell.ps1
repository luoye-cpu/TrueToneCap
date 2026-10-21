# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 用户已经开着 TrueToneCap 时再双击一次，最后到底能不能看到已有窗口被带到前面？
#
# 为什么单独量这一条：代码侧机制是接好的（App.xaml.cs:124 抢 Mutex 失败 → :312 SendMessageTimeout
# 广播 → MainWindow.xaml.cs:3131 收到 → :3166 若最小化则 Restore → :3171 Activate）。
# 但"接好了"不等于"Windows 允许"：一个【没有前台权限的后台进程】调用 SetForegroundWindow/Activate
# 通常会被系统的前台锁拒绝，只闪一下任务栏。这条只能实测。
#
# 上一版判错的坑：第二个进程要等广播跑完（HWND_BROADCAST 的 1500ms 超时是【每个窗口】的，
# 桌面上窗口多时总耗时能到十几秒）才退出。我按固定 8 s 等，读到的"前台不是旧窗"其实是采得太早。
# 这一版改成【轮询到它真退出】再读前台。
$ErrorActionPreference = 'Continue'
$d = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FG2
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static IntPtr FirstWindowOfOtherApp(uint skipPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == skipPid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
            if (txt.Length > 0 && cls.ToString() != "Tooltip") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string TitleOf(IntPtr h) { var t = new StringBuilder(256); GetWindowText(h, t, 256); var c = new StringBuilder(256); GetClassName(h, c, 256); return string.Format("「{0}」<{1}>", t, c); }
}
'@
$mine = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
Write-Output ("起手实例数 = $($mine.Count)")
if ($mine.Count -ne 1) { Write-Output '★ 不是恰好 1 个实例 ⇒ 这一格不可判（先恢复单实例再测）'; exit 2 }
$old = $mine[0]
Write-Output "旧实例 PID=$($old.Id) 主窗=$($old.MainWindowHandle)"

# 先把前台切到一个确定不是本应用的窗口（资源管理器），否则"被带到前台"与"本来就在前台"分不开
$other = [FG2]::FirstWindowOfOtherApp([uint32]$old.Id)
if ($other -eq [IntPtr]::Zero) { Write-Output '★ 找不到别的可见窗做对照 ⇒ 未判'; exit 2 }
$null = [FG2]::SetForegroundWindow($other)
Start-Sleep -Milliseconds 700
$mid = [FG2]::GetForegroundWindow()
Write-Output "前台已切到 $($mid) = $([FG2]::TitleOf($mid))；等于旧主窗？$($mid -eq $old.MainWindowHandle)"
if ($mid -eq $old.MainWindowHandle) { Write-Output '★ 切前台失败 ⇒ 这一格不成立'; exit 2 }

$p2 = Start-Process -FilePath (Join-Path $d 'TrueToneCap.exe') -PassThru -WorkingDirectory $d
# 轮询到第二个进程真退出（上限 40 s），退出【之后】再读前台
$deadline = (Get-Date).AddSeconds(40); $waited = 0
while ((Get-Date) -lt $deadline) { try { $p2.Refresh() } catch { }
    if ($p2.HasExited) { break }
    Start-Sleep -Milliseconds 500; $waited += 0.5 }
Write-Output ("第二个进程：退出=$($p2.HasExited) 等待≈$($waited)s 退出码=$(if($p2.HasExited){$p2.ExitCode}else{'—'})")
Start-Sleep -Milliseconds 1500
$now = [FG2]::GetForegroundWindow()
$alive = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
Write-Output ("现在实例数 = $($alive.Count)")
Write-Output ("现在前台窗 = $($now) = $([FG2]::TitleOf($now))")
$ok = ($alive.Count -eq 1) -and ($now -eq $old.MainWindowHandle)
Write-Output "★ 判词：实例数回到 1 且 前台=旧实例主窗 ⇒ 双击第二个能唤回窗口；否则用户看不到任何反应"
Write-Output "   成立？$ok"
