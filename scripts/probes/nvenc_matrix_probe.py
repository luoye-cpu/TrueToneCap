"""反解 NVENC 的 RGB→YUV 矩阵与摆幅（配合 --nvenc-color-probe）。

判据设计（关键：先验证量尺，再读结论）
  1. libaom 那份是【已知答案】——它的矩阵就是我们下发给 avifenc 的 --cicp 1/13/1（BT.709）、
     摆幅 full（容器实测 range=pc）。它必须先被本脚本判成 (709, full)，
     否则说明解码/采样/公式某一环错了，NVENC 的结论一律不作数。
  2. NVENC 那份在候选里取误差最小者，并要求"容差内恰好一个候选"（见 verdict 与 TOL）：
     最优必须近乎精确，次优必须落在容差外。不满足就报"判不出来"，绝不硬给结论。
  3. --sweep 子模式：把 matrixCoefficients 逐个码点下发给驱动，反推它实际用了哪张矩阵，
     从而判"驱动听不听这个字段"——听 ⇒ 硬件就能做 BT.709/BT.2020，无需降级成 BT.601。
     这条判据不看容器声明（扫描期间 colr 仍按产品逻辑写，与下发的码点无关）。

用法: py scripts/probes/nvenc_matrix_probe.py [--sweep]
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "precision"))
from precision_env import find_tool  # noqa: E402

DIR = os.path.join("artifacts", "precision", "probe", "nvenc_color")
BANDS = [(0, 0, 0), (255, 255, 255), (255, 0, 0), (0, 255, 0),
         (0, 0, 255), (255, 255, 0), (0, 255, 255), (128, 128, 128)]
W = H = 256
BAND_H = 32

# HDR 色带（scRGB 线性值，1.0 = 80 nits）：黑 / 1000nits 白 / 200 红 / 200 绿 / 200 蓝 /
# 200 黄 / 200 青 / 120 中灰。必须与 NvencColorDiag.BuildHdrBands 的表逐值一致。
_v200, _v1000, _v120 = 200.0 / 80.0, 1000.0 / 80.0, 120.0 / 80.0
BANDS_HDR = [(0, 0, 0), (_v1000, _v1000, _v1000), (_v200, 0, 0), (0, _v200, 0),
             (0, 0, _v200), (_v200, _v200, 0), (0, _v200, _v200), (_v120, _v120, _v120)]

# kr, kg, kb —— ITU-R BT.601-7 / BT.709-6 / BT.2020-2
MATRICES = {
    "601": (0.2990, 0.5870, 0.1140),
    "709": (0.2126, 0.7152, 0.0722),
    "2020": (0.2627, 0.6780, 0.0593),
}

# ── ST.2084 / BT.2100 PQ（HDR 色带用）──
# 常数是公开的 PQ 定义值；公式若与产品实现不符，下面的"量尺自检"（libaom_hdr 那份）
# 会先失败 ⇒ 判不出来就停，不会拿错的预测去下结论。
M1 = 2610.0 / 16384.0
M2 = 2523.0 / 32768.0
C1 = 3424.0 / 4096.0
C2 = 2413.0 / 4096.0 * 32.0
C3 = 2392.0 / 4096.0 * 32.0
PAPER_WHITE_NITS = 80.0        # 管线口径：scRGB 1.0 = 80 nits


def pq_signal(v_scrgb):
    """scRGB 线性值 → PQ 编码信号（0..1，即 10-bit 码值 /1023）。"""
    if v_scrgb <= 0.0:
        return 0.0                                  # 黑点必须为 0（纯公式在 0 处给 ~0.097）
    lm = (v_scrgb * PAPER_WHITE_NITS) / 10000.0
    num = C1 + C2 * (lm ** M1)
    den = 1.0 + C3 * (lm ** M1)
    return min(1.0, max(0.0, (num / den) ** (1.0 / M2)))


def predict(rgb, mk, limited, hdr=False):
    """按候选矩阵/摆幅算 10-bit YCbCr 码值。

    SDR：输入是 sRGB 编码后的 R'G'B'（0..255），不做线性化 —— 矩阵本来就定义在
         传输码（non-linear）域上，这一点与 libaom/NVENC 的实际做法一致。
    HDR：输入是 scRGB 线性值，先过 PQ 得到 E'（0..1）再进同一组公式。
    """
    kr, kg, kb = MATRICES[mk]
    if hdr:
        r, g, b = (pq_signal(v) for v in rgb)
    else:
        r, g, b = [v / 255.0 for v in rgb]
    y = kr * r + kg * g + kb * b
    cb = (b - y) / (2.0 - 2.0 * kb) + 0.5
    cr = (r - y) / (2.0 - 2.0 * kr) + 0.5
    if limited:
        f = lambda v: 64.0 + v * 876.0          # 10-bit studio swing: 64..940
    else:
        f = lambda v: v * 1023.0                # 10-bit full swing: 0..1023
    return f(y), f(cb), f(cr)


def decode_yuv(ffmpeg, path):
    """解码成 yuv444p10le 原始帧（不动 range ⇒ 拿到的就是码流里的值）。

    ⚠ 平面寻址是这里最容易写错的一环：rawvideo 是【逐平面连续】排布，
      某平面内 (x,y) 的字节偏移 = 平面基址 + (y*W + x)*2。
      写错成 (W + x) 或把平面基址当行号加，采样会落到越界位置，
      int.from_bytes(b'') 静默返回 0 ⇒ 六个候选的误差全都变成同一个常数（实测就是这样，
      被下面的"量尺自检"抓住）。所以这里额外断言采样值必须落在合理区间。
    """
    cmd = [ffmpeg, "-v", "error", "-i", path, "-frames:v", "1",
           "-pix_fmt", "yuv444p10le", "-f", "rawvideo", "-"]
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.decode("utf-8", "replace").strip() or "ffmpeg 解码失败")
    raw = r.stdout
    plane = W * H * 2                      # 每平面字节数（16-bit LE 采样）
    if len(raw) < plane * 3:
        raise RuntimeError(f"rawvideo 长度 {len(raw)} < 期望 {plane * 3}")

    def sample(base, px, py):
        vals = []
        for oy in range(-3, 4):
            for ox in range(-3, 4):
                off = base + ((py + oy) * W + (px + ox)) * 2
                vals.append(int.from_bytes(raw[off:off + 2], "little"))
        return sum(vals) / len(vals)

    out = []
    for i in range(len(BANDS)):
        cy = i * BAND_H + BAND_H // 2
        y = sample(0, 128, cy)
        cb = sample(plane, 128, cy)
        cr = sample(2 * plane, 128, cy)
        if not (0.0 <= y <= 1023.0 and 0.0 <= cb <= 1023.0 and 0.0 <= cr <= 1023.0):
            raise RuntimeError(f"色带 {i} 采样越界 (Y={y}) ⇒ 平面寻址又错了，别信任何结论")
        out.append((y, cb, cr))

    # 采样确实随颜色变化的自检：全 0 或全等说明读的是同一块内存
    ys = [t[0] for t in out]
    if len(set(round(v) for v in ys)) < 3:
        raise RuntimeError(f"Y 值几乎不随色带变化 {ys} ⇒ 采样无效")
    return out


def container_info(ffprobe, path):
    r = subprocess.run([ffprobe, "-v", "error", "-select_streams", "v:0",
                        "-show_entries",
                        "stream=pix_fmt,color_primaries,color_transfer,color_matrix,color_range",
                        "-print_format", "json", path], capture_output=True, text=True)
    return (json.loads(r.stdout or "{}").get("streams") or [{}])[0]


def read_colr_nclx(path):
    """直接读 ISOBMFF 的 colr(nclx) 原始字节。

    为什么不用 ffprobe 的 color_matrix：实测它对这两个文件的 matrix 一律不打印
    （连我们自己写进去的 1 也读不回来）⇒ 拿它当"声明了什么"的依据会误判。
    返回 (primaries, transfer, matrix, full_range_bit) 或 None。
    """
    d = open(path, "rb").read()
    i = d.find(b"colrnclx")
    if i < 0:
        return None
    b = d[i + 8:i + 15]
    if len(b) < 7:
        return None
    p, t, m = int.from_bytes(b[0:2], "big"), int.from_bytes(b[2:4], "big"), int.from_bytes(b[4:6], "big")
    return p, t, m, (b[6] >> 7) & 1


DECLARED_MATRIX = {1: "709", 5: "601", 6: "601", 9: "2020"}
MTX_CODE = {"601": "5", "709": "1", "2020": "9"}   # 反查：实测判出的矩阵 → 该写进 colr 的码点

# ── sweep 模式：下发给 NV_ENC_CONFIG_AV1.matrixCoefficients 的候选码点 ──
# 号域 = H.273（nvEncodeAPI.h 对 matrixCoefficients 的原文注释指向 ISO/IEC 23091-4/H.273，
# 且其枚举 NV_ENC_VUI_MATRIX_COEFFS 就是 H.273 表 C-4 的子集）。
SWEEP_CODES = [0, 1, 2, 5, 6, 9]
MATRIX_NAME = {0: "identity/RGB", 1: "BT.709", 2: "unspecified",
               5: "BT.470BG(601-625)", 6: "SMPTE170M(601-525)", 9: "BT.2020 NCL"}

# 拟合容差，单位是 10-bit 码值(/1023)。两端都有实测锚点，不是拍的：
#   下界（噪声地板）= 我们自己的量尺路 libaom/SDR 4.0、对照路 HDR 1.0~1.2
#                    —— 纯色带 + 无损解码下唯一来源是舍入与色度抽样；
#   上界（最小真实可分辨差距）= 709 与 2020 的对照拟合误差 9.8（本文件实测）。
# 取 6.0：留出 1.5 倍噪声余量，又仍低于最小的真实差距。
TOL = 6.0


def fit(measured, hdr=False):
    """六个候选逐个试，返回按最大绝对误差排序的结果。"""
    scores = []
    for mk in MATRICES:
        for limited in (False, True):
            worst = 0.0
            for rgb, (my, mcb, mcr) in zip(BANDS_HDR if hdr else BANDS, measured):
                py, pcb, pcr = predict(rgb, mk, limited, hdr)
                worst = max(worst, abs(my - py), abs(mcb - pcb), abs(mcr - pcr))
            scores.append((worst, mk, limited, None))
    scores.sort(key=lambda t: t[0])
    return scores


def verdict(scores, tol=TOL, label=""):
    """判据：① 最优候选必须**近乎精确**（≤TOL）；② 次优候选必须**落在容差外**。

    为什么不再用"与次优相差 ≥20"这种固定余量：三个矩阵两两之间的可分辨距离本来就
    不相等 —— 实测 601↔709/2020 的差别给到 24.9，而 709↔2020 只差 9.8
    （kr/kg/kb 本身就接近）。固定余量会把一个"最优 1.2、次优 11.0"的正确结论
    判成"判不出"，那是拿一把与对象无关的尺去量。
    换成"容差内恰好一个候选"，问的就是唯一性本身：噪声地板与真差距各管各的。

    两个尺度都由实测锚定，见 TOL 的注释。任一条不成立就报"判不出"，
    调用方保持现状、不进产品代码。
    """
    (w1, mk1, lim1, _), (w2, mk2, _, _) = scores[0], scores[1]
    if w1 > tol:
        print(f"    ⚠ {label}最优候选 {mk1} 的绝对误差 {w1:.1f}/1023 已超容差 {tol:.0f} ⇒ "
              f"模型或采样没对上，判不出")
        return None
    if w2 <= tol:
        print(f"    ⚠ {label}{mk1}({w1:.1f}) 与 {mk2}({w2:.1f}) 都在容差 {tol:.0f} 内 ⇒ "
              f"答案不唯一，判不出")
        return None
    return mk1, lim1, w1, w2


def range_from_black(measured):
    """只用黑带判摆幅 —— 不需要 PQ 公式，也不需要矩阵。

    黑带 (0,0,0) 经任何线性矩阵都得到 Y=0，所以：full ⇒ 码值≈0，limited ⇒ 码值=64。
    这条是 HDR 那一路唯一能站得住的摆幅判据（PQ 模型与产品实现是否逐位一致，
    不在本函数的前提里）。返回 "full" / "limited" / None（判不出）。
    """
    y_black = measured[0][0]
    if y_black < 32.0:
        return "full"
    if y_black > 48.0:
        return "limited"
    return None


def invert_matrix(y, cb, cr, mk):
    """YCbCr(归一化信号域) → R'G'B' 信号，与 predict 同一套定义的逆运算。"""
    kr, kg, kb = MATRICES[mk]
    b = y + (2.0 - 2.0 * kb) * (cb - 0.5)
    r = y + (2.0 - 2.0 * kr) * (cr - 0.5)
    g = (y - kr * r - kb * b) / kg
    return r, g, b


