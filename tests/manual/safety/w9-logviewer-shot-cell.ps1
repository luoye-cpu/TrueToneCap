# 把 LogViewer 那 5 枚换过的图标拍下来 —— 它是独立 exe，主程序修好了不代表它也修好了。
#
# 为什么这一格值得跑：报告 §13.6 把它记成"未目视"。LogViewer 的 5 枚图标本轮从 FontIcon 换成
# PathIcon，但整个 10-01 轮里从没截过它的图；而它和主程序是两个进程、两份 App.xaml，
# 主程序的资源链修好了对它没有任何含义。
# 纪律：起的是我自己拉起的工具进程，收尾必须关掉；不碰主程序实例、不碰任何设置文件。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.LogViewer\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TTCLogViewer.exe',
    [int]$SettleSec = 8,
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\logviewer-icons.png',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\logviewer-icons.log'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
[IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== LogViewer 图标目视 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
if (-not (Test-Path -LiteralPath $Exe)) { Say @("RC=2 未判：exe 不在 $Exe"); exit 2 }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class LV
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string RectOf(IntPtr h) { R r; return GetWindowRect(h, out r) ? (r.L + "," + r.T + " " + (r.Rr - r.L) + "x" + (r.B - r.T)) : "无"; }
    public static IntPtr[] OfPid(uint pid)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
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
if (-not ('LV' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来'); exit 2 }

$proc = $null
try {
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path -Parent $Exe)
    Say @("PID=$($proc.Id)，等 $SettleSec s")
    Start-Sleep -Seconds $SettleSec
    if ($proc.HasExited) { Say @("★ 进程已退出（exit=$($proc.ExitCode)）⇒ 未判"); exit 2 }
    $wins = @([LV]::OfPid([uint32]$proc.Id))
    Say @("可见窗 $($wins.Count) 扇")
    $big = [IntPtr]::Zero; $bestW = 0
    foreach ($h in $wins) {
        $r = [LV]::RectOf($h); Say @("  hwnd=$h class<$([LV]::ClassOf($h))> $r")
        if ($r -match '(\d+)x(\d+)$') { $w = [int]$matches[1]; if ($w -gt $bestW) { $bestW = $w; $big = $h } }
    }
    if ($big -eq [IntPtr]::Zero) { Say @('★ 没有可见窗 ⇒ 未判') }
    elseif ($bestW -lt 400) { Say @("★ 最大窗只有 $($bestW)px ⇒ 不像主窗体，判为未判") }
    else {
        $ok = [LV]::Save($big, $Png)
        Say @("取图 $ok（$($bestW)px）⇒ $Png")
        Say @('要看的是工具条：刷新 / 选择目录 / 复制 / 导出 / 关闭 这 5 枚应当有形状，不是空白也不是方块')
    }
} catch { Say @("★ 异常：$($_.Exception.Message)") } finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } ; Say @('本格拉起的工具进程已关闭（主程序实例未触碰）') }
}
exit 0
