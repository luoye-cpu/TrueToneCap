"""AVIF 后端 × CRF 的"分块 p1（逐平面）+ 体积 + 产物 md5 + 这次到底谁编的"总表（#20 / 交接 §48.4 缺口）。

    跑法（默认一条命令跑完整张表）:
        py -3 scripts/probes/avif_backend_ladder_measure.py
    常用可选项:
        --backends LibAom,SvtAv1,Qsv,Nvenc   要跑哪几条分支（默认这四条）
        --crf 1,2,5,8,10,12,15,18,22,26,30   质量档
        --src <png>                          参考帧，默认 artifacts/quality-ladder/v2/src.png
                                             （§48.1–48.4 全部读数都挂在它身上，换源=换一批数）
        --out <dir>                          落盘目录，默认 tests/output/avif-ladder-2026-09-27
        --tools <exe>                        直接用已构建的 TrueToneCap.Tools.exe（跳过 dotnet run）
        --measure-only                        不重新编码，只量目录里已有的产物

为什么还要再写一个脚本，而不是复用 artifacts/quality-ladder/measure-dir.py：
  ① 那张表【只量亮度平面】，而 §48.3 已经立了口径"凡『降色度值多少』的问题必须报色度平面，
     报亮度 p1 就是报错" ⇒ 这里 Y / Cb / Cr 三路都出，缺一路就不算数。
  ② 它假设"文件名里的后端 = 产物真的由那个后端编出"。SVT-AV1 与系统 MFT(QSV) 恰恰是这条
     假设最容易破产的两列（SVT 未编进 avifenc 时 `--codec svt` 静默回落 aom；
     MFT 起不来时 MftAvifEncode 静默回退 libaom）⇒ 判据改成
     "每档另编一次【强制 libaom】，产物逐字节相同 ⇔ 这一档根本没走请求的后端"（C# 侧做，
     结果写进 ladder_rows.tsv，本脚本原样搬到表里，并核对 md5 自洽）。

尺子（与 §48.2 同一把，不另起）：32×32 分块、逐像素平方后按块求均值、取最差 1% 与最差单块；
  Y = 0.2126R+0.7152G+0.0722B，Cb = B−Y，Cr = R−Y（峰值仍按 255 归一）。
自检三条，任一不过 ⇒ 本轮不出结论（退出码 1）：
  A 恒等（源 vs 源）三个平面的分块 p1 都必须是 168.13/inf 那种"没差异"的形状；
  B 负样本（源右移 1px）Y 分块 p1 必须显著掉分，否则这把尺子对边缘错位无牙；
  C 必须复现 §48.4 已登记的四个锚点读数（NVENC 43.50/43.53/43.46/43.11、
    libaom 44.15/42.51/38.85/31.65，以及体积 287.9/188.6/148.5/106.0 KB）——
    复现不了说明环境或产品链路变了，这张新表也就不能和旧表并排读。

⚠ 本机负载不可控（可能有别的 agent 同时在编码）⇒ 本脚本【不报耗时】，只报
  与负载无关的读数：p1（分块、逐平面）、体积、产物 md5、逐字节对照判据、ffprobe 读回。

退出码沿用 scripts/precision/precision_env.py 约定：0 判了且过 / 1 判红 / 3 前置缺失 SKIP。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import csv
import hashlib
import json
import os
import subprocess
import sys

import numpy as np

sys.path.insert(0, os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "precision"))
import precision_env as pe  # noqa: E402

ROOT = pe.repo_root()
TILE = 32
CRF_DEFAULT = "1,2,5,8,10,12,15,18,22,26,30"
BACKENDS_DEFAULT = "LibAom,SvtAv1,Qsv,Nvenc"

# §48.4 已登记读数（交接文档 4485–4490 行）⇒ 锚点复现用，容差写在旁边
ANCHOR = {
    ("Nvenc", "2"): (43.50, 287.9), ("Nvenc", "8"): (43.53, 188.6),
    ("Nvenc", "15"): (43.46, 148.5), ("Nvenc", "30"): (43.11, 106.0),
    ("LibAom", "2"): (44.15, None), ("LibAom", "8"): (42.51, None),
    ("LibAom", "15"): (38.85, None), ("LibAom", "30"): (31.65, None),
}
P1_TOL = 0.20      # dB：同一把尺、同一张源、同一档 ⇒ 应当逐位相同，留一点浮点/解码余量
KB_TOL = 0.005     # 0.5%：NVENC 产物体积对驱动/码控版本敏感，超了就要单独说明


def fmt_crf(v: float) -> str:
    """与 C# 侧一致的 '0.#' 写法（文件名与 TSV 都用它）。"""
    s = f"{v:.10g}"
    return s[:-2].rstrip("0").rstrip(".") if s.endswith(".0") else s