def to_signal(code, full):
    return code / 1023.0 if full else (code - 64.0) / 876.0


def to_code(sig, full):
    return sig * 1023.0 if full else 64.0 + sig * 876.0


def fit_against_reference(ref, tgt, ref_mk, ref_full, tgt_full):
    """HDR 的矩阵判据 —— 不需要 PQ 公式。

    两份文件编的是【同一组带值】，而参照那份（libaom）的矩阵是我们自己下发给 avifenc 的，
    且它按声明执行（SDR 那组已验证：判出 709/full、误差 4.0）。
    所以先把参照文件的 YCbCr 反解成信号 E'R'G'B'（这就是硬件实际收到的那组 PQ 码），
    再用候选矩阵正变换、与硬件文件的实测值比。
    全程不碰 PQ 定义 ⇒ 结论与"我的 PQ 公式是否逐位一致"无关。
    """
    scores = []
    for mk in MATRICES:
        kr, kg, kb = MATRICES[mk]
        worst = 0.0
        for r_meas, t_meas in zip(ref, tgt):
            s = [to_signal(v, ref_full) for v in r_meas]
            er, eg, eb = invert_matrix(s[0], s[1], s[2], ref_mk)
            y = kr * er + kg * eg + kb * eb
            cb = (eb - y) / (2.0 - 2.0 * kb) + 0.5
            cr = (er - y) / (2.0 - 2.0 * kr) + 0.5
            worst = max(worst,
                        abs(t_meas[0] - to_code(y, tgt_full)),
                        abs(t_meas[1] - to_code(cb, tgt_full)),
                        abs(t_meas[2] - to_code(cr, tgt_full)))
        scores.append((worst, mk, not tgt_full, None))
    scores.sort(key=lambda t: t[0])
    return scores


