#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Generate WordLens app icon (multi-size .ico) with Pillow."""
import os
from PIL import Image, ImageDraw, ImageFont

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "WordLens", "Assets")
os.makedirs(OUT_DIR, exist_ok=True)
SIZE = 1024


def font(size, bold=True):
    candidates = [
        r"C:\Windows\Fonts\georgiab.ttf" if bold else r"C:\Windows\Fonts\georgia.ttf",
        r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf",
    ]
    for c in candidates:
        if os.path.exists(c):
            return ImageFont.truetype(c, size)
    return ImageFont.load_default()


def make():
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # rounded square background with vertical gradient
    radius = 210
    bg = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bg)
    top = (30, 41, 59)      # slate-800
    bottom = (12, 17, 29)   # near-black
    for y in range(SIZE):
        t = y / (SIZE - 1)
        c = tuple(int(top[i] * (1 - t) + bottom[i] * t) for i in range(3)) + (255,)
        bd.line([(0, y), (SIZE, y)], fill=c)
    mask = Image.new("L", (SIZE, SIZE), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=radius, fill=255)
    img.paste(bg, (0, 0), mask)

    # subtle inner highlight ring
    d.rounded_rectangle([10, 10, SIZE - 11, SIZE - 11], radius=radius - 10,
                        outline=(255, 255, 255, 26), width=6)

    # letter W
    f = font(620, True)
    text = "W"
    box = d.textbbox((0, 0), text, font=f)
    tw, th = box[2] - box[0], box[3] - box[1]
    x = (SIZE - tw) / 2 - box[0]
    y = (SIZE - th) / 2 - box[1] - 70
    d.text((x, y), text, font=f, fill=(248, 250, 252, 255))

    # amber accent bar under the W
    bar_w, bar_h = 300, 26
    bx = (SIZE - bar_w) / 2
    by = SIZE - 250
    d.rounded_rectangle([bx, by, bx + bar_w, by + bar_h], radius=bar_h // 2,
                        fill=(245, 158, 11, 255))

    # small dictionary "pages" hint: two dots
    d.ellipse([SIZE/2 - 120, by + 100, SIZE/2 - 60, by + 160], fill=(148, 163, 184, 160))
    d.ellipse([SIZE/2 + 60, by + 100, SIZE/2 + 120, by + 160], fill=(148, 163, 184, 160))
    return img


png = os.path.join(OUT_DIR, "wordlens-1024.png")
ico = os.path.join(OUT_DIR, "wordlens.ico")
img = make()
img.save(png)
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save(ico, format="ICO", sizes=sizes)
print("wrote", png, os.path.getsize(png))
print("wrote", ico, os.path.getsize(ico))
