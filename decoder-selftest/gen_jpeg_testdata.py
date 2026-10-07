#!/usr/bin/env python3
"""
Generates REAL JPEG files with Pillow (libjpeg-turbo — a completely independent implementation
from the C# decoder under test) together with Pillow's OWN decoded RGB reference bytes.

This is the honesty anchor for TizenAmbilight's baseline JPEG decoder: the C# side never sees
anything but the JPEG bytes and Pillow's pixels, so a decoder bug cannot hide behind a
hand-written expectation.

For every case it writes, into ./testdata/:
    <name>.jpg      the encoded JPEG (what the TV's capture library would hand us)
    <name>.rgba     Pillow's decode, RGB expanded to RGBA (width*height*4, alpha=255)

Cases are chosen to hit every branch that could plausibly be wrong:

    q90_480x270           the real capture geometry, default 4:2:0 subsampling
    q90_480x270_444       subsampling=0, no chroma upsampling at all
    q90_480x270_422       subsampling=1, horizontal-only chroma upsampling
    q90_480x270_rst       restart markers every MCU row (exercises the RST path)
    q85_640x360_photo     busier content (the requested main-loop size)
    q10_320x180           coarse quantisation, heavy AC truncation
    q75_64x64_gray        single-component grayscale
    odd_123x45            odd width AND height (block padding / edge clamping)
    tiny_17x9             smaller than one MCU
    flat_96x64            near-uniform (all-EOB blocks, DPCM DC)

A JPEG marker probe also emits jpeg_manifest.json with the SOF marker and sampling factors, so
the C# test can state which geometry each case really had.
"""
import json
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "testdata")
os.makedirs(OUT, exist_ok=True)

manifest = []