def run_sweep(ffmpeg, ffprobe):
    """判两件事：① 驱动听不听 matrixCoefficients；② 听的话哪个码点对应哪张矩阵。

    与主判据的分工：主判据核对"声明 vs 像素"（产品验收），扫描只回答"能不能让硬件
    做我们要的变换"。所以这里【不看容器声明】—— 扫描期间 colr 仍按产品逻辑写，
    与本地下发的码点无关，看了只会误导。
    """
    print("\n═══ matrixCoefficients 码点扫描（H.273 号域，只看像素）═══")
    ref_path = os.path.join(DIR, "bands_libaom_hdr.avif")
    ref = None
    if os.path.isfile(ref_path):
        try:
            m = decode_yuv(ffmpeg, ref_path)
            ref = (m, range_from_black(m) == "full")
        except Exception as ex:
            print(f"⚠ HDR 参照样张不可用: {ex}")
    else:
        print("⚠ 缺 bands_libaom_hdr.avif ⇒ HDR 一路没有参照，只判 SDR（不猜）")

    def pix10(p):
        """HDR 样张必须是 10-bit：退到 8-bit NVENC 通路时像素会被升位，拟合全盘作废。"""
        return "10" in (container_info(ffprobe, p).get("pix_fmt") or "")

    seen_sdr, seen_hdr = set(), set()
    for code in SWEEP_CODES:
        row = []
        sdr = os.path.join(DIR, f"bands_nvenc_mk{code}.avif")
        if os.path.isfile(sdr):
            try:
                meas = decode_yuv(ffmpeg, sdr)
                scores = fit(meas)
                v = verdict(scores, label=f"码点 {code} SDR ")
                got = v[0] if v else f"非候选矩阵(最优误差 {scores[0][0]:.1f})"
                row.append(f"SDR→{got}")
                if v:
                    seen_sdr.add((code, v[0]))
            except Exception as ex:
                row.append(f"SDR 解码失败({ex})")
        else:
            row.append("SDR 缺样张")

        hdr = os.path.join(DIR, f"bands_nvenc_hdr_mk{code}.avif")
        if ref is None:
            row.append("HDR 无参照")
        elif not os.path.isfile(hdr):
            row.append("HDR 缺样张")
        elif not pix10(hdr):
            row.append("HDR 非 10-bit ⇒ 退到了 8-bit 通路，作废")
        else:
            try:
                meas = decode_yuv(ffmpeg, hdr)
                rng = range_from_black(meas)
                scores = fit_against_reference(ref[0], meas, "2020", ref[1], rng == "full")
                v = verdict(scores, label=f"码点 {code} HDR ")
                got = v[0] if v else f"非候选矩阵(最优误差 {scores[0][0]:.1f})"
                row.append(f"HDR→{got}/摆幅{rng or '判不出'}")
                if v:
                    seen_hdr.add((code, v[0]))
            except Exception as ex:
                row.append(f"HDR 解码失败({ex})")
        print(f"  {code:>3} ({MATRIX_NAME.get(code, '?')}): " + "  ".join(row))

    ok = True
    for label, pairs in (("SDR", seen_sdr), ("HDR", seen_hdr)):
        mats = {m for _, m in pairs}
        if len({c for c, _ in pairs}) < 2:
            print(f"❌ {label} 可判样张不足 2 个码点 ⇒ 无法判断驱动听不听该字段")
            ok = False
        elif len(mats) == 1:
            print(f"{label}: 所有可判码点都反推出同一张矩阵 {mats} ⇒ "
                  f"驱动【无视】matrixCoefficients，改声明或改软件做 RGB→YUV 才有解")
            ok = False
        else:
            print(f"{label}: 驱动【按字段换矩阵】 ⇒ "
                  + "、".join(f"码点 {c}={m}" for c, m in sorted(pairs)))
    if ok:
        want = {"709": None, "2020": None}
        for src in (seen_sdr, seen_hdr):
            for c, m in src:
                if m in want and want[m] is None:
                    want[m] = c
        print(f"⇒ SDR 要 BT.709 下发 {want['709']}；HDR 要 BT.2020 NCL 下发 {want['2020']}"
              f"（H.273 里都是固定号，正常取值应是 709→1、2020→9）")
    print("\n结论:", "扫描完成，按上面的映射下发码点" if ok else "扫描未给出可执行的映射 ⇒ 维持现状，不要凭猜改")
    return 0 if ok else 1


