# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap 发布产物冒烟验证
#
# 背景（P0-2）:
#   Publish.ps1 此前只检查 Microsoft.WindowsAppRuntime.Bootstrap.dll 是否存在就生成校验和，
#   从不验证产物"能不能跑" —— 缺字典、native 工具缺 VC++ 运行库、着色器没编译出来这类问题，
#   都能静默通过发布流程，直到用户手上才暴露。
#
# 设计原则:
#   · 独立可跑：只依赖 PackageDir，不依赖发布流程上下文，便于事后复查任意一份产物
#   · 默认全静态：文件清单 / 尺寸下限 / PE 头 / 版本资源 —— 全部不需要启动进程，
#     因此可在无桌面会话、无 GPU 的环境（含 CI）稳定执行
#   · 可执行性探针（native --version）默认开启但可关（-SkipExecProbe）：
#     这是唯一能发现"缺运行库"的检查，代价是需要真正启动子进程
#   · 主程序【不启动】：它是 GUI 应用，启动后不会自行退出，会挂住发布流程
#
# 2026-09-20 追加 [5/6]「许可分发与符号卫生」: 此前的 5 步全在验证"能不能跑"，
#   对"该不该带、该不该发"零断言 —— 第三方许可文本没随包、私有 .pdb 随包漏发，
#   都能照常盖章。参数签名与退出码语义未变，仍可被 Publish.ps1 / CI 以子进程调用。
#
# 用法:
#   .\scripts\verify-package.ps1 -PackageDir publish\TrueToneCap-v0.3.4-beta
#   .\scripts\verify-package.ps1 -PackageDir <dir> -ExpectedVersion 0.3.4-beta   # 额外校验版本注入
#   .\scripts\verify-package.ps1 -PackageDir <dir> -SkipExecProbe                # 仅静态校验
#
# 退出码: 0 = 通过；1 = 存在失败项。

param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir,

    # 期望的产品版本（来自 Directory.Build.props / Publish.ps1 -Version）。
    # 提供时会校验 TrueToneCap.exe 的版本资源是否真的注入了该版本 ——
    # 防止"目录名是一个版本、程序集里还是另一个版本"这类版本漂移。
    [string]$ExpectedVersion = "",

    # 期望的源码 commit。提供时会比对「二进制里内嵌的 commit」(ProductVersion 的 +sha 后缀，
    # 由 SourceLink 注入) 与该值 —— 这是唯一能把"磁盘上的 exe"和"仓库里的某次提交"
    # 绑在一起的检查，可拦住"声称 HEAD=X、实际代码是 Y"这类不可复现产物。
    [string]$ExpectedCommit = "",

    # 跳过 native 工具的 --version 探针（不启动任何子进程）
    [switch]$SkipExecProbe = $false,

    [int]$ProbeTimeoutSec = 20
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PackageDir)) {
    Write-Host "❌ 发布目录不存在: $PackageDir" -ForegroundColor Red
    exit 1
}
$PackageDir = (Resolve-Path $PackageDir).Path

$failures = New-Object System.Collections.Generic.List[string]
$warns    = New-Object System.Collections.Generic.List[string]

