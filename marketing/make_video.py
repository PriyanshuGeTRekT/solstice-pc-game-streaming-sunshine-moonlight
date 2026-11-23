"""Renders the Solstice YouTube video (1920x1080), the YouTube Short (1080x1920) and the thumbnail
from the apps' own demo-mode screens (marketing/frames, made by capture.ps1).

python make_video.py [main|short|thumb|all] [--preview SECONDS]
Needs Pillow, NumPy and ffmpeg (found on PATH or in the WinGet packages folder).
"""
import glob
import math
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FRAMES = os.path.join(HERE, "frames")
OUT = os.path.join(HERE, "out")
FPS = 30

BG = (13, 15, 20)
TEXT = (236, 238, 242)
MUTED = (160, 168, 182)
FAINT = (111, 119, 137)
SUN = (255, 179, 71)
OK = (95, 208, 138)
LINE = (53, 60, 78)
FONTS = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")


# ---------------------------------------------------------------- helpers

_font_cache = {}


def font(kind, size):
    name = {"regular": "segoeui.ttf", "semibold": "seguisb.ttf", "bold": "segoeuib.ttf", "black": "seguibl.ttf",
            "light": "segoeuil.ttf"}[kind]
    key = (name, size)
    if key not in _font_cache:
        _font_cache[key] = ImageFont.truetype(os.path.join(FONTS, name), size)
    return _font_cache[key]


def ease(x):  # ease-out cubic, clamped
    x = max(0.0, min(1.0, x))
    return 1 - (1 - x) ** 3


def ease_io(x):
    x = max(0.0, min(1.0, x))
    return 3 * x * x - 2 * x * x * x


def ramp(t, start, dur):
    return ease((t - start) / dur) if dur > 0 else float(t >= start)


def fade_window(t, start, end, fin=0.45, fout=0.35):
    """Opacity for an element shown between start and end."""
    if t < start or t > end:
        return 0.0
    return min(ease((t - start) / fin), ease((end - t) / fout) if fout > 0 else 1.0)


_img_cache = {}


def frame(name):
    if name not in _img_cache:
        _img_cache[name] = Image.open(os.path.join(FRAMES, name + ".png")).convert("RGB")
    return _img_cache[name]


def with_alpha(img, a):
    if a >= 0.999:
        return img
    img = img.copy()
    alpha = img.getchannel("A").point(lambda v: int(v * a))
    img.putalpha(alpha)
    return img


def paste(canvas, img, xy, alpha=1.0, scale=1.0):
    if alpha <= 0.003:
        return
    if abs(scale - 1.0) > 1e-3:
        w, h = img.size
        nw, nh = max(1, int(w * scale)), max(1, int(h * scale))
        img = img.resize((nw, nh), Image.BILINEAR)
        xy = (int(xy[0] + (w - nw) / 2), int(xy[1] + (h - nh) / 2))
    img = with_alpha(img, alpha)
    canvas.alpha_composite(img, (int(xy[0]), int(xy[1])))


