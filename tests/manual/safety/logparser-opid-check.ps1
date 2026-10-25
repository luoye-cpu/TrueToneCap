# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 独立复现 L6b-L7 那条断言：LogViewer 的行解析器认不认带 #opId 前缀的日志行。
# 判据形状 = 一正一负两条臂（负断言必须先被已知存在的串命中一次，否则分不清"没有"与"我没看到"）：
#   对照臂：真机 log 里【不带】opId 的行 → 必须命中（证明这条正则与本机日志格式本来是对得上的）
#   被试臂：真机 log 里【带】opId 的行  → 若 0 命中即确证"截图期间的日志行在查看器里消失"
# 再从产品自己的 FormatLine 造一行，排除"真机日志是旧格式"这一种解释。
$ErrorActionPreference = 'Continue'
$repo = 'C:\PLAN\TrueToneCap'

# 这条模式逐字抄自 src\TrueToneCap.LogViewer\Services\LogParser.cs:104
$pat = '^(\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w+)\s*\]\s+\[([^/\]]+)/([^\]]+)\]\s+(.*?)\s*$'

$logDir = Join-Path $repo 'src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\log'
$file = Get-ChildItem -LiteralPath $logDir -File -Filter 'app_*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $file) { Write-Output 'NO_LOG_FILE'; exit 2 }

# 产品写行的真实前缀（LogService.cs:36）：时间 [级别] #opId [分类/Tag] 消息 文件:行
$opLines    = @()
$plainLines = @()
foreach ($l in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
    if ($l -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s+\[\w+\s*\]\s+#') { if ($opLines.Count -lt 3) { $opLines += $l } }
    elseif ($l -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s+\[\w+\s*\]\s+\[') { if ($plainLines.Count -lt 3) { $plainLines += $l } }
}
$opTotal = (Get-Content -LiteralPath $file.FullName -Encoding UTF8 |
    Where-Object { $_ -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s+\[\w+\s*\]\s+#' }).Count

function Get-Verdict([string]$line, [string]$pattern) {
    if ($line -match $pattern) { 'HIT ' } else { 'MISS' }
}

Write-Output "日志文件: $($file.Name)  带 opId 的行总数: $opTotal"
Write-Output ''
Write-Output '=== 对照臂（不带 opId，应当命中） ==='
foreach ($l in $plainLines) { Write-Output ("  {0}  {1}" -f (Get-Verdict $l $pat), $l) }
Write-Output ''
Write-Output '=== 被试臂（带 opId，L6b-L7 断言它 0 命中） ==='
foreach ($l in $opLines) { Write-Output ("  {0}  {1}" -f (Get-Verdict $l $pat), $l) }
Write-Output ''
# 第三条臂：排除"真机日志是旧格式"这一解释——用 LogParser.cs:101 注释里的示例格式造一行
$sample = '03:17:36.052 [Info   ] [捕获/WgcCapture] 会话启动: 3840x2160 SDR 126ms WgcCaptureService.cs:52'
$withOp = '03:17:36.052 [Info   ] #a1b2c3 [捕获/WgcCapture] 会话启动: 3840x2160 SDR 126ms WgcCaptureService.cs:52'
Write-Output '=== 夹具臂（同一条行，只加 "#a1b2c3 " 前缀） ==='
Write-Output ("  无前缀: {0}" -f (Get-Verdict $sample $pat))
Write-Output ("  加前缀: {0}" -f (Get-Verdict $withOp $pat))
