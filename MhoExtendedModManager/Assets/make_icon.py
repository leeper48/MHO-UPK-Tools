"""
MHO Extended Mod Manager app icon: a comic "POW" burst (yellow, black outline, red inner ring) with a bold "M".
Each size is drawn on its own (fewer spikes and thicker lines when small, halftone dots and a drop shadow only when
large) at 4x and scaled down, so 16 px stays a clean readable shape.
Usage: python make_icon.py <out.ico> [preview.png]
"""
import math, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

YELLOW = (255, 214, 0, 255)
RED = (228, 30, 37, 255)
BLACK = (18, 18, 18, 255)
WHITE = (255, 255, 255, 255)
CYAN_DOT = (255, 170, 0, 255)
FONT = r"C:\Windows\Fonts\impact.ttf"


def burst(cx, cy, r_out, r_in, spikes, rot=-math.pi / 2, jitter=None):
    pts = []
    for i in range(spikes * 2):
        a = rot + i * math.pi / spikes
        r = r_out if i % 2 == 0 else r_in
        if jitter and i % 2 == 0:
            r *= jitter[(i // 2) % len(jitter)]
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def draw(size):
    S = 4 * size                      # supersampled canvas
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = S / 2
    small = size <= 24
    spikes = 8 if small else 12 if size <= 40 else 14
    jitter = None if size <= 40 else [1.0, 0.92, 1.0, 0.95, 0.9, 1.0, 0.94]
    r_out = S * 0.50
    r_in = S * (0.41 if small else 0.38)
    line = S * (0.075 if size <= 16 else 0.06 if small else 0.045 if size <= 40 else 0.03)

    # Drop shadow (large sizes): the burst, offset down-right, black and soft.
    if size >= 48:
        sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(sh).polygon(burst(c + S * 0.03, c + S * 0.035, r_out * 0.97, r_in * 0.97, spikes, jitter=jitter), fill=(0, 0, 0, 150))
        img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(S * 0.012)))
        d = ImageDraw.Draw(img)

    outer = burst(c, c, r_out * 0.97, r_in, spikes, jitter=jitter)
    d.polygon(outer, fill=BLACK)
    inner = burst(c, c, r_out * 0.97 - line * 1.2, r_in - line * 1.2, spikes, jitter=jitter)
    d.polygon(inner, fill=YELLOW)

    # Halftone dots on the yellow (large sizes only): the comic-print look.
    if size >= 48:
        dots = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        dd = ImageDraw.Draw(dots)
        step = S / 18
        for y in range(-1, 20):
            for x in range(-1, 20):
                px, py = x * step + (step / 2 if y % 2 else 0), y * step
                dist = math.hypot(px - c, py - c) / (S / 2)
                rad = step * 0.30 * max(0.0, dist - 0.25)
                if rad > 0.5:
                    dd.ellipse([px - rad, py - rad, px + rad, py + rad], fill=CYAN_DOT)
        mask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(mask).polygon(inner, fill=255)
        img.paste(dots, (0, 0), Image.composite(dots, Image.new("RGBA", (S, S)), mask).split()[3])
        d = ImageDraw.Draw(img)

    # Small sizes: a black "M" straight on the yellow (the most contrast a few pixels can carry). Larger: a red disc
    # with a black ring and a white "M" outlined in black, like a comic sound effect.
    if not small:
        rr = S * 0.27
        d.ellipse([c - rr - line, c - rr - line, c + rr + line, c + rr + line], fill=BLACK)
        d.ellipse([c - rr, c - rr, c + rr, c + rr], fill=RED)
    fs = int(S * (0.58 if small else 0.40))
    font = ImageFont.truetype(FONT, fs)
    txt = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    td = ImageDraw.Draw(txt)
    stroke = 0 if small else max(2, int(line * 0.55))
    bbox = td.textbbox((0, 0), "M", font=font, stroke_width=stroke)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    td.text((c - w / 2 - bbox[0], c - h / 2 - bbox[1]), "M", font=font, fill=BLACK if small else WHITE, stroke_width=stroke, stroke_fill=BLACK)
    if not small:
        txt = txt.rotate(-8, resample=Image.BICUBIC, center=(c, c))
    img.alpha_composite(txt)

    return img.resize((size, size), Image.LANCZOS)


def main():
    out = sys.argv[1]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [draw(s) for s in sizes]
    frames[-1].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    if len(sys.argv) > 2:
        # Preview: every size on a dark and a light background, plus 16/24/32 magnified 4x.
        W = sum(sizes) + 10 * (len(sizes) + 1)
        sheet = Image.new("RGBA", (max(W, 700), 256 * 2 + 40 + 140), (0, 0, 0, 255))
        for row, bg in enumerate([(30, 30, 30, 255), (240, 240, 240, 255)]):
            ImageDraw.Draw(sheet).rectangle([0, row * 276, sheet.width, row * 276 + 266], fill=bg)
            x = 10
            for f in frames:
                sheet.alpha_composite(f, (x, row * 276 + 5))
                x += f.width + 10
        x = 10
        for f in frames[:4]:
            big = f.resize((f.width * 4, f.height * 4), Image.NEAREST)
            sheet.alpha_composite(big, (x, 2 * 276 + 5))
            x += big.width + 20
        sheet.save(sys.argv[2])


if __name__ == "__main__":
    main()
