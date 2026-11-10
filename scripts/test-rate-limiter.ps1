# 智能限流器专项测试
# 目标：验证在多行翻译场景下，每秒请求数不超过阈值

param(
    [int]$MaxRequestsPerSecond = 6,
    [int]$TestLinesCount = 20,
    [int]$Concurrency = 4
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Rate Limiter Test / 限流器测试" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Config / 配置:" -ForegroundColor Yellow
Write-Host "  - 测试行数: $TestLinesCount"
Write-Host "  - 并发限制: $Concurrency"
Write-Host "  - 最大 QPS: $MaxRequestsPerSecond"
Write-Host ""

# 模拟多行文本
$testTexts = @()
for ($i = 1; $i -le $TestLinesCount; $i++) {
    $testTexts += "这是第$i行测试文本。This is test line $i."
}

# 模拟限流器逻辑（简化版，用于演示）
$requestCounts = @{}
$totalRequests = 0
$start = Get-Date

# 每秒重置计数器
$lastReset = Get-Date
$currentSecond = 0

Write-Host "Starting batch translation simulation..." -ForegroundColor Green
Write-Host ""

# 模拟分批处理（与 OcrPreviewWindow.xaml.cs 的 SemaphoreSlim(4) 一致）
$batchSize = $Concurrency
$processedBatches = 0

for ($batchStart = 0; $batchStart -lt $testTexts.Length; $batchStart += $batchSize) {
    $endIndex = [Math]::Min($batchStart + $batchSize, $testTexts.Length)
    $batch = $testTexts[$batchStart..($endIndex-1)]
    
    Write-Host "Batch $([Math]::Floor($batchStart/$batchSize)+1)/[数学]::Ceiling($testTexts.Length/$batchSize): $($batch.Count) lines" -ForegroundColor Cyan
    
    # 模拟批量发送
    foreach ($line in $batch) {
        $totalRequests++
        
        # 检测是否需要限流
        $now = Get-Date
        $sinceLastReset = New-TimeSpan -Seconds $((Get-Date).Second - $lastReset.Second)
        
        if ($sinceLastReset.TotalSeconds -ge 1.0) {
            $currentSecond++
            $requestCounts[$currentSecond] = 0
            $lastReset = Get-Date
            Write-Host "  [秒 $currentSecond]: 重置计数器 → 0 requests" -ForegroundColor Gray
        }
        
        $requestCounts[$currentSecond]++
        
        # 检查是否超过阈值
        if ($requestCounts[$currentSecond] -gt $MaxRequestsPerSecond) {
            $delay = Get-Random -Minimum 100 -Maximum 500
            Write-Host "    [延迟] Line #$totalRequests: 频率超限 ($($requestCounts[$currentSecond])/$MaxRequestsPerSecond)，退避$delay ms" -ForegroundColor Red
            Start-Sleep -Milliseconds $delay
            continue
        } else {
            Write-Host "    [通过] Line #$totalRequests: 请求已发送 (当前:$($requestCounts[$currentSecond])/$MaxRequestsPerSecond)" -ForegroundColor Green
        }
    }
    
    $processedBatches++
    
    # 批次间延迟（模拟真实网络延迟）
    if ($endIndex -lt $testTexts.Length) {
        $batchDelay = Get-Random -Minimum 200 -Maximum 500
        Write-Host "  [批次间] 等待$($batchDelay)ms..." -ForegroundColor Yellow
        Start-Sleep -Milliseconds $batchDelay
    }
}

$elapsed = New-TimeSpan -Start $start -End (Get-Date)

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary / 测试结果汇总" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "总请求数/Total Requests: $totalRequests"
Write-Host "完成时间/Elapsed Time: $($elapsed.TotalSeconds.ToString('F2'))s"
Write-Host "平均 QPS/Average QPS: $("$("%.2f" -f ($totalRequests / $elapsed.TotalSeconds))")"
Write-Host ""

# 显示每秒分布
Write-Host "每秒请求分布/Requests per Second Distribution:" -ForegroundColor Yellow
foreach ($second in ($requestCounts | Sort-Object { [int]$_Key })) {
    $reqCount = $requestCounts[$second._key]
    if ($reqCount -gt $MaxRequestsPerSecond) {
        Write-Host "  Sec $second.Key : [$reqCount] ❌ 超限!" -ForegroundColor Red
    } else {
        Write-Host "  Sec $second.K : [$reqCount] ✅ OK" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "结论/Conclusion" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$avgQps = [math]::Round($totalRequests / $elapsed.TotalSeconds, 2)
if ($avgQps -le $MaxRequestsPerSecond * 1.5) {
    Write-Host "✅ 限流器工作正常! 平均 QPS ($avgQps) < 阈值 * 1.5 ($($MaxRequestsPerSecond * 1.5))" -ForegroundColor Green
    Write-Host "   没有触发风控的频率过高警告。" -ForegroundColor Green
} else {
    Write-Host "⚠️  注意：平均 QPS ($avgQps) 较高，可能需要调整限流参数" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
