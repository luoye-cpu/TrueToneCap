"""Verify --container-audit output with an external decoder (ffmpeg), not with our own parsers.

Invariants (each is a spec/product-level claim, not a re-statement of what the writer did):
  SDR   · every container of the same colour tag must decode to the same pixels as that
          tag's PNG (lossless tol=1, lossy tol=8) -- a container-level structural bug
          (TIFF's strip-offset indirection, a bad iloc, a mis-declared colour range)
          shows up here as black / flat / channel-scrambled.
  HDR   · ffprobe must report transfer=smpte2084 + primaries=bt2020 (the product's PQ claim),
          the image must not be black, and the tone order of the source pattern must survive
          (gray-18 < gray-50 < red-high in decoded luminance) -- a broken PQ path flattens it.

Usage: python scripts/precision/audit_roundtrip.py [--samples <dir>]
Exit codes: 0 pass / 1 a file failed a rule / 3 preconditions missing (SKIP, see precision_env.py)
依赖: ffmpeg + ffprobe（必需，判据主体），avifdec（可选，libavif 参考解析；缺失只降级不判失败）
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
TOL = {".png": 1, ".tiff": 1, ".jxl": 8, ".avif": 8, ".webp": 10, ".jpg": 10}

DIR = None      # 样张目录（main 里解析）
SCRATCH = None  # 解码中间文件目录 = artifacts/precision/decode
FFMPEG = FFPROBE = AVIFDEC = None


def decode(path, pix="rgba"):
    bpp = 4 if pix == "rgba" else 6
    raw = os.path.join(SCRATCH, os.path.basename(path) + "." + pix)
    r = subprocess.run([FFMPEG, "-y", "-hide_banner", "-loglevel", "error", "-i", path,
                        "-frames:v", "1", "-pix_fmt", pix, "-f", "rawvideo", raw],
                       capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(raw):
        return None, (r.stderr or "decode failed").strip()[:60]
    d = open(raw, "rb").read()
    os.remove(raw)
    if len(d) < W * H * bpp:
        return None, "short decode %dB" % len(d)
    return d, ""


def px_of(d, bpp=4):
    def at(x, y):
        i = (y * W + x) * bpp
        if bpp == 4:
            return tuple(d[i:i + 3])
        return tuple((d[i + c * 2] | (d[i + c * 2 + 1] << 8)) for c in range(3))  # rgb48le
    return {k: at(x, y) for k, (x, y) in CENTRES.items()}


def probe_colors(path):
    r = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0", "-show_entries",
                        "stream=pix_fmt,color_primaries,color_transfer", "-of",
                        "default=nw=1", path], capture_output=True, text=True)
    v = dict(x.split("=", 1) for x in r.stdout.split() if "=" in x)
    return v.get("pix_fmt", "?"), v.get("color_primaries", "?"), v.get("color_transfer", "?")


def avif_libavif(path):
    """Reference AVIF verdict from libavif's own decoder: (parses, primaries, transfer).

    Why this exists: ffprobe reports AV1 *stream* VUI, and the NVENC driver writes no
    color_description_present_flag (§9), so "unknown" there is not evidence about our
    container `colr`. libavif parses the container -- and it silently rejected all our
    NVENC AVIF files (hdlr/colr defects) while ffmpeg tolerated them.
    """
    if not AVIFDEC or not os.path.exists(AVIFDEC):
        return None, "?", "?"
    r = subprocess.run([AVIFDEC, "--info", path], capture_output=True, text=True)
    out = r.stdout + r.stderr
    prim = next((l.split(":")[-1].strip() for l in out.splitlines() if "Color Primaries" in l), "?")
    trc = next((l.split(":")[-1].strip() for l in out.splitlines() if "Transfer Char" in l), "?")
    return r.returncode == 0, prim, trc


def png_sbit(path):
    with open(path, "rb") as f:
        d = f.read(4096)
    i = d.find(b"sBIT")
    return tuple(d[i + 4:i + 8]) if i > 0 else None


def declared_depth(path):
    """ffprobe: container depth + the codec's own claim about significant bits."""
    r = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0", "-show_entries",
                        "stream=pix_fmt,bits_per_raw_sample", "-of", "default=nw=1", path],
                       capture_output=True, text=True)
    v = dict(x.split("=", 1) for x in r.stdout.split() if "=" in x)
    return v.get("pix_fmt", "?"), v.get("bits_per_raw_sample", "-")