def main():
    global DIR
    argv = sys.argv[1:]
    # 门禁传 --samples 指到"当场产出、仓库内可见"的那份样张；不给就用默认目录。
    # ⚠ 显式指定时绝不回退读默认目录的陈旧样张（与 scripts/precision 同一约定）。
    if "--samples" in argv:
        i = argv.index("--samples")
        if i + 1 >= len(argv):
            print("❌ --samples 后面要跟目录", file=sys.stderr)
            return 2
        DIR = argv[i + 1]
        print(f"样张目录: {DIR}")

    ffmpeg, ffprobe = find_tool("ffmpeg"), find_tool("ffprobe")
    if not ffmpeg or not ffprobe:
        print("❌ 找不到 ffmpeg/ffprobe（判据不许用自研解码器替代）", file=sys.stderr)
        return 2

    if "--sweep" in argv:
        return run_sweep(ffmpeg, ffprobe)

    # (键, 是否 HDR, 期望矩阵, 期望摆幅) —— 后两项是"量尺自检"：软件路的矩阵与摆幅是
    # 我们自己下发给 avifenc 的，脚本必须先判对它们，才有资格报硬件路的结论。
    # *_wg = 广色域 SDR（primaries=9 + HDR 关）：两侧下发的都是 `9/13/9`（matrix 按三原色取，
    #        见 FormatHelper.AvifMatrixCoefficients）⇒ 期望值必须是 2020，不是 709。
    CASES = [("libaom", False, "709", "full"), ("nvenc", False, None, None),
             ("libaom_wg", False, "2020", "full"), ("nvenc_wg", False, None, None),
             ("qsv", False, None, None),
             ("libaom_hdr", True, "2020", "full"), ("nvenc_hdr", True, None, None)]

    files = {k: os.path.join(DIR, f"bands_{k}.avif") for k, *_ in CASES}
    for key, *_ in CASES:
        if not os.path.isfile(files[key]):
            print(f"⚠ 缺 {key} 样张 ⇒ 该路未判定（对应后端不可用就跳过，不拿别的产物冒充）")
    # 软件参照缺 = 前置不满足（判据无从执行）；硬件样张全缺 = 本机没这块硬件 ⇒ SKIP。
    # 这两种情形不能混成一个退出码：门禁把 3 当"合理跳过"，把非 0 非 3 当失败。
    for key in tuple(k for k, *_ in CASES if k.startswith("libaom")):
        if not os.path.isfile(files[key]):
            print(f"❌ 缺软件参照样张 {files[key]}：先跑 "
                  f"dotnet run --project src/TrueToneCap.Tools -c Release -- --nvenc-color-probe")
            return 2
    hwKeys = tuple(k for k, *_ in CASES if k.startswith("nvenc") or k == "qsv")
    if not any(os.path.isfile(files[k]) for k in hwKeys):
        print("SKIP: 一份硬件样张都没有（本机无 N 卡/核显硬件编码，或硬件编码被关）⇒ 本判据不适用")
        return 3

    ok = True
    results = {}
    hdr_measured = {}          # 参照（libaom_hdr）与硬件（nvenc_hdr）的实测值都要留着做对照
    for key, is_hdr, want_mk, want_rng in CASES:
        p = files[key]
        if not os.path.isfile(p):
            print(f"\n── {key}: 无样张（后端不可用），跳过")
            continue
        info = container_info(ffprobe, p)
        print(f"\n── {key}: {os.path.basename(p)}" + ("（HDR/PQ）" if is_hdr else ""))
        print("   容器: " + "  ".join(f"{k}={info.get(k) or '?'}" for k in
              ("pix_fmt", "color_primaries", "color_transfer", "color_matrix", "color_range")))
        try:
            measured = decode_yuv(ffmpeg, p)
        except Exception as ex:
            print(f"   ❌ 解码失败: {ex}")
            ok = False
            continue

        if is_hdr:
            # HDR 这一路：PQ 模型与产品实现没对上（六个候选误差全相等 ⇒ 自检必然失败），
            # 所以【矩阵判不了就明说判不了】，绝不拿猜的值进容器。
            # 但摆幅有一条不依赖 PQ、也不依赖矩阵的判据：黑带。
            rng = range_from_black(measured)
            colr = read_colr_nclx(p)
            declared = ("full" if colr[3] else "limited") if colr else "?"
            print(f"   [摆幅·黑带判据] 黑带 Y={measured[0][0]:.1f}/1023 ⇒ 实测 {rng or '判不出'}；容器声明 {declared}")
            if rng is None:
                print("   ⚠ 黑带落在 32..48 之间 ⇒ 判不出摆幅")
                ok = False
            elif rng != declared:
                print("   ❌ HDR 容器声明的 full_range 与实测不符 ⇒ 要么改驱动设置，要么改回声明")
                ok = False
            else:
                print("   ✅ HDR 摆幅声明与实测一致")
            hdr_measured[key] = (measured, rng == "full")
            ref = hdr_measured.get("libaom_hdr")
            if key == "libaom_hdr" or ref is None or rng is None:
                if key != "libaom_hdr":
                    print("   ⏭ HDR 矩阵：缺参照样张 ⇒ 未判定")
                continue
            # 对照判据：libaom 那份按我们自己下发的 2020 转换（SDR 已验证它守声明），
            # 反解出信号后再用候选矩阵正变换去凑硬件那份。
            scores = fit_against_reference(ref[0], measured, "2020", ref[1], rng == "full")
            print("   [HDR 矩阵·对照判据] 以 libaom(声明 2020) 反解信号后正变换比对：")
            for w, m, _l, _d in scores[:3]:
                print(f"      候选 {m}: 最大误差 {w:7.1f} /1023")
            dm2 = DECLARED_MATRIX.get(colr[2], f"?{colr[2]}") if colr else "?"
            # 判据必须"近乎精确 且 明显优于次优"两条同时成立（见 verdict 的说明）。
            # 这里差 1.1 vs 24.9 就是这个尺度上的事：解码 rounding 只有 ±1，
            # 所以 1.1 是"同一张矩阵"，24.9 是"另一张矩阵"——旧的"差距<30 即作废"
            # 规则会把一个已经精确到 1/1023 的结论判成判不出，那是量纲搞反了。
            v = verdict(scores, label="HDR ")
            if v is None:
                print("      ⇒ 判不出，容器保持现值不动")
                ok = False
            else:
                mk1, _lim1, w1, w2 = v
                if dm2 != mk1:
                    print(f"      ❌ 硬件实际用 {mk1}（误差 {w1:.1f}，次优 {w2:.1f} 已超容差），"
                          f"容器却声明 {dm2} ⇒ 谎报，必须改")
                    ok = False
                else:
                    print(f"      ✅ 硬件实际用 {mk1}（误差 {w1:.1f}，次优 {w2:.1f}），"
                          f"容器声明 {dm2} 一致")
            continue

        # ③ 验收：容器【声明】的 matrix / full_range 必须等于实测判出的那一个。
        #    这一条才是本探针存在的意义 —— 只报"实际用了哪张矩阵"没完，
        #    声明错了才是用户看到的偏色。矩阵由硬件自己决定，不同硬件可以不同，
        #    所以每个后端各自判、各自与自己的声明比（QSV 样张在时同理）。
        scores = fit(measured, is_hdr)
        results[key] = scores
        for worst, mk, limited, _ in scores[:3]:
            print(f"   候选 {mk}/{'limited' if limited else 'full'}: 最大误差 {worst:7.1f} /1023")
        v = verdict(scores, label=f"{key} ")
        if v is None:
            print(f"   ⇒ 判不出，容器保持现值不动（别拿这个结论去填字段）")
            ok = False
            continue
        mk, limited, w1, w2 = v
        colr = read_colr_nclx(p)
        if colr is None:
            print("   ⚠ 容器里没有 colr(nclx) ⇒ 无法核对声明")
            ok = False
        else:
            dm = DECLARED_MATRIX.get(colr[2], f"?{colr[2]}")
            dr = "full" if colr[3] else "limited"
            same = (dm == mk and dr == ("limited" if limited else "full"))
            print(f"   容器声明 matrix={colr[2]}({dm}) full_range={colr[3]}({dr})"
                  f"  vs 实测 {mk}/{'limited' if limited else 'full'}"
                  f"（误差 {w1:.1f}，次优 {w2:.1f}）"
                  f"  {'✅ 一致' if same else '❌ 声明与实际不符'}")
            if not same:
                print(f"   ⇒ 应写 matrixCoefficients={MTX_CODE[mk]}、"
                      f"full_range={'1' if not limited else '0'}")
            ok = ok and same

        # ④ 量尺自检：软件路必须判成我们自己声明的那一组
        if want_mk is not None:
            got_rng = "full" if not limited else "limited"
            good = (mk == want_mk and got_rng == want_rng)
            print(f"   [量尺自检] 期望 {want_mk}/{want_rng}，判出 {mk}/{got_rng} "
                  f"{'✅ 方法可信' if good else '❌ 公式或采样有误 ⇒ 本脚本全部结论作废'}")
            ok = ok and good

    # ① 量尺自检：libaom 那份必须判成 (709, full) —— 那是我们自己声明的、且容器已佐证
    if "libaom" in results:
        worst, mk, limited, _ = results["libaom"][0]
        good = (mk == "709" and not limited)
        print(f"\n[量尺自检] libaom 判为 {mk}/{'limited' if limited else 'full'} "
              f"{'✅ 与我们的声明一致 ⇒ 方法可信' if good else '❌ 与声明不符 ⇒ 本脚本结论全部作废'}")
        ok = ok and good

    print("\n结论:", "判定完成，可按上面的实测值改声明" if ok else "判据未通过 ⇒ 停在测量阶段，不要改产品代码")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
