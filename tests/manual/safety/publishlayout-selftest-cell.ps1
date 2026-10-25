# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 发布布局下 missingBackend 能不能归零 —— 交接账本 §30.5 的第一条"未验"。
#
# 为什么不是"直接跑那枚 v0.3.6-beta 的包"：包里的 App 出自 v0.3.6-beta，里面还没有
# missingBackend 这一类结局（它是本轮未提交的改动）。所以要在【当前构建的宿主】上造出
# 发布布局的那一条性质 —— 而这条性质对判据来说只有一件事：
#   <BaseDirectory>\native\ 里躺着 avifenc/cjpegli/cjxl/cwebp 四枚 exe
# （Publish.ps1 [3/6] 就是把 Core/Resources 那四枚复制进 native/；两处的文件 sha256 已核过逐字相同，
#   见本脚本臂 B 之前打印的锚点行。四枚探针各自只看自己那一枚：JpegLiNative 跑 `cjpegli.exe -h`，
#   不是查 jxl.dll，所以"少复制一枚"会只错一格 —— 臂 C 就是拿来验这句话的。）
#
# 三臂，同一份二进制、同一次会话内跑完，差值才可归因：
#   A native/ 为空          ⇒ 期望 missingBackend=4（dev 布局的既有读数，本轮重取当对照）
#   B native/ 有四枚        ⇒ 期望 missingBackend=0、fail=0、退出码 0
#   C native/ 只有 cwebp    ⇒ 期望 missingBackend=3、WebP 那一格单独变 ok
#     ⇒ 只有"按格式独立判定"才会是这个读数；若分类是按"native 目录非空"这种粗判断，臂 C 会假绿成 0。
#
# 纪律：native/ 收尾恢复成【轮前那件数】并在 finally 里做（脚本中途抛出也要还原）；
#      设置文件（App 同目录 + %LOCALAPPDATA% 两份）按原字节还原并复算 sha256；
#      progress.txt 每臂先删（SelfTest 是 AppendAllText，不清就分不清哪几行是本臂的）；
#      解析不到汇总行 ⇒ 记【未判】RC=2，不许读成绿。
param(
    [string]$Exe = '',
    [int]$TimeoutSec = 240,
    [switch]$LeaveNative,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\publishlayout-selftest-cell.log'
)
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { 'ABSENT' } }

$tools = @('avifenc.exe', 'cjpegli.exe', 'cjxl.exe', 'cwebp.exe')
$toolsSrc = Join-Path $repo 'src\TrueToneCap.Core\Resources'

