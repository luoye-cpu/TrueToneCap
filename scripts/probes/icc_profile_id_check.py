"""ICC.1 §11.6 ProfileID 算法标定 —— 用本机真实 profile 反证，不靠记忆写代码。

背景：合规审计要求 PatchSrgbPrimaries 不再 clone 系统 sRGB icm（那会随用户产物
分发 HP 的 cprt/creator）。改写 cprt/creator 后 profile 内容变了，ICC.1 要求
ProfileID 跟着重算；但 ID 的字节构成我不能凭记忆写死 —— 于是拿本机若干**已带
非零 ProfileID 的真实 profile** 去撞候选配方：撞得中的那个才算被证实。

判据方向很重要：这里不是"我算出来一个值"就算完，而是"我算出来的值必须等于
上游作者写在 @84 的那个值"。只要有一份真实 profile 命中，配方就不是构造恒等。

用法:  py scripts/probes/icc_profile_id_check.py [--verbose]
退出码: 0 = 至少一份真实 profile 命中某配方；1 = 一份都没命中（配方未证实）。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# 搜索范围：仓库夹具 + Windows 系统色彩目录 + 发布包里的 icc/icm
SEARCH_GLOBS: list[tuple[Path, str]] = [
    (REPO, "tests/fixtures/*.icc"),
    (REPO, "src/**/*.icc"),
    (REPO, "src/**/*.icm"),
    (REPO, "artifacts/**/*.icc"),
    (Path("C:/Windows/System32/spool/drivers/color"), "*.icm"),
    (Path("C:/Windows/System32/spool/drivers/color"), "*.icc"),
    (Path("C:/Windows/System32/Microsoft/Windows/Color"), "*.icc"),
]

XYZ_TAG = {b"wtpt", b"rXYZ", b"gXYZ", b"bXYZ"}


def parse(path: Path) -> dict | None:
    data = path.read_bytes()
    if len(data) < 132 or data[36:40] != b"acsp":
        return None
    n_tags = struct.unpack(">I", data[128:132])[0]
    if n_tags == 0 or 132 + 12 * n_tags > len(data):
        return None
    tags = {}
    for i in range(n_tags):
        e = 132 + 12 * i
        sig = data[e : e + 4]
        off, size = struct.unpack(">II", data[e + 4 : e + 12])
        tags[sig] = (off, size)
    return {
        "path": path,
        "data": data,
        "version": struct.unpack(">I", data[8:12])[0],
        "color_space": data[16:20],
        "pcs": data[20:24],
        "stored_id": data[84:100],
        "tags": tags,
    }


def xyz12(prof: dict, sig: bytes) -> bytes | None:
    """XYZType 的 12 字节数值区（跳过 sig(4)+reserved(4)）。"""
    t = prof["tags"].get(sig)
    if not t:
        return None
    off, size = t
    if size < 20:
        return None
    return prof["data"][off + 8 : off + 20]


def xyz20(prof: dict, sig: bytes) -> bytes | None:
    """整个 XYZType 数据区（含 sig+reserved）。"""
    t = prof["tags"].get(sig)
    if not t:
        return None
    off, size = t
    if size < 20:
        return None
    return prof["data"][off : off + 20]


def hdr(prof: dict, off: int, size: int) -> bytes:
    return prof["data"][off : off + size]


def build(recipes: str, prof: dict) -> bytes | None:
    """按配方拼接待哈希字节流；任一成员缺失则返回 None。"""
    const = {
        "c_55aa0204": bytes([0x55, 0xAA, 0x02, 0x04]),
        "c_055aa204": bytes([0x05, 0x5A, 0xA2, 0x04]),
    }[recipes["const"]]
    parts = [const]
    order = recipes["order"]  # 分组顺序
    for group in order:
        if group == "wtpt":
            f = xyz12 if recipes["xyz"] == "12" else xyz20
            v = f(prof, b"wtpt")
            if v is None:
                return None
            parts.append(v)
        elif group in ("rXYZ", "gXYZ", "bXYZ"):
            f = xyz12 if recipes["xyz"] == "12" else xyz20
            v = f(prof, group.encode())
            if v is None:
                return None
            parts.append(v)
        elif group == "flags":
            parts.append(hdr(prof, 44, 4))
        elif group == "mfr":
            parts.append(hdr(prof, 48, 4))
        elif group == "model":
            parts.append(hdr(prof, 52, 4))
        elif group == "attrs":
            parts.append(hdr(prof, 56, 8))
        elif group == "intent2":
            parts.append(hdr(prof, 64, 2))
        elif group == "intentpcs2":
            parts.append(hdr(prof, 66, 2))
        elif group == "intent4":
            parts.append(hdr(prof, 64, 4))
        elif group == "creator":
            parts.append(hdr(prof, 80, 4))
    return b"".join(parts)


# 候选配方：覆盖规范文本的两种读法（0x55AA0204 / 0x055AA204）、
# XYZ 取 12 还是 20 字节、头部字段与 XYZ 的先后、intent 取 2+2 还是 4。
HEADER_BLOCK_STD = ["flags", "mfr", "model", "attrs", "intent2", "intentpcs2"]
RECIPES: dict[str, dict] = {}


def _reg(name, const, xyz, order):
    RECIPES[name] = {"const": const, "xyz": xyz, "order": order}


for _const in ("c_55aa0204", "c_055aa204"):
    for _xyz in ("12", "20"):
        _reg(
            f"spec_{_xyz}_{_const}",
            _const,
            _xyz,
            ["wtpt"] + HEADER_BLOCK_STD + ["rXYZ", "gXYZ", "bXYZ"],
        )
        _reg(
            f"xyzfirst_{_xyz}_{_const}",
            _const,
            _xyz,
            ["wtpt", "rXYZ", "gXYZ", "bXYZ"] + HEADER_BLOCK_STD,
        )
        _reg(
            f"intent4_{_xyz}_{_const}",
            _const,
            _xyz,
            ["wtpt"] + ["flags", "mfr", "model", "attrs", "intent4"] + ["rXYZ", "gXYZ", "bXYZ"],
        )
        _reg(
            f"nocreator_wtpt_last_{_xyz}_{_const}",
            _const,
            _xyz,
            HEADER_BLOCK_STD + ["rXYZ", "gXYZ", "bXYZ", "wtpt"],
        )


def whole(prof: dict, zero_ranges: list[tuple[int, int]]) -> bytes:
    """整份 profile 参与哈希，指定区段先清零（lcms cmsMD5computeID 的读法）。"""
    buf = bytearray(prof["data"])
    for a, b in zero_ranges:
        buf[a:b] = bytes(b - a)
    return bytes(buf)


# 配方 B 族：整份序列化数据（可清零某些头字段）参与哈希。
# 依据：lcms2/src/cmsmd5.c::cmsMD5computeID 的注释 —— "In the header, rendering
# intent, flags and ID should be set to zero before computing MD32 checksum
# (per 7.2.16 of ICC spec 4.4)"，即把 flags@44、renderingIntent@64、ProfileID@84
# 清零后对**整份 profile** 做 MD5。这份源码是 2026-09-20 从 GitHub 取的（master）。
WHOLE_RECIPES: dict[str, list[tuple[int, int]]] = {
    "whole_nothing_zeroed": [],
    "whole_id_zeroed": [(84, 100)],
    "whole_lcms_flags_intent_id": [(44, 48), (64, 68), (84, 100)],
    "whole_lcms_plus_created": [(24, 36), (44, 48), (64, 68), (84, 100)],
    "whole_flags_intent_id_creator": [(44, 48), (64, 68), (80, 84), (84, 100)],
}


def collect() -> list[Path]:
    seen: set[Path] = set()
    out: list[Path] = []
    for root, pattern in SEARCH_GLOBS:
        if not root.is_dir():
            continue
        for p in sorted(root.glob(pattern)):
            if p.is_file() and p not in seen:
                seen.add(p)
                out.append(p)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    files = collect()
    profiles = []
    for p in files:
        try:
            prof = parse(p)
        except Exception:
            prof = None
        if prof:
            profiles.append(prof)

    non_zero = [pr for pr in profiles if any(pr["stored_id"])]
    print(f"候选 profile 文件: {len(files)}  可解析: {len(profiles)}  带非零 ProfileID: {len(non_zero)}")
    for pr in profiles:
        print(
            f"  {pr['path'].name:<48} v{pr['version'] >> 16:,}.{pr['version'] & 0xFFFF:04x}"
            f" cs={pr['color_space']!r} id={'零' if not any(pr['stored_id']) else pr['stored_id'].hex()}"
        )

    if not non_zero:
        # 不是失败：本机没有任何"带非零 ID 的真实 profile"可比对 ⇒ 这条判据无从执行。
        # 门禁约定 3 = SKIP（见 scripts/precision/precision_env.py 的退出码约定）。
        print("\n[SKIP] 本机找不到任何带非零 ProfileID 的真实 profile —— 配方无从实证。")
        print("       这不是通过，也不是失败：换到装有 v4.4 ICC（如 Windows HDR 校准产物）的机器上再跑。")
        return 3

    hits: dict[str, list[Path]] = {}
    for pr in non_zero:
        for name, rec in RECIPES.items():
            blob = build(rec, pr)
            if blob is None:
                continue
            if hashlib.md5(blob).digest() == pr["stored_id"]:
                hits.setdefault(name, []).append(pr["path"])
        for name, zr in WHOLE_RECIPES.items():
            blob = whole(pr, zr)
            if hashlib.md5(blob).digest() == pr["stored_id"]:
                hits.setdefault(name, []).append(pr["path"])
            # 只对头 128 字节做哈希的变体
            if hashlib.md5(blob[:128]).digest() == pr["stored_id"]:
                hits.setdefault(name + "[:128]", []).append(pr["path"])

    print("\n=== 配方命中情况（对真实 profile 的存储值）===")
    if not hits:
        print(
            "[FAIL] 全部 %d 个候选配方都没能复现任一真实 profile 的 ProfileID"
            % (len(RECIPES) + len(WHOLE_RECIPES) * 2)
        )
        print("       ⇒ 不得凭猜测往代码里写重算逻辑。")
        if args.verbose:
            for pr in non_zero:
                for name, rec in RECIPES.items():
                    blob = build(rec, pr)
                    if blob is not None:
                        print(f"  {pr['path'].name} {name}: len={len(blob)} md5={hashlib.md5(blob).hexdigest()} want={pr['stored_id'].hex()}")
        return 1

    for name, paths in sorted(hits.items(), key=lambda kv: -len(kv[1])):
        print(f"  ✅ {name:<34} 命中 {len(paths)}/{len(non_zero)} 份: {[p.name for p in paths][:4]}")
    best = max(hits.items(), key=lambda kv: len(kv[1]))
    if len(best[1]) != len(non_zero):
        print(f"  ⚠️ 最优配方也只命中 {len(best[1])}/{len(non_zero)}，其余待解释")
        for pr in non_zero:
            if pr["path"] not in best[1]:
                print(f"     未命中: {pr['path'].name} (stored={pr['stored_id'].hex()})")
    else:
        print(f"\nCONFIRMED_RECIPE={best[0]}")
        spec = RECIPES.get(best[0]) or WHOLE_RECIPES.get(best[0].replace("[:128]", ""))
        print(json.dumps(spec, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
