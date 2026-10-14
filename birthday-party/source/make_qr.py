"""Render a QR code as a Minecraft-style block world.

Dark modules become textured blocks (grass-topped dirt, dirt, stone, ores,
obsidian for the finder patterns); light modules become sky with the odd
cloud pixel. Every dark texel stays well below mid-luminance and every light
texel well above it so phone cameras still read it.
"""
import random
import sys

import qrcode
from PIL import Image, ImageDraw

URL = sys.argv[1]
OUT = sys.argv[2]
SEED = int(sys.argv[3]) if len(sys.argv) > 3 else 9

S = 48          # pixels per module
T = 4           # texels per module side
TX = S // T     # pixels per texel
BORDER = 4      # quiet zone in modules

rng = random.Random(SEED)

# --- palettes (R,G,B); dark ones all have luminance < 0.30, light > 0.80 ---
GRASS = [(40, 100, 30), (44, 108, 32), (36, 92, 26), (42, 104, 30)]
DIRT = [(95, 59, 26), (112, 70, 31), (74, 45, 18), (104, 64, 28), (86, 53, 22)]
STONE = [(74, 74, 74), (66, 66, 66), (58, 58, 58), (70, 70, 70), (62, 62, 62)]
OBSIDIAN = [(27, 20, 36), (42, 31, 58), (20, 14, 28), (36, 26, 50)]
OAK = [(78, 56, 30), (92, 66, 36), (64, 46, 24)]
DIAMOND = (24, 112, 128)
REDSTONE = (160, 36, 26)
GOLD = (150, 108, 24)
SKY = [(214, 236, 255), (200, 228, 255), (224, 240, 255), (208, 232, 255)]
CLOUD = (255, 255, 255)


def lum(c):
    r, g, b = [v / 255 for v in c]
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


for pal in (DIRT, STONE, OBSIDIAN, OAK):
    for c in pal:
        assert lum(c) < 0.30, c
for c in GRASS:
    assert lum(c) < 0.38, c  # only the top texel row of a grass block
for c in (DIAMOND, REDSTONE, GOLD):
    assert lum(c) < 0.45, c  # ore specks are 2 of 16 texels
for c in SKY:
    assert lum(c) > 0.80, c


qr = qrcode.QRCode(
    version=None,
    error_correction=qrcode.constants.ERROR_CORRECT_H,
    box_size=1,
    border=BORDER,
)
qr.add_data(URL)
qr.make(fit=True)
m = qr.get_matrix()  # list of rows of bool, border included
N = len(m)
W = N * S
img = Image.new("RGB", (W, W), SKY[0])
d = ImageDraw.Draw(img)

inner = N - 2 * BORDER  # module count of the symbol itself


def is_finder(r, c):
    """Finder patterns live in the three corners of the symbol (7x7 + 1 separator)."""
    r -= BORDER
    c -= BORDER
    if r < 0 or c < 0 or r >= inner or c >= inner:
        return False
    return (r < 8 and c < 8) or (r < 8 and c >= inner - 8) or (r >= inner - 8 and c < 8)


def dark_at(r, c):
    return 0 <= r < N and 0 <= c < N and m[r][c]


def texel(x, y, col):
    d.rectangle([x * TX, y * TX, (x + 1) * TX - 1, (y + 1) * TX - 1], fill=col)


def shade(col, k):
    return tuple(max(0, min(255, int(v * k))) for v in col)


def draw_block(r, c, kind):
    x0, y0 = c * T, r * T
    for ty in range(T):
        for tx in range(T):
            if kind == "grass":
                col = rng.choice(GRASS) if ty == 0 else rng.choice(DIRT)
            elif kind == "dirt":
                col = rng.choice(DIRT)
            elif kind == "stone":
                col = rng.choice(STONE)
            elif kind == "obsidian":
                col = rng.choice(OBSIDIAN)
            elif kind == "oak":
                col = rng.choice(OAK)
            elif kind in ("diamond", "redstone", "gold"):
                col = rng.choice(STONE)
            # light edge top-left, dark edge bottom-right: a block bevel
            if tx == 0 or ty == 0:
                col = shade(col, 1.18)
            elif tx == T - 1 or ty == T - 1:
                col = shade(col, 0.80)
            texel(x0 + tx, y0 + ty, col)
    if kind in ("diamond", "redstone", "gold"):
        ore = {"diamond": DIAMOND, "redstone": REDSTONE, "gold": GOLD}[kind]
        for tx, ty in rng.sample([(1, 1), (2, 1), (1, 2), (2, 2)], 2):
            texel(x0 + tx, y0 + ty, ore)


def draw_sky(r, c):
    x0, y0 = c * T, r * T
    base = rng.choice(SKY)
    for ty in range(T):
        for tx in range(T):
            col = base if rng.random() > 0.08 else rng.choice(SKY)
            texel(x0 + tx, y0 + ty, col)
    # the occasional cloud pixel in the quiet zone only, never inside the symbol
    if (r < BORDER or r >= N - BORDER or c < BORDER or c >= N - BORDER) and rng.random() < 0.05:
        tx, ty = rng.randrange(T), rng.randrange(T)
        texel(x0 + tx, y0 + ty, CLOUD)


for r in range(N):
    for c in range(N):
        if not m[r][c]:
            draw_sky(r, c)
            continue
        if is_finder(r, c):
            kind = "obsidian"
        elif not dark_at(r - 1, c):
            kind = "grass"        # exposed top: grass block
        else:
            roll = rng.random()
            depth = r / N
            if roll < 0.06:
                kind = "diamond" if depth > 0.5 else "redstone"
            elif roll < 0.10:
                kind = "gold"
            elif roll < 0.16:
                kind = "oak"
            elif roll < 0.45 + 0.4 * depth:
                kind = "stone"    # more stone deeper down
            else:
                kind = "dirt"
        draw_block(r, c, kind)

img.save(OUT)
print(f"modules={inner} total={N} size={W}px version={qr.version} url={URL}")
