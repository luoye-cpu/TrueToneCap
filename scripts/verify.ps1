# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap 一键校验（构建 + 测试）
#
# 背景（P0-1）:
#   项目此前没有任何自动化编排（无 .github / 无 git hook），Publish.ps1 里的脏工作区检查、
#   测试门禁、依赖校验【只在人主动发布时】才生效 —— 日常 commit/push 完全没有拦截。
#   本脚本把"发布前该做什么"从 README 的散文变成一条可执行命令，是接入 CI / git hook 的
#   最小落点（不需要 CI 服务器就能每天用）。
#
# 与 Publish.ps1 的关系:
#   · Publish.ps1 内部也跑同样的 8 个套件，但它只在发布时跑；本脚本随时可跑。
#   · 本脚本【不】做工作区/依赖校验 —— 那些只在发布语境下有意义。
#
# 设计要点:
#   · 先 build 一次，再直接跑测试宿主 exe（而不是每次都 dotnet run）
#     —— 6 个套件省下 6 次重复的构建判定，也避开了 CI 中 dotnet run 需要 --no-build 的坑；
#       Program.cs 也注明"直接跑 exe 更稳"（保证 WGC 子进程的 Environment.ProcessPath 正确）。
#   · 测试 exe 路径【不硬编码 TFM】—— 枚举 bin 下按 Configuration 过滤后取最新，
#     TFM / 输出路径形状变化都不会失效。
#   · 结果解析只看两种信号: 退出码（真值）与 `RESULT suite=… passed=… failed=…`（机器可读行）。
#     不要依赖人类可读的总结行 —— 历史上它们的格式有 3 种，正则统计会静默漏套件。
#
# 用法:
#   .\scripts\verify.ps1                    # 构建 + 8 个门禁套件（headless 安全）
#   .\scripts\verify.ps1 -IncludeDesktop    # 追加 --wgc-avif-tests（需真实桌面会话 + GPU）
#   .\scripts\verify.ps1 -SkipBuild         # 跳过构建，只跑测试
#   .\scripts\verify.ps1 -BuildOnly         # 只构建，不跑测试（pre-commit 等快速反馈场景）
#   .\scripts\verify.ps1 -Format            # 额外跑代码格式检查（仅报告，不阻断）
#   .\scripts\verify.ps1 -SkipPublishGates  # 跳过发布门禁回归（默认执行，见 [1.6/2]）
#   .\scripts\verify.ps1 -Configuration Debug
#
# 退出码: 0 = 全部通过；1 = 构建、发布门禁回归、外部判据、色彩管线判据或任一套件失败；2 = 参数组合无意义。
#   （[1.7/2] 外部判据在探测不到 python / ffprobe 时会打印 SKIP 并继续 —— 那是"这轮没判"，
#    不是通过；[1.8/2] 色彩管线判据在探测不到 cjxl / ultrahdr_app 时同理逐条点名 SKIP；
#    见 scripts/precision/README.md 的退出码约定。）
#
# 步骤: [1/2] 构建 → [1.5/2] 格式检查(可选) → [1.6/2] 发布门禁回归 → [1.7/2] 外部判据(可选)
#       → [1.8/2] 色彩管线判据(默认执行，无跳过开关) → [1.9/2] 宿主新鲜度
#       → [1.95/2] 闸门接线 → [1.96/2] 日志行格式对撞 → [1.97/2] App 侧进程内设置迁移判据
#       → [2/2] 逐套件执行

param(
    [string]$Configuration = "Release",

    # 追加 --usability-tests 与 --wgc-avif-tests（需要交互式桌面会话与 GPU）
    [switch]$IncludeDesktop = $false,

    # 跳过构建（已构建过、只想重跑测试时）
    [switch]$SkipBuild = $false,

    # 只构建、不跑测试。用于 pre-commit 之类要求快速反馈的场景（约 27s vs 全量约 110s）
    [switch]$BuildOnly = $false,

    # 显式跳过 [1.7/2] 外部判据（python/ffprobe/avifenc 那一层）。
    # ⚠ 这是"知道自己会少判一类问题"的开关，不是"让它安静过去"的开关：跳过时会打印一段
    #   红字级别的警告，因为这一步专抓"参数写了没人读 / 容器声明与像素不一致"这类自家看不见的回归。
    # ⚠ 它【不】影响 [1.8/2] 色彩管线判据 —— 那一步没有跳过开关。
    [switch]$SkipPrecision = $false,

    # 额外运行 dotnet format --verify-no-changes（【仅报告】，不阻断）
    [switch]$Format = $false,

    # 跳过「发布门禁回归」(scripts/verify-publish-gates.ps1)。默认【不跳过】——
    # 该脚本此前没有任何自动调用方，等于回归保护形同虚设。
    [switch]$SkipPublishGates = $false,

    # 把本轮各套件的断言/跳过数写成 scripts\gate-baseline.json（判据数量的基线，此后变少即红）。
    # 只在"确认这一轮是完整且合法的一轮"之后用；刻意删断言时要连同这份文件一起改，让删除留在 diff 里。
    [switch]$WriteGateBaseline = $false
)

# -SkipBuild 与 -BuildOnly 同时给是矛盾输入（一个跳构建、一个只要构建），
# 静默选一个语义都可能让人误以为跑过测试了 —— 直接报错更省心。
if ($SkipBuild -and $BuildOnly) {
    Write-Host "❌ -SkipBuild 与 -BuildOnly 不能同时使用（语义矛盾）" -ForegroundColor Red
    exit 2
}

# 双宿主兼容助手（Get-Sha256Hex）—— 为什么不直接用 Get-FileHash，见该文件头注释。
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "ps-compat.ps1")

# 宿主策略（2026-10-26 定版）：默认 pwsh 7.x，Windows PowerShell 5.1 基线回退。
# 这里只【报告】本轮宿主（门禁日志自证归属），不按版本号做任何放行/拒绝判定。
Write-Host "TrueToneCap 一键校验 —— 宿主: $(Get-TtcPsHostLabel)"

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$slnx     = Join-Path $RepoRoot "TrueToneCap.slnx"
$testProj = Join-Path $RepoRoot "src\TrueToneCap.Test\TrueToneCap.Test.csproj"

if (-not (Test-Path $slnx)) {
    Write-Host "❌ 未找到解决方案文件: $slnx" -ForegroundColor Red
    exit 1
}

# headless 8 套件（与 Publish.ps1 的发布门禁一致）
# 注: --usability-tests 自 2026-09-14 起已是 headless 安全的 —— 其中真正依赖显示器的
#     2 个用例会在无显示器时"显式跳过"（计入 skipped），不再导致失败。
$suites = @(
    "--unit-tests",
    "--color-tests",
    "--encoding-tests",
    "--service-tests",
    "--ocr-tests",
    "--qr-tests",
    "--format-tests",
    "--usability-tests"
)
if ($IncludeDesktop) {
    $suites += "--wgc-avif-tests"
    Write-Host "ℹ️  已追加 --wgc-avif-tests（需真实桌面会话 + GPU，无 GUI 环境会失败，属预期）" -ForegroundColor Yellow
}

Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  TrueToneCap 一键校验                            ║"
Write-Host "║  配置: $Configuration | 套件: $($suites.Count)"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1/2] 构建
# ══════════════════════════════════════════════════════════════
if ($SkipBuild) {
    Write-Host "[1/2] 构建 —— 已跳过 (-SkipBuild)"
} else {
    Write-Host "[1/2] 构建 $slnx ..."
    # ═══ 2026-09-29（批次 E）改成【必然重编】：+13.2 s 换掉一整类假红 ═══
    # 增量构建下，只改 App 的源码时 MSBuild 正确地跳过 Test/Tools（它们不引用 App），
    # 于是 [1.9/2] 那条"宿主不得早于任何构建输入"永远满足不了 —— 本轮就是这么废掉两轮的
    # （round4-e-batch-gate2 / gate4.log，两次都"整解构建 0 个错误"之后照样红）。
    # 尺子本身没错（错的是"宿主可能旧"这个担心），缺的是让它的口径【可满足】：
    # --no-incremental 让 MSBuild 用自己的依赖图把所有宿主都重写一遍，实测 13.2 秒
    # （`Measure-Command`，Release 全解）。此后这条判据只在"真的没重编"（-SkipBuild、
    # 构建失败、外部产物被人手动挪回来）时红 —— 那才是它想抓的东西。
    & dotnet build $slnx -c $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "❌ 构建失败 (退出码 $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Write-Host "   ✅ 构建通过"
}
Write-Host ""

if ($BuildOnly) {
    Write-Host "══════════════════════════════════════════════════"
    Write-Host "  ✅ -BuildOnly：构建通过，按参数要求【跳过全部测试】" -ForegroundColor Green
    Write-Host "     如需全量门禁请不带 -BuildOnly 运行（约 110s）" -ForegroundColor DarkGray
    Write-Host "══════════════════════════════════════════════════"
    Write-Host ""
    exit 0
}

# ══════════════════════════════════════════════════════════════
#  [1.5/2] 代码格式检查（可选，且【仅报告不阻断】）
# ══════════════════════════════════════════════════════════════
# 为什么默认不做、也不阻断:
#   .editorconfig 的风格规则 severity 一律是 suggestion —— 因为仓库开了
#   TreatWarningsAsErrors，一旦把风格规则提到 error 级就会打断构建。
#   所以格式问题只能靠单独的工具发现；先以"报告项"运行一段时间，
#   摸清真实差异量后再决定是否升级为阻断门禁。
if ($Format) {
    Write-Host "[1.5/2] 代码格式检查（仅报告）..."
    $fmtOut = & dotnet format $slnx --verify-no-changes --no-restore 2>&1 | Out-String
    $fmtCode = $LASTEXITCODE
    if ($fmtCode -eq 0) {
        Write-Host "   ✅ 无格式差异"
    } else {
        Write-Host "   ⚠️ 退出码 $fmtCode —— 存在格式差异，或该 SDK 不支持对 .slnx 运行 dotnet format" -ForegroundColor Yellow
        Write-Host "      ⚠️ 仅报告，不阻断（观察期）" -ForegroundColor DarkGray
        $fmtTail = ($fmtOut -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 6) -join "`n"
        if ($fmtTail) { Write-Host $fmtTail -ForegroundColor DarkGray }
    }
    Write-Host ""
}

