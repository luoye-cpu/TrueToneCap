# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# scripts/verify-msix.ps1 — 解包核对 MSIX 内容（打包后的独立门禁）
#
# 用法:
#   .\scripts\verify-msix.ps1 [-MsixPath <path>] [-KeepExtracted]
#
# ═══ 为什么必须有这个脚本 ═══
# 2026-10-07 实测发现的故障：用 `-p:TtcMsixPackage=true` 打出的 MSIX，
# **构建成功、打包成功、退出码 0**，但解包后核对发现
# `native\` 下 4 枚原生编码器 exe **全部不在包里**（OCR 模型却在）。
#
# 根因：MSIX 打包枚举的是 **MSBuild 声明的项（items）**，不是输出目录里的文件。
#   · OCR 模型走 <Content Include="Models\*.onnx">  → 是声明的项 → 进包
#   · 原生 exe 原本只靠 <Copy DestinationFolder="$(OutDir)native"> → 是文件系统动作 → 漏出包外
# 后果：AVIF / JPEG XL / WebP / JPEG LI 四种格式在包内整体失效。
#
# ⇒ 这类缺陷**只能靠解包核对发现**：编译、发布、打包三条路径全程绿灯。
#   所以本脚本不是"锦上添花"，而是补上那条缺失的判据。
#
# ⚠️ 与 scripts/verify-package.ps1 的分工：
#   verify-package.ps1  核对【发布目录】（绿色包 / Publish.ps1 产物）
#   verify-msix.ps1     核对【MSIX 包内】（商店/侧载产物）—— 两者是不同容器，不能互相替代

param(
    [string]$MsixPath = "",
    [switch]$KeepExtracted
)

$ErrorActionPreference = "Stop"
$failures = [System.Collections.Generic.List[string]]::new()

function Fail($msg) { $script:failures.Add($msg); Write-Host "   ❌ $msg" -ForegroundColor Red }
function Ok($msg)   { Write-Host "   ✅ $msg" -ForegroundColor Green }

Write-Host ""
Write-Host "═══ MSIX 内容核对 ═══" -ForegroundColor Cyan

