# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# W1a 实机矩阵：无感截图（Ctrl+Shift+Q）× 6 格式 × {SDR 档, HDR 档}
#
# 为什么选这条动作当矩阵的行：一次按键同时走完【捕获→色彩→编码→落盘→剪贴板】五环，
# 不需要拖拽、不需要工具栏坐标、不依赖前台焦点 ⇒ 前面 preview-copy-leg 那种
# "键到底进没进"的第三方变量被排除在外，量到的是链路本身而不是夹具。
# （选区那条腿另开一格 W1b，它要专门诊断键盘入口，见报告。）
#
# 每格三件证据，缺一件只能写"未判 + 卡在第几手"：
#   ① 外部裁判（C:\PLAN\FFmpeg 的 ffprobe/ffmpeg，非产品自带解码器）能否解码、尺寸/位深/声明；
#   ② 内容是"有结构的画面"还是平白一片（与 preview-content-cell 同一把尺 preview-stats-lib）；
#   ③ 产品自己的日志：这一格它自报做了什么（格式、HDR 判定、降级、编码完成）。
#
# 会动到你的：真实按两次键盘（注入式，不动光标）+ 写测试目录里的文件 + 覆盖剪贴板（轮前拍快照、轮末还原）。
# 设置按字节临时改（OutputPath→仓库内、FormatIndex→本格），收尾原样还原并复算 sha256。
param(
    [string]$Exe = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64\TrueToneCap.exe',
    [int[]]$Formats = @(0, 1, 2, 3, 4, 5),
    [ValidateSet('Sdr', 'Hdr')] [string]$Arm = 'Sdr',
    [int]$SettleSec = 12,
    [int]$CaptureWaitSec = 30,
    [string]$Log = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\w1a-silent.log',
    [string]$Tag = 'w1a'
)
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string[]]$ls) { [IO.File]::AppendAllText($Log, (($ls -join "`r`n") + "`r`n"), $utf8) }
# 双宿主哈希助手 —— 不用 Get-FileHash 的原因见 scripts/ps-compat.ps1 头注（PSModulePath 遮蔽）
. (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "scripts\ps-compat.ps1")
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-TtcFileHashHex -LiteralPath $p -Algorithm SHA256) } else { 'ABSENT' } }
. 'C:\PLAN\TrueToneCap\tests\manual\safety\preview-stats-lib.ps1'

