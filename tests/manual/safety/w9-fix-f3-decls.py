"""F3 收尾：按本仓风格（[LibraryImport] 源生成 P/Invoke，不是 [DllImport]）补上两个入口。

上一版失败的原因值得记：它拿 'SendMessageTimeout(' + 'static extern' 找锚点 ⇒ 0 命中，
因为本仓用的是 [LibraryImport] + private static partial，压根没有 DllImport。
用错风格的声明即使能编过，也会与同文件其余 P/Invoke 不一致。

注意：App.xaml.cs 里对 AllowSetForegroundWindow 的【调用】上一版已经落盘了，
所以此刻文件是"有调用、无声明"的坏状态 —— 本脚本必须把它补齐，否则编译不过。
"""
import io

APP = 'src/TrueToneCap.App/App.xaml.cs'
MAIN = 'src/TrueToneCap.App/MainWindow.xaml.cs'


def patch(path, old, new, must=True):
    s = io.open(path, encoding='utf-8', newline='').read()
    if '\r\n' in s:
        old = old.replace('\r\n', '\n').replace('\n', '\r\n')
        new = new.replace('\r\n', '\n').replace('\n', '\r\n')
    n = s.count(old)
    if must:
        assert n == 1, (path, n, old[:60])
    s = s.replace(old, new, 1)
    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print('已改', path)


# ── App.xaml.cs：AllowSetForegroundWindow 声明（调用已在上一版落盘） ──
a1 = """    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SendMessageTimeout(nint hWnd, uint msg, nuint wParam, nint lParam,
        uint fuFlags, uint uTimeout, out nint lpdwResult);"""
a2 = a1 + """
    // 第二个实例被 Shell 拉起时持有前台切换权；广播前把它让出去，否则接收侧的
    // Activate() 会被系统前台锁拒绝（实测：广播 6 ms 内就收到了，前台仍是别的窗）。
    private const int ASFW_ANY = -1;
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);"""
patch(APP, a1, a2)

# ── MainWindow.xaml.cs：FlashWindowEx 兜底 ──
m1 = """    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。"""
m2 = """    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHINFO
    {
        public nint hWnd;
        public uint dwFlags;
        public uint uCount;
        public uint uTimeout;
    }

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHINFO pwfi);

    /// <summary>响应第二个实例的激活广播：恢复并前置主窗口。"""
patch(MAIN, m1, m2)

m3 = """                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();"""
m4 = """                AppWindow.Show();
                AppWindow.MoveInZOrderAtTop();
                this.Activate();
                // 即使发送侧已经 AllowSetForegroundWindow，系统仍可能拒绝后台进程的前台切换。
                // 闪任务栏是文档允许的兜底：抢不到前台时至少让用户知道该点哪里 ——
                // "没抢到"和"什么都没发生"对用户是两回事。
                var fhwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var fi = new FLASHINFO
                {
                    hWnd = fhwnd,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uTimeout = 0,
                };
                _ = FlashWindowEx(ref fi);"""
patch(MAIN, m3, m4)
print('F3 两处声明与兜底已按 LibraryImport 风格补齐')
