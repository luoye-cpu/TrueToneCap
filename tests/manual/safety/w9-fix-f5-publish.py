"""F5：发布目录里不许夹带维护者的自测样张。

实测：publish/TrueToneCap-v0.3.6-beta/color-samples/____DISPLAY5-{System,BT2020}.png
两个文件各 7 024 730 B（合计 13.4 MB），时间戳 09-28，而发布目录是 10-01 生成的。
来源已确证：SelfTest.cs:148 把样张写到 AppContext.BaseDirectory，
而 --selftest 是命令行专用入口（App.xaml.cs:77 跑完就 Environment.Exit）⇒
它是【维护者验证时留下的产物】，不是随包资产，用户路径永远不会读它。
grep -c 'color-samples' Publish.ps1 = 0 ⇒ 从来没有排除规则。

形状照本文件已有的"剔第三方 PDB"那一段：先数、再删、报出释放了多少，
删完还要【断言包内确实不再残留】——只删不验，下次有人改回来也不会红。
"""
import io

P = 'Publish.ps1'
ANCHOR = '$buildSize = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB'

BLOCK = '''# ── 剔除维护者自测样张（2026-10-02 可用性审计 F5）────────────────────────────
# color-samples/ 是 SelfTest.cs:148 写到 AppContext.BaseDirectory 的产物，只有 --selftest
# 这条命令行入口会生成它（App.xaml.cs:77 跑完即 Environment.Exit）⇒ 用户路径不读它。
# 它出现在发布目录，是因为有人【在发布目录里跑过自测】：本轮实测到两个 7 024 730 B 的
# ____DISPLAY5-{System,BT2020}.png，时间戳比发布目录还早三天。
# 除了占体积，它还把维护者显示器的 sanitized 名字（DISPLAY5）发给用户。
$sampleDirs = @(Get-ChildItem $OutputDir -Recurse -Directory -Filter "color-samples" -ErrorAction SilentlyContinue)
if ($sampleDirs.Count -gt 0) {
    $sFiles = @($sampleDirs | Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue)
    $sFreed = [math]::Round((($sFiles | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
    foreach ($d in $sampleDirs) { Remove-Item $d.FullName -Recurse -Force }
    Write-Host "   🧹 剔除自测样张目录: $($sampleDirs.Count) 个 / $($sFiles.Count) 文件 / $sFreed MB"
}
# 删完必须自己复核：只删不验，下次被改回来也不会红
$leftover = @(Get-ChildItem $OutputDir -Recurse -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'color-samples' -or $_.Name -match '^_{2,}DISPLAY' })
if ($leftover.Count -gt 0) {
    Write-Host "   ❌ 发布目录仍残留自测样张 $($leftover.Count) 项: $(($leftover | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "   ✅ 发布目录无自测样张残留"

'''

s = io.open(P, encoding='utf-8', newline='').read()
assert s.count(ANCHOR) == 1, s.count(ANCHOR)
nl = '\r\n' if '\r\n' in s else '\n'
block = BLOCK.replace('\n', nl) if nl == '\r\n' else BLOCK
s = s.replace(ANCHOR, block + ANCHOR, 1)
io.open(P, 'w', encoding='utf-8', newline='').write(s)
print('F5 已加入 Publish.ps1：剔除 color-samples/ 并断言无残留')
