"""D50↔D65 适配矩阵的取证脚本（缺陷 C 的数值来源，不是凭记忆写的）

用途：`ColorProfileProvider.BuildValidSRgbIcc()` 的三个原色列、以及 `BakeIccToTarget`
里那张 `d50ToD65Pcs` 矩阵，全部由本脚本从【两外部参照】推出来：
  ① 微软随系统的 sRGB profile（本机 Windows 色彩目录，第三方权威）
  ② IEC 61966-2-1 那张 D65 引用的 sRGB 矩阵（仓库旧实现用的同一组值）

跑法：  py scripts/probes/d50_adaptation_derive.py
依赖：  numpy（Pillow 环境下已带）

三条自检（任何一条不成立就说明矩阵方向或数值错了，必须停下来重测）：
  · A @ D65白  ≈ (0.9642, 1.0, 0.8249)      —— 适配必须把媒体白点映到 PCS 白点
  · Ainv @ D50白 ≈ (0.9505, 1.0, 1.0890)    —— 逆适配必须映回去
  · max|Ainv @ D50列 − D65列| < 1e-3         —— 两组实测值必须互洽
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors

import os
import struct
import sys

try:
    import numpy as np
except ImportError:
    print("需要 numpy（Pillow 环境自带）：pip install numpy", file=sys.stderr)
    sys.exit(2)

# IEC 61966-2-1 的 sRGB RGB→XYZ，**D65 引用**（行主序；每列是一个原色）
D65_COLS = np.array([
    [0.4124, 0.3576, 0.1805],
    [0.2126, 0.7152, 0.0722],
    [0.0193, 0.1192, 0.9505],
])

W65 = np.array([0.95047, 1.0, 1.08883])
W50 = np.array([0.9642, 1.0, 0.8249])


def read_icc_xyz(path):
    """独立实现的 ICC 标签读取（不走仓库产品代码，避免自证）。"""
    d = open(path, "rb").read()
    n = struct.unpack(">I", d[128:132])[0]
    tags = {}
    for i in range(n):
        off = 132 + i * 12
        sig = d[off:off + 4].decode("latin1")
        o = struct.unpack(">I", d[off + 4:off + 8])[0]
        tags[sig] = o

    def one(sig):
        o = tags[sig]
        assert d[o:o + 4].decode("latin1") == "XYZ ", f"{sig} 不是 XYZ 类型"
        x, y, z = struct.unpack(">iii", d[o + 8:o + 20])
        return np.array([x / 65536.0, y / 65536.0, z / 65536.0])

    return {s: one(s) for s in ("wtpt", "rXYZ", "gXYZ", "bXYZ") if s in tags}, len(d), n


def find_system_srgb():
    base = "C:/Windows/System32/spool/drivers/color"
    if not os.path.isdir(base):
        return None
    for f in sorted(os.listdir(base)):
        if "srgb" in f.lower() and f.lower().endswith((".icm", ".icc")):
            return os.path.join(base, f)
    return None


def main():
    path = find_system_srgb()
    if path is None:
        print("❌ 找不到系统 sRGB profile（Windows 色彩目录）⇒ 无法取证，退出", file=sys.stderr)
        return 1

    tags, size, ntags = read_icc_xyz(path)
    print(f"参照 profile: {path}\n  bytes={size} tags={ntags}")
    for sig in ("wtpt", "rXYZ", "gXYZ", "bXYZ"):
        v = tags[sig]
        print(f"  {sig} = ({v[0]:.4f}, {v[1]:.4f}, {v[2]:.4f})")

    # 行主序、与 D65_COLS 同构：行 0 = (rX,gX,bX)、行 1 = (rY,gY,bY)…
    # ⚠ 这里多一个 .T 就会把"列=原色"当成"行=原色"，A 会算成另一张矩阵，
    #   而下面第 3 条自检因为是构造恒等照样通过 —— 真正能判方向的是第 1、2 条白点检查。
    d50 = np.column_stack([tags["rXYZ"], tags["gXYZ"], tags["bXYZ"]])
    colsum = d50.sum(axis=1)
    print(f"  三列之和 = ({colsum[0]:.4f}, {colsum[1]:.4f}, {colsum[2]:.4f})  ← 应≈D50 白")

    A = d50 @ np.linalg.inv(D65_COLS)      # D65 → D50 PCS
    Ainv = np.linalg.inv(A)                # D50 PCS → D65 引用

    print("\nA (D65→D50) =")
    for r in A:
        print("   %+.7f, %+.7f, %+.7f" % tuple(r))
    print("Ainv (D50→D65) =   ← 产品代码 BakeIccToTarget 用的就是这一张")
    for r in Ainv:
        print("   %+.7f, %+.7f, %+.7f" % tuple(r))

    checks = [
        ("A @ D65白 = D50白", np.abs(A @ W65 - W50).max(), 0.002),
        ("Ainv @ D50白 = D65白", np.abs(Ainv @ W50 - W65).max(), 0.002),
        ("Ainv @ D50列 = D65列", np.abs(Ainv @ d50 - D65_COLS).max(), 1e-3),
    ]
    bad = 0
    print("\n自检：")
    for name, err, tol in checks:
        ok = err <= tol
        bad += 0 if ok else 1
        print(f"  {'✅' if ok else '❌'} {name}  max|Δ|={err:.2e} (容差 {tol:g})")

    print("\n结论：", "矩阵方向与数值互洽，可用于产品代码" if not bad
          else "有不符 ⇒ 别把这张矩阵写进产品代码，先查参照")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
