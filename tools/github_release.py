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
SRC_DIR = os.path.join(BASE_DIR, "src", "Supervertaler.Trados")
MANIFEST_18 = os.path.join(SRC_DIR, "pluginpackage.manifest.xml")
MANIFEST_19 = os.path.join(SRC_DIR, "pluginpackage.manifest.19.xml")
DIST_DIR = os.path.join(BASE_DIR, "dist")

# Claude Desktop extension (built by tools/build_mcpb.py). Attached to every
# release: the plugin's "Connect AI assistant" dialog links users to
# /releases/latest to download exactly this file.
MCPB_NAME = "Supervertaler-MCP-Server.mcpb"

# (sdlplugin filename in dist/, zip asset name, human label). The .sdlplugin names are
# load-bearing — they must match the manifest PlugInName — so they are never renamed; the
# zip carries them verbatim.
PLUGINS = [
    ("Supervertaler for Trados.sdlplugin",
     "Supervertaler-for-Trados-Studio-2024.zip",
     "Trados Studio 2024"),
    ("Supervertaler for Trados (Studio 2026).sdlplugin",
     "Supervertaler-for-Trados-Studio-2026.zip",
     "Trados Studio 2026"),
]


def _read_manifest_three(path):
    """3-part version from a manifest (18.20.86.0 -> 18.20.86). The .csproj holds
    a $(TradosStudioVersion) token, so versions are read from the manifests now."""
    with open(path, "r", encoding="utf-8") as f:
        match = re.search(r"<Version>([\d.]+)</Version>", f.read())
    if not match:
        return None
    v = match.group(1)
    return v[:-2] if v.endswith(".0") else v


def read_current_version():
    """The Studio 2024 (major 18) number — used for the git tag and title."""
    return _read_manifest_three(MANIFEST_18)


def read_current_version_19():
    """The Studio 2026 (major 19) number — shown alongside in the release body."""
    return _read_manifest_three(MANIFEST_19)


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
        # Headers are "## [18.x / 19.x] - date" (or the older "## [4.x] - date");
        # key on the first (Studio 2024) version token.
        m = re.match(r"^## \[([\d.]+)(?:\s*/\s*[\d.]+)?\]", part)
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


def build_body(version, version19, selected):
    span = (f"{selected[-1][0]} → {selected[0][0]}"
            if len(selected) > 1 else selected[0][0]) if selected else version

    table = "\n".join(f"| `{zip_name}` | {label} |" for _sdl, zip_name, label in PLUGINS)
    table += f"\n| `{MCPB_NAME}` | AI assistant extension for Claude Desktop (optional, see below) |"
    changelog = "\n\n".join(content for _v, content in selected) if selected else "_See CHANGELOG.md._"

    return f"""Supervertaler for Trados **v{version}** (Studio 2024) / **v{version19}** (Studio 2026) — unsigned builds are attached below. Covers {span}.

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

## 🤖 Supervertaler MCP Server (optional)

`{MCPB_NAME}` connects **Claude Desktop directly to your live Trados Studio session** – ask about the open project, search your TMs and termbases, have translations drafted into the document, all from Claude's own chat window. To install: download the file and **double-click it** – Claude Desktop installs it as an extension. Requires Supervertaler for Trados (this plugin) and works entirely on your own machine. Other MCP-capable AI apps (ChatGPT desktop, Claude Code) are supported via **Settings → AI Settings → Connect AI assistant…** in the plugin. [Documentation](https://docs.supervertaler.com/trados/mcp-server/).

## What's changed

{changelog}

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
"""


def build_mcpb(version):
    """(Re)build the Claude Desktop extension so every release carries a fresh
    one. The Connect dialog in the plugin points users at /releases/latest, so
    a release without the .mcpb would leave that button pointing at nothing.
    The extension gets semver "1.<shared tail>" (e.g. 18.20.94 -> 1.20.94) so
    users can correlate it with the plugin build it shipped with."""
    tail = version.split(".", 1)[1] if "." in version else version
    result = subprocess.run(
        [sys.executable, os.path.join(BASE_DIR, "tools", "build_mcpb.py"),
         "--version", f"1.{tail}"],
        cwd=BASE_DIR)
    mcpb_path = os.path.join(DIST_DIR, MCPB_NAME)
    if result.returncode != 0 or not os.path.exists(mcpb_path):
        return None
    return mcpb_path


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
    version19 = read_current_version_19()
    if not version:
        print("ERROR: could not read version from pluginpackage.manifest.xml")
        sys.exit(1)

    if since is None:
        since = last_github_tag()
    entries = parse_changelog()
    selected, all_versions = select_entries(entries, since)

    if since and since not in all_versions:
        print(f"WARNING: baseline {since} not found in changelog — including all entries")
    print(f"v{version}: baseline = {since or '(none)'}, "
          f"{len(selected)} changelog version(s): {', '.join(v for v, _ in selected) or '—'}")

    body = build_body(version, version19, selected)
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

    print("\nBuilding Claude Desktop extension (.mcpb)…")
    mcpb = build_mcpb(version)
    if mcpb:
        zips.append(mcpb)
    elif "--no-mcpb" in args:
        print("  WARNING: .mcpb build failed — releasing without it (--no-mcpb given)")
    else:
        print("ERROR: .mcpb build failed — the Connect dialog points users at the latest "
              "release, so releases must carry it. Fix the build, or pass --no-mcpb to "
              "release without it.")
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
