# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# TrueToneCap Publish Script
# 完整发布流程：校验依赖 → 编译 → 收集资源 → 打包 → 产出校验和
#
# 设计原则:
#   · 单一事实源: 原生工具链以 src/TrueToneCap.Core/Resources/ 为准，复制到发布包
#   · 失败即阻断: 任何必需依赖缺失 → throw，绝不产出残缺包
#   · 无硬编码: 版本号由 -Version 注入；TFM/输出目录由 MSBuild 决定
#   · 可追溯: 产出 SHA256SUMS.txt + build-info.json
#
# 用法:
#   .\Publish.ps1                              # 默认发布（版本取自 csproj）
#   .\Publish.ps1 -Version v0.3.4-beta         # 指定版本
#   .\Publish.ps1 -NoReadyToRun                # 禁用 ReadyToRun
#   .\Publish.ps1 -CoreAot                     # 试验性 Core AOT（当前为占位，见下）
#   .\Publish.ps1 -Msix                        # 追加产出 MSIX（商店/侧载），并做内容核对
#   .\Publish.ps1 -Msix                        # 商店包口径（2026-10-26 起）：自包含，不带 -FrameworkDependent
#   .\Publish.ps1 -Msix -FrameworkDependent    # ⚠️ 已废弃用于商店提交：目标机无 .NET 运行时即启动失败（见 2026-10-26 实测）
#   .\Publish.ps1 -Msix -Trim                  # 商店包瘦身：裁剪未用托管代码，实测 151 → 114 MB（-25%）
#   .\Publish.ps1 -Force                       # 允许强制终止运行中的 TrueToneCap.exe

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",

    # 版本号：留空则从 App.csproj 的 <Version> 读取，避免与程序集版本漂移
    [string]$Version = "",

    # 输出目录：留空则依据解析出的版本自动生成 publish\TrueToneCap-<Version>
    [string]$OutputDir = "",

    [switch]$CoreAot = $false,
    [switch]$NoReadyToRun = $false,

    # ═══ MSIX 打包（2026-10-07）═══
    # 追加产出 .msix / .appxsym，供微软商店提交或侧载。
    # 不影响既有的绿色包产物 —— 两者并行生成。
    # ⚠️ 该开关只是把 -p:TtcMsixPackage=true 透传给 App 项目；
    #    MSIX 属性的"成套"约束（WindowsPackageType + EnableMsixTooling + AppxManifest）
    #    全部封装在 App.csproj 里，避免在这里出现半套配置。
    [switch]$Msix = $false,

    # ═══ 框架依赖（2026-10-07）═══
    # 只在 -Msix 下有意义：把 .NET 与 WinAppSDK 运行时交给目标机/商店，
    # 实测 MSIX 从 150.90 MB 降到 84.57 MB（-44%）。
    # ⚠️ 不加 -Msix 单独用本开关会产出一个框架依赖的未打包构建（合法但不适合分发：
    #    绿色包的价值就是"解压即用"）。故这里显式告警，而不是静默照做。
    # 理由与实测数字详见 TrueToneCap.App.csproj 的 TtcFrameworkDependent 注释。
    # ⚠️ 2026-10-26 起【不建议用于商店提交】：框架依赖包内没有 .NET 运行时、清单也不声明，
    #    微软从未发布 .NET 运行时的商店框架包（dotnet/runtime#51574 仍 open）⇒
    #    干净机器上装上即启动失败。商店提交用自包含（不带本开关），
    #    实测证据链见 docs/reviews/msix-store/store-readiness-review-2026-10-26.md §6。
    [switch]$FrameworkDependent = $false,

    # ═══ 裁剪（2026-10-26）═══
    # 开启 IL 裁剪（-p:PublishTrimmed=true），删掉静态可达面之外的托管代码。
    # 实测：自包含 MSIX 151 → 114.4 MB（-24%）。与 R2R 复合映像互相抵消
    # （R2R 会把裁掉的体积以原生码加回去），故开启本开关时强制纯 JIT。
    # ⚠️ 裁剪会静默删除反射可达代码：自有代码的 IL 警告已在源码层清零并受
    #    TreatWarningsAsErrors 持续把守；上游注解缺口（IL2104）由 App.csproj 条件块
    #    带理由放行。产出必须过实机启动回归后才可分发（见 store-readiness-review §7.3）。
    [switch]$Trim = $false,

    # 允许强制终止正在运行的 TrueToneCap.exe（默认拒绝，防止误杀造成用户数据丢失）
    [switch]$Force = $false,

    # 跳过依赖校验（仅用于调试发布脚本本身）
    [switch]$SkipDependencyCheck = $false,

    # 允许从「脏工作区」发布（默认拒绝）
    # 理由: build-info.json 会记录 HEAD 的 commit SHA。若工作区有未提交改动，
    #       产物的实际内容与该 SHA 不符 —— 事后无法复现，也无法定位问题版本。
    #       仅在本地调试 / 明确知晓后果时使用。
    [switch]$AllowDirty = $false,

    # 跳过「发布前测试」门禁（默认执行 headless 7 套件，约 90~120s）
    [switch]$SkipTests = $false,

    # 门禁断言总数下限：防止套件被静默删减（误改 gateSuites）或某套件整体零断言执行
    # 时门禁仍然"全绿"。
    # 基线 2026-09-15 实测：7 套件共 350 条断言（service 原 1 条恒真已改为显式 Skip）。
    # 取 340 留出余量 —— 无显示器/D3D 的环境会因 Skip 使通过数自然下降，属预期波动；
    # 但删掉任一套件（最少 30 条）或某套件整体零断言，都会被此下限拦下。
    # 套件内容有合法增删时应同步调整此值。
    [int]$MinGateAssertions = 340,

    # 冒烟验证时跳过 native 工具的「可启动」探针（不启动任何子进程）。
    # 仅在已知子进程不可用、又仍需走完发布流程时使用（如受限环境调试脚本本身）。
    [switch]$SkipExecProbe = $false,

    # 发布成功后仅保留最近 N 个版本目录（0 = 不清理，默认）。
    # 背景: publish/ 曾累积到 1.2 GB（多个历史版本目录 + 7z）。
    # 注意: 只清理 publish\TrueToneCap-* 形式的版本目录，不碰 PACKAGE.md / PLAN /
    #       TTC日志查看器 等非版本目录；且始终保留本次刚产出的那个。
    [int]$KeepLast = 0,

    # 发布成功后在本仓库创建附注标签 v<版本>（默认【不创建】、【不推送】）。
    # 理由: props 里的版本号与 git tag 一度完全脱节 —— 0.3.4-beta 写在 Directory.Build.props 里
    #       却没有任何 tag（最新只到 v0.3.3-beta），用户装的版本对不上仓库中的不可变坐标。
    #       2026-09-20 起 v0.3.4-beta 已按"一条提交 = 一个版本"补齐标签，且 -Tag 会校验
    #       已存在的同名标签是否指向本次 HEAD（见下面的标签段）。
    #       推送是【有副作用】的动作，故意留给人工执行（脚本会打印该执行的命令）。
    [switch]$Tag = $false
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
# 双宿主兼容助手（Get-Sha256Hex）—— 为什么不直接用 Get-FileHash，见该文件头注释。
. (Join-Path $RepoRoot "scripts\ps-compat.ps1")
$AppProj  = Join-Path $RepoRoot "src\TrueToneCap.App\TrueToneCap.App.csproj"
$BuildProps = Join-Path $RepoRoot "Directory.Build.props"

