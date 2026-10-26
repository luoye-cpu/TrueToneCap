"""64 级紧邻台阶 · 端到端精度判据（§29 代理指标②）。

    样张宿主: TrueToneCap.Tools.exe --ramp-sweep   （走 EncoderFactory 的完整生产路径）
    本脚本:   py -3 scripts/precision/ramp_levels.py [--samples <dir>]

要回答的问题只有一句话：**容器实际交付了几级**。
--param-sweep 那批渐变样张验的是"参数有没有进码流""两档位深是否只差最后一次量化"，
但渐变本身相邻级差好几个 LSB ⇒ 8-bit 产物也"看着连续"，看不出精度止步在哪里。
这里的样张是 64 个**紧邻台阶**（相邻带在 10-bit 刻度上只差 1，且刻意放在中间调 ——
sRGB 曲线暗端线性段会放大间隔、亮端平台会压缩间隔，两端都让 8-bit 显得分得开）：
  · 真给到 ≥10-bit ⇒ 64 级基本都数得出来；
  · 中途被钳回 8-bit ⇒ 相邻台阶并进同一级，只剩十几个。

判据方向是精心选的，**只对"不会被噪声与增益撑高"的那一侧下硬断言**
（2026-09-19 首跑实测纠正过一次：原本写"请求 ≥10-bit ⇒ 级数 ≥48"，结果 r_png_d10 只有 44 ——
查下去是【夹具的错】：生产链对线性值做 paper-white 归一（默认 200nits ⇒ ×0.4），
于是 64 个"1 个 10-bit 码"的台阶到输出编码域只剩 0.61 码 ⇒ 量化必然并掉约三成，
那是 10-bit 量化的【定义】，不是缺陷。⇒ 绝对级数阈值量的是我的增益假设，必须换成增益无关的关系式）：
  A) 请求 16-bit 的每一张 ⇒ 级数 ≥ 48。
     16-bit 容器对这个增益有 ~40 倍余量 ⇒ 只有"声明 16-bit 实际按 8-bit 走完"才会不达标（实测 d8 只有 11~12 级）。
  B) 同一格式家族的"高位深 ÷ 8-bit"≥ 1.6 倍。
     这条才是杀"容器声明高位深、源只有 8-bit"的那把刀：假高精度时两档级数几乎相同（比值 ~1）。
     增益是单调压缩，两档同受影响 ⇒ 比值对增益不敏感，换台机器/改 paper white 也不会假红。
     可证伪性自验：把 r_avifsw_d10 换成"8-bit 源塞进 10-bit 容器"的产物 ⇒ 23→12，
     12 ≥ 1.6×12 不成立 ⇒ 立刻红（做法见下）。
     ⚠ 2026-10-27 把阈值从 2 降到 1.6：2 是在"软件 AVIF 用 CRF 60"的旧夹具上标定的，而那一档
       avifenc 折算成 -q 4，量化步长比台阶本身还粗 ⇒ 两臂一起塌到 11/11，比值恒 ~1，量的不是精度。
       样张改用无损档后（见 FormatBench --ramp-sweep 的说明）实测 23/12=1.92：这是 10-bit YUV
       转换本身的舍入上限（44 级源 → 23 级），不是产品缺陷；假高精度的 12 仍被牢牢卡在 1.0。
       1.6 两侧余量：真值 1.92（+20%）、假值 1.0（−38%），且样张与编码器都是钉死的。
       变异正控（每次改夹具/阈值都该跑一次）：
         avifenc -q 100 -s 6 --yuv 444 --depth 10 artifacts/precision/ramp/r_png_d8.png <tmp>/r_avifsw_d10.avif
         py scripts/precision/ramp_levels.py --samples <tmp>   # 期望 B 条 avifsw 判红
  C) 无损容器(PNG/TIFF)且请求 8-bit ⇒ 级数 ≤ 24。
     无损容器不可能自己造出比容器更细的台阶 ⇒ 反向钉住"位深开关真的生效"。
     ⚠ 只对 png/tiff 成立：AV1/JXL 在平坦区会抖动，级数会被【合法地】撑高，那两类不做硬上限。
  D) 缺样张：软件路径 ⇒ FAIL（生成器没跑出来就是回归）；名字带 nv 的硬件路径 ⇒ SKIP（没 GPU 不算失败）。
  E) 判定条数 < 3 ⇒ 整体 SKIP。绝不出现"什么都没判还退 0"。

退出码：0 通过 / 1 回归 / 3 SKIP（见 precision_env.py）。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import glob
import os
import statistics
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from precision_env import (  # noqa: E402
    EXIT_FAIL,
    EXIT_OK,
    EXIT_SKIP,
    find_tool,
    opt,
    samples_dir,
)

BANDS = 64                # 与 --ramp-sweep 的几何一致
ROWS_PER_BAND = 8
MIN_LEVELS_HIGH = 48      # 16-bit 请求 ⇒ 至少 48/64 级（与裸 NVENC 台阶探针同一阈值）
MIN_HIGH_TO_LOW_RATIO = 1.6     # 同家族「高位深级数 ÷ 8-bit 级数」下限（杀假高精度；标定见文件头 B 条）
MAX_LEVELS_8BIT_LOSSLESS = 24   # 无损容器 8-bit ⇒ 不应超过 ~17 级，留抖动/边界余量
MIN_JUDGED = 3            # 判定条数下限，不足 ⇒ 整体 SKIP（不许"没判还绿"）

# 带内只取中部行、画面只取中部列：边界处会被有损编码的插值/环带污染，取中位也压不住
EDGE_X = 16
EDGE_ROW = 2


def decode_rgb48(ffmpeg: str, path: str) -> tuple[int, int, bytes] | None:
    """用 ffmpeg 解成 rgb48le（每通道 16-bit 真精度）。失败返回 None。

    ⚠ 判据只信外部解码器：这里【不用】本仓库的 PNG/TIFF 解析器，否则等于自证。
    """
    import tempfile

    with tempfile.TemporaryDirectory() as td:
        raw = os.path.join(td, "dec.rgb48")
        cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-i", path,
               "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb48le", "-y", raw]
        p = subprocess.run(cmd, capture_output=True, text=True)
        if p.returncode != 0 or not os.path.isfile(raw):
            err = (p.stderr or "").strip().splitlines()
            print(f"    解码失败: {os.path.basename(path)} — {err[-1] if err else 'ffmpeg rc=%d' % p.returncode}")
            return None
        w, h = probe_size(ffmpeg, path)
        data = open(raw, "rb").read()
        if w <= 0 or h <= 0 or len(data) < w * h * 6:
            print(f"    解码尺寸不符: {os.path.basename(path)} w={w} h={h} bytes={len(data)}")
            return None
        return w, h, data


def probe_size(ffmpeg: str, path: str) -> tuple[int, int]:
    """读容器声明的宽高（解码结果的字节数反推尺寸不可靠）。"""
    ffprobe = find_tool("ffprobe")
    if not ffprobe:
        return 0, 0
    cmd = [ffprobe, "-v", "error", "-select_streams", "v:0",
           "-show_entries", "stream=width,height", "-of", "default=nw=1:nk=0", path]
    p = subprocess.run(cmd, capture_output=True, text=True)
    w = h = 0
    for line in (p.stdout or "").splitlines():
        k, _, v = line.partition("=")
        k, v = k.strip(), v.strip()
        if k == "width":
            w = int(v or 0)
        elif k == "height":
            h = int(v or 0)
    return w, h


def band_medians(w: int, h: int, data: bytes) -> list[int]:
    """逐带取 G 通道中位数。

    为什么用中位数而不是均值：均值会被抖动/噪声"平均出"中间级 ⇒ 把 8-bit 说成高精度；
    中位数落在真实众数上，抖动压不住它。G 通道足够 —— 样张是中性灰（R=G=B）。
    """
    out = []
    for b in range(BANDS):
        r0 = b * ROWS_PER_BAND + EDGE_ROW
        r1 = (b + 1) * ROWS_PER_BAND - EDGE_ROW
        vals = []
        for y in range(max(r0, 0), min(r1, h)):
            row = y * w * 6
            for x in range(EDGE_X, min(w - EDGE_X, w)):
                i = row + x * 6 + 2          # +2：RGB48 里 G 的第 2 个字节起
                vals.append(struct.unpack_from("<H", data, i)[0])
        out.append(int(statistics.median(vals)) if vals else -1)
    return out


def main() -> int:
    ffmpeg = find_tool("ffmpeg")
    if not ffmpeg:
        print("⏭ 跳过：找不到 ffmpeg（判据只信外部解码器，没有它就没有判据）")
        return EXIT_SKIP

    sdir = samples_dir("ramp", opt("--samples"))
    if not sdir:
        return EXIT_SKIP          # samples_dir 已经打印过原因

    files = sorted(f for f in glob.glob(os.path.join(sdir, "r_*.*")) if os.path.isfile(f))
    if not files:
        print(f"⏭ 跳过：{sdir} 里没有 r_* 台阶样张（先跑 TrueToneCap.Tools.exe --ramp-sweep）")
        return EXIT_SKIP

    print("═══ 64 级紧邻台阶 · 端到端解出多少级 ═══")
    print(f"  样张: {sdir}")
    print(f"  解码: {ffmpeg}\n")

    # ── 第一遍：解码 + 数级数（只测量，不判定）──
    rows: list[dict] = []
    failed: list[str] = []
    skipped: list[str] = []

    for path in files:
        name = os.path.splitext(os.path.basename(path))[0]      # r_png_d10
        depth = 0
        for p in name.split("_"):
            if p.startswith("d") and p[1:].isdigit():
                depth = int(p[1:])
        # 格式家族：r_<family>_d<depth> —— B 条按家族配对，避免拿 AVIF 的 8-bit 去比 PNG 的 16-bit
        fam = name.split("_")[1] if "_" in name else name
        hardware = "_avifnv" in name or "_nv" in name
        lossless = fam in ("png", "tiff")

        dec = decode_rgb48(ffmpeg, path)
        if dec is None:
            # 硬件通路解不动多半是环境/编码器没跑出来 → SKIP；软件路径必须追
            if hardware:
                skipped.append(f"{name}（硬件通路解码失败）")
                continue
            failed.append(f"{name} 解码失败 ⇒ 无从判定")
            continue

        w, h, data = dec
        meds = band_medians(w, h, data)
        if any(v < 0 for v in meds):
            failed.append(f"{name} 带内取样为空（尺寸/几何与生成器不符）")
            continue
        rows.append({"name": name, "fam": fam, "depth": depth, "lossless": lossless,
                     "levels": len(set(meds)), "lo": min(meds), "hi": max(meds)})

    judged = len(rows)
    print(f"  {'样张':<14} {'级数':>5} /64   中位跨度(16-bit 码)")
    for r in rows:
        print(f"  {r['name']:<14} {r['levels']:>4}      {r['lo']}–{r['hi']}")
    for s in skipped:
        print(f"  ⏭ {s}")

    if judged < MIN_JUDGED:
        print(f"\n⏭ 判定条数 {judged} < {MIN_JUDGED} ⇒ 本轮【没有】判出台阶精度，不算通过")
        return EXIT_SKIP

    # ── 第二遍：三条规则（A 绝对下限 / B 家族内倍率 / C 无损上限）──
    for r in rows:
        if r["depth"] >= 16 and r["levels"] < MIN_LEVELS_HIGH:
            failed.append(f"A 条 {r['name']}: 请求 16-bit 却只解出 {r['levels']} 级"
                          f"（需 ≥{MIN_LEVELS_HIGH}）⇒ 高位深声明未兑现")
        if r["lossless"] and r["depth"] == 8 and r["levels"] > MAX_LEVELS_8BIT_LOSSLESS:
            failed.append(f"C 条 {r['name']}: 无损容器请求 8-bit 却解出 {r['levels']} 级"
                          f"（应 ≤{MAX_LEVELS_8BIT_LOSSLESS}）⇒ 位深开关没生效")

    for fam in sorted({r["fam"] for r in rows}):
        low = next((r for r in rows if r["fam"] == fam and r["depth"] == 8), None)
        highs = [r for r in rows if r["fam"] == fam and r["depth"] >= 10]
        if not low or not highs:
            continue
        for hi in highs:
            if hi["levels"] < low["levels"] * MIN_HIGH_TO_LOW_RATIO:
                failed.append(
                    f"B 条 {fam}: {hi['name']} 只解出 {hi['levels']} 级，"
                    f"未达 {low['name']}（{low['levels']} 级）的 {MIN_HIGH_TO_LOW_RATIO} 倍"
                    f" ⇒ 声明的 {hi['depth']}-bit 没换来更多实际级数（假高精度）")

    print(f"\n判定 {judged} 张（跳过 {len(skipped)}），不符 {len(failed)} 项")
    for f in failed:
        print(f"  ❌ {f}")
    if failed:
        print("\n结论：❌ 台阶精度回归 —— 高精度请求没换来对应级数，或无损容器交出了不该有的级数")
        return EXIT_FAIL
    print(f"\n结论：✅ 生产编码器全链末端：16-bit 请求解出 ≥{MIN_LEVELS_HIGH} 级，"
          f"且各家族高位深相对 8-bit 至少 {MIN_HIGH_TO_LOW_RATIO}×")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
