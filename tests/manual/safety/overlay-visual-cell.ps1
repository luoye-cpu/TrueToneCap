# 覆盖层"看起来变了"的最后一半读数（报告 §23.6 机制 A、任务 #12）
# 两件一次做掉：
#   ① EnumWindows 按 z 序列出该 PID 的全部顶层窗（标题/矩形/可见性）⇒ 直接数"叠了几个背景窗"，
#      不再靠"失败了 29 次"去推"应该有 29 个窗"。
#   ② 对每个窗 PrintWindow(PW_RENDERFULLCONTENT=2) 抓成 PNG（不抢焦点、不动光标），
#      然后算近黑像素占比与亮度均值 ⇒ "没有画面/黑底"这句话变成一个数。
# 需要真鼠标拖一次让选区成形（用户已授权）；结束只杀自己起的 PID、还原设置并核 sha1。
# ⚠ 退役说明（2026-10-01 报告 §40）：本格有几条腿依赖覆盖层 GPU 背景那批字面量
#   （`GPU 呈现抛出异常` / `跳过呈现，一帧未出` / `背景渲染 … GPU=False` / 窗标题 `HDR Preview`）。
#   B 档把那条不上屏的 GPU 供帧路整条删除后，这些字面量【不再有生产者】——读到 0 是预期，
#   不是产品失败。现役口径改由 preview-content-cell.ps1 的 C-LegacyOverlay 臂 + 闩锁判据承担。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'overlay-visual-cell.log',
    [int]$SettleSec = 12,
    [switch]$ForceSdr,
    # 造 GPU 失败腿（报告 §25.9② 那笔"未验"）：先按【无感截图】热键让 WGC 回读占住共享 D3D 锁
    # （本机实测回读 522 ms > PresentFrame 里 TryEnter 的 500 ms），紧接着按【选区】热键开覆盖层
    # ⇒ 覆盖层那一次呈现应当撞在锁超时上 ⇒ GPU背景=False ⇒ CPU 兜底跑到。
    # ⚠ 这一下按下去撞不撞得上是赌的，所以【只有日志真出现"锁超时/一帧未出"那一行才允许下结论】，
    #   没出现就记未判（RC=2），不许把"没撞坏"读成"失败腿也没问题"。
    [switch]$ContendGpu,
    [int]$ContendGapMs = 120
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log = Join-Path $repo (Join-Path 'tests\output' $LogName)
$shotDir = Join-Path $repo 'tests\output\overlay-shots'
$outDir = Join-Path $repo 'tests\output\t3-cells'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$bak = Join-Path $dir 'TrueToneCap.settings.json.ttc-visbak'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
if (-not (Test-Path -LiteralPath $set)) { Fail "没有 $set" }
if (Test-Path -LiteralPath $bak) { Fail "遗留 $bak" }
foreach ($d in @($shotDir, $outDir)) { if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null } }
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 覆盖层视觉取证 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WinDump
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, [Out] char[] s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER {
        public uint size; public int w, h; public ushort planes, bitCount; public uint comp;
        public uint sizeImage; public int xppm, yppm; public uint clrUsed, clrImportant; }

    public class Row { public IntPtr H; public string Title = ""; public RECT Rect; public bool Vis; public int Z; }

    /// 按 z 序（从上到下）列出属于该进程的所有【真·顶层】窗（GetAncestor(GA_ROOTOWNER)==自身）
    public static List<Row> TopByZ(uint targetPid) {
        var list = new List<Row>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid && GetAncestor(h, 2) == h) {
                var r = new Row { H = h, Z = list.Count, Vis = IsWindowVisible(h) };
                var buf = new char[512];
                int n = GetWindowTextW(h, buf, buf.Length);
                r.Title = n > 0 ? new string(buf, 0, n) : "";
                GetWindowRect(h, out r.Rect);
                list.Add(r);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// PrintWindow + PW_RENDERFULLCONTENT(2)：DWM 组合内容也能抓到，且不需要窗口聚焦。
    /// ⚠ 这里【故意不碰 System.Drawing】：Add-Type 默认不引用它，写了就是编译失败（本轮实测）。
    ///   只返回 HBITMAP，转 PNG 交给 PowerShell 那一侧做；用完请 FreeGdi。
    public static IntPtr MakeBitmap(IntPtr hWin, out int w, out int hh) {
        w = 0; hh = 0;
        RECT r; if (!GetWindowRect(hWin, out r)) return IntPtr.Zero;
        w = r.R - r.L; hh = r.B - r.T;
        if (w <= 0 || hh <= 0) return IntPtr.Zero;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, hh);
        IntPtr old = SelectObject(mem, bmp);
        PrintWindow(hWin, mem, 2);
        SelectObject(mem, old);
        DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return bmp;
    }
    public static void FreeGdi(IntPtr o) { DeleteObject(o); }
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr d, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr d, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr d);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr d);
}
'@
if (-not ('WinDump' -as [type])) { Fail 'Add-Type 失败：WinDump 不存在 ⇒ 不给结论' }
Add-Type -AssemblyName System.Drawing
$Error.Clear()   # 让下一个 Add-Type 的编译错误能被原样抓到

