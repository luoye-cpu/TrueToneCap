"""F3：让"双击第二个实例"至少产生一次用户看得见的反应。

实测定位（w9-second-instance-cell.ps1 + 把接收侧日志提到 Info 之后）：
  08:20:20.314 [App]       已有实例运行，激活窗口并退出      ← 第二实例
  08:20:20.320 [MainWindow] 收到单实例激活广播                ← 第一实例，6 ms 后
  但前台窗仍是别的进程 ⇒ 广播到了、ActivateFromOtherInstance 跑了（Restore/Show/
  MoveInZOrderAtTop/Activate 全做了），是 Windows 的前台锁把最后一步否了。

两处改动，各管一半：
  ① 发送侧 AllowSetForegroundWindow(ASFW_ANY)：这是文档给的正规解法——
     当前持有前台权的进程可以把它让出去。第二实例是被 Shell 拉起来的，此刻它有权让。
  ② 接收侧 FlashWindowEx(FLASHW_ALL|FLASHW_TIMERNOFG)：即便 ① 在某些系统配置下仍被拒，
     任务栏按钮也会闪到用户点它为止。"没抢到前台"和"什么都没发生"对用户是两回事。
"""
import io

APP = 'src/TrueToneCap.App/App.xaml.cs'
MAIN = 'src/TrueToneCap.App/MainWindow.xaml.cs'


def patch(path, old, new, count=1):
    s = io.open(path, encoding='utf-8', newline='').read()
    # 本仓 .cs 是 CRLF，而本文件里的三引号字面量是 LF ⇒ 不换算就永远 count=0
    # （上一版正是这么失败的，而且失败得"看起来像锚点错了"）。
    if '\r\n' in s:
        old = old.replace('\r\n', '\n').replace('\n', '\r\n')
        new = new.replace('\r\n', '\n').replace('\n', '\r\n')
    assert s.count(old) == count, (path, s.count(old))
    s = s.replace(old, new, count)
    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print('已改', path)


# ──  发送侧：广播之前先把前台权让出去 ──
old1 = """    private static void BroadcastActivateExistingInstance()
    {
        if (ActivateMessage == 0) return;
        SendMessageTimeout((nint)HWND_BROADCAST, ActivateMessage, 0, 0,
            SMTO_ABORTIFHUNG | SMTO_NORMAL, 1500, out _);
    }"""
new1 = """    private static void BroadcastActivateExistingInstance()
    {
        if (ActivateMessage == 0) return;
        // 本进程是被 Shell 拉起来的、此刻持有前台权；不让出去的话，接收侧的
        // Activate()/SetForegroundWindow() 会被系统前台锁直接拒绝（实测就是被拒：
        // 广播 6 ms 内就收到了，Restore/Show/MoveInZOrderAtTop/Activate 全跑了，前台仍是别的窗）。
        _ = AllowSetForegroundWindow(ASFW_ANY);
        SendMessageTimeout((nint)HWND_BROADCAST, ActivateMessage, 0, 0,
            SMTO_ABORTIFHUNG | SMTO_NORMAL, 1500, out _);
    }"""
patch(APP, old1, new1)

# P/Invoke 声明：挂在既有的 SendMessageTimeout 声明旁边
old2 = """    [DllImport("user32.dll", CharSet = CharSet.Unicode)
    ]
    private static extern uint RegisterWindowMessage(string lpString);"""
s = io.open(APP, encoding='utf-8', newline='').read()
anchor_candidates = [l for l in s.splitlines() if 'SendMessageTimeout(' in l and 'static extern' in l]
assert len(anchor_candidates) == 1, len(anchor_candidates)
decl = anchor_candidates[0]
patch(APP, decl,
      decl + '\r\n'
      + '    private const int ASFW_ANY = -1;\r\n'
      + '    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);',
      count=1)

# ── ② 接收侧：抢不到前台也要让任务栏闪 ──
old3 = """                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();"""
new3 = """                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();
                // 系统仍可能拒绝后台进程的前台切换（这条链在 Win11 上时有时无）。
                // 闪任务栏是文档允许的兜底：抢不到至少让用户知道该点哪里。
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var fi = new FLASHINFO { hWnd = hwnd, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uTimeout = 0 };
                _ = FlashWindowEx(ref fi);"""
patch(MAIN, old3, new3)

# 结构体与 P/Invoke 挂在 ActivateFromOtherInstance 上方
old4 = """    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。"""
new4 = """    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHINFO { public nint hWnd; public uint dwFlags; public uint uCount; public uint uTimeout; }
    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHINFO pwfi);

    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。"""
patch(MAIN, old4, new4)
