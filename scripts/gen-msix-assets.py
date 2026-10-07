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
    py scripts/gen-msix-assets.py
    # writes into src/TrueToneCap.App/Assets/

⚠️ The `targetsize-*` variants are what Windows uses for the Start menu / taskbar at
   different display scales. Omitting them is legal but makes the tile look blurry on
   scaled displays, so the common set is generated here.
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
    os.makedirs(OUT_DIR, exist_ok=True)
    written = []

    # Square44x44Logo / Square150x150Logo / StoreLogo and their targetsize variants.
    # targetsize-* must be square and are used at specific pixel sizes by the shell.
    targets = {
        "StoreLogo.png": 50,
        "Square44x44Logo.png": 44,
        "Square150x150Logo.png": 150,
        "Square310x310Logo.png": 310,
        "Wide310x150Logo.png": None,   # special-cased below (non-square)
        "SplashScreen.png": None,      # special-cased below
    }
    for name, size in targets.items():
        if size is not None:
            data = png(size, size, rounded(size, size))
            written.append((name, size, size))

    # Wide tile 310x150
    data = png(310, 150, rounded(310, 150))
    with open(os.path.join(OUT_DIR, "Wide310x150Logo.png"), "wb") as f:
        f.write(data)
    written.append(("Wide310x150Logo.png", 310, 150))

    # Splash 620x300 (solid brand; manifest references it)
    data = png(620, 300, rounded(620, 300))
    with open(os.path.join(OUT_DIR, "SplashScreen.png"), "wb") as f:
        f.write(data)
    written.append(("SplashScreen.png", 620, 300))

    # targetsize variants for the two most-used logos
    for base, sizes in (("Square44x44Logo", [16, 24, 32, 48, 256]),
                        ("Square150x150Logo", [75, 150])):
        for s in sizes:
            d = png(s, s, rounded(s, s))
            with open(os.path.join(OUT_DIR, f"{base}.targetsize-{s}.png"), "wb") as f:
                f.write(d)
            written.append((f"{base}.targetsize-{s}.png", s, s))

    # regenerate the base files too (the dict loop above wrote them already)
    for name, size in (("StoreLogo.png", 50), ("Square44x44Logo.png", 44),
                       ("Square150x150Logo.png", 150), ("Square310x310Logo.png", 310)):
        with open(os.path.join(OUT_DIR, name), "wb") as f:
            f.write(png(size, size, rounded(size, size)))

    for n, w, h in written:
        print("  %-42s %dx%d" % (n, w, h))
    print("generated %d asset files into %s" % (len(written) + 4, OUT_DIR))


if __name__ == "__main__":
    main()
