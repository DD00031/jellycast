"""Generates the Jellycast plugin catalog icon (512x512 PNG)."""

from PIL import Image, ImageDraw

SIZE = 512
BG_TOP = (0, 164, 220)      # Jellyfin brand blue
BG_BOTTOM = (0, 120, 170)   # deeper blue for subtle depth
WHITE = (255, 255, 255, 255)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

# Rounded-square background with a vertical gradient.
radius = 96
mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=radius, fill=255)

gradient = Image.new("RGBA", (SIZE, SIZE))
gdraw = ImageDraw.Draw(gradient)
for y in range(SIZE):
    t = y / (SIZE - 1)
    r = int(BG_TOP[0] + (BG_BOTTOM[0] - BG_TOP[0]) * t)
    g = int(BG_TOP[1] + (BG_BOTTOM[1] - BG_TOP[1]) * t)
    b = int(BG_TOP[2] + (BG_BOTTOM[2] - BG_TOP[2]) * t)
    gdraw.line([(0, y), (SIZE, y)], fill=(r, g, b, 255))

img.paste(gradient, (0, 0), mask)
draw = ImageDraw.Draw(img)

# Glyph bounding box.
margin = 118
glyph_left = margin
glyph_top = margin
glyph_right = SIZE - margin
glyph_bottom = SIZE - margin
glyph_w = glyph_right - glyph_left
glyph_h = glyph_bottom - glyph_top

# Screen: a wide rounded rectangle across the top of the glyph area.
screen_left = glyph_left
screen_right = glyph_right
screen_top = glyph_top
screen_h = glyph_h * 0.56
screen_bottom = screen_top + screen_h
stroke = 20
draw.rounded_rectangle(
    [screen_left, screen_top, screen_right, screen_bottom],
    radius=18,
    outline=WHITE,
    width=stroke,
)
# play triangle centered on the screen
pcx = (screen_left + screen_right) / 2
pcy = (screen_top + screen_bottom) / 2
tri = 30
draw.polygon(
    [
        (pcx - tri * 0.6, pcy - tri),
        (pcx - tri * 0.6, pcy + tri),
        (pcx + tri * 0.9, pcy),
    ],
    fill=WHITE,
)

# Signal arcs + base dot, confined to the band below the screen, bottom-left origin.
gap = glyph_h * 0.14
origin_x = glyph_left
origin_y = glyph_bottom

base_r = 15
draw.ellipse(
    [origin_x - base_r, origin_y - base_r, origin_x + base_r, origin_y + base_r],
    fill=WHITE,
)

max_r = glyph_bottom - screen_bottom - gap * 0.2
radii = [max_r * 0.42, max_r * 0.68, max_r * 0.95]
arc_width = 17
for r in radii:
    bbox = [origin_x - r, origin_y - r, origin_x + r, origin_y + r]
    draw.arc(bbox, start=270, end=360, fill=WHITE, width=arc_width)

img.save("/Users/rezavanderkleij/Developer/Projects/jellycast/.github/assets/jellycast-icon.png")
print("saved")
