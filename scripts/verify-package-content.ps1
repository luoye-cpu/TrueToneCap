# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# scripts/verify-package-content.ps1
# 「这一轮的代码真的进包了吗」—— 拿发布目录里的 DLL / .xbf 直接查证，不信"我跑过构建了"
#
# 用法: powershell -File scripts/verify-package-content.ps1 <包目录>
#   例: ... scripts\verify-package-content.ps1 publish\TrueToneCap-v0.3.5-beta-s53
#
# 为什么需要它（2026-09-26，交接 §53.7）：`Publish.ps1` exit 0 + 冒烟通过，只证明"那次构建自己没坏"，
# 不证明构建用的是【本轮改过的】源码 —— 中间任何一步跳编译（增量构建因源 mtime 假旧而跳过、
# 复制了上一轮的输出目录）都会产出一枚"全绿但内容是旧的"包，而它是能正常启动的。
# 三道各自的位置：门禁量源码行为，`verify-package.ps1` 量包的结构与依赖，本脚本量**包里的内容
# 是不是这一轮的**。
#
# 检索口径（踩过的坑都在这儿）：
#   · 类型与方法名在元数据 #Strings 堆里是 UTF-8 ⇒ ASCII 扫；
#   · C# 里的中文字面量在 #US 堆里是 UTF-16LE，且**可能落在奇数字节**上 ⇒ 两种对齐都试，
#     所以 `grep -a` 直接在 DLL 上找中文必然零命中（那不等于"串不在包里"）；
#   · XAML 的文本压根不在 DLL 里，在 `.xbf` ⇒ 对照串必须与被检串同容器，否则"查不到"没有意义；
#   · 每组正样本都配一条"本轮之前就有、现在仍在"的对照组：对照组查不到 ⇒ 是检索方法坏了，
#     不是代码没进包（这种假红本轮真的遇见过一次）。
param([Parameter(Mandatory=$true)][string]$Pkg)

$ErrorActionPreference = 'Stop'
$fail = 0

function Read-Bytes([string]$p) { [System.IO.File]::ReadAllBytes($p) }
function Find-Ascii([string]$p, [string]$s) {
    $b = Read-Bytes $p
    $pat = [System.Text.Encoding]::UTF8.GetBytes($s)
    for ($i = 0; $i -le $b.Length - $pat.Length; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $pat.Length; $j++) { if ($b[$i+$j] -ne $pat[$j]) { $ok = $false; break } }
        if ($ok) { return $i }
    }
    return -1
}
function Find-Unicode([string]$p, [string]$s) {
    $b = Read-Bytes $p
    $pat = [System.Text.Encoding]::Unicode.GetBytes($s)   # UTF-16LE，奇偶两种对齐都试
    foreach ($off in 0,1) {
        for ($i = $off; $i -le $b.Length - $pat.Length; $i += 2) {
            $ok = $true
            for ($j = 0; $j -lt $pat.Length; $j++) { if ($b[$i+$j] -ne $pat[$j]) { $ok = $false; break } }
            if ($ok) { return 'yes' }
        }
    }
    return ''
}
function Check([string]$name, [string]$file, [string]$needle, [string]$kind, [bool]$want) {
    if (-not (Test-Path $file)) { Write-Host "  MISSING FILE $file"; $script:fail++; return }
    $hit = if ($kind -eq 'ascii') { (Find-Ascii $file $needle) -ge 0 } else { (Find-Unicode $file $needle) -ne '' }
    if ($hit -eq $want) { Write-Host ("  OK   {0}  {1}  '{2}'" -f $(if($hit){'有'}else{'无'}), $name, $needle) }
    else { Write-Host ("  ❌   {0}  期望{1} 实得{2}  '{3}'" -f $name, $(if($want){'有'}else{'无'}), $(if($hit){'有'}else{'无'}), $needle); $script:fail++ }
}

$core = Join-Path $Pkg 'TrueToneCap.Core.dll'
$app  = Join-Path $Pkg 'TrueToneCap.dll'
$xbf  = Join-Path $Pkg 'MainWindow.xbf'

