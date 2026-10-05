"""量"某个设置键到底有没有真消费者"，给兜底卡定判据形状。

上一版探针的 bug 值得记：目录过滤写成
    if os.sep + 'obj' + dp or ...
第一个操作数是【非空字符串】⇒ 恒真 ⇒ 每个目录都 continue ⇒ 一个文件都没读到，
于是 OutputPath / FormatIndex 这些明明活着也被报成"0 消费者"。
靠"正例必须命中"这一条控制才发现——负断言之前的老规矩。

消费者定义（行级，AppSettingsData.cs 之外）：
  含 \\bKEY\\b，且不是"写进设置"（xxx.KEY =），且不是"回填给控件"（= xxx.KEY），且不是整行注释。
"""
import io
import os
import re
import sys

MODEL = 'AppSettingsData.cs'
WRITE = 'WRITE'
ECHO = 'ECHO'


def collect_files():
    out = []
    for root in ('src', 'scripts'):
        for dp, dn, fn in os.walk(root):
            parts = dp.replace('\\', '/').split('/')
            if 'obj' in parts or 'bin' in parts:
                continue
            for f in fn:
                if f.endswith('.cs'):
                    out.append(os.path.join(dp, f))
    return out


def classify(key, files):
    """返回 (消费者行, 写/回填行) 两组，便于我人工核判据有没有误伤。"""
    consumers, plumbing = [], []
    w = re.compile(r'(?:_settings|settings|cfg|snapshot|_s|s)\s*\.\s*' + re.escape(key) + r'\s*[=]')
    e = re.compile(r'[=]\s*(?:_settings|settings|cfg|snapshot|_s|s)\s*\.\s*' + re.escape(key) + r'\b')
    tok = re.compile(r'\b' + re.escape(key) + r'\b')
    for path in files:
        if os.path.basename(path) == MODEL:
            continue
        text = io.open(path, encoding='utf-8-sig', errors='replace').read()
        for i, line in enumerate(text.splitlines(), 1):
            t = line.strip()
            if t.startswith('//') or t.startswith('///') or t.startswith('*'):
                continue
            if not tok.search(t):
                continue
            if w.search(t) or e.search(t):
                plumbing.append(f'{path}:{i}: {t[:92]}')
            else:
                consumers.append(f'{path}:{i}: {t[:92]}')
    return consumers, plumbing


def main():
    files = collect_files()
    print(f'扫到 .cs 文件 {len(files)} 个（obj/bin 已排除）')
    if len(files) < 40:
        print('★ 文件数少得可疑 ⇒ 目录过滤又坏了，下面的数全部不作数')
        return 2
    for key in sys.argv[1:]:
        c, p = classify(key, files)
        tag = '有消费者' if c else '零消费者'
        print(f'{key:26s} {tag:8s}  消费者 {len(c):2d} 处 / 写与回填 {len(p):2d} 处')
        for h in c[:2]:
            print('        C', h)
    return 0


if __name__ == '__main__':
    sys.exit(main())
