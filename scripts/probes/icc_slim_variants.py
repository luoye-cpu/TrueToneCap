"""实验：TRC 共享化之后，更激进的曲线形态能省多少、lcms2 认不认（2026-11-12）。

背景（用户拍板「TRC 共享 offset + 测一下更激进的」）：
  自产 ICC 的 rTRC/gTRC/bTRC 原先把同一份曲线各写一份（SDR 件 2100B 的 75% 是重复曲线）。
  产品已改为三 tag 共享同一 offset（A 档，SDR 件 1052B）。本实验在【A 档产物字节】之上
  手术出两个更激进的变体并让 lcms2（经 Pillow ImageCms）实测：
    B 档：64 点 curv（共享）—— v2 规范内的激进采样，量化误差由 lcms 换算实测；
    C 档：para type-3 参数化 sRGB 曲线（共享）—— 数学上精确，但 'para' 是 ICC v4 的
          tag 类型，写进 v2.1 profile 属规范越界（lcms/skcms/WIC/Android 都解析，
          老读手未验证）⇒ 只报数据，不进产品。
  PQ 件不在变体范围：ST.2084 不是幂函数形，para 表达不了，4096 点 curv 是它的下限形态
  （共享后 8696B，--icc-check 实测）。

判据：lcms 双向换算（我们的 profile ↔ lcms 内置 sRGB）在灰阶 256 ramp、暗端 0..40、
16³ RGB 网格上的最大 8-bit 偏差 ≤1/255 = 通过。输入由 --color-tests 的 TIFF 夹具
（tiff_srgb8_*.tif，Tag 34675）现场产出，缺它 SKIP(3)。

用法: py scripts/probes/icc_slim_variants.py
退出码: 0 有结论；3 缺输入。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import glob
import io
import os
import struct
import sys
import tempfile

from PIL import Image, ImageCms

COLOR_OUT = os.path.join(tempfile.gettempdir(), "TrueToneCap_ColorTest")
SRGB_TIFF_GLOB = os.path.join(COLOR_OUT, "tiff_srgb8_*.tif")
OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "..", ".gate-probe-out", "icc_slim")

TAG_RTRC, TAG_GTRC, TAG_BTRC = 0x72545243, 0x67545243, 0x62545243


def icc_from_tiff(path: str) -> bytes:
    data = open(path, "rb").read()
    e = "<" if data[:2] == b"II" else ">"
    ifd = struct.unpack(e + "I", data[4:8])[0]
    n = struct.unpack(e + "H", data[ifd:ifd + 2])[0]
    for i in range(n):
        ent = ifd + 2 + i * 12
        tag, typ, cnt = struct.unpack(e + "HHI", data[ent:ent + 8])
        if tag != 34675:
            continue
        size = cnt * {1: 1, 2: 1, 7: 1}.get(typ, 4)
        off = struct.unpack(e + "I", data[ent + 8:ent + 12])[0]
        return data[off:off + size]
    raise RuntimeError("无 Tag 34675")


def entries_of(icc: bytes):
    n = struct.unpack(">I", icc[128:132])[0]
    out = []
    for i in range(n):
        sig, off, size = struct.unpack(">III", icc[132 + i * 12:132 + i * 12 + 12])
        out.append([sig, off, size])
    return out


def rebuild_with_trc(base: bytes, payload: bytes) -> bytes:
    """把 A 档（TRC 共享且是最后一组 tag）里的三份 TRC 指向同一份新 payload。"""
    ents = entries_of(base)
    trc_ents = [e for e in ents if e[0] in (TAG_RTRC, TAG_GTRC, TAG_BTRC)]
    trc_off = min(e[1] for e in trc_ents)            # TRC 块在构建器布局里恒在最后
    body = bytearray(base[:trc_off])
    pad = (-len(payload)) % 4
    body += payload + b"\0" * pad
    total = trc_off + len(payload) + pad
    out = bytearray(total)
    out[:trc_off] = base[:trc_off]
    out[trc_off:trc_off + len(payload)] = payload   # ⚠ payload 必须真写进 out（首版漏了这行，
    #   TRC 数据区全 0 ⇒ lcms 开 profile 能过、建 transform 才炸——"读开"与"用"是两道门）
    for e in ents:
        sig, off, size = e
        if sig in (TAG_RTRC, TAG_GTRC, TAG_BTRC):
            off, size = trc_off, len(payload)
        struct.pack_into(">III", out, 132 + ents.index(e) * 12, sig, off, size)
    struct.pack_into(">I", out, 0, total)            # 回填 profile 尺寸
    return bytes(out)


def curv_payload(points: int) -> bytes:
    vals = []
    for i in range(points):
        v = i / (points - 1)
        lin = v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
        vals.append(min(65535, round(lin * 65535)))
    p = struct.pack(">4sII", b"curv", 0, points)
    p += b"".join(struct.pack(">H", v) for v in vals)
    return p


def para3_payload() -> bytes:
    """ICC para type-3：y=(ax+b)^g for x≥d；y=cx for 0≤x<d —— sRGB EOTF 的精确形。"""
    def s15f16(x: float) -> int:
        return round(x * 65536)
    g, a, b, c, d = 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045
    return struct.pack(">4sIHHIIIII", b"para", 0, 3, 0,
                       s15f16(g), s15f16(a), s15f16(b), s15f16(c), s15f16(d))


def measure(label: str, icc: bytes, builtin_bytes: bytes, ramp_img, grid_img, dark_img):
    prof = ImageCms.getOpenProfile(io.BytesIO(icc))
    builtin = ImageCms.getOpenProfile(io.BytesIO(builtin_bytes))
    name = ImageCms.getProfileName(prof).strip()
    cprt = ImageCms.getProfileCopyright(prof).strip()

    def max_delta(src, dst, img):
        t = ImageCms.buildTransform(src, dst, "RGB", "RGB",
                                    renderingIntent=ImageCms.Intent.PERCEPTUAL)
        out = ImageCms.applyTransform(img, t)
        pa, pb = img.load(), out.load()
        return max(abs(pa[x, y][c] - pb[x, y][c])
                   for y in range(img.height) for x in range(img.width) for c in range(3))

    d_fwd = max(max_delta(prof, builtin, im) for im in (ramp_img, grid_img, dark_img))
    d_bwd = max(max_delta(builtin, prof, im) for im in (ramp_img, grid_img, dark_img))
    ok = d_fwd <= 1 and d_bwd <= 1
    print(f"  {label:<28} {len(icc):>6} B  fwd Δmax={d_fwd}  bwd Δmax={d_bwd}  "
          f"{'✅ ≤1/255' if ok else '❌ 超差'}  | {name} | cprt={' ours' if cprt.startswith('Copyright 2026') else cprt[:30]}")
    return len(icc), ok


def main() -> int:
    tiffs = sorted(glob.glob(SRGB_TIFF_GLOB), key=os.path.getmtime, reverse=True)
    if not tiffs:
        print(f"[SKIP] 找不到 {SRGB_TIFF_GLOB}\n"
              "       先跑: TrueToneCap.Test.exe --color-tests")
        return 3
    # ⚠ 按修改时间取最新：夹具名带随机后缀，旧轮次的文件会留在目录里，
    #   按名字排序会拿到过期字节（本实验首跑就栽在这上面）。

    base = icc_from_tiff(tiffs[0])
    # ICC 版本号是 4 字节 0xMMmmbb00（本例 02100000 = v2.1.0）；
    # 只取 base[8]、base[9] 会把 0x10 打成十进制 16 显示成 "v2.16"，别被误导。
    ver = "v%d.%d.%d" % (base[8], base[9] >> 4, base[9] & 0xF)
    print(f"底图：{os.path.basename(tiffs[0])} → 产品 sRGB profile {len(base)}B "
          f"(version={ver})")

    builtin = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()

    # 判据面：灰阶 256 ramp + 暗端（曲线最陡处）+ 16³ 覆盖色度空间的网格
    ramp = Image.new("RGB", (256, 1))
    for i in range(256):
        ramp.putpixel((i, 0), (i, i, i))
    dark = Image.new("RGB", (64, 1))
    for i in range(64):
        v = round(i * 40 / 63)
        dark.putpixel((i, 0), (v, v, v))
    grid = Image.new("RGB", (4096, 1))
    n = 0
    for r in range(16):
        for g in range(16):
            for b in range(16):
                grid.putpixel((n, 0), (r * 17, g * 17, b * 17))
                n += 1

    print("\n── lcms2 实测（双向换算最大 8-bit 偏差；≤1/255 = 通过）──")
    r = {}
    r["A 产品（256 点 curv 共享）"] = measure("A 产品（256 点 curv 共享）", base, builtin, ramp, grid, dark)
    b = rebuild_with_trc(base, curv_payload(64))
    r["B 64 点 curv 共享"] = measure("B 64 点 curv 共享", b, builtin, ramp, grid, dark)
    c = rebuild_with_trc(base, para3_payload())
    r["C para type-3 共享"] = measure("C para type-3 共享", c, builtin, ramp, grid, dark)

    os.makedirs(OUT_DIR, exist_ok=True)
    for fname, data in (("variant_b_64curv.icc", b), ("variant_c_para3.icc", c)):
        with open(os.path.join(OUT_DIR, fname), "wb") as f:
            f.write(data)

    print("\n── 结论 ──")
    print(f"  A→B 省字节: {r['A 产品（256 点 curv 共享）'][0] - r['B 64 点 curv 共享'][0]} B"
          f"；A→C 省字节: {r['A 产品（256 点 curv 共享）'][0] - r['C para type-3 共享'][0]} B")
    print("  C 档规范警示：'para' 是 ICC v4 的 tag 类型，写进 v2.1 profile 属规范越界"
          "（实测 lcms 解析无碍）；要正当地用它须连 desc/cprt 一起迁 v4（'mluc'），是独立工程。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
