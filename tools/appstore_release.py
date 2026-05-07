"""Generate RWS App Store Manager release info from CHANGELOG.md and build artifacts.

Produces a single Markdown file with all fields needed for the App Store Manager form:
version number, min/max studio version, checksum, and combined changelog.

Usage:
    python tools/appstore_release.py                  # all entries since last App Store release
    python tools/appstore_release.py 4.16.0           # all entries after 4.16.0
    python tools/appstore_release.py 4.17.0 4.18.3    # entries from 4.17.0 through 4.18.3

Output:
    dist/release_notes_v<version>.md   (alongside the .sdlplugin)
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
CSPROJ = os.path.join(BASE_DIR, "src", "Supervertaler.Trados", "Supervertaler.Trados.csproj")
MANIFEST = os.path.join(BASE_DIR, "src", "Supervertaler.Trados", "pluginpackage.manifest.xml")
SDLPLUGIN = os.path.join(BASE_DIR, "dist", "Supervertaler for Trados.sdlplugin")
OUTPUT_DIR = os.path.join(BASE_DIR, "dist")


def read_current_version():
    """Read current version from .csproj (3-part, e.g. 4.18.3)."""
    with open(CSPROJ, "r", encoding="utf-8") as f:
        text = f.read()
    match = re.search(r"<Version>([\d.]+)</Version>", text)
    return match.group(1) if match else None


def read_studio_versions():
    """Read min/max studio versions from pluginpackage.manifest.xml."""
    with open(MANIFEST, "r", encoding="utf-8") as f:
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
    """Parse CHANGELOG.md into a list of (version, content) tuples."""
    with open(changelog_path, "r", encoding="utf-8") as f:
        text = f.read()

    entries = []
    parts = re.split(r"(?=^## \[)", text, flags=re.MULTILINE)

    for part in parts:
        match = re.match(r"^## \[([\d.]+)\]", part)
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
                # Allow parenthetical suffixes, e.g. "Fixed (TermLens popup – ...)"
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

    current_version = read_current_version()
    if not current_version:
        print("ERROR: Could not read version from .csproj")
        sys.exit(1)

    version_four = current_version + ".0"
    min_studio, max_studio = read_studio_versions()
    checksum = compute_checksum(SDLPLUGIN)

    if len(sys.argv) == 3:
        from_version = sys.argv[1]
        to_version = sys.argv[2]
    elif len(sys.argv) == 2:
        from_version = sys.argv[1]
        to_version = current_version
    else:
        from_version = None
        to_version = current_version

    # Filter entries – entries are newest-first in the list
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

    # Build the full release notes file
    output_lines = []
    output_lines.append(f"# RWS App Store Manager – v{version_four}")
    output_lines.append("")
    output_lines.append(f"**Version number:** `{version_four}`")
    output_lines.append(f"**Minimum studio version:** `{min_studio or '?'}`")
    output_lines.append(f"**Maximum studio version:** `{max_studio or '?'}`")
    output_lines.append(f"**Checksum:** `{checksum or 'BUILD NOT FOUND – run bash build.sh first'}`")
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
    output_file = os.path.join(OUTPUT_DIR, f"release_notes_v{current_version}.md")

    with open(output_file, "w", encoding="utf-8") as f:
        f.write(full_output)

    print(f"Release notes for v{version_four} (changes: {version_range}):")
    print(f"  Written to: {output_file}")
    print(f"  {len(added)} added, {len(changed)} changed, {len(fixed)} fixed items")
    if not checksum:
        print(f"  WARNING: {SDLPLUGIN} not found – run bash build.sh first")


if __name__ == "__main__":
    main()
