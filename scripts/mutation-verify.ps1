# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 变异测试脚本：验证本会话新增的关键判据是否真的"有牙"
# 用法：每个变异独立执行 -> 期望单元测试报红 -> 立即还原
# 判据无牙（变异后仍全绿）比没有判据更危险：它让人误以为有保护。

$ErrorActionPreference = 'Stop'
$repo = "C:\PLAN\TrueToneCap"
Set-Location $repo

$targets = @(
    @{ Name = "StartupManager: 禁反射";    File = "src\TrueToneCap.App\Services\StartupManager.cs";
       From = 'private static bool Packaged'; To = 'private static object? X() => null; // MakeGenericMethod' },
    @{ Name = "StartupManager: TaskId";    File = "src\TrueToneCap.App\Services\StartupManager.cs";
       From = 'StartupTaskId = "TrueToneCapStartup"'; To = 'StartupTaskId = "Wrong"' },
    @{ Name = "Preview: cancel rethrow";   File = "src\TrueToneCap.App\OcrPreviewWindow.xaml.cs";
       From = 'catch (OperationCanceledException) { throw; }'; To = 'catch (OperationCanceledException) { }' },
    @{ Name = "Preview: snapshot";         File = "src\TrueToneCap.App\OcrPreviewWindow.xaml.cs";
       From = 'record TranslationSnapshot'; To = 'record TranslationSnapshotRenamed' },
    @{ Name = "Settings: packaged branch"; File = "src\TrueToneCap.App\Services\SettingsService.cs";
       From = 'PackageRuntime.IsPackaged'; To = 'false' },
    @{ Name = "LogService: packaged branch"; File = "src\TrueToneCap.App\Services\LogService.cs";
       From = 'PackageRuntime.IsPackaged'; To = 'false' }
)

$results = @()
foreach ($t in $targets) {
    $path = Join-Path $repo $t.File
    $bak = "$env:TEMP\mutbak.bak"
    Copy-Item $path $bak -Force
    try {
        $orig = Get-Content $path -Raw
        if (-not $orig.Contains($t.From)) {
            $results += [PSCustomObject]@{ Guard=$t.Name; Result="SKIP (pattern not found)"; Ok=$false }
            continue
        }
        $mut = $orig.Replace($t.From, $t.To)
        Set-Content $path -Value $mut -NoNewline

        dotnet build src\TrueToneCap.Test\TrueToneCap.Test.csproj -c Release -p:Platform=x64 *> $null
        dotnet run --project src\TrueToneCap.Test -c Release -- --unit-tests *> "$env:TEMP\mutout.txt"
        $exit = $LASTEXITCODE
        $failLine = (Get-Content "$env:TEMP\mutout.txt" | Select-String -Pattern "结果:" | Select-Object -First 1)
        $results += [PSCustomObject]@{
            Guard  = $t.Name
            Result = $(if ($exit -ne 0) { "RED as expected" } else { "GREEN - NO TEETH!" })
            Ok     = ($exit -ne 0)
        }
    }
    finally {
        Copy-Item $bak $path -Force
    }
}

Write-Output ""
Write-Output "================ MUTATION RESULTS ================"
$results | Format-Table -AutoSize
$noTeeth = $results | Where-Object { -not $_.Ok }
Write-Output "guards without teeth: $($noTeeth.Count)"
if ($noTeeth) { $noTeeth | ForEach-Object { "  ⚠ $($_.Guard)" } }