# ══════════════════════════════════════════════════════════════
#  [1.6/2] 发布门禁回归（scripts/verify-publish-gates.ps1）
# ══════════════════════════════════════════════════════════════
# 为什么必须默认跑:
#   Publish.ps1 的两个门禁（[-1/6] 工作区干净、[1.5/6] 发布前测试）是"只在出包
#   那天才会走到的分支"。平时没人跑，里面写错一个变量名要到正式发版才炸。
#   2026-09-14 审查发现：该回归脚本【没有任何自动调用方】—— 不被 CI 引用、不被
#   git 钩子引用、也不被本脚本引用，回归保护形同虚设。故在此默认接入。
#
# 为什么必须用子进程隔离（不能点源 / & 同会话执行）:
#   该脚本用 `function global:git` 与 `function global:dotnet` 给外部命令打桩。
#   一旦在本会话内执行，这两个桩会一直生效 —— 之后本脚本的 `dotnet build`
#   就会调到桩函数上，构建被静默跳过。这是最难排查的一类假绿灯。
#
# 为什么不写死 pwsh / powershell:
#   self-hosted Windows 运行器默认只带 Windows PowerShell 5.1，写 `pwsh` 会
#   直接报"命令不存在"。改用【当前进程的可执行文件自身】启动子进程：既不依赖
#   具体宿主名，又能保证桩只活在子进程里。
if ($SkipPublishGates) {
    Write-Host "[1.6/2] 发布门禁回归 —— 已跳过 (-SkipPublishGates)"
} else {
    Write-Host "[1.6/2] 发布门禁回归（Publish.ps1 门禁分支）..."
    $gates = Join-Path $RepoRoot "scripts\verify-publish-gates.ps1"
    if (-not (Test-Path $gates)) {
        Write-Host "   ⚠️ 未找到 $gates —— 跳过该步（不阻断）" -ForegroundColor Yellow
    } else {
        $psExe = $null
        try { $psExe = (Get-Process -Id $PID).Path } catch { $psExe = $null }
        if (-not $psExe -or -not (Test-Path $psExe)) {
            Write-Host "   ⚠️ 无法确定 PowerShell 宿主路径 —— 跳过该步（不阻断）" -ForegroundColor Yellow
        } else {
            Write-Host "   ℹ️  以子进程隔离执行: $psExe"
            $gatesCode = 1
            $gatesText = ""
            try {
                # 刻意不写 `... | Out-String`：PS 7 在某些宿主组合下会把它解析成
                # "在管道中间运行文档"而报错。先取回对象数组再拼接，兼容性更好。
                $gatesOut  = & $psExe -NoProfile -NonInteractive -File $gates 2>&1
                $gatesCode = $LASTEXITCODE
                $gatesText = ($gatesOut | ForEach-Object { "$_" }) -join "`n"
            } catch {
                $gatesText = "调用异常: $($_.Exception.Message)"
            }
            if ($gatesCode -eq 0) {
                Write-Host "   ✅ 发布门禁回归通过（门禁分支行为符合预期）"
            } else {
                # 2026-09-15：观察期结束，升级为【阻断】。
                #   理由：2026-09-14 加的输出目录白名单、以及 RESULT 行校验，
                #   曾让本回归 4/5 场景失效，却因为"只告警不阻断"而一直无人发现 ——
                #   不阻断的门禁等于没有门禁。现已修正回归脚本并实测 5/5 通过。
                #   若某台机器因 PowerShell 宿主（5.1/7.x）或执行策略差异失败，
                #   可临时用 -SkipPublishGates 跳过并上报，不要直接改回只告警。
                Write-Host "   ❌ 发布门禁回归未通过 (退出码 $gatesCode)" -ForegroundColor Red
                $gatesTail = ($gatesText -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 10) -join "`n"
                if ($gatesTail) { Write-Host $gatesTail -ForegroundColor DarkGray }
                exit 1
            }
        }
    }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.7/2] 外部判据（可选）—— scripts/precision/*.py
# ══════════════════════════════════════════════════════════════
# 为什么要这一步:
#   上面 7 个套件全部只信【仓库自己的解析器】。而 2026-09-19 的 TIFF 事故证明"自己写的
#   解析器会复刻写入器自己的结构假设"—— 8 轮门禁全绿、外部解码器拒读（交接 §3 纪律的
#   容器层版本）。于是"参数有没有真的进码流""位深/色度有没有生效"这类回归对本门禁【完全
#   失明】；当时判据脚本还躺在被 gitignore 的 .workbuddy-ai/ 里，连"跑过"都不会发生。
#   现在它们入库为 scripts/precision/，并在此默认调用（样张由 Tools 的 --param-sweep 产出）。
#
# 为什么"可选"却仍然阻断:
#   ffmpeg/ffprobe 不是本仓库能自带的依赖，干净机器与 CI 上可能没有 ⇒ 探不到就【显式 SKIP
#   并打印原因】，让人看得见这一轮没判什么；但判据真的不通过 ⇒ 退出码非 0、整条门禁红。
#   脚本的退出码约定见 scripts/precision/precision_env.py：0 通过 / 1 失败 / 3 SKIP。
#
# 为什么不写死 `python`:
#   Windows 的 `python` 很可能是 WindowsApps 的 App Execution Alias 桩，它以退出码 49
#   静默失败（本机实测）。所以候选解释器要各自跑一次 `-c` 探针，只认"退 0 且有输出"的那个。
#   同理 ffprobe 路径也不在这里重复实现 —— 直接问 precision_env.find_tool()，保持单一事实源。

$precisionDir = Join-Path $RepoRoot "scripts\precision"
$precisionScript = Join-Path $precisionDir "chroma_knob_check.py"

# 返回 @{ Code = 退出码; Text = 合并后的输出 }；native 命令用 try/catch 兜住，
# 免得 $ErrorActionPreference=Stop 把 2>&1 的 stderr 变成终止性错误。
function Invoke-PrecisionPython {
    param([string]$Exe, [string[]]$Pre, [string[]]$Params)
    $argv = @($Pre) + @($Params)
    $code = 1
    $text = ""
    try {
        $raw = & $Exe @argv 2>&1
        $code = $LASTEXITCODE
        $text = ($raw | ForEach-Object { "$_" }) -join "`n"
    } catch {
        $text = "调用异常: $($_.Exception.Message)"
    }
    return @{ Code = $code; Text = $text }
}

# 通用版：跑一个 native 命令并把 stdout+stderr 合并回来，**不让 stderr 中止脚本**。
#
# 为什么必须在调用处把 $ErrorActionPreference 降到 Continue（而不是只包 try/catch）：
# Stop 之下 native 的一行 stderr 会被包成 NativeCommandError **终止性错误** ⇒ 整条 pipeline 当场作废，
# 而 try/catch 只是"接住了异常"，**子进程并没有被等完就被丢下** ⇒ 变成孤儿继续跑。
# 2026-09-19 实测过这条：门禁在 --encoding-tests 处被一条正常的 NVENC 隔离警告打断后，
# 那个 Test 进程又自己跑了 4 分钟才随整机蓝屏一起消失（nvlddmkm!_mapInsertFixup）。
# 所以这里既要不截断，也要**等完**：降到 Continue ⇒ stderr 成为非终止错误被 2>&1 收进管道，
# 进程正常等到退出码，$LASTEXITCODE 才是这条套件的真实结果。
function Invoke-CapturedNative {
    # ⚠⚠ 参数名绝不能叫 $Args：那是 PowerShell 的自动变量，`-Args @(...)` 的绑定不生效，
    #    函数里 splat 出去的是【空参数】——子进程收到零参数。实测（2026-09-19 夜）：
    #    七个测试套件因此全部只打印帮助并退出码 1，"测试宿主"分支看起来像"测试在跑但失败"，
    #    而零参数在别的日子完全可能被上层当成"没有报错"。同一文件的 python 侧用的是 -Params，
    #    所以判据一直正常 —— 两处命名不同，坏的那处静默。
    #    现在改名 $ArgList，并硬性拒绝零参数调用（零参数几乎总是调用点写错，不是"就该不带参数"）。
    param([string]$Exe, [string[]]$ArgList)
    if (-not $ArgList -or $ArgList.Count -eq 0) {
        throw "Invoke-CapturedNative 拒绝零参数调用 '$Exe' —— 大概率是参数没传进来，不是该带空参数"
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 1
    $text = ""
    try {
        $raw = & $Exe @ArgList 2>&1
        $code = $LASTEXITCODE
        $text = ($raw | ForEach-Object { "$_" }) -join "`n"
    } catch {
        # 走到这里只可能是"起不来"（路径/权限），不是子进程写了 stderr
        $text = "调用异常: $($_.Exception.Message)"
        $code = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
    } finally {
        $ErrorActionPreference = $prev
    }
    return @{ Code = $code; Text = $text }
}

# ── Tools 宿主：定位与调用（[1.7/2] 与 [1.8/2] 共用，两处各写一份迟早漂移）──
# 与测试宿主同一套"枚举 + 按 Configuration 过滤 + 取最新"，不硬编码 TFM。
function Resolve-ToolsHostExe {
    param([string]$BinRoot, [string]$Config)
    if (-not (Test-Path $BinRoot)) { return $null }
    return Get-ChildItem $BinRoot -Filter "TrueToneCap.Tools.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains $Config } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

# 跑 Tools 的一个模式，返回 @{Code;Text}。没有 exe 时退化为 dotnet run（多一次构建判定）。
function Invoke-ToolsMode {
    param($ToolsExe, [string]$Mode, [string[]]$Extra = @(), [string]$ToolsProj, [string]$Config)
    $modeArgv = @($Mode) + @($Extra | Where-Object { $_ })
    if ($ToolsExe) {
        return Invoke-CapturedNative -Exe $ToolsExe.FullName -ArgList $modeArgv
    }
    return Invoke-CapturedNative -Exe "dotnet" -ArgList (
        @("run", "--project", $ToolsProj, "-c", $Config, "--") + $modeArgv)
}

# ── 前置 1：可用的 python ──
$pyExe = $null; $pyPre = @()
foreach ($cand in @(@{ e = "python"; p = @() }, @{ e = "py"; p = @("-3") }, @{ e = "python3"; p = @() })) {
    if (-not (Get-Command $cand.e -ErrorAction SilentlyContinue)) { continue }
    $probe = Invoke-PrecisionPython -Exe $cand.e -Pre $cand.p -Params @("-c", "import sys;print(sys.version.split()[0])")
    if ($probe.Code -eq 0 -and "$($probe.Text)".Trim()) {
        $pyExe = $cand.e; $pyPre = $cand.p
        break
    }
}

# ── 前置 2：ffprobe（由脚本自己那套探测逻辑回答，避免两处规则分叉） ──
$ffprobePath = ""
if ($pyExe) {
    $probe = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params @(
        "-c", "import sys;sys.path.insert(0, r'$precisionDir');import precision_env as pe;print(pe.find_tool('ffprobe') or '')")
    $cand = "$($probe.Text)".Trim()
    # 只认"确实存在的文件"——探针自己报错（退出码 0 但吐的是 traceback）时按【没探到】处理，
    # 走 SKIP 分支，不能拿一句错误文本当路径继续往下跑。
    if ($probe.Code -eq 0 -and $cand -and (Test-Path $cand -PathType Leaf)) { $ffprobePath = $cand }
}

# ═══ 本轮"没判上"的清单（2026-09-29 审查轮 §11.4 补）═══
# 原来这些只打一行黄字，跑到最后一屏时看不到 ⇒ 一台没有 ffprobe 的机器照样能打印
# "✅ 7 个套件全部通过"，而那 12 条外部判据一条都没判。汇总末尾按这份清单回显，SKIP 才进得了结论行。
$script:GateGaps = @()

if ($SkipPrecision) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 已按 -SkipPrecision 显式跳过" -ForegroundColor Yellow
    Write-Host "   后果：本轮【没有】用外部解码器读回产物 ⇒ '参数写了没人读 / 声明与像素不一致 /" -ForegroundColor Yellow
    Write-Host "          容器实际交付几级 / 跨容器色域声明矛盾' 这四类回归全部未被判定，不是通过。" -ForegroundColor Yellow
    $script:GateGaps += "[1.7/2] 外部判据整步跳过（-SkipPrecision）⇒ 外部解码器读回一条没判"
} elseif (-not (Test-Path $precisionScript)) {
    Write-Host "[1.7/2] 外部判据 —— ❌ 未找到 $precisionScript（门禁接线断了）" -ForegroundColor Red
    exit 1
} elseif (-not $pyExe) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 跳过 (SKIP)" -ForegroundColor Yellow
    Write-Host "   原因: 未找到可用的 python（试过 python / py -3 / python3；WindowsApps 桩会以退出码 49 静默失败）" -ForegroundColor Yellow
    Write-Host "   ⚠️ 本轮【没有】用外部解码器读回产物 ⇒ '参数是否真的进码流'这一类回归未被判定，不是通过" -ForegroundColor DarkGray
    $script:GateGaps += "[1.7/2] 整步跳过（本机没有可用的 python）⇒ 外部解码器读回一条没判"
} elseif (-not $ffprobePath) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 跳过 (SKIP)" -ForegroundColor Yellow
    Write-Host "   原因: 未探测到 ffprobe（PATH 与 C:\ffmpeg*\bin、C:\Windows\ffmpeg-*\bin 等常见位置都没有；" -ForegroundColor Yellow
    Write-Host "         可用 TTC_FFPROBE 显式指定路径）" -ForegroundColor Yellow
    Write-Host "   ⚠️ 判据只信外部解码器，没有它就没有判据 ⇒ 本类回归未被判定，不是通过" -ForegroundColor DarkGray
    $script:GateGaps += "[1.7/2] 整步跳过（未探测到 ffprobe）⇒ 外部解码器读回一条没判"
} else {
    Write-Host "[1.7/2] 外部判据（用外部解码器读回产物，不信本仓库解析器）..."
    Write-Host "   ℹ️  python: $pyExe $($pyPre -join ' ')  |  ffprobe: $ffprobePath" -ForegroundColor DarkGray
    $env:PYTHONIOENCODING = "utf-8"

    # ── 生成样张：定位 Tools 宿主（与测试宿主同样的"枚举 + 按 Configuration 过滤 + 取最新"） ──
    $toolsProj = Join-Path $RepoRoot "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"
    $toolsExe  = Resolve-ToolsHostExe -BinRoot (Join-Path $RepoRoot "src\TrueToneCap.Tools\bin") -Config $Configuration

    $sweepText = ""
    $sweepCode = 1
    if ($toolsExe) {
        Write-Host "   ℹ️  样张宿主: $($toolsExe.FullName)" -ForegroundColor DarkGray
        $sweepText = (& $toolsExe.FullName "--param-sweep" 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $sweepCode = $LASTEXITCODE
    } else {
        Write-Host "   ⚠️ 未找到 TrueToneCap.Tools.exe，退化为 dotnet run（会多一次构建判定）" -ForegroundColor Yellow
        $sweepText = (& dotnet run --project $toolsProj -c $Configuration -- --param-sweep 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $sweepCode = $LASTEXITCODE
    }
    if ($sweepCode -ne 0) {
        Write-Host "   ❌ --param-sweep 生成样张失败 (退出码 $sweepCode)" -ForegroundColor Red
        $tail = ($sweepText -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 10) -join "`n"
        if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
        exit 1
    }

    # ── 样张落点由 src/TrueToneCap.Tools/FormatBench.cs 算死在 artifacts/precision/probe/<kind>，
    #    这里仍复制一份进 artifacts/precision/<kind> 再判 ⇒ 门禁判的始终是【当场产出、仓库内可见】的那一份，
    #    且绝不回退读陈旧样张（precision_env 在显式 --samples 时也不回退）。 ──
    $srcSweep = Join-Path $RepoRoot "artifacts\precision\probe\sweep"
    $dstSweep = Join-Path $RepoRoot "artifacts\precision\sweep"
    if (-not (Test-Path $srcSweep) -or -not @(Get-ChildItem $srcSweep -File -ErrorAction SilentlyContinue).Count) {
        Write-Host "   ❌ --param-sweep 退出码 0，但 $srcSweep 里没有样张 —— 判据无从执行（不能当通过）" -ForegroundColor Red
        exit 1
    }
    if (Test-Path $dstSweep) { Remove-Item $dstSweep -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $dstSweep | Out-Null
    Copy-Item -Path (Join-Path $srcSweep "*") -Destination $dstSweep -Force

    # ── 另两组样张：64 级紧邻台阶(ramp) 与 同图跨格式色域声明(cicp) ──
    # 与渐变样张互补：渐变只能验"参数进没进码流"，台阶才能数出"实际交付几级"，
    # 跨格式那组才能问"同一张图在三个容器里是不是被声明成了同一个色域"。
    foreach ($kind in @("ramp", "cicp")) {
        $mode = "--{0}-sweep" -f $kind
        if ($toolsExe) {
            $sText = (& $toolsExe.FullName $mode 2>&1 | ForEach-Object { "$_" }) -join "`n"
            $sCode = $LASTEXITCODE
        } else {
            $sText = (& dotnet run --project $toolsProj -c $Configuration -- $mode 2>&1 | ForEach-Object { "$_" }) -join "`n"
            $sCode = $LASTEXITCODE
        }
        if ($sCode -ne 0) {
            Write-Host "   ❌ $mode 生成样张失败 (退出码 $sCode)" -ForegroundColor Red
            $st = ($sText -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
            if ($st) { Write-Host $st -ForegroundColor DarkGray }
            exit 1
        }
        $srcK = Join-Path $RepoRoot "artifacts\precision\probe\$kind"
        $dstK = Join-Path $RepoRoot "artifacts\precision\$kind"
        if (-not (Test-Path $srcK) -or -not @(Get-ChildItem $srcK -File -ErrorAction SilentlyContinue).Count) {
            Write-Host "   ❌ $mode 退出码 0，但 $srcK 里没有样张 —— 判据无从执行（不能当通过）" -ForegroundColor Red
            exit 1
        }
        if (Test-Path $dstK) { Remove-Item $dstK -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Force -Path $dstK | Out-Null
        Copy-Item -Path (Join-Path $srcK "*") -Destination $dstK -Force
        if ($kind -eq "ramp") { $dstRamp = $dstK } else { $dstCicp = $dstK }
    }

    # ── 再两组样张：跨容器像素一致(wg) 与 容器自述审计(audit) ──
    # 这两条问的是【第三方解码器看到的像素】那一层 —— sweep/ramp/cicp 都停在"参数进没进码流 /
    # 声明成什么色域"上。2026-09-29 审查轮 §11.6 量到 wg_container_roundtrip / audit_roundtrip
    # 是写好了却没接线的孤儿裁判 ⇒ "外部解码像素"这一层在此之前从未进门禁。
    # 样张目录 precision_env 自己会找 artifacts\precision\probe\<kind>，所以不再拷第二份
    # （多拷一份只会多出一个"两份样张哪个是旧的"的问题）。
    foreach ($gen in @(
        @{ Kind = "audit"; Mode = "--container-audit" },
        @{ Kind = "wg";    Mode = "--wg-samples" }
    )) {
        $mode = $gen.Mode; $kind = $gen.Kind
        if ($toolsExe) {
            $gText = (& $toolsExe.FullName $mode 2>&1 | ForEach-Object { "$_" }) -join "`n"
            $gCode = $LASTEXITCODE
        } else {
            $gText = (& dotnet run --project $toolsProj -c $Configuration -- $mode 2>&1 | ForEach-Object { "$_" }) -join "`n"
            $gCode = $LASTEXITCODE
        }
        if ($gCode -ne 0) {
            Write-Host "   ❌ $mode 生成样张失败 (退出码 $gCode)" -ForegroundColor Red
            $gt = ($gText -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
            if ($gt) { Write-Host $gt -ForegroundColor DarkGray }
            exit 1
        }
        $dirK = Join-Path $RepoRoot "artifacts\precision\probe\$kind"
        if (-not (Test-Path $dirK) -or -not @(Get-ChildItem $dirK -File -ErrorAction SilentlyContinue).Count) {
            Write-Host "   ❌ $mode 退出码 0，但 $dirK 里没有样张 —— 判据无从执行（不能当通过）" -ForegroundColor Red
            exit 1
        }
        if ($kind -eq "audit") { $dstAudit = $dirK } else { $dstWg = $dirK }
    }

    $precisionFailed = $false
    $knownPending = @()      # 已知未修、只报告不阻断的判据（收尾时统一点名，不许悄悄吞掉）
    # 每条判据各吃自己那组样张（sweep = 渐变+参数；ramp = 64 级紧邻台阶）：
    # 混在一个目录里会让 glob 误收，且两类判据问的是两个不同的问题。
    #   · chroma_knob / param_sweep —— 参数有没有真的进码流（ffprobe 读回）
    #   · gamut_depth_consistency   —— 广色域矩阵与输出位深是否解耦（需 ffmpeg）
    #   · ramp_levels               —— 容器实际交付了几级（§29 指标②，杀"声明高位深实为 8-bit"）
    #   · cicp_consistency          —— 同一内容跨格式/后端的色彩声明是否互相一致（§29 指标④）
    # 带 Known 键的判据，其语义要读准：它是【已知未修 ⇒ 只报告不阻断】，不是"这条不算数"。
    # cicp_consistency 首跑查到 2 类真实不一致（AVIF 软件路对 Adobe 下发 --cicp 1/13/1；JXL HDR 把
    # Adobe 像素声明成别的色域），2026-09-19 两批修复后复跑：判定 28 文件 / 4 组、不符 0 项
    # （avifnv adobe 行现在是"无 primaries 码点 + ICC=有"，正是 H.273 无 Adobe 码点时唯一诚实的写法）
    # ⇒ Known 已按该脚本注释的约定摘掉，本条从今天起是阻断项。将来若再挂 Known，必须写清是哪一类
    #   未修、证据在哪，否则等于把判据调哑。
    $checks = @(
        @{ Script = "chroma_knob_check.py";        Samples = $dstSweep },
        @{ Script = "param_sweep_check.py";        Samples = $dstSweep },
        @{ Script = "gamut_depth_consistency.py";  Samples = $dstSweep },
        @{ Script = "ramp_levels.py";              Samples = $dstRamp },
        @{ Script = "cicp_consistency.py";         Samples = $dstCicp },
        @{ Script = "jpeg_icc_readback.py";        Samples = $dstCicp },
        # 2026-09-20：ICC 的【字节本身】——外部读回一致 + 结构自洽 + 跨格式/跨后端同源。
        # 上面两条判据只管"声明成什么色域"，管不到"嵌进去的那份 profile 是不是这个色域的"。
        @{ Script = "icc_readback_matrix.py";      Samples = $dstCicp },
        # 2026-09-20（交接 §32.9-4）：容器 av1C 的颜色记录 vs 码流真身。此前无人核对过 av1C，
        # 于是自研序列头解析器把 profile 0 恒 4:2:0 读成"该读两位"，三份硬件 AVIF 一律标成 4:4:4。
        @{ Script = "av1c_vs_bitstream.py";        Samples = $dstSweep },
        # ── 外部解码像素层（2026-09-29 审查轮 §11.6：这两条早就写好了，只是没人接线）──
        #   · audit_roundtrip          —— 用 ffmpeg/ffprobe（可选 libavif 的 avifdec）读回产品自述的
        #     容器与精度声明，判"它说的是不是真的"，含 16-bit 不写 sBIT 这类"宣称比交付强"的关注点；
        #   · wg_container_roundtrip   —— 同一张广色域图在 PNG/TIFF/AVIF/JXL 里被第三方解码成什么像素，
        #     跨容器必须一致（基准 = 该色域的 PNG）。09-19 的 TIFF 事故就是这一类。
        @{ Script = "audit_roundtrip.py";          Samples = $dstAudit },
        @{ Script = "wg_container_roundtrip.py";   Samples = $dstWg }
    )
    foreach ($check in $checks) {
        Write-Host "   ▶ $($check.Script)" -ForegroundColor DarkGray
        $r = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params @(
            (Join-Path $precisionDir $check.Script), "--samples", $check.Samples)
        if ($r.Code -eq 0) {
            Write-Host "     ✅ 通过"
        } elseif ($r.Code -eq 3) {
            Write-Host "     ⚠️ 跳过 (SKIP) —— 该脚本自己报的前置条件缺失" -ForegroundColor Yellow
            $why = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 4) -join "`n"
            if ($why) { Write-Host $why -ForegroundColor Yellow }
            # SKIP 必须进末屏的"本轮判据未覆盖"清单（2026-09-29 §11.4）：一条没人跑的判据
            # 与一条根本没写的判据，对使用者来说没有区别；只在中间刷一行黄字等于没说。
            $script:GateGaps += "[1.7/2] $($check.Script) 跳过 ⇒ 该层本轮未判"
        } elseif ($check.ContainsKey("Known")) {
            Write-Host "     ⚠️ 不通过，但按【已知未修】挂着不阻断（交接 §30.9）" -ForegroundColor Yellow
            $tail = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
            if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
            $knownPending += $check.Script
        } else {
            Write-Host "     ❌ 判据不通过 (退出码 $($r.Code))" -ForegroundColor Red
            $tail = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 12) -join "`n"
            if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
            $precisionFailed = $true
        }
    }
    # ── 不吃了向样张的判据（自带输入 / 纯仓库内比对）─────────────────────────
    # 2026-09-20 许可合规轮新增，三条都不需要 --samples：
    #   · gen-third-party-notices.py --check —— THIRD-PARTY-NOTICES.md 必须等于
    #     「licenses/ 全文 + INDEX.json」重新生成的结果，防"改了许可文本、清单还旧"。
    #     这条【没有 SKIP 分支】：它是阻断项。
    #   · icc_profile_id_check.py —— ICC ProfileID 配方对真实 profile 的实证；本机找不到
    #     可比对的 v4 profile 时脚本自己报 3=SKIP（那是"这轮没判"，不是通过）。
    #   · icc_selfbuilt_referee.py —— 用 lcms2 读回产品真正嵌进 PNG 的自产 profile（含
    #     "字节序改回小端必须失效"的反证）；缺 --color-tests 样张时同样报 SKIP。
    $probeDir = Join-Path $RepoRoot "scripts\probes"
    foreach ($extra in @(
        @{ Script = (Join-Path $RepoRoot "scripts\gen-third-party-notices.py"); Params = @("--check"); What = "许可清单与全文同步" },
        @{ Script = (Join-Path $RepoRoot "tests\manual\combo_locale_coverage_measure.py"); Params = @("--ceiling", "0", "0"); What = "下拉项本地化覆盖面天花板" },
        @{ Script = (Join-Path $probeDir "icc_profile_id_check.py");            Params = @(); What = "ICC ProfileID 配方实证" },
        @{ Script = (Join-Path $probeDir "icc_selfbuilt_referee.py");           Params = @(); What = "lcms2 读回自产 ICC" }
    )) {
        Write-Host "   ▶ $(Split-Path $extra.Script -Leaf)（$($extra.What)）" -ForegroundColor DarkGray
        $r = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params (@($extra.Script) + $extra.Params)
        if ($r.Code -eq 0) {
            Write-Host "     ✅ 通过"
        } elseif ($r.Code -eq 3) {
            Write-Host "     ⚠️ 跳过 (SKIP) —— 前置条件不满足，这轮没判" -ForegroundColor Yellow
            $why = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join "`n"
            if ($why) { Write-Host $why -ForegroundColor Yellow }
        } else {
            Write-Host "     ❌ 判据不通过 (退出码 $($r.Code))" -ForegroundColor Red
            $tail = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 10) -join "`n"
            if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
            $precisionFailed = $true
        }
    }

    # 已知未修的判据必须留在账面上：跑绿了 4 条不代表色彩声明这一类没问题。
    if ($knownPending.Count) {
        Write-Host "   ⚠️ 【已知未修，本轮不阻断】$($knownPending -join ', ') —— 见交接 §30.9，修完删掉 Known 标记即转阻断" -ForegroundColor Yellow
    }

    # ── 硬件 AVIF 的"声明 vs 像素"实测（2026-09-20，批次 8）──────────────────
    # 上面几条判的都是【容器写了什么】（ffprobe 读回来比对），这一条问的是
    # 【像素到底是被哪张矩阵、什么摆幅编出来的】——NVENC 的 RGB→YUV 由驱动自己做，
    # 容器声明得再漂亮，硬件没照做就是谎报；这种错只有解码回 YUV 才看得见。
    # 样张由 Tools 的 --nvenc-color-probe 现场产出（256x256 纯色带，含广色域 SDR 与 HDR/PQ 三档）。
    Write-Host "   ▶ nvenc_matrix_probe.py（NVENC 矩阵/摆幅实测）" -ForegroundColor DarkGray
    $ncSrc = Join-Path $RepoRoot "artifacts\precision\probe\nvenc_color"
    if ($toolsExe) {
        $ncText = (& $toolsExe.FullName "--nvenc-color-probe" 2>&1 | ForEach-Object { "$_" }) -join "`n"
    } else {
        $ncText = (& dotnet run --project $toolsProj -c $Configuration -- --nvenc-color-probe 2>&1 | ForEach-Object { "$_" }) -join "`n"
    }
    $ncCode = $LASTEXITCODE
    if ($ncCode -ne 0) {
        Write-Host "   ❌ --nvenc-color-probe 退出码 $ncCode（产样失败，判据无从执行）" -ForegroundColor Red
        $tail = ($ncText -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
        if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
        $precisionFailed = $true
    } else {
        $r = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params @(
            (Join-Path $RepoRoot "scripts\probes\nvenc_matrix_probe.py"), "--samples", $ncSrc)
        if ($r.Code -eq 0) {
            Write-Host "     ✅ 通过（硬件按声明的矩阵与摆幅转换）"
        } elseif ($r.Code -eq 3) {
            Write-Host "     ⚠️ 跳过 (SKIP) —— 本机无 NVENC 样张（无 N 卡或硬件编码被关）" -ForegroundColor Yellow
        } else {
            Write-Host "     ❌ 实测矩阵/摆幅与容器声明不一致 (退出码 $($r.Code))" -ForegroundColor Red
            $tail = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 14) -join "`n"
            if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
            $precisionFailed = $true
        }
    }
    # 判据不通过 ⇒ 当场阻断（与 [1.6/2] 一致）：这类失败正是"自己看自己"看不见的回归。
    if ($precisionFailed) {
        Write-Host ""
        Write-Host "❌ 外部判据未通过：参数在码流里没落地 / 容器读不回声明 / 台阶级数不达标 —— 见上方逐项" -ForegroundColor Red
        Write-Host "   样张: $dstSweep（渐变）| $dstRamp（台阶）" -ForegroundColor DarkGray
        exit 1
    }
    Write-Host "   ✅ 外部判据通过（样张: $dstSweep + $dstRamp）"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.8/2] 色彩管线判据（三件，默认执行、无跳过开关）
# ══════════════════════════════════════════════════════════════
# 这三件各证一件事，都不吃 --param-sweep 的样张，所以不并进 [1.7/2] 的 $checks 列表：
#   ① jxl_color_space_discrimination.py —— 外部编码器 cjxl 的 `-x color_space=<IDE名>` 确实影响
#      产物字节（A：sRGB/DisplayP3/RGB_D65_202_Rel_SRG 两两不同；B：假值必须报错且不出图，
#      B 是 A 的对照组 —— 没有它，"不同"可能来自别的原因）。
#   ② --gainmap-color-product —— 走产品自己的 GainMap 编码器落三枚文件，读回 base JPEG 的 ICC
#      原色做三格对照，并在第 4 层用 libultrahdr 的 ultrahdr_app 把三枚解回 RGBA 比对。
#   ③ --jxl-color-product —— 走产品自己的 JXL 入口证明 color_space 标签确实改变产物字节
#      （①的输入是手搓 PPM，而产品中转文件一旦自带色彩元数据，`-x` 会被 cjxl 完全忽略）。
#
# 为什么不并进 [1.7/2]：那一步的前置是 python + ffprobe，这三件的前置是 cjxl / ultrahdr_app。
#   共用一个前置判断会让"没装 ffmpeg"连带 SKIP 掉三条本可以判的判据 —— 那是更粗的假绿灯。
#
# 外部工具路径【只有一处事实源】：scripts/precision/precision_env.py 的 tool_candidates()
#   （与 ffprobe 同一套优先级：TTC_* 环境变量 > 候选表 / PATH）。这里问它要，C# 侧再按
#   "环境变量 > 命令行 > 仓库内候选 + PATH" 探测一遍。
#   ⚠ "文件存在"不等于"能用"：ultrahdr_app 有两代互不兼容的 CLI，第 4 层自己会做能力探测，
#     探不过就报 SKIP —— 不再像此前那样把那台机器上的某个构建目录硬编码在 C# 里。
#
# 退出码沿用 precision_env 的约定：0 = 判了且通过 / 1 = 判红（阻断）/ 3 = SKIP（不阻断，
#   但逐条明写"这一轮没判"，收尾还点名一次）。"退出码 0 但没判"和"通过"必须可区分。

$colorScript = Join-Path $precisionDir "jxl_color_space_discrimination.py"
$toolsProj2  = Join-Path $RepoRoot "src\TrueToneCap.Tools\TrueToneCap.Tools.csproj"
$toolsExe2   = Resolve-ToolsHostExe -BinRoot (Join-Path $RepoRoot "src\TrueToneCap.Tools\bin") -Config $Configuration

# 外部工具候选表：问 python 那处事实源要（不在这里重复实现一遍优先级规则）。
# 只认"确实存在的文件"——探针自己报错（退 0 但吐的是 traceback）时按【没探到】处理。
function Get-ExternalToolCandidates {
    param([string]$Tool)
    if (-not $pyExe) { return @() }
    $p = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params @(
        "-c", "import sys;sys.path.insert(0, r'$precisionDir');import precision_env as pe;print(';'.join(pe.tool_candidates('$Tool')))")
    $s = "$($p.Text)".Trim()
    if ($p.Code -ne 0 -or -not $s) { return @() }
    return @($s -split ';' | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) })
}

# 一条判据的三态结论 ⇒ 一行"判了 / 没判 / 判红"，其余调用方自己记账。
function Show-ColorVerdict {
    param([int]$Code, [string]$Text)
    if ($Code -eq 0) { Write-Host "     ✅ 判了，通过"; return "pass" }
    if ($Code -eq 3) {
        Write-Host "     ⚠️ 跳过 (SKIP) —— 这一层本轮【没判】，不是通过" -ForegroundColor Yellow
        # 只挑"为什么没判"那几行。⚠ 正则里别放裸的 `TTC_`：产物目录名 ttc_gainmap_color_probe
        #   会被 -match 的不区分大小写命中，把"产物留在 …"当成原因打出来（实测踩过一次）。
        $why = (($Text -split "`n" | Where-Object { $_ -match "⏭|跳过|没判|找过|候选|不认" } |
                    Select-Object -First 5) -join "`n")
        if (-not $why) { $why = (($Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join "`n") }
        if ($why) { Write-Host $why -ForegroundColor Yellow }
        return "skip"
    }
    Write-Host "     ❌ 判红 (退出码 $Code) —— 色彩声明在产品路径上没落地，见下方输出" -ForegroundColor Red
    $tail = (($Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 14) -join "`n")
    if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
    return "fail"
}

if (-not $pyExe -and -not $toolsExe2) {
    # 两条腿都没了才是接线问题：python 探不到只影响 ①，Tools 宿主探不到只影响 ②③，
    # 而两者【同时】没有 ⇒ 这一步一条都没判上，那是门禁坏了，不是环境缺件。
    Write-Host "[1.8/2] 色彩管线判据 —— ❌ 既没有可用的 python 也没有 Tools 宿主 ⇒ 三条一条都没判上" -ForegroundColor Red
    Write-Host "   接线断了（构建没产出宿主？），不接受'一条都没判还全绿'" -ForegroundColor Red
    exit 1
} else {
    Write-Host "[1.8/2] 色彩管线判据（三件：cjxl 标签判别 / GainMap 四层的第 4 层 / 产品 JXL 入口）..."
    # ⚠ 必须再套一层 @()：函数里 return @('一条') 会被 PowerShell 的输出管道摊平成【字符串】，
    #   于是 $cjxlCands[0] 取到的是第一个字符 'C' —— 实测把 'C' 当 cjxl 路径递下去，判据自己
    #   报了 SKIP。这类"路径变成单字母"的错只能靠显式包数组拦住，不能指望调用处记得。
    $cjxlCands  = @(Get-ExternalToolCandidates -Tool "cjxl")
    $uhdrCands  = @(Get-ExternalToolCandidates -Tool "ultrahdr_app")
    # 2026-09-27（交接 §62）：--jxl-color-product 新增的 HDR 层要用第三方读回【传递函数】，
    #   递空表等于那一层在门禁里恒 SKIP ⇒ "像素是 PQ、容器声明被 ICC 覆盖成 sRGB"这类错
    #   （§31.3-5，本轮实测成立并已修）永远判不到。候选表向 precision_env 要，不在这写死路径。
    #   ⚠ "文件在"≠"能用"：jxlinfo/djxl 是 libjxl 的构建产物，离开同目录那批 dll 就是
    #     0xC0000135（STATUS_DLL_NOT_FOUND）⇒ C# 侧对每个候选真跑一次、把退出码留下，全不可用才 SKIP。
    $jxlReadCands = @(@(Get-ExternalToolCandidates -Tool "jxlinfo") + @(Get-ExternalToolCandidates -Tool "djxl"))
    Write-Host "   ℹ️  cjxl: $(if ($cjxlCands) { $cjxlCands -join ' | ' } else { '(未探到)' })" -ForegroundColor DarkGray
    Write-Host "   ℹ️  ultrahdr_app: $(if ($uhdrCands) { $uhdrCands -join ' | ' } else { '(未探到 ⇒ 第 4 层会 SKIP)' })" -ForegroundColor DarkGray
    Write-Host "   ℹ️  JXL 第三方读回（HDR 层前置）: $(if ($jxlReadCands) { $jxlReadCands -join ' | ' } else { '(未探到 ⇒ HDR 层本轮不判)' })" -ForegroundColor DarkGray

    $colorFailed  = $false
    $colorSkipped = @()

    # ── ① 外部编码器 cjxl 的标签判别（自带输入，不吃样张）──
    Write-Host "   ▶ jxl_color_space_discrimination.py（-x color_space 是否真的改产物字节）" -ForegroundColor DarkGray
    if (-not $pyExe) {
        Write-Host "     ⚠️ 跳过 (SKIP) —— 没有可用的 python（试过 python / py -3 / python3）⇒ 这条本轮没判" -ForegroundColor Yellow
        $colorSkipped += "cjxl 标签判别"
    } elseif (-not (Test-Path $colorScript)) {
        Write-Host "     ❌ 未找到 $colorScript（门禁接线断了）" -ForegroundColor Red
        $colorFailed = $true
    } elseif (-not $cjxlCands) {
        Write-Host "     ⚠️ 跳过 (SKIP) —— 没探到 cjxl（候选表与 PATH 都没命中）⇒ 这条本轮没判" -ForegroundColor Yellow
        Write-Host "         补法：设 TTC_CJXL=<cjxl.exe>，或确认 src\TrueToneCap.Core\Resources\cjxl.exe 在检出里" -ForegroundColor Yellow
        $colorSkipped += "cjxl 标签判别"
    } else {
        $r = Invoke-PrecisionPython -Exe $pyExe -Pre $pyPre -Params @($colorScript, $cjxlCands[0])
        $v = Show-ColorVerdict -Code $r.Code -Text $r.Text
        if ($v -eq "fail") { $colorFailed = $true } elseif ($v -eq "skip") { $colorSkipped += "cjxl 标签判别" }
    }

    # ── ②③ 走产品自己入口的两条：把候选表整个递给 C#，由它逐个做能力探测 ──
    foreach ($m in @(
            @{ Mode = "--gainmap-color-product"; What = "产品 GainMap 编码器 + 外部参考解码器第 4 层"; Extra = $uhdrCands },
            @{ Mode = "--jxl-color-product";     What = "产品 JXL 入口的 color_space 落地 + HDR 层第三方读回传递函数"; Extra = $jxlReadCands })) {
        Write-Host "   ▶ $($m.Mode)（$($m.What)）" -ForegroundColor DarkGray
        $r = Invoke-ToolsMode -ToolsExe $toolsExe2 -Mode $m.Mode -Extra $m.Extra -ToolsProj $toolsProj2 -Config $Configuration
        $v = Show-ColorVerdict -Code $r.Code -Text $r.Text
        if ($v -eq "fail") { $colorFailed = $true } elseif ($v -eq "skip") { $colorSkipped += $m.Mode }
    }

    if ($colorFailed) {
        Write-Host ""
        Write-Host "❌ 色彩管线判据判红：color_space 标签没抵达编码器 / 增益图两模式没分开 /" -ForegroundColor Red
        Write-Host "   外部解码器看出的不是两张不同的图 —— 见上方逐项，这类错自家解析器看不见" -ForegroundColor Red
        exit 1
    }
    if ($colorSkipped.Count) {
        Write-Host "   ⚠️ 本轮【没判】的：$($colorSkipped -join '、')（$(3 - $colorSkipped.Count)/3 判了）" -ForegroundColor Yellow
        Write-Host "      这不是通过。补上外部工具（TTC_CJXL / TTC_ULTRAHDR_APP）再跑一遍才算判过。" -ForegroundColor Yellow
        $script:GateGaps += "[1.8/2] 色彩管线判据没判上：$($colorSkipped -join '、')"
    } else {
        Write-Host "   ✅ 色彩管线判据 3/3 全部判了且通过"
    }

    # ── ④ HDR AVIF 位深 A/B（2026-09-25：用户要求 libaom + HDR 可选 10/12）──
    # 判据落在【产物】上：请求 12 必须真出 yuv444p12le，且与请求 10 的字节不同、色彩声明不许丢。
    # 为什么必须进门禁：这条改动横跨"协商函数 / 管线 / PQ 量化 / avifenc 命令行"四处，
    # 漏改任何一处的表现都是"界面能选 12、文件仍是 10"—— 单看代码 diff 完全看不出来。
    # 实测还纠正了一处认知：真正决定输出位深的是中转 PNG 的 sBIT（avifenc 由输入推位深），
    # 命令行那个 --depth 在这条路上是冗余保险 ⇒ 变异实验只能红在量化那处，别误以为命令行有牙。
    Write-Host "   ▶ --avif-hdr-depth（HDR AVIF 位深 10 vs 12 的产物对撞）" -ForegroundColor DarkGray
    if ($ffprobePath) { $env:TTC_FFPROBE = $ffprobePath }
    $r = Invoke-ToolsMode -ToolsExe $toolsExe2 -Mode "--avif-hdr-depth" -Extra @() -ToolsProj $toolsProj2 -Config $Configuration
    $v = Show-ColorVerdict -Code $r.Code -Text $r.Text
    if ($v -eq "fail") {
        Write-Host "❌ HDR 位深判红：请求 12-bit 没产出 12-bit 码流（或两次产物逐字节相同 ⇒ 参数没抵达编码器）" -ForegroundColor Red
        exit 1
    }
    # ④ 此前只处理 fail：跳过时既不进上面的"没判"名单、也不进分母 ⇒ "没判"被读成"判过了"。
    if ($v -eq "skip") { $script:GateGaps += "[1.8/2]④ --avif-hdr-depth 位深 A/B 没判上（缺外部件）" }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  定位测试宿主 exe（枚举 + 过滤 + 取最新，不硬编码 TFM）
# ══════════════════════════════════════════════════════════════
$testBinRoot = Join-Path $RepoRoot "src\TrueToneCap.Test\bin"
$testExe = $null
if (Test-Path $testBinRoot) {
    $testExe = Get-ChildItem $testBinRoot -Filter "TrueToneCap.Test.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains $Configuration } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
if ($testExe) {
    Write-Host "   ℹ️  测试宿主: $($testExe.FullName)"
} else {
    Write-Warning "   ⚠️ 未找到 TrueToneCap.Test.exe（已搜索 $testBinRoot 下含 '$Configuration' 路径段的全部匹配）"
    Write-Host "      将退化为 dotnet run（每个套件都会多一次构建判定，且更慢）" -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════
#  [1.9/2] 宿主新鲜度：构建【成功】不等于跑的是这份源码
# ══════════════════════════════════════════════════════════════
# 2026-09-21 实机踩到：两轮"全门禁 8 套件全绿"读的是一个缺了约 46 条断言的旧 Test 宿主 ——
# 构建那一步照样打印"0 个警告 0 个错误 ✅ 构建通过"，因为 MSBuild 判定"无需重编"就跳过了它。
# 与已记在案的"构建失败即作废 RESULT 读数"是同一家族，只是更难发现：失败是绿的，成功也是绿的。
# 判据只有一条可执行的：宿器的写入时间必须不早于任何一份输入源码。
$srcRoot = Join-Path $RepoRoot "src"
$hostsToCheck = @()
if ($testExe) { $hostsToCheck += $testExe }
$toolsExe = Get-ChildItem (Join-Path $srcRoot "TrueToneCap.Tools\bin") -Filter "TrueToneCap.Tools.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -split '\\' -contains $Configuration } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($toolsExe) { $hostsToCheck += $toolsExe }

if ($hostsToCheck.Count -gt 0 -and (Test-Path $srcRoot)) {
    # 宿主的"新鲜度"看**它所在输出目录里最晚的一次写入**：
    # ① 只改托管代码时 MSBuild 重写 .dll 而不动 apphost(.exe) ⇒ 拿 .exe 比会每次正常构建都假红；
    # ② 只改被引用的项目（如 Core）时宿主自己的 .dll 也不必重写 ⇒ 拿单个 .dll 比照样假红。
    # 真正要问的是"构建在源码之后动过手没有"，那就是目录里最晚的时间戳。
    $dirStamp = {
        param ($h)
        $files = Get-ChildItem $h.DirectoryName -File -ErrorAction SilentlyContinue
        if (-not $files) { return $h.LastWriteTime }
        ($files | Measure-Object -Property LastWriteTime -Maximum).Maximum
    }
    # ═══ 输入面（2026-09-29 审查轮 §11.4 扩）═══
    # 原来只收 *.cs/*.xaml ⇒ 改 App/Shaders/*.hlsl 的曲线公式、换 Core/Resources 里那四枚
    # 随包编码器 exe（avifenc/cjxl/cwebp/cjpegli，正是 [1.7/2] 判据的真身）、动 csproj/props
    # 或 tests/fixtures 的 ICC，宿主都可以是旧的而门禁全绿。判据不看"是不是 C#"，看"是不是构建输入"。
    $inputInclude = @(
        '*.cs', '*.xaml', '*.hlsl', '*.csproj', '*.props',
        '*.manifest', 'Directory.Build.props', 'Directory.Packages.props'
    )
    $inputs = @(
        Get-ChildItem $srcRoot -Include $inputInclude -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
        # 随包原生产物：换了它们等于换了被测对象本身，必须进新鲜度面。
        # 2026-10-31 起含可选 deflate.dll（改了它同样是被测对象变了）。
        Get-ChildItem (Join-Path $srcRoot "TrueToneCap.Core\Resources") -Include '*.exe','*.dll' -Recurse -File -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $RepoRoot "tests\fixtures") -Include '*.icc', '*.icm', '*.png' -Recurse -File -ErrorAction SilentlyContinue
    )
    $newest = $inputs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    # ═══ 空输入集不许判成"新鲜"═══
    # 原先的过滤条件写的是 `$newest -and …`：$inputs 一旦为空（srcRoot 挪了、通配没命中、
    # 递归被 -ErrorAction 吞掉），$newest 就是 null ⇒ 每个宿主都不进 staleHosts ⇒ 打印
    # "✅ N 个宿主都不早于 0 份构建输入"。这是本仓反复拦的那一类：守卫自己失明时必须是 ❌，不能是 ✅。
    if ($inputs.Count -eq 0 -or $null -eq $newest) {
        Write-Host ""
        Write-Host "❌ 构建输入枚举为空（实得 $($inputs.Count) 份）⇒ 宿主新鲜度这一格【没判】，按失败处理" -ForegroundColor Red
        Write-Host "   没有『最新的源码』可比，就等于上面所有 RESULT 读数都可能出自旧构建（§11.4 那条『产物是旧的就作废』）" -ForegroundColor Red
        $script:GateGaps += "[1.9/2] 构建输入枚举为空 ⇒ 宿主新鲜度未判，本轮 RESULT 不可信"
        if (-not $SkipBuild) { exit 1 }
        Write-Warning "   本轮带了 -SkipBuild，按参数约定降级为警告（结论仍请自行担保）"
    }
    # ═══ 2026-09-29（批次 E）这里曾经改成"按宿主算输入闭包"，实测【撤回】═══════════════════
    # 动机是真的：只改 App 的源码时，MSBuild 正确地跳过重编 Test/Tools（它们不引用 App），
    # 于是这两个宿主的目录时间戳永远早于那次编辑 ⇒ 全局口径【必然】假红（本轮 gate2/gate4 两轮
    # 都是这样废掉的，而且【整解构建照样 0 个错误】—— 上一版报告把这件事记成"我局部构建的错"，
    # 那只对了一半，见 §14.6）。但我写的闭包版把 Core 漏了：腿 B 只碰 Core 的源码，
    # Test/Tools 两腿都判绿（`freshness-two-leg2.log`，输入数 13 / 25 恰好等于各自项目自己的
    # .cs+csproj 数，Core 那 47 份一份没进去）。PowerShell 函数返回 HashSet 会被解流成字符串数组，
    # 加上 csproj 叶子名在不同目录下不同，两层坑叠在一起就得到一个【安静漏判】的尺子 ——
    # 比假红危险得多。撤回全局口径，把力气花在"让这条口径必然可满足"：见上面 [1/2] 的 --no-incremental。
    $staleHosts = @($hostsToCheck | Where-Object { $newest -and (& $dirStamp $_) -lt $newest.LastWriteTime })
    if ($staleHosts.Count -gt 0) {
        Write-Host ""
        Write-Host "❌ 宿主比源码旧 ⇒ 随后所有 RESULT 读数都属于旧构建，本轮结论作废" -ForegroundColor Red
        Write-Host "   最新的源码: $($newest.FullName)  ($($newest.LastWriteTime.ToString('HH:mm:ss')))"
        foreach ($h in $staleHosts) {
            Write-Host ("   滞后的宿主: {0}  (输出目录最晚写入 {1:HH:mm:ss})" -f $h.FullName, (& $dirStamp $h))
        }
        Write-Host "   修法: dotnet build $slnx -c $Configuration --no-incremental" -ForegroundColor Yellow
        if ($SkipBuild) {
            # -SkipBuild 是调用方明确说"我知道，就用现有的产物" ⇒ 只警告，不改语义
            Write-Warning "   本轮带了 -SkipBuild，按参数约定降级为警告（结论仍请自行担保）"
        } else {
            exit 1
        }
    } elseif ($inputs.Count -gt 0) {
        Write-Host "   ✅ 宿主新鲜度: $($hostsToCheck.Count) 个宿主都不早于 $($inputs.Count) 份构建输入"
    } else {
        # 上面已经点过名了，这一句存在的意义是【不许在这里打印 ✅】：$newest 为 null 时
        # staleHosts 恒空，绿线照打就是"没判"被读成"判过且通过"。
        Write-Host "   ⏭ 宿主新鲜度：本轮未判（构建输入为空，见上面那条 ❌/警告）" -ForegroundColor Yellow
    }
} else {
    # ═══ 整块被跳过也必须是"没判"，不是"判过且通过" ═══
    # 这一格成立的前提是"找得到宿主 + 找得到 src 目录"，两者任一为空时原先【一个字都不印】就往下走
    # ⇒ 换台机器/改配置名，§11.4 立起来的这条判据会安静消失，而门禁照样收 ✅。
    $why = if (-not (Test-Path $srcRoot)) { "找不到构建输入根目录 $srcRoot" }
           else { "一个宿主 exe 都没定位到（Test=$([bool]$testExe) / Tools=$([bool]$toolsExe)，配置名 $Configuration 对不对？）" }
    Write-Host "❌ 宿主新鲜度整块未执行 ⇒ 这一格【没判】：$why" -ForegroundColor Red
    $script:GateGaps += "[1.9/2] 整块跳过（$why）⇒ 宿主新鲜度未判，上面所有 RESULT 可能是旧构建"
    if (-not $SkipBuild) { exit 1 }
    Write-Warning "   本轮带了 -SkipBuild，按参数约定降级为警告（结论仍请自行担保）"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.95/2] 闸门接线自检：commit / push 时这两道究竟会不会跑
# ══════════════════════════════════════════════════════════════
# 起因（2026-09-29 审查轮 §11.6，当场量到）：README:153-154 与 :335 承诺 pre-commit 挡编译、
# pre-push 挡测试，而本机 core.hooksPath 指向 .git\hooks（那里只有 Qoder 的 post-* 跟踪钩子），
# 仓库自带的两个钩子在 scripts\git-hooks 里躺着【一个都不执行】⇒ 除了手工敲这条脚本，
# 今天没有任何自动闸门。"钩子还在仓里"与"钩子在跑"是两件事，后者必须被判而不是被相信。
$hooksWant = Join-Path $RepoRoot 'scripts\git-hooks'
$hooksNow = ''
try { $hooksNow = (& git -C $RepoRoot config --local --get core.hooksPath 2>$null) | Out-String } catch { }
$hooksNow = $hooksNow.Trim()
# ⚠ 先把"实际生效的路径"单独算出来再比较。第一版写成 `(($resolved -or $hooksNow) -ieq $hooksWant)`，
#   括号把 -or 折成布尔 ⇒ -ieq 左侧变 [bool] ⇒ 右侧字符串被转成 $true ⇒ 非空路径一律判"已接线"，
#   这把尺子第一次跑就打了 ✅ 而实况是根本没接（本轮自己造的恒真判据，见报告 §12）。
$hooksEff = $hooksNow
$resolvedHook = (Resolve-Path -LiteralPath $hooksNow -ErrorAction SilentlyContinue)
if ($resolvedHook) { $hooksEff = [string]$resolvedHook.Path }
$hooksWired = ($hooksNow -ne '' -and $hooksEff -ieq $hooksWant -and
    (Test-Path -LiteralPath (Join-Path $hooksWant 'pre-commit')) -and
    (Test-Path -LiteralPath (Join-Path $hooksWant 'pre-push')))
$inCI = ($env:CI -eq 'true' -or $env:GITHUB_ACTIONS -eq 'true')
if ($hooksWired) {
    Write-Host "[1.95/2] 闸门接线: ✅ core.hooksPath → scripts\git-hooks（pre-commit / pre-push 会跑）"
} elseif ($inCI) {
    Write-Host "[1.95/2] 闸门接线: ℹ️  CI 环境不装本机钩子（跳过这一格，不影响结论）" -ForegroundColor DarkGray
} else {
    Write-Host "[1.95/2] 闸门接线: ⚠️  本机钩子【没接】—— commit 不会构建、push 不会跑测试" -ForegroundColor Yellow
    Write-Host "   当前 core.hooksPath = '$hooksNow'（期望 $hooksWant）" -ForegroundColor Yellow
    Write-Host "   ⇒ 本轮之外的一切提交/推送都只靠有人手工跑这条脚本；README 的承诺目前不成立" -ForegroundColor Yellow
    Write-Host "   接法: .\scripts\install-git-hooks.ps1   （只读检查用 -Status；它写 git 配置，需维护者同意）" -ForegroundColor DarkGray
    $script:GateGaps += "[1.95/2] 提交闸门未接线（core.hooksPath ≠ scripts\git-hooks）⇒ 除手工外无自动判据"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.96/2] 日志行格式对撞：写方(LogService) 与 读方(LogViewer 的 LogParser) 必须仍互认
# ══════════════════════════════════════════════════════════════
# 起因（2026-09-29 审查轮 §10.3 V12）：产品 2026-09-21 起给一次操作期间写的行加了 `#opId ` 前缀，
# 而日志查看器的两条正则要求级别方括号后【紧跟】[分类/Tag] ⇒ 那批行整条判 null、在查看器里
# 一条都不显示，且不报错。两个进程之间的事实没有编译期耦合，只能靠对撞判。
# 尺子从源码里现取正则（不抄常数），带一正两负三条臂；读不到 pattern 或臂不对 ⇒ 退非零。
$logLineProbe = Join-Path $RepoRoot 'scripts\probes\logline_format_check.ps1'
if (-not (Test-Path -LiteralPath $logLineProbe)) {
    Write-Host "[1.96/2] 日志行格式对撞 —— ❌ 脚本不在（$logLineProbe，门禁接线断了）" -ForegroundColor Red
    exit 1
}
Write-Host "[1.96/2] 日志行格式对撞（写方 FormattedLine ↔ 读方 LogParser 正则）..."
# ── 缺输入时用产品自己生成，而不是判红 ──
# 这一格要一份"真实产品日志"作对照臂，而探针只从 src\TrueToneCap.App\bin 下找 app_*.log。
# 干净 checkout（bin 刚重建 / 刚清过）里一份都没有 ⇒ 判据会因为"红的是缓存"而不是"格式对不上"。
# 这与 2026-10-21 修 notices 那条的判据同类：干净树上必然判红的格子等于不存在。
# 生成方式用 --window-probe（产品真跑一次、退出码可判、不碰用户设置文件）——
# 不用 --settings-migration，因为后者会 Load() 并可能重写设置。
$logScanRoot = Join-Path $RepoRoot 'src\TrueToneCap.App\bin'
$seedLogs = @(Get-ChildItem -LiteralPath $logScanRoot -Filter 'app_*.log' -Recurse -File -ErrorAction SilentlyContinue)
if ($seedLogs.Count -eq 0) {
    Write-Host "   ℹ️  bin 下没有历史日志（干净 checkout）⇒ 让产品自己跑一次生成真实对照件"
    $seedExe = Get-ChildItem $logScanRoot -Filter "TrueToneCap.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains $Configuration } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $seedExe) {
        Write-Host "❌ [1.96/2] 既没有 app_*.log 也没有 TrueToneCap.exe ⇒ 这一格【没判】" -ForegroundColor Red
        exit 1
    }
    $seedProc = Start-Process -FilePath $seedExe.FullName -ArgumentList '--window-probe' -PassThru -WindowStyle Hidden
    if (-not $seedProc.WaitForExit(60000)) {
        $seedProc.Kill()
        Write-Host "❌ [1.96/2] --window-probe 超时 ⇒ 没有生成对照日志，这一格【没判】" -ForegroundColor Red
        exit 1
    }
    $seedLogs = @(Get-ChildItem -LiteralPath $logScanRoot -Filter 'app_*.log' -Recurse -File -ErrorAction SilentlyContinue)
    if ($seedLogs.Count -eq 0) {
        Write-Host "❌ [1.96/2] --window-probe 退出码 $($seedProc.ExitCode) 但没写出日志 ⇒ 这一格【没判】" -ForegroundColor Red
        exit 1
    }
    Write-Host "    ✅ 已由产品生成对照日志（退出码 $($seedProc.ExitCode)）"
}
# 宿主跟随调用者（与 [1.6/2] 同一纪律）：默认 pwsh 7.x，从 5.1 会话跑才是 5.1。
# 2026-10-26 修正：原写死 `powershell` —— pwsh 会话里偷偷换宿主，且 pwsh-only 机器
# （Win11 可选卸载 5.1）上这条阻塞步恒红，与 [1.6/2] "不写死宿主"的口径自相矛盾。
$logLineOut = & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $logLineProbe 2>&1
$logLineCode = $LASTEXITCODE
($logLineOut | ForEach-Object { "   $_" }) -join "`n" | Write-Host
if ($logLineCode -ne 0) {
    Write-Host "❌ 日志行格式两边对不上 ⇒ 日志查看器会静默丢行（排障证据消失），本轮结论作废" -ForegroundColor Red
    exit 1
}
Write-Host "   ✅ 行格式互认（含 #opId 段），两条负样本仍被拒"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.97/2] App 侧进程内判据：TrueToneCap.exe --settings-migration
# ══════════════════════════════════════════════════════════════
# 为什么必须补这一格：Test 工程只引用 Core ⇒ 迁移门 / 越界归一回落 / GainMap 重映射这三条
# 设置侧自检（跑在用户机器上的那一份 App 代码）在上面的 7 个套件里【一条都判不到】，
# 台账上"App 侧运行时行为 T1=0"记的就是这个洞。而这三条正是 2026-09-16 那次
# "老文件的 QualityAvif=63 一路活到界面显示 CRF: 63"事故的正主（交接 §49）。
# 实测成本 265–672ms（tests\output\app-selftest-cost-cell.log 与
# tests\output\app-selftest-backends-cell.log，均退 0）⇒ 便宜到没有理由不进门禁。
#
# 三条纪律都是本仓踩过坑之后定的，不是随手写的：
#  · --out 指进仓库：这台机器的清理器会秒删 %TEMP% 里新建的文件，证据历史上真丢过；
#    不给 --out 时 SelfTest 会回落 %TEMP%\TrueToneCap_SelfTest，所以这一格【必传】。
#  · 跑前拍设置文件 sha256、跑后按原字节还原：SettingsService.Load() 会重写用户那份
#    TrueToneCap.settings.json（迁移门触发时 Load() 末尾 if (migrated) Save()），
#    门禁不许为此改动用户机器的设置。
#  · 超时 ⇒ 记【未判】并红：这道闸门的规矩是"没判上绝不允许落成绿"。
#
# ⚠ 用 Start-Process + WaitForExit(ms) 而不是 Invoke-CapturedNative：后者没有超时口径，
#   一旦进程挂住整条门禁就停在这一格（而且 $ErrorActionPreference=Stop 下 native 的
#   一行 stderr 会被包成终止性错误 —— 那正是 Invoke-CapturedNative 存在的原因）。
#   这里既不截断 stderr 也不等不完：Start-Process 自带超时，等不到就 Kill。
$appBinRoot = Join-Path $RepoRoot "src\TrueToneCap.App\bin"
$appExe = Get-ChildItem $appBinRoot -Filter "TrueToneCap.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -split '\\' -contains $Configuration } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$appModeOut = Join-Path $RepoRoot "tests\output\app-settings-migration"
$appModeTimeoutSec = 60

Write-Host "[1.97/2] App 侧设置迁移判据（进程内跑 --settings-migration，判据=退出码）..."

if (-not $appExe) {
    # 定位不到宿主 = 这一格整块没跑，绝不能当成"没有失败所以通过"（[1.9/2] 同一口径）
    Write-Host "   ❌ 未找到 TrueToneCap.exe（已搜索 $appBinRoot 下含 '$Configuration' 路径段的全部匹配）" -ForegroundColor Red
    Write-Host "      ⇒ 这一格【没判】：App 侧三条设置自检从此没有任何自动判据，按失败处理" -ForegroundColor Red
    $script:GateGaps += "[1.97/2] 未定位到 App 宿主 exe ⇒ --settings-migration 未判"
    exit 1
}

$settingsPath = Join-Path $appExe.DirectoryName 'TrueToneCap.settings.json'
function Get-Sha256OrAbsent([string]$p) {
    if (Test-Path -LiteralPath $p) { Get-Sha256Hex $p } else { 'ABSENT' }
}
# 轮前的原件按【字节】留着：还原只信原字节，不信"看起来一样"
$settingsBytes = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$settingsSha0 = Get-Sha256OrAbsent $settingsPath

# ⚠ 2026-10-05：这一格的 Start-Process 必须带输出重定向，否则在本机【会挂住整条门禁】。
#   实测（round69）：不带 -RedirectStandardOutput/Error 时，WaitForExit(60000) 永不返回 ——
#   进程其实起来了（stderr 能看到它的 libpng 告警），但 GUI 子系统的宿主没有人接管它的
#   标准句柄，父进程的等待就悬在那里；带上重定向后同一调用 230 ms 返回、rc=0。
#   注意 $appModeTimeoutSec 那道超时【兜不住】它：WaitForExit(ms) 对这种情况不生效，
#   所以这不是"超时会 Kill"能覆盖的，必须从起进程那一行解决。
try {
    New-Item -ItemType Directory -Force -Path $appModeOut | Out-Null
} catch {
    Write-Host "   ❌ --out 目录建不出来（$appModeOut：$($_.Exception.Message)）⇒ 这一格没判，按失败处理" -ForegroundColor Red
    $script:GateGaps += "[1.97/2] --out 目录创建失败 ⇒ --settings-migration 未判"
    exit 1
}
$progressFile = Join-Path $appModeOut 'progress.txt'
# SelfTest 的 progress 是 AppendAllText：不清就分不清哪几行是本轮的
if (Test-Path -LiteralPath $progressFile) { Remove-Item -LiteralPath $progressFile -Force }

$appCode = $null
$appTimedOut = $false
$appSw = [Diagnostics.Stopwatch]::StartNew()
try {
    $appProc = Start-Process -FilePath $appExe.FullName `
        -ArgumentList @('--settings-migration', '--out', $appModeOut) `
        -PassThru -WorkingDirectory $appExe.DirectoryName `
        -RedirectStandardOutput (Join-Path $appModeOut 'stdout.txt') `
        -RedirectStandardError (Join-Path $appModeOut 'stderr.txt')
    # ⚠ 2026-10-17：必须在这一行就 EnableRaisingEvents，否则下面的 ExitCode 恒为空。
    #   实测三臂（同一 exe、同一形状）：不设 ⇒ WaitForExit(60000)=True、HasExited=True、
    #   但 .ExitCode 取回空 ⇒ 这一格落进"拿不到退出码"分支，报的"进程没起来"是假话
    #   （探针臂里 PID 有值、progress.txt 也被写了）；设了 ⇒ App 读回 0（连跑两次）、
    #   cmd /c exit 3 读回 3、cmd /c exit 0 读回 0 ⇒ 既不是恒 0 也不是恒 null。
    #   原先写"环境问题"的那条结论作废：红的是这一行的取码方式，不是这台机器。
    $appProc.EnableRaisingEvents = $true
    if (-not $appProc.WaitForExit($appModeTimeoutSec * 1000)) {
        $appTimedOut = $true
        try { $appProc.Kill() } catch { }
    } else {
        $appCode = $appProc.ExitCode
    }
} catch {
    Write-Host "   ❌ 起进程/等待失败：$($_.Exception.Message)" -ForegroundColor Red
}
$appSw.Stop()

# ── 无论这一格判成什么，先把用户那份设置按原字节还原，再谈结论 ──
$settingsSha1 = Get-Sha256OrAbsent $settingsPath
$settingsChanged = ($settingsSha1 -ne $settingsSha0)
if ($null -ne $settingsBytes) {
    if ($settingsChanged) {
        [IO.File]::WriteAllBytes($settingsPath, $settingsBytes)
        $settingsSha1 = Get-Sha256OrAbsent $settingsPath
        $restored = if ($settingsSha1 -eq $settingsSha0) { "与轮前逐字相同 ✓" } else { "★ 与轮前不同：立即人工核对" }
        Write-Host "   ℹ️  设置文件被这一轮重写 ⇒ 已按原字节还原（前 $($settingsSha0.Substring(0,12))… 现 $($settingsSha1.Substring(0,12))…）$restored" -ForegroundColor Yellow
        if ($settingsSha1 -ne $settingsSha0) {
            # 还原不成功就是门禁改了用户机器的设置 —— 这一格不许带着副作用收场
            Write-Host "      $settingsPath 现在既不是轮前那份、也没被恢复：立即人工核对" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "   ℹ️  设置文件本轮【未改动】（sha256 $($settingsSha0.Substring(0, 12))…）" -ForegroundColor DarkGray
    }
} elseif (Test-Path -LiteralPath $settingsPath) {
    Remove-Item -LiteralPath $settingsPath -Force
    Write-Host "   ⚠️  轮前没有 $settingsPath，这一轮生成了 ⇒ 已删除，恢复'本来就没有'的状态" -ForegroundColor Yellow
} else {
    Write-Host "   ℹ️  轮前没有设置文件、本轮也没生成 ⇒ 无东西可还原" -ForegroundColor DarkGray
}

if ($appTimedOut) {
    Write-Host "   ❌ ${appModeTimeoutSec}s 未退出 ⇒ 记【未判】（实测正常成本不到 1 秒），不记绿" -ForegroundColor Red
    $script:GateGaps += "[1.97/2] --settings-migration 超时 ${appModeTimeoutSec}s 未退出 ⇒ 未判"
    exit 1
}
if ($null -eq $appCode) {
    # 措辞纠正（2026-10-17）：原先写"进程没起来"是凭这条红编的因果，实测进程起了、也退了，
    # 起不来的是取码那一行 ⇒ 红的话只能说到"码没拿到"，不能替机器说"进程没起来"。
    Write-Host "   ❌ 拿不到退出码 ⇒ 这一格【没判】，按失败处理" -ForegroundColor Red
    $script:GateGaps += "[1.97/2] --settings-migration 退出码为空 ⇒ 未判"
    exit 1
}
if ($appCode -ne 0) {
    Write-Host "   ❌ --settings-migration 退出码 $appCode（$($appSw.ElapsedMilliseconds)ms）⇒ App 侧三条设置自检至少一条判红" -ForegroundColor Red
    if (Test-Path -LiteralPath $progressFile) {
        foreach ($l in (Get-Content -LiteralPath $progressFile -Encoding UTF8 | Select-Object -Last 6)) {
            Write-Host ("      " + $l.Substring(0, [Math]::Min(200, $l.Length))) -ForegroundColor DarkGray
        }
    } else {
        Write-Host "      ⚠ 没有 progress.txt（$progressFile）⇒ 连一步都没落盘，读数不可信" -ForegroundColor Yellow
    }
    exit 1
}
# 退出码 0 却一条 progress 都没写 = 这一格其实没跑起来（同 [2/2] 那条"退 0 但无 RESULT"的口径）
if (-not (Test-Path -LiteralPath $progressFile)) {
    Write-Host "   ❌ 退出码 0 但没有 progress.txt ⇒ 判为失败（很可能一步都没跑）：$progressFile" -ForegroundColor Red
    exit 1
}
Write-Host "   ✅ App 侧设置迁移: 退出码 0（$($appSw.ElapsedMilliseconds)ms，上限 ${appModeTimeoutSec}s）｜ $($appExe.Name)｜--out 落 $appModeOut"

# ── 同一格里第二条 App 侧判据：把"运行期才建的 XAML 窗"真建一次（2026-10-21，台账 §70）──
# 起因：点识字/翻译必崩那个缺陷，1836 条断言一条都抓不到 —— 它只在真走到那一步、
# 且抛在 DispatcherQueue 回调里时现形，最后升级成原生 fail-fast，托管侧连日志都没有。
# 构造一次约 50 ms，判据 = 退出码（0=全通过，1=有窗构造失败），与 --settings-migration 同一口径。
$winOut = Join-Path $RepoRoot "tests\output\app-window-probe"
New-Item -ItemType Directory -Force -Path $winOut | Out-Null
$winCode = $null
try {
    $wp = Start-Process -FilePath $appExe.FullName -ArgumentList @('--window-probe') `
        -PassThru -WorkingDirectory $appExe.DirectoryName `
        -RedirectStandardOutput (Join-Path $winOut 'stdout.txt') `
        -RedirectStandardError (Join-Path $winOut 'stderr.txt')
    $wp.EnableRaisingEvents = $true
    if ($wp.WaitForExit($appModeTimeoutSec * 1000)) { $winCode = $wp.ExitCode }
    else { try { $wp.Kill() } catch { } }
} catch {
    Write-Host "   ❌ 起 --window-probe 失败：$($_.Exception.Message)" -ForegroundColor Red
}
if ($null -eq $winCode) {
    Write-Host "   ❌ --window-probe 拿不到退出码 ⇒ 这一格【没判】，按失败处理" -ForegroundColor Red
    $script:GateGaps += "[1.97/2] --window-probe 退出码为空 ⇒ 未判"
    exit 1
}
if ($winCode -ne 0) {
    Write-Host "   ❌ --window-probe 退出码 $winCode ⇒ 有运行期 XAML 窗构造失败，去 $((Split-Path -Parent $appExe.FullName))\log 里找 WindowProbe 行" -ForegroundColor Red
    exit 1
}
Write-Host "   ✅ 运行期 XAML 窗构造探针: 退出码 0（对照臂裸 Window + OcrPreviewWindow）"
Write-Host ""

# ══════════════════════════════════════════════════════════════
# ══════════════════════════════════════════════════════════════
#  [1.98/2] VS-Cap 侧车已归档（2026-10-27）—— 反向哨兵
# ══════════════════════════════════════════════════════════════
# 归档不是"把那几行删掉就算"：侧车树/对撞工程一旦悄悄回来，说明有人把 Qt 依赖又接进产品，
# 却没有同时恢复 BuildVSCap.ps1、打包声明与跨语言像素判据 —— 那时门禁会全绿而功能半死不活。
# 反过来，这三样只要都在【或都不在】才算一致；这里只判"不在"这一侧（它们是本轮的归档目标）。
Write-Host "[1.98/2] VS-Cap 侧车归档一致性..."
$vscapLeftovers = @(
    (Join-Path $RepoRoot 'src\TrueToneCap.VSCap'),
    (Join-Path $RepoRoot 'tests\VSCapInterop'),
    (Join-Path $RepoRoot 'scripts\BuildVSCap.ps1')
) | Where-Object { Test-Path $_ }
if ($vscapLeftovers.Count -gt 0) {
    Write-Host "   ❌ VS-Cap 归档后仍有残留：$($vscapLeftovers -join '；')" -ForegroundColor Red
    Write-Host "      要么彻底归档（连打包/门禁声明一起去掉），要么恢复它的像素交接判据 —— 不许半留。" -ForegroundColor Yellow
    $script:GateGaps += "[1.98/2] VS-Cap 归档残留 $($vscapLeftovers.Count) 项"
    exit 1
}
Write-Host "   ✅ 侧车源码 / 对撞工程 / 构建脚本均不在树内（功能已并入主程序，见 docs/vscap-mode.md）"

#  [2/2] 逐套件执行
# ══════════════════════════════════════════════════════════════
Write-Host "[2/2] 测试..."

$results = New-Object System.Collections.Generic.List[object]
$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($suite in $suites) {
    Write-Host "   ▶ $suite" -ForegroundColor DarkGray

    # ⚠ 必须走 Invoke-CapturedNative：$ErrorActionPreference=Stop 下，native 命令经 `2>&1`
    #   管道时【任何一行 stderr 都会被包成 NativeCommandError 终止性错误】，整个门禁当场中止，
    #   而且中止点之后的套件连"跑过没有"都不留记录。2026-09-19 实测：--encoding-tests 里
    #   NativeEncoderGuard 打印的一条 NVENC 隔离警告（本来就是"崩溃被兜住"的正常输出）
    #   把后面四套全部带走 —— 表现是"门禁在编码套件处静默截断"，不是"某套失败"。
    $r = if ($testExe) {
        Invoke-CapturedNative -Exe $testExe.FullName -ArgList @($suite)
    } else {
        Invoke-CapturedNative -Exe "dotnet" -ArgList @("run", "--project", $testProj, "-c", $Configuration, "--", $suite)
    }
    $output = $r.Text
    $code = $r.Code

    # 机器可读行: RESULT suite=… passed=… failed=… warnings=… elapsedMs=… skipped=…
    $passed = $null; $failed = $null; $skipped = 0
    if ($output -match 'RESULT\s+suite=\S+\s+passed=(\d+)\s+failed=(\d+)') {
        $passed = [int]$Matches[1]
        $failed = [int]$Matches[2]
    }
    if ($output -match '\s+skipped=(\d+)') { $skipped = [int]$Matches[1] }

    $results.Add([pscustomobject]@{
        Suite    = $suite
        Exit     = $code
        Passed   = $passed
        Failed   = $failed
        Skipped  = $skipped
        # 退出码 0 却解析不到 RESULT 行 = 极可能"零断言执行"（如套件名拼错被运行器当未知参数），
        # 必须视为失败，否则门禁就是假绿灯。
        NoResult = ($passed -eq $null)
    })

    if ($code -eq 0 -and $passed -ne $null) {
        $s = if ($skipped -gt 0) { "，跳过 $skipped" } else { "" }
        Write-Host "     ✅ 通过 ($passed 断言$s)"
        # 跳过必须点名。只报"N 处没判"等于说不出内容 —— 2026-09-27 审查 N6：
        # 通过路径原本把套件 stdout 整段丢弃，四条 SKIP 是查不到是谁的。
        if ($skipped -gt 0) {
            $skipLines = @($output -split "`n" | Where-Object { $_ -match '跳过:' })
            # 套件自己那行已经带了 ⏭ / ⚠️ 标记 —— 这里只缩进，不再叠一层，否则审计日志里是"⏭ ⏭ …"
            foreach ($l in $skipLines) { Write-Host ("       " + $l.Trim()) -ForegroundColor DarkYellow }
            if ($skipLines.Count -ne $skipped) {
                Write-Host "       ⚠️ RESULT 报 skipped=$skipped，但只点名到 $($skipLines.Count) 条 ⇒ 有跳过项没写原因" -ForegroundColor Yellow
            }
        }
    } elseif ($code -eq 0 -and $passed -eq $null) {
        Write-Host "     ❌ 退出码 0，但未解析到 RESULT 行 —— 判为失败（很可能零断言执行）" -ForegroundColor Red
        $tail = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
        if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
    } else {
        Write-Host "     ❌ 失败 (退出码 $code)" -ForegroundColor Red
        # 失败时把尾部输出打出来，便于直接定位，不必重跑
        $tail = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
        if ($tail) { Write-Host $tail -ForegroundColor DarkGray }
    }
}

$sw.Stop()

# ══════════════════════════════════════════════════════════════
#  汇总
# ══════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "══════════════════════════════════════════════════"
$maxLen = ($results | ForEach-Object { $_.Suite.Length } | Measure-Object -Maximum).Maximum
foreach ($r in $results) {
    $mark = if ($r.Exit -eq 0 -and -not $r.NoResult) { "✅" } else { "❌" }
    $stat = if ($r.Passed -ne $null) {
        $s = if ($r.Skipped -gt 0) { " / $($r.Skipped) 跳过" } else { "" }
        "$($r.Passed) 通过 / $($r.Failed) 失败$s"
    } elseif ($r.NoResult -and $r.Exit -eq 0) {
        "退出码 0 但无 RESULT 行（零断言执行）"
    } else { "退出码 $($r.Exit)" }
    Write-Host ("  {0} {1,-$maxLen}  {2}" -f $mark, $r.Suite, $stat)
}
Write-Host "══════════════════════════════════════════════════"

# 失败 = 退出码非 0，【或】退出码 0 但没解析到 RESULT 行
# （后者此前被当作"通过"，是完整的假绿灯路径 —— 典型诱因是套件名拼错）
$failedSuites = @($results | Where-Object { $_.Exit -ne 0 -or $_.NoResult })
$totalPassed = ($results | Where-Object { $_.Passed -ne $null } | Measure-Object -Property Passed -Sum).Sum

# ═══ 断言总数对基线（2026-09-29 审查轮 §11.4）═══
# Publish.ps1 里那个 MinGateAssertions=340 对真实 1379 ⇒ 删掉上千条断言两处都照样绿。
# 基线不再手抄：用 -WriteGateBaseline 从一轮真读数生成，此后任何"某套件变少"都要红并点名。
$baselinePath = Join-Path $RepoRoot 'scripts\gate-baseline.json'
if ($WriteGateBaseline -and $failedSuites.Count -eq 0) {
    $doc = [ordered]@{
        取数时间 = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        总断言   = $totalPassed
        各套件   = [ordered]@{}
    }
    foreach ($r in $results) {
        if ($null -ne $r.Passed) {
            $n = $r.Suite
            $doc.各套件.$n = [ordered]@{ 断言 = $r.Passed; 跳过 = $r.Skipped }
        }
    }
    $noteKey = '_说明'
    $doc.各套件.$noteKey = '用 scripts\verify.ps1 -WriteGateBaseline 重生成；刻意删断言时要显式改这份文件，让删除行为留在 diff 里'
    # ⚠ 不能用 `Set-Content -Encoding UTF8`：PowerShell 5.1 那个写法【必然】在文件头加 BOM (EF BB BF)。
    #   2026-10-21 那轮是生成后手工剥掉 BOM 的 —— 治标：本轮 -WriteGateBaseline 一跑就又长回来了，
    #   而且带 BOM 的这份 JSON 用 python json.load(open(p, encoding='utf-8')) 会当场抛
    #   "Unexpected UTF-8 BOM (decode using utf-8-sig)"（本轮实测踩过，也是它逼出来的这条修法）。
    #   改成显式无 BOM 编码写盘，并在写完后自己复核一次 —— 尺子要能看见自己的输出。
    $json = $doc | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($baselinePath, $json, (New-Object System.Text.UTF8Encoding($false)))
    $headBytes = [System.IO.File]::ReadAllBytes($baselinePath)
    if ($headBytes.Length -ge 3 -and $headBytes[0] -eq 0xEF -and $headBytes[1] -eq 0xBB -and $headBytes[2] -eq 0xBF) {
        Write-Host "  ❌ 基线写出来带 BOM ⇒ 按无 BOM UTF-8 读它的脚本会抛异常，这一轮【不算写完】" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ℹ️  基线已写入 $baselinePath（本轮总断言 $totalPassed，无 BOM 已复核）" -ForegroundColor DarkGray
} elseif ($WriteGateBaseline) {
    # ⚠ 本轮有套件失败 ⇒【拒绝写基线】。2026-09-29 实测踩过：一次 --unit-tests 红 1 条的轮次照样带了
    #   -WriteGateBaseline，于是把"通过 899"记成了地板 —— 地板从那一刻起就比真实规模低，
    #   而这正是这条机制存在的理由（"断言变少要红"）。宁可让人重跑一次绿轮，也不能记错地板。
    Write-Host "  ❌ 带了 -WriteGateBaseline 但本轮有套件失败 ⇒ 拒绝写基线（否则把失败读数记成地板）" -ForegroundColor Red
}

$baselineFail = $false
$baselineJustWritten = $false
if ($WriteGateBaseline -and $failedSuites.Count -eq 0) { $baselineJustWritten = $true }
if ((Test-Path -LiteralPath $baselinePath) -and -not $baselineJustWritten) {
    try {
        $base = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
        $ranByName = @{}
        foreach ($r in $results) { $ranByName[$r.Suite] = $r }
        # ═══ 反向比对：基线里有、本轮【没跑】的套件必须点名 ═══
        # 下面那个 foreach 只遍历"本轮跑了的"套件 ⇒ 把一条套件从 $suites 里摘掉（或把它改名）
        # 在这把尺子上是不可见的：断言不会变少，只是那一行读数消失了，门禁全绿。
        # 摘掉一整条判据比重删一条断言的破坏力大得多，却没被任何一处观测到。
        foreach ($p in @($base.各套件.PSObject.Properties)) {
            if ($p.Name -eq '_说明') { continue }
            if (-not $ranByName.ContainsKey($p.Name)) {
                Write-Host "  ❌ 基线里的 $($p.Name) 本轮没有跑 ⇒ 套件被摘掉/改名/构建没产出，这一格【没判】" -ForegroundColor Red
                $baselineFail = $true
            }
        }
        foreach ($r in $results) {
            if ($null -eq $r.Passed) { continue }
            $n = $r.Suite
            $b = $base.各套件.$n
            if ($null -eq $b) {
                Write-Host "  ℹ️  $($r.Suite) 不在基线里（新套件或本轮才启用）⇒ 未比对" -ForegroundColor DarkGray
                $script:GateGaps += "$($r.Suite) 不在基线里 ⇒ 它的断言规模本轮无对照（跑一次 -WriteGateBaseline 收编）"
                continue
            }
            if ($r.Passed -lt $b.断言) {
                Write-Host "  ❌ $($r.Suite) 断言数从基线 $($b.断言) 掉到 $($r.Passed)（少 $($b.断言 - $r.Passed) 条）" -ForegroundColor Red
                Write-Host "     ⇒ 有断言被删/被静默跳过。确系有意就改 scripts\gate-baseline.json 并重跑，别让它悄悄少" -ForegroundColor Red
                $baselineFail = $true
            }
            if ($r.Skipped -gt $b.跳过) {
                Write-Host "  ⚠️ $($r.Suite) 跳过数从基线 $($b.跳过) 涨到 $($r.Skipped) ⇒ 本轮有东西没判" -ForegroundColor Yellow
                $script:GateGaps += "$($r.Suite) 跳过 $($r.Skipped) 条（基线 $($b.跳过)）"
            }
        }
        # 总数也要比：逐套件都比过之后总数当然相等；但"跑了没进基线的套件"会让总数【高于】基线，
        # 那也不是判据变多的信号 ⇒ 只在低于时红（套件被整条摘掉那一类由上面的反向比对兜住）。
        if ($base.总断言 -and $totalPassed -lt $base.总断言) {
            Write-Host "  ❌ 总断言从基线 $($base.总断言) 掉到 $totalPassed ⇒ 与逐套件读数至少有一处不一致，本轮不作数" -ForegroundColor Red
            $baselineFail = $true
        }
    } catch {
        Write-Host "  ❌ 基线文件读不动（$($_.Exception.Message)）⇒ 这一格没判，按失败处理" -ForegroundColor Red
        $baselineFail = $true
    }
} elseif ($baselineJustWritten) {
    # 写基线的那一轮拿本轮读数和【本轮读数】比，恒等 ⇒ 那是自我确认，不是判据。
    # 明说一句，免得"带了 -WriteGateBaseline 的一轮全绿"被当成"基线比对通过"。
    Write-Host "  ℹ️  本轮就是基线的来源 ⇒ 断言规模这一格是自比（恒等），下一轮起才由它拦" -ForegroundColor DarkGray
    $script:GateGaps += "[地板] 本轮写了 gate-baseline.json ⇒ 断言规模比对在本轮是自我比对，无对照价值"
} elseif (-not $WriteGateBaseline) {
    # 基线缺失必须拦，不能默默过去：否则"没有比过"会被读成"比对通过"
    Write-Host "  ❌ 没有 scripts\gate-baseline.json ⇒ 断言总数这一格【没判】。先跑一次 -WriteGateBaseline" -ForegroundColor Red
    $baselineFail = $true
}

if ($script:GateGaps.Count -gt 0) {
    Write-Host "─────────────── 本轮判据未覆盖（不是通过，是没判）───────────────" -ForegroundColor Yellow
    foreach ($g in $script:GateGaps) { Write-Host "  ⏭  $g" -ForegroundColor Yellow }
    Write-Host "  合计 $($script:GateGaps.Count) 处；这些格子本轮无结论，换机/补件后要重跑" -ForegroundColor Yellow
    Write-Host "══════════════════════════════════════════════════"
}

if ($failedSuites.Count -eq 0 -and -not $baselineFail) {
    # 结论行必须带上"未判"的条数：只报 ✅ 的话，上面那串 ⏭ 台账在滚屏里等于没写，
    # 换机器的人会把"没判"读成"判过且通过"（本仓 §11.6 立的那条：汇总数要与点名条数相等）。
    $gapTail = if ($script:GateGaps.Count -gt 0) { "｜另有 $($script:GateGaps.Count) 处未判（见上⏭清单，这些格子本轮无结论）" } else { "｜无未判格子" }
    Write-Host "  ✅ $($results.Count) 个套件全部通过 — 共 $totalPassed 断言 ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)$gapTail"
    Write-Host "══════════════════════════════════════════════════"
    Write-Host ""
    exit 0
}

if ($failedSuites.Count -eq 0 -and $baselineFail) {
    Write-Host "  ❌ 套件全绿，但【断言数量对不上基线】⇒ 这一轮不作数（有判据被删或被静默跳过）" -ForegroundColor Red
} else {
    Write-Host "  ❌ $($failedSuites.Count)/$($results.Count) 个套件失败 — 共 $totalPassed 断言通过 ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)"
}
foreach ($f in $failedSuites) {
    $why = if ($f.NoResult -and $f.Exit -eq 0) { "零断言执行（未解析到 RESULT 行）" } else { "退出码 $($f.Exit)" }
    Write-Host "     · $($f.Suite)  ($why)" -ForegroundColor Red
}
Write-Host "══════════════════════════════════════════════════"
Write-Host ""
exit 1
