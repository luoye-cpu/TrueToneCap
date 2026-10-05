# F 批剩余构造之一：**随包后端被占用**（§15.5 列的"native 目录被占用"那一格）。
# 为什么用"占用"而不是"改名"：改名会污染 [1.9/2] 的构建输入面（随包 exe 在输入面里），
# 而且改完要还原、还原失败就把下一轮跑在坏环境上（§13.7 已经否掉那条路）。
# 占用是同一格故障的无损构造：句柄一放，环境就复原，不需要任何还原动作。
#
# 三条自检，少一条就不给结论：
#   ① 锁到的文件数与路径必须打印（一个都没锁到 = 注入不存在）；
#   ② 锁完之后【亲口试跑一次】该 exe，必须失败 —— 这是"注入真的挡住了后端"的证据；
#      挡住的是进程创建，不是我臆测的"编码会失败"；
#   ③ finally 里放锁后再试跑一次，必须成功 —— 否则收尾时这台机器的后端是我弄坏的。
param(
    [string]$ExeName = 'avifenc.exe',
    [string]$HostDir = 'C:\PLAN\TrueToneCap\src\TrueToneCap.Test\bin\Release\net11.0-windows10.0.26100.0',
    [string]$Suite = '--color-tests',
    [string]$LogName = 'native-busy.log'
)
$ErrorActionPreference = 'Continue'
$log = Join-Path 'C:\PLAN\TrueToneCap\tests\output' $LogName
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
function Fail([string]$m) { Say @("RC=1 $m"); Write-Output "RC=1 $m"; exit 1 }

[System.IO.File]::WriteAllText($log, '', $utf8)
Say @("=== 后端占用格 · $ExeName · $Suite · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

if (-not (Test-Path -LiteralPath $HostDir)) { Fail "找不到宿主目录 $HostDir" }
$targets = @(Get-ChildItem -LiteralPath $HostDir -Filter $ExeName -Recurse -File -ErrorAction SilentlyContinue)
if ($targets.Count -lt 1) { Fail "在 $HostDir 下没找到 $ExeName ⇒ 注入对象不存在，不给结论" }
Say @("锁定目标 $($targets.Count) 份：")
foreach ($t in $targets) { Say @("  $($t.FullName)  ($([math]::Round($t.Length/1kb)) KB)") }

function Try-Launch([string]$path) {
    # 返回 'ok' / 'blocked' / 'other:<msg>'：只认【创建被挡】这一种失败为"注入生效"
    try {
        $pi = New-Object System.Diagnostics.ProcessStartInfo
        $pi.FileName = $path
        $pi.Arguments = '--version'
        $pi.UseShellExecute = $false
        $pi.RedirectStandardOutput = $true
        $pi.RedirectStandardError = $true
        $pi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($pi)
        $null = $p.StandardOutput.ReadToEnd()
        $p.WaitForExit(20000) | Out-Null
        if ($p.ExitCode -eq 0) { return 'ok' }
        return "other:exit=$($p.ExitCode)"
    } catch [System.ComponentModel.Win32Exception] {
        return "blocked:$($_.NativeErrorCode)"
    } catch {
        return "other:$($_.Exception.GetType().Name)"
    }
}

$handles = @()
try {
    foreach ($t in $targets) {
        # FileMode=Open / FileAccess=Read / FileShare=None ⇒ 别人（含 CreateProcess）再打不开这个镜像
        $handles += [System.IO.File]::Open($t.FullName, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    }
    Say @("已持有 $($handles.Count) 个 FileShare.None 句柄")

    $probe = Try-Launch $targets[0].FullName
    Say @("注入自检②：占用后亲口试跑 $($targets[0].Name) ⇒ $probe")
    if ($probe -notlike 'blocked*') {
        Fail "占用之后它仍然起得来（$probe）⇒ 注入没生效，这一跑读数与本格无关，拒判"
    }

    $out = Join-Path 'C:\PLAN\TrueToneCap\tests\manual\safety\native-busy' $Suite.Replace('--','')
    if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }
    $stdoutFile = Join-Path $out 'suite-stdout.txt'
    $p = Start-Process -FilePath (Join-Path $HostDir 'TrueToneCap.Test.exe') -ArgumentList @($Suite) `
        -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutFile -RedirectStandardError (Join-Path $out 'suite-stderr.txt')
    if ($null -eq $p.ExitCode) { Fail "套件退出码读不到 ⇒ 不给结论（不许拿 stdout 里有 ok 代替）" }
    Say @("套件 $Suite 退出码 = $($p.ExitCode)")
    $lines = @(Get-Content -LiteralPath $stdoutFile -Encoding UTF8)
    $hits = @($lines | Where-Object { $_ -match 'avif|AVIF|RESULT|结果:|跳过|Skip' })
    Say @("--- 与 AVIF / 汇总相关的行（共 $($hits.Count) 行，全量见 $stdoutFile）---")
    foreach ($h in $hits) { Say @("  $h") }
    Write-Output "RC=0 EXIT=$($p.ExitCode) LOCKED=$($handles.Count) LOG=$log"
} finally {
    foreach ($h in $handles) { try { $h.Dispose() } catch { } }
    $after = Try-Launch $targets[0].FullName
    Say @("收尾自检③：放锁后再试跑 ⇒ $after")
    if ($after -ne 'ok') { Say @("❌ 放锁后仍然起不来 ⇒ 这台机器的后端状态没还原，请立即复查") }
}