function Stat-Png([string]$p) {
    # 采样 64x64 网格算近黑占比与均值亮度：抓出来是"黑底"还是"有桌面内容"，一个数说清
    $bmp = New-Object System.Drawing.Bitmap($p)
    $step_x = [math]::Max(1, [int]($bmp.Width / 64)); $step_y = [math]::Max(1, [int]($bmp.Height / 64))
    $n = 0; $dark = 0; $sum = 0L
    for ($y = 0; $y -lt $bmp.Height; $y += $step_y) {
        for ($x = 0; $x -lt $bmp.Width; $x += $step_x) {
            $c = $bmp.GetPixel($x, $y)
            $lum = [int]($c.R * 0.299 + $c.G * 0.587 + $c.B * 0.114)
            $n++; $sum += $lum; if ($lum -le 8) { $dark++ }
        }
    }
    $bmp.Dispose()
    return [pscustomobject]@{ Samples = $n; DarkPct = [math]::Round(100.0 * $dark / $n, 1); MeanLum = [math]::Round($sum / $n, 1) }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Ptr
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int cb);
    // ⚠ 不能用 C# 6 的表达式体成员（=>）：PowerShell 5.1 的 Add-Type 走框架级 C# 编译器，
    //   只到 C# 5，写 `=>` 会只回一句 "Cannot add type. Compilation errors occurred."（本轮实测）。
    static INPUT Mk(ushort vk, bool up)
    {
        INPUT i; i.type = 1;
        i.ki = new KEYBDINPUT();
        i.ki.wVk = vk;
        i.ki.dwFlags = up ? 2u : 0u;
        return i;
    }
    public static bool Chord(uint mods, ushort vk) {
        var l = new System.Collections.Generic.List<INPUT>();
        if ((mods & 2) != 0) l.Add(Mk(0x11, false));
        if ((mods & 4) != 0) l.Add(Mk(0x10, false));
        l.Add(Mk(vk, false)); l.Add(Mk(vk, true));
        if ((mods & 4) != 0) l.Add(Mk(0x10, true));
        if ((mods & 2) != 0) l.Add(Mk(0x11, true));
        var a = l.ToArray();
        return SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))) == (uint)a.Length;
    }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    // mouse_event 的最后一个参数是 DWORD_PTR ⇒ 这里必须是 UIntPtr，和下面传的 UIntPtr.Zero 同型。
    // （原先声明成 IntPtr 却传 UIntPtr.Zero：CS1503，而 PS 5.1 的 Add-Type 不把 CS 编号放进异常消息，
    //   只回一句 "Cannot add type. Compilation errors occurred."，要 2>&1 才看得见——已踩实。）
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanW(char ch);
    // ⚠ 上一版声明成 (string ch)：Win32 要的是 WCHAR 值本身，不是指针 ⇒ 传字符串永远返回 -1，
    //   掩码后得到 vk=255，热键按出去没有任何反应——于是整轮量到 0 条打点。签名错比返回错更难查。
}
'@
$whyPtr = '<无错误记录>'
if ($Error.Count) {
    $ed = $Error[0].ErrorDetails.Message
    if (-not $ed) { $ed = $Error[0].Exception.Message }
    $whyPtr = $ed
}
if (-not ('Ptr' -as [type])) { Fail ('Add-Type 失败：Ptr 不存在 ⇒ 不给结论。编译器原话：' + $whyPtr) }

