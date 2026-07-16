"""A stone purification altar, painted Slay-the-Spire style (bold dark outline + painterly stone
shading + glowing cyan runes). The relic rests on the top slab."""
import math, numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT  = (32, 26, 34)          # warm-dark contour (STS uses a dark brown outline, not pure black)
LI   = (162, 164, 178)       # lit stone
DK   = (66, 68, 86)          # shadow stone
HI   = (198, 200, 214)       # top-edge highlight
SEAM = (38, 34, 46)          # crevice shadow
RUNE = (135, 238, 255)


def draw_altar(W, H, cx, cy, s, rune=True):
    """A stepped stone altar centred at (cx,cy), scale s. Returns (layer, surface_xy).
    rune=False omits the glowing cyan rune panel (used by the plain coin emblem)."""
    yy = np.mgrid[0:H, 0:W][0].astype(np.float32)

    # geometry: (top_hw, y_top, bot_hw, y_bot) bottom step -> top slab
    blocks = [
        (1.08*s, cy+0.50*s, 1.22*s, cy+0.70*s),   # base step 1 (widest)
        (0.90*s, cy+0.32*s, 1.02*s, cy+0.50*s),   # base step 2
        (0.54*s, cy-0.26*s, 0.60*s, cy+0.32*s),   # column
        (0.96*s, cy-0.58*s, 0.88*s, cy-0.26*s),   # capital / top slab (overhangs)
    ]
    polys = [[(cx-t, y0), (cx+t, y0), (cx+b, y1), (cx-b, y1)] for (t, y0, b, y1) in blocks]

    # --- outer contour: union silhouette, dilated, dark
    union = Image.new("L", (W, H), 0)
    ud = ImageDraw.Draw(union)
    for p in polys:
        ud.polygon(p, fill=255)
    outline_mask = union.filter(ImageFilter.MaxFilter(7))
    lay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    solid = Image.new("RGBA", (W, H), OUT + (255,))
    lay = Image.composite(solid, lay, outline_mask)

    # --- painterly stone fill per block (vertical light->shadow gradient)
    for (t, y0, b, y1), p in zip(blocks, polys):
        m = Image.new("L", (W, H), 0); ImageDraw.Draw(m).polygon(p, fill=255)
        f = np.clip((yy - y0) / max(1.0, (y1 - y0)), 0, 1)[..., None]
        rgb = np.array(LI, float) + (np.array(DK, float) - np.array(LI, float)) * f
        bl = Image.fromarray(np.concatenate([rgb, np.full((H, W, 1), 255.0)], 2).astype(np.uint8), "RGBA")
        bl.putalpha(m)
        lay = Image.alpha_composite(lay, bl)

    d = ImageDraw.Draw(lay)
    # --- seams (dark) under each block's front edge + top-edge highlight
    for (t, y0, b, y1), p in zip(blocks, polys):
        d.line([p[3], p[2]], fill=SEAM, width=3)           # bottom crevice
        d.line([p[0], p[1]], fill=HI, width=2)             # lit top lip
        d.line([p[0], p[3]], fill=(90, 92, 108), width=1)  # left edge soft
    # a couple of carved cracks on the column for texture
    ccx, ctop, cbot = cx, cy-0.20*s, cy+0.26*s
    d.line([(ccx-0.28*s, ctop+0.1*s), (ccx-0.22*s, cbot-0.1*s)], fill=SEAM, width=1)
    d.line([(ccx+0.24*s, ctop), (ccx+0.30*s, cbot)], fill=SEAM, width=1)

    # --- glowing cyan rune panel carved into the column (optional)
    if rune:
        glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        gd = ImageDraw.Draw(glow)
        ry = cy + 0.03*s
        gd.line([(cx-0.34*s, ry), (cx+0.34*s, ry)], fill=RUNE+(255,), width=2)
        for k in (-0.5, 0.0, 0.5):
            gd.line([(cx+k*0.34*s, ry-0.12*s), (cx+k*0.34*s, ry+0.12*s)], fill=RUNE+(255,), width=2)
        glow = glow.filter(ImageFilter.GaussianBlur(1.3))
        lay = Image.alpha_composite(lay, glow)

    # --- top slab surface (relic rests here): lit stone ellipse + cyan sheen, dark rim
    d = ImageDraw.Draw(lay)
    sy = cy - 0.58*s; shw = 0.96*s; sry = 0.15*s
    d.ellipse([cx-shw, sy-sry, cx+shw, sy+sry], fill=OUT)                       # dark rim
    d.ellipse([cx-shw+2, sy-sry+2, cx+shw-2, sy+sry-2], fill=(176, 180, 194))   # stone top
    d.ellipse([cx-shw*0.7, sy-sry*0.6, cx+shw*0.7, sy+sry*0.6], fill=(150, 214, 234))  # cyan sheen
    return lay, (cx, sy - sry*0.2)


def light_beam(W, H, cx, top_y, bot_y, hw_top, hw_bot, a=95):
    lay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(lay).polygon([(cx-hw_top, top_y), (cx+hw_top, top_y),
                                 (cx+hw_bot, bot_y), (cx-hw_bot, bot_y)], fill=(162, 238, 255, a))
    return lay.filter(ImageFilter.GaussianBlur(7))


def cyan_glow(W, H, cx, cy, rx, ry, a=130, blur=15):
    g = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(g).ellipse([cx-rx, cy-ry, cx+rx, cy+ry], fill=(150, 232, 255, a))
    return g.filter(ImageFilter.GaussianBlur(blur))
