from __future__ import annotations

import io
import struct
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "TransReader.App" / "Assets"
SOURCE = ASSETS / "AppIcon-512.png"
SIZES = (16, 20, 24, 32, 48, 64, 128, 256, 512)


def render_frame(source: Image.Image, size: int) -> Image.Image:
    # Each Windows slot gets its own optical crop and sharpening. Small frames
    # intentionally fill more of the canvas and suppress sub-pixel shadow noise.
    crop_ratio = {16: 0.038, 20: 0.032, 24: 0.026, 32: 0.018}.get(size, 0.0)
    inset = round(source.width * crop_ratio)
    working = source.crop((inset, inset, source.width - inset, source.height - inset))
    frame = working.resize((size, size), Image.Resampling.LANCZOS)

    if size <= 24:
        rgb = Image.merge("RGB", frame.convert("RGB").split())
        rgb = ImageEnhance.Contrast(rgb).enhance(1.10)
        rgb = ImageEnhance.Color(rgb).enhance(1.04)
        rgb = rgb.filter(ImageFilter.UnsharpMask(radius=0.72, percent=145, threshold=2))
        # Keep the resampled alpha edge paired with its RGB pixels. Expanding
        # alpha alone creates dark/cyan halos in the 16–24 px Windows slots.
        alpha = frame.getchannel("A")
        frame = Image.merge("RGBA", (*rgb.split(), alpha))
    elif size <= 64:
        frame = frame.filter(ImageFilter.UnsharpMask(radius=0.85, percent=115, threshold=2))
    else:
        frame = frame.filter(ImageFilter.UnsharpMask(radius=1.0, percent=65, threshold=3))
    return frame


def write_ico(frames: list[tuple[int, bytes]], path: Path) -> None:
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = []
    payload = []
    for size, data in frames:
        dimension = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset))
        payload.append(data)
        offset += len(data)
    path.write_bytes(header + b"".join(entries) + b"".join(payload))


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    png_frames: list[tuple[int, bytes]] = []
    for size in SIZES:
        frame = render_frame(source, size)
        output = ASSETS / f"AppIcon-{size}.png"
        frame.save(output, format="PNG", optimize=True)
        if size <= 256:
            buffer = io.BytesIO()
            frame.save(buffer, format="PNG", optimize=True)
            png_frames.append((size, buffer.getvalue()))
    write_ico(png_frames, ASSETS / "AppIcon.ico")


if __name__ == "__main__":
    main()
