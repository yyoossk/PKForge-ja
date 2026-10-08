from PIL import Image, ImageDraw, ImageFilter, ImageFont
import sys
S=1024
def make(size, bg=True):
    k=4; W=size*k
    im=Image.new('RGBA',(W,W),(0,0,0,0))
    d=ImageDraw.Draw(im)
    u=W/100
    if bg:
        d.rounded_rectangle([0,0,W-1,W-1], radius=22*u, fill=(14,11,12,255))
    # body panel
    d.rounded_rectangle([12*u,12*u,88*u,88*u], radius=12*u, fill=(38,27,30,255), outline=(179,18,46,255), width=int(2.2*u))
    # red header band with angled cut
    hdr=Image.new('RGBA',(W,W),(0,0,0,0)); hd=ImageDraw.Draw(hdr)
    hd.polygon([(0,0),(W,0),(W,36*u),(60*u,36*u),(52*u,44*u),(0,44*u)], fill=(227,38,58,255))
    mask=Image.new('L',(W,W),0); ImageDraw.Draw(mask).rounded_rectangle([12*u,12*u,88*u,88*u], radius=12*u, fill=255)
    im.paste(hdr,(0,0),Image.composite(hdr,Image.new('RGBA',(W,W)),mask).split()[3])
    d.rounded_rectangle([12*u,12*u,88*u,88*u], radius=12*u, outline=(179,18,46,255), width=int(2.2*u))
    # glowing lens
    glow=Image.new('RGBA',(W,W),(0,0,0,0)); g=ImageDraw.Draw(glow)
    cx,cy,r=31*u,30*u,13*u
    g.ellipse([cx-r*1.5,cy-r*1.5,cx+r*1.5,cy+r*1.5], fill=(79,210,255,140))
    glow=glow.filter(ImageFilter.GaussianBlur(5*u))
    im.alpha_composite(glow)
    d=ImageDraw.Draw(im)
    d.ellipse([cx-r-2.2*u,cy-r-2.2*u,cx+r+2.2*u,cy+r+2.2*u], fill=(247,243,243,255))
    d.ellipse([cx-r,cy-r,cx+r,cy+r], fill=(30,140,210,255))
    d.ellipse([cx-r*0.8,cy-r*0.8,cx+r*0.6,cy+r*0.6], fill=(79,210,255,255))
    d.ellipse([cx-r*0.55,cy-r*0.6,cx-r*0.1,cy-r*0.15], fill=(235,250,255,255))
    # LEDs
    for i,c in enumerate([(255,77,94),(255,210,63),(84,214,138)]):
        x=55*u+i*8*u; y=22*u
        d.ellipse([x-2.6*u,y-2.6*u,x+2.6*u,y+2.6*u], fill=c, outline=(14,11,12,255), width=int(0.8*u))
    # screen with bars (data/stats look)
    d.rounded_rectangle([20*u,52*u,80*u,80*u], radius=4*u, fill=(14,11,12,255), outline=(179,18,46,255), width=int(1.2*u))
    bars=[0.85,0.55,0.95,0.4,0.7,0.6]
    for i,h in enumerate(bars):
        x0=25*u+i*9*u
        d.rounded_rectangle([x0, 76*u-h*20*u, x0+6*u, 76*u], radius=1.2*u, fill=(79,210,255,255) if i!=2 else (255,77,94,255))
    return im.resize((size,size), Image.LANCZOS)
out=sys.argv[1]
make(1024).save(f'{out}/icon-1024.png')
make(180,bg=False).save(f'{out}/pkforge.png')
make(180,bg=False).save(f'{out}/splash.png')
