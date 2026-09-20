"""AVIF 单帧（all-intra）压缩基准：libaom 各 speed 档的速度 / 压缩率权衡。

AVIF 是单帧图像格式 —— 不存在帧间预测，全部是帧内（intra）压缩，
正好用来观察编码器在纯 intra 场景下的表现。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import zlib, struct, subprocess, time, os, sys

W, H = 3840, 2160          # 4K：贴近真实截图场景
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "artifacts", "bench")
os.makedirs(OUT, exist_ok=True)
PNG = os.path.join(OUT, "test4k.png")


def write_png(path, w, h):
    """生成带细节的测试图：水平/垂直渐变 + 棋盘 + 确定性噪声，避免过于理想化。"""
    raw = bytearray()
    for y in range(h):
        raw.append(0)                      # 行过滤器: None
        g = (y * 255) // h
        for x in range(w):
            r = (x * 255) // w
            b = (x * 3 + y * 5) % 256
            if ((x >> 4) + (y >> 4)) & 1:
                n = (x * 7 + y * 13) % 32       # 轻量噪声，制造高频细节
                r = min(255, r + n - 16); gg = min(255, g + n - 16); bb = min(255, b + n - 16)
            else:
                gg, bb = g, b
            raw += bytes((r & 255, gg & 255, bb & 255))

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    comp = zlib.compress(bytes(raw), 6)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)))
        f.write(chunk(b"IDAT", comp))
        f.write(chunk(b"IEND", b""))


if not os.path.exists(PNG):
    t0 = time.time()
    write_png(PNG, W, H)
    print(f"生成测试图 {W}x{H} ({os.path.getsize(PNG)/1024:.0f} KB, {time.time()-t0:.1f}s)")
else:
    print(f"复用已有测试图 ({os.path.getsize(PNG)/1024:.0f} KB)")

# avifenc 取仓库内那份**被跟踪的**二进制（原先写死 publish/TrueToneCap-v0.3.4-beta/native/...，
# 那个目录是一次性交付包、会被清掉 ⇒ 换机器/清包后这条基准就跑不了，见交接 §32.13）
AVIFENC = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
                       "src", "TrueToneCap.Core", "Resources", "avifenc.exe")
if not os.path.exists(AVIFENC):
    print(f"未找到 avifenc.exe：{AVIFENC}"); sys.exit(1)

print(f"\n{'speed':>6} | {'耗时(s)':>9} | {'大小(KB)':>9} | {'相对基线':>9}")
print("-" * 44)

baseline = None
for speed in (6, 8, 10):
    out = os.path.join(OUT, f"aom_s{speed}.avif")
    if os.path.exists(out):
        os.remove(out)
    cmd = [AVIFENC, "-s", str(speed), "-q", "60", PNG, out]
    t0 = time.time()
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=240)
        el = time.time() - t0
        if r.returncode != 0 or not os.path.exists(out):
            print(f"{speed:>6} | {'失败':>9} | {'-':>9} | {'-':>9}")
            print("   stderr:", r.stderr.decode('utf-8', 'ignore')[:300])
            continue
        size = os.path.getsize(out) / 1024
        if baseline is None:
            baseline = size
        print(f"{speed:>6} | {el:>9.2f} | {size:>9.1f} | {size/baseline:>8.2f}x")
    except subprocess.TimeoutExpired:
        print(f"{speed:>6} | {'>240s':>9} | {'-':>9} | {'-':>9}")

print("\n注：q=60，1080p 单帧；'相对基线' = 该文件大小 / speed4 的文件大小（>1 表示更大=压缩率更差）")
