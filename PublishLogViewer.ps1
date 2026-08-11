# TrueToneCap.LogViewer Publish Script
# TTC 日志查看器 — 独立可选工具 (不随主包发布)
# 用法: .\PublishLogViewer.ps1 [-OutputDir "publish\TTC日志查看器"]

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish\TTC日志查看器"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "╔══════════════════════════════════════╗"
Write-Host "║  TTC 日志查看器 独立发布            ║"
Write-Host "║  可选工具 — 不随主包分发             ║"
Write-Host "╚══════════════════════════════════════╝"
Write-Host ""

# 清理旧输出
$OutputDir = Join-Path $RepoRoot $OutputDir
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue

# 自包含发布 (WinAppSDK 不支持 PublishSingleFile, 用标准目录发布)
Push-Location $RepoRoot
dotnet publish src\TrueToneCap.LogViewer\TrueToneCap.LogViewer.csproj `
    -c $Configuration -r $Runtime `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "编译失败 (Build failed)" }

# ── 复制应用级 PRI (WinUI3 免打包运行必需 — 缺失则 XamlParseException) ──
$binDir = $null
foreach ($c in @(
    "src\TrueToneCap.LogViewer\bin\x64\$Configuration\net11.0-windows10.0.26100.0\$Runtime",
    "src\TrueToneCap.LogViewer\bin\$Configuration\net11.0-windows10.0.26100.0\$Runtime"
)) { if (Test-Path $c) { $binDir = $c; break } }
$pri = Get-ChildItem $binDir -Filter "TTCLogViewer.pri" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pri) {
    Copy-Item $pri.FullName $OutputDir -Force
    Write-Host "   ✅ 应用级 PRI ($([math]::Round($pri.Length/1KB,1)) KB)"
} else {
    Write-Warning "   ⚠️ 未找到 TTCLogViewer.pri — 运行时可能 XAML 解析失败"
}

# 清理 pdb
Remove-Item "$OutputDir\*.pdb" -Force -ErrorAction SilentlyContinue

# 附带 README 说明
@"
# TTC 日志查看器 (WinUI 3)

独立的日志查看小工具, 用于快速排查 TrueToneCap 日志。

## 功能
- 日志文件列表 (最新在前, 含大小/时间)
- 实时 tail 查看 (自动滚动可关)
- 级别过滤 + 关键字搜索 (含 Tag/调用者)
- 完整日志解析: 时间戳/级别/分类/Tag/消息/调用者文件:行号
- 异常堆栈行自动关联到上一条日志
- 一键清理过期日志 (30 天前)
- 一键清空全部日志
- 目录总大小统计 / 选择日志目录
- Mica 背景深色主题 (现代化 WinUI 3)

## 使用方法
1. 将整个文件夹放在 TrueToneCap 安装目录下
2. 双击 TTC日志查看器.exe, 自动识别日志目录
3. 或点击"选择目录"手动指定 log/ 文件夹

> 该工具为独立可选项, 不随主程序包分发。
> 发布方式: .\PublishLogViewer.ps1
"@ | Set-Content (Join-Path $OutputDir "README.md") -Encoding UTF8

$size = [math]::Round((Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "✅ 发布完成: $OutputDir ($size MB)"
Pop-Location
