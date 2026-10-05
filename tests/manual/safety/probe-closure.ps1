# 诊断 [1.9/2] 新加的"按宿主算输入闭包"到底算出了什么（只读，不改产品码）。
# ⚠ 函数体内不许夹 Write-Output：PS 会把中途输出和返回值拼成数组 ⇒ 判据拿到的是"日志+集合"，
#   这不是假设，本仓已经栽过一次（记忆点：PS 函数体内 Write-Output 会被并进返回值）。
$ErrorActionPreference = 'Continue'
$RepoRoot = 'C:\PLAN\TrueToneCap'
$srcRoot  = Join-Path $RepoRoot 'src'
$Configuration = 'Release'

$inputInclude = @('*.cs', '*.xaml', '*.hlsl', '*.csproj', '*.props',
                  '*.manifest', 'Directory.Build.props', 'Directory.Packages.props')
$inputs = @(
    Get-ChildItem $srcRoot -Include $inputInclude -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
    Get-ChildItem (Join-Path $srcRoot 'TrueToneCap.Core\Resources') -Include '*.exe' -Recurse -File -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $RepoRoot 'tests\fixtures') -Include '*.icc', '*.icm', '*.png' -Recurse -File -ErrorAction SilentlyContinue
)
Write-Output ("inputs 总数 = {0}" -f $inputs.Count)
$sep = [IO.Path]::DirectorySeparatorChar
$inSrc = @($inputs | Where-Object { $_.FullName.StartsWith($srcRoot + $sep) })
Write-Output ("  src 下 {0} 份 / 非 src {1} 份" -f $inSrc.Count, ($inputs.Count - $inSrc.Count))
$inSrc | ForEach-Object { (Split-Path $_.FullName.Substring($srcRoot.Length + 1))[0] } |
    Group-Object | Sort-Object Name | ForEach-Object { Write-Output ("  {0,-28} {1}" -f $_.Name, $_.Count) }

function Get-HostInputClosure([string]$exePath) {
    $projDir = ($exePath -split '\\bin\\')[0]
    $csproj = @(Get-ChildItem -LiteralPath $projDir -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
    if ($csproj.Count -ne 1) { return $null }
    $leaf = Split-Path -Leaf $csproj[0].FullName
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    $trace = New-Object 'System.Collections.Generic.List[string]'
    $queue = New-Object 'System.Collections.Generic.Queue[string]'
    $queue.Enqueue($projDir)
    while ($queue.Count -gt 0) {
        $d = ($queue.Dequeue()).TrimEnd('\')
        if (-not $set.Add($d)) { continue }
        $dirProj = Join-Path $d $leaf
        if (-not (Test-Path -LiteralPath $dirProj)) { $trace.Add("csproj 不在: $dirProj"); continue }
        $txt = @(Get-Content -LiteralPath $dirProj -Raw -ErrorAction SilentlyContinue) -join "`n"
        $ms = [regex]::Matches($txt, '(?is)<ProjectReference[^>]*Include="([^"]+)"')
        $trace.Add(("{0} → ProjectReference 命中 {1}" -f $dirProj, $ms.Count))
        foreach ($m in $ms) {
            $rel = $m.Groups[1].Value.Split(';')[0].Trim().Replace('\', $sep)
            try { $abs = [IO.Path]::GetFullPath([IO.Path]::Combine($d, $rel)); $queue.Enqueue((Split-Path -Parent $abs)) }
            catch { $trace.Add("解析失败: $rel —— $($_.Exception.Message)") }
        }
    }
    $script:LastTrace = $trace
    return , $set
}

foreach ($name in @('TrueToneCap.Test', 'TrueToneCap.Tools')) {
    $exe = @(Get-ChildItem (Join-Path $srcRoot "$name\bin") -Filter "$name.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -split '\\' -contains $Configuration } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    if ($exe.Count -eq 0) { Write-Output ("{0}: 没有宿主" -f $name); continue }
    Write-Output ("=== {0} 宿主 {1}" -f $name, $exe[0].FullName)
    $script:LastTrace = $null
    $c = Get-HostInputClosure $exe[0].FullName
    Write-Output ("  返回值类型 = {0}，Count = {1}" -f $c.GetType().Name, @($c).Count)
    $script:LastTrace | ForEach-Object { Write-Output ("    trace: {0}") -f $_ }
    if ($c -isnot [System.Collections.Generic.HashSet[string]]) { Write-Output '  ⇒ 不是 HashSet，闭包坏了'; continue }
    $pref = @($c) | ForEach-Object { $_ + $sep }
    Write-Output ('  闭包: ' + (($pref | Sort-Object) -join ' ; '))
    $hIn = @($inputs | Where-Object {
        $f = $_.FullName
        (-not $f.StartsWith($srcRoot + $sep)) -or (@($pref | Where-Object { $f.StartsWith($_) }).Count -gt 0)
    })
    Write-Output ("  该宿主输入数 = {0}" -f $hIn.Count)
    $newest = $hIn | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $files = @(Get-ChildItem $exe[0].DirectoryName -File -ErrorAction SilentlyContinue)
    $stamp = ($files | Measure-Object -Property LastWriteTime -Maximum).Maximum
    Write-Output ("  最新输入 = {0} ({1:HH:mm:ss})  宿主目录 = {2:HH:mm:ss}  ⇒ {3}" -f `
        $newest.FullName, $newest.LastWriteTime, $stamp,
        $(if ($stamp -lt $newest.LastWriteTime) { 'STALE（应红）' } else { 'FRESH（绿）' }))
}
