# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 批次 T3 剩下两格：热键冲突回退链 + 无感截图到剪贴板（2026-09-30，用户授权注键鼠）
#
# 两格合跑一趟的理由：回退链不需要注键鼠就能判（本进程抢下两个默认组合，产品首选必然失败，
# 它实际拿到哪一条会写进日志 HotkeyManager:118/128）；而"按一次"要用的是【它实际拿到的那一条】，
# 所以必须先看日志再按键。
# 三条自检：
#   ① 占用要先证明抢得到（抢不到 = 冲突来自别的程序，这一格的对照就不成立）；
#   ② 反证要带对照臂：拿一个没人用的 Ctrl+Shift+Y 注册，必须【成功】——
#      否则"再注册一次失败了"这句话在系统层面可能是恒真的，等于没判；
#   ③ 收尾只杀我自己起的 PID、释放我占的组合、还原设置并核 sha1；产物只落仓库内目录。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [string]$LogName = 'hotkey-clipboard-cell.log',
    [int]$SettleSec = 14,
    [int]$WaitFileSec = 20
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$log = Join-Path $repo (Join-Path 'tests\output' $LogName)
$outDir = Join-Path $repo 'tests\output\t3-cells'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA1).Hash } else { 'ABSENT' } }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$bak = Join-Path $dir 'TrueToneCap.settings.json.ttc-hkbak'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
if (-not (Test-Path -LiteralPath $set)) { Fail "没有 $set" }
if (Test-Path -LiteralPath $bak) { Fail "上一轮遗留 $bak ⇒ 先人工核对" }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 热键冲突 + 剪贴板格 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Win32Hotkey
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ⚠ INPUT 在 x64 上必须是 40 字节（type 4 + 填充 4 + 联合 32，联合按最大的 MOUSEINPUT 算）。
    //   先前按 KEYBDINPUT 直接跟在 type 后面声明 ⇒ Marshal.SizeOf 得到 28，SendInput 直接返回 0 个事件
    //   （= 一个都没送出去），差点被读成"产品没响应热键"。用 Explicit + Size 钉死布局。
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static INPUT Key(ushort vk, bool up)
    {
        var i = new INPUT { type = 1 };
        i.ki.wVk = vk;
        i.ki.dwFlags = up ? 0x0002u : 0u;   // KEYEVENTF_KEYUP
        return i;
    }

    /// 送出一组和键（mods 用 Win32 的 MOD_ALT=1 / MOD_CONTROL=2 / MOD_SHIFT=4 / MOD_WIN=8）。
    /// 返回真正被系统接收的事件数（调用方要拿它跟事件总数比，别只判 true/false）。
    public static uint Chord(uint mods, uint vk)
    {
        var list = new List<INPUT>();
        if ((mods & 0x0002) != 0) list.Add(Key(0x11, false));   // VK_CONTROL
        if ((mods & 0x0001) != 0) list.Add(Key(0x12, false));   // VK_MENU
        if ((mods & 0x0004) != 0) list.Add(Key(0x10, false));   // VK_SHIFT
        if ((mods & 0x0008) != 0) list.Add(Key(0x5B, false));   // VK_LWIN
        list.Add(Key((ushort)vk, false));
        list.Add(Key((ushort)vk, true));
        if ((mods & 0x0008) != 0) list.Add(Key(0x5B, true));
        if ((mods & 0x0004) != 0) list.Add(Key(0x10, true));
        if ((mods & 0x0001) != 0) list.Add(Key(0x12, true));
        if ((mods & 0x0002) != 0) list.Add(Key(0x11, true));
        var arr = list.ToArray();
        uint n = SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
        if (n != (uint)arr.Length) LastErr = Marshal.GetLastWin32Error();
        return n;
    }
    public static int LastErr;
}
'@
if ($Error.Count -gt 0) { Fail "Add-Type 失败：$($Error[0].Exception.Message)" }