Write-Host "包: $Pkg"
Write-Host "`n[1] Core：新类型与其成员（元数据 #Strings 是 UTF-8）"
Check 'FormatColorFacts 类型' $core 'FormatColorFacts' 'ascii' $true
Check 'HdrDelivery 枚举'      $core 'HdrDelivery'      'ascii' $true
Check 'BlockedBySrgbTarget 成员' $core 'BlockedBySrgbTarget' 'ascii' $true
Check 'Declaration 方法'      $core 'Declaration'      'ascii' $true

Write-Host "`n[2] App：卡片新文案（中文字面量在 #US 堆里是 UTF-16LE）"
Check '卡片：sRGB 容器装不下 PQ' $app 'sRGB 容器装不下 PQ' 'unicode' $true
Check '卡片：此格式没有 HDR 通道'  $app '此格式没有 HDR 通道' 'unicode' $true
Check '卡片：底图色域'            $app '底图色域'           'unicode' $true
Check '管线：判定与落点分叉之外的新日志' $app '编码设置'      'unicode' $true

Write-Host "`n[3] 对照组（本轮之前就有、现在仍在的串）——它若在包里查不到，说明检索方法无效，[2] 全绿也不能信"
Check '旧字面量：HDR 开关已禁用' $app 'HDR 开关已禁用' 'unicode' $true
Check '旧类型名：ColorDecision'  $core 'ColorDecision' 'ascii' $true

Write-Host "`n[4] 负样本（本轮删掉的死分支文案，不许还在包里）"
Check '死文案：非标准 HDR10 容器色域' $app '非标准 HDR10 容器色域' 'unicode' $false
Check '死文案：scRGB→AdobeRGB 矩阵'  $app 'scRGB→AdobeRGB 矩阵'   'unicode' $false
Check '旧 ICC 说法：sRGB 目标不嵌入 ICC（通用默认）' $app 'sRGB 目标不嵌入 ICC（通用默认）' 'unicode' $false

Write-Host "`n[5] XAML 文本在 .xbf 里（不在 DLL 里）⇒ 同容器对照"
Check 'xbf 对照组：色彩与 HDR' $xbf '色彩与 HDR' 'unicode' $true
Check 'xbf 新句：色域映射按当前格式说' $xbf '色域映射」按当前格式说' 'unicode' $true
Check 'xbf 旧句：非 sRGB 色域自动嵌入标准 ICC' $xbf '非 sRGB 色域自动嵌入标准 ICC' 'unicode' $false

Write-Host "`n[6] 时间戳与新鲜度"
# 只比【被 git 跟踪的源码】：dotnet 会在 obj/ 下生成 App.xaml 之类的中间文件并被构建自己 touch，
# 用 Get-ChildItem -Recurse 扫 *.xaml 会把它们当成"最新源码"⇒ 每次发布都必然报"产物比源码旧"（假红）。
$tracked = & git -C (Split-Path -Parent (Split-Path -Parent $Pkg)) ls-files 'src/*.cs' 'src/*.xaml' 2>$null
if (-not $tracked) { Write-Host '  ❌ 拿不到 git 跟踪清单 ⇒ 新鲜度这一层没判'; $fail++ }
else {
  $src = $tracked | ForEach-Object { Get-Item (Join-Path (Split-Path -Parent (Split-Path -Parent $Pkg)) $_) } |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
  Write-Host ("  最新跟踪源码: {0}  {1}" -f $src.FullName.Replace((Split-Path -Parent (Split-Path -Parent $Pkg)),''), $src.LastWriteTime.ToString('MM-dd HH:mm:ss'))
  foreach ($f in @($core,$app,$xbf)) {
    Write-Host ("  {0,-22} {1}" -f (Split-Path $f -Leaf), (Get-Item $f).LastWriteTime.ToString('MM-dd HH:mm:ss'))
    if ((Get-Item $f).LastWriteTime -lt $src.LastWriteTime) { Write-Host "  ❌ 比源码旧 ⇒ 本轮结论作废"; $fail++ }
  }
}
Write-Host '  ℹ 时间戳只是第二道；第一道是上面的内容核对（新串在、旧串不在、同容器有对照组）。'

Write-Host ''
if ($fail -eq 0) { Write-Host 'RESULT content=PASS'; exit 0 } else { Write-Host "RESULT content=FAIL ($fail)"; exit 1 }
