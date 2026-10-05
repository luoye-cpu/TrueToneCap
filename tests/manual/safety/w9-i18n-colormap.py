"""R2：把「色域映射」那张卡的中文接进对照表。

做法与理由：
  · 平串一律就地包 LocaleManager.Tr(...)（scan_cs_cjk.py 逐字符扫，不靠正则捞——
    上一版用正则只捞到 9 条，实际 25 条，差点按 9 条去翻译）。
  · 带 {expr} 的内插串【不在这里自动改】：它们要变成 Trf(模板, 值…) 形态，
    模板里占位符编号必须与译文里的编号一致，这是要人核对的，交给第二步手改。
    脚本只把这 7 条列出来并检查它们是否已经被改过，没改就报错退出——
    不许"跑完看起来全绿、其实内插串还是中文"。
  · 中文模式逐字不变：Tr 未命中即原样返回；命中时返回的就是这条中文本身。
"""
import io
import re
import sys

sys.path.insert(0, 'tests/manual/safety')
from scan_cs_cjk import scan                                    # noqa: E402

TARGET = 'src/TrueToneCap.App/MainWindow.ColorManagement.cs'
LM = 'src/TrueToneCap.App/Services/LocaleManager.cs'

# 25 条的译文。平串的键就是源码里那一串；内插串的键是【改后的模板】。
PAIRS = {
    ' —— 面板支持 HDR，系统未开启':
        ' — panel supports HDR, not enabled in the system',
    '8-bit 源 → ICC/矩阵搬到目标 → SDR 输出。':
        '8-bit source -> ICC/matrix to the target -> SDR output.',
    'EDID 未读到原色（不猜）':
        'primaries not read from EDID (not guessed)',
    'WGC Float16 → 色域矩阵 → PQ ST.2084。':
        'WGC Float16 -> gamut matrix -> PQ ST.2084.',
    'WGC Float16 → 分段 Reinhard 出 SDR 底图，另算一张增益图；HDR 在增益图里，不在容器声明里。':
        'WGC Float16 -> piecewise Reinhard into the SDR base image, plus a separate gain map; '
        'the HDR lives in the gain map, not in the container declaration.',
    'WGC Float16 捕获完整广色域 → 3×3 矩阵搬到目标 → 分段 Reinhard → SDR 输出。':
        'WGC Float16 captures the full wide gamut -> 3x3 matrix to the target -> '
        'piecewise Reinhard -> SDR output.',
    '→ HDR 直通（容器写 PQ）': '-> HDR passthrough (container writes PQ)',
    '→ 增益图（SDR 底图 + gain map）': '-> gain map (SDR base + gain map)',
    '→ 直通（同色域）': '-> passthrough (same gamut)',
    '→ 色域映射到': '-> gamut mapped to',
    '→ 色调映射为 SDR（sRGB 容器装不下 PQ）':
        '-> tone-mapped to SDR (an sRGB container cannot carry PQ)',
    '→ 色调映射为 SDR（屏幕证据不授权 HDR 声称）':
        '-> tone-mapped to SDR (per-display evidence does not authorise an HDR claim)',
    '→ 色调映射为 SDR（此格式没有 HDR 通道）':
        '-> tone-mapped to SDR (this format has no HDR channel)',
    '本次按 SDR 色调映射：HDR 开关仍按你的设置保留，只是这块屏的逐屏证据不足以声明 PQ。':
        'Tone-mapped to SDR this run: the HDR switch is kept as you set it; '
        'it is only this panel\'s per-display evidence that is not enough to declare PQ.',
    '本次按 SDR 色调映射：目标被解析/收口成 Rec.709，而 PQ 只能与 Rec.2020 原色一起声明。':
        'Tone-mapped to SDR this run: the target resolved/narrowed to Rec.709, '
        'and PQ can only be declared together with Rec.2020 primaries.',
    '本次按 SDR 色调映射：这个格式的容器没有 HDR 通道（HDR 开关会落空，产物是 8-bit SDR）。':
        'Tone-mapped to SDR this run: this format\'s container has no HDR channel '
        '(the HDR switch has no effect; the output is 8-bit SDR).',
    '源与目标同一套原色 ⇒ 矩阵是恒等变换，本次不跑搬色。':
        'Source and target share the same primaries => the matrix is an identity transform; '
        'no colour transfer runs this time.',
    '读不到被截屏的逐屏证据 ⇒ 不授权广色域/HDR 声称（仍放行 Float16 源）':
        'per-display evidence of the captured screen unavailable '
        '=> no wide-gamut/HDR claim authorised (Float16 sources still pass through)',
    # ── 内插串改完之后的模板（占位符编号必须与译文一致）──
    'HDR（{0}，PQ，面板原生 {1}）': 'HDR ({0}, PQ, panel-native {1})',
    'SDR sRGB（{0}）': 'SDR sRGB ({0})',
    'SDR 广色域（{0}，{1}，ACM）': 'SDR wide gamut ({0}, {1}, ACM)',
    '{0}（底图色域）': '{0} (base image gamut)',
    '⚠ HDR 声称已被收口 ⇒ {0}': '⚠ The HDR claim was narrowed => {0}',
    '⚠ 广色域声明已被逐屏证据收口为 Rec.709 ⇒ {0}':
        '⚠ The wide-gamut claim was narrowed to Rec.709 by per-display evidence => {0}',
    '逐屏证据不足（{0}）⇒ 不授权广色域/HDR 声称':
        'Insufficient per-display evidence ({0}) => no wide-gamut/HDR claim authorised',
}


