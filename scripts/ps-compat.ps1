# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# scripts/ps-compat.ps1 —— 双宿主（Windows PowerShell 5.1 / PowerShell 7.x）兼容助手。
# 消费方：Publish.ps1、scripts/verify.ps1、scripts/pack.ps1（dot-source 本文件）。
#
# 为什么存在（2026-10-26 实测取证）：
#   Windows PowerShell 5.1 在"继承的 PSModulePath 把 pwsh 7 的目录排在前面"的环境里
#   （ZCode / VS Code 的 pwsh 集成终端、Git Bash 会话等都会这样继承），
#   模块自动解析会【抢先命中 pwsh 7 的 Microsoft.PowerShell.Utility 清单】——
#   版本号变成 7.0.0.0、导出 99 个命令、但没有 Get-FileHash，且其程序集是
#   .NET (Core) 编译的，5.1 的 .NET Framework CLR 根本装不进去。
#   实测症状：Get-FileHash 直接"命令不存在"（ConvertFrom-Json 等部分命令侥幸仍可用），
#   于是发布门禁的模型/工具 SHA256 校验整层报"依赖校验失败"（历史上被误归因为
#   "5.1 缺 Get-FileHash"——5.1 其实有这个命令，4.0 起就有）。
#
#   本函数走 .NET 直调，不经过任何 cmdlet 解析，两个宿主行为逐字一致；
#   返回【大写、无连字符】的 hex —— 与 Get-FileHash 的 .Hash 完全同形，
#   调用方原有的 .ToLowerInvariant() 链一概不用改。
#
# 兜底关系：git 钩子在回退 5.1 时还会擦掉继承的 PSModulePath（见 scripts/git-hooks/*），
#   那是环境层的防线；本文件是脚本层的防线 —— 用户从被污染的控制台手动跑门禁时也安全。
function Get-TtcFileHashHex {
    # 通用版（SHA1/SHA256）：2026-10-26 第二轮把 tests/manual 旁路探针与 download_models.ps1
    # 也从 Get-FileHash 迁过来时加的 —— 那批探针有一半用 SHA1 给本机设置文件做指纹（非安全用途）。
    # 行为约束与 Get-Sha256Hex 相同：.NET 直调、大写无连字符、FileShare 与 Get-FileHash 同款。
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [ValidateSet('SHA1', 'SHA256')][string]$Algorithm = 'SHA256'
    )
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Get-TtcFileHashHex: 文件不存在: $LiteralPath"
    }
    if ($Algorithm -eq 'SHA1') { $sha = [System.Security.Cryptography.SHA1]::Create() }
    else { $sha = [System.Security.Cryptography.SHA256]::Create() }
    try {
        # FileShare 与 Get-FileHash 同款（Read + 允许他人读写）：文件正被并行访问时不炸
        $fs = [System.IO.File]::Open($LiteralPath, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') }
        finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    Get-TtcFileHashHex -LiteralPath $LiteralPath -Algorithm SHA256
}

# 本轮门禁跑在哪个宿主上 —— 只做日志展示（verify.ps1 头部打印），绝不参与门禁判定：
# 本仓选主靠"可用性探测"（钩子 command -v / csproj where pwsh / 子进程跟随调用者），
# 不按版本号放行或拒绝；这里读 $PSVersionTable 纯为可观测性，与那条纪律不冲突。
# 5.1 的 $PSVersionTable 自带 PSEdition=Desktop，ContainsKey 只是防御更老的宿主。
function Get-TtcPsHostLabel {
    $v = $PSVersionTable.PSVersion.ToString()
    if ($PSVersionTable.ContainsKey('PSEdition') -and $PSVersionTable.PSEdition -eq 'Core') {
        return "pwsh $v"
    }
    return "Windows PowerShell $v"
}
