#!/usr/bin/env python3
"""
Independent PNG test-data generator for the TizenAmbilight decoder self-test.

Deliberately NOT using PIL: this writes PNGs by hand with zlib so that the
per-scanline FILTER TYPE of every row is under our control. That is what makes
it a real test of the decoder's Sub/Up/Average/Paeth un-filtering code rather
than a test of one library against another.

For every generated <name>.png it also writes <name>.rgba, the exact expected
decoded pixel bytes (row-major RGBA), so the C# decoder can be byte-compared.
"""

import base64
import json
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "testdata")


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data +
            struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


def encode_rgba(w, h, rows, filters, split_idat=False):
    """rows: list of h rows, each a list of (r,g,b,a). filters: list of ints cycled per row."""
    raw = bytearray()
    prev = bytes(w * 4)
    for y in range(h):
        rowbytes = bytes(v for px in rows[y] for v in px)
        ft = filters[y % len(filters)]
        out = bytearray(w * 4)
        for i in range(w * 4):
            a = rowbytes[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            x = rowbytes[i]
            if ft == 0:
                v = x
            elif ft == 1:
                v = (x - a) & 0xFF
            elif ft == 2:
                v = (x - b) & 0xFF
            elif ft == 3:
                v = (x - ((a + b) >> 1)) & 0xFF
            elif ft == 4:
                v = (x - paeth(a, b, c)) & 0xFF
            else:
                raise ValueError("bad filter " + str(ft))
            out[i] = v
        raw.append(ft)
        raw += out
        prev = rowbytes

    idat = zlib.compress(bytes(raw), 9)
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)

    if split_idat and len(idat) > 8:
        cut = len(idat) // 2
        png += chunk(b"IDAT", idat[:cut])
        png += chunk(b"IDAT", idat[cut:])
    else:
        png += chunk(b"IDAT", idat)

    png += chunk(b"IEND", b"")

    expected = b"".join(bytes(v for px in rows[y] for v in px) for y in range(h))
    return png, expected


def gradient_rows(w, h):
    rows = []
    for y in range(h):
        row = []
        for x in range(w):
            row.append(((x * 7) & 0xFF, (y * 11) & 0xFF, ((x + y) * 5) & 0xFF, 255))
        rows.append(row)
    return rows


def pseudo_random_rows(w, h):
    rows = []
    s = 12345
    for y in range(h):
        row = []
        for x in range(w):
            s = (1103515245 * s + 12345) & 0x7FFFFFFF
            r = s & 0xFF
            s = (1103515245 * s + 12345) & 0x7FFFFFFF
            g = s & 0xFF
            s = (1103515245 * s + 12345) & 0x7FFFFFFF
            b = s & 0xFF
            row.append((r, g, b, 255))
        rows.append(row)
    return rows


def solid_rows(w, h, rgba):
    return [[rgba] * w for _ in range(h)]


def main():
    os.makedirs(OUT, exist_ok=True)
    cases = []

    # 1. tiny, every filter type exactly once (rows 0..4)
    rows = pseudo_random_rows(5, 5)
    cases.append(("tiny_allfilters", 5, 5, rows, [0, 1, 2, 3, 4], False))

    # 2. Paeth-only gradient
    rows = gradient_rows(16, 9)
    cases.append(("gradient_paeth", 16, 9, rows, [4], False))

    # 3. Average-only
    rows = gradient_rows(13, 7)
    cases.append(("gradient_average", 13, 7, rows, [3], False))

    # 4. IDAT split across two chunks (exercises chunk concatenation)
    rows = gradient_rows(11, 6)
    cases.append(("split_idat", 11, 6, rows, [0, 4, 2, 1, 3], True))

    # 5. 1x1 edge case
    cases.append(("one_pixel", 1, 1, solid_rows(1, 1, (0xDE, 0xAD, 0xBE, 0xFF)), [0], False))

    # 6. realistic capture size 960x540 (the library clamps 1280x720 -> 960x540)
    rows = gradient_rows(960, 540)
    cases.append(("capture_size_960x540", 960, 540, rows, [0, 1, 2, 3, 4], False))

    manifest = []
    for name, w, h, rows, filters, split in cases:
        png, expected = encode_rgba(w, h, rows, filters, split)
        p = os.path.join(OUT, name + ".png")
        with open(p, "wb") as f:
            f.write(png)
        with open(os.path.join(OUT, name + ".rgba"), "wb") as f:
            f.write(expected)
        manifest.append({"name": name, "png": name + ".png", "rgba": name + ".rgba",
                         "w": w, "h": h, "bytes": len(png), "filters": filters})
        print("%-24s %4dx%-4d png=%6d rgbabytes=%8d filters=%s" %
              (name, w, h, len(png), len(expected), filters))

    with open(os.path.join(OUT, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)

    # ---- embedded in-app self-check image: 8x8, all five filters, mid-size ----
    rows = pseudo_random_rows(8, 8)
    png, expected = encode_rgba(8, 8, rows, [0, 1, 2, 3, 4], True)
    emb = {
        "w": 8, "h": 8,
        "png_b64": base64.b64encode(png).decode(),
        "rgba_hex": expected.hex(),
    }
    with open(os.path.join(OUT, "embedded.json"), "w") as f:
        json.dump(emb, f)
    print("\nembedded 8x8 self-check png: %d bytes b64=%d chars, rgba=%d bytes" %
          (len(png), len(emb["png_b64"]), len(expected)))


if __name__ == "__main__":
    main()
