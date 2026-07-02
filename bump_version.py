"""Bump the Supervertaler.Trados version across all version files.

The plugin uses "Option 3" versioning: the MAJOR version tracks the Trados
Studio major it targets (Studio 2024 = 18, Studio 2026 = 19), and both builds
share the same MINOR.PATCH tail. This script bumps only that shared tail; each
file keeps its own major, so the two builds can never collide in the App Store.

Usage:
    python bump_version.py <minor>.<patch>

Example:
    python bump_version.py 20.87      # -> Studio 2024 18.20.87.0, Studio 2026 19.20.87.0

Updates (major preserved per file, only the shared tail changes):
    - Supervertaler.Trados.csproj: <Version>/<InformationalVersion>
      kept as $(TradosStudioVersion).<tail> so the major resolves to the Studio major
    - pluginpackage.manifest.xml     (Studio 2024, major 18): <Version>18.<tail>.0
    - pluginpackage.manifest.19.xml  (Studio 2026, major 19): <Version>19.<tail>.0
    - plugin.xml (UTF-16 LE, canonical Studio 2024 copy, major 18):
      the <plugin version="..."> attribute + Supervertaler.Trados assembly bindings.
      (The Studio 2026 build's copy is rewritten to 19.<tail>.0 at build time by
       tools/patch_plugin_xml_for_studio19.py.)
"""
import os
import sys
import re

BASE_DIR = os.path.dirname(__file__)
SRC_DIR = os.path.join(BASE_DIR, "src", "Supervertaler.Trados")
PLUGIN_XML = os.path.join(SRC_DIR, "Supervertaler.Trados.plugin.xml")
MANIFEST_XML = os.path.join(SRC_DIR, "pluginpackage.manifest.xml")
MANIFEST_XML_19 = os.path.join(SRC_DIR, "pluginpackage.manifest.19.xml")
CSPROJ = os.path.join(SRC_DIR, "Supervertaler.Trados.csproj")


def validate_tail(tail):
    """The shared tail is MINOR.PATCH, e.g. 20.87."""
    if not re.match(r"^\d+\.\d+$", tail):
        print(f"ERROR: '{tail}' is not a valid MINOR.PATCH tail (expected e.g. 20.87)")
        print("       The major is fixed per build (18/19); you only bump the tail.")
        print("       Example: python bump_version.py 20.87")
        sys.exit(1)


def bump_csproj(tail):
    """Set <Version>/<InformationalVersion> to $(TradosStudioVersion).<tail>."""
    with open(CSPROJ, "r", encoding="utf-8") as f:
        text = f.read()
    for tag in ("Version", "InformationalVersion"):
        pattern = re.compile(rf"<{tag}>[^<]*</{tag}>")
        if not pattern.search(text):
            print(f"  WARNING: <{tag}> not found in .csproj")
            continue
        text = pattern.sub(f"<{tag}>$(TradosStudioVersion).{tail}</{tag}>", text)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"  .csproj: Version=$(TradosStudioVersion).{tail}")


def bump_manifest(tail):
    """Set <Version> in each manifest, preserving that file's own major."""
    for path in (MANIFEST_XML, MANIFEST_XML_19):
        if not os.path.exists(path):
            print(f"  WARNING: manifest not found: {path}")
            continue
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
        pattern = re.compile(r"<Version>(\d+)\.\d+\.\d+\.\d+</Version>")
        m = pattern.search(text)
        if not m:
            print(f"  WARNING: 4-part <Version> not found in {os.path.basename(path)}")
            continue
        major = m.group(1)
        text = pattern.sub(f"<Version>{major}.{tail}.0</Version>", text)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"  {os.path.basename(path)}: Version={major}.{tail}.0")