$ffprobe = 'C:\PLAN\FFmpeg\ffprobe.exe'
$ffmpeg  = 'C:\PLAN\FFmpeg\ffmpeg.exe'
$dir     = Split-Path -Parent $Exe
$set     = Join-Path $dir 'TrueToneCap.settings.json'
$appLog  = Join-Path $dir ("log\app_" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$repoOut = 'C:\PLAN\TrueToneCap\tests\output\realmachine-2026-10-01\shots'
$fmtNames = @('PNG', 'JPEG_LI', 'JPEG_XL', 'AVIF', 'WebP', 'TIFF')   # Core/Settings/FormatOrder.cs UiFormats 逐字对照
New-Item -ItemType Directory -Force -Path $repoOut | Out-Null
[System.IO.File]::WriteAllText($Log, '', $utf8)
Say @("=== W1a 无感截图矩阵 · 档=$Arm · 格式=[$($Formats -join ',')] · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===")

$rc = 2
foreach ($t in @($ffprobe, $ffmpeg)) {
    if (-not (Test-Path -LiteralPath $t)) { Say @("RC=2 未判：外部裁判不在位 $t"); exit 2 }
}
if (-not (Test-Path -LiteralPath $set)) { Say @('RC=2 未判：没有同目录设置文件'); exit 2 }

# ── 前提：本进程 DPI 无感知会篡改 SetCursorPos；这一格不用鼠标，但剪贴板要读位图，先加类型 ──
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SK
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetForegroundWindow(out IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static void Chord(byte m1, byte m2, byte main)
    {
        keybd_event(m1, 0, 0, UIntPtr.Zero); keybd_event(m2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        keybd_event(main, 0, 2, UIntPtr.Zero);
        keybd_event(m2, 0, 2, UIntPtr.Zero); keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static string Fg()
    {
        IntPtr h; if (!GetForegroundWindow(out h)) return "none";
        uint pid; GetWindowThreadProcessId(h, out pid);
        return h + "/pid=" + pid;
    }
}
'@
if (-not ('SK' -as [type])) { Say @('RC=2 未判：P/Invoke 类型没编出来（Add-Type 编译失败会让每个读数伪装成"没有数据"）'); exit 2 }

# ── 设置：以轮前那份字节为模板，每次只改两处，改前断言锚点恰好命中一次 ──
$sha0 = Sha $set; $bytes0 = [IO.File]::ReadAllBytes($set)
Say @("轮前设置 sha256 = $sha0")
$raw0 = [IO.File]::ReadAllText($set)
$outPath0 = $null
try { $outPath0 = (Get-Content -LiteralPath $set -Raw -Encoding UTF8 | ConvertFrom-Json).OutputPath } catch { }
$anchorOut = '"OutputPath":"' + ($outPath0 -replace '\\', '\\') + '"'
$anchorHits = ([regex]::Matches($raw0, [regex]::Escape($anchorOut))).Count
# 0 次是合法的：差量写盘 ⇒ OutputPath 用默认（图片目录）时那个键根本不写（与 FormatIndex 缺失同一形状）
# ⇒ Set-Settings 走"插在首个 { 之后"的补键路（写入按 JSON 转义反斜杠，否则 System.Text.Json 拒读）。>1 才不猜。
if ($anchorHits -gt 1) {
    Say @("RC=2 未判：OutputPath 锚点命中 $anchorHits 次 ⇒ 不猜"); exit 2
}
Say @("轮前盘上 OutputPath 键命中 $anchorHits 次 $(if ($anchorHits -eq 0) { '（缺失 = 默认输出目录，本格按插入补键）' })")
$fmtHits = ([regex]::Matches($raw0, '"FormatIndex":\d+')).Count
# 0 次是合法的：差量写盘 ⇒ 值为默认(PNG=0)时那个键根本不写。>1 才是不敢猜的形状。
if ($fmtHits -gt 1) {
    Say @("RC=2 未判：FormatIndex 锚点命中 $fmtHits 次 ⇒ 不猜"); exit 2
}
Say @("轮前盘上 FormatIndex 键命中 $fmtHits 次 $(if ($fmtHits -eq 0) { '（缺失 = 默认 PNG，本格按插入补键）' })")
Say @("★ OutputPath 按字节临时从「$outPath0」改到 $repoOut（收尾原样还原；本机没有 D 盘，不改就是每格必失败）")

# ── 剪贴板轮前快照（文本 + 位图 + 文件drop），收尾尽量还原 ──
$cbText0 = $null; $cbImage0 = $null; $cbFiles0 = $null
try { $cbText0 = [System.Windows.Forms.Clipboard]::GetText() } catch { }
try { $cbImage0 = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
try { $cbFiles0 = [System.Windows.Forms.Clipboard]::GetFileDropList() } catch { }
Say @("轮前剪贴板：文本 $(if ($cbText0) { '有' } else { '无' }) 位图 $(if ($cbImage0) { "$($cbImage0.Width)x$($cbImage0.Height)" } else { '无' }) 文件 $(@($cbFiles0).Count) 个")

# ── native 后端在位情况（决定这一趟量的是真编码器还是保底）──
$nativeDir = Join-Path $dir 'native'
$nativeHave = @(Get-ChildItem -LiteralPath $nativeDir -Filter '*.exe' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
Say @("native/ 在位 = $(if ($nativeHave.Count) { $nativeHave -join ',' } else { '空 ⇒ 4 个外部后端全部缺位（本趟量的是"后端不在"那一档）'})")

function Set-Settings([int]$fmt) {
    $raw = [IO.File]::ReadAllText($set)
    if ($anchorHits -eq 1) {
        $patched = $raw.Replace($anchorOut, '"OutputPath":"' + ($repoOut -replace '\\', '\\') + '"')
    } else {
        # OutputPath 键缺失（差量写盘的默认形状）⇒ 插在首个 { 之后。
        # 写入的路径必须按 JSON 转义反斜杠（C:\ → C:\\，-replace 替换串给 '\\' 两个字符即得），
        # 否则产品侧 System.Text.Json 读取直接失败。
        if (([regex]::Matches($raw, '\{')).Count -ne 1) { throw 'OutputPath 缺失且无法安全插入（花括号数≠1）' }
        $patched = $raw.Replace('{', ('{"OutputPath":"' + ($repoOut -replace '\\', '\\') + '",'))
    }
    $repl = '"FormatIndex":' + $fmt
    if (([regex]::Matches($patched, '"FormatIndex":\d+')).Count -eq 1) {
        $patched = [regex]::Replace($patched, '"FormatIndex":\d+', $repl)
    } else {
        # 键缺失 = 差量写盘的常态（0 就是默认），补键时只认第一个 {
        if (([regex]::Matches($patched, '\{')).Count -ne 1) { throw 'FormatIndex 缺失且无法安全插入' }
        $patched = $patched.Replace('{', ('{"FormatIndex":' + $fmt + ','))
    }
    [IO.File]::WriteAllText($set, $patched, $utf8)
    return $patched
}

$rows = @()
$proc = $null
try {
    foreach ($fmt in $Formats) {
        $name = $fmtNames[$fmt]
        $json = Set-Settings $fmt
        Say @("")
        Say @("──── 格 $name (FormatIndex=$fmt) 档=$Arm ────")
        Say @("   盘上设置这一格 = $($json.Substring(0, [Math]::Min(200, $json.Length)))")

        if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { } $proc = $null; Start-Sleep -Seconds 2 }
        if ($Arm -eq 'Sdr') { $env:TTC_FORCE_SDR = '1' } else { Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue }
        $logMark = 0; if (Test-Path -LiteralPath $appLog) { $logMark = @(Get-Content -LiteralPath $appLog -Encoding UTF8).Count }
        $before = @(Get-ChildItem -LiteralPath $repoOut -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })

        $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory $dir
        Say @("   PID=$($proc.Id)，等 $SettleSec s")
        Start-Sleep -Seconds $SettleSec

        $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
        $hk = @($slice | Where-Object { $_ -match '已注册:\s*(Ctrl\S*\+Q)' } | Select-Object -First 1)
        $chord = if ($hk) { ([regex]'已注册:\s*(Ctrl\S*\+Q)').Match($hk[0]).Groups[1].Value } else { 'Ctrl+Shift+Q' }
        Say @("   热键 = $chord $(if ($hk) { '（从本会话日志现读）' } else { '（★ 日志没自报，按默认试 ⇒ 这一格要盯"没按到"）' })")
        Say @("   按键前前台窗 = $([SK]::Fg())")
        if ($chord -eq 'Ctrl+Shift+Q') { [SK]::Chord(0x11, 0x10, 0x51) } else { [SK]::Chord(0x11, 0x12, 0x51) }

        # 等产品自己写出完成行（不固定睡：§22 的教训是读早了会把 race 读成产品缺陷）
        $doneLine = $null; $newFile = $null; $dl = (Get-Date).AddSeconds($CaptureWaitSec)
        while ((Get-Date) -lt $dl -and -not $doneLine) {
            $s2 = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
            $hit = @($s2 | Where-Object { $_ -match '编码完成' } | Select-Object -Last 1)
            if ($hit) { $doneLine = $hit[0] }
            Start-Sleep -Milliseconds 400
        }
        $slice = @(Get-Content -LiteralPath $appLog -Encoding UTF8 | Select-Object -Skip $logMark)
        $c = @(Get-ChildItem -LiteralPath $repoOut -File -Recurse -ErrorAction SilentlyContinue |
               Where-Object { $before -notcontains $_.FullName })
        if ($c.Count -ge 1) { $newFile = ($c | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }

        $code = ''
        if (-not $doneLine) { $code = 'no-completion-line' }
        elseif (-not $newFile) { $code = 'no-file' }

        # ① 剪贴板三通道
        $cbImg = $null; $cbFiles = @(); $cbTxt = $null
        try { $cbTxt = [System.Windows.Forms.Clipboard]::GetText() } catch { }
        try { $cbImg = [System.Windows.Forms.Clipboard]::GetImage() } catch { }
        try { $cbFiles = @([System.Windows.Forms.Clipboard]::GetFileDropList()) } catch { }
        $cbDesc = "位图=$(if ($cbImg) { "$($cbImg.Width)x$($cbImg.Height)" } else { '无' }) 文件=$($cbFiles.Count) 文本=$(if ($cbTxt) { '有' } else { '无' })"
        Say @("   ①' 剪贴板：$cbDesc")
        if ($cbFiles.Count -ge 1) {
            $leafs = @($cbFiles | ForEach-Object { Split-Path -Leaf $_ }) -join ', '
            Say @("        drop 列表 = $leafs")
        }
        if (-not $code -and -not $cbImg -and $cbFiles.Count -eq 0) { $code = 'clipboard-empty' }

        # 产品自述（这一格它说了什么：格式、HDR 判定、降级、失败）
        $own = @($slice | Where-Object { $_ -match '捕获配置|开始编码|编码完成|降级|保底|回退|不可用|未找到|失败|位图通道|HDR' })
        Say @("   ③ 产品自述 $($own.Count) 条：")
        foreach ($l in ($own | Select-Object -Last 8)) { Say @("     | $($l.Substring(0, [Math]::Min(160, $l.Length)))") }
        $warns = @($slice | Where-Object { $_ -match '\[(Warning|Error)' })
        Say @("   ③' 本会话 Warning/Error $($warns.Count) 条 $(if ($warns.Count) { '⇒ ' + ($warns[0].Substring(0, [Math]::Min(140, $warns[0].Length))) } else { '' })")

        # ② 外部裁判
        $decl = ''; $stats = ''
        if ($newFile) {
            $j = & $ffprobe -v error -show_entries stream=codec_name,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries -of json $newFile 2>&1
            if ("$LASTEXITCODE" -ne '0') {
                $decl = "★ffprobe 拒绝解码(exit=$LASTEXITCODE)"
                if (-not $code) { $code = 'referee-cannot-decode' }
            } else {
                $s = ($j | ConvertFrom-Json).streams[0]
                $decl = "$($s.codec_name) $($s.width)x$($s.height) pix=$($s.pix_fmt) 空间=$($s.color_space) 传输=$($s.color_transfer) 原色=$($s.color_primaries)"
                Say @("   ② 外部裁判：$decl")
                $side = & $ffprobe -v error -select_streams v:0 -show_entries frame_side_data_list -of json $newFile 2>&1
                $sideTxt = ($side -join ' ')
                $hasIcc = $sideTxt -match 'icc_profile'
                Say @("      ICC 侧数据=$hasIcc")
                $bmp = Join-Path $repoOut "_c.bmp"
                if (Test-Path -LiteralPath $bmp) { Remove-Item -LiteralPath $bmp -Force }
                & $ffmpeg -v error -y -i $newFile -pix_fmt rgb24 $bmp | Out-Null
                if ("$LASTEXITCODE" -ne '0' -or -not (Test-Path -LiteralPath $bmp)) {
                    $stats = '★ffmpeg 读不出 8-bit 副本'
                    if (-not $code) { $code = 'referee-cannot-decode' }
                } else {
                    $st = Get-BitmapStats -File $bmp
                    $stats = "内容 $($st.Kinds) 色/白 $($st.WhitePct)%/黑 $($st.BlackPct)% ⇒ $(Get-StatsVerdict -st $st)"
                    Say @("      $stats")
                    Remove-Item -LiteralPath $bmp -Force -ErrorAction SilentlyContinue
                }
            }
            $fi = Get-Item -LiteralPath $newFile
            Say @("      产物 = $(Split-Path -Leaf $newFile) $([math]::Round($fi.Length/1KB,1)) KB")
        }
        # ── ④ 请求 ↔ 交付：这一格是本轮 W1 的核心，第一版把它漏了，于是"要 JXL 拿到 PNG"被判成 OK ──
        # 06:50 那次实测：盘上 FormatIndex=2(JPEG XL)、native/ 空 ⇒ 产物是 .png，而旧版本格仍打 OK，
        # 因为只查了"文件在不在、能不能解码、是不是平白"——一把不查"是不是要的那张"的尺子就是假绿源。
        $selfFmt = ''
        $cf = @($slice | Where-Object { $_ -match '捕获配置: 格式=(\S+)' } | Select-Object -First 1)
        if ($cf) { $selfFmt = ([regex]'捕获配置: 格式=(\S+)').Match($cf[0]).Groups[1].Value }
        $exts = @('.png'); $want = 'PNG'
        switch ($fmt) {
            0 { $exts = @('.png'); $want = 'PNG' }
            1 { $exts = @('.jpg', '.jpeg'); $want = 'JPEG_LI/JPEG_GAINMAP' }
            2 { $exts = @('.jxl'); $want = 'JPEG_XL' }
            3 { $exts = @('.avif'); $want = 'AVIF' }
            4 { $exts = @('.webp'); $want = 'WebP' }
            5 { $exts = @('.tif', '.tiff'); $want = 'TIFF' }
        }
        $gotExt = if ($newFile) { [IO.Path]::GetExtension($newFile).ToLower() } else { '' }
        $extOk = $exts -contains $gotExt
        $selfOk = if ($fmt -eq 1) { $selfFmt -in @('JPEG_LI', 'JPEG_GAINMAP') } else { $selfFmt -eq $want }
        Say @("   ④ 请求=$name 产品自报=$selfFmt 落盘后缀=$gotExt（允许 $($exts -join '/')）⇒ 后缀符合=$extOk 自报符合=$selfOk")
        if (-not $code) {
            if (-not $extOk) { $code = "format-changed-to$gotExt" }
            elseif (-not $selfOk) { $code = 'self-report-mismatch' }
        }
        if (-not $code) { $code = 'OK' }
        $rows += , @($Arm, $name, $code, "$selfFmt/$gotExt", $decl, $stats, $cbDesc)
        Say @("   判词：$code")
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Say @("   已停止本次起的 PID=$($proc.Id)") } catch { }
        $proc = $null; Start-Sleep -Seconds 1
    }
    $rc = 0
} catch {
    Say @("★ 中断：$($_.Exception.Message)")
} finally {
    Remove-Item Env:TTC_FORCE_SDR -ErrorAction SilentlyContinue
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    if ((Sha $set) -ne $sha0) { [IO.File]::WriteAllBytes($set, $bytes0) }
    $same = (Sha $set) -eq $sha0
    Say @("设置还原自检：$(Sha $set) ⇒ $(if ($same) { '与轮前逐字相同 ✓' } else { '★ 与轮前不同，立即核对' })")
    if (-not $same) { $rc = 2 }
    try {
        if ($cbImage0) { [System.Windows.Forms.Clipboard]::SetImage($cbImage0) }
        elseif ($cbText0) { [System.Windows.Forms.Clipboard]::SetText($cbText0) }
        Say @("剪贴板还原：$(if ($cbImage0) { '已放回轮前那张位图' } elseif ($cbText0) { '已放回轮前那段文本' } else { '轮前本来就是空的 ⇒ 不还原，如实记' })")
    } catch { Say @("★ 剪贴板还原失败：$($_.Exception.Message)（如实记，不假装成功）") }
}

Say @("")
Say @("=== 汇总（档=$Arm）===")
Say @("档 | 格式 | 判词 | 自报/落盘 | 外部裁判 | 内容 | 剪贴板")
foreach ($r in $rows) { Say @(($r -join ' | ')) }
$bad = @($rows | Where-Object { $_[2] -ne 'OK' })
Say @("格子 $($rows.Count) 枚，非 OK $($bad.Count) 枚 $(if ($bad.Count) { '⇒ ' + (($bad | ForEach-Object { "$($_[1]):$($_[2])" }) -join ', ') })")
# 退出码必须跟着点名条数走：第一版只要跑完就打 RC=0，于是"1 格换了格式"照样以 RC=0 收工
# ——汇总数与判决脱钩的守卫，本身就是最大的假绿源。
if ($bad.Count -gt 0) { $rc = 1 }
Write-Output "RC=$rc 格 $($rows.Count) 非OK $($bad.Count)"
exit $rc