# ── 宿主定位：与 scripts\verify.ps1 / app-selftest-backends-cell.ps1 同一套枚举口径 ──
if (-not $Exe) {
    $Exe = (Get-ChildItem (Join-Path $repo 'src\TrueToneCap.App\bin') -Filter 'TrueToneCap.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains 'Release' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
[IO.File]::WriteAllText($Log, '', $utf8)
if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) {
    Say @("RC=2 未判：没定位到宿主 exe（搜索根 $repo\src\TrueToneCap.App\bin，路径段含 'Release'）")
    Write-Host 'RC=2 未判：没有宿主'
    exit 2
}
$dir = Split-Path -Parent $Exe
$native = Join-Path $dir 'native'
Say @("=== 发布布局 · --selftest 三臂 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")
Say @("exe    = $Exe")
Say @("native = $native")

# ── 轮前状态：native/ 必须是【空目录】，否则 dev 布局那条对照就不成立 ──
if (-not (Test-Path -LiteralPath $native)) {
    Say @("轮前 native/ 不存在 ⇒ 本脚本创建它，收尾时删掉（不是还原成'本来就有'）")
    New-Item -ItemType Directory -Force -Path $native | Out-Null
    $script:NativeWasAbsent = $true
} else {
    $script:NativeWasAbsent = $false
}
$before = @(Get-ChildItem -LiteralPath $native -File -ErrorAction SilentlyContinue)
if ($before.Count -ne 0) {
    Say @("RC=2 未判：轮前 native/ 已有 $($before.Count) 件，dev 布局的对照臂不成立，先人工归因：")
    foreach ($f in $before) { Say @("   残留 $($f.Name)  $($f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))") }
    Write-Host 'RC=2 未判：native/ 轮前非空'
    exit 2
}
Say @("轮前 native/ 件数 = $($before.Count)（空）")

# ── 四枚工具的指纹锚点：仓内源 ↔ 已发布包里的 native/ 必须逐字相同 ──
$pkgNative = Join-Path $repo 'publish\TrueToneCap-v0.3.6-beta\native'
foreach ($n in $tools) {
    $srcP = Join-Path $toolsSrc $n
    $pkgP = Join-Path $pkgNative $n
    if (-not (Test-Path -LiteralPath $srcP)) {
        Say @("RC=2 未判：仓内源缺 $srcP")
        Write-Host "RC=2 未判：缺 $n"; exit 2
    }
    $a = Sha $srcP; $b = Sha $pkgP
    # ⚠ 这里以前写成 "$(if ($a -eq $b) { '逐字相同' } { '★ 不同…' })" —— 少一个 else。
    #   实测语义（qoder-scratch/psprobe/ifelse-probe.ps1）：相等那一支会把后一个块的字面量
    #   一起拼进串里 ⇒ 锚点行变成 `逐字相同  '★ 不同…'`，警报字样恒在（假红方向，不是漏判）。
    #   改成先算变量再插值，日志里只可能出现一种说法。
    if ($a -eq $b) { $mark = '逐字相同' } else { $mark = '★ 不同：本脚本模拟的布局不等于发布布局，结论要打折' }
    Say @("锚点 $n  仓内=$($a.Substring(0,16))  包内=$($b.Substring(0,16))  $mark")
}

# ── 设置指纹：测试进程与产品共用配置 ⇒ 跑产品能改用户机器 ──
$watch = @()
$appSet = Join-Path $dir 'TrueToneCap.settings.json'
if (Test-Path -LiteralPath $appSet) { $watch += $appSet }
$ldir = Join-Path $env:LOCALAPPDATA 'TrueToneCap'
if (Test-Path -LiteralPath $ldir) {
    $watch += @(Get-ChildItem -LiteralPath $ldir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.json' } | ForEach-Object { $_.FullName })
}
$snap = @{}
foreach ($p in $watch) { $snap[$p] = @{ Sha = Sha $p; Bytes = [IO.File]::ReadAllBytes($p) } }
Say @("轮前设置指纹 $($snap.Count) 份：" + (($snap.Keys | Sort-Object | ForEach-Object { "$(Split-Path -Leaf $_)=$($snap[$_].Sha.Substring(0,16))" }) -join '  '))

$outRoot = Join-Path $repo 'tests\output\publishlayout-selftest'
if (Test-Path -LiteralPath $outRoot) { Remove-Item -LiteralPath $outRoot -Recurse -Force }

$script:Arms = @()
function Run-Arm([string]$Name, [string]$ExpectMb) {
    $outDir = Join-Path $outRoot $Name
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $prog = Join-Path $outDir 'progress.txt'
    if (Test-Path -LiteralPath $prog) { Remove-Item -LiteralPath $prog -Force }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = $null
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList @('--selftest', '--out', $outDir) -PassThru -WorkingDirectory $dir
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            $sw.Stop()
            try { $p.Kill() } catch { }
            Say @("$Name ⇒ ❌ 超时 ${TimeoutSec}s 未退出 ⇒ 该臂记【未判】")
            $script:Arms += [pscustomobject]@{ Arm = $Name; Exit = '未判'; Mb = -1; Fail = -1; Ok = -1; Skip = -1; PerFmtMb = -1; Ms = $sw.ElapsedMilliseconds; Expect = $ExpectMb }
            return
        }
        $code = $p.ExitCode
    } catch {
        $sw.Stop()
        Say @("$Name ⇒ ❌ 起进程/等待失败：$($_.Exception.Message)")
        $script:Arms += [pscustomobject]@{ Arm = $Name; Exit = '异常'; Mb = -1; Fail = -1; Ok = -1; Skip = -1; PerFmtMb = -1; Ms = $sw.ElapsedMilliseconds; Expect = $ExpectMb }
        return
    }
    $sw.Stop()

    # 两把独立的尺子：① 汇总行里报的数（被测量自报）② 逐格式行自己数出来的 status 词
    $mb = -1; $fail = -1; $ok = -1; $skip = -1
    $perFmtMb = -1
    if (Test-Path -LiteralPath $prog) {
        $lines = @(Get-Content -LiteralPath $prog -Encoding UTF8)
        $sumLine = @($lines | Where-Object { $_ -match 'missingBackend=\d+.*fail=\d+' })
        if ($sumLine.Count -eq 1) {
            $l = $sumLine[0]
            if ($l -match 'ok=(\d+)')     { $ok = [int]$Matches[1] }
            if ($l -match 'skip=(\d+)')   { $skip = [int]$Matches[1] }
            if ($l -match 'missingBackend=(\d+)') { $mb = [int]$Matches[1] }
            if ($l -match 'fail=(\d+)')   { $fail = [int]$Matches[1] }
        } else {
            Say @("$Name ⇒ ★ 汇总行命中 $($sumLine.Count) 条（要求恰好 1 条）⇒ 汇总那一把尺子没判")
        }
        $perFmtMb = @($lines | Where-Object { $_ -match '^\[\d\d:\d\d:\d\d\.\d+\]\s+(PNG|TIFF|JPEG_LI|JPEG_XL|AVIF|WebP)\s+missingBackend\s' }).Count
        Say @("$Name 逐格式行（只列编码那六格；详情整段照录，不截断 —— 上一版按第 90 个字符切，把异常文案切成碎渣）")
        foreach ($l in $lines) {
            if ($l -match '^\[\d\d:\d\d:\d\d\.\d+\]\s+(PNG|TIFF|JPEG_LI|JPEG_XL|AVIF|WebP)\s+(\S+)\s+(\d+)ms\s*(.*)$') {
                Say @("   $($Matches[1]) $($Matches[2]) $($Matches[3])ms  $($Matches[4])")
            }
        }
        foreach ($l in $lines) { if ($l -match '分账不自洽') { Say @("   ★ 产品自己报了分账不自洽：$l") } }
    } else {
        Say @("$Name ⇒ ❌ 没有 progress.txt ⇒ 连一步都没落盘，本臂读数不可信（记【未判】）")
    }
    $script:Arms += [pscustomobject]@{ Arm = $Name; Exit = "$code"; Mb = $mb; Fail = $fail; Ok = $ok; Skip = $skip; PerFmtMb = $perFmtMb; Ms = $sw.ElapsedMilliseconds; Expect = $ExpectMb }
    Say @("$Name ⇒ 退出码=$code 墙钟=$($sw.ElapsedMilliseconds)ms  汇总: ok=$ok skip=$skip missingBackend=$mb fail=$fail  ｜ 逐格式行数=$perFmtMb（期望与汇总的 missingBackend 相等）")
}

