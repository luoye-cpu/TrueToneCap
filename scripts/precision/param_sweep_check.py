"""Judge --param-sweep output: read back what each knob actually did, using external tools only.

Motivation (2026-09-19 audit): the repo's own parsers kept saying "OK" about containers the
reference decoders reject (TIFF strip offsets, AVIF hdlr/colr). So every claim here is read
with ffprobe or a raw byte scan of the *declaration*, never via TrueToneCap code.

Rules applied:
  chroma  · AVIF/HEIF: pix_fmt's subsampling must equal the request.
            8-bit BGRA sources are honestly clamped to 4:2:0 by the software path, so those
            rows are reported but not failed.
  depth   · PNG: IHDR bit depth must be 8 or 16 (spec allows only those for RGBA) and sBIT must
              match the request when >8.            AVIF: av1C/pix_fmt bits must not be < request.
              TIFF: BPS 8 or 16 only.
  colour  · requested tag -> expected cICP primaries code (sRGB 1, P3 12, BT.2020 9);
            AdobeRGB must have NO cICP (H.273 has no code point) and must carry iCCP.
  quality · size must be monotone in the direction of that format's own scale
            (AVIF/WebP: 质量分, ↑=更大; JPEG_XL/JPEG_LI: butteraugli 距离, ↑=更小).

Usage: python scripts/precision/param_sweep_check.py [--samples <dir>]
Exit codes: 0 pass / 1 a rule is violated / 3 preconditions missing (SKIP, see precision_env.py)
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import json
import os
import subprocess
import sys

import precision_env as pe

# 2026-09-27（用户口径）：AdobeRGB 作为【目标】一律收口进 Rec.2020
#（ColorProfileProvider.CollapseSelectableTag，样张宿主也过同一个函数）⇒ 期望码点就是 9。
# 以前这里是 None，含义是"Adobe 没有 H.273 码点 ⇒ 不许写 cICP、只带 ICC"；
# 那套口径下七个容器各走各的 Adobe 出路，任何一处单独改映射都会红成"跨容器不一致"。
# None 这条分支仍保留给"确实无码点可写"的将来情形（见下面 want is None 的处理）。
TAG_PRIM = {"srgb": "1", "p3": "12", "bt2020": "9", "adobe": "9"}
# name -> (chroma, depth, tag-key)
EXPECT = {
    "q_avifsw_10": ("420", 8, "srgb"), "q_avifsw_50": ("420", 8, "srgb"), "q_avifsw_90": ("420", 8, "srgb"),
    "q_webp_10": ("420", 8, "srgb"), "q_webp_50": ("420", 8, "srgb"), "q_webp_90": ("420", 8, "srgb"),
    "q_jpegli_d05": ("444", 8, "srgb"), "q_jpegli_d10": ("444", 8, "srgb"), "q_jpegli_d30": ("444", 8, "srgb"),
    "q_jxl_d05": ("444", 8, "srgb"), "q_jxl_d10": ("444", 8, "srgb"), "q_jxl_d30": ("444", 8, "srgb"),
    "c_avifsw_420": ("420", 10, "srgb"), "c_avifsw_422": ("422", 10, "srgb"), "c_avifsw_444": ("444", 10, "srgb"),
    "c_avifnv_444": ("444", 10, "srgb"), "d_avifnv_10": ("420", 10, "srgb"),
    "d_png_8": ("-", 8, "srgb"), "d_png_10": ("-", 10, "srgb"), "d_png_12": ("-", 12, "srgb"), "d_png_16": ("-", 16, "srgb"),
    "d_tiff_8": ("-", 8, "srgb"), "d_tiff_10": ("-", 10, "srgb"), "d_tiff_16": ("-", 16, "srgb"),
    "d_avifsw_8": ("444", 8, "srgb"), "d_avifsw_12": ("444", 12, "srgb"), "d_jxl_16": ("-", 16, "srgb"),
    "cs_png_srgb": ("-", 10, "srgb"), "cs_png_p3": ("-", 10, "p3"), "cs_png_bt2020": ("-", 10, "bt2020"),
    "cs_png_adobe": ("-", 10, "adobe"), "cs_avif_p3": ("444", 10, "p3"),
    "cs_avif_adobe": ("444", 10, "adobe"),
    "d_jxl_10": ("-", 10, "srgb"), "d_jxl_12": ("-", 12, "srgb"),
}

# 已知且已归档的偏差：命中时报告但【不计失败】。加条目必须带出处，否则等于把判据改绿。
# ("样张名", "判据关键词") -> 出处与结论
KNOWN = {
    ("c_avifnv_444", "色度"):
        "NVENC 的 AV1 管线只有 4:2:0（驱动从不写 chromaFormatIDC）⇒ 请求 444 恒产出 420。"
        "已记录并靠 Auto 过滤兜住（422/444 走 libaom），见 CHANGELOG「NVENC 不再静默改色度」/ 交接 §26.4。",
}


def find(name, directory):
    for f in os.listdir(directory):
        if f.split(".")[0] == name:
            return os.path.join(directory, f)
    return None


def probe(path, ffprobe):
    r = subprocess.run([ffprobe, "-v", "error", "-print_format", "json", "-show_streams", path],
                       capture_output=True, text=True)
    s = next((x for x in json.loads(r.stdout).get("streams", []) if x.get("codec_type") == "video"), {})
    return s


def png_scan(path):
    d = open(path, "rb").read(16384)
    i = d.find(b"IHDR")
    depth = d[i + 12] if i > 0 else 0
    j = d.find(b"cICP")
    k = d.find(b"sBIT")
    return depth, (tuple(d[j + 4:j + 8]) if j > 0 else None), (k > 0 and d[k + 4] > 8)


def avif_has_icc(path):
    """AVIF 里的 ICC 是另一个 colr 盒（colour_type = 'prof'/'rICC'/'ricg'）。"""
    d = open(path, "rb").read()
    i, found = 0, False
    while True:
        i = d.find(b"colr", i)
        if i < 0:
            return found
        ct = d[i + 4:i + 8]
        if ct in (b"prof", b"rICC", b"ricg"):
            found = True
        i += 4


def avif_nclx(path):
    d = open(path, "rb").read()
    i = d.find(b"colrnclx")
    f = d[i + 8:i + 15] if i > 0 else b""
    return (f[0] << 8 | f[1], f[2] << 8 | f[3], f[4] << 8 | f[5]) if len(f) >= 6 else None


def bits_of(pix):
    # 每通道位深：yuv420p10le / rgba64le(=16/通道) / rgb48le(=16) / gbrp12le
    for b in (16, 12, 10):
        if ("%dle" % b) in pix:
            return b
    for total, per in (("64", 16), ("48", 16), ("32", 8), ("24", 8)):
        if total in pix:
            return per
    return 8 if pix else 0


def classify(name, note):
    """命中 KNOWN ⇒ 返回出处字符串（不计失败），否则 None。"""
    for (row, key), source in KNOWN.items():
        if row == name and note.startswith(key):
            return source
    return None


def main():
    tools = pe.require_tools("ffprobe")
    directory = pe.samples_dir("sweep", pe.opt("--samples"))
    ffprobe = tools["ffprobe"]
    rows, problems, known_hits = [], [], []
    # 预期失败：SVT-AV1 实测只支持 4:2:0（§23.3），请求 4:4:4 必须**报错**而不是静默改参数
    if find("c_avifsvt_444", directory) is None:
        rows.append(("c_avifsvt_444", "—", "—", "—", 0, "✅ 按预期失败（SVT 仅 4:2:0，未静默降级）"))
    else:
        problems.append("c_avifsvt_444 竟然成功了 —— SVT 能力表需要重测")
    for name, (chroma, depth, tag) in EXPECT.items():
        p = find(name, directory)
        if p is None:
            problems.append("%s 无产物（编码失败）" % name)
            rows.append((name, "—", "—", "—", 0, "❌ 无产物"))
            continue
        s = probe(p, ffprobe)
        pix = s.get("pix_fmt", "?")
        got = int(s.get("bits_per_raw_sample") or 0) or bits_of(pix)
        prim = s.get("color_primaries", "?") or "?"
        ext = os.path.splitext(p)[1].lower()
        notes = []
        # 色度
        if chroma != "-" and ext == ".avif":
            gch = "444" if "444" in pix else "422" if "422" in pix else "420" if "420" in pix else "?"
            if gch != chroma:
                notes.append("色度 %s→%s" % (chroma, gch))
        # 位深
        if ext == ".png":
            bd, cicp, has_sbit = png_scan(p)
            if depth > 8 and bd != 16:
                notes.append("请求 %d 但 IHDR=%d" % (depth, bd))
            if depth == 8 and bd != 8:
                notes.append("请求 8 但 IHDR=%d" % bd)
            prim = str(cicp[0]) if cicp else "none"
            want = TAG_PRIM[tag]
            if want is None:
                if cicp:
                    notes.append("AdobeRGB 不该写 cICP")
                prim = "none(仅 iCCP)"
            elif prim != want:
                notes.append("原色 %s→%s" % (want, prim))
        elif ext == ".avif":
            if got and got < depth:
                notes.append("位深 %d→%d" % (depth, got))
            n = avif_nclx(p)
            has_icc = avif_has_icc(p)
            prim = (str(n[0]) if n else "无nclx") + ("+ICC" if has_icc else "+无ICC")
            want = TAG_PRIM[tag]
            if want is None:                      # AdobeRGB：H.273 无码点，只能靠 ICC
                if not has_icc:
                    notes.append("AdobeRGB 既无 cICP 也无 ICC ⇒ 容器内无任何色彩声明")
            elif n and str(n[0]) != want:
                notes.append("原色 %s→%s" % (want, n[0]))
        elif ext == ".tiff":
            notes += [] if (depth <= 8 and got == 8) or (depth > 8 and got == 16) else ["位深 %d→%d" % (depth, got)]

        if ext != ".png" and s.get("color_transfer"):
            prim = "%s/%s" % (prim, s["color_transfer"])
        # 已知偏差从 notes 里摘出去，其余算回归
        real, hits = [], []
        for n in notes:
            src = classify(name, n)
            (hits if src else real).append((n, src))
        for n, src in hits:
            known_hits.append("%s: %s ⇒ 已知（%s）" % (name, n, src))
        problems += ["%s: %s" % (name, n) for n, _ in real]
        shown = "; ".join(notes)
        rows.append((name, pix, got or "?", prim, os.path.getsize(p),
                     ("⚠ " + shown + "（已知，不计失败）") if notes and not real
                     else ("⚠ " + shown) if real else "✅"))

    w = max(len(r[0]) for r in rows)
    print("══ 参数请求 vs 外部读回 ══")
    print("  样张: %s" % directory)
    for r in rows:
        print("  %-*s %-12s %-4s %-22s %7d B  %s" % (w, r[0], r[1], r[2], r[3], r[4], r[5]))

    print("\n══ quality 单调性（按各格式自己的刻度）══")
    groups = [("q_avifsw", [("_10", 10), ("_50", 50), ("_90", 90)], "crf"),
              ("q_webp", [("_10", 10), ("_50", 50), ("_90", 90)], "up"),
              ("q_jpegli", [("_d05", .5), ("_d10", 1), ("_d30", 3)], "down"),
              ("q_jxl", [("_d05", .5), ("_d10", 1), ("_d30", 3)], "down")]
    for prefix, suffixes, direction in groups:
        sizes = [os.path.getsize(find(prefix + s, directory)) for s, _ in suffixes if find(prefix + s, directory)]
        if len(sizes) < 3:
            print("  %-10s 缺样张" % prefix)
            problems.append("%s: 缺样张，未判定" % prefix)
            continue
        mono = (sizes[0] < sizes[1] < sizes[2]) if direction == "up" else (sizes[0] > sizes[1] > sizes[2])
        scale = {"up": "质量分↑=体积↑", "down": "butteraugli 距离↑=体积↓",
                 "crf": "CRF↑=质量↓=体积↓"}[direction]
        print("  %-10s %-24s %s  %s" % (prefix, " → ".join(map(str, sizes)), "✅" if mono else "❌ 非单调", scale))
        if not mono:
            problems.append("%s: quality 非单调" % prefix)

    for k in known_hits:
        print("\nℹ️ %s" % k)

    print("\n[RESULT] 待确认 %d 项%s" % (len(problems),
          "：\n  - " + "\n  - ".join(problems) if problems else " ✅"))
    return pe.EXIT_FAIL if problems else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
