# 跑 PowerShell 脚本之前先做一次"只解析不执行"的自检。
# 为什么要有：本仓已经栽过两次——双引号串里放 markdown 反引号会吞掉收尾引号，
# 以及整脚本因一个引号解析失败而"看起来没跑其实跑了个空"。
# 用法: powershell -NoProfile -File tests/manual/safety/psparse.ps1 <脚本路径>
param([Parameter(Mandatory = $true, Position = 0)][string]$Path)
$errs = $null
$null = [System.Management.Automation.PSParser]::Tokenize((Get-Content -Raw -LiteralPath $Path), [ref]$errs)
if ($errs -and $errs.Count) {
    foreach ($e in $errs) { Write-Output ("PARSE {0}: {1}" -f $e.Token.StartLine, $e.Message) }
    exit 1
}
# PSParser 只查词法；再用 AST 查一遍语法（能抓到"空管道元素"这类词法过关、语法不过关的）
$tokens = $null; $perrs = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $Path).Path, [ref]$tokens, [ref]$perrs)
if ($perrs -and $perrs.Count) {
    foreach ($pe in $perrs) { Write-Output ("AST {0}: {1}" -f $pe.Extent.StartLineNumber, $pe.Message) }
    exit 1
}
Write-Output "PARSE_OK $Path"
exit 0
