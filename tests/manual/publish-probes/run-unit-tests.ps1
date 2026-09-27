$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\src\TrueToneCap.Test\bin\Release\net11.0-windows10.0.26100.0\TrueToneCap.Test.exe'
$exe = (Resolve-Path $exe).Path
$out = Join-Path $env:TEMP 'ttc-unit-out.txt'
$p = Start-Process -FilePath $exe -ArgumentList '--unit-tests' `
     -RedirectStandardOutput $out -NoNewWindow -Wait -PassThru
Write-Host "EXIT=$($p.ExitCode)"
Get-Content $out -Encoding Unicode | Select-Object -Last 30