try {
    # ── 臂 A：dev 布局（native/ 空）──
    Run-Arm 'A-emptyNative' 4

    # ── 臂 B：模拟发布布局（四枚齐全）──
    foreach ($n in $tools) {
        Copy-Item -LiteralPath (Join-Path $toolsSrc $n) -Destination $native -Force
        $a = (Sha (Join-Path $toolsSrc $n)); $b = (Sha (Join-Path $native $n))
        if ($a -ne $b) { Say @("★ 复制后指纹不一致 $n ⇒ 本臂作废"); }
    }
    $cntB = @(Get-ChildItem -LiteralPath $native -File).Count
    Say @("臂 B 前置：native/ 现有 $cntB 件（要求 4）")
    if ($cntB -eq 4) { Run-Arm 'B-publishLayout' 0 } else { Say @("臂 B ⇒ ❌ 前置没凑齐 4 件，本臂记【未判】") }

    # ── 臂 C：只有 cwebp ⇒ 分类必须按格式独立，不能按"目录非空"粗判 ──
    foreach ($n in $tools) {
        if ($n -ne 'cwebp.exe') { Remove-Item -LiteralPath (Join-Path $native $n) -Force -ErrorAction SilentlyContinue }
    }
    $cntC = @(Get-ChildItem -LiteralPath $native -File).Count
    Say @("臂 C 前置：native/ 现有 $cntC 件（要求 1：cwebp.exe）")
    if ($cntC -eq 1) { Run-Arm 'C-oneToolOnly' 3 } else { Say @("臂 C ⇒ ❌ 前置不对，本臂记【未判】") }
} finally {
    if ($LeaveNative) {
        Say @("收尾：带了 -LeaveNative ⇒ native/ 保持现状（$(@(Get-ChildItem -LiteralPath $native -File -ErrorAction SilentlyContinue).Count) 件）")
    } else {
        foreach ($n in $tools) { Remove-Item -LiteralPath (Join-Path $native $n) -Force -ErrorAction SilentlyContinue }
        $rest = @(Get-ChildItem -LiteralPath $native -File -ErrorAction SilentlyContinue)
        if ($rest.Count -eq 0) { $markR = '⇒ 已回到 dev 布局原状' } else { $markR = ('★ 仍有残留：' + (($rest | ForEach-Object { $_.Name }) -join ',')) }
        Say @("还原 native/：现件数 = $($rest.Count)（轮前 0）$markR")
        if ($script:NativeWasAbsent -and $rest.Count -eq 0) {
            Remove-Item -LiteralPath $native -Force -ErrorAction SilentlyContinue
            Say @("轮前 native/ 本不存在 ⇒ 已删除该目录")
        }
    }
    foreach ($p in $snap.Keys) {
        if ((Sha $p) -ne $snap[$p].Sha) {
            [IO.File]::WriteAllBytes($p, $snap[$p].Bytes)
            $re = (Sha $p).Substring(0, 16)
            Say @("设置 $($p) 被这一轮重写 ⇒ 已按原字节还原，复算前 16 位 = $re（轮前 $($snap[$p].Sha.Substring(0,16))）")
        }
    }
    $drift = @($snap.Keys | Where-Object { (Sha $_) -ne $snap[$_].Sha })
    if ($drift.Count -eq 0) { $markS = '⇒ 逐字相同 ✓' } else { $markS = ('★ 立即人工核对：' + ($drift -join ',')) }
    Say @("设置还原自检：$($snap.Count) 份，当前与轮前不一致的 = $($drift.Count) $markS")
}

