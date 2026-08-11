"""Generate the app icon (src/Assets/app.ico) + a PNG preview for the README.

The mark: a rounded-square "screen" holding a luminance ramp — deep shadow on the left,
blown-out highlight on the right — which is exactly what an HDR tone mapper controls, with a
bright bloom dot for peak brightness. That reads as "display + dynamic range" at any size,
where a literal sun or a bare letter would not.

Usage: python tools/gen_icon.py
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZES = (256, 128, 64, 48, 32, 24, 16)
SS = 8  # supersample factor: draw big, downscale — keeps curves clean at 16px

BG_TOP = (26, 29, 36)
BG_BOTTOM = (14, 16, 20)
RAMP = [(20, 22, 28), (86, 46, 20), (214, 106, 32), (255, 150, 60), (255, 226, 190), (255, 255, 255)]


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def ramp_color(t: float):
    """Sample the luminance ramp at 0..1."""
    t = max(0.0, min(1.0, t))
    seg = t * (len(RAMP) - 1)
    i = min(int(seg), len(RAMP) - 2)
    return lerp(RAMP[i], RAMP[i + 1], seg - i)


def rounded_mask(size: int, radius: int) -> Image.Image:
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def build(size: int) -> Image.Image:
    S = size * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # body: vertical dark gradient
    for y in range(S):
        d.line([(0, y), (S, y)], fill=lerp(BG_TOP, BG_BOTTOM, y / S) + (255,))

    # luminance ramp band across the lower half — the HDR idea, shadow -> highlight
    band_top, band_bot = int(S * 0.56), int(S * 0.78)
    for x in range(S):
        t = x / S
        # ease so the highlight end reads brighter
        d.line([(x, band_top), (x, band_bot)], fill=ramp_color(t ** 0.72) + (255,))

    # peak-brightness bloom sitting on the bright end of the ramp
    bloom = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bloom)
    cx, cy, r = int(S * 0.775), int(S * 0.67), int(S * 0.115)
    bd.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 244, 224, 255))
    bloom = bloom.filter(ImageFilter.GaussianBlur(S * 0.020))
    img = Image.alpha_composite(img, bloom)
    d = ImageDraw.Draw(img)
    d.ellipse([cx - int(r * 0.55), cy - int(r * 0.55), cx + int(r * 0.55), cy + int(r * 0.55)],
              fill=(255, 255, 255, 255))

    # "R" above the ramp, drawn as geometry so it never depends on an installed font
    white = (246, 249, 255, 255)
    stroke = int(S * 0.088)
    left = int(S * 0.255)
    top = int(S * 0.150)
    bot = int(S * 0.500)
    right = int(S * 0.615)
    bowl_bot = top + int((bot - top) * 0.56)          # where the bowl closes
    outer_r = (bowl_bot - top) // 2

    # stem
    d.rectangle([left, top, left + stroke, bot], fill=white)
    # bowl: draw the ring on its own layer and punch the counter out with a mask,
    # because drawing a transparent rectangle would not erase what is underneath
    bowl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bdraw = ImageDraw.Draw(bowl)
    bdraw.rounded_rectangle([left, top, right, bowl_bot], radius=outer_r, fill=white)
    hole = Image.new("L", (S, S), 255)
    ImageDraw.Draw(hole).rounded_rectangle(
        [left + stroke, top + stroke, right - stroke, bowl_bot - stroke],
        radius=max(1, outer_r - stroke), fill=0)
    bowl.putalpha(Image.composite(bowl.getchannel("A"), Image.new("L", (S, S), 0), hole))
    img = Image.alpha_composite(img, bowl)
    d = ImageDraw.Draw(img)
    # leg: a parallelogram from the bowl's join down to the baseline
    leg_x = left + int(S * 0.150)
    d.polygon(
        [(leg_x, bowl_bot - stroke),
         (leg_x + stroke, bowl_bot - stroke),
         (right, bot),
         (right - stroke, bot)],
        fill=white)

    # clip to rounded square + soft inner border
    img.putalpha(rounded_mask(S, int(S * 0.22)))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22),
                        outline=(255, 255, 255, 26), width=max(1, int(S * 0.008)))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    assets = root / "src" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)

    frames = [build(s) for s in SIZES]
    ico = assets / "app.ico"
    frames[0].save(ico, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"{ico} ({ico.stat().st_size} bytes, {len(SIZES)} tamanhos)")

    # Window.Icon usa PNG: o decodificador ICO do WPF falha com frames PNG-comprimidos
    png_icon = assets / "app.png"
    build(256).save(png_icon)
    print(f"{png_icon}")

    docs = root / "docs"
    docs.mkdir(exist_ok=True)
    png = docs / "icon.png"
    build(256).save(png)
    print(f"{png}")


if __name__ == "__main__":
    main()
