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
function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Get-Sha256Hex: 文件不存在: $LiteralPath"
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        # FileShare 与 Get-FileHash 同款（Read + 允许他人读写）：发布目录正在被并行访问时不炸
        $fs = [System.IO.File]::Open($LiteralPath, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') }
        finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}
