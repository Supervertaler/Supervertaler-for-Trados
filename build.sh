#!/bin/bash
# Build, package, and deploy Supervertaler for Trados.
# Produces TWO .sdlplugin artefacts from one source tree:
#   - Studio 2024 (Studio18): x86, .sdltb via JET OleDb
#   - Studio 2026 (Studio19): x64, .ttb via SQLite
# The Studio 19 build is skipped if Studio19Beta is not installed on this machine.
# Trados Studio must be CLOSED before running this script.
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/Supervertaler.Trados"
DIST_DIR="$SCRIPT_DIR/dist"
DOTNET="${HOME}/.dotnet/dotnet"

STUDIO18_INSTALL="/c/Program Files (x86)/Trados/Trados Studio/Studio18"
STUDIO19_INSTALL="/c/Program Files/Trados/Trados Studio/Studio19"

BUILD_DIR_18="$PROJECT_DIR/bin/Studio18/Release"
BUILD_DIR_19="$PROJECT_DIR/bin/Studio19/Release"

PACKAGES_DIR_18="$LOCALAPPDATA/Trados/Trados Studio/18/Plugins/Packages"
UNPACKED_DIR_18="$LOCALAPPDATA/Trados/Trados Studio/18/Plugins/Unpacked/Supervertaler for Trados"
# Studio 2026 (release) uses the "19" version key and ships its bundled plugins
# (AI Bridge, LanguageWeaver Provider) in Roaming\19\Plugins\Packages, so we
# deploy there to match where Studio 2026 actually looks. (The "stale plugin"
# cleanups below also clear any leftover Local\19 copies from earlier
# attempts, which is where an install made with "This computer for me only"
# would have landed.)
PACKAGES_DIR_19="$APPDATA/Trados/Trados Studio/19/Plugins/Packages"
# Studio extracts to Unpacked/<sdlplugin-filename-without-extension>/, NOT to
# Unpacked/<PlugInName>/. So for "Supervertaler for Trados (Studio 2026).sdlplugin"
# the extracted folder is "Supervertaler for Trados (Studio 2026)" (with suffix).
# Targeting the wrong name leaves the old DLL in place; Studio re-loads it
# without re-extracting from the new .sdlplugin and we keep seeing stale crashes.
UNPACKED_DIR_19="$APPDATA/Trados/Trados Studio/19/Plugins/Unpacked/Supervertaler for Trados (Studio 2026)"
STALE_LOCAL_19_DIR="$LOCALAPPDATA/Trados/Trados Studio/19/Plugins/Packages"
# The Unpacked ROOT, not one folder inside it: the same build unpacks under
# different names depending on where it was installed from (see CLAUDE.md on
# the App Store stripping the "(Studio 2026)" suffix), so this is swept by
# prefix rather than by a literal name.
STALE_LOCAL_19_UNPACKED_ROOT="$LOCALAPPDATA/Trados/Trados Studio/19/Plugins/Unpacked"

OLD_UNPACKED_DIR_18="$LOCALAPPDATA/Trados/Trados Studio/18/Plugins/Unpacked/TermLens"
# build.sh used to deploy to Roaming; switched to Local in v4.19.25 to match
# the install scope end-users get from "This computer for me only" in the
# Trados Plugin Installer.
OLD_ROAMING_PACKAGES_18="$APPDATA/Trados/Trados Studio/18/Plugins/Packages"
OLD_ROAMING_UNPACKED_18="$APPDATA/Trados/Trados Studio/18/Plugins/Unpacked/Supervertaler for Trados"

PLUGIN_FILENAME_18="Supervertaler for Trados.sdlplugin"
PLUGIN_FILENAME_19="Supervertaler for Trados (Studio 2026).sdlplugin"

# Verify all version files share one MINOR.PATCH tail before building.
# "Option 3" scheme: each file's MAJOR is the Studio major it targets — the
# manifests / plugin.xml carry 18 (Studio 2024) or 19 (Studio 2026), and the
# .csproj keeps $(TradosStudioVersion) so its major resolves per build. Only the
# tail is shared and hand-bumped, so that is what we cross-check here (plus a
# guard that the two manifests carry different majors — a shared major was the
# exact App Store collision this scheme prevents).
tail_of() { echo "$1" | sed -n 's/^[0-9][0-9]*\.\([0-9][0-9]*\.[0-9][0-9]*\)\.[0-9][0-9]*$/\1/p'; }
major_of() { echo "$1" | cut -d. -f1; }

