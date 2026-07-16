"""FINAL shop cleanse coin — a centred stone purification altar (no beam, no rune, no orb)."""
import sys, numpy as np
sys.path.insert(0, r"C:/Users/kl95/AppData/Local/Temp/claude/C--Users-kl95-sts2-card-advisor-dev/7832a4a3-cdfe-4618-ad67-947db108d2b2/scratchpad")
from PIL import Image
from altar_common import draw_altar, cyan_glow

SC = r"C:/Users/kl95/AppData/Local/Temp/claude/C--Users-kl95-sts2-card-advisor-dev/7832a4a3-cdfe-4618-ad67-947db108d2b2/scratchpad"
coin = Image.open(SC + "/cleanse_shop_icon.ORIG.png").convert("RGBA")
W, H = coin.size; CX, CY = W/2, H/2

# erase old emblem: repaint the cyan face
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
r = np.sqrt((xx-CX)**2 + (yy-CY)**2); radial = np.clip(r/90.0, 0, 1)[..., None]
base = np.array([122,232,248],float) + (np.array([84,190,205],float) - np.array([122,232,248],float))*radial
a = np.clip((90.0-r)/5.0, 0, 1)
coin = Image.alpha_composite(coin, Image.fromarray(np.concatenate([base,(a*255)[...,None]],2).astype(np.uint8), "RGBA"))

coin = Image.alpha_composite(coin, cyan_glow(W, H, CX, CY, 54, 44, 90, 16))
S = 62
alt, _ = draw_altar(W, H, CX, int(CY - 0.06*S), S, rune=False)
coin = Image.alpha_composite(coin, alt)
coin.save("cleanse_shop_icon.png")
print("wrote cleanse_shop_icon.png")
