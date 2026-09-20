"""Read back what the product actually wrote into the wide-gamut samples.

Two independent questions, both answered with external tools (never with our own parser):
  1. metadata  -- ffprobe's color_* tags (AVIF/JXL/TIFF) + PNG chunk names (cICP/iCCP/sBIT),
                  which is just a structural chunk walk, not colour maths.
  2. stored pixels -- ffmpeg rawvideo decode of the patch centres. These are the numbers a
     viewer shows *if it ignores the colour mark*. The "honoured" expectation is the source
     colour the generator painted (see SOURCE below), so comparing the two tells us whether
     a given viewer can be trusted with BT.2020 + sRGB-transfer files.

这是一份【读数表】而不是断言集：第 3 步（浏览器实际渲染值）要人眼看，脚本无法替判。
所以它只在"读不出任何数据"时非 0，正常产出读数即 0。

Usage: python scripts/precision/wg_compat_check.py [--samples <dir>]
Exit codes: 0 读数产出 / 1 有文件解码或读取失败 / 3 SKIP (see precision_env.py)
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import struct
import subprocess
import sys

import precision_env as pe

W = 640
PATCH = 160, 120          # PW, PH
COLS = 4
CENTRES = {              # matches FormatBench.cs --wg-samples
    "red-mid": (80, 60), "green-mid": (240, 60), "blue-mid": (400, 60), "red-high": (560, 60),
    "gray-18": (80, 180), "gray-50": (240, 180), "cyan-mid": (400, 180), "orange": (560, 180),
}

DIR = None
SCRATCH = None
FFPROBE = FFMPEG = None


def png_chunks(path):
    """Names + a short digest of the colour-related chunks only (structural, not colour maths)."""
    out = {}
    with open(path, "rb") as f:
        if f.read(8) != b"\x89PNG\r\n\x1a\n":
            return {"_error": "not a PNG"}
        while True:
            head = f.read(8)
            if len(head) < 8:
                break
            n, typ = struct.unpack(">I4s", head)
            body = f.read(n + 4)
            key = typ.decode("ascii", "replace")
            if key in ("cICP", "iCCP", "sBIT", "pHYs", "PLTE"):
                if key == "cICP" and n >= 4:
                    out[key] = "prim=%d trc=%d matrix=%d range=%d" % tuple(body[:4])
                elif key == "sBIT" and n >= 4:
                    out[key] = "grayscale" if n == 1 else "rgb=%d/%d/%d" % tuple(body[:3])
                elif key == "iCCP":
                    name, _, rest = body.partition(b"\x00")
                    out[key] = "%s (%d B profile)" % (name.decode("latin1"), len(rest) - 1)
                else:
                    out[key] = "%d B" % n
    return out


def ffprobe_meta(path):
    p = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0", "-show_entries",
                        "stream=pix_fmt,color_primaries,color_transfer,color_space,color_range",
                        "-of", "default=nw=1", path], capture_output=True, text=True)
    vals = dict(x.split("=", 1) for x in p.stdout.split() if "=" in x)
    return " ".join("%s=%s" % (k, vals.get(k, "-")) for k in
                    ("pix_fmt", "color_primaries", "color_transfer", "color_space", "color_range")) \
        if p.returncode == 0 else "ffprobe FAIL " + p.stderr.strip()[:50]


def stored_pixels(path, tag):
    """8-bit sRGB-ish values straight out of the decoder -- what a mark-ignoring viewer shows."""
    out = os.path.join(SCRATCH, "_px_%s.raw" % tag)
    r = subprocess.run([FFMPEG, "-y", "-hide_banner", "-loglevel", "error", "-i", path,
                        "-vf", "scale=flags=neighbor", "-frames:v", "1", "-pix_fmt", "rgb24",
                        "-f", "rawvideo", out], capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        return None
    data = open(out, "rb").read()
    w = 640
    res = {}
    for name, (x, y) in CENTRES.items():
        i = (y * w + x) * 3
        res[name] = tuple(data[i:i + 3])
    os.remove(out)
    return res


def main():
    global DIR, SCRATCH, FFPROBE, FFMPEG
    tools = pe.require_tools("ffprobe", "ffmpeg")
    FFPROBE, FFMPEG = tools["ffprobe"], tools["ffmpeg"]
    DIR = pe.samples_dir("wg", pe.opt("--samples"))
    SCRATCH = pe.artifact_dir("decode")

    files = [f for f in sorted(os.listdir(DIR)) if f.endswith((".png", ".avif", ".jxl", ".tiff"))]
    if not files:
        print("⏭ 跳过：%s 下没有广色域样张" % DIR)
        return pe.EXIT_SKIP
    print("══ 元数据（外部 ffprobe / PNG 块名，非本仓库解析器）══")
    stored = {}
    unreadable = []
    for f in files:
        path = os.path.join(DIR, f)
        tag = os.path.splitext(f)[0]
        if f.endswith(".png"):
            extra = " ".join("%s[%s]" % (k, v) for k, v in png_chunks(path).items())
            print("  %-22s %s\n  %-22s %s" % (tag, ffprobe_meta(path), "", extra))
        else:
            print("  %-22s %s" % (tag, ffprobe_meta(path)))
        stored[tag] = stored_pixels(path, tag)
        if stored[tag] is None:
            unreadable.append(f)

    print("\n══ 存储像素（忽略标记的查看器就会显示这些数）══")
    print("  %-20s %s" % ("file", "  ".join("%-14s" % k for k in CENTRES)))
    for tag in files:
        t = os.path.splitext(tag)[0]
        s = stored.get(t)
        if not s:
            continue
        print("  %-20s %s" % (t, "  ".join("%-14s" % str(s[k]) for k in CENTRES)))
    print("\n  源色（生成器画进 scRGB 的线性值，编码前）: red-mid=(0.20,0,0) green-mid=(0,0.20,0) "
          "blue-mid=(0,0,0.20) red-high=(0.80,0,0) gray-18=(0.18)*3 gray-50=(0.50)*3 "
          "cyan-mid=(0,0.35,0.35) orange=(0.55,0.25,0.10)")
    print("  ⇒ 判据：sRGB 件的存储值 ≈ 源色的 sRGB 编码值；P3/BT2020 件的存储值应明显不同（矩阵转换过）。")
    print("    查看器渲染值落在哪一边，就说明它认/不认标记。")
    print("\n[RESULT] 读数 %d 份，读不出 %d 份%s"
          % (len(files), len(unreadable), "：" + "、".join(unreadable) if unreadable else " ✅"))
    return pe.EXIT_FAIL if unreadable else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
