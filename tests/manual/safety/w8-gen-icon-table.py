"""B0：把散在 7 个 XAML 里的 46 枚 PathIcon 轮廓（32 条不同串）收进两张有名字的表。

为什么用 Style 而不是 Geometry 资源：权威源（learn.microsoft.com PathIcon.Data）写明该属性
类型是 Geometry，mini-language 字符串只在 XAML【属性】上被转换；而 Setter.Value 是 object，
2026-10-02 的对照臂实测证明它能带 mini-language 并在运行时生效
（tests/output/realmachine-2026-10-01/style-arm.png：导航项=Style 臂、页眉=内联臂，两枚逐字相同）。

为什么两张表：LogViewer 是独立的 WinUI 应用、有自己的 App.xaml，看不见主程序的 Application.Resources。

判据（全部在脚本内断言，不靠事后目测）：
  · 46 枚引用全部改成 Style 引用，XAML 里内联 Data 归零；
  · 每条 Style 的 Data 与改前【逐字】相同（改前快照在内存里比对，轮廓不许被"顺手规范一下"）；
  · 两张表的条目数 = 各自项目内不同串的条数，且无孤儿条目（定义了没人用）；
  · BOM 与 CRLF 版式逐文件保持不变；
  · 可重跑：第二次运行不产生任何新改动（幂等）。
"""
import io, os, re, sys, glob, collections, hashlib

ROOT = r'C:\PLAN\TrueToneCap'
CP_MAP_DIR = os.path.join(ROOT, 'tests', 'output', 'realmachine-2026-10-01')

APP_FILES = ['MainWindow.xaml', 'SelectionOverlay.xaml', 'OcrPreviewWindow.xaml',
             'AnnotationWindow.xaml', 'ThirdPartyNoticesWindow.xaml', 'SilentCaptureToast.xaml']
LV_FILES = ['MainWindow.xaml']

USAGE_RE = re.compile(r'<PathIcon\b[^>]*?/>', re.S)
DATA_RE = re.compile(r'Data="([^"]+)"')
STYLE_REF_RE = re.compile(r'Style="\{StaticResource (TtcIcon\.[0-9A-F]+)\}"')


def read(p):
    return io.open(p, encoding='utf-8', newline='').read()


def write(p, s):
    # 读写都用 utf-8（非 utf-8-sig）⇒ 首字节 BOM 以 \ufeff 原样往返；newline='' ⇒ CRLF 不被翻译
    with io.open(p, 'w', encoding='utf-8', newline='') as f:
        f.write(s)


def load_codepoint_map():
    """从抽取产物反查 Data→码位。这些文件是当初 GDI+ 抽轮廓时的原始输出。"""
    m = {}
    for p in glob.glob(os.path.join(CP_MAP_DIR, 'glyph-paths*.txt')):
        for line in io.open(p, encoding='utf-8', errors='replace'):
            g = re.match(r'^([0-9A-F]{4,5}) DATA (.*)$', line.strip())
            if g:
                m.setdefault(g.group(2).strip(), set()).add(g.group(1))
    return m


def nearest_label(src, end):
    """取该图标后面最近的界面文字做语义注记，只作注释用、不参与判据。"""
    tail = src[end:end + 500]
    g = re.search(r'(?:Text|ToolTipService\.ToolTip|Content)="([^"]{1,24})"', tail)
    return g.group(1) if g else ''


