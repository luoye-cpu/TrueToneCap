# 读一枚 PNG 的两份色彩声明，判它们是否互相矛盾：
#   cICP(码点)  vs  iCCP(ICC 的 rXYZ/gXYZ/bXYZ + wtpt + TRC 形状)
# 查看器优先级不同（多数现代查看器 ICC 覆盖 cICP），两份不一致就是"同一张图两种解释"。
param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference = 'Stop'

function Read-BE32([byte[]]$b, [int]$o) { return ([int]$b[$o]*16777216 + [int]$b[$o+1]*65536 + [int]$b[$o+2]*256 + [int]$b[$o+3]) }
function Read-U16([byte[]]$b, [int]$o) { return ($b[$o]*256 + $b[$o+1]) }
# ICC 的 XYZ 是 sfixed(2.30)：值 = int32 / 65536。用 [long] 累加再判符号 ——
# 直接 [int](大数) 在 PowerShell 里会抛 "Value was either too large or too small for an Int32"。
function Read-SFixed([byte[]]$b, [int]$o) {
    $v = ([int64]$b[$o] * 16777216) + ([int64]$b[$o+1] * 65536) + ([int64]$b[$o+2] * 256) + [int64]$b[$o+3]
    if ($v -ge 2147483648) { $v -= 4294967296 }
    return $v / 65536.0
}

$b = [System.IO.File]::ReadAllBytes($Path)
if (([Text.Encoding]::ASCII.GetString($b, 1, 3)) -ne 'PNG') { Write-Host "不是 PNG"; exit 1 }
$i = 8; $cicp = $null; $iccBytes = $null; $ihdr = $null
while ($i -lt $b.Length) {
    $len = Read-BE32 $b $i
    $type = [Text.Encoding]::ASCII.GetString($b, $i+4, 4)
    $data = $i + 8
    if ($type -eq 'IHDR') { $ihdr = @{ w=(Read-BE32 $b $data); h=(Read-BE32 $b ($data+4)); bd=$b[$data+8]; ct=$b[$data+9] } }
    if ($type -eq 'cICP') { $cicp = @($b[$data], $b[$data+1], $b[$data+2], $b[$data+3]) }
    if ($type -eq 'iCCP') {
        $z = $data
        while ($b[$z] -ne 0) { $z++ }             # 关键词 NUL 结尾
        $z++                                       # 压缩法字节
        # 此后是 zlib(RFC1950) 流：CMF,FLG 两字节头 + deflate 本体 + 4 字节 adler32
        $ms = New-Object System.IO.MemoryStream
        $raw = New-Object System.IO.MemoryStream(, $b[($z+3)..($data+$len-4-1)])
        $def = New-Object System.IO.Compression.DeflateStream($raw, [System.IO.Compression.CompressionMode]::Decompress)
        $def.CopyTo($ms); $def.Close(); $raw.Close()
        $iccBytes = $ms.ToArray()
    }
    $i += 12 + $len
}
Write-Host ("文件 {0}" -f (Split-Path $Path -Leaf))
Write-Host ("  IHDR {0}x{1} bitDepth={2} colorType={3}" -f $ihdr.w, $ihdr.h, $ihdr.bd, $ihdr.ct)
if ($cicp) {
    $pri = @{1='BT.709/sRGB';9='BT.2020';11='DCI-P3';12='Display-P3'}[[int]$cicp[0]]
    $tr  = @{13='sRGB';16='PQ';18='HLG'}[[int]$cicp[1]]
    Write-Host ("  cICP primaries={0}({1}) transfer={2}({3}) matrix={4} full={5}" -f $cicp[0], $pri, $cicp[1], $tr, $cicp[2], $cicp[3])
} else { Write-Host "  cICP: 无" }
if (-not $iccBytes) { Write-Host "  iCCP: 无"; exit 0 }

$tag = [Text.Encoding]::ASCII.GetString($iccBytes, 36, 4)   # acsp
$cs  = [Text.Encoding]::ASCII.GetString($iccBytes, 16, 4)   # PCS
$mft = [Text.Encoding]::ASCII.GetString($iccBytes, 40, 4)   # manufacturer
# 自检：ICC 头前 4 字节就是它自己声明的长度。解出来的字节数与它不等 ⇒ 是【本探针】读错了，
# 不是这份 profile 有问题 —— 先怀疑取值环节，再下结论（这条纪律本轮又救了一次自己）。
$declared = Read-BE32 $iccBytes 0
Write-Host ("  iCCP 载荷 {0} B  签名={1} PCS={2} 制造商标识={3}" -f $iccBytes.Length, $tag, $cs, $mft)
Write-Host ("  自检: ICC 自报长度={0}  解出={1}  ⇒ {2}" -f $declared, $iccBytes.Length,
    $(if ($declared -eq $iccBytes.Length) { 'PASS' } else { 'FAIL(探针读错，下面读数不作数)' }))
