"""Patch plugin.xml in the Studio 19 build output for Trados Studio 2026.

Two rewrites are applied to the build-output copy only (never the source tree):

  (a) Sdl.* framework assembly refs. The canonical source plugin.xml references
      ``Sdl.*, Version=18.0.0.0`` (Trados Studio 2024). Studio 2026 ships the same
      Sdl.* assemblies at ``Version=19.0.0.0`` (same PublicKeyToken, verified by
      inspecting Studio19Beta/Sdl.TranslationResourcesApi.dll), so a textual
      18.0.0.0 -> 19.0.0.0 swap on those refs is all that is needed.

  (b) The plugin's OWN version. Under the "Option 3" scheme the plugin major
      tracks the Studio major it targets, so the Studio 2026 build must carry
      major 19. The source copy is the canonical Studio 2024 build (major 18);
      here we bump 18.<tail>.0 -> 19.<tail>.0 on both the <plugin version="...">
      attribute and the Supervertaler.Trados assembly bindings, leaving the
      shared tail intact. This keeps the manifest version, the plugin.xml version
      and the compiled assembly identity all consistent at 19.<tail>.0.

Invoked from the .csproj as a post-build step when TradosStudioVersion=19.
Operates on the file in the build output directory, never on the source-tree copy.

Usage:
    python patch_plugin_xml_for_studio19.py <path/to/output/Supervertaler.Trados.plugin.xml>
"""
import re
import sys


def patch(path: str) -> None:
    with open(path, "rb") as f:
        raw = f.read()

    if raw[:2] == b"\xff\xfe":
        text = raw[2:].decode("utf-16-le")
        encoding = "utf-16-le"
        write_bom = True
    elif raw[:2] == b"\xfe\xff":
        text = raw[2:].decode("utf-16-be")
        encoding = "utf-16-be"
        write_bom = True
    else:
        text = raw.decode("utf-8")
        encoding = "utf-8"
        write_bom = False

    # (a) Sdl.* framework refs: 18.0.0.0 -> 19.0.0.0. Matches only Sdl.* refs so
    #     the plugin's own 18.<tail>.0 version (handled below) is left for (b).
    sdl_re = re.compile(r"(Sdl\.[A-Za-z0-9_.]+, Version=)18\.0\.0\.0(,)")
    text, count = sdl_re.subn(r"\g<1>19.0.0.0\g<2>", text)

    # (b) The plugin's own version: 18.<tail>.0 -> 19.<tail>.0 on the <plugin>
    #     attribute and the Supervertaler.Trados assembly bindings. Only the
    #     leading major changes; the shared tail is preserved.
    ver_re = re.compile(r'(<plugin\s[^>]*?version=")18\.(\d+\.\d+\.\d+)(")')
    text, cver = ver_re.subn(r"\g<1>19.\g<2>\g<3>", text)
    asm_re = re.compile(r"(Supervertaler\.Trados, Version=)18\.(\d+\.\d+\.\d+)(,)")
    text, casm = asm_re.subn(r"\g<1>19.\g<2>\g<3>", text)

    if count == 0 and cver == 0 and casm == 0:
        print(f"  [patch_plugin_xml] WARNING: nothing to patch (no 18.* refs) in {path}")
        return

    out = text.encode(encoding)
    if write_bom:
        bom = b"\xff\xfe" if encoding == "utf-16-le" else b"\xfe\xff"
        out = bom + out

    with open(path, "wb") as f:
        f.write(out)

    print(f"  [patch_plugin_xml] Rewrote {count} Sdl.* refs + {cver} plugin version "
          f"+ {casm} assembly bindings (major 18 -> 19)")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)
    patch(sys.argv[1])
