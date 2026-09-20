#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""§31.3-9 ICC 字节阈值标定探针：把"多小的 ICC 还算一份能声明色域的完整 profile"量出来。

为什么需要一个探针而不是直接讨论数字：
  Core 里 `MinEmbeddableIccBytes = 400` 的注释自己写着"沿用改造前的既有取值，本次只做收敛、
  未重新标定"。而它的用途是"这份 ICC 足以代表目标色域吗"——**长度根本回答不了这个问题**，
  它能回答的只有"这份 blob 还算一份 profile 吗"。要定这个数，就得知道
  ① 一份带齐必需标签的合法 matrix profile 最小能到多少字节，
  ② 现网真实存在的最小 profile 是多少字节，
  ③ 被截断到某个字节数的 profile，权威实现还认不认。

三面取证（缺一面都不算标定）：
  [A] 构造下界：按 ICC.1 必需标签自己拼一份最小完整 matrix profile，逐标签实测字节数；
  [B] 现网语料：Windows 颜色目录里每份 .icm/.icc 的大小与结构（不假设任何先验）；
  [C] 权威裁判：Windows 自己的 ICC 解析器 icm32!OpenColorProfile ——
      拿它同时判"我构造的最小 profile 到底合不合法"和"截断到 N 字节它还认不认"。
      ⚠ 这一面是关键：没有它，[A] 只是我对手工拼字节的自我确认（构造恒等），
        说明不了"别的实现会不会要更多字节"。

退出码：0 = 三条判据都成立；1 = 有判据不成立（阈值落在错误区间）；3 = 环境缺 icm32/语料，未判。
用法：py scripts/probes/icc_min_size_probe.py [--threshold 400] [--emit-dir tests/fixtures]

⚠ 定位：这是**取证脚本**，不在 `verify.ps1` 门禁里。它评的是"拿某个字节数当判据合不合适"——
2026-09-20 的结论是"任何单一字节数都无解，改问结构"（见交接 §32.12），所以按默认的 400 再跑
必然报红，那正是它当时给出的历史判定，不是回归。防回退由常驻用例
`Test_IccEmbeddableIsStructuralNotLength` 负责（夹具就是本脚本 --emit-dir 写出来的那两份）。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import argparse
import io
import os
import struct
import sys

# ── ICC 基本件 ────────────────────────────────────────────────────────────
XYZ_TAGS = (b"rXYZ", b"gXYZ", b"bXYZ")


def s15f16(v: float) -> bytes:
    return struct.pack(">i", int(round(v * 65536.0)))


def xyz(x: float, y: float, z: float) -> bytes:
    # XYZType = signature(4) + reserved(4) + 3 x s15Fixed16(12)
    return b"XYZ " + b"\0\0\0\0" + s15f16(x) + s15f16(y) + s15f16(z)


def desc_v2(text: str) -> bytes:
    # 'desc' = signature(4) + reserved(4) + ASCII(length-prefixed, 含 NUL) + pad to 4
    body = struct.pack(">I", len(text) + 1) + text.encode("ascii", "ignore") + b"\0"
    data = b"desc" + b"\0\0\0\0" + body
    data += b"\0" * ((-len(data)) % 4)
    return data


def curv_empty() -> bytes:
    # count = 0 ⇒ "type 0，无校正"，是 ICC.1 允许的最省写法的 TRC
    return b"curv" + b"\0\0\0\0" + struct.pack(">I", 0)


def text_tag(sig: bytes, payload: bytes = b"\0") -> bytes:
    data = sig + b"\0\0\0\0" + struct.pack(">I", len(payload)) + payload
    data += b"\0" * ((-len(data)) % 4)
    return data


