"""判据：AVIF 的 av1C 记录必须与【码流自己说的】一致（色度/位深/mono）。

为什么需要它（2026-09-20，交接 §32.9-4）：`IvfWriter` 自己走位解析 AV1 序列头，再把
profile / 位深 / mono / chroma_subsampling_x/y / chroma_sample_position 写进 ISOBMFF 的
`av1C` 记录。本仓库此前**没有任何一条判据核对过"这份记录 vs 码流真身"** —— 于是
`ParseAv1SequenceHeader` 把 profile 0（Main，规范规定恒 4:2:0 且码流里【不写】subsampling 位）
读成"该读两位"，读到的是 chroma_sample_position 的 00 ⇒ 三份硬件 AVIF 一律被标成 **4:4:4**，
avifdec 读我们的 av1C 就报 YUV444，而 ffmpeg 解同一条码流报 yuv420p。
容器说谎，采信容器的读取端就会按错的颜色平面布局处理像素。

判法：容器侧的 av1C 由本脚本按 ISO/IEC 14496-15 的位布局自己解（不 import 仓库里的解析器）；
码流侧的"真身"由 **ffprobe/ffmpeg 的 AV1 解码器**报的 pix_fmt 给出 —— 那是另一家实现读
序列头得到的结论。两者必须一致。缺 ffprobe ⇒ 跳过（不冒充通过）；样张里没有 AVIF ⇒ 同样跳过。

用法: py scripts/precision/av1c_vs_bitstream.py [--samples 目录]
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import os
import subprocess as sp
import sys

import precision_env as pe

# pix_fmt → (mono, chroma_x, chroma_y)（AV1 语义：4:2:0 = 1,1；4:2:2 = 1,0；4:4:4 = 0,0）
PIX_FMT = {
    "yuv420p": (0, 1, 1), "yuv420p10le": (0, 1, 1), "yuv420p12le": (0, 1, 1),
    "yuv422p": (0, 1, 0), "yuv422p10le": (0, 1, 0), "yuv422p12le": (0, 1, 0),
    "yuv444p": (0, 0, 0), "yuv444p10le": (0, 0, 0), "yuv444p12le": (0, 0, 0),
    "gray": (1, 1, 1), "gray10le": (1, 1, 1), "gray12le": (1, 1, 1),
}


def boxes(b, s, e):
    out, off = [], s
    while off + 8 <= e:
        sz = int.from_bytes(b[off:off + 4], "big")
        typ = b[off + 4:off + 8].decode("latin1")
        head = 8
        if sz == 1:
            sz = int.from_bytes(b[off + 8:off + 16], "big")
            head = 16
        if sz < 2 or off + sz > e:
            break
        out.append((typ, off, sz, head))
        off += sz
    return out


def find(b, typ, s, e):
    return next((x for x in boxes(b, s, e) if x[0] == typ), None)


def read_av1c(path):
    """解 av1C 的 4 字节记录（ISO/IEC 14496-15 §5.4.2）。"""
    b = open(path, "rb").read()
    meta = find(b, "meta", 0, len(b))
    if not meta:
        return None, "没有 meta 盒"
    iprp = find(b, "iprp", meta[1] + meta[3] + 4, meta[1] + meta[2])   # meta/iprp 是 FullBox
    if not iprp:
        return None, "没有 iprp 盒"
    ipco = find(b, "ipco", iprp[1] + 8, iprp[1] + iprp[2])              # ipco/ipma 是普通 Box
    if not ipco:
        return None, "没有 ipco 盒"
    a = find(b, "av1C", ipco[1] + 8, ipco[1] + ipco[2])
    if not a:
        return None, "没有 av1C 盒"
    body = b[a[1] + 8:a[1] + a[2]]
    if len(body) < 4:
        return None, f"av1C 只有 {len(body)}B"
    if body[0] != 0x81:
        return None, f"av1C 首字节不是 marker|version=0x81（实为 0x{body[0]:02x}）"
    profile, level = (body[1] >> 5) & 7, body[1] & 0x1F
    hbd, twob, mono = (body[2] >> 6) & 1, (body[2] >> 5) & 1, (body[2] >> 4) & 1
    cx, cy, pos = (body[2] >> 3) & 1, (body[2] >> 2) & 1, body[2] & 3
    depth = 12 if (hbd and twob) else (10 if hbd else 8)
    return dict(profile=profile, level=level, bitDepth=depth, mono=bool(mono),
                x=cx, y=cy, pos=pos), ""


def probe_pix_fmt(ffprobe, path):
    r = sp.run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=pix_fmt", "-print_format", "json", path],
               capture_output=True, text=True)
    st = (pe_json(r.stdout).get("streams") or [{}])[0]
    return st.get("pix_fmt") or ""


def pe_json(text):
    import json
    try:
        return json.loads(text or "{}")
    except Exception:
        return {}


def main() -> int:
    d = pe.samples_dir("sweep", pe.opt("--samples"))
    avifs = sorted(f for f in os.listdir(d) if f.lower().endswith(".avif"))
    if not avifs:
        print(f"SKIP: {d} 下没有 AVIF 样张")
        return pe.EXIT_SKIP
    ffprobe = pe.find_tool("ffprobe")
    if not ffprobe:
        print("SKIP: 找不到 ffprobe ⇒ 无法拿到『码流真身』这一侧的独立读数")
        return pe.EXIT_SKIP

    bad = 0
    for f in avifs:
        p = os.path.join(d, f)
        rec, err = read_av1c(p)
        if rec is None:
            print(f"  ❌ {f}: av1C 不可解析 —— {err}")
            bad += 1
            continue
        pix = probe_pix_fmt(ffprobe, p)
        want = PIX_FMT.get(pix)
        if want is None:
            print(f"  ❌ {f}: ffprobe 报 pix_fmt={pix!r}，本脚本不认识这一档（不要猜，报出来）")
            bad += 1
            continue
        mono, x, y = want
        depth_want = 12 if pix.endswith("p12le") else (10 if pix.endswith("p10le") else 8)
        got = (1 if rec["mono"] else 0, rec["x"], rec["y"])
        name = ("mono" if mono else
                "4:2:0" if (x, y) == (1, 1) else
                "4:2:2" if (x, y) == (1, 0) else
                "4:4:4" if (x, y) == (0, 0) else f"({x},{y})")
        probs = []
        if got != (mono, x, y):
            probs.append(f"色度：av1C={got} vs 码流(ffmpeg)={mono,x,y}")
        if rec["bitDepth"] != depth_want:
            probs.append(f"位深：av1C={rec['bitDepth']} vs 码流={depth_want}")
        if probs:
            print(f"  ❌ {f}: " + "；".join(probs) + f"  [profile={rec['profile']} pix_fmt={pix}]")
            bad += 1
        else:
            print(f"  ✅ {f}: av1C 与码流一致（{depth_want}bit {name} profile={rec['profile']}）")

    print("结论:", "av1C 与码流真身一致" if bad == 0 else f"{bad} 份文件的容器颜色记录与码流不符")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
