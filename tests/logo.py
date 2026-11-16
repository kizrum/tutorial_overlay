"""The README's logo (docs/media/logo.webp). Development only; needs Pillow (pip install pillow) and Microsoft Edge.

  python tests/logo.py [OUT.webp]

The logo is an HTML page (sticker wordmark + the step card in 3D, CSS transforms) rendered by headless Edge at 2x
on a transparent background, trimmed and saved lossless. One picture serves both GitHub themes: the glow and the
card read on dark and on white alike. Colours are the card's (src/Theme.cs); the font is the system Segoe UI.
"""
import os
import subprocess
import sys
import tempfile
import time

from PIL import Image

PINK, ORANGE, YELLOW, GREEN, PURPLE = "#FF4D6D", "#FF7A59", "#FFC43D", "#2EE6A6", "#9B5DE5"
CREAM, INK = "#FFF6EE", "#12141A"
FONT = "'Segoe UI Black','Segoe UI',sans-serif"
W, H = 1340, 760  # room around the 1200x600 drawing so the glow fades out instead of being cut

CSS = f"""
html,body{{margin:0;background:transparent;width:{W}px;height:{H}px;overflow:hidden}}
.stage{{position:relative;width:{W}px;height:{H}px;font-family:'Segoe UI',sans-serif}}
.abs{{position:absolute}}
.scene{{position:absolute;perspective:1600px}}
.p3{{position:absolute;transform-style:preserve-3d}}
.layer{{position:absolute;inset:0;border-radius:30px}}
.face{{position:absolute;inset:0;border-radius:30px;background:linear-gradient(180deg,#262A35,#181B22);
      border:3px solid #4A4F60;overflow:hidden;box-sizing:border-box}}
.stripes{{position:absolute;right:-60px;top:-40px;width:220px;height:420px;transform:rotate(28deg);display:flex}}
.stripes i{{display:block;width:26px;height:100%}}
.pill{{position:absolute;left:30px;top:28px;padding:6px 16px;border-radius:20px;background:{PINK};color:{INK};
      font:800 17px 'Segoe UI';letter-spacing:1.5px}}
.prog{{position:absolute;left:30px;top:76px;display:flex;gap:8px}}
.prog b{{display:block;width:44px;height:10px;border-radius:5px;background:#3A3E4C}}
.title{{position:absolute;left:30px;top:108px;color:#F0F2F7;font:700 30px 'Segoe UI';white-space:nowrap}}
.why{{position:absolute;left:30px;top:160px;width:250px;height:14px;border-radius:7px;background:{YELLOW};opacity:.9}}
.why2{{position:absolute;left:30px;top:184px;width:190px;height:14px;border-radius:7px;background:#9CA2B2;opacity:.6}}
.btn{{position:absolute;top:228px;height:46px;border-radius:14px;font:700 19px 'Segoe UI';display:flex;
     align-items:center;justify-content:center}}
.blur{{position:absolute;border-radius:50%;filter:blur(60px)}}
"""

CURSOR = "M4 3 L4 36 L12 28 L18 42 L25 39 L19 26 L31 26 Z"
STAR = "M0 -1 C .12 -.12 .12 -.12 1 0 C .12 .12 .12 .12 0 1 C -.12 .12 -.12 .12 -1 0 C -.12 -.12 -.12 -.12 0 -1 Z"


def wordmark(x, y, size=128):
    """'tutorial_' over a sticker 'Overlay': a thick round pink stroke under a cream fill, with a drop shadow."""
    sw = size * 0.30
    small_y = sw / 2 + 10 + size * 0.42
    big_y = small_y + size * 0.92
    t = f'x="{sw / 2 + 4}" y="{big_y}" font-family="{FONT}" font-size="{size}" font-weight="900" letter-spacing="-1"'
    return (f'<svg class="abs" style="left:{x}px;top:{y}px" width="640" height="{int(big_y + sw)}">'
            f'<text x="{sw / 2 + 10}" y="{small_y}" font-family="{FONT}" font-size="{size * 0.40}" font-weight="900" '
            f'fill="{PINK}">tutorial_</text>'
            f'<text {t} transform="translate(0 {size * 0.09})" fill="#000" opacity=".28" stroke="#000" '
            f'stroke-width="{sw}" stroke-linejoin="round">Overlay</text>'
            f'<text {t} fill="{PINK}" stroke="{PINK}" stroke-width="{sw}" stroke-linejoin="round">Overlay</text>'
            f'<text {t} fill="{CREAM}">Overlay</text></svg>')