def build_matrix_profile(*, want_desc, want_wtpt, want_bkpt, want_primaries,
                         want_trc, want_cprtright, pad_to=0) -> bytes:
    """按 ICC.1 v2 matrix-RGB 的必需标签子集拼一份 profile，头部长度字段如实回填。"""
    tags: list[tuple[bytes, bytes]] = []
    if want_desc:
        tags.append((b"desc", desc_v2("probe")))
    if want_wtpt:
        tags.append((b"wtpt", xyz(0.9642, 1.0, 0.8249)))       # D50，PCS 白点
    if want_bkpt:
        tags.append((b"bkpt", xyz(0.0, 0.0, 0.0)))
    if want_primaries:
        # 真实 sRGB→XYZ(D50) 列值，保证"三列之和 == wtpt"这条硬不变量成立
        tags.append((b"rXYZ", xyz(0.4360747, 0.2225045, 0.0139322)))
        tags.append((b"gXYZ", xyz(0.3850649, 0.7168786, 0.0971045)))
        tags.append((b"bXYZ", xyz(0.1430804, 0.0606169, 0.7141733)))
    if want_trc:
        for t in (b"rTRC", b"gTRC", b"bTRC"):
            tags.append((t, curv_empty()))
    if want_cprtright:
        tags.append((b"cprt", text_tag(b"text", b"(c) probe")))

    if pad_to:
        # 只加"没有语义"的填充标签 ⇒ 长度过了阈值，却仍然一份 primaries 都没有。
        # 这一档专门用来问："Length > 400" 拦得住这种 blob 吗？
        k = 0
        while True:
            fill = text_tag(b"moNo", b"\0" * k)
            est = (132 + 12 * (len(tags) + 1)
                   + sum(len(d) for _, d in tags) + len(fill))
            if est >= pad_to:
                break
            k += 4
        tags.append((b"moNo", fill))

    header = 132
    table = 12 * len(tags)
    total = header + table + sum(len(d) for _, d in tags)

    h = bytearray(header)
    struct.pack_into(">I", h, 0, total)          # profile 长度字段
    h[12:16] = b"mntr"                           # device class = display
    h[16:20] = b"RGB "                           # color space
    h[20:24] = b"XYZ "                           # PCS
    h[36:40] = b"acsp"                           # 固定签名
    h[8:12] = struct.pack(">I", 0x02100000)      # version 2.1.0
    # ⚠ 标签数在 128..131（属于头部最后 4 字节），标签表从 132 起 —— 曾把它写在 132，
    #    于是自己造的 profile 数出 0 个标签，"最小完整"那条判据当场变成永真
    struct.pack_into(">I", h, 128, len(tags))

    cursor = header + table
    table_bytes = bytearray()
    data_bytes = bytearray()
    for sig, d in tags:
        # 4 字节对齐：每个 tag 数据本身已按 4 对齐构造
        table_bytes += sig + struct.pack(">I", cursor) + struct.pack(">I", len(d))
        data_bytes += d
        cursor += len(d)
    out = bytearray(h) + table_bytes + data_bytes
    assert len(out) == total, (len(out), total)
    return bytes(out)


def parse(icc: bytes) -> dict:
    """自己扫一遍：长度字段 / 标签表是否越界 / 必需标签在不在。"""
    r = {"size": len(icc), "declared": None, "len_ok": False, "tags": {},
         "has_primaries": False, "white_sum_err": None}
    if len(icc) < 132:
        return r
    declared = struct.unpack_from(">I", icc, 0)[0]
    r["declared"] = declared
    r["len_ok"] = declared == len(icc)
    n = struct.unpack_from(">I", icc, 128)[0]
    # 标签数占 128..131，标签表从 132 起、每项 12 字节（⚠ 不是 136 起，曾因此把
    # 每份真 profile 都读成"没有 primaries"—— 判据自己坏掉比实现坏掉更难发现）
    if n > 400 or 132 + 12 * n > len(icc):
        return r
    for i in range(n):
        e = 132 + 12 * i
        sig = icc[e:e + 4]
        off, size = struct.unpack_from(">II", icc, e + 4)
        if off + size > len(icc):
            continue
        r["tags"][sig] = (off, size)
    r["has_primaries"] = all(t in r["tags"] for t in XYZ_TAGS)
    if r["has_primaries"] and b"wtpt" in r["tags"]:
        def read_xyz(tag):
            o = r["tags"][tag][0] + 8
            return struct.unpack_from(">iii", icc, o)
        cols = [read_xyz(t) for t in XYZ_TAGS]
        summed = [sum(c[k] for c in cols) / 65536.0 for k in range(3)]
        w = [v / 65536.0 for v in read_xyz(b"wtpt")]
        r["white_sum_err"] = max(abs(summed[k] - w[k]) for k in range(3))
    return r


