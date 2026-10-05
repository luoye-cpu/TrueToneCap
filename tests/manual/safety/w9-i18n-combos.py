"""英文界面补完 · 第三批：7 个下拉共 35 个中文项。

为什么这批只能显式点名：ComboBoxItem 不在 MainWindow.Localization.cs 的 SweepSurface 表里，
而下拉项在收起时根本不在可视树里（要 Expand 才实化）⇒ 就算把 ComboBoxItem 加进扫描面，
扫描也扫不到它们。所以只有 SetComboItemText(下拉, Tag, 文案) 这一条路，这也是本仓
12 个已接线下拉一直在用的机制。

关键做法：**键从 XAML 里抽，不由我抄**。
  理由：SetComboItemText 在中文模式下会把 Content 写成我传进去的那个串（Tr 未命中即原样返回）。
  我抄的串与 XAML 字面量差一个空格、差一个变体选择符，中文模式的界面文字就被悄悄改了。
  所以本脚本解析 XAML 拿到【逐字】的 (Tag, Content)，中文串去查一张我写的译文库；
  库里没有的中文项 ⇒ 直接报错退出，不许静默漏一项。

收尾必须同时改 verify.ps1 的 --ceiling（当前 7 35 ⇒ 0 0），否则覆盖面判据就在放行旧读数。
"""
import io
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
XAML = ROOT / 'src/TrueToneCap.App/MainWindow.xaml'
LOC = ROOT / 'src/TrueToneCap.App/MainWindow.Localization.cs'
LM = ROOT / 'src/TrueToneCap.App/Services/LocaleManager.cs'
CJK = re.compile(r'[\u4e00-\u9fff]')

TARGETS = ['BorderColorCbo', 'OverlayColorCbo', 'LogCategoryCbo', 'LogFilterCbo',
           'LlmProviderCbo', 'TranslationModeCbo', 'AvifCodecCbo']

# 译文库：键必须与 XAML 字面量逐字相同（下面会拿 XAML 抽出来的串反查这张库，
# 对不上就停）。品牌名用各家自己的英文名，不自己发明罗马字。
EN = {
    '天蓝': 'Sky Blue', '翠绿': 'Emerald', '橙红': 'Orange Red', '粉红': 'Pink',
    '金黄': 'Gold', '紫色': 'Purple', '纯白': 'Pure White', '青色': 'Cyan',

    '灰色半透明': 'Translucent Grey', '纯黑半透明': 'Translucent Black',
    '深紫半透明': 'Translucent Deep Purple', '深海蓝': 'Deep Sea Blue',
    '深灰半透明': 'Translucent Dark Grey', '白色半透明': 'Translucent White',
    '深绿半透明': 'Translucent Dark Green',

    '全部': 'All', '⚙️ 系统': '⚙️ System', '📷 捕获': '📷 Capture',
    '💾 编码': '💾 Encoding', '🖥️ 界面': '🖥️ Interface', '🌐 网络': '🌐 Network',
    '调试': 'Debug', '信息': 'Info', '警告': 'Warning', '错误': 'Error',

    '智谱 GLM': 'Zhipu GLM', '硅基流动': 'SiliconFlow',
    '阿里云百炼': 'Alibaba Cloud Bailian', '自定义': 'Custom',

    '免费翻译': 'Free Translation', 'LLM 翻译': 'LLM Translation',
    '视觉翻译': 'Vision Translation',

    '自动': 'Auto', 'Intel QSV / 系统 MFT': 'Intel QSV / System MFT',
}


def read(p):
    return p.read_text(encoding='utf-8-sig')


