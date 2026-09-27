# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
"""判据：JXL 的 HDR(PQ) 产物，容器里【最后剩下的传递函数】到底是不是 PQ。

要办的账（交接 §31.3-5 / §32.9-6 第 5 行，当年"卡在本机无 JXL 解码器"）：
  HDR + DisplayP3 这一路，产品会把手上那份 P3 ICC 交给 cjxl，而那份 ICC 是
  `ColorProfileProvider.PatchSrgbPrimaries(...)` —— 只换 rXYZ/gXYZ/bXYZ/desc，
  **TRC 沿用 sRGB 模板那条曲线**。当年写下的怀疑是"注入 SDR 曲线的 P3 ICC，且与
  `-x color_space=Rec2100PQ` 并存"。本轮把三个问题一次问清：
    Q1 并存时谁赢            —— 用产品自己那份 ICC + 产品自己那个中转文件，四种参数形态各编一遍；
    Q2 赢的那份 TRC 是什么    —— jxlinfo 读 Transfer function，再用 djxl 导出【消费者实际拿到的
                                 那份 ICC】按公共规范解 rTRC，与 IEC 61966-2-1 / ST 2084 两条
                                 参考曲线做偏差比对；
    Q3 那份 ICC 进没进容器    —— jxlinfo 的 "N-byte ICC profile" 行 + djxl `--orig_icc_out`
                                 （只在码流真的内嵌时才会写出文件）。

取证纪律（都踩过坑，见 §56.4）：
  · 容器声明一律由【第三方实现】读回：libjxl 的 jxlinfo / djxl（本机 v0.12.0 a7a9c78 构建）。
    本脚本不调用本仓库任何 JXL 解析器。
  · "读不到 ICC"这种负断言必须有控制组：同一种形态编一枚【必然内嵌】的（曲线表达不了的
    ICC ⇒ libjxl 只能整份塞进容器），控制组读不出来 ⇒ 本轮判不了 ⇒ SKIP，不给绿。
  · primaries 是谁写进去的、谁是读的时候归的最近码点，要分清：本脚本只认 jxlinfo；
    ffprobe 会把自定义原色归到最近的 H.273 码点（§56.4 已吃过一次错判），这里根本不用它。

用法: py scripts/precision/jxl_hdr_p3_transfer_readback.py [--samples 目录]
      前置：先跑 `dotnet run --project src/TrueToneCap.Tools -- --jxl-color-product`
      产出 hdr_*.jxl / product_input.png / product_p3_icc.icc。
退出码（与 precision_env 同口径）：0 = 判了且通过 / 1 = 判红 / 3 = 前置不齐，这一轮没判。
"""
from __future__ import annotations

import hashlib
import os
import re
import struct
import subprocess
import sys

import precision_env as pe
from icc_readback_matrix import GAMUT_PRIMARIES, D50_TO_D65, check_profile

DEFAULT_DIR = os.path.join(pe.repo_root(), "tests", "output", "jxl-hdr-p3-2026-09-27")

# ── 参考曲线：一律取公开规范的常数，不抄本仓库注释里的数 ─────────────────
# IEC 61966-2-1 (sRGB) 的分段 EOTF
def srgb_eotf(v: float) -> float:
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

# ITU-R H.265/ST 2084 (PQ) EOTF，把归一码值换算成 nits
PQ_M1, PQ_M2 = 2610 / 16384, (2523 / 4096) * 128
PQ_C1, PQ_C2, PQ_C3 = 3424 / 4096, (2413 / 4096) * 32, (2392 / 4096) * 32

def pq_eotf_nits(v: float) -> float:
    if v <= 0:
        return 0.0
    p = v ** (1 / PQ_M2)
    num = max(p - PQ_C1, 0.0)
    den = PQ_C2 - PQ_C3 * p
    if den <= 0:
        return 0.0
    return 10000.0 * (num / den) ** (1 / PQ_M1)

GAMMA26_EOTF = lambda v: v ** 2.6        # ICC 里 Display P3 常见的另一种写法


