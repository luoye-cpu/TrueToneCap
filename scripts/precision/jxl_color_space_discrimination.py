#!/usr/bin/env python3
"""JXL 色彩声明的可证伪判据：cjxl 的 `-x color_space=` 到底生效没有。

为什么是"判别"而不是"读回"：本机没有任何 JXL 解码器（cjxl 是静态链，无 djxl；
Pillow / imagecodecs / jxl-python 都开不了 .jxl；ffprobe 对 5 种不同 color_space 的产物
读出完全相同的 `jpegxl,rgba,iec61966-2-1,bt2020` ⇒ 它没读码流里的 ColorEncoding）。
JXL 容器里也【没有 colr 盒】（顶层只有 JXL / ftyp / jxlc），色彩只在 jxlc 码流内。
所以这里用唯一可信的外部事实：**同一个无元数据输入，不同的色彩参数必须产出不同的字节**；
参数被忽略时字节必然相同 ⇒ 这个判据能证伪"没生效"，代价是读不出具体 primaries/TF 号。

三条判据（任何一条不成立就退 1，不许"跑通就算过"）：
  A. 两两不同的取值 ⇒ 字节必须两两不同。相同 = 该值被 cjxl 忽略 ⇒ 产品路径不能依赖它。
  B. 故意编的假值必须【失败且不出图】⇒ 拼错的 color_space 不可能静默出货（fail-closed）。
     这条同时是本判据自己的对照组：没有它，A 的"不同"可能来自别的原因。
     （曾经把它写成"假值应与不传参同值"，那来自一次被误读的实测 —— 见下面的元数据陷阱。）
  C. 隐含前提：中转输入必须【无色彩元数据】。`-x` 是解码提示，输入自带元数据时会被完全忽略 ——
     实测：同一张带 iCCP 的 PNG，8 种取值（含假值）产出 md5 全同。本判据用 P6 PPM 输入，
     所以 A 的"两两不同"同时证明了"提示生效的条件"与"参数真的落进码流"。

用法：py -3 jxl_color_space_discrimination.py [<cjxl.exe>]
     不带路径时用 precision_env.tool_candidates('cjxl') 探测（候选表以仓库内产品自带那份为先），
     也可用 TTC_CJXL 显式指定 —— 与 ffprobe / avifenc 同一套优先级约定。
退出码：0 判据通过 / 1 判据不成立（回归）/ 3 找不到 cjxl ⇒ SKIP，是"这轮没判"，不是通过
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import hashlib
import os
import subprocess
import sys

import precision_env as pe

# 产品路径实际用到的三个值（NativeJxlEncoder.JxlColorSpaceValue 的输出集）
PRODUCT_VALUES = ["sRGB", "DisplayP3", "RGB_D65_202_Rel_SRG"]
BOGUS = "NOTAVAILVALUE_TTC_PROBE"


def sh(data):
    return hashlib.md5(data).hexdigest()[:12]


def make_ppm(path, w=32, h=32):
    """P6 PPM：只有像素，无任何色彩元数据 ⇒ -x 提示在这里才有机会生效。"""
    with open(path, "wb") as f:
        f.write(b"P6\n%d %d\n255\n" % (w, h))
        for y in range(h):
            for x in range(w):
                f.write(bytes(((x * 8) & 255, (y * 8) & 255, ((x + y) * 4) & 255)))


def cjxl(exe, inp, out, extra):
    # 先删旧产物：判据 A 读的是【文件字节】，上一轮留下的同名文件会让"什么都没编码成功"
    # 也读出"三枚两两不同 ⇒ 通过"。实测（2026-09-23）把 cjxl 指成 whoami.exe 后，
    # 不删旧文件的版本照样退 0 —— 一条判据就这样在完全没干活的情况下报了绿灯。
    if os.path.exists(out):
        os.remove(out)
    cmd = [exe, "-q", "90", "--container=1"] + extra + [inp, out]
    r = subprocess.run(cmd, capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr).strip().splitlines()[-1:]


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else None
    if exe:
        # 显式指定就只认它：指到不存在的路径 ⇒ SKIP（不是判红，但绝不是通过）
        cands = [exe] if os.path.isfile(exe) else []
    else:
        cands = pe.tool_candidates("cjxl")
    if not cands:
        print("⏭ 跳过：找不到 cjxl（显式指定的路径不存在，或候选表与 PATH 都没命中）")
        print("   找过：环境变量 TTC_CJXL%s、仓库内 src\\TrueToneCap.Core\\Resources\\cjxl.exe、PATH"
              % ("（指向 '%s'，不存在）" % exe if exe else ""))
        print("   也可用第一个命令行参数显式指定。")
        print("   原因：本判据【只信外部编码器 cjxl】，没有它就没有判据 ⇒ 不能当通过。")
        return pe.EXIT_SKIP
    exe = cands[0]
    print("  ℹ️  cjxl: %s" % exe)
    # 产物一律落在 gitignore 的 artifacts/ 下（与同目录其它判据脚本同约定，见 .gitignore:181）。
    # 早先写在这里 → scripts/precision/jxlcolr/ 会脏工作区，而 Publish.ps1 的 [-1/6] 是硬拦脏工作区的。
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(os.path.dirname(here))
    work = os.path.join(repo, "artifacts", "precision", "jxlcolr")
    os.makedirs(work, exist_ok=True)
    for stale in os.listdir(work):
        if stale.endswith(".jxl") or stale.endswith(".ppm"):
            os.remove(os.path.join(work, stale))
    ppm = os.path.join(work, "probe.ppm")
    make_ppm(ppm)

    hashes, codes = {}, {}
    for v in PRODUCT_VALUES:
        p = os.path.join(work, f"v_{v}.jxl")
        rc, tail = cjxl(exe, ppm, p, ["-x", f"color_space={v}"])
        codes[v] = rc
        hashes[v] = sh(open(p, "rb").read()) if os.path.exists(p) else None
    p = os.path.join(work, f"v_{BOGUS}.jxl")
    rc, _ = cjxl(exe, ppm, p, ["-x", f"color_space={BOGUS}"])
    codes["bogus"] = rc
    hashes["bogus"] = sh(open(p, "rb").read()) if os.path.exists(p) else None
    p = os.path.join(work, "v_none.jxl")
    rc, _ = cjxl(exe, ppm, p, [])
    codes["none"] = rc
    hashes["none"] = sh(open(p, "rb").read()) if os.path.exists(p) else None

    for k in hashes:
        print(f"  {k:22s} exit={codes[k]}  md5={hashes[k]}")

    bad = []
    # A
    seen = {}
    for v in PRODUCT_VALUES:
        if hashes[v] is None:
            bad.append(f"{v} 没产出文件")
            continue
        if hashes[v] in seen:
            bad.append(f"{v} 与 {seen[hashes[v]]} 字节相同 ⇒ 该取值被 cjxl 忽略，产品路径不能依赖")
        seen[hashes[v]] = v
    # B：假值必须【失败且不出图】⇒ 拼错的枚举不可能静默通过（fail-closed）。
    # ⚠ 这一条的方向纠正过一次：在"输入自带色彩元数据"的场景里，cjxl 压根不去解析 -x 的值，
    #   所以那时假值也退出码 0、也出图（8 种取值 md5 全同）——那不能读成"未知值被静默忽略"，
    #   只能读成"这个参数在本输入上根本没被看过"。只有无元数据输入才暴露真实行为：报错。
    bogus_failed = codes["bogus"] != 0 or hashes["bogus"] is None
    if not bogus_failed:
        bad.append(f"假值竟然成功了（exit={codes['bogus']}, md5={hashes['bogus']}）⇒ "
                   "拼错的枚举会静默通过，产品路径不能依赖 color_space")
    else:
        print(f"  ℹ️  假值：exit={codes['bogus']}，产物={'有' if hashes['bogus'] else '无'} "
              f"⇒ cjxl 对无法解析的 color_space 是 fail-closed（拼错不会偷偷出货）")
    if hashes["bogus"] == hashes["none"] and hashes["bogus"] is not None:
        bad.append("假值与不传参同值 ⇒ 提示被忽略，检查输入是否带了色彩元数据")
    if len(bad):
        print("\n❌ 判据未通过：")
        for b in bad:
            print("   -", b)
        return 1
    print(f"\n✅ 判据通过：{len(PRODUCT_VALUES)} 个取值两两字节不同，且假值不出图（fail-closed）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