def combo_items(x):
    """从 XAML 抽 {下拉名: [(Tag, 逐字 Content), …]}。项序保持 XAML 原序。"""
    out = {}
    for name in TARGETS:
        m = re.search(r'<ComboBox[\s>][^<]*?x:Name="' + name + '"', x, re.S)
        if not m:
            sys.exit(f'❌ XAML 里找不到 {name} ⇒ 本轮没量，不是量到 0')
        end = x.find('</ComboBox>', m.end())
        body = x[m.end():end]
        rows = []
        for attrs, inner in re.findall(r'<ComboBoxItem\b([^>]*)>(.*?)</ComboBoxItem>', body, re.S):
            tag = re.search(r'Tag="([^"]*)"', attrs)
            if not tag:
                sys.exit(f'❌ {name} 有一项没有 Tag ⇒ SetComboItemText 按 Tag 找，接不上')
            # XAML 会把元素内容的首尾空白裁掉 ⇒ 运行时 Content 就是 strip 后的那一串。
            # 拿它当键，中文模式下 Tr 原样返回、写回去的就是同一个串，界面文字逐字不变。
            content = inner.strip()
            if '\n' in content or '\r' in content:
                sys.exit(f'❌ {name}[{tag.group(1)}] 内容里有换行 ⇒ strip 不等于 XAML 的裁剪结果，'
                         f'键会对不上运行时 Content')
            rows.append((tag.group(1), content))
        out[name] = rows
    return out


x = read(XAML)
items = combo_items(x)

# ── 反查译文库：XAML 抽出来的每一条中文项必须有译文；非中文项不动 ──
todo, missing = [], []
for name, rows in items.items():
    for tag, content in rows:
        if not CJK.search(content):
            continue
        if content not in EN:
            missing.append(f'{name}[{tag}]={content!r}')
            continue
        todo.append((name, tag, content, EN[content]))
if missing:
    sys.exit('❌ 有中文项没在译文库里（不许静默漏项）：\n   ' + '\n   '.join(missing))

extra = [k for k in EN if not any(k == c for _, cs in items.items() for _, c in cs)]
if extra:
    sys.exit('❌ 译文库里有 XAML 已不再有的键（会变成表里的死键）：' + ' | '.join(map(repr, extra)))

print(f'XAML 抽出：{sum(len(r) for r in items.values())} 项，其中中文 {len(todo)} 项，'
      f'覆盖 {len(set(n for n, *_ in todo))} 个下拉')

# ── 1) 进表（键值都是 XAML 逐字串）──
pairs = sorted({(zh, en) for _, _, zh, en in todo}, key=lambda t: (-len(t[0]), t[0]))
lm, = (read(LM),)
i = lm.index('ZhToEn =')
close = lm.index('\n        };', i)
add = [(z, e) for z, e in pairs if f'["{z}"]' not in lm[i:close]]
for z, _ in add:
    assert '"' not in z and '\\' not in z, f'键里有引号/反斜杠，判据解析式处理不了：{z!r}'
rows_txt = ['        // ── 2026-10-03 英文界面补完 · 第三批：7 个下拉的 35 个中文项（键取自 XAML 逐字）──']
rows_txt += [f'            ["{z}"] = "{e}",' for z, e in add]
lm_new = lm[:close] + '\n' + '\n'.join(rows_txt) + lm[close:]
LM.write_text(lm_new, encoding='utf-8', newline='')
print(f'[1] 进表 {len(add)} 条（表里已有 {len(pairs) - len(add)} 条）')

# ── 2) 接线：每个下拉一段 SetComboItemText ──
loc = read(LOC)
blocks = []
for name in TARGETS:
    lines = [f'        // {name}：项文字是 XAML 字面量，收起时不在可视树里 ⇒ 只能按 Tag 逐个点名。']
    for tag, content, en in [(t, c, EN[c]) for t, c in items[name] if CJK.search(c)]:
        lines.append(f'        SetComboItemText({name}, "{tag}", LocaleManager.Tr("{content}"));')
    blocks.append('\n'.join(lines))
insert = ('\n        // ── 2026-10-03 英文界面补完 · 第三批：过去一次都没接线的 7 个下拉 ──\n'
          + '\n\n'.join(blocks) + '\n\n')
anchor = ('        // ── 色域映射卡片（含本文件刚写下的 Localization 文案，必须最后刷一次覆盖）──')
if '英文界面补完 · 第三批' not in loc:
    assert loc.count(anchor) == 1, f'插入锚点命中 {loc.count(anchor)} 次'
    loc = loc.replace(anchor, insert + anchor, 1)
    LOC.write_text(loc, encoding='utf-8', newline='')
    print(f'[2] 接线 {sum(len(b.splitlines()) - 1 for b in blocks)} 条 SetComboItemText')
else:
    print('[2] 第三批已接过 ⇒ 跳过（幂等）')
print(f'RESOURCE combos={len(TARGETS)} zh_items={len(todo)} distinct_keys={len(pairs)} newly={len(add)}')