# ── 定位包 ──
if (-not $MsixPath) {
    $cand = Get-ChildItem -Path (Join-Path $PSScriptRoot "..\src\TrueToneCap.App\AppPackages") `
                          -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cand) { $MsixPath = $cand.FullName }
}
if (-not $MsixPath -or -not (Test-Path $MsixPath)) {
    Write-Host "❌ 未找到 .msix。先构建：" -ForegroundColor Red
    Write-Host "   dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true"
    exit 1
}
$msix = (Resolve-Path $MsixPath).Path
Write-Host "包: $msix"
Write-Host ("大小: {0:N1} MB" -f ((Get-Item $msix).Length / 1MB))
Write-Host ""

# ── 找 makeappx（优先 NuGet 缓存里的，其次 Windows SDK）──
$makeappx = $null
$nugetCand = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Directory -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending |
             ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter "makeappx.exe" -ErrorAction SilentlyContinue |
                              Where-Object { $_.FullName -match "\\x64\\" } } |
             Select-Object -First 1
if ($nugetCand) { $makeappx = $nugetCand.FullName }
if (-not $makeappx) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $makeappx = Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
                    Sort-Object { [version]$_.Name } -Descending |
                    ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
                    Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $makeappx) { Write-Host "❌ 找不到 makeappx.exe（需 Windows SDK 或 SDK.BuildTools 包）" -ForegroundColor Red; exit 1 }
Write-Host "makeappx: $makeappx"

# ── 解包 ──
$tmp = Join-Path $env:TEMP ("ttc-msix-verify-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
& $makeappx unpack /p $msix /d $tmp /o | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "❌ makeappx unpack 失败 (exit=$LASTEXITCODE)" -ForegroundColor Red; exit 1 }
Write-Host "已解包到: $tmp"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  ① 必需内容（这一节就是 2026-10-07 那次故障的判据）
# ══════════════════════════════════════════════════════════════
Write-Host "[1/4] 必需内容..." -ForegroundColor Cyan

# 4 枚原生编码器：缺任何一个 => 对应格式在包内静默失效。
# 目录名 native\ 不可改：NativeLibraryResolver 只在 AppContext.BaseDirectory\native 下找。
$nativeTools = @("avifenc.exe", "cjxl.exe", "cjpegli.exe", "cwebp.exe")
foreach ($t in $nativeTools) {
    $p = Join-Path $tmp "native\$t"
    if (Test-Path $p) { Ok ("native\{0,-14} {1,8:N2} MB" -f $t, ((Get-Item $p).Length / 1MB)) }
    else { Fail "native\$t 缺失 ⇒ 对应编码格式在包内会静默走保底（AVIF/JXL/WebP/JPEG LI）" }
}

# OCR 模型 + 字典：缺了 OCR 直接不可用
$models = @(
    @{ Path = "data\Models\PP-OCRv6_medium_det.onnx"; Min = 10MB },
    @{ Path = "data\Models\PP-OCRv6_medium_rec.onnx"; Min = 10MB },
    @{ Path = "data\Models\ppocrv6_dict.txt";         Min = 10KB }
)
foreach ($m in $models) {
    $p = Join-Path $tmp $m.Path
    if (Test-Path $p) {
        $len = (Get-Item $p).Length
        if ($len -ge $m.Min) { Ok ("{0,-38} {1,8:N2} MB" -f $m.Path, ($len / 1MB)) }
        else { Fail "$($m.Path) 只有 $([math]::Round($len/1KB,1)) KB（下限 $([math]::Round($m.Min/1KB,0)) KB）—— 下载被截断？" }
    } else { Fail "$($m.Path) 缺失 ⇒ OCR 不可用" }
}

# 主程序
if (Test-Path (Join-Path $tmp "TrueToneCap.exe")) { Ok "TrueToneCap.exe" }
else { Fail "TrueToneCap.exe 缺失 ⇒ 包不可运行" }

# 合规文档（都要随包分发，应用内必须有可见入口 —— 缺了入口就只能显示降级界面）
#   PRIVACY.md           → 微软商店政策 10.5.1 对 Win32/Desktop Bridge 产品强制要求
#   THIRD-PARTY-NOTICES  → BSD/Apache 系列要求再分发时向最终用户展示许可全文
foreach ($doc in @(
    @{ Rel = "PRIVACY.md";               Why = "商店政策 10.5.1 要求 Win32 产品提供隐私政策" },
    @{ Rel = "THIRD-PARTY-NOTICES.md";   Why = "BSD/Apache 要求再分发时展示许可全文" }
)) {
    $p = Join-Path $tmp $doc.Rel
    if (Test-Path $p) { Ok ("{0,-30} {1,8:N1} KB" -f $doc.Rel, ((Get-Item $p).Length / 1KB)) }
    else { Fail "$($doc.Rel) 缺失 ⇒ $($doc.Why)（应用内入口会退化成降级界面）" }
}

Write-Host ""

# ══════════════════════════════════════════════════════════════
#  ② 清单身份
# ══════════════════════════════════════════════════════════════
Write-Host "[2/4] 清单身份..." -ForegroundColor Cyan
$manifestPath = Join-Path $tmp "AppxManifest.xml"
if (-not (Test-Path $manifestPath)) {
    Fail "包内没有 AppxManifest.xml"
} else {
    [xml]$m = Get-Content $manifestPath -Raw
    $id = $m.Package.Identity
    Write-Host "   Name      : $($id.Name)"
    Write-Host "   Publisher : $($id.Publisher)"
    Write-Host "   Version   : $($id.Version)"
    Write-Host "   Arch      : $($id.ProcessorArchitecture)"

    # 版本第四段必须为 0（商店规定）
    if ($id.Version -match '^(\d+)\.(\d+)\.(\d+)\.(\d+)$') {
        if ($matches[4] -eq '0') { Ok "版本第四段为 0（商店要求）" }
        else { Fail "版本第四段是 $($matches[4])，商店要求必须为 0" }
    } else { Fail "版本号格式不是 x.y.z.w: $($id.Version)" }

    # Publisher 占位符未替换 => 上架必失败
    if ($id.Publisher -match 'PLACEHOLDER') {
        Write-Host "   ⚠️  Publisher 仍是占位符 —— 上架前必须替换为 Partner Center 的标识（本机/侧载可忽略）" -ForegroundColor Yellow
    }

    # runFullTrust：桌面应用必需
    $caps = @($m.Package.Capabilities.ChildNodes | Where-Object { $_.Name -eq 'Capability' -or $_.LocalName -eq 'Capability' })
    $hasFullTrust = $caps | Where-Object { $_.GetAttribute('Name') -eq 'runFullTrust' }
    if ($hasFullTrust) { Ok "runFullTrust 能力已声明（桌面应用必需）" }
    else { Fail "缺少 rescap:runFullTrust ⇒ 包内应用无法做 D3D11 捕获/子进程调用" }

    # startupTask：MSIX 下开机自启的正确机制
    $startup = $m.SelectNodes("//*[local-name()='StartupTask']")
    if ($startup.Count -gt 0) { Ok "startupTask 已声明（MSIX 下的开机自启机制，TaskId=$($startup[0].GetAttribute('TaskId')))" }
    else { Write-Host "   ⚠️  未声明 startupTask —— MSIX 下 HKCU\...\Run 无效，开机自启将不可用" -ForegroundColor Yellow }
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  ③ 不应出现的内容
# ══════════════════════════════════════════════════════════════
Write-Host "[3/4] 卫生检查..." -ForegroundColor Cyan
$strays = @(Get-ChildItem -Recurse $tmp -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.pdb', '.ppm', '.tmp') })
if ($strays.Count -eq 0) { Ok "无 .pdb / .ppm / .tmp 残留" }
else {
    $mb = [math]::Round((($strays | Measure-Object Length -Sum).Sum) / 1MB, 2)
    Fail "$($strays.Count) 个调试/临时文件混进包（$mb MB）：$(($strays | Select-Object -First 5 -ExpandProperty Name) -join ', ')"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  ④ 汇总
# ══════════════════════════════════════════════════════════════
Write-Host "[4/4] 汇总..." -ForegroundColor Cyan
$fileCount = (Get-ChildItem -Recurse $tmp -File | Measure-Object).Count
Write-Host "   包内文件数: $fileCount"

if (-not $KeepExtracted) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "❌ MSIX 核对未通过（$($failures.Count) 项）:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "   · $_" -ForegroundColor Red }
    exit 1
}
Write-Host "✅ MSIX 核对通过 — 4 枚原生工具 + 3 项 OCR 资产 + 清单身份均就位" -ForegroundColor Green
exit 0
