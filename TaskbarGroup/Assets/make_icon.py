# Draws the app icon: a dark rounded tile holding four colored app squares
# (a group of apps) and a purple accent outline.
# Usage: python make_icon.py app.ico [preview.png]
import sys
from PIL import Image, ImageDraw

COLORS = [(92, 156, 255), (255, 176, 64), (96, 208, 128), (236, 96, 120)]
SIZES = [16, 24, 32, 48, 64, 128, 256]


def draw(size):
    ss = 4  # supersample
    s = size * ss
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = s * 0.04
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.22,
                        fill=(40, 40, 52, 255), outline=(124, 92, 255, 255),
                        width=max(ss, int(s * 0.045)))
    inner = s * (0.2 if size >= 32 else 0.16)
    gap = s * (0.07 if size >= 32 else 0.09)
    cell = (s - 2 * inner - gap) / 2
    for i, c in enumerate(COLORS):
        x = inner + (i % 2) * (cell + gap)
        y = inner + (i // 2) * (cell + gap)
        d.rounded_rectangle([x, y, x + cell, y + cell], radius=cell * 0.25, fill=c + (255,))
    return img.resize((size, size), Image.LANCZOS)


def main():
    frames = [draw(n) for n in SIZES]
    frames[-1].save(sys.argv[1], format="ICO", sizes=[(n, n) for n in SIZES],
                    append_images=frames[:-1])
    if len(sys.argv) > 2:
        frames[-1].save(sys.argv[2])


if __name__ == "__main__":
    main()
