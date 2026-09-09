# Draws the four dockable-pane glyphs into src/Supervertaler.Trados/Resources/icons.
#
# They are drawn at 8x and downsampled so the 32 px file still reads when Studio
# scales it to the 16 px the tab strip draws. The colour is sampled from
# Resources/sv-icon.ico, and it is light enough to stay visible on Studio's dark
# themes as well as the light ones.
#
# The PNGs are checked in; this script only has to run when a glyph changes.
# tools/make_plugin_resources.ps1 is what turns them into the bundle Studio reads.
#
#   python tools/make_pane_icons.py
import os
from PIL import Image, ImageDraw

BLUE = (30, 136, 229, 255)
S = 256          # working canvas
W = 22           # stroke at working size -> ~2.75 px at 32
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(HERE, 'src', 'Supervertaler.Trados', 'Resources', 'icons')


def canvas():
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    return im, ImageDraw.Draw(im)


def termlens():
    """Two term chips, stacked and offset - what the panel itself shows."""
    im, d = canvas()
    d.rounded_rectangle([26, 58, 174, 122], radius=32, outline=BLUE, width=W)
    d.rounded_rectangle([82, 134, 230, 198], radius=32, outline=BLUE, width=W)
    return im


def termpicker():
    """A list: three rows, each a bullet and a bar."""
    im, d = canvas()
    for y in (62, 122, 182):
        d.ellipse([32, y - 14, 60, y + 14], fill=BLUE)
        d.rounded_rectangle([88, y - 13, 226, y + 13], radius=13, fill=BLUE)
    return im


def supersearch():
    """A magnifier."""
    im, d = canvas()
    d.ellipse([34, 34, 178, 178], outline=BLUE, width=W)
    d.line([160, 160, 220, 220], fill=BLUE, width=26)
    return im


def assistant():
    """A speech bubble."""
    im, d = canvas()
    d.rounded_rectangle([26, 40, 230, 176], radius=42, outline=BLUE, width=W)
    d.polygon([(78, 168), (78, 228), (134, 168)], fill=BLUE)
    d.rectangle([78, 156, 134, 176], fill=BLUE)
    return im


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, glyph in [('termlens', termlens), ('termpicker', termpicker),
                        ('supersearch', supersearch), ('assistant', assistant)]:
        path = os.path.join(OUT, name + '.png')
        glyph().resize((32, 32), Image.LANCZOS).save(path)
        print('wrote', path)

    # The official badge, kept alongside as the one-mark-for-everything
    # alternative: point make_plugin_resources.ps1 at this stem instead.
    ico = os.path.join(HERE, 'src', 'Supervertaler.Trados', 'Resources', 'sv-icon.ico')
    sv = os.path.join(OUT, 'supervertaler.png')
    Image.open(ico).convert('RGBA').resize((32, 32), Image.LANCZOS).save(sv)
    print('wrote', sv)


if __name__ == '__main__':
    main()