def gradient(w, h):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = (x * 255 // max(1, w - 1), y * 255 // max(1, h - 1), (x + y) % 256)
    return img


def photo(w, h):
    """Smooth ramps plus a deterministic pseudo-random dither — real video content has both
    large flat/ramped areas and high-frequency detail, and the two stress different parts of
    the decoder (DC prediction vs AC/quantisation)."""
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            r = (x * 200 // max(1, w - 1) + 20 * ((x * 7 + y * 13) % 5)) & 0xFF
            g = (y * 180 // max(1, h - 1) + 25 * ((x * 11 + y * 3) % 7)) & 0xFF
            b = (128 + 100 * ((x ^ y) & 1) + 30 * ((x + 2 * y) % 3)) & 0xFF
            px[x, y] = (r, g, b)
    return img


def flat(w, h):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = (90, 140, 200)
    return img


CASES = [
    ("jpeg_q90_480x270",       lambda: gradient(480, 270), dict(quality=90)),
    ("jpeg_q90_480x270_444",   lambda: gradient(480, 270), dict(quality=90, subsampling=0)),
    ("jpeg_q90_480x270_422",   lambda: gradient(480, 270), dict(quality=90, subsampling=1)),
    ("jpeg_q90_480x270_rst",   lambda: gradient(480, 270), dict(quality=90, restart_marker_rows=1)),
    ("jpeg_q85_640x360_photo", lambda: photo(640, 360),    dict(quality=85)),
    ("jpeg_q85_640x360_photo_444", lambda: photo(640, 360), dict(quality=85, subsampling=0)),
    ("jpeg_q85_640x360_photo_422", lambda: photo(640, 360), dict(quality=85, subsampling=1)),
    ("jpeg_q10_320x180",       lambda: gradient(320, 180), dict(quality=10)),
    ("jpeg_q75_64x64_gray",    lambda: gradient(64, 64).convert("L"), dict(quality=75)),
    ("jpeg_odd_123x45",        lambda: gradient(123, 45),  dict(quality=90)),
    ("jpeg_tiny_17x9",         lambda: gradient(17, 9),    dict(quality=80)),
    ("jpeg_flat_96x64",        lambda: flat(96, 64),       dict(quality=95)),
]


def sof_info(path):
    """Read the SOF marker + sampling factors straight out of the file, so the test can report
    the real geometry Pillow produced rather than assuming it."""
    with open(path, "rb") as f:
        d = f.read()
    i = 2
    while i + 1 < len(d):
        if d[i] != 0xFF:
            i += 1
            continue
        while i < len(d) and d[i] == 0xFF:
            i += 1
        if i >= len(d):
            break
        m = d[i]
        i += 1
        if m in (0xD8, 0xD9, 0x01) or 0xD0 <= m <= 0xD7:
            continue
        if i + 1 >= len(d):
            break
        ln = (d[i] << 8) | d[i + 1]
        if 0xC0 <= m <= 0xCF and m not in (0xC4, 0xC8, 0xCC):
            nf = d[i + 7]
            comps = []
            for c in range(nf):
                cid = d[i + 8 + c * 3]
                hv = d[i + 9 + c * 3]
                comps.append([cid, hv >> 4, hv & 15])
            return {"marker": "SOF%d" % (m - 0xC0), "width": (d[i + 5] << 8) | d[i + 6],
                    "height": (d[i + 3] << 8) | d[i + 4], "precision": d[i + 2],
                    "components": nf, "sampling": comps,
                    "dri": marker_dri(d)}
        if m == 0xDA:
            break
        i += ln
    return None


def marker_dri(d):
    i = 2
    while i + 1 < len(d):
        if d[i] != 0xFF:
            i += 1
            continue
        while i < len(d) and d[i] == 0xFF:
            i += 1
        if i >= len(d):
            break
        m = d[i]
        i += 1
        if m in (0xD8, 0xD9, 0x01) or 0xD0 <= m <= 0xD7:
            continue
        if i + 1 >= len(d):
            break
        ln = (d[i] << 8) | d[i + 1]
        if m == 0xDD:
            return (d[i + 2] << 8) | d[i + 3]
        if m == 0xDA:
            break
        i += ln
    return 0


for name, mk, kw in CASES:
    img = mk()
    path = os.path.join(OUT, name + ".jpg")
    img.save(path, format="JPEG", **kw)

    # Pillow's OWN decode, expanded to the RGBA layout the C# decoder must produce.
    ref = Image.open(path).convert("RGB")
    raw = ref.tobytes()   # 3 bytes/pixel
    buf = bytearray(ref.width * ref.height * 4)
    buf[0::4] = raw[0::3]
    buf[1::4] = raw[1::3]
    buf[2::4] = raw[2::3]
    buf[3::4] = b"\xff" * (ref.width * ref.height)
    with open(os.path.join(OUT, name + ".rgba"), "wb") as f:
        f.write(bytes(buf))

    info = sof_info(path)
    rec = {
        "name": name,
        "file": name + ".jpg",
        "reference": name + ".rgba",
        "width": ref.width,
        "height": ref.height,
        "pillow_mode": Image.open(path).mode,
        "bytes": os.path.getsize(path),
        "bytes_per_pixel_ref": len(buf),
        "jpeg": info,
    }
    manifest.append(rec)
    print("wrote %-26s %4dx%-4d %-4s %7d B  %s sampling=%s dri=%s" % (
        name, ref.width, ref.height, Image.open(path).mode, rec["bytes"],
        info["marker"] if info else "?", info["sampling"] if info else "?",
        info["dri"] if info else "?"))

with open(os.path.join(OUT, "jpeg_manifest.json"), "w") as f:
    json.dump({"cases": manifest}, f, indent=2)
print("\nmanifest: %d JPEG cases -> testdata/jpeg_manifest.json" % len(manifest))

# ---------------------------------------------------------------------------------------------
# Files that must be REFUSED, so the decoder can never quietly emit a wrong frame for them.
# ---------------------------------------------------------------------------------------------
cmyk = Image.new("CMYK", (32, 32))
cpx = cmyk.load()
for yy in range(32):
    for xx in range(32):
        cpx[xx, yy] = (xx * 8 % 256, yy * 8 % 256, (xx + yy) * 4 % 256, 0)
cmyk_path = os.path.join(OUT, "jpeg_cmyk_32x32.jpg")
cmyk.save(cmyk_path, format="JPEG", quality=80)
cmyk_info = sof_info(cmyk_path)
print("wrote %-26s %4dx%-4d %-4s %7d B  %s components=%s  (must be REFUSED)" % (
    "jpeg_cmyk_32x32", 32, 32, Image.open(cmyk_path).mode,
    os.path.getsize(cmyk_path), cmyk_info["marker"] if cmyk_info else "?",
    cmyk_info["components"] if cmyk_info else "?"))

with open(os.path.join(OUT, "jpeg_reject_manifest.json"), "w") as f:
    json.dump({
        "cmyk_4component": {"file": "jpeg_cmyk_32x32.jpg",
                            "components": cmyk_info["components"] if cmyk_info else -1,
                            "why": "4-component CMYK/YCCK is deliberately unsupported"},
        "progressive_sof2": {"file": "probe_progressive_300x200.jpg",
                             "why": "SOF2 progressive is deliberately unsupported"},
        "not_a_jpeg": {"file": "probe_real.png",
                       "why": "a PNG handed to the JPEG decoder must be refused"},
    }, f, indent=2)
print("wrote jpeg_reject_manifest.json (3 must-refuse cases)")
