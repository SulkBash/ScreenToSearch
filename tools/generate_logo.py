"""Regenerate the vector logo and Windows icon. Requires Pillow."""

from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "ScreenToSearch" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96" role="img" aria-label="Screen to Search">
  <rect width="96" height="96" rx="23" fill="#111925"/>
  <circle cx="45" cy="43" r="25" fill="none" stroke="#77AFFF" stroke-width="9"/>
  <path d="M63 62 77 76" fill="none" stroke="#EAF3FF" stroke-width="9" stroke-linecap="round"/>
  <circle cx="45" cy="43" r="6" fill="#73DED0"/>
</svg>
"""
(ASSETS / "ScreenToSearch.svg").write_text(SVG, encoding="utf-8")


def render(size: int) -> Image.Image:
    scale = size / 96
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    box = lambda values: tuple(round(value * scale) for value in values)
    draw.rounded_rectangle(box((0, 0, 95, 95)), radius=round(23 * scale), fill="#111925")
    draw.ellipse(box((15.5, 13.5, 74.5, 72.5)), outline="#77AFFF", width=round(9 * scale))
    draw.line(box((63, 62, 77, 76)), fill="#EAF3FF", width=round(9 * scale))
    radius = 4.5
    for x, y in ((63, 62), (77, 76)):
        draw.ellipse(box((x - radius, y - radius, x + radius, y + radius)), fill="#EAF3FF")
    draw.ellipse(box((39, 37, 51, 49)), fill="#73DED0")
    return image


master = render(1536)
master.resize((512, 512), Image.Resampling.LANCZOS).save(ASSETS / "ScreenToSearch.png")
master.save(
    ASSETS / "ScreenToSearch.ico",
    format="ICO",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