# ── 权威裁判：LittleCMS（Pillow 自带的 lcms2）───────────────────────────
# 为什么不是 icm32：实测 icm32.dll 只有 21 个具名导出、mscms.dll 的 126 个里没有任何
# "Open*ColorProfile"（那套 ICM API 是按序号从 icm32.lib 导入的，头文件里没有序号映射）
# ⇒ 名字取不到，不能拿"取不到"当"判不了"，换一个真的能跑的裁判。
# LittleCMS 是 ICC 规范的参考实现（Chromium/macOS/Windows 的色彩管理都基于它），
# 它认不认这份字节流，比"我自己按规范拼的算对了没有"独立得多。
def lcms_oracle():
    try:
        from PIL import ImageCms
    except Exception as ex:                                    # noqa: BLE001
        print(f"[C] ⚠ Pillow/lcms2 不可用：{type(ex).__name__}: {ex}")
        return None

    def accepts(blob: bytes) -> tuple[bool, str]:
        try:
            ImageCms.getOpenProfile(io.BytesIO(blob))
            return True, ""
        except Exception as ex:                                # noqa: BLE001
            return False, f"{type(ex).__name__}: {ex}"

    def builtin_srgb() -> bytes | None:
        try:
            return ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
        except Exception:                                      # noqa: BLE001
            return None

    accepts.lcms = ImageCms
    accepts.builtin_srgb = builtin_srgb
    return accepts


# ── [B] 现网语料 ─────────────────────────────────────────────────────────
def corpus_dirs() -> list[str]:
    cands = [os.path.expandvars(r"%SystemRoot%\System32\spool\drivers\color"),
             os.path.expandvars(r"%ProgramFiles%\Color"),
             os.path.expandvars(r"%APPDATA%\Microsoft\Windows\Color")]
    return [d for d in cands if os.path.isdir(d)]


def scan_corpus(ref) -> list[dict]:
    rows = []
    for d in corpus_dirs():
        for name in sorted(os.listdir(d)):
            if not name.lower().endswith((".icc", ".icm")):
                continue
            p = os.path.join(d, name)
            try:
                with open(p, "rb") as f:
                    blob = f.read()
            except OSError:
                continue
            r = parse(blob)
            r["name"] = name
            r["icm_ok"] = ref(blob)[0] if ref else None
            rows.append(r)
    return rows