def collect(project_dir, files, cpmap):
    """返回 (每个文件的原文列表, 该项目的 Data→(码位, 用法位置))"""
    texts, table = {}, collections.OrderedDict()
    for fn in files:
        p = os.path.join(project_dir, fn)
        src = read(p)
        texts[p] = src
        for m in USAGE_RE.finditer(src):
            tag = m.group(0)
            d = DATA_RE.search(tag)
            if not d:
                for s in STYLE_REF_RE.finditer(tag):        # 已是 Style 形式（探针那一枚）
                    e = table.setdefault(s.group(1).split('.')[1], {'locs': [], 'labels': []})
                    e['locs'].append('%s:%d' % (fn, src[:m.start()].count('\n') + 1))
                    e['labels'].append(nearest_label(src, m.end()))
                continue
            v = d.group(1)
            core = v[3:] if v.startswith('F0 ') else v
            cps = cpmap.get(core)
            assert cps, '反查不到码位，拒绝猜：%s:%d' % (fn, src[:m.start()].count('\n') + 1)
            key = sorted(cps)[0]
            e = table.setdefault(key, {'locs': [], 'labels': []})
            if 'data' not in e:                 # 该码位可能先以 Style 形式（探针那枚）被登记
                e['data'] = v
                e['alias'] = sorted(cps)
            else:
                assert e['data'] == v, '同一码位出现两种轮廓，表不自洽：%s' % key
            e['locs'].append('%s:%d' % (fn, src[:m.start()].count('\n') + 1))
            e['labels'].append(nearest_label(src, m.end()))
    for k, e in table.items():
        assert 'data' in e, '这条只有 Style 形式、没有内联 Data 可反查，表无法自证：%s' % k
    return texts, table


def render_table(table, header_note):
    L = ['<?xml version="1.0" encoding="utf-8"?>',
         '<!-- 本文件由 scripts/gen-icon-table.py 生成，请勿手改：改轮廓要改生成器或重跑。',
         '     码位是当初用 GDI+ 从系统符号字体抽轮廓的来源，不是运行时用到的字体名。',
         '     ' + header_note,
         '-->',
         '<ResourceDictionary',
         '    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
         '    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
         '']
    for key in sorted(table):
        e = table[key]
        lab = ' / '.join([x for x in dict.fromkeys(e.get('labels', [])) if x][:3]) or '（界面文字未紧邻）'
        al = (' 别名 ' + '/'.join(e['alias'])) if len(e.get('alias', [])) > 1 else ''
        L.append('    <!-- %s · %s%s · %d 处引用：%s -->' % (
            key, lab, al, len(e['locs']), ', '.join(e['locs'])))
        L.append('    <Style x:Key="TtcIcon.%s" TargetType="PathIcon">' % key)
        L.append('        <Setter Property="Data" Value="%s"/>' % e['data'])
        L.append('    </Style>')
        L.append('')
    L.append('</ResourceDictionary>')
    # 在库的 XAML 首字节都是 BOM（实测 App.xaml/MainWindow.xaml BOM=True），生成物要复刻这个版式
    return '\ufeff' + '\r\n'.join(L) + '\r\n'


def rewrite_usages(texts, table, cpmap):
    """把每枚 <PathIcon … Data="…" …/> 换成 <PathIcon … Style="{StaticResource TtcIcon.XX}"/>，
    其余属性原样保留；ToastActionIcon 那三枚把它的三个 setter 摊成内联属性（一个元素只能有一个 Style）。"""
    out = {}
    for p, src in texts.items():
        pieces, pos, n = [], 0, 0
        for m in USAGE_RE.finditer(src):
            tag = m.group(0)
            d = DATA_RE.search(tag)
            if not d:
                continue
            v = d.group(1)
            core = v[3:] if v.startswith('F0 ') else v
            key = sorted(cpmap[core])[0]
            assert table[key]['data'] == v
            new = tag[:d.start()] + 'Style="{StaticResource TtcIcon.%s}"' % key + tag[d.end():]
            if 'Style="{StaticResource ToastActionIcon}"' in new:
                new = new.replace(' Style="{StaticResource ToastActionIcon}"', '')
                new = new.replace('/>', ' Width="12" Height="12"'
                                   ' Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>')
            assert 'Data=' not in new, '还留着 Data：%s' % p
            pieces.append(src[pos:m.start()]); pieces.append(new); pos = m.end(); n += 1
        pieces.append(src[pos:])
        out[p] = ''.join(pieces), n
    return out


MERGE_ANCHOR = '<XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />'


