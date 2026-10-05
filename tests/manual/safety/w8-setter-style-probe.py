"""B0 可行性探针：Setter.Value 能不能携带路径 mini-language 字符串。

判据设计成【同一张图里自带对照】：MainWindow 第 3 个导航项图标（line 33）与
AI 翻译页页眉那枚（line 374）用的是【逐字相同】的 Data（本轮去重实测：46 枚引用只有 32 条不同串，
这一条属于 x4 重复组）。把 33 改成 Style 形式、374 留内联形式 ⇒ 选中"OCR / 翻译"页后
同一张截图里两枚并排，一枚有形状一枚没有就是 Style 机制不成立，不需要跨图比较。

不把 1593 字符的 Data 打印到终端，只做等值断言。
"""
import io, os, re, sys

ROOT = r'C:\PLAN\TrueToneCap'
MAIN = os.path.join(ROOT, 'src', 'TrueToneCap.App', 'MainWindow.xaml')
APP = os.path.join(ROOT, 'src', 'TrueToneCap.App', 'App.xaml')
KEY = 'TtcIcon.E164'


def read(p):
    return io.open(p, encoding='utf-8', newline='').read()


def write(p, s):
    # 读写都用 encoding='utf-8'（不是 utf-8-sig）⇒ 首字节 BOM 会以 \ufeff 原样往返，
    # newline='' ⇒ CRLF 不被翻译成 LF。两处版式都必须与在库版式逐字一致。
    with io.open(p, 'w', encoding='utf-8', newline='') as f:
        f.write(s)


main = read(MAIN)
app = read(APP)

# --- 1. 取第 3 枚 PathIcon 的 Data，并确认它与第 374 行那枚逐字相同 ---
icons = list(re.finditer(r'<PathIcon\b[^>]*?/>', main, re.S))
assert len(icons) == 8, f'MainWindow 应有 8 枚 PathIcon，实得 {len(icons)}'
nav3 = icons[2]
data3 = re.search(r'Data="([^"]+)"', nav3.group(0)).group(1)
hdr = [m for m in icons if main[:m.start()].count('\n') + 1 == 374]
assert len(hdr) == 1, f'line 374 上应恰有一枚 PathIcon，实得 {len(hdr)}'
data374 = re.search(r'Data="([^"]+)"', hdr[0].group(0)).group(1)
assert data3 == data374, '对照臂失效：两枚 Data 不逐字相同，这张图就不构成 A/B'
assert data3.startswith('F0 '), 'Data 应以 EvenOdd 前缀 "F0 " 开头'
print(f'对照臂成立：两枚 Data 逐字相同，长 {len(data3)} 字符')

# --- 2. App.xaml 里插入一条 Style（放在 MergedDictionaries 之后，避免落进主题字典）---
assert KEY not in app, 'Style key 已存在，探针可能没清理干净'
anchor = '            </ResourceDictionary.MergedDictionaries>'
assert app.count(anchor) == 1, f'锚点命中 {app.count(anchor)} 次（要求 1）⇒ 不猜'
style = (anchor + '\r\n'
         '\r\n'
         '            <!-- B0 可行性探针（2026-10-02）：验 Setter.Value 能不能带路径 mini-language。\r\n'
         '                 若成立，46 枚 PathIcon 的 34,705 字符轮廓可收进一张有名字的表。 -->\r\n'
         f'            <Style x:Key="{KEY}" TargetType="PathIcon">\r\n'
         f'                <Setter Property="Data" Value="{data3}"/>\r\n'
         '            </Style>')
app_new = app.replace(anchor, style, 1)

# --- 3. MainWindow 第 3 枚改成 Style 形式 ---
new_el = '<PathIcon Style="{StaticResource ' + KEY + '}"/>'
main_new = main[:nav3.start()] + new_el + main[nav3.end():]
assert main_new.count(KEY) == 1, 'MainWindow 应恰有一处引用新 Style'
assert main_new.count('<PathIcon') == 8, '改完 MainWindow 仍应是 8 枚 PathIcon'
# 374 那枚必须还是内联形式（它就是对照臂）
assert data374 in main_new, '对照臂被误改：line 374 的内联 Data 必须保留'

write(APP, app_new)
write(MAIN, main_new)
print('已落盘：App.xaml +1 条 Style，MainWindow line 33 改 Style 形式，line 374 保持内联')