CSPROJ_TAIL=$(sed -n 's|.*<Version>\$(TradosStudioVersion)\.\([0-9][0-9.]*\)</Version>.*|\1|p' "$PROJECT_DIR/Supervertaler.Trados.csproj" | head -1)
MANIFEST_VER=$(sed -n 's/.*<Version>\([0-9.]*\)<\/Version>.*/\1/p' "$PROJECT_DIR/pluginpackage.manifest.xml")
MANIFEST_VER_19=$(sed -n 's/.*<Version>\([0-9.]*\)<\/Version>.*/\1/p' "$PROJECT_DIR/pluginpackage.manifest.19.xml")
PLUGIN_VER=$(python "$SCRIPT_DIR/tools/read_plugin_version.py" "$PROJECT_DIR/Supervertaler.Trados.plugin.xml" 2>/dev/null || echo "?")

MANIFEST_TAIL=$(tail_of "$MANIFEST_VER")
MANIFEST_TAIL_19=$(tail_of "$MANIFEST_VER_19")
PLUGIN_TAIL=$(tail_of "$PLUGIN_VER")

if [ -z "$CSPROJ_TAIL" ] \
   || [ "$CSPROJ_TAIL" != "$MANIFEST_TAIL" ] \
   || [ "$CSPROJ_TAIL" != "$MANIFEST_TAIL_19" ] \
   || [ "$CSPROJ_TAIL" != "$PLUGIN_TAIL" ] \
   || [ "$(major_of "$MANIFEST_VER")" = "$(major_of "$MANIFEST_VER_19")" ]; then
    echo ""
    echo "  ERROR: Version mismatch detected!"
    echo "    .csproj tail: ${CSPROJ_TAIL:-<none>}"
    echo "    manifest 18:  $MANIFEST_VER  (tail ${MANIFEST_TAIL:-<none>})"
    echo "    manifest 19:  $MANIFEST_VER_19  (tail ${MANIFEST_TAIL_19:-<none>})"
    echo "    plugin.xml:   $PLUGIN_VER  (tail ${PLUGIN_TAIL:-<none>})"
    echo ""
    echo "  All four must share one MINOR.PATCH tail, and the two manifests must"
    echo "  carry different majors (18 vs 19)."
    echo "  Run: python bump_version.py ${CSPROJ_TAIL:-<minor>.<patch>}"
    echo ""
    exit 1
fi
echo "  Version check passed: Studio 2024 $MANIFEST_VER / Studio 2026 $MANIFEST_VER_19"
echo ""

# Guard the help docs' MCP tool table (Supervertaler-Help repo,
# trados/mcp-server.md) against drift from the shipped tool set. The table's
# prose stays hand-written; only the set of tool names is enforced.
python "$SCRIPT_DIR/tools/check_mcp_docs.py" || exit 1
echo ""

# Compile the shared core/ sources ON THEIR OWN before either plugin build.
#
# core/ is compiled into this assembly, not referenced as a DLL, and
# GlobalUsings.cs puts Supervertaler.Core and .Models in scope for every file -
# core's own files included. So a core file that omits a using builds green
# here and fails in Supervertaler for memoQ, which compiles the same sources
# with no such declaration. GlossaryRepair.cs did exactly that for weeks and
# broke the memoQ build the first time it was pulled (2026-09-16).
#
# Supervertaler.Core.Build.csproj compiles core with no host's global usings in
# scope - the condition the other plugin builds in. About three seconds, and it
# names the file and line rather than surfacing as someone else's broken pull.
# The memoQ repo's build.sh runs the same check; a break in core now cannot
# leave both repos green.
echo "=== Compiling core/ on its own (no host global usings) ==="
if ! CORE_BUILD_OUT=$(dotnet build "$SCRIPT_DIR/core/build/Supervertaler.Core.Build.csproj" -v q --nologo 2>&1); then
    echo "$CORE_BUILD_OUT" | grep -E "error" | sort -u
    echo ""
    echo "  ERROR: core/ does not compile on its own."
    echo "  It compiles inside this plugin only because GlobalUsings.cs is in"
    echo "  scope here; Supervertaler for memoQ has no such file. Add the"
    echo "  missing using to the core file named above."
    echo ""
    exit 1
