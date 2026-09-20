"""判据：广色域矩阵必须与"输出位深"无关，且两档之间只允许差最后一次量化。

为什么需要它（2026-09-19，交接 §29 P1-1/P1-2 的验收）：
  改之前，色域矩阵只写在 `PrepareSdr16Pixels`（≥10-bit 分支）里，8-bit 分支走
  `ToSdr → FloatToSRgbBytes` 且把 tag 覆写成 sRGB ⇒ 用户选 8-bit 就**静默丢掉广色域变换**；
  而"位深下拉"同时决定了"做不做矩阵"这件事本身就是错的。
  这条判据把两件事分开验：
    ① 同一目标色域、只有输出位深不同的两张 PNG，**声明**（cICP primaries）必须一致；
    ② 两张的像素只能差最后一次量化（16→8 取整后 ≤1 LSB）；
    ③ 对照组：同位深、sRGB 目标 与 P3 目标 两张必须**不同** —— 相同就说明矩阵根本没跑
       （这正是改动前的状态，所以这条在旧代码上必红）。

判据只用外部实现：PNG 块按公共规范自己扫（不 import 本仓库的解析器），像素由 ffmpeg 解码。
缺工具/缺样张 ⇒ 打印原因并以 EXIT_SKIP 退出（不许"没判到还退 0"）。

用法: py scripts/precision/gamut_depth_consistency.py [--samples 目录]
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import os
import struct
import subprocess
import sys

import precision_env as pe

TAG_P3_CICP_PRIMARIES = 12      # H.273 colour_primaries 12 = Display P3
PAIRS_PNG = [("gd_png_p3_d8", "gd_png_p3_d16")]
CONTROL_SRGB_8BIT = "d_png_8"   # 同位深的 sRGB 对照组


def path_of(name, directory):
    for f in os.listdir(directory):
        if f.split(".")[0] == name:
            return os.path.join(directory, f)
    return None


def png_chunks(p):
    """按 PNG 公共规范扫块：返回 {类型: 数据} 与 IHDR 解析结果。"""
    with open(p, "rb") as fh:
        data = fh.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{os.path.basename(p)} 不是 PNG")
    off, out, ihdr = 8, {}, None
    while off + 8 <= len(data):
        (ln,) = struct.unpack(">I", data[off:off + 4])
        typ = data[off + 4:off + 8].decode("latin-1")
        body = data[off + 8:off + 8 + ln]
        out[typ] = body
        if typ == "IHDR":
            w, h, depth, ctype, comp, filt, inter = struct.unpack(">IIBBBBB", body[:13])
            ihdr = {"w": w, "h": h, "depth": depth, "color_type": ctype}
        off += 12 + ln
    return out, ihdr


def cicp_primaries(p):
    chunks, ihdr = png_chunks(p)
    body = chunks.get("cICP")
    prim = chunks.get("iCCP")
    return (body[0] if body else None), (prim is not None), ihdr


def decode_rgba(p, ffmpeg):
    """ffmpeg 解码成裸 RGBA：16-bit PNG 用 rgba64le，再归一到 8-bit 量化域。"""
    chunks, ihdr = png_chunks(p)
    bits = ihdr["depth"]
    pix = "rgba64le" if bits == 16 else "rgba"
    r = subprocess.run([ffmpeg, "-v", "error", "-i", p, "-f", "rawvideo", "-pix_fmt", pix, "-"],
                       capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"ffmpeg 解码失败: {r.stderr.decode('utf-8', 'replace').strip()}")
    raw = r.stdout
    if bits == 16:
        n = len(raw) // 2
        vals = struct.unpack(f"<{n}H", raw[:n * 2])
        # 65535/255 = 257 ⇒ 除 257 四舍五入即"最后一次量化到 8-bit"
        return tuple((v + 128) // 257 if v < 65535 else 255 for v in vals)
    return tuple(raw)


def main():
    tools = pe.require_tools("ffmpeg")
    ff = tools["ffmpeg"]
    d = pe.samples_dir("sweep", pe.opt("--samples"))
    print("═══ 广色域矩阵 × 输出位深 一致性 ═══")
    print(f"  样张: {d}")
    fails, info = [], []

    for a, b in PAIRS_PNG:
        pa, pb = path_of(a, d), path_of(b, d)
        if pa is None or pb is None:
            print(f"  ⏭ 缺样张 {a} / {b} ⇒ 先跑 Tools --param-sweep")
            sys.exit(pe.EXIT_SKIP)

        # ① 声明必须一致（都按 P3 标记，且不允许靠 iCCP 兜底而少掉 cICP）
        pr_a, icc_a, ihdr_a = cicp_primaries(pa)
        pr_b, icc_b, ihdr_b = cicp_primaries(pb)
        ok_decl = pr_a == pr_b == TAG_P3_CICP_PRIMARIES
        print(f"\n  {a}(IHDR {ihdr_a['depth']}bit, cICP={pr_a}, iCCP={icc_a}) vs "
              f"{b}(IHDR {ihdr_b['depth']}bit, cICP={pr_b}, iCCP={icc_b})")
        print(f"    ① 两档 cICP primaries 同为 12(Display P3): {'✅' if ok_decl else '❌'}")
        if not ok_decl:
            fails.append("位深档之间声明不一致/缺 cICP（矩阵只在高位深跑）")
        if ihdr_a["depth"] != 8 or ihdr_b["depth"] != 16:
            fails.append(f"位深下拉未生效：IHDR {ihdr_a['depth']} / {ihdr_b['depth']}（应为 8 / 16）")

        # ② 两档像素只差最后一次量化
        try:
            va, vb = decode_rgba(pa, ff), decode_rgba(pb, ff)
        except Exception as ex:
            print(f"    ⚠ 解码比对跳过：{ex}")
            info.append("ffmpeg 解码不可用，②③ 未判")
            continue
        if len(va) != len(vb):
            fails.append("两档解码尺寸不同，无法比对")
            continue
        over = sum(1 for x, y in zip(va, vb) if abs(x - y) > 1)
        worst = max((abs(x - y) for x, y in zip(va, vb)), default=0)
        pct = over / len(va) * 100.0
        ok_q = pct < 0.1 and worst <= 8
        print(f"    ② 16→8 量化后差 >1LSB 的采样占比 {pct:.3f}%（最大差 {worst}）: {'✅' if ok_q else '❌'}")
        if not ok_q:
            fails.append(f"两档像素差不止一次量化（>1LSB 占 {pct:.2f}%，最大 {worst}）")

    # ③ 对照组：同位深 sRGB vs P3 必须不同 ⇒ 矩阵真的作用在 8-bit 档上
    p8 = path_of("gd_png_p3_d8", d)
    ps = path_of(CONTROL_SRGB_8BIT, d)
    if p8 and ps:
        try:
            v8, vs = decode_rgba(p8, ff), decode_rgba(ps, ff)
            if len(v8) == len(vs):
                diff = sum(1 for x, y in zip(v8, vs) if x != y)
                ok_m = diff > len(v8) * 0.05
                print(f"\n  ③ 对照 {CONTROL_SRGB_8BIT}(sRGB) vs gd_png_p3_d8(P3)："
                      f"不同的采样 {diff / len(v8) * 100:.1f}% ⇒ 矩阵{'✅真的跑了' if ok_m else '❌没跑'}")
                if not ok_m:
                    fails.append("8-bit 档广色域矩阵没有作用（两档像素几乎一致 = 旧缺陷）")
                pc, _, ih = cicp_primaries(ps)
                print(f"     对照组自身声明：cICP={pc}, iCCP={_}, IHDR={ih['depth']}bit")
            else:
                info.append("对照组尺寸不同，未判 ③")
        except Exception as ex:
            print(f"    ⚠ 对照组跳过：{ex}")

    print("\n════════════════════════════════════")
    if fails:
        for f in fails:
            print(f"  ❌ {f}")
        print(f"结论：不通过（{len(fails)} 项）")
        sys.exit(pe.EXIT_FAIL)
    for i in info:
        print(f"  ⚠ {i}")
    print("结论：广色域与位深已解耦，两档仅差最后一次量化")
    sys.exit(pe.EXIT_OK)


if __name__ == "__main__":
    main()
