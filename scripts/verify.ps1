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
#   · Publish.ps1 内部也跑同样的 7 个套件，但它只在发布时跑；本脚本随时可跑。
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
#   .\scripts\verify.ps1                    # 构建 + 7 个门禁套件（headless 安全）
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
#       → [1.8/2] 色彩管线判据(默认执行，无跳过开关) → [1.9/2] 宿主新鲜度 → [2/2] 逐套件执行

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
    [switch]$SkipPublishGates = $false
)

# -SkipBuild 与 -BuildOnly 同时给是矛盾输入（一个跳构建、一个只要构建），
# 静默选一个语义都可能让人误以为跑过测试了 —— 直接报错更省心。
if ($SkipBuild -and $BuildOnly) {
    Write-Host "❌ -SkipBuild 与 -BuildOnly 不能同时使用（语义矛盾）" -ForegroundColor Red
    exit 2
}

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$slnx     = Join-Path $RepoRoot "TrueToneCap.slnx"
$testProj = Join-Path $RepoRoot "src\TrueToneCap.Test\TrueToneCap.Test.csproj"

if (-not (Test-Path $slnx)) {
    Write-Host "❌ 未找到解决方案文件: $slnx" -ForegroundColor Red
    exit 1
}

# headless 7 套件（与 Publish.ps1 的发布门禁一致）
# 注: --usability-tests 自 2026-09-14 起已是 headless 安全的 —— 其中真正依赖显示器的
#     2 个用例会在无显示器时"显式跳过"（计入 skipped），不再导致失败。
$suites = @(
    "--unit-tests",
    "--color-tests",
    "--encoding-tests",
    "--service-tests",
    "--ocr-tests",
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
    & dotnet build $slnx -c $Configuration
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

if ($SkipPrecision) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 已按 -SkipPrecision 显式跳过" -ForegroundColor Yellow
    Write-Host "   后果：本轮【没有】用外部解码器读回产物 ⇒ '参数写了没人读 / 声明与像素不一致 /" -ForegroundColor Yellow
    Write-Host "          容器实际交付几级 / 跨容器色域声明矛盾' 这四类回归全部未被判定，不是通过。" -ForegroundColor Yellow
} elseif (-not (Test-Path $precisionScript)) {
    Write-Host "[1.7/2] 外部判据 —— ❌ 未找到 $precisionScript（门禁接线断了）" -ForegroundColor Red
    exit 1
} elseif (-not $pyExe) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 跳过 (SKIP)" -ForegroundColor Yellow
    Write-Host "   原因: 未找到可用的 python（试过 python / py -3 / python3；WindowsApps 桩会以退出码 49 静默失败）" -ForegroundColor Yellow
    Write-Host "   ⚠️ 本轮【没有】用外部解码器读回产物 ⇒ '参数是否真的进码流'这一类回归未被判定，不是通过" -ForegroundColor DarkGray
} elseif (-not $ffprobePath) {
    Write-Host "[1.7/2] 外部判据 —— ⚠️ 跳过 (SKIP)" -ForegroundColor Yellow
    Write-Host "   原因: 未探测到 ffprobe（PATH 与 C:\ffmpeg*\bin、C:\Windows\ffmpeg-*\bin 等常见位置都没有；" -ForegroundColor Yellow
    Write-Host "         可用 TTC_FFPROBE 显式指定路径）" -ForegroundColor Yellow
    Write-Host "   ⚠️ 判据只信外部解码器，没有它就没有判据 ⇒ 本类回归未被判定，不是通过" -ForegroundColor DarkGray
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
        @{ Script = "av1c_vs_bitstream.py";        Samples = $dstSweep }
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
    $inputs = Get-ChildItem $srcRoot -Include *.cs, *.xaml -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
    $newest = $inputs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
    } else {
        Write-Host "   ✅ 宿主新鲜度: $($hostsToCheck.Count) 个宿主都不早于 $($inputs.Count) 份源码"
    }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
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
if ($failedSuites.Count -eq 0) {
    Write-Host "  ✅ $($results.Count) 个套件全部通过 — 共 $totalPassed 断言 ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)"
    Write-Host "══════════════════════════════════════════════════"
    Write-Host ""
    exit 0
}

Write-Host "  ❌ $($failedSuites.Count)/$($results.Count) 个套件失败 — 共 $totalPassed 断言通过 ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)"
foreach ($f in $failedSuites) {
    $why = if ($f.NoResult -and $f.Exit -eq 0) { "零断言执行（未解析到 RESULT 行）" } else { "退出码 $($f.Exit)" }
    Write-Host "     · $($f.Suite)  ($why)" -ForegroundColor Red
}
Write-Host "══════════════════════════════════════════════════"
Write-Host ""
exit 1
