"""判据：JPEG 里的 ICC 必须是 APP2 + 分块字段，并且要被【另一家实现】原样读回来。

为什么需要它（2026-09-20，交接 §31.3-8）：本仓库曾把 ICC 写成 **APP1 且缺 seq_no/chunk_cnt 两字节**。
按 ICC.1/ISO 12233 与 libjpeg-turbo 的实现（jcicc.c 用 `ICC_MARKER = JPEG_APP0+2`，
jdicc.c 读 `data[12]=seq_no`、`data[13]=num_markers`），只认 APP2 的实现有
libjpeg-turbo / ImageMagick / Chromium-Skia / Pillow —— 也就是说这四家读我们的广色域 JPEG
得到的是"**没有 ICC**"，色域声明在最主流的读取链上静默丢失。自家解析器按自家写法读，永远读得到，
所以这条必须换一个独立实现来判：**Pillow**。

四条断言，缺一条就会漏掉一类回归：
  ① 产物里不许出现 APP1+ICC_PROFILE（旧缺陷形态）；
  ② ICC 段必须是 APP2，且 seq_no 从 1 递增、每段的 chunk_cnt 都等于总段数；
  ③ 拼回来的 profile 头部自洽（`acsp` 在 36、声明长度 == 实际长度）；
  ④ Pillow 读回的字节必须与 ②③ 拼出的**逐字节相同**，且至少一张真的带 ICC
     （否则"一个 ICC 都没有"也能把前面几条全判绿 —— 那是假绿灯）。
另加一条对照：P3 / BT.2020 / Adobe 三个样本的 ICC 必须两两不同，
证明"按目标色域换 profile"这一步真的落到了文件里，而不是嵌了同一个。

用法: py scripts/precision/jpeg_icc_readback.py [--samples 目录]
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import os
import struct
import sys

import precision_env as pe

ICC_ID = b"ICC_PROFILE\x00"
WIDE_GAMUT_KEYS = ("p3", "bt2020", "adobe")     # 这些样本必须带 ICC


def scan_segments(data: bytes):
    """按 JPEG 公共规范扫段：返回 [(marker, payload)]，遇 SOS 停止。"""
    out = []
    if len(data) < 2 or data[0:2] != b"\xff\xd8":
        return out
    pos = 2
    while pos + 4 <= len(data):
        if data[pos] != 0xFF:
            pos += 1
            continue
        marker = data[pos + 1]
        if marker == 0xDA:                      # SOS：之后是熵编码数据
            break
        if marker in (0x01, 0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
            pos += 2
            continue
        seg_len = struct.unpack(">H", data[pos + 2:pos + 4])[0]
        if seg_len < 2 or pos + 2 + seg_len > len(data):
            break
        out.append((marker, data[pos + 4:pos + 2 + seg_len]))
        pos += 2 + seg_len
    return out


def icc_from_segments(segs):
    """取出所有 ICC 段并校验分块字段；返回 (profile 或 None, 错误列表)。"""
    errs = []
    chunks = []
    for marker, payload in segs:
        if marker == 0xE1 and payload[:len(ICC_ID)] == ICC_ID:
            errs.append("ICC 写成 APP1(0xE1) —— libjpeg-turbo/Pillow/Chromium 只扫 APP2，等于没声明色域")
            continue
        if marker != 0xE2 or payload[:len(ICC_ID)] != ICC_ID:
            continue
        if len(payload) < len(ICC_ID) + 2:
            errs.append("APP2 段短于标识符+分块字段")
            continue
        seq, cnt = payload[len(ICC_ID)], payload[len(ICC_ID) + 1]
        chunks.append((seq, cnt, payload[len(ICC_ID) + 2:]))
    if not chunks:
        return None, errs
    cnts = {c for _, c, _ in chunks}
    if len(cnts) != 1:
        errs.append(f"chunk_cnt 各段不一致：{sorted(cnts)}")
    total = next(iter(cnts))
    seqs = sorted(s for s, _, _ in chunks)
    if seqs != list(range(1, total + 1)):
        errs.append(f"seq_no 不是 1..{total} 的完整排列：{seqs}")
    prof = b"".join(d for _, _, d in sorted(chunks))
    return prof, errs


def check_icc_header(prof: bytes):
    errs = []
    if len(prof) < 132:
        return [f"profile 只有 {len(prof)}B，不足 132B 头"]
    if prof[36:40] != b"acsp":
        errs.append(f"偏移 36 不是 'acsp'（实为 {prof[36:40]!r}）")
    declared = struct.unpack(">I", prof[0:4])[0]
    if declared != len(prof):
        errs.append(f"头部声明长度 {declared} != 实际 {len(prof)}")
    return errs


def pil_read(path):
    """外部实现读回。Pillow 不可用 ⇒ 整条判据作废（跳过，不冒充通过）。"""
    try:
        from PIL import Image
    except Exception as ex:                       # noqa: BLE001 —— 缺依赖属前置不满足
        print(f"SKIP: 无法导入 Pillow（{ex}）⇒ 本判据的核心就是「外部实现读回」，"
              f"没有替代物，不能靠自家解析器凑数")
        sys.exit(pe.EXIT_SKIP)
    try:
        return Image.open(path).info.get("icc_profile") or b""
    except Exception as ex:                       # noqa: BLE001
        return f"ERR:{ex}"


def main() -> int:
    d = pe.samples_dir("cicp", pe.opt("--samples"))
    files = sorted(f for f in os.listdir(d) if f.lower().endswith((".jpg", ".jpeg")))
    if not files:
        print(f"SKIP: {d} 下没有 JPEG 样张（跑 --cicp-sweep 产出）")
        return pe.EXIT_SKIP

    bad = 0
    with_icc = []
    per_gamut: dict[str, bytes] = {}
    for f in files:
        p = os.path.join(d, f)
        segs = scan_segments(open(p, "rb").read())
        prof, errs = icc_from_segments(segs)
        if prof is not None:
            errs += check_icc_header(prof)
            with_icc.append(f)
            for k in WIDE_GAMUT_KEYS:
                if k in f.lower():
                    per_gamut[k] = prof
        # 关键一条：另一家实现必须读到同样的字节
        got = pil_read(p)
        if isinstance(got, str):
            print(f"  ❌ {f}: Pillow 解图失败 {got}")
            bad += 1
            continue
        if bytes(got) != (prof or b""):
            errs.append(f"Pillow 读回 {len(got)}B 与段内拼出 {len(prof or b'')}B 不同源")
        for e in errs:
            print(f"  ❌ {f}: {e}")
        bad += len(errs)
        if not errs:
            print(f"  ✅ {f}: ICC {len(prof) if prof else 0}B"
                  f"（APP2{' 分块 ' + str(len([1 for m, _ in segs if m == 0xE2 and _[:12] == ICC_ID])) if prof else ''}）")

    # 反假绿灯：一张 ICC 都没有 ⇒ 上面所有循环体都是空转，必须判失败
    if not with_icc:
        print("  ❌ 没有任何样张带 ICC ⇒ 本判据无从执行（不许当成通过）。"
              "检查广色域 JPEG 是否根本没嵌 profile")
        bad += 1
    if len(with_icc) < 3:
        print(f"  ❌ 带 ICC 的样张只有 {len(with_icc)} 张（期望 ≥3：P3/BT.2020/Adobe 各一）")
        bad += 1
    if len(per_gamut) >= 2:
        vals = list(per_gamut.items())
        for i in range(len(vals)):
            for j in range(i + 1, len(vals)):
                if vals[i][1] == vals[j][1]:
                    print(f"  ❌ {vals[i][0]} 与 {vals[j][0]} 嵌的是同一个 profile ⇒ "
                          f"「按目标色域换 profile」没落地")
                    bad += 1

    print("结论:", "JPEG ICC 可被外部实现读回且分块自洽" if bad == 0 else f"{bad} 项不符")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