def sha12(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()[:12]


def md5(path: str) -> str:
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def run(exe: str, *args: str, cwd: str | None = None):
    p = subprocess.run([exe, *args], capture_output=True, text=True, cwd=cwd,
                       encoding="utf-8", errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


# ── ICC 内容：TRC 与 primaries（标签布局按 ICC.1 公开规范，与 icc_readback_matrix 同源）──
def icc_tags(prof: bytes) -> dict[str, int]:
    n = struct.unpack(">I", prof[128:132])[0]
    out = {}
    for i in range(n):
        e = 132 + i * 12
        sig = prof[e:e + 4].decode("latin1")
        off, ln = struct.unpack(">II", prof[e + 4:e + 12])
        out[sig] = off
    return out


def read_trc(prof: bytes, off: int):
    """返回 (类型说明, 把归一码值换算成线性值的函数) ；解不出则 (说明, None)。

    偏移一律按本产物实测：para 的布局是 'para'(4) + reserved(4) + 函数型(2) + reserved(2)
    + 五个 s15Fixed16 ⇒ **参数从 +12 起**（先按 +10 读会把 gamma 读成 3.05e-05）。
    函数型 3 的五个参数在 libjxl 写出的那份 profile 里实测为
    2.4 / 0.947867 / 0.052133 / 0.0773997 / 0.04045，逐项等于 IEC 61966-2-1 自己的
    g、1/1.055、0.055/1.055、1/12.92、0.04045 ⇒ 这条就是 sRGB 的分段 EOTF。
    """
    typ = prof[off:off + 4].decode("latin1")
    if typ == "curv":
        cnt = struct.unpack(">I", prof[off + 8:off + 12])[0]
        if cnt == 0:
            return "curv:linear", lambda v: v
        if cnt == 1:
            g = struct.unpack(">H", prof[off + 12:off + 14])[0] / 100.0
            return f"curv:gamma {g:.2f}", (lambda v, g=g: v ** g)
        vals = [struct.unpack(">H", prof[off + 12 + 2 * i:off + 14 + 2 * i])[0] / 65535.0
                for i in range(cnt)]
        def lut(v: float, vals=vals) -> float:
            x = min(max(v, 0.0), 1.0) * (len(vals) - 1)
            i0, i1 = int(x), min(int(x) + 1, len(vals) - 1)
            f = x - i0
            return vals[i0] * (1 - f) + vals[i1] * f
        return f"curv:LUT{len(vals)}", lut
    if typ == "para":
        fn = struct.unpack(">H", prof[off + 8:off + 10])[0]
        g, a, b, c, d = [struct.unpack(">i", prof[off + 12 + 4 * k:off + 16 + 4 * k])[0] / 65536.0
                         for k in range(5)]
        if fn == 0:
            return f"para:type0 g={g:.4f}", (lambda v: v ** g if v > 0 else 0.0)
        if fn == 1:
            return (f"para:type1 g={g:.4f} a={a:.6f} b={b:.6f}",
                    (lambda v: (a * v + b) ** g if a * v + b > 0 else 0.0))
        if fn == 2:
            return (f"para:type2 g={g:.4f} a={a:.6f} b={b:.6f} c={c:.6f} d={d:.6f}",
                    (lambda v: (a * v + b) ** g + c if a * v + b > 0 else d))
        if fn == 3:
            return (f"para:type3 g={g:.4f} a={a:.6f} b={b:.6f} c={c:.6f} d={d:.6f}",
                    (lambda v: (a * v + b) ** g if v >= d else c * v))
        return f"para:type{fn}", None
    return f"{typ!r}?", None


def trc_matches(fn, ref) -> float:
    """曲线上取 65 个采样点，返回与参考曲线的最大偏差（0 = 完全吻合）。"""
    if fn is None:
        return 9.0
    return max(abs(fn(i / 64.0) - ref(i / 64.0)) for i in range(65))


def primaries_xy(prof: bytes):
    """rXYZ/gXYZ/bXYZ（D50 PCS）→ 反向适应到 D65 → 色度坐标，与 GAMUT_PRIMARIES 可比。"""
    tg = icc_tags(prof)
    rows = [D50_TO_D65[0:3], D50_TO_D65[3:6], D50_TO_D65[6:9]]
    out = []
    for name in ("rXYZ", "gXYZ", "bXYZ"):
        off = tg[name]
        col = [struct.unpack(">i", prof[off + 8 + 4 * k:off + 12 + 4 * k])[0] / 65536.0
               for k in range(3)]
        v = [sum(r[c] * col[c] for c in range(3)) for r in rows]      # X65/Y65/Z65
        s = sum(v)
        out.append((v[0] / s, v[1] / s))
    return out


def nearest_gamut(xy) -> tuple[str, float]:
    best, dist = "?", 9.0
    for name, ref in GAMUT_PRIMARIES.items():
        d = max(abs(xy[i][j] - ref[i][j]) for i in range(3) for j in range(2))
        if d < dist:
            best, dist = name, d
    return best, dist


# ── 主流程 ───────────────────────────────────────────────────────────────
def main() -> int:
    args = sys.argv[1:]
    d = DEFAULT_DIR
    for i, a in enumerate(args):
        if a == "--samples" and i + 1 < len(args):
            d = args[i + 1]
    if not os.path.isdir(d):
        print(f"SKIP: 没有产物目录 {d}（先跑 --jxl-color-product）")
        return pe.EXIT_SKIP

    def tool(name: str, exe: str) -> str | None:
        cands = [c for c in pe.tool_candidates(name)
                 if os.path.basename(c).lower() == exe.lower()]
        return cands[0] if cands else None

    jxlinfo, djxl, cjxl = tool("jxlinfo", "jxlinfo.exe"), tool("djxl", "djxl.exe"), pe.find_tool("cjxl")
    if not (jxlinfo and djxl):
        print("SKIP: 没探到 jxlinfo / djxl（设 TTC_JXLINFO / TTC_DJXL，或把路径作为参数递进来）")
        print(f"      找过：{pe.tool_candidates('jxlinfo')} / {pe.tool_candidates('djxl')}")
        return pe.EXIT_SKIP
    if not cjxl:
        print("SKIP: 没探到 cjxl ⇒ Q1「并存时谁赢」这一层没法编，本轮不判")
        return pe.EXIT_SKIP

    prods = sorted(f for f in os.listdir(d) if re.match(r"^hdr_.*\.jxl$", f))
    png_in = os.path.join(d, "product_input.png")
    icc_p3 = os.path.join(d, "product_p3_icc.icc")
    if not prods or not (os.path.isfile(png_in) and os.path.isfile(icc_p3)):
        print(f"SKIP: {d} 下 {'缺 hdr_*.jxl' if not prods else '缺产品自己的 product_input.png / product_p3_icc.icc'}"
              " ⇒ 本判据没法在产品字节上判（先跑 --jxl-color-product）")
        return pe.EXIT_SKIP

    # 能力探测：jxlinfo 要能加载同目录的 jxl.dll；探不过就是"没判"，不是"通过"
    rc, txt = run(jxlinfo, os.path.join(d, prods[0]))
    if rc != 0 or "JPEG XL" not in txt:
        print(f"SKIP: jxlinfo 干不了活（exit={rc}）：{txt.strip()[:160]}")
        return pe.EXIT_SKIP
    print(f"工具: cjxl={cjxl}\n      djxl={djxl}\n      jxlinfo={jxlinfo}")
    print(f"      {run(cjxl, '--version')[1].splitlines()[0]} / "
          f"{run(djxl, '--version')[1].splitlines()[0]}")

    bad = 0
    print(f"\n── Q1：ICC 与 color fields 并存时谁赢（输入=产品自己写的那个 16-bit PNG 中转，"
          f"ICC=产品自己那份 P3 profile）──")
    forms = {
        "q1_icc_only": ["-x", f"icc_pathname={icc_p3}"],
        "q1_icc_first": ["-x", f"icc_pathname={icc_p3}", "-x", "color_space=Rec2100PQ"],
        "q1_cs_first": ["-x", "color_space=Rec2100PQ", "-x", f"icc_pathname={icc_p3}"],
        "q1_cs_only": ["-x", "color_space=Rec2100PQ"],
    }
    hashes, decls = {}, {}
    for name, extra in forms.items():
        out = os.path.join(d, name + ".jxl")
        if os.path.exists(out):
            os.remove(out)
        rc, o = run(cjxl, "-d", "1.0000", "-e", "9", *extra, "--intensity_target=1000", png_in, out)
        if rc != 0 or not os.path.exists(out):
            print(f"  ❌ {name}: cjxl 失败 exit={rc} {o.strip()[:120]}")
            bad += 1
            continue
        _, info = run(jxlinfo, out)
        tr = grab(info, r"Transfer function:\s*(.+)")
        pr = grab(info, r"Primaries:\s*(.+)")
        hashes[name], decls[name] = md5(out), (tr, pr)
        print(f"  {name:14} {os.path.getsize(out):4} B md5={hashes[name][:12]} → "
              f"Primaries=[{pr}] Transfer=[{tr}]")
    if len(hashes) == 4:
        # ICC 赢 ⇒ 「只给 ICC」与「两种顺序的并存」必须逐字节相同，且必须不同于「只给 fields」
        same = hashes["q1_icc_only"] == hashes["q1_icc_first"] == hashes["q1_cs_first"]
        diff = hashes["q1_icc_only"] != hashes["q1_cs_only"]
        print(f"  {'✅' if same else '❌'} 并存两种顺序 == 只给 ICC："
              f"{'逐字节相同' if same else '不同 ⇒ 顺序会影响结果，得单独追'}")
        print(f"  {'✅' if diff else '❌'} 只给 ICC != 只给 color_space：{'ICC 那份真的改变了产物' if diff else '两者同字节 ⇒ 有一路是空转'}")
        bad += (0 if same else 1) + (0 if diff else 1)

    # ── 控制组：证明"读不到内嵌 ICC"是有意义的负断言 ──────────────────────
    print("\n── 控制组：一枚【必然内嵌】的 ICC（曲线表达不了 ⇒ libjxl 只能整份塞进容器）──")
    cal = None
    for cand in (os.path.join(d, "cal.icc"),
                 os.path.join(pe.repo_root(), "tests", "output", "jxlprobe", "cal.icc")):
        if os.path.isfile(cand):
            cal = cand
            break
    if cal is None:
        cal = next((p for p in sorted(_glob_windows_cal()) if os.path.isfile(p)), None)
    if cal is None:
        print("  ❌ 找不到任何可当控制组的 ICC ⇒ 下面所有『无内嵌 ICC』的负断言都失去对照，本轮不判")
        return pe.EXIT_SKIP
    out = os.path.join(d, "q1_control_embedded.jxl")
    rc, _ = run(cjxl, "-d", "1.0000", "-e", "9", "-x", f"icc_pathname={cal}",
                "--intensity_target=1000", png_in, out)
    _, info = run(jxlinfo, out)
    emb = grab(info, r"^(\d+-byte ICC profile.*)$", ml=True)
    orig = os.path.join(d, "q1_control_orig_icc.icc")
    if os.path.exists(orig):
        os.remove(orig)
    run(djxl, f"--orig_icc_out={orig}", out, os.path.join(d, "q1_control.pam"))
    same_bytes = os.path.isfile(orig) and open(orig, "rb").read() == open(cal, "rb").read()
    print(f"  控制 ICC: {os.path.basename(cal)} {os.path.getsize(cal)} B "
          f"→ jxlinfo: [{emb or '没报内嵌'}] / djxl --orig_icc_out: "
          f"{'存在且逐字节相同' if same_bytes else '没写出文件'}")
    if not (emb and same_bytes):
        print("  ❌ 控制组读不回来 ⇒ 这台机器上这两个探测器不可信，负断言全部作废，本轮不判")
        return pe.EXIT_SKIP
    print("  ✅ 两个第三方探测器都认内嵌 ICC ⇒ 下面『无内嵌 ICC』是有效读数")

    print("\n── Q2/Q3：产品 HDR 产物的容器声明 + 消费者实际拿到的那份 ICC ──")

    # ── 自家解析器的锚点自检（先证"读数的人"没读错，再去读产品）────────────
    # 参照件用微软随系统那份 sRGB Color Space Profile.icm（第三方写的、公开规范的一致实现）：
    # 曲线必须落在 IEC 61966-2-1 上、原色必须落在 sRGB 上。任一条不过 ⇒ 是我的解析器失步，
    # 这一轮不判（不能拿一把没校过的尺子去量产品）。
    ref = os.path.expandvars(r"%SystemRoot%\System32\spool\drivers\color\sRGB Color Space Profile.icm")
    if not os.path.isfile(ref):
        print("  ❌ 找不到参照 profile（微软系统 sRGB）⇒ 本脚本的 ICC 解析器没校过尺，判不得")
        return pe.EXIT_SKIP
    rb = open(ref, "rb").read()
    rkind, rfn = read_trc(rb, icc_tags(rb)["rTRC"])
    rxy, rdev = nearest_gamut(primaries_xy(rb))
    print(f"  自检（参照 = 系统 sRGB profile {len(rb)} B）：rTRC={rkind} 与 IEC 61966-2-1 最大偏差="
          f"{trc_matches(rfn, srgb_eotf):.5f} / 原色最近={rxy}(残差 {rdev:.4f})")
    if trc_matches(rfn, srgb_eotf) > 0.01 or rxy != "srgb" or rdev > 0.012:
        print("  ❌ 参照件都读不对 ⇒ 解析器偏移/常数不可信，本轮不判")
        return pe.EXIT_SKIP
    print("  ✅ 解析器在第三方参照件上校过尺 ⇒ 下面对它读出的曲线与原色可以当证据")

    # 产品自己交给 cjxl 的那份 P3 profile —— §31.3-5 的字面前提，直接在字节上复判
    p3_bytes = open(icc_p3, "rb").read()
    kind3, fn3 = read_trc(p3_bytes, icc_tags(p3_bytes)["rTRC"])
    gam3, dev3 = nearest_gamut(primaries_xy(p3_bytes))
    ds3 = trc_matches(fn3, srgb_eotf)
    print(f"  产品交给 cjxl 的 {len(p3_bytes)} B P3 profile：原色最近={gam3}(残差 {dev3:.4f}) "
          f"rTRC={kind3} 与 IEC 61966-2-1 最大偏差={ds3:.5f} "
          f"⇒ {'【确实保留了 sRGB TRC】—— §31.3-5 的写入侧前提成立' if ds3 < 0.01 else 'TRC 不是 sRGB，前提需改写'}")
    if ds3 >= 0.01:
        bad += 1

    for f in prods:
        p = os.path.join(d, f)
        _, info = run(jxlinfo, p)
        tr = grab(info, r"Transfer function:\s*(.+)")
        pr = grab(info, r"Primaries:\s*(.+)")
        emb = grab(info, r"^(\d+-byte ICC profile.*)$", ml=True)
        inten = grab(info, r"Intensity target:\s*([\d.]+)")
        eff = os.path.join(d, f.replace(".jxl", "_effective.icc"))
        og = os.path.join(d, f.replace(".jxl", "_orig.icc"))
        for x in (eff, og):
            if os.path.exists(x):
                os.remove(x)
        run(djxl, f"--icc_out={eff}", f"--orig_icc_out={og}", p,
            os.path.join(d, f.replace(".jxl", "_dec.pam")))
        og_note = "无"
        if os.path.isfile(og):
            ogb = open(og, "rb").read()
            og_note = (f"{len(ogb)} B"
                       + ("＝产品那份 ICC ⇒ 真的内嵌" if ogb == p3_bytes else "≠产品那份 ICC ⇒ 是解码端按 fields 现造的"))
        line = (f"  {f[:-4]:26} {os.path.getsize(p):4} B md5={md5(p)[:12]} "
                f"Primaries=[{pr}] Transfer=[{tr}] intensity={inten or '-'} "
                f"内嵌ICC=[{emb or '无'}] orig_icc_out=[{og_note}]")
        # 消费者那份 ICC 的 TRC：与两条参考曲线比偏差，谁近说是谁
        trc_note = ""
        is_srgb = False
        if os.path.isfile(eff):
            prof = open(eff, "rb").read()
            errs = [e for e in check_profile(prof)]
            tg = icc_tags(prof)
            if "rTRC" in tg:
                kind, fn = read_trc(prof, tg["rTRC"])
                ds, dg = trc_matches(fn, srgb_eotf), trc_matches(fn, GAMMA26_EOTF)
                # PQ 的往返偏差：曲线值当 PQ 码值解出 nits，归一后与自变量比（0 = 就是 PQ）
                pq_dev = max(abs(pq_eotf_nits(fn(i / 64.0)) / 10000.0 - i / 64.0)
                             for i in range(1, 65))
                is_srgb = ds < 0.01
                is_pq = pq_dev < 0.01 and not is_srgb
                trc_note = (f"\n      消费者 ICC {len(prof)} B rTRC={kind}"
                            f"  与 sRGB 曲线最大偏差={ds:.4f} / 与 γ2.6={dg:.4f}"
                            f" / 与 PQ(往返)={pq_dev:.4f}")
                line += f"  ⇒ TRC 判读：{'sRGB' if is_srgb else 'PQ' if is_pq else '两者都不是'}"
            else:
                # 曲线不在 rTRC —— libjxl 给 PQ 造的那份用的是 Lab PCS + A2B0，
                # 这本身是一条读数（SDR 那格能写字段、PQ 那格只能塞 LUT），不是解析失败。
                trc_note = (f"\n      消费者 ICC {len(prof)} B 无 rTRC（PCS="
                            f"{prof[20:24].decode('latin1')!r}，曲线在 "
                            f"{'A2B0' if 'A2B0' in tg else '?'} 里）⇒ rTRC 这条判据不适用")
            xy = primaries_xy(prof)
            gam, gdev = nearest_gamut(xy)
            trc_note += (f"\n      消费者 ICC 原色→D65 后最近的公开色域：{gam}"
                         f"（最大残差 {gdev:.4f}，容差 0.012）")
            if errs and "PCS 不是 'XYZ '" not in errs[0]:
                trc_note += f"\n      ⚠ check_profile: {errs[0]}"
        print(line + trc_note)

        # 判据：像素是产品 HdrToPq16 写出来的 PQ ⇒ 声明必须是 PQ
        if "PQ" not in tr.upper():
            print(f"      ❌ 像素是 PQ（FormatHelper.HdrToPq16），容器却读回 Transfer=[{tr}]"
                  f"{'，消费者那份 ICC 的 rTRC 也正是 sRGB 曲线' if is_srgb else ''}"
                  f" ⇒ §31.3-5 的自相矛盾在产物上成立")
            bad += 1
        if f.startswith("hdr_DisplayP3") or f.startswith("hdr_DCI_P3"):
            if "P3" not in pr.upper():
                print(f"      ❌ 目标是 P3，读回 Primaries=[{pr}] ⇒ 色域声明没进去")
                bad += 1
            else:
                print(f"      ℹ️ P3 原色声明是对的（读回 [{pr}]）⇒ 错的只有传递函数那一维")

    # ── 后果：同一份 PQ 码值，按各自声明走第三方解码器会落到不同亮度 ────────
    print("\n── 后果读数（第三方 djxl 解码，不是本仓库的换算）──")
    a = pam_samples(os.path.join(d, "hdr_DisplayP3_dec.pam"))
    b = pam_samples(os.path.join(d, "hdr_BT2020_dec.pam"))
    if a and b:
        n = min(len(a), len(b))
        dev = [abs(a[i] - b[i]) for i in range(n)]
        print(f"  两枚产物【默认解码】的存储码值：max|Δ|={max(dev)}  mean|Δ|={sum(dev)/n:.1f}"
              "（0-65535 满量程）⇒ 像素基本同一份，差的只是声明")
        px0, px16 = rgb_at(a, 0, 0), rgb_at(a, 16, 0)
        q0, q16 = rgb_at(b, 0, 0), rgb_at(b, 16, 0)
        for (lbl, code, code2) in (("203-nit 档", px0, q0), ("100-nit 档", px16, q16)):
            codes = [c / 65535.0 for c in code]
            nits = [pq_eotf_nits(c) for c in codes]
            lin = [srgb_eotf(c) for c in codes]
            print(f"  {lbl}存进去的码值 P3格={code} BT2020格={code2} ⇒ 按 PQ 解释 = "
                  f"{[f'{v:.1f}' for v in nits]} nits；按容器声明的 sRGB 曲线解释 = 线性 "
                  f"{[f'{v:.4f}' for v in lin]}（同一个码值，sRGB 那条给出的线性值是 PQ 意图的 "
                  f"{lin[0]/max(nits[0]/10000.0, 1e-9):.1f} 倍）")
    for cs in ("RGB_D65_202_Rel_PeQ",):
        p1 = os.path.join(d, f"hdr_DisplayP3__{cs}.pam")
        p2 = os.path.join(d, f"hdr_BT2020__{cs}.pam")
        run(djxl, f"--color_space={cs}", os.path.join(d, "hdr_DisplayP3.jxl"), p1)
        run(djxl, f"--color_space={cs}", os.path.join(d, "hdr_BT2020.jxl"), p2)
        s1, s2 = pam_samples(p1), pam_samples(p2)
        if s1 and s2:
            n = min(len(s1), len(s2))
            dev = [abs(s1[i] - s2[i]) for i in range(n)]
            e1 = [pq_eotf_nits(c / 65535.0) for c in rgb_at(s1, 0, 0)]
            e2 = [pq_eotf_nits(c / 65535.0) for c in rgb_at(s2, 0, 0)]
            print(f"  把两枚产物都强制解到 {cs}（同一目标空间）：max|Δ|={max(dev)} "
                  f"mean|Δ|={sum(dev)/n:.1f} ⇒ 同一个 203-nit 档，一枚落到 "
                  f"{e1[0]:.1f} nits、另一枚落到 {e2[0]:.1f} nits（{e1[0]/max(e2[0],1e-9):.2f}×）")

    print("\n结论: " + ("JXL HDR 各格的容器声明都与 PQ 像素一致" if bad == 0
                  else f"{bad} 格不一致 ⇒ §31.3-5 成立（HDR 容器带 SDR 曲线），别当已修"))
    return pe.EXIT_OK if bad == 0 else pe.EXIT_FAIL


def _glob_windows_cal():
    import glob
    return glob.glob(os.path.expandvars(
        r"%SystemRoot%\System32\spool\drivers\color\*校准*"))


def grab(text: str, pat: str, ml: bool = False) -> str:
    m = re.search(pat, text, re.M if ml else 0)
    return m.group(1).strip() if m else ""


def rgb_at(samples, y: int, x: int, w: int = 32):
    """PAM 解出来的是 RGBA 逐样本平铺 ⇒ 按 (行, 列) 取那一像素的 RGB。"""
    i = (y * w + x) * 4
    return tuple(samples[i:i + 3])


def pam_samples(path: str):
    """netpbm P7/PAM（公开布局）：头部若干行 + ENDHDR + 大端 16-bit 采样。"""
    if not os.path.isfile(path):
        return None
    b = open(path, "rb").read()
    i = b.find(b"ENDHDR")
    if i < 0:
        return None
    body = b[i + 6 + 1:]
    return list(struct.unpack(">" + "H" * (len(body) // 2), body))


if __name__ == "__main__":
    sys.exit(main())