fi
echo "  core/ compiles standalone"
echo ""

# A running Studio must not be deployed over: wiping its Unpacked folder pulls
# the DLLs out from under a loaded plugin. But the two Studios live in entirely
# separate trees, so a running 2026 is no reason to refuse 2024 - and a blanket
# abort costs a working translator their session every time the other build is
# touched.
#
# Which Studio is running cannot be read off the process list: Get-Process
# reports SDLTradosStudio with an empty Path. So each version is PROBED instead,
# and both helpers below are non-destructive by construction.
#
# That property is the point. An earlier attempt probed by opening the package
# for writing, passed, and then truncated a live 2026 install to 838 bytes when
# the copy failed halfway.
SKIPPED=""

# Can this version be deployed? Renaming each unpacked folder aside is the test:
# Windows will not rename a directory holding DLLs a process has loaded, so a
# running Studio fails here having lost nothing. Success both proves that Studio
# is closed AND performs the wipe we wanted anyway - Studio re-extracts from the
# package on next start.
#
# Sweeps EVERY Supervertaler* folder, not just the name this script installs
# under. The RWS App Store serves the 2026 build as "Supervertaler for
# Trados.sdlplugin", dropping the "(Studio 2026)" suffix used here, and Studio
# extracts to Unpacked/<filename-without-extension> - so the same build can be
# unpacked under two different names. Leaving the other one behind is how you get
# two copies of the plugin loaded at once, which crashes Studio on startup.
claim_unpacked() {
    local unpacked_root="$1"
    local label="$2"
    [ -d "$unpacked_root" ] || return 0     # nothing installed yet: free to deploy

    local d aside
    for d in "$unpacked_root"/Supervertaler*; do
        [ -e "$d" ] || continue             # glob matched nothing
        aside="$d.replacing.$$"
        if ! mv "$d" "$aside" 2>/dev/null; then
            echo ""
            echo "  SKIPPING $label: it is running (its plugin files are in use)."
            echo "  Its install is untouched. Close it and re-run to update that build."
            SKIPPED="$SKIPPED
    - $label"
            return 1
        fi
        echo "  Removed unpacked: $(basename "$d")"
        rm -rf "$aside"
    done
    return 0
}

# Remove any OTHER Supervertaler package sitting in the same folder - notably an
# App Store copy under RWS's own filename. Two packages both declaring
# PlugInName "Supervertaler for Trados" load as two plugins and crash Studio.
sweep_other_packages() {
    local pkg_dir="$1"
    local keep="$2"
    [ -d "$pkg_dir" ] || return 0
    local f
    for f in "$pkg_dir"/Supervertaler*.sdlplugin; do
        [ -e "$f" ] || continue
        [ "$(basename "$f")" = "$keep" ] && continue
        echo "  Removing other Supervertaler package: $(basename "$f")"
        rm -f "$f"
    done
}

# Install without ever writing over the live package: copy to a temp name in the
# same folder, then rename into place. A rename either replaces the file whole or
# fails leaving the original intact - a half-written package is not a state this
# can reach. The hash check is belt-and-braces for the failure we already had.
install_package() {
    local src="$1"
    local dst="$2"
    local tmp="$dst.incoming.$$"
    mkdir -p "$(dirname "$dst")"
    rm -f "$tmp"
    if ! cp "$src" "$tmp" || ! mv -f "$tmp" "$dst"; then
        rm -f "$tmp"
        echo "  ERROR: could not install $dst - left as it was."
        return 1
    fi
    if [ "$(sha256sum < "$src" | cut -d' ' -f1)" != "$(sha256sum < "$dst" | cut -d' ' -f1)" ]; then
        echo "  ERROR: $dst does not match the build it came from."
        return 1
    fi
    return 0
}