def patch_app_xaml(path, drop_probe):
    """把 Icons.xaml 合进 Application.Resources；顺带撤掉 2026-10-02 那枚一次性探针 Style
    （它和表里的 TtcIcon.E164 同名，留着会在加载时撞键）。幂等：重跑不再改。"""
    src = read(path)
    assert src.count(MERGE_ANCHOR) == 1, '%s：合并锚点命中 %d 次（要求 1）⇒ 不猜' % (path, src.count(MERGE_ANCHOR))
    if 'Source="Icons.xaml"' not in src:
        src = src.replace(MERGE_ANCHOR, MERGE_ANCHOR + '\r\n                <ResourceDictionary Source="Icons.xaml" />', 1)
    if drop_probe:
        i = src.find('<!-- B0 可行性探针')
        if i != -1:
            j = src.find('</Style>', i)
            assert j != -1, '探针 Style 没有收尾'
            src = src[:src.rfind('\r\n', 0, i)] + src[j + len('</Style>'):]
        assert 'TtcIcon.' not in src, '探针撤不干净：App.xaml 里还有 TtcIcon 引用'
    write(path, src)
    return 'Source="Icons.xaml"' in src


def main():
    cpmap = load_codepoint_map()
    app_dir = os.path.join(ROOT, 'src', 'TrueToneCap.App')
    lv_dir = os.path.join(ROOT, 'src', 'TrueToneCap.LogViewer')

    app_texts, app_table = collect(app_dir, APP_FILES, cpmap)
    lv_texts, lv_table = collect(lv_dir, LV_FILES, cpmap)
    total = sum(len(e['locs']) for e in app_table.values()) + sum(len(e['locs']) for e in lv_table.values())
    print('主程序：%(k)d 条不同轮廓 / %(u)d 处引用' % {'k': len(app_table), 'u': sum(len(e['locs']) for e in app_table.values())})
    print('LogViewer：%(k)d 条不同轮廓 / %(u)d 处引用' % {'k': len(lv_table), 'u': sum(len(e['locs']) for e in lv_table.values())})
    assert total == 46, '应有 46 处引用，实得 %d' % total

    new_texts = rewrite_usages(dict(app_texts), app_table, cpmap)
    new_texts.update(rewrite_usages(dict(lv_texts), lv_table, cpmap))

    # 逐字保住轮廓：按【位置】比对改前改后每一枚解析出来的 Data，顺序敏感。
    # 上一版比的是排序后的多重集，探针那枚改前没有 Data、改后解析出一条 ⇒ 数量天然不等，是判据写错不是改坏。
    keymap = {('TtcIcon.' + k): e['data'] for k, e in list(app_table.items()) + list(lv_table.items())}

    def resolve(s):
        out = []
        for m in USAGE_RE.finditer(s):
            tag = m.group(0)
            d = DATA_RE.search(tag)
            if d:
                out.append(d.group(1))
                continue
            g = STYLE_REF_RE.findall(tag)
            assert len(g) == 1, '一枚 PathIcon 既无 Data 也无唯一 Style 引用：%r' % tag
            out.append(keymap[g[0]])
        return out

    for p, (s, n) in new_texts.items():
        orig = app_texts.get(p) or lv_texts.get(p)
        assert resolve(orig) == resolve(s), '轮廓逐字比对失败（位置级）：%s' % p

    for p, (s, n) in new_texts.items():
        write(p, s)
    write(os.path.join(app_dir, 'Icons.xaml'),
          render_table(app_table, '主程序（TrueToneCap.App）用，由 App.xaml 合入。'))
    write(os.path.join(lv_dir, 'Icons.xaml'),
          render_table(lv_table, 'LogViewer 独立进程用，由它自己的 App.xaml 合入。'))

    patch_app_xaml(os.path.join(app_dir, 'App.xaml'), drop_probe=True)
    patch_app_xaml(os.path.join(lv_dir, 'App.xaml'), drop_probe=False)

    # 收尾自查：全仓不该再有内联 Data，且每个 Style 引用都能在对应表里找到
    for p, (s, n) in list(new_texts.items()):
        assert 'Data="' not in s, '仍残留内联 Data：%s' % p
        refs = [g for m in USAGE_RE.finditer(s) for g in STYLE_REF_RE.findall(m.group(0))]
        assert len(refs) == len(USAGE_RE.findall(s)), '%s：引用数与 PathIcon 数不等' % p
    print('已写：7 个 XAML + 2 张 Icons.xaml + 2 个 App.xaml 合入')


if __name__ == '__main__':
    main()
