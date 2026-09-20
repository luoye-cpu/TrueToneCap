"""SVT-AV1 vs libaom across the speed axis, at the pipeline's real settings.

Why: the PickAuto decision (2026-09-19, "libaom before SVT") rests on one point — `-s 6`,
which is what SpeedArg hardcodes. That is the only preset the product can ever ask for,
but the conclusion was written as a general claim about SVT. This sweeps the axis so the
record says either "SVT never wins here" or "SVT wins below -s N, revisit if SpeedArg moves".

Inputs are production-shaped: a 16-bit PNG (what PrepareSdr16Pixels feeds) with
--yuv 420 --depth 10, exactly the args NativeAvifEncoder.EncodeFile builds.

这是【基准】不是门禁断言：耗时随机器变化，脚本只报数不判优劣，跑完即退 0。

Usage: python scripts/precision/svt_preset_sweep.py [width] [runs]
Exit codes: 0 跑完 / 3 SKIP（avifenc / ffmpeg 探测不到，见 precision_env.py）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import statistics
import subprocess
import sys
import time

import precision_env as pe

SPEEDS = [0, 2, 4, 6, 8]


def main():
    tools = pe.require_tools("avifenc", "ffmpeg", "ffprobe")
    exe = tools["avifenc"]          # 默认就是仓库内 Resources 那份（产品实际用的构建）
    w = int(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else 1280
    runs = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    tmp = pe.artifact_dir("svt_preset_sweep")

    def png16():
        out = os.path.join(tmp, "in16.png")
        h = w * 9 // 16
        subprocess.run([tools["ffmpeg"], "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                        "-i", "testsrc2=size=%dx%d:rate=1:duration=1" % (w, h),
                        "-pix_fmt", "rgba64le", out], check=True)
        return out

    def readback(path):
        p = subprocess.run([tools["ffprobe"], "-v", "error", "-select_streams", "v:0",
                            "-show_entries", "stream=pix_fmt", "-of", "default=nw=1:nk=1", path],
                           capture_output=True, text=True)
        return p.stdout.strip() or "?"

    def one(codec, speed, src):
        out = os.path.join(tmp, "%s_%d.avif" % (codec, speed))
        r = subprocess.run([exe, "-q", "80", "-s", str(speed), "--codec", codec,
                            "--yuv", "420", "--depth", "10", src, out],
                           capture_output=True, text=True)
        if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) < 100:
            return None, None, "FAIL"
        return None, os.path.getsize(out) // 1024, readback(out)

    src = png16()
    print("avifenc: %s" % exe)
    print("%dx%d 16-bit PNG 输入, -q 80 --yuv 420 --depth 10, %d runs/点（取中位）" % (w, w * 9 // 16, runs))
    print("%-6s %8s %8s %8s   %s" % ("-s", "aom", "svt", "svt-aom", "体积 / ffprobe 读回"))
    rows = {}
    for speed in SPEEDS:
        for codec in ("aom", "svt"):
            ts = []
            kb = 0
            info = ""
            for _ in range(runs):
                t = time.time()
                _, sz, info = one(codec, speed, src)
                dt = time.time() - t
                if sz is None:
                    ts = []
                    info = "FAIL"
                    break
                ts.append(dt)
                kb = sz
            rows[(codec, speed)] = (statistics.median(ts) if ts else None, kb, info)
        a = rows[("aom", speed)]
        s = rows[("svt", speed)]
        delta = "n/a" if a[0] is None or s[0] is None else "%+.1f%%" % ((s[0] - a[0]) / a[0] * 100)
        fmt = lambda t: "  FAIL " if t[0] is None else "%8.2fs" % t[0]
        print("%-6s %8s %8s %8s   aom %dKB/%s  svt %dKB/%s"
              % ("-s %d" % speed, fmt(a), fmt(s), delta, a[1], a[2], s[1], s[2]))
    print("\n注: 单帧、合成内容、固定 -q 80；比较的是相对趋势，不是绝对耗时。artifacts: %s" % tmp)
    print("[RESULT] %d 个 preset × 2 编码器实测完成（基准，不判优劣）" % len(SPEEDS))
    return pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
