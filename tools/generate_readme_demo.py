"""Render an illustrated motion-design demo of the Windows interface.

The example page is a credited Wikipedia screenshot. The selection surface is
reconstructed from CaptureSurface.cs; this animation is not a screen recording.
Requires Pillow and FFmpeg (with libx264) on PATH.
"""

from __future__ import annotations

import argparse
import math
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
MEDIA = ROOT / "docs" / "media"
REFERENCE = MEDIA / "wikipedia-reference.png"
GIF_PATH = MEDIA / "illustrated-walkthrough.gif"
VIDEO_PATH = MEDIA / "screen-to-search-demo.mp4"
PAGE_W, PAGE_H = 1136, 639
OUTPUT_W, OUTPUT_H = PAGE_W - 20, PAGE_H - 21
FPS, DURATION = 24, 22.0
TEXT_BOX = (393, 162, 578, 185)
IMAGE_BOX = (652, 168, 987, 487)
LASSO = [(657, 175), (980, 170), (987, 438), (944, 484),
         (699, 487), (653, 447), (653, 225), (657, 175)]


def get_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    files = (["C:/Windows/Fonts/segoeuib.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]
             if bold else
             ["C:/Windows/Fonts/segoeui.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"])
    for name in files:
        if Path(name).exists():
            return ImageFont.truetype(name, size)
    raise RuntimeError("Segoe UI or DejaVu Sans is required to render the demo")


FONTS = {size: get_font(size) for size in (12, 13, 14, 15, 16, 17, 18, 20)}
BOLD = {size: get_font(size, True) for size in (12, 13, 14, 16, 18, 20, 24, 29, 34, 44)}


def clamp(value: float, low: float = 0.0, high: float = 1.0) -> float:
    return max(low, min(high, value))


def ease(value: float) -> float:
    value = clamp(value)
    return value * value * (3 - 2 * value)


def tween(time: float, start: float, end: float) -> float:
    return ease((time - start) / (end - start))


def lerp_point(start: tuple[float, float], end: tuple[float, float], amount: float) -> tuple[float, float]:
    return (start[0] + (end[0] - start[0]) * amount,
            start[1] + (end[1] - start[1]) * amount)


def centered(draw: ImageDraw.ImageDraw, rect: tuple[int, int, int, int], text: str,
             font: ImageFont.FreeTypeFont, fill: str | tuple[int, ...]) -> None:
    left, top, right, bottom = rect
    box = draw.textbbox((0, 0), text, font=font)
    draw.text(((left + right - (box[2] - box[0])) / 2,
               (top + bottom - (box[3] - box[1])) / 2 - box[1]), text, font=font, fill=fill)


def cursor_position(time: float) -> tuple[float, float] | None:
    movements = [
        (3.3, 3.95, (735, 280), (397, 174)),
        (4.10, 5.12, (397, 174), (574, 174)),
        (5.55, 6.20, (574, 174), (463, 223)),
        (8.05, 8.55, (463, 223), (211, 223)),
        (10.25, 10.75, (211, 223), (337, 223)),
        (12.55, 13.35, (337, 223), (653, 170)),
        (13.50, 14.65, (653, 170), (987, 487)),
        (14.95, 15.55, (987, 487), (754, 525)),
        (16.05, 16.55, (754, 525), LASSO[0]),
    ]
    if time < movements[0][0] or time >= 19.55:
        return None
    last = movements[0][2]
    for start, end, a, b in movements:
        if time < start:
            return last
        if time < end:
            return lerp_point(a, b, tween(time, start, end))
        last = b
    if time < 18.35:
        progress = clamp((time - 16.55) / 1.80) * (len(LASSO) - 1)
        index = min(int(progress), len(LASSO) - 2)
        return lerp_point(LASSO[index], LASSO[index + 1], ease(progress - index))
    return LASSO[-1]


def draw_cursor(draw: ImageDraw.ImageDraw, point: tuple[float, float], time: float) -> None:
    x, y = point
    for moment in (4.10, 5.12, 13.50, 14.65, 16.55, 18.35):
        age = time - moment
        if 0 <= age < 0.32:
            radius = 4 + 16 * age / 0.32
            draw.ellipse((x - radius, y - radius, x + radius, y + radius),
                         outline=(140, 194, 255), width=2)
    points = [(x + 2, y + 2), (x + 2, y + 25), (x + 8, y + 19),
              (x + 13, y + 29), (x + 17, y + 27), (x + 12, y + 17), (x + 22, y + 17)]
    draw.polygon([(px + 2, py + 2) for px, py in points], fill=(15, 22, 35))
    draw.polygon(points, fill=(255, 255, 255), outline=(12, 24, 43), width=2)


def draw_action_bar(page: Image.Image, x: int, y: int, active: str | None) -> None:
    draw = ImageDraw.Draw(page)
    draw.rectangle((x, y, x + 690, y + 51), fill=(28, 34, 50))
    for index, label in enumerate(("Search text", "Translate", "Copy text", "Copy image")):
        left = x + 8 + index * 126
        selected = label == active
        draw.rectangle((left, y + 8, left + 121, y + 41),
                       fill=(68, 106, 178) if selected else (51, 70, 111),
                       outline=(188, 216, 255) if selected else (126, 148, 183), width=1)
        centered(draw, (left, y + 8, left + 121, y + 41), label, FONTS[13], "white")


def draw_toolbar(page: Image.Image) -> None:
    draw = ImageDraw.Draw(page)
    draw.rectangle((14, 14, 327, 61), fill=(28, 34, 50))
    for index, label in enumerate(("Smart (A)", "Text (T)", "Close")):
        left = 21 + index * 98
        draw.rectangle((left, 21, left + 93, 54), fill=(51, 70, 111), outline=(126, 148, 183), width=1)
        centered(draw, (left, 21, left + 93, 54), label, FONTS[13], "white")


def draw_key_hint(page: Image.Image, text: str) -> None:
    draw = ImageDraw.Draw(page)
    width = 126 if text == "CAPS LOCK" else 74
    x = page.width - width - 20
    draw.rounded_rectangle((x, 17, x + width, 57), radius=8,
                           fill=(24, 38, 61), outline=(120, 174, 246), width=2)
    centered(draw, (x, 17, x + width, 57), text, BOLD[13], "#eaf3ff")


def draw_dashed_path(draw: ImageDraw.ImageDraw, points: list[tuple[float, float]]) -> None:
    for first, second in zip(points, points[1:]):
        dx, dy = second[0] - first[0], second[1] - first[1]
        length = math.hypot(dx, dy)
        if not length:
            continue
        for offset in range(0, math.ceil(length), 14):
            a = offset / length
            b = min((offset + 8) / length, 1)
            draw.line((first[0] + dx * a, first[1] + dy * a,
                       first[0] + dx * b, first[1] + dy * b), fill=(255, 255, 255), width=3)


def partial_lasso(time: float) -> list[tuple[float, float]]:
    progress = clamp((time - 16.55) / 1.80) * (len(LASSO) - 1)
    index = min(int(progress), len(LASSO) - 2)
    return list(LASSO[:index + 1]) + [lerp_point(LASSO[index], LASSO[index + 1], ease(progress - index))]


class DemoRenderer:
    def __init__(self) -> None:
        self.reference = Image.open(REFERENCE).convert("RGB")
        if self.reference.size != (1010, 568):
            raise ValueError("The reference screenshot must be 1010x568 pixels")
        self.dimmed = Image.blend(self.reference, Image.new("RGB", self.reference.size, "black"), 105 / 255)
        self.lasso_mask = Image.new("L", self.reference.size, 0)
        ImageDraw.Draw(self.lasso_mask).polygon(LASSO, fill=255)

    def _page(self, time: float) -> Image.Image:
        dim = tween(time, 1.55, 2.35)
        page = Image.blend(self.reference, self.dimmed, dim)
        draw = ImageDraw.Draw(page)

        if 4.10 <= time < 12.85:
            progress = tween(time, 4.10, 5.12)
            left, top, right, bottom = TEXT_BOX
            right = round(left + (right - left) * progress)
            if right > left:
                box = (left, top, right, bottom)
                page.paste(self.reference.crop(box), box[:2])
                highlight = Image.new("RGBA", page.size, (0, 0, 0, 0))
                ImageDraw.Draw(highlight).rectangle(box, fill=(67, 132, 255, 100))
                page = Image.alpha_composite(page.convert("RGBA"), highlight).convert("RGB")
                draw = ImageDraw.Draw(page)
                draw.rectangle(box, outline=(45, 105, 244), width=4)
                if right - left > 4 and bottom - top > 4:
                    draw.rectangle((left + 2, top + 2, right - 2, bottom - 2), outline="white", width=1)
            if time >= 5.20:
                active = ("Copy text" if 6.20 <= time < 8.05 else
                          "Search text" if 8.55 <= time < 10.25 else
                          "Translate" if 10.75 <= time < 12.55 else None)
                draw_action_bar(page, 140, 197, active)

        if 13.50 <= time < 19.55:
            progress = tween(time, 13.50, 14.65)
            x1, y1, x2, y2 = IMAGE_BOX
            right = round(x1 + (x2 - x1) * progress)
            bottom = round(y1 + (y2 - y1) * progress)
            if right > x1 and bottom > y1:
                if time >= 18.35:
                    page.paste(self.reference, (0, 0), self.lasso_mask)
                    draw = ImageDraw.Draw(page)
                    draw.line(LASSO, fill=(45, 105, 244), width=5, joint="curve")
                    draw.line(LASSO, fill=(255, 255, 255), width=2, joint="curve")
                else:
                    box = (x1, y1, right, bottom)
                    page.paste(self.reference.crop(box), box[:2])
                    draw = ImageDraw.Draw(page)
                    draw.rectangle(box, outline=(45, 105, 244), width=5)
                    if right - x1 > 4 and bottom - y1 > 4:
                        draw.rectangle((x1 + 2, y1 + 2, right - 2, bottom - 2), outline="white", width=2)
            if time >= 14.72:
                draw_action_bar(page, 310, 499, "Copy image" if 15.55 <= time < 16.05 else None)
            if 16.55 <= time < 18.35:
                draw_dashed_path(ImageDraw.Draw(page), partial_lasso(time))

        point = cursor_position(time)
        if point is not None:
            draw_cursor(ImageDraw.Draw(page), point, time)

        # A slow camera push makes the selected words and actions easier to read.
        zoom = 1 + 0.18 * tween(time, 3.30, 4.10) * (1 - tween(time, 12.50, 13.30))
        if zoom > 1.001:
            w, h = page.size
            crop_w, crop_h = w / zoom, h / zoom
            left = max(0, min(w - crop_w, 480 - crop_w / 2))
            top = max(0, min(h - crop_h, 236 - crop_h / 2))
            page = page.crop((round(left), round(top), round(left + crop_w), round(top + crop_h)))
            page = page.resize((w, h), Image.Resampling.BICUBIC)
        if time >= 2.15 and time < 19.55:
            draw_toolbar(page)
        if 1.70 <= time < 3.20:
            draw_key_hint(page, "CAPS LOCK")
        elif 12.85 <= time < 14.00:
            draw_key_hint(page, "R")
        elif 16.20 <= time < 17.30:
            draw_key_hint(page, "L")
        return page

    def frame(self, time: float) -> Image.Image:
        page = self._page(time).resize((PAGE_W, PAGE_H), Image.Resampling.BICUBIC)
        if time >= 19.55:
            opacity = round(205 * tween(time, 19.55, 20.15))
            veil = Image.new("RGBA", (PAGE_W, PAGE_H), (8, 15, 28, opacity))
            page = Image.alpha_composite(page.convert("RGBA"), veil).convert("RGB")
            draw = ImageDraw.Draw(page)
            centered(draw, (0, 215, PAGE_W, 288), "See it. Select it. Do more.", BOLD[44], "#ffffff")
            centered(draw, (0, 291, PAGE_W, 334), "COPY TEXT     ·     SEARCH     ·     TRANSLATE     ·     COPY IMAGE",
                     BOLD[16], "#bfd5f8")
        # Trim the former frame and rounded corners so only the central demo remains.
        return page.crop((10, 10, 10 + OUTPUT_W, 10 + OUTPUT_H))


def preview(renderer: DemoRenderer) -> None:
    times = (0.8, 2.6, 4.6, 6.8, 9.0, 11.3, 15.7, 17.5, 20.7)
    thumb_w = 480
    thumb_h = round(thumb_w * OUTPUT_H / OUTPUT_W)
    sheet = Image.new("RGB", (3 * thumb_w, 3 * thumb_h), "#0b1220")
    for index, time in enumerate(times):
        frame = renderer.frame(time).resize((thumb_w, thumb_h), Image.Resampling.LANCZOS)
        sheet.paste(frame, ((index % 3) * thumb_w, (index // 3) * thumb_h))
    path = Path(tempfile.gettempdir()) / "screen-to-search-motion-preview.png"
    sheet.save(path)
    print(path)


def render_video(renderer: DemoRenderer) -> None:
    if not shutil.which("ffmpeg"):
        raise RuntimeError("FFmpeg must be installed and available on PATH")
    command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-vcodec", "rawvideo",
               "-pix_fmt", "rgb24", "-s", f"{OUTPUT_W}x{OUTPUT_H}", "-r", str(FPS), "-i", "-", "-an",
               "-c:v", "libx264", "-preset", "medium", "-crf", "19", "-pix_fmt", "yuv420p",
               "-movflags", "+faststart", str(VIDEO_PATH)]
    process = subprocess.Popen(command, stdin=subprocess.PIPE)
    try:
        for index in range(round(DURATION * FPS)):
            process.stdin.write(renderer.frame(index / FPS).tobytes())
            if index and index % (FPS * 5) == 0:
                print(f"Rendered {index / FPS:.0f}/{DURATION:.0f} seconds", flush=True)
        process.stdin.close()
        if process.wait() != 0:
            raise RuntimeError("FFmpeg could not encode the demo")
    except BaseException:
        process.kill()
        process.wait()
        raise
    filters = ("[0:v]fps=12,scale=960:-2:flags=lanczos,split[a][b];"
               "[a]palettegen=stats_mode=diff:max_colors=128[p];"
               "[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(VIDEO_PATH),
                    "-filter_complex", filters, "-loop", "0", str(GIF_PATH)], check=True)
    print(f"Wrote {VIDEO_PATH} ({VIDEO_PATH.stat().st_size:,} bytes)")
    print(f"Wrote {GIF_PATH} ({GIF_PATH.stat().st_size:,} bytes)")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", action="store_true", help="render a nine-frame contact sheet only")
    args = parser.parse_args()
    renderer = DemoRenderer()
    if args.preview:
        preview(renderer)
    else:
        render_video(renderer)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError, ValueError) as exc:
        print(f"Demo render failed: {exc}", file=sys.stderr)
        sys.exit(1)
