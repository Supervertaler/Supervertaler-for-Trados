"""
Package Supervertaler.Trados build output into an OPC-format .sdlplugin file.

Trados Studio's PluginPackage.OpenPackage() uses System.IO.Packaging (OPC)
to read plugin packages. This means the .sdlplugin file must include:
  - [Content_Types].xml   (MIME type definitions)
  - _rels/.rels           (root relationship pointing to manifest)
  - _rels/pluginpackage.manifest.xml.rels  (file listings as required-resources)
  - pluginpackage.manifest.xml             (plugin metadata)
  - Actual plugin files (DLLs, .plugin.xml, .plugin.resources, etc.)
"""

import os
import re
import sys
import uuid
import zipfile

# Files to include in the package (relative to build output dir)
PLUGIN_FILES = [
    # --- Core plugin ---
    "Supervertaler.Trados.dll",
    "Supervertaler.Trados.plugin.xml",
    "Supervertaler.Trados.plugin.resources",
    # --- Microsoft.Data.Sqlite + SQLitePCLRaw ---
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.provider.dynamic_cdecl.dll",
    # --- iTextSharp (PDF text extraction) ---
    "itextsharp.dll",
    "BouncyCastle.Cryptography.dll",
    # --- DocumentFormat.OpenXml (DOCX read/write for the Import / Export
    #     tab added in v4.20.7) ---
    "DocumentFormat.OpenXml.dll",
    # --- .NET Standard polyfills (Trados ships older versions) ---
    "System.Memory.dll",
    "System.Buffers.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",

    # --- Native SQLite library (per-architecture) ---
    "runtimes/win-x64/native/e_sqlite3.dll",
    "runtimes/win-x86/native/e_sqlite3.dll",
    "runtimes/win-arm64/native/e_sqlite3.dll",
]

# Extra files listed in the <Include> section of pluginpackage.manifest.xml
# These are non-plugin DLLs that need to be deployed alongside the plugin
INCLUDE_FILES = [
    "itextsharp.dll",
    "BouncyCastle.Cryptography.dll",
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.provider.dynamic_cdecl.dll",
    "DocumentFormat.OpenXml.dll",
    "System.Memory.dll",
    "System.Buffers.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",

    "runtimes/win-x64/native/e_sqlite3.dll",
    "runtimes/win-x86/native/e_sqlite3.dll",
    "runtimes/win-arm64/native/e_sqlite3.dll",
]


def make_rel_id():
    """Generate a relationship ID in the same format as Trados tools."""
    return "R" + uuid.uuid4().hex[:16]


def build_content_types_xml(files):
    """Build [Content_Types].xml with MIME types for all file extensions."""
    extensions = set()
    override_parts = []

    for f in files:
        ext = os.path.splitext(f)[1].lstrip(".")
        if ext:
            extensions.add(ext)

    # Always include rels extension (for _rels/ relationship files)
    extensions.add("rels")

    # Map extensions to MIME types
    mime_map = {
        "xml": "text/xml",
        "rels": "application/vnd.openxmlformats-package.relationships+xml",
        "dll": "application/octet-stream",
        "resources": "application/octet-stream",
    }

    parts = []
    for ext in sorted(extensions):
        ct = mime_map.get(ext, "application/octet-stream")
        parts.append(f'<Default Extension="{ext}" ContentType="{ct}" />')

    # The .plugin.xml file needs an Override because .xml is already mapped to text/xml
    override_parts.append(
        '<Override PartName="/Supervertaler.Trados.plugin.xml" ContentType="application/octet-stream" />'
    )

    defaults = "".join(parts)
    overrides = "".join(override_parts)
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        f"{defaults}{overrides}"
        "</Types>"
    )


def build_root_rels():
    """Build _rels/.rels pointing to the manifest."""
    rel_id = make_rel_id()
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        f'<Relationship Type="http://www.sdl.com/OpenExchange/2010/03/sdlplugin/root-manifest-xml" '
        f'Target="/pluginpackage.manifest.xml" Id="{rel_id}" />'
        "</Relationships>"
    )