# ============================================================================
#  Studio 18 build (Trados Studio 2024)
# ============================================================================
if [ -d "$STUDIO18_INSTALL" ]; then
    echo "=== [Studio18] Building Supervertaler for Trados 2024 ==="
    "$DOTNET" build "$PROJECT_DIR/Supervertaler.Trados.csproj" -c Release -p:TradosStudioVersion=18

    # Ensure ARM64 native SQLite binary is in the build output.
    # NuGet restore downloads it but MSBuild only copies x64/x86/arm to the output.
    # Needed for Windows on ARM (Parallels on Apple Silicon, Surface Pro X, etc.).
    ARM64_SRC="$USERPROFILE/.nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.6/runtimes/win-arm64/native/e_sqlite3.dll"
    ARM64_DST_18="$BUILD_DIR_18/runtimes/win-arm64/native"
    if [ -f "$ARM64_SRC" ] && [ ! -f "$ARM64_DST_18/e_sqlite3.dll" ]; then
        echo "  Copying win-arm64 native e_sqlite3.dll..."
        mkdir -p "$ARM64_DST_18"
        cp "$ARM64_SRC" "$ARM64_DST_18/e_sqlite3.dll"
    fi

    echo ""
    echo "=== [Studio18] Packaging $PLUGIN_FILENAME_18 (OPC format) ==="
    mkdir -p "$DIST_DIR"
    rm -f "$DIST_DIR/$PLUGIN_FILENAME_18"
    python "$SCRIPT_DIR/package_plugin.py" "$BUILD_DIR_18" "$DIST_DIR/$PLUGIN_FILENAME_18"

    # NOT mirrored into "RWS AppStore/" here, deliberately. Staging happens in
    # tools/appstore_release.py, which computes the notes' checksums from these
    # same files in the same run - so the staged binary and the checksum
    # describing it can never disagree. Copying on every build is what let a
    # staged submission drift twice in one day: the notes stayed put while the
    # binaries beside them were replaced by ordinary development.

    echo "=== [Studio18] Deploying to Trados Studio 2024 ==="

    # Claims (and thereby wipes) the Unpacked folder so Trados re-extracts
    # cleanly on next start - or leaves this whole block unrun, changing nothing.
    if claim_unpacked "$(dirname "$UNPACKED_DIR_18")" "Trados Studio 2024"; then
    if [ -d "$OLD_UNPACKED_DIR_18" ]; then
        echo "  Removing old Unpacked/TermLens..."
        rm -rf "$OLD_UNPACKED_DIR_18"
    fi

    # Clean up old Roaming install location (build.sh used to deploy here before
    # switching to Local in v4.19.25).
    if [ -f "$OLD_ROAMING_PACKAGES_18/$PLUGIN_FILENAME_18" ]; then
        echo "  Removing old Roaming Packages/$PLUGIN_FILENAME_18..."
        rm -f "$OLD_ROAMING_PACKAGES_18/$PLUGIN_FILENAME_18"
    fi
    if [ -d "$OLD_ROAMING_UNPACKED_18" ]; then
        echo "  Removing old Roaming Unpacked/Supervertaler for Trados..."
        rm -rf "$OLD_ROAMING_UNPACKED_18"
    fi

    # Remove obsolete package names that may still be in Packages.
    for OLD_PKG in "TermLens.sdlplugin" "Supervertaler.Trados.sdlplugin"; do
        if [ -f "$PACKAGES_DIR_18/$OLD_PKG" ]; then
            echo "  Removing old $OLD_PKG..."
            rm -f "$PACKAGES_DIR_18/$OLD_PKG"
        fi
    done
    OLD_DOTTED_UNPACKED_18="$APPDATA/Trados/Trados Studio/18/Plugins/Unpacked/Supervertaler.Trados"
    if [ -d "$OLD_DOTTED_UNPACKED_18" ]; then
        echo "  Removing old Unpacked/Supervertaler.Trados..."
        rm -rf "$OLD_DOTTED_UNPACKED_18"
    fi

    sweep_other_packages "$PACKAGES_DIR_18" "$PLUGIN_FILENAME_18"
    install_package "$DIST_DIR/$PLUGIN_FILENAME_18" "$PACKAGES_DIR_18/$PLUGIN_FILENAME_18" \
        && echo "  Installed: $PACKAGES_DIR_18/$PLUGIN_FILENAME_18"
    fi
    echo ""
else
    echo "  [Studio18] Trados Studio 2024 not installed at $STUDIO18_INSTALL — skipping 18 build."
    echo ""
