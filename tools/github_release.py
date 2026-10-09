"""Cut a GitHub release for Supervertaler for Trados (release notes + MCP assets).

SINGLE-CHANNEL DISTRIBUTION (from 2026-08-02): the plugin binary is published
ONLY through the RWS App Store. GitHub releases carry the notes, the tag, and the
MCP server assets — but NOT the .sdlplugin.

Why this changed:
  * App Store builds are RWS-signed; GitHub builds were not, so every Studio start
    nagged the user with an "unsigned plug-in" dialog they reasonably read as an error.
  * The in-plugin update check queries the App Store catalogue, so anyone who had
    installed from GitHub was running a build NEWER than the catalogue and was
    therefore never told about updates again — silently stuck, sometimes for months.
  * The archive accumulated risk: 67 releases predating the v4.20.34 trial anchor were
    still downloadable, and any of them could be used to reset the 14-day trial
    indefinitely. Those assets were deleted on 2026-08-02.

For a user who needs a fix before it clears App Store review, share the build directly
(e.g. a Google Drive link with an expiry) rather than publishing it. Keep that folder
empty between hand-offs — a standing public link recreates exactly the problem above.

The MCP assets MUST stay attached: the plugin's "Connect AI assistant" dialog points
users at /releases/latest for the .mcpb.

Changelog baseline: the *previous GitHub release tag* (auto-detected via `gh`), NOT the
last App-Store-published version. A GitHub reader's "last seen" is the last GitHub
release; the two channels deliberately use different baselines.

Usage:
    python tools/github_release.py              # write release-body-v<ver>.md, print, no mutations
    python tools/github_release.py --zip-only   # just (re)build the zips in dist/ (build.sh uses this)
    python tools/github_release.py --create      # body + `gh release create` with the MCP assets attached
                                                 # (the plugin zips are built into dist/ but NOT attached)
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
MANIFEST_17 = os.path.join(SRC_DIR, "pluginpackage.manifest.17.xml")
DIST_DIR = os.path.join(BASE_DIR, "dist")

# Claude Desktop extension (built by tools/build_mcpb.py). Attached to every
# release: the plugin's "Connect AI assistant" dialog links users to
# /releases/latest to download exactly this file.
MCPB_NAME = "Supervertaler-MCP-Server.mcpb"
# Plain exe zip (built by the same script) for MCP apps without an
# extension format - ChatGPT desktop and friends.
MCPB_EXE_ZIP = "Supervertaler-MCP-Server-exe.zip"

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
    ("Supervertaler for Trados (Studio 2022).sdlplugin",
     "Supervertaler-for-Trados-Studio-2022.zip",
     "Trados Studio 2022"),
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


def read_current_version_17():
    """The Studio 2022 (major 17) number, or None where there is no 2022 build."""
    return _read_manifest_three(MANIFEST_17) if os.path.exists(MANIFEST_17) else None


def last_github_tag(exclude=None):
    """The most recent GitHub release tag, e.g. 'v4.20.44' -> '4.20.44'. None if no releases.

    `exclude` (the version being released) is skipped, so re-running this AFTER
    the release exists still reports the release *before* it. Without that, the
    tag just published becomes its own baseline, the delta comes out empty, and
    the body file is silently overwritten with a changelog-less release note.
    """
    try:
        out = subprocess.run(
            ["gh", "release", "list", "--limit", "10", "--json", "tagName", "-q", ".[].tagName"],
            cwd=BASE_DIR, capture_output=True, text=True, check=True,
        ).stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    for line in out.splitlines():
        tag = line.strip().lstrip("v")
        if tag and tag != (exclude or "").lstrip("v"):
            return tag
    return None


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


def build_body(version, version19, selected, version17=None):
    span = (f"{selected[-1][0]} → {selected[0][0]}"
            if len(selected) > 1 else selected[0][0]) if selected else version
    also_2022 = f" / **v{version17}** (Studio 2022)" if version17 else ""

    table = f"| `{MCPB_NAME}` | AI assistant extension for Claude Desktop (optional, see below) |"
    table += f"\n| `{MCPB_EXE_ZIP}` | AI assistant server for other local MCP clients, e.g. Claude Code (optional, see below) |"
    changelog = "\n\n".join(content for _v, content in selected) if selected else "_See CHANGELOG.md._"

    return f"""> [!WARNING]
> **Did the update prompt inside Trados send you here to download?**
> Then your plugin predates **v4.19.24** and is still checking GitHub for updates.
> There is no plugin to download on this page - install once from the
> **[RWS App Store](https://appstore.rws.com/plugin/432)** (or *Add-Ins -> RWS App Store*
> inside Studio) and it will check there from then on, and stop warning you about an
> unsigned plug-in at every start.

Supervertaler for Trados **v{version}** (Studio 2024) / **v{version19}** (Studio 2026){also_2022}. Covers {span}.

## 📦 How to install

