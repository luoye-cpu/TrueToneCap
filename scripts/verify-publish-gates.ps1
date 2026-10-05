# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# verify-publish-gates.ps1 —— Publish.ps1 发布门禁的控制流回归测试
#
# 为什么需要它：
#   Publish.ps1 的两个门禁（[-1/6] 工作区干净、[1.5/6] 发布前测试）是"只在出包那天
#   才会走到的分支"。平时没人跑，一旦里面有个写错的变量名或 $LASTEXITCODE 用法问题，
#   要到正式发布时才炸。本脚本把外部工具打桩，让这些分支【每次都能被验证】。
#
# 原理：
#   PowerShell 中「函数」优先级高于「原生命令」，定义同名函数即可拦截 git/dotnet 调用，
#   脚本自身的分支、计数、报错信息则真实执行。
#
# ⚠️ 两个已踩过的坑（改本脚本时务必保留对应写法）：
#   1. 打桩函数必须用 global: 作用域。写在脚本作用域里的函数，对 & 调用的另一个脚本
#      【不可见】—— 实测 Publish.ps1 里用的仍是原生 git/dotnet，验证完全失效。
#   2. 共享状态必须用 $global: 而不是 $script:。在 global: 函数内部，$script: 指向
#      全局作用域，取不到调用方脚本作用域的变量，表现为"对 $null 调用方法"。
#
# 为什么不定 Remove-Item 的桩：
#   本环境的 Remove-Item 被 safe-delete 守卫拦截（SAFE_DELETE_FAIL_CLOSED 拒绝删除）。
#   改用【每次生成唯一的、不存在】的输出目录，使 Publish.ps1 的 Test-Path 为假、
#   根本不会走到 Remove-Item，比打桩更干净。
#
# 用法: .\scripts\verify-publish-gates.ps1
# 退出码: 0 = 全部场景符合预期；1 = 有场景不符

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$pub = Join-Path $RepoRoot 'Publish.ps1'
if (-not (Test-Path $pub)) { Write-Error "找不到 Publish.ps1: $pub"; exit 1 }

# ── 打桩 ──────────────────────────────────────────────────────
$global:VG_GitMode       = 'dirty'
$global:VG_DotnetRunExit = 0
$global:VG_DotnetCalls   = [System.Collections.Generic.List[string]]::new()

function global:git {
    $a = @($args)
    if ($a -contains 'status') {
        if ($global:VG_GitMode -eq 'dirty') { ' M Publish.ps1'; '?? docs/new.md' } else { $null }
    }
    elseif ($a -contains 'rev-parse') { 'abc1234' }
    else { '' }
    $global:LASTEXITCODE = 0
}

function global:dotnet {
    $a = @($args)
    if ($a -contains 'run') {
        # 注意：PowerShell 对【函数】会吃掉 `--` 分隔符（对原生命令不会），
        # 所以这里记录不到 `--`。属打桩假象，不代表真实调用缺 `--`。
        $global:VG_DotnetCalls.Add($a -join ' ')
        $global:LASTEXITCODE = $global:VG_DotnetRunExit

        # ⚠️ 桩必须输出 RESULT 行。发布门禁（Publish.ps1 [1.5/6]）早已不只看退出码：
        #    它逐个套件解析 "RESULT suite=... passed=N failed=M"，
        #    并且"退出码 0 但没有 RESULT 行"会被判为【零断言执行 → 失败】（防假绿灯）。
        #    本桩原先什么都不输出 → 门禁看到"7/7 个套件未通过"，
        #    于是 B/D 两个"应放行"的场景反而被拦下（回归保护失效）。
        #    故按被调用的套件名输出对应的模拟 RESULT；数字取当前真实门禁规模，
        #    保证合计 ≥ MinGateAssertions(340)。
        #    故按被调用的套件名输出对应的模拟 RESULT。
        # ⚠ 2026-09-29 审查轮 §11.4：原先这里的数字是【手抄】的（65/93/14/4/10/30/152，合计 368），
        #    注释还自称"取当前真实门禁规模"——真实规模早已 1400+，于是这个桩一边过期一边把
        #    Publish.ps1 那个 MinGateAssertions=340 的低下限"验证通过"了。现在改成读
        #    scripts\gate-baseline.json（由 verify.ps1 -WriteGateBaseline 生成），读不到才退回手抄值，
        #    并且话要说在前面：手抄值一生效就说明接线断了。
        if ($global:VG_DotnetRunExit -eq 0) {
            $suite = @($a) | Where-Object { $_ -like '--*-tests' } | Select-Object -First 1
            $stubBase = $null
            try {
                $bp = Join-Path $PSScriptRoot 'gate-baseline.json'
                if (Test-Path -LiteralPath $bp) {
                    $stubBase = Get-Content -LiteralPath $bp -Raw | ConvertFrom-Json
                }
            } catch { $stubBase = $null }
            $stubNum = $null
            if ($stubBase -and $suite -and $stubBase.各套件.$suite) { $stubNum = [int]$stubBase.各套件.$suite.断言 }
            if ($null -eq $stubNum) {
                Write-Output "  ⚠️ 桩读不到 gate-baseline.json 里的 $suite ⇒ 用手抄值（这个数字会漂，见本段注释）"
                $stubNum = switch ($suite) {
                    '--unit-tests'      { 65 }
                    '--color-tests'     { 93 }
                    '--encoding-tests'  { 14 }
                    '--service-tests'   { 4 }
                    '--ocr-tests'       { 10 }
                    '--format-tests'    { 30 }
                    '--usability-tests' { 152 }
                    default             { 0 }
                }
            }
            "RESULT suite=$suite passed=$stubNum failed=0 warnings=0 elapsedMs=- skipped=0"
        }
        return
    }
    if ($a -contains 'publish') { throw 'STUB_STOP_AT_PUBLISH' }  # 到此为止，不真的编译
    $global:LASTEXITCODE = 0
}

