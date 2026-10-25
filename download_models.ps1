# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# download_models.ps1 — 下载/更新 PP-OCRv6 ONNX 模型
# 用法: .\download_models.ps1 [-Proxy "socks5://127.0.0.1:10808"]
#
# 模型来源:
#   方案1: rapidocr Python 包 (pip install rapidocr) — 内含 PP-OCRv6 small ONNX
#   方案2: PaddleOCR 官方 BOS (Paddle 格式, 需 paddle2onnx 转换)
#   方案3: HuggingFace RapidAI/RapidOCR (需代理/认证)
#
# 当前模型:
#   检测: PP-OCRv6_det.onnx (v6 small, 9.47MB, Hmean 84.1%)
#   识别: PP-OCRv6_rec.onnx (v6 medium, 73MB, 50 语言统一)
#   字典: ppocr_keys_v2.txt (已随项目分发)
#
# 升级到 medium det: 需要 paddle2onnx >= 2.1.0 + paddlepaddle >= 3.0.0.dev20250426
#   1. pip install --pre paddlepaddle -i https://www.paddlepaddle.org.cn/packages/nightly/cpu/
#   2. pip install paddle2onnx==2.1.0
#   3. 下载 Paddle 模型:
#      https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-OCRv6_medium_det_infer.tar
#   4. paddlex --paddle2onnx --paddle_model_dir <dir> --onnx_model_dir <out> --opset_version 16

param(
    [string]$Proxy = "",

    # 跳过 SHA256 校验（仅在模型【有意升级】且已确认新来源可信时使用）
    # 使用后请立即用实际下载结果更新下方的 $expectedSha256，否则校验形同虚设。
    [switch]$SkipHashCheck = $false
)

$ErrorActionPreference = "Stop"
# 双宿主哈希助手 —— 模型校验不能依赖 Get-FileHash 的 cmdlet 解析（PSModulePath 遮蔽，见该文件头注）
. (Join-Path $PSScriptRoot "scripts\ps-compat.ps1")
$outDir = Join-Path $PSScriptRoot "src\TrueToneCap.App\Models"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ── 方案1: 从 rapidocr Python 包提取 (最可靠) ──
Write-Host "=== Checking rapidocr Python package ===" -ForegroundColor Cyan
$rapidocrModels = ""
try {
    $rapidocrModels = py -c "import rapidocr, os; print(os.path.join(os.path.dirname(rapidocr.__file__), 'models'))" 2>$null
} catch {}

if ($rapidocrModels -and (Test-Path $rapidocrModels)) {
    # ⚠️ 2026-09-15 修正：原实现会把 rapidocr 的 small 版 det 复制成 PP-OCRv6_det.onnx。
    #    但本仓库使用的是 PP-OCRv6_medium_det.onnx（medium，29.7 MB）——
    #    那个 small 副本【既不会被代码加载】，又会在 Models 目录留下一个 9.5 MB 的
    #    易混淆文件（实测曾因此让人误以为"检测模型只有 9 MB"）。故只提示、不复制。
    Write-Host "  ⓘ 检测到 rapidocr 包，但它只提供 small 模型，而本项目需要 medium —— 跳过。" -ForegroundColor DarkGray
    Write-Host "    medium 模型请由下方 HuggingFace 下载，或参照脚本头部的 paddle2onnx 转换步骤。" -ForegroundColor DarkGray
} else {
    Write-Host "  rapidocr not found (only needed for small models). Install: pip install rapidocr" -ForegroundColor Yellow
}

# ── 方案2: HuggingFace 下载 (需代理) ──
if ($Proxy -ne "") {
    Write-Host "`n=== Trying HuggingFace via proxy ===" -ForegroundColor Cyan
    $proxyArg = @{ Proxy = $Proxy }

    # ── 已知可信产物的 SHA256（2026-09-14 由当前源码树内的模型计算并固化）──
    # 用途: 模型是从外网下载的可执行二进制（ONNX 会被 OnnxRuntime 直接加载），
    #       仅用"体积下限"校验挡不住「被替换成同体积恶意模型」或「下载被截断/损坏」。
    # 升级模型时: 先确认新来源可信 → 下载 → 用 -SkipHashCheck 放行 →
    #            立即把新 SHA256 填回这里，别让校验长期失效。
    $expectedSha256 = @{
        "PP-OCRv6_medium_det.onnx" = "f59aa6c475b5a42907fe282d10bc07031511bebcf8476794351a8c06af9958f5"
        "PP-OCRv6_medium_rec.onnx" = "63560d7b64bbdfc3c82cf3f329a6ab5df8f322eb9a7dd8039519abdb1eb4ac92"
    }

    $urls = @(
        @{ Name = "PP-OCRv6_medium_det.onnx"; Url = "https://huggingface.co/RapidAI/RapidOCR/resolve/main/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx"; MinMB = 10 },
        @{ Name = "PP-OCRv6_medium_rec.onnx"; Url = "https://huggingface.co/RapidAI/RapidOCR/resolve/main/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx"; MinMB = 30 }
    )

    if ($SkipHashCheck) {
        Write-Warning "  ⚠️ 已跳过 SHA256 校验 (-SkipHashCheck)。请确认来源可信，并在完成后更新脚本内的期望值。"
    }

    $downloadErrors = @()
    foreach ($m in $urls) {
        $dst = Join-Path $outDir $m.Name
        Write-Host "  Downloading: $($m.Name)..."
        try {
            $tmp = "$dst.tmp"
            Invoke-WebRequest -Uri $m.Url -OutFile $tmp -TimeoutSec 300 @proxyArg
            $sz = [math]::Round((Get-Item $tmp).Length / 1MB, 2)
            if ($sz -lt $m.MinMB) {
                Remove-Item $tmp -Force -ErrorAction SilentlyContinue
                throw "文件过小 ($sz MB < $($m.MinMB) MB)，疑似下载未完成或返回了错误页面。"
            }

            $actual = (Get-TtcFileHashHex -LiteralPath $tmp -Algorithm SHA256).ToLowerInvariant()
            $want   = $expectedSha256[$m.Name]
            if (-not $SkipHashCheck) {
                if ([string]::IsNullOrWhiteSpace($want)) {
                    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
                    throw "脚本内未登记 $($m.Name) 的期望 SHA256，拒绝接受未校验的二进制。"
                }
                if ($actual -ne $want) {
                    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
                    throw "SHA256 不匹配！期望 $want / 实际 $actual —— 已删除临时文件，未覆盖现有模型。"
                }
                Write-Host "    OK: $sz MB  SHA256 校验通过" -ForegroundColor Green
            } else {
                Write-Host "    OK: $sz MB  SHA256 = $actual (未校验)" -ForegroundColor Yellow
            }

            Move-Item $tmp $dst -Force
        } catch {
            Write-Host "    Failed: $($_.Exception.Message)" -ForegroundColor Red
            $downloadErrors += "$($m.Name): $($_.Exception.Message)"
        }
    }

    # 失败即阻断：任何一项下载/校验不过，就不算这次更新成功
    if ($downloadErrors.Count -gt 0) {
        throw "模型下载未全部成功 ($($downloadErrors.Count)/$($urls.Count) 项失败):`n  - " + ($downloadErrors -join "`n  - ")
    }
}

# ── 最终状态 ──
Write-Host "`n=== Model files ===" -ForegroundColor Cyan
Get-ChildItem "$outDir\*" -Include *.onnx,*.txt | ForEach-Object {
    Write-Host ("  {0,-35} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
