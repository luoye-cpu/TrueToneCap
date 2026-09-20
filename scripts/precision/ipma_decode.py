"""Decode ipma under every flags interpretation, for our NVENC file and for an avifenc-produced
reference, so the flags choice is settled by what a known-good file looks like rather than by
guessing which bit means what.

纯结构 dump（ISO/IEC 14496-12 的 box 走查），不需要外部解码器，也不下结论 ——
结论要人读四种 flags 解释哪一种"刚好消费完"。

Usage: python scripts/precision/ipma_decode.py [--samples <dir>] [file.avif ...]
Exit codes: 0 全部解析 / 1 有文件缺失 / 3 SKIP (see precision_env.py)
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import struct
import sys

import precision_env as pe

DEFAULTS = ["sdr_avifnv_srgb.avif", "sdr_avifsw_srgb.avif", "hdr_avifnv.avif"]


def find_box(d, typ):
    """Locate a box by type at any nesting depth; return (offset, size)."""
    stack = [(0, len(d))]
    while stack:
        s, e = stack.pop()
        p = s
        while p + 8 <= e:
            sz = struct.unpack(">I", d[p:p + 4])[0]
            t = d[p + 4:p + 8]
            hdr = 8
            if sz == 1:
                sz = struct.unpack(">Q", d[p + 8:p + 16])[0]
                hdr = 16
            elif sz == 0 or p + sz > e:
                sz = e - p
            if t == typ:
                return p, sz
            if t in (b"meta", b"iprp", b"ipco", b"iinf", b"moov", b"trak", b"mdia", b"stbl"):
                stack.append((p + hdr + (4 if t in (b"meta", b"iinf") else 0), p + sz))
            p += sz
    return None, 0


def decode(body, wide_id, wide_idx):
    """body starts at version/flags of the ipma FullBox."""
    ver = body[0]
    flags = body[3]
    p = 4
    n = struct.unpack(">I", body[p:p + 4])[0]
    p += 4
    out = []
    for _ in range(n):
        if wide_id:
            iid = struct.unpack(">H", body[p:p + 2])[0]; p += 2
        else:
            iid = body[p]; p += 1
        cnt = body[p]; p += 1
        idx = []
        for _ in range(cnt):
            if wide_idx:
                v = struct.unpack(">H", body[p:p + 2])[0]; p += 2
                idx.append(("E" if v & 0x8000 else "-", v & 0x7FFF))
            else:
                v = body[p]; p += 1
                idx.append(("E" if v & 0x80 else "-", v & 0x7F))
        out.append((iid, idx))
    return ver, flags, n, out, p


def main():
    args = [a for a in sys.argv[1:] if a != "--samples" and not a.startswith("--samples=")]
    directory = pe.samples_dir("audit", pe.opt("--samples"))
    files = args or DEFAULTS
    missing = []
    for f in files:
        p = f if os.path.isabs(f) else os.path.join(directory, f)
        if not os.path.exists(p):
            print("missing", f)
            missing.append(f)
            continue
        d = open(p, "rb").read()
        off, sz = find_box(d, b"ipma")
        if off is None:
            print("%-24s 无 ipma" % f)
            missing.append(f)
            continue
        body = d[off + 8:off + sz]
        print("=== %-24s ipma size=%d body=%s" % (f, sz, body.hex(" ")))
        for wide_id in (0, 1):
            for wide_idx in (0, 1):
                try:
                    ver, flags, n, out, consumed = decode(body, wide_id, wide_idx)
                    tail = len(body) - consumed
                    print("    itemID=%s idx=%s  flags=0x%02X entries=%d %s%s"
                          % ("16" if wide_id else "8 ", "16" if wide_idx else "8 ",
                             flags, n, out, "  ⚠ 尾部剩 %d 字节" % tail if tail else "  ✅ 刚好消费完"))
                except Exception as ex:
                    print("    itemID=%s idx=%s  解析失败 %s" % (wide_id, wide_idx, ex))
        print()
    print("[RESULT] 解析 %d 份，缺失/无 ipma %d 份" % (len(files) - len(missing), len(missing)))
    return pe.EXIT_FAIL if missing else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
