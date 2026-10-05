"""修两条静默失效的 SetComboItemText（可用性审计 §8 F6 的两处副产物）。

先把事实钉住（都从 src/TrueToneCap.Core/Services/OcrLanguage.cs 读来，不是猜的）：
  OnnxLanguages    的 ID 有  zh-en / ch / en / ja / ko / fr / de / es / pt / ru / ar / it / nl / pl / th / vi
  WindowsOcrLanguages 的 ID 有  zh-Hans-CN / zh-Hant-TW / en-US / en-GB / ja-JP / ko-KR / fr-FR / de-DE …

⇒ :193 "zh-en" 与 :195 "en-US" 是【条件有效】（各自只在对应引擎被选中时命中），不是死调用；
⇒ :194 的 "zh-Hans" 两份列表里都没有，真实 ID 是 "zh-Hans-CN" ⇒ 静默失效，改对；
⇒ :192 的 Tag 是空串 ""，两份列表里都没有，而且 OlSystem="跟随系统" 这个选项在本产品里
   根本不存在（语言表里没有"跟随系统"这一项）⇒ 不是"写错 Tag"，是【为一个不存在的选项做翻译】，
   删掉调用而不是硬凑一个目标。

⚠ 本脚本不做全仓规范化，只动这两行；改完必须复跑尺子确认 unhooked 数没有变坏。
"""
import io

P = 'src/TrueToneCap.App/MainWindow.Localization.cs'
s = io.open(P, encoding='utf-8', newline='').read()

dead_empty = '        SetComboItemText(OcrLangCbo, "", LocaleManager.OlSystem);\r\n'
dead_hans = '        SetComboItemText(OcrLangCbo, "zh-Hans", LocaleManager.TlChinese);'
if dead_empty not in s:
    dead_empty = dead_empty.replace('\r\n', '\n')
if '\r\n' not in s:
    dead_hans = dead_hans.replace('\r\n', '\n')

assert s.count(dead_empty) == 1, ('空串那行没找到', s.count(dead_empty))
assert s.count(dead_hans) == 1, ('zh-Hans 那行没找到', s.count(dead_hans))

s = s.replace(dead_empty,
              '        // "跟随系统" 这一项在 OcrLanguage 两份列表里都不存在 ⇒ 原先这条 Tag 传空串，\r\n'
              '        // 永远匹配不到任何项（静默死调用）。要支持它得先加语言项，不是先加翻译。\r\n',
              1)
s = s.replace(dead_hans,
              '        SetComboItemText(OcrLangCbo, "zh-Hans-CN", LocaleManager.TlChinese);   // 原写 "zh-Hans"：两份列表里都没有这个 ID',
              1)

io.open(P, 'w', encoding='utf-8', newline='').write(s)
b = open(P, 'rb').read()
crlf, lf = b.count(b'\r\n'), b.count(b'\n') - b.count(b'\r\n')
# 我上面插入的注释用的是 \r\n；若本文件本来是纯 LF，就会被我搞成混合换行
# （MainWindow.xaml.cs 刚踩过一次，HEAD 纯 LF、被我带进 17 个 CRLF）。这里当场纠正。
if crlf and lf == 0:
    pass
elif lf and crlf:
    b2 = b.replace(b'\r\n', b'\n') if lf > crlf else b
    if b2 != b:
        open(P, 'wb').write(b2)
        b = b2
        crlf, lf = b.count(b'\r\n'), b.count(b'\n') - b.count(b'\r\n')
        print('（已把插入的 CRLF 归一回本文件的 LF 约定）')
print('两条死调用已处理；BOM=', b[:3] == b'\xef\xbb\xbf',
      ' CRLF=', crlf, ' 裸LF=', lf)
