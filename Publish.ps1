# TrueToneCap Publish Script
# 完整发布流程：编译 → 收集资源 → 打包
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish\TrueToneCap-v0.3.0-beta",
    [switch]$CoreAot = $false,
    [switch]$NoReadyToRun = $false
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "╔══════════════════════════════════════╗"
Write-Host "║  TrueToneCap v0.3.0 Beta 发布脚本   ║"
Write-Host "║  ReadyToRun: $(-not $NoReadyToRun)                     ║"
Write-Host "║  Core AOT: $CoreAot                           ║"
Write-Host "╚══════════════════════════════════════╝"
Write-Host ""

# ── 0. 验证 PLAN 依赖仓库完整性 ──
Write-Host "[0/5] 验证 PLAN 依赖仓库..."
$planDir = Join-Path $RepoRoot "publish\PLAN"
$planTools = Join-Path $planDir "tools"
$planModels = Join-Path $planDir "models"

$toolFiles = @{ "avifenc.exe" = "AVIF 编码器"; "cjpegli.exe" = "JPEG LI 编码器"; "cwebp.exe" = "WebP 编码器"; "cjxl.exe" = "JPEG XL 编码器" }
$modelFiles = @{ "PP-OCRv6_medium_det.onnx" = "ONNX 文字检测"; "PP-OCRv6_medium_rec.onnx" = "ONNX 文字识别"; "ppocrv6_dict.txt" = "中英文字典" }

$allOk = $true

# 检查 tools/
if (Test-Path $planTools) {
    foreach ($entry in $toolFiles.GetEnumerator()) {
        if (Test-Path (Join-Path $planTools $entry.Key)) {
            $size = [math]::Round((Get-Item (Join-Path $planTools $entry.Key)).Length / 1KB, 1)
            Write-Host "   ✅ $($entry.Key)  ($($entry.Value), $size KB)"
        } else {
            Write-Warning "   ⚠️ 缺少: $($entry.Key) ($($entry.Value))"
            $allOk = $false
        }
    }
} else {
    Write-Warning "   ⚠️ tools/ 目录不存在"
    $allOk = $false
}

# 检查 models/
if (Test-Path $planModels) {
    foreach ($entry in $modelFiles.GetEnumerator()) {
        if (Test-Path (Join-Path $planModels $entry.Key)) {
            $size = [math]::Round((Get-Item (Join-Path $planModels $entry.Key)).Length / 1MB, 1)
            Write-Host "   ✅ $($entry.Key)  ($($entry.Value), $size MB)"
        } else {
            Write-Warning "   ⚠️ 缺少: $($entry.Key) ($($entry.Value))"
            $allOk = $false
        }
    }
} else {
    Write-Warning "   ⚠️ models/ 目录不存在"
    $allOk = $false
}

if (-not $allOk) {
    Write-Warning "   ⚠️ 部分依赖缺失，请检查 publish/PLAN/ 目录"
} else {
    Write-Host "   ✅ PLAN 依赖仓库完整"
}
Write-Host ""

# ── 构建额外参数 ──
$extraArgs = @()

# ReadyToRun（默认开启，-NoReadyToRun 禁用）
if (-not $NoReadyToRun) {
    $extraArgs += "-p:PublishReadyToRun=true"
    $extraArgs += "-p:PublishReadyToRunComposite=true"
    Write-Host "   ⚡ ReadyToRun 已启用 (复合映像)"
} else {
    Write-Host "   🐢 ReadyToRun 已禁用 (纯 JIT)"
}

# Core AOT 分离编译（默认关闭，-CoreAot 启用）
# 以 NativeAOT 编译 TrueToneCap.Core，App 层通过 JIT 桥接调用
# 注意：Core 的 Vortice/ONNX Runtime 依赖需 AOT 兼容
if ($CoreAot) {
    $extraArgs += "-p:CorePublishAot=true"
    Write-Host "   🔥 Core AOT 已启用 (试验性)"
}

# ── 1. Clean & Build ──
Write-Host "[1/5] 编译 Release..."
Push-Location $RepoRoot
$oldEAP = $ErrorActionPreference; $ErrorActionPreference = "Continue"
taskkill /F /IM TrueToneCap.exe 2>$null
$ErrorActionPreference = $oldEAP
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue

