"""独立裁判：让 lcms2（经 Pillow 内置）读回本仓库自产的 ICC，而不是用仓库自己的解析器自证。

背景（2026-09-20 许可合规 A-3 的连带发现）：
`ColorProfileProvider.BuildValidSRgbIcc()` 有 5 处用 `BinaryWriter.Write(int)` 直接写整数 ——
BinaryWriter 只会小端，而 ICC 全程大端 ⇒ profile 尺寸、日期、头部 PCS 照明体、tag 数、
tag 表 offset/size 字节序全反 ⇒ 那份"兜底 sRGB ICC"从来没合法过。
它之所以从未暴露：`GetDefaultSRgbIcc` 优先读系统 profile，正常 Windows 上根本跑不到那条分支，
连判据 ⑨ 也因此是一条假绿灯（A-3 把模板换成手写件之后才被迫现形）。

判据分三层，缺任一层都不算过：
① lcms 能解析，且它**自己读出来**的 description / copyright 与我们的意图一致；
② 数值等价：lcms 内置 sRGB ↔ 本仓库自产 sRGB 双向转换，64 级斜坡逐通道 ≤1/255；
③ 反证（裁判自身的自检）：把同一份 profile 的头部整数改回小端 ⇒ ①/② 必须失效。
   没有这一条，就区分不了"修好了"与"lcms 本来就宽容"。

输入来自 --color-tests 落盘的两个 PNG（iCCP 即产品真正嵌进用户文件的那份）：
  %TEMP%/TrueToneCap_ColorTest/icc_test.png        ← GetDefaultSRgbIcc()
  %TEMP%/TrueToneCap_ColorTest/provenance_p3.png   ← PatchSrgbPrimaries(DisplayP3)

用法:  py scripts/probes/icc_selfbuilt_referee.py
退出码: 0 全过；1 有失败。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import io
import os
import struct
import sys
import tempfile
import zlib

from PIL import Image, ImageCms

COLOR_OUT = os.path.join(tempfile.gettempdir(), "TrueToneCap_ColorTest")
SRGB_PNG = os.path.join(COLOR_OUT, "iccp_test.png")
P3_PNG = os.path.join(COLOR_OUT, "provenance_p3.png")

EXPECTED_CPRT = "Copyright 2026 TrueToneCap contributors - Apache-2.0"
TAG_SIGS = {"desc", "cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"}

fails: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'✅' if ok else '❌'} {name}" + (f" — {detail}" if detail else ""))
    if not ok:
        fails.append(name)


def icc_from_png(path: str) -> bytes | None:
    """独立遍历 chunk 链取 iCCP 并 inflate（不用仓库里的任何解析器）。"""
    with open(path, "rb") as f:
        data = f.read()
    pos = 8
    while pos + 8 <= len(data):
        (ln,) = struct.unpack(">I", data[pos:pos + 4])
        typ = data[pos + 4:pos + 8]
        if typ == b"IEND" or ln < 0 or pos + 12 + ln > len(data):
            return None
        if typ == b"iCCP":
            body = data[pos + 8:pos + 8 + ln]
            name_end = body.index(b"\0")
            assert body[name_end + 1] == 0, "iCCP 压缩方法应为 0"
            return zlib.decompress(body[name_end + 2:])
        pos += 12 + ln
    return None


def tag_table(icc: bytes) -> list[tuple[str, int, int]]:
    n = struct.unpack(">I", icc[128:132])[0]
    out = []
    for i in range(n):
        e = 132 + 12 * i
        sig = icc[e:e + 4].decode("ascii", "replace")
        off, size = struct.unpack(">II", icc[e + 4:e + 12])
        out.append((sig, e, off, size))
    return out


def ramp(count: int = 64) -> Image.Image:
    img = Image.new("RGB", (count, 3))
    px = img.load()
    for i in range(count):
        v = round(i * 255 / (count - 1))
        px[i, 0] = (v, 0, 0)
        px[i, 1] = (0, v, 0)
        px[i, 2] = (0, 0, v)
    return img


def convert(icc_bytes: bytes, dst_bytes: bytes | None, img: Image.Image) -> Image.Image:
    src = ImageCms.getOpenProfile(io.BytesIO(icc_bytes))
    dst = ImageCms.createProfile("sRGB") if dst_bytes is None else ImageCms.getOpenProfile(io.BytesIO(dst_bytes))
    t = ImageCms.buildTransform(src, dst, "RGB", "RGB", renderingIntent=ImageCms.Intent.PERCEPTUAL)
    return ImageCms.applyTransform(img, t)


def max_delta(a: Image.Image, b: Image.Image) -> int:
    pa, pb = a.load(), b.load()
    return max(abs(pa[x, y][c] - pb[x, y][c]) for y in range(a.height) for x in range(a.width) for c in range(3))


def main() -> int:
    for p in (SRGB_PNG, P3_PNG):
        if not os.path.isfile(p):
            # 输入由 --color-tests 现场产出；缺它只说明"这轮没跑测试"，不是产品有问题 ⇒ SKIP(3)
            print(f"[SKIP] 找不到 {p}\n"
                  "       先跑: dotnet run --project src/TrueToneCap.Test -c Release -- --color-tests")
            return 3

    srgb, p3 = icc_from_png(SRGB_PNG), icc_from_png(P3_PNG)
    check("取到两份 iCCP", bool(srgb) and bool(p3), f"sRGB {len(srgb or b'')}B / P3 {len(p3 or b'')}B")
    if not (srgb and p3):
        return 1

    print("\n── ① lcms 自己读出来的字段 ──")
    try:
        ps = ImageCms.getOpenProfile(io.BytesIO(srgb))
        pp = ImageCms.getOpenProfile(io.BytesIO(p3))
    except Exception as ex:
        check("lcms 能打开两份自产 profile", False, f"{type(ex).__name__}: {ex}")
        return 1
    check("lcms 能打开两份自产 profile", True)
    name_s, name_p = ImageCms.getProfileName(ps).strip(), ImageCms.getProfileName(pp).strip()
    check("自产 sRGB 的 profile 描述含 sRGB", "sRGB" in name_s, name_s)
    check("自产 P3 的 profile 描述含 Display P3", "Display P3" in name_p, name_p)
    cprt_s = ImageCms.getProfileCopyright(ps).strip()
    cprt_p = ImageCms.getProfileCopyright(pp).strip()
    check("lcms 读到的 cprt(sRGB) 是本项目自己的 Apache 声明", cprt_s == EXPECTED_CPRT, cprt_s)
    check("lcms 读到的 cprt(P3) 同样是自己的声明（没有残留第三方版权）", cprt_p == EXPECTED_CPRT, cprt_p)
    check("大端回填自洽：头部尺寸 == 实际字节数",
          struct.unpack(">I", srgb[:4])[0] == len(srgb), f"{struct.unpack('>I', srgb[:4])[0]} vs {len(srgb)}")
    sigs = {t[0] for t in tag_table(srgb)}
    check("tag 表按大端解析出全部 10 个标签", TAG_SIGS <= sigs, f"缺 {sorted(TAG_SIGS - sigs)}")

    print("\n── ② 与 lcms 内置 sRGB 数值等价 ──")
    img = ramp()
    ref_bytes = None
    d_fwd = max_delta(img, convert(srgb, ref_bytes, img))
    check("自产 sRGB → lcms 内置 sRGB：≤1/255", d_fwd <= 1, f"最大偏差 {d_fwd}")
    builtin = ImageCms.createProfile("sRGB")
    d_bwd = max_delta(img, convert(ImageCms.ImageCmsProfile(builtin).tobytes(), srgb, img))
    check("lcms 内置 sRGB → 自产 sRGB：≤1/255", d_bwd <= 1, f"最大偏差 {d_bwd}")
    same = ImageCms.ImageCmsProfile(builtin).tobytes()
    d_self = max_delta(img, convert(same, same, img))
    check("裁判自检：内置 sRGB 自转恒等（否则 ② 的容差没有意义）", d_self == 0, f"偏差 {d_self}")
    d_p3 = max_delta(img, convert(p3, same, img))
    check("反证方向：自产 P3 转成 sRGB 必须有可见差异（矩阵真的被换了，不是恒等件）", d_p3 > 4, f"偏差 {d_p3}")

    print("\n── ③ 反证：把头部整数改回小端，①/② 必须失效 ──")
    bad = bytearray(srgb)
    struct.pack_into("<I", bad, 0, len(srgb))
    struct.pack_into("<I", bad, 128, len(tag_table(srgb)))
    for sig, e, off, size in tag_table(srgb):
        struct.pack_into("<I", bad, e + 4, off)
        struct.pack_into("<I", bad, e + 8, size)
    struct.pack_into("<I", bad, 68, 0x0000F6D6)   # 头部 PCS 照明体 X
    bad_bytes = bytes(bad)
    broke = False
    why = ""
    try:
        bp = ImageCms.getOpenProfile(io.BytesIO(bad_bytes))
        c2 = ImageCms.getProfileCopyright(bp).strip()
        d2 = max_delta(img, convert(bad_bytes, ref_bytes, img))
        broke = (c2 != EXPECTED_CPRT) or d2 > 1
        why = f"cprt={c2!r} 偏差={d2}"
    except Exception as ex:
        broke = True
        why = f"lcms 直接拒绝：{type(ex).__name__}"
    check("反证成立：小端版被 lcms 拒绝或转出可见差异", broke, why)

    print()
    if fails:
        print(f"[RESULT] FAIL {len(fails)} 项: {fails}")
        return 1
    print("[RESULT] PASS — lcms2 认可本仓库自产 ICC 的字节序、标签与色度学")
    return 0


if __name__ == "__main__":
    sys.exit(main())
