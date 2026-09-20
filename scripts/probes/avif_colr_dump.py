"""朴素扫描 ISOBMFF 里的 colr(nclx) 盒，把四个字段按规范原样读出来。

为什么手写而不靠 ffprobe：判据说"ffprobe 读回什么"，但这里要问的是
"容器里到底写了什么"——ffprobe 对某些值会直接不打印（例如 matrix），
只看它的输出会把"没写"和"写了但被省略"混为一谈。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import struct
import sys

DIRS = [os.path.join("artifacts", "precision", k) for k in ("cicp", "sweep", "ramp")]


def dump_colr(path):
    d = open(path, "rb").read()
    out = []
    i = 0
    while True:
        i = d.find(b"colrnclx", i)
        if i < 0:
            return out
        b = d[i + 8:i + 15]
        if len(b) < 7:
            return out
        prim, tx, mtx = struct.unpack(">HHH", b[0:6])
        # 第 7 字节最高位 = full_range_flag，其余 7 位是 reserved(须为 0)
        rng = b[6]
        out.append((prim, tx, mtx, rng))
        i += 4


def probe(ffprobe, path):
    import json
    import subprocess
    r = subprocess.run([ffprobe, "-v", "error", "-select_streams", "v:0",
                        "-show_entries", "stream=pix_fmt,color_primaries,color_transfer,"
                        "color_matrix,color_range", "-print_format", "json", path],
                       capture_output=True, text=True)
    s = (json.loads(r.stdout or "{}").get("streams") or [{}])[0]
    return s


def main():
    sys.path.insert(0, os.path.join("scripts", "precision"))
    from precision_env import find_tool
    ff = find_tool("ffprobe")

    hits = []
    for dd in DIRS:
        if not os.path.isdir(dd):
            continue
        for f in sorted(os.listdir(dd)):
            if f.endswith(".avif"):
                hits.append(os.path.join(dd, f))

    if not hits:
        print("没有 .avif 样张")
        return 1

    # 尺寸决定走不走 NVENC（MinNvencDimension=128），一并列出来免得把回退产物当硬件产物
    for p in hits:
        cols = dump_colr(p)
        s = probe(ff, p) if ff else {}
        size = os.path.getsize(p)
        print(f"{os.path.relpath(p):46s} {size:7d}B  colr(nclx)={cols}  "
              f"ffprobe: prim={s.get('color_primaries')} tx={s.get('color_transfer')} "
              f"mtx={s.get('color_matrix')} range={s.get('color_range')} pix={s.get('pix_fmt')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