# 读设置里的实际热键，解析成 mods+vk（不写死 Ctrl+Shift+S：那正是本轮要防的"抄常数"）
$obj0 = [IO.File]::ReadAllText($set) | ConvertFrom-Json
$hotkey = [string]$obj0.Hotkey; if (-not $hotkey) { $hotkey = 'Ctrl+Shift+S' }
$mods = 0
$key = ($hotkey -split '\+' | ForEach-Object { $_.Trim() }) | Where-Object { $_ -notin @('Ctrl','Control','Shift','Alt','Win') }
foreach ($m in ($hotkey -split '\+')) {
    switch ($m.Trim().ToUpperInvariant()) {
        'CTRL' { $mods = $mods -bor 2 } 'CONTROL' { $mods = $mods -bor 2 }
        'SHIFT' { $mods = $mods -bor 4 } 'ALT' { $mods = $mods -bor 1 }
    }
}
$vkRaw = [Ptr]::VkKeyScanW([char]$key[0])
if ($vkRaw -lt 0) { Fail "VkKeyScanW('$($key[0])') 返回 $vkRaw ⇒ 解析不出虚拟键码，不给结论" }
$vk = $vkRaw -band 0xFF
Say @("热键取自设置：$hotkey ⇒ mods=$mods vk=$vk（不写死常数）")

