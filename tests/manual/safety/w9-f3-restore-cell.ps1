# F3 的第二版判据：不测"能不能抢到前台"，测"整条链有没有把窗口叫回来"。
#
# 上一版为什么只能记"未判"：它拿 GetForegroundWindow 当判据，而这台桌面上有第三方
# 全屏置顶窗（实测整轮前台窗从头到尾没变过，连夹具自己调 SetForegroundWindow 都没生效）
# ⇒ 分不清"产品没恢复窗口"和"夹具拿不到前台"。
#
# 这一版改成读【窗口自己的状态】：先最小化，再启第二实例，然后看第一实例的窗口
# 是不是从 minimized 变回非 minimized。ActivateFromOtherInstance 里那句
# `if (p.State == Minimized) p.Restore()` 正是这条链的产物，而 IsIconic 是纯状态查询，
# 不需要任何前台权限 ⇒ 第三方窗再横也污染不了。
#
# 局限要写清：这一格证明的是"广播→恢复"这条链通了，不证明"窗口一定跳到最前"
# （那是 Windows 前台锁的领地，本机不可判）。两件事分开记。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\f3-restore.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== F3 恢复判据（最小化→第二实例→是否 Restore）· $(Get-Date -Format 'HH:mm:ss') ===")

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RS
{
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public const int SW_MINIMIZE = 6;
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    // 只要主窗：WinUI 的顶层窗类名固定，托盘/覆盖层是别的类，按类名筛掉
    public static IntPtr MainWindowOf(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            if (ClassOf(h) != "WinUIDesktopWin32WindowClass") return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
if (-not ('RS' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$dir = Split-Path -Parent $Exe
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count -ne 1) { Say @("起手实例数 = $($pre.Count)（要求恰好 1）⇒ 未判"); exit 2 }
$first = $pre[0]
$mh = [RS]::MainWindowOf([uint32]$first.Id)
if ($mh -eq [IntPtr]::Zero) { Say @('★ 找不到主窗 hwnd ⇒ 未判'); exit 2 }
Say @("第一实例 PID=$($first.Id) 主窗=$mh")

try {
    $null = [RS]::ShowWindow($mh, [RS]::SW_MINIMIZE)
    Start-Sleep -Milliseconds 1500
    $b0 = [RS]::IsIconic($mh)
    Say @("最小化后 IsIconic = $b0")
    if (-not $b0) { Say @('★ 没最小化成功（可能被"最小化到托盘"策略拦掉）⇒ 这一格不成立，未判'); exit 2 }

    $p2 = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline -and -not $p2.HasExited) { Start-Sleep -Milliseconds 500 }
    Say @("第二实例：退出=$($p2.HasExited) 退出码=$(if($p2.HasExited){$p2.ExitCode}else{'—'})")

    # Restore 是异步的（DispatcherQueue.TryEnqueue），给它一点时间；但判据是状态变化，不是固定睡眠
    $restored = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not [RS]::IsIconic($mh)) { $restored = $true; Say @("第 $($i+1) 次轮询：IsIconic 变 False ⇒ 窗口被恢复"); break }
    }
    if (-not $restored) { Say @('★ 10 s 内 IsIconic 始终 True ⇒ 广播没导致恢复，这条链确实断了') }

    $now = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
    Say @("现在实例数 = $($now.Count)")
    Say @("★ 判词：恢复=$restored ⇒ 证明的是【广播→Restore 这条链通】；")
    Say @('   "窗口是否跳到最前"归 Windows 前台锁管，本机有第三方全屏窗、不可判，两件事分开记。')
    if ($restored) { exit 0 } else { exit 1 }
} finally {
    # 别把用户的窗口留在最小化状态
    try { if ([RS]::IsIconic($mh)) { $null = [RS]::ShowWindow($mh, 9) } } catch { }   # SW_RESTORE
}
