"""Render src/CodexCleaner/app.ico: a white chat bubble with an erase stroke on a black tile.

Monochrome like the ChatGPT / Codex apps, but drawn from simple shapes; it does not use the
OpenAI mark. Run: python scripts/make_icon.py
"""
import os
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "CodexCleaner", "app.ico")
INK = (13, 13, 13, 255)
PAPER = (255, 255, 255, 255)


def render(size):
    scale = 8
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = round(s * 0.03)
    d.rounded_rectangle([pad, pad, s - pad - 1, s - pad - 1], radius=round(s * 0.23), fill=INK)

    # Chat bubble with a tail at the lower left; small sizes get a thicker, simpler bubble.
    small = size <= 24
    left, top, right, bottom = s * 0.20, s * 0.22, s * 0.80, s * (0.68 if small else 0.66)
    d.rounded_rectangle([left, top, right, bottom], radius=round(s * (0.16 if small else 0.15)), fill=PAPER)
    d.polygon([(s * 0.29, bottom - s * 0.04), (s * 0.26, s * 0.81), (s * 0.45, bottom - s * 0.02)], fill=PAPER)

    # Diagonal erase stroke with rounded ends across the bubble.
    width = s * (0.11 if small else 0.085)
    a, b = (s * 0.37, s * 0.55 if small else s * 0.54), (s * 0.63, s * 0.35 if small else s * 0.34)
    d.line([a, b], fill=INK, width=round(width))
    for x, y in (a, b):
        r = width / 2
        d.ellipse([x - r, y - r, x + r, y + r], fill=INK)
    return img.resize((size, size), Image.LANCZOS)


images = [render(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
print("wrote", os.path.abspath(OUT))
