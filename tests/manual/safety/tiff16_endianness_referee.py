#!/usr/bin/env python
# SPDX-License-Identifier: Apache-2.0
# tiff16_endianness_referee.py -- TrueToneCap 16-bit TIFF 容器外部裁判 + 端序判别对照臂
#
# 存在的理由（2026-10-31）：ManagedTiffEncoder 改成多 strip Deflate 之后，验收口径是
# "产物字节必须由外部解码器裁判"。此前两路外部裁判（Pillow 4K 8-bit / ffmpeg 4K 16-bit）
# 都过了，但 16-bit 的【字节序】半边被判为 UNVERIFIED：把解码缓冲按 LE 读和按 BE 读
# 都得到 Δ=0 —— 一条不可能变红的对照臂等于没有臂。本脚色做两件事：
#   (1) 用【独立参考】（只由喂给编码器的那份 .bgra 推出，绝不从 TIFF 字节反推）把
#       8/16-bit 逐样本对错钉死；
#   (2) 让端序臂【必须会红】：--bench-tiff 的 16-bit 源恒为 v*257 ⇒ 每个样本都是
#       0xNNNN 回文 ⇒ 交换字节序在数学上是恒等变换 ⇒ 任何裁判在这个 fixture 上都分不出来
#       （本脚本把这一步实测并印出来，作为根因，而不是假装过）。
#       要让它会红，必须换一份【非回文】的产品 16-bit 字节：产品自己的 Float16→16-bit TIFF
#       腿（TrueToneCap.Tools.exe --sdr16-bench → sdr16_tiff16.tiff，512x512，2 strip）
#       样本只有 25.1% 回文 ⇒ 端序臂在此真正有牙齿。
#
# 产品侧 16-bit 约定（逐条取自源码，见报告）：
#   头 "II" 小端                ManagedTiffEncoder.cs:79
#   16-bit 输入 = BGRA、每通道大端   ManagedTiffEncoder.cs:34,335-336,361-365 + ReadU16BE:395
#   8→16 扩展 = v*257           ManagedTiffEncoder.cs:370-373
#   输出 = RGBA、每通道小端        ManagedTiffEncoder.cs:379-382 + WriteU16LE:397-401（说明见 :337）
#   PhotometricInterpretation=2(RGB) :146 / SamplesPerPixel=4 :166 / BitsPerSample=4×depth（交织、每平面一值）:139-140,211-212
#   Compression=8 (Adobe Deflate = zlib 封装) :143,251-258 / ~1 MiB 一条多 strip :19,107-109
#   ⚠ 无 ExtraSamples(338) 标签：alpha 是第 4 样本但未声明语义（baseTagCount=12，:89-92）
#
# 判据：每条臂都打印【两侧的实际输入】；required-red 若为绿 或 required-green 若为红 ⇒ 退出码非 0。
# 用法：py tiff16_endianness_referee.py --repo C:\PLAN\TrueToneCap [--fast] [--out SUB]

import argparse
import os
import subprocess
import sys

import numpy as np

try:
    import tifffile  # 外部实现，按 II/MM 头解读样本
except ImportError:  # pragma: no cover
    print("MISSING DEPENDENCY: tifffile", file=sys.stderr)
    sys.exit(2)

W, H = 3840, 2160
Y_W, Y_H = 512, 512
TEXTURE_RUN = 8  # sdr16 浮点源的竖条纹理周期（FormatBench.cs:521-530 定义，非从产品字节推出）

# ---------------------------------------------------------------- 工具

def chunked_maxdiff(a, b, rows=256):
    """逐样本 |Δ|：分块以免为 4K×16-bit 造出 132 MB 级 int32 临时数组。"""
    if a.shape != b.shape:
        return None, None, f"shape {a.shape} != {b.shape}"
    mx, nd = 0, 0
    n = a.shape[0]
    for i in range(0, n, rows):
        x = a[i:i + rows].astype(np.int32)
        y = b[i:i + rows].astype(np.int32)
        d = np.abs(x - y)
        mx = max(mx, int(d.max()))
        nd += int((d > 0).sum())
    return mx, nd, ""


def palindromic_frac(a):
    """样本高低字节相等（0xNNNN）的占比 —— ==1.0 时端序臂在数学上必然分不出来。"""
    return float(((a >> 8) & 0xFF == (a & 0xFF)).mean())


def asarray_le(raw, w, h):
    return np.frombuffer(raw, dtype="<u2").reshape(h, w, 4).copy()


