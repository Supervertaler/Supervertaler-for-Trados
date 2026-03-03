#!/bin/bash
# Build and package Termview for Trados Studio
# Produces: dist/Termview.sdlplugin
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/Termview"
DIST_DIR="$SCRIPT_DIR/dist"
BUILD_DIR="$PROJECT_DIR/bin/Release"
DOTNET="${HOME}/.dotnet/dotnet"

echo "=== Building Termview ==="
"$DOTNET" build "$PROJECT_DIR/Termview.csproj" -c Release

echo ""
echo "=== Packaging Termview.sdlplugin ==="
mkdir -p "$DIST_DIR"
rm -f "$DIST_DIR/Termview.sdlplugin"

# Create a staging directory
STAGING="$DIST_DIR/_staging"
rm -rf "$STAGING"
mkdir -p "$STAGING"

# Copy required files
cp "$BUILD_DIR/Termview.dll"                "$STAGING/"
cp "$BUILD_DIR/Termview.plugin.xml"         "$STAGING/"
cp "$BUILD_DIR/Termview.plugin.resources"   "$STAGING/"
cp "$BUILD_DIR/pluginpackage.manifest.xml"  "$STAGING/"
cp "$BUILD_DIR/System.Data.SQLite.dll"      "$STAGING/"

# Copy SQLite native libraries (x86/x64)
if [ -d "$BUILD_DIR/x64" ]; then
    mkdir -p "$STAGING/x64"
    cp "$BUILD_DIR/x64/"*.dll "$STAGING/x64/" 2>/dev/null || true
fi
if [ -d "$BUILD_DIR/x86" ]; then
    mkdir -p "$STAGING/x86"
    cp "$BUILD_DIR/x86/"*.dll "$STAGING/x86/" 2>/dev/null || true
fi

# Create ZIP (sdlplugin is just a ZIP) — use PowerShell since zip isn't available in Git Bash
STAGING_WIN=$(cygpath -w "$STAGING")
DIST_ZIP=$(cygpath -w "$DIST_DIR/Termview.zip")
powershell.exe -NoProfile -Command "Compress-Archive -Path '$STAGING_WIN\\*' -DestinationPath '$DIST_ZIP' -Force"
mv "$DIST_DIR/Termview.zip" "$DIST_DIR/Termview.sdlplugin"

# Clean up
rm -rf "$STAGING"

echo ""
echo "=== Done ==="
echo "Package: $DIST_DIR/Termview.sdlplugin"
echo ""
echo "To install manually:"
echo "  Copy Termview.sdlplugin to:"
echo "  %LOCALAPPDATA%\\Trados\\Trados Studio\\18\\Plugins\\Packages\\"
echo "  Then restart Trados Studio."
