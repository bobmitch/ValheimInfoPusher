#!/usr/bin/env python3
"""Draws packaging/icon.png, the 256x256 icon Thunderstore requires.

Committed alongside its output so the icon can be regenerated or tweaked
rather than being an opaque binary. Deliberately dependency-free: the repo
already writes its own QR codes in-process rather than reaching for a
service, and a 256x256 flat-colour image does not justify a Pillow install
on every machine that might want to touch it.

    python3 packaging/make-icon.py

Drawn at 3x and box-downsampled, which is where the antialiasing comes from.
"""

import math
import struct
import zlib
from pathlib import Path

SIZE = 256
SS = 3                      # supersample factor
W = H = SIZE * SS

BG_TOP = (0x0e, 0x14, 0x1c)
BG_BOTTOM = (0x1a, 0x23, 0x31)
AMBER = (0xf0, 0xb4, 0x45)
AMBER_DARK = (0xc8, 0x8c, 0x2c)
SIGNAL = (0x5f, 0xb3, 0xc9)
HOLE = (0x11, 0x18, 0x21)

CX, CY = W / 2, H * 0.43     # pin head centre
R_HEAD = W * 0.125           # pin head radius
R_HOLE = W * 0.052
TIP_Y = CY + W * 0.30        # where the pin points


def blend(buf, x, y, colour, alpha):
    if alpha <= 0:
        return
    i = (y * W + x) * 3
    if alpha >= 1:
        buf[i:i + 3] = bytes(colour)
        return
    for k in range(3):
        buf[i + k] = int(buf[i + k] * (1 - alpha) + colour[k] * alpha + 0.5)


def fill_background(buf):
    for y in range(H):
        t = y / (H - 1)
        row = bytes(
            int(BG_TOP[k] + (BG_BOTTOM[k] - BG_TOP[k]) * t + 0.5) for k in range(3)
        )
        buf[y * W * 3:(y + 1) * W * 3] = row * W


def draw_ring(buf, cx, cy, radius, width, colour, alpha):
    """One concentric signal ring, centred on the pin head."""
    inner, outer = radius - width / 2, radius + width / 2
    y0, y1 = max(0, int(cy - outer) - 1), min(H, int(cy + outer) + 2)
    for y in range(y0, y1):
        dy = y - cy
        if abs(dy) > outer:
            continue
        span = math.sqrt(outer * outer - dy * dy)
        x0, x1 = max(0, int(cx - span) - 1), min(W, int(cx + span) + 2)
        for x in range(x0, x1):
            d = math.hypot(x - cx, dy)
            if inner <= d <= outer:
                blend(buf, x, y, colour, alpha)


def draw_pin(buf):
    # Teardrop: the head circle unioned with the triangle formed by the tip
    # and the circle's two tangent points, so head and tail meet smoothly
    # instead of showing a seam.
    tangent = math.acos(R_HEAD / (TIP_Y - CY))
    tx, ty = R_HEAD * math.sin(tangent), R_HEAD * math.cos(tangent)
    tri = ((CX, TIP_Y), (CX - tx, CY + ty), (CX + tx, CY + ty))

    def in_triangle(px, py):
        def side(a, b):
            return (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0])
        s = (side(tri[0], tri[1]), side(tri[1], tri[2]), side(tri[2], tri[0]))
        return all(v >= 0 for v in s) or all(v <= 0 for v in s)

    y0, y1 = int(CY - R_HEAD) - 2, int(TIP_Y) + 2
    for y in range(max(0, y0), min(H, y1)):
        for x in range(int(CX - R_HEAD) - 2, int(CX + R_HEAD) + 2):
            if not (0 <= x < W):
                continue
            d = math.hypot(x - CX, y - CY)
            if d <= R_HEAD or in_triangle(x, y):
                # Shade the lower half slightly so the pin has some volume.
                t = min(1.0, max(0.0, (y - (CY - R_HEAD)) / (TIP_Y - CY + R_HEAD)))
                colour = tuple(
                    int(AMBER[k] + (AMBER_DARK[k] - AMBER[k]) * t) for k in range(3)
                )
                blend(buf, x, y, colour, 1.0)

    for y in range(int(CY - R_HOLE) - 2, int(CY + R_HOLE) + 2):
        for x in range(int(CX - R_HOLE) - 2, int(CX + R_HOLE) + 2):
            if 0 <= x < W and 0 <= y < H and math.hypot(x - CX, y - CY) <= R_HOLE:
                blend(buf, x, y, HOLE, 1.0)


def downsample(buf):
    out = bytearray(SIZE * SIZE * 3)
    area = SS * SS
    for y in range(SIZE):
        for x in range(SIZE):
            acc = [0, 0, 0]
            for sy in range(SS):
                base = ((y * SS + sy) * W + x * SS) * 3
                for sx in range(SS):
                    i = base + sx * 3
                    acc[0] += buf[i]
                    acc[1] += buf[i + 1]
                    acc[2] += buf[i + 2]
            o = (y * SIZE + x) * 3
            for k in range(3):
                out[o + k] = acc[k] // area
    return out


def write_png(path, size, pixels):
    raw = b"".join(
        b"\x00" + bytes(pixels[y * size * 3:(y + 1) * size * 3]) for y in range(size)
    )

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    Path(path).write_bytes(png)


def main():
    buf = bytearray(W * H * 3)
    fill_background(buf)
    # Rings sit behind the pin, reading as a beacon rather than a plain marker.
    for radius, width, alpha in ((0.222, 0.020, 0.55), (0.306, 0.018, 0.34), (0.390, 0.016, 0.18)):
        draw_ring(buf, CX, CY, W * radius, W * width, SIGNAL, alpha)
    draw_pin(buf)
    out = Path(__file__).with_name("icon.png")
    write_png(out, SIZE, downsample(buf))
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
