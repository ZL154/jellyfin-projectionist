"""
Generate Projectionist logo banner.
Style: black surface, subtle red glow border, bold centered white wordmark.
Saves at multiple sizes for README + plugin.
"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import os

OUT_DIR = os.path.dirname(os.path.abspath(__file__))


def find_font(size, bold=True):
    candidates_bold = [
        r"C:\Windows\Fonts\bahnschrift.ttf",   # modern condensed
        r"C:\Windows\Fonts\Impact.ttf",
        r"C:\Windows\Fonts\arialbd.ttf",
    ]
    candidates_reg = [
        r"C:\Windows\Fonts\bahnschrift.ttf",
        r"C:\Windows\Fonts\arial.ttf",
    ]
    for p in (candidates_bold if bold else candidates_reg):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def make_banner(width, height, text="Projectionist", out_path="logo.png"):
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))

    # ---- subtle red border glow ----
    glow_layer = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow_layer)
    border_inset = 8
    gd.rectangle(
        [border_inset, border_inset, width - border_inset, height - border_inset],
        outline=(229, 9, 20, 230), width=2,
    )
    glow = glow_layer.filter(ImageFilter.GaussianBlur(radius=14))

    # ---- solid black inner panel ----
    panel = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    pd = ImageDraw.Draw(panel)
    pad = 12
    pd.rectangle([pad, pad, width - pad, height - pad], fill=(8, 8, 10, 255))
    # thin red interior border
    pd.rectangle([pad, pad, width - pad, height - pad], outline=(120, 0, 8, 255), width=1)

    # ---- compose: glow under panel ----
    img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, panel)

    # ---- text centered ----
    # auto-pick font size to fit ~70% of inner width
    inner_w = width - 2 * pad - 60  # margin for breathing room
    target_h = int(height * 0.42)
    font = find_font(target_h, bold=True)
    d = ImageDraw.Draw(img)
    while target_h > 12:
        font = find_font(target_h, bold=True)
        bbox = d.textbbox((0, 0), text, font=font)
        tw = bbox[2] - bbox[0]
        th = bbox[3] - bbox[1]
        if tw <= inner_w:
            break
        target_h -= 2

    bbox = d.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    tx = (width - tw) // 2 - bbox[0]
    ty = (height - th) // 2 - bbox[1]
    # subtle text shadow
    d.text((tx + 1, ty + 1), text, font=font, fill=(0, 0, 0, 200))
    d.text((tx, ty), text, font=font, fill=(245, 245, 245, 255))

    img.save(out_path)
    print(f"Wrote {out_path} ({width}x{height})")


if __name__ == "__main__":
    # Big banner for README header
    make_banner(960, 320, "Projectionist", os.path.join(OUT_DIR, "logo-banner.png"))
    # Smaller square-ish for repo social/preview
    make_banner(640, 320, "Projectionist", os.path.join(OUT_DIR, "logo-square.png"))
    # Plugin tile (consistent style for Jellyfin plugin browser thumbnail)
    make_banner(512, 256, "Projectionist", os.path.join(OUT_DIR, "logo.png"))