# 与产品同一套语义（HotkeyManager.ParseHotkey）但独立实现：不复用它的代码，
# 这样「我以为产品注册的是 X」与「产品日志说它注册的是 X」才是对得撞的两件事。
function Parse-Chord([string]$s) {
    $mods = 0; $vk = -1
    foreach ($raw in ($s -split '\+')) {
        $p = $raw.Trim().ToUpperInvariant()
        switch ($p) {
            'CTRL'   { $mods = $mods -bor 0x0002 }
            'CONTROL'{ $mods = $mods -bor 0x0002 }
            'ALT'    { $mods = $mods -bor 0x0001 }
            'SHIFT'  { $mods = $mods -bor 0x0004 }
            'WIN'    { $mods = $mods -bor 0x0008 }
            'WINDOWS'{ $mods = $mods -bor 0x0008 }
            'PRINT'  { $vk = 0x2C }
            'PRTSC'  { $vk = 0x2C }
            'SPACE'  { $vk = 0x20 }
            default {
                if ($p -match '^F(\d{1,2})$') { $vk = 0x60 + [int]$Matches[1] }
                elseif ($p.Length -eq 1) { $vk = [int][char]$p }
                else { $vk = -1 }
            }
        }
    }
    return ,@($mods, $vk)
}

$hwnd = [IntPtr]::Zero          # hWnd=0：热键消息投到本线程队列；本脚本只占位，不收 WM_HOTKEY
$targets = @('Ctrl+Shift+S', 'Ctrl+Shift+Q')
$heldIds = @{}
$claimedChords = @()
$new = @()
$proc = $null
$silentLive = ''