dotnet publish src\TrueToneCap.App\TrueToneCap.App.csproj `
    -c $Configuration -r $Runtime `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    $extraArgs `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "编译失败 (Build failed)" }

$buildSize = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "   📦 编译完成: $([math]::Round($buildSize,1)) MB"

# ── 2. 复制 PRI 资源 (编译后的 XAML 资源索引) ──
Write-Host "[2/5] 复制 PRI 资源..."
# RID 布局：WinUI 输出在 bin\x64\Release\net11.0-windows10.0.26100.0\win-x64
# 旧路径 bin\Release\... 在清理缓存后失效，需自适应查找
$BinDir = $null
$candidates = @(
    "src\TrueToneCap.App\bin\x64\$Configuration\net11.0-windows10.0.26100.0\$Runtime",
    "src\TrueToneCap.App\bin\$Configuration\net11.0-windows10.0.26100.0\$Runtime",
    "src\TrueToneCap.App\bin\x64\$Configuration\net11.0-windows10.0.26100.0"
)
foreach ($c in $candidates) { if (Test-Path $c) { $BinDir = $c; break } }
Write-Host "   📁 资源目录: $BinDir"

$priFile = $null
if ($BinDir) { $priFile = Get-ChildItem $BinDir -Filter "TrueToneCap.pri" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -notmatch '\\obj\\' } | Select-Object -First 1 }
if ($priFile) {
    Copy-Item $priFile.FullName $OutputDir -Force
    Write-Host "   ✅ TrueToneCap.pri ($([math]::Round($priFile.Length/1KB,1)) KB)"
} else {
    Write-Warning "   ⚠️ TrueToneCap.pri 未在 bin 找到，检查 obj/..."
    $objPri = Get-ChildItem "src\TrueToneCap.App\obj\$Configuration" -Recurse -Filter "TrueToneCap.pri" | Select-Object -First 1
    if ($objPri) {
        Copy-Item $objPri.FullName $OutputDir -Force
        Write-Host "   ✅ TrueToneCap.pri (来自 obj, $([math]::Round($objPri.Length/1KB,1)) KB)"
    } else {
        Write-Warning "   ⚠️ PRI 未找到（WinAppSDK 2.x 可能已将资源合并到 WindowsAppRuntime.pri）"
    }
}

# ── 3. 复制 XBF 文件 (编译后的 XAML 二进制) ──
Write-Host "[3/5] 复制 XBF 文件..."
$xbfFiles = $null
if ($BinDir) { $xbfFiles = Get-ChildItem $BinDir -Filter "*.xbf" -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -notmatch '\\obj\\' } }
if ($xbfFiles) {
    Copy-Item $xbfFiles.FullName $OutputDir -Force
    Write-Host "   ✅ $($xbfFiles.Count) 个 XBF 文件"
    $xbfFiles | ForEach-Object { Write-Host "      ├─ $($_.Name)" }
} else {
    Write-Warning "   ⚠️ 未找到 XBF 文件（可能在 obj/ 中，csproj 已处理）"
}

# ── 4. 验证 Windows App Runtime ──
Write-Host "[4/5] 验证 Windows App Runtime..."
$bootstrapDll = "$OutputDir\Microsoft.WindowsAppRuntime.Bootstrap.dll"
if (Test-Path $bootstrapDll) {
    $bootstrapSize = [math]::Round((Get-Item $bootstrapDll).Length / 1KB, 1)
    Write-Host "   ✅ Bootstrap.dll ($bootstrapSize KB)"
} else {
    Write-Warning "   ⚠️ Bootstrap.dll 未找到，运行时可能需要安装 Windows App SDK Runtime"
}

# 复制静态文件
Copy-Item README.md, LICENSE $OutputDir -Force
Write-Host "   ✅ README.md + LICENSE"

# ── 5. 从 PLAN/ 依赖仓库复制非核心组件 ──
Write-Host "[5/5] 复制 PLAN 依赖组件..."

# 5.1 原生工具 → native/ (预提取，避免首次运行等待)
if (Test-Path $planTools) {
    $nativeDir = Join-Path $OutputDir "native"
    New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
    $toolCount = 0
    foreach ($exe in @("avifenc.exe", "cjpegli.exe", "cwebp.exe", "cjxl.exe")) {
        $src = Join-Path $planTools $exe
        if (Test-Path $src) {
            Copy-Item $src $nativeDir -Force
            $size = [math]::Round((Get-Item $src).Length / 1KB, 1)
            Write-Host "   ✅ $exe ($size KB)"
            $toolCount++
        } else {
            Write-Warning "   ⚠️ $exe 在 PLAN/tools/ 中缺失"
        }
    }
    Write-Host "   📁 native/ — $toolCount 个文件"
}

# 5.2 OCR 模型 → data/Models/
if (Test-Path $planModels) {
    $dataModels = Join-Path $OutputDir "data\Models"
    New-Item -ItemType Directory -Path $dataModels -Force | Out-Null
    $modelCount = 0
    foreach ($model in @("PP-OCRv6_medium_det.onnx", "PP-OCRv6_medium_rec.onnx", "ppocrv6_dict.txt")) {
        $src = Join-Path $planModels $model
        if (Test-Path $src) {
            Copy-Item $src $dataModels -Force
            $size = [math]::Round((Get-Item $src).Length / 1MB, 1)
            Write-Host "   ✅ $model ($size MB)"
            $modelCount++
        } else {
            Write-Warning "   ⚠️ $model 在 PLAN/models/ 中缺失"
        }
    }
    Write-Host "   📁 data/Models/ — $modelCount 个文件 (OCR 模型)"
}

# ── 清理调试符号 ──
$pdbCount = (Get-ChildItem $OutputDir -Filter "*.pdb" -ErrorAction SilentlyContinue).Count
if ($pdbCount -gt 0) {
    Remove-Item "$OutputDir\*.pdb" -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "   🧹 已清理 $pdbCount 个 .pdb 调试符号 (移至 data/)"
}

# ── 统计 ──
$totalSize = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
$totalFiles = (Get-ChildItem $OutputDir -Recurse -File).Count
Pop-Location

Write-Host ""
Write-Host "╔══════════════════════════════════════╗"
Write-Host "║  发布完成!                          ║"
Write-Host "║  📦 $([math]::Round($totalSize,1)) MB  |  $totalFiles 文件  ║"
Write-Host "║  📂 $OutputDir ║"
Write-Host "╚══════════════════════════════════════╝"
Write-Host ""
Write-Host "📋 发布包内容概要:"
$nativeCount = (Get-ChildItem "$OutputDir\native" -Filter "*.exe" -ErrorAction SilentlyContinue).Count
$modelSize = (Get-ChildItem "$OutputDir\data\Models" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB
$shaderCount = (Get-ChildItem "$OutputDir\data\Shaders" -Filter "*.cso" -ErrorAction SilentlyContinue).Count
Write-Host "   ├─ 原生工具: $nativeCount 个 (native/)"
Write-Host "   ├─ OCR 模型: $([math]::Round($modelSize,1)) MB (data/Models/)"
Write-Host "   ├─ 着色器: $shaderCount 个 (data/Shaders/)"
Write-Host "   └─ 总文件: $totalFiles 个"
Write-Host ""
