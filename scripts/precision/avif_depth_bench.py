"""定位 AVIF 4K 慢的真正原因：位深 / 色度子采样 对速度的影响。

背景：实测 4K 8-bit 只要 ~1s，但 4K HDR 截图要 ~24s（日志：位深=10 色度=444）。
这里固定 speed/quality，只变 depth 与 yuv，量化各自的代价。

2026-09-20 从 `.workbuddy-ai/`（第三方 agent 的本地目录、被 gitignore）搬进仓库并改路径解析：
结论"HDR 慢在 10-bit + 4:4:4 这一组合"至今被 §29/§30 的位深批次引用，
原版把 avifenc 与样张路径写死成 `publish/TrueToneCap-v0.3.4-beta/native/` 与 `.workbuddy-ai/bench/`
⇒ 清包或换机即不可复跑。判据/基准必须入库，是交接 §23 就立过的规矩。

跑法：py scripts/precision/avif_depth_bench.py     （4K 样张若不存在，先跑一次 avif_bench.py 生成）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import subprocess
import sys
import time

_REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AVIFENC = os.path.join(_REPO, "src", "TrueToneCap.Core", "Resources", "avifenc.exe")
OUT = os.path.join(_REPO, "artifacts", "bench")
PNG = os.path.join(OUT, "test4k.png")

if not os.path.exists(AVIFENC):
    print(f"未找到 avifenc.exe：{AVIFENC}"); sys.exit(1)
if not os.path.exists(PNG):
    print(f"缺少 4K 样张 {PNG}\n  ⇒ 先跑一次 py scripts/precision/avif_bench.py 生成（它负责造图）")
    sys.exit(1)

cases = [
    ("8-bit 420 (常规 SDR)",  ["-d", "8",  "-y", "420"]),
    ("8-bit 444",             ["-d", "8",  "-y", "444"]),
    ("10-bit 420",            ["-d", "10", "-y", "420"]),
    ("10-bit 444 (HDR 现状)", ["-d", "10", "-y", "444"]),
]

os.makedirs(OUT, exist_ok=True)
print(f"{'配置':<24} | {'耗时(s)':>8} | {'大小(KB)':>9} | 相对")
print("-" * 56)
base = None
for label, args in cases:
    out = os.path.join(OUT, f"d{args[1]}_{args[3]}.avif")
    if os.path.exists(out):
        os.remove(out)
    t0 = time.time()
    try:
        r = subprocess.run([AVIFENC, "-s", "6", "-q", "60"] + args + [PNG, out],
                           capture_output=True, timeout=300)
        el = time.time() - t0
        if r.returncode != 0 or not os.path.exists(out):
            print(f"{label:<24} | {'失败':>8} | {'-':>9} | {r.stderr.decode('utf-8','ignore')[:120]}")
            continue
        size = os.path.getsize(out) / 1024
        if base is None:
            base = el
        print(f"{label:<24} | {el:>8.2f} | {size:>9.1f} | {el/base:>5.1f}x")
    except subprocess.TimeoutExpired:
        print(f"{label:<24} | {'>300s':>8} | {'-':>9} |")