# ── 场景 ──────────────────────────────────────────────────────
function Invoke-Scenario {
    param(
        [string]$Name,
        [string]$GitMode,
        [int]$DotnetRunExit,
        [string]$ExpectThrow,        # 期望的异常信息前缀
        [int]$ExpectDotnetRuns,      # 期望的 dotnet run 调用次数
        [switch]$AllowDirty,
        [switch]$SkipTests
    )

    $global:VG_GitMode       = $GitMode
    $global:VG_DotnetRunExit = $DotnetRunExit
    $global:VG_DotnetCalls.Clear()

    # 唯一且不存在的输出目录 → 不会触发 Remove-Item（避开 safe-delete 守卫）
    #
    # ⚠️ 必须落在 publish\ 下、且目录名以 TrueToneCap-v 开头：
    #    Publish.ps1 的输出目录白名单（2026-09-15 新增）会拒绝其它位置 ——
    #    此前本脚本用 %TEMP%\pub-gate-xxx，结果 B/C/D/E 四个场景在
    #    "[1/6] 准备输出目录"就被白名单拦下，根本没走到被测的门禁分支，
    #    回归保护失效（且因"观察期不阻断"而长期无人发现）。
    #    这里改用合规路径，等价于真实发布时的目录形态。
    $out = Join-Path $RepoRoot ('publish\TrueToneCap-v0.0.0-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8))

    $thrown = $null
    try {
        if ($AllowDirty -and $SkipTests) { & $pub -Version '0.0.0-stub' -OutputDir $out -NoReadyToRun -AllowDirty -SkipTests }
        elseif ($AllowDirty)             { & $pub -Version '0.0.0-stub' -OutputDir $out -NoReadyToRun -AllowDirty }
        else                             { & $pub -Version '0.0.0-stub' -OutputDir $out -NoReadyToRun }
    } catch { $thrown = $_.Exception.Message }

    $script:results += [pscustomobject]@{
        Scenario      = $Name
        Threw         = if ($thrown) { $thrown } else { '(未抛异常)' }
        ExpectedThrow = $ExpectThrow
        DotnetRuns    = $global:VG_DotnetCalls.Count
        ExpectRuns    = $ExpectDotnetRuns
        Pass          = $false
    }
    $i = $script:results.Count - 1
    $script:results[$i].Pass = ($thrown -like "$ExpectThrow*") -and ($global:VG_DotnetCalls.Count -eq $ExpectDotnetRuns)

    if (Test-Path $out) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
}

$script:results = @()

Invoke-Scenario 'A 脏工作区/未授权 → 应拒绝发布' `
    -GitMode 'dirty' -DotnetRunExit 0 -ExpectThrow '工作区不干净' -ExpectDotnetRuns 0

# ⚠️ 期望的 dotnet run 次数必须与 Publish.ps1 的 $gateSuites 条目数【严格一致】。
#    2026-09-14 实测漂移：Publish.ps1 加入 --usability-tests 后为 7 个套件，
#    而本文件仍写 6 → 场景 B/C/D 全部判红；又因 verify.ps1 [1.6/2] 观察期只告警不阻断，
#    这个红拦不住任何人，观察期也就永远无法转绿。改 Publish.ps1 的套件列表时必改此处。
Invoke-Scenario 'B 干净工作区 → 应放行' `
    -GitMode 'clean' -DotnetRunExit 0 -ExpectThrow 'STUB_STOP_AT_PUBLISH' -ExpectDotnetRuns 7

Invoke-Scenario 'C 测试失败 → 门禁应阻断' `
    -GitMode 'dirty' -DotnetRunExit 1 -ExpectThrow '测试门禁失败' -ExpectDotnetRuns 7 -AllowDirty

Invoke-Scenario 'D 测试通过 → 应放行到发布步骤' `
    -GitMode 'dirty' -DotnetRunExit 0 -ExpectThrow 'STUB_STOP_AT_PUBLISH' -ExpectDotnetRuns 7 -AllowDirty

Invoke-Scenario 'E -SkipTests → 应跳过门禁' `
    -GitMode 'dirty' -DotnetRunExit 1 -ExpectThrow 'STUB_STOP_AT_PUBLISH' -ExpectDotnetRuns 0 -AllowDirty -SkipTests

# ── 汇总 ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════ 门禁验证结果 ════════"
$script:results | ForEach-Object {
    $mark = if ($_.Pass) { '✅' } else { '❌' }
    Write-Host "$mark $($_.Scenario)"
    Write-Host "      实际: $($_.Threw)  (dotnet run $($_.DotnetRuns) 次)"
    if (-not $_.Pass) {
        Write-Host "      期望: $($_.ExpectedThrow)  (dotnet run $($_.ExpectRuns) 次)" -ForegroundColor Red
    }
}

$failed = @($script:results | Where-Object { -not $_.Pass })
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "✅ 全部 $($script:results.Count) 个场景符合预期" -ForegroundColor Green
    exit 0
} else {
    Write-Host "❌ $($failed.Count)/$($script:results.Count) 个场景不符" -ForegroundColor Red
    exit 1
}