def bump_plugin_xml(tail):
    """Update plugin.xml (UTF-16 LE), preserving the major on both refs."""
    with open(PLUGIN_XML, "rb") as f:
        raw = f.read()
    if raw[:2] == b'\xff\xfe':
        text = raw[2:].decode("utf-16-le")
    else:
        text = raw.decode("utf-16-le")

    # Plugin version attribute, e.g. version="18.20.86.0" -> keep the major
    ver_pattern = re.compile(r'(<plugin\s[^>]*?version=")(\d+)\.\d+\.\d+\.\d+(")')
    c1 = len(ver_pattern.findall(text))
    text = ver_pattern.sub(rf'\g<1>\g<2>.{tail}.0\g<3>', text)

    # Assembly bindings for Supervertaler.Trados -> keep the major
    asm_pattern = re.compile(r"(Supervertaler\.Trados, Version=)(\d+)\.\d+\.\d+\.\d+(,)")
    c2 = len(asm_pattern.findall(text))
    text = asm_pattern.sub(rf"\g<1>\g<2>.{tail}.0\g<3>", text)

    with open(PLUGIN_XML, "wb") as f:
        f.write(b'\xff\xfe')
        f.write(text.encode("utf-16-le"))
    print(f"  plugin.xml: {c1} plugin version + {c2} assembly binding refs updated")


def _tail_of_four(v):
    m = re.match(r"^\d+\.(\d+\.\d+)\.\d+$", v)
    return m.group(1) if m else None


def verify(tail):
    """Read everything back and confirm the shared tail is consistent."""
    errors = []

    with open(CSPROJ, "r", encoding="utf-8") as f:
        ctext = f.read()
    for tag in ("Version", "InformationalVersion"):
        m = re.search(rf"<{tag}>\$\(TradosStudioVersion\)\.(\d+\.\d+)</{tag}>", ctext)
        if not m:
            errors.append(f".csproj <{tag}> is not $(TradosStudioVersion).<tail>")
        elif m.group(1) != tail:
            errors.append(f".csproj <{tag}> tail is '{m.group(1)}', expected '{tail}'")

    majors = {}
    for path in (MANIFEST_XML, MANIFEST_XML_19):
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
        m = re.search(r"<Version>(\d+)\.(\d+\.\d+)\.\d+</Version>", text)
        if not m:
            errors.append(f"{os.path.basename(path)} 4-part <Version> not found")
            continue
        majors[os.path.basename(path)] = m.group(1)
        if m.group(2) != tail:
            errors.append(f"{os.path.basename(path)} tail is '{m.group(2)}', expected '{tail}'")

    with open(PLUGIN_XML, "rb") as f:
        raw = f.read()
    ptext = (raw[2:] if raw[:2] == b'\xff\xfe' else raw).decode("utf-16-le")
    m = re.search(r'<plugin\s[^>]*?version="(\d+)\.(\d+\.\d+)\.\d+"', ptext)
    if not m:
        errors.append("plugin.xml plugin version attribute not found")
    else:
        if m.group(2) != tail:
            errors.append(f"plugin.xml tail is '{m.group(2)}', expected '{tail}'")
        # plugin.xml is the canonical Studio 2024 copy: its major must match manifest 18.
        man18_major = majors.get("pluginpackage.manifest.xml")
        if man18_major and m.group(1) != man18_major:
            errors.append(
                f"plugin.xml major {m.group(1)} != pluginpackage.manifest.xml major {man18_major}"
            )
    for v in re.findall(r"Supervertaler\.Trados, Version=(\d+\.\d+\.\d+\.\d+)", ptext):
        if _tail_of_four(v) != tail:
            errors.append(f"plugin.xml stale assembly binding Version={v}")
            break

    # Sanity: the two manifests must NOT share a major (that shared major was the
    # exact App Store collision this scheme exists to prevent).
    m18 = majors.get("pluginpackage.manifest.xml")
    m19 = majors.get("pluginpackage.manifest.19.xml")
    if m18 and m19 and m18 == m19:
        errors.append(f"both manifests share major {m18} - they must differ (18 vs 19)")

    if errors:
        print("\nERROR: Version check failed after bump!")
        for e in errors:
            print(f"  - {e}")
        sys.exit(1)
    print(f"\nVerified shared tail {tail}  "
          f"(Studio 2024: {m18}.{tail}.0 / Studio 2026: {m19}.{tail}.0)")


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)

    tail = sys.argv[1]  # e.g. "20.87"
    validate_tail(tail)

    print(f"Setting shared tail to {tail}:")
    bump_csproj(tail)
    bump_manifest(tail)
    bump_plugin_xml(tail)
    verify(tail)


if __name__ == "__main__":
    main()