class Ffmpeg:
    def __init__(self, exe):
        self.exe = exe

    def decode_u16(self, path, w, h, pix_fmt, tmp):
        if self.exe is None:
            return None
        if os.path.exists(tmp):
            os.remove(tmp)
        cmd = [self.exe, "-v", "error", "-y", "-i", path,
               "-f", "rawvideo", "-pix_fmt", pix_fmt, tmp]
        try:
            p = subprocess.run(cmd, capture_output=True, timeout=600)
        except Exception as ex:
            print(f"  ffmpeg 调用失败: {ex}")
            return None
        if p.returncode != 0 or not os.path.exists(tmp):
            print(f"  ffmpeg rc={p.returncode} err={p.stderr.decode('utf8', 'replace').strip()[:200]}")
            return None
        raw = open(tmp, "rb").read()
        if len(raw) != w * h * 8:
            print(f"  ffmpeg 输出尺寸 {len(raw)} != {w * h * 8}")
            return None
        return asarray_le(raw, w, h)


def brief(a, n=3):
    """打印参与比较的一侧的实际输入（前 n 个像素），口径：仓库要求两侧都可见。"""
    flat = a.reshape(-1, a.shape[-1])[:n]
    return "[" + "; ".join(" ".join(str(int(v)) for v in row) for row in flat) + "]"


# ---------------------------------------------------------------- 参考（独立于 TIFF 字节）

def ref_from_bgra16(path, w, h):
    """喂给编码器的那份 .bgra（BGRA、每通道大端）→ 期望的 RGBA16 样本数组。
    16-bit 源在产品里是【恒等搬运】（ReadU16BE→WriteU16LE），故参考就是源样本本身。"""
    raw = np.fromfile(path, dtype=np.uint8)
    if raw.size != w * h * 8:
        raise SystemExit(f"参考尺寸不符: {path} bytes={raw.size} 期望={w * h * 8}")
    px = raw.reshape(-1, 8)
    b = (px[:, 0].astype(np.uint16) << 8) | px[:, 1]
    g = (px[:, 2].astype(np.uint16) << 8) | px[:, 3]
    r = (px[:, 4].astype(np.uint16) << 8) | px[:, 5]
    a = (px[:, 6].astype(np.uint16) << 8) | px[:, 7]
    return np.stack([r, g, b, a], axis=1).reshape(h, w, 4)


def widen8_to_16(src8_path, w, h):
    """8-bit 源 BGRA → 16-bit RGBA，按产品口径 v*257（ManagedTiffEncoder.cs:370-373）。"""
    raw = np.fromfile(src8_path, dtype=np.uint8)
    if raw.size != w * h * 4:
        raise SystemExit(f"8-bit 参考尺寸不符: {raw.size} != {w * h * 4}")
    px = raw.reshape(-1, 4).astype(np.uint16)
    b, g, r, a = (px[:, 0], px[:, 1], px[:, 2], px[:, 3])
    return np.stack([r * 257, g * 257, b * 257, a * 257], axis=1).reshape(h, w, 4)


# ---------------------------------------------------------------- 端序判别（结构裁判，不读产品字节）

def decode16(path):
    """外部解码器 → (h,w,4) uint16。任何形状/位深异常都成一条【判据】，不留 traceback。"""
    a = tifffile.imread(path)
    if a.dtype != np.uint16:
        raise ValueError(f"解码 dtype={a.dtype}（期望 uint16：BitsPerSample=4×16 交织）")
    if a.ndim != 3 or a.shape[2] != 4:
        raise ValueError(f"解码 shape={a.shape}（期望 (h,w,4)：SamplesPerPixel=4 + Photometric=RGB）")
    return a


def monotonic_violations(a):
    """产品字节必须服从的【结构】：sdr16 浮点源是水平渐变（FormatBench.cs:521-530，
    每 TEXTURE_RUN 像素一个 1.0/1.1 纹理组）⇒ RGB 通道在每个纹理组内沿 x 非递减。
    这条判据的真值来自 fixture 的生成式，不来自被检字节 ⇒ 非回文样本下它一定能咬端序。"""
    ai = a[:, :, :3].astype(np.int32)
    tot = 0
    for start in range(0, a.shape[1], TEXTURE_RUN):
        seg = ai[:, start:start + TEXTURE_RUN]
        tot += int((np.diff(seg, axis=1) < 0).sum())
    return tot