function Check-File {
    param(
        [string]$RelPath,
        [long]$MinBytes = 1
    )
    $p = Join-Path $PackageDir $RelPath
    if (-not (Test-Path $p)) {
        $script:failures.Add("缺少文件: $RelPath")
        Write-Host "   ❌ $RelPath — 缺失" -ForegroundColor Red
        return $false
    }
    $len = (Get-Item $p).Length
    if ($len -lt $MinBytes) {
        $script:failures.Add("文件过小(疑似截断): $RelPath ($len B < $MinBytes B)")
        Write-Host "   ❌ $RelPath — $len B，低于下限 $MinBytes B" -ForegroundColor Red
        return $false
    }
    Write-Host "   ✅ $RelPath  ($([math]::Round($len / 1KB, 1)) KB)"
    return $true
}

Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  发布产物冒烟验证                                ║"
Write-Host "║  $PackageDir"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1/6] 主程序
# ══════════════════════════════════════════════════════════════
Write-Host "[1/6] 主程序..."
# ⚠️ 下限不要按"完整程序"来定：自包含 .NET 应用里 TrueToneCap.exe 只是 apphost 存根
#   （本机实测 256 KB，无 BSJB 托管元数据），真正的代码在 TrueToneCap.dll 与
#   TrueToneCap.r2r.dll（R2R 复合映像，约 126 MB）。
#   因此这里分别校验：存根 >= 64 KB，托管程序集 >= 256 KB。
$mainOk = Check-File -RelPath "TrueToneCap.exe" -MinBytes 64KB
Check-File -RelPath "TrueToneCap.dll"                          -MinBytes 256KB | Out-Null
Check-File -RelPath "Microsoft.WindowsAppRuntime.Bootstrap.dll" -MinBytes 64KB  | Out-Null

# PE 头校验：不启动进程即可确认是有效可执行映像（防止 0 字节/HTML 错误页被当成 exe 复制进来）
$mainExe = Join-Path $PackageDir "TrueToneCap.exe"
if ($mainOk) {
    try {
        $fs = [System.IO.File]::OpenRead($mainExe)
        $buf = New-Object byte[] 2
        [void]$fs.Read($buf, 0, 2)
        $fs.Close()
        if ($buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) {
            $failures.Add("TrueToneCap.exe 不是有效的 PE 映像（缺少 MZ 头）")
            Write-Host "   ❌ TrueToneCap.exe — 非法 PE 头" -ForegroundColor Red
        } else {
            Write-Host "   ✅ TrueToneCap.exe — PE 映像头有效 (MZ)"
        }
    } catch {
        $failures.Add("读取 TrueToneCap.exe 失败: $($_.Exception.Message)")
    }
}