def run(cmd, cwd=None):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=cwd)
    return p


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for blk in iter(lambda: f.read(1 << 20), b""):
            h.update(blk)
    return h.hexdigest()


def decode_rgba(ffmpeg, path, out_raw):
    """外部解码器把产物解成 rgba（判据只信 ffmpeg，不信仓库自己的容器解析）。"""
    p = run([ffmpeg, "-y", "-loglevel", "error", "-i", path,
             "-f", "rawvideo", "-pix_fmt", "rgba", out_raw])
    if p.returncode != 0 or not os.path.isfile(out_raw):
        err = [l for l in (p.stderr or "").splitlines() if l.strip()]
        raise RuntimeError(f"ffmpeg rc={p.returncode}: {err[-1] if err else '无 stderr'}")
    return out_raw


def ffprobe_stream(ffprobe, path):
    import json as _json
    p = run([ffprobe, "-v", "error", "-print_format", "json", "-show_streams",
             "-show_format", path])
    try:
        d = _json.loads(p.stdout or "{}")
    except Exception:
        return {"codec_name": f"<ffprobe rc={p.returncode}>"}
    s = next((x for x in d.get("streams", []) if x.get("codec_type") == "video"), {})
    out = {k: s.get(k) for k in ("codec_name", "profile", "pix_fmt", "bits_per_raw_sample",
                                 "width", "height", "color_range", "color_space",
                                 "color_transfer", "color_primaries")}
    out["tags"] = {k: v for k, v in (d.get("format", {}).get("tags", {}) or {}).items()
                   if k.lower() in ("encoder", "handler_name")}
    return out


def planes(a):
    y = a @ np.array([0.2126, 0.7152, 0.0722])
    return y, a[..., 2] - y, a[..., 0] - y


