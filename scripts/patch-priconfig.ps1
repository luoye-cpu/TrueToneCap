# 为本地化应用显示名补一条 Strings 文件夹索引。
#
# 背景（2026-11-13，TrueToneCap 0.3.16）：
#   .NET SDK-style WinUI 3 + Microsoft.Windows.SDK.BuildTools.MSIX 的 PRI 生成链
#   在 makepri 10.0.26100.0 下对 .resw 的 resheader 报 PRI224（root node not found），
#   去掉 resheader 又不产生命名资源；实测 .resjson + folder index 可以稳定产出
#   AppDisplayName（zh-CN / en-US 两个候选）。
#
# 这个脚本在 SDK 生成 priconfig.xml 之后、_GenerateProjectPriFileCore 之前运行，
# 给配置插入一条 <index startIndexAt="Strings">（folder + RESJSON）。
# 不删除 SDK 自带的 resources.resfiles 索引：0.3.16 起 Strings 下已无 .resw，
# 那条索引为空，不再触发 PRI224；保留它可以兼容未来 SDK 重新引入的 PRIResource。
#
# 用法（由 TrueToneCap.App.csproj 的 PatchPriConfigForLocalizedDisplayName target 调用）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\patch-priconfig.ps1 `
#     -ConfigPath <obj>\priconfig.xml -StringsDir Strings

param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [string]$StringsDir = "Strings"
)

$ErrorActionPreference = "Stop"

$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "PRI config not found: $ConfigPath"
}

[xml]$doc = Get-Content -LiteralPath $ConfigPath -Raw
$root = $doc.DocumentElement

# 1) 已经有 Strings 索引就不重复插（重复 index 会让 makepri 报 schema/重复错误）
$hasStringsIndex = $false
foreach ($idx in @($root.SelectNodes("index"))) {
    if ($idx.startIndexAt -eq $StringsDir) {
        $hasStringsIndex = $true
        break
    }
}

if (-not $hasStringsIndex) {
    # 以现有第一条 index 的 default 为模板，保持与 SDK 生成的 qualifier 集一致
    $templateDefault = $null
    foreach ($idx in @($root.SelectNodes("index"))) {
        $defaultNode = $idx.SelectSingleNode("default")
        if ($null -ne $defaultNode) {
            $templateDefault = $defaultNode
            break
        }
    }

    $newIndex = $doc.CreateElement("index")
    $newIndex.SetAttribute("root", "\")
    $newIndex.SetAttribute("startIndexAt", $StringsDir)

    if ($null -ne $templateDefault) {
        $newIndex.AppendChild($templateDefault.CloneNode($true)) | Out-Null
    }
    else {
        $default = $doc.CreateElement("default")
        $lang = $doc.CreateElement("qualifier")
        $lang.SetAttribute("name", "Language")
        $lang.SetAttribute("value", "en-US")
        $default.AppendChild($lang) | Out-Null
        $scale = $doc.CreateElement("qualifier")
        $scale.SetAttribute("name", "Scale")
        $scale.SetAttribute("value", "200")
        $default.AppendChild($scale) | Out-Null
        $newIndex.AppendChild($default) | Out-Null
    }

    $folder = $doc.CreateElement("indexer-config")
    $folder.SetAttribute("type", "folder")
    $folder.SetAttribute("foldernameAsQualifier", "true")
    $folder.SetAttribute("filenameAsQualifier", "true")
    $folder.SetAttribute("qualifierDelimiter", ".")
    $newIndex.AppendChild($folder) | Out-Null

    $resjson = $doc.CreateElement("indexer-config")
    $resjson.SetAttribute("type", "RESJSON")
    $resjson.SetAttribute("initialPath", "")
    $newIndex.AppendChild($resjson) | Out-Null

    $root.InsertBefore($newIndex, $root.FirstChild) | Out-Null
}

# 2) 写回：UTF-8 无 BOM + 两空格缩进，保持 makepri 可读
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.Indent = $true
$settings.IndentChars = "  "
$settings.NewLineChars = "`r`n"

$writer = [System.Xml.XmlWriter]::Create($ConfigPath, $settings)
try {
    $doc.Save($writer)
}
finally {
    $writer.Close()
}

Write-Output "Patched PRI config: $ConfigPath"
