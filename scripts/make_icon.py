"""Render src/CodexCleaner/app.ico: the ChatGPT knot in black on a white rounded tile.

The knot path is the OpenAI glyph from Simple Icons (CC0, 24x24 grid); the mark itself is an
OpenAI trademark, see docs/NOTICE.md. Run: python scripts/make_icon.py
"""
import math
import os
import re
from PIL import Image, ImageChops, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "CodexCleaner", "app.ico")
KNOT = (
    "M22.282 9.821a6 6 0 0 0-.516-4.91a6.05 6.05 0 0 0-6.51-2.9A6.065 6.065 0 0 0 4.981 4.18a6 6 0 0 0-3.998 2.9"
    "a6.05 6.05 0 0 0 .743 7.097a5.98 5.98 0 0 0 .51 4.911a6.05 6.05 0 0 0 6.515 2.9A6 6 0 0 0 13.26 24"
    "a6.06 6.06 0 0 0 5.772-4.206a6 6 0 0 0 3.997-2.9a6.06 6.06 0 0 0-.747-7.073M13.26 22.43a4.48 4.48 0 0 1-2.876-1.04"
    "l.141-.081l4.779-2.758a.8.8 0 0 0 .392-.681v-6.737l2.02 1.168a.07.07 0 0 1 .038.052v5.583a4.504 4.504 0 0 1-4.494 4.494"
    "M3.6 18.304a4.47 4.47 0 0 1-.535-3.014l.142.085l4.783 2.759a.77.77 0 0 0 .78 0l5.843-3.369v2.332a.08.08 0 0 1-.033.062"
    "L9.74 19.95a4.5 4.5 0 0 1-6.14-1.646M2.34 7.896a4.5 4.5 0 0 1 2.366-1.973V11.6a.77.77 0 0 0 .388.677l5.815 3.354"
    "l-2.02 1.168a.08.08 0 0 1-.071 0l-4.83-2.786A4.504 4.504 0 0 1 2.34 7.872zm16.597 3.855l-5.833-3.387L15.119 7.2"
    "a.08.08 0 0 1 .071 0l4.83 2.791a4.494 4.494 0 0 1-.676 8.105v-5.678a.79.79 0 0 0-.407-.667m2.01-3.023l-.141-.085"
    "l-4.774-2.782a.78.78 0 0 0-.785 0L9.409 9.23V6.897a.07.07 0 0 1 .028-.061l4.83-2.787a4.5 4.5 0 0 1 6.68 4.66z"
    "m-12.64 4.135l-2.02-1.164a.08.08 0 0 1-.038-.057V6.075a4.5 4.5 0 0 1 7.375-3.453l-.142.08L8.704 5.46"
    "a.8.8 0 0 0-.393.681zm1.097-2.365l2.602-1.5l2.607 1.5v2.999l-2.597 1.5l-2.607-1.5Z"
)


def tokens(d):
    # SVG allows compact forms such as ".5.5" and arc flags written without separators.
    return re.findall(r"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?", d)


def arc(x1, y1, rx, ry, phi, large, sweep, x2, y2):
    # Endpoint-to-centre conversion from the SVG specification, appendix B.2.4.
    if rx == 0 or ry == 0:
        return [(x2, y2)]
    phi = math.radians(phi)
    cos, sin = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    xp, yp = cos * dx + sin * dy, -sin * dx + cos * dy
    rx, ry = abs(rx), abs(ry)
    scale = xp * xp / (rx * rx) + yp * yp / (ry * ry)
    if scale > 1:
        rx, ry = rx * math.sqrt(scale), ry * math.sqrt(scale)
    num = rx * rx * ry * ry - rx * rx * yp * yp - ry * ry * xp * xp
    coef = math.sqrt(max(0, num / (rx * rx * yp * yp + ry * ry * xp * xp))) * (-1 if large == sweep else 1)
    cxp, cyp = coef * rx * yp / ry, -coef * ry * xp / rx
    cx, cy = cos * cxp - sin * cyp + (x1 + x2) / 2, sin * cxp + cos * cyp + (y1 + y2) / 2

    def angle(ux, uy, vx, vy):
        return math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)

    start = angle(1, 0, (xp - cxp) / rx, (yp - cyp) / ry)
    delta = angle((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry)
    if not sweep and delta > 0:
        delta -= 2 * math.pi
    elif sweep and delta < 0:
        delta += 2 * math.pi
    steps = max(4, int(abs(delta) * 24))
    points = []
    for i in range(1, steps + 1):
        t = start + delta * i / steps
        x, y = rx * math.cos(t), ry * math.sin(t)
        points.append((cos * x - sin * y + cx, sin * x + cos * y + cy))
    return points


def subpaths(d):
    items, i = tokens(d), 0
    shapes, current = [], []
    x = y = sx = sy = 0.0
    command = None
    while i < len(items):
        if items[i].isalpha():
            command = items[i]
            i += 1
        rel = command.islower()
        c = command.upper()
        if c == "Z":
            if current:
                shapes.append(current)
            current, x, y = [], sx, sy
            continue
        take = {"M": 2, "L": 2, "H": 1, "V": 1, "A": 7}[c]
        args = [float(v) for v in items[i:i + take]]
        i += take
        if c == "M":
            if current:
                shapes.append(current)
            x, y = (x + args[0], y + args[1]) if rel else args
            sx, sy, current = x, y, [(x, y)]
            command = "l" if rel else "L"
        elif c == "L":
            x, y = (x + args[0], y + args[1]) if rel else args
            current.append((x, y))
        elif c == "H":
            x = x + args[0] if rel else args[0]
            current.append((x, y))
        elif c == "V":
            y = y + args[0] if rel else args[0]
            current.append((x, y))
        else:
            ex, ey = (x + args[5], y + args[6]) if rel else (args[5], args[6])
            current.extend(arc(x, y, args[0], args[1], args[2], int(args[3]), int(args[4]), ex, ey))
            x, y = ex, ey
    if current:
        shapes.append(current)
    return shapes


SHAPES = subpaths(KNOT)


def render(size):
    scale = 8
    s = size * scale
    tile = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(tile)
    pad = round(s * 0.03)
    d.rounded_rectangle([pad, pad, s - pad - 1, s - pad - 1], radius=round(s * 0.23), fill=(255, 255, 255, 255),
                        outline=(214, 214, 214, 255), width=max(scale, round(s * 0.012)))
    # Each ring is drawn into its own mask and combined with XOR, the even-odd rule of the glyph.
    mask = Image.new("L", (s, s), 0)
    inset, span = s * 0.19, s * 0.62 / 24
    for shape in SHAPES:
        ring = Image.new("L", (s, s), 0)
        ImageDraw.Draw(ring).polygon([(inset + px * span, inset + py * span) for px, py in shape], fill=255)
        mask = ImageChops.logical_xor(mask.convert("1"), ring.convert("1")).convert("L")
    tile.paste((13, 13, 13, 255), (0, 0), mask)
    return tile.resize((size, size), Image.LANCZOS)


images = [render(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
print("wrote", os.path.abspath(OUT))
