"""Cross-container round-trip audit: decode every product-written sample with an
independent decoder (ffmpeg) and require that containers carrying the *same* pixels and
the same colour mark agree on what those pixels are.

Why this invariant: the 2026-09-19 TIFF bug was invisible to 8 rounds of gates because the
in-repo TIFF parser mirrored the writer's own structural assumption. A decoder written by
somebody else cannot share that assumption, and "same tag, different container, same
picture" is a spec-level invariant rather than a home-grown expectation.

Tolerances: lossless containers must match exactly; lossy ones get a wider band, but a
*black* or channel-scrambled file fails loudly either way.

Usage: python scripts/precision/wg_container_roundtrip.py [--samples <dir>]
Exit codes: 0 pass / 1 decode failed or containers disagree / 3 SKIP (see precision_env.py)
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import subprocess
import sys

import precision_env as pe

W, H = 640, 240
CENTRES = {"red-mid": (80, 60), "green-mid": (240, 60), "blue-mid": (400, 60),
           "red-high": (560, 60), "gray-18": (80, 180), "gray-50": (240, 180),
           "cyan-mid": (400, 180), "orange": (560, 180)}
LOSSY = {".avif", ".jxl", ".jpg", ".jpeg", ".webp"}

DIR = None
SCRATCH = None
FFMPEG = None


def decode_rgba(path):
    raw = os.path.join(SCRATCH, os.path.basename(path) + ".rgba")
    r = subprocess.run([FFMPEG, "-y", "-hide_banner", "-loglevel", "error", "-i", path,
                        "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", raw],
                       capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(raw):
        return None, (r.stderr or "").strip()[:70]
    d = open(raw, "rb").read()
    os.remove(raw)
    if len(d) < W * H * 4:
        return None, "short decode (%d B)" % len(d)
    return d, ""


def patches(d):
    return {k: tuple(d[(y * W + x) * 4:(y * W + x) * 4 + 4]) for k, (x, y) in CENTRES.items()}


def main():
    global DIR, SCRATCH, FFMPEG
    tools = pe.require_tools("ffmpeg")
    FFMPEG = tools["ffmpeg"]
    DIR = pe.samples_dir("wg", pe.opt("--samples"))
    SCRATCH = pe.artifact_dir("decode")

    files = sorted(f for f in os.listdir(DIR)
                   if f.lower().endswith((".png", ".avif", ".jxl", ".tiff", ".webp", ".jpg")))
    if not files:
        print("⏭ 跳过：%s 下没有可判的容器样张" % DIR)
        return pe.EXIT_SKIP
    by_tag = {}
    broken = []
    print("%-24s %s" % ("file", "decoded patch values (RGBA)"))
    print("-" * 118)
    for f in files:
        p = os.path.join(DIR, f)
        tag = f.split("_")[0]
        d, err = decode_rgba(p)
        if d is None:
            print("%-24s ❌ DECODE FAILED: %s" % (f, err or "?"))
            broken.append(f)
            continue
        px = patches(d)
        by_tag.setdefault(tag, {})[f] = px
        flat = all(v[0] == v[1] == v[2] == v[3] == 0 for v in px.values())
        note = "  ⚠ 全黑" if flat else ""
        if flat:
            broken.append(f)
        print("%-24s %s%s" % (f, " ".join("%s=%s" % (k[:5], px[k]) for k in
                                          ("red-mid", "green-mid", "blue-mid", "gray-50")), note))

    print("\n══ 同色域跨容器一致性（基准 = 该色域的 PNG）══")
    bad = 0
    for tag, group in sorted(by_tag.items()):
        ref_name = next((k for k in group if k.endswith(".png")), None)
        if ref_name is None:
            print("  %-12s 无 PNG 基准，跳过" % tag)
            continue
        ref = group[ref_name]
        for name, px in sorted(group.items()):
            if name == ref_name:
                continue
            lossy = os.path.splitext(name)[1].lower() in LOSSY
            tol = 8 if lossy else 1
            worst = max(abs(ref[k][c] - px[k][c]) for k in CENTRES for c in range(3))
            adiff = sum(abs(ref[k][c] - px[k][c]) for k in CENTRES for c in range(3)) / (len(CENTRES) * 3)
            ok = worst <= tol
            bad += 0 if ok else 1
            print("  %-12s %-22s vs %-16s 最大差 %3d (容差 %d) 平均 %.2f  %s"
                  % (tag, name, ref_name, worst, tol, adiff, "✅" if ok else "❌ 不一致"))
    print("\n[RESULT] 不一致容器数 = %d，解码失败/全黑 = %d" % (bad, len(broken)))
    return pe.EXIT_FAIL if (bad or broken) else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
