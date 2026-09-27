"""同一内容跨格式/跨后端：容器的色彩声明必须互相一致（§29 代理指标④）。

    样张宿主: TrueToneCap.Tools.exe --cicp-sweep    （同一张图 × 4 色域 tag × 7 格式/后端）
    本脚本:   py -3 scripts/precision/cicp_consistency.py [--samples <dir>]

## 为什么这不是 param_sweep_check 的重复
`param_sweep_check.py` 判的是"**每一行**的声明等不等于它自己被告知的值"。
这条判的是"**行与行之间是否自相矛盾**"。两者不等价，而且漏掉的那一半正好是会出事故的那一半：

  每一行各自都"达标"，但 PNG 写 cICP=12、AVIF 写 cICP=12+ICC、WebP 写 primaries=1 且不带 ICC
  ⇒ 同一张图在三个查看器里有三种解释。逐行看谁都没错，合起来就是色偏。
  跨行一致性能抓，逐行达标抓不到。

## 判据（按 tag 分组，组内每个文件归一成一个三元组：primaries 码 / transfer 码 / 是否带 ICC）
  A) 组内所有显式写了 primaries 的文件，码值必须彼此相同，且等于该 tag 的期望值
     （期望表直接复用 `param_sweep_check.TAG_PRIM`，不另写一份事实源）。
  B) 任何文件都不许声明成【别的】色域：写了码又不等于该 tag 的期望 ⇒ 红。
     AdobeRGB 自 2026-09-27 起作为【目标】收口进 Rec.2020（CollapseSelectableTag 与样张宿主同源）
     ⇒ 期望与 bt2020 同为码点 9。旧口径"不许写码点、只带 ICC"给七个容器各留一条 Adobe 出路，
     任何一侧单独改映射都会判成"跨容器不一致"——那正是这次要消掉的东西。
     而像素其实被矩阵搬到了 Adobe ⇒ 这一条必须红。
  C) 既没写 primaries、又不带 ICC、tag 还不是 srgb ⇒ 红（该图【没有任何色彩声明】，查看器只能猜 sRGB）。
  D) 组内 transfer 码必须一致（允许"未声明"，但不许出现 13 与 16 混在一组 —— 那说明 SDR/HDR 分派走岔了）。
  E) 判定规模不足 ⇒ SKIP：每个 tag 组至少要 2 个格式可比、且至少 2 个组有可比文件。
     只有一个文件时谈不上"一致"，绝不冒领通过。
  硬件通路（名字含 avifnv）缺产物 ⇒ 该行 SKIP；软件通路缺产物 ⇒ FAIL（生成器没跑出来就是回归）。

## 退出码约定见 precision_env.py：0 通过 / 1 不一致 / 3 SKIP
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import os
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
# 复用同一套"外部读回"解析器与期望表：判据脚本之间重复一份事实源，就会各自漂移
from param_sweep_check import TAG_PRIM, avif_has_icc, avif_nclx, png_scan, probe  # noqa: E402

# H.273 原色码 → ffprobe 用的名字（ffprobe 读回的是名字，PNG cICP / AVIF nclx 读回的是码，必须归一）
PRIM_NAME = {1: "bt709", 9: "bt2020", 11: "smpte437", 12: "smpte432"}
NAME_TO_PRIM = {v: int(k) for k, v in PRIM_NAME.items()}
NAME_TO_PRIM["unknown"] = None
# ⚠ 码 2 = "unspecified"（ISO/IEC 23091-2），它的含义是"我不声明"，不是"我声明成某个色域"。
# 首跑把 avifenc 为 AdobeRGB 写的 nclx(2,2) 判成"撒谎"，那是判据的错：跳过 --cicp 时 avifenc
# 仍会落一个 unspecified 的 nclx 占位，诚实且无害。⇒ 归一时一律当作"没写"，让规则 C 去管
# "没写码又没 ICC"这种真正危险的情形。
UNSPECIFIED = 2

# 明确记录过"该格式承载不了此色域的声明 ⇒ 允许只写码不带 ICC"的格式（值 = 允许声明 sRGB 码点 1 的格式名）。
# ⚠ 2026-09-27 清空：原先这里放着 {"jxl"}，是 JXL + AdobeRGB 退回 sRGB 那条例外的口子，
#   而那条降级连同它的理由（"libjxl 把承载不了的 ICC 归到最近枚举并丢弃 profile"）一起被实测推翻，
#   用户随后拍定 **AdobeRGB 一律收口进 Rec.2020**（交接 §56.4 / §59）⇒ JXL 的 adobe 样张现在
#   必须和别的广色域目标一样声明 primaries=9，不再允许声明成 1。
#   留着这条口子就会把"有人把 Adobe 改回退 sRGB"判成合法 —— 白名单清空本身就是判据的一部分。
#   旧的两条机制说明也留在这儿免得有人再抄回去：
#     ① "文件里没有 acsp 魔数"不能当"没嵌 ICC"的判据 —— JXL 的 ICC 走 delta 压缩（enc_icc_codec.cc），
#        实测内嵌 820 字节校准 ICC 的产物 grep acsp 也是 0；要判嵌没嵌读 jxlinfo 的那行 "N-byte ICC profile"。
#     ② ffprobe 报 primaries=bt2020 是【读取方】把自定义原色映射到最近的 H.273 码点，
#        不代表文件自己的声明被改成 bt2020。
DOWNGRADE_OK = frozenset()

FORMATS = ("png", "tiff", "jxl", "jpegli", "webp", "avifsw", "avifnv")
TAGS = ("srgb", "p3", "bt2020", "adobe")
MIN_FMT_PER_TAG = 2      # 组内可比格式数下限
MIN_TAG_GROUPS = 2       # 有可比文件的组数下限

# fmt → 扩展名。⚠ 不能默认"扩展名 == fmt"：png/tiff/jxl/webp 恰好同名，会让人以为这条表是冗余的，
# 而 avif 的两个通路（avifsw / avifnv）扩展名都是 .avif —— 首跑就是因为默认分支拼出 .avifsw
# 而把 8 张存在的 AVIF 样张报成"缺产物"。新增格式时必须在这里显式登记。
EXT_OF_FMT = {"png": "png", "tiff": "tiff", "jxl": "jxl", "jpegli": "jpg",
              "webp": "webp", "avifsw": "avif", "avifnv": "avif"}


def _nz(code):
    """H.273 码 2（unspecified）归一成 None = "这个容器没做声明"。"""
    return None if code == UNSPECIFIED else code


def read_declaration(path: str, ffprobe: str) -> dict:
    """把一个容器里的色彩声明归一成 {prim: 码|None, transfer: 码|None, icc: bool, src: 读了什么}。"""
    ext = os.path.splitext(path)[1].lower()
    decl = {"prim": None, "transfer": None, "icc": False, "src": ""}

    if ext == ".png":
        _depth, cicp, _sbit = png_scan(path)
        if cicp:
            decl["prim"], decl["transfer"] = _nz(cicp[0]), _nz(cicp[1])
        blob = open(path, "rb").read()
        decl["icc"] = b"iCCP" in blob
        decl["src"] = "PNG cICP/iCCP"
        return decl

    if ext == ".avif":
        nclx = avif_nclx(path)
        if nclx:
            decl["prim"], decl["transfer"] = _nz(nclx[0]), _nz(nclx[1])
        decl["icc"] = avif_has_icc(path)
        decl["src"] = "AVIF colr(nclx/prof)"
        return decl

    # 其余格式一律交给 ffprobe（它按容器各自的元数据映射成 H.273 名字）
    s = probe(path, ffprobe) or {}
    p = s.get("color_primaries")
    t = s.get("color_transfer")
    decl["prim"] = NAME_TO_PRIM.get(p) if p and p != "unknown" else None
    decl["transfer"] = {"bt709": 1, "smpte432": 12, "iec61966-2-1": 13, "smpte2084": 16,
                        "arib-std-b67": 18}.get(t) if t and t != "unknown" else None
    # ICC 是否随容器携带：一律找 ICC 魔数 'acsp'（位于 profile 头偏移 36，未压缩内嵌时必然可见）。
    # ⚠ 别再用子串 "ICC" 或 ffprobe 的 side_data 判：TIFF 的标签名是 InterColorProfile（不含 "ICC"），
    #   而 ffprobe 的 TIFF 解码器不报 ICC —— 首跑这两处一起漏检，把三份【确实带着 ICC】的 TIFF
    #   判成"零声明"（伪报）。已用 Pillow 独立复核：img.info['icc_profile'] 有值、
    #   标签 34675(小端) 与 acsp 都在文件里。PNG 走上面的 iCCP 分支（profile 被 deflate 压过，
    #   看不到 acsp），不在这个分支里。
    decl["icc"] = b"acsp" in open(path, "rb").read()
    decl["src"] = "ffprobe " + str(s.get("pix_fmt", "?"))
    return decl


def describe(d: dict) -> str:
    p = d["prim"]
    prim = "无" if p is None else "%d(%s)" % (p, PRIM_NAME.get(p, "?"))
    tr = "无" if d["transfer"] is None else str(d["transfer"])
    return "primaries=%-13s transfer=%-4s ICC=%s   读自 %s" % (prim, tr, "有" if d["icc"] else "无", d["src"])


def main() -> int:
    ffprobe = find_tool("ffprobe")
    if not ffprobe:
        print("⏭ 跳过：找不到 ffprobe（声明读不回来就没有判据）")
        return EXIT_SKIP

    sdir = samples_dir("cicp", opt("--samples"))
    if not sdir:
        return EXIT_SKIP

    print("═══ 同一内容跨格式/跨后端 · 色彩声明一致性 ═══")
    print(f"  样张: {sdir}\n")

    groups: dict[str, list[dict]] = {}
    missing_soft: list[str] = []
    skipped_hw: list[str] = []
    for tag in TAGS:
        entries = []
        for fmt in FORMATS:
            # 扩展名一律查表，查不到就是"新格式没登记" ⇒ 当场说清楚，不许退化成"缺产物"
            ext = EXT_OF_FMT.get(fmt)
            if ext is None:
                print(f"❌ 判据内部错误：格式 {fmt} 没在 EXT_OF_FMT 里登记扩展名")
                return EXIT_FAIL
            path = os.path.join(sdir, "cc_%s_%s.%s" % (fmt, tag, ext))
            if not os.path.isfile(path):
                if fmt == "avifnv":
                    skipped_hw.append("cc_%s_%s" % (fmt, tag))
                else:
                    missing_soft.append("cc_%s_%s" % (fmt, tag))
                continue
            d = read_declaration(path, ffprobe)
            d["fmt"], d["tag"] = fmt, tag
            entries.append(d)
        if entries:
            groups[tag] = entries

    for s in skipped_hw:
        print(f"  ⏭ {s}（硬件通路缺产物，不计失败）")
    if missing_soft:
        # 生成器没产出软件通路的样张 ⇒ 判据无从执行，这是回归不是 SKIP
        print(f"  ❌ 缺少 {len(missing_soft)} 张软件通路样张（先跑 TrueToneCap.Tools.exe --cicp-sweep）：")
        for s in missing_soft[:6]:
            print(f"     · {s}")
        return EXIT_FAIL

    comparable = {t: g for t, g in groups.items() if len(g) >= MIN_FMT_PER_TAG}
    if len(comparable) < MIN_TAG_GROUPS:
        print(f"\n⏭ 可比组数 {len(comparable)} < {MIN_TAG_GROUPS}"
              f"（每组需 ≥{MIN_FMT_PER_TAG} 个格式）⇒ 谈不上『一致』，不算通过")
        return EXIT_SKIP

    fails: list[str] = []
    for tag in TAGS:
        g = groups.get(tag)
        if not g:
            continue
        print(f"  ── tag={tag}（{len(g)} 个容器）── 期望 primaries="
              f"{TAG_PRIM.get(tag) or '无码（只带 ICC）'}")
        for e in g:
            print(f"     {e['fmt']:<8} {describe(e)}")

        want = TAG_PRIM.get(tag)
        want_code = None if want is None else int(want)
        explicit = [e for e in g if e["prim"] is not None]

        # A) 组内显式码必须彼此相同
        if len({e["prim"] for e in explicit}) > 1:
            fails.append(f"A 条 {tag}: 组内 primaries 不一致 ⇒ "
                         + " | ".join("%s=%s" % (e["fmt"], e["prim"]) for e in explicit))
        # B) 显式码必须与该文件自己的其它声明、以及该 tag 的可表达性自洽。
        #    分两种情形（2026-09-19 按实测重写，原先一律要求"等于 tag 的期望码"是错的）：
        #    · tag 有 H.273 码点（srgb/p3/bt2020）⇒ 码必须等于期望值；
        #    · tag 无码点（AdobeRGB）⇒ "写了码 + 又带 ICC"就是两份互相矛盾的声明（采信码点的查看器直接错色）；
        #      只写码不带 ICC 时，唯一合法的情形是我们【明确记录过】的格式级降级（见 DOWNGRADE_OK）；
        #      其余任何码点都是谎报（实测 libjxl 就把 Adobe ICC 归成了 bt2020 并丢掉 profile）。
        for e in explicit:
            if want_code is None:
                if e["icc"]:
                    fails.append(f"B 条 {tag}: {e['fmt']} 同时写了 primaries={e['prim']} 又内嵌 ICC ⇒ "
                                 "两份互相矛盾的声明（该色域在 H.273 无码点，码点无论取什么都与 ICC 不符）")
                elif not (e["fmt"] in DOWNGRADE_OK and e["prim"] == 1):
                    fails.append(f"B 条 {tag}: {e['fmt']} 声明 primaries={e['prim']}({PRIM_NAME.get(e['prim'], '?')})，"
                                 f"但 {tag} 无 H.273 码点、又不带 ICC ⇒ 该文件在谎报色域"
                                 f"（合法的降级只允许 {DOWNGRADE_OK} 声明 sRGB=1）")
            elif e["prim"] != want_code:
                fails.append(f"B 条 {tag}: {e['fmt']} 声明 prim={e['prim']}"
                             f"({PRIM_NAME.get(e['prim'], '?')})，应为 {want_code}"
                             f"({PRIM_NAME.get(want_code, '?')})")
        # C) 无码又无 ICC（且不是 srgb）⇒ 没有色彩声明
        for e in g:
            if e["prim"] is None and not e["icc"] and tag != "srgb":
                fails.append(f"C 条 {tag}: {e['fmt']} 既不写 primaries 也不带 ICC ⇒ "
                             "该图无任何色彩声明，查看器只能按 sRGB 猜（P3/BT.2020 像素会被显示成灰淡）")
        # D) transfer 组内一致
        tr = {e["transfer"] for e in g if e["transfer"] is not None}
        if len(tr) > 1:
            fails.append(f"D 条 {tag}: transfer 混用 {sorted(tr)} ⇒ SDR/HDR 分派在各容器间不一致")

    # 跨 tag 的兜底 sanity：不同 tag 的显式码不许全都一样（那说明 tag 根本没传到容器）
    per_tag_code = {t: next((e["prim"] for e in g if e["prim"] is not None), None)
                    for t, g in groups.items()}
    codes = {t: c for t, c in per_tag_code.items() if c is not None}
    if len(codes) >= 2 and len(set(codes.values())) == 1:
        fails.append("跨 tag 条: " + " | ".join("%s→%s" % (t, c) for t, c in codes.items())
                     + " 全等于同一个 primaries 码 ⇒ 色域 tag 根本没进容器")

    print(f"\n判定 {sum(len(g) for g in comparable.values())} 个文件 / {len(comparable)} 组"
          f"（硬件跳过 {len(skipped_hw)}），不符 {len(fails)} 项")
    for f in fails:
        print(f"  ❌ {f}")
    if fails:
        print("\n结论：❌ 同一张图在不同容器里被声明成了不同的样子 —— 查看器间必然出现色偏分歧")
        return EXIT_FAIL
    print("\n结论：✅ 同一内容跨格式/跨后端的色彩声明互相一致（primaries/ICC/transfer 三条都对得上）")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
