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
# deploy there to match where Studio 2026 actually looks. (The 2026 *Beta* used
# a "19beta" key; that build is uninstalled. The "stale plugin" cleanups below
# also clear any leftover Local\19 / Local\19beta copies from earlier attempts.)
PACKAGES_DIR_19="$APPDATA/Trados/Trados Studio/19/Plugins/Packages"
# Studio extracts to Unpacked/<sdlplugin-filename-without-extension>/, NOT to
# Unpacked/<PlugInName>/. So for "Supervertaler for Trados (Studio 2026).sdlplugin"
# the extracted folder is "Supervertaler for Trados (Studio 2026)" (with suffix).
# Targeting the wrong name leaves the old DLL in place; Studio re-loads it
# without re-extracting from the new .sdlplugin and we keep seeing stale crashes.
UNPACKED_DIR_19="$APPDATA/Trados/Trados Studio/19/Plugins/Unpacked/Supervertaler for Trados (Studio 2026)"
STALE_LOCAL_19_DIR="$LOCALAPPDATA/Trados/Trados Studio/19/Plugins/Packages"
STALE_LOCAL_19BETA_DIR="$LOCALAPPDATA/Trados/Trados Studio/19beta/Plugins/Packages"
STALE_LOCAL_19_UNPACKED="$LOCALAPPDATA/Trados/Trados Studio/19/Plugins/Unpacked/Supervertaler for Trados (Studio 2026)"
STALE_LOCAL_19BETA_UNPACKED="$LOCALAPPDATA/Trados/Trados Studio/19beta/Plugins/Unpacked/Supervertaler for Trados (Studio 2026)"

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

# Abort if Trados Studio is running — it locks plugin files and prevents
# a clean extraction on next start, leaving the plugin in a broken state.
if tasklist.exe 2>/dev/null | grep -qi "SDLTradosStudio\|TradosStudio"; then
    echo ""
    echo "  ERROR: Trados Studio is currently running."
    echo "  Close Trados Studio completely, then run this script again."
    exit 1
fi

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

    # Mirror the .sdlplugin into RWS AppStore/ so the AppStore Manager upload step
    # has both the plugin and that version's release notes in one folder.
    APPSTORE_DIR="$SCRIPT_DIR/RWS AppStore"
    mkdir -p "$APPSTORE_DIR"
    cp "$DIST_DIR/$PLUGIN_FILENAME_18" "$APPSTORE_DIR/$PLUGIN_FILENAME_18"
    echo "  Mirrored to: $APPSTORE_DIR/$PLUGIN_FILENAME_18"
    echo ""

    echo "=== [Studio18] Deploying to Trados Studio 2024 ==="

    # Wipe the Unpacked folder so Trados re-extracts cleanly on next start.
    if [ -d "$UNPACKED_DIR_18" ]; then
        echo "  Removing stale Unpacked/Supervertaler for Trados..."
        rm -rf "$UNPACKED_DIR_18"
    fi
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

    mkdir -p "$PACKAGES_DIR_18"
    cp "$DIST_DIR/$PLUGIN_FILENAME_18" "$PACKAGES_DIR_18/$PLUGIN_FILENAME_18"
    echo "  Installed: $PACKAGES_DIR_18/$PLUGIN_FILENAME_18"
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

    APPSTORE_DIR="$SCRIPT_DIR/RWS AppStore"
    mkdir -p "$APPSTORE_DIR"
    cp "$DIST_DIR/$PLUGIN_FILENAME_19" "$APPSTORE_DIR/$PLUGIN_FILENAME_19"
    echo "  Mirrored to: $APPSTORE_DIR/$PLUGIN_FILENAME_19"
    echo ""

    echo "=== [Studio19] Deploying to Trados Studio 2026 ==="

    # Clean any stale Supervertaler copies in the wrong 2026-era folders. Earlier
    # versions of build.sh deployed to %LocalAppData%\...\19\ (no "beta" suffix),
    # which Studio 2026 doesn't read; left there those files just confuse later
    # diagnoses.
    for STALE_DIR in "$STALE_LOCAL_19_DIR" "$STALE_LOCAL_19BETA_DIR"; do
        for STALE_FILE in "$STALE_DIR"/Supervertaler*.sdlplugin; do
            [ -e "$STALE_FILE" ] || continue
            echo "  Removing stale: $STALE_FILE"
            rm -f "$STALE_FILE"
        done
    done
    for STALE_UNPACKED in "$STALE_LOCAL_19_UNPACKED" "$STALE_LOCAL_19BETA_UNPACKED"; do
        if [ -d "$STALE_UNPACKED" ]; then
            echo "  Removing stale Unpacked: $STALE_UNPACKED"
            rm -rf "$STALE_UNPACKED"
        fi
    done

    # Wipe the live Unpacked folder so Studio 2026 re-extracts cleanly on next start.
    if [ -d "$UNPACKED_DIR_19" ]; then
        echo "  Removing stale Unpacked/Supervertaler for Trados in Roaming\\19beta..."
        rm -rf "$UNPACKED_DIR_19"
    fi

    mkdir -p "$PACKAGES_DIR_19"
    cp "$DIST_DIR/$PLUGIN_FILENAME_19" "$PACKAGES_DIR_19/$PLUGIN_FILENAME_19"
    echo "  Installed: $PACKAGES_DIR_19/$PLUGIN_FILENAME_19"
    echo ""
else
    echo "  [Studio19] Trados Studio 2026 Beta not installed at $STUDIO19_INSTALL — skipping 19 build."
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

echo "=== Done — start Trados Studio to load the updated plugin(s) ==="
