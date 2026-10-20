# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
param(
    [Parameter(Mandatory = $true)][string]$QtRoot,
    [Parameter(Mandatory = $true)][string]$ToolchainBin,
    [string]$CMake = 'cmake',
    [string]$Ninja = 'ninja',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$build = Join-Path $repo 'build\vscap'
$runtime = Join-Path $repo 'build\vscap-runtime'
$env:PATH = "$ToolchainBin;$QtRoot\bin;$env:PATH"
& $CMake -S "$repo\src\TrueToneCap.VSCap" -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$Ninja" "-DCMAKE_PREFIX_PATH=$QtRoot" -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON
if ($LASTEXITCODE) { throw 'VS-Cap configure failed' }
& $CMake --build $build
if ($LASTEXITCODE) { throw 'VS-Cap build failed' }
if ($Test) {
    # ── 跨语言像素对撞要三步才闭合 ──
    # C# 写夹具 → Qt 读并回写 → C# 逐字节验回写。
    # 原实现直接跑 vscap-bridge-tests：既没生成夹具也没设 TTC_VSCAP_FIXTURES ⇒
    # 唯一那条 crossLanguagePixels 每次都 QSKIP，而 QtTest 的 SKIP 退出码仍是 0，
    # 于是"桥接测试通过"里其实一条跨语言判定都没有。
    # 夹具落在仓库内（刻意不用 %TEMP%）：这台机器的清理器会秒删 %TEMP% 里新建的文件，历史上证据真丢过。
    $fixtures = Join-Path $repo 'build\vscap-fixtures'
    if (Test-Path $fixtures) { Remove-Item -LiteralPath $fixtures -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $fixtures | Out-Null

    & dotnet run --project (Join-Path $repo 'tests\VSCapInterop') -c Release -- $fixtures
    if ($LASTEXITCODE) { throw 'C# 侧夹具生成 / 自身往返自检失败' }

    $env:TTC_VSCAP_FIXTURES = $fixtures
    try {
        $bridgeOut = & $CMake -E env "PATH=$env:PATH" "$build\vscap-bridge-tests.exe" 2>&1 | Out-String
        $bridgeRc = $LASTEXITCODE
    } finally {
        Remove-Item Env:\TTC_VSCAP_FIXTURES -ErrorAction SilentlyContinue
    }
    Write-Host $bridgeOut
    # SKIP 也是退出码 0 ⇒ 必须点名，不能只看 rc
    if ($bridgeOut -match '(?i)skip[^\n]*crossLanguagePixels') {
        throw 'crossLanguagePixels 被 SKIP ⇒ 跨语言判据一条都没判，不许当通过收场'
    }
    if ($bridgeRc) { throw "VS-Cap bridge tests failed (rc=$bridgeRc)" }

    & dotnet run --project (Join-Path $repo 'tests\VSCapInterop') -c Release -- --verify-qt $fixtures
    if ($LASTEXITCODE) { throw 'Qt 回写的像素与 C# 原始值不一致（通道序/alpha/浮点位级任一处错都算）' }

    # Export encoding is provided by TTC; exercise the unchanged editor and pin workflow.
    & "$build\vscap-workflow-tests.exe" desktopPixelsAndOverlay selectAnnotateAndCopy selectedToolsLineAndUndo editableTextObjects textAndDrawingUndo toolbarShapesAndTextFrames pinAspectZoomAndEditing pinScreenFitAndBorders hdrClipboardAndPin
    if ($LASTEXITCODE) { throw 'VS-Cap workflow tests failed' }
}
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath "$build\ttc-vscap.exe" -Destination $runtime
& "$QtRoot\bin\windeployqt.exe" --release --no-translations --no-opengl-sw --no-system-d3d-compiler --no-quick-import --dir $runtime "$runtime\ttc-vscap.exe"
if ($LASTEXITCODE) { throw 'VS-Cap runtime deployment failed' }
Copy-Item -LiteralPath "$repo\src\TrueToneCap.VSCap\upstream\LICENSE" -Destination "$runtime\VS-Cap-LICENSE.txt"
Copy-Item -LiteralPath "$repo\src\TrueToneCap.VSCap\upstream\Qt-LGPL-3.0.txt","$repo\src\TrueToneCap.VSCap\upstream\Qt-GPL-3.0.txt" -Destination $runtime