# ---------------------------------------------------------------- 跑

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=r"C:\PLAN\TrueToneCap")
    ap.add_argument("--fast", action="store_true", help="跳过 4K 臂，只跑端序判别（fixture Y）")
    ap.add_argument("--out", default="tiff16referee", help="artifacts 下的工作子目录")
    ap.add_argument("--bench", default=None, help="覆盖 fixture X 目录（做变异测试用）")
    ap.add_argument("--probe", default=None, help="覆盖 fixture Y 目录（做变异测试用）")
    args = ap.parse_args()

    repo = args.repo
    work = os.path.join(repo, "artifacts", args.out)
    os.makedirs(work, exist_ok=True)
    bench = args.bench or os.path.join(repo, "artifacts", "bench")
    probe = args.probe or os.path.join(repo, "artifacts", "precision", "probe")

    ff_exe = None
    for cand in (os.environ.get("FFMPEG"), r"C:\PLAN\FFmpeg\ffmpeg.exe", "ffmpeg"):
        if cand and (os.path.isabs(cand) or subprocess.run(
                ["where", cand], capture_output=True).returncode == 0):
            ff_exe = cand if os.path.isabs(cand) else subprocess.run(
                ["where", cand], capture_output=True).stdout.decode().splitlines()[0].strip()
            break
    ff = Ffmpeg(ff_exe)
    print(f"外部解码器: tifffile={tifffile.__version__}  ffmpeg={ff_exe}")

    arms = []  # (name, expect, ok, detail)

    def arm(name, expect, ok, detail):
        arms.append((name, expect, bool(ok), detail))
        flag = "OK " if ok else "XX "
        print(f"{flag}{name:<44} expect={expect:<14} {detail}")

    # ═══ Fixture Y：产品 Float16→16-bit TIFF 腿（非回文 ⇒ 端序臂有牙齿）═══
    y_tif = os.path.join(probe, "sdr16_tiff16.tiff")
    y_png = os.path.join(probe, "sdr16_png10.png")
    print("\n── Fixture Y（512x512 产品 16-bit TIFF，来自 --sdr16-bench；2 strip）──")
    if not (os.path.exists(y_tif) and os.path.exists(y_png)):
        arm("Y.fixture", "存在", False, f"缺 {y_tif} / {y_png}：先跑 --sdr16-bench")
        return finish(arms)
    try:
        y_le = decode16(y_tif)
    except ValueError as ex:
        arm("Y1 结构裁判 LE(渐变单调) [必须绿]", "违反=0", False, f"解码形状不符: {ex}")
        return finish(arms)
    y_be = y_le.byteswap()  # 同一批字节按相反端序解读 = 若 writer 声明与实际不符时的真值
    pf = palindromic_frac(y_le)
    print(f"  Y 解码: shape={y_le.shape} dtype={y_le.dtype} 回文样本占比={pf:.4f}"
          f"（{pf:.4f} < 1 ⇒ 端序臂在此【可分】）  首像素 LE={brief(y_le,2)}")
    arm("Y0 端序可分性前提(回文占比<1)", ">0 非回文", pf < 1.0,
        f"回文={pf:.4f} 非回文={1 - pf:.4f}")

    v_le = monotonic_violations(y_le)
    v_be = monotonic_violations(y_be)
    arm("Y1 结构裁判 LE(渐变单调) [必须绿]", "违反=0", v_le == 0,
        f"LE 组内递减={v_le}  |  BE 组内递减={v_be}（两侧同表：LE={brief(y_le,2)} vs BE={brief(y_be,2)}）")
    arm("Y2 端序臂 字节交换破坏结构 [必须红]", "违反>>0", v_be > 0 and v_le == 0,
        f"LE 递减={v_le} vs BE 递减={v_be}（红/绿分开 {v_be} 个越界 ⇒ 有牙齿）")

    png_raw = ff.decode_u16(y_png, Y_W, Y_H, "rgba64le", os.path.join(work, "y_png_rgba64le.raw"))
    if png_raw is None:
        arm("Y3 跨容器锚(PNG 规格=大端)", "可用", False, "ffmpeg 未能解 PNG")
    else:
        # PNG 腿量化到 10-bit 就近取整，TIFF 腿保 16-bit 全精度 ⇒ 二者最多差 1 个 10-bit 码步 = 64
        m_ok, n_ok, _ = chunked_maxdiff(png_raw, y_le)
        m_sw, n_sw, _ = chunked_maxdiff(png_raw, y_be)
        arm("Y3 跨容器锚 LE vs PNG(≤64=1码步) [必须绿]", "max|Δ|<=64", m_ok <= 64,
            f"LE 侧 max|Δ|={m_ok} 不同={n_ok}/{Y_W*Y_H*4}  |  锚(ffmpeg/PNG,大端)={brief(png_raw,2)} 被测={brief(y_le,2)}")
        arm("Y4 端序臂 交换后 vs PNG 锚 [必须红]", "max|Δ|>64", m_sw > 512,
            f"BE 侧 max|Δ|={m_sw} 不同={n_sw}/{Y_W*Y_H*4}（阈值 512 = 8 个码步，远大于量化噪声）")

    tif_ff = ff.decode_u16(y_tif, Y_W, Y_H, "rgba64le", os.path.join(work, "y_tiff_rgba64le.raw"))
    if tif_ff is not None:
        m, n, err = chunked_maxdiff(tif_ff, y_le)
        arm("Y5 两个外部解码器一致(仅证一致)", "max|Δ|=0", m == 0,
            f"tifffile vs ffmpeg max|Δ|={m} 不同={n} {err}"
            "（⚠ 两者都按 II 头读 ⇒ 这条抓不到【声明与实际不符】，故不可当端序判据）")

    # ═══ Y6 自检牙齿（常驻，不可关）═══
    # 把"writer 声明 II 却按大端存样本"这一缺陷形状直接喂进两条裁判：
    # 合规解码器读到的就是 byteswap(真值)，等价于 y_be。两条裁判必须至少一条咬红；
    # 全都绿 = 本脚本对端序是瞎的 = 判红（一条不会失败的守卫比没有守卫更糟）。
    mv = monotonic_violations(y_be)
    mm = mn = None
    if png_raw is not None:
        mm, mn, _ = chunked_maxdiff(png_raw, y_be)
    bit = (mv > 0) or (mm is not None and mm > 512)
    arm("Y6 自检牙齿 缺陷形状必须被咬住", "至少一条红", bit,
        f"缺陷侧(=byteswap) 组内递减={mv}（正常侧=0）; 缺陷侧 vs PNG 锚 max|Δ|={mm}"
        f"（正常侧={m_ok if png_raw is not None else 'n/a'}）")

    if args.fast:
        return finish(arms)

    # ═══ Fixture X：4K 产品 16-bit（独立 NumPy 参考钉死像素/通道/扩展）═══
    print("\n── Fixture X（4K 产品 16-bit TIFF，来自 --bench-tiff --keep）──")
    src16 = os.path.join(bench, f"keep_tiff_{W}x{H}_16bit_src.bgra")
    if not os.path.exists(src16):
        arm("X.fixture", "存在", False, f"缺 {src16}")
        return finish(arms)
    ref = ref_from_bgra16(src16, W, H)
    print(f"  独立参考: {ref.shape} {ref.dtype} 来自 {os.path.basename(src16)}（绝不读 TIFF 字节）首像素={brief(ref,2)}")
    for effort in ("Smallest", "Quick", "Fastest"):
        p = os.path.join(bench, f"keep_tiff_{W}x{H}_16bit_{effort}.tif")
        if not os.path.exists(p):
            arm(f"X 16bit {effort}", "存在", False, "缺文件")
            continue
        try:
            dec = decode16(p)
        except ValueError as ex:
            arm(f"X1 独立参考 vs tifffile [{effort}]", "max|Δ|=0", False, f"解码形状不符: {ex}")
            continue
        pf = palindromic_frac(dec)
        m, n, _ = chunked_maxdiff(dec, ref)
        arm(f"X1 独立参考 vs tifffile [{effort}]", "max|Δ|=0", m == 0,
            f"max|Δ|={m} 不同={n}/{W*H*4} 参考={brief(ref,2)} 解码={brief(dec,2)}")
        if effort == "Smallest":
            # 端序臂在本 fixture 上的状态：回文 ⇒ 数学上不可分（根因，不当作通过）
            sw = dec.byteswap()
            m2, n2, _ = chunked_maxdiff(sw, ref)
            arm("X6 端序臂 vs 独立参考(本 fixture)", "DEAD-BY-DATA",
                (pf == 1.0 and m2 == 0) or (pf < 1.0 and m2 > 0),
                f"回文占比={pf:.4f} 交换后 max|Δ|={m2} 不同={n2} ⇒ "
                + ("样本全回文：LE/BE 恒等，任何裁判在此都不可能红（换 fixture，见 Y）"
                   if pf == 1.0 else "样本非全回文：该臂已生效并红"))
            # 正控（红）：错扩展 / 错通道序 / 行错位
            wrong_wid = np.minimum((ref >> 8) * 256, 65535).astype(np.uint16)  # v<<8 而非 v*257
            mw, nw, _ = chunked_maxdiff(dec, wrong_wid)
            arm("X2 正控 错扩展 v<<8 代 v*257", "红", mw > 0,
                f"max|Δ|={mw} 不同={nw}/{W*H*4} 错参考={brief(wrong_wid,2)}")
            chan = dec[:, :, [2, 1, 0, 3]]  # 把解码按 BGRA 读
            mc, nc, _ = chunked_maxdiff(chan, ref)
            arm("X3 正控 错通道序 BGRA", "红", mc > 0,
                f"max|Δ|={mc} 不同={nc} 错序解码={brief(chan,2)} vs 参考={brief(ref,2)}")
            shift = np.roll(dec, 1, axis=0)
            ms, ns, _ = chunked_maxdiff(shift, ref)
            arm("X4 正控 行移 1", "红", ms > 0,
                f"max|Δ|={ms} 不同={ns}（若为 0 说明内容上下对称，裁判失去牙齿）")
            tiff_ff = ff.decode_u16(p, W, H, "rgba64le", os.path.join(work, "x_tiff_rgba64le.raw"))
            if tiff_ff is not None:
                mf, nf, _ = chunked_maxdiff(tiff_ff, ref)
                arm("X5 独立参考 vs ffmpeg rgba64le", "max|Δ|=0", mf == 0,
                    f"max|Δ|={mf} 不同={nf}")
            else:
                arm("X5 独立参考 vs ffmpeg rgba64le", "可用", False, "ffmpeg 解码失败")

    # ═══ Fixture X8：8-bit 产品 —— 同一脚本的阴性对照（必须绿）═══
    print("\n── Fixture X8（4K 产品 8-bit，阴性对照）──")
    src8 = os.path.join(bench, f"keep_tiff_{W}x{H}_8bit_src.bgra")
    p8 = os.path.join(bench, f"keep_tiff_{W}x{H}_8bit_Smallest.tif")
    if not (os.path.exists(src8) and os.path.exists(p8)):
        arm("X8.fixture", "存在", False, "缺 8-bit fixture")
    else:
        raw = np.fromfile(src8, dtype=np.uint8).reshape(-1, 4)
        ref8 = raw[:, [2, 1, 0, 3]].reshape(H, W, 4)  # BGRA→RGBA，8-bit 不升位深
        d8 = tifffile.imread(p8)
        if d8.dtype != np.uint8 or d8.shape != (H, W, 4):
            arm("X8-1 8-bit 独立参考 vs tifffile [必须绿]", "max|Δ|=0", False,
                f"8-bit 解码形状/位深不符: dtype={d8.dtype} shape={d8.shape}")
            return finish(arms)
        m, n, _ = chunked_maxdiff(d8, ref8)
        arm("X8-1 8-bit 独立参考 vs tifffile [必须绿]", "max|Δ|=0", m == 0,
            f"max|Δ|={m} 不同={n}/{W*H*4} 参考={brief(ref8,2)} 解码={brief(d8,2)}")
        ms, ns, _ = chunked_maxdiff(np.roll(d8, 1, axis=0), ref8)
        arm("X8-2 8-bit 正控 行移 1", "红", ms > 0, f"max|Δ|={ms} 不同={ns}")
        mc, nc, _ = chunked_maxdiff(d8[:, :, [2, 1, 0, 3]], ref8)
        arm("X8-3 8-bit 正控 错通道序", "红", mc > 0, f"max|Δ|={mc} 不同={nc}")

    return finish(arms)


def finish(arms):
    print("\n══ 汇总 ══")
    bad = []
    for name, expect, ok, detail in arms:
        print(f"  {'PASS' if ok else 'FAIL'}  {name:<46} expect={expect}")
        if not ok:
            bad.append((name, detail))
    if bad:
        print(f"\n❌ {len(bad)}/{len(arms)} 条臂不符合要求：")
        for n, d in bad:
            print(f"   - {n}: {d}")
        return 1
    print(f"\n✅ 全部 {len(arms)} 条臂符合要求（required-red 皆红、required-green 皆绿）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
