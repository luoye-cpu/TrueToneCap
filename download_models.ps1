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
    [string]$Proxy = ""
)

$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "src\TrueToneCap.App\Models"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ── 方案1: 从 rapidocr Python 包提取 (最可靠) ──
Write-Host "=== Checking rapidocr Python package ===" -ForegroundColor Cyan
$rapidocrModels = ""
try {
    $rapidocrModels = py -c "import rapidocr, os; print(os.path.join(os.path.dirname(rapidocr.__file__), 'models'))" 2>$null
} catch {}

if ($rapidocrModels -and (Test-Path $rapidocrModels)) {
    $detSrc = Join-Path $rapidocrModels "PP-OCRv6_det_small.onnx"
    $recSrc = Join-Path $rapidocrModels "PP-OCRv6_rec_small.onnx"

    if (Test-Path $detSrc) {
        $detDst = Join-Path $outDir "PP-OCRv6_det.onnx"
        if (-not (Test-Path $detDst) -or (Get-Item $detDst).Length -lt 5MB) {
            Copy-Item $detSrc $detDst -Force
            Write-Host "  Copied det: PP-OCRv6_det_small.onnx -> PP-OCRv6_det.onnx" -ForegroundColor Green
        } else {
            Write-Host "  det already exists ($([math]::Round((Get-Item $detDst).Length/1MB,2)) MB), skipping" -ForegroundColor DarkGray
        }
    }
    Write-Host "  Note: rapidocr only has 'small' models. For 'medium', use Paddle conversion (see header)." -ForegroundColor Yellow
} else {
    Write-Host "  rapidocr not found. Install: pip install rapidocr" -ForegroundColor Yellow
}

# ── 方案2: HuggingFace 下载 (需代理) ──
if ($Proxy -ne "") {
    Write-Host "`n=== Trying HuggingFace via proxy ===" -ForegroundColor Cyan
    $proxyArg = @{ Proxy = $Proxy }
    $urls = @(
        @{ Name = "PP-OCRv6_medium_det.onnx"; Url = "https://huggingface.co/RapidAI/RapidOCR/resolve/main/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx"; MinMB = 10 },
        @{ Name = "PP-OCRv6_medium_rec.onnx"; Url = "https://huggingface.co/RapidAI/RapidOCR/resolve/main/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx"; MinMB = 30 }
    )
    foreach ($m in $urls) {
        $dst = Join-Path $outDir $m.Name
        Write-Host "  Downloading: $($m.Name)..."
        try {
            $tmp = "$dst.tmp"
            Invoke-WebRequest -Uri $m.Url -OutFile $tmp -TimeoutSec 300 @proxyArg
            $sz = [math]::Round((Get-Item $tmp).Length / 1MB, 2)
            if ($sz -ge $m.MinMB) {
                Move-Item $tmp $dst -Force
                Write-Host "    OK: $sz MB" -ForegroundColor Green
            } else {
                Remove-Item $tmp -Force
                Write-Host "    Too small ($sz MB)" -ForegroundColor Red
            }
        } catch {
            Write-Host "    Failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# ── 最终状态 ──
Write-Host "`n=== Model files ===" -ForegroundColor Cyan
Get-ChildItem "$outDir\*" -Include *.onnx,*.txt | ForEach-Object {
    Write-Host ("  {0,-35} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
