"""Compose the printable invitation card (5 x 7 in at 300 dpi) around the block QR,
then wrap it as a single-card PDF and a two-up US Letter sheet."""
import random
import sys

from PIL import Image, ImageDraw, ImageFont
from reportlab.lib.pagesizes import letter
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas

QR_PATH, OUT_DIR, FONT_DIR = sys.argv[1], sys.argv[2], sys.argv[3]
URL = sys.argv[4]
NAME = sys.argv[5] if len(sys.argv) > 5 else "[Child's name]"
DATE = sys.argv[6] if len(sys.argv) > 6 else "[Saturday, Month 00, 2026]"
WHERE = sys.argv[7] if len(sys.argv) > 7 else "[Venue]"
RSVP_BY = sys.argv[8] if len(sys.argv) > 8 else "[Month 00]"

DPI = 300
W, H = 5 * DPI, 7 * DPI
rng = random.Random(7)

SKY = (134, 192, 255)
SKY2 = (158, 208, 255)
CLOUD = (255, 255, 255)
GOLD = (242, 193, 78)
GRASS = (95, 191, 69)
GRASS_D = (61, 143, 44)
DIRT = (140, 90, 43)
DIRT_D = (95, 59, 26)
STONE_D = (74, 74, 74)
PLANK = (193, 144, 79)
PLANK_D = (138, 95, 44)
PLANK_L = (220, 176, 116)
PAPER = (246, 239, 220)
INK = (30, 26, 18)
INK_SOFT = (90, 74, 51)
RED = (216, 57, 42)
CREEPER = (46, 201, 59)
CREEPER_D = (19, 140, 32)

display = lambda s: ImageFont.truetype(f"{FONT_DIR}/PressStart2P.ttf", s)
body = lambda s: ImageFont.truetype(f"{FONT_DIR}/PixelifySans.ttf", s)

img = Image.new("RGB", (W, H), DIRT)
d = ImageDraw.Draw(img)


def rect(x, y, w, h, col):
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=col)


def shadow_text(x, y, text, font, fill, shadow, off, anchor="la"):
    d.text((x + off, y + off), text, font=font, fill=shadow, anchor=anchor)
    d.text((x, y), text, font=font, fill=fill, anchor=anchor)


# ---- sky ----
SKY_H = 640
rect(0, 0, W, SKY_H, SKY)
rect(0, int(SKY_H * 0.62), W, SKY_H - int(SKY_H * 0.62), SKY2)
# sun
px = 22
sx, sy = W - 300, 90
for dx, dy in [(0, 0), (-1, 0), (1, 0), (0, -1), (0, 1)]:
    rect(sx + dx * px * 2, sy + dy * px * 2, px * 2, px * 2, GOLD)
# clouds
for cx, cy, scale in [(120, 150, 1.0), (640, 70, 1.3), (1100, 330, 0.9), (300, 420, 0.8)]:
    u = int(24 * scale)
    for dx, dy in [(0, 0), (1, 0), (2, 0), (3, 0), (1, -1), (2, -1), (-1, 0)]:
        rect(cx + dx * u, cy + dy * u, u, u, CLOUD)

# creeper face (8x8) on the left of the title block
cp = 18
face = [
    "........",
    "........",
    ".XX..XX.",
    ".XX..XX.",
    "...XX...",
    "..XXXX..",
    "..XXXX..",
    "..X..X..",
]
fx, fy = 110, 215
rect(fx + 10, fy + 10, 8 * cp, 8 * cp, (0, 0, 0))
for r, row in enumerate(face):
    for c, ch in enumerate(row):
        rect(fx + c * cp, fy + r * cp, cp, cp, CREEPER_D if ch == "X" else CREEPER)

# eyebrow tag
tag_font = display(22)
tag = "YOU'RE INVITED"
tw = d.textlength(tag, font=tag_font)
tx, ty = 300, 150
rect(tx + 8, ty + 8, int(tw) + 40, 60, STONE_D)
rect(tx, ty, int(tw) + 40, 60, GOLD)
d.text((tx + 20, ty + 19), tag, font=tag_font, fill=STONE_D)

# title
name_font = display(44 if len(NAME) <= 14 else 34)
shadow_text(300, 250, NAME.upper(), name_font, (255, 255, 255), STONE_D, 7)
line_font = display(40)
shadow_text(300, 340, "IS TURNING", line_font, (255, 255, 255), STONE_D, 7)
nine_font = display(120)
shadow_text(300 + d.textlength("IS TURNING ", font=line_font) + 10, 300, "9", nine_font, GOLD, STONE_D, 9)
sub_font = body(46)
shadow_text(300, 470, "Block party. Pizza. Cake. Bring your player.", sub_font, (255, 255, 255), STONE_D, 4)

# ---- grass line ----
gy = SKY_H
rect(0, gy, W, 16, GRASS)
x = 0
while x < W:
    w = rng.choice([24, 36, 48])
    rect(x, gy + 16, w, 26, rng.choice([GRASS, GRASS, GRASS_D]))
    x += w
rect(0, gy + 42, W, 10, GRASS_D)

# ---- dirt grid texture ----
for yy in range(gy + 52, H, 44):
    rect(0, yy, W, 3, (0, 0, 0))
for xx in range(0, W, 44):
    rect(xx, gy + 52, 3, H - gy - 52, (0, 0, 0))
