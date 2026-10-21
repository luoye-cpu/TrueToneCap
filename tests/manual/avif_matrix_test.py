# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 验证 matrix=0 (identity) vs matrix=9 (BT.2020 NCL) 的解码兼容性
# 同一 PNG → avifenc 两种 matrix → PIL 解码对比
import struct, os, subprocess, tempfile, zlib
from PIL import Image

avifenc = r'C:\PLAN\TrueToneCap\publish\PLAN\tools\avifenc.exe'
tmp = tempfile.mkdtemp()

def make_png(path, rgb_lin, nits_scale=80.0):
    w = h = 64
    def pq_encode(nits):
        L = max(nits / 10000.0, 0.0)
        Lp = L ** (2610/16384)
        num = 3424/4096 + (2413/128) * Lp
        den = 1 + (2392/128) * Lp
        return (num / den) ** (2523/32)
    r, g, b = rgb_lin
    pr = int(pq_encode(r * nits_scale) * 65535)
    pg = int(pq_encode(g * nits_scale) * 65535)
    pb = int(pq_encode(b * nits_scale) * 65535)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            for v in (pr, pg, pb, 65535):
                raw.append((v >> 8) & 0xFF)
                raw.append(v & 0xFF)
    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 16, 6, 0, 0, 0)))
        f.write(chunk(b'cICP', bytes([9, 16, 0, 1])))
        f.write(chunk(b'IDAT', zlib.compress(bytes(raw), 1)))
        f.write(chunk(b'IEND', b''))

colors = [('red', (1, 0, 0)), ('green', (0, 1, 0)), ('blue', (0, 0, 1)),
          ('yellow', (1, 1, 0)), ('cyan', (0, 1, 1)), ('magenta', (1, 0, 1)),
          ('orange', (1, 0.5, 0)), ('skin', (0.8, 0.55, 0.4)), ('white', (1, 1, 1))]

for matrix in [0, 9]:
    print(f'\n═══ matrix={matrix} ═══')
    for name, rgb in colors:
        png = os.path.join(tmp, f'{name}.png')
        avif = os.path.join(tmp, f'{name}_m{matrix}.avif')
        make_png(png, rgb)
        r = subprocess.run([avifenc, '-q', '60', '-s', '4', '--depth', '10',
                            '--cicp', f'9/16/{matrix}', png, avif],
                           capture_output=True, text=True)
        if r.returncode != 0:
            print(f'{name}: 失败 {r.stderr[:100]}')
            continue
        img = Image.open(avif).convert('RGB')
        # 中位数采样
        vals = []
        for y in range(0, 64, 8):
            for x in range(0, 64, 8):
                vals.append(img.getpixel((x, y)))
        vals.sort()
        mid = vals[len(vals)//2]
        print(f'  {name}: {mid}')
