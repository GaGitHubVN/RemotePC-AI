"""Generates every RemotePC AI app icon from one geometry (run: python tools/icons/generate_icons.py).

Outputs
- iOS 26+: apps/mobile/ios/Runner/AppIcon.icon (Icon Composer package: icon.json + SVG layers). The system renders
  the Liquid Glass material, specular highlights and the dark / clear / tinted appearances from the layers.
- iOS 18–25: Assets.xcassets/AppIcon.appiconset single-size 1024 icon with Any / Dark / Tinted appearances. Full-bleed
  and opaque — iOS applies the rounded mask itself (a pre-rounded PNG shows doubled corners).
- Android 8+: adaptive icon (foreground + background + monochrome for Android 13 themed icons) and legacy mipmaps.
- Windows: app.ico (16–256 px).

Geometry is defined once on a 1024×1024 canvas. Requires Pillow (dev-time only).
"""
from __future__ import annotations

import json
import math
import os
import shutil

from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
MOBILE = os.path.join(ROOT, "apps", "mobile")
C = 1024
SS = 4  # supersampling for anti-aliasing

GRAD_A = (40, 84, 255)    # top-left  (brand blue)
GRAD_B = (0, 198, 255)    # bottom-right (cyan)
DARK_A = (24, 30, 54)
DARK_B = (12, 16, 30)

# ---- geometry (1024 canvas) -------------------------------------------------------------------------------------
SCREEN = (212, 262, 812, 662)   # display outline
SCREEN_R = 64
STROKE = 54
NECK = (470, 662, 554, 724)
BASE = (372, 718, 652, 770)
BASE_R = 26
SPARK_C = (512, 462)
SPARK_R = 130                   # outer radius of the 4-point sparkle
SPARK_PINCH = 0.34              # inner radius ratio (concave sides)


def sparkle_points(cx: float, cy: float, r: float, pinch: float, n: int = 64) -> list[tuple[float, float]]:
    """A four-point star with smoothly concave sides (astroid-like), as a polygon."""
    pts = []
    for i in range(n * 4):
        t = 2 * math.pi * i / (n * 4)
        # superellipse |x|^p + |y|^p = r^p with p < 1 gives concave sides; p from pinch
        p = 0.5 + pinch
        c, s = math.cos(t), math.sin(t)
        x = math.copysign(abs(c) ** (2 / p), c) * r
        y = math.copysign(abs(s) ** (2 / p), s) * r
        pts.append((cx + x, cy + y))
    return pts


def gradient(size: int, a, b) -> Image.Image:
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return img


def glyph(size: int, color=(255, 255, 255, 255), scale: float = 1.0, offset=(0, 0)) -> Image.Image:
    """White monitor + AI sparkle on a transparent canvas of `size` (geometry scaled from 1024)."""
    big = size * SS
    k = big / C * scale
    ox = offset[0] * big / C + (big - big * scale) / 2
    oy = offset[1] * big / C + (big - big * scale) / 2
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    T = lambda b: tuple(v * k + (ox if i % 2 == 0 else oy) for i, v in enumerate(b))  # noqa: E731
    d.rounded_rectangle(T(SCREEN), radius=SCREEN_R * k, outline=color, width=round(STROKE * k))
    d.rectangle(T(NECK), fill=color)
    d.rounded_rectangle(T(BASE), radius=BASE_R * k, fill=color)
    d.polygon([(x * k + ox, y * k + oy) for x, y in sparkle_points(*SPARK_C, SPARK_R, SPARK_PINCH)], fill=color)
    return im.resize((size, size), Image.LANCZOS)


def composite(size: int, bg_a, bg_b, fg=(255, 255, 255, 255)) -> Image.Image:
    bg = gradient(size, bg_a, bg_b).convert("RGBA")
    bg.alpha_composite(glyph(size, fg))
    return bg.convert("RGB")


# ---- SVG layers for Icon Composer -------------------------------------------------------------------------------
def svg_screen() -> str:
    x0, y0, x1, y1 = SCREEN
    h = STROKE / 2
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{C}" height="{C}" viewBox="0 0 {C} {C}">'
            f'<rect x="{x0 + h}" y="{y0 + h}" width="{x1 - x0 - STROKE}" height="{y1 - y0 - STROKE}" rx="{SCREEN_R - h}" '
            f'fill="none" stroke="#FFFFFF" stroke-width="{STROKE}"/>'
            f'<rect x="{NECK[0]}" y="{NECK[1]}" width="{NECK[2] - NECK[0]}" height="{NECK[3] - NECK[1]}" fill="#FFFFFF"/>'
            f'<rect x="{BASE[0]}" y="{BASE[1]}" width="{BASE[2] - BASE[0]}" height="{BASE[3] - BASE[1]}" rx="{BASE_R}" fill="#FFFFFF"/>'
            '</svg>\n')


def svg_spark() -> str:
    pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in sparkle_points(*SPARK_C, SPARK_R, SPARK_PINCH))
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{C}" height="{C}" viewBox="0 0 {C} {C}">'
            f'<polygon points="{pts}" fill="#FFFFFF"/></svg>\n')


def p3(rgb) -> str:
    return "display-p3:" + ",".join(f"{v / 255:.5f}" for v in rgb) + ",1.00000"