# soften the grid lines by blending: redraw with translucent effect via a second pass
overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
od = ImageDraw.Draw(overlay)
for yy in range(gy + 52, H, 44):
    od.rectangle([0, yy, W, yy + 2], fill=(0, 0, 0, 0))
img = img.convert("RGBA")
# Replace hard black grid with a subtle darker-dirt grid
pix = img.load()
for yy in range(gy + 52, H, 44):
    for xx in range(W):
        for k in range(3):
            if yy + k < H:
                pix[xx, yy + k] = DIRT_D + (255,)
for xx in range(0, W, 44):
    for yy in range(gy + 52, H):
        for k in range(3):
            if xx + k < W:
                pix[xx + k, yy] = DIRT_D + (255,)
img = img.convert("RGB")
d = ImageDraw.Draw(img)

# ---- plank sign holding the QR ----
qr = Image.open(QR_PATH).convert("RGB")
QR_SIZE = 760
qr = qr.resize((QR_SIZE, QR_SIZE), Image.NEAREST)
PAD = 36
sign_w = QR_SIZE + PAD * 2
sign_h = QR_SIZE + PAD * 2 + 130
sx = (W - sign_w) // 2
sy = gy + 80
rect(sx + 16, sy + 16, sign_w, sign_h, (0, 0, 0))
rect(sx, sy, sign_w, sign_h, PLANK_D)
rect(sx + 12, sy + 12, sign_w - 24, sign_h - 24, PLANK)
rect(sx + 12, sy + 12, sign_w - 24, 10, PLANK_L)
rect(sx + 12, sy + 12, 10, sign_h - 24, PLANK_L)
for yy in range(sy + 60, sy + sign_h - 12, 64):
    rect(sx + 22, yy, sign_w - 44, 5, PLANK_D)
# caption inside sign
cap_font = display(26)
shadow_text(W // 2, sy + 52, "SCAN TO RSVP", cap_font, PAPER, PLANK_D, 5, anchor="mm")
cap2 = body(34)
shadow_text(W // 2, sy + 96, "Point your phone camera at the code", cap2, PAPER, PLANK_D, 3, anchor="mm")
img.paste(qr, (sx + PAD, sy + 130 + PAD - 16))
d = ImageDraw.Draw(img)

# ---- info slots under the sign ----
slot_y = sy + sign_h + 40
slot_font_k = display(18)
slot_font_v = body(40)


def slot(y, k, v):
    h = 72
    rect(60 + 8, y + 8, W - 120, h, (0, 0, 0))
    rect(60, y, W - 120, h, STONE_D)
    rect(60, y, 300, h, STONE_D)
    rect(360, y + 8, W - 120 - 300 - 8, h - 16, PAPER)
    d.text((90, y + h // 2), k, font=slot_font_k, fill=GOLD, anchor="lm")
    d.text((390, y + h // 2), v, font=slot_font_v, fill=INK, anchor="lm")
    return y + h + 14


y = slot(slot_y, "WHEN", DATE)
y = slot(y, "WHERE", WHERE)
y = slot(y, "RSVP BY", RSVP_BY)

# fallback URL and bedrock footer
url_font = body(30)
d.text((W // 2, H - 72), "Can't scan?  " + URL.replace("https://", ""), font=body(30), fill=(255, 255, 255), anchor="mm")
rect(0, H - 40, W, 40, STONE_D)
for xx in range(0, W, 44):
    rect(xx, H - 40, 3, 40, (30, 30, 30))

card_png = f"{OUT_DIR}/invitation-card-5x7.png"
img.save(card_png, dpi=(DPI, DPI))

# ---- PDFs ----
c = canvas.Canvas(f"{OUT_DIR}/invitation-card-5x7.pdf", pagesize=(5 * inch, 7 * inch))
c.setTitle("Block Party Invitation 5x7")
c.drawImage(card_png, 0, 0, 5 * inch, 7 * inch)
c.showPage()
c.save()

# Two cards per US Letter sheet, rotated so two 7 x 5 cards stack with cut marks.
c = canvas.Canvas(f"{OUT_DIR}/invitation-cards-letter-2up.pdf", pagesize=letter)
c.setTitle("Block Party Invitations, two per sheet")
pw, ph = letter
margin_x = (pw - 7 * inch) / 2
gap = 0.25 * inch
top_y = (ph + 2 * 5 * inch + gap) / 2
for i in range(2):
    y_top = top_y - i * (5 * inch + gap)
    c.saveState()
    # rotate the 5x7 portrait card 90 degrees into a 7x5 landscape slot
    c.translate(margin_x, y_top - 5 * inch)
    c.translate(7 * inch, 0)
    c.rotate(90)
    c.drawImage(card_png, 0, 0, 5 * inch, 7 * inch)
    c.restoreState()
    # cut marks
    c.setStrokeColorRGB(0.5, 0.5, 0.5)
    c.setLineWidth(0.5)
    for (mx, my) in [(margin_x, y_top), (margin_x + 7 * inch, y_top), (margin_x, y_top - 5 * inch), (margin_x + 7 * inch, y_top - 5 * inch)]:
        c.line(mx - 18, my, mx - 6, my)
        c.line(mx + 6, my, mx + 18, my) if False else None
        c.line(mx, my - 18, mx, my - 6)
        c.line(mx, my + 6, mx, my + 18)
        c.line(mx + 6, my, mx + 18, my)
c.showPage()
c.save()
print("card ok")