# ── 判决 ──
$verdict = @(); $rc = 0
foreach ($a in $script:Arms) {
    $bad = @()
    if ($a.Mb -lt 0)              { $bad += '汇总没判到' }
    if ($a.Mb -ne $a.PerFmtMb)    { $bad += "两把尺子不一致（汇总 $($a.Mb) vs 逐格式 $($a.PerFmtMb)）" }
    if ($a.Mb -ne $a.Expect)      { $bad += "missingBackend=$($a.Mb) 与期望 $($a.Expect) 不符" }
    # 三臂都要求退出码 0：这正是分账改动的性质 —— 缺工具不该让进程失败。
    # 臂 A 若退出码变 1，就是"缺席又冒充失败"回来了。
    if ("$($a.Exit)" -ne '0') { $bad += "退出码=$($a.Exit)" }
    if ($a.Fail -ne 0)            { $bad += "fail=$($a.Fail) 非 0" }
    if ($bad.Count -gt 0) { $rc = 1; $verdict += "  ❌ $($a.Arm): $($bad -join '；')" }
    else { $verdict += "  ✅ $($a.Arm): ok=$($a.Ok) skip=$($a.Skip) missingBackend=$($a.Mb) fail=$($a.Fail) 退出码=$($a.Exit) 两把尺子一致" }
}
Say @("--- 判决 ---")
foreach ($v in $verdict) { Say @($v) }
Say @("RC=$rc")
Write-Host "RC=$rc  详情见 $Log"
Write-Host ($verdict -join "`n")
exit $rc