# tag 表：从 132 起，每项 sig+offset+size
$n = Read-BE32 $iccBytes 128
$want = @{ 'rXYZ'='rXYZ'; 'gXYZ'='gXYZ'; 'bXYZ'='bXYZ'; 'wtpt'='wtpt'; 'rTRC'='rTRC' }
$found = @{}
for ($k = 0; $k -lt $n; $k++) {
    $e = 132 + $k * 12
    $sig = [Text.Encoding]::ASCII.GetString($iccBytes, $e, 4)
    # ICC tag 表项 = signature(4) + offset(4) + size(4) —— 偏移在 e+4、长度在 e+8
    # （本探针第一版把这两个读反了，于是 rXYZ 的"类型"取到的是长度字段的字节，
    #   解出 null/NaN，一度让我把结论写成"这份 ICC 没有 XYZ 原色标签"。判探针也要先判自己。）
    if ($want.ContainsKey($sig)) { $found[$sig] = @{ off=(Read-BE32 $iccBytes ($e+4)); size=(Read-BE32 $iccBytes ($e+8)) } }
}
function XYZ([string]$sig) {
    if (-not $found.ContainsKey($sig)) { return $null }
    $o = $found[$sig].off
    $type = [Text.Encoding]::ASCII.GetString($iccBytes, $o, 4)
    if ($type -ne 'XYZ ') { return $null }
    return @((Read-SFixed $iccBytes ($o+8)), (Read-SFixed $iccBytes ($o+12)), (Read-SFixed $iccBytes ($o+16)))
}
$r = XYZ 'rXYZ'; $g = XYZ 'gXYZ'; $bl = XYZ 'bXYZ'; $w = XYZ 'wtpt'
if ($r -and $g -and $bl) {
    # x = X/(X+Y+Z)、y = Y/(X+Y+Z) —— 分母是【该原色自己那一列】的和，不是整张矩阵列和。
    # （上一版误用了"三列各自求和"当分母，于是绿点被印成 (0.17,0.675) 这种看着像 AdobeRGB 的假值；
    #   白点那一行当时却正好是 D65 —— 因为它用的是自己那一列的和，恰好是对的。两处不一致就是线索。）
    $rs = $r[0]+$r[1]+$r[2]; $gs = $g[0]+$g[1]+$g[2]; $bs = $bl[0]+$bl[1]+$bl[2]
    Write-Host ("  ICC 原色(x,y):  R=({0:F4},{1:F4})  G=({2:F4},{3:F4})  B=({4:F4},{5:F4})" -f `
        ($r[0]/$rs), ($r[1]/$rs), ($g[0]/$gs), ($g[1]/$gs), ($bl[0]/$bs), ($bl[1]/$bs))
    $dg = [math]::Sqrt([math]::Pow(($g[0]/$gs)-0.30,2) + [math]::Pow(($g[1]/$gs)-0.60,2))
    Write-Host ("  ICC 绿点相对 709 的拉伸 ΔG = {0:F4}   ⇒ {1}" -f $dg, $(if ($dg -ge 0.05) {'宽（与 BT.2020 声明同向）'} else {'不宽（与 BT.2020 声明矛盾）'}))
}
if ($w) { Write-Host ("  ICC 白点(x,y) = ({0:F4},{1:F4})   D65 参考 (0.3127,0.3290)" -f ($w[0]/($w[0]+$w[1]+$w[2])), ($w[1]/($w[0]+$w[1]+$w[2]))) }
if ($found.ContainsKey('rTRC')) {
    $o = $found['rTRC'].off; $type = [Text.Encoding]::ASCII.GetString($iccBytes, $o, 4)
    if ($type -eq 'curv') {
        $cnt = Read-BE32 $iccBytes ($o+8)
        if ($cnt -eq 0) { Write-Host "  rTRC: 线性（L 形 → 这份 ICC 描述的是**线性**空间！）" }
        elseif ($cnt -eq 1) { Write-Host ("  rTRC: gamma={0}" -f ((Read-U16 $iccBytes ($o+12)) / 100.0 * 512.0 / 1024.0)) }
        else {
            $mid = Read-U16 $iccBytes ($o + 12 + ($cnt/2)*2)
            Write-Host ("  rTRC: {0} 点查表，中点值={1}/65535 ⇒ 有效 gamma≈{2:F2}" -f $cnt, $mid, ([math]::Log($mid/65535.0, 0.5)))
        }
    } else { Write-Host "  rTRC: 类型 $type" }
}
