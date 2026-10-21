# -*- coding: utf-8 -*-
"""把"下拉项本地化覆盖面"这把尺子接成门禁判据：加 --ceiling 模式。

原脚本只报数、不判；所以 F6（09-27 记的 N1）一直是散文里的待办，谁都能把它当新发现重报。
加一个天花板：unhooked / unhooked_items 只许变小、不许变大。
这样"这一轮没做完 F6"是明确记账，而"新加的下拉又漏了本地化"会当场红。

天花板数值要和门禁地板一样维护：补完一批就把它调小，调大必须在同一次改动里说明理由。

用法：
  py -3 tests/manual/combo_locale_coverage_measure.py                # 只报数
  py -3 tests/manual/combo_locale_coverage_measure.py --ceiling 7 35 # 当判据跑
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import re
import sys
from pathlib import Path

# 路径按【脚本自身位置】解析，不靠调用方的 cwd —— 门禁从任意目录起都会跑它，
# 用相对路径会在"cwd 不是仓库根"时静默报"找不到源文件"，而那句返回 1 会被读成判据红。
ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "src/TrueToneCap.App/MainWindow.xaml"
LOC = ROOT / "src/TrueToneCap.App/MainWindow.Localization.cs"
CJK = re.compile(r"[\u4e00-\u9fff]")


def read(p: Path) -> str:
    # utf-8-sig：XAML/cs 带 BOM，按 utf-8 读会把首行当成带 \ufeff 的键
    return p.read_text(encoding="utf-8-sig")


def combo_blocks(x: str):
    """产出 (下拉名, 该下拉的 XAML 块)。块尾取【首个 </ComboBox> 与下一个 <ComboBox 之中较早者】。

    为什么不能只找 </ComboBox>：本仓有一份自闭合的 <ComboBox …/>（FormatCbo，项由
    RebuildFormatCombo 从 ItemsSource 生成，XAML 里没有 ComboBoxItem）。旧写法在它身上
    find("</ComboBox>") 会越过它、捞到【下一个】下拉的闭合标签 ⇒ 把别人的项算给它，
    那几项被数两遍。10-03 实测就是这样把 18 个下拉 / 70 个中文项报成 19 / 73。
    块尾同时卡在下一个开标签上，自闭合那一份自然切出空块（它确实没有 XAML 项）。
    """
    for m in re.finditer(r'<ComboBox[\s>][^<]*?x:Name="(\w+)"', x, re.S):
        close = x.find("</ComboBox>", m.end())
        # 必须带 (?![A-Za-z])：<ComboBoxItem 也是 <ComboBox 打头，裸 find 会停在
        # 【块内第一项】上 ⇒ 每个块都切成空 ⇒ 全文 131 项、19 个下拉一起报成 0。
        # 10-03 加"块尾卡在下一个开标签"这一笔时就是这么把尺子改成恒零的。
        nxt_m = re.search(r'<ComboBox(?![A-Za-z])', x[m.end():])
        nxt = m.end() + nxt_m.start() if nxt_m else -1
        if close == -1 and nxt == -1:
            continue
        end = nxt if close == -1 else (close if nxt == -1 or close < nxt else nxt)
        yield m.group(1), m.end(), end


def measure():
    """返回 (named, counted_items, missed, missed_items, total_items)。文件级对照数不过就抛异常。"""
    x = read(XAML)
    loc = read(LOC)

    total_items = len(re.findall(r"<ComboBoxItem\b", x))          # 文件级独立对照数
    open_tags = len(re.findall(r"<ComboBox[\s>]", x))
    close_tags = x.count("</ComboBox>")
    self_closing = len(re.findall(r"<ComboBox\b[^>]*/>", x, re.S))
    print(f"对照数：<ComboBox> 开 {open_tags}（其中自闭合 {self_closing}）/ 闭 {close_tags}；"
          f"<ComboBoxItem> 全文 {total_items}")
    # 开 = 闭 + 自闭合 才是"配平"。旧写法只比 开==闭，于是那一份合法的自闭合
    # 每次都把"分块口径不可信"打在结论上面——而真正该拦的是嵌套/漏尾，不是它。
    if open_tags != close_tags + self_closing:
        print(f"⚠ 开 {open_tags} ≠ 闭 {close_tags} + 自闭合 {self_closing} ⇒ 有嵌套或漏尾，分块口径不可信")

    blocks = list(combo_blocks(x))
    # 结构哨兵：块与块不许重叠。重叠就是"一个项被两个下拉认领"，上面那种双计只有这条能抓。
    overlap = [(a[0], b[0]) for a, b in zip(blocks, blocks[1:]) if a[2] > b[1]]
    if overlap:
        raise AssertionError(f"块区间重叠 ⇒ 同一批项被重复归属：{overlap}")

    named = []
    for name, start, end in blocks:
        items = re.findall(r"<ComboBoxItem\b[^>]*>(.*?)</ComboBoxItem>", x[start:end], re.S)
        zh = [t.strip() for t in items if CJK.search(t)]
        if zh:
            named.append((name, len(items), len(zh), name in loc))

    counted_items = sum(b[2] for b in named)
    claimed = sum(len(re.findall(r"<ComboBoxItem\b", x[s:e])) for _, s, e in blocks)
    if claimed > total_items:
        # 这就是上一轮 1318 > 116 那次事故的哨兵：块尾吞多了会直接暴露
        raise AssertionError(f"块内 ComboBoxItem 合计 {claimed} > 全文 {total_items} ⇒ 解析器失步")
    if counted_items > total_items:
        raise AssertionError(f"选中中文项 {counted_items} > 全文 ComboBoxItem {total_items} ⇒ 解析器失步")
    # 反向哨兵：上面两条只拦"多算"。10-03 实测把块尾改成"卡在下一个开标签"之后，
    # <ComboBoxItem 也被当成开标签 ⇒ 每个块都空 ⇒ named=0，而 --ceiling 0 0 照样绿。
    # "0 个漏"和"尺子根本没看见东西"在这一层是同一个读数，所以必须分开：
    # 全文有 ComboBoxItem 而一块都没切出来，就是尺子瞎了，判"没量到"而不是"量到 0"。
    if total_items > 0 and claimed == 0:
        raise AssertionError(f"全文 {total_items} 个 ComboBoxItem，块里一个都没认领 ⇒ 分块失步，本轮不给结论")

    # ── 独立裁判：换一份【不是我写的】解析器再数一遍 ──
    # 上面三条哨兵全是"我的正则自己跟自己对账"，同源的两种数法会一起错（10-03 那次
    # named=0 恒零就是：块切空了，"多算"两条自然都不触发）。这里用 ElementTree 真解析
    # XAML，按 XML 树数"每个 ComboBox 的直接子 ComboBoxItem"，与正则分块的结果必须逐下拉相等。
    ref = etree_per_combo()
    if ref is None:
        print("⚠ 独立裁判（ElementTree）没跑成 ⇒ 本轮只有同源正则一票，覆盖面按'未判'看")
    else:
        mine = {n: len(re.findall(r"<ComboBoxItem\b", x[s:e])) for n, s, e in blocks}
        disagree = {k: (mine.get(k, '<没切到>'), v) for k, v in ref.items() if mine.get(k) != v}
        if disagree:
            raise AssertionError(f"与 ElementTree 逐下拉对不上 ⇒ 分块口径不可信：{disagree}")
        print(f"独立裁判：ElementTree 数到 {len(ref)} 个具名下拉 / "
              f"{sum(ref.values())} 个直接子项，与分块逐下拉一致 ✓")

    missed = [b for b in named if not b[3]]
    return named, counted_items, missed, sum(b[2] for b in missed), total_items


def etree_per_combo():
    """用 ElementTree 独立数：{具名下拉: 其直接子 ComboBoxItem 数}。解析不了返回 None。"""
    try:
        import xml.etree.ElementTree as ET
        root = ET.parse(XAML).getroot()
        xname = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
        out = {}
        for el in root.iter():
            if not el.tag.endswith("}ComboBox"):
                continue
            nm = el.get(xname)
            if nm:
                out[nm] = sum(1 for c in el if c.tag.endswith("}ComboBoxItem"))
        return out
    except Exception as ex:                       # 裁判缺席要看得见，不许静默当"没有差异"
        print(f"   （ElementTree 失败：{type(ex).__name__}: {ex}）")
        return None


def main() -> int:
    if not XAML.exists() or not LOC.exists():
        print("❌ 找不到源文件 ⇒ 本轮没量，不是量到 0")
        return 1

    argv = sys.argv[1:]
    try:
        named, counted_items, missed, missed_items, total_items = measure()
    except AssertionError as ex:
        print(f"❌ {ex} ⇒ 本轮不给结论")
        return 1

    print(f"含中文项的具名下拉：{len(named)} 个，中文项合计 {counted_items}")
    print(f"本地化代码点名过：{len(named) - len(missed)} 个；一次都没提：{len(missed)} 个")
    for b in sorted(missed, key=lambda t: -t[2]):
        print(f"   漏 {b[0]}  项 {b[1]}  其中中文 {b[2]}")

    print(f"RESULT named={len(named)} zh_items={counted_items} unhooked={len(missed)} "
          f"unhooked_items={missed_items} file_total_items={total_items}")

    if "--ceiling" in argv:
        i = argv.index("--ceiling")
        if i + 2 >= len(argv):
            print("❌ --ceiling 需要两个数（下拉个数上限 中文项上限）⇒ 判据没跑，不作数")
            return 1
        cap_n, cap_items = int(argv[i + 1]), int(argv[i + 2])
        bad = []
        if len(missed) > cap_n:
            bad.append(f"漏点名的下拉 {len(missed)} > 天花板 {cap_n}")
        if missed_items > cap_items:
            bad.append(f"未接中文项 {missed_items} > 天花板 {cap_items}")
        if bad:
            print("❌ 下拉项本地化覆盖面变差：" + "；".join(bad))
            print("   ⇒ 新加的下拉必须同一次改动里接上 SetComboItemText，或把天花板调小并说明理由")
            return 1
        if len(missed) < cap_n or missed_items < cap_items:
            print(f"✅ 覆盖面比天花板更好（{len(missed)}/{missed_items} vs {cap_n}/{cap_items}）"
                  f"⇒ 请把 --ceiling 调小，别把余量留给下一次退化")
        else:
            print(f"✅ 覆盖面 == 天花板（{cap_n} 个下拉 / {cap_items} 项）")
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