# 版本资源校验：这是唯一能证明"-p:Version="真正注入了程序集的静态检查
if ($mainOk -and $ExpectedVersion) {
    $expect = $ExpectedVersion -replace '^[vV]', ''
    try {
        $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($mainExe)
        $got = $fvi.ProductVersion
        if ($got) { $got = $got.Trim() }
        # 程序集版本可能是 "0.3.4-beta" 也可能是 "0.3.4.0"（ informational version 与 file version 差异）
        if ($got -like "$expect*" -or $got -eq ($expect -replace '-.*', '') + ".0") {
            Write-Host "   ✅ 版本资源 = $got （期望 $expect）"
        } else {
            $failures.Add("版本注入不符: 程序集 ProductVersion='$got'，期望 '$expect'")
            Write-Host "   ❌ 版本资源 = '$got'，期望 '$expect'" -ForegroundColor Red
        }

        # 二进制内嵌的 commit（SourceLink 注入到 ProductVersion 的 +<sha> 后缀）
        # 实测形如: 0.3.4-beta+49d6c947fcd92800f1dd6a6d87bfc2292c85ad26
        if ($got -match '\+([0-9a-fA-F]{7,40})') {
            $embedded = $Matches[1].ToLowerInvariant()
            Write-Host "   ℹ️  二进制内嵌 commit = $embedded"
            if ($ExpectedCommit) {
                $want = $ExpectedCommit.Trim().ToLowerInvariant()
                if (-not $want.StartsWith($embedded) -and -not $embedded.StartsWith($want)) {
                    $failures.Add("二进制内嵌 commit 与期望不符: 二进制=$embedded / 期望=$want")
                    Write-Host "   ❌ 二进制内嵌 commit 与期望不符 (期望 $want)" -ForegroundColor Red
                } else {
                    Write-Host "   ✅ 二进制 commit 与源码 HEAD 一致"
                }
            }
        } else {
            $warns.Add("ProductVersion 未包含 +<commit> 后缀（SourceLink 未生效），无法把二进制绑定到具体提交")
        }
    } catch {
        $warns.Add("无法读取版本资源: $($_.Exception.Message)")
    }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [2/6] 原生工具链
# ══════════════════════════════════════════════════════════════
Write-Host "[2/6] 原生工具链 (native/)..."
# 尺寸下限的作用是"挡住截断/空文件/占位"，不是"断言体积应当多大"——
# 所以取下限时要贴着【当前真实体积】的量级，否则一次合法的瘦身就会把发布拦死。
# ⚠️ 2026-10-07 实测教训：cjpegli 原本是 libjxl 预编译件（5.13 MB），下限 1MB 合适；
#    但该次把 cjpegli 改为从 google/jpegli 自编译后体积降到 0.72 MB
#    （不再链接 sjpeg/skcms/brotli/libpng/libjpeg-turbo ⇒ 700KB 上下），
#    于是 1MB 下限把一次**完全正常**的发布拦了下来。
#    ⇒ 已按新体积调整，并在注释里写清依据，避免下次又当"回归"去查。
$nativeTools = @(
    @{ File = "avifenc.exe"; Min = 1MB },      # 实测 ~15.7 MB（自编译，含 aom+svt）
    @{ File = "cjpegli.exe"; Min = 256KB },    # 实测 ~0.72 MB（自编译，不链 libpng/libjpeg-turbo）
    @{ File = "cjxl.exe";    Min = 1MB },      # 实测 ~5.0 MB（libjxl 预编译件）
    @{ File = "cwebp.exe";   Min = 64KB },     # 实测 ~0.71 MB
    # 2026-10-31：可选无损压缩后端（libdeflate 1.26 自编译）。它缺了程序仍能跑（自动回退内置 zlib-ng），
    # 但随包承诺是"有它" ⇒ 尺寸下限照查；启动探针不适用（不是 exe），见下面的跳过分支。
    @{ File = "deflate.dll"; Min = 128KB }     # 实测 183,808 B
)
$nativeOk = @{}
foreach ($t in $nativeTools) {
    $ok = Check-File -RelPath "native\$($t.File)" -MinBytes $t.Min
    $nativeOk[$t.File] = $ok
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [3/6] OCR 模型与着色器
# ══════════════════════════════════════════════════════════════
Write-Host "[3/6] OCR 模型与着色器 (data/)..."
Check-File -RelPath "data\Models\PP-OCRv6_medium_det.onnx" -MinBytes 1MB | Out-Null
Check-File -RelPath "data\Models\PP-OCRv6_medium_rec.onnx" -MinBytes 1MB | Out-Null
Check-File -RelPath "data\Models\ppocrv6_dict.txt"         -MinBytes 8KB  | Out-Null

# 着色器：*.cso 不入库、由 fxc 现场编译 —— 编译被跳过（缺 fxc 且本地无 .cso）时
# 这里会是 0 个文件，程序能启动但一截图就崩，故必须作为失败项而不是警告。
$csoDir = Join-Path $PackageDir "data\Shaders"
$csoCount = 0
if (Test-Path $csoDir) {
    $csoFiles = @(Get-ChildItem $csoDir -Filter "*.hlsl.cso" -ErrorAction SilentlyContinue)
    # 兼容历史命名 <Name>.cso
    if ($csoFiles.Count -eq 0) { $csoFiles = @(Get-ChildItem $csoDir -Filter "*.cso" -ErrorAction SilentlyContinue) }
    $csoCount = $csoFiles.Count
}
if ($csoCount -eq 0) {
    $failures.Add("data\Shaders 下没有任何 .cso —— 着色器未编译，程序可启动但截图即崩")
    Write-Host "   ❌ data\Shaders\*.cso — 0 个" -ForegroundColor Red
} else {
    Write-Host "   ✅ data\Shaders — $csoCount 个 .cso"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [4/6] 可执行性探针（唯一能发现"缺 VC++ 运行库"的检查）
# ══════════════════════════════════════════════════════════════
Write-Host "[4/6] 可执行性探针..."
if ($SkipExecProbe) {
    Write-Host "   ⏭  已跳过 (-SkipExecProbe)：不会验证 native 工具能否真正运行" -ForegroundColor DarkGray
} else {
    # 各工具的版本参数不统一（avifenc/cjxl 用 --version，cwebp 用 -version，cjpegli 无版本参数），
    # 故这里只关心"进程能否启动并退出"，不关心它是否打印版本号。
    $probeArgs = @{ "avifenc.exe" = "--version"; "cjxl.exe" = "--version"; "cwebp.exe" = "-version"; "cjpegli.exe" = "-h" }
    foreach ($t in $nativeTools) {
        $name = $t.File
        if (-not $nativeOk[$name]) { continue }
        # deflate.dll 不是可执行文件：尺寸已由 [2/6] 校验，DLL 能否装载由程序启动自检负责
        #（自检失败会回退内置 zlib-ng，正是 DeflateZlib 的设计），这里不硬启它。
        if (-not $probeArgs.ContainsKey($name)) {
            Write-Host "   ⏭  $name — 非可执行文件，跳过启动探针（尺寸已校验）" -ForegroundColor DarkGray
            continue
        }
        $exe = Join-Path $PackageDir "native\$name"
        try {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $exe
            $psi.Arguments = $probeArgs[$name]
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true
            $p = [System.Diagnostics.Process]::Start($psi)
            if (-not $p.WaitForExit($ProbeTimeoutSec * 1000)) {
                $p.Kill()
                $failures.Add("$name 探针超时（${ProbeTimeoutSec}s 未退出）")
                Write-Host "   ❌ $name — 探针超时" -ForegroundColor Red
                continue
            }
            $ec = [int]$p.ExitCode
            # 退出码非 0 一般不算失败（cjpegli -h 以非 0 退出，缺参数时各工具也都是非 0）。
            # 但以下 NTSTATUS 表示"进程根本没跑起来"—— 这正是本步骤存在的意义：
            #   0xC0000135 STATUS_DLL_NOT_FOUND        —— 缺 VC++ / MinGW 运行库
            #   0xC0000142 STATUS_DLL_INIT_FAILED      —— 运行库初始化失败
            #   0xC000007B STATUS_INVALID_IMAGE_FORMAT —— 映像位数/格式不匹配
            # ⚠️ 缺运行库时 Process.Start 【不抛异常】，只返回这些退出码。
            #    此前只 catch 异常、无条件打印"✅ 可启动"，恰好漏掉本脚本最该抓的故障。
            $reason = switch ($ec) {
                -1073741515 { '0xC0000135 STATUS_DLL_NOT_FOUND（缺运行库）' }
                -1073741502 { '0xC0000142 STATUS_DLL_INIT_FAILED（运行库初始化失败）' }
                -1073741701 { '0xC000007B STATUS_INVALID_IMAGE_FORMAT（映像格式不匹配）' }
                default      { $null }
            }
            if ($reason) {
                $failures.Add("$name 启动失败: $reason")
                Write-Host "   ❌ $name — $reason" -ForegroundColor Red
            } else {
                Write-Host "   ✅ $name — 可启动 (退出码 $ec)"
            }
        } catch {
            $failures.Add("$name 无法启动: $($_.Exception.Message)")
            Write-Host "   ❌ $name — 启动失败: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [5/6] 许可分发与符号卫生（合规门禁）
# ══════════════════════════════════════════════════════════════
# 存在的意义: 本包再分发 libavif / libjxl / libwebp 等第三方原生二进制，
#   BSD-3-Clause / BSD-3-Clause-Clear / IJG / libpng / zlib / Apache-2.0 无一例外
#   都以「再分发时保留版权与许可全文」为授权条件 —— 少发许可文本不是"文档不全"，是侵权。
#   而 Publish.ps1 的 [5/6] 只是无条件 Copy，复制源被误删/改路径就会静默少发文件，
#   此前冒烟对许可分发【零断言】，缺了照样盖章（2026-09-20 审计发现）。
# 同一道理适用于反方向: 第三方私有 .pdb 随包分发既无授权必要（只给调试器读，
#   已实测 DirectML.dll 导入表 26 个模块无一为 .pdb，其 RSDS 记录指向微软 CI 机路径），
#   又白占约 12 MB，Publish.ps1 [2/6] 会剔除；本步骤是那侧改动的对偶断言 ——
#   谁把剔除逻辑改回去，这里就变红。
Write-Host "[5/6] 许可分发与符号卫生..."

# ① 本项目自己的 LICENSE（Apache-2.0 全文）
Check-File -RelPath "LICENSE" -MinBytes 512 | Out-Null

# ② 第三方许可汇总页
Check-File -RelPath "THIRD-PARTY-NOTICES.md" -MinBytes 512 | Out-Null

# ③ licenses/ 目录: 至少 $minLicenseFiles 个非空许可全文
#    真实下限远高于此 —— src/TrueToneCap.Core/Resources/tools-manifest.json 里四个工具的
#    licenseFiles 去重后已有 20 份，且 licenses/ 还含 .NET 运行时 / WinAppSDK / ONNX Runtime /
#    PaddleOCR 等组件的文本（实测目录内 43 个 .txt，2026-10-07 迁 WinML 后 +1）。
#    门禁取 6 是【刻意保守】: 只拦"整个目录没复制进来""只放了 INDEX.json""全是占位空文件"
#    这类真实故障，不拦"清单与目录条目数不一致"那种需要人工判断的漂移 —— 后者交给 INDEX.json。
#    ⚠️ 改了 tools-manifest.json 的许可映射，这里要跟着复核。
#    ⚠️ 2026-10-07 迁 WinML：新增 microsoft-winml-LicenseRef-Microsoft-EULA.txt。
#       Windows ML Runtime 的 EULA 与通用 WindowsAppSDK EULA、以及 WindowsAppSDK.ML 2.1.74
#       的 EULA 是【三份不同文本】（哈希互不相同），不可互相顶替。
$licDir = Join-Path $PackageDir "licenses"
$minLicenseFiles = 6
$minLicenseBytes = 200
if (-not (Test-Path $licDir)) {
    $failures.Add("缺少 licenses/ 目录 —— 第三方许可全文未随包分发")
    Write-Host "   ❌ licenses\ — 目录缺失" -ForegroundColor Red
} else {
    $licFiles = @(Get-ChildItem $licDir -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.Extension -match '^\.(txt|md|rst)$' })
    $stubs = @($licFiles | Where-Object { $_.Length -lt $minLicenseBytes })
    $usable = @($licFiles | Where-Object { $_.Length -ge $minLicenseBytes })
    if ($usable.Count -lt $minLicenseFiles) {
        $failures.Add("licenses/ 下非空许可全文仅 $($usable.Count) 个，少于下限 $minLicenseFiles")
        Write-Host "   ❌ licenses\ — $($usable.Count) 个非空许可文本 (< 下限 $minLicenseFiles)" -ForegroundColor Red
    } else {
        Write-Host "   ✅ licenses\ — $($usable.Count) 个非空许可文本"
    }
    if ($stubs.Count -gt 0) {
        $warns.Add("licenses/ 下有 $($stubs.Count) 个疑似占位的过小文本(< ${minLicenseBytes}B): $($stubs.Name -join ', ')")
        Write-Host "   ⚠️  过小: $($stubs.Name -join ', ')" -ForegroundColor Yellow
    }
}

# ④ licenses/INDEX.json 必须是合法 JSON（它是许可清单的权威索引，坏了下游全猜）
$idxPath = Join-Path $licDir "INDEX.json"
if (-not (Test-Path $idxPath)) {
    $failures.Add("缺少 licenses/INDEX.json")
    Write-Host "   ❌ licenses\INDEX.json — 缺失" -ForegroundColor Red
} elseif ((Get-Item $idxPath).Length -lt 2) {
    $failures.Add("licenses/INDEX.json 为空文件")
    Write-Host "   ❌ licenses\INDEX.json — 空文件" -ForegroundColor Red
} else {
    try {
        $idxJson = Get-Content $idxPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $idxJson) { throw "解析结果为 null（内容不是 JSON 对象/数组）" }
        Write-Host "   ✅ licenses\INDEX.json — JSON 可解析"
    } catch {
        $failures.Add("licenses/INDEX.json 无法解析为 JSON: $($_.Exception.Message)")
        Write-Host "   ❌ licenses\INDEX.json — JSON 解析失败: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ⑤ 包内不得残留第三方私有 .pdb（与 Publish.ps1 [2/6] 的剔除逻辑配对）
#    "自家" 判据与 Publish.ps1 用的是同一条正则 '^TrueToneCap' —— 两处必须一起改。
$ownPdbPattern = '^TrueToneCap'
$allPdbs = @(Get-ChildItem $PackageDir -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue)
$strayPdbs = @($allPdbs | Where-Object { $_.Name -notmatch $ownPdbPattern })
if ($strayPdbs.Count -gt 0) {
    $mb = [math]::Round((($strayPdbs | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
    $failures.Add("包内残留第三方私有调试符号 $($strayPdbs.Count) 个 / $mb MB: $($strayPdbs.Name -join ', ')")
    Write-Host "   ❌ 残留第三方 .pdb ($mb MB): $($strayPdbs.Name -join ', ')" -ForegroundColor Red
} else {
    $keptPdbs = @($allPdbs | Where-Object { $_.Name -match $ownPdbPattern })
    Write-Host "   ✅ 无第三方 .pdb 残留（自家符号保留 $($keptPdbs.Count) 个）"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
# ════════════════════════════════════
#  [5.5/6] VS-Cap 侧车已归档：包内【不许】再出现 data\vscap
# ════════════════════════════════════
# 2026-10-27 之前，侧车由 App.csproj 一条 glob 进包、构建机上没有 Qt 时静默展开成 0 条，
# 于是要靠"Publish 声明带不带 / 包里实际有没有"两边对撞。归档后只剩一个方向：
# 源码、对撞工程、BuildVSCap.ps1 都已移除 ⇒ 包内冒出 ttc-vscap.exe / Qt6*.dll 只能是
# 打包链又把一条死 glob 接回来了（那会同时带走 LGPL 义务与一个起不来的功能）。
Write-Host "[5.5/6] VS-Cap 侧车归档（包内不许出现 data\vscap）..."
$vscapDir = Join-Path $PackageDir 'data\vscap'
if (Test-Path $vscapDir) {
    $leaked = @(Get-ChildItem $vscapDir -Recurse -File -ErrorAction SilentlyContinue)
    $failures.Add("VS-Cap 已归档，包内却仍有 data\vscap（$($leaked.Count) 个文件）⇒ 打包链把死 glob 接回来了")
    Write-Host ("   ❌ data\vscap 仍在包里（{0} 个文件）" -f $leaked.Count) -ForegroundColor Red
} else {
    Write-Host "   ✅ 包内没有 data\vscap（与归档一致）"
}

#  [6/6] 汇总
# ══════════════════════════════════════════════════════════════
$totalFiles = (Get-ChildItem $PackageDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$totalMB    = [math]::Round((Get-ChildItem $PackageDir -Recurse -File -ErrorAction SilentlyContinue |
               Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host "[6/6] 汇总..."
Write-Host "   📦 $totalFiles 个文件 / $totalMB MB"
if ($warns.Count -gt 0) {
    Write-Host "   ⚠️  $($warns.Count) 项警告:" -ForegroundColor Yellow
    foreach ($w in $warns) { Write-Host "      · $w" -ForegroundColor Yellow }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "❌ 冒烟验证失败 — $($failures.Count) 项:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "     · $f" -ForegroundColor Red }
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "✅ 冒烟验证通过" -ForegroundColor Green
Write-Host ""
exit 0