def main():
    raw = io.open(TARGET, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in raw else '\n'
    t = raw.replace('\r\n', '\n')

    hits = scan(t)
    plain = [(p, s) for p, s in hits if '{' not in s]
    interp = sorted({s for _, s in hits if '{' in s})

    # 从后往前包，下标不漂；已包过的跳过 ⇒ 幂等
    changed = 0
    for pos, s in sorted(plain, key=lambda x: -x[0]):
        end = pos + 1 + len(s) + 1
        if t[max(0, pos - 3):pos] == 'Tr(' or t[max(0, pos - 4):pos] == 'Trf(':
            continue
        if t[pos - 1:pos] == '$':
            continue
        t = t[:pos] + 'LocaleManager.Tr(' + t[pos:end] + ')' + t[end:]
        changed += 1
    io.open(TARGET, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
    print(f'[1] 平串包 Tr() {changed} 处（平串共 {len(plain)} 处）')

    still = sorted({s for _, s in scan(t) if '{' in s})
    if still:
        print('[2] 内插串还没改成 Trf 形态，本脚本不动它们；这一轮先报出来，不静默通过：')
        for s in still:
            print('     ', repr(s))
    else:
        print('[2] 内插串已全部是 Trf(模板, …) 形态')

    # 进表：只加源码里现在真的出现过的键（防止写出对不上号的死键）
    body = t
    lm = io.open(LM, encoding='utf-8', newline='').read()
    i = lm.index('ZhToEn =')
    close = lm.index('\n        };', i)
    window = lm[i:close]
    add = []
    for zh, en in PAIRS.items():
        if f'["{zh}"]' in window:
            continue
        if f'"{zh}"' not in body:
            continue          # 源码里还没有这条键（内插串尚未改）⇒ 现在进表就是死键
        add.append((zh, en))
    rows = ['        // ── 2026-10-03 英文界面补完 · 第四批：色域映射卡的运行时拼串（R2）──']
    rows += [f'            ["{z}"] = "{e}",' for z, e in add]
    if add:
        lm = lm[:close] + '\n' + '\n'.join(rows) + lm[close:]
        io.open(LM, 'w', encoding='utf-8', newline='').write(lm)
    print(f'[3] 进表 {len(add)} 条；表里已有 {len(PAIRS) - len(add) - 0} 条待内插串改完再补')
    missing = [z for z in PAIRS if f'"{z}"' in body and f'["{z}"]' not in lm]
    if missing:
        print('❌ 源码里有、表里没有（会成漏翻）：')
        for m in missing:
            print('   ', repr(m))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
