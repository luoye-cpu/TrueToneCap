#!/usr/bin/env python3
# 两条预览路径的色彩差（报告 §24）。输入是产品自己算出来的数，对照曲线这里独立实现
# —— 拿同一个实现比自己是恒等式，量不出任何东西。
#
#   路线 A（GPU）  ：PixelOps.BgraToScrgbLinearFast → R16G16B16A16_Float 交换链 → DWM
#   路线 B（CPU 兜底）：原始 BGRA8 进 WriteableBitmap，按 sRGB 解 → DWM
#
# 总差拆成两块，别混成一个数：
#   ① Fast 近似      ：产品的 fast_linear  vs  IEC 61966-2-1 分段曲线
#   ② Half16 量化    ：exact → binary16 → exact    （与 Fast 无关，纯存储精度）
#
# Δcode 口径不依赖任何面板假设；Δnits 那条要用【本机 SDR 纸白 = 120 nits】这个实测值，
# 换机器必须重量，否则那个数没有意义。
import sys, csv, math, struct

PAPER_WHITE_NITS = 120.0     # 本机实测（交接报告 §…）；只在换算 nits 时用到


def srgb_to_linear(v):
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def linear_to_srgb(l):
    l = min(max(l, 0.0), 1.0)
    return 12.92 * l if l <= 0.0031308 else 1.055 * (l ** (1.0 / 2.4)) - 0.055


def to_half(x):
    return struct.unpack('<e', struct.pack('<e', float(x)))[0]


def self_check():
    """对照实现自己先要站得住，否则后面全是垃圾读数。"""
    assert abs(srgb_to_linear(128 / 255) - 0.21586) < 1e-5, "sRGB 参照点 128 就对不上"
    assert abs(linear_to_srgb(srgb_to_linear(0.3)) - 0.3) < 1e-12, "分段曲线不是互逆"
    assert to_half(1.0) == 1.0 and to_half(0.0) == 0.0
    assert to_half(1.0 + 2 ** -11) == 1.0, "half 步长行为不对"
    assert to_half(2 ** -16) > 0, "次正规没实现好"


def band(c):
    if c <= 10: return "暗部 0-10"
    if c <= 64: return "低中 11-64"
    if c <= 200: return "中 65-200"
    return "高 201-255"


def main():
    self_check()
    rows = list(csv.DictReader(sys.stdin))
    if len(rows) != 256:
        print(f"!! 只读到 {len(rows)} 行，应为 256 ⇒ 上游输出不完整，不给结论")
        sys.exit(1)

    agg = {}
    bands = {}
    half_impl_gap = {"n": 0, "max": 0.0, "at": 0}   # 工具层：CPython 的 half 与 .NET Half 的分歧
    for r in rows:
        c = int(r["code"])
        fast = float(r["fast_linear"])
        half_by_product = float(r["half16_roundtrip"])
        exact = srgb_to_linear(c / 255.0)
        # 主口径一律用【产品自己那列】half16_roundtrip：交换链里存的就是它。
        # 本脚本的 to_half() 只做对照，且实测与 .NET 的 Half 存在 1 ULP 级别的分歧（见下方注脚）
        # ⇒ 拿它当被测量会把工具差异混进产品差异，这里明确不用。
        linA = half_by_product

        g = abs(to_half(fast) - half_by_product)
        if g > 0:
            half_impl_gap["n"] += 1
            if g > half_impl_gap["max"]:
                half_impl_gap["max"], half_impl_gap["at"] = g, c

        parts = {
            "① Fast 近似": fast - exact,
            "② Half16 量化": half_by_product - fast,      # 产品实际发生的量化
            "＝ 总差 A-B": linA - exact,
        }
        codeA = linear_to_srgb(linA) * 255.0       # 落回 8-bit 码点
        for k, d in parts.items():
            s = agg.setdefault(k, {"n": 0, "sum": 0.0, "max": 0.0, "at": 0, "dcode": 0.0})
            s["n"] += 1
            s["sum"] += abs(d)
            if abs(d) > s["max"]:
                s["max"] = abs(d)
                s["at"] = c
                s["dcode"] = abs(codeA - c)
        b = band(c)
        bands.setdefault(b, []).append((abs(codeA - c), c))

    print(f"{'误差来源':<16}{'峰值(线性)':>13}{'@码点':>7}{'Δ8bit码':>10}{'Δnits@纸白120':>16}{'均值|.|':>12}")
    for k in ("① Fast 近似", "② Half16 量化", "＝ 总差 A-B"):
        s = agg[k]
        print(f"{k:<16}{s['max']:>13.8f}{s['at']:>7}{s['dcode']:>10.3f}"
              f"{s['max'] * PAPER_WHITE_NITS:>16.4f}{s['sum'] / s['n']:>12.8f}")

    print("\n按码段看总差（Δ8bit 码；括号里是取到该偏差的码点）：")
    for b in ("暗部 0-10", "低中 11-64", "中 65-200", "高 201-255"):
        v = bands[b]
        mx, at = max(v)
        print(f"  {b:<12} max={mx:.3f} (@{at})  mean={sum(x for x, _ in v) / len(v):.3f}"
              f"   Δ10bit≈{mx * 4:.2f}")

    t = agg["＝ 总差 A-B"]
    print(f"\n一句判词：两条路的数据差最大 {t['max']:.8f}（线性），折到 8-bit 网格是 "
          f"{t['dcode']:.3f} 个码点、到本机纸白是 {t['max'] * PAPER_WHITE_NITS:.4f} nits；"
          f"其中 Half16 量化占 {agg['② Half16 量化']['max']:.8f}、Fast 近似占 "
          f"{agg['① Fast 近似']['max']:.8f}。")
    print(f"\n[工具层注脚] CPython 的 float→half 与 .NET 的 Half 在 "
          f"{half_impl_gap['n']}/256 个码点上不一致，最大 {half_impl_gap['max']:.10f}"
          f"（@{half_impl_gap['at']}，约 1 ULP）⇒ 所以本表的 half 全部取产品那一列，"
          f"脚本的 to_half() 只用于自检。")


main()
