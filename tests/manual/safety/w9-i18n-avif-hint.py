"""把 AVIF 的 Hint 从"三段字面量拼接"合成一条 —— 修死键卡的误报根因。

死键卡的形状是"表里有、界面无一处使用 ⇒ 红"，它靠【在源码里找这条字面量】来判"用没用"。
AVIF 那条 Hint 写成
    Hint: "✅ 先进格式，…。" + "NVENC 可硬件加速（…），…。" + "位深与色度…已隐藏。"
运行时值与我的表键【逐字相同】，但完整串在源码里根本不存在 ⇒ 卡找不到 ⇒ 报成死键。

两种选择里选了这一种：
  A. 放宽卡（比如"允许拼接"）—— 那是为了绿灯改判据，本仓明令禁止；
  B. 把拼接合成一条字面量 —— 运行时行为逐字不变，只是让键在源码里可见、可匹配。
选 B。顺带：三段拼接本来就是当初为了绕开行宽才拆的，注释已经移到上一行，合成一条不影响可读性。

自检：合成前后 Hint 的运行时值必须【逐字相等】——脚本里直接算给你看，不靠目测。
"""
import io

P = 'src/TrueToneCap.App/MainWindow.xaml.cs'
raw = io.open(P, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in raw else '\n'
s = raw.replace('\r\n', '\n')

parts = [
    '✅ 先进格式，支持 HDR（PQ）与广色域 SDR（Rec.2020 原色 + sRGB 传输，colr(nclx) 与 ICC 双标记）。',
    'NVENC 可硬件加速（AV1 仅 4:2:0），软件编码器支持 4:4:4 与 12-bit。',
    '位深与色度按所选编码器实际能力生效，不支持的组合已隐藏。',
]
old = ('            Hint: "' + parts[0] + '"\n'
       '                + "' + parts[1] + '"\n'
       '                + "' + parts[2] + '"),')
assert s.count(old) == 1, f'拼接锚点命中 {s.count(old)} 次'

joined = ''.join(parts)
new = '            Hint: "' + joined + '"),'

# 运行时值逐字相等的自检：合成串必须就是三段拼接的结果。
# （上一版这里还有一条"去空格比较锚点形状"的检查，那是【我写废的检查】——
#  old 里三段之间有换行与缩进，去掉空格也去不掉换行，所以恒不成立；
#  真正的锚点校验是上面那句 assert s.count(old) == 1，它已经过了。）
assert joined == parts[0] + parts[1] + parts[2], '合成串与拼接结果不一致 ⇒ 会改变运行时文本'

s = s.replace(old, new, 1)
io.open(P, 'w', encoding='utf-8', newline='').write(s.replace('\n', '\r\n') if nl == '\r\n' else s)
print('AVIF Hint 已合成一条字面量；运行时值逐字不变（脚本内已 assert）')
print('表里那条键 =', joined[:40] + '…')
