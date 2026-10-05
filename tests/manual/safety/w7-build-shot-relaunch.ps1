# 驱动：关掉实例 → 重编 → 跑取图夹具 → 把实例按原样起回
#
# 为什么要专门一个驱动而不是手敲：产品有单实例 Mutex，且 Stop-Process 之后进程不是立刻消失；
# 上一轮就因为"没等真退出"跑出一臂废数据（内层夹具按起手自检拒跑，判词逻辑却把它读成产品结论）。
# 这里把"等到真没有实例"写成硬前提，做不到就直接 RC=2 未判，绝不带着脏前提往下跑。
param(
    [string]$Bin = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64',
    [string]$Cell = 'w5-mainwindow-shot-cell.ps1',
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\probe-pathicon-shot.log',
    [string]$Png = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\probe-pathicon.png',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
Say @("=== 驱动 · $(Get-Date -Format 'HH:mm:ss') ===")

$killed = @()
foreach ($q in @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)) {
    try { [void]$q.CloseMainWindow() } catch { }
    Start-Sleep -Milliseconds 700
    if (-not $q.HasExited) { try { Stop-Process -Id $q.Id -Force; $killed += $q.Id } catch { } }
}
$dl = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $dl -and @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue).Count) { Start-Sleep -Milliseconds 500 }
$still = @(Get-Process -Name 'TrueToneCap' -ErrorAction SilentlyContinue)
Say @("关闭 PID $(if ($killed.Count) { $killed -join ',' } else { '（无）' })；仍在跑的 = $($still.Count)")
if ($still.Count) { Say @('RC=2 未判：还有实例占着单实例 Mutex'); exit 2 }

if (-not $SkipBuild) {
    $b = & dotnet build (Join-Path 'C:\PLAN\TrueToneCap' 'src\TrueToneCap.App\TrueToneCap.App.csproj') -c Release 2>&1
    $errs = @($b | Select-String -Pattern 'error|错误 CS|错误 MSB' | Select-Object -First 4)
    Say @("构建：$(if ($errs.Count) { '★ ' + ($errs -join ' ;; ') } else { '0 错误' })")
    if ($errs.Count) {
        # 构建失败也要把实例起回去，不能让用户的机器停在"没有托盘程序"的状态
        $p = Start-Process -FilePath (Join-Path $Bin 'TrueToneCap.exe') -WorkingDirectory $Bin -PassThru
        Start-Sleep -Seconds 10; $p.Refresh()
        Say @("构建失败 ⇒ 仍按原样起回实例 PID=$($p.Id) 活着=$(-not $p.HasExited)（旧二进制）")
        exit 3
    }
}

& (Join-Path 'C:\PLAN\TrueToneCap\tests\manual\safety' $Cell) -Log $Log -Png $Png | Out-Null
$after = @(Get-Content -LiteralPath $Log -Encoding UTF8)
foreach ($l in ($after | Where-Object { $_ -match '主窗 hwnd|取图|RC=2|全黑' })) { Say @("  | $($l.Substring(0,[Math]::Min(140,$l.Length)))") }

$p2 = Start-Process -FilePath (Join-Path $Bin 'TrueToneCap.exe') -WorkingDirectory $Bin -PassThru
Start-Sleep -Seconds 12; $p2.Refresh()
Say @("实例已按原样起回：PID=$($p2.Id) 活着=$(-not $p2.HasExited)")
exit 0
