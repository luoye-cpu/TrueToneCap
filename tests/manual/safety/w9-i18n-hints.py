"""英文界面补完 · 第一批：落地页 Format 提示（依据 ui-en.png 的实机取证）。

实机证据：注入 "Language":"en" 后导航/标题/`PNG (Lossless)` 全部翻对，
但 Format 下面两行灰字仍是中文 ⇒ 机制通、覆盖面缺。

三件事一起做：
  ① 7 条 s_formatCaps.Hint 进 ZhToEn 表；
  ② 使用点包 LocaleManager.Tr()。【不能】在 s_formatCaps 声明处翻 —— 那是
     static readonly + 编译期常量，会把语言冻结在类型初始化那一刻，
     而它与 LocaleManager.SetLanguage() 的先后没有保证；
  ③ GainMap 那条是内插串（带动态 nits），拆成两段常量，各自进表。

按【每个文件自身的换行约定】写回：MainWindow.xaml.cs 现在是 CRLF，
LocaleManager.cs 是 LF —— 上一轮我犯过"把 LF 文件改成 CRLF"的方向性错误，这里显式避免。
"""
import io

LM = 'src/TrueToneCap.App/Services/LocaleManager.cs'
MW = 'src/TrueToneCap.App/MainWindow.xaml.cs'

HINTS = [
    ("✅ 无损格式，支持 HDR (cICP Rec.2100 PQ) + 广色域 ICC。截图首选。",
     "✅ Lossless; supports HDR (cICP Rec.2100 PQ) + wide-gamut ICC. Recommended for screenshots."),
    ("✅ Ultra HDR (ISO 21496-1)：SDR 查看器显示基础图，HDR 查看器还原完整动态范围。目标色域就是【底图】的色彩空间：Rec.709 或显式 Rec.2020（三件一起搬，底图本身仍是 SDR）。",
     "✅ Ultra HDR (ISO 21496-1): SDR viewers show the base image, HDR viewers restore the full dynamic range. The target gamut IS the base image's colour space - Rec.709 or explicit Rec.2020 (all three travel together; the base image stays SDR)."),
    ("✅ Google jpegli 编码，butteraugli 距离控制质量。8-bit、容器没有 HDR 通道；Rec.2020 目标只靠 ICC 声明（实测产物带 APP2/ICC），Rec.709 目标则【什么都不写】（靠 JFIF 默认）。",
     "✅ Encoded by Google jpegli, quality driven by butteraugli distance. 8-bit, and the container has no HDR channel; a Rec.2020 target relies on the ICC declaration only (measured output carries APP2/ICC), while a Rec.709 target writes [nothing at all] (falling back to the JFIF default)."),
    ("✅ 新一代格式，Modular 模式对截图极优。支持 HDR (Rec.2100 PQ) + 广色域。16-bit 走 16-bit PNG 中转（cjxl 无 -b 参数，sBIT=16 声明实际精度）。",
     "✅ New-generation format; Modular mode is excellent for screenshots. Supports HDR (Rec.2100 PQ) + wide gamut. 16-bit goes through a 16-bit PNG hand-off (cjxl has no -b flag; sBIT=16 declares the real precision)."),
    ("✅ 先进格式，支持 HDR（PQ）与广色域 SDR（Rec.2020 原色 + sRGB 传输，colr(nclx) 与 ICC 双标记）。NVENC 可硬件加速（AV1 仅 4:2:0），软件编码器支持 4:4:4 与 12-bit。位深与色度按所选编码器实际能力生效，不支持的组合已隐藏。",
     "✅ Advanced format supporting HDR (PQ) and wide-gamut SDR (Rec.2020 primaries + sRGB transfer, dual-marked by colr(nclx) and ICC). NVENC can hardware-accelerate it (AV1 is 4:2:0 only); the software encoders support 4:4:4 and 12-bit. Bit depth and chroma follow the selected encoder's real capability - unsupported combinations are hidden."),
    ("⚠ 仅 8-bit、容器没有 HDR 通道；广色域目标靠 ICC 声明（Rec.709 目标不写声明）。适合简单分享场景。",
     "⚠ 8-bit only, and the container has no HDR channel; a wide-gamut target relies on the ICC declaration (a Rec.709 target writes no declaration). Suited to casual sharing."),
    ("✅ TIFF 无损格式，支持 16-bit HDR + ICC 嵌入。适合存档。",
     "✅ Lossless TIFF, supports 16-bit HDR + embedded ICC. Suited to archiving."),
]

