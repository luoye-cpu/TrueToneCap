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
    & $CMake -E env "PATH=$env:PATH" "$build\vscap-bridge-tests.exe"
    if ($LASTEXITCODE) { throw 'VS-Cap bridge tests failed' }
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
