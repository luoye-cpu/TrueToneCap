# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Name U -Namespace W -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'

function Sample([string]$tag) {
    $p = Get-Process -Name TrueToneCap -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Host "$tag : 进程不在"; return }
    $ws = [math]::Round($p.WorkingSet64 / 1MB)
    $priv = [math]::Round($p.PrivateMemorySize64 / 1MB)
    $heaps = "?"
    try {
        $c = Get-Counter '\.NET CLR Memory(TrueToneCap*)\# Bytes in all Heaps' -ErrorAction Stop -SampleInterval 0 -MaxSamples 1
        $heaps = [math]::Round(($c.CounterSamples[0].CookedValue) / 1MB)
    } catch { }
    $gen = "?"
    try {
        $c2 = Get-Counter '\.NET CLR Memory(TrueToneCap*)\Large Object Heap size' -ErrorAction Stop -SampleInterval 0 -MaxSamples 1
        $gen = [math]::Round(($c2.CounterSamples[0].CookedValue) / 1MB)
    } catch { }
    Write-Host ("{0,-14} WorkingSet={1,6} MB  Private={2,6} MB  托管堆={3,6} MB  LOH={4,6} MB" -f $tag, $ws, $priv, $heaps, $gen)
}

$p = Get-Process -Name TrueToneCap -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Host "TrueToneCap 未运行"; exit 1 }
[void][W.U]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 600

Sample "起点"
for ($i = 1; $i -le 5; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('^+q')
    Start-Sleep -Seconds 9
    Sample "第 $i 张后"
}
Write-Host "— 等待 30s 让 GC 回收 —"
Start-Sleep -Seconds 30
Sample "静置后"
Sample "静置+10s"