def build_manifest_rels(files):
    """Build _rels/pluginpackage.manifest.xml.rels listing all files as required resources."""
    rels = []
    for f in files:
        rel_id = make_rel_id()
        target = "/" + f.replace("\\", "/")
        rels.append(
            f'<Relationship Type="http://www.sdl.com/OpenExchange/2010/03/sdlplugin/required-resource" '
            f'Target="{target}" Id="{rel_id}" />'
        )

    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        + "".join(rels)
        + "</Relationships>"
    )


def read_version_from_build_dir(build_dir):
    """Read the 4-part <Version> from the manifest in the build output.

    The build output's pluginpackage.manifest.xml is the per-Studio-version file
    (major 18 for Studio 2024, 19 for Studio 2026), so this yields the correct
    version for whichever build is being packaged. The .csproj no longer holds a
    literal version — it uses a $(TradosStudioVersion) token — so reading it here
    would not give a real number.
    """
    src = os.path.join(build_dir, "pluginpackage.manifest.xml")
    if not os.path.exists(src):
        print(f"ERROR: Manifest not found in build output: {src}")
        sys.exit(1)
    with open(src, "r", encoding="utf-8-sig") as f:
        content = f.read()
    match = re.search(r"<Version>([\d.]+)</Version>", content)
    if not match:
        print("ERROR: Could not find <Version> in build-output manifest")
        sys.exit(1)
    return match.group(1)


def read_manifest_from_build_dir(build_dir):
    """Read the pluginpackage.manifest.xml from the build output directory.

    The MSBuild CopyPluginManifest target writes the per-Studio-version manifest
    (18 vs 19) there; we ship whatever the build produced verbatim instead of
    regenerating from a hardcoded template — otherwise the dual-build setup
    silently regresses to Studio 18 RequiredProduct constraints.
    """
    src = os.path.join(build_dir, "pluginpackage.manifest.xml")
    if not os.path.exists(src):
        print(f"ERROR: Manifest not found in build output: {src}")
        print("       Did the CopyPluginManifest MSBuild target run?")
        sys.exit(1)
    with open(src, "r", encoding="utf-8-sig") as f:
        return f.read()


def main():
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <build_dir> <output.sdlplugin>")
        sys.exit(1)

    build_dir = sys.argv[1]
    output_path = sys.argv[2]

    # Verify all files exist
    for f in PLUGIN_FILES:
        path = os.path.join(build_dir, f)
        if not os.path.exists(path):
            print(f"ERROR: Missing file: {path}")
            sys.exit(1)

    # Read version from the build-output manifest (for the log line; the manifest
    # itself is shipped verbatim from the build output below).
    version = read_version_from_build_dir(build_dir)
    print(f"Creating OPC package: {output_path} (version {version})")

    with zipfile.ZipFile(output_path, "w", zipfile.ZIP_DEFLATED) as zf:
        # 1. Write plugin files
        for f in PLUGIN_FILES:
            src = os.path.join(build_dir, f)
            zf.write(src, f)
            print(f"  + {f} ({os.path.getsize(src):,} bytes)")

        # 2. Write pluginpackage.manifest.xml (taken verbatim from the build
        #    output — this is how the Studio 18 vs Studio 19 RequiredProduct
        #    constraints get applied to the right package).
        manifest = read_manifest_from_build_dir(build_dir)
        zf.writestr("pluginpackage.manifest.xml", manifest.encode("utf-8"))
        print(f"  + pluginpackage.manifest.xml (from build output)")

        # 3. Write [Content_Types].xml
        all_files = PLUGIN_FILES + ["pluginpackage.manifest.xml"]
        content_types = build_content_types_xml(all_files)
        zf.writestr("[Content_Types].xml", content_types.encode("utf-8"))
        print(f"  + [Content_Types].xml (generated)")

        # 4. Write _rels/.rels
        root_rels = build_root_rels()
        zf.writestr("_rels/.rels", root_rels.encode("utf-8"))
        print(f"  + _rels/.rels (generated)")

        # 5. Write _rels/pluginpackage.manifest.xml.rels
        manifest_rels = build_manifest_rels(PLUGIN_FILES)
        zf.writestr(
            "_rels/pluginpackage.manifest.xml.rels", manifest_rels.encode("utf-8")
        )
        print(f"  + _rels/pluginpackage.manifest.xml.rels (generated)")

    size = os.path.getsize(output_path)
    print(f"\nPackage created: {output_path} ({size:,} bytes)")


if __name__ == "__main__":
    main()
