#!/usr/bin/env python3
"""Generates Assets/app.ico and Assets/app-paused.ico without external dependencies.

The icon is a rounded warm-neutral tile with a terracotta "译"-like stroke mark (a simple glyph
built from rectangles so no font is required). The paused variant is desaturated.
Sizes: 16, 20, 24, 32, 48, 64, 128, 256 (PNG-compressed entries, as Windows expects).
"""
import struct
import zlib
import os

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "BiliIndexTranslate.App", "Assets")


def png_bytes(size, pixels):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw.extend(pixels[y][x])

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def blend(dst, src, a):
    return tuple(int(round(d + (s - d) * a)) for d, s in zip(dst, src))


def rounded_rect_coverage(x, y, size, radius, inset):
    """Coverage 0..1 of pixel (x, y) by a rounded rectangle with given inset and corner radius (4x4 supersampling)."""
    cov = 0
    lo, hi = inset, size - inset
    for sy in range(4):
        for sx in range(4):
            px = x + (sx + 0.5) / 4
            py = y + (sy + 0.5) / 4
            if px < lo or px > hi or py < lo or py > hi:
                continue
            cx = min(max(px, lo + radius), hi - radius)
            cy = min(max(py, lo + radius), hi - radius)
            if (px - cx) ** 2 + (py - cy) ** 2 <= radius ** 2:
                cov += 1
    return cov / 16.0


def mark_coverage(x, y, size):
    """A simplified ideographic mark: a horizontal top bar, a vertical stem, a short lower-right stroke."""
    s = size
    px, py = x + 0.5, y + 0.5
    bar_t = 0.30 * s
    bar_b = 0.30 * s + max(1.0, 0.09 * s)
    bar_l, bar_r = 0.26 * s, 0.74 * s
    stem_l = 0.50 * s - max(0.5, 0.045 * s)
    stem_r = 0.50 * s + max(0.5, 0.045 * s)
    stem_t, stem_b = bar_t, 0.74 * s
    foot_t = 0.58 * s
    foot_b = foot_t + max(1.0, 0.08 * s)
    foot_l, foot_r = 0.50 * s, 0.70 * s
    cov = 0
    for sy in range(4):
        for sx in range(4):
            qx = x + (sx + 0.5) / 4
            qy = y + (sy + 0.5) / 4
            if (bar_l <= qx <= bar_r and bar_t <= qy <= bar_b) or \
               (stem_l <= qx <= stem_r and stem_t <= qy <= stem_b) or \
               (foot_l <= qx <= foot_r and foot_t <= qy <= foot_b):
                cov += 1
    return cov / 16.0


def render(size, paused):
    surface = (0xF7, 0xF2, 0xEC)
    border = (0xD9, 0xCF, 0xC3)
    accent = (0xB2, 0x56, 0x2F)
    if paused:
        surface = (0xEC, 0xEA, 0xE7)
        border = (0xC9, 0xC5, 0xC0)
        accent = (0x9A, 0x94, 0x8E)
    radius = size * 0.22
    inset = size * 0.04
    pixels = []
    for y in range(size):
        row = []
        for x in range(size):
            outer = rounded_rect_coverage(x, y, size, radius, inset)
            inner = rounded_rect_coverage(x, y, size, max(0.5, radius - max(1.0, size * 0.05)), inset + max(1.0, size * 0.05))
            if outer <= 0:
                row.append((0, 0, 0, 0))
                continue
            color = blend(border, surface, inner)
            m = mark_coverage(x, y, size)
            if m > 0:
                color = blend(color, accent, m)
            row.append((color[0], color[1], color[2], int(round(255 * outer))))
        pixels.append(row)
    return png_bytes(size, pixels)


def write_ico(path, paused):
    images = [(s, render(s, paused)) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    data = b""
    for s, png in images:
        entries += struct.pack("<BBBBHHII", s if s < 256 else 0, s if s < 256 else 0, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    with open(path, "wb") as f:
        f.write(header + entries + data)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    write_ico(os.path.join(OUT, "app.ico"), paused=False)
    write_ico(os.path.join(OUT, "app-paused.ico"), paused=True)
    print("wrote app.ico and app-paused.ico to", os.path.abspath(OUT))
