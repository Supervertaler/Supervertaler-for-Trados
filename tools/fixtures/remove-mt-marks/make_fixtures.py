"""Builds the .sdlxliff fixtures for tools/remove_mt_marks_test.ps1.

The two source files are the anonymised "Acme PROJ-001" test documents (Dutch
kettle and bicycle sentences) that memoQ 12.6 wrote into a Trados return package
on 2026-11-23 (Supervertaler for memoQ, tools/fixtures/trados-return-with-mt.sdlrpx).
Each fixture replaces the segment definitions of the six segments with one case
apiece, and two add a comment and a tracked change, so the test runs every case
through Studio's own .sdlxliff reader and writer.

    python tools/fixtures/remove-mt-marks/make_fixtures.py
"""
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
SDL_DOC_INFO = (
    '<doc-info xmlns="http://sdl.com/FileTypes/SdlXliff/1.0">'
    '<cmt-defs><cmt-def id="c-acme-1"><Comments xmlns=""><Comment severity="Medium" user="Tester" '
    'date="2026-11-24T10:00:00" version="1.0">Check this term with Acme</Comment></Comments></cmt-def></cmt-defs>'
    '<rev-defs><rev-def id="r-acme-1" type="Insert" author="Tester" date="11/24/2026 10:00:00" /></rev-defs>'
    '</doc-info>'
)


def load(name):
    return (HERE / name).read_text(encoding="utf-8-sig")


def set_seg(text, mid, seg_def):
    """Replaces the sdl:seg of segment `mid` with `seg_def`."""
    pattern = re.compile(r'<sdl:seg id="%d" [^>]*?(?:/>|>.*?</sdl:seg>)' % mid, re.S)
    new, n = pattern.subn(lambda m: seg_def, text, count=1)
    assert n == 1, mid
    return new


def set_target(text, mid, inner):
    pattern = re.compile(r'(<target><mrk mtype="seg" mid="%d">).*?(</mrk></target>)' % mid, re.S)
    new, n = pattern.subn(lambda m: m.group(1) + inner + m.group(2), text, count=1)
    assert n == 1, mid
    return new


def save(name, text):
    # As Studio and memoQ write them: UTF-8 with a byte-order mark.
    (HERE / name).write_bytes(b"\xef\xbb\xbf" + text.encode("utf-8"))


# ---- cases-A: what is removed, and what stays around it ------------------------
a = load("source-Pakket-test-A.docx.sdlxliff")
a = a.replace('sdl:version="1.0">', 'sdl:version="1.0">' + SDL_DOC_INFO, 1)
a = set_seg(a, 1, '<sdl:seg id="1" conf="Translated" origin="interactive">'
                  '<sdl:prev-origin origin="mt" origin-system="MT / Supervertaler" percent="0" /></sdl:seg>')
a = set_seg(a, 2, '<sdl:seg id="2" conf="Translated" origin="nmt" origin-system="Language Weaver" percent="0" />')
a = set_target(a, 2, '<mrk mtype="x-sdl-comment" sdl:cid="c-acme-1">Never fill</mrk> the kettle above the maximum mark.')
a = set_seg(a, 3, '<sdl:seg id="3" conf="Translated" origin="amt" origin-system="AdaptiveMT" />')
a = set_seg(a, 4, '<sdl:seg id="4" conf="Translated" origin="interactive">'
                  '<sdl:prev-origin origin="mt" origin-system="DeepL">'
                  '<sdl:prev-origin origin="tm" origin-system="Acme TM" percent="75" /></sdl:prev-origin></sdl:seg>')
a = set_target(a, 4, 'Press the <mrk mtype="x-sdl-added" sdl:revid="r-acme-1">power </mrk>switch to heat the water.')
a = set_seg(a, 5, '<sdl:seg id="5" conf="Translated" origin="mt" locked="true" />')
a = set_seg(a, 6, '<sdl:seg id="6" conf="ApprovedTranslation" origin="mt" origin-system="Google">'
                  '<sdl:prev-origin origin="nmt" origin-system="Language Weaver" /></sdl:seg>')
save("cases-A.sdlxliff", a)

# ---- cases-B: what is not MT stays as it was ------------------------------------
b = load("source-Pakket-test-B.docx.sdlxliff")
b = set_seg(b, 1, '<sdl:seg id="1" conf="Translated" origin="tm" origin-system="Acme TM" percent="100" />')
b = set_seg(b, 2, '<sdl:seg id="2" conf="Translated" origin="interactive">'
                  '<sdl:prev-origin origin="tm" origin-system="Acme TM" percent="85" /></sdl:seg>')
b = set_seg(b, 3, '<sdl:seg id="3" conf="Translated" origin="tm" origin-system="Acme TM" percent="100">'
                  '<sdl:prev-origin origin="interactive"><sdl:prev-origin origin="mt" /></sdl:prev-origin></sdl:seg>')
b = set_seg(b, 4, '<sdl:seg id="4" conf="Translated" origin="mt" origin-system="DeepL">'
                  '<sdl:value key="created_by">ACME\\tester</sdl:value>'
                  '<sdl:value key="created_on">11/20/2026 09:00:00</sdl:value></sdl:seg>')
b = set_seg(b, 5, '<sdl:seg id="5" conf="Draft" origin="mt" />')
b = set_seg(b, 6, '<sdl:seg id="6" conf="Translated" origin="nmt" />')
save("cases-B.sdlxliff", b)

# ---- no-mt: nothing to remove, so the task must leave it byte for byte ----------
n = load("source-Pakket-test-A.docx.sdlxliff")
for mid in range(1, 7):
    n = set_seg(n, mid, '<sdl:seg id="%d" conf="Translated" origin="tm" origin-system="Acme TM" percent="100" />' % mid)
n = set_seg(n, 1, '<sdl:seg id="1" conf="Translated" origin="interactive">'
                  '<sdl:prev-origin origin="tm" percent="85" /></sdl:seg>')
save("no-mt.sdlxliff", n)

# ---- locked-only: the only MT is locked, so there is nothing to remove either ---
lo = load("source-Pakket-test-B.docx.sdlxliff")
for mid in range(1, 7):
    lo = set_seg(lo, mid, '<sdl:seg id="%d" conf="Translated" origin="tm" percent="100" />' % mid)
lo = set_seg(lo, 3, '<sdl:seg id="3" conf="Translated" origin="mt" locked="true" />')
save("locked-only.sdlxliff", lo)

print("written:", ", ".join(p.name for p in sorted(HERE.glob("*.sdlxliff")) if not p.name.startswith("source-")))
