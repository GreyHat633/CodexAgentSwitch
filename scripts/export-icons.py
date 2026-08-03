from __future__ import annotations

from pathlib import Path
from typing import Callable

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "assets" / "branding"
PNG_DIR = BRAND / "png"
CONCEPT_DIR = ROOT / "docs" / "branding" / "concepts"
CANVAS = 512
SCALE = 4


def _point(value: float) -> int:
    return round(value * SCALE)


def _xy(values: tuple[float, ...]) -> tuple[int, ...]:
    return tuple(_point(value) for value in values)


def _background(top: str, bottom: str) -> Image.Image:
    size = CANVAS * SCALE
    gradient = Image.new("RGBA", (size, size))
    pixels = gradient.load()
    top_rgb = tuple(int(top[index : index + 2], 16) for index in (1, 3, 5))
    bottom_rgb = tuple(int(bottom[index : index + 2], 16) for index in (1, 3, 5))
    for y in range(size):
        ratio = y / max(1, size - 1)
        color = tuple(round(a + (b - a) * ratio) for a, b in zip(top_rgb, bottom_rgb)) + (255,)
        for x in range(size):
            pixels[x, y] = color

    mask = Image.new("L", (size, size))
    ImageDraw.Draw(mask).rounded_rectangle(_xy((22, 22, 490, 490)), radius=_point(112), fill=255)
    transparent = Image.new("RGBA", (size, size))
    transparent.paste(gradient, mask=mask)
    ImageDraw.Draw(transparent).rounded_rectangle(
        _xy((31, 31, 481, 481)),
        radius=_point(103),
        outline=(255, 255, 255, 38),
        width=_point(6),
    )
    return transparent


def _node(draw: ImageDraw.ImageDraw, center: tuple[int, int], radius: int = 35) -> None:
    x, y = center
    draw.ellipse(_xy((x - radius, y - radius, x + radius, y + radius)), fill="#F7FBFF")
    inner = round(radius * 0.36)
    draw.ellipse(_xy((x - inner, y - inner, x + inner, y + inner)), fill="#62E6D5")


def draw_switchboard() -> Image.Image:
    image = _background("#182C67", "#583DB7")
    draw = ImageDraw.Draw(image)
    line = "#DDE8FF"
    draw.line([_xy((213, 218)), _xy((128, 132))], fill=line, width=_point(28))
    draw.line([_xy((299, 218)), _xy((384, 132))], fill=line, width=_point(28))
    draw.line([_xy((256, 304)), _xy((256, 404))], fill=line, width=_point(28))
    _node(draw, (128, 132))
    _node(draw, (384, 132))
    _node(draw, (256, 404))
    draw.rounded_rectangle(_xy((174, 188, 338, 324)), radius=_point(48), fill="#F8FBFF")
    accent = "#197C9C"
    draw.line([_xy((210, 235)), _xy((295, 235))], fill=accent, width=_point(18))
    draw.polygon([_xy((295, 215)), _xy((320, 235)), _xy((295, 255))], fill=accent)
    draw.line([_xy((302, 278)), _xy((217, 278))], fill=accent, width=_point(18))
    draw.polygon([_xy((217, 258)), _xy((192, 278)), _xy((217, 298))], fill=accent)
    return image


def draw_relay_orbit() -> Image.Image:
    image = _background("#12395C", "#176B73")
    draw = ImageDraw.Draw(image)
    ring = _xy((104, 104, 408, 408))
    draw.arc(ring, 202, 345, fill="#F5FBFF", width=_point(28))
    draw.arc(ring, 22, 165, fill="#F5FBFF", width=_point(28))
    draw.polygon([_xy((109, 285)), _xy((84, 326)), _xy((132, 322))], fill="#F5FBFF")
    draw.polygon([_xy((403, 227)), _xy((428, 186)), _xy((380, 190))], fill="#F5FBFF")
    _node(draw, (256, 118), 40)
    _node(draw, (145, 350), 40)
    _node(draw, (367, 350), 40)
    draw.ellipse(_xy((211, 211, 301, 301)), fill="#F7FBFF")
    draw.rounded_rectangle(_xy((238, 232, 274, 280)), radius=_point(10), fill="#197C9C")
    return image


def draw_console_fork() -> Image.Image:
    image = _background("#291C55", "#28499B")
    draw = ImageDraw.Draw(image)
    line = "#F2F7FF"
    draw.line([_xy((256, 383)), _xy((256, 246)), _xy((146, 144))], fill=line, width=_point(30), joint="curve")
    draw.line([_xy((256, 246)), _xy((366, 144))], fill=line, width=_point(30), joint="curve")
    draw.polygon([_xy((122, 120)), _xy((172, 130)), _xy((137, 166))], fill=line)
    draw.polygon([_xy((390, 120)), _xy((340, 130)), _xy((375, 166))], fill=line)
    _node(draw, (256, 246), 45)
    draw.rounded_rectangle(_xy((164, 352, 348, 432)), radius=_point(28), fill="#F7FBFF")
    draw.line([_xy((200, 379)), _xy((220, 393)), _xy((200, 407))], fill="#197C9C", width=_point(12), joint="curve")
    draw.rounded_rectangle(_xy((235, 400, 309, 412)), radius=_point(6), fill="#62E6D5")
    return image


