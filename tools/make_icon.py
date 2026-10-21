"""Рисует значок «Дымки»: синяя плитка с тремя волнами дымки.

    python tools/make_icon.py

Сохраняет src/Dymka/Assets/dymka.ico (16-128 px - BMP, 256 px - PNG: проводник и
панель задач Windows надёжно читают маленькие значки только в виде BMP) и dymka-256.png.
"""

import io
import os
import struct

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ACCENT = (0x3D, 0x63, 0xDD, 255)
SS = 8  # сглаживание: рисуем крупно и уменьшаем


def wave_points(x0, y, segments, seg=16, amp=8, steps=24):
    """Точки волны «q8 -8 16 0 t16 0 ...» из макета (сетка 120x120)."""
    pts = []
    x = x0
    for i in range(segments):
        cy = y - amp if i % 2 == 0 else y + amp
        p0, p1, p2 = (x, y), (x + seg / 2, cy), (x + seg, y)
        for k in range(steps + (1 if i == segments - 1 else 0)):
            t = k / steps
            px = (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0]
            py = (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]
            pts.append((px, py))
        x += seg
    return pts


def draw(size: int) -> Image.Image:
    big = size * SS
    k = big / 120.0
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 0 if size >= 48 else 0
    d.rounded_rectangle([pad, pad, big - 1 - pad, big - 1 - pad], radius=int(30 * k), fill=ACCENT)

    width = 7.5 if size >= 96 else (9 if size >= 40 else (11 if size >= 24 else 12))
    lines = [(28, 48, 4, 255), (36, 64, 3, 205), (44, 80, 2, 155)]
    if size <= 20:
        lines = [(26, 46, 4, 255), (34, 66, 3, 215), (42, 86, 2, 170)]
    for x0, y, segs, alpha in lines:
        layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        ld = ImageDraw.Draw(layer)
        pts = [(px * k, py * k) for px, py in wave_points(x0, y, segs)]
        r = width * k / 2
        # круглое перо: «штампуем» кружки вдоль кривой - без зазубрин на стыках
        for (ax, ay), (bx, by) in zip(pts, pts[1:]):
            dist = max(1.0, ((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5)
            n = int(dist / max(1.0, r / 6)) + 1
            for i in range(n):
                t = i / n
                cx, cy = ax + (bx - ax) * t, ay + (by - ay) * t
                ld.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
        cx, cy = pts[-1]
        ld.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
        if alpha < 255:
            a = layer.getchannel("A").point(lambda v: v * alpha // 255)
            layer.putalpha(a)
        img = Image.alpha_composite(img, layer)
    return img.resize((size, size), Image.LANCZOS)


def bmp_entry(im: Image.Image) -> bytes:
    w, h = im.size
    px = im.convert("RGBA").tobytes()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[(y * w + x) * 4:(y * w + x) * 4 + 4]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    xor = b"".join(rows)
    mask_row = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(mask_row)
        for x in range(w):
            if px[(y * w + x) * 4 + 3] < 128:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return header + xor + bytes(mask)


def write_ico(path, frames):
    entries = []
    for im in frames:
        if im.size[0] >= 256:
            b = io.BytesIO()
            im.save(b, "PNG")
            data = b.getvalue()
        else:
            data = bmp_entry(im)
        entries.append((im.size[0], data))
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [draw(s) for s in sizes]
    assets = os.path.join(ROOT, "src", "Dymka", "Assets")
    os.makedirs(assets, exist_ok=True)
    write_ico(os.path.join(assets, "dymka.ico"), frames)
    frames[-1].save(os.path.join(assets, "dymka-256.png"))
    # лист предпросмотра всех размеров
    sheet = Image.new("RGBA", (sum(sizes) + 12 * len(sizes) + 12, 280), (247, 249, 252, 255))
    x = 12
    for s, im in zip(sizes, frames):
        sheet.alpha_composite(im, (x, 12))
        x += s + 12
    sheet.save(os.path.join(ROOT, "tools", "icon-preview.png"))
    print("ok")


if __name__ == "__main__":
    main()
