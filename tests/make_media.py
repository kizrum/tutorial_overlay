"""The README's pictures and animations (docs/media). Development only; needs Pillow (pip install pillow).

  record OUTDIR [--fps 15]               screen frames (cursor drawn in, a ring on each click) until OUTDIR/stop exists
  clip FRAMES OUT.webp [--from N] [--to N] [--crop x,y,w,h] [--width W] [--step K] [--quality Q | --lossless]
                                          frames -> an animated WebP
  card FRAMES OUT.webp [--from N] [--to N] [--left X] [--width W] [--hold S]
                                          the step card alone, cut out of each frame, on a transparent background
  join OUT.webp PART.webp[:FROM:TO] ... [--hold S]
                                          animations one after another (frames FROM..TO-1 of each part), the last
                                          frame held S s (2.5)
  mascot FRAMES OUTDIR [--act NAME] [--width W]
                                          the folders of tests --mascot -> one transparent WebP per act

Sharpness: frames are recorded nearly lossless (JPEG 98, no chroma subsampling: coloured text stays clean), the
card and the mascot are encoded lossless, and everything is kept about twice the size the page shows it at, so it
stays sharp on high-DPI screens. The mascot's frames come from the real figure code:
  dotnet run --project tests -- --mascot FRAMES 25 2
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import Image, ImageChops, ImageDraw, ImageGrab, ImageStat


def opt(args, name, default=None):
    return args[args.index(name) + 1] if name in args else default


def frames_in(folder):
    return sorted(f for f in os.listdir(folder) if f.lower().endswith((".jpg", ".png")))


def save_webp(frames, out, ms, lossless, quality=90):
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    extra = dict(lossless=True, quality=100, method=4) if lossless else dict(quality=quality, method=4)
    # a full frame only every 8 s: the screen mostly stands still, so the frames between store only what changed
    # (a quarter of the size at the same quality)
    extra.update(kmin=119, kmax=120)
    frames[0].save(out, save_all=True, append_images=frames[1:], duration=ms, loop=0, allow_mixed=not lossless, **extra)
    print(out, len(frames), "frames", f"{frames[0].width}x{frames[0].height}", os.path.getsize(out) // 1024, "KB")


# ------------------------------------------------------------------ record

def record(args):
    out = args[0]
    fps = float(opt(args, "--fps", 15))
    os.makedirs(out, exist_ok=True)
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # physical pixels, like the overlay
    user32 = ctypes.windll.user32
    pt = wt.POINT()
    n, down_at, t0 = 0, None, time.perf_counter()
    stop = os.path.join(out, "stop")
    print("recording to", out, "- create", stop, "to finish", flush=True)
    while not os.path.exists(stop):
        due = t0 + n / fps
        if (wait := due - time.perf_counter()) > 0:
            time.sleep(wait)
        img = ImageGrab.grab()
        user32.GetCursorPos(ctypes.byref(pt))
        pressed = user32.GetAsyncKeyState(0x01) & 0x8000
        now = time.perf_counter()
        if pressed and down_at is None:
            down_at = now
        if not pressed and down_at is not None and now - down_at > 0.35:
            down_at = None
        draw_cursor(img, pt.x, pt.y, (now - down_at) if down_at is not None else None)
        img.save(os.path.join(out, f"{n:05d}.jpg"), quality=98, subsampling=0)
        n += 1
    print(n, "frames", flush=True)


def draw_cursor(img, x, y, click):
    d = ImageDraw.Draw(img)
    if click is not None:                       # a ring that grows on a click
        r = 14 + 60 * min(click, 0.35)
        d.ellipse((x - r, y - r, x + r, y + r), outline=(255, 77, 109), width=3)
    s = 1.6
    arrow = [(0, 0), (0, 17), (4.5, 13), (7.5, 20), (10, 19), (7, 12.5), (12.5, 12.5)]
    d.polygon([(x + px * s, y + py * s) for px, py in arrow], fill=(255, 255, 255), outline=(0, 0, 0), width=2)


# ------------------------------------------------------------------ clip

def clip(args):
    src, out = args[0], args[1]
    files = frames_in(src)
    a, b = int(opt(args, "--from", 0)), int(opt(args, "--to", len(files) - 1))
    step = int(opt(args, "--step", 1))
    width = int(opt(args, "--width", 0))
    crop = opt(args, "--crop")
    box = tuple(int(v) for v in crop.split(",")) if crop else None
    frames = []
    for f in files[a:b + 1:step]:
        im = Image.open(os.path.join(src, f)).convert("RGB")
        if box:
            x, y, w, h = box
            im = im.crop((x, y, x + w, y + h))
        if width and im.width != width:
            im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
        frames.append(im)
    save_webp(frames, out, round(1000 * step / float(opt(args, "--fps", 15))), "--lossless" in args,
              int(opt(args, "--quality", 90)))


# ------------------------------------------------------------------ card

def card(args):
    """The card's box in each frame: its left padding column is dark between a light top and bottom border (the
    longest such run of the card's own near-black in the column). A card that grows upward (bottom edge fixed) stands on its bottom edge in
    the clip, one that grows downward hangs from its top. A still spell longer than --hold s is cut short."""
    src, out = args[0], args[1]
    files = frames_in(src)
    a, b = int(opt(args, "--from", 0)), int(opt(args, "--to", len(files) - 1))
    left, w = int(opt(args, "--left", 1924)), int(opt(args, "--width", 612))
    hold = int(float(opt(args, "--hold", 1.5)) * 15)
    x = left + 14
    crops, tops, bots, prev, still = [], [], [], None, 0
    for f in files[a:b + 1]:
        im = Image.open(os.path.join(src, f)).convert("RGB")
        px = im.convert("L").load()
        top = bot = None
        last = None                                  # the last light pixel seen going down the column
        run = True                                   # everything since it is the card's own near-black
        for y in range(im.height):
            v = px[x, y]
            if v > 45:
                if last is not None and run and y - last - 1 >= 150 and (bot is None or y - last > bot - top):
                    top, bot = last, y
                last, run = y, True
            elif v > 28:
                run = False                          # another window's dark grey, not the card's padding
        if top is None:
            continue
        tops.append(top)
        bots.append(bot)
        # one pixel in from the border: the card's own edge is half see-through, and what is behind it (another
        # window) would show as a coloured line; the edge is drawn again below
        c = im.crop((left + 1, top + 1, left + w - 1, bot))
        small = c.convert("L").resize((w // 4, c.height // 4))
        if prev is not None and prev.size == small.size and ImageStat.Stat(ImageChops.difference(prev, small)).mean[0] < 0.6:
            still += 1
            if still > hold:
                continue
        else:
            still = 0
        prev = small
        crops.append(c)
    w -= 2
    h = max(c.height for c in crops)
    hang = max(tops) - min(tops) < max(bots) - min(bots)     # the top edge stays put: the card grows downward
    frames = []
    for c in crops:
        k = 4
        size = (w * k, c.height * k)
        mask = Image.new("L", size, 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=13 * k, fill=255)
        edge = Image.new("RGBA", size, (0, 0, 0, 0))     # the card's edge (Palette.Line), drawn smooth
        ImageDraw.Draw(edge).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=13 * k, outline=(58, 62, 76, 255), width=k)
        c = c.convert("RGBA")
        c.alpha_composite(edge.resize(c.size, Image.LANCZOS))
        c.putalpha(mask.resize(c.size, Image.LANCZOS))
        fr = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        fr.alpha_composite(c, (0, 0 if hang else h - c.height))
        frames.append(fr)
    save_webp(frames, out, 67, True)


# ------------------------------------------------------------------ join

def join(args):
    """Parts one after another, each frame with its own duration; smaller frames hang from the top left."""
    hold = float(opt(args, "--hold", 2.5))
    rest = args[1:args.index("--hold")] + args[args.index("--hold") + 2:] if "--hold" in args else args[1:]
    out, parts = args[0], rest
    frames, ms = [], []
    for part in parts:
        path, sep, rng = part.partition(".webp:")
        path = path + ".webp" if sep else path
        im = Image.open(path)
        a, b = 0, im.n_frames
        if rng:
            lo, _, hi = rng.partition(":")
            a, b = int(lo or 0), int(hi or im.n_frames)
        for i in range(a, b):
            im.seek(i)
            frames.append(im.convert("RGBA").copy())
            ms.append(im.info.get("duration", 67))
    w, h = max(f.width for f in frames), max(f.height for f in frames)
    canvas = []
    for f in frames:
        c = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        c.alpha_composite(f)
        canvas.append(c)
    ms[-1] = round(hold * 1000)
    canvas[0].save(out, save_all=True, append_images=canvas[1:], duration=ms, loop=0, lossless=True, method=6)
    print(out, len(canvas), "frames", f"{w}x{h}", os.path.getsize(out) // 1024, "KB", f"{sum(ms) / 1000:.1f} s")


# ------------------------------------------------------------------ mascot

def mascot(args):
    src, out = args[0], args[1]
    width = int(opt(args, "--width", 0))
    only = opt(args, "--act")
    fps = float(opt(args, "--fps", 25))
    os.makedirs(out, exist_ok=True)
    for act in sorted(os.listdir(src)):
        folder = os.path.join(src, act)
        if not os.path.isdir(folder) or (only and act != only):
            continue
        frames = [Image.open(os.path.join(folder, f)).convert("RGBA") for f in frames_in(folder)]
        # trim the empty margin all frames share
        box = None
        for im in frames:
            bb = im.getchannel("A").getbbox()
            if bb:
                box = bb if box is None else (min(box[0], bb[0]), min(box[1], bb[1]), max(box[2], bb[2]), max(box[3], bb[3]))
        if box:
            pad = 8
            box = (max(0, box[0] - pad), max(0, box[1] - pad), min(frames[0].width, box[2] + pad), min(frames[0].height, box[3] + pad))
            frames = [im.crop(box) for im in frames]
        if width:
            frames = [im.resize((width, round(im.height * width / im.width)), Image.LANCZOS) for im in frames]
        save_webp(frames, os.path.join(out, f"mascot-{act}.webp"), round(1000 / fps), True)


if __name__ == "__main__":
    cmd, rest = sys.argv[1], sys.argv[2:]
    {"record": record, "clip": clip, "card": card, "join": join, "mascot": mascot}[cmd](rest)