def _svg_switchboard() -> str:
    return """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#182C67"/><stop offset="1" stop-color="#583DB7"/></linearGradient></defs>
  <rect x="22" y="22" width="468" height="468" rx="112" fill="url(#bg)"/>
  <rect x="31" y="31" width="450" height="450" rx="103" fill="none" stroke="#FFFFFF" stroke-opacity=".15" stroke-width="6"/>
  <g fill="none" stroke="#DDE8FF" stroke-width="28" stroke-linecap="round"><path d="M213 218 128 132"/><path d="m299 218 85-86"/><path d="M256 304v100"/></g>
  <g fill="#F7FBFF" stroke="#62E6D5" stroke-width="25"><circle cx="128" cy="132" r="35"/><circle cx="384" cy="132" r="35"/><circle cx="256" cy="404" r="35"/></g>
  <rect x="174" y="188" width="164" height="136" rx="48" fill="#F8FBFF"/>
  <g fill="none" stroke="#197C9C" stroke-width="18" stroke-linecap="round"><path d="M210 235h85"/><path d="M302 278h-85"/></g>
  <g fill="#197C9C"><path d="m295 215 25 20-25 20z"/><path d="m217 258-25 20 25 20z"/></g>
</svg>\n"""


def _svg_relay() -> str:
    return """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#12395C"/><stop offset="1" stop-color="#176B73"/></linearGradient></defs><rect x="22" y="22" width="468" height="468" rx="112" fill="url(#bg)"/><g fill="none" stroke="#F5FBFF" stroke-width="28"><path d="M109 285a152 152 0 0 0 257 78"/><path d="M403 227a152 152 0 0 0-257-78"/></g><g fill="#F5FBFF"><path d="m109 285-25 41 48-4z"/><path d="m403 227 25-41-48 4z"/></g><g fill="#F7FBFF" stroke="#62E6D5" stroke-width="28"><circle cx="256" cy="118" r="40"/><circle cx="145" cy="350" r="40"/><circle cx="367" cy="350" r="40"/></g><circle cx="256" cy="256" r="45" fill="#F7FBFF"/><rect x="238" y="232" width="36" height="48" rx="10" fill="#197C9C"/></svg>\n"""


def _svg_console() -> str:
    return """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#291C55"/><stop offset="1" stop-color="#28499B"/></linearGradient></defs><rect x="22" y="22" width="468" height="468" rx="112" fill="url(#bg)"/><g fill="none" stroke="#F2F7FF" stroke-width="30" stroke-linecap="round" stroke-linejoin="round"><path d="M256 383V246L146 144"/><path d="M256 246 366 144"/></g><g fill="#F2F7FF"><path d="m122 120 50 10-35 36z"/><path d="m390 120-50 10 35 36z"/></g><circle cx="256" cy="246" r="45" fill="#F7FBFF" stroke="#62E6D5" stroke-width="30"/><rect x="164" y="352" width="184" height="80" rx="28" fill="#F7FBFF"/><path d="m200 379 20 14-20 14" fill="none" stroke="#197C9C" stroke-width="12" stroke-linecap="round" stroke-linejoin="round"/><rect x="235" y="400" width="74" height="12" rx="6" fill="#62E6D5"/></svg>\n"""


def _save_png(image: Image.Image, path: Path, size: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.resize((size, size), Image.Resampling.LANCZOS).save(path, optimize=True)


def main() -> None:
    BRAND.mkdir(parents=True, exist_ok=True)
    PNG_DIR.mkdir(parents=True, exist_ok=True)
    CONCEPT_DIR.mkdir(parents=True, exist_ok=True)

    concepts: list[tuple[str, Callable[[], Image.Image], str]] = [
        ("concept-a-switchboard", draw_switchboard, _svg_switchboard()),
        ("concept-b-relay-orbit", draw_relay_orbit, _svg_relay()),
        ("concept-c-console-fork", draw_console_fork, _svg_console()),
    ]
    for name, renderer, svg in concepts:
        (CONCEPT_DIR / f"{name}.svg").write_text(svg, encoding="utf-8")
        _save_png(renderer(), CONCEPT_DIR / f"{name}.png", 512)

    selected = draw_switchboard()
    (BRAND / "AppIcon.svg").write_text(_svg_switchboard(), encoding="utf-8")
    png_sizes = (16, 20, 24, 32, 40, 44, 48, 64, 96, 128, 150, 256, 310, 512)
    for size in png_sizes:
        _save_png(selected, PNG_DIR / f"AppIcon-{size}.png", size)

    selected.resize((256, 256), Image.Resampling.LANCZOS).save(
        BRAND / "AppIcon.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
