"""tiff_png_referee.py —— 无损容器的【外部解码器】裁判（PIL / tifffile）。

为什么必须有这一份：src/TrueToneCap.Test/EncodingIntegrationTests.cs 的
Test_LosslessShapesAndExternalFixtures 会把四件夹具落到
%TEMP%\\TrueToneCap_EncTest（png_multi_idat.png/.src.bin、tiff16_referee.tif、
tiff16_direct_referee.tif/.src.bin），注释里写明"夹具交给外部裁判
(scripts/precision/tiff_png_referee.py)"——但该脚本此前并不在仓库里，
max|Δ|=0 这类结论只能采信为历史运行记录，本 checkout 不可复现（2026-11-01 审查发现）。
本文件按测试注释的判据要求重建，判据【只信外部实现】（PIL 解 PNG、tifffile 解 TIFF），
绝不 import 本仓库自己的容器解析代码。

三条判据（对应测试注释的三件事）：
  A. PNG 多 IDAT —— PIL 独立解码 png_multi_idat.png（PIL 自己拼接多条 IDAT），
     与 png_multi_idat.src.bin 的原始像素逐字节相等（通道置换 RGBA/BGRA 之一）。
  B. TIFF16 (v×257) —— tifffile 读 tiff16_referee.tif：SamplesPerPixel=4、
     ExtraSamples(tag 338)=2（unassociated alpha）、样本值 == 8-bit 源 ×257。
     注意这份夹具每个 uint16 都是 0xNNNN 回文（v*257 = v<<8|v），端序臂在它上
     【必然不判别】（10-31 实测）——所以它只判数值与 338，不判端序。
  C. TIFF16 直入（tiff16_direct_referee.tif）—— 真 16-bit 输入、每通道大端两字节。
     裁判先从 src.bin 统计非回文样本占比（>50% 才有端序判别力，全回文=端序臂假绿），
     再要求 tifffile 读出的 uint16 数组 == 源按【大端】解析的数组（通道置换之一），
     且 != 源按【小端】解析的数组 —— 后一条是端序判据的"有牙"证明：夹具真能把
     端序读反抓红，而不是怎么读都一样。

用法:  python scripts/precision/tiff_png_referee.py [--samples <dir>]
退出码: 0 判据全过 / 1 判据不通过（回归）/ 3 前置不满足（夹具或库缺失 ⇒ SKIP）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import os
import sys

import precision_env as pe

# 测试侧 OutDir（EncodingIntegrationTests.cs:20）。裁判默认去那里找夹具；
# --samples / TTC_PRECISION_SAMPLES 可指到别的运行留下的目录。
DEFAULT_FIXTURE_DIR = os.path.join(os.environ.get("TEMP", os.environ.get("TMP", "")),
                                   "TrueToneCap_EncTest")

try:
    import numpy as np
    import tifffile
    from PIL import Image
except Exception as ex:  # pragma: no cover - 环境缺库
    print("⏭ 跳过：外部裁判依赖库缺失（%s）。需要 python 侧 numpy / tifffile / Pillow。" % ex)
    sys.exit(pe.EXIT_SKIP)

W, H = 320, 240

_results: list[tuple[bool, str]] = []


def judge(ok: bool, what: str, detail: str) -> None:
    _results.append((ok, what))
    print("  %s %s —— %s" % ("✅" if ok else "❌", what, detail))


def load_src(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read()


def png_pixels(path: str):
    """PIL 独立解码，返回 (w, h, mode, HxWx4 uint8)。"""
    im = Image.open(path)
    im.load()
    rgb = im.convert("RGBA")
    return rgb.width, rgb.height, im.mode, np.asarray(rgb, dtype=np.uint8)


def match_with_perm(decoded: np.ndarray, src: np.ndarray) -> str | None:
    """src(HxWx4) 按哪种通道置换与解码结果全等。只可能是 RGBA 直配或 B/R 互换。"""
    for name, idx in (("RGBA", (0, 1, 2, 3)), ("BGRA", (2, 1, 0, 3))):
        if np.array_equal(decoded, src[:, :, list(idx)]):
            return name
    return None


def main() -> None:
    samples = pe.opt("--samples") or os.environ.get(pe.SAMPLES_ENV, "").strip() or DEFAULT_FIXTURE_DIR
    if not os.path.isdir(samples):
        print("⏭ 跳过：夹具目录不存在：%s" % samples)
        print("   先跑一次测试套件让 Test_LosslessShapesAndExternalFixtures 落盘夹具，")
        print("   或用 --samples / %s 指到实际目录。" % pe.SAMPLES_ENV)
        sys.exit(pe.EXIT_SKIP)

    need = ["png_multi_idat.png", "png_multi_idat.src.bin",
            "tiff16_referee.tif", "tiff16_direct_referee.tif", "tiff16_direct_referee.src.bin"]
    missing = [f for f in need if not os.path.isfile(os.path.join(samples, f))]
    if missing:
        print("⏭ 跳过：夹具不齐（缺 %s）：%s" % (", ".join(missing), samples))
        sys.exit(pe.EXIT_SKIP)

    print("── 外部裁判（tiff_png_referee）· 夹具目录 %s ──" % samples)

    # ── A. PNG 多 IDAT：PIL 独立解码并逐字节比对 ──
    png_path = os.path.join(samples, "png_multi_idat.png")
    src = np.frombuffer(load_src(os.path.join(samples, "png_multi_idat.src.bin")),
                        dtype=np.uint8).reshape(H, W, 4)
    try:
        w, h, mode, dec = png_pixels(png_path)
        perm = match_with_perm(dec, src)
        judge(w == W and h == H, "PNG 尺寸（PIL 独立解码）",
              "PIL 读得 %dx%d mode=%s" % (w, h, mode))
        judge(perm is not None, "PNG 像素逐字节相等（多 IDAT 拼接由 PIL 独立完成）",
              "通道置换=%s" % (perm or "无任何置换全等 ⇒ 像素不符"))
    except Exception as ex:
        judge(False, "PNG 像素逐字节相等（多 IDAT 拼接由 PIL 独立完成）", "PIL 解码抛错：%s" % ex)

    # ── B. TIFF16 (v×257)：数值 + ExtraSamples(338) ──
    tif_path = os.path.join(samples, "tiff16_referee.tif")
    src8 = src  # 同一份 8-bit 源（测试 :461 直接用 px 落 TIFF）
    with tifffile.TiffFile(tif_path) as tf:
        page = tf.pages[0]
        arr = page.asarray()
        spp = int(page.tags["SamplesPerPixel"].value) if "SamplesPerPixel" in page.tags else -1
        extra = int(page.tags["ExtraSamples"].value[0]) if "ExtraSamples" in page.tags else -1
        photo = int(page.tags["PhotometricInterpretation"].value) if "PhotometricInterpretation" in page.tags else -1
        bits = tuple(page.tags["BitsPerSample"].value) if "BitsPerSample" in page.tags else ()
    judge(spp == 4, "TIFF16(v×257) SamplesPerPixel=4", "实得 %d" % spp)
    judge(extra == 2, "TIFF16(v×257) ExtraSamples(338)=2（unassociated alpha）", "实得 %d" % extra)
    judge(photo == 2, "TIFF16(v×257) PhotometricInterpretation=2 (RGB)", "实得 %d" % photo)
    judge(bits == (16, 16, 16, 16), "TIFF16(v×257) BitsPerSample=16×4", "实得 %s" % (bits,))
    if arr.shape == (H, W, 4) and arr.dtype == np.uint16:
        expect = src8[:, :, :].astype(np.uint16) * 257
        perm = match_with_perm(arr, expect)
        judge(perm is not None, "TIFF16(v×257) 样本值 == 源×257（tifffile 独立解压）",
              "通道置换=%s" % (perm or "无任何置换全等"))
    else:
        judge(False, "TIFF16(v×257) 形状/位深", "实得 %s %s" % (arr.shape, arr.dtype))

    # ── C. TIFF16 直入：端序判别力 + 大端数值 + 338 ──
    tif16_path = os.path.join(samples, "tiff16_direct_referee.tif")
    raw16 = load_src(os.path.join(samples, "tiff16_direct_referee.src.bin"))
    pairs = np.frombuffer(raw16, dtype=np.uint8).reshape(-1, 2)
    palindromes = int(np.count_nonzero(pairs[:, 0] == pairs[:, 1]))
    total = int(pairs.shape[0])
    frac = palindromes / max(total, 1)
    judge(frac < 0.5, "直入夹具非回文样本占多数（全回文=端序臂必然假绿）",
          "回文 %d/%d (%.1f%%)" % (palindromes, total, frac * 100))

    be = (pairs[:, 0].astype(np.uint16) << 8) | pairs[:, 1].astype(np.uint16)   # 大端解析
    le = (pairs[:, 1].astype(np.uint16) << 8) | pairs[:, 0].astype(np.uint16)   # 小端解析
    with tifffile.TiffFile(tif16_path) as tf:
        page = tf.pages[0]
        arr16 = page.asarray()
        extra16 = int(page.tags["ExtraSamples"].value[0]) if "ExtraSamples" in page.tags else -1
        byteorder = tf.byteorder
    judge(extra16 == 2, "TIFF16 直入 ExtraSamples(338)=2", "实得 %d" % extra16)
    if arr16.shape == (H, W, 4) and arr16.dtype == np.uint16:
        be_img = be.reshape(H, W, 4)
        le_img = le.reshape(H, W, 4)
        perm_be = match_with_perm(arr16, be_img)
        judge(perm_be is not None, "TIFF16 直入 样本 == 源按大端解析（文件 II 小端、数值正确）",
              "文件端序=%s，通道置换=%s" % (byteorder, perm_be or "无置换全等"))
        # 端序"有牙"反控：同一批源按小端解读必须与解码结果不同（非回文占多数 ⇒ 必然不同）。
        perm_le = match_with_perm(arr16, le_img)
        judge(perm_le is None, "直入夹具端序判别力（小端解读必不符 ⇒ 端序读反会被抓红）",
              "小端解读全等=%s（True=夹具判不动端序）" % (perm_le is not None))
    else:
        judge(False, "TIFF16 直入 形状/位深", "实得 %s %s" % (arr16.shape, arr16.dtype))

    # ── 佐证：PIL 也能打开两份 TIFF（第三只眼，不判数值）──
    for label, p in (("v×257", tif_path), ("直入", tif16_path)):
        try:
            im = Image.open(p)
            im.load()
            judge(True, "PIL 可开 TIFF16（%s）——容器对外部读者合法" % label,
                  "size=%s mode=%s" % (im.size, im.mode))
        except Exception as ex:
            judge(False, "PIL 可开 TIFF16（%s）" % label, "PIL 抛错：%s" % ex)

    bad = [w for (ok, w) in _results if not ok]
    print("── 裁判小结：%d 条判据，%d 条不过%s" % (len(_results), len(bad),
          ("：" + "；".join(bad)) if bad else ""))
    sys.exit(pe.EXIT_FAIL if bad else pe.EXIT_OK)


if __name__ == "__main__":
    main()