def text_img(lines, size, kind="semibold", color=TEXT, spacing=1.12, align="left", max_width=None):
    """Renders lines (list of str or (str, color)) into a transparent image."""
    f = font(kind, size)
    rows = []
    for ln in lines:
        s, c = (ln, color) if isinstance(ln, str) else ln
        if max_width:
            words, cur = s.split(" "), ""
            for w_ in words:
                trial = (cur + " " + w_).strip()
                if f.getlength(trial) <= max_width or not cur:
                    cur = trial
                else:
                    rows.append((cur, c)); cur = w_
            rows.append((cur, c))
        else:
            rows.append((s, c))
    lh = int(size * spacing)
    width = int(max(f.getlength(s) for s, _ in rows)) + 4
    img = Image.new("RGBA", (width, lh * len(rows) + int(size * 0.3)), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i, (s, c) in enumerate(rows):
        x = 0 if align == "left" else (width - f.getlength(s)) / 2
        d.text((x, i * lh), s, font=f, fill=c)
    return img


def shadow(size, radius, blur, opacity=0.55, spread=0):
    w, h = size
    pad = blur * 3
    m = Image.new("L", (w + pad * 2, h + pad * 2), 0)
    ImageDraw.Draw(m).rounded_rectangle((pad - spread, pad - spread, pad + w + spread, pad + h + spread), radius=radius, fill=int(255 * opacity))
    m = m.filter(ImageFilter.GaussianBlur(blur))
    img = Image.new("RGBA", m.size, (0, 0, 0, 0))
    img.putalpha(m)
    return img, pad


def background(w, h, glow=(1.0, 0.0), glow2=(0.0, 1.0)):
    """Dark background with two soft colour glows, like the apps."""
    base = np.zeros((h, w, 3), np.float32)
    base[:] = BG
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    for (gx, gy), col, strength, rad in ((glow, SUN, 0.16, 0.75), (glow2, (139, 147, 255), 0.09, 0.65)):
        cx, cy = gx * w, gy * h
        dist = np.sqrt(((xx - cx) / (w * rad)) ** 2 + ((yy - cy) / (h * rad)) ** 2)
        k = np.clip(1 - dist, 0, 1) ** 2 * strength
        base += (np.array(col, np.float32) - base) * k[..., None]
    return Image.fromarray(base.clip(0, 255).astype(np.uint8)).convert("RGBA")


def orange_box(img, region):
    """Bounding box of the app's orange (button fill or highlighted text) inside region (x0,y0,x1,y1)."""
    a = np.asarray(img).astype(np.int16)
    x0, y0, x1, y1 = region
    sub = a[y0:y1, x0:x1]
    m = (np.abs(sub[..., 0] - 255) < 14) & (np.abs(sub[..., 1] - 179) < 18) & (np.abs(sub[..., 2] - 71) < 24)
    rows = m.sum(1) > 8
    if not rows.any():
        return None
    # Use the lowest contiguous band of orange rows (a button sits below headline digits/text).
    idx = np.where(rows)[0]
    groups, cur = [], [idx[0]]
    for r in idx[1:]:
        if r - cur[-1] <= 3:
            cur.append(r)
        else:
            groups.append(cur); cur = [r]
    groups.append(cur)
    g = groups[-1]
    cols = np.where(m[g[0]:g[-1] + 1].any(0))[0]
    return (x0 + cols[0], y0 + g[0], x0 + cols[-1], y0 + g[-1])


# ---------------------------------------------------------------- props

ICON = Image.open(os.path.join(ROOT, "docs", "icon.png")).convert("RGBA")
PLAY_ICON = None
try:
    _ico = Image.open(os.path.join(ROOT, "src", "SolsticePlay", "Assets", "app.ico"))
    _ico.size = max(_ico.ico.sizes())
    PLAY_ICON = _ico.convert("RGBA").resize((256, 256), Image.LANCZOS)
except Exception:
    PLAY_ICON = ICON


class Window:
    """The PC app inside a Windows 11-style window frame, pre-rendered at a fixed width."""

    TITLE = 46

    def __init__(self, width):
        self.width = width
        self.cache = {}

    def scale(self, shot):
        return self.width / frame(shot).width

    def render(self, shot):
        if shot in self.cache:
            return self.cache[shot]
        src = frame(shot)
        w = self.width
        h = int(src.height * w / src.width)
        content = src.resize((w, h), Image.LANCZOS)
        th = self.TITLE
        win = Image.new("RGBA", (w, h + th), (0, 0, 0, 0))
        mask = Image.new("L", win.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h + th - 1), radius=14, fill=255)
        body = Image.new("RGBA", win.size, (13, 15, 20, 255))
        body.paste(content, (0, th))
        d = ImageDraw.Draw(body)
        icon = ICON.resize((22, 22), Image.LANCZOS)
        body.alpha_composite(icon, (16, (th - 22) // 2))
        d.text((48, th // 2), "Solstice", font=font("regular", 17), fill=(200, 204, 212), anchor="lm")
        # Window controls
        cx = w - 30
        d.line((cx - 8, th // 2, cx + 8, th // 2 - 0), fill=None)
        for i, glyph in enumerate(("x", "box", "min")):
            x = w - 26 - i * 58
            y = th // 2
            if glyph == "x":
                d.line((x - 7, y - 7, x + 7, y + 7), fill=(200, 204, 212), width=2)
                d.line((x - 7, y + 7, x + 7, y - 7), fill=(200, 204, 212), width=2)
            elif glyph == "box":
                d.rectangle((x - 7, y - 7, x + 7, y + 7), outline=(200, 204, 212), width=2)
            else:
                d.line((x - 8, y, x + 8, y), fill=(200, 204, 212), width=2)
        win.paste(body, (0, 0), mask)
        ImageDraw.Draw(win).rounded_rectangle((0, 0, w - 1, h + th - 1), radius=14, outline=LINE, width=2)
        sh, pad = shadow(win.size, 18, 28, 0.6)
        out = Image.new("RGBA", (win.width + pad * 2, win.height + pad * 2), (0, 0, 0, 0))
        out.alpha_composite(sh, (0, 12))
        out.alpha_composite(win, (pad, pad))
        self.cache[shot] = (out, pad)
        return out, pad

    def point(self, shot, x, y):
        """Maps a point in the raw screenshot to coordinates inside the rendered window image."""
        _, pad = self.render(shot)
        s = self.scale(shot)
        return pad + x * s, pad + self.TITLE + y * s


class Handheld:
    """A generic Windows handheld (no brand) with the Solstice Play screen in it."""

    def __init__(self, screen_w):
        self.sw = screen_w
        self.sh = int(screen_w * 9 / 16)
        self.cache = {}

    def render(self, shot):
        if shot in self.cache:
            return self.cache[shot]
        sw, sh = self.sw, self.sh
        side = int(sh * 0.62)        # width of each grip (handheld-like proportions)
        bez = int(sh * 0.06)
        W = sw + 2 * side + 2 * bez
        H = sh + 2 * bez + int(sh * 0.16)
        body = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(body)
        r = int(H * 0.36)
        # Shell with a subtle vertical gradient
        shell = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        grad = np.linspace(46, 22, H).astype(np.uint8)
        g = np.stack([grad, grad + 1, grad + 5, np.full(H, 255, np.uint8)], 1)[:, None, :].repeat(W, 1)
        shell = Image.fromarray(g, "RGBA")
        m = Image.new("L", (W, H), 0)
        ImageDraw.Draw(m).rounded_rectangle((0, 0, W - 1, H - 1), radius=r, fill=255)
        body.paste(shell, (0, 0), m)
        d.rounded_rectangle((0, 0, W - 1, H - 1), radius=r, outline=(70, 74, 86), width=3)
        # Screen
        x0 = side + bez
        y0 = int((H - sh) / 2) - int(sh * 0.02)
        d.rounded_rectangle((x0 - bez, y0 - bez, x0 + sw + bez, y0 + sh + bez), radius=int(bez * 1.2), fill=(8, 9, 12))
        scr = frame(shot).resize((sw, sh), Image.LANCZOS)
        body.paste(scr, (x0, y0))
        # Left controls: stick and d-pad
        cx, cy = side * 0.5, H * 0.34
        sr = side * 0.2
        d.ellipse((cx - sr * 1.25, cy - sr * 1.25, cx + sr * 1.25, cy + sr * 1.25), fill=(14, 15, 19))
        d.ellipse((cx - sr, cy - sr, cx + sr, cy + sr), fill=(40, 42, 50), outline=(70, 74, 86), width=2)
        px, py, pa = side * 0.5, H * 0.68, side * 0.1
        for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            d.rounded_rectangle((px + dx * pa * 1.1 - pa * 0.55, py + dy * pa * 1.1 - pa * 0.55,
                                 px + dx * pa * 1.1 + pa * 0.55, py + dy * pa * 1.1 + pa * 0.55), radius=3, fill=(34, 36, 43))
        # Right controls: face buttons and stick
        bx, by, ba = W - side * 0.5, H * 0.34, side * 0.13
        for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            d.ellipse((bx + dx * ba - ba * 0.5, by + dy * ba - ba * 0.5, bx + dx * ba + ba * 0.5, by + dy * ba + ba * 0.5),
                      fill=(34, 36, 43), outline=(70, 74, 86), width=2)
        cx2, cy2 = W - side * 0.5, H * 0.68
        d.ellipse((cx2 - sr * 1.25, cy2 - sr * 1.25, cx2 + sr * 1.25, cy2 + sr * 1.25), fill=(14, 15, 19))
        d.ellipse((cx2 - sr, cy2 - sr, cx2 + sr, cy2 + sr), fill=(40, 42, 50), outline=(70, 74, 86), width=2)
        sh_, pad = shadow((W, H), r, 36, 0.65)
        out = Image.new("RGBA", (W + pad * 2, H + pad * 2), (0, 0, 0, 0))
        out.alpha_composite(sh_, (0, 18))
        out.alpha_composite(body, (pad, pad))
        self.cache[shot] = (out, pad, (x0, y0))
        return out, pad, (x0, y0)


def cursor_img(scale=1.0):
    s = 2
    pts = [(0, 0), (0, 34), (9, 26), (15, 40), (21, 37), (15, 24), (27, 24)]
    img = Image.new("RGBA", (40 * s, 48 * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.polygon([(x * s + 3, y * s + 3) for x, y in pts], fill=(0, 0, 0, 90))
    d.polygon([(x * s, y * s) for x, y in pts], fill=(255, 255, 255), outline=(20, 22, 28))
    img = img.resize((int(40 * scale), int(48 * scale)), Image.LANCZOS)
    return img


CURSOR = None


def draw_cursor(canvas, pos, t_click=None, t=0.0):
    global CURSOR
    if CURSOR is None:
        CURSOR = cursor_img(1.0)
    x, y = pos
    if t_click is not None and 0 <= t - t_click < 0.6:
        k = (t - t_click) / 0.6
        r = 14 + 46 * ease(k)
        ring = Image.new("RGBA", (140, 140), (0, 0, 0, 0))
        ImageDraw.Draw(ring).ellipse((70 - r, 70 - r, 70 + r, 70 + r), outline=(255, 255, 255, int(200 * (1 - k))), width=4)
        canvas.alpha_composite(ring, (int(x - 70), int(y - 70)))
    press = 0.9 if (t_click is not None and 0 <= t - t_click < 0.15) else 1.0
    c = CURSOR if press == 1.0 else CURSOR.resize((int(CURSOR.width * press), int(CURSOR.height * press)))
    canvas.alpha_composite(c, (int(x), int(y)))


def pill(text, size=26, fg=TEXT, bg=(26, 30, 40), border=LINE, pad=(22, 12), dot=None):
    f = font("semibold", size)
    tw = int(f.getlength(text))
    extra = int(size * 0.9) if dot else 0
    w, h = tw + pad[0] * 2 + extra, size + pad[1] * 2
    img = Image.new("RGBA", (w + 2, h + 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, w, h), radius=h // 2, fill=bg + (235,), outline=border, width=2)
    if dot:
        r = size * 0.22
        cx, cy = pad[0] + r, h / 2
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=dot)
    d.text((pad[0] + extra, h / 2), text, font=f, fill=fg, anchor="lm")
    return img


def caption(canvas, t, xy, eyebrow, headline, sub=None, start=0.0, end=99.0, width=820, size=64, align="left"):
    """Eyebrow + headline + optional sub, sliding up and fading in."""
    x, y = xy
    a = fade_window(t, start, end)
    if a <= 0:
        return
    dy = (1 - ramp(t, start, 0.6)) * 26
    cur = y + dy
    if eyebrow:
        e = text_img([eyebrow.upper()], 24, "bold", SUN)
        ex = x if align == "left" else x - e.width / 2
        paste(canvas, e, (ex, cur), a)
        cur += 46
    h = text_img(headline if isinstance(headline, list) else [headline], size, "bold", TEXT, 1.1, align, width)
    hx = x if align == "left" else x - h.width / 2
    paste(canvas, h, (hx, cur), a)
    cur += h.height + 10
    if sub:
        a2 = fade_window(t, start + 0.25, end)
        s = text_img(sub if isinstance(sub, list) else [sub], 30, "regular", MUTED, 1.35, align, width)
        sx = x if align == "left" else x - s.width / 2
        paste(canvas, s, (sx, cur + (1 - ramp(t, start + 0.25, 0.6)) * 20), a2)


# ---------------------------------------------------------------- main video scenes (1920x1080)

W, H = 1920, 1080
BG_MAIN = None
WIN = Window(1180)
WIN_SMALL = Window(860)
HAND = Handheld(860)
HAND_SMALL = Handheld(540)


def scene_hook(c, t, d):
    a = fade_window(t, 0.0, d, 0.6, 0.4)
    k = ramp(t, 0.1, 0.9)
    icon = ICON.resize((180, 180), Image.LANCZOS)
    paste(c, icon, (W / 2 - 90, 250 - (1 - k) * 30), a * k, 0.85 + 0.15 * k)
    caption(c, t, (W / 2, 470), None, ["Play your PC games", ("on your handheld.", SUN)], None, 0.5, d, 1600, 92, "center")
    if t > 1.4:
        s = text_img(["Low latency. Every launcher. Set up in two minutes."], 38, "regular", MUTED, align="center")
        paste(c, s, (W / 2 - s.width / 2, 720 + (1 - ramp(t, 1.4, 0.6)) * 18), fade_window(t, 1.4, d))


CHORES = [
    "Install a streaming service", "Install a controller driver", "Pick an admin password",
    "Type PINs to pair", "Add every launcher by hand", "Guess encoder and bitrate settings",
    "PC won't show up on a \"Public\" network", "Fix it all again after an update",
]


def scene_problem(c, t, d):
    caption(c, t, (170, 150), "The problem", ["Sunshine + Moonlight are amazing.", ("Setting them up is not.", SUN)],
            None, 0.0, d, 1400, 70)
    f = font("semibold", 34)
    for i, chore in enumerate(CHORES):
        st = 1.1 + i * 0.42
        a = fade_window(t, st, d)
        if a <= 0:
            continue
        col, row = i % 2, i // 2
        x, y = 175 + col * 800, 470 + row * 92
        img = Image.new("RGBA", (760, 70), (0, 0, 0, 0))
        dd = ImageDraw.Draw(img)
        dd.rounded_rectangle((0, 0, 740, 66), radius=16, fill=(22, 25, 34, 255), outline=LINE, width=2)
        dd.text((30, 33), chore, font=f, fill=MUTED, anchor="lm")
        strike = ramp(t, 4.6 + i * 0.08, 0.35)
        if strike > 0:
            tw = f.getlength(chore)
            dd.line((26, 35, 26 + (tw + 10) * strike, 35), fill=(255, 123, 114), width=4)
        paste(c, img, (x, y + (1 - ramp(t, st, 0.5)) * 16), a)


def scene_meet(c, t, d):
    k = ramp(t, 0.0, 0.8)
    a = fade_window(t, 0.0, d, 0.5, 0.4)
    icon = ICON.resize((200, 200), Image.LANCZOS)
    paste(c, icon, (W / 2 - 100, 210), a, 0.8 + 0.2 * k)
    name = text_img(["Solstice"], 120, "bold", TEXT)
    paste(c, name, (W / 2 - name.width / 2, 440 + (1 - k) * 20), a)
    s = text_img(["One click on your PC. One tap on your handheld."], 40, "regular", MUTED, align="center")
    paste(c, s, (W / 2 - s.width / 2, 610), fade_window(t, 0.6, d))
    p1 = pill("Built on Sunshine + Moonlight", 26, dot=SUN)
    p2 = pill("Free & open source", 26, dot=OK)
    a3 = fade_window(t, 1.2, d)
    paste(c, p1, (W / 2 - p1.width - 12, 720), a3)
    paste(c, p2, (W / 2 + 12, 720), a3)


SETUP_SEQ = ["pc-welcome"] + [f"pc-setup-{i}" for i in range(7)] + ["pc-home"]


def scene_setup(c, t, d):
    caption(c, t, (110, 210), "On your PC", ["One click sets up", "everything."], None, 0.0, d, 600, 66)
    bullets = ["Installs Sunshine and the controller driver", "A private, encrypted login",
               "Tuned settings and your launchers", "Checks the encoder and controller"]
    for i, b in enumerate(bullets):
        st = 3.0 + i * 1.6
        a = fade_window(t, st, d)
        if a > 0:
            dot = Image.new("RGBA", (26, 26), (0, 0, 0, 0))
            ImageDraw.Draw(dot).ellipse((3, 3, 23, 23), fill=OK)
            ImageDraw.Draw(dot).line((8, 13, 12, 17, 19, 9), fill=(6, 32, 15), width=3)
            paste(c, dot, (112, 482 + i * 66), a)
            tx = text_img([b], 28, "regular", TEXT, max_width=520)
            paste(c, tx, (152, 476 + i * 66 + (1 - ramp(t, st, 0.4)) * 10), a)

    # Which screen is showing: welcome, then the seven setup steps, then home.
    click_t = 2.0
    if t < click_t + 0.35:
        shot = "pc-welcome"
    else:
        steps_t = t - (click_t + 0.35)
        i = int(steps_t / 0.95)
        shot = SETUP_SEQ[min(1 + i, len(SETUP_SEQ) - 1)]
    win, pad = WIN.render(shot)
    a = fade_window(t, 0.0, d, 0.6, 0.4)
    wx, wy = 690, 120 + (1 - ramp(t, 0.0, 0.8)) * 40
    paste(c, win, (wx - pad, wy - pad), a)

    # Cursor clicks "Set up everything".
    src = frame("pc-welcome")
    box = orange_box(src, (int(src.width * 0.2), int(src.height * 0.3), src.width, src.height))
    if box and t < click_t + 1.2:
        bx, by = WIN.point("pc-welcome", (box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
        target = (wx - pad + bx, wy - pad + by)
        start = (W - 260, H - 120)
        k = ease_io((t - 0.6) / 1.3)
        pos = (start[0] + (target[0] - start[0]) * k, start[1] + (target[1] - start[1]) * k)
        if t > 0.5:
            draw_cursor(c, pos, click_t, t)


def scene_add(c, t, d):
    caption(c, t, (110, 230), "On your PC", ["Add your", "handheld."],
            ["Open the address on the handheld and", "download Solstice Play from your PC."], 0.0, d, 560, 66)
    shot = "pc-link"
    win, pad = WIN.render(shot)
    a = fade_window(t, 0.0, d, 0.5, 0.4)
    wx, wy = 690, 120
    paste(c, win, (wx - pad, wy - pad), a)
    src = frame(shot)
    box = orange_box(src, (int(src.width * 0.25), 0, src.width, int(src.height * 0.45)))
    if box:
        x0, y0 = WIN.point(shot, box[0], box[1])
        x1, y1 = WIN.point(shot, box[2], box[3])
        k = ramp(t, 1.0, 0.5)
        if k > 0:
            puls = 0.5 + 0.5 * math.sin((t - 1.0) * 5)
            hl = Image.new("RGBA", (int(x1 - x0) + 60, int(y1 - y0) + 50), (0, 0, 0, 0))
            ImageDraw.Draw(hl).rounded_rectangle((4, 4, hl.width - 5, hl.height - 5), radius=16,
                                                 outline=SUN + (int(255 * (0.55 + 0.45 * puls)),), width=5)
            paste(c, hl, (wx - pad + x0 - 30, wy - pad + y0 - 25), a * k)


def scene_pair(c, t, d):
    caption(c, t, (W / 2, 70), None, "Same four digits on both screens. Approve.", None, 0.0, d, 1700, 60, "center")
    hand, hpad, _ = HAND_SMALL.render("ally-pair")
    a = fade_window(t, 0.0, d, 0.5, 0.4)
    hx, hy = 40, 330 + (1 - ramp(t, 0.0, 0.7)) * 40
    paste(c, hand, (hx - hpad, hy - hpad), a)
    label = text_img(["ON THE HANDHELD"], 22, "bold", FAINT)
    paste(c, label, (hx + 30, hy - 50), a)

    approved = t > 4.6
    shot = "pc-approve" if not approved else "pc-home"
    win, pad = WIN_SMALL.render(shot)
    wx, wy = 1000, 250 + (1 - ramp(t, 0.3, 0.7)) * 40
    paste(c, win, (wx - pad, wy - pad), fade_window(t, 0.3, d, 0.5, 0.4))
    label2 = text_img(["ON YOUR PC"], 22, "bold", FAINT)
    paste(c, label2, (wx + 20, wy - 50), fade_window(t, 0.3, d))

    src = frame("pc-approve")
    box = orange_box(src, (int(src.width * 0.45), int(src.height * 0.5), src.width, src.height))
    click_t = 4.3
    if box and t < click_t + 1.0:
        bx, by = WIN_SMALL.point("pc-approve", (box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
        target = (wx - pad + bx, wy - pad + by)
        start = (W - 120, H - 80)
        k = ease_io((t - 2.6) / 1.4)
        pos = (start[0] + (target[0] - start[0]) * k, start[1] + (target[1] - start[1]) * k)
        if t > 2.4:
            draw_cursor(c, pos, click_t, t)
    if approved:
        k = ramp(t, 4.7, 0.5)
        badge = pill("Paired", 34, fg=(6, 32, 15), bg=OK, border=OK, pad=(28, 14), dot=(6, 32, 15))
        paste(c, badge, (W / 2 - badge.width / 2, 900 + (1 - k) * 20), fade_window(t, 4.7, d))


def scene_play(c, t, d):
    caption(c, t, (W / 2, 60), "On your handheld", "Pick a game. Press Play.", None, 0.0, d, 1500, 66, "center")
    hand, hpad, _ = HAND.render("ally-home")
    a = fade_window(t, 0.0, d, 0.5, 0.4)
    k = ramp(t, 0.0, 6.0)
    zoom = 1.0 + 0.04 * k
    hx = W / 2 - (hand.width - 2 * hpad) / 2
    paste(c, hand, (hx - hpad, 250 - hpad), a, zoom)
    chips = ["Native 1080p at 120 fps", "Wakes your PC from sleep", "Built for the controller"]
    imgs = [pill(s, 26, dot=SUN) for s in chips]
    total = sum(i.width for i in imgs) + 24 * (len(imgs) - 1)
    x = W / 2 - total / 2
    for i, img in enumerate(imgs):
        paste(c, img, (x, 960), fade_window(t, 1.2 + i * 0.35, d))
        x += img.width + 24


def scene_library(c, t, d):
    caption(c, t, (110, 230), "Every launcher", ["With box art."],
            "Steam, Epic Games, EA app, Ubisoft Connect, Xbox app, GOG Galaxy, Battle.net and Playnite.", 0.0, d, 540, 66)
    win, pad = WIN.render("pc-library")
    a = fade_window(t, 0.0, d, 0.5, 0.4)
    wy = 120 - 60 * ramp(t, 1.5, 4.0)
    paste(c, win, (690 - pad, wy - pad), a)


FEATURES = [
    ("Native resolution", "The PC switches to the handheld's screen mode while you play."),
    ("Wake-on-LAN", "Press Play and a sleeping PC wakes up first."),
    ("No PIN typing", "Approve the handheld on the PC with one click."),
    ("Works on any network", "Finds your PC even when Windows calls the network Public."),
    ("Private by default", "Random login, and the admin page stays on the PC."),
    ("Free & open source", "MIT licensed. Sunshine and Moonlight stay untouched."),
]


def scene_features(c, t, d):
    caption(c, t, (W / 2, 90), None, "Everything the setup guides tell you to do. Done.", None, 0.0, d, 1700, 58, "center")
    cw, ch, gap = 540, 230, 36
    x0 = W / 2 - (3 * cw + 2 * gap) / 2
    for i, (title, body) in enumerate(FEATURES):
        col, row = i % 3, i // 3
        st = 0.4 + i * 0.22
        a = fade_window(t, st, d)
        if a <= 0:
            continue
        card = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
        dd = ImageDraw.Draw(card)
        dd.rounded_rectangle((0, 0, cw - 1, ch - 1), radius=22, fill=(22, 25, 34, 255), outline=LINE, width=2)
        dd.rounded_rectangle((30, 34, 78, 82), radius=12, fill=SUN)
        dd.text((54, 58), str(i + 1), font=font("bold", 26), fill=(28, 19, 4), anchor="mm")
        dd.text((30, 104), title, font=font("bold", 36), fill=TEXT)
        b = text_img([body], 26, "regular", MUTED, 1.3, max_width=cw - 60)
        card.alpha_composite(b, (30, 156))
        paste(c, card, (x0 + col * (cw + gap), 300 + row * (ch + gap) + (1 - ramp(t, st, 0.5)) * 20), a)


def scene_streaming(c, t, d):
    caption(c, t, (110, 230), "While you play", ["Your PC matches", "the handheld."],
            ["The monitor switches to the handheld's", "resolution and refresh rate, then back."], 0.0, d, 560, 66)
    win, pad = WIN.render("pc-streaming")
    paste(c, win, (690 - pad, 120 - pad), fade_window(t, 0.0, d, 0.5, 0.4))


def scene_outro(c, t, d):
    a = fade_window(t, 0.0, d, 0.6, 1.2)
    k = ramp(t, 0.0, 0.8)
    icon = ICON.resize((160, 160), Image.LANCZOS)
    paste(c, icon, (W / 2 - 80, 170), a, 0.85 + 0.15 * k)
    name = text_img(["Solstice"], 104, "bold", TEXT)
    paste(c, name, (W / 2 - name.width / 2, 360), a)
    s = text_img(["Free and open source for Windows."], 40, "regular", MUTED, align="center")
    paste(c, s, (W / 2 - s.width / 2, 510), a)
    b1 = pill("Solstice  ·  for your PC", 32, fg=(28, 19, 4), bg=SUN, border=SUN, pad=(34, 18))
    b2 = pill("Solstice Play  ·  for your handheld", 32, pad=(34, 18))
    a2 = fade_window(t, 0.6, d, 0.5, 1.2)
    paste(c, b1, (W / 2 - b1.width - 14, 620), a2)
    paste(c, b2, (W / 2 + 14, 620), a2)
    l = text_img(["Download link in the description"], 32, "semibold", TEXT, align="center")
    paste(c, l, (W / 2 - l.width / 2, 760), fade_window(t, 1.2, d, 0.5, 1.2))
    l2 = text_img(["github.com/PriyanshuGeTRekT"], 28, "regular", FAINT, align="center")
    paste(c, l2, (W / 2 - l2.width / 2, 812), fade_window(t, 1.2, d, 0.5, 1.2))


MAIN = [
    ("hook", scene_hook, 5.0),
    ("problem", scene_problem, 7.5),
    ("meet", scene_meet, 4.5),
    ("setup", scene_setup, 11.5),
    ("add", scene_add, 6.5),
    ("pair", scene_pair, 8.0),
    ("play", scene_play, 8.0),
    ("library", scene_library, 7.0),
    ("features", scene_features, 7.5),
    ("streaming", scene_streaming, 6.0),
    ("outro", scene_outro, 7.0),
]


# ---------------------------------------------------------------- Short (1080x1920)

SW, SH = 1080, 1920
HAND_SHORT = Handheld(560)
WIN_SHORT = Window(1020)


def s_hook(c, t, d):
    caption(c, t, (SW / 2, 230), None, ["PC games on your", ("handheld.", SUN)], None, 0.0, d, 960, 92, "center")
    s = text_img(["One click on the PC. One tap on the handheld."], 36, "regular", MUTED, 1.3, "center", 900)
    paste(c, s, (SW / 2 - s.width / 2, 520), fade_window(t, 0.5, d))
    hand, hpad, _ = HAND_SHORT.render("ally-home")
    paste(c, hand, (SW / 2 - hand.width / 2, 760 - hpad + (1 - ramp(t, 0.2, 0.8)) * 60), fade_window(t, 0.2, d), 1.0)


def s_setup(c, t, d):
    caption(c, t, (SW / 2, 170), "On your PC", "One click sets up everything.", None, 0.0, d, 960, 64, "center")
    i = int(t / 0.62)
    shot = SETUP_SEQ[min(i, len(SETUP_SEQ) - 1)]
    win, pad = WIN_SHORT.render(shot)
    paste(c, win, (SW / 2 - win.width / 2, 520 - pad), fade_window(t, 0.0, d, 0.5, 0.4))


def s_pair(c, t, d):
    caption(c, t, (SW / 2, 150), None, ["Same four digits.", ("Approve.", SUN)], None, 0.0, d, 960, 80, "center")
    hand, hpad, _ = HAND_SHORT.render("ally-pair")
    paste(c, hand, (SW / 2 - hand.width / 2, 470 - hpad), fade_window(t, 0.0, d, 0.5, 0.4))
    shot = "pc-approve" if t < 3.2 else "pc-home"
    win, pad = WIN_SHORT.render(shot)
    paste(c, win, (SW / 2 - win.width / 2, 1060 - pad), fade_window(t, 0.4, d, 0.5, 0.4))
    if t > 3.2:
        badge = pill("Paired", 40, fg=(6, 32, 15), bg=OK, border=OK, pad=(32, 16), dot=(6, 32, 15))
        paste(c, badge, (SW / 2 - badge.width / 2, 1760), fade_window(t, 3.2, d))


def s_launchers(c, t, d):
    caption(c, t, (SW / 2, 170), "Every launcher", "Steam, Epic, EA, Ubisoft, Xbox and more.", None, 0.0, d, 960, 64, "center")
    win, pad = WIN_SHORT.render("pc-library")
    paste(c, win, (SW / 2 - win.width / 2, 600 - pad - 80 * ramp(t, 1.0, 4.0)), fade_window(t, 0.0, d, 0.5, 0.4))


def s_outro(c, t, d):
    a = fade_window(t, 0.0, d, 0.5, 1.0)
    icon = ICON.resize((200, 200), Image.LANCZOS)
    paste(c, icon, (SW / 2 - 100, 520), a)
    name = text_img(["Solstice"], 120, "bold", TEXT)
    paste(c, name, (SW / 2 - name.width / 2, 770), a)
    s = text_img(["Free & open source", "Sunshine + Moonlight, set up for you"], 40, "regular", MUTED, 1.4, "center")
    paste(c, s, (SW / 2 - s.width / 2, 940), a)
    l = pill("Link in the description", 38, fg=(28, 19, 4), bg=SUN, border=SUN, pad=(36, 20))
    paste(c, l, (SW / 2 - l.width / 2, 1170), fade_window(t, 0.6, d, 0.5, 1.0))


SHORT = [
    ("hook", s_hook, 4.5),
    ("setup", s_setup, 6.0),
    ("pair", s_pair, 6.0),
    ("launchers", s_launchers, 5.0),
    ("outro", s_outro, 4.5),
]


# ---------------------------------------------------------------- rendering

def ffmpeg_path():
    found = shutil.which("ffmpeg")
    if found:
        return found
    hits = glob.glob(os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages", "*FFmpeg*", "**", "ffmpeg.exe"), recursive=True)
    if hits:
        return hits[0]
    raise SystemExit("ffmpeg not found")


def render(scenes, size, bg_glows, out_name, preview=None):
    w, h = size
    bg = background(w, h, *bg_glows)
    total = sum(dur for _, _, dur in scenes)
    if preview:
        total = min(total, preview)
    os.makedirs(OUT, exist_ok=True)
    music = os.path.join(OUT, f"{out_name}-music.wav")
    subprocess.run([sys.executable, os.path.join(HERE, "music.py"), f"{total:.2f}", music], check=True)
    out = os.path.join(OUT, f"{out_name}.mp4")
    cmd = [ffmpeg_path(), "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{w}x{h}", "-r", str(FPS),
           "-i", "-", "-i", music, "-c:v", "libx264", "-preset", "slow", "-crf", "17", "-pix_fmt", "yuv420p",
           "-profile:v", "high", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", out]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    n = int(round(total * FPS))
    starts, acc = [], 0.0
    for name, fn, dur in scenes:
        starts.append((acc, name, fn, dur)); acc += dur
    xfade = 0.35
    chapters = []
    for i in range(n):
        tt = i / FPS
        canvas = bg.copy()
        for s0, name, fn, dur in starts:
            local = tt - s0
            if -xfade <= local <= dur:   # small overlap lets the next scene fade in over the end of the last
                fn(canvas, max(0.0, local), dur)
        proc.stdin.write(canvas.convert("RGB").tobytes())
        if i % (FPS * 5) == 0:
            print(f"  {out_name}: {tt:5.1f}s / {total:.1f}s", flush=True)
    proc.stdin.close()
    proc.wait()
    for s0, name, _, _ in starts:
        chapters.append((s0, name))
    print(f"wrote {out}")
    return out, chapters


def thumbnail():
    w, h = 1280, 720
    c = background(w, h, (0.85, 0.15), (0.05, 1.0))
    hand = Handheld(400)
    img, pad, _ = hand.render("ally-home")
    scale = 640 / (img.width - 2 * pad)
    img = img.resize((int(img.width * scale), int(img.height * scale)), Image.LANCZOS)
    body_w = int((img.width) - 2 * pad * scale)
    c.alpha_composite(img, (int(w - body_w - pad * scale + 70), int(h / 2 - img.height / 2 + 60)))
    d = ImageDraw.Draw(c)
    icon = ICON.resize((86, 86), Image.LANCZOS)
    c.alpha_composite(icon, (56, 52))
    d.text((158, 95), "Solstice", font=font("bold", 50), fill=TEXT, anchor="lm")
    big = font("black", 94)
    d.text((52, 200), "PC GAMES", font=big, fill=TEXT)
    d.text((52, 310), "ON YOUR", font=big, fill=TEXT)
    d.text((52, 420), "HANDHELD", font=big, fill=SUN)
    tag = pill("One click  ·  Free  ·  Open source", 30, pad=(26, 14), dot=OK)
    c.alpha_composite(tag, (56, 616))
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, "thumbnail.png")
    c.convert("RGB").save(path, optimize=True)
    print(f"wrote {path}")


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "all"
    preview = float(sys.argv[sys.argv.index("--preview") + 1]) if "--preview" in sys.argv else None
    if what in ("thumb", "all"):
        thumbnail()
    if what in ("main", "all"):
        _, ch = render(MAIN, (W, H), ((0.95, 0.05), (0.0, 1.0)), "solstice-youtube", preview)
        print("chapters:", [(f"{int(s // 60)}:{int(s % 60):02d}", n) for s, n in ch])
    if what in ("short", "all"):
        render(SHORT, (SW, SH), ((0.9, 0.08), (0.1, 0.95)), "solstice-short", preview)
