"""FINAL rest-site cleanse — a big stone purification altar radiating cleansing light (sunburst bg,
glowing rune + light pooling on the altar top). No relic. Frame = sister mod (Transmute) panel."""
import sys, math, numpy as np
sys.path.insert(0, r"C:/Users/kl95/AppData/Local/Temp/claude/C--Users-kl95-sts2-card-advisor-dev/7832a4a3-cdfe-4618-ad67-947db108d2b2/scratchpad")
from PIL import Image, ImageDraw, ImageFilter
from altar_common import draw_altar, cyan_glow

SRC = "../Sts2RelicTransmute/pck_src/images/ui/rest_site/option_transmute.png"
OUT = "pck_src/images/ui/rest_site/option_cleanse.png"

def sunburst(W, H, cx, cy, col=(150,232,255), rmax=158, n=24):
    lay = Image.new("RGBA", (W, H), (0,0,0,0)); d = ImageDraw.Draw(lay)
    for i in range(n):
        a = (i/n)*2*math.pi + 0.08; ln = rmax if i%2==0 else rmax*0.6; hw = 9 if i%2==0 else 4
        tip = (cx+math.cos(a)*ln, cy+math.sin(a)*ln)
        pa = (cx+math.cos(a+math.pi/2)*hw, cy+math.sin(a+math.pi/2)*hw)
        pb = (cx+math.cos(a-math.pi/2)*hw, cy+math.sin(a-math.pi/2)*hw)
        d.polygon([pa, tip, pb], fill=col+(90,))
    return lay.filter(ImageFilter.GaussianBlur(4))

def sparkles(W, H, pts):
    l = Image.new("RGBA", (W, H), (0,0,0,0)); d = ImageDraw.Draw(l)
    for (x,y,r) in pts:
        d.polygon([(x,y-r),(x+r*0.2,y-r*0.2),(x+r,y),(x+r*0.2,y+r*0.2),(x,y+r),(x-r*0.2,y+r*0.2),(x-r,y),(x-r*0.2,y-r*0.2)], fill=(240,253,255,255))
    return l

frame = Image.open(SRC).convert("RGBA"); W, H = frame.size; CX = W/2
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
dd = np.clip(np.sqrt(((xx-CX)/(W*0.55))**2 + ((yy-H*0.42)/(H*0.72))**2), 0, 1)
core = np.array([210,244,255],float); mid = np.array([32,132,158],float); edge = np.array([9,26,40],float); t = dd[...,None]
rgb = np.where(t<0.42, core+(mid-core)*(t/0.42), mid+(edge-mid)*((t-0.42)/0.58))
motif = Image.fromarray(np.concatenate([rgb, np.full((H,W,1),255,float)],2).astype(np.uint8), "RGBA")

S = 62; cy = int(H*0.56)
alt, surf = draw_altar(W, H, CX, cy, S, rune=True)
BC = (surf[0], surf[1]-6)
motif = Image.alpha_composite(motif, sunburst(W, H, BC[0], BC[1], rmax=158, n=24))
motif = Image.alpha_composite(motif, cyan_glow(W, H, surf[0], surf[1]-10, int(0.85*S), 22, 175, 12))
motif = Image.alpha_composite(motif, alt)
motif = Image.alpha_composite(motif, cyan_glow(W, H, surf[0], surf[1]-4, int(0.5*S), 12, 150, 5))
motif = Image.alpha_composite(motif, sparkles(W, H, [(surf[0]-14, surf[1]-24, 5), (surf[0]+16, surf[1]-30, 6), (surf[0], surf[1]-42, 6)]))

mask = Image.new("L", (W, H), 0)
ImageDraw.Draw(mask).rounded_rectangle([6, 6, W-7, H-7], radius=9, fill=255)
mask = mask.filter(ImageFilter.GaussianBlur(3))
out = frame.copy(); out.paste(motif, (0, 0), mask); out.save(OUT)
print("wrote", OUT)
