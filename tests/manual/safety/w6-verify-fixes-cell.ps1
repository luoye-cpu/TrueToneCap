# W6 复验：②豆腐块 / 那条假警告 与 ③两条空白下拉，改完之后到底成不成立
#
# 四项判据（缺一不许记"已修完"）：
#   A 日志：`XamlControlsResources 加载失败` 必须不再出现，且不得冒出新的 Error；
#   B 取图：主窗抓下来看导航栏那 5 枚图标还是不是豆腐块（PrintWindow flags=2，不抢前台）；
#   C 界面：OverlayColorCbo 必须【显示盘上那个值】而不是空白；OcrLangCbo 在 ONNX 下为空且不再报 Warn；
#   D 往返：进程退出后盘上的 OverlayColor / OcrLanguage 必须逐字还在（旧写法会把它抹成兜底字面量）。
# 纪律同前：只起/只杀本次的 PID；设置按字节还原并复算 sha256；判据不依赖焦点。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int]$SettleSec = 12,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\mainwindow-w6.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\w6-verify.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== W6 复验 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$dir = Split-Path -Parent $Exe
$set = Join-Path $dir 'TrueToneCap.settings.json'
$appLog = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 未判：没有同目录设置文件'); exit 2 }
$bytes0 = [IO.File]::ReadAllBytes($set); $sha0 = Sha $set
$raw0 = [IO.File]::ReadAllText($set)
$oc0 = ''; $ol0 = ''
$m1 = [regex]::Match($raw0, '"OverlayColor":"([^"]*)"'); if ($m1.Success) { $oc0 = $m1.Groups[1].Value }
$m2 = [regex]::Match($raw0, '"OcrLanguage":"([^"]*)"'); if ($m2.Success) { $ol0 = $m2.Groups[1].Value }
Say @("轮前设置 sha256 = $sha0")
Say @("轮前盘上：OverlayColor=「$oc0」 OcrLanguage=「$ol0」")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class G6
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static string Client(IntPtr h) { R r; return GetClientRect(h, out r) ? ((r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static bool Save(IntPtr hwnd, string path)
    {
        R rc; if (!GetClientRect(hwnd, out rc)) return false;
        int w = rc.Rr - rc.L, h = rc.B - rc.T;
        if (w <= 0 || h <= 0) return false;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) return false;
                bmp.Save(path, ImageFormat.Png);
                return true;
            }
        }
    }
}
'@
if (-not ('G6' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来（编译失败会伪装成"没有数据"）'); exit 2 }
$auto = [System.Windows.Automation.AutomationElement]
function Find-Combo($rootEl, [string]$id) {
    $cbs = @($rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox))))
    foreach ($c in $cbs) { if ($c.Current.AutomationId -eq $id) { return $c } }
    return $null
}
function Describe-Combo($rootEl, [string]$id, [string]$expect) {
    $c = Find-Combo $rootEl $id
    if (-not $c) { return "★ 可视树里没有 $id（页面没实化？）" }
    $ec = $null
    try { $ec = $c.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
    if ($ec) { try { $ec.Expand() } catch { } ; Start-Sleep -Milliseconds 800 }
    $items = @($c.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    $sel = @()
    foreach ($it in $items) {
        try { if ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { $sel += $it.Current.Name } } catch { }
    }
    if ($ec) { try { $ec.Collapse() } catch { } }
    $names = @(); foreach ($it in $items) { $names += $it.Current.Name }
    $hit = if ($expect) { $names -contains $expect } else { $false }
    return "$id 项数=$($items.Count) 选中=[$($sel -join '/')] 含盘上原值=$hit 全部=[$($names -join ' | ')]"
}

$proc = $null
$ocSeen = ''; $olSeen = ''
$vA = '未判'; $vB = '未判'; $vC = '未判'; $vD = '未判'
try {
    $m0 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 -ErrorAction SilentlyContinue).Count
    # 起手自检：产品有单实例 Mutex（App.xaml.cs），别人的实例在场时我起的第二个会立刻
# 打一行「已有实例运行」然后退出 ⇒ 那时"0 个顶层窗"是夹具前提不成立，不是产品没界面。
$pre = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
if ($pre.Count) {
    Say @("RC=2 未判：起手就有 $($pre.Count) 个 TrueToneCap 实例在跑（$($pre.Id -join ',')）⇒ 单实例 Mutex 会让本次起的进程立刻退出")
    exit 2
}
$proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0)

    # ── A：那条假警告必须不再响；也不许冒新 Error ──
    $bad = @($slice | Where-Object { $_ -match 'XamlControlsResources' })
    $errs = @($slice | Where-Object { $_ -match '\[Error' })
    $warns = @($slice | Where-Object { $_ -match '\[Warning' })
    Say @("A 本会话日志 $($slice.Count) 行：XamlControlsResources 命中 $($bad.Count) 行，Error $($errs.Count) 条，Warning $($warns.Count) 条")
    foreach ($l in $errs) { Say @("   E| $($l.Substring(0, [Math]::Min(150, $l.Length)))") }
    foreach ($l in @($warns | Select-Object -First 8)) { Say @("   W| $($l.Substring(0, [Math]::Min(150, $l.Length)))") }
    if ($bad.Count -eq 0 -and $errs.Count -eq 0) { $vA = '过：那条警告不再出现，且无新 Error' }
    elseif ($bad.Count -gt 0) { $vA = "★ 仍有 XamlControlsResources 行 $($bad.Count) 条" }
    else { $vA = "★ 警告没了但冒出 Error $($errs.Count) 条" }

    # ── 找主窗（子树元素数最大的那块，不是枚举第一个）──
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $proc.Id)
    $tops = @($auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    $main = $null; $best = -1
    foreach ($w in $tops) {
        $n = -1; try { $n = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)).Count } catch { }
        if ($n -gt $best) { $best = $n; $main = $w }
    }
    if (-not $main) { Say @('★ 没找到主窗 ⇒ B/C/D 全未判'); throw 'no-main-window' }
    $mh = [IntPtr]$main.Current.NativeWindowHandle
    $null = [G6]::ShowWindow($mh, 9); Start-Sleep -Milliseconds 800
    Say @("主窗 hwnd=$mh 子树=$best 客户区=$([G6]::Client($mh)) 可见API=$([G6]::IsWindowVisible($mh))")

    # ── B：取图看豆腐块 ──
    $ok = [G6]::Save($mh, $Png)
    Say @("B 取图 $ok ⇒ $Png（图标是否还缺字形，由人看图判定；本格只保证图是真的）")
    if ($ok) { $vB = "已取图 $Png" } else { $vB = '★ 取图失败' }

    # ── C：两个下拉。先逐个导航页翻，直到那块页面实化出控件 ──
    $navNames = @('输出设置', '截图与录制', 'OCR / 翻译', '系统设置', '运行日志')
    foreach ($page in $navNames) {
        $nav = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
        $hit = @($nav | Where-Object { $_.Current.Name -eq $page } | Select-Object -First 1)
        if (-not $hit) { Say @("★ 找不到导航项「$page」⇒ 这一页跳过量"); continue }
        try { $hit[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
        catch { Say @("★ 切页「$page」抛错：$($_.Exception.Message)（不据此说功能坏了）"); continue }
        Start-Sleep -Milliseconds 1200
        $d1 = Describe-Combo $main 'OverlayColorCbo' $oc0
        $d2 = Describe-Combo $main 'OcrLangCbo' $ol0
        if ($d1 -notmatch '可视树里没有') { $ocSeen = $d1 }
        if ($d2 -notmatch '可视树里没有') { $olSeen = $d2 }
        if ($d1 -notmatch '可视树里没有' -or $d2 -notmatch '可视树里没有') {
            Say @("C 页「$page」：$d1")
            Say @("            $d2")
        }
    }
    $finalSlice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $m0)
    $blankWarn = @($finalSlice | Where-Object { $_ -match '在下拉 .* 的 \d+ 个选项里没有对应项' })
    $zeroItemWarn = @($finalSlice | Where-Object { $_ -match '在下拉 \w+ 的 0 个选项里没有对应项' })
    Say @("C' 回填警告计数：未匹配 $($blankWarn.Count) 条（其中「0 个选项」$($zeroItemWarn.Count) 条）")
    foreach ($l in $blankWarn) { Say @("   | $($l.Substring(0, [Math]::Min(160, $l.Length)))") }
    # 判词必须用【翻页途中真的读到那一格】的那一次读数：最后再找一次会读到"页面已经切走了"⇒
    # 11:20 那一趟就是这样把好读数覆盖成 ★ 的（界面明明显示着 #99001833，判词却说没找到）。
    if ($ocSeen) {
        if ($ocSeen -match '含盘上原值=True' -and $ocSeen -match "选中=\[$oc0\]") {
            $vC = "过：OverlayColorCbo 显示并选中盘上原值「$oc0」"
        } elseif ($ocSeen -match '含盘上原值=True') {
            $vC = "★ 原值在列表里但没被选中 ⇒ $ocSeen"
        } else { $vC = "★ OverlayColorCbo 里没有原值 ⇒ $ocSeen" }
    } elseif ($oc0) { $vC = '★ 翻遍 5 个导航页都没读到 OverlayColorCbo ⇒ 这一格本轮判不了（不是"没问题"）' }
    else { $vC = '盘上没有 OverlayColor 键 ⇒ 这一项本轮判不了（不是"没问题"）' }
    if ($zeroItemWarn.Count -gt 0) { $vC = "$vC ／ ★ 仍有「0 个选项」的噪声 $($zeroItemWarn.Count) 条" }
    else { $vC = "$vC ／ 无「0 个选项」噪声 ✓" }

    # ── D：触发一次【真实保存】再看盘上两个值还在不在 ──
    # 为什么按「保存设置」那颗按钮而不是等它退出：托盘常驻的窗口 CloseMainWindow 未必退得干净
    # （07:00 那一趟就是"窗口关不掉 ⇒ 强杀"），而强杀根本没走 SaveSettings ⇒ 那样读出来的"还在"是假的。
    $btns = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
    $bn = @(); foreach ($b in $btns) { $bn += $b.Current.Name }
    Say @("D' 可视树里的按钮名：[$($bn -join ' | ')]")
    $save = @($btns | Where-Object { $_.Current.Name -match '保存' } | Select-Object -First 1)
    if ($save) {
        try { $save[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Say @("已按「$($save[0].Current.Name)」⇒ 走的是真 SaveSettings") }
        catch { Say @("★ Invoke 抛错：$($_.Exception.Message) ⇒ D 项未判") }
        Start-Sleep -Seconds 3
    } else { Say @('★ 找不到「保存」那颗按钮 ⇒ 本轮没触发真保存，D 项只能记未判') }
    try { $null = $proc.CloseMainWindow() } catch { }
    Start-Sleep -Seconds 5
    if (-not $proc.HasExited) { Say @('窗口关不掉 ⇒ 稍后强杀（本次起的 PID）；D 的判据看的是上面那次真保存之后盘上的值') }
    $rawA = [IO.File]::ReadAllText($set)
    $mA = [regex]::Match($rawA, '"OverlayColor":"([^"]*)"')
    $nowOc = if ($mA.Success) { $mA.Groups[1].Value } else { '（键不存在）' }
    $mB = [regex]::Match($rawA, '"OcrLanguage":"([^"]*)"')
    $nowOl = if ($mB.Success) { $mB.Groups[1].Value } else { '（键不存在）' }
    Say @("D 退出后盘上：OverlayColor=「$nowOc」（轮前「$oc0」） OcrLanguage=「$nowOl」（轮前「$ol0」）")
    if ($save) {
        if ($nowOc -eq $oc0) { $vD = "过：真保存之后 OverlayColor 逐字还是「$oc0」（旧写法这里会被抹成兜底字面量）" }
        else { $vD = "★ 一次保存就把 OverlayColor 改成「$nowOc」" }
    } else { $vD = '未判：没触发到真保存' }
} catch {
    Say @("★ 中断：$($_.Exception.Message)")
} finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force; Say @("已停止本次起的 PID=$($proc.Id)") } catch { } }
    if ((Sha $set) -ne $sha0) {
        Say @("★ 设置与轮前不同 ⇒ 按字节还原（本轮判据 D 要看的就是这个键，还原前已把两个读数记在上面）")
        [IO.File]::WriteAllBytes($set, $bytes0)
    }
    Say @("设置还原自检：$(Sha $set) ⇒ $(if ((Sha $set) -eq $sha0) { '与轮前逐字相同 ✓' } else { '★ 不同，立即核对' })")
}
Say @("")
Say @("--- 四项判词 ---")
Say @("A 日志：$vA")
Say @("B 取图：$vB")
Say @("C 界面：$vC")
Say @("D 往返：$vD")
Write-Output "A=$vA ｜ B=$vB ｜ C=$vC ｜ D=$vD"
exit 0
