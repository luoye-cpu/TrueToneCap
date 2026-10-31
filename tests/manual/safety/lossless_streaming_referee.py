# -*- coding: utf-8 -*-
"""流式无损件的外部裁判（PNG 多条 IDAT / 分条 16-bit TIFF）。

为什么必须存在：审查 §3 表里 ① 与 ③ 两条判据当时只有"产品自己的字节解释自己的字节"——
自洽 CRC 与不判别的端序臂。改形状之后这两格仍是空的，所以这一格只借外部读者：
zlib.crc32 / Pillow / tifffile。

四条臂（全绿才谈得上通过）+ 三条正控（必须红，否则判据没牙齿）：
  P1 Pillow 解出像素 == 源头 BGRA 转 RGBA           （正控 C1：改一个样本必须变红）
  P2 每条 chunk 的 CRC 由 zlib.crc32 独立复算       （正控 C2：翻文件里一个字节必须抓到）
  P3 IDAT 条数>=2、除末条外恰 64 KiB、拼接后能解压   （无正控：这条本身就是形状判据）
  T1 tifffile 解 16-bit 样本 == 源×257 的 RGBA       （正控 C3：把样本按大端误读必须变红）
退出码：0 全绿 / 1 有红 / 3 夹具缺失（未判，不许当通过）。
"""
import io
import os
import struct
import sys
import zlib

W, H = 320, 240
PASS, FAIL = [], []


def say(ok, name, detail):
    (PASS if ok else FAIL).append(name)
    print(("  ✅ " if ok else "  ❌ ") + name + " — " + detail)


def png_chunks(buf):
    out, p = [], 8
    while p + 8 <= len(buf):
        ln = struct.unpack(">I", buf[p:p + 4])[0]
        typ = buf[p + 4:p + 8]
        data = buf[p + 8:p + 8 + ln]
        crc = struct.unpack(">I", buf[p + 8 + ln:p + 12 + ln])[0]
        out.append((typ, data, crc, p))
        p += 12 + ln
        if typ == b"IEND":
            break
    return out