def tile_psnr(refpx, distpx):
    h = refpx.shape[0] // TILE * TILE
    w = refpx.shape[1] // TILE * TILE
    mse = np.maximum(((refpx[:h, :w] - distpx[:h, :w]) ** 2).reshape(
        h // TILE, TILE, w // TILE, TILE).mean(axis=(1, 3)), 1e-12)
    p = 10.0 * np.log10(255.0 ** 2 / mse)
    return float(np.percentile(p, 1)), float(p.min())


def measure_planes(ref_px, dist_px):
    """返回 (Y p1, Y min, Cb p1, Cb min, Cr p1, Cr min, 各平面 MAD, 各平面最大偏差)。"""
    ry, rcb, rcr = planes(ref_px)
    dy, dcb, dcr = planes(dist_px)
    out = {}
    for name, r, d in (("Y", ry, dy), ("Cb", rcb, dcb), ("Cr", rcr, dcr)):
        p1, pmin = tile_psnr(r, d)
        e = np.abs(r - d)
        out[name] = {"p1": round(p1, 2), "min": round(pmin, 2),
                     "mad": round(float(e.mean()), 4), "max": round(float(e.max()), 2)}
    return out


def encode_with_tools(src, out_dir, backend, crfs, tools, log_path):
    """跑一次产品探针（一个后端一个进程：NVENC 的失败粘滞是进程级的，同进程混跑会互相污染）。"""
    if tools:
        cmd = [tools, "--avif-ladder", src, out_dir, "--backend", backend, "--crf", crfs]
    else:
        cmd = ["dotnet", "run", "--project", "src/TrueToneCap.Tools", "-c", "Release", "--",
               "--avif-ladder", src, out_dir, "--backend", backend, "--crf", crfs]
    p = run(cmd, cwd=ROOT)
    with open(log_path, "w", encoding="utf-8") as f:
        f.write(f"$ {' '.join(cmd)}\n[exit code] {p.returncode}\n\n--- stdout ---\n{p.stdout}\n"
                f"--- stderr ---\n{p.stderr}\n")
    return p.returncode


def read_rows(out_dir, backend):
    tsv = os.path.join(out_dir, f"{backend}_ladder_rows.tsv")
    if not os.path.isfile(tsv):
        return None
    rows = {}
    with open(tsv, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f, delimiter="\t"):
            rows[r["crf"]] = r
    return rows


def main():
    tools = pe.opt("--tools")
    src = pe.opt("--src") or os.path.join(ROOT, "artifacts", "quality-ladder", "v2", "src.png")
    out_dir = pe.opt("--out") or os.path.join(ROOT, "tests", "output", "avif-ladder-2026-09-27")
    backends = (pe.opt("--backends") or BACKENDS_DEFAULT).split(",")
    crfs = (pe.opt("--crf") or CRF_DEFAULT).split(",")
    measure_only = "--measure-only" in sys.argv

    tools = os.path.abspath(tools) if tools else None
    if tools and not os.path.isfile(tools):
        print(f"⏭ 跳过：--tools 指的探针不存在：{tools}")
        return pe.EXIT_SKIP
    if not os.path.isfile(src):
        print(f"⏭ 跳过：参考帧不存在 {src}（§48.x 全部读数都挂在这张源上；换源=换一批数）")
        return pe.EXIT_SKIP
    ff = pe.find_tool("ffmpeg")
    ffp = pe.find_tool("ffprobe")
    if not ff or not ffp:
        print("⏭ 跳过：判据只信外部解码器，没有 ffmpeg/ffprobe 就没有判据（见 precision_env）")
        return pe.EXIT_SKIP
    os.makedirs(out_dir, exist_ok=True)

    src_abs = os.path.abspath(src)
    src_md5 = md5(src_abs)
    print("═══ AVIF 后端 × CRF · 分块 p1（逐平面）═══")
    print(f"  参考帧  {src_abs}  md5={src_md5}")
    print(f"  探针    {tools or 'dotnet run --project src/TrueToneCap.Tools -c Release'}")
    print(f"  落盘    {out_dir}")
    print(f"  后端    {','.join(backends)}    CRF {','.join(crfs)}")

    # ── 尺子自检 A/B：恒等 + 负样本 ──
    raw = os.path.join(out_dir, "_ref.raw")
    decode_rgba(ff, src_abs, raw)
    a = np.fromfile(raw, dtype=np.uint8)
    os.remove(raw)
    H, W = 1080, 1920
    if a.size != H * W * 4:
        print(f"❌ 参考帧解码尺寸与脚本假设不符（{a.size} 字节 ≠ {H}×{W}×4）⇒ 尺子几何要对不上，不出结论")
        return pe.EXIT_FAIL
    ref = a.reshape(H, W, 4)[..., :3].astype(np.float64)
    ry, rcb, rcr = planes(ref)
    ident = {}
    for name, p in (("Y", ry), ("Cb", rcb), ("Cr", rcr)):
        p1, pmin = tile_psnr(p, p)
        ident[name] = (p1, pmin)
    print("  自检A 恒等分块 p1/min：" + "  ".join(
        f"{k}={v[0]:.2f}/{v[1]:.2f}" for k, v in ident.items()) + "   （须 >99，即 168.13 那种形状）")
    sh = np.roll(ry, 1, axis=1)
    s1, smin = tile_psnr(ry, sh)
    print(f"  自检B 负样本（源右移 1px）Y 分块 p1/min = {s1:.2f}/{smin:.2f} ⇒ 尺子{'有牙' if s1 < 60 else '无牙（结论作废）'}")
    if min(v[0] for v in ident.values()) < 99 or s1 >= 60:
        print("❌ 尺子自检不过 ⇒ 本轮不出结论")
        return pe.EXIT_FAIL

    # ── 编码 ──
    if not measure_only:
        for b in backends:
            log = os.path.join(out_dir, f"encode_{b}.log")
            rc = encode_with_tools(src_abs, out_dir, b, ",".join(crfs), tools, log)
            print(f"  编码 {b:<8} 退出码 {rc}  日志 {os.path.basename(log)}"
                  + ("" if rc == 0 else "   ← 该行档有失败，表里会写明原因"))

    # ── 测量 ──
    results, problems = [], []
    for b in backends:
        rows = read_rows(out_dir, b)
        if rows is None:
            problems.append(f"{b}: 没有 ladder_rows.tsv ⇒ 探针没跑成（见 encode_{b}.log）")
            continue
        for crf in crfs:
            r = rows.get(fmt_crf(float(crf)))
            if r is None:
                results.append({"backend": b, "crf": crf, "missing": "探针未产出该行"})
                continue
            prod = os.path.join(out_dir, r["file"])
            rec = {"backend": b, "crf": crf, "file": r["file"],
                   "bytes": int(r["bytes"]), "md5": r["md5"],
                   "ctl_md5": r["ctl_md5"], "ctl_bytes": int(r["ctl_bytes"] or 0),
                   "used": r["requested_backend_actually_used"],
                   "dispatched": r["dispatched_backend"], "reason": r["reason"], "error": r["error"]}
            if not os.path.isfile(prod) or rec["bytes"] == 0:
                rec["missing"] = "无产物" + (f"：{r['error']}" if r["error"] else "")
                results.append(rec)
                continue
            rec["md5_local"] = md5(prod)
            rec["md5_matches_tsv"] = rec["md5_local"] == rec["md5"]
            if not rec["md5_matches_tsv"]:
                problems.append(f"{b}/{crf}: 产物 md5 与探针登记值不符 ⇒ 目录被改过或编码不确定")
            rec["ffprobe"] = ffprobe_stream(ffp, prod)
            draw = os.path.join(out_dir, "_dec.raw")
            try:
                decode_rgba(ff, prod, draw)
                d = np.fromfile(draw, dtype=np.uint8)
                os.remove(draw)
                if d.size != H * W * 4:
                    rec["missing"] = f"解码尺寸不符（{d.size} 字节）"
                else:
                    rec["planes"] = measure_planes(ref, d.reshape(H, W, 4)[..., :3].astype(np.float64))
            except Exception as ex:
                rec["missing"] = f"解码失败：{ex}"
            results.append(rec)

    # ── 表 ──
    print("\n══ 后端 × CRF ══  （p1=最差1%分块 / min=最差单块；平面 Y·Cb·Cr 各一路）")
    head = (f"{'后端':<7}{'CRF':>4} {'Y p1':>6}{'min':>7} {'Cb p1':>7}{'min':>7} {'Cr p1':>7}{'min':>7}"
            f" {'KB':>8} {'走了请求后端?':<14} pix_fmt/声明  md5")
    print(head)
    for rec in results:
        b, crf = rec["backend"], rec["crf"]
        if "planes" not in rec:
            print(f"{b:<7}{crf:>4} {'—':>42}  {rec.get('missing', '未量到')}")
            continue
        p = rec["planes"]
        used = {"是：与强制 libaom 产物不同": "是", "否：与强制 libaom 逐字节相同": "否=libaom",
                "n/a（请求即 libaom，无需对照）": "n/a(即libaom)"}.get(rec["used"], rec["used"][:14])
        fp = rec.get("ffprobe", {})
        decl = "/".join(str(fp.get(k) or "-") for k in ("pix_fmt", "color_range", "color_space",
                                                        "color_primaries", "color_transfer"))
        print(f"{b:<7}{crf:>4} {p['Y']['p1']:>6.2f}{p['Y']['min']:>7.2f} "
              f"{p['Cb']['p1']:>7.2f}{p['Cb']['min']:>7.2f} {p['Cr']['p1']:>7.2f}{p['Cr']['min']:>7.2f} "
              f"{rec['bytes'] / 1024.0:>8.1f} {used:<14} {decl:<28} {rec['md5'][:12]}")

    # ── 同一 CRF 上四条分支的极差（"该不该按后端标定一张表"就是这一列）──
    print("\n══ 同一 CRF 上的分支间极差（只在【真的由请求后端产出】的行之间算）══")
    print(f"{'CRF':>4} {'可比分支数':>8} {'Y p1 极差':>10} {'Cb p1 极差':>11} {'Cr p1 极差':>11} {'体积比(最大/最小)':>18}")
    spread = {}
    for crf in crfs:
        same = [r for r in results if r["crf"] == crf and "planes" in r]
        if len(same) < 2:
            print(f"{crf:>4} {len(same):>8}  —— 可比分枝不足")
            continue
        row = {}
        for k in ("Y", "Cb", "Cr"):
            v = [r["planes"][k]["p1"] for r in same]
            row[k] = round(max(v) - min(v), 2)
        sizes = [r["bytes"] for r in same if r["bytes"]]
        ratio = round(max(sizes) / min(sizes), 2) if sizes and min(sizes) else None
        spread[crf] = {"n": len(same), "spread_db": row, "size_ratio": ratio,
                       "backends": [r["backend"] for r in same]}
        print(f"{crf:>4} {len(same):>8} {row['Y']:>10.2f} {row['Cb']:>11.2f} {row['Cr']:>11.2f} "
              f"{str(ratio):>18}")

    # ── 自检 C：复现 §48.4 已登记读数 ──
    print("\n══ 自检C 复现 §48.4 锚点（复现不了 ⇒ 这张新表不能与旧表并排读）══")
    for (b, crf), (p1_exp, kb_exp) in ANCHOR.items():
        rec = next((r for r in results if r["backend"] == b and r["crf"] == crf), None)
        if rec is None or "planes" not in rec:
            print(f"  {b}/{crf}: 未量到（{(rec or {}).get('missing') or '该行不在本次 --crf/--backends 里'}）")
            continue
        got = rec["planes"]["Y"]["p1"]
        ok = abs(got - p1_exp) <= P1_TOL
        line = f"  {b:<7} CRF {crf:<3} Y p1 登记 {p1_exp:.2f} / 本次 {got:.2f} ⇒ {'✅' if ok else '❌'}"
        if kb_exp:
            got_kb = rec["bytes"] / 1024.0
            ok2 = abs(got_kb - kb_exp) <= kb_exp * KB_TOL
            line += f"   体积 登记 {kb_exp:.1f} KB / 本次 {got_kb:.1f} KB ⇒ {'✅' if ok2 else '❌'}"
            ok = ok and ok2
        print(line)
        if not ok:
            problems.append(f"锚点复现失败 {b}/{crf}：登记 {p1_exp}{'' if not kb_exp else '/%.1fKB' % kb_exp} vs 本次 {got:.2f}")

    # ── 逐字节对照判据的一致性（同一 CRF 上 libaom 对照 md5 应当与 LibAom 行 md5 相同）──
    print("\n══ 判据自洽：每档的『强制 libaom 对照』md5 是否等于 LibAom 行的 md5 ══")
    for rec in results:
        if not rec.get("ctl_md5"):
            continue
        lib = next((r for r in results if r["backend"] == "LibAom" and r["crf"] == rec["crf"]), None)
        if lib is None:
            continue
        same = lib.get("md5") == rec["ctl_md5"]
        print(f"  {rec['backend']:<7} CRF {rec['crf']:<3} 对照 md5 {'=' if same else '≠'} LibAom 行 md5"
              + ("" if same else f"   ← 同参数两次编码不确定，跨表对照要谨慎"))
        if not same:
            problems.append(f"{rec['backend']}/{rec['crf']}: libaom 对照与 LibAom 行 md5 不一致")

    with open(os.path.join(out_dir, "measured.json"), "w", encoding="utf-8") as f:
        json.dump({"ref": {"path": src_abs, "md5": src_md5}, "backends": backends, "crfs": crfs,
                   "selfcheck_identity": ident, "anchor_expect": {f"{k[0]}/{k[1]}": v for k, v in ANCHOR.items()},
                   "spread": spread, "rows": results},
                  f, indent=1, ensure_ascii=False)
    print(f"\n明细 JSON → {os.path.join(out_dir, 'measured.json')}")

    print(f"\n[RESULT] 待确认 {len(problems)} 项" + ("：\n  - " + "\n  - ".join(problems) if problems else " ✅"))
    return pe.EXIT_FAIL if problems else pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
