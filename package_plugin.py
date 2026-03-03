"""
Package Termview build output into an OPC-format .sdlplugin file.

Trados Studio's PluginPackage.OpenPackage() uses System.IO.Packaging (OPC)
to read plugin packages. This means the .sdlplugin file must include:
  - [Content_Types].xml   (MIME type definitions)
  - _rels/.rels           (root relationship pointing to manifest)
  - _rels/pluginpackage.manifest.xml.rels  (file listings as required-resources)
  - pluginpackage.manifest.xml             (plugin metadata)
  - Actual plugin files (DLLs, .plugin.xml, .plugin.resources, etc.)
"""

import os
import sys
import uuid
import zipfile

# Files to include in the package (relative to build output dir)
PLUGIN_FILES = [
    "Termview.dll",
    "Termview.plugin.xml",
    "Termview.plugin.resources",
    "System.Data.SQLite.dll",
    "x64/SQLite.Interop.dll",
    "x86/SQLite.Interop.dll",
]

# Extra files listed in the <Include> section of pluginpackage.manifest.xml
# These are non-plugin DLLs that need to be deployed alongside the plugin
INCLUDE_FILES = [
    "System.Data.SQLite.dll",
    "x64/SQLite.Interop.dll",
    "x86/SQLite.Interop.dll",
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
        '<Override PartName="/Termview.plugin.xml" ContentType="application/octet-stream" />'
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


def build_manifest_xml():
    """Build pluginpackage.manifest.xml with Include section for extra files."""
    include_lines = "\n".join(f"    <File>{f}</File>" for f in INCLUDE_FILES)
    return f"""<?xml version="1.0" encoding="utf-8"?>
<PluginPackage xmlns="http://www.sdl.com/Plugins/PluginPackage/1.0">
  <PlugInName>Termview</PlugInName>
  <Version>1.0.0.0</Version>
  <Description>Inline terminology display for Trados Studio by Supervertaler.</Description>
  <Author>Michael Beijer</Author>
  <RequiredProduct name="TradosStudio" minversion="18.0" maxversion="18.9" />
  <Include>
{include_lines}
  </Include>
</PluginPackage>"""


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

    print(f"Creating OPC package: {output_path}")

    with zipfile.ZipFile(output_path, "w", zipfile.ZIP_DEFLATED) as zf:
        # 1. Write plugin files
        for f in PLUGIN_FILES:
            src = os.path.join(build_dir, f)
            zf.write(src, f)
            print(f"  + {f} ({os.path.getsize(src):,} bytes)")

        # 2. Write pluginpackage.manifest.xml (generated, not from build output)
        manifest = build_manifest_xml()
        zf.writestr("pluginpackage.manifest.xml", manifest.encode("utf-8"))
        print(f"  + pluginpackage.manifest.xml (generated)")

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
