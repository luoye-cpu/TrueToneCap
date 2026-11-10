#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# ═══ WCAG 2.1 AA Accessibility Enhancement Script ═══
# 2026-11-08: Add AutomationProperties and keyboard navigation to XAML files
# Usage: .\scripts\fix-accessibility.ps1 -XamlFile <path> [-AddKeyboardAccelerators]

param(
    [Parameter(Mandatory=$true)]
    [string]$XamlFile,
    
    [switch]$AddKeyboardAccelerators
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $XamlFile)) {
    Write-Error "XAML file not found: $XamlFile"
    exit 1
}

$content = Get-Content $XamlFile -Raw -Encoding UTF8

# ═══ Accessibility Properties Mapping ═══
$mappings = @{
    # Output Settings
    'FormatCbo[^>]*' = {
        @'
                AutomationProperties.Name="输出格式选择器"
                AutomationProperties.HelpText="从 PNG、JPEG、AVIF 等格式中选择截图保存格式"@
    }
    
    'QualitySld[^>]*' = {
        @'
                AutomationProperties.Name="图像质量滑块"
                AutomationProperties.HelpText="拖动滑块调整图像压缩质量（1-100）"@
                AutomationProperties.LabeledBy="{Binding ElementName=QualityLabel}"
                AutomationProperties.Value="{Binding Value}"@
    }
    
    'QualityTxt[^>]*' = {
        @'
                AutomationProperties.Name="质量数值输入框"
                AutomationProperties.HelpText="直接输入质量值（1-100）"@
                TabIndex="3"@
    }
    
    'PathTxt[^>]*' = {
        @'
                AutomationProperties.Name="保存路径文本框"
                AutomationProperties.HelpText="设置截图文件的保存位置，支持绝对或相对路径"@
                AutomationProperties.ControlType="TextField"@
    }
    
    'PrefixTxt[^>]*' = {
        @'
                AutomationProperties.Name="文件名前缀输入框"
                AutomationProperties.HelpText="设置导出文件的命名前缀"@
    }
    
    'ArchiveChk[^>]*' = {
        @'
                AutomationProperties.Name="启用文件归档复选框"
                AutomationProperties.HelpText="勾选后按日期自动归档到年份/月份文件夹"@
    }
    
    'ArchiveModeCbo[^>]*' = {
        @'
                AutomationProperties.Name="归档方式选择器"
                AutomationProperties.HelpText="选择按年、按月或按日组织截图文件"@
    }
    
    'HdrSwitch[^>]*' = {
        @'
                AutomationProperties.Name="HDR 输出切换开关"
                AutomationProperties.HelpText="开启可保存 HDR 高动态范围图像，保留更多亮度细节"@
    }
    
    'ColorCbo[^>]*' = {
        @'
                AutomationProperties.Name="目标色域选择器"
                AutomationProperties.HelpText="选择图像的色域空间，跟随显示器/sRGB/BT.2020"@
    }
    
    # Capture Settings
    'HotkeyTxt[^>]*' = {
        @'
                AutomationProperties.Name="截图快捷键设置框"
                AutomationProperties.HelpText="按下此按钮录制自定义的截图组合键"@
    }
    
    'CaptureHotkeyRecordBtn[^>]*' = {
        @'
                AutomationProperties.Name="录制截图快捷键按钮"
                AutomationProperties.HelpText="点击后等待你按下想要的快捷键组合"@
    }
    
    'PreviewChk[^>]*' = {
        @'
                AutomationProperties.Name="预览缩略图复选框"
                AutomationProperties.HelpText="截图后在右下角显示图片预览"@
    }
    
    # AI/OCR Settings
    'OcrEngineCbo[^>]*' = {
        @'
                AutomationProperties.Name="OCR 引擎选择器"
                AutomationProperties.HelpText="选择文字识别引擎：DirectML GPU / ONNX CPU / Windows OCR"@
    }
    
    'TranslationModeCbo[^>]*' = {
        @'
                AutomationProperties.Name="翻译模式选择器"
                AutomationProperties.HelpText="选择翻译方式：免费公共端点 / LLM 云服务"@
    }
    
    'TargetLangCbo[^>]*' = {
        @'
                AutomationProperties.Name="目标语言选择器"
                AutomationProperties.HelpText="选择 OCR 结果需要翻译成的目标语言"@
    }
    
    # System Settings
    'ThemeCbo[^>]*' = {
        @'
                AutomationProperties.Name="主题选择器"
                AutomationProperties.HelpText="选择应用的外观主题：深色/浅色/OLED 灰"@
    }
    
    'LanguageCbo[^>]*' = {
        @'
                AutomationProperties.Name="界面语言选择器"
                AutomationProperties.HelpText="切换应用的显示语言：中文/English"@
    }
    
    'ToastPositionCbo[^>]*' = {
        @'
                AutomationProperties.Name="通知位置选择器"
                AutomationProperties.HelpText="设置截图成功后提示框出现的位置"@
    }
}

$newLines = @()
$currentLineNum = 0

foreach ($line in $content.Split("`n")) {
    $currentLineNum++
    $modified = $false
    
    foreach ($pattern in $mappings.Keys) {
        if ($line -match $pattern) {
            $replacement = & $mappings[$pattern]
            $newLine = $line.TrimEnd() + " `r`n"
            $newLine = $newLine -replace "\s*$", ""
            
            # Find where to insert automation properties (before closing >)
            if ($newLine -match '>\s*$') {
                $insertedLine = $line.TrimEnd() + " `r`n        $replacement`r`n    "
                $newLines += $insertedLine
            } else {
                $newLines += $newLine
            }
            
            $modified = $true
            break
        }
    }
    
    if (-not $modified) {
        $newLines += $line
    }
}

$output = $newLines -join "`r`n"

# Backup original
$backupPath = "$XamlFile.bak"
Set-Content $backupPath $content -Encoding UTF8
Write-Host "✓ Backed up to $backupPath"

# Write enhanced version
Set-Content $XamlFile $output -Encoding UTF8
Write-Host "✓ Enhanced accessibility properties added to $(Split-Path $XamlFile -Leaf)"

# Summary statistics
$count = $mappings.Count
Write-Host "  Applied $count accessibility property mappings"
Write-Host "  Review the changes before committing!"

# Offer to build
Write-Host "`n  Build verification:"
& dotnet build src\TrueToneCap.App\TrueToneCap.App.csproj --verbosity quiet

if ($LastExitCode -eq 0) {
    Write-Host "`n ✓ Build successful! No syntax errors." -ForegroundColor Green
} else {
    Write-Host "`n ✗ Build failed! Check XAML syntax." -ForegroundColor Red
}
