"""Generate RWS App Store Manager release info from CHANGELOG.md and build artifacts.

Produces a single Markdown file with the fields needed for the App Store Manager
form, covering BOTH builds (Studio 2024 = major 18, Studio 2026 = major 19):
version numbers, min/max studio versions, checksums, and the combined changelog.

Usage:
    python tools/appstore_release.py                    # all entries since last release
    python tools/appstore_release.py 18.16.0            # all entries after 18.16.0
    python tools/appstore_release.py 18.17.0 18.20.86   # entries from 18.17.0 through 18.20.86

Version numbers are read from the manifests (the .csproj keeps a
$(TradosStudioVersion) token, so it is not a literal number). Changelog entries
are headed "## [18.<tail> / 19.<tail>] - date"; the first (Studio 2024) number is
used for filtering. Older single-number headers (e.g. "## [4.20.85]") still parse.

Output:
    RWS AppStore/release_notes_v<studio2024-version>.md
    (build.sh mirrors both .sdlplugin files into the same folder so the
    AppStore Manager upload has everything in one place to drag from.)
"""
import hashlib
import os
import re
import sys

# Ensure UTF-8 output on Windows
if sys.stdout.encoding != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8")

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHANGELOG = os.path.join(BASE_DIR, "CHANGELOG.md")
SRC_DIR = os.path.join(BASE_DIR, "src", "Supervertaler.Trados")
MANIFEST_18 = os.path.join(SRC_DIR, "pluginpackage.manifest.xml")
MANIFEST_19 = os.path.join(SRC_DIR, "pluginpackage.manifest.19.xml")
SDLPLUGIN_18 = os.path.join(BASE_DIR, "dist", "Supervertaler for Trados.sdlplugin")
SDLPLUGIN_19 = os.path.join(BASE_DIR, "dist", "Supervertaler for Trados (Studio 2026).sdlplugin")
OUTPUT_DIR = os.path.join(BASE_DIR, "RWS AppStore")


def read_manifest_version(path):
    """Read the 4-part <Version> from a manifest (e.g. 18.20.86.0)."""
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    match = re.search(r"<Version>([\d.]+)</Version>", text)
    return match.group(1) if match else None


def read_studio_versions(path):
    """Read min/max studio versions from a manifest."""
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    match = re.search(
        r'<RequiredProduct\s+name="TradosStudio"\s+minversion="([\d.]+)"\s+maxversion="([\d.]+)"',
        text,
    )
    if match:
        return match.group(1), match.group(2)
    return None, None


