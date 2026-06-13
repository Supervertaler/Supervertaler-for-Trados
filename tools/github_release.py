"""Cut a GitHub release for Supervertaler for Trados, with both plugin zips attached.

GitHub releases carry the *unsigned* plugin builds so eager users can install a fix
before the RWS App Store approves it. They are immutable build checkpoints, append-only
and never deleted — independent of the App Store's rolling delete/re-upload cadence.
(See CLAUDE.md → "Release channels" for the full model.)

Why zips and not bare .sdlplugin files: GitHub Releases replaces spaces in asset
filenames with periods, which would turn "Supervertaler for Trados.sdlplugin" into
"Supervertaler.for.Trados.sdlplugin". Trados extracts a plugin to
Unpacked/<sdlplugin-filename-without-extension>/ and matches it against the manifest
PlugInName, so a dotted name reintroduces the duplicate-package / stale-DLL crash.
Wrapping each plugin in a hyphenated zip preserves the exact inner filename.

Changelog baseline: the *previous GitHub release tag* (auto-detected via `gh`), NOT the
last App-Store-published version. A GitHub reader's "last seen" is the last GitHub
release; the two channels deliberately use different baselines.

Usage:
    python tools/github_release.py              # write release-body-v<ver>.md, print, no mutations
    python tools/github_release.py --zip-only   # just (re)build the two zips in dist/ (build.sh uses this)
    python tools/github_release.py --create      # zips + body + `gh release create` with both zips attached
    python tools/github_release.py --since 4.20.44 --create   # override the auto-detected baseline
"""
import os
import re
import subprocess
import sys
import zipfile

if sys.stdout.encoding != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8")

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHANGELOG = os.path.join(BASE_DIR, "CHANGELOG.md")
CSPROJ = os.path.join(BASE_DIR, "src", "Supervertaler.Trados", "Supervertaler.Trados.csproj")
DIST_DIR = os.path.join(BASE_DIR, "dist")

# (sdlplugin filename in dist/, zip asset name, human label). The .sdlplugin names are
# load-bearing — they must match the manifest PlugInName — so they are never renamed; the
# zip carries them verbatim.
PLUGINS = [
    ("Supervertaler for Trados.sdlplugin",
     "Supervertaler-for-Trados-Studio-2024.zip",
     "Trados Studio 2024"),
    ("Supervertaler for Trados (Studio 2026).sdlplugin",
     "Supervertaler-for-Trados-Studio-2026-beta.zip",
     "Trados Studio 2026 (beta)"),
]


def read_current_version():
    with open(CSPROJ, "r", encoding="utf-8") as f:
        match = re.search(r"<Version>([\d.]+)</Version>", f.read())
    return match.group(1) if match else None


def last_github_tag():
    """The most recent GitHub release tag, e.g. 'v4.20.44' -> '4.20.44'. None if no releases."""
    try:
        out = subprocess.run(
            ["gh", "release", "list", "--limit", "1", "--json", "tagName", "-q", ".[0].tagName"],
            cwd=BASE_DIR, capture_output=True, text=True, check=True,
        ).stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    return out.lstrip("v") or None


def parse_changelog():
    """[(version, raw_markdown_block)], newest first."""
    with open(CHANGELOG, "r", encoding="utf-8") as f:
        text = f.read()
    entries = []
    for part in re.split(r"(?=^## \[)", text, flags=re.MULTILINE):
        m = re.match(r"^## \[([\d.]+)\]", part)
        if m:
            entries.append((m.group(1), part.strip()))
    return entries


def select_entries(entries, since):
    """Entries newer than `since` (exclusive). If `since` is None/unknown, take all."""
    versions = [v for v, _ in entries]
    selected = []
    for v, content in entries:
        if since and v == since:
            break
        selected.append((v, content))
    return selected, versions


def build_body(version, selected):
    span = (f"{selected[-1][0]} → {selected[0][0]}"
            if len(selected) > 1 else selected[0][0]) if selected else version

    table = "\n".join(f"| `{zip_name}` | {label} |" for _sdl, zip_name, label in PLUGINS)
    changelog = "\n\n".join(content for _v, content in selected) if selected else "_See CHANGELOG.md._"

    return f"""Supervertaler for Trados **v{version}** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers {span}.

## 📦 Installing from here (unsigned build – read first)

The plugins attached to this release are the **unsigned** builds. The version on the **RWS App Store is signed and notarised** – that's the recommended channel for most users. These downloads are for trying the latest fixes **before App Store approval** (which can take a few days, especially over a weekend).

**To install:**
1. Download the zip for your Trados version (table below).
2. **Extract it** – inside is a single `.sdlplugin` file.
3. Close Trados Studio, then double-click the `.sdlplugin` to run the Plugin Installer. **Do not rename the file** – Trados matches the filename against the plugin manifest.
4. Trados will warn that the plugin is **not signed**; that is expected for the direct build – click through to continue.

| Download | Trados version |
|---|---|
{table}

## What's changed

{changelog}

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
"""


def make_zips():
    """(Re)create the two release zips in dist/. Returns the list of zip paths."""
    paths = []
    for sdl_name, zip_name, _label in PLUGINS:
        sdl_path = os.path.join(DIST_DIR, sdl_name)
        zip_path = os.path.join(DIST_DIR, zip_name)
        if not os.path.exists(sdl_path):
            print(f"  WARNING: {sdl_name} not found in dist/ — skipping {zip_name} (run bash build.sh first)")
            continue
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.write(sdl_path, arcname=sdl_name)  # arcname preserves the exact, load-bearing name
        print(f"  Zipped: {zip_name}  (contains '{sdl_name}')")
        paths.append(zip_path)
    return paths


def main():
    args = sys.argv[1:]
    zip_only = "--zip-only" in args
    create = "--create" in args
    since = None
    if "--since" in args:
        since = args[args.index("--since") + 1].lstrip("v")

    if zip_only:
        make_zips()
        return

    version = read_current_version()
    if not version:
        print("ERROR: could not read version from .csproj")
        sys.exit(1)

    if since is None:
        since = last_github_tag()
    entries = parse_changelog()
    selected, all_versions = select_entries(entries, since)

    if since and since not in all_versions:
        print(f"WARNING: baseline {since} not found in changelog — including all entries")
    print(f"v{version}: baseline = {since or '(none)'}, "
          f"{len(selected)} changelog version(s): {', '.join(v for v, _ in selected) or '—'}")

    body = build_body(version, selected)
    body_file = os.path.join(BASE_DIR, f"release-body-v{version}.md")
    with open(body_file, "w", encoding="utf-8") as f:
        f.write(body)
    print(f"  Release body written to: {body_file}")

    if not create:
        print("\n(dry run — pass --create to zip the plugins and run `gh release create`)")
        return

    print("\nBuilding zips…")
    zips = make_zips()
    if not zips:
        print("ERROR: no zips produced — aborting release")
        sys.exit(1)

    tag = f"v{version}"
    cmd = ["gh", "release", "create", tag,
           "--title", tag,
           "--notes-file", body_file,
           *zips]
    print(f"\nRunning: gh release create {tag} (+{len(zips)} assets)")
    result = subprocess.run(cmd, cwd=BASE_DIR)
    if result.returncode != 0:
        print("ERROR: gh release create failed")
        sys.exit(result.returncode)
    print(f"\n✓ Released {tag} with {len(zips)} plugin zip(s) attached.")


if __name__ == "__main__":
    main()