# ══════════════════════════════════════════════════════════════
#  解析版本号（单一事实源: Directory.Build.props → App.csproj 兜底）
# ══════════════════════════════════════════════════════════════
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ""
    # 首选 Directory.Build.props（全仓库唯一版本声明处）
    # 注意: 标签可能带 Condition 等属性，故用 <Version\b[^>]*> 匹配
    foreach ($src in @($BuildProps, $AppProj)) {
        if (-not (Test-Path $src)) { continue }
        $text = Get-Content $src -Raw
        if ($text -match '<Version\b[^>]*>\s*([^<]+?)\s*</Version>') {
            $Version = $Matches[1].Trim()
            Write-Host "ℹ️  版本号未指定，从 $(Split-Path -Leaf $src) 读取: $Version" -ForegroundColor DarkGray
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "无法从 Directory.Build.props 或 App.csproj 解析 <Version>，请通过 -Version 显式指定。"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    # 输出目录沿用历史命名习惯（带 v 前缀，如 publish\TrueToneCap-v0.3.4-beta）。
    # ⚠️ 2026-09-14 修正: 此前注释说"带 v 前缀"但代码写的是 "TrueToneCap-$Version"，
    #    实际产出 TrueToneCap-0.3.4-beta —— 注释与代码相反，且与 pack.ps1 的打包名
    #    （TrueToneCap-v<版本>-win-x64.7z）及历史目录 TrueToneCap-v0.3.3-beta 都不一致。
    #    与 pack.ps1:71 保持同样的幂等补 v 写法（-Version 若已带 v 则不重复添加）。
    $dirTag = if ($Version -match '^[vV]') { $Version } else { "v$Version" }
    $OutputDir = Join-Path "publish" "TrueToneCap-$dirTag"
}

# 目录命名可保留 "v" 前缀（约定俗成），但 MSBuild/NuGet 的 Version 属性
# 【不接受】前导 "v"（会触发版本格式错误）。故此处剥离，仅用于 -p:Version=。
$VersionForMsBuild = $Version -replace '^[vV]', ''
if ($VersionForMsBuild -ne $Version) {
    Write-Host "ℹ️  检测到版本前缀 'v'，已为 MSBuild 剥离: $Version → $VersionForMsBuild" -ForegroundColor DarkGray
}
# 基本合法性校验（major[.minor[.patch[.revision]]][-prerelease][+metadata]）
if ($VersionForMsBuild -notmatch '^\d+(\.\d+){0,3}(-[0-9A-Za-z\-\.]+)?(\+[0-9A-Za-z\-\.]+)?$') {
    throw "版本号 '$Version' 不是合法的 NuGet/MSBuild 版本字符串。请使用形如 0.3.4-beta 或 1.0.0 的形式。"
}

# FileVersion / AssemblyVersion 必须和 Version 一起覆盖。
#   Directory.Build.props 把它们钉成 0.3.7.0，且带 Condition="'$(…)' == ''" —— -p:Version 不会连带改它们。
#   2026-10-21 实测：0.3.9-beta 的包里 ProductVersion 已是 0.3.9-beta+<sha>，
#   而 Windows 属性页显示的 FileVersion 仍是 0.3.7.0。
#   取 x.y.z[.w]（去掉 -pre / +meta），不足 4 段补 0（商店要求 Identity 第四段为 0）。
$verParts = @((($VersionForMsBuild -split '[-+]')[0]).Split('.') | Select-Object -First 4)
while ($verParts.Count -lt 4) { $verParts += '0' }
$Version4 = ($verParts -join '.')

# 归一化为绝对路径（后续 Get-ChildItem / Copy-Item 均依赖此路径）
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot $OutputDir
}

Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  TrueToneCap 发布脚本                            ║"
Write-Host "║  版本: $Version"
Write-Host "║  配置: $Configuration | 运行时: $Runtime"
Write-Host "║  ReadyToRun: $(-not $NoReadyToRun) | Core AOT: $CoreAot"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [-1/6] 校验工作区干净 —— 保证 build-info.json 的 commit SHA 可复现
# ══════════════════════════════════════════════════════════════
# build-info.json 会记录 HEAD 的 commit SHA。若工作区存在未提交/未跟踪的改动，
# 产物的真实内容与该 SHA 不符 —— 事后既无法复现，也无法据 SHA 定位问题版本。
# 故默认拒绝；确需从脏工作区出包请显式 -AllowDirty（元数据会标记 dirtyWorktree=true）。
$WorktreeDirty = $false
try {
    $dirtyOutput = (& git -C $RepoRoot status --porcelain 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $dirtyLines = @($dirtyOutput | Where-Object { $_ -and $_.Trim() })
        if ($dirtyLines.Count -gt 0) {
            $WorktreeDirty = $true
            if ($AllowDirty) {
                Write-Warning "   ⚠️ 工作区不干净（-AllowDirty 已指定），将强行继续 ($($dirtyLines.Count) 项改动)"
                foreach ($d in $dirtyLines) { Write-Host "     $d" -ForegroundColor DarkGray }
            } else {
                Write-Host ""
                Write-Host "❌ 工作区不干净，拒绝发布:" -ForegroundColor Red
                foreach ($d in $dirtyLines) { Write-Host "     $d" -ForegroundColor Red }
                Write-Host ""
                Write-Host "   请先提交或储藏这些改动。确需从脏工作区出包请显式指定 -AllowDirty" -ForegroundColor Yellow
                Write-Host "   （产物将不可复现，build-info.json 会标记 dirtyWorktree=true）。" -ForegroundColor Yellow
                throw "工作区不干净: 共 $($dirtyLines.Count) 项未提交/未跟踪改动。"
            }
        } else {
            Write-Host "   ✅ 工作区干净 (commit $((& git -C $RepoRoot rev-parse --short HEAD 2>$null).Trim()))"
        }
    } else {
        Write-Warning "   ⚠️ 无法读取 git 工作区状态（git 不可用或不在仓库中），跳过该校验"
    }
} catch {
    if ($_.Exception.Message -like '工作区不干净*') { throw }
    Write-Warning "   ⚠️ 工作区校验异常，跳过: $($_.Exception.Message)"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [0/6] 校验依赖完整性 —— 缺失即阻断
# ══════════════════════════════════════════════════════════════
Write-Host "[0/6] 验证依赖完整性..."

# 原生工具链单一事实源: Core/Resources/（Publish 时复制进发布包）
$toolsSrc = Join-Path $RepoRoot "src\TrueToneCap.Core\Resources"
# OCR 模型单一事实源: App/Models/
$modelsSrc = Join-Path $RepoRoot "src\TrueToneCap.App\Models"

$toolFiles = [ordered]@{
    "avifenc.exe" = "AVIF 编码器"
    "cjpegli.exe" = "JPEG LI 编码器"
    "cwebp.exe"   = "WebP 编码器"
    "cjxl.exe"    = "JPEG XL 编码器"
    # 2026-10-31：可选无损压缩后端。缺它程序自动回退内置 zlib-ng，
    # 但随包分发的承诺是"有它" ⇒ 这里仍按必需件检查/复制/校验哈希。
    "deflate.dll" = "无损压缩（libdeflate）"
}
$modelFiles = [ordered]@{
    "PP-OCRv6_medium_det.onnx" = "ONNX 文字检测"
    "PP-OCRv6_medium_rec.onnx" = "ONNX 文字识别"
    "ppocrv6_dict.txt"         = "中英文字典"
}

$missing = [System.Collections.Generic.List[string]]::new()

# VS-Cap（Qt 侧车）已于 2026-10-27 归档：App.csproj 不再有 data\vscap 那条 glob，
# 这枚包里【不该】出现 ttc-vscap.exe / Qt6*.dll。verify-package.ps1 的 [5.5/6] 守这个方向。

# 原生工具的「版本 + 来源」记录，由 [0/6] 的 SHA256 校验填充，最终写入 build-info.json
$toolsInfo = @()

if ($SkipDependencyCheck) {
    Write-Warning "   ⚠️ 已跳过依赖校验 (-SkipDependencyCheck)"
} else {
    # tools
    if (Test-Path $toolsSrc) {
        foreach ($entry in $toolFiles.GetEnumerator()) {
            $p = Join-Path $toolsSrc $entry.Key
            if (Test-Path $p) {
                $sz = [math]::Round((Get-Item $p).Length / 1KB, 1)
                Write-Host "   ✅ $($entry.Key)  ($($entry.Value), $sz KB)"
            } else {
                Write-Host "   ❌ 缺少: $($entry.Key) ($($entry.Value))" -ForegroundColor Red
                $missing.Add("src/TrueToneCap.Core/Resources/$($entry.Key)")
            }
        }
    } else {
        Write-Host "   ❌ 原生工具源目录不存在: $toolsSrc" -ForegroundColor Red
        $missing.Add("src/TrueToneCap.Core/Resources/")
    }

    # models
    if (Test-Path $modelsSrc) {
        foreach ($entry in $modelFiles.GetEnumerator()) {
            $p = Join-Path $modelsSrc $entry.Key
            if (Test-Path $p) {
                $sz = [math]::Round((Get-Item $p).Length / 1MB, 1)
                Write-Host "   ✅ $($entry.Key)  ($($entry.Value), $sz MB)"
            } else {
                Write-Host "   ❌ 缺少: $($entry.Key) ($($entry.Value))" -ForegroundColor Red
                $missing.Add("src/TrueToneCap.App/Models/$($entry.Key)")
            }
        }
    } else {
        Write-Host "   ❌ OCR 模型源目录不存在: $modelsSrc" -ForegroundColor Red
        $missing.Add("src/TrueToneCap.App/Models/")
    }

    # ── OCR 模型「完整性」校验 (models-manifest.json) ──
    # 背景: 上面只检查了"文件存在"。模型是外网下载的 ONNX 二进制，会被 OnnxRuntime 直接加载，
    #       存在 ≠ 正确 —— 挡不住「被替换成同体积恶意模型」或「下载被截断/损坏」。
    #       工具侧 2026-09-14 已补齐 tools-manifest.json 强校验，模型侧长期空白，
    #       是供应链上唯一没有完整性校验的一环（2026-09-15 补齐）。
    $modelsManifestPath = Join-Path $modelsSrc "models-manifest.json"
    if (Test-Path $modelsManifestPath) {
        try {
            $modelsManifest = Get-Content $modelsManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($m in $modelsManifest.models) {
                $mp = Join-Path $modelsSrc $m.name
                if (-not (Test-Path $mp)) {
                    Write-Host "   ❌ 模型缺失: $($m.name)" -ForegroundColor Red
                    $missing.Add("模型缺失: $($m.name)")
                    continue
                }
                $actualHash = (Get-Sha256Hex $mp).ToLowerInvariant()
                if ($actualHash -ne $m.sha256.ToLowerInvariant()) {
                    Write-Host "   ❌ 模型 SHA256 不匹配: $($m.name)" -ForegroundColor Red
                    Write-Host "      期望: $($m.sha256)" -ForegroundColor Red
                    Write-Host "      实际: $actualHash" -ForegroundColor Red
                    $missing.Add("模型被替换或损坏: $($m.name)")
                } else {
                    Write-Host "   🔒 $($m.name) — SHA256 校验通过 ($($m.note))"
                }
            }
        } catch {
            Write-Host "   ❌ models-manifest.json 解析失败: $($_.Exception.Message)" -ForegroundColor Red
            $missing.Add("models-manifest.json 解析失败")
        }
    } else {
        Write-Host "   ⚠️ 未找到 models-manifest.json，跳过模型完整性校验" -ForegroundColor Yellow
    }

    # ── VS-Cap 侧车：已归档（2026-10-27），这里不再有"要么编出来、要么 -NoVSCap"的分支 ──
    # 历史：侧车由 App.csproj 一条 glob 进包，目录不存在时展开成 0 个条目 ⇒ 构建与打包全绿而功能不在。
    # 现在那条 glob 与「VS-Cap 模式」一起删了；包内若还冒出 data\vscap ⇒ verify-package 判红。

    if ($missing.Count -gt 0) {
        Write-Host ""
        Write-Host "❌ 依赖校验失败 — 拒绝继续发布，以下依赖缺失:" -ForegroundColor Red
        foreach ($m in $missing) { Write-Host "     · $m" -ForegroundColor Red }
        Write-Host ""
        Write-Host "提示: 模型可由 .\download_models.ps1 获取；原生工具需放入 src/TrueToneCap.Core/Resources/。" -ForegroundColor Yellow
        throw "依赖校验失败: 共 $($missing.Count) 项缺失。"
    }

    # ── 原生工具链「来源与完整性」校验 (tools-manifest.json) ──
    # 背景: 上面只检查了"文件存在"。存在 ≠ 正确 —— 无法回答"包里的 avifenc 是哪个版本、
    #       有没有被替换过"。模型侧早已有 SHA256 固化（download_models.ps1），
    #       工具侧此前完全空白，是供应链上唯一没有校验的一环。
    # 清单同时承担「版本记录」职责: 校验通过后把版本写进 build-info.json，
    # 让"本次发布用的 libavif/libjxl/libwebp 分别是哪一版"可追溯。
    $toolsManifestPath = Join-Path $toolsSrc "tools-manifest.json"
    if (Test-Path $toolsManifestPath) {
        try {
            $toolsManifest = Get-Content $toolsManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            throw "tools-manifest.json 解析失败: $($_.Exception.Message)"
        }
        if (-not $toolsManifest.tools) {
            throw "tools-manifest.json 缺少 tools 数组，无法校验原生工具链来源。"
        }
        foreach ($t in $toolsManifest.tools) {
            $tp = Join-Path $toolsSrc $t.file
            if (-not (Test-Path $tp)) {
                # 存在性已由上面的循环记入 $missing，此处不重复添加，避免同一问题报两遍
                continue
            }
            $wantHash = [string]$t.sha256
            $actualHash = (Get-Sha256Hex $tp).ToLowerInvariant()
            if ($actualHash -ne $wantHash.ToLowerInvariant()) {
                Write-Host ""
                Write-Host "❌ 原生工具校验失败: $($t.file)" -ForegroundColor Red
                Write-Host "     清单期望 SHA256: $wantHash" -ForegroundColor Red
                Write-Host "     实际 SHA256    : $actualHash" -ForegroundColor Red
                Write-Host "   若确为『有意升级工具』，请按 tools-manifest.json 顶部说明同步更新哈希与版本号，" -ForegroundColor Yellow
                Write-Host "   不要靠跳过校验让发布通过。" -ForegroundColor Yellow
                throw "原生工具 SHA256 不匹配: $($t.file)"
            }
            $ver = if ($t.version) { " $($t.version)" } else { " (版本未知)" }
            Write-Host "   ✅ $($t.file)  SHA256 校验通过 — $($t.project)$ver"
            $toolsInfo += [pscustomobject]@{
                file    = $t.file
                project = $t.project
                version = $t.version
            }
        }
    } else {
        Write-Warning "   ⚠️ 未找到 $toolsManifestPath —— 原生工具链无来源/版本记录，本次发布不可追溯。"
        Write-Host "     建议新增该文件（字段与升级步骤见 ONNX 模型哈希固化的同一套约定）。" -ForegroundColor Yellow
    }

    Write-Host "   ✅ 依赖完整（工具 $($toolFiles.Count) 项 / 模型 $($modelFiles.Count) 项）"
    Write-Host ""
}

# ══════════════════════════════════════════════════════════════
#  环境预检: HLSL 编译器 (fxc.exe)
# ══════════════════════════════════════════════════════════════
# 背景: *.cso 被 .gitignore 排除、不入库，故全新克隆必须由 csproj 的 CompileShaders
#       Target 现场编译，而它依赖 Windows SDK 自带的 fxc.exe（不是 dxc —— 见
#       Shaders/CompileShaders.ps1 头部关于 SM6/DXIL 在 D3D11 上 E_INVALIDARG 的说明）。
#
# 之所以【只警告不阻断】: 本机若已存在 .cso 且比 .hlsl 新，Target 会跳过、构建照常成功。
# 硬阻断会误伤这类正常的本地构建；但全新克隆/干净 CI 缺 fxc 必然失败，故提前提示。
$fxcCmd = Get-Command "fxc.exe" -ErrorAction SilentlyContinue
if (-not $fxcCmd) {
    # ⚠️ 必须与 Shaders/CompileShaders.ps1、Services/ShaderCompiler.cs 的 FindFxc()
    #    保持同一套查找逻辑：枚举 Windows Kits\10\bin\<version>\x64\fxc.exe 取最高可用版本。
    #    【不要写死 SDK 版本号】—— 本机实测只有 10.0.26100.0 与 10.0.28000.0，
    #    10.0.22621.0 根本不存在；写死会导致只装了其他版本的机器误报"未找到 fxc.exe"。
    $pfx86 = ${env:ProgramFiles(x86)}
    if (-not $pfx86) { $pfx86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86) }
    $kitRoot = Join-Path $pfx86 "Windows Kits\10\bin"
    $fxcCmd = Get-ChildItem -Path $kitRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\fxc.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
# 记录 Windows SDK 版本：CSO 由它编译，不同版本产出可能不同，
# 不记下来这一层就无法复现（build-info.json 原有 dotnetSdk，但没有 winSdk）。
$winSdkVersion = ""
if ($fxcCmd) {
    $fxcResolved = if ($fxcCmd -is [string]) { $fxcCmd } else { $fxcCmd.Source }
    if ($fxcResolved -match '(10\.0\.\d+\.\d+)') { $winSdkVersion = $Matches[1] }
    Write-Host "   ✅ fxc.exe 可用 (HLSL 预编译)$(if ($winSdkVersion) { " — Windows SDK $winSdkVersion" })"
} else {
    Write-Warning "   ⚠️ 未找到 fxc.exe（已查找 PATH 与 Windows Kits\10\bin\<version>\x64）。"
    Write-Host "      *.cso 不入库，若本机也没有现成的 .cso，CompileShaders Target 会失败。" -ForegroundColor Yellow
    Write-Host "      请安装 Windows SDK，或确认 src\TrueToneCap.App\Shaders\*.cso 已存在。" -ForegroundColor Yellow
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  构建额外参数
# ══════════════════════════════════════════════════════════════
$extraArgs = @()

# ⚠️ 框架依赖时**强制关闭 R2R**，且不受 -NoReadyToRun 影响之外的选择。
# 理由（2026-10-07 实测）：R2R 编译产物与**目标运行时**绑定。
#   自包含时 R2R 映像里含的是自包含布局的引用；改成框架依赖后仍打 R2R，
#   会产出一个 122 MB 的 TrueToneCap.r2r.dll（实测 MSIX 因此从 84.6 MB 反弹到 175.8 MB），
#   既与"瘦身"目标相反，也让映像与框架依赖布局不匹配。
#   ⇒ 框架依赖 = 纯 JIT（这也是官方对 framework-dependent 的常规做法）。
if ($FrameworkDependent -or $Trim) {
    if ($FrameworkDependent) {
        Write-Host "   🐢 ReadyToRun 已禁用（框架依赖模式下强制；R2R 映像与框架依赖布局不匹配）" -ForegroundColor DarkGray
    } else {
        Write-Host "   🐢 ReadyToRun 已禁用（裁剪模式下强制；R2R 会把裁掉的体积以原生码加回，抵消收益）" -ForegroundColor DarkGray
    }
} elseif (-not $NoReadyToRun) {
    $extraArgs += "-p:PublishReadyToRun=true"
    $extraArgs += "-p:PublishReadyToRunComposite=true"
    Write-Host "   ⚡ ReadyToRun 已启用 (复合映像)"
} else {
    Write-Host "   🐢 ReadyToRun 已禁用 (纯 JIT)"
}

if ($CoreAot) {
    # ⚠️ 2026-09-15 更正：这个开关此前【完全没有生效】。
    #    全仓 grep `CorePublishAot` 只有本文件这一处产出，没有任何 csproj 消费它；
    #    `PublishAot` / `PublishTrimmed` / `TrimMode` / `IsAotCompatible` 在各 csproj 中零命中。
    #    即：下面这行传下去后无人读取，实际产出的仍是 JIT + ReadyToRun，
    #    而控制台却打印"已启用"，属于典型的"假生效"。
    #    另外 Core 是【库项目】，对库设置 AOT 本身就没有意义 —— AOT 只作用于可执行入口。
    #    在真正实现（于 App 入口启用并验证 WinUI/WinAppSDK 的 AOT 兼容性）之前，
    #    这里改为明确告警而不是假装生效。
    $extraArgs += "-p:CorePublishAot=true"
    Write-Host "   ⚠️ -CoreAot 目前【不会真正启用 AOT】（库项目不支持，且无 csproj 消费该属性）" -ForegroundColor Yellow
    Write-Host "      实际产物仍为 JIT + ReadyToRun。本开关暂为占位，勿据此认为已做 AOT 发布。" -ForegroundColor Yellow
}

# 版本号注入（覆盖 Directory.Build.props 的默认值；已剥离 v 前缀）
$extraArgs += "-p:Version=$VersionForMsBuild"
# 这两个不跟着 -p:Version 走（props 里各自有 Condition 的非空默认值），必须显式一起传，
# 否则包内 ProductVersion 与 FileVersion 会分属两个版本号。
$extraArgs += "-p:AssemblyVersion=$Version4"
$extraArgs += "-p:FileVersion=$Version4"

# ═══ MSIX 打包开关透传（2026-10-07）═══
# 只传这一个属性：MSIX 的"成套"约束（WindowsPackageType=MSIX + EnableMsixTooling
# + AppxManifest 项）全部在 App.csproj 的条件块里，由它一处决定，避免半套配置。
# 实测教训（写在这里防止以后"优化"掉）：若只设 WindowsPackageType=MSIX 而漏掉
# EnableMsixTooling，`dotnet build` 会**成功退出但一个包都不产出**（假成功），
# 而 MSIX 自己的 targets 也会因 AppxManifest 项缺失而报 "Improper project configuration"。
if ($Msix) {
    $extraArgs += "-p:TtcMsixPackage=true"
    Write-Host "   📦 MSIX 打包已启用（产出 .msix + .appxsym；与绿色包并行）" -ForegroundColor Cyan
}

# ═══ 框架依赖透传（2026-10-07）═══
# 实测体积：自包含 150.90 MB → 框架依赖 84.57 MB（-66.3 MB / -44%）。
# ⚠️ 只传属性、不做联动：TtcMsixPackage 与 TtcFrameworkDependent 在 App.csproj 里
#    是【两个独立开关】（只传后者会得到框架依赖的未打包构建，那是合法用法）。
#    但发布场景下两者应当配套 —— 否则产出的绿色包要求用户预装运行时，与"解压即用"矛盾。
#    故这里在"没开 -Msix 却开了 -FrameworkDependent"时明确告警。
if ($FrameworkDependent) {
    $extraArgs += "-p:TtcFrameworkDependent=true"
    if ($Msix) {
        Write-Host "   🪶 框架依赖已启用：MSIX 体积显著减小（实测 150.9 → 84.6 MB）" -ForegroundColor Cyan
        Write-Host "      代价：目标机需有 .NET 与 WinAppSDK 运行时（商店包由 Windows 自动解析）" -ForegroundColor DarkGray
    } else {
        Write-Host "   ⚠️ -FrameworkDependent 但未加 -Msix" -ForegroundColor Yellow
        Write-Host "      绿色包将变成【框架依赖】：用户解压后若没装 .NET / WinAppSDK 运行时则无法启动，" -ForegroundColor Yellow
        Write-Host "      与'解压即用'的定位冲突。发布绿色包请去掉本开关。" -ForegroundColor Yellow
    }
}

# ═══ 裁剪透传（2026-10-26）═══
# 只传这一个属性：NoWarn（IL2104）与 TrimmerRootDescriptor 都在 App.csproj 的条件块里，
# 由它一处决定，避免半套配置。自有代码反射（IL2026/IL2075）不在此压制 —— 会在构建时判红。
if ($Trim) {
    $extraArgs += "-p:PublishTrimmed=true"
    Write-Host "   ✂️  裁剪已启用（实测 151 → 114 MB）—— 产物分发前必须过实机启动回归" -ForegroundColor Cyan
}

# ══════════════════════════════════════════════════════════════
#  [0.5/6] 校验依赖锁定文件存在（保证可复现还原）
# ══════════════════════════════════════════════════════════════
# 项目启用 RestorePackagesWithLockFile=true，锁定文件必须先提交再发布，
# 否则本次发布的传递依赖可能与既有产物不一致（版本漂移）。
$lockProjects = @("App", "Core", "LogViewer", "Test", "Tools")
$missingLocks = @()
foreach ($lp in $lockProjects) {
    $lockPath = Join-Path $RepoRoot "src\TrueToneCap.$lp\packages.lock.json"
    if (-not (Test-Path $lockPath)) { $missingLocks += "src/TrueToneCap.$lp/packages.lock.json" }
}
if ($missingLocks.Count -gt 0) {
    Write-Host ""
    Write-Host "❌ 缺少依赖锁定文件:" -ForegroundColor Red
    foreach ($m in $missingLocks) { Write-Host "     · $m" -ForegroundColor Red }
    Write-Host "   请先执行 dotnet restore 生成并提交它们，以保证发布可复现。" -ForegroundColor Yellow
    throw "依赖锁定文件缺失: 共 $($missingLocks.Count) 项。"
}
Write-Host "   ✅ 依赖锁定文件齐全 ($($lockProjects.Count) 个项目)"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1/6] 准备输出目录
# ══════════════════════════════════════════════════════════════
Write-Host "[1/6] 准备输出目录..."

$running = Get-Process -Name "TrueToneCap" -ErrorAction SilentlyContinue
if ($running) {
    if ($Force) {
        Write-Warning "   ⚠️ -Force 已指定：强制终止正在运行的 TrueToneCap.exe (PID: $($running.Id -join ','))"
        Stop-Process -Name "TrueToneCap" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    } else {
        throw "TrueToneCap.exe 正在运行 (PID: $($running.Id -join ','))。请先退出程序，或显式使用 -Force 强制终止（未保存的截图/标注工作将丢失）。"
    }
}

# ═══ 2026-09-15 修正（P1-4）：清理动作加白名单，且不得早于测试门禁 ═══
#   ① Remove-Item -Recurse -Force 原本无白名单 —— -OutputDir 传错会直接摧毁任意目录；
#   ② 原顺序是「先删目录、后跑门禁」—— 门禁一旦红，上一版可用产物已被删除，
#      等于把唯一能回退的东西也毁掉（且此时已经来不及复原）。
#   故此处只做校验，真正的删除延后到门禁通过之后（见 [1.6/6]）。
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'publish'))
# ⚠️ 比较前必须补上尾部路径分隔符：否则 "…\publish-evil\TrueToneCap-v1" 也会
#    满足 StartsWith("…\publish")，形成前缀绕过 —— 白名单就形同虚设，
#    而随后的 Remove-Item -Recurse -Force 会真的删除那个目录。
$publishRootSep = $publishRoot.TrimEnd('\') + '\'
if (-not $OutputDir.StartsWith($publishRootSep, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "输出目录必须位于 publish\ 之内：$OutputDir（白名单校验，防止误删其它目录）"
}
if ([System.IO.Path]::GetFileName($OutputDir) -notmatch '^TrueToneCap-v') {
    throw "输出目录名必须以 'TrueToneCap-v' 开头：$OutputDir（发布产物命名约定）"
}
Write-Host "   ✅ 输出目录校验通过: $OutputDir"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [1.5/6] 发布前测试门禁 —— 测试红则不出包
# ══════════════════════════════════════════════════════════════
# 只跑「无需桌面会话」的 7 个套件：
#   --usability-tests / --wgc-avif-tests 依赖真实桌面会话与 GPU 捕获，
#   在无头环境（含 CI）必然失败，故不纳入默认门禁。
#
# ⚠️ 绝不能用 `dotnet test`：测试项目未引用 Microsoft.NET.Test.Sdk，
#    `dotnet test` 会以退出码 0 静默执行零个测试（假绿灯）。
#    唯一有效入口是 `dotnet run --project <Test> -- <套件参数>`。
#    另注：测试运行器一次只接受一个套件参数（命中首个即返回），故此处逐个调用。
Write-Host "[1.5/6] 发布前测试门禁..."

$testProject = Join-Path $RepoRoot "src\TrueToneCap.Test\TrueToneCap.Test.csproj"
# 2026-09-14 变更: 加入 --usability-tests（+153 断言，门禁覆盖率 199/373 → 352/373）。
#   此前它因"需要桌面会话"被整体排除，但 18 个小节里只有 DisplayInfo / RegionDetector
#   两处真正依赖显示器，已改为"无显示器则显式跳过（计入 skipped，不算通过也不算失败）"，
#   其余纯 CPU 用例在 headless 下同样有效。
#   仍排除的只有 --wgc-avif-tests（需真实 WGC 捕获 + GPU）。
$gateSuites = @(
    "--unit-tests",
    "--color-tests",
    "--encoding-tests",
    "--service-tests",
    "--ocr-tests",
    # 2026-10-26 随"识别二维码"新增。⚠ 此列表必须与 scripts/verify.ps1 的 $suites 保持同一集合 ——
    # 漏一条的直接后果：断言下限(=基线总断言)按 8 套件计，这里只跑 7 个 ⇒ 永远差一条的量 ⇒
    # B/D 发布场景误红（实测 2026-10-26：1/7 个套件未通过，实为 1914 < 1961 的下限检查）。
    "--qr-tests",
    "--format-tests",
    "--usability-tests"
)

if ($SkipTests) {
    Write-Warning "   ⚠️ 已跳过发布前测试 (-SkipTests)"
} elseif (-not (Test-Path $testProject)) {
    Write-Warning "   ⚠️ 未找到测试项目 $testProject，跳过测试门禁"
} else {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $failedSuites = @()
    $totalPassed = 0
    Push-Location $RepoRoot
    try {
        foreach ($suite in $gateSuites) {
            Write-Host "   ▶ $suite" -ForegroundColor DarkGray
            # ═══ 2026-09-15 P0 修复: 只判退出码是不够的 ═══
            # 退出码 0 而输出里没有 RESULT 行 = 零断言执行（典型诱因：套件名拼错被运行器
            # 当成未知参数、或运行器吞参数）。此时"测试通过"是假绿灯，照常出包并盖章
            # SHA256SUMS。此处与 scripts/verify.ps1 采用同一判据：
            #   1) 退出码必须为 0
            #   2) 必须解析到 RESULT 行（passed/failed 两个字段）
            #   3) failed 必须为 0
            # ⚠ 子进程往 stderr 写"降级留痕"是常态（例：[NativeEncoderGuard] ⚠️ WebP 崩溃隔离
            #   InvalidOperationException: cwebp 失败 (exit=1)），而 $ErrorActionPreference='Stop'
            #   配上 2>&1 会把每一行 stderr 变成 ErrorRecord 并【当场抛出】⇒
            #   10-03 实测：一次 1154 通过 / 0 失败的绿轮被判定为发布失败，包根本没出。
            #   判据仍然只有那三条（退出码 / 有 RESULT 行 / failed==0），stderr 只作输出不作判决。
            $eap0 = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $raw = & dotnet run --project $testProject -c $Configuration -- $suite 2>&1
                $code = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $eap0
            }
            $output = ($raw | Out-String)
            Write-Host $output
            $passed = $null; $suiteFailed = $null
            if ($output -match 'RESULT\s+suite=\S+\s+passed=(\d+)\s+failed=(\d+)') {
                $passed      = [int]$Matches[1]
                $suiteFailed = [int]$Matches[2]
            }
            if ($code -ne 0) {
                Write-Host "   ❌ $suite 失败 (退出码 $code)" -ForegroundColor Red
                $failedSuites += "$suite (退出码 $code)"
            } elseif ($passed -eq $null) {
                Write-Host "   ❌ $($suite): 退出码 0 但未解析到 RESULT 行 —— 判为失败（很可能零断言执行）" -ForegroundColor Red
                $failedSuites += "$suite (无 RESULT 行 / 零断言执行)"
            } elseif ($suiteFailed -gt 0) {
                Write-Host "   ❌ $suite 失败 ($suiteFailed 条断言)" -ForegroundColor Red
                $failedSuites += "$suite ($suiteFailed 条断言失败)"
            } else {
                $totalPassed += $passed
            }
        }
    } finally {
        Pop-Location
    }
    $sw.Stop()

    # 断言总数下限：防止套件被静默删减（例如误改 gateSuites 列表）后门禁仍然"全绿"
    # ⚠ 2026-09-29 审查轮 §11.4：默认值 340 是三年前的手抄快照，真实规模早已 1400+ ⇒ 删掉上千条断言
    #   这里也不会响（而 verify-publish-gates.ps1 的桩合计 368 恰好"看起来像当前规模"，把这条缺口
    #   伪装成了已判）。下限改为优先读 scripts\gate-baseline.json 的【总断言】——那份文件由
    #   verify.ps1 -WriteGateBaseline 从真读数生成，不靠人抄。读不到才退回参数值，并在话里说清用的哪个。
    $floor = $MinGateAssertions
    $floorSource = '-MinGateAssertions 参数'
    $gateBaseline = Join-Path $PSScriptRoot 'scripts\gate-baseline.json'
    $callerSetFloor = $PSBoundParameters.ContainsKey('MinGateAssertions')
    if (-not $callerSetFloor -and (Test-Path -LiteralPath $gateBaseline)) {
        try {
            $gb = Get-Content -LiteralPath $gateBaseline -Raw | ConvertFrom-Json
            if ($gb.总断言 -gt 0) {
                $floor = [int]$gb.总断言
                $floorSource = "scripts\gate-baseline.json（取数 $($gb.'取数时间')）"
            }
        } catch {
            Write-Host "   ⚠️ 基线文件读不动（$($_.Exception.Message)）⇒ 下限退回 $MinGateAssertions" -ForegroundColor Yellow
        }
    } elseif (-not $callerSetFloor) {
        # 缺文件这一支原先【静默】用 340：一次真实的 1400+ 规模照样"通过"，而上一句注释写的
        # "读不到才退回参数值，并在话里说清用的哪个"根本没兑现 ⇒ 这里必须当场点名。
        Write-Host "   ⚠️ 没有 scripts\gate-baseline.json ⇒ 下限退回手抄值 $MinGateAssertions（比真实规模低一个数量级，" -ForegroundColor Yellow
        Write-Host "      等于这一格没有牙）。修法：跑 scripts\verify.ps1 -WriteGateBaseline 生成基线再来出包" -ForegroundColor Yellow
    }
    # 下限与它的来源【无条件】说一次：原先只在"低于下限"的那条红里才打印来源，于是
    # "这一轮比的其实是手抄的 340"在最需要知道的场合（全绿、照常出包）完全看不见。
    Write-Host "   ℹ️  断言下限 $floor（来源：$floorSource）· 本轮实测 $totalPassed" -ForegroundColor DarkGray
    if ($failedSuites.Count -eq 0 -and $totalPassed -lt $floor) {
        Write-Host "   ❌ 断言总数 $totalPassed 低于下限 $floor —— 门禁疑似被静默删减" -ForegroundColor Red
        Write-Host "      下限来源：$floorSource" -ForegroundColor Red
        Write-Host "      确系有意删减：跑 scripts\verify.ps1 -WriteGateBaseline 重生成基线，让改动留在 diff 里" -ForegroundColor Yellow
        $failedSuites += "断言总数 $totalPassed < 下限 $floor（$floorSource）"
    }

    if ($failedSuites.Count -gt 0) {
        Write-Host ""
        Write-Host "❌ 发布前测试未通过，拒绝继续发布:" -ForegroundColor Red
        foreach ($f in $failedSuites) { Write-Host "     · $f" -ForegroundColor Red }
        Write-Host ""
        throw "测试门禁失败: $($failedSuites.Count)/$($gateSuites.Count) 个套件未通过。"
    }
    Write-Host "   ✅ $($gateSuites.Count) 个套件全部通过 ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"
}

# ══════════════════════════════════════════════════════════════
#  [1.6/6] 清理并重建输出目录 —— 必须在测试门禁【之后】
# ══════════════════════════════════════════════════════════════
# 路径已在 [1/6] 通过白名单校验，此处只负责删除与重建。
# 放在门禁之后的意义：门禁红时不破坏上一版产物。
if (Test-Path $OutputDir) {
    Write-Host "   🧹 清理既有输出目录: $OutputDir"
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Write-Host "   ✅ 输出目录就绪: $OutputDir"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [2/6] 编译 + 发布（csproj 的 CopyPriAndXbf Target 会自动整理 PRI/XBF/PDB）
# ══════════════════════════════════════════════════════════════
Write-Host "[2/6] 编译并发布 ($Configuration / $Runtime)..."

Push-Location $RepoRoot
try {
    # ═══ 自包含开关必须在这里按开关组装，不能在 csproj 里写死 ═══
    # 实测教训（2026-10-07）：命令行的 -p: 优先级**高于** csproj 里的属性。
    # 这里原本硬编码 `--self-contained true -p:WindowsAppSDKSelfContained=true`，
    # 于是 App.csproj 里为 TtcFrameworkDependent 写的 false **被完全覆盖** ——
    # 表现为"开了框架依赖，MSIX 反而更大（175.82 MB）"：既没瘦身，
    # 又因 @extraArgs 里的 PublishReadyToRun=true 多进了一个 122 MB 的复合 R2R 映像。
    # ⇒ 改为按 $FrameworkDependent 组装，让开关真正生效。
    $scArgs = @()
    if ($FrameworkDependent) {
        $scArgs += "--self-contained"; $scArgs += "false"
        $scArgs += "-p:WindowsAppSDKSelfContained=false"
    } else {
        $scArgs += "--self-contained"; $scArgs += "true"
        $scArgs += "-p:WindowsAppSDKSelfContained=true"
    }

    & dotnet publish $AppProj `
        -c $Configuration -r $Runtime `
        @scArgs `
        @extraArgs `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "编译失败 (dotnet publish 退出码 $LASTEXITCODE)" }
} finally {
    Pop-Location
}

# ── 锁定文件归一化（2026-09-14 实测补齐）──────────────────────────────
# `dotnet publish -r win-x64` 会把 RuntimeIdentifier 沿依赖链传递给 TrueToneCap.Core，
# 于是 Core 的 packages.lock.json 被追加一段 "net11.0-windows10.0.26100/win-x64"。
# 而 Core 是库、自身并不声明 RID：一旦提交这一段，无 RID 的普通还原就会报
# NU1004「项目的运行时标识符已更改」，CI 的 `dotnet restore --locked-mode` 直接失败。
# 实测：publish 后锁文件确实多出该段；补一次无 RID 的 `dotnet restore` 可逐字节还原。
# ⚠️ 必须放在 [6/6] 计算 lockFilesSha256 之前，否则 build-info.json 记到的是漂移后的指纹。
Write-Host "   🔒 归一化依赖锁定文件（清除 publish 注入的 RID 段）..."
Push-Location $RepoRoot
try {
    & dotnet restore TrueToneCap.slnx
    if ($LASTEXITCODE -ne 0) {
        # 2026-09-15 修正（P1-2）：原为仅 Warning 后继续。
        # 归一化失败意味着锁文件仍带着 win-x64 段 —— 这个包会一直"能发布"，
        # 但把污染提交上去后，下一次 CI 的 restore --locked-mode 才报 NU1004，
        # 故障点与成因相隔一个提交、极难定位。此处必须硬失败。
        throw "归一化还原失败（退出码 $LASTEXITCODE）：锁文件可能仍带 win-x64 段，请手工检查 src/*/packages.lock.json"
    }
} finally {
    Pop-Location
}

# ── 剔除第三方私有调试符号（2026-09-20 合规补齐）─────────────────────────
# 为什么可以删：.pdb 不是可加载模块，只有调试器会读它。实测判据 ——
#   ① DirectML.dll 的导入表 26 个模块，无一以 .pdb 结尾（PE loader 根本不会去找它）；
#   ② 两者的关联仅存在于 DirectML.dll 的 PE 调试目录里一条
#      IMAGE_DEBUG_TYPE_CODEVIEW / "RSDS" 记录（GUID 993fb961-76a9-4996-85b9-cc972ceec67a, age=1），
#      其中记录的原始路径是 Microsoft 自己的 CI 机（C:\__w\1\s\build\...\DirectML.pdb），
#      终端机器上不存在 —— 也就是说它连"本机可解析"都做不到，只对连了微软符号服务器
#      的调试会话有意义；用户侧运行时 100% 用不到。
#   ③ 文件头 magic 是 "Microsoft C/C++ "（原生 MSF 私有 PDB），不是托管 PDB，
#      因此与 .NET 运行时的符号解析路径也无交集。
# 来源: App.csproj 的 CopyPriAndXbf Target 用 $(PublishDir)*.pdb 通配「所有」pdb 搬进 data/，
#       于是把 Microsoft.AI.DirectML 包里的私有符号一并搬进了发布产物（约 12.3 MB / 占包体积 3%）。
#
# ⚠ 2026-10-07 迁 WinML 后的现状（实测）：ONNX Runtime 依赖由 Microsoft.ML.OnnxRuntime.DirectML
#    换成了 Microsoft.Windows.AI.MachineLearning 2.4.89，`microsoft.ai.directml` 已【不再出现在
#    依赖图里】，发布目录中第三方 .pdb 数量实测为 0 ⇒ 下面这段目前是【空转】。
#    保留它而不是删掉，理由：这是一道"防止第三方私有符号混进发布包"的通用闸门，
#    未来任何包（含 WinML 后续版本、或新增的厂商 EP 包）重新带上 .pdb 时它仍然拦得住；
#    删掉则会静默恢复成"谁带进来就发出去"。
#    上面的 12.3 MB 是历史实测值，保留作文档，不代表当前体积。
# 范围: 只剔第三方。TrueToneCap*.pdb 是本项目的托管符号，留着 —— 它们是用户报障时
#       异常堆栈能带上行号的唯一依据，删了反而伤害可支持性。
# 放在 [3/6]~[6/6] 之前：既让 [5.5/6] 冒烟看到的就是清理后的目录，
# 也让 [6/6] 的 SHA256SUMS.txt 不会给已删除的文件留下哈希条目。
$ownPdbPattern = '^TrueToneCap'
$thirdPartyPdbs = @(Get-ChildItem $OutputDir -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -notmatch $ownPdbPattern })
$keptPdbs       = @(Get-ChildItem $OutputDir -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match  $ownPdbPattern })
if ($thirdPartyPdbs.Count -gt 0) {
    $freed = [math]::Round((($thirdPartyPdbs | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
    foreach ($p in $thirdPartyPdbs) { Remove-Item $p.FullName -Force }
    Write-Host "   🧹 剔除第三方私有 PDB: $($thirdPartyPdbs.Count) 个 / $freed MB ($($thirdPartyPdbs.Name -join ', '))"
}
Write-Host "   ✅ 保留自家 PDB: $($keptPdbs.Count) 个 ($($keptPdbs.Name -join ', '))"

# ── 剔除维护者自测样张（2026-10-02 可用性审计 F5）────────────────────────────
# color-samples/ 是 SelfTest.cs:148 写到 AppContext.BaseDirectory 的产物，只有 --selftest
# 这条命令行入口会生成它（App.xaml.cs:77 跑完即 Environment.Exit）⇒ 用户路径不读它。
# 它出现在发布目录，是因为有人【在发布目录里跑过自测】：本轮实测到两个 7 024 730 B 的
# ____DISPLAY5-{System,BT2020}.png，时间戳比发布目录还早三天。
# 除了占体积，它还把维护者显示器的 sanitized 名字（DISPLAY5）发给用户。
$sampleDirs = @(Get-ChildItem $OutputDir -Recurse -Directory -Filter "color-samples" -ErrorAction SilentlyContinue)
if ($sampleDirs.Count -gt 0) {
    $sFiles = @($sampleDirs | Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue)
    $sFreed = [math]::Round((($sFiles | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
    foreach ($d in $sampleDirs) { Remove-Item $d.FullName -Recurse -Force }
    Write-Host "   🧹 剔除自测样张目录: $($sampleDirs.Count) 个 / $($sFiles.Count) 文件 / $sFreed MB"
}
# 删完必须自己复核：只删不验，下次被改回来也不会红
$leftover = @(Get-ChildItem $OutputDir -Recurse -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'color-samples' -or $_.Name -match '^_{2,}DISPLAY' })
if ($leftover.Count -gt 0) {
    Write-Host "   ❌ 发布目录仍残留自测样张 $($leftover.Count) 项: $(($leftover | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "   ✅ 发布目录无自测样张残留"

$buildSize = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "   📦 编译完成: $([math]::Round($buildSize,1)) MB"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [3/6] 复制原生工具链（单一事实源: Core/Resources/）
# ══════════════════════════════════════════════════════════════
Write-Host "[3/6] 复制原生工具链 → native/..."

if ($SkipDependencyCheck) {
    Write-Warning "   ⚠️ 依赖校验已跳过，工具复制可能不完整"
}

if (Test-Path $toolsSrc) {
    $nativeDir = Join-Path $OutputDir "native"
    New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null

    foreach ($file in $toolFiles.Keys) {
        $src = Join-Path $toolsSrc $file
        if (Test-Path $src) {
            Copy-Item $src $nativeDir -Force
            $sz = [math]::Round((Get-Item $src).Length / 1KB, 1)
            Write-Host "   ✅ $file ($sz KB)"
        } else {
            Write-Host "   ❌ $file 缺失" -ForegroundColor Red
            throw "原生工具复制失败: $file 不存在于 $toolsSrc"
        }
    }
    Write-Host "   📁 native/ — $($toolFiles.Count) 个文件"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [4/6] 复制 OCR 模型（单一事实源: App/Models/）
# ══════════════════════════════════════════════════════════════
Write-Host "[4/6] 复制 OCR 模型 → data/Models/..."

if (Test-Path $modelsSrc) {
    $dataModels = Join-Path $OutputDir "data\Models"
    New-Item -ItemType Directory -Path $dataModels -Force | Out-Null

    foreach ($model in $modelFiles.Keys) {
        $src = Join-Path $modelsSrc $model
        if (Test-Path $src) {
            Copy-Item $src $dataModels -Force
            $sz = [math]::Round((Get-Item $src).Length / 1MB, 1)
            Write-Host "   ✅ $model ($sz MB)"
        } else {
            Write-Host "   ❌ $model 缺失" -ForegroundColor Red
            throw "OCR 模型复制失败: $model 不存在于 $modelsSrc"
        }
    }
    Write-Host "   📁 data/Models/ — $($modelFiles.Count) 个文件"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [5/6] 复制静态文件 + 关键运行时校验
# ══════════════════════════════════════════════════════════════
Write-Host "[5/6] 复制静态文件并校验运行时..."

Copy-Item (Join-Path $RepoRoot "README.md") $OutputDir -Force
Copy-Item (Join-Path $RepoRoot "LICENSE")   $OutputDir -Force
Write-Host "   ✅ README.md + LICENSE"

# 第三方许可全文必须随包分发 —— 本包再分发 libavif/libjxl/libwebp 等原生二进制，
# 而 BSD-3-Clause / IJG / libpng / Apache-2.0 无一例外都以「再分发时保留版权与许可全文」
# 为授权条件；只放仓库根 LICENSE（本项目自己的 Apache-2.0）不构成对上游义务的履行。
# SVT-AV1 的 BSD-3-Clause-Clear 更是明文不授予专利权，缺文本等于让下游用户误以为有授权。
# 强度与 README/LICENSE 一致：$ErrorActionPreference = "Stop"，缺源文件即抛，不出包。
Copy-Item (Join-Path $RepoRoot "THIRD-PARTY-NOTICES.md") $OutputDir -Force
Copy-Item (Join-Path $RepoRoot "licenses") (Join-Path $OutputDir "licenses") -Recurse -Force
$licCount = (Get-ChildItem (Join-Path $OutputDir "licenses") -File -ErrorAction Stop).Count
Write-Host "   ✅ THIRD-PARTY-NOTICES.md + licenses/ ($licCount 个文件)"

$bootstrapDll = Join-Path $OutputDir "Microsoft.WindowsAppRuntime.Bootstrap.dll"
if (Test-Path $bootstrapDll) {
    $sz = [math]::Round((Get-Item $bootstrapDll).Length / 1KB, 1)
    Write-Host "   ✅ Bootstrap.dll ($sz KB)"
} else {
    throw "缺少 Microsoft.WindowsAppRuntime.Bootstrap.dll — 自包含发布不完整。"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [5.5/6] 出包冒烟验证 —— 「能编译」不等于「能运行」
# ══════════════════════════════════════════════════════════════
# 此前只检查了 Bootstrap.dll 是否存在就生成校验和，从不验证产物能不能跑：
# 缺 OCR 字典、native 工具缺 VC++ 运行库、着色器没编译出来（*.cso 不入库、fxc 缺失时
# Target 会被跳过）等情况都能静默通过，直到用户手上才暴露。
#
# 检查内容（详见 scripts/verify-package.ps1）：
#   主程序 / Bootstrap.dll / native 4 个 exe / 模型 3 项 的清单与尺寸下限
#   + PE 头 + 版本资源注入 + data/Shaders/*.cso 非空 + native 工具可启动探针
#   + [5/6] 许可分发断言（THIRD-PARTY-NOTICES.md / licenses/ 文件数下限 / INDEX.json 可解析）
#     与第三方 .pdb 未剔除时的对偶报警 —— 也就是说 [5/6] 与 [2/6] 的剔除逻辑互相把守。
# 放在 [6/6] 之前：不通过就不给它盖章（不生成 SHA256SUMS / build-info）。
Write-Host "[5.5/6] 出包冒烟验证..."

$smokeScript = Join-Path $RepoRoot "scripts\verify-package.ps1"
if (-not (Test-Path $smokeScript)) {
    Write-Warning "   ⚠️ 未找到 $smokeScript，跳过冒烟验证（产物未经运行前校验）"
    $SmokePassed = $false
} else {
    # 提前取一次 HEAD：冒烟会拿它与二进制里内嵌的 SourceLink commit 比对，
    # 拦住"声称 HEAD=X、实际代码是 Y"的不可复现产物（下面 [6/6] 会再取一次，无副作用）。
    if (-not $commitSha) {
        try { $commitSha = (& git -C $RepoRoot rev-parse HEAD 2>$null).Trim() } catch {}
    }
    $smokeArgs = @{ PackageDir = $OutputDir; ExpectedVersion = $VersionForMsBuild }
    if ($commitSha)      { $smokeArgs['ExpectedCommit'] = $commitSha }
    if ($SkipExecProbe)  { $smokeArgs['SkipExecProbe'] = $true }
    & $smokeScript @smokeArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "❌ 冒烟验证未通过 —— 拒绝为该产物生成校验和与构建元数据。" -ForegroundColor Red
        Write-Host "   产物保留在 $OutputDir 供排查；请修复后重新发布。" -ForegroundColor Yellow
        throw "出包冒烟验证失败 (退出码 $LASTEXITCODE)"
    }
    $SmokePassed = $true
    Write-Host "   ✅ 冒烟验证通过"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [5.6/6] MSIX 内容核对（仅 -Msix 时）
# ══════════════════════════════════════════════════════════════
# 为什么单独一步：MSIX 是**与绿色包不同的容器**，verify-package.ps1 核对的是发布目录，
# 看不见包内。2026-10-07 实测过这个盲区 —— 打出的 MSIX 里 4 枚原生编码器全部缺失，
# 而编译/发布/打包/冒烟**全程绿灯**（OCR 模型在、原生工具不在，因为前者是声明的 Content 项、
# 后者当时只是 Copy 动作）。⇒ 必须解包核对，且必须在盖章前失败。
if ($Msix) {
    Write-Host "[5.6/6] MSIX 内容核对..." -ForegroundColor Cyan
    $msixGate = Join-Path $RepoRoot "scripts\verify-msix.ps1"
    if (-not (Test-Path $msixGate)) {
        throw "缺少 $msixGate —— -Msix 已启用但核对脚本不存在，拒绝出包"
    }
    & $msixGate
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "❌ MSIX 核对未通过 —— 拒绝为该产物生成校验和与构建元数据。" -ForegroundColor Red
        Write-Host "   典型原因：原生工具/OCR 资产未进包（见 scripts/verify-msix.ps1 文件头）。" -ForegroundColor Yellow
        throw "MSIX 内容核对失败 (退出码 $LASTEXITCODE)"
    }
    Write-Host ""
}

# ══════════════════════════════════════════════════════════════
#  [5.8/6] 打版本标签（可选）
# ══════════════════════════════════════════════════════════════
# 刻意放在 [6/6] 之前：build-info.json 会记录本次的 tag，而 SHA256SUMS.txt 覆盖
# build-info.json —— 若先生成校验和再改 build-info，校验和会立刻失真。
#
# 只创建本地附注标签，【不推送】：push 是有副作用且不可逆的远程操作，
# 留给人执行（下面会打印确切命令）。
$tagName = ""
if ($Tag) {
    $tagName = "v$VersionForMsBuild"
    $existingTag = @(& git -C $RepoRoot tag -l $tagName 2>$null | Where-Object { $_ -and $_.Trim() })
    if ($existingTag.Count -gt 0) {
        # 2026-09-20 补的是【一致性校验】，不是修 bug：$tagName 在上面的赋值行就已经是非空串，
        # 所以"标签已存在 ⇒ build-info.json 的 tag 字段为空"这个猜测经核代码【不成立】，
        # 记录会照实写 v<版本>。真正缺的是下面这一步 —— 标签存在不代表它指的是这次要发布的提交
        # （0.3.4-beta 就真出现过一次：手工打的标签随后被"一条=一个版本"的重写挪了位置）。
        # 版本号 ↔ 不可变坐标是打标签的全部意义，指错了必须大声报警。
        $tagCommit = (& git -C $RepoRoot rev-list -n 1 $tagName 2>$null | Select-Object -First 1)
        $headCommit = (& git -C $RepoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
        Write-Warning "   ⚠️ 标签 $tagName 已存在，跳过创建（如需重打，请先 git tag -d $tagName）"
        Write-Host   "      build-info.json 仍会如实记录 tag = $tagName"
        if ($tagCommit -and $headCommit -and ($tagCommit.Trim() -ne $headCommit.Trim())) {
            Write-Warning ("   ❌ 标签 $tagName 指向 " + $tagCommit.Substring(0,7) +
                "，而本次 HEAD 是 " + $headCommit.Substring(0,7) + " —— 两者不一致。")
            Write-Warning    "      意味着装了这个标签的人拿到的不是你刚出的这份产物。"
            Write-Warning    "      要么先 `git tag -d $tagName` 让本脚本重打，要么确认版本该往前走了。"
        }
    } else {
        & git -C $RepoRoot tag -a $tagName -m "TrueToneCap $VersionForMsBuild"
        if ($LASTEXITCODE -ne 0) {
            throw "创建标签 $tagName 失败 (git 退出码 $LASTEXITCODE)。请检查 git 身份配置 (user.name / user.email)。"
        }
        Write-Host "   ✅ 已创建本地标签 $tagName"
        Write-Host "      ⚠️ 尚未推送；确认无误后执行: git push origin $tagName" -ForegroundColor Yellow
    }
    Write-Host ""
}

# ══════════════════════════════════════════════════════════════
#  [6/6] 产出校验和与构建元数据
# ══════════════════════════════════════════════════════════════
Write-Host "[6/6] 生成校验和与构建元数据..."

# 构建元数据（可追溯性）
$commitSha = ""
try { $commitSha = (& git -C $RepoRoot rev-parse HEAD 2>$null).Trim() } catch {}
$sdkVersion = ""
try { $sdkVersion = (& dotnet --version 2>$null).Trim() } catch {}

# 机器名：$env:COMPUTERNAME 在部分环境（CI 容器、精简 shell）未定义，会记成 null。
# 回退到 .NET 的主机名解析，再不行才留 null —— 宁可拿到主机名，也不要静默丢字段。
$builtOn = $env:COMPUTERNAME
if (-not $builtOn) {
    try { $builtOn = [System.Net.Dns]::GetHostName() } catch { $builtOn = $null }
}

# 依赖锁定指纹：对各项目 packages.lock.json 求 SHA256，拼成一个稳定标识。
# 用途：把 build-info.json 与"当次使用的精确依赖图"绑定，便于事后审计与复现。
$lockHashParts = foreach ($lp in $lockProjects) {
    $lockPath = Join-Path $RepoRoot "src\TrueToneCap.$lp\packages.lock.json"
    if (Test-Path $lockPath) {
        "$lp=$((Get-Sha256Hex $lockPath).ToLowerInvariant())"
    }
}
$lockFingerprint = ($lockHashParts -join ";")

$buildInfo = [ordered]@{
    product        = "TrueToneCap"
    version        = $VersionForMsBuild
    configuration  = $Configuration
    runtime        = $Runtime
    selfContained  = $true
    readyToRun     = (-not $NoReadyToRun)
    coreAot        = [bool]$CoreAot
    # VS-Cap 侧车已归档（2026-10-27）：不写 vscapIncluded，也不许出现 data\vscap。
    commitSha      = $commitSha
    # 版本标签（-Tag 时创建；空串表示本次发布未打标签）
    tag            = $tagName
    dotnetSdk      = $sdkVersion
    # Windows SDK 版本: CSO 由它的 fxc.exe 编译且 *.cso 不入库，
    # 不同版本产出的字节码可能不同 —— 不记录就无法真正复现本次发布。
    windowsSdk     = $winSdkVersion
    builtAtUtc     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    builtOn        = $builtOn
    # 依赖锁定指纹: 供事后追溯本次发布所用的精确依赖图（可复现性审计）
    lockFilesSha256 = $lockFingerprint
    # 工作区是否含未提交/未跟踪改动。true 表示该产物无法由 commitSha 复现。
    dirtyWorktree  = [bool]$WorktreeDirty
    # 原生工具链版本明细 (来自 tools-manifest.json，SHA256 校验通过后记录)
    tools          = $toolsInfo
    # 发布前测试门禁结果（headless 7 套件；-SkipTests 时为 "skipped"）
    testsRun       = $(if ($SkipTests) { "skipped" } else { ($gateSuites -join ",") })
    # 出包冒烟验证是否通过（false = 脚本缺失导致未验证，产物未经运行前校验）
    smokeTested    = [bool]$SmokePassed
}
$buildInfoPath = Join-Path $OutputDir "build-info.json"
# ⚠ 刻意不用 Set-Content -Encoding UTF8（5.1 必写 BOM / 7 不写 ⇒ 双宿主产物漂移），
#    与下方 SHA256SUMS.txt 同一条纪律：.NET 直写无 BOM。
[System.IO.File]::WriteAllText($buildInfoPath, ($buildInfo | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))
Write-Host "   ✅ build-info.json"

# SHA256 校验和（对发布目录内全部文件，排除校验和文件自身）
$sumsPath = Join-Path $OutputDir "SHA256SUMS.txt"
$files = Get-ChildItem $OutputDir -Recurse -File |
         Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
         Sort-Object FullName
$lines = @(foreach ($f in $files) {
    # 相对路径统一为正斜杠：coreutils 的 sha256sum 把 '\' 当转义符，
    # 原先写成 "af-ZA\Microsoft.ui.xaml.dll.mui" 时整行无法解析。
    $rel  = $f.FullName.Substring($OutputDir.Length).TrimStart('\', '/').Replace('\', '/')
    $hash = (Get-Sha256Hex $f.FullName).ToLowerInvariant()
    "$hash  $rel"
})
# 格式: UTF-8 无 BOM + LF + "<hex>  <path>"（两个空格，coreutils sha256sum 的文本模式惯例），
# 这样 `sha256sum -c SHA256SUMS.txt` 在 Linux / macOS / Git-Bash 上可直接整包校验。
# 此前是 BOM(ef bb bf) + CRLF + 反斜杠三样叠加 —— BOM 会污染首行的 hex、CRLF 会在 path
# 尾部留下 \r、反斜杠被当转义，结果是通用工具全不能用，只剩 certutil 逐个文件手算。
# 刻意不用 Set-Content: PowerShell 5.1 的 -Encoding UTF8 必写 BOM，且数组落盘固定用 CRLF。
$sumsText = ($lines -join "`n")
if ($lines.Count -gt 0) { $sumsText += "`n" }
[System.IO.File]::WriteAllText($sumsPath, $sumsText, (New-Object System.Text.UTF8Encoding $false))
Write-Host "   ✅ SHA256SUMS.txt ($($lines.Count) 个文件, UTF-8 无 BOM / LF / 正斜杠)"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  统计与汇总
# ══════════════════════════════════════════════════════════════
$totalSize  = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
$totalFiles = (Get-ChildItem $OutputDir -Recurse -File).Count

# 2026-10-31：native/ 里不再只有 exe（多了一枚可选 deflate.dll），按全部文件计数。
$nativeCount = (Get-ChildItem (Join-Path $OutputDir "native") -File -ErrorAction SilentlyContinue).Count
$modelSize   = (Get-ChildItem (Join-Path $OutputDir "data\Models") -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB
$shaderCount = (Get-ChildItem (Join-Path $OutputDir "data\Shaders") -Filter "*.cso" -ErrorAction SilentlyContinue).Count

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  发布完成!                                       ║"
Write-Host "║  版本: $Version"
Write-Host "║  📦 $([math]::Round($totalSize,1)) MB  |  $totalFiles 文件"
Write-Host "║  📂 $OutputDir"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""
Write-Host "📋 发布包内容概要:"
Write-Host "   ├─ 原生工具: $nativeCount 个 (native/)"
Write-Host "   ├─ OCR 模型: $([math]::Round($modelSize,1)) MB (data/Models/)"
Write-Host "   ├─ 着色器:   $shaderCount 个 (data/Shaders/)"
Write-Host "   ├─ 校验和:   SHA256SUMS.txt"
Write-Host "   ├─ 构建信息: build-info.json"
Write-Host "   └─ 总文件:   $totalFiles 个"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  [7/6] 旧产物保留策略（可选）
# ══════════════════════════════════════════════════════════════
# 默认 0 = 不清理（删除是不可逆操作，必须显式 -KeepLast 才会执行）。
# 只处理 publish\TrueToneCap-* 形式的版本目录，且【绝不会删本次刚产出的目录】。
if ($KeepLast -gt 0) {
    Write-Host "[7/6] 旧产物保留: 保留最近 $KeepLast 个版本目录..."
    $publishRoot = Split-Path $OutputDir -Parent
    # 安全阀: 若本次产物不在仓库的 publish\ 下（例如 -OutputDir 指向了别处），
    #   下面的清理逻辑就无从判断"该保留谁" —— 直接跳过，绝不误删。
    # ⚠️ 2026-09-14 修正: 此前这里写的是 `$OutputDir -notlike "$publishRoot*"`，
    #   而 $publishRoot 就是 $OutputDir 的父目录 —— 条件【恒为 false】，安全阀是死代码，
    #   -OutputDir 指向任意目录时都会在别处按 TrueToneCap-* 删目录。
    #   必须与"仓库 publish 目录"比较，而不是与自己的父目录比较。
    $repoPublishRoot = Join-Path $RepoRoot "publish"
    if ($OutputDir -notlike "$repoPublishRoot*") {
        Write-Warning "   ⚠️ 本次输出目录不在 $repoPublishRoot 下，跳过旧产物清理（不做无把握的删除）"
        $KeepLast = 0
    }
}
if ($KeepLast -gt 0) {
    $versionDirs = @(Get-ChildItem $publishRoot -Directory -Filter "TrueToneCap-*" -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTime -Descending)
    # 先排除本次产物，再按"保留 N 个（含本次）"计算要删除的
    $keep = @()
    foreach ($d in $versionDirs) {
        if ($keep.Count -ge $KeepLast) { break }
        $keep += $d.FullName
    }
    $toRemove = @($versionDirs | Where-Object { $keep -notcontains $_.FullName })
    if ($toRemove.Count -eq 0) {
        Write-Host "   ✅ 无需清理（共 $($versionDirs.Count) 个版本目录，未超出 $KeepLast）"
    } else {
        $freedMB = 0
        foreach ($d in $toRemove) {
            $sz = (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue |
                   Measure-Object -Property Length -Sum).Sum
            $freedMB += $sz / 1MB
            Write-Host "   🧹 删除 $(Split-Path $d.FullName -Leaf)  ($([math]::Round($sz / 1MB, 1)) MB)"
            Remove-Item -Recurse -Force $d.FullName
        }
        Write-Host "   ✅ 已清理 $($toRemove.Count) 个旧版本目录，释放 $([math]::Round($freedMB, 1)) MB"
    }
    Write-Host ""
}