def compute_checksum(filepath):
    """Compute SHA-256 checksum of a file."""
    if not os.path.exists(filepath):
        return None
    sha256 = hashlib.sha256()
    with open(filepath, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            sha256.update(chunk)
    return sha256.hexdigest()


def parse_changelog(changelog_path):
    """Parse CHANGELOG.md into a list of (version, content) tuples.

    Entry headers look like "## [18.20.86 / 19.20.86] - date" (or the older
    "## [4.20.85] - date"); we key on the FIRST version token in the brackets.
    """
    with open(changelog_path, "r", encoding="utf-8") as f:
        text = f.read()

    entries = []
    parts = re.split(r"(?=^## \[)", text, flags=re.MULTILINE)

    for part in parts:
        match = re.match(r"^## \[([\d.]+)(?:\s*/\s*[\d.]+)?\]", part)
        if not match:
            continue
        version = match.group(1)
        content = part.strip()
        entries.append((version, content))

    return entries


def collect_sections(entries):
    """Merge multiple changelog entries into combined Added/Changed/Fixed sections."""
    added = []
    changed = []
    fixed = []

    for _version, content in entries:
        lines = content.split("\n")
        current_section = None

        for line in lines:
            stripped = line.strip()
            if stripped.startswith("## ["):
                continue
            if stripped == "---":
                continue
            if stripped.startswith("### "):
                header = stripped[4:].strip().lower()
                # Allow parenthetical suffixes, e.g. "Fixed (TermLens popup - ...)"
                head_word = header.split("(", 1)[0].strip()
                if head_word in ("added", "new features"):
                    current_section = "added"
                elif head_word == "changed":
                    current_section = "changed"
                elif head_word == "fixed":
                    current_section = "fixed"
                else:
                    current_section = None
                continue
            if stripped.startswith("- ") and current_section:
                if current_section == "added":
                    added.append(stripped)
                elif current_section == "changed":
                    changed.append(stripped)
                elif current_section == "fixed":
                    fixed.append(stripped)

    # Deduplicate by extracting the bold title from each line
    def dedup(items):
        seen_titles = set()
        result = []
        for item in items:
            match = re.match(r"- \*\*(.+?)\*\*", item)
            title = match.group(1).lower().strip() if match else item.lower()
            if title not in seen_titles:
                seen_titles.add(title)
                result.append(item)
        return result

    return dedup(added), dedup(changed), dedup(fixed)


def main():
    entries = parse_changelog(CHANGELOG)
    if not entries:
        print("ERROR: No changelog entries found")
        sys.exit(1)

    ver18_four = read_manifest_version(MANIFEST_18)
    ver19_four = read_manifest_version(MANIFEST_19)
    if not ver18_four:
        print("ERROR: Could not read version from pluginpackage.manifest.xml")
        sys.exit(1)
    # 3-part Studio 2024 number, used for changelog filtering and the filename.
    ver18_three = ver18_four[:-2] if ver18_four.endswith(".0") else ver18_four

    min18, max18 = read_studio_versions(MANIFEST_18)
    min19, max19 = read_studio_versions(MANIFEST_19)
    checksum18 = compute_checksum(SDLPLUGIN_18)
    checksum19 = compute_checksum(SDLPLUGIN_19)

    if len(sys.argv) == 3:
        from_version = sys.argv[1]
        to_version = sys.argv[2]
    elif len(sys.argv) == 2:
        from_version = sys.argv[1]
        to_version = ver18_three
    else:
        from_version = None
        to_version = ver18_three

    # Filter entries - entries are newest-first in the list
    all_versions = [v for v, _ in entries]

    if from_version:
        if from_version not in all_versions:
            print(f"ERROR: Version {from_version} not found in changelog")
            print(f"Available: {', '.join(all_versions[:10])}")
            sys.exit(1)

    selected = []
    for v, content in entries:
        if to_version and to_version in all_versions:
            if all_versions.index(v) < all_versions.index(to_version):
                continue
        if from_version and v == from_version:
            break
        selected.append((v, content))

    if not selected:
        print(f"No changelog entries found between {from_version} and {to_version}")
        sys.exit(1)

    version_range = f"{selected[-1][0]}-{selected[0][0]}" if len(selected) > 1 else selected[0][0]

    added, changed, fixed = collect_sections(selected)

    # Build changelog text
    sections = []
    if added:
        sections.append("### Added\n" + "\n".join(added))
    if changed:
        sections.append("### Changed\n" + "\n".join(changed))
    if fixed:
        sections.append("### Fixed\n" + "\n".join(fixed))
    changelog_text = "\n\n".join(sections)

    build_not_found = "BUILD NOT FOUND - run bash build.sh first"

    # Build the full release notes file
    output_lines = []
    output_lines.append(f"# RWS App Store Manager - v{ver18_three}")
    output_lines.append("")
    output_lines.append("Two builds ship from this one release (identical feature set, distinct")
    output_lines.append("version numbers so the App Store never sees a collision):")
    output_lines.append("")
    output_lines.append("| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |")
    output_lines.append("|-------|----------------|------------|------------|--------------------|")
    output_lines.append(
        f"| Studio 2024 | `{ver18_four}` | `{min18 or '?'}` | `{max18 or '?'}` | `{checksum18 or build_not_found}` |"
    )
    output_lines.append(
        f"| Studio 2026 | `{ver19_four or '?'}` | `{min19 or '?'}` | `{max19 or '?'}` | `{checksum19 or build_not_found}` |"
    )
    output_lines.append("")
    output_lines.append("---")
    output_lines.append("")
    output_lines.append("## Changelog")
    output_lines.append("")
    output_lines.append(changelog_text)
    output_lines.append("")
    output_lines.append("For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases")

    full_output = "\n".join(output_lines)

    # Write output
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    output_file = os.path.join(OUTPUT_DIR, f"release_notes_v{ver18_three}.md")

    with open(output_file, "w", encoding="utf-8") as f:
        f.write(full_output)

    print(f"Release notes for v{ver18_three} (Studio 2024 {ver18_four} / Studio 2026 {ver19_four}); changes: {version_range}")
    print(f"  Written to: {output_file}")
    print(f"  {len(added)} added, {len(changed)} changed, {len(fixed)} fixed items")
    for label, cs, sp in (("Studio 2024", checksum18, SDLPLUGIN_18), ("Studio 2026", checksum19, SDLPLUGIN_19)):
        if not cs:
            print(f"  WARNING: {sp} not found - run bash build.sh first ({label})")


if __name__ == "__main__":
    main()