# ── 判据 ────────────────────────────────────────────────────────────────
def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--threshold", type=int, default=400,
                    help="要标定的 Core 阈值 MinEmbeddableIccBytes（默认按现行值 400）")
    ap.add_argument("--usable-floor", type=int, default=128,
                    help="另一档 MinUsableIccBytes（cjxl/avifenc --icc 的门）")
    ap.add_argument("--emit-dir", default="",
                    help="把构造出的两份 profile 写成夹具（icc_minimal_legal.icc / icc_shell.icc），"
                         "给 C# 侧判据共用 —— 同一段字节构造不在两种语言里各写一遍")
    args = ap.parse_args()

    print("═══ §31.3-9 ICC 字节阈值标定 ═══\n")

    ref = lcms_oracle()
    lcms_srgb = ref.builtin_srgb() if ref else None

    # 先证明裁判是活的：真 profile 必须收、截断的必须拒
    oracle_ok = False
    if ref:
        print("[C] 裁判 = LittleCMS（ICC 参考实现，Pillow 自带 lcms2）")
        if lcms_srgb:
            i = parse(lcms_srgb)
            print(f"    参考实现自己造的 sRGB profile = {len(lcms_srgb)} B"
                  f"（带 primaries={i['has_primaries']}、长度字段自洽={i['len_ok']}）")
        real = None
        for d in corpus_dirs():
            for name in os.listdir(d):
                if name.lower().endswith((".icc", ".icm")):
                    with open(os.path.join(d, name), "rb") as f:
                        b = f.read()
                    if len(b) > 1024 and parse(b)["has_primaries"]:
                        real = (name, b)
                        break
            if real:
                break
        if real:
            name, b = real
            full, _ = ref(b)
            trunc, _ = ref(b[:700])
            print(f"    自检：真 profile {name}({len(b)}B) → 收={full}；截到 700B → 收={trunc}"
                  f" ⇒ {'裁判有效' if full and not trunc else '裁判存疑（不能只靠它）'}")
            oracle_ok = bool(full and not trunc)

    # ── [A] 构造下界：必需标签逐件加/减，量出"最小完整"到底是几字节 ──
    print("\n[A] 构造的 ICC.1 v2 matrix-RGB 最小 profile（逐件增减必需标签）")
    ladder = [
        ("全必需集 desc+wtpt+bkpt+3prim+3trc", dict(
            want_desc=1, want_wtpt=1, want_bkpt=1, want_primaries=1, want_trc=1,
            want_cprtright=0)),
        ("去掉 bkpt", dict(
            want_desc=1, want_wtpt=1, want_bkpt=0, want_primaries=1, want_trc=1,
            want_cprtright=0)),
        ("去掉 3×TRC（TRC 是必需的）", dict(
            want_desc=1, want_wtpt=1, want_bkpt=1, want_primaries=1, want_trc=0,
            want_cprtright=0)),
        ("去掉 wtpt（PCS 白点是必需的）", dict(
            want_desc=1, want_wtpt=0, want_bkpt=1, want_primaries=1, want_trc=1,
            want_cprtright=0)),
        ("去掉 primaries（只剩壳）", dict(
            want_desc=1, want_wtpt=1, want_bkpt=1, want_primaries=0, want_trc=0,
            want_cprtright=0)),
        ("全必需集 + cprt", dict(
            want_desc=1, want_wtpt=1, want_bkpt=1, want_primaries=1, want_trc=1,
            want_cprtright=1)),
        ("壳 + 补到 500B（长度过阈值却没有 primaries）", dict(
            want_desc=1, want_wtpt=1, want_bkpt=1, want_primaries=0, want_trc=0,
            want_cprtright=1, pad_to=500)),
    ]
    built = {}
    blobs = {}
    for label, kw in ladder:
        blob = build_matrix_profile(**kw)
        blobs[label] = blob
        inf = parse(blob)
        ok = ref(blob)[0] if ref else None
        why = "" if (ok or not ref) else ref(blob)[1][:46]
        built[label] = (len(blob), inf, ok)
        print(f"    {label:<44} {len(blob):>5} B  长度自洽={inf['len_ok']}  "
              f"primaries={inf['has_primaries']}  三列和==wtpt 误差="
              f"{('%.4f' % inf['white_sum_err']) if inf['white_sum_err'] is not None else '—':>7}  "
              f"lcms={ok}{(' ← ' + why) if why else ''}")

    min_full_key = "全必需集 desc+wtpt+bkpt+3prim+3trc"
    min_full_size = built[min_full_key][0]
    shell_key = "壳 + 补到 500B（长度过阈值却没有 primaries）"
    shell_size = built[shell_key][0]
    if args.emit_dir:
        os.makedirs(args.emit_dir, exist_ok=True)
        for key, fname in ((min_full_key, "icc_minimal_legal.icc"),
                           (shell_key, "icc_shell_no_primaries.icc")):
            p = os.path.join(args.emit_dir, fname)
            with open(p, "wb") as f:
                f.write(blobs[key])
            print(f"    夹具写出 {p}（{len(blobs[key])} B，lcms 认可={built[key][2]}，"
                  f"带 primaries={built[key][1]['has_primaries']}）")
    print(f"\n    ⇒ 构造面：能带 primaries 的最小完整 matrix profile = {min_full_size} B")

    # ── [B] 现网语料 ────────────────────────────────────────────────────
    rows = scan_corpus(ref)
    print(f"\n[B] 现网语料（{len(corpus_dirs())} 个目录，{len(rows)} 份 profile）")
    if not rows:
        print("    ⚠ 一份都没扫到 ⇒ 这一面缺失")
    for r in sorted(rows, key=lambda x: x["size"]):
        print(f"    {r['size']:>7} B  长度自洽={str(r['len_ok']):<5} primaries={str(r['has_primaries']):<5}"
              f" lcms={str(r['icm_ok']):<5} 三列和误差="
              f"{('%.4f' % r['white_sum_err']) if r['white_sum_err'] is not None else '—':>8}  {r['name']}")
    legit = [r for r in rows if r["has_primaries"] and r["len_ok"] and r["icm_ok"] is not False]
    smallest_legit = min(legit, key=lambda x: x["size"]) if legit else None
    if smallest_legit:
        print(f"\n    ⇒ 语料面：真实存在的最小『带 primaries、结构自洽、lcms 认』profile = "
              f"{smallest_legit['size']} B（{smallest_legit['name']}）")

    # ── [D] 判定 ───────────────────────────────────────────────────────
    print("\n[D] 判据")
    fails = 0
    floors = {"构造最小完整": min_full_size}
    if lcms_srgb:
        floors["lcms 自带 sRGB"] = len(lcms_srgb)
    if smallest_legit:
        floors["现网最小合法"] = smallest_legit["size"]
    if not legit and ref:
        print("    ❌ 语料面一份合法 profile 都没有 ⇒ 无法标定（判红，不给绿）")
        fails += 1
    floor = min(floors.values())
    print("    三面下界：" + "、".join(f"{k} {v} B" for k, v in floors.items())
          + f" ⇒ 取最小 = {floor} B")

    # ① 阈值不许 ≥ "任何一份合法 profile 的大小"——那样会静默丢掉真实声明
    too_high = args.threshold >= floor
    print(f"    ① 阈值 {args.threshold} 会不会拒掉合法 profile（判据是 Length > 阈值）？ "
          f"{'❌ 会：最小合法样本正好落在阈值上或以内' if too_high else '✅ 不会'}")
    fails += 1 if too_high else 0

    # ② 阈值拦得住的东西，与"能不能声明色域"有没有交集——这是这条阈值的立身理由
    shell_passes = shell_size > args.threshold
    if shell_passes:
        print(f"    ② ❌ 一份 {shell_size} B 的 profile【壳】（长度字段自洽、lcms "
              f"{'认' if built[shell_key][2] else '不认'}，但**没有 rXYZ/gXYZ/bXYZ**）"
              f"能通过阈值 {args.threshold} ⇒ 长度回答不了"
              f"『这份 ICC 足以代表目标色域吗』，注释里那句话是它承担不了的职责")
        fails += 1
    else:
        print(f"    ② 壳({shell_size}B) 未过阈值 ⇒ 长度至少拦住了这一类")

    # ③ 截断实验：真 profile 被截到阈值-1 之后，权威实现还认不认
    if ref and oracle_ok:
        for d in corpus_dirs():
            hit = next((os.path.join(d, n) for n in os.listdir(d)
                        if n.lower().endswith((".icc", ".icm"))
                        and os.path.getsize(os.path.join(d, n)) > args.threshold * 4), None)
            if hit:
                with open(hit, "rb") as f:
                    b = f.read()[:args.threshold - 1]
                ok, _ = ref(b)
                with open(hit, "rb") as f:
                    b = f.read()[:args.threshold - 1]
                ok, _ = ref(b)
                print(f"    ③ 截断到 {len(b)} B（阈值-1）：lcms 认可={ok}、含 primaries="
                      f"{parse(b)['has_primaries']} ⇒ "
                  + ("阈值这一档确实拦在可用/不可用的分界上" if not ok
                     else "❌ 阈值过严：这一档里仍有权威实现认的 profile"))
                fails += 1 if ok else 0
                break
    elif ref:
        print("    ③ 裁判自检未通过 ⇒ 这一面不给结论（不算通过）")
        fails += 1

    # ④ 两档阈值之间是否现网有货
    band = [r for r in rows if args.usable_floor < r["size"] <= args.threshold]
    print(f"    ④ 落在 ({args.usable_floor}, {args.threshold}] 这条带里的现网 profile：{len(band)} 份"
          + (" ⇒ 两档判据在这段互斥，须说明谁说了算" if band else "（两档不冲突）"))

    print(f"\n结果：{'❌ ' + str(fails) + ' 条判据不成立' if fails else '✅ 判据全部成立'}")
    return fails


if __name__ == "__main__":
    sys.exit(main())