**Supervertaler for Trados is published through the [RWS App Store](https://appstore.rws.com/plugin/432).** Install it from there, or from inside Studio via **Add-Ins → RWS App Store**. Those builds are signed by RWS, so Studio loads them without an "unsigned plug-in" prompt at every start, and the plugin's own update check keeps you current.

The plugin binary is **not attached to GitHub releases** – this page is the changelog and the record of what changed in each build. App Store updates go through RWS review, so a brand-new fix can take a day or two to appear; if you are waiting on something specific, email support@supervertaler.com and I will send you the build directly.

| Also attached | What it is |
|---|---|
{table}

## 🤖 Supervertaler MCP Server (optional)

`{MCPB_NAME}` connects **Claude Desktop directly to your live Trados Studio session** – ask about the open project, search your TMs and termbases, run QA checks, have translations drafted into the document, all from Claude's own chat window. To install: download the file, then in Claude Desktop open **Settings → Extensions → Advanced settings** and click **Install extension…** (double-click on the file also works if your system associates `.mcpb` with Claude; drag-and-drop does not). Requires Supervertaler for Trados (this plugin) and works entirely on your own machine. Other MCP clients that run local servers (e.g. **Claude Code**) can use `{MCPB_EXE_ZIP}` instead: unzip it somewhere permanent and point the client's MCP config at the exe – the plugin's **Settings → AI Settings → Connect AI assistant…** dialog copies a ready-made snippet. **ChatGPT desktop works too** – unzip the exe somewhere permanent and register it as a STDIO server in your Codex config; the [setup guide](https://docs.supervertaler.com/trados/mcp-server/#setting-it-up) gives the exact file and the block to paste. What cannot work is a client that runs the server in the cloud, such as the claude.ai or chatgpt.com websites – the bridge is local to your machine by design. [Documentation](https://docs.supervertaler.com/trados/mcp-server/).

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
    users can correlate it with the plugin build it shipped with.

    The exe zip is required too, not optional. Supervertaler for memoQ's
    installer and its ChatGPT setup download SupervertalerMcpServer.exe from
    THIS repo's releases/latest (memoQ 0.1.0, 2026-09-28), as does the
    plugin's own ChatGPT setup. A Trados release without the zip becomes
    "latest" and breaks every new memoQ install, with nothing on the Trados
    side to show it. Returns None unless both files were built."""
    tail = version.split(".", 1)[1] if "." in version else version
    result = subprocess.run(
        [sys.executable, os.path.join(BASE_DIR, "tools", "build_mcpb.py"),
         "--version", f"1.{tail}"],
        cwd=BASE_DIR)
    mcpb_path = os.path.join(DIST_DIR, MCPB_NAME)
    exe_zip = os.path.join(DIST_DIR, MCPB_EXE_ZIP)
    if result.returncode != 0 or not os.path.exists(mcpb_path) or not os.path.exists(exe_zip):
        return None
    return [mcpb_path, exe_zip]


def make_zips():
    """(Re)create the release zips in dist/, one per build found there. Returns the list of zip paths."""
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
        since = last_github_tag(exclude=version)
    entries = parse_changelog()
    selected, all_versions = select_entries(entries, since)

    if since and since not in all_versions:
        print(f"WARNING: baseline {since} not found in changelog — including all entries")
    if not selected:
        print(f"ERROR: no changelog entries after baseline {since or '(none)'} — refusing to "
              f"write an empty release body over release-body-v{version}.md.\n"
              f"       If you are regenerating notes for an already-published release, pass "
              f"--since <the version before {version}>.")
        sys.exit(1)
    print(f"v{version}: baseline = {since or '(none)'}, "
          f"{len(selected)} changelog version(s): {', '.join(v for v, _ in selected) or '—'}")

    body = build_body(version, version19, selected, read_current_version_17())
    body_file = os.path.join(BASE_DIR, f"release-body-v{version}.md")
    with open(body_file, "w", encoding="utf-8") as f:
        f.write(body)
    print(f"  Release body written to: {body_file}")

    if not create:
        print("\n(dry run — pass --create to run `gh release create`)")
        return

    # The plugin zips are still BUILT (build.sh --zip-only relies on it, and they
    # are what gets shared by hand for a pre-review fix) but they are NOT
    # attached: the App Store is the only publication channel. See the module
    # docstring for why.
    print("\nBuilding zips… (local only — not attached to the release)")
    make_zips()

    # Named for what it is. This list has carried only the MCP assets since
    # 18.20.156 stopped attaching the plugin - see the note above - and calling
    # it "zips" kept that stale meaning alive in the messages below.
    assets = []
    print("\nBuilding Claude Desktop extension (.mcpb)…")
    mcpb = build_mcpb(version)
    if mcpb:
        assets.extend(mcpb)
    elif "--no-mcpb" in args:
        print("  WARNING: MCP asset build failed — releasing without them (--no-mcpb given). "
              "This release becomes 'latest': the Connect dialog, both ChatGPT setups and the "
              "memoQ installer will find no MCP server until a release that has them.")
    else:
        print(f"ERROR: MCP asset build failed ({MCPB_NAME} and {MCPB_EXE_ZIP} are both required) — "
              "the Connect dialog, the ChatGPT setups of both plugins and the memoQ installer "
              "fetch them from the latest release, so releases must carry them. Fix the build, "
              "or pass --no-mcpb to release without them.")
        sys.exit(1)

    tag = f"v{version}"
    cmd = ["gh", "release", "create", tag,
           "--title", tag,
           "--notes-file", body_file,
           *assets]
    print(f"\nRunning: gh release create {tag} (+{len(assets)} assets)")
    result = subprocess.run(cmd, cwd=BASE_DIR)
    if result.returncode != 0:
        print("ERROR: gh release create failed")
        sys.exit(result.returncode)
    # Say what was actually attached. This said "plugin zip(s)", which is the
    # one thing a release must NOT carry: the plugin ships only via the RWS
    # App Store, and a message claiming otherwise invites someone to go
    # looking for a binary that is deliberately absent.
    print(f"\n✓ Released {tag} with {len(assets)} MCP asset(s) attached "
          f"(no plugin binary – it ships via the RWS App Store).")


if __name__ == "__main__":
    main()
