"""扫出一个 .cs 文件里所有【含中文的字符串字面量】，并可选地包上 LocaleManager.Tr。

为什么要单独有这么个工具：本仓多次要"把 .cs 里拼给界面的中文接进对照表"，
而每次都用一条正则去捞，捞多捞少没人知道（10-03 就有一次正则漏掉整块 switch 臂，
量出来 9 条、实际 20 条，差点按 9 条去翻译）。这里改成逐字符扫描：
先跳过 // 与 /* */ 注释，再配对双引号，反斜杠转义按两字符吞掉，遇裸换行即失配。
这样"扫到的条数"是一个可复核的数，而不是一个正则的副产品。

用法：
  py -3 tests/manual/safety/scan_cs_cjk.py <文件>            # 只列
  py -3 tests/manual/safety/scan_cs_cjk.py <文件> --wrap      # 就地包 Tr()（幂等）
"""
import io
import re
import sys

CJK = re.compile(r'[\u3000-\u30ff\u4e00-\u9fff\uff00-\uffef]')
BS = chr(92)
DQ = chr(34)


def scan(text):
    """返回 [(起始下标, 内容)]，内容是引号之间的原文（含转义序列本身）。"""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find(chr(10), i)
            i = n if j < 0 else j
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i)
            i = n if j < 0 else j + 2
            continue
        if c == DQ:
            j = i + 1
            buf = []
            closed = False
            while j < n:
                d = text[j]
                if d == BS:
                    buf.append(text[j:j + 2])
                    j += 2
                    continue
                if d == chr(10):
                    break                      # 裸换行 ⇒ 不是合法字面量，失配
                if d == DQ:
                    closed = True
                    break
                buf.append(d)
                j += 1
            if closed:
                s = ''.join(buf)
                if CJK.search(s):
                    out.append((i, s))
                i = j + 1
                continue
        i += 1
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    wrap = '--wrap' in sys.argv
    raw = io.open(path, encoding='utf-8', newline='').read()
    nl = chr(13) + chr(10) if chr(13) in raw else chr(10)
    t = raw.replace(chr(13) + chr(10), chr(10))
    hits = scan(t)
    uniq = sorted({s for _, s in hits})
    print(f'{path}: 含中文字面量 {len(hits)} 处 / 去重 {len(uniq)} 条')
    for u in uniq:
        print('   ', repr(u))
    if not wrap:
        return 0
    # 从后往前替换，避免下标漂移；已经包了 Tr( 的跳过 ⇒ 幂等
    changed = 0
    for pos, s in sorted(hits, key=lambda p: -p[0]):
        seg_end = pos + 1 + len(s) + 1
        before = t[max(0, pos - 20):pos]
        if before.endswith('Tr(') or before.endswith('Trf('):
            continue
        if '$' == t[pos - 1:pos]:
            continue          # 内插串交给 Trf 那条路，这里不动
        t = t[:pos] + 'LocaleManager.Tr(' + t[pos:seg_end] + ')' + t[seg_end:]
        changed += 1
    io.open(path, 'w', encoding='utf-8', newline='').write(t.replace(chr(10), nl))
    print(f'已包 Tr() {changed} 处（跳过已包的与内插串）；剩余未包 {len(uniq) - changed}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
