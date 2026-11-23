"""Builds docs/icon.png, docs/og-image.png and docs/demo.gif from the rendered screenshots.

Run tools/render-screens.ps1 first, then:  python tools/make-media.py
Needs Pillow. Uses the Segoe UI fonts that ship with Windows.
"""
import glob
import os

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, "docs")
SHOTS = os.path.join(DOCS, "screenshots")
BG = (13, 15, 20)
TEXT = (236, 238, 242)
MUTED = (154, 163, 178)
SUN = (255, 179, 71)
FONTS = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")


def font(name, size):
    try:
        return ImageFont.truetype(os.path.join(FONTS, name), size)
    except OSError:
        return ImageFont.load_default()


def shot(name):
    return Image.open(os.path.join(SHOTS, name + ".png")).convert("RGB")


def icon():
    ico = Image.open(os.path.join(ROOT, "src", "Solstice", "Assets", "app.ico"))
    ico.size = max(ico.ico.sizes())
    out = ico.convert("RGBA").resize((256, 256), Image.LANCZOS)
    out.save(os.path.join(DOCS, "icon.png"))
    return out


def og_image(mark):
    w, h = 1280, 640
    img = Image.new("RGB", (w, h), BG)
    # Soft sun glow, top right.
    glow = Image.new("L", (w, h), 0)
    ImageDraw.Draw(glow).ellipse((760, -380, 1580, 300), fill=60)
    img.paste(Image.new("RGB", (w, h), SUN), (0, 0), glow.filter(__import__("PIL.ImageFilter", fromlist=["x"]).GaussianBlur(120)))

    d = ImageDraw.Draw(img)
    img.paste(mark.resize((112, 112), Image.LANCZOS), (72, 74), mark.resize((112, 112), Image.LANCZOS))
    d.text((72, 214), "Solstice", font=font("seguisb.ttf", 76), fill=TEXT)
    lines = ["One-click game streaming from your", "Windows PC to a ROG Ally or any", "Windows handheld. Free & open source."]
    for i, line in enumerate(lines):
        d.text((74, 318 + i * 46), line, font=font("segoeui.ttf", 34), fill=MUTED)
    d.text((74, 486), "Sunshine + Moonlight, set up for you", font=font("seguisb.ttf", 28), fill=SUN)

    # Screenshot peeking in from the right, with a rounded corner and an edge.
    s = shot("pc-home")
    s = s.resize((int(s.width * 560 / s.height), 560), Image.LANCZOS)
    mask = Image.new("L", s.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, s.width - 1, s.height - 1), radius=22, fill=255)
    img.paste(s, (690, 80), mask)
    d.rounded_rectangle((690, 80, 690 + s.width - 1, 80 + s.height - 1), radius=22, outline=(53, 60, 78), width=2)
    img.save(os.path.join(DOCS, "og-image.png"), optimize=True)


def gif():
    W, H, CAP = 960, 640, 56
    title = font("seguisb.ttf", 22)
    sub = font("segoeui.ttf", 18)

    def frame(name, caption, where):
        s = shot(name)
        area_h = H - CAP
        scale = min(W / s.width, area_h / s.height)
        s = s.resize((int(s.width * scale), int(s.height * scale)), Image.LANCZOS)
        f = Image.new("RGB", (W, H), BG)
        f.paste(s, ((W - s.width) // 2, CAP + (area_h - s.height) // 2))
        d = ImageDraw.Draw(f)
        d.text((24, 15), where, font=title, fill=SUN)
        tw = d.textlength(where, font=title)
        d.text((24 + tw + 14, 18), caption, font=sub, fill=MUTED)
        return f

    script = [("pc-welcome", "One click sets everything up", "On your PC", 1800)]
    for i in range(7):
        script.append((f"frame-pc-setup-{i}", "Installs, secures and tunes Sunshine", "On your PC", 650))
    script += [
        ("pc-home", "Ready to stream", "On your PC", 1900),
        ("ally-welcome", "Solstice Play: one tap", "On the ROG Ally", 1700),
        ("ally-pair", "Shows four digits", "On the ROG Ally", 1900),
        ("pc-approve", "Approve the same four digits", "On your PC", 2100),
        ("ally-home", "Pick a game and play", "On the ROG Ally", 2600),
        ("pc-streaming", "Monitor switches to the handheld's mode", "On your PC", 2300),
    ]
    frames = [frame(n, c, w) for n, c, w, _ in script]
    palette_src = Image.new("RGB", (W, H * 3))
    for i, n in enumerate(["pc-home", "ally-home", "pc-library"]):
        palette_src.paste(frame(n, "", "").resize((W, H)), (0, H * i))
    pal = palette_src.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    q = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    q[0].save(os.path.join(DOCS, "demo.gif"), save_all=True, append_images=q[1:],
              duration=[d for *_, d in script], loop=0, optimize=True, disposal=1)


if __name__ == "__main__":
    mark = icon()
    og_image(mark)
    gif()
    for f in glob.glob(os.path.join(SHOTS, "frame-*.png")):
        os.remove(f)
    for f in ["icon.png", "og-image.png", "demo.gif"]:
        print(f, os.path.getsize(os.path.join(DOCS, f)) // 1024, "KB")
