"""SVT-AV1 capability matrix: chroma x bit-depth, judged by what ffprobe reads back.

Why a matrix: AvifEncoderProbe measures chroma at 8-bit and depths at 4:2:0 independently,
which cannot see a combination-specific failure (SVT-AV1 12-bit 4:4:4 errors out at
"Encoding of color planes failed" while 10-bit 4:4:4 works). The static capability table is
per-axis, so this script supplies the missing cross-axis evidence.

这是【实测矩阵】不是门禁断言：MISMATCH/FAIL 是编码器能力的事实记录（例如 SVT 不支持 12-bit
4:4:4 是预期结论），所以跑完即退 0；只有 avifenc/ffmpeg 探测不到才 SKIP。

Usage: python scripts/precision/svt_caps_matrix.py [path-to-avifenc]
Exit codes: 0 跑完（结论看表格）/ 3 SKIP（见 precision_env.py）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import subprocess
import sys

import precision_env as pe

PIX = {"8": "rgba", "10": "rgb48le", "12": "rgba64le"}


def main():
    tools = pe.require_tools("avifenc", "ffprobe", "ffmpeg")
    new = (sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else tools["avifenc"])
    tmp = pe.artifact_dir("svt_caps_matrix")

    def png_for(kind):
        """Production-shaped inputs: the SDR byte[] path feeds an 8-bit PNG, and every
        >=10-bit request feeds the 16-bit PNG from PrepareSdr16Pixels (see FormatEncoders)."""
        out = os.path.join(tmp, "in_%s.png" % kind)
        if not os.path.exists(out):
            pix = {"8": "rgba", "16": "rgba64le"}[kind]
            subprocess.run([tools["ffmpeg"], "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                            "-i", "testsrc=size=256x256:rate=1:duration=1", "-pix_fmt", pix, out],
                           check=True)
        return out

    def ffprobe(path):
        p = subprocess.run([tools["ffprobe"], "-v", "error", "-select_streams", "v:0",
                            "-show_entries", "stream=pix_fmt", "-of", "default=nw=1:nk=1", path],
                           capture_output=True, text=True)
        return p.stdout.strip() or "unreadable"

    def cell(codec, chroma, depth):
        out = os.path.join(tmp, "%s_%s_%s.avif" % (codec, chroma, depth))
        r = subprocess.run([new, "-q", "60", "-s", "6", "--codec", codec, "--yuv", chroma,
                            "--depth", depth, png_for("8" if depth == "8" else "16"), out],
                           capture_output=True, text=True)
        if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) < 100:
            msg = " ".join((r.stderr or r.stdout).split())
            tail = msg[msg.rfind("ERROR"):] if "ERROR" in msg else msg[-60:]
            return "FAIL: " + (tail[:58] or "no output")
        got = ffprobe(out)
        want = {"420": "yuv420p", "422": "yuv422p", "444": "yuv444p"}[chroma]
        want += {8: "", 10: "10le", 12: "12le"}[int(depth)]
        return ("OK   " if got == want else "MISMATCH ") + got

    print("avifenc: %s" % new)
    print(subprocess.run([new, "--version"], capture_output=True, text=True).stdout.strip().splitlines()[0])
    print()
    print("codec        8-bit                  10-bit                 12-bit")
    print("-" * 82)
    cells = []
    for codec in ("svt", "aom"):
        for chroma in ("420", "422", "444"):
            row = (cell(codec, chroma, "8"), cell(codec, chroma, "10"), cell(codec, chroma, "12"))
            cells += row
            print("%-4s %-5s  | %-22s | %-22s | %s" % (codec, chroma, *row))
        print()
    print("\n判据: ffprobe 读回 pix_fmt 必须等于请求的色度+位深（'参数被接受'不等于'真的生效'）")
    print("artifacts: %s" % tmp)
    print("[RESULT] 18 格实测完成：OK %d / MISMATCH %d / FAIL %d（MISMATCH 与 FAIL 是能力结论，见交接 §23）"
          % (sum(c.startswith("OK") for c in cells),
             sum(c.startswith("MISMATCH") for c in cells),
             sum(c.startswith("FAIL") for c in cells)))
    return pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