def card(x, y, w=430, h=300, depth=18):
    """The step card lying tilted in 3D: stacked layers give it thickness, the pointer hovers over Done."""
    layers = "".join(
        f'<div class="layer" style="transform:translateZ({-i}px);background:'
        f'{"#0B0C10" if i > depth - 3 else "#2B2F3B" if i < 4 else "#1A1D25"}"></div>' for i in range(depth, 0, -1))
    stripes = "".join(f'<i style="background:{c}"></i>' for c in (PINK, ORANGE, YELLOW, GREEN))
    prog = "".join(f'<b style="background:{PINK}"></b>' if i < 3 else "<b></b>" for i in range(5))
    return f'''
<div class="abs" style="left:{x - 40}px;top:{y + h * 0.55}px;width:{w + 120}px;height:{h * 0.75}px;border-radius:50%;
  background:radial-gradient(closest-side,rgba(0,0,0,.45),rgba(0,0,0,0))"></div>
<div class="scene" style="left:{x}px;top:{y}px;width:{w}px;height:{h}px">
 <div class="p3" style="width:{w}px;height:{h}px;transform:rotateX(52deg) rotateZ(-30deg) translateY(30px)">{layers}
  <div class="face"><div class="stripes">{stripes}</div><div class="pill">STEP 3 OF 5</div><div class="prog">{prog}</div>
   <div class="title">Add a kick</div><div class="why"></div><div class="why2"></div>
   <div class="btn" style="left:30px;width:96px;background:#2B2F3B;color:#F0F2F7;border:2px solid #4A4F60">Help</div>
   <div class="btn" style="left:{w - 180}px;width:108px;background:{PINK};color:{INK}">Done</div></div>
  <svg class="abs" style="left:{w - 150}px;top:{h - 70}px;transform:translateZ(60px)" width="70" height="80"
   viewBox="0 0 40 46"><path d="{CURSOR}" fill="#fff" stroke="{INK}" stroke-width="2.6" stroke-linejoin="round"/></svg>
 </div></div>'''


def glow(x, y):
    return (f'<div class="blur" style="left:{x}px;top:{y}px;width:380px;height:300px;background:{PINK};opacity:.55"></div>'
            f'<div class="blur" style="left:{x + 220}px;top:{y - 60}px;width:300px;height:260px;background:{PURPLE};opacity:.5"></div>'
            f'<div class="blur" style="left:{x + 120}px;top:{y + 150}px;width:280px;height:200px;background:{ORANGE};opacity:.35"></div>')


def sparkles():
    pts = [(600, 120, 16, YELLOW), (1130, 160, 12, PURPLE), (1080, 470, 18, PINK), (560, 470, 10, GREEN), (900, 70, 9, ORANGE)]
    return (f'<svg class="abs" style="left:0;top:0" width="{W}" height="{H}">'
            + "".join(f'<path d="{STAR}" transform="translate({x} {y}) scale({s})" fill="{c}"/>' for x, y, s, c in pts)
            + "</svg>")


def page():
    body = glow(700, 160) + sparkles() + wordmark(10, 150) + card(660, 130)
    return f'<!doctype html><html><head><meta charset="utf-8"><style>{CSS}</style></head><body><div class="stage"><div class="abs" style="left:60px;top:80px">{body}</div></div></body></html>'


def edge():
    for root in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles")):
        p = os.path.join(root or "", "Microsoft", "Edge", "Application", "msedge.exe")
        if os.path.exists(p):
            return p
    sys.exit("Microsoft Edge not found")


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "docs", "media", "logo.webp")
    tmp = tempfile.mkdtemp(prefix="logo-")
    html, png = os.path.join(tmp, "logo.html"), os.path.join(tmp, "logo.png")
    with open(html, "w", encoding="utf-8") as f:
        f.write(page())
    subprocess.run([edge(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=2",
                    "--default-background-color=00000000", f"--user-data-dir={os.path.join(tmp, 'profile')}",
                    f"--window-size={W},{H}", f"--screenshot={png}", "file:///" + html.replace(os.sep, "/")],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(60):  # Edge may finish writing after the command returns
        if os.path.exists(png) and os.path.getsize(png) > 0:
            break
        time.sleep(0.5)
    time.sleep(0.5)
    im = Image.open(png).convert("RGBA")
    box = im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
    im = im.crop(box)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    im.save(out, lossless=True, quality=100, method=6)
    print(out, f"{im.width}x{im.height}", os.path.getsize(out) // 1024, "KB")


if __name__ == "__main__":
    main()