try {
    # ── 临时设置：产物落仓库内、热键用默认值 ──
    $sha0 = Sha $set
    Say @("轮前设置 sha1: $sha0")
    Move-Item -LiteralPath $set -Destination $bak
    $j = [IO.File]::ReadAllText($bak) | ConvertFrom-Json
    $obj = [ordered]@{}
    foreach ($p in $j.PSObject.Properties) { $obj[$p.Name] = $p.Value }
    $obj['OutputPath'] = $outDir
    $obj['Hotkey'] = 'Ctrl+Shift+S'
    $obj['SilentHotkey'] = 'Ctrl+Shift+Q'
    $obj['RecordHotkey'] = 'Ctrl+Shift+G'
    $obj['SettingsRevision'] = 2
    ($obj | ConvertTo-Json -Compress) | Set-Content -LiteralPath $set -NoNewline -Encoding UTF8
    Say @("写进去的设置: $([IO.File]::ReadAllText($set))")

    # ── ① 抢下两个首选组合（自检①）──
    $i = 0
    foreach ($t in $targets) {
        $i++
        $pv = Parse-Chord $t
        $ok = [Win32Hotkey]::RegisterHotKey($hwnd, 4100 + $i, [uint32]($pv[0] -bor 0x4000), [uint32]$pv[1])
        $errv = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Say @("自检①：本进程注册 $t ⇒ $ok $(if (-not $ok) { "(err=$errv) 抢不到 ⇒ 冲突可能来自别的程序，对照不完整" })")
        if ($ok) { $heldIds[$t] = 4100 + $i }
    }

    # ── ② 起真身 ──
    # ⚠ 日志必须按【本次启动之后】切：本机上今天已经起过五次带窗口的真身，
    #   拿"最后 14 行"会把旧几次的「已注册」混进来，于是挑出来的组合键是上一世的（本轮实测挑错过）。
    $logMark = 0
    if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
    $before = @(Get-ChildItem -LiteralPath $outDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("已起真身 PID=$($proc.Id)，等 $SettleSec s（日志起点标在第 $logMark 行）")
    Start-Sleep -Seconds $SettleSec

    # ── ③ 产品的热键日志（回退链的实况）──
    $all1 = @()
    if (Test-Path -LiteralPath $appLog) { $all1 = @(Get-Content -LiteralPath $appLog -Encoding UTF8) }
    $scoped = @($all1 | Select-Object -Skip $logMark)
    Say @("本次启动之后新增 $($scoped.Count) 行日志")
    $hk = @($scoped | Where-Object { $_ -match 'HotkeyManager' })
    Say @("--- 产品的 HotkeyManager 日志（本次范围内 $($hk.Count) 条）---")
    foreach ($l in $hk) { Say @("  $l") }
    $claimedChords = @($hk | ForEach-Object {
        if ($_ -match '已注册:\s*(.+?)\s*\(id=') { $Matches[1].Trim() } })
    Say @("产品自报【已注册】: $(if ($claimedChords.Count) { $claimedChords -join ' | ' } else { '<日志里没有这一句>' })")

    # ── ④ 独立反证（自检②：必须带能成功的对照臂）──
    foreach ($c in $claimedChords) {
        $pv = Parse-Chord $c
        if ($pv[1] -lt 0) { Say @("   反证跳过「$c」：本脚本解析不出这个组合，不猜"); continue }
        $ok = [Win32Hotkey]::RegisterHotKey($hwnd, 4200, [uint32]($pv[0] -bor 0x4000), [uint32]$pv[1])
        Say @("   反证：本进程再注册「$c」⇒ $(if (-not $ok) { '失败 ⇒ 确实被产品持有 ✓' } else { '成功 ⇒ ★日志说注册了，系统里却没人持有' })")
        if ($ok) { $null = [Win32Hotkey]::UnregisterHotKey($hwnd, 4200) }
    }
    $ctrl = Parse-Chord 'Ctrl+Shift+Y'
    $okc = [Win32Hotkey]::RegisterHotKey($hwnd, 4201, [uint32]($ctrl[0] -bor 0x4000), [uint32]$ctrl[1])
    Say @("   对照臂：注册一个没人用的 Ctrl+Shift+Y ⇒ $okc（必须 True，否则上面那些【失败】毫无意义）")
    if ($okc) { $null = [Win32Hotkey]::UnregisterHotKey($hwnd, 4201) }

    # ── ⑤ 释放占用，按一次产品实际持有的无感组合 ──
    foreach ($k in @($heldIds.Keys)) {
        $null = [Win32Hotkey]::UnregisterHotKey($hwnd, $heldIds[$k])
        $heldIds.Remove($k)
    }
    # 取【最后一条】以 Q 结尾的：这是本次注册链的终点（不是今天更早几次启动的残留）
    $silentLive = @($claimedChords | Where-Object { $_ -match 'Q$' } | Select-Object -Last 1)
    if (-not $silentLive) {
        Say @('⏭ 本次日志里没有以 Q 结尾的已注册组合 ⇒ 按键那一腿不给结论')
    } else {
        $pv = Parse-Chord $silentLive
        $nEv = 0; if ($pv[0] -band 2) { $nEv += 2 }; if ($pv[0] -band 1) { $nEv += 2 }
        if ($pv[0] -band 4) { $nEv += 2 }; if ($pv[0] -band 8) { $nEv += 2 }; $nEv += 2
        $sent = [Win32Hotkey]::Chord([uint32]$pv[0], [uint32]$pv[1])
        Say @("按下「$silentLive」：送出 $sent 个事件 / 应送 $nEv 个 ⇒ $(if ($sent -eq $nEv) { '全数被系统接收 ✓' } else { '★ 没送全，按键这一腿按未判处理（err=' + [Win32Hotkey]::LastErr + '）' })")
        $deadline = (Get-Date).AddSeconds($WaitFileSec)
        # ⚠ 不能按"文件出现"判完成（本轮实测犯的错）：编码是【先写 .名字.guid.tmp 再改名】的原子写，
        #   所以 .tmp 一出现就被我当成产物、随即杀进程 ⇒ 只留下一个 0 字节的 .tmp，
        #   剪贴板那一腿变成"我读早了"而不是判据。判据必须等它自己报完成/失败/取消那一条日志。
        $donePat = '无感截图完成|无感截图编码失败|无感截图编码已取消'
        $doneLine = ''
        Say @("等『无感截图完成』那一条日志（最多 $WaitFileSec s）…")
        while ((Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath $appLog) {
                $tailNew = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark |
                    Where-Object { $_ -match $donePat })
                if ($tailNew.Count -gt 0) { $doneLine = $tailNew[-1]; break }
            }
            Start-Sleep -Milliseconds 500
        }
        if ($doneLine) { Say @("完成行：$doneLine") }
        else { Say @("⏭ 等待窗口内没等到完成行 ⇒ 剪贴板那一腿按【未判】处理（读了也只是 race 的产物）") }

        $nowList = @(Get-ChildItem -LiteralPath $outDir -Recurse -Force -File -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
        $new = @($nowList | Where-Object { $_ -notin $before })
        Say @("--- 产物腿：输出目录（递归，含隐藏）新增 $($new.Count) 个；跑前已有 $($before.Count) 个 ---")
        foreach ($n in $new) { Say @("   $n  $([math]::Round((Get-Item $n).Length / 1kb, 1)) KB  sha1=$(Sha $n)") }
        if ($new.Count -eq 0) { Say @('   ⏭ 没有新文件 ⇒ 这一腿未判（不能读成「截图失败」，也可能是按键没送达）') }
        $tmpOnly = @($new | Where-Object { $_ -match '\.tmp$' })
        if ($tmpOnly.Count -gt 0) { Say @("   ⚠ 其中 $($tmpOnly.Count) 个是未改名的 .tmp ⇒ 编码没走完，产物腿不成立") }

        if ($doneLine -match '无感截图完成') {
            try {
                $img = Get-Clipboard -Format Image -ErrorAction Stop
                if ($null -ne $img) {
                    Say @("--- 剪贴板腿：有位图 $($img.Width)x$($img.Height) 格式=$($img.PixelFormat) ✓ ---")
                    $img.Dispose()
                } else { Say @('--- 剪贴板腿：取到 null ⇒ 完成行有了，但剪贴板里没有位图 ---') }
            } catch { Say @("--- 剪贴板腿：读失败 $($_.Exception.Message) ---") }
        } else {
            Say @('--- 剪贴板腿：未读（没等到完成行）---')
        }

        # ── 界面那一条：HotkeyTxt 显示的组合与日志里【实际注册】的那条对撞 ──
        # 这一条才是本格的主判据：回退本身是好意，但界面还写着用户原来挑的那条、
        # 而生效的是另一条 ⇒ 用户按老键位没反应，且界面上看不出键位已经变了（静默降级）。
        try {
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $auto = [System.Windows.Automation.AutomationElement]
            $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)
            $tops = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
            $big = @($tops | Sort-Object { try { $_.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { 0 } } -Descending | Select-Object -First 1)
            $edits = @()
            if ($null -ne $big) {
                $edits = @($big.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))))
            }
            Say @("--- 界面腿：主窗里 $($edits.Count) 个编辑框，找含 '+' 的那个（快捷键输入框）---")
            foreach ($e in $edits) {
                $v = $e.Current.Name
                try { $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $v = $vp.Current.Value } catch { }
                if ($v -match '\+') { Say @("   界面显示的快捷键 = 「$v」   日志实际注册 = 「$silentLive / 捕获那条见上」") }
            }
        } catch { Say @("   界面腿读失败：$($_.Exception.Message)") }

        try {
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $auto = [System.Windows.Automation.AutomationElement]
            $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)
            $tops = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
            Say @("--- 可见性腿：该 PID 现在 $($tops.Count) 个顶层件 ---")
            foreach ($t in $tops) {
                $kids = try { $t.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count } catch { -1 }
                Say @("   <$($t.Current.ClassName)> 子树 $kids 标题=[$($t.Current.Name)]")
            }
        } catch { Say @("   可见性腿读失败：$($_.Exception.Message)") }

        if (Test-Path -LiteralPath $appLog) {
            $all2 = @(Get-Content -LiteralPath $appLog -Encoding UTF8)
            $raw2 = @($all2 | Select-Object -Skip $logMark | Select-Object -Last 120)
            $hk2 = @($raw2 | Where-Object { $_ -match '无感|编码完成|已保存|剪贴板|Hotkey|Toast|提示|失败' })
            Say @("--- 日志腿（本次范围内相关的 $($hk2.Count) 条，最多印 20 条）---")
            foreach ($l in ($hk2 | Select-Object -Last 20)) { Say @("  $l") }
        }
    }
    Write-Output "RC=0 CLAIMED=[$($claimedChords -join ' / ')] NEW=$($new.Count) PID=$($proc.Id)"
} finally {
    foreach ($k in @($heldIds.Keys)) {
        $null = [Win32Hotkey]::UnregisterHotKey($hwnd, $heldIds[$k])
        Say @("收尾：释放本进程占用的 $k")
    }
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
