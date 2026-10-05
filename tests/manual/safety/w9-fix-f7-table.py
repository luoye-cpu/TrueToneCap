"""F7 的第二半：改文案必须同一次改动里更新对照表，否则本地化闸门会红四条（实测就是红了四条）。

同一个"需重启"的错误声明在源码里有【两份】：
  · LocaleManager.cs:145  FontDesc 属性的中文侧与英文侧
  · LocaleManager.cs:311  ZhToEn 表的键与值
只改 XAML 会留下：新串不在表里（待接 1）+ 旧串变成死键（基线 0 → 实得 1）
+ 变异自检的期望数被顶偏。所以四处（新中/新英 × 两处）必须一起动。
"""
import io

P = 'src/TrueToneCap.App/Services/LocaleManager.cs'
OLD_ZH = '界面字体仅影响应用外观，不影响截图像素。选择已安装的字体后需重启应用生效。'
OLD_EN = 'Font only affects UI appearance, not captured pixels. Restart required after change.'
NEW_ZH = '界面字体仅影响应用外观，不影响截图像素。选择后立即生效；该字体未安装时界面会回落到默认字体。'
NEW_EN = ('Font only affects UI appearance, not captured pixels. Applies immediately; '
          'if the font is not installed the UI falls back to the default.')

s = io.open(P, encoding='utf-8', newline='').read()
assert s.count(OLD_ZH) == 2, s.count(OLD_ZH)
assert s.count(OLD_EN) == 2, s.count(OLD_EN)
s = s.replace(OLD_ZH, NEW_ZH).replace(OLD_EN, NEW_EN)
io.open(P, 'w', encoding='utf-8', newline='').write(s)
b = open(P, 'rb').read()
print('LocaleManager 两处中文 + 两处英文已同步；BOM=', b[:3] == b'\xef\xbb\xbf',
      ' CRLF=', b.count(b'\r\n'), ' 裸LF=', b.count(b'\n') - b.count(b'\r\n'))
