"""Generate the MSIX tile/logo assets required by Package.appxmanifest.

Why this file is in version control
-----------------------------------
MSIX packaging **hard-fails** without the logo files referenced by the manifest
(`MakeAppx`/`AppxManifest` validation rejects a package whose VisualElements point at
missing assets). The repo had no icon assets at all — the app draws its own icon at
runtime (`ToastService.EnsureAppIcon`, a solid #0078D4 PNG) — so the packaged build
needs real files on disk.

These are deliberately generated rather than hand-drawn binaries so the shape is
reproducible and reviewable: change the numbers below, re-run, and the PNGs regenerate
byte-for-byte. Keep the color in sync with `ToastService.EnsureAppIcon` (#0078D4) so the
tile, the toast icon and the window icon agree.

Usage
-----
    py scripts/gen-msix-assets.py                 # 写进 src/TrueToneCap.App/Assets/
    py scripts/gen-msix-assets.py --out <目录>     # 试跑到别处，不碰工作树

⚠️ 三族限定符各管一件事，别混（表见 main()，数值取自官方那张换算表）：
   · `.scale-<N>.png`          —— 显示缩放。清单里那张 base 图是 100% 的尺寸，
                                  Windows 按用户的显示比例去挑 scale-125/150/200/400。
   · `.targetsize-<N>.png`     —— 按**精确像素**请求的图标（任务栏、Alt+Tab、开始菜单列表、
                                  文件关联等），N 是物理像素的边长，与显示比例无关。
   · `..._altform-unplated.png` —— targetsize 的"去掉品牌底板"形态，顺序固定写在 targetsize 之后。
   两条硬规则：`scale` 与 `targetsize` **不能出现在同一个变体上**（官方把
   `Square44x44Logo.scale-200_targetsize-24.png` 明确列为无效），且至少要有一个不带 targetsize
   的变体（就是那张 base，等价于 scale-100）。缺了变体不报错，只是外壳自己缩放、小尺寸发糊。
   另注：unplated 形态不要与 `theme-light` 限定符同用（RS5 及更早会因加载方式产生不可预期行为）。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import struct
import zlib

# Same brand color the app itself uses at runtime (ToastService.EnsureAppIcon).
BRAND = (0x00, 0x78, 0xD4)
WHITE = (0xFF, 0xFF, 0xFF)

OUT_DIR = os.path.join("src", "TrueToneCap.App", "Assets")


def png(width, height, draw):
    """Minimal RGBA PNG writer. `draw(x, y) -> (r, g, b, a)`."""
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter: None
        for x in range(width):
            r, g, b, a = draw(x, y)
            raw += bytes((r, g, b, a))
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def solid(w, h):
    return lambda x, y: (*BRAND, 255)


def unplated(w, h, radius_frac=0.18):
    """altform-unplated: 去掉品牌底板，只画三条"文字行"，用品牌蓝画在透明底上。

    白色条是"有底板"时的前景；unplated 没有底板，若仍用白色，在浅色任务栏上就是一片空白
    —— 官方文档专门提醒要验证 altform-unplated 在浅色背景上的对比度，这里靠改成蓝色满足它。
    """
    bar_h = max(1, h // 7)
    bar_w = max(2, int(w * 0.62))
    bx = (w - bar_w) // 2
    by = max(1, (h - (bar_h * 3 + max(1, bar_h // 2) * 2)) // 2)

    def draw(x, y):
        for i in range(3):
            ly = by + i * (bar_h + max(1, bar_h // 2))
            if ly <= y < ly + bar_h:
                w_i = bar_w if i < 2 else int(bar_w * 0.55)
                if bx <= x < bx + w_i:
                    return (*BRAND, 255)
        return (0, 0, 0, 0)

    return draw


def rounded(w, h, radius_frac=0.18):
    """Brand plate with white 'text line' bars — reads as screenshot/OCR at small sizes.

    Corners are rounded by testing the four corner circles directly. The earlier version
    short-circuited with a compound `if` whose inner check ran for ALL pixels (so it
    decided corner-ness from the wrong circle), which left the corners square — visible
    immediately when the 150x150 tile was rendered. Fixed by picking the ONE relevant
    corner per pixel.
    """
    r = max(2, int(min(w, h) * radius_frac))
    bar_h = max(1, h // 7)
    bar_w = max(2, int(w * 0.62))
    bx = (w - bar_w) // 2
    by = max(1, (h - (bar_h * 3 + max(1, bar_h // 2) * 2)) // 2)

    def inside_round(x, y):
        # Determine which corner circle (if any) this pixel belongs to, then test it.
        cx = cy = None
        if x < r:
            cx = r
        elif x > w - 1 - r:
            cx = w - 1 - r
        if y < r:
            cy = r
        elif y > h - 1 - r:
            cy = h - 1 - r
        if cx is None or cy is None:
            return True          # not in any corner region => inside
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

    def draw(x, y):
        if not inside_round(x, y):
            return (0, 0, 0, 0)
        for i in range(3):
            ly = by + i * (bar_h + max(1, bar_h // 2))
            if ly <= y < ly + bar_h:
                w_i = bar_w if i < 2 else int(bar_w * 0.55)   # last line shorter
                if bx <= x < bx + w_i:
                    return (*WHITE, 255)
        return (*BRAND, 255)

    return draw


def main():
    import sys
    out_dir = OUT_DIR
    for i, a in enumerate(sys.argv[1:]):
        if a == "--out" and i + 2 <= len(sys.argv) - 1:
            out_dir = sys.argv[i + 2]
    os.makedirs(out_dir, exist_ok=True)

    # ══════════════════════════════════════════════════════════════════════
    # 变体表 —— 名称与像素全部取自官方"构造 Windows 应用的图标"那张表，不是本地推测。
    #
    # 两条硬规则（否则造出来的是外壳永远解析不到的死文件）：
    #   ① `scale` 与 `targetsize` **不能同时**出现在同一个变体上
    #      （官方明确把 `Square44x44Logo.scale-200_targetsize-24.png` 列为无效）；
    #      且必须至少有一个不带 targetsize 的变体（base 即 scale-100）。
    #   ② 限定符在文件名里的先后是 `targetsize-<N>_altform-unplated`。
    # 所以 DPI 那一路用 .scale-<N>，任务栏/Alt+Tab 那一路用 .targetsize-<N>[_altform-unplated]。
    # ══════════════════════════════════════════════════════════════════════
    files = {}

    # 清单直接引用的 base（不带限定符 = scale-100）
    files["StoreLogo.png"] = (50, 50, "plated")
    files["Square44x44Logo.png"] = (44, 44, "plated")
    files["Square150x150Logo.png"] = (150, 150, "plated")
    files["Square310x310Logo.png"] = (310, 310, "plated")
    files["Wide310x150Logo.png"] = (310, 150, "plated")
    files["SplashScreen.png"] = (620, 300, "plated")

    # 应用列表图标（= Square44x44Logo）官方列出的全部目标尺寸，带底板与不带底板各一份
    APP_LIST_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]
    for s in APP_LIST_SIZES:
        files["Square44x44Logo.targetsize-%d.png" % s] = (s, s, "plated")
        files["Square44x44Logo.targetsize-%d_altform-unplated.png" % s] = (s, s, "unplated")
    for s in (75, 150):
        files["Square150x150Logo.targetsize-%d.png" % s] = (s, s, "plated")

    # 显示缩放档：base 尺寸 × 官方换算（125/150/200/400%）
    for scale, dims in (
        ("125", {"Square44x44Logo": (55, 55), "Square150x150Logo": (188, 188), "Wide310x150Logo": (388, 188)}),
        ("150", {"Square44x44Logo": (66, 66), "Square150x150Logo": (225, 225), "Wide310x150Logo": (465, 225)}),
        ("200", {"Square44x44Logo": (88, 88), "Square150x150Logo": (300, 300), "Wide310x150Logo": (620, 300)}),
        ("400", {"Square44x44Logo": (176, 176), "Square150x150Logo": (600, 600), "Wide310x150Logo": (1240, 600)}),
    ):
        for base, (w, h) in dims.items():
            files["%s.scale-%s.png" % (base, scale)] = (w, h, "plated")

    written = []
    for name, (w, h, form) in sorted(files.items()):
        painter = rounded(w, h) if form == "plated" else unplated(w, h)
        with open(os.path.join(out_dir, name), "wb") as f:
            f.write(png(w, h, painter))
        written.append((name, w, h))

    for n, w, h in written:
        print("  %-56s %dx%d" % (n, w, h))
    print("generated %d asset files into %s" % (len(written), out_dir))


if __name__ == "__main__":
    main()