def precision_claims():
    """容器位深 / sBIT / bits_per_raw_sample 三者的实际写法（Float16 源真实有效位 ≈11）。"""
    print("\n══ 精度声明审计（源为 Float16 ⇒ 真实有效位 ≈11：1 隐含 + 10 尾数）══")
    print("  %-22s %-14s %-9s %s" % ("file", "容器 pix_fmt", "raw_bits", "PNG sBIT"))
    for f in sorted(x for x in os.listdir(DIR)
                    if x.startswith("sdr_") and os.path.splitext(x)[1].lower() in (".png", ".jxl", ".tiff")):
        p = os.path.join(DIR, f)
        pf, rb = declared_depth(p)
        sb = png_sbit(p) if f.endswith(".png") else None
        print("  %-22s %-14s %-9s %s" % (f, pf, rb, sb if sb else "无(视同全有效)" if f.endswith(".png") else "-"))
    print("  ⇒ 关注点：16-bit 容器不写 sBIT = 宣称 16 位全有效，而源只有 ~11 位有效精度。")


def main():
    global DIR, SCRATCH, FFMPEG, FFPROBE, AVIFDEC
    tools = pe.require_tools("ffmpeg", "ffprobe")
    FFMPEG, FFPROBE = tools["ffmpeg"], tools["ffprobe"]
    AVIFDEC = pe.find_tool("avifdec")
    DIR = pe.samples_dir("audit", pe.opt("--samples"))
    SCRATCH = pe.artifact_dir("decode")
    if not AVIFDEC:
        print("⚠ 未找到 avifdec（libavif 参考解码器）⇒ AVIF 容器解析这项判据降级为只报 ffprobe 流级信息，"
              "不构成整步跳过：像素一致性与 PQ 声明仍由 ffmpeg 判。")

    files = sorted(f for f in os.listdir(DIR) if os.path.splitext(f)[1].lower() in TOL)
    if not files:
        print("⏭ 跳过：%s 下没有可判的容器样张" % DIR)
        return pe.EXIT_SKIP
    dec = {}
    fails = []
    print("%-22s %-6s %-14s %-30s %s" % ("file", "tag", "pix_fmt", "colour (primaries/transfer)", "verdict"))
    print("-" * 110)
    for f in files:
        p = os.path.join(DIR, f)
        # HDR 文件必须按 16-bit 读：PQ 码在低亮度端挤在极少的高 8 位里，降到 8-bit 后
        # gray-18/gray-50 会并级，"色调次序"判据会假红（2026-09-19 首轮实测即如此）。
        pix = "rgb48le" if f.startswith("hdr_") else "rgba"
        bpp = 6 if pix == "rgb48le" else 4
        d, err = decode(p, pix)
        if d is None:
            print("%-22s %-6s %-14s %-30s ❌ %s" % (f, f.split("_")[-1], "-", "-", err))
            fails.append((f, "decode"))
            continue
        dec[f] = px_of(d, bpp)
        ext = os.path.splitext(f)[1].lower()
        pf, prim, trc = probe_colors(p)
        verdict = ""
        if ext == ".avif":
            parses, lp, lt = avif_libavif(p)
            if parses is False:
                verdict = "❌ libavif 拒读整份文件"
            elif parses is None:
                verdict = "⚠ avifdec 不在，未做参考解析"
            else:
                verdict = "✅ (libavif 解析通过 prim=%s trc=%s)" % (lp, lt)
                prim, trc = lp, lt
        px = dec[f]
        flat = len({px[k] for k in CENTRES}) <= 2
        black = all(v == (0, 0, 0) for v in px.values())
        tag = f.split("_")[-1] if f.startswith("sdr_") else "HDR"
        if not verdict:
            verdict = "❌ 全黑" if black else ("⚠ 色块几乎无差异" if flat else "✅")
        print("%-22s %-6s %-14s %-30s %s" % (f, tag, pf, "%s/%s" % (prim, trc), verdict))
        if black or flat or verdict.startswith("❌"):
            fails.append((f, "black/flat/libavif"))

    print("\n══ SDR：同色域跨容器像素一致性（基准 = 该色域 PNG）══")
    for tag in ("srgb", "bt2020"):
        ref = "sdr_png_%s.png" % tag
        if ref not in dec:
            print("  %-8s 缺基准 %s ⇒ 未判定" % (tag, ref))
            fails.append((ref, "missing reference"))
            continue
        for f in sorted(dec):
            suffix = "_" + tag + os.path.splitext(f)[1]
            if not f.startswith("sdr_") or not f.endswith(suffix):
                continue
            if f == ref:
                continue
            tol = TOL[os.path.splitext(f)[1].lower()]
            worst = max(abs(dec[ref][k][c] - dec[f][k][c]) for k in CENTRES for c in range(3))
            ok = worst <= tol
            fails.append((f, "mismatch")) if not ok else None
            print("  %-8s %-22s 最大差 %3d (容差 %2d)  %s" % (tag, f, worst, tol, "✅" if ok else "❌"))

    print("\n══ HDR：PQ 声明与色调次序 ═══")
    hdr_rows = [f for f in sorted(dec) if f.startswith("hdr_")]
    if not hdr_rows:
        print("  缺 HDR 样张 ⇒ 未判定")
        fails.append(("hdr", "missing samples"))
    for f in hdr_rows:
        pf, prim, trc = probe_colors(os.path.join(DIR, f))
        px = dec[f]
        # PQ 编码空间里跨色相较"加权亮度"没有意义（饱和红的 709 加权和天然低于同码值灰）。
        # 成立的判据是【同通道单调】：源里更大的线性值必须映射到更大的 PQ 码，且非零。
        order = (px["gray-18"][0] < px["gray-50"][0]
                 and px["red-mid"][0] < px["red-high"][0]
                 and px["green-mid"][1] > 0 and px["blue-mid"][2] > 0)
        # TIFF 无 CICP 机制（只靠 ICC）、WebP 无色彩声明机制 ⇒ 流级 unknown 是预期，不判失败
        ext = os.path.splitext(f)[1].lower()
        if ext == ".avif":
            parses, prim, trc = avif_libavif(os.path.join(DIR, f))
            want = (prim, trc) == ("9", "16")       # libavif 输出的是 CICP 数字码点
            if parses is None:                      # avifdec 缺失 ⇒ 退回 ffprobe 流级读回
                pf2, prim2, trc2 = probe_colors(os.path.join(DIR, f))
                ok = order and trc2 == "smpte2084" and prim2 == "bt2020"
                print("  %-22s %-10s avifdec 不在，用 ffprobe 读回 %s/%s 通道单调%s  %s"
                      % (f, pf2, prim2, trc2, "✅" if order else "❌", "✅" if ok else "❌"))
            else:
                ok = parses and want and order
                print("  %-22s %-10s libavif prim/trc=%s/%s 通道单调%s  容器声明%s  %s"
                      % (f, pf, prim, trc, "✅" if order else "❌", "✅" if want else "❌",
                         "✅" if ok else "❌"))
        else:
            stream_cicp = ext not in (".tiff", ".webp")
            declared = (trc == "smpte2084" and prim == "bt2020") or not stream_cicp
            ok = order and declared
            print("  %-22s %-10s %-9s 通道单调%s  声明%s  %s"
                  % (f, pf, trc, "✅" if order else "❌",
                     "n/a(容器无 CICP)" if not stream_cicp else ("✅" if declared else "❌ %s/%s" % (prim, trc)),
                     "✅" if ok else "❌"))
        if not ok:
            fails.append((f, "hdr"))

    precision_claims()

    print("\n[RESULT] 文件 %d，问题 %d%s"
          % (len(files), len(fails), "  → " + "; ".join("%s:%s" % x for x in fails) if fails else " ✅ 全部通过"))
    return pe.EXIT_FAIL if fails else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