GAIN_A_ZH = "✅ Ultra HDR (ISO 21496-1)。编码基准 = 系统 SDR 白点 "
GAIN_A_EN = "✅ Ultra HDR (ISO 21496-1). Encoding reference = system SDR white point "
GAIN_B_ZH = " nits。兼容查看器（Chrome/照片）自动对齐；若查看器固定 203 nits，请手动把参考白度调到 "
GAIN_B_EN = " nits. Compatible viewers (Chrome / Photos) align automatically; if yours is fixed at 203 nits, set the reference white manually to "


def load(path):
    raw = io.open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in raw else '\n'
    return raw.replace('\r\n', '\n'), nl


def store(path, text, nl):
    out = text.replace('\n', '\r\n') if nl == '\r\n' else text
    io.open(path, 'w', encoding='utf-8', newline='').write(out)


# ──  LocaleManager.cs：7 + 2 条进表 ──
lm, lm_nl = load(LM)
i = lm.index('ZhToEn =')
close = lm.index('\n        };', i)
assert '["保存设置"]' in lm[i:close], '表尾锚点前没看到已知条目 ⇒ 定位可能错，不猜'
for zh, en in HINTS + [(GAIN_A_ZH, GAIN_A_EN), (GAIN_B_ZH, GAIN_B_EN)]:
    assert zh not in lm, f'这条已经在表里了：{zh[:24]}'
rows = ['        // ── 2026-10-03 英文界面补完 · 第一批：落地页 Format 提示（实机取证 ui-en.png）──']
rows += [f'            ["{zh}"] = "{en}",' for zh, en in HINTS]
rows += [f'            ["{GAIN_A_ZH}"] = "{GAIN_A_EN}",',
         f'            ["{GAIN_B_ZH}"] = "{GAIN_B_EN}",']
lm = lm[:close] + '\n' + '\n'.join(rows) + lm[close:]
store(LM, lm, lm_nl)
print(f'LocaleManager.cs 加入 {len(HINTS) + 2} 条；该文件换行约定 = {lm_nl!r}')

# ── ②③ 使用点包 Tr() ──
mw, mw_nl = load(MW)
old = ('        FormatHintTxt.Text = CurrentFormat() == OutputFormat.JPEG_GAINMAP\n'
       '            ? $"✅ Ultra HDR (ISO 21496-1)。编码基准 = 系统 SDR 白点 '
       '{(_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80)} nits。'
       '兼容查看器（Chrome/照片）自动对齐；若查看器固定 203 nits，请手动把参考白度调到 '
       '{(_settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80)}。"\n'
       '            : cap.Hint;')
assert mw.count(old) == 1, f'使用点锚点命中 {mw.count(old)} 次'
new = ('        // 翻译只能发生在这里，不能发生在 s_formatCaps 的声明处：那张表是 static readonly\n'
       '        // + 编译期常量，在声明处翻会把语言冻结在类型初始化那一刻，而它与 SetLanguage() 的先后没有保证。\n'
       '        // Tr() 未命中就原样返回 ⇒ 中文模式下这两条与改动前逐字相同。\n'
       '        int sdrWhite = _settings.SystemSdrWhiteLevel > 0 ? _settings.SystemSdrWhiteLevel : 80;\n'
       '        FormatHintTxt.Text = CurrentFormat() == OutputFormat.JPEG_GAINMAP\n'
       '            ? LocaleManager.Tr("' + GAIN_A_ZH + '") + sdrWhite\n'
       '              + LocaleManager.Tr("' + GAIN_B_ZH + '") + sdrWhite + "。"\n'
       '            : LocaleManager.Tr(cap.Hint);')
mw = mw.replace(old, new, 1)
store(MW, mw, mw_nl)
print(f'MainWindow.xaml.cs 使用点已包 Tr()；该文件换行约定 = {mw_nl!r}')