def write_icon_composer(path: str) -> None:
    if os.path.isdir(path):
        shutil.rmtree(path)
    os.makedirs(os.path.join(path, "Assets"))
    with open(os.path.join(path, "Assets", "screen.svg"), "w", encoding="utf-8") as f:
        f.write(svg_screen())
    with open(os.path.join(path, "Assets", "spark.svg"), "w", encoding="utf-8") as f:
        f.write(svg_spark())
    doc = {
        "fill": {"linear-gradient": [p3(GRAD_A), p3(GRAD_B)]},
        "groups": [
            {
                "name": "AI",
                "layers": [{"name": "spark", "image-name": "spark.svg", "glass": True}],
                "lighting": "individual",
                "shadow": {"kind": "neutral", "opacity": 0.5},
                "specular": True,
                "translucency": {"enabled": True, "value": 0.4},
            },
            {
                "name": "Display",
                "layers": [{"name": "screen", "image-name": "screen.svg", "glass": True}],
                "lighting": "individual",
                "shadow": {"kind": "neutral", "opacity": 0.5},
                "specular": True,
                "translucency": {"enabled": True, "value": 0.3},
            },
        ],
        "supported-platforms": {"circles": ["watchOS"], "squares": "shared"},
    }
    with open(os.path.join(path, "icon.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")


# ---- iOS 18 asset catalog -----------------------------------------------------------------------------------------
def write_appiconset(path: str) -> None:
    for name in os.listdir(path):
        os.remove(os.path.join(path, name))
    composite(1024, GRAD_A, GRAD_B).save(os.path.join(path, "AppIcon-1024.png"))
    composite(1024, DARK_A, DARK_B, fg=(110, 190, 255, 255)).save(os.path.join(path, "AppIcon-1024-dark.png"))
    # Tinted: grayscale glyph on black; the system applies the user's tint.
    tinted = Image.new("RGBA", (1024, 1024), (0, 0, 0, 255))
    tinted.alpha_composite(glyph(1024, (255, 255, 255, 255)))
    tinted.convert("L").save(os.path.join(path, "AppIcon-1024-tinted.png"))
    contents = {
        "images": [
            {"filename": "AppIcon-1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"},
            {"appearances": [{"appearance": "luminosity", "value": "dark"}], "filename": "AppIcon-1024-dark.png",
             "idiom": "universal", "platform": "ios", "size": "1024x1024"},
            {"appearances": [{"appearance": "luminosity", "value": "tinted"}], "filename": "AppIcon-1024-tinted.png",
             "idiom": "universal", "platform": "ios", "size": "1024x1024"},
        ],
        "info": {"author": "xcode", "version": 1},
    }
    with open(os.path.join(path, "Contents.json"), "w", encoding="utf-8") as f:
        json.dump(contents, f, indent=2)
        f.write("\n")


# ---- Android ------------------------------------------------------------------------------------------------------
def write_android(res: str) -> None:
    dens = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}
    for name, k in dens.items():
        d = os.path.join(res, f"mipmap-{name}")
        os.makedirs(d, exist_ok=True)
        # Legacy launcher (Android 7 and older launchers): rounded square.
        s = round(48 * k)
        icon = composite(s * 4, GRAD_A, GRAD_B).resize((s, s), Image.LANCZOS).convert("RGBA")
        mask = Image.new("L", (s * 4, s * 4), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, s * 4 - 1, s * 4 - 1), radius=s * 4 * 0.22, fill=255)
        icon.putalpha(mask.resize((s, s), Image.LANCZOS))
        icon.save(os.path.join(d, "ic_launcher.png"))
        # Adaptive layers are 108 dp with a 72 dp safe zone: shrink the glyph to 66 %.
        a = round(108 * k)
        glyph(a, scale=0.62).save(os.path.join(d, "ic_launcher_foreground.png"))
        glyph(a, scale=0.62).save(os.path.join(d, "ic_launcher_monochrome.png"))
        gradient(a, GRAD_A, GRAD_B).save(os.path.join(d, "ic_launcher_background.png"))
    any26 = os.path.join(res, "mipmap-anydpi-v26")
    os.makedirs(any26, exist_ok=True)
    with open(os.path.join(any26, "ic_launcher.xml"), "w", encoding="utf-8") as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n'
                '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
                '    <background android:drawable="@mipmap/ic_launcher_background"/>\n'
                '    <foreground android:drawable="@mipmap/ic_launcher_foreground"/>\n'
                '    <monochrome android:drawable="@mipmap/ic_launcher_monochrome"/>\n'
                '</adaptive-icon>\n')


def write_ico(path: str) -> None:
    img = composite(1024, GRAD_A, GRAD_B).convert("RGBA")
    mask = Image.new("L", (1024, 1024), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, 1023, 1023), radius=200, fill=255)
    img.putalpha(mask)
    img.save(path, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    ios = os.path.join(MOBILE, "ios", "Runner")
    write_icon_composer(os.path.join(ios, "AppIcon.icon"))
    write_appiconset(os.path.join(ios, "Assets.xcassets", "AppIcon.appiconset"))
    write_android(os.path.join(MOBILE, "android", "app", "src", "main", "res"))
    write_ico(os.path.join(ROOT, "apps", "windows-host", "src", "RemotePC.Host", "Assets", "app.ico"))
    composite(1024, GRAD_A, GRAD_B).save(os.path.join(ROOT, "tools", "icons", "preview.png"))
    print("icons written")