$proc = $null
$held = $false
try {
    $sha0 = Sha $set
    Move-Item -LiteralPath $set -Destination $bak
    $j = [IO.File]::ReadAllText($bak) | ConvertFrom-Json
    $obj = [ordered]@{}
    foreach ($p in $j.PSObject.Properties) { $obj[$p.Name] = $p.Value }
    $obj['OutputPath'] = $outDir
    $obj['SettingsRevision'] = 2
    ($obj | ConvertTo-Json -Compress) | Set-Content -LiteralPath $set -NoNewline -Encoding UTF8
    Say @("轮前设置 sha1: $sha0（已临时把产物目录改到仓库内）")

    $logMark = 0
    if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }

    # 关键一步：自己占住这个热键，产品注册必然失败 ⇒ 走回退档；我们仍按【回退后真正生效的那条】触发。
    # 但更简单也更可靠的是：不占，直接用产品日志里"已注册"的那一条来按。
    if ($ForceSdr) { $env:TTC_FORCE_SDR = '1'; Say @('已设 TTC_FORCE_SDR=1') }
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("已起真身 PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec

    $all = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
    $live = @($all | Where-Object { $_ -match '已注册:' } | ForEach-Object {
        if ($_ -match '已注册:\s*(.+?)\s*\(id=') { $Matches[1].Trim() } })
    Say @("产品自报已注册: $($live -join ' | ')")

    # 触发：按产品实际注册的那条捕获热键（含 S 的那条），不是我以为的默认值
    $cap = @($live | Where-Object { $_ -match 'S$' } | Select-Object -Last 1)
    if ($cap) {
        $cm = 0; $ck = ''
        foreach ($m in ($cap -split '\+')) {
            switch ($m.Trim().ToUpperInvariant()) {
                'CTRL' { $cm = $cm -bor 2 } 'SHIFT' { $cm = $cm -bor 4 } 'ALT' { $cm = $cm -bor 1 }
                default { $ck = $m.Trim() }
            }
        }
        $cvkRaw = [Ptr]::VkKeyScanW([char]$ck[0])
        if ($cvkRaw -lt 0) { Fail "VkKeyScanW('$($ck[0])') 返回 $cvkRaw ⇒ 键码解析失败，不按键、不给结论" }
        $cvk = $cvkRaw -band 0xFF
        if ($ContendGpu) {
            # 先按"另一个"已注册热键（无感截图那条）把共享 D3D 锁占住，再开覆盖层
            $other = @($live | Where-Object { $_ -notmatch 'S$' } | Select-Object -Last 1)
            if ($other) {
                $om = 0; $ok = ''
                foreach ($m in ($other -split '\+')) {
                    switch ($m.Trim().ToUpperInvariant()) {
                        'CTRL' { $om = $om -bor 2 } 'SHIFT' { $om = $om -bor 4 } 'ALT' { $om = $om -bor 1 }
                        default { $ok = $m.Trim() }
                    }
                }
                $ovkRaw = [Ptr]::VkKeyScanW([char]$ok[0])
                if ($ovkRaw -ge 0) {
                    Say @("GPU 失败腿：先按「$other」占共享 D3D 锁，等 $ContendGapMs ms 再开覆盖层（撞不撞得上是赌的，判据见最后一行）")
                    $null = [Ptr]::Chord($om, ($ovkRaw -band 0xFF))
                    Start-Sleep -Milliseconds $ContendGapMs
                } else { Say @("GPU 失败腿：「$other」解析不出键码 ⇒ 这一腿记未判") }
            } else { Say @("GPU 失败腿：日志里没有第二条已注册热键 ⇒ 这一腿记未判") }
        }
        Say @("按实际注册的「$cap」(mods=$cm vk=$cvk) 唤起覆盖层…")
        $null = [Ptr]::Chord($cm, $cvk)
    } else { Say @('⏭ 日志里没有可识别的捕获热键 ⇒ 不按键，后面按【未判】处理') }
    Start-Sleep -Seconds 3

    # 下键之前先确认覆盖层真的起来了：上一轮 vk 解析错、热键打空，我却照样拖了鼠标——
    # 那一下点到的是用户的桌面，不是产品窗口。任何注入都要先证明目标窗在接事件。
    $seenOverlay = $false
    if (Test-Path -LiteralPath $appLog) {
        $t0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
        $seenOverlay = @($t0 | Where-Object { $_.Contains('进入背景创建') -or $_.Contains('覆盖层就绪') }).Count -gt 0
    }
    if (-not $seenOverlay) {
        # -ContendGpu 这一档的【预期结果之一】就是覆盖层压根没起来（被防重入闸拦下），
        # 那不是失败，是被测行为。所以这里不 Fail，只把拖拽跳掉——绝不往桌面上点第二下。
        if ($ContendGpu) { Say @('覆盖层没起来（防重入闸拦下了第二次按键）⇒ 跳过拖拽，只判通知语义与失败腿') }
        else { Fail '按了热键但日志里没有覆盖层任何一行 ⇒ 拒绝拖拽（不往桌面上点），不给结论' }
    }

    # 真拖一次，让选区成形（用例 A/B 之前量到过工具条，需要这个状态才有背景窗可数）
    if ($seenOverlay) {
    $null = [Ptr]::SetCursorPos(400, 300); Start-Sleep -Milliseconds 250
    [Ptr]::LeftDown()
    $null = [Ptr]::SetCursorPos(1500, 900); Start-Sleep -Milliseconds 200
    $null = [Ptr]::SetCursorPos(2300, 1400); Start-Sleep -Milliseconds 200
    [Ptr]::LeftUp()
    } else { Say @('（未拖拽：目标窗没在接事件，任何注入都不做）') }
    Start-Sleep -Seconds 3

    $rows = [WinDump]::TopByZ([uint32]$proc.Id)
    Say @("--- 该 PID 的顶层窗（按 z 序，0 = 最上）共 $($rows.Count) 个 ---")
    foreach ($r in $rows) {
        $w = $r.Rect.R - $r.Rect.L; $h2 = $r.Rect.B - $r.Rect.T
        Say @("  z=$($r.Z) 可见=$($r.Vis) ${w}x${h2} @$($r.Rect.L),$($r.Rect.T) 标题=[$($r.Title)] hwnd=$($r.H)")
    }
    Say @("--- 逐个 PrintWindow(PW_RENDERFULLCONTENT) 抓图并算近黑占比 ---")
    foreach ($r in $rows) {
        $p = Join-Path $shotDir ("z$($r.Z)_$($proc.Id).png")
        $bw = 0; $bh = 0
        $hb = [WinDump]::MakeBitmap($r.H, [ref]$bw, [ref]$bh)
        if ($hb -ne [IntPtr]::Zero) {
            try {
                $img = [System.Drawing.Image]::FromHbitmap($hb)
                $img.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
                $img.Dispose()
                $s = Stat-Png $p
                Say @("  z=$($r.Z) [$($r.Title)] ${bw}x${bh} 近黑 $($s.DarkPct)% 均值亮度 $($s.MeanLum) 采样 $($s.Samples) ⇒ $p")
            } catch { Say @("  z=$($r.Z) [$($r.Title)] 存图失败：$($_.Exception.Message)") }
            [WinDump]::FreeGdi($hb)
        } else {
            Say @("  z=$($r.Z) [$($r.Title)] 抓不到客户区（无有效尺寸，或该窗没有重定向位图）")
        }
    }

    $all2 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
    $ids = @($all2 | Where-Object { $_ -match '实例#(\d+)' } | ForEach-Object {
        if ($_ -match '实例#(\d+)') { $Matches[1] } } | Sort-Object -Unique)
    Say @("打点读数：本次范围内出现过的【实例号】= $($ids -join ', ')（共 $($ids.Count) 个）")
    foreach ($k in @('进入背景创建', '背景已就绪', '覆盖层就绪', 'GPU 呈现抛出异常', 'GPU 呈现失败',
                     'GPU 锁超时', '跳过呈现，一帧未出', '背景渲染')) {
        # ⚠ 别在这句里加"顺手"的过滤条件：上一版我写了个 `$_ -match (Get-Date).ToString('yyyy')`，
        #   而日志行里根本没有年份 ⇒ 每一项都会被筛成 0，读数看着像"什么都没发生"。
        $c = @($all2 | Where-Object { $_.Contains($k) }).Count
        Say @("  「$k」 $c 条")
    }
    # ── 通知语义判据：被防重入闸拦下的那一发【不是失败】，不许发红色"截图失败"通知 ──
    # （❌ 由码点拼：这些 .ps1 是无 BOM 的，字面量在有的机器上会被按 ANSI 读成乱码）
    $cross = [string][char]0x274C
    $falseFail = @($all2 | Where-Object { $_.Contains('已提交系统通知: ' + $cross) -and $_.Contains('截图失败') }).Count
    $busyIgnored = @($all2 | Where-Object { $_.Contains('忽略重复触发') }).Count
    $verdictBusy = if ($busyIgnored -eq 0) { '未判（这一跑没走到防重入闸）' }
                   elseif ($falseFail -eq 0) { 'PASS（拦下≠失败）' }
                   else { 'RED（被忽略的重复触发仍被报成失败通知）' }
    Say @("通知语义：防重入拦下 $busyIgnored 次，其间「$cross 截图失败」通知 $falseFail 条 ⇒ $verdictBusy")
    $busyRed = ($busyIgnored -gt 0 -and $falseFail -gt 0)   # 攒着，最后并入 rc（$rc 在下面才初始化）

    # ── GPU 失败腿的判决：撞上了才判，没撞上只记未判 ──
    $hdrWnds = @($rows | Where-Object { $_.Title -eq 'HDR Preview' -and $_.Vis }).Count
    $contend = @($all2 | Where-Object { $_.Contains('GPU 锁超时') -or $_.Contains('跳过呈现，一帧未出') }).Count
    $cpuLeg  = @($all2 | Where-Object { $_.Contains('背景渲染') -and $_.Contains('GPU=False') }).Count
    $entryN  = @($all2 | Where-Object { $_.Contains('进入背景创建') }).Count
    Say @("失败腿读数：撞锁/一帧未出 = $contend 条｜CPU 兜底真跑到(背景渲染 GPU=False) = $cpuLeg 条｜进入背景创建 = $entryN 条")
    Say @("   ⓘ 前两条自 2026-10-01 B 档起【无生产者】（GPU 背景分支已删），读到 0 属预期，不得读成产品失败；见文件头退役说明")
    $rc = 0
    if ($busyRed) { $rc = 1 }
    if ($ContendGpu) {
        if ($contend -eq 0) {
            $rc = 2
            Say @('失败腿 ⇒ 【未判】：这一跑没撞上锁超时 ⇒ 既不能说修复无效，也不能说失败腿验过。换时刻/加间隔再试')
        } elseif ($entryN -eq 1 -and $cpuLeg -ge 1 -and $hdrWnds -le 1) {
            Say @('失败腿 ⇒ PASS：呈现确实失败（撞锁），而进入背景创建仍 1 条、CPU 兜底跑到、可见背景窗 ≤1 ⇒ 修复不依赖 GPU 健康')
        } else {
            $rc = 1
            Say @("失败腿 ⇒ RED：撞锁 $contend 条，但 进入=$entryN（应 1）/ CPU 兜底=$cpuLeg（应≥1）/ 可见背景窗=$hdrWnds（应≤1）")
        }
    }
    Say @("RC=$rc WINDOWS=$($rows.Count) HDR_PREVIEW可见窗=$hdrWnds INSTANCES=$($ids.Count) 撞锁=$contend 兜底=$cpuLeg PID=$($proc.Id)")
    Write-Output "RC=$rc WINDOWS=$($rows.Count) HDR_PREVIEW=$hdrWnds INSTANCES=$($ids.Count) 撞锁=$contend 兜底=$cpuLeg"
} finally {
    if ($ForceSdr) { Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue }
    if ($null -ne $proc) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("已停止本次起的 PID=$($proc.Id)") }
        catch { Say @("❌ 停止失败：$($_.Exception.Message)") }
    }
    if (Test-Path -LiteralPath $bak) {
        if (Test-Path -LiteralPath $set) { Remove-Item -LiteralPath $set -Force }
        Move-Item -LiteralPath $bak -Destination $set
        $shaEnd = Sha $set
        Say @("还原自检：原件移回 sha1=$shaEnd ⇒ $(if ($shaEnd -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同：立即核对' })")
    } else { Say @('❌ 备份件不见了 ⇒ 立即人工复查 ' + $bak) }
}