fi

# ============================================================================
#  Studio 19 build (Trados Studio 2026)
# ============================================================================
if [ -d "$STUDIO19_INSTALL" ]; then
    echo "=== [Studio19] Building Supervertaler for Trados 2026 ==="
    "$DOTNET" build "$PROJECT_DIR/Supervertaler.Trados.csproj" -c Release -p:TradosStudioVersion=19

    ARM64_DST_19="$BUILD_DIR_19/runtimes/win-arm64/native"
    if [ -f "$ARM64_SRC" ] && [ ! -f "$ARM64_DST_19/e_sqlite3.dll" ]; then
        echo "  Copying win-arm64 native e_sqlite3.dll..."
        mkdir -p "$ARM64_DST_19"
        cp "$ARM64_SRC" "$ARM64_DST_19/e_sqlite3.dll"
    fi

    echo ""
    echo "=== [Studio19] Packaging $PLUGIN_FILENAME_19 (OPC format) ==="
    mkdir -p "$DIST_DIR"
    rm -f "$DIST_DIR/$PLUGIN_FILENAME_19"
    python "$SCRIPT_DIR/package_plugin.py" "$BUILD_DIR_19" "$DIST_DIR/$PLUGIN_FILENAME_19"

    # Staged by tools/appstore_release.py, not here - see the note above.

    echo "=== [Studio19] Deploying to Trados Studio 2026 ==="

    # Clean any stale Supervertaler copies in the wrong 2026-era folders. Earlier
    # versions of build.sh deployed to %LocalAppData%\...\19\ (no "beta" suffix),
    # which Studio 2026 doesn't read; left there those files just confuse later
    # diagnoses.
    for STALE_DIR in "$STALE_LOCAL_19_DIR"; do
        for STALE_FILE in "$STALE_DIR"/Supervertaler*.sdlplugin; do
            [ -e "$STALE_FILE" ] || continue
            echo "  Removing stale: $STALE_FILE"
            rm -f "$STALE_FILE"
        done
    done
    for STALE_UNPACKED in "$STALE_LOCAL_19_UNPACKED_ROOT"/Supervertaler*; do
        [ -e "$STALE_UNPACKED" ] || continue      # glob matched nothing
        echo "  Removing stale Unpacked: $STALE_UNPACKED"
        rm -rf "$STALE_UNPACKED"
    done

    # Claims (and thereby wipes) the live Unpacked folder so Studio 2026
    # re-extracts cleanly on next start - or bails out having changed nothing.
    if claim_unpacked "$(dirname "$UNPACKED_DIR_19")" "Trados Studio 2026"; then
        sweep_other_packages "$PACKAGES_DIR_19" "$PLUGIN_FILENAME_19"
        install_package "$DIST_DIR/$PLUGIN_FILENAME_19" "$PACKAGES_DIR_19/$PLUGIN_FILENAME_19" \
            && echo "  Installed: $PACKAGES_DIR_19/$PLUGIN_FILENAME_19"
    fi
    echo ""
else
    echo "  [Studio19] Trados Studio 2026 not installed at $STUDIO19_INSTALL — skipping 19 build."
    echo "  Install Studio 2026 to ${STUDIO19_INSTALL/\/c\//C:\\} to enable the 19 build."
    echo ""
fi

# Produce the GitHub-release zips (hyphenated outer name, exact .sdlplugin name inside).
# These are GitHub-only artefacts; the RWS App Store still gets the raw .sdlplugin.
# See tools/github_release.py for why the plugins must be zipped rather than attached bare.
if command -v python >/dev/null 2>&1; then
    echo ""
    echo "=== Producing GitHub-release zips in dist/ ==="
    python "$SCRIPT_DIR/tools/github_release.py" --zip-only
    echo ""
fi

if [ -n "$SKIPPED" ]; then
    echo ""
    echo "=== Built, but NOT installed for: ==="
    echo "$SKIPPED"
    echo ""
    echo "  dist/ holds the new build for every version regardless, so the App Store"
    echo "  staging in tools/appstore_release.py stays accurate. Only the local"
    echo "  install of the version(s) above is one build behind."
fi

echo "=== Done — start Trados Studio to load the updated plugin(s) ==="