def main():
    d = sys.argv[1] if len(sys.argv) > 1 else os.path.expandvars(
        r"%TEMP%\TrueToneCap_EncTest")
    png_p = os.path.join(d, "png_multi_idat.png")
    src_p = os.path.join(d, "png_multi_idat.src.bin")
    tif_p = os.path.join(d, "tiff16_direct_referee.tif")
    tsrc_p = os.path.join(d, "tiff16_direct_referee.src.bin")
    missing = [p for p in (png_p, src_p, tif_p, tsrc_p) if not os.path.exists(p)]
    if missing:
        print("★ 夹具缺失 ⇒ 未判（先跑 --encoding-tests 生成夹具）：" + ", ".join(missing))
        return 3
    print(f"══ 外部裁判 · 夹具目录 {d} ══")

    import numpy as np
    from PIL import Image
    import tifffile as tf

    # ── 源头：产品当年喂进去的那份 BGRA8 / BGRA16（由测试落盘，裁判不再调产品）──
    bgra8 = np.fromfile(src_p, dtype=np.uint8).reshape(H, W, 4)
    exp_rgba = bgra8[:, :, [2, 1, 0, 3]]                       # BGRA → RGBA
    bgra16 = np.fromfile(tsrc_p, dtype=np.uint8).reshape(H, W, 4, 2)

    # ── P1 Pillow 像素 ──
    im = Image.open(png_p)
    im.load()
    got = np.asarray(im)
    diff = int(np.abs(got.astype(int) - exp_rgba.astype(int)).max())
    n = int((got != exp_rgba).sum())
    say(diff == 0 and got.shape == exp_rgba.shape, "P1 Pillow 解出像素与源头逐样本相同",
        f"mode={im.mode} shape={got.shape} max|Δ|={diff} 不同={n}/{got.size}")
    bad = exp_rgba.copy()
    bad[7, 3, 1] ^= 0x40                                        # 只改一个样本
    say(int(np.abs(got.astype(int) - bad.astype(int)).max()) > 0,
        "C1 正控：期望改一个样本就必须对不上", "尺子能分 ⇒ P1 不是恒等式")

    # ── P2 独立 CRC（zlib.crc32，覆盖 类型码+数据）──
    buf = open(png_p, "rb").read()
    cs = png_chunks(buf)
    mism = [t for t, data, crc, off in cs
            if zlib.crc32(t + data) & 0xFFFFFFFF != crc]
    say(len(cs) >= 3 and not mism, "P2 每条 chunk 的 CRC 由 zlib 独立复算全对",
        f"{len(cs)} 条 chunk，复算不一致 {len(mism)} 条 {mism}")
    flip = bytearray(buf)
    flip[cs[0][3] + 8 + 2] ^= 0x01                              # 翻 IHDR 数据里的一个字节
    mism2 = [t for t, data, crc, off in png_chunks(bytes(flip))
             if zlib.crc32(t + data) & 0xFFFFFFFF != crc]
    say(bool(mism2), "C2 正控：翻掉一个字节，独立 CRC 必须抓到",
        f"抓到 {len(mism2)} 条 {mism2}（抓不到=这条判据是装饰）")

    # ── P3 分块形状与拼接可解 ──
    idats = [c for c in cs if c[0] == b"IDAT"]
    lens = [len(c[1]) for c in idats]
    say(len(idats) >= 2, "P3 产物跨多条 IDAT（只有一条时这条判据看不见东西）",
        f"{len(idats)} 条，载荷 {lens}")
    say(all(x == 64 * 1024 for x in lens[:-1]), "P3 除末条外每条载荷恰 64 KiB",
        "/".join(str(x) for x in lens))
    payload = b"".join(c[1] for c in idats)
    try:
        raw = zlib.decompress(payload)
        ok = len(raw) == H * (1 + W * 4)
    except Exception as ex:
        raw, ok = b"", False
        print(f"     （解压抛错 {type(ex).__name__}: {ex}）")
    say(ok, "P3 各条载荷拼接后是一条完整 zlib 流，解压长度=各行(1+宽×4)",
        f"解压 {len(raw)}，应为 {H * (1 + W * 4)}")

    # ── T1 tifffile 解 16-bit（真 16-bit 输入的夹具：样本非回文，端序臂才有牙齿）──
    t = tf.imread(tif_p)
    # 产品口径：输入每通道【大端】BGRA16，输出每通道【小端】RGBA16 ⇒ 期望直接按字节序换过来算
    v_in = (bgra16[:, :, :, 0].astype(np.uint16) << 8) | bgra16[:, :, :, 1].astype(np.uint16)
    exp16 = v_in[:, :, [2, 1, 0, 3]]
    palin = float(((exp16 >> 8) == (exp16 & 0xFF)).mean())
    say(t.shape == exp16.shape and t.dtype == np.uint16, "T1 tifffile 解出形状与类型",
        f"{t.shape} {t.dtype}；夹具回文占比={palin:.4f}")
    dt = int(np.abs(t.astype(np.int64) - exp16.astype(np.int64)).max())
    say(dt == 0, "T1 tifffile 解出的 16-bit 样本与独立算出的期望逐样本相同",
        f"max|Δ|={dt} 不同={int((t != exp16).sum())}/{t.size}")
    swapped = ((exp16 & 0xFF).astype(np.uint16) << 8) | ((exp16 >> 8).astype(np.uint16))
    ds = int(np.abs(t.astype(np.int64) - swapped.astype(np.int64)).max())
    say(palin < 0.9 and ds > 0, "C3 正控：把期望按大端误读必须对不上（端序臂有牙齿）",
        f"回文占比={palin:.4f}（>=0.9 时 LE/BE 读出来一样，这条臂必然假绿——10-31 栽过），"
        f"误读后 max|Δ|={ds}")
    with open(tif_p, "rb") as f:
        hdr = f.read(2)
    say(hdr == b"II", "T1 TIFF 头部是 II（小端），与样本文本声明同向", repr(hdr))

    print(f"\n判词：{'✅ 全绿' if not FAIL else '❌ 有红：' + ', '.join(FAIL)}"
          f"（绿 {len(PASS)} 条 / 红 {len(FAIL)} 条）")
    return 0 if not FAIL else 1


if __name__ == "__main__":
    sys.exit(main())
